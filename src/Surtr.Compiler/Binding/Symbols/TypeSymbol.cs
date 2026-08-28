#nullable enable

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>The form a type takes, which is what decides how it is written and emitted.</summary>
    public enum TypeSymbolKind
    {
        /// <summary>A class declared with <c>class</c>, including the built-in primitives and <c>string</c>.</summary>
        Class,

        /// <summary>An interface: a pure contract (§2.2).</summary>
        Interface,

        /// <summary>An enum, whose cases carry ordinals (§2.4).</summary>
        Enum,

        /// <summary>A <c>value class</c>: a distinct type that erases to its single field (§2.9).</summary>
        ValueClass,

        /// <summary>A <c>singleton</c>: a type with exactly one instance, created at module load (§2.8).</summary>
        Singleton,

        /// <summary>A type declared by the embedding host rather than by Surtr source.</summary>
        Native,

        /// <summary>An array type, written <c>T[]</c>.</summary>
        Array,

        /// <summary>A dictionary type, written <c>{K: V}</c>.</summary>
        Dictionary,

        /// <summary>A tuple type, written <c>(A, B)</c>.</summary>
        Tuple,

        /// <summary>A closure type, written <c>(A, B) -&gt; R</c>.</summary>
        Closure,

        /// <summary>A generator type, written <c>generator&lt;T&gt;</c> (§3.7).</summary>
        Generator,

        /// <summary>A generic type parameter, declared by a type or by a method.</summary>
        TypeParameter,

        /// <summary>
        /// A type that could not be resolved. Binding never returns <see langword="null"/> for a
        /// type, so one bad type reference produces this and every rule that touches it stays
        /// silent, which is what keeps a single mistake from cascading into a screenful.
        /// </summary>
        Error,
    }

    /// <summary>
    /// Marks the types the compiler must recognise by identity rather than by name: the primitives
    /// and the handful of built-ins with their own descriptor symbol.
    /// </summary>
    public enum SpecialType
    {
        /// <summary>Not a special type.</summary>
        None,

        /// <summary>The <c>int</c> primitive.</summary>
        Int,

        /// <summary>The <c>float</c> primitive.</summary>
        Float,

        /// <summary>The <c>bool</c> primitive.</summary>
        Bool,

        /// <summary>The <c>char</c> primitive.</summary>
        Char,

        /// <summary>The built-in <c>string</c>, which is a reference type.</summary>
        String,

        /// <summary>The built-in <c>range</c>, which takes no parameters — both bounds are <c>int</c>.</summary>
        Range,

        /// <summary>
        /// The built-in <c>bytes</c>: a mutable array of bytes, which takes no parameters — a byte
        /// is always a byte.
        /// </summary>
        Bytes,

        /// <summary>
        /// <c>void</c>, legal only as a return type. Not a type anything can be declared of.
        /// </summary>
        Void,

        /// <summary>
        /// <c>unknown</c> (§5.10): holds anything, must be cast before use, and shares the erased
        /// representation a generic type parameter has.
        /// </summary>
        Unknown,

        /// <summary>
        /// <c>never</c> (§9): the bottom type. No value has this type, but an expression that throws
        /// is typed as it, which lets <c>throw</c> sit in any expression slot — a branch of
        /// <c>?:</c>, the right operand of <c>??</c>, a lambda body — without contributing to the
        /// surrounding type. <c>never</c> is assignable to every type and is legal only as a return
        /// type, where a body that never completes satisfies it.
        /// </summary>
        Never,
    }

    /// <summary>The base of every type the binder works with.</summary>
    /// <remarks>
    /// <para>
    /// Type identity is <em>reference</em> identity. Every type is created through
    /// <see cref="TypeSymbolFactory"/>, which interns structurally identical types, so two
    /// occurrences of <c>int[]</c> are the same object and comparing types never walks a structure.
    /// Constructors are therefore internal: a type built outside the factory would compare unequal
    /// to its own twin and quietly break every check that rests on this.
    /// </para>
    /// <para>
    /// Nullability is carried here rather than in a wrapper type. A wrapper would have to be
    /// unwrapped at every <c>is NamedTypeSymbol</c> in the binder, and the one that forgets is a
    /// silent bug; a flag means a nullable type still <em>is</em> its own kind of type. Each type
    /// and its nullable twin are linked as duals, created on first use and cached, so
    /// <c>int?</c> is one object no matter how it is reached.
    /// </para>
    /// </remarks>
    public abstract class TypeSymbol : Symbol
    {
        private TypeSymbol? _dual;

        private protected TypeSymbol(bool isNullable) => IsNullable = isNullable;

        /// <inheritdoc/>
        public sealed override SymbolKind Kind => SymbolKind.Type;

        /// <summary>The form this type takes.</summary>
        public abstract TypeSymbolKind TypeKind { get; }

        /// <summary>Which built-in this is, or <see cref="SpecialType.None"/>.</summary>
        public virtual SpecialType SpecialType => SpecialType.None;

        /// <summary>Whether <c>?</c> was applied, so the type can also hold <c>null</c> (§5.1).</summary>
        public bool IsNullable { get; }

        /// <summary>
        /// Whether a value of this type is a reference. Everything except the four primitives is,
        /// including <c>string</c>, every composite, and an erased type parameter.
        /// </summary>
        public abstract bool IsReferenceType { get; }

        /// <summary>Whether a value of this type is one of the four primitives.</summary>
        /// <remarks>
        /// <c>void</c> answers <see langword="false"/> here and <see langword="true"/> to
        /// <see cref="IsReferenceType"/>, but it names no value at all — check
        /// <see cref="IsVoid"/> before either, wherever a type could be a return type.
        /// </remarks>
        public bool IsPrimitive => !IsReferenceType;

        /// <summary>Whether this is <c>void</c>, which names no value and is legal only as a return type.</summary>
        public bool IsVoid => SpecialType == SpecialType.Void;

        /// <summary>Whether this is <c>never</c>, the bottom type: assignable to everything, produced by a <c>throw</c>.</summary>
        public bool IsNever => SpecialType == SpecialType.Never;

        /// <summary>Whether this type mentions a type parameter anywhere inside it.</summary>
        public virtual bool ContainsTypeParameter => false;

        /// <summary>Whether this is the error type, so rules touching it should stay silent.</summary>
        public bool IsError => TypeKind == TypeSymbolKind.Error;

        /// <summary>This type's nullable form, which is itself if it already is one.</summary>
        public TypeSymbol Nullable => WithNullability(true);

        /// <summary>This type's non-nullable form, which is itself if it already is one.</summary>
        public TypeSymbol NonNullable => WithNullability(false);

        /// <summary>
        /// This type with <c>?</c> applied or removed. The two forms are duals of one another and
        /// are created once, so the result is stable under reference comparison.
        /// </summary>
        public TypeSymbol WithNullability(bool nullable)
        {
            if (nullable == IsNullable)
                return this;

            if (_dual is null)
            {
                var dual = CreateDual();
                dual._dual = this;
                _dual = dual;
            }

            return _dual;
        }

        /// <summary>Builds this type's opposite-nullability twin. Called once, then cached.</summary>
        private protected abstract TypeSymbol CreateDual();

        /// <summary>
        /// Replaces every type parameter this type mentions according to
        /// <paramref name="substitution"/>, returning an interned type.
        /// </summary>
        public virtual TypeSymbol Substitute(TypeSubstitution substitution) => this;

        /// <summary>Appends <c>?</c> when this type is nullable, so subclasses do not each repeat it.</summary>
        private protected string WithNullableSuffix(string text) => IsNullable ? text + "?" : text;
    }
}
