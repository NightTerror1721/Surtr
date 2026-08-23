#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System.Collections.Generic;

namespace Surtr.Runtime.BuiltIns
{
    /// <summary>
    /// Declares <c>Module</c>: the reflection surface over a loaded <see cref="SurtrModule"/>,
    /// what <c>moduleof</c> and <c>Module.get</c>/<c>Module.tryGet</c> return.
    /// </summary>
    /// <remarks>
    /// Same shape and the same restraint as <see cref="SurtrReflectionBuiltIns"/>: enumerates what
    /// a module declares and looks one up by path - it reads and invokes nothing.
    /// </remarks>
    internal static unsafe class SurtrModuleReflectionBuiltIns
    {
        internal static void DeclareModule(SurtrBuiltInTypeBuilder builder)
        {
            var selfType = SurtrBuiltIns.ModuleType.SelfReference;
            var typeArray = SurtrClassReference.Array(SurtrBuiltIns.Type.SelfReference);
            var memberArray = SurtrClassReference.Array(SurtrBuiltIns.Member.SelfReference);
            var moduleArray = SurtrClassReference.Array(selfType);

            builder.Method(
                "get",
                selfType,
                SurtrNativeEntryPoint.FromFunctionPointer(&ModuleGet),
                builder.Params(("path", SurtrClassReference.String)),
                isStatic: true);

            builder.Method(
                "tryGet",
                selfType,
                SurtrNativeEntryPoint.FromFunctionPointer(&ModuleTryGet),
                builder.Params(("path", SurtrClassReference.String)),
                isStatic: true);

            builder.Property("path", SurtrClassReference.String, SurtrNativeEntryPoint.FromFunctionPointer(&ModulePath));
            builder.Method("classes", typeArray, SurtrNativeEntryPoint.FromFunctionPointer(&ModuleClasses));
            builder.Method("interfaces", typeArray, SurtrNativeEntryPoint.FromFunctionPointer(&ModuleInterfaces));
            builder.Method("members", memberArray, SurtrNativeEntryPoint.FromFunctionPointer(&ModuleMembers));
            builder.Method("submodules", moduleArray, SurtrNativeEntryPoint.FromFunctionPointer(&ModuleSubmodules));
        }

        #region get / tryGet
        private static int ModuleGet(SurtrCallArguments arguments)
        {
            string path = arguments.GetString(0).Text;
            if (!TryFindModule(arguments.Runtime, path, out var module))
                throw new KeyNotFoundException($"No module is loaded under path '{path}'.");

            return arguments.Return(WrapModule(arguments.Runtime, module));
        }

        private static int ModuleTryGet(SurtrCallArguments arguments)
        {
            string path = arguments.GetString(0).Text;
            return arguments.Return(TryFindModule(arguments.Runtime, path, out var module)
                ? WrapModule(arguments.Runtime, module)
                : SurtrValue.Null);
        }

        /// <summary>
        /// The built-in module (<see cref="SurtrBuiltIns.ModulePath"/>) is process-wide and never
        /// registered in a runtime's own module table - the same special case
        /// <see cref="SurtrRuntime.TryResolveHandle"/> already needs to reach it by name.
        /// </summary>
        private static bool TryFindModule(SurtrRuntime runtime, string path, out SurtrModule module)
        {
            if (path == SurtrBuiltIns.ModulePath)
            {
                module = SurtrBuiltIns.Module;
                return true;
            }

            return runtime.TryGetModule(path, out module!);
        }
        #endregion

        #region Instance members
        private static int ModulePath(SurtrCallArguments arguments)
            => arguments.Return(arguments.Runtime.NewStringValue(Self(arguments).Path));

        private static int ModuleClasses(SurtrCallArguments arguments)
        {
            var self = Self(arguments);
            var runtime = arguments.Runtime;
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrBuiltIns.Type.SelfReference));

            foreach (var type in self.Classes)
                array.Add(WrapType(runtime, type));

            return arguments.Return(SurtrValue.CreateReference(array.GetSurtrReference()));
        }

        private static int ModuleInterfaces(SurtrCallArguments arguments)
        {
            var self = Self(arguments);
            var runtime = arguments.Runtime;
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrBuiltIns.Type.SelfReference));

            foreach (var type in self.Interfaces)
                array.Add(WrapType(runtime, type));

            return arguments.Return(SurtrValue.CreateReference(array.GetSurtrReference()));
        }

        /// <summary>
        /// A module's own fields, properties and functions - deliberately not its classes and
        /// interfaces, which <see cref="ModuleClasses"/>/<see cref="ModuleInterfaces"/> already
        /// enumerate on their own, the same split <see cref="SurtrModule"/> itself keeps.
        /// </summary>
        private static int ModuleMembers(SurtrCallArguments arguments)
        {
            var self = Self(arguments);
            var runtime = arguments.Runtime;
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrBuiltIns.Member.SelfReference));

            var accessors = new HashSet<SurtrMethodInfo>();
            foreach (var property in self.Properties)
            {
                if (property.Getter is not null)
                    accessors.Add(property.Getter);

                if (property.Setter is not null)
                    accessors.Add(property.Setter);
            }

            foreach (var field in self.Fields)
            {
                if (!IsSynthetic(field.Name))
                    array.Add(WrapMember(runtime, field));
            }

            foreach (var property in self.Properties)
            {
                if (!IsSynthetic(property.Name))
                    array.Add(WrapMember(runtime, property));
            }

            foreach (var overloads in self.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                {
                    // A property's own get_x/set_x already appears once, as the property itself.
                    if (accessors.Contains(overloads[i]))
                        continue;

                    if (!IsSynthetic(overloads[i].Name))
                        array.Add(WrapMember(runtime, overloads[i]));
                }
            }

            return arguments.Return(SurtrValue.CreateReference(array.GetSurtrReference()));
        }

        /// <summary>
        /// Every module loaded in this runtime whose path sits strictly under this one's - a
        /// module is a directory (`ModulePath.cs`), so `a.b` is a different module from `a`, not
        /// one contained in it, and this is the runtime-side counterpart of the same linear scan
        /// the compiler already does for a directory wildcard import (Â§2.1, Fase 9).
        /// </summary>
        private static int ModuleSubmodules(SurtrCallArguments arguments)
        {
            var self = Self(arguments);
            var runtime = arguments.Runtime;
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrBuiltIns.ModuleType.SelfReference));

            string prefix = self.Path + ".";
            foreach (var candidate in runtime.LoadedModules)
            {
                if (candidate.Path.StartsWith(prefix, System.StringComparison.Ordinal))
                    array.Add(WrapModule(runtime, candidate));
            }

            return arguments.Return(SurtrValue.CreateReference(array.GetSurtrReference()));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool IsSynthetic(string name) => name.Length > 0 && name[0] == '$';

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static SurtrModule Self(SurtrCallArguments arguments) => arguments.GetUnchecked<SurtrModuleValue>(0).Wrapped;

        private static SurtrValue WrapModule(SurtrRuntime runtime, SurtrModule wrapped)
            => SurtrValue.CreateReference(runtime.GetOrCreateModuleValue(wrapped).GetSurtrReference());

        private static SurtrValue WrapType(SurtrRuntime runtime, SurtrTypeInfo wrapped)
            => SurtrValue.CreateReference(runtime.GetOrCreateTypeValue(wrapped).GetSurtrReference());

        private static SurtrValue WrapMember(SurtrRuntime runtime, SurtrMemberInfo member)
            => SurtrValue.CreateReference(runtime.NewMemberValue(member).GetSurtrReference());
        #endregion
    }
}
