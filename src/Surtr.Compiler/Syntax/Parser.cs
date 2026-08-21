#nullable enable

using System;
using System.Collections.Generic;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax.Ast;

namespace Surtr.Compiler.Syntax
{
    /// <summary>
    /// Turns a lexed token stream into a syntax tree, following <c>docs/Language-Syntax.md</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ordinary recursive descent, with expressions handled by precedence climbing over the table
    /// in §5.7 rather than by one method per level. The table is the single place a precedence
    /// lives, so a level cannot drift between the document and the code.
    /// </para>
    /// <para>
    /// The parser is split across partial files by what each parses: this one holds the entry
    /// points and shared helpers, <c>Parser.Types.cs</c> types, <c>Parser.Expressions.cs</c>
    /// expressions, <c>Parser.Statements.cs</c> statements and <c>Parser.Declarations.cs</c>
    /// declarations. That mirrors how <c>SurtrCodeEmitter</c> is laid out in Surtr.Core.
    /// </para>
    /// <para>
    /// Three ambiguities are worth knowing about before reading the rest, because each is resolved
    /// by lookahead rather than by grammar:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>A lambda versus a parenthesized expression or a tuple.</b> <c>(a, b)</c> is a tuple and
    /// <c>(a, b) =&gt; a + b</c> is a lambda, and they diverge only after the closing paren, so
    /// <see cref="IsLambdaAhead"/> scans balanced parentheses to look for the <c>=&gt;</c>.
    /// </description></item>
    /// <item><description>
    /// <b>A block versus a dict literal.</b> §5.4 settles this by position: a <c>{</c> where a
    /// statement is expected always opens a block, and one where an expression is expected always
    /// opens a dict literal. The parser never has to guess because it always knows which it is
    /// parsing.
    /// </description></item>
    /// <item><description>
    /// <b>A member's kind.</b> §3.2 makes the introducer keyword decide — <c>let</c>/<c>var</c> a
    /// field, <c>fun</c> a method, <c>constructor</c>, <c>alias</c>, <c>operator</c>, and nothing
    /// at all a property.
    /// </description></item>
    /// </list>
    /// </remarks>
    public sealed partial class Parser
    {
        private readonly TokenReader reader;
        private readonly SurtrDiagnosticBag diagnostics;

        /// <summary>
        /// The buffer this parser was built over, or <see langword="null"/> when it was handed a
        /// token stream instead. Kept for the one production that has to go back to the characters:
        /// an interpolated literal arrives whole and its holes are lexed out of it (§5.2).
        /// </summary>
        private readonly SurtrSourceBuffer? source;

        /// <summary>Creates a parser over a source buffer, lexing it first.</summary>
        /// <param name="source">The source to parse.</param>
        /// <param name="diagnostics">
        /// Where to report problems, or <see langword="null"/> for a bag of its own. The lexer
        /// shares it, so one bag holds everything wrong with the file.
        /// </param>
        public Parser(SurtrSourceBuffer source, SurtrDiagnosticBag? diagnostics = null)
        {
            this.diagnostics = diagnostics ?? new SurtrDiagnosticBag();
            this.source = source;

            List<Token> tokens = new Lexer(source, this.diagnostics).Tokenize();
            reader = new TokenReader(tokens.ToArray(), source.Name, this.diagnostics);
        }

        /// <summary>Creates a parser over an already-lexed token stream.</summary>
        /// <param name="tokens">The tokens, ending with <see cref="TokenType.EndOfFile"/>.</param>
        /// <param name="sourceName">Identifies the source, for diagnostics.</param>
        /// <param name="diagnostics">Where to report problems, or <see langword="null"/> for a bag of its own.</param>
        public Parser(IReadOnlyList<Token> tokens, string sourceName, SurtrDiagnosticBag? diagnostics = null)
        {
            this.diagnostics = diagnostics ?? new SurtrDiagnosticBag();

            Token[] array = new Token[tokens.Count];
            for (int i = 0; i < tokens.Count; i++)
            {
                array[i] = tokens[i];
            }

            reader = new TokenReader(array, sourceName, this.diagnostics);
        }

