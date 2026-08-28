#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Tests.VM;
using Xunit;

namespace Surtr.Tests.Runtime.Classes
{
    /// <summary>
    /// Covers <see cref="SurtrMethodInfo.ArgumentSlotCount"/> over signatures that carry inline
    /// values - the width a call site has to leave, receiver included.
    /// </summary>
    /// <remarks>
    /// Regression: this used to be a bare <c>ParameterCount + 1</c>. A compiled method overrides it
    /// with the width its emitter computed, so the naive answer was invisible there; a native one
    /// never did, so a host declaring a native method over a multi-field <c>value class</c> got a
    /// count short by the difference, and every call site the compiler emitted against it was
    /// mis-sized. Nothing on the execution path reads this - the interpreter takes
    /// <c>argsCount</c> from the instruction - which is exactly why it went unnoticed.
    /// </remarks>
    public class SurtrMethodInfoSlotWidthTests
    {
        private static int Body(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateInt(0));

        /// <summary>A linked two-field value class: one int and one float, so two slots.</summary>
        private static SurtrClass LinkedVec2(SurtrModule module)
        {
            var type = VmMetadataHelpers.DefineClass(module, "Vec2");
            type.IsValueType = true;
            type.AddField(VmMetadataHelpers.Field(module, "x", SurtrClassReference.Integer));
            type.AddField(VmMetadataHelpers.Field(module, "y", SurtrClassReference.Float));

            int nextInterfaceId = SurtrBuiltIns.ReservedInterfaceIds;
            SurtrTypeLinker.LinkClass(type, ref nextInterfaceId);
            return type;
        }

        private static SurtrNativeMethodInfo Native(
            SurtrModule module,
            SurtrClass declaringType,
            bool isStatic,
            params SurtrParameterInfo[] parameters)
            => new(
                "probe",
                SurtrMethodDispatch.Direct,
                SurtrMethodRole.Normal,
                isOverride: false,
                returnType: VmMetadataHelpers.HandleFor(module, SurtrClassReference.Integer),
                parameters: parameters,
                isStatic: isStatic,
                SurtrVisibility.Public,
                declaringType: VmMetadataHelpers.HandleFor(module, declaringType),
                entryPoint: SurtrNativeEntryPoint.FromDelegate(Body));

        [Fact]
        public void AValueClassParameterCountsItsWholeWidth()
        {
            var module = new SurtrModule("test");
            var vec2 = LinkedVec2(module);
            Assert.Equal(2, vec2.FlattenedSlotWidth);

            var method = Native(
                module,
                VmMetadataHelpers.DefineClass(module, "Holder"),
                isStatic: true,
                new SurtrParameterInfo("v", VmMetadataHelpers.HandleFor(module, vec2)));

            Assert.Equal(2, method.ArgumentSlotCount);
        }

        /// <summary>
        /// An instance method on a multi-field value class receives its block unboxed, which is
        /// the rule the compiler's own <c>ApplyValueLayout</c> applies - the two have to agree or a
        /// call emitted against the metadata would not match the frame the callee expects.
        /// </summary>
        [Fact]
        public void AValueClassReceiverCountsItsWholeWidth()
        {
            var module = new SurtrModule("test");
            var vec2 = LinkedVec2(module);

            var method = Native(module, vec2, isStatic: false);

            Assert.Equal(2, method.ArgumentSlotCount);
        }

        [Fact]
        public void AValueClassReceiverAndParameterBothCount()
        {
            var module = new SurtrModule("test");
            var vec2 = LinkedVec2(module);

            var method = Native(
                module,
                vec2,
                isStatic: false,
                new SurtrParameterInfo("other", VmMetadataHelpers.HandleFor(module, vec2)),
                new SurtrParameterInfo("scale", VmMetadataHelpers.HandleFor(module, SurtrClassReference.Float)));

            // Two for the receiver, two for the other Vec2, one for the float.
            Assert.Equal(5, method.ArgumentSlotCount);
        }

        /// <summary>A tuple answers from its descriptor alone, with no linked class to consult.</summary>
        [Fact]
        public void ATupleParameterCountsItsFlattenedWidth()
        {
            var module = new SurtrModule("test");
            var tuple = SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.String);

