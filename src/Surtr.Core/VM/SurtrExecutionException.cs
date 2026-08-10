#nullable enable

using Surtr.Runtime.Classes;
using System;

namespace Surtr.VM
{
    /// <summary>
    /// A trap raised by the virtual machine: something a statically typed program is allowed to get
    /// wrong at run time, such as an out-of-range index or a division by zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Traps are ordinary managed exceptions rather than an error flag threaded through the
    /// interpreter, because every one of them is off the hot path by construction: each is raised
    /// from a <c>NoInlining</c> helper the dispatch loop only ever branches to on failure, so the
    /// success path pays a predicted-not-taken compare and nothing else. An error-code protocol
    /// would move that cost onto every instruction that can fail.
    /// </para>
    /// <para>
    /// Anything the compiler is expected to have proved - a receiver being non-null, an argument
    /// count matching a signature, a local index being in range - is deliberately <em>not</em>
    /// trapped. Re-checking those would mean paying at run time for what static typing already
    /// bought.
    /// </para>
    /// </remarks>
    public class SurtrExecutionException : Exception
    {
        /// <summary>Creates a trap with a message.</summary>
        public SurtrExecutionException(string message) : base(message) { }

        /// <summary>Creates a trap with a message and an underlying cause.</summary>
        public SurtrExecutionException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>Creates a trap that surfaces to Surtr code as <paramref name="surtrType"/>.</summary>
        public SurtrExecutionException(string message, SurtrClass surtrType) : base(message)
        {
            SurtrType = surtrType;
        }

        /// <summary>
        /// The Surtr class this trap presents as to a <c>catch</c> clause, or
        /// <see langword="null"/> if it has none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A trap is a CLR exception on the way out of the interpreter, because that is the cheap
        /// shape for something raised from a cold helper. But a Surtr <c>catch</c> matches on a
        /// Surtr class, so unless the trap names one, the only clause that could ever take it is a
        /// catch-all - which is not what an <c>IndexOutOfRangeException</c> in the library is for.
        /// </para>
        /// <para>
        /// Naming the class here rather than deciding it at the catch site keeps the pairing next
        /// to the condition that raises it: the validation policy says which conditions trap, and
        /// each of those is exactly one of these.
        /// </para>
        /// </remarks>
        public SurtrClass? SurtrType { get; }
    }

    /// <summary>
    /// A run that hit its instruction budget and was aborted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike every other trap, this one is <strong>not offered to the handler tables</strong>: it
    /// leaves the interpreter without a Surtr <c>catch</c> ever seeing it. That is the whole point
    /// of the budget. It exists so a host - a compiler folding a <c>const fun</c>, above all - can
    /// run code that may not terminate without hanging, and a program that could catch the abort
    /// and carry on looping would give that guarantee straight back.
    /// </para>
    /// <para>
    /// It is also why exceeding the budget leaves it <em>exhausted</em> rather than cleared: a
    /// later run on the same machine aborts again immediately until the host sets a new budget, so
    /// there is no window in which the ceiling silently stops applying.
    /// </para>
    /// </remarks>
    public sealed class SurtrBudgetExceededException : SurtrExecutionException
    {
        /// <summary>Creates the abort raised when a run runs out of instructions.</summary>
        public SurtrBudgetExceededException(string message) : base(message) { }
    }

    /// <summary>
    /// A Surtr-level exception that no handler caught, carrying the object the program raised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ever surfaces at the boundary. Inside the interpreter a <c>Throw</c> is not a CLR
    /// exception at all: the machine walks its own frames looking for a handler and jumps straight
    /// to one, which keeps a caught exception costing a table scan rather than the microseconds a
    /// CLR throw costs. This type exists for the case where the search runs out of frames - the
    /// exception has to leave the run somehow, and a host, or a native function that called back
    /// in, has to be able to see what was raised.
    /// </para>
    /// <para>
    /// Deriving from <see cref="SurtrExecutionException"/> means a host that only wants "the script
    /// failed" can catch that one type and get both this and every trap.
    /// </para>
    /// </remarks>
    public sealed class SurtrThrownException : SurtrExecutionException
    {
        /// <summary>Creates the carrier for an uncaught Surtr exception.</summary>
        /// <param name="reference">The raised object's heap reference.</param>
        /// <param name="description">A readable description of the raised object.</param>
        public SurtrThrownException(SurtrRef reference, string description)
            : base($"An uncaught Surtr exception escaped: {description}.")
        {
            Reference = reference;
        }

        /// <summary>
        /// The raised object's heap reference.
        /// </summary>
        /// <remarks>
        /// Resolve it with <c>SurtrRuntime.Resolve</c>. The runtime keeps it rooted until execution
        /// is reset, so it stays valid for as long as the host needs to inspect it.
        /// </remarks>
        public SurtrRef Reference { get; }
    }
}
