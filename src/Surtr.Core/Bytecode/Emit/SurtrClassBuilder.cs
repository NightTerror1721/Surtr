#nullable enable

using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;

namespace Surtr.Bytecode.Emit
{
    /// <summary>
    /// Declares one class: its base, its interfaces, and every member it owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fields land on the <see cref="SurtrClass"/> immediately, because field metadata is complete
    /// the moment it is described - the linker fills in the slot index later. Methods cannot:
    /// their metadata needs the offset of a body that has not been emitted yet, so they are held
    /// here and attached by <see cref="SurtrModuleBuilder.Build"/>. Properties follow the methods,
    /// since a property points at its accessors.
    /// </para>
    /// <para>
    /// Nothing here assigns a slot, a vtable index or a static address. Those are the linker's, and
    /// it runs when the module is loaded into a runtime.
    /// </para>
    /// </remarks>
    public sealed class SurtrClassBuilder
    {
        private readonly SurtrModuleBuilder _module;
        private readonly SurtrClass _class;
        private readonly string _fullName;
        private readonly SurtrClassReference _selfReference;
        private readonly SurtrTypeHandle _selfHandle;

        private readonly List<SurtrMethodBuilder> _methods = new List<SurtrMethodBuilder>();
        private readonly List<SurtrPropertyBuilder> _properties = new List<SurtrPropertyBuilder>();
        private readonly List<SurtrClassBuilder> _nestedClasses = new List<SurtrClassBuilder>();
        private readonly List<SurtrInterfaceBuilder> _nestedInterfaces = new List<SurtrInterfaceBuilder>();

        internal SurtrClassBuilder(
            SurtrModuleBuilder module,
            SurtrClassBuilder? declaringClass,
            string name,
            SurtrClassReference? baseClass,
            bool isAbstract,
            SurtrVisibility visibility,
            bool isSealed = false,
            bool isEnum = false)
        {
            _module = module;
            _fullName = declaringClass is null
                ? module.Module.Path + SurtrClassReference.ModuleSeparator + name
                : declaringClass._fullName + SurtrClassReference.NameSeparator + name;

            _selfReference = SurtrClassReference.Object(_fullName);
            _selfHandle = module.TypeHandle(_selfReference);

            _class = new SurtrClass(
                name,
                SurtrValueTypeCode.Object,
                _selfReference,
                baseClass is null ? null : module.TypeHandle(baseClass.Value),
                isAbstract,
                visibility,
                declaringClass?._selfHandle,
                isSealed,
                isEnum);
        }

        /// <summary>The module this class is declared in.</summary>
        public SurtrModuleBuilder Module => _module;

        /// <summary>The metadata being filled in.</summary>
        public SurtrClass Class => _class;

        /// <summary>The class's fully qualified name, module path included.</summary>
        public string FullName => _fullName;

        /// <summary>The descriptor naming this class.</summary>
        public SurtrClassReference SelfReference => _selfReference;

        /// <summary>The interned handle for this class, as a member's declaring type.</summary>
        public SurtrTypeHandle SelfHandle => _selfHandle;

        /// <summary>Declares the interfaces this class implements.</summary>
        /// <remarks>Replaces any previous list rather than adding to it, as the underlying metadata does.</remarks>
        public SurtrClassBuilder Implements(params SurtrClassReference[] interfaces)
        {
            if (interfaces is null)
                throw new ArgumentNullException(nameof(interfaces));

            var handles = new SurtrTypeHandle[interfaces.Length];
            for (int i = 0; i < interfaces.Length; i++)
                handles[i] = _module.TypeHandle(interfaces[i]);

            _class.SetDeclaredInterfaces(handles);
            return this;
        }

        #region Fields

        /// <summary>Declares an instance field.</summary>
        public SurtrFieldInfo DefineField(
            string name,
            SurtrClassReference fieldType,
            bool isReadOnly = false,
            SurtrVisibility visibility = SurtrVisibility.Public)
        {
            var field = new SurtrFieldInfo(name, _module.TypeHandle(fieldType), false, isReadOnly, visibility, _selfHandle);
            _class.AddField(field);
            return field;
        }

