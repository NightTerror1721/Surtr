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
        // Mirrors the module-path derivation ModuleEmitterTests relies on: a file at
        // <root>/bench/Bench.surtr is module "bench". Nothing here touches the disk.
        private const string SourceRoot = "D:/proj/src";

        private const string ModulePath = "bench";

        private readonly SurtrRuntime _runtime;
        private readonly SurtrCompilation _compilation;
        private readonly Dictionary<string, SurtrMethodInfo> _methods = new();
        private readonly SurtrValue[] _argument = new SurtrValue[1];

        public static SurtrDriver Build(string moduleSource)
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

            // A safety net rather than a limit: a workload that stopped terminating must fail
            // cleanly instead of hanging the harness.
            runtime.InstructionBudget = 5_000_000_000;

            // "hostAdd" is the host global the module's `native fun hostAdd` links against. It has
            // to be published before LoadModule, exactly as the tests do it.
            RegisterNativeGlobals(runtime);

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            return new SurtrDriver(runtime, compilation, ModulePath);
        }

        /// <summary>
        /// Publishes the native globals the bench module declares. The first declared parameter of
        /// a host global is argument zero, so there is no receiver.
        /// </summary>
        private static unsafe void RegisterNativeGlobals(SurtrRuntime runtime)
        {
            runtime.DefineGlobalFunction(
                "hostAdd",
                SurtrClassReference.Integer,
                new[] { new SurtrParameterInfo("value", runtime.TypeHandle(SurtrClassReference.Integer)) },
                SurtrNativeEntryPoint.FromFunctionPointer(&HostAdd));
        }

        private static SurtrValue HostAdd(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetInt(0) + 1);

        private SurtrDriver(SurtrRuntime runtime, SurtrCompilation compilation, string modulePath)
        {
            _runtime = runtime;
            _compilation = compilation;

            if (!runtime.TryGetModule(modulePath, out var module))
                throw new InvalidOperationException($"no module '{modulePath}' was loaded.");

            foreach (var workload in Workloads.AllWorkloads)
            {
                if (!module.TryGetMethods(workload.Name, out var overloads) || overloads.Length == 0)
                    throw new InvalidOperationException($"the bench module declares no '{workload.Name}'.");
                _methods[workload.Name] = overloads[0];
            }
        }

        public string Name => "surtr";

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
        /// inside the timed region: Surtr's collector only runs when the host asks it to.
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
