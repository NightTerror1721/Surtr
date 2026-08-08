#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// The built-in array: a growable, ordered sequence of <see cref="SurtrValue"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It behaves like a list - it grows and shrinks - but the storage underneath is always one
    /// flat <see cref="SurtrValue"/><c>[]</c> plus a count, never a <c>List&lt;T&gt;</c>. A
    /// <c>List&lt;T&gt;</c> would add a second object to chase on every element access and would
    /// hide the buffer behind an indexer with its own bounds check on top of the array's, and
    /// <c>ArrGet</c>/<c>ArrSet</c> are among the hottest instructions in the set. With the buffer
    /// exposed, an element access is one load off <see cref="Items"/>.
    /// </para>
    /// <para>
    /// The buffer is managed rather than a <c>SurtrNativeArray</c>. An array is a collectable
    /// value, and the entity registry sweeps by dropping its reference to an object - there is no
    /// finalization hook - so an unmanaged buffer owned by a collectable object would leak on
    /// every collection. Every collectable object in this namespace holds its values in a managed
    /// array for that reason; the unmanaged arrays belong to metadata, which is disposed
    /// explicitly.
    /// </para>
    /// <para>
    /// Elements carry no per-element type. Surtr is statically typed, so the compiler already
    /// knows an <c>int[]</c> from a <c>string[]</c> and emits accesses that are correct by
    /// construction; storing the element type again per element would cost memory and a check to
    /// re-derive what was known at compile time. What the object does keep is
    /// <see cref="TypeReference"/> - one interned descriptor naming its full parameterised type,
    /// <c>AI</c>, <c>AS</c>, <c>ADIS</c> - so nothing is lost for diagnostics, host interop or a
    /// future dynamic check, at a cost of one field for the whole array rather than one per
    /// element. Tracing does not need it either: values are NaN-boxed, so
    /// <see cref="VisitReferences"/> tests tags.
    /// </para>
    /// </remarks>
    public sealed class SurtrArray : SurtrObject
    {
        private const int MinimumCapacity = 4;

        /// <summary>
        /// The backing buffer. Only the first <see cref="Count"/> entries are live; the rest is
        /// slack and must not be read.
        /// </summary>
        internal SurtrValue[] Items;

        /// <summary>How many entries of <see cref="Items"/> are live.</summary>
        internal int Count;

        private readonly SurtrClassReference _typeReference;

        internal SurtrArray(SurtrClassReference typeReference, int capacity) : base(SurtrBuiltIns.Array)
        {
            _typeReference = typeReference;
            Items = capacity > 0 ? new SurtrValue[capacity] : Array.Empty<SurtrValue>();
            Count = 0;
        }

        /// <summary>How many elements the array holds. What <c>ArrLen</c> pushes.</summary>
        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Count;
        }

        /// <summary>How many elements the array can hold before the buffer has to grow.</summary>
        public int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Items.Length;
        }

        /// <summary>Whether the array holds no elements.</summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Count == 0;
        }

        /// <summary>
        /// This array's full parameterised type descriptor - <c>AI</c> for <c>int[]</c>, and so
        /// on. <see cref="SurtrClassReference.IsValid"/> is false for an array built without one.
        /// </summary>
        public SurtrClassReference TypeReference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _typeReference;
        }

        /// <summary>The declared type of this array's elements, sliced out of <see cref="TypeReference"/>.</summary>
        public SurtrClassReference ElementType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _typeReference.IsValid ? _typeReference.GetArrayElementType() : default;
        }

        /// <summary>Reads an element. The caller is responsible for the range check.</summary>
        public ref SurtrValue this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref Items[index];
        }

        /// <summary>Whether <paramref name="index"/> addresses a live element.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInRange(int index) => (uint)index < (uint)Count;

        #region Mutation
        /// <summary>Appends a value, growing the buffer if it is full.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(SurtrValue value)
        {
            int count = Count;
            if (count == Items.Length)
                Grow(count + 1);

            Items[count] = value;
            Count = count + 1;
        }

        /// <summary>Removes and returns the last element.</summary>
        /// <exception cref="InvalidOperationException">The array is empty.</exception>
        public SurtrValue RemoveLast()
        {
            int last = Count - 1;
            if (last < 0)
                throw new InvalidOperationException("Cannot remove the last element of an empty array.");

            SurtrValue value = Items[last];

            // Cleared, not just abandoned: a stale reference in the slack would keep an entity
            // alive if anything ever traced past Count, and null is the cheap way to be sure it
            // cannot.
            Items[last] = SurtrValue.Null;
            Count = last;
            return value;
        }

        /// <summary>Inserts a value at <paramref name="index"/>, shifting everything after it up.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length]</c>.</exception>
        public void Insert(int index, SurtrValue value)
        {
            int count = Count;
            if ((uint)index > (uint)count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Insertion index is outside the array.");

            if (count == Items.Length)
                Grow(count + 1);

            if (index < count)
                Array.Copy(Items, index, Items, index + 1, count - index);

            Items[index] = value;
            Count = count + 1;
        }

        /// <summary>Removes the element at <paramref name="index"/>, shifting everything after it down.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
        public void RemoveAt(int index)
        {
            int count = Count;
            if ((uint)index >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Removal index is outside the array.");

            int last = count - 1;
            if (index < last)
                Array.Copy(Items, index + 1, Items, index, last - index);

            Items[last] = SurtrValue.Null;
            Count = last;
        }

        /// <summary>Drops every element, keeping the buffer for reuse.</summary>
        public void Clear()
        {
            // Only the live prefix needs clearing; everything past Count was already blanked when
            // it stopped being live.
            Array.Clear(Items, 0, Count);
            Count = 0;
        }

        /// <summary>Reverses the elements in place.</summary>
        public void Reverse()
        {
            var items = Items;
            int low = 0;
            int high = Count - 1;

            while (low < high)
            {
                SurtrValue swap = items[low];
                items[low] = items[high];
                items[high] = swap;
                low++;
                high--;
            }
        }

        /// <summary>
        /// Shrinks the array to <paramref name="length"/> elements, blanking whatever falls off.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative or longer than the array.</exception>
        public void Truncate(int length)
        {
            if ((uint)length > (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(length), length, "Truncation length is outside the array.");

            Array.Clear(Items, length, Count - length);
            Count = length;
        }

        /// <summary>Makes room for at least <paramref name="capacity"/> elements without changing the length.</summary>
        public void EnsureCapacity(int capacity)
        {
            if (capacity > Items.Length)
                Grow(capacity);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Grow(int required)
        {
            int capacity = Items.Length == 0 ? MinimumCapacity : Items.Length * 2;
            if (capacity < required)
                capacity = required;

            Array.Resize(ref Items, capacity);
        }
        #endregion

        #region Search
        /// <summary>
        /// Finds the first element equal to <paramref name="value"/> under the runtime's value
        /// semantics, or <c>-1</c>. What <c>ArrIn</c> resolves to.
        /// </summary>
        /// <remarks>
        /// Takes the comparer rather than comparing raw bits because equality is a language
        /// question, not a bit-pattern one: two distinct string objects holding the same text are
        /// the same value. The comparer is a sealed class and this calls it directly, so the scan
        /// stays a direct call per element instead of an interface dispatch.
        /// </remarks>
        public int IndexOf(SurtrValue value, SurtrValueComparer comparer)
        {
            var items = Items;
            int count = Count;

            for (int i = 0; i < count; i++)
            {
                if (comparer.ValuesEqual(items[i], value))
                    return i;
            }

            return -1;
        }

        /// <summary>Whether any element equals <paramref name="value"/> under the runtime's value semantics.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(SurtrValue value, SurtrValueComparer comparer) => IndexOf(value, comparer) >= 0;

        /// <summary>Removes the first element equal to <paramref name="value"/>.</summary>
        /// <returns><see langword="true"/> if an element was removed.</returns>
        public bool Remove(SurtrValue value, SurtrValueComparer comparer)
        {
            int index = IndexOf(value, comparer);
            if (index < 0)
                return false;

            RemoveAt(index);
            return true;
        }
        #endregion

        internal override void VisitReferences(SurtrEntityMarker marker)
        {
            var items = Items;
            int count = Count;

            for (int i = 0; i < count; i++)
                marker.Mark(items[i]);
        }
    }
}
