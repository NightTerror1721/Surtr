#nullable enable

namespace Surtr.Bytecode
{
    /// <summary>
    /// The complete instruction set of the Surtr virtual machine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Surtr is a stack machine: instructions take their operands from the evaluation stack and
    /// leave their results there. Anything that cannot come from the stack - pool indices, jump
    /// offsets, argument counts - is encoded inline after the opcode byte as an *immediate*.
    /// </para>
    /// <para>
    /// Every member below documents three things:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><b>Encoding</b> - the byte layout, written as <c>opcode(1) name(width)</c>, with the total instruction length. Immediates are little-endian.</description></item>
    ///   <item><description><b>Stack</b> - the transition, written <c>before -&gt; after</c>, where <c>...</c> is the untouched remainder of the stack and the rightmost entry is the top.</description></item>
    ///   <item><description><b>Notes</b> - only where the behaviour is not obvious from the name.</description></item>
    /// </list>
    /// <para>
    /// Naming conventions that run through the whole set:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>An <c>F</c> prefix means the operands are floats. Untagged opcodes work on integers, bools and chars, which all share integer representation.</description></item>
    ///   <item><description>An <c>R</c> prefix means the operands are compared as references - identity, not value.</description></item>
    ///   <item><description>An <c>X</c> suffix widens an immediate to 4 bytes, for pools or jump distances that outgrow the 2-byte form.</description></item>
    ///   <item><description>An <c>S</c> suffix narrows an immediate to 1 byte, for the common small-index case.</description></item>
    ///   <item><description>A trailing digit is a dedicated opcode for that fixed index, so the common case costs no immediate at all.</description></item>
    /// </list>
    /// <para>
    /// Pool indices refer to the tables on the declaring module's chunk: constants, types, fields,
    /// methods and functions each have their own. Since the enum is the on-disk encoding, the
    /// numeric value of every member is part of the bytecode format - inserting a member in the
    /// middle renumbers everything after it and invalidates already-compiled bytecode.
    /// </para>
    /// </remarks>
    public enum OpCode : byte
    {
        /// <summary>Does nothing.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ...</c><br/>
        /// Notes: useful as a patch target when the emitter has to overwrite an instruction in place.
        /// </remarks>
        Nop = 0x00,


        #region Stack Operations
        /// <summary>Duplicates the value on top of the stack.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ..., value, value</c>
        /// </remarks>
        Dup,

        /// <summary>Duplicates the top two values, preserving their order.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a, b, a, b</c>
        /// </remarks>
        Dup2,

        /// <summary>Exchanges the top two values.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., b, a</c>
        /// </remarks>
        Swap,

        /// <summary>Exchanges the top two pairs of values, keeping each pair's internal order.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b, c, d -&gt; ..., c, d, a, b</c>
        /// </remarks>
        Swap2,

        /// <summary>Pushes the null reference.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., null</c>
        /// </remarks>
        PushNull,

        /// <summary>Pushes a signed 8-bit integer literal, sign-extended to a full integer value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) value(1)</c> - 2 bytes.<br/>
        /// Stack: <c>... -&gt; ..., int</c><br/>
        /// Notes: the narrowest way to materialise a small literal without touching the constant pool.
        /// </remarks>
        PushI8,

        /// <summary>Pushes a signed 16-bit integer literal, sign-extended to a full integer value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) value(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., int</c>
        /// </remarks>
        PushI16,

        /// <summary>Pushes a signed 32-bit integer literal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) value(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., int</c>
        /// </remarks>
        PushI32,

        /// <summary>Discards the value on top of the stack.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c><br/>
        /// Notes: how a call's unused return value is dropped in statement position.
        /// </remarks>
        Pop,
        #endregion


        #region Load / Store Operations
        /// <summary>Loads the constant at <c>constIdx</c> from the chunk's constant pool.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) constIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc,

        /// <summary>Loads constant 0.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc0,

        /// <summary>Loads constant 1.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc1,

        /// <summary>Loads constant 2.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc2,

        /// <summary>Loads constant 3.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc3,

        /// <summary>Loads constant 4.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc4,

        /// <summary>Loads constant 5.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc5,

        /// <summary>Loads constant 6.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc6,

        /// <summary>Loads constant 7.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc7,

