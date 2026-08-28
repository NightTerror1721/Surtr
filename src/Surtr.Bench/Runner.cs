#nullable enable

using Surtr.Runtime.Objects;
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

        /// <summary>
        /// The 90th and 99th percentiles. The median says what a run typically costs; these say what
        /// a run can occasionally cost, which is the number a frame budget is set from.
        /// </summary>
        public readonly double P90;
        public readonly double P99;

        /// <summary>What one run cost in memory, measured outside the timed samples.</summary>
        public readonly MemorySample Memory;

        public Measurement(
            double median,
            double min,
            double max,
            double lowerQuartile,
            double upperQuartile,
            double p90,
            double p99,
            MemorySample memory)
        {
            Median = median;
            Min = min;
            Max = max;
            LowerQuartile = lowerQuartile;
            UpperQuartile = upperQuartile;
            P90 = p90;
            P99 = p99;
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

            if (_options.VerifyOnly || _options.Smoke)
                return VerifyRun();

            if (_options.Processes > 1)
                return RunMultiProcess();

            SurtrDriver? surtr = null;
            SurtrDriver? surtrAuto = null;
            LuaDriver? moon = null;
            NativeLuaDriver? luajit = null;
            try
            {
                var engines = new List<IBenchEngine>();
                BuildSurtrEngines(ref surtr, ref surtrAuto);
                if (surtr != null) engines.Add(surtr);
                if (surtrAuto != null) engines.Add(surtrAuto);
                if (_options.RunMoonSharp)
                {
                    moon = LuaDriver.Load(Workloads.LuaSource);
                    engines.Add(moon);
                }
                if (_options.RunLuaJit)
                {
                    luajit = NativeLuaDriver.Load(Workloads.LuaSource);
                    engines.Add(luajit);
                }

                PrintHeader(engines);

                var accumulated = new Dictionary<string, Accumulator>();
                bool allOk = true;

                // One whole-catalogue pass per round. The point of a round is that it is whole:
                // the order the cases run in changes what a later case pays for (generic
                // instantiations warm up once and stay warm, and which ones that is depends on what
                // ran first — the csproj's note on dictMembers documents a 1.27x swing from order
                // alone). Shuffling the order per round and reporting the median across rounds is
                // the only defence that does not depend on which cases happen to be neighbours.
                for (int round = 0; round < _options.Rounds; round++)
                {
                    foreach (var workload in OrderOfRound(round))
                    {
                        if (!Matches(workload.Name))
                            continue;

                        if (!accumulated.TryGetValue(workload.Name, out var accumulator))
                        {
                            accumulator = new Accumulator();
                            accumulated[workload.Name] = accumulator;
                        }

                        long size = ScaledSize(workload.Size);
                        accumulator.Size = size;

                        // One run per engine per case, checked against the C# reference. Three
                        // implementations of the same algorithm agreeing on a checksum is the only
                        // thing standing between a fast engine and one that is fast because it is not
                        // doing the work — see the note on Workloads. Runs once, on the first round.
                        if (round == 0)
                        {
                            double expected = RunBaseline(workload, size);
                            var results = new double[engines.Count];
                            for (int i = 0; i < engines.Count; i++)
                                results[i] = engines[i].Call(workload, size);
                            bool ok = Verified(workload, expected, results);
                            allOk = allOk && ok;
                            if (!ok)
                                ReportDisagreement(workload, expected, results, engines);
                            accumulator.Ok = ok;
                        }

                        var engineMs = new Measurement[engines.Count];
                        var engineExtreme = new bool[engines.Count];
                        for (int i = 0; i < engines.Count; i++)
                        {
                            IBenchEngine engine = engines[i];

                            // The MoonSharp circuit breaker: Surtr always measures first (index 0),
                            // so by the time the loop reaches MoonSharp there is already a real
                            // reference to gauge against. One untimed-warmup probe call answers
                            // whether the full warmup+iterations run is worth paying for at all -
                            // arrayFill alone measured MoonSharp at ~7000x Surtr, and a case that
                            // extreme is what turned a 40-case suite into a multi-hour run.
                            if (moon != null && ReferenceEquals(engine, moon) && i > 0 && engineMs[0].Median > 0)
                            {
                                double referenceMs = engineMs[0].Median;
                                var probe = Stopwatch.StartNew();
                                engine.Call(workload, size);
                                probe.Stop();
                                double probeMs = probe.Elapsed.TotalMilliseconds;

                                if (probeMs >= referenceMs * RunnerOptions.MoonSharpExtremeRatio)
                                {
                                    engineMs[i] = new Measurement(
                                        probeMs, probeMs, probeMs, probeMs, probeMs, probeMs, probeMs,
                                        engine.SampleMemory());
                                    engineExtreme[i] = true;
                                    continue;
                                }
                            }

                            engineMs[i] = Measure(
                                () => engine.Call(workload, size),
                                _options.Iterations,
                                _options.WarmupIterations,
                                engine.Collect,
                                engine.SampleMemory,
                                _options.MemoryRuns,
                                _options.GcInclusive);
                        }
                        Measurement baselineMs = Measure(
                            () => RunBaseline(workload, size),
                            _options.Iterations,
                            _options.WarmupIterations,
                            memoryRuns: _options.MemoryRuns,
                            gcInclusive: _options.GcInclusive);

                        accumulator.Add(engineMs, baselineMs, engineExtreme);
                    }
                }

                var rows = new List<CsvRow>();
                bool strictViolation = false;
                int spreadWarnings = 0;

                // Printed in catalogue order regardless of the order rounds ran in, so a table you
                // compare against an earlier run lines up row for row even when both were shuffled.
                foreach (var workload in Workloads.AllWorkloads)
                {
                    if (!Matches(workload.Name) || !accumulated.TryGetValue(workload.Name, out var accumulator))
                        continue;

                    (Measurement[] engineMs, Measurement baselineMs) = accumulator.Reduce();

                    bool spreadWarn = engineMs.Length > 0
                        && engineMs[0].Spread > RunnerOptions.SpreadWarningThreshold;
                    if (spreadWarn)
                        spreadWarnings++;
                    if (_options.Strict && (!accumulator.Ok || spreadWarn))
                        strictViolation = true;

                    PrintRow(workload, accumulator.Size, engineMs, baselineMs, accumulator.Ok, spreadWarn, engines, _options.Percentiles, accumulator.Extreme);
                    rows.Add(new CsvRow(workload.Name, accumulator.Size, workload.Measures, engineMs, baselineMs, accumulator.Ok, workload.DiagnosticOnly, accumulator.Extreme));
                }

                PrintSummary(rows, engines);

                if (spreadWarnings > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine(
                        "{0} row(s) marked ok!: reference spread above {1:P0}, so the median is not yet worth quoting.",
                        spreadWarnings,
                        RunnerOptions.SpreadWarningThreshold);
                }

                if (_options.Strict && strictViolation)
                {
                    Console.WriteLine();
                    Console.WriteLine("--strict: at least one row failed verification or its reference spread exceeded {0:P0}.", RunnerOptions.SpreadWarningThreshold);
                }

                if (_options.CsvPath != null)
                    AppendCsv(_options.CsvPath, rows, engines);

                return allOk && !strictViolation ? 0 : 1;
            }
            finally
            {
                surtr?.Dispose();
                surtrAuto?.Dispose();
                luajit?.Dispose();
            }
        }

        /// <summary>
        /// Builds the Surtr engine(s) for the configured GC mode. In <c>both</c> mode the manual
        /// engine keeps the plain name (it is the reference the ratios are relative to) and the
        /// automatic one is named <c>surtr-auto</c>, so the two policies are compared like any two
        /// engines. The default build is automatic — that is the runtime's own default.
        /// </summary>
        private void BuildSurtrEngines(ref SurtrDriver? manual, ref SurtrDriver? automatic)
        {
            if (!_options.RunSurtr)
                return;

            long budget = _options.Smoke ? SurtrDriver.SmokeInstructionBudget : SurtrDriver.DefaultInstructionBudget;

            switch (_options.SurtrGc)
            {
                case SurtrGcBenchMode.Manual:
                    manual = SurtrDriver.Build(Workloads.ModuleSource, budget, SurtrGcPolicy.Manual, "surtr");
                    break;
                case SurtrGcBenchMode.Automatic:
                    manual = SurtrDriver.Build(Workloads.ModuleSource, budget, SurtrGcPolicy.Automatic, "surtr");
                    break;
                default:
                    manual = SurtrDriver.Build(Workloads.ModuleSource, budget, SurtrGcPolicy.Manual, "surtr");
                    automatic = SurtrDriver.Build(Workloads.ModuleSource, budget, SurtrGcPolicy.Automatic, "surtr-auto");
                    break;
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

        /// <summary>
        /// The fast, timing-free pass <c>--verify-only</c> and <c>--smoke</c> share. One run of
        /// every selected case per engine, checked against the C# baseline; the exit code is the
        /// verdict. This is the run a CI job calls: it catches a workload whose three implementations
        /// have drifted apart, or a crash, in seconds, without a single timing being involved.
        /// </summary>
        private int VerifyRun()
        {
            SurtrDriver? surtr = null;
            SurtrDriver? surtrAuto = null;
            LuaDriver? moon = null;
            NativeLuaDriver? luajit = null;
            try
            {
                var engines = new List<IBenchEngine>();
                BuildSurtrEngines(ref surtr, ref surtrAuto);
                if (surtr != null) engines.Add(surtr);
                if (surtrAuto != null) engines.Add(surtrAuto);
                if (_options.RunMoonSharp)
                {
                    moon = LuaDriver.Load(Workloads.LuaSource);
                    engines.Add(moon);
                }
                if (_options.RunLuaJit)
                {
                    luajit = NativeLuaDriver.Load(Workloads.LuaSource);
                    engines.Add(luajit);
                }

                Console.WriteLine(_options.Smoke
                    ? "Smoke pass — every case at a hundredth of its size, a tight instruction budget, no timing."
                    : "Verification pass — one run per case at its full size, no timing.");
                var names = new List<string>();
                foreach (IBenchEngine engine in engines)
                    names.Add(engine.Name);
                names.Add("C# baseline");
                Console.WriteLine("Engines: {0}", string.Join(" vs ", names));
                Console.WriteLine();

                var header = new List<string> { Pad("workload", 15), Pad("size", 9) };
                foreach (IBenchEngine engine in engines)
                    header.Add(Pad(engine.Name, 14));
                header.Add(Pad("c#", 14));
                header.Add("result");
                Console.WriteLine(string.Join("  ", header));

                bool allOk = true;
                int shown = 0;

                foreach (var workload in Workloads.AllWorkloads)
                {
                    if (!Matches(workload.Name))
                        continue;

                    long size = _options.Smoke
                        ? ScaledSize(Math.Max(1L, workload.Size / 100))
                        : ScaledSize(workload.Size);

                    double expected = RunBaseline(workload, size);
                    var results = new double[engines.Count];
                    for (int i = 0; i < engines.Count; i++)
                        results[i] = engines[i].Call(workload, size);
                    bool ok = Verified(workload, expected, results);
                    allOk = allOk && ok;
                    shown++;

                    var row = new List<string>
                    {
                        Pad(workload.Name, 15),
                        Pad(size.ToString(CultureInfo.InvariantCulture), 9),
                    };
                    foreach (double result in results)
                        row.Add(Pad(FormatChecksum(result), 14));
                    row.Add(Pad(FormatChecksum(expected), 14));
                    row.Add(ok ? "ok" : "FAIL");
                    Console.WriteLine(string.Join("  ", row));

                    if (!ok)
                        ReportDisagreement(workload, expected, results, engines);
                }

                Console.WriteLine();
                Console.WriteLine(allOk
                    ? "All {0} cases agree with the C# baseline."
                    : "{0} cases run; at least one disagrees.", shown);
                return allOk ? 0 : 1;
            }
            finally
            {
                surtr?.Dispose();
                surtrAuto?.Dispose();
                luajit?.Dispose();
            }
        }

        /// <summary>
        /// The <c>--processes</c> mode: every selected case runs in <paramref name="processes"/>
        /// fresh processes (this executable, restricted to that one case) and the reported number
        /// per engine is the fastest sample — the op-cache-friendly state, which is the
        /// interpreter's true throughput. A single process samples one of the two bimodal states
        /// the interpreter's dispatch loop flips between, and a median drawn from one process is
        /// that state's, not the interpreter's (docs/Informe-Volatilidad-Run.md §4). The state
        /// spread — how far above the fastest the slowest sampled state was — is reported per case
        /// so a bimodal case is not mistaken for a stable one.
        /// </summary>
        private int RunMultiProcess()
        {
            // The child CSV's engine columns come in this order (BuildSurtrEngines, then MoonSharp,
            // then LuaJIT), and each engine column is followed by its ms figures.
            var engineNames = new List<string>();
            if (_options.RunSurtr)
            {
                engineNames.Add("surtr");
                if (_options.SurtrGc == SurtrGcBenchMode.Both)
                    engineNames.Add("surtr-auto");
            }
            if (_options.RunMoonSharp)
                engineNames.Add("lua");
            if (_options.RunLuaJit)
                engineNames.Add("luajit");
            if (engineNames.Count == 0)
                throw new InvalidOperationException("--processes needs an engine to time; --baseline-only has none.");

            Console.WriteLine();
            Console.WriteLine(
                "Fresh-process measurement: {0} process(es) per case; the ms reported is the fastest\n" +
                "(the op-cache-friendly state), and the state spread is how far the slowest state was.\n" +
                "A single-state result means all N processes landed in the same state — the fast state\n" +
                "is not always reachable: its probability depends on the machine's code-layout state\n" +
                "and can drop to zero for hours (docs/Informe-Volatilidad-Run.md). Use >= 7 processes.", _options.Processes);
            Console.WriteLine();

            var header = new List<string> { Pad("workload", 15), Pad("size", 9) };
            foreach (string engine in engineNames)
                header.Add(Pad(engine + " ms", 11));
            header.Add(Pad("c# ms", 11));
            for (int i = 1; i < engineNames.Count; i++)
                header.Add(Pad("vs " + engineNames[i], 10));
            header.Add(Pad("vs c#", 8));
            header.Add(Pad("bytes", 9));
            header.Add(Pad("objs", 8));
            header.Add(Pad("kept", 8));
            header.Add(Pad("c#B", 9));
            header.Add(Pad("state", 8));
            header.Add("result");
            Console.WriteLine(string.Join("  ", header));

            var rows = new List<(CsvRow Row, double StateSpread)>();
            bool allOk = true;

            foreach (var workload in Workloads.AllWorkloads)
            {
                if (!Matches(workload.Name))
                    continue;

                long size = ScaledSize(workload.Size);

                // The op-cache state is a property of the process (the JIT code's absolute
                // addresses, re-rolled by ASLR per launch), so sampling more than one state means
                // launching more than one process. Run them sequentially: parallel children would
                // share the cores and blur the very timing being measured.
                var samples = new List<ChildSample>();
                bool ok = true;
                for (int i = 0; i < _options.Processes; i++)
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "surtrbench-" + Guid.NewGuid().ToString("N") + ".csv");
                    ChildSample? sample = RunChildProcess(workload.Name, tmp, engineNames);
                    if (sample == null)
                    {
                        ok = false;
                        break;
                    }
                    samples.Add(sample.Value);
                }

                if (samples.Count == 0)
                {
                    Console.WriteLine(Pad(workload.Name, 15) + Pad("FAIL", 9));
                    allOk = false;
                    continue;
                }

                var engineMin = new double[engineNames.Count];
                var engineMax = new double[engineNames.Count];
                for (int e = 0; e < engineNames.Count; e++)
                {
                    double min = double.MaxValue, max = double.MinValue;
                    foreach (ChildSample sample in samples)
                    {
                        if (sample.EngineMs[e] < min) min = sample.EngineMs[e];
                        if (sample.EngineMs[e] > max) max = sample.EngineMs[e];
                    }
                    engineMin[e] = min;
                    engineMax[e] = max;
                }

                // Extreme if the breaker tripped in any of the N child processes - the label is
                // meant to warn a reader off the number, so one process catching it is enough.
                var extreme = new bool[engineNames.Count];
                foreach (ChildSample sample in samples)
                {
                    for (int e = 0; e < engineNames.Count && e < sample.Extreme.Length; e++)
                        extreme[e] |= sample.Extreme[e];
                }

                double baselineMin = double.MaxValue;
                foreach (ChildSample sample in samples)
                    if (sample.CsharpMs < baselineMin)
                        baselineMin = sample.CsharpMs;

                // The memory of the fastest child: memory is not bimodal (it does not depend on
                // the op cache), so the representative run's figures are the workload's.
                ChildSample fastest = samples[0];
                for (int i = 1; i < samples.Count; i++)
                    if (samples[i].EngineMs[0] < fastest.EngineMs[0])
                        fastest = samples[i];

                // How far the unlucky state was above the fast one, on the reference engine.
                double stateSpread = engineMin[0] > 0 ? (engineMax[0] - engineMin[0]) / engineMin[0] : 0;
                bool bimodal = stateSpread > 0.20;

                // A synthetic Measurement whose "median" is the fast state, so the ratios and the
                // summary behave; the quartiles are pinned to the min so the within-process Spread
                // is zero and the state spread is the number that describes the case.
                var engineMs = new Measurement[engineNames.Count];
                for (int e = 0; e < engineNames.Count; e++)
                    engineMs[e] = new Measurement(engineMin[e], engineMin[e], engineMax[e], engineMin[e], engineMin[e], engineMax[e], engineMax[e], e == 0 ? fastest.SurtrMemory : MemorySample.None);
                var baseline = new Measurement(baselineMin, baselineMin, baselineMin, baselineMin, baselineMin, baselineMin, baselineMin, fastest.CsharpMemory);

                var cells = new List<string>
                {
                    Pad(workload.Name, 15),
                    Pad(size.ToString(CultureInfo.InvariantCulture), 9),
                };
                for (int e = 0; e < engineMs.Length; e++)
                {
                    string cell = extreme[e] ? FormatMs(engineMs[e].Median) + "!!" : FormatMs(engineMs[e].Median);
                    cells.Add(Pad(cell, 11));
                }
                cells.Add(Pad(FormatMs(baseline.Median), 11));
                for (int i = 1; i < engineNames.Count; i++)
                {
                    string ratio;
                    if (extreme[i])
                    {
                        ratio = engineMs[0].Median > 0 ? ">=" + FormatRatio(RunnerOptions.MoonSharpExtremeRatio) : "  —  ";
                    }
                    else
                    {
                        ratio = engineMs[0].Median > 0 && engineMs[i].Median > 0
                            ? FormatRatio(engineMs[i].Median / engineMs[0].Median)
                            : "  —  ";
                    }
                    cells.Add(Pad(ratio, 10));
                }
                string overBaseline = engineMs[0].Median > 0 && baseline.Median > 0
                    ? FormatRatio(engineMs[0].Median / baseline.Median)
                    : "  —  ";
                cells.Add(Pad(overBaseline, 8));
                cells.Add(Pad(FormatBytes(engineMs[0].Memory.AllocatedBytes), 9));
                cells.Add(Pad(FormatCount(engineMs[0].Memory.AllocatedObjects), 8));
                cells.Add(Pad(FormatCount(engineMs[0].Memory.LiveObjects), 8));
                cells.Add(Pad(FormatBytes(baseline.Memory.AllocatedBytes), 9));
                cells.Add(Pad(FormatPercent(stateSpread), 8));
                cells.Add(Pad(bimodal ? "bimodal" : "single", 8));
                bool anyExtreme = Array.Exists(extreme, e => e);
                cells.Add((ok ? "ok" : "FAIL")
                    + (workload.DiagnosticOnly ? " (diag)" : "")
                    + (anyExtreme ? " EXTREMO-LENTO" : ""));
                Console.WriteLine(string.Join("  ", cells));

                rows.Add((new CsvRow(workload.Name, size, workload.Measures, engineMs, baseline, ok, workload.DiagnosticOnly, extreme), stateSpread));
                allOk = allOk && ok;
            }

            var csvRows = new List<CsvRow>();
            foreach ((CsvRow row, double _) in rows)
                csvRows.Add(row);
            PrintSummaryMulti(csvRows, engineNames);

            if (_options.CsvPath != null)
                AppendMultiCsv(_options.CsvPath, rows, engineNames, _options.Processes);

            return allOk ? 0 : 1;
        }

        /// <summary>
        /// Runs this same executable restricted to one workload, waits for it, and returns the
        /// engine times (and the C# baseline) it reported, plus the memory figures — one fresh
        /// process, one sample of the op-cache state. Null means the child failed its verification
        /// or crashed.
        /// </summary>
        private ChildSample? RunChildProcess(string workloadName, string csvPath, IReadOnlyList<string> engineNames)
        {
            // Rebuild the parent's command for the child: same options, minus --processes (so it
            // does not recurse), minus the parent's --workload filters and --csv (each child writes
            // its own fresh CSV for exactly this workload).
            var childArgs = new List<string>();
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--processes" || args[i] == "--csv" || args[i] == "--workload")
                {
                    i++; // skip the option's value
                    continue;
                }
                childArgs.Add(args[i]);
            }
            childArgs.Add("--workload");
            childArgs.Add(workloadName);
            childArgs.Add("--csv");
            childArgs.Add(csvPath);

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath!,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                };
                foreach (string argument in childArgs)
                    startInfo.ArgumentList.Add(argument);

                using Process process = Process.Start(startInfo)!;
                // Drain both streams concurrently so a chatty child cannot deadlock the parent.
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
                {
                    process.Kill();
                    return null;
                }
                if (process.ExitCode != 0)
                    return null;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("child process for '{0}' failed: {1}", workloadName, exception.Message);
                return null;
            }

            try
            {
                // The child's CSV: "#"-prefixed comment lines, then a header row, then one data row
                // per case. The data rows are real CSV (the measures column is quoted and can hold
                // a comma), so fields are split with quotes honoured, and columns are found by name
                // against the header rather than by position.
                string? header = null;
                string? row = null;
                foreach (string line in File.ReadLines(csvPath))
                {
                    if (line.Length == 0 || line[0] == '#')
                        continue;
                    if (header == null)
                    {
                        header = line;
                        continue;
                    }
                    if (row == null && StartsWithField(line, workloadName))
                        row = line;
                    if (row != null && header != null)
                        break;
                }
                if (header == null || row == null)
                    return null;

                List<string> headerFields = SplitCsvLine(header);
                List<string> dataFields = SplitCsvLine(row);
                if (headerFields.Count != dataFields.Count)
                    return null;

                var column = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < headerFields.Count; i++)
                    column[headerFields[i]] = i;

                var engineMs = new double[engineNames.Count];
                for (int e = 0; e < engineNames.Count; e++)
                {
                    if (!column.TryGetValue(engineNames[e] + "_ms", out int index)
                        || !double.TryParse(dataFields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out engineMs[e]))
                        return null;
                }
                if (!column.TryGetValue("csharp_ms", out int csharpIndex)
                    || !double.TryParse(dataFields[csharpIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out double csharpMs))
                    return null;

                var extreme = new bool[engineNames.Count];
                if (column.TryGetValue("extreme_engines", out int extremeIndex))
                {
                    var extremeNames = new HashSet<string>(
                        dataFields[extremeIndex].Split(';', StringSplitOptions.RemoveEmptyEntries),
                        StringComparer.Ordinal);
                    for (int e = 0; e < engineNames.Count; e++)
                        extreme[e] = extremeNames.Contains(engineNames[e]);
                }

                return new ChildSample(
                    engineMs,
                    csharpMs,
                    ReadMemory(column, dataFields, "surtr_alloc_bytes", "surtr_alloc_objects", "surtr_kept_objects", "surtr_heap_bytes"),
                    ReadMemory(column, dataFields, "csharp_alloc_bytes", null, null, null),
                    extreme);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                try { File.Delete(csvPath); } catch (IOException) { }
            }
        }

        /// <summary>Whether a CSV data row begins with this workload's name.</summary>
        private static bool StartsWithField(string line, string field)
        {
            int comma = line.IndexOf(',');
            return comma > 0 && line.AsSpan(0, comma).SequenceEqual(field);
        }

        /// <summary>
        /// Splits a CSV line honouring double-quoted fields and the "" escape, because the measures
        /// column is quoted and several of its values contain a comma.
        /// </summary>
        private static List<string> SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            fields.Add(current.ToString());
            return fields;
        }

        /// <summary>One memory sample read by column name from the child's CSV; absent columns read as unavailable.</summary>
        private static MemorySample ReadMemory(
            IReadOnlyDictionary<string, int> column,
            IReadOnlyList<string> data,
            string bytesColumn,
            string? objectsColumn,
            string? keptColumn,
            string? heapColumn)
        {
            long Field(string name)
                => name != null && column.TryGetValue(name, out int index)
                    && long.TryParse(data[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                    ? value
                    : MemorySample.Unavailable;

            return new MemorySample(
                Field(bytesColumn),
                Field(objectsColumn!),
                Field(keptColumn!),
                Field(heapColumn!));
        }

        /// <summary>One fresh-process sample: the per-engine times, the C# baseline, and the memory figures.</summary>
        private readonly struct ChildSample
        {
            public readonly double[] EngineMs;
            public readonly double CsharpMs;
            public readonly MemorySample SurtrMemory;
            public readonly MemorySample CsharpMemory;

            /// <summary>Which engine indices the MoonSharp circuit breaker capped inside the child. Same length and order as <see cref="EngineMs"/>.</summary>
            public readonly bool[] Extreme;

            public ChildSample(double[] engineMs, double csharpMs, MemorySample surtrMemory, MemorySample csharpMemory, bool[] extreme)
            {
                EngineMs = engineMs;
                CsharpMs = csharpMs;
                SurtrMemory = surtrMemory;
                CsharpMemory = csharpMemory;
                Extreme = extreme;
            }
        }

        /// <summary>The geometric-mean speed-ups for the multi-process mode, over the fast states.</summary>
        private static void PrintSummaryMulti(IReadOnlyList<CsvRow> rows, IReadOnlyList<string> engineNames)
        {
            bool wrote = false;
            for (int i = 1; i < engineNames.Count; i++)
            {
                double logSum = 0;
                int count = 0;
                foreach (CsvRow row in rows)
                {
                    if (row.DiagnosticOnly)
                        continue;
                    if (row.IsExtreme(i))
                        continue;

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
                    Console.WriteLine(
                        "geometric mean speed-up ({0} over {1}, {2} cases): {3}x",
                        engineNames[0],
                        engineNames[i],
                        count,
                        Math.Exp(logSum / count).ToString("F1", CultureInfo.InvariantCulture));
                }
            }
        }

        /// <summary>
        /// The multi-process CSV: the same <c>*_ms</c> column names a single-process run writes,
        /// but holding the fast state (the min across processes) rather than a within-process
        /// median, plus the state spread so nobody mistakes a bimodal case for a stable one. The
        /// settings line carries <c>processes=N</c>, which is what tells a reader which of the two
        /// meanings a column has.
        /// </summary>
        private void AppendMultiCsv(string path, IReadOnlyList<(CsvRow Row, double StateSpread)> rows, IReadOnlyList<string> engineNames, int processes)
        {
            bool appendHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            using var writer = new StreamWriter(path, append: true);
            if (appendHeader)
            {
                writer.WriteLine("# machine: " + SystemInfo.FingerprintLine());
                writer.WriteLine("# settings: iters={0} warmup={1} rounds={2} shuffle={3} seed={4} gc-inclusive={5} memory-runs={6} surtr-gc={7} processes={8}",
                    _options.Iterations, _options.WarmupIterations, _options.Rounds, _options.Shuffle, _options.ShuffleSeed, _options.GcInclusive, _options.MemoryRuns, _options.SurtrGc.ToString().ToLowerInvariant(), processes);
                writer.WriteLine("# processes=N: every *_ms column is the fastest of N fresh processes (the op-cache-friendly state), not a within-process median; state_spread_pct is how far the slowest state was; memory columns are the fastest process's.");
                var line = new StringBuilder("workload,size");
                foreach (string engine in engineNames)
                    line.Append(',').Append(engine).Append("_ms");
                line.Append(",csharp_ms");
                line.Append(",surtr_alloc_bytes,surtr_alloc_objects,surtr_kept_objects,surtr_heap_bytes");
                line.Append(",csharp_alloc_bytes");
                line.Append(",state_spread_pct,processes,ok,diagnostic_only,extreme_engines");
                writer.WriteLine(line);
            }

            foreach ((CsvRow row, double stateSpread) in rows)
            {
                var line = new StringBuilder();
                line.Append(row.Name);
                line.Append(',').Append(row.Size.ToString(CultureInfo.InvariantCulture));
                foreach (Measurement measurement in row.EngineMeasurements)
                    line.Append(',').Append(measurement.Median.ToString("F3", CultureInfo.InvariantCulture));
                line.Append(',').Append(row.Baseline.Median.ToString("F3", CultureInfo.InvariantCulture));
                if (row.EngineMeasurements.Length > 0)
                {
                    MemorySample memory = row.EngineMeasurements[0].Memory;
                    line.Append(',').Append(Number(memory.AllocatedBytes));
                    line.Append(',').Append(Number(memory.AllocatedObjects));
                    line.Append(',').Append(Number(memory.LiveObjects));
                    line.Append(',').Append(Number(memory.HeapBytes));
                }
                else
                {
                    line.Append(",,,,");
                }
                line.Append(',').Append(Number(row.Baseline.Memory.AllocatedBytes));
                line.Append(',').Append((stateSpread * 100.0).ToString("F1", CultureInfo.InvariantCulture));
                line.Append(',').Append(processes.ToString(CultureInfo.InvariantCulture));
                line.Append(',').Append(row.Ok ? "ok" : "FAIL");
                line.Append(',').Append(row.DiagnosticOnly ? "1" : "0");
                line.Append(',').Append(ExtremeEnginesField(row.Extreme, engineNames));
                writer.WriteLine(line);
            }
        }

        /// <summary>
        /// The catalogue in the order one round runs it: declaration order, or a seeded shuffle so
        /// no case is always warmed by the same neighbours. The seed and the round number make the
        /// order reproducible — the same seed gives the same run every time, which is what makes
        /// two runs comparable at all.
        /// </summary>
        private IReadOnlyList<Workload> OrderOfRound(int round)
        {
            if (!_options.Shuffle)
                return Workloads.AllWorkloads;

            var list = new List<Workload>(Workloads.AllWorkloads);
            var random = new Random(_options.ShuffleSeed + round * 7919);
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
            return list;
        }

        private bool Matches(string name)
        {
            if (_options.WorkloadFilters.Count == 0)
                return true;

            foreach (string filter in _options.WorkloadFilters)
            {
                if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The size a workload actually runs at. Clamped to an int: every engine's entry point takes
        /// a 32-bit size, and an unscaled run never gets near the limit — but <c>--scale</c> is
        /// arbitrary user input, and a scale that pushed a size past 2^31 used to wrap to a negative
        /// n silently, which is the kind of number a "reliable" benchmark must never produce.
        /// </summary>
        private long ScaledSize(long size)
            => Math.Min(int.MaxValue, Math.Max(1L, (long)(size * _options.Scale)));

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
        /// <para>
        /// The warm-up is a real phase rather than the single run it used to be, and it is what a
        /// median is worth anything on. It has two jobs: drive the JIT past first-call compilation
        /// on every path the workload touches (the csproj forces loop-bearing methods straight to
        /// optimized code, but a method without a loop still tiers up on call count), and let the
        /// heap reach the size the workload actually wants so the first timed sample is not paying
        /// for growth every later one inherits.
        /// </para>
        /// <para>
        /// The default settles before each sample and never inside it, so the timed region is the
        /// work alone. That is the right answer when comparing interpreters, but it means no sample
        /// ever pays for the collection its own allocations cause — the bill is deferred to the next
        /// sample and the last one never pays at all. <paramref name="gcInclusive"/> closes that by
        /// collecting at the end of the timed region, so the sample pays for what it caused. The two
        /// are different questions ("how fast is the engine" vs "how fast is the engine plus the
        /// collector it feeds") and both have a mode.
        /// </para>
        /// </remarks>
        private static Measurement Measure(
            Func<double> operation,
            int iterations,
            int warmupIterations,
            Action? settle = null,
            Func<MemorySample>? sampleMemory = null,
            int memoryRuns = 3,
            bool gcInclusive = false)
        {
            Func<double> measured = gcInclusive ? MeasureWithCollection(operation, settle) : operation;

            for (int i = 0; i < warmupIterations; i++)
                measured();

            // Memory is counted on its own runs, outside the samples: reading the counters costs
            // several calls, and a run that is being measured for time should have nothing else in
            // it. The heap is settled first so what the run allocates is not confused with what an
            // earlier one left behind.
            settle?.Invoke();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            sampleMemory ??= DefaultMemorySample;
            MemorySample memory = MeasureMemoryMedian(sampleMemory, settle, operation, memoryRuns);

            var samples = new double[iterations];
            for (int i = 0; i < iterations; i++)
            {
                if (!gcInclusive)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    settle?.Invoke();
                }

                var stopwatch = Stopwatch.StartNew();
                measured();
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
                samples[Math.Min(iterations - 1, (int)(iterations * 0.90))],
                samples[Math.Min(iterations - 1, (int)(iterations * 0.99))],
                memory);
        }

        /// <summary>
        /// Wraps the operation so each timed sample ends by collecting — the engine's own collector
        /// and the CLR's — making the sample pay for the garbage it just produced instead of handing
        /// the bill to the next one.
        /// </summary>
        private static Func<double> MeasureWithCollection(Func<double> operation, Action? settle)
            => () =>
            {
                double result = operation();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                settle?.Invoke();
                return result;
            };

        /// <summary>
        /// A workload's memory figures as the median across several untimed runs rather than the
        /// single run it used to be. One run's allocation delta is dominated by where the GC happened
        /// to trigger for allocation-heavy cases, and the median of three gives a figure you can
        /// quote. Every field that an engine marks unavailable is left unavailable.
        /// </summary>
        private static MemorySample MeasureMemoryMedian(
            Func<MemorySample> sampleMemory,
            Action? settle,
            Func<double> operation,
            int runs)
        {
            var allocated = new List<long>(runs);
            var objects = new List<long>(runs);
            var live = new List<long>(runs);
            var heap = new List<long>(runs);

            for (int i = 0; i < runs; i++)
            {
                settle?.Invoke();
                GC.Collect();
                GC.WaitForPendingFinalizers();

                MemorySample before = sampleMemory();
                operation();
                MemorySample delta = sampleMemory().Since(before);

                AddMemory(allocated, delta.AllocatedBytes);
                AddMemory(objects, delta.AllocatedObjects);
                AddMemory(live, delta.LiveObjects);
                AddMemory(heap, delta.HeapBytes);
            }

            return new MemorySample(
                MemoryMedian(allocated),
                MemoryMedian(objects),
                MemoryMedian(live),
                MemoryMedian(heap));
        }

        private static void AddMemory(List<long> values, long value)
        {
            if (value != MemorySample.Unavailable)
                values.Add(value);
        }

        private static long MemoryMedian(List<long> values)
        {
            if (values.Count == 0)
                return MemorySample.Unavailable;
            values.Sort();
            return values[values.Count / 2];
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

            if (_options.Rounds > 1)
                Console.WriteLine("{0} whole-catalogue rounds (shuffle seed {1}); every figure below is the median across rounds.", _options.Rounds, _options.ShuffleSeed);
            else if (_options.Shuffle)
                Console.WriteLine("Catalogue order shuffled by seed {0}.", _options.ShuffleSeed);

            if (_options.GcInclusive)
                Console.WriteLine("Each timed sample pays for the collection its own allocations cause (--gc-inclusive).");
            else
                Console.WriteLine("Surtr collects its heap between runs only, never inside the timed region.");

            if (_options.SurtrGc == SurtrGcBenchMode.Both)
                Console.WriteLine("Surtr runs twice: 'surtr' collects only when the harness asks, 'surtr-auto' collects by itself at its safepoints.");
            else
                Console.WriteLine("Surtr collector: {0}.", _options.SurtrGc.ToString().ToLowerInvariant());

            Console.WriteLine("Machine: {0}", SystemInfo.FingerprintLine());
            Console.WriteLine();

            var names = new List<string>();
            foreach (IBenchEngine engine in engines)
                names.Add(engine.Name);
            names.Add("C# baseline");
            Console.WriteLine("Engines: {0}", string.Join(" vs ", names));
            Console.WriteLine();
            Console.WriteLine("Memory columns are per run, measured outside the timed samples:");
            Console.WriteLine("  bytes   managed bytes allocated (comparable across surtr, lua and c#: all three allocate on the CLR heap)");
            Console.WriteLine("  objs    Surtr objects allocated, from the entity registry");
            Console.WriteLine("  kept    Surtr objects still live when the run returned");
            Console.WriteLine("  c#B     managed bytes the C# baseline allocated, for the same work");
            Console.WriteLine("  spread  interquartile range over the median; above ~10% the median is not yet worth quoting");
            if (_options.Percentiles)
                Console.WriteLine("  p90/p99 the reference engine's 90th/99th percentile, the numbers a frame budget is set from");
            Console.WriteLine("  ok!     verification passed but the reference spread is above 10%: read the median with care");
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
            if (_options.Percentiles)
            {
                header.Add(Pad("p90", 7));
                header.Add(Pad("p99", 7));
            }
            header.Add("result");
            Console.WriteLine(string.Join("  ", header));
        }

        private static void PrintRow(
            Workload workload,
            long size,
            Measurement[] engineMs,
            Measurement baselineMs,
            bool ok,
            bool spreadWarn,
            IReadOnlyList<IBenchEngine> engines,
            bool percentiles,
            bool[]? extreme = null)
        {
            var cells = new List<string>
            {
                Pad(workload.Name, 15),
                Pad(size.ToString(CultureInfo.InvariantCulture), 9),
            };

            for (int i = 0; i < engineMs.Length; i++)
            {
                // A capped engine's figure is a floor from one probe call, not a real median - the
                // "!!" marks it so nobody reads it as an ordinary measurement.
                string cell = extreme != null && i < extreme.Length && extreme[i]
                    ? FormatMs(engineMs[i].Median) + "!!"
                    : FormatMs(engineMs[i].Median);
                cells.Add(Pad(cell, 11));
            }
            cells.Add(Pad(FormatMs(baselineMs.Median), 11));

            // One ratio per engine after the first: how much slower that engine is than the
            // reference (Surtr, which is always first in the list).
            for (int i = 1; i < engines.Count; i++)
            {
                string ratio;
                if (extreme != null && i < extreme.Length && extreme[i])
                {
                    ratio = engineMs[0].Median > 0
                        ? ">=" + FormatRatio(RunnerOptions.MoonSharpExtremeRatio)
                        : "  —  ";
                }
                else
                {
                    ratio = engineMs[0].Median > 0 && engineMs[i].Median > 0
                        ? FormatRatio(engineMs[i].Median / engineMs[0].Median)
                        : "  —  ";
                }
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
            cells.Add(Pad(FormatPercent(engineMs.Length > 0 ? engineMs[0].Spread : 0), 8));

            if (percentiles && engineMs.Length > 0)
            {
                cells.Add(Pad(FormatMs(engineMs[0].P90), 7));
                cells.Add(Pad(FormatMs(engineMs[0].P99), 7));
            }

            bool anyExtreme = extreme != null && Array.Exists(extreme, e => e);
            cells.Add((ok ? (spreadWarn ? "ok!" : "ok") : "FAIL")
                + (workload.DiagnosticOnly ? " (diag)" : "")
                + (anyExtreme ? " EXTREMO-LENTO" : ""));

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
                    // A diagnostic-only case (e.g. vec2Class) runs and is reported like any other,
                    // but it exists to document an avoidable idiom's cost, not to rank engines - so
                    // it stays out of the one number meant to summarise the whole suite.
                    if (row.DiagnosticOnly)
                        continue;

                    // A capped engine's figure is a floor from one probe call, not a real median -
                    // averaging it in would let one extreme outlier dominate the geometric mean.
                    if (row.IsExtreme(i))
                        continue;

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

        private void AppendCsv(string path, List<CsvRow> rows, IReadOnlyList<IBenchEngine> engines)
        {
            var engineNames = new List<string>(engines.Count);
            foreach (IBenchEngine engine in engines)
                engineNames.Add(engine.Name);

            bool appendHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            var line = new StringBuilder();
            using (var writer = new StreamWriter(path, append: true))
            {
                if (appendHeader)
                {
                    // Lines a CSV reader must skip: the machine a run happened on, and the settings
                    // that produced it. Two runs of the same command on different machines are
                    // different data and this is the only record of which was which.
                    writer.WriteLine("# machine: " + SystemInfo.FingerprintLine());
                    writer.WriteLine("# settings: iters={0} warmup={1} rounds={2} shuffle={3} seed={4} gc-inclusive={5} memory-runs={6} surtr-gc={7}",
                        _options.Iterations, _options.WarmupIterations, _options.Rounds, _options.Shuffle, _options.ShuffleSeed, _options.GcInclusive, _options.MemoryRuns, _options.SurtrGc.ToString().ToLowerInvariant());

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
                    if (HasEngine(engines, "surtr-auto"))
                        line.Append(",surtr_auto_alloc_bytes,surtr_auto_alloc_objects,surtr_auto_kept_objects,surtr_auto_heap_bytes");
                    line.Append(",lua_alloc_bytes,luajit_heap_bytes,csharp_alloc_bytes");
                    line.Append(",spread_pct");
                    line.Append(",surtr_p90_ms,surtr_p99_ms,csharp_p90_ms,csharp_p99_ms");
                    line.Append(",ok,diagnostic_only,extreme_engines");
                    writer.WriteLine(line);
                }

                foreach (var row in rows)
                {
                    line.Clear();
                    line.Append(row.Name);
                    line.Append(',').Append(row.Size.ToString(CultureInfo.InvariantCulture));
                    foreach (Measurement measurement in row.EngineMeasurements)
                        line.Append(',').Append(measurement.Median.ToString("F3", CultureInfo.InvariantCulture));
                    line.Append(',').Append(row.Baseline.Median.ToString("F3", CultureInfo.InvariantCulture));
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
                    if (row.EngineMeasurements.Length > 0 && row.EngineMeasurements[0].Median > 0 && row.Baseline.Median > 0)
                        line.Append(',').Append((row.EngineMeasurements[0].Median / row.Baseline.Median).ToString("F3", CultureInfo.InvariantCulture));
                    else
                        line.Append(',');

                    // Quoted: several of these contain a comma, and an unquoted one would shift
                    // every column after it by a field.
                    line.Append(",\"").Append(row.Measures.Replace("\"", "\"\"")).Append('"');

                    // Named by engine rather than by position: the columns a run produces depend on
                    // which engines it was given, and a reader joining two CSVs from different
                    // invocations should not have to work out which is which.
                    AppendMemory(line, MemoryOf(row, engines, "surtr"), objectsAndHeap: true);
                    if (HasEngine(engines, "surtr-auto"))
                        AppendMemory(line, MemoryOf(row, engines, "surtr-auto"), objectsAndHeap: true);
                    AppendMemory(line, MemoryOf(row, engines, "lua"), objectsAndHeap: false);

                    MemorySample luajit = MemoryOf(row, engines, "luajit");
                    line.Append(',').Append(Number(luajit.HeapBytes));
                    line.Append(',').Append(Number(row.Baseline.Memory.AllocatedBytes));

                    if (row.EngineMeasurements.Length > 0)
                        line.Append(',').Append(FormatPercent(row.EngineMeasurements[0].Spread));
                    else
                        line.Append(',');

                    if (row.EngineMeasurements.Length > 0)
                    {
                        line.Append(',').Append(row.EngineMeasurements[0].P90.ToString("F3", CultureInfo.InvariantCulture));
                        line.Append(',').Append(row.EngineMeasurements[0].P99.ToString("F3", CultureInfo.InvariantCulture));
                        line.Append(',').Append(row.Baseline.P90.ToString("F3", CultureInfo.InvariantCulture));
                        line.Append(',').Append(row.Baseline.P99.ToString("F3", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        line.Append(",,,,");
                    }

                    line.Append(',').Append(row.Ok ? "ok" : "FAIL");
                    line.Append(',').Append(row.DiagnosticOnly ? "1" : "0");
                    line.Append(',').Append(ExtremeEnginesField(row.Extreme, engineNames));
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

        private static string FormatChecksum(double value)
            => value.ToString("R", CultureInfo.InvariantCulture);

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

        private static bool HasEngine(IReadOnlyList<IBenchEngine> engines, string name)
        {
            foreach (IBenchEngine engine in engines)
            {
                if (string.Equals(engine.Name, name, StringComparison.Ordinal))
                    return true;
            }

            return false;
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

        /// <summary>The engines the MoonSharp circuit breaker capped for this row, semicolon-joined (a CSV field needs no quoting for that separator). Empty when none did.</summary>
        private static string ExtremeEnginesField(bool[]? extreme, IReadOnlyList<string> engineNames)
        {
            if (extreme == null)
                return "";

            var names = new List<string>();
            for (int i = 0; i < extreme.Length && i < engineNames.Count; i++)
            {
                if (extreme[i])
                    names.Add(engineNames[i]);
            }
            return string.Join(";", names);
        }

        private readonly struct CsvRow
        {
            public readonly string Name;
            public readonly long Size;
            public readonly string Measures;
            public readonly Measurement[] EngineMeasurements;
            public readonly Measurement Baseline;
            public readonly bool Ok;
            public readonly bool DiagnosticOnly;

            /// <summary>Which engine indices the MoonSharp circuit breaker capped, indexed the same as <see cref="EngineMeasurements"/>. Null means none did.</summary>
            public readonly bool[]? Extreme;

            public CsvRow(string name, long size, string measures, Measurement[] engineMeasurements, Measurement baseline, bool ok, bool diagnosticOnly, bool[]? extreme = null)
            {
                Name = name;
                Size = size;
                Measures = measures;
                EngineMeasurements = engineMeasurements;
                Baseline = baseline;
                Ok = ok;
                DiagnosticOnly = diagnosticOnly;
                Extreme = extreme;
            }

            public bool IsExtreme(int engineIndex) => Extreme != null && engineIndex < Extreme.Length && Extreme[engineIndex];
        }

        /// <summary>One workload's measurements across all rounds, reduced to a single row.</summary>
        private sealed class Accumulator
        {
            public long Size;
            public bool Ok;

            /// <summary>
            /// Which engine indices tripped the MoonSharp circuit breaker on at least one round -
            /// null until the first round that trips it, then OR'd across every later round so a
            /// case that is only sometimes extreme still gets the label.
            /// </summary>
            public bool[]? Extreme;

            private readonly List<Measurement[]> _engineRounds = new();
            private readonly List<Measurement> _baselineRounds = new();

            public void Add(Measurement[] engine, Measurement baseline, bool[]? extreme = null)
            {
                _engineRounds.Add(engine);
                _baselineRounds.Add(baseline);
                if (extreme != null)
                {
                    Extreme ??= new bool[extreme.Length];
                    for (int i = 0; i < extreme.Length; i++)
                        Extreme[i] |= extreme[i];
                }
            }

            /// <summary>
            /// The row this workload prints: the median of its per-round figures, per engine. One
            /// round passes straight through; several rounds are combined field by field so the
            /// reported median, quartiles, percentiles and memory are all medians of the same shape.
            /// </summary>
            public (Measurement[] Engine, Measurement Baseline) Reduce()
            {
                int rounds = _engineRounds.Count;
                if (rounds == 1)
                    return (_engineRounds[0], _baselineRounds[0]);

                var engine = new Measurement[_engineRounds[0].Length];
                for (int i = 0; i < engine.Length; i++)
                {
                    var values = new Measurement[rounds];
                    for (int r = 0; r < rounds; r++)
                        values[r] = _engineRounds[r][i];
                    engine[i] = ReduceMeasurement(values);
                }

                return (engine, ReduceMeasurement(_baselineRounds));
            }

            private static Measurement ReduceMeasurement(IReadOnlyList<Measurement> rounds)
                => new Measurement(
                    MedianAcross(rounds, m => m.Median),
                    MedianAcross(rounds, m => m.Min),
                    MedianAcross(rounds, m => m.Max),
                    MedianAcross(rounds, m => m.LowerQuartile),
                    MedianAcross(rounds, m => m.UpperQuartile),
                    MedianAcross(rounds, m => m.P90),
                    MedianAcross(rounds, m => m.P99),
                    MedianMemory(rounds));

            private static double MedianAcross(IReadOnlyList<Measurement> rounds, Func<Measurement, double> selector)
            {
                var values = new double[rounds.Count];
                for (int i = 0; i < rounds.Count; i++)
                    values[i] = selector(rounds[i]);
                Array.Sort(values);
                return values[values.Length / 2];
            }

            private static MemorySample MedianMemory(IReadOnlyList<Measurement> rounds)
                => new MemorySample(
                    MedianLong(rounds, m => m.Memory.AllocatedBytes),
                    MedianLong(rounds, m => m.Memory.AllocatedObjects),
                    MedianLong(rounds, m => m.Memory.LiveObjects),
                    MedianLong(rounds, m => m.Memory.HeapBytes));

            private static long MedianLong(IReadOnlyList<Measurement> rounds, Func<Measurement, long> selector)
            {
                var values = new List<long>(rounds.Count);
                foreach (Measurement round in rounds)
                {
                    long value = selector(round);
                    if (value != MemorySample.Unavailable)
                        values.Add(value);
                }

                if (values.Count == 0)
                    return MemorySample.Unavailable;
                values.Sort();
                return values[values.Count / 2];
            }
        }
    }
}