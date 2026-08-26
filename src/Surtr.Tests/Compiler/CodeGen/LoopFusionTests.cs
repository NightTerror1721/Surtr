#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Runtime.Objects;
using System;
using System.Linq;

namespace Surtr.Tests.Compiler.CodeGen
{
    /// <summary>
    /// Covers the fused loop steps: one extended instruction where a <c>for-in</c> used to emit a
    /// guard, an element read and a step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fusion changes the *shape* of every loop it applies to - the whole step moves to the
    /// bottom, an indexed walk is entered by jumping to it with the index at -1, and the guard of a
    /// range loop runs once above the loop instead of on every iteration. So the risk is not
    /// arithmetic; it is that some way out of a loop lands in the wrong place. Most of what is
    /// below runs a loop and checks where control went: every exit, every collection kind, and the
    /// boundaries - empty, one element, the very first and very last iteration.
    /// </para>
    /// <para>
    /// The three lowerings the fusion touches - indexed, range and dictionary - open no protected
    /// region of their own, so <c>IDisposable</c> closing is not in play here; that lives on the
    /// generator and contract paths, which the fusion does not touch. What is in play is a
    /// <c>try</c> the program wrote *around* a loop, which is checked below.
    /// </para>
    /// </remarks>
    public sealed class LoopFusionTests : IDisposable
    {
        private const string Root = "D:/proj/src";

        private readonly System.Collections.Generic.List<IDisposable> _owned =
            new System.Collections.Generic.List<IDisposable>();

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
        }

