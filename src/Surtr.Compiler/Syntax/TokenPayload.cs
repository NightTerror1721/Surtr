#nullable enable

using System;

namespace Surtr.Compiler.Syntax
{
    /// <summary>
    /// A literal token's decoded value. Exists because a token's raw <see cref="Token.Lexeme"/> is
    /// not always the value it denotes - a string literal's lexeme still has its quotes and escape
    /// sequences, a character literal's still has its escape sequence, if any.
    /// </summary>
    /// <remarks>
    /// An integer, a float and a character value never coexist - <see cref="Kind"/> says which one
    /// is present - so <see cref="AsInteger"/>, <see cref="AsFloat"/> and <see cref="AsCharacter"/>
    /// all read a single 8-byte field instead of three separate ones, the same trick
    /// <c>SurtrRawValue</c> uses in Surtr.Core for the same reason. A float's bits are
    /// reinterpreted through <see cref="BitConverter"/> rather than truncated through a numeric
    /// conversion. <see cref="AsString"/> is backed by a separate field: it is a reference, and the
    /// CLR does not allow a reference field to overlap value fields in an explicit-layout struct.
    /// </remarks>
    public readonly struct TokenPayload
    {
        /// <summary>The payload of a token that doesn't carry a literal value.</summary>
        public static readonly TokenPayload None = default;

        /// <summary>Which of the accessors below is valid to read.</summary>
        public TokenPayloadKind Kind { get; }

        private readonly long raw;
        private readonly string? text;

        private TokenPayload(TokenPayloadKind kind, long raw, string? text)
        {
            Kind = kind;
            this.raw = raw;
            this.text = text;
        }

        /// <summary>Wraps a parsed integer literal.</summary>
        public static TokenPayload ForInteger(long value) => new TokenPayload(TokenPayloadKind.Integer, value, null);

        /// <summary>Wraps a parsed floating-point literal.</summary>
        public static TokenPayload ForFloat(double value) => new TokenPayload(TokenPayloadKind.Float, BitConverter.DoubleToInt64Bits(value), null);

        /// <summary>Wraps a decoded character literal.</summary>
        public static TokenPayload ForCharacter(char value) => new TokenPayload(TokenPayloadKind.Character, value, null);

        /// <summary>Wraps a decoded string literal.</summary>
        public static TokenPayload ForString(string value) => new TokenPayload(TokenPayloadKind.String, 0, value);

        /// <summary>The integer value. Throws if <see cref="Kind"/> isn't <see cref="TokenPayloadKind.Integer"/>.</summary>
        public long AsInteger => Kind == TokenPayloadKind.Integer ? raw : throw MismatchedKind(TokenPayloadKind.Integer);

        /// <summary>The floating-point value. Throws if <see cref="Kind"/> isn't <see cref="TokenPayloadKind.Float"/>.</summary>
        public double AsFloat => Kind == TokenPayloadKind.Float ? BitConverter.Int64BitsToDouble(raw) : throw MismatchedKind(TokenPayloadKind.Float);

        /// <summary>The character value. Throws if <see cref="Kind"/> isn't <see cref="TokenPayloadKind.Character"/>.</summary>
        public char AsCharacter => Kind == TokenPayloadKind.Character ? (char)raw : throw MismatchedKind(TokenPayloadKind.Character);

        /// <summary>The string value. Throws if <see cref="Kind"/> isn't <see cref="TokenPayloadKind.String"/>.</summary>
        public string AsString => Kind == TokenPayloadKind.String && text is not null ? text : throw MismatchedKind(TokenPayloadKind.String);

        private InvalidOperationException MismatchedKind(TokenPayloadKind expected)
        {
            return new InvalidOperationException($"Expected a {expected} payload, but this one is {Kind}.");
        }
    }
}
