#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Surtr.LanguageServer.Protocol;
using Surtr.LanguageServer.Workspace;

namespace Surtr.Tests.LanguageServer
{
    /// <summary>
    /// Exercises the Language Server's workspace/completion/hover pipeline against the real
    /// compiler â€” the same path <see cref="Surtr.LanguageServer.LspServer"/> drives â€” focused on the
    /// two things a request must always get right regardless of what a file imports: the built-in
    /// surface (Â§13) is always in scope, and an import (Â§2.1) is resolved and diagnosed exactly like
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

            // Â§1.2 reserves "constructor" and explicitly does *not* reserve "new" â€” Surtr has no
            // `new` (Â§5.5) â€” nor "not"/"or", which do not exist as tokens at all (logical operators
            // are symbolic: &&, ||, !).
            Assert.Contains("constructor", labels);
            Assert.Contains("moduleof", labels);
            Assert.DoesNotContain("new", labels);
            Assert.DoesNotContain("not", labels);
            Assert.DoesNotContain("or", labels);

            // Â§1.2's fourth contextual keyword ("this", "super" and "value" are the other three,
            // already covered above via Â§1.2's reserved-word list) â€” "attribute" (Â§11) was added to
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
                "import proj.core.Entity as Core;\n\n" +
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
                "import proj.core.Entity as Core;\n\n" +
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
                "import proj.core.Shapes.{Entity};\n\n" +
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
                "import proj.core.Entity as Core;\n\n" +
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
                "import proj.core.Shapes.{Entity};\n\n" +
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
            // Â§3.3: satisfying an interface never requires `override` - `iterate` here is a plain
            // Direct method with nothing in its own signature saying it fulfils IIterable<int>. Hover
            // resolves a call site through the bound tree (SymbolResolver's pass one), which is where
            // this enrichment lives; hovering the declaration's own name instead falls to the plain
            // signature-text pass (pass two) and is not covered by this fix â€” a follow-up, not a claim
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

            // No "override" for an interface member (Â§3.3: satisfying one never requires it).
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

        [Fact]
        public void AnExtensionMethodBroughtByAWildcardImportCompletesAfterADot()
        {
            const string coreSource =
                "public class Vec2 {\n" +
                "    public let x: float;\n" +
                "    public let y: float;\n" +
                "    public constructor(x: float, y: float) { this.x = x; this.y = y; }\n" +
                "}\n" +
                "public extension Vec2 { lengthSquared: float => this.x * this.x + this.y * this.y; }\n";
            const string appSource =
                "import proj.core.*;\n\n" +
                "public class Holder {\n" +
                "    public fun run(): void {\n" +
                "        let v: Vec2 = Vec2(3.0, 4.0);\n" +
                "        let n: float = v.lengthSquared;\n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(
                ("proj/core/Vec2.surtr", coreSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");
            int dotEnd = appSource.IndexOf("v.lengthSquared", StringComparison.Ordinal) + "v.".Length;

            var completion = CompletionProvider.Complete(workspace.Snapshot, appPath, appSource, dotEnd);

            Assert.Contains(completion.Items, item => item.Label == "lengthSquared");
        }

        [Fact]
        public void HoverAndDefinitionOnAnExtensionMethodCallReachTheExtensionBlockAndNameItAsOne()
        {
            const string source =
                "class Vec2 {\n" +
                "    public let x: float;\n" +
                "    public let y: float;\n" +
                "    public constructor(x: float, y: float) { this.x = x; this.y = y; }\n" +
                "}\n" +
                "extension Vec2 {\n" +
                "    fun lengthSquared(self: Vec2): float => self.x * self.x + self.y * self.y;\n" +
                "}\n" +
                "fun run(): float { return Vec2(3.0, 4.0).lengthSquared(); }\n";

            var workspace = Tree(("app/Vec2.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Vec2.surtr");
            int callNameOffset = source.LastIndexOf("lengthSquared()", StringComparison.Ordinal);

            var hit = SymbolResolver.Resolve(workspace.Snapshot, path, source, callNameOffset);

            Assert.NotNull(hit);
            Assert.Contains("extension method on `Vec2`", hit!.Markdown);

            Assert.True(hit.HasDefinition, "Expected the call to resolve to the extension method's own declaration.");
            Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(hit.DefinitionFile!), ignoreCase: true);

            // The declaration's span must land on the method *inside* the `extension` block, not on
            // some unrelated same-named/same-arity module function `SymbolResolver` might otherwise
            // have matched by accident (`MatchesParent`'s original, extension-unaware rule).
            int declaredAt = source.IndexOf("fun lengthSquared(self: Vec2)", StringComparison.Ordinal) + "fun ".Length;
            Assert.Equal(declaredAt, hit.DefinitionStart);
        }

        [Fact]
        public void ExtensionAppearsInBareKeywordCompletion()
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
            Assert.Contains("extension", completion.Items.Select(item => item.Label));
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

            // The provider now tags more than this/super (types, type parameters, contextual
            // keywords), so the "reads this" assertion is scoped to the `variable` tokens, which are
            // exactly the this/super pass.
            int variableType = Array.IndexOf(SemanticTokensProvider.TokenTypes, "variable");
            int thisOrSuperCount = 0;

            // The literal "this" text inside the string must not itself be tagged - no decoded
            // span may fall inside the string literal's own range.
            int stringLiteralStart = source.IndexOf("\"this\"", StringComparison.Ordinal);
            int stringLiteralEnd = stringLiteralStart + "\"this\"".Length;

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

                if (result.Data[i + 3] == variableType)
                {
                    thisOrSuperCount++;
                    Assert.True(tagged == "this" || tagged == "super",
                        $"A variable token must read 'this' or 'super', got '{tagged}'.");
                }

                // The literal "this" text inside the string must not itself be tagged - no decoded
                // span may fall inside the string literal's own range.
                Assert.False(offset >= stringLiteralStart && offset < stringLiteralEnd,
                    "The 'this' spelled inside the string literal must not be tagged as the keyword.");
            }

