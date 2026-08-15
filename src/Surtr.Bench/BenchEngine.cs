#nullable enable

using System;

namespace Surtr.Bench
{
    /// <summary>
    /// One benchmarked engine. <see cref="Call"/> runs a workload once and returns its numeric
    /// result; the harness times it, verifies it against the C# baseline and compares engines by
    /// their medians. <see cref="Collect"/> settles the engine's own heap between samples.
    /// </summary>
    internal interface IBenchEngine
    {
        /// <summary>The column heading this engine prints under.</summary>
        string Name { get; }

        /// <summary>Runs one workload once, with <paramref name="size"/> as its argument.</summary>
        double Call(Workload workload, long size);

        /// <summary>
        /// Settles the engine's heap between samples. Nothing here is timed. The managed engines
        /// are settled by the CLR collection in <see cref="Runner.Measure"/> already, so the default
        /// is a no-op; only the Surtr driver overrides it with its own collector.
        /// </summary>
        void Collect() { }

        /// <summary>
        /// The engine's memory counters right now, for the harness to difference across a run.
        /// </summary>
        /// <remarks>
        /// The default reports the CLR allocation counter, which is the right answer for every
        /// engine whose objects are CLR objects — Surtr, MoonSharp and the baseline all allocate on
        /// the managed heap, so the same counter sees all three and the figures are comparable. An
        /// engine with a heap of its own overrides this; LuaJIT does, and Surtr extends it with the
        /// object-level counts only it can give.
        /// </remarks>
        MemorySample SampleMemory() => new MemorySample(
            GC.GetAllocatedBytesForCurrentThread(),
            MemorySample.Unavailable,
            MemorySample.Unavailable,
            MemorySample.Unavailable);
    }
}
