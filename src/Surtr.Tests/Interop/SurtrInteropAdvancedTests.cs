#nullable enable

using Surtr.Interop;
using Surtr.Interop.Attributes;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
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
    }
}