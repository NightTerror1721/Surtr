#nullable enable

using System;
using System.Collections.Generic;

namespace Surtr.Bytecode.Emit
{
    /// <summary>
    /// Assembles the instruction stream of a single method: raw bytes, labels and branches, and
    /// the operand-stack bookkeeping the frame protocol needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The surface comes in three tiers, and a compiler is expected to live almost entirely in the
    /// third:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>Raw</b> - <see cref="Emit(OpCode, int, int)"/>, <see cref="EmitU8"/>,
    ///     <see cref="EmitI16"/>, <see cref="EmitI32"/>. Writes exactly what it is told and nothing
    ///     else; the escape hatch for anything the tiers above do not cover.
    ///   </description></item>
    ///   <item><description>
    ///     <b>One method per opcode</b>, named after the opcode and taking its exact operands, in
    ///     <c>SurtrCodeEmitter.OpCodes.cs</c>. These never substitute one encoding for another: a
    ///     call to <c>JPZ</c> emits <c>JPZ</c>, and fails if its target is out of reach.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Grouped helpers</b>, in <c>SurtrCodeEmitter.Helpers.cs</c>, which pick the encoding -
    ///     <see cref="LoadLocal(SurtrLocal)"/> chooses between <c>Ldl0</c>…<c>Ldl5</c>,
    ///     <c>LdlS</c> and <c>Ldl</c>; <see cref="Jump"/> chooses between <c>JP</c> and
    ///     <c>JPX</c> once the body's layout is known.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Two things are tracked as the stream is built, because neither can be recovered afterwards
    /// and both are load-bearing:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Operand stack depth.</b> Every tier-two method declares what it pops and pushes, which
    ///     yields <see cref="MaxStackDepth"/> - the value a frame is sized from, and the subject of
    ///     the interpreter's only stack-overflow check. Getting it wrong by hand corrupts the stack
    ///     with nothing to catch it, so it is computed rather than asked for.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Depth agreement at labels.</b> Every path reaching a label must arrive with the same
    ///     depth. A mismatch is a compiler bug, and it surfaces here - at the instruction that
    ///     caused it - instead of as a stack that slowly slides out of alignment at run time.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Branch offsets are never written when a branch is emitted. Labels are resolved in
    /// <see cref="Finish"/>, after a relaxation pass has settled which branches need the wide form:
    /// widening one branch moves everything after it, which can push a second branch out of reach,
    /// so the pass runs to a fixed point. That is also why a protected region records its bounds as
    /// labels rather than positions.
    /// </para>
    /// </remarks>
    public sealed partial class SurtrCodeEmitter
    {
        /// <summary>A branch whose offset immediate is filled in once its target is known.</summary>
        /// <remarks>
        /// A class rather than a struct so the relaxation pass can update entries in place while
        /// walking them. This is load-time code and runs once per method, so the allocation is not
        /// on any path that matters.
        /// </remarks>
        private sealed class JumpPatch
        {
            /// <summary>Position of the instruction's opcode byte.</summary>
            public int InstructionStart;

            /// <summary>Position the offset immediate is written at.</summary>
            public int OperandPosition;

            /// <summary>Width of the offset immediate, in bytes: 2 or 4.</summary>
            public int Width;

            /// <summary>The label this branch targets.</summary>
            public int Label;

            /// <summary>The opcode as currently emitted.</summary>
            public OpCode Current;

            /// <summary>The form to rewrite into when the short offset does not reach.</summary>
            public OpCode Wide;

            /// <summary>Width of the type index preceding the offset, or 0 when there is none.</summary>
            public int TypeOperandWidth;

            /// <summary>
            /// Whether the offset is measured from the instruction's own opcode byte rather than
            /// from the byte after it. True only for <c>Switch</c> and <c>SwitchLookup</c>.
            /// </summary>
            public bool RelativeToInstruction;

            /// <summary>Whether the caller pinned the width, so relaxation must leave it alone.</summary>
            public bool Pinned;

            /// <summary>Set by the relaxation pass between deciding to widen and doing it.</summary>
            public bool Widening;

