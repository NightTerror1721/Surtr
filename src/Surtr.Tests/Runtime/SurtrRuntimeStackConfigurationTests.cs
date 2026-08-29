#nullable enable

using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;
using System;
using System.Linq;

namespace Surtr.Tests.Runtime
{
    /// <summary>
    /// Covers <see cref="SurtrRuntime.DataStackSlots"/>/<see cref="SurtrRuntime.MaxCallDepth"/>: the
    /// host-facing door onto <c>SurtrVirtualMachine</c>'s already-existing (but <c>internal</c>)
    /// configurable stack sizes, which is what lets a host sandboxing untrusted or size-sensitive
    /// script content bound how much memory one call budget can reach.
    /// </summary>
    public sealed class SurtrRuntimeStackConfigurationTests
    {
        private const string Root = "D:/proj/src";

        /// <summary>A module-level function that recurses forever, for forcing a stack trap on demand.</summary>
        private const string InfiniteRecursionSource = @"
fun recurse(n: int): int {
    return recurse(n + 1);
}
";

        private static SurtrMethodInfo LoadRecurse(SurtrRuntime runtime)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/Recurse.surtr", InfiniteRecursionSource);

            var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(!compilation.HasErrors, "Unexpected diagnostics: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var emitter = new ModuleEmitter(compilation, binder);
            Assert.True(emitter.TryEmit(), "Emission failed: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            Assert.True(runtime.TryGetModule("game.Recurse", out var loaded), "Module 'game.Recurse' did not load.");
            Assert.True(loaded.TryGetMethods("recurse", out var overloads));
            return overloads[0];
        }

        #region Defaults

        [Fact]
        public void ANewRuntime_UsesTheDocumentedDefaultsUntilConfigured()
        {
            using var runtime = new SurtrRuntime();
            Assert.Equal(SurtrRuntime.DefaultDataStackSlots, runtime.DataStackSlots);
            Assert.Equal(SurtrRuntime.DefaultMaxCallDepth, runtime.MaxCallDepth);
        }

        #endregion

        #region Configuration is honored

        [Fact]
        public void ASmallMaxCallDepth_TrapsAtExactlyTheConfiguredDepth()
        {
            using var runtime = new SurtrRuntime();
            runtime.MaxCallDepth = 8;

            var recurse = LoadRecurse(runtime);

            var exception = Assert.Throws<SurtrExecutionException>(
                () => runtime.Invoke(recurse, SurtrValue.CreateInt(0)));

            Assert.Contains("8", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Call stack overflow", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ASmallDataStackSlots_TrapsWithADataStackOverflow_BeforeTheDefaultCallDepthIsReached()
        {
            using var runtime = new SurtrRuntime();

            // 256 is SurtrVirtualMachine's own floor (MinimumDataStackSlots) - a handful of slots
            // per recursive call exhausts it long before the *default* call depth (1024) would ever
            // be reached, so the trap that fires proves the data stack, not the call stack, is what
            // was actually configured smaller here.
            runtime.DataStackSlots = 256;

            var recurse = LoadRecurse(runtime);

            var exception = Assert.Throws<SurtrExecutionException>(
                () => runtime.Invoke(recurse, SurtrValue.CreateInt(0)));

            Assert.Contains("Data stack overflow", exception.Message, StringComparison.Ordinal);
        }

        #endregion

        #region One-shot configuration

        [Fact]
        public void ConfiguringTheStack_AfterFirstExecution_Throws()
        {
            using var runtime = new SurtrRuntime();
            var recurse = LoadRecurse(runtime);

            // Any execution builds the machine lazily - a tiny, harmless call is enough to force it.
            runtime.MaxCallDepth = 64;
            Assert.Throws<SurtrExecutionException>(() => runtime.Invoke(recurse, SurtrValue.CreateInt(0)));

            Assert.Throws<InvalidOperationException>(() => runtime.MaxCallDepth = 32);
            Assert.Throws<InvalidOperationException>(() => runtime.DataStackSlots = 512);
        }

        #endregion
    }
}
