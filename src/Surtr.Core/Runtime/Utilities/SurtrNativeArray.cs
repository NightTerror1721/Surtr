#nullable enable

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Utilities
{
    /// <summary>
    /// A bare pointer + length pair that owns and frees its own unmanaged buffer.
    /// </summary>
    /// <remarks>
    /// <see cref="Pointer"/> is exposed raw so hot call sites can index it directly with zero
    /// indirection; the indexers below are just convenience wrappers over the same access -
    /// in Release they compile down to the exact same code (the bounds check is a
    /// <c>Debug.Assert</c>, stripped there), so they cost nothing over dereferencing
    /// <see cref="Pointer"/> by hand.
    /// </remarks>
    internal unsafe struct SurtrNativeArray<T> : IDisposable where T : unmanaged
    {
        internal T* Pointer;
        internal int Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrNativeArray(int length)
        {
            Pointer = MemOps.Allocate<T>((nuint)length);
            Length = length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrNativeArray(int length, bool zeroed)
        {
            Pointer = zeroed ? MemOps.AllocateZeroed<T>((nuint)length) : MemOps.Allocate<T>((nuint)length);
            Length = length;
        }

        public readonly ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Debug.Assert((uint)index < (uint)Length, "SurtrNativeArray index out of range.");
                return ref Pointer[index];
            }
        }

        public readonly ref T this[nuint index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Debug.Assert(index < (uint)Length, "SurtrNativeArray index out of range.");
                return ref Pointer[index];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Resize(int newLength)
        {
            Pointer = MemOps.Reallocate<T>(Pointer, (nuint)newLength);
            Length = newLength;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Clear()
            => MemOps.Clear(Pointer, (nuint)Length * (nuint)sizeof(T));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            MemOps.Free(Pointer);
            Pointer = null;
            Length = 0;
        }
    }
}
