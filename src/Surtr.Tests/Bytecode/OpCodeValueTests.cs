#nullable enable

using Surtr.Bytecode;
using Surtr.Bytecode.Emit;
using Surtr.Runtime.Classes;
using Surtr.Tests.VM;
using System;
using System.Collections.Generic;

namespace Surtr.Tests.Bytecode
{
    /// <summary>
    /// Pins the numeric value of every opcode, because that number is the on-disk encoding.
    /// </summary>
    /// <remarks>
    /// The set is laid out by family and every member's value is written out in
    /// <c>OpCode.cs</c>, so a member cannot be renumbered by accident - but it can still be
    /// renumbered on purpose, and this is what makes that a decision someone has to come here and
    /// record. A change to the table below invalidates every <c>.surtrc</c> already written.
    /// </remarks>
    public class OpCodeValueTests
    {
        /// <summary>Every opcode and the value it is defined to have, as of the current format.</summary>
        private static readonly (OpCode Op, byte Value)[] Assigned = new (OpCode, byte)[]
        {
            (OpCode.Nop, 0x00), (OpCode.Dup, 0x01), (OpCode.Dup2, 0x02), (OpCode.Swap, 0x03),
            (OpCode.Swap2, 0x04), (OpCode.Pop, 0x05), (OpCode.PushNull, 0x06), (OpCode.PushTrue, 0x07),
            (OpCode.PushFalse, 0x08), (OpCode.PushI8, 0x09), (OpCode.PushI16, 0x0A), (OpCode.PushI32, 0x0B),
            (OpCode.PushChar, 0x0C), (OpCode.PushAbsent, 0x0D), (OpCode.Ldc0, 0x0E), (OpCode.Ldc1, 0x0F),
            (OpCode.Ldc2, 0x10), (OpCode.Ldc3, 0x11), (OpCode.Ldc4, 0x12), (OpCode.Ldc5, 0x13),
            (OpCode.Ldc6, 0x14), (OpCode.Ldc7, 0x15), (OpCode.Ldc8, 0x16), (OpCode.Ldc9, 0x17),
            (OpCode.LdcS, 0x18), (OpCode.Ldc, 0x19), (OpCode.LdcX, 0x1A), (OpCode.Ldl0, 0x1B),
            (OpCode.Ldl1, 0x1C), (OpCode.Ldl2, 0x1D), (OpCode.Ldl3, 0x1E), (OpCode.Ldl4, 0x1F),
            (OpCode.Ldl5, 0x20), (OpCode.LdlS, 0x21), (OpCode.Ldl, 0x22), (OpCode.Stl0, 0x23),
            (OpCode.Stl1, 0x24), (OpCode.Stl2, 0x25), (OpCode.Stl3, 0x26), (OpCode.Stl4, 0x27),
            (OpCode.Stl5, 0x28), (OpCode.StlS, 0x29), (OpCode.Stl, 0x2A), (OpCode.IncLocal, 0x2B),
            // 0x2C-0x2F (Ldg/LdgX/Stg/StgX) are retired - see RetiredValues below.
            (OpCode.FieldGet, 0x30), (OpCode.FieldSet, 0x31), (OpCode.StaticFieldGet, 0x32), (OpCode.StaticFieldGetX, 0x33),
            (OpCode.StaticFieldSet, 0x34), (OpCode.StaticFieldSetX, 0x35), (OpCode.UpValueGet, 0x36), (OpCode.Add, 0x37),
            (OpCode.FAdd, 0x38), (OpCode.Sub, 0x39), (OpCode.FSub, 0x3A), (OpCode.Mul, 0x3B),
            (OpCode.FMul, 0x3C), (OpCode.Div, 0x3D), (OpCode.FDiv, 0x3E), (OpCode.Mod, 0x3F),
            (OpCode.FMod, 0x40), (OpCode.Pow, 0x41), (OpCode.FPow, 0x42), (OpCode.Neg, 0x43),
            (OpCode.FNeg, 0x44), (OpCode.And, 0x45), (OpCode.Or, 0x46), (OpCode.Xor, 0x47),
            (OpCode.Not, 0x48), (OpCode.Shl, 0x49), (OpCode.Shr, 0x4A), (OpCode.Sar, 0x4B),
            (OpCode.Inv, 0x4C), (OpCode.EQ, 0x4D), (OpCode.NE, 0x4E), (OpCode.GT, 0x4F),
            (OpCode.GE, 0x50), (OpCode.LT, 0x51), (OpCode.LE, 0x52), (OpCode.FEQ, 0x53),
            (OpCode.FNE, 0x54), (OpCode.FGT, 0x55), (OpCode.FGE, 0x56), (OpCode.FLT, 0x57),
            (OpCode.FLE, 0x58), (OpCode.REQ, 0x59), (OpCode.RNE, 0x5A), (OpCode.StrEQ, 0x5B),
            (OpCode.StrNE, 0x5C), (OpCode.IsNull, 0x5D), (OpCode.IsNotNull, 0x5E), (OpCode.IsAbsent, 0x5F),
            (OpCode.IsPresent, 0x60), (OpCode.I2F, 0x61), (OpCode.F2I, 0x62), (OpCode.I2C, 0x63),
            (OpCode.C2I, 0x64), (OpCode.I2B, 0x65), (OpCode.B2I, 0x66), (OpCode.BoxInt, 0x67),
            (OpCode.BoxFloat, 0x68), (OpCode.BoxBool, 0x69), (OpCode.BoxChar, 0x6A), (OpCode.BoxAs, 0x6B),
            (OpCode.BoxAsX, 0x6C), (OpCode.Unbox, 0x6D), (OpCode.InstanceOf, 0x6E), (OpCode.InstanceOfX, 0x6F),
            (OpCode.Cast, 0x70), (OpCode.CastX, 0x71), (OpCode.CastOrNull, 0x72), (OpCode.CastOrNullX, 0x73),
            (OpCode.JP, 0x74), (OpCode.JPX, 0x75), (OpCode.JPZ, 0x76), (OpCode.JPZX, 0x77),
            (OpCode.JPNZ, 0x78), (OpCode.JPNZX, 0x79), (OpCode.JPN, 0x7A), (OpCode.JPNX, 0x7B),
            (OpCode.JPNN, 0x7C), (OpCode.JPNNX, 0x7D), (OpCode.JPA, 0x7E), (OpCode.JPAX, 0x7F),
            (OpCode.JPNA, 0x80), (OpCode.JPNAX, 0x81), (OpCode.JPEQ, 0x82), (OpCode.JPEQX, 0x83),
            (OpCode.JPNE, 0x84), (OpCode.JPNEX, 0x85), (OpCode.JPGT, 0x86), (OpCode.JPGTX, 0x87),
            (OpCode.JPGE, 0x88), (OpCode.JPGEX, 0x89), (OpCode.JPLT, 0x8A), (OpCode.JPLTX, 0x8B),
            (OpCode.JPLE, 0x8C), (OpCode.JPLEX, 0x8D), (OpCode.JPFEQ, 0x8E), (OpCode.JPFEQX, 0x8F),
            (OpCode.JPFNE, 0x90), (OpCode.JPFNEX, 0x91), (OpCode.JPFGT, 0x92), (OpCode.JPFGTX, 0x93),
            (OpCode.JPFGE, 0x94), (OpCode.JPFGEX, 0x95), (OpCode.JPFLT, 0x96), (OpCode.JPFLTX, 0x97),
            (OpCode.JPFLE, 0x98), (OpCode.JPFLEX, 0x99), (OpCode.JPREQ, 0x9A), (OpCode.JPREQX, 0x9B),
            (OpCode.JPRNE, 0x9C), (OpCode.JPRNEX, 0x9D), (OpCode.JPStrEQ, 0x9E), (OpCode.JPStrEQX, 0x9F),
            (OpCode.JPStrNE, 0xA0), (OpCode.JPStrNEX, 0xA1), (OpCode.JPInstanceOf, 0xA2), (OpCode.JPInstanceOfX, 0xA3),
            (OpCode.Switch, 0xA4), (OpCode.SwitchLookup, 0xA5), (OpCode.CallLocalModule, 0xA6), (OpCode.CallLocalModuleX, 0xA7),
            (OpCode.CallModule, 0xA8), (OpCode.CallModuleX, 0xA9),
            // 0xAA-0xAB (CallGlobalNative/CallGlobalNativeX) are retired - see RetiredValues below.
            (OpCode.InvokeVirtual, 0xAC), (OpCode.InvokeSpecial, 0xAD), (OpCode.InvokeStatic, 0xAE), (OpCode.InvokeStaticX, 0xAF),
            (OpCode.InvokeInterface, 0xB0), (OpCode.InvokeClosure, 0xB1), (OpCode.NewClosure, 0xB2), (OpCode.NewClosureX, 0xB3),
            (OpCode.ReturnVoid, 0xB4), (OpCode.ReturnValue, 0xB5), (OpCode.Throw, 0xB6), (OpCode.ObjNew, 0xB7),
            (OpCode.ObjNewX, 0xB8), (OpCode.StrLen, 0xB9), (OpCode.StrGet, 0xBA), (OpCode.StrCat, 0xBB),
            (OpCode.StrHash, 0xBC), (OpCode.ArrNew, 0xBD), (OpCode.ArrNewX, 0xBE), (OpCode.ArrPack, 0xBF),
            (OpCode.ArrLen, 0xC0), (OpCode.ArrGet, 0xC1), (OpCode.ArrSet, 0xC2), (OpCode.ArrPush, 0xC3),
            (OpCode.ArrPop, 0xC4), (OpCode.ArrInsert, 0xC5), (OpCode.ArrRemoveAt, 0xC6), (OpCode.ArrClear, 0xC7),
            (OpCode.ArrIndexOf, 0xC8), (OpCode.ArrIn, 0xC9), (OpCode.ArrNIn, 0xCA), (OpCode.TupPack, 0xCB),
            (OpCode.TupUnpack, 0xCC), (OpCode.TupLen, 0xCD), (OpCode.TupGet, 0xCE), (OpCode.TupGetC, 0xCF),
            (OpCode.DictNew, 0xD0), (OpCode.DictPack, 0xD1), (OpCode.DictLen, 0xD2), (OpCode.DictGet, 0xD3),
            (OpCode.DictSet, 0xD4), (OpCode.DictDel, 0xD5), (OpCode.DictClear, 0xD6), (OpCode.DictKeys, 0xD7),
            (OpCode.DictValues, 0xD8), (OpCode.DictIn, 0xD9), (OpCode.DictNIn, 0xDA), (OpCode.RangeNew, 0xDB),
            (OpCode.RangeNewInclusive, 0xDC), (OpCode.LoadType, 0xDD), (OpCode.LoadTypeX, 0xDE),
            (OpCode.GetTypeOfValue, 0xDF), (OpCode.LoadModule, 0xE0), (OpCode.LoadModuleX, 0xE1),
            (OpCode.LoadCurrentModule, 0xE2), (OpCode.BoxDynamic, 0xE3), (OpCode.DynEQ, 0xE4),
            (OpCode.DynNE, 0xE5), (OpCode.UnboxDynamic, 0xE6), (OpCode.NewFunction, 0xE7),
            (OpCode.NewFunctionX, 0xE8), (OpCode.ReturnValues, 0xE9), (OpCode.LoadValueLocal, 0xEA),
            (OpCode.StoreValueLocal, 0xEB), (OpCode.LoadLocalField, 0xEC), (OpCode.StoreLocalField, 0xED),
            (OpCode.BoxValue, 0xEE), (OpCode.UnboxValue, 0xEF),
            (OpCode.LoadValueField, 0xF0), (OpCode.StoreValueField, 0xF1),
            (OpCode.LoadValueStatic, 0xF2), (OpCode.StoreValueStatic, 0xF3),
            (OpCode.RangePack, 0xF4), (OpCode.RangeUnpack, 0xF5),
            (OpCode.GenNew, 0xF6), (OpCode.GenIterate, 0xF7), (OpCode.GenResume, 0xF8),
            (OpCode.GenCurrent, 0xF9), (OpCode.Yield, 0xFA)
        };

