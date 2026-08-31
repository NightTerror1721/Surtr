#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Text;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// The <c>bytes</c> built-in's members, and the host functions behind them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>bytes</c> is a mutable binary buffer (§1.1): the language has no one-byte primitive,
    /// so the surface deals in <c>int</c> - a read answers 0-255 and every write validates that
    /// range. The collection verbs mirror <see cref="SurtrCompositeBuiltIns"/>' array surface so
    /// the two feel alike: <c>length</c>, <c>get</c>/<c>set</c>, <c>push</c>/<c>pop</c>,
    /// <c>insert</c>/<c>removeAt</c>, <c>truncate</c>/<c>reserve</c>, <c>clear</c>,
    /// <c>indexOf</c>/<c>contains</c>. The indexer is the same read/write the array's is: <c>op_[]</c>
    /// is declared as a static operator (§5.6), so <c>b[i]</c> and <c>b[i] = v</c> bind and emit
    /// through the ordinary operator machinery with no special case anywhere. What an array does
    /// not have is the buffer's read side: <c>slice</c>, <c>concat</c>, the copy family
    /// (<c>copy</c>/<c>copyFrom</c>/<c>copyTo</c>), the UTF-8 bridge and the hex <c>toString</c>.
    /// </para>
    /// <para>
    /// Equality follows the composite precedent, not the string one: <c>==</c>/<c>!=</c> are
    /// identity, exactly like arrays - a buffer is mutable, so value equality on <c>==</c> would
    /// change meaning while you held a reference. Content comparison is explicit, through
    /// <c>equals</c>/<c>compareTo</c>, which also satisfy <c>IEquatable&lt;bytes&gt;</c> and
    /// <c>IComparable&lt;bytes&gt;</c>. The parameters are declared erased for the same reason the
    /// string ones are: the contracts fix their member at <c>equals(G0)</c>/<c>compareTo(G0)</c>,
    /// which erases to <c>E</c>, so a concrete parameter here would miss the vtable slot.
    /// </para>
    /// </remarks>
    internal static unsafe class SurtrBytesBuiltIn
    {
        private const int MinByte = 0;
        private const int MaxByte = 255;

        internal static void Declare(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference bytes = SurtrClassReference.Bytes;
            SurtrClassReference integer = SurtrClassReference.Integer;
            SurtrClassReference boolean = SurtrClassReference.Boolean;
            SurtrClassReference text = SurtrClassReference.String;
            SurtrClassReference intArray = SurtrClassReference.Array(integer);

            builder.Property("length", integer, SurtrNativeEntryPoint.FromFunctionPointer(&GetLength), isPure: true);
            builder.Property("capacity", integer, SurtrNativeEntryPoint.FromFunctionPointer(&GetCapacity), isPure: true);
            builder.Property("isEmpty", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&GetIsEmpty), isPure: true);

            builder.Method("get", integer, SurtrNativeEntryPoint.FromFunctionPointer(&Get), builder.Params(("index", integer)), isPure: true);
            builder.Method("set", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&Set), builder.Params(("index", integer), ("value", integer)));
            builder.Method("push", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&Push), builder.Params(("value", integer)));
            builder.Method("pop", integer, SurtrNativeEntryPoint.FromFunctionPointer(&Pop));
            builder.Method("insert", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&Insert), builder.Params(("index", integer), ("value", integer)));
            builder.Method("removeAt", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&RemoveAt), builder.Params(("index", integer)));
            builder.Method("truncate", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&Truncate), builder.Params(("length", integer)));
            builder.Method("reserve", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&Reserve), builder.Params(("capacity", integer)));
            builder.Method("clear", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&Clear));
            builder.Method("reverse", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&Reverse));
            builder.Method("indexOf", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IndexOf), builder.Params(("value", integer)), isPure: true);
            builder.Method("lastIndexOf", integer, SurtrNativeEntryPoint.FromFunctionPointer(&LastIndexOf), builder.Params(("value", integer)), isPure: true);
            builder.Method("contains", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&Contains), builder.Params(("value", integer)), isPure: true);
            builder.Method("slice", bytes, SurtrNativeEntryPoint.FromFunctionPointer(&Slice), builder.Params(("start", integer), ("length", integer)), isPure: true);
            builder.Method("concat", bytes, SurtrNativeEntryPoint.FromFunctionPointer(&Concat), builder.Params(("other", bytes)), isPure: true);

            // Copying: a snapshot, a reusable buffer reset, and a region write.
            builder.Method("copy", bytes, SurtrNativeEntryPoint.FromFunctionPointer(&Copy), isPure: true);
            builder.Method("copyFrom", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&CopyFrom), builder.Params(("source", bytes)));
            builder.Method("copyFrom", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&CopyFromSlice), builder.Params(("source", bytes), ("sourceOffset", integer), ("count", integer)));
            builder.Method("copyTo", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&CopyTo), builder.Params(("target", bytes)));
            builder.Method("copyTo", SurtrClassReference.Void, SurtrNativeEntryPoint.FromFunctionPointer(&CopyToOffset), builder.Params(("target", bytes), ("targetOffset", integer)));

            // The `b[i]` read/write indexer (§5.6). A static operator whose receiver is argument
            // zero, the exact shape a user-declared `operator[]` has, so the existing operator
            // machinery binds and emits it with no special case: read is (receiver, index) -> int,
            // write is (receiver, index, value) -> void. Same bodies as get/set, same checks.
            builder.Method(
                "op_[]",
                integer,
                SurtrNativeEntryPoint.FromFunctionPointer(&IndexGet),
                builder.Params(("receiver", bytes), ("index", integer)),
                isStatic: true,
                isPure: true);
            builder.Method(
                "op_[]",
                SurtrClassReference.Void,
                SurtrNativeEntryPoint.FromFunctionPointer(&IndexSet),
                builder.Params(("receiver", bytes), ("index", integer), ("value", integer)),
                isStatic: true);

            builder.Method("toString", text, SurtrNativeEntryPoint.FromFunctionPointer(&ToHexString), isPure: true, dispatch: SurtrMethodDispatch.Virtual, isOverride: true);
            builder.Method("decodeUTF8", text, SurtrNativeEntryPoint.FromFunctionPointer(&DecodeUTF8), isPure: true);

            // Erased parameter and virtual dispatch, exactly like string's: see the class remarks.
            builder.Method("equals", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&ContentEquals), builder.Params(("other", SurtrClassReference.Erased)), dispatch: SurtrMethodDispatch.Virtual, isPure: true);
            builder.Method("compareTo", integer, SurtrNativeEntryPoint.FromFunctionPointer(&CompareTo), builder.Params(("other", SurtrClassReference.Erased)), dispatch: SurtrMethodDispatch.Virtual, isPure: true);

            builder.Method("empty", bytes, SurtrNativeEntryPoint.FromFunctionPointer(&Empty), isStatic: true, isPure: true);
            builder.Method("withCapacity", bytes, SurtrNativeEntryPoint.FromFunctionPointer(&WithCapacity), builder.Params(("capacity", integer)), isStatic: true, isPure: true);
            builder.Method("from", bytes, SurtrNativeEntryPoint.FromFunctionPointer(&From), builder.Params(("array", intArray)), isStatic: true, isPure: true);
            builder.Method(
                "from",
                bytes,
                SurtrNativeEntryPoint.FromFunctionPointer(&FromSlice),
                builder.Params(("array", intArray), ("offset", integer), ("length", integer)),
                isStatic: true,
                isPure: true);
            builder.Method("fromUTF8", bytes, SurtrNativeEntryPoint.FromFunctionPointer(&FromUTF8), builder.Params(("text", text)), isStatic: true, isPure: true);
            builder.Method("repeat", bytes, SurtrNativeEntryPoint.FromFunctionPointer(&Repeat), builder.Params(("value", integer), ("count", integer)), isStatic: true, isPure: true);
        }

        /// <summary>Validates that a caller-supplied int fits in one byte, on the way into the buffer.</summary>
        private static void ValidateByte(SurtrCallArguments arguments, int parameterIndex, string parameterName)
        {
            int value = arguments.GetInt(parameterIndex);
            if ((uint)value > MaxByte)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    value,
                    $"A byte must be in [{MinByte}, {MaxByte}], not {value}.");
            }
        }

        private static int Get(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            int index = arguments.GetInt(1);

            if (!self.IsInRange(index))
                throw new ArgumentOutOfRangeException(nameof(index), index, "Bytes index is out of range.");

            return arguments.Return(SurtrValue.CreateInt(self[index]));
        }

        private static int Set(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            int index = arguments.GetInt(1);

            if (!self.IsInRange(index))
                throw new ArgumentOutOfRangeException(nameof(index), index, "Bytes index is out of range.");

            ValidateByte(arguments, 2, "value");
            self[index] = (byte)arguments.GetInt(2);
            return arguments.Return(SurtrValue.Null);
        }

        private static int Push(SurtrCallArguments arguments)
        {
            ValidateByte(arguments, 1, "value");
            arguments.GetUnchecked<SurtrBytes>(0).Add((byte)arguments.GetInt(1));
            return arguments.Return(SurtrValue.Null);
        }

        private static int Pop(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);

            if (self.IsEmpty)
                throw new InvalidOperationException("Cannot pop from an empty buffer.");

            return arguments.Return(SurtrValue.CreateInt(self.RemoveLast()));
        }

        private static int Insert(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            int index = arguments.GetInt(1);

            // Inserting at Count is appending, so the upper bound is inclusive here.
            if ((uint)index > (uint)self.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Bytes insertion index is out of range.");

            ValidateByte(arguments, 2, "value");
            self.Insert(index, (byte)arguments.GetInt(2));
            return arguments.Return(SurtrValue.Null);
        }

        private static int RemoveAt(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrBytes>(0).RemoveAt(arguments.GetInt(1));
            return arguments.Return(SurtrValue.Null);
        }

        private static int Truncate(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrBytes>(0).Truncate(arguments.GetInt(1));
            return arguments.Return(SurtrValue.Null);
        }

        private static int Reserve(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrBytes>(0).EnsureCapacity(arguments.GetInt(1));
            return arguments.Return(SurtrValue.Null);
        }

        private static int Clear(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrBytes>(0).Clear();
            return arguments.Return(SurtrValue.Null);
        }

        private static int Reverse(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrBytes>(0).Reverse();
            return arguments.Return(SurtrValue.Null);
        }

        private static int IndexOf(SurtrCallArguments arguments)
        {
            ValidateByte(arguments, 1, "value");
            return arguments.Return(SurtrValue.CreateInt(arguments.GetUnchecked<SurtrBytes>(0).IndexOf((byte)arguments.GetInt(1))));
        }

        private static int LastIndexOf(SurtrCallArguments arguments)
        {
            ValidateByte(arguments, 1, "value");
            return arguments.Return(SurtrValue.CreateInt(arguments.GetUnchecked<SurtrBytes>(0).LastIndexOf((byte)arguments.GetInt(1))));
        }

        private static int Contains(SurtrCallArguments arguments)
        {
            ValidateByte(arguments, 1, "value");
            return arguments.Return(SurtrValue.CreateBool(arguments.GetUnchecked<SurtrBytes>(0).Contains((byte)arguments.GetInt(1))));
        }

        private static int GetLength(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(arguments.GetUnchecked<SurtrBytes>(0).Count));

        private static int GetCapacity(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(arguments.GetUnchecked<SurtrBytes>(0).Items.Length));

        private static int GetIsEmpty(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(arguments.GetUnchecked<SurtrBytes>(0).Count == 0));

        private static int Slice(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            int start = arguments.GetInt(1);
            int length = arguments.GetInt(2);

            // Widened to long before adding: arbitrary caller-supplied ints can wrap past
            // int.MaxValue in the sum, which would otherwise let an out-of-range pair slip past.
            if (start < 0 || length < 0 || (long)start + length > self.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(start),
                    $"Slice [{start}, {start + length}) is out of range for a {self.Count}-byte buffer.");
            }

            var data = new byte[length];
            Buffer.BlockCopy(self.Items, start, data, 0, length);
            return arguments.Return(arguments.Runtime.NewBytesValue(data));
        }

        private static int Concat(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            var other = arguments.GetUnchecked<SurtrBytes>(1);

            var combined = new byte[self.Count + other.Count];
            Buffer.BlockCopy(self.Items, 0, combined, 0, self.Count);
            Buffer.BlockCopy(other.Items, 0, combined, self.Count, other.Count);
            return arguments.Return(arguments.Runtime.NewBytesValue(combined));
        }

        // The indexer bodies are the same reads and writes `get`/`set` do; the receiver arrives as
        // argument zero because the operator is static.
        private static int IndexGet(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            int index = arguments.GetInt(1);

            if (!self.IsInRange(index))
                throw new ArgumentOutOfRangeException(nameof(index), index, "Bytes index is out of range.");

            return arguments.Return(SurtrValue.CreateInt(self[index]));
        }

        private static int IndexSet(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            int index = arguments.GetInt(1);

            if (!self.IsInRange(index))
                throw new ArgumentOutOfRangeException(nameof(index), index, "Bytes index is out of range.");

            ValidateByte(arguments, 2, "value");
            self[index] = (byte)arguments.GetInt(2);
            return arguments.Return(SurtrValue.Null);
        }

        /// <summary>Backs <c>bytes.copy()</c>: an independent buffer with the same live bytes.</summary>
        private static int Copy(SurtrCallArguments arguments)
            => arguments.Return(arguments.Runtime.NewBytesValue(arguments.GetUnchecked<SurtrBytes>(0).ToArray()));

        /// <summary>Backs <c>bytes.copyFrom(source)</c>: this buffer's contents are replaced by source's.</summary>
        private static int CopyFrom(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            var source = arguments.GetUnchecked<SurtrBytes>(1);

            CopyRangeInto(self, source, sourceOffset: 0, count: source.Count);
            return arguments.Return(SurtrValue.Null);
        }

        /// <summary>Backs <c>bytes.copyFrom(source, sourceOffset, count)</c>: replaced by a bounded slice of source.</summary>
        private static int CopyFromSlice(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            var source = arguments.GetUnchecked<SurtrBytes>(1);
            int sourceOffset = arguments.GetInt(2);
            int count = arguments.GetInt(3);

            if (sourceOffset < 0 || count < 0 || (long)sourceOffset + count > source.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceOffset),
                    $"offset {sourceOffset} and count {count} are out of range for a {source.Count}-byte source.");
            }

            CopyRangeInto(self, source, sourceOffset, count);
            return arguments.Return(SurtrValue.Null);
        }

        /// <summary>Backs <c>bytes.copyTo(target)</c>: this buffer's bytes are written into target from index 0.</summary>
        private static int CopyTo(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            var target = arguments.GetUnchecked<SurtrBytes>(1);

            WriteRangeInto(target, self, targetOffset: 0);
            return arguments.Return(SurtrValue.Null);
        }

        /// <summary>Backs <c>bytes.copyTo(target, targetOffset)</c>: written into target at an offset, growing it to fit.</summary>
        private static int CopyToOffset(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            var target = arguments.GetUnchecked<SurtrBytes>(1);
            int targetOffset = arguments.GetInt(2);

            if (targetOffset < 0)
                throw new ArgumentOutOfRangeException(nameof(targetOffset), targetOffset, "Target offset cannot be negative.");

            WriteRangeInto(target, self, targetOffset);
            return arguments.Return(SurtrValue.Null);
        }

        /// <summary>Replaces this buffer's live prefix with a slice of another's.</summary>
        private static void CopyRangeInto(SurtrBytes self, SurtrBytes source, int sourceOffset, int count)
        {
            self.EnsureCapacity(count);
            Buffer.BlockCopy(source.Items, sourceOffset, self.Items, 0, count);
            self.Count = count;
        }

        /// <summary>Writes a source buffer's live bytes into a target at an offset, growing it to fit.</summary>
        private static void WriteRangeInto(SurtrBytes target, SurtrBytes source, int targetOffset)
        {
            long end = (long)targetOffset + source.Count;
            if (end > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(targetOffset), targetOffset, "The copy would exceed the maximum buffer size.");

            int endInt = (int)end;
            target.EnsureCapacity(endInt);
            Buffer.BlockCopy(source.Items, 0, target.Items, targetOffset, source.Count);
            if (endInt > target.Count)
                target.Count = endInt;
        }

        /// <summary>Backs <c>bytes.toString()</c>: uppercase hex, one pair per byte, no separator.</summary>
        private static int ToHexString(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);

            const string digits = "0123456789ABCDEF";
            var buffer = new char[self.Count * 2];
            var items = self.Items;

            for (int i = 0; i < self.Count; i++)
            {
                byte value = items[i];
                buffer[i * 2] = digits[value >> 4];
                buffer[i * 2 + 1] = digits[value & 0x0F];
            }

            return arguments.Return(arguments.Runtime.NewStringValue(new string(buffer)));
        }

        /// <summary>Backs <c>bytes.decodeUTF8()</c>: the buffer's bytes decoded as UTF-8 text.</summary>
        private static int DecodeUTF8(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            return arguments.Return(arguments.Runtime.NewStringValue(
                Encoding.UTF8.GetString(self.Items, 0, self.Count)));
        }

        /// <summary>Backs <c>bytes.equals(other)</c>: content equality, byte for byte.</summary>
        private static int ContentEquals(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            var other = arguments.GetUnchecked<SurtrBytes>(1);

            if (self.Count != other.Count)
                return arguments.Return(SurtrValue.CreateBool(false));

            return arguments.Return(SurtrValue.CreateBool(SequenceEqual(self.Items, other.Items, self.Count)));
        }

        private static int CompareTo(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrBytes>(0);
            var other = arguments.GetUnchecked<SurtrBytes>(1);

            int common = Math.Min(self.Count, other.Count);
            for (int i = 0; i < common; i++)
            {
                int difference = self.Items[i] - other.Items[i];
                if (difference != 0)
                    return arguments.Return(SurtrValue.CreateInt(difference < 0 ? -1 : 1));
            }

            if (self.Count == other.Count)
                return arguments.Return(SurtrValue.CreateInt(0));

            return arguments.Return(SurtrValue.CreateInt(self.Count < other.Count ? -1 : 1));
        }

        private static bool SequenceEqual(byte[] left, byte[] right, int length)
        {
            for (int i = 0; i < length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }

        private static int Empty(SurtrCallArguments arguments)
            => arguments.Return(arguments.Runtime.NewBytesValue(0));

        private static int WithCapacity(SurtrCallArguments arguments)
        {
            int capacity = arguments.GetInt(0);
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity cannot be negative.");

            return arguments.Return(arguments.Runtime.NewBytesValue(capacity));
        }

        private static int From(SurtrCallArguments arguments)
        {
            var source = arguments.GetUnchecked<SurtrArray>(0);
            var data = new byte[source.Length];

            for (int i = 0; i < source.Length; i++)
            {
                int value = source[i].AsInt;
                if ((uint)value > MaxByte)
                    throw new ArgumentOutOfRangeException(nameof(value), value, $"A byte must be in [{MinByte}, {MaxByte}], not {value}.");
                data[i] = (byte)value;
            }

            return arguments.Return(arguments.Runtime.NewBytesValue(data));
        }

        private static int FromSlice(SurtrCallArguments arguments)
        {
            var source = arguments.GetUnchecked<SurtrArray>(0);
            int offset = arguments.GetInt(1);
            int length = arguments.GetInt(2);

            if (offset < 0 || length < 0 || (long)offset + length > source.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset),
                    $"offset {offset} and length {length} are out of range for a {source.Length}-element array.");
            }

            var data = new byte[length];
            for (int i = 0; i < length; i++)
            {
                int value = source[offset + i].AsInt;
                if ((uint)value > MaxByte)
                    throw new ArgumentOutOfRangeException(nameof(value), value, $"A byte must be in [{MinByte}, {MaxByte}], not {value}.");
                data[i] = (byte)value;
            }

            return arguments.Return(arguments.Runtime.NewBytesValue(data));
        }

        private static int FromUTF8(SurtrCallArguments arguments)
        {
            string text = arguments.GetUnchecked<SurtrString>(0).Value;
            return arguments.Return(arguments.Runtime.NewBytesValue(Encoding.UTF8.GetBytes(text)));
        }

        private static int Repeat(SurtrCallArguments arguments)
        {
            ValidateByte(arguments, 0, "value");
            int count = arguments.GetInt(1);

            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, "Count cannot be negative.");

            var data = new byte[count];
            Array.Fill(data, (byte)arguments.GetInt(0));
            return arguments.Return(arguments.Runtime.NewBytesValue(data));
        }
    }
}