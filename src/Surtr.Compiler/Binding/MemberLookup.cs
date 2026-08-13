#nullable enable

using Surtr.Compiler.Binding.Symbols;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Finds a member on a type, following base classes and interfaces, and reading it as the
    /// receiver's own type arguments make it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A member is declared once, on the open generic, so <c>Box&lt;T&gt;.get()</c> is one symbol
    /// no matter how many constructions exist — that is what erasure means and it is deliberate.
    /// What the <em>compiler</em> owes on top is the substituted view: on a <c>Box&lt;int&gt;</c>
    /// receiver, <c>get()</c> returns <c>int</c> and <c>set("x")</c> is a type error. This builds
    /// that view once per construction and caches it.
    /// </para>
    /// <para>
    /// The walk goes base classes first, then interfaces, because a class member is what a call
    /// binds to and a contract only says one must exist.
    /// </para>
    /// <para>
    /// A composite — <c>int[]</c>, <c>{string: int}</c>, a tuple, a closure — is not a
    /// <see cref="NamedTypeSymbol"/> and carries no members of its own, because one
    /// <c>SurtrBuiltIns.Array</c> stands behind every array parameterisation. So the lookup pairs
    /// the two: <c>int[]</c> walks the <c>array</c> class <em>constructed with</em> <c>int</c>,
    /// which is what makes <c>xs.push(3)</c> take an <c>int</c> and <c>xs.push("x")</c> a type
    /// error, against metadata alone.
    /// </para>
    /// </remarks>
    public sealed class MemberLookup
    {
        private static readonly IReadOnlyList<MethodSymbol> NoMethods = Array.Empty<MethodSymbol>();

        private readonly TypeSymbolFactory _factory;
        private readonly MetadataImporter _importer;

        private readonly Dictionary<NamedTypeSymbol, IReadOnlyList<Symbol>> _substituted =
            new Dictionary<NamedTypeSymbol, IReadOnlyList<Symbol>>();

        /// <summary>Creates a lookup over one compilation's types.</summary>
        /// <param name="factory">The factory every substituted type is interned through.</param>
        /// <param name="importer">
        /// Where the built-in collection classes come from, since a composite type's members live
        /// on one of those rather than on the composite itself.
        /// </param>
        public MemberLookup(TypeSymbolFactory factory, MetadataImporter importer)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        }

        /// <summary>
        /// Every method of that name reachable on the type, nearest declaration first, with a base
        /// declaration an override already covers left out.
        /// </summary>
        /// <remarks>
        /// The hiding rule, and it has to be here rather than in overload resolution: an override
        /// has by definition the same signature as what it replaces, so both reaching a call site
        /// would make every call on a derived receiver ambiguous. Nearest-first is what makes
        /// keeping the first one the right answer.
        /// </remarks>
        public IReadOnlyList<MethodSymbol> FindMethods(TypeSymbol type, string name)
        {
            List<MethodSymbol>? found = null;

            foreach (var member in Reachable(type))
            {
                if (member is not MethodSymbol method || !string.Equals(method.Name, name, StringComparison.Ordinal))
                    continue;

                if (found is null)
                {
                    found = new List<MethodSymbol> { method };
                    continue;
                }

                if (!IsHidden(found, method))
                    found.Add(method);
            }

            return found ?? NoMethods;
        }

        private static bool IsHidden(List<MethodSymbol> found, MethodSymbol candidate)
        {
            foreach (var nearer in found)
            {
                if (nearer.Parameters.Count != candidate.Parameters.Count)
                    continue;

                bool same = true;
                for (int i = 0; i < nearer.Parameters.Count && same; i++)
                    same = ReferenceEquals(nearer.Parameters[i].Type, candidate.Parameters[i].Type);

                if (same)
                    return true;
            }

            return false;
        }

        /// <summary>The nearest field of that name, or <see langword="null"/>.</summary>
        public FieldSymbol? FindField(TypeSymbol type, string name)
        {
            foreach (var member in Reachable(type))
            {
                if (member is FieldSymbol field && string.Equals(field.Name, name, StringComparison.Ordinal))
                    return field;
            }

            return null;
        }

        /// <summary>The nearest property of that name, or <see langword="null"/>.</summary>
        public PropertySymbol? FindProperty(TypeSymbol type, string name)
        {
            foreach (var member in Reachable(type))
            {
                if (member is PropertySymbol property && string.Equals(property.Name, name, StringComparison.Ordinal))
                    return property;
            }

            return null;
        }

        /// <summary>
        /// Every member reachable on a type, in lookup order: its own first, then its bases, then
        /// the contracts it satisfies.
        /// </summary>
        public IEnumerable<Symbol> Reachable(TypeSymbol type)
        {
            if (BackingType(type.NonNullable) is not NamedTypeSymbol named)
                yield break;

            var seen = new HashSet<NamedTypeSymbol>();
            var queue = new Queue<NamedTypeSymbol>();
            queue.Enqueue(named);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!seen.Add(current))
                    continue;

                foreach (var member in MembersOf(current))
                    yield return member;

                if (current.BaseType is NamedTypeSymbol baseType)
                    queue.Enqueue(baseType);

                foreach (var contract in current.Interfaces)
                    queue.Enqueue(contract);
            }
        }

        /// <summary>
        /// The named type a lookup actually walks: itself for anything declared, and the one
        /// built-in class behind a composite, constructed with the composite's own parameters.
        /// </summary>
        /// <remarks>
        /// <c>tuple</c> and <c>closure</c> take no arguments, since both are parameterised by a
        /// <em>list</em> whose length varies per value — so what is reachable on one is the thin
        /// surface the class declares, and element access stays the statically typed
        /// <c>TupGet</c>.
        /// </remarks>
        public NamedTypeSymbol? BackingType(TypeSymbol type)
        {
            switch (type.NonNullable)
            {
                case NamedTypeSymbol named:
                    return named;

                case ArrayTypeSymbol array:
                    return Construct(_importer.ArrayType, array.ElementType);

                case DictionaryTypeSymbol dictionary:
                    return Construct(_importer.DictionaryType, dictionary.KeyType, dictionary.ValueType);

                case TupleTypeSymbol:
                    return _importer.TupleType;

                case ClosureTypeSymbol:
                    return _importer.ClosureType;

                default:
                    return null;
            }
        }

        private static NamedTypeSymbol Construct(NamedTypeSymbol definition, params TypeSymbol[] arguments)
            => definition.Arity == arguments.Length ? definition.Construct(arguments) : definition;

        /// <summary>The members of a type as its own type arguments make them.</summary>
        public IReadOnlyList<Symbol> MembersOf(NamedTypeSymbol type)
        {
            if (!type.IsConstructed)
                return type.Members;

            if (_substituted.TryGetValue(type, out var cached))
                return cached;

            var substitution = type.SubstitutionFromArguments(_factory);
            if (substitution.IsEmpty)
                return type.Members;

            var members = new List<Symbol>(type.Members.Count);
            foreach (var member in type.Members)
                members.Add(Substitute(member, type, substitution));

            _substituted.Add(type, members);
            return members;
        }

        private Symbol Substitute(Symbol member, NamedTypeSymbol owner, TypeSubstitution substitution)
        {
            switch (member)
            {
                case FieldSymbol field:
                {
                    var type = substitution.Apply(field.Type);
                    if (ReferenceEquals(type, field.Type))
                        return field;

                    return new FieldSymbol(field.Name, owner, type)
                    {
                        IsStatic = field.IsStatic,
                        IsReadOnly = field.IsReadOnly,
                        Accessibility = field.Accessibility,
                        IsSynthetic = field.IsSynthetic,
                        ImportedFrom = field.ImportedFrom,
                    };
                }

                case PropertySymbol property:
                {
                    var type = substitution.Apply(property.Type);
                    if (ReferenceEquals(type, property.Type))
                        return property;

                    return new PropertySymbol(property.Name, owner, type)
                    {
                        IsStatic = property.IsStatic,
                        Accessibility = property.Accessibility,
                        Getter = property.Getter is null ? null : (MethodSymbol)Substitute(property.Getter, owner, substitution),
                        Setter = property.Setter is null ? null : (MethodSymbol)Substitute(property.Setter, owner, substitution),
                    };
                }

                case MethodSymbol method:
                    return Substitute(method, owner, substitution);

                default:
                    return member;
            }
        }

        private MethodSymbol Substitute(MethodSymbol method, NamedTypeSymbol owner, TypeSubstitution substitution)
        {
            var returnType = substitution.Apply(method.ReturnType);

            bool changed = !ReferenceEquals(returnType, method.ReturnType);
            var parameters = new ParameterSymbol[method.Parameters.Count];

            for (int i = 0; i < parameters.Length; i++)
            {
                var original = method.Parameters[i];
                var type = substitution.Apply(original.Type);
                changed |= !ReferenceEquals(type, original.Type);
                parameters[i] = original;

                if (!ReferenceEquals(type, original.Type))
                {
                    parameters[i] = new ParameterSymbol(original.Name, type, i)
                    {
                        HasDefaultValue = original.HasDefaultValue,
                        IsVararg = original.IsVararg,
                    };
                }
            }

            if (!changed)
                return method;

            var substituted = new MethodSymbol(method.Name, owner, returnType)
            {
                IsStatic = method.IsStatic,
                Accessibility = method.Accessibility,
                Dispatch = method.Dispatch,
                Role = method.Role,
                IsOverride = method.IsOverride,
                IsSealed = method.IsSealed,
                IsNative = method.IsNative,
                IsInline = method.IsInline,
                IsForceInline = method.IsForceInline,
                IsConst = method.IsConst,
                IsSynthetic = method.IsSynthetic,
                IsConversion = method.IsConversion,
                TypeParameters = method.TypeParameters,
                Parameters = parameters,

                // Carried across, or an `int[]`'s `push` would be a symbol no call site could
                // emit: a substituted view is still the same method table entry.
                ImportedFrom = method.ImportedFrom,
            };

            return substituted;
        }
    }
}
