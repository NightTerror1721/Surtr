#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.Runtime.Classes
{
    /// <summary>
    /// Covers the parameter-shape metadata overload resolution reads across a module boundary:
    /// defaults, varargs, and the three rules in <c>Language-Syntax.md</c> §3.5 that make a
    /// parameter list safe to walk once.
    /// </summary>
    public class SurtrParameterInfoTests
    {
        #region Test fixture helpers

        private static SurtrModule NewModule(string path = "test") => new(path);

        private static SurtrTypeHandle HandleFor(SurtrModule module, SurtrClassReference reference)
            => module.TypeHandles.GetOrAdd(reference);

        private static SurtrValue StubBody(SurtrCallArguments arguments) => SurtrValue.Null;

        private static SurtrNativeEntryPoint Stub() => SurtrNativeEntryPoint.FromDelegate(StubBody);

        private static SurtrMethodInfo Method(SurtrModule module, string name, params SurtrParameterInfo[] parameters)
            => new SurtrNativeMethodInfo(
                name,
                SurtrMethodDispatch.Direct,
                SurtrMethodRole.Normal,
                isOverride: false,
                HandleFor(module, SurtrClassReference.Void),
                parameters,
                isStatic: true,
                SurtrVisibility.Public,
                declaringType: null,
                Stub());

        #endregion

        #region A parameter carries its default and its varargs mark

        [Fact]
        public void RequiredParameter_HasNoDefaultAndIsNotVarargs()
        {
            var module = NewModule();
            var parameter = new SurtrParameterInfo("x", HandleFor(module, SurtrClassReference.Integer));

            Assert.False(parameter.HasDefault);
            Assert.False(parameter.IsVarargs);
            Assert.Equal(SurtrConstantKind.None, parameter.DefaultValue.Kind);
        }

        [Fact]
        public void DefaultedParameter_ReportsItsConstant()
        {
            var module = NewModule();
            var parameter = new SurtrParameterInfo("hp", HandleFor(module, SurtrClassReference.Integer), SurtrConstant.Integer(100));

            Assert.True(parameter.HasDefault);
            Assert.Equal(SurtrConstantKind.Integer, parameter.DefaultValue.Kind);
            Assert.Equal(100, parameter.DefaultValue.Value.AsInt);
        }

        [Fact]
        public void VarargsParameter_DeclaresItsElementType()
        {
            var module = NewModule();
            var parameter = new SurtrParameterInfo(
                "args",
                HandleFor(module, SurtrClassReference.String),
                SurtrConstant.None,
                isVarargs: true);

            Assert.True(parameter.IsVarargs);
            Assert.False(parameter.HasDefault);
            Assert.Equal("S", parameter.ParameterType.Reference.Descriptor);
        }

        [Fact]
        public void VarargsWithADefault_IsRejectedAtTheParameter()
        {
            var module = NewModule();

            Assert.Throws<ArgumentException>(() => new SurtrParameterInfo(
                "args",
                HandleFor(module, SurtrClassReference.String),
                SurtrConstant.String("x"),
                isVarargs: true));
        }

        #endregion

        #region Derived counts a call site resolves against

        [Fact]
        public void RequiredParameterCount_StopsAtTheFirstDefault()
        {
            var module = NewModule();
            var method = Method(
                module,
                "spawn",
                new SurtrParameterInfo("x", HandleFor(module, SurtrClassReference.Float)),
                new SurtrParameterInfo("y", HandleFor(module, SurtrClassReference.Float)),
                new SurtrParameterInfo("hp", HandleFor(module, SurtrClassReference.Integer), SurtrConstant.Integer(100)));

            Assert.Equal(3, method.ParameterCount);
            Assert.Equal(2, method.RequiredParameterCount);
            Assert.False(method.HasVarargs);
        }

        [Fact]
        public void RequiredParameterCount_StopsAtVarargsToo()
        {
            var module = NewModule();
            var method = Method(
                module,
                "format",
                new SurtrParameterInfo("pattern", HandleFor(module, SurtrClassReference.String)),
                new SurtrParameterInfo("args", HandleFor(module, SurtrClassReference.String), SurtrConstant.None, isVarargs: true));

            Assert.Equal(1, method.RequiredParameterCount);
            Assert.True(method.HasVarargs);
        }

        [Fact]
        public void AListWithNoOptionals_RequiresEveryParameter()
        {
            var module = NewModule();
            var method = Method(
                module,
                "log",
                new SurtrParameterInfo("message", HandleFor(module, SurtrClassReference.String)));

            Assert.Equal(1, method.RequiredParameterCount);
        }

        #endregion

        #region §3.5's shape rules are enforced where the member is declared

        [Fact]
        public void RequiredAfterDefaulted_IsRejected()
        {
            var module = NewModule();

            Assert.Throws<ArgumentException>(() => Method(
                module,
                "bad",
                new SurtrParameterInfo("a", HandleFor(module, SurtrClassReference.Integer), SurtrConstant.Integer(1)),
                new SurtrParameterInfo("b", HandleFor(module, SurtrClassReference.Integer))));
        }

        [Fact]
        public void VarargsThatIsNotLast_IsRejected()
        {
            var module = NewModule();

            Assert.Throws<ArgumentException>(() => Method(
                module,
                "bad",
                new SurtrParameterInfo("args", HandleFor(module, SurtrClassReference.String), SurtrConstant.None, isVarargs: true),
                new SurtrParameterInfo("tail", HandleFor(module, SurtrClassReference.Integer))));
        }

        [Fact]
        public void TwoVarargsParameters_AreRejected()
        {
            var module = NewModule();

            Assert.Throws<ArgumentException>(() => Method(
                module,
                "bad",
                new SurtrParameterInfo("a", HandleFor(module, SurtrClassReference.String), SurtrConstant.None, isVarargs: true),
                new SurtrParameterInfo("b", HandleFor(module, SurtrClassReference.String), SurtrConstant.None, isVarargs: true)));
        }

        [Fact]
        public void VarargsAfterADefault_IsRejectedBecauseNoPositionalCallCouldReachIt()
        {
            var module = NewModule();

            Assert.Throws<ArgumentException>(() => Method(
                module,
                "bad",
                new SurtrParameterInfo("a", HandleFor(module, SurtrClassReference.Integer), SurtrConstant.Integer(1)),
                new SurtrParameterInfo("rest", HandleFor(module, SurtrClassReference.String), SurtrConstant.None, isVarargs: true)));
        }

        [Fact]
        public void ANativeGlobalFunction_IsHeldToTheSameRules()
        {
            var module = NewModule();

            Assert.Throws<ArgumentException>(() => new SurtrNativeGlobalFunction(
                "bad",
                HandleFor(module, SurtrClassReference.Void),
                new[]
                {
                    new SurtrParameterInfo("a", HandleFor(module, SurtrClassReference.Integer), SurtrConstant.Integer(1)),
                    new SurtrParameterInfo("b", HandleFor(module, SurtrClassReference.Integer)),
                },
                Stub()));
        }

        #endregion

        #region Constants

        [Fact]
        public void EveryConstantShape_ReportsItsKindAndPayload()
        {
            Assert.Equal(7, SurtrConstant.Integer(7).Value.AsInt);
            Assert.Equal(1.5, SurtrConstant.Float(1.5).Value.AsFloat);
            Assert.True(SurtrConstant.Boolean(true).Value.AsBool);
            Assert.Equal('a', SurtrConstant.Character('a').Value.AsChar);
            Assert.Equal("hi", SurtrConstant.String("hi").Text);

            Assert.Equal(SurtrConstantKind.Null, SurtrConstant.Null.Kind);
            Assert.True(SurtrConstant.Null.HasValue);
            Assert.False(SurtrConstant.None.HasValue);
        }

        [Fact]
        public void AStringConstant_RefusesNullAndPointsAtTheNullLiteral()
        {
            Assert.Throws<ArgumentNullException>(() => SurtrConstant.String(null!));
        }

        [Fact]
        public void Materialize_InternsAStringAndPassesAPrimitiveThrough()
        {
            using var runtime = new SurtrRuntime();

            SurtrValue text = SurtrConstant.String("hello").Materialize(runtime);
            Assert.True(text.IsReference);

            // Interned, so the same text materializes to the same object on the same runtime.
            SurtrValue again = SurtrConstant.String("hello").Materialize(runtime);
            Assert.Equal(text.AsReference, again.AsReference);

            Assert.Equal(42, SurtrConstant.Integer(42).Materialize(runtime).AsInt);
        }

        [Fact]
        public void Materialize_OnNothing_Throws()
        {
            using var runtime = new SurtrRuntime();
            Assert.Throws<InvalidOperationException>(() => SurtrConstant.None.Materialize(runtime));
        }

        #endregion
    }
}
