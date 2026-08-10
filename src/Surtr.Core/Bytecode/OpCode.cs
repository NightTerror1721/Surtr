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
    ///   <item><description>A <c>Str</c> prefix means the operands are strings compared by their text rather than by identity.</description></item>
    ///   <item><description>An <c>X</c> suffix widens an immediate to 4 bytes, for pools or jump distances that outgrow the 2-byte form.</description></item>
    ///   <item><description>An <c>S</c> suffix narrows an immediate to 1 byte, for the common small-index case.</description></item>
    ///   <item><description>A trailing digit is a dedicated opcode for that fixed index, so the common case costs no immediate at all.</description></item>
    /// </list>
    /// <para>
    /// Pool indices refer to the tables on the declaring module's chunk: constants, types, fields,
    /// methods and modules each have their own. Since the enum is the on-disk encoding, the
    /// numeric value of every member is part of the bytecode format - inserting a member in the
    /// middle renumbers everything after it and invalidates already-compiled bytecode.
    /// <b>That rule is in force</b>: <c>Bytecode/Image/</c> writes chunks to bytes, so every value
    /// below is on disk somewhere. New opcodes go at the end, whatever family they belong to,
    /// which is why the tail of this enum reads less tidily than its middle. See
    /// <c>docs/Opcodes.md</c> for the set laid out by family with its numeric values.
    /// </para>
    /// <para>
    /// There is deliberately no separate opcode for calling a host-implemented method. Where a
    /// call lands is a property of the <c>SurtrMethodInfo</c> the call site names, not of the call
    /// site, and the interpreter has to read it anyway - a virtual call can resolve onto a native
    /// override. Every <c>Invoke</c> and <c>Call</c> below therefore reaches bytecode and host code
    /// alike, at the cost of one byte load and a perfectly predicted branch.
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

        /// <summary>String equality by text.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c><br/>
        /// Notes: the counterpart to <see cref="REQ"/> for the one reference type Surtr compares by
        /// value. Its own opcode rather than a call to <c>string.equals</c>, because <c>==</c> on
        /// strings is common enough that a call per comparison would show. Two null strings are
        /// equal; a null and a non-null are not.
        /// </remarks>
        StrEQ,

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

        /// <summary>String inequality by text.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c>
        /// </remarks>
        StrNE,

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

        /// <summary>Retags an integer as a character.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., char</c><br/>
        /// Notes: int, bool and char share one representation, so this changes only the value's tag
        /// and truncates the payload to 16 bits. The tag still matters: it is what decides which
        /// class the value reports and which box <see cref="BoxChar"/> versus <see cref="BoxInt"/>
        /// produces.
        /// </remarks>
        I2C,

        /// <summary>Retags a character as an integer.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., int</c><br/>
        /// Notes: always exact - every character fits an integer.
        /// </remarks>
        C2I,

        /// <summary>Converts an integer to a boolean.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c><br/>
        /// Notes: normalises as well as retags - any non-zero integer becomes <c>true</c>, so the
        /// payload is always 0 or 1 afterwards. That normalisation is what lets every boolean
        /// opcode treat the payload as a bit.
        /// </remarks>
        I2B,

        /// <summary>Retags a boolean as an integer, giving 0 or 1.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., int</c>
        /// </remarks>
        B2I,

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

        /// <summary>Boxes a character into a heap object.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., ref</c>
        /// </remarks>
        BoxChar,

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
        /// the value is unchanged on success; a failure traps as `InvalidCastException`.
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
        /// Notes: an out-of-range index traps as `IndexOutOfRangeException`.
        /// </remarks>
        StrGet,
        #endregion


        #region Array Operations
        /// <summary>Allocates an array of the type at <c>typeIdx</c>, whose length is taken from the stack.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., size -&gt; ..., array</c><br/>
        /// Notes: <c>typeIdx</c> names the whole parameterised type - <c>AI</c>, <c>AS</c>,
        /// <c>ADIS</c> - not the element type alone, so one immediate carries both the descriptor
        /// the object keeps and the element family the elements are initialised from. Elements
        /// start at that family's zero: <c>0</c>, <c>0.0</c>, <c>false</c>, <c>'\0'</c> or null.
        /// </remarks>
        ArrNew,

        /// <summary>Allocates an array whose length is an immediate.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2) size(4)</c> - 7 bytes.<br/>
        /// Stack: <c>... -&gt; ..., array</c><br/>
        /// Notes: not a widened <see cref="ArrNew"/> but a different addressing mode - the length
        /// moves from the stack into the instruction, for arrays of statically known size.
        /// </remarks>
        ArrNewX,

        /// <summary>Pops <c>size</c> values and packs them into a new array.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2) size(2)</c> - 5 bytes.<br/>
        /// Stack: <c>..., v1, ..., vN -&gt; ..., array</c><br/>
        /// Notes: what an array literal compiles to. The deepest popped value becomes element 0,
        /// matching <see cref="TupPack"/>.
        /// </remarks>
        ArrPack,

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
        /// Notes: an out-of-range index traps as `IndexOutOfRangeException`.
        /// </remarks>
        ArrGet,

        /// <summary>Writes an array element.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, index, value -&gt; ...</c><br/>
        /// Notes: consumes all three operands and pushes nothing.
        /// </remarks>
        ArrSet,

        /// <summary>Appends a value to an array, growing it.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, value -&gt; ...</c><br/>
        /// Notes: an opcode rather than a method on the <c>array</c> built-in because there is no
        /// way to write its signature - a descriptor names one concrete type, and "the element type
        /// of whatever this array is" is not expressible. The same reasoning covers every opcode
        /// from here to <see cref="ArrIndexOf"/>, and their dictionary counterparts.
        /// </remarks>
        ArrPush,

        /// <summary>Removes and pushes an array's last element.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr -&gt; ..., value</c><br/>
        /// Notes: popping an empty array traps.
        /// </remarks>
        ArrPop,

        /// <summary>Inserts a value at an index, shifting everything after it up.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, index, value -&gt; ...</c><br/>
        /// Notes: an index equal to the length appends; anything beyond it traps.
        /// </remarks>
        ArrInsert,

        /// <summary>Removes the element at an index, shifting everything after it down.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, index -&gt; ...</c>
        /// </remarks>
        ArrRemoveAt,

        /// <summary>Drops every element of an array.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr -&gt; ...</c>
        /// </remarks>
        ArrClear,

        /// <summary>Pushes the index of the first element equal to a value, or <c>-1</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, value -&gt; ..., int</c><br/>
        /// Notes: equality is the runtime's value semantics, not raw bits, so two distinct string
        /// objects holding the same text match. Linear scan.
        /// </remarks>
        ArrIndexOf,

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
        /// <summary>Pops <c>size</c> values and packs them into a tuple of the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2) size(1)</c> - 4 bytes.<br/>
        /// Stack: <c>..., v1, ..., vN -&gt; ..., tuple</c><br/>
        /// Notes: the deepest popped value becomes element 0. Caps arity at 255. <c>typeIdx</c>
        /// names the shape - <c>T(IS)</c> - which is the only place a tuple's element types are
        /// recorded, since elements carry no type of their own.
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
        /// <summary>Allocates an empty dictionary of the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., dict</c><br/>
        /// Notes: <c>typeIdx</c> names the whole pair - <c>DIS</c> for <c>{int: string}</c>.
        /// </remarks>
        DictNew,

        /// <summary>Pops <c>count</c> key/value pairs and packs them into a new dictionary.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2) count(2)</c> - 5 bytes.<br/>
        /// Stack: <c>..., k1, v1, ..., kN, vN -&gt; ..., dict</c><br/>
        /// Notes: what a dictionary literal compiles to. Later pairs overwrite earlier ones on a
        /// duplicate key, as <see cref="DictSet"/> does.
        /// </remarks>
        DictPack,

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

        /// <summary>Removes the entry stored under a key.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., dict, key -&gt; ..., bool</c><br/>
        /// Notes: pushes whether an entry was actually removed, so a caller that does not care can
        /// drop it with <see cref="Pop"/> and one that does needs no second lookup.
        /// </remarks>
        DictDel,

        /// <summary>Drops every entry of a dictionary.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., dict -&gt; ...</c>
        /// </remarks>
        DictClear,

        /// <summary>Collects a dictionary's keys into a new array of the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., dict -&gt; ..., array</c><br/>
        /// Notes: the array's own type has to be named here because it cannot be derived at run
        /// time - the dictionary knows <c>DIS</c>, but building <c>AI</c> from it would mean parsing
        /// a descriptor on every call. In the dictionary's own iteration order.
        /// </remarks>
        DictKeys,

        /// <summary>Collects a dictionary's values into a new array of the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., dict -&gt; ..., array</c><br/>
        /// Notes: in the same order as <see cref="DictKeys"/>, so the two line up element for element.
        /// </remarks>
        DictValues,

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
        /// into the instance rather than a name lookup. A null receiver hits the CLR null check and surfaces as `NullReferenceException`.
        /// </remarks>
        FieldGet,

        /// <summary>Writes an instance field.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., obj, value -&gt; ...</c><br/>
        /// Notes: the compiler must reject this against a read-only field outside a constructor.
        /// </remarks>
        FieldSet,

        /// <summary>Reads a static field, or a module-level variable.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c><br/>
        /// Notes: no receiver, which is why this cannot be folded into <see cref="FieldGet"/> -
        /// doing so would put a static/instance test on one of the hottest instructions in the set.
        /// Module-level variables are the same thing: Surtr has no true globals, so a module
        /// variable is a static of its module and reaches its storage the same way. The field table
        /// entry carries the address of the slot itself, resolved when the declaring type was
        /// linked, so this is one indirect load.
        /// </remarks>
        StaticFieldGet,

        /// <summary>Reads a static field using a 4-byte field index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        StaticFieldGetX,

        /// <summary>Writes a static field, or a module-level variable.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c><br/>
        /// Notes: the compiler must reject this against a read-only field outside its static
        /// initializer.
        /// </remarks>
        StaticFieldSet,

        /// <summary>Writes a static field using a 4-byte field index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        StaticFieldSetX,
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

        /// <summary>Branches if the two popped strings hold the same text.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPStrEQ,

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

        /// <summary>Branches if the two popped strings hold the same text, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPStrEQX,

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

        /// <summary>Branches if the two popped strings hold different text.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPStrNE,

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

        /// <summary>Branches if the two popped strings hold different text, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPStrNEX,

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

        /// <summary>Branches through a jump table indexed by a contiguous range of integers.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) low(4) count(4) defaultOffset(4) offsets(4 * count)</c> - 13 + 4n bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c><br/>
        /// Notes: the popped value selects <c>offsets[value - low]</c>; anything outside
        /// <c>[low, low + count)</c> takes <c>defaultOffset</c>. One bounds check and one indexed
        /// load, whatever the number of cases - which is the whole reason a <c>switch</c> is not
        /// just a chain of <see cref="JPEQ"/>.
        /// <para>
        /// Every offset here is relative to <em>this instruction's own opcode byte</em>, unlike the
        /// ordinary branches, which are relative to the instruction that follows them. A
        /// variable-length instruction has no fixed "next" address to measure from at emit time.
        /// The same applies to <see cref="SwitchLookup"/>.
        /// </para>
        /// </remarks>
        Switch,

        /// <summary>Branches by searching a sorted table of integer keys.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) count(4) defaultOffset(4) (key(4) offset(4)) * count</c> - 9 + 8n bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c><br/>
        /// Notes: the counterpart to <see cref="Switch"/> for sparse cases, where a dense table
        /// would be mostly padding. Keys must be sorted ascending; the interpreter binary-searches
        /// them, so lookup is logarithmic rather than the linear scan a chain of comparisons costs.
        /// Offsets are measured from this instruction's opcode byte.
        /// </remarks>
        SwitchLookup,
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
        /// receiver hits the CLR null check and surfaces as `NullReferenceException`.
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

        /// <summary>Invokes a static method.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) argsCount(1) retCount(1)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: no receiver is popped. It carries no type index: the method entry already knows
        /// its declaring class, and static initializers run when their module is loaded rather than
        /// on first touch, so there is nothing for the interpreter to trigger here.
        /// </remarks>
        InvokeStatic,

        /// <summary>Invokes a static method, with a 4-byte method index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(4) argsCount(1) retCount(1)</c> - 7 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c>
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

        /// <summary>Calls a closure taken from the stack.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) argsCount(1) retCount(1)</c> - 3 bytes.<br/>
        /// Stack: <c>..., closure, a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: the only call form with no index immediate - the target comes from the stack, so
        /// it is resolved entirely at run time. A null closure hits the CLR null check and surfaces as `NullReferenceException`.
        /// </remarks>
        InvokeClosure,
        #endregion


        #region Exception Operations
        /// <summary>Raises the object on top of the stack as an exception.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., exception -&gt; </c> (the frame does not continue)<br/>
        /// Notes: control leaves this instruction and does not come back. The interpreter unwinds
        /// frame by frame looking for a handler whose protected range covers the raising
        /// instruction and whose caught type matches, clears that frame's operand stack, pushes the
        /// exception, and resumes at the handler.
        /// <para>
        /// There is deliberately no opcode for entering or leaving a <c>try</c>. Protected ranges
        /// live in a table on the method, so a <c>try</c> that never throws costs exactly nothing -
        /// where a push/pop-handler pair would cost two instructions on every entry. <c>finally</c>
        /// is the compiler's job: emit the block on each normal exit path, plus a catch-all handler
        /// that runs it and re-raises with this opcode. That is what javac does, and it keeps the
        /// interpreter free of a second unwinding mode.
        /// </para>
        /// <para>
        /// A trap the VM itself raises - a bad index, a division by zero - and an exception thrown
        /// by host code are both catchable the same way: they are wrapped as objects and unwound
        /// through the same tables.
        /// </para>
        /// </remarks>
        Throw,
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


        #region Nullable Primitive Operations
        // Appended rather than filed next to the reference-null instructions above, because the
        // enum value is the on-disk encoding: inserting one in the middle would renumber every
        // opcode after it and silently invalidate previously compiled bytecode.

        /// <summary>Pushes the "no value" state of a nullable primitive.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeCode(1)</c> - 2 bytes.<br/>
        /// Stack: <c>... -&gt; ..., absent</c><br/>
        /// Notes: the immediate is the <c>SurtrValueTypeCode</c> of the primitive that is missing,
        /// so the value can say what it is the absence of. It is never the null <em>reference</em>:
        /// that is <see cref="PushNull"/>, and the two carry different tags on purpose.
        /// </remarks>
        PushAbsent,

        /// <summary>Replaces a nullable primitive with whether it holds no value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c><br/>
        /// Notes: tests the tag, not the payload, which is exactly why it cannot be
        /// <see cref="IsNull"/>. A reference is its 32-bit payload, so <c>IsNull</c> ignores the
        /// tag - and an <c>int</c> of value zero would answer that test the same way a null does.
        /// </remarks>
        IsAbsent,

        /// <summary>Replaces a nullable primitive with whether it holds a value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c>
        /// </remarks>
        IsPresent,

        /// <summary>Pops a value and branches if it is an absent primitive.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a -&gt; ...</c><br/>
        /// Notes: what <c>??</c> and <c>?.</c> lower to over a nullable primitive.
        /// </remarks>
        JPA,

        /// <summary>Pops a value and branches if it is an absent primitive, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a -&gt; ...</c>
        /// </remarks>
        JPAX,

        /// <summary>Pops a value and branches if it is a present primitive.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a -&gt; ...</c>
        /// </remarks>
        JPNA,

        /// <summary>Pops a value and branches if it is a present primitive, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a -&gt; ...</c>
        /// </remarks>
        JPNAX,
        #endregion


        #region Value Class Operations
        /// <summary>Boxes the value on top of the stack as an instance of a named class.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a -&gt; ..., ref</c><br/>
        /// Notes: what a <c>value class</c> boxes through. The <c>Box*</c> family carries no type
        /// index because a boxed primitive takes the class the unboxed primitive already had; a
        /// value class is erased to the field it wraps, so where it has to become a reference the
        /// class it should present as is exactly the thing the value no longer says. Unboxing is
        /// still <see cref="Unbox"/>: the box's own value carries its tag.
        /// </remarks>
        BoxAs,

        /// <summary>Boxes as a named class, with a 4-byte type index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a -&gt; ..., ref</c>
        /// </remarks>
        BoxAsX,
        #endregion


        #region Range Operations
        /// <summary>Builds a range from two int bounds, excluding the upper one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., lo, hi -&gt; ..., ref</c><br/>
        /// Notes: allocates. A range written inline in a <c>for-in</c> header must never reach
        /// this - the compiler lowers that to a counted loop over two ints - so this is for a range
        /// that genuinely escapes into a variable, a parameter or a return.
        /// </remarks>
        RangeNew,

        /// <summary>Builds a range from two int bounds, including the upper one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., lo, hi -&gt; ..., ref</c><br/>
        /// Notes: the <c>..=</c> form. A separate opcode rather than an increment at the call site
        /// because <c>hi</c> may be <c>int.MaxValue</c>, where incrementing would wrap.
        /// </remarks>
        RangeNewInclusive,
        #endregion


        #region String Hashing
        /// <summary>Replaces a string with its hash.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., str -&gt; ..., hash</c><br/>
        /// Notes: reads the hash <see cref="Runtime.Objects.SurtrString"/> computed once at
        /// construction, so this is a load rather than a walk over the text. The value is
        /// <see cref="Runtime.Objects.SurtrString.ComputeHash"/>'s, which depends only on the text -
        /// that is what lets a compiler hash a <c>switch</c>'s case labels at build time and have
        /// them still match at run time, in another process. This exists for that lowering: hash,
        /// <c>SwitchLookup</c>, then <c>StrEQ</c> to settle collisions.
        /// </remarks>
        StrHash,
        #endregion
    }
}
