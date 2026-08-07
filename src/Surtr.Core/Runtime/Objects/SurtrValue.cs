#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// A single NaN-boxed Surtr VM value: an int, float, bool, char, or entity reference
    /// packed into 8 bytes. The top 16 bits of <see cref="Raw"/> hold a type tag (or are part
    /// of a NaN float payload), and the low bits hold the payload.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public readonly struct SurtrValue
    {
        internal const SurtrRawValue TagMask        = 0xFFFF000000000000;
        internal const SurtrRawValue PayloadMask    = 0x0000FFFFFFFFFFFF;

        internal const SurtrRawValue TagMaskInt         = 0xFFF1000000000000;
        internal const SurtrRawValue TagMaskFloat       = 0xFFF2000000000000;
        internal const SurtrRawValue TagMaskBool        = 0xFFF3000000000000;
        internal const SurtrRawValue TagMaskChar        = 0xFFF4000000000000;
        internal const SurtrRawValue TagMaskReference    = 0xFFF5000000000000;

        internal const SurtrRawTagValue TagInt = 0xFFF1;
        internal const SurtrRawTagValue TagFloat = 0xFFF2;
        internal const SurtrRawTagValue TagBool = 0xFFF3;
        internal const SurtrRawTagValue TagChar = 0xFFF4;
        internal const SurtrRawTagValue TagReference = 0xFFF5;

        /// <summary>The reserved reference id representing "no entity" (a null reference).</summary>
        public const SurtrRef NullRef = 0;


        [FieldOffset(0)] internal readonly SurtrRawValue Raw;

        /// <summary>The value reinterpreted as a raw <see cref="SurtrFloat"/> bit pattern.</summary>
        [FieldOffset(0)] public readonly SurtrFloat AsFloat;


        #region Constructors
        internal SurtrValue(SurtrRawTagValue tag, SurtrRawPayloadValue payload)
        {
            AsFloat = 0;
            Raw = ((SurtrRawValue)tag << 48) | payload;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SurtrValue(SurtrRawValue raw)
        {
            AsFloat = 0.0;
            Raw = raw;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SurtrValue(SurtrFloat rawFloat)
        {
            AsFloat = rawFloat;
            Raw = 0;
        }

        /// <summary>Creates a null reference value, equivalent to <see cref="Null"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrValue()
        {
            AsFloat = 0;
            Raw = TagMaskReference | NullRef;
        }
        #endregion

        #region Type Casting
        /// <summary>The value reinterpreted as a <see cref="SurtrInt"/>. Only meaningful when <see cref="IsInt"/> is true.</summary>
        public SurtrInt AsInt
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (SurtrInt)Raw;
        }

        /// <summary>The value reinterpreted as a <see cref="SurtrBool"/>. Only meaningful when <see cref="IsBool"/> is true.</summary>
        public SurtrBool AsBool
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (SurtrInt)Raw != 0;
        }

        /// <summary>The value reinterpreted as a <see cref="SurtrChar"/>. Only meaningful when <see cref="IsChar"/> is true.</summary>
        public SurtrChar AsChar
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (SurtrChar)Raw;
        }

        /// <summary>The value reinterpreted as a <see cref="SurtrRef"/>. Only meaningful when <see cref="IsReference"/> is true.</summary>
        public SurtrRef AsReference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (SurtrRef)Raw;
        }
        #endregion

        #region Type Checking
        /// <summary>Whether this value holds an int.</summary>
        public bool IsInt
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (Raw & TagMask) == TagInt;
        }

        /// <summary>Whether this value holds a float.</summary>
        public bool IsFloat
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (Raw & TagMask) == TagFloat;
        }

        /// <summary>Whether this value holds a bool.</summary>
        public bool IsBool
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (Raw & TagMask) == TagBool;
        }

        /// <summary>Whether this value holds a char.</summary>
        public bool IsChar
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (Raw & TagMask) == TagChar;
        }

        /// <summary>Whether this value holds an entity reference (null or otherwise).</summary>
        public bool IsReference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (Raw & TagMask) == TagMaskReference;
        }

        /// <summary>Whether this value is a reference equal to <see cref="NullRef"/>.</summary>
        public bool IsNullReference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ((Raw & TagMask) == TagMaskReference) && (SurtrRef)Raw == NullRef;
        }
        #endregion

        #region Factory Methods
        /// <summary>Creates an int value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(SurtrInt value) => new(TagInt, (SurtrRawPayloadValue)value);

        /// <summary>Creates an int value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(sbyte value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);

        /// <summary>Creates an int value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(short value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);

        /// <summary>Creates an int value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(long value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);

        /// <summary>Creates an int value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(byte value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);

        /// <summary>Creates an int value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(ushort value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);

        /// <summary>Creates an int value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(uint value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);

        /// <summary>Creates an int value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(ulong value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);


        /// <summary>Creates a float value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateFloat(SurtrFloat value) => new(value);

        /// <summary>Creates a float value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateFloat(float value) => new((SurtrFloat)value);


        /// <summary>Creates a bool value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateBool(SurtrBool value) => new(TagBool, (SurtrRawPayloadValue)(value ? 1U : 0U));


        /// <summary>Creates a char value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateChar(SurtrChar value) => new(TagChar, (SurtrRawPayloadValue)value);


        /// <summary>Creates an entity reference value from a raw <see cref="SurtrRef"/> id.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateReference(SurtrRef value) => new(TagReference, (SurtrRawPayloadValue)value);
        #endregion

        #region Static Properties
        /// <summary>The default <see cref="SurtrValue"/>: a null reference.</summary>
        public static readonly SurtrValue Default = CreateReference(NullRef);

        /// <summary>A null reference value.</summary>
        public static readonly SurtrValue Null = CreateReference(NullRef);

        /// <summary>The boolean value <c>true</c>.</summary>
        public static readonly SurtrValue True = CreateBool(true);

        /// <summary>The boolean value <c>false</c>.</summary>
        public static readonly SurtrValue False = CreateBool(false);
        #endregion
    }
}
