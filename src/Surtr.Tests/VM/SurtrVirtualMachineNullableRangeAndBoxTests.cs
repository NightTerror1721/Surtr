#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;

namespace Surtr.Tests.VM
{
    /// <summary>
    /// Covers the three value-representation features the surface syntax asked the runtime for:
    /// natively-tagged nullable primitives, first-class ranges, and a boxing path that can name the
    /// class it presents as.
    /// </summary>
    public unsafe class SurtrVirtualMachineNullableRangeAndBoxTests
    {
        private static SurtrValue Run(SurtrRuntime runtime, BytecodeBuilder builder, int localCount = 0, int maxStackSize = 16)
        {
            var module = new SurtrModule("test");
            return runtime.Invoke(builder.Build(module, localCount, maxStackSize));
        }

        #region Nullable primitives

        [Fact]
        public void AnAbsentPrimitive_IsNeitherAFloatNorAReference()
        {
            SurtrValue absent = SurtrValue.CreateAbsent(SurtrValueTypeCode.Integer);

            Assert.True(absent.IsAbsent);
            Assert.False(absent.IsFloat);
            Assert.False(absent.IsReference);
            Assert.False(absent.IsInt);
            Assert.Equal(SurtrValueTypeCode.Integer, absent.AbsentTypeCode);
        }

        [Fact]
        public void AnAbsentPrimitive_IsToldApartFromAZeroInt()
        {
            // The whole reason absence needs its own tag: a reference is its 32-bit payload, so the
            // payload-only null test would read an int 0 as null.
            SurtrValue zero = SurtrValue.CreateInt(0);
            SurtrValue absent = SurtrValue.CreateAbsent(SurtrValueTypeCode.Integer);

            Assert.False(zero.IsAbsent);
            Assert.NotEqual(zero.Raw, absent.Raw);
        }

        [Fact]
        public void AnAbsentPrimitive_IsNotTheNullReference()
        {
            Assert.False(SurtrValue.Null.IsAbsent);
            Assert.False(SurtrValue.CreateAbsent(SurtrValueTypeCode.Integer).IsNullReference);
        }

        [Theory]
        [InlineData(true, 1)]
        [InlineData(false, 0)]
        public void IsAbsent_AnswersTheTag(bool pushAbsent, int expected)
        {
            using var runtime = new SurtrRuntime();

            var builder = new BytecodeBuilder();
            if (pushAbsent)
                builder.Op(OpCode.PushAbsent).U8((byte)SurtrValueTypeCode.Integer);
            else
                builder.Op(OpCode.PushI32).I32(0);

            builder.Op(OpCode.IsAbsent).Op(OpCode.ReturnValue);

            Assert.Equal(expected == 1, Run(runtime, builder).AsBool);
        }

        [Theory]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        public void IsPresent_IsTheNegation(bool pushAbsent, int expected)
        {
            using var runtime = new SurtrRuntime();

            var builder = new BytecodeBuilder();
            if (pushAbsent)
                builder.Op(OpCode.PushAbsent).U8((byte)SurtrValueTypeCode.Integer);
            else
                builder.Op(OpCode.PushI32).I32(0);

            builder.Op(OpCode.IsPresent).Op(OpCode.ReturnValue);

            Assert.Equal(expected == 1, Run(runtime, builder).AsBool);
        }

        [Fact]
        public void JPA_BranchesOnlyOnAnAbsentValue()
        {
            using var runtime = new SurtrRuntime();

            // The shape of `a ?? b` over an int?.
            var builder = new BytecodeBuilder();
            int fallback = builder.NewLabel();
            builder
                .Op(OpCode.PushAbsent).U8((byte)SurtrValueTypeCode.Integer)
                .JumpShort(OpCode.JPA, fallback)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue)
                .MarkLabel(fallback)
                .Op(OpCode.PushI32).I32(99).Op(OpCode.ReturnValue);

            Assert.Equal(99, Run(runtime, builder).AsInt);
        }

