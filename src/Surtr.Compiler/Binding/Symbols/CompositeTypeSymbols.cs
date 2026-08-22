#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>An array type, written <c>T[]</c> (§5.9).</summary>
    public sealed class ArrayTypeSymbol : TypeSymbol
    {
        internal ArrayTypeSymbol(TypeSymbol elementType, bool isNullable = false) : base(isNullable)
            => ElementType = elementType;

        /// <summary>The element type.</summary>
        public TypeSymbol ElementType { get; }

        private string? _name;

        /// <inheritdoc/>
        public override string Name => _name ??= ToDisplayString();

        /// <inheritdoc/>
        public override TypeSymbolKind TypeKind => TypeSymbolKind.Array;

        /// <inheritdoc/>
        public override bool IsReferenceType => true;

        /// <inheritdoc/>
        public override bool ContainsTypeParameter => ElementType.ContainsTypeParameter;

        /// <inheritdoc/>
        public override TypeSymbol Substitute(TypeSubstitution substitution)
        {
            var element = ElementType.Substitute(substitution);
            return ReferenceEquals(element, ElementType)
                ? this
                : substitution.Factory.Array(element).WithNullability(IsNullable);
        }

        /// <inheritdoc/>
        private protected override TypeSymbol CreateDual() => new ArrayTypeSymbol(ElementType, !IsNullable);

        /// <inheritdoc/>
        public override string ToDisplayString() => WithNullableSuffix(ElementType.ToDisplayString() + "[]");
    }

    /// <summary>A dictionary type, written <c>{K: V}</c> (§5.4).</summary>
    public sealed class DictionaryTypeSymbol : TypeSymbol
    {
        internal DictionaryTypeSymbol(TypeSymbol keyType, TypeSymbol valueType, bool isNullable = false)
            : base(isNullable)
        {
            KeyType = keyType;
            ValueType = valueType;
        }

        /// <summary>The key type.</summary>
        public TypeSymbol KeyType { get; }

        /// <summary>The value type.</summary>
        public TypeSymbol ValueType { get; }

        private string? _name;

        /// <inheritdoc/>
        public override string Name => _name ??= ToDisplayString();

        /// <inheritdoc/>
        public override TypeSymbolKind TypeKind => TypeSymbolKind.Dictionary;

        /// <inheritdoc/>
        public override bool IsReferenceType => true;

        /// <inheritdoc/>
        public override bool ContainsTypeParameter => KeyType.ContainsTypeParameter || ValueType.ContainsTypeParameter;

        /// <inheritdoc/>
        public override TypeSymbol Substitute(TypeSubstitution substitution)
        {
            var key = KeyType.Substitute(substitution);
            var value = ValueType.Substitute(substitution);

            return ReferenceEquals(key, KeyType) && ReferenceEquals(value, ValueType)
                ? this
                : substitution.Factory.Dictionary(key, value).WithNullability(IsNullable);
        }

        /// <inheritdoc/>
        private protected override TypeSymbol CreateDual() => new DictionaryTypeSymbol(KeyType, ValueType, !IsNullable);

        /// <inheritdoc/>
        public override string ToDisplayString()
            => WithNullableSuffix("{" + KeyType.ToDisplayString() + ": " + ValueType.ToDisplayString() + "}");
    }

    /// <summary>A tuple type, written <c>(A, B)</c> (§5.5).</summary>
    public sealed class TupleTypeSymbol : TypeSymbol
    {
        internal TupleTypeSymbol(IReadOnlyList<TypeSymbol> elementTypes, bool isNullable = false) : base(isNullable)
            => ElementTypes = elementTypes;

        /// <summary>The element types, in order.</summary>
        public IReadOnlyList<TypeSymbol> ElementTypes { get; }

        private string? _name;

        /// <inheritdoc/>
        public override string Name => _name ??= ToDisplayString();

        /// <inheritdoc/>
        public override TypeSymbolKind TypeKind => TypeSymbolKind.Tuple;

        /// <inheritdoc/>
        public override bool IsReferenceType => true;

        /// <inheritdoc/>
        public override bool ContainsTypeParameter
        {
            get
            {
                for (int i = 0; i < ElementTypes.Count; i++)
                {
                    if (ElementTypes[i].ContainsTypeParameter)
                        return true;
                }

                return false;
            }
        }

        /// <inheritdoc/>
        public override TypeSymbol Substitute(TypeSubstitution substitution)
        {
            var substituted = TypeSubstitution.SubstituteAll(ElementTypes, substitution);
            return substituted is null
                ? this
                : substitution.Factory.Tuple(substituted).WithNullability(IsNullable);
        }

        /// <inheritdoc/>
        private protected override TypeSymbol CreateDual() => new TupleTypeSymbol(ElementTypes, !IsNullable);

        /// <inheritdoc/>
        public override string ToDisplayString()
        {
            var builder = new StringBuilder("(");
            for (int i = 0; i < ElementTypes.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");
                builder.Append(ElementTypes[i].ToDisplayString());
            }

            return WithNullableSuffix(builder.Append(')').ToString());
        }
    }

    /// <summary>A closure type, written <c>(A, B) -&gt; R</c> (§5.3).</summary>
    public sealed class ClosureTypeSymbol : TypeSymbol
    {
        internal ClosureTypeSymbol(IReadOnlyList<TypeSymbol> parameterTypes, TypeSymbol returnType, bool isNullable = false)
            : base(isNullable)
        {
            ParameterTypes = parameterTypes;
            ReturnType = returnType;
        }

        /// <summary>The parameter types, in order.</summary>
        public IReadOnlyList<TypeSymbol> ParameterTypes { get; }

        /// <summary>The return type. This is the one position where <c>void</c> is legal.</summary>
        public TypeSymbol ReturnType { get; }

        private string? _name;

        /// <inheritdoc/>
        public override string Name => _name ??= ToDisplayString();

        /// <inheritdoc/>
        public override TypeSymbolKind TypeKind => TypeSymbolKind.Closure;

        /// <inheritdoc/>
        public override bool IsReferenceType => true;

        /// <inheritdoc/>
        public override bool ContainsTypeParameter
        {
            get
            {
                if (ReturnType.ContainsTypeParameter)
                    return true;

                for (int i = 0; i < ParameterTypes.Count; i++)
                {
                    if (ParameterTypes[i].ContainsTypeParameter)
                        return true;
                }

                return false;
            }
        }

        /// <inheritdoc/>
        public override TypeSymbol Substitute(TypeSubstitution substitution)
        {
            var parameters = TypeSubstitution.SubstituteAll(ParameterTypes, substitution);
            var returnType = ReturnType.Substitute(substitution);

            if (parameters is null && ReferenceEquals(returnType, ReturnType))
                return this;

            return substitution.Factory
                .Closure(parameters ?? ParameterTypes, returnType)
                .WithNullability(IsNullable);
        }

        /// <inheritdoc/>
        private protected override TypeSymbol CreateDual()
            => new ClosureTypeSymbol(ParameterTypes, ReturnType, !IsNullable);

        /// <inheritdoc/>
        public override string ToDisplayString()
        {
            var builder = new StringBuilder("(");
            for (int i = 0; i < ParameterTypes.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");
                builder.Append(ParameterTypes[i].ToDisplayString());
            }

            builder.Append(") -> ").Append(ReturnType.ToDisplayString());
            return WithNullableSuffix(builder.ToString());
        }
    }

    /// <summary>
    /// The result of a type reference that could not be resolved.
    /// </summary>
    /// <remarks>
    /// Binding a type never returns <see langword="null"/>. One unresolved name yields this, the
    /// diagnostic is reported at the name itself, and every later rule that touches the result
    /// stays quiet — which is what stops one typo from producing a screenful of consequences.
    /// </remarks>
    public sealed class ErrorTypeSymbol : TypeSymbol
    {
        internal ErrorTypeSymbol(string name, bool isNullable = false) : base(isNullable) => Name = name;

        /// <inheritdoc/>
        public override string Name { get; }

        /// <inheritdoc/>
        public override TypeSymbolKind TypeKind => TypeSymbolKind.Error;

        /// <inheritdoc/>
        public override bool IsReferenceType => true;

        /// <inheritdoc/>
        private protected override TypeSymbol CreateDual() => new ErrorTypeSymbol(Name, !IsNullable);

        /// <inheritdoc/>
        public override string ToDisplayString() => WithNullableSuffix(Name);
    }

    /// <summary>
    /// Compares type lists by element identity, so the factory can intern a tuple or a closure by
    /// its shape. Sound because every type is interned, which makes reference equality the same
    /// relation as type equality.
    /// </summary>
    internal sealed class TypeListComparer : IEqualityComparer<IReadOnlyList<TypeSymbol>>
    {
        internal static readonly TypeListComparer Instance = new TypeListComparer();

        private TypeListComparer()
        {
        }

        public bool Equals(IReadOnlyList<TypeSymbol> x, IReadOnlyList<TypeSymbol> y)
        {
            if (ReferenceEquals(x, y))
                return true;

            if (x is null || y is null || x.Count != y.Count)
                return false;

            for (int i = 0; i < x.Count; i++)
            {
                if (!ReferenceEquals(x[i], y[i]))
                    return false;
            }

            return true;
        }

        public int GetHashCode(IReadOnlyList<TypeSymbol> obj)
        {
            var hash = new HashCode();
            for (int i = 0; i < obj.Count; i++)
                hash.Add(obj[i]);

            return hash.ToHashCode();
        }
    }
}
