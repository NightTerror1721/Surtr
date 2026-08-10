#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;

namespace Surtr.Tests.VM
{
    public class SurtrVirtualMachineComparisonAndBitwiseTests
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

        #region Integer comparisons

        [Theory]
        [InlineData(5, 5, true)]
        [InlineData(5, 6, false)]
        public void EQ_ComparesInts(int left, int right, bool expected)
            => Assert.Equal(expected, Run(PushInts(left, right).Op(OpCode.EQ).Op(OpCode.ReturnValue)).AsBool);

        [Theory]
        [InlineData(5, 5, false)]
        [InlineData(5, 6, true)]
        public void NE_ComparesInts(int left, int right, bool expected)
            => Assert.Equal(expected, Run(PushInts(left, right).Op(OpCode.NE).Op(OpCode.ReturnValue)).AsBool);

        [Theory]
        [InlineData(6, 5, true)]
        [InlineData(5, 5, false)]
        [InlineData(4, 5, false)]
        public void GT_ComparesInts(int left, int right, bool expected)
            => Assert.Equal(expected, Run(PushInts(left, right).Op(OpCode.GT).Op(OpCode.ReturnValue)).AsBool);

        [Theory]
        [InlineData(6, 5, true)]
        [InlineData(5, 5, true)]
        [InlineData(4, 5, false)]
        public void GE_ComparesInts(int left, int right, bool expected)
            => Assert.Equal(expected, Run(PushInts(left, right).Op(OpCode.GE).Op(OpCode.ReturnValue)).AsBool);

        [Theory]
        [InlineData(4, 5, true)]
        [InlineData(5, 5, false)]
        [InlineData(6, 5, false)]
        public void LT_ComparesInts(int left, int right, bool expected)
            => Assert.Equal(expected, Run(PushInts(left, right).Op(OpCode.LT).Op(OpCode.ReturnValue)).AsBool);

        [Theory]
        [InlineData(4, 5, true)]
        [InlineData(5, 5, true)]
        [InlineData(6, 5, false)]
        public void LE_ComparesInts(int left, int right, bool expected)
            => Assert.Equal(expected, Run(PushInts(left, right).Op(OpCode.LE).Op(OpCode.ReturnValue)).AsBool);

        #endregion

        #region Float comparisons

        [Fact]
        public void FEQ_ComparesFloats()
            => Assert.True(Run(PushFloats(1.5, 1.5).Op(OpCode.FEQ).Op(OpCode.ReturnValue)).AsBool);

        [Fact]
        public void FEQ_OfNaN_IsFalse()
            => Assert.False(Run(PushFloats(double.NaN, double.NaN).Op(OpCode.FEQ).Op(OpCode.ReturnValue)).AsBool);

        [Fact]
        public void FNE_OfNaN_IsTrue()
            => Assert.True(Run(PushFloats(double.NaN, double.NaN).Op(OpCode.FNE).Op(OpCode.ReturnValue)).AsBool);

        [Fact]
        public void FGT_ComparesFloats()
            => Assert.True(Run(PushFloats(2.5, 1.5).Op(OpCode.FGT).Op(OpCode.ReturnValue)).AsBool);

        [Fact]
        public void FGE_ComparesFloats()
            => Assert.True(Run(PushFloats(1.5, 1.5).Op(OpCode.FGE).Op(OpCode.ReturnValue)).AsBool);

        [Fact]
        public void FLT_ComparesFloats()
            => Assert.True(Run(PushFloats(1.5, 2.5).Op(OpCode.FLT).Op(OpCode.ReturnValue)).AsBool);

        [Fact]
        public void FLE_ComparesFloats()
            => Assert.True(Run(PushFloats(1.5, 1.5).Op(OpCode.FLE).Op(OpCode.ReturnValue)).AsBool);

        #endregion

        #region Reference comparisons

