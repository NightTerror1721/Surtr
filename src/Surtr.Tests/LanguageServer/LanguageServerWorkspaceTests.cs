#nullable enable

using System;
using System.Collections.Generic;
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
            Assert.Contains("moduleof", labels);
            Assert.DoesNotContain("new", labels);
            Assert.DoesNotContain("not", labels);
            Assert.DoesNotContain("or", labels);

            // §1.2's fourth contextual keyword ("this", "super" and "value" are the other three,
            // already covered above via §1.2's reserved-word list) — "attribute" (§11) was added to
            // the language in the same session that documented this list but never propagated here,
            // so completion never offered it even though it is a real, legal token.
            Assert.Contains("attribute", labels);
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

        [Fact]
        public void AModuleAliasCompletesItsModulesTypes()
        {
            const string coreSource = "public class Entity {\n    public fun greet(): string { return \"hi\"; }\n}\n";
            const string appSource =
                "import proj.core as Core;\n\n" +
                "public class Holder {\n" +
                "    public fun run(): void {\n" +
                "        let e = Core.Entity();\n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(
                ("proj/core/Entity.surtr", coreSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");
            int dotEnd = appSource.IndexOf("Core.Entity()", StringComparison.Ordinal) + "Core.".Length;

            var completion = CompletionProvider.Complete(workspace.Snapshot, appPath, appSource, dotEnd);
            Assert.Contains(completion.Items, item => item.Label == "Entity");
        }

        [Fact]
        public void AModuleAliasNameItselfAppearsInBareCompletion()
        {
            const string coreSource = "public class Entity { }\n";
            const string appSource =
                "import proj.core as Core;\n\n" +
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
            Assert.Contains(completion.Items, item => item.Label == "Core");
        }

        [Fact]
        public void ASelectiveImportBringsOnlyTheListedTypeIntoBareCompletion()
        {
            const string coreSource = "public class Entity { }\npublic class Widget { }\n";
            const string appSource =
                "import proj.core.{Entity};\n\n" +
                "public class Holder {\n" +
                "    public fun run(): void {\n" +
                "        \n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(
                ("proj/core/Shapes.surtr", coreSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");
            int insideBody = appSource.IndexOf("        \n", StringComparison.Ordinal) + "        ".Length;

            var completion = CompletionProvider.Complete(workspace.Snapshot, appPath, appSource, insideBody);
            Assert.Contains(completion.Items, item => item.Label == "Entity");
            Assert.DoesNotContain(completion.Items, item => item.Label == "Widget");
        }

        /// <summary>`proj.core` has no files of its own here - only its submodule `proj.core.geo` does.</summary>
        [Fact]
        public void ADirectoryWildcardBringsASubmodulesTypeIntoBareCompletion()
        {
            const string nestedSource = "public class Entity { }\n";
            const string appSource =
                "import proj.core.*;\n\n" +
                "public class Holder {\n" +
                "    public fun run(): void {\n" +
                "        \n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(
                ("proj/core/geo/Entity.surtr", nestedSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");
            int insideBody = appSource.IndexOf("        \n", StringComparison.Ordinal) + "        ".Length;

            var completion = CompletionProvider.Complete(workspace.Snapshot, appPath, appSource, insideBody);
            Assert.Contains(completion.Items, item => item.Label == "Entity");
        }

        /// <summary>Fase 12's sweep: hover/definition on a type reached only through a module alias (Fase 7).</summary>
        [Fact]
        public void HoverAndDefinitionOnAModuleAliasedTypeReachTheDeclaringFile()
        {
            const string coreSource = "public class Entity {\n    public fun greet(): string { return \"hi\"; }\n}\n";
            const string appSource =
                "import proj.core as Core;\n\n" +
                "public class Holder {\n" +
                "    public var e: Core.Entity;\n" +
                "}\n";

            var workspace = Tree(
                ("proj/core/Entity.surtr", coreSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");
            string corePath = Path.Combine(_root, "proj", "core", "Entity.surtr");

            int nameOffset = appSource.IndexOf("Core.Entity;", StringComparison.Ordinal) + "Core.".Length;
            var hit = SymbolResolver.Resolve(workspace.Snapshot, appPath, appSource, nameOffset);

            Assert.NotNull(hit);
            Assert.True(hit!.HasDefinition, "Expected the aliased type name to resolve to a declaration.");
            Assert.Equal(Path.GetFullPath(corePath), Path.GetFullPath(hit.DefinitionFile!), ignoreCase: true);
        }

        /// <summary>Fase 12's sweep: hover/definition on a type reached only through a selective import (Fase 8).</summary>
        [Fact]
        public void HoverAndDefinitionOnASelectivelyImportedTypeReachTheDeclaringFile()
        {
            const string coreSource = "public class Entity {\n    public fun greet(): string { return \"hi\"; }\n}\npublic class Widget { }\n";
            const string appSource =
                "import proj.core.{Entity};\n\n" +
                "public class Holder {\n" +
                "    public var e: Entity;\n" +
                "}\n";

            var workspace = Tree(
                ("proj/core/Shapes.surtr", coreSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");
            string corePath = Path.Combine(_root, "proj", "core", "Shapes.surtr");

            int nameOffset = appSource.IndexOf("Entity;", StringComparison.Ordinal);
            var hit = SymbolResolver.Resolve(workspace.Snapshot, appPath, appSource, nameOffset);

            Assert.NotNull(hit);
            Assert.True(hit!.HasDefinition, "Expected the selectively imported type name to resolve to a declaration.");
            Assert.Equal(Path.GetFullPath(corePath), Path.GetFullPath(hit.DefinitionFile!), ignoreCase: true);
        }

        /// <summary>Fase 12's sweep: hover/definition on a type reached only through a directory wildcard's submodule (Fase 9).</summary>
        [Fact]
        public void HoverAndDefinitionOnADirectoryWildcardSubmoduleTypeReachTheDeclaringFile()
        {
            const string nestedSource = "public class Entity {\n    public fun greet(): string { return \"hi\"; }\n}\n";
            const string appSource =
                "import proj.core.*;\n\n" +
                "public class Holder {\n" +
                "    public var e: Entity;\n" +
                "}\n";

            var workspace = Tree(
                ("proj/core/geo/Entity.surtr", nestedSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");
            string nestedPath = Path.Combine(_root, "proj", "core", "geo", "Entity.surtr");

            int nameOffset = appSource.IndexOf("Entity;", StringComparison.Ordinal);
            var hit = SymbolResolver.Resolve(workspace.Snapshot, appPath, appSource, nameOffset);

            Assert.NotNull(hit);
            Assert.True(hit!.HasDefinition, "Expected the directory-wildcard-imported submodule type to resolve to a declaration.");
            Assert.Equal(Path.GetFullPath(nestedPath), Path.GetFullPath(hit.DefinitionFile!), ignoreCase: true);
        }

        /// <summary>
        /// `typeof`'s instance form (a bound <c>Operand</c> rather than a <c>TargetType</c>) has to
        /// be walked into for hover to reach a local used as its argument - before
        /// <c>SymbolResolver.WalkExpression</c> grew a <c>BoundTypeOfExpression</c> case, the walk
        /// stopped dead at the <c>typeof</c> node and <c>Resolve</c> returned null for it entirely
        /// (no hover at all, not even a degraded one - a local never gets <c>HasDefinition</c> in
        /// this LSP regardless of construct, so the markdown text is what actually pins the fix).
        /// </summary>
        [Fact]
        public void HoverOnATypeOfInstanceOperandReachesTheLocalItReads()
        {
            const string source =
                "public class Box { public let value: int = 0; }\n" +
                "public class Holder {\n" +
                "    public fun run(): void {\n" +
                "        let b: Box = Box();\n" +
                "        let t = typeof(b);\n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(("app/Holder.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Holder.surtr");
            int nameOffset = source.IndexOf("typeof(b)", StringComparison.Ordinal) + "typeof(".Length;

            var hit = SymbolResolver.Resolve(workspace.Snapshot, path, source, nameOffset);

            Assert.NotNull(hit);
            Assert.Contains("b: Box", hit!.Markdown);
        }

        /// <summary>
        /// `typeof`'s static form still resolves a type name directly, same as before the fix -
        /// this pins that <c>BoundTypeOfExpression</c>'s <c>TargetType</c> path keeps working
        /// alongside the newly-added <c>Operand</c> path above. A bound-tree type hover never
        /// carries <c>HasDefinition</c> either, for <c>is</c>/<c>as</c> just as much as for
        /// <c>typeof</c> (<c>ConsiderName</c> reaches it through <c>FromType</c>, which sets no
        /// definition file), so the markdown is again what a fix or a regression would show up in.
        /// </summary>
        [Fact]
        public void HoverOnATypeOfStaticOperandNamesTheClass()
        {
            const string coreSource = "public class Entity {\n    public fun greet(): string { return \"hi\"; }\n}\n";
            const string appSource =
                "import proj.core.*;\n\n" +
                "public class Holder {\n" +
                "    public fun run(): void {\n" +
                "        let t = typeof(Entity);\n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(
                ("proj/core/Entity.surtr", coreSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");

            int nameOffset = appSource.IndexOf("typeof(Entity)", StringComparison.Ordinal) + "typeof(".Length;
            var hit = SymbolResolver.Resolve(workspace.Snapshot, appPath, appSource, nameOffset);

            Assert.NotNull(hit);
            Assert.Contains("Entity", hit!.Markdown);
        }

        /// <summary>
        /// Member completion after a dot has to descend through a <c>typeof(...)</c> wrapper to
        /// reach a non-bare-identifier receiver - before <c>CompletionProvider.ChildrenOf</c> grew a
        /// <c>BoundTypeOfExpression</c> case, the walk had nowhere to go past the outer node and
        /// completion on <c>typeof(Box().</c> returned nothing.
        /// </summary>
        [Fact]
        public void MemberCompletionAfterADotReachesThroughATypeOfWrapper()
        {
            const string source =
                "public class Box { public let value: int = 0; }\n" +
                "public class Holder {\n" +
                "    public fun run(): void {\n" +
                "        let t = typeof(Box().value);\n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(("app/Holder.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Holder.surtr");
            int dotEnd = source.IndexOf("typeof(Box().", StringComparison.Ordinal) + "typeof(Box().".Length;

            var completion = CompletionProvider.Complete(workspace.Snapshot, path, source, dotEnd);

            Assert.Contains(completion.Items, item => item.Label == "value");
        }

        [Fact]
        public void HoverOnACallToAnImplicitInterfaceMemberNamesTheContractItSatisfies()
        {
            // §3.3: satisfying an interface never requires `override` - `iterate` here is a plain
            // Direct method with nothing in its own signature saying it fulfils IIterable<int>. Hover
            // resolves a call site through the bound tree (SymbolResolver's pass one), which is where
            // this enrichment lives; hovering the declaration's own name instead falls to the plain
            // signature-text pass (pass two) and is not covered by this fix — a follow-up, not a claim
            // made here.
            const string source =
                "public class Counter : IIterable<int> {\n" +
                "    public fun iterate(): IIterator<int> { return [1, 2, 3].iterate(); }\n" +
                "}\n" +
                "public class Holder {\n" +
                "    public fun run(): void {\n" +
                "        let c: Counter = Counter();\n" +
                "        let it = c.iterate();\n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(("app/Counter.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Counter.surtr");
            int nameOffset = source.IndexOf("c.iterate()", StringComparison.Ordinal) + "c.".Length;

            var hit = SymbolResolver.Resolve(workspace.Snapshot, path, source, nameOffset);

            Assert.NotNull(hit);
            Assert.Contains("implements", hit!.Markdown);
            Assert.Contains("IIterable<int>.iterate", hit.Markdown);
        }

        [Fact]
        public void HoverOnACallResolvingToAnAbstractMethodNeverClaimsToImplementAnything()
        {
            // The obligation-declaring side of a contract must not show "implements" itself - it is
            // the thing being implemented, not an implementation. `s.area()` resolves (statically) to
            // Shape's own abstract declaration, not Circle's override, since dispatch is a runtime
            // fact and binding only sees the static type of `s`.
            const string source =
                "public interface IShape {\n" +
                "    fun area(): float;\n" +
                "}\n" +
                "public abstract class Shape : IShape {\n" +
                "    public abstract fun area(): float;\n" +
                "}\n" +
                "public class Circle : Shape {\n" +
                "    public override fun area(): float { return 3.14; }\n" +
                "}\n" +
                "public class Holder {\n" +
                "    public fun run(): void {\n" +
                "        let s: Shape = Circle();\n" +
                "        let a = s.area();\n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(("app/Shape.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Shape.surtr");
            int nameOffset = source.IndexOf("s.area()", StringComparison.Ordinal) + "s.".Length;

            var hit = SymbolResolver.Resolve(workspace.Snapshot, path, source, nameOffset);

            Assert.NotNull(hit);
            Assert.DoesNotContain("implements", hit!.Markdown);
        }

        [Fact]
        public void ImplementMissingMembersOffersNoActionOnceEveryObligationIsSatisfied()
        {
            const string source =
                "public class Counter : IIterable<int> {\n" +
                "    public override fun iterate(): IIterator<int> { return [1, 2, 3].iterate(); }\n" +
                "}\n";

            var workspace = Tree(("app/Counter.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Counter.surtr");
            int somewhereInClass = source.IndexOf("class Counter", StringComparison.Ordinal);

            var actions = CodeActionProvider.Complete(workspace.Snapshot, path, source, somewhereInClass);
            Assert.Empty(actions);
        }

        [Fact]
        public void ImplementMissingMembersFixesAnUnimplementedInterfaceAndAppliedEditCompilesClean()
        {
            const string source =
                "public class Counter : IIterable<int> {\n" +
                "}\n";

            var workspace = Tree(("app/Counter.surtr", source));
            workspace.Rebuild();

            string path = Path.Combine(_root, "app", "Counter.surtr");
            int somewhereInClass = source.IndexOf("class Counter", StringComparison.Ordinal);

            var actions = CodeActionProvider.Complete(workspace.Snapshot, path, source, somewhereInClass);
            var action = Assert.Single(actions);
            Assert.Contains("iterate", action.Title);

            var edits = Assert.Single(action.Edit!.Changes);
            var edit = Assert.Single(edits.Value);

            // No "override" for an interface member (§3.3: satisfying one never requires it).
            Assert.DoesNotContain("override", edit.NewText);
            Assert.Contains("fun iterate()", edit.NewText);
            Assert.Contains("InvalidOperationException", edit.NewText);

            string patched = ApplyEdit(source, edit);
            var patchedWorkspace = Tree(("app/Counter.surtr", patched));
            var patchedDiagnostics = patchedWorkspace.Rebuild();
            Assert.True(patchedDiagnostics.Values.All(list => list.Count == 0),
                "The stub the code action generated must itself compile clean: " + Describe(patchedDiagnostics) + "\n" + patched);
        }

        [Fact]
        public void ImplementMissingMembersWritesOverrideForAnAbstractBaseMemberButNotForAnInterface()
        {
            const string source =
                "public abstract class Shape {\n" +
                "    public abstract fun area(): float;\n" +
                "}\n" +
                "public class Circle : Shape {\n" +
                "}\n";

            var workspace = Tree(("app/Shape.surtr", source));
            workspace.Rebuild();

            string path = Path.Combine(_root, "app", "Shape.surtr");
            int somewhereInClass = source.IndexOf("class Circle", StringComparison.Ordinal);

            var actions = CodeActionProvider.Complete(workspace.Snapshot, path, source, somewhereInClass);
            var action = Assert.Single(actions);
            var edit = Assert.Single(Assert.Single(action.Edit!.Changes).Value);

            Assert.Contains("override fun area()", edit.NewText);

            string patched = ApplyEdit(source, edit);
            var patchedWorkspace = Tree(("app/Shape.surtr", patched));
            var patchedDiagnostics = patchedWorkspace.Rebuild();
            Assert.True(patchedDiagnostics.Values.All(list => list.Count == 0),
                "The stub the code action generated must itself compile clean: " + Describe(patchedDiagnostics) + "\n" + patched);
        }

        /// <summary>Applies a single LSP <see cref="Surtr.LanguageServer.Protocol.TextEdit"/> to plain text, for test assertions only.</summary>
        private static string ApplyEdit(string text, Surtr.LanguageServer.Protocol.TextEdit edit)
        {
            var lines = TextLines.Index(text);
            int offset = lines.OffsetAt(edit.Range.Start.Line, edit.Range.Start.Character);
            return text.Substring(0, offset) + edit.NewText + text.Substring(offset);
        }

        [Fact]
        public void SemanticTokensTagThisAndSuperOnlyWhereTheyAreRealReceiversNotElsewhere()
        {
            // "this" inside the string literal must NOT be tagged - only the two real occurrences
            // (an implicit receiver on the field read, and an explicit one before .bark()) are
            // resolved from the bound tree, which is exactly the position-accuracy a regex grammar
            // cannot get right on its own.
            const string source =
                "public class Animal {\n" +
                "    public let name: string = \"this\";\n" +
                "    public constructor(name: string) { this.name = name; }\n" +
                "    public fun bark(): string { return this.name; }\n" +
                "}\n" +
                "public class Dog : Animal {\n" +
                "    public constructor(name: string) : super(name) { }\n" +
                "    public fun describe(): string { return this.bark(); }\n" +
                "}\n";

            var workspace = Tree(("app/Animal.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Animal.surtr");
            var result = SemanticTokensProvider.Compute(workspace.Snapshot, path, source);

            int tokenCount = result.Data.Count / 5;
            Assert.True(tokenCount >= 3, $"Expected at least 3 this/super tokens, got {tokenCount}.");

            // Decode back to absolute offsets and check every tagged span really reads "this".
            var lines = TextLines.Index(source);
            int line = 0;
            int character = 0;
            for (int i = 0; i < result.Data.Count; i += 5)
            {
                line += result.Data[i];
                character = result.Data[i] == 0 ? character + result.Data[i + 1] : result.Data[i + 1];
                int length = result.Data[i + 2];

                int offset = lines.OffsetAt(line, character);
                string tagged = source.Substring(offset, length);
                Assert.Equal("this", tagged);
            }

            // The literal "this" text inside the string must not itself be tagged - none of the
            // decoded spans may fall inside the string literal's own range.
            int stringLiteralStart = source.IndexOf("\"this\"", StringComparison.Ordinal);
            int stringLiteralEnd = stringLiteralStart + "\"this\"".Length;
            line = 0;
            character = 0;
            for (int i = 0; i < result.Data.Count; i += 5)
            {
                line += result.Data[i];
                character = result.Data[i] == 0 ? character + result.Data[i + 1] : result.Data[i + 1];
                int offset = lines.OffsetAt(line, character);
                Assert.False(offset >= stringLiteralStart && offset < stringLiteralEnd,
                    "The 'this' spelled inside the string literal must not be tagged as the keyword.");
            }
        }

        [Fact]
        public void SemanticTokensTagValueAndAttributeOnlyOnTheDeclarationsThatUseThemAsKeywords()
        {
            const string source =
                "value class Money {\n" +
                "    public let amount: int;\n" +
                "    public constructor(amount: int) { this.amount = amount; }\n" +
                "}\n" +
                "attribute class Range {\n" +
                "    public let lo: int = 0;\n" +
                "}\n" +
                "public class Box {\n" +
                // "value" here is the implicit setter parameter, never the keyword - must not be tagged.
                "    public amount: int { get => 0; set { let value2 = value; } }\n" +
                "}\n";

            var workspace = Tree(("app/Money.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Money.surtr");
            var result = SemanticTokensProvider.Compute(workspace.Snapshot, path, source);

            var lines = TextLines.Index(source);
            var taggedTexts = new List<string>();
            int line = 0;
            int character = 0;
            for (int i = 0; i < result.Data.Count; i += 5)
            {
                line += result.Data[i];
                character = result.Data[i] == 0 ? character + result.Data[i + 1] : result.Data[i + 1];
                int length = result.Data[i + 2];
                int offset = lines.OffsetAt(line, character);
                taggedTexts.Add(source.Substring(offset, length));
            }

            Assert.Contains("value", taggedTexts);
            Assert.Contains("attribute", taggedTexts);
            Assert.Contains("get", taggedTexts);
            Assert.Contains("set", taggedTexts);

            // Exactly one "value" is tagged (the "value class" keyword) - the setter's implicit
            // parameter, spelled "value" twice more in the last line, must not add two more.
            Assert.Single(taggedTexts.FindAll(t => t == "value"));
        }

        private static string Describe(System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IReadOnlyList<Surtr.Compiler.Diagnostics.SurtrDiagnostic>> diagnostics)
            => string.Join(" | ", diagnostics.SelectMany(pair => pair.Value).Select(d => d.ToString()));
    }
}
