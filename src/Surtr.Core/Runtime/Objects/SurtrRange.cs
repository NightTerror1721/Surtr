#nullable enable

using Surtr.Runtime.BuiltIns;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// A half-open or closed interval of <c>int</c>s: what <c>0..10</c> and <c>0..=10</c> evaluate
    /// to when the value escapes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A range is a first-class value, not merely <c>for-in</c> header syntax - it can be bound,
    /// passed and returned. It is still the case that <strong>a range written inline in a loop
    /// header must never allocate one of these</strong>: the compiler lowers
    /// <c>for (i in lo..hi)</c> to a counted loop over two ints, and only a range that genuinely
    /// escapes into a variable, a parameter or a return is materialised. Allocating per loop entry
    /// is exactly the hidden per-iteration cost the performance rules forbid.
    /// </para>
    /// <para>
    /// Both bounds are <c>int</c> and the type is not parameterised, which is what keeps its
    /// descriptor a bare symbol like a primitive's rather than a nesting form like the
    /// collections'.
    /// </para>
    /// <para>
    /// The bounds are stored exactly as written, with inclusivity as its own flag, rather than
    /// normalised to a half-open pair. Normalising would mean adding one to the upper bound, and
    /// <c>0..=int.MaxValue</c> is a legal range whose normalised form does not exist.
    /// </para>
    /// </remarks>
    public sealed class SurtrRange : SurtrObject
    {
        private readonly SurtrInt _start;
        private readonly SurtrInt _end;
        private readonly bool _inclusive;

        internal SurtrRange(SurtrInt start, SurtrInt end, bool inclusive) : base(SurtrBuiltIns.Range)
        {
            _start = start;
            _end = end;
            _inclusive = inclusive;
        }

        /// <summary>The lower bound, always included.</summary>
        public SurtrInt Start
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _start;
        }

        /// <summary>The upper bound, as written. Whether it is included is <see cref="IsInclusive"/>.</summary>
        public SurtrInt End
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _end;
        }

        /// <summary>Whether <see cref="End"/> is part of the range: the <c>..=</c> form.</summary>
        public bool IsInclusive
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _inclusive;
        }

        /// <summary>Whether the range contains no values at all.</summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _inclusive ? _end < _start : _end <= _start;
        }

        /// <summary>
        /// How many values the range covers, saturating at <see cref="int.MaxValue"/>.
        /// </summary>
        /// <remarks>
        /// Counted in 64 bits and then clamped, because the span of an inclusive range over the
        /// whole of <c>int</c> is one more than <c>int</c> can hold. Saturating is the honest
        /// answer for a value the language can only express as an <c>int</c>.
        /// </remarks>
        public SurtrInt Length
        {
            get
            {
                long count = (long)_end - _start + (_inclusive ? 1 : 0);
                if (count <= 0)
                    return 0;

                return count > int.MaxValue ? int.MaxValue : (SurtrInt)count;
            }
        }

        /// <summary>Whether <paramref name="value"/> falls inside the range.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(SurtrInt value)
            => value >= _start && (_inclusive ? value <= _end : value < _end);

        // Two ints and a flag: nothing here is a Surtr entity, so there is nothing to trace.
        internal override void VisitReferences(SurtrEntityMarker marker) { }
    }
}
