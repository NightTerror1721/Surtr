#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Surtr.Bench
{
    /// <summary>How many Surtr engines the run compares, and under which collector policy.</summary>
    internal enum SurtrGcBenchMode
    {
        /// <summary>Only the harness collects, between samples.</summary>
        Manual,

        /// <summary>The runtime also collects by itself at its safepoints (the runtime's default).</summary>
        Automatic,

        /// <summary>Two Surtr engines, one per mode, so the collector policy's cost is a ratio.</summary>
        Both,
    }

    /// <summary>Parsed command-line options for the harness.</summary>
    internal sealed class RunnerOptions
    {
        /// <summary>
        /// Substrings a workload's name must contain to run. Empty means every workload. Repeatable:
        /// each <c>--workload</c> adds a filter, and a case runs when any filter matches it.
        /// </summary>
        public readonly List<string> WorkloadFilters = new();

        public int Iterations = 9;
        public double Scale = 1.0;

        /// <summary>
        /// Untimed runs before the timed ones. Three rather than one: a single warm-up leaves the
        /// heap unsized and any method without a loop still tiering up on call count, both of which
        /// land on the first timed sample and widen the spread the median is drawn from.
        /// </summary>
        public int WarmupIterations = 3;
        public bool SurtrOnly;
        public bool LuaOnly;
        public bool LuajitOnly;
        public bool BaselineOnly;
        public bool NoLuajit;
        public string? CsvPath;
        public bool ShowHelp;

        /// <summary>List the catalogue and what each case measures, then exit without running.</summary>
        public bool ListOnly;

        /// <summary>Run the extended-prefix calibration instead of the catalogue.</summary>
        /// <remarks>
        /// A different question from every other mode: not "how fast is this workload" but "what
        /// does one <c>Ext</c> dispatch cost", which is the number the extended instruction
        /// space's admission rule rests on. See <see cref="PrefixTax"/>.
        /// </remarks>
        public bool PrefixTax;

        /// <summary>Run every workload once at its size and check the engines against the C# baseline, no timing.</summary>
        public bool VerifyOnly;

        /// <summary>Like <see cref="VerifyOnly"/> but at a hundredth of every size and a tight instruction budget: a fast CI sanity pass.</summary>
        public bool Smoke;

        /// <summary>Run the catalogue in a seeded random order rather than declaration order.</summary>
        public bool Shuffle;

        /// <summary>Seed for <see cref="Shuffle"/>. Fixed so a given seed reproduces the exact same order, hence the exact same run.</summary>
        public int ShuffleSeed = 12345;

        /// <summary>Whole-catalogue passes; the reported number per workload is the median across rounds.</summary>
        public int Rounds = 1;

        /// <summary>Print the reference engine's p90 and p99 alongside the median.</summary>
        public bool Percentiles;

        /// <summary>Time the collection the run itself causes, by collecting inside the timed region.</summary>
        public bool GcInclusive;

        /// <summary>How many untimed runs each workload's memory figures are the median of. One is noise for allocation-heavy cases.</summary>
        public int MemoryRuns = 3;

        /// <summary>Exit non-zero if any workload's reference spread is above <see cref="SpreadWarningThreshold"/>.</summary>
        public bool Strict;

        /// <summary>
        /// Which Surtr collector setup to run. <see cref="SurtrGcBenchMode.Both"/> adds a second
        /// Surtr engine so the automatic collector can be compared against the manual one as a ratio
        /// like any other engine.
        /// </summary>
        public SurtrGcBenchMode SurtrGc = SurtrGcBenchMode.Automatic;

        /// <summary>Above this spread the median is not yet worth quoting; <c>--strict</c> turns it into a failure.</summary>
        public const double SpreadWarningThreshold = 0.10;

        /// <summary>Whether the Surtr side runs. Only the baseline mode and the other engines' only-modes suppress it.</summary>
        public bool RunSurtr => !LuaOnly && !LuajitOnly && !BaselineOnly;

        /// <summary>Whether the MoonSharp side runs. <c>--no-luajit</c> does not touch it.</summary>
        public bool RunMoonSharp => !SurtrOnly && !LuajitOnly && !BaselineOnly;

        /// <summary>Whether the LuaJIT side runs. It is on by default and off under any only-mode.</summary>
        public bool RunLuaJit => !NoLuajit && !SurtrOnly && !LuaOnly && !BaselineOnly;

        public const string Usage =
            "surtrbench — the Surtr VM measured against MoonSharp, LuaJIT and a C# baseline\n"
            + "\n"
            + "usage: dotnet run --project src/Surtr.Bench -- [options]\n"
            + "\n"
            + "modes:\n"
            + "  --verify-only        run every workload once and check all engines against the C#\n"
            + "                       baseline; no timing. The exit code is the verdict.\n"
            + "  --smoke              like --verify-only but at a hundredth of every size and a tight\n"
            + "                       instruction budget: the fast CI sanity pass.\n"
            + "\n"
            + "options:\n"
            + "  --workload <substring>  run only cases whose name contains <substring>; repeatable,\n"
            + "                           a case runs when any given substring matches it\n"
            + "  --iters <n>             timed iterations per case (default 9); the median is reported\n"
            + "  --scale <factor>        multiply every workload size by <factor> (default 1.0)\n"
            + "  --warmup <n>            untimed runs before the timed ones (default 3)\n"
            + "  --no-warmup             skip the warm-up entirely; expect a much wider spread\n"
            + "  --rounds <n>            run the whole catalogue <n> times and report the median across\n"
            + "                          rounds (default 1); the best defence against cross-talk between cases\n"
            + "  --shuffle               run the cases in a seeded random order instead of declaration order\n"
            + "  --seed <n>              the shuffle seed (default 12345); also implies --shuffle\n"
            + "  --gc-inclusive          collect inside the timed region, so each sample pays for the\n"
            + "                          collection its own allocations cause; the default defers the cost\n"
            + "                          to the next sample and never charges the last one\n"
            + "  --memory-runs <n>       how many untimed runs a workload's memory figures are the median\n"
            + "                          of (default 3); one run is noise for allocation-heavy cases\n"
            + "  --percentiles           also print the reference engine's p90 and p99 per row\n"
            + "  --strict                exit non-zero if any workload's reference spread is above 10%,\n"
            + "                          or any verification fails\n"
            + "  --surtr-gc <mode>       which Surtr collector to run: manual (harness collects\n"
            + "                          between samples), automatic (the runtime's default: collects\n"
            + "                          at its safepoints too), or both (adds a second Surtr engine\n"
            + "                          so the two policies are compared as a ratio)\n"
            + "  --extreme               the full suite: --shuffle --rounds 3 --iters 15 --warmup 5\n"
            + "                          --memory-runs 5 --percentiles\n"
            + "  --surtr-only            run only the Surtr side\n"
            + "  --lua-only              run only the MoonSharp side\n"
            + "  --luajit-only           run only the LuaJIT side\n"
            + "  --no-luajit             run Surtr and MoonSharp but not LuaJIT\n"
            + "  --baseline-only         run only the C# baseline\n"
            + "  --csv <path>            append the results to <path> as CSV\n"
            + "  --list                  list the catalogue and what each case measures, then exit\n"
            + "  --prefix-tax            measure what one Ext-prefixed dispatch costs, and exit;\n"
            + "                          --iters sets the loop size, --rounds the sample count\n"
            + "  -h, --help              show this help\n";

        public static RunnerOptions Parse(string[] args)
        {
            var options = new RunnerOptions();

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "-h":
                    case "--help":
                        options.ShowHelp = true;
                        break;
                    case "--workload":
                        options.WorkloadFilters.Add(NextValue(args, ref i, arg));
                        break;
                    case "--iters":
                        options.Iterations = ParseInt(NextValue(args, ref i, arg), arg);
                        if (options.Iterations < 1)
                            throw new ArgumentException("--iters must be at least 1.");
                        break;
                    case "--scale":
                        options.Scale = ParseDouble(NextValue(args, ref i, arg), arg);
                        if (options.Scale <= 0)
                            throw new ArgumentException("--scale must be positive.");
                        break;
                    case "--warmup":
                        options.WarmupIterations = ParseInt(NextValue(args, ref i, arg), arg);
                        if (options.WarmupIterations < 0)
                            throw new ArgumentException("--warmup cannot be negative.");
                        break;
                    case "--no-warmup":
                        options.WarmupIterations = 0;
                        break;
                    case "--verify-only":
                        options.VerifyOnly = true;
                        break;
                    case "--smoke":
                        options.Smoke = true;
                        break;
                    case "--rounds":
                        options.Rounds = ParseInt(NextValue(args, ref i, arg), arg);
                        if (options.Rounds < 1)
                            throw new ArgumentException("--rounds must be at least 1.");
                        break;
                    case "--shuffle":
                        options.Shuffle = true;
                        break;
                    case "--seed":
                        options.ShuffleSeed = ParseInt(NextValue(args, ref i, arg), arg);
                        options.Shuffle = true;
                        break;
                    case "--gc-inclusive":
                        options.GcInclusive = true;
                        break;
                    case "--memory-runs":
                        options.MemoryRuns = ParseInt(NextValue(args, ref i, arg), arg);
                        if (options.MemoryRuns < 1)
                            throw new ArgumentException("--memory-runs must be at least 1.");
                        break;
                    case "--percentiles":
                        options.Percentiles = true;
                        break;
                    case "--strict":
                        options.Strict = true;
                        break;
                    case "--surtr-gc":
                        options.SurtrGc = ParseSurtrGc(NextValue(args, ref i, arg));
                        break;
                    case "--extreme":
                        options.Shuffle = true;
                        options.Rounds = 3;
                        options.Iterations = 15;
                        options.WarmupIterations = 5;
                        options.MemoryRuns = 5;
                        options.Percentiles = true;
                        break;
                    case "--surtr-only":
                        options.SurtrOnly = true;
                        break;
                    case "--lua-only":
                        options.LuaOnly = true;
                        break;
                    case "--luajit-only":
                        options.LuajitOnly = true;
                        break;
                    case "--no-luajit":
                        options.NoLuajit = true;
                        break;
                    case "--baseline-only":
                        options.BaselineOnly = true;
                        break;
                    case "--csv":
                        options.CsvPath = NextValue(args, ref i, arg);
                        break;
                    case "--list":
                        options.ListOnly = true;
                        break;
                    case "--prefix-tax":
                        options.PrefixTax = true;
                        break;
                    default:
                        throw new ArgumentException($"unknown option '{arg}'.");
                }
            }

            int modes = (options.SurtrOnly ? 1 : 0) + (options.LuaOnly ? 1 : 0) + (options.LuajitOnly ? 1 : 0) + (options.BaselineOnly ? 1 : 0);
            if (modes > 1)
                throw new ArgumentException("--surtr-only, --lua-only, --luajit-only and --baseline-only are mutually exclusive.");

            return options;
        }

        private static string NextValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"{option} needs a value.");
            return args[++index];
        }

        private static int ParseInt(string text, string option)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                throw new ArgumentException($"'{text}' is not a valid value for {option}.");
            return value;
        }

        private static double ParseDouble(string text, string option)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new ArgumentException($"'{text}' is not a valid value for {option}.");
            return value;
        }

        private static SurtrGcBenchMode ParseSurtrGc(string text)
        {
            switch (text.ToLowerInvariant())
            {
                case "manual":
                    return SurtrGcBenchMode.Manual;
                case "automatic":
                    return SurtrGcBenchMode.Automatic;
                case "both":
                    return SurtrGcBenchMode.Both;
                default:
                    throw new ArgumentException($"'{text}' is not a valid Surtr GC mode; use manual, automatic or both.");
            }
        }
    }
}
