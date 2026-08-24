#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;
using System;

namespace Surtr.Tests.VM
{
    /// <summary>
    /// The five generator opcodes, exercised at the byte level.
    /// </summary>
    /// <remarks>
    /// These test the protocol the compiler emits <em>against</em>, not what it happens to emit:
    /// that a body suspends with its locals and its pending operands intact, that the two ways out
    /// of a body answer the resumer the same way, and that the states rule out the two orders that
    /// would corrupt a frame. The end-to-end source tests live beside the emitter.
    /// </remarks>
    public class SurtrVirtualMachineGeneratorTests
    {
        /// <summary>
        /// A generator body counting down from its one argument: <c>n, n-1, ..., 1</c>.
        /// </summary>
        /// <remarks>
        /// The counter lives in local 0 across every suspension, which is the point: strategy B
        /// keeps it in the frame rather than promoting it to a heap cell, so a body that reads it
        /// back correctly is evidence the copy round-trips.
        /// </remarks>
        private static SurtrBytecodeMethodInfo BuildCountdownBody()
        {
            var module = new SurtrModule("generators");
            var builder = new BytecodeBuilder();

            int top = builder.NewLabel();
            int end = builder.NewLabel();

            builder
                .MarkLabel(top)
                .Op(OpCode.Ldl0)
                .Op(OpCode.PushI32).I32(0)
                .JumpShort(OpCode.JPLE, end)
                .Op(OpCode.Ldl0)
                .Op(OpCode.Yield)
                .Op(OpCode.Ldl0)
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.Sub)
                .Op(OpCode.Stl0)
                .JumpShort(OpCode.JP, top)
                .MarkLabel(end)
                .Op(OpCode.ReturnVoid);

            return builder.Build(module, localCount: 1, maxStackSize: 8);
        }

        /// <summary>Adds a <c>generator&lt;int&gt;</c> type entry and the body to a caller's tables.</summary>
        private static (int MethodIndex, int TypeIndex) Wire(BytecodeBuilder builder, SurtrModule module, SurtrBytecodeMethodInfo body)
            => (builder.AddMethod(body),
                builder.AddType(module.TypeHandles.GetOrAdd(SurtrClassReference.Generator(SurtrClassReference.Integer))));

        [Fact]
        public void GenNew_DoesNotRunTheBody()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("caller");
            var builder = new BytecodeBuilder();

