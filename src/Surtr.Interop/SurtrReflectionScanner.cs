#nullable enable

using Surtr.Interop.Attributes;
using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Surtr.Interop
{
    /// <summary>
    /// The reflection fallback: builds a <see cref="NativeTypeDescriptor"/> from a CLR type and its
    /// attributes, exactly as the source generator does at compile time. Entry points come from
    /// <see cref="SurtrReflectionInvoker"/> (DynamicMethod shims), so this path is not AOT-safe.
    /// </summary>
    public static class SurtrReflectionScanner
    {
        /// <summary>Scans a type marked <see cref="SurtrNativeTypeAttribute"/> into a descriptor.</summary>
        public static NativeTypeDescriptor Scan(Type type, SurtrNamingPolicy scopePolicy = SurtrNamingPolicy.Default)
        {
            if (type is null)
                throw new ArgumentNullException(nameof(type));

            var attribute = type.GetCustomAttribute<SurtrNativeTypeAttribute>()
                ?? throw new InvalidOperationException($"'{type.FullName}' is not marked [SurtrNativeType].");

            var effectivePolicy = attribute.NamingPolicy ?? scopePolicy;
            string name = attribute.Name ?? SurtrNaming.Apply(type.Name, effectivePolicy, SurtrNameKind.Type);
            string? module = attribute.Module;
            string fullName = module is null ? name : module + ":" + name;

            var descriptor = new NativeTypeDescriptor
            {
                FullName = fullName,
                Module = module,
                Name = name,
                Description = attribute.Description,
                Kind = type.IsEnum ? NativeTypeKind.Enum : type.IsValueType ? NativeTypeKind.Struct : NativeTypeKind.Class,
                BaseType = GetBaseTypeDescriptor(type, effectivePolicy),
            };

            if (descriptor.Kind == NativeTypeKind.Enum)
            {
                descriptor.EnumCases = Enum.GetNames(type);
                descriptor.EnumValues = Enum.GetValues(type).Cast<object>().ToArray();
                return descriptor;
            }

            descriptor.Members = ScanMembers(type, effectivePolicy);
            return descriptor;
        }

        private static string? GetBaseTypeDescriptor(Type type, SurtrNamingPolicy policy)
        {
            var baseType = type.BaseType;
            if (baseType is null || baseType == typeof(object) || baseType == typeof(ValueType) || baseType == typeof(Enum))
                return null;

            var attribute = baseType.GetCustomAttribute<SurtrNativeTypeAttribute>();
            return attribute is null ? null : SurtrTypeMapper.FullNameOf(baseType, attribute, policy);
        }

        private static NativeMemberDescriptor[] ScanMembers(Type type, SurtrNamingPolicy policy)
        {
            var members = new List<NativeMemberDescriptor>();

            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                if (ScanMethod(constructor, policy, isConstructor: true) is { } ctor)
                    members.Add(ctor);
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                    continue;

                if (ScanMethod(method, policy, isConstructor: false) is { } exposed)
                    members.Add(exposed);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (ScanField(field, policy) is { } exposed)
                    members.Add(exposed);
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (ScanProperty(property, policy) is { } exposed)
                    members.Add(exposed);
            }

            return members.ToArray();
        }

        private static NativeMethodDescriptor? ScanMethod(MethodBase method, SurtrNamingPolicy policy, bool isConstructor)
        {
            var ignore = method.GetCustomAttribute<SurtrNativeIgnoreAttribute>();
            var attribute = method.GetCustomAttribute<SurtrNativeMethodAttribute>();
            if (ignore is not null || (attribute is not null && !attribute.Expose))
                return null;

            var memberPolicy = attribute?.NamingPolicy ?? policy;
            string name = isConstructor ? "ctor" : attribute?.Name ?? SurtrNaming.Apply(method.Name, memberPolicy, SurtrNameKind.Member);
            bool isStatic = method.IsStatic;

            var parameters = method.GetParameters();
            var fullDescriptors = new SurtrClassReference[parameters.Length];
            var surtrParameters = new List<NativeParameterDescriptor>();
            var outDescriptors = new List<SurtrClassReference>();

            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var parameterAttribute = parameter.GetCustomAttribute<SurtrNativeParameterAttribute>();

                if (parameter.IsOut)
                {
                    outDescriptors.Add(parameterAttribute?.TypeDescriptor is { } d
                        ? SurtrClassReference.FromDescriptor(d)
                        : SurtrTypeMapper.Map(parameter.ParameterType.GetElementType()!, memberPolicy));
                    continue;
                }

                if (parameter.ParameterType.IsByRef)
                    return null; // ref/in have no Surtr equivalent

                var descriptor = parameterAttribute?.TypeDescriptor is { } typeDescriptor
                    ? SurtrClassReference.FromDescriptor(typeDescriptor)
                    : SurtrTypeMapper.Map(parameter.ParameterType, memberPolicy);

                fullDescriptors[i] = descriptor;

                surtrParameters.Add(new NativeParameterDescriptor
                {
                    Name = parameterAttribute?.Name ?? SurtrNaming.Apply(parameter.Name, memberPolicy, SurtrNameKind.Member),
                    Description = parameterAttribute?.Description,
                    TypeDescriptor = descriptor.Descriptor,
                });
            }

            var returnDescriptor = BuildReturnDescriptor(method, attribute, memberPolicy, outDescriptors);

            var slot = new ReflectionMemberSlot
            {
                Kind = ReflectionMemberKind.Method,
                Method = method as MethodInfo,
                IsStatic = isStatic,
                ResultDescriptor = returnDescriptor,
                Parameters = fullDescriptors,
            };

            return new NativeMethodDescriptor
            {
                Name = name,
                Description = attribute?.Description,
                Visibility = attribute?.Visibility ?? SurtrInteropVisibility.Public,
                IsStatic = isStatic,
                IsConstructor = isConstructor,
                ReturnDescriptor = returnDescriptor.Descriptor,
                Parameters = surtrParameters.ToArray(),
                EntryPoint = SurtrReflectionInvoker.Create(slot),
            };
        }

        private static SurtrClassReference BuildReturnDescriptor(
            MethodBase method,
            SurtrNativeMethodAttribute? attribute,
            SurtrNamingPolicy policy,
            List<SurtrClassReference> outDescriptors)
        {
            bool voidReturn = method is MethodInfo info && info.ReturnType == typeof(void);

            if (outDescriptors.Count == 0)
            {
                if (attribute?.ReturnDescriptor is { } declared)
                    return SurtrClassReference.FromDescriptor(declared);

                return method is MethodInfo m ? SurtrTypeMapper.Map(m.ReturnType, policy) : SurtrClassReference.Void;
            }

            SurtrClassReference? declaredReturn = method is MethodInfo mi && !voidReturn
                ? (attribute?.ReturnDescriptor is { } dr ? SurtrClassReference.FromDescriptor(dr) : SurtrTypeMapper.Map(mi.ReturnType, policy))
                : null;

            if (outDescriptors.Count == 1 && voidReturn)
                return outDescriptors[0];

            var elements = new List<SurtrClassReference>();
            if (declaredReturn is { } ret)
                elements.Add(ret);
            elements.AddRange(outDescriptors);

            return SurtrClassReference.Tuple(elements.ToArray());
        }

        private static NativeFieldDescriptor? ScanField(FieldInfo field, SurtrNamingPolicy policy)
        {
            var ignore = field.GetCustomAttribute<SurtrNativeIgnoreAttribute>();
            var attribute = field.GetCustomAttribute<SurtrNativeFieldAttribute>();
            if (ignore is not null || (attribute is not null && !attribute.Expose))
                return null;

            var memberPolicy = attribute?.NamingPolicy ?? policy;
            string name = attribute?.Name ?? SurtrNaming.Apply(field.Name, memberPolicy, SurtrNameKind.Member);
            bool isStatic = field.IsStatic;

            var typeReference = attribute?.TypeDescriptor is { } declared
                ? SurtrClassReference.FromDescriptor(declared)
                : SurtrTypeMapper.Map(field.FieldType, memberPolicy);

            return new NativeFieldDescriptor
            {
                Name = name,
                Description = attribute?.Description,
                Visibility = attribute?.Visibility ?? SurtrInteropVisibility.Public,
                IsStatic = isStatic,
                ReadOnly = attribute?.ReadOnly ?? field.IsInitOnly,
                TypeDescriptor = typeReference.Descriptor,
                Getter = SurtrReflectionInvoker.Create(new ReflectionMemberSlot { Kind = ReflectionMemberKind.FieldGetter, Field = field, IsStatic = isStatic, ResultDescriptor = typeReference }),
                Setter = SurtrReflectionInvoker.Create(new ReflectionMemberSlot { Kind = ReflectionMemberKind.FieldSetter, Field = field, IsStatic = isStatic, ResultDescriptor = typeReference }),
            };
        }

        private static NativePropertyDescriptor? ScanProperty(PropertyInfo property, SurtrNamingPolicy policy)
        {
            var ignore = property.GetCustomAttribute<SurtrNativeIgnoreAttribute>();
            var attribute = property.GetCustomAttribute<SurtrNativePropertyAttribute>();
            if (ignore is not null || (attribute is not null && !attribute.Expose))
                return null;

            var memberPolicy = attribute?.NamingPolicy ?? policy;
            string name = attribute?.Name ?? SurtrNaming.Apply(property.Name, memberPolicy, SurtrNameKind.Member);

            var typeReference = attribute?.TypeDescriptor is { } declared
                ? SurtrClassReference.FromDescriptor(declared)
                : SurtrTypeMapper.Map(property.PropertyType, memberPolicy);

            bool hasGetter = property.GetMethod is { } getter && getter.IsPublic;
            bool hasSetter = property.SetMethod is { } setter && setter.IsPublic;
            bool isStatic = (property.GetMethod ?? property.SetMethod)?.IsStatic ?? false;

            var descriptor = new NativePropertyDescriptor
            {
                Name = name,
                Description = attribute?.Description,
                Visibility = attribute?.Visibility ?? SurtrInteropVisibility.Public,
                IsStatic = isStatic,
                HasGetter = hasGetter,
                HasSetter = hasSetter,
                TypeDescriptor = typeReference.Descriptor,
            };

            if (hasGetter)
                descriptor.Getter = SurtrReflectionInvoker.Create(new ReflectionMemberSlot { Kind = ReflectionMemberKind.PropertyGetter, Method = property.GetMethod, IsStatic = isStatic, ResultDescriptor = typeReference });

            if (hasSetter)
                descriptor.Setter = SurtrReflectionInvoker.Create(new ReflectionMemberSlot { Kind = ReflectionMemberKind.PropertySetter, Method = property.SetMethod, IsStatic = isStatic, ResultDescriptor = typeReference });

            return descriptor;
        }
    }
}
