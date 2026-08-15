#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;

namespace Surtr.Tests.VM
{
    public class SurtrVirtualMachineStringTests
    {
        private static SurtrValue Run(SurtrRuntime runtime, BytecodeBuilder builder, int maxStackSize = 32)
        {
            var module = new SurtrModule("test");
            var method = builder.Build(module, localCount: 0, maxStackSize);
            return runtime.Invoke(method);
        }

        [Fact]
        public void StrLen_ReadsTheTextLength()
        {
            using var runtime = new SurtrRuntime();
            var text = runtime.NewString("hello");

            var builder = new BytecodeBuilder();
            builder.LoadReference(text).Op(OpCode.StrLen).Op(OpCode.ReturnValue);

            Assert.Equal(5, Run(runtime, builder).AsInt);
        }

        [Fact]
        public void StrCat_ConcatenatesIntoANewString()
        {
            using var runtime = new SurtrRuntime();
            var left = runtime.NewString("foo");
            var right = runtime.NewString("bar");

            var builder = new BytecodeBuilder();
            builder.LoadReference(left).LoadReference(right).Op(OpCode.StrCat).U8(2).Op(OpCode.ReturnValue);

            var result = Run(runtime, builder);
            var resultString = runtime.Resolve<SurtrString>(result);
            Assert.Equal("foobar", resultString!.Value);
        }

        /// <summary>
        /// The count is what makes an interpolation one allocation instead of n - 1, so the wide
        /// path has to join in stack order and get the length right in one pass.
        /// </summary>
        [Fact]
        public void StrCat_JoinsSeveralOperandsInStackOrder()
        {
            using var runtime = new SurtrRuntime();

            var builder = new BytecodeBuilder();
            builder
                .LoadReference(runtime.NewString("a"))
                .LoadReference(runtime.NewString("bb"))
                .LoadReference(runtime.NewString(string.Empty))
                .LoadReference(runtime.NewString("ccc"))
                .LoadReference(runtime.NewString("d"))
                .Op(OpCode.StrCat).U8(5)
                .Op(OpCode.ReturnValue);

            Assert.Equal("abbcccd", runtime.Resolve<SurtrString>(Run(runtime, builder))!.Value);
        }

        /// <summary>Past the reusable buffer's initial size, so the growth path runs too.</summary>
        [Fact]
        public void StrCat_JoinsMoreOperandsThanTheBufferStartsWith()
        {
            using var runtime = new SurtrRuntime();

            const int count = 20;
            var builder = new BytecodeBuilder();

            for (int i = 0; i < count; i++)
                builder.LoadReference(runtime.NewString(i.ToString()));

            builder.Op(OpCode.StrCat).U8(count).Op(OpCode.ReturnValue);

            var expected = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
                expected.Append(i);

            Assert.Equal(expected.ToString(), runtime.Resolve<SurtrString>(Run(runtime, builder, maxStackSize: 64))!.Value);
        }

        [Fact]
        public void StrHash_PushesTheTextsHash()
        {
            using var runtime = new SurtrRuntime();
            var text = runtime.NewString("hello");

            var builder = new BytecodeBuilder();
            builder.LoadReference(text).Op(OpCode.StrHash).Op(OpCode.ReturnValue);

            Assert.Equal(SurtrString.ComputeHash("hello"), Run(runtime, builder).AsInt);
        }

        /// <summary>
        /// The hash a compiler bakes into a switch has to be the one the program computes later,
        /// in another process — so it depends on the text and on nothing else.
        /// </summary>
        [Fact]
        public void StrHash_DependsOnlyOnTheText()
        {
            using var runtime = new SurtrRuntime();

            var builder = new BytecodeBuilder();
            builder.LoadReference(runtime.NewString("state")).Op(OpCode.StrHash).Op(OpCode.ReturnValue);

            // Two distinct objects holding the same text, one of them built long after the other.
            Assert.Equal(SurtrString.ComputeHash("state"), Run(runtime, builder).AsInt);
            Assert.Equal(runtime.NewString("state").Hash, Run(runtime, builder).AsInt);

            // Golden values. These are what pin the algorithm down: change how the hash is
            // computed and every switch in every already-compiled module silently takes the wrong
            // arm, so a deliberate change has to come here and say so.
            Assert.Equal(-742319854, SurtrString.ComputeHash("state"));
            Assert.Equal(unchecked((int)2166136261), SurtrString.ComputeHash(""));
        }

