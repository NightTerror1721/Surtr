#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;

namespace Surtr.Tests.VM
{
    public class SurtrVirtualMachineArrayTests
    {
        private static SurtrValue Run(SurtrRuntime runtime, BytecodeBuilder builder, int maxStackSize = 32)
        {
            var module = new SurtrModule("test");
            var method = builder.Build(module, localCount: 0, maxStackSize);
            return runtime.Invoke(method);
        }

        #region Allocation

        [Fact]
        public void ArrNew_AllocatesAZeroedIntArray()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();
            int arrayType = builder.AddType(module.TypeHandles.GetOrAdd(SurtrClassReference.Array(SurtrClassReference.Integer)));

            builder
                .Op(OpCode.PushI32).I32(3)
                .Op(OpCode.ArrNew).I16(arrayType)
                .Op(OpCode.PushI32).I32(0)
                .Op(OpCode.ArrGet)
                .Op(OpCode.ReturnValue);

            var method = builder.Build(module, localCount: 0, maxStackSize: 16);
            var result = runtime.Invoke(method);

            Assert.True(result.IsInt);
            Assert.Equal(0, result.AsInt);
        }

        [Fact]
        public void ArrNew_OfAReferenceElementType_ZeroesToAnUntaggedNull()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();
            int arrayType = builder.AddType(module.TypeHandles.GetOrAdd(SurtrClassReference.Array(SurtrClassReference.String)));

            builder
                .Op(OpCode.PushI32).I32(2)
                .Op(OpCode.ArrNew).I16(arrayType)
                .Op(OpCode.PushI32).I32(0)
                .Op(OpCode.ArrGet)
                .Op(OpCode.ReturnValue);

            var method = builder.Build(module, localCount: 0, maxStackSize: 16);
            var result = runtime.Invoke(method);