        /// <summary>Loads constant 8.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc8,

        /// <summary>Loads constant 9.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc9,

        /// <summary>Loads a constant using a 4-byte index, for pools larger than 65536 entries.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) constIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        LdcX,

        /// <summary>Loads a constant using a 1-byte index, for the first 256 pool entries.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) constIdx(1)</c> - 2 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        LdcS,

        /// <summary>Loads the local variable at <c>localIdx</c> from the current frame.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) localIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldl,

        /// <summary>Loads local 0.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c><br/>
        /// Notes: on an instance method this is the receiver.
        /// </remarks>
        Ldl0,

        /// <summary>Loads local 1.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldl1,

        /// <summary>Loads local 2.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldl2,

        /// <summary>Loads local 3.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldl3,

        /// <summary>Loads local 4.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldl4,

        /// <summary>Loads local 5.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldl5,

        /// <summary>Loads a local using a 1-byte index, for the first 256 slots of the frame.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) localIdx(1)</c> - 2 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        LdlS,

        /// <summary>Reads a host-defined global variable.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) globalIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c><br/>
        /// Notes: indexes the native global table, the only truly global namespace in Surtr.
        /// A direct indexed load off that table's value storage - the host reaches the same slot
        /// through an accessor, but bytecode does not.
        /// </remarks>
        Ldg,

        /// <summary>Reads a host-defined global variable using a 4-byte index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) globalIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        LdgX,

        /// <summary>Pops a value and stores it into the local at <c>localIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) localIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl,

        /// <summary>Pops a value into local 0.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl0,

        /// <summary>Pops a value into local 1.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl1,

        /// <summary>Pops a value into local 2.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl2,

        /// <summary>Pops a value into local 3.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl3,

        /// <summary>Pops a value into local 4.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl4,

        /// <summary>Pops a value into local 5.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl5,

        /// <summary>Pops a value into a local using a 1-byte index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) localIdx(1)</c> - 2 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        StlS,

        /// <summary>Pops a value and writes it into a host-defined global variable.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) globalIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c><br/>
        /// Notes: the compiler must reject this against a global the host registered as read-only.
        /// </remarks>
        Stg,

        /// <summary>Pops a value into a host-defined global variable using a 4-byte index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) globalIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        StgX,
        #endregion


        #region Arithmetic Operations
        /// <summary>Integer addition.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a + b</c>
        /// </remarks>
        Add,

        /// <summary>Floating-point addition.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a + b</c>
        /// </remarks>
        FAdd,

        /// <summary>Integer subtraction.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a - b</c><br/>
        /// Notes: the deeper operand is the minuend, so the result is <c>a - b</c>, not <c>b - a</c>.
        /// </remarks>
        Sub,

        /// <summary>Floating-point subtraction.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a - b</c>
        /// </remarks>
        FSub,

        /// <summary>Integer multiplication.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a * b</c>
        /// </remarks>
        Mul,

        /// <summary>Floating-point multiplication.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a * b</c>
        /// </remarks>
        FMul,

        /// <summary>Integer division.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a / b</c><br/>
        /// Notes: division by zero has no defined result yet and needs a trap decision.
        /// </remarks>
        Div,

        /// <summary>Floating-point division.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a / b</c><br/>
        /// Notes: division by zero follows IEEE 754 and yields an infinity or NaN rather than trapping.
        /// </remarks>
        FDiv,

        /// <summary>Integer remainder.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a % b</c><br/>
        /// Notes: as with <see cref="Div"/>, a zero divisor still needs a defined behaviour.
        /// </remarks>
        Mod,

        /// <summary>Floating-point remainder.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a % b</c>
        /// </remarks>
        FMod,

        /// <summary>Integer exponentiation.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a ** b</c><br/>
        /// Notes: raises the deeper operand to the power of the top one. A negative exponent has
        /// no integer result and needs a defined behaviour.
        /// </remarks>
        Pow,

        /// <summary>Floating-point exponentiation.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a ** b</c>
        /// </remarks>
        FPow,

        /// <summary>Integer negation.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., -a</c>
        /// </remarks>
        Neg,

        /// <summary>Floating-point negation.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., -a</c><br/>
        /// Notes: flips the sign bit, so it also turns zero into negative zero.
        /// </remarks>
        FNeg,

