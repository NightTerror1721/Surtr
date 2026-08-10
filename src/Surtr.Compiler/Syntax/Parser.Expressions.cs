#nullable enable

using Surtr.Compiler.Diagnostics;
using System.Collections.Generic;
using Surtr.Compiler.Syntax.Ast;

namespace Surtr.Compiler.Syntax
{
    public sealed partial class Parser
    {
        /// <summary>
        /// Precedence of each binary operator, straight from §5.7's table. Higher binds tighter;
        /// zero means the token is not a binary operator. Assignment and the ternary are handled
        /// separately because they are right-associative and their shapes differ.
        /// </summary>
        private static int BinaryPrecedence(TokenType type)
        {
            switch (type)
            {
                case TokenType.NullCoalesce: return 3;
                case TokenType.LogicalOr: return 4;
                case TokenType.LogicalAnd: return 5;
                case TokenType.Pipe: return 6;
                case TokenType.Caret: return 7;
                case TokenType.Ampersand: return 8;

                case TokenType.Equal:
                case TokenType.NotEqual:
                case TokenType.ReferenceEqual:
                case TokenType.ReferenceNotEqual: return 9;

                case TokenType.Less:
                case TokenType.LessEqual:
                case TokenType.Greater:
                case TokenType.GreaterEqual: return 10;

                case TokenType.Spaceship: return 11;

                case TokenType.DotDot:
                case TokenType.DotDotEquals: return 12;

                case TokenType.ShiftLeft:
                case TokenType.ShiftRight:
                case TokenType.UnsignedShiftRight: return 13;

                case TokenType.Plus:
                case TokenType.Minus: return 14;

                case TokenType.Star:
                case TokenType.Slash:
                case TokenType.Percent: return 15;

                default: return 0;
            }
        }

        /// <summary>Maps a token to the operator it denotes. Only called for tokens with a non-zero precedence.</summary>
        private static BinaryOperator ToBinaryOperator(TokenType type)
        {
            switch (type)
            {
                case TokenType.Plus: return BinaryOperator.Add;
                case TokenType.Minus: return BinaryOperator.Subtract;
                case TokenType.Star: return BinaryOperator.Multiply;
                case TokenType.Slash: return BinaryOperator.Divide;
                case TokenType.Percent: return BinaryOperator.Modulo;
                case TokenType.ShiftLeft: return BinaryOperator.ShiftLeft;
                case TokenType.ShiftRight: return BinaryOperator.ShiftRight;
                case TokenType.UnsignedShiftRight: return BinaryOperator.UnsignedShiftRight;
                case TokenType.Ampersand: return BinaryOperator.BitAnd;
                case TokenType.Pipe: return BinaryOperator.BitOr;
                case TokenType.Caret: return BinaryOperator.BitXor;
                case TokenType.LogicalAnd: return BinaryOperator.LogicalAnd;
                case TokenType.LogicalOr: return BinaryOperator.LogicalOr;
                case TokenType.Equal: return BinaryOperator.Equal;
                case TokenType.NotEqual: return BinaryOperator.NotEqual;
                case TokenType.ReferenceEqual: return BinaryOperator.ReferenceEqual;
                case TokenType.ReferenceNotEqual: return BinaryOperator.ReferenceNotEqual;
                case TokenType.Less: return BinaryOperator.Less;
                case TokenType.LessEqual: return BinaryOperator.LessEqual;
                case TokenType.Greater: return BinaryOperator.Greater;
                case TokenType.GreaterEqual: return BinaryOperator.GreaterEqual;
                case TokenType.Spaceship: return BinaryOperator.Compare;
                case TokenType.NullCoalesce: return BinaryOperator.NullCoalesce;
                case TokenType.DotDot: return BinaryOperator.Range;
                default: return BinaryOperator.RangeInclusive;
            }
        }

