#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Compiler.Compilation;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Tests.Compiler.Compilation
{
    /// <summary>
    /// Covers <see cref="SurtrIncrementalBuild"/> and <see cref="ModuleDependencyGraph.Dependents"/>:
    /// an unchanged module is reused rather than recompiled, a changed leaf recompiles alone, and a
    /// changed dependency also recompiles everything that (transitively) depends on it.
    /// </summary>
    public sealed class SurtrIncrementalBuildTests
    {
        /// <summary>Wraps <see cref="InMemoryIncrementalBuildCache"/> to record which modules a build actually recompiled (<see cref="Store"/> only runs for the dirty set).</summary>
        private sealed class CountingCache : IIncrementalBuildCache
        {
            private readonly InMemoryIncrementalBuildCache _inner = new InMemoryIncrementalBuildCache();
            public readonly List<string> Stored = new List<string>();

            public bool TryGet(string modulePath, out string contentHash, out SurtrModuleImage image)
                => _inner.TryGet(modulePath, out contentHash, out image);

            public void Store(string modulePath, string contentHash, SurtrModuleImage image)
            {
                Stored.Add(modulePath);
                _inner.Store(modulePath, contentHash, image);
            }
        }

        private static readonly List<(string ModulePath, string Text)> TwoIndependentModules = new List<(string, string)>
        {
            ("game.A", "public fun a(): int { return 1; }"),
            ("game.B", "public fun b(): int { return 2; }"),
        };

        [Fact]
        public void ASecondBuildWithNoChanges_RecompilesNothing()
        {
            var cache = new CountingCache();

            var first = SurtrIncrementalBuild.Run(TwoIndependentModules, cache);
            Assert.Equal(2, first.Count);
            Assert.Equal(2, cache.Stored.Count);

            cache.Stored.Clear();
            var second = SurtrIncrementalBuild.Run(TwoIndependentModules, cache);

            Assert.Empty(cache.Stored);
            Assert.Equal(2, second.Count);
        }

        [Fact]
        public void ChangingALeafModule_RecompilesOnlyThatModule()
        {
            var cache = new CountingCache();
            SurtrIncrementalBuild.Run(TwoIndependentModules, cache);
            cache.Stored.Clear();

            var changed = new List<(string ModulePath, string Text)>
            {
                ("game.A", "public fun a(): int { return 1; }"),   // unchanged
                ("game.B", "public fun b(): int { return 20; }"),  // changed
            };

            SurtrIncrementalBuild.Run(changed, cache);

            Assert.Equal(new[] { "game.B" }, cache.Stored);
        }

        [Fact]
        public void ChangingADependency_AlsoRecompilesItsDependents()
        {
            var cache = new CountingCache();
            var sources = new List<(string ModulePath, string Text)>
            {
                ("game.core.Base", "public fun base(): int { return 1; }"),
                ("game.core.Derived", "import game.core.Base;\npublic fun derived(): int { return base() + 1; }"),
            };

            SurtrIncrementalBuild.Run(sources, cache);
            cache.Stored.Clear();

            var changed = new List<(string ModulePath, string Text)>
            {
                ("game.core.Base", "public fun base(): int { return 100; }"),                        // changed
                ("game.core.Derived", "import game.core.Base;\npublic fun derived(): int { return base() + 1; }"), // text unchanged
            };

            SurtrIncrementalBuild.Run(changed, cache);

            // Derived's own text never changed, but Base's signature could have - the conservative
            // rule recompiles it too, not just the module whose text actually differs.
            Assert.Equal(2, cache.Stored.Count);
            Assert.Contains("game.core.Base", cache.Stored);
            Assert.Contains("game.core.Derived", cache.Stored);
        }

        [Fact]
        public void RunProducesTheSameResultRegardlessOfCacheState()
        {
            var cache = new CountingCache();
            var first = SurtrIncrementalBuild.Run(TwoIndependentModules, cache);

            var warmImage = first.Single(i => i.Path == "game.A");

            var changed = new List<(string ModulePath, string Text)>
            {
                ("game.A", "public fun a(): int { return 1; }"),
                ("game.B", "public fun b(): int { return 20; }"),
            };

            var second = SurtrIncrementalBuild.Run(changed, cache);
            var reusedImage = second.Single(i => i.Path == "game.A");

            // game.A was reused (not in cache.Stored for this call), and the bytes are the exact
            // same cached image, not a re-derived equivalent.
            Assert.Same(warmImage, reusedImage);
        }

        [Fact]
        public void Dependents_InvertsDependenciesOf()
        {
            var graph = new ModuleDependencyGraph();
            graph.AddDependency("A", "B"); // A imports B
            graph.AddDependency("C", "B"); // C imports B
            graph.AddDependency("B", "D"); // B imports D

            var dependentsOfB = graph.Dependents("B");
            Assert.Contains("A", dependentsOfB);
            Assert.Contains("C", dependentsOfB);
            Assert.DoesNotContain("D", dependentsOfB);

            Assert.Empty(graph.Dependents("A"));
        }
    }
}
