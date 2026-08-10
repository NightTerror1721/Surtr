#nullable enable

using Surtr.Compiler.Syntax;

namespace Surtr.Compiler.Diagnostics
{
    /// <summary>
    /// Raised when the lexer meets input it cannot turn into a token: an unterminated string or
    /// character literal, an unrecognized escape sequence, a malformed numeric literal, or a
    /// character that begins no token at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lexer normally <em>reports</em> these into its <see cref="SurtrDiagnosticBag"/> and
    /// recovers, so one bad character does not hide every problem after it. This type exists for
    /// the caller that asked for the simple behaviour instead, through
    /// <see cref="SurtrDiagnosticBag.ThrowIfErrors"/>.
    /// </para>
    /// <para>
    /// It carries the whole <see cref="Diagnostic"/> rather than a message and a position, so that
    /// what a caller catches and what a driver collects are the same thing.
    /// </para>
    /// </remarks>
    public sealed class SurtrLexerException : SurtrCompilerException
    {
        /// <summary>Initializes the exception from the diagnostic it reports.</summary>
        /// <param name="diagnostic">The diagnostic being raised.</param>
        public SurtrLexerException(SurtrDiagnostic diagnostic)
            : base(diagnostic.ToString())
        {
            Diagnostic = diagnostic;
        }

        /// <summary>The diagnostic this exception reports.</summary>
        public SurtrDiagnostic Diagnostic { get; }

        /// <summary>What kind of problem this is.</summary>
        public SurtrDiagnosticCode Code => Diagnostic.Code;

        /// <summary>The range of source the offending input covers.</summary>
        public SourceSpan Span => Diagnostic.Span;

        /// <summary>Where in the source the offending input starts.</summary>
        public SourceLocation Location => Diagnostic.Span.Start;

        /// <summary>Identifies the source the failure occurred in.</summary>
        public string SourceName => Diagnostic.SourceName;
    }
}
