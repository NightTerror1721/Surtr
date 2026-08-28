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
    public class SurtrInteropAdvancedTests
    {
        [SurtrNativeType]
        public class Vec2
        {
            public float X;
            public float Y;

            public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2 { X = a.X + b.X, Y = a.Y + b.Y };

            public float this[int i] => i == 0 ? X : Y;
        }

        [Fact]
        public void Operators_AreMappedToSurtrOperatorNames()
        {
            using var runtime = new SurtrRuntime();

            var descriptor = SurtrReflectionScanner.Scan(typeof(Vec2));
            Assert.Contains(descriptor.Members, static m => m is NativeMethodDescriptor { Name: "op_+" });
            Assert.Contains(descriptor.Members, static m => m is NativeMethodDescriptor { Name: "op_[]" });

            var vec2 = SurtrBridge.Register(runtime, descriptor);
            Assert.True(vec2.TryGetMethods("op_+", out _));
            Assert.True(vec2.TryGetMethods("op_[]", out _));
        }

        [SurtrNativeType]
        public class ShapeBase
        {
            public virtual int Area() => 0;
        }

        [SurtrNativeType]
        public class Square : ShapeBase
        {
            public int Side;

            public override int Area() => Side * Side;
        }

        [Fact]
        public void VirtualAndOverride_MethodsAreMarked()
        {
            var baseDescriptor = SurtrReflectionScanner.Scan(typeof(ShapeBase));
            var baseArea = baseDescriptor.Members.OfType<NativeMethodDescriptor>().Single(static m => m.Name == "area");
            Assert.True(baseArea.IsVirtual);

            var squareDescriptor = SurtrReflectionScanner.Scan(typeof(Square));
            var squareArea = squareDescriptor.Members.OfType<NativeMethodDescriptor>().Single(static m => m.Name == "area");
            Assert.True(squareArea.IsVirtual);
            Assert.True(squareArea.IsOverride);
        }

        [SurtrNativeType]
        public class Box<T>
        {
            public T Value = default!;
        }

        [Fact]
        public void ClosedGeneric_PopulatesTypeArguments()
        {
            var descriptor = SurtrReflectionScanner.Scan(typeof(Box<int>));
            Assert.Equal("Box`1", descriptor.Name);
            Assert.Equal(new[] { "I" }, descriptor.TypeArguments);

            using var runtime = new SurtrRuntime();
            var box = SurtrBridge.Register(runtime, descriptor);
            Assert.True(runtime.TryGetNativeClass("Box`1", out _));
            Assert.Equal("NBox`1;I", box.SelfReference.Descriptor);
        }

        [SurtrNativeType]
        public class Callbacks
        {
            public int Apply(int x, Func<int, int> f) => f(x);
        }

        [Fact]
        public void DelegateParameter_MapsToAClosureDescriptor()
        {
            var descriptor = SurtrReflectionScanner.Scan(typeof(Callbacks));
            var apply = descriptor.Members.OfType<NativeMethodDescriptor>().Single(static m => m.Name == "apply");

            Assert.Equal("L(I)I", apply.Parameters[1].TypeDescriptor);

            using var runtime = new SurtrRuntime();
            var callbacks = SurtrBridge.Register(runtime, descriptor);
            Assert.True(callbacks.TryGetMethods("apply", out _));
        }

        [SurtrNativeType]
        public class Snake
        {
            public int CamelValue;
        }

        [Fact]
        public void RuntimeScopeNamingPolicy_OverridesTheDefault()
        {
            var descriptor = SurtrReflectionScanner.Scan(typeof(Snake), SurtrNamingPolicy.PascalCase);
            Assert.Contains(descriptor.Members, static m => m is NativeFieldDescriptor { Name: "CamelValue" });
            Assert.DoesNotContain(descriptor.Members, static m => m is NativeFieldDescriptor { Name: "camelValue" });
        }

        [SurtrNativeType]
        public class Price : IComparable<Price>
        {
            public int Value;

            public int CompareTo(Price? other) => Value.CompareTo(other?.Value ?? 0);
        }

        [Fact]
        public void IComparable_CompareToMapsToSpaceshipOperator()
        {
            var descriptor = SurtrReflectionScanner.Scan(typeof(Price));
            Assert.Contains(descriptor.Members, static m => m is NativeMethodDescriptor { Name: "op_<=>" });
            Assert.DoesNotContain(descriptor.Members, static m => m is NativeMethodDescriptor { Name: "compareTo" });

            using var runtime = new SurtrRuntime();
            var price = SurtrBridge.Register(runtime, descriptor);
            Assert.True(price.TryGetMethods("op_<=>", out _));
        }
    
        #region Out-parameters

        [SurtrNativeType]
        public class Lookup
        {
            /// <summary>The shape that crashed: a non-void return plus one out-parameter.</summary>
            public bool TryGet(out int value) { value = 42; return true; }

            /// <summary>Two out-parameters and a void return - the tuple has no leading result.</summary>
            public void Split(out int whole, out float fraction) { whole = 3; fraction = 0.5f; }

            /// <summary>One out-parameter and a void return, which is a scalar rather than a tuple.</summary>
            public void Only(out int value) { value = 7; }

            /// <summary>No out-parameters at all - the ordinary single-slot path.</summary>
            public int Plain() => 9;
        }

        private static SurtrMethodInfo Method(SurtrClass type, string name)
        {
            Assert.True(type.TryGetMethods(name, out var overloads));
            return Assert.Single(overloads);
        }

        private static (SurtrRuntime Runtime, SurtrClass Type, SurtrNativeObject Instance) Lookups()
        {
            var runtime = new SurtrRuntime();
            var type = SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(Lookup)));
            return (runtime, type, runtime.WrapNative(type, new Lookup()));
        }

        /// <summary>
        /// A non-void return plus one out-parameter comes back as a two-element tuple, with each
        /// element marshalled against its <em>own</em> descriptor.
        /// </summary>
        /// <remarks>
        /// Regression: the two elements were written with <c>elements[next++] = f(elementTypes[next])</c>,
        /// and C# sequences the left-hand index before the right-hand operand — so the increment landed
        /// first, every element was converted against its successor's type, and the last one indexed
        /// past the end of the descriptor list. The types here are deliberately different: converting
        /// the <c>bool</c> against the <c>int</c> descriptor yields 1 rather than failing, so a test
        /// over two elements of one type would have passed through the bug.
        /// </remarks>
        [Fact]
        public void AnOutParameterBesideAResultComesBackAsATuple()
        {
            var (runtime, type, instance) = Lookups();
            using (runtime)
            {
                var method = Method(type, "tryGet");
                Assert.Equal("T(BI)", method.ReturnType.Reference.Descriptor);
                Assert.Equal(2, method.ResultSlotCount);

                var tuple = runtime.Resolve<SurtrTuple>(
                    runtime.Invoke(method, SurtrValue.CreateReference(instance.GetSurtrReference())));

                Assert.NotNull(tuple);
                Assert.Equal(2, tuple!.Length);
                Assert.True(tuple[0].IsBool);
                Assert.True(tuple[0].AsBool);
                Assert.Equal(42, tuple[1].AsInt);
            }
        }

        /// <summary>Two out-parameters over a void return fill the tuple with no leading result.</summary>
        [Fact]
        public void TwoOutParametersFillTheWholeTuple()
        {
            var (runtime, type, instance) = Lookups();
            using (runtime)
            {
                var method = Method(type, "split");
                Assert.Equal("T(IF)", method.ReturnType.Reference.Descriptor);
                Assert.Equal(2, method.ResultSlotCount);

                var tuple = runtime.Resolve<SurtrTuple>(
                    runtime.Invoke(method, SurtrValue.CreateReference(instance.GetSurtrReference())));

                Assert.NotNull(tuple);
                Assert.Equal(3, tuple![0].AsInt);
                Assert.Equal(0.5, tuple[1].AsFloat, 6);
            }
        }

        /// <summary>
        /// A lone out-parameter over a void return is the value itself, not a one-element tuple —
        /// the path that never went through the tuple builder and so was never broken.
        /// </summary>
        [Fact]
        public void ALoneOutParameterIsTheResultItself()
        {
            var (runtime, type, instance) = Lookups();
            using (runtime)
            {
                var method = Method(type, "only");
                Assert.Equal("I", method.ReturnType.Reference.Descriptor);
                Assert.Equal(1, method.ResultSlotCount);

                Assert.Equal(
                    7,
                    runtime.Invoke(method, SurtrValue.CreateReference(instance.GetSurtrReference())).AsInt);
            }
        }

        /// <summary>A method with no out-parameters keeps the ordinary one-slot answer.</summary>
        [Fact]
        public void AMethodWithNoOutParametersIsUnaffected()
        {
            var (runtime, type, instance) = Lookups();
            using (runtime)
            {
                var method = Method(type, "plain");
                Assert.Equal(1, method.ResultSlotCount);

                Assert.Equal(
                    9,
                    runtime.Invoke(method, SurtrValue.CreateReference(instance.GetSurtrReference())).AsInt);
            }
        }

        /// <summary>
        /// The result block is written flat, so the slots the caller copies back are the tuple's
        /// own elements rather than one reference and whatever the stack held after it.
        /// </summary>
        /// <remarks>
        /// The body used to pack a <see cref="SurtrTuple"/> and answer one slot, which was right
        /// before a tuple became a value type and wrong afterwards: <c>ResultSlotCount</c> is the
        /// flattened width, so the caller copies that many slots regardless. Reading the block
        /// through <c>TryInvoke</c> is what pins the width, since <c>Invoke</c> re-packs it.
        /// </remarks>
        [Fact]
        public void TheResultBlockIsWrittenFlatRatherThanAsAPackedTuple()
        {
            var (runtime, type, instance) = Lookups();
            using (runtime)
            {
                var method = Method(type, "tryGet");
                var results = new SurtrValue[method.ResultSlotCount];

                Assert.True(runtime.TryInvoke(
                    method,
                    new[] { SurtrValue.CreateReference(instance.GetSurtrReference()) },
                    results));

                // Each slot is the element itself. A packed tuple would put a reference in slot 0
                // and leave slot 1 untouched.
                Assert.True(results[0].IsBool);
                Assert.True(results[0].AsBool);
                Assert.Equal(42, results[1].AsInt);
            }
        }

        #endregion
    }
}
