#nullable enable

using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;

namespace Surtr.Bytecode.Emit
{
    // Tier three: the surface a compiler actually writes against.
    //
    // Where tier two is one method per opcode, these are one method per *intent* - load a local,
    // compare two values, call this method - and they choose the encoding. That is the whole point:
    // the instruction set spells out dedicated opcodes for small indices, narrow immediates and each
    // operand family precisely so a compiler does not have to think about any of it, and this is
    // where that thinking is done once instead of at every emit site.
    public sealed partial class SurtrCodeEmitter
    {
        #region Locals

        /// <summary>Loads a local, in the narrowest encoding that reaches its slot.</summary>
        public SurtrCodeEmitter LoadLocal(SurtrLocal local) => LoadLocal(LocalIndex(local));

        /// <summary>Loads the local at <paramref name="index"/>, in the narrowest encoding that reaches it.</summary>
        public SurtrCodeEmitter LoadLocal(int index) => index switch
        {
            0 => Ldl0(),
            1 => Ldl1(),
            2 => Ldl2(),
            3 => Ldl3(),
            4 => Ldl4(),
            5 => Ldl5(),
            <= byte.MaxValue and > 5 => LdlS(index),
            <= ushort.MaxValue => Ldl(index),
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A frame cannot hold more than 65536 locals; there is no wider form of Ldl."),
        };

        /// <summary>Stores into a local, in the narrowest encoding that reaches its slot.</summary>
        public SurtrCodeEmitter StoreLocal(SurtrLocal local) => StoreLocal(LocalIndex(local));

        /// <summary>Stores into the local at <paramref name="index"/>, in the narrowest encoding that reaches it.</summary>
        public SurtrCodeEmitter StoreLocal(int index) => index switch
        {
            0 => Stl0(),
            1 => Stl1(),
            2 => Stl2(),
            3 => Stl3(),
            4 => Stl4(),
            5 => Stl5(),
            <= byte.MaxValue and > 5 => StlS(index),
            <= ushort.MaxValue => Stl(index),
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A frame cannot hold more than 65536 locals; there is no wider form of Stl."),
        };

        private int LocalIndex(SurtrLocal local)
        {
            if (!local.IsValid)
                throw new ArgumentException("Local was not obtained from a method builder.", nameof(local));
            return local.Index;
        }

        #endregion

        #region Constants and literals

        /// <summary>Loads a pool constant, in the narrowest encoding that reaches its slot.</summary>
        public SurtrCodeEmitter LoadConstant(SurtrConstantToken constant)
        {
            int index = ConstantIndex(constant);
            return index switch
            {
                0 => Ldc0(),
                1 => Ldc1(),
                2 => Ldc2(),
                3 => Ldc3(),
                4 => Ldc4(),
                5 => Ldc5(),
                6 => Ldc6(),
                7 => Ldc7(),
                8 => Ldc8(),
                9 => Ldc9(),
                <= byte.MaxValue => LdcS(constant),
                <= ushort.MaxValue => Ldc(constant),
                _ => LdcX(constant),
            };
        }

        /// <summary>Materialises an integer literal without touching the constant pool.</summary>
        /// <remarks>
        /// <c>PushI8</c>/<c>PushI16</c>/<c>PushI32</c> carry the value inline, so a small integer
        /// costs no pool slot at all - which matters because the cheap <c>Ldc0</c>…<c>Ldc9</c>
        /// encodings are a scarce resource better spent on values that cannot be pushed inline.
        /// </remarks>
        public SurtrCodeEmitter LoadInt(int value)
        {
            if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
                return PushI8(value);

            if (value >= short.MinValue && value <= short.MaxValue)
                return PushI16(value);

            return PushI32(value);
        }

        /// <summary>Loads a float literal through the constant pool.</summary>
        public SurtrCodeEmitter LoadFloat(double value) => LoadConstant(_module.ConstantFloat(value));

        /// <summary>Loads a boolean literal through the constant pool.</summary>
        /// <remarks>
        /// A pool entry rather than <c>PushI8</c> followed by <c>I2B</c>: booleans carry their own
        /// tag, there are exactly two of them, and the pool deduplicates - so the whole module pays
        /// at most two slots and every use is a single-byte load.
        /// </remarks>
        public SurtrCodeEmitter LoadBool(bool value) => LoadConstant(_module.ConstantBool(value));