        /// <summary>Logical negation of a boolean.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., !a</c><br/>
        /// Notes: this is the boolean operator. The bitwise complement is <see cref="Not"/>.
        /// </remarks>
        Inv,
        #endregion


        #region Comparison Operations
        /// <summary>Integer equality.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c><br/>
        /// Notes: also covers bools and chars, which share the integer representation.
        /// </remarks>
        EQ,

        /// <summary>Floating-point equality.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c><br/>
        /// Notes: IEEE 754 semantics, so NaN compares unequal to everything including itself.
        /// </remarks>
        FEQ,

        /// <summary>Reference identity.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c><br/>
        /// Notes: compares handles, not contents - two equal strings in different objects are not identical.
        /// </remarks>
        REQ,

        /// <summary>Integer inequality.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c>
        /// </remarks>
        NE,

        /// <summary>Floating-point inequality.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c><br/>
        /// Notes: NaN compares unequal to everything, so this yields true when either side is NaN.
        /// </remarks>
        FNE,

        /// <summary>Reference non-identity.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c>
        /// </remarks>
        RNE,

        /// <summary>Integer greater-than.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &gt; b</c>
        /// </remarks>
        GT,

        /// <summary>Floating-point greater-than.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &gt; b</c><br/>
        /// Notes: false whenever either operand is NaN.
        /// </remarks>
        FGT,

        /// <summary>Integer greater-than-or-equal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &gt;= b</c>
        /// </remarks>
        GE,

        /// <summary>Floating-point greater-than-or-equal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &gt;= b</c><br/>
        /// Notes: false whenever either operand is NaN.
        /// </remarks>
        FGE,

        /// <summary>Integer less-than.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &lt; b</c>
        /// </remarks>
        LT,

        /// <summary>Floating-point less-than.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &lt; b</c><br/>
        /// Notes: false whenever either operand is NaN.
        /// </remarks>
        FLT,

        /// <summary>Integer less-than-or-equal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &lt;= b</c>
        /// </remarks>
        LE,

        /// <summary>Floating-point less-than-or-equal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &lt;= b</c><br/>
        /// Notes: false whenever either operand is NaN.
        /// </remarks>
        FLE,

        /// <summary>Tests whether the top value is the null reference.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c>
        /// </remarks>
        IsNull,

        /// <summary>Tests whether the top value is a non-null reference.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c>
        /// </remarks>
        IsNotNull,

        /// <summary>Tests whether the top value is an instance of the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c><br/>
        /// Notes: the type is an immediate, not a stack operand. Resolves through the class's
        /// ancestor chain for classes and its interface table for interfaces.
        /// </remarks>
        InstanceOf,

        /// <summary>Tests instance-of using a 4-byte type index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c>
        /// </remarks>
        InstanceOfX,
        #endregion


        #region Bitwise Operations
        /// <summary>Bitwise AND.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &amp; b</c>
        /// </remarks>
        And,

        /// <summary>Bitwise OR.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a | b</c>
        /// </remarks>
        Or,

        /// <summary>Bitwise exclusive OR.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a ^ b</c>
        /// </remarks>
        Xor,

        /// <summary>Bitwise complement.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., ~a</c><br/>
        /// Notes: this is the bitwise operator. The boolean negation is <see cref="Inv"/>.
        /// </remarks>
        Not,

        /// <summary>Left shift.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &lt;&lt; b</c><br/>
        /// Notes: shifts the deeper operand left by the top one. Shift counts at or above the
        /// operand width still need a defined behaviour.
        /// </remarks>
        Shl,

        /// <summary>Logical right shift, filling with zeroes.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &gt;&gt;&gt; b</c><br/>
        /// Notes: does not preserve the sign - a negative value becomes a large positive one.
        /// The sign-preserving form is <see cref="Sar"/>.
        /// </remarks>
        Shr,

        /// <summary>Arithmetic right shift, replicating the sign bit.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &gt;&gt; b</c><br/>
        /// Notes: keeps the sign, so a negative value stays negative. The zero-filling form is
        /// <see cref="Shr"/>.
        /// </remarks>
        Sar,
        #endregion


        #region Conversion Operations
        /// <summary>Widens an integer to a float.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., float</c>
        /// </remarks>
        I2F,

