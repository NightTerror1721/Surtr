#nullable enable

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// Identifies every basic type family in Surtr. Each code has a parallel <c>SurtrClass</c>,
    /// since everything in the language is an object; the code exists so the VM can branch on a
    /// type without going through class metadata.
    /// </summary>
    public enum SurtrValueTypeCode : byte
    {
        /// <summary>Not a valid type.</summary>
        Invalid     = 0,

        /// <summary>The integer primitive.</summary>
        Integer     = 1,

        /// <summary>The float primitive.</summary>
        Float       = 2,

        /// <summary>The boolean primitive.</summary>
        Boolean     = 3,

        /// <summary>The character primitive.</summary>
        Character   = 4,

        /// <summary>The built-in string type.</summary>
        String      = 5,

        /// <summary>The built-in array type, parameterized by its element type.</summary>
        Array       = 6,

        /// <summary>The built-in tuple type, parameterized by its element types.</summary>
        Tuple       = 7,

        /// <summary>The built-in dictionary type, parameterized by its key and value types.</summary>
        Dictionary  = 8,

        /// <summary>The built-in closure type, parameterized by its parameter and return types.</summary>
        Closure     = 9,

        /// <summary>A class declared in Surtr source.</summary>
        Object      = 10,

        /// <summary>A type defined by the embedding host rather than by Surtr source.</summary>
        Native      = 11,
    }

    /// <summary>Classification predicates and conversions for <see cref="SurtrValueTypeCode"/>.</summary>
    public static class SurtrValueTypeCodeExtensions
    {
        private const SurtrValueTypeCode MinValid = SurtrValueTypeCode.Integer;
        private const SurtrValueTypeCode MaxValid = SurtrValueTypeCode.Native;

        private const byte MinValue = (byte)MinValid;
        private const byte MaxValue = (byte)MaxValid;

        extension (SurtrValueTypeCode code)
        {
            /// <summary>Whether the code names a real type.</summary>
            public bool IsValid => code >= MinValid && code <= MaxValid;

            /// <summary>Whether the code does not name a real type.</summary>
            public bool IsInvalid => code < MinValid || code > MaxValid;

            /// <summary>Whether the code is a primitive (integer, float, boolean or character).</summary>
            public bool IsPrimitive => code >= SurtrValueTypeCode.Integer && code <= SurtrValueTypeCode.Character;

            /// <summary>Whether the code is a built-in composite (string, array, tuple, dictionary or closure).</summary>
            public bool IsBuiltIn => code >= SurtrValueTypeCode.String && code <= SurtrValueTypeCode.Closure;

            /// <summary>Whether the code is <see cref="SurtrValueTypeCode.Object"/>.</summary>
            public bool IsObject => code == SurtrValueTypeCode.Object;

            /// <summary>Whether the code is <see cref="SurtrValueTypeCode.Native"/>.</summary>
            public bool IsNative => code == SurtrValueTypeCode.Native;


            /// <summary>Whether values of this type are passed by value. Currently the same set as <c>IsPrimitive</c>.</summary>
            public bool IsValueType => code >= SurtrValueTypeCode.Integer && code <= SurtrValueTypeCode.Character;

            /// <summary>Whether values of this type are passed by reference: every built-in composite plus object and native types.</summary>
            public bool IsReferenceType => code >= SurtrValueTypeCode.String && code <= SurtrValueTypeCode.Native;

            /// <summary>The code's underlying byte value.</summary>
            public byte ToByte() => (byte)code;

            /// <summary>Converts a raw byte back into a code, mapping anything out of range to <see cref="SurtrValueTypeCode.Invalid"/>.</summary>
            public static SurtrValueTypeCode FromByte(byte value)
            {
                if (value < MinValue || value > MaxValue)
                    return SurtrValueTypeCode.Invalid;
                return (SurtrValueTypeCode)value;
            }
        }
    }
}
