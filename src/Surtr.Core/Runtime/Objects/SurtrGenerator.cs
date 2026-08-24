#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>Where a <see cref="SurtrGenerator"/> is in its life.</summary>
    /// <remarks>
    /// Four states rather than the obvious three, and <see cref="Running"/> is the one that is easy
    /// to leave out. Suspending copies the live frame <em>out</em> of the data stack and resuming
    /// copies it back <em>in</em>, so a generator that is resumed while it is already running would
    /// have its own live frame overwritten by a stale copy of itself - a corruption with nothing to
    /// catch it. The state makes that a trap instead. It also covers the delegation cycle that
    /// <c>yield*</c> will make possible without needing a second mechanism.
    /// </remarks>
    public enum SurtrGeneratorState : byte
    {
        /// <summary>Created, arguments captured, body not started.</summary>
        NotStarted = 0,

        /// <summary>Stopped at a <c>yield</c>, with its frame held in <see cref="SurtrGenerator.Slots"/>.</summary>
        Suspended = 1,

        /// <summary>Executing right now: its frame is live on the data stack.</summary>
        Running = 2,

        /// <summary>Finished - by falling off the end, by <c>return;</c>, or by an escaping exception.</summary>
        Exhausted = 3,
    }

    /// <summary>
    /// The built-in generator: a suspended method body plus the frame it will resume into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Moulded on <see cref="SurtrClosure"/>, and for the same reason: everything the interpreter
    /// needs to build or rebuild the frame is copied out flat at construction, so resuming is a
    /// couple of field reads and a block copy rather than a walk through metadata. The difference
    /// is what each holds. A closure holds the values it captured and is called any number of
    /// times; a generator holds <em>one</em> in-progress activation and is walked once.
    /// </para>
    /// <para>
    /// <b>Why a copied frame rather than a compiler-built state machine.</b> C# rewrites an
    /// iterator body into a class whose fields are its live locals and whose <c>MoveNext</c> is a
    /// re-entrant switch. That needs liveness analysis and flow numbering in the front end, and it
    /// promotes every crossing local to the heap. This VM already has what makes the cheaper answer
    /// work: a frame is a flat run of untyped slots at a known width, so suspending is
    /// <c>MemOps.Copy</c> of <c>[Base, sp)</c> into <see cref="Slots"/> and resuming is the same
    /// copy back. The body compiles exactly as an ordinary method would. The price is the
    /// restriction every language in this space already accepts: a <c>yield</c> must be lexically
    /// inside the generator, never inside something it calls, because that frame is gone by then.
    /// See <c>docs/Plan-Generadores.md</c> §4.
    /// </para>
    /// <para>
    /// <b>Arguments are captured, not the frame.</b> Calling a generator function runs no body
    /// (§3.1): the stub built by the compiler evaluates the arguments and hands them here, where
    /// they wait in <see cref="Slots"/> until the first resume lays them out as locals
    /// <c>[0, ArgumentCount)</c>. That is why one buffer serves both purposes - before the first
    /// resume it holds the arguments, afterwards it holds the whole suspended frame - and why a
    /// generator nobody iterates costs exactly one allocation.
    /// </para>
    /// </remarks>
    public sealed class SurtrGenerator : SurtrObject
    {
        /// <summary>The method whose body this generator runs - the compiler's hidden body method, never the stub.</summary>
        internal readonly SurtrMethodInfo Method;

        /// <summary>The chunk holding the body, copied off <see cref="Method"/> so resuming is a field read.</summary>
        internal readonly SurtrChunk Chunk;

        /// <summary>Byte offset of the body's first instruction inside <see cref="Chunk"/>'s code.</summary>
        internal readonly int CodeOffset;

        /// <summary>How many local slots a frame for this body needs, arguments included.</summary>
        internal readonly int LocalCount;

        /// <summary>The deepest the operand stack gets while the body runs.</summary>
        internal readonly int MaxStackSize;

        /// <summary>How many arguments were handed to the generator function, receiver included.</summary>
        internal readonly int ArgumentCount;

        /// <summary>
        /// The suspended frame: locals <c>[0, LocalCount)</c> followed by whatever operands were
        /// pending, <see cref="SlotCount"/> of them in total.
        /// </summary>
        /// <remarks>
        /// Managed rather than a <c>SurtrNativeArray</c> because a generator is a collectable value
        /// and the registry sweeps by dropping its reference, with no finalization hook - an
        /// unmanaged buffer owned by one would leak on every collection. It is also what
        /// <see cref="VisitReferences"/> traces, exactly as a closure's captures are traced.
        /// Allocated once at construction with room for the widest frame the body can reach, so no
        /// <c>yield</c> ever allocates.
        /// </remarks>
        internal readonly SurtrValue[] Slots;

        /// <summary>How many of <see cref="Slots"/> are live: the width of the suspended frame.</summary>
        internal int SlotCount;

        /// <summary>Where the body resumes, as a byte offset into <see cref="Chunk"/>'s code.</summary>
        internal int ResumeOffset;

        /// <summary>The value the last <c>yield</c> produced, which <c>current</c> reads back.</summary>
        /// <remarks>
        /// Held here rather than left on the operand stack so that both ways of resuming agree. The
        /// fast path reads it with <c>GenCurrent</c> and the interface path through the native
        /// <c>current</c> accessor, and neither has to know which one produced it.
        /// </remarks>
        internal SurtrValue Current;

        /// <summary>Where this generator is in its life. See <see cref="SurtrGeneratorState"/>.</summary>
        internal SurtrGeneratorState State;

        /// <summary>
        /// The generator this one is delegating to, or <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// Always <see langword="null"/> in phase 1, which has no <c>yield*</c>. It is declared now
        /// because the resume path is where delegation lives: a <c>yield* inner</c> records the link
        /// and suspends without a frame, and every later resume walks the chain to the innermost
        /// generator instead of copying each intermediate frame in and out. Reserving the field and
        /// writing the walk as a loop that happens never to iterate keeps phase 2 to an opcode and a
        /// lowering rather than a reopening of this protocol. See
        /// <c>docs/Plan-Generadores.md</c> §11.3.
        /// </remarks>
        internal SurtrGenerator? Delegate;

        private readonly SurtrClassReference _typeReference;

        /// <summary>
        /// Builds a generator whose arguments the caller writes into <see cref="Slots"/>.
        /// </summary>
        /// <remarks>
        /// The arguments arrive as a count rather than as a span, and the one caller - the
        /// interpreter's <c>GenNew</c> - fills the slots itself. They are sitting on the data stack
        /// as raw values, so any other shape would mean building a temporary just to copy it
        /// straight back out.
        /// </remarks>
        internal SurtrGenerator(
            SurtrClassReference typeReference,
            SurtrBytecodeMethodInfo method,
            int argumentCount)
            : base(SurtrBuiltIns.Generator)
        {
            _typeReference = typeReference;
            Method = method;
            Chunk = method.Chunk;
            CodeOffset = method.CodeOffset;
            LocalCount = method.LocalCount;
            MaxStackSize = method.MaxStackSize;
            ArgumentCount = argumentCount;

            // Sized for the widest frame the body can ever reach, so suspending is a copy into
            // space that already exists and no `yield` ever allocates. The arguments live at the
            // bottom of that same buffer until the first resume turns them into locals.
            Slots = new SurtrValue[method.LocalCount + method.MaxStackSize];

            SlotCount = argumentCount;
            ResumeOffset = method.CodeOffset;
            Current = SurtrValue.Null;
            State = SurtrGeneratorState.NotStarted;
        }

        /// <summary>The method body this generator runs.</summary>
        public SurtrMethodInfo TargetMethod
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Method;
        }

        /// <summary>This generator's full type descriptor - <c>YI</c> for <c>generator&lt;int&gt;</c>.</summary>
        public SurtrClassReference TypeReference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _typeReference;
        }

        /// <summary>Where this generator is in its life.</summary>
        public SurtrGeneratorState GetState()
        {
            return State;
        }

        /// <summary>Whether this generator has produced its last element.</summary>
        public bool IsExhausted
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => State == SurtrGeneratorState.Exhausted;
        }

        /// <summary>The value the last <c>yield</c> produced.</summary>
        public SurtrValue GetCurrent()
        {
            return Current;
        }

        /// <summary>
        /// Marks this generator finished and releases the references its suspended frame held.
        /// </summary>
        /// <remarks>
        /// Clearing matters as much as the state does: an exhausted generator can stay reachable
        /// for as long as whoever iterated it keeps the variable, and its frame may name objects
        /// nothing else does. Dropping the live width and blanking the slots lets the collector
        /// take them at the next sweep instead of at the generator's own death.
        /// </remarks>
        internal void Finish()
        {
            State = SurtrGeneratorState.Exhausted;
            Delegate = null;
            Current = SurtrValue.Null;

            var slots = Slots;
            for (int i = 0; i < SlotCount; i++)
                slots[i] = SurtrValue.Null;

            SlotCount = 0;
        }

        internal override void VisitReferences(SurtrEntityMarker marker)
        {
            // The suspended frame is this generator's whole reachable content - the method and the
            // chunk are metadata, which the registry does not own. Only the live prefix is traced:
            // the slack above SlotCount is whatever a previous, deeper suspension left there, and
            // retaining that would keep objects alive on the strength of a stale copy.
            var slots = Slots;
            int count = SlotCount;
            for (int i = 0; i < count; i++)
                marker.Mark(slots[i]);

            marker.Mark(Current);

            // Marked, not walked: a delegate is a registered entity of its own, so handing it to
            // the marker is what both keeps it alive and stops a delegation cycle from recursing
            // forever. Null throughout phase 1, traced anyway so that turning delegation on in
            // phase 2 is not silently a collector bug.
            marker.Mark(Delegate);
        }
    }
}
