#nullable enable

using System;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// How a runtime's collector may run on its own.
    /// </summary>
    /// <remarks>
    /// <see cref="SurtrRuntime.Collect(bool)"/> always works regardless of the mode: the automatic path
    /// only decides whether the runtime collects <em>by itself</em> at its safepoints. That makes
    /// a "hybrid" mode unnecessary - an <see cref="Automatic"/> runtime is already manual-plus-auto.
    /// </remarks>
    public enum SurtrGcMode : byte
    {
        /// <summary>
        /// Only the host collects, by calling <see cref="SurtrRuntime.Collect(bool)"/>.
        /// </summary>
        Manual = 0,

        /// <summary>
        /// The runtime also collects by itself at safepoints, whenever the policy's thresholds are
        /// crossed. Manual calls still work.
        /// </summary>
        Automatic = 1,
    }

    /// <summary>
    /// Configures when a runtime's collector runs on its own, and how it sweeps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read-only and value-typed so a copy can sit on the hot path without an allocation. A runtime
    /// stores one of these verbatim inside its <see cref="SurtrEntityRegistry"/> and folds
    /// <see cref="Mode"/> away at configuration time: in <see cref="SurtrGcMode.Manual"/> the
    /// allocation threshold becomes <see cref="long.MaxValue"/>, so the per-allocation check is a
    /// single compare that is never taken and predicts perfectly.
    /// </para>
    /// <para>
    /// Collection is <em>deferred to a safepoint</em>, never inlined into an allocation: an
    /// allocation can be mid-construction (an array's elements are filled after it is registered),
    /// so running a sweep inside it could reclaim the very object being built. The policy only
    /// arms a flag; the interpreter collects at its next allocation safepoint or native boundary.
    /// </para>
    /// </remarks>
    public readonly struct SurtrGcPolicy
    {
        /// <summary>How the collector is allowed to run on its own.</summary>
        public SurtrGcMode Mode { get; }

        /// <summary>
        /// How many entity allocations since the last collection may happen before the runtime
        /// considers collecting. Ignored in <see cref="SurtrGcMode.Manual"/>.
        /// </summary>
        public long AllocationThreshold { get; }

        /// <summary>
        /// How full the registry's live set must be, as a percentage of its capacity, before the
        /// runtime considers collecting. <c>0</c> disables the trigger. Ignored in
        /// <see cref="SurtrGcMode.Manual"/>.
        /// </summary>
        public int LiveEntityThresholdPercent { get; }

        /// <summary>
        /// How many collections may run before the next one is a full sweep: every
        /// <see cref="NurseryFrequency"/>-th collection sweeps everything, and the ones in between
        /// spare entities that survived a previous collection. <c>1</c> makes every collection
        /// full.
        /// </summary>
        public int NurseryFrequency { get; }

        /// <summary>Creates a policy with the given settings.</summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="allocationThreshold"/> is below <c>1</c>,
        /// <paramref name="liveEntityThresholdPercent"/> is outside <c>[0, 100]</c>, or
        /// <paramref name="nurseryFrequency"/> is below <c>1</c>.
        /// </exception>
        public SurtrGcPolicy(
            SurtrGcMode mode,
            long allocationThreshold,
            int liveEntityThresholdPercent,
            int nurseryFrequency)
        {
            if (allocationThreshold < 1)
                throw new ArgumentOutOfRangeException(nameof(allocationThreshold), allocationThreshold, "The allocation threshold must be at least 1.");

            if (liveEntityThresholdPercent is < 0 or > 100)
                throw new ArgumentOutOfRangeException(nameof(liveEntityThresholdPercent), liveEntityThresholdPercent, "The live-entity threshold must be within [0, 100].");

            if (nurseryFrequency < 1)
                throw new ArgumentOutOfRangeException(nameof(nurseryFrequency), nurseryFrequency, "The nursery frequency must be at least 1.");

            Mode = mode;
            AllocationThreshold = allocationThreshold;
            LiveEntityThresholdPercent = liveEntityThresholdPercent;
            NurseryFrequency = nurseryFrequency;
        }

        /// <summary>The policy a freshly-initialized runtime uses: never collects on its own.</summary>
        public static SurtrGcPolicy Manual { get; } = new(SurtrGcMode.Manual, long.MaxValue, 0, 1);

        /// <summary>
        /// The policy a freshly-initialized runtime uses when it is created without one being
        /// configured: collects after 10 000 allocations, or when the live set fills 75 % of the
        /// registry's capacity, with every collection a full sweep.
        /// </summary>
        public static SurtrGcPolicy Automatic { get; } = new(SurtrGcMode.Automatic, 10_000, 75, 1);
    }
}