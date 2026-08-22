#nullable enable

using Surtr.Interop.Attributes;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;

namespace Surtr.Interop
{
    /// <summary>
    /// Turns a <see cref="NativeTypeDescriptor"/> into live Surtr metadata on a runtime: a native
    /// class or enum, its methods, fields and properties, and the entry points that back them. It is
    /// the single consumer of descriptors, so the reflection scanner and the source generator's
    /// emitted code both register through it.
    /// </summary>
    public static class SurtrTypeMaterializer
    {
        /// <summary>
        /// Materializes <paramref name="descriptor"/> into <paramref name="runtime"/>, registering
        /// globally (or per-runtime) via <see cref="SurtrRuntime.DefineNativeClass"/> /
        /// <see cref="SurtrRuntime.DefineNativeEnum"/>.
        /// </summary>
        /// <returns>The linked <see cref="SurtrClass"/> representing the type.</returns>
        public static SurtrClass Register(SurtrRuntime runtime, NativeTypeDescriptor descriptor)
        {
            if (runtime is null)
                throw new ArgumentNullException(nameof(runtime));

            if (descriptor is null)
                throw new ArgumentNullException(nameof(descriptor));

            return descriptor.Kind == NativeTypeKind.Enum
                ? RegisterEnum(runtime, descriptor)
                : RegisterClass(runtime, descriptor);
        }

        private static SurtrClass RegisterClass(SurtrRuntime runtime, NativeTypeDescriptor descriptor)
        {
            SurtrClass? baseClass = ResolveBase(runtime, descriptor);

            var declared = runtime.DefineNativeClass(descriptor.FullName, baseClass);

            foreach (var member in descriptor.Members)
                AddMember(runtime, declared, member);

            runtime.FinishNativeClass(declared);
            return declared;
        }

        private static SurtrClass RegisterEnum(SurtrRuntime runtime, NativeTypeDescriptor descriptor)
        {
            var declared = runtime.DefineNativeEnum(descriptor.FullName);

            Type enumType = typeof(void);
            var entries = new List<KeyValuePair<object, SurtrRef>>(descriptor.EnumCases.Length);

            for (int i = 0; i < descriptor.EnumCases.Length; i++)
            {
                object boxed = descriptor.EnumValues[i];
                enumType = boxed.GetType();

                var proxy = runtime.WrapNative(declared, boxed);
                runtime.AddRoot(proxy);
                runtime.DefineNativeEnumCase(declared, descriptor.EnumCases[i], proxy);
                entries.Add(new KeyValuePair<object, SurtrRef>(boxed, proxy.GetSurtrReference()));
            }

            runtime.FinishNativeClass(declared);

            if (descriptor.EnumCases.Length > 0)
                SurtrInteropState.For(runtime).AddEnumCache(enumType, new SurtrEnumCache(runtime, enumType, entries));

            return declared;
        }

        private static SurtrClass? ResolveBase(SurtrRuntime runtime, NativeTypeDescriptor descriptor)
        {
            if (descriptor.BaseType is null)
                return null;

            if (!runtime.TryGetNativeClass(descriptor.BaseType, out var baseClass))
                throw new InvalidOperationException(
                    $"Base type '{descriptor.BaseType}' of '{descriptor.FullName}' is not registered; register base types first.");

            return baseClass;
        }

        private static void AddMember(SurtrRuntime runtime, SurtrClass declared, NativeMemberDescriptor member)
        {
            switch (member)
            {
                case NativeMethodDescriptor method:
                    AddMethod(runtime, declared, method);
                    break;

                case NativeFieldDescriptor field:
                    runtime.DefineNativeField(
                        declared,
                        field.Name,
                        Resolve(field.TypeDescriptor),
                        field.Getter,
                        field.Setter,
                        field.IsStatic,
                        field.ReadOnly,
                        Visibility(field.Visibility));
                    break;

                case NativePropertyDescriptor property:
                    AddProperty(runtime, declared, property);
                    break;
            }
        }

        private static void AddMethod(SurtrRuntime runtime, SurtrClass declared, NativeMethodDescriptor method)
        {
            var returnType = Handle(runtime, method.ReturnDescriptor ?? SurtrClassReference.Void.Descriptor);
            var declaringType = Handle(runtime, declared.SelfReference.Descriptor);

            var parameters = new List<SurtrParameterInfo>(method.Parameters.Length);
            for (int i = 0; i < method.Parameters.Length; i++)
            {
                var parameter = method.Parameters[i];
                if (parameter.IsOut)
                    continue;

                parameters.Add(new SurtrParameterInfo(
                    parameter.Name,
                    Handle(runtime, parameter.TypeDescriptor ?? SurtrClassReference.Erased.Descriptor)));
            }

            var info = new SurtrNativeMethodInfo(
                method.Name,
                method.IsVirtual ? SurtrMethodDispatch.Virtual : SurtrMethodDispatch.Direct,
                method.IsConstructor ? SurtrMethodRole.Constructor : SurtrMethodRole.Normal,
                isOverride: false,
                returnType,
                parameters.ToArray(),
                method.IsStatic,
                Visibility(method.Visibility),
                declaringType,
                method.EntryPoint,
                linkName: method.LinkName);

            declared.AddMethod(info);
        }

        private static void AddProperty(SurtrRuntime runtime, SurtrClass declared, NativePropertyDescriptor property)
        {
            var propertyType = Handle(runtime, property.TypeDescriptor ?? SurtrClassReference.Erased.Descriptor);
            var declaringType = Handle(runtime, declared.SelfReference.Descriptor);
            var visibility = Visibility(property.Visibility);

            SurtrMethodInfo? getter = null;
            SurtrMethodInfo? setter = null;

            if (property.HasGetter)
            {
                getter = new SurtrNativeMethodInfo(
                    "get_" + property.Name,
                    SurtrMethodDispatch.Direct,
                    SurtrMethodRole.Normal,
                    isOverride: false,
                    propertyType,
                    Array.Empty<SurtrParameterInfo>(),
                    property.IsStatic,
                    visibility,
                    declaringType,
                    property.Getter);

                declared.AddMethod(getter);
            }

            if (property.HasSetter)
            {
                setter = new SurtrNativeMethodInfo(
                    "set_" + property.Name,
                    SurtrMethodDispatch.Direct,
                    SurtrMethodRole.Normal,
                    isOverride: false,
                    Handle(runtime, SurtrClassReference.Void.Descriptor),
                    new[] { new SurtrParameterInfo("value", propertyType) },
                    property.IsStatic,
                    visibility,
                    declaringType,
                    property.Setter);

                declared.AddMethod(setter);
            }

            var info = new SurtrPropertyInfo(property.Name, propertyType, getter, setter, property.IsStatic, visibility, declaringType);
            declared.AddProperty(info);
        }

        private static SurtrTypeHandle Handle(SurtrRuntime runtime, string descriptor)
            => runtime.TypeHandle(SurtrClassReference.FromDescriptor(descriptor));

        private static SurtrClassReference Resolve(string? descriptor)
            => descriptor is null
                ? throw new InvalidOperationException("A member type descriptor was not resolved before materialization.")
                : SurtrClassReference.FromDescriptor(descriptor);

        private static SurtrVisibility Visibility(SurtrInteropVisibility visibility) => visibility switch
        {
            SurtrInteropVisibility.Private => SurtrVisibility.Private,
            SurtrInteropVisibility.Internal => SurtrVisibility.Internal,
            SurtrInteropVisibility.Protected => SurtrVisibility.Protected,
            _ => SurtrVisibility.Public,
        };
    }
}
