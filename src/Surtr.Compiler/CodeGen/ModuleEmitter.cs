#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Bytecode.Image;
using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.CodeGen
{
    /// <summary>
    /// Turns a bound compilation into built modules, one <see cref="SurtrModuleBuilder"/> at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order is forced rather than chosen, at two levels. <b>Between</b> modules it is
    /// <see cref="SurtrCompilation.LoadOrder"/>, because a call into another module names an entry
    /// in <em>that</em> module's method table, which does not exist until it has been built —
    /// which is also why a dependency cycle is a hard error rather than something to resolve here.
    /// <b>Within</b> a module it is declare → emit → <see cref="SurtrModuleBuilder.Build"/>, because
    /// <see cref="SurtrBytecodeMethodInfo"/> snapshots its body's offset in its constructor, so no
    /// method metadata can exist until every body has been laid out.
    /// </para>
    /// <para>
    /// Three things the compiler synthesises land here rather than in the binder, because each is a
    /// decision about where code <em>runs</em> rather than about what a program means: an
    /// auto-property's backing field and its two trivial accessors; a static initializer carrying a
    /// type's static field initializers and, for an enum, its cases; and the instance field
    /// initializers, which are emitted at the top of every constructor — with a parameterless one
    /// synthesised when a class has initializers and declares none.
    /// </para>
    /// <para>
    /// Anything it cannot emit raises <see cref="SurtrEmitException"/>, which
    /// <see cref="TryEmit"/> turns into a diagnostic against the member that caused it. A
    /// compilation with errors is not emitted at all: the bound tree of a failed compilation holds
    /// error nodes, and emitting one would produce a module that runs.
    /// </para>
    /// </remarks>
    public sealed class ModuleEmitter
    {
        private readonly SurtrCompilation _compilation;
        private readonly Binder _binder;
        private readonly DescriptorEmitter _descriptors = new DescriptorEmitter();

        private readonly Dictionary<MethodSymbol, SurtrMethodInfo> _builtMethods =
            new Dictionary<MethodSymbol, SurtrMethodInfo>();

        private readonly Dictionary<MethodSymbol, SurtrModule> _methodOwners =
            new Dictionary<MethodSymbol, SurtrModule>();

        private readonly Dictionary<FieldSymbol, SurtrFieldInfo> _builtFields =
            new Dictionary<FieldSymbol, SurtrFieldInfo>();

        private readonly List<SurtrModule> _modules = new List<SurtrModule>();

        /// <summary>Creates an emitter over a bound compilation.</summary>
        public ModuleEmitter(SurtrCompilation compilation, Binder binder)
        {
            _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
            _binder = binder ?? throw new ArgumentNullException(nameof(binder));
        }

        /// <summary>The modules built so far, in load order.</summary>
        public IReadOnlyList<SurtrModule> Modules => _modules;

        /// <summary>
        /// Emits every module the compilation declares, in load order.
        /// </summary>
        /// <returns>Whether emission finished; a failure has already been reported.</returns>
        public bool TryEmit()
        {
            if (_emitted is bool already)
                return already;

            if (_compilation.HasErrors)
                return (bool)(_emitted = false);

            foreach (var source in _compilation.LoadOrder)
            {
                if (!_binder.Modules.TryGetValue(source.Path, out var symbol))
                    continue;

                try
                {
                    _modules.Add(EmitModule(symbol, source));
                }
                catch (Exception exception) when (exception is SurtrCompilerException or InvalidOperationException or ArgumentException)
                {
                    _compilation.Diagnostics.ReportError(
                        SurtrDiagnosticCode.NotLowered,
                        $"Module '{source.Path}' could not be emitted: {exception.Message}",
                        source.Units.Count > 0 ? source.Units[0].File.Path : source.Path,
                        span: default);

                    return (bool)(_emitted = false);
                }
            }

            return (bool)(_emitted = true);
        }

        // Emission is once per emitter: the modules it produced are already in Modules, and running
        // it again would build a second copy of every one of them.
        private bool? _emitted;

        /// <summary>Emits every module and writes each as a portable image.</summary>
        public IReadOnlyList<SurtrModuleImage> EmitImages()
        {
            if (!TryEmit())
                return Array.Empty<SurtrModuleImage>();

            var images = new SurtrModuleImage[_modules.Count];
            for (int i = 0; i < images.Length; i++)
                images[i] = SurtrModuleImage.FromModule(_modules[i]);

            return images;
        }

        #region One module
        private SurtrModule EmitModule(ModuleSymbol symbol, SurtrSourceModule source)
        {
            var builder = new SurtrModuleBuilder(symbol.Path);
            var context = new EmitContext(builder, _descriptors)
            {
                Bodies = _binder.Bodies,
                Folder = _binder.ConstFolder,
            };

            // Everything built earlier is nameable from here: a symbol resolves to real metadata,
            // and a module-level function also to the module whose table carries it.
            foreach (var pair in _builtMethods)
                context.Bind(pair.Key, pair.Value, _methodOwners.TryGetValue(pair.Key, out var owner) ? owner : null);

            foreach (var pair in _builtFields)
                context.Declare(pair.Key, pair.Value);

            var types = new List<TypeEmission>();

            foreach (var type in symbol.Types)
                DeclareType(context, builder, declaringClass: null, type, types);

            foreach (var emission in types)
                DeclareMembers(context, emission);

            DeclareModuleMembers(context, builder, symbol);

            foreach (var emission in types)
                EmitTypeBodies(context, emission);

            EmitModuleBodies(context, builder, symbol);

            var built = builder.Build();
            Record(context, symbol, types, built);
            return built;
        }

        /// <summary>One type being emitted, paired with the builder it landed on.</summary>
        private sealed class TypeEmission
        {
            public TypeEmission(NamedTypeSymbol symbol, SurtrClassBuilder? @class, SurtrInterfaceBuilder? contract)
            {
                Symbol = symbol;
                Class = @class;
                Contract = contract;
            }

            public NamedTypeSymbol Symbol { get; }

            public SurtrClassBuilder? Class { get; }

            public SurtrInterfaceBuilder? Contract { get; }

            /// <summary>The methods declared on it, in the order their symbols were walked.</summary>
            public List<(MethodSymbol Symbol, SurtrMethodBuilder Builder)> Methods { get; } =
                new List<(MethodSymbol, SurtrMethodBuilder)>();

            /// <summary>Its instance field initializers, which every constructor has to run.</summary>
            public List<BoundFieldInitializer> InstanceInitializers { get; } = new List<BoundFieldInitializer>();

            /// <summary>Its static field initializers and, for an enum, its cases.</summary>
            public List<BoundFieldInitializer> StaticInitializers { get; } = new List<BoundFieldInitializer>();

            /// <summary>The constructors declared on it, so the initializers can be prepended to each.</summary>
            public List<(MethodSymbol Symbol, SurtrMethodBuilder Builder)> Constructors { get; } =
                new List<(MethodSymbol, SurtrMethodBuilder)>();

            /// <summary>The constructor made for it because it declares none and has initializers.</summary>
            public SurtrMethodBuilder? SyntheticConstructor { get; set; }
        }
        #endregion

        #region Declaring types
        private void DeclareType(
            EmitContext context,
            SurtrModuleBuilder module,
            SurtrClassBuilder? declaringClass,
            NamedTypeSymbol symbol,
            List<TypeEmission> into)
        {
            TypeEmission emission;

            switch (symbol.TypeKind)
            {
                case TypeSymbolKind.Interface:
                {
                    var contract = declaringClass is null
                        ? module.DefineInterface(symbol.MetadataName, Visibility(symbol))
                        : declaringClass.DefineNestedInterface(symbol.MetadataName, Visibility(symbol));

                    Parameterise(contract.Interface, symbol);
                    context.Declare(symbol, contract);
                    emission = new TypeEmission(symbol, null, contract);
                    break;
                }

                case TypeSymbolKind.Enum:
                {
                    var @enum = declaringClass is null
                        ? module.DefineEnum(symbol.MetadataName, Visibility(symbol))
                        : declaringClass.DefineNestedEnum(symbol.MetadataName, Visibility(symbol));

                    context.Declare(symbol, @enum);
                    emission = new TypeEmission(symbol, @enum, null);
                    break;
                }

                default:
                {
                    // A value class and a singleton are both real classes here: §2.9 erases a value
                    // class only where its type is statically known, so the class it presents as
                    // where it cannot be erased still has to exist.
                    var baseType = symbol.BaseType is NamedTypeSymbol declared
                        ? _descriptors.Emit(declared)
                        : (SurtrClassReference?)null;

                    var @class = declaringClass is null
                        ? module.DefineClass(symbol.MetadataName, baseType, symbol.IsAbstract, Visibility(symbol), symbol.IsSealed)
                        : declaringClass.DefineNestedClass(symbol.MetadataName, baseType, symbol.IsAbstract, Visibility(symbol), symbol.IsSealed);

                    Parameterise(@class.Class, symbol);
                    context.Declare(symbol, @class);
                    emission = new TypeEmission(symbol, @class, null);
                    break;
                }
            }

            into.Add(emission);

            foreach (var nested in symbol.NestedTypes)
                DeclareType(context, module, emission.Class, nested, into);
        }

        /// <summary>Copies a declaration's type parameter names onto its metadata.</summary>
        /// <remarks>
        /// Only the names: everything else about a generic is erased (§8), and the arity is already
        /// in the name. What the runtime does with these is answer <c>G&lt;n&gt;</c>, which is why
        /// the order matters and the identity does not.
        /// </remarks>
        private static void Parameterise(SurtrTypeInfo type, NamedTypeSymbol symbol)
        {
            var parameters = symbol.TypeParameters;
            if (parameters.Count == 0)
                return;

            var names = new string[parameters.Count];
            for (int i = 0; i < names.Length; i++)
                names[i] = parameters[i].Name;

            switch (type)
            {
                case SurtrClass @class: @class.SetGenericParameters(names); return;
                case SurtrInterface contract: contract.SetGenericParameters(names); return;
            }
        }

        private void DeclareMembers(EmitContext context, TypeEmission emission)
        {
            var symbol = emission.Symbol;

            if (emission.Contract is SurtrInterfaceBuilder contract)
            {
                DeclareContractMembers(context, contract, symbol);
                return;
            }

            var @class = emission.Class!;

            if (symbol.Interfaces.Count > 0)
            {
                var implemented = new SurtrClassReference[symbol.Interfaces.Count];
                for (int i = 0; i < implemented.Length; i++)
                    implemented[i] = _descriptors.Emit(symbol.Interfaces[i]);

                @class.Implements(implemented);
            }

            foreach (var member in symbol.Members)
            {
                switch (member)
                {
                    case FieldSymbol field:
                        DeclareField(context, @class, symbol, field);
                        continue;

                    case PropertySymbol property:
                        DeclareProperty(context, emission, @class, property);
                        continue;

                    // The accessors are declared with their property, so they are skipped here or
                    // each would be declared twice.
                    case MethodSymbol { Role: MethodRole.PropertyGetter or MethodRole.PropertySetter }:
                        continue;

                    case MethodSymbol method:
                        DeclareMethod(context, emission, @class, method);
                        continue;
                }
            }

            SortInitializers(emission, symbol);

            // Declared here rather than while bodies are emitted, because a creation site in
            // another type may be emitted first and has to be able to name it.
            if (emission.Constructors.Count == 0 && emission.InstanceInitializers.Count > 0)
            {
                var synthesised = @class.DefineConstructor();
                emission.SyntheticConstructor = synthesised;
                context.DeclareDefaultConstructor(symbol, synthesised);
            }
        }

        private void DeclareContractMembers(EmitContext context, SurtrInterfaceBuilder contract, NamedTypeSymbol symbol)
        {
            if (symbol.Interfaces.Count > 0)
            {
                var extended = new SurtrClassReference[symbol.Interfaces.Count];
                for (int i = 0; i < extended.Length; i++)
                    extended[i] = _descriptors.Emit(symbol.Interfaces[i]);

                contract.Extends(extended);
            }

            foreach (var member in symbol.Members)
            {
                switch (member)
                {
                    case PropertySymbol property:
                    {
                        var declared = contract.DefineProperty(property.Name, _descriptors.Emit(property.Type));

                        if (property.Getter is MethodSymbol getter && declared.Getter is SurtrMethodInfo boundGetter)
                            context.Bind(getter, boundGetter);

                        if (property.Setter is MethodSymbol setter && declared.Setter is SurtrMethodInfo boundSetter)
                            context.Bind(setter, boundSetter);

                        continue;
                    }

                    case MethodSymbol { Role: MethodRole.PropertyGetter or MethodRole.PropertySetter }:
                        continue;

                    case MethodSymbol method:
                        context.Bind(
                            method,
                            contract.DefineMethod(
                                _descriptors.EmitMethodName(method),
                                _descriptors.Emit(method.ReturnType),
                                Parameters(context, method)));

                        continue;
                }
            }
        }

        private void DeclareField(EmitContext context, SurtrClassBuilder @class, NamedTypeSymbol owner, FieldSymbol field)
        {
            // An enum case is a static of the enum's own type, and the builder is what assigns the
            // ordinal an exhaustive switch indexes on.
            if (owner.TypeKind == TypeSymbolKind.Enum && field.IsStatic && ReferenceEquals(field.Type, owner))
            {
                context.Declare(field, @class.DefineEnumCase(field.Name, Visibility(field.Accessibility)).Field);
                return;
            }

            var descriptor = _descriptors.Emit(field.Type);

            context.Declare(
                field,
                field.IsStatic
                    ? @class.DefineStaticField(field.Name, descriptor, field.IsReadOnly, Visibility(field.Accessibility))
                    : @class.DefineField(field.Name, descriptor, field.IsReadOnly, Visibility(field.Accessibility)));
        }

        /// <summary>
        /// Declares a property: its metadata, its accessors, and the backing field an auto-property
        /// needs.
        /// </summary>
        /// <remarks>
        /// An accessor with no body written is one the compiler supplies, and what it reads and
        /// writes is a field nothing in the source declared — <c>$backing$health</c>, per §6.2. The
        /// accessors themselves are deliberately <em>not</em> in that scheme: <c>get_x</c> and
        /// <c>set_x</c> are the names <c>SurtrTypeLinker</c> looks for.
        /// </remarks>
        private void DeclareProperty(
            EmitContext context,
            TypeEmission emission,
            SurtrClassBuilder @class,
            PropertySymbol property)
        {
            var declared = @class.DefineProperty(
                property.Name, _descriptors.Emit(property.Type), property.IsStatic, Visibility(property.Accessibility));

            // Either accessor being bare is enough: §3.4 lets `{ get; set { ... } }` mix them, and
            // the bare half still needs somewhere to read from.
            bool auto =
                (property.Getter is not null && !_binder.Bodies.ContainsKey(property.Getter))
                || (property.Setter is not null && !_binder.Bodies.ContainsKey(property.Setter));

            if (auto)
            {
                var backing = new FieldSymbol(SyntheticNames.BackingField(property.Name), property.ContainingSymbol!, property.Type)
                {
                    IsStatic = property.IsStatic,
                    Accessibility = Accessibility.Private,
                    IsSynthetic = true,
                };

                property.BackingField = backing;
                DeclareField(context, @class, emission.Symbol, backing);
            }

            if (property.Getter is MethodSymbol getter)
            {
                var builder = declared.DefineGetter(Dispatch(getter), getter.IsOverride);
                context.Declare(getter, builder);
                emission.Methods.Add((getter, builder));
            }

            if (property.Setter is MethodSymbol setter)
            {
                var builder = declared.DefineSetter(Dispatch(setter), setter.IsOverride);
                context.Declare(setter, builder);
                emission.Methods.Add((setter, builder));
            }
        }

        private void DeclareMethod(EmitContext context, TypeEmission emission, SurtrClassBuilder @class, MethodSymbol method)
        {
            string name = _descriptors.EmitMethodName(method);
            var parameters = Parameters(context, method);

            if (method.Role == MethodRole.Constructor)
            {
                var constructor = @class.DefineConstructor(parameters, Visibility(method.Accessibility));
                context.Declare(method, constructor);
                emission.Methods.Add((method, constructor));
                emission.Constructors.Add((method, constructor));
                return;
            }

            if (method.Dispatch == MethodDispatch.Abstract)
            {
                context.Bind(
                    method,
                    @class.DefineAbstractMethod(name, _descriptors.Emit(method.ReturnType), parameters, Visibility(method.Accessibility)));

                return;
            }

            if (method.IsNative)
            {
                // A native member travels as a name: the address cannot, so every runtime that
                // loads the image publishes its own body under this link name (§10).
                context.Bind(
                    method,
                    @class.DeclareNativeMethod(
                        name,
                        _descriptors.Emit(method.ReturnType),
                        LinkName(method),
                        parameters,
                        method.IsStatic,
                        Dispatch(method),
                        method.IsOverride,
                        Visibility(method.Accessibility)));

                return;
            }

            var builder = @class.DefineMethod(
                name,
                _descriptors.Emit(method.ReturnType),
                parameters,
                method.IsStatic,
                Dispatch(method),
                OverridesABaseMethod(method),
                Visibility(method.Accessibility),
                method.IsSealed);

            context.Declare(method, builder);
            emission.Methods.Add((method, builder));
        }

        /// <summary>
        /// Whether <c>override</c> in the source really replaces a base class method.
        /// </summary>
        /// <remarks>
        /// It does not when the method implements an interface: §2.2 makes a contract a promise
        /// rather than an inheritance, and <c>SurtrTypeLinker</c> rejects an override with no base
        /// entry to replace. Both are written <c>override</c> in Surtr, so the difference is one the
        /// emitter has to settle rather than the syntax.
        /// </remarks>
        private static bool OverridesABaseMethod(MethodSymbol method)
        {
            if (!method.IsOverride || method.ContainingType is not NamedTypeSymbol owner)
                return false;

            for (var walk = owner.BaseType; walk is not null; walk = walk.BaseType)
            {
                foreach (var member in walk.Members)
                {
                    if (member is MethodSymbol candidate
                        && string.Equals(candidate.Name, method.Name, StringComparison.Ordinal)
                        && candidate.Parameters.Count == method.Parameters.Count)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void DeclareModuleMembers(EmitContext context, SurtrModuleBuilder builder, ModuleSymbol module)
        {
            foreach (var field in module.Fields)
                context.Declare(field, builder.DefineVariable(field.Name, _descriptors.Emit(field.Type), field.IsReadOnly, Visibility(field.Accessibility)));

            foreach (var property in module.Properties)
            {
                var declared = builder.DefineProperty(property.Name, _descriptors.Emit(property.Type), Visibility(property.Accessibility));

                if (property.Getter is MethodSymbol getter)
                    context.Declare(getter, declared.DefineGetter());

                if (property.Setter is MethodSymbol setter)
                    context.Declare(setter, declared.DefineSetter());
            }

            foreach (var method in module.Methods)
            {
                if (method.IsNative)
                    throw new SurtrEmitException($"'{method.Name}' is a module-level native function, which binds through the host global table rather than through a link name.");

                context.Declare(
                    method,
                    builder.DefineFunction(
                        _descriptors.EmitMethodName(method),
                        _descriptors.Emit(method.ReturnType),
                        Parameters(context, method),
                        Visibility(method.Accessibility)));
            }
        }

        private SurtrParameterInfo[] Parameters(EmitContext context, MethodSymbol method)
        {
            var parameters = new SurtrParameterInfo[method.Parameters.Count];

            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = method.Parameters[i];

                // A varargs parameter is declared by its element type: the body sees an array of it,
                // and the call site is what packs one.
                parameters[i] = parameter.IsVararg && parameter.Type.NonNullable is ArrayTypeSymbol array
                    ? context.Module.VarargsParameter(parameter.Name, _descriptors.Emit(array.ElementType))
                    : context.Module.Parameter(parameter.Name, _descriptors.Emit(parameter.Type));
            }

            return parameters;
        }

        /// <summary>Splits a type's initializers into the two places they run from.</summary>
        private void SortInitializers(TypeEmission emission, NamedTypeSymbol symbol)
        {
            foreach (var initializer in _binder.FieldInitializers)
            {
                if (!ReferenceEquals(initializer.DeclaringType, symbol))
                    continue;

                if (initializer.Field.IsStatic)
                    emission.StaticInitializers.Add(initializer);
                else
                    emission.InstanceInitializers.Add(initializer);
            }
        }
        #endregion

        #region Emitting bodies
        private void EmitTypeBodies(EmitContext context, TypeEmission emission)
        {
            if (emission.Class is not SurtrClassBuilder @class)
                return;

            if (emission.SyntheticConstructor is SurtrMethodBuilder synthetic)
            {
                EmitInstanceInitializers(context, emission, synthetic);
                synthetic.Code.ReturnVoid();
            }

            foreach (var (symbol, builder) in emission.Methods)
            {
                if (symbol.Role == MethodRole.Constructor)
                {
                    EmitInstanceInitializers(context, emission, builder);
                    EmitBody(context, symbol, builder, allowMissing: true);
                    continue;
                }

                if (IsAutoAccessor(emission.Symbol, symbol))
                {
                    EmitAutoAccessor(context, emission.Symbol, symbol, builder);
                    continue;
                }

                EmitBody(context, symbol, builder, allowMissing: false);
            }

            if (emission.StaticInitializers.Count == 0)
                return;

            var initializer = @class.DefineStaticInitializer();
            var body = new MethodBodyEmitter(initializer, StaticInitializerSymbol(emission.Symbol), context);

            // One emitter across all of them, and fragments rather than bodies: each assignment is
            // a statement in the same method, and letting any of them finish it would leave every
            // later one unreachable.
            foreach (var field in emission.StaticInitializers)
                body.EmitFragment(Assignment(field));

            initializer.Code.ReturnVoid();
        }

        private void EmitModuleBodies(EmitContext context, SurtrModuleBuilder builder, ModuleSymbol module)
        {
            foreach (var method in module.Methods)
            {
                if (context.TryGetBuilder(method, out var function))
                    EmitBody(context, method, function, allowMissing: false);
            }

            foreach (var property in module.Properties)
            {
                if (property.Getter is MethodSymbol getter && context.TryGetBuilder(getter, out var read))
                    EmitBody(context, getter, read, allowMissing: false);

                if (property.Setter is MethodSymbol setter && context.TryGetBuilder(setter, out var write))
                    EmitBody(context, setter, write, allowMissing: false);
            }

            var initializers = new List<BoundFieldInitializer>();
            foreach (var initializer in _binder.FieldInitializers)
            {
                if (initializer.DeclaringType is null && ReferenceEquals(initializer.Field.ContainingSymbol, module))
                    initializers.Add(initializer);
            }

            if (initializers.Count == 0)
                return;

            // A module-level variable is a static of its module, so its initializer runs from the
            // module's own initializer — which the runtime runs after every class's.
            var moduleInitializer = builder.DefineStaticInitializer();
            var body = new MethodBodyEmitter(moduleInitializer, StaticInitializerSymbol(null, module), context);

            foreach (var field in initializers)
                body.EmitFragment(Assignment(field));

            moduleInitializer.Code.ReturnVoid();
        }

        private void EmitBody(EmitContext context, MethodSymbol symbol, SurtrMethodBuilder builder, bool allowMissing)
        {
            if (!_binder.Bodies.TryGetValue(symbol, out var body))
            {
                if (!allowMissing)
                    throw new SurtrEmitException($"'{symbol.Name}' has no body to emit.");

                builder.Code.ReturnVoid();
                return;
            }

            new MethodBodyEmitter(builder, symbol, context).Emit(body);
        }

        private void EmitInstanceInitializers(EmitContext context, TypeEmission emission, SurtrMethodBuilder constructor)
        {
            if (emission.InstanceInitializers.Count == 0)
                return;

            var body = new MethodBodyEmitter(constructor, InstanceInitializerSymbol(emission.Symbol), context);

            foreach (var field in emission.InstanceInitializers)
                body.EmitFragment(Assignment(field));
        }

        /// <summary>Whether an accessor is one the compiler has to supply a body for.</summary>
        private bool IsAutoAccessor(NamedTypeSymbol owner, MethodSymbol accessor)
            => accessor.Role is MethodRole.PropertyGetter or MethodRole.PropertySetter
                && !_binder.Bodies.ContainsKey(accessor)
                && FindProperty(owner, accessor)?.BackingField is not null;

        private void EmitAutoAccessor(EmitContext context, NamedTypeSymbol owner, MethodSymbol accessor, SurtrMethodBuilder builder)
        {
            var property = FindProperty(owner, accessor)!;
            var backing = context.Resolve(property.BackingField!)
                ?? throw new SurtrEmitException($"'{property.Name}' has no backing field to read.");

            var code = builder.Code;
            bool isGetter = accessor.Role == MethodRole.PropertyGetter;

            if (accessor.IsStatic)
            {
                if (isGetter)
                {
                    code.LoadStaticField(backing);
                    code.ReturnValue();
                    return;
                }

                code.LoadLocal(builder.Parameter(0));
                code.StoreStaticField(backing);
                code.ReturnVoid();
                return;
            }

            code.LoadLocal(builder.Receiver);

            if (isGetter)
            {
                code.LoadField(backing);
                code.ReturnValue();
                return;
            }

            code.LoadLocal(builder.Parameter(0));
            code.StoreField(backing);
            code.ReturnVoid();
        }

        private static PropertySymbol? FindProperty(NamedTypeSymbol owner, MethodSymbol accessor)
        {
            foreach (var member in owner.Members)
            {
                if (member is PropertySymbol property
                    && (ReferenceEquals(property.Getter, accessor) || ReferenceEquals(property.Setter, accessor)))
                {
                    return property;
                }
            }

            return null;
        }

        /// <summary>Wraps one field initializer as the assignment it is.</summary>
        private static BoundStatement Assignment(BoundFieldInitializer initializer)
        {
            var syntax = initializer.Value.Syntax;

            BoundExpression? receiver = initializer.Field.IsStatic
                ? null
                : new BoundThisExpression(syntax, (TypeSymbol)initializer.DeclaringType!, isSuper: false);

            return new BoundExpressionStatement(
                syntax,
                new BoundAssignmentExpression(
                    syntax,
                    new BoundFieldExpression(syntax, receiver, initializer.Field),
                    initializer.Value));
        }

        /// <summary>
        /// The symbol an initializer fragment is emitted against.
        /// </summary>
        /// <remarks>
        /// Not a member of anything: an initializer runs inside a real method, and this exists only
        /// so the emitter has a name to put in a diagnostic and a receiver rule to apply.
        /// </remarks>
        private MethodSymbol StaticInitializerSymbol(NamedTypeSymbol? owner, ModuleSymbol? module = null)
            => new MethodSymbol("cinit", (Symbol?)owner ?? module!, _compilation.TypeFactory.Void) { IsStatic = true };

        private MethodSymbol InstanceInitializerSymbol(NamedTypeSymbol owner)
            => new MethodSymbol("ctor", owner, _compilation.TypeFactory.Void);
        #endregion

        #region Recording what was built
        private void Record(EmitContext context, ModuleSymbol symbol, List<TypeEmission> types, SurtrModule built)
        {
            foreach (var method in symbol.Methods)
            {
                if (context.TryGetBuilder(method, out var builder) && builder.Built is SurtrMethodInfo info)
                {
                    _builtMethods[method] = info;
                    _methodOwners[method] = built;
                }
            }

            foreach (var property in symbol.Properties)
            {
                RecordAccessor(context, property.Getter, built, moduleLevel: true);
                RecordAccessor(context, property.Setter, built, moduleLevel: true);
            }

            foreach (var field in symbol.Fields)
            {
                if (context.Resolve(field) is SurtrFieldInfo info)
                    _builtFields[field] = info;
            }

            foreach (var emission in types)
            {
                foreach (var (method, builder) in emission.Methods)
                {
                    if (builder.Built is SurtrMethodInfo info)
                        _builtMethods[method] = info;
                }

                foreach (var member in emission.Symbol.Members)
                {
                    switch (member)
                    {
                        case FieldSymbol field when context.Resolve(field) is SurtrFieldInfo info:
                            _builtFields[field] = info;
                            continue;

                        case MethodSymbol method when context.Resolve(method) is SurtrMethodInfo bound:
                            _builtMethods[method] = bound;
                            continue;
                    }
                }
            }
        }

        private void RecordAccessor(EmitContext context, MethodSymbol? accessor, SurtrModule built, bool moduleLevel)
        {
            if (accessor is null || !context.TryGetBuilder(accessor, out var builder) || builder.Built is not SurtrMethodInfo info)
                return;

            _builtMethods[accessor] = info;

            if (moduleLevel)
                _methodOwners[accessor] = built;
        }
        #endregion

        #region Translation
        // A type has no accessibility in the symbol model, because §2.1 gives one no meaning: a
        // module is the unit of visibility and every type it declares is reachable from it.
        private static SurtrVisibility Visibility(NamedTypeSymbol type) => SurtrVisibility.Public;

        private static SurtrVisibility Visibility(Accessibility accessibility) => accessibility switch
        {
            Accessibility.Public => SurtrVisibility.Public,
            Accessibility.Protected => SurtrVisibility.Protected,
            Accessibility.Internal => SurtrVisibility.Internal,
            _ => SurtrVisibility.Private,
        };

        private static SurtrMethodDispatch Dispatch(MethodSymbol method) => method.Dispatch switch
        {
            MethodDispatch.Virtual => SurtrMethodDispatch.Virtual,
            MethodDispatch.Abstract => SurtrMethodDispatch.Abstract,
            _ => SurtrMethodDispatch.Direct,
        };

        /// <summary>
        /// The name a host publishes a <c>native</c> member's body under (§10).
        /// </summary>
        /// <remarks>
        /// Derived rather than declared, so a member the source did not name still has a stable one:
        /// the owner's full name plus the member's, which cannot collide inside one type because a
        /// signature already cannot.
        /// </remarks>
        private string LinkName(MethodSymbol method)
            => (method.ContainingType is NamedTypeSymbol owner ? owner.FullMetadataName + "." : string.Empty)
                + _descriptors.EmitMethodName(method);
        #endregion
    }
}
