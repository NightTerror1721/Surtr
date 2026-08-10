#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System.Collections.Generic;

namespace Surtr.Tests.Runtime.BuiltIns
{
    /// <summary>
    /// Covers the general iteration path: every built-in collection satisfying
    /// <c>IIterable&lt;T&gt;</c>, and the cursor behind it.
    /// </summary>
    /// <remarks>
    /// What is being pinned down here is that the contract is real - that an <c>int[]</c> can be
    /// reached through the same interface a user collection would implement. A compiled
    /// <c>for-in</c> over any of these is required to lower to an indexed loop and never touch a
    /// single line under test here (<c>Language-Syntax.md</c> §4.2).
    /// </remarks>
    public class SurtrIteratorBuiltInsTests
    {
        private static SurtrInterface Iterable()
        {
            SurtrBuiltIns.EnsureBuilt();
            Assert.True(SurtrBuiltIns.Module.TryGetInterface("IIterable", out var contract));
            return contract;
        }

        private static SurtrInterface IteratorContract()
        {
            SurtrBuiltIns.EnsureBuilt();
            Assert.True(SurtrBuiltIns.Module.TryGetInterface("IIterator", out var contract));
            return contract;
        }

        #region The contracts

        [Fact]
        public void EveryBuiltInCollection_SatisfiesIIterable()
        {
            SurtrInterface iterable = Iterable();

            var collections = new[]
            {
                SurtrBuiltIns.Array,
                SurtrBuiltIns.String,
                SurtrBuiltIns.Tuple,
                SurtrBuiltIns.Dictionary,
                SurtrBuiltIns.Range,
            };

            foreach (var collection in collections)
                Assert.True(collection.Implements(iterable), $"{collection.Name} does not implement IIterable.");
        }

        [Fact]
        public void Iterator_SatisfiesIIterator()
        {
            Assert.True(SurtrBuiltIns.Iterator.Implements(IteratorContract()));
        }

        /// <summary>
        /// An interface call resolves through the receiver's vtable, so the implementation has to
        /// be findable there — declaring it Direct would leave the contract unsatisfiable.
        /// </summary>
        [Fact]
        public void InterfaceDispatch_ResolvesIterateOnEachCollection()
        {
            SurtrInterface iterable = Iterable();
            Assert.True(iterable.TryGetMethods("iterate", out var contractMethods));

            int slot = contractMethods[0].VTableSlot;
            int index = SurtrBuiltIns.Array.IndexOfInterface(iterable);

            Assert.True(index >= 0);
            Assert.Equal("iterate", SurtrBuiltIns.Array.GetInterfaceMethod(index, slot).Name);
        }

        /// <summary>
        /// <c>closure</c> is deliberately not iterable: it is parameterised by a signature, not by
        /// a sequence of elements.
        /// </summary>
        [Fact]
        public void Closure_IsNotIterable()
        {
            Assert.False(SurtrBuiltIns.Closure.Implements(Iterable()));
        }

        #endregion

        #region Walking each source

        [Fact]
        public void ArrayIterator_YieldsEveryElementInOrder()
        {
            using var runtime = new SurtrRuntime();

            var source = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            for (int i = 0; i < 4; i++)
                source.Add(SurtrValue.CreateInt(i * 10));

            var iterator = runtime.NewIterator(SurtrIteratorKind.Array, source);

            for (int i = 0; i < 4; i++)
            {
                Assert.True(iterator.MoveNext(runtime));
                Assert.Equal(i * 10, iterator.Current.AsInt);
            }

            Assert.False(iterator.MoveNext(runtime));
        }

        [Fact]
        public void StringIterator_YieldsCharacters()
        {
            using var runtime = new SurtrRuntime();

            var iterator = runtime.NewIterator(SurtrIteratorKind.String, runtime.NewString("hey"));

            foreach (char expected in "hey")
            {
                Assert.True(iterator.MoveNext(runtime));
                Assert.Equal(expected, iterator.Current.AsChar);
            }

            Assert.False(iterator.MoveNext(runtime));
        }

        [Fact]
        public void RangeIterator_CoversItsBoundsAndRespectsInclusivity()
        {
            using var runtime = new SurtrRuntime();

            Assert.Equal(new[] { 2, 3, 4 }, Walk(runtime, 2, 5, inclusive: false));
            Assert.Equal(new[] { 2, 3, 4, 5 }, Walk(runtime, 2, 5, inclusive: true));
            Assert.Empty(Walk(runtime, 5, 5, inclusive: false));
        }

        [Fact]
        public void TupleIterator_YieldsEveryElement()
        {
            using var runtime = new SurtrRuntime();

            var source = runtime.NewTuple(
                SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.String),
                new[] { SurtrValue.CreateInt(7), runtime.ValueOf(runtime.NewString("x")) });

