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

        #region Loop steps

        /// <summary>Advances an indexed walk over an array: step, test, fetch and branch back.</summary>
        /// <remarks>
        /// Encoding: <c>0xFF sub(1) srcSlot(1) idxSlot(1) varSlot(1) offset(2)</c> - 7 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c> (nothing is touched; the whole step is slot to slot)<br/>
        /// Notes: the whole per-element cost of a <c>for-in</c> over an array in one instruction.
        /// Written out, that step is <c>Ldl idx · Ldl src · ArrLen · JPGE end · Ldl src · Ldl idx ·
        /// ArrGet · Stl var</c> at the top and <c>IncLocal · Jump</c> at the bottom - ten
        /// dispatches of overhead per element, which this collapses to one.
        /// <para>
        /// Increments the index, reloads the array's <c>Count</c> (the body may have pushed, and
        /// the walk is defined to see that), and if the index is still in range writes the element
        /// into <c>varSlot</c> and branches back by <c>offset</c>. Otherwise it falls through,
        /// which is the loop's exit - so the emitter places this at the *bottom* of the loop with
        /// the exit label immediately after it, and enters through a jump with the index at -1.
        /// </para>
        /// <para>
        /// There is no bounds trap here and none is being skipped: the test that decides whether to
        /// continue is the same test that would have checked the read, so the read cannot be out of
        /// range. That is what makes this a fusion rather than an unchecked opcode.
        /// </para>
        /// <para>
        /// Emitted only when the loop variable occupies one slot; a wider one still needs the
        /// unpack the general lowering performs.
        /// </para>
        /// </remarks>
        ArrForNext = 0x01,

        /// <summary>The 4-byte-offset form of <see cref="ArrForNext"/>.</summary>
        /// <remarks>
        /// Encoding: <c>0xFF sub(1) srcSlot(1) idxSlot(1) varSlot(1) offset(4)</c> - 9 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c><br/>
        /// Notes: what jump relaxation rewrites <see cref="ArrForNext"/> into when the body it has
        /// to reach back over outgrew a signed 2-byte offset.
        /// </remarks>
        ArrForNextX = 0x02,

        /// <summary>Advances an indexed walk over a string. See <see cref="ArrForNext"/>.</summary>
        /// <remarks>
        /// Encoding: <c>0xFF sub(1) srcSlot(1) idxSlot(1) varSlot(1) offset(2)</c> - 7 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c><br/>
        /// Notes: the element written into <c>varSlot</c> is a character.
        /// </remarks>
        StrForNext = 0x03,

        /// <summary>The 4-byte-offset form of <see cref="StrForNext"/>.</summary>
        /// <remarks>
        /// Encoding: <c>0xFF sub(1) srcSlot(1) idxSlot(1) varSlot(1) offset(4)</c> - 9 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c>
        /// </remarks>
        StrForNextX = 0x04,

        /// <summary>Advances an indexed walk over a tuple. See <see cref="ArrForNext"/>.</summary>
        /// <remarks>
        /// Encoding: <c>0xFF sub(1) srcSlot(1) idxSlot(1) varSlot(1) offset(2)</c> - 7 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c><br/>
        /// Notes: the source is the boxed tuple the lowering packs once at loop entry, since the
        /// frame has no addressing mode for a dynamic offset into a local range.
        /// </remarks>
        TupForNext = 0x05,

        /// <summary>The 4-byte-offset form of <see cref="TupForNext"/>.</summary>
        /// <remarks>
        /// Encoding: <c>0xFF sub(1) srcSlot(1) idxSlot(1) varSlot(1) offset(4)</c> - 9 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c>
        /// </remarks>
        TupForNextX = 0x06,

        /// <summary>Advances a walk over a dictionary's key snapshot, yielding the pair.</summary>
        /// <remarks>
        /// Encoding: <c>0xFF sub(1) keysSlot(1) idxSlot(1) dictSlot(1) pairSlot(1) offset(2)</c> - 8 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c><br/>
        /// Notes: the largest fusion in the set, and the one with most to gain. Written out, a
        /// dictionary walk's step is <b>seventeen</b> dispatches: four to guard the index against
        /// the snapshot's length, four to read the key out of it, four to look the value up, three
        /// to lay the pair into the loop variable's two slots, and two to step and jump.
        /// <para>
        /// Increments the index, and while it is inside the key snapshot: reads the key, resolves
        /// its value in the dictionary, writes the two of them into <c>pairSlot</c> and
        /// <c>pairSlot + 1</c> - the loop variable is always a <c>(K, V)</c> pair, so this is the
        /// one member of the family that writes two slots - and branches back. The key/value
        /// temporaries the written-out form needed disappear with it.
        /// </para>
        /// <para>
        /// The absent-key trap is kept, because it stays reachable: the body may delete a key the
        /// snapshot still lists.
        /// </para>
        /// </remarks>
        DictForNext = 0x07,

        /// <summary>The 4-byte-offset form of <see cref="DictForNext"/>.</summary>
        /// <remarks>
        /// Encoding: <c>0xFF sub(1) keysSlot(1) idxSlot(1) dictSlot(1) pairSlot(1) offset(4)</c> - 10 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c>
        /// </remarks>
        DictForNextX = 0x08,

        /// <summary>Advances a counted loop over an inclusive range: step, test and branch back.</summary>
        /// <remarks>
        /// Encoding: <c>0xFF sub(1) varSlot(1) limitSlot(1) offset(2)</c> - 6 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c><br/>
        /// Notes: increments the loop variable <em>unconditionally</em> - which is what
        /// <c>IncLocal</c> followed by a top-of-loop guard did, so the value left behind is the
        /// same - and branches back while it is <c>&lt;=</c> the limit. Overflow wraps, exactly as
        /// the written-out form wrapped.
        /// <para>
        /// Unlike the indexed walks, the range family cannot rotate its entry: the loop variable is
        /// the one the program declared, and starting it one below its bound would wrap at
        /// <c>int.MinValue</c>. So the header guard stays for the first iteration and only the step
        /// is fused - five dispatches into one instead of ten.
        /// </para>
        /// <para>
        /// There are two of these rather than one opcode plus a normalised limit, because
        /// normalising an exclusive bound means <c>limit - 1</c>, which wraps at
        /// <c>int.MinValue</c> - the exact case the escaped-range lowering already handles by hand.
        /// The emitter knows statically which form it has, and the extended space is large enough
        /// that spending a second value is cheaper than moving the hazard into the prologue.
        /// </para>
        /// </remarks>
        ForRangeNextLE = 0x09,

        /// <summary>The 4-byte-offset form of <see cref="ForRangeNextLE"/>.</summary>
        /// <remarks>
        /// Encoding: <c>0xFF sub(1) varSlot(1) limitSlot(1) offset(4)</c> - 8 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c>
        /// </remarks>
        ForRangeNextLEX = 0x0A,

        /// <summary>Advances a counted loop over an exclusive range. See <see cref="ForRangeNextLE"/>.</summary>
        /// <remarks>
        /// Encoding: <c>0xFF sub(1) varSlot(1) limitSlot(1) offset(2)</c> - 6 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c><br/>
        /// Notes: branches back while the incremented variable is <c>&lt;</c> the limit.
        /// </remarks>
        ForRangeNextLT = 0x0B,

        /// <summary>The 4-byte-offset form of <see cref="ForRangeNextLT"/>.</summary>
        /// <remarks>
        /// Encoding: <c>0xFF sub(1) varSlot(1) limitSlot(1) offset(4)</c> - 8 bytes.<br/>
        /// Stack: <c>... -&gt; ...</c>
        /// </remarks>
        ForRangeNextLTX = 0x0C,

        #endregion
    }
}
