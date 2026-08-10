#nullable enable

using Surtr.Compiler.Binding.Symbols;
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
        }

        /// <summary>The argument's type.</summary>
        public TypeSymbol Type { get; }

        /// <summary>The parameter it names, or <see langword="null"/> when positional.</summary>
        public string? Name { get; }
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

                        var absorbed = _conversions.Classify(argument.Type, element);
                        if (!absorbed.IsImplicit)
                            return false;

                        mapped[varargIndex] = parameters[varargIndex].Type;
                        conversions.Add(absorbed);
                        expandsVarargs = true;
                        continue;
                    }

                    if (target >= parameters.Count)
                        return false;

                    var conversion = _conversions.Classify(argument.Type, parameters[target].Type);
                    if (!conversion.IsImplicit)
                        return false;

                    mapped[target] = argument.Type;
                    conversions.Add(conversion);
                    continue;
                }

                naming = true;

                int named = IndexOf(parameters, argument.Name);
                if (named < 0 || mapped[named] is not null)
                    return false;

                var namedConversion = _conversions.Classify(argument.Type, parameters[named].Type);
                if (!namedConversion.IsImplicit)
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