        [Fact]
        public void JPA_DoesNotBranchOnAPresentZero()
        {
            using var runtime = new SurtrRuntime();

            var builder = new BytecodeBuilder();
            int fallback = builder.NewLabel();
            builder
                .Op(OpCode.PushI32).I32(0)
                .JumpShort(OpCode.JPA, fallback)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue)
                .MarkLabel(fallback)
                .Op(OpCode.PushI32).I32(99).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(runtime, builder).AsInt);
        }

        [Fact]
        public void JPNA_BranchesOnlyOnAPresentValue()
        {
            using var runtime = new SurtrRuntime();

            var builder = new BytecodeBuilder();
            int present = builder.NewLabel();
            builder
                .Op(OpCode.PushI32).I32(5)
                .JumpShort(OpCode.JPNA, present)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(present)
                .Op(OpCode.PushI32).I32(7).Op(OpCode.ReturnValue);

            Assert.Equal(7, Run(runtime, builder).AsInt);
        }

        [Fact]
        public void AnAbsentPrimitive_DoesNotRetainWhateverItsPayloadWouldName()
        {
            using var runtime = new SurtrRuntime();

            // The payload of an absent value is a type code, not an id - but it is a small integer,
            // so it collides with real entity ids all the time. Tracing tests the exact reference
            // tag, which is what makes a distinct tag inert to the collector for free, and this is
            // the observable form of that: rooting the absent value keeps nothing alive.
            var doomed = runtime.NewString("doomed");
            int id = doomed.GetSurtrReference();

            SurtrValue absent = SurtrValue.CreateAbsent((SurtrValueTypeCode)id);
            Assert.Equal((ulong)id, absent.Raw & 0xFFFFFFFF);

            var roots = new ulong[] { absent.Raw };
            runtime.Collect(null, null, roots, fullCollection: true);

            Assert.Null(runtime.Resolve<SurtrString>(SurtrValue.CreateReference(id)));
        }

        [Fact]
        public void TheDescriptorForANullablePrimitive_RoundTrips()
        {
            var nullable = SurtrClassReference.Nullable(SurtrClassReference.Integer);

            Assert.Equal("?I", nullable.Descriptor);
            Assert.True(nullable.IsNullablePrimitive);
            Assert.Equal(SurtrValueTypeCode.Integer, nullable.TypeCode);
            Assert.Equal("I", nullable.GetUnderlyingPrimitive().Descriptor);
            Assert.True(SurtrClassReference.IsWellFormed("?F"));
        }

        [Fact]
        public void OnlyAPrimitive_CanBeMadeNullable()
        {
            // A nullable reference needs no descriptor of its own, so allowing one would create a
            // second spelling for a type that already has a canonical one.
            Assert.Throws<System.ArgumentException>(() => SurtrClassReference.Nullable(SurtrClassReference.String));
            Assert.False(SurtrClassReference.IsWellFormed("?S"));
        }

        #endregion

        #region Ranges

        [Fact]
        public void RangeNew_BuildsTheExclusiveForm()
        {
            using var runtime = new SurtrRuntime();

            var builder = new BytecodeBuilder();
            builder.Op(OpCode.PushI32).I32(0).Op(OpCode.PushI32).I32(10).Op(OpCode.RangeNew).Op(OpCode.ReturnValue);

            var range = runtime.Resolve<SurtrRange>(Run(runtime, builder))!;

            Assert.Equal(0, range.Start);
            Assert.Equal(10, range.End);
            Assert.False(range.IsInclusive);
            Assert.Equal(10, range.Length);
            Assert.True(range.Contains(9));
            Assert.False(range.Contains(10));
        }

        [Fact]
        public void RangeNewInclusive_IncludesTheUpperBound()
        {
            using var runtime = new SurtrRuntime();

            var builder = new BytecodeBuilder();
            builder.Op(OpCode.PushI32).I32(0).Op(OpCode.PushI32).I32(10).Op(OpCode.RangeNewInclusive).Op(OpCode.ReturnValue);

            var range = runtime.Resolve<SurtrRange>(Run(runtime, builder))!;

            Assert.True(range.IsInclusive);
            Assert.Equal(11, range.Length);
            Assert.True(range.Contains(10));
        }

        [Fact]
        public void ARangeOverTheWholeOfInt_SaturatesItsLengthInsteadOfWrapping()
        {
            // Counted in 64 bits and clamped: the span of an inclusive range over the whole of
            // int is one more than an int can hold, which is also why normalising the bound away
            // at construction was not an option.
            var range = new SurtrRange(0, int.MaxValue, inclusive: true);
            Assert.Equal(int.MaxValue, range.Length);
        }

        [Fact]
        public void AnEmptyRange_ReportsZeroLength()
        {
            using var runtime = new SurtrRuntime();

            var builder = new BytecodeBuilder();
            builder.Op(OpCode.PushI32).I32(5).Op(OpCode.PushI32).I32(5).Op(OpCode.RangeNew).Op(OpCode.ReturnValue);

            var range = runtime.Resolve<SurtrRange>(Run(runtime, builder))!;

            Assert.True(range.IsEmpty);
            Assert.Equal(0, range.Length);
        }

        [Fact]
        public void TheRangeDescriptor_IsABareSymbol()
        {
            SurtrBuiltIns.EnsureBuilt();

            Assert.Equal("R", SurtrClassReference.Range.Descriptor);
            Assert.Equal(SurtrValueTypeCode.Range, SurtrClassReference.Range.TypeCode);
            Assert.Equal("range", SurtrClassReference.Range.ToDisplayString());
            Assert.Same(SurtrBuiltIns.Range, SurtrBuiltIns.ForTypeCode(SurtrValueTypeCode.Range));
        }

        [Fact]
        public void ARangeIsAReferenceTypeAndStillInsideTheBuiltInRun()
        {
            Assert.True(SurtrValueTypeCode.Range.IsBuiltIn);
            Assert.True(SurtrValueTypeCode.Range.IsReferenceType);
            Assert.False(SurtrValueTypeCode.Range.IsValueType);
        }

        #endregion

        #region Boxing under a named class

        [Fact]
        public void BoxAs_GivesTheBoxTheClassTheCallSiteNamed()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");

            var entityId = VmMetadataHelpers.DefineClass(module, "EntityId");
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();
            int type = builder.AddType(VmMetadataHelpers.HandleFor(module, entityId));
            builder.Op(OpCode.PushI32).I32(7).Op(OpCode.BoxAs).I16(type).Op(OpCode.ReturnValue);

            var boxed = runtime.Resolve<SurtrBoxed>(runtime.Invoke(builder.Build(module, 0, 8)))!;

            Assert.Same(entityId, boxed.Class);
            Assert.Equal(7, boxed.BoxedValue.AsInt);
        }

        [Fact]
        public void ABoxedValueClass_IsNotEqualToABoxedPrimitiveWithTheSameBits()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");

            var entityId = VmMetadataHelpers.DefineClass(module, "EntityId");
            SurtrTypeLinker.LinkModule(module);

            var builder = new BytecodeBuilder();
            int type = builder.AddType(VmMetadataHelpers.HandleFor(module, entityId));
            builder.Op(OpCode.PushI32).I32(7).Op(OpCode.BoxAs).I16(type).Op(OpCode.ReturnValue);

            SurtrValue asEntityId = runtime.Invoke(builder.Build(module, 0, 8));
            var asInt = runtime.Box(SurtrValue.CreateInt(7));

            Assert.False(runtime.ValueComparer.ValuesEqual(
                asEntityId,
                SurtrValue.CreateReference(asInt.GetSurtrReference())));

            // And neither is it equal to the bare primitive it erases to.
            Assert.False(runtime.ValueComparer.ValuesEqual(asEntityId, SurtrValue.CreateInt(7)));
        }

        [Fact]
        public void ABoxedPrimitive_IsStillEqualToItsUnboxedSelf()
        {
            using var runtime = new SurtrRuntime();
            var boxed = runtime.Box(SurtrValue.CreateInt(5));

            Assert.True(runtime.ValueComparer.ValuesEqual(
                SurtrValue.CreateReference(boxed.GetSurtrReference()),
                SurtrValue.CreateInt(5)));
        }

        #endregion
    }
}