        /// <summary>Everything found wrong with the source, by the lexer and the parser alike.</summary>
        /// <remarks>
        /// <b>Check this.</b> Parsing does not throw on a syntax error — it reports and recovers —
        /// so a tree that came back is not by itself evidence that the file was well-formed. Use
        /// <see cref="SurtrDiagnosticBag.HasErrors"/>, or
        /// <see cref="SurtrDiagnosticBag.ThrowIfErrors"/> for the simple behaviour.
        /// </remarks>
        public SurtrDiagnosticBag Diagnostics => diagnostics;

        /// <summary>
        /// Parses a whole source file, recovering from anything it cannot parse.
        /// </summary>
        /// <remarks>
        /// A declaration that does not parse is reported and skipped, and parsing resumes at the
        /// next one — so the tree that comes back holds every declaration that <em>did</em> parse,
        /// and <see cref="Diagnostics"/> holds the rest of the story. One bad line hiding the next
        /// twenty is the behaviour worth avoiding here: it is what makes a compiler feel like it is
        /// arguing rather than helping.
        /// </remarks>
        public CompilationUnitSyntax ParseCompilationUnit()
        {
            SourceLocation start = reader.CurrentLocation;

            List<ImportSyntax> imports = new List<ImportSyntax>();
            while (reader.Check(TokenType.KeywordImport) || reader.Check(TokenType.KeywordExport))
            {
                try
                {
                    imports.Add(ParseImport());
                }
                catch (SurtrParserException)
                {
                    SynchronizeToDeclaration();
                }
            }

            List<DeclarationSyntax> declarations = new List<DeclarationSyntax>();
            while (!reader.Check(TokenType.EndOfFile))
            {
                int before = reader.Position;

                try
                {
                    declarations.Add(ParseDeclaration());
                }
                catch (SurtrParserException)
                {
                    SynchronizeToDeclaration();
                }

                // Nothing consumed and nothing thrown would be an infinite loop. It should not be
                // reachable, but a parser that hangs is a far worse failure than one that stops.
                if (reader.Position == before)
                {
                    reader.Advance();
                }
            }

            return new CompilationUnitSyntax(SpanFrom(start), imports, declarations);
        }

        /// <summary>
        /// Skips forward to somewhere a new declaration could plausibly begin, after one failed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Resynchronising is guesswork by nature, so it is kept to the two anchors that are almost
        /// never wrong: a <c>;</c>, which ends a declaration, and a token that can only start one —
        /// a modifier, or an introducer keyword. It stops at a <c>}</c> without consuming it, since
        /// that belongs to whoever opened the block.
        /// </para>
        /// <para>
        /// Brace depth is tracked so that a failure inside a method body skips the rest of the body
        /// rather than mistaking the first <c>fun</c> nested in it for the next declaration.
        /// </para>
        /// </remarks>
        private void SynchronizeToDeclaration()
        {
            int depth = 0;

            while (!reader.Check(TokenType.EndOfFile))
            {
                if (depth == 0 && reader.Check(TokenType.RightBrace))
                {
                    return;
                }

                if (depth == 0 && StartsDeclaration(reader.CurrentType))
                {
                    return;
                }

                TokenType type = reader.CurrentType;
                reader.Advance();

                switch (type)
                {
                    case TokenType.LeftBrace:
                        depth++;
                        break;

                    case TokenType.RightBrace:
                        depth--;
                        break;

                    case TokenType.Semicolon:
                        if (depth == 0)
                        {
                            return;
                        }

                        break;
                }
            }
        }

