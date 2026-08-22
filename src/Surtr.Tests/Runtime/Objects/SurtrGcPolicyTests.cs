#nullable enable

using Surtr.Bytecode;
using Surtr.Bytecode.Emit;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Tests.VM;
using System;

namespace Surtr.Tests.Runtime.Objects
{
    /// <summary>
    /// Covers the automatic-collection policy: what a runtime's collector is allowed to do on its
    /// own, when it arms a pending collection, and where that collection runs. The sweep itself is
    /// exercised at length by <see cref="SurtrEntityRegistryTests"/>; these tests pin down the
    /// policy plumbing around it.
    /// </summary>
    public class SurtrGcPolicyTests
    {
        private static readonly SurtrClassReference IntArray =
            SurtrClassReference.Array(SurtrClassReference.Integer);

        #region Defaults and configuration

        [Fact]
        public void ANewRuntime_CollectsOnItsOwnByDefault()
        {
            using var runtime = new SurtrRuntime();
            Assert.Equal(SurtrGcMode.Automatic, runtime.GcPolicy.Mode);
        }

        [Fact]
        public void ConfigureGc_ReplacesThePolicy()
        {
            using var runtime = new SurtrRuntime();

            runtime.ConfigureGc(SurtrGcPolicy.Manual);
            Assert.Equal(SurtrGcMode.Manual, runtime.GcPolicy.Mode);

            var custom = new SurtrGcPolicy(SurtrGcMode.Automatic, 4096, 50, 4);
            runtime.ConfigureGc(custom);
            Assert.Equal(SurtrGcMode.Automatic, runtime.GcPolicy.Mode);
            Assert.Equal(4096, runtime.GcPolicy.AllocationThreshold);
            Assert.Equal(50, runtime.GcPolicy.LiveEntityThresholdPercent);
            Assert.Equal(4, runtime.GcPolicy.NurseryFrequency);
        }

