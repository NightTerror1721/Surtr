#nullable enable

using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Catches overloads no signature could tell apart (§3.5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method is found by name plus parameters, and the parameters it is found by are the
    /// <em>emitted</em> ones — so this compares what the descriptor would say, not what the source
    /// wrote. Three things collapse on the way, and each one is a pair of overloads that would
    /// otherwise collide in a real method table with nothing left to diagnose it:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>A type parameter</b> erases, so <c>f(Box&lt;T&gt;)</c> and <c>f(Box&lt;U&gt;)</c> inside
    /// one generic type are the same signature — Java's "same erasure", from the same cause.
    /// </description></item>
    /// <item><description>
    /// <b>A reference's nullability</b> is not in the descriptor, so <c>f(Foo)</c> and
    /// <c>f(Foo?)</c> are the same. A nullable <em>primitive</em> is a distinct type and stays one.
    /// </description></item>
    /// <item><description>
    /// <b>A value class</b> erases to the field it wraps (§2.9), so <c>f(EntityId)</c> and
    /// <c>f(int)</c> are the same.
    /// </description></item>
    /// </list>
    /// <para>
    /// An alias needs no rule of its own: §2.7 makes it resolve to its target, so two members
    /// differing only by one already arrive here as the same types.
    /// </para>
    /// <para>
    /// A conversion is the exception in the other direction. <c>operator as</c> is overloaded on
    /// what it converts <em>to</em>, which a signature key excludes, so the return type joins the
    /// key here exactly as the target descriptor joins the name at emit.
    /// </para>
    /// </remarks>
    internal sealed class SignatureSet
    {
        private readonly TypeSymbolFactory _factory;
        private readonly SurtrDiagnosticBag _diagnostics;
        private readonly string _sourceName;

        private readonly Dictionary<Signature, MethodSymbol> _seen =
            new Dictionary<Signature, MethodSymbol>(SignatureComparer.Instance);

        internal SignatureSet(TypeSymbolFactory factory, SurtrDiagnosticBag diagnostics, string sourceName)
        {
            _factory = factory;
            _diagnostics = diagnostics;
            _sourceName = sourceName;
        }

        /// <summary>Records a method, reporting it if something already occupies its signature.</summary>
        internal void Add(MethodSymbol method, SourceSpan span)
        {
            var parameters = new TypeSymbol[method.Parameters.Count];
            for (int i = 0; i < parameters.Length; i++)
                parameters[i] = Erase(method.Parameters[i].Type);

            var signature = new Signature(
                method.Name,
                parameters,
                method.IsConversion ? Erase(method.ReturnType) : null);

            if (_seen.ContainsKey(signature))
            {
                _diagnostics.ReportError(
                    SurtrDiagnosticCode.DuplicateOverload,
                    $"'{method.ToDisplayString()}' has the same signature as an overload already declared here.",
                    _sourceName,
                    span);

                return;
            }

            _seen.Add(signature, method);
        }

        private TypeSymbol Erase(TypeSymbol type)
        {
            switch (type)
            {
                case TypeParameterSymbol:
                    return _factory.Unknown;

                case ArrayTypeSymbol array:
                    return Reference(_factory.Array(Erase(array.ElementType)));

                case DictionaryTypeSymbol dictionary:
                    return Reference(_factory.Dictionary(Erase(dictionary.KeyType), Erase(dictionary.ValueType)));

                case TupleTypeSymbol tuple:
                    return Reference(_factory.Tuple(EraseAll(tuple.ElementTypes)));

                case ClosureTypeSymbol closure:
                    return Reference(_factory.Closure(EraseAll(closure.ParameterTypes), Erase(closure.ReturnType)));

                case NamedTypeSymbol { TypeKind: TypeSymbolKind.ValueClass } valueClass:
                {
                    // Erased to the field it wraps, and the nullability rides along: `EntityId?`
                    // over an int is `int?`, which is a distinct type and must stay one.
                    var underlying = valueClass.UnderlyingType;
                    if (underlying is null)
                        return _factory.ErrorType;

                    var erased = Erase(underlying);
                    return erased.IsReferenceType
                        ? erased.NonNullable
                        : erased.WithNullability(erased.IsNullable || valueClass.IsNullable);
                }

                case NamedTypeSymbol named when named.IsConstructed:
                    return Reference(named.Definition.Construct(EraseAll(named.TypeArguments)));

                default:
                    return type.IsReferenceType ? type.NonNullable : type;
            }
        }

        // A reference's nullability never reaches a descriptor, so two signatures differing only by
        // it are one signature.
        private static TypeSymbol Reference(TypeSymbol type) => type.NonNullable;

        private TypeSymbol[] EraseAll(IReadOnlyList<TypeSymbol> types)
        {
            var erased = new TypeSymbol[types.Count];
            for (int i = 0; i < types.Count; i++)
                erased[i] = Erase(types[i]);

            return erased;
        }

        private readonly struct Signature
        {
            internal Signature(string name, TypeSymbol[] parameters, TypeSymbol? conversionTarget)
            {
                Name = name;
                Parameters = parameters;
                ConversionTarget = conversionTarget;
            }

            internal string Name { get; }

            internal TypeSymbol[] Parameters { get; }

            internal TypeSymbol? ConversionTarget { get; }
        }

        private sealed class SignatureComparer : IEqualityComparer<Signature>
        {
            internal static readonly SignatureComparer Instance = new SignatureComparer();

            public bool Equals(Signature x, Signature y)
            {
                if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal)
                    || x.Parameters.Length != y.Parameters.Length
                    || !ReferenceEquals(x.ConversionTarget, y.ConversionTarget))
                {
                    return false;
                }

                for (int i = 0; i < x.Parameters.Length; i++)
                {
                    if (!ReferenceEquals(x.Parameters[i], y.Parameters[i]))
                        return false;
                }

                return true;
            }

            public int GetHashCode(Signature obj)
            {
                var hash = new HashCode();
                hash.Add(obj.Name, StringComparer.Ordinal);

                for (int i = 0; i < obj.Parameters.Length; i++)
                    hash.Add(obj.Parameters[i]);

                if (obj.ConversionTarget is not null)
                    hash.Add(obj.ConversionTarget);

                return hash.ToHashCode();
            }
        }
    }
}
