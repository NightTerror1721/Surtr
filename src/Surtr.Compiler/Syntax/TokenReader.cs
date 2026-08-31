#nullable enable

using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Utilities;

namespace Surtr.Compiler.Syntax
{
    /// <summary>
    /// Walks a lexed token stream, extending <see cref="Cursor{T}"/> with the two things
    /// token-level parsing needs that a bare cursor does not know about: comparison by
    /// <see cref="TokenType"/> rather than by whole token, and the ability to take a single
    /// <c>&gt;</c> off a token that begins with one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Cursor{T}"/>'s own <c>Check</c>/<c>Match</c> compare elements with
    /// <c>EqualityComparer&lt;T&gt;.Default</c>, which for a <see cref="Token"/> would compare its
    /// lexeme and position too. A parser only ever asks "what kind of token is this", so the
    /// overloads here shadow that with type-only comparison.
    /// </para>
    /// <para>
    /// <b>The angle-bracket split.</b> Maximal munch means the lexer hands back <c>&gt;&gt;</c> for
    /// the tail of <c>Box&lt;Box&lt;T&gt;&gt;</c> and <c>&gt;&gt;&gt;</c> for one more level of
    /// nesting — it cannot know it is inside a type argument list, and no lexer can.
    /// <see cref="ConsumeTypeArgumentClose"/> is where that is repaid: it takes one <c>&gt;</c>
    /// worth of whatever token is there and remembers the rest in <see cref="pendingCloseAngles"/>,
    /// so each enclosing type argument list gets its own in turn. Every ordinary read goes through
    /// <see cref="Advance()"/>, which refuses to step over an unconsumed <c>&gt;</c>, so a leftover
    /// can never be silently dropped.
    /// </para>
    /// <para>
    /// The <c>=</c>-suffixed shapes (<c>&gt;=</c>, <c>&gt;&gt;=</c>, <c>&gt;&gt;&gt;=</c>) are
    /// <em>not</em> split, because doing so would mean synthesising an <c>=</c> token that the
    /// lexer never produced. They are rejected with a message asking for a space instead. This only
    /// bites on <c>Foo&lt;T&gt;= x</c> written with no space before the <c>=</c>, which is both
    /// rare and trivially fixed at the call site — a poor trade against carrying a token-rewriting
    /// mechanism through the whole parser.
    /// </para>
    /// </remarks>
    internal sealed class TokenReader : Cursor<Token>
    {
        /// <summary>
        /// How many <c>&gt;</c> remain from a multi-angle token the parser has begun splitting.
        /// Non-zero only in the middle of closing nested type argument lists.
        /// </summary>
        private int pendingCloseAngles;

        /// <summary>Identifies the source being parsed, for diagnostics.</summary>
        internal string SourceName { get; }

        /// <summary>Where problems are recorded.</summary>
        internal SurtrDiagnosticBag Diagnostics { get; }

        /// <summary>Creates a reader over an already-lexed token stream.</summary>
        /// <param name="tokens">The tokens, ending with <see cref="TokenType.EndOfFile"/>.</param>
        /// <param name="sourceName">Identifies the source, for diagnostics.</param>
        /// <param name="diagnostics">Where to record problems.</param>
        internal TokenReader(Token[] tokens, string sourceName, SurtrDiagnosticBag diagnostics) : base(tokens)
        {
            SourceName = sourceName;
            Diagnostics = diagnostics;
        }

        /// <summary>The type of the token at the current position.</summary>
        internal TokenType CurrentType => PeekType(0);

        /// <summary>
        /// True when the current token starts on a later line than the one before it. That is the
        /// signal a statement or declaration ends even without a <c>;</c>: nothing on this line can
        /// continue it, so the line break itself is the terminator (§1).
        /// </summary>
        internal bool IsAfterLineBreak => Position > 0 && Current.Location.Line > Peek(-1).Location.Line;

        /// <summary>
        /// The type of the token <paramref name="offset"/> slots ahead, without copying the whole
        /// <see cref="Token"/> — the type is one byte, the token is ~64. The parser's lookahead and
        /// the type-argument scans read types far more often than whole tokens, so this is the read
        /// they take.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal TokenType PeekType(int offset)
        {
            var span = Elements;
            int index = Position + offset;
            return (uint)index < (uint)span.Length ? span[index].Type : default;
        }

        /// <summary>Where the current token starts.</summary>
        internal SourceLocation CurrentLocation => Current.Location;

        /// <summary>
        /// The offset one past the last token consumed, or the current token's start when nothing
        /// has been consumed yet.
        /// </summary>
        /// <remarks>
        /// What closes a node's span. A production knows where it started because it kept the
        /// location; where it <em>ended</em> is wherever the reader got to, which is the previous
        /// token's end rather than the current one's start — the two differ by whatever trivia sat
        /// between them, and a node should not claim the whitespace after it.
        /// </remarks>
        internal int ConsumedEnd => Position > 0 ? Peek(-1).Span.End : Current.Location.Position;

