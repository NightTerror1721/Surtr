#nullable enable

using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// One protected region of a bytecode method: a range of code, and where to go when something
    /// raised inside it matches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table rather than opcodes. Entering a <c>try</c> emits nothing at all and costs nothing at
    /// run time; only an exception that is actually raised pays, and it pays a walk over a handful
    /// of entries. A push-handler / pop-handler pair would instead charge two instructions and a
    /// side stack to every <c>try</c> the program ever enters, almost all of which complete
    /// normally.
    /// </para>
    /// <para>
    /// Offsets are absolute within the declaring module's chunk, not relative to the method, since
    /// every method is already an offset into one shared instruction stream.
    /// </para>
    /// <para>
    /// Entries are searched in order, so the compiler must emit inner regions before the outer ones
    /// enclosing them, and a type-specific handler before a catch-all covering the same range.
    /// </para>
    /// </remarks>
    public readonly struct SurtrExceptionHandler
    {
        private readonly int _tryStart;
        private readonly int _tryEnd;
        private readonly int _handlerOffset;
        private readonly SurtrTypeHandle? _catchType;

        /// <summary>Describes a protected region.</summary>
        /// <param name="tryStart">Offset of the region's first instruction, inclusive.</param>
        /// <param name="tryEnd">Offset one past the region's last instruction.</param>
        /// <param name="handlerOffset">Where the handler's first instruction lives.</param>
        /// <param name="catchType">
        /// The type this handler catches, or <see langword="null"/> to catch everything. A
        /// catch-all is also how a <c>finally</c> is expressed: run the block, then re-raise with
        /// <c>Throw</c>.
        /// </param>
        public SurtrExceptionHandler(int tryStart, int tryEnd, int handlerOffset, SurtrTypeHandle? catchType)
        {
            _tryStart = tryStart;
            _tryEnd = tryEnd;
            _handlerOffset = handlerOffset;
            _catchType = catchType;
        }

        /// <summary>Offset of the protected region's first instruction, inclusive.</summary>
        public int TryStart
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _tryStart;
        }

        /// <summary>Offset one past the protected region's last instruction.</summary>
        public int TryEnd
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _tryEnd;
        }

        /// <summary>Where the handler's first instruction lives.</summary>
        public int HandlerOffset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _handlerOffset;
        }

        /// <summary>The type this handler catches, or <see langword="null"/> for a catch-all.</summary>
        public SurtrTypeHandle? CatchType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _catchType;
        }

        /// <summary>
        /// Whether this region covers the instruction a frame is suspended at.
        /// </summary>
        /// <remarks>
        /// <paramref name="resumeOffset"/> is the frame's saved instruction pointer, which always
        /// sits <em>past</em> the instruction that raised - the interpreter advances it while
        /// decoding, and a suspended caller is parked after its call. Testing
        /// <c>TryStart &lt; resume &lt;= TryEnd</c> maps that back exactly onto "the raising
        /// instruction started inside [TryStart, TryEnd)", because both bounds are instruction
        /// boundaries.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Covers(int resumeOffset) => resumeOffset > _tryStart && resumeOffset <= _tryEnd;
    }
}
