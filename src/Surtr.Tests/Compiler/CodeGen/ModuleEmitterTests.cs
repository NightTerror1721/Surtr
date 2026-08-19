#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
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
        {
            var project = new SurtrProject(Root);
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

        private static SurtrMethodInfo Function(SurtrRuntime runtime, string modulePath, string name)
        {
            Assert.True(runtime.TryGetModule(modulePath, out var module), $"No module '{modulePath}' was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"'{modulePath}' declares no '{name}'.");
            return overloads[0];
        }

        private static SurtrValue Call(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => runtime.Invoke(Function(runtime, "game.core", name), arguments);

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

            Assert.Equal(42, runtime.Invoke(Function(runtime, "game.core", "answer"), Array.Empty<SurtrValue>()).AsInt);
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
                "import game.math as M;\nfun run(): int { return M.Box(21).value; }",
                ("/game/math/Box.surtr", "public class Box { public let value: int = 0; public constructor(value: int) { this.value = value; } }"));

            Assert.Equal(21, Int(runtime, "run"));
        }

        [Fact]
        public void AModuleAliasWorksInATypeAnnotation()
        {
            var runtime = Run(
                "import game.math as M;\n"
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
                "import game.math as M;\nimport game.other as M;\nfun run(): int { return 1; }",
                ("/game/math/Box.surtr", "public class Box { public let value: int = 0; }"),
                ("/game/other/Thing.surtr", "public class Thing { }"));

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.DuplicateModuleAlias);
        }
        #endregion

        #region Import: lista selectiva de miembros (§2.1, Fase 8)
        [Fact]
        public void ASelectiveImportBringsEveryListedNameIntoUnqualifiedScope()
        {
            var runtime = Run(
                "import game.math.{Box, Pair};\n"
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
                "import game.math.{Box};\n"
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
            var runtime = Run("fun run(): int { let m: Module = moduleof(game.core); return 1; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ModuleOfOnAnotherModuleCompilesAndRuns()
        {
            var runtime = Run(
                "import game.entities.Foo;\nfun run(): int { let m: Module = moduleof(game.entities); return Foo(5).n; }",
                ("/game/entities/Foo.surtr", "public class Foo { public let n: int = 0; public constructor(n: int) { this.n = n; } }"));

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>No `import` at all - only `moduleof` crosses the module boundary, which has to add its own dependency edge for load order to come out right.</summary>
        [Fact]
        public void ModuleOfAloneCreatesADependencyEdgeWithNoImport()
        {
            var runtime = Run(
                "fun run(): int { let m: Module = moduleof(game.entities); return 1; }",
                ("/game/entities/Foo.surtr", "public class Foo { public let n: int = 0; }"));

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ModuleOfThroughAnAliasResolvesTheAliasedModule()
        {
            var runtime = Run(
                "import game.entities as GE;\nfun run(): int { let m: Module = moduleof(GE); return 1; }",
                ("/game/entities/Foo.surtr", "public class Foo { public let n: int = 0; }"));

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>The runtime caches one `Module` value per `SurtrModule`, the same as `Type`.</summary>
        [Fact]
        public void ModuleOfOnTheSameModuleTwiceReturnsTheSameValue()
        {
            var runtime = Run(
                "fun a(): Module { return moduleof(game.core); }\nfun b(): Module { return moduleof(game.core); }");

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
            var runtime = Run("fun run(): string { return moduleof(game.core).path; }");

            Assert.Equal("game.core", Text(runtime, "run"));
        }

        [Fact]
        public void ModuleClassesEnumeratesItsOwnDeclaredClasses()
        {
            var runtime = Run("class Foo { }\nclass Bar { }\nfun run(): int { return moduleof(game.core).classes().length; }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void ModuleInterfacesEnumeratesItsOwnDeclaredInterfaces()
        {
            var runtime = Run("interface Named { }\nfun run(): int { return moduleof(game.core).interfaces().length; }");

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
                    + "fun run(): int { return moduleof(game.core).members().length; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void ModuleSubmodulesReachesANestedModule()
        {
            var runtime = Run(
                "fun run(): int { return moduleof(game.core).submodules().length; }",
                ("/game/core/sub/Deep.surtr", "public class Deep { }"));

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ModuleGetFindsALoadedModuleByPath()
        {
            var runtime = Run(
                "import game.entities.Foo;\nfun run(): string { return Module.get(\"game.entities\").path; }",
                ("/game/entities/Foo.surtr", "public class Foo { }"));

            Assert.Equal("game.entities", Text(runtime, "run"));
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
            var runtime = Run("class Foo { }\nfun run(): string { return Type.get(\"Ogame.core:Foo;\").name; }");

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
                "fun run(): int { if (Type.tryGet(\"Ogame.core:NoSuchType;\") == null) { return 1; } return 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void TypeGetThrowsForAnUnknownDescriptor()
        {
            var runtime = Run("fun run(): Type { return Type.get(\"Ogame.core:NoSuchType;\"); }");

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
                    + "class Hero : Named { public override fun name(): string { return \"hero\"; } }\n"
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
        public void AnEnumCaseIsAStaticInstanceTheInitializerBuilt()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                    + "fun run(): bool { return Suit.Hearts === Suit.Hearts; }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        [Fact]
        public void TwoEnumCasesAreDifferentInstances()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                    + "fun run(): bool { return Suit.Hearts === Suit.Spades; }");

            Assert.False(Call(runtime, "run").AsBool);
        }

        [Fact]
        public void AnEnumCaseCarriesItsConstructorArguments()
        {
            var runtime = Run(
                "enum Suit {\n"
                    + "  Hearts(1), Spades(4);\n"
                    + "  public let rank: int;\n"
                    + "  public constructor(rank: int) { this.rank = rank; }\n"
                    + "}\n"
                    + "fun run(): int { return Suit.Spades.rank; }");

            Assert.Equal(4, Int(runtime, "run"));
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
                    + "singleton Registry : Named { public override fun name(): string { return \"registry\"; } }\n"
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
                    + "  public override fun compareTo(other: Score): int { return this.value <=> other.value; }\n"
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
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class C { public constructor() : super() { } }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidConstructorChain);
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
                "fun run(): int { return game.util.Thing(9).n(); }",
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
                    + "  let simple = game.util.Simple(4);\n"
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
                    + "  public override fun getKind(): IShape.Kind { return IShape.Kind.Circle; }\n"
                    + "}\n"
                    + "fun run(): int { let c: IShape = Circle(); return c.getKind() === IShape.Kind.Circle ? 1 : 0; }");

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
                    + "class F : IFactory { public override fun make(): IFactory.Handle { return IFactory.Handle(); } }\n"
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
                    + "class C : INamed { public override name: string { get { return \"x\"; } } }\n"
                    + "fun run(): string { let n: INamed = C(); return n.name; }");

            Assert.Equal("x", Text(runtime, "run"));
        }

        /// <summary>An interface property's setter has to reach the contract, or no call site can assign through it.</summary>
        [Fact]
        public void AnInterfacePropertyKeepsItsSetter()
        {
            var runtime = Run(
                "interface ICounted { count: int { get; set; } }\n"
                    + "class C : ICounted { public override count: int { get; set; } }\n"
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
                    + "  override operator+(self: IAddable, other: IAddable): int { return 7; }\n"
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

        #region Reflexion de atributos: Type/Member (Fase 6)
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
        public void TypeBaseTypeWalksToTheDeclaredParentAndIsNullAtTheRoot()
        {
            var runtime = Run(
                "class Animal { public let legs: int = 4; }\n"
                    + "class Dog : Animal { public let name: string = \"Rex\"; }\n"
                    + "fun dogBaseName(): string { return Type.of(Dog()).baseType.name; }\n"
                    + "fun animalHasNoBase(): int {\n"
                    + "  if (Type.of(Animal()).baseType == null) { return 1; }\n"
                    + "  return 0;\n"
                    + "}");

            Assert.Equal("Animal", Text(runtime, "dogBaseName"));
            Assert.Equal(1, Int(runtime, "animalHasNoBase"));
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
                    + "fun parameterCount(): int { return Type.get(\"Ogame.core:Box`1;G0\").genericParameterCount; }\n"
                    + "fun parameterName(): string { return Type.get(\"Ogame.core:Box`1;G0\").genericParameters()[0]; }\n"
                    + "fun firstConstraint(): string { return Type.get(\"Ogame.core:Box`1;G0\").genericConstraints()[0][0]; }");

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
                    + "fun sameViaGet(): int { return typeof(Box<int>) === Type.get(\"Ogame.core:Box`1;I\") ? 1 : 0; }");

            Assert.Equal("int", Text(runtime, "argumentName"));
            Assert.Equal("Ogame.core:Box`1;I", Text(runtime, "descriptor"));
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
                    + "fun openHasNoArguments(): int { return Type.get(\"Ogame.core:Box`1;G0\").genericArguments().length; }\n"
                    + "fun openIsTheInstanceClass(): int { return Type.get(\"Ogame.core:Box`1;G0\") === Type.of(Box<int>()) ? 1 : 0; }");

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
                    + "fun openViaGetHasNoArguments(): int { return Type.get(\"Ogame.core:Box`1;G0\").genericArguments().length; }");

            Assert.Equal(0, Int(runtime, "noArguments"));
            Assert.Equal(1, Int(runtime, "noDescriptor"));
            Assert.Equal(0, Int(runtime, "openViaGetHasNoArguments"));
        }

        [Fact]
        public void TypeGetOnAConstructionRetainsTheDescriptorItWasAskedFor()
        {
            var runtime = Run(
                "class Box<T> { }\n"
                    + "fun descriptor(): string { return Type.get(\"Ogame.core:Box`1;S\").descriptor; }\n"
                    + "fun argumentName(): string { return Type.get(\"Ogame.core:Box`1;S\").genericArguments()[0].name; }");

            Assert.Equal("Ogame.core:Box`1;S", Text(runtime, "descriptor"));
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
                "fun run(): int { let t: game.util.Quiet? = null; return 1; }",
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
                    + "  public override fun compareTo(other: Score): int { return value - other.value; }\n"
                    + "}\n"
                    + "fun biggest<T : IComparable<T>>(a: T, b: T): T { return a.compareTo(b) >= 0 ? a : b; }\n"
                    + "fun run(): int { let s: Score = biggest(Score(4), Score(9)); return s.value; }");

            Assert.Equal(9, Int(runtime, "run"));
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

            Assert.True(runtime.TryGetModule("game.core", out var module));
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
                    + "  public override fun compareTo(other: Score): int { return value - other.value; }\n"
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
                "import game.core.*;\nfun run(): int { let s: Score = biggest(Score(4), Score(9)); return s.value; }");

            using var compilation = SurtrCompilation.Create(app);
            var binder = compilation.Bind();
            binder.BindBodies();
            Assert.False(compilation.HasErrors);
            var appEmitter = new ModuleEmitter(compilation, binder);
            Assert.True(appEmitter.TryEmit());
            runtime.LoadModule(appEmitter.Modules[0]);

            Assert.Equal(9, runtime.Invoke(Function(runtime, "game.util", "run"), Array.Empty<SurtrValue>()).AsInt);
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
                "import game.core.*;\nfun run(): int { biggest(Plain(), Plain()); return 1; }");

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
                    + "  public override fun compareTo(other: Score): int { return value - other.value; }\n"
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
                "import game.core.*;\n"
                    + "fun run(): int { let b: Box<Score> = Box(Score(3)); let s: Score = b.biggest(Score(4), Score(9)); return s.value; }");

            using var compilation = SurtrCompilation.Create(app);
            var binder = compilation.Bind();
            binder.BindBodies();
            Assert.False(compilation.HasErrors);
            var appEmitter = new ModuleEmitter(compilation, binder);
            Assert.True(appEmitter.TryEmit());
            runtime.LoadModule(appEmitter.Modules[0]);

            Assert.Equal(9, runtime.Invoke(Function(runtime, "game.util", "run"), Array.Empty<SurtrValue>()).AsInt);
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
                    + "  public override fun iterate(): IIterator<T> { return [_value].iterate(); }\n"
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
                    + "  public override fun iterate(): IIterator<int> { return [1, 2, 3].iterate(); }\n"
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
                    + "  public override fun compareTo(other: Score): int { return value - other.value; }\n"
                    + "}\n"
                    + "class Holder<T : IComparable<T>> {\n"
                    + "  public var item: T;\n"
                    + "  constructor(item: T) { this.item = item; }\n"
                    + "  public fun corrupt(s: Score): void { this.item = s; }\n"
                    + "}");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CannotConvert);
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

            runtime.DefineNativeBody("game.core.get_ScreenWidth", SurtrNativeEntryPoint.FromFunctionPointer(&GetScreenWidth));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(1280, Int(runtime, "run"));
        }

        private static SurtrValue GetScreenWidth(SurtrCallArguments arguments) => SurtrValue.CreateInt(1280);

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
            runtime.DefineNativeBody("game.core.get_TimeScale", SurtrNativeEntryPoint.FromFunctionPointer(&GetTimeScale));
            runtime.DefineNativeBody("game.core.set_TimeScale", SurtrNativeEntryPoint.FromFunctionPointer(&SetTimeScale));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(1, Int(runtime, "run"));
            Assert.Equal(0.5, _writtenTimeScale);
        }

        // A plain static field, not a closure capture: SurtrNativeEntryPoint.FromFunctionPointer
        // needs a static method with no captured state.
        private static double? _writtenTimeScale;
        private static SurtrValue GetTimeScale(SurtrCallArguments arguments) => SurtrValue.CreateFloat(_writtenTimeScale ?? 0.0);
        private static SurtrValue SetTimeScale(SurtrCallArguments arguments)
        {
            _writtenTimeScale = arguments.GetFloat(0);
            return SurtrValue.Null;
        }

        [Fact]
        public unsafe void ANativeFunctionCallReachesTheHostsBody()
        {
            var emitter = Build("native fun hostSquare(value: int): int;\nfun run(): int { return hostSquare(3); }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            runtime.DefineNativeBody("game.core.hostSquare", SurtrNativeEntryPoint.FromFunctionPointer(&Square));

            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(9, Int(runtime, "run"));
        }

        // A module-level native takes no receiver, so its first declared parameter is argument zero.
        private static SurtrValue Square(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetInt(0) * arguments.GetInt(0));

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

            runtime.DefineNativeBody("game.core.load", SurtrNativeEntryPoint.FromFunctionPointer(&FirstLoad));
            runtime.DefineNativeBody("other.util.load", SurtrNativeEntryPoint.FromFunctionPointer(&SecondLoad));

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            Assert.Equal(1, Int(runtime, "run"));

            Assert.True(runtime.TryGetModule("other.util", out var other));
            Assert.True(other.TryGetMethods("run", out var runOverloads));
            Assert.Equal(2, runtime.Invoke(runOverloads[0]).AsInt);
        }

        private static SurtrValue FirstLoad(SurtrCallArguments arguments) => SurtrValue.CreateInt(1);

        private static SurtrValue SecondLoad(SurtrCallArguments arguments) => SurtrValue.CreateInt(2);
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
            runtime.DefineNativeBody("game.core:Sprite.doubled", SurtrNativeEntryPoint.FromFunctionPointer(&DoubleSecondArgument));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(42, Int(runtime, "run"));
        }

        private static SurtrValue DoubleSecondArgument(SurtrCallArguments arguments) => SurtrValue.CreateInt(arguments.GetInt(1) * 2);

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
            runtime.DefineNativeBody("game.core:MathHost.triple", SurtrNativeEntryPoint.FromFunctionPointer(&TripleFirstArgument));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(21, Int(runtime, "run"));
        }

        private static SurtrValue TripleFirstArgument(SurtrCallArguments arguments) => SurtrValue.CreateInt(arguments.GetInt(0) * 3);

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
            runtime.DefineNativeBody("game.core:Box.get_value", SurtrNativeEntryPoint.FromFunctionPointer(&GetBoxValue));
            runtime.DefineNativeBody("game.core:Box.set_value", SurtrNativeEntryPoint.FromFunctionPointer(&SetBoxValue));
            runtime.LoadModule(emitter.Modules[0]);

            // +1000 on the read is deliberate: a write-then-read through an *ordinary* auto-property
            // (a real backing field, the exact shape this used to be silently downgraded to before
            // the fix - ModuleEmitter.DeclareProperty never checked IsNative) would echo back plain
            // 5, passing even though no host code ever ran. Only a genuine call into GetBoxValue can
            // produce 1005.
            Assert.Equal(1005, Int(runtime, "run"));
        }

        private static int _boxValue;
        private static SurtrValue GetBoxValue(SurtrCallArguments arguments) => SurtrValue.CreateInt(_boxValue + 1000);
        private static SurtrValue SetBoxValue(SurtrCallArguments arguments)
        {
            _boxValue = arguments.GetInt(1);
            return SurtrValue.Null;
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

            runtime.DefineNativeBody("game.core:Foo.get_x", SurtrNativeEntryPoint.FromFunctionPointer(&GetNinetyNine));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(99, Int(runtime, "run"));
        }

        private static SurtrValue GetNinetyNine(SurtrCallArguments arguments) => SurtrValue.CreateInt(99);

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
            runtime.DefineNativeBody("game.core:Foo.get_x", SurtrNativeEntryPoint.FromFunctionPointer(&GetFooX));
            runtime.DefineNativeBody("game.core:Foo.set_x", SurtrNativeEntryPoint.FromFunctionPointer(&SetFooX));
            runtime.LoadModule(emitter.Modules[0]);

            // +2000, for the same reason ANativeInstancePropertyInsideAClassReachesTheHostsBody
            // offsets its read: an ordinary auto-property's backing field would echo back plain 7.
            Assert.Equal(2007, Int(runtime, "run"));
        }

        private static int _fooX;
        private static SurtrValue GetFooX(SurtrCallArguments arguments) => SurtrValue.CreateInt(_fooX + 2000);
        private static SurtrValue SetFooX(SurtrCallArguments arguments)
        {
            _fooX = arguments.GetInt(1);
            return SurtrValue.Null;
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
            runtime.DefineNativeBody("game.core:Config.get_x", SurtrNativeEntryPoint.FromFunctionPointer(&GetFiftyFive));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(55, Int(runtime, "run"));
        }

        private static SurtrValue GetFiftyFive(SurtrCallArguments arguments) => SurtrValue.CreateInt(55);

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

            runtime.DefineNativeBody("game.core:EntityId.validate", SurtrNativeEntryPoint.FromFunctionPointer(&AlwaysTrue));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(1, Int(runtime, "run"));
        }

        private static SurtrValue AlwaysTrue(SurtrCallArguments arguments) => SurtrValue.CreateBool(true);

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

            runtime.DefineNativeBody("game.core:Suit.describe", SurtrNativeEntryPoint.FromFunctionPointer(&GetSeven));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(7, Int(runtime, "run"));
        }

        private static SurtrValue GetSeven(SurtrCallArguments arguments) => SurtrValue.CreateInt(7);

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
            runtime.DefineNativeBody("game.core:Outer.Inner.ping", SurtrNativeEntryPoint.FromFunctionPointer(&GetThree));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(3, Int(runtime, "run"));
        }

        private static SurtrValue GetThree(SurtrCallArguments arguments) => SurtrValue.CreateInt(3);

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
                ("/game/util/M.surtr", "import game.core.*;\npublic extension Vec2 { fun bonus(v: Vec2): int => 1; }"));

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
                    + "  public override fun compareTo(other: Score): int { return value - other.value; }\n"
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
        #endregion
    }
}
