#nullable enable

using Surtr.Compiler.Utilities;

namespace Surtr.Compiler.Syntax
{
    /// <summary>
    /// Walks a <see cref="SurtrSourceBuffer"/> one character at a time, extending
    /// <see cref="Cursor{T}"/> with the one thing char-level lexing needs that a bare cursor
    /// doesn't know about: line and column, so a lexer can attach a source position to every
    /// token and diagnostic without rescanning the buffer to find it.
    /// </summary>
    /// <remarks>
    /// Only <c>\n</c> is treated as a line break - a lone <c>\r</c> is just an ordinary character,
    /// which handles both Unix (<c>\n</c>) and Windows (<c>\r\n</c>) line endings correctly. Backing
    /// up over a newline recomputes <see cref="Column"/> by rescanning to the previous line break,
    /// since nothing here keeps a history of earlier line lengths; every other <see cref="Back()"/>
    /// is an O(1) decrement.
    /// </remarks>
    internal sealed class CharReader : Cursor<char>
    {
        /// <summary>Identifies the source being read, forwarded from the owning <see cref="SurtrSourceBuffer"/>.</summary>
        internal string SourceName { get; }

        /// <summary>The 1-based line the cursor is currently on.</summary>
        internal int Line { get; private set; }

        /// <summary>The 1-based column within <see cref="Line"/> the cursor is currently at.</summary>
        internal int Column { get; private set; }

        internal CharReader(SurtrSourceBuffer source) : base(source.Text)
        {
            SourceName = source.Name;
            Line = 1;
            Column = 1;
        }

        /// <summary>Starts reading part-way into a buffer, at a position already known.</summary>
        /// <param name="source">The buffer to read.</param>
        /// <param name="origin">Where to start, in that buffer's own coordinates.</param>
        /// <remarks>
        /// For scanning a fragment in place rather than in a buffer of its own: an interpolated
        /// literal's <c>${...}</c> hole is lexed out of the file it was written in, so that what
        /// comes out is measured against that file like everything else. Reading a copy of the
        /// fragment would restart every offset at zero, and those offsets are what slice the buffer
        /// back into lexemes — they have to mean the same thing on both sides.
        /// </remarks>
        internal CharReader(SurtrSourceBuffer source, SourceLocation origin) : base(source.Text)
        {
            SourceName = source.Name;
            Line = origin.Line;
            Column = origin.Column;
            Position = origin.Position;
        }

        /// <inheritdoc/>
        internal override char Advance()
        {
            bool consuming = !IsAtEnd;
            char consumed = base.Advance();
            if (!consuming)
            {
                return consumed;
            }

            if (consumed == '\n')
            {
                Line++;
                Column = 1;
            }
            else
            {
                Column++;
            }

            return consumed;
        }

        /// <inheritdoc/>
        internal override char Back()
        {
            bool moving = Position > 0;
            char restored = base.Back();
            if (!moving)
            {
                return restored;
            }

            if (restored == '\n')
            {
                Line--;
                Column = ComputeColumn();
            }
            else
            {
                Column--;
            }

            return restored;
        }

        /// <summary>Recomputes the column at the current position by scanning back to the previous <c>\n</c> (or the start of the buffer).</summary>
        private int ComputeColumn()
        {
            int column = 1;
            int offset = -1;
            while (Position + offset >= 0 && Peek(offset) != '\n')
            {
                column++;
                offset--;
            }

            return column;
        }
    }
}
