#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;

namespace Surtr.Compiler.Compilation
{
    /// <summary>
    /// One build: a directory of <c>.surtr</c> files in, a directory of <c>.surtrc</c> images out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The piece §14.2 called the build model, and it is deliberately thin. Everything it does is
    /// already available piecewise — <see cref="SurtrProject"/> takes files, <see cref="Binder"/>
    /// binds them, <see cref="ModuleEmitter"/> emits images — and what was missing was only the
    /// part that touches the file system: finding the sources, and writing what came out.
    /// </para>
    /// <para>
    /// It stays a <em>convenience</em> over the in-memory API rather than the way in. A host
    /// embedding the compiler has its own idea of where source lives — a Unity asset database is
    /// not a directory walk — and building a <see cref="SurtrProject"/> by hand is still the
    /// primary path. That is also why nothing here caches, watches or does incremental work: those
    /// are the host's questions, and answering them badly here would be worse than not answering.
    /// </para>
    /// </remarks>
    public sealed class SurtrBuild
    {
        private SurtrBuild(SurtrDiagnosticBag diagnostics, IReadOnlyList<string> written)
        {
            Diagnostics = diagnostics;
            Written = written;
        }

        /// <summary>Everything the build found wrong, from every stage.</summary>
        public SurtrDiagnosticBag Diagnostics { get; }

        /// <summary>The images written, in load order.</summary>
        public IReadOnlyList<string> Written { get; }

        /// <summary>Whether anything stopped the build producing images.</summary>
        public bool Failed => Diagnostics.HasErrors;

        /// <summary>Builds the project a project file describes.</summary>
        /// <param name="projectFilePath">The <c>.surtrproj</c> to read.</param>
        public static SurtrBuild Run(string projectFilePath)
        {
            var diagnostics = new SurtrDiagnosticBag();
            var file = SurtrProjectFile.Read(projectFilePath, diagnostics);

            if (diagnostics.HasErrors)
                return new SurtrBuild(diagnostics, Array.Empty<string>());

            string root = Path.GetFullPath(Path.Combine(file.Directory, file.Root));
            string output = Path.GetFullPath(Path.Combine(file.Directory, file.Output));

            return Run(
                root, output, file.RootModulePath, file.References, file.Constants, diagnostics, file.Directory,
                file.WarningsAsErrors, file.SuppressedCodes);
        }

        /// <summary>Builds a source tree with no project file, taking every setting as given.</summary>
        public static SurtrBuild Run(
            string sourceRoot,
            string outputDirectory,
            string rootModulePath = "",
            IReadOnlyList<string>? references = null,
            IReadOnlyDictionary<string, BuildConstant>? constants = null,
            SurtrDiagnosticBag? diagnostics = null,
            string? referenceBase = null,
            bool warningsAsErrors = false,
            IReadOnlyCollection<SurtrDiagnosticCode>? suppressedCodes = null)
        {
            diagnostics ??= new SurtrDiagnosticBag();

            if (!Directory.Exists(sourceRoot))
            {
                diagnostics.ReportError(
                    SurtrDiagnosticCode.ProjectFileInvalid,
                    $"There is no source directory at '{sourceRoot}'.",
                    sourceRoot,
                    span: default);

                return new SurtrBuild(diagnostics, Array.Empty<string>());
            }

            var project = new SurtrProject(sourceRoot, rootModulePath);

            // Sorted, so two builds of one tree produce their modules in the same order and a
            // diagnostic list is comparable between runs.
            var sources = new List<string>(Directory.GetFiles(sourceRoot, "*.surtr", SearchOption.AllDirectories));
            sources.Sort(StringComparer.Ordinal);

            if (sources.Count == 0)
            {
                diagnostics.ReportWarning(
                    SurtrDiagnosticCode.ProjectFileInvalid,
                    $"'{sourceRoot}' holds no .surtr files.",
                    sourceRoot,
                    span: default);
            }

            foreach (string source in sources)
                project.AddSourceFile(source, File.ReadAllText(source));

            foreach (string reference in references ?? Array.Empty<string>())
            {
                string path = Path.GetFullPath(Path.Combine(referenceBase ?? ".", reference));

                if (!File.Exists(path))
                {
                    diagnostics.ReportError(
                        SurtrDiagnosticCode.ProjectFileInvalid,
                        $"There is no image to reference at '{path}'.",
                        path,
                        span: default);

                    continue;
                }

                project.AddReference(SurtrModuleImage.FromBytes(File.ReadAllBytes(path)));
            }

            if (constants is not null)
            {
                foreach (var constant in constants)
                    project.Define(constant.Key, constant.Value);
            }

            return Compile(project, output: outputDirectory, diagnostics, warningsAsErrors, suppressedCodes);
        }

