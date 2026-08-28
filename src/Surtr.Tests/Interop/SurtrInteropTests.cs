#nullable enable

using Surtr.Bytecode;
using Surtr.Interop;
using Surtr.Interop.Attributes;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Tests.VM;
using System;
using System.Linq;
using Xunit;

namespace Surtr.Tests.Interop
{
    public class SurtrInteropTests
    {
        #region Native fields

        private sealed class Thing
        {
            public int Value;
        }

        private static int GetValue(SurtrCallArguments args)
        {
            var target = args.Runtime.Resolve<SurtrNativeObject>(args[0])!.TargetAs<Thing>()!;
            return args.Return(SurtrValue.CreateInt(target.Value));
        }

        private static int SetValue(SurtrCallArguments args)
        {
            var target = args.Runtime.Resolve<SurtrNativeObject>(args[0])!.TargetAs<Thing>()!;
            target.Value = args.GetInt(1);
            return args.Return(SurtrValue.Null);
        }

        private static SurtrValue Run(SurtrRuntime runtime, SurtrModule module, BytecodeBuilder builder, int maxStackSize = 32)
            => runtime.Invoke(builder.Build(module, localCount: 0, maxStackSize));

        [Fact]
        public void DefineNativeField_OwnsNoSlotAndReadsThroughTheVm()
        {
            using var runtime = new SurtrRuntime();

            var thing = runtime.DefineNativeClass("test:Thing");
            var field = runtime.DefineNativeField(
                thing,
                "value",
                SurtrClassReference.Integer,
                SurtrNativeEntryPoint.FromDelegate(GetValue),
                SurtrNativeEntryPoint.FromDelegate(SetValue));

            runtime.FinishNativeClass(thing);

            // A native field has no instance slot and never enters the instance layout.
            Assert.IsType<SurtrNativeFieldInfo>(field);
            Assert.Equal(-1, field.Slot);
            Assert.Empty(thing.InstanceFields);

            var instance = runtime.WrapNative(thing, new Thing { Value = 41 });

            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();
            int fieldIndex = builder.AddField(field);

            builder
                .LoadReference(instance).Op(OpCode.FieldGet).I16(fieldIndex)
                .Op(OpCode.ReturnValue);

            Assert.Equal(41, Run(runtime, module, builder).AsInt);
        }

        [Fact]
        public void NativeFieldSet_WritesThroughTheVm()
        {
            using var runtime = new SurtrRuntime();

            var thing = runtime.DefineNativeClass("test:Thing");
            var field = runtime.DefineNativeField(
                thing,
                "value",
                SurtrClassReference.Integer,
                SurtrNativeEntryPoint.FromDelegate(GetValue),
                SurtrNativeEntryPoint.FromDelegate(SetValue));

            runtime.FinishNativeClass(thing);

            var target = new Thing { Value = 0 };
            var instance = runtime.WrapNative(thing, target);

            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();
            int fieldIndex = builder.AddField(field);

            builder
                .LoadReference(instance).Op(OpCode.PushI32).I32(99).Op(OpCode.FieldSet).I16(fieldIndex)
                .LoadReference(instance).Op(OpCode.FieldGet).I16(fieldIndex)
                .Op(OpCode.ReturnValue);

            Assert.Equal(99, Run(runtime, module, builder).AsInt);
            Assert.Equal(99, target.Value);
        }

        [Fact]
        public unsafe void ReadOnlyNativeField_SetterThrows()
        {
            using var runtime = new SurtrRuntime();

            var thing = runtime.DefineNativeClass("test:ReadOnlyThing");
            var field = runtime.DefineNativeField(
                thing,
                "value",
                SurtrClassReference.Integer,
                SurtrNativeEntryPoint.FromDelegate(GetValue),
                SurtrNativeEntryPoint.FromDelegate(SetValue),
                isReadOnly: true);

            runtime.FinishNativeClass(thing);

            var nativeField = Assert.IsType<SurtrNativeFieldInfo>(field);
            Assert.Throws<InvalidOperationException>(() =>
                nativeField.Setter.Invoke(new SurtrCallArguments(runtime, null, 0)));
        }

