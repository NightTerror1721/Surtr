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

        private readonly List<BodyBinding> _bodies = new List<BodyBinding>();

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
        private readonly Dictionary<NamedTypeSymbol, TypeBinding> _typeBindings = new Dictionary<NamedTypeSymbol, TypeBinding>();
        private readonly List<TypeBinding> _declared = new List<TypeBinding>();
        private readonly List<ConstraintBinding> _constraints = new List<ConstraintBinding>();
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
            _resolver = new TypeResolver(_factory, compilation.Importer, _diagnostics);
            Conversions = new Conversions(_factory);
            MemberLookup = new MemberLookup(_factory);
            OverloadResolution = new OverloadResolution(Conversions);
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

        /// <summary>Picking the member a call site means.</summary>
        public OverloadResolution OverloadResolution { get; }

        /// <summary>The bodies bound so far, by the method each belongs to.</summary>
        public IReadOnlyDictionary<MethodSymbol, BoundStatement> Bodies => _bound;

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

        private bool TryGetModuleSymbol(string modulePath, out ModuleSymbol module)
            => _modules.TryGetValue(modulePath, out module!)
                || _compilation.Importer.TryGetModuleSymbol(modulePath, out module!);
        #endregion

        #region Phase 2 - hierarchy and members
        private void MemberPhase()
        {
            foreach (var binding in _declared)
                BindHierarchy(binding);

            BindConstraints();

            foreach (var binding in _declared)
                BindMembers(binding);

            foreach (var module in _modules.Values)
                BindModuleMembers(module);

            // Last, because a bound like `<T : IComparable<T>>` names a type whose own hierarchy is
            // still being resolved while the signatures above are bound.
            _resolver.VerifyConstraints(Conversions);
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
            var signatures = new SignatureSet(_factory, _diagnostics, binding.SourceName);
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

                        var bound = BindProperty(property, symbol, binding, isInterface);
                        if (!names.Add(property.Name))
                            Duplicate(binding, property.Span, property.Name);

                        members.Add(bound);
                        if (bound.Getter is not null)
                            members.Add(bound.Getter);
                        if (bound.Setter is not null)
                            members.Add(bound.Setter);

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

                        var bound = BindMethod(method, symbol, binding, isInterface);
                        signatures.Add(bound, method.Span);
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
                        signatures.Add(bound, constructor.Span);
                        members.Add(bound);
                        continue;
                    }

                    case OperatorDeclarationSyntax op:
                    {
                        var bound = BindOperator(op, symbol, binding);
                        signatures.Add(bound, op.Span);
                        members.Add(bound);
                        continue;
                    }
                }
            }

            foreach (var enumCase in syntax.EnumCases)
            {
                members.Add(new FieldSymbol(enumCase.Name, symbol, symbol)
                {
                    IsStatic = true,
                    IsReadOnly = true,
                    Accessibility = Accessibility.Public,
                });

                if (!names.Add(enumCase.Name))
                    Duplicate(binding, enumCase.Span, enumCase.Name);
            }

            if (syntax.Kind == TypeDeclarationKind.ValueClass)
                BindValueClassField(binding, members, letFields);

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

        private void BindModuleMembers(ModuleSymbol module)
        {
            var scope = _moduleScopes[module.Path];
            var sourceModule = _compilation.Modules[module.Path];

            var fields = new List<FieldSymbol>();
            var properties = new List<PropertySymbol>();
            var methods = new List<MethodSymbol>();
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var unit in sourceModule.Units)
            {
                var signatures = new SignatureSet(_factory, _diagnostics, unit.File.Path);

                foreach (var declaration in Flatten(unit.Syntax.Declarations, unit.File.Path))
                {
                    switch (declaration)
                    {
                        case FieldDeclarationSyntax field:
                            CheckBuildConstant(field.Name, unit.File.Path, field.Span);
                            if (!names.Add(field.Name))
                                ReportAt(unit.File.Path, field.Span, SurtrDiagnosticCode.DuplicateDeclaration,
                                    $"'{field.Name}' is already declared in module '{module.Path}'.");

                            fields.Add(BindModuleField(field, module, scope, unit.File.Path));
                            continue;

                        case PropertyDeclarationSyntax property:
                            CheckBuildConstant(property.Name, unit.File.Path, property.Span);
                            if (!names.Add(property.Name))
                                ReportAt(unit.File.Path, property.Span, SurtrDiagnosticCode.DuplicateDeclaration,
                                    $"'{property.Name}' is already declared in module '{module.Path}'.");

                            properties.Add(BindModuleProperty(property, module, scope, unit.File.Path));
                            continue;

                        case MethodDeclarationSyntax method:
                            CheckBuildConstant(method.Name, unit.File.Path, method.Span);
                            var bound = BindModuleMethod(method, module, scope, unit.File.Path);
                            signatures.Add(bound, method.Span);
                            methods.Add(bound);
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
            // Const functions first, and not for tidiness: §7.2 folds a call by running the callee's
            // emitted body, so a `const if` inside an ordinary body can only be answered once every
            // const fun has one. Binding them in two rounds is the whole of that ordering.
            foreach (var body in _bodies)
            {
                if (body.Method.IsConst)
                    BindOne(body);
            }

            PrepareConstFolding();

            foreach (var body in _bodies)
                BindOne(body);

            VerifyConstantDeclarations();
            return _bound;
        }

        private void BindOne(BodyBinding body)
        {
            if (_bound.ContainsKey(body.Method))
                return;

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
                body.Method);

            var bound = binder.BindBody(body.Syntax);
            _bound.Add(body.Method, bound);

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
            => new FieldSymbol(syntax.Name, owner, ResolveOrInfer(syntax.Type, binding.Scope, binding.SourceName))
            {
                IsStatic = syntax.IsStatic,
                IsReadOnly = !syntax.IsMutable,
                Accessibility = Translate(syntax.Visibility, Accessibility.Private),
            };

        private FieldSymbol BindModuleField(FieldDeclarationSyntax syntax, ModuleSymbol owner, Scope scope, string sourceName)
            => new FieldSymbol(syntax.Name, owner, ResolveOrInfer(syntax.Type, scope, sourceName))
            {
                IsStatic = true,
                IsReadOnly = !syntax.IsMutable,
                Accessibility = Translate(syntax.Visibility, Accessibility.Internal),
            };

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

            WireAccessors(property, syntax.Accessors, owner, syntax.Dispatch, isInterface, accessibility);
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

            WireAccessors(property, syntax.Accessors, owner, DispatchModifier.None, isInterface: false, property.Accessibility);
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
            bool isInterface,
            Accessibility accessibility)
        {
            bool hasGetter = accessors.Count == 0;
            bool hasSetter = false;

            for (int i = 0; i < accessors.Count; i++)
            {
                if (accessors[i].IsGetter)
                    hasGetter = true;
                else
                    hasSetter = true;
            }

            if (hasGetter)
            {
                property.Getter = new MethodSymbol(MemberNames.Getter(property.Name), owner, property.Type)
                {
                    IsStatic = property.IsStatic,
                    Accessibility = accessibility,
                    Role = MethodRole.PropertyGetter,
                    Dispatch = TranslateDispatch(dispatch, isInterface),
                    IsOverride = dispatch == DispatchModifier.Override,
                };
            }

            if (hasSetter)
            {
                var setter = new MethodSymbol(MemberNames.Setter(property.Name), owner, _factory.Void)
                {
                    IsStatic = property.IsStatic,
                    Accessibility = accessibility,
                    Role = MethodRole.PropertySetter,
                    Dispatch = TranslateDispatch(dispatch, isInterface),
                    IsOverride = dispatch == DispatchModifier.Override,
                };

                setter.Parameters = new[] { new ParameterSymbol("value", property.Type, 0, setter) };
                property.Setter = setter;
            }
        }

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
            return method;
        }

        private MethodSymbol BindOperator(OperatorDeclarationSyntax syntax, NamedTypeSymbol owner, TypeBinding binding)
        {
            bool isConversion = syntax.Operator == TokenType.KeywordAs;

            string name = OperatorNames.TryGetSymbol(syntax.Operator, out _)
                ? OperatorNames.For(syntax.Operator, syntax.Parameters.Count)
                : OperatorNames.Prefix + "?";

            // §5.6: an overload is always public and always static, and can be nothing else.
            var method = new MethodSymbol(name, owner, _factory.ErrorType)
            {
                IsStatic = true,
                Accessibility = Accessibility.Public,
                Role = MethodRole.Operator,
                IsConversion = isConversion,
            };

            method.ReturnType = _resolver.Resolve(syntax.ReturnType, binding.Scope, binding.SourceName);
            method.Parameters = BindParameters(syntax.Parameters, method, binding.Scope, binding.SourceName);
            RecordBody(method, syntax.Body, binding.Scope, binding.Module, owner, binding.SourceName);
            return method;
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
        private void BindConstraints()
        {
            foreach (var binding in _constraints)
            {
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
                parameters[i] = new ParameterSymbol(
                    syntax[i].Name,
                    ResolveOrInfer(syntax[i].Type, scope, sourceName),
                    i,
                    owner)
                {
                    HasDefaultValue = syntax[i].DefaultValue is not null,
                    IsVararg = syntax[i].IsVarargs,
                };
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
