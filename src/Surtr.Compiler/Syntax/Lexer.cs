#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Surtr.Compiler.Diagnostics;

namespace Surtr.Compiler.Syntax
{
    /// <summary>
    /// Turns a <see cref="SurtrSourceBuffer"/> into a stream of <see cref="Token"/>s, following
    /// <c>docs/Language-Syntax.md</c> - §1.2 for the reserved words and §5.7 for the operator set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Operators are scanned by <b>maximal munch</b>: the longest operator that matches at the
    /// current position wins, so <c>!==</c> is never read as <c>!=</c> followed by <c>=</c>. The
    /// switch in <see cref="ScanOperator"/> is written longest-first for each starting character
    /// for exactly that reason, and reordering an arm there changes the language.
    /// </para>
    /// <para>
    /// Two places need lookahead beyond the current character, both because a token boundary is
    /// genuinely ambiguous without it:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>A <c>.</c> after digits.</b> It only starts a fractional part if a digit follows it, so
    /// <c>0..10</c> scans as <c>0</c> <c>..</c> <c>10</c> rather than a malformed float. Without
    /// this, first-class ranges (§5.4) would be unlexable next to integer literals.
    /// </description></item>
    /// <item><description>
    /// <b><c>///</c> versus <c>//</c>.</b> A doc comment is a token; an ordinary comment is trivia
    /// and never reaches the stream.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Malformed input is reported into <see cref="Diagnostics"/> and skipped</b>, rather than
    /// producing a <see cref="TokenType.Invalid"/> token for a later stage to puzzle over. The
    /// lexer is the only stage that still knows the exact character position of the problem, so it
    /// is the right one to describe it — and the only one that can, since a token that never
    /// existed leaves nothing behind to point at.
    /// </para>
    /// <para>
    /// Recovery skips a failed <em>literal</em> whole rather than a character, so the closing quote
    /// of a bad string is never read as opening another one. A compiler whose second complaint is
    /// caused by its first is a compiler whose output gets skimmed.
    /// </para>
    /// </remarks>
    public sealed class Lexer
    {
        private readonly SurtrSourceBuffer source;
        private readonly CharReader reader;
        private readonly SurtrDiagnosticBag diagnostics;

        /// <summary>Creates a lexer over <paramref name="source"/>.</summary>
        /// <param name="source">The source text to scan.</param>
        /// <param name="diagnostics">
        /// Where to report malformed input, or <see langword="null"/> for a bag of its own. Pass the
        /// parser's so that one compilation collects everything in one place.
        /// </param>
        public Lexer(SurtrSourceBuffer source, SurtrDiagnosticBag? diagnostics = null)
        {
            this.source = source;
            this.diagnostics = diagnostics ?? new SurtrDiagnosticBag();
            reader = new CharReader(source);
        }

        /// <summary>Scans a fragment of a buffer, starting at <paramref name="origin"/>.</summary>
        /// <param name="source">
        /// The buffer, cut off where the fragment ends — the end is what stops the scan, since a
        /// lexer runs to the end of what it is given.
        /// </param>
        /// <param name="origin">Where the fragment starts, in that buffer's coordinates.</param>
        /// <param name="diagnostics">Where to report problems.</param>
        internal Lexer(SurtrSourceBuffer source, SourceLocation origin, SurtrDiagnosticBag? diagnostics)
        {
            this.source = source;
            this.diagnostics = diagnostics ?? new SurtrDiagnosticBag();
            reader = new CharReader(source, origin);
        }

        /// <summary>Everything the lexer has found wrong with the source.</summary>
        public SurtrDiagnosticBag Diagnostics => diagnostics;

