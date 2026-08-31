#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Stdlib.Script;
using System;
using System.Linq;

namespace Surtr.Tests.Stdlib
{
    /// <summary>
    /// End-to-end coverage for <c>Surtr.Stdlib.Script</c> (Fase 8, §6.3: dynamic compilation/eval) -
    /// compiles a real driver script through the ordinary <c>SurtrCompilation</c> path, loads
    /// <c>surtr.script.Script</c>'s own native bodies via <see cref="SurtrScripting.LoadInto"/>, and
    /// runs the whole thing on a real <see cref="SurtrRuntime"/>.
    /// </summary>
    public sealed class SurtrScriptingTests : IDisposable
    {
        private const string Root = "D:/proj/src";
        private readonly System.Collections.Generic.List<IDisposable> _owned = new System.Collections.Generic.List<IDisposable>();

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
        }

        private SurtrRuntime BuildAndLoad(string driverSource)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", "test", driverSource);
            project.AddSourceFile(Root + "/surtr/script/Script.surtr", SurtrScripting.ScriptModulePath, SurtrScripting.ScriptModuleSource);

            var compilation = SurtrCompilation.Create(project);
            _owned.Add(compilation);

            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(!compilation.HasErrors, "Binding reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var emitter = new ModuleEmitter(compilation, binder);
            Assert.True(emitter.TryEmit(), "Emission reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            SurtrScripting.RegisterNativeBodies(runtime);

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            return runtime;
        }

        private static Surtr.Runtime.Classes.SurtrMethodInfo Function(SurtrRuntime runtime, string name)
        {
            Assert.True(runtime.TryGetModule("test", out var module), "no test module");
            Assert.True(module.TryGetMethods(name, out var overloads), "missing " + name);
            return overloads[0];
        }

        private static int Int(SurtrRuntime runtime, string name) => runtime.Invoke(Function(runtime, name)).AsInt;
        private static bool Bool(SurtrRuntime runtime, string name) => runtime.Invoke(Function(runtime, name)).AsBool;
        private static string Text(SurtrRuntime runtime, string name)
            => runtime.Resolve<Surtr.Runtime.Objects.SurtrString>(runtime.Invoke(Function(runtime, name)))!.Text;

        [Fact]
        public void CompilesAndCallsAFunctionWithArguments()
        {
            var runtime = BuildAndLoad(
                "import surtr.script.Script;\n"
                    + "fun run(): int {\n"
                    + "  let s = Script.compile(\"fun add(a: int, b: int): int { return a + b; }\");\n"
                    + "  if (!s.isValid) return -1;\n"
                    + "  return s.call(\"add\", 3, 4) as int;\n"
                    + "}\n");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void HasFunctionReportsPresenceCorrectly()
        {
            var runtime = BuildAndLoad(
                "import surtr.script.Script;\n"
                    + "fun run(): bool {\n"
                    + "  let s = Script.compile(\"fun greet(): string { return \\\"hi\\\"; }\");\n"
                    + "  return s.hasFunction(\"greet\") && !s.hasFunction(\"nope\");\n"
                    + "}\n");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void InvalidSourceIsNotValidAndReportsAnError()
        {
            var runtime = BuildAndLoad(
                "import surtr.script.Script;\n"
                    + "fun run(): bool {\n"
                    + "  let s = Script.compile(\"this is not surtr at all {{{\");\n"
                    + "  return !s.isValid && s.lastError().length > 0;\n"
                    + "}\n");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void EvalIntEvaluatesAnExpressionDirectly()
        {
            var runtime = BuildAndLoad(
                "import surtr.script.Script;\n"
                    + "fun run(): int { return evalInt(\"6 * 7\"); }\n");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void EvalStringEvaluatesAStringExpression()
        {
            var runtime = BuildAndLoad(
                "import surtr.script.Script;\n"
                    + "fun run(): string { return evalString(\"\\\"hello\\\" + \\\" \\\" + \\\"world\\\"\"); }\n");

            Assert.Equal("hello world", Text(runtime, "run"));
        }

        [Fact]
        public void TwoSeparateCompilesDoNotCollide()
        {
            var runtime = BuildAndLoad(
                "import surtr.script.Script;\n"
                    + "fun run(): int {\n"
                    + "  let a = Script.compile(\"fun value(): int { return 1; }\");\n"
                    + "  let b = Script.compile(\"fun value(): int { return 2; }\");\n"
                    + "  return (a.call(\"value\") as int) * 10 + (b.call(\"value\") as int);\n"
                    + "}\n");

            Assert.Equal(12, Int(runtime, "run"));
        }
    }
}