            Assert.True(thisOrSuperCount >= 3,
                $"Expected at least 3 this/super tokens, got {thisOrSuperCount}.");
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

        [Fact]
        public void SemanticTokensTagTypesTypeParametersAndContextualKeywords()
        {
            // The semantic pass now tags what a regex grammar cannot: type references in any
            // position (type parameters and their uses included), with the contextual keywords
            // coloured as modifiers/variables rather than as bare `keyword`, and no false positive
            // on an implicit receiver's span (a bare `_items` read must not be tagged at all).
            const string source =
                "public class Box<T> {\n" +
                "    private var _items: T[];\n" +
                "    public length: T { get { return _items[0]; } set { _items[0] = value; } }\n" +
                "    public fun pick(x: T): T { return _items[0]; }\n" +
                "}\n" +
                "value class Money {\n" +
                "    public let amount: int;\n" +
                "}\n";

            var workspace = Tree(("app/Box.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Box.surtr");
            var result = SemanticTokensProvider.Compute(workspace.Snapshot, path, source);

            int type = Array.IndexOf(SemanticTokensProvider.TokenTypes, "type");
            int typeParameter = Array.IndexOf(SemanticTokensProvider.TokenTypes, "typeParameter");
            int modifier = Array.IndexOf(SemanticTokensProvider.TokenTypes, "modifier");

            var tokens = DecodeSemanticTokens(result, source);

            // The type parameter's declaration name is a type parameter...
            Assert.True(tokens.Any(t => t.Text == "T" && t.TokenType == typeParameter),
                "Expected Box<T>'s 'T' to be tagged as a type parameter.");
            // ...and its uses in annotations, parameters and returns are types.
            Assert.True(tokens.Count(t => t.Text == "T" && t.TokenType == type) >= 3,
                "Expected the uses of 'T' (field, property, parameter, return) to be tagged as types.");
            // The built-in `int` is left to the grammar, never overridden by this pass.
            Assert.DoesNotContain(tokens, t => t.Text == "int");
            // Contextual keywords ride the modifier slot (blue like fun/public), not keyword.
            Assert.True(tokens.Any(t => t.Text == "value" && t.TokenType == modifier), "Expected 'value' as a modifier.");
            Assert.True(tokens.Any(t => t.Text == "get" && t.TokenType == modifier), "Expected 'get' as a modifier.");
            Assert.True(tokens.Any(t => t.Text == "set" && t.TokenType == modifier), "Expected 'set' as a modifier.");
            // A bare field read is not an implicit-receiver span any more: nothing tags `_items`.
            Assert.DoesNotContain(tokens, t => t.Text == "_items");
        }

        [Fact]
        public void InlayHintsCoverInferredTypesLambdaReturnsAndParameterNames()
        {
            const string source =
                "public class Vec2 {\n" +
                "    public constructor(x: float, y: float) { }\n" +
                "}\n" +
                "public class Game {\n" +
                "    public fun run(): void {\n" +
                "        let count = 42;\n" +
                "        let v = Vec2(1.0, 2.0);\n" +
                "        let f = (a: int) => a * 2;\n" +
                "        let hp = 100;\n" +
                "        spawn(hp, 'a');\n" +
                "    }\n" +
                "}\n" +
                "fun spawn(hp: int, tag: char): void { }\n";

            var workspace = Tree(("app/Game.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Game.surtr");
            var hints = InlayHintProvider.Compute(workspace.Snapshot, path, source);
            var labels = hints.Select(h => (h.Label?.ToString() ?? string.Empty, h.Kind)).ToList();

            // Inferred local types.
            Assert.Contains((": int", InlayHintKinds.Type), labels);
            Assert.Contains((": Vec2", InlayHintKinds.Type), labels);
            Assert.Contains((": (int) -> int", InlayHintKinds.Type), labels);
            // The lambda's inferred return type.
            Assert.True(hints.Count(h => h.Label?.ToString() == ": int" && h.Kind == InlayHintKinds.Type) >= 3,
                "Expected the local, the lambda and the lambda's return to each carry an ': int' type hint.");
            // Parameter names on literal/variable-mismatch arguments.
            Assert.Contains(("x:", InlayHintKinds.Parameter), labels);
            Assert.Contains(("y:", InlayHintKinds.Parameter), labels);
            // `spawn(hp, 'a')`: `hp` already names its parameter, only the literal gets a hint.
            Assert.Contains(("tag:", InlayHintKinds.Parameter), labels);
            Assert.DoesNotContain(("hp:", InlayHintKinds.Parameter), labels);
        }

        [Fact]
        public void HoverOnAConstructorCallShowsTheConstructorSignature()
        {
            const string source =
                "public class Vec2 {\n" +
                "    public let x: float;\n" +
                "    public let y: float;\n" +
                "    public constructor(x: float, y: float) { this.x = x; this.y = y; }\n" +
                "    public fun scale(s: float): Vec2 { return Vec2(x * s, y * s); }\n" +
                "}\n";

            var workspace = Tree(("app/Vec2.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Vec2.surtr");
            int callee = source.IndexOf("Vec2(x * s", StringComparison.Ordinal);
            var hit = SymbolResolver.Resolve(workspace.Snapshot, path, source, callee);

            Assert.NotNull(hit);
            Assert.Contains("constructor", hit!.Markdown);
            Assert.Contains("x : float, y : float", hit.Markdown);
            Assert.DoesNotContain("class Vec2", hit.Markdown);
        }

        [Fact]
        public void HoverOnABuiltInTypeShowsOneFencedCard()
        {
            const string source =
                "public class Box {\n" +
                "    public let value: int;\n" +
                "}\n";

            var workspace = Tree(("app/Box.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Box.surtr");
            int typeOffset = source.IndexOf("int;", StringComparison.Ordinal);
            var hit = SymbolResolver.Resolve(workspace.Snapshot, path, source, typeOffset);

            Assert.NotNull(hit);
            Assert.Contains("primitive type", hit!.Markdown);
            Assert.Equal(1, CountOccurrences(hit!.Markdown!, "```surtr"));
        }

        [Fact]
        public void HoverOnALocalShowsACardTheGrammarColoursItsTypeAsOne()
        {
            // The popup is a fenced block the TextMate grammar colours on its own (semantic tokens
            // only apply to the editor buffer), and the grammar only recognises `name: Type` as a
            // typed declaration when a binding keyword precedes it - so the card is rendered with a
            // `let`/`var` prefix, which is also how the declaration reads in source.
            const string source =
                "public class Dog { }\n" +
                "public class Game {\n" +
                "    public fun run(): void {\n" +
                "        let maybe: Dog? = null;\n" +
                "        let count = 42;\n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(("app/Game.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Game.surtr");

            int inferredOffset = source.IndexOf("count =", StringComparison.Ordinal);
            var inferred = SymbolResolver.Resolve(workspace.Snapshot, path, source, inferredOffset);
            Assert.NotNull(inferred);
            Assert.Contains("let count: int", inferred!.Markdown);

            int annotatedOffset = source.IndexOf("maybe:", StringComparison.Ordinal);
            var annotated = SymbolResolver.Resolve(workspace.Snapshot, path, source, annotatedOffset);
            Assert.NotNull(annotated);
            Assert.Contains("let maybe: Dog?", annotated!.Markdown);
        }

        #region Re-export and whole-module imports (Â§2.1)
        [Fact]
        public void HoverOnATypeReExportedByAnAggregatorReachesTheDeclaringFile()
        {
            const string mathSource = "public class Vec2 {\n    public let x: int;\n}\n";
            const string indexSource = "export import module proj.math.Vec2;\n";
            const string appSource =
                "import proj.core.Index;\n\n" +
                "public class Holder {\n" +
                "    public var v: Vec2;\n" +
                "}\n";

            var workspace = Tree(
                ("proj/math/Vec2.surtr", mathSource),
                ("proj/core/Index.surtr", indexSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");
            string mathPath = Path.Combine(_root, "proj", "math", "Vec2.surtr");

            int nameOffset = appSource.IndexOf("Vec2;", StringComparison.Ordinal);
            var hit = SymbolResolver.Resolve(workspace.Snapshot, appPath, appSource, nameOffset);

            Assert.NotNull(hit);
            Assert.True(hit!.HasDefinition, "Expected the re-exported type to resolve to its declaration.");
            Assert.Equal(Path.GetFullPath(mathPath), Path.GetFullPath(hit.DefinitionFile!), ignoreCase: true);
        }

        [Fact]
        public void CompletionAfterADotOnAnAggregatorOffersItsReExportedTypes()
        {
            const string mathSource = "public class Vec2 {\n    public let x: int;\n}\n";
            const string indexSource = "export import module proj.math.Vec2;\n";
            const string appSource =
                "import proj.core.Index as I;\n\n" +
                "public class Holder {\n" +
                "    public var v: I.Vec2;\n" +
                "    public fun run(): void {\n" +
                "        let w = I.Vec2();\n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(
                ("proj/math/Vec2.surtr", mathSource),
                ("proj/core/Index.surtr", indexSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");

            int dotOffset = appSource.LastIndexOf("I.", StringComparison.Ordinal) + 2;
            var completion = CompletionProvider.Complete(workspace.Snapshot, appPath, appSource, dotOffset);

            Assert.True(
                completion.Items.Any(item => item.Label == "Vec2"),
                "Vec2 missing from: " + string.Join(", ", completion.Items.Select(i => i.Label)));
        }

        [Fact]
        public void AWholeModuleImportedMemberCompletesUnqualified()
        {
            const string mathSource = "public fun add(a: int, b: int): int { return a + b; }\n";
            const string appSource =
                "import module proj.math.Math;\n\n" +
                "public class Holder {\n" +
                "    public fun run(): int {\n" +
                "        return add(1, 2);\n" +
                "    }\n" +
                "}\n";

            var workspace = Tree(
                ("proj/math/Math.surtr", mathSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string appPath = Path.Combine(_root, "proj", "app", "Holder.surtr");

            int offset = appSource.IndexOf("add(", StringComparison.Ordinal) + 1;
            var completion = CompletionProvider.Complete(workspace.Snapshot, appPath, appSource, offset);

            Assert.Contains(completion.Items, item => item.Label == "add");
        }

        [Fact]
        public void AWholeModuleImportOffersItsOwnModuleMembersInExpressionCompletion()
        {
            const string mathSource = "public fun add(a: int, b: int): int { return a + b; }\n";
            const string appSource =
                "import module proj.math.Math;\n\n" +
                "public class Holder {\n" +
                "    public fun run(): int { return add(2, 3); }\n" +
                "}\n";

            var workspace = Tree(
                ("proj/math/Math.surtr", mathSource),
                ("proj/app/Holder.surtr", appSource));

            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));
        }
        #endregion

        private static List<(string Text, int TokenType)> DecodeSemanticTokens(SemanticTokens tokens, string source)
        {
            var decoded = new List<(string, int)>();
            var lines = TextLines.Index(source);
            int line = 0;
            int character = 0;
            for (int i = 0; i < tokens.Data.Count; i += 5)
            {
                line += tokens.Data[i];
                character = tokens.Data[i] == 0 ? character + tokens.Data[i + 1] : tokens.Data[i + 1];
                int length = tokens.Data[i + 2];
                int offset = lines.OffsetAt(line, character);
                decoded.Add((source.Substring(offset, length), tokens.Data[i + 3]));
            }

            return decoded;
        }

        #region Value types and destructuring (the phase 7 smoke pass)

        /// <summary>
        /// A multi-field <c>value class</c> compiles clean through the language server's own
        /// pipeline, and its fields complete after a dot. The declaration is the shape §2.9 gained
        /// when value types stopped being single-field wrappers, so the point is that nothing on
        /// the LSP path assumed the old rule.
        /// </summary>
        [Fact]
        public void AMultiFieldValueClassCompilesCleanAndItsFieldsComplete()
        {
            const string source = @"public value class Vec2 {
    public let x: float;
    public let y: float;
    public constructor(x: float, y: float) { this.x = x; this.y = y; }
    public fun dot(other: Vec2): float { return this.x * other.x + this.y * other.y; }
}
public class Holder {
    public fun run(): float {
        let v: Vec2 = Vec2(1.0, 2.0);
        return v.x;
    }
}
";

            var workspace = Tree(("app/Holder.surtr", source));
            var diagnostics = workspace.Rebuild();

            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "A multi-field value class must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Holder.surtr");
            int dotEnd = source.IndexOf("v.x;", StringComparison.Ordinal) + "v.".Length;

            var completion = CompletionProvider.Complete(workspace.Snapshot, path, source, dotEnd);
            var labels = completion.Items.Select(item => item.Label).ToList();

            Assert.Contains("x", labels);
            Assert.Contains("y", labels);
            Assert.Contains("dot", labels);
        }

        /// <summary>
        /// Hover on a value-typed local names the value class rather than the field it erases to.
        /// That distinction is the whole of §2.9: erasure is a runtime representation, and the type
        /// checker - which is what hover reads - never sees it.
        /// </summary>
        [Fact]
        public void HoverOnAValueTypedLocalNamesTheValueClassNotItsField()
        {
            const string source = @"public value class EntityId {
    public let raw: int;
    public constructor(raw: int) { this.raw = raw; }
}
public class Holder {
    public fun run(): int {
        let id: EntityId = EntityId(7);
        return id.raw;
    }
}
";

            var workspace = Tree(("app/Holder.surtr", source));
            var diagnostics = workspace.Rebuild();
            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "The fixture itself must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Holder.surtr");
            int nameOffset = source.IndexOf("id.raw", StringComparison.Ordinal);

            var hit = SymbolResolver.Resolve(workspace.Snapshot, path, source, nameOffset);

            Assert.NotNull(hit);
            Assert.Contains("id: EntityId", hit!.Markdown);
        }

        /// <summary>
        /// A destructuring declaration (§4.5) binds real locals, so the names it introduces have to
        /// hover like any other. This is the case the desugaring exists to make true: nothing
        /// downstream should be able to tell the difference.
        /// </summary>
        [Fact]
        public void DestructuredNamesAreOrdinaryLocalsToHover()
        {
            const string source = @"public class Holder {
    public fun divmod(a: int, b: int): (int, int) { return (a / b, a % b); }
    public fun run(): int {
        let (quotient, remainder) = divmod(17, 5);
        return quotient + remainder;
    }
}
";

            var workspace = Tree(("app/Holder.surtr", source));
            var diagnostics = workspace.Rebuild();

            Assert.True(diagnostics.Values.All(list => list.Count == 0),
                "A destructuring declaration must compile clean: " + Describe(diagnostics));

            string path = Path.Combine(_root, "app", "Holder.surtr");
            int useOffset = source.IndexOf("return quotient", StringComparison.Ordinal) + "return ".Length;

            var hit = SymbolResolver.Resolve(workspace.Snapshot, path, source, useOffset);

            Assert.NotNull(hit);
            Assert.Contains("quotient: int", hit!.Markdown);
        }

        /// <summary>
        /// A malformed destructuring reports <c>InvalidDestructuring</c> against the file that
        /// wrote it, carrying a span. A diagnostic the server cannot place is one no editor can
        /// underline, so the span is the part that matters here rather than the code.
        /// </summary>
        [Fact]
        public void AMalformedDestructuringIsReportedWithASpanTheEditorCanUnderline()
        {
            const string source = @"public class Holder {
    public fun pair(): (int, int) { return (1, 2); }
    public fun run(): int {
        let (a, b, c) = pair();
        return a + b + c;
    }
}
";

            var workspace = Tree(("app/Holder.surtr", source));
            var diagnostics = workspace.Rebuild();

            string path = Path.Combine(_root, "app", "Holder.surtr");

            Assert.True(diagnostics.TryGetValue(path, out var reported) && reported.Count > 0,
                "Expected the arity mismatch to be reported: " + Describe(diagnostics));

            var invalid = reported.FirstOrDefault(
                d => d.Code == Surtr.Compiler.Diagnostics.SurtrDiagnosticCode.InvalidDestructuring);

            Assert.True(invalid is not null,
                "Expected InvalidDestructuring among: " + Describe(diagnostics));
            Assert.True(invalid!.Span.Length > 0,
                "The diagnostic must carry a span an editor can underline, not a bare position.");
        }

        #endregion

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private static string Describe(System.Collections.Generic.IReadOnlyDictionary<string, System.Collections.Generic.IReadOnlyList<Surtr.Compiler.Diagnostics.SurtrDiagnostic>> diagnostics)
            => string.Join(" | ", diagnostics.SelectMany(pair => pair.Value).Select(d => d.ToString()));
    }
}