            /// <summary>Whether this branch is a candidate for widening.</summary>
            public bool CanWiden => !Pinned && Width == 2;
        }

        private readonly SurtrMethodBuilder _method;
        private readonly SurtrModuleBuilder _module;

        private readonly List<byte> _code = new List<byte>(64);
        private readonly List<int> _labelPositions = new List<int>();
        private readonly List<int> _labelDepths = new List<int>();
        private readonly List<JumpPatch> _patches = new List<JumpPatch>();

        private int _depth;
        private int _maxDepth;
        private bool _reachable = true;
        private bool _finished;

        internal SurtrCodeEmitter(SurtrMethodBuilder method)
        {
            _method = method;
            _module = method.Module;
        }

        /// <summary>The method this stream belongs to.</summary>
        public SurtrMethodBuilder Method => _method;

        /// <summary>The next byte offset that will be written, relative to the method's own start.</summary>
        /// <remarks>
        /// Only meaningful while emitting: widening a branch shifts everything after it, so a
        /// position taken here does not survive <see cref="Finish"/>. Use a label for anything that
        /// has to.
        /// </remarks>
        public int Position => _code.Count;

        /// <summary>How deep the operand stack is at the point about to be emitted.</summary>
        public int StackDepth => _depth;

        /// <summary>The deepest the operand stack gets anywhere in the body so far.</summary>
        /// <remarks>This is what a frame's <c>MaxStackSize</c> is taken from.</remarks>
        public int MaxStackDepth => _maxDepth;

        /// <summary>
        /// Whether control can reach the next instruction, or the previous one was an
        /// unconditional transfer and nothing has marked a label since.
        /// </summary>
        public bool IsReachable => _reachable;

        /// <summary>Whether <see cref="Finish"/> has run and the stream is frozen.</summary>
        public bool IsFinished => _finished;

        #region Labels

        /// <summary>Allocates a label, to be fixed to a position later.</summary>
        public SurtrLabel NewLabel()
        {
            _labelPositions.Add(-1);
            _labelDepths.Add(-1);
            return new SurtrLabel(_labelPositions.Count - 1);
        }

        /// <summary>
        /// Fixes <paramref name="label"/> to the current position and joins the control flow
        /// arriving there with the flow falling into it.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The label was already marked, or the paths reaching it disagree on stack depth.
        /// </exception>
        public SurtrCodeEmitter MarkLabel(SurtrLabel label)
        {
            int id = ValidateLabel(label);
            ThrowIfMarked(id);

            if (_reachable)
            {
                RecordLabelDepth(id, _depth);
            }
            else
            {
                // Nothing falls into this label, so its depth is whatever the branches targeting it
                // agreed on. A label nothing targets either is genuinely dead code, which starts at
                // an empty stack because no path ever reaches it.
                _depth = _labelDepths[id] >= 0 ? _labelDepths[id] : 0;
                _reachable = true;
            }

            _labelPositions[id] = _code.Count;
            return this;
        }

        /// <summary>
        /// Fixes <paramref name="label"/> to the current position as the first instruction of an
        /// exception handler.
        /// </summary>
        /// <remarks>
        /// A handler is entered by the unwinder rather than by a branch, and it is entered with the
        /// frame's operand stack cleared and the raised object pushed onto it - so its depth is
        /// exactly one, whatever the protected region left behind.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The label was already marked, or something branches to it at a depth other than one.</exception>
        public SurtrCodeEmitter MarkHandler(SurtrLabel label)
        {
            int id = ValidateLabel(label);
            ThrowIfMarked(id);

            if (_labelDepths[id] >= 0 && _labelDepths[id] != 1)
                throw new InvalidOperationException(
                    $"Handler label {label} is also branched to with a stack depth of {_labelDepths[id]}; a handler always starts at depth 1.");

            _labelDepths[id] = 1;
            _labelPositions[id] = _code.Count;
            _depth = 1;
            _reachable = true;

            if (_maxDepth < 1)
                _maxDepth = 1;

            return this;
        }