        /// <summary>
        /// Scans the whole source. The returned list always ends with a single
        /// <see cref="TokenType.EndOfFile"/> token, so a parser never has to bounds-check its
        /// lookahead.
        /// </summary>
        /// <remarks>
        /// <b>Malformed input is reported and skipped, not thrown.</b> A lexer that gave up on the
        /// first bad character would hand the parser nothing, so a file with one stray <c>#</c> in
        /// it would report exactly one problem and hide every other — and the parser, which finds
        /// most of what is actually wrong with a file, would never run at all.
        /// </remarks>
        public List<Token> Tokenize()
        {
            // Preseed from the buffer: a source of length N yields on the order of N/2 tokens, and
            // starting there avoids the doubling reallocations a token-heavy file would otherwise
            // grow through.
            int capacity = Math.Min(source.Text.Length / 2 + 16, 1 << 20);
            List<Token> tokens = new List<Token>(capacity);

            while (true)
            {
                int before = reader.Position;
                Token token;

                try
                {
                    token = NextToken();
                }
                catch (SurtrLexerException)
                {
                    // Already reported by Error; all that is left is to get past it.
                    Recover(before);
                    continue;
                }

                tokens.Add(token);

                if (token.Type == TokenType.EndOfFile)
                {
                    return tokens;
                }
            }
        }

        /// <summary>Gets past a token that failed to scan, so the next problem reported is a new one.</summary>
        /// <param name="start">Where the failed token began.</param>
        /// <remarks>
        /// <para>
        /// Skipping one character would be enough to guarantee progress, and would be the wrong
        /// answer: resuming in the middle of a bad string literal finds the closing quote, calls it
        /// the start of another one, and reports a second problem that exists only because of the
        /// first. Cascades like that are what make a compiler's output worth ignoring.
        /// </para>
        /// <para>
        /// So a failed literal is skipped as a literal — up to and including its closing delimiter,
        /// or to the end of the line, since neither string nor character literals may cross one.
        /// Anything else advances by a character, which is all that is needed when the token that
        /// failed was one character long to begin with.
        /// </para>
        /// </remarks>
        private void Recover(int start)
        {
            char opener = start < source.Text.Length ? source.Text.Span[start] : '\0';

            if (opener == '"' || opener == '\'')
            {
                while (!reader.IsAtEnd && reader.Current != '\n')
                {
                    if (reader.Advance() == opener)
                    {
                        return;
                    }
                }

                return;
            }

            // A token that failed without consuming anything would otherwise be retried forever.
            if (reader.Position == start && !reader.IsAtEnd)
            {
                reader.Advance();
            }
        }

        /// <summary>Scans and returns the next token, skipping any whitespace and ordinary comments before it.</summary>
        public Token NextToken()
        {
            SkipTrivia();

            SourceLocation start = SourceLocation.FromCharReader(reader);

            if (reader.IsAtEnd)
            {
                return Make(TokenType.EndOfFile, start);
            }

            char current = reader.Current;

            if (current == '/' && reader.Peek(1) == '/' && reader.Peek(2) == '/' && reader.Peek(3) != '/')
            {
                return ScanDocComment(start);
            }

            if (IsIdentifierStart(current))
            {
                return ScanIdentifierOrKeyword(start);
            }

            if (IsDigit(current))
            {
                return ScanNumber(start);
            }

            if (current == '"')
            {
                return ScanString(start);
            }

            if (current == '\'')
            {
                return ScanCharacter(start);
            }

            return ScanOperator(start);
        }

        /// <summary>
        /// Consumes whitespace, <c>//</c> line comments and <c>/* */</c> block comments. Stops at a
        /// <c>///</c> doc comment, which is a token rather than trivia.
        /// </summary>
        private void SkipTrivia()
        {
            while (!reader.IsAtEnd)
            {
                char current = reader.Current;

                // Fast path for the whitespace that actually occurs in source, before falling
                // back to the full Unicode table.
                if (current == ' ' || current == '\t' || current == '\n' || current == '\r'
                    || current == '\f' || current == '\v' || char.IsWhiteSpace(current))
                {
                    reader.Skip();
                    continue;
                }

                if (current != '/')
                {
                    return;
                }

                char next = reader.Peek(1);

                if (next == '/')
                {
                    // A doc comment is a token, so leave it for NextToken. `////` and longer are
                    // ordinary comments, matching the convention C# uses.
                    if (reader.Peek(2) == '/' && reader.Peek(3) != '/')
                    {
                        return;
                    }

                    reader.AdvanceUntil('\n');
                    continue;
                }

                if (next == '*')
                {
                    SkipBlockComment();
                    continue;
                }

                return;
            }
        }

