using Microsoft.CodeAnalysis;

namespace Surtr.Interop.SourceGenerator
{
    /// <summary>
    /// The generator's diagnostics: every warning and error it can report. Keeping them in one place
    /// (with their release-tracking entries in <c>AnalyzerReleases.*.md</c>) is what lets the Roslyn
    /// analyzer-release analyzer validate them instead of being suppressed.
    /// </summary>
    internal static class SurtrDiagnostics
    {
        /// <summary>The category every rule belongs to.</summary>
        public const string Category = "Surtr.Interop";

        /// <summary>
        /// A public member is deliberately left out of the generated Surtr surface: it has no Surtr
        /// equivalent (unmapped operator, ref/in, abstract, multi-dimensional indexer, open generic).
        /// </summary>
        public static readonly DiagnosticDescriptor UnsupportedMember = new DiagnosticDescriptor(
            "SURTRINTEROP001",
            "Member not exposed to Surtr",
            "Member '{0}' on type '{1}' is not exposed to Surtr: {2}",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        /// <summary>
        /// A TypeDescriptor/ReturnDescriptor written in an attribute is not a well-formed Surtr
        /// descriptor; registration would fail at run time, so it is caught at compile time.
        /// </summary>
        public static readonly DiagnosticDescriptor InvalidDescriptor = new DiagnosticDescriptor(
            "SURTRINTEROP002",
            "Invalid Surtr descriptor",
            "'{0}' on type '{1}' is not a well-formed Surtr descriptor: '{2}'",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>The closed-form TypeArguments count does not match the type's generic arity.</summary>
        public static readonly DiagnosticDescriptor ArityMismatch = new DiagnosticDescriptor(
            "SURTRINTEROP003",
            "Generic arity mismatch",
            "TypeArguments for '{0}' supplies {1} argument(s) but the type declares arity {2}",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        /// <summary>A C# static class cannot be registered as a native Surtr type (it has no instances).</summary>
        public static readonly DiagnosticDescriptor StaticType = new DiagnosticDescriptor(
            "SURTRINTEROP004",
            "Static type cannot be a native type",
            "Type '{0}' is static and cannot be registered as a native Surtr type",
            Category,
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    }
}