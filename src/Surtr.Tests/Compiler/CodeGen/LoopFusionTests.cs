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

        private static System.Collections.Generic.IEnumerable<string> Lines(string disassembly)
            => disassembly.Split(new[] { '\n' }).Select(line => line.Trim());

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

        #region The counted while

        // `while (i < n) { ...; i += 1; }` takes the same ForRangeNext step a `for i in 0..n`
        // does. The risk is the same one the range walk had - the test moves from the top of the
        // body to a guard plus a bottom step - plus one that is new here: the increment is a
        // statement the program wrote, so anything that can skip it (a `continue`) or that can
        // change what the step reads (an assignment to the counter or the limit) has to keep
        // behaving exactly as the unfused loop did.

        [Fact]
        public void ACountedWhileStepsWithForRangeNext()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; var i = 0; while (i < n) { t = t + i; i += 1; } return t; }");

            Assert.Equal(1, Count(code, "ForRangeNextLT"));
            Assert.Equal(0, Count(code, "IncLocal"));

            // One guard above the loop, not one test per iteration.
            Assert.Equal(1, Count(code, "JPGE"));
        }

        [Fact]
        public void AConstantLimitIsHoistedIntoItsOwnSlot()
        {
            string code = Disassemble(
                "fun run(): int { var t = 0; var i = 0; while (i < 10) { t = t + i; i += 1; } return t; }");

            Assert.Equal(1, Count(code, "ForRangeNextLT"));

            // The limit reaches a slot once, above the loop, instead of being pushed per iteration.
            Assert.Equal(1, Lines(code).Count(line => line.EndsWith("PushI8 10", StringComparison.Ordinal)));
        }

        [Fact]
        public void AnInclusiveLimitUsesTheInclusiveStep()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; var i = 0; while (i <= n) { t = t + i; i += 1; } return t; }");

            Assert.Equal(1, Count(code, "ForRangeNextLE"));
        }

        [Fact]
        public void ACountedWhileCountsWhatTheWrittenLoopCounts()
        {
            var runtime = Run(
                "fun run(n: int): int { var t = 0; var i = 0; while (i < n) { t = t + i; i += 1; } return t; }\n"
                    + "fun upTo(n: int): int { var t = 0; var i = 0; while (i <= n) { t = t + i; i += 1; } return t; }");

            Assert.Equal(0, Int(runtime, "run", SurtrValue.CreateInt(0)));
            Assert.Equal(0, Int(runtime, "run", SurtrValue.CreateInt(-3)));
            Assert.Equal(0, Int(runtime, "run", SurtrValue.CreateInt(1)));
            Assert.Equal(45, Int(runtime, "run", SurtrValue.CreateInt(10)));
            Assert.Equal(55, Int(runtime, "upTo", SurtrValue.CreateInt(10)));
            Assert.Equal(0, Int(runtime, "upTo", SurtrValue.CreateInt(0)));
        }

        [Fact]
        public void BreakLeavesACountedWhileAtOnce()
        {
            var runtime = Run(
                "fun run(n: int): int { var t = 0; var i = 0;\n"
                    + "  while (i < n) { if (i == 3) { break; } t = t + i; i += 1; } return t; }");

            Assert.Equal(3, Int(runtime, "run", SurtrValue.CreateInt(100)));
        }

        [Fact]
        public void AContinueRefusesTheFusionRatherThanSkippingTheIncrement()
        {
            // The written loop re-tests without incrementing, so this counts 1..9 skipping 5 and
            // must not turn into a step that increments on the way round.
            string source =
                "fun run(): int { var t = 0; var i = 0;\n"
                    + "  while (i < 10) { i += 1; if (i == 5) { continue; } t = t + i; } return t; }";

            Assert.Equal(0, Count(Disassemble(source), "ForRangeNextLT"));
            Assert.Equal(50, Int(Run(source), "run"));
        }

        [Fact]
        public void AContinueInsideANestedLoopAlsoRefusesTheFusion()
        {
            // A labelled continue can name the outer loop from inside the inner one, so the scan
            // descends rather than trying to work out which loop each continue belongs to.
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; var i = 0;\n"
                    + "  while (i < n) { for (x in 0..2) { if (x == 1) { continue; } t = t + 1; } i += 1; }\n"
                    + "  return t; }");

            Assert.Equal(1, Count(code, "ForRangeNextLT"));
        }

        [Fact]
        public void TheStepSeesABodyThatMovesTheCounter()
        {
            // The step increments the slot, so a body that also wrote to it is seen exactly as the
            // written loop saw it: the two writes compose.
            var runtime = Run(
                "fun run(): int { var t = 0; var i = 0;\n"
                    + "  while (i < 10) { t = t + 1; i = i + 2; i += 1; } return t; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void TheStepSeesABodyThatMovesTheLimit()
        {
            // Both slots are re-read every step, so shrinking the limit inside the body ends the
            // loop where the written form ended it.
            var runtime = Run(
                "fun run(): int { var t = 0; var i = 0; var n = 10;\n"
                    + "  while (i < n) { t = t + 1; if (t == 3) { n = 4; } i += 1; } return t; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void AnIncrementThatIsNotLastRefusesTheFusion()
        {
            // Anything after the increment would run between it and the test, which the fused step
            // has no room for.
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; var i = 0; while (i < n) { i += 1; t = t + i; } return t; }");

            Assert.Equal(0, Count(code, "ForRangeNextLT"));
        }

        [Fact]
        public void AStepOtherThanOneRefusesTheFusion()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; var i = 0; while (i < n) { t = t + i; i += 2; } return t; }");

            Assert.Equal(0, Count(code, "ForRangeNextLT"));
        }

        [Fact]
        public void APostIncrementStepAlsoFuses()
        {
            // `i++` binds to a `BoundUnaryExpression`, not the `BoundAssignmentExpression` that
            // `i = i + 1` / `i += 1` produce (`BodyBinder.BindUnary`), so this is a distinct shape
            // the fusion has to recognise on its own rather than getting for free.
            var runtime = Run(
                "fun run(n: int): int { var t = 0; var i = 0; while (i < n) { t = t + i; i++; } return t; }");

            Assert.Equal(1, Count(Disassemble(
                "fun run(n: int): int { var t = 0; var i = 0; while (i < n) { t = t + i; i++; } return t; }"),
                "ForRangeNextLT"));
            Assert.Equal(45, Int(runtime, "run", SurtrValue.CreateInt(10)));
        }

        [Fact]
        public void APreIncrementStepAlsoFuses()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; var i = 0; while (i < n) { t = t + i; ++i; } return t; }");

            Assert.Equal(1, Count(code, "ForRangeNextLT"));
        }

        [Fact]
        public void ACountingDownWhileRefusesTheFusion()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; var i = n; while (i > 0) { t = t + i; i += -1; } return t; }");

            Assert.Equal(0, Count(code, "ForRangeNextLT"));
            Assert.Equal(0, Count(code, "ForRangeNextLE"));
        }

        [Fact]
        public void ACountedWhileInsideATryStillLeavesThroughTheHandler()
        {
            var runtime = Run(
                "fun run(): int { var t = 0;\n"
                    + "  try { var i = 0; while (i < 10) { t = t + 1; if (t == 3) { throw Exception(\"x\"); } i += 1; } }\n"
                    + "  catch (e: Exception) { t = t + 100; }\n"
                    + "  return t; }");

            Assert.Equal(103, Int(runtime, "run"));
        }

        [Fact]
        public void ACountedWhileThatAlwaysReturnsEmitsNoStep()
        {
            // The body cannot fall out, so nothing reaches the step - and emitting one there would
            // put an instruction after the last reachable point of the loop.
            var runtime = Run(
                "fun run(n: int): int { var i = 0; while (i < n) { return 7; } return 0; }");

            Assert.Equal(7, Int(runtime, "run", SurtrValue.CreateInt(3)));
            Assert.Equal(0, Int(runtime, "run", SurtrValue.CreateInt(0)));
        }

        #endregion

        #region The counted for

        // `for (init; i < n; i += 1) { ... }` takes the same ForRangeNext step the counted `while`
        // does. The increment lives in the loop's own `step` clause rather than as the body's last
        // statement, so there is no last-statement peeling here - but the risk of landing a `break`,
        // a `continue` or an early `return` in the wrong place is exactly the same one the counted
        // `while` tests cover, so this mirrors that region rather than inventing new coverage.

        [Fact]
        public void ACountedForStepsWithForRangeNext()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; for (var i = 0; i < n; i = i + 1) { t = t + i; } return t; }");

            Assert.Equal(1, Count(code, "ForRangeNextLT"));
            Assert.Equal(0, Count(code, "IncLocal"));
            Assert.Equal(1, Count(code, "JPGE"));
        }

        [Fact]
        public void ACountedForAcceptsACompoundAssignmentStep()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; for (var i = 0; i < n; i += 1) { t = t + i; } return t; }");

            Assert.Equal(1, Count(code, "ForRangeNextLT"));
        }

        [Fact]
        public void AnInclusiveForLimitUsesTheInclusiveStep()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; for (var i = 0; i <= n; i = i + 1) { t = t + i; } return t; }");

            Assert.Equal(1, Count(code, "ForRangeNextLE"));
        }

        [Fact]
        public void ACountedForCountsWhatTheWrittenLoopCounts()
        {
            var runtime = Run(
                "fun run(n: int): int { var t = 0; for (var i = 0; i < n; i = i + 1) { t = t + i; } return t; }\n"
                    + "fun upTo(n: int): int { var t = 0; for (var i = 0; i <= n; i = i + 1) { t = t + i; } return t; }");

            Assert.Equal(0, Int(runtime, "run", SurtrValue.CreateInt(0)));
            Assert.Equal(0, Int(runtime, "run", SurtrValue.CreateInt(-3)));
            Assert.Equal(0, Int(runtime, "run", SurtrValue.CreateInt(1)));
            Assert.Equal(45, Int(runtime, "run", SurtrValue.CreateInt(10)));
            Assert.Equal(55, Int(runtime, "upTo", SurtrValue.CreateInt(10)));
            Assert.Equal(0, Int(runtime, "upTo", SurtrValue.CreateInt(0)));
        }

        [Fact]
        public void BreakLeavesACountedForAtOnce()
        {
            var runtime = Run(
                "fun run(n: int): int { var t = 0;\n"
                    + "  for (var i = 0; i < n; i = i + 1) { if (i == 3) { break; } t = t + i; } return t; }");

            Assert.Equal(3, Int(runtime, "run", SurtrValue.CreateInt(100)));
        }

        [Fact]
        public void AContinueInACountedForRefusesTheFusionButStillBehaves()
        {
            // Unlike the counted `while`, a `for`'s `continue` runs the step before re-testing
            // either way - fused or not - so this is a safety-margin check, not a semantics one:
            // the fusion still refuses to fire, and the unfused path still counts correctly.
            string source =
                "fun run(): int { var t = 0;\n"
                    + "  for (var i = 0; i < 10; i = i + 1) { if (i == 5) { continue; } t = t + i; } return t; }";

            Assert.Equal(0, Count(Disassemble(source), "ForRangeNextLT"));
            Assert.Equal(40, Int(Run(source), "run"));
        }

        [Fact]
        public void AContinueInsideANestedForAlsoRefusesTheOuterFusion()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0;\n"
                    + "  for (var i = 0; i < n; i = i + 1) { for (x in 0..2) { if (x == 1) { continue; } t = t + 1; } }\n"
                    + "  return t; }");

            Assert.Equal(1, Count(code, "ForRangeNextLT"));
        }

        [Fact]
        public void TheForStepSeesABodyThatMovesTheCounter()
        {
            var runtime = Run(
                "fun run(): int { var t = 0;\n"
                    + "  for (var i = 0; i < 10; i = i + 1) { t = t + 1; i = i + 2; } return t; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void TheForStepSeesABodyThatMovesTheLimit()
        {
            var runtime = Run(
                "fun run(): int { var t = 0; var n = 10;\n"
                    + "  for (var i = 0; i < n; i = i + 1) { t = t + 1; if (t == 3) { n = 4; } } return t; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void AForStepOtherThanOneRefusesTheFusion()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; for (var i = 0; i < n; i += 2) { t = t + i; } return t; }");

            Assert.Equal(0, Count(code, "ForRangeNextLT"));
        }

        [Fact]
        public void AForWithAPostIncrementStepAlsoFuses()
        {
            // The single most idiomatic C-family loop shape - `for (...; i < n; i++)` - has to
            // reach the fusion too, not just its `i += 1` cousin.
            var runtime = Run(
                "fun run(n: int): int { var t = 0; for (var i = 0; i < n; i++) { t = t + i; } return t; }");

            Assert.Equal(1, Count(Disassemble(
                "fun run(n: int): int { var t = 0; for (var i = 0; i < n; i++) { t = t + i; } return t; }"),
                "ForRangeNextLT"));
            Assert.Equal(45, Int(runtime, "run", SurtrValue.CreateInt(10)));
        }

        [Fact]
        public void AForWithAPreIncrementStepAlsoFuses()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; for (var i = 0; i < n; ++i) { t = t + i; } return t; }");

            Assert.Equal(1, Count(code, "ForRangeNextLT"));
        }

        [Fact]
        public void ACountingDownForRefusesTheFusion()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; for (var i = n; i > 0; i += -1) { t = t + i; } return t; }");

            Assert.Equal(0, Count(code, "ForRangeNextLT"));
            Assert.Equal(0, Count(code, "ForRangeNextLE"));
        }

        [Fact]
        public void AForWithNoStepClauseRefusesTheFusion()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0; for (var i = 0; i < n;) { t = t + i; i = i + 1; } return t; }");

            Assert.Equal(0, Count(code, "ForRangeNextLT"));
        }

        [Fact]
        public void AForWithNoConditionRefusesTheFusion()
        {
            string code = Disassemble(
                "fun run(n: int): int { var t = 0;\n"
                    + "  for (var i = 0; ; i = i + 1) { if (i >= n) { break; } t = t + i; } return t; }");

            Assert.Equal(0, Count(code, "ForRangeNextLT"));
            Assert.Equal(0, Count(code, "ForRangeNextLE"));
        }

        [Fact]
        public void ACountedForInsideATryStillLeavesThroughTheHandler()
        {
            var runtime = Run(
                "fun run(): int { var t = 0;\n"
                    + "  try { for (var i = 0; i < 10; i = i + 1) { t = t + 1; if (t == 3) { throw Exception(\"x\"); } } }\n"
                    + "  catch (e: Exception) { t = t + 100; }\n"
                    + "  return t; }");

            Assert.Equal(103, Int(runtime, "run"));
        }

        [Fact]
        public void ACountedForThatAlwaysReturnsEmitsNoStep()
        {
            var runtime = Run(
                "fun run(n: int): int { for (var i = 0; i < n; i = i + 1) { return 7; } return 0; }");

            Assert.Equal(7, Int(runtime, "run", SurtrValue.CreateInt(3)));
            Assert.Equal(0, Int(runtime, "run", SurtrValue.CreateInt(0)));
        }

        #endregion
    }
}
