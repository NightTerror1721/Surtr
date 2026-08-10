#nullable enable

namespace Surtr.Compiler.Syntax
{
    /// <summary>
    /// A point in a source buffer, carried by every <see cref="Token"/> and every diagnostic.
    /// Holds both the human coordinates and the raw offset because they answer different
    /// questions: line and column are for the message a user reads, <see cref="Position"/> is what
    /// slices the buffer to recover a token's text.
    /// </summary>
    public readonly struct SourceLocation
    {
        /// <summary>The 1-based line.</summary>
        public int Line { get; }

        /// <summary>The 1-based column within <see cref="Line"/>.</summary>
        public int Column { get; }

        /// <summary>The absolute character offset into the source buffer, independent of line/column.</summary>
        public int Position { get; }

        /// <summary>Creates a location.</summary>
        /// <param name="line">The 1-based line.</param>
        /// <param name="column">The 1-based column within the line.</param>
        /// <param name="position">The absolute character offset into the source buffer.</param>
        public SourceLocation(int line, int column, int position)
        {
            Line = line;
            Column = column;
            Position = position;
        }

        internal static SourceLocation FromCharReader(CharReader reader)
        {
            return new SourceLocation(reader.Line, reader.Column, reader.Position);
        }
    }
}
