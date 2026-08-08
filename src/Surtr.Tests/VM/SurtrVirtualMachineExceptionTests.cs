#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;
using System;

namespace Surtr.Tests.VM
{
    public class SurtrVirtualMachineExceptionTests
    {
        #region Throw + handler, same frame

        [Fact]
        public void Throw_CaughtByACatchAllHandler_InTheSameFrame()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();

            int tryStart = builder.Position;
            builder.Op(OpCode.PushI32).I32(7).Op(OpCode.BoxInt).Op(OpCode.Throw);
            int tryEnd = builder.Position;
            int handlerOffset = builder.Position;
            builder.Op(OpCode.Unbox).Op(OpCode.ReturnValue);

            var method = builder.Build(module, localCount: 0, maxStackSize: 8);
            method.SetExceptionHandlers(new[] { new SurtrExceptionHandler(tryStart, tryEnd, handlerOffset, catchType: null) });

            Assert.Equal(7, runtime.Invoke(method).AsInt);
        }

        [Fact]
        public void Throw_Uncaught_EscapesAsSurtrThrownException_CarryingTheRaisedObject()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(13).Op(OpCode.BoxInt).Op(OpCode.Throw)
                .Build(module, localCount: 0, maxStackSize: 4);

            var thrown = Assert.Throws<SurtrThrownException>(() => runtime.Invoke(method));