            Assert.Equal(SurtrValue.NullRef, result.AsReference);
        }

        [Fact]
        public void ArrNewX_AllocatesWithAFourByteLength()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();
            int arrayType = builder.AddType(module.TypeHandles.GetOrAdd(SurtrClassReference.Array(SurtrClassReference.Integer)));

            builder
                .Op(OpCode.ArrNewX).I16(arrayType).I32(5)
                .Op(OpCode.ArrLen)
                .Op(OpCode.ReturnValue);

            var method = builder.Build(module, localCount: 0, maxStackSize: 16);
            Assert.Equal(5, runtime.Invoke(method).AsInt);
        }

        [Fact]
        public void ArrPack_BuildsAnArrayFromStackValues()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();
            int arrayType = builder.AddType(module.TypeHandles.GetOrAdd(SurtrClassReference.Array(SurtrClassReference.Integer)));

            builder
                .Op(OpCode.PushI32).I32(10)
                .Op(OpCode.PushI32).I32(20)
                .Op(OpCode.PushI32).I32(30)
                .Op(OpCode.ArrPack).I16(arrayType).I16(3)
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.ArrGet)
                .Op(OpCode.ReturnValue);

            var method = builder.Build(module, localCount: 0, maxStackSize: 16);
            Assert.Equal(20, runtime.Invoke(method).AsInt);
        }

        #endregion

        #region Length, get, set

        [Fact]
        public void ArrLen_ReadsTheElementCount()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            array.Add(SurtrValue.CreateInt(1));
            array.Add(SurtrValue.CreateInt(2));

            var builder = new BytecodeBuilder();
            builder.LoadReference(array).Op(OpCode.ArrLen).Op(OpCode.ReturnValue);

            Assert.Equal(2, Run(runtime, builder).AsInt);
        }

        [Fact]
        public void ArrGet_OutOfRange_Traps()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));

            var builder = new BytecodeBuilder();
            builder.LoadReference(array).Op(OpCode.PushI32).I32(0).Op(OpCode.ArrGet).Op(OpCode.ReturnValue);

            Assert.Throws<SurtrExecutionException>(() => Run(runtime, builder));
        }

        [Fact]
        public void ArrSet_WritesAnElement()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            array.Add(SurtrValue.CreateInt(0));

            var builder = new BytecodeBuilder();
            builder
                .LoadReference(array).Op(OpCode.PushI32).I32(0).Op(OpCode.PushI32).I32(99).Op(OpCode.ArrSet)
                .LoadReference(array).Op(OpCode.PushI32).I32(0).Op(OpCode.ArrGet)
                .Op(OpCode.ReturnValue);

            Assert.Equal(99, Run(runtime, builder).AsInt);
        }

        [Fact]
        public void ArrSet_OutOfRange_Traps()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));

            var builder = new BytecodeBuilder();
            builder.LoadReference(array).Op(OpCode.PushI32).I32(0).Op(OpCode.PushI32).I32(1).Op(OpCode.ArrSet).Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue);

            Assert.Throws<SurtrExecutionException>(() => Run(runtime, builder));
        }

        #endregion

        #region Push, pop, insert, remove, clear

        [Fact]
        public void ArrPush_GrowsTheArray()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));

            var builder = new BytecodeBuilder();
            builder
                .LoadReference(array).Op(OpCode.PushI32).I32(7).Op(OpCode.ArrPush)
                .LoadReference(array).Op(OpCode.ArrLen)
                .Op(OpCode.ReturnValue);

            Assert.Equal(1, Run(runtime, builder).AsInt);
            Assert.Equal(7, array[0].AsInt);
        }

        [Fact]
        public void ArrPop_RemovesAndReturnsTheLastElement()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            array.Add(SurtrValue.CreateInt(1));
            array.Add(SurtrValue.CreateInt(2));

            var builder = new BytecodeBuilder();
            builder.LoadReference(array).Op(OpCode.ArrPop).Op(OpCode.ReturnValue);

            Assert.Equal(2, Run(runtime, builder).AsInt);
            Assert.Equal(1, array.Length);
        }

        [Fact]
        public void ArrPop_OfAnEmptyArray_Traps()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));

            var builder = new BytecodeBuilder();
            builder.LoadReference(array).Op(OpCode.ArrPop).Op(OpCode.ReturnValue);

            Assert.Throws<SurtrExecutionException>(() => Run(runtime, builder));
        }

        [Fact]
        public void ArrInsert_ShiftsSubsequentElementsUp()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            array.Add(SurtrValue.CreateInt(1));
            array.Add(SurtrValue.CreateInt(3));

            var builder = new BytecodeBuilder();
            builder
                .LoadReference(array).Op(OpCode.PushI32).I32(1).Op(OpCode.PushI32).I32(2).Op(OpCode.ArrInsert)
                .LoadReference(array).Op(OpCode.PushI32).I32(1).Op(OpCode.ArrGet)
                .Op(OpCode.ReturnValue);

            Assert.Equal(2, Run(runtime, builder).AsInt);
            Assert.Equal(3, array.Length);
        }

        [Fact]
        public void ArrInsert_OutOfRange_Traps()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));

            var builder = new BytecodeBuilder();
            builder.LoadReference(array).Op(OpCode.PushI32).I32(5).Op(OpCode.PushI32).I32(1).Op(OpCode.ArrInsert).Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue);

            Assert.Throws<SurtrExecutionException>(() => Run(runtime, builder));
        }

        [Fact]
        public void ArrRemoveAt_ShiftsSubsequentElementsDown()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            array.Add(SurtrValue.CreateInt(1));
            array.Add(SurtrValue.CreateInt(2));
            array.Add(SurtrValue.CreateInt(3));

            var builder = new BytecodeBuilder();
            builder
                .LoadReference(array).Op(OpCode.PushI32).I32(1).Op(OpCode.ArrRemoveAt)
                .LoadReference(array).Op(OpCode.ArrLen)
                .Op(OpCode.ReturnValue);

            Assert.Equal(2, Run(runtime, builder).AsInt);
            Assert.Equal(1, array[0].AsInt);
            Assert.Equal(3, array[1].AsInt);
        }

        [Fact]
        public void ArrRemoveAt_OutOfRange_Traps()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));

            var builder = new BytecodeBuilder();
            builder.LoadReference(array).Op(OpCode.PushI32).I32(0).Op(OpCode.ArrRemoveAt).Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue);

            Assert.Throws<SurtrExecutionException>(() => Run(runtime, builder));
        }

        [Fact]
        public void ArrClear_EmptiesTheArray()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            array.Add(SurtrValue.CreateInt(1));

            var builder = new BytecodeBuilder();
            builder.LoadReference(array).Op(OpCode.ArrClear).Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue);

            Run(runtime, builder);
            Assert.Equal(0, array.Length);
        }

        #endregion

        #region Search

        [Fact]
        public void ArrIndexOf_UsesValueSemantics()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.String));
            array.Add(runtime.ValueOf(runtime.NewString("hello")));

            var builder = new BytecodeBuilder();
            builder.LoadReference(array).LoadReference(runtime.NewString("hello")).Op(OpCode.ArrIndexOf).Op(OpCode.ReturnValue);

            Assert.Equal(0, Run(runtime, builder).AsInt);
        }

        [Fact]
        public void ArrIn_IsTrueWhenPresent()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            array.Add(SurtrValue.CreateInt(5));

            var builder = new BytecodeBuilder();
            builder.LoadReference(array).Op(OpCode.PushI32).I32(5).Op(OpCode.ArrIn).Op(OpCode.ReturnValue);

            Assert.True(Run(runtime, builder).AsBool);
        }

        [Fact]
        public void ArrNIn_IsTrueWhenAbsent()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            array.Add(SurtrValue.CreateInt(5));

            var builder = new BytecodeBuilder();
            builder.LoadReference(array).Op(OpCode.PushI32).I32(6).Op(OpCode.ArrNIn).Op(OpCode.ReturnValue);

            Assert.True(Run(runtime, builder).AsBool);
        }

        #endregion
    }
}
