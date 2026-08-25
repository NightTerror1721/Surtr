#nullable enable

using Surtr.Bytecode;
using Surtr.Bytecode.Emit;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.Bytecode.Emit
{
    /// <summary>
    /// End-to-end coverage for the emitter: everything here builds a module through
    /// <see cref="SurtrModuleBuilder"/>, loads it into a real runtime and runs it, so a test only
    /// passes if the emitted bytes, the metadata built around them and the linker all agree.
    /// </summary>
    public class SurtrModuleBuilderTests
    {
        private static SurtrParameterInfo[] IntParameters(SurtrModuleBuilder builder, params string[] names)
        {
            var parameters = new SurtrParameterInfo[names.Length];
            for (int i = 0; i < names.Length; i++)
                parameters[i] = new SurtrParameterInfo(names[i], builder.TypeHandle(SurtrClassReference.Integer));

            return parameters;
        }

        #region Module-level functions

        [Fact]
        public void AFunction_RunsThroughTheRuntime()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("test");
            var add = builder.DefineFunction("add", SurtrClassReference.Integer);

            add.Code
                .LoadInt(1)
                .LoadInt(2)
                .Add(SurtrValueTypeCode.Integer)
                .ReturnValue();

            runtime.LoadModule(builder.Build());

            Assert.Equal(3, runtime.Invoke(add.Built!).AsInt);
        }

        [Fact]
        public void ParametersAndLocals_AreNumberedFromTheFrameBase()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("test");
            var scale = builder.DefineFunction("scale", SurtrClassReference.Integer, IntParameters(builder, "n"));

            var doubled = scale.DeclareLocal("doubled");

            scale.Code
                .LoadLocal(scale.Parameter(0))
                .LoadInt(2)
                .Multiply(SurtrValueTypeCode.Integer)
                .StoreLocal(doubled)
                .LoadLocal(doubled)
                .ReturnValue();

            runtime.LoadModule(builder.Build());

            // One parameter and one declared local, and a module function has no receiver.
            Assert.Equal(0, scale.Parameter(0).Index);
            Assert.Equal(1, doubled.Index);
            Assert.Equal(2, scale.LocalCount);

            Assert.Equal(14, runtime.Invoke(scale.Built!, SurtrValue.CreateInt(7)).AsInt);
        }

        [Fact]
        public void MaxStackSize_IsComputedFromTheEmittedInstructions()
        {
            var builder = new SurtrModuleBuilder("test");
            var deep = builder.DefineFunction("deep", SurtrClassReference.Integer);

            deep.Code
                .LoadInt(1)
                .LoadInt(2)
                .LoadInt(3)
                .Add(SurtrValueTypeCode.Integer)
                .Add(SurtrValueTypeCode.Integer)
                .ReturnValue();

            Assert.Equal(3, deep.Code.MaxStackDepth);

            builder.Build();
            Assert.Equal(3, deep.Built!.MaxStackSize);
        }

        [Fact]
        public void ALoopWithABackwardJump_Terminates()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("test");
            var sum = builder.DefineFunction("sum", SurtrClassReference.Integer);

            var index = sum.DeclareLocal("i");
            var total = sum.DeclareLocal("total");
            var loop = sum.Code.NewLabel();
            var done = sum.Code.NewLabel();

            sum.Code
                .LoadInt(0).StoreLocal(total)
                .LoadInt(1).StoreLocal(index)
                .MarkLabel(loop)
                .LoadLocal(index).LoadInt(5)
                .JumpIfCompare(SurtrComparison.Greater, SurtrValueTypeCode.Integer, done)
                .LoadLocal(total).LoadLocal(index).Add(SurtrValueTypeCode.Integer).StoreLocal(total)
                .LoadLocal(index).LoadInt(1).Add(SurtrValueTypeCode.Integer).StoreLocal(index)
                .Jump(loop)
                .MarkLabel(done)
                .LoadLocal(total)
                .ReturnValue();

            runtime.LoadModule(builder.Build());

            Assert.Equal(15, runtime.Invoke(sum.Built!).AsInt);
        }

        #endregion

        #region Constants and literals

        [Fact]
        public void TheConstantPool_DeduplicatesByEncodedBits()
        {
            var builder = new SurtrModuleBuilder("test");

            Assert.Equal(builder.ConstantFloat(1.5).Index, builder.ConstantFloat(1.5).Index);
            Assert.Equal(builder.StringLiteral("hello").Index, builder.StringLiteral("hello").Index);

            // Positive and negative zero encode differently, so they are different constants -
            // which is exactly what the runtime's own float comparison assumes.
            Assert.NotEqual(builder.ConstantFloat(0.0).Index, builder.ConstantFloat(-0.0).Index);
        }

        [Fact]
        public void StringLiterals_AreInternedIntoThePoolAtLoad()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("test");
            var concat = builder.DefineFunction("concat", SurtrClassReference.Boolean);

            concat.Code
                .LoadString("ab")
                .LoadString("a")
                .LoadString("b")
                .StrCat(2)
                .StrEQ()
                .ReturnValue();

            runtime.LoadModule(builder.Build());

            Assert.True(runtime.Invoke(concat.Built!).AsBool);
        }

        [Fact]
        public void SmallIntegers_AreCarriedInlineRatherThanInThePool()
        {
            var builder = new SurtrModuleBuilder("test");
            var f = builder.DefineFunction("f", SurtrClassReference.Integer);

            f.Code.LoadInt(7).ReturnValue();

            var module = builder.Build();

            Assert.Equal(0, module.Chunk.Constants.Length);
            Assert.Equal(OpCode.PushI8, (OpCode)module.Chunk.Code[f.Built!.CodeOffset]);
        }

        #endregion

        #region Module variables

        [Fact]
        public void AModuleVariable_IsWrittenByTheStaticInitializerAndReadBack()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("test");
            var counter = builder.DefineVariable("counter", SurtrClassReference.Integer);

            var initializer = builder.DefineStaticInitializer();
            initializer.Code
                .LoadInt(42)
                .StoreStaticField(builder.Field(counter))
                .ReturnVoid();

            var read = builder.DefineFunction("read", SurtrClassReference.Integer);
            read.Code
                .LoadStaticField(builder.Field(counter))
                .ReturnValue();

            runtime.LoadModule(builder.Build());

            Assert.Equal(42, runtime.Invoke(read.Built!).AsInt);
        }

        #endregion

        #region Classes

        [Fact]
        public void AClassHierarchy_DispatchesThroughTheVirtualMethodTable()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("test");

            var baseClass = builder.DefineClass("Base");
            var value = baseClass.DefineField("value", SurtrClassReference.Integer);

            var baseConstructor = baseClass.DefineConstructor(IntParameters(builder, "v"));
            baseConstructor.Code
                .LoadLocal(baseConstructor.Receiver)
                .LoadLocal(baseConstructor.Parameter(0))
                .StoreField(builder.Field(value))
                .ReturnVoid();

            var read = baseClass.DefineMethod(
                "read", SurtrClassReference.Integer, dispatch: SurtrMethodDispatch.Virtual);

            read.Code
                .LoadLocal(read.Receiver)
                .LoadField(builder.Field(value))
                .ReturnValue();

            var derived = builder.DefineClass("Derived", baseClass.SelfReference);

            var derivedConstructor = derived.DefineConstructor(IntParameters(builder, "v"));
            derivedConstructor.Code
                .LoadLocal(derivedConstructor.Receiver)
                .LoadLocal(derivedConstructor.Parameter(0))
                .Call(baseConstructor)
                .ReturnVoid();

            var overridden = derived.DefineMethod(
                "read", SurtrClassReference.Integer, dispatch: SurtrMethodDispatch.Virtual, isOverride: true);

            overridden.Code
                .LoadLocal(overridden.Receiver)
                .LoadField(builder.Field(value))
                .LoadInt(100)
                .Add(SurtrValueTypeCode.Integer)
                .ReturnValue();

            var run = builder.DefineFunction("run", SurtrClassReference.Integer);
            run.Code
                .NewObject(derived.SelfReference)
                .Dup()
                .LoadInt(5)
                .Call(derivedConstructor)
                .Call(read)
                .ReturnValue();

            runtime.LoadModule(builder.Build());

            // Called against the base's declaration, resolved onto the override.
            Assert.Equal(105, runtime.Invoke(run.Built!).AsInt);
        }

        [Fact]
        public void AnInterfaceCall_ResolvesThroughTheImplementingClass()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("test");

            var shape = builder.DefineInterface("IShape");
            var area = shape.DefineMethod("area", SurtrClassReference.Integer);

            var square = builder.DefineClass("Square");
            square.Implements(shape.SelfReference);

            var constructor = square.DefineConstructor();
            constructor.Code.ReturnVoid();

            var implementation = square.DefineMethod(
                "area", SurtrClassReference.Integer, dispatch: SurtrMethodDispatch.Virtual);

            implementation.Code.LoadInt(64).ReturnValue();

            var run = builder.DefineFunction("run", SurtrClassReference.Integer);
            run.Code
                .NewObject(square.SelfReference)
                .Dup()
                .Call(constructor)
                .Call(area)
                .ReturnValue();

            runtime.LoadModule(builder.Build());

            Assert.Equal(64, runtime.Invoke(run.Built!).AsInt);
        }

        [Fact]
        public void APropertyDeclaresItsAccessorsAsMethods()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("test");
            var holder = builder.DefineClass("Holder");
            var backing = holder.DefineField("backing", SurtrClassReference.Integer);

            var constructor = holder.DefineConstructor();
            constructor.Code
                .LoadLocal(constructor.Receiver)
                .LoadInt(9)
                .StoreField(builder.Field(backing))
                .ReturnVoid();

            var property = holder.DefineProperty("size", SurtrClassReference.Integer);
            var getter = property.DefineGetter();
            getter.Code
                .LoadLocal(getter.Receiver)
                .LoadField(builder.Field(backing))
                .ReturnValue();

            var run = builder.DefineFunction("run", SurtrClassReference.Integer);
            run.Code
                .NewObject(holder.SelfReference)
                .Dup()
                .Call(constructor)
                .Call(getter)
                .ReturnValue();

            runtime.LoadModule(builder.Build());

            Assert.True(holder.Class.TryGetMethods("get_size", out _));
            Assert.True(holder.Class.TryGetProperty("size", out var declared));
            Assert.Same(getter.Built, declared.Getter);
            Assert.Equal(9, runtime.Invoke(run.Built!).AsInt);
        }

        #endregion

        #region Cross-module calls

        [Fact]
        public void ACrossModuleCall_ReachesTheCalleesOwnMethodTable()
        {
            using var runtime = new SurtrRuntime();

            var libraryBuilder = new SurtrModuleBuilder("lib");
            var five = libraryBuilder.DefineFunction("five", SurtrClassReference.Integer);
            five.Code.LoadInt(5).ReturnValue();

            var library = libraryBuilder.Build();
            runtime.LoadModule(library);

            var applicationBuilder = new SurtrModuleBuilder("app");
            var main = applicationBuilder.DefineFunction("main", SurtrClassReference.Integer);
            main.Code
                .CallExternal(library, five.Built!)
                .LoadInt(1)
                .Add(SurtrValueTypeCode.Integer)
                .ReturnValue();

            runtime.LoadModule(applicationBuilder.Build());

            Assert.Equal(6, runtime.Invoke(main.Built!).AsInt);
        }

        #endregion

        #region Switches and closures

        [Theory]
        [InlineData(0, 10)]
        [InlineData(1, 20)]
        [InlineData(2, 30)]
        [InlineData(9, -1)]
        public void SwitchOn_PicksTheDenseTableForContiguousCases(int input, int expected)
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("test");
            var pick = builder.DefineFunction("pick", SurtrClassReference.Integer, IntParameters(builder, "n"));

            var first = pick.Code.NewLabel();
            var second = pick.Code.NewLabel();
            var third = pick.Code.NewLabel();
            var fallback = pick.Code.NewLabel();

            pick.Code
                .LoadLocal(pick.Parameter(0))
                .SwitchOn(
                    new[]
                    {
                        new SurtrSwitchCase(2, third),
                        new SurtrSwitchCase(0, first),
                        new SurtrSwitchCase(1, second),
                    },
                    fallback)
                .MarkLabel(first).LoadInt(10).ReturnValue()
                .MarkLabel(second).LoadInt(20).ReturnValue()
                .MarkLabel(third).LoadInt(30).ReturnValue()
                .MarkLabel(fallback).LoadInt(-1).ReturnValue();

            var module = builder.Build();
            runtime.LoadModule(module);

            Assert.Equal(OpCode.Switch, (OpCode)module.Chunk.Code[pick.Built!.CodeOffset + 1]);
            Assert.Equal(expected, runtime.Invoke(pick.Built!, SurtrValue.CreateInt(input)).AsInt);
        }

        [Fact]
        public void AClosure_ReadsItsCapturedValue()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("test");

            var body = builder.DefineFunction("body", SurtrClassReference.Integer);
            body.Code
                .UpValueGet(0)
                .LoadInt(1)
                .Add(SurtrValueTypeCode.Integer)
                .ReturnValue();

            var run = builder.DefineFunction("run", SurtrClassReference.Integer);
            run.Code
                .LoadInt(41)
                .NewClosureFor(body, 1)
                .CallClosure(0, hasResult: true)
                .ReturnValue();

            runtime.LoadModule(builder.Build());

            Assert.Equal(42, runtime.Invoke(run.Built!).AsInt);
        }

        #endregion

        #region Exception handlers

        [Fact]
        public void ACatchAll_TakesTheOffsetsIntoTheChunksOwnAddressSpace()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("test");

            // Emitted first so the guarded method does not start at offset zero: a handler table
            // built with method-relative offsets would still pass if it did.
            var filler = builder.DefineFunction("filler", SurtrClassReference.Integer);
            filler.Code.LoadInt(0).ReturnValue();

            var guarded = builder.DefineFunction("guarded", SurtrClassReference.Integer);
            var handler = guarded.Code.NewLabel();

            var region = guarded.BeginTry();
            guarded.Code
                .LoadInt(1)
                .LoadInt(0)
                .Divide(SurtrValueTypeCode.Integer)
                .ReturnValue();
            guarded.EndTry(region);

            guarded.AddCatchAll(region, handler);
            guarded.Code
                .MarkHandler(handler)
                .Pop()
                .LoadInt(-1)
                .ReturnValue();

            runtime.LoadModule(builder.Build());

            var handlers = guarded.Built!.Handlers;
            Assert.Single(handlers);
            Assert.True(handlers[0].TryStart >= guarded.Built!.CodeOffset);
            Assert.Equal(-1, runtime.Invoke(guarded.Built!).AsInt);
        }

        [Fact]
        public void NestedRegions_AreOrderedInnermostFirst()
        {
            var builder = new SurtrModuleBuilder("test");
            var method = builder.DefineFunction("nested", SurtrClassReference.Void);

            var outerHandler = method.Code.NewLabel();
            var innerHandler = method.Code.NewLabel();

            var outer = method.BeginTry();
            method.Code.Nop();

            var inner = method.BeginTry();
            method.Code.Nop();
            method.EndTry(inner);

            method.EndTry(outer);

            // Declared outer-first on purpose: the builder is what puts them in search order.
            method.AddCatchAll(outer, outerHandler);
            method.AddCatchAll(inner, innerHandler);

            method.Code.MarkHandler(outerHandler).Pop().ReturnVoid();
            method.Code.MarkHandler(innerHandler).Pop().ReturnVoid();

            builder.Build();

            var handlers = method.Built!.Handlers;
            Assert.Equal(2, handlers.Length);
            Assert.True(handlers[0].TryEnd - handlers[0].TryStart < handlers[1].TryEnd - handlers[1].TryStart);
        }

        #endregion

        #region Jump widths

        [Fact]
        public void AnAutoJumpTooFarForTwoBytes_IsWidenedAndStillLands()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("test");
            var far = builder.DefineFunction("far", SurtrClassReference.Integer);

            var end = far.Code.NewLabel();
            far.Code.Jump(end);

            for (int i = 0; i < 40000; i++)
                far.Code.Nop();

            far.Code.MarkLabel(end).LoadInt(7).ReturnValue();

            var module = builder.Build();
            runtime.LoadModule(module);

            Assert.Equal(OpCode.JPX, (OpCode)module.Chunk.Code[far.Built!.CodeOffset]);
            Assert.Equal(7, runtime.Invoke(far.Built!).AsInt);
        }

        [Fact]
        public void AShortJumpPinnedByTheCaller_FailsRatherThanWidening()
        {
            var builder = new SurtrModuleBuilder("test");
            var far = builder.DefineFunction("far", SurtrClassReference.Integer);

            var end = far.Code.NewLabel();
            far.Code.JP(end);

            for (int i = 0; i < 40000; i++)
                far.Code.Nop();

            far.Code.MarkLabel(end).LoadInt(7).ReturnValue();

            var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Contains("cannot reach its target", error.Message);
        }

        [Fact]
        public void ANearAutoJump_KeepsTheTwoByteForm()
        {
            var builder = new SurtrModuleBuilder("test");
            var near = builder.DefineFunction("near", SurtrClassReference.Integer);

            var end = near.Code.NewLabel();
            near.Code.Jump(end).Nop().MarkLabel(end).LoadInt(1).ReturnValue();

            var module = builder.Build();

            Assert.Equal(OpCode.JP, (OpCode)module.Chunk.Code[near.Built!.CodeOffset]);
        }

        [Fact]
        public void AnAutoAbsentBranchTooFarForTwoBytes_WidensToJPAXAndStillLands()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("test");
            var far = builder.DefineFunction("far", SurtrClassReference.Integer);

            // An absent int always takes the branch, so reaching the end at all is the proof that
            // the widened form both encoded and landed.
            var end = far.Code.NewLabel();
            far.Code.PushAbsent(SurtrValueTypeCode.Integer).JPA(end);

            for (int i = 0; i < 40000; i++)
                far.Code.Nop();

            far.Code.MarkLabel(end).LoadInt(7).ReturnValue();

            var module = builder.Build();
            runtime.LoadModule(module);

            Assert.Equal(OpCode.JPAX, (OpCode)module.Chunk.Code[far.Built!.CodeOffset + 2]);
            Assert.Equal(7, runtime.Invoke(far.Built!).AsInt);
        }

        [Fact]
        public void AShortAbsentBranch_KeepsTheTwoByteForm()
        {
            var builder = new SurtrModuleBuilder("test");
            var near = builder.DefineFunction("near", SurtrClassReference.Integer);

            var end = near.Code.NewLabel();
            near.Code.PushAbsent(SurtrValueTypeCode.Integer).JPNA(end)
                .Nop().MarkLabel(end).LoadInt(1).ReturnValue();

            var module = builder.Build();

            Assert.Equal(OpCode.JPNA, (OpCode)module.Chunk.Code[near.Built!.CodeOffset + 2]);
        }

        #endregion

        #region Emitter diagnostics

        [Fact]
        public void PathsReachingALabelAtDifferentDepths_AreRejected()
        {
            var builder = new SurtrModuleBuilder("test");
            var method = builder.DefineFunction("bad", SurtrClassReference.Void);

            var join = method.Code.NewLabel();

            method.Code.LoadBool(true).JumpIfTrue(join);
            method.Code.LoadInt(1);

            var error = Assert.Throws<InvalidOperationException>(() => method.Code.MarkLabel(join));
            Assert.Contains("disagree on operand stack depth", error.Message);
        }

        [Fact]
        public void PoppingMoreThanTheStackHolds_IsRejected()
        {
            var builder = new SurtrModuleBuilder("test");
            var method = builder.DefineFunction("bad", SurtrClassReference.Void);

            var error = Assert.Throws<InvalidOperationException>(() => method.Code.Add(SurtrValueTypeCode.Integer));
            Assert.Contains("underflow", error.Message);
        }

        [Fact]
        public void ABranchToAnUnmarkedLabel_IsRejectedAtBuild()
        {
            var builder = new SurtrModuleBuilder("test");
            var method = builder.DefineFunction("bad", SurtrClassReference.Void);

            method.Code.Jump(method.Code.NewLabel());

            var error = Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Contains("never marked", error.Message);
        }

        [Fact]
        public void OrderingComparisonsOnReferences_AreRejected()
        {
            var builder = new SurtrModuleBuilder("test");
            var method = builder.DefineFunction("bad", SurtrClassReference.Void);

            method.Code.LoadNull().LoadNull();

            Assert.Throws<ArgumentException>(
                () => method.Code.Compare(SurtrComparison.Less, SurtrValueTypeCode.Object));
        }

        #endregion

        #region Disassembly

        [Fact]
        public void TheDisassembler_DecodesWhatTheEmitterWrote()
        {
            var builder = new SurtrModuleBuilder("test");
            var method = builder.DefineFunction("body", SurtrClassReference.Integer, IntParameters(builder, "n"));

            var skip = method.Code.NewLabel();
            method.Code
                .LoadLocal(method.Parameter(0))
                .LoadInt(3)
                .JumpIfCompare(SurtrComparison.Less, SurtrValueTypeCode.Integer, skip)
                .LoadString("big")
                .Pop()
                .MarkLabel(skip)
                .LoadInt(1)
                .ReturnValue();

            var module = builder.Build();
            string text = SurtrBytecodeDisassembler.Disassemble(module);

            Assert.Contains("module test", text);
            Assert.Contains("Ldl0", text);
            Assert.Contains("JPLT", text);
            Assert.Contains("\"big\"", text);
            Assert.Contains("ReturnValue", text);
        }

        #endregion
    }
}
