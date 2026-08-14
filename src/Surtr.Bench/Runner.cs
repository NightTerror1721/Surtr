#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Surtr.Bench
{
    /// <summary>A set of timed samples distilled to a median and the spread around it.</summary>
    internal readonly struct Measurement
    {
        public readonly double Median;
        public readonly double Min;
        public readonly double Max;

        public Measurement(double median, double min, double max)
        {
            Median = median;
            Min = min;
            Max = max;
        }
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

                    // One run per engine per case, checked against the C# reference. This also
                    // warms the code paths before the timed loop.
                    var results = new double[engines.Count];
                    for (int i = 0; i < engines.Count; i++)
                        results[i] = engines[i].Call(workload, size);
                    bool ok = Verified(workload, expected, results);
                    allOk = allOk && ok;

                    var engineMs = new Measurement[engines.Count];
                    for (int i = 0; i < engines.Count; i++)
                    {
                        IBenchEngine engine = engines[i];
                        engineMs[i] = Measure(() => engine.Call(workload, size), _options.Iterations, _options.Warmup, engine.Collect);
                    }
                    Measurement baselineMs = Measure(() => RunBaseline(workload, size), _options.Iterations, _options.Warmup);

                    PrintRow(workload, size, engineMs, baselineMs, ok, engines);
                    rows.Add(new CsvRow(workload.Name, size, engineMs, baselineMs, ok));
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

        private bool Matches(string name)
            => _options.WorkloadFilter == null || name.IndexOf(_options.WorkloadFilter, StringComparison.OrdinalIgnoreCase) >= 0;

        private long ScaledSize(long size)
            => Math.Max(1L, (long)(size * _options.Scale));

        private static double RunBaseline(Workload workload, long size)
            => workload.Kind == WorkloadKind.Int ? workload.BaselineInt!(size) : workload.BaselineFloat!(size);

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
        /// Times one operation. A warm-up run (when enabled) then <paramref name="iterations"/>
        /// timed runs; the median is the reportable number and the min/max give the spread. The
        /// CLR heap is settled before every sample so no engine inherits the previous one's debris,
        /// and <paramref name="settle"/> runs the engine's own collector the same way — but nothing
        /// in the timed region.
        /// </summary>
        private static Measurement Measure(Func<double> operation, int iterations, bool warmup, Action? settle = null)
        {
            if (warmup)
                operation();

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

            double min = samples[0];
            double max = samples[0];
            for (int i = 1; i < samples.Length; i++)
            {
                min = Math.Min(min, samples[i]);
                max = Math.Max(max, samples[i]);
            }

            Array.Sort(samples);
            return new Measurement(samples[iterations / 2], min, max);
        }

        private void PrintHeader(IReadOnlyList<IBenchEngine> engines)
        {
            Console.WriteLine("Surtr benchmark suite: median of {0} runs{1}, sizes fixed", _options.Iterations, _options.Warmup ? " after a warm-up run" : "");

            var names = new List<string>();
            foreach (IBenchEngine engine in engines)
                names.Add(engine.Name);
            names.Add("C# baseline");
            Console.WriteLine("Engines: {0}", string.Join(" vs ", names));

            Console.WriteLine("Surtr collects its heap between runs only, never inside the timed region.");
            Console.WriteLine();
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
                Pad(workload.Name, 13),
                Pad(size.ToString(CultureInfo.InvariantCulture), 10),
            };

            foreach (Measurement measurement in engineMs)
                cells.Add(Pad(FormatMs(measurement.Median), 12));
            cells.Add(Pad(FormatMs(baselineMs.Median), 12));

            // One ratio per engine after the first: how much slower that engine is than the
            // reference (Surtr, which is always first in the list).
            for (int i = 1; i < engines.Count; i++)
            {
                string ratio = engineMs[0].Median > 0 && engineMs[i].Median > 0
                    ? FormatRatio(engineMs[i].Median / engineMs[0].Median)
                    : "  —  ";
                cells.Add(Pad(ratio, 9));
            }

            string spread = engineMs.Length > 0 && engineMs[0].Median > 0
                ? FormatPercent((engineMs[0].Max - engineMs[0].Min) / engineMs[0].Median)
                : "  —  ";
            cells.Add(Pad(spread, 8));
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
                    if (row.EngineMeasurements.Length > 0 && row.EngineMeasurements[0].Median > 0)
                        line.Append(',').Append(FormatPercent((row.EngineMeasurements[0].Max - row.EngineMeasurements[0].Min) / row.EngineMeasurements[0].Median));
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

        private static string Pad(string text, int width)
            => (text + new string(' ', width)).Substring(0, width);

        private readonly struct CsvRow
        {
            public readonly string Name;
            public readonly long Size;
            public readonly Measurement[] EngineMeasurements;
            public readonly double BaselineMs;
            public readonly bool Ok;

            public CsvRow(string name, long size, Measurement[] engineMeasurements, Measurement baselineMs, bool ok)
            {
                Name = name;
                Size = size;
                EngineMeasurements = engineMeasurements;
                BaselineMs = baselineMs.Median;
                Ok = ok;
            }
        }
    }
}
