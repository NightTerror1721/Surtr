#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Surtr.LanguageServer.Workspace
{
    /// <summary>
    /// The server's whole state: which files the workspace holds, which of them are open in the
    /// editor with unsaved text, and the current <see cref="CompilationSnapshot"/> those files were
    /// last compiled into. Rebuilding is deliberately whole-workspace — the compiler has no
    /// incremental path, and a wrong incremental answer is worse than a slow one.
    /// </summary>
    public sealed class Workspace
    {
        private readonly string _rootPath;
        private readonly string _rootModulePath;
        private readonly Dictionary<string, string> _openDocuments = new Dictionary<string, string>(PathComparer);

        private CompilationSnapshot _snapshot = CompilationSnapshot.Empty;

        public Workspace(string rootPath, string rootModulePath = "")
        {
            _rootPath = rootPath;
            _rootModulePath = rootModulePath ?? string.Empty;
        }

        /// <summary>The directory the compilation's module paths are derived from.</summary>
        public string RootPath => _rootPath;

        /// <summary>What the source root itself is called, prefixed onto every derived module path.</summary>
        public string RootModulePath => _rootModulePath;

        /// <summary>The most recent compilation, valid after the first <see cref="Rebuild"/>.</summary>
        public CompilationSnapshot Snapshot => _snapshot;

        /// <summary>Records the text an editor holds for an open document.</summary>
        public void SetOpenDocument(string uri, string text)
        {
            _openDocuments[PathFromUri(uri)] = text ?? throw new ArgumentNullException(nameof(text));
        }

        /// <summary>Forgets an editor document, so its on-disk text is used again.</summary>
        public void CloseDocument(string uri) => _openDocuments.Remove(PathFromUri(uri));

        /// <summary>The text of a file: its open buffer when there is one, otherwise its on-disk contents.</summary>
        public string CurrentText(string path)
        {
            if (_openDocuments.TryGetValue(path, out string? open))
                return open;

            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }

        /// <summary>
        /// Recompiles every <c>.surtr</c> file under the root, swapping the snapshot and returning
        /// the diagnostics the compilation produced, keyed by file path. The previous compilation is
        /// disposed once the new one is in place.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<Surtr.Compiler.Diagnostics.SurtrDiagnostic>> Rebuild()
        {
            var files = FindSourceFiles().ToList();

            Surtr.Compiler.Compilation.SurtrProject project =
                new Surtr.Compiler.Compilation.SurtrProject(_rootPath, _rootModulePath);

            foreach (string file in files)
                project.AddSourceFile(file, CurrentText(file));

            if (files.Count == 0)
            {
                _snapshot.Dispose();
                _snapshot = CompilationSnapshot.Empty;
                return new Dictionary<string, IReadOnlyList<Surtr.Compiler.Diagnostics.SurtrDiagnostic>>();
            }

            Surtr.Compiler.Compilation.SurtrCompilation? compilation = null;
            Surtr.Compiler.Binding.Binder? binder = null;
            var diagnostics = new List<Surtr.Compiler.Diagnostics.SurtrDiagnostic>();

            try
            {
                compilation = Surtr.Compiler.Compilation.SurtrCompilation.Create(project);
                binder = compilation.Bind();
                binder.BindBodies();

                foreach (var diagnostic in compilation.Diagnostics)
                    diagnostics.Add(diagnostic);
            }
            catch (Surtr.Compiler.Diagnostics.SurtrCompilerException ex)
            {
                // The compiler reports by collecting rather than throwing, but a hand-written file
                // can still push a boundary it guards with an exception; surface it as a diagnostic
                // rather than crashing the server.
                diagnostics.Add(new Surtr.Compiler.Diagnostics.SurtrDiagnostic(
                    Surtr.Compiler.Diagnostics.SurtrDiagnosticCode.AmbiguousName,
                    Surtr.Compiler.Diagnostics.SurtrDiagnosticSeverity.Error,
                    "The compiler failed on this workspace: " + ex.Message,
                    Path.GetFullPath(_rootPath),
                    new Surtr.Compiler.Syntax.SourceSpan(
                        new Surtr.Compiler.Syntax.SourceLocation(1, 1, 0), 0)));
            }
            catch (Exception ex)
            {
                diagnostics.Add(new Surtr.Compiler.Diagnostics.SurtrDiagnostic(
                    Surtr.Compiler.Diagnostics.SurtrDiagnosticCode.AmbiguousName,
                    Surtr.Compiler.Diagnostics.SurtrDiagnosticSeverity.Error,
                    "The language server could not compile this workspace: " + ex.Message,
                    Path.GetFullPath(_rootPath),
                    new Surtr.Compiler.Syntax.SourceSpan(
                        new Surtr.Compiler.Syntax.SourceLocation(1, 1, 0), 0)));
            }

            var snapshot = new CompilationSnapshot(compilation, binder, diagnostics);
            _snapshot.Dispose();
            _snapshot = snapshot;

            return GroupDiagnostics(diagnostics);
        }

        /// <summary>Groups a compilation's diagnostics by the file they name.</summary>
        public static IReadOnlyDictionary<string, IReadOnlyList<Surtr.Compiler.Diagnostics.SurtrDiagnostic>> GroupDiagnostics(
            IReadOnlyList<Surtr.Compiler.Diagnostics.SurtrDiagnostic> diagnostics)
        {
            var groups = new Dictionary<string, IReadOnlyList<Surtr.Compiler.Diagnostics.SurtrDiagnostic>>(PathComparer);

            foreach (var diagnostic in diagnostics)
            {
                string sourceName = diagnostic.SourceName;
                if (string.IsNullOrEmpty(sourceName))
                    continue;

                if (!groups.TryGetValue(sourceName, out var bucket))
                {
                    var list = new List<Surtr.Compiler.Diagnostics.SurtrDiagnostic>();
                    groups[sourceName] = list;
                    bucket = list;
                }

                ((List<Surtr.Compiler.Diagnostics.SurtrDiagnostic>)bucket).Add(diagnostic);
            }

            foreach (var pair in groups.ToList())
                groups[pair.Key] = ((List<Surtr.Compiler.Diagnostics.SurtrDiagnostic>)pair.Value).OrderBy(d => d.Span.Start.Position).ToList();

            return groups;
        }

        /// <summary>Every <c>.surtr</c> file under the root, skipping build and version-control noise.</summary>
        public IEnumerable<string> FindSourceFiles()
        {
            if (!Directory.Exists(_rootPath))
                yield break;

            foreach (string file in Directory.EnumerateFiles(_rootPath, "*.surtr", SearchOption.AllDirectories))
            {
                string normalized = Path.GetFullPath(file);
                if (IsIgnored(normalized))
                    continue;

                yield return normalized;
            }
        }

        private static bool IsIgnored(string path)
        {
            string[] segments = path.Split(Path.DirectorySeparatorChar);
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                if (segment is "obj" or "bin" or ".git" or ".vs" or "node_modules")
                    return true;
            }

            return false;
        }

        /// <summary>Converts a <c>file://</c> URI (or a plain path) to a local path.</summary>
        /// <remarks>
        /// An editor percent-encodes the drive's colon — VSCode sends <c>file:///d%3A/Projects</c> —
        /// and that stops <see cref="Uri"/> from recognizing a DOS path, so <c>LocalPath</c> hands
        /// back <c>/d:/Projects</c> with the URI's leading slash still on it. Resolving that would
        /// name a directory on whichever drive the server happens to be running from, and the
        /// workspace would then find no source files at all rather than fail: every feature goes
        /// quiet at once, with nothing reported anywhere. The slash is dropped here instead.
        /// </remarks>
        public static string PathFromUri(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return string.Empty;

            string path = Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile
                ? parsed.LocalPath
                : uri;

            if (path.Length >= 3
                && (path[0] == '/' || path[0] == '\\')
                && char.IsLetter(path[1])
                && path[2] == ':')
            {
                path = path.Substring(1);
            }

            return path.Length == 0 ? string.Empty : Path.GetFullPath(path);
        }

        /// <summary>Converts a local path to the <c>file://</c> URI LSP uses.</summary>
        public static string UriFromPath(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

        /// <summary>Releases the current compilation's resources.</summary>
        public void Dispose() => _snapshot.Dispose();

        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    }
}
