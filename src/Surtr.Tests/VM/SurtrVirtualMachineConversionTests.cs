#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;

namespace Surtr.Tests.VM
{
    public class SurtrVirtualMachineConversionTests
    {
        private static SurtrValue Run(BytecodeBuilder builder, int maxStackSize = 32)
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = builder.Build(module, localCount: 0, maxStackSize);
            return runtime.Invoke(method);
        }

        #region Numeric conversions

        [Fact]
        public void I2F_ConvertsAnIntToAFloat()
        {
            var builder = new BytecodeBuilder().Op(OpCode.PushI32).I32(7).Op(OpCode.I2F).Op(OpCode.ReturnValue);
            Assert.Equal(7.0, Run(builder).AsFloat);
        }

        [Fact]
        public void F2I_TruncatesTowardsZero()
        {
            var builder = new BytecodeBuilder();
            builder.LoadFloat(7.9).Op(OpCode.F2I).Op(OpCode.ReturnValue);
            Assert.Equal(7, Run(builder).AsInt);
        }

        [Fact]
        public void F2I_SaturatesAboveIntMaxValue()
        {
            var builder = new BytecodeBuilder();
            builder.LoadFloat(1e30).Op(OpCode.F2I).Op(OpCode.ReturnValue);
            Assert.Equal(int.MaxValue, Run(builder).AsInt);
        }

        [Fact]
        public void F2I_SaturatesBelowIntMinValue()
        {
            var builder = new BytecodeBuilder();
            builder.LoadFloat(-1e30).Op(OpCode.F2I).Op(OpCode.ReturnValue);
            Assert.Equal(int.MinValue, Run(builder).AsInt);
        }

        [Fact]
        public void F2I_OfNaN_IsZero()
        {
            var builder = new BytecodeBuilder();
            builder.LoadFloat(double.NaN).Op(OpCode.F2I).Op(OpCode.ReturnValue);
            Assert.Equal(0, Run(builder).AsInt);
        }

        [Fact]
        public void I2C_TruncatesToSixteenBits()
        {
            var builder = new BytecodeBuilder().Op(OpCode.PushI32).I32(65 + 65536).Op(OpCode.I2C).Op(OpCode.ReturnValue);
            Assert.Equal('A', Run(builder).AsChar);
        }

        [Fact]
        public void C2I_WidensBackToAnInt()
        {
            var builder = new BytecodeBuilder();
            builder.LoadConstant(SurtrValue.CreateChar('A').Raw).Op(OpCode.C2I).Op(OpCode.ReturnValue);
            Assert.Equal(65, Run(builder).AsInt);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(1, true)]
        [InlineData(-1, true)]
        [InlineData(42, true)]
        public void I2B_NormalizesToZeroOrOne(int value, bool expected)
        {
            var builder = new BytecodeBuilder().Op(OpCode.PushI32).I32(value).Op(OpCode.I2B).Op(OpCode.ReturnValue);
            var result = Run(builder);
            Assert.True(result.IsBool);
            Assert.Equal(expected, result.AsBool);
        }

        [Fact]
        public void B2I_WidensATrueBoolToOne()
        {
            var builder = new BytecodeBuilder();
            builder.LoadConstant(SurtrValue.True.Raw).Op(OpCode.B2I).Op(OpCode.ReturnValue);
            Assert.Equal(1, Run(builder).AsInt);
        }

        [Fact]
        public void B2I_WidensAFalseBoolToZero()
        {
            var builder = new BytecodeBuilder();
            builder.LoadConstant(SurtrValue.False.Raw).Op(OpCode.B2I).Op(OpCode.ReturnValue);
            Assert.Equal(0, Run(builder).AsInt);
        }

        #endregion

        #region Boxing

        [Fact]
        public void BoxInt_ThenUnbox_RoundTrips()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(42)
                .Op(OpCode.BoxInt)
                .Op(OpCode.Unbox)
                .Op(OpCode.ReturnValue);

            var result = Run(builder);
            Assert.True(result.IsInt);
            Assert.Equal(42, result.AsInt);
        }

        [Fact]
        public void BoxFloat_ThenUnbox_RoundTrips()
        {
            var builder = new BytecodeBuilder();
            builder.LoadFloat(2.5).Op(OpCode.BoxFloat).Op(OpCode.Unbox).Op(OpCode.ReturnValue);

            var result = Run(builder);
            Assert.True(result.IsFloat);
            Assert.Equal(2.5, result.AsFloat);
        }

        [Fact]
        public void BoxBool_ThenUnbox_RoundTrips()
        {
            var builder = new BytecodeBuilder();
            builder.LoadConstant(SurtrValue.True.Raw).Op(OpCode.BoxBool).Op(OpCode.Unbox).Op(OpCode.ReturnValue);

            var result = Run(builder);
            Assert.True(result.IsBool);
            Assert.True(result.AsBool);
        }

        [Fact]
        public void BoxChar_ThenUnbox_RoundTrips()
        {
            var builder = new BytecodeBuilder();
            builder.LoadConstant(SurtrValue.CreateChar('z').Raw).Op(OpCode.BoxChar).Op(OpCode.Unbox).Op(OpCode.ReturnValue);

            var result = Run(builder);
            Assert.True(result.IsChar);
            Assert.Equal('z', result.AsChar);
        }

        [Fact]
        public void BoxInt_ProducesALiveReferenceEachTime()
        {
            // Two boxes of equal content must still be distinct entities.
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(9)
                .Op(OpCode.BoxInt)
                .Op(OpCode.PushI32).I32(9)
                .Op(OpCode.BoxInt)
                .Op(OpCode.RNE)
                .Op(OpCode.ReturnValue);

            Assert.True(Run(builder).AsBool);
        }

        #endregion
    }
}
