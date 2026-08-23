#nullable enable

using Surtr.Bytecode;
using Surtr.Bytecode.Emit;
using Surtr.Bytecode.Image;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Runtime.Utilities;
using System;

namespace Surtr.Tests.VM
{
    /// <summary>
    /// Covers multi-slot returns (<c>ReturnValues</c>) and the value-type opcodes: moving blocks
    /// through locals, boxing into an ordinary instance, and the linker's flattened layout.
    /// </summary>
    public class SurtrVirtualMachineValueTypesTests
    {
        private static SurtrValue Run(SurtrRuntime runtime, SurtrModule module, BytecodeBuilder builder, int localCount = 0, int maxStackSize = 32)
        {
            var method = builder.Build(module, localCount, maxStackSize);
            return runtime.Invoke(method);
        }

        /// <summary>A two-field value class - an int and a float - linked as a value type.</summary>
        private static (SurtrClass Type, SurtrFieldInfo X, SurtrFieldInfo Y) DefineVec2(SurtrModule module)
        {
            var type = VmMetadataHelpers.DefineClass(module, "Vec2");
            type.IsValueType = true;

            var x = VmMetadataHelpers.Field(module, "x", SurtrClassReference.Integer);
            var y = VmMetadataHelpers.Field(module, "y", SurtrClassReference.Float);
            type.AddField(x);
            type.AddField(y);

            return (type, x, y);
        }

        /// <summary>A three-field int-only value class, for a three-slot block.</summary>
        private static SurtrClass DefineTriple(SurtrModule module)
        {
            var type = VmMetadataHelpers.DefineClass(module, "Triple");
            type.IsValueType = true;

            for (int i = 0; i < 3; i++)
                type.AddField(VmMetadataHelpers.Field(module, $"f{i}", SurtrClassReference.Integer));

            return type;
        }

        #region ReturnValues

        [Fact]
        public void ReturnValues_AtEntryDepth_HandsTheBlockToTheHost()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var triple = DefineTriple(module);
            VmMetadataHelpers.HandleFor(module, triple);
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();

            builder
                .Op(OpCode.PushI32).I32(10)
                .Op(OpCode.PushI32).I32(20)
                .Op(OpCode.PushI32).I32(30)
                .Op(OpCode.ReturnValues).U8(3);

            // The declared return type is what tells the host boundary how many slots to expect:
            // a three-field value class is a three-slot result.
            var method = builder.Build(module, localCount: 0, maxStackSize: 8, returnType: triple.SelfReference);

            var results = new SurtrValue[3];
            Assert.True(runtime.TryInvoke(method, ReadOnlySpan<SurtrValue>.Empty, results));
            Assert.Equal(10, results[0].AsInt);
            Assert.Equal(20, results[1].AsInt);
            Assert.Equal(30, results[2].AsInt);
        }

        [Fact]
        public void TryInvoke_AnswersOneSlotForASingleValueMethod()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder().Op(OpCode.PushI32).I32(7).Op(OpCode.ReturnValue);
            var method = builder.Build(module, localCount: 0, maxStackSize: 4, returnType: SurtrClassReference.Integer);

