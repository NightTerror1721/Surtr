#nullable enable

using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using System;
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
            if (args.Length == 0 || IsHelp(args[0]))
            {
                Usage();
                return args.Length == 0 ? BadUsage : Ok;
            }

            return args[0] switch
            {
                "build" => Build(args),
                _ => Unknown(args[0]),
            };
        }

        private static int Build(string[] args)
        {
            string target = args.Length > 1 ? args[1] : ".";
            var build = Locate(target, out string described);

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
        private static SurtrBuild? Locate(string target, out string problem)
        {
            problem = string.Empty;

            if (File.Exists(target))
                return SurtrBuild.Run(target);

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
                return SurtrBuild.Run(projects[0]);

            // No project file, so the directory is the source root and everything takes its default.
            return SurtrBuild.Run(target, Path.Combine(target, "build"));
        }

        private static void Report(SurtrDiagnostic diagnostic)
        {
            var writer = diagnostic.IsError ? Console.Error : Console.Out;
            writer.WriteLine(diagnostic.ToString());
        }

        private static bool IsHelp(string argument)
            => argument is "-h" or "--help" or "help" or "-?" or "/?";

        private static int Unknown(string command)
        {
            Console.Error.WriteLine($"surtrc: '{command}' is not a command.");
            Usage();
            return BadUsage;
        }

        private static void Usage()
        {
            Console.WriteLine("surtrc build [path]");
            Console.WriteLine();
            Console.WriteLine("  path   a .surtrproj file, a directory holding one, or a source tree.");
            Console.WriteLine("         Defaults to the current directory.");
            Console.WriteLine();
            Console.WriteLine("A project file is one directive per line:");
            Console.WriteLine();
            Console.WriteLine("  root      = src           where module paths are derived from (§2.1)");
            Console.WriteLine("  module    = game          what the root itself is called");
            Console.WriteLine("  output    = build         where .surtrc images are written");
            Console.WriteLine("  define    Debug = true    a build constant (§7.4)");
            Console.WriteLine("  reference ../lib/x.surtrc a module compiled earlier");
        }
    }
}
