#nullable enable

using Surtr.Runtime.Utilities;
using System;
using System.Numerics;

namespace Surtr.Tests.Runtime.Utilities
{
    public unsafe class MemOpsTests
    {
        // Vector<byte>.Count depends on the hardware running the suite (16/32/64 for
        // SSE2/AVX2/AVX-512), so boundary sizes are derived from it rather than hardcoded -
        // otherwise this suite would only ever exercise the tail loop on some machines.
        private static readonly int VectorSize = Vector<byte>.Count;

        public static TheoryData<int> BoundaryByteCounts()
        {
            var data = new TheoryData<int>();
            foreach (int size in new[]
            {
                0, 1, 3,
                VectorSize - 1, VectorSize, VectorSize + 1,
                (2 * VectorSize) - 1, 2 * VectorSize, (2 * VectorSize) + 1,
                (3 * VectorSize) + 7,
            })
            {
                if (size >= 0)
                    data.Add(size);
            }
            return data;
        }

        [Theory]
        [MemberData(nameof(BoundaryByteCounts))]
        public void Clear_ZeroesEveryByte(int byteCount)
        {
            byte* buffer = MemOps.Allocate<byte>((nuint)byteCount);
            try
            {
                for (int i = 0; i < byteCount; i++)
                    buffer[i] = 0xFF;

                MemOps.Clear(buffer, (nuint)byteCount);

                Assert.True(MemOps.IsZero(buffer, (nuint)byteCount));
            }
            finally
            {
                MemOps.Free(buffer);
            }
        }

        [Theory]
        [MemberData(nameof(BoundaryByteCounts))]
        public void Fill_SetsEveryByteToThePattern(int byteCount)
        {
            const byte pattern = 0xAB;

            byte* buffer = MemOps.Allocate<byte>((nuint)byteCount);
            try
            {
                MemOps.Fill(buffer, (nuint)byteCount, pattern);

                for (int i = 0; i < byteCount; i++)
                    Assert.Equal(pattern, buffer[i]);
            }
            finally
            {
                MemOps.Free(buffer);
            }
        }

        [Fact]
        public void Fill_WithZero_BehavesLikeClear()
        {
            const int byteCount = 64;
            byte* buffer = MemOps.Allocate<byte>(byteCount);
            try
            {
                for (int i = 0; i < byteCount; i++)
                    buffer[i] = 0xFF;

                MemOps.Fill(buffer, byteCount, 0);

                Assert.True(MemOps.IsZero(buffer, byteCount));
            }
            finally
            {
                MemOps.Free(buffer);
            }
        }

        [Theory]
        [MemberData(nameof(BoundaryByteCounts))]
        public void AreEqual_ForIdenticalContent_IsTrue(int byteCount)
        {
            byte* left = MemOps.Allocate<byte>((nuint)byteCount);
            byte* right = MemOps.Allocate<byte>((nuint)byteCount);
            try
            {
                for (int i = 0; i < byteCount; i++)
                    left[i] = right[i] = (byte)(i * 31);

                Assert.True(MemOps.AreEqual(left, right, (nuint)byteCount));
            }
            finally
            {
                MemOps.Free(left);
                MemOps.Free(right);
            }
        }

        [Theory]
        [MemberData(nameof(BoundaryByteCounts))]
        public void AreEqual_DetectsADifferenceInTheFirstByte(int byteCount)
        {
            if (byteCount == 0)
                return;

            byte* left = MemOps.Allocate<byte>((nuint)byteCount);
            byte* right = MemOps.Allocate<byte>((nuint)byteCount);
            try
            {
                MemOps.Clear(left, (nuint)byteCount);
                MemOps.Clear(right, (nuint)byteCount);
                right[0] = 1;

                Assert.False(MemOps.AreEqual(left, right, (nuint)byteCount));
            }
            finally
            {
                MemOps.Free(left);
                MemOps.Free(right);
            }
        }

