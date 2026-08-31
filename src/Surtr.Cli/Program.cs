#nullable enable

using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;

namespace Surtr.Cli
{
    /// <summary>
    /// <c>surtrc</c>: the command that turns a directory of <c>.surtr</c> files into <c>.surtrc</c>
    /// images.
    /// </summary>
    /// <remarks>
    /// Everything it does is <see cref="SurtrBuild"/> plus argument parsing and an exit code. It is
    /// deliberately that thin: a host embedding Surtr builds a <see cref="SurtrProject"/> itself,
    /// and a command that grew its own idea of a build would be a second one to keep in step.
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
                Console.WriteLine($"surtrc {VersionText()}");
                return Ok;
            }

            return args[0] switch
            {
                "build" => Build(args),
                "help" => OkWith(Usage),
                "version" => OkWith(() => Console.WriteLine($"surtrc {VersionText()}")),
                _ => Unknown(args[0]),
            };
        }

        private static int Build(string[] args)
        {
            bool package = false;
            string? packageName = null;
            string? entryModule = null;
            string? entryFunction = null;
            var positional = new List<string>();

            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--package")
                {
                    package = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        packageName = args[++i];
                }
                else if (args[i] == "--entry")
                {
                    if (i + 2 >= args.Length)
                    {
                        Console.Error.WriteLine("surtrc: '--entry' needs a module path and a function name.");
                        return BadUsage;
                    }

                    entryModule = args[++i];
                    entryFunction = args[++i];
                }
                else
                {
                    positional.Add(args[i]);
                }
            }

            string target = positional.Count > 0 ? positional[0] : ".";
            var build = Locate(target, package, packageName, entryModule, entryFunction, out string described);

            if (build is null)
            {
                Console.Error.WriteLine($"surtrc: {described}");
                return BadUsage;
            }

            foreach (var diagnostic in build.Diagnostics)
                Report(diagnostic);

            if (build.Failed)
            {
                Console.Error.WriteLine($"surtrc: build failed ({build.Diagnostics.ErrorCount} error(s)).");
                return Failed;
            }

            foreach (string written in build.Written)
                Console.WriteLine($"surtrc: wrote {written}");

            Console.WriteLine($"surtrc: {build.Written.Count} module(s) built.");
            return Ok;
        }

        /// <summary>
        /// Works out what was asked for: a project file, a directory holding one, or a source tree.
        /// </summary>
        /// <remarks>
        /// A directory with exactly one <c>.surtrproj</c> in it is the ordinary case and is taken as
        /// meaning that file. Two of them is ambiguous and says so rather than picking — the same
        /// rule §3.5 applies to an overloaded call, for the same reason.
        /// </remarks>
        private static SurtrBuild? Locate(
            string target, bool package, string? packageName, string? entryModule, string? entryFunction, out string problem)
        {
            problem = string.Empty;

            if (File.Exists(target))
                return SurtrBuild.Run(target, package, packageName, entryModule, entryFunction);

            if (!Directory.Exists(target))
            {
                problem = $"there is nothing at '{target}'.";
                return null;
            }

            string[] projects = Directory.GetFiles(target, "*.surtrproj", SearchOption.TopDirectoryOnly);

            if (projects.Length > 1)
            {
                problem = $"'{target}' holds {projects.Length} project files; name the one to build.";
                return null;
            }

            if (projects.Length == 1)
                return SurtrBuild.Run(projects[0], package, packageName, entryModule, entryFunction);

            // No project file, so the directory is the source root and everything takes its default.
            return SurtrBuild.Run(
                target, Path.Combine(target, "build"),
                package: package, packagePath: packageName, entryModulePath: entryModule, entryFunction: entryFunction);
        }

        private static void Report(SurtrDiagnostic diagnostic)
        {
            var writer = diagnostic.IsError ? Console.Error : Console.Out;
            writer.WriteLine(diagnostic.ToString());
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
            Console.Error.WriteLine($"surtrc: '{command}' is not a command.");
            Usage();
            return BadUsage;
        }

        private static void Usage()
        {
            Console.WriteLine("surtrc - the Surtr compiler");
            Console.WriteLine();
            Console.WriteLine("usage: surtrc <command> [options]");
            Console.WriteLine();
            Console.WriteLine("commands:");
            Console.WriteLine("  build [path] [--package [name]] [--entry module function]");
            Console.WriteLine("  help               show this help");
            Console.WriteLine("  version            show the version");
            Console.WriteLine();
            Console.WriteLine("build compiles a .surtr / .surtrproj tree into loadable images.");
            Console.WriteLine();
            Console.WriteLine("  path            a .surtrproj file, a directory holding one, or a source");
            Console.WriteLine("                  tree. Defaults to the current directory.");
            Console.WriteLine("  --package [name] write a single .surtrx package instead of loose .surtrc");
            Console.WriteLine("                  images. The 'package' project directive does the same; a");
            Console.WriteLine("                  name overrides the default ('<module>.surtrx').");
            Console.WriteLine("  --entry mod fn   set the package entry point, overriding the project's");
            Console.WriteLine("                  'entry' directive. Without either, a module-level 'main'");
            Console.WriteLine("                  is auto-detected (an error if there is more than one).");
            Console.WriteLine();
            Console.WriteLine("A project file (.surtrproj) is one directive per line:");
            Console.WriteLine();
            Console.WriteLine("  root      = src           where module paths are derived from (§2.1)");
            Console.WriteLine("  module    = game          what the source root itself is called");
            Console.WriteLine("  output    = build         where .surtrc images are written");
            Console.WriteLine("  define    Debug = true    a build constant (§7.4)");
            Console.WriteLine("  reference ../lib/x.surtrc a module image compiled earlier (e.g. the stdlib)");
            Console.WriteLine("  entry     game main       the module-level function to start from");
            Console.WriteLine("  package   = true          write a .surtrx package");
            Console.WriteLine("  warningsAsErrors = true   turn warnings into errors");
            Console.WriteLine("  suppress  Code1, Code2    silence named diagnostics");
            Console.WriteLine();
            Console.WriteLine("To run a program, see 'surtr'. Both tools accept -h/--help and -v/--version.");
        }
    }
}
