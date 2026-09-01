#nullable enable

using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using System;
using System.Linq;

namespace Surtr.Tests.Runtime
{
    /// <summary>
    /// Covers <see cref="SurtrRuntime.AllowedModulePrefixes"/>: a module outside the allowlist must
    /// fail in <see cref="SurtrRuntime.LoadModule(SurtrModule)"/> with
    /// <see cref="SurtrCapabilityDeniedException"/>, never reach the runtime's module table, and
    /// never be reachable by loading a different module of the same name later; the policy itself
    /// must be settable only before the first module load, the same one-shot rule
    /// <see cref="SurtrRuntime.DataStackSlots"/>/<see cref="SurtrRuntime.MaxCallDepth"/> already
    /// follow (see <c>SurtrRuntimeStackConfigurationTests</c>).
    /// </summary>
    public sealed class SurtrRuntimeCapabilitySandboxTests
    {
        private const string Root = "D:/proj/src";

        private static SurtrModule[] Compile(params (string path, string source)[] files)
        {
            var project = new SurtrProject(Root);
            foreach (var (path, source) in files)
                project.AddSourceFile(Root + "/" + path, source);

            var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(!compilation.HasErrors, "Unexpected diagnostics: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var emitter = new ModuleEmitter(compilation, binder);
            Assert.True(emitter.TryEmit(), "Emission failed: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            return emitter.Modules.ToArray();
        }

        #region No restriction by default

        [Fact]
        public void ANewRuntime_HasNoAllowedModulePrefixes_AndLoadsAnything()
        {
            using var runtime = new SurtrRuntime();
            Assert.Null(runtime.AllowedModulePrefixes);

            var modules = Compile(("foo/A.surtr", "fun ping(): int { return 1; }\n"));
            runtime.LoadModule(modules[0]);

            Assert.True(runtime.TryGetModule("foo.A", out _));
        }

        [Fact]
        public void AnEmptyAllowedModulePrefixesList_MeansUnrestricted_LikeNull()
        {
            using var runtime = new SurtrRuntime();
            runtime.AllowedModulePrefixes = Array.Empty<string>();

            var modules = Compile(("foo/A.surtr", "fun ping(): int { return 1; }\n"));
            runtime.LoadModule(modules[0]);

            Assert.True(runtime.TryGetModule("foo.A", out _));
        }

        #endregion

        #region Denial

        [Fact]
        public void AModuleOutsideTheAllowlist_ThrowsCapabilityDenied_AndNeverRegisters()
        {
            using var runtime = new SurtrRuntime();
            runtime.AllowedModulePrefixes = new[] { "foo" };

            // "foox" shares the string prefix "foo" but is not the same path segment - the boundary
            // this exercises is dotted-segment matching, not raw substring matching.
            var modules = Compile(("foox/B.surtr", "fun ping(): int { return 1; }\n"));

            Assert.Throws<SurtrCapabilityDeniedException>(() => runtime.LoadModule(modules[0]));
            Assert.False(runtime.TryGetModule("foox.B", out _));
        }

        [Fact]
        public void AModuleInsideTheAllowlist_LoadsNormally_ExactAndNestedPrefix()
        {
            using var runtime = new SurtrRuntime();
            runtime.AllowedModulePrefixes = new[] { "foo" };

            var modules = Compile(
                ("foo.surtr", "fun ping(): int { return 1; }\n"),
                ("foo/nested/C.surtr", "fun pong(): int { return 2; }\n"));

            foreach (var module in modules)
                runtime.LoadModule(module);

            Assert.True(runtime.TryGetModule("foo", out _));
            Assert.True(runtime.TryGetModule("foo.nested.C", out _));
        }

        #endregion

        #region One-shot configuration

        [Fact]
        public void ConfiguringAllowedModulePrefixes_AfterFirstLoadModuleCall_Throws()
        {
            using var runtime = new SurtrRuntime();
            var modules = Compile(("foo/A.surtr", "fun ping(): int { return 1; }\n"));
            runtime.LoadModule(modules[0]);

            Assert.Throws<InvalidOperationException>(() => runtime.AllowedModulePrefixes = new[] { "foo" });
        }

        [Fact]
        public void ADeniedLoadAttempt_StillLocksThePolicy()
        {
            using var runtime = new SurtrRuntime();
            runtime.AllowedModulePrefixes = new[] { "foo" };

            var modules = Compile(("bar/A.surtr", "fun ping(): int { return 1; }\n"));
            Assert.Throws<SurtrCapabilityDeniedException>(() => runtime.LoadModule(modules[0]));

            // A rejected attempt already committed the runtime to a policy - loosening it
            // afterwards would let a script that failed once simply retry under a laxer rule.
            Assert.Throws<InvalidOperationException>(() => runtime.AllowedModulePrefixes = null);
        }

        #endregion
    }
}