            // A body whose very first instruction would trap if it ran. Calling a generator
            // function has to be free of side effects (§3.1), so building one must not reach it.
            var bodyModule = new SurtrModule("generators");
            var body = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.PushI32).I32(0)
                .Op(OpCode.Div)
                .Op(OpCode.Yield)
                .Op(OpCode.ReturnVoid)
                .Build(bodyModule, localCount: 0, maxStackSize: 8);

            var (methodIndex, typeIndex) = Wire(builder, module, body);

            builder
                .Op(OpCode.GenNew).I16(methodIndex).I16(typeIndex).U8(0)
                .Op(OpCode.ReturnValue);

            var entry = builder.Build(module, localCount: 0, maxStackSize: 8);
            var result = runtime.Invoke(entry);

            var generator = Assert.IsType<SurtrGenerator>(runtime.Resolve(result));
            Assert.Equal(SurtrGeneratorState.NotStarted, generator.GetState());
        }

        [Fact]
        public void GenResume_WalksTheWholeBodyAndThenReportsExhaustion()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            var (methodIndex, typeIndex) = Wire(builder, module, BuildCountdownBody());

            int top = builder.NewLabel();
            int end = builder.NewLabel();

            // local0: the generator. local1: the running total.
            builder
                .Op(OpCode.PushI32).I32(3)
                .Op(OpCode.GenNew).I16(methodIndex).I16(typeIndex).U8(1)
                .Op(OpCode.GenIterate)
                .Op(OpCode.Stl0)

                .MarkLabel(top)
                .Op(OpCode.Ldl0)
                .Op(OpCode.GenResume)
                .JumpShort(OpCode.JPZ, end)
                .Op(OpCode.Ldl1)
                .Op(OpCode.Ldl0)
                .Op(OpCode.GenCurrent)
                .Op(OpCode.Add)
                .Op(OpCode.Stl1)
                .JumpShort(OpCode.JP, top)

                .MarkLabel(end)
                .Op(OpCode.Ldl1)
                .Op(OpCode.ReturnValue);

            var entry = builder.Build(module, localCount: 2, maxStackSize: 16);
            Assert.Equal(6, runtime.Invoke(entry).AsInt); // 3 + 2 + 1
        }

        [Fact]
        public void AnExhaustedGeneratorKeepsAnsweringFalse()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("caller");
            var builder = new BytecodeBuilder();

            // A body that yields nothing at all: the first resume already finds the end.
            var bodyModule = new SurtrModule("generators");
            var body = new BytecodeBuilder()
                .Op(OpCode.ReturnVoid)
                .Build(bodyModule, localCount: 0, maxStackSize: 4);

            var (methodIndex, typeIndex) = Wire(builder, module, body);

            // Resumes twice and returns the second answer, which has to be false rather than a
            // second entry into a frame that no longer exists.
            builder
                .Op(OpCode.GenNew).I16(methodIndex).I16(typeIndex).U8(0)
                .Op(OpCode.Stl0)
                .Op(OpCode.Ldl0)
                .Op(OpCode.GenResume)
                .Op(OpCode.Pop)
                .Op(OpCode.Ldl0)
                .Op(OpCode.GenResume)
                .Op(OpCode.ReturnValue);

            var entry = builder.Build(module, localCount: 1, maxStackSize: 8);
            Assert.False(runtime.Invoke(entry).AsBool);
        }

        [Fact]
        public void PendingOperandsSurviveASuspension()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("caller");
            var builder = new BytecodeBuilder();

            // 100 is pushed, then the body yields 7 with that 100 still on the operand stack, then
            // adds 5 to it after resuming. Only a suspension that carries the operand stack - not
            // just the locals - can answer 105.
            var bodyModule = new SurtrModule("generators");
            var body = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(100)
                .Op(OpCode.PushI32).I32(7)
                .Op(OpCode.Yield)
                .Op(OpCode.PushI32).I32(5)
                .Op(OpCode.Add)
                .Op(OpCode.Yield)
                .Op(OpCode.ReturnVoid)
                .Build(bodyModule, localCount: 0, maxStackSize: 8);

            var (methodIndex, typeIndex) = Wire(builder, module, body);

            builder
                .Op(OpCode.GenNew).I16(methodIndex).I16(typeIndex).U8(0)
                .Op(OpCode.Stl0)
                .Op(OpCode.Ldl0)
                .Op(OpCode.GenResume)
                .Op(OpCode.Pop)
                .Op(OpCode.Ldl0)
                .Op(OpCode.GenResume)
                .Op(OpCode.Pop)
                .Op(OpCode.Ldl0)
                .Op(OpCode.GenCurrent)
                .Op(OpCode.ReturnValue);

            var entry = builder.Build(module, localCount: 1, maxStackSize: 8);
            Assert.Equal(105, runtime.Invoke(entry).AsInt);
        }

        [Fact]
        public void GenIterate_RefusesAGeneratorThatHasAlreadyStarted()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            var (methodIndex, typeIndex) = Wire(builder, module, BuildCountdownBody());

            builder
                .Op(OpCode.PushI32).I32(2)
                .Op(OpCode.GenNew).I16(methodIndex).I16(typeIndex).U8(1)
                .Op(OpCode.Stl0)
                .Op(OpCode.Ldl0)
                .Op(OpCode.GenResume)
                .Op(OpCode.Pop)
                .Op(OpCode.Ldl0)
                .Op(OpCode.GenIterate)
                .Op(OpCode.ReturnValue);

            var entry = builder.Build(module, localCount: 1, maxStackSize: 8);

            var thrown = Assert.Throws<SurtrExecutionException>(() => runtime.Invoke(entry));
            Assert.Same(SurtrBuiltIns.InvalidOperationException, thrown.SurtrType);
        }

        [Fact]
        public void AGeneratorThatIsAlreadyRunningRefusesToBeResumed()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            var (methodIndex, typeIndex) = Wire(builder, module, BuildCountdownBody());

            builder
                .Op(OpCode.PushI32).I32(2)
                .Op(OpCode.GenNew).I16(methodIndex).I16(typeIndex).U8(1)
                .Op(OpCode.ReturnValue);

            var entry = builder.Build(module, localCount: 0, maxStackSize: 8);
            var generator = Assert.IsType<SurtrGenerator>(runtime.Resolve(runtime.Invoke(entry)));

            // The state is forced rather than reached by a body resuming itself, which would need a
            // generator to name itself before it exists. What matters is the guard: `Running` means
            // a live frame is on the data stack, and resuming would copy a stale frame over it -
            // the one corruption strategy B cannot detect after the fact.
            generator.State = SurtrGeneratorState.Running;

            var thrown = Assert.Throws<SurtrExecutionException>(() => runtime.ResumeGenerator(generator));
            Assert.Same(SurtrBuiltIns.InvalidOperationException, thrown.SurtrType);
        }

        [Fact]
        public void AnExceptionLeavingTheBodyExhaustsTheGenerator()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("caller");
            var builder = new BytecodeBuilder();

            var bodyModule = new SurtrModule("generators");
            var body = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.Yield)
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.PushI32).I32(0)
                .Op(OpCode.Div)
                .Op(OpCode.Yield)
                .Op(OpCode.ReturnVoid)
                .Build(bodyModule, localCount: 0, maxStackSize: 8);

            var (methodIndex, typeIndex) = Wire(builder, module, body);

            builder
                .Op(OpCode.GenNew).I16(methodIndex).I16(typeIndex).U8(0)
                .Op(OpCode.ReturnValue);

            var entry = builder.Build(module, localCount: 0, maxStackSize: 8);
            var generator = Assert.IsType<SurtrGenerator>(runtime.Resolve(runtime.Invoke(entry)));

            Assert.True(runtime.ResumeGenerator(generator));
            Assert.Throws<SurtrExecutionException>(() => runtime.ResumeGenerator(generator));

            // The frame was discarded by the unwind, so there is nothing left to resume into and
            // the generator has to say so rather than try.
            Assert.Equal(SurtrGeneratorState.Exhausted, generator.GetState());
            Assert.False(runtime.ResumeGenerator(generator));
        }

        [Fact]
        public void GenDelegate_WalksTheInnerGeneratorAndThenResumesTheOuter()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("caller");
            var builder = new BytecodeBuilder();

            var innerBody = BuildCountdownBody();
            int innerIndex = builder.AddMethod(innerBody);
            int generatorType = builder.AddType(
                module.TypeHandles.GetOrAdd(SurtrClassReference.Generator(SurtrClassReference.Integer)));

            // The outer yields 100, delegates to a countdown from its own argument, then yields 200.
            // Local 0 holds the countdown's bound, which the outer was called with.
            var outerModule = new SurtrModule("generators");
            var outerBuilder = new BytecodeBuilder();
            int innerInOuter = outerBuilder.AddMethod(innerBody);
            int typeInOuter = outerBuilder.AddType(
                outerModule.TypeHandles.GetOrAdd(SurtrClassReference.Generator(SurtrClassReference.Integer)));

            var outerBody = outerBuilder
                .Op(OpCode.PushI32).I32(100)
                .Op(OpCode.Yield)
                .Op(OpCode.Ldl0)
                .Op(OpCode.GenNew).I16(innerInOuter).I16(typeInOuter).U8(1)
                .Op(OpCode.GenDelegate)
                .Op(OpCode.PushI32).I32(200)
                .Op(OpCode.Yield)
                .Op(OpCode.ReturnVoid)
                .Build(outerModule, localCount: 1, maxStackSize: 8);

            int outerIndex = builder.AddMethod(outerBody);

            builder
                .Op(OpCode.PushI32).I32(2)
                .Op(OpCode.GenNew).I16(outerIndex).I16(generatorType).U8(1)
                .Op(OpCode.ReturnValue);

            _ = innerIndex;

            var entry = builder.Build(module, localCount: 0, maxStackSize: 8);
            var generator = Assert.IsType<SurtrGenerator>(runtime.Resolve(runtime.Invoke(entry)));

            var produced = new System.Collections.Generic.List<int>();
            while (runtime.ResumeGenerator(generator))
                produced.Add(generator.GetCurrent().AsInt);

            // The outer's own elements bracket the inner's, and the consumer never sees the seam.
            Assert.Equal(new[] { 100, 2, 1, 200 }, produced);
            Assert.Equal(SurtrGeneratorState.Exhausted, generator.GetState());
        }

        [Fact]
        public void TheHostResumePathAgreesWithTheCompiledOne()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            var (methodIndex, typeIndex) = Wire(builder, module, BuildCountdownBody());

            builder
                .Op(OpCode.PushI32).I32(3)
                .Op(OpCode.GenNew).I16(methodIndex).I16(typeIndex).U8(1)
                .Op(OpCode.ReturnValue);

            var entry = builder.Build(module, localCount: 0, maxStackSize: 8);
            var generator = Assert.IsType<SurtrGenerator>(runtime.Resolve(runtime.Invoke(entry)));

            // Driven entirely from outside the machine, which is the path a generator travelling as
            // an IIterable<T> takes. It has to produce the same 3, 2, 1 the opcodes do.
            var produced = new System.Collections.Generic.List<int>();
            while (runtime.ResumeGenerator(generator))
                produced.Add(generator.GetCurrent().AsInt);

            Assert.Equal(new[] { 3, 2, 1 }, produced);
            Assert.Equal(SurtrGeneratorState.Exhausted, generator.GetState());
        }
    }
}
