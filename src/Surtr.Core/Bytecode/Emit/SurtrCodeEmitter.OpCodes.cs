#nullable enable

using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;

namespace Surtr.Bytecode.Emit
{
    // Tier two: one method per opcode, named after it and taking its exact operands.
    //
    // These are deliberately literal. A call to JPZ emits JPZ and fails if its target turns out to
    // be too far away; it never quietly becomes JPZX. Choosing between encodings is the grouped
    // helpers' job, in SurtrCodeEmitter.Helpers.cs, so that a caller who wants one specific
    // instruction can always get exactly that one.
    //
    // Semantics live on the OpCode members themselves and are not restated here - see
    // src/Surtr.Core/Bytecode/OpCode.cs. What each method does add is the stack effect it reports
    // to the tracker, which is the one piece of information the enum does not carry in a form code
    // can read.
    public sealed partial class SurtrCodeEmitter
    {
        #region Immediate helpers

        private SurtrCodeEmitter WithI8(OpCode op, int value, int pop, int push, string operand)
        {
            ThrowIfFinished();
            CheckRange(value, sbyte.MinValue, sbyte.MaxValue, op, operand);
            Track(pop, push);
            _code.Add((byte)op);
            _code.Add((byte)value);
            return this;
        }

        private SurtrCodeEmitter WithI16(OpCode op, int value, int pop, int push, string operand)
        {
            ThrowIfFinished();
            CheckRange(value, short.MinValue, short.MaxValue, op, operand);
            Track(pop, push);
            _code.Add((byte)op);
            _code.Add((byte)value);
            _code.Add((byte)(value >> 8));
            return this;
        }

        /// <summary>An opcode carrying a 2-byte type index followed by a second immediate.</summary>
        private SurtrCodeEmitter WithTypeAnd(OpCode op, SurtrTypeToken type, int value, int valueWidth, int valueMax, int pop, int push, string operand)
        {
            ThrowIfFinished();

            int typeIndex = TypeIndex(type);
            CheckRange(typeIndex, 0, ushort.MaxValue, op, "typeIdx");
            CheckRange(value, 0, valueMax, op, operand);

            Track(pop, push);

            _code.Add((byte)op);
            _code.Add((byte)typeIndex);
            _code.Add((byte)(typeIndex >> 8));

            for (int i = 0; i < valueWidth; i++)
                _code.Add((byte)(value >> (8 * i)));

            return this;
        }

        /// <summary>An opcode carrying a method index of <paramref name="indexWidth"/> bytes, then an argument and a result count.</summary>
        private SurtrCodeEmitter WithCall(OpCode op, int methodIndex, int indexWidth, int argumentCount, int resultCount)
        {
            ThrowIfFinished();

            CheckRange(methodIndex, 0, indexWidth == 2 ? ushort.MaxValue : int.MaxValue, op, "methodIdx");
            CheckArgumentCounts(op, argumentCount, resultCount);

            Track(argumentCount, resultCount);

            _code.Add((byte)op);
            for (int i = 0; i < indexWidth; i++)
                _code.Add((byte)(methodIndex >> (8 * i)));

            _code.Add((byte)argumentCount);
            _code.Add((byte)resultCount);
            return this;
        }

        private static void CheckArgumentCounts(OpCode op, int argumentCount, int resultCount)
        {
            CheckRange(argumentCount, 0, byte.MaxValue, op, "argsCount");

            // The frame protocol records retCount on the callee's frame and writes at most one
            // value back, so anything above one has no meaning: several results are returned by
            // packing a tuple.
            CheckRange(resultCount, 0, 1, op, "retCount");
        }

        private int MethodIndex(SurtrMethodToken token)
        {
            if (!token.IsValid)
                throw new ArgumentException("Method token was not obtained from a module builder.", nameof(token));
            return token.Index;
        }

        private int FieldIndex(SurtrFieldToken token)
        {
            if (!token.IsValid)
                throw new ArgumentException("Field token was not obtained from a module builder.", nameof(token));
            return token.Index;
        }

        private int ConstantIndex(SurtrConstantToken token)
        {
            if (!token.IsValid)
                throw new ArgumentException("Constant token was not obtained from a module builder.", nameof(token));
            return token.Index;
        }

        #endregion

        /// <summary>Emits <see cref="OpCode.Nop"/>.</summary>
        public SurtrCodeEmitter Nop() => Simple(OpCode.Nop, 0, 0);

        #region Stack Operations

        /// <summary>Emits <see cref="OpCode.Dup"/>.</summary>
        public SurtrCodeEmitter Dup() => Simple(OpCode.Dup, 1, 2);

        /// <summary>Emits <see cref="OpCode.Dup2"/>.</summary>
        public SurtrCodeEmitter Dup2() => Simple(OpCode.Dup2, 2, 4);

        /// <summary>Emits <see cref="OpCode.Swap"/>.</summary>
        public SurtrCodeEmitter Swap() => Simple(OpCode.Swap, 2, 2);

        /// <summary>Emits <see cref="OpCode.Swap2"/>.</summary>
        public SurtrCodeEmitter Swap2() => Simple(OpCode.Swap2, 4, 4);

        /// <summary>Emits <see cref="OpCode.PushNull"/>.</summary>
        public SurtrCodeEmitter PushNull() => Simple(OpCode.PushNull, 0, 1);

        /// <summary>Emits <see cref="OpCode.PushTrue"/>.</summary>
        public SurtrCodeEmitter PushTrue() => Simple(OpCode.PushTrue, 0, 1);

        /// <summary>Emits <see cref="OpCode.PushFalse"/>.</summary>
        public SurtrCodeEmitter PushFalse() => Simple(OpCode.PushFalse, 0, 1);

        /// <summary>Emits <see cref="OpCode.PushChar"/>.</summary>
        /// <param name="value">The literal, carried inline as a UTF-16 code unit.</param>
        public SurtrCodeEmitter PushChar(char value) => WithU16(OpCode.PushChar, value, 0, 1, "value");

        /// <summary>Emits <see cref="OpCode.PushAbsent"/>.</summary>
        /// <param name="typeCode">Which primitive family the missing value belongs to.</param>
        /// <exception cref="ArgumentException"><paramref name="typeCode"/> is not a primitive.</exception>
        public SurtrCodeEmitter PushAbsent(SurtrValueTypeCode typeCode)
        {
            // Only a primitive has a nullable form that needs a tag: a nullable reference is just
            // a reference, and null is already representable there.
            if (!typeCode.IsPrimitive)
                throw new ArgumentException(
                    $"PushAbsent needs a primitive type code; {typeCode} is not one.",
                    nameof(typeCode));

            return WithU8(OpCode.PushAbsent, typeCode.ToByte(), 0, 1, nameof(typeCode));
        }

        /// <summary>Emits <see cref="OpCode.PushI8"/>.</summary>
        /// <param name="value">The literal, sign-extended to a full integer at run time.</param>
        public SurtrCodeEmitter PushI8(int value) => WithI8(OpCode.PushI8, value, 0, 1, "value");

        /// <summary>Emits <see cref="OpCode.PushI16"/>.</summary>
        /// <param name="value">The literal, sign-extended to a full integer at run time.</param>
        public SurtrCodeEmitter PushI16(int value) => WithI16(OpCode.PushI16, value, 0, 1, "value");

