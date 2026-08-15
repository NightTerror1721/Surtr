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

            return Run(root, output, file.RootModulePath, file.References, file.Constants, diagnostics, file.Directory);
        }

        /// <summary>Builds a source tree with no project file, taking every setting as given.</summary>
        public static SurtrBuild Run(
            string sourceRoot,
            string outputDirectory,
            string rootModulePath = "",
            IReadOnlyList<string>? references = null,
            IReadOnlyDictionary<string, BuildConstant>? constants = null,
            SurtrDiagnosticBag? diagnostics = null,
            string? referenceBase = null)
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

            return Compile(project, output: outputDirectory, diagnostics);
        }

        private static SurtrBuild Compile(SurtrProject project, string output, SurtrDiagnosticBag diagnostics)
        {
            using var compilation = SurtrCompilation.Create(project);

            var binder = compilation.Bind();
            binder.BindBodies();

            var emitter = new ModuleEmitter(compilation, binder);
            var images = emitter.EmitImages();

            diagnostics.AddRange(compilation.Diagnostics);

            if (diagnostics.HasErrors)
                return new SurtrBuild(diagnostics, Array.Empty<string>());

            Directory.CreateDirectory(output);

            var written = new List<string>(images.Count);
            for (int i = 0; i < images.Count; i++)
            {
                // Named after the module rather than after any file: §2.1 makes a module a
                // directory, so several files produce one image and the module path is the only
                // name that identifies it.
                string path = Path.Combine(output, emitter.Modules[i].Path + ".surtrc");

                File.WriteAllBytes(path, images[i].ToBytes());
                written.Add(path);
            }

            return new SurtrBuild(diagnostics, written);
        }
    }
}