        /// <summary>Narrows a float to an integer.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., int</c><br/>
        /// Notes: lossy. The rounding mode, and what happens for NaN or out-of-range values,
        /// still need to be pinned down.
        /// </remarks>
        F2I,

        /// <summary>Boxes an integer into a heap object.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., ref</c><br/>
        /// Notes: allocates, so the result is a collectable reference rather than an inline value.
        /// </remarks>
        BoxInt,

        /// <summary>Boxes a float into a heap object.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., ref</c>
        /// </remarks>
        BoxFloat,

        /// <summary>Boxes a boolean into a heap object.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., ref</c>
        /// </remarks>
        BoxBool,

        /// <summary>Unwraps a boxed value back to its inline representation.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., ref -&gt; ..., value</c><br/>
        /// Notes: recovers whichever primitive was boxed - the tag on the boxed value says which,
        /// so no per-type opcode is needed on the way back.
        /// </remarks>
        Unbox,

        /// <summary>Casts the top value to the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a -&gt; ..., a</c><br/>
        /// Notes: the type is an immediate, not a stack operand. This is a checked reference cast:
        /// the value is unchanged on success, and a failure needs a defined trap.
        /// </remarks>
        Cast,

        /// <summary>Casts using a 4-byte type index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a -&gt; ..., a</c>
        /// </remarks>
        CastX,
        #endregion


        #region String Operations
        /// <summary>Pushes the length of a string in characters.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., str -&gt; ..., int</c>
        /// </remarks>
        StrLen,

        /// <summary>Concatenates two strings.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a + b</c><br/>
        /// Notes: the deeper operand comes first in the result. Allocates a new string.
        /// </remarks>
        StrCat,

        /// <summary>Reads the character at an index of a string.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., str, index -&gt; ..., char</c><br/>
        /// Notes: an out-of-range index needs a defined trap.
        /// </remarks>
        StrGet,
        #endregion


        #region Array Operations
        /// <summary>Allocates an array whose length is taken from the stack.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., size -&gt; ..., array</c><br/>
        /// Notes: carries no element type, unlike <see cref="ObjNew"/>. Whether the runtime can
        /// build the array's reference map without one is still open.
        /// </remarks>
        ArrNew,

        /// <summary>Allocates an array whose length is an immediate.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) size(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., array</c><br/>
        /// Notes: not a widened <see cref="ArrNew"/> but a different addressing mode - the length
        /// moves from the stack into the instruction, for arrays of statically known size.
        /// </remarks>
        ArrNewX,

        /// <summary>Pushes an array's length.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr -&gt; ..., int</c>
        /// </remarks>
        ArrLen,

        /// <summary>Reads an array element.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, index -&gt; ..., value</c><br/>
        /// Notes: an out-of-range index needs a defined trap.
        /// </remarks>
        ArrGet,

        /// <summary>Writes an array element.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, index, value -&gt; ...</c><br/>
        /// Notes: consumes all three operands and pushes nothing.
        /// </remarks>
        ArrSet,

        /// <summary>Tests whether an array contains a value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, value -&gt; ..., bool</c><br/>
        /// Notes: linear scan, so cost grows with the array.
        /// </remarks>
        ArrIn,

        /// <summary>Tests whether an array does not contain a value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, value -&gt; ..., bool</c><br/>
        /// Notes: exists as its own opcode so the negated form costs no extra instruction.
        /// </remarks>
        ArrNIn,
        #endregion


        #region Tuple Operations
        /// <summary>Pops <c>size</c> values and packs them into a tuple.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) size(1)</c> - 2 bytes.<br/>
        /// Stack: <c>..., v1, ..., vN -&gt; ..., tuple</c><br/>
        /// Notes: the deepest popped value becomes element 0. Caps arity at 255.
        /// </remarks>
        TupPack,

        /// <summary>Expands a tuple into <c>size</c> separate stack entries.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) size(1)</c> - 2 bytes.<br/>
        /// Stack: <c>..., tuple -&gt; ..., v1, ..., vN</c><br/>
        /// Notes: element 0 ends up deepest, so packing and unpacking round-trip.
        /// </remarks>
        TupUnpack,