        [Fact]
        public void EveryOpCodeHasTheValueTheFormatSaysItHas()
        {
            foreach (var (op, value) in Assigned)
                Assert.Equal(value, (byte)op);
        }

        /// <summary>Nothing is defined that the table above does not name, and nothing twice.</summary>
        [Fact]
        public void TheTableCoversTheWholeSet()
        {
            var declared = (OpCode[])Enum.GetValues(typeof(OpCode));

            Assert.Equal(declared.Length, Assigned.Length);
            Assert.Equal(declared.Length, new HashSet<OpCode>(declared).Count);

            var named = new HashSet<OpCode>();
            foreach (var (op, _) in Assigned)
                Assert.True(named.Add(op), $"{op} appears twice in the table.");

            foreach (var op in declared)
                Assert.Contains(op, named);
        }

        /// <summary>
        /// A retired opcode's old byte value, never reused - reusing one would make an old module
        /// silently execute a different instruction. See the note at the top of <c>OpCode.cs</c>.
        /// </summary>
        private static readonly byte[] RetiredValues = { 0x2C, 0x2D, 0x2E, 0x2F, 0xAA, 0xAB };

        /// <summary>
        /// The values run from zero with no gap except at a retired opcode's old slot, which is
        /// what lets the interpreter's switch stay close to a jump table while still keeping a
        /// retired value permanently unassigned rather than reused by whatever is filed next.
        /// </summary>
        [Fact]
        public void TheAssignedValuesAreContiguousExceptAtRetiredSlots()
        {
            var ordered = new List<(OpCode Op, byte Value)>(Assigned);
            ordered.Sort((a, b) => a.Value.CompareTo(b.Value));

            int expected = 0;
            foreach (var (_, value) in ordered)
            {
                while (Array.IndexOf(RetiredValues, (byte)expected) >= 0)
                    expected++;

                Assert.Equal(expected, value);
                expected++;
            }
        }

