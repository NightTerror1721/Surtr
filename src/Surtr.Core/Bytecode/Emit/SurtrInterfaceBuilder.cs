#nullable enable

using Surtr.Runtime.Classes;
using System;

namespace Surtr.Bytecode.Emit
{
    /// <summary>
    /// Declares one interface: the contracts it extends, and the members it requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything an interface declares is complete the moment it is described - a contract has no
    /// bodies, no fields and no static members, so nothing here waits on the module's instruction
    /// stream the way a class's methods do. Members are attached as they are declared.
    /// </para>
    /// <para>
    /// Each method's declaring type is set to the interface itself. That is not cosmetic:
    /// <c>InvokeInterface</c> reads it to find which contract a call site is routed through, so a
    /// method whose declaring type named the implementing class would dispatch against the wrong
    /// slot block.
    /// </para>
    /// </remarks>
    public sealed class SurtrInterfaceBuilder
    {
        private readonly SurtrModuleBuilder _module;
        private readonly SurtrInterface _interface;
        private readonly string _fullName;
        private readonly SurtrClassReference _selfReference;
        private readonly SurtrTypeHandle _selfHandle;

        internal SurtrInterfaceBuilder(
            SurtrModuleBuilder module,
            string? declaringTypeFullName,
            string name,
            SurtrVisibility visibility,
            SurtrTypeHandle? declaringType)
        {
            _module = module;
            _fullName = declaringTypeFullName is null
                ? module.Module.Path + SurtrClassReference.ModuleSeparator + name
                : declaringTypeFullName + SurtrClassReference.NameSeparator + name;

            _selfReference = SurtrClassReference.Object(_fullName);
            _selfHandle = module.TypeHandle(_selfReference);
            _interface = new SurtrInterface(name, _selfReference, visibility, declaringType);
        }

        /// <summary>The module this interface is declared in.</summary>
        public SurtrModuleBuilder Module => _module;

        /// <summary>The metadata being filled in.</summary>
        public SurtrInterface Interface => _interface;

        /// <summary>The interface's fully qualified name, module path included.</summary>
        public string FullName => _fullName;

        /// <summary>The descriptor naming this interface.</summary>
        /// <remarks>
        /// An object descriptor, the same shape a class gets: a type reference cannot know in
        /// advance which of the two it will turn out to name, and the resolver tries both.
        /// </remarks>
        public SurtrClassReference SelfReference => _selfReference;

        /// <summary>The interned handle for this interface, as a member's declaring type.</summary>
        public SurtrTypeHandle SelfHandle => _selfHandle;

        /// <summary>Declares the interfaces this one extends.</summary>
        public SurtrInterfaceBuilder Extends(params SurtrClassReference[] interfaces)
        {
            if (interfaces is null)
                throw new ArgumentNullException(nameof(interfaces));

            var handles = new SurtrTypeHandle[interfaces.Length];
            for (int i = 0; i < interfaces.Length; i++)
                handles[i] = _module.TypeHandle(interfaces[i]);

            _interface.SetDeclaredExtendedInterfaces(handles);
            return this;
        }

        /// <summary>Declares a method the contract requires.</summary>
        public SurtrAbstractMethodInfo DefineMethod(
            string name,
            SurtrClassReference returnType,
            SurtrParameterInfo[]? parameters = null)
        {
            var method = new SurtrAbstractMethodInfo(
                name,
                _module.TypeHandle(returnType),
                parameters ?? Array.Empty<SurtrParameterInfo>(),
                SurtrVisibility.Public,
                _selfHandle);

            _interface.AddMethod(method);
            _module.RegisterInterfaceMethod(method);
            return method;
        }

        /// <summary>Declares a property the contract requires, along with the accessors behind it.</summary>
        /// <param name="name">The property's name; the accessors become <c>get_x</c> and <c>set_x</c>.</param>
        /// <param name="propertyType">The property's declared type.</param>
        /// <param name="readable">Whether the contract requires a getter.</param>
        /// <param name="writable">Whether the contract requires a setter.</param>
        public SurtrPropertyInfo DefineProperty(
            string name,
            SurtrClassReference propertyType,
            bool readable = true,
            bool writable = false)
        {
            if (!readable && !writable)
                throw new ArgumentException($"Interface property '{_interface.Name}.{name}' must require at least one accessor.", nameof(readable));

            SurtrAbstractMethodInfo? getter = readable
                ? DefineMethod("get_" + name, propertyType)
                : null;

            SurtrAbstractMethodInfo? setter = writable
                ? DefineMethod("set_" + name, SurtrClassReference.Void, new[] { new SurtrParameterInfo("value", _module.TypeHandle(propertyType)) })
                : null;

            var property = new SurtrPropertyInfo(
                name,
                _module.TypeHandle(propertyType),
                getter,
                setter,
                false,
                SurtrVisibility.Public,
                _selfHandle);

            _interface.AddProperty(property);
            return property;
        }
    }
}
