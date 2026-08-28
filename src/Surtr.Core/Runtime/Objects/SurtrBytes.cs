#nullable enable

using Surtr.Runtime.BuiltIns;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// The built-in <c>bytes</c> type: a mutable array of bytes, like a binary buffer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backing storage is a plain CLR <c>byte[]</c>, deliberately. Unlike
    /// <see cref="SurtrArray"/>'s unmanaged buffer, a byte array crosses host boundaries all the
    /// time - a Unity texture, a network packet, a file read - and the natural CLR shape for that
    /// is a managed <c>byte[]</c>, zero-copy on the way in and out. A managed buffer also needs no
    /// <see cref="SurtrArray.ReleaseBuffer"/> hook: it holds no <see cref="SurtrValue"/> and the CLR
    /// collector reclaims it with the object.
    /// </para>
    /// <para>
    /// Only the first <see cref="Count"/> entries are live; the rest is slack so an appending
    /// buffer does not reallocate per byte, exactly the bargain <c>capacity</c> exposes. The
    /// element type is always a byte, so unlike <see cref="SurtrArray"/> there is no per-array
    /// descriptor to keep - the class itself says what every element is.
    /// </para>
    /// <para>
    /// Values cross the surface as <see cref="int"/> in Surtr source - the language has no
    /// one-byte primitive, so a read answers 0-255 and a write accepts 0-255. The native members
    /// in <see cref="BuiltIns.SurtrBytesBuiltIn"/> are what check the range on the way in.
    /// </para>
    /// </remarks>
    public sealed class SurtrBytes : SurtrObject
    {
        private const int MinimumCapacity = 4;

        /// <summary>The backing buffer. Only the first <see cref="Count"/> entries are live.</summary>
        internal byte[] Items;

        /// <summary>How many bytes of <see cref="Items"/> are live.</summary>
        internal int Count;

        internal SurtrBytes(int capacity) : base(SurtrBuiltIns.Bytes)
        {
            Items = new byte[Math.Max(capacity, MinimumCapacity)];
            Count = 0;
        }

        internal SurtrBytes(byte[] data) : base(SurtrBuiltIns.Bytes)
        {
            Items = data;
            Count = data.Length;
        }

        /// <summary>How many bytes the buffer holds. What the <c>length</c> property reads.</summary>
        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Count;
        }

        /// <summary>How many bytes the buffer can hold before it has to grow.</summary>
        public int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Items.Length;
        }

        /// <summary>Whether the buffer holds no bytes.</summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Count == 0;
        }

        /// <summary>Reads or writes one byte. The caller is responsible for the range check.</summary>
        public byte this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Items[index];
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => Items[index] = value;
        }

        /// <summary>Whether <paramref name="index"/> addresses a live byte.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInRange(int index) => (uint)index < (uint)Count;

        #region Mutation
        /// <summary>Appends a byte, growing the buffer if it is full.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(byte value)
        {
            int count = Count;
            if (count == Items.Length)
                Grow(count + 1);

            Items[count] = value;
            Count = count + 1;
        }

        /// <summary>Removes and returns the last byte.</summary>
        /// <exception cref="InvalidOperationException">The buffer is empty.</exception>
        public byte RemoveLast()
        {
            int last = Count - 1;
            if (last < 0)
                throw new InvalidOperationException("Cannot remove the last byte of an empty buffer.");

            byte value = Items[last];
            Items[last] = 0;
            Count = last;
            return value;
        }

        /// <summary>Inserts a byte at <paramref name="index"/>, shifting everything after it up.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length]</c>.</exception>
        public void Insert(int index, byte value)
        {
            int count = Count;
            if ((uint)index > (uint)count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Insertion index is outside the buffer.");

            if (count == Items.Length)
                Grow(count + 1);

            if (index < count)
                Buffer.BlockCopy(Items, index, Items, index + 1, count - index);

            Items[index] = value;
            Count = count + 1;
        }

        /// <summary>Removes the byte at <paramref name="index"/>, shifting everything after it down.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
        public void RemoveAt(int index)
        {
            int count = Count;
            if ((uint)index >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Removal index is outside the buffer.");

            int last = count - 1;
            if (index < last)
                Buffer.BlockCopy(Items, index + 1, Items, index, last - index);

            Items[last] = 0;
            Count = last;
        }

        /// <summary>Drops every byte, keeping the buffer for reuse.</summary>
        public void Clear()
        {
            Array.Clear(Items, 0, Count);
            Count = 0;
        }

        /// <summary>Reverses the bytes in place.</summary>
        public void Reverse()
        {
            Array.Reverse(Items, 0, Count);
        }

        /// <summary>Shrinks the buffer to <paramref name="length"/> bytes, blanking whatever falls off.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative or longer than the buffer.</exception>
        public void Truncate(int length)
        {
            if ((uint)length > (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(length), length, "Truncation length is outside the buffer.");

            if (length < Count)
                Array.Clear(Items, length, Count - length);
            Count = length;
        }

        /// <summary>Makes room for at least <paramref name="capacity"/> bytes without changing the length.</summary>
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

            var grown = new byte[capacity];
            Buffer.BlockCopy(Items, 0, grown, 0, Count);
            Items = grown;
        }
        #endregion

        #region Search
        /// <summary>Finds the first index holding <paramref name="value"/>, or <c>-1</c>.</summary>
        public int IndexOf(byte value)
        {
            var items = Items;
            int count = Count;

            for (int i = 0; i < count; i++)
            {
                if (items[i] == value)
                    return i;
            }

            return -1;
        }

        /// <summary>Finds the last index holding <paramref name="value"/>, or <c>-1</c>.</summary>
        public int LastIndexOf(byte value)
        {
            var items = Items;
            int count = Count;

            for (int i = count - 1; i >= 0; i--)
            {
                if (items[i] == value)
                    return i;
            }

            return -1;
        }

        /// <summary>Whether the buffer holds <paramref name="value"/> anywhere.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(byte value) => IndexOf(value) >= 0;
        #endregion

        /// <summary>A copy of the live bytes, as a CLR array the host can take over.</summary>
        public byte[] ToArray()
        {
            var copy = new byte[Count];
            Buffer.BlockCopy(Items, 0, copy, 0, Count);
            return copy;
        }

        // A byte[] holds no Surtr values, so there is nothing here to trace.
        internal override void VisitReferences(SurtrEntityMarker marker) { }

        /// <inheritdoc/>
        public override string ToString() => $"bytes({Count})";
    }
}