            var boxed = runtime.Resolve<SurtrBoxed>(SurtrValue.CreateReference(thrown.Reference));
            Assert.NotNull(boxed);
            Assert.Equal(13, boxed!.BoxedValue.AsInt);
        }

        #endregion

        #region Typed handlers

        private sealed class ErrorFixture
        {
            public SurtrModule Module { get; }
            public SurtrClass MyError { get; }
            public SurtrClass OtherError { get; }

            public ErrorFixture()
            {
                Module = new SurtrModule("test");
                MyError = VmMetadataHelpers.DefineClass(Module, "MyError");
                OtherError = VmMetadataHelpers.DefineClass(Module, "OtherError");
                SurtrTypeLinker.LinkModule(Module);
            }
        }

        [Fact]
        public void Throw_CaughtByAMatchingTypedHandler()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new ErrorFixture();
            var error = runtime.NewInstance(fixture.MyError);

            var builder = new BytecodeBuilder();
            int tryStart = builder.Position;
            builder.LoadReference(error).Op(OpCode.Throw);
            int tryEnd = builder.Position;
            int handlerOffset = builder.Position;
            builder.Op(OpCode.Pop).Op(OpCode.PushI32).I32(200).Op(OpCode.ReturnValue);

            var method = builder.Build(fixture.Module, localCount: 0, maxStackSize: 8);
            var catchType = VmMetadataHelpers.HandleFor(fixture.Module, fixture.MyError);
            method.SetExceptionHandlers(new[] { new SurtrExceptionHandler(tryStart, tryEnd, handlerOffset, catchType) });

            Assert.Equal(200, runtime.Invoke(method).AsInt);
        }

        [Fact]
        public void Throw_NotCaughtByAMismatchedTypedHandler_Escapes()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new ErrorFixture();
            var error = runtime.NewInstance(fixture.OtherError);

            var builder = new BytecodeBuilder();
            int tryStart = builder.Position;
            builder.LoadReference(error).Op(OpCode.Throw);
            int tryEnd = builder.Position;
            int handlerOffset = builder.Position;
            builder.Op(OpCode.Pop).Op(OpCode.PushI32).I32(200).Op(OpCode.ReturnValue);

            var method = builder.Build(fixture.Module, localCount: 0, maxStackSize: 8);
            var catchType = VmMetadataHelpers.HandleFor(fixture.Module, fixture.MyError); // does not match OtherError
            method.SetExceptionHandlers(new[] { new SurtrExceptionHandler(tryStart, tryEnd, handlerOffset, catchType) });

            Assert.Throws<SurtrThrownException>(() => runtime.Invoke(method));
        }

        [Fact]
        public void Throw_SkipsAMismatchedTypedHandler_AndFallsThroughToACatchAll()
        {
            // Handlers are searched in order: a type-specific handler ahead of a catch-all
            // covering the same range must be tried first, and skipped when it does not match.
            using var runtime = new SurtrRuntime();
            var fixture = new ErrorFixture();
            var error = runtime.NewInstance(fixture.OtherError);

            var builder = new BytecodeBuilder();
            int tryStart = builder.Position;
            builder.LoadReference(error).Op(OpCode.Throw);
            int tryEnd = builder.Position;

            int typedHandlerOffset = builder.Position;
            builder.Op(OpCode.Pop).Op(OpCode.PushI32).I32(111).Op(OpCode.ReturnValue);

            int catchAllOffset = builder.Position;
            builder.Op(OpCode.Pop).Op(OpCode.PushI32).I32(222).Op(OpCode.ReturnValue);

            var method = builder.Build(fixture.Module, localCount: 0, maxStackSize: 8);
            var mismatchedType = VmMetadataHelpers.HandleFor(fixture.Module, fixture.MyError);
            method.SetExceptionHandlers(new[]
            {
                new SurtrExceptionHandler(tryStart, tryEnd, typedHandlerOffset, mismatchedType),
                new SurtrExceptionHandler(tryStart, tryEnd, catchAllOffset, catchType: null),
            });

            Assert.Equal(222, runtime.Invoke(method).AsInt);
        }

        #endregion

        #region Cross-frame (call boundary)

        [Fact]
        public void Throw_UncaughtInTheRaisingFrame_IsCaughtByAnAncestorFramesHandler()
        {
            using var runtime = new SurtrRuntime();

            var innerModule = new SurtrModule("inner");
            var inner = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(9).Op(OpCode.BoxInt).Op(OpCode.Throw)
                .Build(innerModule, localCount: 0, maxStackSize: 4);

            var outerModule = new SurtrModule("outer");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(inner);

            int tryStart = builder.Position;
            builder.Op(OpCode.InvokeStatic).I16(methodIndex).U8(0).U8(1);
            int tryEnd = builder.Position;
            int handlerOffset = builder.Position;
            builder.Op(OpCode.Pop).Op(OpCode.PushI32).I32(300).Op(OpCode.ReturnValue);

            var outer = builder.Build(outerModule, localCount: 0, maxStackSize: 8);
            outer.SetExceptionHandlers(new[] { new SurtrExceptionHandler(tryStart, tryEnd, handlerOffset, catchType: null) });

            Assert.Equal(300, runtime.Invoke(outer).AsInt);
        }

        #endregion

        #region A VM trap, wrapped and caught like any other exception

        [Fact]
        public void ATrap_IsWrappedAsANativeObject_AndCanBeCaught()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();

            int tryStart = builder.Position;
            builder.Op(OpCode.PushI32).I32(1).Op(OpCode.PushI32).I32(0).Op(OpCode.Div); // traps: division by zero
            int tryEnd = builder.Position;
            int handlerOffset = builder.Position;
            builder.Op(OpCode.ReturnValue); // returns the wrapped exception reference itself

            var method = builder.Build(module, localCount: 0, maxStackSize: 8);
            method.SetExceptionHandlers(new[] { new SurtrExceptionHandler(tryStart, tryEnd, handlerOffset, catchType: null) });

            var result = runtime.Invoke(method);
            var proxy = runtime.Resolve<SurtrNativeProxy>(result);

            Assert.NotNull(proxy);
            Assert.IsType<SurtrExecutionException>(proxy!.Target);
        }

        #endregion

        #region Reentrancy

        // A plain static field, not a closure capture: SurtrNativeEntryPoint.FromDelegate needs a
        // non-capturing static method, and reading a static field does not count as capturing.
        private static SurtrBytecodeMethodInfo? _reentrantTarget;

        private static SurtrValue ReentrantBody(SurtrCallArguments arguments)
        {
            SurtrValue innerResult = arguments.Runtime.Invoke(_reentrantTarget!);
            return SurtrValue.CreateInt(innerResult.AsInt + 1);
        }

        [Fact]
        public void ANativeFunction_CanReenterTheVm_AndTheOuterCallResumesCorrectly()
        {
            using var runtime = new SurtrRuntime();

            var innerModule = new SurtrModule("inner");
            _reentrantTarget = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(41).Op(OpCode.ReturnValue)
                .Build(innerModule, localCount: 0, maxStackSize: 4);

            var function = runtime.DefineGlobalFunction(
                "reenter", SurtrClassReference.Integer, Array.Empty<SurtrParameterInfo>(),
                SurtrNativeEntryPoint.FromDelegate(ReentrantBody));

            var outerModule = new SurtrModule("outer");
            var outer = new BytecodeBuilder()
                .Op(OpCode.CallGlobalNative).I16(function.Index).U8(0).U8(1) // -> reenters, returns 41 + 1 = 42
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.Add)                                              // proves the outer frame resumed: 42 + 1
                .Op(OpCode.ReturnValue)
                .Build(outerModule, localCount: 0, maxStackSize: 8);

            Assert.Equal(43, runtime.Invoke(outer).AsInt);
        }

        #endregion

        #region Recovery

        [Fact]
        public void ResetExecution_RecoversTheRuntimeAfterAnUncaughtException()
        {
            using var runtime = new SurtrRuntime();

            var throwingModule = new SurtrModule("throwing");
            var throwing = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(1).Op(OpCode.BoxInt).Op(OpCode.Throw)
                .Build(throwingModule, localCount: 0, maxStackSize: 4);

            Assert.Throws<SurtrThrownException>(() => runtime.Invoke(throwing));

            runtime.ResetExecution();

            var okModule = new SurtrModule("ok");
            var ok = new BytecodeBuilder().Op(OpCode.PushI32).I32(5).Op(OpCode.ReturnValue)
                .Build(okModule, localCount: 0, maxStackSize: 4);

            Assert.Equal(5, runtime.Invoke(ok).AsInt);
        }

        #endregion
    }
}