        /// <summary>Equal text hashes equally; the point of the whole exercise.</summary>
        [Fact]
        public void StrHash_AgreesWithStringEquality()
        {
            using var runtime = new SurtrRuntime();

            var left = runtime.NewString("idle");
            var right = runtime.NewString("idle");

            Assert.NotSame(left, right);
            Assert.True(left.TextEquals(right));
            Assert.Equal(left.Hash, right.Hash);
        }

        [Fact]
        public void StrGet_ReadsACharacterAtAnIndex()
        {
            using var runtime = new SurtrRuntime();
            var text = runtime.NewString("hello");

            var builder = new BytecodeBuilder();
            builder.LoadReference(text).Op(OpCode.PushI32).I32(1).Op(OpCode.StrGet).Op(OpCode.ReturnValue);

            Assert.Equal('e', Run(runtime, builder).AsChar);
        }

        [Fact]
        public void StrGet_OutOfRange_Traps()
        {
            using var runtime = new SurtrRuntime();
            var text = runtime.NewString("hi");

            var builder = new BytecodeBuilder();
            builder.LoadReference(text).Op(OpCode.PushI32).I32(5).Op(OpCode.StrGet).Op(OpCode.ReturnValue);

            Assert.Throws<SurtrExecutionException>(() => Run(runtime, builder));
        }

        [Fact]
        public void StrEQ_ComparesByContent_NotIdentity()
        {
            using var runtime = new SurtrRuntime();
            var left = runtime.NewString("hello");
            var right = runtime.NewString("hello"); // a distinct object, same text

            var builder = new BytecodeBuilder();
            builder.LoadReference(left).LoadReference(right).Op(OpCode.StrEQ).Op(OpCode.ReturnValue);

            Assert.True(Run(runtime, builder).AsBool);
        }

        [Fact]
        public void StrEQ_OfDifferentText_IsFalse()
        {
            using var runtime = new SurtrRuntime();
            var left = runtime.NewString("hello");
            var right = runtime.NewString("world");

            var builder = new BytecodeBuilder();
            builder.LoadReference(left).LoadReference(right).Op(OpCode.StrEQ).Op(OpCode.ReturnValue);

            Assert.False(Run(runtime, builder).AsBool);
        }

        [Fact]
        public void StrNE_OfDifferentText_IsTrue()
        {
            using var runtime = new SurtrRuntime();
            var left = runtime.NewString("hello");
            var right = runtime.NewString("world");

            var builder = new BytecodeBuilder();
            builder.LoadReference(left).LoadReference(right).Op(OpCode.StrNE).Op(OpCode.ReturnValue);

            Assert.True(Run(runtime, builder).AsBool);
        }

        [Fact]
        public void JPStrEQ_BranchesWhenContentMatches()
        {
            using var runtime = new SurtrRuntime();
            var left = runtime.NewString("match");
            var right = runtime.NewString("match");

            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .LoadReference(left).LoadReference(right)
                .JumpShort(OpCode.JPStrEQ, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue) // not taken
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue); // taken

            Assert.Equal(1, Run(runtime, builder).AsInt);
        }

        [Fact]
        public void JPStrNE_BranchesWhenContentDiffers()
        {
            using var runtime = new SurtrRuntime();
            var left = runtime.NewString("left");
            var right = runtime.NewString("right");

            var builder = new BytecodeBuilder();
            int taken = builder.NewLabel();
            builder
                .LoadReference(left).LoadReference(right)
                .JumpShort(OpCode.JPStrNE, taken)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue)
                .MarkLabel(taken)
                .Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(runtime, builder).AsInt);
        }
    }
}
