#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;

namespace Surtr.Bench
{
    /// <summary>
    /// Compiles the embedded Surtr module once, loads it into one runtime, and hands back cached
    /// <see cref="SurtrMethodInfo"/> handles so a timed loop pays only the call, never the front end.
    /// </summary>
    internal sealed class SurtrDriver : IBenchEngine, IDisposable
    {
        // Mirrors the module-path derivation (docs/Compiler-Plan.md §2.1, ModulePath.TryDerive):
        // a file at <root>/bench/Bench.surtr is module "bench.Bench" - directory segments plus the
        // file's own name. Nothing here touches the disk.
        private const string SourceRoot = "D:/proj/src";

        private const string ModulePath = "bench.Bench";

        private readonly SurtrRuntime _runtime;
        private readonly SurtrCompilation _compilation;
        private readonly Dictionary<string, SurtrMethodInfo> _methods = new();
        private readonly SurtrValue[] _argument = new SurtrValue[1];
        private readonly string _name;

        public static SurtrDriver Build(string moduleSource)
            => Build(moduleSource, DefaultInstructionBudget, SurtrGcPolicy.Automatic, "surtr");

        /// <summary>
        /// Builds with a caller-chosen instruction budget. The harness passes a much tighter budget
        /// for <c>--smoke</c>: a workload that stopped terminating must fail in seconds there, not
        /// hang a CI pass for the same reason the default budget catches it late.
        /// </summary>
        public static SurtrDriver Build(string moduleSource, long instructionBudget)
            => Build(moduleSource, instructionBudget, SurtrGcPolicy.Automatic, "surtr");

        /// <summary>
        /// Builds with a caller-chosen instruction budget and collector policy. The policy is what
        /// the <c>--surtr-gc</c> comparison is made of: a manual runtime only ever collects when the
        /// harness asks it to between samples, while an automatic one also collects by itself at its
        /// safepoints, and the gap between the two is the cost of the policy in one number.
        /// </summary>
        public static SurtrDriver Build(string moduleSource, long instructionBudget, SurtrGcPolicy gcPolicy, string name)
        {
            var project = new SurtrProject(SourceRoot);
            project.AddSourceFile(SourceRoot + "/bench/Bench.surtr", moduleSource);

            var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();
            if (compilation.HasErrors)
                throw new InvalidOperationException("binding reported: " + string.Join("; ", compilation.Diagnostics));

            var emitter = new ModuleEmitter(compilation, binder);
            if (!emitter.TryEmit())
                throw new InvalidOperationException("emission reported: " + string.Join("; ", compilation.Diagnostics));

            var runtime = new SurtrRuntime();

            // The runtime collects on its own by default; the manual policy is the pre-policy
            // behaviour, and the two together are what --surtr-gc both measures side by side.
            runtime.ConfigureGc(gcPolicy);

            // A safety net rather than a limit: a workload that stopped terminating must fail
            // cleanly instead of hanging the harness.
            runtime.InstructionBudget = instructionBudget;

            // "bench.Bench.hostAdd" is the link name the module's module-level `native fun hostAdd`
            // resolves to: <modulePath>.<name>, since a module-level native has no owning type to
            // qualify it the way a class member's does (docs/Language-Syntax.md §10). It has to be
            // published before LoadModule, exactly as the tests do it.
            RegisterNativeBodies(runtime);

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            return new SurtrDriver(runtime, compilation, ModulePath, name);
        }

        /// <summary>5e9 instructions: a real workload cannot touch it, and a non-terminating one fails instead of hanging.</summary>
        public const long DefaultInstructionBudget = 5_000_000_000;

        /// <summary>The budget <c>--smoke</c> runs under: every smoke size is a hundredth of the real one.</summary>
        public const long SmokeInstructionBudget = 50_000_000;