        /// <summary>
        /// Fixes <paramref name="label"/> to the current position without touching control flow or
        /// stack tracking.
        /// </summary>
        /// <remarks>
        /// For offsets that have to survive relaxation but are not branch targets - the bounds of a
        /// protected region, say, which the handler table records but nothing jumps to.
        /// </remarks>
        public SurtrCodeEmitter MarkPosition(SurtrLabel label)
        {
            int id = ValidateLabel(label);
            ThrowIfMarked(id);
            _labelPositions[id] = _code.Count;
            return this;
        }

        /// <summary>Whether <paramref name="label"/> has been fixed to a position.</summary>
        public bool IsMarked(SurtrLabel label) => _labelPositions[ValidateLabel(label)] >= 0;

        /// <summary>
        /// The byte offset <paramref name="label"/> resolved to, relative to the method's own start.
        /// </summary>
        /// <exception cref="InvalidOperationException">The stream has not been finished yet, so positions are still provisional.</exception>
        public int PositionOf(SurtrLabel label)
        {
            if (!_finished)
                throw new InvalidOperationException("Label positions are only final once the method's code has been finished.");

            return _labelPositions[ValidateLabel(label)];
        }

        private int ValidateLabel(SurtrLabel label)
        {
            if (!label.IsValid)
                throw new ArgumentException("Label was not allocated by an emitter.", nameof(label));

            int id = label.Id;
            if ((uint)id >= (uint)_labelPositions.Count)
                throw new ArgumentException($"Label {label} does not belong to this emitter.", nameof(label));

            return id;
        }

        private void ThrowIfMarked(int id)
        {
            if (_labelPositions[id] >= 0)
                throw new InvalidOperationException($"Label L{id} has already been marked at offset {_labelPositions[id]}.");
        }

        private void RecordLabelDepth(int id, int depth)
        {
            int existing = _labelDepths[id];
            if (existing < 0)
                _labelDepths[id] = depth;
            else if (existing != depth)
                throw new InvalidOperationException(
                    $"Paths reaching label L{id} disagree on operand stack depth: {existing} versus {depth}.");
        }

        #endregion

        #region Stack tracking

        /// <summary>
        /// Accounts for an instruction that removes <paramref name="pop"/> operands and leaves
        /// <paramref name="push"/> behind.
        /// </summary>
        /// <remarks>
        /// Every tier-two method calls this for itself. It is public so that code emitted through
        /// the raw tier can keep the tracker honest; passing the wrong numbers there produces a
        /// wrong <see cref="MaxStackDepth"/>, which is exactly the thing the tracker exists to
        /// prevent.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The instruction would pop more than the stack holds.</exception>
        public SurtrCodeEmitter AdjustStack(int pop, int push)
        {
            Track(pop, push);
            return this;
        }

        private void Track(int pop, int push)
        {
            // Unreachable code never runs, so it can neither underflow the stack nor raise the high
            // water mark a frame is sized from. Tracking it would only invent constraints.
            if (!_reachable)
                return;

            if (pop > _depth)
                throw new InvalidOperationException(
                    $"Operand stack underflow at offset {_code.Count} in '{_method.Name}': the instruction pops {pop} but the stack holds {_depth}.");

            _depth = _depth - pop + push;

            if (_depth > _maxDepth)
                _maxDepth = _depth;
        }

        /// <summary>Marks the following instructions as unreachable, until the next label.</summary>
        private void EndFlow() => _reachable = false;

        #endregion

        #region Raw emission

        /// <summary>Writes an opcode byte and accounts for its stack effect.</summary>
        /// <remarks>
        /// The raw tier. Immediates are the caller's to write with <see cref="EmitU8"/>,
        /// <see cref="EmitI16"/> and <see cref="EmitI32"/>, and nothing here validates that they
        /// match the opcode.
        /// </remarks>
        public SurtrCodeEmitter Emit(OpCode op, int pop = 0, int push = 0)
        {
            ThrowIfFinished();
            Track(pop, push);
            _code.Add((byte)op);
            return this;
        }

