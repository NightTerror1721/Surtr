#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Globalization;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// The <c>string</c> built-in's members, and the host functions behind them.
    /// </summary>
    /// <remarks>
    /// Every entry point here has the one shape <see cref="SurtrNativeFunction"/> defines, so the
    /// interpreter reaches all of them through the same indirect call. <c>arguments[0]</c> is the
    /// receiver - the layout <c>InvokeNative</c> leaves on the stack - and the declared parameters
    /// follow it. Everything here reads through <see cref="SurtrCallArguments"/>'s
    /// <c>*Unchecked</c> tier: the interpreter only ever reaches one of these after the compiler
    /// matched the declared Surtr signature against the call site, so the index and the argument
    /// types are already known good.
    /// </remarks>
    internal static unsafe class SurtrStringBuiltIn
    {
        internal static void Declare(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference text = SurtrClassReference.String;
            SurtrClassReference integer = SurtrClassReference.Integer;
            SurtrClassReference boolean = SurtrClassReference.Boolean;
            SurtrClassReference character = SurtrClassReference.Character;
            SurtrClassReference textArray = SurtrClassReference.Array(text);

            builder.Property("length", integer, SurtrNativeEntryPoint.FromFunctionPointer(&GetLength));
            builder.Property("isEmpty", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&GetIsEmpty));

            builder.Method("charAt", character, SurtrNativeEntryPoint.FromFunctionPointer(&CharAt), builder.Params(("index", integer)));
            builder.Method("indexOf", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IndexOf), builder.Params(("value", text)));
            builder.Method("lastIndexOf", integer, SurtrNativeEntryPoint.FromFunctionPointer(&LastIndexOf), builder.Params(("value", text)));
            builder.Method("contains", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&Contains), builder.Params(("value", text)));
            builder.Method("startsWith", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&StartsWith), builder.Params(("value", text)));
            builder.Method("endsWith", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&EndsWith), builder.Params(("value", text)));
            builder.Method("substring", text, SurtrNativeEntryPoint.FromFunctionPointer(&Substring), builder.Params(("start", integer), ("length", integer)));
            builder.Method("concat", text, SurtrNativeEntryPoint.FromFunctionPointer(&Concat), builder.Params(("other", text)));
            builder.Method("replace", text, SurtrNativeEntryPoint.FromFunctionPointer(&Replace), builder.Params(("target", text), ("replacement", text)));
            builder.Method("repeat", text, SurtrNativeEntryPoint.FromFunctionPointer(&Repeat), builder.Params(("count", integer)));
            builder.Method("split", textArray, SurtrNativeEntryPoint.FromFunctionPointer(&Split), builder.Params(("separator", text)));
            builder.Method("toUpper", text, SurtrNativeEntryPoint.FromFunctionPointer(&ToUpper));
            builder.Method("toLower", text, SurtrNativeEntryPoint.FromFunctionPointer(&ToLower));
            builder.Method("trim", text, SurtrNativeEntryPoint.FromFunctionPointer(&Trim));
            builder.Method("reverse", text, SurtrNativeEntryPoint.FromFunctionPointer(&Reverse));
            builder.Method("equals", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&EqualsText), builder.Params(("other", text)));
            builder.Method("compareTo", integer, SurtrNativeEntryPoint.FromFunctionPointer(&CompareTo), builder.Params(("other", text)));
            builder.Method("toString", text, SurtrNativeEntryPoint.FromFunctionPointer(&ToStringSelf));

            builder.Method("fromChar", text, SurtrNativeEntryPoint.FromFunctionPointer(&FromChar), builder.Params(("value", character)), isStatic: true);
            builder.Method("join", text, SurtrNativeEntryPoint.FromFunctionPointer(&Join), builder.Params(("separator", text), ("parts", textArray)), isStatic: true);
        }

        private static SurtrValue GetLength(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetUnchecked<SurtrString>(0).Value.Length);

        private static SurtrValue GetIsEmpty(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(arguments.GetUnchecked<SurtrString>(0).Value.Length == 0);

        private static SurtrValue CharAt(SurtrCallArguments arguments)
        {
            string self = arguments.GetUnchecked<SurtrString>(0).Value;
            int index = arguments.GetInt(1);

            // Out-of-range indexing is one of the traps the instruction set still leaves
            // undefined; until that decision lands, surfacing it as a CLR exception is at least
            // loud and debuggable, rather than reading past the string.
            if ((uint)index >= (uint)self.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, "String index is out of range.");

            return SurtrValue.CreateChar(self[index]);
        }

        private static SurtrValue IndexOf(SurtrCallArguments arguments)
        {
            string self = arguments.GetUnchecked<SurtrString>(0).Value;
            string value = arguments.GetUnchecked<SurtrString>(1).Value;
            return SurtrValue.CreateInt(self.IndexOf(value, StringComparison.Ordinal));
        }

        private static SurtrValue LastIndexOf(SurtrCallArguments arguments)
        {
            string self = arguments.GetUnchecked<SurtrString>(0).Value;
            string value = arguments.GetUnchecked<SurtrString>(1).Value;
            return SurtrValue.CreateInt(self.LastIndexOf(value, StringComparison.Ordinal));
        }

        private static SurtrValue Contains(SurtrCallArguments arguments)
        {
            string self = arguments.GetUnchecked<SurtrString>(0).Value;
            string value = arguments.GetUnchecked<SurtrString>(1).Value;
            return SurtrValue.CreateBool(self.IndexOf(value, StringComparison.Ordinal) >= 0);
        }

        private static SurtrValue StartsWith(SurtrCallArguments arguments)
        {
            string self = arguments.GetUnchecked<SurtrString>(0).Value;
            string value = arguments.GetUnchecked<SurtrString>(1).Value;
            return SurtrValue.CreateBool(self.StartsWith(value, StringComparison.Ordinal));
        }

        private static SurtrValue EndsWith(SurtrCallArguments arguments)
        {
            string self = arguments.GetUnchecked<SurtrString>(0).Value;
            string value = arguments.GetUnchecked<SurtrString>(1).Value;
            return SurtrValue.CreateBool(self.EndsWith(value, StringComparison.Ordinal));
        }

        private static SurtrValue Substring(SurtrCallArguments arguments)
        {
            string self = arguments.GetUnchecked<SurtrString>(0).Value;
            int start = arguments.GetInt(1);
            int length = arguments.GetInt(2);

            if (start < 0 || length < 0 || start + length > self.Length)
                throw new ArgumentOutOfRangeException(nameof(start), $"Substring [{start}, {start + length}) is out of range for a string of length {self.Length}.");

            return arguments.Runtime.NewStringValue(self.Substring(start, length));
        }

        private static SurtrValue Concat(SurtrCallArguments arguments)
        {
            string self = arguments.GetUnchecked<SurtrString>(0).Value;
            string other = arguments.GetUnchecked<SurtrString>(1).Value;
            return arguments.Runtime.NewStringValue(string.Concat(self, other));
        }

        private static SurtrValue Replace(SurtrCallArguments arguments)
        {
            string self = arguments.GetUnchecked<SurtrString>(0).Value;
            string target = arguments.GetUnchecked<SurtrString>(1).Value;
            string replacement = arguments.GetUnchecked<SurtrString>(2).Value;

            if (target.Length == 0)
                return arguments.Runtime.NewStringValue(self);

            return arguments.Runtime.NewStringValue(self.Replace(target, replacement));
        }

        private static SurtrValue Repeat(SurtrCallArguments arguments)
        {
            string self = arguments.GetUnchecked<SurtrString>(0).Value;
            int count = arguments.GetInt(1);

            if (count <= 0 || self.Length == 0)
                return arguments.Runtime.NewStringValue(string.Empty);

            var builder = new System.Text.StringBuilder(self.Length * count);
            for (int i = 0; i < count; i++)
                builder.Append(self);

            return arguments.Runtime.NewStringValue(builder.ToString());
        }

        private static SurtrValue Split(SurtrCallArguments arguments)
        {
            var runtime = arguments.Runtime;
            string self = arguments.GetUnchecked<SurtrString>(0).Value;
            string separator = arguments.GetUnchecked<SurtrString>(1).Value;

            string[] parts = separator.Length == 0
                ? new[] { self }
                : self.Split(new[] { separator }, StringSplitOptions.None);

            var result = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.String), parts.Length);
            for (int i = 0; i < parts.Length; i++)
                result.Add(runtime.NewStringValue(parts[i]));

            return runtime.ValueOf(result);
        }

        private static SurtrValue ToUpper(SurtrCallArguments arguments)
            // Invariant, not current-culture: a script's output must not change with the machine's
            // regional settings, and the Turkish dotless i is the classic way that goes wrong.
            => arguments.Runtime.NewStringValue(arguments.GetUnchecked<SurtrString>(0).Value.ToUpperInvariant());

        private static SurtrValue ToLower(SurtrCallArguments arguments)
            => arguments.Runtime.NewStringValue(arguments.GetUnchecked<SurtrString>(0).Value.ToLowerInvariant());

        private static SurtrValue Trim(SurtrCallArguments arguments)
            => arguments.Runtime.NewStringValue(arguments.GetUnchecked<SurtrString>(0).Value.Trim());

        private static SurtrValue Reverse(SurtrCallArguments arguments)
        {
            string self = arguments.GetUnchecked<SurtrString>(0).Value;

            var characters = self.ToCharArray();
            Array.Reverse(characters);
            return arguments.Runtime.NewStringValue(new string(characters));
        }

        private static SurtrValue EqualsText(SurtrCallArguments arguments)
        {
            var self = arguments.GetUnchecked<SurtrString>(0);
            var other = arguments.GetUnchecked<SurtrString>(1);
            return SurtrValue.CreateBool(self.TextEquals(other));
        }

        private static SurtrValue CompareTo(SurtrCallArguments arguments)
        {
            string self = arguments.GetUnchecked<SurtrString>(0).Value;
            string other = arguments.GetUnchecked<SurtrString>(1).Value;
            return SurtrValue.CreateInt(string.CompareOrdinal(self, other));
        }

        // A string is already its own text, so this hands the same object back rather than
        // allocating a copy - toString() on a string should cost nothing.
        private static SurtrValue ToStringSelf(SurtrCallArguments arguments)
            => arguments.GetValueUnchecked(0);

        private static SurtrValue FromChar(SurtrCallArguments arguments)
            => arguments.Runtime.NewStringValue(arguments.GetChar(0).ToString(CultureInfo.InvariantCulture));

        private static SurtrValue Join(SurtrCallArguments arguments)
        {
            var runtime = arguments.Runtime;
            string separator = arguments.GetUnchecked<SurtrString>(0).Value;
            var parts = arguments.GetUnchecked<SurtrArray>(1);

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                    builder.Append(separator);

                builder.Append(runtime.Dereference<SurtrString>(parts[i].Raw).Value);
            }

            return runtime.NewStringValue(builder.ToString());
        }
    }
}
