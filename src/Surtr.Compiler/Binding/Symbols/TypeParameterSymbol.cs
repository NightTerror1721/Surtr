#nullable enable

using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>A generic type parameter, declared either by a type or by a method (§6).</summary>
    /// <remarks>
    /// <para>
    /// Which symbol declares it matters at emit, not only for scoping. A type's parameter is
    /// written <c>G&lt;n&gt;</c>, which names the declaring type's n-th parameter and is what lets a
    /// member signature mention its own container's element type. A <em>method</em>'s parameter has
    /// no such encoding — nothing at the call site knows which method's parameter list to index
    /// into — so it emits as the plain erased symbol.
    /// </para>
    /// <para>
    /// A type parameter is always a reference: an erased slot holds a reference by definition,
    /// which is the whole reason a primitive flowing into one has to be boxed.
    /// </para>
    /// </remarks>
    public sealed class TypeParameterSymbol : TypeSymbol
    {
        private static readonly IReadOnlyList<TypeSymbol> NoConstraints = Array.Empty<TypeSymbol>();

        private IReadOnlyList<TypeSymbol> _constraints = NoConstraints;

        internal TypeParameterSymbol(string name, Symbol containingSymbol, int ordinal, bool isNullable = false)
            : base(isNullable)
        {
            Name = name;
            ContainingSymbol = containingSymbol;
            Ordinal = ordinal;
        }

        /// <inheritdoc/>
        public override string Name { get; }

        /// <inheritdoc/>
        public override Symbol? ContainingSymbol { get; }

        /// <summary>Its position in the declaring symbol's parameter list.</summary>
        public int Ordinal { get; }

        /// <inheritdoc/>
        public override TypeSymbolKind TypeKind => TypeSymbolKind.TypeParameter;

        /// <inheritdoc/>
        public override bool IsReferenceType => true;

        /// <inheritdoc/>
        public override bool ContainsTypeParameter => true;

        /// <summary>Whether a method declares this parameter, rather than a type.</summary>
        public bool IsMethodTypeParameter => ContainingSymbol is MethodSymbol;

        /// <summary>
        /// The bounds written against it (<c>&lt;T : IComparable&lt;T&gt;&gt;</c>), combined with
        /// <c>&amp;</c> when there is more than one. Empty when unconstrained.
        /// </summary>
        public IReadOnlyList<TypeSymbol> Constraints
        {
            get => _constraints;
            internal set => _constraints = value;
        }

        /// <summary>The declaration-site variance written against it, invariant by default (§6).</summary>
        public TypeParameterVariance Variance { get; internal set; } = TypeParameterVariance.Invariant;

        /// <inheritdoc/>
        public override TypeSymbol Substitute(TypeSubstitution substitution)
        {
            // A substitution is keyed on the non-nullable parameter, since `T?` and `T` are the
            // same parameter with different nullability at the use site.
            var key = (TypeParameterSymbol)NonNullable;
            return substitution.TryGetValue(key, out var replacement)
                ? replacement.WithNullability(IsNullable || replacement.IsNullable)
                : this;
        }

        /// <inheritdoc/>
        private protected override TypeSymbol CreateDual()
            => new TypeParameterSymbol(Name, ContainingSymbol!, Ordinal, !IsNullable);

        /// <inheritdoc/>
        public override string ToDisplayString() => WithNullableSuffix(Name);
    }
}
