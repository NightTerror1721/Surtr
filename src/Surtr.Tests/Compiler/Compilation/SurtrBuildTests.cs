#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.IO;
using System.Linq;

namespace Surtr.Tests.Compiler.Compilation
{
    /// <summary>
    /// §14.2's build model: a directory of <c>.surtr</c> files in, loadable <c>.surtrc</c> images out.
    /// </summary>
    /// <remarks>
    /// These are the one part of the compiler that touches a file system, so they are the one part
    /// that needs a real one. Each test writes its own tree under a directory of its own and deletes
    /// it afterwards, so two of them can never see each other's files.
    /// </remarks>
    public sealed class SurtrBuildTests : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "surtr-build-tests",
            Guid.NewGuid().ToString("N"));

        private readonly System.Collections.Generic.List<IDisposable> _owned =
            new System.Collections.Generic.List<IDisposable>();

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();

            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        private string Tree(string name, params (string Path, string Text)[] files)
        {
            string directory = Path.Combine(_root, name);

            foreach (var (path, text) in files)
            {
                string full = Path.Combine(directory, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }

            return directory;
        }

        private SurtrRuntime Load(SurtrBuild build)
        {
            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            foreach (string written in build.Written)
                runtime.LoadModule(SurtrModuleImage.FromBytes(File.ReadAllBytes(written)).Instantiate());

            return runtime;
        }

        private static SurtrMethodInfo Function(SurtrRuntime runtime, string modulePath, string name)
        {
            Assert.True(runtime.TryGetModule(modulePath, out var module), $"No module '{modulePath}' was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"'{modulePath}' declares no '{name}'.");
            return overloads[0];
        }

        [Fact]
        public void ATreeOfTwoModulesBuildsAndRuns()
        {
            string tree = Tree(
                "two",
                ("game.surtrproj", "root = src\nmodule = game\noutput = out\n"),
                ("src/util/Math.surtr", "public fun twice(x: int): int { return x + x; }"),
                ("src/core/Entity.surtr", "import game.util.*;\npublic fun doubled(n: int): int { return twice(n); }"));

            var build = SurtrBuild.Run(Path.Combine(tree, "game.surtrproj"));

            Assert.False(build.Failed, string.Join("; ", build.Diagnostics.Select(d => d.ToString())));
            Assert.Equal(2, build.Written.Count);

            var runtime = Load(build);
            Assert.Equal(84, runtime.Invoke(Function(runtime, "game.core.Entity", "doubled"), SurtrValue.CreateInt(42)).AsInt);
        }

        /// <summary>§7.4: the build is where a `const if` gets its facts from.</summary>
        [Fact]
        public void ABuildConstantPicksTheBranch()
        {
            string tree = Tree(
                "flavour",
                ("p.surtrproj", "root = src\nmodule = game\noutput = out\ndefine Debug = false\n"),
                ("src/core/M.surtr",
                    "const if (Debug) { public fun mode(): string { return \"debug\"; } }\n"
                        + "else { public fun mode(): string { return \"release\"; } }\n"));

            var build = SurtrBuild.Run(Path.Combine(tree, "p.surtrproj"));
            Assert.False(build.Failed, string.Join("; ", build.Diagnostics.Select(d => d.ToString())));

            var runtime = Load(build);
            var text = runtime.Resolve<SurtrString>(runtime.Invoke(Function(runtime, "game.core.M", "mode")));

            Assert.Equal("release", text!.Text);
        }

        /// <summary>
        /// A module compiled by an earlier build, referenced as an image — which is what
        /// <c>reference</c> is for, and what a cross-module call through the module reference table
        /// has to reach.
        /// </summary>
        [Fact]
        public void AnImageBuiltEarlierCanBeReferenced()
        {
            string library = Tree(
                "lib",
                ("lib.surtrproj", "root = src\nmodule = lib\noutput = out\n"),
                ("src/math/M.surtr", "public fun square(x: int): int { return x * x; }"));

            var built = SurtrBuild.Run(Path.Combine(library, "lib.surtrproj"));
            Assert.False(built.Failed, string.Join("; ", built.Diagnostics.Select(d => d.ToString())));

            string application = Tree(
                "app",
                ("app.surtrproj",
                    "root = src\nmodule = app\noutput = out\nreference \"" + built.Written[0].Replace('\\', '/') + "\"\n"),
                ("src/core/M.surtr", "import lib.math.M;\npublic fun run(): int { return square(3); }"));

            var build = SurtrBuild.Run(Path.Combine(application, "app.surtrproj"));
            Assert.False(build.Failed, string.Join("; ", build.Diagnostics.Select(d => d.ToString())));

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            runtime.LoadModule(SurtrModuleImage.FromBytes(File.ReadAllBytes(built.Written[0])).Instantiate());

            foreach (string written in build.Written)
                runtime.LoadModule(SurtrModuleImage.FromBytes(File.ReadAllBytes(written)).Instantiate());

            Assert.Equal(9, runtime.Invoke(Function(runtime, "app.core.M", "run")).AsInt);
        }

        /// <summary>A failed build writes nothing: half a module set is worse than none.</summary>
        [Fact]
        public void ASourceErrorWritesNothing()
        {
            string tree = Tree(
                "broken",
                ("p.surtrproj", "root = src\nmodule = game\noutput = out\n"),
                ("src/core/M.surtr", "public fun run(): int { return nope; }"));

            var build = SurtrBuild.Run(Path.Combine(tree, "p.surtrproj"));

            Assert.True(build.Failed);
            Assert.Empty(build.Written);
            Assert.False(Directory.Exists(Path.Combine(tree, "out")));
        }

        /// <summary>§2.1: each file is its own module, so two files in one directory are two images.</summary>
        [Fact]
        public void FilesInOneDirectoryAreDistinctImages()
        {
            string tree = Tree(
                "named",
                ("p.surtrproj", "root = src\nmodule = game\noutput = out\n"),
                ("src/core/A.surtr", "public fun a(): int { return 1; }"),
                ("src/core/B.surtr", "public fun b(): int { return 2; }"));

            var build = SurtrBuild.Run(Path.Combine(tree, "p.surtrproj"));

            Assert.False(build.Failed, string.Join("; ", build.Diagnostics.Select(d => d.ToString())));
            var names = build.Written.Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.Equal(new[] { "game.core.A.surtrc", "game.core.B.surtrc" }, names);
        }

        [Fact]
        public void ASourceTreeBuildsWithNoProjectFile()
        {
            string tree = Tree("bare", ("src/core/M.surtr", "public fun run(): int { return 5; }"));

            var build = SurtrBuild.Run(Path.Combine(tree, "src"), Path.Combine(tree, "out"), rootModulePath: "game");
            Assert.False(build.Failed, string.Join("; ", build.Diagnostics.Select(d => d.ToString())));

            var runtime = Load(build);
            Assert.Equal(5, runtime.Invoke(Function(runtime, "game.core.M", "run")).AsInt);
        }

        [Fact]
        public void AMissingProjectFileIsReported()
        {
            var build = SurtrBuild.Run(Path.Combine(_root, "nothing.surtrproj"));

            Assert.True(build.Failed);
            Assert.Contains(build.Diagnostics, d => d.Code == SurtrDiagnosticCode.ProjectFileInvalid);
        }

        [Fact]
        public void ADirectiveTheBuildDoesNotUnderstandIsReported()
        {
            string tree = Tree(
                "bad",
                ("p.surtrproj", "root = src\nnonsense = 1\n"),
                ("src/core/M.surtr", "public fun run(): int { return 1; }"));

            var build = SurtrBuild.Run(Path.Combine(tree, "p.surtrproj"));

            Assert.True(build.Failed);
            Assert.Contains(build.Diagnostics, d => d.Code == SurtrDiagnosticCode.ProjectFileInvalid);
        }

        /// <summary>A build constant is typed the way a literal is (§5.8), since §7.4 makes it one.</summary>
        [Fact]
        public void ProjectFileConstantsAreTypedLikeLiterals()
        {
            string tree = Tree(
                "typed",
                ("p.surtrproj", "define Flag\ndefine Name = \"x\"\ndefine Count = 3\ndefine Scale = 1.5\ndefine Off = false\n"));

            var diagnostics = new SurtrDiagnosticBag();
            var file = SurtrProjectFile.Read(Path.Combine(tree, "p.surtrproj"), diagnostics);

            Assert.False(diagnostics.HasErrors);
            Assert.Equal(BuildConstantKind.Bool, file.Constants["Flag"].Kind);
            Assert.Equal(true, file.Constants["Flag"].Value);
            Assert.Equal(BuildConstantKind.String, file.Constants["Name"].Kind);
            Assert.Equal(BuildConstantKind.Int, file.Constants["Count"].Kind);
            Assert.Equal(BuildConstantKind.Float, file.Constants["Scale"].Kind);
            Assert.Equal(false, file.Constants["Off"].Value);
        }

        [Fact]
        public void AProjectFileTakesDefaultsForWhatItDoesNotSay()
        {
            string tree = Tree("defaults", ("p.surtrproj", "# nothing but a comment\n"));

            var diagnostics = new SurtrDiagnosticBag();
            var file = SurtrProjectFile.Read(Path.Combine(tree, "p.surtrproj"), diagnostics);

            Assert.False(diagnostics.HasErrors);
            Assert.Equal("src", file.Root);
            Assert.Equal("build", file.Output);
            Assert.Equal(string.Empty, file.RootModulePath);
        }

        /// <summary>
        /// An empty source tree is only a warning (<c>SurtrBuild.Run</c> reports "holds no .surtr
        /// files" as a <c>ReportWarning</c>), so it is the one built-in warning every build can
        /// trigger on demand - exactly what's needed to exercise <c>warningsAsErrors</c>/
        /// <c>suppress</c> end to end without needing a source-level warning of the front end's own.
        /// </summary>
        [Fact]
        public void WarningsAsErrors_TurnsTheEmptySourceTreeWarningIntoAFailure()
        {
            string tree = Tree("empty-strict", ("p.surtrproj", "root = src\nmodule = game\noutput = out\nwarningsAsErrors = true\n"));
            Directory.CreateDirectory(Path.Combine(tree, "src"));

            var build = SurtrBuild.Run(Path.Combine(tree, "p.surtrproj"));

            Assert.True(build.Failed);
            Assert.Contains(build.Diagnostics, d => d.Code == SurtrDiagnosticCode.ProjectFileInvalid && d.IsError);
        }

        [Fact]
        public void Suppress_DropsTheNamedDiagnosticEntirely()
        {
            string tree = Tree("empty-suppressed", ("p.surtrproj", "root = src\nmodule = game\noutput = out\nsuppress ProjectFileInvalid\n"));
            Directory.CreateDirectory(Path.Combine(tree, "src"));

            var build = SurtrBuild.Run(Path.Combine(tree, "p.surtrproj"));

            Assert.False(build.Failed);
            Assert.DoesNotContain(build.Diagnostics, d => d.Code == SurtrDiagnosticCode.ProjectFileInvalid);
        }

        /// <summary>
        /// <see cref="SurtrBuild.RunIncremental(string, IIncrementalBuildCache)"/> is a different
        /// path through the compiler (a throwaway dependency-discovery pass, then a second,
        /// possibly-smaller compilation) - the correctness bar for that difference is that a cold
        /// cache produces byte-for-byte the same images as the ordinary, non-incremental build.
        /// </summary>
        [Fact]
        public void RunIncremental_WithAColdCache_MatchesTheOrdinaryBuildByteForByte()
        {
            string tree = Tree(
                "incremental-parity",
                ("game.surtrproj", "root = src\nmodule = game\noutput = out\n"),
                ("src/util/Math.surtr", "public fun twice(x: int): int { return x + x; }"),
                ("src/core/Entity.surtr", "import game.util.*;\npublic fun doubled(n: int): int { return twice(n); }"));

            var ordinary = SurtrBuild.Run(Path.Combine(tree, "game.surtrproj"));
            Assert.False(ordinary.Failed, string.Join("; ", ordinary.Diagnostics.Select(d => d.ToString())));

            var incremental = SurtrBuild.RunIncremental(Path.Combine(tree, "game.surtrproj"), new InMemoryIncrementalBuildCache());
            Assert.False(incremental.Failed, string.Join("; ", incremental.Diagnostics.Select(d => d.ToString())));

            Assert.Equal(ordinary.Written.Count, incremental.Written.Count);

            var ordinaryByPath = ordinary.Written.ToDictionary(path => Path.GetFileName(path), path => File.ReadAllBytes(path));
            foreach (string incrementalFile in incremental.Written)
            {
                string name = Path.GetFileName(incrementalFile);
                Assert.True(ordinaryByPath.TryGetValue(name, out byte[]? expected), $"'{name}' was not written by the ordinary build.");
                Assert.Equal(expected, File.ReadAllBytes(incrementalFile));
            }
        }

        /// <summary>A second incremental build against the same cache, with no source changes, recompiles nothing and still runs correctly.</summary>
        [Fact]
        public void RunIncremental_ASecondCallWithNoChanges_StillProducesAWorkingBuild()
        {
            string tree = Tree(
                "incremental-warm",
                ("game.surtrproj", "root = src\nmodule = game\noutput = out\n"),
                ("src/util/Math.surtr", "public fun twice(x: int): int { return x + x; }"),
                ("src/core/Entity.surtr", "import game.util.*;\npublic fun doubled(n: int): int { return twice(n); }"));

            var cache = new InMemoryIncrementalBuildCache();
            var warmup = SurtrBuild.RunIncremental(Path.Combine(tree, "game.surtrproj"), cache);
            Assert.False(warmup.Failed, string.Join("; ", warmup.Diagnostics.Select(d => d.ToString())));

            var second = SurtrBuild.RunIncremental(Path.Combine(tree, "game.surtrproj"), cache);
            Assert.False(second.Failed, string.Join("; ", second.Diagnostics.Select(d => d.ToString())));

            var runtime = Load(second);
            Assert.Equal(84, runtime.Invoke(Function(runtime, "game.core.Entity", "doubled"), SurtrValue.CreateInt(42)).AsInt);
        }
    }
}