        /// <summary>True when the current token is of the given type and no split is in progress.</summary>
        /// <param name="type">The type to test for.</param>
        internal bool Check(TokenType type) => pendingCloseAngles == 0 && CurrentType == type;

        /// <summary>
        /// True when the token <paramref name="offset"/> positions ahead is of the given type.
        /// Raw lookahead: it ignores any split in progress, which is safe because every caller uses
        /// it outside type-argument parsing.
        /// </summary>
        /// <param name="offset">How far ahead to look.</param>
        /// <param name="type">The type to test for.</param>
        internal bool CheckAt(int offset, TokenType type) => PeekType(offset) == type;

        /// <summary>Consumes the current token if it is of the given type, and says whether it did.</summary>
        /// <param name="type">The type to match.</param>
        internal bool Match(TokenType type)
        {
            if (!Check(type))
            {
                return false;
            }

            Advance();
            return true;
        }

        /// <summary>
        /// Consumes the current token, requiring it to be of the given type. Unlike
        /// <see cref="Match(TokenType)"/> this is for a position the grammar guarantees is filled,
        /// so failing it throws rather than returning a sentinel the caller might not check.
        /// </summary>
        /// <param name="type">The required type.</param>
        /// <param name="what">What was expected, for the error message.</param>
        internal Token Expect(TokenType type, string what)
        {
            if (!Check(type))
            {
                throw Error(SurtrDiagnosticCode.UnexpectedToken, $"Expected {what}.");
            }

            return Advance();
        }

        /// <summary>Consumes an identifier and returns its text.</summary>
        /// <param name="what">What the identifier names, for the error message.</param>
        internal string ExpectIdentifier(string what)
        {
            return Expect(TokenType.Identifier, what).ToString();
        }

        /// <inheritdoc/>
        internal override Token Advance()
        {
            if (pendingCloseAngles > 0)
            {
                throw Error(SurtrDiagnosticCode.UnclosedTypeArgumentList, "Expected '>' to close the type argument list.");
            }

            return base.Advance();
        }

        /// <summary>True when a <c>&gt;</c> is available here, standalone or as the head of a longer token.</summary>
        internal bool CheckTypeArgumentClose()
        {
            if (pendingCloseAngles > 0)
            {
                return true;
            }

            return CurrentType == TokenType.Greater
                || CurrentType == TokenType.ShiftRight
                || CurrentType == TokenType.UnsignedShiftRight;
        }

        /// <summary>
        /// Consumes one <c>&gt;</c> closing a type argument list, splitting a <c>&gt;&gt;</c> or
        /// <c>&gt;&gt;&gt;</c> if that is what is there.
        /// </summary>
        internal void ConsumeTypeArgumentClose()
        {
            if (pendingCloseAngles > 0)
            {
                pendingCloseAngles--;
                return;
            }

            switch (CurrentType)
            {
                case TokenType.Greater:
                    base.Advance();
                    return;

                case TokenType.ShiftRight:
                    base.Advance();
                    pendingCloseAngles = 1;
                    return;

                case TokenType.UnsignedShiftRight:
                    base.Advance();
                    pendingCloseAngles = 2;
                    return;

                case TokenType.GreaterEqual:
                case TokenType.ShiftRightAssign:
                case TokenType.UnsignedShiftRightAssign:
                    throw Error(SurtrDiagnosticCode.UnclosedTypeArgumentList, "A type argument list cannot be closed by a token ending in '='; put a space before the '='.");

                default:
                    throw Error(SurtrDiagnosticCode.UnclosedTypeArgumentList, "Expected '>' to close the type argument list.");
            }
        }

        /// <summary>
        /// Reports a problem at the current token and hands back the exception that abandons the
        /// production.
        /// </summary>
        /// <remarks>
        /// One step for both, because they are one decision: the diagnostic is recorded whether or
        /// not anything catches the exception, so a recovery point can carry on parsing while the
        /// problem is already on the record.
        /// </remarks>
        /// <param name="code">What kind of problem this is.</param>
        /// <param name="message">What went wrong.</param>
        internal SurtrParserException Error(SurtrDiagnosticCode code, string message)
        {
            return Error(code, message, Current.Span);
        }

        /// <summary>Reports a problem covering an explicit range.</summary>
        /// <param name="code">What kind of problem this is.</param>
        /// <param name="message">What went wrong.</param>
        /// <param name="span">The source the problem is about.</param>
        internal SurtrParserException Error(SurtrDiagnosticCode code, string message, SourceSpan span)
        {
            var diagnostic = new SurtrDiagnostic(code, SurtrDiagnosticSeverity.Error, message, SourceName, span);
            Diagnostics.Report(diagnostic);
            return new SurtrParserException(diagnostic);
        }

        /// <summary>Reports a problem starting at <paramref name="start"/> and running to the last token read.</summary>
        /// <param name="code">What kind of problem this is.</param>
        /// <param name="message">What went wrong.</param>
        /// <param name="start">Where the offending construct began.</param>
        internal SurtrParserException Error(SurtrDiagnosticCode code, string message, SourceLocation start)
        {
            return Error(code, message, SourceSpan.FromBounds(start, ConsumedEnd));
        }
    }
}