        /// <summary>
        /// Builds the project a project file describes, reusing whatever <paramref name="cache"/>
        /// already holds for any module whose source (and everything it depends on) is unchanged
        /// since the last call - see <see cref="SurtrIncrementalBuild"/>.
        /// </summary>
        /// <param name="projectFilePath">The <c>.surtrproj</c> to read.</param>
        /// <param name="cache">Where compiled modules are looked up and stored between calls.</param>
        public static SurtrBuild RunIncremental(string projectFilePath, IIncrementalBuildCache cache)
        {
            var diagnostics = new SurtrDiagnosticBag();
            var file = SurtrProjectFile.Read(projectFilePath, diagnostics);

            if (diagnostics.HasErrors)
                return new SurtrBuild(diagnostics, Array.Empty<string>());

            string root = Path.GetFullPath(Path.Combine(file.Directory, file.Root));
            string output = Path.GetFullPath(Path.Combine(file.Directory, file.Output));

            return RunIncremental(
                root, output, cache, file.RootModulePath, file.References, file.Constants, diagnostics, file.Directory,
                file.WarningsAsErrors, file.SuppressedCodes);
        }

        /// <summary>
        /// <see cref="RunIncremental(string, IIncrementalBuildCache)"/> over a source tree with no
        /// project file, taking every setting as given.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="Run(string, string, string, IReadOnlyList{string}, IReadOnlyDictionary{string, BuildConstant}, SurtrDiagnosticBag, string, bool, IReadOnlyCollection{SurtrDiagnosticCode})"/>,
        /// <see cref="Written"/>'s order here is not necessarily load order - freshly recompiled
        /// modules come first, reused ones after, in no particular order among themselves. A host
        /// loading them should resolve by name (<c>SurtrRuntime.LoadModule</c>'s own retry, or
        /// <c>ModuleSet.Load</c>'s fixed-point loop) rather than assume this list is topologically
        /// sorted.
        /// </remarks>
        public static SurtrBuild RunIncremental(
            string sourceRoot,
            string outputDirectory,
            IIncrementalBuildCache cache,
            string rootModulePath = "",
            IReadOnlyList<string>? references = null,
            IReadOnlyDictionary<string, BuildConstant>? constants = null,
            SurtrDiagnosticBag? diagnostics = null,
            string? referenceBase = null,
            bool warningsAsErrors = false,
            IReadOnlyCollection<SurtrDiagnosticCode>? suppressedCodes = null)
        {
            if (cache is null)
                throw new ArgumentNullException(nameof(cache));

            diagnostics ??= new SurtrDiagnosticBag();

            if (!Directory.Exists(sourceRoot))
            {
                diagnostics.ReportError(
                    SurtrDiagnosticCode.ProjectFileInvalid,
                    $"There is no source directory at '{sourceRoot}'.",
                    sourceRoot,
                    span: default);

                return new SurtrBuild(diagnostics, Array.Empty<string>());
            }

            var files = new List<string>(Directory.GetFiles(sourceRoot, "*.surtr", SearchOption.AllDirectories));
            files.Sort(StringComparer.Ordinal);

            if (files.Count == 0)
            {
                diagnostics.ReportWarning(
                    SurtrDiagnosticCode.ProjectFileInvalid,
                    $"'{sourceRoot}' holds no .surtr files.",
                    sourceRoot,
                    span: default);
            }

            var sources = new List<(string ModulePath, string Text)>(files.Count);
            foreach (string filePath in files)
            {
                var status = ModulePath.TryDerive(sourceRoot, filePath, rootModulePath, out string modulePath, out string offendingSegment);

                if (status != ModulePathStatus.Ok)
                {
                    string problem = status == ModulePathStatus.InvalidSegment
                        ? $"'{offendingSegment}' in '{filePath}' is not a legal Surtr identifier."
                        : $"'{filePath}' is outside the source root '{sourceRoot}'.";

                    diagnostics.ReportError(SurtrDiagnosticCode.ProjectFileInvalid, problem, filePath, span: default);
                    continue;
                }

                sources.Add((modulePath, File.ReadAllText(filePath)));
            }

            var externalReferences = new List<SurtrModuleImage>();
            foreach (string reference in references ?? Array.Empty<string>())
            {
                string path = Path.GetFullPath(Path.Combine(referenceBase ?? ".", reference));

                if (!File.Exists(path))
                {
                    diagnostics.ReportError(
                        SurtrDiagnosticCode.ProjectFileInvalid,
                        $"There is no image to reference at '{path}'.",
                        path,
                        span: default);

                    continue;
                }

                externalReferences.Add(SurtrModuleImage.FromBytes(File.ReadAllBytes(path)));
            }

            if (diagnostics.HasErrors)
                return new SurtrBuild(diagnostics, Array.Empty<string>());

            var images = SurtrIncrementalBuild.Run(sources, cache, constants, diagnostics, externalReferences);

            if (warningsAsErrors || (suppressedCodes is not null && suppressedCodes.Count > 0))
                diagnostics = diagnostics.ApplyPolicy(warningsAsErrors, suppressedCodes);

            if (diagnostics.HasErrors)
                return new SurtrBuild(diagnostics, Array.Empty<string>());

            return new SurtrBuild(diagnostics, WriteImages(images, outputDirectory));
        }

