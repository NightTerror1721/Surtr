#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.Runtime
{
    /// <summary>
    /// Covers a module-level <c>native</c> member: it binds to a host body <em>by link name when
    /// the module loads</em>, rather than by an address baked into the bytecode when it was
    /// compiled - the same binding a class's own native member gets (§10). There is no separate
    /// host-global table anymore; a module-level native function is an ordinary method in the
    /// module's own method table, reached with <c>CallLocalModule</c>, whose body is supplied by
    /// <see cref="SurtrRuntime.DefineNativeBody"/> instead of a function pointer at declaration time.
    /// </summary>
    public class SurtrNativeImportTests
    {
        private static SurtrValue ReturnSeven(SurtrCallArguments arguments) => SurtrValue.CreateInt(7);

        private static SurtrNativeEntryPoint Seven() => SurtrNativeEntryPoint.FromDelegate(ReturnSeven);

        /// <summary>
        /// A module declaring one module-level native function under <paramref name="linkName"/>,
        /// callable through its own function "call".
        /// </summary>
        private static SurtrModule ModuleCalling(string linkName, string path = "test")
        {
            var builder = new SurtrModuleBuilder(path);
            var native = builder.DeclareNativeFunction(linkName, SurtrClassReference.Integer, linkName);

            var method = builder.DefineFunction("call", SurtrClassReference.Integer);
            method.Code.Call(native);
            method.Code.ReturnValue();

            return builder.Build();
        }

        #region Binding by name

        [Fact]
        public void ANativeFunction_BindsToTheHostBodyOfThatLinkName()
        {
            using var runtime = new SurtrRuntime();
            runtime.DefineNativeBody("seven", Seven());

            var module = ModuleCalling("seven");
            runtime.LoadModule(module);

            Assert.True(module.TryGetMethods("call", out var overloads));
            Assert.Equal(7, runtime.Invoke(overloads[0]).AsInt);
        }

        #endregion

        #region Failing at load rather than at the instruction

        [Fact]
        public void AFunctionTheHostNeverRegistered_FailsTheLoad()
        {
            using var runtime = new SurtrRuntime();

            var error = Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(ModuleCalling("missing")));

            Assert.Contains("missing", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AFailedLoad_LeavesTheModuleUnregistered()
        {
            using var runtime = new SurtrRuntime();
            var module = ModuleCalling("missing");

            Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(module));

            // The rollback matters: the path has to stay free for a corrected module to take.
            runtime.DefineNativeBody("missing", Seven());
            runtime.LoadModule(module);
        }

        #endregion

        #region A module belongs to the runtime that loaded it

        [Fact]
        public void LoadingOneModuleIntoTwoRuntimes_IsRejected()
        {
            using var first = new SurtrRuntime();
            using var second = new SurtrRuntime();

            first.DefineNativeBody("seven", Seven());
            second.DefineNativeBody("seven", Seven());

            var module = ModuleCalling("seven");
            first.LoadModule(module);

            // Its string literals carry references from the first runtime's heap and its native
            // member is bound to that runtime's registration, so the second would be reading
            // someone else's ids. Rejecting it turns a silent corruption into a clear failure.
            var error = Assert.Throws<InvalidOperationException>(() => second.LoadModule(module));
            Assert.Contains("already loaded", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TwoRuntimesPublishingDifferentBodies_BothRunTheirOwnSource()
        {
            using var first = new SurtrRuntime();
            using var second = new SurtrRuntime();

            // Binding is by link name, not by an index baked into the bytecode, so the very same
            // compiled module can resolve to a different body per runtime.
            first.DefineNativeBody("value", SurtrNativeEntryPoint.FromDelegate(ReturnOne));
            second.DefineNativeBody("value", SurtrNativeEntryPoint.FromDelegate(ReturnTwo));

            var firstModule = ModuleCalling("value");
            var secondModule = ModuleCalling("value");

            first.LoadModule(firstModule);
            second.LoadModule(secondModule);

            Assert.True(firstModule.TryGetMethods("call", out var firstCall));
            Assert.True(secondModule.TryGetMethods("call", out var secondCall));

            Assert.Equal(1, first.Invoke(firstCall[0]).AsInt);
            Assert.Equal(2, second.Invoke(secondCall[0]).AsInt);
        }

        private static SurtrValue ReturnOne(SurtrCallArguments arguments) => SurtrValue.CreateInt(1);
        private static SurtrValue ReturnTwo(SurtrCallArguments arguments) => SurtrValue.CreateInt(2);

        #endregion
    }
}
