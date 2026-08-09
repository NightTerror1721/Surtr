#nullable enable

using System;
using System.Collections.Generic;
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

        /// <summary>Creates a parser over a source buffer, lexing it first.</summary>
        /// <param name="source">The source to parse.</param>
        public Parser(SurtrSourceBuffer source)
        {
            List<Token> tokens = new Lexer(source).Tokenize();
            reader = new TokenReader(tokens.ToArray(), source.Name);
        }

        /// <summary>Creates a parser over an already-lexed token stream.</summary>
        /// <param name="tokens">The tokens, ending with <see cref="TokenType.EndOfFile"/>.</param>
        /// <param name="sourceName">Identifies the source, for diagnostics.</param>
        public Parser(IReadOnlyList<Token> tokens, string sourceName)
        {
            Token[] array = new Token[tokens.Count];
            for (int i = 0; i < tokens.Count; i++)
            {
                array[i] = tokens[i];
            }

            reader = new TokenReader(array, sourceName);
        }

        /// <summary>Parses a whole source file.</summary>
        public CompilationUnitSyntax ParseCompilationUnit()
        {
            SourceLocation start = reader.CurrentLocation;

            List<ImportSyntax> imports = new List<ImportSyntax>();
            while (reader.Check(TokenType.KeywordImport))
            {
                imports.Add(ParseImport());
            }

            List<DeclarationSyntax> declarations = new List<DeclarationSyntax>();
            while (!reader.Check(TokenType.EndOfFile))
            {
                declarations.Add(ParseDeclaration());
            }

            return new CompilationUnitSyntax(start, imports, declarations);
        }

        /// <summary>Parses <c>import Path.To.Name;</c> or <c>import Path.To.*;</c>.</summary>
        private ImportSyntax ParseImport()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.KeywordImport, "'import'");

            List<string> path = new List<string> { reader.ExpectIdentifier("a module or type name") };
            bool wildcard = false;

            while (reader.Match(TokenType.Dot))
            {
                if (reader.Match(TokenType.Star))
                {
                    wildcard = true;
                    break;
                }

                path.Add(reader.ExpectIdentifier("a name after '.'"));
            }

            reader.Expect(TokenType.Semicolon, "';' after the import");
            return new ImportSyntax(start, path, wildcard);
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
                attributes.Add(new AttributeSyntax(start, name, arguments));
            }

            return (IReadOnlyList<AttributeSyntax>?)attributes ?? EmptyAttributes;
        }

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