        /// <summary>Declares a static field.</summary>
        public SurtrFieldInfo DefineStaticField(
            string name,
            SurtrClassReference fieldType,
            bool isReadOnly = false,
            SurtrVisibility visibility = SurtrVisibility.Public)
        {
            var field = new SurtrFieldInfo(name, _module.TypeHandle(fieldType), true, isReadOnly, visibility, _selfHandle);
            _class.AddField(field);
            return field;
        }

        #endregion

        #region Methods

        /// <summary>Declares a method with a bytecode body.</summary>
        /// <param name="name">The method's declared name.</param>
        /// <param name="returnType">The method's return type, or <see cref="SurtrClassReference.Void"/>.</param>
        /// <param name="parameters">The declared parameters, the receiver excluded.</param>
        /// <param name="isStatic">Whether the method belongs to the class rather than to its instances.</param>
        /// <param name="dispatch">
        /// Direct by default: a method is only virtual when it says so, and there is no implicit
        /// override.
        /// </param>
        /// <param name="isOverride">Whether this method replaces one inherited from a base class.</param>
        /// <param name="visibility">How widely the method is visible.</param>
        /// <param name="isSealed">
        /// Whether this override closes its branch of the hierarchy, so nothing below may replace
        /// it again. Legal only together with <paramref name="isOverride"/>.
        /// </param>
        public SurtrMethodBuilder DefineMethod(
            string name,
            SurtrClassReference returnType,
            SurtrParameterInfo[]? parameters = null,
            bool isStatic = false,
            SurtrMethodDispatch dispatch = SurtrMethodDispatch.Direct,
            bool isOverride = false,
            SurtrVisibility visibility = SurtrVisibility.Public,
            bool isSealed = false)
        {
            var builder = new SurtrMethodBuilder(
                _module, this, name, returnType,
                parameters ?? Array.Empty<SurtrParameterInfo>(),
                isStatic, visibility, dispatch, SurtrMethodRole.Normal, isOverride, isSealed);

            _methods.Add(builder);
            _module.RegisterMethod(builder);
            return builder;
        }

        /// <summary>Declares an instance constructor.</summary>
        /// <remarks>
        /// Constructors are never inherited and so are always directly dispatched. Chaining to a
        /// base constructor is a call in the body, not metadata - emit
        /// <c>InvokeSpecial</c> against the base's constructor.
        /// </remarks>
        public SurtrMethodBuilder DefineConstructor(
            SurtrParameterInfo[]? parameters = null,
            SurtrVisibility visibility = SurtrVisibility.Public,
            string name = "ctor")
        {
            var builder = new SurtrMethodBuilder(
                _module, this, name, SurtrClassReference.Void,
                parameters ?? Array.Empty<SurtrParameterInfo>(),
                false, visibility, SurtrMethodDispatch.Direct, SurtrMethodRole.Constructor, false);

            _methods.Add(builder);
            _module.RegisterMethod(builder);
            return builder;
        }

        /// <summary>Declares the class's static initializer.</summary>
        /// <remarks>
        /// Run once when the module is loaded, before the module's own initializer and in
        /// declaration order relative to sibling classes. An initializer that reads another class's
        /// statics only sees them if that class was declared first, which is the compiler's to
        /// check.
        /// </remarks>
        public SurtrMethodBuilder DefineStaticInitializer(string name = "cinit")
        {
            var builder = new SurtrMethodBuilder(
                _module, this, name, SurtrClassReference.Void,
                Array.Empty<SurtrParameterInfo>(),
                true, SurtrVisibility.Private, SurtrMethodDispatch.Direct, SurtrMethodRole.StaticInitializer, false);

            _methods.Add(builder);
            _module.RegisterMethod(builder);
            return builder;
        }

        /// <summary>Declares a method with no body, to be implemented by a derived class.</summary>
        public SurtrAbstractMethodInfo DefineAbstractMethod(
            string name,
            SurtrClassReference returnType,
            SurtrParameterInfo[]? parameters = null,
            SurtrVisibility visibility = SurtrVisibility.Public,
            string[]? genericParameters = null,
            string[][]? genericConstraints = null)
        {
            var method = new SurtrAbstractMethodInfo(
                name,
                _module.TypeHandle(returnType),
                parameters ?? Array.Empty<SurtrParameterInfo>(),
                visibility,
                _selfHandle,
                genericParameters,
                genericConstraints);

            _class.AddMethod(method);
            return method;
        }

