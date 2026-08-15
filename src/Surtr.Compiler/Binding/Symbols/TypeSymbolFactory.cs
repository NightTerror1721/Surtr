#nullable enable

using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>
    /// Creates and interns every <see cref="TypeSymbol"/> in one compilation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Interning is what makes type identity reference identity: <c>int[]</c> asked for twice is
    /// one object, so comparing two types never walks a structure and a dictionary keyed on a type
    /// needs no custom comparer. Every path that builds a type — parsing a type reference,
    /// substituting a generic, importing metadata — goes through here, and nothing constructs a
    /// type symbol directly.
    /// </para>
    /// <para>
    /// One factory belongs to one compilation. Sharing one across compilations would let a symbol
    /// from one leak into another, and a symbol carries its declaration, so that is a stale-state
    /// bug rather than a saving.
    /// </para>
    /// </remarks>
    public sealed class TypeSymbolFactory
    {
        private readonly Dictionary<TypeSymbol, ArrayTypeSymbol> _arrays = new Dictionary<TypeSymbol, ArrayTypeSymbol>();

        private readonly Dictionary<TypeSymbol, Dictionary<TypeSymbol, DictionaryTypeSymbol>> _dictionaries =
            new Dictionary<TypeSymbol, Dictionary<TypeSymbol, DictionaryTypeSymbol>>();

        private readonly Dictionary<IReadOnlyList<TypeSymbol>, TupleTypeSymbol> _tuples =
            new Dictionary<IReadOnlyList<TypeSymbol>, TupleTypeSymbol>(TypeListComparer.Instance);

        private readonly Dictionary<TypeSymbol, Dictionary<IReadOnlyList<TypeSymbol>, ClosureTypeSymbol>> _closures =
            new Dictionary<TypeSymbol, Dictionary<IReadOnlyList<TypeSymbol>, ClosureTypeSymbol>>();

        private readonly Dictionary<string, ErrorTypeSymbol> _errors =
            new Dictionary<string, ErrorTypeSymbol>(StringComparer.Ordinal);

        /// <summary>Creates a factory with the built-in types already interned.</summary>
        public TypeSymbolFactory()
        {
            BuiltInModule = new ModuleSymbol("surtr");

            Int = Special("int", SpecialType.Int);
            Float = Special("float", SpecialType.Float);
            Bool = Special("bool", SpecialType.Bool);
            Char = Special("char", SpecialType.Char);
            String = Special("string", SpecialType.String);
            Range = Special("range", SpecialType.Range);
            Void = Special("void", SpecialType.Void);
            Unknown = Special("unknown", SpecialType.Unknown);

            ErrorType = new ErrorTypeSymbol("?");
        }

        /// <summary>The module the built-in types belong to, matching the runtime's <c>surtr</c> module.</summary>
        public ModuleSymbol BuiltInModule { get; }

        /// <summary>The <c>int</c> primitive.</summary>
        public NamedTypeSymbol Int { get; }

        /// <summary>The <c>float</c> primitive.</summary>
        public NamedTypeSymbol Float { get; }

        /// <summary>The <c>bool</c> primitive.</summary>
        public NamedTypeSymbol Bool { get; }

        /// <summary>The <c>char</c> primitive.</summary>
        public NamedTypeSymbol Char { get; }

        /// <summary>The built-in <c>string</c>.</summary>
        public NamedTypeSymbol String { get; }

        /// <summary>The built-in <c>range</c>.</summary>
        public NamedTypeSymbol Range { get; }

        /// <summary>The <c>void</c> marker, legal only as a return type.</summary>
        public NamedTypeSymbol Void { get; }

        /// <summary>The <c>unknown</c> type (§5.10), which shares the erased representation.</summary>
        public NamedTypeSymbol Unknown { get; }

        /// <summary>The type an unresolvable type reference binds to.</summary>
        public ErrorTypeSymbol ErrorType { get; }

        /// <summary>The array type with the given element type.</summary>
        public ArrayTypeSymbol Array(TypeSymbol elementType)
        {
            if (_arrays.TryGetValue(elementType, out var array))
                return array;

            array = new ArrayTypeSymbol(elementType);
            _arrays.Add(elementType, array);
            return array;
        }

        /// <summary>The dictionary type with the given key and value types.</summary>
        public DictionaryTypeSymbol Dictionary(TypeSymbol keyType, TypeSymbol valueType)
        {
            if (!_dictionaries.TryGetValue(keyType, out var byValue))
            {
                byValue = new Dictionary<TypeSymbol, DictionaryTypeSymbol>();
                _dictionaries.Add(keyType, byValue);
            }

            if (byValue.TryGetValue(valueType, out var dictionary))
                return dictionary;

            dictionary = new DictionaryTypeSymbol(keyType, valueType);
            byValue.Add(valueType, dictionary);
            return dictionary;
        }

        /// <summary>The tuple type with the given element types.</summary>
        public TupleTypeSymbol Tuple(IReadOnlyList<TypeSymbol> elementTypes)
        {
            if (_tuples.TryGetValue(elementTypes, out var tuple))
                return tuple;

            var copy = Copy(elementTypes);
            tuple = new TupleTypeSymbol(copy);
            _tuples.Add(copy, tuple);
            return tuple;
        }

        /// <summary>The closure type with the given parameter types and return type.</summary>
        public ClosureTypeSymbol Closure(IReadOnlyList<TypeSymbol> parameterTypes, TypeSymbol returnType)
        {
            if (!_closures.TryGetValue(returnType, out var byParameters))
            {
                byParameters = new Dictionary<IReadOnlyList<TypeSymbol>, ClosureTypeSymbol>(TypeListComparer.Instance);
                _closures.Add(returnType, byParameters);
            }

            if (byParameters.TryGetValue(parameterTypes, out var closure))
                return closure;

            var copy = Copy(parameterTypes);
            closure = new ClosureTypeSymbol(copy, returnType);
            byParameters.Add(copy, closure);
            return closure;
        }

        /// <summary>
        /// The error type carrying a name, so a diagnostic downstream can still say what was
        /// written. Interned per name.
        /// </summary>
        public ErrorTypeSymbol Error(string name)
        {
            if (_errors.TryGetValue(name, out var error))
                return error;

            error = new ErrorTypeSymbol(name);
            _errors.Add(name, error);
            return error;
        }

        /// <summary>
        /// Creates a type declaration. The result is the definition: its base type, interfaces,
        /// members and type parameters are filled in by binding's later phases.
        /// </summary>
        public NamedTypeSymbol DeclareType(
            string name,
            TypeSymbolKind typeKind,
            ModuleSymbol? containingModule = null,
            NamedTypeSymbol? containingType = null)
            => new NamedTypeSymbol(name, typeKind, containingModule, containingType);

        /// <summary>Creates a type parameter belonging to a type or to a generic method.</summary>
        public TypeParameterSymbol DeclareTypeParameter(string name, Symbol containingSymbol, int ordinal)
            => new TypeParameterSymbol(name, containingSymbol, ordinal);

        /// <summary>Starts a substitution that will intern whatever it rebuilds through this factory.</summary>
        public TypeSubstitutionBuilder BeginSubstitution() => new TypeSubstitutionBuilder(this);

        /// <summary>A substitution that replaces nothing.</summary>
        public TypeSubstitution EmptySubstitution() => TypeSubstitution.Empty(this);

        private NamedTypeSymbol Special(string name, SpecialType specialType)
            => new NamedTypeSymbol(name, TypeSymbolKind.Class, BuiltInModule, containingType: null, specialType);

        private static TypeSymbol[] Copy(IReadOnlyList<TypeSymbol> types)
        {
            var copy = new TypeSymbol[types.Count];
            for (int i = 0; i < types.Count; i++)
                copy[i] = types[i];

            return copy;
        }
    }
}
