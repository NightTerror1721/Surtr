#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// An unresolved, by-name reference to a Surtr type, encoded as a compact descriptor string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Member signatures can't point at a <c>SurtrClass</c> instance directly (that would make
    /// class construction order-dependent and circular), nor at a <see cref="SurtrRef"/> (those
    /// only exist once an entity has been registered, which happens after construction). So a
    /// type reference is carried as text and resolved later, once every class in a module is
    /// known.
    /// </para>
    /// <para>
    /// The encoding is a descriptor rather than a dotted C#-style full name because composite
    /// types nest: an array is an array <em>of</em> something, a dictionary maps one type
    /// <em>to</em> another. Descriptors nest unambiguously and parse in a single left-to-right
    /// pass with one character of lookahead, whereas <c>Array&lt;Dictionary&lt;int, string&gt;&gt;</c>
    /// would need a real bracket-and-comma parser. Use <see cref="ToDisplayString"/> when a
    /// human needs to read one.
    /// </para>
    /// <para>Grammar:</para>
    /// <code>
    /// descriptor := 'I'                                integer
    ///             | 'F'                                float
    ///             | 'B'                                boolean
    ///             | 'C'                                character
    ///             | 'S'                                string
    ///             | 'A' descriptor                     array of T
    ///             | 'D' descriptor descriptor          dictionary from K to V
    ///             | 'T' '(' descriptor* ')'            tuple
    ///             | 'L' '(' descriptor* ')' descriptor closure (params) -> return
    ///             | 'O' fullname ';' descriptor{arity} Surtr object type
    ///             | 'N' fullname ';' descriptor{arity} host-defined native type
    ///             | 'R'                                range of ints
    ///             | 'E'                                erased generic type parameter
    ///             | 'G' digit                          the declaring type's n-th generic parameter
    ///             | 'H' digit                          the declaring method's n-th generic parameter
    ///             | '?' primitive                      nullable primitive (?I, ?F, ?B, ?C)
    /// fullname   := modulePath ':' segment ('.' segment)*
    /// segment    := typeName ('`' arity)?
    /// </code>
    /// <para>
    /// The <c>':'</c> in a full name separates the module path from the type path, so a resolver
    /// splits it in one pass instead of probing prefixes to find where the module ends.
    /// </para>
    /// <para>
    /// A generic type mangles its arity into its name segment (<c>Box`1</c>), which makes arity
    /// part of the type's identity - <c>Box&lt;T&gt;</c> and <c>Box&lt;T, U&gt;</c> are unrelated
    /// declarations - and is also what lets the argument list follow the terminator with neither
    /// brackets nor a count: <c>{arity}</c> above is read off the name before the arguments are
    /// reached, so the whole thing still parses in one pass with one character of lookahead.
    /// <b>Only the last segment's arity counts</b>; the earlier ones are qualification, because a
    /// type nested inside a generic one does not see its container's parameters. So
    /// <c>Obox:Box`1;I</c> is <c>Box&lt;int&gt;</c>, and <c>Obox:Box`1.Entry;</c> is an
    /// <c>Entry</c> that takes nothing.
    /// </para>
    /// <para>
    /// The arguments say nothing to the runtime: two constructions of one declaration share a full
    /// name and resolve to the same <see cref="SurtrClass"/>, exactly as <c>AI</c> and <c>AS</c>
    /// both resolve to the shared array class. What they buy is that a signature can tell
    /// <c>f(Box&lt;int&gt;)</c> from <c>f(Box&lt;string&gt;)</c>. See
    /// <c>docs/Compiler-Plan.md</c> §8.
    /// </para>
    /// </remarks>
    public readonly struct SurtrClassReference : IEquatable<SurtrClassReference>
    {
        #region Descriptor Symbols
        /// <summary>Descriptor symbol for <see cref="SurtrValueTypeCode.Integer"/>.</summary>
        public const char SymbolInteger = 'I';

        /// <summary>Descriptor symbol for <see cref="SurtrValueTypeCode.Float"/>.</summary>
        public const char SymbolFloat = 'F';

        /// <summary>Descriptor symbol for <see cref="SurtrValueTypeCode.Boolean"/>.</summary>
        public const char SymbolBoolean = 'B';

        /// <summary>Descriptor symbol for <see cref="SurtrValueTypeCode.Character"/>.</summary>
        public const char SymbolCharacter = 'C';

        /// <summary>Descriptor symbol for <see cref="SurtrValueTypeCode.String"/>.</summary>
        public const char SymbolString = 'S';

        /// <summary>Descriptor symbol for <see cref="SurtrValueTypeCode.Array"/>, followed by its element descriptor.</summary>
        public const char SymbolArray = 'A';

        /// <summary>Descriptor symbol for <see cref="SurtrValueTypeCode.Dictionary"/>, followed by its key then value descriptors.</summary>
        public const char SymbolDictionary = 'D';

        /// <summary>Descriptor symbol for <see cref="SurtrValueTypeCode.Tuple"/>, followed by a parenthesized element list.</summary>
        public const char SymbolTuple = 'T';

        /// <summary>Descriptor symbol for <see cref="SurtrValueTypeCode.Closure"/>, followed by a parenthesized parameter list then the return descriptor.</summary>
        public const char SymbolClosure = 'L';

        /// <summary>
        /// Descriptor symbol for <see cref="SurtrValueTypeCode.Range"/>.
        /// </summary>
        /// <remarks>
        /// A bare symbol like a primitive's, not a nesting form like <c>A</c> or <c>D</c>: both
        /// bounds of a range are always <c>int</c>, so there is nothing to parameterise it by and
        /// nothing for the nesting grammar to carry.
        /// </remarks>
        public const char SymbolRange = 'R';

        /// <summary>Descriptor symbol for <see cref="SurtrValueTypeCode.Object"/>, followed by a full name and <see cref="NameTerminator"/>.</summary>
        public const char SymbolObject = 'O';

        /// <summary>Descriptor symbol for <see cref="SurtrValueTypeCode.Native"/>, followed by a full name and <see cref="NameTerminator"/>.</summary>
        public const char SymbolNative = 'N';

        /// <summary>
        /// Descriptor symbol for <see cref="SurtrValueTypeCode.Erased"/>: what a generic type
        /// parameter is written as once the compiler has checked it away.
        /// </summary>
        /// <remarks>
        /// Carries no name. Two different type parameters of the same class erase to the same
        /// descriptor, exactly as they do on the JVM, because nothing at run time can tell them
        /// apart or needs to - <c>E</c> says only "a reference whose type the compiler already
        /// verified".
        /// </remarks>
        public const char SymbolErased = 'E';

        /// <summary>
        /// Descriptor symbol for <see cref="SurtrValueTypeCode.Void"/>. Only legal as the return
        /// descriptor of a closure descriptor - a parameter, field or element can never be void.
        /// </summary>
        public const char SymbolVoid = 'V';

        /// <summary>
        /// Descriptor symbol introducing a declared generic parameter of the <em>method</em> the
        /// member belongs to: <c>H</c> followed by one decimal digit, so <c>H0</c> is the method's
        /// first parameter.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The type-level twin <see cref="SymbolGenericParameter"/> already exists and resolves to
        /// the same erased class, but the two cannot share a symbol: a descriptor is the canonical
        /// form for comparison and hashing, and <c>G0</c> in a signature written against one
        /// construction must never be mistaken for the same parameter in another. Keeping the
        /// method's parameters under their own symbol lets a reader tell "the declaring type's
        /// first parameter" from "the declaring method's first parameter" without knowing which
        /// member the descriptor belongs to - the <c>SignatureKey()</c> erasure rewrites both to
        /// <see cref="SymbolErased"/>, so the slot they occupy stays the same.
        /// </para>
        /// <para>
        /// One digit, like <c>G</c>: the arity that would need ten parameters does not exist, and a
        /// fixed width keeps this parsing in one pass with a single character of lookahead.
        /// </para>
        /// </remarks>
        public const char SymbolMethodGenericParameter = 'H';

        /// <summary>
        /// Descriptor symbol introducing a declared generic parameter of the type the member
        /// belongs to: <c>G</c> followed by one decimal digit, so <c>G0</c> is the declaring
        /// type's first parameter.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It resolves to the same class <see cref="SymbolErased"/> does, so it costs the runtime
        /// nothing and changes no layout: a generic slot is a reference either way. What it adds
        /// is the one thing <c>E</c> throws away - <em>which</em> parameter it is - and that is
        /// what lets a built-in declare a member whose signature mentions its own element type.
        /// <c>array.push(value: G0)</c> is checkable against <c>int[]</c>; <c>push(value: E)</c>
        /// is not, because nothing connects the parameter back to the receiver.
        /// </para>
        /// <para>
        /// One digit, not a run of them: the arity that would need ten parameters does not exist,
        /// and a fixed width keeps this parsing in one pass with a single character of lookahead
        /// like every other symbol.
        /// </para>
        /// </remarks>
        public const char SymbolGenericParameter = 'G';

        /// <summary>
        /// Descriptor prefix marking a nullable primitive: <c>?</c> followed by a primitive
        /// symbol, so <c>?I</c> is <c>int?</c>.
        /// </summary>
        /// <remarks>
        /// Legal only before <see cref="SymbolInteger"/>, <see cref="SymbolFloat"/>,
        /// <see cref="SymbolBoolean"/> or <see cref="SymbolCharacter"/>, and that restriction is
        /// the point. A nullable <em>reference</em> needs no encoding at all - a reference is its
        /// 32-bit payload and null is already representable - so allowing <c>?S</c> would create a
        /// second descriptor for a type that already has one, and descriptors are the canonical
        /// form for comparison and hashing. Only a primitive needs somewhere to put "absent",
        /// which is what the reserved value tag provides.
        /// </remarks>
        public const char SymbolNullable = '?';

        /// <summary>Terminates the full name that follows <see cref="SymbolObject"/> or <see cref="SymbolNative"/>.</summary>
        public const char NameTerminator = ';';

        /// <summary>Opens the element/parameter list of a tuple or closure descriptor.</summary>
        public const char ListOpen = '(';

        /// <summary>Closes the element/parameter list of a tuple or closure descriptor.</summary>
        public const char ListClose = ')';

        /// <summary>Separates the module path from the type path inside a full name.</summary>
        public const char ModuleSeparator = ':';

        /// <summary>Separates path segments within the module path, and enclosing types from nested ones.</summary>
        public const char NameSeparator = '.';

        /// <summary>
        /// Introduces the arity mangled into a generic type's name segment, so <c>Box`1</c> and
        /// <c>Box`2</c> are different types rather than a collision.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Backtick is not a legal Surtr identifier character, so a mangled name can never collide
        /// with a declared one. Arity is part of a type's identity - the argument list is not:
        /// <c>Box&lt;int&gt;</c> and <c>Box&lt;string&gt;</c> are two descriptors resolving to one
        /// <see cref="SurtrClass"/>, exactly as <c>AI</c> and <c>AS</c> both resolve to the shared
        /// array class. Nothing is reified.
        /// </para>
        /// <para>
        /// Putting the arity in the name is also what lets the argument list follow
        /// <see cref="NameTerminator"/> with neither brackets nor a count: a reader knows how many
        /// descriptors to expect before it reaches them. See <c>docs/Compiler-Plan.md</c> §8.
        /// </para>
        /// </remarks>
        public const char ArityMarker = '`';
        #endregion

        private readonly string? _descriptor;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private SurtrClassReference(string descriptor) => _descriptor = descriptor;

        /// <summary>The raw descriptor text, or an empty string for a default-constructed reference.</summary>
        public string Descriptor
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _descriptor ?? string.Empty;
        }

        /// <summary>Whether this reference carries a descriptor at all (a default-constructed one does not).</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !string.IsNullOrEmpty(_descriptor);
        }

        /// <summary>
        /// The type code this reference denotes, read straight off the descriptor's first
        /// character, or <see cref="SurtrValueTypeCode.Invalid"/> if the reference is empty or
        /// starts with an unrecognized symbol.
        /// </summary>
        public SurtrValueTypeCode TypeCode
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                string? descriptor = _descriptor;
                if (string.IsNullOrEmpty(descriptor))
                    return SurtrValueTypeCode.Invalid;

                // A nullable primitive is still that primitive: it occupies a value slot, is not
                // traced, and answers IsValueType the same way. The prefix says only that one
                // reserved tag pattern is also in its range.
                return descriptor![0] == SymbolNullable
                    ? (descriptor.Length > 1 ? CodeOf(descriptor[1]) : SurtrValueTypeCode.Invalid)
                    : CodeOf(descriptor[0]);
            }
        }

        /// <summary>
        /// Whether this reference names a nullable primitive - one that can also hold the reserved
        /// "no value" pattern.
        /// </summary>
        public bool IsNullablePrimitive
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => !string.IsNullOrEmpty(_descriptor) && _descriptor![0] == SymbolNullable;
        }

        /// <summary>
        /// Whether this reference names a declared generic parameter of its declaring type, and
        /// which one.
        /// </summary>
        /// <returns><see langword="true"/> if the descriptor is a generic parameter.</returns>
        public bool TryGetGenericParameterIndex(out int index)
            => TryGetParameterIndex(SymbolGenericParameter, out index);

        /// <summary>
        /// Whether this reference names a declared generic parameter of the declaring
        /// <em>method</em>, and which one.
        /// </summary>
        /// <returns><see langword="true"/> if the descriptor is a method generic parameter.</returns>
        public bool TryGetMethodGenericParameterIndex(out int index)
            => TryGetParameterIndex(SymbolMethodGenericParameter, out index);

        private bool TryGetParameterIndex(char symbol, out int index)
        {
            string descriptor = Descriptor;
            if (descriptor.Length == 2 && descriptor[0] == symbol)
            {
                int digit = descriptor[1] - '0';
                if ((uint)digit <= 9)
                {
                    index = digit;
                    return true;
                }
            }

            index = -1;
            return false;
        }

        /// <summary>
        /// The type code of the component this descriptor nests directly - an array's element, a
        /// dictionary's key - or <see cref="SurtrValueTypeCode.Invalid"/> if it nests nothing.
        /// </summary>
        /// <remarks>
        /// Reads one character instead of slicing out a whole nested descriptor, which is what the
        /// typed accessors do and what makes them allocate. The interpreter needs exactly this much
        /// when it allocates an array: which family the elements belong to, so it can fill them with
        /// that family's zero.
        /// </remarks>
        internal SurtrValueTypeCode NestedTypeCode
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                string descriptor = Descriptor;
                return descriptor.Length > 1 ? CodeOf(descriptor[1]) : SurtrValueTypeCode.Invalid;
            }
        }

        #region Primitive & Built-in Singletons
        /// <summary>A reference to the built-in integer type.</summary>
        public static SurtrClassReference Integer { get; } = new(SymbolInteger.ToString());

        /// <summary>A reference to the built-in float type.</summary>
        public static SurtrClassReference Float { get; } = new(SymbolFloat.ToString());

        /// <summary>A reference to the built-in boolean type.</summary>
        public static SurtrClassReference Boolean { get; } = new(SymbolBoolean.ToString());

        /// <summary>A reference to the built-in character type.</summary>
        public static SurtrClassReference Character { get; } = new(SymbolCharacter.ToString());

        /// <summary>A reference to the built-in string type.</summary>
        public static SurtrClassReference String { get; } = new(SymbolString.ToString());

        /// <summary>A reference to the built-in range type.</summary>
        public static SurtrClassReference Range { get; } = new(SymbolRange.ToString());

        /// <summary>A reference to an erased generic type parameter.</summary>
        public static SurtrClassReference Erased { get; } = new(SymbolErased.ToString());

        /// <summary>The return reference of a method that returns nothing.</summary>
        public static SurtrClassReference Void { get; } = new(SymbolVoid.ToString());
        #endregion

        #region Factories
        /// <summary>Builds a reference to an array of <paramref name="elementType"/>.</summary>
        public static SurtrClassReference Array(SurtrClassReference elementType)
            => new(SymbolArray + elementType.Descriptor);

        /// <summary>Builds a reference to a dictionary mapping <paramref name="keyType"/> to <paramref name="valueType"/>.</summary>
        public static SurtrClassReference Dictionary(SurtrClassReference keyType, SurtrClassReference valueType)
            => new(SymbolDictionary + keyType.Descriptor + valueType.Descriptor);

        /// <summary>Builds a reference to a tuple with the given element types, in order.</summary>
        public static SurtrClassReference Tuple(params SurtrClassReference[] elementTypes)
        {
            var builder = new StringBuilder();
            builder.Append(SymbolTuple).Append(ListOpen);
            for (int i = 0; i < elementTypes.Length; i++)
                builder.Append(elementTypes[i].Descriptor);
            builder.Append(ListClose);
            return new SurtrClassReference(builder.ToString());
        }

        /// <summary>Builds a reference to a closure taking <paramref name="parameterTypes"/> and returning <paramref name="returnType"/>.</summary>
        public static SurtrClassReference Closure(SurtrClassReference returnType, params SurtrClassReference[] parameterTypes)
        {
            var builder = new StringBuilder();
            builder.Append(SymbolClosure).Append(ListOpen);
            for (int i = 0; i < parameterTypes.Length; i++)
                builder.Append(parameterTypes[i].Descriptor);
            builder.Append(ListClose).Append(returnType.Descriptor);
            return new SurtrClassReference(builder.ToString());
        }

        /// <summary>Builds a reference to a Surtr object type by full name (for example <c>game.core:Entity.Handle</c>).</summary>
        public static SurtrClassReference Object(string fullName)
            => new(SymbolObject + fullName + NameTerminator);

        /// <summary>Builds a reference to a host-defined native type by full name.</summary>
        public static SurtrClassReference Native(string fullName)
            => new(SymbolNative + fullName + NameTerminator);

        /// <summary>
        /// Builds a reference to a constructed generic type: a full name whose last segment carries
        /// its arity, followed by that many argument descriptors.
        /// </summary>
        /// <remarks>
        /// The caller supplies the mangled name (<c>box:Box`1</c>), because the arity belongs to
        /// the declaration and this only writes what it is told. Passing a count that disagrees
        /// with the name produces a descriptor nothing can parse, so the two are checked here
        /// rather than at the first read.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// The arity mangled into <paramref name="fullName"/> does not match the number of
        /// arguments given.
        /// </exception>
        public static SurtrClassReference Constructed(string fullName, params SurtrClassReference[] typeArguments)
            => Constructed(SymbolObject, fullName, typeArguments);

        /// <summary>Builds a reference to a constructed generic host-defined native type.</summary>
        /// <exception cref="ArgumentException">
        /// The arity mangled into <paramref name="fullName"/> does not match the number of
        /// arguments given.
        /// </exception>
        public static SurtrClassReference ConstructedNative(string fullName, params SurtrClassReference[] typeArguments)
            => Constructed(SymbolNative, fullName, typeArguments);

        private static SurtrClassReference Constructed(char symbol, string fullName, SurtrClassReference[] typeArguments)
        {
            if (fullName is null)
                throw new ArgumentNullException(nameof(fullName));

            int declared = ArityOf(fullName);
            int supplied = typeArguments?.Length ?? 0;

            if (declared != supplied)
            {
                throw new ArgumentException(
                    $"'{fullName}' declares an arity of {declared} but {supplied} type argument(s) were given.",
                    nameof(typeArguments));
            }

            var builder = new StringBuilder();
            builder.Append(symbol).Append(fullName).Append(NameTerminator);

            for (int i = 0; i < supplied; i++)
                builder.Append(typeArguments![i].Descriptor);

            return new SurtrClassReference(builder.ToString());
        }

        /// <summary>
        /// Mangles an arity onto a type name segment, so <c>Box</c> with one parameter becomes
        /// <c>Box`1</c>. A non-generic name is returned unchanged.
        /// </summary>
        public static string MangleArity(string name, int arity)
        {
            if (arity < 0)
                throw new ArgumentOutOfRangeException(nameof(arity), arity, "An arity cannot be negative.");

            return arity == 0 ? name : name + ArityMarker + arity.ToString();
        }

        /// <summary>
        /// The arity mangled into a full name's last segment, or zero when it names a non-generic
        /// type. Only the last segment counts - the earlier ones are qualification.
        /// </summary>
        public static int ArityOf(string fullName)
        {
            int marker = fullName.LastIndexOf(ArityMarker);
            if (marker < 0 || marker < fullName.LastIndexOf(NameSeparator))
                return 0;

            int arity = 0;
            for (int i = marker + 1; i < fullName.Length; i++)
            {
                char digit = fullName[i];
                if (digit < '0' || digit > '9')
                    return 0;

                arity = (arity * 10) + (digit - '0');
            }

            return marker + 1 < fullName.Length ? arity : 0;
        }

        /// <summary>
        /// Builds a reference to the declaring type's <paramref name="index"/>-th generic
        /// parameter.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0-9.</exception>
        public static SurtrClassReference GenericParameter(int index)
        {
            if ((uint)index > 9)
                throw new ArgumentOutOfRangeException(nameof(index), index, "A generic parameter index must be a single digit.");

            return new SurtrClassReference(GenericParameterDescriptors[index]);
        }

        /// <summary>
        /// Builds a reference to the declaring method's <paramref name="index"/>-th generic
        /// parameter. Distinct from <see cref="GenericParameter"/> by its symbol, so a signature
        /// can never confuse one with the other - see <see cref="SymbolMethodGenericParameter"/>.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0-9.</exception>
        public static SurtrClassReference MethodGenericParameter(int index)
        {
            if ((uint)index > 9)
                throw new ArgumentOutOfRangeException(nameof(index), index, "A method generic parameter index must be a single digit.");

            return new SurtrClassReference(MethodGenericParameterDescriptors[index]);
        }

        /// <summary>Builds a reference to the nullable form of a primitive type.</summary>
        /// <exception cref="ArgumentException"><paramref name="primitiveType"/> is not a primitive.</exception>
        public static SurtrClassReference Nullable(SurtrClassReference primitiveType)
        {
            if (primitiveType.IsNullablePrimitive)
                return primitiveType;

            if (!primitiveType.TypeCode.IsPrimitive || primitiveType.Descriptor.Length != 1)
                throw new ArgumentException(
                    $"Only a primitive can be made nullable; '{primitiveType.Descriptor}' is not one. A nullable reference needs no descriptor of its own.",
                    nameof(primitiveType));

            return new SurtrClassReference(SymbolNullable + primitiveType.Descriptor);
        }

        /// <summary>The primitive underlying a nullable primitive reference, or this reference unchanged.</summary>
        public SurtrClassReference GetUnderlyingPrimitive()
            => IsNullablePrimitive ? new SurtrClassReference(Descriptor.Substring(1)) : this;

        /// <summary>Wraps an already-encoded descriptor without validating it. Intended for metadata loaded from trusted bytecode.</summary>
        public static SurtrClassReference FromDescriptor(string descriptor) => new(descriptor);

        /// <summary>
        /// The same type with every generic parameter rewritten to <see cref="SymbolErased"/>.
        /// </summary>
        /// <remarks>
        /// This is the form a signature key compares, so it is also the form a bridge method's
        /// parameters have to be declared in: a class implementing <c>IComparable&lt;Vec2&gt;</c>
        /// occupies a slot keyed on <c>compareTo(E)</c>, which nothing spelled <c>Vec2</c> can
        /// match. Returns the reference unchanged when it mentions no parameter, so the common case
        /// allocates nothing.
        /// </remarks>
        public static SurtrClassReference Erase(SurtrClassReference reference)
        {
            string descriptor = reference.Descriptor;

            // Either symbol makes a rewrite necessary - a method parameter is a parameter too.
            if (descriptor.IndexOf(SymbolGenericParameter) < 0
                && descriptor.IndexOf(SymbolMethodGenericParameter) < 0)
            {
                return reference;
            }

            var builder = new StringBuilder(descriptor.Length);
            AppendErased(builder, descriptor);
            return new SurtrClassReference(builder.ToString());
        }

        /// <summary>
        /// Appends a descriptor with every generic parameter rewritten to the erased symbol.
        /// </summary>
        /// <remarks>
        /// A single left-to-right pass, because <c>G</c> and <c>H</c> are always followed by
        /// exactly one digit and nothing else in the grammar can produce that pair - the same
        /// one-character-of-lookahead property the descriptor encoding is built on. Nested forms
        /// are covered for free: <c>AG0</c> becomes <c>AE</c> without the loop having to know it
        /// is inside an array.
        /// </remarks>
        internal static void AppendErased(StringBuilder builder, string descriptor)
        {
            for (int i = 0; i < descriptor.Length; i++)
            {
                char symbol = descriptor[i];

                if ((symbol == SymbolGenericParameter || symbol == SymbolMethodGenericParameter)
                    && i + 1 < descriptor.Length)
                {
                    builder.Append(SymbolErased);
                    i++;
                    continue;
                }

                builder.Append(symbol);

                // A full name runs to its terminator verbatim: a type called `G0` inside one is a
                // name, not a parameter, and must not be rewritten.
                if (symbol != SymbolObject && symbol != SymbolNative)
                    continue;

                int terminator = descriptor.IndexOf(NameTerminator, i + 1);
                int end = terminator < 0 ? descriptor.Length : terminator + 1;

                builder.Append(descriptor, i + 1, end - i - 1);
                i = end - 1;
            }
        }

        // Interned so a generic parameter reference never allocates: ten one-off strings against
        // a descriptor that appears in every element-polymorphic built-in signature.
        private static readonly string[] GenericParameterDescriptors =
        {
            "G0", "G1", "G2", "G3", "G4", "G5", "G6", "G7", "G8", "G9",
        };

        // The method-level twin, kept separate so a signature key cannot confuse the two.
        private static readonly string[] MethodGenericParameterDescriptors =
        {
            "H0", "H1", "H2", "H3", "H4", "H5", "H6", "H7", "H8", "H9",
        };
        #endregion

        #region Composite Accessors
        /// <summary>The element type of an array reference. Only meaningful when <see cref="TypeCode"/> is <see cref="SurtrValueTypeCode.Array"/>.</summary>
        public SurtrClassReference GetArrayElementType()
            => Slice(1, SkipDescriptor(Descriptor, 1));

        /// <summary>The key type of a dictionary reference. Only meaningful when <see cref="TypeCode"/> is <see cref="SurtrValueTypeCode.Dictionary"/>.</summary>
        public SurtrClassReference GetDictionaryKeyType()
            => Slice(1, SkipDescriptor(Descriptor, 1));

        /// <summary>The value type of a dictionary reference. Only meaningful when <see cref="TypeCode"/> is <see cref="SurtrValueTypeCode.Dictionary"/>.</summary>
        public SurtrClassReference GetDictionaryValueType()
        {
            string descriptor = Descriptor;
            int valueStart = SkipDescriptor(descriptor, 1);
            return Slice(valueStart, SkipDescriptor(descriptor, valueStart));
        }

        /// <summary>The element types of a tuple reference, in order. Only meaningful when <see cref="TypeCode"/> is <see cref="SurtrValueTypeCode.Tuple"/>.</summary>
        public SurtrClassReference[] GetTupleElementTypes()
            => ReadList(Descriptor, 2, out _);

        /// <summary>The parameter types of a closure reference, in order. Only meaningful when <see cref="TypeCode"/> is <see cref="SurtrValueTypeCode.Closure"/>.</summary>
        public SurtrClassReference[] GetClosureParameterTypes()
            => ReadList(Descriptor, 2, out _);

        /// <summary>The return type of a closure reference. Only meaningful when <see cref="TypeCode"/> is <see cref="SurtrValueTypeCode.Closure"/>.</summary>
        public SurtrClassReference GetClosureReturnType()
        {
            string descriptor = Descriptor;
            ReadList(descriptor, 2, out int afterList);
            return Slice(afterList, SkipDescriptor(descriptor, afterList));
        }

        /// <summary>
        /// Extracts the full name from an object or native reference (without the leading symbol
        /// or the trailing <see cref="NameTerminator"/>).
        /// </summary>
        /// <returns><see langword="true"/> if this reference carries a full name.</returns>
        public bool TryGetFullName(out string fullName)
        {
            string descriptor = Descriptor;
            if (descriptor.Length > 1 && (descriptor[0] == SymbolObject || descriptor[0] == SymbolNative))
            {
                int end = descriptor.IndexOf(NameTerminator, 1);
                if (end > 1)
                {
                    fullName = descriptor.Substring(1, end - 1);
                    return true;
                }
            }

            fullName = string.Empty;
            return false;
        }

        /// <summary>
        /// How many type arguments this reference carries: the arity mangled into its full name's
        /// last segment, or zero for anything that is not a constructed object or native type.
        /// </summary>
        public int GenericArity
        {
            get
            {
                string descriptor = Descriptor;
                if (descriptor.Length <= 1 || (descriptor[0] != SymbolObject && descriptor[0] != SymbolNative))
                    return 0;

                return SkipFullName(descriptor, 1, out int arity) < 0 ? 0 : arity;
            }
        }

        /// <summary>
        /// The type arguments this reference supplies, empty when it names a non-generic type.
        /// </summary>
        /// <remarks>
        /// Diagnostics and host interop only. Nothing on an execution path needs these - two
        /// constructions of one declaration resolve to the same <see cref="SurtrClass"/>, so the
        /// arguments change no layout, no dispatch and no tracing.
        /// </remarks>
        public SurtrClassReference[] GetTypeArguments()
        {
            string descriptor = Descriptor;
            if (descriptor.Length <= 1 || (descriptor[0] != SymbolObject && descriptor[0] != SymbolNative))
                return System.Array.Empty<SurtrClassReference>();

            int index = SkipFullName(descriptor, 1, out int arity);
            if (index < 0 || arity == 0)
                return System.Array.Empty<SurtrClassReference>();

            var arguments = new SurtrClassReference[arity];
            for (int i = 0; i < arity; i++)
            {
                int end = SkipDescriptor(descriptor, index);
                if (end < 0)
                    return System.Array.Empty<SurtrClassReference>();

                arguments[i] = Slice(index, end);
                index = end;
            }

            return arguments;
        }

        /// <summary>
        /// Splits a full name into its module path and its type path (the type name plus any
        /// nested type names, still dot-separated).
        /// </summary>
        /// <returns><see langword="true"/> if <paramref name="fullName"/> contains a <see cref="ModuleSeparator"/>.</returns>
        public static bool TrySplitFullName(string fullName, out string modulePath, out string typePath)
        {
            int separator = fullName.IndexOf(ModuleSeparator);
            if (separator < 0)
            {
                modulePath = string.Empty;
                typePath = fullName;
                return false;
            }

            modulePath = fullName.Substring(0, separator);
            typePath = fullName.Substring(separator + 1);
            return true;
        }
        #endregion

        #region Parsing
        /// <summary>
        /// Returns the index just past the descriptor that starts at <paramref name="index"/>, or
        /// <c>-1</c> if the descriptor is malformed.
        /// </summary>
        /// <remarks>
        /// Recursive, but only as deep as the type nesting itself, which is tiny in practice.
        /// This is the single primitive every composite accessor is built on.
        /// </remarks>
        public static int SkipDescriptor(string descriptor, int index)
        {
            if ((uint)index >= (uint)descriptor.Length)
                return -1;

            switch (descriptor[index])
            {
                case SymbolInteger:
                case SymbolFloat:
                case SymbolBoolean:
                case SymbolCharacter:
                case SymbolString:
                case SymbolRange:
                case SymbolErased:
                case SymbolVoid:
                    return index + 1;

                case SymbolArray:
                    return SkipDescriptor(descriptor, index + 1);

                case SymbolDictionary:
                {
                    int afterKey = SkipDescriptor(descriptor, index + 1);
                    return afterKey < 0 ? -1 : SkipDescriptor(descriptor, afterKey);
                }

                case SymbolTuple:
                {
                    int afterList = SkipList(descriptor, index + 1);
                    return afterList;
                }

                case SymbolClosure:
                {
                    int afterList = SkipList(descriptor, index + 1);
                    return afterList < 0 ? -1 : SkipDescriptor(descriptor, afterList);
                }

                case SymbolObject:
                case SymbolNative:
                {
                    int afterName = SkipFullName(descriptor, index + 1, out int arity);
                    if (afterName < 0)
                        return -1;

                    // The arity mangled into the last name segment says how many argument
                    // descriptors follow, so the list needs neither brackets nor a count and this
                    // stays one left-to-right pass.
                    for (int i = 0; i < arity; i++)
                    {
                        afterName = SkipDescriptor(descriptor, afterName);
                        if (afterName < 0)
                            return -1;
                    }

                    return afterName;
                }

                case SymbolGenericParameter:
                case SymbolMethodGenericParameter:
                {
                    // Exactly one digit has to follow, which is what keeps this fixed-width.
                    if ((uint)(index + 1) >= (uint)descriptor.Length)
                        return -1;

                    int digit = descriptor[index + 1] - '0';
                    return (uint)digit <= 9 ? index + 2 : -1;
                }

                case SymbolNullable:
                {
                    if ((uint)(index + 1) >= (uint)descriptor.Length)
                        return -1;

                    // Only a primitive may be made nullable - see SymbolNullable.
                    return CodeOf(descriptor[index + 1]).IsPrimitive ? index + 2 : -1;
                }

                default:
                    return -1;
            }
        }

        /// <summary>Whether <paramref name="descriptor"/> is a single well-formed descriptor with nothing trailing it.</summary>
        public static bool IsWellFormed(string descriptor)
            => !string.IsNullOrEmpty(descriptor) && SkipDescriptor(descriptor, 0) == descriptor.Length;

        /// <summary>
        /// Skips a full name and reports the arity mangled into its last segment.
        /// </summary>
        /// <remarks>
        /// Only the last segment counts. The earlier ones are qualification - a type nested inside
        /// a generic one does not see its container's parameters (<c>docs/Compiler-Plan.md</c> §8),
        /// so <c>Box`1.Entry</c> names an <c>Entry</c> of arity zero and takes no arguments. The
        /// arity resets at every <see cref="NameSeparator"/>, which is what makes that one pass.
        /// </remarks>
        private static int SkipFullName(string descriptor, int index, out int arity)
        {
            arity = 0;

            while (index < descriptor.Length)
            {
                char symbol = descriptor[index];

                if (symbol == NameTerminator)
                    return index + 1;

                if (symbol == NameSeparator)
                {
                    arity = 0;
                    index++;
                    continue;
                }

                if (symbol == ArityMarker)
                {
                    index++;

                    int digits = 0;
                    while (index < descriptor.Length && descriptor[index] >= '0' && descriptor[index] <= '9')
                    {
                        arity = (arity * 10) + (descriptor[index] - '0');
                        index++;
                        digits++;
                    }

                    // A marker with no digits after it is malformed, not an arity of zero.
                    if (digits == 0)
                    {
                        arity = 0;
                        return -1;
                    }

                    continue;
                }

                index++;
            }

            arity = 0;
            return -1;
        }

        private static int SkipList(string descriptor, int index)
        {
            if ((uint)index >= (uint)descriptor.Length || descriptor[index] != ListOpen)
                return -1;

            index++;
            while (index < descriptor.Length && descriptor[index] != ListClose)
            {
                index = SkipDescriptor(descriptor, index);
                if (index < 0)
                    return -1;
            }

            return index < descriptor.Length ? index + 1 : -1;
        }

        private static SurtrClassReference[] ReadList(string descriptor, int index, out int afterList)
        {
            var items = new List<SurtrClassReference>();

            while (index < descriptor.Length && descriptor[index] != ListClose)
            {
                int end = SkipDescriptor(descriptor, index);
                if (end < 0)
                    break;

                items.Add(new SurtrClassReference(descriptor.Substring(index, end - index)));
                index = end;
            }

            afterList = index < descriptor.Length ? index + 1 : index;
            return items.ToArray();
        }

        private SurtrClassReference Slice(int start, int end)
        {
            string descriptor = Descriptor;
            if (start < 0 || end < 0 || end > descriptor.Length || start >= end)
                return default;
            return new SurtrClassReference(descriptor.Substring(start, end - start));
        }

        private static SurtrValueTypeCode CodeOf(char symbol) => symbol switch
        {
            SymbolInteger => SurtrValueTypeCode.Integer,
            SymbolFloat => SurtrValueTypeCode.Float,
            SymbolBoolean => SurtrValueTypeCode.Boolean,
            SymbolCharacter => SurtrValueTypeCode.Character,
            SymbolString => SurtrValueTypeCode.String,
            SymbolArray => SurtrValueTypeCode.Array,
            SymbolTuple => SurtrValueTypeCode.Tuple,
            SymbolDictionary => SurtrValueTypeCode.Dictionary,
            SymbolClosure => SurtrValueTypeCode.Closure,
            SymbolObject => SurtrValueTypeCode.Object,
            SymbolNative => SurtrValueTypeCode.Native,
            SymbolRange => SurtrValueTypeCode.Range,
            SymbolErased => SurtrValueTypeCode.Erased,
            SymbolVoid => SurtrValueTypeCode.Void,
            // A generic parameter is an erased slot that remembers which parameter it was: the
            // index is metadata for the compiler, the representation is the erased one. The
            // method-level one is the same slot and the same representation.
            SymbolGenericParameter => SurtrValueTypeCode.Erased,
            SymbolMethodGenericParameter => SurtrValueTypeCode.Erased,
            _ => SurtrValueTypeCode.Invalid,
        };
        #endregion

        #region Display
        /// <summary>
        /// Renders the descriptor as readable Surtr-like source syntax, for diagnostics and
        /// debugging. Never use this as a key or for comparisons - <see cref="Descriptor"/> is
        /// the canonical form.
        /// </summary>
        public string ToDisplayString()
        {
            var builder = new StringBuilder();
            AppendDisplay(builder, Descriptor, 0);
            return builder.ToString();
        }

        private static int AppendDisplay(StringBuilder builder, string descriptor, int index)
        {
            if ((uint)index >= (uint)descriptor.Length)
                return -1;

            switch (descriptor[index])
            {
                case SymbolInteger: builder.Append("int"); return index + 1;
                case SymbolFloat: builder.Append("float"); return index + 1;
                case SymbolBoolean: builder.Append("bool"); return index + 1;
                case SymbolCharacter: builder.Append("char"); return index + 1;
                case SymbolString: builder.Append("string"); return index + 1;
                case SymbolRange: builder.Append("range"); return index + 1;
                // `unknown` rather than a bare '?', which now means nullable and would read as
                // two different things in the same sentence.
                case SymbolErased: builder.Append("unknown"); return index + 1;
                case SymbolVoid: builder.Append("void"); return index + 1;

                case SymbolGenericParameter:
                case SymbolMethodGenericParameter:
                {
                    // No name to print: the descriptor carries the position, and the declaring
                    // type - which is what knows the name - is not reachable from here.
                    if ((uint)(index + 1) >= (uint)descriptor.Length)
                        return -1;
                    builder.Append('T').Append(descriptor[index + 1]);
                    return index + 2;
                }

                case SymbolNullable:
                {
                    int next = AppendDisplay(builder, descriptor, index + 1);
                    builder.Append('?');
                    return next;
                }

                case SymbolArray:
                {
                    int next = AppendDisplay(builder, descriptor, index + 1);
                    builder.Append("[]");
                    return next;
                }

                case SymbolDictionary:
                {
                    builder.Append('{');
                    int afterKey = AppendDisplay(builder, descriptor, index + 1);
                    builder.Append(": ");
                    int afterValue = AppendDisplay(builder, descriptor, afterKey);
                    builder.Append('}');
                    return afterValue;
                }

                case SymbolTuple:
                {
                    builder.Append('(');
                    int next = AppendDisplayList(builder, descriptor, index + 2);
                    builder.Append(')');
                    return next;
                }

                case SymbolClosure:
                {
                    builder.Append('(');
                    int afterList = AppendDisplayList(builder, descriptor, index + 2);
                    builder.Append(") -> ");
                    return AppendDisplay(builder, descriptor, afterList);
                }

                case SymbolObject:
                case SymbolNative:
                {
                    int end = descriptor.IndexOf(NameTerminator, index + 1);
                    if (end < 0)
                        return -1;

                    // The mangled arity is machinery, not something to read: print `Box<int>`,
                    // never ``Box`1<int>``.
                    for (int i = index + 1; i < end; i++)
                    {
                        char symbol = descriptor[i];
                        if (symbol == ArityMarker)
                        {
                            while (i + 1 < end && descriptor[i + 1] >= '0' && descriptor[i + 1] <= '9')
                                i++;

                            continue;
                        }

                        builder.Append(symbol);
                    }

                    int next = SkipFullName(descriptor, index + 1, out int arity);
                    if (next < 0)
                        return -1;

                    if (arity == 0)
                        return next;

                    builder.Append('<');
                    for (int i = 0; i < arity; i++)
                    {
                        if (i > 0)
                            builder.Append(", ");

                        next = AppendDisplay(builder, descriptor, next);
                        if (next < 0)
                            return -1;
                    }

                    builder.Append('>');
                    return next;
                }

                default:
                    builder.Append('?');
                    return -1;
            }
        }

        private static int AppendDisplayList(StringBuilder builder, string descriptor, int index)
        {
            bool first = true;
            while (index < descriptor.Length && descriptor[index] != ListClose)
            {
                if (!first)
                    builder.Append(", ");
                first = false;

                index = AppendDisplay(builder, descriptor, index);
                if (index < 0)
                    return -1;
            }

            return index < descriptor.Length ? index + 1 : index;
        }

        /// <inheritdoc/>
        public override string ToString() => Descriptor;
        #endregion

        #region Equality
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(SurtrClassReference other)
            => string.Equals(_descriptor, other._descriptor, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is SurtrClassReference other && Equals(other);

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int GetHashCode() => _descriptor?.GetHashCode() ?? 0;

        /// <summary>Compares two references by their canonical descriptor text.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(SurtrClassReference left, SurtrClassReference right) => left.Equals(right);

        /// <summary>Compares two references by their canonical descriptor text.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(SurtrClassReference left, SurtrClassReference right) => !left.Equals(right);
        #endregion
    }
}
