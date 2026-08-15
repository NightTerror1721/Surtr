#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Surtr.Bench
{
    /// <summary>A set of timed samples distilled to a median, the spread around it, and what one run allocated.</summary>
    internal readonly struct Measurement
    {
        public readonly double Median;
        public readonly double Min;
        public readonly double Max;

        /// <summary>The lower and upper quartiles, which is what <see cref="Spread"/> is measured between.</summary>
        public readonly double LowerQuartile;
        public readonly double UpperQuartile;

        /// <summary>What one run cost in memory, measured outside the timed samples.</summary>
        public readonly MemorySample Memory;

        public Measurement(double median, double min, double max, double lowerQuartile, double upperQuartile, MemorySample memory)
        {
            Median = median;
            Min = min;
            Max = max;
            LowerQuartile = lowerQuartile;
            UpperQuartile = upperQuartile;
            Memory = memory;
        }

        /// <summary>
        /// The spread as a fraction of the median, measured across the interquartile range rather
        /// than min-to-max: one descheduled sample should not get to describe the whole run. A
        /// figure above roughly 10% means the median is not yet something to draw a conclusion from.
        /// </summary>
        public double Spread => Median > 0 ? (UpperQuartile - LowerQuartile) / Median : 0;
    }

    /// <summary>Runs the catalogue: builds the engines, times each workload, verifies, reports.</summary>
    internal sealed class Runner
    {
        private readonly RunnerOptions _options;

        public Runner(RunnerOptions options)
        {
            _options = options;
        }

        public int Run()
        {
            if (_options.ListOnly)
            {
                ListCatalogue();
                return 0;
            }

            SurtrDriver? surtr = null;
            LuaDriver? moon = null;
            NativeLuaDriver? luajit = null;
            try
            {
                if (_options.RunSurtr)
                    surtr = SurtrDriver.Build(Workloads.ModuleSource);
                if (_options.RunMoonSharp)
                    moon = LuaDriver.Load(Workloads.LuaSource);
                if (_options.RunLuaJit)
                    luajit = NativeLuaDriver.Load(Workloads.LuaSource);

                // Surtr first: it is the reference engine the ratios and the geomeans are relative to.
                var engines = new List<IBenchEngine>();
                if (surtr != null) engines.Add(surtr);
                if (moon != null) engines.Add(moon);
                if (luajit != null) engines.Add(luajit);

                PrintHeader(engines);

                var rows = new List<CsvRow>();
                bool allOk = true;

                foreach (var workload in Workloads.AllWorkloads)
                {
                    if (!Matches(workload.Name))
                        continue;

                    long size = ScaledSize(workload.Size);
                    double expected = RunBaseline(workload, size);

                    // One run per engine per case, checked against the C# reference. Three
                    // implementations of the same algorithm agreeing on a checksum is the only
                    // thing standing between a fast engine and one that is fast because it is not
                    // doing the work — see the note on Workloads.
                    var results = new double[engines.Count];
                    for (int i = 0; i < engines.Count; i++)
                        results[i] = engines[i].Call(workload, size);
                    bool ok = Verified(workload, expected, results);
                    allOk = allOk && ok;

                    if (!ok)
                        ReportDisagreement(workload, expected, results, engines);

                    var engineMs = new Measurement[engines.Count];
                    for (int i = 0; i < engines.Count; i++)
                    {
                        IBenchEngine engine = engines[i];
                        engineMs[i] = Measure(
                            () => engine.Call(workload, size),
                            _options.Iterations,
                            _options.WarmupIterations,
                            engine.Collect,
                            engine.SampleMemory);
                    }
                    Measurement baselineMs = Measure(() => RunBaseline(workload, size), _options.Iterations, _options.WarmupIterations);

                    PrintRow(workload, size, engineMs, baselineMs, ok, engines);
                    rows.Add(new CsvRow(workload.Name, size, workload.Measures, engineMs, baselineMs, ok));
                }

                PrintSummary(rows, engines);

                if (_options.CsvPath != null)
                    AppendCsv(_options.CsvPath, rows, engines);

                return allOk ? 0 : 1;
            }
            finally
            {
                surtr?.Dispose();
                luajit?.Dispose();
            }
        }

        /// <summary>
        /// Prints what each case is for. A workload's name says what it is called and its timing
        /// says what it cost; neither says which VM mechanism it was chosen to put under load, and
        /// that is the thing you need in order to know whether a row moving is the row you meant.
        /// </summary>
        private void ListCatalogue()
        {
            int shown = 0;
            Console.WriteLine("{0}  {1}  {2}", Pad("workload", 16), Pad("size", 9), "measures");

            foreach (var workload in Workloads.AllWorkloads)
            {
                if (!Matches(workload.Name))
                    continue;

                Console.WriteLine(
                    "{0}  {1}  {2}",
                    Pad(workload.Name, 16),
                    Pad(workload.Size.ToString(CultureInfo.InvariantCulture), 9),
                    workload.Measures);
                shown++;
            }

            Console.WriteLine();
            Console.WriteLine("{0} cases.", shown);
        }

        private bool Matches(string name)
            => _options.WorkloadFilter == null || name.IndexOf(_options.WorkloadFilter, StringComparison.OrdinalIgnoreCase) >= 0;

        private long ScaledSize(long size)
            => Math.Max(1L, (long)(size * _options.Scale));

        private static double RunBaseline(Workload workload, long size)
            => workload.Kind == WorkloadKind.Int ? workload.BaselineInt!(size) : workload.BaselineFloat!(size);

        /// <summary>
        /// Says which engine disagreed and by how much. A bare FAIL says only that the three
        /// implementations are not the same algorithm any more, which is the one thing about a
        /// failure that was already obvious.
        /// </summary>
        private static void ReportDisagreement(
            Workload workload,
            double expected,
            double[] results,
            IReadOnlyList<IBenchEngine> engines)
        {
            Console.WriteLine("  {0}: the C# baseline says {1}", workload.Name, Format(expected));
            for (int i = 0; i < engines.Count; i++)
            {
                if (!Agrees(workload, expected, results[i]))
                    Console.WriteLine("    {0} says {1}", engines[i].Name, Format(results[i]));
            }

            static string Format(double value)
                => value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool Agrees(Workload workload, double expected, double result)
        {
            if (workload.Kind == WorkloadKind.Int)
                return Math.Abs(result - expected) < 0.5;

            double tolerance = Math.Max(1.0, Math.Abs(expected)) * 1e-9;
            return Math.Abs(result - expected) <= tolerance;
        }

        private static bool Verified(Workload workload, double expected, double[] results)
        {
            if (workload.Kind == WorkloadKind.Int)
            {
                foreach (double result in results)
                    if (Math.Abs(result - expected) >= 0.5)
                        return false;
                return true;
            }

            double tolerance = Math.Max(1.0, Math.Abs(expected)) * 1e-9;
            foreach (double result in results)
                if (Math.Abs(result - expected) > tolerance)
                    return false;
            return true;
        }

        /// <summary>
        /// Times one operation: <paramref name="warmupIterations"/> untimed runs, then
        /// <paramref name="iterations"/> timed ones. The median is the reportable number and the
        /// quartiles give the spread. The CLR heap is settled before every sample so no engine
        /// inherits the previous one's debris, and <paramref name="settle"/> runs the engine's own
        /// collector the same way — but nothing in the timed region.
        /// </summary>
        /// <remarks>
        /// The warm-up is a real phase rather than the single run it used to be, and it is what a
        /// median is worth anything on. It has two jobs: drive the JIT past first-call compilation
        /// on every path the workload touches (the csproj forces loop-bearing methods straight to
        /// optimized code, but a method without a loop still tiers up on call count), and let the
        /// heap reach the size the workload actually wants so the first timed sample is not paying
        /// for growth every later one inherits.
        /// </remarks>
        private static Measurement Measure(
            Func<double> operation,
            int iterations,
            int warmupIterations,
            Action? settle = null,
            Func<MemorySample>? sampleMemory = null)
        {
            for (int i = 0; i < warmupIterations; i++)
                operation();

            // Memory is counted on its own run, outside the samples: reading the counters costs
            // several calls, and a run that is being measured for time should have nothing else in
            // it. The heap is settled first so what the run allocates is not confused with what an
            // earlier one left behind.
            settle?.Invoke();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            sampleMemory ??= DefaultMemorySample;
            MemorySample before = sampleMemory();
            operation();
            MemorySample memory = sampleMemory().Since(before);

            var samples = new double[iterations];
            for (int i = 0; i < iterations; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                settle?.Invoke();

                var stopwatch = Stopwatch.StartNew();
                operation();
                stopwatch.Stop();
                samples[i] = stopwatch.Elapsed.TotalMilliseconds;
            }

            Array.Sort(samples);
            return new Measurement(
                samples[iterations / 2],
                samples[0],
                samples[iterations - 1],
                samples[iterations / 4],
                samples[Math.Min(iterations - 1, (iterations * 3) / 4)],
                memory);
        }

        /// <summary>What the C# baseline is measured with: the CLR counter and nothing else.</summary>
        private static MemorySample DefaultMemorySample() => new MemorySample(
            GC.GetAllocatedBytesForCurrentThread(),
            MemorySample.Unavailable,
            MemorySample.Unavailable,
            MemorySample.Unavailable);

        private void PrintHeader(IReadOnlyList<IBenchEngine> engines)
        {
            Console.WriteLine(
                "Surtr benchmark suite: median of {0} timed runs after {1} warm-up runs, sizes fixed",
                _options.Iterations,
                _options.WarmupIterations);

            var names = new List<string>();
            foreach (IBenchEngine engine in engines)
                names.Add(engine.Name);
            names.Add("C# baseline");
            Console.WriteLine("Engines: {0}", string.Join(" vs ", names));

            Console.WriteLine("Surtr collects its heap between runs only, never inside the timed region.");
            Console.WriteLine();
            Console.WriteLine("Memory columns are per run, measured outside the timed samples:");
            Console.WriteLine("  bytes   managed bytes allocated (comparable across surtr, lua and c#: all three allocate on the CLR heap)");
            Console.WriteLine("  objs    Surtr objects allocated, from the entity registry");
            Console.WriteLine("  kept    Surtr objects still live when the run returned");
            Console.WriteLine("  c#B     managed bytes the C# baseline allocated, for the same work");
            Console.WriteLine("  spread  interquartile range over the median; above ~10% the median is not yet worth quoting");
            Console.WriteLine();

            var header = new List<string> { Pad("workload", 15), Pad("size", 9) };
            foreach (IBenchEngine engine in engines)
                header.Add(Pad(engine.Name + " ms", 11));
            header.Add(Pad("c# ms", 11));
            for (int i = 1; i < engines.Count; i++)
                header.Add(Pad("vs " + engines[i].Name, 10));
            header.Add(Pad("vs c#", 8));
            header.Add(Pad("bytes", 9));
            header.Add(Pad("objs", 8));
            header.Add(Pad("kept", 8));
            header.Add(Pad("c#B", 9));
            header.Add(Pad("spread", 8));
            Console.WriteLine(string.Join("  ", header));
        }

        private static void PrintRow(
            Workload workload,
            long size,
            Measurement[] engineMs,
            Measurement baselineMs,
            bool ok,
            IReadOnlyList<IBenchEngine> engines)
        {
            var cells = new List<string>
            {
                Pad(workload.Name, 15),
                Pad(size.ToString(CultureInfo.InvariantCulture), 9),
            };

            foreach (Measurement measurement in engineMs)
                cells.Add(Pad(FormatMs(measurement.Median), 11));
            cells.Add(Pad(FormatMs(baselineMs.Median), 11));

            // One ratio per engine after the first: how much slower that engine is than the
            // reference (Surtr, which is always first in the list).
            for (int i = 1; i < engines.Count; i++)
            {
                string ratio = engineMs[0].Median > 0 && engineMs[i].Median > 0
                    ? FormatRatio(engineMs[i].Median / engineMs[0].Median)
                    : "  —  ";
                cells.Add(Pad(ratio, 10));
            }

            // How many times the C# baseline Surtr costs. Printed as its own column rather than
            // left for the reader to divide, because it is the number that says what the language
            // costs over writing the same thing by hand.
            string overBaseline = engineMs.Length > 0 && engineMs[0].Median > 0 && baselineMs.Median > 0
                ? FormatRatio(engineMs[0].Median / baselineMs.Median)
                : "  —  ";
            cells.Add(Pad(overBaseline, 8));

            MemorySample reference = engineMs.Length > 0 ? engineMs[0].Memory : MemorySample.None;
            cells.Add(Pad(FormatBytes(reference.AllocatedBytes), 9));
            cells.Add(Pad(FormatCount(reference.AllocatedObjects), 8));
            cells.Add(Pad(FormatCount(reference.LiveObjects), 8));
            cells.Add(Pad(FormatBytes(baselineMs.Memory.AllocatedBytes), 9));
            cells.Add(Pad(engineMs.Length > 0 ? FormatPercent(engineMs[0].Spread) : "  —  ", 8));
            cells.Add(ok ? "ok" : "FAIL");

            Console.WriteLine(string.Join("  ", cells));
        }

        private static void PrintSummary(List<CsvRow> rows, IReadOnlyList<IBenchEngine> engines)
        {
            bool wrote = false;
            for (int i = 1; i < engines.Count; i++)
            {
                double logSum = 0;
                int count = 0;
                foreach (var row in rows)
                {
                    double referenceMs = row.EngineMeasurements[0].Median;
                    double otherMs = row.EngineMeasurements[i].Median;
                    if (referenceMs > 0 && otherMs > 0)
                    {
                        logSum += Math.Log(otherMs / referenceMs);
                        count++;
                    }
                }

                if (count > 0)
                {
                    if (!wrote)
                    {
                        Console.WriteLine();
                        wrote = true;
                    }

                    double geometricMean = Math.Exp(logSum / count);
                    Console.WriteLine(
                        "geometric mean speed-up ({0} over {1}, {2} cases): {3}x",
                        engines[0].Name,
                        engines[i].Name,
                        count,
                        geometricMean.ToString("F1", CultureInfo.InvariantCulture));
                }
            }
        }

        private static void AppendCsv(string path, List<CsvRow> rows, IReadOnlyList<IBenchEngine> engines)
        {
            bool appendHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            var line = new StringBuilder();
            using (var writer = new StreamWriter(path, append: true))
            {
                if (appendHeader)
                {
                    line.Append("workload,size");
                    foreach (IBenchEngine engine in engines)
                        line.Append(',').Append(engine.Name).Append("_ms");
                    line.Append(",csharp_ms");
                    for (int i = 1; i < engines.Count; i++)
                    {
                        line.Append(',').Append(engines[0].Name).Append("_over_").Append(engines[i].Name);
                        line.Append(',').Append(engines[i].Name).Append("_over_").Append(engines[0].Name);
                    }
                    line.Append(",surtr_over_csharp,measures");
                    line.Append(",surtr_alloc_bytes,surtr_alloc_objects,surtr_kept_objects,surtr_heap_bytes");
                    line.Append(",lua_alloc_bytes,luajit_heap_bytes,csharp_alloc_bytes");
                    line.Append(",spread_pct,ok");
                    writer.WriteLine(line);
                }

                foreach (var row in rows)
                {
                    line.Clear();
                    line.Append(row.Name);
                    line.Append(',').Append(row.Size.ToString(CultureInfo.InvariantCulture));
                    foreach (Measurement measurement in row.EngineMeasurements)
                        line.Append(',').Append(measurement.Median.ToString("F3", CultureInfo.InvariantCulture));
                    line.Append(',').Append(row.BaselineMs.ToString("F3", CultureInfo.InvariantCulture));
                    for (int i = 1; i < row.EngineMeasurements.Length; i++)
                    {
                        if (row.EngineMeasurements[0].Median > 0 && row.EngineMeasurements[i].Median > 0)
                        {
                            line.Append(',').Append((row.EngineMeasurements[i].Median / row.EngineMeasurements[0].Median).ToString("F3", CultureInfo.InvariantCulture));
                            line.Append(',').Append((row.EngineMeasurements[0].Median / row.EngineMeasurements[i].Median).ToString("F3", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            line.Append(",,");
                        }
                    }
                    if (row.EngineMeasurements.Length > 0 && row.EngineMeasurements[0].Median > 0 && row.BaselineMs > 0)
                        line.Append(',').Append((row.EngineMeasurements[0].Median / row.BaselineMs).ToString("F3", CultureInfo.InvariantCulture));
                    else
                        line.Append(',');

                    // Quoted: several of these contain a comma, and an unquoted one would shift
                    // every column after it by a field.
                    line.Append(",\"").Append(row.Measures.Replace("\"", "\"\"")).Append('"');

                    // Named by engine rather than by position: the columns a run produces depend on
                    // which engines it was given, and a reader joining two CSVs from different
                    // invocations should not have to work out which is which.
                    AppendMemory(line, MemoryOf(row, engines, "surtr"), objectsAndHeap: true);
                    AppendMemory(line, MemoryOf(row, engines, "lua"), objectsAndHeap: false);

                    MemorySample luajit = MemoryOf(row, engines, "luajit");
                    line.Append(',').Append(Number(luajit.HeapBytes));
                    line.Append(',').Append(Number(row.BaselineMemory.AllocatedBytes));

                    if (row.EngineMeasurements.Length > 0)
                        line.Append(',').Append(FormatPercent(row.EngineMeasurements[0].Spread));
                    else
                        line.Append(',');

                    line.Append(',').Append(row.Ok ? "ok" : "FAIL");
                    writer.WriteLine(line);
                }
            }
        }

        private static string FormatMs(double milliseconds)
            => milliseconds.ToString("F3", CultureInfo.InvariantCulture);

        private static string FormatRatio(double ratio)
            => ratio.ToString("F1", CultureInfo.InvariantCulture) + "x";

        private static string FormatPercent(double fraction)
            => (fraction * 100.0).ToString("F1", CultureInfo.InvariantCulture) + "%";

        /// <summary>
        /// Bytes at three significant figures. A VM running inside a frame budget is judged on this
        /// as much as on time — a run that allocates a megabyte hands the collector a bill that
        /// comes due in some later frame, and the timing column cannot show that.
        /// </summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes == MemorySample.Unavailable)
                return "  —  ";
            if (bytes < 1024)
                return bytes.ToString(CultureInfo.InvariantCulture) + "B";
            if (bytes < 1024 * 1024)
                return (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + "K";
            return (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + "M";
        }

        /// <summary>An object count at three significant figures, or a dash if the engine has none.</summary>
        private static string FormatCount(long count)
        {
            if (count == MemorySample.Unavailable)
                return "  —  ";
            if (count < 1000)
                return count.ToString(CultureInfo.InvariantCulture);
            if (count < 1000000)
                return (count / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + "k";
            return (count / 1000000.0).ToString("F1", CultureInfo.InvariantCulture) + "M";
        }

        private static string Pad(string text, int width)
            => (text + new string(' ', width)).Substring(0, width);

        /// <summary>One engine's memory figures for a row, found by name rather than by index.</summary>
        private static MemorySample MemoryOf(CsvRow row, IReadOnlyList<IBenchEngine> engines, string name)
        {
            for (int i = 0; i < engines.Count && i < row.EngineMeasurements.Length; i++)
            {
                if (string.Equals(engines[i].Name, name, StringComparison.Ordinal))
                    return row.EngineMeasurements[i].Memory;
            }

            return MemorySample.None;
        }

        private static void AppendMemory(StringBuilder line, MemorySample memory, bool objectsAndHeap)
        {
            line.Append(',').Append(Number(memory.AllocatedBytes));
            if (!objectsAndHeap)
                return;

            line.Append(',').Append(Number(memory.AllocatedObjects));
            line.Append(',').Append(Number(memory.LiveObjects));
            line.Append(',').Append(Number(memory.HeapBytes));
        }

        /// <summary>A counter as a CSV field, or empty where the engine does not expose it.</summary>
        private static string Number(long value)
            => value == MemorySample.Unavailable ? "" : value.ToString(CultureInfo.InvariantCulture);

        private readonly struct CsvRow
        {
            public readonly string Name;
            public readonly long Size;
            public readonly string Measures;
            public readonly Measurement[] EngineMeasurements;
            public readonly double BaselineMs;
            public readonly MemorySample BaselineMemory;
            public readonly bool Ok;

            public CsvRow(string name, long size, string measures, Measurement[] engineMeasurements, Measurement baseline, bool ok)
            {
                Name = name;
                Size = size;
                Measures = measures;
                EngineMeasurements = engineMeasurements;
                BaselineMs = baseline.Median;
                BaselineMemory = baseline.Memory;
                Ok = ok;
            }
        }
    }
}