        private static SurtrBuild Compile(
            SurtrProject project,
            string output,
            SurtrDiagnosticBag diagnostics,
            bool warningsAsErrors = false,
            IReadOnlyCollection<SurtrDiagnosticCode>? suppressedCodes = null)
        {
            using var compilation = SurtrCompilation.Create(project);

            var binder = compilation.Bind();
            binder.BindBodies();

            var emitter = new ModuleEmitter(compilation, binder);
            var images = emitter.EmitImages();

            diagnostics.AddRange(compilation.Diagnostics);

            // Applied once, over the whole bag (the project file's own validation diagnostics plus
            // everything the compilation reported) rather than per-source: a suppressed code or a
            // promoted warning means the same thing regardless of which stage produced it.
            if (warningsAsErrors || (suppressedCodes is not null && suppressedCodes.Count > 0))
                diagnostics = diagnostics.ApplyPolicy(warningsAsErrors, suppressedCodes);

            if (diagnostics.HasErrors)
                return new SurtrBuild(diagnostics, Array.Empty<string>());

            return new SurtrBuild(diagnostics, WriteImages(images, output));
        }

        /// <summary>
        /// Writes every image to <paramref name="output"/>, named after its own module path - the
        /// one name that identifies an image regardless of which of the module's own source
        /// file(s) it was built from (a module is a file, §2.1, so in practice always exactly one).
        /// </summary>
        private static List<string> WriteImages(IReadOnlyList<SurtrModuleImage> images, string output)
        {
            Directory.CreateDirectory(output);

            var written = new List<string>(images.Count);
            for (int i = 0; i < images.Count; i++)
            {
                string path = Path.Combine(output, images[i].Path + ".surtrc");
                File.WriteAllBytes(path, images[i].ToBytes());
                written.Add(path);
            }

            return written;
        }
    }
}
