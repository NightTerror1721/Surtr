#nullable enable

using Surtr.Compiler.Syntax;

namespace Surtr.Compiler.Diagnostics
{
    /// <summary>
    /// Thrown when the lexer meets input it cannot turn into a token: an unterminated string or
    /// character literal, an unrecognized escape sequence, a malformed numeric literal, or a
    /// character that begins no token at all.
    /// </summary>
    /// <remarks>
    /// Every one of these is a defect in the source rather than a recoverable ambiguity, so the
    /// lexer throws instead of emitting <see cref="TokenType.Invalid"/> and letting a later stage
    /// discover the problem with less context about what went wrong. Carrying
    /// <see cref="Location"/> is the point of having a distinct type here: the message alone
    /// cannot say where in the file to look.
    /// </remarks>
    public sealed class SurtrLexerException : SurtrCompilerException
    {
        /// <summary>Where in the source the offending input starts.</summary>
        public SourceLocation Location { get; }

        /// <summary>Identifies the source the failure occurred in - a file path, or a placeholder for in-memory sources.</summary>
        public string SourceName { get; }

        /// <summary>Initializes the exception with what went wrong and where.</summary>
        /// <param name="message">A description of the malformed input.</param>
        /// <param name="sourceName">Identifies the source being lexed.</param>
        /// <param name="location">Where the offending input starts.</param>
        public SurtrLexerException(string message, string sourceName, SourceLocation location)
            : base($"{sourceName}({location.Line},{location.Column}): {message}")
        {
            Location = location;
            SourceName = sourceName;
        }
    }
}
