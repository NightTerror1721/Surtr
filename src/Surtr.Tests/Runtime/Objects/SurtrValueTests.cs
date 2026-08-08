#nullable enable

using Surtr.Runtime.Objects;

namespace Surtr.Tests.Runtime.Objects
{
    public class SurtrValueTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        [InlineData(int.MaxValue)]
        public void Int_RoundTrips_ThroughRawBits(int value)
        {
            SurtrValue boxed = SurtrValue.CreateInt(value);

            Assert.True(boxed.IsInt);
            Assert.False(boxed.IsFloat);
            Assert.Equal(value, boxed.AsInt);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(1.5)]
        [InlineData(-1.5)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.NegativeInfinity)]
        [InlineData(double.NaN)]
        public void Float_RoundTrips_AndIsNeverMistakenForATaggedValue(double value)
        {
            SurtrValue boxed = SurtrValue.CreateFloat(value);

            Assert.True(boxed.IsFloat);
            Assert.False(boxed.IsInt);
            Assert.False(boxed.IsBool);
            Assert.False(boxed.IsChar);
            Assert.False(boxed.IsReference);

            if (double.IsNaN(value))
                Assert.True(double.IsNaN(boxed.AsFloat));
            else
                Assert.Equal(value, boxed.AsFloat);
        }

        [Fact]
        public void Float_PreservesTheSignOfNegativeZero()
        {
            SurtrValue positiveZero = SurtrValue.CreateFloat(0.0);
            SurtrValue negativeZero = SurtrValue.CreateFloat(-0.0);

            // 0.0 == -0.0 by value, but NaN boxing must round-trip the raw bit pattern
            // exactly - otherwise the VM's FEQ and a bit-level trace would disagree.
            Assert.NotEqual(positiveZero.Raw, negativeZero.Raw);
            Assert.True(double.IsNegative(negativeZero.AsFloat));
            Assert.False(double.IsNegative(positiveZero.AsFloat));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Bool_RoundTrips(bool value)
        {
            SurtrValue boxed = SurtrValue.CreateBool(value);

            Assert.True(boxed.IsBool);
            Assert.Equal(value, boxed.AsBool);
        }

        [Fact]
        public void Char_RoundTrips()
        {
            SurtrValue boxed = SurtrValue.CreateChar('S');

            Assert.True(boxed.IsChar);
            Assert.Equal('S', boxed.AsChar);
        }

        [Fact]
        public void Reference_RoundTrips_AndTracksNullness()
        {
            SurtrValue nonNull = SurtrValue.CreateReference(42);
            SurtrValue @null = SurtrValue.CreateReference(SurtrValue.NullRef);

            Assert.True(nonNull.IsReference);
            Assert.False(nonNull.IsNullReference);
            Assert.Equal(42, nonNull.AsReference);

            Assert.True(@null.IsReference);
            Assert.True(@null.IsNullReference);
        }

        [Fact]
        public void Null_And_Default_AreTheSameNullReference()
        {
            Assert.Equal(SurtrValue.Default.Raw, SurtrValue.Null.Raw);
            Assert.True(SurtrValue.Null.IsNullReference);
        }

        [Fact]
        public void True_And_False_AreDistinctTaggedBools()
        {
            Assert.True(SurtrValue.True.IsBool);
            Assert.True(SurtrValue.False.IsBool);
            Assert.True(SurtrValue.True.AsBool);
            Assert.False(SurtrValue.False.AsBool);
        }

        [Fact]
        public void FromRaw_IsTheInverseOfRaw()
        {
            SurtrValue original = SurtrValue.CreateInt(1234);
            SurtrValue roundTripped = SurtrValue.FromRaw(original.Raw);

            Assert.Equal(original.Raw, roundTripped.Raw);
        }
    }
}
