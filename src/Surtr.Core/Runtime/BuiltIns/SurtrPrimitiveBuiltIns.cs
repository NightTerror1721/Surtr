#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Globalization;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// The members of the four primitive built-ins - <c>int</c>, <c>float</c>, <c>bool</c> and
    /// <c>char</c> - and the host functions behind them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A primitive receiver can arrive either way. Most of the time it is the raw NaN-boxed value
    /// straight off the stack, since nothing had to allocate to call a method that only reads it;
    /// sometimes it is a <see cref="SurtrBoxed"/>, because the value had already been boxed for
    /// some other reason and there is no point unboxing it just to call through it. Every entry
    /// point here reads the receiver through <see cref="SurtrCallArguments.GetPrimitiveUnchecked"/>,
    /// which accepts both - the language says a boxed <c>5</c> and an unboxed <c>5</c> are the
    /// same value of the same class, so a method on that class has to be reachable from either
    /// representation.
    /// </para>
    /// <para>
    /// Text conversions are all <see cref="CultureInfo.InvariantCulture"/>. A script that prints a
    /// number must print it the same way on every machine that runs it; picking up the host's
    /// regional settings would make the same bytecode produce <c>1.5</c> or <c>1,5</c> depending
    /// on who launched the game.
    /// </para>
    /// </remarks>
    internal static unsafe class SurtrPrimitiveBuiltIns
    {
        #region Integer
        internal static void DeclareInteger(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference integer = SurtrClassReference.Integer;
            SurtrClassReference real = SurtrClassReference.Float;
            SurtrClassReference text = SurtrClassReference.String;

            builder.Method("toString", text, SurtrNativeEntryPoint.FromFunctionPointer(&IntToString), isPure: true, dispatch: SurtrMethodDispatch.Virtual, isOverride: true);
            builder.Method("toFloat", real, SurtrNativeEntryPoint.FromFunctionPointer(&IntToFloat), isPure: true);
            builder.Method("abs", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntAbs), isPure: true);
            builder.Method("sign", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntSign), isPure: true);

            builder.Method("min", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntMin), builder.Params(("a", integer), ("b", integer)), isStatic: true, isPure: true);
            builder.Method("max", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntMax), builder.Params(("a", integer), ("b", integer)), isStatic: true, isPure: true);
            builder.Method("clamp", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntClamp), builder.Params(("value", integer), ("low", integer), ("high", integer)), isStatic: true, isPure: true);
            builder.Method("parse", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntParse), builder.Params(("text", text)), isStatic: true, isPure: true);
            builder.Method("parseStrict", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntParseStrict), builder.Params(("text", text)), isStatic: true, isPure: true);
            builder.Method("parseStrict", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntParseStrictRadix), builder.Params(("text", text), ("radix", integer)), isStatic: true, isPure: true);
        }

        private static int IntToString(SurtrCallArguments arguments)
            => arguments.Return(arguments.Runtime.NewStringValue(arguments.GetPrimitiveUnchecked(0).AsInt.ToString(CultureInfo.InvariantCulture)));

        private static int IntToFloat(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateFloat(arguments.GetPrimitiveUnchecked(0).AsInt));

        private static int IntAbs(SurtrCallArguments arguments)
        {
            int value = arguments.GetPrimitiveUnchecked(0).AsInt;

            // Math.Abs(int.MinValue) throws; wrapping instead keeps this total, which matters
            // because there is no trap mechanism to hand an overflow to yet.
            return arguments.Return(SurtrValue.CreateInt(value < 0 ? unchecked(-value) : value));
        }

        private static int IntSign(SurtrCallArguments arguments)
        {
            int value = arguments.GetPrimitiveUnchecked(0).AsInt;
            return arguments.Return(SurtrValue.CreateInt(value > 0 ? 1 : value < 0 ? -1 : 0));
        }

        private static int IntMin(SurtrCallArguments arguments)
        {
            int a = arguments.GetInt(0);
            int b = arguments.GetInt(1);
            return arguments.Return(SurtrValue.CreateInt(a < b ? a : b));
        }

        private static int IntMax(SurtrCallArguments arguments)
        {
            int a = arguments.GetInt(0);
            int b = arguments.GetInt(1);
            return arguments.Return(SurtrValue.CreateInt(a > b ? a : b));
        }

        private static int IntClamp(SurtrCallArguments arguments)
        {
            int value = arguments.GetInt(0);
            int low = arguments.GetInt(1);
            int high = arguments.GetInt(2);
            return arguments.Return(SurtrValue.CreateInt(value < low ? low : value > high ? high : value));
        }

        // Returns 0 for unparseable text rather than trapping, because the instruction set has no
        // trap to raise yet. Revisit alongside the rest of the undefined trap behaviour.
        private static int IntParse(SurtrCallArguments arguments)
        {
            string text = arguments.GetUnchecked<SurtrString>(0).Value;
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed);
            return arguments.Return(SurtrValue.CreateInt(parsed));
        }

        /// <summary>The throwing counterpart to <see cref="IntParse"/>, backing <c>int(aString)</c>.</summary>
        private static int IntParseStrict(SurtrCallArguments arguments)
        {
            string text = arguments.GetUnchecked<SurtrString>(0).Value;

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                throw new System.FormatException($"'{text}' is not a valid int.");

            return arguments.Return(SurtrValue.CreateInt(parsed));
        }

        /// <summary>
        /// Backs <c>int(aString, radix)</c>. Written by hand rather than through
        /// <see cref="Convert.ToInt32(string, int)"/>, which only accepts bases 2, 8, 10 and 16 -
        /// this accepts any base in [2, 36], the range every digit/letter alphabet can name.
        /// </summary>
        private static int IntParseStrictRadix(SurtrCallArguments arguments)
        {
            string text = arguments.GetUnchecked<SurtrString>(0).Value;
            int radix = arguments.GetInt(1);

            if (radix < 2 || radix > 36)
                throw new System.ArgumentException($"radix must be between 2 and 36, not {radix}.", "radix");

            int index = 0;
            bool negative = false;
            if (index < text.Length && (text[index] == '+' || text[index] == '-'))
            {
                negative = text[index] == '-';
                index++;
            }

            if (index >= text.Length)
                throw new System.FormatException($"'{text}' is not a valid base-{radix} int.");

            // The largest magnitude a base-radix accumulator can reach before it has definitely
            // overflowed int - checked every digit, so the multiply below never runs on a value
            // already past it, which is what keeps this total against arbitrarily long input.
            long limit = negative ? 2147483648L : 2147483647L;
            long accumulated = 0;

            for (; index < text.Length; index++)
            {
                int digit = DigitValue(text[index]);
                if (digit < 0 || digit >= radix)
                    throw new System.FormatException($"'{text}' is not a valid base-{radix} int.");

                accumulated = accumulated * radix + digit;
                if (accumulated > limit)
                    throw new System.FormatException($"'{text}' is out of range for int.");
            }

            return arguments.Return(SurtrValue.CreateInt((int)(negative ? -accumulated : accumulated)));
        }

        /// <summary>A digit's value in any base up to 36, or -1 if it names none. Letters are case-insensitive.</summary>
        private static int DigitValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'z') return c - 'a' + 10;
            if (c >= 'A' && c <= 'Z') return c - 'A' + 10;
            return -1;
        }
        #endregion

        #region Float
        internal static void DeclareFloat(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference integer = SurtrClassReference.Integer;
            SurtrClassReference real = SurtrClassReference.Float;
            SurtrClassReference boolean = SurtrClassReference.Boolean;
            SurtrClassReference text = SurtrClassReference.String;

            builder.Method("toString", text, SurtrNativeEntryPoint.FromFunctionPointer(&FloatToString), isPure: true, dispatch: SurtrMethodDispatch.Virtual, isOverride: true);
            builder.Method("toInt", integer, SurtrNativeEntryPoint.FromFunctionPointer(&FloatToInt), isPure: true);
            builder.Method("abs", real, SurtrNativeEntryPoint.FromFunctionPointer(&FloatAbs), isPure: true);
            builder.Method("sqrt", real, SurtrNativeEntryPoint.FromFunctionPointer(&FloatSqrt), isPure: true);
            builder.Method("floor", integer, SurtrNativeEntryPoint.FromFunctionPointer(&FloatFloor), isPure: true);
            builder.Method("ceil", integer, SurtrNativeEntryPoint.FromFunctionPointer(&FloatCeil), isPure: true);
            builder.Method("round", integer, SurtrNativeEntryPoint.FromFunctionPointer(&FloatRound), isPure: true);
            builder.Method("isNaN", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&FloatIsNaN), isPure: true);
            builder.Method("isInfinite", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&FloatIsInfinite), isPure: true);

            builder.Method("min", real, SurtrNativeEntryPoint.FromFunctionPointer(&FloatMin), builder.Params(("a", real), ("b", real)), isStatic: true, isPure: true);
            builder.Method("max", real, SurtrNativeEntryPoint.FromFunctionPointer(&FloatMax), builder.Params(("a", real), ("b", real)), isStatic: true, isPure: true);
            builder.Method("pow", real, SurtrNativeEntryPoint.FromFunctionPointer(&FloatPow), builder.Params(("value", real), ("exponent", real)), isStatic: true, isPure: true);
            builder.Method("parse", real, SurtrNativeEntryPoint.FromFunctionPointer(&FloatParse), builder.Params(("text", text)), isStatic: true, isPure: true);
            builder.Method("parseStrict", real, SurtrNativeEntryPoint.FromFunctionPointer(&FloatParseStrict), builder.Params(("text", text)), isStatic: true, isPure: true);
        }

        private static int FloatToString(SurtrCallArguments arguments)
            // "R" round-trips: the text parses back to the identical double, which is what a
            // script serialising a value needs.
            => arguments.Return(arguments.Runtime.NewStringValue(arguments.GetPrimitiveUnchecked(0).AsFloat.ToString("R", CultureInfo.InvariantCulture)));

        private static int FloatToInt(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt((SurtrInt)arguments.GetPrimitiveUnchecked(0).AsFloat));

        private static int FloatAbs(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateFloat(Math.Abs(arguments.GetPrimitiveUnchecked(0).AsFloat)));

        private static int FloatSqrt(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateFloat(Math.Sqrt(arguments.GetPrimitiveUnchecked(0).AsFloat)));

        private static int FloatFloor(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt((SurtrInt)Math.Floor(arguments.GetPrimitiveUnchecked(0).AsFloat)));

        private static int FloatCeil(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt((SurtrInt)Math.Ceiling(arguments.GetPrimitiveUnchecked(0).AsFloat)));

        private static int FloatRound(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt((SurtrInt)Math.Round(arguments.GetPrimitiveUnchecked(0).AsFloat, MidpointRounding.AwayFromZero)));

        private static int FloatIsNaN(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(double.IsNaN(arguments.GetPrimitiveUnchecked(0).AsFloat)));

        private static int FloatIsInfinite(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(double.IsInfinity(arguments.GetPrimitiveUnchecked(0).AsFloat)));

        private static int FloatMin(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateFloat(Math.Min(arguments.GetFloat(0), arguments.GetFloat(1))));

        private static int FloatMax(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateFloat(Math.Max(arguments.GetFloat(0), arguments.GetFloat(1))));

        private static int FloatPow(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateFloat(Math.Pow(arguments.GetFloat(0), arguments.GetFloat(1))));

        // NaN rather than 0 for unparseable text: it is the float world's own "not a number", and
        // it propagates instead of silently reading as a legitimate value.
        private static int FloatParse(SurtrCallArguments arguments)
        {
            string text = arguments.GetUnchecked<SurtrString>(0).Value;

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                parsed = double.NaN;

            return arguments.Return(SurtrValue.CreateFloat(parsed));
        }

        /// <summary>The throwing counterpart to <see cref="FloatParse"/>, backing <c>float(aString)</c>.</summary>
        private static int FloatParseStrict(SurtrCallArguments arguments)
        {
            string text = arguments.GetUnchecked<SurtrString>(0).Value;

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                throw new System.FormatException($"'{text}' is not a valid float.");

            return arguments.Return(SurtrValue.CreateFloat(parsed));
        }
        #endregion

        #region Boolean
        internal static void DeclareBoolean(SurtrBuiltInTypeBuilder builder)
        {
            builder.Method("toString", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&BoolToString), isPure: true, dispatch: SurtrMethodDispatch.Virtual, isOverride: true);
            builder.Method("toInt", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&BoolToInt), isPure: true);
            builder.Method("parseStrict", SurtrClassReference.Boolean, SurtrNativeEntryPoint.FromFunctionPointer(&BoolParseStrict), builder.Params(("text", SurtrClassReference.String)), isStatic: true, isPure: true);
        }

        private static int BoolToString(SurtrCallArguments arguments)
            // Interned, not freshly allocated: there are exactly two of these strings and a
            // program is going to ask for them constantly.
            => arguments.Return(SurtrValue.CreateReference(
                arguments.Runtime.InternString(arguments.GetPrimitiveUnchecked(0).AsBool ? "true" : "false").GetSurtrReference()));

        private static int BoolToInt(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(arguments.GetPrimitiveUnchecked(0).AsBool ? 1 : 0));

        /// <summary>Backs <c>bool(aString)</c>. Accepts <c>"true"</c>/<c>"1"</c> and <c>"false"</c>/<c>"0"</c>, case-insensitively for the words.</summary>
        private static int BoolParseStrict(SurtrCallArguments arguments)
        {
            string text = arguments.GetUnchecked<SurtrString>(0).Value;

            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1")
                return arguments.Return(SurtrValue.CreateBool(true));

            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0")
                return arguments.Return(SurtrValue.CreateBool(false));

            throw new System.FormatException($"'{text}' is neither \"true\"/\"1\" nor \"false\"/\"0\".");
        }
        #endregion

        #region Character
        internal static void DeclareCharacter(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference boolean = SurtrClassReference.Boolean;
            SurtrClassReference character = SurtrClassReference.Character;
            SurtrClassReference text = SurtrClassReference.String;

            builder.Method("toString", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&CharToString), isPure: true, dispatch: SurtrMethodDispatch.Virtual, isOverride: true);
            builder.Method("parseStrict", character, SurtrNativeEntryPoint.FromFunctionPointer(&CharParseStrict), builder.Params(("text", text)), isStatic: true, isPure: true);
            builder.Method("toInt", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&CharToInt), isPure: true);
            builder.Method("toUpper", character, SurtrNativeEntryPoint.FromFunctionPointer(&CharToUpper), isPure: true);
            builder.Method("toLower", character, SurtrNativeEntryPoint.FromFunctionPointer(&CharToLower), isPure: true);
            builder.Method("isDigit", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&CharIsDigit), isPure: true);
            builder.Method("isLetter", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&CharIsLetter), isPure: true);
            builder.Method("isLetterOrDigit", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&CharIsLetterOrDigit), isPure: true);
            builder.Method("isWhitespace", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&CharIsWhitespace), isPure: true);
            builder.Method("isUpper", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&CharIsUpper), isPure: true);
            builder.Method("isLower", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&CharIsLower), isPure: true);
        }

        private static int CharToString(SurtrCallArguments arguments)
            => arguments.Return(arguments.Runtime.NewStringValue(arguments.GetPrimitiveUnchecked(0).AsChar.ToString(CultureInfo.InvariantCulture)));

        private static int CharToInt(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(arguments.GetPrimitiveUnchecked(0).AsChar));

        private static int CharToUpper(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateChar(char.ToUpperInvariant(arguments.GetPrimitiveUnchecked(0).AsChar)));

        private static int CharToLower(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateChar(char.ToLowerInvariant(arguments.GetPrimitiveUnchecked(0).AsChar)));

        private static int CharIsDigit(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(char.IsDigit(arguments.GetPrimitiveUnchecked(0).AsChar)));

        private static int CharIsLetter(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(char.IsLetter(arguments.GetPrimitiveUnchecked(0).AsChar)));

        private static int CharIsLetterOrDigit(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(char.IsLetterOrDigit(arguments.GetPrimitiveUnchecked(0).AsChar)));

        private static int CharIsWhitespace(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(char.IsWhiteSpace(arguments.GetPrimitiveUnchecked(0).AsChar)));

        private static int CharIsUpper(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(char.IsUpper(arguments.GetPrimitiveUnchecked(0).AsChar)));

        private static int CharIsLower(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(char.IsLower(arguments.GetPrimitiveUnchecked(0).AsChar)));

        /// <summary>Backs <c>char(aString)</c> â€” the string's first character. <c>FormatException</c> on an empty string, never a validation of the character itself.</summary>
        private static int CharParseStrict(SurtrCallArguments arguments)
        {
            string text = arguments.GetUnchecked<SurtrString>(0).Value;

            if (text.Length == 0)
                throw new System.FormatException("Cannot take the first character of an empty string.");

            return arguments.Return(SurtrValue.CreateChar(text[0]));
        }
        #endregion
    }
}