        /// <summary>Maps an assignment token to its operator, or <c>null</c> when the token is not one.</summary>
        private static AssignmentOperator? ToAssignmentOperator(TokenType type)
        {
            switch (type)
            {
                case TokenType.Assign: return AssignmentOperator.Assign;
                case TokenType.PlusAssign: return AssignmentOperator.Add;
                case TokenType.MinusAssign: return AssignmentOperator.Subtract;
                case TokenType.StarAssign: return AssignmentOperator.Multiply;
                case TokenType.SlashAssign: return AssignmentOperator.Divide;
                case TokenType.PercentAssign: return AssignmentOperator.Modulo;
                case TokenType.AmpersandAssign: return AssignmentOperator.BitAnd;
                case TokenType.PipeAssign: return AssignmentOperator.BitOr;
                case TokenType.CaretAssign: return AssignmentOperator.BitXor;
                case TokenType.ShiftLeftAssign: return AssignmentOperator.ShiftLeft;
                case TokenType.ShiftRightAssign: return AssignmentOperator.ShiftRight;
                case TokenType.UnsignedShiftRightAssign: return AssignmentOperator.UnsignedShiftRight;
                case TokenType.NullCoalesceAssign: return AssignmentOperator.NullCoalesce;
                default: return null;
            }
        }

        /// <summary>Parses a full expression, assignment included.</summary>
        private ExpressionSyntax ParseExpression()
        {
            ExpressionSyntax left = ParseConditional();

            AssignmentOperator? assignment = ToAssignmentOperator(reader.CurrentType);
            if (assignment is null)
            {
                return left;
            }

            SourceLocation start = reader.CurrentLocation;
            reader.Advance();

            // Right-associative: `a = b = c` is `a = (b = c)`, so recurse rather than loop.
            ExpressionSyntax value = ParseExpression();
            return new AssignmentExpressionSyntax(SpanFrom(start), assignment.Value, left, value);
        }

        /// <summary>Parses the ternary, which sits just above assignment and is also right-associative.</summary>
        private ExpressionSyntax ParseConditional()
        {
            ExpressionSyntax condition = ParseBinary(1);

            if (!reader.Check(TokenType.Question))
            {
                return condition;
            }

            SourceLocation start = reader.CurrentLocation;
            reader.Advance();

            ExpressionSyntax whenTrue = ParseExpression();
            reader.Expect(TokenType.Colon, "':' in the conditional expression");
            ExpressionSyntax whenFalse = ParseExpression();

            return new ConditionalExpressionSyntax(SpanFrom(start), condition, whenTrue, whenFalse);
        }

        /// <summary>
        /// Precedence climbing over §5.7's table. <c>is</c>, <c>as</c> and <c>as?</c> are folded in
        /// here rather than given their own methods: they sit at ordinary precedence levels and
        /// differ only in taking a type on the right instead of an expression.
        /// </summary>
        private ExpressionSyntax ParseBinary(int minPrecedence)
        {
            ExpressionSyntax left = ParseUnary();

            while (true)
            {
                // `is` binds at the relational level (10) and takes a type.
                if (reader.Check(TokenType.KeywordIs) && 10 >= minPrecedence)
                {
                    SourceLocation start = reader.CurrentLocation;
                    reader.Advance();
                    left = new TypeTestExpressionSyntax(SpanFrom(start), left, ParseType());
                    continue;
                }

                int precedence = BinaryPrecedence(reader.CurrentType);
                if (precedence == 0 || precedence < minPrecedence)
                {
                    return left;
                }

                BinaryOperator op = ToBinaryOperator(reader.CurrentType);
                reader.Advance();

                // Everything here is left-associative, so the right operand binds one level tighter.
                ExpressionSyntax right = ParseBinary(precedence + 1);

                // The whole operation, not the operator: an error about `a + b` should underline
                // both operands.
                left = new BinaryExpressionSyntax(left.Span.To(right.Span), op, left, right);
            }
        }

