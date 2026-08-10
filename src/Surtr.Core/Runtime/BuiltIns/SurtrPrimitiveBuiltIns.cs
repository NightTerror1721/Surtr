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

            builder.Method("toString", text, SurtrNativeEntryPoint.FromFunctionPointer(&IntToString));
            builder.Method("toFloat", real, SurtrNativeEntryPoint.FromFunctionPointer(&IntToFloat));
            builder.Method("abs", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntAbs));
            builder.Method("sign", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntSign));

            builder.Method("min", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntMin), builder.Params(("a", integer), ("b", integer)), isStatic: true);
            builder.Method("max", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntMax), builder.Params(("a", integer), ("b", integer)), isStatic: true);
            builder.Method("clamp", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntClamp), builder.Params(("value", integer), ("low", integer), ("high", integer)), isStatic: true);
            builder.Method("parse", integer, SurtrNativeEntryPoint.FromFunctionPointer(&IntParse), builder.Params(("text", text)), isStatic: true);
        }

        private static SurtrValue IntToString(SurtrCallArguments arguments)
            => arguments.Runtime.NewStringValue(arguments.GetPrimitiveUnchecked(0).AsInt.ToString(CultureInfo.InvariantCulture));

        private static SurtrValue IntToFloat(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(arguments.GetPrimitiveUnchecked(0).AsInt);

        private static SurtrValue IntAbs(SurtrCallArguments arguments)
        {
            int value = arguments.GetPrimitiveUnchecked(0).AsInt;

            // Math.Abs(int.MinValue) throws; wrapping instead keeps this total, which matters
            // because there is no trap mechanism to hand an overflow to yet.
            return SurtrValue.CreateInt(value < 0 ? unchecked(-value) : value);
        }

        private static SurtrValue IntSign(SurtrCallArguments arguments)
        {
            int value = arguments.GetPrimitiveUnchecked(0).AsInt;
            return SurtrValue.CreateInt(value > 0 ? 1 : value < 0 ? -1 : 0);
        }

        private static SurtrValue IntMin(SurtrCallArguments arguments)
        {
            int a = arguments.GetInt(0);
            int b = arguments.GetInt(1);
            return SurtrValue.CreateInt(a < b ? a : b);
        }

        private static SurtrValue IntMax(SurtrCallArguments arguments)
        {
            int a = arguments.GetInt(0);
            int b = arguments.GetInt(1);
            return SurtrValue.CreateInt(a > b ? a : b);
        }

        private static SurtrValue IntClamp(SurtrCallArguments arguments)
        {
            int value = arguments.GetInt(0);
            int low = arguments.GetInt(1);
            int high = arguments.GetInt(2);
            return SurtrValue.CreateInt(value < low ? low : value > high ? high : value);
        }

        // Returns 0 for unparseable text rather than trapping, because the instruction set has no
        // trap to raise yet. Revisit alongside the rest of the undefined trap behaviour.
        private static SurtrValue IntParse(SurtrCallArguments arguments)
        {
            string text = arguments.GetUnchecked<SurtrString>(0).Value;
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed);
            return SurtrValue.CreateInt(parsed);
        }
        #endregion

        #region Float
        internal static void DeclareFloat(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference integer = SurtrClassReference.Integer;
            SurtrClassReference real = SurtrClassReference.Float;
            SurtrClassReference boolean = SurtrClassReference.Boolean;
            SurtrClassReference text = SurtrClassReference.String;

            builder.Method("toString", text, SurtrNativeEntryPoint.FromFunctionPointer(&FloatToString));
            builder.Method("toInt", integer, SurtrNativeEntryPoint.FromFunctionPointer(&FloatToInt));
            builder.Method("abs", real, SurtrNativeEntryPoint.FromFunctionPointer(&FloatAbs));
            builder.Method("sqrt", real, SurtrNativeEntryPoint.FromFunctionPointer(&FloatSqrt));
            builder.Method("floor", integer, SurtrNativeEntryPoint.FromFunctionPointer(&FloatFloor));
            builder.Method("ceil", integer, SurtrNativeEntryPoint.FromFunctionPointer(&FloatCeil));
            builder.Method("round", integer, SurtrNativeEntryPoint.FromFunctionPointer(&FloatRound));
            builder.Method("isNaN", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&FloatIsNaN));
            builder.Method("isInfinite", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&FloatIsInfinite));

            builder.Method("min", real, SurtrNativeEntryPoint.FromFunctionPointer(&FloatMin), builder.Params(("a", real), ("b", real)), isStatic: true);
            builder.Method("max", real, SurtrNativeEntryPoint.FromFunctionPointer(&FloatMax), builder.Params(("a", real), ("b", real)), isStatic: true);
            builder.Method("pow", real, SurtrNativeEntryPoint.FromFunctionPointer(&FloatPow), builder.Params(("value", real), ("exponent", real)), isStatic: true);
            builder.Method("parse", real, SurtrNativeEntryPoint.FromFunctionPointer(&FloatParse), builder.Params(("text", text)), isStatic: true);
        }

        private static SurtrValue FloatToString(SurtrCallArguments arguments)
            // "R" round-trips: the text parses back to the identical double, which is what a
            // script serialising a value needs.
            => arguments.Runtime.NewStringValue(arguments.GetPrimitiveUnchecked(0).AsFloat.ToString("R", CultureInfo.InvariantCulture));

        private static SurtrValue FloatToInt(SurtrCallArguments arguments)
            => SurtrValue.CreateInt((SurtrInt)arguments.GetPrimitiveUnchecked(0).AsFloat);

        private static SurtrValue FloatAbs(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Abs(arguments.GetPrimitiveUnchecked(0).AsFloat));

        private static SurtrValue FloatSqrt(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Sqrt(arguments.GetPrimitiveUnchecked(0).AsFloat));

        private static SurtrValue FloatFloor(SurtrCallArguments arguments)
            => SurtrValue.CreateInt((SurtrInt)Math.Floor(arguments.GetPrimitiveUnchecked(0).AsFloat));

        private static SurtrValue FloatCeil(SurtrCallArguments arguments)
            => SurtrValue.CreateInt((SurtrInt)Math.Ceiling(arguments.GetPrimitiveUnchecked(0).AsFloat));

        private static SurtrValue FloatRound(SurtrCallArguments arguments)
            => SurtrValue.CreateInt((SurtrInt)Math.Round(arguments.GetPrimitiveUnchecked(0).AsFloat, MidpointRounding.AwayFromZero));

        private static SurtrValue FloatIsNaN(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(double.IsNaN(arguments.GetPrimitiveUnchecked(0).AsFloat));

        private static SurtrValue FloatIsInfinite(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(double.IsInfinity(arguments.GetPrimitiveUnchecked(0).AsFloat));

        private static SurtrValue FloatMin(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Min(arguments.GetFloat(0), arguments.GetFloat(1)));

        private static SurtrValue FloatMax(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Max(arguments.GetFloat(0), arguments.GetFloat(1)));

        private static SurtrValue FloatPow(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Pow(arguments.GetFloat(0), arguments.GetFloat(1)));

        // NaN rather than 0 for unparseable text: it is the float world's own "not a number", and
        // it propagates instead of silently reading as a legitimate value.
        private static SurtrValue FloatParse(SurtrCallArguments arguments)
        {
            string text = arguments.GetUnchecked<SurtrString>(0).Value;

            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                parsed = double.NaN;

            return SurtrValue.CreateFloat(parsed);
        }
        #endregion

        #region Boolean
        internal static void DeclareBoolean(SurtrBuiltInTypeBuilder builder)
        {
            builder.Method("toString", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&BoolToString));
            builder.Method("toInt", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&BoolToInt));
        }

        private static SurtrValue BoolToString(SurtrCallArguments arguments)
            // Interned, not freshly allocated: there are exactly two of these strings and a
            // program is going to ask for them constantly.
            => SurtrValue.CreateReference(
                arguments.Runtime.InternString(arguments.GetPrimitiveUnchecked(0).AsBool ? "true" : "false").GetSurtrReference());

        private static SurtrValue BoolToInt(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetPrimitiveUnchecked(0).AsBool ? 1 : 0);
        #endregion

        #region Character
        internal static void DeclareCharacter(SurtrBuiltInTypeBuilder builder)
        {
            SurtrClassReference boolean = SurtrClassReference.Boolean;
            SurtrClassReference character = SurtrClassReference.Character;

            builder.Method("toString", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&CharToString));
            builder.Method("toInt", SurtrClassReference.Integer, SurtrNativeEntryPoint.FromFunctionPointer(&CharToInt));
            builder.Method("toUpper", character, SurtrNativeEntryPoint.FromFunctionPointer(&CharToUpper));
            builder.Method("toLower", character, SurtrNativeEntryPoint.FromFunctionPointer(&CharToLower));
            builder.Method("isDigit", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&CharIsDigit));
            builder.Method("isLetter", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&CharIsLetter));
            builder.Method("isLetterOrDigit", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&CharIsLetterOrDigit));
            builder.Method("isWhitespace", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&CharIsWhitespace));
            builder.Method("isUpper", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&CharIsUpper));
            builder.Method("isLower", boolean, SurtrNativeEntryPoint.FromFunctionPointer(&CharIsLower));
        }

        private static SurtrValue CharToString(SurtrCallArguments arguments)
            => arguments.Runtime.NewStringValue(arguments.GetPrimitiveUnchecked(0).AsChar.ToString(CultureInfo.InvariantCulture));

        private static SurtrValue CharToInt(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetPrimitiveUnchecked(0).AsChar);

        private static SurtrValue CharToUpper(SurtrCallArguments arguments)
            => SurtrValue.CreateChar(char.ToUpperInvariant(arguments.GetPrimitiveUnchecked(0).AsChar));

        private static SurtrValue CharToLower(SurtrCallArguments arguments)
            => SurtrValue.CreateChar(char.ToLowerInvariant(arguments.GetPrimitiveUnchecked(0).AsChar));

        private static SurtrValue CharIsDigit(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(char.IsDigit(arguments.GetPrimitiveUnchecked(0).AsChar));

        private static SurtrValue CharIsLetter(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(char.IsLetter(arguments.GetPrimitiveUnchecked(0).AsChar));

        private static SurtrValue CharIsLetterOrDigit(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(char.IsLetterOrDigit(arguments.GetPrimitiveUnchecked(0).AsChar));

        private static SurtrValue CharIsWhitespace(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(char.IsWhiteSpace(arguments.GetPrimitiveUnchecked(0).AsChar));

        private static SurtrValue CharIsUpper(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(char.IsUpper(arguments.GetPrimitiveUnchecked(0).AsChar));

        private static SurtrValue CharIsLower(SurtrCallArguments arguments)
            => SurtrValue.CreateBool(char.IsLower(arguments.GetPrimitiveUnchecked(0).AsChar));
        #endregion
    }
}
