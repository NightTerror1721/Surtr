#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.Runtime.Objects
{
    public class SurtrArrayTests
    {
        private static SurtrArray NewArray(int capacity = 0) => new(SurtrClassReference.Array(SurtrClassReference.Integer), capacity);

        [Fact]
        public void NewArray_StartsEmpty()
        {
            var array = NewArray();
            Assert.Equal(0, array.Length);
            Assert.True(array.IsEmpty);
        }

        [Fact]
        public void Add_AppendsAndGrowsPastInitialCapacity()
        {
            var array = NewArray();
            for (int i = 0; i < 10; i++)
                array.Add(SurtrValue.CreateInt(i));

            Assert.Equal(10, array.Length);
            Assert.True(array.Capacity >= 10);
            for (int i = 0; i < 10; i++)
                Assert.Equal(i, array[i].AsInt);
        }

        [Fact]
        public void RemoveLast_OnEmptyArray_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => NewArray().RemoveLast());
        }

        [Fact]
        public void RemoveLast_ReturnsTheLastElementAndShrinksLength()
        {
            var array = NewArray();
            array.Add(SurtrValue.CreateInt(1));
            array.Add(SurtrValue.CreateInt(2));

            SurtrValue removed = array.RemoveLast();

            Assert.Equal(2, removed.AsInt);
            Assert.Equal(1, array.Length);
        }

        [Theory]
        [InlineData(0)] // front
        [InlineData(1)] // middle
        [InlineData(3)] // == Length: append via Insert
        public void Insert_ShiftsSubsequentElementsUp(int index)
        {
            var array = NewArray();
            array.Add(SurtrValue.CreateInt(10));
            array.Add(SurtrValue.CreateInt(20));
            array.Add(SurtrValue.CreateInt(30));

            array.Insert(index, SurtrValue.CreateInt(99));

            Assert.Equal(4, array.Length);
            Assert.Equal(99, array[index].AsInt);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(4)] // > Length
        public void Insert_OutOfRange_Throws(int index)
        {
            var array = NewArray();
            array.Add(SurtrValue.CreateInt(1));
            array.Add(SurtrValue.CreateInt(2));
            array.Add(SurtrValue.CreateInt(3));

            Assert.Throws<ArgumentOutOfRangeException>(() => array.Insert(index, SurtrValue.CreateInt(0)));
        }

        [Fact]
        public void RemoveAt_ShiftsSubsequentElementsDown()
        {
            var array = NewArray();
            array.Add(SurtrValue.CreateInt(10));
            array.Add(SurtrValue.CreateInt(20));
            array.Add(SurtrValue.CreateInt(30));

            array.RemoveAt(1);

            Assert.Equal(2, array.Length);
            Assert.Equal(10, array[0].AsInt);
            Assert.Equal(30, array[1].AsInt);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(3)] // == Length
        public void RemoveAt_OutOfRange_Throws(int index)
        {
            var array = NewArray();
            array.Add(SurtrValue.CreateInt(1));
            array.Add(SurtrValue.CreateInt(2));
            array.Add(SurtrValue.CreateInt(3));

            Assert.Throws<ArgumentOutOfRangeException>(() => array.RemoveAt(index));
        }

        [Fact]
        public void Clear_ResetsLengthButKeepsCapacity()
        {
            var array = NewArray();
            for (int i = 0; i < 5; i++)
                array.Add(SurtrValue.CreateInt(i));
            int capacityBefore = array.Capacity;

            array.Clear();

            Assert.Equal(0, array.Length);
            Assert.True(array.IsEmpty);
            Assert.Equal(capacityBefore, array.Capacity);
        }

        [Fact]
        public void Reverse_ReversesInPlace()
        {
            var array = NewArray();
            array.Add(SurtrValue.CreateInt(1));
            array.Add(SurtrValue.CreateInt(2));
            array.Add(SurtrValue.CreateInt(3));

            array.Reverse();

            Assert.Equal(3, array[0].AsInt);
            Assert.Equal(2, array[1].AsInt);
            Assert.Equal(1, array[2].AsInt);
        }

        [Fact]
        public void Truncate_ShrinksLength()
        {
            var array = NewArray();
            for (int i = 0; i < 5; i++)
                array.Add(SurtrValue.CreateInt(i));

            array.Truncate(2);

            Assert.Equal(2, array.Length);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(6)] // > Count
        public void Truncate_OutOfRange_Throws(int length)
        {
            var array = NewArray();
            for (int i = 0; i < 5; i++)
                array.Add(SurtrValue.CreateInt(i));

            Assert.Throws<ArgumentOutOfRangeException>(() => array.Truncate(length));
        }

        [Fact]
        public void EnsureCapacity_GrowsTheBufferWithoutChangingLength()
        {
            var array = NewArray();
            array.Add(SurtrValue.CreateInt(1));

            array.EnsureCapacity(100);

            Assert.True(array.Capacity >= 100);
            Assert.Equal(1, array.Length);
        }

        [Fact]
        public void IndexOfAndContains_UseValueSemantics_NotIdentity()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.String));
            array.Add(runtime.ValueOf(runtime.NewString("hello")));

            SurtrValue lookup = runtime.ValueOf(runtime.NewString("hello")); // a distinct string object, same text

            Assert.Equal(0, array.IndexOf(lookup, runtime.ValueComparer));
            Assert.True(array.Contains(lookup, runtime.ValueComparer));
        }

        [Fact]
        public void Remove_DropsTheFirstMatchingElement()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            array.Add(SurtrValue.CreateInt(1));
            array.Add(SurtrValue.CreateInt(2));

            bool removed = array.Remove(SurtrValue.CreateInt(1), runtime.ValueComparer);

            Assert.True(removed);
            Assert.Equal(1, array.Length);
            Assert.Equal(2, array[0].AsInt);
        }

        [Fact]
        public void ElementType_IsSlicedFromTheArraysTypeReference()
        {
            var array = NewArray();
            Assert.Equal(SurtrClassReference.Integer, array.ElementType);
        }

        [Fact]
        public void VisitReferences_KeepsOnlyElementsWithinLength_ThroughACollection()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.String));

            var first = runtime.NewString("first");
            var second = runtime.NewString("second");
            // Captured before collecting: releasing an entity resets its own SurtrRef to null,
            // so re-deriving the id from the (possibly-collected) object afterwards would defeat
            // the check.
            SurtrValue firstRef = SurtrValue.CreateReference(first.GetSurtrReference());
            SurtrValue secondRef = SurtrValue.CreateReference(second.GetSurtrReference());

            array.Add(runtime.ValueOf(first));
            array.Add(runtime.ValueOf(second));

            runtime.AddRoot(array);
            runtime.Collect();

            Assert.NotNull(runtime.Resolve<SurtrString>(firstRef));
            Assert.NotNull(runtime.Resolve<SurtrString>(secondRef));

            // Dropping "second" clears its slot; a collection afterwards must not still trace it.
            array.RemoveLast();
            runtime.Collect();

            Assert.Null(runtime.Resolve<SurtrString>(secondRef));
            Assert.NotNull(runtime.Resolve<SurtrString>(firstRef));
        }
    }
}