        /// <summary>Loads a character literal through the constant pool.</summary>
        public SurtrCodeEmitter LoadChar(char value) => LoadConstant(_module.ConstantChar(value));

        /// <summary>Loads a string literal, interned into one object per distinct text at load time.</summary>
        public SurtrCodeEmitter LoadString(string text) => LoadConstant(_module.StringLiteral(text));

        /// <summary>Pushes the null reference.</summary>
        public SurtrCodeEmitter LoadNull() => PushNull();

        #endregion

        #region Host globals

        // A host global is reached by *name*: the module declares the import, and the name is
        // bound to the host's global table when the module loads. Nothing here needs a runtime,
        // which is the point - a module can be compiled before any runtime exists, and loaded into
        // more than one.

        /// <summary>Reads a host global this module imported, in the narrowest encoding that reaches it.</summary>
        public SurtrCodeEmitter LoadGlobal(SurtrNativeVariableToken import)
            => import.Index <= ushort.MaxValue ? Ldg(import) : LdgX(import);

        /// <summary>Declares an import for <paramref name="name"/> if needed, then reads it.</summary>
        public SurtrCodeEmitter LoadGlobal(string name)
            => LoadGlobal(_method.Module.NativeVariable(name));

        /// <summary>Reads a host global the host has already described, by its name.</summary>
        public SurtrCodeEmitter LoadGlobal(SurtrNativeGlobalVariable variable)
        {
            if (variable is null)
                throw new ArgumentNullException(nameof(variable));

            return LoadGlobal(variable.Name);
        }

        /// <summary>Writes a host global this module imported, in the narrowest encoding that reaches it.</summary>
        public SurtrCodeEmitter StoreGlobal(SurtrNativeVariableToken import)
            => import.Index <= ushort.MaxValue ? Stg(import) : StgX(import);

        /// <summary>Declares an import for <paramref name="name"/> if needed, then writes it.</summary>
        public SurtrCodeEmitter StoreGlobal(string name)
            => StoreGlobal(_method.Module.NativeVariable(name));

        /// <summary>Writes a host global the host has already described, by its name.</summary>
        /// <exception cref="ArgumentException">The variable was declared read-only to Surtr code.</exception>
        public SurtrCodeEmitter StoreGlobal(SurtrNativeGlobalVariable variable)
        {
            if (variable is null)
                throw new ArgumentNullException(nameof(variable));

            // The interpreter does not enforce this - Stg is a raw indexed store - so rejecting it
            // here is the only thing standing between a read-only host global and compiled code
            // that writes it anyway.
            if (variable.IsReadOnly)
                throw new ArgumentException($"Host global '{variable.Name}' is read-only to Surtr code.", nameof(variable));

            return StoreGlobal(variable.Name);
        }

        #endregion

        #region Fields

        /// <summary>Reads an instance field.</summary>
        public SurtrCodeEmitter LoadField(SurtrFieldToken field) => FieldGet(field);

        /// <summary>Writes an instance field.</summary>
        public SurtrCodeEmitter StoreField(SurtrFieldToken field) => FieldSet(field);

        /// <summary>Reads a static field or a module-level variable, in the narrowest encoding that reaches it.</summary>
        public SurtrCodeEmitter LoadStaticField(SurtrFieldToken field)
            => FieldIndex(field) <= ushort.MaxValue ? StaticFieldGet(field) : StaticFieldGetX(field);

        /// <summary>Writes a static field or a module-level variable, in the narrowest encoding that reaches it.</summary>
        public SurtrCodeEmitter StoreStaticField(SurtrFieldToken field)
            => FieldIndex(field) <= ushort.MaxValue ? StaticFieldSet(field) : StaticFieldSetX(field);

        /// <summary>Reads an instance field, naming it directly.</summary>
        public SurtrCodeEmitter LoadField(SurtrFieldInfo field) => FieldGet(_module.Field(field));

        /// <summary>Writes an instance field, naming it directly.</summary>
        public SurtrCodeEmitter StoreField(SurtrFieldInfo field) => FieldSet(_module.Field(field));

