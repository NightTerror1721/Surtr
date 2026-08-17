#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using System.Linq;

namespace Surtr.Tests.Compiler.Compilation
{
    /// <summary>
    /// Covers everything that has to be settled before binding: which module each file belongs to,
    /// what order the modules load in, and what already-built metadata is in scope.
    /// </summary>
    public sealed class SurtrCompilationTests
    {
        private const string Root = "D:/proj/src";

        private static SurtrProject Project(string rootModulePath = "") => new SurtrProject(Root, rootModulePath);

        #region Grouping
        [Fact]
        public void FilesInOneDirectoryBecomeOneModule()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/game/core/Entity.surtr", "class Entity { }")
                .AddSourceFile(Root + "/game/core/World.surtr", "class World { }")
                .AddSourceFile(Root + "/game/math/Vec2.surtr", "class Vec2 { }"));

            Assert.False(compilation.HasErrors);
            Assert.Equal(2, compilation.Modules.Count);
            Assert.Equal(2, compilation.Modules["game.core"].Units.Count);
            Assert.Single(compilation.Modules["game.math"].Units);
        }

        [Fact]
        public void AFileWithNoModuleIsReportedAndSkipped()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/Loose.surtr", "class Loose { }")
                .AddSourceFile(Root + "/game/Entity.surtr", "class Entity { }"));

            Assert.True(compilation.HasErrors);
            Assert.Equal(SurtrDiagnosticCode.InvalidModulePath, compilation.Diagnostics[0].Code);

            // The rest of the project still compiles: one bad file is not a reason to stop.
            Assert.Single(compilation.Modules);
            Assert.True(compilation.Modules.ContainsKey("game"));
        }

        [Fact]
        public void ADirectoryThatIsNotAnIdentifierIsReported()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/my-module/Entity.surtr", "class Entity { }"));

            Assert.True(compilation.HasErrors);
            Assert.Equal(SurtrDiagnosticCode.InvalidModulePath, compilation.Diagnostics[0].Code);
        }
        #endregion

        #region Diagnostics
        [Fact]
        public void ParseErrorsLandInTheSameBagAsEverythingElse()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/game/Broken.surtr", "class {{{ "));

            Assert.True(compilation.HasErrors);

            // A syntax error is a 2xxx code and a module-path one is 3xxx; both arrive here.
            Assert.Contains(compilation.Diagnostics, d => (int)d.Code >= 2000 && (int)d.Code < 3000);
        }

        [Fact]
        public void ACleanProjectReportsNothing()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/game/Entity.surtr", "class Entity { public var id: int = 0; }"));

            Assert.Empty(compilation.Diagnostics);
            Assert.False(compilation.HasErrors);
        }
        #endregion

        #region Dependencies
        [Fact]
        public void AnImportBecomesADependencyEdge()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/game/math/Vec2.surtr", "class Vec2 { }")
                .AddSourceFile(Root + "/game/core/Entity.surtr", "import game.math.Vec2;\nclass Entity { }"));

            Assert.False(compilation.HasErrors);
            Assert.Contains("game.math", compilation.Dependencies.DependenciesOf("game.core"));
        }

        [Fact]
        public void AWildcardImportNamesTheModuleOutright()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/game/math/Vec2.surtr", "class Vec2 { }")
                .AddSourceFile(Root + "/game/core/Entity.surtr", "import game.math.*;\nclass Entity { }"));

            Assert.False(compilation.HasErrors);
            Assert.Contains("game.math", compilation.Dependencies.DependenciesOf("game.core"));
        }

        [Fact]
        public void ADependencyLoadsBeforeWhatDependsOnIt()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/game/math/Vec2.surtr", "class Vec2 { }")
                .AddSourceFile(Root + "/game/core/Entity.surtr", "import game.math.Vec2;\nclass Entity { }")
                .AddSourceFile(Root + "/app/Main.surtr", "import game.core.Entity;\nclass Main { }"));

            Assert.False(compilation.HasErrors);

            var order = compilation.LoadOrder.Select(m => m.Path).ToList();
            Assert.Equal(new[] { "game.math", "game.core", "app" }, order);
        }

        [Fact]
        public void AnImportOfSomethingNothingProvidesIsReported()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/game/Entity.surtr", "import nowhere.At.All;\nclass Entity { }"));

            Assert.True(compilation.HasErrors);
            Assert.Equal(SurtrDiagnosticCode.UnresolvedImport, compilation.Diagnostics[0].Code);
        }

        [Fact]
        public void AnImportOfABuiltInContractResolves()
        {
            // The built-in module is process-wide and always reachable, so nothing has to
            // reference it for this to work.
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/game/Entity.surtr", "import surtr.IComparable;\nclass Entity { }"));

            Assert.False(compilation.HasErrors);
            Assert.Contains("surtr", compilation.Dependencies.DependenciesOf("game"));
        }

        [Fact]
        public void ACycleBetweenModulesIsReported()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/a/A.surtr", "import b.B;\nclass A { }")
                .AddSourceFile(Root + "/b/B.surtr", "import a.A;\nclass B { }"));

            Assert.True(compilation.HasErrors);
            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.ModuleCycle);

            // Nothing is ordered, because there is no order.
            Assert.Empty(compilation.LoadOrder);
        }

        [Fact]
        public void ACycleDiagnosticNamesTheModulesInvolved()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/a/A.surtr", "import b.B;\nclass A { }")
                .AddSourceFile(Root + "/b/B.surtr", "import a.A;\nclass B { }"));

            var cycle = compilation.Diagnostics.Single(d => d.Code == SurtrDiagnosticCode.ModuleCycle);

            Assert.Contains("a", cycle.Message);
            Assert.Contains("b", cycle.Message);
            Assert.Contains("->", cycle.Message);
        }

        [Fact]
        public void TwoFilesInOneModuleImportingEachOtherIsNotACycle()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/game/A.surtr", "import game.B;\nclass A { }")
                .AddSourceFile(Root + "/game/B.surtr", "import game.A;\nclass B { }"));

            Assert.False(compilation.HasErrors);
            Assert.Single(compilation.LoadOrder);
        }

        /// <summary>
        /// §2.6: a fully qualified name reaches a type with no <c>import</c> at all, and until this
        /// was fixed, that was the one path <see cref="ModuleDependencyGraph"/> never learned about —
        /// <see cref="Create"/> only scans <c>import</c> syntax, at parse time, before binding has
        /// resolved anything. <see cref="TypeResolver"/> now records the edge itself, the moment it
        /// resolves such a name — which only happens once binding runs, hence <c>Bind().BindBodies()</c>
        /// here rather than asserting straight after <c>Create</c>.
        /// </summary>
        [Fact]
        public void AFullyQualifiedReferenceWithNoImportBecomesADependencyEdgeOnceBound()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/game/math/Vec2.surtr", "public class Vec2 { }")
                .AddSourceFile(Root + "/game/core/Entity.surtr", "class Entity { public var p: game.math.Vec2; }"));

            // Create() alone never saw an import, so the edge does not exist yet.
            Assert.DoesNotContain("game.math", compilation.Dependencies.DependenciesOf("game.core"));

            compilation.Bind().BindBodies();

            Assert.False(compilation.HasErrors);
            Assert.Contains("game.math", compilation.Dependencies.DependenciesOf("game.core"));
        }

        /// <summary>
        /// The edge alone is not the fix — <see cref="Create"/>'s own <c>LoadOrder</c> was already
        /// computed before binding ran, so it still needs recomputing. This is what
        /// <c>CodeGen.ModuleEmitter</c> does, via <c>RefreshLoadOrder</c>, right before it starts
        /// emitting.
        /// </summary>
        [Fact]
        public void RefreshingTheLoadOrderPicksUpAFullyQualifiedDependency()
        {
            var compilation = SurtrCompilation.Create(Project()
                .AddSourceFile(Root + "/game/math/Vec2.surtr", "public class Vec2 { }")
                .AddSourceFile(Root + "/game/core/Entity.surtr", "class Entity { public var p: game.math.Vec2; }"));

            // Nothing connects the two modules yet, so their relative order is whichever the
            // alphabetical tie-break gives unconnected modules — "game.core" before "game.math".
            Assert.Equal(new[] { "game.core", "game.math" }, compilation.LoadOrder.Select(m => m.Path));

            compilation.Bind().BindBodies();
            compilation.RefreshLoadOrder();

            Assert.Equal(new[] { "game.math", "game.core" }, compilation.LoadOrder.Select(m => m.Path));
        }
        #endregion

        #region References
        [Fact]
        public void AReferencedModuleIsImportableAndOrdersFirst()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("game.math");
            builder.DefineClass("Vec2");
            runtime.LoadModule(builder.Build());

            Assert.True(runtime.TryGetModule("game.math", out var built));

            var compilation = SurtrCompilation.Create(Project()
                .AddReference(built)
                .AddSourceFile(Root + "/game/core/Entity.surtr", "import game.math.Vec2;\nclass Entity { }"));

            Assert.False(compilation.HasErrors);
            Assert.Contains("game.math", compilation.Dependencies.DependenciesOf("game.core"));

            // The referenced module is already built, so it has no source to order - only the
            // source module comes out.
            Assert.Equal(new[] { "game.core" }, compilation.LoadOrder.Select(m => m.Path));
        }

        [Fact]
        public void ABuildConstantIsCarriedOnTheProject()
        {
            var project = Project()
                .AddSourceFile(Root + "/game/Entity.surtr", "class Entity { }");

            project.Define("Debug", BuildConstant.Bool(true));
            project.Define("Platform", BuildConstant.String("IL2CPP"));

            var compilation = SurtrCompilation.Create(project);

            Assert.Equal(2, compilation.Project.BuildConstants.Count);
            Assert.Equal(BuildConstantKind.Bool, compilation.Project.BuildConstants["Debug"].Kind);
            Assert.Equal("IL2CPP", compilation.Project.BuildConstants["Platform"].Value);
        }

        [Fact]
        public void ABuildConstantCannotBeDefinedTwiceOrNamedIllegally()
        {
            var project = Project();
            project.Define("Debug", BuildConstant.Bool(true));

            Assert.Throws<System.ArgumentException>(() => project.Define("Debug", BuildConstant.Bool(false)));
            Assert.Throws<System.ArgumentException>(() => project.Define("my-flag", BuildConstant.Bool(true)));
        }
        #endregion
    }
}
