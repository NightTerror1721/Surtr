#nullable enable

using Surtr.Compiler.Binding.Symbols;
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

        private readonly Dictionary<string, ModuleSymbol> _modules =
            new Dictionary<string, ModuleSymbol>(StringComparer.Ordinal);

        private readonly Dictionary<AliasSymbol, AliasBinding> _aliases = new Dictionary<AliasSymbol, AliasBinding>();
        private readonly HashSet<AliasSymbol> _resolvingAliases = new HashSet<AliasSymbol>();

        /// <summary>Creates a resolver over one compilation's factory, imports and diagnostics.</summary>
        public TypeResolver(TypeSymbolFactory factory, MetadataImporter importer, SurtrDiagnosticBag diagnostics)
        {
            _factory = factory;
            _importer = importer;
            _diagnostics = diagnostics;
        }

        /// <summary>Makes a module's types reachable by fully qualified name.</summary>
        public void AddModule(ModuleSymbol module) => _modules[module.Path] = module;

        /// <summary>
        /// Records where an alias's target is written, so it can be resolved the first time the
        /// alias is used rather than in declaration order.
        /// </summary>
        public void RegisterAlias(AliasSymbol alias, AliasDeclarationSyntax syntax, Scope scope, string sourceName)
            => _aliases[alias] = new AliasBinding(syntax, scope, sourceName);

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
                    return _factory.Tuple(ResolveAll(tuple.ElementTypes, scope, sourceName));

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

            if (syntax.Path.Count == 1)
                return ResolveSimple(syntax, syntax.Path[0], arguments, scope, sourceName);

            // `Entity.Handle` before `game.core.Entity`: a name in scope is nearer than a module.
            if (TryResolveThroughScope(syntax, arguments, scope, sourceName, out var nested))
                return nested;

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

                if (!TryGetModule(modulePath, out var module))
                    continue;

                var candidates = module.FindTypes(syntax.Path[split]);
                if (candidates.Count == 0)
                    continue;

                NamedTypeSymbol? container = null;
                bool last = split == syntax.Path.Count - 1;
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
                    continue;

                for (int i = split + 1; i < syntax.Path.Count && container is not null; i++)
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
                    continue;

                resolved = Apply(syntax, container, arguments, sourceName);
                return true;
            }

            return false;
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
                _diagnostics.ReportError(
                    SurtrDiagnosticCode.WrongTypeArgumentCount,
                    $"No declaration of '{Join(syntax.Path, syntax.Path.Count)}' takes {arguments.Length} type argument(s).",
                    sourceName,
                    syntax.Span);

                return _factory.ErrorType;
            }

            // Two imports both answering: §2.1 makes that a problem here, at the use.
            _diagnostics.ReportError(
                SurtrDiagnosticCode.AmbiguousName,
                $"'{Join(syntax.Path, syntax.Path.Count)}' is brought into scope by more than one import.",
                sourceName,
                syntax.Span);

            return _factory.ErrorType;
        }

        private TypeSymbol Apply(NamedTypeSyntax syntax, Symbol symbol, TypeSymbol[] arguments, string sourceName)
        {
            switch (symbol)
            {
                case NamedTypeSymbol named:
                {
                    if (named.Arity != arguments.Length)
                    {
                        _diagnostics.ReportError(
                            SurtrDiagnosticCode.WrongTypeArgumentCount,
                            $"'{named.Name}' takes {named.Arity} type argument(s), not {arguments.Length}.",
                            sourceName,
                            syntax.Span);

                        return _factory.ErrorType;
                    }

                    return arguments.Length == 0 ? named : named.Construct(arguments);
                }

                case TypeParameterSymbol parameter:
                {
                    if (arguments.Length > 0)
                    {
                        _diagnostics.ReportError(
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
                _diagnostics.ReportError(
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
                _diagnostics.ReportError(
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

            _diagnostics.ReportError(
                SurtrDiagnosticCode.UnresolvedName,
                $"'{written}' does not name a type in scope.",
                sourceName,
                syntax.Span);

            return _factory.Error(written);
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