        /// <summary>Reads a static field or module-level variable, naming it directly.</summary>
        public SurtrCodeEmitter LoadStaticField(SurtrFieldInfo field) => LoadStaticField(_module.Field(field));

        /// <summary>Writes a static field or module-level variable, naming it directly.</summary>
        public SurtrCodeEmitter StoreStaticField(SurtrFieldInfo field) => StoreStaticField(_module.Field(field));

        #endregion

        #region Types, allocation and casts

        /// <summary>Allocates an uninitialised instance, in the narrowest encoding that reaches the type.</summary>
        public SurtrCodeEmitter NewObject(SurtrTypeToken type)
            => TypeIndex(type) <= ushort.MaxValue ? ObjNew(type) : ObjNewX(type);

        /// <summary>Allocates an uninitialised instance of the named class.</summary>
        public SurtrCodeEmitter NewObject(SurtrClassReference type) => NewObject(_module.Type(type));

        /// <summary>Casts the top value, in the narrowest encoding that reaches the type.</summary>
        public SurtrCodeEmitter CastTo(SurtrTypeToken type)
            => TypeIndex(type) <= ushort.MaxValue ? Cast(type) : CastX(type);

        /// <summary>Casts the top value to the named type.</summary>
        public SurtrCodeEmitter CastTo(SurtrClassReference type) => CastTo(_module.Type(type));

        /// <summary>Tests the top value's type, in the narrowest encoding that reaches it.</summary>
        public SurtrCodeEmitter TestInstanceOf(SurtrTypeToken type)
            => TypeIndex(type) <= ushort.MaxValue ? InstanceOf(type) : InstanceOfX(type);

        /// <summary>Tests whether the top value is an instance of the named type.</summary>
        public SurtrCodeEmitter TestInstanceOf(SurtrClassReference type) => TestInstanceOf(_module.Type(type));

        /// <summary>Allocates an array whose length comes from the stack.</summary>
        /// <param name="arrayType">The whole parameterised array type, for example <c>AI</c>.</param>
        public SurtrCodeEmitter NewArray(SurtrClassReference arrayType) => ArrNew(_module.Type(arrayType));

        /// <summary>Allocates an array of statically known length.</summary>
        public SurtrCodeEmitter NewArray(SurtrClassReference arrayType, int size) => ArrNewX(_module.Type(arrayType), size);

        /// <summary>Packs the top <paramref name="size"/> values into a new array.</summary>
        public SurtrCodeEmitter PackArray(SurtrClassReference arrayType, int size) => ArrPack(_module.Type(arrayType), size);

        /// <summary>Packs the top <paramref name="size"/> values into a tuple.</summary>
        public SurtrCodeEmitter PackTuple(SurtrClassReference tupleType, int size) => TupPack(_module.Type(tupleType), size);

        /// <summary>Expands a tuple into <paramref name="size"/> stack entries.</summary>
        public SurtrCodeEmitter UnpackTuple(int size) => TupUnpack(size);

        /// <summary>Allocates an empty dictionary.</summary>
        public SurtrCodeEmitter NewDictionary(SurtrClassReference dictionaryType) => DictNew(_module.Type(dictionaryType));

        /// <summary>Packs the top <paramref name="count"/> key/value pairs into a new dictionary.</summary>
        public SurtrCodeEmitter PackDictionary(SurtrClassReference dictionaryType, int count) => DictPack(_module.Type(dictionaryType), count);

        /// <summary>Collects a dictionary's keys into a new array of <paramref name="keyArrayType"/>.</summary>
        public SurtrCodeEmitter DictionaryKeys(SurtrClassReference keyArrayType) => DictKeys(_module.Type(keyArrayType));

        /// <summary>Collects a dictionary's values into a new array of <paramref name="valueArrayType"/>.</summary>
        public SurtrCodeEmitter DictionaryValues(SurtrClassReference valueArrayType) => DictValues(_module.Type(valueArrayType));

        #endregion

        #region Arithmetic by operand family

        /// <summary>Adds the top two operands.</summary>
        public SurtrCodeEmitter Add(SurtrValueTypeCode operandType)
            => operandType == SurtrValueTypeCode.Float ? FAdd() : RequireIntegral(operandType, "Add").Add();

