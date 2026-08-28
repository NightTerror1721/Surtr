#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// The <c>IComparable&lt;T&gt;</c> and <c>IEquatable&lt;T&gt;</c> members the value families
    /// declare, and the contract satisfactions those declarations record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The four core contracts (<c>Language-Syntax.md</c> Â§13.2) are promises the built-ins have
    /// to keep for the language to be uniform: a generic <c>max&lt;T : IComparable&lt;T&gt;&gt;</c>
    /// is only useful if <c>int</c> and <c>string</c> can be its <c>T</c>, and the operators give
    /// the primitives order and equality without their classes admitting it.
    /// </para>
    /// <para>
    /// Every member here is <see cref="SurtrMethodDispatch.Virtual"/>, unlike most of the built-in
    /// surface. Interface dispatch routes through the receiver's vtable, so an implementation that
    /// was not in one could not be found â€” the same rule the iterator contracts follow.
    /// </para>
    /// <para>
    /// The primitive bodies are written to agree with the opcodes the operators lower to: the
    /// interpreter compares floats with IEEE semantics, so <c>float.compareTo</c> spells the same
    /// three-way comparison <c>&lt;=&gt;</c> does, NaN included â€” a NaN operand orders as neither
    /// less nor greater on the opcode path, so it answers 0 here too. A composite's <c>equals</c>
    /// is reference identity, exactly what <see cref="SurtrValueComparer"/> already does.
    /// </para>
    /// </remarks>
    internal static unsafe class SurtrContractBuiltIns
    {
        /// <summary>The descriptor naming <c>IComparable&lt;T&gt;</c> for one concrete argument.</summary>
        private static SurtrClassReference ComparableOf(SurtrClassReference argument)
            => SurtrStandardLibrary.ContractReference("IComparable", argument);

        /// <summary>The descriptor naming <c>IEquatable&lt;T&gt;</c> for one concrete argument.</summary>
        private static SurtrClassReference EquatableOf(SurtrClassReference argument)
            => SurtrStandardLibrary.ContractReference("IEquatable", argument);

        /// <summary>
        /// The parameter every contract method below is declared with, regardless of the concrete
        /// type it really wants.
        /// </summary>
        /// <remarks>
        /// <c>IComparable&lt;T&gt;</c>/<c>IEquatable&lt;T&gt;</c> fix their own member at
        /// <c>compareTo(G0)</c>/<c>equals(G0)</c>, which <see cref="SurtrMethodInfo.SignatureKey"/>
        /// erases to <c>compareTo(E)</c>/<c>equals(E)</c> regardless of what <c>T</c> was
        /// instantiated to â€” that erasure runs once, on the interface's own declaration, and never
        /// looks at the implementer. A concrete parameter here (<c>int</c>, say) would erase to
        /// <c>I</c> and simply miss the slot <c>SurtrTypeLinker.BuildInterfaceDispatch</c> is
        /// looking for; declaring it already-erased is what makes the two match. Every native body
        /// below reads its argument through <see cref="SurtrCallArguments.GetPrimitiveUnchecked"/>
        /// or <see cref="SurtrCallArguments.GetUnchecked{T}"/>, both of which already accept a
        /// boxed or unboxed value identically, so nothing downstream has to change for it.
        /// </remarks>
        private static readonly SurtrClassReference Erased = SurtrClassReference.Erased;

        /// <summary>Declares <c>int : IComparable&lt;int&gt; &amp; IEquatable&lt;int&gt;</c>.</summary>
        internal static void DeclareIntegerContracts(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference integer = SurtrClassReference.Integer;

            builder.Implements(ComparableOf(integer), EquatableOf(integer));

            builder.Method(
                "compareTo",
                SurtrClassReference.Integer,
                SurtrNativeEntryPoint.FromFunctionPointer(&IntCompareTo),
                builder.Params(("other", Erased)),
                dispatch: SurtrMethodDispatch.Virtual, isPure: true);

            builder.Method(
                "equals",
                SurtrClassReference.Boolean,
                SurtrNativeEntryPoint.FromFunctionPointer(&IntEquals),
                builder.Params(("other", Erased)),
                dispatch: SurtrMethodDispatch.Virtual, isPure: true);
        }

        /// <summary>Declares <c>float : IComparable&lt;float&gt; &amp; IEquatable&lt;float&gt;</c>.</summary>
        internal static void DeclareFloatContracts(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference real = SurtrClassReference.Float;

            builder.Implements(ComparableOf(real), EquatableOf(real));

            builder.Method(
                "compareTo",
                SurtrClassReference.Integer,
                SurtrNativeEntryPoint.FromFunctionPointer(&FloatCompareTo),
                builder.Params(("other", Erased)),
                dispatch: SurtrMethodDispatch.Virtual, isPure: true);

            builder.Method(
                "equals",
                SurtrClassReference.Boolean,
                SurtrNativeEntryPoint.FromFunctionPointer(&FloatEquals),
                builder.Params(("other", Erased)),
                dispatch: SurtrMethodDispatch.Virtual, isPure: true);
        }

        /// <summary>
        /// Declares <c>bool : IEquatable&lt;bool&gt;</c>. There is no <c>IComparable</c>: the
        /// language defines no ordering over booleans, and inventing <c>false &lt; true</c> here
        /// would add a semantics no operator has.
        /// </summary>
        internal static void DeclareBooleanContracts(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference boolean = SurtrClassReference.Boolean;

            builder.Implements(EquatableOf(boolean));

            builder.Method(
                "equals",
                SurtrClassReference.Boolean,
                SurtrNativeEntryPoint.FromFunctionPointer(&BoolEquals),
                builder.Params(("other", Erased)),
                dispatch: SurtrMethodDispatch.Virtual, isPure: true);
        }

        /// <summary>Declares <c>char : IComparable&lt;char&gt; &amp; IEquatable&lt;char&gt;</c>.</summary>
        internal static void DeclareCharacterContracts(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference character = SurtrClassReference.Character;

            builder.Implements(ComparableOf(character), EquatableOf(character));

            builder.Method(
                "compareTo",
                SurtrClassReference.Integer,
                SurtrNativeEntryPoint.FromFunctionPointer(&CharCompareTo),
                builder.Params(("other", Erased)),
                dispatch: SurtrMethodDispatch.Virtual, isPure: true);

            builder.Method(
                "equals",
                SurtrClassReference.Boolean,
                SurtrNativeEntryPoint.FromFunctionPointer(&CharEquals),
                builder.Params(("other", Erased)),
                dispatch: SurtrMethodDispatch.Virtual, isPure: true);
        }

        /// <summary>
        /// Records that <c>string</c> satisfies <c>IComparable&lt;string&gt;</c> and
        /// <c>IEquatable&lt;string&gt;</c>. The members themselves already exist â€” <c>compareTo</c>
        /// backs the relational operators and <c>equals</c> the text equality â€” so all that
        /// happens here is the declaration, plus their dispatch being virtual.
        /// </summary>
        internal static void DeclareStringContracts(SurtrBuiltInTypeBuilder builder)
        {
            builder.Implements(ComparableOf(SurtrClassReference.String), EquatableOf(SurtrClassReference.String));
        }

        /// <summary>
        /// Records that <c>bytes</c> satisfies <c>IComparable&lt;bytes&gt;</c> and
        /// <c>IEquatable&lt;bytes&gt;</c>. The members already exist - <c>compareTo</c>/<c>equals</c>
        /// back the explicit content comparison - so all that happens here is the declaration, the
        /// same way <see cref="DeclareStringContracts"/> records string's. Note the difference from
        /// every other composite: the bodies compare <em>contents</em>, not identity, because the
        /// members themselves are declared in <see cref="SurtrBytesBuiltIn"/>, not through
        /// <see cref="DeclareIdentityEquatable"/>.
        /// </summary>
        internal static void DeclareBytesContracts(SurtrBuiltInTypeBuilder builder)
        {
            builder.Implements(ComparableOf(SurtrClassReference.Bytes), EquatableOf(SurtrClassReference.Bytes));
        }

        /// <summary>
        /// Declares <c>array : IEquatable&lt;array&lt;T&gt;&gt;</c> â€” an array equals another array
        /// by identity, like every composite, never by contents.
        /// </summary>
        internal static void DeclareArrayContracts(SurtrBuiltInTypeBuilder builder)
        {
            DeclareIdentityEquatable(builder, SurtrClassReference.Array(SurtrClassReference.GenericParameter(0)));
        }

        /// <summary>
        /// Declares <c>dict : IEquatable&lt;dict&lt;K, V&gt;&gt;</c>, identity equality like every
        /// composite.
        /// </summary>
        internal static void DeclareDictionaryContracts(SurtrBuiltInTypeBuilder builder)
        {
            DeclareIdentityEquatable(
                builder,
                SurtrClassReference.Dictionary(SurtrClassReference.GenericParameter(0), SurtrClassReference.GenericParameter(1)));
        }

        /// <summary>Declares <c>range : IEquatable&lt;range&gt;</c>, identity equality like every composite.</summary>
        internal static void DeclareRangeContracts(SurtrBuiltInTypeBuilder builder)
        {
            DeclareIdentityEquatable(builder, SurtrClassReference.Range);
        }

        /// <summary>
        /// The one <c>equals</c> every composite shares: <c>this === other</c>. A composite's
        /// <c>SurtrValueComparer</c> comparison is identity, so this keeps the same answer the
        /// comparer would give and no composite needs a body of its own.
        /// </summary>
        private static void DeclareIdentityEquatable(SurtrBuiltInTypeBuilder builder, SurtrClassReference self)
        {
            builder.Implements(EquatableOf(self));

            // `self` names the interface argument (`array<G0>`, `dict<G0, G1>`, ...), not the
            // parameter: the slot IEquatable<T> actually exposes is `equals(G0)`, erased to
            // `equals(E)`, and that is what has to be declared here regardless of how compound
            // `self` is - see the remark on Erased above.
            builder.Method(
                "equals",
                SurtrClassReference.Boolean,
                SurtrNativeEntryPoint.FromFunctionPointer(&IdentityEquals),
                builder.Params(("other", Erased)),
                dispatch: SurtrMethodDispatch.Virtual, isPure: true);
        }

        private static int IntCompareTo(SurtrCallArguments arguments)
        {
            int left = arguments.GetPrimitiveUnchecked(0).AsInt;
            int right = arguments.GetPrimitiveUnchecked(1).AsInt;
            return arguments.Return(SurtrValue.CreateInt(left < right ? -1 : left > right ? 1 : 0));
        }

        private static int IntEquals(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(arguments.GetPrimitiveUnchecked(0).AsInt == arguments.GetPrimitiveUnchecked(1).AsInt));

        private static int FloatCompareTo(SurtrCallArguments arguments)
        {
            double left = arguments.GetPrimitiveUnchecked(0).AsFloat;
            double right = arguments.GetPrimitiveUnchecked(1).AsFloat;
            return arguments.Return(SurtrValue.CreateInt(left < right ? -1 : left > right ? 1 : 0));
        }

        private static int FloatEquals(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(arguments.GetPrimitiveUnchecked(0).AsFloat == arguments.GetPrimitiveUnchecked(1).AsFloat));

        private static int BoolEquals(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(arguments.GetPrimitiveUnchecked(0).AsBool == arguments.GetPrimitiveUnchecked(1).AsBool));

        private static int CharCompareTo(SurtrCallArguments arguments)
        {
            char left = arguments.GetPrimitiveUnchecked(0).AsChar;
            char right = arguments.GetPrimitiveUnchecked(1).AsChar;
            return arguments.Return(SurtrValue.CreateInt(left < right ? -1 : left > right ? 1 : 0));
        }

        private static int CharEquals(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(arguments.GetPrimitiveUnchecked(0).AsChar == arguments.GetPrimitiveUnchecked(1).AsChar));

        private static int IdentityEquals(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(ReferenceEquals(
                arguments.GetUnchecked<SurtrObject>(0),
                arguments.GetUnchecked<SurtrObject>(1))));
    }
}
