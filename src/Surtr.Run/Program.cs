#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Stdlib;
using Surtr.VM;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Surtr.Run
{
    /// <summary>
    /// <c>surtr</c>: loads <c>.surtrc</c> images (or a <c>.surtrx</c> package) into a real
    /// <see cref="SurtrRuntime"/> and calls into them - <c>surtrc</c>'s other half, the way
    /// <c>java</c> is to <c>javac</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Language-Syntax.md</c> §2.5 makes naming the function to call necessary rather than a
    /// convenience: Surtr has no <c>main</c>. A loose image set needs its module path and function
    /// named on the command line; a <c>.surtrx</c> package carries its own entry point, so
    /// <c>surtr &lt;file&gt;.surtrx</c> just runs it.
    /// </para>
    /// <para>
    /// The standard library is the runtime's to provide: every program that runs here gets it, so
    /// <see cref="SurtrStdlib.LoadAll"/> runs before any user module is loaded. Its native bodies
    /// (today <c>Math</c>'s sixteen operations) are C# function pointers that only this process can
    /// supply, and a package that also embeds the stdlib's module images is simply skipped where it
    /// overlaps - so a program using <c>Math</c> runs whether it was packaged or not.
    /// </para>
    /// </remarks>
    internal static class Program
    {
        private const int Ok = 0;
        private const int Failed = 1;
        private const int BadUsage = 2;

        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Usage();
                return BadUsage;
            }

            if (IsHelp(args[0]))
            {
                Usage();
                return Ok;
            }

            if (IsVersion(args[0]))
            {
                Console.WriteLine($"surtr {VersionText()}");
                return Ok;
            }

            // A package given directly is the short form of 'run'.
            if (IsPackage(args[0]))
                return RunPackage(args[0], args.Skip(1).ToArray());

            return args[0] switch
            {
                "run" => Run(args),
                "list" => List(args),
                "help" => OkWith(Usage),
                "version" => OkWith(() => Console.WriteLine($"surtr {VersionText()}")),
                _ => Unknown(args[0]),
            };
        }

        private static int Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("surtr: 'run' needs a path.");
                Usage();
                return BadUsage;
            }

            string target = args[1];

            if (IsPackage(target))
                return RunPackage(target, args.Skip(2).ToArray());

            if (args.Length < 4)
            {
                Console.Error.WriteLine("surtr: 'run' on loose images needs a path, a module path and a function name.");
                Usage();
                return BadUsage;
            }

            string modulePath = args[2];
            string function = args[3];
            string[] callArguments = args.Skip(4).ToArray();

            if (!TryLoadLoose(target, out var runtime, out _))
                return Failed;

            using (runtime)
                return Invoke(runtime, modulePath, function, callArguments);
        }

        private static int RunPackage(string path, string[] callArguments)
        {
            if (!TryLoadPackage(path, out var runtime, out var package))
                return Failed;

            using (runtime)
                return Invoke(runtime, package.EntryModulePath, package.EntryFunction, callArguments);
        }

        private static int List(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("surtr: 'list' needs a path.");
                Usage();
                return BadUsage;
            }

            string target = args[1];

            if (IsPackage(target))
            {
                if (!TryLoadPackage(target, out var pkgRuntime, out var pkgPackage))
                    return Failed;

                using (pkgRuntime)
                {
                    ListLoaded(pkgRuntime, pkgPackage);
                    return Ok;
                }
            }

            if (!TryLoadLoose(target, out var looseRuntime, out var looseLoaded))
                return Failed;

            using (looseRuntime)
            {
                ListLoaded(looseRuntime, looseLoaded);
                return Ok;
            }
        }

        /// <summary>Discovers and loads every image a loose path names, reporting failure once.</summary>
        private static bool TryLoadLoose(string path, out SurtrRuntime runtime, out List<string> loaded)
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

        /// <summary>Loads a package, reporting failure once.</summary>
        private static bool TryLoadPackage(string path, out SurtrRuntime runtime, out SurtrPackage package)
        {
            runtime = new SurtrRuntime();
            package = null!;

            try
            {
                package = SurtrPackage.FromBytes(File.ReadAllBytes(path));
                SurtrStdlib.LoadAll(runtime);
                runtime.LoadModules(package.Modules);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SurtrImageFormatException or InvalidOperationException)
            {
                runtime.Dispose();
                Console.Error.WriteLine($"surtr: {exception.Message}");
                return false;
            }
        }

        /// <summary>Resolves and calls the named module-level entry point, printing what it returns.</summary>
        private static int Invoke(SurtrRuntime runtime, string modulePath, string function, string[] callArguments)
        {
            if (!runtime.TryGetModule(modulePath, out var module))
                return Fail($"no module '{modulePath}' was loaded.");

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

        private static void ListLoaded(SurtrRuntime runtime, SurtrPackage package)
        {
            Console.WriteLine($"entry: {package.EntryModulePath}.{package.EntryFunction}");

            foreach (var image in package.Modules)
            {
                if (!runtime.TryGetModule(image.Path, out var module))
                    continue;

                ListModule(module);
            }
        }

        private static void ListLoaded(SurtrRuntime runtime, List<string> loaded)
        {
            foreach (string path in loaded)
            {
                if (!runtime.TryGetModule(path, out var module))
                    continue;

                ListModule(module);
            }
        }

        private static void ListModule(SurtrModule module)
        {
            Console.WriteLine(module.Path);

            var names = module.Methods
                .SelectMany(overloads => overloads)
                .Select(EntryPoint.Shape)
                .OrderBy(shape => shape, StringComparer.Ordinal);

            foreach (string shape in names)
                Console.WriteLine("  " + shape);
        }

        private static bool IsPackage(string path)
            => path.EndsWith(SurtrPackage.FileExtension, StringComparison.OrdinalIgnoreCase);

        private static int Fail(string message)
        {
            Console.Error.WriteLine($"surtr: {message}");
            return Failed;
        }

        private static bool IsHelp(string argument)
            => argument is "-h" or "--help" or "help" or "-?" or "/?";

        private static bool IsVersion(string argument)
            => argument is "-v" or "--version" or "version" or "-V";

        private static string VersionText()
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "0.0.0" : version.ToString();
        }

        private static int OkWith(Action write)
        {
            write();
            return Ok;
        }

        private static int Unknown(string command)
        {
            Console.Error.WriteLine($"surtr: '{command}' is not a command.");
            Usage();
            return BadUsage;
        }

        private static void Usage()
        {
            Console.WriteLine("surtr - the Surtr runtime");
            Console.WriteLine();
            Console.WriteLine("usage: surtr <command> [options]");
            Console.WriteLine();
            Console.WriteLine("commands:");
            Console.WriteLine("  <file>.surtrx [args...]          run a packaged program (short form)");
            Console.WriteLine("  run <path> <module.path> <function> [args...]");
            Console.WriteLine("  run <file>.surtrx [args...]      run a packaged program");
            Console.WriteLine("  list <path>                      list a path's module-level functions");
            Console.WriteLine("  help                            show this help");
            Console.WriteLine("  version                         show the version");
            Console.WriteLine();
            Console.WriteLine("run a program:");
            Console.WriteLine();
            Console.WriteLine("  surtr game.surtrx               run the package's entry point");
            Console.WriteLine("  surtr run game.surtrx          same as above");
            Console.WriteLine("  surtr run out main             run module-level 'main' in module 'out' (loose images)");
            Console.WriteLine("  surtr list game.surtrx         show the entry point and declared functions");
            Console.WriteLine();
            Console.WriteLine("  path          a .surtrc file, a directory of them (searched recursively),");
            Console.WriteLine("                or a .surtrx package.");
            Console.WriteLine("  module.path   the dot-separated module the function is declared in (§2.1).");
            Console.WriteLine("  function      a module-level function's name - there is no 'main' (§2.5), so a");
            Console.WriteLine("                package carries its own entry point; 'list' shows what a path declares.");
            Console.WriteLine("  args          text, converted against each parameter's declared type:");
            Console.WriteLine("                int, float, bool, char, string, or 'null'. A trailing varargs");
            Console.WriteLine("                parameter absorbs whatever is left over.");
            Console.WriteLine();
            Console.WriteLine("The standard library is loaded automatically, so programs using it (e.g. Math)");
            Console.WriteLine("run whether packaged or not. Both forms accept -h/--help and -v/--version.");
        }
    }
}
