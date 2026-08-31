#nullable enable

using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Compilation
{
    /// <summary>
    /// Where a module's source text comes from. The compiler used to read only from the
    /// filesystem (via <see cref="SurtrBuild"/>), which is fine for a CLI but wrong for an
    /// embedding host — a Unity asset database is not a directory walk, and a host may hold
    /// source in memory, in a database, or behind a network call. This is the seam that lets a
    /// host supply its own source without the compiler caring where it lives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The provider is keyed by <em>module path</em> (§2.1), not by file path: a module is a
    /// dotted path, and the provider answers "given <c>Ogame.core.Entity</c>, give me the text".
    /// This is deliberately the same key the rest of the compiler reasons in, so a host never has
    /// to translate a module name into a location of its own.
    /// </para>
    /// <para>
    /// A provider that returns <see langword="false"/> is not an error by itself: resolution
    /// falls through to the next provider, and only when no provider knows the module does the
    /// import become an <c>UnresolvedImport</c> diagnostic. The same module may be split across
    /// providers, but the first provider that knows a module wins.
    /// </para>
    /// </remarks>
    public interface ISourceProvider
    {
        /// <summary>
        /// Asks this provider for the source of a module.
        /// </summary>
        /// <param name="modulePath">The dotted module path to resolve (§2.1).</param>
        /// <param name="text">The module's source text when the provider knows it.</param>
        /// <param name="diagnosticPath">
        /// A path to hang diagnostics on when the module is compiled — a file path for a
        /// filesystem-backed provider, or any stable identifier for a custom one.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when this provider supplied the module, <see langword="false"/>
        /// when it does not know it.
        /// </returns>
        bool TryGetSource(string modulePath, out string text, out string diagnosticPath);
    }

    /// <summary>
    /// The <see cref="ISourceProvider"/> for a filesystem-backed source tree — the one
    /// <see cref="SurtrBuild"/> uses, and the default for a <see cref="SurtrProject"/> that does
    /// not name another.
    /// </summary>
    /// <remarks>
    /// It is the reverse of <see cref="ModulePath.TryDerive"/>: given a module path it derives the
    /// file that would hold it and reads it. A module whose path contains a segment that is not a
    /// legal identifier can never be resolved here, which is consistent with §2.1 never letting
    /// such a module be named by an <c>import</c> in the first place.
    /// </remarks>
    public sealed class FileSystemSourceProvider : ISourceProvider
    {
        private readonly string _sourceRoot;
        private readonly string _rootModulePath;

        /// <summary>Creates a provider that reads files under <paramref name="sourceRoot"/>.</summary>
        /// <param name="sourceRoot">The directory module paths are derived relative to.</param>
        /// <param name="rootModulePath">What the source root itself is called, prefixed onto every derived module path.</param>
        public FileSystemSourceProvider(string sourceRoot, string rootModulePath = "")
        {
            _sourceRoot = sourceRoot ?? throw new ArgumentNullException(nameof(sourceRoot));
            _rootModulePath = rootModulePath ?? string.Empty;
        }

        /// <inheritdoc/>
        public bool TryGetSource(string modulePath, out string text, out string diagnosticPath)
        {
            // Reconstruct the file's path from its module segments (§2.1): each segment is a
            // directory except the last, which is the file name, and the root-module prefix
            // becomes the directory above the segments.
            string? relative = ModulePath.TryToFile(_rootModulePath, modulePath);
            if (relative is null)
            {
                text = string.Empty;
                diagnosticPath = string.Empty;
                return false;
            }

            string file = System.IO.Path.Combine(_sourceRoot, relative);
            if (!System.IO.File.Exists(file))
            {
                text = string.Empty;
                diagnosticPath = string.Empty;
                return false;
            }

            text = System.IO.File.ReadAllText(file);
            diagnosticPath = file;
            return true;
        }
    }

    /// <summary>
    /// Chains several providers in order: the first one that knows a module wins, and one that
    /// answers <see langword="false"/> is not an error, exactly as <see cref="ISourceProvider"/>'s
    /// own remarks already describe - this is what actually implements chaining several of them
    /// together, rather than every host writing that loop by hand.
    /// </summary>
    public sealed class CompositeSourceProvider : ISourceProvider
    {
        private readonly ISourceProvider[] _providers;

        /// <summary>Creates a provider that tries each of <paramref name="providers"/> in order.</summary>
        public CompositeSourceProvider(params ISourceProvider[] providers)
        {
            _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        }

        /// <inheritdoc/>
        public bool TryGetSource(string modulePath, out string text, out string diagnosticPath)
        {
            foreach (var provider in _providers)
            {
                if (provider.TryGetSource(modulePath, out text, out diagnosticPath))
                    return true;
            }

            text = string.Empty;
            diagnosticPath = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// A pure in-memory <see cref="ISourceProvider"/>: a fixed map of module path to source text,
    /// for a host that already has its scripts as data (a manifest, a database row, an asset
    /// already loaded) and wants lazy import resolution with nothing filesystem-shaped in between.
    /// </summary>
    public sealed class DictionarySourceProvider : ISourceProvider
    {
        private readonly IReadOnlyDictionary<string, string> _sources;

        /// <summary>Creates a provider backed by a fixed module-path-to-source-text map.</summary>
        public DictionarySourceProvider(IReadOnlyDictionary<string, string> sources)
        {
            _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        }

        /// <inheritdoc/>
        public bool TryGetSource(string modulePath, out string text, out string diagnosticPath)
        {
            if (_sources.TryGetValue(modulePath, out text!))
            {
                diagnosticPath = modulePath;
                return true;
            }

            text = string.Empty;
            diagnosticPath = string.Empty;
            return false;
        }
    }
}