        /// <summary>Subtracts the top operand from the one below it.</summary>
        public SurtrCodeEmitter Subtract(SurtrValueTypeCode operandType)
            => operandType == SurtrValueTypeCode.Float ? FSub() : RequireIntegral(operandType, "Sub").Sub();

        /// <summary>Multiplies the top two operands.</summary>
        public SurtrCodeEmitter Multiply(SurtrValueTypeCode operandType)
            => operandType == SurtrValueTypeCode.Float ? FMul() : RequireIntegral(operandType, "Mul").Mul();

        /// <summary>Divides the deeper operand by the top one.</summary>
        public SurtrCodeEmitter Divide(SurtrValueTypeCode operandType)
            => operandType == SurtrValueTypeCode.Float ? FDiv() : RequireIntegral(operandType, "Div").Div();

        /// <summary>Takes the remainder of the deeper operand divided by the top one.</summary>
        public SurtrCodeEmitter Remainder(SurtrValueTypeCode operandType)
            => operandType == SurtrValueTypeCode.Float ? FMod() : RequireIntegral(operandType, "Mod").Mod();

        /// <summary>Raises the deeper operand to the power of the top one.</summary>
        public SurtrCodeEmitter Power(SurtrValueTypeCode operandType)
            => operandType == SurtrValueTypeCode.Float ? FPow() : RequireIntegral(operandType, "Pow").Pow();

        /// <summary>Negates the top operand.</summary>
        public SurtrCodeEmitter Negate(SurtrValueTypeCode operandType)
            => operandType == SurtrValueTypeCode.Float ? FNeg() : RequireIntegral(operandType, "Neg").Neg();

        private SurtrCodeEmitter RequireIntegral(SurtrValueTypeCode operandType, string operation)
        {
            // Integers, booleans and characters share one representation, so the untagged opcodes
            // cover all three. Anything else has no arithmetic form at all.
            if (operandType != SurtrValueTypeCode.Integer &&
                operandType != SurtrValueTypeCode.Boolean &&
                operandType != SurtrValueTypeCode.Character)
            {
                throw new ArgumentException($"{operation} has no form for operands of type {operandType}.", nameof(operandType));
            }

            return this;
        }

        #endregion

        #region Comparison by operand family

        /// <summary>Compares the top two operands and pushes the boolean result.</summary>
        /// <exception cref="ArgumentException">The comparison has no form for this operand family - ordering on references or strings, say.</exception>
        public SurtrCodeEmitter Compare(SurtrComparison comparison, SurtrValueTypeCode operandType)
            => Simple(ComparisonOpCode(comparison, operandType), 2, 1);

        /// <summary>Compares the top two operands and branches, without the boolean reaching the stack.</summary>
        public SurtrCodeEmitter JumpIfCompare(
            SurtrComparison comparison,
            SurtrValueTypeCode operandType,
            SurtrLabel target,
            SurtrJumpWidth width = SurtrJumpWidth.Auto)
        {
            var (shortOp, wideOp) = ComparisonBranchOpCodes(comparison, operandType);
            return Branch(shortOp, wideOp, target, 2, width, false);
        }

        private static OpCode ComparisonOpCode(SurtrComparison comparison, SurtrValueTypeCode operandType)
        {
            if (operandType == SurtrValueTypeCode.Float)
            {
                return comparison switch
                {
                    SurtrComparison.Equal => OpCode.FEQ,
                    SurtrComparison.NotEqual => OpCode.FNE,
                    SurtrComparison.Greater => OpCode.FGT,
                    SurtrComparison.GreaterOrEqual => OpCode.FGE,
                    SurtrComparison.Less => OpCode.FLT,
                    SurtrComparison.LessOrEqual => OpCode.FLE,
                    _ => throw UnknownComparison(comparison),
                };
            }

            if (operandType == SurtrValueTypeCode.String)
            {
                return comparison switch
                {
                    SurtrComparison.Equal => OpCode.StrEQ,
                    SurtrComparison.NotEqual => OpCode.StrNE,
                    _ => throw NoOrdering(comparison, operandType),
                };
            }

            if (operandType.IsReferenceType)
            {
                return comparison switch
                {
                    SurtrComparison.Equal => OpCode.REQ,
                    SurtrComparison.NotEqual => OpCode.RNE,
                    _ => throw NoOrdering(comparison, operandType),
                };
            }

            if (!operandType.IsPrimitive)
                throw new ArgumentException($"{operandType} is not a comparable operand family.", nameof(operandType));

            return comparison switch
            {
                SurtrComparison.Equal => OpCode.EQ,
                SurtrComparison.NotEqual => OpCode.NE,
                SurtrComparison.Greater => OpCode.GT,
                SurtrComparison.GreaterOrEqual => OpCode.GE,
                SurtrComparison.Less => OpCode.LT,
                SurtrComparison.LessOrEqual => OpCode.LE,
                _ => throw UnknownComparison(comparison),
            };
        }