        /// <summary>Pushes a tuple's arity.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., tup -&gt; ..., int</c>
        /// </remarks>
        TupLen,

        /// <summary>Reads a tuple element.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., tup, index -&gt; ..., value</c><br/>
        /// Notes: there is no matching setter - tuples are immutable once packed.
        /// </remarks>
        TupGet,
        #endregion


        #region Dictionary Operations
        /// <summary>Allocates an empty dictionary.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., dict</c>
        /// </remarks>
        DictNew,

        /// <summary>Pushes the number of entries in a dictionary.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., dict -&gt; ..., int</c>
        /// </remarks>
        DictLen,

        /// <summary>Reads the value stored under a key.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., dict, key -&gt; ..., value</c><br/>
        /// Notes: a missing key needs a defined behaviour - trap, or push null.
        /// </remarks>
        DictGet,

        /// <summary>Stores a value under a key, inserting or replacing.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., dict, key, value -&gt; ...</c>
        /// </remarks>
        DictSet,

        /// <summary>Tests whether a dictionary holds a key.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., dict, key -&gt; ..., bool</c>
        /// </remarks>
        DictIn,

        /// <summary>Tests whether a dictionary does not hold a key.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., dict, key -&gt; ..., bool</c>
        /// </remarks>
        DictNIn,
        #endregion


        #region Object Operations
        /// <summary>Allocates an uninitialised instance of the class at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., obj</c><br/>
        /// Notes: allocation only. The instance is sized from the class's instance slot count and
        /// zeroed; a constructor still has to be invoked separately, normally with
        /// <see cref="InvokeSpecial"/>. Instantiating an abstract class must be rejected.
        /// </remarks>
        ObjNew,

        /// <summary>Allocates an instance using a 4-byte type index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., obj</c>
        /// </remarks>
        ObjNewX,
        #endregion


        #region Field Operations
        /// <summary>Reads an instance field.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., obj -&gt; ..., value</c><br/>
        /// Notes: the field table entry carries the slot index, so the read is a direct offset
        /// into the instance rather than a name lookup. A null receiver needs a defined trap.
        /// </remarks>
        FieldGet,

        /// <summary>Writes an instance field.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., obj, value -&gt; ...</c><br/>
        /// Notes: the compiler must reject this against a read-only field outside a constructor.
        /// </remarks>
        FieldSet,
        #endregion


        #region Closure Operations
        /// <summary>Captures upvalues and builds a closure over the function at <c>functionIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) functionIdx(2) upvaluesCount(1)</c> - 4 bytes.<br/>
        /// Stack: <c>..., u1, ..., uN -&gt; ..., closure</c><br/>
        /// Notes: pops exactly <c>upvaluesCount</c> values, deepest becoming upvalue 0, which is
        /// the numbering <see cref="UpValueGet"/> uses. Caps captures at 255.
        /// </remarks>
        NewClosure,

        /// <summary>Builds a closure using a 4-byte function index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) functionIdx(4) upvaluesCount(1)</c> - 6 bytes.<br/>
        /// Stack: <c>..., u1, ..., uN -&gt; ..., closure</c>
        /// </remarks>
        NewClosureX,
        #endregion


        #region Upvalue Operations
        /// <summary>Reads a captured variable from the currently executing closure.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) upvalueIdx(1)</c> - 2 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c><br/>
        /// Notes: only valid inside a closure body. There is no matching setter, so captures are
        /// read-only as the set stands.
        /// </remarks>
        UpValueGet,
        #endregion


        #region Control Flow Operations
        /// <summary>Branches if the popped condition is false.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., cond -&gt; ...</c><br/>
        /// Notes: the offset is signed and relative to the instruction following this one, so a
        /// negative value branches backwards - the shape of every loop.
        /// </remarks>
        JPZ,

        /// <summary>Branches if the popped condition is true.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., cond -&gt; ...</c>
        /// </remarks>
        JPNZ,

        /// <summary>Branches if the popped value is the null reference.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        JPN,

        /// <summary>Branches if the popped value is a non-null reference.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        JPNN,

        /// <summary>Branches unconditionally.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c>
        /// </remarks>
        JP,

        /// <summary>Branches if the popped condition is false, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., cond -&gt; ...</c>
        /// </remarks>
        JPZX,

