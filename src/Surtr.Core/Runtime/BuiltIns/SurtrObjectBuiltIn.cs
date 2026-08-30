#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// The three members every class in the language answers to by default: <c>equals</c>,
    /// <c>hashCode</c> and <c>toString</c>, declared once on the root <c>object</c> class.
    /// </summary>
    /// <remarks>
    /// <c>equals</c>/<c>hashCode</c> forward straight to <see cref="SurtrValueComparer"/> - the
    /// same comparer <c>==</c> and every dictionary already use - so a Surtr-level
    /// <c>x.equals(y)</c> can never disagree with <c>x == y</c>. It already compares a boxed value
    /// type structurally (<c>SurtrValueComparer.ReferencesEqual</c>'s <c>SurtrInstance</c> case),
    /// so this default is correct as-is for a value class or an enum with no member of its own -
    /// no per-family or per-type override is declared here for that reason. Both are declared
    /// <see cref="SurtrMethodDispatch.Virtual"/>, since the whole point is that a call through a
    /// statically-known subtype can still land on that subtype's own override; every built-in
    /// this reaches is sealed, so a call site whose receiver's type is known devirtualises to a
    /// direct call and never pays for the vtable slot at all (<c>Binder.cs</c>'s sealed check).
    /// </remarks>
    internal static unsafe class SurtrObjectBuiltIn
    {
        /// <summary>Declares <c>equals</c>, <c>hashCode</c> and <c>toString</c> on <c>object</c> itself.</summary>
        internal static void Declare(SurtrBuiltInTypeBuilder builder)
        {
            // `object?` and `object` share one descriptor: a reference's nullability needs no
            // encoding of its own (a reference is its payload, and null is already representable
            // in it), so the parameter is declared against the plain `object` reference either way.
            builder.Method(
                "equals",
                SurtrClassReference.Boolean,
                SurtrNativeEntryPoint.FromFunctionPointer(&DefaultEquals),
                builder.Params(("other", SurtrBuiltIns.Object.SelfReference)),
                dispatch: SurtrMethodDispatch.Virtual,
                isPure: true);

            builder.Method(
                "hashCode",
                SurtrClassReference.Integer,
                SurtrNativeEntryPoint.FromFunctionPointer(&DefaultHashCode),
                dispatch: SurtrMethodDispatch.Virtual,
                isPure: true);

            builder.Method(
                "toString",
                SurtrClassReference.String,
                SurtrNativeEntryPoint.FromFunctionPointer(&DefaultToString),
                dispatch: SurtrMethodDispatch.Virtual,
                isPure: true);
        }

        private static int DefaultEquals(SurtrCallArguments arguments)
        {
            var comparer = arguments.Runtime.ValueComparer;
            bool result = comparer.ValuesEqual(arguments.GetValueUnchecked(0), arguments.GetValueUnchecked(1));
            return arguments.Return(SurtrValue.CreateBool(result));
        }

        private static int DefaultHashCode(SurtrCallArguments arguments)
        {
            int hash = arguments.Runtime.ValueComparer.HashOf(arguments.GetValueUnchecked(0));
            return arguments.Return(SurtrValue.CreateInt(hash));
        }

        private static int DefaultToString(SurtrCallArguments arguments)
        {
            var self = arguments.Get<SurtrObject>(0);
            return arguments.Return(SurtrValue.CreateReference(arguments.Runtime.NewString(self.Class.Name).GetSurtrReference()));
        }
    }
}