        /// <summary>Consumes a <c>/* ... */</c> comment. Block comments do not nest: the first <c>*/</c> closes it.</summary>
        private void SkipBlockComment()
        {
            SourceLocation start = SourceLocation.FromCharReader(reader);
            reader.Skip(2);

            while (!reader.IsAtEnd)
            {
                if (reader.Current == '*' && reader.Peek(1) == '/')
                {
                    reader.Skip(2);
                    return;
                }

                reader.Skip();
            }

            throw Error(SurtrDiagnosticCode.UnterminatedComment, "Unterminated block comment.", start);
        }

        /// <summary>Scans a <c>///</c> doc comment to the end of its line. The payload is the text after the slashes, trimmed.</summary>
        private Token ScanDocComment(SourceLocation start)
        {
            reader.Skip(3);

            int textStart = reader.Position;
            reader.AdvanceUntil('\n');

            ReadOnlySpan<char> text = source.Text.Span.Slice(textStart, reader.Position - textStart).Trim();
            return Make(TokenType.DocComment, start, TokenPayload.ForString(text.ToString()));
        }

        /// <summary>Scans an identifier, then checks whether it is one of §1.2's reserved words.</summary>
        private Token ScanIdentifierOrKeyword(SourceLocation start)
        {
            while (!reader.IsAtEnd && IsIdentifierPart(reader.Current))
            {
                reader.Skip();
            }

            ReadOnlySpan<char> text = source.Text.Span.Slice(start.Position, reader.Position - start.Position);
            return Make(KeywordOrIdentifier(text), start);
        }

        /// <summary>
        /// Scans a numeric literal (§5.8): decimal, <c>0x</c> hex or <c>0b</c> binary, with <c>_</c>
        /// digit-group separators allowed in any base, and a decimal point or an exponent - never a
        /// suffix - deciding whether the result is a float.
        /// </summary>
        private Token ScanNumber(SourceLocation start)
        {
            if (reader.Current == '0' && (reader.Peek(1) == 'x' || reader.Peek(1) == 'X'))
            {
                return ScanRadixNumber(start, 16, DigitKind.Hex, "hexadecimal");
            }

            if (reader.Current == '0' && (reader.Peek(1) == 'b' || reader.Peek(1) == 'B'))
            {
                return ScanRadixNumber(start, 2, DigitKind.Binary, "binary");
            }

            SkipDigits(DigitKind.Decimal);

            bool isFloat = false;

            // A `.` only opens a fractional part when a digit follows it. That is what keeps
            // `0..10` lexing as an integer and a range rather than a malformed float, and it also
            // leaves `1.toString()` available as member access on an integer.
            if (reader.Current == '.' && IsDigit(reader.Peek(1)))
            {
                isFloat = true;
                reader.Skip();
                SkipDigits(DigitKind.Decimal);
            }

            if (reader.Current == 'e' || reader.Current == 'E')
            {
                int offset = (reader.Peek(1) == '+' || reader.Peek(1) == '-') ? 2 : 1;

                if (IsDigit(reader.Peek(offset)))
                {
                    isFloat = true;
                    reader.Skip(offset);
                    SkipDigits(DigitKind.Decimal);
                }
            }

            ReadOnlySpan<char> digits = source.Text.Span.Slice(start.Position, reader.Position - start.Position);

            if (isFloat)
            {
                if (!TryParseFloat(digits, out double floatValue))
                {
                    throw Error(SurtrDiagnosticCode.InvalidNumericLiteral, $"'{digits.ToString()}' is not a valid floating-point literal.", start);
                }

                return Make(TokenType.FloatLiteral, start, TokenPayload.ForFloat(floatValue));
            }

            if (!TryParseInteger(digits, out long intValue))
            {
                throw Error(SurtrDiagnosticCode.InvalidNumericLiteral, $"'{digits.ToString()}' is not a valid integer literal.", start);
            }

            return Make(TokenType.IntegerLiteral, start, TokenPayload.ForInteger(intValue));
        }

