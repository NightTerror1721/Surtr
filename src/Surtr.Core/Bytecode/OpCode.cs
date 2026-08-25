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
    ///   <item><description>A <c>C</c> suffix moves an operand off the stack and into the instruction as a constant.</description></item>
    ///   <item><description>A trailing digit is a dedicated opcode for that fixed index, so the common case costs no immediate at all.</description></item>
    /// </list>
    /// <para>
    /// Pool indices refer to the tables on the declaring module's chunk: constants, types, fields,
    /// methods and modules each have their own.
    /// </para>
    /// <para>
    /// <b>Every value below is written out, and every value is final.</b> The enum member's number
    /// is the on-disk encoding, so it is part of the bytecode format rather than an implementation
    /// detail - which is why the set is laid out by family here, in the order
    /// <c>docs/Opcodes.md</c> documents it, with each member's value spelled rather than implied.
    /// Implicit numbering would make inserting a member in the middle a silent renumbering of
    /// everything after it; explicit numbering makes the same edit a compiler error, and makes a
    /// value something a reader can look up rather than count out.
    /// </para>
    /// <para>
    /// <b>New opcodes take a free value at the end and are never given one already in use.</b>
    /// The set is numbered contiguously from 0x00, family by family, and every value between Nop
    /// and GenResumed is assigned; 0xF0 through 0xFF are free. When the set did carry holes -
    /// retired instructions whose old bytes could never be reused - it was renumbered back into
    /// one contiguous run instead of growing around the gaps, a deliberate reset recorded here
    /// and in <c>OpCodeValueTests</c>: every byte of every image written before that reset means
    /// something different now, which is why a reader refuses an image whose format version is
    /// not its own instead of guessing. Changing how a module is *framed*, rather than what runs
    /// inside it, is what <c>SurtrModuleImage.FormatVersion</c> is for.
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
        Dup = 0x01,

        /// <summary>Discards the value on top of the stack.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c><br/>
        /// Notes: how a call's unused return value is dropped in statement position.
        /// </remarks>
        Pop = 0x02,
        #endregion

        #region Constants and Literals

        /// <summary>Pushes the null reference.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., null</c><br/>
        /// Notes: this is the null <em>reference</em>. The absence of a nullable primitive is
        /// <see cref="PushAbsent"/>, and the two carry different tags on purpose.
        /// </remarks>
        PushNull = 0x03,

        /// <summary>Pushes the boolean <c>true</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., bool</c><br/>
        /// Notes: there are exactly two booleans, so each gets an opcode and neither costs a constant
        /// pool slot. The pool's first ten slots are the cheapest storage in the format and are better
        /// spent on values that cannot be pushed inline at all.
        /// </remarks>
        PushTrue = 0x04,

        /// <summary>Pushes the boolean <c>false</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., bool</c>
        /// </remarks>
        PushFalse = 0x05,

        /// <summary>Pushes a signed 8-bit integer literal, sign-extended to a full integer value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) value(1)</c> - 2 bytes.<br/>
        /// Stack: <c>... -&gt; ..., int</c><br/>
        /// Notes: the narrowest way to materialise a small literal without touching the constant pool.
        /// </remarks>
        PushI8 = 0x06,

        /// <summary>Pushes a signed 16-bit integer literal, sign-extended to a full integer value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) value(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., int</c>
        /// </remarks>
        PushI16 = 0x07,

        /// <summary>Pushes a signed 32-bit integer literal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) value(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., int</c>
        /// </remarks>
        PushI32 = 0x08,

        /// <summary>Pushes a character literal, carried inline as a UTF-16 code unit.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) value(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., char</c><br/>
        /// Notes: the immediate is unsigned and covers the whole code unit range, so every character
        /// literal fits inline and none reaches the constant pool. The pushed value carries the char
        /// tag, which is what decides the class it reports and which box <see cref="BoxChar"/> makes.
        /// </remarks>
        PushChar = 0x09,

        /// <summary>Pushes the "no value" state of a nullable primitive.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeCode(1)</c> - 2 bytes.<br/>
        /// Stack: <c>... -&gt; ..., absent</c><br/>
        /// Notes: the immediate is the <c>SurtrValueTypeCode</c> of the primitive that is missing,
        /// so the value can say what it is the absence of. It is never the null <em>reference</em>:
        /// that is <see cref="PushNull"/>, and the two carry different tags on purpose.
        /// </remarks>
        PushAbsent = 0x0A,

        /// <summary>Loads constant 0.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc0 = 0x0B,

        /// <summary>Loads constant 1.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc1 = 0x0C,

        /// <summary>Loads constant 2.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc2 = 0x0D,

        /// <summary>Loads constant 3.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc3 = 0x0E,

        /// <summary>Loads constant 4.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc4 = 0x0F,

        /// <summary>Loads constant 5.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc5 = 0x10,

        /// <summary>Loads constant 6.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc6 = 0x11,

        /// <summary>Loads constant 7.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc7 = 0x12,

        /// <summary>Loads constant 8.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc8 = 0x13,

        /// <summary>Loads constant 9.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc9 = 0x14,

        /// <summary>Loads a constant using a 1-byte index, for the first 256 pool entries.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) constIdx(1)</c> - 2 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        LdcS = 0x15,

        /// <summary>Loads the constant at <c>constIdx</c> from the chunk's constant pool.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) constIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldc = 0x16,

        /// <summary>Loads a constant using a 4-byte index, for pools larger than 65536 entries.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) constIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        LdcX = 0x17,
        #endregion

        #region Local Variables

        /// <summary>Loads local 0.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c><br/>
        /// Notes: on an instance method this is the receiver.
        /// </remarks>
        Ldl0 = 0x18,

        /// <summary>Loads local 1.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldl1 = 0x19,

        /// <summary>Loads local 2.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldl2 = 0x1A,

        /// <summary>Loads local 3.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldl3 = 0x1B,

        /// <summary>Loads local 4.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldl4 = 0x1C,

        /// <summary>Loads local 5.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldl5 = 0x1D,

        /// <summary>Loads a local using a 1-byte index, for the first 256 slots of the frame.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) localIdx(1)</c> - 2 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        LdlS = 0x1E,

        /// <summary>Loads the local variable at <c>localIdx</c> from the current frame.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) localIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        Ldl = 0x1F,

        /// <summary>Pops a value into local 0.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl0 = 0x20,

        /// <summary>Pops a value into local 1.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl1 = 0x21,

        /// <summary>Pops a value into local 2.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl2 = 0x22,

        /// <summary>Pops a value into local 3.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl3 = 0x23,

        /// <summary>Pops a value into local 4.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl4 = 0x24,

        /// <summary>Pops a value into local 5.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl5 = 0x25,

        /// <summary>Pops a value into a local using a 1-byte index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) localIdx(1)</c> - 2 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        StlS = 0x26,

        /// <summary>Pops a value and stores it into the local at <c>localIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) localIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        Stl = 0x27,

        /// <summary>Adds a signed 8-bit constant to an integer local, in place.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) localIdx(1) delta(1)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c><br/>
        /// Notes: the whole of <c>i += 1</c> in one instruction. Written out it is <c>Ldl</c>,
        /// <c>PushI8</c>, <c>Add</c>, <c>Stl</c> - four dispatches, up to eight bytes, and two round
        /// trips through the operand stack - for an update that never needs to leave the frame. The
        /// delta is signed, so a decrement is the same instruction. The local is read and written as an
        /// integer and comes back tagged as one; a slot holding anything else has no defined result, and
        /// the compiler is what guarantees it does not. Locals past 255, and deltas outside a signed
        /// byte, fall back to the long form - <c>SurtrCodeEmitter.IncrementLocal</c> decides which.
        /// </remarks>
        IncLocal = 0x28,
        #endregion

        #region Field Operations

        /// <summary>Reads an instance field.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., obj -&gt; ..., value</c><br/>
        /// Notes: the field table entry carries the slot index, so the read is a direct offset
        /// into the instance rather than a name lookup. A null receiver hits the CLR null check and surfaces as `NullReferenceException`.
        /// </remarks>
        FieldGet = 0x29,

        /// <summary>Writes an instance field.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., obj, value -&gt; ...</c><br/>
        /// Notes: the compiler must reject this against a read-only field outside a constructor.
        /// </remarks>
        FieldSet = 0x2A,

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
        StaticFieldGet = 0x2B,

        /// <summary>Reads a static field using a 4-byte field index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c>
        /// </remarks>
        StaticFieldGetX = 0x2C,

        /// <summary>Writes a static field, or a module-level variable.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c><br/>
        /// Notes: the compiler must reject this against a read-only field outside its static
        /// initializer.
        /// </remarks>
        StaticFieldSet = 0x2D,

        /// <summary>Writes a static field using a 4-byte field index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        StaticFieldSetX = 0x2E,
        #endregion

        #region Upvalue Operations

        /// <summary>Reads a captured variable from the currently executing closure.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) upvalueIdx(1)</c> - 2 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c><br/>
        /// Notes: only valid inside a closure body. There is deliberately no matching setter: a capture
        /// is copied rather than shared, so the compiler only ever captures what is never reassigned.
        /// </remarks>
        UpValueGet = 0x2F,
        #endregion

        #region Value Type Operations

        /// <summary>Copies a multi-slot value out of a local range onto the operand stack.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) localIdx(2) n(1)</c> - 4 bytes.<br/>
        /// Stack: <c>... -&gt; ..., s1, ..., sn</c><br/>
        /// Notes: the whole block moves; nothing is resolved and no tag is inspected. What a load
        /// of a variable whose declared type occupies more than one slot lowers to - a one-slot
        /// type keeps using <see cref="Ldl"/> and its dedicated forms. The count travels in the
        /// instruction so the interpreter never resolves a type to move bytes.
        /// </remarks>
        LoadValueLocal = 0x30,

        /// <summary>Pops a multi-slot value into a local range.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) localIdx(2) n(1)</c> - 4 bytes.<br/>
        /// Stack: <c>..., s1, ..., sn -&gt; ...</c><br/>
        /// Notes: the mirror of <see cref="LoadValueLocal"/>, and what an assignment to such a
        /// variable lowers to - copying the block is exactly the copy-on-assignment semantics of
        /// a value type.
        /// </remarks>
        StoreValueLocal = 0x31,

        /// <summary>Reads one slot at a fixed offset inside a local range.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) localIdx(2) offset(2)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c><br/>
        /// Notes: what reading one field of a multi-slot local lowers to - <c>v.x</c> reads slot
        /// <c>local + 0</c> without moving the rest of the value anywhere. The offset is absolute
        /// within the frame (local index plus field offset, already summed by the compiler), so
        /// the read is one indexed load off the frame base.
        /// </remarks>
        LoadLocalField = 0x32,

        /// <summary>Writes one slot at a fixed offset inside a local range.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) localIdx(2) offset(2)</c> - 5 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c><br/>
        /// Notes: the write side of <see cref="LoadLocalField"/>. In practice only the compiler's
        /// constructor splice emits it - fields of a value class are <c>let</c>, so assignment is
        /// construction - but the opcode itself does not care who writes or when.
        /// </remarks>
        StoreLocalField = 0x33,

        /// <summary>Copies a multi-slot value out of an instance's flattened field block.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(2) n(1)</c> - 4 bytes.<br/>
        /// Stack: <c>..., obj -&gt; ..., s1, ..., sn</c><br/>
        /// Notes: what reading a field whose declared type is a multi-field value class lowers to
        /// (<c>enemy.position</c>). The field's own slot index is where the block starts inside
        /// the instance - the linker flattened nested value types into consecutive slots, so the
        /// copy is one indexed base plus a run. Reading one sub-slot of that value keeps using
        /// <see cref="FieldGet"/> at the summed absolute slot; this moves the whole block.
        /// </remarks>
        LoadValueField = 0x34,

        /// <summary>Pops a receiver and a multi-slot value into an instance's flattened field block.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(2) n(1)</c> - 4 bytes.<br/>
        /// Stack: <c>..., obj, s1, ..., sn -&gt; ...</c><br/>
        /// Notes: the write side of <see cref="LoadValueField"/>, and what every assignment to such
        /// a field lowers to - including the constructor splice, which is the only writer a
        /// <c>let</c> field ever gets. Copying the block is the value type's copy-on-assignment
        /// semantics showing at its storage boundary.
        /// </remarks>
        StoreValueField = 0x35,

        /// <summary>Copies a multi-slot value out of a static's flattened storage.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(2) n(1)</c> - 4 bytes.<br/>
        /// Stack: <c>... -&gt; ..., s1, ..., sn</c><br/>
        /// Notes: the static counterpart of <see cref="LoadValueField"/>. A static whose declared
        /// type is a multi-field value class owns <c>n</c> consecutive slots in its owner's static
        /// storage, addressed from the slot the linker bound - so the read is one indirect base
        /// plus a run, exactly what <see cref="StaticFieldGet"/> does for one slot.
        /// </remarks>
        LoadValueStatic = 0x36,

        /// <summary>Pops a multi-slot value into a static's flattened storage.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) fieldIdx(2) n(1)</c> - 4 bytes.<br/>
        /// Stack: <c>..., s1, ..., sn -&gt; ...</c><br/>
        /// Notes: the write side of <see cref="LoadValueStatic"/> - what an assignment to such a
        /// static, and the static initializer that seeds it, both lower to.
        /// </remarks>
        StoreValueStatic = 0x37,

        /// <summary>Boxes a multi-slot value on top of the stack as an instance of its class.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2) n(1)</c> - 4 bytes.<br/>
        /// Stack: <c>..., s1, ..., sn -&gt; ..., ref</c><br/>
        /// Notes: what a value type flowing into a reference-typed slot boxes through. The box is
        /// an ordinary instance of the named class whose field slots receive the <c>n</c> stack
        /// slots verbatim - which is why every existing path that walks instances (field access,
        /// virtual dispatch, the collector through the class's reference-slot map) already knows
        /// how to walk a boxed value. Allocates, so it routes through the safepoint like every
        /// other allocation opcode.
        /// </remarks>
        BoxValue = 0x38,

        /// <summary>Copies the field slots of a boxed value back onto the operand stack.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) n(1)</c> - 2 bytes.<br/>
        /// Stack: <c>..., ref -&gt; ..., s1, ..., sn</c><br/>
        /// Notes: the mirror of <see cref="BoxValue"/>. The count is an immediate because the
        /// compiler knows the subject's layout statically; the receiver itself is not re-checked -
        /// a cast to the value type has already run by the time this executes, exactly as
        /// <see cref="Unbox"/> assumes its subject is a box.
        /// </remarks>
        UnboxValue = 0x39,

        /// <summary>Packs a range block into the heap object it presents as.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., lo, hi, flag -&gt; ..., ref</c><br/>
        /// Notes: the packed-form half of the range boundary - what a range crossing into a
        /// one-reference slot (an array element, a dictionary key, an erased parameter) boxes
        /// through. The result is an ordinary registered <c>SurtrRange</c>, which is what every
        /// path that walks boxed values already walks. Allocates, so it routes through the
        /// safepoint like every other packing opcode.
        /// </remarks>
        RangePack = 0x3A,

        /// <summary>Expands a packed range back into its three-slot block.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., ref -&gt; ..., lo, hi, flag</c><br/>
        /// Notes: the mirror of <see cref="RangePack"/>, on the way back out of a one-reference
        /// slot. No allocation, so no safepoint.
        /// </remarks>
        RangeUnpack = 0x3B,
        #endregion

        #region Arithmetic Operations

        /// <summary>Integer addition.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a + b</c>
        /// </remarks>
        Add = 0x3C,

        /// <summary>Floating-point addition.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a + b</c>
        /// </remarks>
        FAdd = 0x3D,

        /// <summary>Integer subtraction.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a - b</c><br/>
        /// Notes: the deeper operand is the minuend, so the result is <c>a - b</c>, not <c>b - a</c>.
        /// </remarks>
        Sub = 0x3E,

        /// <summary>Floating-point subtraction.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a - b</c>
        /// </remarks>
        FSub = 0x3F,

        /// <summary>Integer multiplication.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a * b</c>
        /// </remarks>
        Mul = 0x40,

        /// <summary>Floating-point multiplication.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a * b</c>
        /// </remarks>
        FMul = 0x41,

        /// <summary>Integer division.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a / b</c><br/>
        /// Notes: division by zero has no defined result yet and needs a trap decision.
        /// </remarks>
        Div = 0x42,

        /// <summary>Floating-point division.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a / b</c><br/>
        /// Notes: division by zero follows IEEE 754 and yields an infinity or NaN rather than trapping.
        /// </remarks>
        FDiv = 0x43,

        /// <summary>Integer remainder.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a % b</c><br/>
        /// Notes: as with <see cref="Div"/>, a zero divisor still needs a defined behaviour.
        /// </remarks>
        Mod = 0x44,

        /// <summary>Floating-point remainder.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a % b</c>
        /// </remarks>
        FMod = 0x45,

        /// <summary>Integer negation.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., -a</c>
        /// </remarks>
        Neg = 0x46,

        /// <summary>Floating-point negation.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., -a</c><br/>
        /// Notes: flips the sign bit, so it also turns zero into negative zero.
        /// </remarks>
        FNeg = 0x47,
        #endregion

        #region Bitwise and Logical Operations

        /// <summary>Bitwise AND.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &amp; b</c>
        /// </remarks>
        And = 0x48,

        /// <summary>Bitwise OR.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a | b</c>
        /// </remarks>
        Or = 0x49,

        /// <summary>Bitwise exclusive OR.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a ^ b</c>
        /// </remarks>
        Xor = 0x4A,

        /// <summary>Bitwise complement.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., ~a</c><br/>
        /// Notes: this is the bitwise operator. The boolean negation is <see cref="Inv"/>.
        /// </remarks>
        Not = 0x4B,

        /// <summary>Left shift.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &lt;&lt; b</c><br/>
        /// Notes: shifts the deeper operand left by the top one. Shift counts at or above the
        /// operand width still need a defined behaviour.
        /// </remarks>
        Shl = 0x4C,

        /// <summary>Logical right shift, filling with zeroes.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &gt;&gt;&gt; b</c><br/>
        /// Notes: does not preserve the sign - a negative value becomes a large positive one.
        /// The sign-preserving form is <see cref="Sar"/>.
        /// </remarks>
        Shr = 0x4D,

        /// <summary>Arithmetic right shift, replicating the sign bit.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &gt;&gt; b</c><br/>
        /// Notes: keeps the sign, so a negative value stays negative. The zero-filling form is
        /// <see cref="Shr"/>.
        /// </remarks>
        Sar = 0x4E,

        /// <summary>Logical negation of a boolean.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., !a</c><br/>
        /// Notes: this is the boolean operator. The bitwise complement is <see cref="Not"/>.
        /// </remarks>
        Inv = 0x4F,
        #endregion

        #region Comparison Operations

        /// <summary>Integer equality.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c><br/>
        /// Notes: also covers bools and chars, which share the integer representation.
        /// </remarks>
        EQ = 0x50,

        /// <summary>Integer inequality.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c>
        /// </remarks>
        NE = 0x51,

        /// <summary>Integer greater-than.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &gt; b</c>
        /// </remarks>
        GT = 0x52,

        /// <summary>Integer greater-than-or-equal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &gt;= b</c>
        /// </remarks>
        GE = 0x53,

        /// <summary>Integer less-than.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &lt; b</c>
        /// </remarks>
        LT = 0x54,

        /// <summary>Integer less-than-or-equal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &lt;= b</c>
        /// </remarks>
        LE = 0x55,

        /// <summary>Floating-point equality.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c><br/>
        /// Notes: IEEE 754 semantics, so NaN compares unequal to everything including itself.
        /// </remarks>
        FEQ = 0x56,

        /// <summary>Floating-point inequality.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c><br/>
        /// Notes: NaN compares unequal to everything, so this yields true when either side is NaN.
        /// </remarks>
        FNE = 0x57,

        /// <summary>Floating-point greater-than.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &gt; b</c><br/>
        /// Notes: false whenever either operand is NaN.
        /// </remarks>
        FGT = 0x58,

        /// <summary>Floating-point greater-than-or-equal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &gt;= b</c><br/>
        /// Notes: false whenever either operand is NaN.
        /// </remarks>
        FGE = 0x59,

        /// <summary>Floating-point less-than.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &lt; b</c><br/>
        /// Notes: false whenever either operand is NaN.
        /// </remarks>
        FLT = 0x5A,

        /// <summary>Floating-point less-than-or-equal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., a &lt;= b</c><br/>
        /// Notes: false whenever either operand is NaN.
        /// </remarks>
        FLE = 0x5B,

        /// <summary>Reference identity.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c><br/>
        /// Notes: compares handles, not contents - two equal strings in different objects are not identical.
        /// </remarks>
        REQ = 0x5C,

        /// <summary>Reference non-identity.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c>
        /// </remarks>
        RNE = 0x5D,

        /// <summary>String equality by text.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c><br/>
        /// Notes: the counterpart to <see cref="REQ"/> for the one reference type Surtr compares by
        /// value. Its own opcode rather than a call to <c>string.equals</c>, because <c>==</c> on
        /// strings is common enough that a call per comparison would show. Two null strings are
        /// equal; a null and a non-null are not.
        /// </remarks>
        StrEQ = 0x5E,

        /// <summary>String inequality by text.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c>
        /// </remarks>
        StrNE = 0x5F,

        /// <summary>Value equality decided at runtime by each operand's own tag, for a generic type parameter's own slot.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c><br/>
        /// Notes: what <c>==</c> lowers to when neither operand's static type is a family this
        /// opcode already has a dedicated form for - a bare, still-abstract type parameter, most
        /// commonly. <c>==</c> is <em>value</em> equality everywhere in Surtr (Language-Syntax.md
        /// §5.7), which for a boxed primitive means a boxed <c>5</c> equals an unboxed <c>5</c> and
        /// two independently boxed <c>5</c>s equal each other - exactly what <c>REQ</c> gets wrong,
        /// since two different boxes are two different entities. The one comparer already answers
        /// this for every dictionary and array search (<c>SurtrValueComparer.ValuesEqual</c>); this
        /// is that same comparer reached from a compiled <c>==</c>, for the receiver whose static
        /// type is too erased to know in advance which of <see cref="EQ"/>/<see cref="FEQ"/>/
        /// <see cref="REQ"/>/<see cref="StrEQ"/> it will turn out to need.
        /// </remarks>
        DynEQ = 0x60,

        /// <summary>The negation of <see cref="DynEQ"/>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a, b -&gt; ..., bool</c>
        /// </remarks>
        DynNE = 0x61,
        #endregion

        #region Null and Absence Tests

        /// <summary>Tests whether the top value is the null reference.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c>
        /// </remarks>
        IsNull = 0x62,

        /// <summary>Tests whether the top value is a non-null reference.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c>
        /// </remarks>
        IsNotNull = 0x63,

        /// <summary>Replaces a nullable primitive with whether it holds no value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c><br/>
        /// Notes: tests the tag, not the payload, which is exactly why it cannot be
        /// <see cref="IsNull"/>. A reference is its 32-bit payload, so <c>IsNull</c> ignores the
        /// tag - and an <c>int</c> of value zero would answer that test the same way a null does.
        /// </remarks>
        IsAbsent = 0x64,

        /// <summary>Replaces a nullable primitive with whether it holds a value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c>
        /// </remarks>
        IsPresent = 0x65,
        #endregion

        #region Conversion Operations

        /// <summary>Widens an integer to a float.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., float</c>
        /// </remarks>
        I2F = 0x66,

        /// <summary>Narrows a float to an integer.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., int</c><br/>
        /// Notes: lossy, and pinned down rather than an unchecked C# cast, whose behaviour for an
        /// out-of-range double is platform-defined and would differ between x64 and ARM. Truncates
        /// toward zero in range; saturates to <c>int.MinValue</c>/<c>int.MaxValue</c> outside it;
        /// <c>NaN</c> converts to <c>0</c>.
        /// </remarks>
        F2I = 0x67,

        /// <summary>Retags an integer as a character.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., char</c><br/>
        /// Notes: int, bool and char share one representation, so this changes only the value's tag
        /// and truncates the payload to 16 bits. The tag still matters: it is what decides which
        /// class the value reports and which box <see cref="BoxChar"/> versus <see cref="BoxInt"/>
        /// produces.
        /// </remarks>
        I2C = 0x68,

        /// <summary>Retags a character as an integer.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., int</c><br/>
        /// Notes: always exact - every character fits an integer.
        /// </remarks>
        C2I = 0x69,

        /// <summary>Converts an integer to a boolean.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c><br/>
        /// Notes: normalises as well as retags - any non-zero integer becomes <c>true</c>, so the
        /// payload is always 0 or 1 afterwards. That normalisation is what lets every boolean
        /// opcode treat the payload as a bit.
        /// </remarks>
        I2B = 0x6A,

        /// <summary>Retags a boolean as an integer, giving 0 or 1.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., int</c>
        /// </remarks>
        B2I = 0x6B,
        #endregion

        #region Boxing Operations

        /// <summary>Boxes an integer into a heap object.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., ref</c><br/>
        /// Notes: allocates, so the result is a collectable reference rather than an inline value.
        /// </remarks>
        BoxInt = 0x6C,

        /// <summary>Boxes a float into a heap object.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., ref</c>
        /// </remarks>
        BoxFloat = 0x6D,

        /// <summary>Boxes a boolean into a heap object.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., ref</c>
        /// </remarks>
        BoxBool = 0x6E,

        /// <summary>Boxes a character into a heap object.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., ref</c>
        /// </remarks>
        BoxChar = 0x6F,

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
        BoxAs = 0x70,

        /// <summary>Boxes as a named class, with a 4-byte type index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a -&gt; ..., ref</c>
        /// </remarks>
        BoxAsX = 0x71,

        /// <summary>Unwraps a boxed value back to its inline representation.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., ref -&gt; ..., value</c><br/>
        /// Notes: recovers whichever primitive was boxed - the tag on the boxed value says which,
        /// so no per-type opcode is needed on the way back.
        /// </remarks>
        Unbox = 0x72,

        /// <summary>Boxes whatever primitive is on top of the stack, chosen by its own tag rather than the compiler's.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., ref</c><br/>
        /// Notes: a no-op when the subject is already a reference (including <c>null</c>) - the
        /// same convention <c>BoxInt</c>/<c>BoxFloat</c>/<c>BoxBool</c>/<c>BoxChar</c> follow when
        /// the compiler already knows a value needs no box, just decided from the tag instead of
        /// from a static type. Exists for a generic type parameter's own erased slot: a value
        /// reaching or leaving one through a body compiled once for every substitution of <c>T</c>
        /// (an array element read while <c>T</c> is still the declaring generic's own bare
        /// parameter, an <c>IIterator&lt;T&gt;.current</c> read through interface dispatch) may
        /// already be boxed or may still be a built-in's own raw storage, and nothing at the call
        /// site can tell which - only the value's own tag can. <see cref="Unbox"/> already reads
        /// that tag on the way back out; this is its mirror on the way in, and the two together are
        /// what let a value cross an erased slot without the compiler ever having to know <c>T</c>.
        /// </remarks>
        BoxDynamic = 0x73,

        /// <summary>The mirror of <see cref="BoxDynamic"/>: unboxes only a boxed primitive, leaving everything else untouched.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., a -&gt; ..., value</c><br/>
        /// Notes: a no-op both when the subject is already a raw primitive and when it is a
        /// reference that is not a box (an ordinary object, array, string - anything <see
        /// cref="Unbox"/> would need to already know is a box to touch safely). Exists for the write
        /// side of the same erased slot <see cref="BoxDynamic"/> reads from: a value statically
        /// typed by a bare generic parameter arrives already boxed (the calling convention boxes an
        /// argument or a field write on the way in), but the collection's own native storage this
        /// body writes it into - an array element, a dict value - was never boxed to begin with and
        /// must not become the one element that is (<c>docs/VM-Plan.md</c> §3.5's "no per-element
        /// type tags", which holds regardless of whether the array's own compile-time element type
        /// is still abstract). <c>Unbox</c> stays unconditional for the one call site that already
        /// knows its subject is a box; this is for the site that cannot know.
        /// </remarks>
        UnboxDynamic = 0x74,
        #endregion

        #region Range Operations

        /// <summary>Lays out a range block from two int bounds, excluding the upper one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., lo, hi -&gt; ..., lo, hi, 0</c><br/>
        /// Notes: allocates nothing - a range is an inline value (§2.9), three slots wide, and
        /// this opcode only materialises the inclusive flag its operator baked in. A range written
        /// inline in a <c>for-in</c> header must never reach this - the compiler lowers that to a
        /// counted loop over two ints - so this is for a range that genuinely escapes into a
        /// variable, a parameter or a return.
        /// </remarks>
        RangeNew = 0x75,

        /// <summary>Lays out a range block from two int bounds, including the upper one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., lo, hi -&gt; ..., lo, hi, 1</c><br/>
        /// Notes: the <c>..=</c> form - the mirror of <see cref="RangeNew"/>, differing only in
        /// the flag it writes. A separate opcode rather than an increment at the call site because
        /// <c>hi</c> may be <c>int.MaxValue</c>, where incrementing would wrap.
        /// </remarks>
        RangeNewInclusive = 0x76,
        #endregion

        #region String Operations

        /// <summary>Pushes the length of a string in characters.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., str -&gt; ..., int</c>
        /// </remarks>
        StrLen = 0x77,

        /// <summary>Reads the character at an index of a string.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., str, index -&gt; ..., char</c><br/>
        /// Notes: an out-of-range index traps as `IndexOutOfRangeException`.
        /// </remarks>
        StrGet = 0x78,

        /// <summary>Concatenates the top <c>count</c> strings into one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) count(1)</c> - 2 bytes.<br/>
        /// Stack: <c>..., s1, ..., sN -&gt; ..., string</c><br/>
        /// Notes: the deepest popped operand comes first in the result. <c>count</c> is at least two and
        /// at most 255.
        /// <para>
        /// The count is the whole point of the encoding. A chain of two-operand concatenations builds
        /// every intermediate result: <c>a + b + c + d</c> allocates three strings and copies the prefix
        /// four times over, and an interpolation with n holes is that shape by construction. One
        /// instruction with a count allocates exactly one string of exactly the right length, which is
        /// what a compiler should emit for a whole <c>+</c> spine or a whole interpolation - see
        /// <c>SurtrCodeEmitter.StrCat(int)</c>.
        /// </para>
        /// </remarks>
        StrCat = 0x79,

        /// <summary>Replaces a string with its hash.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., str -&gt; ..., hash</c><br/>
        /// Notes: reads the hash <see cref="Runtime.Objects.SurtrString"/> computed once on first
        /// need and cached, so this is a load rather than a walk over the text on every use. The
        /// value is <see cref="Runtime.Objects.SurtrString.ComputeHash"/>'s, which depends only on
        /// the text - that is what lets a compiler hash a <c>switch</c>'s case labels at build time
        /// and have them still match at run time, in another process. This exists for that
        /// lowering: hash, <c>SwitchLookup</c>, then <c>StrEQ</c> to settle collisions.
        /// </remarks>
        StrHash = 0x7A,
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
        ArrNew = 0x7B,

        /// <summary>Allocates an array whose length is an immediate.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2) size(4)</c> - 7 bytes.<br/>
        /// Stack: <c>... -&gt; ..., array</c><br/>
        /// Notes: not a widened <see cref="ArrNew"/> but a different addressing mode - the length
        /// moves from the stack into the instruction, for arrays of statically known size.
        /// </remarks>
        ArrNewX = 0x7C,

        /// <summary>Pops <c>size</c> values and packs them into a new array.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2) size(2)</c> - 5 bytes.<br/>
        /// Stack: <c>..., v1, ..., vN -&gt; ..., array</c><br/>
        /// Notes: what an array literal compiles to. The deepest popped value becomes element 0,
        /// matching <see cref="TupPack"/>.
        /// </remarks>
        ArrPack = 0x7D,

        /// <summary>Pushes an array's length.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr -&gt; ..., int</c>
        /// </remarks>
        ArrLen = 0x7E,

        /// <summary>Reads an array element.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, index -&gt; ..., value</c><br/>
        /// Notes: an out-of-range index traps as `IndexOutOfRangeException`.
        /// </remarks>
        ArrGet = 0x7F,

        /// <summary>Writes an array element.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, index, value -&gt; ...</c><br/>
        /// Notes: consumes all three operands and pushes nothing.
        /// </remarks>
        ArrSet = 0x80,

        /// <summary>Appends a value to an array, growing it.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, value -&gt; ...</c><br/>
        /// Notes: an opcode rather than a method on the <c>array</c> built-in because there is no
        /// way to write its signature - a descriptor names one concrete type, and "the element type
        /// of whatever this array is" is not expressible. The same reasoning covers every opcode
        /// from here to <see cref="ArrIndexOf"/>, and their dictionary counterparts.
        /// </remarks>
        ArrPush = 0x81,

        /// <summary>Removes and pushes an array's last element.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr -&gt; ..., value</c><br/>
        /// Notes: popping an empty array traps.
        /// </remarks>
        ArrPop = 0x82,

        /// <summary>Inserts a value at an index, shifting everything after it up.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, index, value -&gt; ...</c><br/>
        /// Notes: an index equal to the length appends; anything beyond it traps.
        /// </remarks>
        ArrInsert = 0x83,

        /// <summary>Removes the element at an index, shifting everything after it down.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, index -&gt; ...</c>
        /// </remarks>
        ArrRemoveAt = 0x84,

        /// <summary>Drops every element of an array.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr -&gt; ...</c>
        /// </remarks>
        ArrClear = 0x85,

        /// <summary>Pushes the index of the first element equal to a value, or <c>-1</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, value -&gt; ..., int</c><br/>
        /// Notes: equality is the runtime's value semantics, not raw bits, so two distinct string
        /// objects holding the same text match. Linear scan.
        /// </remarks>
        ArrIndexOf = 0x86,

        /// <summary>Tests whether an array contains a value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., arr, value -&gt; ..., bool</c><br/>
        /// Notes: linear scan, so cost grows with the array.
        /// </remarks>
        ArrIn = 0x87,
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
        TupPack = 0x88,

        /// <summary>Expands a tuple into <c>size</c> separate stack entries.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) size(1)</c> - 2 bytes.<br/>
        /// Stack: <c>..., tuple -&gt; ..., v1, ..., vN</c><br/>
        /// Notes: element 0 ends up deepest, so packing and unpacking round-trip.
        /// </remarks>
        TupUnpack = 0x89,

        /// <summary>Pushes a tuple's arity.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., tup -&gt; ..., int</c>
        /// </remarks>
        TupLen = 0x8A,

        /// <summary>Reads a tuple element at an index taken from the stack.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., tup, index -&gt; ..., value</c><br/>
        /// Notes: there is no matching setter - tuples are immutable once packed. A written tuple index
        /// is always a constant, so this is not what an element access compiles to;
        /// <see cref="TupGetC"/> is. What needs this form is a lowered <c>for-in</c>, whose index is a
        /// loop counter. An out-of-range index traps as <c>IndexOutOfRangeException</c>.
        /// </remarks>
        TupGet = 0x8B,

        /// <summary>Reads the tuple element at an immediate index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) index(1)</c> - 2 bytes.<br/>
        /// Stack: <c>..., tup -&gt; ..., value</c><br/>
        /// Notes: the form a compiler emits for <c>t.0</c> or <c>t[1]</c>, since a tuple index has to be
        /// a constant for the element's type to be known - which is the same reason there is no setter.
        /// One byte of immediate replaces a whole push, and the value never reaches the stack to be
        /// popped again. A tuple's arity is capped at 255 by <see cref="TupPack"/>, so the one-byte index
        /// reaches every element there can be and needs no wide form. An out-of-range index traps as
        /// <c>IndexOutOfRangeException</c>, as <see cref="TupGet"/> does.
        /// </remarks>
        TupGetC = 0x8C,
        #endregion

        #region Dictionary Operations

        /// <summary>Allocates an empty dictionary of the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., dict</c><br/>
        /// Notes: <c>typeIdx</c> names the whole pair - <c>DIS</c> for <c>{int: string}</c>.
        /// </remarks>
        DictNew = 0x8D,

        /// <summary>Pops <c>count</c> key/value pairs and packs them into a new dictionary.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2) count(2)</c> - 5 bytes.<br/>
        /// Stack: <c>..., k1, v1, ..., kN, vN -&gt; ..., dict</c><br/>
        /// Notes: what a dictionary literal compiles to. Later pairs overwrite earlier ones on a
        /// duplicate key, as <see cref="DictSet"/> does.
        /// </remarks>
        DictPack = 0x8E,

        /// <summary>Pushes the number of entries in a dictionary.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., dict -&gt; ..., int</c>
        /// </remarks>
        DictLen = 0x8F,

        /// <summary>Reads the value stored under a key.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., dict, key -&gt; ..., value</c><br/>
        /// Notes: a missing key needs a defined behaviour - trap, or push null.
        /// </remarks>
        DictGet = 0x90,

        /// <summary>Stores a value under a key, inserting or replacing.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., dict, key, value -&gt; ...</c>
        /// </remarks>
        DictSet = 0x91,

        /// <summary>Removes the entry stored under a key.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., dict, key -&gt; ..., bool</c><br/>
        /// Notes: pushes whether an entry was actually removed, so a caller that does not care can
        /// drop it with <see cref="Pop"/> and one that does needs no second lookup.
        /// </remarks>
        DictDel = 0x92,

        /// <summary>Drops every entry of a dictionary.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., dict -&gt; ...</c>
        /// </remarks>
        DictClear = 0x93,

        /// <summary>Collects a dictionary's keys into a new array of the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., dict -&gt; ..., array</c><br/>
        /// Notes: the array's own type has to be named here because it cannot be derived at run
        /// time - the dictionary knows <c>DIS</c>, but building <c>AI</c> from it would mean parsing
        /// a descriptor on every call. In the dictionary's own iteration order.
        /// </remarks>
        DictKeys = 0x94,

        /// <summary>Collects a dictionary's values into a new array of the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., dict -&gt; ..., array</c><br/>
        /// Notes: in the same order as <see cref="DictKeys"/>, so the two line up element for element.
        /// </remarks>
        DictValues = 0x95,

        /// <summary>Tests whether a dictionary holds a key.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., dict, key -&gt; ..., bool</c>
        /// </remarks>
        DictIn = 0x96,
        #endregion

        #region Type Tests and Casts

        /// <summary>Tests whether the top value is an instance of the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c><br/>
        /// Notes: the type is an immediate, not a stack operand. Resolves through the class's
        /// ancestor chain for classes and its interface table for interfaces.
        /// </remarks>
        InstanceOf = 0x97,

        /// <summary>Tests instance-of using a 4-byte type index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a -&gt; ..., bool</c>
        /// </remarks>
        InstanceOfX = 0x98,

        /// <summary>Casts the top value to the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a -&gt; ..., a</c><br/>
        /// Notes: the type is an immediate, not a stack operand. This is a checked reference cast:
        /// the value is unchanged on success; a failure traps as <c>InvalidCastException</c>. The
        /// non-throwing form is <see cref="CastOrNull"/>.
        /// </remarks>
        Cast = 0x99,

        /// <summary>Casts using a 4-byte type index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a -&gt; ..., a</c>
        /// </remarks>
        CastX = 0x9A,

        /// <summary>Keeps the top value if it is an instance of the type at <c>typeIdx</c>, and replaces it with null otherwise.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a -&gt; ..., a | null</c><br/>
        /// Notes: what <c>as?</c> lowers to. <see cref="Cast"/> traps on a mismatch and
        /// <see cref="InstanceOf"/> discards the value to answer about it, so a non-throwing cast written
        /// from those two costs a spill to a local, two type tests and a branch. This costs one type
        /// test. A null subject stays null, which is the same answer either way, and matching resolves
        /// through the ancestor chain for a class and the interface table for a contract, exactly as
        /// <see cref="Cast"/> does.
        /// </remarks>
        CastOrNull = 0x9B,

        /// <summary>Casts or yields null, with a 4-byte type index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a -&gt; ..., a | null</c>
        /// </remarks>
        CastOrNullX = 0x9C,

        /// <summary>Pushes the <c>Type</c> value for the compile-time-known type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., type</c><br/>
        /// Notes: what the static form of <c>typeof</c> lowers to - <c>typeof(SomeClass)</c> or
        /// <c>typeof(ISomeInterface)</c>, neither of which reads any value off the stack. The type
        /// is an immediate, resolved once at module load through <c>typeTable[typeIdx]</c> exactly
        /// as <see cref="InstanceOf"/> resolves its own. Allocates only the first time a given type
        /// is asked for on this runtime - the runtime caches one <c>Type</c> object per class or
        /// interface, so a repeated <c>typeof</c> on the same type is a cache hit, not a fresh
        /// entity every call.
        /// </remarks>
        LoadType = 0x9D,

        /// <summary>Loads the compile-time-known type's <c>Type</c> value, with a 4-byte type index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., type</c>
        /// </remarks>
        LoadTypeX = 0x9E,

        /// <summary>Reads the class of the value on top of the stack and pushes its <c>Type</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., ref -&gt; ..., type</c><br/>
        /// Notes: what the instance form of <c>typeof</c> lowers to when the operand's static type
        /// cannot say the answer by itself - reads <c>.Class</c> off the reference exactly as
        /// <see cref="InstanceOf"/>'s reference half does, no different for a boxed primitive than
        /// for an ordinary object. The subject is never checked for null, matching <c>FieldGet</c>
        /// and the native <c>Type.of</c> this replaces: a null reaching here is the compiler's
        /// nullability analysis failing to guard it, not a condition this opcode traps. A primitive
        /// operand never reaches this at all - its class can never differ from its static one, so
        /// the compiler lowers <c>typeof</c> straight to <see cref="LoadType"/> against that type
        /// instead, skipping both the box and this read.
        /// </remarks>
        GetTypeOfValue = 0x9F,
        #endregion

        #region Module Access

        /// <summary>Pushes the <c>Module</c> value for another module, named by its slot in the module table.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) moduleIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., module</c><br/>
        /// Notes: what <c>moduleof(ModulePath)</c> lowers to when the path names a module other
        /// than the one emitting it - the same module table <see cref="CallModule"/> already reads,
        /// interned once per distinct target by the emitter, so naming a module through
        /// <c>moduleof</c> and calling into it share one table entry. The target must already be
        /// loaded and linked. Allocates only the first time a given module is asked for on this
        /// runtime - see <see cref="LoadType"/>'s identical caching for <c>Type</c>.
        /// </remarks>
        LoadModule = 0xA0,

        /// <summary>Loads another module's <c>Module</c> value, with a 4-byte module index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) moduleIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., module</c>
        /// </remarks>
        LoadModuleX = 0xA1,

        /// <summary>Pushes the <c>Module</c> value for the module this frame's chunk belongs to.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., module</c><br/>
        /// Notes: what <c>moduleof(ModulePath)</c> lowers to when the path names the same module
        /// emitting it. A module does not reach itself through the module table - the same rule
        /// <see cref="CallLocalModule"/> already follows for a call - so this reads the owning
        /// module straight off the executing chunk instead of an index.
        /// </remarks>
        LoadCurrentModule = 0xA2,
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
        ObjNew = 0xA3,

        /// <summary>Allocates an instance using a 4-byte type index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., obj</c>
        /// </remarks>
        ObjNewX = 0xA4,
        #endregion

        #region Control Flow Operations

        /// <summary>Branches unconditionally.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c>
        /// </remarks>
        JP = 0xA5,

        /// <summary>Branches unconditionally, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c>
        /// </remarks>
        JPX = 0xA6,

        /// <summary>Branches if the popped condition is false.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., cond -&gt; ...</c><br/>
        /// Notes: the offset is signed and relative to the instruction following this one, so a
        /// negative value branches backwards - the shape of every loop.
        /// </remarks>
        JPZ = 0xA7,

        /// <summary>Branches if the popped condition is false, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., cond -&gt; ...</c>
        /// </remarks>
        JPZX = 0xA8,

        /// <summary>Branches if the popped condition is true.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., cond -&gt; ...</c>
        /// </remarks>
        JPNZ = 0xA9,

        /// <summary>Branches if the popped condition is true, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., cond -&gt; ...</c>
        /// </remarks>
        JPNZX = 0xAA,

        /// <summary>Branches if the popped value is the null reference.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        JPN = 0xAB,

        /// <summary>Branches if the popped value is null, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        JPNX = 0xAC,

        /// <summary>Branches if the popped value is a non-null reference.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        JPNN = 0xAD,

        /// <summary>Branches if the popped value is non-null, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        JPNNX = 0xAE,

        /// <summary>Pops a value and branches if it is an absent primitive.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a -&gt; ...</c><br/>
        /// Notes: what <c>??</c> and <c>?.</c> lower to over a nullable primitive.
        /// </remarks>
        JPA = 0xAF,

        /// <summary>Pops a value and branches if it is an absent primitive, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a -&gt; ...</c>
        /// </remarks>
        JPAX = 0xB0,

        /// <summary>Pops a value and branches if it is a present primitive.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a -&gt; ...</c>
        /// </remarks>
        JPNA = 0xB1,

        /// <summary>Pops a value and branches if it is a present primitive, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a -&gt; ...</c>
        /// </remarks>
        JPNAX = 0xB2,

        /// <summary>Branches if the two popped integers are equal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: fuses a comparison and a branch, so the boolean never reaches the stack. This is
        /// why the whole compare-and-branch family exists alongside the plain comparisons.
        /// </remarks>
        JPEQ = 0xB3,

        /// <summary>Branches if the two popped integers are equal, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPEQX = 0xB4,

        /// <summary>Branches if the two popped integers differ.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPNE = 0xB5,

        /// <summary>Branches if the two popped integers differ, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPNEX = 0xB6,

        /// <summary>Branches if the deeper popped integer is greater than the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: taken when <c>a &gt; b</c>.
        /// </remarks>
        JPGT = 0xB7,

        /// <summary>Branches on integer greater-than, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPGTX = 0xB8,

        /// <summary>Branches if the deeper popped integer is greater than or equal to the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPGE = 0xB9,

        /// <summary>Branches on integer greater-or-equal, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPGEX = 0xBA,

        /// <summary>Branches if the deeper popped integer is less than the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPLT = 0xBB,

        /// <summary>Branches on integer less-than, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPLTX = 0xBC,

        /// <summary>Branches if the deeper popped integer is less than or equal to the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPLE = 0xBD,

        /// <summary>Branches on integer less-or-equal, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPLEX = 0xBE,

        /// <summary>Branches if the two popped floats are equal.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: never taken when either operand is NaN.
        /// </remarks>
        JPFEQ = 0xBF,

        /// <summary>Branches if the two popped floats are equal, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPFEQX = 0xC0,

        /// <summary>Branches if the two popped floats differ.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: always taken when either operand is NaN.
        /// </remarks>
        JPFNE = 0xC1,

        /// <summary>Branches if the two popped floats differ, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPFNEX = 0xC2,

        /// <summary>Branches if the deeper popped float is greater than the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: never taken when either operand is NaN.
        /// </remarks>
        JPFGT = 0xC3,

        /// <summary>Branches on float greater-than, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPFGTX = 0xC4,

        /// <summary>Branches if the deeper popped float is greater than or equal to the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: never taken when either operand is NaN.
        /// </remarks>
        JPFGE = 0xC5,

        /// <summary>Branches on float greater-or-equal, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPFGEX = 0xC6,

        /// <summary>Branches if the deeper popped float is less than the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: never taken when either operand is NaN.
        /// </remarks>
        JPFLT = 0xC7,

        /// <summary>Branches on float less-than, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPFLTX = 0xC8,

        /// <summary>Branches if the deeper popped float is less than or equal to the top one.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c><br/>
        /// Notes: never taken when either operand is NaN.
        /// </remarks>
        JPFLE = 0xC9,

        /// <summary>Branches on float less-or-equal, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPFLEX = 0xCA,

        /// <summary>Branches if the two popped references are identical.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPREQ = 0xCB,

        /// <summary>Branches if the two popped references are identical, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPREQX = 0xCC,

        /// <summary>Branches if the two popped references are not identical.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPRNE = 0xCD,

        /// <summary>Branches if the two popped references are not identical, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPRNEX = 0xCE,

        /// <summary>Branches if the two popped strings hold the same text.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPStrEQ = 0xCF,

        /// <summary>Branches if the two popped strings hold the same text, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPStrEQX = 0xD0,

        /// <summary>Branches if the two popped strings hold different text.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(2)</c> - 3 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPStrNE = 0xD1,

        /// <summary>Branches if the two popped strings hold different text, with a 4-byte offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) relativeOffset(4)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a, b -&gt; ...</c>
        /// </remarks>
        JPStrNEX = 0xD2,

        /// <summary>Branches if the popped value is an instance of the type at <c>typeIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(2) relativeOffset(2)</c> - 5 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c><br/>
        /// Notes: carries two immediates, so this is the widest of the 2-byte-offset branches.
        /// Fuses <see cref="InstanceOf"/> with a branch, which is the shape a type switch compiles to.
        /// </remarks>
        JPInstanceOf = 0xD3,

        /// <summary>Branches on instance-of, with 4-byte type index and offset.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) typeIdx(4) relativeOffset(4)</c> - 9 bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c>
        /// </remarks>
        JPInstanceOfX = 0xD4,

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
        Switch = 0xD5,

        /// <summary>Branches by searching a sorted table of integer keys.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) count(4) defaultOffset(4) (key(4) offset(4)) * count</c> - 9 + 8n bytes.<br/>
        /// Stack: <c>..., value -&gt; ...</c><br/>
        /// Notes: the counterpart to <see cref="Switch"/> for sparse cases, where a dense table
        /// would be mostly padding. Keys must be sorted ascending; the interpreter binary-searches
        /// them, so lookup is logarithmic rather than the linear scan a chain of comparisons costs.
        /// Offsets are measured from this instruction's opcode byte.
        /// </remarks>
        SwitchLookup = 0xD6,
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
        CallLocalModule = 0xD7,

        /// <summary>Calls a function in the current module, with a 4-byte function index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) functionIdx(4) argsCount(1) retCount(1)</c> - 7 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c>
        /// </remarks>
        CallLocalModuleX = 0xD8,

        /// <summary>Calls a module-level function in another module.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) moduleIdx(2) functionIdx(2) argsCount(1) retCount(1)</c> - 7 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: the target module must already be loaded and linked.
        /// </remarks>
        CallModule = 0xD9,

        /// <summary>Calls a function in another module, with 4-byte module and function indices.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) moduleIdx(4) functionIdx(4) argsCount(1) retCount(1)</c> - 11 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: the longest instruction in the set.
        /// </remarks>
        CallModuleX = 0xDA,

        /// <summary>Invokes an instance method through the receiver's virtual method table.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) argsCount(1) retCount(1)</c> - 5 bytes.<br/>
        /// Stack: <c>..., obj, a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: the method table entry supplies a vtable slot, so dispatch is one load plus an
        /// indirect call - the receiver's runtime class decides which override runs. A null
        /// receiver hits the CLR null check and surfaces as `NullReferenceException`.
        /// </remarks>
        InvokeVirtual = 0xDB,

        /// <summary>Invokes an instance method without virtual dispatch.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) argsCount(1) retCount(1)</c> - 5 bytes.<br/>
        /// Stack: <c>..., obj, a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: binds exactly the method named in the table, ignoring any override. This is how
        /// constructors and explicit base calls are issued.
        /// </remarks>
        InvokeSpecial = 0xDC,

        /// <summary>Invokes a static method.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) argsCount(1) retCount(1)</c> - 5 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: no receiver is popped. It carries no type index: the method entry already knows
        /// its declaring class, and static initializers run when their module is loaded rather than
        /// on first touch, so there is nothing for the interpreter to trigger here.
        /// </remarks>
        InvokeStatic = 0xDD,

        /// <summary>Invokes a static method, with a 4-byte method index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(4) argsCount(1) retCount(1)</c> - 7 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., result?</c>
        /// </remarks>
        InvokeStaticX = 0xDE,

        /// <summary>Invokes a method through an interface contract.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) argsCount(1) retCount(1)</c> - 5 bytes.<br/>
        /// Stack: <c>..., obj, a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: resolves through the receiver class's interface dispatch table, which maps an
        /// interface slot to a vtable slot - one extra indirection over <see cref="InvokeVirtual"/>.
        /// </remarks>
        InvokeInterface = 0xDF,

        /// <summary>Calls a closure taken from the stack.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) argsCount(1) retCount(1)</c> - 3 bytes.<br/>
        /// Stack: <c>..., closure, a1, ..., aN -&gt; ..., result?</c><br/>
        /// Notes: the only call form with no index immediate - the target comes from the stack, so
        /// it is resolved entirely at run time. A null closure hits the CLR null check and surfaces as `NullReferenceException`.
        /// </remarks>
        InvokeClosure = 0xE0,

        /// <summary>Captures upvalues and builds a closure over the function at <c>functionIdx</c>.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) functionIdx(2) upvaluesCount(1)</c> - 4 bytes.<br/>
        /// Stack: <c>..., u1, ..., uN -&gt; ..., closure</c><br/>
        /// Notes: pops exactly <c>upvaluesCount</c> values, deepest becoming upvalue 0, which is
        /// the numbering <see cref="UpValueGet"/> uses. Caps captures at 255.
        /// </remarks>
        NewClosure = 0xE1,

        /// <summary>Builds a closure using a 4-byte function index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) functionIdx(4) upvaluesCount(1)</c> - 6 bytes.<br/>
        /// Stack: <c>..., u1, ..., uN -&gt; ..., closure</c>
        /// </remarks>
        NewClosureX = 0xE2,
        #endregion

        #region Function Operations

        /// <summary>
        /// Builds the canonical zero-capture function value for the method at <c>functionIdx</c>.
        /// </summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) functionIdx(2)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., ref</c><br/>
        /// Notes: the value is the one shared <c>SurtrClosure</c> for that method within this
        /// runtime - created, cached and rooted the first time any site asks - so nothing is
        /// allocated on the heap for an evaluation. It is the same type a capturing closure over
        /// the same signature has (it is a <c>SurtrClosure</c>), which is what lets a lambda that
        /// captures nothing coexist with one that does under one type. The compiler emits this only
        /// when the lambda is stateless; a body that captures anything still goes through
        /// <see cref="NewClosure"/>.
        /// </remarks>
        NewFunction = 0xE3,

        /// <summary>Builds a canonical function value using a 4-byte function index.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) functionIdx(4)</c> - 5 bytes.<br/>
        /// Stack: <c>... -&gt; ..., ref</c>
        /// </remarks>
        NewFunctionX = 0xE4,
        #endregion

        #region Return Operations

        /// <summary>Returns from the current function without a value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ...</c><br/>
        /// Notes: discards the frame; anything left on its operand stack is dropped.
        /// </remarks>
        ReturnVoid = 0xE5,

        /// <summary>Returns from the current function with a single value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., result -&gt; ...</c><br/>
        /// Notes: pops one value and hands it to the caller. A result wider than one slot returns
        /// through <see cref="ReturnValues"/> instead; this form is the one-slot case, which is
        /// every reference and every primitive.
        /// </remarks>
        ReturnValue = 0xE6,

        /// <summary>Returns from the current function with several contiguous values.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) n(1)</c> - 2 bytes.<br/>
        /// Stack: <c>..., s1, ..., sn -&gt; ...</c><br/>
        /// Notes: pops <c>n</c> contiguous slots and writes them at the frame base when the caller
        /// asked for a result (the call's <c>retCount</c> immediate != 0); discards them
        /// otherwise. What a method whose declared return type occupies more than one slot returns:
        /// the callee emits its own result slot count, so neither the call site nor the frame
        /// protocol changes - <c>retCount</c> still answers zero or one <em>results</em>, and the
        /// width of that one result is a fact about the callee's type. A one-slot return keeps
        /// using <see cref="ReturnValue"/>, and returning nothing stays <see cref="ReturnVoid"/>.
        /// </remarks>
        ReturnValues = 0xE7,
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
        Throw = 0xE8,
        #endregion

        #region Generator Operations

        /// <summary>Builds a generator over the body method at <c>methodIdx</c>, without running it.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1) methodIdx(2) typeIdx(2) argsCount(1)</c> - 6 bytes.<br/>
        /// Stack: <c>..., a1, ..., aN -&gt; ..., generator</c><br/>
        /// Notes: pops exactly <c>argsCount</c> values, receiver included, and stores them as the
        /// generator's incoming arguments; the body does not start until something resumes it, which
        /// is what makes calling a generator function free of side effects (§3.1). <c>methodIdx</c>
        /// names the compiler's hidden body method, never the stub that emits this - the stub is an
        /// ordinary method whose declared return is <c>Y&lt;elem&gt;</c>, which is what keeps every
        /// call site to a generator an ordinary call. <c>typeIdx</c> carries the whole parameterised
        /// type (<c>YI</c>, <c>YS</c>) for the same reason <see cref="ArrNew"/> does: the body's own
        /// return descriptor says nothing about what it yields, and the object has to keep its type.
        /// Allocates, so it routes through the safepoint.
        /// </remarks>
        GenNew = 0xE9,

        /// <summary>Checks that a generator has not been iterated yet, leaving it on the stack.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., generator -&gt; ..., generator</c><br/>
        /// Notes: the fast path's counterpart to what <c>iterate()</c> checks, emitted once per loop
        /// rather than per element. A generator object is single-use, so walking one that has already
        /// started would silently iterate nothing; this traps with
        /// <c>InvalidOperationException</c> instead. Calling the generator <em>function</em> again is
        /// what restarts (§12.2).
        /// </remarks>
        GenIterate = 0xEA,

        /// <summary>Resumes a generator until its next <c>yield</c> or its end.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., generator -&gt; ..., bool</c><br/>
        /// Notes: <see langword="true"/> if the body yielded, in which case the value is held on the
        /// generator and <see cref="GenCurrent"/> reads it; <see langword="false"/> if it finished.
        /// The frame is rebuilt in the running machine rather than through a nested run, so a
        /// resume costs a block copy and a frame entry. Resuming a generator that is already running
        /// traps with <c>InvalidOperationException</c>; resuming an exhausted one answers
        /// <see langword="false"/>.
        /// </remarks>
        GenResume = 0xEB,

        /// <summary>Reads the value a generator's last <c>yield</c> produced.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., generator -&gt; ..., value</c><br/>
        /// Notes: only meaningful after a <see cref="GenResume"/> that answered
        /// <see langword="true"/>. Reads the same field the native <c>current</c> accessor reads, so
        /// the two paths cannot disagree about what the last element was.
        /// </remarks>
        GenCurrent = 0xEC,

        /// <summary>Suspends the executing generator, handing back one value.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., value -&gt; </c> (the frame is suspended, not popped to a result)<br/>
        /// Notes: copies the live frame - locals plus pending operands - into the generator, records
        /// where to resume, and returns control to whoever resumed it. To the resumer this behaves
        /// exactly like a return, which is what lets one <c>Yield</c> serve both the compiled fast
        /// path and a resume that came from host code. Legal only in a body reached through
        /// <see cref="GenResume"/>; the compiler guarantees that by never emitting it anywhere else.
        /// </remarks>
        Yield = 0xED,

        /// <summary>Delegates the executing generator to another one, and resumes that one now.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>..., inner -&gt; </c> (the outer frame is suspended, and the inner one entered)<br/>
        /// Notes: what <c>yield from</c> lowers to when the operand is statically a generator. The
        /// outer generator's frame is copied out once and a link to <c>inner</c> recorded on it;
        /// from then on a resume walks the chain straight to the innermost generator that still has
        /// a frame, so an N-deep delegation costs one frame copy per element rather than N. When
        /// the inner is exhausted the link is cleared and the outer continues after this
        /// instruction. Delegating to a generator that is already running - directly or around a
        /// cycle - traps with <c>InvalidOperationException</c>, which is what the <c>Running</c>
        /// state is for. Any other iterable is lowered to a loop of ordinary <see cref="Yield"/>s
        /// instead, since there is no frame to link to.
        /// </remarks>
        GenDelegate = 0xEE,

        /// <summary>Pushes the value the executing generator's last suspension was resumed with.</summary>
        /// <remarks>
        /// Encoding: <c>opcode(1)</c> - 1 byte.<br/>
        /// Stack: <c>... -&gt; ..., value</c><br/>
        /// Notes: what makes <c>yield</c> and <c>yield from</c> <em>expressions</em> (§3.7). The
        /// statement forms emit <see cref="Yield"/> or <see cref="GenDelegate"/> alone and pay
        /// nothing for this, which is why the suspension opcodes keep the stack effect they were
        /// given rather than growing one. One opcode covers both because the value is the same
        /// question twice: for a <c>yield</c> it is what <c>send(v)</c> injected, and for a
        /// <c>yield from</c> it is what the delegated-to generator returned - in each case "the
        /// value that flowed back in when this suspension ended". Reads
        /// <c>SurtrGenerator.Resumed</c>, which every resumption that carries nothing clears, so a
        /// stale injection can never be read as a fresh one. Legal only in a generator body; the
        /// compiler guarantees that by never emitting it anywhere else.
        /// </remarks>
        GenResumed = 0xEF,
        #endregion

    }
}
