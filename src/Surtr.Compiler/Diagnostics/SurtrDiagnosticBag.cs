#nullable enable

using Surtr.Compiler.Syntax;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Compiler.Diagnostics
{
    /// <summary>
    /// Collects everything a compilation has to say, so a pass can keep going after a problem
    /// instead of stopping at the first one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes the front end usable rather than merely correct. A compiler that throws
    /// on the first mismatch reports one problem per run, which means a file with twenty errors
    /// takes twenty runs to fix — and an editor showing a single squiggle is worse than useless,
    /// because the one it shows is often a consequence of a problem further up.
    /// </para>
    /// <para>
    /// The bag is deliberately dumb: it accumulates and answers whether anything failed. Deciding
    /// when to stop is the driver's, since only it knows whether a later pass can say anything
    /// useful — binding a tree the parser recovered inside usually can, lowering one usually
    /// cannot.
    /// </para>
    /// <para>
    /// Not thread-safe, and does not need to be: one bag belongs to one compilation, and the passes
    /// within it run in order.
    /// </para>
    /// </remarks>
    public sealed class SurtrDiagnosticBag : IReadOnlyList<SurtrDiagnostic>
    {
        private readonly List<SurtrDiagnostic> diagnostics = new List<SurtrDiagnostic>();
        private int errorCount;

        /// <summary>How many diagnostics have been reported, of any severity.</summary>
        public int Count => diagnostics.Count;

        /// <summary>How many of them are errors.</summary>
        public int ErrorCount => errorCount;

        /// <summary>Whether anything reported so far stops a module being produced.</summary>
        public bool HasErrors => errorCount > 0;

        /// <summary>The diagnostic at <paramref name="index"/>, in the order they were reported.</summary>
        public SurtrDiagnostic this[int index] => diagnostics[index];

        /// <summary>Records a diagnostic.</summary>
        /// <param name="diagnostic">The diagnostic to record.</param>
        public void Report(SurtrDiagnostic diagnostic)
        {
            if (diagnostic is null)
            {
                throw new ArgumentNullException(nameof(diagnostic));
            }

            diagnostics.Add(diagnostic);

            if (diagnostic.IsError)
            {
                errorCount++;
            }
        }

        /// <summary>Records an error.</summary>
        /// <param name="code">What kind of problem it is.</param>
        /// <param name="message">The problem, worded for a person.</param>
        /// <param name="sourceName">Which source it is in.</param>
        /// <param name="span">The range of source it is about.</param>
        public void ReportError(SurtrDiagnosticCode code, string message, string sourceName, SourceSpan span)
        {
            Report(new SurtrDiagnostic(code, SurtrDiagnosticSeverity.Error, message, sourceName, span));
        }

        /// <summary>Records a warning.</summary>
        /// <param name="code">What kind of problem it is.</param>
        /// <param name="message">The problem, worded for a person.</param>
        /// <param name="sourceName">Which source it is in.</param>
        /// <param name="span">The range of source it is about.</param>
        public void ReportWarning(SurtrDiagnosticCode code, string message, string sourceName, SourceSpan span)
        {
            Report(new SurtrDiagnostic(code, SurtrDiagnosticSeverity.Warning, message, sourceName, span));
        }

        /// <summary>
        /// Drops everything reported after <paramref name="count"/>, which is what a pass that had to
        /// bind something twice keeps from reporting it twice.
        /// </summary>
        /// <remarks>
        /// Deliberately narrow. Generic inference has to bind an argument to learn its type before
        /// the parameter it will be checked against exists, so that first binding is speculative and
        /// anything it found is found again — properly, and once — when the argument is bound against
        /// the settled type. This is not a general "undo": a caller that truncates past work it did
        /// not do itself is discarding someone else's diagnostics.
        /// </remarks>
        /// <param name="count">The <see cref="Count"/> to return to.</param>
        public void TruncateTo(int count)
        {
            if (count < 0 || count > diagnostics.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            for (int i = diagnostics.Count - 1; i >= count; i--)
            {
                if (diagnostics[i].IsError)
                {
                    errorCount--;
                }

                diagnostics.RemoveAt(i);
            }
        }

        /// <summary>
        /// A new bag holding the same diagnostics, with a suppression/severity policy applied: any
        /// diagnostic whose code is in <paramref name="suppressedCodes"/> is dropped entirely, and
        /// - when <paramref name="warningsAsErrors"/> is set - every remaining warning is promoted
        /// to an error.
        /// </summary>
        /// <remarks>
        /// Deliberately produces a new bag rather than mutating this one: a diagnostic is immutable
        /// by design (it outlives the pass that produced it), and a promoted warning is a genuinely
        /// different diagnostic - its severity changed - not the same one with a field flipped.
        /// </remarks>
        public SurtrDiagnosticBag ApplyPolicy(bool warningsAsErrors, IReadOnlyCollection<SurtrDiagnosticCode>? suppressedCodes)
        {
            var result = new SurtrDiagnosticBag();

            for (int i = 0; i < diagnostics.Count; i++)
            {
                var diagnostic = diagnostics[i];
                if (suppressedCodes is not null && suppressedCodes.Contains(diagnostic.Code))
                    continue;

                var severity = warningsAsErrors && diagnostic.Severity == SurtrDiagnosticSeverity.Warning
                    ? SurtrDiagnosticSeverity.Error
                    : diagnostic.Severity;

                result.Report(new SurtrDiagnostic(diagnostic.Code, severity, diagnostic.Message, diagnostic.SourceName, diagnostic.Span));
            }

            return result;
        }

        /// <summary>Adds everything from another bag, keeping its order.</summary>
        /// <param name="other">The bag to drain into this one.</param>
        public void AddRange(SurtrDiagnosticBag other)
        {
            if (other is null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            for (int i = 0; i < other.diagnostics.Count; i++)
            {
                Report(other.diagnostics[i]);
            }
        }

        /// <summary>
        /// Throws the first error, for a caller that wants the simple behaviour.
        /// </summary>
        /// <remarks>
        /// The escape hatch for a script, a test, or anything that only cares whether compilation
        /// worked. It reports the <em>first</em> error rather than a summary because the first one
        /// is the one least likely to be a consequence of another.
        /// </remarks>
        /// <exception cref="SurtrDiagnosticException">Anything in the bag is an error.</exception>
        public void ThrowIfErrors()
        {
            for (int i = 0; i < diagnostics.Count; i++)
            {
                if (diagnostics[i].IsError)
                {
                    throw new SurtrDiagnosticException(diagnostics[i]);
                }
            }
        }

        /// <inheritdoc/>
        public IEnumerator<SurtrDiagnostic> GetEnumerator() => diagnostics.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Carries one diagnostic out as an exception, for callers that asked for that.</summary>
    public sealed class SurtrDiagnosticException : SurtrCompilerException
    {
        /// <summary>Initializes the exception from the diagnostic it reports.</summary>
        /// <param name="diagnostic">The diagnostic being raised.</param>
        public SurtrDiagnosticException(SurtrDiagnostic diagnostic)
            : base(diagnostic is null ? throw new ArgumentNullException(nameof(diagnostic)) : diagnostic.ToString())
        {
            Diagnostic = diagnostic;
        }

        /// <summary>The diagnostic this exception reports.</summary>
        public SurtrDiagnostic Diagnostic { get; }
    }
}