            var results = new SurtrValue[1];
            Assert.True(runtime.TryInvoke(method, ReadOnlySpan<SurtrValue>.Empty, results));
            Assert.Equal(7, results[0].AsInt);
        }

        [Fact]
        public void TryInvoke_AnswersNoSlotsForAVoidMethod()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder().Op(OpCode.ReturnVoid);
            var method = builder.Build(module, localCount: 0, maxStackSize: 4);

            Assert.True(runtime.TryInvoke(method, ReadOnlySpan<SurtrValue>.Empty, Span<SurtrValue>.Empty));
        }

        [Fact]
        public void ReturnValues_FromACalledMethod_WritesTheBlockAtTheCallersFrameBase()
        {
            using var runtime = new SurtrRuntime();

            // The callee returns a two-slot block. On return the block lands at the callee's own
            // frame base - which is exactly where the caller's operand stack resumes - so the
            // caller pops it like any other result: two stores, then the read back.
            var calleeModule = new SurtrModule("callee");
            var callee = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(11)
                .Op(OpCode.PushI32).I32(22)
                .Op(OpCode.ReturnValues).U8(2)
                .Build(calleeModule, localCount: 0, maxStackSize: 8);

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(callee);

            builder
                .Op(OpCode.InvokeStatic).I16(methodIndex).U8(0).U8(1)
                .Op(OpCode.StlS).U8(0)
                .Op(OpCode.StlS).U8(1)
                .Op(OpCode.Ldl0)
                .Op(OpCode.Ldl1)
                .Op(OpCode.Add)
                .Op(OpCode.ReturnValue);

            var caller = builder.Build(callerModule, localCount: 2, maxStackSize: 8);

            Assert.Equal(33, runtime.Invoke(caller).AsInt);
        }

        [Fact]
        public void ReturnValues_WithNoResultAsked_DiscardsTheBlock()
        {
            using var runtime = new SurtrRuntime();

            var calleeModule = new SurtrModule("callee");
            var callee = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.PushI32).I32(2)
                .Op(OpCode.ReturnValues).U8(2)
                .Build(calleeModule, localCount: 0, maxStackSize: 8);

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(callee);

            builder
                .Op(OpCode.InvokeStatic).I16(methodIndex).U8(0).U8(0)
                .Op(OpCode.PushI32).I32(5)
                .Op(OpCode.ReturnValue);

            var caller = builder.Build(callerModule, localCount: 0, maxStackSize: 8);

            Assert.Equal(5, runtime.Invoke(caller).AsInt);
        }

        #endregion

        #region Locals and fields

        [Fact]
        public void StoreAndLoadValueLocal_RoundTripTheBlockThroughAFrameSlotRange()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var (vec2, _, _) = DefineVec2(module);
            VmMetadataHelpers.HandleFor(module, vec2);
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();

            builder
                .Op(OpCode.PushI32).I32(4)
                .LoadFloat(2.5)
                .Op(OpCode.StoreValueLocal).I16(0).U8(2)
                .Op(OpCode.LoadValueLocal).I16(0).U8(2)
                .Op(OpCode.ReturnValues).U8(2);

            var method = builder.Build(module, localCount: 2, maxStackSize: 8, returnType: vec2.SelfReference);

            var results = new SurtrValue[2];
            Assert.True(runtime.TryInvoke(method, ReadOnlySpan<SurtrValue>.Empty, results));
            Assert.Equal(4, results[0].AsInt);
            Assert.Equal(2.5, results[1].AsFloat);
        }

        [Fact]
        public void LoadLocalField_ReadsOneSlotOfABlockWithoutMovingIt()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();

            builder
                .Op(OpCode.PushI32).I32(4)
                .Op(OpCode.PushI32).I32(9)
                .Op(OpCode.StoreValueLocal).I16(0).U8(2)
                .Op(OpCode.LoadLocalField).I16(0).I16(1)
                .Op(OpCode.ReturnValue);

            // Slot 1 of the block is the second element.
            Assert.Equal(9, Run(runtime, module, builder, localCount: 2).AsInt);
        }

        [Fact]
        public void StoreLocalField_WritesOneSlotOfABlockInPlace()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();

            builder
                .Op(OpCode.PushI32).I32(4)
                .Op(OpCode.PushI32).I32(9)
                .Op(OpCode.StoreValueLocal).I16(0).U8(2)
                .Op(OpCode.Pop)
                .Op(OpCode.PushI32).I32(99)
                .Op(OpCode.StoreLocalField).I16(0).I16(0)
                .Op(OpCode.LoadLocalField).I16(0).I16(0)
                .Op(OpCode.ReturnValue);

            Assert.Equal(99, Run(runtime, module, builder, localCount: 2).AsInt);
        }

        #endregion

        #region Box and unbox

        [Fact]
        public void BoxThenUnboxValue_RoundTripsEverySlot()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var (type, _, _) = DefineVec2(module);
            VmMetadataHelpers.HandleFor(module, type);
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(module, type));

            builder
                .Op(OpCode.PushI32).I32(7)
                .LoadFloat(2.5)
                .Op(OpCode.BoxValue).I16(typeIndex).U8(2)
                .Op(OpCode.UnboxValue).U8(2)
                .Op(OpCode.ReturnValues).U8(2);

            var method = builder.Build(module, localCount: 0, maxStackSize: 8, returnType: type.SelfReference);

            var results = new SurtrValue[2];
            Assert.True(runtime.TryInvoke(method, ReadOnlySpan<SurtrValue>.Empty, results));
            Assert.Equal(7, results[0].AsInt);
            Assert.Equal(2.5, results[1].AsFloat);
        }

        [Fact]
        public void ABoxedValue_IsAnOrdinaryInstanceOfItsClass()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var (type, x, _) = DefineVec2(module);
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(module, type));
            int fieldIndex = builder.AddField(x);

            builder
                .Op(OpCode.PushI32).I32(7)
                .LoadFloat(2.5)
                .Op(OpCode.BoxValue).I16(typeIndex).U8(2)
                .Op(OpCode.FieldGet).I16(fieldIndex)
                .Op(OpCode.ReturnValue);

            Assert.Equal(7, Run(runtime, module, builder).AsInt);
        }

        #endregion

        #region Layout

        [Fact]
        public void TheLinker_FlattensAValueTypeIntoItsFieldWidths()
        {
            var module = new SurtrModule("layout");
            var (type, _, _) = DefineVec2(module);

            SurtrTypeLinker.LinkModule(module);

            Assert.True(type.IsValueType);
            Assert.Equal(2, type.FlattenedSlotWidth);
            Assert.Equal(2, type.InstanceSlotCount);
            Assert.Equal(0, type.ReferenceSlots.Length);
        }

        [Fact]
        public void TheLinker_TracksReferenceSlotsThroughNestedValues()
        {
            var module = new SurtrModule("layout");

            // Inner wraps one string; Outer holds Inner plus an int, so the string lives two
            // values deep and its absolute slot is what the collector must follow.
            var inner = VmMetadataHelpers.DefineClass(module, "Inner");
            inner.IsValueType = true;
            inner.AddField(VmMetadataHelpers.Field(module, "s", SurtrClassReference.String));

            var outer = VmMetadataHelpers.DefineClass(module, "Outer");
            outer.IsValueType = true;
            outer.AddField(VmMetadataHelpers.Field(module, "inner", inner.SelfReference));
            outer.AddField(VmMetadataHelpers.Field(module, "n", SurtrClassReference.Integer));

            // Same linking contract: both handles resolved before the layout walk runs.
            VmMetadataHelpers.HandleFor(module, inner);
            VmMetadataHelpers.HandleFor(module, outer);

            SurtrTypeLinker.LinkModule(module);

            // Inner flattens to one slot (its string), so Outer is that slot plus the int - two
            // slots wide - and the string's absolute reference slot stays at 0.
            Assert.Equal(1, inner.FlattenedSlotWidth);
            Assert.Equal(1, inner.ReferenceSlots.Length);

            Assert.Equal(2, outer.FlattenedSlotWidth);
            Assert.Equal(1, outer.ReferenceSlots.Length);
            Assert.Equal(0, outer.ReferenceSlots[0]);
        }

        [Fact]
        public void TheLinker_RejectsAValueTypeThatContainsItself()
        {
            var module = new SurtrModule("layout");
            var loop = VmMetadataHelpers.DefineClass(module, "Loop");
            loop.IsValueType = true;
            loop.AddField(VmMetadataHelpers.Field(module, "self", loop.SelfReference));

            // Linking runs only after every handle the module mentions is resolved; resolving the
            // self-reference is what makes the cycle visible to the layout walk at all.
            VmMetadataHelpers.HandleFor(module, loop);

            Assert.Throws<InvalidOperationException>(() => SurtrTypeLinker.LinkModule(module));
        }

        [Fact]
        public void TheLinker_RejectsAValueTypeWiderThanACallCanCarry()
        {
            var module = new SurtrModule("layout");
            var wide = VmMetadataHelpers.DefineClass(module, "Wide");
            wide.IsValueType = true;

            for (int i = 0; i < 255; i++)
                wide.AddField(VmMetadataHelpers.Field(module, $"f{i}", SurtrClassReference.Integer));

            Assert.Throws<InvalidOperationException>(() => SurtrTypeLinker.LinkModule(module));
        }

        [Fact]
        public void AnOrdinaryClass_KeepsOneSlotPerField()
        {
            var module = new SurtrModule("layout");
            var (type, _, _) = DefineVec2(module);
            type.IsValueType = false;

            SurtrTypeLinker.LinkModule(module);

            Assert.False(type.IsValueType);
            Assert.Equal(-1, type.FlattenedSlotWidth);
            Assert.Equal(2, type.InstanceSlotCount);
        }

        #endregion

        #region Inline fields

        /// <summary>
        /// An ordinary class whose field holds a two-slot value type: the field claims consecutive
        /// slots, the holder's instance grows by the value's whole width, and the inner string's
        /// absolute slot lands in both reference maps.
        /// </summary>
        [Fact]
        public void TheLinker_FlattensAValueTypeField_IntoTheHoldersLayout()
        {
            var module = new SurtrModule("layout");

            var inner = VmMetadataHelpers.DefineClass(module, "Inner");
            inner.IsValueType = true;
            inner.AddField(VmMetadataHelpers.Field(module, "n", SurtrClassReference.Integer));
            inner.AddField(VmMetadataHelpers.Field(module, "s", SurtrClassReference.String));

            var holder = VmMetadataHelpers.DefineClass(module, "Holder");
            var count = VmMetadataHelpers.Field(module, "count", SurtrClassReference.Integer);
            var tag = VmMetadataHelpers.Field(module, "tag", inner.SelfReference);
            var label = VmMetadataHelpers.Field(module, "label", SurtrClassReference.String);
            holder.AddField(count);
            holder.AddField(tag);
            holder.AddField(label);

            var origin = VmMetadataHelpers.Field(module, "origin", inner.SelfReference, isStatic: true);
            holder.AddField(origin);

            VmMetadataHelpers.HandleFor(module, inner);
            VmMetadataHelpers.HandleFor(module, holder);

            SurtrTypeLinker.LinkModule(module);

            Assert.Equal(2, inner.FlattenedSlotWidth);
            Assert.Equal(new[] { 1 }, ToArray(inner.ReferenceSlots)); // inner.s, relative to the block

            // count keeps slot 0; the inline value owns slots 1-2; label follows at 3.
            Assert.Equal(0, count.SlotIndex);
            Assert.Equal(1, tag.SlotIndex);
            Assert.Equal(3, label.SlotIndex);
            Assert.Equal(4, holder.InstanceSlotCount);
            Assert.Equal(new[] { 2, 3 }, ToArray(holder.ReferenceSlots)); // tag.s shifted by the field's base, then label

            // The static claims the same width in its own storage, its string tracked too.
            Assert.Equal(0, origin.SlotIndex);
            Assert.Equal(2, holder.StaticStorage.Length);
            Assert.Equal(new[] { 1 }, ToArray(holder.ReferenceStaticSlots));
        }

        [Fact]
        public void ModuleLevelGlobals_ClaimAValueTypesWholeWidth()
        {
            var module = new SurtrModule("globals");

            var inner = VmMetadataHelpers.DefineClass(module, "Inner");
            inner.IsValueType = true;
            inner.AddField(VmMetadataHelpers.Field(module, "n", SurtrClassReference.Integer));
            inner.AddField(VmMetadataHelpers.Field(module, "s", SurtrClassReference.String));

            var home = VmMetadataHelpers.Field(module, "home", inner.SelfReference, isStatic: true);
            var name = VmMetadataHelpers.Field(module, "name", SurtrClassReference.String, isStatic: true);
            module.AddField(home);
            module.AddField(name);

            VmMetadataHelpers.HandleFor(module, inner);

            SurtrTypeLinker.LinkModule(module);

            Assert.Equal(0, home.SlotIndex);
            Assert.Equal(2, name.SlotIndex);
            Assert.Equal(3, module.StaticStorage.Length);
            Assert.Equal(new[] { 1, 2 }, ToArray(module.ReferenceStaticSlots));
        }

        [Fact]
        public void StoreThenLoadValueField_MovesABlockThroughAnInstance()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var (vec2, _, _) = DefineVec2(module);
            var holder = VmMetadataHelpers.DefineClass(module, "Holder");
            var position = VmMetadataHelpers.Field(module, "position", vec2.SelfReference);
            holder.AddField(position);

            // Both handles the module mentions resolve before the layout walk runs.
            VmMetadataHelpers.HandleFor(module, vec2);
            VmMetadataHelpers.HandleFor(module, holder);
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();
            int holderTypeIndex = builder.AddType(VmMetadataHelpers.HandleFor(module, holder));
            int fieldIndex = builder.AddField(position);

            builder
                .Op(OpCode.ObjNew).I16(holderTypeIndex)
                .Op(OpCode.Stl).I16(0)
                .Op(OpCode.Ldl).I16(0)
                .Op(OpCode.PushI32).I32(7)
                .LoadFloat(2.5)
                .Op(OpCode.StoreValueField).I16(fieldIndex).U8(2)
                .Op(OpCode.Ldl).I16(0)
                .Op(OpCode.LoadValueField).I16(fieldIndex).U8(2)
                .Op(OpCode.ReturnValues).U8(2);

            var method = builder.Build(module, localCount: 1, maxStackSize: 8, returnType: vec2.SelfReference);

            var results = new SurtrValue[2];
            Assert.True(runtime.TryInvoke(method, ReadOnlySpan<SurtrValue>.Empty, results));
            Assert.Equal(7, results[0].AsInt);
            Assert.Equal(2.5, results[1].AsFloat);
        }

        [Fact]
        public void ASubSlotOfAnInlineField_ReadsAtItsAbsolutePosition()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var (vec2, _, _) = DefineVec2(module);
            var holder = VmMetadataHelpers.DefineClass(module, "Holder");
            var position = VmMetadataHelpers.Field(module, "position", vec2.SelfReference);
            holder.AddField(position);
            VmMetadataHelpers.HandleFor(module, vec2);
            VmMetadataHelpers.HandleFor(module, holder);
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();
            int holderTypeIndex = builder.AddType(VmMetadataHelpers.HandleFor(module, holder));
            int fieldIndex = builder.AddField(position);

            // Store the block, load it back, drop the second slot, and answer the first.
            builder
                .Op(OpCode.ObjNew).I16(holderTypeIndex)
                .Op(OpCode.Stl).I16(0)
                .Op(OpCode.Ldl).I16(0)
                .Op(OpCode.PushI32).I32(7)
                .LoadFloat(2.5)
                .Op(OpCode.StoreValueField).I16(fieldIndex).U8(2)
                .Op(OpCode.Ldl).I16(0)
                .Op(OpCode.LoadValueField).I16(fieldIndex).U8(2)
                .Op(OpCode.Pop)
                .Op(OpCode.ReturnValue);

            Assert.Equal(7, Run(runtime, module, builder, localCount: 1).AsInt);
        }

        [Fact]
        public void StoreThenLoadValueStatic_MovesABlockThroughStaticStorage()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var (vec2, _, _) = DefineVec2(module);
            var holder = VmMetadataHelpers.DefineClass(module, "Holder");
            var origin = VmMetadataHelpers.Field(module, "origin", vec2.SelfReference, isStatic: true);
            holder.AddField(origin);
            VmMetadataHelpers.HandleFor(module, vec2);
            VmMetadataHelpers.HandleFor(module, holder);
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();
            int fieldIndex = builder.AddField(origin);

            builder
                .Op(OpCode.PushI32).I32(11)
                .LoadFloat(1.5)
                .Op(OpCode.StoreValueStatic).I16(fieldIndex).U8(2)
                .Op(OpCode.LoadValueStatic).I16(fieldIndex).U8(2)
                .Op(OpCode.ReturnValues).U8(2);

            var method = builder.Build(module, localCount: 0, maxStackSize: 8, returnType: vec2.SelfReference);

            var results = new SurtrValue[2];
            Assert.True(runtime.TryInvoke(method, ReadOnlySpan<SurtrValue>.Empty, results));
            Assert.Equal(11, results[0].AsInt);
            Assert.Equal(1.5, results[1].AsFloat);
        }

        private static int[] ToArray(SurtrNativeArray<int> slots)
        {
            var values = new int[slots.Length];
            for (int i = 0; i < values.Length; i++)
                values[i] = slots[i];

            return values;
        }

        #endregion

        #region Equality

        [Fact]
        public void TwoBoxesOfTheSameValue_CompareEqualAndHashAlike()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("eq");
            var (type, _, _) = DefineVec2(module);
            SurtrTypeLinker.LinkModule(module);

            var first = runtime.NewInstance(type);
            var second = runtime.NewInstance(type);
            foreach (var box in new[] { first, second })
            {
                box[0] = SurtrValue.CreateInt(3);
                box[1] = SurtrValue.CreateFloat(1.5f);
            }

            var comparer = runtime.ValueComparer;

            Assert.True(comparer.ValuesEqual(SurtrValue.CreateReference(first.GetSurtrReference()), SurtrValue.CreateReference(second.GetSurtrReference())));
            Assert.Equal(comparer.HashOf(SurtrValue.CreateReference(first.GetSurtrReference())), comparer.HashOf(SurtrValue.CreateReference(second.GetSurtrReference())));

            second[0] = SurtrValue.CreateInt(4);
            Assert.False(comparer.ValuesEqual(SurtrValue.CreateReference(first.GetSurtrReference()), SurtrValue.CreateReference(second.GetSurtrReference())));
        }

        [Fact]
        public void BoxesOfDifferentValueTypes_NeverCompareEqual()
        {
            using var runtime = new SurtrRuntime();
            var leftModule = new SurtrModule("left");
            var (left, _, _) = DefineVec2(leftModule);
            SurtrTypeLinker.LinkModule(leftModule);

            var rightModule = new SurtrModule("right");
            var right = VmMetadataHelpers.DefineClass(rightModule, "Pair");
            right.IsValueType = true;
            right.AddField(VmMetadataHelpers.Field(rightModule, "a", SurtrClassReference.Integer));
            right.AddField(VmMetadataHelpers.Field(rightModule, "b", SurtrClassReference.Float));
            SurtrTypeLinker.LinkModule(rightModule);

            var leftBox = runtime.NewInstance(left);
            leftBox[0] = SurtrValue.CreateInt(1);
            leftBox[1] = SurtrValue.CreateFloat(1.0f);

            var rightBox = runtime.NewInstance(right);
            rightBox[0] = SurtrValue.CreateInt(1);
            rightBox[1] = SurtrValue.CreateFloat(1.0f);

            var comparer = runtime.ValueComparer;

            Assert.False(comparer.ValuesEqual(SurtrValue.CreateReference(leftBox.GetSurtrReference()), SurtrValue.CreateReference(rightBox.GetSurtrReference())));
        }

        #endregion

        #region Image round trip

        [Fact]
        public void TheImageFlag_SurvivesARoundTrip_AndLayoutFollowsIt()
        {
            var builder = new SurtrModuleBuilder("game");
            var vec2 = builder.DefineClass("Vec2");
            vec2.DefineField("x", SurtrClassReference.Integer);
            vec2.DefineField("y", SurtrClassReference.Float);
            vec2.Class.IsValueType = true;

            var function = builder.DefineFunction("answer", SurtrClassReference.Integer);
            function.Code.LoadInt(41).LoadInt(1).Add().ReturnValue();

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            var loaded = module.TypeHandles.GetOrAdd(vec2.SelfReference).ResolvedType;
            var valueClass = Assert.IsType<SurtrClass>(loaded);
            Assert.True(valueClass.IsValueType);
            Assert.Equal(2, valueClass.FlattenedSlotWidth);
        }

        #endregion
    }
}
