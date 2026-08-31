#nullable enable

using Surtr.Compiler.Compilation;
using System.Collections.Generic;

namespace Surtr.Tests.Compiler.Compilation
{
    /// <summary>
    /// Covers the two <see cref="ISourceProvider"/> conveniences a host reaches for when composing
    /// its own module resolution: <see cref="CompositeSourceProvider"/> (chain several) and
    /// <see cref="DictionarySourceProvider"/> (a fixed in-memory map, for a host that already has
    /// its scripts as data).
    /// </summary>
    public sealed class SourceProviderTests
    {
        private sealed class StubProvider : ISourceProvider
        {
            private readonly Dictionary<string, string> _sources;
            public StubProvider(Dictionary<string, string> sources) => _sources = sources;

            public bool TryGetSource(string modulePath, out string text, out string diagnosticPath)
            {
                if (_sources.TryGetValue(modulePath, out text!))
                {
                    diagnosticPath = "stub:" + modulePath;
                    return true;
                }

                text = string.Empty;
                diagnosticPath = string.Empty;
                return false;
            }
        }

        [Fact]
        public void DictionarySourceProvider_ResolvesAKnownModule_AndFailsForAnUnknownOne()
        {
            var provider = new DictionarySourceProvider(new Dictionary<string, string>
            {
                ["game.core.M"] = "fun f(): int { return 1; }",
            });

            Assert.True(provider.TryGetSource("game.core.M", out string text, out string diagnosticPath));
            Assert.Contains("return 1", text);
            Assert.Equal("game.core.M", diagnosticPath);

            Assert.False(provider.TryGetSource("game.core.NoSuchModule", out _, out _));
        }

        [Fact]
        public void CompositeSourceProvider_TheFirstProviderThatKnowsAModuleWins()
        {
            var first = new StubProvider(new Dictionary<string, string> { ["a"] = "from-first" });
            var second = new StubProvider(new Dictionary<string, string> { ["a"] = "from-second", ["b"] = "from-second" });

            var composite = new CompositeSourceProvider(first, second);

            Assert.True(composite.TryGetSource("a", out string textA, out _));
            Assert.Equal("from-first", textA);

            Assert.True(composite.TryGetSource("b", out string textB, out _));
            Assert.Equal("from-second", textB);
        }

        [Fact]
        public void CompositeSourceProvider_FailsCleanlyWhenNoProviderKnowsTheModule()
        {
            var composite = new CompositeSourceProvider(
                new StubProvider(new Dictionary<string, string>()),
                new StubProvider(new Dictionary<string, string>()));

            Assert.False(composite.TryGetSource("nowhere", out _, out _));
        }
    }
}