        /// <summary>Parses the prefix unary operators, then the cast level, then postfix.</summary>
        private ExpressionSyntax ParseUnary()
        {
            SourceLocation start = reader.CurrentLocation;

            switch (reader.CurrentType)
            {
                case TokenType.Minus:
                    reader.Advance();
                    return new UnaryExpressionSyntax(SpanFrom(start), UnaryOperator.Negate, ParseUnary());

                case TokenType.LogicalNot:
                    reader.Advance();
                    return new UnaryExpressionSyntax(SpanFrom(start), UnaryOperator.Not, ParseUnary());

                case TokenType.Tilde:
                    reader.Advance();
                    return new UnaryExpressionSyntax(SpanFrom(start), UnaryOperator.Complement, ParseUnary());

                case TokenType.Increment:
                    reader.Advance();
                    return new UnaryExpressionSyntax(SpanFrom(start), UnaryOperator.PreIncrement, ParseUnary());

                case TokenType.Decrement:
                    reader.Advance();
                    return new UnaryExpressionSyntax(SpanFrom(start), UnaryOperator.PreDecrement, ParseUnary());

                default:
                    return ParseCast();
            }
        }

        /// <summary>Parses <c>x as T</c> and <c>x as? T</c>, which bind tighter than any prefix operator (§5.7).</summary>
        private ExpressionSyntax ParseCast()
        {
            ExpressionSyntax operand = ParsePostfix();

            while (reader.Check(TokenType.KeywordAs))
            {
                SourceLocation start = reader.CurrentLocation;
                reader.Advance();

                // §5.7: a type must follow `as`, so a `?` here can only be the safe-cast form.
                bool safe = reader.Match(TokenType.Question);
                operand = new CastExpressionSyntax(SpanFrom(start), operand, ParseType(), safe);
            }

            return operand;
        }

        /// <summary>Parses calls, indexing, member access, postfix increment and <c>!!</c>.</summary>
        private ExpressionSyntax ParsePostfix()
        {
            ExpressionSyntax expression = ParsePrimary();

            while (true)
            {
                SourceLocation start = reader.CurrentLocation;

                if (reader.Check(TokenType.Dot) || reader.Check(TokenType.QuestionDot))
                {
                    bool nullConditional = reader.CurrentType == TokenType.QuestionDot;
                    reader.Advance();
                    string name = reader.ExpectIdentifier("a member name after '.'");
                    expression = new MemberAccessExpressionSyntax(SpanFrom(start), expression, name, nullConditional);
                    continue;
                }

                if (reader.Check(TokenType.LeftParen))
                {
                    expression = new CallExpressionSyntax(SpanFrom(start), expression, EmptyTypes, ParseArgumentList());
                    continue;
                }

                if (reader.Check(TokenType.LeftBracket))
                {
                    reader.Advance();
                    ExpressionSyntax index = ParseExpression();
                    reader.Expect(TokenType.RightBracket, "']' to close the index");
                    expression = new IndexExpressionSyntax(SpanFrom(start), expression, index);
                    continue;
                }

                if (reader.Check(TokenType.Increment))
                {
                    reader.Advance();
                    expression = new UnaryExpressionSyntax(SpanFrom(start), UnaryOperator.PostIncrement, expression);
                    continue;
                }

                if (reader.Check(TokenType.Decrement))
                {
                    reader.Advance();
                    expression = new UnaryExpressionSyntax(SpanFrom(start), UnaryOperator.PostDecrement, expression);
                    continue;
                }

                if (reader.Check(TokenType.BangBang))
                {
                    reader.Advance();
                    expression = new UnaryExpressionSyntax(SpanFrom(start), UnaryOperator.NullAssert, expression);
                    continue;
                }

                return expression;
            }
        }

        /// <summary>Parses an argument list, positional or named (§3.5).</summary>
        private IReadOnlyList<ArgumentSyntax> ParseArgumentList()
        {
            reader.Expect(TokenType.LeftParen, "'(' to open the arguments");

            List<ArgumentSyntax> arguments = new List<ArgumentSyntax>();
            while (!reader.Check(TokenType.RightParen))
            {
                SourceLocation start = reader.CurrentLocation;

                // A named argument is `name: value`. An argument list only ever follows a callee,
                // so this cannot be confused with a lambda's parameter list (§3.5).
                string? name = null;
                if (reader.Check(TokenType.Identifier) && reader.CheckAt(1, TokenType.Colon))
                {
                    name = reader.Advance().ToString();
                    reader.Advance();
                }

                arguments.Add(new ArgumentSyntax(SpanFrom(start), name, ParseExpression()));

                if (!reader.Match(TokenType.Comma))
                {
                    break;
                }
            }

            reader.Expect(TokenType.RightParen, "')' to close the arguments");
            return arguments;
        }