            var iterator = runtime.NewIterator(SurtrIteratorKind.Tuple, source);

            Assert.True(iterator.MoveNext(runtime));
            Assert.Equal(7, iterator.Current.AsInt);
            Assert.True(iterator.MoveNext(runtime));
            Assert.False(iterator.MoveNext(runtime));
        }

        /// <summary>A dictionary yields <c>(key, value)</c> pairs — the only element type that loses nothing.</summary>
        [Fact]
        public void DictionaryIterator_YieldsKeyValuePairs()
        {
            using var runtime = new SurtrRuntime();

            var source = runtime.NewDictionary(
                SurtrClassReference.Dictionary(SurtrClassReference.String, SurtrClassReference.Integer));

            source.Set(runtime.ValueOf(runtime.InternString("a")), SurtrValue.CreateInt(1));

            var iterator = runtime.NewIterator(
                SurtrIteratorKind.Dictionary,
                source,
                new[] { runtime.ValueOf(runtime.InternString("a")) });

            Assert.True(iterator.MoveNext(runtime));

            var pair = runtime.Resolve<SurtrTuple>(iterator.Current);
            Assert.NotNull(pair);
            Assert.Equal(2, pair!.Length);
            Assert.Equal("a", runtime.Resolve<SurtrString>(pair[0])!.Text);
            Assert.Equal(1, pair[1].AsInt);

            Assert.False(iterator.MoveNext(runtime));
        }

        /// <summary>
        /// A key removed after the snapshot is skipped rather than reported as a pair that no
        /// longer exists.
        /// </summary>
        [Fact]
        public void DictionaryIterator_SkipsAKeyRemovedMidWalk()
        {
            using var runtime = new SurtrRuntime();

            var source = runtime.NewDictionary(
                SurtrClassReference.Dictionary(SurtrClassReference.String, SurtrClassReference.Integer));

            SurtrValue first = runtime.ValueOf(runtime.InternString("a"));
            SurtrValue second = runtime.ValueOf(runtime.InternString("b"));

            source.Set(first, SurtrValue.CreateInt(1));
            source.Set(second, SurtrValue.CreateInt(2));

            var iterator = runtime.NewIterator(SurtrIteratorKind.Dictionary, source, new[] { first, second });

            source.Remove(first);

            Assert.True(iterator.MoveNext(runtime));
            Assert.Equal(2, runtime.Resolve<SurtrTuple>(iterator.Current)![1].AsInt);
            Assert.False(iterator.MoveNext(runtime));
        }

        [Fact]
        public void Reset_WalksTheSourceAgain()
        {
            using var runtime = new SurtrRuntime();

            var source = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            source.Add(SurtrValue.CreateInt(1));

            var iterator = runtime.NewIterator(SurtrIteratorKind.Array, source);

            Assert.True(iterator.MoveNext(runtime));
            Assert.False(iterator.MoveNext(runtime));

            iterator.Reset();

            Assert.True(iterator.MoveNext(runtime));
            Assert.Equal(1, iterator.Current.AsInt);
        }

        /// <summary>An exhausted cursor reports null rather than the last value it produced.</summary>
        [Fact]
        public void Current_IsNullBeforeTheFirstStepAndAfterTheLast()
        {
            using var runtime = new SurtrRuntime();

            var source = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            source.Add(SurtrValue.CreateInt(1));

            var iterator = runtime.NewIterator(SurtrIteratorKind.Array, source);
            Assert.True(iterator.Current.IsNullReference);

            Assert.True(iterator.MoveNext(runtime));
            Assert.False(iterator.MoveNext(runtime));
            Assert.True(iterator.Current.IsNullReference);
        }

        #endregion

        #region Collection

        /// <summary>
        /// A collection reached only through a live iterator has to survive: the stack slot it was
        /// read from is routinely gone by the time the loop body runs.
        /// </summary>
        [Fact]
        public void ALiveIterator_KeepsItsSourceReachable()
        {
            using var runtime = new SurtrRuntime();

            var source = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            source.Add(SurtrValue.CreateInt(42));

            var iterator = runtime.NewIterator(SurtrIteratorKind.Array, source);
            runtime.AddRoot(iterator);

            runtime.Collect();

            Assert.True(iterator.MoveNext(runtime));
            Assert.Equal(42, iterator.Current.AsInt);
        }

        #endregion

        private static int[] Walk(SurtrRuntime runtime, int start, int end, bool inclusive)
        {
            var iterator = runtime.NewIterator(SurtrIteratorKind.Range, runtime.NewRange(start, end, inclusive));

            var walked = new List<int>();
            while (iterator.MoveNext(runtime))
                walked.Add(iterator.Current.AsInt);

            return walked.ToArray();
        }
    }
}
