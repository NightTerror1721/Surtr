#nullable enable

using System;
using System.Globalization;

namespace Surtr.Bench
{
    /// <summary>Parsed command-line options for the harness.</summary>
    internal sealed class RunnerOptions
    {
        public string? WorkloadFilter;
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
            + "options:\n"
            + "  --workload <substring>  run only cases whose name contains <substring>\n"
            + "  --iters <n>             timed iterations per case (default 9); the median is reported\n"
            + "  --scale <factor>        multiply every workload size by <factor> (default 1.0)\n"
            + "  --warmup <n>            untimed runs before the timed ones (default 3)\n"
            + "  --no-warmup             skip the warm-up entirely; expect a much wider spread\n"
            + "  --surtr-only            run only the Surtr side\n"
            + "  --lua-only              run only the MoonSharp side\n"
            + "  --luajit-only           run only the LuaJIT side\n"
            + "  --no-luajit             run Surtr and MoonSharp but not LuaJIT\n"
            + "  --baseline-only         run only the C# baseline\n"
            + "  --csv <path>            append the results to <path> as CSV\n"
            + "  --list                  list the catalogue and what each case measures, then exit\n"
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
                        options.WorkloadFilter = NextValue(args, ref i, arg);
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
    }
}
