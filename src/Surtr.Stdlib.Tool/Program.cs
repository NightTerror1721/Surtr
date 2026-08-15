#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Bytecode.Image;
using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
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

            foreach (string relative in sources)
            {
                if (!BuildOne(sourceRoot, relative, outputDirectory, disassemblyDirectory, out string diagnostics))
                {
                    Console.Error.WriteLine("Stdlib build failed for " + ModuleOf(relative) + ":");
                    Console.Error.WriteLine(diagnostics);
                    return 1;
                }
            }

            Console.WriteLine("Wrote " + sources.Count + " stdlib image(s) to " + outputDirectory);
            Console.WriteLine("Wrote " + sources.Count + " disassembly(ies) to " + disassemblyDirectory);
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
        /// Compiles one source file into a <c>.surtrc</c> image written to <paramref name="outputDirectory"/>,
        /// plus a disassembled text rendering written to <paramref name="disassemblyDirectory"/>.
        /// </summary>
        private static bool BuildOne(string sourceRoot, string relative, string outputDirectory, string disassemblyDirectory, out string diagnostics)
        {
            string modulePath = ModuleOf(relative);

            // The file's directory is the project's source root, and the full module path is the
            // root module path, so §2.1's derivation reads exactly the name we computed: a file at
            // its own source root belongs to whatever the root module path says, no synthetic
            // directories needed to fold the file name in as a segment.
            string realFile = Path.Combine(sourceRoot, ModulePrefix, relative.Replace('/', Path.DirectorySeparatorChar));
            var project = new SurtrProject(Path.GetDirectoryName(realFile)!, rootModulePath: modulePath);
            project.AddSourceFile(realFile, File.ReadAllText(realFile));

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

            string path = Path.Combine(outputDirectory, modulePath + SurtrModuleImage.FileExtension);
            File.WriteAllBytes(path, images[0].ToBytes());

            // The disassembler reads an emitter-built module (the only form whose name tables are
            // populated - it cannot render a module re-instantiated from image bytes), and the
            // emitter module is exactly what just got serialized into the image above, so this is
            // the human-checkable view of precisely what was compiled.
            string disasm = SurtrBytecodeDisassembler.Disassemble(emitter.Modules[0]);
            string disasmPath = Path.Combine(disassemblyDirectory, modulePath + ".txt");
            File.WriteAllText(disasmPath, disasm);

            diagnostics = bag.ToString();
            return true;
        }
    }
}
