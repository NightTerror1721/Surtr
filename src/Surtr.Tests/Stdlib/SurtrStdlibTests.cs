#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Stdlib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Surtr.Tests.Stdlib
{
    /// <summary>
    /// The end of the stdlib story: the <c>.surtrc</c> images <c>Surtr.Stdlib.Tool</c> writes are
    /// loaded into a real runtime through <see cref="SurtrStdlib.LoadInto"/>, which publishes the
    /// native bodies first and then loads. These tests use the <em>committed</em> image
    /// <c>build/surtr.math.Math.surtrc</c>, not a freshly compiled one, so what they exercise is the
    /// transport path — bytes on disk → <see cref="SurtrModuleImage"/> → link-name binding →
    /// module load — exactly what a host that ships the stdlib as embedded resources will do.
    /// </summary>
    public class SurtrStdlibTests
    {
        /// <summary>Loads the real <c>surtr.math.Math</c> image through the stdlib loader.</summary>
        private static SurtrModuleImage MathImage()
            => SurtrModuleImage.FromBytes(File.ReadAllBytes(RepoRoot() + "/src/Surtr.Stdlib/build/surtr.math.Math.surtrc"));

        /// <summary>Every image <c>Surtr.Stdlib.Tool</c> actually compiled, from the committed build output.</summary>
        private static List<SurtrModuleImage> AllImages()
        {
            string buildDirectory = RepoRoot() + "/src/Surtr.Stdlib/build";
            var images = new List<SurtrModuleImage>();

            foreach (string path in Directory.GetFiles(buildDirectory, "*" + SurtrModuleImage.FileExtension))
                images.Add(SurtrModuleImage.FromBytes(File.ReadAllBytes(path)));

            return images;
        }

        /// <summary>
        /// The flat, sorted list of native link names <c>Surtr.Stdlib.Tool</c> wrote alongside the
        /// images - what a build-time drift check would compare against
        /// <see cref="SurtrStdlib.RegisterNativeBodies"/>.
        /// </summary>
        private static string[] NativeLinkNameManifest()
            => File.ReadAllLines(RepoRoot() + "/src/Surtr.Stdlib/build/native-link-names.txt");

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

        private static SurtrMethodInfo Function(SurtrRuntime runtime, string modulePath, string name)
        {
            Assert.True(runtime.TryGetModule(modulePath, out var module), $"No module '{modulePath}' was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"'{modulePath}' declares no '{name}'.");
            return overloads[0];
        }

        /// <summary>
        /// The module-level <c>native fun</c> declarations in <c>Math.surtr</c> reach their C#
        /// bodies: <see cref="SurtrStdlib.LoadInto"/> published them under their link names before
        /// the load, so a call is a real call into <c>Surtr.Stdlib.Native.SurtrMathNative</c> and friends.
        /// </summary>
        [Fact]
        public void AModuleLevelNativeReachesTheBodyTheLoaderPublished()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadInto(runtime, new[] { MathImage() });

            Assert.Equal(3.0, runtime.Invoke(Function(runtime, "surtr.math.Math", "floor"), SurtrValue.CreateFloat(3.7)).AsFloat);
            Assert.Equal(0.0, runtime.Invoke(Function(runtime, "surtr.math.Math", "sin"), SurtrValue.CreateFloat(0.0)).AsFloat);
        }

        /// <summary>
        /// The newly added member goes through the same chain as the rest: declared in
        /// <c>Math.surtr</c>, travelling as link name <c>hypot</c>, published by the loader.
        /// </summary>
        [Fact]
        public void ANativeAddedToTheStdlibBindsLikeAnyOther()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadInto(runtime, new[] { MathImage() });

            Assert.Equal(
                5.0,
                runtime.Invoke(Function(runtime, "surtr.math.Math", "hypot"), SurtrValue.CreateFloat(3.0), SurtrValue.CreateFloat(4.0)).AsFloat);
        }

        /// <summary>
        /// The pure-Surtr half of the module — functions with compiled bodies, not native ones —
        /// loads and runs beside the natives in the same module, no seam between them (§10).
        /// </summary>
        [Fact]
        public void ACompiledFunctionRunsBesideTheNatives()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadInto(runtime, new[] { MathImage() });

            Assert.Equal(1.0, runtime.Invoke(Function(runtime, "surtr.math.Math", "clamp01"), SurtrValue.CreateFloat(1.5)).AsFloat);
            Assert.Equal(System.Math.PI, runtime.Invoke(Function(runtime, "surtr.math.Math", "degreesToRadians"), SurtrValue.CreateFloat(180.0)).AsFloat, 5);
        }

        /// <summary>
        /// The failure a loader that forgets a body produces: loading the image into a runtime that
        /// never published one throws, naming the missing link name — the same guard every other
        /// <c>native</c> declaration gets at load.
        /// </summary>
        [Fact]
        public void TheImageFailsToLoadWithoutThePublishedBodies()
        {
            using var runtime = new SurtrRuntime();

            var error = Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(MathImage()));
            Assert.Contains("surtr.math.Math", error.Message, StringComparison.Ordinal);
            Assert.Contains("DefineNativeBody", error.Message, StringComparison.Ordinal);
        }

        /// <summary>The byte[] overload is the shape embedded stdlib resources arrive in.</summary>
        [Fact]
        public void LoadIntoAcceptsRawImageBytes()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadInto(runtime, new[] { MathImage().ToBytes() });

            Assert.Equal(
                5.0,
                runtime.Invoke(Function(runtime, "surtr.math.Math", "hypot"), SurtrValue.CreateFloat(3.0), SurtrValue.CreateFloat(4.0)).AsFloat);
        }

        /// <summary>Two runtimes can each load the same stdlib image; each binds its own bodies.</summary>
        [Fact]
        public void OneImageLoadsIntoManyRuntimes()
        {
            using var first = new SurtrRuntime();
            using var second = new SurtrRuntime();
            SurtrStdlib.LoadInto(first, new[] { MathImage() });
            SurtrStdlib.LoadInto(second, new[] { MathImage() });

            Assert.Equal(
                5.0,
                first.Invoke(Function(first, "surtr.math.Math", "hypot"), SurtrValue.CreateFloat(3.0), SurtrValue.CreateFloat(4.0)).AsFloat);
            Assert.Equal(
                4.0,
                second.Invoke(Function(second, "surtr.math.Math", "hypot"), SurtrValue.CreateFloat(0.0), SurtrValue.CreateFloat(4.0)).AsFloat);
        }

        /// <summary>
        /// A sandboxed host asking for only <c>Math</c> gets exactly the modules under
        /// <c>surtr/math/</c> - nothing from <c>core</c>, <c>collections</c> or <c>text</c>.
        /// </summary>
        [Fact]
        public void SelectiveLoadOnlyLoadsTheChosenCategory()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadInto(runtime, AllImages(), StdlibModules.Math);

            Assert.True(runtime.TryGetModule("surtr.math.Math", out _));
            Assert.True(runtime.TryGetModule("surtr.math.Angle", out _));
            Assert.False(runtime.TryGetModule("surtr.core.Exception", out _));
            Assert.False(runtime.TryGetModule("surtr.collections.List", out _));
            Assert.False(runtime.TryGetModule("surtr.collections.Collection", out _));
            Assert.False(runtime.TryGetModule("surtr.text.StringBuilder", out _));
        }

        /// <summary>Two categories together bring in exactly their union, nothing from a third.</summary>
        [Fact]
        public void SelectiveLoadUnionsTheChosenCategories()
        {
            // Text is not self-sufficient on its own since Regex was added (it needs List/Stack
            // from Collections, and Result from Core - see StdlibModules.Text's own doc comment),
            // so this now selects three categories, not two, and asserts what Text now drags in
            // rather than that it doesn't.
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadInto(runtime, AllImages(), StdlibModules.Math | StdlibModules.Text | StdlibModules.Collections | StdlibModules.Core);

            Assert.True(runtime.TryGetModule("surtr.math.Math", out _));
            Assert.True(runtime.TryGetModule("surtr.text.StringBuilder", out _));
            Assert.True(runtime.TryGetModule("surtr.text.Regex", out _));
            Assert.True(runtime.TryGetModule("surtr.collections.List", out _));
            Assert.True(runtime.TryGetModule("surtr.core.Result", out _));
            Assert.False(runtime.TryGetModule("surtr.diagnostics.Assert", out _));
        }

        /// <summary><see cref="StdlibModules.All"/> loads everything, matching the unfiltered overload.</summary>
        [Fact]
        public void SelectiveLoadAllMatchesTheUnfilteredOverload()
        {
            using var runtime = new SurtrRuntime();
            var images = AllImages();
            SurtrStdlib.LoadInto(runtime, images, StdlibModules.All);

            foreach (var image in images)
                Assert.True(runtime.TryGetModule(image.Path, out _), $"'{image.Path}' should have loaded under StdlibModules.All.");
        }

        /// <summary>The selective overload also takes raw bytes, the shape embedded resources arrive in.</summary>
        [Fact]
        public void SelectiveLoadAcceptsRawImageBytes()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadInto(runtime, AllImages().Select(image => image.ToBytes()), StdlibModules.Collections);

            Assert.True(runtime.TryGetModule("surtr.collections.List", out _));
            Assert.False(runtime.TryGetModule("surtr.math.Math", out _));
        }

        /// <summary>
        /// The categories are independent: <c>Collections</c> alone loads
        /// <c>surtr.collections.Stack</c>, whose <c>pop()</c>/<c>peek()</c> throw the <em>built-in</em>
        /// <c>InvalidOperationException</c> - the trap-mapped class every file sees without an
        /// import - and not a twin declared in <c>surtr.core.Exception</c>. A same-named twin
        /// would split catch-by-type in two, so a driver compiled against the stdlib images that
        /// catches the built-in name must take the throw even with <c>surtr.core.Exception</c>
        /// never loaded. <c>Math</c> stays excluded either way.
        /// </summary>
        [Fact]
        public void SelectingCollectionsLoadsAloneAndStackThrowsTheBuiltInInvalidOperationException()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadInto(runtime, AllImages(), StdlibModules.Collections);

            Assert.True(runtime.TryGetModule("surtr.collections.Stack", out _));
            Assert.False(runtime.TryGetModule("surtr.core.Exception", out _));
            Assert.False(runtime.TryGetModule("surtr.math.Math", out _));
            Assert.Equal(7, PopEmptyUnderCatch(AllImages(), runtime));
        }

        /// <summary>Categories added for io/diagnostics: previously excluded by any selection narrower than <c>All</c>.</summary>
        [Fact]
        public void SelectingIo_LoadsOnlyTheIoCategory()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadInto(runtime, AllImages(), StdlibModules.Io);

            Assert.True(runtime.TryGetModule("surtr.io.Stream", out _));
            Assert.True(runtime.TryGetModule("surtr.io.Enums", out _));
            Assert.False(runtime.TryGetModule("surtr.collections.Stack", out _));
        }

        [Fact]
        public void SelectingDiagnostics_LoadsOnlyTheDiagnosticsCategory()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadInto(runtime, AllImages(), StdlibModules.Diagnostics);

            Assert.True(runtime.TryGetModule("surtr.diagnostics.Assert", out _));
            Assert.False(runtime.TryGetModule("surtr.math.Math", out _));
        }

        /// <summary>
        /// The predicate overload gives finer granularity than a whole category: exactly one module
        /// out of <c>Math</c> (<c>Angle</c>, which has no dependency on <c>Math</c> itself),
        /// without its sibling.
        /// </summary>
        [Fact]
        public void ThePredicateOverload_SelectsASingleModuleWithoutItsCategorySiblings()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadInto(runtime, AllImages(), (string path) => path == "surtr.math.Angle");

            Assert.True(runtime.TryGetModule("surtr.math.Angle", out _));
            Assert.False(runtime.TryGetModule("surtr.math.Math", out _));
        }

        /// <summary>
        /// Compiles and loads a driver over the given images and runs an empty-stack
        /// <c>pop()</c> under <c>catch (e: InvalidOperationException)</c>. The name binds to
        /// the built-in class - the driver imports nothing from <c>core</c> - so reaching the
        /// sentinel proves the throw and the catch name one and the same class. Compiling against
        /// referenced stdlib images is also what pins the importer fix: a module referenced as an
        /// image must not strip the implicitly-imported built-in library out of scope.
        /// </summary>
        private static int PopEmptyUnderCatch(List<SurtrModuleImage> images, SurtrRuntime runtime)
        {
            const string driver =
                "import surtr.collections.Stack;\n"
                + "fun popEmpty(): int {\n"
                + "    let s = Stack<int>();\n"
                + "    s.push(1);\n"
                + "    try { s.pop(); s.pop(); }\n"
                + "    catch (e: InvalidOperationException) { return 7; }\n"
                + "    return 0;\n"
                + "}\n";

            var project = new SurtrProject(sourceRoot: ".");
            project.AddSourceFile("driver.surtr", "driver", driver);

            foreach (var image in images)
                project.AddReference(image);

            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            if (compilation.Diagnostics.HasErrors)
                throw new InvalidOperationException(
                    "The stdlib driver does not compile: " + string.Join("; ", compilation.Diagnostics));

            var emitter = new ModuleEmitter(compilation, binder);
            foreach (var image in emitter.EmitImages())
                runtime.LoadModule(image);

            return runtime.Invoke(Function(runtime, "driver", "popEmpty")).AsInt;
        }

        /// <summary>
        /// The drift detector Fase 11 asks for: every native link name
        /// <c>Surtr.Stdlib.Tool</c> actually compiled into the committed build output is one
        /// <see cref="SurtrStdlib.RegisterNativeBodies"/> publishes. A <c>native fun</c> added to
        /// the stdlib source without a matching entry added there would fail this test instead of
        /// only failing once someone loads a runtime and hits the missing body.
        /// </summary>
        [Fact]
        public void EveryNativeLinkNameTheStdlibBuildCompiledIsRegistered()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.RegisterNativeBodies(runtime);

            foreach (string linkName in NativeLinkNameManifest())
            {
                Assert.True(
                    runtime.TryGetNativeBody(linkName, out _),
                    $"'{linkName}' is compiled into the stdlib build but SurtrStdlib.RegisterNativeBodies does not publish it.");
            }
        }

        /// <summary>
        /// <c>Surtr.Stdlib.csproj</c>'s <c>BuildStdlibImages</c> target embeds every <c>.surtrc</c>
        /// it writes as a resource of this very assembly, under
        /// <c>Surtr.Stdlib.Images.&lt;modulePath&gt;.surtrc</c>. This is the other half of that
        /// contract: one embedded resource per image the committed build output carries, with
        /// nothing missing and nothing extra.
        /// </summary>
        [Fact]
        public void TheAssemblyEmbedsOneResourcePerStdlibImage()
        {
            var assembly = typeof(SurtrStdlib).Assembly;
            var embedded = assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith("Surtr.Stdlib.Images.", StringComparison.Ordinal))
                .ToList();

            var expected = AllImages().Select(image => "Surtr.Stdlib.Images." + image.Path + SurtrModuleImage.FileExtension);

            Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), embedded.OrderBy(name => name, StringComparer.Ordinal));
        }

        /// <summary>
        /// The batteries-included entry point: no images to source or transport by hand, just a
        /// runtime. This is what a Unity host dropping <c>Surtr.Core.dll</c>/<c>Surtr.Stdlib.dll</c>
        /// into <c>Assets/Plugins</c> and calling <see cref="SurtrStdlib.LoadAll(SurtrRuntime)"/> gets.
        /// </summary>
        [Fact]
        public void LoadAllLoadsEveryEmbeddedImage()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadAll(runtime);

            foreach (var image in AllImages())
                Assert.True(runtime.TryGetModule(image.Path, out _), $"'{image.Path}' should have loaded under LoadAll.");

            Assert.Equal(
                5.0,
                runtime.Invoke(Function(runtime, "surtr.math.Math", "hypot"), SurtrValue.CreateFloat(3.0), SurtrValue.CreateFloat(4.0)).AsFloat);
        }

        /// <summary>The <see cref="StdlibModules"/> overload of <see cref="SurtrStdlib.LoadAll(SurtrRuntime, StdlibModules)"/> filters the embedded set the same way the explicit-images overload filters a caller-supplied one.</summary>
        [Fact]
        public void LoadAllWithSelectionOnlyLoadsTheChosenCategory()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadAll(runtime, StdlibModules.Math);

            Assert.True(runtime.TryGetModule("surtr.math.Math", out _));
            Assert.True(runtime.TryGetModule("surtr.math.Angle", out _));
            Assert.False(runtime.TryGetModule("surtr.core.Exception", out _));
            Assert.False(runtime.TryGetModule("surtr.collections.List", out _));
        }

        /// <summary>
        /// The <c>@Pure</c> mark on the standard library's pure functions (§P3) travels through the
        /// image: a user's <c>@Pure</c> body can call <c>Math.max</c> without tripping the purity
        /// contract check, because the imported method carries the mark.
        /// </summary>
        [Fact]
        public void ThePureMarkSurvivesIntoTheMathImage()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadInto(runtime, new[] { MathImage() });

            Assert.True(
                Function(runtime, "surtr.math.Math", "max").TryGetAttribute(SurtrBuiltIns.Pure, out _),
                "Math.max should carry @Pure in the committed image.");
        }
    }
}
