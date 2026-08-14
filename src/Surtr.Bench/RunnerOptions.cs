#nullable enable

using System;
using System.Globalization;

namespace Surtr.Bench
{
    /// <summary>Parsed command-line options for the harness.</summary>
    internal sealed class RunnerOptions
    {
        public string? WorkloadFilter;
        public int Iterations = 5;
        public double Scale = 1.0;
        public bool Warmup = true;
        public bool SurtrOnly;
        public bool LuaOnly;
        public bool BaselineOnly;
        public string? CsvPath;
        public bool ShowHelp;

        public const string Usage =
            "surtrbench — the Surtr VM measured against MoonSharp (Lua) and a C# baseline\n"
            + "\n"
            + "usage: dotnet run --project src/Surtr.Bench -- [options]\n"
            + "\n"
            + "options:\n"
            + "  --workload <substring>  run only cases whose name contains <substring>\n"
            + "  --iters <n>             timed iterations per case (default 5); the median is reported\n"
            + "  --scale <factor>        multiply every workload size by <factor> (default 1.0)\n"
            + "  --no-warmup             skip the untimed warm-up run before each case\n"
            + "  --surtr-only            run only the Surtr side\n"
            + "  --lua-only              run only the MoonSharp side\n"
            + "  --baseline-only         run only the C# baseline\n"
            + "  --csv <path>            append the results to <path> as CSV\n"
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
                    case "--no-warmup":
                        options.Warmup = false;
                        break;
                    case "--surtr-only":
                        options.SurtrOnly = true;
                        break;
                    case "--lua-only":
                        options.LuaOnly = true;
                        break;
                    case "--baseline-only":
                        options.BaselineOnly = true;
                        break;
                    case "--csv":
                        options.CsvPath = NextValue(args, ref i, arg);
                        break;
                    default:
                        throw new ArgumentException($"unknown option '{arg}'.");
                }
            }

            int modes = (options.SurtrOnly ? 1 : 0) + (options.LuaOnly ? 1 : 0) + (options.BaselineOnly ? 1 : 0);
            if (modes > 1)
                throw new ArgumentException("--surtr-only, --lua-only and --baseline-only are mutually exclusive.");

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
