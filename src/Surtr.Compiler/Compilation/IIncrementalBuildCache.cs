#nullable enable

using Surtr.Bytecode.Image;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Compilation
{
    /// <summary>
    /// Where <see cref="SurtrIncrementalBuild"/> keeps a compiled module between builds, so an
    /// unchanged one is reused instead of recompiled.
    /// </summary>
    /// <remarks>
    /// Deliberately pluggable rather than a fixed implementation: a long-lived embedding host (a
    /// script console, a game server recompiling one script at a time) wants a cache that survives
    /// as long as the process does, which <see cref="InMemoryIncrementalBuildCache"/> already is;
    /// a build tool invoked once per process wants one backed by disk, which is the host's own
    /// question to answer - nothing about the incremental algorithm cares where an entry lives.
    /// </remarks>
    public interface IIncrementalBuildCache
    {
        /// <summary>The image and content hash last stored for a module, if any.</summary>
        /// <param name="modulePath">The module's dotted path.</param>
        /// <param name="contentHash">The hash of the source text the stored image was built from.</param>
        /// <param name="image">The image itself.</param>
        /// <returns><see langword="true"/> if the cache holds an entry for this module.</returns>
        bool TryGet(string modulePath, out string contentHash, out SurtrModuleImage image);

        /// <summary>Records (or replaces) the image built for a module and the hash it was built from.</summary>
        void Store(string modulePath, string contentHash, SurtrModuleImage image);
    }

    /// <summary>
    /// The default <see cref="IIncrementalBuildCache"/>: a plain in-memory map, alive for as long as
    /// the process holds a reference to it.
    /// </summary>
    public sealed class InMemoryIncrementalBuildCache : IIncrementalBuildCache
    {
        private readonly Dictionary<string, (string Hash, SurtrModuleImage Image)> _entries =
            new Dictionary<string, (string Hash, SurtrModuleImage Image)>(StringComparer.Ordinal);

        /// <inheritdoc/>
        public bool TryGet(string modulePath, out string contentHash, out SurtrModuleImage image)
        {
            if (_entries.TryGetValue(modulePath, out var entry))
            {
                contentHash = entry.Hash;
                image = entry.Image;
                return true;
            }

            contentHash = string.Empty;
            image = null!;
            return false;
        }

        /// <inheritdoc/>
        public void Store(string modulePath, string contentHash, SurtrModuleImage image)
        {
            if (modulePath is null)
                throw new ArgumentNullException(nameof(modulePath));

            if (contentHash is null)
                throw new ArgumentNullException(nameof(contentHash));

            if (image is null)
                throw new ArgumentNullException(nameof(image));

            _entries[modulePath] = (contentHash, image);
        }
    }
}
