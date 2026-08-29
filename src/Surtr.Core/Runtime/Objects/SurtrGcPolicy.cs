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

        /// <summary>
        /// The most entities the heap may ever hold live at once, or <c>0</c> - the default - for
        /// no limit.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A hard ceiling, not a collection trigger: unlike <see cref="AllocationThreshold"/> and
        /// <see cref="LiveEntityThresholdPercent"/>, it is <em>not</em> folded away in
        /// <see cref="SurtrGcMode.Manual"/> - a host sandboxing untrusted script content wants the
        /// cap enforced whether or not it drives collection itself.
        /// </para>
        /// <para>
        /// Checked when the registry's backing storage would otherwise grow to accommodate more
        /// entities - the same cold path an ordinary capacity doubling already runs on, so an
        /// allocation that reuses a freed id never pays for the check. That means the cap is a
        /// ceiling on how large the heap may grow, not an instantaneous count: a runtime whose
        /// initial capacity already sits at or above it is unaffected until growth is next needed.
        /// A host that wants the cap enforced from the very first allocation should size the
        /// runtime's initial entity capacity at or below it.
        /// </para>
        /// </remarks>
        public long MaxLiveEntities { get; }

        /// <summary>Creates a policy with the given settings.</summary>
        /// <param name="mode">How the collector is allowed to run on its own.</param>
        /// <param name="allocationThreshold">See <see cref="AllocationThreshold"/>.</param>
        /// <param name="liveEntityThresholdPercent">See <see cref="LiveEntityThresholdPercent"/>.</param>
        /// <param name="nurseryFrequency">See <see cref="NurseryFrequency"/>.</param>
        /// <param name="maxLiveEntities">
        /// The most entities the heap may hold live at once, or <c>0</c> for no limit. See
        /// <see cref="MaxLiveEntities"/>.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="allocationThreshold"/> is below <c>1</c>,
        /// <paramref name="liveEntityThresholdPercent"/> is outside <c>[0, 100]</c>,
        /// <paramref name="nurseryFrequency"/> is below <c>1</c>, or
        /// <paramref name="maxLiveEntities"/> is negative.
        /// </exception>
        public SurtrGcPolicy(
            SurtrGcMode mode,
            long allocationThreshold,
            int liveEntityThresholdPercent,
            int nurseryFrequency,
            long maxLiveEntities = 0)
        {
            if (allocationThreshold < 1)
                throw new ArgumentOutOfRangeException(nameof(allocationThreshold), allocationThreshold, "The allocation threshold must be at least 1.");

            if (liveEntityThresholdPercent is < 0 or > 100)
                throw new ArgumentOutOfRangeException(nameof(liveEntityThresholdPercent), liveEntityThresholdPercent, "The live-entity threshold must be within [0, 100].");

            if (nurseryFrequency < 1)
                throw new ArgumentOutOfRangeException(nameof(nurseryFrequency), nurseryFrequency, "The nursery frequency must be at least 1.");

            if (maxLiveEntities < 0)
                throw new ArgumentOutOfRangeException(nameof(maxLiveEntities), maxLiveEntities, "The live-entity cap cannot be negative.");

            Mode = mode;
            AllocationThreshold = allocationThreshold;
            LiveEntityThresholdPercent = liveEntityThresholdPercent;
            NurseryFrequency = nurseryFrequency;
            MaxLiveEntities = maxLiveEntities;
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