        [Fact]
        public void REQ_OfTheSameReference_IsTrue()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(5)
                .Op(OpCode.BoxInt)
                .Op(OpCode.Dup)
                .Op(OpCode.REQ)
                .Op(OpCode.ReturnValue);

            Assert.True(Run(builder).AsBool);
        }

        [Fact]
        public void REQ_OfTwoDistinctBoxes_IsFalse_EvenWithEqualContent()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(5)
                .Op(OpCode.BoxInt)
                .Op(OpCode.PushI32).I32(5)
                .Op(OpCode.BoxInt)
                .Op(OpCode.REQ)
                .Op(OpCode.ReturnValue);

            Assert.False(Run(builder).AsBool);
        }

        [Fact]
        public void RNE_OfTwoDistinctBoxes_IsTrue()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(5)
                .Op(OpCode.BoxInt)
                .Op(OpCode.PushI32).I32(5)
                .Op(OpCode.BoxInt)
                .Op(OpCode.RNE)
                .Op(OpCode.ReturnValue);

            Assert.True(Run(builder).AsBool);
        }

        [Fact]
        public void IsNull_OfANullReference_IsTrue()
        {
            var builder = new BytecodeBuilder().Op(OpCode.PushNull).Op(OpCode.IsNull).Op(OpCode.ReturnValue);
            Assert.True(Run(builder).AsBool);
        }

        [Fact]
        public void IsNull_OfALiveReference_IsFalse()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.BoxInt)
                .Op(OpCode.IsNull)
                .Op(OpCode.ReturnValue);

            Assert.False(Run(builder).AsBool);
        }

        [Fact]
        public void IsNotNull_OfALiveReference_IsTrue()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.BoxInt)
                .Op(OpCode.IsNotNull)
                .Op(OpCode.ReturnValue);

            Assert.True(Run(builder).AsBool);
        }

        #endregion

        #region Bitwise

        [Fact]
        public void And_MasksBits()
            => Assert.Equal(0b1000, Run(PushInts(0b1100, 0b1010).Op(OpCode.And).Op(OpCode.ReturnValue)).AsInt);

        [Fact]
        public void Or_CombinesBits()
            => Assert.Equal(0b1110, Run(PushInts(0b1100, 0b1010).Op(OpCode.Or).Op(OpCode.ReturnValue)).AsInt);

        [Fact]
        public void Xor_TogglesBits()
            => Assert.Equal(0b0110, Run(PushInts(0b1100, 0b1010).Op(OpCode.Xor).Op(OpCode.ReturnValue)).AsInt);

        [Fact]
        public void Not_ComplementsEveryBit()
        {
            var builder = new BytecodeBuilder().Op(OpCode.PushI32).I32(0).Op(OpCode.Not).Op(OpCode.ReturnValue);
            Assert.Equal(-1, Run(builder).AsInt);
        }

        [Fact]
        public void Shl_ShiftsLeft()
            => Assert.Equal(8, Run(PushInts(1, 3).Op(OpCode.Shl).Op(OpCode.ReturnValue)).AsInt);

        [Fact]
        public void Shl_MasksTheCountToFiveBits()
        {
            // 32 & 31 == 0, so this must behave like a shift by zero, not overflow to zero itself.
            var builder = PushInts(1, 32).Op(OpCode.Shl).Op(OpCode.ReturnValue);
            Assert.Equal(1, Run(builder).AsInt);
        }

        [Fact]
        public void Shr_ShiftsRightWithoutSignExtension()
        {
            // Logical shift: the sign bit fills with zero, unlike Sar.
            var builder = PushInts(-1, 28).Op(OpCode.Shr).Op(OpCode.ReturnValue);
            Assert.Equal(0xF, Run(builder).AsInt);
        }

        [Fact]
        public void Sar_ShiftsRightWithSignExtension()
        {
            var builder = PushInts(-8, 1).Op(OpCode.Sar).Op(OpCode.ReturnValue);
            Assert.Equal(-4, Run(builder).AsInt);
        }

        #endregion
    }
}