        /// <summary>Declares a method whose body is a host function, already linked.</summary>
        /// <remarks>
        /// For a module the host builds and loads in the same process. Give
        /// <paramref name="linkName"/> as well if the module will ever be written to an image: the
        /// address cannot travel, so a name is what a load in another process binds against.
        /// </remarks>
        public SurtrNativeMethodInfo DefineNativeMethod(
            string name,
            SurtrClassReference returnType,
            SurtrNativeEntryPoint entryPoint,
            SurtrParameterInfo[]? parameters = null,
            bool isStatic = false,
            SurtrMethodDispatch dispatch = SurtrMethodDispatch.Direct,
            bool isOverride = false,
            SurtrVisibility visibility = SurtrVisibility.Public,
            bool isSealed = false,
            string? linkName = null)
        {
            var method = new SurtrNativeMethodInfo(
                name, dispatch, SurtrMethodRole.Normal, isOverride,
                _module.TypeHandle(returnType),
                parameters ?? Array.Empty<SurtrParameterInfo>(),
                isStatic, visibility, _selfHandle, entryPoint, isSealed, linkName);

            _class.AddMethod(method);
            return method;
        }

        /// <summary>
        /// Declares a method whose body the loading runtime will supply, by name.
        /// </summary>
        /// <remarks>
        /// The form to use for a module meant to travel: the declaration says what the member
        /// looks like and what it is called, and every runtime that loads it publishes its own
        /// body with <c>SurtrRuntime.DefineNativeBody</c>. A module can mix these freely with
        /// bytecode methods, which is what lets one module be written half in Surtr and half in
        /// C#. <c>linkName</c> is the name the host publishes the body under.
        /// </remarks>
        public SurtrNativeMethodInfo DeclareNativeMethod(
            string name,
            SurtrClassReference returnType,
            string linkName,
            SurtrParameterInfo[]? parameters = null,
            bool isStatic = false,
            SurtrMethodDispatch dispatch = SurtrMethodDispatch.Direct,
            bool isOverride = false,
            SurtrVisibility visibility = SurtrVisibility.Public,
            bool isSealed = false,
            string[]? genericParameters = null,
            string[][]? genericConstraints = null)
        {
            var method = new SurtrNativeMethodInfo(
                name, dispatch, SurtrMethodRole.Normal, isOverride,
                _module.TypeHandle(returnType),
                parameters ?? Array.Empty<SurtrParameterInfo>(),
                isStatic, visibility, _selfHandle, linkName, isSealed,
                genericParameters, genericConstraints);

            _class.AddMethod(method);
            return method;
        }

        /// <summary>Declares a constructor whose body is a host function.</summary>
        /// <remarks>
        /// The constructor crosses the wire as an <em>instance factory</em>: no receiver, its
        /// parameters from slot 0, and the newly created instance written over that same slot -
        /// which is why its declared return names this class rather than <c>V</c>. A native class's
        /// instance is the object its host constructor creates; there is nothing for
        /// <c>ObjNew</c> to allocate and no receiver to run against.
        /// </remarks>
        public SurtrNativeMethodInfo DefineNativeConstructor(
            SurtrNativeEntryPoint entryPoint,
            SurtrParameterInfo[]? parameters = null,
            SurtrVisibility visibility = SurtrVisibility.Public,
            string name = "ctor",
            string? linkName = null)
        {
            var method = new SurtrNativeMethodInfo(
                name, SurtrMethodDispatch.Direct, SurtrMethodRole.Constructor, false,
                _selfHandle,
                parameters ?? Array.Empty<SurtrParameterInfo>(),
                false, visibility, _selfHandle, entryPoint, false, linkName);

            _class.AddMethod(method);
            return method;
        }

