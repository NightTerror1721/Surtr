#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.Runtime.Objects
{
    public class SurtrBoxedTests
    {
        [Fact]
        public void Box_OfAnInt_ReportsIsInt()
        {
            using var runtime = new SurtrRuntime();
            var boxed = runtime.Box(SurtrValue.CreateInt(5));

            Assert.True(boxed.IsInt);
            Assert.False(boxed.IsFloat);
            Assert.False(boxed.IsBool);
            Assert.False(boxed.IsChar);
            Assert.Equal(5, boxed.BoxedValue.AsInt);
        }

        [Fact]
        public void Box_OfAFloat_ReportsIsFloat()
        {
            using var runtime = new SurtrRuntime();
            var boxed = runtime.Box(SurtrValue.CreateFloat(3.5));

            Assert.True(boxed.IsFloat);
            Assert.Equal(3.5, boxed.BoxedValue.AsFloat);
        }

        [Fact]
        public void Box_OfABool_ReportsIsBool()
        {
            using var runtime = new SurtrRuntime();
            var boxed = runtime.Box(SurtrValue.True);

            Assert.True(boxed.IsBool);
        }

        [Fact]
        public void Box_OfAChar_ReportsIsChar()
        {
            using var runtime = new SurtrRuntime();
            var boxed = runtime.Box(SurtrValue.CreateChar('x'));

            Assert.True(boxed.IsChar);
        }

        [Fact]
        public void Box_OfAReference_Throws()
        {
            using var runtime = new SurtrRuntime();
            var reference = runtime.ValueOf(runtime.NewString("hi"));

            Assert.Throws<ArgumentException>(() => runtime.Box(reference));
        }

        [Fact]
        public void ToString_OfABoxedInt_RendersTheNumber()
        {
            using var runtime = new SurtrRuntime();
            Assert.Equal("42", runtime.Box(SurtrValue.CreateInt(42)).ToString());
        }

        [Fact]
        public void ToString_OfABoxedBool_RendersTrueOrFalse()
        {
            using var runtime = new SurtrRuntime();
            Assert.Equal("true", runtime.Box(SurtrValue.True).ToString());
            Assert.Equal("false", runtime.Box(SurtrValue.False).ToString());
        }

        [Fact]
        public void ToString_OfABoxedChar_RendersTheCharacter()
        {
            using var runtime = new SurtrRuntime();
            Assert.Equal("z", runtime.Box(SurtrValue.CreateChar('z')).ToString());
        }

        [Fact]
        public void ToString_OfABoxedFloat_RendersTheNumber()
        {
            using var runtime = new SurtrRuntime();
            string expected = (1.25).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(expected, runtime.Box(SurtrValue.CreateFloat(1.25)).ToString());
        }
    }
}