        private static (OpCode Short, OpCode Wide) ComparisonBranchOpCodes(SurtrComparison comparison, SurtrValueTypeCode operandType)
        {
            if (operandType == SurtrValueTypeCode.Float)
            {
                return comparison switch
                {
                    SurtrComparison.Equal => (OpCode.JPFEQ, OpCode.JPFEQX),
                    SurtrComparison.NotEqual => (OpCode.JPFNE, OpCode.JPFNEX),
                    SurtrComparison.Greater => (OpCode.JPFGT, OpCode.JPFGTX),
                    SurtrComparison.GreaterOrEqual => (OpCode.JPFGE, OpCode.JPFGEX),
                    SurtrComparison.Less => (OpCode.JPFLT, OpCode.JPFLTX),
                    SurtrComparison.LessOrEqual => (OpCode.JPFLE, OpCode.JPFLEX),
                    _ => throw UnknownComparison(comparison),
                };
            }

            if (operandType == SurtrValueTypeCode.String)
            {
                return comparison switch
                {
                    SurtrComparison.Equal => (OpCode.JPStrEQ, OpCode.JPStrEQX),
                    SurtrComparison.NotEqual => (OpCode.JPStrNE, OpCode.JPStrNEX),
                    _ => throw NoOrdering(comparison, operandType),
                };
            }

            if (operandType.IsReferenceType)
            {
                return comparison switch
                {
                    SurtrComparison.Equal => (OpCode.JPREQ, OpCode.JPREQX),
                    SurtrComparison.NotEqual => (OpCode.JPRNE, OpCode.JPRNEX),
                    _ => throw NoOrdering(comparison, operandType),
                };
            }

            if (!operandType.IsPrimitive)
                throw new ArgumentException($"{operandType} is not a comparable operand family.", nameof(operandType));

            return comparison switch
            {
                SurtrComparison.Equal => (OpCode.JPEQ, OpCode.JPEQX),
                SurtrComparison.NotEqual => (OpCode.JPNE, OpCode.JPNEX),
                SurtrComparison.Greater => (OpCode.JPGT, OpCode.JPGTX),
                SurtrComparison.GreaterOrEqual => (OpCode.JPGE, OpCode.JPGEX),
                SurtrComparison.Less => (OpCode.JPLT, OpCode.JPLTX),
                SurtrComparison.LessOrEqual => (OpCode.JPLE, OpCode.JPLEX),
                _ => throw UnknownComparison(comparison),
            };
        }

        private static ArgumentException NoOrdering(SurtrComparison comparison, SurtrValueTypeCode operandType)
            => new ArgumentException($"{operandType} operands only support equality; {comparison} has no opcode.", nameof(comparison));

        private static ArgumentException UnknownComparison(SurtrComparison comparison)
            => new ArgumentException($"Unknown comparison {comparison}.", nameof(comparison));

        #endregion

        #region Conversions and boxing

        /// <summary>Converts the top primitive from one family to another, routing through integer when needed.</summary>
        /// <remarks>
        /// Only <c>int</c> pairs directly with every other family, so the mixed cases become two
        /// instructions - <c>char</c> to <c>float</c> is <c>C2I</c> then <c>I2F</c>. Converting a
        /// family to itself emits nothing.
        /// </remarks>
        public SurtrCodeEmitter Convert(SurtrValueTypeCode from, SurtrValueTypeCode to)
        {
            if (from == to)
                return this;

            if (!from.IsPrimitive || !to.IsPrimitive)
                throw new ArgumentException($"There is no conversion between {from} and {to}; only primitives convert.", nameof(to));

            switch (from)
            {
                case SurtrValueTypeCode.Character: C2I(); break;
                case SurtrValueTypeCode.Boolean: B2I(); break;
                case SurtrValueTypeCode.Float: F2I(); break;
            }

            return to switch
            {
                SurtrValueTypeCode.Integer => this,
                SurtrValueTypeCode.Float => I2F(),
                SurtrValueTypeCode.Character => I2C(),
                SurtrValueTypeCode.Boolean => I2B(),
                _ => throw new ArgumentException($"{to} is not a primitive family.", nameof(to)),
            };
        }

