#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.Runtime.Objects
{
    public class SurtrClosureTests
    {
        private static SurtrValue StubBody(SurtrCallArguments arguments) => SurtrValue.CreateInt(1);

        private static SurtrNativeMethodInfo NativeMethod(SurtrModule module, int parameterCount)
        {
            var parameters = new SurtrParameterInfo[parameterCount];
            for (int i = 0; i < parameterCount; i++)
                parameters[i] = new SurtrParameterInfo($"p{i}", module.TypeHandles.GetOrAdd(SurtrClassReference.Integer));

            return new SurtrNativeMethodInfo(
                "stub",
                SurtrMethodDispatch.Direct,
                SurtrMethodRole.Normal,
                isOverride: false,
                module.TypeHandles.GetOrAdd(SurtrClassReference.Integer),
                parameters,
                isStatic: true,
                SurtrVisibility.Public,
                declaringType: null,
                SurtrNativeEntryPoint.FromDelegate(StubBody));
        }

        private static SurtrAbstractMethodInfo AbstractMethod(SurtrModule module)
            => new(
                "stub",
                module.TypeHandles.GetOrAdd(SurtrClassReference.Void),
                Array.Empty<SurtrParameterInfo>(),
                SurtrVisibility.Public,
                declaringType: null);

        [Fact]
        public void Arity_MatchesTheMethodsParameterCount()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = NativeMethod(module, parameterCount: 2);

            var closure = runtime.NewClosure(method);

            Assert.Equal(2, closure.Arity);
        }

        [Fact]
        public void UpValueCount_MatchesTheCapturedArray()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = NativeMethod(module, parameterCount: 0);

            var closure = runtime.NewClosure(method, new[] { SurtrValue.CreateInt(1), SurtrValue.CreateInt(2), SurtrValue.CreateInt(3) });

            Assert.Equal(3, closure.UpValueCount);
        }

        [Fact]
        public void GetUpValue_ReturnsTheCapturedValueAtThatIndex()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = NativeMethod(module, parameterCount: 0);

            var closure = runtime.NewClosure(method, new[] { SurtrValue.CreateInt(7) });

            Assert.Equal(7, closure.GetUpValue(0).AsInt);
        }

        [Fact]
        public void OverANativeMethod_IsNativeIsTrue()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = NativeMethod(module, parameterCount: 0);

            var closure = runtime.NewClosure(method);

            Assert.True(closure.IsNative);
        }

        [Fact]
        public void OverAnAbstractMethod_Throws()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = AbstractMethod(module);

            Assert.Throws<ArgumentException>(() => runtime.NewClosure(method));
        }

        [Fact]
        public void GetOrCreateFunctionValue_ReturnsTheSameClosureForTheSameMethod()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = NativeMethod(module, parameterCount: 0);

            var first = runtime.GetOrCreateFunctionValue(method);
            var second = runtime.GetOrCreateFunctionValue(method);

            Assert.Same(first, second);
            Assert.Equal(0, first.UpValueCount);
        }

        [Fact]
        public void GetOrCreateFunctionValue_DistinguishesMethodsByIdentity()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var first = NativeMethod(module, parameterCount: 0);
            var second = NativeMethod(module, parameterCount: 0);

            Assert.NotSame(runtime.GetOrCreateFunctionValue(first), runtime.GetOrCreateFunctionValue(second));
        }

        [Fact]
        public void NewClosure_WithNoUpValues_ReturnsTheCanonicalFunctionValue()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = NativeMethod(module, parameterCount: 0);

            // A closure with nothing to capture is a function: the host-created form and the
            // language's zero-capture lambda resolve to the same shared value.
            Assert.Same(runtime.GetOrCreateFunctionValue(method), runtime.NewClosure(method));
        }

        [Fact]
        public void NewClosure_WithUpValues_IsStillAFreshObject()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = NativeMethod(module, parameterCount: 0);

            var capture = runtime.NewClosure(method, new[] { SurtrValue.CreateInt(1) });

            Assert.Equal(1, capture.UpValueCount);
            Assert.NotSame(runtime.GetOrCreateFunctionValue(method), capture);
        }

        [Fact]
        public void VisitReferences_KeepsOnlyReferenceTypedUpValuesAlive()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = NativeMethod(module, parameterCount: 0);

            var captured = runtime.NewString("captured");
            SurtrValue capturedRef = SurtrValue.CreateReference(captured.GetSurtrReference());

            // Mixes a primitive upvalue with a reference upvalue - the primitive must not
            // confuse the marker (SurtrEntityMarker.Mark(SurtrValue) is a no-op for non-references).
            var closure = runtime.NewClosure(method, new[] { SurtrValue.CreateInt(42), runtime.ValueOf(captured) });

            runtime.AddRoot(closure);
            runtime.Collect();

            Assert.NotNull(runtime.Resolve<SurtrString>(capturedRef));
        }
    }
}
