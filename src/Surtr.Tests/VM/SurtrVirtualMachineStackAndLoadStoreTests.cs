#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Objects;

namespace Surtr.Tests.VM
{
    public class SurtrVirtualMachineStackAndLoadStoreTests
    {
        private static SurtrValue Run(BytecodeBuilder builder, int localCount = 8, int maxStackSize = 32)
        {
            using var runtime = new SurtrRuntime();
            var module = new Surtr.Runtime.Classes.SurtrModule("test");
            var method = builder.Build(module, localCount, maxStackSize);
            return runtime.Invoke(method);
        }

        #region Stack operations

        [Fact]
        public void Nop_DoesNothing()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(5)
                .Op(OpCode.Nop)
                .Op(OpCode.Nop)
                .Op(OpCode.ReturnValue);

            Assert.Equal(5, Run(builder).AsInt);
        }

        [Fact]
        public void Dup_DuplicatesTheTopOfStack()
        {
            // 20 + Dup(=20) = 40: only true if Dup actually pushed a second 20.
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(20)
                .Op(OpCode.Dup)
                .Op(OpCode.Add)
                .Op(OpCode.ReturnValue);

            Assert.Equal(40, Run(builder).AsInt);
        }

        [Fact]
        public void PushNull_PushesANullReference()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushNull)
                .Op(OpCode.ReturnValue);

            var result = Run(builder);
            Assert.True(result.IsReference);
            Assert.True(result.IsNullReference);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(127)]
        [InlineData(-128)]
        [InlineData(-1)]
        public void PushI8_SignExtendsIntoAnInt(int value)
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI8).U8(value)
                .Op(OpCode.ReturnValue);

            Assert.Equal(value, Run(builder).AsInt);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(32767)]
        [InlineData(-32768)]
        [InlineData(-1)]
        public void PushI16_SignExtendsIntoAnInt(int value)
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI16).I16(value)
                .Op(OpCode.ReturnValue);

            Assert.Equal(value, Run(builder).AsInt);
        }

        [Fact]
        public void PushTrue_PushesATaggedBoolean()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushTrue)
                .Op(OpCode.ReturnValue);

            var result = Run(builder);
            Assert.True(result.IsBool);
            Assert.True(result.AsBool);
        }

        [Fact]
        public void PushFalse_PushesATaggedBoolean()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushFalse)
                .Op(OpCode.ReturnValue);

            var result = Run(builder);
            Assert.True(result.IsBool);
            Assert.False(result.AsBool);
        }

        /// <summary>
        /// The tag is the point: an untagged 1 would still read as true, but it would report the
        /// wrong class and box as an <c>int</c>.
        /// </summary>
        [Theory]
        [InlineData('a')]
        [InlineData('\0')]
        [InlineData('￿')]
        public void PushChar_CarriesTheWholeCodeUnitRangeInline(char value)
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushChar).I16(value)
                .Op(OpCode.ReturnValue);

            var result = Run(builder);
            Assert.True(result.IsChar);
            Assert.Equal(value, result.AsChar);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(int.MaxValue)]
        [InlineData(int.MinValue)]
        [InlineData(-1)]
        public void PushI32_CarriesTheFullRange(int value)
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(value)
                .Op(OpCode.ReturnValue);

            Assert.Equal(value, Run(builder).AsInt);
        }

        [Fact]
        public void Pop_DiscardsTheTopOfStack()
        {
            // If Pop failed to discard, the top would still be 99, not 7.
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(7)
                .Op(OpCode.PushI32).I32(99)
                .Op(OpCode.Pop)
                .Op(OpCode.ReturnValue);

            Assert.Equal(7, Run(builder).AsInt);
        }

        #endregion

        #region Constants

        [Theory]
        [InlineData(OpCode.Ldc0, 0)]
        [InlineData(OpCode.Ldc1, 1)]
        [InlineData(OpCode.Ldc2, 2)]
        [InlineData(OpCode.Ldc3, 3)]
        [InlineData(OpCode.Ldc4, 4)]
        [InlineData(OpCode.Ldc5, 5)]
        [InlineData(OpCode.Ldc6, 6)]
        [InlineData(OpCode.Ldc7, 7)]
        [InlineData(OpCode.Ldc8, 8)]
        [InlineData(OpCode.Ldc9, 9)]
        public void LdcDedicated_ReadsItsFixedPoolSlot(OpCode op, int slot)
        {
            var builder = new BytecodeBuilder();
            for (int i = 0; i <= slot; i++)
                builder.Constant(SurtrValue.CreateInt(i * 111).Raw);

            builder.Op(op).Op(OpCode.ReturnValue);

            Assert.Equal(slot * 111, Run(builder).AsInt);
        }

        [Fact]
        public void Ldc_ReadsAnArbitraryTwoBytePoolIndex()
        {
            var builder = new BytecodeBuilder();
            for (int i = 0; i < 20; i++)
                builder.Constant(SurtrValue.CreateInt(i).Raw);

            builder.Op(OpCode.Ldc).I16(15).Op(OpCode.ReturnValue);

            Assert.Equal(15, Run(builder).AsInt);
        }

        [Fact]
        public void LdcS_ReadsAOneBytePoolIndex()
        {
            var builder = new BytecodeBuilder();
            builder.LoadConstant(SurtrValue.CreateInt(321).Raw).Op(OpCode.ReturnValue);

            Assert.Equal(321, Run(builder).AsInt);
        }

        [Fact]
        public void LdcX_ReadsAFourBytePoolIndex()
        {
            var builder = new BytecodeBuilder();
            for (int i = 0; i < 300; i++)
                builder.Constant(SurtrValue.CreateInt(i).Raw);

            builder.Op(OpCode.Wide).Op(OpCode.Ldc).I32(299).Op(OpCode.ReturnValue);

            Assert.Equal(299, Run(builder).AsInt);
        }

        [Fact]
        public void Ldc_CanLoadAFloatConstant()
        {
            var builder = new BytecodeBuilder();
            builder.LoadFloat(3.5).Op(OpCode.ReturnValue);

            Assert.Equal(3.5, Run(builder).AsFloat);
        }

        #endregion

        #region Locals

        [Theory]
        [InlineData(OpCode.Stl0, OpCode.Ldl0, 0)]
        [InlineData(OpCode.Stl1, OpCode.Ldl1, 1)]
        [InlineData(OpCode.Stl2, OpCode.Ldl2, 2)]
        [InlineData(OpCode.Stl3, OpCode.Ldl3, 3)]
        [InlineData(OpCode.Stl4, OpCode.Ldl4, 4)]
        [InlineData(OpCode.Stl5, OpCode.Ldl5, 5)]
        public void DedicatedStlAndLdl_RoundTripThroughTheirFixedSlot(OpCode store, OpCode load, int slot)
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(1000 + slot)
                .Op(store)
                .Op(load)
                .Op(OpCode.ReturnValue);

            Assert.Equal(1000 + slot, Run(builder, localCount: 6).AsInt);
        }

        [Fact]
        public void StlS_And_LdlS_RoundTripThroughAOneByteSlotIndex()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(77)
                .Op(OpCode.StlS).U8(6)
                .Op(OpCode.LdlS).U8(6)
                .Op(OpCode.ReturnValue);

            Assert.Equal(77, Run(builder, localCount: 8).AsInt);
        }

        [Fact]
        public void Stl_And_Ldl_RoundTripThroughATwoByteSlotIndex()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(555)
                .Op(OpCode.Stl).I16(20)
                .Op(OpCode.Ldl).I16(20)
                .Op(OpCode.ReturnValue);

            Assert.Equal(555, Run(builder, localCount: 24).AsInt);
        }

        [Theory]
        [InlineData(1, 41)]
        [InlineData(-1, 39)]
        [InlineData(127, 167)]
        [InlineData(-128, -88)]
        public void IncLocal_AddsItsSignedDeltaInPlace(int delta, int expected)
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(40)
                .Op(OpCode.StlS).U8(2)
                .Op(OpCode.IncLocal).U8(2).U8(delta)
                .Op(OpCode.LdlS).U8(2)
                .Op(OpCode.ReturnValue);

            var result = Run(builder);
            Assert.True(result.IsInt);
            Assert.Equal(expected, result.AsInt);
        }

        /// <summary>The update never touches the operand stack, so a value under it is undisturbed.</summary>
        [Fact]
        public void IncLocal_LeavesTheOperandStackAlone()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(9)
                .Op(OpCode.IncLocal).U8(0).U8(5)
                .Op(OpCode.ReturnValue);

            Assert.Equal(9, Run(builder).AsInt);
        }

        [Fact]
        public void FreshLocals_StartZeroed()
        {
            // No store into local 3 at all - a fresh frame's locals must read back as a zeroed,
            // untagged slot (int 0 under Ldl3's untagged interpretation), not garbage.
            var builder = new BytecodeBuilder()
                .Op(OpCode.Ldl3)
                .Op(OpCode.ReturnValue);

            Assert.Equal(0, Run(builder, localCount: 6).AsInt);
        }

        [Fact]
        public void Locals_AreIndependentSlots()
        {
            var builder = new BytecodeBuilder()
                .Op(OpCode.PushI32).I32(1)
                .Op(OpCode.Stl0)
                .Op(OpCode.PushI32).I32(2)
                .Op(OpCode.Stl1)
                .Op(OpCode.Ldl0)
                .Op(OpCode.Ldl1)
                .Op(OpCode.Sub)
                .Op(OpCode.ReturnValue);

            Assert.Equal(-1, Run(builder, localCount: 2).AsInt); // 1 - 2
        }

        #endregion

        #region Return

        [Fact]
        public void ReturnVoid_AtTheTopLevel_YieldsNull()
        {
            var builder = new BytecodeBuilder().Op(OpCode.ReturnVoid);
            var result = Run(builder);

            Assert.True(result.IsNullReference);
        }

        [Fact]
        public void ReturnVoid_FromACalledMethod_LeavesATaggedNullForTheCaller()
        {
            using var runtime = new SurtrRuntime();

            var calleeModule = new Surtr.Runtime.Classes.SurtrModule("callee");
            var callee = new BytecodeBuilder().Op(OpCode.ReturnVoid).Build(calleeModule, localCount: 0, maxStackSize: 4);

            var callerModule = new Surtr.Runtime.Classes.SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(callee);
            builder.Op(OpCode.InvokeStatic).I16(methodIndex).U8(0).U8(1).Op(OpCode.ReturnValue);

            var caller = builder.Build(callerModule, localCount: 0, maxStackSize: 8);
            Assert.True(runtime.Invoke(caller).IsNullReference);
        }

        #endregion
    }
}