        /// <summary>Branches if the popped condition is true, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., cond -&gt; ...</c>
        /// </remarks>
        JPNZX,

        /// <summary>Branches if the popped value is null, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        JPNX,

        /// <summary>Branches if the popped value is non-null, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        JPNNX,

        /// <summary>Branches unconditionally, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c>
        /// </remarks>
        JPX,

        /// <summary>Branches if the two popped integers are equal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: fuses a comparison and a branch, so the boolean never reaches the stack. This is
        /// why the whole compare-and-branch family exists alongside the plain comparisons.
        /// </remarks>
        JPEQ,

        /// <summary>Branches if the two popped floats are equal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: never taken when either operand is NaN.
        /// </remarks>
        JPFEQ,

        /// <summary>Branches if the two popped references are identical.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPREQ,

        /// <summary>Branches if the two popped integers are equal, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPEQX,

        /// <summary>Branches if the two popped floats are equal, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPFEQX,

        /// <summary>Branches if the two popped references are identical, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPREQX,

        /// <summary>Branches if the two popped integers differ.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPNE,

        /// <summary>Branches if the two popped floats differ.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: always taken when either operand is NaN.
        /// </remarks>
        JPFNE,

        /// <summary>Branches if the two popped references are not identical.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPRNE,

        /// <summary>Branches if the two popped integers differ, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPNEX,

        /// <summary>Branches if the two popped floats differ, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPFNEX,

        /// <summary>Branches if the two popped references are not identical, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPRNEX,

        /// <summary>Branches if the deeper popped integer is greater than the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: taken when <c>a &gt; b</c>.
        /// </remarks>
        JPGT,

        /// <summary>Branches if the deeper popped float is greater than the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: never taken when either operand is NaN.
        /// </remarks>
        JPFGT,

        /// <summary>Branches on integer greater-than, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPGTX,

        /// <summary>Branches on float greater-than, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPFGTX,

        /// <summary>Branches if the deeper popped integer is greater than or equal to the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPGE,

        /// <summary>Branches if the deeper popped float is greater than or equal to the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: never taken when either operand is NaN.
        /// </remarks>
        JPFGE,

        /// <summary>Branches on integer greater-or-equal, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPGEX,

        /// <summary>Branches on float greater-or-equal, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPFGEX,

        /// <summary>Branches if the deeper popped integer is less than the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPLT,

        /// <summary>Branches if the deeper popped float is less than the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: never taken when either operand is NaN.
        /// </remarks>
        JPFLT,

        /// <summary>Branches on integer less-than, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPLTX,

        /// <summary>Branches on float less-than, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPFLTX,

        /// <summary>Branches if the deeper popped integer is less than or equal to the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPLE,

        /// <summary>Branches if the deeper popped float is less than or equal to the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: never taken when either operand is NaN.
        /// </remarks>
        JPFLE,

        /// <summary>Branches on integer less-or-equal, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPLEX,

        /// <summary>Branches on float less-or-equal, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPFLEX,

        /// <summary>Branches if the popped value is an instance of the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2) relativeOffset(2)</c> - 5 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c><br/>
        /// Notes: carries two immediates, so this is the widest of the 2-byte-offset branches.
        /// Fuses <see cref="InstanceOf"/> with a branch, which is the shape a type switch compiles to.
        /// </remarks>
        JPInstanceOf,

        /// <summary>Branches on instance-of, with 4-byte type index and offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(4) relativeOffset(4)</c> - 9 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        JPInstanceOfX,
        #endregion


        #region Call Operations
        /// <summary>Calls a module-level function declared in the current module.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) functionIdx(2) argsCount(1) retCount(1)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: pops exactly <c>argsCount</c> values, deepest being the first parameter, and
        /// pushes <c>retCount</c> results. Skipping the module table is what makes this the cheap
        /// case for intra-module calls.
        /// </remarks>
        CallLocalModule,

        /// <summary>Calls a function in the current module, with a 4-byte function index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) functionIdx(4) argsCount(1) retCount(1)</c> - 7 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c>
        /// </remarks>
        CallLocalModuleX,

        /// <summary>Calls a module-level function in another module.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) moduleIdx(2) functionIdx(2) argsCount(1) retCount(1)</c> - 7 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: the target module must already be loaded and linked.
        /// </remarks>
        CallModule,

