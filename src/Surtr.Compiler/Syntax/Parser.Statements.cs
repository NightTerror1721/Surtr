#nullable enable

using Surtr.Compiler.Diagnostics;
using System;
using System.Collections.Generic;
using Surtr.Compiler.Syntax.Ast;

namespace Surtr.Compiler.Syntax
{
    public sealed partial class Parser
    {
        /// <summary>Parses a braced block, which introduces a scope (§4.4).</summary>
        private BlockStatementSyntax ParseBlock()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.LeftBrace, "'{' to open the block");

            // A statement that does not parse is reported and skipped, and the block carries on
            // with the next one. Recovering here rather than only at declaration level is what
            // keeps one bad line inside a long method from discarding the whole method.
            List<StatementSyntax>? statements = null;
            while (!reader.Check(TokenType.RightBrace) && !reader.Check(TokenType.EndOfFile))
            {
                int before = reader.Position;

                try
                {
                    (statements ??= new List<StatementSyntax>(8)).Add(ParseStatement());
                }
                catch (SurtrParserException)
                {
                    SynchronizeToStatement();
                }

                if (reader.Position == before)
                {
                    reader.Advance();
                }
            }

            reader.Expect(TokenType.RightBrace, "'}' to close the block");
            return new BlockStatementSyntax(SpanFrom(start), (IReadOnlyList<StatementSyntax>?)statements ?? Array.Empty<StatementSyntax>());
        }

        /// <summary>Parses one statement.</summary>
        private StatementSyntax ParseStatement()
        {
            SourceLocation start = reader.CurrentLocation;

            switch (reader.CurrentType)
            {
                // §5.4: a `{` where a statement is expected always opens a block, never a dict.
                case TokenType.LeftBrace:
                    return ParseBlock();

                case TokenType.Semicolon:
                    reader.Advance();
                    return new EmptyStatementSyntax(SpanFrom(start));

                case TokenType.KeywordLet:
                case TokenType.KeywordVar:
                    return ParseLocalDeclaration();

                case TokenType.KeywordConst:
                    // `const` introduces either a compile-time binding or a compile-time branch.
                    return reader.CheckAt(1, TokenType.KeywordIf) ? ParseIf() : ParseLocalDeclaration();

                case TokenType.KeywordIf:
                    return ParseIf();

                case TokenType.KeywordWhile:
                    return ParseWhile();

                case TokenType.KeywordFor:
                    return ParseFor();

                case TokenType.KeywordSwitch:
                    return ParseSwitchStatement();

                case TokenType.KeywordTry:
                    return ParseTry();

                case TokenType.KeywordThrow:
                    reader.Advance();
                    ExpressionSyntax thrown = ParseExpression();
                    reader.Expect(TokenType.Semicolon, "';' after the thrown value");
                    return new ThrowStatementSyntax(SpanFrom(start), thrown);

                case TokenType.KeywordReturn:
                    reader.Advance();
                    ExpressionSyntax? returned = reader.Check(TokenType.Semicolon) ? null : ParseExpression();
                    reader.Expect(TokenType.Semicolon, "';' after the return");
                    return new ReturnStatementSyntax(SpanFrom(start), returned);

                case TokenType.KeywordBreak:
                case TokenType.KeywordContinue:
                    return ParseBreakOrContinue();

                default:
                    // `outer: for (...)` — a label is an identifier followed by ':'. Nothing else in
                    // statement position has that shape, since a named argument only ever appears
                    // inside an argument list.
                    if (reader.Check(TokenType.Identifier) && reader.CheckAt(1, TokenType.Colon))
                    {
                        string label = reader.Advance().ToString();
                        reader.Advance();
                        StatementSyntax labeled = ParseStatement();
                        return new LabeledStatementSyntax(SpanFrom(start), label, labeled);
                    }

                    ExpressionSyntax expression = ParseExpression();
                    reader.Expect(TokenType.Semicolon, "';' after the expression");
                    return new ExpressionStatementSyntax(SpanFrom(start), expression);
            }
        }

        /// <summary>Parses <c>let</c>, <c>var</c> or <c>const</c> declaring a local.</summary>
        private StatementSyntax ParseLocalDeclaration()
        {
            SourceLocation start = reader.CurrentLocation;

            bool isConst = reader.Match(TokenType.KeywordConst);
            bool isMutable = false;

            if (!isConst)
            {
                isMutable = reader.CurrentType == TokenType.KeywordVar;
                reader.Advance();

                // `let (a, b) = value;` - a destructuring declaration (§4.5). A `const` one is
                // deliberately not recognised: a tuple cannot fold, so the ordinary path's
                // "expects a variable name" is the honest refusal.
                if (reader.CurrentType == TokenType.LeftParen)
                    return ParseTupleDeclaration(start, isMutable);
            }

            string name = reader.ExpectIdentifier("a variable name");
            TypeSyntax? type = reader.Match(TokenType.Colon) ? ParseType() : null;
            ExpressionSyntax? initializer = reader.Match(TokenType.Assign) ? ParseExpression() : null;

            reader.Expect(TokenType.Semicolon, "';' after the declaration");
            return new LocalDeclarationStatementSyntax(SpanFrom(start), name, type, initializer, isMutable, isConst);
        }

        /// <summary>Parses the name list of <c>let (a, b) = value;</c> (§4.5).</summary>
        private StatementSyntax ParseTupleDeclaration(SourceLocation start, bool isMutable)
        {
            reader.Expect(TokenType.LeftParen, "'('");

            var names = new List<string>();
            while (true)
            {
                names.Add(reader.ExpectIdentifier("a name to bind"));
                if (!reader.Match(TokenType.Comma))
                    break;
            }

            reader.Expect(TokenType.RightParen, "')' after the names");
            reader.Expect(TokenType.Assign, "'=' - a destructuring declaration has nothing to declare without one");
            ExpressionSyntax initializer = ParseExpression();
            reader.Expect(TokenType.Semicolon, "';' after the declaration");

            return new TupleDeclarationStatementSyntax(SpanFrom(start), names, initializer, isMutable);
        }

        /// <summary>Parses <c>if</c> and <c>const if</c>, with the dangling <c>else</c> bound to the nearest one (§4.1).</summary>
        private StatementSyntax ParseIf()
        {
            SourceLocation start = reader.CurrentLocation;
            bool isConst = reader.Match(TokenType.KeywordConst);

            reader.Expect(TokenType.KeywordIf, "'if'");
            reader.Expect(TokenType.LeftParen, "'(' after 'if'");
            ExpressionSyntax condition = ParseExpression();
            reader.Expect(TokenType.RightParen, "')' after the condition");

            StatementSyntax then = ParseEmbeddedStatement();

            // Binding `else` here, at the innermost `if` that has not taken one, is exactly the
            // nearest-unmatched rule §4.1 specifies.
            StatementSyntax? elseBranch = null;
            if (reader.Match(TokenType.KeywordElse))
            {
                elseBranch = ParseEmbeddedStatement();
            }

            return new IfStatementSyntax(SpanFrom(start), condition, then, elseBranch, isConst);
        }

        /// <summary>
        /// Parses the body of an <c>if</c>, loop or <c>else</c>. Braces are optional (§4.1), but a
        /// declaration may not appear unbraced: its binding would leave scope immediately and could
        /// never be read, so it is always a mistake.
        /// </summary>
        private StatementSyntax ParseEmbeddedStatement()
        {
            if (reader.Check(TokenType.KeywordLet) || reader.Check(TokenType.KeywordVar)
                || (reader.Check(TokenType.KeywordConst) && !reader.CheckAt(1, TokenType.KeywordIf)))
            {
                throw reader.Error(SurtrDiagnosticCode.DeclarationAsEmbeddedStatement, "A declaration cannot be the unbraced body of an 'if' or a loop — its binding would immediately leave scope.");
            }

            return ParseStatement();
        }

        /// <summary>Parses a <c>while</c> loop.</summary>
        private StatementSyntax ParseWhile()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.KeywordWhile, "'while'");
            reader.Expect(TokenType.LeftParen, "'(' after 'while'");
            ExpressionSyntax condition = ParseExpression();
            reader.Expect(TokenType.RightParen, "')' after the condition");

            StatementSyntax whileBody = ParseEmbeddedStatement();
            return new WhileStatementSyntax(SpanFrom(start), condition, whileBody);
        }

        /// <summary>
        /// Parses both <c>for</c> forms. They share a prefix, and the <c>in</c> after the first
        /// name is what tells them apart (§4.2).
        /// </summary>
        private StatementSyntax ParseFor()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.KeywordFor, "'for'");
            reader.Expect(TokenType.LeftParen, "'(' after 'for'");

            if (IsForInAhead(out bool declaresBinding))
            {
                if (declaresBinding)
                {
                    reader.Advance();
                }

                string variableName = reader.ExpectIdentifier("the loop variable's name");
                TypeSyntax? variableType = reader.Match(TokenType.Colon) ? ParseType() : null;

                reader.Expect(TokenType.KeywordIn, "'in' in the for-in loop");
                ExpressionSyntax sequence = ParseExpression();
                reader.Expect(TokenType.RightParen, "')' after the sequence");

                StatementSyntax forInBody = ParseEmbeddedStatement();
                return new ForInStatementSyntax(SpanFrom(start), variableName, variableType, sequence, forInBody);
            }

            StatementSyntax? initializer = null;
            if (!reader.Match(TokenType.Semicolon))
            {
                initializer = reader.Check(TokenType.KeywordLet) || reader.Check(TokenType.KeywordVar)
                    ? ParseLocalDeclaration()
                    : ParseExpressionStatement();
            }

            ExpressionSyntax? condition = reader.Check(TokenType.Semicolon) ? null : ParseExpression();
            reader.Expect(TokenType.Semicolon, "';' after the loop condition");

            ExpressionSyntax? step = reader.Check(TokenType.RightParen) ? null : ParseExpression();
            reader.Expect(TokenType.RightParen, "')' after the for clauses");

            StatementSyntax forBody = ParseEmbeddedStatement();
            return new ForStatementSyntax(SpanFrom(start), initializer, condition, step, forBody);
        }

        /// <summary>
        /// Looks past an optional <c>let</c>/<c>var</c> and a name for the <c>in</c> that marks a
        /// <c>for-in</c>. A type annotation may sit between them, so the scan skips one.
        /// </summary>
        private bool IsForInAhead(out bool declaresBinding)
        {
            int offset = 0;
            declaresBinding = false;

            if (reader.CheckAt(0, TokenType.KeywordLet) || reader.CheckAt(0, TokenType.KeywordVar))
            {
                declaresBinding = true;
                offset = 1;
            }

            if (!reader.CheckAt(offset, TokenType.Identifier))
            {
                return false;
            }

            offset++;

            // An annotated loop variable — `for (i: int in xs)` — puts a type between the two.
            if (reader.CheckAt(offset, TokenType.Colon))
            {
                offset++;
                while (!reader.CheckAt(offset, TokenType.KeywordIn)
                    && !reader.CheckAt(offset, TokenType.Semicolon)
                    && !reader.CheckAt(offset, TokenType.RightParen)
                    && !reader.CheckAt(offset, TokenType.EndOfFile))
                {
                    offset++;
                }
            }

            return reader.CheckAt(offset, TokenType.KeywordIn);
        }

        /// <summary>Parses an expression followed by its <c>;</c>, for a <c>for</c> initializer.</summary>
        private StatementSyntax ParseExpressionStatement()
        {
            SourceLocation start = reader.CurrentLocation;
            ExpressionSyntax expression = ParseExpression();
            reader.Expect(TokenType.Semicolon, "';' after the expression");
            return new ExpressionStatementSyntax(SpanFrom(start), expression);
        }

        /// <summary>Parses the statement form of <c>switch</c>, which allows fallthrough (§4.3).</summary>
        private StatementSyntax ParseSwitchStatement()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.KeywordSwitch, "'switch'");
            reader.Expect(TokenType.LeftParen, "'(' after 'switch'");
            ExpressionSyntax subject = ParseExpression();
            reader.Expect(TokenType.RightParen, "')' after the switch subject");
            reader.Expect(TokenType.LeftBrace, "'{' to open the switch body");

            List<SwitchSectionSyntax> sections = new List<SwitchSectionSyntax>();
            while (!reader.Check(TokenType.RightBrace) && !reader.Check(TokenType.EndOfFile))
            {
                SourceLocation sectionStart = reader.CurrentLocation;
                List<ExpressionSyntax> labels = new List<ExpressionSyntax>();

                // Several `case` labels may share one body, which is how §4.3 groups them.
                while (reader.Check(TokenType.KeywordCase) || reader.Check(TokenType.KeywordDefault))
                {
                    if (reader.Match(TokenType.KeywordDefault))
                    {
                        reader.Expect(TokenType.Colon, "':' after 'default'");
                        continue;
                    }

                    reader.Advance();
                    labels.Add(ParseExpression());
                    reader.Expect(TokenType.Colon, "':' after the case label");
                }

                List<StatementSyntax> statements = new List<StatementSyntax>();
                while (!reader.Check(TokenType.KeywordCase) && !reader.Check(TokenType.KeywordDefault)
                    && !reader.Check(TokenType.RightBrace) && !reader.Check(TokenType.EndOfFile))
                {
                    statements.Add(ParseStatement());
                }

                sections.Add(new SwitchSectionSyntax(SpanFrom(sectionStart), labels, statements));
            }

            reader.Expect(TokenType.RightBrace, "'}' to close the switch body");
            return new SwitchStatementSyntax(SpanFrom(start), subject, sections);
        }

        /// <summary>Parses <c>try</c> with its <c>catch</c> clauses and optional <c>finally</c> (§9).</summary>
        private StatementSyntax ParseTry()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.KeywordTry, "'try'");

            BlockStatementSyntax body = ParseBlock();
            List<CatchClauseSyntax> catches = new List<CatchClauseSyntax>();

            while (reader.Check(TokenType.KeywordCatch))
            {
                SourceLocation catchStart = reader.CurrentLocation;
                reader.Advance();
                reader.Expect(TokenType.LeftParen, "'(' after 'catch'");

                string variableName = reader.ExpectIdentifier("the caught exception's name");
                reader.Expect(TokenType.Colon, "':' before the exception type");
                TypeSyntax exceptionType = ParseType();

                reader.Expect(TokenType.RightParen, "')' after the catch clause");
                BlockStatementSyntax catchBody = ParseBlock();
                catches.Add(new CatchClauseSyntax(SpanFrom(catchStart), variableName, exceptionType, catchBody));
            }

            BlockStatementSyntax? finallyBlock = reader.Match(TokenType.KeywordFinally) ? ParseBlock() : null;

            if (catches.Count == 0 && finallyBlock is null)
            {
                throw reader.Error(SurtrDiagnosticCode.IncompleteTryStatement, "A 'try' needs at least one 'catch' or a 'finally'.", start);
            }

            return new TryStatementSyntax(SpanFrom(start), body, catches, finallyBlock);
        }

        /// <summary>Parses <c>break</c>/<c>continue</c>, with an optional loop label (§4.2).</summary>
        private StatementSyntax ParseBreakOrContinue()
        {
            SourceLocation start = reader.CurrentLocation;
            bool isContinue = reader.CurrentType == TokenType.KeywordContinue;
            reader.Advance();

            string? label = reader.Check(TokenType.Identifier) ? reader.Advance().ToString() : null;
            reader.Expect(TokenType.Semicolon, isContinue ? "';' after 'continue'" : "';' after 'break'");

            return new BreakStatementSyntax(SpanFrom(start), isContinue, label);
        }
    }
}
