#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// The members of the built-in <c>generator</c> class: the general path a generator is walked
    /// through when it travels as an interface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One class satisfies both contracts.</b> <c>generator&lt;T&gt;</c> declares
    /// <c>IIterable&lt;T&gt;</c> and <c>IIterator&lt;T&gt;</c> together, and <c>iterate()</c> hands
    /// back the receiver. A generator object is single-use (<c>docs/Plan-Generadores.md</c> §12.2),
    /// so a separate cursor could only ever hold the position this object already holds - it would
    /// be an allocation per loop that buys nothing. JavaScript and Python landed on the same shape
    /// for the same reason.
    /// </para>
    /// <para>
    /// <b>None of this is what a compiled loop runs.</b> A <c>for-in</c> whose sequence is
    /// statically a <c>generator&lt;T&gt;</c> lowers to <c>GenIterate</c>/<c>GenResume</c>/
    /// <c>GenCurrent</c>, which do the same work without an interface dispatch, a native call and a
    /// nested run per element. What lives here is the uniformity: a generator assigned to an
    /// <c>IIterable&lt;int&gt;</c>, or handed to <c>Sequence&lt;T&gt;</c>, iterates on exactly the
    /// same terms as anything else - the same division <c>Language-Syntax.md</c> §4.2 already makes
    /// for arrays and ranges.
    /// </para>
    /// <para>
    /// Every member is <see cref="SurtrMethodDispatch.Virtual"/>, like the rest of the iteration
    /// surface: interface dispatch resolves through the receiver's vtable, so an implementation
    /// that was not in one could not be found.
    /// </para>
    /// </remarks>
    internal static unsafe class SurtrGeneratorBuiltIns
    {
        /// <summary>Declares <c>generator</c>'s members and the two contracts it satisfies.</summary>
        internal static void Declare(SurtrBuiltInTypeBuilder builder)
        {
            builder.Implements(SurtrIteratorBuiltIns.IterableReference);
            builder.Implements(SurtrIteratorBuiltIns.IteratorReference);

            builder.Method(
                "iterate",
                SurtrIteratorBuiltIns.IteratorReference,
                SurtrNativeEntryPoint.FromFunctionPointer(&Iterate),
                dispatch: SurtrMethodDispatch.Virtual);

            builder.Method(
                "moveNext",
                SurtrClassReference.Boolean,
                SurtrNativeEntryPoint.FromFunctionPointer(&MoveNext),
                dispatch: SurtrMethodDispatch.Virtual);

            builder.Property(
                "current",
                SurtrClassReference.GenericParameter(0),
                SurtrNativeEntryPoint.FromFunctionPointer(&Current),
                dispatch: SurtrMethodDispatch.Virtual);
        }

        /// <summary>
        /// <c>iterate()</c>: hands back the receiver, refusing a generator that has already been
        /// walked.
        /// </summary>
        /// <remarks>
        /// The check is the whole reason this is not simply the identity. Walking an already-started
        /// generator would iterate nothing and say nothing, which is a bug that never announces
        /// itself; refusing it names the mistake and points at the fix, which is to call the
        /// generator function again. <c>GenIterate</c> is the compiled path's copy of exactly this
        /// test, run once per loop rather than per element.
        /// </remarks>
        private static int Iterate(SurtrCallArguments arguments)
        {
            var generator = arguments.GetUnchecked<SurtrGenerator>(0);

            if (generator.State != SurtrGeneratorState.NotStarted)
            {
                throw new SurtrExecutionException(
                    "This generator has already been iterated. A generator object is single-use; call the generator function again to start a new one.",
                    SurtrBuiltIns.InvalidOperationException);
            }

            return arguments.Return(arguments.Runtime.ValueOf(generator));
        }

        /// <summary>
        /// <c>moveNext()</c>: resumes the body until its next <c>yield</c> or its end.
        /// </summary>
        /// <remarks>
        /// Re-enters the machine, which is legal because the interpreter published its stack pointer
        /// and instruction pointer before transferring here. This is the per-element cost the
        /// compiled path exists to avoid.
        /// </remarks>
        private static int MoveNext(SurtrCallArguments arguments)
        {
            var generator = arguments.GetUnchecked<SurtrGenerator>(0);
            return arguments.Return(SurtrValue.CreateBool(arguments.Runtime.ResumeGenerator(generator)));
        }

        /// <summary>
        /// <c>current</c>: the value the last <c>yield</c> produced.
        /// </summary>
        /// <remarks>
        /// Reads the same field <c>GenCurrent</c> reads, through the same delegation chain, which is
        /// what keeps the two paths from ever disagreeing about what the last element was.
        /// </remarks>
        private static int Current(SurtrCallArguments arguments)
            => arguments.Return(arguments.GetUnchecked<SurtrGenerator>(0).GetCurrent());
    }
}
