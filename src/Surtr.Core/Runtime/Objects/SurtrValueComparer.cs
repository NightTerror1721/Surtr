#nullable enable

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// Decides when two <see cref="SurtrValue"/>s are the same value, and hashes them to match.
    /// One per <see cref="SurtrRuntime"/>, shared by every dictionary and every array search it
    /// owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raw bit equality would be wrong in both directions, which is why this type exists rather
    /// than a plain <c>Raw == Raw</c>. It is too strict for the values the language treats as
    /// values - two distinct string objects holding <c>"hello"</c> must be one dictionary key, and
    /// a boxed <c>5</c> must be the same key as an unboxed <c>5</c>, since boxing is a
    /// representation choice and not a different type. And it is too loose for floats, where
    /// <c>+0.0</c> and <c>-0.0</c> have different bits but are the same number, and where two NaNs
    /// have the same bits without being equal under IEEE 754.
    /// </para>
    /// <para>
    /// Everything else compares by identity, because that is what it means: two arrays with the
    /// same contents are two arrays. Tuples are the exception among the composites - they are
    /// immutable, so structural equality is well defined and stable for the life of the key, which
    /// is exactly the property a hash table needs. That recursion terminates: a tuple's elements
    /// are fixed at pack time, so a tuple cannot contain itself, and any path through a mutable
    /// container stops at that container's identity.
    /// </para>
    /// <para>
    /// The <see cref="IEqualityComparer{T}"/> implementation is explicit and forwards to
    /// <see cref="ValuesEqual"/> and <see cref="HashOf"/>. Anything on a hot path should call
    /// those directly: this type is sealed, so a direct call is a call the JIT can inline, while
    /// the interface form is a dispatch it cannot. <c>Dictionary&lt;,&gt;</c> pays the interface
    /// cost regardless, which is unavoidable and the same cost any custom-compared dictionary
    /// pays.
    /// </para>
    /// </remarks>
    public sealed class SurtrValueComparer : IEqualityComparer<SurtrValue>
    {
        private readonly SurtrRuntime _runtime;

        internal SurtrValueComparer(SurtrRuntime runtime) => _runtime = runtime;

        /// <summary>Whether <paramref name="left"/> and <paramref name="right"/> are the same Surtr value.</summary>
        public bool ValuesEqual(SurtrValue left, SurtrValue right)
        {
            // Identical bits settles the overwhelming majority of comparisons: same primitive, or
            // the same entity. Floats are the one case where identical bits are not conclusive -
            // NaN is unequal to itself - so they are filtered out before this can answer.
            if (left.IsFloat || right.IsFloat)
                return left.IsFloat && right.IsFloat && left.AsFloat.Equals(right.AsFloat);

            if (left.Raw == right.Raw)
                return true;

            bool leftIsReference = left.IsReference;
            if (leftIsReference == right.IsReference)
            {
                // Two primitives whose bits already differed: different numbers, or the same
                // number under different tags - an int 5 and a char 5 are not the same value.
                return leftIsReference && ReferencesEqual(left.AsReference, right.AsReference);
            }

            // A box against a raw primitive. This asymmetric case has to be handled explicitly, or
            // boxed 5 and unboxed 5 would hash into the same bucket and then compare unequal
            // inside it - a key findable by neither representation.
            return leftIsReference
                ? BoxEquals(left.AsReference, right)
                : BoxEquals(right.AsReference, left);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool BoxEquals(SurtrRef boxedSide, SurtrValue primitiveSide)
            => _runtime.Context.EntityRegistry.Get(boxedSide) is SurtrBoxed boxed
                && ValuesEqual(boxed.Value, primitiveSide);

        /// <summary>A hash consistent with <see cref="ValuesEqual"/>.</summary>
        public int HashOf(SurtrValue value)
        {
            // Double.GetHashCode already normalises -0.0 to 0.0 and every NaN to one code, which
            // is precisely the normalisation ValuesEqual assumes.
            if (value.IsFloat)
                return value.AsFloat.GetHashCode();

            if (!value.IsReference)
                return value.Raw.GetHashCode();

            return ReferenceHash(value.AsReference);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool ReferencesEqual(SurtrRef left, SurtrRef right)
        {
            // Both sides are non-null and distinct entities by the time we get here, so the only
            // way they can still be equal is by carrying equal contents.
            var leftEntity = _runtime.Context.EntityRegistry.Get(left);
            var rightEntity = _runtime.Context.EntityRegistry.Get(right);

            if (leftEntity is null || rightEntity is null)
                return false;

            switch (leftEntity)
            {
                case SurtrString leftString:
                    return rightEntity is SurtrString rightString && leftString.TextEquals(rightString);

                case SurtrBoxed leftBoxed:
                    return rightEntity is SurtrBoxed rightBoxed && ValuesEqual(leftBoxed.Value, rightBoxed.Value);

                case SurtrTuple leftTuple:
                    return rightEntity is SurtrTuple rightTuple && TuplesEqual(leftTuple, rightTuple);

                default:
                    return false;
            }
        }

        private bool TuplesEqual(SurtrTuple left, SurtrTuple right)
        {
            var leftElements = left.Elements;
            var rightElements = right.Elements;

            if (leftElements.Length != rightElements.Length)
                return false;

            for (int i = 0; i < leftElements.Length; i++)
            {
                if (!ValuesEqual(leftElements[i], rightElements[i]))
                    return false;
            }

            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int ReferenceHash(SurtrRef reference)
        {
            var entity = _runtime.Context.EntityRegistry.Get(reference);

            switch (entity)
            {
                case SurtrString text:
                    return text.Hash;

                // Hashing through the boxed value, not the box, is what puts a boxed 5 and an
                // unboxed 5 in the same bucket - the other half of treating them as one value.
                case SurtrBoxed boxed:
                    return HashOf(boxed.Value);

                case SurtrTuple tuple:
                    return TupleHash(tuple);

                default:
                    return reference;
            }
        }

        private int TupleHash(SurtrTuple tuple)
        {
            var elements = tuple.Elements;

            // FNV-1a over the element hashes. Order-sensitive, which matters: (1, 2) and (2, 1)
            // are different keys.
            unchecked
            {
                int hash = (int)2166136261;
                for (int i = 0; i < elements.Length; i++)
                    hash = (hash ^ HashOf(elements[i])) * 16777619;

                return hash;
            }
        }

        bool IEqualityComparer<SurtrValue>.Equals(SurtrValue left, SurtrValue right) => ValuesEqual(left, right);

        int IEqualityComparer<SurtrValue>.GetHashCode(SurtrValue value) => HashOf(value);
    }
}
