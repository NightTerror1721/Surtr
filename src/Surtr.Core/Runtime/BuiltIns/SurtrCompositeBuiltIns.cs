#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// The members of the parameterised built-ins - <c>array</c>, <c>tuple</c>, <c>dict</c> and
    /// <c>closure</c> - and the host functions behind them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These four carry a much thinner member surface than <c>string</c> or the primitives, and it
    /// is a consequence of the descriptor system rather than an oversight. A descriptor names one
    /// concrete type: <c>I</c>, <c>AS</c>, <c>DIS</c>. There is no way to write "the element type
    /// of whatever this array is", so <c>push</c>, <c>pop</c>, <c>get</c>, <c>set</c>,
    /// <c>indexOf</c>, <c>keys</c> and the rest of the element-polymorphic surface cannot be given
    /// a signature at all. Only the members whose types do not depend on the parameterisation -
    /// lengths, emptiness, <c>clear</c>, <c>reverse</c>, index-based removal - are declarable, and
    /// those are what is here.
    /// </para>
    /// <para>
    /// The behaviour itself is not missing: it lives as ordinary methods on
    /// <see cref="SurtrArray"/>, <see cref="SurtrDictionary"/> and <see cref="SurtrTuple"/>, which
    /// is what the interpreter calls when it executes <c>ArrGet</c>, <c>DictSet</c>, <c>TupGet</c>
    /// and their siblings. Element access was always going to be an opcode rather than a method
    /// call, so the gap is narrower than the member list suggests - it is <c>push</c>/<c>pop</c>
    /// that have no route into Surtr source yet, and closing it needs a descriptor form for a
    /// built-in's own type parameter.
    /// </para>
    /// </remarks>
    internal static unsafe class SurtrCompositeBuiltIns
    {
        #region Array
        internal static void DeclareArray(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference integer = SurtrClassReference.Integer;
            SurtrClassReference boolean = SurtrClassReference.Boolean;

            builder.Property("length", integer, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayLength));
            builder.Property("capacity", integer, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayCapacity));
            builder.Property("isEmpty", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayIsEmpty));

            builder.Method("clear", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayClear));
            builder.Method("reverse", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayReverse));
            builder.Method("removeAt", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayRemoveAt), builder.Params(("index", integer)));
            builder.Method("truncate", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayTruncate), builder.Params(("length", integer)));
            builder.Method("reserve", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayReserve), builder.Params(("capacity", integer)));
        }

        private static SurtrValue ArrayLength(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetUnchecked<SurtrArray>(0).Count);

        private static SurtrValue ArrayCapacity(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetUnchecked<SurtrArray>(0).Capacity);

        private static SurtrValue ArrayIsEmpty(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(arguments.GetUnchecked<SurtrArray>(0).Count == 0);

        // A void method still has to return something down the one native signature there is;
        // the null reference is the agreed filler, and the caller discards it because the
        // instruction's retCount is zero.
        private static SurtrValue ArrayClear(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrArray>(0).Clear();
            return SurtrValue.Null;
        }

        private static SurtrValue ArrayReverse(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrArray>(0).Reverse();
            return SurtrValue.Null;
        }

        private static SurtrValue ArrayRemoveAt(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrArray>(0).RemoveAt(arguments.GetInt(1));
            return SurtrValue.Null;
        }

        private static SurtrValue ArrayTruncate(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrArray>(0).Truncate(arguments.GetInt(1));
            return SurtrValue.Null;
        }

        private static SurtrValue ArrayReserve(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrArray>(0).EnsureCapacity(arguments.GetInt(1));
            return SurtrValue.Null;
        }
        #endregion

        #region Tuple
        internal static void DeclareTuple(SurtrBuiltInTypeBuilder builder)
        {
            builder.Property("length", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&TupleLength));
            builder.Property("isEmpty", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&TupleIsEmpty));
        }

        private static SurtrValue TupleLength(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetUnchecked<SurtrTuple>(0).Elements.Length);

        private static SurtrValue TupleIsEmpty(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(arguments.GetUnchecked<SurtrTuple>(0).Elements.Length == 0);
        #endregion

        #region Dictionary
        internal static void DeclareDictionary(SurtrBuiltInTypeBuilder builder)
        {
            builder.Property("count", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryCount));
            builder.Property("isEmpty", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryIsEmpty));

            builder.Method("clear", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryClear));
        }

        private static SurtrValue DictionaryCount(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetUnchecked<SurtrDictionary>(0).Entries.Count);

        private static SurtrValue DictionaryIsEmpty(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(arguments.GetUnchecked<SurtrDictionary>(0).Entries.Count == 0);

        private static SurtrValue DictionaryClear(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrDictionary>(0).Clear();
            return SurtrValue.Null;
        }
        #endregion

        #region Closure
        internal static void DeclareClosure(SurtrBuiltInTypeBuilder builder)
        {
            builder.Property("arity", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&ClosureArity));
            builder.Property("upValueCount", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&ClosureUpValueCount));
            builder.Property("isNative", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&ClosureIsNative));
        }

        private static SurtrValue ClosureArity(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetUnchecked<SurtrClosure>(0).ParameterCount);

        private static SurtrValue ClosureUpValueCount(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetUnchecked<SurtrClosure>(0).UpValues.Length);

        private static SurtrValue ClosureIsNative(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(arguments.GetUnchecked<SurtrClosure>(0).IsNative);
        #endregion
    }
}
