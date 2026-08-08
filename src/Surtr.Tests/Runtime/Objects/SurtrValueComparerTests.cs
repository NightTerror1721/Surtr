#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.Runtime.Objects
{
    public sealed class SurtrValueComparerTests : IDisposable
    {
        private readonly SurtrRuntime _runtime = new();

        public void Dispose() => _runtime.Dispose();

        private SurtrValueComparer Comparer => _runtime.ValueComparer;

        [Fact]
        public void Ints_WithEqualPayload_AreEqual()
        {
            SurtrValue left = SurtrValue.CreateInt(5);
            SurtrValue right = SurtrValue.CreateInt(5);

            Assert.True(Comparer.ValuesEqual(left, right));
            Assert.Equal(Comparer.HashOf(left), Comparer.HashOf(right));
        }

        [Fact]
        public void Ints_WithDifferentPayload_AreNotEqual()
        {
            Assert.False(Comparer.ValuesEqual(SurtrValue.CreateInt(5), SurtrValue.CreateInt(6)));
        }

        [Fact]
        public void SamePayload_DifferentTag_IsNotEqual()
        {
            // An int 5 and a char 5 share a numeric payload but are not the same Surtr value.
            SurtrValue five = SurtrValue.CreateInt(5);
            SurtrValue charFive = SurtrValue.CreateChar((char)5);

            Assert.False(Comparer.ValuesEqual(five, charFive));
        }

        [Fact]
        public void PositiveAndNegativeZero_AreEqual()
        {
            SurtrValue positive = SurtrValue.CreateFloat(0.0);
            SurtrValue negative = SurtrValue.CreateFloat(-0.0);

            Assert.True(Comparer.ValuesEqual(positive, negative));
            Assert.Equal(Comparer.HashOf(positive), Comparer.HashOf(negative));
        }

        [Fact]
        public void NaN_IsEqualToItself_UnderValueSemantics()
        {
            SurtrValue left = SurtrValue.CreateFloat(double.NaN);
            SurtrValue right = SurtrValue.CreateFloat(double.NaN);

            Assert.True(Comparer.ValuesEqual(left, right));
            Assert.Equal(Comparer.HashOf(left), Comparer.HashOf(right));
        }

        [Fact]
        public void Float_And_Int_WithMatchingBits_AreNotEqual()
        {
            SurtrValue asFloat = SurtrValue.CreateFloat(5.0);
            SurtrValue asInt = SurtrValue.CreateInt(5);

            Assert.False(Comparer.ValuesEqual(asFloat, asInt));
        }

        [Fact]
        public void DistinctStrings_WithSameText_AreEqual()
        {
            SurtrString left = _runtime.NewString("hello");
            SurtrString right = _runtime.NewString("hello");

            Assert.NotSame(left, right);

            SurtrValue leftValue = _runtime.ValueOf(left);
            SurtrValue rightValue = _runtime.ValueOf(right);

            Assert.True(Comparer.ValuesEqual(leftValue, rightValue));
            Assert.Equal(Comparer.HashOf(leftValue), Comparer.HashOf(rightValue));
        }

        [Fact]
        public void Strings_WithDifferentText_AreNotEqual()
        {
            SurtrValue left = _runtime.ValueOf(_runtime.NewString("hello"));
            SurtrValue right = _runtime.ValueOf(_runtime.NewString("world"));

            Assert.False(Comparer.ValuesEqual(left, right));
        }

        [Fact]
        public void BoxedPrimitive_EqualsTheEquivalentUnboxedPrimitive_BothWays()
        {
            SurtrValue unboxed = SurtrValue.CreateInt(5);
            SurtrValue boxed = _runtime.ValueOf(_runtime.Box(unboxed));

            Assert.True(Comparer.ValuesEqual(boxed, unboxed));
            Assert.True(Comparer.ValuesEqual(unboxed, boxed));
            Assert.Equal(Comparer.HashOf(unboxed), Comparer.HashOf(boxed));
        }

        [Fact]
        public void BoxedPrimitive_DoesNotEqualADifferentUnboxedPrimitive()
        {
            SurtrValue boxed = _runtime.ValueOf(_runtime.Box(SurtrValue.CreateInt(5)));

            Assert.False(Comparer.ValuesEqual(boxed, SurtrValue.CreateInt(6)));
        }

        [Fact]
        public void Tuples_WithEqualElementsInOrder_AreStructurallyEqual()
        {
            SurtrClassReference tupleType = SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.Integer);

            SurtrValue left = _runtime.ValueOf(_runtime.NewTuple(tupleType, new[] { SurtrValue.CreateInt(1), SurtrValue.CreateInt(2) }));
            SurtrValue right = _runtime.ValueOf(_runtime.NewTuple(tupleType, new[] { SurtrValue.CreateInt(1), SurtrValue.CreateInt(2) }));

            Assert.True(Comparer.ValuesEqual(left, right));
            Assert.Equal(Comparer.HashOf(left), Comparer.HashOf(right));
        }

        [Fact]
        public void Tuples_AreOrderSensitive()
        {
            SurtrClassReference tupleType = SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.Integer);

            SurtrValue left = _runtime.ValueOf(_runtime.NewTuple(tupleType, new[] { SurtrValue.CreateInt(1), SurtrValue.CreateInt(2) }));
            SurtrValue right = _runtime.ValueOf(_runtime.NewTuple(tupleType, new[] { SurtrValue.CreateInt(2), SurtrValue.CreateInt(1) }));

            Assert.False(Comparer.ValuesEqual(left, right));
        }

        [Fact]
        public void Tuples_OfDifferentArity_AreNotEqual()
        {
            SurtrClassReference pairType = SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.Integer);
            SurtrClassReference singleType = SurtrClassReference.Tuple(SurtrClassReference.Integer);

            SurtrValue left = _runtime.ValueOf(_runtime.NewTuple(pairType, new[] { SurtrValue.CreateInt(1), SurtrValue.CreateInt(2) }));
            SurtrValue right = _runtime.ValueOf(_runtime.NewTuple(singleType, new[] { SurtrValue.CreateInt(1) }));

            Assert.False(Comparer.ValuesEqual(left, right));
        }

        [Fact]
        public void Arrays_CompareByIdentity_NotContent()
        {
            SurtrArray left = _runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            SurtrArray right = _runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));

            SurtrValue leftValue = _runtime.ValueOf(left);
            SurtrValue rightValue = _runtime.ValueOf(right);

            // Same (empty) contents, different objects: not equal, unlike a tuple.
            Assert.False(Comparer.ValuesEqual(leftValue, rightValue));
            Assert.True(Comparer.ValuesEqual(leftValue, leftValue));
        }

        [Fact]
        public void NullReferences_AreEqualToThemselves()
        {
            Assert.True(Comparer.ValuesEqual(SurtrValue.Null, SurtrValue.Null));
        }
    }
}