        /// <summary>Declares a constructor whose body the loading runtime will supply, by name.</summary>
        /// <remarks>
        /// Instance-factory wire shape, like <see cref="DefineNativeConstructor(SurtrNativeEntryPoint, SurtrParameterInfo[], SurtrVisibility, string, string?)"/>:
        /// the return names this class, and the loading runtime publishes a body that answers the
        /// new instance over slot 0.
        /// </remarks>
        public SurtrNativeMethodInfo DeclareNativeConstructor(
            string linkName,
            SurtrParameterInfo[]? parameters = null,
            SurtrVisibility visibility = SurtrVisibility.Public,
            string name = "ctor")
        {
            var method = new SurtrNativeMethodInfo(
                name, SurtrMethodDispatch.Direct, SurtrMethodRole.Constructor, false,
                _selfHandle,
                parameters ?? Array.Empty<SurtrParameterInfo>(),
                false, visibility, _selfHandle, linkName);

            _class.AddMethod(method);
            return method;
        }

        #endregion

        #region Properties and nested types

        /// <summary>Declares a property, whose accessors are then added to the returned builder.</summary>
        public SurtrPropertyBuilder DefineProperty(
            string name,
            SurtrClassReference propertyType,
            bool isStatic = false,
            SurtrVisibility visibility = SurtrVisibility.Public)
        {
            var property = new SurtrPropertyBuilder(_module, this, null, name, propertyType, isStatic, visibility);
            _properties.Add(property);
            return property;
        }

        /// <summary>Declares a class nested inside this one.</summary>
        public SurtrClassBuilder DefineNestedClass(
            string name,
            SurtrClassReference? baseClass = null,
            bool isAbstract = false,
            SurtrVisibility visibility = SurtrVisibility.Public,
            bool isSealed = false)
        {
            var nested = new SurtrClassBuilder(_module, this, name, baseClass, isAbstract, visibility, isSealed);
            _class.AddNestedClass(nested.Class);
            _nestedClasses.Add(nested);
            return nested;
        }

        /// <summary>Declares an enum nested inside this class.</summary>
        public SurtrClassBuilder DefineNestedEnum(string name, SurtrClassReference? baseType = null, SurtrVisibility visibility = SurtrVisibility.Public)
        {
            var nested = new SurtrClassBuilder(_module, this, name, baseType, false, visibility, isSealed: true, isEnum: true);
            nested.Class.IsValueType = true;
            _class.AddNestedClass(nested.Class);
            _nestedClasses.Add(nested);
            return nested;
        }

        /// <summary>
        /// Declares the next case of this enum: a static, read-only field of the enum's own type,
        /// holding a value the static initializer constructs.
        /// </summary>
        /// <remarks>
        /// The ordinal follows the order the cases are declared in; the value is the case's
        /// <c>= n</c> (or implied progression), the key an exhaustive switch over the enum
        /// dispatches on.
        /// </remarks>
        public SurtrEnumCaseInfo DefineEnumCase(string name, int value, SurtrVisibility visibility = SurtrVisibility.Public)
        {
            var field = new SurtrFieldInfo(name, _selfHandle, true, true, visibility, _selfHandle);
            return _class.AddEnumCase(field, value);
        }

        /// <summary>Declares an interface nested inside this class.</summary>
        public SurtrInterfaceBuilder DefineNestedInterface(string name, SurtrVisibility visibility = SurtrVisibility.Public)
        {
            var nested = new SurtrInterfaceBuilder(_module, _fullName, name, visibility, _selfHandle);
            _class.AddNestedInterface(nested.Interface);
            _nestedInterfaces.Add(nested);
            return nested;
        }

        #endregion

        #region Build

        internal void CollectMethods(List<SurtrMethodBuilder> into)
        {
            into.AddRange(_methods);

            foreach (var nested in _nestedClasses)
                nested.CollectMethods(into);
        }

        /// <summary>Attaches everything that could only be built once the bodies were laid out.</summary>
        internal void AttachMembers()
        {
            foreach (var method in _methods)
                _class.AddMethod(method.Built!);

            foreach (var property in _properties)
                _class.AddProperty(property.Build());

            foreach (var nested in _nestedClasses)
                nested.AttachMembers();
        }

