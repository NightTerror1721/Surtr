#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;

namespace Surtr.Tests.VM
{
    public class SurtrVirtualMachineObjectAndFieldTests
    {
        private static SurtrValue Run(SurtrRuntime runtime, SurtrModule module, BytecodeBuilder builder, int maxStackSize = 32)
        {
            var method = builder.Build(module, localCount: 0, maxStackSize);
            return runtime.Invoke(method);
        }

        [Fact]
        public void ObjNew_AllocatesAZeroedInstance()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var type = VmMetadataHelpers.DefineClass(module, "Point");
            var x = VmMetadataHelpers.Field(module, "x", SurtrClassReference.Integer);
            type.AddField(x);
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(module, type));
            int fieldIndex = builder.AddField(x);

            builder
                .Op(OpCode.ObjNew).I16(typeIndex)
                .Op(OpCode.FieldGet).I16(fieldIndex)
                .Op(OpCode.ReturnValue);

            // Unlike ArrNew (which retags a fresh element to its family's zero), a fresh instance's
            // field slots are the plain C# array default: an untagged zero. It still reads back as
            // 0 through AsInt - that is the whole point of the untagged-zero convention - but it
            // does not carry the Int tag until something actually writes an int into the slot.
            var result = Run(runtime, module, builder);
            Assert.False(result.IsInt);
            Assert.Equal(0, result.AsInt);
        }

        [Fact]
        public void ObjNewX_AllocatesWithAFourByteTypeIndex()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var type = VmMetadataHelpers.DefineClass(module, "Marker");
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(module, type));

            builder.Op(OpCode.ObjNewX).I32(typeIndex).Op(OpCode.IsNotNull).Op(OpCode.ReturnValue);

            Assert.True(Run(runtime, module, builder).AsBool);
        }

        [Fact]
        public void FieldGetAndSet_RoundTripThroughAnInstance()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var type = VmMetadataHelpers.DefineClass(module, "Counter");
            var count = VmMetadataHelpers.Field(module, "count", SurtrClassReference.Integer);
            type.AddField(count);
            SurtrTypeLinker.LinkModule(module);

            var instance = runtime.NewInstance(type);

            var builder = new BytecodeBuilder();
            int fieldIndex = builder.AddField(count);

            builder
                .LoadReference(instance).Op(OpCode.PushI32).I32(41).Op(OpCode.FieldSet).I16(fieldIndex)
                .LoadReference(instance).Op(OpCode.FieldGet).I16(fieldIndex)
                .Op(OpCode.ReturnValue);

            Assert.Equal(41, Run(runtime, module, builder).AsInt);
        }

        [Fact]
        public void FieldGet_OnADerivedInstance_UsesTheBaseClasssSlot()
        {
            // The linker keeps an inherited field's slot index, so a FieldGet compiled against the
            // base type must still resolve correctly on a derived instance.
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var baseType = VmMetadataHelpers.DefineClass(module, "Base");
            var x = VmMetadataHelpers.Field(module, "x", SurtrClassReference.Integer);
            baseType.AddField(x);

            var derivedType = VmMetadataHelpers.DefineClass(module, "Derived", baseClass: baseType);
            var y = VmMetadataHelpers.Field(module, "y", SurtrClassReference.Integer);
            derivedType.AddField(y);

            SurtrTypeLinker.LinkModule(module);

            var instance = runtime.NewInstance(derivedType);

            var builder = new BytecodeBuilder();
            int xField = builder.AddField(x);
            int yField = builder.AddField(y);

            builder
                .LoadReference(instance).Op(OpCode.PushI32).I32(1).Op(OpCode.FieldSet).I16(xField)
                .LoadReference(instance).Op(OpCode.PushI32).I32(2).Op(OpCode.FieldSet).I16(yField)
                .LoadReference(instance).Op(OpCode.FieldGet).I16(xField)
                .LoadReference(instance).Op(OpCode.FieldGet).I16(yField)
                .Op(OpCode.Sub)
                .Op(OpCode.ReturnValue);

            Assert.Equal(-1, Run(runtime, module, builder).AsInt); // 1 - 2
        }

        [Fact]
        public void StaticFieldGetAndSet_RoundTripThroughAClasssStaticStorage()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var type = VmMetadataHelpers.DefineClass(module, "Registry");
            var total = VmMetadataHelpers.Field(module, "total", SurtrClassReference.Integer, isStatic: true);
            type.AddField(total);
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();
            int fieldIndex = builder.AddField(total);

            builder
                .Op(OpCode.PushI32).I32(7).Op(OpCode.StaticFieldSet).I16(fieldIndex)
                .Op(OpCode.StaticFieldGet).I16(fieldIndex)
                .Op(OpCode.ReturnValue);

            Assert.Equal(7, Run(runtime, module, builder).AsInt);
        }

        [Fact]
        public void StaticFieldGetAndSetX_UseAFourByteFieldIndex()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var type = VmMetadataHelpers.DefineClass(module, "Registry");
            var total = VmMetadataHelpers.Field(module, "total", SurtrClassReference.Integer, isStatic: true);
            type.AddField(total);
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();
            int fieldIndex = builder.AddField(total);

            builder
                .Op(OpCode.PushI32).I32(13).Op(OpCode.StaticFieldSetX).I32(fieldIndex)
                .Op(OpCode.StaticFieldGetX).I32(fieldIndex)
                .Op(OpCode.ReturnValue);

            Assert.Equal(13, Run(runtime, module, builder).AsInt);
        }

        [Fact]
        public void StaticFieldGetAndSet_WorkTheSameWayForAModuleLevelVariable()
        {
            // A module-level variable is a static of its module - same table, same opcodes.
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var counter = VmMetadataHelpers.Field(module, "counter", SurtrClassReference.Integer, isStatic: true);
            module.AddField(counter);
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();
            int fieldIndex = builder.AddField(counter);

            builder
                .Op(OpCode.PushI32).I32(99).Op(OpCode.StaticFieldSet).I16(fieldIndex)
                .Op(OpCode.StaticFieldGet).I16(fieldIndex)
                .Op(OpCode.ReturnValue);

            Assert.Equal(99, Run(runtime, module, builder).AsInt);
        }
    }
}
