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
            builder.LoadReference(left).LoadReference(right).Op(OpCode.StrCat).Op(OpCode.ReturnValue);

            var result = Run(runtime, builder);
            var resultString = runtime.Resolve<SurtrString>(result);
            Assert.Equal("foobar", resultString!.Value);
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
