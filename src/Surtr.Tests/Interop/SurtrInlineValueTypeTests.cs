#nullable enable

using Surtr.Interop;
using Surtr.Interop.Attributes;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Linq;
using Xunit;

namespace Surtr.Tests.Interop
{
    /// <summary>
    /// Covers <c>[SurtrNativeType(Inline = true)]</c>: a CLR struct exposed as a Surtr value type -
    /// a run of contiguous slots Surtr owns - rather than boxed behind a native proxy.
    /// </summary>
    public class SurtrInlineValueTypeTests
    {
        [SurtrNativeType(Module = "unity", Name = "Vector3", Inline = true)]
        public struct Vector3
        {
            public float X;
            public float Y;
            public float Z;

            public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }

            public static Vector3 Of(float x, float y, float z) => new(x, y, z);

            public float SqrMagnitude() => (X * X) + (Y * Y) + (Z * Z);

            public Vector3 Normalizedish => new(X / 2f, Y / 2f, Z / 2f);

            public float Sum => X + Y + Z;

            public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        }

        [SurtrNativeType(Module = "unity", Name = "Bounds", Inline = true)]
        public struct Bounds
        {
            public Vector3 Center;
            public Vector3 Extents;

            public float Volume() => 8f * Extents.X * Extents.Y * Extents.Z;
        }

        /// <summary>The same struct exposed the old way, to pin that nothing changed without opt-in.</summary>
        [SurtrNativeType(Module = "unity", Name = "BoxedVector")]
        public struct BoxedVector
        {
            public float X;
            public float Y;
        }

        #region Scanning

        [Fact]
        public void AnInlineStructScansItsFieldsAsSlotsRatherThanAccessors()
        {
            var descriptor = SurtrReflectionScanner.Scan(typeof(Vector3));

            Assert.True(descriptor.IsInline);
            Assert.Equal(typeof(Vector3), descriptor.ClrType);

            var slots = descriptor.Members.OfType<NativeValueFieldDescriptor>().ToList();
            Assert.Equal(3, slots.Count);
            Assert.Equal(new[] { "x", "y", "z" }, slots.Select(static s => s.Name));
            Assert.All(slots, static s => Assert.Equal(SurtrClassReference.Float.Descriptor, s.TypeDescriptor));

            // Its methods and operators are still ordinary native members.
            Assert.Contains(descriptor.Members, static m => m is NativeMethodDescriptor { Name: "sqrMagnitude" });
            Assert.Contains(descriptor.Members, static m => m is NativeMethodDescriptor { Name: "op_+" });

            // Nothing became an accessor-backed native field.
            Assert.DoesNotContain(descriptor.Members, static m => m is NativeFieldDescriptor);
        }

        /// <summary>Without the opt-in a struct is boxed exactly as it always was.</summary>
        [Fact]
        public void AStructWithoutTheOptInIsUnchanged()
        {
            var descriptor = SurtrReflectionScanner.Scan(typeof(BoxedVector));

            Assert.False(descriptor.IsInline);
            Assert.Equal(NativeTypeKind.Struct, descriptor.Kind);
            Assert.Equal(2, descriptor.Members.OfType<NativeFieldDescriptor>().Count());
            Assert.Empty(descriptor.Members.OfType<NativeValueFieldDescriptor>());
        }

        [SurtrNativeType(Inline = true)]
        public struct HasAReference
        {
            public int Id;
            public string Label;
        }

        /// <summary>
        /// A field with no inline representation is refused outright rather than half-exposed: the
        /// CLR struct could not be rebuilt from a block that is missing one of its fields.
        /// </summary>
        [Fact]
        public void AFieldWithNoInlineRepresentationIsRefused()
        {
            var error = Assert.Throws<InvalidOperationException>(() => SurtrReflectionScanner.Scan(typeof(HasAReference)));
            Assert.Contains("no inline representation", error.Message, StringComparison.Ordinal);
        }

        [SurtrNativeType(Inline = true)]
        public struct HasAHiddenField
        {
            public int Visible;

            [SurtrNativeIgnore]
            public int Hidden;
        }

        /// <summary>Hiding a field would drop a slot out of the middle of the block.</summary>
        [Fact]
        public void AHiddenFieldIsRefused()
        {
            var error = Assert.Throws<InvalidOperationException>(() => SurtrReflectionScanner.Scan(typeof(HasAHiddenField)));
            Assert.Contains("cannot be hidden", error.Message, StringComparison.Ordinal);
        }

        [SurtrNativeType(Inline = true)]
        public struct Empty
        {
        }

        /// <summary>An inline value type is its fields, so one with none of them is nothing.</summary>
        [Fact]
        public void AFieldlessInlineStructIsRefused()
        {
            var error = Assert.Throws<InvalidOperationException>(() => SurtrReflectionScanner.Scan(typeof(Empty)));
            Assert.Contains("no instance field", error.Message, StringComparison.Ordinal);
        }

