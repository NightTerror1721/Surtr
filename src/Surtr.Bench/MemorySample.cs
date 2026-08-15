#nullable enable

using System;

namespace Surtr.Bench
{
    /// <summary>
    /// One engine's memory counters at an instant, so the harness can difference two of them across
    /// a run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every field is a running total that only ever grows, never a rate or a delta, because a
    /// counter that resets cannot be read twice around a region. <see cref="LiveObjects"/> is the
    /// exception and is deliberately an instantaneous level rather than a total — differencing it
    /// is what says how much a run <em>kept</em>, as against how much it merely touched.
    /// </para>
    /// <para>
    /// <see cref="Unavailable"/> marks a figure an engine cannot produce rather than a zero, since
    /// zero is a real and interesting answer here: <c>intLoop</c> genuinely allocates nothing, and
    /// a column that renders "no allocation" and "no idea" identically is worse than one that omits
    /// the engine.
    /// </para>
    /// </remarks>
    internal readonly struct MemorySample
    {
        /// <summary>The value of a counter the engine does not expose.</summary>
        public const long Unavailable = -1;

        /// <summary>Bytes allocated since the engine started, on the heap it allocates from.</summary>
        public readonly long AllocatedBytes;

        /// <summary>Engine-level objects allocated since it started.</summary>
        public readonly long AllocatedObjects;

        /// <summary>Engine-level objects alive right now.</summary>
        public readonly long LiveObjects;

        /// <summary>Bytes the engine's heap currently holds, live or not.</summary>
        public readonly long HeapBytes;

        public MemorySample(long allocatedBytes, long allocatedObjects, long liveObjects, long heapBytes)
        {
            AllocatedBytes = allocatedBytes;
            AllocatedObjects = allocatedObjects;
            LiveObjects = liveObjects;
            HeapBytes = heapBytes;
        }

        /// <summary>A sample from an engine that exposes nothing.</summary>
        public static MemorySample None => new MemorySample(Unavailable, Unavailable, Unavailable, Unavailable);

        /// <summary>
        /// Subtracts an earlier sample from this one, field by field, keeping
        /// <see cref="Unavailable"/> wherever either side had it.
        /// </summary>
        public MemorySample Since(MemorySample earlier) => new MemorySample(
            Difference(AllocatedBytes, earlier.AllocatedBytes),
            Difference(AllocatedObjects, earlier.AllocatedObjects),
            Difference(LiveObjects, earlier.LiveObjects),
            HeapBytes);

        private static long Difference(long now, long before)
            => now == Unavailable || before == Unavailable ? Unavailable : Math.Max(0, now - before);
    }
}
