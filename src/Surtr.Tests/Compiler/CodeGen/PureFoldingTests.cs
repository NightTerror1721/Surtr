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

        private static SurtrValue Call(SurtrRuntime runtime, string name)
        {
            Assert.True(runtime.TryGetModule("game.core.Test", out var module), "No test module was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"'{name}' declares no function.");
            return runtime.Invoke(overloads[0], Array.Empty<SurtrValue>());
        }

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
        /// A <c>@Pure</c> body that calls another function is not folded — the callee's referential
        /// transparency is not guaranteed by the caller's mark, and proving it transitively is a
        /// later refinement.
        /// </summary>
        [Fact]
        public void APureFunctionThatCallsIsNotFolded()
        {
            string code = Disassemble(
                "@Pure fun helper(x: float): float { return x * 2.0; }\n"
                    + "@Pure fun uses(x: float): float { return helper(x) * 3.0 + 1.0; }\n"
                    + "fun run(): float { return uses(2.0); }");

            Assert.Equal(1, Count(code, "CallLocalModule"));
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
    }
}