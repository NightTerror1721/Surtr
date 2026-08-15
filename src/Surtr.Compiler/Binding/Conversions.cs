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
    /// Everything here follows from three decisions taken elsewhere. Generics are invariant (§6),
    /// so a construction converts only to itself. There are no user-defined <em>implicit</em>
    /// conversions (§5.6), so the implicit set is fixed and small. And <c>unknown</c> is the erased
    /// slot with a surface name (§5.10), so it takes anything and gives nothing back without a cast.
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
            => Classify(source, destination).IsImplicit;

        /// <summary>How <paramref name="source"/> reaches <paramref name="destination"/>, if it does.</summary>
        public Conversion Classify(TypeSymbol source, TypeSymbol destination)
        {
            if (ReferenceEquals(source, destination))
                return Conversion.Identity;

            // One bad name should report once, not everywhere its value goes.
            if (source.IsError || destination.IsError)
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
        /// through base classes or interfaces.
        /// </summary>
        public bool IsSubtype(TypeSymbol derived, TypeSymbol baseType)
        {
            if (ReferenceEquals(derived.NonNullable, baseType.NonNullable))
                return true;

            // A composite is not a NamedTypeSymbol and carries no interface list of its own, but the
            // class behind it does — which is what makes `int[]` an `IIterable<int>` (§4.2).
            if (Named(derived) is not NamedTypeSymbol from || baseType.NonNullable is not NamedTypeSymbol to)
                return false;

            return WalkForBase(from, to, new HashSet<NamedTypeSymbol>());
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

        private static NamedTypeSymbol AsSeenFrom(NamedTypeSymbol type, TypeSubstitution substitution)
            => substitution.IsEmpty ? type : (NamedTypeSymbol)type.Substitute(substitution);

        private Conversion ClassifyImplicit(TypeSymbol source, TypeSymbol destination)
        {
            // Anything at all flows into a slot that only knows it holds a reference. The compiler
            // owes a box on the way in for a primitive, and a Cast on the way out.
            if (IsErasedSlot(destination))
                return Conversion.Of(ConversionKind.ImplicitErasure);

            if (IsErasedSlot(source))
                return Conversion.None;

            // `T` becoming `T?` costs nothing: a nullable is the same type with one more value.
            if (destination.IsNullable && !source.IsNullable && ReferenceEquals(source, destination.NonNullable))
                return Conversion.Of(ConversionKind.ImplicitNullable);

            if (source.IsNullable && !destination.IsNullable)
                return Conversion.None;

            var from = source.NonNullable;
            var to = destination.NonNullable;

            // §5.7 names exactly one implicit numeric widening, and mixing an int with a float is
            // what makes overload resolution non-trivial in the first place.
            if (from.SpecialType == SpecialType.Int && to.SpecialType == SpecialType.Float)
                return Conversion.Of(ConversionKind.ImplicitNumeric);

            if (to is NamedTypeSymbol && IsSubtype(from, to))
                return Conversion.Of(ConversionKind.ImplicitReference);

            return Conversion.None;
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