        /// <summary>Scans a <c>0x</c> or <c>0b</c> literal. <paramref name="radix"/> and <paramref name="kind"/> are what differ between the two.</summary>
        private Token ScanRadixNumber(SourceLocation start, int radix, DigitKind kind, string description)
        {
            reader.Skip(2);

            int digitsStart = reader.Position;
            SkipDigits(kind);

            ReadOnlySpan<char> digits = source.Text.Span.Slice(digitsStart, reader.Position - digitsStart);

            if (digits.Length == 0)
            {
                throw Error(SurtrDiagnosticCode.InvalidNumericLiteral, $"A {description} literal needs at least one digit.", start);
            }

            long value = 0;
            int digitCount = 0;
            try
            {
                for (int i = 0; i < digits.Length; i++)
                {
                    char current = digits[i];
                    if (current == '_')
                        continue;

                    digitCount++;
                    value = checked((value * radix) + DigitValue(current));
                }
            }
            catch (OverflowException)
            {
                throw Error(SurtrDiagnosticCode.NumericLiteralOutOfRange, $"The {description} literal is too large to fit in an integer.", start);
            }

            if (digitCount == 0)
            {
                throw Error(SurtrDiagnosticCode.InvalidNumericLiteral, $"A {description} literal needs at least one digit.", start);
            }

            return Make(TokenType.IntegerLiteral, start, TokenPayload.ForInteger(value));
        }

        private enum DigitKind
        {
            Decimal,
            Hex,
            Binary,
        }

        /// <summary>Advances over the digits <paramref name="kind"/> accepts, allowing <c>_</c> separators between them.</summary>
        private void SkipDigits(DigitKind kind)
        {
            while (!reader.IsAtEnd && (IsDigitOfKind(reader.Current, kind) || reader.Current == '_'))
            {
                reader.Skip();
            }
        }

        private static bool IsDigitOfKind(char c, DigitKind kind) => kind switch
        {
            DigitKind.Decimal => c >= '0' && c <= '9',
            DigitKind.Hex => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'),
            _ => c == '0' || c == '1',
        };

        /// <summary>Parses a decimal integer literal over its raw span, stripping <c>_</c> separators only when present.</summary>
        private static bool TryParseInteger(ReadOnlySpan<char> digits, out long value)
        {
            if (digits.IndexOf('_') < 0)
                return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);

            Span<char> buffer = digits.Length <= 64 ? stackalloc char[64] : new char[digits.Length];
            int count = 0;
            for (int i = 0; i < digits.Length; i++)
            {
                if (digits[i] != '_')
                    buffer[count++] = digits[i];
            }