        /// <summary>Nothing was renumbered into a retired opcode's old slot.</summary>
        [Fact]
        public void RetiredValuesAreNotAssignedToAnything()
        {
            var assignedValues = new HashSet<byte>();
            foreach (var (_, value) in Assigned)
                assignedValues.Add(value);

            foreach (byte retired in RetiredValues)
                Assert.DoesNotContain(retired, assignedValues);
        }

        /// <summary>
        /// Every opcode decodes. The disassembler carries its own copy of the byte layout, so an
        /// opcode nothing added to it renders as garbage and takes every instruction after it with
        /// it - which is how <c>PushAbsent</c> and the four absence branches went unnoticed.
        /// </summary>
        [Fact]
        public void TheDisassemblerKnowsEveryOpCode()
        {
            foreach (OpCode op in (OpCode[])Enum.GetValues(typeof(OpCode)))
            {
                var module = new SurtrModule("test");
                var builder = new BytecodeBuilder();
                builder.Op(op);

                // Enough zeroed padding for the widest immediate any opcode carries, so decoding
                // reads real bytes rather than running off the end.
                for (int i = 0; i < 16; i++)
                    builder.U8(0);

                string text = SurtrBytecodeDisassembler.Disassemble(builder.Build(module, localCount: 0, maxStackSize: 8));

                Assert.DoesNotContain("unknown opcode", text, StringComparison.Ordinal);
                Assert.Contains(op.ToString(), text, StringComparison.Ordinal);
            }
        }
    }
}
