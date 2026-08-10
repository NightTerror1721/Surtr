#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.Runtime
{
    /// <summary>
    /// Covers the per-module native import table: a <c>native</c> declaration binds to a host
    /// global <em>by name when the module loads</em>, rather than by an index baked into the
    /// bytecode when it was compiled.
    /// </summary>
    public class SurtrNativeImportTests
    {
        private static SurtrValue ReturnSeven(SurtrCallArguments arguments) => SurtrValue.CreateInt(7);

        private static SurtrNativeEntryPoint Seven() => SurtrNativeEntryPoint.FromDelegate(ReturnSeven);

        /// <summary>A module whose one function reads a native variable and returns it.</summary>
        private static SurtrModule ModuleReading(string globalName, string path = "test")
        {
            var builder = new SurtrModuleBuilder(path);
            var method = builder.DefineFunction("read", SurtrClassReference.Integer);

            method.Code.LoadGlobal(globalName);
            method.Code.ReturnValue();

            return builder.Build();
        }

        /// <summary>A module whose one function calls a native function and returns its result.</summary>
        private static SurtrModule ModuleCalling(string functionName, string path = "test")
        {
            var builder = new SurtrModuleBuilder(path);
            var method = builder.DefineFunction("call", SurtrClassReference.Integer);

            method.Code.CallGlobal(functionName, 0, 1);
            method.Code.ReturnValue();

            return builder.Build();
        }

        #region Binding by name

        [Fact]
        public void ANativeVariable_BindsToTheHostGlobalOfThatName()
        {
            using var runtime = new SurtrRuntime();
            var global = runtime.DefineGlobal("counter", SurtrClassReference.Integer);
            runtime.Globals.SetValue(global, SurtrValue.CreateInt(11));

            var module = ModuleReading("counter");
            runtime.LoadModule(module);

            Assert.True(module.TryGetMethods("read", out var overloads));
            Assert.Equal(11, runtime.Invoke(overloads[0]).AsInt);
        }

        [Fact]
        public void ANativeFunction_BindsToTheHostFunctionOfThatName()
        {
            using var runtime = new SurtrRuntime();
            runtime.DefineGlobalFunction(
                "seven", SurtrClassReference.Integer, Array.Empty<SurtrParameterInfo>(), Seven());

            var module = ModuleCalling("seven");
            runtime.LoadModule(module);

            Assert.True(module.TryGetMethods("call", out var overloads));
            Assert.Equal(7, runtime.Invoke(overloads[0]).AsInt);
        }

        [Fact]
        public void TheSameImportDeclaredTwice_IsOneSlot()
        {
            var builder = new SurtrModuleBuilder("test");

            Assert.Equal(builder.NativeVariable("shared").Index, builder.NativeVariable("shared").Index);
            Assert.NotEqual(builder.NativeVariable("shared").Index, builder.NativeVariable("other").Index);
        }

        #endregion

        #region Failing at load rather than at the instruction

        [Fact]
        public void AVariableTheHostNeverRegistered_FailsTheLoad()
        {
            using var runtime = new SurtrRuntime();

            var error = Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(ModuleReading("missing")));

            Assert.Contains("missing", error.Message, StringComparison.Ordinal);
            Assert.Contains("native variable", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AFunctionTheHostNeverRegistered_FailsTheLoad()
        {
            using var runtime = new SurtrRuntime();

            var error = Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(ModuleCalling("missing")));

            Assert.Contains("missing", error.Message, StringComparison.Ordinal);
            Assert.Contains("native function", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AFailedLoad_LeavesTheModuleUnregistered()
        {
            using var runtime = new SurtrRuntime();

            Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(ModuleReading("missing")));

            // The rollback matters: the path has to stay free for a corrected module to take.
            runtime.DefineGlobal("missing", SurtrClassReference.Integer);
            runtime.LoadModule(ModuleReading("missing"));
        }

        #endregion

        #region A module belongs to the runtime that loaded it

        [Fact]
        public void LoadingOneModuleIntoTwoRuntimes_IsRejected()
        {
            using var first = new SurtrRuntime();
            using var second = new SurtrRuntime();

            first.DefineGlobal("counter", SurtrClassReference.Integer);
            second.DefineGlobal("counter", SurtrClassReference.Integer);

            var module = ModuleReading("counter");
            first.LoadModule(module);

            // Its string literals carry references from the first runtime's heap and its imports
            // are bound to that runtime's globals, so the second would be reading someone else's
            // ids. Rejecting it turns a silent corruption into a clear failure.
            var error = Assert.Throws<InvalidOperationException>(() => second.LoadModule(module));
            Assert.Contains("already loaded", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TwoRuntimesThatNumberTheirGlobalsDifferently_BothRunTheSameSource()
        {
            using var first = new SurtrRuntime();
            using var second = new SurtrRuntime();

            // Registration order differs, so "counter" is index 0 in one runtime and 1 in the
            // other. Binding by name is what makes the compiled module indifferent to that.
            var firstCounter = first.DefineGlobal("counter", SurtrClassReference.Integer);
            first.Globals.SetValue(firstCounter, SurtrValue.CreateInt(1));

            second.DefineGlobal("padding", SurtrClassReference.Integer);
            var secondCounter = second.DefineGlobal("counter", SurtrClassReference.Integer);
            second.Globals.SetValue(secondCounter, SurtrValue.CreateInt(2));

            Assert.NotEqual(firstCounter.Index, secondCounter.Index);

            var firstModule = ModuleReading("counter");
            var secondModule = ModuleReading("counter");

            first.LoadModule(firstModule);
            second.LoadModule(secondModule);

            Assert.True(firstModule.TryGetMethods("read", out var firstRead));
            Assert.True(secondModule.TryGetMethods("read", out var secondRead));

            Assert.Equal(1, first.Invoke(firstRead[0]).AsInt);
            Assert.Equal(2, second.Invoke(secondRead[0]).AsInt);
        }

        #endregion
    }
}
