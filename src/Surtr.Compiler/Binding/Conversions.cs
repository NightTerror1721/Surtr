#nullable enable

using Surtr.Compiler.Binding.Symbols;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>How one type reaches another.</summary>
    public enum ConversionKind
    {
        /// <summary>There is no conversion.</summary>
        None,

        /// <summary>The two types are the same.</summary>
        Identity,

        /// <summary>A value becoming its own nullable form, <c>T</c> to <c>T?</c> (§5.1).</summary>
        ImplicitNullable,

        /// <summary><c>int</c> to <c>float</c>, the one implicit numeric widening the language has.</summary>
        ImplicitNumeric,

        /// <summary>A derived type becoming a base class or an interface it implements.</summary>
        ImplicitReference,

        /// <summary>
        /// Anything becoming <c>unknown</c> or a generic parameter — a slot that holds a reference
        /// and nothing more.
        /// </summary>
        ImplicitErasure,

        /// <summary>A narrowing between primitives, which must be written.</summary>
        ExplicitNumeric,

        /// <summary>A base type becoming a derived one, checked at run time.</summary>
        ExplicitReference,

        /// <summary>Reading a concrete type back out of an erased slot, checked at run time.</summary>
        ExplicitErasure,

        /// <summary>An <c>operator as</c> the source type declares (§5.6).</summary>
        UserDefined,
    }

    /// <summary>One classified conversion.</summary>
    public readonly struct Conversion
    {
        private Conversion(ConversionKind kind, MethodSymbol? method)
        {
            Kind = kind;
            Method = method;
        }

        /// <summary>What kind of conversion it is.</summary>
        public ConversionKind Kind { get; }

        /// <summary>The <c>operator as</c> that performs it, for <see cref="ConversionKind.UserDefined"/>.</summary>
        public MethodSymbol? Method { get; }

        /// <summary>No conversion exists.</summary>
        public static Conversion None => new Conversion(ConversionKind.None, null);

        /// <summary>The types are the same.</summary>
        public static Conversion Identity => new Conversion(ConversionKind.Identity, null);

        /// <summary>Builds a conversion of a given kind.</summary>
        public static Conversion Of(ConversionKind kind) => new Conversion(kind, null);

        /// <summary>Builds a user-defined conversion through an <c>operator as</c>.</summary>
        public static Conversion User(MethodSymbol method) => new Conversion(ConversionKind.UserDefined, method);

        /// <summary>Whether a conversion exists at all.</summary>
        public bool Exists => Kind != ConversionKind.None;

        /// <summary>Whether the two types are the same.</summary>
        public bool IsIdentity => Kind == ConversionKind.Identity;

        /// <summary>
        /// Whether it happens without being written. A user-defined one never does: §5.6 makes
        /// <c>operator as</c> explicit-only, because overload resolution already has
        /// <c>int</c> → <c>float</c> as its hard case and letting user types join would turn
        /// ambiguity diagnostics into guesswork.
        /// </summary>
        public bool IsImplicit => Kind switch
        {
            ConversionKind.Identity => true,
            ConversionKind.ImplicitNullable => true,
            ConversionKind.ImplicitNumeric => true,
            ConversionKind.ImplicitReference => true,
            ConversionKind.ImplicitErasure => true,
            _ => false,
        };
    }

    /// <summary>
    /// Decides whether one type reaches another, and how.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything here follows from three decisions taken elsewhere. Generics are invariant
    /// <em>by default</em> (§6): a construction converts only to itself unless its declaration
    /// annotated a parameter <c>out</c>/<c>in</c>, in which case the annotation's direction — and
    /// nothing wider than reference conversion — relates two constructions of one declaration.
    /// There are no user-defined <em>implicit</em> conversions (§5.6), so the implicit set is fixed
    /// and small. And <c>unknown</c> is the erased slot with a surface name (§5.10), so it takes
    /// anything and gives nothing back without a cast.
    /// </para>
    /// <para>
    /// The error type converts both ways and silently, so one unresolved name does not produce a
    /// second diagnostic everywhere its value flows.
    /// </para>
    /// </remarks>
    public sealed class Conversions
    {
        private readonly TypeSymbolFactory _factory;
        private readonly MemberLookup? _lookup;
        private readonly HashSet<NamedTypeSymbol> _subtypeScratch = new HashSet<NamedTypeSymbol>();

        /// <summary>Creates a classifier over one compilation's types.</summary>
        /// <param name="factory">The factory every type is interned through.</param>
        /// <param name="lookup">
        /// What maps a composite onto the built-in class behind it, so <c>int[]</c> can be seen to
        /// satisfy <c>IIterable&lt;int&gt;</c>. Optional, because the classifier is testable on its
        /// own and every rule but that one is about named types already.
        /// </param>
        public Conversions(TypeSymbolFactory factory, MemberLookup? lookup = null)
        {
            _factory = factory;
            _lookup = lookup;
        }

        /// <summary>Whether a value of <paramref name="source"/> may be used where <paramref name="destination"/> is expected.</summary>
        public bool IsAssignable(TypeSymbol source, TypeSymbol destination)
            => IsImplicitlyConvertible(source, destination);

        /// <summary>
        /// Whether an implicit conversion exists — the cheap half of <see cref="Classify"/>, for the
        /// callers that only need to answer yes or no. It never descends into the explicit or
        /// user-defined half, which is dead work for an assignment check and the dominant cost of
        /// <see cref="Classify"/> for a type pair that does not convert.
        /// </summary>
        public bool IsImplicitlyConvertible(TypeSymbol source, TypeSymbol destination)
            => ClassifyImplicitOnly(source, destination).Exists;

        /// <summary>
        /// The implicit conversion between two types, or <see cref="Conversion.None"/> when only an
        /// explicit one exists. The fast half of <see cref="Classify"/>, for the callers — overload
        /// applicability, assignment, extension-method filtering — that never want the explicit or
        /// user-defined answer.
        /// </summary>
        public Conversion ClassifyImplicitOnly(TypeSymbol source, TypeSymbol destination)
        {
            if (ReferenceEquals(source, destination))
                return Conversion.Identity;

            // One bad name should report once, not everywhere its value goes.
            if (source.IsError || destination.IsError)
                return Conversion.Identity;

            // `never` is the bottom type: a throw can stand wherever a value of any type is
            // expected, and reaching it means the value is never produced.
            if (source.IsNever)
                return Conversion.Identity;

            if (source.IsVoid || destination.IsVoid)
                return Conversion.None;

            return ClassifyImplicit(source, destination);
        }

        /// <summary>How <paramref name="source"/> reaches <paramref name="destination"/>, if it does.</summary>
        public Conversion Classify(TypeSymbol source, TypeSymbol destination)
        {
            if (ReferenceEquals(source, destination))
                return Conversion.Identity;

            // One bad name should report once, not everywhere its value goes.
            if (source.IsError || destination.IsError)
                return Conversion.Identity;

            // `never` is the bottom type: a throw can stand wherever a value of any type is
            // expected, and reaching it means the value is never produced.
            if (source.IsNever)
                return Conversion.Identity;

            if (source.IsVoid || destination.IsVoid)
                return Conversion.None;

            var implicitConversion = ClassifyImplicit(source, destination);
            if (implicitConversion.Exists)
                return implicitConversion;

            return ClassifyExplicit(source, destination);
        }

        /// <summary>Whether <c>null</c> may be written where <paramref name="destination"/> is expected (§5.1).</summary>
        public bool AcceptsNull(TypeSymbol destination)
        {
            if (destination.IsError)
                return true;

            // An erased slot holds a reference, and a reference is already able to be null.
            if (destination.SpecialType == SpecialType.Unknown || destination.TypeKind == TypeSymbolKind.TypeParameter)
                return true;

            return destination.IsNullable;
        }

        /// <summary>
        /// Whether <paramref name="derived"/> is <paramref name="baseType"/> or something below it,
        /// through base classes, interfaces, or a type parameter's own bounds.
        /// </summary>
        /// <remarks>
        /// A type parameter is above exactly what its bounds promise (§6): inside
        /// <c>Node&lt;T : IComparable&lt;T&gt;&gt;</c>, writing <c>Node&lt;T&gt;</c> in a member of
        /// its own declaration asks whether the bare parameter satisfies
        /// <c>IComparable&lt;T&gt;</c> — and §6's answer is yes, because that is precisely what the
        /// bound promises every construction will satisfy. Without this walk the question read as a
        /// flat no and every self-referencing use of a constrained generic failed its bounds check.
        /// </remarks>
        public bool IsSubtype(TypeSymbol derived, TypeSymbol baseType)
        {
            if (ReferenceEquals(derived.NonNullable, baseType.NonNullable))
                return true;

            // A bare parameter is below nothing in the class graph, but above its own bounds (§6):
            // asked whether `T` satisfies `IComparable<T>` under `<T : IComparable<T>>`, the walk
            // below would read as a flat no, and every self-referencing use of a constrained
            // generic would fail a check its declaration promises it passes.
            if (derived.NonNullable is TypeParameterSymbol parameter
                && ReachesThroughBounds(parameter, baseType, new HashSet<TypeParameterSymbol>()))
                return true;

            // A composite is not a NamedTypeSymbol and carries no interface list of its own, but the
            // class behind it does — which is what makes `int[]` an `IIterable<int>` (§4.2).
            if (Named(derived) is not NamedTypeSymbol from || baseType.NonNullable is not NamedTypeSymbol to)
                return false;

            // Reused across calls: the walk is depth-first, never re-enters IsSubtype, and clears
            // the set at its own entry, so one scratch instance is safe and saves an allocation per
            // subtype check — the most frequent primitive of overload resolution.
            _subtypeScratch.Clear();
            return WalkForBase(from, to, _subtypeScratch);
        }

        /// <summary>
        /// Whether one of <paramref name="parameter"/>'s bounds reaches
        /// <paramref name="target"/> — directly, through deeper bounds, or by subtyping.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The visiting set tracks the parameters on the <em>current path</em> only, added on entry
        /// and removed on exit, so mutually referencing bounds (<c>&lt;T : U, U : T&gt;</c>) stop at
        /// the cycle without a diamond shape being mistaken for one. It is allocated by the caller:
        /// this walk is rare next to the ordinary subtype test, and a fresh set keeps re-entrant
        /// calls from sharing state.
        /// </para>
        /// <para>
        /// Each concrete bound goes through <see cref="WalkForBase"/> rather than a plain equality,
        /// because a bound may itself be generic (<c>T : IEnumerable&lt;T&gt;</c>) and the question
        /// is about the whole hierarchy above it.
        /// </para>
        /// </remarks>
        private bool ReachesThroughBounds(TypeParameterSymbol parameter, TypeSymbol target, HashSet<TypeParameterSymbol> visiting)
        {
            if (!visiting.Add(parameter))
                return false;

            try
            {
                foreach (var bound in parameter.Constraints)
                {
                    if (bound.IsError)
                        continue;

                    var boundCore = bound.NonNullable;

                    if (boundCore is TypeParameterSymbol nested)
                    {
                        if (!ReferenceEquals(nested, parameter) && ReachesThroughBounds(nested, target, visiting))
                            return true;
                    }
                    else if (ReferenceEquals(boundCore, target.NonNullable))
                    {
                        return true;
                    }
                    else if (Named(bound) is NamedTypeSymbol from && target.NonNullable is NamedTypeSymbol to)
                    {
                        // A fresh set rather than the shared scratch: bounds checks now also run
                        // nested inside variance matching, where clearing a set an outer walk is
                        // using would drop its cycle protection.
                        if (WalkForBase(from, to, new HashSet<NamedTypeSymbol>()))
                            return true;
                    }
                }
            }
            finally
            {
                visiting.Remove(parameter);
            }

            return false;
        }

        private NamedTypeSymbol? Named(TypeSymbol type)
        {
            if (type.NonNullable is NamedTypeSymbol named)
                return named;

            return _lookup?.BackingType(type);
        }

        /// <summary>
        /// Whether <paramref name="to"/> is anywhere above <paramref name="from"/>, reading each
        /// step as the construction below it makes it.
        /// </summary>
        /// <remarks>
        /// The substitution is what makes this correct rather than nearly correct. A declaration
        /// records that it satisfies <c>IIterable&lt;T&gt;</c> in terms of its <em>own</em>
        /// parameter, so <c>array&lt;int&gt;</c> reaches <c>IIterable&lt;int&gt;</c> only once that
        /// <c>T</c> has been replaced — and with generics invariant (§6), the unsubstituted symbol
        /// would compare unequal and the answer would be a flat no.
        /// </remarks>
        private bool WalkForBase(NamedTypeSymbol from, NamedTypeSymbol to, HashSet<NamedTypeSymbol> seen)
        {
            if (!seen.Add(from))
                return false;

            if (ReferenceEquals(from, to))
                return true;

            // Same declaration, different arguments: the annotations decide. This is the one place
            // the old total invariance opens up — and only for a declaration that asked for it,
            // which keeps every unannotated construction converting exactly as before.
            if (ReferenceEquals(from.Definition, to.Definition)
                && MatchesVariantArguments(from, to))
            {
                return true;
            }

            var substitution = from.SubstitutionFromArguments(_factory);

            foreach (var contract in from.Interfaces)
            {
                if (WalkForBase(AsSeenFrom(contract, substitution), to, seen))
                    return true;
            }

            return from.BaseType is NamedTypeSymbol baseType
                && !ReferenceEquals(baseType, from)
                && WalkForBase(AsSeenFrom(baseType, substitution), to, seen);
        }

        /// <summary>
        /// Whether two constructions of one declaration relate argument by argument under what
        /// each parameter declared: invariant demands identity, <c>out</c> an element-wise
        /// conversion in, <c>in</c> an element-wise conversion out.
        /// </summary>
        private bool MatchesVariantArguments(NamedTypeSymbol from, NamedTypeSymbol to)
        {
            var fromArguments = from.TypeArguments;
            var toArguments = to.TypeArguments;
            if (fromArguments.Count != toArguments.Count)
                return false;

            var parameters = from.Definition.TypeParameters;
            for (int i = 0; i < fromArguments.Count; i++)
            {
                switch (i < parameters.Count ? parameters[i].Variance : TypeParameterVariance.Invariant)
                {
                    case TypeParameterVariance.Covariant:
                        if (!IsVariantAssignable(fromArguments[i], toArguments[i]))
                            return false;

                        break;

                    case TypeParameterVariance.Contravariant:
                        if (!IsVariantAssignable(toArguments[i], fromArguments[i]))
                            return false;

                        break;

                    default:
                        // Interned constructions make reference equality the same relation as type
                        // equality — the same fact every signature key in the compiler leans on.
                        if (!ReferenceEquals(fromArguments[i], toArguments[i]))
                            return false;

                        break;
                }
            }

            return true;
        }

        /// <summary>
        /// The restricted assignability variance is allowed to lean on: identity, nullability
        /// widening, hierarchy through <see cref="WalkForBase"/>, bounds of a bare parameter, and
        /// the structural families.
        /// </summary>
        /// <remarks>
        /// Deliberately narrower than <see cref="ClassifyImplicitOnly"/>. Numeric widening would be
        /// unsound under erasure — a covariant read hands back the boxed reference it stored, and
        /// no per-element conversion exists at run time to turn an int box into a float one — and
        /// user-defined conversions are explicit-only besides. Nullability moves one way only,
        /// exactly as it does for whole types: non-null flows into nullable, never back.
        /// </remarks>
        private bool IsVariantAssignable(TypeSymbol source, TypeSymbol destination)
        {
            if (ReferenceEquals(source, destination))
                return true;

            if (source.IsError || destination.IsError || source.IsNever)
                return true;

            if (source.IsNullable && !destination.IsNullable)
                return false;

            var from = source.NonNullable;
            var to = destination.NonNullable;
            if (ReferenceEquals(from, to))
                return true;

            switch (from, to)
            {
                // A fresh visiting set rather than the shared scratch: this runs *inside* an
                // active WalkForBase whenever a covariant read nests another construction, and
                // clearing a set the outer walk is using would drop its cycle protection.
                case (NamedTypeSymbol namedFrom, NamedTypeSymbol namedTo):
                    return WalkForBase(namedFrom, namedTo, new HashSet<NamedTypeSymbol>());

                case (ClosureTypeSymbol closureFrom, ClosureTypeSymbol closureTo):
                    if (closureFrom.ParameterTypes.Count != closureTo.ParameterTypes.Count)
                        return false;

                    for (int i = 0; i < closureFrom.ParameterTypes.Count; i++)
                    {
                        if (!IsVariantAssignable(closureTo.ParameterTypes[i], closureFrom.ParameterTypes[i]))
                            return false;
                    }

                    return IsVariantAssignable(closureFrom.ReturnType, closureTo.ReturnType);

                case (TupleTypeSymbol tupleFrom, TupleTypeSymbol tupleTo):
                    if (tupleFrom.ElementTypes.Count != tupleTo.ElementTypes.Count)
                        return false;

                    for (int i = 0; i < tupleFrom.ElementTypes.Count; i++)
                    {
                        if (!IsVariantAssignable(tupleFrom.ElementTypes[i], tupleTo.ElementTypes[i]))
                            return false;
                    }

                    return true;

                case (GeneratorTypeSymbol generatorFrom, GeneratorTypeSymbol generatorTo):
                    return IsVariantAssignable(generatorFrom.ElementType, generatorTo.ElementType);

                case (TypeParameterSymbol bounded, _):
                    return ReachesThroughBounds(bounded, destination, new HashSet<TypeParameterSymbol>());

                default:
                    return false;
            }
        }

        private static NamedTypeSymbol AsSeenFrom(NamedTypeSymbol type, TypeSubstitution substitution)
            => substitution.IsEmpty ? type : (NamedTypeSymbol)type.Substitute(substitution);

        private Conversion ClassifyImplicit(TypeSymbol source, TypeSymbol destination)
        {
            // A type parameter is a real, invariant type to the compiler even though it erases at
            // runtime (§6) — "generics are invariant" applies inside the declaring body too, not
            // only at a constructed call site. Classify's own identity fast path already covers `T`
            // reaching `T`; the only other implicit path is `T` widening to its own `T?`, mirroring
            // the ordinary nullable-widening rule below but restricted to the same parameter rather
            // than any type. Anything else — a concrete type, or a different parameter entirely —
            // has no business flowing into a `T`-typed slot from inside `T`'s own declaration, which
            // is exactly the check that catches `this.value = 5;` against a field typed `T`.
            if (destination.TypeKind == TypeSymbolKind.TypeParameter)
            {
                return ReferenceEquals(source.NonNullable, destination.NonNullable)
                    && destination.IsNullable && !source.IsNullable
                        ? Conversion.Of(ConversionKind.ImplicitNullable)
                        : Conversion.None;
            }

            // Anything at all flows into `unknown`, which only knows it holds a reference. The
            // compiler owes a box on the way in for a primitive, and a Cast on the way out.
            if (destination.SpecialType == SpecialType.Unknown)
                return Conversion.Of(ConversionKind.ImplicitErasure);

            // §6: a constrained parameter widens to its bounds, exactly as a concrete class widens
            // to its base. Inside `<T : IComparable<T>>`, a `T` may flow into an
            // `IComparable<T>`-typed slot — that upcast is the bound's whole purpose, and without it
            // a body could see the member through lookup yet be refused the assignment to call it.
            // Nullability rides along one way only: `T` and `T?` both reach a nullable bound, a
            // nullable `T?` never reaches a non-nullable one.
            if (source.NonNullable is TypeParameterSymbol bounded
                && !(source.IsNullable && !destination.IsNullable)
                && ReachesThroughBounds(bounded, destination.NonNullable, new HashSet<TypeParameterSymbol>()))
                return Conversion.Of(ConversionKind.ImplicitReference);

            if (IsErasedSlot(source))
                return Conversion.None;

            // `T` becoming `T?` costs nothing: a nullable is the same type with one more value.
            if (destination.IsNullable && !source.IsNullable && ReferenceEquals(source, destination.NonNullable))
                return Conversion.Of(ConversionKind.ImplicitNullable);

            if (source.IsNullable && !destination.IsNullable)
                return Conversion.None;

            var from = source.NonNullable;
            var to = destination.NonNullable;

            // §5.3: a tuple's element names are sugar for the positions and never join the
            // signature, so `(x: int, y: string)` and `(int, string)` are the same tuple type even
            // though the factory interns them as separate objects. That sameness has to be called
            // out as identity here, before the structural-variance rule below classifies it as an
            // ImplicitReference — which the emitter would honor by packing the tuple, the wrong
            // thing for two types that share a layout.
            if (from is TupleTypeSymbol tupleFrom && to is TupleTypeSymbol tupleTo && SameTupleShape(tupleFrom, tupleTo))
                return Conversion.Identity;

            // §5.7 names exactly one implicit numeric widening, and mixing an int with a float is
            // what makes overload resolution non-trivial in the first place.
            if (from.SpecialType == SpecialType.Int && to.SpecialType == SpecialType.Float)
                return Conversion.Of(ConversionKind.ImplicitNumeric);

            if (to is NamedTypeSymbol && IsSubtype(from, to))
                return Conversion.Of(ConversionKind.ImplicitReference);

            // §6: the structural families carry variance of their own. A closure accepts a
            // closure whose inputs are wider and whose output is narrower, a tuple widens
            // element by element, and a generator widens with what it yields — the same
            // direction-restricted comparison declared variance uses, applied to types nobody
            // had to annotate because their shape already says which side produces.
            if (from.TypeKind is TypeSymbolKind.Closure or TypeSymbolKind.Tuple or TypeSymbolKind.Generator
                && IsVariantAssignable(from, to))
            {
                return Conversion.Of(ConversionKind.ImplicitReference);
            }

            return Conversion.None;
        }

        /// <summary>Whether a type is an enum marked <c>@Flags</c>, whose values are single ints (§P14).</summary>
        private static bool IsFlagsEnum(TypeSymbol type)
            => type is NamedTypeSymbol { TypeKind: TypeSymbolKind.Enum, IsFlagsEnum: true };

        /// <summary>
        /// Whether two tuples have the same element types in the same order. Element types are
        /// interned, so reference identity is the comparison; names never participate (§5.3).
        /// </summary>
        private static bool SameTupleShape(TupleTypeSymbol from, TupleTypeSymbol to)
        {
            if (from.ElementTypes.Count != to.ElementTypes.Count)
                return false;

            for (int i = 0; i < from.ElementTypes.Count; i++)
            {
                if (!ReferenceEquals(from.ElementTypes[i], to.ElementTypes[i]))
                    return false;
            }

            return true;
        }

        private Conversion ClassifyExplicit(TypeSymbol source, TypeSymbol destination)
        {
            // Reading a concrete type back out of an erased slot is §1.11's second obligation.
            if (IsErasedSlot(source))
                return Conversion.Of(ConversionKind.ExplicitErasure);

            var from = source.NonNullable;
            var to = destination.NonNullable;

            if (from.IsPrimitive && to.IsPrimitive && !from.IsVoid && !to.IsVoid)
                return Conversion.Of(ConversionKind.ExplicitNumeric);

            // §P14: a @Flags enum is one int, so this moves no bits - but it has to be written,
            // because the two are different types to everything above emit, and because the whole
            // point of the enum is that an arbitrary int is not one of its combinations. It is what
            // makes the empty set expressible (`0 as Perm`) and what lets a flag set be stored or
            // sent somewhere that only holds numbers.
            if (IsFlagsEnum(from) ? to.SpecialType == SpecialType.Int : from.SpecialType == SpecialType.Int && IsFlagsEnum(to))
                return Conversion.Of(ConversionKind.ExplicitNumeric);

            // `T?` narrowing back to `T` is what `!!` asserts, and it is a cast like any other.
            if (source.IsNullable && ReferenceEquals(from, to))
                return Conversion.Of(ConversionKind.ExplicitReference);

            if (from is NamedTypeSymbol && to is NamedTypeSymbol && IsSubtype(to, from))
                return Conversion.Of(ConversionKind.ExplicitReference);

            return FindUserDefined(from, to);
        }

        /// <summary>
        /// Finds an <c>operator as</c> converting between the two types.
        /// </summary>
        /// <remarks>
        /// §5.6 puts both directions of a conversion on whichever type declares each, so both ends
        /// are searched rather than only the source.
        /// </remarks>
        private Conversion FindUserDefined(TypeSymbol from, TypeSymbol to)
        {
            var found = FindConversionOn(from, from, to) ?? FindConversionOn(to, from, to);
            return found is null ? Conversion.None : Conversion.User(found);
        }

        private MethodSymbol? FindConversionOn(TypeSymbol owner, TypeSymbol from, TypeSymbol to)
        {
            if (owner is not NamedTypeSymbol named)
                return null;

            foreach (var member in named.Definition.Members)
            {
                if (member is not MethodSymbol method || !method.IsConversion || method.Parameters.Count != 1)
                    continue;

                if (ReferenceEquals(method.Parameters[0].Type.NonNullable, from)
                    && ReferenceEquals(method.ReturnType.NonNullable, to))
                {
                    return method;
                }
            }

            return null;
        }

        private static bool IsErasedSlot(TypeSymbol type)
            => type.SpecialType == SpecialType.Unknown || type.TypeKind == TypeSymbolKind.TypeParameter;
    }
}
