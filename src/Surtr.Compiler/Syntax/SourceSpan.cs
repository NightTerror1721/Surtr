#nullable enable

using System;

namespace Surtr.Compiler.Syntax
{
    /// <summary>
    /// A range of source text: where a token, a node or a problem starts, and how far it reaches.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="SourceLocation"/> answers "where", which is enough for a message and not enough
    /// for anything that has to <em>show</em> the reader what it means. Underlining a bad
    /// expression, selecting a declaration, or reporting the extent of a region a parser gave up on
    /// all need both ends.
    /// </para>
    /// <para>
    /// Stored as a start plus a length rather than two locations: the end's line and column are
    /// derivable from the buffer on the rare occasion something wants them, and paying 12 bytes per
    /// node to cache them would be paying for the case that almost never comes up.
    /// </para>
    /// </remarks>
    public readonly struct SourceSpan : IEquatable<SourceSpan>
    {
        /// <summary>Where the range begins.</summary>
        public SourceLocation Start { get; }

        /// <summary>How many characters the range covers.</summary>
        public int Length { get; }

        /// <summary>Creates a range.</summary>
        /// <param name="start">Where the range begins.</param>
        /// <param name="length">How many characters it covers.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
        public SourceSpan(SourceLocation start, int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), length, "A source span cannot have a negative length.");
            }

            Start = start;
            Length = length;
        }

        /// <summary>The absolute offset one past the last character of the range.</summary>
        public int End => Start.Position + Length;

        /// <summary>Whether the range covers no characters at all.</summary>
        public bool IsEmpty => Length == 0;

        /// <summary>Creates a range from its start to an absolute end offset.</summary>
        /// <param name="start">Where the range begins.</param>
        /// <param name="end">The offset one past the last character. Clamped to <paramref name="start"/> if it precedes it.</param>
        /// <remarks>
        /// Clamps rather than throws because the one caller that can produce an inverted pair is a
        /// parser that has just failed and is describing how far it got. A diagnostic about a
        /// diagnostic helps nobody.
        /// </remarks>
        public static SourceSpan FromBounds(SourceLocation start, int end)
        {
            return new SourceSpan(start, end > start.Position ? end - start.Position : 0);
        }

        /// <summary>The smallest range covering both this one and <paramref name="other"/>.</summary>
        /// <param name="other">The range to extend to.</param>
        /// <remarks>
        /// What a parser builds a node's span with: the first token's range extended to the last
        /// one's. Assumes <paramref name="other"/> does not start before this one, which is true of
        /// every construction site — a production reads forwards.
        /// </remarks>
        public SourceSpan To(SourceSpan other)
        {
            return FromBounds(Start, other.End);
        }

        /// <summary>Whether an absolute offset falls inside the range.</summary>
        /// <param name="position">The offset to test.</param>
        public bool Contains(int position)
        {
            return position >= Start.Position && position < End;
        }

        /// <summary>Whether this range wholly covers <paramref name="other"/>.</summary>
        /// <param name="other">The range to test.</param>
        public bool Contains(SourceSpan other)
        {
            return other.Start.Position >= Start.Position && other.End <= End;
        }

        /// <inheritdoc/>
        public bool Equals(SourceSpan other)
        {
            return Start.Position == other.Start.Position && Length == other.Length;
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is SourceSpan other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => (Start.Position * 397) ^ Length;

        /// <summary>Renders the range as <c>line:column+length</c>, for diagnostics and test failures.</summary>
        public override string ToString() => $"{Start.Line}:{Start.Column}+{Length}";
    }
}
