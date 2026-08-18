#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Bytecode.Image;
using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Surtr.Stdlib.Tool
{
    /// <summary>
    /// Compiles the Surtr stdlib sources to <c>.surtrc</c> images on disk, one per <c>.surtr</c> file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The executable the <c>Surtr.Stdlib.csproj</c> build target invokes. It takes two arguments —
    /// the source root (whose <c>surtr/</c> tree holds the <c>.surtr</c> sources) and the output
    /// directory — and writes one <c>surtrc</c> image per source file into the latter.
    /// </para>
    /// <para>
    /// Each <c>.surtr</c> file is its own module, named by its full location under <c>surtr/</c>:
    /// every directory segment plus the file name (without extension) becomes a dotted path segment,
    /// so <c>surtr/math/Angle.surtr</c> is module <c>surtr.math.Angle</c> and a file at
    /// <c>surtr/Math.surtr</c> is module <c>surtr.Math</c>. This matches §2.1 — a module has no
    /// header, so where a file lives is the only thing that names it — with the file name standing
    /// in as the final segment, since the stdlib keeps each module in its own file.
    /// </para>
    /// <para>
    /// It deliberately reads the <c>.surtr</c> sources from <em>disk</em> and references only
    /// <c>Surtr.Compiler</c>. Referencing <c>Surtr.Stdlib</c> (which builds this tool) would be a
    /// circular dependency, and reading the sources means the tool needs no assembly to inspect.
    /// </para>
    /// </remarks>
    internal static class Program
    {
        private const string ModulePrefix = "surtr";

        private const string SourceFileExtension = ".surtr";

        /// <summary>
        /// The flat, sorted, one-per-line list of every native link name this build compiled.
        /// Deliberately plain text rather than a structured format: this tool and
        /// <c>Surtr.Core</c> (which embeds it) are both <c>netstandard2.1</c>, and a JSON
        /// serializer is a dependency the host would also have to ship (the same reasoning
        /// <c>SurtrProjectFile.cs</c>'s own line-directive format follows).
        /// </summary>
        internal const string NativeLinkNamesFileName = "native-link-names.txt";

        private static int Main(string[] args)
        {
            if (args.Length < 2 || string.IsNullOrEmpty(args[0]) || string.IsNullOrEmpty(args[1]))
            {
                Console.Error.WriteLine("Usage: Surtr.Stdlib.Tool <source-root> <output-directory> [<disassembly-directory>]");
                return 2;
            }

            string sourceRoot = Path.GetFullPath(args[0]);
            string outputDirectory = Path.GetFullPath(args[1]);
            string disassemblyDirectory = Path.GetFullPath(
                args.Length > 2 && !string.IsNullOrEmpty(args[2])
                    ? args[2]
                    : Path.Combine(outputDirectory, "disasm"));

            var sources = FindSources(sourceRoot);
            if (sources.Count == 0)
            {
                Console.Error.WriteLine("No .surtr sources found under '" + sourceRoot + "\\surtr'.");
                return 2;
            }

            sources.Sort(StringComparer.Ordinal);

            Directory.CreateDirectory(outputDirectory);
            Directory.CreateDirectory(disassemblyDirectory);

            var nativeLinkNames = new List<string>();

            if (!BuildAll(sourceRoot, sources, outputDirectory, disassemblyDirectory, nativeLinkNames, out string diagnostics))
            {
                Console.Error.WriteLine("Stdlib build failed:");
                Console.Error.WriteLine(diagnostics);
                return 1;
            }

            // Written next to the images themselves: what Surtr.Core embeds alongside them, and
            // what a test compares against SurtrStdlib.RegisterNativeBodies to catch a `native fun`
            // added to the stdlib source without its C# body being registered - before anyone loads
            // a runtime and finds out the hard way.
            nativeLinkNames.Sort(StringComparer.Ordinal);
            string manifestPath = Path.Combine(outputDirectory, NativeLinkNamesFileName);
            File.WriteAllLines(manifestPath, nativeLinkNames);

            Console.WriteLine("Wrote " + sources.Count + " stdlib image(s) to " + outputDirectory);
            Console.WriteLine("Wrote " + sources.Count + " disassembly(ies) to " + disassemblyDirectory);
            Console.WriteLine("Wrote " + nativeLinkNames.Count + " native link name(s) to " + manifestPath);
            return 0;
        }

        /// <summary>The module name for a <c>.surtr</c> file at <paramref name="relative"/> under <c>surtr/</c>.</summary>
        private static string ModuleOf(string relative)
        {
            string withoutExtension = relative.EndsWith(SourceFileExtension, StringComparison.Ordinal)
                ? relative.Substring(0, relative.Length - SourceFileExtension.Length)
                : relative;
            return ModulePrefix + ModulePath.Separator + withoutExtension.Replace('/', ModulePath.Separator);
        }

        /// <summary>
        /// Every <c>.surtr</c> file under <c>&lt;sourceRoot&gt;/surtr/</c>, as a path relative to
        /// the <c>surtr/</c> root, with forward slashes. Each is its own module.
        /// </summary>
        private static List<string> FindSources(string sourceRoot)
        {
            var names = new List<string>();
            string surtrRoot = Path.Combine(sourceRoot, ModulePrefix);
            if (!Directory.Exists(surtrRoot))
                return names;

            foreach (string file in Directory.GetFiles(surtrRoot, "*" + SourceFileExtension, SearchOption.AllDirectories))
                names.Add(Path.GetRelativePath(surtrRoot, file).Replace('\\', '/'));

            return names;
        }

        /// <summary>
        /// Compiles every <c>.surtr</c> source in one compilation and writes one <c>.surtrc</c>
        /// image per module into <paramref name="outputDirectory"/>, plus a disassembled text
        /// rendering of each into <paramref name="disassemblyDirectory"/>.
        /// </summary>
        /// <remarks>
        /// One compilation for all of them is what makes a cross-module <c>import</c> work — a
        /// stdlib module can name another (<c>List.surtr</c> imports <c>surtr.collections.Collection</c>),
        /// and a module only exists in the same compilation it is declared in. Each file is still
        /// its own module: the project is told the module path outright rather than letting §2.1's
        /// directory derivation fold every file in a folder into one module.
        /// </remarks>
        private static bool BuildAll(
            string sourceRoot,
            List<string> sources,
            string outputDirectory,
            string disassemblyDirectory,
            List<string> nativeLinkNames,
            out string diagnostics)
        {
            var project = new SurtrProject(sourceRoot, rootModulePath: ModulePrefix);
            foreach (string relative in sources)
            {
                string realFile = Path.Combine(sourceRoot, ModulePrefix, relative.Replace('/', Path.DirectorySeparatorChar));
                project.AddSourceFile(realFile, ModuleOf(relative), File.ReadAllText(realFile));
            }

            var bag = new StringBuilder();

            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            foreach (var diagnostic in compilation.Diagnostics)
                bag.AppendLine(diagnostic.ToString());

            if (compilation.HasErrors)
            {
                diagnostics = bag.ToString();
                return false;
            }

            var emitter = new ModuleEmitter(compilation, binder);
            if (!emitter.TryEmit())
            {
                foreach (var diagnostic in compilation.Diagnostics)
                    bag.AppendLine(diagnostic.ToString());
                diagnostics = bag.ToString();
                return false;
            }

            var images = emitter.EmitImages();
            if (images.Count == 0)
            {
                diagnostics = bag.ToString();
                return false;
            }

            for (int i = 0; i < images.Count; i++)
            {
                var module = emitter.Modules[i];

                string path = Path.Combine(outputDirectory, module.Path + SurtrModuleImage.FileExtension);
                File.WriteAllBytes(path, images[i].ToBytes());

                // The disassembler reads an emitter-built module (the only form whose name tables
                // are populated - it cannot render a module re-instantiated from image bytes), and
                // the emitter module is exactly what just got serialized into the image above, so
                // this is the human-checkable view of precisely what was compiled.
                string disasm = SurtrBytecodeDisassembler.Disassemble(module);
                string disasmPath = Path.Combine(disassemblyDirectory, module.Path + ".txt");
                File.WriteAllText(disasmPath, disasm);

                CollectNativeLinkNames(module, nativeLinkNames);
            }

            diagnostics = bag.ToString();
            return true;
        }

        /// <summary>
        /// Every <c>native fun</c>/<c>native let</c>/<c>native var</c> link name this module (or
        /// any class nested inside it, at any depth) declares.
        /// </summary>
        private static void CollectNativeLinkNames(SurtrModule module, List<string> nativeLinkNames)
        {
            foreach (var overloads in module.Methods)
                CollectNativeLinkNames(overloads, nativeLinkNames);

            foreach (var declared in module.Classes)
                CollectNativeLinkNames(declared, nativeLinkNames);
        }

        private static void CollectNativeLinkNames(SurtrClass declared, List<string> nativeLinkNames)
        {
            foreach (var overloads in declared.Methods)
                CollectNativeLinkNames(overloads, nativeLinkNames);

            foreach (var nested in declared.NestedClasses)
                CollectNativeLinkNames(nested, nativeLinkNames);
        }

        private static void CollectNativeLinkNames(SurtrMethodInfo[] overloads, List<string> nativeLinkNames)
        {
            for (int i = 0; i < overloads.Length; i++)
            {
                if (overloads[i] is SurtrNativeMethodInfo native)
                    nativeLinkNames.Add(native.LinkName);
            }
        }
    }
}