        /// <summary>
        /// Boxes the top value if its family is a primitive, and emits nothing otherwise.
        /// </summary>
        /// <remarks>
        /// The shape the erasure rule needs: a value flowing into an erased slot has to be a
        /// reference, and a reference already is one. Callers can therefore box unconditionally on
        /// that path without first testing what they have.
        /// </remarks>
        public SurtrCodeEmitter Box(SurtrValueTypeCode valueType) => valueType switch
        {
            SurtrValueTypeCode.Integer => BoxInt(),
            SurtrValueTypeCode.Float => BoxFloat(),
            SurtrValueTypeCode.Boolean => BoxBool(),
            SurtrValueTypeCode.Character => BoxChar(),
            _ => this,
        };

        #endregion

        #region Branching

        /// <summary>Branches unconditionally.</summary>
        public SurtrCodeEmitter Jump(SurtrLabel target, SurtrJumpWidth width = SurtrJumpWidth.Auto)
            => Branch(OpCode.JP, OpCode.JPX, target, 0, width, true);

        /// <summary>Pops a condition and branches when it is false.</summary>
        public SurtrCodeEmitter JumpIfFalse(SurtrLabel target, SurtrJumpWidth width = SurtrJumpWidth.Auto)
            => Branch(OpCode.JPZ, OpCode.JPZX, target, 1, width, false);

        /// <summary>Pops a condition and branches when it is true.</summary>
        public SurtrCodeEmitter JumpIfTrue(SurtrLabel target, SurtrJumpWidth width = SurtrJumpWidth.Auto)
            => Branch(OpCode.JPNZ, OpCode.JPNZX, target, 1, width, false);

        /// <summary>Pops a value and branches when it is the null reference.</summary>
        public SurtrCodeEmitter JumpIfNull(SurtrLabel target, SurtrJumpWidth width = SurtrJumpWidth.Auto)
            => Branch(OpCode.JPN, OpCode.JPNX, target, 1, width, false);

        /// <summary>Pops a value and branches when it is a non-null reference.</summary>
        public SurtrCodeEmitter JumpIfNotNull(SurtrLabel target, SurtrJumpWidth width = SurtrJumpWidth.Auto)
            => Branch(OpCode.JPNN, OpCode.JPNNX, target, 1, width, false);

        /// <summary>Pops a value and branches when it is an instance of <paramref name="type"/>.</summary>
        public SurtrCodeEmitter JumpIfInstanceOf(SurtrTypeToken type, SurtrLabel target, SurtrJumpWidth width = SurtrJumpWidth.Auto)
            => BranchInstanceOf(type, target, width);

        /// <summary>Pops a value and branches when it is an instance of the named type.</summary>
        public SurtrCodeEmitter JumpIfInstanceOf(SurtrClassReference type, SurtrLabel target, SurtrJumpWidth width = SurtrJumpWidth.Auto)
            => BranchInstanceOf(_module.Type(type), target, width);

