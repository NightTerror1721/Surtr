#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using Xunit;

namespace Surtr.Tests.Runtime.Classes
{
    /// <summary>
    /// Covers <see cref="SurtrRuntime.DefineNativeValueClass"/> - a host struct exposed as an
    /// inline value type rather than boxed behind a <see cref="SurtrNativeObject"/> proxy.
    /// </summary>
    /// <remarks>
    /// The point of the shape is that <b>Surtr owns the storage</b>: the fields are real slots, so
    /// reading one never crosses into host code, and the CLR struct is rebuilt from those slots
    /// only when a native member needs one.
    /// </remarks>
    public class SurtrNativeValueClassTests
    {
        private static SurtrClass Vector3(SurtrRuntime runtime, string name = "UnityEngine:Vector3")
        {
            var type = runtime.DefineNativeValueClass(name);
            runtime.DefineValueField(type, "x", SurtrClassReference.Float);
            runtime.DefineValueField(type, "y", SurtrClassReference.Float);
            runtime.DefineValueField(type, "z", SurtrClassReference.Float);
            runtime.FinishNativeClass(type);
            return type;
        }

        [Fact]
        public void ItLinksAsAnInlineBlockOfItsFields()
        {
            using var runtime = new SurtrRuntime();
            var vector3 = Vector3(runtime);

            Assert.True(vector3.IsValueType);
            Assert.Equal(3, vector3.FlattenedSlotWidth);
            Assert.Equal(3, vector3.InstanceFields.Length);
        }

        /// <summary>
        /// The class is <c>Object</c>, not <c>Native</c>: the type code describes the boxed form,
        /// and boxing an inline block yields an ordinary instance holding those same slots - not a
        /// proxy around a CLR object the block was never backed by.
        /// </summary>
        [Fact]
        public void ItsTypeCodeIsObjectSoItsBoxedFormIsAnOrdinaryInstance()
        {
            using var runtime = new SurtrRuntime();
            var vector3 = Vector3(runtime);

            Assert.Equal(SurtrValueTypeCode.Object, vector3.TypeCode);

            var boxed = runtime.NewInstance(vector3);
            Assert.Equal(3, boxed.SlotCount);
            Assert.IsType<SurtrInstance>(boxed);
        }

        /// <summary>Fields claim their slots in declaration order, which is what the marshaler rebuilds in.</summary>
        [Fact]
        public void FieldsTakeTheirSlotsInDeclarationOrder()
        {
            using var runtime = new SurtrRuntime();
            var vector3 = Vector3(runtime);

            Assert.True(vector3.TryGetField("x", out var x));
            Assert.True(vector3.TryGetField("y", out var y));
            Assert.True(vector3.TryGetField("z", out var z));

            Assert.Equal(0, x.Slot);
            Assert.Equal(1, y.Slot);
            Assert.Equal(2, z.Slot);
        }

        /// <summary>An inline value is immutable, so its fields are read-only from Surtr.</summary>
        [Fact]
        public void ItsFieldsAreReadOnly()
        {
            using var runtime = new SurtrRuntime();
            var vector3 = Vector3(runtime);

            Assert.True(vector3.TryGetField("x", out var x));
            Assert.True(x.IsReadOnly);
        }

        /// <summary>
        /// A nested value class folds its own slots into the run, exactly as a compiled one does -
        /// the layout path is shared, so nothing here knows the declaration came from a host.
        /// </summary>
        [Fact]
        public void ANestedValueClassFoldsItsSlotsIntoTheBlock()
        {
            using var runtime = new SurtrRuntime();
            var vector3 = Vector3(runtime);

            var bounds = runtime.DefineNativeValueClass("UnityEngine:Bounds");
            runtime.DefineValueField(bounds, "center", vector3.SelfReference);
            runtime.DefineValueField(bounds, "extents", vector3.SelfReference);
            runtime.FinishNativeClass(bounds);

            Assert.Equal(6, bounds.FlattenedSlotWidth);
        }

        /// <summary>
        /// A single-field value class erases to the field it wraps and stays one slot, the same
        /// rule a compiled one follows - so exposing a thin host wrapper costs nothing.
        /// </summary>
        [Fact]
        public void ASingleFieldValueClassStaysOneSlot()
        {
            using var runtime = new SurtrRuntime();

            var entityId = runtime.DefineNativeValueClass("game:EntityId");
            runtime.DefineValueField(entityId, "raw", SurtrClassReference.Integer);
            runtime.FinishNativeClass(entityId);

            Assert.Equal(1, entityId.FlattenedSlotWidth);
        }

        /// <summary>A value type has no identity to inherit through, so the linker refuses a base.</summary>
        [Fact]
        public void AValueFieldNeedsAValueClass()
        {
            using var runtime = new SurtrRuntime();
            var ordinary = runtime.DefineNativeClass("game:Thing");

            Assert.Throws<ArgumentException>(
                () => runtime.DefineValueField(ordinary, "x", SurtrClassReference.Integer));
        }

        /// <summary>
        /// A native method over the value class counts its receiver's whole block, so a call site
        /// emitted against it leaves the right number of slots.
        /// </summary>
        [Fact]
        public void ANativeMemberCountsTheBlockAsItsReceiver()
        {
            using var runtime = new SurtrRuntime();
            var vector3 = Vector3(runtime);

            var method = new SurtrNativeMethodInfo(
                "magnitude",
                SurtrMethodDispatch.Direct,
                SurtrMethodRole.Normal,
                isOverride: false,
                returnType: runtime.TypeHandle(SurtrClassReference.Float),
                parameters: Array.Empty<SurtrParameterInfo>(),
                isStatic: false,
                SurtrVisibility.Public,
                declaringType: runtime.TypeHandle(vector3.SelfReference),
                entryPoint: SurtrNativeEntryPoint.FromDelegate(Magnitude));

            Assert.Equal(3, method.ArgumentSlotCount);
        }

        private static int Magnitude(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateFloat(0));
    }
}