        [Fact]
        public void AnInvalidPolicy_CannotBeConstructed()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SurtrGcPolicy(SurtrGcMode.Automatic, 0, 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SurtrGcPolicy(SurtrGcMode.Automatic, 1, 101, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SurtrGcPolicy(SurtrGcMode.Automatic, 1, -1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new SurtrGcPolicy(SurtrGcMode.Automatic, 1, 0, 0));
        }

        #endregion

        #region The deferred safepoint

        [Fact]
        public void ManualMode_NeverCollectsOnItsOwn()
        {
            using var runtime = new SurtrRuntime();
            runtime.ConfigureGc(SurtrGcPolicy.Manual);

            var dropped = runtime.NewArray(IntArray);
            SurtrValue droppedRef = runtime.ValueOf(dropped);
            for (int i = 0; i < 32; i++)
                runtime.NewArray(IntArray);

            // The registrations never arm the pending flag in Manual mode, so a safepoint does
            // nothing - the unreachable arrays stay put, exactly as before automatic collection.
            runtime.CollectAtSafepoint();

            Assert.Equal(0, runtime.TotalCollections);
            Assert.NotNull(runtime.Resolve<SurtrArray>(droppedRef));
        }

        [Fact]
        public void AutomaticMode_CollectsAtASafepoint_OnceTheAllocationThresholdIsCrossed()
        {
            using var runtime = new SurtrRuntime();
            runtime.ConfigureGc(new SurtrGcPolicy(SurtrGcMode.Automatic, allocationThreshold: 4, liveEntityThresholdPercent: 0, nurseryFrequency: 1));

            var kept = runtime.NewArray(IntArray);          // 1
            var dropped = runtime.NewArray(IntArray);       // 2
            SurtrValue keptRef = runtime.ValueOf(kept);
            SurtrValue droppedRef = runtime.ValueOf(dropped);
            runtime.AddRoot(kept);

            runtime.NewArray(IntArray);                     // 3
            runtime.NewArray(IntArray);                     // 4 -> threshold crossed, pending armed

            Assert.Equal(4, runtime.AllocationsSinceLastCollection);

            runtime.CollectAtSafepoint();

            Assert.Equal(1, runtime.TotalCollections);
            Assert.Equal(0, runtime.AllocationsSinceLastCollection);
            Assert.NotNull(runtime.Resolve<SurtrArray>(keptRef));     // rooted -> survived
            Assert.Null(runtime.Resolve<SurtrArray>(droppedRef));     // unreachable -> collected
        }

        [Fact]
        public void AManualCollect_DrainsThePendingFlagAndRestartsTheCounter()
        {
            using var runtime = new SurtrRuntime();
            runtime.ConfigureGc(new SurtrGcPolicy(SurtrGcMode.Automatic, allocationThreshold: 4, liveEntityThresholdPercent: 0, nurseryFrequency: 1));

            for (int i = 0; i < 8; i++)
                runtime.NewArray(IntArray);

            Assert.True(runtime.AllocationsSinceLastCollection >= 4);

            // The host collects by hand before any safepoint runs; the sweep must clear the pending
            // flag, or the next safepoint would collect twice for one crossing.
            runtime.Collect(fullCollection: true);

            Assert.Equal(1, runtime.TotalCollections);
            Assert.Equal(0, runtime.AllocationsSinceLastCollection);

            runtime.CollectAtSafepoint();
            Assert.Equal(1, runtime.TotalCollections);
        }

        [Fact]
        public void TheNurseryFrequency_DecidesWhichCollectionsAreFull()
        {
            using var runtime = new SurtrRuntime();
            runtime.ConfigureGc(new SurtrGcPolicy(SurtrGcMode.Automatic, allocationThreshold: 1, liveEntityThresholdPercent: 0, nurseryFrequency: 3));

            // Three rounds, each crossing the threshold and hitting a safepoint. With a frequency
            // of 3 the first two sweeps are nursery and the third is full.
            for (int i = 0; i < 3; i++)
            {
                runtime.NewArray(IntArray);
                runtime.CollectAtSafepoint();
            }

            Assert.Equal(3, runtime.TotalCollections);
            Assert.Equal(2, runtime.TotalNurseryCollections);
            Assert.Equal(1, runtime.TotalFullCollections);
        }

        #endregion

        #region End to end through the interpreter

        [Fact]
        public void PureBytecodeAllocations_AreCollectedAtAllocationSites()
        {
            using var runtime = new SurtrRuntime();
            runtime.ConfigureGc(new SurtrGcPolicy(SurtrGcMode.Automatic, allocationThreshold: 100, liveEntityThresholdPercent: 0, nurseryFrequency: 1));

            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();
            int arrayType = builder.AddType(module.TypeHandles.GetOrAdd(IntArray));
            int loopStart = builder.NewLabel();

            // local0 = i = 0; do { new int[1]; i += 1; } while (i < 200); return i;
            builder
                .Op(OpCode.PushI32).I32(0).Op(OpCode.Stl0)
                .MarkLabel(loopStart)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ArrNew).I16(arrayType)
                .Op(OpCode.Pop)
                .Op(OpCode.Ldl0).Op(OpCode.PushI32).I32(1).Op(OpCode.Add).Op(OpCode.Stl0)
                .Op(OpCode.Ldl0).Op(OpCode.PushI32).I32(200)
                .JumpShort(OpCode.JPLT, loopStart)
                .Op(OpCode.Ldl0)
                .Op(OpCode.ReturnValue);

            var method = builder.Build(module, localCount: 1, maxStackSize: 16);

            // No host call to Collect anywhere: the threshold crossing arms the pending flag and the
            // next allocation site drains it between loop iterations. A pure-bytecode script with
            // no native calls therefore collects on its own.
            Assert.Equal(200, runtime.Invoke(method).AsInt);
            Assert.True(runtime.TotalCollections >= 1, "A pure-bytecode loop should collect on its own.");

            // The last crossing may be pending (deferred to the next safepoint), which is the
            // design; a safepoint now drains it and restarts the counter.
            runtime.CollectAtSafepoint();
            Assert.Equal(0, runtime.AllocationsSinceLastCollection);
        }

        [Fact]
        public void TheNativeBoundary_CollectsAfterAHostBodyAllocates()
        {
            using var runtime = new SurtrRuntime();
            runtime.ConfigureGc(new SurtrGcPolicy(SurtrGcMode.Automatic, allocationThreshold: 50, liveEntityThresholdPercent: 0, nurseryFrequency: 1));
            runtime.DefineNativeBody("allocMany", SurtrNativeEntryPoint.FromDelegate(AllocMany));

            var module = ModuleCallingNative();
            runtime.LoadModule(module);

            Assert.True(module.TryGetMethods("run", out var overloads));
            var result = runtime.Invoke(overloads[0]);

            // The native body allocated two hundred throwaway arrays in one call; none of them
            // were collectable while it ran (there is no safepoint inside host code), so the sweep
            // happens once, at the native boundary right after it returns.
            Assert.Equal(0, result.AsInt);
            Assert.Equal(1, runtime.TotalCollections);
            Assert.Equal(0, runtime.AllocationsSinceLastCollection);
        }

        private static SurtrValue AllocMany(SurtrCallArguments arguments)
        {
            var runtime = arguments.Runtime;
            for (int i = 0; i < 200; i++)
                runtime.NewArray(IntArray);
            return SurtrValue.CreateInt(runtime.TotalCollections);
        }

        private static SurtrModule ModuleCallingNative()
        {
            var builder = new SurtrModuleBuilder("test");
            var native = builder.DeclareNativeFunction("allocMany", SurtrClassReference.Integer, "allocMany");
            var run = builder.DefineFunction("run", SurtrClassReference.Integer);
            run.Code.Call(native);
            run.Code.ReturnValue();
            return builder.Build();
        }

        #endregion
    }
}