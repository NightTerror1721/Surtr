#nullable enable

using Surtr.Runtime.Utilities;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// A process-wide, thread-local pool of unmanaged <see cref="SurtrRawValue"/> buffers for the
    /// collectable objects (<see cref="SurtrArray"/>, <see cref="SurtrInstance"/>,
    /// <see cref="SurtrTuple"/>, <see cref="SurtrClosure"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Renting from the pool is what makes unmanaged backing buffers pay off: without it, every
    /// short-lived object pays an HGlobal allocate and free (via <see cref="MemOps"/>), which is
    /// several times the cost of the managed-array allocation the pool replaces. With it, a
    /// created-and-collected object reuses the buffer the previous one left behind. The memory
    /// itself is the same HGlobal pool, so a buffer can be shared between any runtimes - it is
    /// raw memory with no type identity.
    /// </para>
    /// <para>
    /// Each thread keeps its own free lists, so the rent/return path is plain loads and stores -
    /// no lock and no <see cref="System.Threading.Interlocked"/> instruction. That matters: in a
    /// tight allocation stream the per-object pool bookkeeping is the entire overhead the pool is
    /// meant to amortise, and a single locked instruction per rent and per return would eat most
    /// of the gain. A buffer lives in exactly one place at any moment - an owning object or one
    /// thread's free stack - so thread-local pooling is correct by construction; a runtime whose
    /// VM and host collection run on different threads simply reuses buffers less, never wrongly.
    /// </para>
    /// <para>
    /// Buffers are bucketed by size class (power-of-two slot counts) and each class keeps a LIFO
    /// free stack of at most a fixed byte budget; anything beyond it, or any buffer above
    /// <see cref="MaxClassSlots"/>, is freed outright, so the pool's worst-case retention per
    /// thread is a few megabytes regardless of how many objects are created. A rented buffer's
    /// exposed prefix is zeroed before it leaves, so callers that relied on zeroed slots (instance
    /// fields, tuple packs, array slack) keep that guarantee.
    /// </para>
    /// </remarks>
    internal static unsafe class SurtrValueBufferPool
    {
        /// <summary>Classes above this many slots are never pooled; they allocate and free direct.</summary>
        private const int MaxClassSlots = 4096;

        /// <summary>How many bytes each class may retain idle before the overflow is freed.</summary>
        private const int BudgetBytesPerClass = 256 * 1024;

        /// <summary>Ceiling on idle buffers per class, so tiny classes cannot grow past reason.</summary>
        private const int MaxFreePerClass = 16384;

        /// <summary>Number of power-of-two classes from 2^0 to 2^12 (1..4096 slots).</summary>
        private const int ClassCount = 13;

        [ThreadStatic] private static SurtrRawValue*[][]? s_freeLists;
        [ThreadStatic] private static int[]? s_tops;

        private static SurtrRawValue*[][] Lists
        {
            get
            {
                if (s_freeLists is null)
                {
                    var lists = new SurtrRawValue*[ClassCount][];
                    for (int i = 0; i < ClassCount; i++)
                        lists[i] = new SurtrRawValue*[MaxFreeForClass(i)];
                    s_freeLists = lists;
                    s_tops = new int[ClassCount];
                }
                return s_freeLists;
            }
        }

        private static int[] Tops
        {
            get
            {
                _ = Lists; // guarantee both are initialized together
                return s_tops!;
            }
        }

        /// <summary>How many idle buffers the class may hold before it starts freeing.</summary>
        private static int MaxFreeForClass(int index)
        {
            int byBudget = BudgetBytesPerClass / (8 << index);
            return Math.Min(MaxFreePerClass, Math.Max(1, byBudget));
        }

        /// <summary>The class index whose size (in slots) is <c>1 &lt;&lt; index</c>.</summary>
        private static int ClassIndex(int slotCount)
        {
            int index = 0;
            int size = 1;
            while (size < slotCount)
            {
                size <<= 1;
                index++;
            }
            return index;
        }

        /// <summary>
        /// Rents a buffer of at least <paramref name="slotCount"/> slots, with the exposed prefix
        /// zeroed. <paramref name="capacity"/> reports the usable size, which may exceed
        /// <paramref name="slotCount"/> when the buffer came from (or will go back to) a size
        /// class.
        /// </summary>
        public static SurtrRawValue* Rent(int slotCount, out int capacity)
        {
            if (slotCount <= 0)
            {
                capacity = 0;
                return null;
            }

            if (slotCount > MaxClassSlots)
            {
                capacity = slotCount;
                return MemOps.AllocateZeroed<SurtrRawValue>((nuint)slotCount);
            }

            int index = ClassIndex(slotCount);
            capacity = 1 << index;

            var lists = Lists;
            var tops = Tops;
            int top = tops[index] - 1;
            if (top >= 0)
            {
                var list = lists[index];
                SurtrRawValue* ptr = list[top];
                list[top] = null;
                tops[index] = top;
                MemOps.Clear(ptr, (nuint)slotCount * sizeof(SurtrRawValue));
                return ptr;
            }

            return MemOps.AllocateZeroed<SurtrRawValue>((nuint)capacity);
        }

        /// <summary>Returns a rented buffer to the pool, or frees it if the class is full or too large.</summary>
        public static void Return(SurtrRawValue* ptr, int capacity)
        {
            if (ptr == null)
                return;

            if (capacity <= 0 || capacity > MaxClassSlots)
            {
                MemOps.Free(ptr);
                return;
            }

            int index = ClassIndex(capacity);
            var lists = Lists;
            var tops = Tops;
            int top = tops[index];
            if (top < MaxFreeForClass(index))
            {
                lists[index][top] = ptr;
                tops[index] = top + 1;
            }
            else
            {
                MemOps.Free(ptr);
            }
        }
    }
}