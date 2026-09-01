#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Runtime.Testing;
using Surtr.VM;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Surtr.Tests.Compiler.CodeGen
{
    /// <summary>
    /// Covers Step 5 end to end: Surtr source becomes a real module, is loaded into a real runtime,
    /// and is run.
    /// </summary>
    /// <remarks>
    /// Nothing here stops at the bytecode. A test that asserted on an instruction sequence would
    /// pin the encoding rather than the meaning, and the encoding is the emitter's to choose — what
    /// has to hold is that the program computes what the source says it computes.
    /// </remarks>
    public sealed class ModuleEmitterTests : IDisposable
    {
        private const string Root = "D:/proj/src";

        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
        }

        private ModuleEmitter Build(string source, params (string Path, string Text)[] extra)
            => Build(source, defineDebug: false, extra);

        private ModuleEmitter Build(string source, bool defineDebug, params (string Path, string Text)[] extra)
        {
            var project = new SurtrProject(Root);
            if (defineDebug)
                project.Define("Debug", BuildConstant.Bool(true));
            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            foreach (var (path, text) in extra)
                project.AddSourceFile(Root + path, text);

            var compilation = SurtrCompilation.Create(project);
            _owned.Add(compilation);

            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(
                !compilation.HasErrors,
                "Binding reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var emitter = new ModuleEmitter(compilation, binder);

            Assert.True(
                emitter.TryEmit(),
                "Emission reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            return emitter;
        }

        private SurtrRuntime Load(ModuleEmitter emitter)
        {
            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            return runtime;
        }

        private SurtrRuntime Run(string source, params (string Path, string Text)[] extra) => Load(Build(source, extra));

        /// <summary>Builds and loads a module with the <c>Debug</c> constant defined, so checks on.</summary>
        private SurtrRuntime RunDebug(string source, params (string Path, string Text)[] extra)
            => Load(Build(source, defineDebug: true, extra));

        private static SurtrMethodInfo Function(SurtrRuntime runtime, string modulePath, string name)
        {
            Assert.True(runtime.TryGetModule(modulePath, out var module), $"No module '{modulePath}' was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"'{modulePath}' declares no '{name}'.");
            return overloads[0];
        }

        private static SurtrValue Call(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => runtime.Invoke(Function(runtime, "game.core.Test", name), arguments);

        private static int Int(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => Call(runtime, name, arguments).AsInt;

        private static string Text(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => runtime.Resolve<SurtrString>(Call(runtime, name, arguments))!.Text;

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Surtr.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate the repo root from " + AppContext.BaseDirectory);
        }

        #region A whole module
        [Fact]
        public void AModuleLevelFunctionRunsWhatItsSourceSays()
        {
            var runtime = Run("fun square(x: int): int { return x * x; }");
            Assert.Equal(49, Int(runtime, "square", SurtrValue.CreateInt(7)));
        }

        [Fact]
        public void OneFunctionCallsAnother()
        {
            var runtime = Run(
                "fun square(x: int): int { return x * x; }\n"
                    + "fun sumOfSquares(a: int, b: int): int { return square(a) + square(b); }");

            Assert.Equal(25, Int(runtime, "sumOfSquares", SurtrValue.CreateInt(3), SurtrValue.CreateInt(4)));
        }

        [Fact]
        public void AModuleVariableIsInitialisedByTheModulesOwnInitializer()
        {
            var runtime = Run("var counter: int = 41;\nfun bump(): int { counter = counter + 1; return counter; }");
            Assert.Equal(42, Int(runtime, "bump"));
        }

        [Fact]
        public void AModuleIsWrittenAsAnImageAndReadBack()
        {
            var emitter = Build("fun answer(): int { return 42; }");
            var images = emitter.EmitImages();

            Assert.Single(images);

            // A fresh runtime, from bytes alone: what makes the image the artefact rather than the
            // in-memory module.
            var reloaded = SurtrModuleImage.FromBytes(images[0].ToBytes());
            using var runtime = new SurtrRuntime();
            runtime.LoadModule(reloaded.Instantiate());

            Assert.Equal(42, runtime.Invoke(Function(runtime, "game.core.Test", "answer"), Array.Empty<SurtrValue>()).AsInt);
        }

        [Fact]
        public void AModuleReachesAnotherOneItDependsOn()
        {
            // `public` is load-bearing: §3.1 defaults a module-level declaration to `internal`, which
            // is exactly the module it is declared in.
            var runtime = Run(
                "import game.math.*;\nfun run(): int { return twice(21); }",
                ("/game/math/Math.surtr", "public fun twice(x: int): int { return x + x; }"));

            Assert.Equal(42, Int(runtime, "run"));
        }
        #endregion

        #region Import: alias de modulo (§2.1, Fase 7)
        [Fact]
        public void AModuleAliasConstructsATypeThroughTheAliasedName()
        {
            var runtime = Run(
                "import game.math.Box as M;\nfun run(): int { return M.Box(21).value; }",
                ("/game/math/Box.surtr", "public class Box { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.Equal(21, Int(runtime, "run"));
        }

        [Fact]
        public void AModuleAliasWorksInATypeAnnotation()
        {
            var runtime = Run(
                "import game.math.Box as M;\n"
                    + "fun run(): int { let b: M.Box = M.Box(7); return b.value; }",
                ("/game/math/Box.surtr", "public class Box { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>An alias only reaches its module's types qualified - it is not also a wildcard import.</summary>
        [Fact]
        public void AModuleAliasDoesNotBringTheUnqualifiedNameIntoScope()
        {
            using var compilation = Reject(
                "import game.math as M;\nfun run(): int { return Box(1).value; }",
                ("/game/math/Box.surtr", "public class Box { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.True(compilation.HasErrors, "'Box' should not be reachable unqualified through an alias-only import.");
        }

[Fact]
        public void TwoImportsCannotClaimTheSameAlias()
        {
using var compilation = Reject(
                  "import game.math.Box as M;\nimport game.other.Thing as M;\nfun run(): int { return 1; }",
                  ("/game/math/Box.surtr", "public class Box { public let value: int = 0; }"),
                  ("/game/other/Thing.surtr", "public class Thing { }"));

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.DuplicateModuleAlias);
        }
        #endregion

        #region Import: modulo completo (§2.1, import module)
        [Fact]
        public void AWholeModuleImportBringsItsModuleMembersUnqualified()
        {
            // `import module X.Y;` imports a whole module's surface � types and module-level
            // members alike � the way `import X.Y.*;` would, without recursing into submodules.
            var runtime = Run(
                "import module game.math.Math;\nfun run(): int { return add(2, 3); }",
                ("/game/math/Math.surtr", "public fun add(a: int, b: int): int { return a + b; }"));

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void AWholeModuleImportBringsItsTypesUnqualified()
        {
            var runtime = Run(
                "import module game.math.Box;\nfun run(): int { return Box(21).value; }",
                ("/game/math/Box.surtr", "public class Box { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.Equal(21, Int(runtime, "run"));
        }

        [Fact]
        public void AWholeModuleImportDoesNotReachASubmodule()
        {
            // Unlike a directory wildcard, `import module` names exactly one file's module and
            // stops there � a submodule is a different module.
            using var compilation = Reject(
                "import module game.math;\nfun run(): int { return add(2, 3); }",
                ("/game/math/Math.surtr", "public fun add(a: int, b: int): int { return a + b; }"));

            Assert.True(compilation.HasErrors, "`import module` should not recurse into submodules.");
        }

        [Fact]
        public void AWholeModuleImportStillAllowsTheModuleKeywordAsAQualifier()
        {
            // `module` is a contextual keyword after `import`; elsewhere it stays an ordinary
            // identifier, so `moduleof(no.such.module)` still parses.
            var runtime = Run(
                "import module game.math.Math;\nfun run(): Module { return moduleof(game.math.Math); }",
                ("/game/math/Math.surtr", "public fun add(a: int, b: int): int { return a + b; }"));

            var module = runtime.Invoke(Function(runtime, "game.core.Test", "run"), Array.Empty<SurtrValue>());
            Assert.False(module.IsNullReference);
        }
        #endregion

        #region Import: re-export (§2.1, export import)
        [Fact]
        public void AnExportImportReExposesTypesToAQualifiedConsumer()
        {
            // `export import module` in the aggregator folds the target's types into the
            // aggregator's own surface, so a module alias of the aggregator names a type declared
            // in Box.surtr.
            var runtime = Run(
                "import game.core.Index as I;\nfun run(): int { return I.Box(21).value; }",
                ("/game/math/Box.surtr", "public class Box { public let value: int = 0; public constructor(value: int) { this.value = value; } }"),
                ("/game/core/Index.surtr", "export import module game.math.Box;"));

            Assert.Equal(21, Int(runtime, "run"));
        }

        [Fact]
        public void AnExportImportReExposesModuleMembersToAWildcardConsumer()
        {
            // A consumer that imports the aggregator reaches everything it re-exported without
            // qualifying, exactly as if the aggregator had declared it.
            var runtime = Run(
                "import game.core.Index;\nfun run(): int { return add(2, 3); }",
                ("/game/math/Math.surtr", "public fun add(a: int, b: int): int { return a + b; }"),
                ("/game/core/Index.surtr", "export import module game.math.Math;"));

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void AnExportImportReExposesATypeToAClassField()
        {
            // A class field annotated with a type that an aggregator re-exported, reached by
            // importing the aggregator as a module � the type resolves and works at runtime.
            var runtime = Run(
                "import proj.core.Index;\nclass Holder { public var v: Vec2; public fun make(): int { let b = Vec2(7); return b.x; } }\nfun run(): int { return Holder().make(); }",
                ("/proj/math/Vec2.surtr", "public class Vec2 { public let x: int = 0; public constructor(x: int) { this.x = x; } }"),
                ("/proj/core/Index.surtr", "export import module proj.math.Vec2;"));

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AnExportImportFoldsTypesIntoTheAggregatorsSurface()
        {
            // `import game.core.Index.*` in a consumer brings the re-exported type in unqualified.
            var runtime = Run(
                "import game.core.Index;\nfun run(): int { return Box(21).value; }",
                ("/game/math/Box.surtr", "public class Box { public let value: int = 0; public constructor(value: int) { this.value = value; } }"),
                ("/game/core/Index.surtr", "export import module game.math.Box;"));

            Assert.Equal(21, Int(runtime, "run"));
        }

        [Fact]
        public void AReExportChainIsTransitive()
        {
            // Index re-exports Math; Math re-exports the primitive box. A consumer of Index sees
            // the whole chain.
            var runtime = Run(
                "import game.core.Index;\nfun run(): int { return add(2, 3); }",
                ("/game/math/Math.surtr", "export import module game.math.Numbers;\npublic fun add(a: int, b: int): int { return a + b; }"),
                ("/game/math/Numbers.surtr", "public fun twice(x: int): int { return x + x; }"),
                ("/game/core/Index.surtr", "export import module game.math.Math;"));

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void AReExportStillRespectsAccessibility()
        {
            // An internal member of a re-exported module is not widened by the re-export: only
            // what the declaring module already made public crosses the boundary.
            using var compilation = Reject(
                "import game.core.Index;\nfun run(): int { return add(2, 3); }",
                ("/game/math/Math.surtr", "fun add(a: int, b: int): int { return a + b; }"),
                ("/game/core/Index.surtr", "export import module game.math.Math;"));

            Assert.True(compilation.HasErrors, "an internal member should stay inaccessible across the re-export.");
        }

        [Fact]
        public void ANamedMemberImportBringsAModuleFunctionInUnqualified()
        {
            // �2.1's broader member import: a named import may name a module-level function, not
            // only a type.
            var runtime = Run(
                "import game.math.Math.add;\nfun run(): int { return add(2, 3); }",
                ("/game/math/Math.surtr", "public fun add(a: int, b: int): int { return a + b; }"));

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void ASelectiveMemberImportBringsOnlyTheListedMembers()
        {
            var runtime = Run(
                "import game.math.Math.{add};\nfun run(): int { return add(2, 3); }",
                ("/game/math/Math.surtr", "public fun add(a: int, b: int): int { return a + b; }\npublic fun sub(a: int, b: int): int { return a - b; }"));

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void ASelectiveMemberImportLeavesUnlistedMembersUnreachable()
        {
            using var compilation = Reject(
                "import game.math.Math.{add};\nfun run(): int { return sub(2, 1); }",
                ("/game/math/Math.surtr", "public fun add(a: int, b: int): int { return a + b; }\npublic fun sub(a: int, b: int): int { return a - b; }"));

            Assert.True(compilation.HasErrors, "'sub' should not be reachable through a selective import that left it out.");
        }

        [Fact]
        public void AnExportNamedMemberImportReExposesAFunctionToAConsumer()
        {
            var runtime = Run(
                "import game.core.Index;\nfun run(): int { return add(2, 3); }",
                ("/game/math/Math.surtr", "public fun add(a: int, b: int): int { return a + b; }"),
                ("/game/core/Index.surtr", "export import game.math.Math.add;"));

            Assert.Equal(5, Int(runtime, "run"));
        }
        #endregion

        #region Import: lista selectiva de miembros (§2.1, Fase 8)
        [Fact]
        public void ASelectiveImportBringsEveryListedNameIntoUnqualifiedScope()
        {
var runtime = Run(
                  "import game.math.Shapes.{Box, Pair};\n"
                      + "fun run(): int { return Box(3).value + Pair(4).value; }",
                  ("/game/math/Shapes.surtr",
                      "public class Box { public let value: int = 0; public constructor(value: int) { this.value = value; } }\n"
                          + "public class Pair { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>A name left off the list is not brought in, even though its sibling was.</summary>
        [Fact]
        public void ASelectiveImportLeavesOutAnUnlistedSibling()
        {
            using var compilation = Reject(
                "import game.math.{Box};\nfun run(): int { return Pair(1).value; }",
                ("/game/math/Shapes.surtr",
                    "public class Box { public let value: int = 0; public constructor(value: int) { this.value = value; } }\n"
                        + "public class Pair { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.True(compilation.HasErrors, "'Pair' should not be reachable - only 'Box' was listed.");
        }

        [Fact]
        public void ASelectiveImportWorksInATypeAnnotation()
        {
var runtime = Run(
                  "import game.math.Shapes.{Box};\n"
                      + "fun run(): int { let b: Box = Box(9); return b.value; }",
                  ("/game/math/Shapes.surtr", "public class Box { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.Equal(9, Int(runtime, "run"));
        }
        #endregion

        #region Import: wildcard de directorio recursivo (§2.1, Fase 9)
        /// <summary>
        /// `game.math` has no files of its own - only its submodules do - which is exactly the
        /// case the old exact-match-only wildcard could never resolve at all.
        /// </summary>
        [Fact]
        public void ADirectoryWildcardReachesEverySubmoduleWhenTheDirectoryHasNoFilesOfItsOwn()
        {
            var runtime = Run(
                "import game.math.*;\nfun run(): int { return Sin(3).value + Eq(4).value; }",
                ("/game/math/trig/Sin.surtr", "public class Sin { public let value: int = 0; public constructor(value: int) { this.value = value; } }"),
                ("/game/math/algebra/Eq.surtr", "public class Eq { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>The exact module's own declarations and its submodules' both come in - one does not shadow the other.</summary>
        [Fact]
        public void ADirectoryWildcardReachesBothTheModulesOwnTypesAndItsSubmodules()
        {
            var runtime = Run(
                "import game.math.*;\nfun run(): int { return Box(3).value + Sin(4).value; }",
                ("/game/math/Box.surtr", "public class Box { public let value: int = 0; public constructor(value: int) { this.value = value; } }"),
                ("/game/math/trig/Sin.surtr", "public class Sin { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>Recursion is not just one directory level deep.</summary>
        [Fact]
        public void ADirectoryWildcardReachesADeeplyNestedSubmodule()
        {
            var runtime = Run(
                "import game.math.*;\nfun run(): int { return Sin(5).value; }",
                ("/game/math/trig/hyperbolic/Sin.surtr", "public class Sin { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>A sibling directory (`game.other`) is not under the `game.math` prefix and must not leak in.</summary>
        [Fact]
        public void ADirectoryWildcardDoesNotReachASiblingModule()
        {
            using var compilation = Reject(
                "import game.math.*;\nfun run(): int { return Other(1).value; }",
                ("/game/math/trig/Sin.surtr", "public class Sin { public let value: int = 0; }"),
                ("/game/other/Other.surtr", "public class Other { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.True(compilation.HasErrors, "'game.other' is not nested under 'game.math' and must not be reachable.");
        }

        /// <summary>A wildcard's functions/variables reach unqualified too (§2.5), for a submodule exactly as for the exact module.</summary>
        [Fact]
        public void ADirectoryWildcardBringsASubmodulesFunctionsInToo()
        {
            var runtime = Run(
                "import game.math.*;\nfun run(): int { return twice(21); }",
                ("/game/math/trig/Trig.surtr", "public fun twice(x: int): int { return x + x; }"));

            Assert.Equal(42, Int(runtime, "run"));
        }
        #endregion

        #region Modulo por archivo con path explicito (§2.1)
        /// <summary>
        /// The stdlib keeps one module per file with the file name as the path's final segment, so
        /// §2.1's directory derivation cannot name them and each file is told its module outright.
        /// A module declared that way is a module like any other: one sibling can import it by its
        /// whole path and reach its types. Regression for the stdlib build, where `List.surtr`'s
        /// `import surtr.collections.Collection;` resolved against nothing but the built-in `surtr`
        /// module and silently imported no `IReadOnlyCollection`.
        /// </summary>
        [Fact]
        public void AFileWithAnExplicitModulePathCanBeImportedByASibling()
        {
            var project = new SurtrProject(Root, rootModulePath: "surtr");
            project.AddSourceFile(
                Root + "/surtr/collections/Collection.surtr",
                "surtr.collections.Collection",
                "public interface IReadOnlyCollection<T> : IIterable<T>\n"
                    + "{\n"
                    + "    length: int { get; }\n"
                    + "    fun get(index: int): T;\n"
                    + "    fun contains(item: T): bool;\n"
                    + "}\n"
                    + "public interface ICollection<T> : IReadOnlyCollection<T>\n"
                    + "{\n"
                    + "    fun add(item: T): void;\n"
                    + "}");
            project.AddSourceFile(
                Root + "/surtr/collections/List.surtr",
                "surtr.collections.List",
                "import surtr.collections.Collection;\n"
                    + "\n"
                    + "public interface IReadOnlyList<T> : IReadOnlyCollection<T>\n"
                    + "{\n"
                    + "    length: int { get; }\n"
                    + "    fun get(index: int): T;\n"
                    + "}");

            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.False(
                compilation.HasErrors,
                "Binding reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var emitter = new ModuleEmitter(compilation, binder);
            Assert.True(
                emitter.TryEmit(),
                "Emission reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);
            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            Assert.True(runtime.TryGetModule("surtr.collections.Collection", out _));
            Assert.True(runtime.TryGetModule("surtr.collections.List", out var listModule));

            // The import is not a facade: `IReadOnlyList`'s declared parent is the very
            // `IReadOnlyCollection` the sibling module declares, resolved across the module boundary.
            Assert.True(listModule.TryGetInterface("IReadOnlyList`1", out var readOnlyList));
            Assert.Equal(1, readOnlyList.DeclaredExtendedInterfaceHandles.Length);
            var parent = readOnlyList.DeclaredExtendedInterfaceHandles[0].ResolvedType as SurtrInterface;
            Assert.NotNull(parent);
            Assert.True(
                parent!.SelfReference.TryGetFullName(out string fullName)
                && fullName.StartsWith("surtr.collections.Collection:", StringComparison.Ordinal));
        }

        /// <summary>
        /// The stdlib's own <c>LinkedList&lt;T&gt;</c> — the exact sources the tool compiles —
        /// runs against the extended contract the import enabled. Because
        /// <c>IReadOnlyList&lt;T&gt; : IReadOnlyCollection&lt;T&gt;</c> now holds, the class must
        /// implement <c>contains</c>/<c>copyTo</c>/<c>iterate</c> beside the original members, so
        /// this exercises all of it at runtime rather than only asserting that it compiled.
        /// </summary>
        [Fact]
        public void TheStdlibLinkedListRunsThroughItsExtendedContract()
        {
            string collections = RepoRoot() + "/src/Surtr.Stdlib/src/surtr/collections";
            string collectionSource = File.ReadAllText(collections + "/Collection.surtr");
            string listSource = File.ReadAllText(collections + "/List.surtr");

            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "import surtr.collections.List;\n"
                    + "fun run(): int {\n"
                    + "    var list = LinkedList<int>();\n"
                    + "    list.add(10); list.add(20); list.add(30);\n"
                    + "    if (list.length != 3) return 1;\n"
                    + "    if (!list.contains(20)) return 2;\n"
                    + "    if (list.contains(99)) return 3;\n"
                    + "    var sum = 0;\n"
                    + "    for (var item in list) sum = sum + item;\n"
                    + "    if (sum != 60) return 4;\n"
                    + "    var target = [0, 0, 0];\n"
                    + "    list.copyTo(target, 0);\n"
                    + "    if (target[0] != 10 || target[1] != 20 || target[2] != 30) return 5;\n"
                    + "    list.removeAt(1);\n"
                    + "    if (list.length != 2 || list[1] != 30) return 6;\n"
                    + "    list.clear();\n"
                    + "    if (list.length != 0) return 7;\n"
                    + "    return 0;\n"
                    + "}");
            project.AddSourceFile(
                Root + "/surtr/collections/Collection.surtr", "surtr.collections.Collection", collectionSource);
            project.AddSourceFile(
                Root + "/surtr/collections/List.surtr", "surtr.collections.List", listSource);

            var compilation = SurtrCompilation.Create(project);
            _owned.Add(compilation);

            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(
                !compilation.HasErrors,
                "Binding reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var emitter = new ModuleEmitter(compilation, binder);

            Assert.True(
                emitter.TryEmit(),
                "Emission reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var runtime = Load(emitter);
            Assert.Equal(0, Call(runtime, "run").AsInt);
        }

        /// <summary>
        /// Regression: iterating a <c>Sequence&lt;T&gt;</c> (a value class) with <c>for-in</c> used
        /// to crash the VM with "SurtrNativeArray index out of range" because the loop's value was
        /// the erased closure field, which <c>InvokeInterface</c> could not dispatch on. The emitter
        /// now boxes the receiver first, and <c>Sequence&lt;T&gt;</c> implements <c>IIterable&lt;T&gt;</c>
        /// so the boxed form has the slot to answer.
        /// </summary>
        [Fact]
        public void AForInOverASequenceValueClassWorks()
        {
            string collections = RepoRoot() + "/src/Surtr.Stdlib/src/surtr/collections";
            string collectionSource = File.ReadAllText(collections + "/Collection.surtr");
            string listSource = File.ReadAllText(collections + "/List.surtr");
            string sequenceSource = File.ReadAllText(collections + "/Sequence.surtr");

            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "import surtr.collections.Sequence;\n"
                    + "fun run(): int {\n"
                    + "    var sum = 0;\n"
                    + "    for (x in Sequence<int>.of(1, 2, 3)) sum = sum + x;\n"
                    + "    if (sum != 6) return 1;\n"
                    + "    var emptySum = 0;\n"
                    + "    for (x in Sequence<int>.empty) emptySum = emptySum + x;\n"
                    + "    if (emptySum != 0) return 2;\n"
                    + "    if (Sequence<int>.empty.count() != 0) return 3;\n"
                    + "    return 0;\n"
                    + "}");
            project.AddSourceFile(
                Root + "/surtr/collections/Collection.surtr", "surtr.collections.Collection", collectionSource);
            project.AddSourceFile(
                Root + "/surtr/collections/List.surtr", "surtr.collections.List", listSource);
            project.AddSourceFile(
                Root + "/surtr/collections/Set.surtr",
                "surtr.collections.Set",
                File.ReadAllText(collections + "/Set.surtr"));
            project.AddSourceFile(
                Root + "/surtr/collections/Map.surtr",
                "surtr.collections.Map",
                File.ReadAllText(collections + "/Map.surtr"));
            project.AddSourceFile(
                Root + "/surtr/collections/Sequence.surtr", "surtr.collections.Sequence", sequenceSource);

            var compilation = SurtrCompilation.Create(project);
            _owned.Add(compilation);

            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(
                !compilation.HasErrors,
                "Binding reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var emitter = new ModuleEmitter(compilation, binder);

            Assert.True(
                emitter.TryEmit(),
                "Emission reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var runtime = Load(emitter);
            Assert.Equal(0, Call(runtime, "run").AsInt);
        }
        #endregion

        #region Import: modulo completo sin wildcard (§2.1)
        /// <summary>Regression: a real trailing type name still wins - this phase must not change it.</summary>
        [Fact]
        public void ANamedImportWithARealTrailingTypeStillWorks()
        {
            var runtime = Run(
                "import game.entities.Foo;\nfun run(): int { return Foo(9).n; }",
                ("/game/entities/Foo.surtr", "public class Foo { public let n: int = 0; public constructor(n: int) { this.n = n; } }"));

            Assert.Equal(9, Int(runtime, "run"));
        }

        /// <summary>A bare `import ModulePath;` over a real directory module brings its own declarations in, exactly like `import ModulePath.*;`.</summary>
        [Fact]
        public void ABareModuleImportBringsInTheModulesOwnDeclarations()
        {
            var runtime = Run(
                "import game.entities;\nfun run(): int { return Foo(3).n; }",
                ("/game/entities/Foo.surtr", "public class Foo { public let n: int = 0; public constructor(n: int) { this.n = n; } }"));

            Assert.Equal(3, Int(runtime, "run"));
        }

        /// <summary>`game.entities` has no files of its own - only its submodules do - same recursive case that motivated Fase 9's wildcard.</summary>
        [Fact]
        public void ABareModuleImportReachesEverySubmoduleWhenTheDirectoryHasNoFilesOfItsOwn()
        {
            var runtime = Run(
                "import game.entities;\nfun run(): int { return Sin(3).value + Eq(4).value; }",
                ("/game/entities/trig/Sin.surtr", "public class Sin { public let value: int = 0; public constructor(value: int) { this.value = value; } }"),
                ("/game/entities/algebra/Eq.surtr", "public class Eq { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>A bare module import's functions/variables reach unqualified too, same as the wildcard's.</summary>
        [Fact]
        public void ABareModuleImportBringsTheModulesFunctionsInToo()
        {
            var runtime = Run(
                "import game.entities;\nfun run(): int { return twice(21); }",
                ("/game/entities/Entities.surtr", "public fun twice(x: int): int { return x + x; }"));

            Assert.Equal(42, Int(runtime, "run"));
        }

        /// <summary>A sibling directory does not leak in through a bare module import.</summary>
        [Fact]
        public void ABareModuleImportDoesNotReachASiblingModule()
        {
            using var compilation = Reject(
                "import game.entities;\nfun run(): int { return Other(1).value; }",
                ("/game/entities/Foo.surtr", "public class Foo { public let n: int = 0; }"),
                ("/game/other/Other.surtr", "public class Other { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.True(compilation.HasErrors, "'game.other' is not nested under 'game.entities' and must not be reachable.");
        }

        /// <summary>
        /// When the whole path resolves as a module AND a shorter prefix + trailing type of the
        /// same name would also resolve, the longest prefix (the whole path, as a module) wins -
        /// same rule the split loop already used for two module prefixes of different lengths.
        /// </summary>
        [Fact]
        public void TheWholePathAsAModuleWinsOverAShorterPrefixWithATypeOfTheSameName()
        {
            var runtime = Run(
                "import game.entities;\nfun run(): int { return Foo(5).n; }",
                // `game` also declares a type literally named `entities` - it must lose to the
                // longer prefix, `game.entities` resolving as a module in its own right.
                ("/game/Shadow.surtr", "public class entities { public let n: int = 999; }"),
                ("/game/entities/Foo.surtr", "public class Foo { public let n: int = 0; public constructor(n: int) { this.n = n; } }"));

            Assert.Equal(5, Int(runtime, "run"));
        }
        #endregion

        #region moduleof (§2.1)
        [Fact]
        public void ModuleOfOnTheCurrentModuleCompilesAndRuns()
        {
            var runtime = Run("fun run(): int { let m: Module = moduleof(game.core.Test); return 1; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ModuleOfOnAnotherModuleCompilesAndRuns()
        {
            var runtime = Run(
                "import game.entities.Foo;\nfun run(): int { let m: Module = moduleof(game.entities.Foo); return Foo(5).n; }",
                ("/game/entities/Foo.surtr", "public class Foo { public let n: int = 0; public constructor(n: int) { this.n = n; } }"));

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>No `import` at all - only `moduleof` crosses the module boundary, which has to add its own dependency edge for load order to come out right.</summary>
        [Fact]
        public void ModuleOfAloneCreatesADependencyEdgeWithNoImport()
        {
            var runtime = Run(
                "fun run(): int { let m: Module = moduleof(game.entities.Foo); return 1; }",
                ("/game/entities/Foo.surtr", "public class Foo { public let n: int = 0; }"));

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ModuleOfThroughAnAliasResolvesTheAliasedModule()
        {
            var runtime = Run(
                "import game.entities.Foo as GE;\nfun run(): int { let m: Module = moduleof(GE); return 1; }",
                ("/game/entities/Foo.surtr", "public class Foo { public let n: int = 0; }"));

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>The runtime caches one `Module` value per `SurtrModule`, the same as `Type`.</summary>
        [Fact]
        public void ModuleOfOnTheSameModuleTwiceReturnsTheSameValue()
        {
            var runtime = Run(
                "fun a(): Module { return moduleof(game.core.Test); }\nfun b(): Module { return moduleof(game.core.Test); }");

            var first = Call(runtime, "a");
            var second = Call(runtime, "b");

            Assert.Equal(first.AsReference, second.AsReference);
        }

        [Fact]
        public void ModuleOfOnAnUnknownPathReportsADiagnostic()
        {
            using var compilation = Reject("fun run(): int { let m: Module = moduleof(no.such.module); return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnresolvedModuleOf);
        }

        [Fact]
        public void ModulePathReturnsTheDottedPath()
        {
            var runtime = Run("fun run(): string { return moduleof(game.core.Test).path; }");

            Assert.Equal("game.core.Test", Text(runtime, "run"));
        }

        [Fact]
        public void ModuleClassesEnumeratesItsOwnDeclaredClasses()
        {
            var runtime = Run("class Foo { }\nclass Bar { }\nfun run(): int { return moduleof(game.core.Test).classes().length; }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void ModuleInterfacesEnumeratesItsOwnDeclaredInterfaces()
        {
            var runtime = Run("interface Named { }\nfun run(): int { return moduleof(game.core.Test).interfaces().length; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ModuleMembersIncludesAFunctionAndAVariableButNotAClass()
        {
            // Four, not two: `bump` and the field, but also `run` itself (a function of this same
            // module) and the synthesised module initializer ("cinit", needed because `counter` has
            // one) - `Type.members()` already includes a synthesised constructor the same way
            // (documented as appearing under `ctor`), so this mirrors established behaviour rather
            // than filtering it out.
            var runtime = Run(
                "var counter: int = 0;\nfun bump(): int { return 1; }\nclass Foo { }\n"
                    + "fun run(): int { return moduleof(game.core.Test).members().length; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void ModuleSubmodulesReachesANestedModule()
        {
            var runtime = Run(
                "fun run(): int { return moduleof(game.core.Test).submodules().length; }",
                ("/game/core/Test/deep/Deep.surtr", "public class Deep { }"));

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ModuleGetFindsALoadedModuleByPath()
        {
            var runtime = Run(
                "import game.entities.Foo;\nfun run(): string { return Module.get(\"game.entities.Foo\").path; }",
                ("/game/entities/Foo.surtr", "public class Foo { }"));

            Assert.Equal("game.entities.Foo", Text(runtime, "run"));
        }

        [Fact]
        public void ModuleGetFindsTheBuiltInModuleByItsReservedPath()
        {
            var runtime = Run("fun run(): string { return Module.get(\"surtr\").path; }");

            Assert.Equal("surtr", Text(runtime, "run"));
        }

        [Fact]
        public void ModuleTryGetReturnsNullForAnUnknownPath()
        {
            var runtime = Run(
                "fun run(): int { if (Module.tryGet(\"no.such.module\") == null) { return 1; } return 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ModuleGetThrowsForAnUnknownPath()
        {
            var runtime = Run("fun run(): Module { return Module.get(\"no.such.module\"); }");

            // Uncaught and native-thrown, with no Surtr handler anywhere on the stack to search -
            // the trap-to-class mapping only rewrites what a `catch` actually looks for, so this
            // escapes as the CLR exception itself rather than as SurtrThrownException.
            Assert.Throws<KeyNotFoundException>(() => Call(runtime, "run"));
        }

        [Fact]
        public void TypeGetResolvesAUserClassByDescriptor()
        {
            var runtime = Run("class Foo { }\nfun run(): string { return Type.get(\"Ogame.core.Test:Foo;\").name; }");

            Assert.Equal("Foo", Text(runtime, "run"));
        }

        [Fact]
        public void TypeGetResolvesAPrimitiveByDescriptor()
        {
            var runtime = Run("fun run(): string { return Type.get(\"I\").name; }");

            Assert.Equal("int", Text(runtime, "run"));
        }

        [Fact]
        public void TypeTryGetReturnsNullForAnUnknownDescriptor()
        {
            var runtime = Run(
                "fun run(): int { if (Type.tryGet(\"Ogame.core.Test:NoSuchType;\") == null) { return 1; } return 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void TypeGetThrowsForAnUnknownDescriptor()
        {
            var runtime = Run("fun run(): Type { return Type.get(\"Ogame.core.Test:NoSuchType;\"); }");

            Assert.Throws<KeyNotFoundException>(() => Call(runtime, "run"));
        }
        #endregion

        #region Gaps closed after the Language-Syntax.md audit (§2.2, §2.4, §3.2, §4.2, §9)
        [Fact]
        public void AbstractAndSealedTogetherOnAClassIsRejected()
        {
            // 'sealed' before 'abstract', matching §3.2's canonical order - so this reaches the
            // semantic abstract+sealed check rather than the (separate, also real) order check.
            using var compilation = Reject("sealed abstract class Foo { }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidClassModifiers);
        }

        [Fact]
        public void AnEnumDeclaringAClassBaseIsRejected()
        {
            using var compilation = Reject(
                "class NotAnInterface { }\nenum Suit : NotAnInterface { Hearts, Spades }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidEnumBase);
        }

        /// <summary>Regression: an enum with no members after its cases still needs no ';'.</summary>
        [Fact]
        public void AnEnumWithNoMembersStillNeedsNoSemicolon()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\nfun run(): int { return 1; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AnEnumMissingTheSemicolonBeforeItsMembersIsRejected()
        {
            using var compilation = Reject(
                "enum Suit { Hearts, Spades\n  public fun describe(): string { return \"x\"; }\n}\nfun run(): int { return 1; }");

            Assert.True(compilation.HasErrors, "A member after an enum's case list with no ';' should be rejected, not silently misparsed as another case.");
        }

        [Fact]
        public void AnEnumWithASemicolonBeforeItsMembersCompiles()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades;\n  public fun describe(): string { return \"x\"; }\n}\n"
                    + "fun run(): string { return Suit.Hearts.describe(); }");

            Assert.Equal("x", Text(runtime, "run"));
        }

        [Fact]
        public void LetInTheClassicForHeaderIsRejected()
        {
            using var compilation = Reject(
                "fun run(): int { for (let i = 0; i < 3; i += 1) { } return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidForLoopBinding);
        }

        [Fact]
        public void VarInTheClassicForHeaderStillCompiles()
        {
            var runtime = Run(
                "fun run(): int { var total = 0; for (var i = 0; i < 3; i += 1) { total += i; } return total; }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        [Fact]
        public void ThrowingSomethingThatDoesNotExtendExceptionIsRejected()
        {
            using var compilation = Reject(
                "class NotAnException { }\nfun run(): int { throw NotAnException(); }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidThrowableType);
        }

        [Fact]
        public void CatchingSomethingThatDoesNotExtendExceptionIsRejected()
        {
            using var compilation = Reject(
                "class NotAnException { }\n"
                    + "fun run(): int { try { } catch (e: NotAnException) { } return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidThrowableType);
        }

        [Fact]
        public void ThrowingARealExceptionSubclassStillCompiles()
        {
            var runtime = Run(
                "class MyException : Exception {\n"
                    + "  public constructor(message: string) : super(message) { }\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "  try { throw MyException(\"boom\"); } catch (e: MyException) { return 1; }\n"
                    + "  return 0;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AThrowExpressionFillsTheFalseBranchOfAConditional()
        {
            // §9: `throw` is an expression typed `never`, so a branch of `?:` can be a throw and
            // the conditional still has the other branch's type.
            var runtime = Run(
                "class MyException : Exception {\n"
                    + "  public constructor(message: string) : super(message) { }\n"
                    + "}\n"
                    + "fun pick(cond: bool): int { return cond ? 7 : throw MyException(\"boom\"); }\n"
                    + "fun run(): int {\n"
                    + "  try { return pick(true); } catch (e: MyException) { return 2; }\n"
                    + "}");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AThrowExpressionOnTheThrowingSideOfAConditionalEscapes()
        {
            var runtime = Run(
                "class MyException : Exception {\n"
                    + "  public constructor(message: string) : super(message) { }\n"
                    + "}\n"
                    + "fun pick(cond: bool): int { return cond ? 7 : throw MyException(\"boom\"); }\n"
                    + "fun run(): int {\n"
                    + "  try { return pick(false); } catch (e: MyException) { return 3; }\n"
                    + "}");

            Assert.Equal(3, Int(runtime, "run"));
        }

        [Fact]
        public void AThrowExpressionIsTheRightOperandOfNullCoalesce()
        {
            // §9: `??`'s right operand can be a throw; the whole expression takes the left's
            // non-nullable type.
            var runtime = Run(
                "class MyException : Exception {\n"
                    + "  public constructor(message: string) : super(message) { }\n"
                    + "}\n"
                    + "fun guarded(value: int?): int { return value ?? throw MyException(\"boom\"); }\n"
                    + "fun run(): int {\n"
                    + "  try { return guarded(5); } catch (e: MyException) { return 2; }\n"
                    + "}");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void AThrowExpressionReachesACatchFromNullCoalesce()
        {
            var runtime = Run(
                "class MyException : Exception {\n"
                    + "  public constructor(message: string) : super(message) { }\n"
                    + "}\n"
                    + "fun guarded(value: int?): int { return value ?? throw MyException(\"boom\"); }\n"
                    + "fun run(): int {\n"
                    + "  try { return guarded(null); } catch (e: MyException) { return 9; }\n"
                    + "}");

            Assert.Equal(9, Int(runtime, "run"));
        }

        [Fact]
        public void AnExpressionLambdasBodyMayBeAThrow()
        {
            // A lambda `() => throw E` is a closure whose body never completes; invoking it
            // surfaces the exception.
            var runtime = Run(
                "class MyException : Exception {\n"
                    + "  public constructor(message: string) : super(message) { }\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "  let explode = (): int => throw MyException(\"boom\");\n"
                    + "  try { return explode(); } catch (e: MyException) { return 4; }\n"
                    + "}");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void AMethodDeclaredNeverDoesNotNeedToReturn()
        {
            // `never` as a written return type: a body that only throws satisfies it, and
            // NotAllPathsReturn is not reported.
            var runtime = Run(
                "class MyException : Exception {\n"
                    + "  public constructor(message: string) : super(message) { }\n"
                    + "}\n"
                    + "fun fail(): never { throw MyException(\"boom\"); }\n"
                    + "fun run(): int {\n"
                    + "  try { return fail(); } catch (e: MyException) { return 6; }\n"
                    + "}");

            Assert.Equal(6, Int(runtime, "run"));
        }

        [Fact]
        public void AThrowExpressionInAReturnStillChecksTheThrownType()
        {
            using var compilation = Reject(
                "class NotAnException { }\nfun run(): int { return true ? 1 : throw NotAnException(); }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidThrowableType);
        }

        [Fact]
        public void AThrowExpressionDoesNotMakeFollowingCodeUnreachable()
        {
            // Flow analysis joins the branches of `?:`: a throw in the false branch must not mark
            // the statement after the conditional unreachable.
            var runtime = Run(
                "class MyException : Exception {\n"
                    + "  public constructor(message: string) : super(message) { }\n"
                    + "}\n"
                    + "fun pick(cond: bool): int {\n"
                    + "  let v = cond ? 1 : throw MyException(\"boom\");\n"
                    + "  return v + 1;\n"
                    + "}\n"
                    + "fun run(): int { return pick(true); }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void ModifiersOutOfOrderAreRejected()
        {
            using var compilation = Reject("class Foo { static public fun bar(): int { return 1; } }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidModifier);
        }

        [Fact]
        public void ModifiersInCanonicalOrderStillCompile()
        {
            var runtime = Run(
                "class Foo { public static fun bar(): int { return 1; } }\n"
                    + "fun run(): int { return Foo.bar(); }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void HidingAVirtualMemberWithoutOverrideIsRejected()
        {
            using var compilation = Reject(
                "class Animal { public virtual fun speak(): string { return \"...\"; } }\n"
                    + "class Dog : Animal { public fun speak(): string { return \"Woof\"; } }\n"
                    + "fun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.MissingOverride);
        }

        [Fact]
        public void HidingAVirtualMemberWithDifferentReturnTypeStillRequiresOverride()
        {
            // The runtime places vtable slots by name plus parameter types, return type deliberately
            // excluded (SignatureKey). So a derived member that shares name and parameter shape with
            // a base virtual collides with that slot even when its return type differs — the binding
            // must demand `override` here, or the linker collapses the two at load time.
            using var compilation = Reject(
                "class Animal { public virtual fun speak(): string { return \"...\"; } }\n"
                    + "class Dog : Animal { public fun speak(): int { return 1; } }\n"
                    + "fun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.MissingOverride);
        }

        [Fact]
        public void OverrideWithDifferentReturnTypeIsAcceptedWhenMarkedOverride()
        {
            // `override` makes the intent explicit, so the return-type difference is fine — the
            // member takes the base's slot deliberately.
            using var compilation = Reject(
                "class Animal { public virtual fun speak(): string { return \"...\"; } }\n"
                    + "class Dog : Animal { public override fun speak(): int { return 1; } }\n"
                    + "fun run(): int { return 1; }");

            Assert.DoesNotContain(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.MissingOverride);
        }

        [Fact]
        public void OverridingAVirtualMemberWithOverrideStillCompiles()
        {
            var runtime = Run(
                "class Animal { public virtual fun speak(): string { return \"...\"; } }\n"
                    + "class Dog : Animal { public override fun speak(): string { return \"Woof\"; } }\n"
                    + "fun run(): string { let a: Animal = Dog(); return a.speak(); }");

            Assert.Equal("Woof", Text(runtime, "run"));
        }

        /// <summary>A different signature is not hiding anything, so no 'override' is owed.</summary>
        [Fact]
        public void ADifferentSignatureIsAnOverloadNotAHiddenOverride()
        {
            var runtime = Run(
                "class Animal { public virtual fun speak(): string { return \"...\"; } }\n"
                    + "class Dog : Animal { public fun speak(loudly: bool): string { return \"Woof\"; } }\n"
                    + "fun run(): string { return Dog().speak(true); }");

            Assert.Equal("Woof", Text(runtime, "run"));
        }

        /// <summary>Hiding a non-virtual (Direct) base member needs no 'override' - there is no vtable slot to silently miss.</summary>
        [Fact]
        public void HidingADirectBaseMemberNeedsNoOverride()
        {
            var runtime = Run(
                "class Animal { public fun speak(): string { return \"...\"; } }\n"
                    + "class Dog : Animal { public fun speak(): string { return \"Woof\"; } }\n"
                    + "fun run(): string { return Dog().speak(); }");

            Assert.Equal("Woof", Text(runtime, "run"));
        }

        [Fact]
        public void ANonTrailingDefaultParameterIsRejected()
        {
            using var compilation = Reject("fun f(a: int = 1, b: string): void { }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidParameterList);
        }

        [Fact]
        public void ANonTrailingVarargsParameterIsRejected()
        {
            using var compilation = Reject("fun f(a: int..., b: string): void { }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidParameterList);
        }

        [Fact]
        public void TwoVarargsParametersAreRejected()
        {
            using var compilation = Reject("fun f(a: int..., b: string...): void { }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidParameterList);
        }

        [Fact]
        public void AVarargsParameterWithADefaultIsRejected()
        {
            using var compilation = Reject("fun f(a: int... = 1): void { }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidParameterList);
        }

        [Fact]
        public void APositionalArgumentAfterANamedOneIsRejected()
        {
            using var compilation = Reject(
                "fun spawn(x: float, y: float): int { return 1; }\nfun run(): int { return spawn(x: 1.0, 2.0); }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.PositionalArgumentAfterNamed);
        }

        [Fact]
        public void TrailingDefaultsAndACallMixingPositionalThenNamedStillCompile()
        {
            var runtime = Run(
                "fun spawn(x: float, y: float, hp: int = 100): int { return hp; }\n"
                    + "fun run(): int { return spawn(1.0, y: 2.0); }");

            Assert.Equal(100, Int(runtime, "run"));
        }

        [Fact]
        public void ANestedTypeCannotNameItsContainersTypeParameter()
        {
            using var compilation = Reject(
                "class Box<T> {\n  class Entry { public let x: T; }\n}\nfun run(): int { return 1; }");

            Assert.True(compilation.HasErrors, "'T' belongs to Box, not Box.Entry - the static-nested rule (§6) says Entry cannot name it.");
        }

        [Fact]
        public void ANestedTypeWithItsOwnTypeParameterOfTheSameNameStillCompiles()
        {
            var runtime = Run(
                "class Box<T> {\n"
                    + "  class Entry<T> { public let x: T; public constructor(x: T) { this.x = x; } }\n"
                    + "  public fun wrap(value: T): Entry<T> { return Entry<T>(value); }\n"
                    + "}\n"
                    + "fun run(): int { return Box<int>().wrap(7).x; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>
        /// Regression: nested type names used to be registered in the container's outside scope, so
        /// two containers with a same-named nested type collided there - a body inside C could
        /// resolve B to A's B (reported as private) or to an ambiguity. Each container's body must
        /// find its own.
        /// </summary>
        [Fact]
        public void TwoContainersWithASameNamedNestedTypeEachResolveTheirOwn()
        {
            var runtime = Run(
                "class A\n"
                    + "{\n"
                    + "  private class B { public fun tag(): int { return 1; } }\n"
                    + "  public fun run(): int { return B().tag(); }\n"
                    + "}\n"
                    + "class C\n"
                    + "{\n"
                    + "  private class B { public fun tag(): int { return 2; } }\n"
                    + "  public fun run(): int { return B().tag(); }\n"
                    + "}\n"
                    + "fun runA(): int { return A().run(); }\n"
                    + "fun runC(): int { return C().run(); }");

            Assert.Equal(1, Int(runtime, "runA"));
            Assert.Equal(2, Int(runtime, "runC"));
        }

        /// <summary>
        /// §2.6: a nested type is named from outside through its container, so a bare nested name
        /// must not answer at module level. The old registration flattened nested names into the
        /// module scope, which made this compile.
        /// </summary>
        [Fact]
        public void ANestedTypeNameIsNotVisibleOutsideItsContainer()
        {
            using var compilation = Reject(
                "class Outer { class Inner { } }\nfun run(): int { let x: Inner? = null; return 1; }");

            Assert.True(compilation.HasErrors, "Inner belongs to Outer and must not answer to a bare name at module level.");
        }

        /// <summary>
        /// The static-nested rule (§6) keeps a container's type parameters out of a nested type's
        /// body, but a nested type still sees its siblings - the two live in separate scopes, which
        /// is exactly the split the same-named-nested-type fix depends on.
        /// </summary>
        [Fact]
        public void ANestedTypeSeesItsSiblingsButNotItsContainersParameters()
        {
            var runtime = Run(
                "class Outer<T>\n"
                    + "{\n"
                    + "  class Helper { public fun tag(): int { return 5; } }\n"
                    + "  class User { public fun use(): int { return Helper().tag(); } }\n"
                    + "  public fun run(): int { return User().use(); }\n"
                    + "}\n"
                    + "fun run(): int { return Outer<int>().run(); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void SealedThenOverrideIsTheOnlyAcceptedOrderForBoth()
        {
            var runtime = Run(
                "class Animal { public virtual fun speak(): string { return \"...\"; } }\n"
                    + "class Dog : Animal { public sealed override fun speak(): string { return \"Woof\"; } }\n"
                    + "fun run(): string { let a: Animal = Dog(); return a.speak(); }");

            Assert.Equal("Woof", Text(runtime, "run"));
        }
        #endregion

        #region Classes
        [Fact]
        public void AClassIsConstructedAndItsFieldsRead()
        {
            var runtime = Run(
                "class Point {\n"
                    + "  public var x: int;\n"
                    + "  public var y: int;\n"
                    + "  public constructor(x: int, y: int) { this.x = x; this.y = y; }\n"
                    + "  public fun sum(): int { return this.x + this.y; }\n"
                    + "}\n"
                    + "fun run(): int { let p = Point(3, 4); return p.sum(); }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AnInstanceFieldInitializerRunsFromEveryConstructor()
        {
            var runtime = Run(
                "class Counter {\n"
                    + "  public var value: int = 10;\n"
                    + "  public constructor() { this.value = this.value + 5; }\n"
                    + "}\n"
                    + "fun run(): int { return Counter().value; }");

            Assert.Equal(15, Int(runtime, "run"));
        }

        [Fact]
        public void AClassWithInitializersAndNoConstructorStillGetsThem()
        {
            var runtime = Run(
                "class Defaults { public var value: int = 7; }\nfun run(): int { return Defaults().value; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AStaticFieldIsInitialisedBeforeAnythingReadsIt()
        {
            var runtime = Run(
                "class Config { public static var limit: int = 99; }\nfun run(): int { return Config.limit; }");

            Assert.Equal(99, Int(runtime, "run"));
        }

        [Fact]
        public void AnAutoPropertyReadsAndWritesItsBackingField()
        {
            var runtime = Run(
                "class Player {\n"
                    + "  public health: int { get; set; }\n"
                    + "}\n"
                    + "fun run(): int { let p = Player(); p.health = 33; return p.health; }");

            Assert.Equal(33, Int(runtime, "run"));
        }

        [Fact]
        public void AWrittenAccessorBodyIsWhatRuns()
        {
            var runtime = Run(
                "class Box {\n"
                    + "  public var raw: int = 4;\n"
                    + "  public doubled: int { get { return this.raw * 2; } }\n"
                    + "}\n"
                    + "fun run(): int { return Box().doubled; }");

            Assert.Equal(8, Int(runtime, "run"));
        }

        [Fact]
        public void AVirtualCallLandsOnTheOverride()
        {
            var runtime = Run(
                "class Shape { public virtual fun sides(): int { return 0; } }\n"
                    + "class Square : Shape { public override fun sides(): int { return 4; } }\n"
                    + "fun run(): int { let s: Shape = Square(); return s.sides(); }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void ASuperCallReachesTheBaseImplementation()
        {
            var runtime = Run(
                "class Shape { public virtual fun sides(): int { return 3; } }\n"
                    + "class Square : Shape { public override fun sides(): int { return super.sides() + 1; } }\n"
                    + "fun run(): int { return Square().sides(); }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        /// <summary>
        /// The property-read twin of <see cref="ASuperCallReachesTheBaseImplementation"/>. Before
        /// devirtualisation reached property accessors, <c>super.n</c> here still dispatched
        /// virtually with a <c>Square</c> receiver — reaching <c>Square.n</c>'s own getter again
        /// instead of <c>Shape.n</c>'s, which either answers 5 (self-recursion happening to read a
        /// field first) or never returns, rather than the 4 a genuine base call gives.
        /// </summary>
        [Fact]
        public void ASuperPropertyReadReachesTheBaseImplementation()
        {
            var runtime = Run(
                "class Shape { public virtual n: int { get { return 3; } } }\n"
                    + "class Square : Shape { public override n: int { get { return super.n + 1; } } }\n"
                    + "fun run(): int { return Square().n; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        /// <summary>Value correctness for <see cref="LoweringChoiceTests.ASealedOverrideDevirtualizesOnAnUnsealedClass"/>.</summary>
        [Fact]
        public void ASealedOverrideDevirtualizesOnAnUnsealedClass()
        {
            var runtime = Run(
                "class Animal { public virtual fun speak(): string { return \"...\"; } }\n"
                    + "class Dog : Animal { public sealed override fun speak(): string { return \"Woof\"; } }\n"
                    + "fun run(d: Dog): string { return d.speak(); }\n"
                    + "fun call(): string { return run(Dog()); }");

            Assert.Equal("Woof", Text(runtime, "call"));
        }

        [Fact]
        public void AnInterfaceCallResolvesThroughTheDispatchTable()
        {
            var runtime = Run(
                "interface Named { fun name(): string; }\n"
                    + "class Hero : Named { public fun name(): string { return \"hero\"; } }\n"
                    + "fun run(): string { let n: Named = Hero(); return n.name(); }");

            Assert.Equal("hero", Text(runtime, "run"));
        }
        #endregion

        #region Arrow-bodied members (§3.3, §3.4)
        [Fact]
        public void AnArrowBodiedMethodReturnsItsExpression()
        {
            var runtime = Run(
                "fun add(a: int, b: int): int => a + b;\n"
                    + "fun run(): int { return add(3, 4); }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AVoidArrowBodiedMethodEvaluatesItsExpressionForEffect()
        {
            var runtime = Run(
                "class Counter {\n"
                    + "  private var _value: int;\n"
                    + "  public fun bump(): void => _value = _value + 1;\n"
                    + "  public fun value(): int { return _value; }\n"
                    + "}\n"
                    + "fun run(): int { let c = Counter(); c.bump(); c.bump(); return c.value(); }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void AShortFormReadOnlyPropertyReadsThroughItsArrowExpression()
        {
            var runtime = Run(
                "class Vec2 {\n"
                    + "  private let _x: int;\n"
                    + "  public x: int => _x;\n"
                    + "  constructor(x: int) { _x = x; }\n"
                    + "}\n"
                    + "fun run(): int { return Vec2(5).x; }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void AGetterAndSetterMayBothUseTheArrowForm()
        {
            var runtime = Run(
                "class Box {\n"
                    + "  private var _value: int;\n"
                    + "  public value: int { get => _value; set => _value = value; }\n"
                    + "  constructor(v: int) { _value = v; }\n"
                    + "}\n"
                    + "fun run(): int { let b = Box(1); b.value = 9; return b.value; }");

            Assert.Equal(9, Int(runtime, "run"));
        }

        /// <summary>
        /// A getter marked <c>virtual</c> on its own, with no dispatch modifier on the property
        /// itself, still gets a real vtable slot and dispatches through it (§3.2, §3.4) — not just
        /// metadata that says so, but an actual call through an <c>Animal</c>-typed reference landing
        /// on <c>Dog</c>'s override.
        /// </summary>
        [Fact]
        public void APerAccessorVirtualGetterDispatchesThroughTheVtable()
        {
            var runtime = Run(
                "class Animal {\n"
                    + "  public name: string { virtual get => \"Animal\"; }\n"
                    + "}\n"
                    + "class Dog : Animal {\n"
                    + "  public override name: string { get => \"Dog\"; }\n"
                    + "}\n"
                    + "fun run(): string { let a: Animal = Dog(); return a.name; }");

            Assert.Equal("Dog", Text(runtime, "run"));
        }
        #endregion

        #region Enums
        [Fact]
        public void AnEnumCaseIsAStaticTheInitializerBuilt()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                    + "fun run(): bool { return Suit.Hearts == Suit.Hearts; }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        [Fact]
        public void TwoEnumCasesCompareByValue()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                    + "fun run(): bool { return Suit.Hearts == Suit.Spades; }");

            Assert.False(Call(runtime, "run").AsBool);
        }

        /// <summary>
        /// §6.2: an enum is a value, so identity over one is refused outright — the same rejection
        /// a value class gets, surfaced at emission like the value-class one.
        /// </summary>
        [Fact]
        public void IdentityComparisonOverAnEnumIsRejected()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr",
                "enum Suit { Hearts, Spades }\nfun run(): bool { return Suit.Hearts === Suit.Spades; }");

            var compilation = SurtrCompilation.Create(project);
            _owned.Add(compilation);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(!compilation.HasErrors, "Binding must pass: identity over a value is refused at emission, like over a value class.");

            Assert.False(new ModuleEmitter(compilation, binder).TryEmit(), "'===' over an enum must be rejected.");
        }

        [Fact]
        public void AnEnumCaseCarriesItsConstructorArguments()
        {
            var runtime = Run(
                "enum Suit {\n"
                    + "  Hearts(1), Spades(4);\n"
                    + "  public let rank: int;\n"
                    + "  private constructor(rank: int) { this.rank = rank; }\n"
                    + "}\n"
                    + "fun run(): int { return Suit.Spades.rank; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        /// <summary>
        /// The synthetic <c>value</c> is filled by the compiler, not the constructor: each case's
        /// own implied value lands in the first field of the constructed block (§2.2).
        /// </summary>
        [Fact]
        public void TheSyntheticValueFieldIsFilledPerCase()
        {
            var runtime = Run(
                "enum Suit {\n"
                    + "  Hearts(1), Spades(4);\n"
                    + "  public let rank: int;\n"
                    + "  private constructor(rank: int) { this.rank = rank; }\n"
                    + "}\n"
                    + "fun run(): int { return Suit.Spades.value; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>
        /// A case-carrying enum is a multi-field value class, so <c>==</c> walks its fields
        /// structurally: different values mean different instances, exactly like the old
        /// representation promised with different singleton objects.
        /// </summary>
        [Fact]
        public void MultiFieldEnumsCompareStructurallyByValue()
        {
            var runtime = Run(
                "enum Suit {\n"
                    + "  Hearts(\"h\"), Spades(\"s\");\n"
                    + "  public let glyph: string;\n"
                    + "  private constructor(glyph: string) { this.glyph = glyph; }\n"
                    + "}\n"
                    + "fun run(): bool { return Suit.Hearts == Suit.Spades; }");

            Assert.False(Call(runtime, "run").AsBool);
        }

        /// <summary>
        /// A multi-field enum's case read is a whole block, not a literal: comparing it against a
        /// value that came from a local (not from another case read) must still compare structurally
        /// and not mistake the int <c>value</c> slot for the whole value.
        /// </summary>
        [Fact]
        public void AMultiFieldEnumCaseReadComparedAgainstALocalComparesStructurally()
        {
            var runtime = Run(
                "enum Suit {\n"
                    + "  Hearts(\"h\"), Spades(\"s\");\n"
                    + "  public let glyph: string;\n"
                    + "  private constructor(glyph: string) { this.glyph = glyph; }\n"
                    + "}\n"
                    + "fun same(): bool { let s: Suit = Suit.Hearts; return s == Suit.Hearts; }\n"
                    + "fun different(): bool { let s: Suit = Suit.Spades; return s == Suit.Hearts; }\n");

            Assert.True(Call(runtime, "same").AsBool);
            Assert.False(Call(runtime, "different").AsBool);
        }

        /// <summary>
        /// Same guard as <see cref="AMultiFieldEnumCaseReadComparedAgainstALocalComparesStructurally"/>,
        /// with the other side coming out of a class field instead of a local.
        /// </summary>
        [Fact]
        public void AMultiFieldEnumCaseReadComparedAgainstAClassFieldComparesStructurally()
        {
            var runtime = Run(
                "enum Suit {\n"
                    + "  Hearts(\"h\"), Spades(\"s\");\n"
                    + "  public let glyph: string;\n"
                    + "  private constructor(glyph: string) { this.glyph = glyph; }\n"
                    + "}\n"
                    + "class Box { public let suit: Suit; public constructor(suit: Suit) { this.suit = suit; } }\n"
                    + "fun same(): bool { return Box(Suit.Hearts).suit == Suit.Hearts; }\n"
                    + "fun different(): bool { return Box(Suit.Spades).suit == Suit.Hearts; }\n");

            Assert.True(Call(runtime, "same").AsBool);
            Assert.False(Call(runtime, "different").AsBool);
        }

        [Fact]
        public void ASwitchOverAnEnumMatchesByCase()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                    + "fun rank(s: Suit): int { switch (s) { case Suit.Hearts: return 1; case Suit.Spades: return 4; } return 0; }\n"
                    + "fun run(): int { return rank(Suit.Spades); }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        /// <summary>
        /// A switch over a case-carrying enum dispatches on the <c>value</c> slot of the block
        /// (§2.4) — the user fields stay out of the key, and explicit case values are the keys.
        /// </summary>
        [Fact]
        public void ASwitchOverAMultiFieldEnumMatchesByExplicitValue()
        {
            var runtime = Run(
                "enum Suit {\n"
                    + "  Hearts(\"h\") = 1, Spades(\"s\") = 10, Clubs(\"c\") = 11;\n"
                    + "  public let glyph: string;\n"
                    + "  private constructor(glyph: string) { this.glyph = glyph; }\n"
                    + "}\n"
                    + "fun rank(s: Suit): int { switch (s) { case Suit.Hearts: return 1; case Suit.Spades: return 4; case Suit.Clubs: return 7; } return 0; }\n"
                    + "fun run(): int { return rank(Suit.Spades); }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        // ── Fase 1 (docs/Plan-Roadmap-Novedades.md, propuesta 5): type patterns in switch ────────

        /// <summary>
        /// A type-pattern section dispatches by the subject's dynamic class, in section order (a
        /// more specific guarded pattern before the bare one for the same type), and the guard
        /// reads the pattern's own narrowed local - not the original, wider-typed subject.
        /// </summary>
        [Fact]
        public void ASwitchStatementDispatchesByTypePatternAndEvaluatesTheGuardOnTheNarrowedLocal()
        {
            var runtime = Run(
                "class Shape {}\n"
                    + "class Circle : Shape { public let radius: float; public constructor(radius: float) { this.radius = radius; } }\n"
                    + "class Square : Shape { public let side: float; public constructor(side: float) { this.side = side; } }\n"
                    + "fun describe(s: Shape): string {\n"
                    + "  switch (s) {\n"
                    + "    case c is Circle if c.radius > 10.0: return \"big circle\";\n"
                    + "    case c is Circle: return \"circle\";\n"
                    + "    case sq is Square: return \"square:${sq.side}\";\n"
                    + "    default: return \"other\";\n"
                    + "  }\n"
                    + "}\n"
                    + "fun runBig(): string { return describe(Circle(20.0)); }\n"
                    + "fun runSmall(): string { return describe(Circle(1.0)); }\n"
                    + "fun runSquare(): string { return describe(Square(5.0)); }\n"
                    + "fun runOther(): string { return describe(Shape()); }\n");

            Assert.Equal("big circle", Text(runtime, "runBig"));
            Assert.Equal("circle", Text(runtime, "runSmall"));
            Assert.Equal("square:5", Text(runtime, "runSquare"));
            Assert.Equal("other", Text(runtime, "runOther"));
        }

        /// <summary>
        /// The expression form's arms work exactly the same way, and a plain value label can share a
        /// switch with type-pattern arms - each tested in source order until one matches.
        /// </summary>
        [Fact]
        public void ASwitchExpressionMixesAnOrdinaryValueArmWithTypePatternArms()
        {
            var runtime = Run(
                "class Shape {}\n"
                    + "class Circle : Shape { public let radius: float; public constructor(radius: float) { this.radius = radius; } }\n"
                    + "class Square : Shape {}\n"
                    + "fun describe(s: Shape, marker: Circle): string {\n"
                    + "  return switch (s) {\n"
                    + "    marker -> \"origin\",\n"
                    + "    c is Circle -> \"circle\",\n"
                    + "    sq is Square -> \"square\",\n"
                    + "    else -> \"other\",\n"
                    + "  };\n"
                    + "}\n"
                    + "fun runOrigin(): string { let m = Circle(0.0); return describe(m, m); }\n"
                    + "fun runOtherCircle(): string { let m = Circle(0.0); return describe(Circle(5.0), m); }\n");

            Assert.Equal("origin", Text(runtime, "runOrigin"));
            Assert.Equal("circle", Text(runtime, "runOtherCircle"));
        }

        /// <summary>A pattern whose guard fails falls through to the next test, not to the default.</summary>
        [Fact]
        public void AFailedGuardFallsThroughToTheNextPatternRatherThanTheDefault()
        {
            var runtime = Run(
                "class Shape {}\n"
                    + "class Circle : Shape { public let radius: float; public constructor(radius: float) { this.radius = radius; } }\n"
                    + "fun describe(s: Shape): string {\n"
                    + "  switch (s) {\n"
                    + "    case c is Circle if c.radius > 10.0: return \"big\";\n"
                    + "    case c is Circle: return \"small\";\n"
                    + "    default: return \"other\";\n"
                    + "  }\n"
                    + "}\n"
                    + "fun run(): string { return describe(Circle(1.0)); }\n");

            Assert.Equal("small", Text(runtime, "run"));
        }

        /// <summary>
        /// A type pattern's set of matching values is never provably closed, so - unlike an enum -
        /// an expression switch that uses one always needs an explicit <c>else</c>.
        /// </summary>
        [Fact]
        public void ASwitchExpressionWithATypePatternArmNeedsAnElse()
        {
            using var compilation = Reject(
                "class Shape {}\n"
                    + "class Circle : Shape {}\n"
                    + "fun run(): string { let s = Circle(); return switch (s) { c is Circle -> \"circle\", }; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.SwitchNotExhaustive);
        }

        /// <summary>Combining a type pattern with another label in the same section is rejected, not silently mis-bound.</summary>
        [Fact]
        public void ASwitchSectionCannotCombineATypePatternWithAnotherLabel()
        {
            using var compilation = Reject(
                "class Shape {}\n"
                    + "class Circle : Shape {}\n"
                    + "fun run(): string {\n"
                    + "  let s = Circle();\n"
                    + "  let other = Circle();\n"
                    + "  switch (s) { case c is Circle: case other: return \"x\"; default: return \"y\"; }\n"
                    + "  return \"z\";\n"
                    + "}");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidSwitchPattern);
        }

        /// <summary>
        /// Unlike an ordinary value section's locals - visible in every later section, for
        /// fallthrough - a type pattern's binding is scoped to its own section alone, the same as a
        /// <c>catch</c> clause's exception local is scoped to its own clause.
        /// </summary>
        [Fact]
        public void ATypePatternLocalIsNotVisibleInAnotherSection()
        {
            using var compilation = Reject(
                "class Shape {}\n"
                    + "class Circle : Shape {}\n"
                    + "class Square : Shape {}\n"
                    + "fun run(s: Shape): string {\n"
                    + "  switch (s) {\n"
                    + "    case c is Circle: break;\n"
                    + "    case sq is Square: return c.toString();\n"
                    + "    default: break;\n"
                    + "  }\n"
                    + "  return \"\";\n"
                    + "}");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnresolvedName);
        }

        /// <summary>
        /// The synthesized API every enum answers to (§2.3): structural <c>equals</c>, a
        /// <c>hashCode</c> equal to the value's own for a bare enum, and a <c>toString</c> naming
        /// the case.
        /// </summary>
        [Fact]
        public void AnEnumAnswersEqualsHashCodeAndToString()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                + "fun run(): int {\n"
                + "  var n = 0;\n"
                + "  if (Suit.Hearts.equals(Suit.Hearts)) { n = n + 1; }\n"
                + "  if (!Suit.Hearts.equals(Suit.Spades)) { n = n + 10; }\n"
                + "  if (Suit.Hearts.hashCode() == 0) { n = n + 100; }\n"
                + "  if (Suit.Hearts.toString() == \"Hearts\") { n = n + 1000; }\n"
                + "  return n;\n"
                + "}");

            Assert.Equal(1111, Int(runtime, "run"));
        }

        /// <summary>A <c>toString</c> on a value no case names falls back to <c>Name(value)</c>.</summary>
        [Fact]
        public void ToStringFallsBackToTheQualifiedNameForAnUnknownValue()
        {
            var runtime = Run(
                "enum Suit { Hearts }\n"
                + "fun run(): string { return Suit.of(77) == null ? \"fallback\" : \"case\"; }");

            Assert.Equal("fallback", Text(runtime, "run"));
        }

        /// <summary><c>values()</c> returns every case in declaration order.</summary>
        [Fact]
        public void ValuesReturnsEveryCaseInDeclarationOrder()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades, Clubs }\n"
                + "fun run(): int { let all = Suit.values(); return all.length * 100 + (all[2] == Suit.Clubs ? 1 : 0); }");

            Assert.Equal(301, Int(runtime, "run"));
        }

        /// <summary><c>values()</c> returns a fresh array per call (§6.7) — mutating one call's copy never leaks into the next.</summary>
        [Fact]
        public void ValuesReturnsAFreshArrayPerCall()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                + "fun run(): int { let a = Suit.values(); a[0] = Suit.Spades; return Suit.values()[0] == Suit.Hearts ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary><c>of(value)</c> is the inverse of <c>.value</c>, and null when no case carries the value.</summary>
        [Fact]
        public void OfValueRoundTripsAndIsNullForUnknowns()
        {
            var runtime = Run(
                "enum Suit { Hearts = 1, Spades = 4 }\n"
                + "fun roundTrip(): int { return Suit.of(Suit.Spades.value).value; }\n"
                + "fun unknown(): int { return Suit.of(99) == null ? 1 : 0; }");

            Assert.Equal(4, Int(runtime, "roundTrip"));
            Assert.Equal(1, Int(runtime, "unknown"));
        }

        /// <summary><c>of(name)</c> finds a case by its exact name, and null otherwise.</summary>
        [Fact]
        public void OfNameFindsCasesExactlyAndIsNullOtherwise()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                + "fun found(): int { return Suit.of(\"Spades\") == Suit.Spades ? 1 : 0; }\n"
                + "fun wrongCase(): int { return Suit.of(\"hearts\") == null ? 1 : 0; }\n"
                + "fun unknown(): int { return Suit.of(\"Diamonds\") == null ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "found"));
            Assert.Equal(1, Int(runtime, "wrongCase"));
            Assert.Equal(1, Int(runtime, "unknown"));
        }

        /// <summary>
        /// A <c>@Flags</c> enum's <c>of(value)</c> is total: any int is a representable
        /// combination, so it is never null (§2.3).
        /// </summary>
        [Fact]
        public void AFlagsEnumsOfValueIsTotal()
        {
            var runtime = Run(
                Perms + "fun run(): int { return Perm.of(3) == null ? 0 : (Perm.of(3) == (Perm.Read | Perm.Write) ? 1 : 0); }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>Enums order by value, through the synthesized <c>compareTo</c> and <c>operator&lt;=&gt;</c> (§2.3, §5.6).</summary>
        [Fact]
        public void EnumsOrderByValueThroughCompareToAndSpaceship()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades, Clubs }\n"
                + "fun compare(): int { return Suit.Spades.compareTo(Suit.Hearts) > 0 ? 1 : 0; }\n"
                + "fun less(): int { return Suit.Hearts < Suit.Spades ? 1 : 0; }\n"
                + "fun greaterOrEqual(): int { return Suit.Spades >= Suit.Hearts ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "compare"));
            Assert.Equal(1, Int(runtime, "less"));
            Assert.Equal(1, Int(runtime, "greaterOrEqual"));
        }

        /// <summary>
        /// Every enum satisfies <c>IEquatable&lt;E&gt;</c> and <c>IComparable&lt;E&gt;</c> (§6.8):
        /// a generic constraint instantiates with an enum, and the contract slots dispatch through
        /// the synthesized members via the bridge.
        /// </summary>
        [Fact]
        public void AnEnumSatisfiesTheComparableAndEquatableContracts()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                + "fun biggest<T : IComparable<T>>(a: T, b: T): T { return a.compareTo(b) >= 0 ? a : b; }\n"
                + "fun same<E : IEquatable<E>>(a: E, b: E): bool { return a.equals(b); }\n"
                + "fun run(): int { return biggest(Suit.Spades, Suit.Hearts) == Suit.Spades && same(Suit.Hearts, Suit.Hearts) ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>A case-carrying enum satisfies the same contracts; the bridge unboxes its block receiver and arguments.</summary>
        [Fact]
        public void ACaseCarryingEnumSatisfiesTheContracts()
        {
            var runtime = Run(
                "enum Suit {\n"
                    + "  Hearts(\"h\"), Spades(\"s\");\n"
                    + "  public let glyph: string;\n"
                    + "  private constructor(glyph: string) { this.glyph = glyph; }\n"
                    + "}\n"
                    + "fun biggest<T : IComparable<T>>(a: T, b: T): T { return a.compareTo(b) >= 0 ? a : b; }\n"
                    + "fun run(): int { return biggest(Suit.Spades, Suit.Hearts) == Suit.Spades ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>
        /// §2.3ter: <c>==</c> over an enum is a slot comparison and never lowers to a call to
        /// <c>equals</c> — even a user-written <c>equals</c> that lies cannot change it.
        /// </summary>
        [Fact]
        public void EqualityOverAnEnumNeverCallsEquals()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades;\n"
                + "  public fun equals(other: Suit): bool { return false; } }\n"
                + "fun run(): int { return Suit.Hearts == Suit.Hearts ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>A member the source declares replaces the synthesized one (§2.3, R9).</summary>
        [Fact]
        public void AUserWrittenEqualsOverridesTheSynthesizedOne()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades;\n"
                + "  public fun equals(other: Suit): bool { return false; } }\n"
                + "fun run(): int { return Suit.Hearts.equals(Suit.Hearts) ? 1 : 0; }");

            Assert.Equal(0, Int(runtime, "run"));
        }

        /// <summary>Duplicate values in a <c>@Flags</c> enum are equal for both comparison and equality (§2.3).</summary>
        [Fact]
        public void DuplicateFlagsValuesCompareEqual()
        {
            var runtime = Run(
                "@Flags enum Perm { None = 0, Read = 1, Write = 2, ReadAlias = 1 }\n"
                + "fun run(): int { return Perm.Read.compareTo(Perm.ReadAlias) == 0 && Perm.Read.equals(Perm.ReadAlias) ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary><c>of(value)</c> on a duplicate <c>@Flags</c> value returns the first case (§2.3).</summary>
        [Fact]
        public void OfValueReturnsTheFirstCaseForADuplicateFlagsValue()
        {
            var runtime = Run(
                "@Flags enum Perm { None = 0, Read = 1, ReadAlias = 1 }\n"
                + "fun run(): int { return Perm.of(1) == Perm.Read ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>Ordering is by value, not declaration position (§6.8).</summary>
        [Fact]
        public void OrderingIsByExplicitValueNotDeclarationOrder()
        {
            var runtime = Run(
                "enum Suit { Hearts = 1, Spades = 100 }\n"
                + "fun run(): int { return Suit.Hearts < Suit.Spades && Suit.Spades > Suit.Hearts ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>The synthesized API works for a case-carrying enum too: values holds the blocks, equality walks every field, and ordering reads the <c>value</c> slot.</summary>
        [Fact]
        public void TheSynthesizedApiWorksForACaseCarryingEnum()
        {
            var runtime = Run(
                "enum Suit {\n"
                    + "  Hearts(\"h\"), Spades(\"s\");\n"
                    + "  public let glyph: string;\n"
                    + "  private constructor(glyph: string) { this.glyph = glyph; }\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "  var n = 0;\n"
                    + "  if (Suit.values().length == 2) { n = n + 1; }\n"
                    + "  if (Suit.Hearts.equals(Suit.Hearts) && !Suit.Hearts.equals(Suit.Spades)) { n = n + 10; }\n"
                    + "  if (Suit.Hearts.compareTo(Suit.Spades) < 0 && Suit.Spades > Suit.Hearts) { n = n + 100; }\n"
                    + "  return n;\n"
                    + "}");

            Assert.Equal(111, Int(runtime, "run"));
        }

        /// <summary>
        /// A case-carrying enum's nullable form is a boxed reference (§5.1, value-types handoff),
        /// so its <c>of</c> is synthesized even multi-field: it boxes a matching case's block and
        /// returns the null reference for an unknown value.
        /// </summary>
        [Fact]
        public void OfIsSynthesizedForACaseCarryingEnumAndReturnsNullForUnknowns()
        {
            var runtime = Run(
                "enum Suit {\n"
                    + "  Hearts(\"h\"), Spades(\"s\");\n"
                    + "  public let glyph: string;\n"
                    + "  private constructor(glyph: string) { this.glyph = glyph; }\n"
                    + "}\n"
                    + "fun known(): int { let s = Suit.of(1); return s == null ? -1 : s.glyph == \"s\" ? 1 : 0; }\n"
                    + "fun unknown(): int { return Suit.of(99) == null ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "known"));
            Assert.Equal(1, Int(runtime, "unknown"));
        }

        /// <summary>
        /// A multi-field value class's nullable form can be returned as null without underflow: its
        /// <c>T?</c> is a boxed reference (present = boxed instance, absent = null), so the null
        /// occupies the same single slot a present value is boxed into.
        /// </summary>
        [Fact]
        public void ANullableMultiFieldValueClassCanReturnNull()
        {
            var runtime = Run(
                "value class Vec2 {\n"
                    + "  public let x: int;\n"
                    + "  public let y: int;\n"
                    + "  public constructor(x: int, y: int) { this.x = x; this.y = y; }\n"
                    + "}\n"
                    + "fun nothing(): Vec2? { return null; }\n"
                    + "fun something(): Vec2? { return Vec2(1, 2); }\n"
                    + "fun check(): int { return nothing() == null ? (something() == null ? 0 : 1) : 0; }");

            Assert.Equal(1, Int(runtime, "check"));
        }

        /// <summary>
        /// A nullable multi-field value class unboxes when a null-check narrows it to the block
        /// form, so reading a field off it works.
        /// </summary>
        [Fact]
        public void ANullableMultiFieldValueClassUnboxesForFieldAccess()
        {
            var runtime = Run(
                "value class Vec2 {\n"
                    + "  public let x: int;\n"
                    + "  public let y: int;\n"
                    + "  public constructor(x: int, y: int) { this.x = x; this.y = y; }\n"
                    + "}\n"
                    + "fun roundTrip(): int { let v: Vec2? = Vec2(3, 4); return v == null ? -1 : v.x; }");

            Assert.Equal(3, Int(runtime, "roundTrip"));
        }

        /// <summary>
        /// A nullable multi-field value class round-trips through <c>of</c>/<c>.value</c>-style
        /// access: the boxed reference unboxes back into the block on read.
        /// </summary>
        [Fact]
        public void ANullableMultiFieldValueClassRoundTripsThroughAssert()
        {
            var runtime = Run(
                "value class Vec2 {\n"
                    + "  public let x: int;\n"
                    + "  public let y: int;\n"
                    + "  public constructor(x: int, y: int) { this.x = x; this.y = y; }\n"
                    + "}\n"
                    + "fun roundTrip(): int { let v: Vec2? = Vec2(7, 8); let w = v!!; return w.x; }");

            Assert.Equal(7, Int(runtime, "roundTrip"));
        }

        /// <summary>
        /// A nullable multi-field value class works with <c>?.</c>: the boxed receiver unboxes
        /// before the member access, and a null receiver produces the nullable result's absent
        /// value.
        /// </summary>
        [Fact]
        public void ANullableMultiFieldValueClassSupportsNullConditional()
        {
            var runtime = Run(
                "value class Vec2 {\n"
                    + "  public let x: int;\n"
                    + "  public let y: int;\n"
                    + "  public constructor(x: int, y: int) { this.x = x; this.y = y; }\n"
                    + "}\n"
                    + "fun present(): int { let v: Vec2? = Vec2(9, 1); return (v?.x ?? 0); }\n"
                    + "fun absent(): int { let v: Vec2? = null; return (v?.x ?? 0); }");

            Assert.Equal(9, Int(runtime, "present"));
            Assert.Equal(0, Int(runtime, "absent"));
        }

        /// <summary>
        /// A case-carrying enum's <c>of</c> round-trips through <c>.value</c>: the boxed case
        /// unboxes back into the block, so reading the value slot off it works.
        /// </summary>
        [Fact]
        public void OfRoundTripsForACaseCarryingEnum()
        {
            var runtime = Run(
                "enum Suit {\n"
                    + "  Hearts(\"h\"), Spades(\"s\");\n"
                    + "  public let glyph: string;\n"
                    + "  private constructor(glyph: string) { this.glyph = glyph; }\n"
                    + "}\n"
                    + "fun roundTrip(): int { let s = Suit.of(Suit.Spades.value); return s == null ? -1 : s.value; }");

            Assert.Equal(1, Int(runtime, "roundTrip"));
        }

        /// <summary>
        /// Calling a method on a nullable multi-field value (narrowed to its block form) unboxes
        /// the boxed receiver before the dispatch, so instance methods work off an <c>of</c> result.
        /// </summary>
        [Fact]
        public void AMethodOnANarrowedNullableBlockUnboxesItsReceiver()
        {
            var runtime = Run(
                "value class Vec2 {\n"
                    + "  public let x: int;\n"
                    + "  public let y: int;\n"
                    + "  public constructor(x: int, y: int) { this.x = x; this.y = y; }\n"
                    + "  public fun sum(): int { return this.x + this.y; }\n"
                    + "}\n"
                    + "fun run(): int { let v: Vec2? = Vec2(3, 4); return v == null ? -1 : v.sum(); }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>
        /// The value-types handoff's original repros now emit: a multi-field value class and a
        /// case-carrying enum can return <c>null</c> from their nullable type without underflow.
        /// </summary>
        [Fact]
        public void NullableMultiFieldValuesReturnNullWithoutUnderflow()
        {
            var runtime = Run(
                "value class Vec2 {\n"
                    + "  public let x: int;\n"
                    + "  public let y: int;\n"
                    + "  public constructor(x: int, y: int) { this.x = x; this.y = y; }\n"
                    + "}\n"
                    + "enum Suit {\n"
                    + "  Hearts(\"h\"), Spades(\"s\");\n"
                    + "  public let glyph: string;\n"
                    + "  private constructor(glyph: string) { this.glyph = glyph; }\n"
                    + "}\n"
                    + "fun make(): Vec2? { return null; }\n"
                    + "fun pick(): Suit? { return null; }\n"
                    + "fun check(): int { return make() == null && pick() == null ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "check"));
        }

        /// <summary>
        /// Regression: a <c>forceinline</c> call's spliced result temp used to be sized from the
        /// <em>enclosing</em> method's return type rather than the callee's own — so a single-slot
        /// <c>float</c> result spliced into a caller that itself returns a multi-field value class
        /// declared a 2-slot temp for a 1-slot value, and storing the callee's `return` into it
        /// underflowed the stack. Constructing the value class after the inlined call is what
        /// triggers it; the caller's own return type is what decides the (wrong) temp width.
        /// </summary>
        [Fact]
        public void ForceInlineCallFollowedByMultiFieldValueClassConstructionDoesNotUnderflow()
        {
            var runtime = Run(
                "import game.math.*;\n"
                    + "value class Vec2 {\n"
                    + "  public let x: float;\n"
                    + "  public let y: float;\n"
                    + "  public constructor(x: float, y: float) { this.x = x; this.y = y; }\n"
                    + "}\n"
                    + "fun make(t: float): Vec2 {\n"
                    + "  let c = clamp01(t);\n"
                    + "  return Vec2(c, c);\n"
                    + "}",
                ("/game/math/Math.surtr",
                    "public forceinline fun clamp01(value: float): float {\n"
                        + "  if (value < 0.0) return 0.0;\n"
                        + "  if (value > 1.0) return 1.0;\n"
                        + "  return value;\n"
                        + "}"));

            var instance = runtime.Resolve<SurtrInstance>(Call(runtime, "make", SurtrValue.CreateFloat(2.5)))!;
            Assert.Equal(1.0, instance[0].AsFloat);
            Assert.Equal(1.0, instance[1].AsFloat);
        }

        /// <summary>
        /// The synthesized bodies carry the <c>@Pure</c>/<c>@NoAlloc</c> marks (§2.3bis): a user
        /// <c>@NoAlloc</c> body may call them, and the analyzer accepts the synthesized bodies as
        /// written.
        /// </summary>
        [Fact]
        public void TheSynthesizedBodiesPassTheNoAllocPromise()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                + "@NoAlloc\n"
                + "public fun run(): bool { return Suit.Hearts.equals(Suit.Hearts) && Suit.of(0) != null; }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        /// <summary>
        /// A <c>@Flags</c> combination no case names still renders — the <c>toString</c> fallback
        /// is <c>Name(value)</c> (§2.3).
        /// </summary>
        [Fact]
        public void AFlagsCombinationToStringsThroughTheFallback()
        {
            var runtime = Run(
                Perms + "fun run(): string { return (Perm.Read | Perm.Write).toString(); }");

            Assert.Equal("Perm(3)", Text(runtime, "run"));
        }

        /// <summary>
        /// The <c>@Pure</c>/<c>@NoAlloc</c> marks the synthesized members carry (§2.3bis) travel
        /// through the image, so a module importing the enum sees them exactly as a source author
        /// would have written them.
        /// </summary>
        [Fact]
        public void TheSynthesizedMarksSurviveTheImage()
        {
            var emitter = Build(
                "enum Suit { Hearts, Spades }");

            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes());
            using var runtime = new SurtrRuntime();
            var module = reloaded.Instantiate();
            runtime.LoadModule(module);

            var suit = module.FindClass("Suit")!;
            Assert.True(suit.TryGetMethods("equals", out var equals));
            Assert.True(equals[0].TryGetAttribute(SurtrBuiltIns.Pure, out _), "equals is @Pure.");
            Assert.True(equals[0].TryGetAttribute(SurtrBuiltIns.NoAlloc, out _), "equals is @NoAlloc.");

            Assert.True(suit.TryGetMethods("values", out var values));
            Assert.False(values[0].TryGetAttribute(SurtrBuiltIns.Pure, out _), "values() is deliberately not @Pure (§6.7).");
        }
        #endregion

        #region The lowerings Step 5 owed
        [Fact]
        public void ALambdaBecomesAClosureOverALiftedFunction()
        {
            var runtime = Run(
                "fun run(): int { let add = (a: int, b: int) => a + b; return add(2, 3); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void ALambdaCapturesByValue()
        {
            var runtime = Run(
                "fun run(): int { let base = 40; let bump = (x: int) => x + base; return bump(2); }");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void ALambdaInsideAMethodCapturesTheReceiver()
        {
            var runtime = Run(
                "class Adder {\n"
                    + "  public var offset: int = 10;\n"
                    + "  public fun make(): (int) -> int { return (x: int) => x + this.offset; }\n"
                    + "}\n"
                    + "fun run(): int { let f = Adder().make(); return f(5); }");

            Assert.Equal(15, Int(runtime, "run"));
        }

        [Fact]
        public void ATryCatchRunsItsHandler()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  try { throw Exception(\"boom\"); }\n"
                    + "  catch (e: Exception) { return 1; }\n"
                    + "  return 0;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AFinallyRunsOnTheNormalPath()
        {
            var runtime = Run(
                "var trace: int = 0;\n"
                    + "fun run(): int {\n"
                    + "  try { trace = trace + 1; }\n"
                    + "  finally { trace = trace + 10; }\n"
                    + "  return trace;\n"
                    + "}");

            Assert.Equal(11, Int(runtime, "run"));
        }

        [Fact]
        public void AFinallyRunsBeforeAReturnInsideTheTry()
        {
            var runtime = Run(
                "var trace: int = 0;\n"
                    + "fun body(): int { try { return 1; } finally { trace = 7; } }\n"
                    + "fun run(): int { let r = body(); return r + trace; }");

            Assert.Equal(8, Int(runtime, "run"));
        }

        [Fact]
        public void AFinallyRunsWhenTheTryThrowsAndNothingCatches()
        {
            var runtime = Run(
                "var trace: int = 0;\n"
                    + "fun risky(): void { try { throw Exception(\"boom\"); } finally { trace = 5; } }\n"
                    + "fun run(): int { try { risky(); } catch (e: Exception) { } return trace; }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void ASafeCastYieldsNullWhenItDoesNotApply()
        {
            var runtime = Run(
                "class Animal { }\nclass Dog : Animal { }\nclass Cat : Animal { }\n"
                    + "fun run(): int { let a: Animal = Cat(); let d = a as? Dog; return d == null ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ASafeCastKeepsTheValueWhenItApplies()
        {
            var runtime = Run(
                "class Animal { }\nclass Dog : Animal { public fun legs(): int { return 4; } }\n"
                    + "fun run(): int { let a: Animal = Dog(); let d = a as? Dog; return d == null ? 0 : d!!.legs(); }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void StringsAreOrderedThroughCompareTo()
        {
            var runtime = Run("fun run(): bool { return \"apple\" < \"banana\"; }");
            Assert.True(Call(runtime, "run").AsBool);
        }

        [Fact]
        public void ThreeWayCompareOnStringsGivesTheSign()
        {
            var runtime = Run("fun run(): int { return \"b\" <=> \"a\"; }");
            Assert.True(Int(runtime, "run") > 0);
        }

        [Fact]
        public void AStringSwitchMatchesByTextRatherThanByHash()
        {
            var runtime = Run(
                "fun pick(s: string): int {\n"
                    + "  switch (s) { case \"one\": return 1; case \"two\": return 2; case \"three\": return 3; }\n"
                    + "  return 0;\n"
                    + "}\n"
                    + "fun run(): int { return pick(\"two\") * 100 + pick(\"nope\"); }");

            Assert.Equal(200, Int(runtime, "run"));
        }

        [Fact]
        public void ADenseIntegerSwitchStillPicksTheRightArm()
        {
            var runtime = Run(
                "fun pick(n: int): int {\n"
                    + "  switch (n) { case 0: return 10; case 1: return 11; case 2: return 12; case 3: return 13; }\n"
                    + "  return -1;\n"
                    + "}\n"
                    + "fun run(): int { return pick(2) + pick(9); }");

            Assert.Equal(11, Int(runtime, "run"));
        }

        [Fact]
        public void AForInOverADictionaryWalksKeyValuePairs()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let m: {string: int} = {\"a\": 1, \"b\": 2, \"c\": 3};\n"
                    + "  var total = 0;\n"
                    + "  for (e in m) { total = total + e[1]; }\n"
                    + "  return total;\n"
                    + "}");

            Assert.Equal(6, Int(runtime, "run"));
        }

        [Fact]
        public void AForInOverAnIterableGoesThroughItsCursor()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let xs: int[] = [1, 2, 3];\n"
                    + "  let it: IIterable<int> = xs;\n"
                    + "  var total = 0;\n"
                    + "  for (x in it) { total = total + x; }\n"
                    + "  return total;\n"
                    + "}");

            Assert.Equal(6, Int(runtime, "run"));
        }

        [Fact]
        public void AForInOverARangeHeldInALocalStillWalksIt()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let r = 1..=4;\n"
                    + "  var total = 0;\n"
                    + "  for (i in r) { total = total + i; }\n"
                    + "  return total;\n"
                    + "}");

            Assert.Equal(10, Int(runtime, "run"));
        }

        #region Ranges as values (§2.9)

        [Fact]
        public void ProbeArrayOfRangesOnly()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let a = [10..12];\n"
                    + "  let first: range = a[0];\n"
                    + "  return first.start * 100 + first.end;\n"
                    + "}");
            Assert.Equal(1012, Int(runtime, "run"));
        }

        [Fact]
        public void ProbeDictWithRangeKeysOnly()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let scores: {range: int} = {};\n"
                    + "  scores[5..=9] = 42;\n"
                    + "  return scores[5..=9];\n"
                    + "}");
            Assert.Equal(42, Int(runtime, "run"));
        }

        /// <summary>
        /// An escaped range is three slots, so it round-trips through variables, parameters,
        /// returns and calls without ever being a reference the registry owns.
        /// </summary>
        [Fact]
        public void AnEscapedRangeSurvivesVariablesParametersAndReturns()
        {
            var runtime = Run(
                "fun widen(r: range): range { return r; }\n"
                    + "fun run(): int {\n"
                    + "  let r = widen(2..=6);\n"
                    + "  var n = 0;\n"
                    + "  for (i in r) { n += i; }\n"
                    + "  return r.start * 10000 + r.end * 100 + n;\n"
                    + "}");

            // start=2, end=6, sum 2+3+4+5+6=20.
            Assert.Equal(20000 + 600 + 20, Int(runtime, "run"));
        }

        /// <summary>Two ranges written the same way are the same value; the flag alone separates the forms.</summary>
        [Fact]
        public void RangeEqualityIsStructuralAndFormSensitive()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let a = 0..3;\n"
                    + "  let b = 0..3;\n"
                    + "  let c = 0..=3;\n"
                    + "  if (a != b) { return 1; }\n"
                    + "  if (a == c) { return 2; }\n"
                    + "  if (!(a == b)) { return 3; }\n"
                    + "  if (!(a.start == c.start && a.end == c.end)) { return 4; }\n"
                    + "  return 0;\n"
                    + "}");

            Assert.Equal(0, Int(runtime, "run"));
        }

        /// <summary>The members that are pure slot reads fold; the computed ones reach their native bodies.</summary>
        [Fact]
        public void ARangesMembersAnswerFromTheBlock()
        {
            var runtime = Run(
                "fun run(): string {\n"
                    + "  let r = 2..=7;\n"
                    + "  let empty = 9..9;\n"
                    + "  return \"\" + r.start + \",\" + r.end + \",\" + r.length + \",\" + r.isEmpty + \",\" + empty.isEmpty + \",\" + r.contains(7) + \",\" + r.contains(8);\n"
                    + "}");

            Assert.Equal("2,7,6,false,true,true,false", Text(runtime, "run"));
        }

        /// <summary><c>string(aRange)</c> spells it back the way it was written.</summary>
        [Fact]
        public void ARangeToStringRoundTripsItsSpelling()
        {
            var runtime = Run(
                "fun run(): string { return string(1..4) + \"|\" + string(1..=4); }");

            Assert.Equal("1..4|1..=4", Text(runtime, "run"));
        }

        /// <summary>
        /// Crossing into one-reference storage packs the block: array elements keep their bounds
        /// and a dictionary keyed by ranges finds keys by value, not by pack identity.
        /// </summary>
        [Fact]
        public void ARangePacksIntoSingleSlotStorage()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let a = [10..12, 20..=22];\n"
                    + "  let first: range = a[0];\n"
                    + "  let scores: {range: int} = {};\n"
                    + "  scores[5..=9] = 42;\n"
                    + "  scores[5..=9] = scores[5..=9] + 1;\n"
                    + "  return first.start * 1000 + first.end * 10 + (first.isInclusive ? 0 : 1) + scores[5..=9] * 100000;\n"
                    + "}");

            // first = 10..12 → 10000+120+1; the dict key was found by value and updated to 43.
            Assert.Equal(43 * 100000 + 10121, Int(runtime, "run"));
        }

        /// <summary>A range nested inside composites flattens like any other inline value.</summary>
        [Fact]
        public void ARangeFlattensIntoTuplesAndValueClassFields()
        {
            var runtime = Run(
                "value class Window {\n"
                    + "  public let rows: range;\n"
                    + "  constructor(rows: range) { this.rows = rows; }\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "  let w = Window(3..=5);\n"
                    + "  let pair = (1..2, w.rows);\n"
                    + "  let inner: range = pair[1];\n"
                    + "  var n = 0;\n"
                    + "  for (i in inner) { n += i; }\n"
                    + "  return w.rows.end * 100 + pair[0].end * 10 + n;\n"
                    + "}");

            // rows end=5, (1..2).end=2, sum 3+4+5=12 → 512? no: 500 + 20 + 12.
            Assert.Equal(532, Int(runtime, "run"));
        }

        #endregion

        [Fact]
        public void AnInlineFunctionIsSplicedIntoItsCallSite()
        {
            var runtime = Run(
                "inline fun twice(x: int): int { return x + x; }\n"
                    + "fun run(): int { return twice(3) + twice(4); }");

            Assert.Equal(14, Int(runtime, "run"));
        }

        /// <summary>
        /// A spliced body with more than one `return` still has to join them at one exit — the
        /// single-tail-return fast path (see <c>LoweringChoiceTests</c>) does not apply here, and
        /// both exits still have to reach the right value.
        /// </summary>
        [Fact]
        public void AMultiReturnInlineBodyStillJoinsCorrectly()
        {
            var runtime = Run(
                "inline fun sign(x: int): int { if (x < 0) { return -1; } return 1; }\n"
                    + "fun run(a: int): int { return sign(a); }");

            Assert.Equal(-1, Int(runtime, "run", SurtrValue.CreateInt(-5)));
            Assert.Equal(1, Int(runtime, "run", SurtrValue.CreateInt(5)));
        }

        /// <summary>
        /// The cost heuristic (§3.6) splices a body no <c>inline</c> was written on: a single-return
        /// arithmetic body is two instructions, and a frame for it is the frame the heuristic exists
        /// to remove.
        /// </summary>
        [Fact]
        public void ATrivialFunctionIsSplicedWithoutAnyModifier()
        {
            var runtime = Run(
                "fun twice(x: int): int { return x + x; }\n"
                    + "fun run(a: int): int { return twice(a) + 1; }");

            Assert.Equal(7, Int(runtime, "run", SurtrValue.CreateInt(3)));
        }

        /// <summary>
        /// An auto-property's accessors are one instruction each — a field load and a field store —
        /// and §3.6 inlines both at the call site, so reading and writing one never pays for a frame.
        /// </summary>
        [Fact]
        public void AnAutoPropertyReadAndWriteLowerToTheBackingField()
        {
            var runtime = Run(
                "class A { public n: int { get; set; } }\n"
                    + "fun run(): int { let a = A(); a.n = 9; return a.n; }");

            Assert.Equal(9, Int(runtime, "run"));
        }

        /// <summary>
        /// <c>forceinline</c> ignores the cost heuristic entirely - a body well above both the
        /// default (2) and the <c>inline</c> (8) threshold still has to splice and compute the
        /// right value, not merely avoid throwing.
        /// </summary>
        [Fact]
        public void AForceInlineFunctionSplicesRegardlessOfCost()
        {
            var runtime = Run(
                "forceinline fun heavy(x: int): int {\n"
                    + "  if (x < 0) { return -1; }\n"
                    + "  if (x == 0) { return 0; }\n"
                    + "  return x * x + x + 1;\n"
                    + "}\n"
                    + "fun run(a: int): int { return heavy(a); }");

            Assert.Equal(7, Int(runtime, "run", SurtrValue.CreateInt(2)));
        }

        /// <summary>
        /// A call reached through virtual dispatch can resolve to any override at run time, so
        /// <c>forceinline</c> cannot splice it - and has to say so at compile time rather than
        /// splice the wrong body or ignore the hint silently.
        /// </summary>
        [Fact]
        public void AForceInlineVirtualMethodCallIsNotLowered()
        {
            var reported = Unlowerable(
                "class A {\n"
                    + "  public virtual forceinline fun speak(): int { return 1; }\n"
                    + "}\n"
                    + "fun run(a: A): int { return a.speak(); }");

            Assert.Single(reported);
        }

        /// <summary>
        /// The read-side twin of <see cref="AForceInlineVirtualMethodCallIsNotLowered"/>: a
        /// property read reaches its getter through a synthetic call that always claims
        /// non-virtual (so <c>TryInline</c>'s own dispatch guard cannot catch a virtual getter on
        /// its own), which is exactly why <c>TryInlinePropertyGetter</c> has to fail loudly here
        /// itself instead of silently falling back to an ordinary virtual read.
        /// </summary>
        [Fact]
        public void AForceInlineVirtualPropertyGetterIsNotLowered()
        {
            var reported = Unlowerable(
                "class A {\n"
                    + "  public virtual forceinline n: int { get { return 1; } set { } }\n"
                    + "}\n"
                    + "fun run(a: A): int { return a.n; }");

            Assert.Single(reported);
        }

        /// <summary>
        /// A <c>forceinline</c> function that calls itself cannot be spliced without expanding
        /// forever, so the recursive call has to fail to lower rather than loop the emitter.
        /// </summary>
        [Fact]
        public void AForceInlineFunctionThatCallsItselfIsNotLowered()
        {
            var reported = Unlowerable(
                "forceinline fun loopy(x: int): int { if (x <= 0) { return 0; } return loopy(x - 1); }\n"
                    + "fun run(a: int): int { return loopy(a); }");

            Assert.NotEmpty(reported);
        }

        /// <summary>
        /// A computed property's setter honors <c>forceinline</c> the same way its getter and an
        /// ordinary method do (§3.4/§3.6) - before this, only the getter side of a property ever
        /// reached the inline machinery, and a computed setter's hint was silently ignored.
        /// </summary>
        [Fact]
        public void AForceInlinePropertySetterSplicesAndAppliesItsBody()
        {
            var runtime = Run(
                "class A {\n"
                    + "  public var _n: int;\n"
                    + "  public forceinline n: int { get { return this._n; } set { this._n = value * 2; } }\n"
                    + "}\n"
                    + "fun run(): int { let a = A(); a.n = 5; return a._n; }");

            Assert.Equal(10, Int(runtime, "run"));
        }

        /// <summary>
        /// The mirror of <see cref="AForceInlineVirtualMethodCallIsNotLowered"/> for a property
        /// setter: a virtual accessor cannot be spliced, and a <c>forceinline</c> one has to fail
        /// to lower rather than write through the wrong override or drop the hint silently.
        /// </summary>
        [Fact]
        public void AForceInlineVirtualPropertySetterIsNotLowered()
        {
            var reported = Unlowerable(
                "class A {\n"
                    + "  public virtual forceinline n: int { get { return 0; } set { } }\n"
                    + "}\n"
                    + "fun run(a: A): int { a.n = 1; return 0; }");

            Assert.Single(reported);
        }

        /// <summary>
        /// A class method shadows a module-level function of the same name at an unqualified call
        /// site inside that class, silently and without ambiguity (Binder.cs's <c>Scope</c> looks
        /// at the containing type before the module) - proven here by giving the two distinct
        /// return values rather than only checking that binding reports no error.
        /// </summary>
        [Fact]
        public void AnUnqualifiedCallInsideAClassPrefersItsOwnMethodOverAModuleFunctionOfTheSameName()
        {
            var runtime = Run(
                "public fun greet(): int { return 1; }\n"
                    + "class Greeter {\n"
                    + "  public fun greet(): int { return 2; }\n"
                    + "  public fun run(): int { return greet(); }\n"
                    + "}\n"
                    + "fun run(): int { return Greeter().run(); }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void AConstFunctionCallWithConstantArgumentsIsFoldedAway()
        {
            var runtime = Run(
                "const fun square(x: int): int { return x * x; }\n"
                    + "fun run(): int { return square(5); }");

            Assert.Equal(25, Int(runtime, "run"));
        }

        [Fact]
        public void AConstFunctionIsStillCallableWithSomethingNotConstant()
        {
            var runtime = Run(
                "const fun square(x: int): int { return x * x; }\n"
                    + "fun run(n: int): int { return square(n); }");

            Assert.Equal(36, Int(runtime, "run", SurtrValue.CreateInt(6)));
        }

        /// <summary>
        /// A constant *expression* argument folds exactly like a literal one — the disassembly-level
        /// confirmation that the call site itself disappears is in <c>LoweringChoiceTests</c>.
        /// </summary>
        [Fact]
        public void AConstFunctionFoldsAConstantExpressionArgumentToo()
        {
            var runtime = Run(
                "const fun square(x: int): int { return x * x; }\n"
                    + "fun run(): int { return square(2 + 3); }");

            Assert.Equal(25, Int(runtime, "run"));
        }

        [Fact]
        public void IncrementLeavesTheRightValueBehind()
        {
            var runtime = Run(
                "fun run(): int { var i = 5; let post = i++; let pre = ++i; return post * 100 + pre * 10 + i; }");

            // post reads 5, i becomes 6; pre makes it 7 and reads 7.
            Assert.Equal(577, Int(runtime, "run"));
        }

        [Fact]
        public void AValueClassIsItsFieldWhereTheTypeIsKnown()
        {
            var runtime = Run(
                "value class EntityId { public let raw: int; public constructor(raw: int) { this.raw = raw; } }\n"
                    + "fun run(): int { let id = EntityId(7); return id.raw; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>
        /// Building a <em>generic</em> value class reaches its constructor through the substituted
        /// clone (§6), but the body and the wrapped assignment are keyed by the declaration. The
        /// splice has to resolve through <c>OriginalDefinition</c> or every construction of one
        /// fails with a SURTR4001 instead of emitting the wrapped field.
        /// </summary>
        [Fact]
        public void AGenericValueClassConstructionSplicesItsDeclarationBody()
        {
            var runtime = Run(
                "value class Box<T> { public let value: T; public constructor(value: T) { this.value = value; } }\n"
                    + "fun run(): int { let b = Box<int>(7); return b.value; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AnArraysOwnMembersAreCallableFromSource()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let xs: int[] = [1, 2];\n"
                    + "  xs.push(3);\n"
                    + "  return xs.length * 100 + xs.get(2);\n"
                    + "}");

            Assert.Equal(303, Int(runtime, "run"));
        }
        #endregion

        #region Const bindings (§7.1)
        /// <summary>
        /// A module-level `const` has to fold into every use and carry no slot at all — the same
        /// promise §7.1 makes and, before this fix, the compiler did not keep: it compiled to an
        /// ordinary module variable indistinguishable from a `static let`.
        /// </summary>
        [Fact]
        public void AModuleConstCarriesNoSlot()
        {
            var module = Reload("const MaxEntities: int = 512;\nfun run(): int { return MaxEntities + 1; }");

            Assert.False(module.TryGetField("MaxEntities", out _));
        }

        [Fact]
        public void AModuleConstStillFoldsIntoEveryUse()
        {
            var runtime = Run("const MaxEntities: int = 512;\nfun run(): int { return MaxEntities + 1; }");

            Assert.Equal(513, Int(runtime, "run"));
        }

        [Fact]
        public void AClassConstCarriesNoSlot()
        {
            var module = Reload("class Physics {\n  const Gravity: float = -9.81;\n}\nfun run(): int { return 1; }");

            Assert.False(module.FindClass("Physics")!.TryGetField("Gravity", out _));
        }

        [Fact]
        public void AClassConstStillFoldsIntoEveryUse()
        {
            var runtime = Run(
                "class Physics {\n"
                    + "  const Gravity: float = -9.81;\n"
                    + "  public static fun fall(t: float): float { return Gravity * t; }\n"
                    + "}\n"
                    + "fun run(): float { return Physics.fall(2.0); }");

            Assert.Equal(-19.62, Call(runtime, "run").AsFloat, 3);
        }

        /// <summary>A local `const` carries no local slot either, and folds the same way.</summary>
        [Fact]
        public void ALocalConstFoldsAndCarriesNoSlot()
        {
            var runtime = Run("fun run(): int { const half = 21; return half + half; }");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void AConstWhoseTypeIsNotPrimitiveOrStringIsReported()
        {
            using var compilation = Reject(
                "class Vec2 { public let x: float = 0.0; }\nconst Origin: Vec2 = Vec2();");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidConstType);
        }

        [Fact]
        public void ALocalConstWhoseTypeIsNotPrimitiveOrStringIsReported()
        {
            using var compilation = Reject(
                "class Vec2 { public let x: float = 0.0; }\n"
                    + "fun run(): int { const v: Vec2 = Vec2(); return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidConstType);
        }

        /// <summary>A `const` still works as a parameter default (§3.5) with no slot of its own.</summary>
        [Fact]
        public void AModuleConstUsableAsADefaultCarriesNoSlotEither()
        {
            var runtime = Run(
                "const Base: int = 7;\n"
                    + "fun f(a: int = Base): int { return a; }\n"
                    + "fun run(): int { return f(); }");

            Assert.Equal(7, Int(runtime, "run"));
        }
        #endregion

        #region Parameter defaults (§3.5)
        [Fact]
        public void AnOmittedArgumentTakesItsDefault()
        {
            var runtime = Run(
                "fun spawn(x: int, hp: int = 100): int { return x * 1000 + hp; }\n"
                    + "fun run(): int { return spawn(1); }");

            Assert.Equal(1100, Int(runtime, "run"));
        }

        [Fact]
        public void AWrittenArgumentStillWins()
        {
            var runtime = Run(
                "fun spawn(x: int, hp: int = 100): int { return x * 1000 + hp; }\n"
                    + "fun run(): int { return spawn(1, 50); }");

            Assert.Equal(1050, Int(runtime, "run"));
        }

        [Fact]
        public void ANamedArgumentMaySkipADefaultedOne()
        {
            var runtime = Run(
                "fun make(a: int = 1, b: int = 2, c: int = 4): int { return a * 100 + b * 10 + c; }\n"
                    + "fun run(): int { return make(c: 9); }");

            Assert.Equal(129, Int(runtime, "run"));
        }

        [Fact]
        public void ADefaultMayBeAConstOrAConstFunction()
        {
            var runtime = Run(
                "const Base: int = 7;\n"
                    + "const fun twice(x: int): int { return x + x; }\n"
                    + "fun f(a: int = Base, b: int = twice(4)): int { return a * 100 + b; }\n"
                    + "fun run(): int { return f(); }");

            Assert.Equal(708, Int(runtime, "run"));
        }

        [Fact]
        public void AnIntegerDefaultWidensIntoAFloatParameter()
        {
            var runtime = Run(
                "fun scale(v: float = 2): float { return v * 3.0; }\nfun run(): float { return scale(); }");

            Assert.Equal(6.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void ADefaultThatDoesNotFoldIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "fun other(): int { return 1; }\nfun f(a: int = other()): int { return a; }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.NotAConstant);
        }

        [Fact]
        public void ADefaultSurvivesTheImage()
        {
            var emitter = Build("fun spawn(x: int, hp: int = 100): int { return x + hp; }");
            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes()).Instantiate();

            Assert.True(reloaded.TryGetMethods("spawn", out var overloads));
            Assert.True(overloads[0].Parameters[1].HasDefault);
            Assert.Equal(100, overloads[0].Parameters[1].DefaultValue.Value.AsInt);
        }

        /// <summary>
        /// `null` is itself a compile-time constant (the one no `const` declaration can produce,
        /// per <c>SurtrConstant</c>'s own remarks) and has to fold like any other literal default.
        /// </summary>
        [Fact]
        public void ANullDefaultOnAReferenceParameterFoldsToTheNullConstant()
        {
            var emitter = Build("fun f(x: string = null): string { return x; }");
            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes()).Instantiate();

            Assert.True(reloaded.TryGetMethods("f", out var overloads));
            Assert.True(overloads[0].Parameters[0].HasDefault);
            Assert.Equal(SurtrConstantKind.Null, overloads[0].Parameters[0].DefaultValue.Kind);
        }

        [Fact]
        public void ANullDefaultOnANullablePrimitiveFoldsToTheNullConstant()
        {
            var emitter = Build("fun f(x: int? = null): int? { return x; }");
            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes()).Instantiate();

            Assert.True(reloaded.TryGetMethods("f", out var overloads));
            Assert.True(overloads[0].Parameters[0].HasDefault);
            Assert.Equal(SurtrConstantKind.Null, overloads[0].Parameters[0].DefaultValue.Kind);
        }

        /// <summary>
        /// Before this folded, `ReportUnfoldedDefaults` could not tell "folded to null" apart from
        /// "never folded" and always reported <c>NotAConstant</c> for a `= null` default.
        /// </summary>
        [Fact]
        public void ANullDefaultReportsNoDiagnostic()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "fun f(x: string = null): string { return x; }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.False(
                compilation.HasErrors,
                "Unexpected: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        /// <summary>A comparison against the null literal folds like any other constant binary (§7.3).</summary>
        [Fact]
        public void ANullComparisonFoldsInADeclarationLevelConstIf()
        {
            var runtime = Run(
                "const if (null == null) {\n"
                    + "  fun run(): int { return 1; }\n"
                    + "} else {\n"
                    + "  fun run(): int { return 0; }\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ANullInequalityFoldsInADeclarationLevelConstIf()
        {
            var runtime = Run(
                "const if (null != null) {\n"
                    + "  fun run(): int { return 1; }\n"
                    + "} else {\n"
                    + "  fun run(): int { return 0; }\n"
                    + "}");

            Assert.Equal(0, Int(runtime, "run"));
        }
        #endregion

        #region Singletons (§2.8)
        [Fact]
        public void ASingletonIsBuiltOnceAndReachedByItsOwnName()
        {
            var runtime = Run(
                "singleton Counter {\n"
                    + "  public var value: int = 0;\n"
                    + "  public fun bump(): int { this.value = this.value + 1; return this.value; }\n"
                    + "}\n"
                    + "fun run(): int { Counter.bump(); Counter.bump(); return Counter.value; }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void ASingletonIsAValueAndSatisfiesItsInterface()
        {
            var runtime = Run(
                "interface Named { fun name(): string; }\n"
                    + "singleton Registry : Named { public fun name(): string { return \"registry\"; } }\n"
                    + "fun describe(n: Named): string { return n.name(); }\n"
                    + "fun run(): string { return describe(Registry); }");

            Assert.Equal("registry", Text(runtime, "run"));
        }

        [Fact]
        public void ASingletonHoldsItsStateAcrossCalls()
        {
            var runtime = Run(
                "singleton Store {\n"
                    + "  private var _entries: {string: int} = {};\n"
                    + "  public fun put(k: string, v: int): void { this._entries[k] = v; }\n"
                    + "  public fun get(k: string): int { return this._entries[k]; }\n"
                    + "}\n"
                    + "fun run(): int { Store.put(\"a\", 41); return Store.get(\"a\") + 1; }");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void ASingletonCannotDeclareAConstructor()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "singleton Bad { public constructor() { } }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidValueClass);
        }
        #endregion

        #region Bridges into a generic interface's erased slot
        [Fact]
        public void ATypedImplementationReachesAGenericContractThroughABridge()
        {
            var runtime = Run(
                "class Score : IComparable<Score> {\n"
                    + "  public let value: int;\n"
                    + "  public constructor(value: int) { this.value = value; }\n"
                    + "  public fun compareTo(other: Score): int { return this.value <=> other.value; }\n"
                    + "}\n"
                    + "fun order(a: IComparable<Score>, b: Score): int { return a.compareTo(b); }\n"
                    + "fun run(): int { return order(Score(9), Score(4)); }");

            Assert.True(Int(runtime, "run") > 0);
        }

        [Fact]
        public void ABridgeForwardsToWhicheverOverrideTheReceiverHas()
        {
            var runtime = Run(
                "class Base : IEquatable<Base> {\n"
                    + "  public virtual fun equals(other: Base): bool { return false; }\n"
                    + "}\n"
                    + "class Always : Base { public override fun equals(other: Base): bool { return true; } }\n"
                    + "fun same(a: IEquatable<Base>, b: Base): bool { return a.equals(b); }\n"
                    + "fun run(): bool { return same(Always(), Base()); }");

            Assert.True(Call(runtime, "run").AsBool);
        }
        #endregion

        #region Interface satisfaction without `override` (§3.3)
        /// <summary>
        /// A plain method — no `virtual`/`override` — satisfies an interface obligation as long as
        /// its signature matches. It stays `Direct` (callable straight off the concrete type), and
        /// a synthetic bridge occupies the interface's slot for calls that go through <c>IBar</c>.
        /// </summary>
        [Fact]
        public void ADirectMethodSatisfiesAnInterfaceWithoutOverride()
        {
            var runtime = Run(
                "interface IBar { fun doThing(): int; }\n"
                    + "class Foo : IBar {\n"
                    + "  public fun doThing(): int { return 42; }\n"
                    + "}\n"
                    + "fun viaInterface(b: IBar): int { return b.doThing(); }\n"
                    + "fun run(): int { let f = Foo(); return f.doThing() + viaInterface(f); }");

            Assert.Equal(84, Int(runtime, "run"));
        }

        /// <summary>The property twin of <see cref="ADirectMethodSatisfiesAnInterfaceWithoutOverride"/>: an auto-property with no `override` still fills a get/set contract.</summary>
        [Fact]
        public void ADirectAutoPropertySatisfiesAnInterfacePropertyWithoutOverride()
        {
            var runtime = Run(
                "interface INamed { name: string { get; set; } }\n"
                    + "class C : INamed { public name: string { get; set; } }\n"
                    + "fun run(): string { let c = C(); c.name = \"x\"; let n: INamed = c; return n.name; }");

            Assert.Equal("x", Text(runtime, "run"));
        }

        /// <summary>
        /// A class may still write `virtual`/`override` for a member it wants a subclass to be able
        /// to replace further — that path is untouched, and the two forms interoperate: a bridge
        /// forwards to whichever the class actually declared.
        /// </summary>
        [Fact]
        public void AVirtualMethodStillSatisfiesAnInterfaceAndRemainsOverridable()
        {
            var runtime = Run(
                "interface IBar { fun doThing(): int; }\n"
                    + "class Foo : IBar { public virtual fun doThing(): int { return 1; } }\n"
                    + "class Sub : Foo { public override fun doThing(): int { return 2; } }\n"
                    + "fun viaInterface(b: IBar): int { return b.doThing(); }\n"
                    + "fun run(): int { return viaInterface(Sub()); }");

            Assert.Equal(2, Int(runtime, "run"));
        }
        #endregion

        #region Value classes (§2.9)
        [Fact]
        public void AValueClassMethodIsCallableOnItsOwnType()
        {
            var runtime = Run(
                "value class EntityId {\n"
                    + "  public let raw: int;\n"
                    + "  public constructor(raw: int) { this.raw = raw; }\n"
                    + "  public fun doubled(): int { return this.raw * 2; }\n"
                    + "}\n"
                    + "fun run(): int { let id = EntityId(21); return id.doubled(); }");

            Assert.Equal(42, Int(runtime, "run"));
        }

        /// <summary>
        /// A computed property's getter is a call on the receiver too (§6.3's boxing rule applies
        /// to it exactly as it does to an ordinary method call) — the wrapped field stays `let`
        /// (§2.9), so the property only reads it back transformed.
        /// </summary>
        [Fact]
        public void AValueClassComputedPropertyReadsThroughItsGetter()
        {
            var runtime = Run(
                "value class Meters {\n"
                    + "  public let raw: int;\n"
                    + "  public constructor(raw: int) { this.raw = raw; }\n"
                    + "  public doubled: int { get { return this.raw * 2; } }\n"
                    + "}\n"
                    + "fun run(): int { let m = Meters(10); return m.doubled; }");

            Assert.Equal(20, Int(runtime, "run"));
        }

        [Fact]
        public void AValueClassStillCostsNothingWhereItsTypeIsKnown()
        {
            var runtime = Run(
                "value class EntityId { public let raw: int; public constructor(raw: int) { this.raw = raw; } }\n"
                    + "fun run(): int { let id = EntityId(7); return id.raw; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AValueClassWithNoConstructorIsBuiltFromItsField()
        {
            // A value class that declares no constructor gets a synthetic one taking the type of
            // its single `let` field and assigning it, so `EntityId(7)` binds and yields `7`.
            var runtime = Run(
                "value class EntityId { public let raw: int; }\n"
                    + "fun run(): int { let id = EntityId(7); return id.raw; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AValueClassWithNoConstructorRejectsZeroOrSeveralArguments()
        {
            // The synthetic value-class constructor takes exactly one argument (the field's type),
            // so zero or several arguments is a clean binding error, not an emit-time crash.
            using var zero = Reject("value class EntityId { public let raw: int; }\nfun run(): int { return EntityId().raw; }");
            Assert.Contains(zero.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnresolvedCall);

            using var several = Reject("value class EntityId { public let raw: int; }\nfun run(): int { return EntityId(1, 2).raw; }");
            Assert.Contains(several.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnresolvedCall);
        }

        [Fact]
        public void AValueClassFlowingIntoAnErasedSlotIsBoxedAsItself()
        {
            var runtime = Run(
                "value class EntityId {\n"
                    + "  public let raw: int;\n"
                    + "  public constructor(raw: int) { this.raw = raw; }\n"
                    + "}\n"
                    + "fun run(): int { let u: unknown = EntityId(5); let back = u as EntityId; return back.raw; }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>
        /// Value correctness for <see cref="LoweringChoiceTests.AValueClassMethodSatisfyingAnInterfaceWithoutOverrideDoesNotBoxOnADirectCall"/>:
        /// the same method has to answer correctly both directly and through the interface, since a
        /// bridge now stands between the interface and the `Direct` body.
        /// </summary>
        [Fact]
        public void AValueClassMethodSatisfyingAnInterfaceWithoutOverrideWorksBothWays()
        {
            var runtime = Run(
                "interface IDoubling { fun doubled(): int; }\n"
                    + "value class EntityId : IDoubling {\n"
                    + "  public let raw: int;\n"
                    + "  public constructor(raw: int) { this.raw = raw; }\n"
                    + "  public fun doubled(): int { return this.raw * 2; }\n"
                    + "}\n"
                    + "fun viaInterface(d: IDoubling): int { return d.doubled(); }\n"
                    + "fun run(): int { let id = EntityId(21); return id.doubled() + viaInterface(id); }");

            Assert.Equal(84, Int(runtime, "run"));
        }
        #endregion

        #region Nested lambdas
        [Fact]
        public void ALambdaInsideALambdaCapturesThroughTheOuterOne()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let base = 40;\n"
                    + "  let outer = (a: int) => ((b: int) => a + b + base)(1);\n"
                    + "  return outer(1);\n"
                    + "}");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void ANestedLambdaMayCaptureTheReceiverThroughItsOuterOne()
        {
            var runtime = Run(
                "class Adder {\n"
                    + "  public var offset: int = 10;\n"
                    + "  public fun make(): (int) -> int { return (x: int) => ((y: int) => y + this.offset)(x); }\n"
                    + "}\n"
                    + "fun run(): int { return Adder().make()(5); }");

            Assert.Equal(15, Int(runtime, "run"));
        }

        [Fact]
        public void ANestedLambdaReturnedFromTheOuterOneStillSeesTheCapture()
        {
            var runtime = Run(
                "fun make(): (int) -> (int) -> int {\n"
                    + "  let scale = 3;\n"
                    + "  return (a: int) => (b: int) => (a + b) * scale;\n"
                    + "}\n"
                    + "fun run(): int { return make()(2)(5); }");

            Assert.Equal(21, Int(runtime, "run"));
        }
        #endregion

        #region Closures held in members (§8)
        [Fact]
        public void AClosureInAStaticIsCalledThroughItsTypeName()
        {
            var runtime = Run(
                "class First { public static let Make: () -> int = () => 5; }\n"
                    + "fun run(): int { return First.Make(); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void AClosureInAnInstanceFieldIsCalledThroughTheReceiver()
        {
            var runtime = Run(
                "class Box {\n"
                    + "  public let handler: (int) -> int;\n"
                    + "  public constructor(h: (int) -> int) { this.handler = h; }\n"
                    + "}\n"
                    + "fun run(): int { return Box((x: int) => x * 3).handler(3); }");

            Assert.Equal(9, Int(runtime, "run"));
        }

        [Fact]
        public void AClosureFromAPropertyIsCalledTheSameWay()
        {
            var runtime = Run(
                "class Box { public handler: () -> int { get { return () => 4; } } }\n"
                    + "fun run(): int { return Box().handler(); }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void ASingletonsClosureIsReachedThroughItsName()
        {
            var runtime = Run(
                "singleton Registry { public let make: () -> int = () => 6; }\n"
                    + "fun run(): int { return Registry.make(); }");

            Assert.Equal(6, Int(runtime, "run"));
        }

        /// <summary>§5.1: the guard wraps the invocation, so a null receiver calls nothing.</summary>
        [Fact]
        public void ANullReceiverCallsNoClosureAtAll()
        {
            var runtime = Run(
                "class Box { public let handler: () -> int = () => 7; }\n"
                    + "fun call(b: Box?): int { let v = b?.handler(); return v == null ? 0 : v!!; }\n"
                    + "fun present(): int { return call(Box()); }\n"
                    + "fun absent(): int { return call(null); }");

            Assert.Equal(7, Int(runtime, "present"));
            Assert.Equal(0, Int(runtime, "absent"));
        }
        #endregion

        #region Method-group to closure (§8)
        [Fact]
        public void ABareModuleFunctionNameConvertsToAClosureWithNoLambdaWritten()
        {
            var runtime = Run(
                "fun add(a: int, b: int): int { return a + b; }\n"
                    + "fun run(): int { let f: (int, int) -> int = add; return f(2, 3); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void AStaticMethodConvertsToAClosureThroughItsTypeName()
        {
            var runtime = Run(
                "class Math2 { public static fun square(x: int): int { return x * x; } }\n"
                    + "fun run(): int { let f: (int) -> int = Math2.square; return f(6); }");

            Assert.Equal(36, Int(runtime, "run"));
        }

        [Fact]
        public void AnInstanceMethodConvertsToAClosureThroughTheImplicitThis()
        {
            var runtime = Run(
                "class Counter {\n"
                    + "  private var _value: int;\n"
                    + "  public constructor(v: int) { _value = v; }\n"
                    + "  public fun getValue(): int { return _value; }\n"
                    + "  public fun asClosure(): () -> int { let f: () -> int = getValue; return f; }\n"
                    + "}\n"
                    + "fun run(): int { return Counter(41).asClosure()(); }");

            Assert.Equal(41, Int(runtime, "run"));
        }

        [Fact]
        public void AnInstanceMethodConvertsToAClosureThroughAnExplicitReceiver()
        {
            var runtime = Run(
                "class Counter {\n"
                    + "  private var _value: int;\n"
                    + "  public constructor(v: int) { _value = v; }\n"
                    + "  public fun getValue(): int { return _value; }\n"
                    + "}\n"
                    + "fun run(): int { let c = Counter(9); let f: () -> int = c.getValue; return f(); }");

            Assert.Equal(9, Int(runtime, "run"));
        }

        [Fact]
        public void AVoidMethodConvertsToAVoidClosureAndRunsForEffect()
        {
            var runtime = Run(
                "class Counter {\n"
                    + "  private var _value: int;\n"
                    + "  public fun bump(): void { _value = _value + 1; }\n"
                    + "  public fun value(): int { return _value; }\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "  let c = Counter();\n"
                    + "  let f: () -> void = c.bump;\n"
                    + "  f(); f(); f();\n"
                    + "  return c.value();\n"
                    + "}");

            Assert.Equal(3, Int(runtime, "run"));
        }

        /// <summary>
        /// The receiver's own value is captured once, at conversion time - not re-read on every
        /// call - and a virtual method still dispatches through the captured receiver's real class,
        /// exactly as `c.speak()` written directly would.
        /// </summary>
        [Fact]
        public void AVirtualMethodClosureDispatchesThroughTheReceiversActualClass()
        {
            var runtime = Run(
                "class Animal { public virtual fun speak(): int { return 1; } }\n"
                    + "class Dog : Animal { public override fun speak(): int { return 2; } }\n"
                    + "fun run(): int { let a: Animal = Dog(); let f: () -> int = a.speak; return f(); }");

            Assert.Equal(2, Int(runtime, "run"));
        }
        #endregion

        #region Refusals
        [Fact]
        public void OverridingASealedMemberIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class A { public virtual fun f(): int { return 1; } }\n"
                    + "class B : A { public sealed override fun f(): int { return 2; } }\n"
                    + "class C : B { public override fun f(): int { return 3; } }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidBaseType);
        }

        [Fact]
        public void ACompilationWithErrorsIsNotEmitted()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", "fun run(): int { return nope; }");

            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(compilation.HasErrors);
            Assert.False(new ModuleEmitter(compilation, binder).TryEmit());
        }

        /// <summary>
        /// Compiles something emission gives up on, and hands back what it reported.
        /// </summary>
        /// <remarks>
        /// An integer literal too wide for an <c>int</c> is the one construct that binds cleanly and
        /// then cannot be lowered, which makes it the only way to reach these paths from source.
        /// </remarks>
        private IReadOnlyList<SurtrDiagnostic> Unlowerable(string source, params (string Path, string Text)[] extra)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            foreach (var (path, text) in extra)
                project.AddSourceFile(Root + path, text);

            var compilation = SurtrCompilation.Create(project);
            _owned.Add(compilation);

            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.False(compilation.HasErrors, "This is meant to bind cleanly and fail at emit.");
            Assert.False(new ModuleEmitter(compilation, binder).TryEmit());

            return compilation.Diagnostics.Where(d => d.Code == SurtrDiagnosticCode.NotLowered).ToList();
        }

        [Fact]
        public void AnEmitFailureUnderlinesWhatCausedIt()
        {
            var reported = Assert.Single(Unlowerable("fun run(): int { return 99999999999; }"));

            Assert.Equal("99999999999".Length, reported.Span.Length);
            Assert.Equal(1, reported.Span.Start.Line);
        }

        [Fact]
        public void EveryMemberThatFailsIsReported()
        {
            var reported = Unlowerable(
                "fun a(): int { return 99999999999; }\n"
                + "fun b(): int { return 99999999999; }\n"
                + "fun c(): int { return 1; }");

            Assert.Equal(2, reported.Count);
        }

        [Fact]
        public void ItIsReportedAgainstTheFileTheMemberIsIn()
        {
            var reported = Assert.Single(Unlowerable(
                "fun run(): int { return 1; }",
                ("/game/core/Other.surtr", "fun other(): int { return 99999999999; }")));

            Assert.EndsWith("Other.surtr", reported.SourceName);
        }
        #endregion

        #region Constructor chaining (§3.2)
        [Fact]
        public void ASuperChainRunsTheBaseConstructor()
        {
            var runtime = Run(
                "class Animal {\n"
                    + "  public let name: string;\n"
                    + "  public constructor(name: string) { this.name = name; }\n"
                    + "}\n"
                    + "class Dog : Animal {\n"
                    + "  public constructor(name: string) : super(name) { }\n"
                    + "}\n"
                    + "fun run(): string { return Dog(\"rex\").name; }");

            Assert.Equal("rex", Text(runtime, "run"));
        }

        [Fact]
        public void AThisChainRunsTheOtherConstructor()
        {
            var runtime = Run(
                "class C {\n"
                    + "  public var n: int = 0;\n"
                    + "  public constructor() : this(5) { }\n"
                    + "  public constructor(n: int) { this.n = n; }\n"
                    + "}\n"
                    + "fun run(): int { return C().n; }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>
        /// §3.2: the chained-to constructor already ran them, so running them again would undo
        /// whatever it did with them.
        /// </summary>
        [Fact]
        public void AThisChainDoesNotRerunTheInstanceInitializers()
        {
            var runtime = Run(
                "class C {\n"
                    + "  public var log: int = 0;\n"
                    + "  public constructor() : this(0) { log += 1; }\n"
                    + "  public constructor(n: int) { }\n"
                    + "}\n"
                    + "fun run(): int { return C().log; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void TheInstanceInitializersRunAfterTheSuperChain()
        {
            var runtime = Run(
                "class Base { public var b: int = 0; public constructor(b: int) { this.b = b; } }\n"
                    + "class Derived : Base {\n"
                    + "  public var d: int = 4;\n"
                    + "  public constructor() : super(6) { }\n"
                    + "}\n"
                    + "fun run(): int { let x = Derived(); return x.b + x.d; }");

            Assert.Equal(10, Int(runtime, "run"));
        }

        /// <summary>§3.2: a constructor that omits the chain still reaches the base's parameterless one.</summary>
        [Fact]
        public void AConstructorWithNoChainStillReachesItsBase()
        {
            var runtime = Run(
                "class Base { public var n: int = 0; public constructor() { n = 7; } }\n"
                    + "class Derived : Base { public constructor() { } }\n"
                    + "fun run(): int { return Derived().n; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>
        /// A derived class declaring nothing at all still has to be constructed: <c>ObjNew</c> only
        /// allocates, so without a synthesised constructor the base's initializers never run.
        /// </summary>
        [Fact]
        public void ADerivedClassWithNoMembersStillRunsItsBasesInitializers()
        {
            var runtime = Run(
                "class Base { public var n: int = 7; }\n"
                    + "class Derived : Base { }\n"
                    + "fun run(): int { return Derived().n; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AChainReachesThroughThreeLevels()
        {
            var runtime = Run(
                "class A { public var n: int = 0; public constructor(n: int) { this.n = n; } }\n"
                    + "class B : A { public constructor(n: int) : super(n + 1) { } }\n"
                    + "class C : B { public constructor() : super(5) { } }\n"
                    + "fun run(): int { return C().n; }");

            Assert.Equal(6, Int(runtime, "run"));
        }

        [Fact]
        public void AChainToASuperThatDoesNotExistIsReported()
        {
            // Every class now implicitly extends `object`, so `super()` with no arguments against
            // a base that declares no constructor is legal (it calls nothing, same as an omitted
            // chain - BodyBinder.Expressions.cs's TryResolveConstructor says so explicitly). What
            // is still illegal is passing an argument to a base with no constructor to receive it -
            // reported as an unresolved call against zero candidates, not InvalidConstructorChain.
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class C { public constructor() : super(5) { } }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnresolvedCall);
        }

        /// <summary>
        /// §3.2 gives an omitted chain one meaning — the base's parameterless constructor — so where
        /// the base has none, the omission names nothing and the base would go unconstructed.
        /// </summary>
        [Fact]
        public void AConstructorWithNoChainWhoseBaseHasNoParameterlessOneIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class A { public var n: int = 0; public constructor(n: int) { this.n = n; } }\n"
                    + "class B : A { public constructor() { } }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.BaseConstructorUnreachable);
        }

        /// <summary>The same case, reached by declaring no constructor at all.</summary>
        [Fact]
        public void AClassWithNoConstructorWhoseBaseNeedsArgumentsIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class A { public constructor(n: int) { } }\nclass B : A { }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.BaseConstructorUnreachable);
        }

        /// <summary>
        /// Constructors are not inherited, so a grandparent's parameterless one does not answer for
        /// the parent that sits between.
        /// </summary>
        [Fact]
        public void AGrandparentsParameterlessConstructorDoesNotSatisfyTheParent()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class A { public constructor() { } }\n"
                    + "class B : A { public constructor(n: int) : super() { } }\n"
                    + "class C : B { }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.BaseConstructorUnreachable);
        }

        /// <summary>
        /// A base that declares no constructor needs nothing called: its initializers run from the
        /// parameterless one the emitter synthesises for it.
        /// </summary>
        [Fact]
        public void ABaseThatDeclaresNoConstructorNeedsNoChain()
        {
            var runtime = Run(
                "class A { public var n: int = 5; }\nclass B : A { }\nfun run(): int { return B().n; }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>
        /// §9's own shape: every library exception takes a message, so a subclass has to pass one up.
        /// </summary>
        [Fact]
        public void AUserExceptionChainsItsMessageIntoTheLibrary()
        {
            var runtime = Run(
                "class BadThing : Exception { constructor(message: string) : super(message) { } }\n"
                    + "fun run(): string {\n"
                    + "  try { throw BadThing(\"nope\"); }\n"
                    + "  catch (e: BadThing) { return e.message; }\n"
                    + "}");

            Assert.Equal("nope", Text(runtime, "run"));
        }

        [Fact]
        public void AThisChainThatLoopsBackIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class C {\n"
                    + "  public constructor() : this(1) { }\n"
                    + "  public constructor(n: int) : this() { }\n"
                    + "}");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidConstructorChain);
        }

        /// <summary>
        /// The synthesised constructor has no symbol, so a creation site in another module can only
        /// reach it through metadata the emitter carried across.
        /// </summary>
        [Fact]
        public void ConstructingAClassFromAnotherModuleRunsItsInitializers()
        {
            var runtime = Run(
                "import game.util.Thing;\nfun run(): int { return Thing().n; }",
                ("/game/util/Thing.surtr", "public class Thing { public let n: int = 6; }"));

            Assert.Equal(6, Int(runtime, "run"));
        }

        /// <summary>
        /// §2.6 lets a fully qualified name reach a type with no <c>import</c> at all, and binding
        /// already resolved one that way (<see cref="Binding.BinderTests.AFullyQualifiedNameWorksWithoutAnImport"/>)
        /// — but until now, the dependency graph <see cref="ModuleEmitter"/> emits in
        /// (<c>SurtrCompilation.LoadOrder</c>) only ever learned about an edge from an explicit
        /// <c>import</c>, scanned once at parse time before binding ran. A construction reached only
        /// through a fully qualified name had no edge recorded at all, so the two modules could come
        /// out in either relative order — and calling into whichever one hadn't been built yet threw
        /// "uses a call to 'ctor', which is neither being emitted here nor already built" (SURTR4001)
        /// at emission, though binding itself reported nothing wrong. Fixed by having
        /// <c>TypeResolver</c> record the edge itself, the moment it resolves such a name, and having
        /// <c>ModuleEmitter</c> ask <c>SurtrCompilation</c> to recompute the load order right before
        /// it starts emitting — by which point binding has always finished discovering every one.
        /// </summary>
        [Fact]
        public void ConstructingAClassFromAnotherModuleWorksWithNoImportAtAll()
        {
var runtime = Run(
                  "fun run(): int { return game.util.Thing.Thing(9).n(); }",
                  ("/game/util/Thing.surtr", "public class Thing { private let _n: int; public constructor(n: int) { _n = n; } public fun n(): int { return _n; } }"));

            Assert.Equal(9, Int(runtime, "run"));
        }

        /// <summary>
        /// The same gap, for a class whose constructor is <em>written</em> rather than synthesised —
        /// the shape <see cref="ConstructingAClassFromAnotherModuleWorksWithNoImportAtAll"/> exercises,
        /// but confirmed once more against exactly the reduced case the bug was first reproduced with.
        /// </summary>
        [Fact]
        public void AnExplicitConstructorFromAnotherModuleIsCallableWithNoImport()
        {
var runtime = Run(
                  "fun run(): int {\n"
                      + "  let simple = game.util.Simple.Simple(4);\n"
                      + "  return simple.get();\n"
                      + "}",
                  ("/game/util/Simple.surtr",
                      "public class Simple {\n"
                          + "  private var _n: int;\n"
                          + "  public constructor(n: int) { this._n = n; }\n"
                          + "  public fun get(): int { return this._n; }\n"
                          + "}"));

            Assert.Equal(4, Int(runtime, "run"));
        }
        #endregion

        #region Static blocks (§2.5, §3.2)
        [Fact]
        public void AModuleStaticBlockRunsAtLoad()
        {
            var runtime = Run("var counter: int = 0;\nstatic { counter = 7; }\nfun run(): int { return counter; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AClassStaticBlockRunsAtLoad()
        {
            var runtime = Run(
                "class C { public static var n: int = 1; static { n = 7; } }\nfun run(): int { return C.n; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>
        /// §2.5 runs a block in the source position it appears among the field initializers, so a
        /// block reads what the ones above it wrote and is read by the ones below.
        /// </summary>
        [Fact]
        public void AStaticBlockRunsInItsSourcePositionAmongTheInitializers()
        {
            var runtime = Run(
                "var a: int = 1;\nstatic { a += 1; }\nvar b: int = 10;\nstatic { b += a; }\nfun run(): int { return b; }");

            Assert.Equal(12, Int(runtime, "run"));
        }
        #endregion

        #region Nullable access (§5.1)
        [Fact]
        public void ASafeNavigationYieldsNullInsteadOfFaulting()
        {
            var runtime = Run(
                "class Holder { public let name: string = \"x\"; }\n"
                    + "fun run(): int {\n"
                    + "  let h: Holder? = null;\n"
                    + "  let n = h?.name;\n"
                    + "  return n == null ? 1 : 0;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ASafeNavigationReadsTheMemberWhenTheReceiverIsThere()
        {
            var runtime = Run(
                "class Holder { public let name: string = \"x\"; }\n"
                    + "fun run(): string { let h: Holder? = Holder(); return h?.name ?? \"fallback\"; }");

            Assert.Equal("x", Text(runtime, "run"));
        }

        /// <summary>A primitive member's absence is the absent tag, which is what <c>??</c> tests.</summary>
        [Fact]
        public void ASafeNavigationOnAPrimitiveMemberCoalesces()
        {
            var runtime = Run(
                "class Holder { public let size: int = 9; }\n"
                    + "fun run(): int { let h: Holder? = null; return h?.size ?? 4; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void ASafeNavigationChainShortCircuitsAtTheFirstNull()
        {
            var runtime = Run(
                "class Inner { public let name: string = \"x\"; }\n"
                    + "class Outer { public var inner: Inner? = null; }\n"
                    + "fun run(): int {\n"
                    + "  let o: Outer? = Outer();\n"
                    + "  return o?.inner?.name == null ? 1 : 0;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>
        /// The receiver is evaluated once, which is the half of <c>?.</c> that a re-evaluating
        /// lowering would get wrong without ever looking wrong.
        /// </summary>
        [Fact]
        public void ASafeNavigationEvaluatesItsReceiverOnce()
        {
            var runtime = Run(
                "var calls: int = 0;\n"
                    + "class Holder { public let name: string = \"x\"; }\n"
                    + "fun make(): Holder? { calls += 1; return null; }\n"
                    + "fun run(): int { let n = make()?.name; return calls; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ANullAssertionThrowsWhenItDoesNotHold()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let s: string? = null;\n"
                    + "  try { let t = s!!; return 0; }\n"
                    + "  catch (e: NullReferenceException) { return 1; }\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ANullAssertionPassesTheValueThroughWhenItHolds()
        {
            var runtime = Run("fun run(): string { let s: string? = \"x\"; return s!!; }");

            Assert.Equal("x", Text(runtime, "run"));
        }

        [Fact]
        public void ANullAssertionOnAnAbsentPrimitiveThrows()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  var n: int? = null;\n"
                    + "  try { let v = n!!; return 0; }\n"
                    + "  catch (e: NullReferenceException) { return 1; }\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>
        /// A present <c>0</c> is not absence: a reference is its 32-bit payload, so the two would be
        /// one value without the absent tag.
        /// </summary>
        [Fact]
        public void APresentZeroIsNotAbsent()
        {
            var runtime = Run("fun run(): int { var n: int? = 0; return n ?? 7; }");

            Assert.Equal(0, Int(runtime, "run"));
        }

        /// <summary>
        /// Every value of a nullable primitive is present, and <c>1</c> is not a special case.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It was. Comparing a nullable primitive against <c>null</c> used to emit <c>PushAbsent</c>
        /// against <c>EQ</c>/<c>NE</c>, and those are the integer opcodes: they compare the low 32
        /// bits, because int, bool and char share a representation and differ only in their tag.
        /// Absence differs from a present value in nothing <em>but</em> its tag, and the payload
        /// <c>PushAbsent</c> leaves there is the missing primitive's type code — so an <c>int?</c>
        /// holding <c>SurtrValueTypeCode.Integer</c>, which is 1, compared equal to null.
        /// </para>
        /// <para>
        /// The neighbouring test could never have caught it: 0 is the one int whose payload does
        /// not collide with a type code. This one sweeps a range, which is what it takes.
        /// </para>
        /// </remarks>
        [Fact]
        public void NoValueOfANullablePrimitiveReadsAsAbsent()
        {
            var runtime = Run("""
                fun mask(n: int): int {
                    var m: int = 0;
                    for (var i = 0; i < n; i += 1) {
                        let v: int? = i;
                        if (v == null) { m = m + (1 << i); }
                        if (!(v != null)) { m = m + (1 << i); }
                        if ((v ?? -1) != i) { m = m + (1 << i); }
                    }
                    return m;
                }
                """);

            Assert.Equal(0, Int(runtime, "mask", SurtrValue.CreateInt(24)));
        }

        [Fact]
        public void ACharacterWhoseCodeUnitIsATypeCodeIsStillPresent()
        {
            // The char type code is 4, so U+0004 is the char-shaped form of the same
            // collision. Written as an escape rather than as the raw control character,
            // which no editor, encoding or diff viewer along the way can be trusted to carry.
            var runtime = Run("""
                fun run(): int {
                    let c: char? = '\u0004';
                    if (c == null) { return 1; }
                    return 0;
                }
                """);

            Assert.Equal(0, Int(runtime, "run"));
        }

        /// <summary>
        /// The float side failed the other way: absent-float is a NaN, and <c>FEQ</c> answers false
        /// however it is asked, so an absent <c>float?</c> compared <em>unequal</em> to null.
        /// </summary>
        [Fact]
        public void AnAbsentFloatComparesEqualToNull()
        {
            var runtime = Run("""
                fun run(): int {
                    let absent: float? = null;
                    let present: float? = 1.5;
                    var acc: int = 0;
                    if (absent == null) { acc = acc + 1; }
                    if (present != null) { acc = acc + 2; }
                    if (null == absent) { acc = acc + 4; }
                    return acc;
                }
                """);

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void ABooleanNullableDistinguishesFalseFromAbsent()
        {
            var runtime = Run("""
                fun run(): int {
                    let no: bool? = false;
                    let absent: bool? = null;
                    var acc: int = 0;
                    if (no == null) { acc = acc + 1; }
                    if (absent == null) { acc = acc + 2; }
                    return acc;
                }
                """);

            Assert.Equal(2, Int(runtime, "run"));
        }
        #endregion

        #region Null and instanceof checks — value correctness for LoweringChoiceTests' shape assertions

        [Fact]
        public void ANullEqualityOnAReferenceComputesCorrectly()
        {
            var runtime = Run(
                "class Box { }\n"
                    + "fun run(): int {\n"
                    + "  let present: Box? = Box();\n"
                    + "  let absent: Box? = null;\n"
                    + "  var acc = 0;\n"
                    + "  if (present == null) { acc = acc + 1; }\n"
                    + "  if (absent == null) { acc = acc + 2; }\n"
                    + "  return acc;\n"
                    + "}");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void ANullEqualityBranchComputesCorrectly()
        {
            var runtime = Run(
                "class Box { }\n"
                    + "fun run(a: Box?): int { if (a == null) { return 1; } return 0; }\n"
                    + "fun call(): int { return run(null) * 10 + run(Box()); }");

            Assert.Equal(10, Int(runtime, "call"));
        }

        [Fact]
        public void AStringNullCheckComputesCorrectly()
        {
            var runtime = Run(
                "fun run(s: string?): bool { return s != null; }\n"
                    + "fun call(): int { return (run(\"hi\") ? 1 : 0) * 10 + (run(null) ? 1 : 0); }");

            Assert.Equal(10, Int(runtime, "call"));
        }

        [Fact]
        public void ANullablePrimitiveNullCheckBranchComputesCorrectly()
        {
            var runtime = Run(
                "fun run(n: int?): int { if (n == null) { return 1; } return 0; }\n"
                    + "fun call(): int {\n"
                    + "  let absent: int? = null;\n"
                    + "  let present: int? = 7;\n"
                    + "  return run(absent) * 10 + run(present);\n"
                    + "}");

            Assert.Equal(10, Int(runtime, "call"));
        }

        /// <summary>
        /// A bare `null` argument is bound with no expected type (overload resolution has not yet
        /// picked a parameter to convert it against - <c>BodyBinder.BindArguments</c>), so it
        /// carries <c>ErrorType</c> as a placeholder until <c>BodyBinder.Convert</c> retypes it
        /// against the chosen parameter. That retyping used to be unreachable: `Convert`'s general
        /// "already broken, don't cascade" bail-out on <c>expression.Type.IsError</c> caught the
        /// placeholder first and returned the still-untyped literal, which <c>EmitLiteral</c> then
        /// read as a plain null *reference* (<c>LoadNull</c>) instead of the absent tag §5.1
        /// requires for a nullable primitive - so `n ?? -1` silently reinterpreted the null
        /// reference's all-zero payload as a present `0`.
        /// </summary>
        [Fact]
        public void ANullArgumentToANullablePrimitiveParameterIsTheAbsentTag()
        {
            var runtime = Run(
                "fun run(n: int?): int { return n ?? -1; }\n"
                    + "fun call(): int { return run(null) * 100 + run(9); }");

            Assert.Equal(-91, Int(runtime, "call"));
        }

        [Fact]
        public void AnInstanceOfBranchComputesCorrectly()
        {
            var runtime = Run(
                "class Animal { }\nclass Dog : Animal { }\nclass Cat : Animal { }\n"
                    + "fun run(a: Animal): int { if (a is Dog) { return 1; } return 0; }\n"
                    + "fun call(): int { return run(Dog()) * 10 + run(Cat()); }");

            Assert.Equal(10, Int(runtime, "call"));
        }

        [Fact]
        public void ASafeCastOnAPrimitiveStillComputesBothOutcomes()
        {
            var runtime = Run(
                "fun run(u: unknown): int {\n"
                    + "  let n = u as? int;\n"
                    + "  return n ?? -1;\n"
                    + "}\n"
                    + "fun call(): int { return run(5) * 100 + run(\"nope\"); }");

            Assert.Equal(499, Int(runtime, "call"));
        }

        #endregion

        #region Varargs (§3.5)
        [Fact]
        public void AVarargsCallAbsorbsTheSurplus()
        {
            var runtime = Run(
                "fun count(first: string, rest: string...): int { return rest.length; }\n"
                    + "fun run(): int { return count(\"a\", \"b\", \"c\"); }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void AVarargsCallWithNoSurplusPacksAnEmptyArray()
        {
            var runtime = Run(
                "fun count(first: string, rest: string...): int { return rest.length; }\n"
                    + "fun run(): int { return count(\"a\"); }");

            Assert.Equal(0, Int(runtime, "run"));
        }

        [Fact]
        public void AVarargsParameterMayBePassedAWholeArray()
        {
            var runtime = Run(
                "fun count(first: string, rest: string...): int { return rest.length; }\n"
                    + "fun run(): int { return count(\"a\", [\"b\", \"c\", \"d\"]); }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        [Fact]
        public void TheBodySeesAVarargsParameterAsAnArray()
        {
            var runtime = Run(
                "fun first(prefix: string, rest: string...): string { return rest.get(0); }\n"
                    + "fun run(): string { return first(\"a\", \"b\", \"c\"); }");

            Assert.Equal("b", Text(runtime, "run"));
        }

        /// <summary>§13.4's own shape, which was unreachable while varargs did not resolve.</summary>
        [Fact]
        public void StringFormatIsCallableFromSource()
        {
            var runtime = Run("fun run(): string { return string.format(\"{0}-{1}\", \"a\", \"b\"); }");

            Assert.Equal("a-b", Text(runtime, "run"));
        }

        [Fact]
        public void AVarargsSignatureSurvivesAModuleBoundary()
        {
            var runtime = Run(
                "import game.util.*;\nfun run(): int { return tally(\"a\", \"b\", \"c\"); }",
                ("/game/util/M.surtr", "public fun tally(first: string, rest: string...): int { return rest.length; }"));

            Assert.Equal(2, Int(runtime, "run"));
        }
        #endregion

        #region Interfaces (§2.3, §3.4)
        /// <summary>
        /// §2.3 allows a nested type in a contract: it carries no state, so it does not reopen the
        /// "pure contract" rule.
        /// </summary>
        [Fact]
        public void AnEnumNestedInAnInterfaceLoadsAndResolves()
        {
            var runtime = Run(
                "interface IShape {\n"
                    + "  enum Kind { Circle, Square }\n"
                    + "  fun getKind(): Kind;\n"
                    + "}\n"
                    + "class Circle : IShape {\n"
                    + "  public fun getKind(): IShape.Kind { return IShape.Kind.Circle; }\n"
                    + "}\n"
                    + "fun run(): int { let c: IShape = Circle(); return c.getKind() == IShape.Kind.Circle ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AClassNestedInAnInterfaceLoadsAndResolves()
        {
            var runtime = Run(
                "interface IFactory {\n"
                    + "  public class Handle { public let id: int = 3; public constructor() { } }\n"
                    + "  fun make(): Handle;\n"
                    + "}\n"
                    + "class F : IFactory { public fun make(): IFactory.Handle { return IFactory.Handle(); } }\n"
                    + "fun run(): int { let f: IFactory = F(); return f.make().id; }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        /// <summary>
        /// A property satisfying a contract is written <c>override</c> like one replacing a base —
        /// §2.2 makes a contract a promise — and the linker rejects an override with no base entry.
        /// </summary>
        [Fact]
        public void APropertyCanImplementAnInterfaceProperty()
        {
            var runtime = Run(
                "interface INamed { name: string { get; } }\n"
                    + "class C : INamed { public name: string { get { return \"x\"; } } }\n"
                    + "fun run(): string { let n: INamed = C(); return n.name; }");

            Assert.Equal("x", Text(runtime, "run"));
        }

        /// <summary>An interface property's setter has to reach the contract, or no call site can assign through it.</summary>
        [Fact]
        public void AnInterfacePropertyKeepsItsSetter()
        {
            var runtime = Run(
                "interface ICounted { count: int { get; set; } }\n"
                    + "class C : ICounted { public count: int { get; set; } }\n"
                    + "fun run(): int { let c: ICounted = C(); c.count = 7; return c.count; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void APropertyOverrideStillReachesTheBase()
        {
            var runtime = Run(
                "class Base { public virtual n: int { get { return 1; } } }\n"
                    + "class Derived : Base { public override n: int { get { return 9; } } }\n"
                    + "fun run(): int { let b: Base = Derived(); return b.n; }");

            Assert.Equal(9, Int(runtime, "run"));
        }

        /// <summary>
        /// <c>SurtrTypeLinker</c> already refuses this at load time; this is the same rule run at
        /// compile time, before <c>surtrc build</c> could write an incomplete class to disk.
        /// </summary>
        [Fact]
        public void AClassMissingAnInterfaceMethodIsReported()
        {
            using var compilation = Reject(
                "interface IShape {\n"
                    + "  fun area(): float;\n"
                    + "}\n"
                    + "class Circle : IShape {\n"
                    + "}");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.MissingImplementation);
        }

        [Fact]
        public void AClassMissingAnInheritedAbstractMethodIsReported()
        {
            using var compilation = Reject(
                "abstract class Shape {\n"
                    + "  public abstract fun area(): float;\n"
                    + "}\n"
                    + "class Circle : Shape {\n"
                    + "}");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.MissingImplementation);
        }

        /// <summary>A constructed generic interface's obligations are checked the same as any other.</summary>
        [Fact]
        public void AConstructedGenericInterfaceLeftUnimplementedIsReported()
        {
            using var compilation = Reject("class BadScore : IComparable<BadScore> {\n}");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.MissingImplementation);
        }

        /// <summary>
        /// Declaring the class itself <c>abstract</c> is the escape hatch — but the member still has
        /// to be redeclared <c>abstract</c> there, since only a <c>virtual</c>/<c>abstract</c>
        /// declaration creates a vtable slot at all; leaving it out entirely gives the interface
        /// dispatch table nothing to route through, abstract class or not.
        /// </summary>
        [Fact]
        public void AnAbstractClassMayRedeclareAnInterfaceMethodAbstractForItsSubclassToImplement()
        {
            var runtime = Run(
                "interface IShape {\n"
                    + "  fun area(): float;\n"
                    + "}\n"
                    + "abstract class Shape : IShape {\n"
                    + "  public abstract fun area(): float;\n"
                    + "}\n"
                    + "class Circle : Shape {\n"
                    + "  public override fun area(): float { return 3.0; }\n"
                    + "}\n"
                    + "fun run(): float { let s: IShape = Circle(); return s.area(); }");

            Assert.Equal(3.0, Call(runtime, "run").AsFloat);
        }

        /// <summary>
        /// Regression: an abstract property's bodyless accessors were mistaken for the
        /// auto-generated form, so the emitter synthesised a backing field for a member that must
        /// not implement anything. An abstract property is signature-only — no storage, no
        /// accessor bodies (§3.3, §3.4).
        /// </summary>
        [Fact]
        public void AnAbstractPropertyGetsNoBackingField()
        {
            var emitter = Build(
                "abstract class Shape {\n"
                    + "  public abstract name: string { get; set; }\n"
                    + "}\n");

            var shape = Assert.Single(emitter.Modules).FindClass("Shape");
            Assert.False(shape!.TryGetField("$backing$name", out _));
        }

        /// <summary>
        /// The abstract property's accessors are declared signature-only, and a concrete subclass
        /// satisfies them with real bodies — the same bargain as an abstract method (§3.3, §3.4).
        /// </summary>
        [Fact]
        public void AnAbstractPropertyIsSatisfiedByAConcreteSubclass()
        {
            var runtime = Run(
                "abstract class Shape {\n"
                    + "  public abstract name: string { get; set; }\n"
                    + "}\n"
                    + "class Square : Shape {\n"
                    + "  public override name: string { get => \"sq\"; set { } }\n"
                    + "}\n"
                    + "fun run(): string { let s: Shape = Square(); return s.name; }");

            Assert.Equal("sq", Text(runtime, "run"));
        }

        /// <summary>
        /// An abstract class implementing an interface but never even redeclaring the member
        /// abstract leaves no vtable slot at all — a load-time crash with no diagnostic before this
        /// fix, since the compiler treated "abstract" as a blanket exemption.
        /// </summary>
        [Fact]
        public void AnAbstractClassStillHasToNameAnInterfaceMethodItLeavesUnimplemented()
        {
            using var compilation = Reject(
                "interface IShape {\n"
                    + "  fun area(): float;\n"
                    + "}\n"
                    + "abstract class Shape : IShape {\n"
                    + "}");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.MissingImplementation);
        }
        #endregion

        #region Exhaustive switch expressions (§4.3)
        /// <summary>
        /// The form exhaustiveness checking exists to allow: every case listed, so no <c>else</c> is
        /// needed and the last arm is what is left over.
        /// </summary>
        [Fact]
        public void AnExhaustiveSwitchOverAnEnumNeedsNoElse()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                    + "fun run(): int { let s = Suit.Spades; return switch (s) { Suit.Hearts -> 1, Suit.Spades -> 2, }; }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void AnExhaustiveSwitchStillPicksAnEarlierArm()
        {
            var runtime = Run(
                "enum Colour { Red, Green, Blue }\n"
                    + "fun run(): int {\n"
                    + "  let c = Colour.Green;\n"
                    + "  return switch (c) { Colour.Red -> 1, Colour.Green -> 2, Colour.Blue -> 3, };\n"
                    + "}");

            Assert.Equal(2, Int(runtime, "run"));
        }

        /// <summary>
        /// Anything without a fixed set of values still needs one — reported at binding, where it is
        /// a property of the program, rather than at emit as something not lowered.
        /// </summary>
        [Fact]
        public void ASwitchExpressionOverAnOpenTypeNeedsAnElse()
        {
            using var compilation = Reject("fun run(): int { return switch (2) { 1 -> 10, 2 -> 20, }; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.SwitchNotExhaustive);
        }

        /// <summary>A nullable enum can also be null, which no arm covers.</summary>
        [Fact]
        public void ASwitchExpressionOverANullableEnumNeedsAnElse()
        {
            using var compilation = Reject(
                "enum Suit { Hearts, Spades }\n"
                    + "fun run(): int { let s: Suit? = null; return switch (s) { Suit.Hearts -> 1, Suit.Spades -> 2, }; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.SwitchNotExhaustive);
        }
        #endregion

        #region Operator overloads (§5.6)
        /// <summary>
        /// A declared `operator==` has to win over the built-in fallback, which would otherwise
        /// treat two operands of the same class as "assignable to each other" (identity) and
        /// resolve before the overload is ever looked up.
        /// </summary>
        [Fact]
        public void AnEqualityOperatorIsInvokedOverIdentity()
        {
            var runtime = Run(
                "class Vec2 {\n"
                    + "  public let x: float;\n"
                    + "  public let y: float;\n"
                    + "  constructor(x: float, y: float) { this.x = x; this.y = y; }\n"
                    + "  operator==(a: Vec2, b: Vec2): bool { return a.x == b.x && a.y == b.y; }\n"
                    + "}\n"
                    + "fun run(): bool { let a = Vec2(1.0, 2.0); let b = Vec2(1.0, 2.0); return a == b; }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        /// <summary>§3.3: an operator may take an arrow body — the same sugar, the same lowering.</summary>
        [Fact]
        public void AnArrowBodiedOperatorComputesLikeABlockOne()
        {
            var runtime = Run(
                "class Vec2 {\n"
                    + "  public var x: float;\n"
                    + "  public var y: float;\n"
                    + "  constructor(x: float, y: float) => init(x, y);\n"
                    + "  private fun init(x: float, y: float): void { this.x = x; this.y = y; }\n"
                    + "  operator+(a: Vec2, b: Vec2): Vec2 => Vec2(a.x + b.x, a.y + b.y);\n"
                    + "}\n"
                    + "fun run(): bool { let s = Vec2(1.0, 2.0) + Vec2(3.0, 4.0); return s.x == 4.0 && s.y == 6.0; }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        /// <summary>`!=` reuses the same `operator==` lookup and negates its result.</summary>
        [Fact]
        public void InequalityNegatesTheDeclaredEqualityOperator()
        {
            var runtime = Run(
                "class Vec2 {\n"
                    + "  public let x: float;\n"
                    + "  public let y: float;\n"
                    + "  constructor(x: float, y: float) { this.x = x; this.y = y; }\n"
                    + "  operator==(a: Vec2, b: Vec2): bool { return a.x == b.x && a.y == b.y; }\n"
                    + "}\n"
                    + "fun run(): bool { let a = Vec2(1.0, 2.0); let b = Vec2(1.0, 2.0); return a != b; }");

            Assert.False(Call(runtime, "run").AsBool);
        }

        /// <summary>A class declaring no `operator==` still compares by reference identity.</summary>
        [Fact]
        public void EqualityWithoutAnOperatorStaysReferenceIdentity()
        {
            var runtime = Run(
                "class Plain {\n"
                    + "  public var value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "}\n"
                    + "fun run(): bool { let a = Plain(5); let b = Plain(5); return a == b; }");

            Assert.False(Call(runtime, "run").AsBool);
        }

        /// <summary>
        /// `<`, `<=`, `>` and `>=` are declared through `operator<=>` alone (§5.6) — a type never
        /// writes them separately — so the relational form has to reduce the three-way `int` result
        /// to a `bool` itself, and used to surface the raw `int` as the whole expression's type.
        /// </summary>
        [Fact]
        public void ARelationalOperatorReducesUserSpaceshipToABool()
        {
            var runtime = Run(
                "class Score {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "  operator<=>(a: Score, b: Score): int { return a.value - b.value; }\n"
                    + "}\n"
                    + "fun run(): bool { return Score(4) < Score(9); }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        [Fact]
        public void EveryRelationalFormReducesTheSameSpaceshipCorrectly()
        {
            var runtime = Run(
                "class Score {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "  operator<=>(a: Score, b: Score): int { return a.value - b.value; }\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "  let a = Score(4); let b = Score(9);\n"
                    + "  var n = 0;\n"
                    + "  if (a < b) { n = n + 1; }\n"
                    + "  if (a <= b) { n = n + 10; }\n"
                    + "  if (b > a) { n = n + 100; }\n"
                    + "  if (b >= a) { n = n + 1000; }\n"
                    + "  if (a > b) { n = n + 10000; }\n"
                    + "  return n;\n"
                    + "}");

            Assert.Equal(1111, Int(runtime, "run"));
        }

        /// <summary>
        /// A <c>virtual operator</c> is an instance method (§5.6), so the call goes through the
        /// receiver's vtable: an operand pair whose static type is the base still lands on the
        /// derived override.
        /// </summary>
        [Fact]
        public void AVirtualOperatorDispatchesThroughTheReceiver()
        {
            var runtime = Run(
                "class Shape {\n"
                    + "  virtual operator+(self: Shape, other: Shape): int { return 1; }\n"
                    + "}\n"
                    + "class Circle : Shape {\n"
                    + "  override operator+(self: Shape, other: Shape): int { return 2; }\n"
                    + "}\n"
                    + "fun run(): int { let a: Shape = Circle(); let b: Shape = Circle(); return a + b; }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        /// <summary>
        /// An operator declared on an interface is reached through the interface's method slots, so
        /// a call through an interface-typed receiver resolves to the implementing class's override.
        /// </summary>
        [Fact]
        public void AnInterfaceOperatorDispatchesThroughTheInterface()
        {
            var runtime = Run(
                "interface IAddable {\n"
                    + "  operator+(self: IAddable, other: IAddable): int;\n"
                    + "}\n"
                    + "class Vec2 : IAddable {\n"
                    + "  virtual operator+(self: IAddable, other: IAddable): int { return 7; }\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "  let a: IAddable = Vec2();\n"
                    + "  let b: IAddable = Vec2();\n"
                    + "  return a + b;\n"
                    + "}");

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>An operator is an instance method, so <c>this</c> is its receiver, not a boxed argument.</summary>
        [Fact]
        public void AVirtualOperatorBodyReadsItsReceiverThroughThis()
        {
            var runtime = Run(
                "class Counter {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "  virtual operator+(self: Counter, other: Counter): int { return this.value + other.value; }\n"
                    + "}\n"
                    + "fun run(): int { let a = Counter(3); let b = Counter(4); return a + b; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>An operator declared with the wrong arity is rejected where it is declared.</summary>
        [Theory]
        [InlineData("operator+(a: Plain): Plain { return a; }")]
        [InlineData("operator-(a: Plain, b: Plain, c: Plain): Plain { return a; }")]
        [InlineData("operator!(a: Plain, b: Plain): bool { return true; }")]
        [InlineData("operator++(a: Plain, b: Plain): Plain { return a; }")]
        public void AnOperatorWithTheWrongArityIsReported(string declaration)
        {
            using var compilation = Reject("class Plain { " + declaration + " }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidOperatorSignature);
        }

        [Fact]
        public void AnEqualityOperatorMustReturnBool()
        {
            using var compilation = Reject(
                "class Plain { operator==(a: Plain, b: Plain): int { return 0; } }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidOperatorSignature);
        }

        [Fact]
        public void ASpaceshipOperatorMustReturnInt()
        {
            using var compilation = Reject(
                "class Plain { operator<=>(a: Plain, b: Plain): bool { return true; } }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidOperatorSignature);
        }

        [Fact]
        public void AnIndexerWriteFormMustReturnVoid()
        {
            using var compilation = Reject(
                "class Plain {\n"
                    + "  operator[](p: Plain, i: int): int { return i; }\n"
                    + "  operator[](p: Plain, i: int, v: int): int { return v; }\n"
                    + "}");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidOperatorSignature);
        }

        [Fact]
        public void AnIndexerWithTheWrongArityIsReported()
        {
            using var compilation = Reject("class Plain { operator[](p: Plain, i: int, j: int, v: int): void { } }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidOperatorSignature);
        }
        #endregion

        #region Indexers (§5.6)
        /// <summary>
        /// An overload is always static, so the read form takes the receiver and the index — the
        /// same shape every other binary overload has.
        /// </summary>
        [Fact]
        public void AnIndexerReadsThroughItsOperator()
        {
            var runtime = Run(
                "class Bag {\n"
                    + "  private var _items: int[] = [10, 20, 30];\n"
                    + "  operator[](b: Bag, i: int): int { return b._items.get(i); }\n"
                    + "}\n"
                    + "fun run(): int { let b = Bag(); return b[1]; }");

            Assert.Equal(20, Int(runtime, "run"));
        }

        [Fact]
        public void AnIndexerWritesThroughItsOperator()
        {
            var runtime = Run(
                "class Bag {\n"
                    + "  private var _items: int[] = [10, 20, 30];\n"
                    + "  operator[](b: Bag, i: int): int { return b._items.get(i); }\n"
                    + "  operator[](b: Bag, i: int, v: int): void { b._items.set(i, v); }\n"
                    + "}\n"
                    + "fun run(): int { let b = Bag(); b[1] = 99; return b[1]; }");

            Assert.Equal(99, Int(runtime, "run"));
        }

        /// <summary>§5.6 puts no restriction on the index's type; only on how many there are.</summary>
        [Fact]
        public void AnIndexerMayTakeAnyKeyType()
        {
            var runtime = Run(
                "class Table {\n"
                    + "  private var _d: {string: string} = {};\n"
                    + "  operator[](t: Table, k: string): string { return t._d.get(k); }\n"
                    + "  operator[](t: Table, k: string, v: string): void { t._d.set(k, v); }\n"
                    + "}\n"
                    + "fun run(): string { let t = Table(); t[\"x\"] = \"y\"; return t[\"x\"]; }");

            Assert.Equal("y", Text(runtime, "run"));
        }

        [Fact]
        public void IndexingATypeThatDeclaresNoOperatorIsReported()
        {
            using var compilation = Reject("class Plain { }\nfun run(): int { let p = Plain(); return p[0]; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.NotSupportedOnType);
        }
        #endregion

        #region Attributes (§11)
        /// <summary>
        /// Through the image, because that is the form an attribute has to survive in: §11's audience
        /// is host reflection, which reads a module someone compiled earlier.
        /// </summary>
        private SurtrModule Reload(string source)
        {
            var image = SurtrModuleImage.FromBytes(Build(source).EmitImages()[0].ToBytes());
            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            var module = image.Instantiate();
            runtime.LoadModule(module);
            return module;
        }

        private static string Describe(SurtrMemberInfo member)
        {
            var parts = new List<string>();

            foreach (var attribute in member.Attributes)
            {
                var arguments = new List<string>();
                foreach (var argument in attribute.Arguments)
                {
                    arguments.Add(argument.Kind switch
                    {
                        SurtrConstantKind.Integer => argument.Value.AsInt.ToString(),
                        SurtrConstantKind.Float => argument.Value.AsFloat.ToString(CultureInfo.InvariantCulture),
                        SurtrConstantKind.Boolean => argument.Value.AsBool.ToString().ToLowerInvariant(),
                        SurtrConstantKind.Character => argument.Value.AsChar.ToString(),
                        SurtrConstantKind.String => argument.Text ?? "null",
                        _ => "null",
                    });
                }

                string name = attribute.AttributeType.Reference.ToDisplayString();
                parts.Add(name.Substring(name.IndexOf(':') + 1) + "(" + string.Join(", ", arguments) + ")");
            }

            return string.Join(", ", parts);
        }

        [Fact]
        public void AnAttributeOnAMethodSurvivesTheImage()
        {
            var module = Reload(
                "class Marker : Attribute { public let n: int = 0; }\n"
                    + "class Target {\n"
                    + "  @Marker(3)\n"
                    + "  public fun thing(): int { return 1; }\n"
                    + "}");

            Assert.True(module.FindClass("Target")!.TryGetMethods("thing", out var overloads));
            Assert.Equal("Marker(3)", Describe(overloads[0]));
        }

        [Fact]
        public void AnAttributeOnAClassSurvivesTheImage()
        {
            var module = Reload("class Marker : Attribute { public let n: int = 0; }\n@Marker(7)\nclass Target { }");

            Assert.Equal("Marker(7)", Describe(module.FindClass("Target")!));
        }

        [Fact]
        public void AnAttributeOnAFieldSurvivesTheImage()
        {
            var module = Reload(
                "class SerializeField : Attribute { }\n"
                    + "class Component {\n"
                    + "  @SerializeField\n"
                    + "  public var speed: float = 5.0;\n"
                    + "}");

            Assert.True(module.FindClass("Component")!.TryGetField("speed", out var field));
            Assert.Equal("SerializeField()", Describe(field));
        }

        /// <summary>§11's own example, arguments and all.</summary>
        [Fact]
        public void AnAttributeOnAPropertyKeepsItsArguments()
        {
            var module = Reload(
                "class Range : Attribute { public let lo: int = 0; public let hi: int = 0; }\n"
                    + "class Player {\n"
                    + "  @Range(0, 100)\n"
                    + "  public health: int { get; set; }\n"
                    + "}");

            Assert.True(module.FindClass("Player")!.TryGetProperty("health", out var property));
            Assert.Equal("Range(0, 100)", Describe(property));
        }

        [Fact]
        public void ADeclarationMayCarrySeveralAttributes()
        {
            var module = Reload(
                "class A : Attribute { }\nclass B : Attribute { }\n"
                    + "class Target {\n"
                    + "  @A\n"
                    + "  @B\n"
                    + "  public fun thing(): int { return 1; }\n"
                    + "}");

            Assert.True(module.FindClass("Target")!.TryGetMethods("thing", out var overloads));
            Assert.Equal("A(), B()", Describe(overloads[0]));
        }

        /// <summary>An argument is a constant, and §7.1 is where a named one comes from.</summary>
        [Fact]
        public void AnAttributeArgumentMayBeAConst()
        {
            var module = Reload(
                "const Limit: int = 42;\nclass Marker : Attribute { public let n: int = 0; }\n@Marker(Limit)\nclass Target { }");

            Assert.Equal("Marker(42)", Describe(module.FindClass("Target")!));
        }

        /// <summary>An attribute argument may be an enum constant — the case folds to its value (§2.3quater).</summary>
        [Fact]
        public void AnAttributeArgumentMayBeAnEnumConstant()
        {
            var module = Reload(
                "enum Level { Low, High }\nclass Marker : Attribute { public let level: Level; }\n@Marker(Level.High)\nclass Target { }");

            Assert.Equal("Marker(1)", Describe(module.FindClass("Target")!));
        }

        [Fact]
        public void SomethingThatIsNotAnAttributeIsReported()
        {
            using var compilation = Reject("class Plain { }\n@Plain\nclass Target { }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidAttribute);
        }

        /// <summary>
        /// An attribute instance is built when its module loads, before anything runs — so an
        /// argument that is not a constant has nothing to be.
        /// </summary>
        [Fact]
        public void AnAttributeArgumentThatIsNotConstantIsReported()
        {
            using var compilation = Reject(
                "class Marker : Attribute { public let n: int = 0; }\n"
                    + "fun compute(): int { return 1; }\n"
                    + "@Marker(compute())\n"
                    + "class Target { }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.NotAConstant);
        }

        /// <summary>
        /// <c>attribute class</c> implies extending <c>Attribute</c> - no <c>: Attribute</c> needed -
        /// and still survives the image like any other attribute.
        /// </summary>
        [Fact]
        public void AnAttributeKeywordClassNeedsNoExplicitBaseAndSurvivesTheImage()
        {
            var module = Reload(
                "attribute class Marker { }\n"
                    + "class Target {\n"
                    + "  @Marker\n"
                    + "  public fun thing(): int { return 1; }\n"
                    + "}");

            Assert.True(module.FindClass("Target")!.TryGetMethods("thing", out var overloads));
            Assert.Equal("Marker()", Describe(overloads[0]));
        }

        [Fact]
        public void AnAttributeRestrictedToMethodsMayBeWrittenOnAMethod()
        {
            var module = Reload(
                "attribute(Method) class OnlyMethods { }\n"
                    + "class Target {\n"
                    + "  @OnlyMethods\n"
                    + "  public fun thing(): int { return 1; }\n"
                    + "}");

            Assert.True(module.FindClass("Target")!.TryGetMethods("thing", out var overloads));
            Assert.Equal("OnlyMethods()", Describe(overloads[0]));
        }

        [Fact]
        public void AnAttributeRestrictedToMethodsIsRejectedOnAField()
        {
            using var compilation = Reject(
                "attribute(Method) class OnlyMethods { }\n"
                    + "class Target {\n"
                    + "  @OnlyMethods\n"
                    + "  public var speed: float = 1.0;\n"
                    + "}");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.AttributeTargetMismatch);
        }

        [Fact]
        public void AnAttributeWithNoTargetListIsUnrestricted()
        {
            var module = Reload(
                "attribute class Anywhere { }\n"
                    + "@Anywhere\n"
                    + "class Target {\n"
                    + "  @Anywhere\n"
                    + "  public var speed: float = 1.0;\n"
                    + "}");

            Assert.Equal("Anywhere()", Describe(module.FindClass("Target")!));
            Assert.True(module.FindClass("Target")!.TryGetField("speed", out var field));
            Assert.Equal("Anywhere()", Describe(field));
        }

        [Fact]
        public void AnAttributeKeywordClassExtendingSomethingThatIsNotAttributeIsRejected()
        {
            using var compilation = Reject("class Plain { }\nattribute class Marker : Plain { }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidAttribute);
        }

        /// <summary>
        /// <c>CompileTimeOnly</c> retention (§11): checked and folded like any other attribute use,
        /// but never reaches the compiled image - the opposite of the default <c>Runtime</c> case,
        /// which does.
        /// </summary>
        [Fact]
        public void ACompileTimeOnlyAttributeIsCheckedButNeverEmitted()
        {
            var module = Reload(
                "attribute(CompileTimeOnly) class Todo { }\n"
                    + "class Target {\n"
                    + "  @Todo\n"
                    + "  public fun thing(): int { return 1; }\n"
                    + "}");

            Assert.True(module.FindClass("Target")!.TryGetMethods("thing", out var overloads));
            Assert.Equal(string.Empty, Describe(overloads[0]));
        }

        [Fact]
        public void ACompileTimeOnlyAttributeStillReportsANonConstantArgument()
        {
            using var compilation = Reject(
                "attribute(CompileTimeOnly) class Todo { public let n: int = 0; }\n"
                    + "fun compute(): int { return 1; }\n"
                    + "class Target {\n"
                    + "  @Todo(compute())\n"
                    + "  public fun thing(): int { return 1; }\n"
                    + "}");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.NotAConstant);
        }
        #endregion

        #region Atributos built-in: @Value y @Range punta a punta

        [Fact]
        public void AValueClassAnswersEqualsWithoutDeclaringIt()
        {
            var runtime = Run(
                "@Value\n"
                    + "class Vec2 {\n"
                    + "  public let x: float;\n"
                    + "  public let y: float;\n"
                    + "  constructor(x: float, y: float) { this.x = x; this.y = y; }\n"
                    + "}\n"
                    + "fun equal(): int { return Vec2(1.0, 2.0).equals(Vec2(1.0, 2.0)) ? 1 : 0; }\n"
                    + "fun different(): int { return Vec2(1.0, 2.0).equals(Vec2(1.0, 3.0)) ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "equal"));
            Assert.Equal(0, Int(runtime, "different"));
        }

        /// <summary>
        /// The value members are real methods, not call-site lowering: they exist in the image
        /// under the same real names §11.1 gives them, and a host can invoke them by reflection.
        /// </summary>
        [Fact]
        public void AValueClassEmitsRealValueMembers()
        {
            var emitter = Build(
                "@Value\n"
                    + "class Vec2 {\n"
                    + "  public let x: float;\n"
                    + "  public let y: float;\n"
                    + "  constructor(x: float, y: float) { this.x = x; this.y = y; }\n"
                    + "}");

            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes());
            using var runtime = new SurtrRuntime();
            var module = reloaded.Instantiate();
            runtime.LoadModule(module);

            var vec = module.FindClass("Vec2")!;
            Assert.True(vec.TryGetMethods("equals", out var equals) && equals.Length == 1);
            Assert.True(vec.TryGetMethods("hashCode", out var hashCode) && hashCode.Length == 1);
            Assert.True(vec.TryGetMethods("toString", out var display) && display.Length == 1);

            var a = runtime.NewInstance(vec);
            var b = runtime.NewInstance(vec);
            var aRef = SurtrValue.CreateReference(a.GetSurtrReference());
            var bRef = SurtrValue.CreateReference(b.GetSurtrReference());

            // Two freshly allocated, zeroed instances are structurally equal.
            Assert.True(runtime.Invoke(equals[0], aRef, bRef).AsBool);

            // The hash is a real integer, and equal values share it.
            var hashA = runtime.Invoke(hashCode[0], aRef).AsInt;
            var hashB = runtime.Invoke(hashCode[0], bRef).AsInt;
            Assert.Equal(hashA, hashB);

            // The display names the class and reaches the field labels.
            var text = runtime.Resolve<SurtrString>(runtime.Invoke(display[0], aRef))!.Text;
            Assert.StartsWith("Vec2(", text);
            Assert.Contains("x=", text);
        }

        [Fact]
        public void ADeclaredEqualsWinsOverTheSynthesis()
        {
            var runtime = Run(
                "@Value\n"
                    + "class Picky {\n"
                    + "  public let n: int;\n"
                    + "  constructor(n: int) { this.n = n; }\n"
                    + "  public fun equals(other: Picky): bool { return false; }\n"
                    + "}\n"
                    + "fun run(): int { return Picky(1).equals(Picky(1)) ? 1 : 0; }");

            Assert.Equal(0, Int(runtime, "run"));
        }

        /// <summary>
        /// <c>@Value</c> turns <c>==</c> into a field-by-field comparison, so two distinct
        /// instances of the same shape answer as equal - the whole point of the opt-in.
        /// </summary>
        [Fact]
        public void ValueMarkedClassesCompareStructurally()
        {
            var runtime = Run(
                "@Value\n"
                    + "class Vec2 {\n"
                    + "  public let x: float;\n"
                    + "  public let y: float;\n"
                    + "  constructor(x: float, y: float) { this.x = x; this.y = y; }\n"
                    + "}\n"
                    + "fun equal(): int { return Vec2(1.0, 2.0) == Vec2(1.0, 2.0) ? 1 : 0; }\n"
                    + "fun different(): int { return Vec2(1.0, 2.0) == Vec2(1.0, 3.0) ? 1 : 0; }\n"
                    + "fun notEqual(): int { return Vec2(1.0, 2.0) != Vec2(1.0, 3.0) ? 1 : 0; }\n"
                    + "fun sameInstance(): int { var v = Vec2(9.0, 9.0); return v == v ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "equal"));
            Assert.Equal(0, Int(runtime, "different"));
            Assert.Equal(1, Int(runtime, "notEqual"));
            Assert.Equal(1, Int(runtime, "sameInstance"));
        }

        [Fact]
        public void ValueEqualityCoversInheritedFieldsAndNullSafely()
        {
            var runtime = Run(
                "@Value\n"
                    + "class Base {\n"
                    + "  public let tag: string;\n"
                    + "  constructor(tag: string) { this.tag = tag; }\n"
                    + "}\n"
                    + "@Value\n"
                    + "class Item : Base {\n"
                    + "  public let n: int;\n"
                    + "  constructor(tag: string, n: int) : super(tag) { this.n = n; }\n"
                    + "}\n"
                    + "fun inheritedMatters(): int { return Item(\"a\", 1) == Item(\"b\", 1) ? 1 : 0; }\n"
                    + "fun inheritedCounts(): int { return Item(\"a\", 1) == Item(\"a\", 1) ? 1 : 0; }\n"
                    + "fun againstNull(): int { return Item(\"a\", 1) == null ? 1 : 0; }");

            Assert.Equal(0, Int(runtime, "inheritedMatters"));
            Assert.Equal(1, Int(runtime, "inheritedCounts"));
            Assert.Equal(0, Int(runtime, "againstNull"));
        }

        /// <summary>A declared operator== outranks the mark, exactly as §11.1 orders the rules.</summary>
        [Fact]
        public void ADeclaredOperatorStillWinsOverTheValueMark()
        {
            var runtime = Run(
                "@Value\n"
                    + "class Picky {\n"
                    + "  public let n: int;\n"
                    + "  constructor(n: int) { this.n = n; }\n"
                    + "  operator==(a: Picky, b: Picky): bool { return false; }\n"
                    + "}\n"
                    + "fun run(): int { return Picky(1) == Picky(1) ? 1 : 0; }");

            Assert.Equal(0, Int(runtime, "run"));
        }

        [Fact]
        public void ANestedValueFieldComparesStructurallyToo()
        {
            var runtime = Run(
                "@Value\n"
                    + "class Inner {\n"
                    + "  public let n: int;\n"
                    + "  constructor(n: int) { this.n = n; }\n"
                    + "}\n"
                    + "@Value\n"
                    + "class Outer {\n"
                    + "  public let inner: Inner?;\n"
                    + "  constructor(inner: Inner?) { this.inner = inner; }\n"
                    + "}\n"
                    + "fun deep(): int { return Outer(Inner(4)) == Outer(Inner(4)) ? 1 : 0; }\n"
                    + "fun shallowBreaksIt(): int { return Outer(Inner(4)) == Outer(Inner(5)) ? 1 : 0; }\n"
                    + "fun nullFieldsAgree(): int { return Outer(null) == Outer(null) ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "deep"));
            Assert.Equal(0, Int(runtime, "shallowBreaksIt"));
            Assert.Equal(1, Int(runtime, "nullFieldsAgree"));
        }

        /// <summary>
        /// The mark is spent inside the compiler: like everything CompileTimeOnly, the use never
        /// reaches the image.
        /// </summary>
        [Fact]
        public void AValueUseIsNeverEmitted()
        {
            var module = Reload(
                "@Value\n"
                    + "class Vec {\n"
                    + "  public let x: float = 0.0;\n"
                    + "}");

            Assert.Equal(string.Empty, Describe(module.FindClass("Vec")!));
        }

        [Fact]
        public void ARangeUseSurvivesTheImageWithBothBounds()
        {
            var emitter = Build(
                "class Player {\n"
                    + "  @Range(0, 100)\n"
                    + "  public var health: float = 100.0;\n"
                    + "}");

            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes());
            using var runtime = new SurtrRuntime();
            var module = reloaded.Instantiate();
            runtime.LoadModule(module);

            Assert.True(module.FindClass("Player")!.TryGetField("health", out var field));
            Assert.True(field.TryGetAttribute(SurtrBuiltIns.RangeAttribute, out var usage));

            var instance = runtime.Resolve<SurtrInstance>(SurtrValue.CreateReference(usage.Instance))!;
            Assert.Equal(0.0, instance[0].AsFloat);
            Assert.Equal(100.0, instance[1].AsFloat);
        }

        [Fact]
        public void AnExportUseCarriesItsAliasThroughTheImage()
        {
            var emitter = Build(
                "@Export\n"
                    + "class Enemy {\n"
                    + "  @Export(\"hitPoints\")\n"
                    + "  public var health: float = 10.0;\n"
                    + "}");

            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes());
            using var runtime = new SurtrRuntime();
            var module = reloaded.Instantiate();
            runtime.LoadModule(module);

            var enemy = module.FindClass("Enemy")!;
            Assert.True(enemy.TryGetAttribute(SurtrBuiltIns.Export, out _), "The class mark should survive.");

            Assert.True(enemy.TryGetField("health", out var health));
            Assert.True(health.TryGetAttribute(SurtrBuiltIns.Export, out var alias));
            var instance = runtime.Resolve<SurtrInstance>(SurtrValue.CreateReference(alias.Instance))!;
            Assert.Equal("hitPoints", runtime.Resolve<SurtrString>(instance[0])!.Text);
        }

        #endregion

        #region Runner de @Test/@TestSuite

        /// <summary>
        /// The host-side runner discovers tests purely through reflection - the <c>@Test</c> mark
        /// on parameterless methods, the <c>@TestSuite</c> mark naming a group - and runs them,
        /// static ones directly and instance ones on a fresh instance whose parameterless
        /// constructor has run.
        /// </summary>
        [Fact]
        public void TestRunnerDiscoversAndRunsPassingTests()
        {
            var runtime = Run(
                "@TestSuite(\"Vec\")\n"
                    + "class VecTests {\n"
                    + "  @Test(\"one\")\n"
                    + "  public fun first(): void { }\n"
                    + "  @Test\n"
                    + "  public static fun second(): void { }\n"
                    + "  public fun notATest(): void { }\n"
                    + "}\n"
                    + "public fun unrelated(): void { }");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            var results = SurtrTestRunner.Run(runtime, module);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.True(r.Passed));
            Assert.Contains(results, r => r.Name == "one" && r.Suite == "Vec");
            Assert.Contains(results, r => r.Name == "second" && r.Suite == "Vec");
        }

        [Fact]
        public void TestRunnerReportsAThrowingTestAsFailed()
        {
            var runtime = Run(
                "@TestSuite\n"
                    + "class MathTests {\n"
                    + "  @Test(\"boom\")\n"
                    + "  public fun divides(): void { let x: int = 1 / 0; }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            var results = SurtrTestRunner.Run(runtime, module);

            var failed = Assert.Single(results);
            Assert.Equal("boom", failed.Name);
            Assert.False(failed.Passed);
            Assert.Equal(SurtrTestOutcome.Failed, failed.Outcome);
            Assert.False(string.IsNullOrEmpty(failed.Failure));
        }

        /// <summary>
        /// <c>@TestIgnore</c> is the complement of <c>@Test</c> (§P9): the runner still discovers
        /// the method and reports it, with the reason the mark carries, but never enters the body -
        /// which the effect counter is what proves.
        /// </summary>
        [Fact]
        public void TestRunnerReportsAnIgnoredTestAsSkippedWithoutRunningIt()
        {
            var runtime = Run(
                "var ran: int = 0;\n"
                    + "public fun effects(): int { return ran; }\n"
                    + "@TestSuite(\"Vec\")\n"
                    + "class VecTests {\n"
                    + "  @Test(\"kept\")\n"
                    + "  public fun kept(): void { ran = ran + 1; }\n"
                    + "  @Test(\"dropped\")\n"
                    + "  @TestIgnore(\"flaky on CI\")\n"
                    + "  public fun dropped(): void { ran = ran + 100; }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            var results = SurtrTestRunner.Run(runtime, module);

            Assert.Equal(2, results.Count);

            var kept = Assert.Single(results, r => r.Name == "kept");
            Assert.Equal(SurtrTestOutcome.Passed, kept.Outcome);

            var dropped = Assert.Single(results, r => r.Name == "dropped");
            Assert.Equal(SurtrTestOutcome.Skipped, dropped.Outcome);
            Assert.True(dropped.Skipped);
            Assert.False(dropped.Passed);
            Assert.Equal("flaky on CI", dropped.SkipReason);
            Assert.Null(dropped.Failure);

            Assert.Equal(1, Int(runtime, "effects"));
        }

        [Fact]
        public void AnIgnoredTestWithNoReasonStillSkips()
        {
            var runtime = Run(
                "class Tests {\n"
                    + "  @Test\n"
                    + "  @TestIgnore\n"
                    + "  public fun boom(): void { let x: int = 1 / 0; }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            var skipped = Assert.Single(SurtrTestRunner.Run(runtime, module));

            Assert.Equal(SurtrTestOutcome.Skipped, skipped.Outcome);
            Assert.Null(skipped.SkipReason);
        }

        [Fact]
        public void ATestIgnoreUseCarriesItsReasonThroughTheImage()
        {
            var emitter = Build(
                "class Tests {\n"
                    + "  @Test\n"
                    + "  @TestIgnore(\"waiting on #42\")\n"
                    + "  public fun pending(): void { }\n"
                    + "}");

            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes());
            using var runtime = new SurtrRuntime();
            var module = reloaded.Instantiate();
            runtime.LoadModule(module);

            Assert.True(module.FindClass("Tests")!.TryGetMethods("pending", out var overloads));
            Assert.True(overloads[0].TryGetAttribute(SurtrBuiltIns.TestIgnore, out var usage));

            var instance = runtime.Resolve<SurtrInstance>(SurtrValue.CreateReference(usage.Instance))!;
            Assert.Equal("waiting on #42", runtime.Resolve<SurtrString>(instance[0])!.Text);
        }

        #endregion

        #region Fixtures de @TestBefore/@TestAfter (§P10)

        /// <summary>
        /// Per-test, not per-suite (§P10): two tests in one class mean the fixtures run twice
        /// each, which the counters are what separate from a once-around-the-group reading.
        /// </summary>
        [Fact]
        public void FixturesRunAroundEachTestRatherThanOncePerSuite()
        {
            var runtime = Run(
                "var beforeRuns: int = 0;\n"
                    + "var afterRuns: int = 0;\n"
                    + "public fun readBefores(): int { return beforeRuns; }\n"
                    + "public fun readAfters(): int { return afterRuns; }\n"
                    + "class Tests {\n"
                    + "  @TestBefore\n"
                    + "  public static fun setUp(): void { beforeRuns = beforeRuns + 1; }\n"
                    + "  @TestAfter\n"
                    + "  public static fun tearDown(): void { afterRuns = afterRuns + 1; }\n"
                    + "  @Test(\"a\")\n"
                    + "  public static fun a(): void { }\n"
                    + "  @Test(\"b\")\n"
                    + "  public static fun b(): void { }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            var results = SurtrTestRunner.Run(runtime, module);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.True(r.Passed));
            Assert.Equal(2, Int(runtime, "readBefores"));
            Assert.Equal(2, Int(runtime, "readAfters"));
        }

        /// <summary>
        /// The guarantee that makes acquiring something in a <c>@TestBefore</c> safe: the body
        /// never runs, the test is reported failed, and the <c>@TestAfter</c> runs anyway.
        /// </summary>
        [Fact]
        public void AThrowingBeforeFailsTheTestAndStillRunsTheAfter()
        {
            var runtime = Run(
                "var afterRuns: int = 0;\n"
                    + "var bodyRuns: int = 0;\n"
                    + "public fun readAfters(): int { return afterRuns; }\n"
                    + "public fun readBody(): int { return bodyRuns; }\n"
                    + "class Tests {\n"
                    + "  @TestBefore\n"
                    + "  public static fun setUp(): void { let x: int = 1 / 0; }\n"
                    + "  @TestAfter\n"
                    + "  public static fun tearDown(): void { afterRuns = afterRuns + 1; }\n"
                    + "  @Test(\"t\")\n"
                    + "  public static fun t(): void { bodyRuns = bodyRuns + 1; }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            var failed = Assert.Single(SurtrTestRunner.Run(runtime, module));

            Assert.Equal(SurtrTestOutcome.Failed, failed.Outcome);
            Assert.False(string.IsNullOrEmpty(failed.Failure));
            Assert.Equal(0, Int(runtime, "readBody"));
            Assert.Equal(1, Int(runtime, "readAfters"));
        }

        [Fact]
        public void AFixtureWrapsItsOwnClassesTestsAndNoOthers()
        {
            var runtime = Run(
                "var alphaSetUps: int = 0;\n"
                    + "public fun readAlpha(): int { return alphaSetUps; }\n"
                    + "class Alpha {\n"
                    + "  @TestBefore\n"
                    + "  public static fun setUp(): void { alphaSetUps = alphaSetUps + 1; }\n"
                    + "  @Test(\"alpha\")\n"
                    + "  public static fun alpha(): void { }\n"
                    + "}\n"
                    + "class Beta {\n"
                    + "  @Test(\"beta\")\n"
                    + "  public static fun beta(): void { }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            var results = SurtrTestRunner.Run(runtime, module);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.True(r.Passed));
            Assert.Equal(1, Int(runtime, "readAlpha"));
        }

        /// <summary>
        /// Module scope means every test in the module, the ones inside its classes included — and
        /// a loose <c>@Test fun</c> is discovered as an ordinary test, under the module's path for
        /// a suite since there is no <c>@TestSuite</c> to name it (§2.5).
        /// </summary>
        [Fact]
        public void AModuleLevelFixtureWrapsBothLooseAndClassTests()
        {
            var runtime = Run(
                "var setUps: int = 0;\n"
                    + "var looseRuns: int = 0;\n"
                    + "public fun readSetUps(): int { return setUps; }\n"
                    + "public fun readLoose(): int { return looseRuns; }\n"
                    + "@TestBefore\n"
                    + "public fun setUp(): void { setUps = setUps + 1; }\n"
                    + "@Test(\"loose\")\n"
                    + "public fun loose(): void { looseRuns = looseRuns + 1; }\n"
                    + "class Tests {\n"
                    + "  @Test(\"inClass\")\n"
                    + "  public static fun inClass(): void { }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            var results = SurtrTestRunner.Run(runtime, module);

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.True(r.Passed));

            var loose = Assert.Single(results, r => r.Name == "loose");
            Assert.Equal("game.core.Test", loose.Suite);
            Assert.Contains(results, r => r.Name == "inClass" && r.Suite == "Tests");

            Assert.Equal(2, Int(runtime, "readSetUps"));
            Assert.Equal(1, Int(runtime, "readLoose"));
        }

        [Fact]
        public void AnInstanceFixtureAndItsTestShareOneInstance()
        {
            var runtime = Run(
                "var observed: int = 0;\n"
                    + "public fun readObserved(): int { return observed; }\n"
                    + "class Tests {\n"
                    + "  public var n: int = 0;\n"
                    + "  @TestBefore\n"
                    + "  public fun setUp(): void { n = 41; }\n"
                    + "  @Test(\"shares\")\n"
                    + "  public fun shares(): void { observed = n; }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            Assert.True(Assert.Single(SurtrTestRunner.Run(runtime, module)).Passed);
            Assert.Equal(41, Int(runtime, "readObserved"));
        }

        /// <summary>
        /// A static test beside an instance fixture still gets an instance built for it — the
        /// fixture has to have something to run on, and the test not needing one does not answer
        /// that question.
        /// </summary>
        [Fact]
        public void AStaticTestStillGetsAnInstanceWhenItsFixtureIsOne()
        {
            var runtime = Run(
                "var touched: int = 0;\n"
                    + "public fun readTouched(): int { return touched; }\n"
                    + "class Tests {\n"
                    + "  @TestBefore\n"
                    + "  public fun setUp(): void { touched = touched + 1; }\n"
                    + "  @Test(\"staticOne\")\n"
                    + "  public static fun staticOne(): void { }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            Assert.True(Assert.Single(SurtrTestRunner.Run(runtime, module)).Passed);
            Assert.Equal(1, Int(runtime, "readTouched"));
        }

        [Fact]
        public void AnIgnoredTestRunsNoFixturesEither()
        {
            var runtime = Run(
                "var setUps: int = 0;\n"
                    + "public fun readSetUps(): int { return setUps; }\n"
                    + "class Tests {\n"
                    + "  @TestBefore\n"
                    + "  public static fun setUp(): void { setUps = setUps + 1; }\n"
                    + "  @Test\n"
                    + "  @TestIgnore(\"pending\")\n"
                    + "  public static fun dropped(): void { }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            Assert.True(Assert.Single(SurtrTestRunner.Run(runtime, module)).Skipped);
            Assert.Equal(0, Int(runtime, "readSetUps"));
        }

        #endregion

        #region Runner de @Benchmark (§P11)

        /// <summary>
        /// A benchmark is discovered like a test and run unlike one (§P11): repeatedly, warmup
        /// first, and timed. The effect counter is what proves the repetition — one call would be
        /// a test.
        /// </summary>
        [Fact]
        public void BenchmarkRunnerWarmsUpThenTimesEachDiscoveredMethod()
        {
            var runtime = Run(
                "var calls: int = 0;\n"
                    + "public fun readCalls(): int { return calls; }\n"
                    + "@TestSuite(\"Math\")\n"
                    + "class MathBenchmarks {\n"
                    + "  @Benchmark\n"
                    + "  public static fun addition(): void { calls = calls + 1; }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            var measured = Assert.Single(SurtrTestRunner.RunBenchmarks(runtime, warmup: 2, iterations: 5, module));

            Assert.Equal("addition", measured.Name);
            Assert.Equal("Math", measured.Suite);
            Assert.Equal(5, measured.Iterations);
            Assert.True(measured.Measured);
            Assert.Null(measured.Failure);
            Assert.True(measured.TotalMilliseconds >= 0.0);
            Assert.True(measured.MinimumMilliseconds <= measured.MedianMilliseconds);
            Assert.Equal(measured.MedianMilliseconds * 1_000_000.0, measured.NanosecondsPerOperation);

            Assert.Equal(7, Int(runtime, "readCalls"));
        }

        [Fact]
        public void TheTestPassAndTheBenchmarkPassDiscoverDifferentMethods()
        {
            var runtime = Run(
                "class Suite {\n"
                    + "  @Test(\"t\")\n"
                    + "  public static fun t(): void { }\n"
                    + "  @Benchmark\n"
                    + "  public static fun b(): void { }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));

            var tests = SurtrTestRunner.Run(runtime, module);
            Assert.Equal("t", Assert.Single(tests).Name);

            var benchmarks = SurtrTestRunner.RunBenchmarks(runtime, warmup: 0, iterations: 1, module);
            Assert.Equal("b", Assert.Single(benchmarks).Name);
        }

        [Fact]
        public void AnInstanceBenchmarkBuildsItsReceiverOnceBeforeTheWarmup()
        {
            var runtime = Run(
                "var constructions: int = 0;\n"
                    + "public fun readConstructions(): int { return constructions; }\n"
                    + "class Benchmarks {\n"
                    + "  public constructor() { constructions = constructions + 1; }\n"
                    + "  @Benchmark\n"
                    + "  public fun work(): void { }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            var measured = Assert.Single(SurtrTestRunner.RunBenchmarks(runtime, warmup: 3, iterations: 4, module));

            Assert.True(measured.Measured);
            Assert.Equal(1, Int(runtime, "readConstructions"));
        }

        [Fact]
        public void AThrowingBenchmarkReportsItsFailureRatherThanANumber()
        {
            var runtime = Run(
                "class Benchmarks {\n"
                    + "  @Benchmark\n"
                    + "  public static fun boom(): void { let x: int = 1 / 0; }\n"
                    + "}");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            var measured = Assert.Single(SurtrTestRunner.RunBenchmarks(runtime, warmup: 0, iterations: 1, module));

            Assert.False(measured.Measured);
            Assert.False(string.IsNullOrEmpty(measured.Failure));
            Assert.Equal(0.0, measured.MedianMilliseconds);
        }

        [Fact]
        public void ABenchmarkPassNeedsAtLeastOneTimedCall()
        {
            var runtime = Run("class Empty { }");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => SurtrTestRunner.RunBenchmarks(runtime, warmup: 0, iterations: 0, module));
        }

        [Fact]
        public void ABenchmarkUseSurvivesTheImage()
        {
            var emitter = Build(
                "class Benchmarks {\n"
                    + "  @Benchmark\n"
                    + "  public fun work(): void { }\n"
                    + "}");

            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes());
            using var runtime = new SurtrRuntime();
            var module = reloaded.Instantiate();
            runtime.LoadModule(module);

            Assert.True(module.FindClass("Benchmarks")!.TryGetMethods("work", out var overloads));
            Assert.True(overloads[0].TryGetAttribute(SurtrBuiltIns.Benchmark, out _));
        }

        #endregion

        #region @Throws en imagen (§P12)

        /// <summary>
        /// <c>@Throws</c> is the one built-in written more than once on a declaration (§P12), so
        /// what the image has to carry is a list rather than a value: both uses materialize, each
        /// with its own <c>name</c>, in the order they were written.
        /// </summary>
        [Fact]
        public void BothThrowsUsesSurviveTheImageAsSeparateInstances()
        {
            var emitter = Build(
                "class Parser {\n"
                    + "  @Throws(\"ArgumentException\")\n"
                    + "  @Throws(\"FormatException\")\n"
                    + "  public fun parse(text: string): int { return 0; }\n"
                    + "}");

            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes());
            using var runtime = new SurtrRuntime();
            var module = reloaded.Instantiate();
            runtime.LoadModule(module);

            Assert.True(module.FindClass("Parser")!.TryGetMethods("parse", out var overloads));

            var named = new List<string>();
            foreach (var usage in overloads[0].Attributes)
            {
                if (!ReferenceEquals(usage.AttributeType.ResolvedClass, SurtrBuiltIns.Throws))
                    continue;

                var instance = runtime.Resolve<SurtrInstance>(SurtrValue.CreateReference(usage.Instance))!;
                named.Add(runtime.Resolve<SurtrString>(instance[0])!.Text);
            }

            Assert.Equal(new[] { "ArgumentException", "FormatException" }, named);
        }

        /// <summary>
        /// <c>@NoAlloc</c> reaches the image like every mark but <c>@Value</c> (§P13): its meaning
        /// is a compile-time check, but a host profiling a build wants to know which members
        /// promised what.
        /// </summary>
        [Fact]
        public void ANoAllocMarkSurvivesTheImage()
        {
            var emitter = Build(
                "class Physics {\n"
                    + "  @NoAlloc\n"
                    + "  public fun step(dt: float): float { return dt * 2.0; }\n"
                    + "}");

            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes());
            using var runtime = new SurtrRuntime();
            var module = reloaded.Instantiate();
            runtime.LoadModule(module);

            Assert.True(module.FindClass("Physics")!.TryGetMethods("step", out var overloads));
            Assert.True(overloads[0].TryGetAttribute(SurtrBuiltIns.NoAlloc, out _));
        }

        #endregion

        #region @Flags: representacion entera (§P14)

        private const string Perms =
            "@Flags\n"
                + "enum Perm { Read, Write, Execute }\n";

        /// <summary>
        /// The mark changes the representation (§P14): a case is the integer <c>1 &lt;&lt; ordinal</c>
        /// rather than a static instance, which is what makes a combination a value of the type at
        /// all — two references ANDed together name nothing.
        /// </summary>
        [Fact]
        public void EachFlagsCaseIsTheBitAtItsDeclaredPosition()
        {
            var runtime = Run(
                Perms
                    + "public fun read(): int { return Perm.Read as int; }\n"
                    + "public fun write(): int { return Perm.Write as int; }\n"
                    + "public fun execute(): int { return Perm.Execute as int; }");

            Assert.Equal(1, Int(runtime, "read"));
            Assert.Equal(2, Int(runtime, "write"));
            Assert.Equal(4, Int(runtime, "execute"));
        }

        [Fact]
        public void TheBitwiseOperatorsCombineAndMaskAndProduceTheEnumItself()
        {
            var runtime = Run(
                Perms
                    + "public fun combined(): int { return (Perm.Read | Perm.Execute) as int; }\n"
                    + "public fun masked(): int { return ((Perm.Read | Perm.Execute) & Perm.Execute) as int; }\n"
                    + "public fun toggled(): int { return (Perm.Read ^ Perm.Read) as int; }\n"
                    // The result being the enum is what lets it land in a declared slot with no cast.
                    + "public fun assigned(): int { let rw: Perm = Perm.Read | Perm.Write; return rw as int; }");

            Assert.Equal(5, Int(runtime, "combined"));
            Assert.Equal(4, Int(runtime, "masked"));
            Assert.Equal(0, Int(runtime, "toggled"));
            Assert.Equal(3, Int(runtime, "assigned"));
        }

        [Fact]
        public void ComplementRemovesAFlag()
        {
            var runtime = Run(
                Perms
                    + "public fun removed(): int { return ((Perm.Read | Perm.Write) & ~Perm.Write) as int; }");

            Assert.Equal(1, Int(runtime, "removed"));
        }

        /// <summary>
        /// Compound assignment comes free: the binder expands <c>p |= f</c> to <c>p = p | f</c>
        /// before anything reads an operator, so the branch that resolves <c>|</c> is the only one
        /// there ever was.
        /// </summary>
        [Fact]
        public void CompoundAssignmentWorksThroughTheSameOperator()
        {
            var runtime = Run(
                Perms
                    + "public fun compound(): int { var p: Perm = Perm.Read; p |= Perm.Execute; return p as int; }\n"
                    + "public fun cleared(): int { var p: Perm = Perm.Read | Perm.Write; p &= ~Perm.Read; return p as int; }");

            Assert.Equal(5, Int(runtime, "compound"));
            Assert.Equal(2, Int(runtime, "cleared"));
        }

        [Fact]
        public void EqualityComparesValuesRatherThanReferences()
        {
            var runtime = Run(
                Perms
                    + "public fun holds(): int { let p: Perm = Perm.Read | Perm.Write; return (p & Perm.Write) == Perm.Write ? 1 : 0; }\n"
                    + "public fun lacks(): int { let p: Perm = Perm.Read | Perm.Write; return (p & Perm.Execute) == Perm.Execute ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "holds"));
            Assert.Equal(0, Int(runtime, "lacks"));
        }

        /// <summary>
        /// The cast is explicit in both directions and moves no bits — but it has to be written,
        /// because an arbitrary int is not a combination of the enum's cases. It is also what makes
        /// the empty set expressible, there being no case for zero.
        /// </summary>
        [Fact]
        public void TheCastToAndFromIntRoundTripsAndNamesTheEmptySet()
        {
            var runtime = Run(
                Perms
                    + "public fun roundTrip(): int { return (5 as Perm) as int; }\n"
                    + "public fun empty(): int { let none: Perm = 0 as Perm; return (Perm.Read & ~Perm.Read) == none ? 1 : 0; }");

            Assert.Equal(5, Int(runtime, "roundTrip"));
            Assert.Equal(1, Int(runtime, "empty"));
        }

        /// <summary>
        /// An ordinary enum is untouched: its cases stay instances, so combining them is still not
        /// a thing the language does.
        /// </summary>
        [Fact]
        public void AnUnmarkedEnumKeepsItsInstanceRepresentation()
        {
            var runtime = Run(
                "enum Color { Red, Green }\n"
                    + "public fun same(): int { return Color.Red == Color.Red ? 1 : 0; }\n"
                    + "public fun different(): int { return Color.Red == Color.Green ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "same"));
            Assert.Equal(0, Int(runtime, "different"));
        }

        /// <summary>
        /// The mark reaches the image, and the type it marks arrives as a value-class enum: the
        /// shared representation of every enum from the migration, with <c>IsValueType</c> set for
        /// the linker and the synthetic <c>value</c> field present.
        /// </summary>
        [Fact]
        public void AFlagsEnumTravelsAsAValueClassEnum()
        {
            var emitter = Build(Perms + "public fun unused(): int { return 0; }");

            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes());
            using var runtime = new SurtrRuntime();
            var module = reloaded.Instantiate();
            runtime.LoadModule(module);

            var perm = module.FindClass("Perm")!;
            Assert.True(perm.IsEnum, "A @Flags enum is an enum at runtime — a value class over the synthetic 'value' field.");
            Assert.True(perm.IsValueType, "An enum is a value class, so the linker flattens it.");
            Assert.True(perm.TryGetAttribute(SurtrBuiltIns.Flags, out _), "The mark itself still travels.");
            Assert.True(perm.TryGetField("Read", out _));
            Assert.True(perm.TryGetField("value", out _), "The synthetic 'value' field travels with the enum.");
        }

        /// <summary>
        /// <c>contains</c> is a lowering, not a member (§P14): the receiver is an int with no
        /// instance behind it, so what the call means is <c>(p &amp; f) == f</c> and that is what
        /// it binds to.
        /// </summary>
        [Fact]
        public void ContainsTestsOneFlagOfACombination()
        {
            var runtime = Run(
                Perms
                    + "public fun holds(): int { let p: Perm = Perm.Read | Perm.Write; return p.contains(Perm.Write) ? 1 : 0; }\n"
                    + "public fun lacks(): int { let p: Perm = Perm.Read | Perm.Write; return p.contains(Perm.Execute) ? 1 : 0; }\n"
                    + "public fun holdsBoth(): int { let p: Perm = Perm.Read | Perm.Write; return p.contains(Perm.Read | Perm.Write) ? 1 : 0; }\n"
                    + "public fun onACase(): int { return Perm.Read.contains(Perm.Read) ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "holds"));
            Assert.Equal(0, Int(runtime, "lacks"));
            Assert.Equal(1, Int(runtime, "holdsBoth"));
            Assert.Equal(1, Int(runtime, "onACase"));
        }

        /// <summary>
        /// The argument is read twice by the test and only once by the program: it goes into a
        /// temporary, so an argument with an effect keeps having exactly one.
        /// </summary>
        [Fact]
        public void ContainsEvaluatesItsArgumentExactlyOnce()
        {
            var runtime = Run(
                Perms
                    + "var calls: int = 0;\n"
                    + "public fun readCalls(): int { return calls; }\n"
                    + "fun pick(): Perm { calls = calls + 1; return Perm.Write; }\n"
                    + "public fun run(): int { let p: Perm = Perm.Read | Perm.Write; return p.contains(pick()) ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
            Assert.Equal(1, Int(runtime, "readCalls"));
        }

        #endregion

        #region Reflexion de atributos: Type/Member (Fase 6)

        /// <summary>
        /// The built-in vocabulary rides the same path a user attribute does: the use serializes
        /// against the built-in class's descriptor and materializes into a real instance at load,
        /// which is what both the host (<c>TryGetAttribute</c>) and scripts
        /// (<c>Member.attributes()</c>) read afterwards.
        /// </summary>
        [Fact]
        public void ABuiltInObsoleteUseSurvivesTheImageAndMaterializes()
        {
            var emitter = Build(
                "class Player {\n"
                    + "  @Obsolete(\"use run2\")\n"
                    + "  public fun run(): void { }\n"
                    + "}");

            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes());
            using var runtime = new SurtrRuntime();
            var module = reloaded.Instantiate();
            runtime.LoadModule(module);

            Assert.True(module.FindClass("Player")!.TryGetMethods("run", out var overloads));
            Assert.Equal("Obsolete(use run2)", Describe(overloads[0]));

            Assert.True(overloads[0].TryGetAttribute(SurtrBuiltIns.Obsolete, out var usage));
            var instance = runtime.Resolve<SurtrInstance>(SurtrValue.CreateReference(usage.Instance))!;
            Assert.Equal("Obsolete", instance.Class.Name);
            Assert.Equal("use run2", runtime.Resolve<SurtrString>(instance[0])!.Text);
        }

        [Fact]
        public void AScriptReadsABuiltInAttributesReasonThroughReflection()
        {
            var runtime = Run(
                "class Player {\n"
                    + "  @NoDiscard(\"check whether it parsed\")\n"
                    + "  public fun tryRun(): bool { return true; }\n"
                    + "}\n"
                    + "fun reason(): string {\n"
                    + "    for (m in Type.of(Player()).members()) {\n"
                    + "        if (m.name == \"tryRun\") {\n"
                    + "            let attrs = m.attributes();\n"
                    + "            if (attrs.length > 0) { return (attrs[0] as NoDiscard).reason; }\n"
                    + "        }\n"
                    + "    }\n"
                    + "    return \"\";\n"
                    + "}");

            Assert.Equal("check whether it parsed", Text(runtime, "reason"));
        }

        [Fact]
        public void TypeOfReportsTheDeclaredClassName()
        {
            var runtime = Run(
                "class Box { public let value: int = 0; }\n"
                    + "fun boxTypeName(): string { return Type.of(Box()).name; }");

            Assert.Equal("Box", Text(runtime, "boxTypeName"));
        }

        [Fact]
        public void TypeOfBoxesAPrimitiveOntoItsSharedClass()
        {
            var runtime = Run("fun intTypeName(): string { return Type.of(5).name; }");
            Assert.Equal("int", Text(runtime, "intTypeName"));
        }

        [Fact]
        public void TypeMembersCountsDeclaredMembersOnceEachEvenAnAutoProperty()
        {
            var runtime = Run(
                "class Box {\n"
                    + "  public let value: int = 0;\n"
                    + "  public size: int { get; set; }\n"
                    + "  public fun describe(): int { return 1; }\n"
                    + "}\n"
                    + "fun boxMemberCount(): int { return Type.of(Box()).members().length; }");

            // ctor (synthesized, since `value` has an initializer) + value (field) + size
            // (property, its backing field and get_size/set_size folded into the one property) +
            // describe (method) = 4, not the 6 raw declarations the linker actually tracks.
            Assert.Equal(4, Int(runtime, "boxMemberCount"));
        }

        [Fact]
        public void TypeMembersReportsEachDeclarationsOwnKind()
        {
            var runtime = Run(
                "class Box {\n"
                    + "  public let value: int = 0;\n"
                    + "  public size: int { get; set; }\n"
                    + "  public fun describe(): int { return 1; }\n"
                    + "}\n"
                    + "fun kinds(): string {\n"
                    + "  let members = Type.of(Box()).members();\n"
                    + "  var result = \"\";\n"
                    + "  for (m in members) { result = result + m.name + \":\" + m.kind + \";\"; }\n"
                    + "  return result;\n"
                    + "}");

            string kinds = Text(runtime, "kinds");
            Assert.Contains("value:field;", kinds);
            Assert.Contains("size:property;", kinds);
            Assert.Contains("describe:method;", kinds);
        }

        [Fact]
        public void MemberDeclaringTypePointsBackToItsOwner()
        {
            var runtime = Run(
                "class Box { public let value: int = 0; }\n"
                    + "fun declaringTypeName(): string {\n"
                    + "  for (m in Type.of(Box()).members()) {\n"
                    + "    if (m.name == \"value\") { return m.declaringType.name; }\n"
                    + "  }\n"
                    + "  return \"missing\";\n"
                    + "}");

            Assert.Equal("Box", Text(runtime, "declaringTypeName"));
        }

        [Fact]
        public void ADeclarationWithNoAttributesReportsAnEmptyList()
        {
            var runtime = Run(
                "class Box { public let value: int = 0; }\n"
                    + "fun attributeCount(): int {\n"
                    + "  for (m in Type.of(Box()).members()) {\n"
                    + "    if (m.name == \"value\") { return m.attributes().length; }\n"
                    + "  }\n"
                    + "  return -1;\n"
                    + "}");

            Assert.Equal(0, Int(runtime, "attributeCount"));
        }

        /// <summary>Reads a member's attribute back as a real, already-constructed instance.</summary>
        [Fact]
        public void MemberAttributesExposesTheMaterializedAttributeInstance()
        {
            var runtime = Run(
                "class Marker : Attribute { public let n: int = 0; }\n"
                    + "class Target {\n"
                    + "  @Marker(3)\n"
                    + "  public fun thing(): int { return 1; }\n"
                    + "}\n"
                    + "fun markerValue(): int {\n"
                    + "  for (m in Type.of(Target()).members()) {\n"
                    + "    if (m.name == \"thing\") {\n"
                    + "      let marker = m.attributes()[0] as Marker;\n"
                    + "      return marker.n;\n"
                    + "    }\n"
                    + "  }\n"
                    + "  return -1;\n"
                    + "}");

            Assert.Equal(3, Int(runtime, "markerValue"));
        }

        [Fact]
        public void TypeAttributesReadsAnAttributeWrittenOnTheClassItself()
        {
            var runtime = Run(
                "class Marker : Attribute { public let n: int = 0; }\n"
                    + "@Marker(9)\n"
                    + "class Tagged { public let value: int = 0; }\n"
                    + "fun taggedMarkerValue(): int { return (Type.of(Tagged()).attributes()[0] as Marker).n; }");

            Assert.Equal(9, Int(runtime, "taggedMarkerValue"));
        }

        [Fact]
        public void TypeBaseTypeWalksToTheDeclaredParentAndIsObjectAtTheRoot()
        {
            var runtime = Run(
                "class Animal { public let legs: int = 4; }\n"
                    + "class Dog : Animal { public let name: string = \"Rex\"; }\n"
                    + "fun dogBaseName(): string { return Type.of(Dog()).baseType.name; }\n"
                    + "fun animalBaseIsObject(): int {\n"
                    + "  if (Type.of(Animal()).baseType.name == \"object\") { return 1; }\n"
                    + "  return 0;\n"
                    + "}");

            Assert.Equal("Animal", Text(runtime, "dogBaseName"));
            Assert.Equal(1, Int(runtime, "animalBaseIsObject"));
        }

        /// <summary>
        /// The polymorphic smoke test the whole feature exists for: a value statically known only
        /// as `object` still reaches equals/hashCode/toString through the vtable, landing on
        /// whatever the concrete class - here one with no override of its own - actually is.
        /// </summary>
        [Fact]
        public void APlainClassAnsweredThroughObjectUsesTheInheritedDefaults()
        {
            var runtime = Run(
                "class Animal { public let legs: int = 4; }\n"
                    + "fun sameInstanceEqualsItself(): int {\n"
                    + "  let a = Animal();\n"
                    + "  let asObject: object = a;\n"
                    + "  return asObject.equals(a) ? 1 : 0;\n"
                    + "}\n"
                    + "fun differentInstancesAreNotEqual(): int {\n"
                    + "  let asObject: object = Animal();\n"
                    + "  return asObject.equals(Animal()) ? 1 : 0;\n"
                    + "}\n"
                    + "fun defaultToStringNamesTheClass(): string {\n"
                    + "  let asObject: object = Animal();\n"
                    + "  return asObject.toString();\n"
                    + "}\n"
                    + "fun hashCodeIsStableForTheSameInstance(): int {\n"
                    + "  let a = Animal();\n"
                    + "  let asObject: object = a;\n"
                    + "  return asObject.hashCode() == asObject.hashCode() ? 1 : 0;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "sameInstanceEqualsItself"));
            Assert.Equal(0, Int(runtime, "differentInstancesAreNotEqual"));
            Assert.Equal("Animal", Text(runtime, "defaultToStringNamesTheClass"));
            Assert.Equal(1, Int(runtime, "hashCodeIsStableForTheSameInstance"));
        }

        /// <summary>
        /// `object`/`Enum`/`ValueType` declare no constructor, so a class whose base resolves to
        /// one implicitly - no constructor of its own, no field initializer - gets no synthesised
        /// chain at all (`ModuleEmitter.NeedsConstruction` walks the base chain and finds nothing
        /// to call). Constructing an instance must not throw or otherwise misbehave for that reason.
        /// </summary>
        [Fact]
        public void AClassWithNoConstructorAndNoBaseToConstructBuildsCleanly()
        {
            var runtime = Run(
                "class Empty { }\n"
                    + "fun make(): int {\n"
                    + "  let e = Empty();\n"
                    + "  return e == null ? 0 : 1;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "make"));
        }

        /// <summary>
        /// A primitive boxed behind `object` reaches the same default equals/hashCode/toString a
        /// user class does, and its own toString() - now a real override of object's slot - is
        /// what actually runs, not object's generic class-name fallback.
        /// </summary>
        [Fact]
        public void APrimitiveThroughObjectUsesItsOwnToStringNotTheGenericDefault()
        {
            var runtime = Run(
                "fun boxedIntToString(): string {\n"
                    + "  let asObject: object = 5;\n"
                    + "  return asObject.toString();\n"
                    + "}\n"
                    + "fun boxedIntsCompareByValue(): int {\n"
                    + "  let a: object = 5;\n"
                    + "  let b: object = 5;\n"
                    + "  return a.equals(b) ? 1 : 0;\n"
                    + "}");

            Assert.Equal("5", Text(runtime, "boxedIntToString"));
            Assert.Equal(1, Int(runtime, "boxedIntsCompareByValue"));
        }

        /// <summary>
        /// A range's inline three-slot representation needed its own receiver-convention fix,
        /// separate from a multi-field value class's: <c>SurtrMethodInfo.ArgumentSlotCount</c>
        /// hardcoded the receiver width to 3 for any member declared on <c>range</c> regardless of
        /// dispatch, which only <c>toString()</c> - now an override of <c>object</c>'s virtual slot
        /// - ever exercised as a non-Direct call. This is the same polymorphic path the multi-field
        /// value class test above exercises, for the representation that needed a different fix.
        /// </summary>
        [Fact]
        public void ARangeThroughObjectUsesItsOwnToStringNotTheGenericDefault()
        {
            var runtime = Run(
                "fun rangeAsObjectToString(): string {\n"
                    + "  let asObject: object = 1..4;\n"
                    + "  return asObject.toString();\n"
                    + "}");

            Assert.Equal("1..4", Text(runtime, "rangeAsObjectToString"));
        }

        /// <summary>Every built-in is declared sealed once it extends object, so nothing may extend it.</summary>
        [Fact]
        public void ExtendingABuiltInIsRejected()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", "class Foo : int { }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidBaseType);
        }

        /// <summary>
        /// The baseline this feature must not disturb: <c>array&lt;T&gt;.indexOf</c>/<c>contains</c>
        /// reach an ordinary class instance's equality through <c>SurtrValueComparer</c>'s untyped
        /// fallback (there is no <c>SurtrInstance</c> case for a non-value-type class - see
        /// <c>ReferencesEqual</c>'s <c>default:</c>), and a class declaring no <c>equals</c> of its
        /// own must still compare by identity there, exactly as before <c>SurtrObject.EqualsOverridable</c>
        /// existed.
        /// </summary>
        [Fact]
        public void AnArrayOfPlainClassesWithNoEqualsOverride_IndexOfStillComparesByIdentity()
        {
            var runtime = Run(
                "class Point { public let x: int; public constructor(x: int) { this.x = x; } }\n"
                    + "fun run(): int {\n"
                    + "  let a = Point(1);\n"
                    + "  let b = Point(1);\n"
                    + "  let xs = [a];\n"
                    + "  return (xs.contains(a) ? 1 : 0) * 10 + (xs.contains(b) ? 1 : 0);\n"
                    + "}");

            Assert.Equal(10, Int(runtime, "run"));
        }

        /// <summary>
        /// The new behaviour Part B of the <c>object</c> root work exists for: a class writing a
        /// real <c>override fun equals(other: object?): bool</c> is now honoured by the same
        /// generic/untyped <c>array&lt;T&gt;.indexOf</c>/<c>contains</c> path the identity test
        /// above exercises - the vtable slot no longer resolves to <c>object.equals</c>, so
        /// <see cref="Surtr.Runtime.Objects.SurtrObject.EqualsOverridable"/>'s slow path runs the
        /// override instead of the comparer's identity default.
        /// </summary>
        [Fact]
        public void AnArrayOfPlainClassesWithARealEqualsOverride_IndexOfNowRespectsIt()
        {
            var runtime = Run(
                "class Point {\n"
                    + "  public let x: int;\n"
                    + "  public constructor(x: int) { this.x = x; }\n"
                    + "  public override fun equals(other: object?): bool {\n"
                    + "    let p = other as? Point;\n"
                    + "    return p != null && p.x == this.x;\n"
                    + "  }\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "  let a = Point(1);\n"
                    + "  let b = Point(1);\n"
                    + "  let c = Point(2);\n"
                    + "  let xs = [a];\n"
                    + "  return (xs.contains(b) ? 1 : 0) * 10 + (xs.contains(c) ? 1 : 0);\n"
                    + "}");

            Assert.Equal(10, Int(runtime, "run"));
        }
        #endregion

        #region typeof (Fase 13)
        [Fact]
        public void TypeOfOnAnInstanceMatchesItsClassName()
        {
            var runtime = Run(
                "class Box { public let value: int = 0; }\n"
                    + "fun boxTypeName(): string { return typeof(Box()).name; }");

            Assert.Equal("Box", Text(runtime, "boxTypeName"));
        }

        [Fact]
        public void TypeOfOnAPrimitiveNamesItsSharedClass()
        {
            var runtime = Run("fun intTypeName(): string { return typeof(5).name; }");
            Assert.Equal("int", Text(runtime, "intTypeName"));
        }

        [Fact]
        public void TypeOfOnATypeNameNamesItDirectlyWithNoInstance()
        {
            var runtime = Run(
                "class Box { public let value: int = 0; }\n"
                    + "fun boxTypeName(): string { return typeof(Box).name; }");

            Assert.Equal("Box", Text(runtime, "boxTypeName"));
        }

        [Fact]
        public void TypeOfOnAnInterfaceNameResolvesToItWithNoBaseType()
        {
            var runtime = Run(
                "interface INamed { name: string { get; } }\n"
                    + "fun namedTypeName(): string { return typeof(INamed).name; }\n"
                    + "fun namedIsInterface(): int { return typeof(INamed).isInterface ? 1 : 0; }\n"
                    + "fun namedHasNoBase(): int {\n"
                    + "  if (typeof(INamed).baseType == null) { return 1; }\n"
                    + "  return 0;\n"
                    + "}");

            Assert.Equal("INamed", Text(runtime, "namedTypeName"));
            Assert.Equal(1, Int(runtime, "namedIsInterface"));
            Assert.Equal(1, Int(runtime, "namedHasNoBase"));
        }

        [Fact]
        public void TypeOfOnAClassNameIsNotAnInterface()
        {
            var runtime = Run(
                "class Box { public let value: int = 0; }\n"
                    + "fun boxIsInterface(): int { return typeof(Box).isInterface ? 1 : 0; }");

            Assert.Equal(0, Int(runtime, "boxIsInterface"));
        }

        [Fact]
        public void TypeOfSharesIdentityAcrossRepeatedCallsOnTheSameType()
        {
            var runtime = Run(
                "class Box { public let value: int = 0; }\n"
                    + "fun sameType(): int { return typeof(Box()) === typeof(Box()) ? 1 : 0; }\n"
                    + "fun sameStaticType(): int { return typeof(Box) === typeof(Box) ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "sameType"));
            Assert.Equal(1, Int(runtime, "sameStaticType"));
        }

        [Fact]
        public void TypeOfAndTypeDotOfShareTheSameCachedIdentity()
        {
            var runtime = Run(
                "class Box { public let value: int = 0; }\n"
                    + "fun sameType(): int { return typeof(Box()) === Type.of(Box()) ? 1 : 0; }\n"
                    + "fun sameAsStatic(): int { return typeof(Box) === Type.of(Box()) ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "sameType"));
            Assert.Equal(1, Int(runtime, "sameAsStatic"));
        }

        /// <summary>
        /// The instance form reads the value's actual runtime class, not its static one - the whole
        /// reason it needs a runtime read at all rather than always resolving statically.
        /// </summary>
        [Fact]
        public void TypeOfOnAPolymorphicValueReadsItsRuntimeClassNotItsStaticType()
        {
            var runtime = Run(
                "class Animal { public let legs: int = 4; }\n"
                    + "class Dog : Animal { public let name: string = \"Rex\"; }\n"
                    + "fun dogTypeName(): string {\n"
                    + "  let a: Animal = Dog();\n"
                    + "  return typeof(a).name;\n"
                    + "}");

            Assert.Equal("Dog", Text(runtime, "dogTypeName"));
        }

        /// <summary>
        /// §1.1's separate type/value namespaces let `Box` name a class and a local at once; the
        /// binder resolves the ambiguity type-first, the same order every other place this binder
        /// meets the identical ambiguity already uses (see <c>TryBindAsType</c>'s own remarks).
        /// </summary>
        [Fact]
        public void TypeOfPrefersATypeNameOverASameNamedLocal()
        {
            var runtime = Run(
                "class Box { public let value: int = 0; }\n"
                    + "fun shadowed(): string {\n"
                    + "  let Box = 5;\n"
                    + "  return typeof(Box).name;\n"
                    + "}");

            Assert.Equal("Box", Text(runtime, "shadowed"));
        }

        /// <summary>
        /// A name followed by a generic argument list is the one shape the parser resolves as a
        /// type on its own (<c>Parser.LooksLikeGenericTypeOnlyAhead</c>), since a bare Surtr
        /// expression has no <c>&lt;...&gt;</c> of its own to collide with it.
        /// </summary>
        [Fact]
        public void TypeOfOnAGenericTypeNameParsesAsTheStaticForm()
        {
            var runtime = Run(
                "class Box<T> { public fun n(): int { return 1; } }\n"
                    + "fun boxIntTypeName(): string { return typeof(Box<int>).name; }");

            // §6: arity mangles into the name segment, so a generic class's own metadata name
            // carries the backtick - the same `Box`1` any other reflection over it would report.
            Assert.Equal("Box`1", Text(runtime, "boxIntTypeName"));
        }

        /// <summary>
        /// The generic metadata the compiler now keeps (§docs/Plan-Genericos-Metadata.md, Pasos 1-2)
        /// is readable from Surtr: a Type's parameter names and their bounds are the class's own
        /// tables, exposed verbatim. The open class — whose descriptor's argument is the
        /// declaration's own parameter — is reached through Type.get of its open descriptor, since
        /// neither typeof(Box) nor Box() can name it (the parser only reads a type operand with an
        /// argument list, and a construction without arguments is ambiguous).
        /// </summary>
        [Fact]
        public void AGenericTypesParametersAndConstraintsAreReadableFromReflection()
        {
            var runtime = Run(
                "class Box<T : IComparable<T>> { public fun n(): int { return 1; } }\n"
                    + "fun parameterCount(): int { return Type.get(\"Ogame.core.Test:Box`1;G0\").genericParameterCount; }\n"
                    + "fun parameterName(): string { return Type.get(\"Ogame.core.Test:Box`1;G0\").genericParameters()[0]; }\n"
                    + "fun firstConstraint(): string { return Type.get(\"Ogame.core.Test:Box`1;G0\").genericConstraints()[0][0]; }");

            Assert.Equal(1, Int(runtime, "parameterCount"));
            Assert.Equal("T", Text(runtime, "parameterName"));
            Assert.Equal("Osurtr:IComparable`1;G0", Text(runtime, "firstConstraint"));
        }

        [Fact]
        public void AConstructionRetainsItsArgumentsAndItsDescriptor()
        {
            var runtime = Run(
                "class Box<T> { public let value: T; constructor(value: T) { this.value = value; } }\n"
                    + "fun argumentName(): string { return typeof(Box<int>).genericArguments()[0].name; }\n"
                    + "fun descriptor(): string { return typeof(Box<int>).descriptor; }\n"
                    + "fun sameViaGet(): int { return typeof(Box<int>) === Type.get(\"Ogame.core.Test:Box`1;I\") ? 1 : 0; }");

            Assert.Equal("int", Text(runtime, "argumentName"));
            Assert.Equal("Ogame.core.Test:Box`1;I", Text(runtime, "descriptor"));
            Assert.Equal(1, Int(runtime, "sameViaGet"));
        }

        /// <summary>
        /// The whole point of retaining the descriptor: two constructions of one class are two
        /// distinct Type values with distinct arguments, exactly as C#'s List&lt;int&gt; and
        /// List&lt;string&gt; are distinct types. Nothing reified - one class, one method table -
        /// only the descriptor that named each construction.
        /// </summary>
        [Fact]
        public void TwoConstructionsAreDistinctTypeValuesWithTheirOwnArguments()
        {
            var runtime = Run(
                "class Box<T> { }\n"
                    + "fun distinct(): int { return typeof(Box<int>) === typeof(Box<string>) ? 1 : 0; }\n"
                    + "fun intArgument(): string { return typeof(Box<int>).genericArguments()[0].name; }\n"
                    + "fun stringArgument(): string { return typeof(Box<string>).genericArguments()[0].name; }");

            Assert.Equal(0, Int(runtime, "distinct"));
            Assert.Equal("int", Text(runtime, "intArgument"));
            Assert.Equal("string", Text(runtime, "stringArgument"));
        }

        /// <summary>
        /// An open form — the descriptor whose argument is the declaration's own parameter — is
        /// the class itself, not a construction: same identity as Type.of(instance), and no
        /// arguments to report. typeof(Box) cannot reach it (the parser only reads a type operand
        /// when there is an argument list), so the open class is reached through Type.of or
        /// Type.get of its open descriptor.
        /// </summary>
        [Fact]
        public void TheOpenFormIsTheSharedClassNotAConstruction()
        {
            var runtime = Run(
                "class Box<T> { public fun n(): int { return 1; } }\n"
                    + "fun openHasNoArguments(): int { return Type.get(\"Ogame.core.Test:Box`1;G0\").genericArguments().length; }\n"
                    + "fun openIsTheInstanceClass(): int { return Type.get(\"Ogame.core.Test:Box`1;G0\") === Type.of(Box<int>()) ? 1 : 0; }");

            Assert.Equal(0, Int(runtime, "openHasNoArguments"));
            Assert.Equal(1, Int(runtime, "openIsTheInstanceClass"));
        }

        /// <summary>
        /// An instance carries no construction, so Type.of and the instance typeof cannot say which
        /// one it is - the documented limit of the class-shared design. The descriptor is null and
        /// the arguments are empty, rather than guessed.
        /// </summary>
        [Fact]
        public void AnInstanceCannotSayItsConstruction()
        {
            var runtime = Run(
                "class Box<T> { public fun n(): int { return 1; } }\n"
                    + "fun noArguments(): int { return Type.of(Box<int>()).genericArguments().length; }\n"
                    + "fun noDescriptor(): int { return Type.of(Box<int>()).descriptor == null ? 1 : 0; }\n"
                    + "fun openViaGetHasNoArguments(): int { return Type.get(\"Ogame.core.Test:Box`1;G0\").genericArguments().length; }");

            Assert.Equal(0, Int(runtime, "noArguments"));
            Assert.Equal(1, Int(runtime, "noDescriptor"));
            Assert.Equal(0, Int(runtime, "openViaGetHasNoArguments"));
        }

        [Fact]
        public void TypeGetOnAConstructionRetainsTheDescriptorItWasAskedFor()
        {
            var runtime = Run(
                "class Box<T> { }\n"
                    + "fun descriptor(): string { return Type.get(\"Ogame.core.Test:Box`1;S\").descriptor; }\n"
                    + "fun argumentName(): string { return Type.get(\"Ogame.core.Test:Box`1;S\").genericArguments()[0].name; }");

            Assert.Equal("Ogame.core.Test:Box`1;S", Text(runtime, "descriptor"));
            Assert.Equal("string", Text(runtime, "argumentName"));
        }
        #endregion

        #region Accessibility (§3.1)
        private static SurtrCompilation Reject(string source, params (string Path, string Text)[] extra)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            foreach (var (path, text) in extra)
                project.AddSourceFile(Root + path, text);

            var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();
            return compilation;
        }

        [Fact]
        public void APrivateFieldIsNotReachableFromOutsideItsType()
        {
            using var compilation = Reject("class C { private let n: int = 1; }\nfun run(): int { return C().n; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.Inaccessible);
        }

        /// <summary>§3.1: a class member with no visibility written is private.</summary>
        [Fact]
        public void AMemberWithNoVisibilityWrittenIsPrivate()
        {
            using var compilation = Reject("class C { let n: int = 1; }\nfun run(): int { return C().n; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.Inaccessible);
        }

        [Fact]
        public void APrivateMethodIsNotReachableFromOutsideItsType()
        {
            using var compilation = Reject(
                "class C { private fun hidden(): int { return 1; } }\nfun run(): int { return C().hidden(); }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.Inaccessible);
        }

        [Fact]
        public void AProtectedMemberIsNotReachableFromOutsideTheHierarchy()
        {
            using var compilation = Reject(
                "class Base { protected fun step(): int { return 1; } }\n"
                    + "class Other { public fun poke(b: Base): int { return b.step(); } }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.Inaccessible);
        }

        /// <summary>§3.1's other default: a top-level declaration is internal to its own module.</summary>
        [Fact]
        public void AModuleLevelFunctionIsNotReachableFromAnotherModule()
        {
            using var compilation = Reject(
                "import game.util.*;\nfun run(): int { return secret(); }",
                ("/game/util/M.surtr", "internal fun secret(): int { return 1; }"));

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.Inaccessible);
        }

        [Fact]
        public void AnInternalTypeIsNotReachableFromAnotherModule()
        {
            using var compilation = Reject(
                "import game.util.*;\nfun run(): int { let h = Hidden(); return 1; }",
                ("/game/util/M.surtr", "internal class Hidden { public constructor() { } }"));

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.Inaccessible);
        }

        /// <summary>And writing it out in full does not get around it (§2.1's convenience, not a loophole).</summary>
        [Fact]
        public void AQualifiedNameDoesNotBypassVisibility()
        {
using var compilation = Reject(
                  "fun run(): int { let t: game.util.M.Quiet? = null; return 1; }",
                  ("/game/util/M.surtr", "class Quiet { }"));

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.Inaccessible);
        }

        /// <summary>§2.6: a nested type takes a visibility like any other member.</summary>
        [Fact]
        public void APrivateNestedTypeIsNotReachableFromOutside()
        {
            using var compilation = Reject("class Outer { class Inner { } }\nfun run(): int { let x: Outer.Inner? = null; return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.Inaccessible);
        }

        [Fact]
        public void APrivateMemberIsReachableFromItsOwnType()
        {
            var runtime = Run(
                "class C {\n"
                    + "  private let n: int = 1;\n"
                    + "  public fun read(): int { return n; }\n"
                    + "}\n"
                    + "fun run(): int { return C().read(); }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>
        /// What <c>private</c> names is a declaration's whole text, so one instance reaches another's
        /// — the rule C# and Java both have.
        /// </summary>
        [Fact]
        public void APrivateMemberIsReachableOnAnotherInstanceOfTheSameType()
        {
            var runtime = Run(
                "class C {\n"
                    + "  private let n: int;\n"
                    + "  constructor(n: int) { this.n = n; }\n"
                    + "  public fun other(c: C): int { return c.n; }\n"
                    + "}\n"
                    + "fun run(): int { return C(1).other(C(2)); }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void AProtectedMemberIsReachableFromADerivedType()
        {
            var runtime = Run(
                "class Base { protected fun step(): int { return 5; } }\n"
                    + "class Derived : Base { public fun go(): int { return step(); } }\n"
                    + "fun run(): int { return Derived().go(); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>A nested type is written inside its container's text, so it sees its privates.</summary>
        [Fact]
        public void ANestedTypeReachesItsContainersPrivates()
        {
            var runtime = Run(
                "class Outer {\n"
                    + "  private static let Secret: int = 7;\n"
                    + "  public class Inner { public fun read(): int { return Outer.Secret; } }\n"
                    + "}\n"
                    + "fun run(): int { return Outer.Inner().read(); }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>
        /// Accessibility filters the candidate set rather than judging the winner, so a public
        /// overload is not shadowed by a private one it was never competing with.
        /// </summary>
        [Fact]
        public void APublicOverloadWinsOverAnInaccessibleOne()
        {
            var runtime = Run(
                "class C {\n"
                    + "  private fun pick(x: int): int { return 1; }\n"
                    + "  public fun pick(x: string): int { return 2; }\n"
                    + "}\n"
                    + "fun run(): int { return C().pick(\"a\"); }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void AnInternalMemberIsReachableWithinItsOwnModule()
        {
            var runtime = Run("internal fun helper(): int { return 3; }\nfun run(): int { return helper(); }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        /// <summary>The standard library is public, and every program leans on it (§13).</summary>
        [Fact]
        public void TheStandardLibraryStaysReachable()
        {
            var runtime = Run("fun run(): int { var xs: int[] = [1]; xs.push(2); return xs.length + \"abc\".length; }");

            Assert.Equal(5, Int(runtime, "run"));
        }
        #endregion

        #region Lambda inference (§8, §5.9)
        /// <summary>
        /// §5.9 lets a lambda's parameters go unwritten where a target type supplies them, and at a
        /// call site that target is the parameter of whichever overload wins.
        /// </summary>
        [Fact]
        public void ALambdaTakesItsParameterTypesFromTheParameterItIsPassedTo()
        {
            var runtime = Run(
                "fun apply(f: (int) -> int): int { return f(3); }\nfun run(): int { return apply((x) => x * 2); }");

            Assert.Equal(6, Int(runtime, "run"));
        }

        /// <summary>§8's own example, which needs both parameters typed from `sort`'s comparator.</summary>
        [Fact]
        public void TheComparatorInSpecSection8Compiles()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  var xs: int[] = [3, 1, 2];\n"
                    + "  xs.sort((a, b) => a - b);\n"
                    + "  return xs.get(0);\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AnInferredLambdaTakesItsReturnTypeFromTheTargetToo()
        {
            var runtime = Run(
                "fun test(f: (int) -> bool): bool { return f(2); }\nfun run(): bool { return test((n) => n > 1); }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        [Fact]
        public void AnInferredLambdaStillCaptures()
        {
            var runtime = Run(
                "fun apply(f: (int) -> int): int { return f(3); }\n"
                    + "fun run(): int { let bonus = 7; return apply((x) => x + bonus); }");

            Assert.Equal(10, Int(runtime, "run"));
        }

        [Fact]
        public void AConstructorsClosureParameterTypesALambdaToo()
        {
            var runtime = Run(
                "class Runner {\n"
                    + "  private let _f: (int) -> int;\n"
                    + "  constructor(f: (int) -> int) { _f = f; }\n"
                    + "  public fun run(n: int): int { return _f(n); }\n"
                    + "}\n"
                    + "fun run(): int { let r = Runner((x) => x * 2); return r.run(4); }");

            Assert.Equal(8, Int(runtime, "run"));
        }

        [Fact]
        public void ANamedArgumentStillTypesItsLambda()
        {
            var runtime = Run(
                "fun apply(label: string, f: (int) -> int): int { return f(3); }\n"
                    + "fun run(): int { return apply(label: \"x\", f: (n) => n * 2); }");

            Assert.Equal(6, Int(runtime, "run"));
        }

        /// <summary>
        /// Arity is all applicability can ask of an unbound lambda, since its parameter types come
        /// <em>from</em> the parameter — but arity is enough to tell two overloads apart.
        /// </summary>
        [Fact]
        public void ArityPicksBetweenTwoClosureOverloads()
        {
            var runtime = Run(
                "fun on(f: (int) -> int): int { return 1; }\n"
                    + "fun on(f: (int, int) -> int): int { return 2; }\n"
                    + "fun run(): int { return on((a, b) => a + b); }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericMethodsClosureParameterTypesItsLambda()
        {
            var runtime = Run(
                "fun applyTo<T>(value: T, f: (T) -> T): T { return f(value); }\n"
                    + "fun run(): int { return applyTo(5, (x) => x); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void ALambdaWithNoTargetAtAllIsStillReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", "fun run(): int { let f = (x) => x * 2; return 1; }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CannotInferType);
        }

        /// <summary>
        /// A lambda of the wrong arity fails the <em>call</em>, and only that: binding it anyway
        /// would report that its parameters have no types, which points at the lambda rather than at
        /// the call that is actually wrong.
        /// </summary>
        [Fact]
        public void ALambdaOfTheWrongArityReportsTheCallAndNothingElse()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "fun apply(f: (int) -> int): int { return f(3); }\nfun run(): int { return apply((a, b) => a + b); }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnresolvedCall);
            Assert.DoesNotContain(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CannotInferType);
        }

        /// <summary>And an error inside the body is reported once, from the one binding it gets.</summary>
        [Fact]
        public void AnErrorInsideAnInferredLambdaIsReportedOnce()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "fun apply(f: (int) -> int): int { return f(3); }\nfun run(): int { return apply((x) => nope(x)); }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Single(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnresolvedName);
        }
        #endregion

        #region Generics (§6)
        private const string Box =
            "class Box<T> {\n"
                + "  private let _value: T;\n"
                + "  constructor(value: T) { _value = value; }\n"
                + "  public fun get(): T { return _value; }\n"
                + "}\n";

        /// <summary>
        /// §6's own example: a bound is what lets a body call anything on a <c>T</c> at all.
        /// </summary>
        [Fact]
        public void AConstraintExposesItsMembersOnATypeParameter()
        {
            var runtime = Run(
                "class Score : IComparable<Score> {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "  public fun compareTo(other: Score): int { return value - other.value; }\n"
                    + "}\n"
                    + "fun biggest<T : IComparable<T>>(a: T, b: T): T { return a.compareTo(b) >= 0 ? a : b; }\n"
                    + "fun run(): int { let s: Score = biggest(Score(4), Score(9)); return s.value; }");

            Assert.Equal(9, Int(runtime, "run"));
        }

        [Fact]
        public void AStaticFieldOnAGenericClassIsReachedThroughItsConstruction()
        {
            // §6: a static member of a generic class is reached through a *construction* —
            // `Box<int>.counter` — which substitutes the type. The field is one slot shared by
            // every construction (erasure).
            var runtime = Run(
                "class Counter<T> {\n"
                    + "  public static var total: int = 0;\n"
                    + "  public fun bump(): int { Counter<int>.total = Counter<int>.total + 1; return Counter<int>.total; }\n"
                    + "}\n"
                    + "fun run(): int { let c = Counter<int>(); c.bump(); c.bump(); return Counter<int>.total; }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void AStaticFieldNotMentioningTheTypeParameterWorksOnTheOpenForm()
        {
            // `Box<>.total` (open form) is valid when the member's type does not mention `T`; the
            // statics are shared by every construction, so the slot is the same one.
            var runtime = Run(
                "class Counter<T> {\n"
                    + "  public static var total: int = 0;\n"
                    + "}\n"
                    + "fun run(): int { Counter<>.total = 41; return Counter<int>.total + 1; }");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void AnOpenFormStaticThatMentionsTheTypeParameterIsRejected()
        {
            // `Box<>.make` has `T` in its signature, so the open form would hand back an
            // unsubstituted type; the construction form `Box<int>.make` is the one that works.
            using var compilation = Reject(
                "class Box<T> {\n"
                    + "  private let _value: T;\n"
                    + "  constructor(value: T) { _value = value; }\n"
                    + "  public static fun make(value: T): Box<T> { return Box<T>(value); }\n"
                    + "}\n"
                    + "fun run(): int { let b = Box<>.make(5); return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.WrongTypeArgumentCount);
        }

        [Fact]
        public void AStaticMethodOnAGenericClassIsReachedThroughItsConstruction()
        {
            var runtime = Run(
                "class Box<T> {\n"
                    + "  private let _value: T;\n"
                    + "  constructor(value: T) { _value = value; }\n"
                    + "  public fun get(): T { return _value; }\n"
                    + "  public static fun make(value: T): Box<T> { return Box<T>(value); }\n"
                    + "}\n"
                    + "fun run(): int { return Box<int>.make(7).get(); }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AStaticPropertyOnAGenericClassSubstitutesItsType()
        {
            // A static property whose type mentions `T` substitutes it through a construction:
            // `Holder<int>.last` binds as a `Holder<int>`, not the open `Holder<T>`.
            var runtime = Run(
                "class Holder<T> {\n"
                    + "  private let _value: T;\n"
                    + "  constructor(value: T) { _value = value; }\n"
                    + "  public fun get(): T { return _value; }\n"
                    + "  public static last: int => 42;\n"
                    + "}\n"
                    + "fun run(): int { return Holder<int>.last; }");

            Assert.Equal(42, Int(runtime, "run"));
        }

        /// <summary>
        /// A direct call on a statically-typed <c>int</c> receiver never goes through a type
        /// parameter at all: overload resolution just finds <c>int</c>'s own <c>compareTo</c>, and
        /// the call still has to box the receiver before <c>InvokeVirtual</c> can look its class up
        /// in the entity registry - the exact same box a generic call needs, just reached without a
        /// constraint in the way.
        /// </summary>
        [Fact]
        public void AnIntLiteralCallsCompareToDirectly()
        {
            var runtime = Run("fun run(): int { return 5.compareTo(3); }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AnIntLiteralCallsEqualsDirectly()
        {
            var runtime = Run("fun run(): bool { return 5.equals(5) && !5.equals(6); }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        /// <summary>
        /// Casting to the contract explicitly (rather than through a generic constraint) reaches
        /// the exact same vtable slot through <c>as</c>'s ordinary reference conversion, which boxes
        /// the primitive on the way in - a second, independent path to the same slot
        /// <see cref="APrimitiveIntSatisfiesAnIComparableConstraint"/> reaches generically.
        /// </summary>
        [Fact]
        public void AnIntCastToIComparableCallsCompareToThroughTheInterface()
        {
            var runtime = Run(
                "fun run(): int { let c: IComparable<int> = 7 as IComparable<int>; return c.compareTo(3); }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>
        /// The built-ins satisfy the same contracts a user class does (§13.2):
        /// <c>int : IComparable&lt;int&gt;</c>, so <c>biggest&lt;T : IComparable&lt;T&gt;&gt;</c>
        /// instantiates with <c>T = int</c> exactly as it does with a Surtr class. This is the
        /// generic-constraint path, which reaches a primitive receiver through
        /// <c>InvokeInterface</c> - unlike a direct, statically-typed call, it has to box the
        /// receiver first since interface dispatch resolves the callee's class through the entity
        /// registry, which only a boxed value is in.
        /// </summary>
        [Fact]
        public void APrimitiveIntSatisfiesAnIComparableConstraint()
        {
            var runtime = Run(
                "fun biggest<T : IComparable<T>>(a: T, b: T): T { return a.compareTo(b) >= 0 ? a : b; }\n"
                    + "fun run(): int { return biggest(4, 9); }");

            Assert.Equal(9, Int(runtime, "run"));
        }

        /// <summary>The same contract, satisfied by <c>float</c> rather than <c>int</c>.</summary>
        [Fact]
        public void APrimitiveFloatSatisfiesAnIComparableConstraint()
        {
            var runtime = Run(
                "fun biggest<T : IComparable<T>>(a: T, b: T): T { return a.compareTo(b) >= 0 ? a : b; }\n"
                    + "fun run(): float { return biggest(4.5, 2.5); }");

            Assert.Equal(4.5f, Call(runtime, "run").AsFloat, 3);
        }

        /// <summary>The same contract again, satisfied by <c>string</c> - already a reference, so no boxing is needed.</summary>
        [Fact]
        public void AStringSatisfiesAnIComparableConstraint()
        {
            var runtime = Run(
                "fun biggest<T : IComparable<T>>(a: T, b: T): T { return a.compareTo(b) >= 0 ? a : b; }\n"
                    + "fun run(): string { return biggest(\"apple\", \"banana\"); }");

            Assert.Equal("banana", Text(runtime, "run"));
        }

        /// <summary>
        /// <c>char</c> and <c>bool</c> also satisfy their contracts: <c>char</c> orders, <c>bool</c>
        /// only equates (§13.2 - the language defines no ordering over booleans).
        /// </summary>
        [Fact]
        public void ACharSatisfiesAnIComparableConstraint()
        {
            var runtime = Run(
                "fun biggest<T : IComparable<T>>(a: T, b: T): T { return a.compareTo(b) >= 0 ? a : b; }\n"
                    + "fun run(): char { return biggest('a', 'z'); }");

            Assert.Equal('z', (char)Call(runtime, "run").AsChar);
        }

        [Fact]
        public void APrimitiveIntSatisfiesAnIEquatableConstraint()
        {
            var runtime = Run(
                "fun same<T : IEquatable<T>>(a: T, b: T): bool { return a.equals(b); }\n"
                    + "fun run(): bool { return same(4, 4) && !same(4, 5); }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        [Fact]
        public void ABoolSatisfiesAnIEquatableConstraint()
        {
            var runtime = Run(
                "fun same<T : IEquatable<T>>(a: T, b: T): bool { return a.equals(b); }\n"
                    + "fun run(): bool { return same(true, true) && !same(true, false); }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        /// <summary>
        /// A composite built-in satisfies <c>IEquatable</c> by identity, like every non-primitive
        /// (<c>docs/Runtime-Model.md</c>'s rule for the object model): two arrays with the same
        /// elements are not <c>equals</c>, only an array compared against itself is.
        /// </summary>
        [Fact]
        public void AnArraySatisfiesAnIEquatableConstraintByIdentity()
        {
            var runtime = Run(
                "fun same<T : IEquatable<T>>(a: T, b: T): bool { return a.equals(b); }\n"
                    + "fun run(): bool { let xs: int[] = [1, 2, 3]; let ys: int[] = [1, 2, 3]; return same(xs, xs) && !same(xs, ys); }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        /// <summary>
        /// Regression: calling a method through a <em>user-declared</em> generic interface, where
        /// the parameter is that interface's own type parameter, used to crash the VM with
        /// <c>InvalidCastException: A '&lt;class&gt;' cannot be cast to 'erased'</c>, for every
        /// element type - unlike the built-in <c>IComparable&lt;T&gt;</c>/<c>IEquatable&lt;T&gt;</c>
        /// cases above, which always worked. The bridge <c>ModuleEmitter.EmitBridges</c> synthesizes
        /// to satisfy the interface's erased vtable slot forwards to the class's own <c>has(item: T)</c>
        /// - and since a generic class keeps one compiled body regardless of instantiation (§6), that
        /// body's own parameter is <em>itself</em> still erased. <c>Narrow</c> (the bridge's argument
        /// conversion) had no case for its destination already being a bare type parameter, so it
        /// fell into the general "cast to a concrete type" path and cast an already-erased value to
        /// the `Erased` marker class itself - which nothing is ever "a subclass of", so the cast
        /// failed unconditionally. Both the direct-dispatch call (<c>viaConcrete</c>, always worked)
        /// and the interface-dispatch one (<c>viaInterface</c>, the regression) are covered so a
        /// future change cannot fix one path while re-breaking the other.
        /// </summary>
        [Fact]
        public void AUserDeclaredGenericInterfaceDispatchesAMethodTakingItsOwnTypeParameter()
        {
            var runtime = Run(
                "interface IHolder<T> { fun has(item: T): bool; }\n"
                    + "class Box<T> : IHolder<T> {\n"
                    + "    private let _v: T;\n"
                    + "    public constructor(v: T) { _v = v; }\n"
                    + "    public fun has(item: T): bool => _v == item;\n"
                    + "}\n"
                    + "fun viaConcrete(): bool { let b = Box<int>(5); return b.has(5); }\n"
                    + "fun viaInterface(): bool { let b: IHolder<int> = Box<int>(5); return b.has(5); }\n");

            Assert.True(Call(runtime, "viaConcrete").AsBool);
            Assert.True(Call(runtime, "viaInterface").AsBool);
        }

        /// <summary>An unconstrained parameter promises nothing, and there is no root class to fall back to.</summary>
        [Fact]
        public void AnUnconstrainedTypeParameterExposesNothing()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "fun nope<T>(a: T): int { return a.compareTo(a); }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnresolvedName);
        }

        /// <summary>
        /// A bound is not only a compile-time rule: it reaches the metadata, so a module loaded
        /// from an image can still answer what <c>Box&lt;T&gt;</c> demanded of <c>T</c>.
        /// </summary>
        [Fact]
        public void AGenericClasssConstraintsReachTheMetadata()
        {
            var runtime = Run("class Box<T : IComparable<T>> { constructor() { } }");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            // The arity is part of the type's identity (§6), so the metadata name is mangled.
            Assert.True(module.TryGetClass("Box`1", out var box));

            Assert.Equal("T", box.GenericParameters[0]);
            Assert.Equal("Osurtr:IComparable`1;G0", Assert.Single(box.GenericConstraints[0]));
        }

        /// <summary>
        /// A generic method compiled into one module and called from another arrives with its type
        /// parameters intact, so inference and its constraint check work across the image rather
        /// than only in the process that compiled it.
        /// </summary>
        [Fact]
        public void AGnricMethodFromAnotherModuleInfersItsParameters()
        {
            // Phase 1: the declaring module, as an image.
            var emitter = Build(
                "public class Score : IComparable<Score> {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "  public fun compareTo(other: Score): int { return value - other.value; }\n"
                    + "}\n"
                    + "public class Plain { }\n"
                    + "public fun biggest<T : IComparable<T>>(a: T, b: T): T { return a.compareTo(b) >= 0 ? a : b; }");
            var built = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes()).Instantiate();

            // Phase 2: a fresh runtime and a fresh compilation, importing the image.
            using var runtime = new SurtrRuntime();
            runtime.LoadModule(built);

            var app = new SurtrProject(Root);
            app.AddReference(built);
            app.AddSourceFile(
                Root + "/game/util/Util.surtr",
                "import game.core.Test;\nfun run(): int { let s: Score = biggest(Score(4), Score(9)); return s.value; }");

            using var compilation = SurtrCompilation.Create(app);
            var binder = compilation.Bind();
            binder.BindBodies();
            Assert.False(compilation.HasErrors);
            var appEmitter = new ModuleEmitter(compilation, binder);
            Assert.True(appEmitter.TryEmit());
            runtime.LoadModule(appEmitter.Modules[0]);

            Assert.Equal(9, runtime.Invoke(Function(runtime, "game.util.Util", "run"), Array.Empty<SurtrValue>()).AsInt);
        }

        /// <summary>
        /// The same cross-module call, with a type the bound rejects: the constraint the declaring
        /// module wrote must still be checked against the substituted argument.
        /// </summary>
        [Fact]
        public void AGnricMethodFromAnotherModuleKeepsItsConstraint()
        {
            var emitter = Build(
                "public class Plain { }\n"
                    + "public fun biggest<T : IComparable<T>>(a: T, b: T): T { return a; }");
            var built = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes()).Instantiate();

            var app = new SurtrProject(Root);
            app.AddReference(built);
            app.AddSourceFile(
                Root + "/game/util/Util.surtr",
                "import game.core.Test;\nfun run(): int { biggest(Plain(), Plain()); return 1; }");

            using var compilation = SurtrCompilation.Create(app);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.ConstraintNotSatisfied);
        }

        /// <summary>
        /// A generic method declared inside a generic class, compiled into one module and called
        /// from another: the type's parameter (G0, in the field and the constructor) and the
        /// method's own (H0, with its own constraint) both arrive through the image, substitute,
        /// and are checked against the substituted arguments.
        /// </summary>
        [Fact]
        public void AGnricMethodOnAGnricClassSurvivesTheImage()
        {
            var emitter = Build(
                "public class Score : IComparable<Score> {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "  public fun compareTo(other: Score): int { return value - other.value; }\n"
                    + "}\n"
                    + "public class Box<T : IComparable<T>> {\n"
                    + "  public let value: T;\n"
                    + "  constructor(value: T) { this.value = value; }\n"
                    + "  public fun biggest<U : IComparable<U>>(a: U, b: U): U { return a.compareTo(b) >= 0 ? a : b; }\n"
                    + "}");
            var built = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes()).Instantiate();

            using var runtime = new SurtrRuntime();
            runtime.LoadModule(built);

            var app = new SurtrProject(Root);
            app.AddReference(built);
            app.AddSourceFile(
                Root + "/game/util/Util.surtr",
                "import game.core.Test;\n"
                    + "fun run(): int { let b: Box<Score> = Box(Score(3)); let s: Score = b.biggest(Score(4), Score(9)); return s.value; }");

            using var compilation = SurtrCompilation.Create(app);
            var binder = compilation.Bind();
            binder.BindBodies();
            Assert.False(compilation.HasErrors);
            var appEmitter = new ModuleEmitter(compilation, binder);
            Assert.True(appEmitter.TryEmit());
            runtime.LoadModule(appEmitter.Modules[0]);

            Assert.Equal(9, runtime.Invoke(Function(runtime, "game.util.Util", "run"), Array.Empty<SurtrValue>()).AsInt);
        }

        [Fact]
        public void AGenericTypeIsConstructedFromTheTypeItGoesInto()
        {
            var runtime = Run(Box + "fun run(): int { let b: Box<int> = Box(5); return b.get(); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericTypeIsConstructedFromWrittenTypeArguments()
        {
            var runtime = Run(Box + "fun run(): int { let b = Box<int>(5); return b.get(); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericTypeIsConstructedFromItsConstructorsArguments()
        {
            var runtime = Run(Box + "fun run(): int { let b = Box(5); return b.get(); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>
        /// One class, one method table, one compiled body — and two constructions that read as
        /// different types. That is the whole of what erasure buys and what the compiler owes.
        /// </summary>
        [Fact]
        public void TwoConstructionsOfOneGenericKeepTheirOwnTypes()
        {
            var runtime = Run(
                Box + "fun run(): string { let a = Box(\"x\"); let b = Box(\"y\"); return a.get() + b.get(); }");

            Assert.Equal("xy", Text(runtime, "run"));
        }

        [Fact]
        public void ASubstitutedMemberRejectsTheWrongArgument()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                Box + "fun run(): int { let b: Box<int> = Box(\"x\"); return b.get(); }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.True(compilation.HasErrors);
        }

        [Fact]
        public void AGenericTypeMayBeItsOwnTypeArgument()
        {
            var runtime = Run(
                Box + "fun run(): int {\n"
                    + "  let inner: Box<int> = Box(3);\n"
                    + "  let outer: Box<Box<int>> = Box(inner);\n"
                    + "  return outer.get().get();\n"
                    + "}");

            Assert.Equal(3, Int(runtime, "run"));
        }

        /// <summary>§6: arity is part of identity, so these are two declarations sharing a spelling.</summary>
        [Fact]
        public void ArityPicksBetweenTwoDeclarationsOfOneName()
        {
            var runtime = Run(
                "class Result<T> { public fun n(): int { return 1; } }\n"
                    + "class Result<T, E> { public fun n(): int { return 2; } }\n"
                    + "fun run(): int { let r: Result<int, string> = Result(); return r.n(); }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericCallInfersItsTypeArgumentsFromTheArguments()
        {
            var runtime = Run("fun pick<T>(a: T, b: T): T { return a; }\nfun run(): int { return pick(1, 2); }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericCallMayWriteItsTypeArguments()
        {
            var runtime = Run("fun pick<T>(a: T, b: T): T { return a; }\nfun run(): int { return pick<int>(1, 2); }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericCallInfersItsTypeArgumentsFromTheExpectedReturn()
        {
            // `let b: Box<int> = makeBox();` fills `T` from the expected return type even though no
            // argument mentions it. The body only throws, so no `T` value is ever needed.
            var runtime = Run(
                "class Box<T> {\n"
                    + "  private let _value: T;\n"
                    + "  constructor(value: T) { _value = value; }\n"
                    + "  public fun get(): T { return _value; }\n"
                    + "}\n"
                    + "class Boom : Exception {\n"
                    + "  public constructor(message: string) : super(message) { }\n"
                    + "}\n"
                    + "fun makeBox<T>(): Box<T> { throw Boom(\"unreachable\"); }\n"
                    + "fun run(): int {\n"
                    + "  try { let b: Box<int> = makeBox(); return b.get(); }\n"
                    + "  catch (e: Exception) { return 3; }\n"
                    + "}");

            Assert.Equal(3, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericConstructionInfersFromTheExpectedParameterAtACallSite()
        {
            // `take(Box())` with `take(b: Box<int>)` fills the construction's `T` from the
            // parameter the argument lands in, so the call site does not have to write it.
            var runtime = Run(
                "class Box<T> {\n"
                    + "  private let _value: T;\n"
                    + "  constructor(value: T) { _value = value; }\n"
                    + "  public fun get(): T { return _value; }\n"
                    + "}\n"
                    + "fun take(b: Box<int>): int { return b.get(); }\n"
                    + "fun run(): int { return take(Box(7)); }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AConstructionWithNoOwnSourceDefersToItsParameter()
        {
            // The real Brecha B: `take(Box())` — the construction has no type arguments written and
            // no argument of its own to infer from, so the winning parameter `Box<int>` supplies
            // them, exactly as it would type a deferred lambda.
            var runtime = Run(
                "class Box<T> {\n"
                    + "  private var _value: T?;\n"
                    + "  constructor() { _value = null; }\n"
                    + "  public constructor(value: T) { _value = value; }\n"
                    + "  public fun hasValue(): bool { return _value != null; }\n"
                    + "}\n"
                    + "fun take(b: Box<int>): int { return b.hasValue() ? 0 : 1; }\n"
                    + "fun run(): int { return take(Box()); }");

            // `Box()` built a `Box<int>` with a null value, so `hasValue` is false — the point is
            // that it bound at all: before the deferral it failed with CannotInferTypeArgument.
            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AConstructionWithItsOwnSourceStillWidensToItsParameter()
        {
            // `take(Box(5.0))` with `take(b: Box<float>)` defers the construction and re-binds it
            // against the parameter, so `Box<float>` wins (top-down target typing).
            var runtime = Run(
                "class Box<T> {\n"
                    + "  private let _value: T;\n"
                    + "  constructor(value: T) { _value = value; }\n"
                    + "  public fun get(): T { return _value; }\n"
                    + "}\n"
                    + "fun take(b: Box<float>): float { return b.get(); }\n"
                    + "fun run(): int { return take(Box(5.0)) > 4.5 ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AConstructionWithItsOwnIntStillWidensToItsFloatParameter()
        {
            // `take(Box(5))` with `take(b: Box<float>)`: the `5` is an int but the parameter is
            // `Box<float>`, so the int→float conversion has to happen BEFORE the box. The value
            // class's own type parameter `T` is already substituted to `float` in the construction,
            // so it must convert against `float` — not be treated as a method type parameter and
            // erased to `unknown`, which would box the raw int and then fail the cast on read.
            var runtime = Run(
                "class Box<T> {\n"
                    + "  private let _value: T;\n"
                    + "  constructor(value: T) { _value = value; }\n"
                    + "  public fun get(): T { return _value; }\n"
                    + "}\n"
                    + "fun take(b: Box<float>): float { return b.get(); }\n"
                    + "fun run(): int { return take(Box(5)) > 4.5 ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>Inference walks into a composite: <c>T[]</c> against an <c>int[]</c> gives <c>int</c>.</summary>
        [Fact]
        public void InferenceWalksIntoACompositeParameter()
        {
            var runtime = Run("fun count<T>(items: T[]): int { return items.length; }\nfun run(): int { return count([1, 2, 3]); }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericMethodInsideAGenericClassSubstitutesBoth()
        {
            var runtime = Run(
                "class Holder<T> {\n"
                    + "  private let _value: T;\n"
                    + "  constructor(value: T) { _value = value; }\n"
                    + "  public fun map<U>(other: U): U { return other; }\n"
                    + "}\n"
                    + "fun run(): int { let h: Holder<string> = Holder(\"x\"); return h.map(5); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>
        /// §6 checks a bound against the <em>substituted</em> type: <c>T : IComparable&lt;T&gt;</c>
        /// asked of a <c>Plain</c> is asking about <c>IComparable&lt;Plain&gt;</c>.
        /// </summary>
        [Fact]
        public void AnArgumentThatDoesNotSatisfyItsBoundIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class Plain { }\n"
                    + "fun biggest<T : IComparable<T>>(a: T, b: T): T { return a; }\n"
                    + "fun run(): int { biggest(Plain(), Plain()); return 1; }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.ConstraintNotSatisfied);
        }

        /// <summary>Two answers for one parameter is a refusal, not a widening — §3.5's "no silent pick".</summary>
        [Fact]
        public void ContradictoryInferenceIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "fun pick<T>(a: T, b: T): T { return a; }\nfun run(): int { return pick(1, \"x\"); }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CannotInferTypeArgument);
        }

        /// <summary>§1.11's two obligations, seen from source: box on the way in, cast on the way out.</summary>
        [Fact]
        public void APrimitiveSurvivesARoundTripThroughAnErasedSlot()
        {
            var runtime = Run(Box + "fun run(): int { let b: Box<int> = Box(42); let n: int = b.get(); return n + 0; }");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void AValueClassSurvivesARoundTripThroughAnErasedSlot()
        {
            var runtime = Run(
                Box + "value class EntityId {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "}\n"
                    + "fun run(): int { let b: Box<EntityId> = Box(EntityId(7)); return b.get().value; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericFromAnotherModuleIsConstructedAndCalled()
        {
            var runtime = Run(
                "import game.util.Box;\nfun run(): int { let b: Box<int> = Box(3); return b.get(); }",
                ("/game/util/Box.surtr",
                    "public class Box<T> {\n"
                        + "  private let _value: T;\n"
                        + "  public constructor(value: T) { _value = value; }\n"
                        + "  public fun get(): T { return _value; }\n"
                        + "}"));

            Assert.Equal(3, Int(runtime, "run"));
        }

        /// <summary>
        /// A generic class satisfying a generic contract, walked by <c>for-in</c> — which puts the
        /// bridge, the erased slot and interface dispatch on one path.
        /// </summary>
        [Fact]
        public void AGenericClassCanSatisfyAGenericContract()
        {
            var runtime = Run(
                "class Single<T> : IIterable<T> {\n"
                    + "  private let _value: T;\n"
                    + "  constructor(value: T) { _value = value; }\n"
                    + "  public fun iterate(): IIterator<T> { return [_value].iterate(); }\n"
                    + "}\n"
                    + "fun run(): int { var total = 0; for (n in Single(4)) { total += n; } return total; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        /// <summary>
        /// A non-generic class implementing a built-in generic interface with a fixed argument and
        /// no chain involved — the simplest shape of the scenario reported as failing to resolve;
        /// runs end to end on the real VM, not just through the binder.
        /// </summary>
        [Fact]
        public void ANonGenericClassImplementsABuiltInGenericInterfaceDirectly()
        {
            var runtime = Run(
                "class Counter : IIterable<int> {\n"
                    + "  public fun iterate(): IIterator<int> { return [1, 2, 3].iterate(); }\n"
                    + "}\n"
                    + "fun run(): int { var total = 0; for (n in Counter()) { total += n; } return total; }");

            Assert.Equal(6, Int(runtime, "run"));
        }

        [Fact]
        public void AConstructionMayStopShortOfADefaultedParameter()
        {
            var runtime = Run(
                "class Counter<T> {\n"
                    + "  private let _value: T;\n"
                    + "  private let _n: int;\n"
                    + "  constructor(value: T, n: int = 1) { _value = value; _n = n; }\n"
                    + "  public fun n(): int { return _n; }\n"
                    + "}\n"
                    + "fun run(): int { let c = Counter(\"x\"); return c.n(); }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>
        /// A construction with nothing to infer from is refused rather than guessed at, the same
        /// trade §5.9 makes for a bare <c>[]</c>.
        /// </summary>
        [Fact]
        public void AConstructionWithNothingToInferFromIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class Empty<T> { public fun n(): int { return 1; } }\nfun run(): int { let e = Empty(); return e.n(); }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CannotInferTypeArgument);
        }

        /// <summary>
        /// A construction whose arguments the compiler inferred is still a construction, and its
        /// bounds are not optional because nobody wrote them.
        /// </summary>
        [Fact]
        public void AnInferredConstructionStillChecksItsBounds()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class Plain { }\n"
                    + "class Sorted<T : IComparable<T>> {\n"
                    + "  private let _value: T;\n"
                    + "  constructor(value: T) { _value = value; }\n"
                    + "}\n"
                    + "fun run(): int { let s = Sorted(Plain()); return 1; }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.ConstraintNotSatisfied);
        }

        /// <summary>
        /// And one written inside a body: those sites are recorded while the body binds, which is
        /// after the member phase verified the ones written on declarations.
        /// </summary>
        [Fact]
        public void AConstructedTypeWrittenInABodyChecksItsBounds()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class Plain { }\n"
                    + "class Sorted<T : IComparable<T>> { }\n"
                    + "fun run(): int { let s: Sorted<Plain>? = null; return 1; }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.ConstraintNotSatisfied);
        }

        /// <summary>
        /// The other half of the type-argument scan: a <c>&lt;</c> that closes nothing is a
        /// comparison, and stays one.
        /// </summary>
        [Fact]
        public void AComparisonIsNotReadAsATypeArgumentList()
        {
            var runtime = Run("fun run(): bool { let a = 1; let b = 2; return a < b; }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        /// <summary>
        /// Inside its own declaration, a field typed `T` is not a wildcard slot — assigning a
        /// concrete literal to it is exactly as wrong as assigning it into any other type the
        /// method does not declare, and used to compile silently because `T` was classified the
        /// same way `unknown` is.
        /// </summary>
        [Fact]
        public void AssigningAConcreteLiteralIntoATypeParameterFieldIsRejected()
        {
            using var compilation = Reject(
                "class Box<T> {\n"
                    + "  public var value: T;\n"
                    + "  constructor(value: T) { this.value = value; }\n"
                    + "  public fun corrupt(): void { this.value = 5; }\n"
                    + "}");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CannotConvert);
        }

        /// <summary>The one thing that does reach a `T`-typed slot is `T` itself.</summary>
        [Fact]
        public void AssigningTheDeclaredParameterIntoATypeParameterFieldStillCompiles()
        {
            var runtime = Run(
                "class Box<T> {\n"
                    + "  public var value: T;\n"
                    + "  constructor(value: T) { this.value = value; }\n"
                    + "  public fun set(x: T): void { this.value = x; }\n"
                    + "  public fun get(): T { return this.value; }\n"
                    + "}\n"
                    + "fun run(): int { let b = Box(1); b.set(9); return b.get(); }");

            Assert.Equal(9, Int(runtime, "run"));
        }

        /// <summary>
        /// A value satisfying `T`'s own constraint still does not become assignable to a `T`-typed
        /// slot — Java has the same asymmetry, for the same reason: knowing `T` can be used as
        /// `IComparable&lt;T&gt;` says nothing about what may flow the other way into `T`.
        /// </summary>
        [Fact]
        public void SatisfyingATypeParametersConstraintDoesNotMakeAValueAssignableToIt()
        {
            using var compilation = Reject(
                "class Score : IComparable<Score> {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "  public fun compareTo(other: Score): int { return value - other.value; }\n"
                    + "}\n"
                    + "class Holder<T : IComparable<T>> {\n"
                    + "  public var item: T;\n"
                    + "  constructor(item: T) { this.item = item; }\n"
                    + "  public fun corrupt(s: Score): void { this.item = s; }\n"
                    + "}");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CannotConvert);
        }

        /// <summary>
        /// Inside its own declaration, a constrained generic may be applied to its own bare
        /// parameter — <c>Node&lt;T&gt;</code> as a member of <c>Node&lt;T : IComparable&lt;T&gt;&gt;</c>
        /// — because §6 promises every construction will satisfy the bound, and the bare parameter
        /// is what every construction hands its members. Used to read as a failed bounds check.
        /// </summary>
        [Fact]
        public void ASelfReferencingConstructionInsideItsOwnDeclarationCompiles()
        {
            var runtime = Run(
                "class Node<T : IComparable<T>> {\n"
                    + "  public let value: T;\n"
                    + "  public var next: Node<T>?;\n"
                    + "  constructor(value: T) { this.value = value; }\n"
                    + "}\n"
                    + "fun run(): int { let n = Node(7); n.next = Node(8); return n.next!!.value - 1; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>
        /// §6's bound widens in the body too: a <c>T</c> flows into an
        /// <c>IComparable&lt;T&gt;</c>-typed local, which is the whole point of writing the bound.
        /// </summary>
        [Fact]
        public void ATypeParameterWidensToItsBoundInABody()
        {
            var runtime = Run(
                "class Score : IComparable<Score> {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "  public fun compareTo(other: Score): int { return value - other.value; }\n"
                    + "}\n"
                    + "class Box<T : IComparable<T>> {\n"
                    + "  public let item: T;\n"
                    + "  constructor(item: T) { this.item = item; }\n"
                    + "  public fun beats(other: T): bool {\n"
                    + "    let comparable: IComparable<T> = this.item;\n"
                    + "    return comparable.compareTo(other) > 0;\n"
                    + "  }\n"
                    + "}\n"
                    + "fun run(): bool { let b = Box(Score(9)); return b.beats(Score(4)); }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        /// <summary>
        /// Passing the bare parameter as a type argument satisfies the callee's substituted bound:
        /// inside <c>Box&lt;T : IComparable&lt;T&gt;&gt;</c>, calling
        /// <c>biggest&lt;U : IComparable&lt;U&gt;&gt;(a, b)</c> infers <c>U = T</c>, and the check
        /// must read the promise, not reject it.
        /// </summary>
        [Fact]
        public void ABareTypeArgumentSatisfiesAnotherCallsSubstitutedBound()
        {
            var runtime = Run(
                "class Score : IComparable<Score> {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "  public fun compareTo(other: Score): int { return value - other.value; }\n"
                    + "}\n"
                    + "fun biggest<U : IComparable<U>>(a: U, b: U): U { return a.compareTo(b) >= 0 ? a : b; }\n"
                    + "class Box<T : IComparable<T>> {\n"
                    + "  public let item: T;\n"
                    + "  constructor(item: T) { this.item = item; }\n"
                    + "  public fun best(other: T): T { return biggest(this.item, other); }\n"
                    + "}\n"
                    + "fun run(): int { let b = Box(Score(3)); return b.best(Score(11)).value; }");

            Assert.Equal(11, Int(runtime, "run"));
        }

        /// <summary>
        /// Mutually referencing bounds (<c>&lt;T : U&gt;, &lt;U : T&gt;</c>) are a declaration
        /// error — the cycle promises nothing any construction can satisfy — reported at the
        /// declaration rather than as a failed check at every later use.
        /// </summary>
        [Fact]
        public void MutuallyReferencingBoundsAreReportedAtTheDeclaration()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class Left<T : U, U : T> {\n"
                    + "  public var pair: Left<T, U>?;\n"
                    + "}\n"
                    + "fun run(): int { return 1; }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CircularTypeParameterConstraint);
        }

        /// <summary>A parameter bounded by itself is the one-parameter shape of the same cycle.</summary>
        [Fact]
        public void ASelfBoundTypeParameterIsReportedAtTheDeclaration()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class Odd<T : T> { }\nfun run(): int { return 1; }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CircularTypeParameterConstraint);
        }

        /// <summary>
        /// Two parameters sharing a bound (<c>&lt;T : C, U : C&gt;</c>) is not a cycle — the shared
        /// bound must not be mistaken for one on the walk.
        /// </summary>
        [Fact]
        public void SharedBoundsAreNotMistakenForACycle()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "interface IC { fun n(): int; }\n"
                    + "class Pair<T : IC, U : IC> {\n"
                    + "  public var again: Pair<T, U>?;\n"
                    + "}\n"
                    + "fun run(): int { return 1; }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.False(compilation.HasErrors,
                "Unexpected: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        /// <summary>
        /// The widening is one-way: the bound still does not reach back into a `T`-typed slot, so
        /// an explicit cast out of the bound remains the only path down.
        /// </summary>
        [Fact]
        public void WideningToABoundIsImplicitButComingBackStaysExplicit()
        {
            using var compilation = Reject(
                "class Holder<T : IComparable<T>> {\n"
                    + "  public var item: T;\n"
                    + "  constructor(item: T) { this.item = item; }\n"
                    + "  public fun corrupt(c: IComparable<T>): void { this.item = c; }\n"
                    + "}");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CannotConvert);
        }

        #endregion

        #region Variance (§6)
        /// <summary>
        /// The motivating case: a collection of derived elements consumed where the element's base
        /// is expected. <c>IIterable</c> declares <c>out T</c>, so the array widens with its
        /// elements and the loop reads real values back — which is what makes this an end-to-end
        /// test and not just a type-checker one.
        /// </summary>
        [Fact]
        public void ACovariantIterableAcceptsADerivedElementsCollection()
        {
            var runtime = Run(
                "interface IShape { fun area(): float; }\n"
                    + "class Circle : IShape {\n"
                    + "  public let radius: float;\n"
                    + "  constructor(radius: float) { this.radius = radius; }\n"
                    + "  public fun area(): float { return 3.0 * radius * radius; }\n"
                    + "}\n"
                    + "fun total(shapes: IIterable<IShape>): float {\n"
                    + "  var sum = 0.0;\n"
                    + "  for (s in shapes) { sum = sum + s.area(); }\n"
                    + "  return sum;\n"
                    + "}\n"
                    + "fun run(): float {\n"
                    + "  let circles: Circle[] = [Circle(1.0), Circle(2.0)];\n"
                    + "  return total(circles);\n"
                    + "}");

            var result = Call(runtime, "run").AsFloat;
            Assert.Equal(3.0 * (1.0 + 4.0), result, 5);
        }

        /// <summary>
        /// A comparer of animals compares dogs: the argument widens <em>against</em> the
        /// annotation, the call runs for real, and the base-typed member reads a dog through it.
        /// </summary>
        [Fact]
        public void AContravariantComparerServesWhereADerivedOneIsAskedFor()
        {
            var runtime = Run(
                "class Animal {\n"
                    + "  public let rank: int;\n"
                    + "  constructor(rank: int) { this.rank = rank; }\n"
                    + "}\n"
                    + "class Dog : Animal {\n"
                    + "  constructor(rank: int) : super(rank) { }\n"
                    + "}\n"
                    + "class ByRank : IComparable<Animal> {\n"
                    + "  public fun compareTo(other: Animal): int { return other.rank; }\n"
                    + "}\n"
                    + "fun serve(c: IComparable<Dog>): int { return c.compareTo(Dog(41)); }\n"
                    + "fun run(): int { return serve(ByRank()); }");

            Assert.Equal(41, Int(runtime, "run"));
        }

        /// <summary>
        /// Method-group style: an animal handler serves where a dog handler is declared — that is
        /// what contravariance of inputs means. Before closure variance this was an error; now it
        /// compiles and calls back through the widened handler with a real dog.
        /// </summary>
        [Fact]
        public void AContravariantClosureAcceptsAWiderHandler()
        {
            var runtime = Run(
                "class Animal { public fun name(): string { return \"animal\"; } }\n"
                    + "class Dog : Animal { }\n"
                    + "fun speak(handler: (Dog) -> string): string { return handler(Dog()); }\n"
                    + "fun run(): string { let describe = (a: Animal) => a.name(); return speak(describe); }");

            Assert.Equal("animal", Text(runtime, "run"));
        }

        /// <summary>The invariance default still holds for mutable collections.</summary>
        [Fact]
        public void AnUnannotatedCollectionStaysInvariant()
        {
            // Arrays are invariant because they are writable: feeding dogs where animals are
            // stored would let someone push a cat into a dog array (§3.1). IIterable widens
            // because it only reads; the array itself never does.
            using var compilation = Reject(
                "class Animal { }\n"
                    + "class Dog : Animal { }\n"
                    + "fun feed(meal: Animal[]): void { }\n"
                    + "fun run(): void { let dogs: Dog[] = [Dog()]; feed(dogs); }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnresolvedCall);
        }

        /// <summary><c>out T</c> in a parameter position is refused at the declaration itself.</summary>
        [Fact]
        public void ACovariantParameterInAnInputPositionIsRefusedAtTheDeclaration()
        {
            using var compilation = Reject(
                "interface Sink<out T> {\n"
                    + "  fun write(item: T): void;\n"
                    + "}\n"
                    + "fun run(): void { }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.VariantParameterUsedAsInput);
        }

        /// <summary>And symmetrically, <c>in T</c> in a return position.</summary>
        [Fact]
        public void AContravariantParameterInAnOutputPositionIsRefusedAtTheDeclaration()
        {
            using var compilation = Reject(
                "interface Source<in T> {\n"
                    + "  fun next(): T;\n"
                    + "}\n"
                    + "fun run(): void { }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.VariantParameterUsedAsOutput);
        }

        /// <summary>
        /// A field reads and writes, so it forces its own declaration invariant — reported once,
        /// at the annotation, not as a mysterious subtype failure somewhere else.
        /// </summary>
        [Fact]
        public void ACovariantParameterOverAFieldIsRefused()
        {
            using var compilation = Reject(
                "class Cell<out T> {\n"
                    + "  public var item: T;\n"
                    + "}\n"
                    + "fun run(): void { }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.VariantParameterUsedAsInput);
        }

        /// <summary>Variance belongs to declarations; a method's parameters cannot carry it.</summary>
        [Fact]
        public void AVarianceModifierOnAMethodTypeParameterIsRefused()
        {
            using var compilation = Reject(
                "fun first<out T>(items: T[]): T { return items[0]; }\n"
                    + "fun run(): int { return first([1, 2]); }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidVarianceModifier);
        }

        /// <summary>
        /// An alias is transparent — it *is* its target — so there is no family of constructions
        /// for an annotation to relate, and writing one is refused rather than silently ignored.
        /// </summary>
        [Fact]
        public void AVarianceModifierOnAnAliasTypeParameterIsRefused()
        {
            using var compilation = Reject(
                "alias P<out T> = IIterable<T>;\n"
                    + "fun run(): void { }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidVarianceModifier);
        }
        #endregion

        #region Module-level natives (§10)
        /// <summary>
        /// §10: a module naming a host global nobody registered fails to load, rather than reading a
        /// zero out of storage of its own.
        /// </summary>
        [Fact]
        public void AModuleNamingAnUnregisteredNativeVariableFailsToLoad()
        {
            var emitter = Build("native let ScreenWidth: int;\nfun run(): int { return ScreenWidth; }");

            using var runtime = new SurtrRuntime();
            Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(emitter.Modules[0]));
        }

        [Fact]
        public void AModuleNamingAnUnregisteredNativeFunctionFailsToLoad()
        {
            var emitter = Build("native fun hostLog(message: string): void;\nfun run(): int { hostLog(\"hi\"); return 1; }");

            using var runtime = new SurtrRuntime();
            Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(emitter.Modules[0]));
        }

        [Fact]
        public unsafe void ANativeVariableReadsTheHostsOwnStorage()
        {
            // A module-level `native let` is a native property with only a getter (§10); the host
            // publishes that getter's body by its link name, `get_<name>` prefixed with the module
            // path - the same convention a native class accessor uses, minus the type.
            var emitter = Build("native let ScreenWidth: int;\nfun run(): int { return ScreenWidth; }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            runtime.DefineNativeBody("game.core.Test.get_ScreenWidth", SurtrNativeEntryPoint.FromFunctionPointer(&GetScreenWidth));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(1280, Int(runtime, "run"));
        }

        private static int GetScreenWidth(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateInt(1280));

        [Fact]
        public unsafe void AWriteToANativeVariableLandsInTheHostsOwnStorage()
        {
            // A module-level `native var` gets both accessors (§10); both need a body registered
            // before load even though `run` only calls the setter here - `BindNativeBodies` binds
            // every native member the module declares, not only the ones a given caller reaches.
            var emitter = Build("native var TimeScale: float;\nfun run(): int { TimeScale = 0.5; return 1; }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            _writtenTimeScale = null;
            runtime.DefineNativeBody("game.core.Test.get_TimeScale", SurtrNativeEntryPoint.FromFunctionPointer(&GetTimeScale));
            runtime.DefineNativeBody("game.core.Test.set_TimeScale", SurtrNativeEntryPoint.FromFunctionPointer(&SetTimeScale));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(1, Int(runtime, "run"));
            Assert.Equal(0.5, _writtenTimeScale);
        }

        // A plain static field, not a closure capture: SurtrNativeEntryPoint.FromFunctionPointer
        // needs a static method with no captured state.
        private static double? _writtenTimeScale;
        private static int GetTimeScale(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateFloat(_writtenTimeScale ?? 0.0));
        private static int SetTimeScale(SurtrCallArguments arguments)
        {
            _writtenTimeScale = arguments.GetFloat(0);
            return arguments.Return(SurtrValue.Null);
        }

        [Fact]
        public unsafe void ANativeFunctionCallReachesTheHostsBody()
        {
            var emitter = Build("native fun hostSquare(value: int): int;\nfun run(): int { return hostSquare(3); }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            runtime.DefineNativeBody("game.core.Test.hostSquare", SurtrNativeEntryPoint.FromFunctionPointer(&Square));

            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(9, Int(runtime, "run"));
        }

        // A module-level native takes no receiver, so its first declared parameter is argument zero.
        private static int Square(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(arguments.GetInt(0) * arguments.GetInt(0)));

        [Fact]
        public void ANativeVariableCannotHaveAnInitializer()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", "native let ScreenWidth: int = 5;");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidNativeDeclaration);
        }

        /// <summary>
        /// A module-level native travels as <c>&lt;modulePath&gt;.&lt;name&gt;</c> (§10), so two
        /// modules declaring a same-named <c>native fun</c> bind against distinct link names
        /// instead of silently sharing whatever single body was registered under the bare name.
        /// </summary>
        [Fact]
        public unsafe void TwoModulesSameNamedNativesBindDistinctBodies()
        {
            var emitter = Build(
                "native fun load(): int;\nfun run(): int { return load(); }",
                ("/other/util/Test.surtr", "native fun load(): int;\nfun run(): int { return load(); }"));

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            runtime.DefineNativeBody("game.core.Test.load", SurtrNativeEntryPoint.FromFunctionPointer(&FirstLoad));
            runtime.DefineNativeBody("other.util.Test.load", SurtrNativeEntryPoint.FromFunctionPointer(&SecondLoad));

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            Assert.Equal(1, Int(runtime, "run"));

            Assert.True(runtime.TryGetModule("other.util.Test", out var other));
            Assert.True(other.TryGetMethods("run", out var runOverloads));
            Assert.Equal(2, runtime.Invoke(runOverloads[0]).AsInt);
        }

        private static int FirstLoad(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateInt(1));

        private static int SecondLoad(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateInt(2));
        #endregion

        #region Class-level natives (§10)
        //
        // A `native` member inside a class binds by link name exactly like a module-level one does
        // (§10): `moduleName:ClassName.memberName`, derived by ModuleEmitter.LinkName from the
        // owning type's FullMetadataName plus the accessor's own name - no signature, since the
        // compiler always supplies an explicit link name and never falls back to deriving one.

        [Fact]
        public unsafe void ANativeInstanceMethodInsideAClassReachesTheHostsBody()
        {
            var emitter = Build(
                "class Sprite {\n"
                    + "  public native fun doubled(x: int): int;\n"
                    + "}\n"
                    + "fun run(): int { return Sprite().doubled(21); }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            // Argument 0 is the receiver for an instance native member; the declared parameter
            // follows it.
            runtime.DefineNativeBody("game.core.Test:Sprite.doubled", SurtrNativeEntryPoint.FromFunctionPointer(&DoubleSecondArgument));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(42, Int(runtime, "run"));
        }

        private static int DoubleSecondArgument(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateInt(arguments.GetInt(1) * 2));

        [Fact]
        public unsafe void AStaticNativeMethodInsideAClassReachesTheHostsBody()
        {
            var emitter = Build(
                "class MathHost {\n"
                    + "  public static native fun triple(x: int): int;\n"
                    + "}\n"
                    + "fun run(): int { return MathHost.triple(7); }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            // No receiver for a static member, so the declared parameter is argument 0.
            runtime.DefineNativeBody("game.core.Test:MathHost.triple", SurtrNativeEntryPoint.FromFunctionPointer(&TripleFirstArgument));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(21, Int(runtime, "run"));
        }

        private static int TripleFirstArgument(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateInt(arguments.GetInt(0) * 3));

        /// <summary>
        /// A native property written the explicit `{ get; set; }` way, compiled through the real
        /// front end rather than built directly with <c>SurtrModuleBuilder</c> - the class-level
        /// mechanism a `native let`/`native var` (below) is sugar for.
        /// </summary>
        [Fact]
        public unsafe void ANativeInstancePropertyInsideAClassReachesTheHostsBody()
        {
            var emitter = Build(
                "class Box {\n"
                    + "  public native value: int { get; set; }\n"
                    + "}\n"
                    + "fun run(): int { let b = Box(); b.value = 5; return b.value; }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            _boxValue = 0;
            runtime.DefineNativeBody("game.core.Test:Box.get_value", SurtrNativeEntryPoint.FromFunctionPointer(&GetBoxValue));
            runtime.DefineNativeBody("game.core.Test:Box.set_value", SurtrNativeEntryPoint.FromFunctionPointer(&SetBoxValue));
            runtime.LoadModule(emitter.Modules[0]);

            // +1000 on the read is deliberate: a write-then-read through an *ordinary* auto-property
            // (a real backing field, the exact shape this used to be silently downgraded to before
            // the fix - ModuleEmitter.DeclareProperty never checked IsNative) would echo back plain
            // 5, passing even though no host code ever ran. Only a genuine call into GetBoxValue can
            // produce 1005.
            Assert.Equal(1005, Int(runtime, "run"));
        }

        private static int _boxValue;
        private static int GetBoxValue(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateInt(_boxValue + 1000));
        private static int SetBoxValue(SurtrCallArguments arguments)
        {
            _boxValue = arguments.GetInt(1);
            return arguments.Return(SurtrValue.Null);
        }

        /// <summary>
        /// The fix: a `native let` inside a class used to silently bind as an ordinary field with
        /// real storage (<c>Binder.BindField</c> never read <c>syntax.IsNative</c>), so this used to
        /// read back <c>0</c> with no error and no need to register a host body at all.
        /// </summary>
        [Fact]
        public unsafe void ANativeLetInsideAClassBindsAsANativeGetterOnlyProperty()
        {
            var emitter = Build(
                "class Foo {\n"
                    + "  public native let x: int;\n"
                    + "  public fun run(): int { return this.x; }\n"
                    + "}\n"
                    + "fun run(): int { return Foo().run(); }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            runtime.DefineNativeBody("game.core.Test:Foo.get_x", SurtrNativeEntryPoint.FromFunctionPointer(&GetNinetyNine));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(99, Int(runtime, "run"));
        }

        private static int GetNinetyNine(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateInt(99));

        /// <summary>The read-write twin: a `native var` inside a class gets both accessors.</summary>
        [Fact]
        public unsafe void ANativeVarInsideAClassBindsAsANativeReadWriteProperty()
        {
            var emitter = Build(
                "class Foo {\n"
                    + "  public native var x: int;\n"
                    + "  public fun run(): int { this.x = 7; return this.x; }\n"
                    + "}\n"
                    + "fun run(): int { return Foo().run(); }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            _fooX = 0;
            runtime.DefineNativeBody("game.core.Test:Foo.get_x", SurtrNativeEntryPoint.FromFunctionPointer(&GetFooX));
            runtime.DefineNativeBody("game.core.Test:Foo.set_x", SurtrNativeEntryPoint.FromFunctionPointer(&SetFooX));
            runtime.LoadModule(emitter.Modules[0]);

            // +2000, for the same reason ANativeInstancePropertyInsideAClassReachesTheHostsBody
            // offsets its read: an ordinary auto-property's backing field would echo back plain 7.
            Assert.Equal(2007, Int(runtime, "run"));
        }

        private static int _fooX;
        private static int GetFooX(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateInt(_fooX + 2000));
        private static int SetFooX(SurtrCallArguments arguments)
        {
            _fooX = arguments.GetInt(1);
            return arguments.Return(SurtrValue.Null);
        }

        /// <summary>A `native let`/`native var` inside a class can be static too, same as an
        /// explicit native property can.</summary>
        [Fact]
        public unsafe void AStaticNativeLetInsideAClassIsStatic()
        {
            var emitter = Build(
                "class Config {\n"
                    + "  public static native let x: int;\n"
                    + "}\n"
                    + "fun run(): int { return Config.x; }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            // No receiver: a static native accessor's argument list is empty here.
            runtime.DefineNativeBody("game.core.Test:Config.get_x", SurtrNativeEntryPoint.FromFunctionPointer(&GetFiftyFive));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(55, Int(runtime, "run"));
        }

        private static int GetFiftyFive(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateInt(55));

        [Fact]
        public void ANativeLetInsideAClassCannotHaveAnInitializer()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", "class Foo {\n  public native let x: int = 5;\n}\n");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidNativeDeclaration);
        }

        [Fact]
        public unsafe void ANativeMethodOnAValueClassReachesTheHostsBody()
        {
            var emitter = Build(
                "value class EntityId {\n"
                    + "  public let raw: int;\n"
                    + "  public constructor(raw: int) { this.raw = raw; }\n"
                    + "  public native fun validate(): bool;\n"
                    + "}\n"
                    + "fun run(): int { return EntityId(5).validate() ? 1 : 0; }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            runtime.DefineNativeBody("game.core.Test:EntityId.validate", SurtrNativeEntryPoint.FromFunctionPointer(&AlwaysTrue));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(1, Int(runtime, "run"));
        }

        private static int AlwaysTrue(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateBool(true));

        [Fact]
        public unsafe void ANativeMethodOnAnEnumReachesTheHostsBody()
        {
            var emitter = Build(
                "enum Suit {\n"
                    + "  Hearts, Spades;\n"
                    + "  public native fun describe(): int;\n"
                    + "}\n"
                    + "fun run(): int { return Suit.Hearts.describe(); }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            runtime.DefineNativeBody("game.core.Test:Suit.describe", SurtrNativeEntryPoint.FromFunctionPointer(&GetSeven));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(7, Int(runtime, "run"));
        }

        private static int GetSeven(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateInt(7));

        [Fact]
        public unsafe void ANativeMethodOnANestedClassReachesTheHostsBody()
        {
            var emitter = Build(
                "class Outer {\n"
                    + "  public class Inner {\n"
                    + "    public native fun ping(): int;\n"
                    + "  }\n"
                    + "}\n"
                    + "fun run(): int { return Outer.Inner().ping(); }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            // A nested type's FullMetadataName chains with '.', same as its display name: §2.6.
            runtime.DefineNativeBody("game.core.Test:Outer.Inner.ping", SurtrNativeEntryPoint.FromFunctionPointer(&GetThree));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(3, Int(runtime, "run"));
        }

        private static int GetThree(SurtrCallArguments arguments) => arguments.Return(SurtrValue.CreateInt(3));

        /// <summary>§10 for a class member, mirroring <see cref="AModuleNamingAnUnregisteredNativeFunctionFailsToLoad"/>.</summary>
        [Fact]
        public void AClassNamingAnUnregisteredNativeMemberFailsToLoad()
        {
            var emitter = Build(
                "class Foo {\n"
                    + "  public native fun bar(): int;\n"
                    + "}\n"
                    + "fun run(): int { return Foo().bar(); }");

            using var runtime = new SurtrRuntime();
            Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(emitter.Modules[0]));
        }
        #endregion

        #region Built-in default constructors

        /// <summary>
        /// Before the fix, `int()`/`float()`/`bool()`/`char()`/`string()` compiled and ran anyway
        /// (declaring no constructors and taking no arguments satisfied constructor resolution),
        /// silently reading back the entity reference `ObjNew` allocated as raw NaN-boxed bits
        /// instead of the type's own default value.
        /// </summary>
        [Fact]
        public void ParameterlessPrimitiveAndStringConstructorsAreDefaults()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let a: int = int();\n"
                    + "  let b: float = float();\n"
                    + "  let c: bool = bool();\n"
                    + "  let d: char = char();\n"
                    + "  let e: string = string();\n"
                    + "  var acc = a;\n"
                    + "  if (b == 0.0) { acc = acc + 10; }\n"
                    + "  if (!c) { acc = acc + 100; }\n"
                    + "  if (d == char()) { acc = acc + 1000; }\n"
                    + "  if (e == \"\") { acc = acc + 10000; }\n"
                    + "  return acc;\n"
                    + "}");

            Assert.Equal(11110, Int(runtime, "run"));
        }

        [Fact]
        public void AParameterlessRangeIsEmpty()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  var count = 0;\n"
                    + "  for (i in range()) { count = count + 1; }\n"
                    + "  return count;\n"
                    + "}");

            Assert.Equal(0, Int(runtime, "run"));
        }

        [Fact]
        public void AParameterlessVoidConstructionIsRejected()
        {
            using var compilation = Reject("fun run(): int { void(); return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.NotSupportedOnType);
        }

        [Fact]
        public void AParameterlessUnknownConstructionIsRejected()
        {
            using var compilation = Reject("fun run(): int { let u = unknown(); return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.NotSupportedOnType);
        }

        #endregion

        #region Built-in member opcode substitution — value correctness for LoweringChoiceTests' shape assertions

        [Fact]
        public void ArrayOperationsComputeCorrectlyThroughTheirOpcodes()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let xs: int[] = [1, 2, 3];\n"
                    + "  xs.push(9);\n"
                    + "  xs.set(0, xs.get(0) + 100);\n"
                    + "  xs.insert(1, 5);\n"
                    + "  xs.removeAt(2);\n"
                    + "  let found = xs.indexOf(9);\n"
                    + "  let has = xs.contains(5);\n"
                    + "  let last = xs.pop();\n"
                    + "  let g0 = xs.get(0);\n"
                    + "  let g1 = xs.get(1);\n"
                    + "  let lengthBeforeClear = xs.length;\n"
                    + "  xs.clear();\n"
                    + "  return g0 + g1 + found * 10 + (has ? 1 : 0) + last + lengthBeforeClear * 1000 + xs.length * 100000;\n"
                    + "}");

            // xs starts [1,2,3] -> push(9): [1,2,3,9] -> set(0, get(0)+100): [101,2,3,9] ->
            // insert(1,5): [101,5,2,3,9] -> removeAt(2): [101,5,3,9] -> indexOf(9)=3 ->
            // contains(5)=true -> pop()=9, xs=[101,5,3] -> g0=101, g1=5 -> lengthBeforeClear=3 ->
            // clear(): xs=[].
            Assert.Equal(101 + 5 + 3 * 10 + 1 + 9 + 3 * 1000 + 0, Int(runtime, "run"));
        }

        [Fact]
        public void StringLengthAndCharAtComputeCorrectly()
        {
            var runtime = Run("fun run(): bool { let s = \"hello\"; return s.length == 5 && s.charAt(0) == 'h'; }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        [Fact]
        public void TupleLengthComputesCorrectly()
        {
            var runtime = Run("fun run(): int { let t = (1, \"a\", true); return t.length; }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        [Fact]
        public void DictGetAndSetComputeCorrectly()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let m: {string: int} = {\"x\": 1};\n"
                    + "  m.set(\"x\", m.get(\"x\") + 41);\n"
                    + "  return m.get(\"x\");\n"
                    + "}");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void DictReserveDoesNotDisturbExistingEntries()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let m: {string: int} = {};\n"
                    + "  m.reserve(64);\n"
                    + "  m.set(\"a\", 21);\n"
                    + "  m.reserve(128);\n"
                    + "  return m.get(\"a\") * 2;\n"
                    + "}");

            Assert.Equal(42, Int(runtime, "run"));
        }

        #endregion

        #region Nameable collection constructors (§5.3.1)

        [Fact]
        public void ArrayEmptyConstructorIsEmpty()
        {
            var runtime = Run("fun run(): int { let xs = array<int>(); return xs.length; }");
            Assert.Equal(0, Int(runtime, "run"));
        }

        [Fact]
        public void ArrayCapacityConstructorZeroFillsToTheGivenLength()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let xs = array<int>(5);\n"
                    + "  return xs.length * 1000 + xs.get(0) + xs.get(4);\n"
                    + "}");

            // Every element starts at int's zero, so both the first and the last read back 0.
            Assert.Equal(5000, Int(runtime, "run"));
        }

        [Fact]
        public void ArrayCapacityConstructorWorksWithARuntimeSizeToo()
        {
            // Not a written constant, so this exercises the runtime ArrNew form rather than ArrNewX.
            var runtime = Run("fun run(n: int): int { let xs = array<int>(n); return xs.length; }");
            Assert.Equal(7, Int(runtime, "run", SurtrValue.CreateInt(7)));
        }

        [Fact]
        public void DictEmptyConstructorIsEmptyAndStillUsable()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let m = dict<string, int>();\n"
                    + "  let before = m.length;\n"
                    + "  m.set(\"x\", 7);\n"
                    + "  return before * 1000 + m.get(\"x\");\n"
                    + "}");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void DictCapacityConstructorStaysEmptyUntilSomethingIsSet()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let m = dict<string, int>(32);\n"
                    + "  let before = m.length;\n"
                    + "  m.set(\"x\", 5);\n"
                    + "  return before * 1000 + m.get(\"x\");\n"
                    + "}");

            // before == 0: capacity is a hint, not a length, exactly like array.reserve/dict.reserve.
            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void ArrayFromTupleCastReadsEveryElementInOrder()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let a = array<int>((10, 20, 30));\n"
                    + "  return a.length * 1000 + a.get(0) + a.get(1) + a.get(2);\n"
                    + "}");

            Assert.Equal(3000 + 60, Int(runtime, "run"));
        }

        [Fact]
        public void ArrayFromTupleCastWidensElementsImplicitly()
        {
            var runtime = Run(
                "fun run(): float {\n"
                    + "  let a = array<float>((1, 2, 3));\n"
                    + "  return a.get(0) + a.get(1) + a.get(2);\n"
                    + "}");

            Assert.Equal(6.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void TupleFromArrayCastReadsEveryElementIntoItsSlot()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let xs: int[] = [10, 20, 30];\n"
                    + "  let t = tuple<int, int, int>(xs);\n"
                    + "  return t[0] + t[1] + t[2];\n"
                    + "}");

            Assert.Equal(60, Int(runtime, "run"));
        }

        [Fact]
        public void TupleFromArrayArityMismatchThrowsInvalidCastException()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let xs: int[] = [1, 2];\n"
                    + "  try { let t = tuple<int, int, int>(xs); return 0; }\n"
                    + "  catch (e: InvalidCastException) { return 1; }\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void TheUnitTupleConstructsWithNoElements()
        {
            var runtime = Run("fun run(): int { let u = tuple<>(); return u.length; }");
            Assert.Equal(0, Int(runtime, "run"));
        }

        /// <summary>
        /// <c>array&lt;int&gt;</c> and <c>int[]</c> aren't just convertible — they're the same type,
        /// so a value built through one name behaves exactly as one declared through the other.
        /// </summary>
        [Fact]
        public void ArrayGenericFormAndSymbolicFormAreTheSameTypeAtRuntimeToo()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let a: array<int> = [1, 2, 3];\n"
                    + "  a.push(4);\n"
                    + "  return a.length;\n"
                    + "}");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void ANoArgConstructionOfANonEmptyTupleIsRejected()
        {
            using var compilation = Reject("fun run(): int { let t = tuple<int, string>(); return 0; }");
            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.TupleArityFixed);
        }

        [Fact]
        public void ACapacityConstructionOfATupleIsRejected()
        {
            using var compilation = Reject("fun run(): int { let t = tuple<int, string>(5); return 0; }");
            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.TupleArityFixed);
        }

        [Fact]
        public void CastingIntoADictIsRejected()
        {
            using var compilation = Reject(
                "fun run(): int { let m = dict<int, string>((1, \"a\")); return 0; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CollectionCastNotSupported);
        }

        [Fact]
        public void AnArrayCastFromATupleWithNoConversionToTheElementTypeIsRejected()
        {
            using var compilation = Reject(
                "fun run(): int { let a = array<int>((\"x\", \"y\")); return 0; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CollectionElementConversionMissing);
        }

        #endregion

        #region Nameable primitive/string/range constructors (§5.3.2)

        [Fact]
        public void APrimitiveConstructorConvertsBetweenPrimitives()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let a = int(3.9);\n"
                    + "  let b = int(-3.9);\n"
                    + "  let c = int(true);\n"
                    + "  let d = int('A');\n"
                    + "  return a * 1000 + b * 100 + c * 10 + d;\n"
                    + "}");

            // 3*1000 + (-3)*100 + 1*10 + 65 = 3000 - 300 + 10 + 65 = 2775
            Assert.Equal(2775, Int(runtime, "run"));
        }

        [Fact]
        public void FloatToIntTruncatesTowardZeroSaturatesAndReadsNaNAsZero()
        {
            var runtime = Run(
                "fun truncPos(): int { return int(2.9); }\n"
                    + "fun truncNeg(): int { return int(-2.9); }\n"
                    + "fun tooBig(): int { return int(1e300); }\n"
                    + "fun tooSmall(): int { return int(-1e300); }\n"
                    + "fun notANumber(): int { return int(0.0 / 0.0); }\n");

            Assert.Equal(2, Int(runtime, "truncPos"));
            Assert.Equal(-2, Int(runtime, "truncNeg"));
            Assert.Equal(int.MaxValue, Int(runtime, "tooBig"));
            Assert.Equal(int.MinValue, Int(runtime, "tooSmall"));
            Assert.Equal(0, Int(runtime, "notANumber"));
        }

        [Fact]
        public void IntParsesFromAValidString()
        {
            var runtime = Run("fun run(): int { return int(\"123\") + int(\"-7\"); }");
            Assert.Equal(116, Int(runtime, "run"));
        }

        [Fact]
        public void IntConstructorThrowsFormatExceptionOnBadText()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  try { let x = int(\"abc\"); return 0; }\n"
                    + "  catch (e: FormatException) { return 1; }\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void IntParsesWithARadix()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let hex = int(\"ff\", 16);\n"
                    + "  let bin = int(\"1010\", 2);\n"
                    + "  let neg = int(\"-z\", 36);\n"
                    + "  return hex * 1000000 + bin * 1000 + neg;\n"
                    + "}");

            Assert.Equal(255 * 1000000 + 10 * 1000 - 35, Int(runtime, "run"));
        }

        [Fact]
        public void IntRadixConstructorThrowsFormatExceptionOnAnInvalidDigit()
        {
            // '2' is not a valid base-2 digit.
            var runtime = Run(
                "fun run(): int {\n"
                    + "  try { let x = int(\"102\", 2); return 0; }\n"
                    + "  catch (e: FormatException) { return 1; }\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void IntRadixConstructorThrowsArgumentExceptionOnABadRadix()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  try { let x = int(\"5\", 37); return 0; }\n"
                    + "  catch (e: ArgumentException) { return 1; }\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void FloatParsesFromAValidStringAndThrowsOnAnInvalidOne()
        {
            var runtime = Run(
                "fun run(): float { return float(\"3.5\"); }\n"
                    + "fun bad(): int {\n"
                    + "  try { let x = float(\"nope\"); return 0; }\n"
                    + "  catch (e: FormatException) { return 1; }\n"
                    + "}");

            Assert.Equal(3.5, Call(runtime, "run").AsFloat);
            Assert.Equal(1, Int(runtime, "bad"));
        }

        [Fact]
        public void BoolParsesCaseInsensitivelyAndThrowsOnAnInvalidString()
        {
            var runtime = Run(
                "fun run(): bool { return bool(\"TRUE\") && bool(\"1\") && !bool(\"false\") && !bool(\"0\"); }\n"
                    + "fun bad(): int {\n"
                    + "  try { let x = bool(\"maybe\"); return 0; }\n"
                    + "  catch (e: FormatException) { return 1; }\n"
                    + "}");

            Assert.True(Call(runtime, "run").AsBool);
            Assert.Equal(1, Int(runtime, "bad"));
        }

        [Fact]
        public void CharTakesTheFirstCharacterAndThrowsOnAnEmptyString()
        {
            var runtime = Run(
                "fun run(): char { return char(\"hi\"); }\n"
                    + "fun bad(): int {\n"
                    + "  try { let x = char(\"\"); return 0; }\n"
                    + "  catch (e: FormatException) { return 1; }\n"
                    + "}");

            Assert.Equal('h', (char)Call(runtime, "run").AsChar);
            Assert.Equal(1, Int(runtime, "bad"));
        }

        [Fact]
        public void StringConstructorsComposeFromEveryScalar()
        {
            var runtime = Run(
                "fun run(): string {\n"
                    + "  return string(42) + \"|\" + string(3.5) + \"|\" + string(true) + \"|\" + string('x') + \"|\" + string(0..10) + \"|\" + string('*', 3) + \"|\" + string(['h', 'i']) + \"|\" + string(['h', 'e', 'l', 'l', 'o'], 1, 3);\n"
                    + "}");

            Assert.Equal("42|3.5|true|x|0..10|***|hi|ell", Text(runtime, "run"));
        }

        [Fact]
        public void StringSliceConstructorThrowsIndexOutOfRangeOnABadOffsetOrLength()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let chars = ['a', 'b', 'c'];\n"
                    + "  try { let x = string(chars, 2, 5); return 0; }\n"
                    + "  catch (e: IndexOutOfRangeException) { return 1; }\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void RangeConstructorsMatchTheEquivalentOperators()
        {
            var runtime = Run(
                "fun exclusive(): int { let r = range(1, 5); return r.length; }\n"
                    + "fun inclusiveConstant(): int { let r = range(1, 5, true); return r.length; }\n"
                    + "fun exclusiveConstant(): int { let r = range(1, 5, false); return r.length; }\n"
                    + "fun runtimeFlag(flag: bool): int { let r = range(1, 5, flag); return r.length; }\n");

            Assert.Equal(4, Int(runtime, "exclusive"));
            Assert.Equal(5, Int(runtime, "inclusiveConstant"));
            Assert.Equal(4, Int(runtime, "exclusiveConstant"));
            Assert.Equal(5, Int(runtime, "runtimeFlag", SurtrValue.CreateBool(true)));
            Assert.Equal(4, Int(runtime, "runtimeFlag", SurtrValue.CreateBool(false)));
        }

        [Fact]
        public void APrimitiveConstructorWithNoMatchingArgumentsIsRejected()
        {
            using var compilation = Reject("fun run(): int { let x = int(true, false); return 0; }");
            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.NoBuiltInConstructorMatch);
        }

        #endregion

        #region Nameable array/dict shapes with a runtime length (§5.3.3)

        [Fact]
        public void ArraySizeDefaultConstructorFillsEveryElement()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let a = array<int>(5, -1);\n"
                    + "  var sum = 0;\n"
                    + "  for (x in a) sum += x;\n"
                    + "  return a.length * 1000 + sum;\n"
                    + "}");

            Assert.Equal(5000 - 5, Int(runtime, "run"));
        }

        [Fact]
        public void ArrayCopyConstructorIsAGenuineIndependentCopy()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let src: int[] = [1, 2, 3];\n"
                    + "  let copy = array<int>(src);\n"
                    + "  copy.push(99);\n"
                    + "  return copy.length * 1000 + copy.get(0) + src.length;\n"
                    + "}");

            // src stays length 3 after mutating copy — proof they don't alias the same buffer.
            Assert.Equal(4000 + 1 + 3, Int(runtime, "run"));
        }

        [Fact]
        public void ArrayCopyConstructorWidensElementsImplicitly()
        {
            var runtime = Run(
                "fun run(): float {\n"
                    + "  let src: int[] = [1, 2, 3];\n"
                    + "  let copy = array<float>(src);\n"
                    + "  return copy.get(0) + copy.get(1) + copy.get(2);\n"
                    + "}");

            Assert.Equal(6.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void ArrayFromIterableConstructorWalksARange()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let a = array<int>(0..5);\n"
                    + "  var sum = 0;\n"
                    + "  for (x in a) sum += x;\n"
                    + "  return a.length * 1000 + sum;\n"
                    + "}");

            Assert.Equal(5000 + 10, Int(runtime, "run"));
        }

        [Fact]
        public void DictFromPairsConstructorBuildsEveryEntry()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let pairs = [(\"a\", 1), (\"b\", 2), (\"c\", 3)];\n"
                    + "  let d = dict<string, int>(pairs);\n"
                    + "  return d.length * 1000 + d.get(\"b\");\n"
                    + "}");

            Assert.Equal(3002, Int(runtime, "run"));
        }

        [Fact]
        public void DictFromParallelArraysConstructorBuildsEveryEntry()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let keys: string[] = [\"x\", \"y\", \"z\"];\n"
                    + "  let values: int[] = [10, 20, 30];\n"
                    + "  let d = dict<string, int>(keys, values);\n"
                    + "  return d.length * 1000 + d.get(\"y\");\n"
                    + "}");

            Assert.Equal(3020, Int(runtime, "run"));
        }

        [Fact]
        public void DictFromParallelArraysThrowsArgumentExceptionOnMismatchedLengths()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let keys: string[] = [\"x\", \"y\"];\n"
                    + "  let values: int[] = [10, 20, 30];\n"
                    + "  try { let d = dict<string, int>(keys, values); return 0; }\n"
                    + "  catch (e: ArgumentException) { return 1; }\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void TupleExplicitPositionalConstructorMatchesTheLiteral()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let t = tuple<int, int, int>(10, 20, 30);\n"
                    + "  return t[0] + t[1] + t[2];\n"
                    + "}");

            Assert.Equal(60, Int(runtime, "run"));
        }

        #endregion

        #region Extension methods (§15) — Fase 1: instance methods, same module, non-generic
        private const string Vec2 =
            "class Vec2 {\n"
                + "  public let x: float;\n"
                + "  public let y: float;\n"
                + "  public constructor(x: float, y: float) { this.x = x; this.y = y; }\n"
                + "}\n";

        [Fact]
        public void AnExtensionMethodIsCallableOnItsTargetType()
        {
            var runtime = Run(
                Vec2
                    + "extension Vec2 { fun lengthSquared(v: Vec2): float => v.x * v.x + v.y * v.y; }\n"
                    + "fun run(): float { return Vec2(3.0, 4.0).lengthSquared(); }");

            Assert.Equal(25.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AnExtensionMethodsExtraArgumentsFollowTheReceiver()
        {
            var runtime = Run(
                Vec2
                    + "extension Vec2 { fun scaled(v: Vec2, factor: float): float => (v.x + v.y) * factor; }\n"
                    + "fun run(): float { return Vec2(1.0, 2.0).scaled(2.0); }");

            Assert.Equal(6.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AnExtensionMethodOnANonReceiverExpressionEvaluatesTheReceiverOnce()
        {
            // The receiver here is a call with a side effect (a module counter) - if `CompleteExtension`
            // ever re-bound it from syntax instead of reusing the already-bound expression, this would
            // observe 2.
            var runtime = Run(
                Vec2
                    + "var calls: int = 0;\n"
                    + "fun makeVec(): Vec2 { calls += 1; return Vec2(1.0, 1.0); }\n"
                    + "extension Vec2 { fun lengthSquared(v: Vec2): float => v.x * v.x + v.y * v.y; }\n"
                    + "fun run(): int { let n = makeVec().lengthSquared(); calls += 0; return calls; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ARealMemberWinsSilentlyOverAnExtensionWithTheSameName()
        {
            var runtime = Run(
                "class Vec2 {\n"
                    + "  public let x: float;\n"
                    + "  public constructor(x: float) { this.x = x; }\n"
                    + "  public fun describe(): float => x;\n"
                    + "}\n"
                    + "extension Vec2 { fun describe(v: Vec2): float => -1.0; }\n"
                    + "fun run(): float { return Vec2(9.0).describe(); }");

            Assert.Equal(9.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void TwoExtensionMethodsOnDifferentTargetTypesResolveIndependently()
        {
            var runtime = Run(
                Vec2
                    + "extension Vec2 { fun describe(v: Vec2): string => \"vec2\"; }\n"
                    + "extension int { fun describe(n: int): string => \"int\"; }\n"
                    + "fun runVec(): string { return Vec2(1.0, 2.0).describe(); }\n"
                    + "fun runInt(): string { let n = 5; return n.describe(); }");

            Assert.Equal("vec2", Text(runtime, "runVec"));
            Assert.Equal("int", Text(runtime, "runInt"));
        }

        [Fact]
        public void AnExtensionNestedInAClassIsReachableFromThatClasssOwnMembers()
        {
            var runtime = Run(
                Vec2
                    + "class Registry {\n"
                    + "  public constructor() { }\n"
                    + "  private extension Vec2 { fun secret(v: Vec2): float => 42.0; }\n"
                    + "  public fun useSecret(v: Vec2): float => v.secret();\n"
                    + "}\n"
                    + "fun run(): float { return Registry().useSecret(Vec2(1.0, 1.0)); }");

            Assert.Equal(42.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AnExtensionNestedInAClassIsNotReachableFromOutsideIt()
        {
            using var compilation = Reject(
                Vec2
                    + "class Registry {\n"
                    + "  public constructor() { }\n"
                    + "  private extension Vec2 { fun secret(v: Vec2): float => 42.0; }\n"
                    + "}\n"
                    + "fun run(): float { return Vec2(1.0, 1.0).secret(); }");

            Assert.True(compilation.HasErrors, "A private extension nested in another class should not be a candidate outside it.");
        }

        [Fact]
        public void AnExtensionMemberWiderThanItsBlockIsRejected()
        {
            using var compilation = Reject(
                Vec2
                    + "class Registry {\n"
                    + "  public constructor() { }\n"
                    + "  private extension Vec2 { public fun open(v: Vec2): float => 1.0; }\n"
                    + "}\n"
                    + "fun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.ExtensionMemberVisibilityTooWide);
        }

        [Fact]
        public void AnExtensionMemberNarrowerThanItsBlockIsAccepted()
        {
            var runtime = Run(
                Vec2
                    + "class Registry {\n"
                    + "  public constructor() { }\n"
                    + "  internal extension Vec2 { private fun secret(v: Vec2): float => 7.0; }\n"
                    + "  public fun useSecret(v: Vec2): float => v.secret();\n"
                    + "}\n"
                    + "fun run(): float { return Registry().useSecret(Vec2(1.0, 1.0)); }");

            Assert.Equal(7.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AnExtensionMethodWhoseFirstParameterIsNotTheTargetTypeIsRejected()
        {
            using var compilation = Reject(
                Vec2 + "extension Vec2 { fun broken(x: int): int => x; }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidExtensionReceiver);
        }

        [Fact]
        public void AnExtensionMethodWithNoParametersAtAllIsRejected()
        {
            using var compilation = Reject(
                Vec2 + "extension Vec2 { fun broken(): int => 1; }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidExtensionReceiver);
        }

        [Fact]
        public void AFieldInsideAnExtensionBlockIsRejected()
        {
            using var compilation = Reject(
                Vec2 + "extension Vec2 { let cached: int = 0; }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidExtensionMember);
        }

        [Fact]
        public void AConstructorInsideAnExtensionBlockIsRejected()
        {
            using var compilation = Reject(
                Vec2 + "extension Vec2 { constructor() { } }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidExtensionMember);
        }

        [Fact]
        public void AnExtensionBlockDeclaredStaticIsRejected()
        {
            using var compilation = Reject(
                Vec2 + "static extension Vec2 { fun f(v: Vec2): int => 1; }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidModifier);
        }
        #endregion

        #region Extension methods (§15) — Fase 2: imports and scope visibility
        [Fact]
        public void AnExtensionMethodBroughtByAWildcardImportIsCallable()
        {
            var runtime = Run(
                "import game.util.*;\nfun run(): float { return Vec2(3.0, 4.0).lengthSquared(); }",
                ("/game/util/M.surtr",
                    "public class Vec2 {\n"
                        + "  public let x: float;\n"
                        + "  public let y: float;\n"
                        + "  public constructor(x: float, y: float) { this.x = x; this.y = y; }\n"
                        + "}\n"
                        + "public extension Vec2 { fun lengthSquared(v: Vec2): float => v.x * v.x + v.y * v.y; }"));

            Assert.Equal(25.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AnExtensionMethodNotImportedIsNotACandidate()
        {
            using var compilation = Reject(
                "class Vec2 { public let x: float; public constructor(x: float) { this.x = x; } }\n"
                    + "fun run(): int { return Vec2(1.0).bonus(); }",
                ("/game/util/M.surtr", "import game.core.Test;\npublic extension Vec2 { fun bonus(v: Vec2): int => 1; }"));

            Assert.True(compilation.HasErrors, "An extension declared in a module nobody imported should not be a candidate.");
        }

        [Fact]
        public void TwoEquallyApplicableExtensionsFromDifferentImportsAreAmbiguous()
        {
            using var compilation = Reject(
                "import game.shapes.*;\nimport game.util.a.*;\nimport game.util.b.*;\n"
                    + "fun run(): string { return Vec2(1.0).describe(); }",
                ("/game/shapes/S.surtr", "class Vec2 { public let x: float; public constructor(x: float) { this.x = x; } }"),
                ("/game/util/a/A.surtr", "import game.shapes.*;\npublic extension Vec2 { fun describe(v: Vec2): string => \"a\"; }"),
                ("/game/util/b/B.surtr", "import game.shapes.*;\npublic extension Vec2 { fun describe(v: Vec2): string => \"b\"; }"));

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnresolvedCall);
        }

        [Fact]
        public void AnInternalExtensionIsNotReachableFromAnImportingModule()
        {
            using var compilation = Reject(
                "import game.util.*;\nfun run(): float { return Vec2(1.0, 1.0).lengthSquared(); }",
                ("/game/util/M.surtr",
                    Vec2 + "extension Vec2 { fun lengthSquared(v: Vec2): float => v.x * v.x + v.y * v.y; }"));

            Assert.True(compilation.HasErrors, "An extension block with no visibility written defaults to internal (§3.1) and should not reach another module.");
        }
        #endregion

        #region Extension methods (§15) — Fase 3: static methods
        [Fact]
        public void AStaticExtensionMethodIsCallableOnItsTargetType()
        {
            var runtime = Run(
                Vec2
                    + "extension Vec2 { static fun zero(): Vec2 => Vec2(0.0, 0.0); }\n"
                    + "fun run(): float { return Vec2.zero().x; }");

            Assert.Equal(0.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AStaticExtensionMethodTakesOrdinaryArguments()
        {
            var runtime = Run(
                Vec2
                    + "extension Vec2 { static fun of(x: float, y: float): Vec2 => Vec2(x, y); }\n"
                    + "fun run(): float { return Vec2.of(3.0, 4.0).x + Vec2.of(3.0, 4.0).y; }");

            Assert.Equal(7.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void ARealStaticMemberWinsSilentlyOverAStaticExtensionWithTheSameName()
        {
            var runtime = Run(
                "class Vec2 {\n"
                    + "  public let x: float;\n"
                    + "  public constructor(x: float) { this.x = x; }\n"
                    + "  public static fun zero(): Vec2 => Vec2(9.0);\n"
                    + "}\n"
                    + "extension Vec2 { static fun zero(): Vec2 => Vec2(-1.0); }\n"
                    + "fun run(): float { return Vec2.zero().x; }");

            Assert.Equal(9.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AStaticExtensionMethodBroughtByAWildcardImportIsCallable()
        {
            var runtime = Run(
                "import game.util.*;\nfun run(): float { return Vec2.zero().x; }",
                ("/game/util/M.surtr",
                    "public class Vec2 {\n"
                        + "  public let x: float;\n"
                        + "  public let y: float;\n"
                        + "  public constructor(x: float, y: float) { this.x = x; this.y = y; }\n"
                        + "}\n"
                        + "public extension Vec2 { static fun zero(): Vec2 => Vec2(0.0, 0.0); }"));

            Assert.Equal(0.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AnInternalStaticExtensionIsNotReachableFromAnImportingModule()
        {
            // Vec2 itself is public, so this isolates the extension's own default visibility
            // (internal, unwritten) rather than confounding it with the type's.
            using var compilation = Reject(
                "import game.util.*;\nfun run(): float { return Vec2.zero().x; }",
                ("/game/util/M.surtr",
                    "public class Vec2 {\n"
                        + "  public let x: float;\n"
                        + "  public let y: float;\n"
                        + "  public constructor(x: float, y: float) { this.x = x; this.y = y; }\n"
                        + "}\n"
                        + "extension Vec2 { static fun zero(): Vec2 => Vec2(0.0, 0.0); }"));

            Assert.True(compilation.HasErrors, "A static extension with no visibility written defaults to internal (§3.1) and should not reach another module.");
        }
        #endregion

        #region Extension methods (§15) — Fase 4: extension properties
        [Fact]
        public void AReadOnlyExtensionPropertyIsReadableOnItsTargetType()
        {
            var runtime = Run(
                Vec2 + "extension Vec2 { lengthSquared: float => this.x * this.x + this.y * this.y; }\n"
                    + "fun run(): float { return Vec2(3.0, 4.0).lengthSquared; }");

            Assert.Equal(25.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AnExtensionPropertyWithExplicitGetAndSetWorks()
        {
            var runtime = Run(
                "class Vec2 {\n"
                    + "  public var x: float;\n"
                    + "  public constructor(x: float) { this.x = x; }\n"
                    + "}\n"
                    + "extension Vec2 {\n"
                    + "  doubled: float {\n"
                    + "    get { return this.x * 2.0; }\n"
                    + "    set { this.x = value / 2.0; }\n"
                    + "  }\n"
                    + "}\n"
                    + "fun run(): float { let v = Vec2(3.0); v.doubled = 10.0; return v.x; }");

            Assert.Equal(5.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void ARealPropertyWinsSilentlyOverAnExtensionPropertyWithTheSameName()
        {
            var runtime = Run(
                "class Vec2 {\n"
                    + "  public let x: float;\n"
                    + "  public constructor(x: float) { this.x = x; }\n"
                    + "  public doubled: float => x * 2.0;\n"
                    + "}\n"
                    + "extension Vec2 { doubled: float => -1.0; }\n"
                    + "fun run(): float { return Vec2(5.0).doubled; }");

            Assert.Equal(10.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AStaticExtensionPropertyIsReadableOnItsTargetType()
        {
            var runtime = Run(
                Vec2 + "extension Vec2 { static zero: Vec2 => Vec2(0.0, 0.0); }\n"
                    + "fun run(): float { return Vec2.zero.x; }");

            Assert.Equal(0.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AnExtensionMethodCanReferenceItsReceiverAsThisToo()
        {
            // §15.1's own explicit-parameter model still requires the parameter to be written out
            // (`self` here) - `this` is an additional way to reach the very same parameter, not a
            // replacement for writing it.
            var runtime = Run(
                Vec2 + "extension Vec2 { fun lengthSquared(self: Vec2): float => this.x * this.x + this.y * this.y; }\n"
                    + "fun run(): float { return Vec2(3.0, 4.0).lengthSquared(); }");

            Assert.Equal(25.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AnExtensionPropertyNestedInAClassIsReachableFromThatClasssOwnMembers()
        {
            var runtime = Run(
                Vec2
                    + "class Registry {\n"
                    + "  public constructor() { }\n"
                    + "  private extension Vec2 { secret: float => this.x + this.y; }\n"
                    + "  public fun readSecret(v: Vec2): float => v.secret;\n"
                    + "}\n"
                    + "fun run(): float { return Registry().readSecret(Vec2(1.0, 2.0)); }");

            Assert.Equal(3.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AnExtensionPropertyBroughtByAWildcardImportIsReadable()
        {
            var runtime = Run(
                "import game.util.*;\nfun run(): float { return Vec2(3.0, 4.0).lengthSquared; }",
                ("/game/util/M.surtr",
                    "public class Vec2 {\n"
                        + "  public let x: float;\n"
                        + "  public let y: float;\n"
                        + "  public constructor(x: float, y: float) { this.x = x; this.y = y; }\n"
                        + "}\n"
                        + "public extension Vec2 { lengthSquared: float => this.x * this.x + this.y * this.y; }"));

            Assert.Equal(25.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void TwoEquallyApplicableExtensionPropertiesFromDifferentImportsAreAmbiguous()
        {
            using var compilation = Reject(
                "import game.shapes.*;\nimport game.util.a.*;\nimport game.util.b.*;\n"
                    + "fun run(): float { return Vec2(1.0, 2.0).lengthSquared; }",
                ("/game/shapes/S.surtr",
                    "public class Vec2 {\n  public let x: float;\n  public let y: float;\n"
                        + "  public constructor(x: float, y: float) { this.x = x; this.y = y; }\n}"),
                ("/game/util/a/A.surtr",
                    "import game.shapes.*;\npublic extension Vec2 { lengthSquared: float => this.x; }"),
                ("/game/util/b/B.surtr",
                    "import game.shapes.*;\npublic extension Vec2 { lengthSquared: float => this.y; }"));

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnresolvedCall);
        }

        [Fact]
        public void AnExtensionAutoPropertyWithNoAccessorsIsRejected()
        {
            using var compilation = Reject(
                Vec2 + "extension Vec2 { cached: float { } }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidExtensionMember);
        }

        [Fact]
        public void AnExtensionPropertyAccessorWithNoBodyIsRejected()
        {
            using var compilation = Reject(
                Vec2 + "extension Vec2 { cached: float { get; } }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidExtensionMember);
        }
        #endregion

        #region Extension methods (§15) — Fase 5: composite and built-in targets
        [Fact]
        public void AnExtensionMethodIsCallableOnAnArrayTargetType()
        {
            var runtime = Run(
                "extension int[] { fun sum(xs: int[]): int {\n"
                    + "  var total = 0;\n"
                    + "  for (x in xs) { total += x; }\n"
                    + "  return total;\n"
                    + "} }\n"
                    + "fun run(): int { let xs: int[] = [1, 2, 3, 4]; return xs.sum(); }");

            Assert.Equal(10, Int(runtime, "run"));
        }

        [Fact]
        public void ExtensionsOverDifferentArrayElementTypesResolveIndependently()
        {
            var runtime = Run(
                "extension int[] { fun describe(xs: int[]): string => \"ints\"; }\n"
                    + "extension string[] { fun describe(xs: string[]): string => \"strings\"; }\n"
                    + "fun runInts(): string { let xs: int[] = [1]; return xs.describe(); }\n"
                    + "fun runStrings(): string { let xs: string[] = [\"a\"]; return xs.describe(); }");

            Assert.Equal("ints", Text(runtime, "runInts"));
            Assert.Equal("strings", Text(runtime, "runStrings"));
        }

        [Fact]
        public void AnExtensionMethodIsCallableOnADictionaryTargetType()
        {
            var runtime = Run(
                "extension {string: int} { fun total(m: {string: int}): int {\n"
                    + "  var sum = 0;\n"
                    + "  for (k in m.keys()) { sum += m.get(k); }\n"
                    + "  return sum;\n"
                    + "} }\n"
                    + "fun run(): int { let m: {string: int} = {\"a\": 1, \"b\": 2}; return m.total(); }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        [Fact]
        public void AnExtensionMethodIsCallableOnTheStringTargetType()
        {
            var runtime = Run(
                "extension string { fun shout(s: string): string => s + \"!\"; }\n"
                    + "fun run(): string { return \"hi\".shout(); }");

            Assert.Equal("hi!", Text(runtime, "run"));
        }

        [Fact]
        public void AnExtensionMethodIsCallableOnAUserValueClassTargetType()
        {
            var runtime = Run(
                "value class EntityId { public let raw: int; public constructor(raw: int) { this.raw = raw; } }\n"
                    + "extension EntityId { fun doubled(id: EntityId): int => id.raw * 2; }\n"
                    + "fun run(): int { return EntityId(21).doubled(); }");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void AnExtensionPropertyIsReadableOnAnArrayTargetType()
        {
            var runtime = Run(
                "extension int[] { isEmptyIsh: bool => this.length == 0; }\n"
                    + "fun run(): bool { let xs: int[] = []; return xs.isEmptyIsh; }");

            Assert.True(Call(runtime, "run").AsBool);
        }
        #endregion

        #region Extension methods (§15) — Fase 6: generic extensions
        [Fact]
        public void AnExtensionMethodOverAnArrayInfersItsElementTypeImplicitly()
        {
            // `T` needs no separate `<T>` list at all (§15.4) - the bare name inside the target
            // type (`T[]`) is enough to declare it.
            var runtime = Run(
                "extension T[] { fun second(self: T[]): T => self[1]; }\n"
                    + "fun run(): int { let xs: int[] = [10, 20, 30]; return xs.second(); }");

            Assert.Equal(20, Int(runtime, "run"));
        }

        [Fact]
        public void AnExtensionMethodOverAUserGenericClassInfersItsTypeParameter()
        {
            var runtime = Run(
                Box + "extension Box<T> { fun unwrap(self: Box<T>): T => self.get(); }\n"
                    + "fun run(): int { return Box(5).unwrap(); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericExtensionMethodsOwnTypeParameterIsNotTheTargetsRealG0()
        {
            // §15.4: the extension's own `T` is inferred fresh at each call site through ordinary
            // generic-method substitution, never through the array built-in's own erasure — an
            // extra parameter of type `T` (`fallback`), not just the receiver, still infers and
            // substitutes correctly.
            var runtime = Run(
                "extension T[] { fun firstOrDefault(self: T[], fallback: T): T => self.length == 0 ? fallback : self[0]; }\n"
                    + "fun run(): int { let xs: int[] = []; return xs.firstOrDefault(9); }");

            Assert.Equal(9, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericExtensionMethodWithAConstraintCanCallItsBoundsMembers()
        {
            var runtime = Run(
                "class Score : IComparable<Score> {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "  public fun compareTo(other: Score): int { return value - other.value; }\n"
                    + "}\n"
                    + "extension<T : IComparable<T>> T[] {\n"
                    + "  fun maxOf(self: T[]): T {\n"
                    + "    var best = self[0];\n"
                    + "    for (x in self) { if (x.compareTo(best) > 0) { best = x; } }\n"
                    + "    return best;\n"
                    + "  }\n"
                    + "}\n"
                    + "fun run(): int { let xs: Score[] = [Score(4), Score(9), Score(2)]; return xs.maxOf().value; }");

            Assert.Equal(9, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericExtensionMethodViolatingItsConstraintIsRejected()
        {
            using var compilation = Reject(
                "class Plain { }\n"
                    + "extension<T : IComparable<T>> T[] { fun maxOf(self: T[]): T => self[0]; }\n"
                    + "fun run(): Plain { let xs: Plain[] = [Plain()]; return xs.maxOf(); }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.ConstraintNotSatisfied);
        }

        [Fact]
        public void APropertyInsideAnExplicitlyGenericExtensionBlockIsRejected()
        {
            using var compilation = Reject(
                "extension<T> T[] { first: T => this[0]; }\nfun run(): int { return 1; }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidExtensionMember);
        }

        [Fact]
        public void AGenericExtensionOverABuiltInInterfaceInfersItsTypeParameterThroughTheImplementingComposite()
        {
            // `IIterable<T>` is the target, not `int[]` itself - inferring the extension's own `T`
            // has to walk from the supplied `int[]` up to the built-in `array<int>` class behind it
            // and on to the `IIterable<int>` it implements, the same hierarchy walk
            // `Conversions.WalkForBase` already does for ordinary assignability.
            var runtime = Run(
                "extension IIterable<T> { fun countAll(self: IIterable<T>): int {\n"
                    + "  var total = 0;\n"
                    + "  for (x in self) { total += 1; }\n"
                    + "  return total;\n"
                    + "} }\n"
                    + "fun run(): int { let xs: int[] = [1, 2, 3]; return xs.countAll(); }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        /// <summary>
        /// The same contract, reached through a Surtr source class rather than a built-in
        /// composite: the receiver satisfies <c>IIterable&lt;int&gt;</c> with its own
        /// <c>iterate()</c>, so the extension's <c>T</c> infers from the user type's
        /// imported interface slot.
        /// </summary>
        [Fact]
        public void AnExtensionOverABuiltInInterfaceResolvesThroughAUserTypeThatImplementsIt()
        {
            var runtime = Run(
                "class Rope : IIterable<int> {\n"
                    + "  public let chars: int[];\n"
                    + "  constructor(chars: int[]) { this.chars = chars; }\n"
                    + "  public fun iterate(): IIterator<int> => chars.iterate();\n"
                    + "}\n"
                    + "extension IIterable<T> { fun countAll(self: IIterable<T>): int {\n"
                    + "  var total = 0;\n"
                    + "  for (x in self) { total += 1; }\n"
                    + "  return total;\n"
                    + "} }\n"
                    + "fun run(): int { let r = Rope([1, 2, 3, 4]); return r.countAll(); }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void AnExtensionOverABuiltInInterfaceResolvesThroughString()
        {
            var runtime = Run(
                "extension IIterable<T> { fun countAll(self: IIterable<T>): int {\n"
                    + "  var total = 0;\n"
                    + "  for (x in self) { total += 1; }\n"
                    + "  return total;\n"
                    + "} }\n"
                    + "fun run(): int { return \"hey\".countAll(); }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        /// <summary>
        /// A dictionary satisfies <c>IIterable</c> over its (K, V) pair tuple, so the extension's
        /// <c>T</c> infers as the tuple the receiver yields - one hierarchy walk, from the dict
        /// composite straight up to the contract.
        /// </summary>
        [Fact]
        public void AnExtensionOverABuiltInInterfaceInfersATupleFromADictionaryReceiver()
        {
            var runtime = Run(
                "extension IIterable<T> { fun countAll(self: IIterable<T>): int {\n"
                    + "  var total = 0;\n"
                    + "  for (x in self) { total += 1; }\n"
                    + "  return total;\n"
                    + "} }\n"
                    + "fun run(): int { let m: {string: int} = {\"a\": 1, \"b\": 2}; return m.countAll(); }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        /// <summary>
        /// The two-parameter dictionary target: <c>K</c> and <c>V</c> both infer from the
        /// receiver's written type, exactly as two type arguments on a user generic would.
        /// </summary>
        [Fact]
        public void AnExtensionOverATwoParameterBuiltInTargetInfersBoth()
        {
            var runtime = Run(
                "extension {K: V} { fun keyCount(self: {K: V}): int => self.keys().length; }\n"
                    + "fun run(): int { let m: {string: int} = {\"a\": 1, \"b\": 2}; return m.keyCount(); }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        /// <summary>
        /// The extension's <c>T</c> flows into a second parameter whose type is the interface's
        /// own argument, not just the receiver: the call site substitutes <c>Score</c> for both.
        /// </summary>
        [Fact]
        public void AnExtensionOverABuiltInInterfaceSubstitutesTheTargetIntoAnExtraParameter()
        {
            var runtime = Run(
                "class Score : IComparable<Score> {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "  public fun compareTo(other: Score): int { return value - other.value; }\n"
                    + "}\n"
                    + "extension IComparable<T> { fun isLessThan(self: IComparable<T>, other: T): bool => self.compareTo(other) < 0; }\n"
                    + "fun run(): int { return Score(4).isLessThan(Score(9)) ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>
        /// The same extension, over the same built-in interface, but this time the receiver
        /// implementing it is <c>int</c> rather than a Surtr class - the built-ins satisfy
        /// <c>IComparable</c>/<c>IEquatable</c> too (§13.2), so an extension written once against
        /// the contract reaches a primitive the same way it reaches a user type.
        /// </summary>
        [Fact]
        public void AnExtensionOverABuiltInInterfaceResolvesThroughAPrimitiveReceiver()
        {
            var runtime = Run(
                "extension IComparable<T> { fun isLessThan(self: IComparable<T>, other: T): bool => self.compareTo(other) < 0; }\n"
                    + "fun run(): int { return 4.isLessThan(9) ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>
        /// A generic extension compiled into one module and used from another: the extension
        /// member travels as a generic method of its module, and the importing call site has to
        /// infer and substitute its parameter from metadata alone - the same path any cross-module
        /// generic method takes, for the receiver-shaped call an extension is.
        /// </summary>
        [Fact]
        public void AGenericExtensionSurvivesTheImageIntoAnotherModule()
        {
            var emitter = Build("public extension T[] { fun second(self: T[]): T => self[1]; }");
            var built = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes()).Instantiate();

            using var runtime = new SurtrRuntime();
            runtime.LoadModule(built);

            var app = new SurtrProject(Root);
            app.AddReference(built);
            app.AddSourceFile(
                Root + "/game/util/Util.surtr",
                "import game.core.Test;\nfun run(): int { let xs: int[] = [10, 20]; return xs.second(); }");

            using var compilation = SurtrCompilation.Create(app);
            var binder = compilation.Bind();
            binder.BindBodies();
            Assert.True(!compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
            var appEmitter = new ModuleEmitter(compilation, binder);
            Assert.True(appEmitter.TryEmit());
            runtime.LoadModule(appEmitter.Modules[0]);

            Assert.Equal(20, runtime.Invoke(Function(runtime, "game.util.Util", "run"), Array.Empty<SurtrValue>()).AsInt);
        }

        [Fact]
        public void AnExtensionOverANestedArrayTargetInfersTheElementType()
        {
            var runtime = Run(
                "extension T[][] { fun cellCount(self: T[][]): int {\n"
                    + "  var n = 0;\n"
                    + "  for (row in self) { n += row.length; }\n"
                    + "  return n;\n"
                    + "} }\n"
                    + "fun run(): int { let m: int[][] = [[1, 2], [3]]; return m.cellCount(); }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        [Fact]
        public void AnExtensionMethodIsCallableOnATupleTargetType()
        {
            var runtime = Run(
                "extension (int, string) { fun describe(self: (int, string)): string => self[1] + \" #\" + self[0]; }\n"
                    + "fun run(): string { let t = (7, \"seven\"); return t.describe(); }");

            Assert.Equal("seven #7", Text(runtime, "run"));
        }

        [Fact]
        public void AnExtensionMethodIsCallableOnARangeTargetType()
        {
            var runtime = Run(
                "extension range { fun size(r: range): int { var n = 0; for (x in r) { n += 1; } return n; } }\n"
                    + "fun run(): int { return (0..=3).size(); }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void AnExtensionMethodIsCallableOnAnIntTargetType()
        {
            var runtime = Run(
                "extension int { fun doubled(n: int): int => n * 2; }\n"
                    + "fun run(): int { return 21.doubled(); }");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void AnExtensionMethodIsCallableOnAFloatTargetType()
        {
            var runtime = Run(
                "extension float { fun halved(n: float): float => n / 2.0; }\n"
                    + "fun run(): float { return 9.0.halved(); }");

            Assert.Equal(4.5f, Call(runtime, "run").AsFloat, 3);
        }

        [Fact]
        public void AnExtensionMethodIsCallableOnABoolTargetType()
        {
            var runtime = Run(
                "extension bool { fun toYesNo(b: bool): string => b ? \"yes\" : \"no\"; }\n"
                    + "fun run(): string { return true.toYesNo(); }");

            Assert.Equal("yes", Text(runtime, "run"));
        }

        /// <summary>
        /// An extension over a contract the receiver does not satisfy is simply not a candidate:
        /// the call site fails to resolve, exactly as a member the type never declared would.
        /// </summary>
        [Fact]
        public void AnExtensionOverABuiltInInterfaceDoesNotResolveFromATypeThatDoesNotImplementIt()
        {
            using var compilation = Reject(
                "class Plain { }\n"
                    + "extension IIterable<T> { fun countAll(self: IIterable<T>): int => 0; }\n"
                    + "fun run(): int { return Plain().countAll(); }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnresolvedName);
        }
        #endregion

        #region @Range runtime checks (§P4)

        [Fact]
        public void AnInRangeAssignmentPassesWhenChecksAreOn()
        {
            var runtime = RunDebug(
                "class Player {\n"
                    + "  @Range(0.0, 100.0)\n"
                    + "  public var health: float = 50.0;\n"
                    + "  public fun setHealth(v: float): void { health = v; }\n"
                    + "}\n"
                    + "fun run(): float { let p = Player(); p.setHealth(75.0); return p.health; }");

            Assert.Equal(75.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AnOutOfRangeAssignmentThrowsWithTheFieldAndBoundsNamed()
        {
            var runtime = RunDebug(
                "class Player {\n"
                    + "  @Range(0.0, 100.0)\n"
                    + "  public var health: float = 50.0;\n"
                    + "  public fun setHealth(v: float): void { health = v; }\n"
                    + "}\n"
                    + "fun run(): float { let p = Player(); p.setHealth(150.0); return p.health; }");

            var thrown = Assert.Throws<SurtrThrownException>(() => Call(runtime, "run"));
            Assert.Contains("ArgumentOutOfRangeException", thrown.Message, StringComparison.Ordinal);

            var raised = runtime.Resolve<SurtrInstance>(SurtrValue.CreateReference(thrown.Reference));
            Assert.NotNull(raised);
            string message = runtime.Resolve<SurtrString>(raised![0])!.Text;
            Assert.Contains("health", message, StringComparison.Ordinal);
            Assert.Contains("100", message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnIntegerFieldIsCheckedAgainstItsFloatBounds()
        {
            var runtime = RunDebug(
                "class Game {\n"
                    + "  @Range(1, 8)\n"
                    + "  public var bounces: int = 3;\n"
                    + "  public fun setBounces(v: int): void { bounces = v; }\n"
                    + "}\n"
                    + "fun run(): int { let g = Game(); g.setBounces(9); return g.bounces; }");

            Assert.Throws<SurtrThrownException>(() => Call(runtime, "run"));
        }

        [Fact]
        public void APropertySetIsCheckedLikeAFieldWrite()
        {
            var runtime = RunDebug(
                "class Gauge {\n"
                    + "  private var _level: float = 0.0;\n"
                    + "  @Range(0.0, 10.0)\n"
                    + "  public level: float\n"
                    + "  {\n"
                    + "    get { return _level; }\n"
                    + "    set { _level = value; }\n"
                    + "  }\n"
                    + "}\n"
                    + "fun run(): float { let g = Gauge(); g.level = 42.0; return g.level; }");

            Assert.Throws<SurtrThrownException>(() => Call(runtime, "run"));
        }

        [Fact]
        public void AReleaseBuildWithoutDebugCarriesNoCheck()
        {
            var runtime = Run(
                "class Player {\n"
                    + "  @Range(0.0, 100.0)\n"
                    + "  public var health: float = 50.0;\n"
                    + "  public fun setHealth(v: float): void { health = v; }\n"
                    + "}\n"
                    + "fun run(): float { let p = Player(); p.setHealth(150.0); return p.health; }");

            Assert.Equal(150.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void ASingleLowerBoundRejectsOnlyValuesBelowIt()
        {
            var runtime = RunDebug(
                "class Player {\n"
                    + "  @Range(100.0)\n"
                    + "  public var health: float = 150.0;\n"
                    + "  public fun setHealth(v: float): void { health = v; }\n"
                    + "}\n"
                    + "fun below(): float { let p = Player(); p.setHealth(25.0); return p.health; }\n"
                    + "fun above(): float { let p = Player(); p.setHealth(200.0); return p.health; }");

            var thrown = Assert.Throws<SurtrThrownException>(() => Call(runtime, "below"));
            Assert.Contains("ArgumentOutOfRangeException", thrown.Message, StringComparison.Ordinal);

            Assert.Equal(200.0, Call(runtime, "above").AsFloat);
        }

        [Fact]
        public void AStaticFieldWriteIsCheckedAgainstItsRange()
        {
            var runtime = RunDebug(
                "class Config {\n"
                    + "  @Range(0.0, 100.0)\n"
                    + "  public static var health: float = 50.0;\n"
                    + "}\n"
                    + "fun run(): float { Config.health = 150.0; return Config.health; }");

            Assert.Throws<SurtrThrownException>(() => Call(runtime, "run"));
        }

        [Fact]
        public void AnOutOfRangeFieldInitializerThrowsAtConstruction()
        {
            var runtime = RunDebug(
                "class Player {\n"
                    + "  @Range(0.0, 100.0)\n"
                    + "  public var health: float = 150.0;\n"
                    + "}\n"
                    + "fun run(): float { let p = Player(); return p.health; }");

            var thrown = Assert.Throws<SurtrThrownException>(() => Call(runtime, "run"));
            Assert.Contains("ArgumentOutOfRangeException", thrown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnInRangeFieldInitializerConstructsNormally()
        {
            var runtime = RunDebug(
                "class Player {\n"
                    + "  @Range(0.0, 100.0)\n"
                    + "  public var health: float = 25.0;\n"
                    + "}\n"
                    + "fun run(): float { let p = Player(); return p.health; }");

            Assert.Equal(25.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void AnOutOfRangeStaticFieldInitializerThrowsAtLoad()
        {
            var thrown = Assert.Throws<SurtrThrownException>(() =>
            {
                _ = RunDebug(
                    "class Config {\n"
                        + "  @Range(0.0, 100.0)\n"
                        + "  public static var health: float = 150.0;\n"
                        + "}\n"
                        + "fun run(): float { return Config.health; }");
            });

            Assert.Contains("ArgumentOutOfRangeException", thrown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ANestedAssignmentIntoARangedFieldIsChecked()
        {
            var runtime = RunDebug(
                "class Player {\n"
                    + "  @Range(0.0, 100.0)\n"
                    + "  public var health: float = 50.0;\n"
                    + "}\n"
                    + "fun run(): float { var result = 0.0; let p = Player(); result = (p.health = 150.0) + 1.0; return result; }");

            Assert.Throws<SurtrThrownException>(() => Call(runtime, "run"));
        }

        [Fact]
        public void ACompoundAssignmentIntoARangedFieldIsChecked()
        {
            var runtime = RunDebug(
                "class Player {\n"
                    + "  @Range(0.0, 100.0)\n"
                    + "  public var health: float = 50.0;\n"
                    + "}\n"
                    + "fun run(): float { let p = Player(); p.health += 200.0; return p.health; }");

            Assert.Throws<SurtrThrownException>(() => Call(runtime, "run"));
        }

        [Fact]
        public void ARangedAssignmentValueIsEvaluatedExactlyOnce()
        {
            var runtime = RunDebug(
                "class Player {\n"
                    + "  @Range(0.0, 100.0)\n"
                    + "  public var health: float = 50.0;\n"
                    + "}\n"
                    + "var calls: int = 0;\n"
                    + "fun next(): float { calls = calls + 1; return 75.0; }\n"
                    + "fun run(): int { let p = Player(); p.health = next(); return calls; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>
        /// Regression for B11 (docs/Plan-Revision-Stdlib.md §6.3c), now fixed: a generic method
        /// invoking its own closure parameter with a value of its own generic type, synchronously,
        /// in the same method - the doc's exact minimal repro. Root cause, confirmed by reading the
        /// emitted bytecode: a lambda's unwritten parameter type is bound against the *substituted*
        /// closure type it lands in (<c>apply(5, (v) => v * 100)</c> types <c>v</c> as concrete
        /// <c>int</c>, which is what lets <c>v * 100</c> type-check at all), so the lifted lambda's
        /// compiled body reads its parameter raw - but <c>apply</c>'s own body is generic (<c>f: (T0)
        /// -&gt; T0</c>, fully erased), so its call to <c>f(v)</c> always pushes a boxed value, the
        /// convention every <c>T0</c>-typed value at rest in a generic body follows. The lambda's raw
        /// read then multiplies a boxed reference's own raw bits (an entity id) instead of the value
        /// inside the box. Fix: every lifted lambda body defensively unboxes a primitive or
        /// single-field-value-class parameter at entry (<c>MethodBodyEmitter.
        /// EmitLambdaParameterUnboxIfNeeded</c>) - a no-op when the value already arrived raw (an
        /// ordinary, concretely-typed call), and the missing unbox when it arrived boxed (the
        /// generic-erased call).
        /// </summary>
        /// <remarks>
        /// Also covers three variants that turned out to matter while narrowing this down: T fixed
        /// by a class's own generic parameter rather than inferred fresh in this call (closer to
        /// <c>Sequence&lt;T&gt;</c>'s shape), two separate type parameters instead of reusing one for
        /// both the value and the closure's return, and an explicit type argument instead of
        /// inference - all four reproduced identically before the fix. A lambda that only calls a
        /// method on its parameter (<c>v.toString()</c>) never reproduced this at all: dynamic
        /// dispatch reads a value's class off its own reference regardless of whether the reader's
        /// static type is erased or concrete, so it was never a counterexample to the root cause -
        /// unlike the doc's original "synchronous vs deferred invocation" theory, which this rules
        /// out (Box&lt;T&gt;.apply here calls its closure synchronously, in the same shape as the
        /// working theory's "unaffected" examples, and reproduced anyway).
        /// </remarks>
        [Fact]
        public void GenericMethodInvokesItsOwnClosureParameterWithTheRightValue()
        {
            var runtime = Run(
                "fun apply<T>(v: T, f: (T) -> T): T { return f(v); }\n"
                    + "fun run(): int { return apply(5, (v) => v * 100); }\n"
                    + "class Box<T> {\n"
                    + "  private var _value: T;\n"
                    + "  public constructor(value: T) { this._value = value; }\n"
                    + "  public fun apply(f: (T) -> T): T { return f(_value); }\n"
                    + "}\n"
                    + "fun runClassField(): int { let b = Box<int>(5); return b.apply((v) => v * 100); }\n"
                    + "fun applyTwoParams<T, U>(v: T, f: (T) -> U): U { return f(v); }\n"
                    + "fun runTwoTypeParams(): int { return applyTwoParams(5, (v) => v * 100); }\n"
                    + "fun runExplicitTypeArgument(): int { return apply<int>(5, (v) => v * 100); }\n"
                    + "fun applyToString<T>(v: T, f: (T) -> string): string { return f(v); }\n"
                    + "fun runMethodCallOnParam(): int { return applyToString(5, (v) => v.toString()).length; }\n");

            Assert.Equal(500, Int(runtime, "run"));
            Assert.Equal(500, Int(runtime, "runClassField"));
            Assert.Equal(500, Int(runtime, "runTwoTypeParams"));
            Assert.Equal(500, Int(runtime, "runExplicitTypeArgument"));
            Assert.Equal(1, Int(runtime, "runMethodCallOnParam"));
        }

        #endregion
    }
}
