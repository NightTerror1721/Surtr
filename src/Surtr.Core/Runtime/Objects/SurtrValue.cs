#nullable enable

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Surtr.Runtime.Objects
{
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

        public const SurtrRef NullRef = 0;


        [FieldOffset(0)] internal readonly SurtrRawValue Raw;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrValue()
        {
            AsFloat = 0;
            Raw = TagMaskReference | NullRef;
        }
        #endregion

        #region Type Casting
        public SurtrInt AsInt
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (SurtrInt)Raw;
        }

        public SurtrBool AsBool
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (SurtrInt)Raw != 0;
        }

        public SurtrChar AsChar
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (SurtrChar)Raw;
        }

        public SurtrRef AsReference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (SurtrRef)Raw;
        }
        #endregion

        #region Type Checking
        public bool IsInt
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (Raw & TagMask) == TagInt;
        }

        public bool IsFloat
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (Raw & TagMask) == TagFloat;
        }

        public bool IsBool
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (Raw & TagMask) == TagBool;
        }

        public bool IsChar
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (Raw & TagMask) == TagChar;
        }

        public bool IsReference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (Raw & TagMask) == TagMaskReference;
        }

        public bool IsNullReference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ((Raw & TagMask) == TagMaskReference) && (SurtrRef)Raw == NullRef;
        }
        #endregion

        #region Factory Methods
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(SurtrInt value) => new(TagInt, (SurtrRawPayloadValue)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(sbyte value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(short value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(long value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(byte value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(ushort value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(uint value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateInt(ulong value) => new(TagInt, (SurtrRawPayloadValue)(SurtrInt)value);


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateFloat(SurtrFloat value) => new(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateFloat(float value) => new((SurtrFloat)value);


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateBool(SurtrBool value) => new(TagBool, (SurtrRawPayloadValue)(value ? 1U : 0U));


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateChar(SurtrChar value) => new(TagChar, (SurtrRawPayloadValue)value);


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrValue CreateReference(SurtrRef value) => new(TagReference, (SurtrRawPayloadValue)value);
        #endregion

        #region Static Properties
        public static readonly SurtrValue Default = CreateReference(NullRef);
        public static readonly SurtrValue Null = CreateReference(NullRef);
        public static readonly SurtrValue True = CreateBool(true);
        public static readonly SurtrValue False = CreateBool(false);
        #endregion
    }
}
