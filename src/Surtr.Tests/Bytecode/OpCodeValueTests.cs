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
            (OpCode.Nop, 0x00), (OpCode.Dup, 0x01), (OpCode.Pop, 0x02), (OpCode.PushNull, 0x03),
            (OpCode.PushTrue, 0x04), (OpCode.PushFalse, 0x05), (OpCode.PushI8, 0x06), (OpCode.PushI16, 0x07),
            (OpCode.PushI32, 0x08), (OpCode.PushChar, 0x09), (OpCode.PushAbsent, 0x0A), (OpCode.Ldc0, 0x0B),
            (OpCode.Ldc1, 0x0C), (OpCode.Ldc2, 0x0D), (OpCode.Ldc3, 0x0E), (OpCode.Ldc4, 0x0F),
            (OpCode.Ldc5, 0x10), (OpCode.Ldc6, 0x11), (OpCode.Ldc7, 0x12), (OpCode.Ldc8, 0x13),
            (OpCode.Ldc9, 0x14), (OpCode.LdcS, 0x15), (OpCode.Ldc, 0x16), (OpCode.Ldl0, 0x18), (OpCode.Ldl1, 0x19), (OpCode.Ldl2, 0x1A), (OpCode.Ldl3, 0x1B),
            (OpCode.Ldl4, 0x1C), (OpCode.Ldl5, 0x1D), (OpCode.LdlS, 0x1E), (OpCode.Ldl, 0x1F),
            (OpCode.Stl0, 0x20), (OpCode.Stl1, 0x21), (OpCode.Stl2, 0x22), (OpCode.Stl3, 0x23),
            (OpCode.Stl4, 0x24), (OpCode.Stl5, 0x25), (OpCode.StlS, 0x26), (OpCode.Stl, 0x27),
            (OpCode.IncLocal, 0x28), (OpCode.FieldGet, 0x29), (OpCode.FieldSet, 0x2A), (OpCode.StaticFieldGet, 0x2B),
            (OpCode.StaticFieldSet, 0x2D), (OpCode.UpValueGet, 0x2F),
            (OpCode.LoadValueLocal, 0x30), (OpCode.StoreValueLocal, 0x31), (OpCode.LoadLocalField, 0x32), (OpCode.StoreLocalField, 0x33),
            (OpCode.LoadValueField, 0x34), (OpCode.StoreValueField, 0x35), (OpCode.LoadValueStatic, 0x36), (OpCode.StoreValueStatic, 0x37),
            (OpCode.BoxValue, 0x38), (OpCode.UnboxValue, 0x39), (OpCode.RangePack, 0x3A), (OpCode.RangeUnpack, 0x3B),
            (OpCode.Add, 0x3C), (OpCode.FAdd, 0x3D), (OpCode.Sub, 0x3E), (OpCode.FSub, 0x3F),
            (OpCode.Mul, 0x40), (OpCode.FMul, 0x41), (OpCode.Div, 0x42), (OpCode.FDiv, 0x43),
            (OpCode.Mod, 0x44), (OpCode.FMod, 0x45), (OpCode.Neg, 0x46), (OpCode.FNeg, 0x47),
            (OpCode.And, 0x48), (OpCode.Or, 0x49), (OpCode.Xor, 0x4A), (OpCode.Not, 0x4B),
            (OpCode.Shl, 0x4C), (OpCode.Shr, 0x4D), (OpCode.Sar, 0x4E), (OpCode.Inv, 0x4F),
            (OpCode.EQ, 0x50), (OpCode.NE, 0x51), (OpCode.GT, 0x52), (OpCode.GE, 0x53),
            (OpCode.LT, 0x54), (OpCode.LE, 0x55), (OpCode.FEQ, 0x56), (OpCode.FNE, 0x57),
            (OpCode.FGT, 0x58), (OpCode.FGE, 0x59), (OpCode.FLT, 0x5A), (OpCode.FLE, 0x5B),
            (OpCode.REQ, 0x5C), (OpCode.RNE, 0x5D), (OpCode.StrEQ, 0x5E), (OpCode.StrNE, 0x5F),
            (OpCode.DynEQ, 0x60), (OpCode.DynNE, 0x61), (OpCode.IsNull, 0x62), (OpCode.IsNotNull, 0x63),
            (OpCode.IsAbsent, 0x64), (OpCode.IsPresent, 0x65), (OpCode.I2F, 0x66), (OpCode.F2I, 0x67),
            (OpCode.I2C, 0x68), (OpCode.C2I, 0x69), (OpCode.I2B, 0x6A), (OpCode.B2I, 0x6B),
            (OpCode.BoxInt, 0x6C), (OpCode.BoxFloat, 0x6D), (OpCode.BoxBool, 0x6E), (OpCode.BoxChar, 0x6F),
            (OpCode.BoxAs, 0x70), (OpCode.Unbox, 0x72), (OpCode.BoxDynamic, 0x73),
            (OpCode.UnboxDynamic, 0x74), (OpCode.RangeNew, 0x75), (OpCode.RangeNewInclusive, 0x76), (OpCode.StrLen, 0x77),
            (OpCode.StrGet, 0x78), (OpCode.StrCat, 0x79), (OpCode.StrHash, 0x7A), (OpCode.ArrNew, 0x7B),
            (OpCode.ArrNewX, 0x7C), (OpCode.ArrPack, 0x7D), (OpCode.ArrLen, 0x7E), (OpCode.ArrGet, 0x7F),
            (OpCode.ArrSet, 0x80), (OpCode.ArrPush, 0x81), (OpCode.ArrPop, 0x82), (OpCode.ArrInsert, 0x83),
            (OpCode.ArrRemoveAt, 0x84), (OpCode.ArrClear, 0x85), (OpCode.ArrIndexOf, 0x86), (OpCode.ArrIn, 0x87),
            (OpCode.TupPack, 0x88), (OpCode.TupUnpack, 0x89), (OpCode.TupLen, 0x8A), (OpCode.TupGet, 0x8B),
            (OpCode.TupGetC, 0x8C), (OpCode.DictNew, 0x8D), (OpCode.DictPack, 0x8E), (OpCode.DictLen, 0x8F),
            (OpCode.DictGet, 0x90), (OpCode.DictSet, 0x91), (OpCode.DictDel, 0x92), (OpCode.DictClear, 0x93),
            (OpCode.DictKeys, 0x94), (OpCode.DictValues, 0x95), (OpCode.DictIn, 0x96), (OpCode.InstanceOf, 0x97),
            (OpCode.Cast, 0x99), (OpCode.CastOrNull, 0x9B),
            (OpCode.LoadType, 0x9D), (OpCode.GetTypeOfValue, 0x9F),
            (OpCode.LoadModule, 0xA0), (OpCode.LoadCurrentModule, 0xA2), (OpCode.ObjNew, 0xA3),
            (OpCode.JP, 0xA5), (OpCode.JPZ, 0xA7),
            (OpCode.JPNZ, 0xA9), (OpCode.JPN, 0xAB),
            (OpCode.JPNN, 0xAD), (OpCode.JPA, 0xAF),
            (OpCode.JPNA, 0xB1), (OpCode.JPEQ, 0xB3),
            (OpCode.JPNE, 0xB5), (OpCode.JPGT, 0xB7),
            (OpCode.JPGE, 0xB9), (OpCode.JPLT, 0xBB),
            (OpCode.JPLE, 0xBD), (OpCode.JPFEQ, 0xBF),
            (OpCode.JPFNE, 0xC1), (OpCode.JPFGT, 0xC3),
            (OpCode.JPFGE, 0xC5), (OpCode.JPFLT, 0xC7),
            (OpCode.JPFLE, 0xC9), (OpCode.JPREQ, 0xCB),
            (OpCode.JPRNE, 0xCD), (OpCode.JPStrEQ, 0xCF),
            (OpCode.JPStrNE, 0xD1), (OpCode.JPInstanceOf, 0xD3),
            (OpCode.Switch, 0xD5), (OpCode.SwitchLookup, 0xD6), (OpCode.CallLocalModule, 0xD7),
            (OpCode.CallModule, 0xD9), (OpCode.InvokeVirtual, 0xDB),
            (OpCode.InvokeSpecial, 0xDC), (OpCode.InvokeStatic, 0xDD), (OpCode.InvokeInterface, 0xDF),
            (OpCode.InvokeClosure, 0xE0), (OpCode.NewClosure, 0xE1), (OpCode.NewFunction, 0xE3),
            (OpCode.ReturnVoid, 0xE5), (OpCode.ReturnValue, 0xE6), (OpCode.ReturnValues, 0xE7),
            (OpCode.Throw, 0xE8), (OpCode.GenNew, 0xE9), (OpCode.GenIterate, 0xEA), (OpCode.GenResume, 0xEB),
            (OpCode.GenCurrent, 0xEC), (OpCode.Yield, 0xED), (OpCode.GenDelegate, 0xEE), (OpCode.GenResumed, 0xEF),
            (OpCode.Wide, 0xF0), (OpCode.Ext, 0xFF)
        };

        /// <summary>Every extended opcode and the value it is defined to have.</summary>
        /// <remarks>
        /// The same contract as <see cref="Assigned"/>, over the second space the
        /// <see cref="OpCode.Ext"/> prefix opens. Values here are equally on-disk and equally
        /// final; <c>0xFF</c> stays reserved as a second prefix.
        /// </remarks>
        private static readonly (SurtrExtOpCode Op, byte Value)[] AssignedExt = new (SurtrExtOpCode, byte)[]
        {
            (SurtrExtOpCode.Probe, 0x00),
            (SurtrExtOpCode.ArrForNext, 0x01), (SurtrExtOpCode.ArrForNextX, 0x02),
            (SurtrExtOpCode.StrForNext, 0x03), (SurtrExtOpCode.StrForNextX, 0x04),
            (SurtrExtOpCode.TupForNext, 0x05), (SurtrExtOpCode.TupForNextX, 0x06),
            (SurtrExtOpCode.DictForNext, 0x07), (SurtrExtOpCode.DictForNextX, 0x08),
            (SurtrExtOpCode.ForRangeNextLE, 0x09), (SurtrExtOpCode.ForRangeNextLEX, 0x0A),
            (SurtrExtOpCode.ForRangeNextLT, 0x0B), (SurtrExtOpCode.ForRangeNextLTX, 0x0C)
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
        /// <remarks>
        /// All thirty-nine are the <c>*X</c> twins the <see cref="OpCode.Wide"/> prefix replaced:
        /// a widened instruction is now the prefix plus the ordinary opcode, so each of these
        /// values went back to the pool rather than staying a name of its own. Their old encodings
        /// cannot be re-issued, which is what the image format version guards.
        /// </remarks>
        private static readonly byte[] RetiredValues =
        {
            0x17, 0x2C, 0x2E, 0x71, 0x98, 0x9A, 0x9C, 0x9E,
            0xA1, 0xA4, 0xA6, 0xA8, 0xAA, 0xAC, 0xAE, 0xB0,
            0xB2, 0xB4, 0xB6, 0xB8, 0xBA, 0xBC, 0xBE, 0xC0,
            0xC2, 0xC4, 0xC6, 0xC8, 0xCA, 0xCC, 0xCE, 0xD0,
            0xD2, 0xD4, 0xD8, 0xDA, 0xDE, 0xE2, 0xE4
        };

        /// <summary>
        /// The values run from zero with no gap except at a retired opcode's old slot, which is
        /// what lets the interpreter's switch stay close to a jump table while still keeping a
        /// retired value permanently unassigned rather than reused by whatever is filed next.
        /// </summary>
        [Fact]
        public void TheAssignedValuesAreContiguousExceptAtRetiredSlots()
        {
            // Ext is deliberately not part of the contiguous run: it is the last value in the
            // byte space, so the primary set can keep growing upward into 0xF0..0xFE without ever
            // colliding with the prefix.
            var ordered = new List<(OpCode Op, byte Value)>();
            foreach (var entry in Assigned)
            {
                if (entry.Op != OpCode.Ext)
                    ordered.Add(entry);
            }

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
                // The prefix has no line of its own - a prefixed instruction is printed under the
                // sub-opcode's name - so it is covered by the extended test below instead.
                if (op == OpCode.Ext)
                    continue;

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

        #region Extended space

        [Fact]
        public void EveryExtendedOpCodeHasTheValueTheFormatSaysItHas()
        {
            foreach (var (op, value) in AssignedExt)
                Assert.Equal(value, (byte)op);
        }

        /// <summary>Nothing is defined in the extended space that the table above does not name.</summary>
        [Fact]
        public void TheExtendedTableCoversTheWholeSpace()
        {
            var declared = (SurtrExtOpCode[])Enum.GetValues(typeof(SurtrExtOpCode));

            Assert.Equal(declared.Length, AssignedExt.Length);

            var named = new HashSet<SurtrExtOpCode>();
            foreach (var (op, _) in AssignedExt)
                Assert.True(named.Add(op), $"{op} appears twice in the extended table.");

            foreach (var op in declared)
                Assert.Contains(op, named);
        }

        /// <summary>
        /// 0xFF stays out of the extended space, reserved as a second prefix. Spending it on an
        /// ordinary instruction would close the only door out if 256 values ever ran short.
        /// </summary>
        [Fact]
        public void TheExtendedSpaceKeepsItsOwnPrefixReserved()
        {
            foreach (var (op, _) in AssignedExt)
                Assert.NotEqual(0xFF, (byte)op);
        }

        /// <summary>
        /// Every extended opcode decodes, for the same reason the primary ones must: the
        /// disassembler carries its own copy of the byte layout, and one it does not know takes
        /// every instruction after it down with it.
        /// </summary>
        [Fact]
        public void TheDisassemblerKnowsEveryExtendedOpCode()
        {
            foreach (SurtrExtOpCode op in (SurtrExtOpCode[])Enum.GetValues(typeof(SurtrExtOpCode)))
            {
                var module = new SurtrModule("test");
                var builder = new BytecodeBuilder();
                builder.Op(OpCode.Ext);
                builder.U8((byte)op);

                for (int i = 0; i < 16; i++)
                    builder.U8(0);

                string text = SurtrBytecodeDisassembler.Disassemble(builder.Build(module, localCount: 0, maxStackSize: 8));

                Assert.DoesNotContain("unknown extended opcode", text, StringComparison.Ordinal);
                Assert.Contains(op.ToString(), text, StringComparison.Ordinal);
            }
        }

        #endregion
    }
}

