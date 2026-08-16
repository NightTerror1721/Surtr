#nullable enable

using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Surtr.Bytecode.Emit
{
    /// <summary>
    /// Renders a built module's bytecode as text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For debugging an emitter and for asserting on emitted code in tests. It decodes the same
    /// byte layout the interpreter does, so a disagreement between the two shows up as garbled
    /// output rather than as a wrong answer at run time - which makes this the cheapest check that
    /// a newly written emit path lays its immediates out the way the opcode says it should.
    /// </para>
    /// <para>
    /// This is diagnostic code and makes no attempt to be fast or allocation-free.
    /// </para>
    /// </remarks>
    public static class SurtrBytecodeDisassembler
    {
        /// <summary>Renders every method in <paramref name="module"/>, in entry order.</summary>
        /// <exception cref="InvalidOperationException">The module's chunk has not been built.</exception>
        public static string Disassemble(SurtrModule module)
        {
            if (module is null)
                throw new ArgumentNullException(nameof(module));

            var chunk = module.Chunk;
            var builder = new StringBuilder();

            builder.Append("module ").Append(module.Path).AppendLine();
            AppendPools(builder, chunk);

            var bodies = CollectBodies(module);
            foreach (var body in bodies)
            {
                builder.AppendLine();
                AppendMethod(builder, chunk, body);
            }

            return builder.ToString();
        }

        /// <summary>Renders a single method.</summary>
        public static string Disassemble(SurtrBytecodeMethodInfo method)
        {
            if (method is null)
                throw new ArgumentNullException(nameof(method));

            var builder = new StringBuilder();
            AppendMethod(builder, method.Chunk, method);
            return builder.ToString();
        }

        private static List<SurtrBytecodeMethodInfo> CollectBodies(SurtrModule module)
        {
            var bodies = new List<SurtrBytecodeMethodInfo>();

            foreach (var overloads in module.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                {
                    if (overloads[i] is SurtrBytecodeMethodInfo bytecode)
                        bodies.Add(bytecode);
                }
            }

            foreach (var declared in module.Classes)
                CollectClassBodies(declared, bodies);

            bodies.Sort(static (left, right) => left.EntryIndex.CompareTo(right.EntryIndex));
            return bodies;
        }

        private static void CollectClassBodies(SurtrClass declared, List<SurtrBytecodeMethodInfo> bodies)
        {
            foreach (var overloads in declared.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                {
                    if (overloads[i] is SurtrBytecodeMethodInfo bytecode)
                        bodies.Add(bytecode);
                }
            }

            foreach (var nested in declared.NestedClasses)
                CollectClassBodies(nested, bodies);
        }

        private static void AppendPools(StringBuilder builder, SurtrChunk chunk)
        {
            builder.Append("  constants: ").Append(chunk.Constants.Length)
                   .Append("   types: ").Append(chunk.TypeTable.Length)
                   .Append("   fields: ").Append(chunk.FieldTable.Length)
                   .Append("   methods: ").Append(chunk.MethodTable.Length)
                   .Append("   modules: ").Append(chunk.ModuleTable.Length)
                   .AppendLine();

            for (int i = 0; i < chunk.StringConstants.Length; i++)
            {
                builder.Append("  string #").Append(chunk.StringConstantSlots[i])
                       .Append(" = \"").Append(chunk.StringConstants[i]).Append('"')
                       .AppendLine();
            }
        }

        private static void AppendMethod(StringBuilder builder, SurtrChunk chunk, SurtrBytecodeMethodInfo method)
        {
            int start = method.CodeOffset;
            int end = EndOffsetOf(chunk, start);

            builder.Append(method.IsStatic ? "static " : string.Empty)
                   .Append(method.Name)
                   .Append(method.ToSignature().ToDisplayString())
                   .Append("   [entry ").Append(method.EntryIndex)
                   .Append(", locals ").Append(method.LocalCount)
                   .Append(", stack ").Append(method.MaxStackSize)
                   .Append(", code ").Append(start).Append("..").Append(end).Append(']')
                   .AppendLine();

            int position = start;
            while (position < end)
                position = AppendInstruction(builder, chunk, position);

            var handlers = method.Handlers;
            for (int i = 0; i < handlers.Length; i++)
            {
                builder.Append("  handler [").Append(Hex(handlers[i].TryStart))
                       .Append(", ").Append(Hex(handlers[i].TryEnd)).Append(") -> ")
                       .Append(Hex(handlers[i].HandlerOffset))
                       .Append("  catch ")
                       .Append(handlers[i].CatchType is null ? "*" : handlers[i].CatchType!.Reference.ToDisplayString())
                       .AppendLine();
            }
        }

        /// <summary>
        /// Where a body ends: at the start of whichever body begins after it, or at the end of the
        /// chunk when nothing does.
        /// </summary>
        /// <remarks>
        /// Methods carry a start offset and nothing else, because a body always runs to the next
        /// one - the emitter lays them out back to back in one stream.
        /// </remarks>
        private static int EndOffsetOf(SurtrChunk chunk, int start)
        {
            int end = chunk.Code.Length;

            for (int i = 0; i < chunk.MethodOffsets.Length; i++)
            {
                int candidate = chunk.MethodOffsets[i];
                if (candidate > start && candidate < end)
                    end = candidate;
            }

            return end;
        }

        private static int AppendInstruction(StringBuilder builder, SurtrChunk chunk, int position)
        {
            var op = (OpCode)chunk.Code[position];
            int operand = position + 1;

            builder.Append("  ").Append(Hex(position)).Append("  ").Append(op.ToString());

            switch (op)
            {
                // ---- no immediate ------------------------------------------------------------
                case OpCode.Nop:
                case OpCode.Dup:
                case OpCode.Dup2:
                case OpCode.Swap:
                case OpCode.Swap2:
                case OpCode.PushNull:
                case OpCode.PushTrue:
                case OpCode.PushFalse:
                case OpCode.Pop:
                case OpCode.Ldc0:
                case OpCode.Ldc1:
                case OpCode.Ldc2:
                case OpCode.Ldc3:
                case OpCode.Ldc4:
                case OpCode.Ldc5:
                case OpCode.Ldc6:
                case OpCode.Ldc7:
                case OpCode.Ldc8:
                case OpCode.Ldc9:
                case OpCode.Ldl0:
                case OpCode.Ldl1:
                case OpCode.Ldl2:
                case OpCode.Ldl3:
                case OpCode.Ldl4:
                case OpCode.Ldl5:
                case OpCode.Stl0:
                case OpCode.Stl1:
                case OpCode.Stl2:
                case OpCode.Stl3:
                case OpCode.Stl4:
                case OpCode.Stl5:
                case OpCode.Add:
                case OpCode.FAdd:
                case OpCode.Sub:
                case OpCode.FSub:
                case OpCode.Mul:
                case OpCode.FMul:
                case OpCode.Div:
                case OpCode.FDiv:
                case OpCode.Mod:
                case OpCode.FMod:
                case OpCode.Pow:
                case OpCode.FPow:
                case OpCode.Neg:
                case OpCode.FNeg:
                case OpCode.Inv:
                case OpCode.EQ:
                case OpCode.FEQ:
                case OpCode.REQ:
                case OpCode.StrEQ:
                case OpCode.NE:
                case OpCode.FNE:
                case OpCode.RNE:
                case OpCode.StrNE:
                case OpCode.GT:
                case OpCode.FGT:
                case OpCode.GE:
                case OpCode.FGE:
                case OpCode.LT:
                case OpCode.FLT:
                case OpCode.LE:
                case OpCode.FLE:
                case OpCode.IsNull:
                case OpCode.IsNotNull:
                case OpCode.And:
                case OpCode.Or:
                case OpCode.Xor:
                case OpCode.Not:
                case OpCode.Shl:
                case OpCode.Shr:
                case OpCode.Sar:
                case OpCode.I2F:
                case OpCode.F2I:
                case OpCode.I2C:
                case OpCode.C2I:
                case OpCode.I2B:
                case OpCode.B2I:
                case OpCode.IsAbsent:
                case OpCode.IsPresent:
                case OpCode.RangeNew:
                case OpCode.RangeNewInclusive:
                case OpCode.BoxInt:
                case OpCode.BoxFloat:
                case OpCode.BoxBool:
                case OpCode.BoxChar:
                case OpCode.Unbox:
                case OpCode.StrLen:
                case OpCode.StrHash:
                case OpCode.StrGet:
                case OpCode.ArrLen:
                case OpCode.ArrGet:
                case OpCode.ArrSet:
                case OpCode.ArrPush:
                case OpCode.ArrPop:
                case OpCode.ArrInsert:
                case OpCode.ArrRemoveAt:
                case OpCode.ArrClear:
                case OpCode.ArrIndexOf:
                case OpCode.ArrIn:
                case OpCode.ArrNIn:
                case OpCode.TupLen:
                case OpCode.TupGet:
                case OpCode.DictLen:
                case OpCode.DictGet:
                case OpCode.DictSet:
                case OpCode.DictDel:
                case OpCode.DictClear:
                case OpCode.DictIn:
                case OpCode.DictNIn:
                case OpCode.Throw:
                case OpCode.ReturnVoid:
                case OpCode.ReturnValue:
                    builder.AppendLine();
                    return operand;

                // ---- one inline literal ------------------------------------------------------
                case OpCode.PushI8:
                    builder.Append(' ').Append((sbyte)chunk.Code[operand]).AppendLine();
                    return operand + 1;

                case OpCode.PushI16:
                    builder.Append(' ').Append((short)ReadU16(chunk, operand)).AppendLine();
                    return operand + 2;

                case OpCode.PushChar:
                {
                    // A control character rendered as itself would corrupt the listing, so anything
                    // outside printable ASCII is shown as its code unit instead.
                    int unit = ReadU16(chunk, operand);

                    if (unit >= 0x20 && unit < 0x7F)
                        builder.Append(" '").Append((char)unit).Append('\'').AppendLine();
                    else
                        builder.Append(" U+").Append(unit.ToString("X4", CultureInfo.InvariantCulture)).AppendLine();

                    return operand + 2;
                }

                case OpCode.PushI32:
                    builder.Append(' ').Append(ReadI32(chunk, operand)).AppendLine();
                    return operand + 4;

                // ---- constant pool -----------------------------------------------------------
                case OpCode.LdcS:
                    return AppendConstant(builder, chunk, operand, chunk.Code[operand], 1);

                case OpCode.Ldc:
                    return AppendConstant(builder, chunk, operand, ReadU16(chunk, operand), 2);

                case OpCode.LdcX:
                    return AppendConstant(builder, chunk, operand, ReadI32(chunk, operand), 4);

                // ---- frame slots and host globals --------------------------------------------
                case OpCode.LdlS:
                case OpCode.StlS:
                case OpCode.TupUnpack:
                case OpCode.TupGetC:
                case OpCode.UpValueGet:
                case OpCode.StrCat:
                case OpCode.PushAbsent:
                    builder.Append(' ').Append(chunk.Code[operand]).AppendLine();
                    return operand + 1;

                case OpCode.IncLocal:
                    builder.Append(' ').Append(chunk.Code[operand])
                           .Append(" by ").Append((sbyte)chunk.Code[operand + 1])
                           .AppendLine();
                    return operand + 2;

                case OpCode.Ldl:
                case OpCode.Stl:
                    builder.Append(' ').Append(ReadU16(chunk, operand)).AppendLine();
                    return operand + 2;

                // ---- type access table -------------------------------------------------------
                case OpCode.InstanceOf:
                case OpCode.Cast:
                case OpCode.CastOrNull:
                case OpCode.ArrNew:
                case OpCode.DictNew:
                case OpCode.DictKeys:
                case OpCode.DictValues:
                case OpCode.ObjNew:
                case OpCode.BoxAs:
                    return AppendType(builder, chunk, operand, ReadU16(chunk, operand), 2);

                case OpCode.InstanceOfX:
                case OpCode.CastX:
                case OpCode.CastOrNullX:
                case OpCode.ObjNewX:
                case OpCode.BoxAsX:
                    return AppendType(builder, chunk, operand, ReadI32(chunk, operand), 4);

                case OpCode.ArrNewX:
                    AppendTypeName(builder, chunk, ReadU16(chunk, operand));
                    builder.Append(" size=").Append(ReadI32(chunk, operand + 2)).AppendLine();
                    return operand + 6;

                case OpCode.ArrPack:
                case OpCode.DictPack:
                    AppendTypeName(builder, chunk, ReadU16(chunk, operand));
                    builder.Append(" count=").Append(ReadU16(chunk, operand + 2)).AppendLine();
                    return operand + 4;

                case OpCode.TupPack:
                    AppendTypeName(builder, chunk, ReadU16(chunk, operand));
                    builder.Append(" count=").Append(chunk.Code[operand + 2]).AppendLine();
                    return operand + 3;

                // ---- field access table ------------------------------------------------------
                case OpCode.FieldGet:
                case OpCode.FieldSet:
                case OpCode.StaticFieldGet:
                case OpCode.StaticFieldSet:
                    return AppendField(builder, chunk, operand, ReadU16(chunk, operand), 2);

                case OpCode.StaticFieldGetX:
                case OpCode.StaticFieldSetX:
                    return AppendField(builder, chunk, operand, ReadI32(chunk, operand), 4);

                // ---- closures ----------------------------------------------------------------
                case OpCode.NewClosure:
                    AppendMethodName(builder, chunk, ReadU16(chunk, operand));
                    builder.Append(" upvalues=").Append(chunk.Code[operand + 2]).AppendLine();
                    return operand + 3;

                case OpCode.NewClosureX:
                    AppendMethodName(builder, chunk, ReadI32(chunk, operand));
                    builder.Append(" upvalues=").Append(chunk.Code[operand + 4]).AppendLine();
                    return operand + 5;

                // ---- branches ----------------------------------------------------------------
                case OpCode.JPZ:
                case OpCode.JPNZ:
                case OpCode.JPN:
                case OpCode.JPNN:
                case OpCode.JPA:
                case OpCode.JPNA:
                case OpCode.JP:
                case OpCode.JPEQ:
                case OpCode.JPFEQ:
                case OpCode.JPREQ:
                case OpCode.JPStrEQ:
                case OpCode.JPNE:
                case OpCode.JPFNE:
                case OpCode.JPRNE:
                case OpCode.JPStrNE:
                case OpCode.JPGT:
                case OpCode.JPFGT:
                case OpCode.JPGE:
                case OpCode.JPFGE:
                case OpCode.JPLT:
                case OpCode.JPFLT:
                case OpCode.JPLE:
                case OpCode.JPFLE:
                    return AppendBranch(builder, chunk, operand, (short)ReadU16(chunk, operand), 2);

                case OpCode.JPZX:
                case OpCode.JPNZX:
                case OpCode.JPNX:
                case OpCode.JPNNX:
                case OpCode.JPAX:
                case OpCode.JPNAX:
                case OpCode.JPX:
                case OpCode.JPEQX:
                case OpCode.JPFEQX:
                case OpCode.JPREQX:
                case OpCode.JPStrEQX:
                case OpCode.JPNEX:
                case OpCode.JPFNEX:
                case OpCode.JPRNEX:
                case OpCode.JPStrNEX:
                case OpCode.JPGTX:
                case OpCode.JPFGTX:
                case OpCode.JPGEX:
                case OpCode.JPFGEX:
                case OpCode.JPLTX:
                case OpCode.JPFLTX:
                case OpCode.JPLEX:
                case OpCode.JPFLEX:
                    return AppendBranch(builder, chunk, operand, ReadI32(chunk, operand), 4);

                case OpCode.JPInstanceOf:
                    AppendTypeName(builder, chunk, ReadU16(chunk, operand));
                    builder.Append(" -> ").Append(Hex(operand + 4 + (short)ReadU16(chunk, operand + 2))).AppendLine();
                    return operand + 4;

                case OpCode.JPInstanceOfX:
                    AppendTypeName(builder, chunk, ReadI32(chunk, operand));
                    builder.Append(" -> ").Append(Hex(operand + 8 + ReadI32(chunk, operand + 4))).AppendLine();
                    return operand + 8;

                case OpCode.Switch:
                    return AppendSwitch(builder, chunk, position, operand);

                case OpCode.SwitchLookup:
                    return AppendSwitchLookup(builder, chunk, position, operand);

                // ---- calls -------------------------------------------------------------------
                case OpCode.CallLocalModule:
                case OpCode.InvokeVirtual:
                case OpCode.InvokeSpecial:
                case OpCode.InvokeStatic:
                case OpCode.InvokeInterface:
                    AppendMethodName(builder, chunk, ReadU16(chunk, operand));
                    AppendCounts(builder, chunk, operand + 2);
                    return operand + 4;

                case OpCode.CallLocalModuleX:
                case OpCode.InvokeStaticX:
                    AppendMethodName(builder, chunk, ReadI32(chunk, operand));
                    AppendCounts(builder, chunk, operand + 4);
                    return operand + 6;

                case OpCode.CallModule:
                    AppendExternal(builder, chunk, ReadU16(chunk, operand), ReadU16(chunk, operand + 2));
                    AppendCounts(builder, chunk, operand + 4);
                    return operand + 6;

                case OpCode.CallModuleX:
                    AppendExternal(builder, chunk, ReadI32(chunk, operand), ReadI32(chunk, operand + 4));
                    AppendCounts(builder, chunk, operand + 8);
                    return operand + 10;

                case OpCode.InvokeClosure:
                    AppendCounts(builder, chunk, operand);
                    return operand + 2;

                default:
                    builder.Append("  ; unknown opcode 0x").Append(((byte)op).ToString("X2", CultureInfo.InvariantCulture)).AppendLine();
                    return operand;
            }
        }

        private static int AppendConstant(StringBuilder builder, SurtrChunk chunk, int operand, int index, int width)
        {
            builder.Append(' ').Append(index);

            for (int i = 0; i < chunk.StringConstantSlots.Length; i++)
            {
                if (chunk.StringConstantSlots[i] == index)
                {
                    builder.Append(" \"").Append(chunk.StringConstants[i]).Append('"');
                    break;
                }
            }

            builder.AppendLine();
            return operand + width;
        }

        private static int AppendType(StringBuilder builder, SurtrChunk chunk, int operand, int index, int width)
        {
            AppendTypeName(builder, chunk, index);
            builder.AppendLine();
            return operand + width;
        }

        private static void AppendTypeName(StringBuilder builder, SurtrChunk chunk, int index)
        {
            builder.Append(' ').Append(index);

            if ((uint)index < (uint)chunk.TypeTable.Length)
                builder.Append(" (").Append(chunk.TypeTable[index].Reference.ToDisplayString()).Append(')');
        }

        private static int AppendField(StringBuilder builder, SurtrChunk chunk, int operand, int index, int width)
        {
            builder.Append(' ').Append(index);

            if ((uint)index < (uint)chunk.FieldTable.Length)
                builder.Append(" (").Append(chunk.FieldTable[index].Name).Append(')');

            builder.AppendLine();
            return operand + width;
        }

        private static void AppendMethodName(StringBuilder builder, SurtrChunk chunk, int index)
        {
            builder.Append(' ').Append(index);

            if ((uint)index < (uint)chunk.MethodTable.Length)
                builder.Append(" (").Append(chunk.MethodTable[index].Name).Append(')');
        }

        private static void AppendExternal(StringBuilder builder, SurtrChunk chunk, int moduleIndex, int functionIndex)
        {
            builder.Append(' ').Append(moduleIndex).Append(':').Append(functionIndex);

            if ((uint)moduleIndex < (uint)chunk.ModuleTable.Length)
            {
                var target = chunk.ModuleTable[moduleIndex];
                builder.Append(" (").Append(target.Path);

                if ((uint)functionIndex < (uint)target.Chunk.MethodTable.Length)
                    builder.Append('.').Append(target.Chunk.MethodTable[functionIndex].Name);

                builder.Append(')');
            }
        }

        private static void AppendCounts(StringBuilder builder, SurtrChunk chunk, int position)
        {
            builder.Append(" args=").Append(chunk.Code[position])
                   .Append(" ret=").Append(chunk.Code[position + 1])
                   .AppendLine();
        }

        private static int AppendBranch(StringBuilder builder, SurtrChunk chunk, int operand, int offset, int width)
        {
            int next = operand + width;
            builder.Append(' ').Append(offset >= 0 ? "+" : string.Empty).Append(offset)
                   .Append(" -> ").Append(Hex(next + offset))
                   .AppendLine();
            return next;
        }

        private static int AppendSwitch(StringBuilder builder, SurtrChunk chunk, int instruction, int operand)
        {
            int low = ReadI32(chunk, operand);
            int count = ReadI32(chunk, operand + 4);

            builder.Append(" low=").Append(low).Append(" count=").Append(count)
                   .Append(" default -> ").Append(Hex(instruction + ReadI32(chunk, operand + 8)))
                   .AppendLine();

            for (int i = 0; i < count; i++)
            {
                builder.Append("          case ").Append(low + i)
                       .Append(" -> ").Append(Hex(instruction + ReadI32(chunk, operand + 12 + (i * 4))))
                       .AppendLine();
            }

            return operand + 12 + (count * 4);
        }

        private static int AppendSwitchLookup(StringBuilder builder, SurtrChunk chunk, int instruction, int operand)
        {
            int count = ReadI32(chunk, operand);

            builder.Append(" count=").Append(count)
                   .Append(" default -> ").Append(Hex(instruction + ReadI32(chunk, operand + 4)))
                   .AppendLine();

            for (int i = 0; i < count; i++)
            {
                int entry = operand + 8 + (i * 8);
                builder.Append("          case ").Append(ReadI32(chunk, entry))
                       .Append(" -> ").Append(Hex(instruction + ReadI32(chunk, entry + 4)))
                       .AppendLine();
            }

            return operand + 8 + (count * 8);
        }

        private static int ReadU16(SurtrChunk chunk, int position)
            => chunk.Code[position] | (chunk.Code[position + 1] << 8);

        private static int ReadI32(SurtrChunk chunk, int position)
            => chunk.Code[position]
             | (chunk.Code[position + 1] << 8)
             | (chunk.Code[position + 2] << 16)
             | (chunk.Code[position + 3] << 24);

        private static string Hex(int offset) => offset.ToString("X4", CultureInfo.InvariantCulture);
    }
}