        [SurtrNativeType(Inline = true)]
        public class NotAStruct
        {
            public int X;
        }

        /// <summary>Only a struct has an inline representation to ask for.</summary>
        [Fact]
        public void InlineOnAClassIsRefused()
        {
            var error = Assert.Throws<InvalidOperationException>(() => SurtrReflectionScanner.Scan(typeof(NotAStruct)));
            Assert.Contains("only a struct", error.Message, StringComparison.Ordinal);
        }

        #endregion

        #region Materializing

        [Fact]
        public void AnInlineStructMaterializesAsAValueClassOfItsFields()
        {
            using var runtime = new SurtrRuntime();
            var vector3 = SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(Vector3)));

            Assert.True(vector3.IsValueType);
            Assert.Equal(3, vector3.FlattenedSlotWidth);

            Assert.True(vector3.TryGetField("x", out var x));
            Assert.True(vector3.TryGetField("z", out var z));
            Assert.Equal(0, x.Slot);
            Assert.Equal(2, z.Slot);

            // A slot, not a native field: reading it never crosses into host code.
            Assert.IsNotType<SurtrNativeFieldInfo>(x);
        }

        /// <summary>
        /// A nested inline struct folds its own slots into the run, so a <c>Bounds</c> is six
        /// floats rather than two references.
        /// </summary>
        [Fact]
        public void ANestedInlineStructFoldsItsSlotsIn()
        {
            using var runtime = new SurtrRuntime();

            // Registration order matters, the same way a base class already needs it.
            SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(Vector3)));
            var bounds = SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(Bounds)));

            Assert.True(bounds.IsValueType);
            Assert.Equal(6, bounds.FlattenedSlotWidth);
        }

        /// <summary>
        /// An instance method on the value class counts the whole block as its receiver, so a call
        /// site emitted against it leaves the right number of slots.
        /// </summary>
        [Fact]
        public void AMemberOfAnInlineStructTakesTheBlockAsItsReceiver()
        {
            using var runtime = new SurtrRuntime();
            var vector3 = SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(Vector3)));

            Assert.True(vector3.TryGetMethods("sqrMagnitude", out var overloads));
            Assert.Equal(3, overloads[0].ArgumentSlotCount);
        }

        /// <summary>The boxed form is an ordinary instance holding the same slots, not a proxy.</summary>
        [Fact]
        public void ItsBoxedFormIsAnOrdinaryInstance()
        {
            using var runtime = new SurtrRuntime();
            var vector3 = SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(Vector3)));

            Assert.Equal(SurtrValueTypeCode.Object, vector3.TypeCode);
            Assert.Equal(3, runtime.NewInstance(vector3).SlotCount);
        }

        #endregion
    
        #region Invoking

        private static (SurtrRuntime Runtime, SurtrClass Type) Registered()
        {
            var runtime = new SurtrRuntime();
            var type = SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(Vector3)));
            return (runtime, type);
        }

        private static SurtrValue[] Block(float x, float y, float z)
            => new[] { SurtrValue.CreateFloat(x), SurtrValue.CreateFloat(y), SurtrValue.CreateFloat(z) };

        /// <summary>
        /// An instance method rebuilds its receiver out of the argument slots - there is no
        /// reference to resolve, the block <em>is</em> the receiver.
        /// </summary>
        [Fact]
        public void AnInstanceMethodRebuildsItsReceiverFromTheBlock()
        {
            var (runtime, type) = Registered();
            using (runtime)
            {
                Assert.True(type.TryGetMethods("sqrMagnitude", out var overloads));

                var result = runtime.Invoke(overloads[0], Block(1, 2, 3));

                Assert.Equal(14.0, result.AsFloat, 6);
            }
        }

        /// <summary>
        /// An operator takes two whole blocks and answers a third, so its call frame is six slots
        /// in and three out - the case that breaks entirely if arguments are walked one per slot.
        /// </summary>
        [Fact]
        public void AnOperatorTakesTwoBlocksAndAnswersOne()
        {
            var (runtime, type) = Registered();
            using (runtime)
            {
                Assert.True(type.TryGetMethods("op_+", out var overloads));
                var plus = overloads[0];

                Assert.Equal(6, plus.ArgumentSlotCount);
                Assert.Equal(3, plus.ResultSlotCount);

                var results = new SurtrValue[3];
                Assert.True(runtime.TryInvoke(
                    plus,
                    new[]
                    {
                        SurtrValue.CreateFloat(1), SurtrValue.CreateFloat(2), SurtrValue.CreateFloat(3),
                        SurtrValue.CreateFloat(10), SurtrValue.CreateFloat(20), SurtrValue.CreateFloat(30),
                    },
                    results));

                Assert.Equal(11.0, results[0].AsFloat, 6);
                Assert.Equal(22.0, results[1].AsFloat, 6);
                Assert.Equal(33.0, results[2].AsFloat, 6);
            }
        }

        /// <summary>
        /// An inline result crossing back to the host is re-packed into one boxed instance, so a
        /// C# caller still gets a single value - the same bargain every multi-slot return makes.
        /// </summary>
        [Fact]
        public void AnInlineResultRepacksForTheHostBoundary()
        {
            var (runtime, type) = Registered();
            using (runtime)
            {
                Assert.True(type.TryGetMethods("op_+", out var overloads));

                var boxed = runtime.Resolve<SurtrInstance>(runtime.Invoke(
                    overloads[0],
                    SurtrValue.CreateFloat(1), SurtrValue.CreateFloat(2), SurtrValue.CreateFloat(3),
                    SurtrValue.CreateFloat(10), SurtrValue.CreateFloat(20), SurtrValue.CreateFloat(30)));

                Assert.NotNull(boxed);
                Assert.Equal(3, boxed!.SlotCount);
                Assert.Equal(11.0, boxed[0].AsFloat, 6);
                Assert.Equal(33.0, boxed[2].AsFloat, 6);
            }
        }

        /// <summary>A nested inline struct is read out of one flat run of six slots.</summary>
        [Fact]
        public void ANestedBlockIsReadFromOneFlatRun()
        {
            using var runtime = new SurtrRuntime();
            SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(Vector3)));
            var bounds = SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(Bounds)));

            Assert.True(bounds.TryGetMethods("volume", out var overloads));
            Assert.Equal(6, overloads[0].ArgumentSlotCount);

            var result = runtime.Invoke(
                overloads[0],
                SurtrValue.CreateFloat(0), SurtrValue.CreateFloat(0), SurtrValue.CreateFloat(0),
                SurtrValue.CreateFloat(2), SurtrValue.CreateFloat(3), SurtrValue.CreateFloat(4));

            // Extents are half-sizes, so the volume is 8 * 2 * 3 * 4.
            Assert.Equal(192.0, result.AsFloat, 6);
        }


        /// <summary>
        /// A constructor is deliberately not exposed on an inline value type, and a static factory
        /// is the shape that works: it returns the block flat, with nothing to allocate.
        /// </summary>
        /// <remarks>
        /// A Surtr constructor is reached by allocating first and running the body against the new
        /// instance as its receiver. An inline value has nothing to allocate and no receiver to
        /// fill - it <em>is</em> its result - so exposing one would need a construction protocol
        /// this layer does not have. Refused rather than exposed broken.
        /// </remarks>
        [Fact]
        public void NoConstructorIsExposedButAStaticFactoryWorks()
        {
            var (runtime, type) = Registered();
            using (runtime)
            {
                Assert.False(type.TryGetMethods("ctor", out _));

                Assert.True(type.TryGetMethods("of", out var overloads));
                var factory = overloads[0];

                Assert.Equal(3, factory.ArgumentSlotCount);
                Assert.Equal(3, factory.ResultSlotCount);

                var results = new SurtrValue[3];
                Assert.True(runtime.TryInvoke(
                    factory,
                    new[] { SurtrValue.CreateFloat(4), SurtrValue.CreateFloat(5), SurtrValue.CreateFloat(6) },
                    results));

                Assert.Equal(4.0, results[0].AsFloat, 6);
                Assert.Equal(5.0, results[1].AsFloat, 6);
                Assert.Equal(6.0, results[2].AsFloat, 6);
            }
        }


        /// <summary>
        /// A property getter rebuilds the receiver from the block too, and one returning another
        /// inline struct hands back its own flat run.
        /// </summary>
        [Fact]
        public void APropertyReadsTheBlockAndCanAnswerAnother()
        {
            var (runtime, type) = Registered();
            using (runtime)
            {
                Assert.True(type.TryGetProperty("normalizedish", out _));
                Assert.True(type.TryGetMethods("get_normalizedish", out var getters));
                Assert.Equal(3, getters[0].ArgumentSlotCount);
                Assert.Equal(3, getters[0].ResultSlotCount);

                var results = new SurtrValue[3];
                Assert.True(runtime.TryInvoke(getters[0], Block(2, 4, 6), results));

                Assert.Equal(1.0, results[0].AsFloat, 6);
                Assert.Equal(2.0, results[1].AsFloat, 6);
                Assert.Equal(3.0, results[2].AsFloat, 6);
            }
        }

        /// <summary>A scalar property over an inline receiver still reads the block.</summary>
        [Fact]
        public void AScalarPropertyReadsTheBlock()
        {
            var (runtime, type) = Registered();
            using (runtime)
            {
                Assert.True(type.TryGetMethods("get_sum", out var getters));
                Assert.Equal(3, getters[0].ArgumentSlotCount);

                Assert.Equal(6.0, runtime.Invoke(getters[0], Block(1, 2, 3)).AsFloat, 6);
            }
        }

        #endregion
    }
}
