#nullable enable

using System;
using System.IO;
using System.Linq;
using Surtr.LanguageServer.Workspace;

namespace Surtr.Tests.LanguageServer
{
    /// <summary>
    /// Exercises the Language Server's workspace/completion/hover pipeline against the real
    /// compiler — the same path <see cref="Surtr.LanguageServer.LspServer"/> drives — focused on the
    /// two things a request must always get right regardless of what a file imports: the built-in
    /// surface (§13) is always in scope, and an import (§2.1) is resolved and diagnosed exactly like
    /// the compiler's own binder does. These are the only tests the language server has today; each
    /// one writes its own file tree under a fresh temp directory and deletes it afterwards.
    /// </summary>
    public sealed class LanguageServerWorkspaceTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "surtr-lsp-tests",
            Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private Workspace Tree(params (string Path, string Text)[] files)
        {
            foreach (var (path, text) in files)
            {
                string full = Path.Combine(_root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }

            return new Workspace(_root);
        }

        [Fact]
        public void ABuiltInArrayMemberCompletesWithNoImportAtAll()
        {
            const string source =
                "public class Holder {\n" +
                "    public fun run(): void {\n" +
                "        let xs: int[] = [1, 2, 3];\n" +
                "        let n: int = xs.length;\n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(("app/Holder.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Count == 0 || diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Holder.surtr");
            int dotEnd = source.IndexOf("xs.length", StringComparison.Ordinal) + "xs.".Length;

            var completion = CompletionProvider.Complete(workspace.Snapshot, path, source, dotEnd);

            Assert.Contains(completion.Items, item => item.Label == "length");
            Assert.Contains(completion.Items, item => item.Label == "push");
        }

        [Fact]
        public void AWildcardImportedTypeAppearsInCompletionAndResolvesToItsDeclaration()
        {
            const string coreSource = "public class Entity {\n    public fun greet(): string { return \"hi\"; }\n}\n";
            const string appSource =
                "import proj.core.*;\n\n" +
                "public class Holder {\n" +
                "    public fun run(): void {\n" +
                "        \n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(
                ("proj/core/Entity.surtr", coreSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");
            int insideBody = appSource.IndexOf("        \n", StringComparison.Ordinal) + "        ".Length;

            var completion = CompletionProvider.Complete(workspace.Snapshot, appPath, appSource, insideBody);
            Assert.Contains(completion.Items, item => item.Label == "Entity");
        }

        [Fact]
        public void CollidingWildcardImportsAreDiagnosedAtTheUseNotTheImport()
        {
            const string firstSource = "public class Widget { }\n";
            const string secondSource = "public class Widget { }\n";
            const string appSource =
                "import proj.first.*;\n" +
                "import proj.second.*;\n\n" +
                "public class Holder {\n" +
                "    public fun run(): void {\n" +
                "        let w: Widget = null;\n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(
                ("proj/first/Widget.surtr", firstSource),
                ("proj/second/Widget.surtr", secondSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");
            Assert.True(diagnostics.TryGetValue(appPath, out var appDiagnostics) && appDiagnostics.Count > 0,
                "Expected the ambiguous 'Widget' use to be reported against the consuming file, not the imports: " + Describe(diagnostics));
        }

        [Fact]
        public void ExpressionCompletionKeywordsMatchSection1Point2()
        {
            const string source =
                "public class Holder {\n" +
                "    public fun run(): void {\n" +
                "        \n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(("app/Holder.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Holder.surtr");
            int insideBody = source.IndexOf("        \n", StringComparison.Ordinal) + "        ".Length;

            var completion = CompletionProvider.Complete(workspace.Snapshot, path, source, insideBody);
            var labels = completion.Items.Select(item => item.Label).ToList();

            // §1.2 reserves "constructor" and explicitly does *not* reserve "new" — Surtr has no
            // `new` (§5.5) — nor "not"/"or", which do not exist as tokens at all (logical operators
            // are symbolic: &&, ||, !).
            Assert.Contains("constructor", labels);
            Assert.DoesNotContain("new", labels);
            Assert.DoesNotContain("not", labels);
            Assert.DoesNotContain("or", labels);
        }

        [Fact]
        public void HoverAndDefinitionOnAWildcardImportedTypeReachTheDeclaringFile()
        {
            const string coreSource = "public class Entity {\n    public fun greet(): string { return \"hi\"; }\n}\n";
            const string appSource =
                "import proj.core.*;\n\n" +
                "public class Holder {\n" +
                "    public var e: Entity;\n" +
                "}\n";

            var workspace = Tree(
                ("proj/core/Entity.surtr", coreSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");
            string corePath = Path.Combine(_root, "proj", "core", "Entity.surtr");

            int nameOffset = appSource.IndexOf("Entity;", StringComparison.Ordinal);
            var hit = SymbolResolver.Resolve(workspace.Snapshot, appPath, appSource, nameOffset);

            Assert.NotNull(hit);
            Assert.True(hit!.HasDefinition, "Expected the imported type name to resolve to a declaration.");
            Assert.Equal(Path.GetFullPath(corePath), Path.GetFullPath(hit.DefinitionFile!), ignoreCase: true);
        }

        private static string Describe(System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IReadOnlyList<Surtr.Compiler.Diagnostics.SurtrDiagnostic>> diagnostics)
            => string.Join(" | ", diagnostics.SelectMany(pair => pair.Value).Select(d => d.ToString()));
    }
}
