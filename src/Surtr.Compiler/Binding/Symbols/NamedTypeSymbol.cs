#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Surtr.Compiler.Syntax.Ast;

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>
    /// A type with a declaration and a name: a class, interface, enum, <c>value class</c>,
    /// <c>singleton</c> or host-declared native type — and also the primitives, <c>string</c>,
    /// <c>range</c>, <c>void</c> and <c>unknown</c>, since everything in Surtr is an object and
    /// those have members like anything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A generic type has three related symbols. The <see cref="Definition"/> is the declaration
    /// itself (<c>Box&lt;T&gt;</c>) and is the only one that owns declaration state. A
    /// <em>construction</em> (<c>Box&lt;int&gt;</c>) pairs that definition with type arguments and
    /// is interned per definition. A nullable form (<c>Box&lt;int&gt;?</c>) is the dual of either.
    /// Everything except the arguments and the nullability is read through the definition, so
    /// filling a type in during binding's second phase is done once and seen by all three.
    /// </para>
    /// <para>
    /// Arity is part of identity: <c>Box&lt;T&gt;</c> and <c>Box&lt;T, U&gt;</c> are unrelated
    /// types with unrelated declarations, which is why <see cref="MetadataName"/> mangles the arity
    /// into the name the runtime will see. See <c>docs/Compiler-Plan.md</c> §8.
    /// </para>
    /// </remarks>
    public sealed class NamedTypeSymbol : TypeSymbol
    {
        private static readonly IReadOnlyList<TypeSymbol> NoTypeArguments = Array.Empty<TypeSymbol>();
        private static readonly IReadOnlyList<TypeParameterSymbol> NoTypeParameters = Array.Empty<TypeParameterSymbol>();
        private static readonly IReadOnlyList<NamedTypeSymbol> NoInterfaces = Array.Empty<NamedTypeSymbol>();
        private static readonly IReadOnlyList<Symbol> NoMembers = Array.Empty<Symbol>();

        private readonly NamedTypeSymbol? _definition;
        private readonly IReadOnlyList<TypeSymbol> _typeArguments;

        // Declaration state. Only ever read or written through Definition, so a construction and
        // a nullable form see whatever the declaration was filled in with.
        private IReadOnlyList<TypeParameterSymbol> _typeParameters = NoTypeParameters;
        private IReadOnlyList<NamedTypeSymbol> _interfaces = NoInterfaces;
        private IReadOnlyList<Symbol> _members = NoMembers;
        private Dictionary<IReadOnlyList<TypeSymbol>, NamedTypeSymbol>? _constructions;

        // Cached per construction: a construction is interned per definition, so its argument
        // substitution is a pure function of the instance and never changes.
        private TypeSubstitution? _substitutionFromArguments;

        /// <summary>Creates a type definition.</summary>
        internal NamedTypeSymbol(
            string name,
            TypeSymbolKind typeKind,
            ModuleSymbol? containingModule,
            NamedTypeSymbol? containingType,
            SpecialType specialType = SpecialType.None)
            : base(isNullable: false)
        {
            Name = name;
            TypeKind = typeKind;
            SpecialType = specialType;
            ContainingModule = containingModule;
            ContainingType = containingType;
            _definition = null;
            _typeArguments = NoTypeArguments;
        }

        /// <summary>Creates a construction or a nullable form of <paramref name="definition"/>.</summary>
        private NamedTypeSymbol(NamedTypeSymbol definition, IReadOnlyList<TypeSymbol> typeArguments, bool isNullable)
            : base(isNullable)
        {
            Name = definition.Name;
            TypeKind = definition.TypeKind;
            SpecialType = definition.SpecialType;
            ContainingModule = definition.ContainingModule;
            ContainingType = definition.ContainingType;
            _definition = definition;
            _typeArguments = typeArguments;
        }

        /// <inheritdoc/>
        public override string Name { get; }

        /// <inheritdoc/>
        public override TypeSymbolKind TypeKind { get; }

        /// <inheritdoc/>
        public override SpecialType SpecialType { get; }

        /// <summary>The module this type is declared in, or <see langword="null"/> for a built-in.</summary>
        public ModuleSymbol? ContainingModule { get; }

        /// <summary>The type this one is nested in (§2.6), or <see langword="null"/> at module level.</summary>
        public NamedTypeSymbol? ContainingType { get; }

        /// <inheritdoc/>
        public override Symbol? ContainingSymbol => (Symbol?)ContainingType ?? ContainingModule;

        /// <summary>Who may name this type (§3.1).</summary>
        /// <remarks>
        /// <para>
        /// Read through the definition, so a construction and its nullable form answer what the
        /// declaration says rather than carrying a copy that could drift from it.
        /// </para>
        /// <para>
        /// Public by default rather than by §3.1's rule, because the default here is for types
        /// nobody declared in source — the built-ins and anything a host published. What source
        /// declares is set from the declaration, where §3.1's own defaults apply.
        /// </para>
        /// </remarks>
        public Accessibility Accessibility
        {
            get => _definition?.Accessibility ?? _accessibility;
            internal set => _accessibility = value;
        }

        private Accessibility _accessibility = Accessibility.Public;

        /// <summary>
        /// The declaration this type comes from: itself when it is the declaration, and the open
        /// generic when it is a construction or a nullable form.
        /// </summary>
        public NamedTypeSymbol Definition => _definition ?? this;

        /// <summary>Whether this symbol is the declaration rather than a construction or nullable form.</summary>
        public bool IsDefinition => _definition is null && !IsNullable;

        /// <inheritdoc/>
        public override bool IsReferenceType => SpecialType switch
        {
            SpecialType.Int or SpecialType.Float or SpecialType.Bool or SpecialType.Char => false,
            _ => true,
        };

        /// <summary>The type parameters this declaration takes, empty when it is not generic.</summary>
        public IReadOnlyList<TypeParameterSymbol> TypeParameters => Definition._typeParameters;

        /// <summary>The arguments this construction supplies, empty on a declaration.</summary>
        public IReadOnlyList<TypeSymbol> TypeArguments => _typeArguments;

        /// <summary>How many type parameters the declaration takes.</summary>
        public int Arity => Definition._typeParameters.Count;

        /// <summary>Whether the declaration takes type parameters at all.</summary>
        public bool IsGeneric => Arity > 0;

        /// <summary>Whether this symbol supplies type arguments.</summary>
        public bool IsConstructed => _typeArguments.Count > 0;

        /// <summary>The class this one extends, or <see langword="null"/>.</summary>
        public NamedTypeSymbol? BaseType
        {
            get => Definition._baseType;
            internal set => Definition._baseType = value;
        }

        private NamedTypeSymbol? _baseType;

        /// <summary>The interfaces this type declares that it implements.</summary>
        public IReadOnlyList<NamedTypeSymbol> Interfaces
        {
            get => Definition._interfaces;
            internal set => Definition._interfaces = value;
        }

        /// <summary>
        /// The default concrete class a collection literal target-typed to this interface builds,
        /// written as <c>interface IList&lt;T&gt; default List&lt;T&gt;</c>. Null for a class, enum,
        /// value class, singleton, or an interface without a declared default. Only meaningful on an
        /// interface's definition.
        /// </summary>
        public NamedTypeSymbol? DefaultBuilder
        {
            get => Definition._defaultBuilder;
            internal set => Definition._defaultBuilder = value;
        }

        private NamedTypeSymbol? _defaultBuilder;

        private readonly HashSet<string> _bridgeKeys = new(StringComparer.Ordinal);

        /// <summary>
        /// The erased slot keys this type's own synthesized bridges fill (§3.3), recorded by the
        /// emitter as it defines them. A subclass consults its ancestors' sets to know an
        /// interface obligation is already answered by inheritance and must not be bridged again.
        /// </summary>
        public IReadOnlyCollection<string> BridgeKeys => _bridgeKeys;

        internal void AddBridgeKey(string key) => _bridgeKeys.Add(key);

        internal bool HasBridgeKey(string key) => _bridgeKeys.Contains(key);

        /// <summary>
        /// The single field a <c>value class</c> wraps (§2.9), or <see langword="null"/> for every
        /// other kind. This is what the type erases to where its type is statically known.
        /// </summary>
        public TypeSymbol? UnderlyingType
        {
            get => Definition._underlyingType;
            internal set => Definition._underlyingType = value;
        }

        private TypeSymbol? _underlyingType;

        /// <summary>
        /// Whether this enum carries <c>@Flags</c> (§11), which makes its cases the integers
        /// <c>1 &lt;&lt; ordinal</c> and its values single <c>int</c> slots rather than references.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Set in the declaration phase off the written syntax, because the representation it
        /// chooses is needed by everything downstream of declaration — the member phase's signature
        /// keys, every descriptor, every opcode choice — and an attribute does not bind until
        /// bodies do.
        /// </para>
        /// <para>
        /// Always <see langword="false"/> for a type that is not an enum, and for an enum imported
        /// from another module: the mark erases with the representation (the descriptor is
        /// <c>I</c>), which is the same thing that happens to a <c>value class</c> across a module
        /// boundary.
        /// </para>
        /// </remarks>
        public bool IsFlagsEnum
        {
            get => Definition._isFlagsEnum;
            internal set => Definition._isFlagsEnum = value;
        }

        private bool _isFlagsEnum;

        /// <summary>
        /// The static field holding a <c>singleton</c>'s one instance (§2.8), or
        /// <see langword="null"/> for every other kind.
        /// </summary>
        /// <remarks>
        /// This is what lets the declaration's own name answer as a value: §1.1 puts type names and
        /// value names in separate namespaces, so <c>Registry</c> resolves as a type and then has to
        /// be read through here to become one.
        /// </remarks>
        public FieldSymbol? SingletonInstance
        {
            get => Definition._singletonInstance;
            internal set => Definition._singletonInstance = value;
        }

        private FieldSymbol? _singletonInstance;

        /// <summary>Whether the declaration is <c>sealed</c>, so nothing may extend it.</summary>
        public bool IsSealed
        {
            get => Definition._isSealed;
            internal set => Definition._isSealed = value;
        }

        private bool _isSealed;

        /// <summary>Whether the declaration is <c>abstract</c>, so nothing may instantiate it.</summary>
        public bool IsAbstract
        {
            get => Definition._isAbstract;
            internal set => Definition._isAbstract = value;
        }

        private bool _isAbstract;

        /// <summary>
        /// The declaration kinds this type may be applied to when it is used as an attribute (§11),
        /// or <see cref="SurtrAttributeTargets.None"/> for no restriction. Meaningless unless
        /// <see cref="IsAttribute"/>.
        /// </summary>
        public SurtrAttributeTargets AllowedAttributeTargets
        {
            get => Definition._allowedAttributeTargets;
            internal set => Definition._allowedAttributeTargets = value;
        }

        private SurtrAttributeTargets _allowedAttributeTargets;

        /// <summary>Whether this type was declared with the <c>attribute</c> keyword (§11).</summary>
        public bool IsAttribute
        {
            get => Definition._isAttribute;
            internal set => Definition._isAttribute = value;
        }

        private bool _isAttribute;

        /// <summary>
        /// Whether an <c>attribute</c> type's retention is <c>CompileTimeOnly</c> — checked and then
        /// discarded, never emitted into the compiled image. Meaningless unless
        /// <see cref="IsAttribute"/>; false (the default) means <c>Runtime</c>.
        /// </summary>
        public bool IsCompileTimeOnlyAttribute
        {
            get => Definition._isCompileTimeOnlyAttribute;
            internal set => Definition._isCompileTimeOnlyAttribute = value;
        }

        private bool _isCompileTimeOnlyAttribute;

        /// <summary>
        /// The members as declared. On a construction these are still written in terms of the
        /// declaration's type parameters — apply <see cref="SubstitutionFromArguments"/> to see
        /// them as this construction does.
        /// </summary>
        public IReadOnlyList<Symbol> Members
        {
            get => Definition._members;
            internal set => Definition._members = value;
        }

        /// <summary>The types declared inside this one (§2.6), which is qualification rather than containment.</summary>
        public IReadOnlyList<NamedTypeSymbol> NestedTypes
        {
            get => Definition._nestedTypes;
            internal set
            {
                Definition._nestedTypes = value;
                Definition._nestedByName = null;
            }
        }

        private IReadOnlyList<NamedTypeSymbol> _nestedTypes = Array.Empty<NamedTypeSymbol>();
        private Dictionary<string, List<NamedTypeSymbol>>? _nestedByName;

        /// <summary>
        /// The types nested here under a name. A list, because arity is part of identity, so one
        /// name may cover several declarations.
        /// </summary>
        public IReadOnlyList<NamedTypeSymbol> FindNestedTypes(string name)
        {
            var definition = Definition;

            if (definition._nestedByName is null)
            {
                definition._nestedByName = new Dictionary<string, List<NamedTypeSymbol>>(StringComparer.Ordinal);
                foreach (var nested in definition._nestedTypes)
                {
                    if (!definition._nestedByName.TryGetValue(nested.Name, out var bucket))
                    {
                        bucket = new List<NamedTypeSymbol>();
                        definition._nestedByName.Add(nested.Name, bucket);
                    }

                    bucket.Add(nested);
                }
            }

            return definition._nestedByName.TryGetValue(name, out var found)
                ? found
                : (IReadOnlyList<NamedTypeSymbol>)NoNestedTypes;
        }

        private static readonly IReadOnlyList<NamedTypeSymbol> NoNestedTypes = Array.Empty<NamedTypeSymbol>();

        /// <summary>Declares this type's type parameters, in order. Binding's declaration phase.</summary>
        internal void SetTypeParameters(IReadOnlyList<TypeParameterSymbol> typeParameters)
        {
            if (!IsDefinition)
                throw new InvalidOperationException("Type parameters belong to the declaration, not to a construction.");

            Definition._typeParameters = typeParameters;
        }

        /// <inheritdoc/>
        public override bool ContainsTypeParameter
        {
            get
            {
                for (int i = 0; i < _typeArguments.Count; i++)
                {
                    if (_typeArguments[i].ContainsTypeParameter)
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Pairs this construction's arguments with the declaration's parameters, so a member's
        /// declared types can be read as this construction sees them.
        /// </summary>
        public TypeSubstitution SubstitutionFromArguments(TypeSymbolFactory factory)
        {
            var parameters = TypeParameters;
            if (parameters.Count == 0 || _typeArguments.Count != parameters.Count)
                return factory.EmptySubstitution();

            if (_substitutionFromArguments is not null)
                return _substitutionFromArguments;

            var builder = new TypeSubstitutionBuilder(factory);
            for (int i = 0; i < parameters.Count; i++)
                builder.Add(parameters[i], _typeArguments[i]);

            return _substitutionFromArguments = builder.ToSubstitution();
        }

        /// <summary>
        /// Builds this declaration with the given type arguments, interned so the same arguments
        /// always give back the same symbol.
        /// </summary>
        /// <exception cref="ArgumentException">The argument count does not match the arity.</exception>
        public NamedTypeSymbol Construct(IReadOnlyList<TypeSymbol> typeArguments)
        {
            var definition = Definition;

            if (typeArguments.Count != definition.Arity)
            {
                throw new ArgumentException(
                    $"'{definition.Name}' takes {definition.Arity} type argument(s), not {typeArguments.Count}.",
                    nameof(typeArguments));
            }

            if (typeArguments.Count == 0)
                return definition;

            var constructions = definition._constructions ??=
                new Dictionary<IReadOnlyList<TypeSymbol>, NamedTypeSymbol>(TypeListComparer.Instance);

            if (constructions.TryGetValue(typeArguments, out var constructed))
                return constructed;

            var copy = new TypeSymbol[typeArguments.Count];
            for (int i = 0; i < typeArguments.Count; i++)
                copy[i] = typeArguments[i];

            constructed = new NamedTypeSymbol(definition, copy, isNullable: false);
            constructions.Add(copy, constructed);
            return constructed;
        }

        /// <inheritdoc/>
        public override TypeSymbol Substitute(TypeSubstitution substitution)
        {
            if (_typeArguments.Count == 0)
                return this;

            TypeSymbol[]? substituted = null;
            for (int i = 0; i < _typeArguments.Count; i++)
            {
                var argument = _typeArguments[i].Substitute(substitution);
                if (ReferenceEquals(argument, _typeArguments[i]) && substituted is null)
                    continue;

                if (substituted is null)
                {
                    substituted = new TypeSymbol[_typeArguments.Count];
                    for (int j = 0; j < i; j++)
                        substituted[j] = _typeArguments[j];
                }

                substituted[i] = argument;
            }

            if (substituted is null)
                return this;

            return Definition.Construct(substituted).WithNullability(IsNullable);
        }

        /// <summary>
        /// The name the runtime sees, with arity mangled in for a generic type. Backtick cannot
        /// appear in a Surtr identifier, so this can never collide with a declared name.
        /// </summary>
        public string MetadataName
        {
            get
            {
                int arity = Arity;
                return arity == 0 ? Name : Name + '`' + arity.ToString();
            }
        }

        /// <summary>
        /// The full name a descriptor carries: <c>modulePath ':' typeName ('.' nested)*</c>, with
        /// each segment's arity mangled in.
        /// </summary>
        public string FullMetadataName
        {
            get
            {
                var builder = new StringBuilder();
                builder.Append(ContainingModule?.Path ?? string.Empty).Append(':');
                AppendNestedName(builder, this);
                return builder.ToString();
            }
        }

        private static void AppendNestedName(StringBuilder builder, NamedTypeSymbol type)
        {
            if (type.ContainingType is not null)
            {
                AppendNestedName(builder, type.ContainingType);
                builder.Append('.');
            }

            builder.Append(type.MetadataName);
        }

        /// <inheritdoc/>
        private protected override TypeSymbol CreateDual()
            => new NamedTypeSymbol(Definition, _typeArguments, !IsNullable);

        /// <inheritdoc/>
        public override string ToDisplayString()
        {
            var parameters = TypeParameters;
            if (_typeArguments.Count == 0 && parameters.Count == 0)
                return WithNullableSuffix(Name);

            var builder = new StringBuilder(Name).Append('<');

            if (_typeArguments.Count > 0)
            {
                for (int i = 0; i < _typeArguments.Count; i++)
                {
                    if (i > 0)
                        builder.Append(", ");
                    builder.Append(_typeArguments[i].ToDisplayString());
                }
            }
            else
            {
                // An open declaration prints its parameter names, which is how it was written.
                for (int i = 0; i < parameters.Count; i++)
                {
                    if (i > 0)
                        builder.Append(", ");
                    builder.Append(parameters[i].Name);
                }
            }

            return WithNullableSuffix(builder.Append('>').ToString());
        }
    }
}
