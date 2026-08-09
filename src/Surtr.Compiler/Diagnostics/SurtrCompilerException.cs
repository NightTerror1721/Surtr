#nullable enable

using System;

namespace Surtr.Compiler.Diagnostics
{
    /// <summary>
    /// Root of every exception the compiler pipeline raises. Lexing, parsing, binding and code
    /// generation each derive their own exception types from this one, so a caller that only
    /// cares that <em>something</em> in the compiler failed can catch
    /// <see cref="SurtrCompilerException"/> without naming every stage individually.
    /// </summary>
    public abstract class SurtrCompilerException : Exception
    {
        /// <summary>Initializes the exception with a message describing the failure.</summary>
        protected SurtrCompilerException(string message) : base(message)
        {
        }

        /// <summary>Initializes the exception with a message and the exception that caused it.</summary>
        protected SurtrCompilerException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
