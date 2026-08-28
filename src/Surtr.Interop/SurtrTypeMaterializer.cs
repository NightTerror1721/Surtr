#nullable enable

using Surtr.Interop.Attributes;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
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
            if (descriptor.IsInline)
                return RegisterValueClass(runtime, descriptor);

            SurtrClass? baseClass = ResolveBase(runtime, descriptor);

            var declared = runtime.DefineNativeClass(descriptor.FullName, baseClass, TypeArguments(descriptor));

            foreach (var member in descriptor.Members)
                AddMember(runtime, declared, member);

            runtime.FinishNativeClass(declared);
            return declared;
        }

        /// <summary>
        /// Materializes a struct exposed with <c>Inline = true</c> as a Surtr value class: a run of
        /// contiguous slots that Surtr owns, rather than a proxy around a CLR instance.
        /// </summary>
        /// <remarks>
        /// The storage fields go on first and in descriptor order, because that order <em>is</em>
        /// the layout - it decides which slot each field takes, and the marshaler rebuilds the CLR
        /// struct by walking the same sequence. Everything else the type exposes (methods,
        /// properties, statics, operators) is added afterwards exactly as it is on an ordinary
        /// native class; only where the data lives is different.
        /// </remarks>
        private static SurtrClass RegisterValueClass(SurtrRuntime runtime, NativeTypeDescriptor descriptor)
        {
            // A value type has no identity to inherit through, so a base is not merely unused here
            // - it cannot exist. The linker refuses one outright; saying so at registration points
            // at the declaration rather than at the link.
            if (descriptor.BaseType is not null)
            {
                throw new InvalidOperationException(
                    $"Inline value type '{descriptor.FullName}' cannot extend '{descriptor.BaseType}': "
                    + "a value type has no identity to inherit through.");
            }

            var declared = runtime.DefineNativeValueClass(descriptor.FullName, TypeArguments(descriptor));

            foreach (var member in descriptor.Members)
            {
                if (member is NativeValueFieldDescriptor field)
                    runtime.DefineValueField(declared, field.Name, SurtrClassReference.FromDescriptor(field.TypeDescriptor), Visibility(field.Visibility));
            }

            foreach (var member in descriptor.Members)
            {
                if (member is not NativeValueFieldDescriptor)
                    AddMember(runtime, declared, member);
            }

            runtime.FinishNativeClass(declared);
            return declared;
        }

        private static SurtrClass RegisterEnum(SurtrRuntime runtime, NativeTypeDescriptor descriptor)
        {
            var declared = runtime.DefineNativeEnum(descriptor.FullName, TypeArguments(descriptor));

            // An enum is a value class whose first field is the synthetic `value` (§2.4); the
            // host's enum carries just it, and the case statics hold the values.
            runtime.DefineValueField(declared, "value", SurtrClassReference.Integer, SurtrVisibility.Public);

            for (int i = 0; i < descriptor.EnumCases.Length; i++)
            {
                var @case = descriptor.EnumCases[i];
                runtime.DefineNativeEnumCase(declared, @case.Name, @case.Value);
            }

            // A [Flags] CLR enum registers as a Surtr @Flags enum, so `| & ^` work on it (§2.7).
            if (descriptor.IsFlags)
                declared.AddAttribute(new SurtrAttributeUsage(runtime.TypeHandle(SurtrBuiltIns.Flags.SelfReference), Array.Empty<SurtrConstant>()));

            runtime.FinishNativeClass(declared);
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

        private static SurtrClassReference[]? TypeArguments(NativeTypeDescriptor descriptor)
        {
            if (descriptor.TypeArguments is null || descriptor.TypeArguments.Length == 0)
                return null;

            var arguments = new SurtrClassReference[descriptor.TypeArguments.Length];
            for (int i = 0; i < arguments.Length; i++)
                arguments[i] = SurtrClassReference.FromDescriptor(descriptor.TypeArguments[i]);

            return arguments;
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
                isOverride: method.IsOverride,
                returnType,
                parameters.ToArray(),
                // A constructor is never static on the Surtr side - source reaches it by naming
                // the type, not through the type - even though its entry point follows the
                // static-shaped wire (no receiver; the instance is the result). The metadata
                // guard against static constructors is about source semantics, not wire shape.
                isStatic: method.IsStatic && !method.IsConstructor,
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