        /// <summary>Calls a function in another module, with 4-byte module and function indices.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) moduleIdx(4) functionIdx(4) argsCount(1) retCount(1)</c> - 11 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: the longest instruction in the set.
        /// </remarks>
        CallModuleX,

        /// <summary>Calls a host-defined global function.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) functionIdx(2) argsCount(1) retCount(1)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: dispatches through the native entry point, a managed function pointer, so the
        /// call costs no marshalling transition.
        /// </remarks>
        CallGlobalNative,

        /// <summary>Calls a host-defined global function, with a 4-byte function index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) functionIdx(4) argsCount(1) retCount(1)</c> - 7 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c>
        /// </remarks>
        CallGlobalNativeX,
        #endregion


        #region Method Operations
        /// <summary>Invokes an instance method through the receiver's virtual method table.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) argsCount(1) retCount(1)</c> - 5 bytes.<br/>
        /// Stack: <c>..., obj, a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: the method table entry supplies a vtable slot, so dispatch is one load plus an
        /// indirect call - the receiver's runtime class decides which override runs. A null
        /// receiver needs a defined trap.
        /// </remarks>
        InvokeVirtual,

        /// <summary>Invokes an instance method without virtual dispatch.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) argsCount(1) retCount(1)</c> - 5 bytes.<br/>
        /// Stack: <c>..., obj, a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: binds exactly the method named in the table, ignoring any override. This is how
        /// constructors and explicit base calls are issued.
        /// </remarks>
        InvokeSpecial,

        /// <summary>Invokes a static method of the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) typeIdx(2) argsCount(1) retCount(1)</c> - 7 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: no receiver is popped.
        /// </remarks>
        InvokeStatic,

        /// <summary>Invokes a static method, with a 4-byte type index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) typeIdx(4) argsCount(1) retCount(1)</c> - 9 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: only the type index widens here; the method index stays 2 bytes.
        /// </remarks>
        InvokeStaticX,

        /// <summary>Invokes a method through an interface contract.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) argsCount(1) retCount(1)</c> - 5 bytes.<br/>
        /// Stack: <c>..., obj, a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: resolves through the receiver class's interface dispatch table, which maps an
        /// interface slot to a vtable slot - one extra indirection over <see cref="InvokeVirtual"/>.
        /// </remarks>
        InvokeInterface,

        /// <summary>Invokes a host-implemented instance method.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) argsCount(1) retCount(1)</c> - 5 bytes.<br/>
        /// Stack: <c>..., obj, a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: goes straight to the native entry point instead of through the vtable.
        /// </remarks>
        InvokeNative,

        /// <summary>Invokes a host-implemented instance method, with a 4-byte method index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(4) argsCount(1) retCount(1)</c> - 7 bytes.<br/>
        /// Stack: <c>..., obj, a1, ..., aN -&gt; ..., result?</c>
        /// </remarks>
        InvokeNativeX,

        /// <summary>Invokes a host-implemented static method of the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) typeIdx(2) argsCount(1) retCount(1)</c> - 7 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c>
        /// </remarks>
        InvokeStaticNative,

        /// <summary>Invokes a host-implemented static method, with a 4-byte type index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) typeIdx(4) argsCount(1) retCount(1)</c> - 9 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c>
        /// </remarks>
        InvokeStaticNativeX,

        /// <summary>Calls a closure taken from the stack.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) argsCount(1) retCount(1)</c> - 3 bytes.<br/>
        /// Stack: <c>..., closure, a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: the only call form with no index immediate - the target comes from the stack, so
        /// it is resolved entirely at run time. A null closure needs a defined trap.
        /// </remarks>
        InvokeClosure,
        #endregion


        #region Return Operations
        /// <summary>Returns from the current function without a value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ...</c><br/>
        /// Notes: discards the frame; anything left on its operand stack is dropped.
        /// </remarks>
        ReturnVoid,

        /// <summary>Returns from the current function with a single value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., result -&gt; ...</c><br/>
        /// Notes: pops one value and hands it to the caller. Returning several values means
        /// packing them into a tuple first, since there is no multi-value return instruction.
        /// </remarks>
        ReturnValue,
        #endregion
    }
}
