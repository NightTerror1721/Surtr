#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;

namespace Surtr.Tests.VM
{
    public class SurtrVirtualMachineTupleAndDictionaryTests
    {
        private static SurtrValue Run(SurtrRuntime runtime, SurtrModule module, BytecodeBuilder builder, int maxStackSize = 32)
        {
            var method = builder.Build(module, localCount: 0, maxStackSize);
            return runtime.Invoke(method);
        }

        #region Tuples

        [Fact]
        public void TupPack_BuildsATupleFromStackValues()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();
            int tupleType = builder.AddType(module.TypeHandles.GetOrAdd(
                SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.Integer)));

            builder
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.PushI32).I32(2)
                .Op(OpCode.TupPack).I16(tupleType).U8(2)
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.TupGet)
                .Op(OpCode.ReturnValue);

            Assert.Equal(2, Run(runtime, module, builder).AsInt);
        }

        [Fact]
        public void TupLen_ReadsTheArity()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var tuple = runtime.NewTuple(
                SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.Integer, SurtrClassReference.Integer), 3);

            var builder = new BytecodeBuilder();
            builder.LoadReference(tuple).Op(OpCode.TupLen).Op(OpCode.ReturnValue);

            Assert.Equal(3, Run(runtime, module, builder).AsInt);
        }

        [Fact]
        public void TupGet_OutOfRange_Traps()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var tuple = runtime.NewTuple(SurtrClassReference.Tuple(SurtrClassReference.Integer), 1);

            var builder = new BytecodeBuilder();
            builder.LoadReference(tuple).Op(OpCode.PushI32).I32(5).Op(OpCode.TupGet).Op(OpCode.ReturnValue);

            Assert.Throws<SurtrExecutionException>(() => Run(runtime, module, builder));
        }

        [Fact]
        public void TupGetC_ReadsTheElementNamedByItsImmediate()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();
            int tupleType = builder.AddType(module.TypeHandles.GetOrAdd(
                SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.Integer, SurtrClassReference.Integer)));

            builder
                .Op(OpCode.PushI32).I32(10)
                .Op(OpCode.PushI32).I32(20)
                .Op(OpCode.PushI32).I32(30)
                .Op(OpCode.TupPack).I16(tupleType).U8(3)
                .Op(OpCode.TupGetC).U8(2)
                .Op(OpCode.ReturnValue);

            Assert.Equal(30, Run(runtime, module, builder).AsInt);
        }

        [Fact]
        public void TupGetC_OutOfRange_Traps()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var tuple = runtime.NewTuple(SurtrClassReference.Tuple(SurtrClassReference.Integer), 1);

            var builder = new BytecodeBuilder();
            builder.LoadReference(tuple).Op(OpCode.TupGetC).U8(5).Op(OpCode.ReturnValue);

            Assert.Throws<SurtrExecutionException>(() => Run(runtime, module, builder));
        }

        [Fact]
        public void TupUnpack_PushesEveryElementInOrder()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var tuple = runtime.NewTuple(
                SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.Integer), 2);
            tuple.SetDuringPack(0, SurtrValue.CreateInt(11));
            tuple.SetDuringPack(1, SurtrValue.CreateInt(22));

            var builder = new BytecodeBuilder();
            builder
                .LoadReference(tuple)
                .Op(OpCode.TupUnpack).U8(2)
                // Stack is now [11, 22]; Sub is order-sensitive, proving unpack order.
                .Op(OpCode.Sub)
                .Op(OpCode.ReturnValue);

            Assert.Equal(-11, Run(runtime, module, builder).AsInt); // 11 - 22
        }

        #endregion

        #region Dictionaries

        [Fact]
        public void DictNew_AllocatesAnEmptyDictionary()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();
            int dictType = builder.AddType(module.TypeHandles.GetOrAdd(
                SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.Integer)));

            builder.Op(OpCode.DictNew).I16(dictType).Op(OpCode.DictLen).Op(OpCode.ReturnValue);

            Assert.Equal(0, Run(runtime, module, builder).AsInt);
        }

        [Fact]
        public void DictPack_BuildsFromKeyValuePairsOnTheStack()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var builder = new BytecodeBuilder();
            int dictType = builder.AddType(module.TypeHandles.GetOrAdd(
                SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.Integer)));

            builder
                .Op(OpCode.PushI32).I32(1).Op(OpCode.PushI32).I32(100) // key 1 -> 100
                .Op(OpCode.PushI32).I32(2).Op(OpCode.PushI32).I32(200) // key 2 -> 200
                .Op(OpCode.DictPack).I16(dictType).I16(2)
                .Op(OpCode.PushI32).I32(2)
                .Op(OpCode.DictGet)
                .Op(OpCode.ReturnValue);

            Assert.Equal(200, Run(runtime, module, builder).AsInt);
        }

        [Fact]
        public void DictGet_MissingKey_Traps()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var dict = runtime.NewDictionary(SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.Integer));

            var builder = new BytecodeBuilder();
            builder.LoadReference(dict).Op(OpCode.PushI32).I32(1).Op(OpCode.DictGet).Op(OpCode.ReturnValue);

            Assert.Throws<SurtrExecutionException>(() => Run(runtime, module, builder));
        }

        [Fact]
        public void DictSet_InsertsOrReplaces()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var dict = runtime.NewDictionary(SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.Integer));

            var builder = new BytecodeBuilder();
            builder
                .LoadReference(dict).Op(OpCode.PushI32).I32(5).Op(OpCode.PushI32).I32(50).Op(OpCode.DictSet)
                .LoadReference(dict).Op(OpCode.PushI32).I32(5).Op(OpCode.DictGet)
                .Op(OpCode.ReturnValue);

            Assert.Equal(50, Run(runtime, module, builder).AsInt);
        }

        [Fact]
        public void DictDel_RemovesTheEntryAndReportsWhetherOneExisted()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var dict = runtime.NewDictionary(SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.Integer));
            dict.Set(SurtrValue.CreateInt(5), SurtrValue.CreateInt(50));

            var builder = new BytecodeBuilder();
            builder.LoadReference(dict).Op(OpCode.PushI32).I32(5).Op(OpCode.DictDel).Op(OpCode.ReturnValue);

            Assert.True(Run(runtime, module, builder).AsBool);
            Assert.Equal(0, dict.Count);
        }

        [Fact]
        public void DictClear_EmptiesTheDictionary()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var dict = runtime.NewDictionary(SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.Integer));
            dict.Set(SurtrValue.CreateInt(1), SurtrValue.CreateInt(1));

            var builder = new BytecodeBuilder();
            builder.LoadReference(dict).Op(OpCode.DictClear).Op(OpCode.PushI32).I32(0).Op(OpCode.ReturnValue);

            Run(runtime, module, builder);
            Assert.Equal(0, dict.Count);
        }

        [Fact]
        public void DictKeys_ReturnsAnArrayOfEveryKey()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var dict = runtime.NewDictionary(SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.Integer));
            dict.Set(SurtrValue.CreateInt(1), SurtrValue.CreateInt(100));
            dict.Set(SurtrValue.CreateInt(2), SurtrValue.CreateInt(200));

            var builder = new BytecodeBuilder();
            int arrayType = builder.AddType(module.TypeHandles.GetOrAdd(SurtrClassReference.Array(SurtrClassReference.Integer)));
            builder.LoadReference(dict).Op(OpCode.DictKeys).I16(arrayType).Op(OpCode.ArrLen).Op(OpCode.ReturnValue);

            Assert.Equal(2, Run(runtime, module, builder).AsInt);
        }

        [Fact]
        public void DictValues_ReturnsAnArrayOfEveryValue()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var dict = runtime.NewDictionary(SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.Integer));
            dict.Set(SurtrValue.CreateInt(1), SurtrValue.CreateInt(100));

            var builder = new BytecodeBuilder();
            int arrayType = builder.AddType(module.TypeHandles.GetOrAdd(SurtrClassReference.Array(SurtrClassReference.Integer)));
            builder
                .LoadReference(dict).Op(OpCode.DictValues).I16(arrayType)
                .Op(OpCode.PushI32).I32(0).Op(OpCode.ArrGet)
                .Op(OpCode.ReturnValue);

            Assert.Equal(100, Run(runtime, module, builder).AsInt);
        }

        [Fact]
        public void DictIn_IsTrueForAPresentKey()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var dict = runtime.NewDictionary(SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.Integer));
            dict.Set(SurtrValue.CreateInt(1), SurtrValue.CreateInt(1));

            var builder = new BytecodeBuilder();
            builder.LoadReference(dict).Op(OpCode.PushI32).I32(1).Op(OpCode.DictIn).Op(OpCode.ReturnValue);

            Assert.True(Run(runtime, module, builder).AsBool);
        }

        #endregion
    }
}
