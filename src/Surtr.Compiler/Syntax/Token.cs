#nullable enable

using System;

namespace Surtr.Compiler.Syntax
{
    /// <summary>
    /// One lexical unit produced by the lexer. Deliberately data-oriented rather than a class
    /// hierarchy (no <c>NumberToken</c>, <c>StringToken</c>, <c>IdentifierToken</c>, ...): nothing
    /// about a token's behavior differs by kind, only what's in these four fields, so a hierarchy
    /// would just multiply types with no state or logic of their own - the same reasoning
    /// <c>SurtrMethodDispatch</c>/<c>SurtrMethodRole</c> follow in Surtr.Core by staying plain
    /// fields instead of a second subclassing axis. A flat struct is also exactly what the
    /// parser's future token cursor wants: cheap to copy, trivial to compare, and it lives in a
    /// plain array with no per-token allocation.
    /// </summary>
    public readonly struct Token
    {
        /// <summary>What kind of token this is.</summary>
        public TokenType Type { get; }

        /// <summary>The exact source text this token was scanned from - quotes and escape sequences included, where they apply.</summary>
        public ReadOnlyMemory<char> Lexeme { get; }

        /// <summary>Where this token starts in the source.</summary>
        public SourceLocation Location { get; }

        /// <summary>
        /// The token's decoded literal value, for the kinds where <see cref="Lexeme"/> alone isn't
        /// the value. <see cref="TokenPayload.Kind"/> is <see cref="TokenPayloadKind.None"/> for
        /// every token that doesn't carry one - keywords, punctuation, identifiers.
        /// </summary>
        public TokenPayload Payload { get; }

        /// <summary>The number of characters this token spans in the source.</summary>
        public int Length => Lexeme.Length;

        /// <summary>Creates a token.</summary>
        /// <param name="type">What kind of token this is.</param>
        /// <param name="lexeme">The exact source text the token was scanned from.</param>
        /// <param name="location">Where the token starts in the source.</param>
        /// <param name="payload">The token's decoded literal value, if it has one.</param>
        public Token(TokenType type, ReadOnlyMemory<char> lexeme, SourceLocation location, TokenPayload payload = default)
        {
            Type = type;
            Lexeme = lexeme;
            Location = location;
            Payload = payload;
        }

        /// <summary>The lexeme as a string, for display and diagnostics. Allocates - never use it on a hot path.</summary>
        public override string ToString() => Lexeme.ToString();
    }
}