        /// <summary>
        /// Publishes the native bodies the bench module's `native fun` declarations link against.
        /// The first declared parameter of a module-level native is argument zero, so there is no
        /// receiver.
        /// </summary>
        private static unsafe void RegisterNativeBodies(SurtrRuntime runtime)
        {
            runtime.DefineNativeBody(ModulePath + ".hostAdd", SurtrNativeEntryPoint.FromFunctionPointer(&HostAdd));
            runtime.DefineNativeBody(ModulePath + ".hostSin", SurtrNativeEntryPoint.FromFunctionPointer(&HostSin));
            runtime.DefineNativeBody(ModulePath + ".hostCos", SurtrNativeEntryPoint.FromFunctionPointer(&HostCos));
            runtime.DefineNativeBody(ModulePath + ".hostSqrt", SurtrNativeEntryPoint.FromFunctionPointer(&HostSqrt));
        }

        // The in-place native convention: read every input before the first write, then answer
        // how many slots were written. Each of these reads argument zero and returns one slot, so
        // the aliasing rule is satisfied by the argument being consumed inside the Return call.
        private static int HostAdd(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(arguments.GetInt(0) + 1));

        private static int HostSin(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateFloat(Math.Sin(arguments.GetFloat(0))));

        private static int HostCos(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateFloat(Math.Cos(arguments.GetFloat(0))));

        private static int HostSqrt(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateFloat(Math.Sqrt(arguments.GetFloat(0))));

        private SurtrDriver(SurtrRuntime runtime, SurtrCompilation compilation, string modulePath, string name)
        {
            _runtime = runtime;
            _compilation = compilation;
            _name = name;

            if (!runtime.TryGetModule(modulePath, out var module))
                throw new InvalidOperationException($"no module '{modulePath}' was loaded.");

            foreach (var workload in Workloads.AllWorkloads)
            {
                if (!module.TryGetMethods(workload.Name, out var overloads) || overloads.Length == 0)
                    throw new InvalidOperationException($"the bench module declares no '{workload.Name}'.");
                _methods[workload.Name] = overloads[0];
            }
        }

        public string Name => _name;

        /// <summary>
        /// Calls one workload once and returns its result as the numeric type its case returns.
        /// </summary>
        public double Call(Workload workload, long size)
        {
            _argument[0] = SurtrValue.CreateInt((int)size);
            SurtrValue result = _runtime.Invoke(_methods[workload.Name], _argument);
            return workload.Kind == WorkloadKind.Int ? result.AsInt : result.AsFloat;
        }

        /// <summary>
        /// Collects whatever the previous call left unreachable. Called between samples, never
        /// inside the timed region. The runtime also collects on its own at its safepoints (see
        /// <see cref="SurtrRuntime.GcPolicy"/>); this call just makes the measurement deterministic
        /// by sweeping before the sample is taken.
        /// </summary>
        public void Collect() => _runtime.Collect();

        /// <summary>
        /// The CLR allocation counter, plus the object-level counts the registry can give and no
        /// other engine here can.
        /// </summary>
        /// <remarks>
        /// Objects allocated is reconstructed rather than counted: what is live now, plus
        /// everything collection has reclaimed, is everything ever registered, and differencing two
        /// of those across a run gives what the run allocated. That keeps the registration path —
        /// which runs once per object the VM creates — free of a counter it would otherwise carry
        /// forever for the benefit of a benchmark.
        /// </remarks>
        public MemorySample SampleMemory() => new MemorySample(
            GC.GetAllocatedBytesForCurrentThread(),
            _runtime.LiveObjectCount + _runtime.TotalCollectedObjects,
            _runtime.LiveObjectCount,
            (long)_runtime.HeapCapacity * SlotBytes);

        /// <summary>
        /// Bytes of registry per object slot: the entity reference, the free-list id, the age byte
        /// and the mark bit. The managed entities themselves are on the CLR heap and are counted by
        /// <see cref="MemorySample.AllocatedBytes"/>; this is what the registry costs on top, and it
        /// is charged for every slot whether occupied or not.
        /// </summary>
        private const int SlotBytes = 8 + 4 + 1;

        public void Dispose()
        {
            _runtime.Dispose();
            _compilation.Dispose();
        }
    }
}