        #endregion

        #region Native enums

        [SurtrNativeType]
        public enum LogLevel
        {
            Debug,
            Info,
            Warning,
            Error,
        }

        [SurtrNativeType]
        [System.Flags]
        public enum Perm
        {
            Read = 1,
            Write = 2,
            Execute = 4,
        }

        [Fact]
        public void DefineNativeEnum_ProducesASurtrEnumWithCases()
        {
            using var runtime = new SurtrRuntime();

            var descriptor = SurtrReflectionScanner.Scan(typeof(LogLevel));
            Assert.Equal(NativeTypeKind.Enum, descriptor.Kind);
            Assert.Equal(new[] { "Debug", "Info", "Warning", "Error" }, descriptor.EnumCases.Select(c => c.Name));
            Assert.Equal(new[] { 0L, 1L, 2L, 3L }, descriptor.EnumCases.Select(c => c.Value));

            var enumClass = SurtrBridge.Register(runtime, descriptor);

            Assert.True(enumClass.IsEnum);
            Assert.Equal(4, enumClass.EnumCases.Length);
            Assert.Equal("Debug", enumClass.EnumCases[0].Name);
            Assert.Equal(3, enumClass.EnumCases[3].Ordinal);

            // A case's value travels with it: the static holds the int, not a proxy reference.
            Assert.Equal(2, enumClass.EnumCases[2].Value);

            // An enum is a value class whose first field is `value` (§2.4).
            Assert.True(enumClass.IsValueType);
            Assert.True(enumClass.TryGetField("value", out _));
        }

        [Fact]
        public void AFlagsEnumRegistersAsFlagsAndMarshalizesArithmetically()
        {
            using var runtime = new SurtrRuntime();

            // A [Flags] CLR enum reports it, with each case's numeric value.
            var descriptor = SurtrReflectionScanner.Scan(typeof(Perm));
            Assert.True(descriptor.IsFlags);
            Assert.Equal(new[] { 1L, 2L, 4L }, descriptor.EnumCases.Select(c => c.Value));

            var enumClass = SurtrBridge.Register(runtime, descriptor);
            Assert.True(enumClass.TryGetAttribute(SurtrBuiltIns.Flags, out _), "A [Flags] CLR enum registers as a Surtr @Flags enum.");

            // Marshaling is pure arithmetic: a combination of bits with no named case is as valid
            // as a named one — no proxy, no cache, no "not registered".
            var surtr = SurtrEnums.ToSurtr(runtime, Perm.Read | Perm.Write);
            Assert.Equal(3, surtr.AsInt);

            Assert.Equal(Perm.Read | Perm.Write, SurtrEnums.ToClr<Perm>(runtime, surtr));
            Assert.Equal((Perm)6, SurtrEnums.ToClr<Perm>(runtime, SurtrValue.CreateInt(6)));
        }

        #endregion

        #region Reflection fallback

        [SurtrNativeType]
        public class Calculator
        {
            public int Add(int a, int b) => a + b;

            public int Count;

            public string Label { get; set; } = "x";
        }

        [Fact]
        public void ScanAndRegister_ExposesMethodsFieldsAndProperties()
        {
            using var runtime = new SurtrRuntime();

            SurtrBridge.ScanAndRegister(runtime, typeof(Calculator), typeof(LogLevel));

            Assert.True(runtime.TryGetNativeClass("Calculator", out var calculator));

            Assert.True(calculator.TryGetMethods("add", out var addOverloads));
            Assert.True(calculator.TryGetField("count", out _));
            Assert.True(calculator.TryGetProperty("label", out _));

            // Naming policy default adapts PascalCase members to Surtr camelCase.
            Assert.False(calculator.TryGetField("Count", out _));

            var instance = runtime.WrapNative(calculator, new Calculator { Count = 7 });

            var add = addOverloads[0];
            var result = runtime.Invoke(add,
                SurtrValue.CreateReference(instance.GetSurtrReference()),
                SurtrValue.CreateInt(3),
                SurtrValue.CreateInt(4));

            Assert.Equal(7, result.AsInt);
        }

        #endregion
    }
}
