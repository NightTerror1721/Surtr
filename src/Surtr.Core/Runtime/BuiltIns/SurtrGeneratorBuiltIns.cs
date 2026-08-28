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
                dispatch: SurtrMethodDispatch.Virtual,
                isPure: true);

            // `IIterator<T>` extends `IDisposable`, so this fills a contract slot and has to be
            // Virtual like the rest. It is also the whole reason the cursor contract was made
            // disposable: closing a generator is the one case where a cursor genuinely has pending
            // work of its own to run. See docs/Plan-Disposicion.md §3.2.
            builder.Method(
                "dispose",
                SurtrClassReference.Void,
                SurtrNativeEntryPoint.FromFunctionPointer(&Dispose),
                dispatch: SurtrMethodDispatch.Virtual);

            // The coroutine surface (§3.7), and deliberately off every contract: nothing else in
            // the language can be sent to or raised into, so an interface would have exactly one
            // implementation and cost an indirection to reach it. Direct dispatch, like the rest of
            // the built-in surface that satisfies nothing.
            builder.Method(
                "send",
                SurtrClassReference.Boolean,
                SurtrNativeEntryPoint.FromFunctionPointer(&Send),
                builder.Params(("value", SurtrClassReference.Erased)));

            builder.Method(
                "raise",
                SurtrClassReference.Boolean,
                SurtrNativeEntryPoint.FromFunctionPointer(&Raise),
                builder.Params(("error", SurtrBuiltIns.Exception.SelfReference)));

            builder.Property(
                "result",
                SurtrClassReference.Erased,
                SurtrNativeEntryPoint.FromFunctionPointer(&Result),
                isPure: true);
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

        /// <summary>
        /// <c>send(value)</c>: resumes the body, handing the value to the <c>yield</c> it is
        /// suspended at.
        /// </summary>
        /// <remarks>
        /// Answers what <c>moveNext</c> answers - whether the body yielded again - rather than
        /// handing the produced value straight back, so that the two ways of advancing a generator
        /// have one shape and <c>current</c> stays the one place an element is read. Python returns
        /// the value and signals the end with an exception, which only works in a language whose
        /// whole iteration protocol is built on that exception.
        /// </remarks>
        private static int Send(SurtrCallArguments arguments)
        {
            var generator = arguments.GetUnchecked<SurtrGenerator>(0);

            // Read before the write: `Return` overwrites slot 0, and slot 1 is read here first.
            var value = arguments.GetValueUnchecked(1);

            return arguments.Return(SurtrValue.CreateBool(arguments.Runtime.SendToGenerator(generator, value)));
        }

        /// <summary>
        /// <c>raise(error)</c>: throws inside the body at the point it is suspended.
        /// </summary>
        /// <remarks>
        /// Named <c>raise</c> rather than <c>throw</c> because <c>throw</c> is a hard-reserved word
        /// (§1.2), so <c>g.throw(e)</c> would not parse as a member access at all.
        /// </remarks>
        private static int Raise(SurtrCallArguments arguments)
        {
            var generator = arguments.GetUnchecked<SurtrGenerator>(0);
            var error = arguments.GetValueUnchecked(1).AsReference;

            return arguments.Return(SurtrValue.CreateBool(arguments.Runtime.RaiseInGenerator(generator, error)));
        }

        /// <summary>
        /// <c>dispose()</c>: ends the body, running the <c>finally</c> blocks it has pending.
        /// </summary>
        /// <remarks>
        /// What a <c>for-in</c> calls on the way out, whichever way it leaves, and what a
        /// hand-written consumer should call too. Idempotent, as <c>IDisposable</c> requires.
        /// </remarks>
        private static int Dispose(SurtrCallArguments arguments)
        {
            arguments.Runtime.DisposeGenerator(arguments.GetUnchecked<SurtrGenerator>(0));
            return 0;
        }

        /// <summary>
        /// <c>result</c>: what <c>return expr;</c> left behind, or null.
        /// </summary>
        /// <remarks>
        /// Only meaningful once the body has ended; before that it reads null, because a generator
        /// still suspended has not returned anything yet. Typed <c>unknown</c> for the same reason
        /// the value <c>send</c> takes is: a generator declares its <em>element</em> (§3.7), and
        /// there is nowhere in that declaration to write a second type.
        /// </remarks>
        private static int Result(SurtrCallArguments arguments)
            => arguments.Return(arguments.GetUnchecked<SurtrGenerator>(0).GetResult());
    }
}
