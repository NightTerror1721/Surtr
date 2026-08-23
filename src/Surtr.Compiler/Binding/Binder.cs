#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;
using Surtr.Runtime.BuiltIns;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Turns a parsed compilation into symbols, in phases.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Phases exist because a member's signature can name a type declared later in the file, or in
    /// another file of the same module, so one pass cannot do it. The
    /// <see cref="DeclarationPhase"/> creates a symbol for every declared type without looking at a
    /// single signature; the <see cref="MemberPhase"/> then resolves base types, interfaces and
    /// every member signature against the complete set. After it, every type's surface is known —
    /// which is exactly the state <see cref="MetadataImporter"/> produces for a module that was
    /// compiled earlier, so a source type and an imported one become interchangeable.
    /// </para>
    /// <para>
    /// Binding bodies is a third phase and is not here yet. What this settles is everything a body
    /// would need to look up.
    /// </para>
    /// </remarks>
    public sealed class Binder : IDisposable
    {
        private readonly SurtrCompilation _compilation;
        private readonly SurtrDiagnosticBag _diagnostics;
        private readonly TypeSymbolFactory _factory;
        private readonly TypeResolver _resolver;

        private readonly Scope _globalScope = new Scope();

        private readonly Dictionary<string, ModuleSymbol> _modules =
            new Dictionary<string, ModuleSymbol>(StringComparer.Ordinal);

        private readonly Dictionary<string, Scope> _moduleScopes = new Dictionary<string, Scope>(StringComparer.Ordinal);
        private readonly Dictionary<string, Scope> _importScopes = new Dictionary<string, Scope>(StringComparer.Ordinal);

        private readonly Dictionary<string, List<ImportedModule>> _importedModules =
            new Dictionary<string, List<ImportedModule>>(StringComparer.Ordinal);

        // Modules a module re-exports (`export import module X.Y;`, §2.1): a consumer that
        // imports the re-exporting module sees these too. Accumulated during BindImports, handed
        // to `ModuleSymbol.ReExportedModules` once every import is bound.
        private readonly Dictionary<string, List<ImportedModule>> _reExportedModules =
            new Dictionary<string, List<ImportedModule>>(StringComparer.Ordinal);

        // Types a module re-exports by name (`export import X.Y;`, `export import X.{A,B}`, §2.1),
        // folded into the re-exporting module's own `Types` once every import is bound.
        private readonly Dictionary<string, List<NamedTypeSymbol>> _reExportedTypes =
            new Dictionary<string, List<NamedTypeSymbol>>(StringComparer.Ordinal);

        // Accumulated across every `extension` block bound for a module — at module level and
        // nested inside a class alike (§15) — then handed to `ModuleSymbol.ExtensionMethods` once,
        // after both `BindMembers` and `BindModuleMembers` have run for every module.
        private readonly Dictionary<string, List<MethodSymbol>> _extensionMethodsByModule =
            new Dictionary<string, List<MethodSymbol>>(StringComparer.Ordinal);

        private readonly Dictionary<string, List<PropertySymbol>> _extensionPropertiesByModule =
            new Dictionary<string, List<PropertySymbol>>(StringComparer.Ordinal);

        private readonly List<BodyBinding> _bodies = new List<BodyBinding>();

        // Methods whose declaration omitted the return type (§8) — the body is to infer it from.
        // A method may also be *added* here and yet never infer (recursion, conflicting returns),
        // which InferReturnTypes reports at the end of its pass.
        private readonly HashSet<MethodSymbol> _inferReturnTypes = new HashSet<MethodSymbol>();
        private readonly List<InitializerBinding> _initializers = new List<InitializerBinding>();
        private readonly List<DefaultBinding> _defaults = new List<DefaultBinding>();
        private readonly List<BoundFieldInitializer> _boundInitializers = new List<BoundFieldInitializer>();
        private readonly List<StaticBlockBinding> _staticBlocks = new List<StaticBlockBinding>();
        private readonly List<AttributeBinding> _attributes = new List<AttributeBinding>();
        private readonly List<BoundStaticBlock> _boundStaticBlocks = new List<BoundStaticBlock>();

        // Everything that runs when a module loads, in one list: what `InitializerOrder` needs is
        // every fragment of a module at once, and which of them is a field's and which a block's is
        // exactly what it compares.
        private readonly List<InitializerOrder.Fragment> _loadFragments = new List<InitializerOrder.Fragment>();
        private readonly List<ChainBinding> _chains = new List<ChainBinding>();

        private readonly Dictionary<MethodSymbol, BoundConstructorChain> _boundChains =
            new Dictionary<MethodSymbol, BoundConstructorChain>();

        // One counter across field initializers and `static { }` blocks alike, because §2.5 and §3.2
        // interleave them in source order and the emitter has to merge two lists back into one.
        private int _nextInitializerOrder;

        private readonly Dictionary<IReadOnlyList<DeclarationSyntax>, IReadOnlyList<DeclarationSyntax>> _flattened =
            new Dictionary<IReadOnlyList<DeclarationSyntax>, IReadOnlyList<DeclarationSyntax>>(ByReference.Instance);

        /// <summary>Keys a cache on the identity of a syntax list rather than on its contents.</summary>
        private sealed class ByReference : IEqualityComparer<IReadOnlyList<DeclarationSyntax>>
        {
            internal static readonly ByReference Instance = new ByReference();

            public bool Equals(IReadOnlyList<DeclarationSyntax> x, IReadOnlyList<DeclarationSyntax> y)
                => ReferenceEquals(x, y);

            public int GetHashCode(IReadOnlyList<DeclarationSyntax> obj)
                => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        private readonly Dictionary<MethodSymbol, BoundStatement> _bound =
            new Dictionary<MethodSymbol, BoundStatement>();
        private readonly Dictionary<MethodSymbol, string> _bodyFiles = new Dictionary<MethodSymbol, string>();
        private readonly Dictionary<NamedTypeSymbol, TypeBinding> _typeBindings = new Dictionary<NamedTypeSymbol, TypeBinding>();
        private readonly List<TypeBinding> _declared = new List<TypeBinding>();
        private readonly List<ConstraintBinding> _constraints = new List<ConstraintBinding>();

        // How many of them have had their bounds resolved. A method's type parameters are declared
        // while its signature is bound, which is after the first run, so the second picks up here.
        private int _constraintsBound;
        private readonly List<ConstantDeclaration> _constantDeclarations = new List<ConstantDeclaration>();

        private readonly Dictionary<string, List<MethodSymbol>> _constFunctions =
            new Dictionary<string, List<MethodSymbol>>(StringComparer.Ordinal);

        private ConstFolder? _constFolder;
        private string? _lastFoldFailure;

        private Binder(SurtrCompilation compilation)
        {
            _compilation = compilation;
            _diagnostics = compilation.Diagnostics;
            _factory = compilation.TypeFactory;
            _resolver = new TypeResolver(_factory, compilation.Importer, _diagnostics, compilation.Dependencies);
            MemberLookup = new MemberLookup(_factory, compilation.Importer);
            Conversions = new Conversions(_factory, MemberLookup);
            OverloadResolution = new OverloadResolution(Conversions);
            _signatures = new SignatureSet(_factory, _diagnostics);
            Constants = new ConstantEvaluator(compilation.Project.BuildConstants);
        }

        /// <summary>Folds the constants <c>const if</c> needs, over syntax (§7.3).</summary>
        public ConstantEvaluator Constants { get; }

        /// <summary>
        /// What runs a <c>const fun</c> at compile time, or <see langword="null"/> until
        /// <see cref="BindBodies"/> has run — and afterwards too, if the compilation declares none.
        /// </summary>
        public ConstFolder? ConstFolder => _constFolder;

        /// <summary>How one type reaches another.</summary>
        public Conversions Conversions { get; }

        /// <summary>Finding a member on a type.</summary>
        public MemberLookup MemberLookup { get; }

        /// <summary>
        /// Compares an <c>override</c> against the member it replaces by the emitted signature.
        /// Shared, not per-call, so the hierarchy checks do not build one per obligation.
        /// </summary>
        private readonly SignatureSet _signatures;

        /// <summary>Picking the member a call site means.</summary>
        public OverloadResolution OverloadResolution { get; }

        /// <summary>The bodies bound so far, by the method each belongs to.</summary>
        public IReadOnlyDictionary<MethodSymbol, BoundStatement> Bodies => _bound;

        /// <summary>
        /// The file each body was written in, by method.
        /// </summary>
        /// <remarks>
        /// Not on the bound tree, which knows spans and not files, and not on the symbol, which a
        /// module's several files share. It is here because emission raises diagnostics of its own
        /// and a span with the wrong file underlines the wrong text.
        /// </remarks>
        public IReadOnlyDictionary<MethodSymbol, string> BodyFiles => _bodyFiles;

        /// <summary>
        /// Every field initializer and enum case, in declaration order — which is the order they
        /// run in.
        /// </summary>
        public IReadOnlyList<BoundFieldInitializer> FieldInitializers => _boundInitializers;

        /// <summary>
        /// Every <c>static { }</c> block, carrying the position it runs at among the initializers
        /// beside it (§2.5, §3.2).
        /// </summary>
        public IReadOnlyList<BoundStaticBlock> StaticBlocks => _boundStaticBlocks;

        /// <summary>
        /// The <c>super(...)</c> or <c>this(...)</c> each constructor chains to, by constructor (§3.2).
        /// </summary>
        /// <remarks>
        /// A constructor with no entry here chains to nothing written; whether it still has to reach
        /// its base's parameterless constructor is a question about the base, and one the emitter
        /// answers rather than this table.
        /// </remarks>
        public IReadOnlyDictionary<MethodSymbol, BoundConstructorChain> ConstructorChains => _boundChains;

        /// <summary>The modules this compilation declares, by path.</summary>
        public IReadOnlyDictionary<string, ModuleSymbol> Modules => _modules;

        /// <summary>The type resolver, so a later phase resolves names the same way this one did.</summary>
        public TypeResolver Resolver => _resolver;

        /// <summary>The outermost scope, holding the built-in type names.</summary>
        public Scope GlobalScope => _globalScope;

        /// <summary>Runs the declaration and member phases over a compilation.</summary>
        public static Binder Bind(SurtrCompilation compilation)
        {
            var binder = new Binder(compilation);

            binder.SeedGlobalScope();
            binder.DeclarationPhase();
            binder.MemberPhase();
            return binder;
        }

        #region Global scope
        private void SeedGlobalScope()
        {
            // §1.1 makes these ordinary identifiers rather than keywords, so they live in the
            // outermost scope and a nearer declaration shadows them like any other name.
            _globalScope.TryDeclare("int", _factory.Int);
            _globalScope.TryDeclare("float", _factory.Float);
            _globalScope.TryDeclare("bool", _factory.Bool);
            _globalScope.TryDeclare("char", _factory.Char);
            _globalScope.TryDeclare("string", _factory.String);
            _globalScope.TryDeclare("range", _factory.Range);
            _globalScope.TryDeclare("void", _factory.Void);
            _globalScope.TryDeclare("unknown", _factory.Unknown);
            _globalScope.TryDeclare("never", _factory.Never);

            // §13: the standard library is imported implicitly - `surtr` is in scope in every file
            // with no `import` line, which is what lets `Exception` and `IComparable<T>` be written
            // unqualified everywhere the spec writes them. It sits in the outermost scope, so any
            // declaration of the same name shadows it rather than colliding with it.
            var library = _compilation.Importer.ImportModule(SurtrBuiltIns.Module);
            foreach (var type in library.Types)
                _globalScope.AddCandidate(type.Name, type);
        }
        #endregion

        #region Phase 1 - declarations
        private void DeclarationPhase()
        {
            CollectConstants();

            foreach (var sourceModule in _compilation.Modules.Values)
            {
                var module = new ModuleSymbol(sourceModule.Path);
                _modules.Add(sourceModule.Path, module);

                // Imports sit in a scope of their own between the module's declarations and the
                // built-ins, so a local declaration shadows an imported name instead of competing
                // with it - while two wildcard imports still collide with each other.
                var importScope = _globalScope.CreateChild();
                _importScopes.Add(sourceModule.Path, importScope);
                _moduleScopes.Add(sourceModule.Path, importScope.CreateChild());
                _resolver.AddModule(module);
            }

            foreach (var sourceModule in _compilation.Modules.Values)
            {
                var module = _modules[sourceModule.Path];
                var moduleScope = _moduleScopes[sourceModule.Path];

                var types = new List<NamedTypeSymbol>();
                var declaredHere = new HashSet<string>(StringComparer.Ordinal);

                foreach (var unit in sourceModule.Units)
                {
                    foreach (var declaration in Flatten(unit.Syntax.Declarations, unit.File.Path))
                    {
                        DeclareMember(
                            declaration, module, containingType: null, moduleScope, types, declaredHere, unit.File.Path);
                    }
                }

                module.Types = types;
            }

            // Imports come after every module's own types exist, so a name declared locally shadows
            // an imported one rather than racing it.
            foreach (var sourceModule in _compilation.Modules.Values)
                BindImports(sourceModule);

            // Re-exports fold into a module's own surface only after every import of every module
            // is bound: an aggregator's Types are complete before anything consumes them, and the
            // member-import lists grow with the transitive re-exports of what each module imports.
            ApplyReExports();
        }

        private void DeclareMember(
            DeclarationSyntax declaration,
            ModuleSymbol module,
            NamedTypeSymbol? containingType,
            Scope scope,
            List<NamedTypeSymbol> types,
            HashSet<string> declaredHere,
            string sourceName)
        {
            switch (declaration)
            {
                case TypeDeclarationSyntax type:
                    types.Add(DeclareType(type, module, containingType, scope, declaredHere, sourceName));
                    return;

                case AliasDeclarationSyntax alias:
                    DeclareAlias(alias, module, containingType, scope, declaredHere, sourceName);
                    return;

                case ConstIfDeclarationSyntax:
                    // Already resolved by Flatten before this list was walked.
                    return;
            }
        }

        /// <summary>
        /// Resolves every declaration-level <c>const if</c>, leaving only the declarations that
        /// exist in this build.
        /// </summary>
        /// <remarks>
        /// §7.3: a member in an untaken branch does not exist in any sense — no field slot, no
        /// vtable entry, no metadata. So the branch is dropped here, before anything looks at it,
        /// which is also what lets it name host types this build does not have.
        /// </remarks>
        private IReadOnlyList<DeclarationSyntax> Flatten(IReadOnlyList<DeclarationSyntax> declarations, string sourceName)
        {
            // Cached by identity: a module's declarations are flattened once in each phase, and a
            // condition that failed to fold should be reported once rather than once per phase.
            if (_flattened.TryGetValue(declarations, out var cached))
                return cached;

            var result = FlattenCore(declarations, sourceName);
            _flattened.Add(declarations, result);
            return result;
        }

        private IReadOnlyList<DeclarationSyntax> FlattenCore(IReadOnlyList<DeclarationSyntax> declarations, string sourceName)
        {
            bool any = false;
            for (int i = 0; i < declarations.Count && !any; i++)
                any = declarations[i] is ConstIfDeclarationSyntax;

            if (!any)
                return declarations;

            var flattened = new List<DeclarationSyntax>(declarations.Count);
            foreach (var declaration in declarations)
            {
                if (declaration is not ConstIfDeclarationSyntax conditional)
                {
                    flattened.Add(declaration);
                    continue;
                }

                if (!Constants.TryEvaluateCondition(conditional.Condition, out bool taken))
                {
                    ReportAt(
                        sourceName,
                        conditional.Condition.Span,
                        SurtrDiagnosticCode.NotAConstant,
                        "A 'const if' condition has to fold to a bool at compile time.");

                    continue;
                }

                flattened.AddRange(Flatten(taken ? conditional.Then : conditional.Else, sourceName));
            }

            return flattened;
        }

        /// <summary>
        /// Registers every module-level <c>const</c> before any <c>const if</c> is folded.
        /// </summary>
        /// <remarks>
        /// A <c>const</c> may be written below the <c>const if</c> that reads it, so they are all
        /// collected first and folded on demand.
        /// </remarks>
        private void CollectConstants()
        {
            foreach (var sourceModule in _compilation.Modules.Values)
            {
                foreach (var unit in sourceModule.Units)
                {
                    foreach (var declaration in unit.Syntax.Declarations)
                    {
                        if (declaration is FieldDeclarationSyntax { IsConst: true, Initializer: not null } constant)
                            AddConstant(constant, unit.File.Path);
                    }
                }
            }
        }

        private NamedTypeSymbol DeclareType(
            TypeDeclarationSyntax syntax,
            ModuleSymbol module,
            NamedTypeSymbol? containingType,
            Scope scope,
            HashSet<string> declaredHere,
            string sourceName)
        {
            var kind = syntax.Kind switch
            {
                TypeDeclarationKind.Interface => TypeSymbolKind.Interface,
                TypeDeclarationKind.Enum => TypeSymbolKind.Enum,
                TypeDeclarationKind.ValueClass => TypeSymbolKind.ValueClass,
                TypeDeclarationKind.Singleton => TypeSymbolKind.Singleton,
                _ => TypeSymbolKind.Class,
            };

            var symbol = _factory.DeclareType(syntax.Name, kind, module, containingType);

            // §3.1's two defaults: a top-level declaration is internal to its module, and one nested
            // inside a type is a member of it and private like any other — except inside an
            // interface, where §2.3 makes every member implicitly public and a nested type is not
            // the exception that would make an interface's own contract unreadable.
            symbol.Accessibility = Translate(
                syntax.Visibility,
                containingType is null
                    ? Accessibility.Internal
                    : containingType.TypeKind == TypeSymbolKind.Interface
                        ? Accessibility.Public
                        : Accessibility.Private);

            if (syntax.TypeParameters.Count > 0)
            {
                // An enum's cases are a fixed set of ordinals and a singleton has exactly one
                // instance created at module load, so neither has anything a type argument could
                // select. A generic declaration would be a degenerate type that cannot be named by
                // its arity and could even be "constructed" (§6, §2.4, §2.8). Report it and skip
                // creating the parameters so the symbol stays non-generic and unconstructable.
                if (syntax.Kind is TypeDeclarationKind.Enum or TypeDeclarationKind.Singleton)
                {
                    string kindWord = syntax.Kind == TypeDeclarationKind.Enum ? "enum" : "singleton";
                    _diagnostics.ReportError(
                        SurtrDiagnosticCode.InvalidGenericDeclaration,
                        $"'{syntax.Name}' is a {kindWord}; only one of it exists, so it cannot declare type parameters.",
                        sourceName,
                        syntax.TypeParameters[0].Span);
                }
                else if (syntax.TypeParameters.Count > 10)
                {
                    _diagnostics.ReportError(
                        SurtrDiagnosticCode.TooManyTypeParameters,
                        $"'{syntax.Name}' declares {syntax.TypeParameters.Count} type parameters; "
                            + "the descriptor form encodes at most ten.",
                        sourceName,
                        syntax.Span);
                }

                if (syntax.Kind is not (TypeDeclarationKind.Enum or TypeDeclarationKind.Singleton))
                {
                    var parameters = new TypeParameterSymbol[syntax.TypeParameters.Count];
                    for (int i = 0; i < parameters.Length; i++)
                        parameters[i] = _factory.DeclareTypeParameter(syntax.TypeParameters[i].Name, symbol, i);

                    symbol.SetTypeParameters(parameters);
                }
            }

            // Arity is part of identity, so a duplicate is a name *and* an arity that already
            // exist - `Result<T>` next to `Result<T, E>` is two declarations, not a collision.
            string key = syntax.Name + '`' + symbol.Arity.ToString();
            if (!declaredHere.Add(key))
            {
                _diagnostics.ReportError(
                    SurtrDiagnosticCode.DuplicateDeclaration,
                    $"'{syntax.Name}' is already declared with {symbol.Arity} type parameter(s) here.",
                    sourceName,
                    syntax.Span);
            }

            scope.AddCandidate(syntax.Name, symbol);
            RecordAttributes(symbol, syntax.Attributes, scope, sourceName);

            // A type's own parameters and its nested types are visible inside it and nowhere else,
            // but they cannot live in one scope: a nested type's declaration must not see its
            // container's type parameters (§6, the static-nested rule), yet its body may still name
            // its container's other nested types - a sibling. So the container gets two scopes:
            // `nestedScope` holds its nested types' names, and `typeScope` (a child of it) holds its
            // type parameters and is what its own members bind against. A nested type's declaration
            // binds against `nestedScope`, which puts the sibling names on its chain and keeps the
            // parameters off it.
            var nestedScope = scope.CreateChild();
            var typeScope = nestedScope.CreateChild();
            foreach (var parameter in symbol.TypeParameters)
                typeScope.TryDeclare(parameter.Name, parameter);

            // A bound may name the parameter it bounds (`<T : IComparable<T>>`), so it is resolved
            // in the scope that already holds the parameters rather than the one outside them.
            if (symbol.Arity > 0)
                _constraints.Add(new ConstraintBinding(symbol.TypeParameters, syntax.TypeParameters, typeScope, sourceName));

            var nested = new List<NamedTypeSymbol>();
            var nestedNames = new HashSet<string>(StringComparer.Ordinal);

            // A `const` inside the type is in scope for a `const if` inside it too.
            foreach (var member in syntax.Members)
            {
                if (member is FieldDeclarationSyntax { IsConst: true, Initializer: not null } constant)
                    AddConstant(constant, sourceName);
            }

            var members = Flatten(syntax.Members, sourceName);

            foreach (var member in members)
            {
                // A nested type's name is a member of its container (§2.6), so it is declared
                // against `nestedScope` rather than `scope` - it must not be visible at the
                // container's outside, or two containers' same-named nested types collide there.
                // The static-nested rule is why it is not `typeScope` either: `typeScope` carries
                // `symbol`'s own type parameters, and a nested type's declaration must not see
                // them; only the members bound directly on `symbol` itself do.
                if (member is TypeDeclarationSyntax or AliasDeclarationSyntax)
                    DeclareMember(member, module, symbol, nestedScope, nested, nestedNames, sourceName);
            }

            symbol.NestedTypes = nested;

            var binding = new TypeBinding(symbol, syntax, members, typeScope, module, sourceName);
            _typeBindings.Add(symbol, binding);
            _declared.Add(binding);

            return symbol;
        }

        private void DeclareAlias(
            AliasDeclarationSyntax syntax,
            ModuleSymbol module,
            NamedTypeSymbol? containingType,
            Scope scope,
            HashSet<string> declaredHere,
            string sourceName)
        {
            var alias = new AliasSymbol(syntax.Name, (Symbol?)containingType ?? module)
            {
                Accessibility = Translate(syntax.Visibility, containingType is null ? Accessibility.Internal : Accessibility.Private),
            };

            if (syntax.TypeParameters.Count > 0)
            {
                var parameters = new TypeParameterSymbol[syntax.TypeParameters.Count];
                for (int i = 0; i < parameters.Length; i++)
                    parameters[i] = _factory.DeclareTypeParameter(syntax.TypeParameters[i].Name, alias, i);

                alias.TypeParameters = parameters;
            }

            string key = syntax.Name + '`' + alias.TypeParameters.Count.ToString();
            if (!declaredHere.Add(key))
            {
                _diagnostics.ReportError(
                    SurtrDiagnosticCode.DuplicateDeclaration,
                    $"'{syntax.Name}' is already declared here.",
                    sourceName,
                    syntax.Span);
            }

            scope.AddCandidate(syntax.Name, alias);

            // An alias's own parameters are in scope in its target, and nowhere else.
            var aliasScope = scope.CreateChild();
            foreach (var parameter in alias.TypeParameters)
                aliasScope.TryDeclare(parameter.Name, parameter);

            _resolver.RegisterAlias(alias, syntax, aliasScope, sourceName);
        }

        private void BindImports(SurtrSourceModule sourceModule)
        {
            var scope = _importScopes[sourceModule.Path];
            var imported = new List<ImportedModule>();
            _importedModules.Add(sourceModule.Path, imported);

            var reExported = new List<ImportedModule>();
            _reExportedModules.Add(sourceModule.Path, reExported);

            var reExportedTypes = new List<NamedTypeSymbol>();
            _reExportedTypes.Add(sourceModule.Path, reExportedTypes);

            foreach (var unit in sourceModule.Units)
            {
                foreach (var import in unit.Syntax.Imports)
                {
                    // `import module X.Y;` names a whole module and brings its full surface — types
                    // and module-level members alike — exactly as a wildcard over that one module
                    // would, but without recursing into submodules. It is the explicit way to say
                    // "this file, all of it".
                    if (import.IsModule)
                    {
                        if (TryGetModuleSymbol(Join(import.Path, import.Path.Count), out var whole))
                        {
                            ImportModuleSurface(scope, imported, whole);
                            ReExport(import, sourceModule, reExported, whole);
                        }
                        else
                        {
                            ReportAt(
                                unit.File.Path,
                                import.Span,
                                SurtrDiagnosticCode.UnresolvedImport,
                                $"No module provides '{string.Join(ModulePath.Separator.ToString(), import.Path)}'.");
                        }

                        continue;
                    }

                    if (import.IsWildcard)
                    {
                        // A directory wildcard (§2.1, Fase 9) reaches the exact module if it
                        // exists, plus every submodule nested under it - a module is a directory,
                        // so `a.b` is a different one from `a`, not a member of it, and neither
                        // resolving nor missing the other stops the rest from being brought in.
                        string wildcardPath = Join(import.Path, import.Path.Count);

                        if (TryGetModuleSymbol(wildcardPath, out var module))
                        {
                            ImportWildcardModule(scope, imported, module);
                            ReExport(import, sourceModule, reExported, module);
                        }

                        foreach (var nested in ModulesUnderPrefix(wildcardPath))
                        {
                            ImportWildcardModule(scope, imported, nested);
                            ReExport(import, sourceModule, reExported, nested);
                        }

                        continue;
                    }

                    // `import X as Y` names a module by its whole path - unlike the named-import
                    // split below, there is no trailing type name to peel off the end.
                    if (import.Alias is not null)
                    {
                        if (TryGetModuleSymbol(Join(import.Path, import.Path.Count), out var aliased))
                        {
                            ReExport(import, sourceModule, reExported, aliased);

                            if (!scope.TryDeclareModuleAlias(import.Alias, aliased))
                            {
                                ReportAt(
                                    unit.File.Path,
                                    import.Span,
                                    SurtrDiagnosticCode.DuplicateModuleAlias,
                                    $"'{import.Alias}' is already used as a module alias in this module.");
                            }
                        }

                        continue;
                    }

                    // `import X.{A, B}` names a module by its whole path too, then brings each
                    // listed name in on its own - the same type-namespace candidate a repeated
                    // `import X.A; import X.B;` would add, just written once.
                    if (import.Members is not null)
                    {
                        if (TryGetModuleSymbol(Join(import.Path, import.Path.Count), out var listed))
                        {
                            bool anyMember = false;
                            var only = new List<string>();

                            foreach (var memberName in import.Members)
                            {
                                bool brought = false;
                                foreach (var type in listed.FindTypes(memberName))
                                {
                                    scope.AddCandidate(type.Name, type);
                                    ReExportType(import, reExportedTypes, type);
                                    brought = true;
                                }

                                // §2.1's broader member import: a name that is not a type may still
                                // be a module-level function, variable or property, and a selective
                                // import brings exactly that member — not the whole module.
                                if (!brought && ModuleDeclaresMember(listed, memberName))
                                {
                                    only.Add(memberName);
                                    anyMember = true;
                                }
                            }

                            if (anyMember)
                            {
                                ImportMembers(scope, imported, listed, only);
                                ReExportMembers(import, sourceModule, reExported, listed, only);
                            }
                        }

                        continue;
                    }

                    // A path that resolves entirely as a module - or has submodules nested under
                    // it, even without a module of its own (a directory holding only
                    // subdirectories) - is the longest possible module prefix, so §2.1 makes it
                    // win over any shorter prefix + trailing type name the loop below would try:
                    // `import ModulePath;` is then equivalent to `import ModulePath.*;`.
                    string wholePath = Join(import.Path, import.Path.Count);
                    bool matchedWhole = false;

                    if (TryGetModuleSymbol(wholePath, out var wholeModule))
                    {
                        ImportWildcardModule(scope, imported, wholeModule);
                        ReExport(import, sourceModule, reExported, wholeModule);
                        matchedWhole = true;
                    }

                    foreach (var nested in ModulesUnderPrefix(wholePath))
                    {
                        ImportWildcardModule(scope, imported, nested);
                        ReExport(import, sourceModule, reExported, nested);
                        matchedWhole = true;
                    }

                    if (matchedWhole)
                        continue;

                    // A named import brings exactly one name in, and the longest module prefix says
                    // where the module ends.
                    for (int split = import.Path.Count - 1; split > 0; split--)
                    {
                        if (!TryGetModuleSymbol(Join(import.Path, split), out var module))
                            continue;

                        string name = import.Path[split];
                        bool brought = false;

                        foreach (var type in module.FindTypes(name))
                        {
                            scope.AddCandidate(type.Name, type);
                            ReExportType(import, reExportedTypes, type);
                            brought = true;
                        }

                        // §2.1's broader member import: a named import that does not name a type may
                        // name a module-level function, variable or property instead.
                        if (!brought && ModuleDeclaresMember(module, name))
                        {
                            ImportMembers(scope, imported, module, new List<string> { name });
                            ReExportMembers(import, sourceModule, reExported, module, new List<string> { name });
                        }

                        break;
                    }
                }
            }
        }

        /// <summary>Whether a module declares a module-level member (function, variable or property) of this name.</summary>
        /// <remarks>
        /// Read from the module's own units rather than from <see cref="ModuleSymbol.Methods"/>,
        /// <see cref="ModuleSymbol.Fields"/> or <see cref="ModuleSymbol.Properties"/>: imports bind
        /// before the member phase fills those, so the source declarations are the only complete
        /// answer at this point.
        /// </remarks>
        private bool ModuleDeclaresMember(ModuleSymbol module, string name)
        {
            if (!_compilation.Modules.TryGetValue(module.Path, out var sourceModule))
                return false;

            foreach (var unit in sourceModule.Units)
            {
                foreach (var declaration in unit.Syntax.Declarations)
                {
                    switch (declaration)
                    {
                        case FieldDeclarationSyntax field when string.Equals(field.Name, name, StringComparison.Ordinal):
                        case PropertyDeclarationSyntax property when string.Equals(property.Name, name, StringComparison.Ordinal):
                        case MethodDeclarationSyntax method when string.Equals(method.Name, name, StringComparison.Ordinal):
                            return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Imports selected members of a module — the unit of a named or selective import that
        /// names a module-level function, variable or property, §2.1. The module joins the
        /// member-import list filtered to exactly those names, so nothing else from it leaks into
        /// bare-name resolution.
        /// </summary>
        private static void ImportMembers(Scope scope, List<ImportedModule> imported, ModuleSymbol module, IReadOnlyList<string> only)
        {
            foreach (var name in only)
            {
                foreach (var type in module.FindTypes(name))
                    scope.AddCandidate(type.Name, type);
            }

            AddImported(imported, module, only);
        }

        private static void AddImported(List<ImportedModule> imported, ModuleSymbol module, IReadOnlyList<string>? only)
        {
            for (int i = 0; i < imported.Count; i++)
            {
                if (!ReferenceEquals(imported[i].Module, module))
                    continue;

                // A module already brought in whole never narrows; a filtered entry merges names.
                if (imported[i].Only is null || only is null)
                    return;

                var merged = new HashSet<string>(imported[i].Only, StringComparer.Ordinal);
                foreach (var name in only)
                    merged.Add(name);

                imported[i] = new ImportedModule(module, new List<string>(merged));
                return;
            }

            imported.Add(only is null
                ? new ImportedModule(module)
                : new ImportedModule(module, new List<string>(only)));
        }

        /// <summary>
        /// Records a module as re-exported by the importing module when the import carries
        /// <c>export</c> (§2.1): the module's types join the re-exporting module's own
        /// <see cref="ModuleSymbol.Types"/> (so <c>Aggregator.Type</c> works), and the module
        /// itself is tracked so its module-level members become reachable to a consumer of the
        /// re-exporter.
        /// </summary>
        private void ReExport(
            ImportSyntax import,
            SurtrSourceModule sourceModule,
            List<ImportedModule> reExported,
            ModuleSymbol target)
        {
            if (!import.IsExport)
                return;

            AddImported(reExported, target, only: null);

            // A consumer of the re-exporter can reach the re-exported module's members, so the
            // re-exporter depends on it for load order, exactly as if it had imported it outright.
            _compilation.Dependencies.AddDependency(sourceModule.Path, target.Path);
        }

        /// <summary>Records selected members of a module as re-exported, the member form of <see cref="ReExport"/>.</summary>
        private void ReExportMembers(
            ImportSyntax import,
            SurtrSourceModule sourceModule,
            List<ImportedModule> reExported,
            ModuleSymbol target,
            IReadOnlyList<string> only)
        {
            if (!import.IsExport)
                return;

            AddImported(reExported, target, only);
            _compilation.Dependencies.AddDependency(sourceModule.Path, target.Path);
        }

        /// <summary>Records one type re-exported by name, folded into the re-exporter's own types.</summary>
        private static void ReExportType(ImportSyntax import, List<NamedTypeSymbol> reExportedTypes, NamedTypeSymbol type)
        {
            if (!import.IsExport)
                return;

            if (!reExportedTypes.Contains(type))
                reExportedTypes.Add(type);
        }

        /// <summary>
        /// Imports one module's whole surface — types into the scope and the module into the
        /// member-import list — the unit of <c>import module X.Y;</c>.
        /// </summary>
        private static void ImportModuleSurface(Scope scope, List<ImportedModule> imported, ModuleSymbol module)
        {
            AddTypesToScope(scope, module);

            AddImported(imported, module, only: null);
        }

        /// <summary>
        /// Folds what a module re-exported into its own surface: its re-exported modules' types and
        /// its by-name re-exported types join <see cref="ModuleSymbol.Types"/>, and the re-exported
        /// modules are recorded on <see cref="ModuleSymbol.ReExportedModules"/>. Runs once, after
        /// every module's imports are bound, so an aggregator's surface is complete before anything
        /// consumes it.
        /// </summary>
        private void ApplyReExports()
        {
            // The transitive closure of what each module re-exports, walked from the direct
            // re-exports BindImports recorded: if B re-exports A and A re-exports D, then B
            // re-exports D too — a consumer of B sees everything A and D expose, at any depth.
            // A re-export filtered to some members stays filtered; only a whole-module re-export
            // extends through the re-exported module's own re-exports.
            var closure = new Dictionary<string, List<ImportedModule>>(StringComparer.Ordinal);

            foreach (var module in _modules.Values)
            {
                var reExported = _reExportedModules.TryGetValue(module.Path, out var direct)
                    ? direct
                    : (IReadOnlyList<ImportedModule>)Array.Empty<ImportedModule>();

                var visited = new HashSet<ModuleSymbol>();
                var result = new List<ImportedModule>();
                VisitReExports(reExported, visited, result);
                closure.Add(module.Path, result);

                module.ReExportedModules = result;
            }

            // Collect the types each module re-exports — of every module it re-exports whole, plus
            // the ones it re-exported by name. Kept apart from Types so the emitter still sees only
            // the types this module truly declares; FindTypes and the import scopes see both.
            foreach (var module in _modules.Values)
            {
                var types = new List<NamedTypeSymbol>();

                foreach (var reExportedModule in closure[module.Path])
                {
                    foreach (var type in reExportedModule.Module.Types)
                    {
                        if (!types.Contains(type))
                            types.Add(type);
                    }
                }

                if (_reExportedTypes.TryGetValue(module.Path, out var byName))
                {
                    foreach (var type in byName)
                    {
                        if (!types.Contains(type))
                            types.Add(type);
                    }
                }

                module.ReExportedTypes = types;
            }

            // A module that imports the re-exporter sees its re-exported surface too, exactly as if the
            // re-exporter had declared it: the re-exported types join the consumer's import scope,
            // and the re-exported modules join its member-import list. This is what makes
            // `import Aggregator.*` reach the types and module-level members of everything the
            // aggregator re-exported. Runs after ReExportedTypes/ReExportedModules are populated,
            // so a consumer's own BindImports (which ran earlier) never saw them yet.
            foreach (var module in _modules.Values)
            {
                if (!_importedModules.TryGetValue(module.Path, out var imported))
                    continue;

                var scope = _importScopes[module.Path];

                foreach (var directImport in new List<ImportedModule>(imported))
                {
                    if (!closure.TryGetValue(directImport.Module.Path, out var reExported))
                        continue;

                    // A consumer that only brought some of the re-exporter's members still reaches
                    // every re-exported member; the filter applies to the re-exporter itself, not
                    // to what it re-exports.
                    foreach (var reExportedModule in reExported)
                    {
                        if (directImport.Only is not null
                            && reExportedModule.Only is not null
                            && !directImport.Only.Intersect(reExportedModule.Only).Any())
                            continue;

                        AddImported(imported, reExportedModule.Module, reExportedModule.Only);
                    }

                    // The re-exporter's re-exported types become reachable unqualified from the
                    // consumer, exactly as the re-exporter's own types already are.
                    if (directImport.Only is null)
                    {
                        foreach (var type in directImport.Module.ReExportedTypes)
                            scope.AddCandidate(type.Name, type);
                    }
                }
            }
        }

        private static void VisitReExports(
            IReadOnlyList<ImportedModule> modules,
            HashSet<ModuleSymbol> visited,
            List<ImportedModule> result)
        {
            foreach (var imported in modules)
            {
                if (!visited.Add(imported.Module))
                    continue;

                result.Add(imported);

                // Only a whole-module re-export extends through the re-exported module's own
                // re-exports; a filtered one names the members directly and stops here.
                if (imported.Only is null)
                    VisitReExports(imported.Module.ReExportedModules, visited, result);
            }
        }

        /// <summary>The modules a wildcard import brought into scope, whose members are reachable unqualified.</summary>
        private IReadOnlyList<ImportedModule> ImportedBy(ModuleSymbol module)
            => _importedModules.TryGetValue(module.Path, out var imported)
                ? imported
                : (IReadOnlyList<ImportedModule>)Array.Empty<ImportedModule>();

        private bool TryGetModuleSymbol(string modulePath, out ModuleSymbol module)
            => _modules.TryGetValue(modulePath, out module!)
                || _compilation.Importer.TryGetModuleSymbol(modulePath, out module!);

        /// <summary>Brings one module's types and members into scope for a wildcard import, the exact module or one nested under it.</summary>
        private static void ImportWildcardModule(Scope scope, List<ImportedModule> imported, ModuleSymbol module)
        {
            AddTypesToScope(scope, module);

            // §2.5 makes a module a container of members, so a wildcard import brings its
            // functions and variables in too — not only its types.
            AddImported(imported, module, only: null);
        }

        /// <summary>Adds a module's own types and its re-exported types to a scope, as import candidates.</summary>
        private static void AddTypesToScope(Scope scope, ModuleSymbol module)
        {
            foreach (var type in module.Types)
                scope.AddCandidate(type.Name, type);

            // Re-exported types are visible to a consumer of the re-exporter exactly as its own
            // are: `import Aggregator.*` brings them in unqualified.
            foreach (var type in module.ReExportedTypes)
                scope.AddCandidate(type.Name, type);
        }

        /// <summary>
        /// Every module in this compilation whose path sits strictly under <paramref name="prefix"/>
        /// - what lets a directory wildcard (§2.1, Fase 9) reach a submodule. A module is a
        /// directory, so `a.b` is a different one from `a`, not a member of it, and an exact-match
        /// lookup on `a` alone would never find it. Restricted to this compilation's own modules,
        /// the same set <c>ModuleDependencyGraph</c> already reasons about — an already-compiled
        /// image has no directory index for this to walk.
        /// </summary>
        private IEnumerable<ModuleSymbol> ModulesUnderPrefix(string prefix)
        {
            string dotted = prefix + ModulePath.Separator;
            foreach (var module in _modules.Values)
            {
                if (module.Path.StartsWith(dotted, StringComparison.Ordinal))
                    yield return module;
            }
        }
        #endregion

        #region Phase 2 - hierarchy and members
        /// <summary>
        /// Tells the resolver where the source it is about to resolve sits, which is what §3.1's
        /// accessibility is measured against.
        /// </summary>
        /// <remarks>
        /// Set at each point that starts resolving a new piece of source rather than threaded through
        /// every <c>Resolve</c> call: the module and type are the same for everything one of those
        /// pieces contains, and a parameter would have to be carried through a dozen signatures that
        /// have no other use for it.
        /// </remarks>
        private void EnterContext(ModuleSymbol? module, NamedTypeSymbol? type)
        {
            _resolver.CurrentModule = module;
            _resolver.CurrentType = type;
        }

        private void MemberPhase()
        {
            foreach (var binding in _declared)
            {
                EnterContext(binding.Module, binding.Symbol);
                BindHierarchy(binding);
            }

            // Nothing says which module a constraint list belongs to, so it resolves with no context
            // and the check fails open rather than inventing an answer.
            EnterContext(null, null);
            BindConstraints();

            foreach (var binding in _declared)
            {
                EnterContext(binding.Module, binding.Symbol);
                BindMembers(binding);
            }

            foreach (var module in _modules.Values)
            {
                EnterContext(module, null);
                BindModuleMembers(module);
            }

            // Every `extension` block for a module is bound by now, whether written at module level
            // (BindModuleMembers, just above) or nested inside one of its classes (BindMembers, just
            // above that) — this is the one place both contributions are combined.
            foreach (var module in _modules.Values)
            {
                module.ExtensionMethods = _extensionMethodsByModule.TryGetValue(module.Path, out var extensions)
                    ? extensions
                    : Array.Empty<MethodSymbol>();

                module.ExtensionProperties = _extensionPropertiesByModule.TryGetValue(module.Path, out var extensionProperties)
                    ? extensionProperties
                    : Array.Empty<PropertySymbol>();
            }

            // Again, for the type parameters a method's own signature declared: those did not exist
            // when the first run went through, and a bound nobody resolved is a bound a body cannot
            // call anything through.
            EnterContext(null, null);
            BindConstraints();

            // Before the hierarchy checks, because those compare return types against a contract:
            // a method that omitted its own is inferred from its body here, so an override or an
            // interface implementation that *does* declare one is compared against something real.
            InferReturnTypes();

            // After every type has its members, because the question is about a base class's, and
            // nothing says a base is bound before what extends it.
            foreach (var binding in _declared)
            {
                CheckSealedOverrides(binding);
                CheckOverrideRequired(binding);
                CheckBaseConstructorIsReachable(binding);
                CheckMembersImplemented(binding);
            }

            // Last, because a bound like `<T : IComparable<T>>` names a type whose own hierarchy is
            // still being resolved while the signatures above are bound.
            _resolver.VerifyConstraints(Conversions);
        }

        /// <summary>
        /// Rejects an <c>override</c> of a member the base declared <c>sealed</c> (§2.2).
        /// </summary>
        /// <remarks>
        /// The runtime does not check it — <c>SurtrTypeLinker</c> replaces the vtable entry either
        /// way — so this is the only thing standing between `sealed` and a member that says it
        /// closes its branch and does not.
        /// </remarks>
        private void CheckSealedOverrides(TypeBinding binding)
        {
            foreach (var member in binding.Symbol.Members)
            {
                if (member is not MethodSymbol { IsOverride: true } method)
                    continue;

                for (var walk = binding.Symbol.BaseType; walk is not null; walk = walk.BaseType)
                {
                    if (Overridden(walk, method) is not MethodSymbol overridden)
                        continue;

                    if (overridden.IsSealed)
                    {
                        Report(
                            SurtrDiagnosticCode.InvalidBaseType,
                            binding,
                            binding.Syntax.Span,
                            $"'{overridden.ContainingType?.Name}.{method.Name}' is sealed, so '{binding.Symbol.Name}' cannot override it.");
                    }

                    break;
                }
            }
        }

        /// <summary>
        /// Rejects a member that matches an inherited <c>virtual</c>/<c>abstract</c> member's full
        /// signature without declaring <c>override</c> (§3.2) - no implicit override, so a name and
        /// parameter list that happen to collide with a base's vtable slot must say so explicitly
        /// instead of silently hiding it.
        /// </summary>
        /// <remarks>
        /// Matched on the same erased name-plus-parameter <see cref="SignatureSet.MatchesSlot"/> the
        /// runtime's <c>SignatureKey</c> uses to place vtable slots (return type deliberately
        /// excluded — a derived member sharing the slot must say so), rather than the looser
        /// name-plus-count <see cref="Overridden"/> uses for the sealed check above, so two overloads
        /// that merely share a name and arity are not mistaken for one hiding the other. Only a
        /// <c>Direct</c>-dispatch base member is exempt - it has no vtable slot to begin with,
        /// so nothing is silently lost by not overriding it.
        /// </remarks>
        private void CheckOverrideRequired(TypeBinding binding)
        {
            var symbol = binding.Symbol;

            foreach (var member in symbol.Members)
            {
                if (member is not MethodSymbol { IsOverride: false, IsStatic: false, Role: MethodRole.Normal } method)
                    continue;

                for (var walk = symbol.BaseType; walk is not null; walk = walk.BaseType)
                {
                    MethodSymbol? hidden = null;

                    foreach (var candidate in walk.Members)
                    {
                        if (candidate is MethodSymbol { Dispatch: MethodDispatch.Virtual or MethodDispatch.Abstract } virtualCandidate
                            && _signatures.MatchesSlot(method, virtualCandidate))
                        {
                            hidden = virtualCandidate;
                            break;
                        }
                    }

                    if (hidden is null)
                        continue;

                    Report(
                        SurtrDiagnosticCode.MissingOverride,
                        binding,
                        binding.Syntax.Span,
                        $"'{symbol.Name}.{method.Name}' matches '{hidden.ContainingType?.Name}.{hidden.Name}', which is virtual - mark it 'override' or give it a visibly different signature.");
                    break;
                }
            }
        }

        /// <summary>
        /// Rejects a concrete class that leaves an interface member or an inherited <c>abstract</c>
        /// member without a real body (§2.2, §2.3).
        /// </summary>
        /// <remarks>
        /// <c>SurtrTypeLinker</c> (<c>Runtime/Classes/SurtrTypeLinker.cs</c>) already refuses this at
        /// load time — but only at load time, and <c>surtrc build</c> never loads what it compiles,
        /// so a class this incomplete used to write a <c>.surtrc</c> image to disk with no error at
        /// all. This runs the same check here, early enough to name the source class rather than
        /// leave the failure to surface as an unlabelled runtime exception wherever the image is
        /// eventually loaded.
        /// </remarks>
        private void CheckMembersImplemented(TypeBinding binding)
        {
            var symbol = binding.Symbol;

            // An interface owes nothing to itself; a construction reports through its declaration.
            // Unlike an ordinary abstract member, an abstract class is *not* otherwise exempt here:
            // SurtrTypeLinker.BuildInterfaceDispatch indexes only Virtual/Abstract dispatch, never
            // Direct, so an abstract class that implements an interface but never even redeclares a
            // member `abstract` leaves no vtable slot for the runtime to route the interface call
            // through at all - a load-time crash a fully abstract-exempt check here would miss.
            if (symbol.TypeKind == TypeSymbolKind.Interface || !symbol.IsDefinition)
                return;

            var visited = new HashSet<NamedTypeSymbol>();
            var contracts = new List<NamedTypeSymbol>();
            CollectInterfaces(symbol, visited, contracts);

            foreach (var contract in contracts)
            {
                foreach (var member in contract.Members)
                {
                    if (member is MethodSymbol { Dispatch: MethodDispatch.Abstract } required)
                        CheckObligation(binding, symbol, contract, required);
                }
            }

            for (var ancestor = symbol.BaseType; ancestor is not null; ancestor = SubstitutedBase(ancestor))
            {
                foreach (var member in ancestor.Members)
                {
                    if (member is MethodSymbol { Dispatch: MethodDispatch.Abstract } required)
                        CheckObligation(binding, symbol, ancestor, required);
                }
            }
        }

        /// <summary>
        /// Confirms <paramref name="symbol"/>'s own hierarchy answers one abstract obligation named
        /// by <paramref name="contract"/> (an interface, or a base class), mirroring the two checks
        /// <c>SurtrTypeLinker</c> makes at load time.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>BuildInterfaceDispatch</c> needs a vtable slot to exist for it at all. A
        /// <c>virtual</c>/<c>override</c> or another <c>abstract</c> declaration occupies one
        /// directly; a plain <c>Direct</c> member never enters the vtable itself, but
        /// <see cref="CodeGen.ModuleEmitter.EmitBridges"/> gives it a synthetic forwarding bridge in
        /// the slot instead, so it satisfies the obligation without giving up <c>Direct</c> dispatch
        /// anywhere else it's called. <c>VerifyConcrete</c> then refuses to let a concrete class
        /// leave the slot it found still abstract.
        /// </para>
        /// <para>
        /// The signature check is the half the runtime cannot see. <c>SurtrTypeLinker</c> matches by
        /// name plus <em>erased</em> parameter types, so a member whose erased shape matches but
        /// whose types differ — <c>get(int): T</c> where <c>IReadOnlyCollection&lt;int&gt;</c>
        /// declares <c>get(int): int</c> — links cleanly and then reads its return as the contract's
        /// type at a call site. So <paramref name="required"/> is read as <paramref name="contract"/>
        /// declares it (substituted through the construction) and compared against what was found,
        /// by the emitted signature, return included.
        /// </para>
        /// </remarks>
        private void CheckObligation(TypeBinding binding, NamedTypeSymbol symbol, NamedTypeSymbol contract, MethodSymbol required)
        {
            var found = FindMember(symbol, required.Name, required.Parameters.Count);
            if (found is null)
            {
                Report(
                    SurtrDiagnosticCode.MissingImplementation,
                    binding,
                    binding.Syntax.Span,
                    $"'{symbol.Name}' does not implement '{contract.Name}.{required.Name}'; declare a matching member, "
                        + $"or mark it 'abstract' on '{symbol.Name}' to leave it for a subclass.");
                return;
            }

            if (!symbol.IsAbstract && found.Dispatch == MethodDispatch.Abstract)
            {
                Report(
                    SurtrDiagnosticCode.MissingImplementation,
                    binding,
                    binding.Syntax.Span,
                    $"'{symbol.Name}' does not implement '{contract.Name}.{required.Name}'; "
                        + $"implement it, or declare '{symbol.Name}' abstract.");
            }

            // §8: a member with no return type was reported by InferReturnTypes — either its declaration
            // omitted one and the contract check there kept it out of inference, or the inference
            // failed. Comparing its empty return against the contract's would only add a second,
            // misleading mismatch on top, so the signature check is skipped for it.
            if (found.ReturnType.IsError)
                return;

            var substituted = MemberLookup.SubstituteMethod(required, contract.SubstitutionFromArguments(_factory));
            if (!_signatures.Matches(substituted, found))
            {
                Report(
                    SurtrDiagnosticCode.OverrideSignatureMismatch,
                    binding,
                    binding.Syntax.Span,
                    $"'{symbol.Name}.{found.ToDisplayString()}' does not implement "
                        + $"'{contract.Name}.{substituted.ToDisplayString()}' as '{contract.ToDisplayString()}' declares it.");
            }
        }

        /// <summary>
        /// Every abstract obligation <paramref name="type"/> does not yet answer — an interface
        /// member (its own, inherited, or reached through an interface-to-interface extension) or an
        /// inherited <c>abstract</c> class member with no matching member anywhere in its own
        /// hierarchy. Read-only, and exposed for tooling: the language server's "implement missing
        /// members" code action needs exactly this answer, and re-deriving the substitution-aware
        /// interface walk itself in <c>Surtr.LanguageServer</c> would risk repeating the two real
        /// bugs <c>5cca11a</c>/<c>0bef8a2</c> already found and fixed here — a member reached through
        /// a constructed built-in interface reading its own unsubstituted type parameter instead of
        /// the receiver's. This method answers by calling the exact same private helpers
        /// <see cref="CheckMembersImplemented"/> does (<see cref="CollectInterfaces"/>,
        /// <see cref="FindMember"/>, <see cref="SubstitutedBase"/>), so the two can never drift apart
        /// on what counts as "missing".
        /// </summary>
        /// <remarks>
        /// Deliberately narrower than every failure <see cref="CheckObligation"/> reports: a member
        /// that exists but has the wrong signature (<see cref="SurtrDiagnosticCode.OverrideSignatureMismatch"/>)
        /// is a correction, not something a stub can fill in, so it is left out here on purpose.
        /// </remarks>
        public IReadOnlyList<MissingMember> MissingAbstractMembers(NamedTypeSymbol type)
        {
            var result = new List<MissingMember>();

            // Same exemption CheckMembersImplemented uses: an interface owes nothing to itself, and
            // a construction reports through its own declaration instead.
            if (type.TypeKind == TypeSymbolKind.Interface || !type.IsDefinition)
                return result;

            void Consider(NamedTypeSymbol contract, MethodSymbol required, bool fromInterface)
            {
                var found = FindMember(type, required.Name, required.Parameters.Count);
                bool missing = found is null || (!type.IsAbstract && found.Dispatch == MethodDispatch.Abstract);
                if (!missing)
                    return;

                var substituted = MemberLookup.SubstituteMethod(required, contract.SubstitutionFromArguments(_factory));
                result.Add(new MissingMember(contract, substituted, fromInterface));
            }

            var visited = new HashSet<NamedTypeSymbol>();
            var contracts = new List<NamedTypeSymbol>();
            CollectInterfaces(type, visited, contracts);

            foreach (var contract in contracts)
            {
                foreach (var member in contract.Members)
                {
                    if (member is MethodSymbol { Dispatch: MethodDispatch.Abstract } required)
                        Consider(contract, required, fromInterface: true);
                }
            }

            for (var ancestor = type.BaseType; ancestor is not null; ancestor = SubstitutedBase(ancestor))
            {
                foreach (var member in ancestor.Members)
                {
                    if (member is MethodSymbol { Dispatch: MethodDispatch.Abstract } required)
                        Consider(ancestor, required, fromInterface: false);
                }
            }

            return result;
        }

        /// <summary>
        /// Every interface a type owes an implementation to: its own declared interfaces, its base
        /// chain's, and each interface's own <c>interface : interface</c> extensions.
        /// </summary>
        /// <remarks>
        /// Substitution-aware, the same way <c>MemberLookup.Reachable</c> and
        /// <c>Conversions.WalkForBase</c> are: a construction's declared bases and interfaces are
        /// written in terms of its own parameters (§6), so they have to be read as the construction
        /// makes them or the <c>int</c> in <c>IReadOnlyCollection&lt;int&gt;</c> is lost on the way
        /// to the members reached through it — a class implementing <c>ICollection&lt;int&gt;</c>
        /// would then be checked against <c>IReadOnlyCollection&lt;ICollection.T&gt;</c> rather than
        /// <c>IReadOnlyCollection&lt;int&gt;</c>. Finding an obligation needs none of this (name and
        /// arity a type argument cannot change), but the signature check in
        /// <see cref="CheckObligation"/> leans on it to read the contract's members correctly.
        /// </remarks>
        private void CollectInterfaces(NamedTypeSymbol from, HashSet<NamedTypeSymbol> visited, List<NamedTypeSymbol> interfaces)
        {
            if (!visited.Add(from))
                return;

            if (from.TypeKind == TypeSymbolKind.Interface)
                interfaces.Add(from);

            var substitution = from.SubstitutionFromArguments(_factory);

            foreach (var contract in from.Interfaces)
            {
                if (substitution.IsEmpty)
                    CollectInterfaces(contract, visited, interfaces);
                else if (substitution.Apply(contract) is NamedTypeSymbol substitutedContract)
                    CollectInterfaces(substitutedContract, visited, interfaces);
            }

            if (from.BaseType is NamedTypeSymbol baseType)
                CollectInterfaces(substitution.IsEmpty ? baseType : (NamedTypeSymbol)baseType.Substitute(substitution), visited, interfaces);
        }

        /// <summary>
        /// The base of <paramref name="current"/>, read as <paramref name="current"/>'s construction
        /// makes it — the next level of the base-abstract-member walk, substituted so a signature
        /// check against it sees the concrete type arguments, not the open declaration's parameters.
        /// </summary>
        private NamedTypeSymbol? SubstitutedBase(NamedTypeSymbol current)
        {
            if (current.BaseType is not NamedTypeSymbol baseType)
                return null;

            var substitution = current.SubstitutionFromArguments(_factory);
            return substitution.IsEmpty ? baseType : (NamedTypeSymbol)baseType.Substitute(substitution);
        }

        /// <summary>
        /// The nearest member of <paramref name="type"/> or of a class it extends that can answer
        /// for an interface obligation, matching by name and parameter count. Closest to
        /// <paramref name="type"/> wins, the same as the runtime's own vtable: an override replaces
        /// the slot in place, so a derived class's answer is this walk's first match.
        /// </summary>
        /// <remarks>
        /// <c>Virtual</c>/<c>Abstract</c> candidates occupy a real vtable slot and answer directly.
        /// A <c>Direct</c> candidate answers too — <see cref="CodeGen.ModuleEmitter.EmitBridges"/>
        /// gives it a synthetic forwarding bridge in the contract's slot instead, the same mechanism
        /// already used for a generic contract's erased slot (§8), so the member itself keeps
        /// <c>Direct</c> dispatch (and every optimisation that comes with it) everywhere but through
        /// the interface.
        /// </remarks>
        private static MethodSymbol? FindMember(NamedTypeSymbol type, string name, int arity)
        {
            for (var walk = type; walk is not null; walk = walk.BaseType)
            {
                foreach (var member in walk.Members)
                {
                    if (member is MethodSymbol candidate
                        && string.Equals(candidate.Name, name, StringComparison.Ordinal)
                        && candidate.Parameters.Count == arity)
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Rejects a constructor that chains to nothing when its base has no parameterless
        /// constructor to reach implicitly (§3.2).
        /// </summary>
        /// <remarks>
        /// <para>
        /// §3.2 gives an omitted chain one meaning — the base's parameterless constructor — so where
        /// the base has none, the omission names nothing. Nothing downstream can salvage it either:
        /// <c>ObjNew</c> allocates and runs nothing, so what the author would get is an instance whose
        /// base was never constructed, which is the shape of bug that shows up nowhere near its cause.
        /// </para>
        /// <para>
        /// The question is asked of the syntax rather than of the symbols, because what matters is
        /// whether a chain was <em>written</em> and a bound chain is not built until bodies are. A
        /// class declaring no constructor at all is the same case: the emitter synthesises a
        /// parameterless one, and it would have the same nothing to call.
        /// </para>
        /// <para>
        /// "Parameterless" is strict, matching both §3.2's wording and what
        /// <c>ModuleEmitter.EmitImplicitBaseChain</c> looks for — a base constructor whose arguments
        /// all have defaults is reachable by writing <c>super()</c>, and writing it is what the
        /// diagnostic asks for.
        /// </para>
        /// </remarks>
        private void CheckBaseConstructorIsReachable(TypeBinding binding)
        {
            if (binding.Symbol.BaseType is not NamedTypeSymbol baseType)
                return;

            bool declaresAny = false;
            bool takesNoArguments = false;

            foreach (var member in MemberLookup.MembersOf(baseType))
            {
                if (member is not MethodSymbol { Role: MethodRole.Constructor } constructor)
                    continue;

                declaresAny = true;
                takesNoArguments |= constructor.Parameters.Count == 0;
            }

            // A base that declares none needs nothing called: whatever initializers it has run from
            // the parameterless constructor the emitter synthesises for it.
            if (!declaresAny || takesNoArguments)
                return;

            bool declaredHere = false;

            foreach (var member in binding.Members)
            {
                if (member is not ConstructorDeclarationSyntax constructor)
                    continue;

                declaredHere = true;

                if (constructor.ChainArguments is not null)
                    continue;

                Report(
                    SurtrDiagnosticCode.BaseConstructorUnreachable,
                    binding,
                    constructor.Span,
                    $"'{baseType.Name}' has no parameterless constructor, so this constructor has to chain to one with 'super(...)'.");
            }

            if (declaredHere)
                return;

            Report(
                SurtrDiagnosticCode.BaseConstructorUnreachable,
                binding,
                binding.Syntax.Span,
                $"'{baseType.Name}' has no parameterless constructor, so '{binding.Symbol.Name}' has to declare one that chains to it with 'super(...)'.");
        }

        private static MethodSymbol? Overridden(NamedTypeSymbol baseType, MethodSymbol method)
        {
            foreach (var member in baseType.Members)
            {
                if (member is MethodSymbol candidate
                    && string.Equals(candidate.Name, method.Name, StringComparison.Ordinal)
                    && candidate.Parameters.Count == method.Parameters.Count)
                {
                    return candidate;
                }
            }

            return null;
        }

        private void BindHierarchy(TypeBinding binding)
        {
            var symbol = binding.Symbol;
            var syntax = binding.Syntax;

            symbol.IsAbstract = syntax.IsAbstract || syntax.Kind == TypeDeclarationKind.Interface;

            // An enum is a sealed class (§2.4), and a value class cannot be extended (§2.9).
            symbol.IsSealed = syntax.IsSealed
                || syntax.Kind == TypeDeclarationKind.Enum
                || syntax.Kind == TypeDeclarationKind.ValueClass
                || syntax.Kind == TypeDeclarationKind.Singleton;

            // §2.2: the two class-level modifiers are mutually exclusive - `abstract` promises a
            // subclass will provide a body, `sealed` forbids one existing at all. Checked against
            // what was actually written, not the computed IsSealed above, since an enum/value
            // class/singleton is sealed for a reason that has nothing to do with this rule.
            if (syntax.IsAbstract && syntax.IsSealed)
            {
                Report(SurtrDiagnosticCode.InvalidClassModifiers, binding, syntax.Span,
                    $"'{symbol.Name}' cannot be both 'abstract' and 'sealed' — 'abstract' requires a subclass to provide a body, and 'sealed' forbids one.");
            }

            var interfaces = new List<NamedTypeSymbol>();
            NamedTypeSymbol? baseClass = null;

            foreach (var baseSyntax in syntax.BaseTypes)
            {
                var resolved = _resolver.Resolve(baseSyntax, binding.Scope, binding.SourceName);

                if (resolved.IsError)
                    continue;

                if (resolved is not NamedTypeSymbol named)
                {
                    Report(SurtrDiagnosticCode.InvalidBaseType, binding, baseSyntax.Span,
                        $"'{resolved.ToDisplayString()}' is neither a class nor an interface.");
                    continue;
                }

                if (named.TypeKind == TypeSymbolKind.Interface)
                {
                    interfaces.Add(named);
                    continue;
                }

                if (syntax.Kind == TypeDeclarationKind.Interface)
                {
                    Report(SurtrDiagnosticCode.InvalidBaseType, binding, baseSyntax.Span,
                        $"An interface may only extend interfaces, and '{named.Name}' is a class.");
                    continue;
                }

                // §2.4: an enum can implement interfaces (each case is a genuine instance) but
                // never declares a base class - the enum class itself already occupies that slot.
                if (syntax.Kind == TypeDeclarationKind.Enum)
                {
                    Report(SurtrDiagnosticCode.InvalidEnumBase, binding, baseSyntax.Span,
                        $"An enum may only implement interfaces, and '{named.Name}' is a class - the enum class itself already occupies the base-class slot.");
                    continue;
                }

                if (baseClass is not null)
                {
                    Report(SurtrDiagnosticCode.InvalidBaseType, binding, baseSyntax.Span,
                        $"'{symbol.Name}' already extends '{baseClass.Name}'; a class extends at most one.");
                    continue;
                }

                if (named.IsSealed)
                {
                    Report(SurtrDiagnosticCode.InvalidBaseType, binding, baseSyntax.Span,
                        $"'{named.Name}' is sealed and cannot be extended.");
                    continue;
                }

                if (syntax.Kind == TypeDeclarationKind.ValueClass)
                {
                    Report(SurtrDiagnosticCode.InvalidValueClass, binding, baseSyntax.Span,
                        $"A value class wraps one field and has no layout to inherit, so '{symbol.Name}' cannot extend '{named.Name}'.");
                    continue;
                }

                baseClass = named;
            }

            // §11: `attribute class Foo` implies extending `Attribute`, the same way any other
            // class extending it already qualified (`ExtendsAttribute`). No explicit base written
            // resolves it the same way a `@Foo` use resolves the attribute's own name; an explicit
            // one just has to actually reach `Attribute`, which lets `attribute class Foo : Bar`
            // still work when `Bar` itself already extends it.
            if (syntax.IsAttribute)
            {
                if (baseClass is null)
                {
                    var attributeBase = _resolver.Resolve(
                        new NamedTypeSyntax(syntax.Span, new[] { "Attribute" }, System.Array.Empty<TypeSyntax>()),
                        binding.Scope, binding.SourceName);

                    if (attributeBase.NonNullable is NamedTypeSymbol resolvedAttribute)
                        baseClass = resolvedAttribute;
                }
                else if (!ExtendsAttribute(baseClass))
                {
                    Report(SurtrDiagnosticCode.InvalidAttribute, binding, binding.Syntax.Span,
                        $"'{symbol.Name}' is declared 'attribute', so its base has to be 'Attribute' or extend it, not '{baseClass.Name}'.");
                }

                symbol.IsAttribute = true;
                symbol.AllowedAttributeTargets = syntax.SurtrAttributeTargets;
                symbol.IsCompileTimeOnlyAttribute = syntax.IsCompileTimeOnlyAttribute;
            }

            symbol.Interfaces = interfaces;

            if (baseClass is not null && !CreatesCycle(symbol, baseClass, binding))
                symbol.BaseType = baseClass;
        }

        /// <summary>
        /// Every interface <paramref name="type"/> owes an answer to, transitively: the ones it
        /// names, its base chain's, and each interface's own extensions — substituted the way the
        /// construction reads them.
        /// </summary>
        /// <remarks>
        /// The emitter's bridge synthesis walks this list per obligation, so a member written
        /// without <c>override</c> gets its erased-signature bridge for <em>every</em> contract
        /// that requires it, however many interface levels up that requirement lives.
        /// </remarks>
        internal IReadOnlyList<NamedTypeSymbol> AllInterfacesOf(NamedTypeSymbol type)
        {
            var visited = new HashSet<NamedTypeSymbol>();
            var contracts = new List<NamedTypeSymbol>();
            CollectInterfaces(type, visited, contracts);
            return contracts;
        }

        /// <summary>
        /// Whether making <paramref name="candidate"/> the base of <paramref name="symbol"/> would
        /// close a loop, which is the same question <c>SurtrBuildState.Linking</c> answers at load
        /// — asked here so it can be pointed at a span.
        /// </summary>
        private bool CreatesCycle(NamedTypeSymbol symbol, NamedTypeSymbol candidate, TypeBinding binding)
        {
            for (NamedTypeSymbol? walk = candidate; walk is not null; walk = walk.BaseType)
            {
                if (!ReferenceEquals(walk.Definition, symbol.Definition))
                    continue;

                Report(SurtrDiagnosticCode.InheritanceCycle, binding, binding.Syntax.Span,
                    $"'{symbol.Name}' would extend itself through '{candidate.Name}'.");

                return true;
            }

            return false;
        }

        private void BindMembers(TypeBinding binding)
        {
            var symbol = binding.Symbol;
            var syntax = binding.Syntax;
            bool isInterface = syntax.Kind == TypeDeclarationKind.Interface;

            var members = new List<Symbol>();
            var signatures = new SignatureSet(_factory, _diagnostics);
            var names = new HashSet<string>(StringComparer.Ordinal);
            int letFields = 0;

            foreach (var member in binding.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field:
                    {
                        if (isInterface)
                        {
                            Report(SurtrDiagnosticCode.InvalidInterfaceMember, binding, field.Span,
                                $"An interface is a pure contract and cannot declare the field '{field.Name}'.");
                            continue;
                        }

                        // §10: a `native let`/`native var` inside a class is a native property, not
                        // a field with real storage - the host owns the value, exactly as a
                        // module-level one does (BindModuleNativeVariable). It is excluded from
                        // `letFields` on purpose: a value class's "exactly one let field" count
                        // (BindValueClassField below) means a field with real storage to erase to,
                        // and a native property has none.
                        if (field.IsNative)
                        {
                            if (!names.Add(field.Name))
                                Duplicate(binding, field.Span, field.Name);

                            var nativeProperty = BindClassNativeField(field, symbol, binding);
                            members.Add(nativeProperty);
                            if (nativeProperty.Getter is not null)
                            {
                                members.Add(nativeProperty.Getter);
                                signatures.Add(nativeProperty.Getter, binding.SourceName, field.Span);
                            }
                            if (nativeProperty.Setter is not null)
                            {
                                members.Add(nativeProperty.Setter);
                                signatures.Add(nativeProperty.Setter, binding.SourceName, field.Span);
                            }

                            continue;
                        }

                        if (!field.IsMutable)
                            letFields++;

                        var bound = BindField(field, symbol, binding);
                        if (!names.Add(field.Name))
                            Duplicate(binding, field.Span, field.Name);

                        members.Add(bound);
                        continue;
                    }

                    case PropertyDeclarationSyntax property:
                    {
                        if (isInterface && property.IsStatic)
                        {
                            Report(SurtrDiagnosticCode.InvalidInterfaceMember, binding, property.Span,
                                $"An interface has no statics, so '{property.Name}' cannot be one.");
                            continue;
                        }

                        // A native accessor has a real body - the host's - so it is exactly as much
                        // a default implementation as one written in Surtr, and an interface allows
                        // neither (§2.3).
                        if (isInterface && property.IsNative)
                        {
                            Report(SurtrDiagnosticCode.InvalidInterfaceMember, binding, property.Span,
                                $"An interface declares no default implementations, so '{property.Name}' cannot be native either.");
                            continue;
                        }

                        var bound = BindProperty(property, symbol, binding, isInterface);
                        if (!names.Add(property.Name))
                            Duplicate(binding, property.Span, property.Name);

                        members.Add(bound);
                        if (bound.Getter is not null)
                        {
                            members.Add(bound.Getter);
                            signatures.Add(bound.Getter, binding.SourceName, property.Span);
                        }
                        if (bound.Setter is not null)
                        {
                            members.Add(bound.Setter);
                            signatures.Add(bound.Setter, binding.SourceName, property.Span);
                        }

                        continue;
                    }

                    case MethodDeclarationSyntax method:
                    {
                        if (isInterface && method.IsStatic)
                        {
                            Report(SurtrDiagnosticCode.InvalidInterfaceMember, binding, method.Span,
                                $"An interface has no statics, so '{method.Name}' cannot be one.");
                            continue;
                        }

                        if (isInterface && method.Body is not null)
                        {
                            Report(SurtrDiagnosticCode.InvalidInterfaceMember, binding, method.Span,
                                $"An interface declares no default implementations, so '{method.Name}' cannot have a body.");
                            continue;
                        }

                        // A native method has no Surtr body, so the check above misses it - but its
                        // body is the host's, exactly as real a default implementation as one
                        // written in Surtr, and an interface allows neither (§2.3).
                        if (isInterface && method.IsNative)
                        {
                            Report(SurtrDiagnosticCode.InvalidInterfaceMember, binding, method.Span,
                                $"An interface declares no default implementations, so '{method.Name}' cannot be native either.");
                            continue;
                        }

                        var bound = BindMethod(method, symbol, binding, isInterface);
                        signatures.Add(bound, binding.SourceName, method.Span);
                        members.Add(bound);
                        continue;
                    }

                    case ConstructorDeclarationSyntax constructor:
                    {
                        if (isInterface)
                        {
                            Report(SurtrDiagnosticCode.InvalidInterfaceMember, binding, constructor.Span,
                                "An interface cannot declare a constructor.");
                            continue;
                        }

                        var bound = BindConstructor(constructor, symbol, binding);
                        signatures.Add(bound, binding.SourceName, constructor.Span);
                        members.Add(bound);
                        continue;
                    }

                    case StaticBlockDeclarationSyntax block:
                    {
                        if (isInterface)
                        {
                            Report(SurtrDiagnosticCode.InvalidInterfaceMember, binding, block.Span,
                                "An interface has no statics, so it cannot declare a 'static' block.");
                            continue;
                        }

                        _staticBlocks.Add(new StaticBlockBinding(
                            block, binding.Scope, binding.Module, symbol, binding.SourceName, _nextInitializerOrder++));

                        continue;
                    }

                    case OperatorDeclarationSyntax op:
                    {
                        if (isInterface && op.Body is not null)
                        {
                            Report(SurtrDiagnosticCode.InvalidInterfaceMember, binding, op.Span,
                                $"An interface declares no default implementations, so 'operator{(OperatorNames.TryGetSymbol(op.Operator, out string spelled) ? spelled : "?")}' cannot have a body.");
                            continue;
                        }

                        var bound = BindOperator(op, symbol, binding);
                        signatures.Add(bound, binding.SourceName, op.Span);
                        members.Add(bound);
                        continue;
                    }

                    case ExtensionDeclarationSyntax extension:
                    {
                        // Nesting inside a class only narrows visibility (§15.2) - the block's
                        // methods still belong to the declaring module, never to `symbol`.
                        BindExtension(extension, binding.Module, symbol, binding.Scope, binding.SourceName);
                        continue;
                    }
                }
            }

            foreach (var enumCase in syntax.EnumCases)
            {
                var field = new FieldSymbol(enumCase.Name, symbol, symbol)
                {
                    IsStatic = true,
                    IsReadOnly = true,
                    Accessibility = Accessibility.Public,
                };

                members.Add(field);

                // A case is a static holding one instance the enum's own initializer builds, so it
                // is an initializer like any other — with a construction on the right.
                _initializers.Add(new InitializerBinding(
                    field, null, enumCase, binding.Scope, binding.Module, symbol, binding.SourceName, _nextInitializerOrder++));

                if (!names.Add(enumCase.Name))
                    Duplicate(binding, enumCase.Span, enumCase.Name);
            }

            if (syntax.Kind == TypeDeclarationKind.ValueClass)
                BindValueClassField(binding, members, letFields);

            if (syntax.Kind == TypeDeclarationKind.Singleton)
                BindSingletonInstance(binding, symbol, members);

            symbol.Members = members;
        }

        private void BindValueClassField(TypeBinding binding, List<Symbol> members, int letFields)
        {
            // §2.9, generalized: every instance field is declared `let`, and there is at least one.
            // Exactly one field keeps the erasure the section introduced - the class IS that field
            // wherever its type is statically known. Several fields make the class a value type in
            // its own right: it occupies one flattened block of slots instead, and never erases.
            var instanceFields = new List<FieldSymbol>();

            foreach (var member in members)
            {
                if (member is FieldSymbol field && !field.IsStatic)
                    instanceFields.Add(field);
            }

            if (instanceFields.Count == 0 || instanceFields.Count != letFields)
            {
                Report(SurtrDiagnosticCode.InvalidValueClass, binding, binding.Syntax.Span,
                    $"A value class declares its fields 'let'; '{binding.Symbol.Name}' declares {instanceFields.Count} instance field(s), {letFields} of them 'let'.");

                return;
            }

            if (instanceFields.Count > 1 && binding.Symbol.TypeParameters.Count > 0)
            {
                Report(SurtrDiagnosticCode.ValueTypeLayout, binding, binding.Syntax.Span,
                    $"'{binding.Symbol.Name}' declares generic parameters; a multi-field value class cannot have one layout per substitution.");

                return;
            }

            if (instanceFields.Count == 1)
            {
                binding.Symbol.UnderlyingType = instanceFields[0].Type;
                return;
            }

            // A multi-field value class: no erasure. A direct self-reference is caught now; the
            // full flattened width - indirect cycles and the slot cap included - is checked when
            // the emitter first needs the layout, which is where ValueTypeLayout reports.
            foreach (var field in instanceFields)
            {
                if (ReferenceEquals(field.Type.NonNullable, binding.Symbol))
                {
                    Report(SurtrDiagnosticCode.ValueTypeLayout, binding, binding.Syntax.Span,
                        $"'{binding.Symbol.Name}' contains itself; no finite value layout can hold it.");

                    return;
                }
            }
        }

        /// <summary>
        /// Gives a <c>singleton</c> the one thing that makes it a value: a static holding its
        /// instance (§2.8).
        /// </summary>
        /// <remarks>
        /// Built like an enum case, and for the same reason — the declaration's own name has to
        /// answer as a value, and a type name cannot. So the instance is a synthetic static of the
        /// singleton's own class, and an initializer builds it with everything else the module
        /// loads. §2.8 forbids a constructor, since nothing would choose when to run it.
        /// </remarks>
        private void BindSingletonInstance(TypeBinding binding, NamedTypeSymbol symbol, List<Symbol> members)
        {
            symbol.IsSealed = true;

            foreach (var member in members)
            {
                if (member is MethodSymbol { Role: MethodRole.Constructor })
                {
                    Report(
                        SurtrDiagnosticCode.InvalidValueClass,
                        binding,
                        binding.Syntax.Span,
                        $"'{symbol.Name}' is a singleton, so nothing chooses when it is built and it cannot declare a constructor.");

                    break;
                }
            }

            var instance = new FieldSymbol(SyntheticNames.Instance(symbol.Name), symbol, symbol)
            {
                IsStatic = true,
                IsReadOnly = true,
                Accessibility = Accessibility.Public,
                IsSynthetic = true,
            };

            members.Add(instance);
            symbol.SingletonInstance = instance;

            _initializers.Add(new InitializerBinding(
                instance, null, null, binding.Scope, binding.Module, symbol, binding.SourceName, _nextInitializerOrder++, binding.Syntax));
        }

        private void BindModuleMembers(ModuleSymbol module)
        {
            var scope = _moduleScopes[module.Path];
            var sourceModule = _compilation.Modules[module.Path];

            var fields = new List<FieldSymbol>();
            var properties = new List<PropertySymbol>();
            var methods = new List<MethodSymbol>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var signatures = new SignatureSet(_factory, _diagnostics);

            foreach (var unit in sourceModule.Units)
            {
                foreach (var declaration in Flatten(unit.Syntax.Declarations, unit.File.Path))
                {
                    switch (declaration)
                    {
                        case FieldDeclarationSyntax field:
                            CheckBuildConstant(field.Name, unit.File.Path, field.Span);
                            if (!names.Add(field.Name))
                                ReportAt(unit.File.Path, field.Span, SurtrDiagnosticCode.DuplicateDeclaration,
                                    $"'{field.Name}' is already declared in module '{module.Path}'.");

                            // §10: a module-level `native let/var` is a native property, not an
                            // import entry — the host owns the storage and Surtr reaches it through
                            // `get_x`/`set_x` accessors published by link name, exactly as a
                            // class-level native member does.
                            if (field.IsNative)
                            {
                                var nativeProperty = BindModuleNativeVariable(field, module, scope, unit.File.Path);
                                if (nativeProperty.Getter is not null)
                                    signatures.Add(nativeProperty.Getter, unit.File.Path, field.Span);
                                if (nativeProperty.Setter is not null)
                                    signatures.Add(nativeProperty.Setter, unit.File.Path, field.Span);

                                properties.Add(nativeProperty);
                            }
                            else
                            {
                                fields.Add(BindModuleField(field, module, scope, unit.File.Path));
                            }

                            continue;

                        case PropertyDeclarationSyntax property:
                            CheckBuildConstant(property.Name, unit.File.Path, property.Span);
                            if (!names.Add(property.Name))
                                ReportAt(unit.File.Path, property.Span, SurtrDiagnosticCode.DuplicateDeclaration,
                                    $"'{property.Name}' is already declared in module '{module.Path}'.");

                            var boundProperty = BindModuleProperty(property, module, scope, unit.File.Path);
                            if (boundProperty.Getter is not null)
                                signatures.Add(boundProperty.Getter, unit.File.Path, property.Span);
                            if (boundProperty.Setter is not null)
                                signatures.Add(boundProperty.Setter, unit.File.Path, property.Span);

                            properties.Add(boundProperty);
                            continue;

                        case MethodDeclarationSyntax method:
                            CheckBuildConstant(method.Name, unit.File.Path, method.Span);
                            var bound = BindModuleMethod(method, module, scope, unit.File.Path);
                            signatures.Add(bound, unit.File.Path, method.Span);
                            methods.Add(bound);
                            continue;

                        case StaticBlockDeclarationSyntax block:
                            // §2.5: a module body holds declarations only, and initialization logic
                            // that does not fit a field initializer goes here — running at load in
                            // the source position it appears among the other initializers.
                            _staticBlocks.Add(new StaticBlockBinding(
                                block, scope, module, null, unit.File.Path, _nextInitializerOrder++));

                            continue;

                        case ExtensionDeclarationSyntax extension:
                            BindExtension(extension, module, containingType: null, scope, unit.File.Path);
                            continue;
                    }
                }
            }

            module.Fields = fields;
            module.Properties = properties;
            module.Methods = methods;
        }

        /// <summary>
        /// Binds one <c>extension &lt;Type&gt; { ... }</c> block (§15) — at module level
        /// (<paramref name="containingType"/> <see langword="null"/>) or nested inside a class.
        /// Every method it declares is appended to <see cref="_extensionMethodsByModule"/>, keyed by
        /// <paramref name="module"/>'s path; <see cref="MemberPhase"/> hands the accumulated list to
        /// <c>ModuleSymbol.ExtensionMethods</c> once, after every block for every module has run.
        /// </summary>
        private void BindExtension(
            ExtensionDeclarationSyntax syntax,
            ModuleSymbol module,
            NamedTypeSymbol? containingType,
            Scope scope,
            string sourceName)
        {
            // §3.1's two defaults, the same rule DeclareType applies to a type: a block written
            // directly in a module is internal to it, and one nested inside a class is private to
            // that class like any other member — except inside an interface, where §2.3 makes every
            // member public.
            Accessibility blockAccessibility = Translate(
                syntax.Visibility,
                containingType is null
                    ? Accessibility.Internal
                    : containingType.TypeKind == TypeSymbolKind.Interface
                        ? Accessibility.Public
                        : Accessibility.Private);

            if (!_extensionMethodsByModule.TryGetValue(module.Path, out var extensions))
            {
                extensions = new List<MethodSymbol>();
                _extensionMethodsByModule.Add(module.Path, extensions);
            }

            if (!_extensionPropertiesByModule.TryGetValue(module.Path, out var extensionProperties))
            {
                extensionProperties = new List<PropertySymbol>();
                _extensionPropertiesByModule.Add(module.Path, extensionProperties);
            }

            var signatures = new SignatureSet(_factory, _diagnostics);

            foreach (var member in syntax.Members)
            {
                if (member is PropertyDeclarationSyntax property)
                {
                    // Fase 6 (§15.4) does not extend to properties: a property has no argument list
                    // for overload resolution to infer a type parameter from the way a call's
                    // arguments do (`SubstituteGenericCandidates`), and a read (`obj.prop`) has no
                    // machinery at all to run that inference against — so a generic block's `T`
                    // never reaches one. Rejected here rather than silently resolved against an
                    // unfilled `T`, which would misreport as an ordinary unresolved-name error far
                    // from its actual cause.
                    if (syntax.TypeParameters.Count > 0)
                    {
                        ReportAt(sourceName, property.Span, SurtrDiagnosticCode.InvalidExtensionMember,
                            $"'{property.Name}' cannot be declared inside a generic extension block (§15.4) — only methods can take the block's type parameters yet.");
                        continue;
                    }

                    var propertyTarget = _resolver.Resolve(syntax.TargetType, scope, sourceName);
                    if (propertyTarget.IsError)
                        continue;

                    if (!IsValidExtensionTarget(propertyTarget))
                    {
                        ReportAt(sourceName, syntax.TargetType.Span, SurtrDiagnosticCode.InvalidExtensionTarget,
                            $"'{propertyTarget.ToDisplayString()}' cannot be extended (§15) — a type parameter and 'void' name no concrete receiver.");
                        continue;
                    }

                    if (BindExtensionProperty(property, module, propertyTarget, containingType, blockAccessibility, scope, sourceName) is PropertySymbol bound)
                        extensionProperties.Add(bound);

                    continue;
                }

                if (member is not MethodDeclarationSyntax method)
                {
                    ReportAt(sourceName, member.Span, SurtrDiagnosticCode.InvalidExtensionMember,
                        "An extension block may only declare methods and computed properties (§15) — fields, constructors and static blocks are not supported.");
                    continue;
                }

                if (method.TypeParameters.Count > 0 || method.IsNative || method.IsConst
                    || method.Dispatch != DispatchModifier.None || method.IsSealed || method.Body is null)
                {
                    ReportAt(sourceName, method.Span, SurtrDiagnosticCode.InvalidExtensionMember,
                        $"'{method.Name}' cannot declare its own type parameters (write them on the 'extension' block itself, §15.4), or be native, const, abstract/virtual/override, sealed, or bodyless — an extension block does not support that yet (§15).");
                    continue;
                }

                var methodScope = scope.CreateChild();
                var extMethod = new MethodSymbol(method.Name, module, _factory.ErrorType)
                {
                    IsStatic = true,
                    Accessibility = ResolveExtensionMemberAccessibility(method.Visibility, blockAccessibility, sourceName, method.Span),
                    IsInline = method.Inline == InlineModifier.Inline,
                    IsForceInline = method.Inline == InlineModifier.ForceInline,
                    ExtensionDeclaringContainer = containingType,
                    ExtensionIsStatic = method.IsStatic,
                };

                // The block's own type parameters (§15.4), written explicitly only when a bound is
                // needed — the same per-method declaration and constraint-binding an ordinary
                // generic method's own `<T>` already gets, since an extension method is exactly that:
                // a module-level function, generic over parameters nobody but itself owns. Reused
                // rather than re-implemented, and deliberately a *fresh* symbol set per member (not
                // one shared across the block) so two members are never accidentally unified against
                // each other through a name they only look like they share.
                if (syntax.TypeParameters.Count > 0)
                    BindTypeParameters(extMethod, syntax.TypeParameters, methodScope, sourceName);

                var implicitParameters = new List<TypeParameterSymbol>();
                var target = BindExtensionTargetType(syntax.TargetType, extMethod, methodScope, sourceName, implicitParameters);

                // `null` only comes back once `TypeResolver` itself already reported why (a wrong
                // type argument count, a genuinely undeclared qualified name) — reporting a second,
                // vaguer diagnostic on top would turn one mistake into two.
                if (target is null)
                    continue;

                if (!IsValidExtensionTarget(target))
                {
                    ReportAt(sourceName, syntax.TargetType.Span, SurtrDiagnosticCode.InvalidExtensionTarget,
                        $"'{target.ToDisplayString()}' cannot be extended (§15) — a bare type parameter and 'void' name no concrete receiver.");
                    continue;
                }

                if (syntax.TypeParameters.Count == 0 && implicitParameters.Count > 0)
                    extMethod.TypeParameters = implicitParameters;

                extMethod.ExtensionTargetType = target;

                if (method.ReturnType is not null)
                    extMethod.ReturnType = _resolver.Resolve(method.ReturnType, methodScope, sourceName);
                else
                    _inferReturnTypes.Add(extMethod);
                extMethod.Parameters = BindParameters(method.Parameters, extMethod, methodScope, sourceName);

                // An instance extension's receiver — `obj.method()` is bound against it — is an
                // ordinary, explicitly-named first parameter, not the implicit `this` a real instance
                // method reads off slot zero (BodyBinder.BindThis): extension methods are
                // module-level functions (`ContainingSymbol` is `module`, not `target`), with no
                // implicit receiver slot to give it. `this` still works inside the body — it resolves
                // to this same parameter (`BodyBinder.ExtensionReceiver`) rather than through
                // `_containingType` — but the parameter itself has to be written out, since there is
                // nothing else here to bind `this` to. A `static fun` (§15.3) takes no receiver at
                // all — it is reached as `Type.method()`, resolved by matching the type named at the
                // call site, never an argument.
                if (!method.IsStatic
                    && (extMethod.Parameters.Count == 0 || !ReferenceEquals(extMethod.Parameters[0].Type, target)))
                {
                    ReportAt(sourceName, method.Span, SurtrDiagnosticCode.InvalidExtensionReceiver,
                        $"'{method.Name}' must take '{target.ToDisplayString()}' as its first parameter — the receiver 'obj.{method.Name}(...)' is bound against.");
                }

                RecordBody(extMethod, method.Body, methodScope, module, containingType: null, sourceName);
                RecordAttributes(extMethod, method.Attributes, methodScope, sourceName);

                signatures.Add(extMethod, sourceName, method.Span);
                extensions.Add(extMethod);
            }
        }

        /// <summary>
        /// Whether a resolved type is one <c>extension</c> may target (§15): anything concrete. A
        /// bare type parameter and <c>void</c> are the two things a receiver can never actually be.
        /// </summary>
        private static bool IsValidExtensionTarget(TypeSymbol type)
            => type is not TypeParameterSymbol && !type.IsVoid;

        /// <summary>
        /// Resolves an <c>extension</c> block's target type (§15.4), declaring a fresh type
        /// parameter — owned by <paramref name="owner"/> — for any bare name mentioned inside it that
        /// does not already resolve to something, Kotlin/Swift-style: <c>extension T[] { }</c> and
        /// <c>extension Box&lt;T&gt; { }</c> need no separate <c>&lt;T&gt;</c> list at all, only a
        /// bound does (§15.4's explicit <c>extension&lt;T : Bound&gt;</c> form, already declared into
        /// <paramref name="scope"/> by the time this runs — which is exactly why a name already found
        /// there is read, never redeclared).
        /// </summary>
        /// <remarks>
        /// Declaration and resolution are interleaved on purpose, not run as two passes: a later
        /// argument (<c>{T: T}</c>'s value position) has to see the same parameter a earlier one just
        /// declared, and <c>Box&lt;T&gt;</c>'s <c>T</c> must <em>not</em> resolve against whatever
        /// <c>Box</c>'s own declaration happens to have called its parameter — the static-nested rule
        /// (§6) that is the entire reason this method exists instead of a plain
        /// <see cref="TypeResolver.Resolve"/> call. <see cref="TypeResolver.TryResolveTypeName"/> is
        /// what makes "does this already mean something" answerable without reporting a diagnostic
        /// for the common case where it does not — a parameter's whole point.
        /// </remarks>
        private TypeSymbol? BindExtensionTargetType(
            TypeSyntax syntax,
            MethodSymbol owner,
            Scope scope,
            string sourceName,
            List<TypeParameterSymbol> declared)
        {
            switch (syntax)
            {
                case NamedTypeSyntax { Path.Count: 1, TypeArguments.Count: 0 } named:
                {
                    if (_resolver.TryResolveTypeName(named.Path, scope, named.Span, out var existing))
                        return existing;

                    if (scope.Lookup(named.Path[0]) is { IsFound: true, Symbol: TypeParameterSymbol already })
                        return already;

                    var parameter = _factory.DeclareTypeParameter(named.Path[0], owner, declared.Count);
                    declared.Add(parameter);
                    scope.TryDeclare(named.Path[0], parameter);
                    return parameter;
                }

                case NamedTypeSyntax named:
                {
                    // The arguments are declared/resolved first so a name in one of them (`T` in
                    // `Box<T>`) is already in `scope` by the time the whole reference resolves — the
                    // ordinary resolver then finds it exactly like any other type in scope, so there
                    // is no need to hand-build the construction here too.
                    foreach (var argument in named.TypeArguments)
                    {
                        if (BindExtensionTargetType(argument, owner, scope, sourceName, declared) is null)
                            return null;
                    }

                    var resolved = _resolver.Resolve(syntax, scope, sourceName);
                    return resolved.IsError ? null : resolved;
                }

                case ArrayTypeSyntax array:
                {
                    var element = BindExtensionTargetType(array.ElementType, owner, scope, sourceName, declared);
                    return element is null ? null : _factory.Array(element);
                }

                case DictTypeSyntax dictionary:
                {
                    var key = BindExtensionTargetType(dictionary.KeyType, owner, scope, sourceName, declared);
                    var value = BindExtensionTargetType(dictionary.ValueType, owner, scope, sourceName, declared);
                    return key is null || value is null ? null : _factory.Dictionary(key, value);
                }

                default:
                {
                    // A tuple, closure or nullable target — no case in §15.4's examples needs an
                    // implicitly-declared parameter this deep, so a bare name here resolves as an
                    // ordinary (and, for one not already in scope, unresolved) type reference.
                    var resolved = _resolver.Resolve(syntax, scope, sourceName);
                    return resolved.IsError ? null : resolved;
                }
            }
        }

        /// <summary>
        /// Binds one property inside an <c>extension</c> block (§15.1) — always computed, since
        /// there is no backing field an already-built type could offer it. The receiver an instance
        /// accessor's body reaches through <c>this</c> (<c>BodyBinder.ExtensionReceiver</c>) is a
        /// synthesized first parameter, exactly like an extension method's except the user never
        /// writes it out — there is nowhere in a property's own declaration to name it.
        /// </summary>
        private PropertySymbol? BindExtensionProperty(
            PropertyDeclarationSyntax syntax,
            ModuleSymbol module,
            TypeSymbol target,
            NamedTypeSymbol? containingType,
            Accessibility blockAccessibility,
            Scope scope,
            string sourceName)
        {
            if (syntax.IsNative)
            {
                ReportAt(sourceName, syntax.Span, SurtrDiagnosticCode.InvalidExtensionMember,
                    $"'{syntax.Name}' cannot be native — an extension block does not support that (§15).");
                return null;
            }

            var propertyScope = scope.CreateChild();
            var type = _resolver.Resolve(syntax.Type, propertyScope, sourceName);
            var accessibility = ResolveExtensionMemberAccessibility(syntax.Visibility, blockAccessibility, sourceName, syntax.Span);

            var property = new PropertySymbol(syntax.Name, module, type)
            {
                // Unlike every extension method's `MethodSymbol.IsStatic` (always true, §15's whole
                // module-level-function trick), this one stays the source truth: `ResolveProperty`/
                // `EmitPropertyRead`/`Store` read `PropertySymbol.IsStatic` alone to decide whether to
                // push a receiver before the accessor call, and an instance extension property needs
                // exactly that push — its accessors take the receiver as an ordinary parameter, which
                // is what ends up filled from it.
                IsStatic = syntax.IsStatic,
                Accessibility = accessibility,
                ExtensionTargetType = target,
                ExtensionDeclaringContainer = containingType,
            };

            AccessorSyntax? getter = null;
            AccessorSyntax? setter = null;
            foreach (var accessor in syntax.Accessors)
            {
                if (accessor.IsGetter)
                    getter = accessor;
                else
                    setter = accessor;
            }

            if (getter is null && setter is null)
            {
                ReportAt(sourceName, syntax.Span, SurtrDiagnosticCode.InvalidExtensionMember,
                    $"'{syntax.Name}' must declare at least one accessor with a body — an extension property is always computed (§15.1).");
                return null;
            }

            if (getter is AccessorSyntax boundGetter)
                property.Getter = BindExtensionAccessor(boundGetter, MemberNames.Getter(syntax.Name), module, target, containingType, syntax.IsStatic, type, accessibility, propertyScope, sourceName);

            if (setter is AccessorSyntax boundSetter)
                property.Setter = BindExtensionAccessor(boundSetter, MemberNames.Setter(syntax.Name), module, target, containingType, syntax.IsStatic, _factory.Void, accessibility, propertyScope, sourceName, valueParameterType: type);

            RecordAttributes(property, syntax.Attributes, propertyScope, sourceName);
            return property;
        }

        /// <summary>
        /// Binds one accessor of an extension property — the shared shape between <c>get</c> and
        /// <c>set</c>, which differ only in their return type and whether a trailing <c>value</c>
        /// parameter follows the receiver.
        /// </summary>
        private MethodSymbol BindExtensionAccessor(
            AccessorSyntax syntax,
            string name,
            ModuleSymbol module,
            TypeSymbol target,
            NamedTypeSymbol? containingType,
            bool isStatic,
            TypeSymbol returnType,
            Accessibility propertyAccessibility,
            Scope scope,
            string sourceName,
            TypeSymbol? valueParameterType = null)
        {
            var accessorScope = scope.CreateChild();
            var accessor = new MethodSymbol(name, module, returnType)
            {
                IsStatic = true,
                Accessibility = ResolveAccessorAccessibility(syntax, propertyAccessibility, sourceName),
                Role = valueParameterType is null ? MethodRole.PropertyGetter : MethodRole.PropertySetter,
                ExtensionTargetType = target,
                ExtensionDeclaringContainer = containingType,
                ExtensionIsStatic = isStatic,
            };

            if (syntax.HasOwnDispatch || syntax.IsSealed)
            {
                ReportAt(sourceName, syntax.Span, SurtrDiagnosticCode.InvalidExtensionMember,
                    $"'{name}' cannot be virtual/override/abstract or sealed — an extension block does not support that (§15).");
            }

            var parameters = new List<ParameterSymbol>();
            if (!isStatic)
                parameters.Add(new ParameterSymbol(SyntheticNames.ExtensionReceiver, target, parameters.Count, accessor));

            if (valueParameterType is not null)
                parameters.Add(new ParameterSymbol("value", valueParameterType, parameters.Count, accessor));

            accessor.Parameters = parameters;

            if (syntax.Body is null)
            {
                ReportAt(sourceName, syntax.Span, SurtrDiagnosticCode.InvalidExtensionMember,
                    $"'{name}' must have a body — an extension property is always computed, never an auto-property (§15.1).");
            }
            else
            {
                RecordBody(accessor, syntax.Body, accessorScope, module, containingType: null, sourceName);
            }

            return accessor;
        }

        /// <summary>
        /// A member's effective accessibility inside an <c>extension</c> block (§15.2): its own, when
        /// written, narrowed to or left equal to the block's — never wider. Left unwritten, it
        /// inherits the block's outright, the same default an ordinary member takes from its type.
        /// </summary>
        private Accessibility ResolveExtensionMemberAccessibility(Visibility written, Accessibility blockAccessibility, string sourceName, SourceSpan span)
        {
            if (written == Visibility.Default)
                return blockAccessibility;

            Accessibility resolved = Translate(written, blockAccessibility);
            if (resolved > blockAccessibility)
            {
                ReportAt(sourceName, span, SurtrDiagnosticCode.ExtensionMemberVisibilityTooWide,
                    $"A member's own visibility cannot be wider than its extension block's — '{Describe(resolved)}' is wider than '{Describe(blockAccessibility)}'.");
            }

            return resolved;
        }

        /// <summary>Records a <c>const</c>, both for folding and for the §7.1 check on its initializer.</summary>
        private void AddConstant(FieldDeclarationSyntax syntax, string sourceName)
        {
            Constants.AddConstant(syntax.Name, syntax.Initializer!);
            _constantDeclarations.Add(new ConstantDeclaration(syntax.Name, syntax.Initializer!, sourceName));
        }

        private void CheckBuildConstant(string name, string sourceName, SourceSpan span)
        {
            // §7.4: shadowing a build flag would be invisible at the use site.
            if (_compilation.Project.BuildConstants.ContainsKey(name))
            {
                ReportAt(sourceName, span, SurtrDiagnosticCode.BuildConstantShadowed,
                    $"The build defines '{name}', so a module member cannot take that name.");
            }
        }
        #endregion

        #region Phase 2.5 - inferred return types
        /// <summary>
        /// Fills in the return types a declaration omitted (§8) by binding each body's and reading
        /// the type back off its <c>return</c> statements.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Run between the member phase and the obligation checks: an <c>override</c> or an
        /// interface implementation that <em>does</em> declare its return type is compared against
        /// the contract by signature, which needs this pass's answers to be real before that runs.
        /// </para>
        /// <para>
        /// A fixpoint, not a single pass, because one inferred function may call another whose own
        /// return type is inferred too: <c>fun f() =&gt; g()</c> cannot know it returns <c>int</c>
        /// until <c>g</c> has, so the loop re-tries whoever still could not decide until no one
        /// can. The body is bound speculatively — a throwaway <see cref="BodyBinder"/> whose
        /// diagnostics are discarded, exactly like the binder's own <c>Speculative</c> — so the
        /// real body pass later reports everything once. A body that itself has errors is left
        /// alone (the real pass reports them); a body that binds cleanly but whose returns do not
        /// agree, or that reaches a recursive call whose own type is still unresolved, is reported
        /// here for writing the type instead.
        /// </para>
        /// </remarks>
        private void InferReturnTypes()
        {
            var errored = new HashSet<MethodSymbol>();
            var contracted = new HashSet<MethodSymbol>();

            // An interface implementation is a contract, so its return type cannot be inferred from
            // the body the way an ordinary method's can — the signature has to be the contract's,
            // which means it has to be written. A class member that answers an obligation with no
            // written return type is reported here, before the inference runs, and kept out of it.
            foreach (var binding in _declared)
            {
                var symbol = binding.Symbol;
                if (symbol.TypeKind == TypeSymbolKind.Interface || !symbol.IsDefinition)
                    continue;

                var visited = new HashSet<NamedTypeSymbol>();
                var contracts = new List<NamedTypeSymbol>();
                CollectInterfaces(symbol, visited, contracts);

                foreach (var contract in contracts)
                {
                    foreach (var member in contract.Members)
                    {
                        if (member is not MethodSymbol { Dispatch: MethodDispatch.Abstract } required)
                            continue;

                        EnforceDeclaredContractReturn(binding, symbol, contract, required, contracted);
                    }
                }

                for (var ancestor = symbol.BaseType; ancestor is not null; ancestor = SubstitutedBase(ancestor))
                {
                    foreach (var member in ancestor.Members)
                    {
                        if (member is MethodSymbol { Dispatch: MethodDispatch.Abstract } required)
                            EnforceDeclaredContractReturn(binding, symbol, ancestor, required, contracted);
                    }
                }
            }

            bool progressed = true;

            while (progressed)
            {
                progressed = false;

                foreach (var body in _bodies)
                {
                    var method = body.Method;
                    if (!_inferReturnTypes.Contains(method) || errored.Contains(method) || contracted.Contains(method)
                        || !method.ReturnType.IsError)
                        continue;

                    switch (TryInferReturnType(body, out TypeSymbol inferred))
                    {
                        case ReturnInference.Inferred:
                            method.ReturnType = inferred;
                            progressed = true;
                            break;

                        case ReturnInference.BodyError:
                            errored.Add(method);
                            break;

                        case ReturnInference.CannotInfer:
                            break;
                    }
                }
            }

            foreach (var body in _bodies)
            {
                var method = body.Method;
                if (!_inferReturnTypes.Contains(method) || errored.Contains(method) || contracted.Contains(method)
                    || !method.ReturnType.IsError)
                    continue;

                ReportAt(
                    body.SourceName,
                    body.Syntax.Span,
                    SurtrDiagnosticCode.CannotInferType,
                    $"Cannot infer the return type of '{method.Name}'; write it after the ':'.");
            }
        }

        /// <summary>
        /// Reports one obligation a class answers with a member that omits its return type, and
        /// keeps that member out of inference.
        /// </summary>
        private void EnforceDeclaredContractReturn(
            TypeBinding binding,
            NamedTypeSymbol symbol,
            NamedTypeSymbol contract,
            MethodSymbol required,
            HashSet<MethodSymbol> contracted)
        {
            var found = FindMember(symbol, required.Name, required.Parameters.Count);
            if (found is null || !found.ReturnType.IsError)
                return;

            Report(
                SurtrDiagnosticCode.ReturnTypeRequired,
                binding,
                binding.Syntax.Span,
                $"'{symbol.Name}.{found.Name}' implements '{contract.Name}.{required.Name}', so its return type cannot be inferred; write it after the ':'.");

            contracted.Add(found);
        }

        private enum ReturnInference
        {
            Inferred,
            BodyError,
            CannotInfer,
        }

        /// <summary>Binds one body speculatively and reads what it returns.</summary>
        private ReturnInference TryInferReturnType(BodyBinding body, out TypeSymbol inferred)
        {
            inferred = _factory.Void;

            EnterContext(body.Module, body.ContainingType);

            var binder = new BodyBinder(
                _factory,
                _resolver,
                Conversions,
                MemberLookup,
                OverloadResolution,
                Constants,
                _diagnostics,
                body.SourceName,
                body.Scope,
                body.Module,
                body.ContainingType,
                body.Method,
                ImportedBy(body.Module));

            int before = _diagnostics.Count;
            var bound = binder.BindBody(body.Syntax);
            if (_diagnostics.Count > before)
            {
                _diagnostics.TruncateTo(before);
                return ReturnInference.BodyError;
            }

            var types = new List<TypeSymbol>();
            CollectReturnTypes(bound, types);

            if (types.Count == 0)
            {
                inferred = _factory.Void;
                return ReturnInference.Inferred;
            }

            TypeSymbol? agreed = null;
            foreach (var type in types)
            {
                if (agreed is null)
                {
                    agreed = type;
                    continue;
                }

                if (!ReferenceEquals(agreed, type))
                    return ReturnInference.CannotInfer;
            }

            if (agreed is null || agreed.IsError)
                return ReturnInference.CannotInfer;

            inferred = agreed;
            return ReturnInference.Inferred;
        }

        /// <summary>
        /// Collects every <c>return</c> statement a body can take, for inference. Statements only:
        /// an expression holds no <c>return</c> except inside a lambda, and a lambda's is its own —
        /// not this function's — so the walk stops at statements and never enters expressions.
        /// </summary>
        private void CollectReturnTypes(BoundNode? node, List<TypeSymbol> types)
        {
            if (node is null)
                return;

            switch (node)
            {
                case BoundReturnStatement @return:
                    types.Add(@return.Value?.Type ?? _factory.Void);
                    return;

                case BoundBlockStatement block:
                    foreach (var statement in block.Statements)
                        CollectReturnTypes(statement, types);
                    return;

                case BoundIfStatement @if:
                    CollectReturnTypes(@if.Then, types);
                    CollectReturnTypes(@if.Else, types);
                    return;

                case BoundWhileStatement @while:
                    CollectReturnTypes(@while.Body, types);
                    return;

                case BoundForStatement @for:
                    CollectReturnTypes(@for.Body, types);
                    return;

                case BoundForInStatement forIn:
                    CollectReturnTypes(forIn.Body, types);
                    return;

                case BoundSwitchStatement @switch:
                    foreach (var section in @switch.Sections)
                    {
                        foreach (var statement in section.Statements)
                            CollectReturnTypes(statement, types);
                    }
                    return;

                case BoundTryStatement @try:
                    CollectReturnTypes(@try.Body, types);
                    foreach (var clause in @try.Catches)
                        CollectReturnTypes(clause.Body, types);
                    CollectReturnTypes(@try.Finally, types);
                    return;

                case BoundLabeledStatement labeled:
                    CollectReturnTypes(labeled.Statement, types);
                    return;
            }
        }
        #endregion

        #region Phase 3 - bodies
        /// <summary>
        /// Binds every body the compilation declared, and returns them by method.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Bind"/> because it is a separate phase and answers a separate
        /// question: phases 1 and 2 settle what every type <em>is</em>, which is all a tool needs
        /// for navigation or metadata. One body's binding cannot affect another's, so they run in
        /// any order.
        /// </remarks>
        public IReadOnlyDictionary<MethodSymbol, BoundStatement> BindBodies()
        {
            // Before anything is bound, because a call site that omits an argument emits the
            // default in its place — so the value has to exist by the time any body is walked.
            FoldDefaults();

            // Const functions first, and not for tidiness: §7.2 folds a call by running the callee's
            // emitted body, so a `const if` inside an ordinary body can only be answered once every
            // const fun has one. Binding them in two rounds is the whole of that ordering.
            foreach (var body in _bodies)
            {
                if (body.Method.IsConst)
                    BindOne(body);
            }

            PrepareConstFolding();

            // Again, for the ones that name a `const fun`: §7.2 makes one a constant expression, and
            // the folder that answers it did not exist a moment ago.
            FoldDefaults();
            ReportUnfoldedDefaults();

            foreach (var body in _bodies)
                BindOne(body);

            // After the bodies, because an initializer may call a const fun and folding one needs
            // the folder — which the round above is what builds.
            foreach (var initializer in _initializers)
                BindInitializer(initializer);

            foreach (var block in _staticBlocks)
                BindStaticBlock(block);

            foreach (var chain in _chains)
                BindChain(chain);

            // After the defaults have folded, so an attribute argument may be a `const` (§11 takes
            // constants, §7.1 is where they come from) - and after every type exists, since the
            // attribute class is resolved by name like any other.
            foreach (var attribute in _attributes)
                BindAttributes(attribute);

            // After every fragment is bound and every body with it: a fragment reaching a static
            // through a call is the same mistake as one reading it outright, and answering that
            // needs the callee's tree.
            InitializerOrder.Check(_modules.Values, _loadFragments, _bound, _diagnostics);

            RejectChainCycles();
            VerifyConstantDeclarations();

            // Again, for the constructed types a body wrote: `let s: Sorted<Plain> = ...` records a
            // construction site while its body binds, which is after the run at the end of the member
            // phase. Sites are cleared as they are verified, so this checks the new ones only.
            _resolver.VerifyConstraints(Conversions);

            return _bound;
        }

        /// <summary>
        /// Folds every parameter default that has not folded yet (§3.5).
        /// </summary>
        /// <remarks>
        /// Over syntax through <see cref="ConstantEvaluator"/>, which is what §3.5's "must be a
        /// compile-time constant" means, and run twice: once before any body is bound, so an
        /// ordinary call site can emit one, and again once a <c>const fun</c> can be run.
        /// </remarks>
        private void FoldDefaults()
        {
            foreach (var binding in _defaults)
            {
                if (binding.Parameter.DefaultValueFolded)
                    continue;

                if (Constants.TryEvaluate(binding.Syntax, out object? value))
                {
                    binding.Parameter.DefaultValue = value;
                    binding.Parameter.DefaultValueFolded = true;
                }
            }
        }

        private void ReportUnfoldedDefaults()
        {
            foreach (var binding in _defaults)
            {
                if (binding.Parameter.DefaultValueFolded)
                    continue;

                ReportAt(
                    binding.SourceName,
                    binding.Syntax.Span,
                    SurtrDiagnosticCode.NotAConstant,
                    $"The default for '{binding.Parameter.Name}' has to fold at compile time; §3.5 allows only a constant.");
            }
        }

        /// <summary>Binds one field initializer or enum case, in a scope that is its declaration's.</summary>
        private void BindInitializer(InitializerBinding initializer)
        {
            EnterContext(initializer.Module, initializer.ContainingType);
            var owner = new MethodSymbol(
                initializer.Field.Name,
                (Symbol?)initializer.ContainingType ?? initializer.Module,
                initializer.Field.Type)
            {
                // A static initializer has no receiver, so a `this` in one has to be reported
                // rather than silently bound against nothing.
                IsStatic = initializer.Field.IsStatic,
            };

            var binder = new BodyBinder(
                _factory,
                _resolver,
                Conversions,
                MemberLookup,
                OverloadResolution,
                Constants,
                _diagnostics,
                initializer.SourceName,
                initializer.Scope,
                initializer.Module,
                initializer.ContainingType,
                owner,
                ImportedBy(initializer.Module));

            BoundExpression value;

            if (initializer.EnumCase is EnumCaseSyntax enumCase)
                value = binder.BindEnumCase(enumCase, initializer.ContainingType!);
            else if (initializer.Syntax is ExpressionSyntax written)
                value = binder.BindInitializer(written, initializer.Field.Type);
            else
                value = binder.BindSingletonInstance(initializer.ContainingType!, initializer.Anchor);

            // A const's initializer is bound above so its conversion against the declared type is
            // checked exactly like any other's, but a const carries no slot at all (§7.1) — so
            // nothing past this point should ever see it. Leaving it out of both lists is what keeps
            // it out of ModuleEmitter's field declarations, its static/instance initializer
            // sequence, and InitializerOrder's load-order check, all in one place, rather than
            // teaching each of those to skip it individually.
            if (initializer.Field.IsConst)
                return;

            _boundInitializers.Add(new BoundFieldInitializer(
                initializer.Field, value, initializer.ContainingType, initializer.Order));

            _loadFragments.Add(new InitializerOrder.Fragment(
                initializer.Module,
                initializer.ContainingType,
                initializer.Field,
                initializer.Order,
                value,
                initializer.SourceName));
        }

        /// <summary>
        /// Records a declaration's attributes to be resolved once every type exists (§11).
        /// </summary>
        private void RecordAttributes(Symbol target, IReadOnlyList<AttributeSyntax> syntax, Scope scope, string sourceName)
        {
            if (syntax.Count > 0)
                _attributes.Add(new AttributeBinding(target, syntax, scope, sourceName));
        }

        /// <summary>
        /// Resolves one declaration's attributes: the class each names, and the constants it is given.
        /// </summary>
        /// <remarks>
        /// <para>
        /// §11 fixes the syntax and leaves the vocabulary open, so nothing here knows any particular
        /// attribute — what it checks is that the name resolves to a class extending
        /// <c>Attribute</c>, which is what the runtime instantiates at load, and that every argument
        /// is a constant, which is what the metadata can carry.
        /// </para>
        /// <para>
        /// An attribute that does not resolve is reported and dropped rather than failing the
        /// declaration it sits on: the declaration is still perfectly good, and §11's audience is
        /// tooling and host reflection rather than the program's own meaning.
        /// </para>
        /// </remarks>
        private void BindAttributes(AttributeBinding binding)
        {
            var uses = new List<AttributeUse>(binding.Syntax.Count);

            foreach (var written in binding.Syntax)
            {
                var resolved = _resolver.Resolve(
                    new NamedTypeSyntax(written.Span, new[] { written.Name }, System.Array.Empty<TypeSyntax>()),
                    binding.Scope,
                    binding.SourceName);

                if (resolved.IsError)
                    continue;

                if (resolved.NonNullable is not NamedTypeSymbol type || !ExtendsAttribute(type))
                {
                    ReportAt(
                        binding.SourceName,
                        written.Span,
                        SurtrDiagnosticCode.InvalidAttribute,
                        $"'{written.Name}' is not an attribute; §11 attaches a class extending 'Attribute'.");

                    continue;
                }

                if (type.IsAttribute && type.AllowedAttributeTargets != SurtrAttributeTargets.None)
                {
                    SurtrAttributeTargets actual = DeclarationTargetOf(binding.Target);
                    if ((type.AllowedAttributeTargets & actual) == 0)
                    {
                        ReportAt(
                            binding.SourceName,
                            written.Span,
                            SurtrDiagnosticCode.AttributeTargetMismatch,
                            $"'{written.Name}' cannot be written here; its target list does not include {DescribeAttributeTarget(actual)}.");

                        continue;
                    }
                }

                var arguments = new object?[written.Arguments.Count];
                bool folded = true;

                for (int i = 0; i < arguments.Length; i++)
                {
                    if (Constants.TryEvaluate(written.Arguments[i], out object? value))
                    {
                        arguments[i] = value;
                        continue;
                    }

                    ReportAt(
                        binding.SourceName,
                        written.Arguments[i].Span,
                        SurtrDiagnosticCode.NotAConstant,
                        $"An argument to '{written.Name}' has to fold at compile time; an attribute is built at load, before anything runs.");

                    folded = false;
                }

                if (folded)
                    uses.Add(new AttributeUse(type, arguments));
            }

            if (uses.Count > 0)
                binding.Target.Attributes = uses;
        }

        private static bool ExtendsAttribute(NamedTypeSymbol type)
        {
            for (var walk = type; walk is not null; walk = walk.BaseType)
            {
                if (string.Equals(walk.Name, "Attribute", StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Which of §11's declaration kinds a symbol is, for matching against an
        /// <c>attribute(...)</c>'s target list. <see cref="SurtrAttributeTargets.None"/> for anything a
        /// target list cannot name (a module, an alias, a parameter, a local) — which is exactly
        /// right, since a restricted attribute could then never match it.
        /// </summary>
        private static SurtrAttributeTargets DeclarationTargetOf(Symbol symbol) => symbol switch
        {
            NamedTypeSymbol { TypeKind: TypeSymbolKind.Interface } => SurtrAttributeTargets.Interface,
            NamedTypeSymbol { TypeKind: TypeSymbolKind.Enum } => SurtrAttributeTargets.Enum,
            NamedTypeSymbol => SurtrAttributeTargets.Class,
            FieldSymbol => SurtrAttributeTargets.Field,
            PropertySymbol => SurtrAttributeTargets.Property,
            MethodSymbol => SurtrAttributeTargets.Method,
            _ => SurtrAttributeTargets.None,
        };

        private static string DescribeAttributeTarget(SurtrAttributeTargets target) => target switch
        {
            SurtrAttributeTargets.Class => "a class",
            SurtrAttributeTargets.Interface => "an interface",
            SurtrAttributeTargets.Enum => "an enum",
            SurtrAttributeTargets.Field => "a field",
            SurtrAttributeTargets.Property => "a property",
            SurtrAttributeTargets.Method => "a method",
            _ => "this kind of declaration",
        };

        /// <summary>
        /// Binds one <c>static { ... }</c> block, in the scope its declaration sits in (§2.5, §3.2).
        /// </summary>
        /// <remarks>
        /// The owner it is bound against is static and nameless, for the same reason a field
        /// initializer's is: the block runs inside the container's static initializer, and this
        /// exists so a <c>this</c> written inside one is reported rather than bound against nothing.
        /// </remarks>
        private void BindStaticBlock(StaticBlockBinding block)
        {
            EnterContext(block.Module, block.ContainingType);
            var owner = new MethodSymbol(
                "static",
                (Symbol?)block.ContainingType ?? block.Module,
                _factory.Void)
            {
                IsStatic = true,
            };

            var binder = new BodyBinder(
                _factory,
                _resolver,
                Conversions,
                MemberLookup,
                OverloadResolution,
                Constants,
                _diagnostics,
                block.SourceName,
                block.Scope,
                block.Module,
                block.ContainingType,
                owner,
                ImportedBy(block.Module));

            var body = binder.BindBody(block.Syntax.Body);
            _boundStaticBlocks.Add(new BoundStaticBlock(body, block.ContainingType, block.Module, block.Order));

            _loadFragments.Add(new InitializerOrder.Fragment(
                block.Module, block.ContainingType, field: null, block.Order, body, block.SourceName));
        }

        /// <summary>
        /// Binds a constructor's <c>super(...)</c> or <c>this(...)</c> against the constructors of
        /// the type it names (§3.2).
        /// </summary>
        private void BindChain(ChainBinding chain)
        {
            EnterContext(chain.Module, chain.Owner);
            var target = chain.Syntax.ChainsToThis ? chain.Owner : chain.Owner.BaseType;

            if (target is null)
            {
                ReportAt(
                    chain.SourceName,
                    chain.Syntax.Span,
                    SurtrDiagnosticCode.InvalidConstructorChain,
                    $"'{chain.Owner.Name}' has no base class, so there is no 'super' constructor to chain to.");

                return;
            }

            var binder = new BodyBinder(
                _factory,
                _resolver,
                Conversions,
                MemberLookup,
                OverloadResolution,
                Constants,
                _diagnostics,
                chain.SourceName,
                chain.Scope,
                chain.Module,
                chain.Owner,
                chain.Constructor,
                ImportedBy(chain.Module));

            if (binder.BindConstructorChain(chain.Syntax, target, chain.Syntax.ChainsToThis) is BoundConstructorChain bound)
                _boundChains.Add(chain.Constructor, bound);
        }

        /// <summary>
        /// Rejects a <c>this(...)</c> chain that comes back round to where it started.
        /// </summary>
        /// <remarks>
        /// Nothing downstream could survive one: the emitter would emit each constructor calling the
        /// next, and the cycle would only show up as a stack overflow the first time an instance was
        /// built. A <c>super</c> chain cannot loop, since the hierarchy itself is already acyclic.
        /// </remarks>
        private void RejectChainCycles()
        {
            foreach (var chain in _chains)
            {
                if (!chain.Syntax.ChainsToThis)
                    continue;

                var walk = chain.Constructor;
                for (int step = 0; step < _chains.Count + 1; step++)
                {
                    if (!_boundChains.TryGetValue(walk, out var next) || !next.IsThis)
                        break;

                    walk = next.Target;

                    if (!ReferenceEquals(walk, chain.Constructor))
                        continue;

                    ReportAt(
                        chain.SourceName,
                        chain.Syntax.Span,
                        SurtrDiagnosticCode.InvalidConstructorChain,
                        $"This 'this(...)' chain on '{chain.Owner.Name}' comes back to itself, so no constructor would ever run.");

                    break;
                }
            }
        }

        private void BindOne(BodyBinding body)
        {
            if (_bound.ContainsKey(body.Method))
                return;

            EnterContext(body.Module, body.ContainingType);

            var binder = new BodyBinder(
                _factory,
                _resolver,
                Conversions,
                MemberLookup,
                OverloadResolution,
                Constants,
                _diagnostics,
                body.SourceName,
                body.Scope,
                body.Module,
                body.ContainingType,
                body.Method,
                ImportedBy(body.Module));

            var bound = binder.BindBody(body.Syntax);
            _bound.Add(body.Method, bound);
            _bodyFiles[body.Method] = body.SourceName;

            // After binding, not during: reachability and definite assignment are questions
            // about a whole body, and the bound tree is the form that has one.
            FlowAnalysis.Analyze(body.Method, bound, _diagnostics, body.SourceName);
        }

        /// <summary>
        /// Checks every <c>const fun</c> and gives the constant evaluator something that can run one.
        /// </summary>
        /// <remarks>
        /// Both halves belong together: a function that breaks §7.2's rules is reported against its
        /// own declaration, and only the ones left are worth building a scratch runtime for. A
        /// compilation with no <c>const fun</c> at all builds none, so nothing pays for a feature it
        /// does not use.
        /// </remarks>
        private void PrepareConstFolding()
        {
            bool any = false;

            foreach (var body in _bodies)
            {
                if (!body.Method.IsConst || !_bound.TryGetValue(body.Method, out var bound))
                    continue;

                ConstFunctionCheck.Verify(body.Method, bound, _diagnostics, body.SourceName);

                if (!_constFunctions.TryGetValue(body.Method.Name, out var overloads))
                    _constFunctions.Add(body.Method.Name, overloads = new List<MethodSymbol>());

                overloads.Add(body.Method);
                any = true;
            }

            if (!any)
                return;

            _constFolder = new ConstFolder(_bound);
            Constants.CallFolder = FoldConstCall;
        }

        /// <summary>
        /// Resolves a <c>const fun</c> call written inside a constant expression, and folds it.
        /// </summary>
        /// <remarks>
        /// Resolution is by name and argument count across the whole compilation, which is the same
        /// fidelity <see cref="ConstantEvaluator"/> already gives a constant's name: folding runs
        /// over syntax, before scopes exist in the form a call site would need. Two const functions
        /// answering to one name and arity make the call ambiguous rather than arbitrary, so nothing
        /// is folded silently against the wrong one.
        /// </remarks>
        private bool FoldConstCall(string name, IReadOnlyList<object?> arguments, out object? value)
        {
            value = null;

            if (_constFolder is null || !_constFunctions.TryGetValue(name, out var overloads))
                return false;

            MethodSymbol? match = null;
            foreach (var candidate in overloads)
            {
                if (candidate.Parameters.Count != arguments.Count)
                    continue;

                if (match is not null)
                {
                    _lastFoldFailure = $"'{name}' names more than one const function taking {arguments.Count} argument(s).";
                    return false;
                }

                match = candidate;
            }

            if (match is null)
            {
                _lastFoldFailure = $"'{name}' is not a const function taking {arguments.Count} argument(s).";
                return false;
            }

            bool folded = _constFolder.TryFold(match, arguments, out value, out string failure);
            if (!folded)
                _lastFoldFailure = failure;

            return folded;
        }

        /// <summary>
        /// Checks that every <c>const</c> initializer really is a constant expression (§7.1).
        /// </summary>
        /// <remarks>
        /// Last, because an initializer may call a <c>const fun</c> and folding one needs its bound
        /// body. Nothing forces a constant to be folded otherwise — most are read by a <c>const if</c>
        /// or by nothing at all — so this is the pass that turns "did not fold" into a diagnostic
        /// rather than into a silently missing value.
        /// </remarks>
        private void VerifyConstantDeclarations()
        {
            foreach (var declaration in _constantDeclarations)
            {
                _lastFoldFailure = null;

                if (Constants.TryGetValue(declaration.Name, out _))
                    continue;

                ReportAt(
                    declaration.SourceName,
                    declaration.Initializer.Span,
                    SurtrDiagnosticCode.NotAConstant,
                    _lastFoldFailure is null
                        ? $"'{declaration.Name}' is const, so its initializer has to fold at compile time."
                        : $"'{declaration.Name}' is const and its initializer did not fold: {_lastFoldFailure}");
            }
        }

        /// <summary>Releases the scratch runtime const folding used, if one was built.</summary>
        public void Dispose()
        {
            _constFolder?.Dispose();
            _constFolder = null;
        }

        private void RecordBody(
            MethodSymbol method,
            BlockStatementSyntax? syntax,
            Scope scope,
            ModuleSymbol module,
            NamedTypeSymbol? containingType,
            string sourceName)
        {
            if (syntax is not null)
                _bodies.Add(new BodyBinding(method, syntax, scope, module, containingType, sourceName));
        }
        #endregion

        #region Member binding
        private FieldSymbol BindField(FieldDeclarationSyntax syntax, NamedTypeSymbol owner, TypeBinding binding)
        {
            var field = new FieldSymbol(syntax.Name, owner, ResolveOrInfer(syntax.Type, binding.Scope, binding.SourceName))
            {
                // §7.1: a const is implicitly static and never written to again — there is no
                // per-instance constant, so neither is read from what was written on the declaration.
                IsStatic = syntax.IsConst || syntax.IsStatic,
                IsReadOnly = syntax.IsConst || !syntax.IsMutable,
                IsConst = syntax.IsConst,
                Accessibility = Translate(syntax.Visibility, Accessibility.Private),
            };

            CheckConstType(field, syntax, binding.SourceName);

            if (syntax.Initializer is not null)
            {
                _initializers.Add(new InitializerBinding(
                    field, syntax.Initializer, null, binding.Scope, binding.Module, owner, binding.SourceName, _nextInitializerOrder++));
            }

            RecordAttributes(field, syntax.Attributes, binding.Scope, binding.SourceName);
            return field;
        }

        private FieldSymbol BindModuleField(FieldDeclarationSyntax syntax, ModuleSymbol owner, Scope scope, string sourceName)
        {
            var field = new FieldSymbol(syntax.Name, owner, ResolveOrInfer(syntax.Type, scope, sourceName))
            {
                IsStatic = true,
                IsReadOnly = syntax.IsConst || !syntax.IsMutable,
                IsConst = syntax.IsConst,
                Accessibility = Translate(syntax.Visibility, Accessibility.Internal),
            };

            CheckConstType(field, syntax, sourceName);

            if (syntax.Initializer is not null)
                _initializers.Add(new InitializerBinding(field, syntax.Initializer, null, scope, owner, null, sourceName, _nextInitializerOrder++));

            RecordAttributes(field, syntax.Attributes, scope, sourceName);
            return field;
        }

        /// <summary>
        /// Binds a module-level <c>native let/var</c> as a native property (§10).
        /// </summary>
        /// <remarks>
        /// The host owns the storage, so a read is a call to a native <c>get_x</c> accessor and a
        /// write (for <c>var</c>) to a native <c>set_x</c>, both published by link name through
        /// <c>SurtrRuntime.DefineNativeBody</c> — the same path a class-level native member takes.
        /// The syntax is a field declaration, and the binder's job is the shape change: everything
        /// downstream reads the accessors as ordinary methods, which is exactly how a source
        /// property is wired.
        /// </remarks>
        private PropertySymbol BindModuleNativeVariable(
            FieldDeclarationSyntax syntax,
            ModuleSymbol owner,
            Scope scope,
            string sourceName)
        {
            var type = ResolveOrInfer(syntax.Type, scope, sourceName);
            var accessibility = Translate(syntax.Visibility, Accessibility.Internal);

            var property = new PropertySymbol(syntax.Name, owner, type)
            {
                IsStatic = true,
                Accessibility = accessibility,
            };

            var getter = new MethodSymbol(MemberNames.Getter(syntax.Name), owner, type)
            {
                IsStatic = true,
                Accessibility = accessibility,
                Role = MethodRole.PropertyGetter,
                IsNative = true,
            };
            property.Getter = getter;

            // `native let` is read-only: no setter, so a write is rejected where a property with
            // no setter is, without the emitter needing a special case.
            if (syntax.IsMutable)
            {
                var setter = new MethodSymbol(MemberNames.Setter(syntax.Name), owner, _factory.Void)
                {
                    IsStatic = true,
                    Accessibility = accessibility,
                    Role = MethodRole.PropertySetter,
                    IsNative = true,
                };
                setter.Parameters = new[] { new ParameterSymbol("value", type, 0, setter) };
                property.Setter = setter;
            }

            // §10: the host owns the value, so there is nothing here to initialize — and an
            // initializer would be written against a value the host may already have set.
            if (syntax.Initializer is not null)
            {
                ReportAt(sourceName, syntax.Span, SurtrDiagnosticCode.InvalidNativeDeclaration,
                    $"'{syntax.Name}' is native, so the host owns its value and it cannot have an initializer.");
            }

            RecordAttributes(property, syntax.Attributes, scope, sourceName);
            return property;
        }

        /// <summary>
        /// Binds a class-level <c>native let/var</c> as a native property (§10) — the class-member
        /// twin of <see cref="BindModuleNativeVariable"/>.
        /// </summary>
        /// <remarks>
        /// Written as a field for the same readability reason a module-level one is, but there is
        /// no backing storage: the host owns the value, so a read is a call to a native
        /// <c>get_x</c> and a write (for <c>var</c>) to a native <c>set_x</c>, both published by
        /// link name the same way any other native member of the class is. Static or instance
        /// follows <c>syntax.IsStatic</c> — unlike the module-level form, which is always static
        /// because a module has no instances.
        /// </remarks>
        private PropertySymbol BindClassNativeField(FieldDeclarationSyntax syntax, NamedTypeSymbol owner, TypeBinding binding)
        {
            var type = ResolveOrInfer(syntax.Type, binding.Scope, binding.SourceName);
            var accessibility = Translate(syntax.Visibility, Accessibility.Private);

            var property = new PropertySymbol(syntax.Name, owner, type)
            {
                IsStatic = syntax.IsStatic,
                Accessibility = accessibility,
            };

            var getter = new MethodSymbol(MemberNames.Getter(syntax.Name), owner, type)
            {
                IsStatic = syntax.IsStatic,
                Accessibility = accessibility,
                Role = MethodRole.PropertyGetter,
                IsNative = true,
            };
            property.Getter = getter;

            // `native let` is read-only: no setter, the same rule a module-level one follows.
            if (syntax.IsMutable)
            {
                var setter = new MethodSymbol(MemberNames.Setter(syntax.Name), owner, _factory.Void)
                {
                    IsStatic = syntax.IsStatic,
                    Accessibility = accessibility,
                    Role = MethodRole.PropertySetter,
                    IsNative = true,
                };
                setter.Parameters = new[] { new ParameterSymbol("value", type, 0, setter) };
                property.Setter = setter;
            }

            // §10: the host owns the value, so there is nothing here to initialize — and an
            // initializer would be written against a value the host may already have set.
            if (syntax.Initializer is not null)
            {
                ReportAt(binding.SourceName, syntax.Span, SurtrDiagnosticCode.InvalidNativeDeclaration,
                    $"'{syntax.Name}' is native, so the host owns its value and it cannot have an initializer.");
            }

            RecordAttributes(property, syntax.Attributes, binding.Scope, binding.SourceName);
            return property;
        }

        /// <summary>
        /// Rejects a <c>const</c> whose declared type is not a primitive or <c>string</c> (§7.1) —
        /// a composite value cannot be substituted at a use site, since each use would need its own
        /// object, which is not what a constant means.
        /// </summary>
        private void CheckConstType(FieldSymbol field, FieldDeclarationSyntax syntax, string sourceName)
        {
            if (!syntax.IsConst)
                return;

            var type = field.Type.NonNullable;
            if (type.IsPrimitive || type.SpecialType == SpecialType.String)
                return;

            ReportAt(sourceName, syntax.Span, SurtrDiagnosticCode.InvalidConstType,
                $"'{syntax.Name}' is const, so its type has to be a primitive or 'string' (§7.1), not '{field.Type.ToDisplayString()}'.");
        }

        private PropertySymbol BindProperty(
            PropertyDeclarationSyntax syntax,
            NamedTypeSymbol owner,
            TypeBinding binding,
            bool isInterface)
        {
            var type = _resolver.Resolve(syntax.Type, binding.Scope, binding.SourceName);
            var accessibility = Translate(syntax.Visibility, isInterface ? Accessibility.Public : Accessibility.Private);

            var property = new PropertySymbol(syntax.Name, owner, type)
            {
                IsStatic = syntax.IsStatic,
                Accessibility = accessibility,
            };

            WireAccessors(
                property, syntax.Accessors, owner, syntax.Dispatch, syntax.IsSealed, isInterface, accessibility,
                binding.Scope, binding.Module, owner, binding.SourceName, syntax.Inline, syntax.IsNative);

            RecordAttributes(property, syntax.Attributes, binding.Scope, binding.SourceName);
            return property;
        }

        private PropertySymbol BindModuleProperty(
            PropertyDeclarationSyntax syntax,
            ModuleSymbol owner,
            Scope scope,
            string sourceName)
        {
            var property = new PropertySymbol(syntax.Name, owner, _resolver.Resolve(syntax.Type, scope, sourceName))
            {
                IsStatic = true,
                Accessibility = Translate(syntax.Visibility, Accessibility.Internal),
            };

            WireAccessors(
                property, syntax.Accessors, owner, DispatchModifier.None, isSealed: false, isInterface: false, property.Accessibility,
                scope, owner, containingType: null, sourceName, syntax.Inline, syntax.IsNative);

            RecordAttributes(property, syntax.Attributes, scope, sourceName);
            return property;
        }

        /// <summary>
        /// Gives a property the <c>get_x</c>/<c>set_x</c> pair the linker looks for.
        /// </summary>
        /// <remarks>
        /// A property is a name in front of two methods, and the runtime only ever sees the
        /// methods. Emitting them here rather than at codegen keeps overload resolution and
        /// override checking working on one kind of symbol.
        /// </remarks>
        private void WireAccessors(
            PropertySymbol property,
            IReadOnlyList<AccessorSyntax> accessors,
            Symbol owner,
            DispatchModifier dispatch,
            bool isSealed,
            bool isInterface,
            Accessibility accessibility,
            Scope scope,
            ModuleSymbol module,
            NamedTypeSymbol? containingType,
            string sourceName,
            InlineModifier inline,
            bool isNative)
        {
            // A property written bare is `get` alone, and an auto-property either way: an accessor
            // with no body is one code generation synthesises against a backing field.
            AccessorSyntax? getter = accessors.Count == 0 ? new AccessorSyntax(default, true, null) : null;
            AccessorSyntax? setter = null;

            for (int i = 0; i < accessors.Count; i++)
            {
                if (accessors[i].IsGetter)
                    getter = accessors[i];
                else
                    setter = accessors[i];
            }

            if (getter is not null)
            {
                Accessibility getterAccessibility = ResolveAccessorAccessibility(getter, accessibility, sourceName);
                var bound = new MethodSymbol(MemberNames.Getter(property.Name), owner, property.Type)
                {
                    IsStatic = property.IsStatic,
                    Accessibility = getterAccessibility,
                    Role = MethodRole.PropertyGetter,
                    Dispatch = TranslateDispatch(getter.HasOwnDispatch ? getter.Dispatch : dispatch, isInterface),
                    IsOverride = (getter.HasOwnDispatch ? getter.Dispatch : dispatch) == DispatchModifier.Override,
                    IsSealed = getter.HasOwnDispatch ? getter.IsSealed : isSealed,
                    IsInline = (getter.Inline != InlineModifier.None ? getter.Inline : inline) == InlineModifier.Inline,
                    IsForceInline = (getter.Inline != InlineModifier.None ? getter.Inline : inline) == InlineModifier.ForceInline,
                    IsNative = isNative,
                };

                RecordBody(bound, getter.Body, scope, module, containingType, sourceName);
                property.Getter = bound;
            }

            if (setter is not null)
            {
                Accessibility setterAccessibility = ResolveAccessorAccessibility(setter, accessibility, sourceName);
                var bound = new MethodSymbol(MemberNames.Setter(property.Name), owner, _factory.Void)
                {
                    IsStatic = property.IsStatic,
                    Accessibility = setterAccessibility,
                    Role = MethodRole.PropertySetter,
                    Dispatch = TranslateDispatch(setter.HasOwnDispatch ? setter.Dispatch : dispatch, isInterface),
                    IsOverride = (setter.HasOwnDispatch ? setter.Dispatch : dispatch) == DispatchModifier.Override,
                    IsSealed = setter.HasOwnDispatch ? setter.IsSealed : isSealed,
                    IsInline = (setter.Inline != InlineModifier.None ? setter.Inline : inline) == InlineModifier.Inline,
                    IsForceInline = (setter.Inline != InlineModifier.None ? setter.Inline : inline) == InlineModifier.ForceInline,
                    IsNative = isNative,
                };

                bound.Parameters = new[] { new ParameterSymbol("value", property.Type, 0, bound) };
                RecordBody(bound, setter.Body, scope, module, containingType, sourceName);
                property.Setter = bound;
            }
        }

        /// <summary>
        /// An accessor's effective accessibility: its own, when it wrote one, otherwise the
        /// property's. An accessor's own visibility must be strictly narrower than the property's —
        /// equal is pointless (the accessor could have written nothing) and wider would let a caller
        /// reach, through that one accessor, something the property itself already hides from them.
        /// </summary>
        private Accessibility ResolveAccessorAccessibility(AccessorSyntax accessor, Accessibility propertyAccessibility, string sourceName)
        {
            if (accessor.Visibility == Visibility.Default)
                return propertyAccessibility;

            Accessibility resolved = Translate(accessor.Visibility, propertyAccessibility);
            if (resolved >= propertyAccessibility)
            {
                ReportAt(sourceName, accessor.Span, SurtrDiagnosticCode.AccessorVisibilityNotNarrower,
                    $"An accessor's own visibility must be narrower than the property's — '{Describe(resolved)}' is not narrower than '{Describe(propertyAccessibility)}'.");
            }

            return resolved;
        }

        private static string Describe(Accessibility accessibility) => accessibility switch
        {
            Accessibility.Private => "private",
            Accessibility.Protected => "protected",
            Accessibility.Internal => "internal",
            _ => "public",
        };

        private MethodSymbol BindMethod(
            MethodDeclarationSyntax syntax,
            NamedTypeSymbol owner,
            TypeBinding binding,
            bool isInterface)
        {
            var scope = binding.Scope.CreateChild();
            var method = new MethodSymbol(syntax.Name, owner, _factory.ErrorType)
            {
                IsStatic = syntax.IsStatic,
                Accessibility = Translate(syntax.Visibility, isInterface ? Accessibility.Public : Accessibility.Private),
                Dispatch = TranslateDispatch(syntax.Dispatch, isInterface),
                IsOverride = syntax.Dispatch == DispatchModifier.Override,
                IsSealed = syntax.IsSealed,
                IsNative = syntax.IsNative,
                IsInline = syntax.Inline == InlineModifier.Inline,
                IsForceInline = syntax.Inline == InlineModifier.ForceInline,
                IsConst = syntax.IsConst,
            };

            BindTypeParameters(method, syntax.TypeParameters, scope, binding.SourceName);
            BindMethodReturnType(method, syntax, scope, binding.SourceName);
            method.Parameters = BindParameters(syntax.Parameters, method, scope, binding.SourceName);
            RecordBody(method, syntax.Body, scope, binding.Module, owner, binding.SourceName);
            RecordAttributes(method, syntax.Attributes, binding.Scope, binding.SourceName);
            return method;
        }

        private MethodSymbol BindModuleMethod(
            MethodDeclarationSyntax syntax,
            ModuleSymbol owner,
            Scope moduleScope,
            string sourceName)
        {
            var scope = moduleScope.CreateChild();
            var method = new MethodSymbol(syntax.Name, owner, _factory.ErrorType)
            {
                IsStatic = true,
                Accessibility = Translate(syntax.Visibility, Accessibility.Internal),
                IsNative = syntax.IsNative,
                IsInline = syntax.Inline == InlineModifier.Inline,
                IsForceInline = syntax.Inline == InlineModifier.ForceInline,
                IsConst = syntax.IsConst,
            };

            BindTypeParameters(method, syntax.TypeParameters, scope, sourceName);
            BindMethodReturnType(method, syntax, scope, sourceName);
            method.Parameters = BindParameters(syntax.Parameters, method, scope, sourceName);
            RecordBody(method, syntax.Body, scope, owner, containingType: null, sourceName);
            RecordAttributes(method, syntax.Attributes, scope, sourceName);
            return method;
        }

        /// <summary>
        /// Resolves a method's return type, or records that the body is to infer it (§8).
        /// </summary>
        /// <remarks>
        /// An omitted return type is inferred from the body — exactly as a lambda's is — but only
        /// where there is a body to infer from and a signature the method owns. A bodyless method
        /// (<c>abstract</c>, <c>native</c>, an interface member) has nothing to infer from, and an
        /// <c>override</c> must match the contract it replaces, which cannot be checked against an
        /// inferred one; both have to write it.
        /// </remarks>
        private void BindMethodReturnType(MethodSymbol method, MethodDeclarationSyntax syntax, Scope scope, string sourceName)
        {
            if (syntax.ReturnType is not null)
            {
                method.ReturnType = _resolver.Resolve(syntax.ReturnType, scope, sourceName);
                return;
            }

            if (syntax.Dispatch == DispatchModifier.Override)
            {
                ReportAt(sourceName, syntax.Span, SurtrDiagnosticCode.ReturnTypeRequired,
                    $"'{method.Name}' overrides a member, so its return type cannot be inferred; write it after the ':'.");
                return;
            }

            if (syntax.Body is null)
            {
                ReportAt(sourceName, syntax.Span, SurtrDiagnosticCode.ReturnTypeRequired,
                    $"'{method.Name}' has no body to infer a return type from; write it after the ':'.");
                return;
            }

            _inferReturnTypes.Add(method);
        }

        private MethodSymbol BindConstructor(
            ConstructorDeclarationSyntax syntax,
            NamedTypeSymbol owner,
            TypeBinding binding)
        {
            var method = new MethodSymbol(MemberNames.Constructor, owner, _factory.Void)
            {
                Accessibility = Translate(syntax.Visibility, Accessibility.Public),
                Role = MethodRole.Constructor,
            };

            method.Parameters = BindParameters(syntax.Parameters, method, binding.Scope, binding.SourceName);
            RecordBody(method, syntax.Body, binding.Scope, binding.Module, owner, binding.SourceName);
            RecordAttributes(method, syntax.Attributes, binding.Scope, binding.SourceName);

            // Bound in a pass of its own, after every signature exists: a chain names a constructor
            // of this class or of its base, and overload resolution needs both complete.
            if (syntax.ChainArguments is not null)
                _chains.Add(new ChainBinding(method, syntax, binding.Scope, binding.Module, owner, binding.SourceName));

            return method;
        }

        private MethodSymbol BindOperator(OperatorDeclarationSyntax syntax, NamedTypeSymbol owner, TypeBinding binding)
        {
            bool isConversion = syntax.Operator == TokenType.KeywordAs;

            string name = OperatorNames.TryGetSymbol(syntax.Operator, out _)
                ? OperatorNames.For(syntax.Operator, syntax.Parameters.Count)
                : OperatorNames.Prefix + "?";

            bool isInterface = binding.Symbol.TypeKind == TypeSymbolKind.Interface;

            // §5.6: an overload is always public. A plain declaration is a static method taking
            // every operand; a dispatch modifier, or an interface declaration, makes it an instance
            // method whose receiver is the first parameter — that is what lets an operator follow
            // the same virtual/abstract rules as any other method, and reach the same vtable and
            // interface slots (§2.2). An interface forces abstract, exactly as a method does.
            // A conversion is the exception: its single parameter is the source, its target is the
            // return, and nothing about it ever dispatches, so it stays static.
            bool instanceOperator = !isConversion
                && (isInterface
                    || syntax.Dispatch != DispatchModifier.None
                    || syntax.IsSealed);

            if (isConversion && (isInterface || syntax.Dispatch != DispatchModifier.None || syntax.IsSealed))
            {
                Report(
                    SurtrDiagnosticCode.InvalidOperatorSignature,
                    binding,
                    syntax.Span,
                    "A conversion ('operator as') is always static: it is declared on the source type and never dispatches.");
            }

            var method = new MethodSymbol(name, owner, _factory.ErrorType)
            {
                IsStatic = !instanceOperator,
                Accessibility = Accessibility.Public,
                Role = MethodRole.Operator,
                IsConversion = isConversion,
                Dispatch = instanceOperator ? TranslateDispatch(syntax.Dispatch, isInterface) : MethodDispatch.Direct,
                IsOverride = syntax.Dispatch == DispatchModifier.Override,
                IsSealed = syntax.IsSealed,
            };

            method.ReturnType = _resolver.Resolve(syntax.ReturnType, binding.Scope, binding.SourceName);
            method.Parameters = BindParameters(syntax.Parameters, method, binding.Scope, binding.SourceName);

            // Abstract operators end at `;`, and a concrete one needs a body — the same bargain a
            // method makes, and the same two mistakes get caught here at binding rather than
            // leaving one of them to the emitter's "has no body to emit".
            if (method.Dispatch == MethodDispatch.Abstract && syntax.Body is not null)
            {
                Report(
                    SurtrDiagnosticCode.InvalidOperatorSignature,
                    binding,
                    syntax.Body.Span,
                    $"An abstract operator cannot have a body.");
            }
            else if (method.Dispatch != MethodDispatch.Abstract && syntax.Body is null)
            {
                Report(
                    SurtrDiagnosticCode.InvalidOperatorSignature,
                    binding,
                    syntax.Span,
                    $"An operator that is not abstract needs a body.");
            }

            if (syntax.Body is not null)
            {
                RecordBody(method, syntax.Body, binding.Scope, binding.Module, owner, binding.SourceName);
            }

            RecordAttributes(method, syntax.Attributes, binding.Scope, binding.SourceName);
            CheckOperatorSignature(syntax, method, binding);
            return method;
        }

        /// <summary>
        /// Rejects an operator overload whose arity or return type does not match the shape §5.6's
        /// own table fixes for the token it overloads.
        /// </summary>
        /// <remarks>
        /// Nothing checked this before this fix: the parser accepts any parameter list and any
        /// return type after any overloadable token, and the only thing that ever noticed a wrong
        /// one was <c>OverloadResolution</c> quietly never finding it — leaving a real, wrong
        /// declaration to sit in the method table as a method nothing could ever call, with no
        /// diagnostic anywhere pointing at why.
        /// </remarks>
        private void CheckOperatorSignature(OperatorDeclarationSyntax syntax, MethodSymbol method, TypeBinding binding)
        {
            int count = method.Parameters.Count;
            string symbol = OperatorNames.TryGetSymbol(syntax.Operator, out string spelling) ? spelling : "?";

            switch (syntax.Operator)
            {
                case TokenType.Plus:
                case TokenType.Star:
                case TokenType.Slash:
                case TokenType.Percent:
                case TokenType.Ampersand:
                case TokenType.Pipe:
                case TokenType.Caret:
                case TokenType.ShiftLeft:
                case TokenType.ShiftRight:
                case TokenType.UnsignedShiftRight:
                    if (count != 2)
                        ReportOperatorArity(binding, syntax, symbol, "2 parameters", count);
                    break;

                case TokenType.Minus:
                    // §5.6: unary negation and binary subtraction share the token, told apart by arity.
                    if (count != 1 && count != 2)
                        ReportOperatorArity(binding, syntax, symbol, "1 parameter (unary) or 2 (binary)", count);
                    break;

                case TokenType.LogicalNot:
                case TokenType.Tilde:
                case TokenType.Increment:
                case TokenType.Decrement:
                case TokenType.KeywordAs:
                    if (count != 1)
                        ReportOperatorArity(binding, syntax, symbol, "1 parameter", count);
                    break;

                case TokenType.Equal:
                    if (count != 2)
                        ReportOperatorArity(binding, syntax, symbol, "2 parameters", count);
                    else if (!ReferenceEquals(method.ReturnType, _factory.Bool))
                        ReportOperatorReturn(binding, syntax, symbol, "bool", method.ReturnType);
                    break;

                case TokenType.Spaceship:
                    if (count != 2)
                        ReportOperatorArity(binding, syntax, symbol, "2 parameters", count);
                    else if (!ReferenceEquals(method.ReturnType, _factory.Int))
                        ReportOperatorReturn(binding, syntax, symbol, "int", method.ReturnType);
                    break;

                case TokenType.LeftBracket:
                    if (count != 2 && count != 3)
                    {
                        ReportOperatorArity(
                            binding, syntax, symbol,
                            "2 parameters (the receiver and the index) or 3 (the receiver, the index, then the value)",
                            count);
                    }
                    else if (count == 3 && !ReferenceEquals(method.ReturnType, _factory.Void))
                    {
                        ReportOperatorReturn(binding, syntax, symbol, "void", method.ReturnType);
                    }

                    break;
            }

            // §5.6: at least one operand has to be the declaring type — a type cannot define how
            // two types foreign to it interact. An indexer is the one exception: its receiver is
            // the object being indexed, so it must be the declaring type even when the index or
            // value happen to be, which is what makes `operator[]` a member rather than a two-type
            // relation. `operator as` carries its target as the return, so its single parameter is
            // the only operand there is. An instance operator is an indexer on a broader stage: its
            // first parameter *is* the receiver, so it must be the declaring type — or, for an
            // `override` implementing a base or interface, that ancestor, which is the one spelling
            // that keeps the vtable and interface slots reachable.
            bool validOperands;
            if (!method.IsStatic || syntax.Operator == TokenType.LeftBracket)
            {
                // An instance operator (abstract/virtual/override/sealed) or an indexer: the
                // receiver is the object the operator acts on, so its declared type has to be the
                // declaring type - or, for an override, the ancestor whose vtable or interface
                // slot it fills, which is exactly what the hierarchy-walking IsReceiver allows.
                validOperands = count > 0 && IsReceiver(method.Parameters[0].Type, binding.Symbol);
            }
            else
            {
                // A static operator has no receiver and no override to preserve a slot for, so
                // every operand - including the first - is checked against the declaring type
                // itself with the strict IsDeclaringType, never the ancestor-walking IsReceiver.
                // Foreign types in a subclass's own ancestry must not satisfy the rule below: a
                // type cannot define how two types foreign to it interact just because it happens
                // to extend one of them.
                bool anyOperandIsDeclaring = false;
                for (int i = 0; i < count; i++)
                    anyOperandIsDeclaring |= IsDeclaringType(method.Parameters[i].Type, binding.Symbol);

                validOperands = anyOperandIsDeclaring;
            }

            if (!validOperands)
                ReportOperatorOperand(binding, syntax, symbol, binding.Symbol, syntax.Operator == TokenType.LeftBracket || !method.IsStatic);
        }

        /// <summary>Whether <paramref name="type"/> is the type declaring an operator, or a construction of it.</summary>
        /// <remarks>
        /// A generic class's own operands arrive as a construction of its definition — <c>Matrix&lt;T&gt;</c>
        /// inside <c>class Matrix&lt;T&gt;</c> resolves to the definition built with the class's own
        /// parameter — so the definition is what the test reads. A reference's nullability is a flag,
        /// not a wrapper, so the nullable twin compares equal to the plain one.
        /// </remarks>
        private static bool IsDeclaringType(TypeSymbol type, NamedTypeSymbol owner)
        {
            var stripped = type.NonNullable;
            if (ReferenceEquals(stripped, owner))
                return true;

            return stripped is NamedTypeSymbol named
                && named.IsConstructed
                && ReferenceEquals(named.Definition, owner);
        }

        private static bool IsReceiver(TypeSymbol type, NamedTypeSymbol owner)
        {
            if (IsDeclaringType(type, owner))
                return true;

            // An `override operator` implementing a base class or an interface repeats that
            // ancestor's receiver — the one spelling that keeps its vtable or interface slot
            // reachable. `IAddable` and the class that implements it are different types, so the
            // receiver test has to walk the hierarchy rather than stop at the declaring type.
            var stripped = type.NonNullable;
            if (stripped is not NamedTypeSymbol named || named.IsConstructed)
                return false;

            var seen = new HashSet<NamedTypeSymbol>();
            var frontier = new Stack<NamedTypeSymbol>();
            frontier.Push(owner);

            while (frontier.Count > 0)
            {
                var current = frontier.Pop();
                if (!seen.Add(current))
                    continue;

                if (ReferenceEquals(current, named))
                    return true;

                if (current.BaseType is not null)
                    frontier.Push(current.BaseType);

                foreach (var @interface in current.Interfaces)
                    frontier.Push(@interface);
            }

            return false;
        }

        private void ReportOperatorOperand(
            TypeBinding binding, OperatorDeclarationSyntax syntax, string symbol, NamedTypeSymbol owner, bool receiverRequired)
        {
            string message = receiverRequired
                ? $"'operator{symbol}' must take '{owner.Name}' as its receiver (§5.6): the operands operate "
                    + $"on the object being used, which has to be '{owner.Name}' itself (or a base or interface "
                    + "it extends, when overriding one)."
                : $"'operator{symbol}' must take '{owner.Name}' among its operands (§5.6): a type cannot "
                    + "define how two types foreign to it interact.";

            Report(
                SurtrDiagnosticCode.InvalidOperatorSignature,
                binding,
                syntax.Span,
                message);
        }

        private void ReportOperatorArity(TypeBinding binding, OperatorDeclarationSyntax syntax, string symbol, string expected, int actual)
        {
            Report(
                SurtrDiagnosticCode.InvalidOperatorSignature,
                binding,
                syntax.Span,
                $"'operator{symbol}' takes {expected} (§5.6), not {actual}.");
        }

        private void ReportOperatorReturn(TypeBinding binding, OperatorDeclarationSyntax syntax, string symbol, string expected, TypeSymbol actual)
        {
            Report(
                SurtrDiagnosticCode.InvalidOperatorSignature,
                binding,
                syntax.Span,
                $"'operator{symbol}' has to return '{expected}' (§5.6), not '{actual.ToDisplayString()}'.");
        }

        private void BindTypeParameters(MethodSymbol method, IReadOnlyList<TypeParameterSyntax> syntax, Scope scope, string sourceName)
        {
            if (syntax.Count == 0)
                return;

            var parameters = new TypeParameterSymbol[syntax.Count];
            for (int i = 0; i < parameters.Length; i++)
            {
                parameters[i] = _factory.DeclareTypeParameter(syntax[i].Name, method, i);
                scope.TryDeclare(syntax[i].Name, parameters[i]);
            }

            method.TypeParameters = parameters;
            _constraints.Add(new ConstraintBinding(parameters, syntax, scope, sourceName));
        }

        /// <summary>
        /// Resolves every type parameter's bounds, once every type exists.
        /// </summary>
        /// <remarks>
        /// Run twice, and it has to be: a <em>type</em>'s parameters are declared in the declaration
        /// phase, so their bounds have to be resolved before any signature is bound against them —
        /// while a <em>method</em>'s parameters are declared by binding its signature, which happens
        /// after. Picking up where the last run stopped is what keeps a method's
        /// <c>&lt;T : IComparable&lt;T&gt;&gt;</c> from staying unbounded, which would leave its body
        /// unable to call anything on a <c>T</c>.
        /// </remarks>
        private void BindConstraints()
        {
            for (; _constraintsBound < _constraints.Count; _constraintsBound++)
            {
                var binding = _constraints[_constraintsBound];

                for (int i = 0; i < binding.Parameters.Count && i < binding.Syntax.Count; i++)
                {
                    var written = binding.Syntax[i].Constraints;
                    if (written.Count == 0)
                        continue;

                    var bounds = new TypeSymbol[written.Count];
                    for (int c = 0; c < bounds.Length; c++)
                        bounds[c] = _resolver.Resolve(written[c], binding.Scope, binding.SourceName);

                    binding.Parameters[i].Constraints = bounds;
                }
            }
        }

        private readonly struct ConstraintBinding
        {
            public ConstraintBinding(
                IReadOnlyList<TypeParameterSymbol> parameters,
                IReadOnlyList<TypeParameterSyntax> syntax,
                Scope scope,
                string sourceName)
            {
                Parameters = parameters;
                Syntax = syntax;
                Scope = scope;
                SourceName = sourceName;
            }

            public IReadOnlyList<TypeParameterSymbol> Parameters { get; }

            public IReadOnlyList<TypeParameterSyntax> Syntax { get; }

            public Scope Scope { get; }

            public string SourceName { get; }
        }

        private ParameterSymbol[] BindParameters(
            IReadOnlyList<ParameterSyntax> syntax,
            MethodSymbol owner,
            Scope scope,
            string sourceName)
        {
            var parameters = new ParameterSymbol[syntax.Count];
            for (int i = 0; i < parameters.Length; i++)
            {
                var declared = ResolveOrInfer(syntax[i].Type, scope, sourceName);

                // §3.5 declares varargs by its *element* type and says the body sees an array of
                // it, so the array is what the symbol carries: everything downstream — applicability,
                // the packing of the surplus, and the member lookup a body does on the parameter —
                // reads the parameter's type and would otherwise be reading the element's.
                parameters[i] = new ParameterSymbol(
                    syntax[i].Name,
                    syntax[i].IsVarargs && !declared.IsError ? _factory.Array(declared) : declared,
                    i,
                    owner)
                {
                    HasDefaultValue = syntax[i].DefaultValue is not null,
                    IsVararg = syntax[i].IsVarargs,
                };

                if (syntax[i].DefaultValue is ExpressionSyntax written)
                    _defaults.Add(new DefaultBinding(parameters[i], written, sourceName));
            }

            return parameters;
        }

        /// <summary>
        /// Resolves a written type, or leaves the error type where one was omitted.
        /// </summary>
        /// <remarks>
        /// An omitted type is inferred from an initializer, which needs the body phase. Reporting
        /// it here would be a diagnostic about something that is not wrong.
        /// </remarks>
        private TypeSymbol ResolveOrInfer(TypeSyntax? syntax, Scope scope, string sourceName)
            => syntax is null ? _factory.ErrorType : _resolver.Resolve(syntax, scope, sourceName);
        #endregion

        #region Helpers
        private static MethodDispatch TranslateDispatch(DispatchModifier dispatch, bool isInterface)
        {
            if (isInterface)
                return MethodDispatch.Abstract;

            return dispatch switch
            {
                DispatchModifier.Virtual => MethodDispatch.Virtual,
                DispatchModifier.Override => MethodDispatch.Virtual,
                DispatchModifier.Abstract => MethodDispatch.Abstract,
                _ => MethodDispatch.Direct,
            };
        }

        private static Accessibility Translate(Visibility visibility, Accessibility fallback) => visibility switch
        {
            Visibility.Public => Accessibility.Public,
            Visibility.Internal => Accessibility.Internal,
            Visibility.Protected => Accessibility.Protected,
            Visibility.Private => Accessibility.Private,
            _ => fallback,
        };

        private void Duplicate(TypeBinding binding, SourceSpan span, string name)
            => Report(SurtrDiagnosticCode.DuplicateDeclaration, binding, span, $"'{name}' is already declared in '{binding.Symbol.Name}'.");

        private void Report(SurtrDiagnosticCode code, TypeBinding binding, SourceSpan span, string message)
            => _diagnostics.ReportError(code, message, binding.SourceName, span);

        private void ReportAt(string sourceName, SourceSpan span, SurtrDiagnosticCode code, string message)
            => _diagnostics.ReportError(code, message, sourceName, span);

        private static string Join(IReadOnlyList<string> path, int count)
        {
            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (i > 0)
                    builder.Append('.');

                builder.Append(path[i]);
            }

            return builder.ToString();
        }

        /// <summary>One <c>const</c> declaration, kept so §7.1's rule can be checked once at the end.</summary>
        private readonly struct ConstantDeclaration
        {
            public ConstantDeclaration(string name, ExpressionSyntax initializer, string sourceName)
            {
                Name = name;
                Initializer = initializer;
                SourceName = sourceName;
            }

            public string Name { get; }

            public ExpressionSyntax Initializer { get; }

            public string SourceName { get; }
        }

        /// <summary>One parameter default, kept until there is something that can fold it.</summary>
        private readonly struct DefaultBinding
        {
            public DefaultBinding(ParameterSymbol parameter, ExpressionSyntax syntax, string sourceName)
            {
                Parameter = parameter;
                Syntax = syntax;
                SourceName = sourceName;
            }

            public ParameterSymbol Parameter { get; }

            public ExpressionSyntax Syntax { get; }

            public string SourceName { get; }
        }

        /// <summary>One field initializer or enum case, kept until phase 3 can bind it.</summary>
        private readonly struct InitializerBinding
        {
            public InitializerBinding(
                FieldSymbol field,
                ExpressionSyntax? syntax,
                EnumCaseSyntax? enumCase,
                Scope scope,
                ModuleSymbol module,
                NamedTypeSymbol? containingType,
                string sourceName,
                int order,
                SyntaxNode? anchor = null)
            {
                Field = field;
                Syntax = syntax;
                EnumCase = enumCase;
                Scope = scope;
                Module = module;
                ContainingType = containingType;
                SourceName = sourceName;
                Order = order;
                Anchor = anchor ?? (SyntaxNode?)syntax ?? enumCase!;
            }

            public FieldSymbol Field { get; }

            /// <summary>The written initializer, for an ordinary field.</summary>
            public ExpressionSyntax? Syntax { get; }

            /// <summary>The case, for an enum — which has arguments rather than an expression.</summary>
            public EnumCaseSyntax? EnumCase { get; }

            /// <summary>Where a diagnostic about this initializer points, whatever shape it has.</summary>
            public SyntaxNode Anchor { get; }

            public Scope Scope { get; }

            public ModuleSymbol Module { get; }

            public NamedTypeSymbol? ContainingType { get; }

            public string SourceName { get; }

            /// <summary>Its position among its container's initializers and <c>static</c> blocks.</summary>
            public int Order { get; }
        }

        /// <summary>A <c>static { ... }</c> block waiting to be bound, and where it runs (§2.5, §3.2).</summary>
        /// <summary>A declaration's attributes, waiting for every type to exist (§11).</summary>
        private readonly struct AttributeBinding
        {
            public AttributeBinding(Symbol target, IReadOnlyList<AttributeSyntax> syntax, Scope scope, string sourceName)
            {
                Target = target;
                Syntax = syntax;
                Scope = scope;
                SourceName = sourceName;
            }

            public Symbol Target { get; }

            public IReadOnlyList<AttributeSyntax> Syntax { get; }

            public Scope Scope { get; }

            public string SourceName { get; }
        }

        private readonly struct StaticBlockBinding
        {
            public StaticBlockBinding(
                StaticBlockDeclarationSyntax syntax,
                Scope scope,
                ModuleSymbol module,
                NamedTypeSymbol? containingType,
                string sourceName,
                int order)
            {
                Syntax = syntax;
                Scope = scope;
                Module = module;
                ContainingType = containingType;
                SourceName = sourceName;
                Order = order;
            }

            public StaticBlockDeclarationSyntax Syntax { get; }

            public Scope Scope { get; }

            public ModuleSymbol Module { get; }

            public NamedTypeSymbol? ContainingType { get; }

            public string SourceName { get; }

            public int Order { get; }
        }

        /// <summary>A constructor's written <c>super(...)</c> or <c>this(...)</c>, waiting to be bound (§3.2).</summary>
        private readonly struct ChainBinding
        {
            public ChainBinding(
                MethodSymbol constructor,
                ConstructorDeclarationSyntax syntax,
                Scope scope,
                ModuleSymbol module,
                NamedTypeSymbol owner,
                string sourceName)
            {
                Constructor = constructor;
                Syntax = syntax;
                Scope = scope;
                Module = module;
                Owner = owner;
                SourceName = sourceName;
            }

            public MethodSymbol Constructor { get; }

            public ConstructorDeclarationSyntax Syntax { get; }

            public Scope Scope { get; }

            public ModuleSymbol Module { get; }

            public NamedTypeSymbol Owner { get; }

            public string SourceName { get; }
        }

        private readonly struct BodyBinding
        {
            public BodyBinding(
                MethodSymbol method,
                BlockStatementSyntax syntax,
                Scope scope,
                ModuleSymbol module,
                NamedTypeSymbol? containingType,
                string sourceName)
            {
                Method = method;
                Syntax = syntax;
                Scope = scope;
                Module = module;
                ContainingType = containingType;
                SourceName = sourceName;
            }

            public MethodSymbol Method { get; }

            public BlockStatementSyntax Syntax { get; }

            public Scope Scope { get; }

            public ModuleSymbol Module { get; }

            public NamedTypeSymbol? ContainingType { get; }

            public string SourceName { get; }
        }

        private readonly struct TypeBinding
        {
            public TypeBinding(
                NamedTypeSymbol symbol,
                TypeDeclarationSyntax syntax,
                IReadOnlyList<DeclarationSyntax> members,
                Scope scope,
                ModuleSymbol module,
                string sourceName)
            {
                Symbol = symbol;
                Syntax = syntax;
                Members = members;
                Scope = scope;
                Module = module;
                SourceName = sourceName;
            }

            public NamedTypeSymbol Symbol { get; }

            public TypeDeclarationSyntax Syntax { get; }

            /// <summary>The members that survive this build's <c>const if</c>s.</summary>
            public IReadOnlyList<DeclarationSyntax> Members { get; }

            public Scope Scope { get; }

            public ModuleSymbol Module { get; }

            public string SourceName { get; }
        }
        #endregion

        /// <summary>One unmet abstract obligation, as <see cref="MissingAbstractMembers"/> reports it.</summary>
        public sealed class MissingMember
        {
            /// <summary>Creates one unmet abstract obligation.</summary>
            public MissingMember(NamedTypeSymbol contract, MethodSymbol required, bool fromInterface)
            {
                Contract = contract;
                Required = required;
                FromInterface = fromInterface;
            }

            /// <summary>The interface or abstract base class that declares the obligation.</summary>
            public NamedTypeSymbol Contract { get; }

            /// <summary>
            /// The member to implement, already substituted through <see cref="Contract"/>'s
            /// construction — a stub built from this reads the receiver's own type arguments, not
            /// the open declaration's.
            /// </summary>
            public MethodSymbol Required { get; }

            /// <summary>
            /// Whether <see cref="Contract"/> is an interface — a stub for it never writes
            /// <c>override</c> (§3.3: satisfying an interface never requires it) — or an abstract
            /// base class, where <c>override</c> is mandatory.
            /// </summary>
            public bool FromInterface { get; }
        }
    }
}
