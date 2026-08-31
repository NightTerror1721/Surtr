#nullable enable

using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Turns a type as written into the type it names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every failure produces the error type rather than nothing, so one unresolved name reports
    /// once and every rule that touches the result afterwards stays quiet. Binding a type never
    /// returns <see langword="null"/>.
    /// </para>
    /// <para>
    /// Two lookups are deliberately tried in order for a dotted name. <c>Entity.Handle</c> is first
    /// read as a nested type reached through something in scope, and only if that fails as a fully
    /// qualified <c>module.Type</c> — because §2.6 makes <c>.</c> the qualifier at every level, so
    /// nothing about the syntax says where the module ends and the type begins.
    /// </para>
    /// </remarks>
    public sealed class TypeResolver
    {
        private readonly TypeSymbolFactory _factory;
        private readonly MetadataImporter _importer;
        private readonly SurtrDiagnosticBag _diagnostics;
        private readonly ModuleDependencyGraph _dependencies;

        private readonly Dictionary<string, ModuleSymbol> _modules =
            new Dictionary<string, ModuleSymbol>(StringComparer.Ordinal);

        private readonly Dictionary<AliasSymbol, AliasBinding> _aliases = new Dictionary<AliasSymbol, AliasBinding>();
        private readonly HashSet<AliasSymbol> _resolvingAliases = new HashSet<AliasSymbol>();

        private readonly List<ConstructionSite> _constructions = new List<ConstructionSite>();

        private int _suppressed;

        /// <summary>
        /// Checks every constructed generic against its parameters' bounds (§6).
        /// </summary>
        /// <remarks>
        /// Deferred to its own pass because a constraint like <c>&lt;T : IComparable&lt;T&gt;&gt;</c>
        /// names a type whose hierarchy is itself still being resolved while signatures are bound.
        /// Once phase 2 is done, every bound is known and the check is a subtype test.
        /// </remarks>
        public void VerifyConstraints(Conversions conversions)
        {
            foreach (var site in _constructions)
            {
                var parameters = site.Type.TypeParameters;
                var arguments = site.Type.TypeArguments;

                // A bound is written in terms of the declaration's own parameters, so it has to be
                // read as this construction makes it: `<T : IComparable<T>>` on `Sorter<Vec2>` is
                // the question of whether `Vec2` satisfies `IComparable<Vec2>`.
                var substitution = site.Type.SubstitutionFromArguments(_factory);

                for (int i = 0; i < parameters.Count && i < arguments.Count; i++)
                {
                    foreach (var declaredBound in parameters[i].Constraints)
                    {
                        var bound = substitution.Apply(declaredBound);

                        if (bound.IsError || arguments[i].IsError || conversions.IsSubtype(arguments[i], bound))
                            continue;

                        ReportError(
                            SurtrDiagnosticCode.ConstraintNotSatisfied,
                            $"'{arguments[i].ToDisplayString()}' does not satisfy '{parameters[i].Name} : {bound.ToDisplayString()}'.",
                            site.SourceName,
                            site.Span);
                    }
                }
            }

            _constructions.Clear();
        }

        private readonly struct ConstructionSite
        {
            internal ConstructionSite(NamedTypeSymbol type, SourceSpan span, string sourceName)
            {
                Type = type;
                Span = span;
                SourceName = sourceName;
            }

            internal NamedTypeSymbol Type { get; }

            internal SourceSpan Span { get; }

            internal string SourceName { get; }
        }

        /// <summary>Creates a resolver over one compilation's factory, imports, dependency graph and diagnostics.</summary>
        public TypeResolver(
            TypeSymbolFactory factory,
            MetadataImporter importer,
            SurtrDiagnosticBag diagnostics,
            ModuleDependencyGraph dependencies)
        {
            _factory = factory;
            _importer = importer;
            _diagnostics = diagnostics;
            _dependencies = dependencies;
        }

        /// <summary>Makes a module's types reachable by fully qualified name.</summary>
        public void AddModule(ModuleSymbol module) => _modules[module.Path] = module;

        /// <summary>
        /// The metadata importer this resolver reads the built-in module through — exposed so a
        /// caller can recognize <c>array</c>/<c>dict</c>/<c>tuple</c> by the same reference identity
        /// <see cref="Apply"/> uses internally, without importing the built-in module a second time.
        /// </summary>
        internal MetadataImporter Importer => _importer;

        /// <summary>
        /// Records where an alias's target is written, so it can be resolved the first time the
        /// alias is used rather than in declaration order.
        /// </summary>
        public void RegisterAlias(AliasSymbol alias, AliasDeclarationSyntax syntax, Scope scope, string sourceName)
            => _aliases[alias] = new AliasBinding(syntax, scope, sourceName);

        /// <summary>
        /// Whether a dotted name names a type, without reporting if it does not.
        /// </summary>
        /// <remarks>
        /// A body binder asks this before treating a name as a value, because <c>Vec2(1, 2)</c> is
        /// a construction and <c>Suit.Hearts</c> is a static member — and a name that turns out to
        /// be an ordinary local is not a mistake worth a diagnostic.
        /// </remarks>
        public bool TryResolveTypeName(IReadOnlyList<string> path, Scope scope, SourceSpan span, out NamedTypeSymbol type)
        {
            var syntax = new NamedTypeSyntax(span, path, System.Array.Empty<TypeSyntax>());

            _suppressed++;
            try
            {
                var resolved = Resolve(syntax, scope, string.Empty);
                if (resolved is NamedTypeSymbol named && !named.IsError)
                {
                    type = named;
                    return true;
                }
            }
            finally
            {
                _suppressed--;
            }

            type = null!;
            return false;
        }

        /// <summary>Resolves a type as written.</summary>
        public TypeSymbol Resolve(TypeSyntax syntax, Scope scope, string sourceName)
        {
            switch (syntax)
            {
                case ArrayTypeSyntax array:
                    return _factory.Array(Resolve(array.ElementType, scope, sourceName));

                case DictTypeSyntax dictionary:
                    return _factory.Dictionary(
                        Resolve(dictionary.KeyType, scope, sourceName),
                        Resolve(dictionary.ValueType, scope, sourceName));

                case TupleTypeSyntax tuple:
                    return _factory.Tuple(ResolveAll(tuple.ElementTypes, scope, sourceName), tuple.ElementNames);

                case ClosureTypeSyntax closure:
                    return _factory.Closure(
                        ResolveAll(closure.ParameterTypes, scope, sourceName),
                        Resolve(closure.ReturnType, scope, sourceName));

                case NullableTypeSyntax nullable:
                    return Resolve(nullable.ElementType, scope, sourceName).Nullable;

                case NamedTypeSyntax named:
                    return ResolveNamed(named, scope, sourceName);

                default:
                    return _factory.ErrorType;
            }
        }

        private TypeSymbol[] ResolveAll(IReadOnlyList<TypeSyntax> types, Scope scope, string sourceName)
        {
            var resolved = new TypeSymbol[types.Count];
            for (int i = 0; i < types.Count; i++)
                resolved[i] = Resolve(types[i], scope, sourceName);

            return resolved;
        }

        private TypeSymbol ResolveNamed(NamedTypeSyntax syntax, Scope scope, string sourceName)
        {
            var arguments = ResolveAll(syntax.TypeArguments, scope, sourceName);

            // `generator<T>` is a family, not a declaration: like `T[]` and `{K: V}` it names one
            // built-in class whatever it is parameterised by, so it is built here rather than
            // looked up. The parser only produces this path from the `generator` keyword, so no
            // user type can be shadowed by it.
            if (syntax.Path.Count == 1 && syntax.Path[0] == "generator")
            {
                if (arguments.Length != 1)
                {
                    ReportError(
                        SurtrDiagnosticCode.InvalidGenericDeclaration,
                        $"'generator' takes exactly one type argument, but was given {arguments.Length}.",
                        sourceName,
                        syntax.Span);

                    return _factory.ErrorType;
                }

                return _factory.Generator(arguments[0]);
            }

            if (syntax.Path.Count == 1)
                return ResolveSimple(syntax, syntax.Path[0], arguments, scope, sourceName);

            // `Entity.Handle` before `game.core.Entity`: a name in scope is nearer than a module.
            if (TryResolveThroughScope(syntax, arguments, scope, sourceName, out var nested))
                return nested;

            if (TryResolveThroughAlias(syntax, arguments, scope, sourceName, out var aliased))
                return aliased;

            if (TryResolveQualified(syntax, arguments, sourceName, out var qualified))
                return qualified;

            return Unresolved(syntax, sourceName);
        }

        private TypeSymbol ResolveSimple(
            NamedTypeSyntax syntax,
            string name,
            TypeSymbol[] arguments,
            Scope scope,
            string sourceName)
        {
            var found = scope.Lookup(name);

            if (!found.IsFound)
                return Unresolved(syntax, sourceName);

            if (found.IsAmbiguous)
                return SelectByArity(syntax, found.Candidates, arguments, sourceName);

            return Apply(syntax, found.Symbol!, arguments, sourceName);
        }

        private bool TryResolveThroughScope(
            NamedTypeSyntax syntax,
            TypeSymbol[] arguments,
            Scope scope,
            string sourceName,
            out TypeSymbol resolved)
        {
            resolved = _factory.ErrorType;

            var head = scope.Lookup(syntax.Path[0]);
            if (!head.IsFound || head.Symbol is not NamedTypeSymbol container)
                return false;

            for (int i = 1; i < syntax.Path.Count; i++)
            {
                var nested = container.FindNestedTypes(syntax.Path[i]);
                if (nested.Count == 0)
                    return false;

                bool last = i == syntax.Path.Count - 1;
                int wanted = last ? arguments.Length : 0;

                NamedTypeSymbol? match = null;
                for (int c = 0; c < nested.Count; c++)
                {
                    if (nested[c].Arity == wanted)
                    {
                        match = nested[c];
                        break;
                    }
                }

                if (match is null)
                    return false;

                container = match;
            }

            resolved = Apply(syntax, container, arguments, sourceName);
            return true;
        }

        private bool TryResolveQualified(
            NamedTypeSyntax syntax,
            TypeSymbol[] arguments,
            string sourceName,
            out TypeSymbol resolved)
        {
            resolved = _factory.ErrorType;

            // The longest module prefix wins, which is what makes a nested type reachable by a
            // fully qualified name at all.
            for (int split = syntax.Path.Count - 1; split > 0; split--)
            {
                string modulePath = Join(syntax.Path, split);

                if (!TryGetModule(modulePath, out var module) || !TryResolveFromModule(syntax, module, split, arguments, sourceName, out resolved))
                    continue;

                // The one edge `SurtrCompilation.BuildDependencyGraph` cannot see at parse time: a
                // name reached with no `import` (§2.6 allows one) is only known to cross a module
                // boundary once this resolves it. Recorded regardless of `_suppressed` — a call site
                // asking "is this a type" through `TryResolveTypeName` still means it, if the answer
                // is yes; suppression only silences the diagnostic on a *no*, and a *no* never
                // reaches this line at all. `AddDependency` no-ops a module referencing itself, and
                // `ModuleDependencyGraph.AddModule` already tolerates a path with no source behind
                // it, so this needs no guard beyond knowing which module the reference is *from*.
                if (CurrentModule is ModuleSymbol current)
                    _dependencies.AddDependency(current.Path, modulePath);

                return true;
            }

            return false;
        }

        /// <summary>
        /// <c>Alias.Entity</c> or <c>Alias.Outer.Inner</c>, where <c>Alias</c> resolves through
        /// <c>import X as Alias;</c> (§2.1) rather than through a scope-visible type or a written-
        /// out module path.
        /// </summary>
        private bool TryResolveThroughAlias(
            NamedTypeSyntax syntax,
            TypeSymbol[] arguments,
            Scope scope,
            string sourceName,
            out TypeSymbol resolved)
        {
            resolved = _factory.ErrorType;

            var module = scope.LookupModuleAlias(syntax.Path[0]);
            if (module is null || !TryResolveFromModule(syntax, module, 1, arguments, sourceName, out resolved))
                return false;

            // The alias itself already crossed the module boundary at its own `import` line; a use
            // of it is a second, independent reference to that same dependency.
            if (CurrentModule is ModuleSymbol current)
                _dependencies.AddDependency(current.Path, module.Path);

            return true;
        }

        /// <summary>
        /// Reads <c>syntax.Path[startIndex]</c> as a top-level type declared in <paramref name="module"/>,
        /// then walks any further segments as nested types - the shared tail of both a fully
        /// qualified name (where <paramref name="startIndex"/> is where the module path ended) and
        /// a name reached through a module alias (where it is always <c>1</c>).
        /// </summary>
        private bool TryResolveFromModule(
            NamedTypeSyntax syntax,
            ModuleSymbol module,
            int startIndex,
            TypeSymbol[] arguments,
            string sourceName,
            out TypeSymbol resolved)
        {
            resolved = _factory.ErrorType;

            var candidates = module.FindTypes(syntax.Path[startIndex]);
            if (candidates.Count == 0)
                return false;

            NamedTypeSymbol? container = null;
            bool last = startIndex == syntax.Path.Count - 1;
            int wanted = last ? arguments.Length : 0;

            for (int c = 0; c < candidates.Count; c++)
            {
                if (candidates[c].Arity == wanted)
                {
                    container = candidates[c];
                    break;
                }
            }

            if (container is null)
                return false;

            for (int i = startIndex + 1; i < syntax.Path.Count && container is not null; i++)
            {
                var nested = container.FindNestedTypes(syntax.Path[i]);
                int nestedWanted = i == syntax.Path.Count - 1 ? arguments.Length : 0;

                container = null;
                for (int c = 0; c < nested.Count; c++)
                {
                    if (nested[c].Arity == nestedWanted)
                    {
                        container = nested[c];
                        break;
                    }
                }
            }

            if (container is null)
                return false;

            resolved = Apply(syntax, container, arguments, sourceName);
            return true;
        }

        /// <summary>
        /// Resolves a <c>moduleof</c> path (§2.1) to the module it names as a whole. Unlike a type
        /// reference there is no trailing type name to peel off the end, and unlike an import there
        /// is no wildcard union to take: the whole path — or, when its one segment is a declared
        /// alias, the alias's target — has to name exactly one module.
        /// </summary>
        public bool TryResolveModulePath(IReadOnlyList<string> path, Scope scope, string sourceName, out ModuleSymbol module)
        {
            module = null!;

            if (path.Count == 0)
                return false;

            // `moduleof(Alias)` reads exactly as `moduleof(<the alias's target>)` would (§2.1) - the
            // same compile-time qualifier rewrite an alias already is everywhere else a module may
            // be named.
            if (path.Count == 1 && scope.LookupModuleAlias(path[0]) is ModuleSymbol aliased)
            {
                module = aliased;
            }
            else if (!TryGetModule(Join(path, path.Count), out module))
            {
                return false;
            }

            if (CurrentModule is ModuleSymbol current)
                _dependencies.AddDependency(current.Path, module.Path);

            return true;
        }

        private bool TryGetModule(string modulePath, out ModuleSymbol module)
            => _modules.TryGetValue(modulePath, out module!) || _importer.TryGetModuleSymbol(modulePath, out module!);

        private TypeSymbol SelectByArity(
            NamedTypeSyntax syntax,
            IReadOnlyList<Symbol> candidates,
            TypeSymbol[] arguments,
            string sourceName)
        {
            var matching = new List<Symbol>();

            foreach (var candidate in candidates)
            {
                int arity = candidate switch
                {
                    NamedTypeSymbol named => named.Arity,
                    AliasSymbol alias => alias.TypeParameters.Count,
                    _ => 0,
                };

                if (arity == arguments.Length)
                    matching.Add(candidate);
            }

            if (matching.Count == 1)
                return Apply(syntax, matching[0], arguments, sourceName);

            if (matching.Count == 0)
            {
                ReportError(
                    SurtrDiagnosticCode.WrongTypeArgumentCount,
                    $"No declaration of '{Join(syntax.Path, syntax.Path.Count)}' takes {arguments.Length} type argument(s).",
                    sourceName,
                    syntax.Span);

                return _factory.ErrorType;
            }

            // Two imports both answering: §2.1 makes that a problem here, at the use.
            ReportError(
                SurtrDiagnosticCode.AmbiguousName,
                $"'{Join(syntax.Path, syntax.Path.Count)}' is brought into scope by more than one import.",
                sourceName,
                syntax.Span);

            return _factory.ErrorType;
        }

        /// <summary>
        /// The module whose source is being resolved, or <see langword="null"/> when that is not
        /// known — which is what accessibility is measured against (§3.1).
        /// </summary>
        /// <remarks>
        /// Ambient rather than a parameter, because a type reference is resolved from a dozen places
        /// that each already carry a scope and a source name, and the module is the same for every
        /// one of them at any given moment. Left null it fails open, which is the right way round: a
        /// resolver that does not know where it is should not invent a protection error.
        /// </remarks>
        public ModuleSymbol? CurrentModule { get; set; }

        /// <summary>The type a body is being resolved inside, for <c>private</c> and <c>protected</c>.</summary>
        public NamedTypeSymbol? CurrentType { get; set; }

        private void RequireAccessible(NamedTypeSymbol type, NamedTypeSyntax syntax, string sourceName)
        {
            if (_suppressed > 0 || AccessCheck.IsAccessible(type, type.Accessibility, CurrentType, CurrentModule))
                return;

            ReportError(
                SurtrDiagnosticCode.Inaccessible,
                $"'{type.Name}' is not visible from here; §3.1 defaults a top-level declaration to 'internal' and a nested one to 'private'.",
                sourceName,
                syntax.Span);
        }

        /// <summary>
        /// The arity cap a tuple's own allocation opcode (<c>TupPack</c>) imposes, which the
        /// nameable <c>tuple&lt;...&gt;</c> form (below) has to diagnose at the type reference
        /// rather than let surface only once emission fails.
        /// </summary>
        private const int MaxTupleArity = byte.MaxValue;

        private TypeSymbol Apply(NamedTypeSyntax syntax, Symbol symbol, TypeSymbol[] arguments, string sourceName)
        {
            switch (symbol)
            {
                case NamedTypeSymbol named:
                {
                    // array<T>, dict<K,V> and tuple<T1,...,Tn> are nameable, callable identifiers
                    // for exactly the types T[], {K:V} and (T1,...,Tn) already name — not a
                    // separate type, so this redirects the built-in class's own construction onto
                    // the identical, structurally-interned composite TypeSymbol the symbolic form
                    // produces, rather than a constructed NamedTypeSymbol of the class. Unqualified
                    // `array`/`dict`/`tuple` name lookup (and MemberLookup.BackingType, which never
                    // reaches this method at all) is untouched — this only fires once a full type
                    // application has resolved to one of these three built-ins specifically, via
                    // reference identity so a user's own shadowing declaration is never affected.
                    //
                    // tuple's imported Arity is 0 - its arity varies per value, not per declaration,
                    // per docs/VM-Plan.md §4.6 - so its check has to run before the ordinary arity
                    // test below rejects every non-empty argument list out of hand.
                    if (ReferenceEquals(named, _importer.TupleType))
                    {
                        if (arguments.Length > MaxTupleArity)
                        {
                            ReportError(
                                SurtrDiagnosticCode.WrongTypeArgumentCount,
                                $"A tuple can have at most {MaxTupleArity} elements, not {arguments.Length}.",
                                sourceName,
                                syntax.Span);

                            return _factory.ErrorType;
                        }

                        return _factory.Tuple(arguments);
                    }

                    if (named.Arity != arguments.Length)
                    {
                        ReportError(
                            SurtrDiagnosticCode.WrongTypeArgumentCount,
                            $"'{named.Name}' takes {named.Arity} type argument(s), not {arguments.Length}.",
                            sourceName,
                            syntax.Span);

                        return _factory.ErrorType;
                    }

                    // Every named resolution funnels through here — a name in scope, a fully
                    // qualified one, and each step of a nested one — so accessibility is asked once,
                    // where the type is finally settled on.
                    RequireAccessible(named, syntax, sourceName);

                    if (ReferenceEquals(named, _importer.ArrayType))
                        return _factory.Array(arguments[0]);

                    if (ReferenceEquals(named, _importer.DictionaryType))
                        return _factory.Dictionary(arguments[0], arguments[1]);

                    if (arguments.Length == 0)
                        return named;

                    var constructed = named.Construct(arguments);

                    // Checked later, not here: a constraint may name a type whose own members are
                    // still being resolved, so the site is recorded and verified once phase 2 has
                    // finished settling every signature.
                    if (_suppressed == 0)
                        _constructions.Add(new ConstructionSite(constructed, syntax.Span, sourceName));

                    return constructed;
                }

                case TypeParameterSymbol parameter:
                {
                    if (arguments.Length > 0)
                    {
                        ReportError(
                            SurtrDiagnosticCode.WrongTypeArgumentCount,
                            $"'{parameter.Name}' is a type parameter and takes no type arguments.",
                            sourceName,
                            syntax.Span);

                        return _factory.ErrorType;
                    }

                    return parameter;
                }

                case AliasSymbol alias:
                    return ApplyAlias(syntax, alias, arguments, sourceName);

                default:
                    return Unresolved(syntax, sourceName);
            }
        }

        private TypeSymbol ApplyAlias(NamedTypeSyntax syntax, AliasSymbol alias, TypeSymbol[] arguments, string sourceName)
        {
            var target = ResolveAliasTarget(alias, syntax.Span, sourceName);

            if (alias.TypeParameters.Count != arguments.Length)
            {
                ReportError(
                    SurtrDiagnosticCode.WrongTypeArgumentCount,
                    $"Alias '{alias.Name}' takes {alias.TypeParameters.Count} type argument(s), not {arguments.Length}.",
                    sourceName,
                    syntax.Span);

                return _factory.ErrorType;
            }

            if (arguments.Length == 0)
                return target;

            var builder = _factory.BeginSubstitution();
            for (int i = 0; i < arguments.Length; i++)
                builder.Add(alias.TypeParameters[i], arguments[i]);

            return builder.ToSubstitution().Apply(target);
        }

        /// <summary>
        /// Resolves what an alias stands for, on first use.
        /// </summary>
        /// <remarks>
        /// Lazily, because an alias may target another one declared later, and detecting a cycle
        /// the same way <c>SurtrBuildState.Linking</c> does — by meeting something already being
        /// resolved — is what §2.7 asks for.
        /// </remarks>
        private TypeSymbol ResolveAliasTarget(AliasSymbol alias, SourceSpan span, string sourceName)
        {
            if (alias.Target is TypeSymbol resolved)
                return resolved;

            if (!_aliases.TryGetValue(alias, out var binding))
                return _factory.ErrorType;

            if (!_resolvingAliases.Add(alias))
            {
                ReportError(
                    SurtrDiagnosticCode.AliasCycle,
                    $"Alias '{alias.Name}' is defined in terms of itself.",
                    sourceName,
                    span);

                alias.Target = _factory.ErrorType;
                return alias.Target;
            }

            try
            {
                alias.Target = Resolve(binding.Syntax.Target, binding.Scope, binding.SourceName);
                return alias.Target;
            }
            finally
            {
                _resolvingAliases.Remove(alias);
            }
        }

        private TypeSymbol Unresolved(NamedTypeSyntax syntax, string sourceName)
        {
            string written = Join(syntax.Path, syntax.Path.Count);

            ReportError(
                SurtrDiagnosticCode.UnresolvedName,
                $"'{written}' does not name a type in scope.",
                sourceName,
                syntax.Span);

            return _factory.Error(written);
        }

        /// <summary>
        /// Reports, unless a caller is only asking whether a name happens to be a type.
        /// </summary>
        private void ReportError(SurtrDiagnosticCode code, string message, string sourceName, SourceSpan span)
        {
            if (_suppressed == 0)
                _diagnostics.ReportError(code, message, sourceName, span);
        }

        private static string Join(IReadOnlyList<string> path, int count)
        {
            if (count == 1)
                return path[0];

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    builder.Append('.');

                builder.Append(path[i]);
            }

            return builder.ToString();
        }

        private readonly struct AliasBinding
        {
            public AliasBinding(AliasDeclarationSyntax syntax, Scope scope, string sourceName)
            {
                Syntax = syntax;
                Scope = scope;
                SourceName = sourceName;
            }

            public AliasDeclarationSyntax Syntax { get; }

            public Scope Scope { get; }

            public string SourceName { get; }
        }
    }
}
