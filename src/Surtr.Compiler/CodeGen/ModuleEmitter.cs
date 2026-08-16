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
    /// Anything it cannot emit raises <see cref="SurtrEmitException"/>, carrying the span of the
    /// construct it gave up on, which <see cref="Guarded"/> turns into a diagnostic against that
    /// construct — one per member, so a module with two un-lowered lines reports both. What it does
    /// not do is build that module or emit the ones after it: a half-emitted body is not a module,
    /// and every module after it names entries in its method table. A compilation with errors is not
    /// emitted at all: the bound tree of a failed compilation holds error nodes, and emitting one
    /// would produce a module that runs.
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

        // Keyed by type rather than by method, because a synthesised constructor has no symbol.
        private readonly Dictionary<NamedTypeSymbol, SurtrMethodInfo> _builtDefaultConstructors =
            new Dictionary<NamedTypeSymbol, SurtrMethodInfo>();

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

                _source = source;
                _failed = false;

                try
                {
                    if (EmitModule(symbol, source) is not SurtrModule built)
                        return (bool)(_emitted = false);

                    _modules.Add(built);
                }
                catch (Exception exception) when (exception is SurtrCompilerException or InvalidOperationException or ArgumentException)
                {
                    // What reaches here is a failure outside any one member: a declaration, or the
                    // Build() that lays the module out. There is no narrower place to point at.
                    Report(member: null, exception);
                    return (bool)(_emitted = false);
                }
            }

            return (bool)(_emitted = true);
        }

        // The module being emitted, for the file a diagnostic belongs to, and whether a member of it
        // has already failed.
        private SurtrSourceModule? _source;
        private bool _failed;

        /// <summary>
        /// Emits one member, turning a failure into a diagnostic against the construct that caused it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Per member rather than per module, because "this module could not be emitted" is true of
        /// every module that contains one bad line and tells a reader nothing about which. What a
        /// <see cref="SurtrEmitException"/> carries is the span of the construct itself, so the
        /// report lands where the source is.
        /// </para>
        /// <para>
        /// The rest of the module is emitted afterwards, so one un-lowered construct does not hide
        /// the others — but the module is not built and nothing after it is emitted either. A
        /// half-emitted body is not a module, and a later module names entries in this one's method
        /// table, so continuing would replace one real diagnostic with a cascade of invented ones.
        /// </para>
        /// </remarks>
        private void Guarded(MethodSymbol? member, Action emit)
        {
            try
            {
                emit();
            }
            catch (Exception exception) when (exception is SurtrCompilerException or InvalidOperationException or ArgumentException)
            {
                Report(member, exception);
                _failed = true;
            }
        }

        private void Report(MethodSymbol? member, Exception exception)
        {
            // An emit failure already names the member it happened in; anything else is a message
            // about the mechanism, so the member has to be said here.
            string message = exception is SurtrEmitException
                ? exception.Message
                : member is null
                    ? $"Module '{_source?.Path}' could not be emitted: {exception.Message}"
                    : $"'{member.Name}' could not be emitted: {exception.Message}";

            _compilation.Diagnostics.ReportError(
                SurtrDiagnosticCode.NotLowered,
                message,
                FileOf(member),
                exception is SurtrEmitException emit ? emit.Span : default);
        }

        /// <summary>The file a member was written in, falling back to the module's first.</summary>
        private string FileOf(MethodSymbol? member)
        {
            if (member is not null && _binder.BodyFiles.TryGetValue(member, out string? file))
                return file;

            return _source is SurtrSourceModule source && source.Units.Count > 0
                ? source.Units[0].File.Path
                : _source?.Path ?? string.Empty;
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
        private SurtrModule? EmitModule(ModuleSymbol symbol, SurtrSourceModule source)
        {
            var builder = new SurtrModuleBuilder(symbol.Path);
            var context = new EmitContext(builder, _descriptors)
            {
                Bodies = _binder.Bodies,
                Folder = _binder.ConstFolder,
                Importer = _compilation.Importer,
            };

            // Everything built earlier is nameable from here: a symbol resolves to real metadata,
            // and a module-level function also to the module whose table carries it.
            foreach (var pair in _builtMethods)
                context.Bind(pair.Key, pair.Value, _methodOwners.TryGetValue(pair.Key, out var owner) ? owner : null);

            foreach (var pair in _builtFields)
                context.Declare(pair.Key, pair.Value);

            foreach (var pair in _builtDefaultConstructors)
                context.BindDefaultConstructor(pair.Key, pair.Value);

            var types = new List<TypeEmission>();

            foreach (var type in symbol.Types)
                DeclareType(context, builder, declaringClass: null, type, types);

            foreach (var emission in types)
                DeclareMembers(context, emission);

            DeclareModuleMembers(context, builder, symbol);

            foreach (var emission in types)
                EmitTypeBodies(context, emission);

            EmitModuleBodies(context, builder, symbol);

            // Laying out a module whose bodies did not all emit would fail on a half-written one and
            // replace a diagnostic that points at source with one that points at the emitter.
            if (_failed)
                return null;

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
            string declaredName = DeclaredName(symbol, declaringClass);

            switch (symbol.TypeKind)
            {
                case TypeSymbolKind.Interface:
                {
                    var contract = declaringClass is null
                        ? module.DefineInterface(declaredName, Visibility(symbol))
                        : declaringClass.DefineNestedInterface(declaredName, Visibility(symbol));

                    Parameterise(contract.Interface, symbol);
                    context.Declare(symbol, contract);
                    emission = new TypeEmission(symbol, null, contract);
                    break;
                }

                case TypeSymbolKind.Enum:
                {
                    var @enum = declaringClass is null
                        ? module.DefineEnum(declaredName, Visibility(symbol))
                        : declaringClass.DefineNestedEnum(declaredName, Visibility(symbol));

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
                        ? module.DefineClass(declaredName, baseType, symbol.IsAbstract, Visibility(symbol), symbol.IsSealed)
                        : declaringClass.DefineNestedClass(declaredName, baseType, symbol.IsAbstract, Visibility(symbol), symbol.IsSealed);

                    Parameterise(@class.Class, symbol);
                    context.Declare(symbol, @class);
                    emission = new TypeEmission(symbol, @class, null);
                    break;
                }
            }

            Attach(context, symbol, emission.Class?.Class ?? (SurtrMemberInfo)emission.Contract!.Interface);
            into.Add(emission);

            foreach (var nested in symbol.NestedTypes)
                DeclareType(context, module, emission.Class, nested, into);
        }

        /// <summary>
        /// The name a type is declared under, which for one nested inside an <em>interface</em>
        /// carries the qualification (§2.3).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Nesting is stored on <c>SurtrClass</c> and not on <c>SurtrInterface</c>, so a type nested
        /// in a contract has no container to be added to — while §2.3 allows one, because a nested
        /// type is not a member with state and does not reopen the "pure contract" rule.
        /// </para>
        /// <para>
        /// It is declared at module level under its qualified name instead. That is not a workaround
        /// as much as the naming model showing through: §2.6 makes nesting <em>qualification</em>, a
        /// full name is <c>modulePath ':' segment ('.' segment)*</c>, and the descriptor the compiler
        /// already emits for this type is exactly <c>module:IShape.Kind</c>. Declaring it under that
        /// name is what makes the module's key and the descriptor agree. What it costs is that
        /// nothing at runtime relates the two types — which nothing needs to, since a nested type
        /// does not see its container's parameters (§6) and reaches nothing of its container's.
        /// </para>
        /// </remarks>
        private static string DeclaredName(NamedTypeSymbol symbol, SurtrClassBuilder? declaringClass)
        {
            if (declaringClass is not null || symbol.ContainingType is not NamedTypeSymbol container)
                return symbol.MetadataName;

            var name = new System.Text.StringBuilder(symbol.MetadataName);

            for (var walk = container; walk is not null; walk = walk.ContainingType)
                name.Insert(0, '.').Insert(0, walk.MetadataName);

            return name.ToString();
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
                    // A const carries no slot at all (§7.1) — every read of it already folded to a
                    // literal while binding (BodyBinder.ResolveField), so there is nothing here to
                    // declare.
                    case FieldSymbol { IsConst: true }:
                        continue;

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
            // another type may be emitted first and has to be able to name it. A class with no
            // initializers of its own still needs one when its base has something to run: nothing
            // else would ever reach the base's constructor, since `ObjNew` only allocates.
            if (emission.Constructors.Count == 0
                && (emission.InstanceInitializers.Count > 0 || NeedsConstruction(symbol.BaseType)))
            {
                var synthesised = @class.DefineConstructor();
                emission.SyntheticConstructor = synthesised;
                context.DeclareDefaultConstructor(symbol, synthesised);
            }
        }

        /// <summary>
        /// Whether building an instance of <paramref name="type"/> has to run anything, so a derived
        /// class declaring no constructor still needs one to reach it (§3.2).
        /// </summary>
        /// <remarks>
        /// Decided from symbols rather than from what has been emitted so far, because a derived
        /// class may be declared before its base and the two questions would otherwise be answered
        /// in whichever order the file happened to be written in.
        /// </remarks>
        private bool NeedsConstruction(NamedTypeSymbol? type)
        {
            for (var walk = type; walk is not null; walk = walk.BaseType)
            {
                foreach (var member in walk.Members)
                {
                    if (member is MethodSymbol { Role: MethodRole.Constructor })
                        return true;
                }

                foreach (var initializer in _binder.FieldInitializers)
                {
                    if (!initializer.Field.IsStatic && ReferenceEquals(initializer.DeclaringType, walk))
                        return true;
                }
            }

            return false;
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
                        // Which accessors the contract requires is what the declaration wrote, and
                        // the builder's default is read-only — so a `{ get; set; }` on an interface
                        // would otherwise lose its setter, and every call site assigning through the
                        // contract would name a method the interface does not declare.
                        var declared = contract.DefineProperty(
                            property.Name,
                            _descriptors.Emit(property.Type),
                            readable: property.Getter is not null,
                            writable: property.Setter is not null);

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
                var @case = @class.DefineEnumCase(field.Name, Visibility(field.Accessibility)).Field;
                context.Declare(field, @case);
                Attach(context, field, @case);
                return;
            }

            var descriptor = _descriptors.Emit(field.Type);

            var declared = field.IsStatic
                ? @class.DefineStaticField(field.Name, descriptor, field.IsReadOnly, Visibility(field.Accessibility))
                : @class.DefineField(field.Name, descriptor, field.IsReadOnly, Visibility(field.Accessibility));

            context.Declare(field, declared);
            Attach(context, field, declared);
        }

        /// <summary>
        /// Copies a declaration's attributes onto the metadata the runtime instantiates them from (§11).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The attribute is a real class and its arguments fill its fields positionally, so what
        /// travels is a type handle and a list of constants. The instance itself is built when the
        /// declaring module loads, with the module's other statics — which is why nothing here runs
        /// anything, and why an argument had to fold at compile time.
        /// </para>
        /// <para>
        /// Attached at declaration time, because <c>SurtrMemberInfo</c> refuses one once the member
        /// is built.
        /// </para>
        /// </remarks>
        private void Attach(EmitContext context, Symbol symbol, SurtrMemberInfo member)
        {
            foreach (var use in symbol.Attributes)
                member.AddAttribute(Usage(context, use));
        }

        private SurtrAttributeUsage Usage(EmitContext context, AttributeUse use)
        {
            var arguments = new SurtrConstant[use.Arguments.Count];
            for (int i = 0; i < arguments.Length; i++)
                arguments[i] = Constant(use.Arguments[i], use.Type.Name);

            return context.Module.Attribute(_descriptors.Emit(use.Type), arguments);
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

            foreach (var use in property.Attributes)
                declared.AddAttribute(Usage(context, use));

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

            // `override` is dropped where it names no base accessor, for the reason it is dropped on
            // a method: §2.2 makes a contract a promise rather than an inheritance, both are written
            // `override`, and `SurtrTypeLinker` rejects an override with no base entry to replace.
            if (property.Getter is MethodSymbol getter)
            {
                var builder = declared.DefineGetter(Dispatch(getter), OverridesABaseMethod(getter));
                context.Declare(getter, builder);
                emission.Methods.Add((getter, builder));
            }

            if (property.Setter is MethodSymbol setter)
            {
                var builder = declared.DefineSetter(Dispatch(setter), OverridesABaseMethod(setter));
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

                foreach (var use in method.Attributes)
                    constructor.AddAttribute(Usage(context, use));

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

            foreach (var use in method.Attributes)
                builder.AddAttribute(Usage(context, use));

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
            {
                // §7.1: a const carries no slot at all — every read of it already folded to a
                // literal while binding (BodyBinder.ResolveField).
                if (field.IsConst)
                    continue;

                // §10: a `native` variable declares nothing here. It has no static slot — the host
                // owns the storage — and the import that reaches it is interned at each use site, so
                // defining a variable of the same name would give the module a second, dead one.
                if (field.IsNative)
                {
                    builder.NativeVariable(field.Name);
                    continue;
                }

                var variable = builder.DefineVariable(field.Name, _descriptors.Emit(field.Type), field.IsReadOnly, Visibility(field.Accessibility));
                context.Declare(field, variable);
                Attach(context, field, variable);
            }

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
                // A module-level `native fun` is a host global (§10): it goes in the import table
                // rather than the method table, and a call site emits `CallGlobalNative`. Declared
                // here as well as at each use so a module that only ever passes it around still
                // fails to load when the host never registered it.
                if (method.IsNative)
                {
                    builder.NativeFunction(method.Name);
                    continue;
                }

                var function = builder.DefineFunction(
                    _descriptors.EmitMethodName(method),
                    _descriptors.Emit(method.ReturnType),
                    Parameters(context, method),
                    Visibility(method.Accessibility));

                foreach (var use in method.Attributes)
                    function.AddAttribute(Usage(context, use));

                context.Declare(method, function);
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
                if (parameter.IsVararg && parameter.Type.NonNullable is ArrayTypeSymbol array)
                {
                    parameters[i] = context.Module.VarargsParameter(parameter.Name, _descriptors.Emit(array.ElementType));
                    continue;
                }

                var type = _descriptors.Emit(parameter.Type);

                parameters[i] = parameter.HasDefaultValue
                    ? context.Module.Parameter(parameter.Name, type, Constant(parameter))
                    : context.Module.Parameter(parameter.Name, type);
            }

            return parameters;
        }

        /// <summary>
        /// A folded default in the form metadata carries, so a module compiled later can omit the
        /// argument too.
        /// </summary>
        /// <remarks>
        /// The interpreter never reads this — §4.8 makes filling a default the call site's job — but
        /// the declaration is where the value is written, and an image that dropped it would make a
        /// defaulted parameter mandatory to everything downstream.
        /// </remarks>
        private static SurtrConstant Constant(ParameterSymbol parameter) => parameter.DefaultValue switch
        {
            null => SurtrConstant.Null,
            long integer when parameter.Type.NonNullable.SpecialType == SpecialType.Float => SurtrConstant.Float(integer),
            long integer => SurtrConstant.Integer((int)integer),
            double real => SurtrConstant.Float(real),
            bool boolean => SurtrConstant.Boolean(boolean),
            char character => SurtrConstant.Character(character),
            string text => SurtrConstant.String(text),
            _ => throw new SurtrEmitException(
                $"The default for '{parameter.Name}' folded to a '{parameter.DefaultValue.GetType().Name}', which metadata cannot carry."),
        };

        /// <summary>One folded attribute argument, in the form metadata carries (§11).</summary>
        private static SurtrConstant Constant(object? value, string attribute) => value switch
        {
            null => SurtrConstant.Null,
            long integer => SurtrConstant.Integer((int)integer),
            int integer => SurtrConstant.Integer(integer),
            double real => SurtrConstant.Float(real),
            bool boolean => SurtrConstant.Boolean(boolean),
            char character => SurtrConstant.Character(character),
            string text => SurtrConstant.String(text),
            _ => throw new SurtrEmitException(
                $"An argument to '{attribute}' folded to a '{value.GetType().Name}', which metadata cannot carry."),
        };

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
                Guarded(null, () =>
                {
                    EmitConstructorPrologue(context, emission, constructor: null, synthetic);
                    synthetic.Code.ReturnVoid();
                });
            }

            foreach (var (symbol, builder) in emission.Methods)
            {
                if (symbol.Role == MethodRole.Constructor)
                {
                    // A value class's constructor is spliced at every creation site (§2.9), so the
                    // declared one is never reached. Emitting its body anyway would write to a
                    // field slot that a boxed value class does not have.
                    if (emission.Symbol.TypeKind == TypeSymbolKind.ValueClass)
                    {
                        builder.Code.ReturnVoid();
                        continue;
                    }

                    Guarded(symbol, () =>
                    {
                        EmitConstructorPrologue(context, emission, symbol, builder);
                        EmitBody(context, symbol, builder, allowMissing: true);
                    });

                    continue;
                }

                if (IsAutoAccessor(emission.Symbol, symbol))
                {
                    Guarded(symbol, () => EmitAutoAccessor(context, emission.Symbol, symbol, builder));
                    continue;
                }

                Guarded(symbol, () => EmitBody(context, symbol, builder, allowMissing: false));
            }

            Guarded(null, () => EmitBridges(context, emission, @class));

            var blocks = StaticBlocksOf(emission.Symbol);

            if (emission.StaticInitializers.Count == 0 && blocks.Count == 0)
                return;

            Guarded(null, () =>
            {
                var initializer = @class.DefineStaticInitializer();
                var body = new MethodBodyEmitter(initializer, StaticInitializerSymbol(emission.Symbol), context);

                // One emitter across all of them, and fragments rather than bodies: each assignment
                // is a statement in the same method, and letting any of them finish it would leave
                // every later one unreachable.
                EmitStaticFragments(body, emission.StaticInitializers, blocks);

                initializer.Code.ReturnVoid();
            });
        }

        /// <summary>
        /// Emits a type's or module's static field initializers and <c>static { }</c> blocks in the
        /// one order they were written in (§2.5, §3.2).
        /// </summary>
        /// <remarks>
        /// Two lists, one sequence: a block reads what the initializers above it wrote and is read by
        /// the ones below, so merging them by declaration position is the whole point of recording
        /// that position at all.
        /// </remarks>
        private static void EmitStaticFragments(
            MethodBodyEmitter body,
            List<BoundFieldInitializer> initializers,
            List<BoundStaticBlock> blocks)
        {
            int field = 0;
            int block = 0;

            while (field < initializers.Count || block < blocks.Count)
            {
                bool takeField = block >= blocks.Count
                    || (field < initializers.Count && initializers[field].Order <= blocks[block].Order);

                if (takeField)
                    body.EmitFragment(Assignment(initializers[field++]));
                else
                    body.EmitFragment(blocks[block++].Body);
            }
        }

        /// <summary>The <c>static { }</c> blocks a type declares, in source order.</summary>
        private List<BoundStaticBlock> StaticBlocksOf(NamedTypeSymbol symbol)
        {
            var blocks = new List<BoundStaticBlock>();

            foreach (var block in _binder.StaticBlocks)
            {
                if (ReferenceEquals(block.DeclaringType, symbol))
                    blocks.Add(block);
            }

            return blocks;
        }

        #region Bridges
        /// <summary>
        /// Emits a bridge for every generic-interface slot the class fills with a typed member.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>SurtrTypeLinker</c> matches an implementation to a contract slot by
        /// <c>SignatureKey()</c>, which writes <c>G&lt;n&gt;</c> as <c>E</c> — so a class satisfying
        /// <c>IComparable&lt;Vec2&gt;</c> occupies a slot keyed <c>compareTo(E)</c>, and its own
        /// <c>compareTo(Vec2)</c> misses it by spelling alone. This is Java's bargain and the
        /// compiler's half of it: a second member, named after the contract's, taking the erased
        /// parameters, that casts and forwards.
        /// </para>
        /// <para>
        /// The bridge is emitted, never bound. It has no symbol, so nothing in source can name it
        /// and <c>SignatureSet</c> never sees it — which is what keeps it from reading as a
        /// duplicate of the member it forwards to.
        /// </para>
        /// </remarks>
        private void EmitBridges(EmitContext context, TypeEmission emission, SurtrClassBuilder @class)
        {
            var owner = emission.Symbol;

            foreach (var contract in owner.Interfaces)
            {
                var open = contract.Definition.Members;
                var closed = _binder.MemberLookup.MembersOf(contract);

                for (int i = 0; i < open.Count && i < closed.Count; i++)
                {
                    if (open[i] is not MethodSymbol declared || closed[i] is not MethodSymbol wanted)
                        continue;

                    if (!NeedsBridge(owner, declared, wanted, out var target))
                        continue;

                    EmitBridge(context, @class, declared, wanted, target);
                }
            }
        }

        /// <summary>
        /// Whether a contract slot needs a bridge, and which member it should forward to.
        /// </summary>
        /// <remarks>
        /// Only when the class fills the slot with something more specific than the slot's own
        /// erased shape. A member already declared against the erased type occupies the slot
        /// directly and needs nothing, and a contract that mentions no type parameter has nothing
        /// to erase in the first place.
        /// </remarks>
        private bool NeedsBridge(
            NamedTypeSymbol owner,
            MethodSymbol declared,
            MethodSymbol wanted,
            out MethodSymbol target)
        {
            target = null!;

            string slot = SlotKey(declared);
            if (string.Equals(slot, SlotKey(wanted), StringComparison.Ordinal))
                return false;

            foreach (var candidate in _binder.MemberLookup.FindMethods(owner, wanted.Name))
            {
                string key = SlotKey(candidate);

                // Already erased: the class wrote the slot's own shape, so nothing is missing.
                if (string.Equals(key, slot, StringComparison.Ordinal))
                    return false;

                if (string.Equals(key, SlotKey(wanted), StringComparison.Ordinal))
                    target = candidate;
            }

            return target is not null;
        }

        /// <summary>The key <c>SurtrTypeLinker</c> matches a contract slot on.</summary>
        private string SlotKey(MethodSymbol method)
        {
            var builder = new System.Text.StringBuilder(method.Name).Append('(');

            foreach (var parameter in method.Parameters)
                builder.Append(SurtrClassReference.Erase(_descriptors.Emit(parameter.Type)).Descriptor);

            return builder.Append(')').ToString();
        }

        private void EmitBridge(
            EmitContext context,
            SurtrClassBuilder @class,
            MethodSymbol declared,
            MethodSymbol wanted,
            MethodSymbol target)
        {
            var parameters = new SurtrParameterInfo[declared.Parameters.Count];
            for (int i = 0; i < parameters.Length; i++)
            {
                parameters[i] = context.Module.Parameter(
                    declared.Parameters[i].Name,
                    SurtrClassReference.Erase(_descriptors.Emit(declared.Parameters[i].Type)));
            }

            var bridge = @class.DefineMethod(
                wanted.Name,
                SurtrClassReference.Erase(_descriptors.Emit(declared.ReturnType)),
                parameters,
                isStatic: false,
                SurtrMethodDispatch.Virtual,
                isOverride: false,
                SurtrVisibility.Public);

            var code = bridge.Code;
            code.LoadLocal(bridge.Receiver);

            for (int i = 0; i < parameters.Length; i++)
            {
                code.LoadLocal(bridge.Parameter(i));

                // The slot handed us a reference; the member it forwards to wants the real type.
                if (!ReferenceEquals(declared.Parameters[i].Type, wanted.Parameters[i].Type))
                    Narrow(code, wanted.Parameters[i].Type);
            }

            // Virtual, so an override further down the hierarchy is what a call through the
            // contract lands on — which is the whole reason the slot stores a vtable index.
            if (context.TryGetBuilder(target, out var local))
                code.Call(local);
            else if (context.Resolve(target) is SurtrMethodInfo built)
                code.CallVirtual(built);
            else
                throw new SurtrEmitException($"'{target.Name}' has no metadata for a bridge to forward to.");

            if (declared.ReturnType.IsVoid)
            {
                code.ReturnVoid();
                return;
            }

            // A slot returning the erased type has to be handed a reference back.
            if (!ReferenceEquals(declared.ReturnType, wanted.ReturnType))
                code.Box(MethodBodyEmitter.TypeCodeOf(wanted.ReturnType));

            code.ReturnValue();
        }

        /// <summary>Reads a concrete type out of the erased value a contract slot passes.</summary>
        private void Narrow(SurtrCodeEmitter code, TypeSymbol target)
        {
            var bare = target.NonNullable;

            if (bare.TypeKind == TypeSymbolKind.ValueClass)
            {
                code.CastTo(_descriptors.EmitBoxedForm((NamedTypeSymbol)bare));
                code.Unbox();
                return;
            }

            code.CastTo(_descriptors.Emit(bare));

            if (bare.IsPrimitive && !bare.IsVoid)
                code.Unbox();
        }
        #endregion

        private void EmitModuleBodies(EmitContext context, SurtrModuleBuilder builder, ModuleSymbol module)
        {
            foreach (var method in module.Methods)
            {
                if (context.TryGetBuilder(method, out var function))
                    Guarded(method, () => EmitBody(context, method, function, allowMissing: false));
            }

            foreach (var property in module.Properties)
            {
                if (property.Getter is MethodSymbol getter && context.TryGetBuilder(getter, out var read))
                    Guarded(getter, () => EmitBody(context, getter, read, allowMissing: false));

                if (property.Setter is MethodSymbol setter && context.TryGetBuilder(setter, out var write))
                    Guarded(setter, () => EmitBody(context, setter, write, allowMissing: false));
            }

            var initializers = new List<BoundFieldInitializer>();
            foreach (var initializer in _binder.FieldInitializers)
            {
                if (initializer.DeclaringType is null && ReferenceEquals(initializer.Field.ContainingSymbol, module))
                    initializers.Add(initializer);
            }

            var blocks = new List<BoundStaticBlock>();
            foreach (var block in _binder.StaticBlocks)
            {
                if (block.DeclaringType is null && ReferenceEquals(block.Module, module))
                    blocks.Add(block);
            }

            if (initializers.Count == 0 && blocks.Count == 0)
                return;

            Guarded(null, () =>
            {
                // A module-level variable is a static of its module, so its initializer runs from
                // the module's own initializer — which the runtime runs after every class's. A
                // module-level `static { }` block runs from the same place, in its own source
                // position among them.
                var moduleInitializer = builder.DefineStaticInitializer();
                var body = new MethodBodyEmitter(moduleInitializer, StaticInitializerSymbol(null, module), context);

                EmitStaticFragments(body, initializers, blocks);

                moduleInitializer.Code.ReturnVoid();
            });
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

        /// <summary>
        /// Emits what runs before a constructor's own body: the chain it names, and this class's
        /// instance field initializers (§3.2).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The order is forced. A <c>super(...)</c> chain runs the base constructor first, because
        /// this class's initializers may read what the base set up. A <c>this(...)</c> chain runs
        /// another constructor of the same class, which already ran the initializers — so running
        /// them again would undo whatever that constructor did with them, which is exactly what
        /// §3.2 says must not happen.
        /// </para>
        /// <para>
        /// With nothing written, the base's parameterless constructor is called anyway: it is what
        /// §3.2 promises, and <c>ObjNew</c> allocates without running anything.
        /// </para>
        /// </remarks>
        private void EmitConstructorPrologue(
            EmitContext context,
            TypeEmission emission,
            MethodSymbol? constructor,
            SurtrMethodBuilder builder)
        {
            // Against the real constructor where there is one, because a chain's arguments are
            // ordinary expressions over its parameters and a stand-in symbol would not own them.
            var body = new MethodBodyEmitter(builder, constructor ?? InstanceInitializerSymbol(emission.Symbol), context);

            if (constructor is not null && _binder.ConstructorChains.TryGetValue(constructor, out var chain))
            {
                body.EmitFragment(ChainStatement(emission.Symbol, chain));

                if (chain.IsThis)
                    return;
            }
            else
            {
                EmitImplicitBaseChain(context, emission.Symbol, builder);
            }

            foreach (var field in emission.InstanceInitializers)
                body.EmitFragment(Assignment(field));
        }

        /// <summary>Wraps a bound chain as the call on <c>this</c> that it is.</summary>
        private static BoundStatement ChainStatement(NamedTypeSymbol owner, BoundConstructorChain chain)
            => new BoundExpressionStatement(
                chain.Syntax,
                new BoundCallExpression(
                    chain.Syntax,
                    new BoundThisExpression(chain.Syntax, owner, isSuper: false),
                    chain.Target,
                    chain.Arguments,
                    isVirtual: false));

        /// <summary>
        /// Calls the base's parameterless constructor for a constructor that chains to nothing.
        /// </summary>
        /// <remarks>
        /// Emitted as instructions rather than as a bound call, because the constructor it reaches
        /// may be one the compiler synthesised — which has a builder and metadata but no symbol for
        /// a bound tree to name.
        /// </remarks>
        private void EmitImplicitBaseChain(EmitContext context, NamedTypeSymbol owner, SurtrMethodBuilder builder)
        {
            if (owner.BaseType is not NamedTypeSymbol baseType)
                return;

            foreach (var member in _binder.MemberLookup.MembersOf(baseType))
            {
                if (member is not MethodSymbol { Role: MethodRole.Constructor } candidate || candidate.Parameters.Count != 0)
                    continue;

                builder.Code.LoadLocal(builder.Receiver);

                if (context.TryGetBuilder(candidate, out var declared))
                    builder.Code.Call(declared, discardResult: true);
                else if (context.Resolve(candidate) is SurtrMethodInfo built)
                    builder.Code.Call(built, discardResult: true);
                else
                    throw new SurtrEmitException($"'{baseType.Name}' has a constructor with no metadata to call.");

                return;
            }

            if (!TryGetSynthesizedConstructor(context, baseType, out var synthetic, out var syntheticInfo))
                return;

            builder.Code.LoadLocal(builder.Receiver);

            if (synthetic is not null)
                builder.Code.Call(synthetic, discardResult: true);
            else
                builder.Code.Call(syntheticInfo!, discardResult: true);
        }

        /// <summary>
        /// The constructor synthesised for a type that declares none, from this module or from one
        /// built earlier in the same compilation.
        /// </summary>
        /// <remarks>
        /// Both halves are needed because a synthesised constructor has no symbol: inside the module
        /// being built it is a builder, and across a module boundary it is metadata that nothing on
        /// the symbol side records. Without the second half a creation site in another module would
        /// allocate an instance and run none of its initializers.
        /// </remarks>
        private bool TryGetSynthesizedConstructor(
            EmitContext context,
            NamedTypeSymbol type,
            out SurtrMethodBuilder? builder,
            out SurtrMethodInfo? built)
        {
            if (context.TryGetDefaultConstructor(type, out var declared))
            {
                builder = declared;
                built = null;
                return true;
            }

            builder = null;
            return context.TryGetBuiltDefaultConstructor(type, out built!);
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

                // A synthesised constructor has no symbol, so nothing above records it — and a
                // creation site in a module built later has to be able to call it or the instance it
                // allocates runs none of its initializers.
                if (emission.SyntheticConstructor?.Built is SurtrMethodInfo synthetic)
                    _builtDefaultConstructors[emission.Symbol.Definition] = synthetic;

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
        private static SurtrVisibility Visibility(NamedTypeSymbol type) => Visibility(type.Accessibility);

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