            return long.TryParse(buffer.Slice(0, count), NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Parses a float literal over its raw span, stripping <c>_</c> separators only when present.</summary>
        private static bool TryParseFloat(ReadOnlySpan<char> digits, out double value)
        {
            if (digits.IndexOf('_') < 0)
                return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

            Span<char> buffer = digits.Length <= 64 ? stackalloc char[64] : new char[digits.Length];
            int count = 0;
            for (int i = 0; i < digits.Length; i++)
            {
                if (digits[i] != '_')
                    buffer[count++] = digits[i];
            }

            return double.TryParse(buffer.Slice(0, count), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Scans a string literal (§5.8), deciding as it goes whether the result is a plain
        /// <see cref="TokenType.StringLiteral"/> or an
        /// <see cref="TokenType.InterpolatedStringLiteral"/>.
        /// </summary>
        /// <remarks>
        /// Escapes are validated either way, so a bad one is reported here rather than surviving
        /// into the parser. The decoded value is only <em>used</em> when the literal has no
        /// interpolation: an interpolated literal keeps its raw text instead, because splitting it
        /// into text and expression parts is a parser-stage concern that still needs to see which
        /// dollars were written <c>\$</c>.
        /// </remarks>
        private Token ScanString(SourceLocation start)
        {
            reader.Skip();

            int contentStart = reader.Position;
            int decodedLength = 0;
            bool interpolated = false;

            // First pass: find the closing quote, validating every escape via ScanEscape (which is
            // what reports a bad one), counting the decoded length and detecting an unescaped `$`.
            while (true)
            {
                if (reader.IsAtEnd)
                {
                    throw Error(SurtrDiagnosticCode.UnterminatedStringLiteral, "Unterminated string literal.", start);
                }

                char current = reader.Current;

                if (current == '"')
                {
                    break;
                }

                if (current == '\n')
                {
                    throw Error(SurtrDiagnosticCode.LiteralSpansLines, "A string literal cannot span multiple lines.", start);
                }

                if (current == '\\')
                {
                    ScanEscape();
                    decodedLength++;
                    continue;
                }

                // Any `$` reaching here is unescaped - ScanEscape consumed the escaped ones.
                if (current == '$')
                {
                    interpolated = true;
                }

                decodedLength++;
                reader.Skip();
            }

            int contentLength = reader.Position - contentStart;
            reader.Skip();

            if (interpolated)
            {
                string raw = source.Text.Slice(contentStart, contentLength).ToString();
                return Make(TokenType.InterpolatedStringLiteral, start, TokenPayload.ForString(raw));
            }

            // Decoded length never exceeds the raw content (an escape only shrinks), so the raw
            // content bounds the buffer. Small strings stay on the stack; the only heap allocation
            // is the final string itself — no StringBuilder in between.
            Span<char> buffer = contentLength <= 256 ? stackalloc char[contentLength] : new char[contentLength];
            int count = DecodeSpan(source.Text.Span.Slice(contentStart, contentLength), buffer);
            return Make(TokenType.StringLiteral, start, TokenPayload.ForString(new string(buffer.Slice(0, count))));
        }

        /// <summary>Decodes an already-validated string's content into <paramref name="destination"/>. Pass 1 validated every escape, so nothing here can fail.</summary>
        private static int DecodeSpan(ReadOnlySpan<char> content, Span<char> destination)
        {
            int source = 0;
            int target = 0;

            while (source < content.Length)
            {
                char current = content[source];
                if (current == '\\')
                {
                    char escape = content[source + 1];
                    source += 2;

                    switch (escape)
                    {
                        case 'n': destination[target++] = '\n'; break;
                        case 't': destination[target++] = '\t'; break;
                        case 'r': destination[target++] = '\r'; break;
                        case '0': destination[target++] = '\0'; break;
                        case '\\': destination[target++] = '\\'; break;
                        case '\'': destination[target++] = '\''; break;
                        case '"': destination[target++] = '"'; break;
                        case '$': destination[target++] = '$'; break;
                        case 'u':
                        {
                            int value = 0;
                            for (int i = 0; i < 4; i++)
                                value = (value * 16) + DigitValue(content[source + i]);
                            source += 4;
                            destination[target++] = (char)value;
                            break;
                        }
                    }
                }
                else
                {
                    destination[target++] = current;
                    source++;
                }
            }

            return target;
        }

        /// <summary>Scans a character literal: exactly one character, or one escape sequence, between single quotes.</summary>
        private Token ScanCharacter(SourceLocation start)
        {
            reader.Skip();

            if (reader.IsAtEnd || reader.Current == '\'')
            {
                throw Error(SurtrDiagnosticCode.InvalidCharacterLiteral, "A character literal cannot be empty.", start);
            }

            char value = reader.Current == '\\' ? ScanEscape() : ScanPlainCharacter(start);

            if (reader.IsAtEnd || reader.Current != '\'')
            {
                throw Error(SurtrDiagnosticCode.InvalidCharacterLiteral, "A character literal must hold exactly one character.", start);
            }

            reader.Skip();
            return Make(TokenType.CharacterLiteral, start, TokenPayload.ForCharacter(value));
        }

        /// <summary>Consumes one ordinary (unescaped) character of a character literal.</summary>
        private char ScanPlainCharacter(SourceLocation start)
        {
            if (reader.Current == '\n')
            {
                throw Error(SurtrDiagnosticCode.LiteralSpansLines, "A character literal cannot span multiple lines.", start);
            }

            return reader.Advance();
        }

        /// <summary>
        /// Consumes a backslash escape and returns the character it denotes. Covers §5.8's set -
        /// <c>\n \t \r \\ \' \" \0</c> and <c>\uXXXX</c> - plus §5.2's <c>\$</c>, which is what
        /// lets a literal dollar sign survive inside an interpolated string.
        /// </summary>
        private char ScanEscape()
        {
            SourceLocation start = SourceLocation.FromCharReader(reader);
            reader.Skip();

            if (reader.IsAtEnd)
            {
                throw Error(SurtrDiagnosticCode.InvalidEscapeSequence, "Unterminated escape sequence.", start);
            }

            char escape = reader.Advance();

            switch (escape)
            {
                case 'n': return '\n';
                case 't': return '\t';
                case 'r': return '\r';
                case '0': return '\0';
                case '\\': return '\\';
                case '\'': return '\'';
                case '"': return '"';
                case '$': return '$';
                case 'u': return ScanUnicodeEscape(start);
                default:
                    throw Error(SurtrDiagnosticCode.InvalidEscapeSequence, $"Unrecognized escape sequence '\\{escape}'.", start);
            }
        }

        /// <summary>Consumes the four hex digits of a <c>\uXXXX</c> escape and returns the code point they name.</summary>
        private char ScanUnicodeEscape(SourceLocation start)
        {
            int value = 0;

            for (int i = 0; i < 4; i++)
            {
                if (reader.IsAtEnd || !IsHexDigit(reader.Current))
                {
                    throw Error(SurtrDiagnosticCode.InvalidEscapeSequence, "A '\\u' escape needs exactly four hexadecimal digits.", start);
                }

                value = (value * 16) + DigitValue(reader.Advance());
            }

            return (char)value;
        }

        /// <summary>
        /// Scans an operator or a punctuation mark. Each arm is ordered longest-match-first, which
        /// is what implements maximal munch; see the remarks on this class.
        /// </summary>
        private Token ScanOperator(SourceLocation start)
        {
            char current = reader.Advance();

            switch (current)
            {
                case '(': return Make(TokenType.LeftParen, start);
                case ')': return Make(TokenType.RightParen, start);
                case '{': return Make(TokenType.LeftBrace, start);
                case '}': return Make(TokenType.RightBrace, start);
                case '[': return Make(TokenType.LeftBracket, start);
                case ']': return Make(TokenType.RightBracket, start);
                case ';': return Make(TokenType.Semicolon, start);
                case ',': return Make(TokenType.Comma, start);
                case ':': return Make(TokenType.Colon, start);
                case '@': return Make(TokenType.At, start);
                case '~': return Make(TokenType.Tilde, start);

                case '.':
                    if (reader.Match('.'))
                    {
                        if (reader.Match('.')) return Make(TokenType.Ellipsis, start);
                        if (reader.Match('=')) return Make(TokenType.DotDotEquals, start);
                        return Make(TokenType.DotDot, start);
                    }
                    return Make(TokenType.Dot, start);

                case '=':
                    if (reader.Match('='))
                    {
                        return Make(reader.Match('=') ? TokenType.ReferenceEqual : TokenType.Equal, start);
                    }
                    if (reader.Match('>')) return Make(TokenType.FatArrow, start);
                    return Make(TokenType.Assign, start);

                case '!':
                    if (reader.Match('='))
                    {
                        return Make(reader.Match('=') ? TokenType.ReferenceNotEqual : TokenType.NotEqual, start);
                    }
                    if (reader.Match('!')) return Make(TokenType.BangBang, start);
                    return Make(TokenType.LogicalNot, start);

                case '<':
                    if (reader.Match('='))
                    {
                        return Make(reader.Match('>') ? TokenType.Spaceship : TokenType.LessEqual, start);
                    }
                    if (reader.Match('<'))
                    {
                        return Make(reader.Match('=') ? TokenType.ShiftLeftAssign : TokenType.ShiftLeft, start);
                    }
                    return Make(TokenType.Less, start);

                case '>':
                    if (reader.Match('=')) return Make(TokenType.GreaterEqual, start);
                    if (reader.Match('>'))
                    {
                        if (reader.Match('>'))
                        {
                            return Make(reader.Match('=') ? TokenType.UnsignedShiftRightAssign : TokenType.UnsignedShiftRight, start);
                        }
                        return Make(reader.Match('=') ? TokenType.ShiftRightAssign : TokenType.ShiftRight, start);
                    }
                    return Make(TokenType.Greater, start);

                case '?':
                    if (reader.Match('?'))
                    {
                        return Make(reader.Match('=') ? TokenType.NullCoalesceAssign : TokenType.NullCoalesce, start);
                    }
                    if (reader.Match('.')) return Make(TokenType.QuestionDot, start);
                    return Make(TokenType.Question, start);

                case '-':
                    if (reader.Match('>')) return Make(TokenType.Arrow, start);
                    if (reader.Match('=')) return Make(TokenType.MinusAssign, start);
                    if (reader.Match('-')) return Make(TokenType.Decrement, start);
                    return Make(TokenType.Minus, start);

                case '+':
                    if (reader.Match('=')) return Make(TokenType.PlusAssign, start);
                    if (reader.Match('+')) return Make(TokenType.Increment, start);
                    return Make(TokenType.Plus, start);

                case '*':
                    return Make(reader.Match('=') ? TokenType.StarAssign : TokenType.Star, start);

                case '/':
                    return Make(reader.Match('=') ? TokenType.SlashAssign : TokenType.Slash, start);

                case '%':
                    return Make(reader.Match('=') ? TokenType.PercentAssign : TokenType.Percent, start);

                case '&':
                    if (reader.Match('&')) return Make(TokenType.LogicalAnd, start);
                    return Make(reader.Match('=') ? TokenType.AmpersandAssign : TokenType.Ampersand, start);

                case '|':
                    if (reader.Match('|')) return Make(TokenType.LogicalOr, start);
                    return Make(reader.Match('=') ? TokenType.PipeAssign : TokenType.Pipe, start);

                case '^':
                    return Make(reader.Match('=') ? TokenType.CaretAssign : TokenType.Caret, start);

                default:
                    throw Error(SurtrDiagnosticCode.UnexpectedCharacter, $"'{current}' does not begin any token.", start);
            }
        }

        /// <summary>Builds a token spanning from <paramref name="start"/> to the cursor's current position.</summary>
        private Token Make(TokenType type, SourceLocation start, TokenPayload payload = default)
        {
            ReadOnlyMemory<char> lexeme = source.Text.Slice(start.Position, reader.Position - start.Position);
            return new Token(type, lexeme, start, payload);
        }

        /// <summary>
        /// Reports a malformed piece of input and hands back the exception that abandons the token.
        /// </summary>
        /// <remarks>
        /// Reporting and throwing are one step because they are one decision: the diagnostic is
        /// recorded whether or not anyone catches the exception, so <see cref="Tokenize"/> can
        /// recover and keep scanning while a caller that wanted the simple behaviour still gets it.
        /// The span runs from where the token started to wherever scanning gave up, which is what
        /// lets a tool underline the whole bad literal rather than its first character.
        /// </remarks>
        private SurtrLexerException Error(SurtrDiagnosticCode code, string message, SourceLocation start)
        {
            var diagnostic = new SurtrDiagnostic(
                code,
                SurtrDiagnosticSeverity.Error,
                message,
                source.Name,
                SourceSpan.FromBounds(start, reader.Position));

            diagnostics.Report(diagnostic);
            return new SurtrLexerException(diagnostic);
        }

        /// <summary>
        /// Maps an identifier's text to its reserved word, or to <see cref="TokenType.Identifier"/>
        /// if it is not one. Dispatching on length first keeps this to a couple of comparisons per
        /// identifier without allocating a string to look up in a dictionary.
        /// </summary>
        /// <remarks>
        /// This list is §1.2 verbatim. Type names are absent on purpose - §1.1 makes them ordinary
        /// identifiers - and so are the contextual keywords <c>this</c>, <c>super</c> and
        /// <c>value</c>, which §3.2 leaves to the parser.
        /// </remarks>
        private static TokenType KeywordOrIdentifier(ReadOnlySpan<char> text)
        {
            switch (text.Length)
            {
                case 2:
                    if (Is(text, "as")) return TokenType.KeywordAs;
                    if (Is(text, "if")) return TokenType.KeywordIf;
                    if (Is(text, "in")) return TokenType.KeywordIn;
                    if (Is(text, "is")) return TokenType.KeywordIs;
                    break;

                case 3:
                    if (Is(text, "for")) return TokenType.KeywordFor;
                    if (Is(text, "fun")) return TokenType.KeywordFun;
                    if (Is(text, "let")) return TokenType.KeywordLet;
                    if (Is(text, "try")) return TokenType.KeywordTry;
                    if (Is(text, "var")) return TokenType.KeywordVar;
                    break;

                case 4:
                    if (Is(text, "case")) return TokenType.KeywordCase;
                    if (Is(text, "else")) return TokenType.KeywordElse;
                    if (Is(text, "enum")) return TokenType.KeywordEnum;
                    if (Is(text, "null")) return TokenType.KeywordNull;
                    if (Is(text, "true")) return TokenType.KeywordTrue;
                    break;

                case 5:
                    if (Is(text, "alias")) return TokenType.KeywordAlias;
                    if (Is(text, "break")) return TokenType.KeywordBreak;
                    if (Is(text, "catch")) return TokenType.KeywordCatch;
                    if (Is(text, "class")) return TokenType.KeywordClass;
                    if (Is(text, "const")) return TokenType.KeywordConst;
                    if (Is(text, "false")) return TokenType.KeywordFalse;
                    if (Is(text, "throw")) return TokenType.KeywordThrow;
                    if (Is(text, "while")) return TokenType.KeywordWhile;
                    break;

                case 6:
                    if (Is(text, "export")) return TokenType.KeywordExport;
                    if (Is(text, "import")) return TokenType.KeywordImport;
                    if (Is(text, "inline")) return TokenType.KeywordInline;
                    if (Is(text, "native")) return TokenType.KeywordNative;
                    if (Is(text, "public")) return TokenType.KeywordPublic;
                    if (Is(text, "return")) return TokenType.KeywordReturn;
                    if (Is(text, "sealed")) return TokenType.KeywordSealed;
                    if (Is(text, "static")) return TokenType.KeywordStatic;
                    if (Is(text, "switch")) return TokenType.KeywordSwitch;
                    if (Is(text, "typeof")) return TokenType.KeywordTypeOf;
                    break;

                case 7:
                    if (Is(text, "default")) return TokenType.KeywordDefault;
                    if (Is(text, "finally")) return TokenType.KeywordFinally;
                    if (Is(text, "private")) return TokenType.KeywordPrivate;
                    if (Is(text, "virtual")) return TokenType.KeywordVirtual;
                    break;

                case 8:
                    if (Is(text, "abstract")) return TokenType.KeywordAbstract;
                    if (Is(text, "continue")) return TokenType.KeywordContinue;
                    if (Is(text, "internal")) return TokenType.KeywordInternal;
                    if (Is(text, "moduleof")) return TokenType.KeywordModuleOf;
                    if (Is(text, "noinline")) return TokenType.KeywordNoInline;
                    if (Is(text, "operator")) return TokenType.KeywordOperator;
                    if (Is(text, "override")) return TokenType.KeywordOverride;
                    break;

                case 9:
                    if (Is(text, "extension")) return TokenType.KeywordExtension;
                    if (Is(text, "interface")) return TokenType.KeywordInterface;
                    if (Is(text, "protected")) return TokenType.KeywordProtected;
                    if (Is(text, "singleton")) return TokenType.KeywordSingleton;
                    break;

                case 11:
                    if (Is(text, "constructor")) return TokenType.KeywordConstructor;
                    if (Is(text, "forceinline")) return TokenType.KeywordForceInline;
                    break;
            }

            return TokenType.Identifier;
        }

        private static bool Is(ReadOnlySpan<char> text, string keyword) => text.SequenceEqual(keyword.AsSpan());

        private static bool IsIdentifierStart(char c)
            => (uint)(c - 'A') <= 25u || (uint)(c - 'a') <= 25u || c == '_' || char.IsLetter(c);

        private static bool IsIdentifierPart(char c)
            => (uint)(c - 'A') <= 25u || (uint)(c - 'a') <= 25u || (uint)(c - '0') <= 9u || c == '_' || char.IsLetterOrDigit(c);

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static bool IsHexDigit(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private static int DigitValue(char c)
        {
            if (c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return c - 'A' + 10;
        }
    }
}
