#nullable enable

using Surtr.Compiler.Binding.Symbols;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>What looking a name up found.</summary>
    /// <remarks>
    /// Ambiguity is a result rather than an error, because §2.1 makes a name pulled in from two
    /// imports a problem <em>at the point of use</em> and not at the <c>import</c> line. Importing
    /// two modules that both declare <c>Vec2</c> is fine right up until someone writes it.
    /// </remarks>
    public readonly struct LookupResult
    {
        private static readonly IReadOnlyList<Symbol> None = Array.Empty<Symbol>();

        private LookupResult(Symbol? symbol, IReadOnlyList<Symbol> candidates)
        {
            Symbol = symbol;
            Candidates = candidates;
        }

        /// <summary>What the name resolved to, or <see langword="null"/> if nothing or too much did.</summary>
        public Symbol? Symbol { get; }

        /// <summary>The competing declarations, when more than one answered to the name.</summary>
        public IReadOnlyList<Symbol> Candidates { get; }

        /// <summary>Nothing in scope answered to the name.</summary>
        public static LookupResult NotFound => new LookupResult(null, None);

        /// <summary>Exactly one declaration answered.</summary>
        public static LookupResult Found(Symbol symbol) => new LookupResult(symbol, None);

        /// <summary>Several declarations answered, and the source has to say which.</summary>
        public static LookupResult Ambiguous(IReadOnlyList<Symbol> candidates) => new LookupResult(null, candidates);

        /// <summary>Whether more than one declaration answered.</summary>
        public bool IsAmbiguous => Candidates.Count > 1;

        /// <summary>Whether anything at all answered.</summary>
        public bool IsFound => Symbol is not null || IsAmbiguous;
    }

    /// <summary>
    /// One level of name resolution, chained to the level outside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The chain runs inside out — type parameters, then the enclosing type's own declarations,
    /// then the module's, then named imports, then wildcard imports, then the built-in types. The
    /// first level that answers wins, which is what makes a nearer declaration shadow a further one
    /// without any level having to know what the others hold.
    /// </para>
    /// <para>
    /// Types and members live in <b>separate namespaces</b>: §1.1 makes a type name an ordinary
    /// identifier resolved in the type namespace, so a class and a function may share a name.
    /// A scope is therefore built per namespace rather than holding both.
    /// </para>
    /// </remarks>
    public sealed class Scope
    {
        private readonly Dictionary<string, object> _symbols = new Dictionary<string, object>(StringComparer.Ordinal);
        private Dictionary<string, ModuleSymbol>? _moduleAliases;

        /// <summary>Creates a scope nested inside <paramref name="parent"/>.</summary>
        public Scope(Scope? parent = null) => Parent = parent;

        /// <summary>The scope outside this one, searched when this one has no answer.</summary>
        public Scope? Parent { get; }

        /// <summary>Creates a scope nested inside this one.</summary>
        public Scope CreateChild() => new Scope(this);

        /// <summary>
        /// Declares a name in this scope.
        /// </summary>
        /// <returns>
        /// <see langword="false"/> if the name is already declared <em>here</em>. A name declared in
        /// an outer scope is shadowed rather than rejected.
        /// </returns>
        public bool TryDeclare(string name, Symbol symbol)
        {
            if (_symbols.ContainsKey(name))
                return false;

            _symbols.Add(name, symbol);
            return true;
        }

        /// <summary>
        /// Adds a candidate for a name that several declarations may answer to, the way a wildcard
        /// import does. Two candidates make the name ambiguous rather than replacing one another.
        /// </summary>
        public void AddCandidate(string name, Symbol symbol)
        {
            if (!_symbols.TryGetValue(name, out object? existing))
            {
                _symbols.Add(name, symbol);
                return;
            }

            if (existing is List<Symbol> candidates)
            {
                if (!candidates.Contains(symbol))
                    candidates.Add(symbol);

                return;
            }

            var single = (Symbol)existing;
            if (ReferenceEquals(single, symbol))
                return;

            _symbols[name] = new List<Symbol> { single, symbol };
        }

        /// <summary>What this scope alone says about a name, ignoring the chain.</summary>
        public LookupResult LookupLocal(string name)
        {
            if (!_symbols.TryGetValue(name, out object? found))
                return LookupResult.NotFound;

            return found is List<Symbol> candidates
                ? LookupResult.Ambiguous(candidates)
                : LookupResult.Found((Symbol)found);
        }

        /// <summary>What the chain says about a name, innermost scope first.</summary>
        public LookupResult Lookup(string name)
        {
            for (Scope? scope = this; scope is not null; scope = scope.Parent)
            {
                var result = scope.LookupLocal(name);
                if (result.IsFound)
                    return result;
            }

            return LookupResult.NotFound;
        }

        /// <summary>Every name declared directly in this scope.</summary>
        public IEnumerable<string> Names => _symbols.Keys;

        /// <summary>
        /// Declares a module alias (<c>import X as Y;</c>, §2.1) in this scope.
        /// </summary>
        /// <remarks>
        /// Kept in a dictionary of its own rather than alongside <see cref="Lookup"/>'s types and
        /// members: a <see cref="ModuleSymbol"/> answering to a name there would reach every
        /// existing caller that assumes what it finds is a type or a member, for the sake of a
        /// name that only <see cref="TypeResolver"/>'s qualified-name lookup ever wants. Two
        /// aliases sharing a name is a duplicate declaration, not a shadow - an alias has no
        /// import of its own to be nearer than, unlike a type name reached from two wildcards.
        /// </remarks>
        /// <returns><see langword="false"/> if the name is already an alias <em>in this scope</em>.</returns>
        public bool TryDeclareModuleAlias(string name, ModuleSymbol module)
        {
            _moduleAliases ??= new Dictionary<string, ModuleSymbol>(StringComparer.Ordinal);

            if (_moduleAliases.ContainsKey(name))
                return false;

            _moduleAliases.Add(name, module);
            return true;
        }

        /// <summary>What the chain says a module alias named <paramref name="name"/> points at, innermost scope first.</summary>
        public ModuleSymbol? LookupModuleAlias(string name)
        {
            for (Scope? scope = this; scope is not null; scope = scope.Parent)
            {
                if (scope._moduleAliases is not null && scope._moduleAliases.TryGetValue(name, out var module))
                    return module;
            }

            return null;
        }
    }
}
