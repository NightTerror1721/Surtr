#nullable enable

using Surtr.Compiler.Syntax;

namespace Surtr.Compiler.Diagnostics
{
    /// <summary>
    /// Raised when the token stream does not match the grammar: a missing <c>;</c>, an unexpected
    /// keyword where an expression was due, a modifier on a declaration that cannot take it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inside the parser this is <b>control flow, not failure</b>. A production that cannot
    /// continue throws, and the nearest recovery point — a declaration boundary or a statement
    /// boundary — catches it, resynchronises and carries on, so the diagnostic it already reported
    /// is joined by whatever else the file has wrong rather than hiding it.
    /// </para>
    /// <para>
    /// It reaches a caller only through <see cref="SurtrDiagnosticBag.ThrowIfErrors"/>, or from one
    /// of the narrower entry points that parses a fragment with no boundary to recover at.
    /// </para>
    /// </remarks>
    public sealed class SurtrParserException : SurtrCompilerException
    {
        /// <summary>Initializes the exception from the diagnostic it reports.</summary>
        /// <param name="diagnostic">The diagnostic being raised.</param>
        public SurtrParserException(SurtrDiagnostic diagnostic)
            : base(diagnostic.ToString())
        {
            Diagnostic = diagnostic;
        }

        /// <summary>The diagnostic this exception reports.</summary>
        public SurtrDiagnostic Diagnostic { get; }

        /// <summary>What kind of problem this is.</summary>
        public SurtrDiagnosticCode Code => Diagnostic.Code;

        /// <summary>The range of source the offending tokens cover.</summary>
        public SourceSpan Span => Diagnostic.Span;

        /// <summary>Where in the source the offending token starts.</summary>
        public SourceLocation Location => Diagnostic.Span.Start;

        /// <summary>Identifies the source the failure occurred in.</summary>
        public string SourceName => Diagnostic.SourceName;
    }
}
