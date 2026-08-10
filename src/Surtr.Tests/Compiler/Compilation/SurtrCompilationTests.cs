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