            var method = Native(
                module,
                VmMetadataHelpers.DefineClass(module, "Holder"),
                isStatic: true,
                new SurtrParameterInfo("pair", VmMetadataHelpers.HandleFor(module, tuple)));

            Assert.Equal(2, method.ArgumentSlotCount);
        }

        /// <summary>
        /// A varargs parameter is one slot whatever its element type: the caller packs the surplus
        /// into an array, and an array is a reference.
        /// </summary>
        [Fact]
        public void AVarargsParameterStaysOneSlot()
        {
            var module = new SurtrModule("test");
            var vec2 = LinkedVec2(module);

            var method = Native(
                module,
                VmMetadataHelpers.DefineClass(module, "Holder"),
                isStatic: true,
                new SurtrParameterInfo("rest", VmMetadataHelpers.HandleFor(module, vec2), SurtrConstant.None, isVarargs: true));

            Assert.Equal(1, method.ArgumentSlotCount);
        }

        /// <summary>
        /// A one-field value class erases to the field it wraps, so it is one slot and the count is
        /// unchanged - the case the whole design keeps free of regression.
        /// </summary>
        [Fact]
        public void ASingleFieldValueClassStaysOneSlot()
        {
            var module = new SurtrModule("test");

            var angle = VmMetadataHelpers.DefineClass(module, "Angle");
            angle.IsValueType = true;
            angle.AddField(VmMetadataHelpers.Field(module, "radians", SurtrClassReference.Float));

            int nextInterfaceId = SurtrBuiltIns.ReservedInterfaceIds;
            SurtrTypeLinker.LinkClass(angle, ref nextInterfaceId);

            var method = Native(
                module,
                VmMetadataHelpers.DefineClass(module, "Holder"),
                isStatic: true,
                new SurtrParameterInfo("a", VmMetadataHelpers.HandleFor(module, angle)));

            Assert.Equal(1, method.ArgumentSlotCount);
        }

        /// <summary>
        /// An ordinary class parameter is a reference and stays one slot, receiver included.
        /// </summary>
        [Fact]
        public void OrdinaryReferencesAreOneSlotEach()
        {
            var module = new SurtrModule("test");
            var holder = VmMetadataHelpers.DefineClass(module, "Holder");

            var method = Native(
                module,
                holder,
                isStatic: false,
                new SurtrParameterInfo("other", VmMetadataHelpers.HandleFor(module, holder)),
                new SurtrParameterInfo("n", VmMetadataHelpers.HandleFor(module, SurtrClassReference.Integer)));

            Assert.Equal(3, method.ArgumentSlotCount);
        }

        /// <summary>
        /// An unresolved handle falls back to one slot rather than guessing, and the answer becomes
        /// final once linking has run - which is why the width is derived on every read instead of
        /// being cached at construction.
        /// </summary>
        [Fact]
        public void AnUnresolvedValueClassFallsBackToOneSlotUntilItLinks()
        {
            var module = new SurtrModule("test");

            var vec2 = VmMetadataHelpers.DefineClass(module, "Vec2");
            vec2.IsValueType = true;
            vec2.AddField(VmMetadataHelpers.Field(module, "x", SurtrClassReference.Integer));
            vec2.AddField(VmMetadataHelpers.Field(module, "y", SurtrClassReference.Float));

            var handle = VmMetadataHelpers.HandleFor(module, vec2);

            var method = Native(
                module,
                VmMetadataHelpers.DefineClass(module, "Holder"),
                isStatic: true,
                new SurtrParameterInfo("v", handle));

            // Not linked yet: the layout does not exist, so there is no width to read.
            Assert.Equal(1, method.ArgumentSlotCount);

            int nextInterfaceId = SurtrBuiltIns.ReservedInterfaceIds;
            SurtrTypeLinker.LinkClass(vec2, ref nextInterfaceId);

            // Linked: the same metadata now answers the real width.
            Assert.Equal(2, method.ArgumentSlotCount);
        }
    }
}
