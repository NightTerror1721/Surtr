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

        private readonly Dictionary<Signature, MethodSymbol> _seen =
            new Dictionary<Signature, MethodSymbol>(SignatureComparer.Instance);

        internal SignatureSet(TypeSymbolFactory factory, SurtrDiagnosticBag diagnostics)
        {
            _factory = factory;
            _diagnostics = diagnostics;
        }

        /// <summary>Records a method, reporting it if something already occupies its signature.</summary>
        /// <param name="method">The method to record.</param>
        /// <param name="sourceName">Which source it was declared in — a module's members span several files.</param>
        /// <param name="span">The range of source the method was declared over.</param>
        internal void Add(MethodSymbol method, string sourceName, SourceSpan span)
        {
            // An instance operator's first parameter is its receiver (§5.6), which the runtime
            // keeps implicit — so its signature key excludes it, exactly as a method's does, and
            // two operators differing only in the receiver would collide in the real method table
            // with nothing here to catch it.
            int first = method.Role == MethodRole.Operator && !method.IsStatic ? 1 : 0;

            var parameters = new TypeSymbol[method.Parameters.Count - first];
            for (int i = first; i < method.Parameters.Count; i++)
                parameters[i - first] = Erase(method.Parameters[i].Type);

            // §5.x: a constructor's `each` clause is part of its identity — `constructor(int)` and
            // `constructor(int) each (item: T)` are two overloads — so the each parameters (erased)
            // join the key exactly as the ordinary ones do. Nothing else has an each clause.
            TypeSymbol[]? each = null;
            if (method.Role == MethodRole.Constructor && method.EachParameters is not null)
            {
                each = new TypeSymbol[method.EachParameters.Count];
                for (int i = 0; i < each.Length; i++)
                    each[i] = Erase(method.EachParameters[i].Type);
            }

            var signature = new Signature(
                method.Name,
                parameters,
                method.IsConversion ? Erase(method.ReturnType) : null,
                each);

            if (_seen.ContainsKey(signature))
            {
                _diagnostics.ReportError(
                    SurtrDiagnosticCode.DuplicateOverload,
                    $"'{method.ToDisplayString()}' has the same signature as an overload already declared here.",
                    sourceName,
                    span);

                return;
            }

            _seen.Add(signature, method);
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> and <paramref name="required"/> occupy the same
        /// vtable slot: same name, same erased parameter types (an instance operator's receiver
        /// excluded, since the runtime keeps it implicit), and — unlike the runtime's own key, which
        /// excludes it — the same erased return type.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The runtime's <c>SignatureKey</c> is deliberately return-blind (<c>docs/VM-Plan.md</c>
        /// §4.1), and it erases <c>G&lt;n&gt;</c>, so an override whose parameter <em>shape</em>
        /// matches but whose types differ — <c>get(int): T</c> where the contract, read as the
        /// construction the class declares, is <c>get(int): int</c> — links cleanly with nothing
        /// downstream to notice. That is the check this adds: the same <see cref="Erase"/> view
        /// <see cref="Add"/> compares overloads with, plus the return type the linker cannot see.
        /// </para>
        /// </remarks>
        internal bool Matches(MethodSymbol candidate, MethodSymbol required)
        {
            if (!string.Equals(candidate.Name, required.Name, StringComparison.Ordinal)
                || candidate.Parameters.Count != required.Parameters.Count)
            {
                return false;
            }

            int first = candidate.Role == MethodRole.Operator && !candidate.IsStatic ? 1 : 0;
            for (int i = first; i < candidate.Parameters.Count; i++)
            {
                if (!ReferenceEquals(Erase(candidate.Parameters[i].Type), Erase(required.Parameters[i].Type)))
                    return false;
            }

            return ReferenceEquals(Erase(candidate.ReturnType), Erase(required.ReturnType));
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> occupies the same vtable slot as
        /// <paramref name="required"/>, matching what the runtime's <c>SignatureKey</c> will decide:
        /// name plus erased parameter types, <em>no</em> return type.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>SignatureKey</c> (<c>Runtime-Model.md</c> §4.1) deliberately excludes the return type —
        /// it is what the linker's <c>PlaceInVTable</c> matches an override on, and an override that
        /// shared only the return with a base slot would still occupy that slot in runtime. So a
        /// derived member that shares name and parameter shape with a base <c>virtual</c> must be
        /// told apart here <em>without</em> the return, or the binding would admit two "different"
        /// methods that the linker then collapses into one slot (and rejects at load).
        /// </para>
        /// <para>
        /// This is deliberately a <em>different</em> question from <see cref="Matches"/>, which keeps
        /// the return so an interface implementation with the wrong return type is still reported.
        /// A contract is a promise about the value handed back; a vtable slot is just a place.
        /// </para>
        /// </remarks>
        internal bool MatchesSlot(MethodSymbol candidate, MethodSymbol required)
        {
            if (!string.Equals(candidate.Name, required.Name, StringComparison.Ordinal)
                || candidate.Parameters.Count != required.Parameters.Count)
            {
                return false;
            }

            int first = candidate.Role == MethodRole.Operator && !candidate.IsStatic ? 1 : 0;
            for (int i = first; i < candidate.Parameters.Count; i++)
            {
                if (!ReferenceEquals(Erase(candidate.Parameters[i].Type), Erase(required.Parameters[i].Type)))
                    return false;
            }

            return true;
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

                case NamedTypeSymbol { TypeKind: TypeSymbolKind.Enum }:
                    // An enum is a nominal value class from the migration (§2.4): its descriptor is
                    // its own, so `f(Perm)` and `f(int)` are two real method table slots. What
                    // erases — for the erased-signature contract slots — is the generic parameter,
                    // not an enum's own name.
                    return Reference(type);

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
            internal Signature(string name, TypeSymbol[] parameters, TypeSymbol? conversionTarget, TypeSymbol[]? eachParameters)
            {
                Name = name;
                Parameters = parameters;
                ConversionTarget = conversionTarget;
                EachParameters = eachParameters;
            }

            internal string Name { get; }

            internal TypeSymbol[] Parameters { get; }

            internal TypeSymbol? ConversionTarget { get; }

            /// <summary>The erased each-clause parameters of a constructor, or <see langword="null"/>.</summary>
            internal TypeSymbol[]? EachParameters { get; }
        }

        private sealed class SignatureComparer : IEqualityComparer<Signature>
        {
            internal static readonly SignatureComparer Instance = new SignatureComparer();

            public bool Equals(Signature x, Signature y)
            {
                if (!string.Equals(x.Name, y.Name, StringComparison.Ordinal)
                    || x.Parameters.Length != y.Parameters.Length
                    || !ReferenceEquals(x.ConversionTarget, y.ConversionTarget)
                    || !SameEach(x.EachParameters, y.EachParameters))
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

                if (obj.EachParameters is not null)
                {
                    for (int i = 0; i < obj.EachParameters.Length; i++)
                        hash.Add(obj.EachParameters[i]);
                }

                return hash.ToHashCode();
            }

            private static bool SameEach(TypeSymbol[]? x, TypeSymbol[]? y)
            {
                if (ReferenceEquals(x, y))
                    return true;

                if (x is null || y is null || x.Length != y.Length)
                    return false;

                for (int i = 0; i < x.Length; i++)
                {
                    if (!ReferenceEquals(x[i], y[i]))
                        return false;
                }

                return true;
            }
        }
    }
}
