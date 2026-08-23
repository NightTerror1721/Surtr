#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;

namespace Surtr.Tests.Compiler.CodeGen
{
    /// <summary>
    /// Covers the one native signature answering several slots: the body writes its results into
    /// the same <see cref="SurtrCallArguments"/> block the arguments arrived in and returns how
    /// many it wrote - the interpreter moves the stack pointer, nothing else changes shape.
    /// </summary>
    public sealed class NativeMultiReturnTests : IDisposable
    {
        private const string Root = "D:/proj/src";

        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
        }

        [Fact]
        public void ANativeFunction_ReturnsAnInlineTuple_ByWritingTwoSlots()
        {
            var emitter = Build(
                "public native fun divmod(a: int, b: int): (int, int);\n"
                    + "fun go(): int { let (q, r) = divmod(17, 5); return q * 10 + r; }");

            using var runtime = new SurtrRuntime();
            _owned.Add(runtime);
            runtime.DefineNativeBody("game.core.Test.divmod", SurtrNativeEntryPoint.FromDelegate(DivMod));
            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            Assert.Equal(32, Int(runtime, "go"));

            // The host boundary answers the same body with the boxed form.
            var divmod = Function(runtime, "game.core.Test", "divmod");
            var packed = runtime.Invoke(divmod, SurtrValue.CreateInt(9), SurtrValue.CreateInt(2));
            var tuple = runtime.Resolve<SurtrTuple>(packed)!;
            Assert.Equal(2, tuple.Length);
            Assert.Equal(4, tuple[0].AsInt);
            Assert.Equal(1, tuple[1].AsInt);
        }

        // A named static method: FromDelegate requires a non-capturing target. Two results,
        // written over the two arguments - the whole convention in four lines. Reading every
        // input before the first write is the one discipline in-place answering demands.
        private static int DivMod(SurtrCallArguments args)
        {
            int quotient = args.GetInt(0) / args.GetInt(1);
            int remainder = args.GetInt(0) % args.GetInt(1);

            args.WriteResultUnchecked(0, SurtrValue.CreateInt(quotient));
            args.WriteResultUnchecked(1, SurtrValue.CreateInt(remainder));
            return 2;
        }

        [Fact]
        public void ANativeFunction_WithNoArguments_WritesItsResultAboveTheEmptyBlock()
        {
            var emitter = Build(
                "public native fun answer(): (int, string);\n"
                    + "fun go(): string { let (n, label) = answer(); return n.toString() + label; }");

            using var runtime = new SurtrRuntime();
            _owned.Add(runtime);
            runtime.DefineNativeBody("game.core.Test.answer", SurtrNativeEntryPoint.FromDelegate(Answer));

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            Assert.Equal("42answer", Text(runtime, "go"));
        }

        private static int Answer(SurtrCallArguments args)
            => args.Return(
                SurtrValue.CreateInt(42),
                args.Runtime.NewStringValue("answer"));

        private ModuleEmitter Build(string source)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            var compilation = SurtrCompilation.Create(project);
            _owned.Add(compilation);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(!compilation.HasErrors);
            var emitter = new ModuleEmitter(compilation, binder);
            Assert.True(emitter.TryEmit());
            return emitter;
        }

        private static SurtrMethodInfo Function(SurtrRuntime runtime, string modulePath, string name)
        {
            Assert.True(runtime.TryGetModule(modulePath, out var module));
            Assert.True(module.TryGetMethods(name, out var overloads));
            return overloads[0];
        }

        private static int Int(SurtrRuntime runtime, string name)
            => runtime.Invoke(Function(runtime, "game.core.Test", name)).AsInt;

        private static string Text(SurtrRuntime runtime, string name)
            => runtime.Resolve<SurtrString>(runtime.Invoke(Function(runtime, "game.core.Test", name)))!.Text;
    }
}
