#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System.Collections.Generic;

namespace Surtr.Tests.VM
{
    public class SurtrVirtualMachineControlFlowTests
    {
        private static SurtrValue Run(BytecodeBuilder builder, int localCount = 0, int maxStackSize = 16)
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = builder.Build(module, localCount, maxStackSize);
            return runtime.Invoke(method);
        }

        #region Unary conditional jumps

        [Theory]
        [InlineData(0, 1)]  // zero -> taken
        [InlineData(5, 0)]  // non-zero -> not taken
        public void JPZ_BranchesOnlyWhenTheOperandIsZero(int pushed, int expected)
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .Op(OpCode.PushI32).I32(pushed)
                .JumpShort(OpCode.JPZ, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(expected, Run(builder).AsInt);
        }

        [Theory]
        [InlineData(5, 1)]
        [InlineData(0, 0)]
        public void JPNZ_BranchesOnlyWhenTheOperandIsNonZero(int pushed, int expected)
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .Op(OpCode.PushI32).I32(pushed)
                .JumpShort(OpCode.JPNZ, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(expected, Run(builder).AsInt);
        }

        [Fact]
        public void JPN_BranchesOnANullReference()
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .Op(OpCode.PushNull)
                .JumpShort(OpCode.JPN, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(builder).AsInt);
        }

        [Fact]
        public void JPNN_BranchesOnALiveReference()
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .Op(OpCode.PushI32).I32(1).Op(OpCode.BoxInt)
                .JumpShort(OpCode.JPNN, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(builder).AsInt);
        }

        [Fact]
        public void JP_JumpsUnconditionally_SkippingWhatFollows()
        {
            var builder = new BytecodeBuilder();
            int target = builder.NewLabel();
            builder
                .JumpShort(OpCode.JP, target)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue) // skipped
                .MarkLabel(target)
                .Op(OpCode.PushI32).I32(7).Op(OpCode.ReturnValue);

            Assert.Equal(7, Run(builder).AsInt);
        }

        [Fact]
        public void JPZX_UsesAFourByteOffset()
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .Op(OpCode.PushI32).I32(0)
                .JumpWide(OpCode.JPZ, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(builder).AsInt);
        }

        [Fact]
        public void JPX_UsesAFourByteOffset()
        {
            var builder = new BytecodeBuilder();
            int target = builder.NewLabel();
            builder
                .JumpWide(OpCode.JP, target)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(target)
                .Op(OpCode.PushI32).I32(9).Op(OpCode.ReturnValue);

            Assert.Equal(9, Run(builder).AsInt);
        }

        #endregion

        #region Compare-and-jump

        [Fact]
        public void JPEQ_BranchesWhenIntsAreEqual()
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .Op(OpCode.PushI32).I32(5).Op(OpCode.PushI32).I32(5)
                .JumpShort(OpCode.JPEQ, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(builder).AsInt);
        }

        [Fact]
        public void JPEQX_UsesAFourByteOffset()
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .Op(OpCode.PushI32).I32(5).Op(OpCode.PushI32).I32(5)
                .JumpWide(OpCode.JPEQ, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(builder).AsInt);
        }

        [Fact]
        public void JPNE_BranchesWhenIntsDiffer()
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .Op(OpCode.PushI32).I32(5).Op(OpCode.PushI32).I32(6)
                .JumpShort(OpCode.JPNE, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(builder).AsInt);
        }

        [Fact]
        public void JPFEQ_BranchesWhenFloatsAreEqual()
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .LoadFloat(1.5).LoadFloat(1.5)
                .JumpShort(OpCode.JPFEQ, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(builder).AsInt);
        }

        [Fact]
        public void JPFNE_BranchesWhenFloatsDiffer()
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .LoadFloat(1.5).LoadFloat(2.5)
                .JumpShort(OpCode.JPFNE, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(builder).AsInt);
        }

        [Fact]
        public void JPREQ_BranchesOnTheSameReference()
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .Op(OpCode.PushI32).I32(1).Op(OpCode.BoxInt).Op(OpCode.Dup)
                .JumpShort(OpCode.JPREQ, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(builder).AsInt);
        }

        [Fact]
        public void JPRNE_BranchesOnDistinctReferences_EvenWithEqualContent()
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .Op(OpCode.PushI32).I32(1).Op(OpCode.BoxInt)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.BoxInt)
                .JumpShort(OpCode.JPRNE, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(builder).AsInt);
        }

        [Theory]
        [InlineData(OpCode.JPGT, 6, 5, 1)]
        [InlineData(OpCode.JPGT, 5, 5, 0)]
        [InlineData(OpCode.JPGE, 5, 5, 1)]
        [InlineData(OpCode.JPGE, 4, 5, 0)]
        [InlineData(OpCode.JPLT, 4, 5, 1)]
        [InlineData(OpCode.JPLT, 5, 5, 0)]
        [InlineData(OpCode.JPLE, 5, 5, 1)]
        [InlineData(OpCode.JPLE, 6, 5, 0)]
        public void OrderedIntCompareAndJump(OpCode op, int left, int right, int expected)
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .Op(OpCode.PushI32).I32(left).Op(OpCode.PushI32).I32(right)
                .JumpShort(op, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(expected, Run(builder).AsInt);
        }

        [Theory]
        [InlineData(OpCode.JPFGT, 6.0, 5.0, 1)]
        [InlineData(OpCode.JPFGE, 5.0, 5.0, 1)]
        [InlineData(OpCode.JPFLT, 4.0, 5.0, 1)]
        [InlineData(OpCode.JPFLE, 5.0, 5.0, 1)]
        public void OrderedFloatCompareAndJump(OpCode op, double left, double right, int expected)
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .LoadFloat(left).LoadFloat(right)
                .JumpShort(op, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(expected, Run(builder).AsInt);
        }

        [Fact]
        public void JPGTX_UsesAFourByteOffset()
        {
            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .Op(OpCode.PushI32).I32(6).Op(OpCode.PushI32).I32(5)
                .JumpWide(OpCode.JPGT, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(builder).AsInt);
        }

        #endregion

        #region Switch / SwitchLookup

        [Theory]
        [InlineData(10, 100)]
        [InlineData(11, 200)]
        [InlineData(12, 300)]
        [InlineData(99, -1)] // out of [low, low+count) -> default
        public void Switch_DispatchesADenseRange(int input, int expected)
        {
            var builder = new BytecodeBuilder();
            int case0 = builder.NewLabel();
            int case1 = builder.NewLabel();
            int case2 = builder.NewLabel();
            int fallback = builder.NewLabel();
            int end = builder.NewLabel();

            builder
                .Op(OpCode.PushI32).I32(input)
                .EmitSwitch(low: 10, caseLabels: new[] { case0, case1, case2 }, defaultLabel: fallback)
                .MarkLabel(case0).Op(OpCode.PushI32).I32(100).JumpShort(OpCode.JP, end)
                .MarkLabel(case1).Op(OpCode.PushI32).I32(200).JumpShort(OpCode.JP, end)
                .MarkLabel(case2).Op(OpCode.PushI32).I32(300).JumpShort(OpCode.JP, end)
                .MarkLabel(fallback).Op(OpCode.PushI32).I32(-1).JumpShort(OpCode.JP, end)
                .MarkLabel(end).Op(OpCode.ReturnValue);

            Assert.Equal(expected, Run(builder).AsInt);
        }

        [Theory]
        [InlineData(5, 50)]
        [InlineData(100, 1000)]
        [InlineData(7, -1)] // not one of the sparse keys -> default
        public void SwitchLookup_DispatchesSparseKeys(int input, int expected)
        {
            var builder = new BytecodeBuilder();
            int caseA = builder.NewLabel();
            int caseB = builder.NewLabel();
            int fallback = builder.NewLabel();
            int end = builder.NewLabel();

            var cases = new List<(int Key, int Label)> { (5, caseA), (100, caseB) };

            builder
                .Op(OpCode.PushI32).I32(input)
                .EmitSwitchLookup(cases, fallback)
                .MarkLabel(caseA).Op(OpCode.PushI32).I32(50).JumpShort(OpCode.JP, end)
                .MarkLabel(caseB).Op(OpCode.PushI32).I32(1000).JumpShort(OpCode.JP, end)
                .MarkLabel(fallback).Op(OpCode.PushI32).I32(-1).JumpShort(OpCode.JP, end)
                .MarkLabel(end).Op(OpCode.ReturnValue);

            Assert.Equal(expected, Run(builder).AsInt);
        }

        #endregion

        #region A real loop, to prove backward jumps work end to end

        [Fact]
        public void BackwardJump_ImplementsASummingLoop()
        {
            // local0 = i = 1; local1 = sum = 0;
            // while (i <= 5) { sum += i; i += 1; }
            // return sum; -> 1+2+3+4+5 = 15
            var builder = new BytecodeBuilder();
            int loopStart = builder.NewLabel();
            int loopEnd = builder.NewLabel();

            builder
                .Op(OpCode.PushI32).I32(1).Op(OpCode.Stl0)
                .MarkLabel(loopStart)
                .Op(OpCode.Ldl0).Op(OpCode.PushI32).I32(5)
                .JumpShort(OpCode.JPGT, loopEnd)
                .Op(OpCode.Ldl1).Op(OpCode.Ldl0).Op(OpCode.Add).Op(OpCode.Stl1)
                .Op(OpCode.Ldl0).Op(OpCode.PushI32).I32(1).Op(OpCode.Add).Op(OpCode.Stl0)
                .JumpShort(OpCode.JP, loopStart)
                .MarkLabel(loopEnd)
                .Op(OpCode.Ldl1)
                .Op(OpCode.ReturnValue);

            Assert.Equal(15, Run(builder, localCount: 2).AsInt);
        }

        #endregion
    }
}
