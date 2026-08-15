#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;

namespace Surtr.Tests.VM
{
    public class SurtrVirtualMachineCastAndInstanceOfTests
    {
        private static SurtrValue Run(SurtrRuntime runtime, SurtrModule module, BytecodeBuilder builder, int maxStackSize = 16)
        {
            var method = builder.Build(module, localCount: 0, maxStackSize);
            return runtime.Invoke(method);
        }

        private sealed class Fixture
        {
            public SurtrModule Module { get; }
            public SurtrClass A { get; }
            public SurtrClass B { get; } // extends A
            public SurtrClass C { get; } // unrelated

            public Fixture()
            {
                Module = new SurtrModule("test");
                A = VmMetadataHelpers.DefineClass(Module, "A");
                B = VmMetadataHelpers.DefineClass(Module, "B", baseClass: A);
                C = VmMetadataHelpers.DefineClass(Module, "C");
                SurtrTypeLinker.LinkModule(Module);
            }
        }

        #region InstanceOf

        [Fact]
        public void InstanceOf_ADerivedInstanceAgainstItsBase_IsTrue()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();
            var instance = runtime.NewInstance(fixture.B);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.A));
            builder.LoadReference(instance).Op(OpCode.InstanceOf).I16(typeIndex).Op(OpCode.ReturnValue);

            Assert.True(Run(runtime, fixture.Module, builder).AsBool);
        }

        [Fact]
        public void InstanceOf_ABaseInstanceAgainstADerivedType_IsFalse()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();
            var instance = runtime.NewInstance(fixture.A);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.B));
            builder.LoadReference(instance).Op(OpCode.InstanceOf).I16(typeIndex).Op(OpCode.ReturnValue);

            Assert.False(Run(runtime, fixture.Module, builder).AsBool);
        }

        [Fact]
        public void InstanceOf_AnUnrelatedType_IsFalse()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();
            var instance = runtime.NewInstance(fixture.A);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.C));
            builder.LoadReference(instance).Op(OpCode.InstanceOf).I16(typeIndex).Op(OpCode.ReturnValue);

            Assert.False(Run(runtime, fixture.Module, builder).AsBool);
        }

        [Fact]
        public void InstanceOf_ANullSubject_IsFalse()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.A));
            builder.Op(OpCode.PushNull).Op(OpCode.InstanceOf).I16(typeIndex).Op(OpCode.ReturnValue);

            Assert.False(Run(runtime, fixture.Module, builder).AsBool);
        }

        [Fact]
        public void InstanceOfX_UsesAFourByteTypeIndex()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();
            var instance = runtime.NewInstance(fixture.B);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.A));
            builder.LoadReference(instance).Op(OpCode.InstanceOfX).I32(typeIndex).Op(OpCode.ReturnValue);

            Assert.True(Run(runtime, fixture.Module, builder).AsBool);
        }

        #endregion

        #region JPInstanceOf / JPInstanceOfX

        [Fact]
        public void JPInstanceOf_BranchesWhenTheSubjectMatches()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();
            var instance = runtime.NewInstance(fixture.B);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.A));
            int taken = builder.NewLabel();

            builder
                .LoadReference(instance)
                .JumpShortInstanceOf(typeIndex, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(runtime, fixture.Module, builder).AsInt);
        }

        [Fact]
        public void JPInstanceOf_DoesNotBranchWhenTheSubjectDoesNotMatch()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();
            var instance = runtime.NewInstance(fixture.A);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.B));
            int taken = builder.NewLabel();

            builder
                .LoadReference(instance)
                .JumpShortInstanceOf(typeIndex, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(0, Run(runtime, fixture.Module, builder).AsInt);
        }

        [Fact]
        public void JPInstanceOfX_UsesAFourByteTypeIndexAndOffset()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();
            var instance = runtime.NewInstance(fixture.B);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.A));
            int taken = builder.NewLabel();

            builder
                .LoadReference(instance)
                .JumpWideInstanceOf(typeIndex, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(runtime, fixture.Module, builder).AsInt);
        }

        #endregion

        #region Cast

        [Fact]
        public void Cast_ToAMatchingType_LeavesTheReferenceOnTheStack()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();
            var instance = runtime.NewInstance(fixture.B);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.A));
            builder.LoadReference(instance).Op(OpCode.Cast).I16(typeIndex).Op(OpCode.IsNotNull).Op(OpCode.ReturnValue);

            Assert.True(Run(runtime, fixture.Module, builder).AsBool);
        }

        [Fact]
        public void Cast_ToAnUnrelatedType_Traps()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();
            var instance = runtime.NewInstance(fixture.A);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.C));
            builder.LoadReference(instance).Op(OpCode.Cast).I16(typeIndex).Op(OpCode.ReturnValue);

            Assert.Throws<SurtrExecutionException>(() => Run(runtime, fixture.Module, builder));
        }

        [Fact]
        public void Cast_ANullReference_NeverTraps()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.C));
            builder.Op(OpCode.PushNull).Op(OpCode.Cast).I16(typeIndex).Op(OpCode.IsNull).Op(OpCode.ReturnValue);

            Assert.True(Run(runtime, fixture.Module, builder).AsBool);
        }

        [Fact]
        public void CastX_UsesAFourByteTypeIndex_AndStillTraps()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();
            var instance = runtime.NewInstance(fixture.A);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.C));
            builder.LoadReference(instance).Op(OpCode.CastX).I32(typeIndex).Op(OpCode.ReturnValue);

            Assert.Throws<SurtrExecutionException>(() => Run(runtime, fixture.Module, builder));
        }

        #endregion

        #region CastOrNull

        [Fact]
        public void CastOrNull_ToAMatchingType_KeepsTheReference()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();
            var instance = runtime.NewInstance(fixture.B);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.A));
            builder.LoadReference(instance).Op(OpCode.CastOrNull).I16(typeIndex).Op(OpCode.ReturnValue);

            var result = Run(runtime, fixture.Module, builder);
            Assert.Same(instance, runtime.Resolve<SurtrInstance>(result));
        }

        [Fact]
        public void CastOrNull_ToAnUnrelatedType_YieldsNullRatherThanTrapping()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();
            var instance = runtime.NewInstance(fixture.A);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.C));
            builder.LoadReference(instance).Op(OpCode.CastOrNull).I16(typeIndex).Op(OpCode.ReturnValue);

            var result = Run(runtime, fixture.Module, builder);
            Assert.True(result.IsNullReference);
        }

        [Fact]
        public void CastOrNull_ANullReference_StaysNull()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.C));
            builder.Op(OpCode.PushNull).Op(OpCode.CastOrNull).I16(typeIndex).Op(OpCode.IsNull).Op(OpCode.ReturnValue);

            Assert.True(Run(runtime, fixture.Module, builder).AsBool);
        }

        [Fact]
        public void CastOrNullX_UsesAFourByteTypeIndex()
        {
            using var runtime = new SurtrRuntime();
            var fixture = new Fixture();
            var instance = runtime.NewInstance(fixture.A);

            var builder = new BytecodeBuilder();
            int typeIndex = builder.AddType(VmMetadataHelpers.HandleFor(fixture.Module, fixture.C));
            builder.LoadReference(instance).Op(OpCode.CastOrNullX).I32(typeIndex).Op(OpCode.ReturnValue);

            Assert.True(Run(runtime, fixture.Module, builder).IsNullReference);
        }

        #endregion
    }
}