        /// <summary>Writes a single immediate byte.</summary>
        public SurtrCodeEmitter EmitU8(int value)
        {
            ThrowIfFinished();
            _code.Add((byte)value);
            return this;
        }

        /// <summary>Writes a 2-byte little-endian immediate.</summary>
        public SurtrCodeEmitter EmitI16(int value)
        {
            ThrowIfFinished();
            _code.Add((byte)value);
            _code.Add((byte)(value >> 8));
            return this;
        }

        /// <summary>Writes a 4-byte little-endian immediate.</summary>
        public SurtrCodeEmitter EmitI32(int value)
        {
            ThrowIfFinished();
            _code.Add((byte)value);
            _code.Add((byte)(value >> 8));
            _code.Add((byte)(value >> 16));
            _code.Add((byte)(value >> 24));
            return this;
        }

        private void ThrowIfFinished()
        {
            if (_finished)
                throw new InvalidOperationException($"Method '{_method.Name}' has already been finished and can no longer be emitted into.");
        }

        #endregion

        #region Internal emission primitives

        /// <summary>An opcode with no immediates.</summary>
        private SurtrCodeEmitter Simple(OpCode op, int pop, int push)
        {
            ThrowIfFinished();
            Track(pop, push);
            _code.Add((byte)op);
            return this;
        }

        /// <summary>An opcode followed by one unsigned byte.</summary>
        private SurtrCodeEmitter WithU8(OpCode op, int value, int pop, int push, string operand)
        {
            ThrowIfFinished();
            CheckRange(value, 0, byte.MaxValue, op, operand);
            Track(pop, push);
            _code.Add((byte)op);
            _code.Add((byte)value);
            return this;
        }

        /// <summary>An opcode followed by one unsigned 2-byte index.</summary>
        private SurtrCodeEmitter WithU16(OpCode op, int value, int pop, int push, string operand)
        {
            ThrowIfFinished();
            CheckRange(value, 0, ushort.MaxValue, op, operand);
            Track(pop, push);
            _code.Add((byte)op);
            _code.Add((byte)value);
            _code.Add((byte)(value >> 8));
            return this;
        }

        /// <summary>An opcode followed by one 4-byte index.</summary>
        private SurtrCodeEmitter WithI32(OpCode op, int value, int pop, int push)
        {
            ThrowIfFinished();
            Track(pop, push);
            _code.Add((byte)op);
            AppendI32(_code, value);
            return this;
        }

        private static void CheckRange(int value, int min, int max, OpCode op, string operand)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(
                    operand, value, $"{op} takes a {operand} in [{min}, {max}].");
        }

        private static void AppendI32(List<byte> code, int value)
        {
            code.Add((byte)value);
            code.Add((byte)(value >> 8));
            code.Add((byte)(value >> 16));
            code.Add((byte)(value >> 24));
        }

        /// <summary>
        /// Emits a branch whose offset is measured from the byte after it, in whichever width
        /// <paramref name="width"/> asks for.
        /// </summary>
        private SurtrCodeEmitter Branch(OpCode shortOp, OpCode wideOp, SurtrLabel label, int pop, SurtrJumpWidth width, bool endsFlow)
        {
            ThrowIfFinished();

            int id = ValidateLabel(label);
            Track(pop, 0);

            if (_reachable)
                RecordLabelDepth(id, _depth);

            bool wide = width == SurtrJumpWidth.Wide;
            int start = _code.Count;

            _code.Add((byte)(wide ? wideOp : shortOp));

            var patch = new JumpPatch
            {
                InstructionStart = start,
                OperandPosition = _code.Count,
                Width = wide ? 4 : 2,
                Label = id,
                Current = wide ? wideOp : shortOp,
                Wide = wideOp,
                TypeOperandWidth = 0,
                RelativeToInstruction = false,
                Pinned = width != SurtrJumpWidth.Auto,
            };

            if (wide)
                AppendI32(_code, 0);
            else
            {
                _code.Add(0);
                _code.Add(0);
            }

            _patches.Add(patch);

            if (endsFlow)
                EndFlow();

            return this;
        }

