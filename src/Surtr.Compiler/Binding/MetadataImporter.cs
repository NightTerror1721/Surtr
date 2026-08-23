#nullable enable

using Surtr.Compiler.Binding.Symbols;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Turns already-built runtime metadata into symbols the binder can use: the built-ins, a
    /// module compiled earlier, and the types the host declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the inverse of <c>CodeGen.DescriptorEmitter</c> and the <b>only</b> place a
    /// descriptor is read. That is a boundary, not a shortcut: metadata is the form a dependency
    /// arrives in, so something has to decode it, and confining that to one type is what keeps
    /// every other part of <c>Binding</c> working in symbols. Nothing here produces a descriptor.
    /// </para>
    /// <para>
    /// An imported symbol is indistinguishable from a source one once built, which is the point —
    /// the binder must not care whether a type came from a file it just parsed or from a
    /// <c>.surtrc</c>. What is unavoidably lost is what erasure already threw away: a
    /// <c>value class</c> arrives as the class it boxes into, an alias never existed, and a
    /// constructed generic's arguments are read from the descriptor rather than from a declaration.
    /// </para>
    /// <para>
    /// Import is lazy per type and cached by metadata identity, and a type's shell is cached
    /// <em>before</em> its members are read, so a class whose method mentions its own type does not
    /// recurse forever.
    /// </para>
    /// </remarks>
    public sealed class MetadataImporter
    {
        private readonly TypeSymbolFactory _factory;

        private readonly Dictionary<string, SurtrModule> _modules =
            new Dictionary<string, SurtrModule>(StringComparer.Ordinal);

        private readonly Dictionary<string, SurtrTypeInfo> _hostTypes =
            new Dictionary<string, SurtrTypeInfo>(StringComparer.Ordinal);

        private readonly Dictionary<SurtrTypeInfo, NamedTypeSymbol> _types =
            new Dictionary<SurtrTypeInfo, NamedTypeSymbol>();

        private readonly Dictionary<string, ModuleSymbol> _moduleSymbols =
            new Dictionary<string, ModuleSymbol>(StringComparer.Ordinal);

        /// <summary>
        /// The module paths whose <see cref="ModuleSymbol"/> has had its surface imported. Kept
        /// apart from <see cref="_moduleSymbols"/> because that dictionary also holds
        /// containing-module shells, which are empty by design until this importer - or nothing -
        /// fills them.
        /// </summary>
        private readonly Dictionary<string, ModuleSymbol> _completedModules =
            new Dictionary<string, ModuleSymbol>(StringComparer.Ordinal);

        /// <summary>Creates an importer that interns everything it builds through one factory.</summary>
        public MetadataImporter(TypeSymbolFactory factory)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));

            // The built-in module is process-wide and always reachable, so nothing has to
            // reference it explicitly for `string` or `IIterable<T>` to resolve.
            SurtrBuiltIns.EnsureBuilt();
            AddModule(SurtrBuiltIns.Module);
        }

        /// <summary>The factory every imported type is interned through.</summary>
        public TypeSymbolFactory Factory => _factory;

        /// <summary>
        /// The one class behind every array parameterisation, which is where an array's members
        /// live.
        /// </summary>
        /// <remarks>
        /// The binder's <c>int[]</c> is an <see cref="ArrayTypeSymbol"/> and carries no members of
        /// its own, deliberately: <c>AI</c> and <c>AS</c> resolve to one
        /// <c>SurtrBuiltIns.Array</c>, so one symbol has to hold what they share.
        /// <see cref="MemberLookup"/> pairs the two by constructing this with the element type.
        /// </remarks>
        public NamedTypeSymbol ArrayType => _arrayType ??= Import(SurtrBuiltIns.Array);

        /// <summary>The one class behind every dictionary parameterisation.</summary>
        public NamedTypeSymbol DictionaryType => _dictionaryType ??= Import(SurtrBuiltIns.Dictionary);

        /// <summary>
        /// The class behind every tuple, which declares no parameters — a tuple's element type
        /// varies per index, so no fixed parameter could name what it holds.
        /// </summary>
        public NamedTypeSymbol TupleType => _tupleType ??= Import(SurtrBuiltIns.Tuple);

        /// <summary>The class behind every closure, which declares no parameters for the same reason.</summary>
        public NamedTypeSymbol ClosureType => _closureType ??= Import(SurtrBuiltIns.Closure);

        private NamedTypeSymbol? _arrayType;
        private NamedTypeSymbol? _dictionaryType;
        private NamedTypeSymbol? _tupleType;
        private NamedTypeSymbol? _closureType;

        /// <summary>Makes a module's types available to resolve against.</summary>
        public void AddModule(SurtrModule module)
        {
            if (module is null)
                throw new ArgumentNullException(nameof(module));

            _modules[module.Path] = module;
        }

        /// <summary>Makes a host-declared type available to resolve against.</summary>
        public void AddHostType(SurtrTypeInfo type)
        {
            if (type is null)
                throw new ArgumentNullException(nameof(type));

            if (type.SelfReference.TryGetFullName(out string fullName))
                _hostTypes[fullName] = type;
        }

        /// <summary>Imports a whole module: its types and its module-level members.</summary>
        /// <remarks>
        /// Completing is keyed by path and idempotent, and it has to be: a containing-module shell
        /// (<see cref="ModuleSymbolFor"/>) may already sit under this path, created while some
        /// other module's metadata was being read - a base class, an interface a nested class
        /// implements, anything whose descriptor names this module. That shell is deliberately
        /// empty, and returning it as if it were an imported surface silently stripped every
        /// built-in name out of the global scope (§13's implicit <c>surtr</c> import reads exactly
        /// this method). Completion fills the shell in place, so one path keeps one
        /// <see cref="ModuleSymbol"/> instance and every type interned against it gains the
        /// members.
        /// </remarks>
        public ModuleSymbol ImportModule(SurtrModule module)
        {
            if (module is null)
                throw new ArgumentNullException(nameof(module));

            AddModule(module);

            if (_completedModules.TryGetValue(module.Path, out var completed))
                return completed;

            var symbol = ModuleSymbolFor(module.Path);
            _completedModules.Add(module.Path, symbol);

            var types = new List<NamedTypeSymbol>();
            foreach (var type in module.Classes)
                types.Add(Import(type));

            foreach (var contract in module.Interfaces)
                types.Add(Import(contract));

            symbol.Types = types;
            symbol.Fields = ImportFields(module.Fields, containingSymbol: symbol, declaringType: null);
            symbol.Properties = ImportProperties(module.Properties, containingSymbol: symbol, declaringType: null);
            var methods = ImportMethods(module.Methods, containingSymbol: symbol, declaringType: null);
            symbol.Methods = methods;

            // An extension member travels as an ordinary module function whose receiver is its
            // first parameter; the image's `IsExtension` mark is what lets a later compiler tell
            // it apart again, so the imported module resolves `obj.member(...)` through the
            // extension path (§15) instead of treating it as a bare function.
            var extensions = new List<MethodSymbol>();
            for (int i = 0; i < methods.Count; i++)
            {
                if (methods[i].ImportedFrom is SurtrMethodInfo imported && imported.IsExtension)
                    extensions.Add(methods[i]);
            }

            symbol.ExtensionMethods = extensions;

            return symbol;
        }

        /// <summary>Imports one class or interface.</summary>
        public NamedTypeSymbol Import(SurtrTypeInfo type)
        {
            if (_types.TryGetValue(type, out var cached))
                return cached;

            // A built-in whose whole descriptor is one symbol already has a canonical symbol in the
            // factory. Declaring a second would give `int` two symbols that compare unequal - and
            // since §13 puts the whole `surtr` module in scope implicitly, the two would then be
            // reported as an ambiguous name at every use.
            if (TryImportScalarBuiltIn(type, out var wellKnown))
            {
                // Cached before its members are read, for the same reason every other type is: the
                // signature of `int.toString()` mentions `string`, which mentions `int` again.
                _types.Add(type, wellKnown);
                Complete(wellKnown, type);
                return wellKnown;
            }

            var symbol = CreateShell(type);

            // Cached before members are read: a class whose method mentions its own type would
            // otherwise import it again, forever.
            _types.Add(type, symbol);
            Complete(symbol, type);
            return symbol;
        }

        /// <summary>
        /// Imports the type a descriptor names.
        /// </summary>
        /// <param name="reference">The descriptor.</param>
        /// <param name="declaringType">
        /// Whose generic parameters a <c>G&lt;n&gt;</c> or <c>H&lt;n&gt;</c> in the descriptor
        /// refers to - the type for <c>G</c>, the method for <c>H</c>. Without it, one resolves to
        /// <c>unknown</c>, which is the same representation and all that is left to say about it.
        /// </param>
        public TypeSymbol Import(SurtrClassReference reference, Symbol? declaringType = null)
        {
            if (!reference.IsValid)
                return _factory.ErrorType;

            if (reference.IsNullablePrimitive)
                return Import(reference.GetUnderlyingPrimitive(), declaringType).Nullable;

            switch (reference.TypeCode)
            {
                case SurtrValueTypeCode.Integer: return _factory.Int;
                case SurtrValueTypeCode.Float: return _factory.Float;
                case SurtrValueTypeCode.Boolean: return _factory.Bool;
                case SurtrValueTypeCode.Character: return _factory.Char;
                case SurtrValueTypeCode.String: return _factory.String;
                case SurtrValueTypeCode.Range: return _factory.Range;
                case SurtrValueTypeCode.Void: return _factory.Void;

                case SurtrValueTypeCode.Array:
                    return _factory.Array(Import(reference.GetArrayElementType(), declaringType));

                case SurtrValueTypeCode.Dictionary:
                    return _factory.Dictionary(
                        Import(reference.GetDictionaryKeyType(), declaringType),
                        Import(reference.GetDictionaryValueType(), declaringType));

                case SurtrValueTypeCode.Tuple:
                    return _factory.Tuple(ImportAll(reference.GetTupleElementTypes(), declaringType));

                case SurtrValueTypeCode.Closure:
                    return _factory.Closure(
                        ImportAll(reference.GetClosureParameterTypes(), declaringType),
                        Import(reference.GetClosureReturnType(), declaringType));

                case SurtrValueTypeCode.Erased:
                {
                    // `G<n>` and `E` are one representation, and the descriptor is the only thing
                    // that still tells them apart - which is exactly what it was given the form for.
                    // A method's `H<n>` is its own, resolved against the method rather than the type.
                    if (reference.TryGetGenericParameterIndex(out int ordinal))
                    {
                        var owner = declaringType is MethodSymbol declaringMethod
                            ? declaringMethod.ContainingType
                            : declaringType as NamedTypeSymbol;

                        if (owner is not null && ordinal < owner.TypeParameters.Count)
                            return owner.TypeParameters[ordinal];
                    }

                    if (reference.TryGetMethodGenericParameterIndex(out int methodOrdinal)
                        && declaringType is MethodSymbol method
                        && methodOrdinal < method.TypeParameters.Count)
                    {
                        return method.TypeParameters[methodOrdinal];
                    }

                    return _factory.Unknown;
                }

                case SurtrValueTypeCode.Object:
                case SurtrValueTypeCode.Native:
                    return ImportNamed(reference, declaringType);

                default:
                    return _factory.ErrorType;
            }
        }

        /// <summary>Whether a module of this path has been made available to resolve against.</summary>
        public bool KnowsModule(string modulePath) => _modules.ContainsKey(modulePath);

        /// <summary>The symbol for a module that has been imported, if it has been.</summary>
        public bool TryGetModuleSymbol(string modulePath, out ModuleSymbol module)
            => _moduleSymbols.TryGetValue(modulePath, out module!);

        /// <summary>
        /// The built module a path names, for a module this compilation references rather than
        /// declares.
        /// </summary>
        /// <remarks>
        /// A cross-module call names an entry in the <em>callee's</em> table and reaches it through
        /// the caller's module reference table, so emitting one needs the callee's
        /// <c>SurtrModule</c> — which the symbol side does not carry, because a module-level member
        /// records no owner. This is where that is recovered.
        /// </remarks>
        public bool TryGetBuiltModule(string modulePath, out SurtrModule module)
            => _modules.TryGetValue(modulePath, out module!);

        /// <summary>Finds the metadata a full name refers to, across every module and host type known.</summary>
        public bool TryResolve(string fullName, out SurtrTypeInfo type)
        {
            if (_hostTypes.TryGetValue(fullName, out type!))
                return true;

            SurtrClassReference.TrySplitFullName(fullName, out string modulePath, out string typePath);

            if (!_modules.TryGetValue(modulePath, out var module))
            {
                type = null!;
                return false;
            }

            return TryResolveIn(module, typePath, out type);
        }

        private static bool TryResolveIn(SurtrModule module, string typePath, out SurtrTypeInfo type)
        {
            int separator = typePath.IndexOf(SurtrClassReference.NameSeparator);
            string head = separator < 0 ? typePath : typePath.Substring(0, separator);

            if (module.TryGetClass(head, out var declared))
            {
                return separator < 0
                    ? Found(declared, out type)
                    : TryResolveNested(declared, typePath.Substring(separator + 1), out type);
            }

            if (separator < 0 && module.TryGetInterface(head, out var contract))
                return Found(contract, out type);

            type = null!;
            return false;
        }

        private static bool TryResolveNested(SurtrClass container, string typePath, out SurtrTypeInfo type)
        {
            int separator = typePath.IndexOf(SurtrClassReference.NameSeparator);
            string head = separator < 0 ? typePath : typePath.Substring(0, separator);

            if (container.TryGetNestedClass(head, out var nested))
            {
                return separator < 0
                    ? Found(nested, out type)
                    : TryResolveNested(nested, typePath.Substring(separator + 1), out type);
            }

            if (separator < 0 && container.TryGetNestedInterface(head, out var contract))
                return Found(contract, out type);

            type = null!;
            return false;
        }

        private static bool Found(SurtrTypeInfo found, out SurtrTypeInfo type)
        {
            type = found;
            return true;
        }

        private TypeSymbol ImportNamed(SurtrClassReference reference, Symbol? declaringType)
        {
            if (!reference.TryGetFullName(out string fullName))
                return _factory.ErrorType;

            if (!TryResolve(fullName, out var metadata))
                return _factory.Error(reference.ToDisplayString());

            var definition = Import(metadata);

            var arguments = reference.GetTypeArguments();
            if (arguments.Length == 0 || arguments.Length != definition.Arity)
                return definition;

            return definition.Construct(ImportAll(arguments, declaringType));
        }

        private TypeSymbol[] ImportAll(SurtrClassReference[] references, Symbol? declaringType)
        {
            var imported = new TypeSymbol[references.Length];
            for (int i = 0; i < references.Length; i++)
                imported[i] = Import(references[i], declaringType);

            return imported;
        }

        /// <summary>
        /// Maps a built-in class whose descriptor is a single symbol onto the factory's own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only the scalar ones. <c>array</c>, <c>dict</c>, <c>tuple</c> and <c>closure</c> carry a
        /// bare family symbol as their self reference — deliberately not a well-formed descriptor,
        /// since one class covers every parameterisation — so they import as ordinary named types
        /// and keep somewhere for their members to live.
        /// </para>
        /// <para>
        /// The symbol comes back from the factory rather than being declared here because
        /// <c>int</c> has to be <em>one</em> symbol: a second would compare unequal to the first,
        /// and since §13 puts the whole <c>surtr</c> module in scope implicitly, the two would then
        /// be reported as an ambiguous name at every use. Its members are still imported onto it —
        /// everything in Surtr is an object, so <c>string</c> without <c>length</c> would be a hole
        /// in the language rather than an optimisation.
        /// </para>
        /// </remarks>
        private bool TryImportScalarBuiltIn(SurtrTypeInfo type, out NamedTypeSymbol wellKnown)
        {
            string descriptor = type.SelfReference.Descriptor;

            if (descriptor.Length == 1)
            {
                switch (descriptor[0])
                {
                    case SurtrClassReference.SymbolInteger: wellKnown = _factory.Int; return true;
                    case SurtrClassReference.SymbolFloat: wellKnown = _factory.Float; return true;
                    case SurtrClassReference.SymbolBoolean: wellKnown = _factory.Bool; return true;
                    case SurtrClassReference.SymbolCharacter: wellKnown = _factory.Char; return true;
                    case SurtrClassReference.SymbolString: wellKnown = _factory.String; return true;
                    case SurtrClassReference.SymbolRange: wellKnown = _factory.Range; return true;
                    case SurtrClassReference.SymbolErased: wellKnown = _factory.Unknown; return true;
                    case SurtrClassReference.SymbolVoid: wellKnown = _factory.Void; return true;
                }
            }

            wellKnown = null!;
            return false;
        }

        private NamedTypeSymbol CreateShell(SurtrTypeInfo type)
        {
            var kind = type switch
            {
                SurtrInterface => TypeSymbolKind.Interface,
                SurtrClass { IsEnum: true } => TypeSymbolKind.Enum,
                SurtrClass { TypeCode: SurtrValueTypeCode.Native } => TypeSymbolKind.Native,
                _ => TypeSymbolKind.Class,
            };

            NamedTypeSymbol? containingType = null;
            ModuleSymbol? containingModule = null;

            if (type.DeclaringType?.ResolvedType is SurtrTypeInfo declaring)
            {
                containingType = Import(declaring);
                containingModule = containingType.ContainingModule;
            }
            else if (type.SelfReference.TryGetFullName(out string fullName))
            {
                SurtrClassReference.TrySplitFullName(fullName, out string modulePath, out _);
                containingModule = ModuleSymbolFor(modulePath);
            }
            else
            {
                // `array`, `dict`, `tuple` and `closure` name themselves with a bare family symbol,
                // which carries no module path — deliberately, since one class covers every
                // parameterisation. They are built-ins, so that is the module they belong to, and
                // saying so is what keeps their members reachable through the standard-library rule.
                containingModule = ModuleSymbolFor(SurtrBuiltIns.ModulePath);
            }

            // The metadata name carries the arity; the symbol keeps the two apart, so strip it back
            // off and let the type parameters put it back.
            string name = StripArity(type.Name);
            var symbol = _factory.DeclareType(name, kind, containingModule, containingType);
            symbol.Accessibility = Translate(type.Visibility);

            var declared = type.GenericParameters;
            if (declared.Length > 0)
            {
                var parameters = new TypeParameterSymbol[declared.Length];
                for (int i = 0; i < declared.Length; i++)
                    parameters[i] = _factory.DeclareTypeParameter(declared[i], symbol, i);

                symbol.SetTypeParameters(parameters);
            }

            return symbol;
        }

        private void Complete(NamedTypeSymbol symbol, SurtrTypeInfo type)
        {
            var interfaces = new List<NamedTypeSymbol>();

            if (type is SurtrClass declared)
            {
                symbol.IsAbstract = declared.IsAbstract;
                symbol.IsSealed = declared.IsSealed;

                if (declared.BaseType is SurtrTypeHandle baseHandle
                    && ImportHandle(baseHandle, symbol) is NamedTypeSymbol baseType)
                {
                    symbol.BaseType = baseType;
                }

                foreach (var handle in declared.DeclaredInterfaceHandles)
                {
                    if (ImportHandle(handle, symbol) is NamedTypeSymbol contract)
                        interfaces.Add(contract);
                }

                symbol.Members = ImportClassMembers(declared, symbol);
            }
            else if (type is SurtrInterface contractType)
            {
                symbol.IsAbstract = true;

                foreach (var handle in contractType.DeclaredExtendedInterfaceHandles)
                {
                    if (ImportHandle(handle, symbol) is NamedTypeSymbol extended)
                        interfaces.Add(extended);
                }

                symbol.Members = ImportInterfaceMembers(contractType, symbol);
            }

            symbol.Interfaces = interfaces;

            ImportConstraints(symbol, type);
        }

        /// <summary>
        /// Rebuilds the bounds a generic type declared, which erasure used to drop at emit.
        /// </summary>
        /// <remarks>
        /// Each bound travels as a descriptor, so importing one is the same
        /// <see cref="Import(SurtrClassReference, Symbol?)"/> call any other type
        /// position takes - which is also what keeps a bound naming the
        /// type's own parameter (<c>IComparable&lt;T&gt;</c> → <c>G0</c>) resolving to the right
        /// <see cref="TypeParameterSymbol"/> rather than to <c>unknown</c>.
        /// </remarks>
        private void ImportConstraints(NamedTypeSymbol symbol, SurtrTypeInfo type)
        {
            var written = type.GenericConstraints;
            if (written.Length == 0)
                return;

            var parameters = symbol.TypeParameters;
            for (int i = 0; i < parameters.Count; i++)
            {
                var bounds = written[i];
                if (bounds.Length == 0)
                    continue;

                var imported = new TypeSymbol[bounds.Length];
                for (int b = 0; b < bounds.Length; b++)
                    imported[b] = Import(SurtrClassReference.FromDescriptor(bounds[b]), symbol);

                parameters[i].Constraints = imported;
            }
        }

        private TypeSymbol ImportHandle(SurtrTypeHandle handle, NamedTypeSymbol declaringType)
        {
            // A resolved handle is the metadata itself, which is both cheaper and exact. An
            // unresolved one still carries its descriptor, and this importer resolves names of its
            // own accord, so a module that was never loaded is not a dead end.
            if (handle.ResolvedType is SurtrTypeInfo resolved)
            {
                var definition = Import(resolved);

                var arguments = handle.Reference.GetTypeArguments();
                if (arguments.Length > 0 && arguments.Length == definition.Arity)
                    return definition.Construct(ImportAll(arguments, declaringType));

                return definition;
            }

            return Import(handle.Reference, declaringType);
        }

        private List<Symbol> ImportClassMembers(SurtrClass type, NamedTypeSymbol symbol)
        {
            var members = new List<Symbol>();

            members.AddRange(ImportFields(type.Fields, symbol, symbol));
            members.AddRange(ImportProperties(type.Properties, symbol, symbol));
            members.AddRange(ImportMethods(type.Methods, symbol, symbol));

            return members;
        }

        private List<Symbol> ImportInterfaceMembers(SurtrInterface type, NamedTypeSymbol symbol)
        {
            var members = new List<Symbol>();

            members.AddRange(ImportProperties(type.Properties, symbol, symbol));
            members.AddRange(ImportMethods(type.Methods, symbol, symbol));

            return members;
        }

        private List<FieldSymbol> ImportFields(
            IEnumerable<SurtrFieldInfo> fields,
            Symbol containingSymbol,
            NamedTypeSymbol? declaringType)
        {
            var imported = new List<FieldSymbol>();

            foreach (var field in fields)
            {
                imported.Add(new FieldSymbol(field.Name, containingSymbol, Import(field.FieldType.Reference, declaringType))
                {
                    IsStatic = field.IsStatic,
                    IsReadOnly = field.IsReadOnly,
                    Accessibility = Translate(field.Visibility),
                    ImportedFrom = field,
                });
            }

            return imported;
        }

        private List<PropertySymbol> ImportProperties(
            IEnumerable<SurtrPropertyInfo> properties,
            Symbol containingSymbol,
            NamedTypeSymbol? declaringType)
        {
            var imported = new List<PropertySymbol>();

            foreach (var property in properties)
            {
                var symbol = new PropertySymbol(
                    property.Name,
                    containingSymbol,
                    Import(property.PropertyType.Reference, declaringType))
                {
                    IsStatic = property.IsStatic,
                    Accessibility = Translate(property.Visibility),
                };

                if (property.Getter is SurtrMethodInfo getter)
                    symbol.Getter = ImportMethod(getter, containingSymbol, declaringType);

                if (property.Setter is SurtrMethodInfo setter)
                    symbol.Setter = ImportMethod(setter, containingSymbol, declaringType);

                imported.Add(symbol);
            }

            return imported;
        }

        private List<MethodSymbol> ImportMethods(
            IEnumerable<SurtrMethodInfo[]> overloadGroups,
            Symbol containingSymbol,
            NamedTypeSymbol? declaringType)
        {
            var imported = new List<MethodSymbol>();

            foreach (var overloads in overloadGroups)
            {
                for (int i = 0; i < overloads.Length; i++)
                    imported.Add(ImportMethod(overloads[i], containingSymbol, declaringType));
            }

            return imported;
        }

        private MethodSymbol ImportMethod(
            SurtrMethodInfo method,
            Symbol containingSymbol,
            NamedTypeSymbol? declaringType)
        {
            // The method's own type parameters exist before its signature is imported, because a
            // parameter or return written `H<n>` must resolve to them. The placeholder return is
            // replaced once they exist.
            var symbol = new MethodSymbol(
                method.Name,
                containingSymbol,
                _factory.Void)
            {
                IsStatic = method.IsStatic,
                Accessibility = Translate(method.Visibility),
                Dispatch = Translate(method.Dispatch),
                Role = Translate(method.Role),
                IsOverride = method.IsOverride,
                IsSealed = method.IsSealed,
                IsNative = method is SurtrNativeMethodInfo,
                IsSynthetic = SyntheticNames.IsSynthetic(method.Name),
                ImportedFrom = method,
            };

            // A class-nested extension (§15.2) carries its container so the accessibility check
            // runs against the class it was private to, the way the source binder records it.
            if (method.IsExtension && declaringType is not null)
                symbol.ExtensionDeclaringContainer = declaringType;

            var declared = method.GenericParameters;
            if (declared.Count > 0)
            {
                var parameters = new TypeParameterSymbol[declared.Count];
                for (int i = 0; i < declared.Count; i++)
                    parameters[i] = _factory.DeclareTypeParameter(declared[i], symbol, i);

                symbol.TypeParameters = parameters;
            }

            symbol.ReturnType = Import(method.ReturnType.Reference, symbol);

            var declaredParameters = method.Parameters;
            var parameters2 = new ParameterSymbol[declaredParameters.Length];
            for (int i = 0; i < declaredParameters.Length; i++)
            {
                var parameterType = Import(declaredParameters[i].ParameterType.Reference, symbol);

                // Metadata carries a varargs parameter by its element type, which is what §3.5
                // declares — so the array the body sees has to be rebuilt here, or an imported
                // signature would differ from the same one in source.
                if (declaredParameters[i].IsVarargs)
                    parameterType = _factory.Array(parameterType);

                parameters2[i] = new ParameterSymbol(
                    declaredParameters[i].Name,
                    parameterType,
                    i,
                    symbol)
                {
                    IsVararg = declaredParameters[i].IsVarargs,
                    HasDefaultValue = declaredParameters[i].HasDefault,
                    DefaultValue = Import(declaredParameters[i].DefaultValue),
                    DefaultValueFolded = declaredParameters[i].HasDefault,
                };
            }

            symbol.Parameters = parameters2;

            ImportMethodConstraints(symbol, method);
            return symbol;
        }

        /// <summary>
        /// Rebuilds the bounds a generic method declared, the way
        /// <see cref="ImportConstraints(NamedTypeSymbol, SurtrTypeInfo)"/> does for a type — one
        /// list per parameter, each bound imported against the method so an <c>H&lt;n&gt;</c>
        /// inside it resolves to the rebuilt parameter rather than to <c>unknown</c>.
        /// </summary>
        private void ImportMethodConstraints(MethodSymbol symbol, SurtrMethodInfo method)
        {
            var written = method.GenericConstraints;
            if (written.Count == 0)
                return;

            var parameters = symbol.TypeParameters;
            for (int i = 0; i < parameters.Count; i++)
            {
                var bounds = written[i];
                if (bounds.Length == 0)
                    continue;

                var imported = new TypeSymbol[bounds.Length];
                for (int b = 0; b < bounds.Length; b++)
                    imported[b] = Import(SurtrClassReference.FromDescriptor(bounds[b]), symbol);

                parameters[i].Constraints = imported;
            }
        }

        /// <summary>
        /// A parameter default, back in the compiler's own constant vocabulary (§3.5).
        /// </summary>
        /// <remarks>
        /// The integer widens to <see cref="long"/> here rather than staying a machine int, because
        /// that is the width every folded constant has in the binder — a literal read from source
        /// arrives the same way, so a call site cannot tell an imported default from a written one.
        /// </remarks>
        private static object? Import(SurtrConstant constant) => constant.Kind switch
        {
            SurtrConstantKind.Integer => (long)constant.Value.AsInt,
            SurtrConstantKind.Float => constant.Value.AsFloat,
            SurtrConstantKind.Boolean => constant.Value.AsBool,
            SurtrConstantKind.Character => constant.Value.AsChar,
            SurtrConstantKind.String => constant.Text,
            _ => null,
        };

        private ModuleSymbol ModuleSymbolFor(string modulePath)
        {
            if (_moduleSymbols.TryGetValue(modulePath, out var symbol))
                return symbol;

            symbol = new ModuleSymbol(modulePath);
            _moduleSymbols.Add(modulePath, symbol);
            return symbol;
        }

        private static string StripArity(string metadataName)
        {
            int marker = metadataName.LastIndexOf(SurtrClassReference.ArityMarker);
            return marker < 0 ? metadataName : metadataName.Substring(0, marker);
        }

        private static Accessibility Translate(SurtrVisibility visibility) => visibility switch
        {
            SurtrVisibility.Public => Accessibility.Public,
            SurtrVisibility.Protected => Accessibility.Protected,
            SurtrVisibility.Internal => Accessibility.Internal,
            _ => Accessibility.Private,
        };

        private static MethodDispatch Translate(SurtrMethodDispatch dispatch) => dispatch switch
        {
            SurtrMethodDispatch.Virtual => MethodDispatch.Virtual,
            SurtrMethodDispatch.Abstract => MethodDispatch.Abstract,
            _ => MethodDispatch.Direct,
        };

        private static MethodRole Translate(SurtrMethodRole role) => role switch
        {
            SurtrMethodRole.Constructor => MethodRole.Constructor,
            SurtrMethodRole.StaticInitializer => MethodRole.StaticInitializer,
            _ => MethodRole.Normal,
        };
    }
}