        private ModuleEmitter Emit(string source)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            var compilation = SurtrCompilation.Create(project);
            _owned.Add(compilation);

            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(
                !compilation.HasErrors,
                "Binding reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var emitter = new ModuleEmitter(compilation, binder);

            Assert.True(
                emitter.TryEmit(),
                "Emission reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            return emitter;
        }

        private string Disassemble(string source)
            => SurtrBytecodeDisassembler.Disassemble(Emit(source).Modules[0]);

        private SurtrRuntime Run(string source)
        {
            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            foreach (var module in Emit(source).Modules)
                runtime.LoadModule(module);

            return runtime;
        }

        private static SurtrValue Call(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
        {
            Assert.True(runtime.TryGetModule("game.core.Test", out var module), "No test module was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"'{name}' declares no function.");
            return runtime.Invoke(overloads[0], arguments);
        }

        private static int Int(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => Call(runtime, name, arguments).AsInt;

        private static string Text(SurtrRuntime runtime, string name)
            => runtime.Resolve<SurtrString>(Call(runtime, name))!.Text;

        private static int Count(string disassembly, string mnemonic)
            => disassembly
                .Split('\n')
                .Count(line => line.Trim().Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1)
                    .FirstOrDefault()?.Split(' ')[0] == mnemonic);

        #region Each kind lowers to its own step

        [Fact]
        public void AnArrayWalkStepsWithArrForNext()
        {
            string code = Disassemble("fun run(xs: int[]): int { var t = 0; for (x in xs) { t = t + x; } return t; }");

            Assert.Equal(1, Count(code, "ArrForNext"));
            Assert.Equal(0, Count(code, "ArrLen"));
            Assert.Equal(0, Count(code, "ArrGet"));
        }

        [Fact]
        public void AStringWalkStepsWithStrForNext()
        {
            string code = Disassemble("fun run(s: string): int { var t = 0; for (c in s) { t = t + 1; } return t; }");

            Assert.Equal(1, Count(code, "StrForNext"));
            Assert.Equal(0, Count(code, "StrLen"));
            Assert.Equal(0, Count(code, "StrGet"));
        }

        [Fact]
        public void ADictionaryWalkStepsWithDictForNext()
        {
            string code = Disassemble(
                "fun run(m: {int: int}): int { var t = 0; for (e in m) { t = t + e[0] + e[1]; } return t; }");

            Assert.Equal(1, Count(code, "DictForNext"));

            // The key read, the value lookup and the pair store are all inside the step now, so
            // the key/value temporaries the written-out form needed are gone with them.
            Assert.Equal(0, Count(code, "DictGet"));
            Assert.Equal(0, Count(code, "ArrGet"));
        }

        [Fact]
        public void AnInclusiveRangeWalkStepsWithForRangeNextLE()
        {
            string code = Disassemble("fun run(n: int): int { var t = 0; for (i in 0..=n) { t = t + i; } return t; }");

            Assert.Equal(1, Count(code, "ForRangeNextLE"));
            Assert.Equal(0, Count(code, "ForRangeNextLT"));
        }

        [Fact]
        public void AnExclusiveRangeWalkStepsWithForRangeNextLT()
        {
            string code = Disassemble("fun run(n: int): int { var t = 0; for (i in 0..n) { t = t + i; } return t; }");

            Assert.Equal(1, Count(code, "ForRangeNextLT"));
            Assert.Equal(0, Count(code, "ForRangeNextLE"));
        }

        /// <summary>
        /// A loop variable that stores inline is more than one slot, and the fused step writes
        /// exactly one - so that loop keeps the written-out form, unpack and all.
        /// </summary>
        [Fact]
        public void AMultiSlotLoopVariableKeepsTheWrittenOutWalk()
        {
            string code = Disassemble(
                "value class Pair { public let a: int; public let b: int;\n"
                    + "  public constructor(a: int, b: int) { this.a = a; this.b = b; } }\n"
                    + "fun run(xs: Pair[]): int { var t = 0; for (p in xs) { t = t + p.a; } return t; }");

            Assert.Equal(0, Count(code, "ArrForNext"));
            Assert.Equal(1, Count(code, "ArrGet"));
        }

        #endregion

        #region What every loop answers

        [Fact]
        public void AnArrayWalkVisitsEveryElementInOrder()
        {
            var runtime = Run(
                "fun run(): string { let xs = [10, 20, 30]; var s = \"\"; for (x in xs) { s = s + \"${x},\"; } return s; }");

            Assert.Equal("10,20,30,", Text(runtime, "run"));
        }

        [Fact]
        public void AnEmptyArrayWalkRunsItsBodyNotAtAll()
        {
            var runtime = Run("fun run(): int { let xs: int[] = []; var n = 0; for (x in xs) { n = n + 1; } return n; }");
            Assert.Equal(0, Int(runtime, "run"));
        }

        [Fact]
        public void AOneElementArrayWalkRunsItsBodyExactlyOnce()
        {
            var runtime = Run("fun run(): int { let xs = [7]; var n = 0; for (x in xs) { n = n + x; } return n; }");
            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AStringWalkVisitsEveryCharacter()
        {
            var runtime = Run("fun run(): string { var s = \"\"; for (c in \"abc\") { s = s + c + \"-\"; } return s; }");
            Assert.Equal("a-b-c-", Text(runtime, "run"));
        }

        [Fact]
        public void ADictionaryWalkVisitsEveryPair()
        {
            var runtime = Run(
                "fun run(): int { let m: {int: int} = {1: 10, 2: 20, 3: 30};\n"
                    + "  var t = 0; for (e in m) { t = t + e[0] * 100 + e[1]; } return t; }");

            Assert.Equal((1 * 100 + 10) + (2 * 100 + 20) + (3 * 100 + 30), Int(runtime, "run"));
        }

        [Fact]
        public void AnEmptyDictionaryWalkRunsItsBodyNotAtAll()
        {
            var runtime = Run("fun run(): int { let m: {int: int} = {}; var n = 0; for (e in m) { n = n + 1; } return n; }");
            Assert.Equal(0, Int(runtime, "run"));
        }

        /// <summary>
        /// The bound is exclusive, so the last iteration is at <c>n - 1</c> - the boundary the
        /// step's own test decides and the one an off-by-one in the fusion would move.
        /// </summary>
        [Fact]
        public void AnExclusiveRangeStopsOneBelowItsBound()
        {
            var runtime = Run("fun run(): string { var s = \"\"; for (i in 0..4) { s = s + \"${i}\"; } return s; }");
            Assert.Equal("0123", Text(runtime, "run"));
        }

        [Fact]
        public void AnInclusiveRangeStopsAtItsBound()
        {
            var runtime = Run("fun run(): string { var s = \"\"; for (i in 0..=4) { s = s + \"${i}\"; } return s; }");
            Assert.Equal("01234", Text(runtime, "run"));
        }

        /// <summary>
        /// An empty range must not run its body once. The guard above the loop is what decides
        /// that, and it is the half of the range lowering the fusion did *not* move.
        /// </summary>
        [Fact]
        public void AnEmptyRangeRunsItsBodyNotAtAll()
        {
            var runtime = Run("fun run(): int { var n = 0; for (i in 5..5) { n = n + 1; } return n; }");
            Assert.Equal(0, Int(runtime, "run"));
        }

        [Fact]
        public void AnInclusiveRangeOfOneRunsItsBodyExactlyOnce()
        {
            var runtime = Run("fun run(): int { var n = 0; for (i in 5..=5) { n = n + 1; } return n; }");
            Assert.Equal(1, Int(runtime, "run"));
        }

        #endregion

        #region Every way out

        [Fact]
        public void BreakLeavesAnArrayWalkAtTheRightElement()
        {
            var runtime = Run(
                "fun run(): int { let xs = [1, 2, 3, 4]; var t = 0;\n"
                    + "  for (x in xs) { if (x == 3) { break; } t = t + x; } return t; }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        [Fact]
        public void ContinueSkipsToTheNextElementRatherThanRestarting()
        {
            var runtime = Run(
                "fun run(): int { let xs = [1, 2, 3, 4]; var t = 0;\n"
                    + "  for (x in xs) { if (x % 2 == 0) { continue; } t = t + x; } return t; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        /// <summary>
        /// <c>continue</c> targets the step, which after the rotation *is* the fused instruction -
        /// so a continue on the very last element still has to fall out of the loop rather than
        /// read past the end.
        /// </summary>
        [Fact]
        public void ContinueOnTheLastElementStillEndsTheWalk()
        {
            var runtime = Run(
                "fun run(): int { let xs = [1, 2, 3]; var t = 0;\n"
                    + "  for (x in xs) { if (x == 3) { continue; } t = t + x; } return t; }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        [Fact]
        public void ReturnLeavesAnArrayWalkImmediately()
        {
            var runtime = Run(
                "fun run(): int { let xs = [1, 2, 3]; for (x in xs) { if (x == 2) { return 99; } } return 0; }");

            Assert.Equal(99, Int(runtime, "run"));
        }

        [Fact]
        public void BreakAndContinueWorkInARangeWalk()
        {
            var runtime = Run(
                "fun run(): int { var t = 0;\n"
                    + "  for (i in 0..10) { if (i == 7) { break; } if (i % 2 == 0) { continue; } t = t + i; }\n"
                    + "  return t; }");

            Assert.Equal(1 + 3 + 5, Int(runtime, "run"));
        }

        [Fact]
        public void BreakAndContinueWorkInADictionaryWalk()
        {
            var runtime = Run(
                "fun run(): int { let m: {int: int} = {1: 1, 2: 2, 3: 3, 4: 4}; var t = 0;\n"
                    + "  for (e in m) { if (e[0] == 4) { break; } if (e[0] == 2) { continue; } t = t + e[1]; }\n"
                    + "  return t; }");

            Assert.Equal(1 + 3, Int(runtime, "run"));
        }

        [Fact]
        public void ANestedWalkKeepsTheTwoStepsApart()
        {
            var runtime = Run(
                "fun run(): int { let xs = [1, 2, 3]; var t = 0;\n"
                    + "  for (a in xs) { for (b in xs) { t = t + a * b; } } return t; }");

            Assert.Equal(36, Int(runtime, "run"));
        }

        #endregion

        #region Around a protected region

        /// <summary>
        /// A <c>try</c> wrapping a loop still catches out of its body, and the handler's protected
        /// range still covers the rotated step.
        /// </summary>
        [Fact]
        public void AThrowOutOfAWalkIsCaughtByTheTryAroundIt()
        {
            var runtime = Run(
                "import surtr.core.Exception;\n"
                    + "fun run(): int { let xs = [1, 2, 3]; var t = 0;\n"
                    + "  try { for (x in xs) { if (x == 2) { throw Exception(\"stop\"); } t = t + x; } }\n"
                    + "  catch (e: Exception) { t = t + 100; }\n"
                    + "  return t; }");

            Assert.Equal(101, Int(runtime, "run"));
        }

        /// <summary>A <c>finally</c> around a loop still runs when the loop is left by <c>break</c>.</summary>
        [Fact]
        public void AFinallyAroundAWalkRunsOnBreak()
        {
            var runtime = Run(
                "fun run(): int { let xs = [1, 2, 3]; var t = 0;\n"
                    + "  try { for (x in xs) { if (x == 2) { break; } t = t + x; } } finally { t = t + 10; }\n"
                    + "  return t; }");

            Assert.Equal(11, Int(runtime, "run"));
        }

        /// <summary>And when it is left by <c>return</c>, which unwinds through the region.</summary>
        [Fact]
        public void AFinallyAroundAWalkRunsOnReturn()
        {
            var runtime = Run(
                "fun run(): int { let xs = [1, 2, 3]; var t = 0;\n"
                    + "  try { for (x in xs) { if (x == 2) { return t + 50; } t = t + x; } } finally { t = t + 10; }\n"
                    + "  return t; }");

            Assert.Equal(51, Int(runtime, "run"));
        }

        /// <summary>
        /// A <c>try</c> <em>inside</em> the loop body: the protected region is entered and left
        /// once per iteration, and the step sits outside it.
        /// </summary>
        [Fact]
        public void ATryInsideAWalkIsEnteredEveryIteration()
        {
            var runtime = Run(
                "import surtr.core.Exception;\n"
                    + "fun run(): int { let xs = [1, 2, 3]; var t = 0;\n"
                    + "  for (x in xs) { try { if (x == 2) { throw Exception(\"skip\"); } t = t + x; }\n"
                    + "                  catch (e: Exception) { t = t + 100; } }\n"
                    + "  return t; }");

            Assert.Equal(104, Int(runtime, "run"));
        }

        #endregion

        #region The walk sees what the body does

        /// <summary>
        /// The step reloads the collection's length every element, which is what makes a body that
        /// appends visible to the walk. The written-out form did that by re-emitting <c>ArrLen</c>
        /// per iteration; the fused one has to keep it, or a growing array would stop short.
        /// </summary>
        [Fact]
        public void AWalkSeesElementsTheBodyAppends()
        {
            var runtime = Run(
                "fun run(): int { let xs = [1]; var n = 0;\n"
                    + "  for (x in xs) { n = n + 1; if (n < 5) { xs.push(x + 1); } } return n; }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>
        /// A dictionary walk reads a snapshot of the keys, so an insertion during the walk is not
        /// visited - and deleting a key the snapshot still lists is the one case that keeps the
        /// fused step's absent-key trap reachable.
        /// </summary>
        [Fact]
        public void ADictionaryWalkDoesNotVisitWhatTheBodyInserts()
        {
            var runtime = Run(
                "fun run(): int { let m: {int: int} = {1: 1}; var n = 0;\n"
                    + "  for (e in m) { n = n + 1; if (n < 3) { m[n + 10] = 0; } } return n; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void DeletingAKeyTheSnapshotStillListsTraps()
        {
            var runtime = Run(
                "fun run(): int { let m: {int: int} = {1: 1, 2: 2}; var t = 0;\n"
                    + "  for (e in m) { m.remove(2); t = t + e[1]; } return t; }");

            Assert.ThrowsAny<Exception>(() => Call(runtime, "run"));
        }

        #endregion
    }
}