        internal SurtrMethodBuilder CreateAccessor(
            string name,
            SurtrClassReference returnType,
            SurtrParameterInfo[] parameters,
            bool isStatic,
            SurtrMethodDispatch dispatch,
            bool isOverride,
            SurtrVisibility visibility)
            => DefineMethod(name, returnType, parameters, isStatic, dispatch, isOverride, visibility);

        #endregion
    }

    /// <summary>
    /// Declares a property and the accessor methods behind it.
    /// </summary>
    /// <remarks>
    /// The accessors are ordinary methods named <c>get_x</c> and <c>set_x</c>, the way the CLR does
    /// it, so they land in the declaring type's method tables and get linked along with everything
    /// else. A property whose accessors were not in any table would be the one member the linker
    /// never sees.
    /// </remarks>
    public sealed class SurtrPropertyBuilder
    {
        private readonly SurtrModuleBuilder _module;
        private readonly SurtrClassBuilder? _class;
        private readonly SurtrInterfaceBuilder? _interface;
        private readonly string _name;
        private readonly SurtrClassReference _propertyType;
        private readonly bool _static;
        private readonly SurtrVisibility _visibility;

        private SurtrMethodBuilder? _getterBuilder;
        private SurtrMethodBuilder? _setterBuilder;
        private SurtrMethodInfo? _getter;
        private SurtrMethodInfo? _setter;

        internal SurtrPropertyBuilder(
            SurtrModuleBuilder module,
            SurtrClassBuilder? declaringClass,
            SurtrInterfaceBuilder? declaringInterface,
            string name,
            SurtrClassReference propertyType,
            bool isStatic,
            SurtrVisibility visibility)
        {
            _module = module;
            _class = declaringClass;
            _interface = declaringInterface;
            _name = name;
            _propertyType = propertyType;
            _static = isStatic;
            _visibility = visibility;
        }

        /// <summary>The property's declared name, without the accessor prefix.</summary>
        public string Name => _name;

        /// <summary>Adds a <c>get_x</c> accessor with a bytecode body.</summary>
        public SurtrMethodBuilder DefineGetter(SurtrMethodDispatch dispatch = SurtrMethodDispatch.Direct, bool isOverride = false)
            => _getterBuilder = CreateAccessor("get_" + _name, _propertyType, Array.Empty<SurtrParameterInfo>(), dispatch, isOverride);

        /// <summary>Adds a <c>set_x</c> accessor with a bytecode body, taking one <c>value</c> parameter.</summary>
        public SurtrMethodBuilder DefineSetter(SurtrMethodDispatch dispatch = SurtrMethodDispatch.Direct, bool isOverride = false)
        {
            var parameters = new[] { new SurtrParameterInfo("value", _module.TypeHandle(_propertyType)) };
            return _setterBuilder = CreateAccessor("set_" + _name, SurtrClassReference.Void, parameters, dispatch, isOverride);
        }

        /// <summary>
        /// Attaches an attribute to this property (§11).
        /// </summary>
        /// <remarks>
        /// Buffered rather than attached, for the reason a method's are: the metadata does not exist
        /// until <see cref="Build"/> runs, and a member refuses an attribute once it is built.
        /// </remarks>
        public SurtrPropertyBuilder AddAttribute(SurtrAttributeUsage attribute)
        {
            _attributes.Add(attribute);
            return this;
        }

        private readonly List<SurtrAttributeUsage> _attributes = new List<SurtrAttributeUsage>();

        /// <summary>Uses an already-built method as this property's getter.</summary>
        public SurtrPropertyBuilder BindGetter(SurtrMethodInfo getter)
        {
            _getter = getter;
            return this;
        }

        /// <summary>Uses an already-built method as this property's setter.</summary>
        public SurtrPropertyBuilder BindSetter(SurtrMethodInfo setter)
        {
            _setter = setter;
            return this;
        }

        /// <summary>Adds a <c>get_x</c> accessor whose body is a host function, already linked.</summary>
        /// <remarks>
        /// A native property is nothing but native accessors: a property is already a pair of
        /// ordinary <c>get_x</c>/<c>set_x</c> methods, so making them native needs no new concept
        /// and they travel in an image exactly as any other native member does.
        /// </remarks>
        public SurtrPropertyBuilder DefineNativeGetter(SurtrNativeEntryPoint entryPoint, string? linkName = null)
        {
            _getter = NativeAccessor("get_" + _name, _propertyType, Array.Empty<SurtrParameterInfo>(), entryPoint, linkName);
            return this;
        }