        /// <summary>
        /// Emits whichever switch form suits the case values: a dense jump table when they are
        /// close together, a binary-searched key table when they are not.
        /// </summary>
        /// <param name="cases">The arms, in any order and with any keys, as long as no key repeats.</param>
        /// <param name="defaultTarget">Where a value matching no arm goes.</param>
        /// <param name="densityFactor">
        /// How much padding a dense table may carry: the range is accepted when it is no more than
        /// this many times the number of arms. Two means a table at least half full.
        /// </param>
        public SurtrCodeEmitter SwitchOn(IReadOnlyList<SurtrSwitchCase> cases, SurtrLabel defaultTarget, int densityFactor = 2)
        {
            if (cases is null)
                throw new ArgumentNullException(nameof(cases));

            if (cases.Count == 0)
                return Pop().Jump(defaultTarget);

            var sorted = new SurtrSwitchCase[cases.Count];
            for (int i = 0; i < cases.Count; i++)
                sorted[i] = cases[i];

            Array.Sort(sorted, static (left, right) => left.Key.CompareTo(right.Key));

            for (int i = 1; i < sorted.Length; i++)
            {
                if (sorted[i].Key == sorted[i - 1].Key)
                    throw new ArgumentException($"Switch case {sorted[i].Key} appears more than once.", nameof(cases));
            }

            long low = sorted[0].Key;
            long high = sorted[sorted.Length - 1].Key;
            long range = high - low + 1;

            if (range <= (long)sorted.Length * densityFactor && range <= int.MaxValue)
            {
                var table = new SurtrLabel[(int)range];
                for (int i = 0; i < table.Length; i++)
                    table[i] = defaultTarget;

                for (int i = 0; i < sorted.Length; i++)
                    table[sorted[i].Key - low] = sorted[i].Label;

                return Switch((int)low, table, defaultTarget);
            }

            return SwitchLookup(sorted, defaultTarget);
        }

        #endregion

        #region Calls

        /// <summary>
        /// Calls a method declared in this module, picking the dispatch opcode from the callee's
        /// own metadata and deriving the argument and result counts from its signature.
        /// </summary>
        /// <param name="callee">The target, which must already have been declared on this builder.</param>
        /// <param name="discardResult">
        /// Whether to ask for no result even though the callee produces one. The frame protocol
        /// drops it on return, so this saves the <c>Pop</c> a statement-position call would need.
        /// </param>
        public SurtrCodeEmitter Call(SurtrMethodBuilder callee, bool discardResult = false)
        {
            if (callee is null)
                throw new ArgumentNullException(nameof(callee));

            int arguments = callee.ArgumentSlotCount;
            int results = discardResult || callee.ReturnTypeReference.TypeCode.IsVoid ? 0 : 1;

            // A method builder always carries a body, and an interface member never can, so this is
            // the one call form that needs no contract test.
            return EmitCall(callee.Token, callee.IsModuleLevel, callee.IsStatic, false, callee.Dispatch, arguments, results);
        }

        /// <summary>
        /// Calls an already-built method, picking the dispatch opcode from its metadata.
        /// </summary>
        /// <remarks>
        /// The one case this cannot infer is an interface call: a method's declaring type is carried
        /// as a descriptor, and a descriptor does not say whether it names a class or a contract
        /// until the module is loaded. Methods declared through <see cref="SurtrInterfaceBuilder"/>
        /// are recognised because the builder remembers them; anything else needs
        /// <see cref="CallInterface"/>.
        /// </remarks>
        public SurtrCodeEmitter Call(SurtrMethodInfo callee, bool discardResult = false)
        {
            if (callee is null)
                throw new ArgumentNullException(nameof(callee));

            bool moduleLevel = callee.DeclaringType is null;
            bool contract = _module.IsInterfaceMethod(callee);

            int arguments = callee.ParameterCount + (moduleLevel || callee.IsStatic ? 0 : 1);
            int results = discardResult || callee.ReturnType.Reference.TypeCode.IsVoid ? 0 : 1;

            return EmitCall(_module.Method(callee), moduleLevel, callee.IsStatic, contract, callee.Dispatch, arguments, results);
        }

        /// <summary>Calls a method through the receiver's virtual method table, whatever the callee declares.</summary>
        public SurtrCodeEmitter CallVirtual(SurtrMethodInfo callee, bool discardResult = false)
            => InvokeVirtual(_module.Method(RequireInstance(callee)), callee.ParameterCount + 1, ResultsFor(callee, discardResult));

        /// <summary>Calls a method without virtual dispatch: constructors, and explicit base calls.</summary>
        public SurtrCodeEmitter CallSpecial(SurtrMethodInfo callee, bool discardResult = false)
            => InvokeSpecial(_module.Method(RequireInstance(callee)), callee.ParameterCount + 1, ResultsFor(callee, discardResult));

        /// <summary>Calls a method through an interface contract.</summary>
        public SurtrCodeEmitter CallInterface(SurtrMethodInfo callee, bool discardResult = false)
            => InvokeInterface(_module.Method(RequireInstance(callee)), callee.ParameterCount + 1, ResultsFor(callee, discardResult));

