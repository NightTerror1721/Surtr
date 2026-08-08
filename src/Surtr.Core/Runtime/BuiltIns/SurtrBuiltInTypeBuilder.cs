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
        /// Declares a native method on the class.
        /// </summary>
        /// <remarks>
        /// Always <see cref="SurtrMethodDispatch.Direct"/>. Built-in classes are sealed by
        /// construction - nothing extends <c>string</c> - so there is nothing an override could
        /// come from, and a vtable slot would be an indirection with only ever one occupant.
        /// </remarks>
        internal SurtrMethodInfo Method(
            string name,
            SurtrClassReference returnType,
            SurtrNativeEntryPoint entryPoint,
            SurtrParameterInfo[]? parameters = null,
            bool isStatic = false)
        {
            var method = new SurtrNativeMethodInfo(
                name,
                SurtrMethodDispatch.Direct,
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
            bool isStatic = false)
        {
            SurtrMethodInfo? getterMethod = null;
            SurtrMethodInfo? setterMethod = null;

            if (getter.IsValid)
                getterMethod = Method("get_" + name, propertyType, getter, NoParameters, isStatic);

            if (setter.IsValid)
                setterMethod = Method("set_" + name, SurtrClassReference.Void, setter, Params(("value", propertyType)), isStatic);

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
