#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// The members of the parameterised built-ins - <c>array</c>, <c>tuple</c>, <c>dict</c> and
    /// <c>closure</c> - and the host functions behind them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>array</c> and <c>dict</c> declare their element-polymorphic members against their own
    /// generic parameters: <c>array</c> declares <c>T</c> and <c>dict</c> declares <c>K</c> and
    /// <c>V</c>, so <c>push</c> takes <c>G0</c> and <c>get</c> maps <c>G0</c> to <c>G1</c>. A
    /// compiler substitutes the receiver's actual parameterisation at the call site, which is what
    /// lets <c>int[].push("x")</c> be rejected against metadata alone. At run time a generic
    /// parameter is an erased slot, so none of this costs anything: the interpreter hands a native
    /// member the raw stack slot, tag included, and an <c>int</c> pushed into an <c>int[]</c> is
    /// never boxed on the way.
    /// </para>
    /// <para>
    /// <c>tuple</c> and <c>closure</c> declare no parameters and keep the thin surface, and that
    /// is not an oversight either: both are parameterised by a list whose length varies per value,
    /// and a tuple's element type varies per <em>index</em>, so no fixed parameter could name what
    /// <c>get(index)</c> returns. Element access there stays what it always was - <c>TupGet</c>
    /// with a statically known index, which the compiler already types exactly.
    /// </para>
    /// <para>
    /// Every member here mirrors behaviour that also lives as an ordinary method on
    /// <see cref="SurtrArray"/>, <see cref="SurtrDictionary"/> or <see cref="SurtrTuple"/>, which
    /// is what the interpreter calls from <c>ArrGet</c>, <c>DictSet</c>, <c>TupGet</c> and their
    /// siblings. The declarations exist so Surtr source can reach the same behaviour by name.
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

            // Everything below names the array's own element type through G0.
            SurtrClassReference element = SurtrClassReference.GenericParameter(0);

            builder.Method("get", element, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayGet), builder.Params(("index", integer)));
            builder.Method("set", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArraySet), builder.Params(("index", integer), ("value", element)));
            builder.Method("push", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayPush), builder.Params(("value", element)));
            builder.Method("pop", element, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayPop));
            builder.Method("insert", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayInsert), builder.Params(("index", integer), ("value", element)));
            builder.Method("indexOf", integer, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayIndexOf), builder.Params(("value", element)));
            builder.Method("contains", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayContains), builder.Params(("value", element)));
            builder.Method("remove", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayRemove), builder.Params(("value", element)));
        }

        // A G0 argument arrives as whatever the caller had on the stack, tag and all - the
        // parameter is erased, not boxed - so these read the raw slot rather than a typed one.
        private static SurtrValue ArrayGet(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrArray>(0);
            int index = arguments.GetInt(1);

            if (!self.IsInRange(index))
                throw new ArgumentOutOfRangeException(nameof(index), index, "Array index is out of range.");

            return self[index];
        }

        private static SurtrValue ArraySet(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrArray>(0);
            int index = arguments.GetInt(1);

            if (!self.IsInRange(index))
                throw new ArgumentOutOfRangeException(nameof(index), index, "Array index is out of range.");

            self[index] = arguments.GetValueUnchecked(2);
            return SurtrValue.Null;
        }

        private static SurtrValue ArrayPush(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrArray>(0).Add(arguments.GetValueUnchecked(1));
            return SurtrValue.Null;
        }

        private static SurtrValue ArrayPop(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrArray>(0);

            if (self.Count == 0)
                throw new InvalidOperationException("Cannot pop from an empty array.");

            return self.RemoveLast();
        }

        private static SurtrValue ArrayInsert(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrArray>(0);
            int index = arguments.GetInt(1);

            // Inserting at Count is appending, so the upper bound is inclusive here.
            if ((uint)index > (uint)self.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Array insertion index is out of range.");

            self.Insert(index, arguments.GetValueUnchecked(2));
            return SurtrValue.Null;
        }

        private static SurtrValue ArrayIndexOf(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetUnchecked<SurtrArray>(0)
                .IndexOf(arguments.GetValueUnchecked(1), arguments.Runtime.ValueComparer));

        private static SurtrValue ArrayContains(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(arguments.GetUnchecked<SurtrArray>(0)
                .Contains(arguments.GetValueUnchecked(1), arguments.Runtime.ValueComparer));

        private static SurtrValue ArrayRemove(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(arguments.GetUnchecked<SurtrArray>(0)
                .Remove(arguments.GetValueUnchecked(1), arguments.Runtime.ValueComparer));

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
            // `length`, not `count`: every built-in collection answers its size under the same
            // name, so there is no rule about which container uses which word.
            builder.Property("length", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryCount));
            builder.Property("isEmpty", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryIsEmpty));

            builder.Method("clear", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryClear));

            SurtrClassReference key = SurtrClassReference.GenericParameter(0);
            SurtrClassReference value = SurtrClassReference.GenericParameter(1);

            builder.Method("get", value, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryGet), builder.Params(("key", key)));
            builder.Method("set", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&DictionarySet), builder.Params(("key", key), ("value", value)));
            builder.Method("containsKey", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryContainsKey), builder.Params(("key", key)));
            builder.Method("remove", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryRemove), builder.Params(("key", key)));
            builder.Method("keys", SurtrClassReference.Array(key), SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryKeys));
            builder.Method("values", SurtrClassReference.Array(value), SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryValues));
        }

        private static SurtrValue DictionaryGet(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrDictionary>(0);

            if (!self.TryGet(arguments.GetValueUnchecked(1), out SurtrValue value))
                throw new KeyNotFoundException("The dictionary has no entry under that key.");

            return value;
        }

        private static SurtrValue DictionarySet(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrDictionary>(0).Set(arguments.GetValueUnchecked(1), arguments.GetValueUnchecked(2));
            return SurtrValue.Null;
        }

        private static SurtrValue DictionaryContainsKey(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(arguments.GetUnchecked<SurtrDictionary>(0).ContainsKey(arguments.GetValueUnchecked(1)));

        private static SurtrValue DictionaryRemove(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(arguments.GetUnchecked<SurtrDictionary>(0).Remove(arguments.GetValueUnchecked(1)));

        // The result array is parameterised from the dictionary's own descriptor, so {int: string}
        // yields an int[] and a string[] rather than two arrays of nothing in particular.
        private static SurtrValue DictionaryKeys(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrDictionary>(0);
            var keys = arguments.Runtime.NewArray(
                SurtrClassReference.Array(self.TypeReference.GetDictionaryKeyType()),
                self.Entries.Count);

            self.CopyKeysTo(keys);
            return SurtrValue.CreateReference(keys.GetSurtrReference());
        }

        private static SurtrValue DictionaryValues(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrDictionary>(0);
            var values = arguments.Runtime.NewArray(
                SurtrClassReference.Array(self.TypeReference.GetDictionaryValueType()),
                self.Entries.Count);

            self.CopyValuesTo(values);
            return SurtrValue.CreateReference(values.GetSurtrReference());
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

        #region Range
        internal static void DeclareRange(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference integer = SurtrClassReference.Integer;
            SurtrClassReference boolean = SurtrClassReference.Boolean;

            builder.Property("start", integer, SurtrNativeEntryPoint.FromFunctionPointer(&RangeStart));
            builder.Property("end", integer, SurtrNativeEntryPoint.FromFunctionPointer(&RangeEnd));
            builder.Property("length", integer, SurtrNativeEntryPoint.FromFunctionPointer(&RangeLength));
            builder.Property("isEmpty", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&RangeIsEmpty));
            builder.Property("isInclusive", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&RangeIsInclusive));

            builder.Method("contains", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&RangeContains), builder.Params(("value", integer)));
        }

        private static SurtrValue RangeStart(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetUnchecked<SurtrRange>(0).Start);

        private static SurtrValue RangeEnd(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetUnchecked<SurtrRange>(0).End);

        private static SurtrValue RangeLength(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetUnchecked<SurtrRange>(0).Length);

        private static SurtrValue RangeIsEmpty(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(arguments.GetUnchecked<SurtrRange>(0).IsEmpty);

        private static SurtrValue RangeIsInclusive(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(arguments.GetUnchecked<SurtrRange>(0).IsInclusive);

        private static SurtrValue RangeContains(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(arguments.GetUnchecked<SurtrRange>(0).Contains(arguments.GetInt(1)));
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
