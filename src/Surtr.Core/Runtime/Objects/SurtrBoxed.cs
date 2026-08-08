#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// A primitive - int, float, bool or char - living on the heap as an object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a change of representation, not of type. A boxed <c>5</c> and an unboxed <c>5</c>
    /// are the same value of the same class: <see cref="SurtrObject.Class"/> here is the very
    /// <see cref="SurtrClass"/> the raw primitive already had, the one
    /// <see cref="SurtrBuiltIns.ForTypeCode"/> hands out. Nothing in the language distinguishes
    /// them - <see cref="SurtrValueComparer"/> hashes and compares a box through its content
    /// precisely so the two land in the same dictionary bucket.
    /// </para>
    /// <para>
    /// It exists because <see cref="SurtrValue"/> is the interpreter's fast path, not a language
    /// tier: a NaN-boxed primitive is 8 bytes with no identity and no <see cref="SurtrRef"/>, so
    /// the moment a primitive has to be reached through a reference - stored where a reference is
    /// expected, or handed to something that traces its operands - it needs a home in the entity
    /// registry. That is what <c>BoxInt</c>, <c>BoxFloat</c> and <c>BoxBool</c> allocate, and what
    /// <c>Unbox</c> reads back. <c>Unbox</c> needs no per-type opcode because the tag on
    /// <see cref="Value"/> already says which primitive came in.
    /// </para>
    /// <para>
    /// Immutable, which is what makes boxes safe to compare by content and safe to share.
    /// </para>
    /// </remarks>
    public sealed class SurtrBoxed : SurtrObject
    {
        /// <summary>The boxed primitive, still NaN-boxed. What <c>Unbox</c> pushes.</summary>
        internal readonly SurtrValue Value;

        internal SurtrBoxed(SurtrClass primitiveClass, SurtrValue value) : base(primitiveClass)
        {
            Value = value;
        }

        /// <summary>The boxed primitive.</summary>
        public SurtrValue BoxedValue
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Value;
        }

        /// <summary>Whether the box holds an int.</summary>
        public bool IsInt
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Value.IsInt;
        }

        /// <summary>Whether the box holds a float.</summary>
        public bool IsFloat
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Value.IsFloat;
        }

        /// <summary>Whether the box holds a bool.</summary>
        public bool IsBool
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Value.IsBool;
        }

        /// <summary>Whether the box holds a char.</summary>
        public bool IsChar
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Value.IsChar;
        }

        // A primitive can never be a reference, so a box roots nothing.
        internal override void VisitReferences(SurtrEntityMarker marker) { }

        /// <inheritdoc/>
        public override string ToString()
        {
            if (Value.IsInt) return Value.AsInt.ToString(CultureInfo.InvariantCulture);
            if (Value.IsBool) return Value.AsBool ? "true" : "false";
            if (Value.IsChar) return Value.AsChar.ToString();
            if (Value.IsFloat) return Value.AsFloat.ToString(CultureInfo.InvariantCulture);
            return string.Empty;
        }
    }
}
