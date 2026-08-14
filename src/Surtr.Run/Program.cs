#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Run
{
    /// <summary>
    /// <c>surtr</c>: loads <c>.surtrc</c> images into a real <see cref="SurtrRuntime"/> and calls
    /// into them - <c>surtrc</c>'s other half, the way <c>java</c> is to <c>javac</c>.
    /// </summary>
    /// <remarks>
    /// <c>Language-Syntax.md</c> §2.5 makes this necessary rather than a convenience: Surtr has no
    /// <c>main</c>, so a host is expected to name whatever it wants to call. This is the smallest
    /// possible host that does that from a shell - it builds no compilation and references
    /// <c>Surtr.Core</c> alone, since running a compiled artifact needs none of the front end. What
    /// it cannot do is give a native import or a host-declared class a body: that needs C# on the
    /// other end, which is what embedding the runtime directly is for.
    /// </remarks>
    internal static class Program
    {
        private const int Ok = 0;
        private const int Failed = 1;
        private const int BadUsage = 2;

        private static int Main(string[] args)
        {
            if (args.Length == 0 || IsHelp(args[0]))
            {
                Usage();
                return args.Length == 0 ? BadUsage : Ok;
            }

            return args[0] switch
            {
                "run" => Run(args),
                "list" => List(args),
                _ => Unknown(args[0]),
            };
        }

        private static int Run(string[] args)
        {
            if (args.Length < 4)
            {
                Console.Error.WriteLine("surtr: 'run' needs a path, a module path and a function name.");
                Usage();
                return BadUsage;
            }

            string path = args[1];
            string modulePath = args[2];
            string function = args[3];
            string[] callArguments = args.Skip(4).ToArray();

            if (!TryLoad(path, out var runtime, out var loaded))
                return Failed;

            using (runtime)
            {
                if (!runtime.TryGetModule(modulePath, out var module))
                    return Fail($"no module '{modulePath}' was loaded from '{path}'.\n{Loaded(loaded)}");

                SurtrMethodInfo method;
                SurtrValue[] bound;

                try
                {
                    method = EntryPoint.Resolve(module, function, callArguments.Length);
                    bound = EntryPoint.Bind(runtime, method, callArguments);
                }
                catch (Exception exception) when (exception is EntryPoint.InvocationException or ArgumentBinding.BindingException)
                {
                    return Fail(exception.Message);
                }

                SurtrValue result;

                try
                {
                    result = runtime.Invoke(method, bound);
                }
                catch (SurtrExecutionException exception)
                {
                    // Leaves the interpreter mid-frame - nothing runs after this, but resetting first
                    // is what CLAUDE.md says a host does before touching the runtime again, and
                    // Dispose() below is exactly that kind of touch.
                    runtime.ResetExecution();
                    return Fail(exception.Message);
                }

                string? described = EntryPoint.Describe(runtime, method, result);
                if (described is not null)
                    Console.WriteLine(described);

                return Ok;
            }
        }

        private static int List(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("surtr: 'list' needs a path.");
                Usage();
                return BadUsage;
            }

            if (!TryLoad(args[1], out var runtime, out var loaded))
                return Failed;

            using (runtime)
            {
                foreach (string path in loaded)
                {
                    if (!runtime.TryGetModule(path, out var module))
                        continue;

                    Console.WriteLine(path);

                    var names = module.Methods
                        .SelectMany(overloads => overloads)
                        .Select(EntryPoint.Shape)
                        .OrderBy(shape => shape, StringComparer.Ordinal);

                    foreach (string shape in names)
                        Console.WriteLine("  " + shape);
                }

                return Ok;
            }
        }

        /// <summary>Discovers and loads every image at <paramref name="path"/>, reporting failure once.</summary>
        private static bool TryLoad(string path, out SurtrRuntime runtime, out List<string> loaded)
        {
            runtime = new SurtrRuntime();
            loaded = new List<string>();

            try
            {
                var files = ModuleSet.Discover(path);
                loaded = ModuleSet.Load(runtime, files);
                return true;
            }
            catch (ModuleSet.LoadFailure failure)
            {
                runtime.Dispose();
                Console.Error.WriteLine($"surtr: {failure.Message}");
                return false;
            }
        }

        private static string Loaded(IReadOnlyList<string> paths)
            => paths.Count == 0 ? "Nothing was loaded." : "Loaded: " + string.Join(", ", paths);

        private static int Fail(string message)
        {
            Console.Error.WriteLine($"surtr: {message}");
            return Failed;
        }

        private static bool IsHelp(string argument)
            => argument is "-h" or "--help" or "help" or "-?" or "/?";

        private static int Unknown(string command)
        {
            Console.Error.WriteLine($"surtr: '{command}' is not a command.");
            Usage();
            return BadUsage;
        }

        private static void Usage()
        {
            Console.WriteLine("surtr run <path> <module.path> <function> [args...]");
            Console.WriteLine("surtr list <path>");
            Console.WriteLine();
            Console.WriteLine("  path          a .surtrc file, or a directory of them (searched recursively).");
            Console.WriteLine("  module.path   the dot-separated module the function is declared in (§2.1).");
            Console.WriteLine("  function      a module-level function's name - there is no 'main' (§2.5),");
            Console.WriteLine("                so this is what says where to start. 'list' shows what a");
            Console.WriteLine("                loaded path declares.");
            Console.WriteLine("  args          text, converted against each parameter's declared type:");
            Console.WriteLine("                int, float, bool, char, string, or 'null'. A trailing");
            Console.WriteLine("                varargs parameter absorbs whatever is left over.");
            Console.WriteLine();
            Console.WriteLine("Only a module-level function can be called this way - not a method on a class,");
            Console.WriteLine("which would need an instance nothing at the command line can construct.");
        }
    }
}
