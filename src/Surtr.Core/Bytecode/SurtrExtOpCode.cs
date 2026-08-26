#nullable enable

namespace Surtr.Bytecode
{
    /// <summary>
    /// The extended instruction set: everything reached through the <see cref="OpCode.Ext"/>
    /// prefix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An extended instruction is <c>0xFF sub(1) &lt;immediates&gt;</c>. The values here are a
    /// second, independent 256-value space, documented exactly like the primary set - Encoding,
    /// Stack, Notes - and equally final: a value written into an image means one thing forever.
    /// <c>0xFF</c> is reserved as a second prefix, so this space can itself be extended without
    /// another format decision.
    /// </para>
    /// <para>
    /// <b>The admission rule.</b> A prefixed instruction costs one extra byte, one extra load and
    /// one extra indirect branch over a primary one - about one dispatch. So an extended opcode
    /// earns its place only by saving <b>two or more</b> dispatches: this space is for
    /// superinstructions that collapse a whole emitted sequence into one, and never for a
    /// specialisation whose entire benefit is removing a type test or a tag compare. Those would
    /// run *slower* here than the generic opcode they replace. A specialisation reaches this space
    /// only fused with the operand loads around it, where the fusion is what pays.
    /// </para>
    /// <para>
    /// <b>Contract.</b> Every member here must: charge the step budget through <c>Branched</c> if
    /// it transfers control; carry a 4-byte-offset <c>X</c> twin if it branches, so jump
    /// relaxation can widen it; measure offsets from the end of the instruction, prefix included;
    /// and take slot operands in one byte, which the emitter guarantees by falling back to the
    /// classic sequence when a slot does not fit.
    /// </para>
    /// <para>
    /// <c>docs/Plan-Opcodes-Extendidos.md</c> carries the cost model, the catalogue and the
    /// measurement protocol behind all of it.
    /// </para>
    /// </remarks>
    public enum SurtrExtOpCode : byte
    {
        /// <summary>Pushes a local, exactly as <see cref="OpCode.LdlS"/> does.</summary>
        /// <remarks>
        /// Encoding: <c>0xFF sub(1) localIdx(1)</c> - 3 bytes.<br/>
        /// Stack: <c>... -&gt; ..., value</c><br/>
        /// Notes: a deliberate duplicate, and the one member of this space that is not meant to
        /// be useful. The compiler never emits it. It exists to <em>measure</em> the prefix: the
        /// admission rule above rests on "a nested indirect branch costs about one dispatch",
        /// which is an estimate until something weighs it. Emitting a hot loop's local loads
        /// through this instead of <see cref="OpCode.LdlS"/> changes exactly one thing - the
        /// dispatch path - so the delta is the prefix's price with nothing else mixed in.
        /// Keeping it in the set means that price can be re-measured on new hardware or a new
        /// runtime rather than assumed from the last time anyone checked.
        /// </remarks>
        Probe = 0x00,
    }
}
