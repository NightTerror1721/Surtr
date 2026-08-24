#nullable enable

using Surtr.Compiler.Diagnostics;
using System;
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

        /// <summary>Parses a full expression, assignment and <c>yield</c> included.</summary>
        private ExpressionSyntax ParseExpression()
        {
            if (reader.Check(TokenType.KeywordYield))
                return ParseYield();

            ExpressionSyntax left = ParseConditional();

            AssignmentOperator? assignment = ToAssignmentOperator(reader.CurrentType);
            if (assignment is null)
            {
                return left;
            }

            reader.Advance();

            // Right-associative: `a = b = c` is `a = (b = c)`, so recurse rather than loop.
            ExpressionSyntax value = ParseExpression();

            // From the target, as the binary operators do: the assignment is `a = b`, and a span
            // starting at the `=` would leave the target outside the node that assigns to it.
            return new AssignmentExpressionSyntax(SpanFrom(left.Span.Start), assignment.Value, left, value);
        }

        /// <summary>Parses <c>yield</c> and <c>yield from</c>, at the lowest precedence there is (§3.7).</summary>
        /// <remarks>
        /// The operand is a full expression, so <c>yield a + b</c> yields the sum and using a
        /// <c>yield</c> as an operand needs parentheses - JavaScript's and Python's rule, and the
        /// only one that keeps <c>yield</c> readable as a statement.
        /// </remarks>
        private ExpressionSyntax ParseYield()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.KeywordYield, "'yield'");

            // `from` is contextual and recognized in exactly one place: directly after `yield`
            // (§3.7). Reserving it would be far too costly for a word this attractive as an
            // identifier — `countdown(from: int)` is the spec's own example — and one token of
            // lookahead settles it without ambiguity anywhere else. The cost is that a variable
            // literally named `from` cannot be yielded bare inside a generator; `yield (from)` is
            // how you say that.
            bool delegating = CheckContextual("from");
            if (delegating)
                reader.Advance();

            ExpressionSyntax yielded = ParseExpression();
            return new YieldExpressionSyntax(SpanFrom(start), yielded, delegating);
        }

        /// <summary>Parses the ternary, which sits just above assignment and is also right-associative.</summary>
        private ExpressionSyntax ParseConditional()
        {
            ExpressionSyntax condition = ParseBinary(1);

            if (!reader.Check(TokenType.Question))
            {
                return condition;
            }

            reader.Advance();

            ExpressionSyntax whenTrue = ParseExpression();
            reader.Expect(TokenType.Colon, "':' in the conditional expression");
            ExpressionSyntax whenFalse = ParseExpression();

            // The whole conditional, condition included, not the `? :` part of it.
            return new ConditionalExpressionSyntax(SpanFrom(condition.Span.Start), condition, whenTrue, whenFalse);
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
                    reader.Advance();

                    // Spans the operand as the other binary operators do: the test is `x is T`. The
                    // type is read into a local first, for the reason the call below gives: arguments
                    // evaluate left to right, so `SpanFrom` inlined here would measure up to `is` and
                    // stop before the type it is testing against.
                    TypeSyntax tested = ParseType();
                    left = new TypeTestExpressionSyntax(SpanFrom(left.Span.Start), left, tested);
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
                    ExpressionSyntax negated = ParseUnary();
                    return new UnaryExpressionSyntax(SpanFrom(start), UnaryOperator.Negate, negated);

                case TokenType.LogicalNot:
                    reader.Advance();
                    ExpressionSyntax negatedLogically = ParseUnary();
                    return new UnaryExpressionSyntax(SpanFrom(start), UnaryOperator.Not, negatedLogically);

                case TokenType.Tilde:
                    reader.Advance();
                    ExpressionSyntax complemented = ParseUnary();
                    return new UnaryExpressionSyntax(SpanFrom(start), UnaryOperator.Complement, complemented);

                case TokenType.Increment:
                    reader.Advance();
                    ExpressionSyntax incremented = ParseUnary();
                    return new UnaryExpressionSyntax(SpanFrom(start), UnaryOperator.PreIncrement, incremented);

                case TokenType.Decrement:
                    reader.Advance();
                    ExpressionSyntax decremented = ParseUnary();
                    return new UnaryExpressionSyntax(SpanFrom(start), UnaryOperator.PreDecrement, decremented);

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
                reader.Advance();

                // §5.7: a type must follow `as`, so a `?` here can only be the safe-cast form.
                bool safe = reader.Match(TokenType.Question);

                // Spans the operand: the conversion is `x as T`, and a span starting at `as` would
                // put the very value being converted outside the node converting it. The type is
                // read first so the span reaches its end — see the call below.
                TypeSyntax target = ParseType();
                operand = new CastExpressionSyntax(SpanFrom(operand.Span.Start), operand, target, safe);
            }

            return operand;
        }

        /// <summary>
        /// Whether the <c>&lt;</c> at the current position opens a call's type argument list rather
        /// than being a comparison.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The one genuinely ambiguous shape in the expression grammar: <c>a &lt; b &gt; (c)</c> can
        /// be read as two comparisons or as one generic call, and no amount of grammar settles it —
        /// which is why this is a scan rather than a production. It looks ahead over the tokens a type
        /// argument list can contain, and takes the generic reading only when the angles balance and
        /// a <c>(</c> follows the close. Anything else — a literal, an operator, a <c>;</c> — ends the
        /// scan and leaves the <c>&lt;</c> a comparison.
        /// </para>
        /// <para>
        /// Nothing is consumed and nothing is reported, so the fallback costs a bounded look and no
        /// diagnostic: a scan that fails must leave no trace, or a comparison would report the errors
        /// of the type argument list it was never trying to be.
        /// </para>
        /// </remarks>
        private (bool isGenericCall, bool isMemberAccess) LooksLikeTypeArgumentList()
        {
            const int Limit = 256;

            int depth = 0;

            for (int offset = 0; offset < Limit; offset++)
            {
                switch (reader.PeekType(offset))
                {
                    case TokenType.Less:
                        depth++;
                        break;

                    case TokenType.Greater:
                    case TokenType.ShiftRight:
                    case TokenType.UnsignedShiftRight:
                    {
                        // Maximal munch hands back `>>` and `>>>` whole, and in a nested list they
                        // close two and three levels — the same split ConsumeTypeArgumentClose makes.
                        depth -= reader.PeekType(offset) switch
                        {
                            TokenType.Greater => 1,
                            TokenType.ShiftRight => 2,
                            _ => 3,
                        };

                        if (depth > 0)
                            break;

                        // A list that closes more angles than it opened was never one.
                        // After the close comes a `(` (a generic call), or — for the member-access
                        // form — a `.`/`?.` (a generic name reaching a static member). Both facts are
                        // answered by the same scan, so a postfix `<` pays for it exactly once.
                        var following = reader.PeekType(offset + 1);
                        return (
                            depth == 0 && (following == TokenType.LeftParen || following == TokenType.Dot || following == TokenType.QuestionDot),
                            depth == 0 && (following == TokenType.Dot || following == TokenType.QuestionDot));
                    }

                    // Everything a type can be written with: a name, a qualification, a separator,
                    // `?`, and the bracket forms of an array, a dict, a tuple and a closure.
                    case TokenType.Identifier:
                    case TokenType.Dot:
                    case TokenType.Comma:
                    case TokenType.Question:
                    case TokenType.LeftBracket:
                    case TokenType.RightBracket:
                    case TokenType.LeftBrace:
                    case TokenType.RightBrace:
                    case TokenType.Colon:
                    case TokenType.LeftParen:
                    case TokenType.RightParen:
                    case TokenType.Arrow:
                        break;

                    default:
                        return (false, false);
                }
            }

            return (false, false);
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

                    // From the receiver, like the call and index below: an access is `t.value`, not
                    // the `.value` half of it. A span that skips its own receiver is one a tool
                    // cannot walk into — the receiver sits outside the node that owns it.
                    expression = new MemberAccessExpressionSyntax(SpanFrom(expression.Span.Start), expression, name, nullConditional);
                    continue;
                }

                if (reader.Check(TokenType.LeftParen))
                {
                    // The argument list must be consumed before the span is captured: `SpanFrom`
                    // measures up to whatever has been read, and the call's span runs from its callee
                    // to the closing parenthesis. Captured too early it would be degenerate — the call
                    // node would point at `(` alone, which is where every downstream diagnostic would
                    // land.
                    var arguments = ParseArgumentList();
                    expression = new CallExpressionSyntax(SpanFrom(expression.Span.Start), expression, EmptyTypes, arguments);
                    continue;
                }

                // `pick<int>(1, 2)` — a call with its type arguments written out (§6). Only taken
                // when the tokens really close a type argument list and a `(` follows, so
                // `a < b` stays a comparison.
                if (reader.Check(TokenType.Less))
                {
                    var (isGenericCall, isMemberAccess) = LooksLikeTypeArgumentList();

                    if (isGenericCall)
                    {
                        // `Box<int>.prop` / `Box<,>.make()` — a generic name reaching a static member.
                        // The member access branch below consumes the `.`; the generic name is the
                        // receiver it hangs off.
                        if (isMemberAccess)
                        {
                            var nameArguments = ParseWildcardTypeArgumentList();

                            if (expression is IdentifierExpressionSyntax named)
                            {
                                expression = new GenericNameExpressionSyntax(SpanFrom(named.Span.Start), named.Name, nameArguments);
                                continue;
                            }
                        }

                        var typeArguments = ParseTypeArgumentList();
                        var arguments = ParseArgumentList();
                        expression = new CallExpressionSyntax(SpanFrom(expression.Span.Start), expression, typeArguments, arguments);
                        continue;
                    }
                }

                if (reader.Check(TokenType.LeftBracket))
                {
                    reader.Advance();
                    ExpressionSyntax index = ParseExpression();
                    reader.Expect(TokenType.RightBracket, "']' to close the index");
                    // Same order as the call above: the index must be read before the span is
                    // captured, or the node would point at `[` alone.
                    expression = new IndexExpressionSyntax(SpanFrom(expression.Span.Start), expression, index);
                    continue;
                }

                // A postfix operator spans its operand too: `x++` is the operation, `++` alone is not.
                if (reader.Check(TokenType.Increment))
                {
                    reader.Advance();
                    expression = new UnaryExpressionSyntax(SpanFrom(expression.Span.Start), UnaryOperator.PostIncrement, expression);
                    continue;
                }

                if (reader.Check(TokenType.Decrement))
                {
                    reader.Advance();
                    expression = new UnaryExpressionSyntax(SpanFrom(expression.Span.Start), UnaryOperator.PostDecrement, expression);
                    continue;
                }

                if (reader.Check(TokenType.BangBang))
                {
                    reader.Advance();
                    expression = new UnaryExpressionSyntax(SpanFrom(expression.Span.Start), UnaryOperator.NullAssert, expression);
                    continue;
                }

                return expression;
            }
        }

        /// <summary>Parses an argument list, positional or named (§3.5).</summary>
        private IReadOnlyList<ArgumentSyntax> ParseArgumentList()
        {
            reader.Expect(TokenType.LeftParen, "'(' to open the arguments");

            // Lazy: `f()` is the most common call shape of all, and it needs no list at all.
            List<ArgumentSyntax>? arguments = null;
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

                ExpressionSyntax argumentValue = ParseExpression();
                (arguments ??= new List<ArgumentSyntax>(4)).Add(new ArgumentSyntax(SpanFrom(start), name, argumentValue));

                if (!reader.Match(TokenType.Comma))
                {
                    break;
                }
            }

            reader.Expect(TokenType.RightParen, "')' to close the arguments");
            return (IReadOnlyList<ArgumentSyntax>?)arguments ?? Array.Empty<ArgumentSyntax>();
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

                case TokenType.KeywordTypeOf:
                    return ParseTypeOf();

                case TokenType.KeywordModuleOf:
                    return ParseModuleOf();

                case TokenType.LeftBracket:
                    return ParseArrayLiteral();

                case TokenType.LeftBrace:
                    return ParseDictLiteral();

                case TokenType.LeftParen:
                    return IsLambdaAhead() ? ParseLambda() : ParseParenthesizedOrTuple();

                case TokenType.KeywordThrow:
                    // `throw` as an expression (§9): the thrown value is whatever the full
                    // expression grammar produces (`throw a + b` throws `a + b`), and the result
                    // is typed `never`, which lets it sit in `?:`, `??` and lambda bodies.
                    reader.Advance();
                    return new ThrowExpressionSyntax(SpanFrom(start), ParseExpression());

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
        /// Parses <c>typeof(X)</c>. Unlike <c>is</c>/<c>as</c>, <c>X</c> cannot always be parsed as
        /// a <see cref="TypeSyntax"/> - <c>typeof</c> has to reach an arbitrary value too
        /// (<c>typeof(Box())</c>, <c>typeof(5)</c>), and a call, a literal or an arithmetic
        /// expression are not type syntax at all. Only one shape can never also be an expression:
        /// a name followed by a generic argument list, since a bare Surtr call has no <c>&lt;...&gt;</c>
        /// of its own outside <c>pick&lt;int&gt;(...)</c> (which needs a <c>(</c> after the close,
        /// not the <c>)</c> that ends <c>typeof</c>). That one shape is parsed as a type here, via
        /// <see cref="LooksLikeGenericTypeOnlyAhead"/>'s lookahead; everything else, bare or dotted
        /// name included, parses as an ordinary expression and is left for the binder to decide
        /// between a type and a value (<c>BodyBinder.BindTypeOf</c>).
        /// </summary>
        private ExpressionSyntax ParseTypeOf()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Advance();

            reader.Expect(TokenType.LeftParen, "'(' after 'typeof'");

            if (LooksLikeGenericTypeOnlyAhead())
            {
                TypeSyntax typeOperand = ParseType();
                reader.Expect(TokenType.RightParen, "')' to close 'typeof'");
                return new TypeOfExpressionSyntax(SpanFrom(start), typeOperand);
            }

            ExpressionSyntax operand = ParseExpression();
            reader.Expect(TokenType.RightParen, "')' to close 'typeof'");
            return new TypeOfExpressionSyntax(SpanFrom(start), operand);
        }

        /// <summary>
        /// Parses <c>moduleof(ModulePath)</c>. Always static, unlike <c>typeof</c>: there is no
        /// instance form over an arbitrary value (§2.1), so the operand is always a dotted module
        /// path - never an expression - and there is no type-vs-value ambiguity to resolve here.
        /// The parenthesised shape mirrors <c>typeof(X)</c>'s rather than <c>import</c>'s
        /// unparenthesised path, for consistency with the language's one other reflection operator.
        /// </summary>
        private ExpressionSyntax ParseModuleOf()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Advance();

            reader.Expect(TokenType.LeftParen, "'(' after 'moduleof'");

            List<string> path = new List<string> { reader.ExpectIdentifier("a module path") };
            while (reader.Match(TokenType.Dot))
                path.Add(reader.ExpectIdentifier("a name after '.'"));

            reader.Expect(TokenType.RightParen, "')' to close 'moduleof'");
            return new ModuleOfExpressionSyntax(SpanFrom(start), path);
        }

        /// <summary>
        /// Whether <c>typeof</c>'s argument, starting at the current token, is a name followed by a
        /// type argument list that closes right before <c>)</c> - the one shape a bare Surtr
        /// expression can never take. Scans exactly like <see cref="LooksLikeTypeArgumentList"/>
        /// (same allowed tokens, same <c>&gt;</c>/<c>&gt;&gt;</c>/<c>&gt;&gt;&gt;</c> splitting),
        /// differing only in what has to follow the close - <c>)</c> here, since a generic call
        /// still needs its own trailing argument list and that is not what <c>typeof</c> is
        /// parsing. Nothing is consumed and nothing is reported; a scan that fails leaves the reader
        /// exactly where it found it, matching <see cref="LooksLikeTypeArgumentList"/>'s own
        /// contract.
        /// </summary>
        private bool LooksLikeGenericTypeOnlyAhead()
        {
            if (!reader.Check(TokenType.Identifier))
                return false;

            int offset = 1;
            while (reader.Peek(offset).Type == TokenType.Dot && reader.Peek(offset + 1).Type == TokenType.Identifier)
                offset += 2;

            if (reader.Peek(offset).Type != TokenType.Less)
                return false;

            const int Limit = 256;
            int depth = 0;

            for (; offset < Limit; offset++)
            {
                switch (reader.Peek(offset).Type)
                {
                    case TokenType.Less:
                        depth++;
                        break;

                    case TokenType.Greater:
                    case TokenType.ShiftRight:
                    case TokenType.UnsignedShiftRight:
                    {
                        depth -= reader.Peek(offset).Type switch
                        {
                            TokenType.Greater => 1,
                            TokenType.ShiftRight => 2,
                            _ => 3,
                        };

                        if (depth > 0)
                            break;

                        return depth == 0 && reader.Peek(offset + 1).Type == TokenType.RightParen;
                    }

                    case TokenType.Identifier:
                    case TokenType.Dot:
                    case TokenType.Comma:
                    case TokenType.Question:
                    case TokenType.LeftBracket:
                    case TokenType.RightBracket:
                    case TokenType.LeftBrace:
                    case TokenType.RightBrace:
                    case TokenType.Colon:
                    case TokenType.LeftParen:
                    case TokenType.RightParen:
                    case TokenType.Arrow:
                        break;

                    default:
                        return false;
                }
            }

            return false;
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

                    parts.Add(ParseInterpolationHole(token, expressionStart, j - expressionStart));
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

        /// <summary>Parses one <c>${ ... }</c> hole, in place in the file that holds it.</summary>
        /// <param name="token">The literal the hole was written in.</param>
        /// <param name="offset">Where the hole's expression starts inside the literal's raw text.</param>
        /// <param name="length">How long that expression is.</param>
        /// <remarks>
        /// <para>
        /// Scanned out of the file rather than out of a copy, so its nodes carry the file's own
        /// coordinates. Two facts of the lexer are what make the mapping exact, and both are worth
        /// knowing before touching this: an interpolated literal's payload is a verbatim slice of
        /// the source — the escapes are left undecoded precisely so this stage can still see them —
        /// so an index into it is an offset from the literal's first content character; and a string
        /// literal cannot span lines, so the hole is on the literal's own line and its column is a
        /// count of characters from there.
        /// </para>
        /// <para>
        /// The buffer is cut off at the hole's end because that is what stops the scan: a lexer runs
        /// to the end of what it is handed, and cutting it there costs nothing, since a slice from
        /// zero leaves every index meaning what it did.
        /// </para>
        /// </remarks>
        private ExpressionSyntax ParseInterpolationHole(Token token, int offset, int length)
        {
            // The payload starts one character past the opening quote.
            int contentStart = token.Span.Start.Position + 1;
            int holeStart = contentStart + offset;

            if (source is null)
            {
                // Built from a token stream, so there are no characters to go back to. The hole is
                // still parsed, out of its own text, and only its positions are unusable.
                Parser detached = new Parser(SurtrSourceBuffer.FromString(
                    token.Payload.AsString.Substring(offset, length), reader.SourceName));
                return SingleExpression(detached, token);
            }

            var window = SurtrSourceBuffer.FromMemory(source.Text.Slice(0, holeStart + length), source.Name);
            var origin = new SourceLocation(
                token.Span.Start.Line,
                token.Span.Start.Column + 1 + offset,
                holeStart);

            List<Token> tokens = new Lexer(window, origin, diagnostics).Tokenize();
            return SingleExpression(new Parser(tokens, reader.SourceName, diagnostics), token);
        }

        /// <summary>Reads the one expression a hole is allowed to hold.</summary>
        private ExpressionSyntax SingleExpression(Parser inner, Token token)
        {
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
                ExpressionSyntax entryValue = ParseExpression();
                entries.Add(new DictEntrySyntax(SpanFrom(entryStart), key, entryValue));

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
        /// <para>
        /// The two forms are identical up to the closing paren — <c>(a, b)</c> is a tuple and
        /// <c>(a, b) =&gt; a + b</c> a lambda — so no bounded lookahead settles it. Scanning is
        /// cheap because a parameter list is short and the scan never nests into another one.
        /// </para>
        /// <para>
        /// A return-annotated lambda <c>(a, b): Ret =&gt; a + b</c> adds the <c>:</c> a function
        /// declaration uses after its parameter list (§8), so the lambda reads exactly like the
        /// <c>fun</c> it is an anonymous form of. The <c>:</c> can only follow the closing paren
        /// here — it is not a postfix operator — so the scan treats it as the introduction of a
        /// return type and keeps going for the <c>=&gt;</c> that ends it.
        /// </para>
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
                            TokenType after = reader.Peek(offset + 1).Type;

                            if (after == TokenType.FatArrow)
                                return true;

                            // `(params): Ret => ...`: skip the return type, which can itself nest
                            // (a closure return, a generic construction), to the `=>` that ends it.
                            if (after == TokenType.Colon)
                                return LambdaReturnTypeEndsWithFatArrow(offset + 2);

                            return false;
                        }
                        break;

                    case TokenType.EndOfFile:
                        return false;
                }
            }
        }

        /// <summary>
        /// Whether the return type that starts at <paramref name="offset"/> — right after the
        /// lambda's <c>(params):</c> — is followed by the <c>=&gt;</c> that ends the lambda.
        /// </summary>
        /// <remarks>
        /// A return type is scanned rather than parsed because this runs in lookahead, where the
        /// reader cannot be advanced. The depth a composite type opens — parentheses for a tuple or
        /// closure, brackets for an array, braces for a dict, angle brackets for a construction — is
        /// tracked so that a nested <c>=&gt;</c> is not mistaken for the lambda's own, and the
        /// lexer's <c>&gt;&gt;</c>/<c>&gt;&gt;&gt;</c> close two or three angle brackets at once.
        /// Only the <c>=&gt;</c> at depth zero counts. A <c>:</c> at depth zero ends the scan the
        /// other way: the return type cannot contain one (a dict type keeps its own <c>:</c> inside
        /// braces), so a bare one is a ternary or a dict separator after a parenthesized expression
        /// — <c>cond ? (x) : (y) =&gt; z</c> and <c>{ (x): (y) =&gt; z }</c> are not lambdas.
        /// </remarks>
        private bool LambdaReturnTypeEndsWithFatArrow(int offset)
        {
            int depth = 0;

            for (int i = offset; ; i++)
            {
                TokenType type = reader.Peek(i).Type;

                switch (type)
                {
                    case TokenType.LeftParen:
                    case TokenType.LeftBracket:
                    case TokenType.LeftBrace:
                    case TokenType.Less:
                        depth++;
                        break;

                    case TokenType.RightParen:
                    case TokenType.RightBracket:
                    case TokenType.RightBrace:
                    case TokenType.Greater:
                        if (--depth < 0)
                            return false;
                        break;

                    case TokenType.ShiftRight:
                        if ((depth -= 2) < 0)
                            return false;
                        break;

                    case TokenType.UnsignedShiftRight:
                        if ((depth -= 3) < 0)
                            return false;
                        break;

                    case TokenType.FatArrow:
                        return depth == 0;

                    case TokenType.Colon:
                        if (depth == 0)
                            return false;
                        break;

                    case TokenType.EndOfFile:
                        return false;
                }
            }
        }

        /// <summary>
        /// Parses <c>(params) =&gt; expr</c>, <c>(params) =&gt; { ... }</c> and the return-annotated
        /// forms <c>(params): Ret =&gt; expr</c>/<c>{ ... }</c> (§8). The <c>:</c> is the same colon a
        /// function declaration uses after its parameter list, so the lambda reads exactly like the
        /// <c>fun</c> it is an anonymous form of.
        /// </summary>
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

            TypeSyntax? returnType = reader.Match(TokenType.Colon) ? ParseType() : null;
            reader.Expect(TokenType.FatArrow, "'=>' in the lambda");

            if (reader.Check(TokenType.LeftBrace))
            {
                BlockStatementSyntax lambdaBody = ParseBlock();
                return new LambdaExpressionSyntax(SpanFrom(start), parameters, returnType, null, lambdaBody);
            }

            ExpressionSyntax lambdaResult = ParseExpression();
            return new LambdaExpressionSyntax(SpanFrom(start), parameters, returnType, lambdaResult, null);
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
                ExpressionSyntax armResult = ParseExpression();
                arms.Add(new SwitchExpressionArmSyntax(SpanFrom(armStart), values, armResult));

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
