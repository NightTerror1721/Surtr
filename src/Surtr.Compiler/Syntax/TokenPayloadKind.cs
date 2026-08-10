#nullable enable

namespace Surtr.Compiler.Syntax
{
    /// <summary>Which field of a <see cref="TokenPayload"/> is actually populated, if any.</summary>
    public enum TokenPayloadKind : byte
    {
        /// <summary>No literal value - the token's <see cref="Token.Lexeme"/> is everything there is to know about it (keywords, punctuation, identifiers).</summary>
        None = 0,

        /// <summary>An integer literal's parsed value.</summary>
        Integer,

        /// <summary>A floating-point literal's parsed value.</summary>
        Float,

        /// <summary>A character literal's decoded value (its escape sequence, if any, already resolved).</summary>
        Character,

        /// <summary>A string literal's decoded value (escape sequences resolved, quotes stripped).</summary>
        String,
    }
}
