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
            LuaDriver? lua = null;
            try
            {
                if (!_options.LuaOnly && !_options.BaselineOnly)
                    surtr = SurtrDriver.Build(Workloads.ModuleSource);
                if (!_options.SurtrOnly && !_options.BaselineOnly)
                    lua = LuaDriver.Load(Workloads.LuaSource);

                PrintHeader(surtr != null, lua != null);

                var rows = new List<CsvRow>();
                bool allOk = true;

                foreach (var workload in Workloads.AllWorkloads)
                {
                    if (!Matches(workload.Name))
                        continue;

                    long size = ScaledSize(workload.Size);
                    double expected = RunBaseline(workload, size);

                    bool haveSurtr = surtr != null;
                    bool haveLua = lua != null;

                    // One run per engine per case, checked against the C# reference. This also
                    // warms the code paths before the timed loop.
                    double surtrResult = haveSurtr ? SurtrOnce(surtr!, workload, size) : 0;
                    double luaResult = haveLua ? lua!.CallNumber(workload.Name, size) : 0;
                    bool ok = Verified(workload, expected, surtrResult, luaResult, haveSurtr, haveLua);
                    allOk = allOk && ok;

                    Measurement surtrMs = haveSurtr
                        ? Measure(() => SurtrOnce(surtr!, workload, size), _options.Iterations, _options.Warmup, surtr!.Collect)
                        : default;
                    Measurement luaMs = haveLua
                        ? Measure(() => lua!.CallNumber(workload.Name, size), _options.Iterations, _options.Warmup)
                        : default;
                    Measurement baselineMs = Measure(() => RunBaseline(workload, size), _options.Iterations, _options.Warmup);

                    PrintRow(workload, size, surtrMs, luaMs, baselineMs, ok, haveSurtr, haveLua);
                    rows.Add(new CsvRow(workload.Name, size, surtrMs, luaMs, baselineMs, ok, haveSurtr, haveLua));
                }

                PrintSummary(rows);

                if (_options.CsvPath != null)
                    AppendCsv(_options.CsvPath, rows);

                return allOk ? 0 : 1;
            }
            finally
            {
                surtr?.Dispose();
            }
        }

        private bool Matches(string name)
            => _options.WorkloadFilter == null || name.IndexOf(_options.WorkloadFilter, StringComparison.OrdinalIgnoreCase) >= 0;

        private long ScaledSize(long size)
            => Math.Max(1L, (long)(size * _options.Scale));

        private static double SurtrOnce(SurtrDriver surtr, Workload workload, long size)
            => workload.Kind == WorkloadKind.Int ? surtr.CallInt(workload.Name, size) : surtr.CallFloat(workload.Name, size);

        private static double RunBaseline(Workload workload, long size)
            => workload.Kind == WorkloadKind.Int ? workload.BaselineInt!(size) : workload.BaselineFloat!(size);

        private static bool Verified(Workload workload, double expected, double surtrResult, double luaResult, bool haveSurtr, bool haveLua)
        {
            if (workload.Kind == WorkloadKind.Int)
            {
                return (!haveSurtr || Math.Abs(surtrResult - expected) < 0.5)
                    && (!haveLua || Math.Abs(luaResult - expected) < 0.5);
            }

            double tolerance = Math.Max(1.0, Math.Abs(expected)) * 1e-9;
            return (!haveSurtr || Math.Abs(surtrResult - expected) <= tolerance)
                && (!haveLua || Math.Abs(luaResult - expected) <= tolerance);
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

        private void PrintHeader(bool haveSurtr, bool haveLua)
        {
            Console.WriteLine("Surtr benchmark suite: median of {0} runs{1}, sizes fixed", _options.Iterations, _options.Warmup ? " after a warm-up run" : "");
            string engines = haveSurtr && haveLua
                ? "Surtr vs MoonSharp 2.0.0"
                : haveSurtr ? "Surtr" : haveLua ? "MoonSharp 2.0.0" : "C# baseline";
            Console.WriteLine("Engines: {0}", engines);
            Console.WriteLine("Surtr collects its heap between runs only, never inside the timed region.");
            Console.WriteLine();
        }

        private static void PrintRow(
            Workload workload,
            long size,
            Measurement surtrMs,
            Measurement luaMs,
            Measurement baselineMs,
            bool ok,
            bool haveSurtr,
            bool haveLua)
        {
            string surtrText = haveSurtr ? FormatMs(surtrMs.Median) : "  —  ";
            string luaText = haveLua ? FormatMs(luaMs.Median) : "  —  ";
            string baselineText = FormatMs(baselineMs.Median);

            string ratioText;
            string inverseText;
            string spreadText;
            if (haveSurtr && haveLua && luaMs.Median > 0 && surtrMs.Median > 0)
            {
                double ratio = luaMs.Median / surtrMs.Median;
                ratioText = FormatRatio(ratio);
                inverseText = FormatRatio(1.0 / ratio);
                spreadText = FormatPercent((surtrMs.Max - surtrMs.Min) / surtrMs.Median);
            }
            else
            {
                ratioText = "  —  ";
                inverseText = "  —  ";
                spreadText = haveSurtr ? FormatPercent((surtrMs.Max - surtrMs.Min) / surtrMs.Median) : "  —  ";
            }

            Console.WriteLine(
                string.Join("  ",
                    Pad(workload.Name, 13),
                    Pad(size.ToString(CultureInfo.InvariantCulture), 10),
                    Pad(surtrText, 12),
                    Pad(luaText, 12),
                    Pad(baselineText, 12),
                    Pad(ratioText, 9),
                    Pad(inverseText, 9),
                    Pad(spreadText, 8),
                    ok ? "ok" : "FAIL"));
        }

        private void PrintSummary(List<CsvRow> rows)
        {
            double logSum = 0;
            int count = 0;
            foreach (var row in rows)
            {
                if (row.HaveSurtr && row.HaveLua && row.SurtrMs > 0 && row.LuaMs > 0)
                {
                    logSum += Math.Log(row.LuaMs / row.SurtrMs);
                    count++;
                }
            }

            if (count > 0)
            {
                double geometricMean = Math.Exp(logSum / count);
                Console.WriteLine();
                Console.WriteLine("geometric mean speed-up (Surtr over MoonSharp, {0} cases): {1}x", count, geometricMean.ToString("F1", CultureInfo.InvariantCulture));
            }
        }

        private static void AppendCsv(string path, List<CsvRow> rows)
        {
            bool appendHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            var line = new StringBuilder();
            using (var writer = new StreamWriter(path, append: true))
            {
                if (appendHeader)
                    writer.WriteLine("workload,size,surtr_ms,lua_ms,csharp_ms,surtr_over_lua,lua_over_surtr,spread_pct,ok");

                foreach (var row in rows)
                {
                    line.Clear();
                    line.Append(row.Name);
                    line.Append(',').Append(row.Size.ToString(CultureInfo.InvariantCulture));
                    line.Append(',').Append(row.HaveSurtr ? row.SurtrMs.ToString("F3", CultureInfo.InvariantCulture) : "");
                    line.Append(',').Append(row.HaveLua ? row.LuaMs.ToString("F3", CultureInfo.InvariantCulture) : "");
                    line.Append(',').Append(row.BaselineMs.ToString("F3", CultureInfo.InvariantCulture));
                    if (row.HaveSurtr && row.HaveLua && row.LuaMs > 0 && row.SurtrMs > 0)
                    {
                        line.Append(',').Append((row.LuaMs / row.SurtrMs).ToString("F3", CultureInfo.InvariantCulture));
                        line.Append(',').Append((row.SurtrMs / row.LuaMs).ToString("F3", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        line.Append(",,");
                    }
                    line.Append(',').Append(row.HaveSurtr ? FormatPercent((row.MaxMs - row.MinMs) / row.SurtrMs) : "");
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
            public readonly double SurtrMs;
            public readonly double MinMs;
            public readonly double MaxMs;
            public readonly double LuaMs;
            public readonly double BaselineMs;
            public readonly bool Ok;
            public readonly bool HaveSurtr;
            public readonly bool HaveLua;

            public CsvRow(string name, long size, Measurement surtrMs, Measurement luaMs, Measurement baselineMs, bool ok, bool haveSurtr, bool haveLua)
            {
                Name = name;
                Size = size;
                SurtrMs = haveSurtr ? surtrMs.Median : 0;
                MinMs = haveSurtr ? surtrMs.Min : 0;
                MaxMs = haveSurtr ? surtrMs.Max : 0;
                LuaMs = haveLua ? luaMs.Median : 0;
                BaselineMs = baselineMs.Median;
                Ok = ok;
                HaveSurtr = haveSurtr;
                HaveLua = haveLua;
            }
        }
    }
}