        /// <summary>Skips forward to the end of the statement that failed, or to the end of its block.</summary>
        /// <remarks>
        /// The statement-level counterpart of <see cref="SynchronizeToDeclaration"/>: a <c>;</c>
        /// ends a statement, a <c>}</c> ends the block and is left for the caller, and a keyword
        /// that can only begin a statement is a place to pick up again.
        /// </remarks>
        private void SynchronizeToStatement()
        {
            int depth = 0;

            while (!reader.Check(TokenType.EndOfFile))
            {
                if (depth == 0 && reader.Check(TokenType.RightBrace))
                {
                    return;
                }

                if (depth == 0 && StartsStatement(reader.CurrentType))
                {
                    return;
                }

                TokenType type = reader.CurrentType;
                reader.Advance();

                switch (type)
                {
                    case TokenType.LeftBrace:
                        depth++;
                        break;

                    case TokenType.RightBrace:
                        depth--;
                        break;

                    case TokenType.Semicolon:
                        if (depth == 0)
                        {
                            return;
                        }

                        break;
                }
            }
        }

        /// <summary>Whether a token can only appear at the start of a declaration.</summary>
        private static bool StartsDeclaration(TokenType type)
        {
            switch (type)
            {
                case TokenType.KeywordPublic:
                case TokenType.KeywordPrivate:
                case TokenType.KeywordProtected:
                case TokenType.KeywordInternal:
                case TokenType.KeywordStatic:
                case TokenType.KeywordSealed:
                case TokenType.KeywordAbstract:
                case TokenType.KeywordVirtual:
                case TokenType.KeywordOverride:
                case TokenType.KeywordInline:
                case TokenType.KeywordForceInline:
                case TokenType.KeywordNative:
                case TokenType.KeywordClass:
                case TokenType.KeywordInterface:
                case TokenType.KeywordEnum:
                case TokenType.KeywordSingleton:
                case TokenType.KeywordAlias:
                case TokenType.KeywordConstructor:
                case TokenType.KeywordOperator:
                case TokenType.KeywordFun:
                case TokenType.KeywordImport:
                case TokenType.At:
                case TokenType.DocComment:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Whether a token can only appear at the start of a statement.</summary>
        private static bool StartsStatement(TokenType type)
        {
            switch (type)
            {
                case TokenType.KeywordIf:
                case TokenType.KeywordWhile:
                case TokenType.KeywordFor:
                case TokenType.KeywordSwitch:
                case TokenType.KeywordTry:
                case TokenType.KeywordThrow:
                case TokenType.KeywordReturn:
                case TokenType.KeywordBreak:
                case TokenType.KeywordContinue:
                case TokenType.KeywordLet:
                case TokenType.KeywordVar:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Parses <c>import Path.To.Name;</c> or <c>import Path.To.*;</c>.</summary>
        private ImportSyntax ParseImport()
        {
            SourceLocation start = reader.CurrentLocation;

            bool isExport = false;
            if (reader.Match(TokenType.KeywordExport))
            {
                isExport = true;
                reader.Expect(TokenType.KeywordImport, "'import' after 'export'");
            }
            else
            {
                reader.Expect(TokenType.KeywordImport, "'import'");
            }

            // `module` is a contextual keyword (§2.1): it only means "whole module" directly after
            // `import`, so it is not reserved and stays a legal identifier everywhere else — a
            // `moduleof(no.such.module)` path is unaffected.
            bool isModule = false;
            if (reader.Current.Type == TokenType.Identifier
                && reader.Current.Lexeme.Span.SequenceEqual("module".AsSpan()))
            {
                isModule = true;
                reader.Advance();
            }

            List<string> path = new List<string> { reader.ExpectIdentifier("a module or type name") };
            bool wildcard = false;
            List<string>? members = null;

            while (reader.Match(TokenType.Dot))
            {
                if (reader.Match(TokenType.Star))
                {
                    wildcard = true;
                    break;
                }

                if (reader.Check(TokenType.LeftBrace))
                {
                    members = ParseImportMemberList();
                    break;
                }

                path.Add(reader.ExpectIdentifier("a name after '.'"));
            }

            string? alias = null;
            if (!wildcard && members is null && reader.Match(TokenType.KeywordAs))
                alias = reader.ExpectIdentifier("a name after 'as'");

            reader.Expect(TokenType.Semicolon, "';' after the import");
            return new ImportSyntax(SpanFrom(start), path, wildcard, alias, members, isModule, isExport);
        }

        /// <summary>The comma-separated names inside <c>import Module.{A, B};</c>'s trailing braces.</summary>
        private List<string> ParseImportMemberList()
        {
            reader.Expect(TokenType.LeftBrace, "'{'");
            List<string> members = new List<string> { reader.ExpectIdentifier("a name inside '{ }'") };

            while (reader.Match(TokenType.Comma))
                members.Add(reader.ExpectIdentifier("a name after ','"));

            reader.Expect(TokenType.RightBrace, "'}' to close the import list");
            return members;
        }

        /// <summary>Collects the <c>///</c> doc comment lines sitting immediately before a declaration.</summary>
        private IReadOnlyList<string> ParseDocComment()
        {
            List<string>? lines = null;

            while (reader.Check(TokenType.DocComment))
            {
                lines ??= new List<string>();
                lines.Add(reader.Advance().Payload.AsString);
            }

            return (IReadOnlyList<string>?)lines ?? EmptyDocComment;
        }

        /// <summary>Collects the <c>@Name(args)</c> attributes sitting before a declaration (§11).</summary>
        private IReadOnlyList<AttributeSyntax> ParseAttributes()
        {
            List<AttributeSyntax>? attributes = null;

            while (reader.Check(TokenType.At))
            {
                SourceLocation start = reader.CurrentLocation;
                reader.Advance();

                string name = reader.ExpectIdentifier("an attribute name");
                List<ExpressionSyntax> arguments = new List<ExpressionSyntax>();

                if (reader.Match(TokenType.LeftParen))
                {
                    while (!reader.Check(TokenType.RightParen))
                    {
                        arguments.Add(ParseExpression());
                        if (!reader.Match(TokenType.Comma))
                        {
                            break;
                        }
                    }

                    reader.Expect(TokenType.RightParen, "')' to close the attribute arguments");
                }

                attributes ??= new List<AttributeSyntax>();
                attributes.Add(new AttributeSyntax(SpanFrom(start), name, arguments));
            }

            return (IReadOnlyList<AttributeSyntax>?)attributes ?? EmptyAttributes;
        }

        /// <summary>
        /// The span of a production: from where it started to the end of the last token it read.
        /// </summary>
        /// <param name="start">Where the production began, captured before its first token.</param>
        /// <remarks>
        /// Every node is built at the end of its production, so by the time this is called the
        /// reader is sitting just past the construct — which makes one helper enough for the whole
        /// parser and means no production has to track its own extent.
        /// </remarks>
        private SourceSpan SpanFrom(SourceLocation start) => SourceSpan.FromBounds(start, reader.ConsumedEnd);

        /// <summary>True when the current identifier's text is <paramref name="text"/> — for the contextual keywords (§3.2).</summary>
        private bool CheckContextual(string text)
        {
            return reader.Check(TokenType.Identifier) && reader.Current.Lexeme.Span.SequenceEqual(text.AsSpan());
        }

        /// <summary>True when the identifier <paramref name="offset"/> ahead has the given text.</summary>
        private bool CheckContextualAt(int offset, string text)
        {
            Token token = reader.Peek(offset);
            return token.Type == TokenType.Identifier && token.Lexeme.Span.SequenceEqual(text.AsSpan());
        }

        private static readonly IReadOnlyList<string> EmptyDocComment = [];
        private static readonly IReadOnlyList<AttributeSyntax> EmptyAttributes = [];
        private static readonly IReadOnlyList<TypeSyntax> EmptyTypes = [];
        private static readonly IReadOnlyList<TypeParameterSyntax> EmptyTypeParameters = [];
    }
}
