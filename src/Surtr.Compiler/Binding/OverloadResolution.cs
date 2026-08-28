#nullable enable

using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax.Ast;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>One argument at a call site, as far as overload resolution needs to know.</summary>
    public readonly struct ArgumentInfo
    {
        /// <summary>Creates an argument.</summary>
        /// <param name="type">Its type.</param>
        /// <param name="name">The parameter it names, for a named argument (§3.5).</param>
        public ArgumentInfo(TypeSymbol type, string? name = null)
        {
            Type = type;
            Name = name;
            LambdaArity = null;
            DeferredDefinition = null;
        }

        private ArgumentInfo(int arity, string? name, TypeSymbol type)
        {
            Type = type;
            Name = name;
            LambdaArity = arity;
            DeferredDefinition = null;
        }

        private ArgumentInfo(NamedTypeSymbol deferred, string? name, TypeSymbol type)
        {
            Type = type;
            Name = name;
            LambdaArity = null;
            DeferredDefinition = deferred;
        }

        /// <summary>
        /// A lambda whose parameters are not written out, so it has no type until it is told one
        /// (§5.9, §8).
        /// </summary>
        /// <remarks>
        /// The one argument that cannot say what it is before the overload is picked, because what it
        /// is <em>is</em> what the parameter says. So it comes in as an arity: applicability asks
        /// whether the parameter is a closure taking that many, and the lambda is bound afterwards
        /// against the one that won.
        /// </remarks>
        public static ArgumentInfo Lambda(int arity, TypeSymbol errorType, string? name = null)
            => new ArgumentInfo(arity, name, errorType);

        /// <summary>
        /// A generic construction whose type arguments nothing at the call site settles — <c>Box()</c>
        /// with nothing written and no argument to infer from (§6). Like a deferred lambda it has no
        /// type until the winning parameter tells it one, so it enters resolution as its generic
        /// definition and is bound afterwards against the parameter it lands in.
        /// </summary>
        public static ArgumentInfo DeferredConstruction(NamedTypeSymbol definition, TypeSymbol errorType, string? name = null)
            => new ArgumentInfo(definition, name, errorType);

        /// <summary>The argument's type, or the error type for a lambda or deferred construction not yet bound.</summary>
        public TypeSymbol Type { get; }

        /// <summary>The parameter it names, or <see langword="null"/> when positional.</summary>
        public string? Name { get; }

        /// <summary>How many parameters an unbound lambda takes, or <see langword="null"/> for anything else.</summary>
        public int? LambdaArity { get; }

        /// <summary>
        /// The generic definition an unbound construction is built from, or <see langword="null"/>.
        /// </summary>
        public NamedTypeSymbol? DeferredDefinition { get; }
    }

    /// <summary>How a call site resolved.</summary>
    public enum OverloadStatus
    {
        /// <summary>Exactly one candidate won.</summary>
        Resolved,

        /// <summary>Nothing of that name exists.</summary>
        NoCandidates,

        /// <summary>Candidates exist, but the arguments fill none of them.</summary>
        NoApplicableCandidate,

        /// <summary>Two candidates are equally specific, so the call site has to disambiguate.</summary>
        Ambiguous,
    }

    /// <summary>What overload resolution decided.</summary>
    public readonly struct OverloadResult
    {
        internal OverloadResult(OverloadStatus status, MethodSymbol? method, IReadOnlyList<MethodSymbol> candidates)
        {
            Status = status;
            Method = method;
            Candidates = candidates;
        }

        /// <summary>How it went.</summary>
        public OverloadStatus Status { get; }

        /// <summary>The winner, when there is one.</summary>
        public MethodSymbol? Method { get; }

        /// <summary>
        /// The candidates worth naming in a diagnostic: the applicable ones when the call was
        /// ambiguous, all of them when none applied.
        /// </summary>
        public IReadOnlyList<MethodSymbol> Candidates { get; }

        /// <summary>Whether exactly one candidate won.</summary>
        public bool IsResolved => Status == OverloadStatus.Resolved;
    }

    /// <summary>
    /// Picks the member a call site means, by §3.5's four rules in the order they are written.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rule 1 — that overloads must differ in their parameter list — is not here: it is a property
    /// of a <em>declaration</em> and is checked once, by <c>SignatureSet</c>, rather than at every
    /// call. What this does is rules 2 through 4: which candidates the arguments can fill, which of
    /// those is most specific, and the refusal to guess when two tie.
    /// </para>
    /// <para>
    /// Specificity is decided per argument rather than by a score, so a candidate wins only if it
    /// is at least as good everywhere and strictly better somewhere. A score would let one exact
    /// match outvote two conversions, which is a silent pick dressed up as a rule.
    /// </para>
    /// </remarks>
    public sealed class OverloadResolution
    {
        private static readonly IReadOnlyList<MethodSymbol> NoCandidates = Array.Empty<MethodSymbol>();

        private readonly Conversions _conversions;

        /// <summary>Creates a resolver over one compilation's conversions.</summary>
        public OverloadResolution(Conversions conversions) => _conversions = conversions;

        /// <summary>Picks the candidate a call with these arguments means.</summary>
        public OverloadResult Resolve(IReadOnlyList<MethodSymbol> candidates, IReadOnlyList<ArgumentInfo> arguments)
        {
            if (candidates.Count == 0)
                return new OverloadResult(OverloadStatus.NoCandidates, null, NoCandidates);

            var applicable = new List<Candidate>();

            for (int i = 0; i < candidates.Count; i++)
            {
                if (TryBuild(candidates[i], arguments, out var candidate))
                    applicable.Add(candidate);
            }

            if (applicable.Count == 0)
                return new OverloadResult(OverloadStatus.NoApplicableCandidate, null, candidates);

            if (applicable.Count == 1)
                return new OverloadResult(OverloadStatus.Resolved, applicable[0].Method, NoCandidates);

            var best = applicable[0];
            for (int i = 1; i < applicable.Count; i++)
            {
                if (IsBetter(applicable[i], best))
                    best = applicable[i];
            }

            // Winning against the one that happened to be best so far is not the same as winning
            // against every other, so the check is repeated against the whole set.
            var tied = new List<MethodSymbol>();
            foreach (var candidate in applicable)
            {
                if (!ReferenceEquals(candidate.Method, best.Method) && !IsBetter(best, candidate))
                    tied.Add(candidate.Method);
            }

            // §11: when nothing about the arguments separates the candidates, an @Obsolete mark
            // does. A tie that would have been reported — or lost to whichever obsolete candidate
            // happened to be first — goes to the one that is not being retired instead, and only
            // when every applicable candidate carries the mark does the call resolve to it and
            // warn at its site.
            if (tied.Count > 0 && BuiltInAttributes.IsObsolete(best.Method))
            {
                for (int i = 0; i < tied.Count; i++)
                {
                    if (BuiltInAttributes.IsObsolete(tied[i]))
                        continue;

                    for (int j = 0; j < applicable.Count; j++)
                    {
                        if (ReferenceEquals(applicable[j].Method, tied[i]))
                        {
                            best = applicable[j];
                            break;
                        }
                    }

                    tied.Clear();
                    break;
                }
            }

            if (tied.Count > 0)
            {
                tied.Insert(0, best.Method);
                return new OverloadResult(OverloadStatus.Ambiguous, null, tied);
            }

            return new OverloadResult(OverloadStatus.Resolved, best.Method, NoCandidates);
        }

        /// <summary>
        /// Works out whether the arguments fill this candidate's parameters, after defaults supply
        /// trailing omissions and varargs absorbs any surplus.
        /// </summary>
        private bool TryBuild(MethodSymbol method, IReadOnlyList<ArgumentInfo> arguments, out Candidate candidate)
        {
            candidate = default;

            var parameters = method.Parameters;
            int varargIndex = -1;
            for (int i = 0; i < parameters.Count; i++)
            {
                if (parameters[i].IsVararg)
                    varargIndex = i;
            }

            var mapped = new TypeSymbol?[parameters.Count];
            var conversions = new List<Conversion>(arguments.Count);
            bool expandsVarargs = false;
            int positional = 0;
            bool naming = false;

            for (int i = 0; i < arguments.Count; i++)
            {
                var argument = arguments[i];

                if (argument.Name is null)
                {
                    // §3.5: once naming starts it continues to the end of the call.
                    if (naming)
                        return false;

                    int target = positional++;

                    if (varargIndex >= 0 && target >= varargIndex)
                    {
                        // The surplus is absorbed, unless the whole array was passed as one value.
                        var element = ElementOf(parameters[varargIndex].Type);
                        if (element is null)
                            return false;

                        if (target == varargIndex && arguments.Count - i == 1
                            && _conversions.IsAssignable(argument.Type, parameters[varargIndex].Type))
                        {
                            mapped[varargIndex] = argument.Type;
                            conversions.Add(_conversions.Classify(argument.Type, parameters[varargIndex].Type));
                            continue;
                        }

                        var absorbed = _conversions.ClassifyImplicitOnly(argument.Type, element);
                        if (!absorbed.Exists)
                            return false;

                        mapped[varargIndex] = parameters[varargIndex].Type;
                        conversions.Add(absorbed);
                        expandsVarargs = true;
                        continue;
                    }

                    if (target >= parameters.Count)
                        return false;

                    if (argument.LambdaArity is int arity)
                    {
                        if (!Accepts(parameters[target].Type, arity))
                            return false;

                        mapped[target] = parameters[target].Type;
                        conversions.Add(Conversion.Of(ConversionKind.Identity));
                        continue;
                    }

                    if (argument.DeferredDefinition is NamedTypeSymbol deferred)
                    {
                        if (!AcceptsDeferred(parameters[target].Type, deferred))
                            return false;

                        mapped[target] = parameters[target].Type;
                        conversions.Add(Conversion.Of(ConversionKind.Identity));
                        continue;
                    }

                    // Implicit-only: a candidate rejected on its first argument (the common case
                    // with overloads) never pays for the explicit or user-defined half of Classify.
                    var conversion = _conversions.ClassifyImplicitOnly(argument.Type, parameters[target].Type);
                    if (!conversion.Exists)
                        return false;

                    mapped[target] = argument.Type;
                    conversions.Add(conversion);
                    continue;
                }

                naming = true;

                int named = IndexOf(parameters, argument.Name);
                if (named < 0 || mapped[named] is not null)
                    return false;

                if (argument.LambdaArity is int namedArity)
                {
                    if (!Accepts(parameters[named].Type, namedArity))
                        return false;

                    mapped[named] = parameters[named].Type;
                    conversions.Add(Conversion.Of(ConversionKind.Identity));
                    continue;
                }

                if (argument.DeferredDefinition is NamedTypeSymbol namedDeferred)
                {
                    if (!AcceptsDeferred(parameters[named].Type, namedDeferred))
                        return false;

                    mapped[named] = parameters[named].Type;
                    conversions.Add(Conversion.Of(ConversionKind.Identity));
                    continue;
                }

                var namedConversion = _conversions.ClassifyImplicitOnly(argument.Type, parameters[named].Type);
                if (!namedConversion.Exists)
                    return false;

                mapped[named] = argument.Type;
                conversions.Add(namedConversion);
            }

            int defaulted = 0;
            for (int i = 0; i < parameters.Count; i++)
            {
                if (mapped[i] is not null)
                    continue;

                // An empty varargs is filled by an empty array, not by a default.
                if (i == varargIndex)
                {
                    expandsVarargs = true;
                    continue;
                }

                if (!parameters[i].HasDefaultValue)
                    return false;

                defaulted++;
            }

            candidate = new Candidate(method, conversions, defaulted, expandsVarargs || varargIndex >= 0);
            return true;
        }

        /// <summary>
        /// Whether a parameter could receive a lambda taking <paramref name="arity"/> parameters.
        /// </summary>
        /// <remarks>
        /// Arity and nothing else, because nothing else is known yet: the lambda's parameter types
        /// come <em>from</em> this parameter, so asking whether they match would be asking whether it
        /// matches itself. Two overloads taking closures of the same arity therefore tie, and §3.5's
        /// rule 4 makes that an ambiguity the call site resolves — which is the honest answer, since a
        /// reader could not tell them apart either.
        /// </remarks>
        private static bool Accepts(TypeSymbol parameter, int arity)
            => parameter.NonNullable is ClosureTypeSymbol closure && closure.ParameterTypes.Count == arity;

        /// <summary>
        /// Whether a parameter could receive a deferred generic construction — one whose type
        /// arguments the winning parameter will settle. The parameter has to be that construction's
        /// own generic definition, or a construction of it; anything else cannot type it.
        /// </summary>
        private static bool AcceptsDeferred(TypeSymbol parameter, NamedTypeSymbol deferred)
        {
            if (parameter.NonNullable is not NamedTypeSymbol paramType)
                return false;

            return ReferenceEquals(paramType, deferred) || ReferenceEquals(paramType.Definition, deferred);
        }

        /// <summary>
        /// Whether <paramref name="left"/> beats <paramref name="right"/>, by §3.5's rule 3.
        /// </summary>
        private static bool IsBetter(in Candidate left, in Candidate right)
        {
            // "A non-varargs candidate always beats a varargs one" - stated as absolute, so it is
            // checked before anything about the arguments themselves.
            if (left.IsVarargs != right.IsVarargs)
                return !left.IsVarargs;

            if (left.Conversions.Count == right.Conversions.Count)
            {
                bool strictlyBetterSomewhere = false;

                for (int i = 0; i < left.Conversions.Count; i++)
                {
                    bool leftExact = left.Conversions[i].IsIdentity;
                    bool rightExact = right.Conversions[i].IsIdentity;

                    if (leftExact == rightExact)
                        continue;

                    if (!leftExact)
                        return false;

                    strictlyBetterSomewhere = true;
                }

                if (strictlyBetterSomewhere)
                    return true;
            }

            // "A candidate that needs neither defaults nor varargs beats one that does."
            return left.Defaulted < right.Defaulted;
        }

        private static int IndexOf(IReadOnlyList<ParameterSymbol> parameters, string name)
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                if (string.Equals(parameters[i].Name, name, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        private static TypeSymbol? ElementOf(TypeSymbol varargType)
            => varargType is ArrayTypeSymbol array ? array.ElementType : null;

        private readonly struct Candidate
        {
            internal Candidate(MethodSymbol method, List<Conversion> conversions, int defaulted, bool isVarargs)
            {
                Method = method;
                Conversions = conversions;
                Defaulted = defaulted;
                IsVarargs = isVarargs;
            }

            internal MethodSymbol Method { get; }

            internal List<Conversion> Conversions { get; }

            internal int Defaulted { get; }

            internal bool IsVarargs { get; }
        }
    }
}
