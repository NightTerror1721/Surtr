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
    ///             | 'O' fullname ';'                   Surtr object type
    ///             | 'N' fullname ';'                   host-defined native type
    /// fullname   := modulePath ':' typeName ('.' nestedTypeName)*
    /// </code>
    /// <para>
    /// The <c>':'</c> in a full name separates the module path from the type path, so a resolver
    /// splits it in one pass instead of probing prefixes to find where the module ends.
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

        /// <summary>Descriptor symbol for <see cref="SurtrValueTypeCode.Object"/>, followed by a full name and <see cref="NameTerminator"/>.</summary>
        public const char SymbolObject = 'O';

        /// <summary>Descriptor symbol for <see cref="SurtrValueTypeCode.Native"/>, followed by a full name and <see cref="NameTerminator"/>.</summary>
        public const char SymbolNative = 'N';

        /// <summary>
        /// Descriptor symbol for <see cref="SurtrValueTypeCode.Void"/>. Only legal as the return
        /// descriptor of a closure descriptor - a parameter, field or element can never be void.
        /// </summary>
        public const char SymbolVoid = 'V';

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
            get => string.IsNullOrEmpty(_descriptor) ? SurtrValueTypeCode.Invalid : CodeOf(_descriptor![0]);
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

        /// <summary>Wraps an already-encoded descriptor without validating it. Intended for metadata loaded from trusted bytecode.</summary>
        public static SurtrClassReference FromDescriptor(string descriptor) => new(descriptor);
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
                    int end = descriptor.IndexOf(NameTerminator, index + 1);
                    return end < 0 ? -1 : end + 1;
                }

                default:
                    return -1;
            }
        }

        /// <summary>Whether <paramref name="descriptor"/> is a single well-formed descriptor with nothing trailing it.</summary>
        public static bool IsWellFormed(string descriptor)
            => !string.IsNullOrEmpty(descriptor) && SkipDescriptor(descriptor, 0) == descriptor.Length;

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
            SymbolVoid => SurtrValueTypeCode.Void,
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
                case SymbolVoid: builder.Append("void"); return index + 1;

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
                    builder.Append(descriptor, index + 1, end - index - 1);
                    return end + 1;
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
