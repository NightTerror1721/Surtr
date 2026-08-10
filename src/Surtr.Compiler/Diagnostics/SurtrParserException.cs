#nullable enable

using Surtr.Compiler.Syntax;

namespace Surtr.Compiler.Diagnostics
{
    /// <summary>
    /// Thrown when the token stream does not match the grammar: a missing <c>;</c>, an unexpected
    /// keyword where an expression was due, a modifier on a declaration that cannot take it.
    /// </summary>
    /// <remarks>
    /// The parser throws on the first problem rather than recovering and continuing. Error
    /// recovery — resynchronising at a statement boundary so one bad line does not hide the next
    /// twenty — is worth having in a compiler people use interactively, but it is a design in its
    /// own right and would be guesswork before there is a binder to report alongside it.
    /// </remarks>
    public sealed class SurtrParserException : SurtrCompilerException
    {
        /// <summary>Where in the source the offending token starts.</summary>
        public SourceLocation Location { get; }

        /// <summary>Identifies the source the failure occurred in — a file path, or a placeholder for in-memory sources.</summary>
        public string SourceName { get; }

        /// <summary>Initializes the exception with what went wrong and where.</summary>
        /// <param name="message">A description of the mismatch.</param>
        /// <param name="sourceName">Identifies the source being parsed.</param>
        /// <param name="location">Where the offending token starts.</param>
        public SurtrParserException(string message, string sourceName, SourceLocation location)
            : base($"{sourceName}({location.Line},{location.Column}): {message}")
        {
            Location = location;
            SourceName = sourceName;
        }
    }
}
