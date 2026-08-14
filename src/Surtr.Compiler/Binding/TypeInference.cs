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
        internal static bool TryInfer(
            IReadOnlyList<TypeParameterSymbol> parameters,
            IReadOnlyList<TypeSymbol> declared,
            IReadOnlyList<TypeSymbol?> supplied,
            TypeSymbolFactory factory,
            out TypeSymbol[] arguments,
            out TypeParameterSymbol? unresolved)
        {
            arguments = new TypeSymbol[parameters.Count];
            unresolved = null;

            var found = new Dictionary<TypeParameterSymbol, TypeSymbol>();

            for (int i = 0; i < declared.Count && i < supplied.Count; i++)
            {
                if (supplied[i] is TypeSymbol actual && !actual.IsError)
                    Unify(declared[i], actual, found, factory);
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
            TypeSymbolFactory factory)
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
                    Unify(array.ElementType, suppliedArray.ElementType, found, factory);
                    return;

                case DictionaryTypeSymbol dictionary when actual is DictionaryTypeSymbol suppliedDictionary:
                    Unify(dictionary.KeyType, suppliedDictionary.KeyType, found, factory);
                    Unify(dictionary.ValueType, suppliedDictionary.ValueType, found, factory);
                    return;

                case TupleTypeSymbol tuple when actual is TupleTypeSymbol suppliedTuple:
                    for (int i = 0; i < tuple.ElementTypes.Count && i < suppliedTuple.ElementTypes.Count; i++)
                        Unify(tuple.ElementTypes[i], suppliedTuple.ElementTypes[i], found, factory);

                    return;

                case ClosureTypeSymbol closure when actual is ClosureTypeSymbol suppliedClosure:
                    for (int i = 0; i < closure.ParameterTypes.Count && i < suppliedClosure.ParameterTypes.Count; i++)
                        Unify(closure.ParameterTypes[i], suppliedClosure.ParameterTypes[i], found, factory);

                    Unify(closure.ReturnType, suppliedClosure.ReturnType, found, factory);
                    return;

                case NamedTypeSymbol named when named.IsConstructed && actual is NamedTypeSymbol suppliedNamed:
                {
                    // `Box<T>` against a `Box<int>` argument: the same declaration on both sides is
                    // what makes matching the arguments positionally meaningful. Generics are
                    // invariant (§6), so a different declaration says nothing about T.
                    if (!ReferenceEquals(named.Definition, suppliedNamed.Definition))
                        return;

                    for (int i = 0; i < named.TypeArguments.Count && i < suppliedNamed.TypeArguments.Count; i++)
                        Unify(named.TypeArguments[i], suppliedNamed.TypeArguments[i], found, factory);

                    return;
                }
            }
        }
    }
}
