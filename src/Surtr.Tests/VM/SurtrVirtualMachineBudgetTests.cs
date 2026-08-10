#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;

namespace Surtr.Tests.VM
{
    /// <summary>
    /// Covers the instruction budget: the ceiling that lets a compiler fold a <c>const fun</c> by
    /// running it on the real interpreter without risking a hang.
    /// </summary>
    public class SurtrVirtualMachineBudgetTests
    {
        /// <summary>An unconditional backward jump to itself: the shortest program that never ends.</summary>
        private static SurtrBytecodeMethodInfo SpinForever(SurtrModule module)
        {
            var builder = new BytecodeBuilder();
            int top = builder.NewLabel();
            builder
                .MarkLabel(top)
                .JumpShort(OpCode.JP, top);

            return builder.Build(module, 0, 4);
        }

        private static SurtrBytecodeMethodInfo ReturnsSeven(SurtrModule module)
        {
            var builder = new BytecodeBuilder();
            builder.Op(OpCode.PushI32).I32(7).Op(OpCode.ReturnValue);
            return builder.Build(module, 0, 4);
        }

        [Fact]
        public void WithNoBudget_TheDefaultIsUnlimited()
        {
            using var runtime = new SurtrRuntime();
            Assert.Equal(0, runtime.InstructionBudget);
        }

        [Fact]
        public void ANonTerminatingProgram_AbortsOnceTheBudgetRunsOut()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = SpinForever(module);

            runtime.InstructionBudget = 10_000;

            var error = Assert.Throws<SurtrBudgetExceededException>(() => runtime.Invoke(method));
            Assert.Contains("budget", error.Message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AProgramThatFitsTheBudget_RunsNormally()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = ReturnsSeven(module);

            runtime.InstructionBudget = 1_000;

            Assert.Equal(7, runtime.Invoke(method).AsInt);
        }

        [Fact]
        public void TheBudget_IsConsumedAcrossCallsUntilItIsSetAgain()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var method = ReturnsSeven(module);

            runtime.InstructionBudget = 4;
            Assert.Equal(7, runtime.Invoke(method).AsInt);

            // Two instructions went into the first call, so what is left is visible and finite.
            Assert.True(runtime.InstructionBudget > 0);
            Assert.True(runtime.InstructionBudget < 4);
        }

        [Fact]
        public void ABudgetSetBeforeAnythingHasRun_StillApplies()
        {
            using var runtime = new SurtrRuntime();

            // Set before the machine exists at all - it is built lazily on first use.
            runtime.InstructionBudget = 50;

            var module = new SurtrModule("test");
            Assert.Throws<SurtrBudgetExceededException>(() => runtime.Invoke(SpinForever(module)));
        }

        [Fact]
        public void ANegativeBudget_ReadsAsUnlimitedRatherThanAsAnImmediateAbort()
        {
            using var runtime = new SurtrRuntime();
            runtime.InstructionBudget = -1;

            Assert.Equal(0, runtime.InstructionBudget);

            var module = new SurtrModule("test");
            Assert.Equal(7, runtime.Invoke(ReturnsSeven(module)).AsInt);
        }

        [Fact]
        public void RaiseAndCatchInALoop_IsBilledToo()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");

            // try { throw } catch { <back to the top> } - the instructions between raises are
            // never written back, so without a charge on handler entry this would spin forever.
            var builder = new BytecodeBuilder();
            int top = builder.NewLabel();

            int tryStart = builder.Position;
            builder.MarkLabel(top).Op(OpCode.PushNull).Op(OpCode.Throw);
            int tryEnd = builder.Position;

            int handlerOffset = builder.Position;
            builder.Op(OpCode.Pop).JumpShort(OpCode.JP, top);

            var method = builder.Build(module, 0, 4);
            method.SetExceptionHandlers(new[]
            {
                new SurtrExceptionHandler(tryStart, tryEnd, handlerOffset, catchType: null),
            });

            runtime.InstructionBudget = 10_000;

            Assert.Throws<SurtrBudgetExceededException>(() => runtime.Invoke(method));
        }

        [Fact]
        public void ACatchAllHandler_CannotSwallowTheAbort()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");

            // The spin sits inside a try with a catch-all. If the abort were an ordinary trap the
            // handler would take it and the program would carry on looping, which is exactly the
            // guarantee the budget exists to make.
            var builder = new BytecodeBuilder();
            int top = builder.NewLabel();

            int tryStart = builder.Position;
            builder.MarkLabel(top).JumpShort(OpCode.JP, top);
            int tryEnd = builder.Position;

            int handlerOffset = builder.Position;
            builder.Op(OpCode.Pop).Op(OpCode.PushI32).I32(1).Op(OpCode.ReturnValue);

            var method = builder.Build(module, 0, 4);
            method.SetExceptionHandlers(new[]
            {
                new SurtrExceptionHandler(tryStart, tryEnd, handlerOffset, catchType: null),
            });

            runtime.InstructionBudget = 5_000;

            Assert.Throws<SurtrBudgetExceededException>(() => runtime.Invoke(method));
        }

        [Fact]
        public void AnExhaustedBudget_KeepsAbortingUntilTheHostSetsANewOne()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");

            runtime.InstructionBudget = 5_000;
            Assert.Throws<SurtrBudgetExceededException>(() => runtime.Invoke(SpinForever(module)));

            // Exhausted, not cleared - otherwise the next run would silently be unlimited.
            runtime.ResetExecution();
            Assert.Throws<SurtrBudgetExceededException>(() => runtime.Invoke(ReturnsSeven(module)));

            runtime.InstructionBudget = 1_000;
            Assert.Equal(7, runtime.Invoke(ReturnsSeven(module)).AsInt);
        }
    }
}
