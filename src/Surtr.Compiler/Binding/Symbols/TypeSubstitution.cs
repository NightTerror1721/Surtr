#nullable enable

using System.Collections.Generic;

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>
    /// A mapping from type parameters to the types supplied for them, used to read a generic
    /// declaration's members as one particular construction sees them.
    /// </summary>
    /// <remarks>
    /// Keyed on the parameter <em>symbol</em> rather than on its ordinal, because a generic method
    /// inside a generic class has two parameters that both sit at ordinal 0 and mean different
    /// things. It carries the factory because substituting rebuilds composites, and a rebuilt type
    /// has to come back interned or it would compare unequal to its own twin.
    /// </remarks>
    public sealed class TypeSubstitution
    {
        private readonly Dictionary<TypeParameterSymbol, TypeSymbol>? _map;

        internal TypeSubstitution(TypeSymbolFactory factory, Dictionary<TypeParameterSymbol, TypeSymbol>? map)
        {
            Factory = factory;
            _map = map;
        }

        /// <summary>The factory that interns whatever this substitution builds.</summary>
        public TypeSymbolFactory Factory { get; }

        /// <summary>Whether this substitution replaces nothing.</summary>
        public bool IsEmpty => _map is null || _map.Count == 0;

        /// <summary>A substitution that replaces nothing.</summary>
        public static TypeSubstitution Empty(TypeSymbolFactory factory) => new TypeSubstitution(factory, null);

        /// <summary>Applies this substitution to <paramref name="type"/>.</summary>
        public TypeSymbol Apply(TypeSymbol type) => IsEmpty ? type : type.Substitute(this);

        /// <summary>Looks up what a type parameter is replaced by.</summary>
        public bool TryGetValue(TypeParameterSymbol parameter, out TypeSymbol replacement)
        {
            if (_map is not null && _map.TryGetValue(parameter, out var found))
            {
                replacement = found;
                return true;
            }

            replacement = parameter;
            return false;
        }

        /// <summary>
        /// Substitutes a whole list, returning <see langword="null"/> when nothing changed so the
        /// caller can hand back the type it already had rather than interning an identical one.
        /// </summary>
        internal static TypeSymbol[]? SubstituteAll(IReadOnlyList<TypeSymbol> types, TypeSubstitution substitution)
        {
            TypeSymbol[]? substituted = null;

            for (int i = 0; i < types.Count; i++)
            {
                var replacement = types[i].Substitute(substitution);
                if (ReferenceEquals(replacement, types[i]) && substituted is null)
                    continue;

                if (substituted is null)
                {
                    substituted = new TypeSymbol[types.Count];
                    for (int j = 0; j < i; j++)
                        substituted[j] = types[j];
                }

                substituted[i] = replacement;
            }

            return substituted;
        }
    }

    /// <summary>Accumulates the pairs of a <see cref="TypeSubstitution"/>.</summary>
    public struct TypeSubstitutionBuilder
    {
        private readonly TypeSymbolFactory _factory;
        private Dictionary<TypeParameterSymbol, TypeSymbol>? _map;

        /// <summary>Starts an empty builder against the factory that will intern the results.</summary>
        public TypeSubstitutionBuilder(TypeSymbolFactory factory)
        {
            _factory = factory;
            _map = null;
        }

        /// <summary>Maps one type parameter to the type supplied for it.</summary>
        public void Add(TypeParameterSymbol parameter, TypeSymbol replacement)
        {
            _map ??= new Dictionary<TypeParameterSymbol, TypeSymbol>();
            _map[parameter] = replacement;
        }

        /// <summary>Freezes the pairs added so far into a substitution.</summary>
        public TypeSubstitution ToSubstitution() => new TypeSubstitution(_factory, _map);
    }
}
