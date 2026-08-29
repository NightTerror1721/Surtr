#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Diagnostics;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Surtr.Compiler.Compilation
{
    /// <summary>
    /// Compiles a set of module sources, reusing whatever a cache already holds for the modules
    /// that did not change - and everything that does not (transitively) depend on one that did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The question <c>SurtrBuild</c> deliberately leaves to the host ("nothing here caches,
    /// watches or builds incrementally"). This answers it without touching the compiler's core
    /// invariant that <see cref="SurtrCompilation.Bind"/> is a one-shot, monolithic pass over
    /// whatever <see cref="SurtrProject"/> it was given - nothing here rebinds part of a
    /// compilation. Instead it runs the compiler <em>twice</em>, both times through the ordinary
    /// public API:
    /// </para>
    /// <list type="number">
    /// <item>A throwaway <see cref="SurtrCompilation"/> over every source given - parsed, not
    /// bound - just to read <see cref="SurtrCompilation.Dependencies"/>, the import graph. Parsing
    /// is cheap; this pass never binds or emits anything.</item>
    /// <item>The dirty set: any module whose content hash does not match what the cache has, plus
    /// - via <see cref="ModuleDependencyGraph.Dependents"/> - every module that (transitively)
    /// depends on one. A dependency's signature can change what a caller binds against even when
    /// the caller's own text did not, so this is deliberately conservative rather than assuming a
    /// dependent is safe.</item>
    /// <item>A real <see cref="SurtrCompilation"/> containing only the dirty modules as source; every
    /// clean module is handed in as an already-built reference
    /// (<see cref="SurtrProject.AddReference(SurtrModuleImage)"/>) instantiated straight from the
    /// cache, so it is never re-parsed, re-bound or re-emitted.</item>
    /// </list>
    /// <para>
    /// For an embedded-scripting host recompiling one script whose module nothing else imports,
    /// this makes the second pass compile exactly that one module - the rest of the set costs a
    /// cache lookup each, not a rebuild.
    /// </para>
    /// </remarks>
    public static class SurtrIncrementalBuild
    {
        /// <summary>
        /// Compiles <paramref name="sources"/>, recompiling only what changed (or depends on what
        /// changed) since the last call against <paramref name="cache"/>.
        /// </summary>
        /// <param name="sources">Every module's path and current source text. A module not named here is not part of this build.</param>
        /// <param name="cache">Where compiled modules are looked up and stored between calls.</param>
        /// <param name="constants">The constants this build defines (§7.4), applied to the dirty subset like any other build.</param>
        /// <param name="diagnostics">Where problems are recorded. A fresh bag is used if none is given.</param>
        /// <param name="externalReferences">
        /// Already-compiled images this build depends on but does not itself own or cache - a
        /// separately built library, referenced the same way a <c>reference</c> directive would.
        /// Added to every pass unconditionally; nothing here tracks their content for invalidation.
        /// </param>
        /// <returns>Every module's image - freshly compiled or reused - in no particular order. Empty if anything failed.</returns>
        public static IReadOnlyList<SurtrModuleImage> Run(
            IReadOnlyList<(string ModulePath, string Text)> sources,
            IIncrementalBuildCache cache,
            IReadOnlyDictionary<string, BuildConstant>? constants = null,
            SurtrDiagnosticBag? diagnostics = null,
            IReadOnlyList<SurtrModuleImage>? externalReferences = null)
        {
            if (sources is null)
                throw new ArgumentNullException(nameof(sources));

            if (cache is null)
                throw new ArgumentNullException(nameof(cache));

            diagnostics ??= new SurtrDiagnosticBag();

            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (modulePath, text) in sources)
                hashes[modulePath] = ComputeHash(text);

            var dependencyGraph = DiscoverDependencyGraph(sources, externalReferences, diagnostics);
            if (diagnostics.HasErrors)
                return Array.Empty<SurtrModuleImage>();

            var dirty = DetermineDirtySet(sources, hashes, cache, dependencyGraph);

            var project = new SurtrProject(sourceRoot: ".");
            var reused = new List<SurtrModuleImage>();

            foreach (var (modulePath, text) in sources)
            {
                if (dirty.Contains(modulePath))
                {
                    project.AddSourceFile(modulePath, modulePath, text);
                }
                else if (cache.TryGet(modulePath, out _, out var cachedImage))
                {
                    project.AddReference(cachedImage);
                    reused.Add(cachedImage);
                }
            }

            foreach (var reference in externalReferences ?? Array.Empty<SurtrModuleImage>())
                project.AddReference(reference);

            if (constants is not null)
            {
                foreach (var constant in constants)
                    project.Define(constant.Key, constant.Value);
            }

            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            var emitter = new ModuleEmitter(compilation, binder);
            var freshImages = emitter.EmitImages();

            diagnostics.AddRange(compilation.Diagnostics);

            if (diagnostics.HasErrors)
                return Array.Empty<SurtrModuleImage>();

            var result = new List<SurtrModuleImage>(freshImages.Count + reused.Count);
            for (int i = 0; i < freshImages.Count; i++)
            {
                var image = freshImages[i];
                cache.Store(image.Path, hashes[image.Path], image);
                result.Add(image);
            }

            result.AddRange(reused);
            return result;
        }

        /// <summary>
        /// Parses every source into a throwaway compilation solely to read its import graph -
        /// never bound, never emitted.
        /// </summary>
        private static ModuleDependencyGraph DiscoverDependencyGraph(
            IReadOnlyList<(string ModulePath, string Text)> sources,
            IReadOnlyList<SurtrModuleImage>? externalReferences,
            SurtrDiagnosticBag diagnostics)
        {
            var project = new SurtrProject(sourceRoot: ".");
            foreach (var (modulePath, text) in sources)
                project.AddSourceFile(modulePath, modulePath, text);

            foreach (var reference in externalReferences ?? Array.Empty<SurtrModuleImage>())
                project.AddReference(reference);

            using var compilation = SurtrCompilation.Create(project);
            diagnostics.AddRange(compilation.Diagnostics);
            return compilation.Dependencies;
        }

        /// <summary>
        /// Every module whose content changed, plus every module that transitively depends on one
        /// that did - the conservative invalidation rule: a dependency's signature can change what
        /// a dependent binds against even when the dependent's own text is untouched.
        /// </summary>
        private static HashSet<string> DetermineDirtySet(
            IReadOnlyList<(string ModulePath, string Text)> sources,
            IReadOnlyDictionary<string, string> hashes,
            IIncrementalBuildCache cache,
            ModuleDependencyGraph dependencyGraph)
        {
            var dirty = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();

            foreach (var (modulePath, _) in sources)
            {
                bool unchanged = cache.TryGet(modulePath, out string cachedHash, out _)
                    && string.Equals(cachedHash, hashes[modulePath], StringComparison.Ordinal);

                if (!unchanged && dirty.Add(modulePath))
                    queue.Enqueue(modulePath);
            }

            while (queue.Count > 0)
            {
                string modulePath = queue.Dequeue();

                foreach (string dependent in dependencyGraph.Dependents(modulePath))
                {
                    if (dirty.Add(dependent))
                        queue.Enqueue(dependent);
                }
            }

            return dirty;
        }

        private static string ComputeHash(string text)
        {
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));

            // BitConverter rather than Convert.ToHexString: this targets netstandard2.1, and
            // ToHexString is a .NET 5+ addition.
            return BitConverter.ToString(hash).Replace("-", string.Empty);
        }
    }
}