        /// <summary>Adds a <c>set_x</c> accessor whose body is a host function, already linked.</summary>
        public SurtrPropertyBuilder DefineNativeSetter(SurtrNativeEntryPoint entryPoint, string? linkName = null)
        {
            _setter = NativeAccessor("set_" + _name, SurtrClassReference.Void, ValueParameter(), entryPoint, linkName);
            return this;
        }

        /// <summary>Adds a <c>get_x</c> accessor whose body the loading runtime will supply, by name.</summary>
        public SurtrPropertyBuilder DeclareNativeGetter(string linkName)
        {
            _getter = DeclaredAccessor("get_" + _name, _propertyType, Array.Empty<SurtrParameterInfo>(), linkName);
            return this;
        }

        /// <summary>Adds a <c>set_x</c> accessor whose body the loading runtime will supply, by name.</summary>
        public SurtrPropertyBuilder DeclareNativeSetter(string linkName)
        {
            _setter = DeclaredAccessor("set_" + _name, SurtrClassReference.Void, ValueParameter(), linkName);
            return this;
        }

        private SurtrParameterInfo[] ValueParameter()
            => new[] { new SurtrParameterInfo("value", _module.TypeHandle(_propertyType)) };

        private SurtrMethodInfo NativeAccessor(
            string name,
            SurtrClassReference returnType,
            SurtrParameterInfo[] parameters,
            SurtrNativeEntryPoint entryPoint,
            string? linkName)
        {
            if (_class is not null)
                return _class.DefineNativeMethod(
                    name, returnType, entryPoint, parameters, _static,
                    SurtrMethodDispatch.Direct, false, _visibility, false, linkName);

            if (_interface is not null)
                throw new InvalidOperationException(
                    $"Property '{_name}' is declared on an interface, which cannot carry a body; bind abstract accessors instead.");

            return _module.DefineNativeFunction(name, returnType, entryPoint, parameters, _visibility, linkName);
        }

        private SurtrMethodInfo DeclaredAccessor(
            string name,
            SurtrClassReference returnType,
            SurtrParameterInfo[] parameters,
            string linkName)
        {
            if (_class is not null)
                return _class.DeclareNativeMethod(
                    name, returnType, linkName, parameters, _static,
                    SurtrMethodDispatch.Direct, false, _visibility);

            if (_interface is not null)
                throw new InvalidOperationException(
                    $"Property '{_name}' is declared on an interface, which cannot carry a body; bind abstract accessors instead.");

            return _module.DeclareNativeFunction(name, returnType, linkName, parameters, _visibility);
        }

        private SurtrMethodBuilder CreateAccessor(
            string name,
            SurtrClassReference returnType,
            SurtrParameterInfo[] parameters,
            SurtrMethodDispatch dispatch,
            bool isOverride)
        {
            if (_interface is not null)
                throw new InvalidOperationException(
                    $"Property '{_name}' is declared on an interface, which cannot carry a body; bind abstract accessors instead.");

            // A module-level property's accessors are module-level functions, which have no
            // receiver and no vtable to be dispatched through.
            return _class is null
                ? _module.CreateAccessor(name, returnType, parameters, _visibility)
                : _class.CreateAccessor(name, returnType, parameters, _static, dispatch, isOverride, _visibility);
        }

        internal SurtrPropertyInfo Build()
        {
            SurtrTypeHandle? declaringType = _class?.SelfHandle ?? _interface?.SelfHandle;

            var property = new SurtrPropertyInfo(
                _name,
                _module.TypeHandle(_propertyType),
                _getterBuilder?.Built ?? _getter,
                _setterBuilder?.Built ?? _setter,
                _static,
                _visibility,
                declaringType);

            for (int i = 0; i < _attributes.Count; i++)
                property.AddAttribute(_attributes[i]);

            return property;
        }
    }
}
