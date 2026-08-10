#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;

namespace Surtr.Tests.VM
{
    public class SurtrVirtualMachineArithmeticTests
    {
        private static SurtrValue Run(BytecodeBuilder builder, int maxStackSize = 32)
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = builder.Build(module, localCount: 0, maxStackSize);
            return runtime.Invoke(method);
        }

        private static BytecodeBuilder PushInts(int left, int right)
            => new BytecodeBuilder().Op(OpCode.PushI32).I32(left).Op(OpCode.PushI32).I32(right);

        private static BytecodeBuilder PushFloats(double left, double right)
            => new BytecodeBuilder().LoadFloat(left).LoadFloat(right);

        #region Integer arithmetic

        [Fact]
        public void Add_AddsTheTopTwoInts()
        {
            var builder = PushInts(2, 3).Op(OpCode.Add).Op(OpCode.ReturnValue);
            Assert.Equal(5, Run(builder).AsInt);
        }

        [Fact]
        public void Sub_SubtractsRightFromLeft()
        {
            var builder = PushInts(10, 4).Op(OpCode.Sub).Op(OpCode.ReturnValue);
            Assert.Equal(6, Run(builder).AsInt);
        }

        [Fact]
        public void Mul_MultipliesTheTopTwoInts()
        {
            var builder = PushInts(6, 7).Op(OpCode.Mul).Op(OpCode.ReturnValue);
            Assert.Equal(42, Run(builder).AsInt);
        }

        [Fact]
        public void Div_TruncatesTowardsZero()
        {
            var builder = PushInts(-7, 2).Op(OpCode.Div).Op(OpCode.ReturnValue);
            Assert.Equal(-3, Run(builder).AsInt);
        }

        [Fact]
        public void Div_ByZero_Traps()
        {
            var builder = PushInts(1, 0).Op(OpCode.Div).Op(OpCode.ReturnValue);
            Assert.Throws<SurtrExecutionException>(() => Run(builder));
        }

        [Fact]
        public void Div_IntMinValueByNegativeOne_Traps()
        {
            var builder = PushInts(int.MinValue, -1).Op(OpCode.Div).Op(OpCode.ReturnValue);
            Assert.Throws<SurtrExecutionException>(() => Run(builder));
        }

        [Fact]
        public void Mod_FollowsCSharpsSignConvention()
        {
            var builder = PushInts(-7, 2).Op(OpCode.Mod).Op(OpCode.ReturnValue);
            Assert.Equal(-1, Run(builder).AsInt);
        }

        [Fact]
        public void Mod_ByZero_Traps()
        {
            var builder = PushInts(1, 0).Op(OpCode.Mod).Op(OpCode.ReturnValue);
            Assert.Throws<SurtrExecutionException>(() => Run(builder));
        }

        [Fact]
        public void Pow_ComputesIntegerExponentiation()
        {
            var builder = PushInts(2, 10).Op(OpCode.Pow).Op(OpCode.ReturnValue);
            Assert.Equal(1024, Run(builder).AsInt);
        }

        [Fact]
        public void Pow_ZeroExponent_IsOne()
        {
            var builder = PushInts(5, 0).Op(OpCode.Pow).Op(OpCode.ReturnValue);
            Assert.Equal(1, Run(builder).AsInt);
        }

        [Fact]
        public void Pow_NegativeExponent_Traps()
        {
            var builder = PushInts(2, -1).Op(OpCode.Pow).Op(OpCode.ReturnValue);
            Assert.Throws<SurtrExecutionException>(() => Run(builder));
        }

        [Fact]
        public void Neg_NegatesAnInt()
        {
            var builder = new BytecodeBuilder().Op(OpCode.PushI32).I32(42).Op(OpCode.Neg).Op(OpCode.ReturnValue);
            Assert.Equal(-42, Run(builder).AsInt);
        }

        [Fact]
        public void Inv_FlipsABooleansLowBit()
        {
            var builder = new BytecodeBuilder();
            builder.LoadConstant(SurtrValue.True.Raw).Op(OpCode.Inv).Op(OpCode.ReturnValue);

            Assert.False(Run(builder).AsBool);
        }

        [Fact]
        public void Inv_OfFalse_IsTrue()
        {
            var builder = new BytecodeBuilder();
            builder.LoadConstant(SurtrValue.False.Raw).Op(OpCode.Inv).Op(OpCode.ReturnValue);

            Assert.True(Run(builder).AsBool);
        }

        #endregion

        #region Float arithmetic

        [Fact]
        public void FAdd_AddsTheTopTwoFloats()
        {
            var builder = PushFloats(1.5, 2.25).Op(OpCode.FAdd).Op(OpCode.ReturnValue);
            Assert.Equal(3.75, Run(builder).AsFloat);
        }

        [Fact]
        public void FSub_SubtractsRightFromLeft()
        {
            var builder = PushFloats(5.5, 1.5).Op(OpCode.FSub).Op(OpCode.ReturnValue);
            Assert.Equal(4.0, Run(builder).AsFloat);
        }

        [Fact]
        public void FMul_MultipliesTheTopTwoFloats()
        {
            var builder = PushFloats(2.5, 4.0).Op(OpCode.FMul).Op(OpCode.ReturnValue);
            Assert.Equal(10.0, Run(builder).AsFloat);
        }

        [Fact]
        public void FDiv_FollowsIeee754_IncludingDivisionByZero()
        {
            var builder = PushFloats(1.0, 0.0).Op(OpCode.FDiv).Op(OpCode.ReturnValue);
            Assert.Equal(double.PositiveInfinity, Run(builder).AsFloat);
        }

        [Fact]
        public void FMod_ComputesTheIeeeRemainder()
        {
            var builder = PushFloats(5.5, 2.0).Op(OpCode.FMod).Op(OpCode.ReturnValue);
            Assert.Equal(1.5, Run(builder).AsFloat);
        }

        [Fact]
        public void FPow_UsesMathPow()
        {
            var builder = PushFloats(2.0, 0.5).Op(OpCode.FPow).Op(OpCode.ReturnValue);
            Assert.Equal(System.Math.Sqrt(2.0), Run(builder).AsFloat);
        }

        [Fact]
        public void FNeg_FlipsTheSignBit_EvenOnZero()
        {
            var builder = new BytecodeBuilder();
            builder.LoadFloat(0.0).Op(OpCode.FNeg).Op(OpCode.ReturnValue);

            var result = Run(builder);
            Assert.Equal(0.0, result.AsFloat);
            Assert.True(double.IsNegative(result.AsFloat));
        }

        [Fact]
        public void FNeg_OfNaN_PreservesNaN()
        {
            var builder = new BytecodeBuilder();
            builder.LoadFloat(double.NaN).Op(OpCode.FNeg).Op(OpCode.ReturnValue);

            Assert.True(double.IsNaN(Run(builder).AsFloat));
        }

        #endregion
    }
}
