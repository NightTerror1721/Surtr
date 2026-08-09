#nullable enable

using Surtr.Runtime.Objects;
using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>What shape of literal a <see cref="SurtrConstant"/> carries.</summary>
    /// <remarks>
    /// Deliberately not <see cref="SurtrValueTypeCode"/>, even though the two overlap on the
    /// primitives: this enum has to express two states no type code can, "there is no constant
    /// here" and "the constant is the null literal", and a type code that grew them would be
    /// answering a different question everywhere else it is read.
    /// </remarks>
    public enum SurtrConstantKind : byte
    {
        /// <summary>No constant at all - what a parameter without a default carries.</summary>
        None = 0,

        /// <summary>An integer literal.</summary>
        Integer = 1,

        /// <summary>A float literal.</summary>
        Float = 2,

        /// <summary>A boolean literal.</summary>
        Boolean = 3,

        /// <summary>A character literal.</summary>
        Character = 4,

        /// <summary>A string literal, held as CLR text.</summary>
        String = 5,

        /// <summary>The null literal.</summary>
        Null = 6,
    }

    /// <summary>
    /// A compile-time constant carried by metadata: a parameter's default value, or an argument
    /// to an attribute.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a bare <see cref="SurtrValue"/>, and the reason is strings. A primitive constant is a
    /// value and fits in eight bytes, but a string constant has no <see cref="SurtrRef"/> until
    /// some runtime interns it - and metadata is built before any runtime exists and outlives any
    /// one of them. So the text travels as CLR text here and becomes an object only where it is
    /// used, through <see cref="Materialize"/>.
    /// </para>
    /// <para>
    /// The set of legal shapes is the one <c>Language-Syntax.md</c> §7.1 fixes for <c>const</c> -
    /// a primitive or a string - plus the null literal, which a default value for a nullable
    /// parameter needs and which no <c>const</c> declaration can produce.
    /// </para>
    /// </remarks>
    public readonly struct SurtrConstant
    {
        private readonly SurtrValue _value;
        private readonly string? _text;
        private readonly SurtrConstantKind _kind;

        private SurtrConstant(SurtrConstantKind kind, SurtrValue value, string? text)
        {
            _kind = kind;
            _value = value;
            _text = text;
        }

        /// <summary>What shape of literal this constant carries.</summary>
        public SurtrConstantKind Kind
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _kind;
        }

        /// <summary>Whether this carries a constant at all.</summary>
        public bool HasValue
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _kind != SurtrConstantKind.None;
        }

        /// <summary>
        /// The primitive payload. Meaningful for every kind except
        /// <see cref="SurtrConstantKind.String"/>, whose payload is <see cref="Text"/>.
        /// </summary>
        public SurtrValue Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value;
        }

        /// <summary>The text payload, or <see langword="null"/> unless <see cref="Kind"/> is <see cref="SurtrConstantKind.String"/>.</summary>
        public string? Text
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _text;
        }

        #region Factory Methods
        /// <summary>The absence of a constant.</summary>
        public static SurtrConstant None => default;

        /// <summary>Creates an integer constant.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrConstant Integer(SurtrInt value)
            => new(SurtrConstantKind.Integer, SurtrValue.CreateInt(value), null);

        /// <summary>Creates a float constant.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrConstant Float(SurtrFloat value)
            => new(SurtrConstantKind.Float, SurtrValue.CreateFloat(value), null);

        /// <summary>Creates a boolean constant.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrConstant Boolean(SurtrBool value)
            => new(SurtrConstantKind.Boolean, SurtrValue.CreateBool(value), null);

        /// <summary>Creates a character constant.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrConstant Character(SurtrChar value)
            => new(SurtrConstantKind.Character, SurtrValue.CreateChar(value), null);

        /// <summary>Creates a string constant.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>; use <see cref="Null"/>.</exception>
        public static SurtrConstant String(string value)
        {
            if (value is null)
                throw new ArgumentNullException(nameof(value), "A string constant cannot be null; use SurtrConstant.Null.");

            return new SurtrConstant(SurtrConstantKind.String, SurtrValue.Null, value);
        }

        /// <summary>Creates the null-literal constant.</summary>
        public static SurtrConstant Null => new(SurtrConstantKind.Null, SurtrValue.Null, null);
        #endregion

        /// <summary>
        /// Turns this constant into a value on a particular runtime, interning a string constant
        /// the way a literal in a chunk's constant pool is interned.
        /// </summary>
        /// <exception cref="InvalidOperationException">This carries no constant.</exception>
        public SurtrValue Materialize(SurtrRuntime runtime)
        {
            if (runtime is null)
                throw new ArgumentNullException(nameof(runtime));

            switch (_kind)
            {
                case SurtrConstantKind.String:
                    return SurtrValue.CreateReference(runtime.InternString(_text!).GetSurtrReference());

                case SurtrConstantKind.None:
                    throw new InvalidOperationException("This constant carries no value to materialize.");

                default:
                    return _value;
            }
        }

        /// <summary>Renders the constant the way source would spell it. For diagnostics only.</summary>
        public override string ToString() => _kind switch
        {
            SurtrConstantKind.None => "<none>",
            SurtrConstantKind.Integer => _value.AsInt.ToString(CultureInfo.InvariantCulture),
            SurtrConstantKind.Float => _value.AsFloat.ToString("R", CultureInfo.InvariantCulture),
            SurtrConstantKind.Boolean => _value.AsBool ? "true" : "false",
            SurtrConstantKind.Character => "'" + _value.AsChar + "'",
            SurtrConstantKind.String => "\"" + _text + "\"",
            SurtrConstantKind.Null => "null",
            _ => "<invalid>",
        };
    }
}
