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
