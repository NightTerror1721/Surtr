#nullable enable

using Surtr.Runtime.Classes;
using System;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// Declaration helper for the built-in classes: hangs native methods and properties on one
    /// <see cref="SurtrClass"/> while it is still under construction.
    /// </summary>
    /// <remarks>
    /// Setup-time code that runs once per process, so it favours being readable over being
    /// allocation-free - the parameter arrays and handles it builds are the class's permanent
    /// metadata anyway. Nothing here survives onto an execution path: what the interpreter ends up
    /// calling is a raw address inside a <see cref="SurtrNativeEntryPoint"/>.
    /// </remarks>
    internal sealed class SurtrBuiltInTypeBuilder
    {
        /// <summary>The shared empty parameter list, for the many built-in members that take nothing.</summary>
        internal static readonly SurtrParameterInfo[] NoParameters = Array.Empty<SurtrParameterInfo>();

        private readonly SurtrClass _class;
        private readonly SurtrTypeHandle _selfHandle;
        private readonly SurtrTypeHandleTable _handles;

        internal SurtrBuiltInTypeBuilder(SurtrClass @class, SurtrTypeHandle selfHandle, SurtrTypeHandleTable handles)
        {
            _class = @class;
            _selfHandle = selfHandle;
            _handles = handles;
        }

        /// <summary>The class being built.</summary>
        internal SurtrClass Class => _class;

        /// <summary>Interns a type handle in the built-in module's table.</summary>
        internal SurtrTypeHandle Handle(SurtrClassReference reference) => _handles.GetOrAdd(reference);

        /// <summary>Builds a parameter list from name/type pairs, in declaration order.</summary>
        internal SurtrParameterInfo[] Params(params (string Name, SurtrClassReference Type)[] parameters)
        {
            if (parameters.Length == 0)
                return NoParameters;

            var built = new SurtrParameterInfo[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                built[i] = new SurtrParameterInfo(parameters[i].Name, Handle(parameters[i].Type));

            return built;
        }

        /// <summary>
        /// Builds a parameter list ending in a varargs parameter, whose declared type is the
        /// <em>element</em> type - the body sees an array of it.
        /// </summary>
        /// <remarks>
        /// The call site packs the array, so a native body reading one is reading an ordinary
        /// <see cref="Objects.SurtrArray"/> argument and nothing about the calling convention
        /// changes.
        /// </remarks>
        internal SurtrParameterInfo[] ParamsWithVarargs(
            (string Name, SurtrClassReference Type) varargs,
            params (string Name, SurtrClassReference Type)[] leading)
        {
            var built = new SurtrParameterInfo[leading.Length + 1];
            for (int i = 0; i < leading.Length; i++)
                built[i] = new SurtrParameterInfo(leading[i].Name, Handle(leading[i].Type));

            built[leading.Length] = new SurtrParameterInfo(varargs.Name, Handle(varargs.Type), SurtrConstant.None, isVarargs: true);
            return built;
        }

        /// <summary>
        /// Declares a native method on the class.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="SurtrMethodDispatch.Direct"/> by default. Built-in classes are sealed by
        /// construction - nothing extends <c>string</c> - so there is nothing an override could
        /// come from, and a vtable slot would be an indirection with only ever one occupant.
        /// </para>
        /// <para>
        /// The exception is a member that satisfies an interface. Interface dispatch resolves
        /// through the class's vtable, so an implementation has to occupy a slot in it -
        /// <c>SurtrTypeLinker.BuildInterfaceDispatch</c> looks the contract's signature up among
        /// the virtual methods and nowhere else. Those are declared
        /// <see cref="SurtrMethodDispatch.Virtual"/>, which costs the interface-typed call site an
        /// indirection it was always going to pay and costs a statically-typed one nothing, since
        /// the compiler can still bind it directly.
        /// </para>
        /// </remarks>
        internal SurtrMethodInfo Method(
            string name,
            SurtrClassReference returnType,
            SurtrNativeEntryPoint entryPoint,
            SurtrParameterInfo[]? parameters = null,
            bool isStatic = false,
            SurtrMethodDispatch dispatch = SurtrMethodDispatch.Direct)
        {
            var method = new SurtrNativeMethodInfo(
                name,
                dispatch,
                SurtrMethodRole.Normal,
                isOverride: false,
                Handle(returnType),
                parameters ?? NoParameters,
                isStatic,
                SurtrVisibility.Public,
                _selfHandle,
                entryPoint);

            _class.AddMethod(method);
            return method;
        }

        /// <summary>Declares the interfaces this built-in class satisfies.</summary>
        /// <remarks>
        /// The handles are interned unresolved and bound by the built-in module's own binding pass,
        /// which is what lets a collection name an interface declared after it.
        /// </remarks>
        internal void Implements(params SurtrClassReference[] interfaces)
        {
            var handles = new SurtrTypeHandle[interfaces.Length];
            for (int i = 0; i < interfaces.Length; i++)
                handles[i] = Handle(interfaces[i]);

            _class.SetDeclaredInterfaces(handles);
        }

        /// <summary>Declares an instance field on the class.</summary>
        /// <remarks>
        /// The built-in <em>collections</em> have no fields - their storage is CLR state on the
        /// object, reached by native code that already knows the concrete type. A library class
        /// like <c>Exception</c> is different: Surtr source extends it, so its state has to be a
        /// real slot in a real instance layout that the linker lays out and the collector traces.
        /// </remarks>
        internal SurtrFieldInfo Field(string name, SurtrClassReference fieldType, bool isReadOnly = true)
        {
            var field = new SurtrFieldInfo(name, Handle(fieldType), isStatic: false, isReadOnly, SurtrVisibility.Private, _selfHandle);
            _class.AddField(field);
            return field;
        }

        /// <summary>Declares a native constructor on the class.</summary>
        internal SurtrMethodInfo Constructor(SurtrNativeEntryPoint entryPoint, SurtrParameterInfo[]? parameters = null)
        {
            var constructor = new SurtrNativeMethodInfo(
                "ctor",
                SurtrMethodDispatch.Direct,
                SurtrMethodRole.Constructor,
                isOverride: false,
                Handle(SurtrClassReference.Void),
                parameters ?? NoParameters,
                isStatic: false,
                SurtrVisibility.Public,
                _selfHandle,
                entryPoint);

            _class.AddMethod(constructor);
            return constructor;
        }

        /// <summary>
        /// Declares a native property on the class, plus the accessor methods behind it.
        /// </summary>
        /// <remarks>
        /// The accessors are declared as ordinary methods named <c>get_x</c> and <c>set_x</c>, the
        /// way the CLR does it, so they end up in the class's method tables and get frozen by the
        /// linker along with everything else - a property whose accessors were not in any table
        /// would be the one member the linker never sees.
        /// </remarks>
        internal SurtrPropertyInfo Property(
            string name,
            SurtrClassReference propertyType,
            SurtrNativeEntryPoint getter,
            SurtrNativeEntryPoint setter = default,
            bool isStatic = false,
            SurtrMethodDispatch dispatch = SurtrMethodDispatch.Direct)
        {
            SurtrMethodInfo? getterMethod = null;
            SurtrMethodInfo? setterMethod = null;

            if (getter.IsValid)
                getterMethod = Method("get_" + name, propertyType, getter, NoParameters, isStatic, dispatch);

            if (setter.IsValid)
                setterMethod = Method("set_" + name, SurtrClassReference.Void, setter, Params(("value", propertyType)), isStatic, dispatch);

            var property = new SurtrPropertyInfo(
                name,
                Handle(propertyType),
                getterMethod,
                setterMethod,
                isStatic,
                SurtrVisibility.Public,
                _selfHandle);

            _class.AddProperty(property);
            return property;
        }
    }
}
