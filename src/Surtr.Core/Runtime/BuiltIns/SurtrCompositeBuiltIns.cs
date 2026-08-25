#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;
using System.Globalization;

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
    /// <c>get(index)</c> returns. Element access there stays an opcode with a statically known
    /// index, which the compiler already types exactly - <c>TupGetC</c>, which carries that index
    /// as an immediate precisely because it can never be anything but a constant.
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

            builder.Property("length", integer, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayLength), isPure: true);
            builder.Property("capacity", integer, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayCapacity), isPure: true);
            builder.Property("isEmpty", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayIsEmpty), isPure: true);

            builder.Method("clear", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayClear));
            builder.Method("reverse", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayReverse));
            builder.Method("removeAt", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayRemoveAt), builder.Params(("index", integer)));
            builder.Method("truncate", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayTruncate), builder.Params(("length", integer)));
            builder.Method("reserve", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayReserve), builder.Params(("capacity", integer)));

            // Everything below names the array's own element type through G0.
            SurtrClassReference element = SurtrClassReference.GenericParameter(0);

            builder.Method("get", element, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayGet), builder.Params(("index", integer)), isPure: true);
            builder.Method("set", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArraySet), builder.Params(("index", integer), ("value", element)));
            builder.Method("push", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayPush), builder.Params(("value", element)));
            builder.Method("pop", element, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayPop));
            builder.Method("insert", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayInsert), builder.Params(("index", integer), ("value", element)));
            builder.Method("indexOf", integer, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayIndexOf), builder.Params(("value", element)), isPure: true);
            builder.Method("contains", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayContains), builder.Params(("value", element)), isPure: true);
            builder.Method("remove", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&ArrayRemove), builder.Params(("value", element)));

            // `items.sort((a, b) => a.score - b.score)` is written out in Language-Syntax.md Â§8, so
            // the comparator form is the one that has to exist. There is deliberately no
            // parameterless `sort()`: ordering elements by nothing in particular would mean
            // dispatching IComparable on an erased slot, and saying which order you want is one
            // lambda.
            builder.Method(
                "sort",
                SurtrClassReference.Void,
                SurtrNativeEntryPoint.FromFunctionPointer(&ArraySort),
                builder.Params(("comparator", SurtrClassReference.Closure(integer, element, element))));
        }

        /// <summary>
        /// Sorts in place through a Surtr comparator, stably.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A merge sort rather than <see cref="Array.Sort(Array)"/>, for two reasons that both
        /// come back to a comparator being <em>Surtr code</em>. It is stable, so equal elements
        /// keep the order they were in - which a script sorting by one field of several depends on
        /// and introsort does not give. And it is the algorithm written here, so its result cannot
        /// change with the BCL underneath: the same array and the same comparator sort the same way
        /// on every platform and every runtime version, which is the same reason
        /// <c>SurtrString.ComputeHash</c> is not <c>string.GetHashCode</c>.
        /// </para>
        /// <para>
        /// Every comparison re-enters the VM. That is inherent to a comparator written in the
        /// language rather than a cost this shape adds, and the frame protocol supports it - but it
        /// does mean sorting is not something to do per frame.
        /// </para>
        /// </remarks>
        private static int ArraySort(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrArray>(0);
            var comparator = arguments.Get<SurtrClosure>(1);
            var runtime = arguments.Runtime;

            int length = self.Count;
            if (length < 2)
                return arguments.Return(SurtrValue.Null);

            var items = self.Items;
            var scratch = new SurtrValue[length];

            // Reused across every comparison rather than allocated per call: a sort makes
            // n log n of them.
            var operands = new SurtrValue[2];

            for (int width = 1; width < length; width <<= 1)
            {
                for (int start = 0; start < length; start += width << 1)
                {
                    int middle = Math.Min(start + width, length);
                    int end = Math.Min(start + (width << 1), length);

                    Merge(items, scratch, start, middle, end, runtime, comparator, operands);
                }

                for (int i = 0; i < length; i++)
                    items[i] = scratch[i].Raw;
            }

            return arguments.Return(SurtrValue.Null);
        }

        private static void Merge(
            SurtrRawValue* items,
            SurtrValue[] scratch,
            int start,
            int middle,
            int end,
            SurtrRuntime runtime,
            SurtrClosure comparator,
            SurtrValue[] operands)
        {
            int left = start;
            int right = middle;

            for (int next = start; next < end; next++)
            {
                // `<= 0` rather than `< 0` is what makes this stable: on a tie the left run, which
                // held the earlier element, goes first.
                bool takeLeft = left < middle && (right >= end || Compare(runtime, comparator, operands, SurtrValue.FromRaw(items[left]), SurtrValue.FromRaw(items[right])) <= 0);

                scratch[next] = SurtrValue.FromRaw(takeLeft ? items[left++] : items[right++]);
            }
        }

        private static int Compare(
            SurtrRuntime runtime,
            SurtrClosure comparator,
            SurtrValue[] operands,
            SurtrValue left,
            SurtrValue right)
        {
            operands[0] = left;
            operands[1] = right;

            return runtime.InvokeClosure(comparator, operands).AsInt;
        }

        // A G0 argument arrives as whatever the caller had on the stack, tag and all - the
        // parameter is erased, not boxed - so these read the raw slot rather than a typed one.
        private static int ArrayGet(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrArray>(0);
            int index = arguments.GetInt(1);

            if (!self.IsInRange(index))
                throw new ArgumentOutOfRangeException(nameof(index), index, "Array index is out of range.");

            return arguments.Return(self[index]);
        }

        private static int ArraySet(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrArray>(0);
            int index = arguments.GetInt(1);

            if (!self.IsInRange(index))
                throw new ArgumentOutOfRangeException(nameof(index), index, "Array index is out of range.");

            self[index] = arguments.GetValueUnchecked(2);
            return arguments.Return(SurtrValue.Null);
        }

        private static int ArrayPush(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrArray>(0).Add(arguments.GetValueUnchecked(1));
            return arguments.Return(SurtrValue.Null);
        }

        private static int ArrayPop(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrArray>(0);

            if (self.Count == 0)
                throw new InvalidOperationException("Cannot pop from an empty array.");

            return arguments.Return(self.RemoveLast());
        }

        private static int ArrayInsert(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrArray>(0);
            int index = arguments.GetInt(1);

            // Inserting at Count is appending, so the upper bound is inclusive here.
            if ((uint)index > (uint)self.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Array insertion index is out of range.");

            self.Insert(index, arguments.GetValueUnchecked(2));
            return arguments.Return(SurtrValue.Null);
        }

        private static int ArrayIndexOf(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(arguments.GetUnchecked<SurtrArray>(0)
                .IndexOf(arguments.GetValueUnchecked(1), arguments.Runtime.ValueComparer)));

        private static int ArrayContains(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(arguments.GetUnchecked<SurtrArray>(0)
                .Contains(arguments.GetValueUnchecked(1), arguments.Runtime.ValueComparer)));

        private static int ArrayRemove(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(arguments.GetUnchecked<SurtrArray>(0)
                .Remove(arguments.GetValueUnchecked(1), arguments.Runtime.ValueComparer)));

        private static int ArrayLength(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(arguments.GetUnchecked<SurtrArray>(0).Count));

        private static int ArrayCapacity(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(arguments.GetUnchecked<SurtrArray>(0).Capacity));

        private static int ArrayIsEmpty(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(arguments.GetUnchecked<SurtrArray>(0).Count == 0));

        // A void method still has to return something down the one native signature there is;
        // the null reference is the agreed filler, and the caller discards it because the
        // instruction's retCount is zero.
        private static int ArrayClear(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrArray>(0).Clear();
            return arguments.Return(SurtrValue.Null);
        }

        private static int ArrayReverse(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrArray>(0).Reverse();
            return arguments.Return(SurtrValue.Null);
        }

        private static int ArrayRemoveAt(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrArray>(0).RemoveAt(arguments.GetInt(1));
            return arguments.Return(SurtrValue.Null);
        }

        private static int ArrayTruncate(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrArray>(0).Truncate(arguments.GetInt(1));
            return arguments.Return(SurtrValue.Null);
        }

        private static int ArrayReserve(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrArray>(0).EnsureCapacity(arguments.GetInt(1));
            return arguments.Return(SurtrValue.Null);
        }
        #endregion

        #region Tuple
        internal static void DeclareTuple(SurtrBuiltInTypeBuilder builder)
        {
            builder.Property("length", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&TupleLength), isPure: true);
            builder.Property("isEmpty", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&TupleIsEmpty), isPure: true);
        }

        private static int TupleLength(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(arguments.GetUnchecked<SurtrTuple>(0).Elements.Length));

        private static int TupleIsEmpty(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(arguments.GetUnchecked<SurtrTuple>(0).Elements.Length == 0));
        #endregion

        #region Dictionary
        internal static void DeclareDictionary(SurtrBuiltInTypeBuilder builder)
        {
            // `length`, not `count`: every built-in collection answers its size under the same
            // name, so there is no rule about which container uses which word.
            builder.Property("length", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryCount), isPure: true);
            builder.Property("isEmpty", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryIsEmpty), isPure: true);

            builder.Method("clear", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryClear));

            // The utility "constructor" a dict cannot spell as one (Â§5.3 gives it no `Dictionary<K,V>`
            // name to construct through) â€” an empty `{}` literal plus a capacity hint, the same
            // symmetry `array.reserve` already gives a `[]` literal.
            builder.Method(
                "reserve", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryReserve),
                builder.Params(("capacity", SurtrClassReference.Integer)));

            SurtrClassReference key = SurtrClassReference.GenericParameter(0);
            SurtrClassReference value = SurtrClassReference.GenericParameter(1);

            builder.Method("get", value, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryGet), builder.Params(("key", key)), isPure: true);
            builder.Method("set", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&DictionarySet), builder.Params(("key", key), ("value", value)));
            builder.Method("containsKey", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryContainsKey), builder.Params(("key", key)), isPure: true);
            builder.Method("remove", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryRemove), builder.Params(("key", key)));
            builder.Method("keys", SurtrClassReference.Array(key), SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryKeys), isPure: true);
            builder.Method("values", SurtrClassReference.Array(value), SurtrNativeEntryPoint.FromFunctionPointer(&DictionaryValues), isPure: true);
        }

        private static int DictionaryGet(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrDictionary>(0);

            if (!self.TryGet(arguments.GetValueUnchecked(1), out SurtrValue value))
                throw new KeyNotFoundException("The dictionary has no entry under that key.");

            return arguments.Return(value);
        }

        private static int DictionarySet(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrDictionary>(0).Set(arguments.GetValueUnchecked(1), arguments.GetValueUnchecked(2));
            return arguments.Return(SurtrValue.Null);
        }

        /// <summary>
        /// Hints at the entry count about to be added, the same way <c>array.reserve</c> hints at
        /// element count â€” whichever of the dictionary's two stores is live gets the call, since
        /// exactly one of <see cref="SurtrDictionary.Entries"/>/<see cref="SurtrDictionary.IntEntries"/>
        /// is ever in use for a given dictionary.
        /// </summary>
        private static int DictionaryReserve(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrDictionary>(0);
            int capacity = arguments.GetInt(1);

            if (self.IntEntries is { } ints)
                ints.EnsureCapacity(capacity);
            else
                self.Entries!.EnsureCapacity(capacity);

            return arguments.Return(SurtrValue.Null);
        }

        private static int DictionaryContainsKey(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(arguments.GetUnchecked<SurtrDictionary>(0).ContainsKey(arguments.GetValueUnchecked(1))));

        private static int DictionaryRemove(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(arguments.GetUnchecked<SurtrDictionary>(0).Remove(arguments.GetValueUnchecked(1))));

        // The result array is parameterised from the dictionary's own descriptor, so {int: string}
        // yields an int[] and a string[] rather than two arrays of nothing in particular.
        private static int DictionaryKeys(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrDictionary>(0);
            var keys = arguments.Runtime.NewArray(
                SurtrClassReference.Array(self.TypeReference.GetDictionaryKeyType()),
                self.Count);

            self.CopyKeysTo(keys);
            return arguments.Return(SurtrValue.CreateReference(keys.GetSurtrReference()));
        }

        private static int DictionaryValues(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrDictionary>(0);
            var values = arguments.Runtime.NewArray(
                SurtrClassReference.Array(self.TypeReference.GetDictionaryValueType()),
                self.Count);

            self.CopyValuesTo(values);
            return arguments.Return(SurtrValue.CreateReference(values.GetSurtrReference()));
        }

        private static int DictionaryCount(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(arguments.GetUnchecked<SurtrDictionary>(0).Count));

        private static int DictionaryIsEmpty(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(arguments.GetUnchecked<SurtrDictionary>(0).IsEmpty));

        private static int DictionaryClear(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrDictionary>(0).Clear();
            return arguments.Return(SurtrValue.Null);
        }
        #endregion

        #region Range
        /// <summary>
        /// The <c>range</c> members. A range is an inline value (§2.9), so its receiver crosses
        /// the call as the three-slot block it is - start at slot 0, end at slot 1, the inclusive
        /// flag at slot 2 - and every member reads that block directly; nothing here sees a box.
        /// </summary>
        internal static void DeclareRange(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference integer = SurtrClassReference.Integer;
            SurtrClassReference boolean = SurtrClassReference.Boolean;

            builder.Property("start", integer, SurtrNativeEntryPoint.FromFunctionPointer(&RangeStart), isPure: true);
            builder.Property("end", integer, SurtrNativeEntryPoint.FromFunctionPointer(&RangeEnd), isPure: true);
            builder.Property("length", integer, SurtrNativeEntryPoint.FromFunctionPointer(&RangeLength), isPure: true);
            builder.Property("isEmpty", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&RangeIsEmpty), isPure: true);
            builder.Property("isInclusive", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&RangeIsInclusive), isPure: true);

            builder.Method("contains", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&RangeContains), builder.Params(("value", integer)), isPure: true);
            builder.Method("toString", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&RangeToString), isPure: true);
        }

        private static int RangeStart(SurtrCallArguments arguments)
            => arguments.Return(arguments.GetValueUnchecked(0));

        private static int RangeEnd(SurtrCallArguments arguments)
            => arguments.Return(arguments.GetValueUnchecked(1));

        private static int RangeLength(SurtrCallArguments arguments)
        {
            int start = arguments.GetInt(0);
            int end = arguments.GetInt(1);
            bool inclusive = arguments.GetBool(2);

            // Counted in 64 bits and then clamped, because the span of an inclusive range over
            // the whole of int is one more than int can hold.
            long count = (long)end - start + (inclusive ? 1 : 0);
            return arguments.Return(SurtrValue.CreateInt(count <= 0 ? 0 : count > int.MaxValue ? int.MaxValue : (int)count));
        }

        private static int RangeIsEmpty(SurtrCallArguments arguments)
        {
            int end = arguments.GetInt(1);
            bool inclusive = arguments.GetBool(2);
            return arguments.Return(SurtrValue.CreateBool(inclusive ? end < arguments.GetInt(0) : end <= arguments.GetInt(0)));
        }

        private static int RangeIsInclusive(SurtrCallArguments arguments)
            => arguments.Return(arguments.GetValueUnchecked(2));

        private static int RangeContains(SurtrCallArguments arguments)
        {
            int start = arguments.GetInt(0);
            int end = arguments.GetInt(1);
            bool inclusive = arguments.GetBool(2);
            int value = arguments.GetInt(3);

            return arguments.Return(SurtrValue.CreateBool(value >= start && (inclusive ? value <= end : value < end)));
        }

        /// <summary>Backs <c>string(aRange)</c> — the same <c>a..b</c>/<c>a..=b</c> spelling range literals use.</summary>
        private static int RangeToString(SurtrCallArguments arguments)
        {
            string start = arguments.GetInt(0).ToString(CultureInfo.InvariantCulture);
            string end = arguments.GetInt(1).ToString(CultureInfo.InvariantCulture);

            return arguments.Return(arguments.Runtime.NewStringValue(
                arguments.GetBool(2) ? $"{start}..={end}" : $"{start}..{end}"));
        }
        #endregion

        #region Closure
        internal static void DeclareClosure(SurtrBuiltInTypeBuilder builder)
        {
            builder.Property("arity", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&ClosureArity), isPure: true);
            builder.Property("upValueCount", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&ClosureUpValueCount), isPure: true);
            builder.Property("isNative", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&ClosureIsNative), isPure: true);
        }

        private static int ClosureArity(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(arguments.GetUnchecked<SurtrClosure>(0).ParameterCount));

        private static int ClosureUpValueCount(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(arguments.GetUnchecked<SurtrClosure>(0).UpValues.Length));

        private static int ClosureIsNative(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(arguments.GetUnchecked<SurtrClosure>(0).IsNative));
        #endregion
    }
}