        /// <summary>Emits <see cref="OpCode.PushI32"/>.</summary>
        /// <param name="value">The literal.</param>
        public SurtrCodeEmitter PushI32(int value) => WithI32(OpCode.PushI32, value, 0, 1);

        /// <summary>Emits <see cref="OpCode.Pop"/>.</summary>
        public SurtrCodeEmitter Pop() => Simple(OpCode.Pop, 1, 0);

        #endregion

        #region Load / Store Operations

        /// <summary>Emits <see cref="OpCode.Ldc"/>.</summary>
        public SurtrCodeEmitter Ldc(SurtrConstantToken constant)
            => WithU16(OpCode.Ldc, ConstantIndex(constant), 0, 1, "constIdx");

        /// <summary>Emits <see cref="OpCode.Ldc0"/>.</summary>
        public SurtrCodeEmitter Ldc0() => Simple(OpCode.Ldc0, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldc1"/>.</summary>
        public SurtrCodeEmitter Ldc1() => Simple(OpCode.Ldc1, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldc2"/>.</summary>
        public SurtrCodeEmitter Ldc2() => Simple(OpCode.Ldc2, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldc3"/>.</summary>
        public SurtrCodeEmitter Ldc3() => Simple(OpCode.Ldc3, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldc4"/>.</summary>
        public SurtrCodeEmitter Ldc4() => Simple(OpCode.Ldc4, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldc5"/>.</summary>
        public SurtrCodeEmitter Ldc5() => Simple(OpCode.Ldc5, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldc6"/>.</summary>
        public SurtrCodeEmitter Ldc6() => Simple(OpCode.Ldc6, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldc7"/>.</summary>
        public SurtrCodeEmitter Ldc7() => Simple(OpCode.Ldc7, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldc8"/>.</summary>
        public SurtrCodeEmitter Ldc8() => Simple(OpCode.Ldc8, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldc9"/>.</summary>
        public SurtrCodeEmitter Ldc9() => Simple(OpCode.Ldc9, 0, 1);

        /// <summary>Emits <see cref="OpCode.LdcX"/>.</summary>
        public SurtrCodeEmitter LdcX(SurtrConstantToken constant)
            => WithI32(OpCode.LdcX, ConstantIndex(constant), 0, 1);

        /// <summary>Emits <see cref="OpCode.LdcS"/>.</summary>
        public SurtrCodeEmitter LdcS(SurtrConstantToken constant)
            => WithU8(OpCode.LdcS, ConstantIndex(constant), 0, 1, "constIdx");

        /// <summary>Emits <see cref="OpCode.Ldl"/>.</summary>
        public SurtrCodeEmitter Ldl(int localIndex) => WithU16(OpCode.Ldl, localIndex, 0, 1, "localIdx");

        /// <summary>Emits <see cref="OpCode.Ldl0"/>.</summary>
        public SurtrCodeEmitter Ldl0() => Simple(OpCode.Ldl0, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldl1"/>.</summary>
        public SurtrCodeEmitter Ldl1() => Simple(OpCode.Ldl1, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldl2"/>.</summary>
        public SurtrCodeEmitter Ldl2() => Simple(OpCode.Ldl2, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldl3"/>.</summary>
        public SurtrCodeEmitter Ldl3() => Simple(OpCode.Ldl3, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldl4"/>.</summary>
        public SurtrCodeEmitter Ldl4() => Simple(OpCode.Ldl4, 0, 1);

        /// <summary>Emits <see cref="OpCode.Ldl5"/>.</summary>
        public SurtrCodeEmitter Ldl5() => Simple(OpCode.Ldl5, 0, 1);

        /// <summary>Emits <see cref="OpCode.LdlS"/>.</summary>
        public SurtrCodeEmitter LdlS(int localIndex) => WithU8(OpCode.LdlS, localIndex, 0, 1, "localIdx");

        /// <summary>Emits <see cref="OpCode.Stl"/>.</summary>
        public SurtrCodeEmitter Stl(int localIndex) => WithU16(OpCode.Stl, localIndex, 1, 0, "localIdx");

        /// <summary>Emits <see cref="OpCode.Stl0"/>.</summary>
        public SurtrCodeEmitter Stl0() => Simple(OpCode.Stl0, 1, 0);

        /// <summary>Emits <see cref="OpCode.Stl1"/>.</summary>
        public SurtrCodeEmitter Stl1() => Simple(OpCode.Stl1, 1, 0);

        /// <summary>Emits <see cref="OpCode.Stl2"/>.</summary>
        public SurtrCodeEmitter Stl2() => Simple(OpCode.Stl2, 1, 0);

        /// <summary>Emits <see cref="OpCode.Stl3"/>.</summary>
        public SurtrCodeEmitter Stl3() => Simple(OpCode.Stl3, 1, 0);

        /// <summary>Emits <see cref="OpCode.Stl4"/>.</summary>
        public SurtrCodeEmitter Stl4() => Simple(OpCode.Stl4, 1, 0);

        /// <summary>Emits <see cref="OpCode.Stl5"/>.</summary>
        public SurtrCodeEmitter Stl5() => Simple(OpCode.Stl5, 1, 0);

        /// <summary>Emits <see cref="OpCode.StlS"/>.</summary>
        public SurtrCodeEmitter StlS(int localIndex) => WithU8(OpCode.StlS, localIndex, 1, 0, "localIdx");

        /// <summary>Emits <see cref="OpCode.IncLocal"/>.</summary>
        /// <param name="localIndex">The slot to update, which must be within the first 256.</param>
        /// <param name="delta">How much to add, as a signed byte; a negative value decrements.</param>
        /// <remarks>
        /// Nothing here falls back to the long form when either operand is out of range - that is
        /// <see cref="IncrementLocal(int, int)"/>'s job, in the tier above. This is the literal
        /// instruction, and it touches the operand stack not at all.
        /// </remarks>
        public SurtrCodeEmitter IncLocal(int localIndex, int delta)
        {
            ThrowIfFinished();
            CheckRange(localIndex, 0, byte.MaxValue, OpCode.IncLocal, "localIdx");
            CheckRange(delta, sbyte.MinValue, sbyte.MaxValue, OpCode.IncLocal, "delta");

            _code.Add((byte)OpCode.IncLocal);
            _code.Add((byte)localIndex);
            _code.Add((byte)delta);
            return this;
        }

        #endregion

        #region Arithmetic Operations

        /// <summary>Emits <see cref="OpCode.Add"/>.</summary>
        public SurtrCodeEmitter Add() => Simple(OpCode.Add, 2, 1);

        /// <summary>Emits <see cref="OpCode.FAdd"/>.</summary>
        public SurtrCodeEmitter FAdd() => Simple(OpCode.FAdd, 2, 1);

        /// <summary>Emits <see cref="OpCode.Sub"/>.</summary>
        public SurtrCodeEmitter Sub() => Simple(OpCode.Sub, 2, 1);

        /// <summary>Emits <see cref="OpCode.FSub"/>.</summary>
        public SurtrCodeEmitter FSub() => Simple(OpCode.FSub, 2, 1);

        /// <summary>Emits <see cref="OpCode.Mul"/>.</summary>
        public SurtrCodeEmitter Mul() => Simple(OpCode.Mul, 2, 1);

        /// <summary>Emits <see cref="OpCode.FMul"/>.</summary>
        public SurtrCodeEmitter FMul() => Simple(OpCode.FMul, 2, 1);

        /// <summary>Emits <see cref="OpCode.Div"/>.</summary>
        public SurtrCodeEmitter Div() => Simple(OpCode.Div, 2, 1);

        /// <summary>Emits <see cref="OpCode.FDiv"/>.</summary>
        public SurtrCodeEmitter FDiv() => Simple(OpCode.FDiv, 2, 1);

        /// <summary>Emits <see cref="OpCode.Mod"/>.</summary>
        public SurtrCodeEmitter Mod() => Simple(OpCode.Mod, 2, 1);

        /// <summary>Emits <see cref="OpCode.FMod"/>.</summary>
        public SurtrCodeEmitter FMod() => Simple(OpCode.FMod, 2, 1);

        /// <summary>Emits <see cref="OpCode.Pow"/>.</summary>
        public SurtrCodeEmitter Pow() => Simple(OpCode.Pow, 2, 1);

        /// <summary>Emits <see cref="OpCode.FPow"/>.</summary>
        public SurtrCodeEmitter FPow() => Simple(OpCode.FPow, 2, 1);

        /// <summary>Emits <see cref="OpCode.Neg"/>.</summary>
        public SurtrCodeEmitter Neg() => Simple(OpCode.Neg, 1, 1);

        /// <summary>Emits <see cref="OpCode.FNeg"/>.</summary>
        public SurtrCodeEmitter FNeg() => Simple(OpCode.FNeg, 1, 1);

        /// <summary>Emits <see cref="OpCode.Inv"/>.</summary>
        public SurtrCodeEmitter Inv() => Simple(OpCode.Inv, 1, 1);

        #endregion

        #region Comparison Operations

        /// <summary>Emits <see cref="OpCode.EQ"/>.</summary>
        public SurtrCodeEmitter EQ() => Simple(OpCode.EQ, 2, 1);

        /// <summary>Emits <see cref="OpCode.FEQ"/>.</summary>
        public SurtrCodeEmitter FEQ() => Simple(OpCode.FEQ, 2, 1);

        /// <summary>Emits <see cref="OpCode.REQ"/>.</summary>
        public SurtrCodeEmitter REQ() => Simple(OpCode.REQ, 2, 1);

        /// <summary>Emits <see cref="OpCode.StrEQ"/>.</summary>
        public SurtrCodeEmitter StrEQ() => Simple(OpCode.StrEQ, 2, 1);

        /// <summary>Emits <see cref="OpCode.NE"/>.</summary>
        public SurtrCodeEmitter NE() => Simple(OpCode.NE, 2, 1);

        /// <summary>Emits <see cref="OpCode.FNE"/>.</summary>
        public SurtrCodeEmitter FNE() => Simple(OpCode.FNE, 2, 1);

        /// <summary>Emits <see cref="OpCode.RNE"/>.</summary>
        public SurtrCodeEmitter RNE() => Simple(OpCode.RNE, 2, 1);

        /// <summary>Emits <see cref="OpCode.StrNE"/>.</summary>
        public SurtrCodeEmitter StrNE() => Simple(OpCode.StrNE, 2, 1);

        /// <summary>Emits <see cref="OpCode.GT"/>.</summary>
        public SurtrCodeEmitter GT() => Simple(OpCode.GT, 2, 1);

        /// <summary>Emits <see cref="OpCode.FGT"/>.</summary>
        public SurtrCodeEmitter FGT() => Simple(OpCode.FGT, 2, 1);

        /// <summary>Emits <see cref="OpCode.GE"/>.</summary>
        public SurtrCodeEmitter GE() => Simple(OpCode.GE, 2, 1);

        /// <summary>Emits <see cref="OpCode.FGE"/>.</summary>
        public SurtrCodeEmitter FGE() => Simple(OpCode.FGE, 2, 1);

        /// <summary>Emits <see cref="OpCode.LT"/>.</summary>
        public SurtrCodeEmitter LT() => Simple(OpCode.LT, 2, 1);

        /// <summary>Emits <see cref="OpCode.FLT"/>.</summary>
        public SurtrCodeEmitter FLT() => Simple(OpCode.FLT, 2, 1);

        /// <summary>Emits <see cref="OpCode.LE"/>.</summary>
        public SurtrCodeEmitter LE() => Simple(OpCode.LE, 2, 1);

        /// <summary>Emits <see cref="OpCode.FLE"/>.</summary>
        public SurtrCodeEmitter FLE() => Simple(OpCode.FLE, 2, 1);

        /// <summary>Emits <see cref="OpCode.IsNull"/>.</summary>
        public SurtrCodeEmitter IsNull() => Simple(OpCode.IsNull, 1, 1);

        /// <summary>Emits <see cref="OpCode.IsNotNull"/>.</summary>
        public SurtrCodeEmitter IsNotNull() => Simple(OpCode.IsNotNull, 1, 1);

        /// <summary>Emits <see cref="OpCode.IsAbsent"/>.</summary>
        public SurtrCodeEmitter IsAbsent() => Simple(OpCode.IsAbsent, 1, 1);

        /// <summary>Emits <see cref="OpCode.IsPresent"/>.</summary>
        public SurtrCodeEmitter IsPresent() => Simple(OpCode.IsPresent, 1, 1);

        /// <summary>Emits <see cref="OpCode.InstanceOf"/>.</summary>
        public SurtrCodeEmitter InstanceOf(SurtrTypeToken type)
            => WithU16(OpCode.InstanceOf, TypeIndex(type), 1, 1, "typeIdx");

        /// <summary>Emits <see cref="OpCode.InstanceOfX"/>.</summary>
        public SurtrCodeEmitter InstanceOfX(SurtrTypeToken type)
            => WithI32(OpCode.InstanceOfX, TypeIndex(type), 1, 1);

        #endregion

        #region Bitwise Operations

        /// <summary>Emits <see cref="OpCode.And"/>.</summary>
        public SurtrCodeEmitter And() => Simple(OpCode.And, 2, 1);

        /// <summary>Emits <see cref="OpCode.Or"/>.</summary>
        public SurtrCodeEmitter Or() => Simple(OpCode.Or, 2, 1);

        /// <summary>Emits <see cref="OpCode.Xor"/>.</summary>
        public SurtrCodeEmitter Xor() => Simple(OpCode.Xor, 2, 1);

        /// <summary>Emits <see cref="OpCode.Not"/>.</summary>
        public SurtrCodeEmitter Not() => Simple(OpCode.Not, 1, 1);

        /// <summary>Emits <see cref="OpCode.Shl"/>.</summary>
        public SurtrCodeEmitter Shl() => Simple(OpCode.Shl, 2, 1);

        /// <summary>Emits <see cref="OpCode.Shr"/>.</summary>
        public SurtrCodeEmitter Shr() => Simple(OpCode.Shr, 2, 1);

        /// <summary>Emits <see cref="OpCode.Sar"/>.</summary>
        public SurtrCodeEmitter Sar() => Simple(OpCode.Sar, 2, 1);

        #endregion

        #region Conversion Operations

        /// <summary>Emits <see cref="OpCode.I2F"/>.</summary>
        public SurtrCodeEmitter I2F() => Simple(OpCode.I2F, 1, 1);

        /// <summary>Emits <see cref="OpCode.F2I"/>.</summary>
        public SurtrCodeEmitter F2I() => Simple(OpCode.F2I, 1, 1);

        /// <summary>Emits <see cref="OpCode.I2C"/>.</summary>
        public SurtrCodeEmitter I2C() => Simple(OpCode.I2C, 1, 1);

        /// <summary>Emits <see cref="OpCode.C2I"/>.</summary>
        public SurtrCodeEmitter C2I() => Simple(OpCode.C2I, 1, 1);

        /// <summary>Emits <see cref="OpCode.I2B"/>.</summary>
        public SurtrCodeEmitter I2B() => Simple(OpCode.I2B, 1, 1);

        /// <summary>Emits <see cref="OpCode.B2I"/>.</summary>
        public SurtrCodeEmitter B2I() => Simple(OpCode.B2I, 1, 1);

        /// <summary>Emits <see cref="OpCode.BoxInt"/>.</summary>
        public SurtrCodeEmitter BoxInt() => Simple(OpCode.BoxInt, 1, 1);

        /// <summary>Emits <see cref="OpCode.BoxAs"/>.</summary>
        /// <param name="type">The class the box should present as.</param>
        public SurtrCodeEmitter BoxAs(SurtrTypeToken type) => WithU16(OpCode.BoxAs, TypeIndex(type), 1, 1, nameof(type));

        /// <summary>Emits <see cref="OpCode.BoxAsX"/>.</summary>
        /// <param name="type">The class the box should present as.</param>
        public SurtrCodeEmitter BoxAsX(SurtrTypeToken type) => WithI32(OpCode.BoxAsX, TypeIndex(type), 1, 1);

        /// <summary>Emits <see cref="OpCode.RangeNew"/>: the exclusive <c>lo..hi</c> form.</summary>
        public SurtrCodeEmitter RangeNew() => Simple(OpCode.RangeNew, 2, 1);

        /// <summary>Emits <see cref="OpCode.RangeNewInclusive"/>: the <c>lo..=hi</c> form.</summary>
        public SurtrCodeEmitter RangeNewInclusive() => Simple(OpCode.RangeNewInclusive, 2, 1);

        /// <summary>Emits <see cref="OpCode.BoxFloat"/>.</summary>
        public SurtrCodeEmitter BoxFloat() => Simple(OpCode.BoxFloat, 1, 1);

        /// <summary>Emits <see cref="OpCode.BoxBool"/>.</summary>
        public SurtrCodeEmitter BoxBool() => Simple(OpCode.BoxBool, 1, 1);

        /// <summary>Emits <see cref="OpCode.BoxChar"/>.</summary>
        public SurtrCodeEmitter BoxChar() => Simple(OpCode.BoxChar, 1, 1);

        /// <summary>Emits <see cref="OpCode.Unbox"/>.</summary>
        public SurtrCodeEmitter Unbox() => Simple(OpCode.Unbox, 1, 1);

        /// <summary>Emits <see cref="OpCode.BoxDynamic"/>.</summary>
        public SurtrCodeEmitter BoxDynamic() => Simple(OpCode.BoxDynamic, 1, 1);

        /// <summary>Emits <see cref="OpCode.UnboxDynamic"/>.</summary>
        public SurtrCodeEmitter UnboxDynamic() => Simple(OpCode.UnboxDynamic, 1, 1);

        /// <summary>Emits <see cref="OpCode.Cast"/>.</summary>
        public SurtrCodeEmitter Cast(SurtrTypeToken type)
            => WithU16(OpCode.Cast, TypeIndex(type), 1, 1, "typeIdx");

        /// <summary>Emits <see cref="OpCode.CastX"/>.</summary>
        public SurtrCodeEmitter CastX(SurtrTypeToken type)
            => WithI32(OpCode.CastX, TypeIndex(type), 1, 1);

        /// <summary>Emits <see cref="OpCode.CastOrNull"/>: the <c>as?</c> form, which yields null rather than trapping.</summary>
        public SurtrCodeEmitter CastOrNull(SurtrTypeToken type)
            => WithU16(OpCode.CastOrNull, TypeIndex(type), 1, 1, "typeIdx");

        /// <summary>Emits <see cref="OpCode.CastOrNullX"/>.</summary>
        public SurtrCodeEmitter CastOrNullX(SurtrTypeToken type)
            => WithI32(OpCode.CastOrNullX, TypeIndex(type), 1, 1);

        /// <summary>Emits <see cref="OpCode.LoadType"/>.</summary>
        public SurtrCodeEmitter LoadType(SurtrTypeToken type)
            => WithU16(OpCode.LoadType, TypeIndex(type), 0, 1, "typeIdx");

        /// <summary>Emits <see cref="OpCode.LoadTypeX"/>.</summary>
        public SurtrCodeEmitter LoadTypeX(SurtrTypeToken type)
            => WithI32(OpCode.LoadTypeX, TypeIndex(type), 0, 1);

        /// <summary>Emits <see cref="OpCode.GetTypeOfValue"/>.</summary>
        public SurtrCodeEmitter GetTypeOfValue() => Simple(OpCode.GetTypeOfValue, 1, 1);

        /// <summary>Emits <see cref="OpCode.LoadModule"/>.</summary>
        public SurtrCodeEmitter LoadModule(SurtrModuleToken module)
            => WithU16(OpCode.LoadModule, ModuleIndex(module), 0, 1, "moduleIdx");

        /// <summary>Emits <see cref="OpCode.LoadModuleX"/>.</summary>
        public SurtrCodeEmitter LoadModuleX(SurtrModuleToken module)
            => WithI32(OpCode.LoadModuleX, ModuleIndex(module), 0, 1);

        /// <summary>Emits <see cref="OpCode.LoadCurrentModule"/>.</summary>
        public SurtrCodeEmitter LoadCurrentModule() => Simple(OpCode.LoadCurrentModule, 0, 1);

        #endregion

        #region String Operations

        /// <summary>Emits <see cref="OpCode.StrLen"/>.</summary>
        public SurtrCodeEmitter StrLen() => Simple(OpCode.StrLen, 1, 1);

        /// <summary>Emits <see cref="OpCode.StrCat"/>, joining the top <paramref name="count"/> strings.</summary>
        /// <param name="count">How many operands to join, from 2 to 255.</param>
        /// <remarks>
        /// A whole <c>+</c> spine or a whole interpolation should reach this as one call. Joining
        /// them two at a time builds every intermediate string, which is n - 1 allocations and a
        /// prefix copied n times, where one instruction with a count allocates exactly once.
        /// </remarks>
        public SurtrCodeEmitter StrCat(int count)
        {
            if (count < 2)
                throw new ArgumentOutOfRangeException(nameof(count), count, "StrCat joins at least two strings.");

            return WithU8(OpCode.StrCat, count, count, 1, nameof(count));
        }

        /// <summary>Emits <see cref="OpCode.StrHash"/>.</summary>
        public SurtrCodeEmitter StrHash() => Simple(OpCode.StrHash, 1, 1);

        /// <summary>Emits <see cref="OpCode.StrGet"/>.</summary>
        public SurtrCodeEmitter StrGet() => Simple(OpCode.StrGet, 2, 1);

        #endregion

        #region Array Operations

        /// <summary>Emits <see cref="OpCode.ArrNew"/>.</summary>
        /// <param name="arrayType">The whole parameterised array type, not its element type.</param>
        public SurtrCodeEmitter ArrNew(SurtrTypeToken arrayType)
            => WithU16(OpCode.ArrNew, TypeIndex(arrayType), 1, 1, "typeIdx");

        /// <summary>Emits <see cref="OpCode.ArrNewX"/>.</summary>
        /// <param name="arrayType">The whole parameterised array type, not its element type.</param>
        /// <param name="size">The statically known length, taken from the instruction rather than the stack.</param>
        public SurtrCodeEmitter ArrNewX(SurtrTypeToken arrayType, int size)
            => WithTypeAnd(OpCode.ArrNewX, arrayType, size, 4, int.MaxValue, 0, 1, "size");

        /// <summary>Emits <see cref="OpCode.ArrPack"/>.</summary>
        /// <param name="arrayType">The whole parameterised array type, not its element type.</param>
        /// <param name="size">How many values to pop into the new array.</param>
        public SurtrCodeEmitter ArrPack(SurtrTypeToken arrayType, int size)
            => WithTypeAnd(OpCode.ArrPack, arrayType, size, 2, ushort.MaxValue, size, 1, "size");

        /// <summary>Emits <see cref="OpCode.ArrLen"/>.</summary>
        public SurtrCodeEmitter ArrLen() => Simple(OpCode.ArrLen, 1, 1);

        /// <summary>Emits <see cref="OpCode.ArrGet"/>.</summary>
        public SurtrCodeEmitter ArrGet() => Simple(OpCode.ArrGet, 2, 1);

        /// <summary>Emits <see cref="OpCode.ArrSet"/>.</summary>
        public SurtrCodeEmitter ArrSet() => Simple(OpCode.ArrSet, 3, 0);

        /// <summary>Emits <see cref="OpCode.ArrPush"/>.</summary>
        public SurtrCodeEmitter ArrPush() => Simple(OpCode.ArrPush, 2, 0);

        /// <summary>Emits <see cref="OpCode.ArrPop"/>.</summary>
        public SurtrCodeEmitter ArrPop() => Simple(OpCode.ArrPop, 1, 1);

        /// <summary>Emits <see cref="OpCode.ArrInsert"/>.</summary>
        public SurtrCodeEmitter ArrInsert() => Simple(OpCode.ArrInsert, 3, 0);

        /// <summary>Emits <see cref="OpCode.ArrRemoveAt"/>.</summary>
        public SurtrCodeEmitter ArrRemoveAt() => Simple(OpCode.ArrRemoveAt, 2, 0);

        /// <summary>Emits <see cref="OpCode.ArrClear"/>.</summary>
        public SurtrCodeEmitter ArrClear() => Simple(OpCode.ArrClear, 1, 0);

        /// <summary>Emits <see cref="OpCode.ArrIndexOf"/>.</summary>
        public SurtrCodeEmitter ArrIndexOf() => Simple(OpCode.ArrIndexOf, 2, 1);

        /// <summary>Emits <see cref="OpCode.ArrIn"/>.</summary>
        public SurtrCodeEmitter ArrIn() => Simple(OpCode.ArrIn, 2, 1);

        /// <summary>Emits <see cref="OpCode.ArrNIn"/>.</summary>
        public SurtrCodeEmitter ArrNIn() => Simple(OpCode.ArrNIn, 2, 1);

        #endregion

        #region Tuple Operations

        /// <summary>Emits <see cref="OpCode.TupPack"/>.</summary>
        /// <param name="tupleType">The tuple's shape, which is the only place its element types are recorded.</param>
        /// <param name="size">How many values to pop, at most 255.</param>
        public SurtrCodeEmitter TupPack(SurtrTypeToken tupleType, int size)
            => WithTypeAnd(OpCode.TupPack, tupleType, size, 1, byte.MaxValue, size, 1, "size");

        /// <summary>Emits <see cref="OpCode.TupUnpack"/>.</summary>
        /// <param name="size">The tuple's arity, at most 255.</param>
        public SurtrCodeEmitter TupUnpack(int size) => WithU8(OpCode.TupUnpack, size, 1, size, "size");

        /// <summary>Emits <see cref="OpCode.TupLen"/>.</summary>
        public SurtrCodeEmitter TupLen() => Simple(OpCode.TupLen, 1, 1);

        /// <summary>Emits <see cref="OpCode.TupGet"/>, taking the index off the stack.</summary>
        /// <remarks>
        /// For an index that is genuinely computed - a lowered <c>for-in</c>'s loop counter. A
        /// written tuple index is a constant, and <see cref="TupGetC"/> is the form for that.
        /// </remarks>
        public SurtrCodeEmitter TupGet() => Simple(OpCode.TupGet, 2, 1);

        /// <summary>Emits <see cref="OpCode.TupGetC"/>, with the index as an immediate.</summary>
        /// <param name="index">Which element, from 0 to 254 - a tuple's arity is capped at 255.</param>
        public SurtrCodeEmitter TupGetC(int index) => WithU8(OpCode.TupGetC, index, 1, 1, nameof(index));

        #endregion

        #region Dictionary Operations

        /// <summary>Emits <see cref="OpCode.DictNew"/>.</summary>
        /// <param name="dictionaryType">The whole key/value pair, not either half.</param>
        public SurtrCodeEmitter DictNew(SurtrTypeToken dictionaryType)
            => WithU16(OpCode.DictNew, TypeIndex(dictionaryType), 0, 1, "typeIdx");

        /// <summary>Emits <see cref="OpCode.DictPack"/>.</summary>
        /// <param name="dictionaryType">The whole key/value pair, not either half.</param>
        /// <param name="count">How many key/value pairs to pop; twice this many stack entries.</param>
        public SurtrCodeEmitter DictPack(SurtrTypeToken dictionaryType, int count)
            => WithTypeAnd(OpCode.DictPack, dictionaryType, count, 2, ushort.MaxValue, count * 2, 1, "count");

        /// <summary>Emits <see cref="OpCode.DictLen"/>.</summary>
        public SurtrCodeEmitter DictLen() => Simple(OpCode.DictLen, 1, 1);

        /// <summary>Emits <see cref="OpCode.DictGet"/>.</summary>
        public SurtrCodeEmitter DictGet() => Simple(OpCode.DictGet, 2, 1);

        /// <summary>Emits <see cref="OpCode.DictSet"/>.</summary>
        public SurtrCodeEmitter DictSet() => Simple(OpCode.DictSet, 3, 0);

        /// <summary>Emits <see cref="OpCode.DictDel"/>.</summary>
        public SurtrCodeEmitter DictDel() => Simple(OpCode.DictDel, 2, 1);

        /// <summary>Emits <see cref="OpCode.DictClear"/>.</summary>
        public SurtrCodeEmitter DictClear() => Simple(OpCode.DictClear, 1, 0);

        /// <summary>Emits <see cref="OpCode.DictKeys"/>.</summary>
        /// <param name="keyArrayType">The type of the array being produced, which cannot be derived from the dictionary's own.</param>
        public SurtrCodeEmitter DictKeys(SurtrTypeToken keyArrayType)
            => WithU16(OpCode.DictKeys, TypeIndex(keyArrayType), 1, 1, "typeIdx");

        /// <summary>Emits <see cref="OpCode.DictValues"/>.</summary>
        /// <param name="valueArrayType">The type of the array being produced, which cannot be derived from the dictionary's own.</param>
        public SurtrCodeEmitter DictValues(SurtrTypeToken valueArrayType)
            => WithU16(OpCode.DictValues, TypeIndex(valueArrayType), 1, 1, "typeIdx");

        /// <summary>Emits <see cref="OpCode.DictIn"/>.</summary>
        public SurtrCodeEmitter DictIn() => Simple(OpCode.DictIn, 2, 1);

        /// <summary>Emits <see cref="OpCode.DictNIn"/>.</summary>
        public SurtrCodeEmitter DictNIn() => Simple(OpCode.DictNIn, 2, 1);

        #endregion

        #region Object Operations

        /// <summary>Emits <see cref="OpCode.ObjNew"/>.</summary>
        public SurtrCodeEmitter ObjNew(SurtrTypeToken type)
            => WithU16(OpCode.ObjNew, TypeIndex(type), 0, 1, "typeIdx");

        /// <summary>Emits <see cref="OpCode.ObjNewX"/>.</summary>
        public SurtrCodeEmitter ObjNewX(SurtrTypeToken type)
            => WithI32(OpCode.ObjNewX, TypeIndex(type), 0, 1);

        #endregion

        #region Field Operations

        /// <summary>Emits <see cref="OpCode.FieldGet"/>.</summary>
        public SurtrCodeEmitter FieldGet(SurtrFieldToken field)
            => WithU16(OpCode.FieldGet, FieldIndex(field), 1, 1, "fieldIdx");

        /// <summary>Emits <see cref="OpCode.FieldSet"/>.</summary>
        public SurtrCodeEmitter FieldSet(SurtrFieldToken field)
            => WithU16(OpCode.FieldSet, FieldIndex(field), 2, 0, "fieldIdx");

        /// <summary>Emits <see cref="OpCode.StaticFieldGet"/>.</summary>
        public SurtrCodeEmitter StaticFieldGet(SurtrFieldToken field)
            => WithU16(OpCode.StaticFieldGet, FieldIndex(field), 0, 1, "fieldIdx");

        /// <summary>Emits <see cref="OpCode.StaticFieldGetX"/>.</summary>
        public SurtrCodeEmitter StaticFieldGetX(SurtrFieldToken field)
            => WithI32(OpCode.StaticFieldGetX, FieldIndex(field), 0, 1);

        /// <summary>Emits <see cref="OpCode.StaticFieldSet"/>.</summary>
        public SurtrCodeEmitter StaticFieldSet(SurtrFieldToken field)
            => WithU16(OpCode.StaticFieldSet, FieldIndex(field), 1, 0, "fieldIdx");

        /// <summary>Emits <see cref="OpCode.StaticFieldSetX"/>.</summary>
        public SurtrCodeEmitter StaticFieldSetX(SurtrFieldToken field)
            => WithI32(OpCode.StaticFieldSetX, FieldIndex(field), 1, 0);

        #endregion

        #region Closure Operations

        /// <summary>Emits <see cref="OpCode.NewClosure"/>.</summary>
        /// <param name="function">The closure's body, an entry in the method access table.</param>
        /// <param name="upValueCount">How many captures to pop, at most 255.</param>
        public SurtrCodeEmitter NewClosure(SurtrMethodToken function, int upValueCount)
        {
            ThrowIfFinished();

            int index = MethodIndex(function);
            CheckRange(index, 0, ushort.MaxValue, OpCode.NewClosure, "functionIdx");
            CheckRange(upValueCount, 0, byte.MaxValue, OpCode.NewClosure, "upvaluesCount");

            Track(upValueCount, 1);

            _code.Add((byte)OpCode.NewClosure);
            _code.Add((byte)index);
            _code.Add((byte)(index >> 8));
            _code.Add((byte)upValueCount);
            return this;
        }

        /// <summary>Emits <see cref="OpCode.NewClosureX"/>.</summary>
        /// <param name="function">The closure's body, an entry in the method access table.</param>
        /// <param name="upValueCount">How many captures to pop, at most 255.</param>
        public SurtrCodeEmitter NewClosureX(SurtrMethodToken function, int upValueCount)
        {
            ThrowIfFinished();

            int index = MethodIndex(function);
            CheckRange(upValueCount, 0, byte.MaxValue, OpCode.NewClosureX, "upvaluesCount");

            Track(upValueCount, 1);

            _code.Add((byte)OpCode.NewClosureX);
            AppendI32(_code, index);
            _code.Add((byte)upValueCount);
            return this;
        }

        #endregion

        #region Upvalue Operations

        /// <summary>Emits <see cref="OpCode.UpValueGet"/>.</summary>
        public SurtrCodeEmitter UpValueGet(int upValueIndex)
            => WithU8(OpCode.UpValueGet, upValueIndex, 0, 1, "upvalueIdx");

        #endregion

        #region Control Flow Operations

        /// <summary>Emits <see cref="OpCode.JPZ"/>.</summary>
        public SurtrCodeEmitter JPZ(SurtrLabel target) => Branch(OpCode.JPZ, OpCode.JPZX, target, 1, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPNZ"/>.</summary>
        public SurtrCodeEmitter JPNZ(SurtrLabel target) => Branch(OpCode.JPNZ, OpCode.JPNZX, target, 1, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPN"/>.</summary>
        public SurtrCodeEmitter JPN(SurtrLabel target) => Branch(OpCode.JPN, OpCode.JPNX, target, 1, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPNN"/>.</summary>
        public SurtrCodeEmitter JPNN(SurtrLabel target) => Branch(OpCode.JPNN, OpCode.JPNNX, target, 1, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPA"/>: branches when a nullable primitive holds nothing.</summary>
        public SurtrCodeEmitter JPA(SurtrLabel target) => Branch(OpCode.JPA, OpCode.JPAX, target, 1, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPNA"/>: branches when a nullable primitive holds a value.</summary>
        public SurtrCodeEmitter JPNA(SurtrLabel target) => Branch(OpCode.JPNA, OpCode.JPNAX, target, 1, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JP"/>.</summary>
        public SurtrCodeEmitter JP(SurtrLabel target) => Branch(OpCode.JP, OpCode.JPX, target, 0, SurtrJumpWidth.Short, true);

        /// <summary>Emits <see cref="OpCode.JPZX"/>.</summary>
        public SurtrCodeEmitter JPZX(SurtrLabel target) => Branch(OpCode.JPZ, OpCode.JPZX, target, 1, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPNZX"/>.</summary>
        public SurtrCodeEmitter JPNZX(SurtrLabel target) => Branch(OpCode.JPNZ, OpCode.JPNZX, target, 1, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPNX"/>.</summary>
        public SurtrCodeEmitter JPNX(SurtrLabel target) => Branch(OpCode.JPN, OpCode.JPNX, target, 1, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPNNX"/>.</summary>
        public SurtrCodeEmitter JPNNX(SurtrLabel target) => Branch(OpCode.JPNN, OpCode.JPNNX, target, 1, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPX"/>.</summary>
        public SurtrCodeEmitter JPX(SurtrLabel target) => Branch(OpCode.JP, OpCode.JPX, target, 0, SurtrJumpWidth.Wide, true);

        /// <summary>Emits <see cref="OpCode.JPEQ"/>.</summary>
        public SurtrCodeEmitter JPEQ(SurtrLabel target) => Branch(OpCode.JPEQ, OpCode.JPEQX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPFEQ"/>.</summary>
        public SurtrCodeEmitter JPFEQ(SurtrLabel target) => Branch(OpCode.JPFEQ, OpCode.JPFEQX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPREQ"/>.</summary>
        public SurtrCodeEmitter JPREQ(SurtrLabel target) => Branch(OpCode.JPREQ, OpCode.JPREQX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPStrEQ"/>.</summary>
        public SurtrCodeEmitter JPStrEQ(SurtrLabel target) => Branch(OpCode.JPStrEQ, OpCode.JPStrEQX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPEQX"/>.</summary>
        public SurtrCodeEmitter JPEQX(SurtrLabel target) => Branch(OpCode.JPEQ, OpCode.JPEQX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPFEQX"/>.</summary>
        public SurtrCodeEmitter JPFEQX(SurtrLabel target) => Branch(OpCode.JPFEQ, OpCode.JPFEQX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPREQX"/>.</summary>
        public SurtrCodeEmitter JPREQX(SurtrLabel target) => Branch(OpCode.JPREQ, OpCode.JPREQX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPStrEQX"/>.</summary>
        public SurtrCodeEmitter JPStrEQX(SurtrLabel target) => Branch(OpCode.JPStrEQ, OpCode.JPStrEQX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPNE"/>.</summary>
        public SurtrCodeEmitter JPNE(SurtrLabel target) => Branch(OpCode.JPNE, OpCode.JPNEX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPFNE"/>.</summary>
        public SurtrCodeEmitter JPFNE(SurtrLabel target) => Branch(OpCode.JPFNE, OpCode.JPFNEX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPRNE"/>.</summary>
        public SurtrCodeEmitter JPRNE(SurtrLabel target) => Branch(OpCode.JPRNE, OpCode.JPRNEX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPStrNE"/>.</summary>
        public SurtrCodeEmitter JPStrNE(SurtrLabel target) => Branch(OpCode.JPStrNE, OpCode.JPStrNEX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPNEX"/>.</summary>
        public SurtrCodeEmitter JPNEX(SurtrLabel target) => Branch(OpCode.JPNE, OpCode.JPNEX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPFNEX"/>.</summary>
        public SurtrCodeEmitter JPFNEX(SurtrLabel target) => Branch(OpCode.JPFNE, OpCode.JPFNEX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPRNEX"/>.</summary>
        public SurtrCodeEmitter JPRNEX(SurtrLabel target) => Branch(OpCode.JPRNE, OpCode.JPRNEX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPStrNEX"/>.</summary>
        public SurtrCodeEmitter JPStrNEX(SurtrLabel target) => Branch(OpCode.JPStrNE, OpCode.JPStrNEX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPGT"/>.</summary>
        public SurtrCodeEmitter JPGT(SurtrLabel target) => Branch(OpCode.JPGT, OpCode.JPGTX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPFGT"/>.</summary>
        public SurtrCodeEmitter JPFGT(SurtrLabel target) => Branch(OpCode.JPFGT, OpCode.JPFGTX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPGTX"/>.</summary>
        public SurtrCodeEmitter JPGTX(SurtrLabel target) => Branch(OpCode.JPGT, OpCode.JPGTX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPFGTX"/>.</summary>
        public SurtrCodeEmitter JPFGTX(SurtrLabel target) => Branch(OpCode.JPFGT, OpCode.JPFGTX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPGE"/>.</summary>
        public SurtrCodeEmitter JPGE(SurtrLabel target) => Branch(OpCode.JPGE, OpCode.JPGEX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPFGE"/>.</summary>
        public SurtrCodeEmitter JPFGE(SurtrLabel target) => Branch(OpCode.JPFGE, OpCode.JPFGEX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPGEX"/>.</summary>
        public SurtrCodeEmitter JPGEX(SurtrLabel target) => Branch(OpCode.JPGE, OpCode.JPGEX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPFGEX"/>.</summary>
        public SurtrCodeEmitter JPFGEX(SurtrLabel target) => Branch(OpCode.JPFGE, OpCode.JPFGEX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPLT"/>.</summary>
        public SurtrCodeEmitter JPLT(SurtrLabel target) => Branch(OpCode.JPLT, OpCode.JPLTX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPFLT"/>.</summary>
        public SurtrCodeEmitter JPFLT(SurtrLabel target) => Branch(OpCode.JPFLT, OpCode.JPFLTX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPLTX"/>.</summary>
        public SurtrCodeEmitter JPLTX(SurtrLabel target) => Branch(OpCode.JPLT, OpCode.JPLTX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPFLTX"/>.</summary>
        public SurtrCodeEmitter JPFLTX(SurtrLabel target) => Branch(OpCode.JPFLT, OpCode.JPFLTX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPLE"/>.</summary>
        public SurtrCodeEmitter JPLE(SurtrLabel target) => Branch(OpCode.JPLE, OpCode.JPLEX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPFLE"/>.</summary>
        public SurtrCodeEmitter JPFLE(SurtrLabel target) => Branch(OpCode.JPFLE, OpCode.JPFLEX, target, 2, SurtrJumpWidth.Short, false);

        /// <summary>Emits <see cref="OpCode.JPLEX"/>.</summary>
        public SurtrCodeEmitter JPLEX(SurtrLabel target) => Branch(OpCode.JPLE, OpCode.JPLEX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPFLEX"/>.</summary>
        public SurtrCodeEmitter JPFLEX(SurtrLabel target) => Branch(OpCode.JPFLE, OpCode.JPFLEX, target, 2, SurtrJumpWidth.Wide, false);

        /// <summary>Emits <see cref="OpCode.JPInstanceOf"/>.</summary>
        public SurtrCodeEmitter JPInstanceOf(SurtrTypeToken type, SurtrLabel target)
            => BranchInstanceOf(type, target, SurtrJumpWidth.Short);

        /// <summary>Emits <see cref="OpCode.JPInstanceOfX"/>.</summary>
        public SurtrCodeEmitter JPInstanceOfX(SurtrTypeToken type, SurtrLabel target)
            => BranchInstanceOf(type, target, SurtrJumpWidth.Wide);

        /// <summary>Emits <see cref="OpCode.Switch"/>: a dense jump table over a contiguous range.</summary>
        /// <param name="low">The value <paramref name="cases"/><c>[0]</c> corresponds to.</param>
        /// <param name="cases">One target per value in <c>[low, low + cases.Length)</c>.</param>
        /// <param name="defaultTarget">Where anything outside that range goes.</param>
        public SurtrCodeEmitter Switch(int low, SurtrLabel[] cases, SurtrLabel defaultTarget)
        {
            ThrowIfFinished();

            if (cases is null)
                throw new ArgumentNullException(nameof(cases));

            Track(1, 0);

            int start = _code.Count;
            _code.Add((byte)OpCode.Switch);
            AppendI32(_code, low);
            AppendI32(_code, cases.Length);

            SwitchEntry(start, defaultTarget);

            for (int i = 0; i < cases.Length; i++)
                SwitchEntry(start, cases[i]);

            EndFlow();
            return this;
        }

        /// <summary>Emits <see cref="OpCode.SwitchLookup"/>: a binary-searched table of sparse keys.</summary>
        /// <param name="sortedCases">The arms, which must already be sorted by key, ascending.</param>
        /// <param name="defaultTarget">Where a value matching no key goes.</param>
        /// <exception cref="ArgumentException">The keys are not strictly ascending, which the interpreter's binary search assumes.</exception>
        public SurtrCodeEmitter SwitchLookup(IReadOnlyList<SurtrSwitchCase> sortedCases, SurtrLabel defaultTarget)
        {
            ThrowIfFinished();

            if (sortedCases is null)
                throw new ArgumentNullException(nameof(sortedCases));

            for (int i = 1; i < sortedCases.Count; i++)
            {
                if (sortedCases[i].Key <= sortedCases[i - 1].Key)
                    throw new ArgumentException(
                        $"SwitchLookup keys must be strictly ascending; {sortedCases[i - 1].Key} is followed by {sortedCases[i].Key}.",
                        nameof(sortedCases));
            }

            Track(1, 0);

            int start = _code.Count;
            _code.Add((byte)OpCode.SwitchLookup);
            AppendI32(_code, sortedCases.Count);

            SwitchEntry(start, defaultTarget);

            for (int i = 0; i < sortedCases.Count; i++)
            {
                AppendI32(_code, sortedCases[i].Key);
                SwitchEntry(start, sortedCases[i].Label);
            }

            EndFlow();
            return this;
        }

        #endregion

        #region Call Operations

        /// <summary>Emits <see cref="OpCode.CallLocalModule"/>.</summary>
        public SurtrCodeEmitter CallLocalModule(SurtrMethodToken function, int argumentCount, int resultCount)
            => WithCall(OpCode.CallLocalModule, MethodIndex(function), 2, argumentCount, resultCount);

        /// <summary>Emits <see cref="OpCode.CallLocalModuleX"/>.</summary>
        public SurtrCodeEmitter CallLocalModuleX(SurtrMethodToken function, int argumentCount, int resultCount)
            => WithCall(OpCode.CallLocalModuleX, MethodIndex(function), 4, argumentCount, resultCount);

        /// <summary>Emits <see cref="OpCode.CallModule"/>.</summary>
        public SurtrCodeEmitter CallModule(SurtrExternalMethodToken target, int argumentCount, int resultCount)
        {
            ThrowIfFinished();

            if (!target.IsValid)
                throw new ArgumentException("External method token was not obtained from a module builder.", nameof(target));

            CheckRange(target.ModuleIndex, 0, ushort.MaxValue, OpCode.CallModule, "moduleIdx");
            CheckRange(target.FunctionIndex, 0, ushort.MaxValue, OpCode.CallModule, "functionIdx");
            CheckArgumentCounts(OpCode.CallModule, argumentCount, resultCount);

            Track(argumentCount, resultCount);

            _code.Add((byte)OpCode.CallModule);
            _code.Add((byte)target.ModuleIndex);
            _code.Add((byte)(target.ModuleIndex >> 8));
            _code.Add((byte)target.FunctionIndex);
            _code.Add((byte)(target.FunctionIndex >> 8));
            _code.Add((byte)argumentCount);
            _code.Add((byte)resultCount);
            return this;
        }

        /// <summary>Emits <see cref="OpCode.CallModuleX"/>.</summary>
        public SurtrCodeEmitter CallModuleX(SurtrExternalMethodToken target, int argumentCount, int resultCount)
        {
            ThrowIfFinished();

            if (!target.IsValid)
                throw new ArgumentException("External method token was not obtained from a module builder.", nameof(target));

            CheckArgumentCounts(OpCode.CallModuleX, argumentCount, resultCount);

            Track(argumentCount, resultCount);

            _code.Add((byte)OpCode.CallModuleX);
            AppendI32(_code, target.ModuleIndex);
            AppendI32(_code, target.FunctionIndex);
            _code.Add((byte)argumentCount);
            _code.Add((byte)resultCount);
            return this;
        }

        #endregion

        #region Method Operations

        /// <summary>Emits <see cref="OpCode.InvokeVirtual"/>.</summary>
        /// <param name="method">The call target, an entry in the method access table.</param>
        /// <param name="argumentCount">Every incoming slot, the receiver included.</param>
        /// <param name="resultCount">0 or 1: the frame protocol writes back at most one value.</param>
        public SurtrCodeEmitter InvokeVirtual(SurtrMethodToken method, int argumentCount, int resultCount)
            => WithCall(OpCode.InvokeVirtual, MethodIndex(method), 2, argumentCount, resultCount);

        /// <summary>Emits <see cref="OpCode.InvokeSpecial"/>.</summary>
        /// <param name="method">The call target, an entry in the method access table.</param>
        /// <param name="argumentCount">Every incoming slot, the receiver included.</param>
        /// <param name="resultCount">0 or 1: the frame protocol writes back at most one value.</param>
        public SurtrCodeEmitter InvokeSpecial(SurtrMethodToken method, int argumentCount, int resultCount)
            => WithCall(OpCode.InvokeSpecial, MethodIndex(method), 2, argumentCount, resultCount);

        /// <summary>Emits <see cref="OpCode.InvokeStatic"/>.</summary>
        public SurtrCodeEmitter InvokeStatic(SurtrMethodToken method, int argumentCount, int resultCount)
            => WithCall(OpCode.InvokeStatic, MethodIndex(method), 2, argumentCount, resultCount);

        /// <summary>Emits <see cref="OpCode.InvokeStaticX"/>.</summary>
        public SurtrCodeEmitter InvokeStaticX(SurtrMethodToken method, int argumentCount, int resultCount)
            => WithCall(OpCode.InvokeStaticX, MethodIndex(method), 4, argumentCount, resultCount);

        /// <summary>Emits <see cref="OpCode.InvokeInterface"/>.</summary>
        /// <param name="method">The call target, an entry in the method access table.</param>
        /// <param name="argumentCount">Every incoming slot, the receiver included.</param>
        /// <param name="resultCount">0 or 1: the frame protocol writes back at most one value.</param>
        public SurtrCodeEmitter InvokeInterface(SurtrMethodToken method, int argumentCount, int resultCount)
            => WithCall(OpCode.InvokeInterface, MethodIndex(method), 2, argumentCount, resultCount);

        /// <summary>Emits <see cref="OpCode.InvokeClosure"/>.</summary>
        /// <param name="argumentCount">
        /// The arguments alone. The closure itself sits below them and is consumed as well, so this
        /// instruction removes one more slot than it is told about.
        /// </param>
        /// <param name="resultCount">0 or 1: the frame protocol writes back at most one value.</param>
        public SurtrCodeEmitter InvokeClosure(int argumentCount, int resultCount)
        {
            ThrowIfFinished();
            CheckArgumentCounts(OpCode.InvokeClosure, argumentCount, resultCount);

            Track(argumentCount + 1, resultCount);

            _code.Add((byte)OpCode.InvokeClosure);
            _code.Add((byte)argumentCount);
            _code.Add((byte)resultCount);
            return this;
        }

        #endregion

        #region Exception Operations

        /// <summary>Emits <see cref="OpCode.Throw"/>.</summary>
        public SurtrCodeEmitter Throw()
        {
            Simple(OpCode.Throw, 1, 0);
            EndFlow();
            return this;
        }

        #endregion

        #region Return Operations

        /// <summary>Emits <see cref="OpCode.ReturnVoid"/>.</summary>
        public SurtrCodeEmitter ReturnVoid()
        {
            Simple(OpCode.ReturnVoid, 0, 0);
            EndFlow();
            return this;
        }

        /// <summary>Emits <see cref="OpCode.ReturnValue"/>.</summary>
        public SurtrCodeEmitter ReturnValue()
        {
            Simple(OpCode.ReturnValue, 1, 0);
            EndFlow();
            return this;
        }

        #endregion
    }
}
