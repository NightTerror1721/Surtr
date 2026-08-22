#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;

namespace Surtr.Tests.VM
{
    public class SurtrVirtualMachineClosureTests
    {
        /// <summary>Builds a standalone bytecode method: <c>UpValueGet 0 + Ldl0</c>, returned.</summary>
        private static SurtrBytecodeMethodInfo BuildAddUpValueToArgumentMethod()
        {
            var module = new SurtrModule("target");
            var builder = new BytecodeBuilder()
                .Op(OpCode.UpValueGet).U8(0)
                .Op(OpCode.Ldl0)
                .Op(OpCode.Add)
                .Op(OpCode.ReturnValue);

            return builder.Build(module, localCount: 1, maxStackSize: 8);
        }

        [Fact]
        public void NewClosure_ThenInvokeClosure_CallsThroughToTheCapturedMethod()
        {
            using var runtime = new SurtrRuntime();
            var target = BuildAddUpValueToArgumentMethod();

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(target);

            builder
                .Op(OpCode.PushI32).I32(10)                    // captured value
                .Op(OpCode.NewClosure).I16(methodIndex).U8(1)
                .Op(OpCode.PushI32).I32(5)                      // argument
                .Op(OpCode.InvokeClosure).U8(1).U8(1)
                .Op(OpCode.ReturnValue);

            var callerMethod = builder.Build(callerModule, localCount: 0, maxStackSize: 16);
            Assert.Equal(15, runtime.Invoke(callerMethod).AsInt); // 10 (upvalue) + 5 (argument)
        }

        [Fact]
        public void NewClosureX_UsesAFourByteMethodIndex()
        {
            using var runtime = new SurtrRuntime();
            var target = BuildAddUpValueToArgumentMethod();

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(target);

            builder
                .Op(OpCode.PushI32).I32(100)
                .Op(OpCode.NewClosureX).I32(methodIndex).U8(1)
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.InvokeClosure).U8(1).U8(1)
                .Op(OpCode.ReturnValue);

            var callerMethod = builder.Build(callerModule, localCount: 0, maxStackSize: 16);
            Assert.Equal(101, runtime.Invoke(callerMethod).AsInt);
        }

        [Fact]
        public void Closure_WithNoCaptures_StillCalls()
        {
            using var runtime = new SurtrRuntime();
            var targetModule = new SurtrModule("target");
            var target = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(42)
                .Op(OpCode.ReturnValue)
                .Build(targetModule, localCount: 0, maxStackSize: 4);

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(target);

            builder
                .Op(OpCode.NewClosure).I16(methodIndex).U8(0)
                .Op(OpCode.InvokeClosure).U8(0).U8(1)
                .Op(OpCode.ReturnValue);

            var callerMethod = builder.Build(callerModule, localCount: 0, maxStackSize: 8);
            Assert.Equal(42, runtime.Invoke(callerMethod).AsInt);
        }

        [Fact]
        public void TwoClosuresOverTheSameMethod_CaptureIndependentValues()
        {
            using var runtime = new SurtrRuntime();
            var target = BuildAddUpValueToArgumentMethod();

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(target);

            builder
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.NewClosure).I16(methodIndex).U8(1)   // closure A, captures 1
                .Op(OpCode.PushI32).I32(1000)
                .Op(OpCode.NewClosure).I16(methodIndex).U8(1)   // closure B, captures 1000
                .Op(OpCode.PushI32).I32(1)                       // argument for B
                .Op(OpCode.InvokeClosure).U8(1).U8(1)            // call B -> 1000 + 1 = 1001
                .Op(OpCode.Swap)                                 // bring closure A on top
                .Op(OpCode.PushI32).I32(1)                       // argument for A
                .Op(OpCode.InvokeClosure).U8(1).U8(1)            // call A -> 1 + 1 = 2
                .Op(OpCode.Add)                                  // 1001 + 2
                .Op(OpCode.ReturnValue);

            var callerMethod = builder.Build(callerModule, localCount: 0, maxStackSize: 16);
            Assert.Equal(1003, runtime.Invoke(callerMethod).AsInt);
        }

        [Fact]
        public void NewFunction_ThenInvokeClosure_CallsThroughToTheMethod()
        {
            using var runtime = new SurtrRuntime();
            var targetModule = new SurtrModule("target");
            var target = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(42)
                .Op(OpCode.ReturnValue)
                .Build(targetModule, localCount: 0, maxStackSize: 4);

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(target);

            builder
                .Op(OpCode.NewFunction).I16(methodIndex)
                .Op(OpCode.InvokeClosure).U8(0).U8(1)
                .Op(OpCode.ReturnValue);

            var callerMethod = builder.Build(callerModule, localCount: 0, maxStackSize: 8);
            Assert.Equal(42, runtime.Invoke(callerMethod).AsInt);
        }

        [Fact]
        public void NewFunctionX_UsesAFourByteMethodIndex()
        {
            using var runtime = new SurtrRuntime();
            var targetModule = new SurtrModule("target");
            var target = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(7)
                .Op(OpCode.ReturnValue)
                .Build(targetModule, localCount: 0, maxStackSize: 4);

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(target);

            builder
                .Op(OpCode.NewFunctionX).I32(methodIndex)
                .Op(OpCode.InvokeClosure).U8(0).U8(1)
                .Op(OpCode.ReturnValue);

            var callerMethod = builder.Build(callerModule, localCount: 0, maxStackSize: 8);
            Assert.Equal(7, runtime.Invoke(callerMethod).AsInt);
        }

        [Fact]
        public void EveryEvaluationOfNewFunction_ReturnsTheSameReference()
        {
            using var runtime = new SurtrRuntime();
            var targetModule = new SurtrModule("target");
            var target = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.ReturnValue)
                .Build(targetModule, localCount: 0, maxStackSize: 4);

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(target);

            builder
                .Op(OpCode.NewFunction).I16(methodIndex)
                .Op(OpCode.ReturnValue);

            var callerMethod = builder.Build(callerModule, localCount: 0, maxStackSize: 8);

            // The opcode hands back the one shared closure for the method, so two evaluations - in
            // two separate runs - are the same value. That is the referential transparency that
            // makes a zero-capture lambda an ordinary function.
            SurtrValue first = runtime.Invoke(callerMethod);
            SurtrValue second = runtime.Invoke(callerMethod);
            Assert.True(first.IsReference);
            Assert.Equal(first.Raw, second.Raw);
        }
    }
}
