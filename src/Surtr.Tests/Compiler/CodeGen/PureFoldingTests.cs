#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;
using System;
using System.IO;
using System.Linq;

namespace Surtr.Tests.Compiler.CodeGen
{
    /// <summary>
    /// Covers §P3 fase 3: folding calls to a verified-strict <c>@Pure</c> function whose arguments
    /// are all compile-time constants. The fold replaces the call with its evaluated result, so a
    /// folded body carries no <c>Call*</c> opcode and a non-foldable one keeps the call.
    /// </summary>
    public sealed class PureFoldingTests : IDisposable
    {
        private const string Root = "D:/proj/src";

        private readonly System.Collections.Generic.List<IDisposable> _owned =
            new System.Collections.Generic.List<IDisposable>();

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
        }

        /// <summary>Compiles one module and renders its bytecode.</summary>
        private string Disassemble(string source)
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

            return SurtrBytecodeDisassembler.Disassemble(emitter.Modules[0]);
        }

        /// <summary>Builds and loads a module, returning the runnable runtime.</summary>
        private SurtrRuntime Run(string source)
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

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            return runtime;
        }

        private static SurtrValue Call(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
        {
            Assert.True(runtime.TryGetModule("game.core.Test", out var module), "No test module was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"'{name}' declares no function.");
            return runtime.Invoke(overloads[0], arguments);
        }

        private static int Int(SurtrRuntime runtime, string name)
            => Call(runtime, name).AsInt;

        private static int Count(string disassembly, string mnemonic)
            => disassembly
                .Split('\n')
                .Count(line => line.Trim().Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1)
                    .FirstOrDefault()?.Split(' ')[0] == mnemonic);

        /// <summary>
        /// A strictly-pure <c>@Pure</c> function with constant arguments folds: the call disappears
        /// from the bytecode and the result is the evaluated constant. The body is deliberately
        /// larger than the inline threshold, so its absence proves folding rather than inlining.
        /// </summary>
        [Fact]
        public void AStrictlyPureCallWithConstantArgumentsIsFolded()
        {
            string code = Disassemble(
                "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun run(): float { return big(2.0); }");

            Assert.Equal(0, Count(code, "CallLocalModule"));
            Assert.Equal(0, Count(code, "CallModule"));

            var runtime = Run(
                "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun run(): float { return big(2.0); }");

            Assert.Equal(8.0, Call(runtime, "run").AsFloat);
        }

        /// <summary>
        /// A foldable <c>@Pure</c> function with a branch folds each arm into the constant it
        /// evaluates to — the folder runs the real bytecode, so a conditional is answered, not
        /// skipped.
        /// </summary>
        [Fact]
        public void APureFunctionWithBranchesFoldsThroughThem()
        {
            var runtime = Run(
                "@Pure fun clamp(v: float): float\n"
                    + "{\n"
                    + "  if (v < 0.0) { return 0.0; }\n"
                    + "  if (v > 100.0) { return 100.0; }\n"
                    + "  return v;\n"
                    + "}\n"
                    + "fun run(): float { return clamp(150.0) + clamp(-1.0) + clamp(50.0); }");

            Assert.Equal(150.0, Call(runtime, "run").AsFloat);

            string code = Disassemble(
                "@Pure fun clamp(v: float): float\n"
                    + "{\n"
                    + "  if (v < 0.0) { return 0.0; }\n"
                    + "  if (v > 100.0) { return 100.0; }\n"
                    + "  return v;\n"
                    + "}\n"
                    + "fun run(): float { return clamp(150.0); }");

            Assert.Equal(0, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// A call whose argument is not a compile-time constant cannot fold: the result depends on
        /// the runtime value, so the call must stay.
        /// </summary>
        [Fact]
        public void ACallWithARuntimeArgumentIsNotFolded()
        {
            string code = Disassemble(
                "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun run(v: float): float { return big(v); }");

            Assert.Equal(1, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// A <c>@Pure</c> body that reads a mutable module variable is not referentially transparent,
        /// so folding it would freeze a value that can change — the call survives.
        /// </summary>
        [Fact]
        public void APureFunctionReadingMutableStateIsNotFolded()
        {
            string code = Disassemble(
                "var base: float = 10.0;\n"
                    + "@Pure fun big(x: float): float { return x * x + x * base - base / x + 1.0; }\n"
                    + "fun run(): float { return big(2.0); }");

            Assert.Equal(1, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// A <c>@Pure</c> body that calls a non-<c>@Pure</c> function is not folded — the callee's
        /// referential transparency is not established, so neither is the caller's.
        /// </summary>
        [Fact]
        public void APureFunctionThatCallsAnImpureFunctionIsNotFolded()
        {
            string code = Disassemble(
                "fun helper(x: float): float { return x * 2.0; }\n"
                    + "@Pure fun uses(x: float): float { return helper(x) * 3.0 + 1.0; }\n"
                    + "fun run(): float { return uses(2.0); }");

            Assert.Equal(1, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// Transitive folding: a <c>@Pure</c> function whose body calls only other verified
        /// <c>@Pure</c> functions is itself foldable, so <c>clamp01</c> composed from <c>@Pure</c>
        /// helpers folds down to a constant.
        /// </summary>
        [Fact]
        public void APureFunctionCallingPureHelpersFoldsTransitively()
        {
            string code = Disassemble(
                "@Pure fun minOf(a: float, b: float): float { return a < b ? a : b; }\n"
                    + "@Pure fun maxOf(a: float, b: float): float { return a > b ? a : b; }\n"
                    + "@Pure fun clamp01(x: float): float { return maxOf(0.0, minOf(1.0, x)); }\n"
                    + "fun run(): float { return clamp01(5.0); }");

            Assert.Equal(0, Count(code, "CallLocalModule"));

            var runtime = Run(
                "@Pure fun minOf(a: float, b: float): float { return a < b ? a : b; }\n"
                    + "@Pure fun maxOf(a: float, b: float): float { return a > b ? a : b; }\n"
                    + "@Pure fun clamp01(x: float): float { return maxOf(0.0, minOf(1.0, x)); }\n"
                    + "fun run(): float { return clamp01(5.0); }");

            Assert.Equal(1.0, Call(runtime, "run").AsFloat);
        }

        /// <summary>
        /// A <c>@Pure</c> function whose body reaches a pure native built-in is foldable too: the
        /// native has no body of its own, but it runs in the evaluator's scratch runtime, so
        /// folding a call to the wrapper evaluates the string read to a constant.
        /// </summary>
        [Fact]
        public void APureFunctionCallingAPureNativeFolds()
        {
            string code = Disassemble(
                "@Pure fun first(s: string): char { return s.charAt(0); }\n"
                    + "fun run(): char { return first(\"abc\"); }");

            Assert.Equal(0, Count(code, "CallLocalModule"));

            var runtime = Run(
                "@Pure fun first(s: string): char { return s.charAt(0); }\n"
                    + "fun run(): char { return first(\"abc\"); }");

            Assert.Equal('a', Call(runtime, "run").AsChar);
        }

        /// <summary>
        /// An instance <c>@Pure</c> method is not folded: its result can depend on the receiver's
        /// state, which a static-only folding pass cannot know is constant.
        /// </summary>
        [Fact]
        public void AnInstancePureMethodIsNotFolded()
        {
            string code = Disassemble(
                "class Calculator {\n"
                    + "  public var scale: float = 1.0;\n"
                    + "  @Pure public fun apply(x: float): float { return x * x + x * this.scale - this.scale / x + 1.0; }\n"
                    + "}\n"
                    + "fun run(): float { let c = Calculator(); return c.apply(2.0); }");

            Assert.True(code.Contains("(apply)", StringComparison.Ordinal), "Disassembly:\n" + code);
        }

        /// <summary>
        /// CSE: two identical <c>@Pure</c> calls in one binary expression are evaluated once — the
        /// bytecode carries a single call to the helper, and the result is the same as two calls.
        /// </summary>
        [Fact]
        public void ADuplicatedPureCallInABinaryIsEvaluatedOnce()
        {
            string code = Disassemble(
                "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun run(v: float): float { return big(v) + big(v); }");

            Assert.Equal(1, Count(code, "CallLocalModule"));

            var runtime = Run(
                "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun run(v: float): float { return big(v) + big(v); }");

            Assert.Equal(16.0, Call(runtime, "run", SurtrValue.CreateFloat(2.0)).AsFloat);
        }

        /// <summary>
        /// CSE: two identical <c>@Pure</c> calls as arguments of another call are evaluated once.
        /// </summary>
        [Fact]
        public void ADuplicatedPureCallAmongCallArgumentsIsEvaluatedOnce()
        {
            string code = Disassemble(
                "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun combine(a: float, b: float): float { return a + b; }\n"
                    + "fun run(v: float): float { return combine(big(v), big(v)); }");

            Assert.Equal(1, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// CSE does not collapse a call whose argument has effects: the argument is evaluated once
        /// per call by design, so two calls mean two evaluations.
        /// </summary>
        [Fact]
        public void ADuplicatedPureCallOverAnImpureArgumentIsNotCollapsed()
        {
            var runtime = Run(
                "var calls: int = 0;\n"
                    + "fun next(): float { calls = calls + 1; return 2.0; }\n"
                    + "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun run(): int { let total = big(next()) + big(next()); return calls; }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        /// <summary>
        /// Cross-statement CSE: the same <c>@Pure</c> call across two statements of a straight-line
        /// run is evaluated once, with the second statement reading the first's local.
        /// </summary>
        [Fact]
        public void APureCallRepeatedAcrossStatementsIsEvaluatedOnce()
        {
            string code = Disassemble(
                "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun run(v: float): float { let a = big(v); let b = big(v); return a + b; }");

            Assert.Equal(1, Count(code, "CallLocalModule"));

            var runtime = Run(
                "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun run(v: float): float { let a = big(v); let b = big(v); return a + b; }");

            Assert.Equal(16.0, Call(runtime, "run", SurtrValue.CreateFloat(2.0)).AsFloat);
        }

        /// <summary>
        /// A write to a call's argument between the two occurrences kills the reuse: the second call
        /// sees the new argument, so it has to run again.
        /// </summary>
        [Fact]
        public void AWriteToAnArgumentBetweenStatementsKillsTheReuse()
        {
            string code = Disassemble(
                "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun run(v: float): float { let a = big(v); v = 5.0; let b = big(v); return a + b; }");

            Assert.Equal(2, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// Control flow whose body writes a call's argument kills the reuse: after the construct
        /// the argument may hold a different value, so the call runs again.
        /// </summary>
        [Fact]
        public void ControlFlowThatWritesAnOperandBetweenStatementsKillsTheReuse()
        {
            string code = Disassemble(
                "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun run(v: float): float\n"
                    + "{\n"
                    + "  let a = big(v);\n"
                    + "  if (v > 0.0) { v = 1.0; }\n"
                    + "  let b = big(v);\n"
                    + "  return a + b;\n"
                    + "}");

            Assert.Equal(2, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// Dominance: an expression available before an <c>if</c> dominates code after it, so the
        /// reuse survives the branch as long as the branch writes none of its operands.
        /// </summary>
        [Fact]
        public void AControlFlowThatWritesNothingKeepsTheReuseAcrossIt()
        {
            string code = Disassemble(
                "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun run(v: float): float\n"
                    + "{\n"
                    + "  let a = big(v);\n"
                    + "  var t = 0.0;\n"
                    + "  if (v > 0.0) { t = 1.0; }\n"
                    + "  let b = big(v);\n"
                    + "  return a + b + t;\n"
                    + "}");

            Assert.Equal(1, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// Dominance across a loop: an expression available before a loop dominates code after it,
        /// so the reuse survives when the loop body writes none of its operands.
        /// </summary>
        [Fact]
        public void AReuseSurvivesALoopThatWritesNothing()
        {
            string code = Disassemble(
                "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun run(v: float): float\n"
                    + "{\n"
                    + "  let a = big(v);\n"
                    + "  var i = 0;\n"
                    + "  while (i < 3) { i = i + 1; }\n"
                    + "  let b = big(v);\n"
                    + "  return a + b;\n"
                    + "}");

            Assert.Equal(1, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// A loop body that writes a call's argument kills the reuse after the loop: the value may
        /// have changed, so the call runs again.
        /// </summary>
        [Fact]
        public void ALoopThatWritesAnOperandKillsTheReuse()
        {
            string code = Disassemble(
                "@Pure fun big(x: float): float { return x * 2.0 + 3.0 * x - 4.0 / x; }\n"
                    + "fun run(v: float): float\n"
                    + "{\n"
                    + "  let a = big(v);\n"
                    + "  var i = 0;\n"
                    + "  while (i < 3) { v = v + 1.0; i = i + 1; }\n"
                    + "  let b = big(v);\n"
                    + "  return a + b;\n"
                    + "}");

            Assert.Equal(2, Count(code, "CallLocalModule"));
        }
    }
}