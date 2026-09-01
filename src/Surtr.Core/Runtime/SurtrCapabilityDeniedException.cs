#nullable enable

using System;

namespace Surtr.Runtime
{
    /// <summary>
    /// Thrown when a module's path falls outside a runtime's <see cref="SurtrRuntime.AllowedModulePrefixes"/>.
    /// </summary>
    /// <remarks>
    /// An ordinary CLR exception raised from <see cref="SurtrRuntime.LoadModule(Classes.SurtrModule)"/>
    /// itself, not a VM trap - denial is a load-time, host-facing policy decision, the same family
    /// as <see cref="Objects.SurtrHeapLimitExceededException"/> for the heap's
    /// own hard cap. Kept as its own type rather than the bare <see cref="InvalidOperationException"/>
    /// the rest of <c>LoadModule</c> throws for an unresolved or duplicate module, so a host can
    /// distinguish "this module does not exist" from "this module exists but was not permitted" by
    /// type instead of by parsing a message.
    /// </remarks>
    public sealed class SurtrCapabilityDeniedException : Exception
    {
        /// <summary>Initializes the exception with which module was denied and why.</summary>
        /// <param name="message">A description of the problem.</param>
        public SurtrCapabilityDeniedException(string message) : base(message)
        {
        }
    }
}
