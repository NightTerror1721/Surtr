#nullable enable

using System;

namespace Surtr.Bench
{
    /// <summary>The harness entry point: parse the command line and run what it names.</summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            RunnerOptions options;
            try
            {
                options = RunnerOptions.Parse(args);
            }
            catch (ArgumentException exception)
            {
                Console.Error.WriteLine(exception.Message);
                Console.Error.WriteLine(RunnerOptions.Usage);
                return 2;
            }

            if (options.ShowHelp)
            {
                Console.WriteLine(RunnerOptions.Usage);
                return 0;
            }

            // Not a workload and not a filter over them: it measures the interpreter's dispatch
            // path itself, so it runs instead of the catalogue rather than alongside it.
            if (options.PrefixTax)
            {
                try
                {
                    return PrefixTax.Run(
                        iterations: options.Iterations >= 1000 ? options.Iterations : 2_000_000,
                        samples: Math.Max(options.Rounds, 9));
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("prefix tax measurement failed: " + exception);
                    return 1;
                }
            }

            try
            {
                return new Runner(options).Run();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("benchmark failed: " + exception);
                return 1;
            }
        }
    }
}
