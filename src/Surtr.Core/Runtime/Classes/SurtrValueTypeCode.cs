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

        /// <summary>
        /// The built-in generator type, parameterized by the element it yields.
        /// </summary>
        /// <remarks>
        /// Filed beside <c>Closure</c> because the two are the same kind of thing: a suspended
        /// piece of code plus the state it needs to run. It is parameterised by one type - what a
        /// <c>yield</c> hands back - so its descriptor is the nesting form <c>Y&lt;elem&gt;</c>
        /// rather than the bare symbol <c>Range</c> gets, which is what keeps the element visible
        /// to diagnostics and to cross-module checking.
        /// </remarks>
        Generator   = 10,

        /// <summary>
        /// The built-in range type: a half-open or closed interval of integers.
        /// </summary>
        /// <remarks>
        /// Sits inside the built-in run rather than after it, so both <c>IsBuiltIn</c> and
        /// <c>IsReferenceType</c> stay single range compares. Unlike its neighbours it is not
        /// parameterised - both bounds are always <c>int</c> - which is why its descriptor is a
        /// bare symbol rather than a nesting form.
        /// </remarks>
        Range       = 11,

        /// <summary>
        /// The built-in bytes type: a mutable array of bytes, behind every <c>SurtrBytes</c>.
        /// </summary>
        /// <remarks>
        /// A plain single-slot reference type, like <c>string</c>: the compiler proves the element
        /// accesses and the runtime stores the data as a CLR <c>byte[]</c>. It sits inside the
        /// built-in run so <c>IsBuiltIn</c> and <c>IsReferenceType</c> stay single range compares,
        /// and it is not parameterised - a byte is always a byte - so its descriptor is a bare
        /// symbol rather than a nesting form.
        /// </remarks>
        Bytes       = 12,

        /// <summary>A class declared in Surtr source.</summary>
        Object      = 13,

        /// <summary>A type defined by the embedding host rather than by Surtr source.</summary>
        Native      = 14,

        /// <summary>
        /// What a generic type parameter erases to.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Surtr's generics are a compile-time construct: the compiler checks them and then throws
        /// them away, the way Java does. That leaves one thing the runtime still has to answer -
        /// what a field or parameter declared as <c>T</c> looks like in memory - because a class's
        /// instance layout and its reference-slot map are built from declared types.
        /// </para>
        /// <para>
        /// The answer is this code: an erased slot always holds a reference. That makes the layout
        /// computable without knowing the type argument, at the cost the same decision imposes on
        /// Java - a primitive flowing into an erased slot has to be boxed, and reading one back out
        /// needs a checked cast the compiler inserts. It sits inside the reference-type range on
        /// purpose, so <c>IsReferenceType</c> stays a range compare rather than growing a special
        /// case.
        /// </para>
        /// </remarks>
        Erased      = 15,

        /// <summary>
        /// The absence of a value: the return "type" of a method that returns nothing.
        /// </summary>
        /// <remarks>
        /// Only ever legal as a return type. Nothing can be declared of this type, no value ever
        /// carries it, and its <c>SurtrClass</c> is an abstract, memberless marker - it exists
        /// because every <see cref="SurtrMethodInfo"/> needs a return descriptor and
        /// <c>ReturnVoid</c> methods have nothing else to name. Kept last so every real type,
        /// reference types included, forms one contiguous range below it.
        /// </remarks>
        Void        = 16,
    }

    /// <summary>Classification predicates and conversions for <see cref="SurtrValueTypeCode"/>.</summary>
    public static class SurtrValueTypeCodeExtensions
    {
        private const SurtrValueTypeCode MinValid = SurtrValueTypeCode.Integer;
        private const SurtrValueTypeCode MaxValid = SurtrValueTypeCode.Void;

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

            /// <summary>Whether the code is a built-in composite (string, array, tuple, dictionary, closure, generator, range or bytes).</summary>
            public bool IsBuiltIn => code >= SurtrValueTypeCode.String && code <= SurtrValueTypeCode.Bytes;

            /// <summary>Whether the code is <see cref="SurtrValueTypeCode.Range"/>.</summary>
            public bool IsRange => code == SurtrValueTypeCode.Range;

            /// <summary>Whether the code is <see cref="SurtrValueTypeCode.Bytes"/>.</summary>
            public bool IsBytes => code == SurtrValueTypeCode.Bytes;

            /// <summary>Whether the code is <see cref="SurtrValueTypeCode.Generator"/>.</summary>
            public bool IsGenerator => code == SurtrValueTypeCode.Generator;

            /// <summary>Whether the code is <see cref="SurtrValueTypeCode.Object"/>.</summary>
            public bool IsObject => code == SurtrValueTypeCode.Object;

            /// <summary>Whether the code is <see cref="SurtrValueTypeCode.Native"/>.</summary>
            public bool IsNative => code == SurtrValueTypeCode.Native;

            /// <summary>Whether the code is <see cref="SurtrValueTypeCode.Erased"/> - an erased generic type parameter.</summary>
            public bool IsErased => code == SurtrValueTypeCode.Erased;

            /// <summary>Whether the code is <see cref="SurtrValueTypeCode.Void"/>, and so names no value at all.</summary>
            public bool IsVoid => code == SurtrValueTypeCode.Void;


            /// <summary>Whether values of this type are passed by value. Currently the same set as <c>IsPrimitive</c>.</summary>
            public bool IsValueType => code >= SurtrValueTypeCode.Integer && code <= SurtrValueTypeCode.Character;

            /// <summary>
            /// Whether values of this type are passed by reference: every built-in composite except
            /// <c>range</c>, object and native types, and an erased generic parameter, which is
            /// always a reference.
            /// </summary>
            /// <remarks>
            /// A range is excluded since it became an inline value (§2.9): its values travel as
            /// their own three-slot block, and its packed form is a representation choice rather
            /// than its storage identity - exactly the line that separates the primitives from the
            /// composites here.
            /// </remarks>
            public bool IsReferenceType => code >= SurtrValueTypeCode.String && code <= SurtrValueTypeCode.Erased
                && code != SurtrValueTypeCode.Range;

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
