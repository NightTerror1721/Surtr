#nullable enable

using Surtr.Compiler.Syntax;
using System;
using System.Globalization;

namespace Surtr.Compiler.Diagnostics
{
    /// <summary>How much a diagnostic matters.</summary>
    public enum SurtrDiagnosticSeverity
    {
        /// <summary>Something worth saying that does not stop compilation.</summary>
        Warning = 0,

        /// <summary>Something that stops the compilation producing a module.</summary>
        Error = 1,
    }

    /// <summary>
    /// One problem the compiler found: what it was, where, and how to say it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Immutable and self-contained. A diagnostic outlives the pass that produced it — a driver
    /// collects them from every stage, sorts them and prints them at the end — so it carries
    /// everything needed to render it rather than a reference back to the state that found it.
    /// </para>
    /// <para>
    /// It holds a <see cref="SourceSpan"/> rather than a point because that is what lets a tool
    /// <em>show</em> the problem: a caret under a whole expression rather than under its first
    /// character.
    /// </para>
    /// </remarks>
    public sealed class SurtrDiagnostic
    {
        /// <summary>Creates a diagnostic.</summary>
        /// <param name="code">What kind of problem this is.</param>
        /// <param name="severity">How much it matters.</param>
        /// <param name="message">The problem, worded for a person.</param>
        /// <param name="sourceName">Which source it is in — a file path, or a placeholder.</param>
        /// <param name="span">The range of source it is about.</param>
        public SurtrDiagnostic(
            SurtrDiagnosticCode code,
            SurtrDiagnosticSeverity severity,
            string message,
            string sourceName,
            SourceSpan span)
        {
            Code = code;
            Severity = severity;
            Message = message ?? throw new ArgumentNullException(nameof(message));
            SourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
            Span = span;
        }

        /// <summary>What kind of problem this is.</summary>
        public SurtrDiagnosticCode Code { get; }

        /// <summary>How much it matters.</summary>
        public SurtrDiagnosticSeverity Severity { get; }

        /// <summary>The problem, worded for a person.</summary>
        public string Message { get; }

        /// <summary>Which source it is in.</summary>
        public string SourceName { get; }

        /// <summary>The range of source it is about.</summary>
        public SourceSpan Span { get; }

        /// <summary>Whether this stops the compilation producing a module.</summary>
        public bool IsError => Severity == SurtrDiagnosticSeverity.Error;

        /// <summary>Backing cache for <see cref="Id"/>.</summary>
        private string? _id;

        /// <summary>
        /// The code as it is written down and searched for: <c>SURTR2001</c>. Computed once — the
        /// Language Server reads it once per diagnostic per publish, which is often.
        /// </summary>
        public string Id => _id ??= "SURTR" + ((int)Code).ToString("D4", CultureInfo.InvariantCulture);

        /// <summary>
        /// Renders the diagnostic in the shape every C-family toolchain prints:
        /// <c>file(line,col): error SURTR2001: message</c>.
        /// </summary>
        /// <remarks>
        /// Deliberately the conventional format rather than a nicer one of its own, because IDEs,
        /// editors and CI log parsers already recognise it.
        /// </remarks>
        public override string ToString()
        {
            string severity = IsError ? "error" : "warning";

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}({1},{2}): {3} {4}: {5}",
                SourceName,
                Span.Start.Line,
                Span.Start.Column,
                severity,
                Id,
                Message);
        }
    }
}
