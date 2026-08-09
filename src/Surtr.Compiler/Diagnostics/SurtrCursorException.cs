#nullable enable

namespace Surtr.Compiler.Diagnostics
{
    /// <summary>
    /// Thrown by <see cref="Utilities.Cursor{T}"/>'s <c>Consume</c> overloads when none of the
    /// expected elements were found at the current position. Unlike <c>Check</c> and <c>Match</c>,
    /// which are for grammar that is genuinely optional, <c>Consume</c> is for a position the
    /// grammar guarantees is filled - reaching this exception means that guarantee was broken.
    /// </summary>
    public sealed class SurtrCursorException : SurtrCompilerException
    {
        /// <summary>Initializes the exception with the message to surface to the caller.</summary>
        public SurtrCursorException(string message) : base(message)
        {
        }
    }
}
