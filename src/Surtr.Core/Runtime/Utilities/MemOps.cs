#nullable enable

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Surtr.Runtime.Utilities
{
    /// <summary>
    /// Unmanaged memory utilities built on <see cref="Marshal"/>'s HGlobal allocator. Stands
    /// in for <c>System.Runtime.InteropServices.NativeMemory</c>, which isn't available on
    /// netstandard2.1 (that API is .NET 6+).
    /// </summary>
    internal static unsafe class MemOps
    {
        #region Allocation
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* Allocate(nuint byteCount)
        {
            IntPtr size = checked((IntPtr)(nint)byteCount);
            return (void*)Marshal.AllocHGlobal(size);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* Allocate(nuint elementCount, nuint elementSize)
            => Allocate(CheckedByteCount(elementCount, elementSize));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* AllocateZeroed(nuint byteCount)
        {
            void* ptr = Allocate(byteCount);
            Clear(ptr, byteCount);
            return ptr;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* AllocateZeroed(nuint elementCount, nuint elementSize)
            => AllocateZeroed(CheckedByteCount(elementCount, elementSize));
        #endregion

        #region Reallocation
        public static void* Reallocate(void* ptr, nuint newByteCount)
        {
            if (ptr == null)
                return Allocate(newByteCount);

            IntPtr newSize = checked((IntPtr)(nint)newByteCount);
            return (void*)Marshal.ReAllocHGlobal((IntPtr)ptr, newSize);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* Reallocate(void* ptr, nuint newElementCount, nuint elementSize)
            => Reallocate(ptr, CheckedByteCount(newElementCount, elementSize));
        #endregion

        #region Deallocation
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Free(void* ptr)
        {
            if (ptr != null)
                Marshal.FreeHGlobal((IntPtr)ptr);
        }
        #endregion

        #region Copying & Moving
        /// <summary>
        /// Copies <paramref name="byteCount"/> bytes from <paramref name="source"/> to
        /// <paramref name="destination"/>. Safe for overlapping regions (memmove semantics).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Copy(void* source, void* destination, nuint byteCount)
            => Buffer.MemoryCopy(source, destination, byteCount, byteCount);

        /// <summary>
        /// Equivalent to <see cref="Copy"/> - provided separately so call sites can express
        /// "moving" intent, even though both are overlap-safe.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Move(void* source, void* destination, nuint byteCount)
            => Buffer.MemoryCopy(source, destination, byteCount, byteCount);
        #endregion

        #region Filling & Clearing
        /// <summary>
        /// Zeroes <paramref name="byteCount"/> bytes starting at <paramref name="ptr"/>. Uses
        /// <see cref="Vector{T}"/> chunks (16/32/64 bytes depending on SSE2/AVX2/AVX-512) when
        /// the hardware accelerates them, falling back to an 8-byte-at-a-time loop otherwise
        /// and for the unaligned tail.
        /// </summary>
        public static void Clear(void* ptr, nuint byteCount)
        {
            byte* bytePtr = (byte*)ptr;
            nuint offset = 0;

            if (Vector.IsHardwareAccelerated)
            {
                Vector<byte> zero = Vector<byte>.Zero;
                nuint vectorSize = (nuint)Vector<byte>.Count;
                nuint doubleVectorSize = vectorSize * 2;

                for (; offset + doubleVectorSize <= byteCount; offset += doubleVectorSize)
                {
                    *(Vector<byte>*)(bytePtr + offset) = zero;
                    *(Vector<byte>*)(bytePtr + offset + vectorSize) = zero;
                }

                for (; offset + vectorSize <= byteCount; offset += vectorSize)
                    *(Vector<byte>*)(bytePtr + offset) = zero;
            }

            ClearTail(bytePtr + offset, byteCount - offset);
        }

        /// <summary>
        /// Sets <paramref name="byteCount"/> bytes starting at <paramref name="ptr"/> to
        /// <paramref name="value"/>, using the same <see cref="Vector{T}"/>-chunked strategy
        /// as <see cref="Clear"/>.
        /// </summary>
        public static void Fill(void* ptr, nuint byteCount, byte value)
        {
            if (value == 0)
            {
                Clear(ptr, byteCount);
                return;
            }

            byte* bytePtr = (byte*)ptr;
            nuint offset = 0;

            if (Vector.IsHardwareAccelerated)
            {
                Vector<byte> pattern = new(value);
                nuint vectorSize = (nuint)Vector<byte>.Count;
                nuint doubleVectorSize = vectorSize * 2;

                for (; offset + doubleVectorSize <= byteCount; offset += doubleVectorSize)
                {
                    *(Vector<byte>*)(bytePtr + offset) = pattern;
                    *(Vector<byte>*)(bytePtr + offset + vectorSize) = pattern;
                }

                for (; offset + vectorSize <= byteCount; offset += vectorSize)
                    *(Vector<byte>*)(bytePtr + offset) = pattern;
            }

            FillTail(bytePtr + offset, byteCount - offset, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClearTail(byte* ptr, nuint byteCount)
        {
            ulong* qwordPtr = (ulong*)ptr;
            nuint qwordCount = byteCount / 8;

            for (nuint i = 0; i < qwordCount; i++)
                qwordPtr[i] = 0;

            byte* bytePtr = ptr + qwordCount * 8;
            nuint remaining = byteCount - qwordCount * 8;

            for (nuint i = 0; i < remaining; i++)
                bytePtr[i] = 0;
        }

        private static void FillTail(byte* ptr, nuint byteCount, byte value)
        {
            ulong pattern = value * 0x0101010101010101UL;
            ulong* qwordPtr = (ulong*)ptr;
            nuint qwordCount = byteCount / 8;

            for (nuint i = 0; i < qwordCount; i++)
                qwordPtr[i] = pattern;

            byte* bytePtr = ptr + qwordCount * 8;
            nuint remaining = byteCount - qwordCount * 8;

            for (nuint i = 0; i < remaining; i++)
                bytePtr[i] = value;
        }
        #endregion

        #region Comparison
        public static bool AreEqual(void* left, void* right, nuint byteCount)
        {
            if (left == right)
                return true;

            byte* leftBytePtr = (byte*)left;
            byte* rightBytePtr = (byte*)right;
            nuint offset = 0;

            if (Vector.IsHardwareAccelerated)
            {
                nuint vectorSize = (nuint)Vector<byte>.Count;

                for (; offset + vectorSize <= byteCount; offset += vectorSize)
                {
                    if (*(Vector<byte>*)(leftBytePtr + offset) != *(Vector<byte>*)(rightBytePtr + offset))
                        return false;
                }
            }

            return AreEqualTail(leftBytePtr + offset, rightBytePtr + offset, byteCount - offset);
        }

        public static bool IsZero(void* ptr, nuint byteCount)
        {
            byte* bytePtr = (byte*)ptr;
            nuint offset = 0;

            if (Vector.IsHardwareAccelerated)
            {
                Vector<byte> zero = Vector<byte>.Zero;
                nuint vectorSize = (nuint)Vector<byte>.Count;

                for (; offset + vectorSize <= byteCount; offset += vectorSize)
                {
                    if (*(Vector<byte>*)(bytePtr + offset) != zero)
                        return false;
                }
            }

            return IsZeroTail(bytePtr + offset, byteCount - offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AreEqualTail(byte* left, byte* right, nuint byteCount)
        {
            ulong* leftQwordPtr = (ulong*)left;
            ulong* rightQwordPtr = (ulong*)right;
            nuint qwordCount = byteCount / 8;

            for (nuint i = 0; i < qwordCount; i++)
            {
                if (leftQwordPtr[i] != rightQwordPtr[i])
                    return false;
            }

            byte* leftBytePtr = left + qwordCount * 8;
            byte* rightBytePtr = right + qwordCount * 8;
            nuint remaining = byteCount - qwordCount * 8;

            for (nuint i = 0; i < remaining; i++)
            {
                if (leftBytePtr[i] != rightBytePtr[i])
                    return false;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsZeroTail(byte* ptr, nuint byteCount)
        {
            ulong* qwordPtr = (ulong*)ptr;
            nuint qwordCount = byteCount / 8;

            for (nuint i = 0; i < qwordCount; i++)
            {
                if (qwordPtr[i] != 0)
                    return false;
            }

            byte* bytePtr = ptr + qwordCount * 8;
            nuint remaining = byteCount - qwordCount * 8;

            for (nuint i = 0; i < remaining; i++)
            {
                if (bytePtr[i] != 0)
                    return false;
            }

            return true;
        }
        #endregion

        #region Typed Helpers
        // Thin casting wrappers over the void*-returning overloads above, generic on an
        // unmanaged T so call sites don't need an explicit (T*) cast. Each is a single
        // AggressiveInlining cast/call, so there is no overhead versus casting by hand.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T* Allocate<T>(nuint elementCount) where T : unmanaged
            => (T*)Allocate(CheckedByteCount(elementCount, (nuint)sizeof(T)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T* Allocate<T>(nuint elementCount, nuint elementSize) where T : unmanaged
            => (T*)Allocate(elementCount, elementSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T* AllocateZeroed<T>(nuint elementCount) where T : unmanaged
            => (T*)AllocateZeroed(CheckedByteCount(elementCount, (nuint)sizeof(T)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T* AllocateZeroed<T>(nuint elementCount, nuint elementSize) where T : unmanaged
            => (T*)AllocateZeroed(elementCount, elementSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T* Reallocate<T>(T* ptr, nuint newElementCount) where T : unmanaged
            => (T*)Reallocate(ptr, CheckedByteCount(newElementCount, (nuint)sizeof(T)));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T* Reallocate<T>(T* ptr, nuint newElementCount, nuint elementSize) where T : unmanaged
            => (T*)Reallocate(ptr, newElementCount, elementSize);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Free<T>(T* ptr) where T : unmanaged
            => Free((void*)ptr);
        #endregion

        #region Internals
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static nuint CheckedByteCount(nuint elementCount, nuint elementSize)
        {
            try
            {
                checked
                {
                    return elementCount * elementSize;
                }
            }
            catch (OverflowException)
            {
                throw new OutOfMemoryException($"Allocation of {elementCount} elements of size {elementSize} overflows the addressable byte count.");
            }
        }
        #endregion
    }
}