        /// <summary>Parses the atoms: literals, names, and the bracketed forms.</summary>
        private ExpressionSyntax ParsePrimary()
        {
            SourceLocation start = reader.CurrentLocation;

            switch (reader.CurrentType)
            {
                case TokenType.IntegerLiteral:
                case TokenType.FloatLiteral:
                case TokenType.StringLiteral:
                case TokenType.CharacterLiteral:
                case TokenType.KeywordTrue:
                case TokenType.KeywordFalse:
                case TokenType.KeywordNull:
                    return new LiteralExpressionSyntax(reader.Advance());

                case TokenType.InterpolatedStringLiteral:
                    return ParseInterpolatedString();

                case TokenType.KeywordSwitch:
                    return ParseSwitchExpression();

                case TokenType.LeftBracket:
                    return ParseArrayLiteral();

                case TokenType.LeftBrace:
                    return ParseDictLiteral();

                case TokenType.LeftParen:
                    return IsLambdaAhead() ? ParseLambda() : ParseParenthesizedOrTuple();

                case TokenType.Identifier:
                    // `this` and `super` are contextual (§3.2), so they arrive as identifiers.
                    if (CheckContextual("this"))
                    {
                        reader.Advance();
                        return new ThisExpressionSyntax(SpanFrom(start));
                    }

                    if (CheckContextual("super"))
                    {
                        reader.Advance();
                        return new SuperExpressionSyntax(SpanFrom(start));
                    }

                    // Consumed first: arguments evaluate left to right, so a SpanFrom sitting
                    // beside the Advance would close the span before the token was read.
                    string name = reader.Advance().ToString();
                    return new IdentifierExpressionSyntax(SpanFrom(start), name);

                default:
                    throw reader.Error(SurtrDiagnosticCode.ExpectedExpression, $"Expected an expression, found {reader.CurrentType}.");
            }
        }

        /// <summary>
        /// Re-lexes an interpolated literal's raw text into its alternating text and expression
        /// parts (§5.2). The lexer deliberately leaves this whole, because splitting it means
        /// parsing the spliced expressions — which is this layer's job, not the lexer's.
        /// </summary>
        private ExpressionSyntax ParseInterpolatedString()
        {
            Token token = reader.Advance();
            string raw = token.Payload.AsString;

            List<ExpressionSyntax> parts = new List<ExpressionSyntax>();
            System.Text.StringBuilder text = new System.Text.StringBuilder();

            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];

                if (c == '\\' && i + 1 < raw.Length)
                {
                    text.Append(DecodeEscape(raw, ref i));
                    continue;
                }

                if (c != '$')
                {
                    text.Append(c);
                    continue;
                }

                FlushInterpolationText(parts, text, token);

                if (i + 1 < raw.Length && raw[i + 1] == '{')
                {
                    int depth = 0;
                    int expressionStart = i + 2;
                    int j = i + 1;

                    for (; j < raw.Length; j++)
                    {
                        if (raw[j] == '{') depth++;
                        else if (raw[j] == '}' && --depth == 0) break;
                    }

                    if (depth != 0)
                    {
                        throw reader.Error(SurtrDiagnosticCode.InvalidInterpolation, "Unterminated '${' in the interpolated string.", token.Span);
                    }

                    parts.Add(ParseInterpolationHole(raw.Substring(expressionStart, j - expressionStart), token));
                    i = j;
                    continue;
                }

                int nameStart = i + 1;
                int nameEnd = nameStart;
                while (nameEnd < raw.Length && (char.IsLetterOrDigit(raw[nameEnd]) || raw[nameEnd] == '_'))
                {
                    nameEnd++;
                }

                if (nameEnd == nameStart)
                {
                    throw reader.Error(SurtrDiagnosticCode.InvalidInterpolation, "A '$' in an interpolated string must be followed by a name or '{'.", token.Span);
                }

