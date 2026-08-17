#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.IO;

namespace Surtr.Tests.Runtime
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
        /// the load, so a call is a real call into <c>SurtrStandardLibrary.MathSin</c> and friends.
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
    }
}