        /// <summary>Emits <c>JPInstanceOf</c>, which carries a type index ahead of its offset.</summary>
        private SurtrCodeEmitter BranchInstanceOf(SurtrTypeToken type, SurtrLabel label, SurtrJumpWidth width)
        {
            ThrowIfFinished();

            int id = ValidateLabel(label);
            int typeIndex = TypeIndex(type);
            Track(1, 0);

            if (_reachable)
                RecordLabelDepth(id, _depth);

            bool wide = width == SurtrJumpWidth.Wide || typeIndex > ushort.MaxValue;
            int start = _code.Count;

            _code.Add((byte)(wide ? OpCode.JPInstanceOfX : OpCode.JPInstanceOf));

            if (wide)
                AppendI32(_code, typeIndex);
            else
            {
                _code.Add((byte)typeIndex);
                _code.Add((byte)(typeIndex >> 8));
            }

            var patch = new JumpPatch
            {
                InstructionStart = start,
                OperandPosition = _code.Count,
                Width = wide ? 4 : 2,
                Label = id,
                Current = wide ? OpCode.JPInstanceOfX : OpCode.JPInstanceOf,
                Wide = OpCode.JPInstanceOfX,
                TypeOperandWidth = wide ? 4 : 2,
                RelativeToInstruction = false,
                Pinned = width != SurtrJumpWidth.Auto,
            };

            if (wide)
                AppendI32(_code, 0);
            else
            {
                _code.Add(0);
                _code.Add(0);
            }

            _patches.Add(patch);
            return this;
        }

        /// <summary>Records one of a switch's offsets, which are measured from its opcode byte.</summary>
        private void SwitchEntry(int instructionStart, SurtrLabel label)
        {
            int id = ValidateLabel(label);

            if (_reachable)
                RecordLabelDepth(id, _depth);

            _patches.Add(new JumpPatch
            {
                InstructionStart = instructionStart,
                OperandPosition = _code.Count,
                Width = 4,
                Label = id,
                Current = OpCode.Nop,
                Wide = OpCode.Nop,
                TypeOperandWidth = 0,
                RelativeToInstruction = true,
                Pinned = true,
            });

            AppendI32(_code, 0);
        }

        private int TypeIndex(SurtrTypeToken token)
        {
            if (!token.IsValid)
                throw new ArgumentException("Type token was not obtained from a module builder.", nameof(token));
            return token.Index;
        }

        #endregion

        #region Resolution

        /// <summary>
        /// Settles every branch width, writes every offset, and freezes the stream.
        /// </summary>
        /// <returns>The method's instruction bytes, ready to be copied into the module's chunk.</returns>
        /// <remarks>
        /// Called once, by <see cref="SurtrModuleBuilder.Build"/>. After this,
        /// <see cref="PositionOf"/> answers with final offsets, which is what the exception handler
        /// table is built from.
        /// </remarks>
        /// <exception cref="InvalidOperationException">A branch targets a label that was never marked, or a pinned short branch cannot reach its target.</exception>
        internal byte[] Finish()
        {
            if (_finished)
                throw new InvalidOperationException($"Method '{_method.Name}' has already been finished.");

            foreach (var patch in _patches)
            {
                if (_labelPositions[patch.Label] < 0)
                    throw new InvalidOperationException(
                        $"Label L{patch.Label} is branched to in '{_method.Name}' but was never marked.");
            }

            Relax();

            foreach (var patch in _patches)
            {
                int distance = DistanceOf(patch);

                if (patch.Width == 2 && (distance < short.MinValue || distance > short.MaxValue))
                    throw new InvalidOperationException(
                        $"{patch.Current} at offset {patch.InstructionStart} in '{_method.Name}' cannot reach its target: " +
                        $"{distance} bytes away, and a 2-byte offset only reaches ±32767. Emit the wide form, or let the emitter pick with SurtrJumpWidth.Auto.");

                WriteLittleEndian(patch.OperandPosition, distance, patch.Width);
            }

            _finished = true;
            return _code.ToArray();
        }

