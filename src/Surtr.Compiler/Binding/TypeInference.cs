#nullable enable

using Surtr.Compiler.Binding.Symbols;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Works out what a type parameter was meant to be, by matching a declared type against the
    /// type actually supplied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One mechanism for two callers, because the question is the same one twice: a generic method
    /// call (<c>pick(1, 2)</c>) infers the method's parameters from its arguments, and a construction
    /// (<c>Box(5)</c>) infers the type's parameters from its constructor's. Neither is a full
    /// constraint solver — a declared type is walked structurally against the supplied one, and a
    /// parameter is bound the first time it is met.
    /// </para>
    /// <para>
    /// Deliberately no lower/upper bound lattice and no second pass. §5.9 already makes inference
    /// local and one-directional for ordinary types, and the same reasoning applies here: the cases a
    /// lattice would additionally solve are the ones where a reader could not predict the answer
    /// either, and the diagnostics it produces when it fails are famously hard to read. Where this
    /// cannot decide, the call site writes the type argument.
    /// </para>
    /// <para>
    /// A conflict — the same parameter met twice with different types — is not silently widened. It
    /// fails, and the call reports that nothing fills the parameter, which is what §3.5's rule 4
    /// asks for everywhere else: no silent pick.
    /// </para>
    /// </remarks>
    internal static class TypeInference
    {
        /// <summary>
        /// Infers one type argument per parameter from matched declared/supplied pairs.
        /// </summary>
        /// <param name="parameters">The type parameters to fill.</param>
        /// <param name="declared">The declared types, as written against those parameters.</param>
        /// <param name="supplied">
        /// What each declared type received; a null entry is a position that says nothing, such as an
        /// argument that failed to bind.
        /// </param>
        /// <param name="factory">Interns whatever the substitution rebuilds.</param>
        /// <param name="arguments">The inferred arguments, in parameter order, when every one was found.</param>
        /// <param name="unresolved">The first parameter nothing filled, when inference failed.</param>
        /// <param name="lookup">
        /// What maps a composite onto the built-in class behind it and what a name resolves an
        /// ancestor's interfaces through. Optional, because most callers infer from concrete,
        /// already-matching shapes; without it, a declared type reached only through an ancestor
        /// (an extension's own <c>T</c> declared against <c>IIterable&lt;T&gt;</c>, supplied an
        /// <c>int[]</c>) is left unfilled rather than resolved through the hierarchy.
        /// </param>
        internal static bool TryInfer(
            IReadOnlyList<TypeParameterSymbol> parameters,
            IReadOnlyList<TypeSymbol> declared,
            IReadOnlyList<TypeSymbol?> supplied,
            TypeSymbolFactory factory,
            out TypeSymbol[] arguments,
            out TypeParameterSymbol? unresolved,
            MemberLookup? lookup = null)
        {
            arguments = new TypeSymbol[parameters.Count];
            unresolved = null;

            var found = new Dictionary<TypeParameterSymbol, TypeSymbol>();

            for (int i = 0; i < declared.Count && i < supplied.Count; i++)
            {
                if (supplied[i] is TypeSymbol actual && !actual.IsError)
                    Unify(declared[i], actual, found, factory, lookup);
            }

            for (int i = 0; i < parameters.Count; i++)
            {
                if (!found.TryGetValue(parameters[i], out var inferred) || inferred.IsError)
                {
                    unresolved = parameters[i];
                    return false;
                }

                arguments[i] = inferred;
            }

            return true;
        }

        /// <summary>Builds the substitution the inferred arguments make.</summary>
        internal static TypeSubstitution Substitution(
            IReadOnlyList<TypeParameterSymbol> parameters,
            IReadOnlyList<TypeSymbol> arguments,
            TypeSymbolFactory factory)
        {
            var builder = new TypeSubstitutionBuilder(factory);
            for (int i = 0; i < parameters.Count && i < arguments.Count; i++)
                builder.Add(parameters[i], arguments[i]);

            return builder.ToSubstitution();
        }

        /// <summary>
        /// Matches a declared type against a supplied one, binding whatever type parameters the
        /// declared side mentions.
        /// </summary>
        /// <remarks>
        /// Nullability is dropped on both sides before comparing: <c>T</c> filled from a
        /// <c>string?</c> argument infers <c>string</c>, since <c>?</c> is a flow fact about a
        /// reference rather than a different type argument, and a nullable primitive reaching a
        /// generic slot boxes either way.
        /// </remarks>
        private static void Unify(
            TypeSymbol declared,
            TypeSymbol supplied,
            Dictionary<TypeParameterSymbol, TypeSymbol> found,
            TypeSymbolFactory factory,
            MemberLookup? lookup)
        {
            var bare = declared.NonNullable;
            var actual = supplied.NonNullable;

            switch (bare)
            {
                case TypeParameterSymbol parameter:
                    // First one wins, and a second, different answer leaves the parameter unfilled
                    // rather than picking between them.
                    if (found.TryGetValue(parameter, out var already))
                    {
                        if (!ReferenceEquals(already, actual))
                            found[parameter] = factory.ErrorType;

                        return;
                    }

                    found.Add(parameter, actual);
                    return;

                case ArrayTypeSymbol array when actual is ArrayTypeSymbol suppliedArray:
                    Unify(array.ElementType, suppliedArray.ElementType, found, factory, lookup);
                    return;

                case DictionaryTypeSymbol dictionary when actual is DictionaryTypeSymbol suppliedDictionary:
                    Unify(dictionary.KeyType, suppliedDictionary.KeyType, found, factory, lookup);
                    Unify(dictionary.ValueType, suppliedDictionary.ValueType, found, factory, lookup);
                    return;

                case TupleTypeSymbol tuple when actual is TupleTypeSymbol suppliedTuple:
                    for (int i = 0; i < tuple.ElementTypes.Count && i < suppliedTuple.ElementTypes.Count; i++)
                        Unify(tuple.ElementTypes[i], suppliedTuple.ElementTypes[i], found, factory, lookup);

                    return;

                case ClosureTypeSymbol closure when actual is ClosureTypeSymbol suppliedClosure:
                    for (int i = 0; i < closure.ParameterTypes.Count && i < suppliedClosure.ParameterTypes.Count; i++)
                        Unify(closure.ParameterTypes[i], suppliedClosure.ParameterTypes[i], found, factory, lookup);

                    Unify(closure.ReturnType, suppliedClosure.ReturnType, found, factory, lookup);
                    return;

                case NamedTypeSymbol named when named.IsConstructed:
                {
                    // `Box<T>` against a `Box<int>` argument matches directly; `IIterable<T>`
                    // against an `int[]` (or a class implementing `IIterable<string>` two levels
                    // up) does not, so the supplied side is walked up its own base/interface chain
                    // — through the built-in class behind a composite, same as
                    // Conversions.WalkForBase — looking for the ancestor sharing this declaration.
                    // Generics stay invariant (§6): only that one ancestor's own arguments feed T.
                    NamedTypeSymbol? start = actual is NamedTypeSymbol suppliedNamed
                        ? suppliedNamed
                        : lookup?.BackingType(actual);

                    if (start is null)
                        return;

                    var ancestor = FindMatchingAncestor(start, named.Definition, factory, new HashSet<NamedTypeSymbol>());
                    if (ancestor is null)
                        return;

                    for (int i = 0; i < named.TypeArguments.Count && i < ancestor.TypeArguments.Count; i++)
                        Unify(named.TypeArguments[i], ancestor.TypeArguments[i], found, factory, lookup);

                    return;
                }
            }
        }

        /// <summary>
        /// Finds the ancestor of <paramref name="from"/> — itself, a base, or an interface, at any
        /// depth — declared from <paramref name="targetDefinition"/>, substituted with the type
        /// arguments that ancestor is reached with.
        /// </summary>
        /// <remarks>
        /// Mirrors <c>Conversions.WalkForBase</c>, but matches on the declaration rather than on a
        /// fully constructed type: <c>Conversions</c> already knows both ends concretely (is this
        /// <c>int[]</c> an <c>IIterable&lt;int&gt;</c>?), while inference is looking for whichever
        /// construction of <c>IIterable&lt;T&gt;</c> the supplied type reaches, so it can read the
        /// element type back out of it.
        /// </remarks>
        private static NamedTypeSymbol? FindMatchingAncestor(
            NamedTypeSymbol from,
            NamedTypeSymbol targetDefinition,
            TypeSymbolFactory factory,
            HashSet<NamedTypeSymbol> seen)
        {
            if (!seen.Add(from))
                return null;

            if (ReferenceEquals(from.Definition, targetDefinition))
                return from;

            var substitution = from.SubstitutionFromArguments(factory);

            foreach (var contract in from.Interfaces)
            {
                var seenFrom = AsSeenFrom(contract, substitution);
                if (FindMatchingAncestor(seenFrom, targetDefinition, factory, seen) is NamedTypeSymbol found)
                    return found;
            }

            if (from.BaseType is NamedTypeSymbol baseType && !ReferenceEquals(baseType, from))
                return FindMatchingAncestor(AsSeenFrom(baseType, substitution), targetDefinition, factory, seen);

            return null;
        }

        private static NamedTypeSymbol AsSeenFrom(NamedTypeSymbol type, TypeSubstitution substitution)
            => substitution.IsEmpty ? type : (NamedTypeSymbol)type.Substitute(substitution);
    }
}
