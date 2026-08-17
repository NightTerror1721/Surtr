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

        private readonly Dictionary<string, List<ModuleSymbol>> _importedModules =
            new Dictionary<string, List<ModuleSymbol>>(StringComparer.Ordinal);

        private readonly List<BodyBinding> _bodies = new List<BodyBinding>();
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
                if (syntax.TypeParameters.Count > 10)
                {
                    _diagnostics.ReportError(
                        SurtrDiagnosticCode.TooManyTypeParameters,
                        $"'{syntax.Name}' declares {syntax.TypeParameters.Count} type parameters; "
                            + "the descriptor form encodes at most ten.",
                        sourceName,
                        syntax.Span);
                }

                var parameters = new TypeParameterSymbol[syntax.TypeParameters.Count];
                for (int i = 0; i < parameters.Length; i++)
                    parameters[i] = _factory.DeclareTypeParameter(syntax.TypeParameters[i].Name, symbol, i);

                symbol.SetTypeParameters(parameters);
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

            // A type's own parameters and its nested types are visible inside it and nowhere else.
            var typeScope = scope.CreateChild();
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
                if (member is TypeDeclarationSyntax or AliasDeclarationSyntax)
                    DeclareMember(member, module, symbol, typeScope, nested, nestedNames, sourceName);
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
            var imported = new List<ModuleSymbol>();
            _importedModules.Add(sourceModule.Path, imported);

            foreach (var unit in sourceModule.Units)
            {
                foreach (var import in unit.Syntax.Imports)
                {
                    if (import.IsWildcard)
                    {
                        if (TryGetModuleSymbol(Join(import.Path, import.Path.Count), out var module))
                        {
                            foreach (var type in module.Types)
                                scope.AddCandidate(type.Name, type);

                            // §2.5 makes a module a container of members, so a wildcard import
                            // brings its functions and variables in too — not only its types.
                            if (!imported.Contains(module))
                                imported.Add(module);
                        }

                        continue;
                    }

                    // A named import brings exactly one name in, and the longest module prefix says
                    // where the module ends.
                    for (int split = import.Path.Count - 1; split > 0; split--)
                    {
                        if (!TryGetModuleSymbol(Join(import.Path, split), out var module))
                            continue;

                        foreach (var type in module.FindTypes(import.Path[split]))
                            scope.AddCandidate(type.Name, type);

                        break;
                    }
                }
            }
        }

        /// <summary>The modules a wildcard import brought into scope, whose members are reachable unqualified.</summary>
        private IReadOnlyList<ModuleSymbol> ImportedBy(ModuleSymbol module)
            => _importedModules.TryGetValue(module.Path, out var imported)
                ? imported
                : (IReadOnlyList<ModuleSymbol>)Array.Empty<ModuleSymbol>();

        private bool TryGetModuleSymbol(string modulePath, out ModuleSymbol module)
            => _modules.TryGetValue(modulePath, out module!)
                || _compilation.Importer.TryGetModuleSymbol(modulePath, out module!);
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

            // Again, for the type parameters a method's own signature declared: those did not exist
            // when the first run went through, and a bound nobody resolved is a bound a body cannot
            // call anything through.
            EnterContext(null, null);
            BindConstraints();

            // After every type has its members, because the question is about a base class's, and
            // nothing says a base is bound before what extends it.
            foreach (var binding in _declared)
            {
                CheckSealedOverrides(binding);
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
        /// <c>BuildInterfaceDispatch</c> needs a vtable slot to exist for it at all — which only a
        /// <c>virtual</c>/<c>override</c> or another <c>abstract</c> declaration creates, since a
        /// plain, non-overriding method never enters the vtable and so can never satisfy one, even
        /// where its name and parameters happen to match. <c>VerifyConcrete</c> then refuses to let
        /// a concrete class leave the slot it found still abstract.
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
                    $"'{symbol.Name}' does not implement '{contract.Name}.{required.Name}'; implement it with 'override', "
                        + $"or declare it 'abstract' on '{symbol.Name}' to leave it for a subclass.");
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
        /// The nearest member of <paramref name="type"/> or of a class it extends occupying a
        /// vtable slot (<c>Virtual</c> or <c>Abstract</c> dispatch — never <c>Direct</c>, which never
        /// enters the vtable and so cannot answer for one) matching by name and parameter count.
        /// Closest to <paramref name="type"/> wins, the same as the runtime's own vtable: an override
        /// replaces the slot in place, so a derived class's answer is this walk's first match.
        /// </summary>
        private static MethodSymbol? FindMember(NamedTypeSymbol type, string name, int arity)
        {
            for (var walk = type; walk is not null; walk = walk.BaseType)
            {
                foreach (var member in walk.Members)
                {
                    if (member is MethodSymbol { Dispatch: not MethodDispatch.Direct } candidate
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

            symbol.Interfaces = interfaces;

            if (baseClass is not null && !CreatesCycle(symbol, baseClass, binding))
                symbol.BaseType = baseClass;
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
            // §2.9: exactly one field, declared `let`. Two would leave nothing to erase to.
            FieldSymbol? wrapped = null;
            int instanceFields = 0;

            foreach (var member in members)
            {
                if (member is FieldSymbol field && !field.IsStatic)
                {
                    instanceFields++;
                    wrapped ??= field;
                }
            }

            if (instanceFields != 1 || letFields != 1)
            {
                Report(SurtrDiagnosticCode.InvalidValueClass, binding, binding.Syntax.Span,
                    $"A value class wraps exactly one 'let' field; '{binding.Symbol.Name}' declares {instanceFields}.");

                return;
            }

            binding.Symbol.UnderlyingType = wrapped!.Type;
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
                    }
                }
            }

            module.Fields = fields;
            module.Properties = properties;
            module.Methods = methods;
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
            method.ReturnType = _resolver.Resolve(syntax.ReturnType, scope, binding.SourceName);
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
            method.ReturnType = _resolver.Resolve(syntax.ReturnType, scope, sourceName);
            method.Parameters = BindParameters(syntax.Parameters, method, scope, sourceName);
            RecordBody(method, syntax.Body, scope, owner, containingType: null, sourceName);
            RecordAttributes(method, syntax.Attributes, scope, sourceName);
            return method;
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
    }
}