                // Every part takes the whole literal's span. The text a part was decoded from does
                // not sit at a fixed offset in the source — escapes and `${` holes shift it — and a
                // hole's own nodes come from a nested parser working in its own coordinates, so
                // precise sub-spans would mean remapping a whole subtree. Worth doing when a binder
                // starts reporting inside interpolations; not before.
                parts.Add(new IdentifierExpressionSyntax(token.Span, raw.Substring(nameStart, nameEnd - nameStart)));
                i = nameEnd - 1;
            }

            FlushInterpolationText(parts, text, token);
            return new InterpolatedStringExpressionSyntax(token.Span, parts);
        }

        /// <summary>Parses one <c>${ ... }</c> hole by running a nested parser over its text.</summary>
        private ExpressionSyntax ParseInterpolationHole(string source, Token token)
        {
            Parser inner = new Parser(SurtrSourceBuffer.FromString(source, reader.SourceName));
            ExpressionSyntax expression = inner.ParseExpression();

            if (!inner.reader.Check(TokenType.EndOfFile))
            {
                throw reader.Error(SurtrDiagnosticCode.InvalidInterpolation, "An interpolation hole must hold exactly one expression.", token.Span);
            }

            return expression;
        }

        /// <summary>Emits the accumulated literal text as a part, if there is any.</summary>
        private static void FlushInterpolationText(List<ExpressionSyntax> parts, System.Text.StringBuilder text, Token token)
        {
            if (text.Length == 0)
            {
                return;
            }

            Token literal = new Token(TokenType.StringLiteral, token.Lexeme, token.Location, TokenPayload.ForString(text.ToString()));
            parts.Add(new LiteralExpressionSyntax(literal));
            text.Clear();
        }

        /// <summary>Decodes one escape inside an interpolated literal's raw text.</summary>
        private static char DecodeEscape(string raw, ref int index)
        {
            char escape = raw[++index];

            switch (escape)
            {
                case 'n': return '\n';
                case 't': return '\t';
                case 'r': return '\r';
                case '0': return '\0';
                case 'u':
                    string hex = raw.Substring(index + 1, 4);
                    index += 4;
                    return (char)System.Convert.ToInt32(hex, 16);
                default: return escape;
            }
        }

        /// <summary>Parses <c>[a, b, c]</c>.</summary>
        private ExpressionSyntax ParseArrayLiteral()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.LeftBracket, "'[' to open the array literal");

            List<ExpressionSyntax> elements = new List<ExpressionSyntax>();
            while (!reader.Check(TokenType.RightBracket))
            {
                elements.Add(ParseExpression());
                if (!reader.Match(TokenType.Comma))
                {
                    break;
                }
            }

            reader.Expect(TokenType.RightBracket, "']' to close the array literal");
            return new ArrayLiteralExpressionSyntax(SpanFrom(start), elements);
        }

        /// <summary>Parses <c>{ k: v }</c>. Only reachable in expression position, per §5.4.</summary>
        private ExpressionSyntax ParseDictLiteral()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.LeftBrace, "'{' to open the dictionary literal");

            List<DictEntrySyntax> entries = new List<DictEntrySyntax>();
            while (!reader.Check(TokenType.RightBrace))
            {
                SourceLocation entryStart = reader.CurrentLocation;
                ExpressionSyntax key = ParseExpression();
                reader.Expect(TokenType.Colon, "':' between the key and value");
                entries.Add(new DictEntrySyntax(SpanFrom(entryStart), key, ParseExpression()));

                if (!reader.Match(TokenType.Comma))
                {
                    break;
                }
            }

            reader.Expect(TokenType.RightBrace, "'}' to close the dictionary literal");
            return new DictLiteralExpressionSyntax(SpanFrom(start), entries);
        }

        /// <summary>Parses <c>(a)</c> as a grouping and <c>(a, b)</c> as a tuple.</summary>
        private ExpressionSyntax ParseParenthesizedOrTuple()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.LeftParen, "'('");

            List<ExpressionSyntax> elements = new List<ExpressionSyntax>();
            while (!reader.Check(TokenType.RightParen))
            {
                elements.Add(ParseExpression());
                if (!reader.Match(TokenType.Comma))
                {
                    break;
                }
            }

            reader.Expect(TokenType.RightParen, "')'");

            if (elements.Count == 0)
            {
                throw reader.Error(SurtrDiagnosticCode.ExpectedExpression, "Expected an expression inside the parentheses.", start);
            }

            // One element is a grouping, not a one-element tuple; the node it wrapped is enough.
            return elements.Count == 1 ? elements[0] : new TupleLiteralExpressionSyntax(SpanFrom(start), elements);
        }

        /// <summary>
        /// Decides whether the <c>(</c> at the cursor opens a lambda's parameter list rather than a
        /// grouping or a tuple, by scanning balanced parentheses for a following <c>=&gt;</c>.
        /// </summary>
        /// <remarks>
        /// The two forms are identical up to the closing paren — <c>(a, b)</c> is a tuple and
        /// <c>(a, b) =&gt; a + b</c> a lambda — so no bounded lookahead settles it. Scanning is
        /// cheap because a parameter list is short and the scan never nests into another one.
        /// </remarks>
        private bool IsLambdaAhead()
        {
            int depth = 0;

            for (int offset = 0; ; offset++)
            {
                TokenType type = reader.Peek(offset).Type;

                switch (type)
                {
                    case TokenType.LeftParen:
                        depth++;
                        break;

                    case TokenType.RightParen:
                        if (--depth == 0)
                        {
                            return reader.Peek(offset + 1).Type == TokenType.FatArrow;
                        }
                        break;

                    case TokenType.EndOfFile:
                        return false;
                }
            }
        }

        /// <summary>Parses <c>(params) =&gt; expr</c> and <c>(params) =&gt; { ... }</c> (§8).</summary>
        private ExpressionSyntax ParseLambda()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.LeftParen, "'(' to open the lambda parameters");

            List<ParameterSyntax> parameters = new List<ParameterSyntax>();
            while (!reader.Check(TokenType.RightParen))
            {
                SourceLocation parameterStart = reader.CurrentLocation;
                string name = reader.ExpectIdentifier("a lambda parameter name");

                // §5.9: the annotation is optional when a target type supplies it.
                TypeSyntax? type = reader.Match(TokenType.Colon) ? ParseType() : null;
                parameters.Add(new ParameterSyntax(SpanFrom(parameterStart), name, type, null, false));

                if (!reader.Match(TokenType.Comma))
                {
                    break;
                }
            }

            reader.Expect(TokenType.RightParen, "')' to close the lambda parameters");
            reader.Expect(TokenType.FatArrow, "'=>' in the lambda");

            if (reader.Check(TokenType.LeftBrace))
            {
                return new LambdaExpressionSyntax(SpanFrom(start), parameters, null, ParseBlock());
            }

            return new LambdaExpressionSyntax(SpanFrom(start), parameters, ParseExpression(), null);
        }

        /// <summary>Parses the expression form of <c>switch</c> (§4.3).</summary>
        private ExpressionSyntax ParseSwitchExpression()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.KeywordSwitch, "'switch'");
            reader.Expect(TokenType.LeftParen, "'(' after 'switch'");
            ExpressionSyntax subject = ParseExpression();
            reader.Expect(TokenType.RightParen, "')' after the switch subject");
            reader.Expect(TokenType.LeftBrace, "'{' to open the switch arms");

            List<SwitchExpressionArmSyntax> arms = new List<SwitchExpressionArmSyntax>();
            while (!reader.Check(TokenType.RightBrace))
            {
                SourceLocation armStart = reader.CurrentLocation;
                List<ExpressionSyntax> values = new List<ExpressionSyntax>();

                if (!reader.Match(TokenType.KeywordElse))
                {
                    do
                    {
                        values.Add(ParseExpression());
                    }
                    while (reader.Match(TokenType.Comma));
                }

                reader.Expect(TokenType.Arrow, "'->' in the switch arm");
                arms.Add(new SwitchExpressionArmSyntax(SpanFrom(armStart), values, ParseExpression()));

                if (!reader.Match(TokenType.Comma))
                {
                    break;
                }
            }

            reader.Expect(TokenType.RightBrace, "'}' to close the switch arms");
            return new SwitchExpressionSyntax(SpanFrom(start), subject, arms);
        }
    }
}
