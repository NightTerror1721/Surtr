#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// The <c>iterator</c> class, and the <c>iterate()</c> member every built-in collection gets in
    /// order to satisfy <c>IIterable&lt;T&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Language-Syntax.md</c> §4.2 defines what <c>for-in</c> can walk by an interface rather
    /// than by a list of blessed types, so that a user class is iterable on exactly the same terms
    /// as an array. That only holds if the built-ins actually satisfy the contract - otherwise the
    /// interface is a promise only user code can keep, and <c>let xs: IIterable&lt;int&gt; = ints;</c>
    /// fails to link.
    /// </para>
    /// <para>
    /// It stays true at the same time that <b>no compiled loop should ever call any of this</b>.
    /// The same section requires <c>for-in</c> over a range, an array, a tuple or a dict to lower
    /// to a direct indexed loop with no iterator object and no interface call. These declarations
    /// are the general path: what makes the language uniform, not what makes the common case fast.
    /// </para>
    /// <para>
    /// Every member here is <see cref="SurtrMethodDispatch.Virtual"/>, unlike the rest of the
    /// built-in surface. Interface dispatch routes through the receiver's vtable, so an
    /// implementation that was not in one could not be found - see
    /// <c>SurtrBuiltInTypeBuilder.Method</c>.
    /// </para>
    /// </remarks>
    internal static unsafe class SurtrIteratorBuiltIns
    {
        /// <summary>
        /// The descriptor naming <c>IIterable&lt;T&gt;</c>, the contract each collection declares.
        /// </summary>
        /// <remarks>
        /// Constructed with the declaring type's own parameter rather than left open: the arity
        /// mangled into the name says one argument follows, and a name that promises one and
        /// supplies none is not a descriptor anything can read.
        /// </remarks>
        internal static SurtrClassReference IterableReference { get; } =
            SurtrStandardLibrary.ContractReference("IIterable", 1);

        /// <summary>The descriptor naming <c>IIterator&lt;T&gt;</c>, which every <c>iterate()</c> returns.</summary>
        internal static SurtrClassReference IteratorReference { get; } =
            SurtrStandardLibrary.ContractReference("IIterator", 1);

        /// <summary>Declares the cursor itself: the two members <c>IIterator&lt;T&gt;</c> asks for.</summary>
        /// <remarks>
        /// <c>current</c> is declared as <c>G0</c> even though <c>iterator</c> declares no generic
        /// parameter of its own, because the contract's slot is keyed on the erased signature and
        /// the element type genuinely is whatever the source held. A compiler substitutes the
        /// source's element type at the use site; the runtime sees a reference either way.
        /// </remarks>
        internal static void DeclareIterator(SurtrBuiltInTypeBuilder builder)
        {
            builder.Implements(IteratorReference);

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

            // Not part of the contract, and deliberately so: rewinding is meaningful on a cursor
            // over a snapshot and meaningless on one over a stream, so it stays off the interface
            // that a user class has to satisfy.
            builder.Method(
                "reset",
                SurtrClassReference.Void,
                SurtrNativeEntryPoint.FromFunctionPointer(&Reset));
        }

        /// <summary>Adds <c>iterate()</c> to one collection and declares the contract it satisfies.</summary>
        internal static void DeclareIterable(SurtrBuiltInTypeBuilder builder, SurtrIteratorKind kind)
        {
            builder.Implements(IterableReference);

            builder.Method(
                "iterate",
                IteratorReference,
                EntryPointFor(kind),
                dispatch: SurtrMethodDispatch.Virtual);
        }

        private static SurtrNativeEntryPoint EntryPointFor(SurtrIteratorKind kind) => kind switch
        {
            SurtrIteratorKind.Array => SurtrNativeEntryPoint.FromFunctionPointer(&IterateArray),
            SurtrIteratorKind.String => SurtrNativeEntryPoint.FromFunctionPointer(&IterateString),
            SurtrIteratorKind.Tuple => SurtrNativeEntryPoint.FromFunctionPointer(&IterateTuple),
            SurtrIteratorKind.Range => SurtrNativeEntryPoint.FromFunctionPointer(&IterateRange),
            _ => SurtrNativeEntryPoint.FromFunctionPointer(&IterateDictionary),
        };

        #region The cursor

        private static SurtrValue MoveNext(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(arguments.GetUnchecked<SurtrIterator>(0).MoveNext(arguments.Runtime));

        private static SurtrValue Current(SurtrCallArguments arguments)
            => arguments.GetUnchecked<SurtrIterator>(0).Current;

        private static SurtrValue Reset(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrIterator>(0).Reset();
            return SurtrValue.Null;
        }

        #endregion

        #region iterate()

        private static SurtrValue IterateArray(SurtrCallArguments arguments)
            => Register(arguments, SurtrIteratorKind.Array, arguments.GetUnchecked<SurtrArray>(0));

        private static SurtrValue IterateString(SurtrCallArguments arguments)
            => Register(arguments, SurtrIteratorKind.String, arguments.GetUnchecked<SurtrString>(0));

        private static SurtrValue IterateTuple(SurtrCallArguments arguments)
            => Register(arguments, SurtrIteratorKind.Tuple, arguments.GetUnchecked<SurtrTuple>(0));

        private static SurtrValue IterateRange(SurtrCallArguments arguments)
            => Register(arguments, SurtrIteratorKind.Range, arguments.GetUnchecked<SurtrRange>(0));

        private static SurtrValue IterateDictionary(SurtrCallArguments arguments)
        {
            var source = arguments.GetUnchecked<SurtrDictionary>(0);

            // The snapshot is taken here rather than lazily, so the pairs a loop sees are settled
            // by the moment iteration began - see SurtrIterator's remarks on mutation.
            var keys = new SurtrValue[source.Count];
            int next = 0;
            foreach (var entry in source.Entries)
                keys[next++] = entry.Key;

            return Register(arguments, SurtrIteratorKind.Dictionary, source, keys);
        }

        private static SurtrValue Register(
            SurtrCallArguments arguments,
            SurtrIteratorKind kind,
            SurtrObject source,
            SurtrValue[]? keys = null)
        {
            var runtime = arguments.Runtime;
            return runtime.ValueOf(runtime.NewIterator(kind, source, keys));
        }

        #endregion
    }
}
