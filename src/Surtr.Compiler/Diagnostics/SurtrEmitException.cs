#nullable enable

using System;

namespace Surtr.Compiler.Diagnostics
{
    /// <summary>
    /// Raised when code generation meets a bound node it cannot turn into bytecode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not how a program's own mistakes are reported — those are diagnostics, collected in a
    /// <see cref="SurtrDiagnosticBag"/>, and emission only ever runs on a body that already bound
    /// cleanly. What this names is the other case: a construct the emitter does not cover yet. It is
    /// thrown rather than collected because it aborts one body outright, and because the caller that
    /// can turn it into something a user should read is several frames up — const folding turns it
    /// into <see cref="SurtrDiagnosticCode.ConstEvaluationFailed"/> against the call site that asked
    /// for the fold, which is the span a reader can act on.
    /// </para>
    /// <para>
    /// A construct listed in <c>docs/Compiler-Plan.md</c> §5 that has not been lowered yet reaches
    /// here, which is deliberate: an emitter that quietly emitted something else would be worse than
    /// one that says it cannot.
    /// </para>
    /// </remarks>
    public sealed class SurtrEmitException : SurtrCompilerException
    {
        /// <summary>Initializes the exception with a message describing what could not be emitted.</summary>
        public SurtrEmitException(string message) : base(message)
        {
        }

        /// <summary>Initializes the exception with a message and the exception that caused it.</summary>
        public SurtrEmitException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