        /// <summary>
        /// Widens every 2-byte branch that cannot reach, repeatedly, until none is left.
        /// </summary>
        /// <remarks>
        /// One pass is not enough: widening a branch inserts bytes, which moves everything after it
        /// and can push a second branch out of reach. Each round grows the code, and there are
        /// finitely many branches to widen, so the loop terminates.
        /// </remarks>
        private void Relax()
        {
            while (true)
            {
                List<JumpPatch>? widening = null;

                foreach (var patch in _patches)
                {
                    if (!patch.CanWiden)
                        continue;

                    int distance = DistanceOf(patch);
                    if (distance >= short.MinValue && distance <= short.MaxValue)
                        continue;

                    patch.Widening = true;
                    (widening ??= new List<JumpPatch>()).Add(patch);
                }

                if (widening is null)
                    return;

                Widen(widening);
            }
        }

        /// <summary>
        /// Rewrites the code with <paramref name="widening"/> re-encoded in their wide form, and
        /// moves every label and patch position through the shift that causes.
        /// </summary>
        /// <remarks>
        /// <paramref name="widening"/> arrives in ascending position order, because patches are
        /// recorded as they are emitted and no two of these share an instruction.
        /// </remarks>
        private void Widen(List<JumpPatch> widening)
        {
            int oldCount = _code.Count;

            // One entry per old byte, plus the end-of-code position: a branch's "measured from"
            // point is the byte after it, which for the last instruction is exactly oldCount.
            var map = new int[oldCount + 1];
            var rewritten = new List<byte>(oldCount + widening.Count * 4);

            int oldPos = 0;
            int next = 0;

            while (oldPos < oldCount)
            {
                if (next < widening.Count && oldPos == widening[next].InstructionStart)
                {
                    var patch = widening[next++];

                    // Every widenable branch ends with its offset, so the instruction runs from its
                    // opcode byte to the end of that immediate.
                    int oldLength = patch.OperandPosition + patch.Width - patch.InstructionStart;

                    for (int i = 0; i < oldLength; i++)
                        map[oldPos + i] = rewritten.Count;

                    rewritten.Add((byte)patch.Wide);

                    if (patch.TypeOperandWidth == 2)
                    {
                        int typeIndex = _code[patch.InstructionStart + 1] | (_code[patch.InstructionStart + 2] << 8);
                        AppendI32(rewritten, typeIndex);
                    }

                    AppendI32(rewritten, 0);
                    oldPos += oldLength;
                }
                else
                {
                    map[oldPos] = rewritten.Count;
                    rewritten.Add(_code[oldPos]);
                    oldPos++;
                }
            }

            map[oldCount] = rewritten.Count;

            for (int i = 0; i < _labelPositions.Count; i++)
            {
                if (_labelPositions[i] >= 0)
                    _labelPositions[i] = map[_labelPositions[i]];
            }

            foreach (var patch in _patches)
            {
                patch.InstructionStart = map[patch.InstructionStart];

                if (patch.Widening)
                {
                    patch.Widening = false;
                    patch.Current = patch.Wide;
                    patch.Width = 4;

                    if (patch.TypeOperandWidth == 2)
                        patch.TypeOperandWidth = 4;

                    patch.OperandPosition = patch.InstructionStart + 1 + patch.TypeOperandWidth;
                }
                else
                {
                    // Untouched instructions were copied byte for byte, so their interior positions
                    // map exactly.
                    patch.OperandPosition = map[patch.OperandPosition];
                }
            }

            _code.Clear();
            _code.AddRange(rewritten);
        }

        private int DistanceOf(JumpPatch patch)
        {
            int target = _labelPositions[patch.Label];
            int origin = patch.RelativeToInstruction
                ? patch.InstructionStart
                : patch.OperandPosition + patch.Width;

            return target - origin;
        }

        private void WriteLittleEndian(int position, int value, int width)
        {
            for (int i = 0; i < width; i++)
                _code[position + i] = (byte)(value >> (8 * i));
        }

        #endregion
    }
}