        /// <summary>Calls a module-level function in another, already-built module.</summary>
        public SurtrCodeEmitter CallExternal(Runtime.Classes.SurtrModule target, SurtrMethodInfo callee, bool discardResult = false)
        {
            if (callee is null)
                throw new ArgumentNullException(nameof(callee));

            var token = _module.ExternalMethod(target, callee);
            int arguments = callee.ParameterCount + (callee.DeclaringType is null || callee.IsStatic ? 0 : 1);
            int results = ResultsFor(callee, discardResult);

            return token.ModuleIndex <= ushort.MaxValue && token.FunctionIndex <= ushort.MaxValue
                ? CallModule(token, arguments, results)
                : CallModuleX(token, arguments, results);
        }

        /// <summary>Calls a host global this module imported.</summary>
        public SurtrCodeEmitter CallGlobal(SurtrNativeFunctionToken import, int argumentCount, int resultCount)
            => import.Index <= ushort.MaxValue
                ? CallGlobalNative(import, argumentCount, resultCount)
                : CallGlobalNativeX(import, argumentCount, resultCount);

        /// <summary>Declares an import for <paramref name="name"/> if needed, then calls it.</summary>
        public SurtrCodeEmitter CallGlobal(string name, int argumentCount, int resultCount)
            => CallGlobal(_method.Module.NativeFunction(name), argumentCount, resultCount);

        /// <summary>
        /// Calls a host global the host has already described, taking its arity and return from
        /// the signature it was registered with.
        /// </summary>
        public SurtrCodeEmitter CallGlobal(SurtrNativeGlobalFunction function, bool discardResult = false)
        {
            if (function is null)
                throw new ArgumentNullException(nameof(function));

            int results = discardResult || function.ReturnType.Reference.TypeCode.IsVoid ? 0 : 1;

            return CallGlobal(function.Name, function.ParameterCount, results);
        }

        /// <summary>Calls the closure sitting below <paramref name="argumentCount"/> arguments on the stack.</summary>
        public SurtrCodeEmitter CallClosure(int argumentCount, bool hasResult)
            => InvokeClosure(argumentCount, hasResult ? 1 : 0);

        /// <summary>Builds a closure over <paramref name="function"/>, in the narrowest encoding that reaches it.</summary>
        public SurtrCodeEmitter NewClosureFor(SurtrMethodToken function, int upValueCount)
            => MethodIndex(function) <= ushort.MaxValue ? NewClosure(function, upValueCount) : NewClosureX(function, upValueCount);

        /// <summary>Builds a closure over a method declared on this builder.</summary>
        public SurtrCodeEmitter NewClosureFor(SurtrMethodBuilder function, int upValueCount)
            => NewClosureFor(function is null ? throw new ArgumentNullException(nameof(function)) : function.Token, upValueCount);

        private SurtrCodeEmitter EmitCall(
            SurtrMethodToken token,
            bool moduleLevel,
            bool isStatic,
            bool contract,
            SurtrMethodDispatch dispatch,
            int arguments,
            int results)
        {
            bool wide = MethodIndex(token) > ushort.MaxValue;

            if (moduleLevel)
            {
                return wide
                    ? CallLocalModuleX(token, arguments, results)
                    : CallLocalModule(token, arguments, results);
            }

            if (contract)
                return InvokeInterface(token, arguments, results);

            if (isStatic)
            {
                return wide
                    ? InvokeStaticX(token, arguments, results)
                    : InvokeStatic(token, arguments, results);
            }

            return dispatch == SurtrMethodDispatch.Direct
                ? InvokeSpecial(token, arguments, results)
                : InvokeVirtual(token, arguments, results);
        }

        private static SurtrMethodInfo RequireInstance(SurtrMethodInfo callee)
        {
            if (callee is null)
                throw new ArgumentNullException(nameof(callee));

            if (callee.IsStatic)
                throw new ArgumentException($"'{callee.Name}' is static and has no receiver to dispatch on.", nameof(callee));

            return callee;
        }

        private static int ResultsFor(SurtrMethodInfo callee, bool discardResult)
            => discardResult || callee.ReturnType.Reference.TypeCode.IsVoid ? 0 : 1;

        #endregion
    }
}
