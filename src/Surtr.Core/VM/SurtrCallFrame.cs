#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;

namespace Surtr.VM
{
    /// <summary>
    /// One activation record on the VM's call stack: everything needed to describe the frame that
    /// is executing, and everything needed to put the previous one back exactly as it was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A frame describes <em>itself</em> rather than carrying a copy of its caller's state. Restoring
    /// the caller on a return is therefore "pop this frame, then read the one now on top", which
    /// keeps every field single-meaning and makes the frame array a real, walkable stack trace at
    /// any point - including while a native call is in flight.
    /// </para>
    /// <para>
    /// <see cref="IP"/> is the one field whose meaning depends on position: for the executing frame
    /// the live instruction pointer lives in a local inside the dispatch loop, and <see cref="IP"/>
    /// only holds a value while the frame is <em>suspended</em> - either because it called something,
    /// or because it is about to enter host code. The interpreter publishes it before every
    /// transfer out of the loop, which is what lets a native function re-enter the VM and unwind
    /// back without the outer frame losing its place.
    /// </para>
    /// <para>
    /// The call stack is a managed <c>SurtrCallFrame[]</c> rather than unmanaged memory, because a
    /// frame holds object references - its chunk, its method, the closure it is running - and those
    /// have to be reachable by the CLR collector for as long as the frame lives. The interpreter
    /// still reaches frames without a bounds check by taking a <c>ref</c> to element zero once and
    /// offsetting from it. The <em>data</em> stack is the opposite: pure <see cref="SurtrRawValue"/>
    /// with no managed content, so it lives in unmanaged memory and the Surtr collector scans it
    /// through a raw pointer.
    /// </para>
    /// </remarks>
    internal unsafe struct SurtrCallFrame
    {
        /// <summary>
        /// This frame's slot zero: local 0, argument 0, and where its results are written on return.
        /// </summary>
        /// <remarks>
        /// Arguments arrive already in place - the caller pushed them, and the callee's frame simply
        /// starts underneath them - so entering a call copies nothing. Locals <c>[0, ArgumentCount)</c>
        /// are the incoming arguments, locals <c>[ArgumentCount, LocalCount)</c> are zeroed on entry,
        /// and the frame's operand stack begins at <c>Base + LocalCount</c>.
        /// </remarks>
        internal SurtrRawValue* Base;

        /// <summary>The first byte of the chunk this frame executes in, so a jump is a base-plus-offset.</summary>
        internal byte* CodeBase;

        /// <summary>
        /// Where this frame resumes, valid only while it is suspended. Published by the interpreter
        /// before it calls anything, so an in-flight native call cannot lose the frame's place.
        /// </summary>
        internal byte* IP;

        /// <summary>The chunk supplying this frame's constant, type, field, method and module pools.</summary>
        internal SurtrChunk? Chunk;

        /// <summary>The method this frame is running. Kept for diagnostics and stack traces.</summary>
        internal SurtrMethodInfo? Method;

        /// <summary>
        /// The closure this frame is running, or <see langword="null"/> for an ordinary call.
        /// <c>UpValueGet</c> reads through it.
        /// </summary>
        internal SurtrClosure? Closure;

        /// <summary>
        /// The generator this frame is running, or <see langword="null"/> for an ordinary call.
        /// <c>Yield</c> copies the frame into it.
        /// </summary>
        /// <remarks>
        /// Never set at the same time as <see cref="Closure"/>, which is why the two share one
        /// entry in the machine's root buffer: a generator is declared as a member, so its body
        /// captures nothing and has no closure to reach through. A generator lambda would break
        /// that, which is exactly why phase 1 does not have one.
        /// </remarks>
        internal SurtrGenerator? Generator;

        /// <summary>How many slots this frame's locals occupy, arguments included.</summary>
        internal int LocalCount;

        /// <summary>How many incoming arguments this frame was given, receiver included.</summary>
        internal int ArgumentCount;

        /// <summary>
        /// How many results the caller asked for - the <c>retCount</c> immediate of the call
        /// instruction. Zero or one; several values are returned by packing a tuple.
        /// </summary>
        internal int ExpectedResults;
    }
}