        [Theory]
        [MemberData(nameof(BoundaryByteCounts))]
        public void AreEqual_DetectsADifferenceInTheLastByte(int byteCount)
        {
            if (byteCount == 0)
                return;

            byte* left = MemOps.Allocate<byte>((nuint)byteCount);
            byte* right = MemOps.Allocate<byte>((nuint)byteCount);
            try
            {
                MemOps.Clear(left, (nuint)byteCount);
                MemOps.Clear(right, (nuint)byteCount);
                right[byteCount - 1] = 1;

                Assert.False(MemOps.AreEqual(left, right, (nuint)byteCount));
            }
            finally
            {
                MemOps.Free(left);
                MemOps.Free(right);
            }
        }

        [Fact]
        public void AreEqual_ForTheSamePointer_IsTrueWithoutReading()
        {
            // Passing a null pointer for both sides would crash if AreEqual read through it -
            // the left == right fast path has to short-circuit before any dereference.
            Assert.True(MemOps.AreEqual(null, null, 128));
        }

        [Theory]
        [MemberData(nameof(BoundaryByteCounts))]
        public void IsZero_DetectsANonZeroByteAnywhere(int byteCount)
        {
            for (int position = 0; position < byteCount; position++)
            {
                byte* buffer = MemOps.Allocate<byte>((nuint)byteCount);
                try
                {
                    MemOps.Clear(buffer, (nuint)byteCount);
                    buffer[position] = 1;

                    Assert.False(MemOps.IsZero(buffer, (nuint)byteCount));
                }
                finally
                {
                    MemOps.Free(buffer);
                }
            }
        }

        [Fact]
        public void Copy_DuplicatesNonOverlappingContent()
        {
            byte* source = MemOps.Allocate<byte>(32);
            byte* destination = MemOps.Allocate<byte>(32);
            try
            {
                for (int i = 0; i < 32; i++)
                    source[i] = (byte)i;
                MemOps.Clear(destination, 32);

                MemOps.Copy(source, destination, 32);

                Assert.True(MemOps.AreEqual(source, destination, 32));
            }
            finally
            {
                MemOps.Free(source);
                MemOps.Free(destination);
            }
        }

        [Fact]
        public void Move_HandlesForwardOverlap_LikeMemmove()
        {
            // Shifting a buffer right by one byte within itself only round-trips correctly
            // under memmove semantics; a naive forward memcpy would clobber the tail before
            // reading it.
            byte* buffer = MemOps.Allocate<byte>(16);
            try
            {
                for (int i = 0; i < 16; i++)
                    buffer[i] = (byte)i;

                MemOps.Move(buffer, buffer + 1, 15);

                Assert.Equal(0, buffer[0]);
                for (int i = 1; i < 16; i++)
                    Assert.Equal((byte)(i - 1), buffer[i]);
            }
            finally
            {
                MemOps.Free(buffer);
            }
        }

        [Fact]
        public void AllocateZeroed_ReturnsZeroedMemory()
        {
            byte* buffer = MemOps.AllocateZeroed<byte>(256);
            try
            {
                Assert.True(MemOps.IsZero(buffer, 256));
            }
            finally
            {
                MemOps.Free(buffer);
            }
        }

        [Fact]
        public void Reallocate_PreservesExistingContent_WhenGrowing()
        {
            int* buffer = MemOps.Allocate<int>(4);
            try
            {
                for (int i = 0; i < 4; i++)
                    buffer[i] = i + 1;

                buffer = MemOps.Reallocate(buffer, 64);

                for (int i = 0; i < 4; i++)
                    Assert.Equal(i + 1, buffer[i]);
            }
            finally
            {
                MemOps.Free(buffer);
            }
        }

        [Fact]
        public void Reallocate_ANullPointer_AllocatesFresh()
        {
            int* buffer = null;
            buffer = MemOps.Reallocate(buffer, 8);
            try
            {
                Assert.True((IntPtr)buffer != IntPtr.Zero);
            }
            finally
            {
                MemOps.Free(buffer);
            }
        }

        [Fact]
        public void Free_ANullPointer_IsANoOp()
        {
            MemOps.Free((void*)null);
        }

        [Fact]
        public void Allocate_ByElementCountAndSize_OverflowingByteCount_ThrowsOutOfMemory()
        {
            Assert.Throws<OutOfMemoryException>(() => MemOps.Allocate(nuint.MaxValue, (nuint)2));
        }
    }
}
