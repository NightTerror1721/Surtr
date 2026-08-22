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

            var attribute = TypeAttribute(type)
                ?? throw new InvalidOperationException($"'{type.FullName}' is not marked [SurtrNativeType].");

            var effectivePolicy = attribute.NamingPolicy ?? scopePolicy;

            string stripped = StripArity(type.Name);
            string adaptedName = attribute.Name ?? SurtrNaming.Apply(stripped, effectivePolicy, SurtrNameKind.Type);
            string mangledName = type.IsGenericType
                ? SurtrClassReference.MangleArity(adaptedName, type.GetGenericArguments().Length)
                : adaptedName;

            string? module = attribute.Module;
            string fullName = module is null ? mangledName : module + ":" + mangledName;

            var descriptor = new NativeTypeDescriptor
            {
                FullName = fullName,
                Module = module,
                Name = mangledName,
                Description = attribute.Description,
                Kind = type.IsEnum ? NativeTypeKind.Enum : type.IsValueType ? NativeTypeKind.Struct : NativeTypeKind.Class,
                BaseType = GetBaseTypeDescriptor(type, effectivePolicy),
                TypeArguments = type.IsGenericType
                    ? type.GetGenericArguments().Select(t => SurtrTypeMapper.Map(t, effectivePolicy).Descriptor).ToArray()
                    : Array.Empty<string>(),
            };

            if (descriptor.Kind == NativeTypeKind.Enum)
            {
                descriptor.EnumCases = Enum.GetNames(type);
                descriptor.EnumValues = Enum.GetValues(type).Cast<object>().ToArray();
                return descriptor;
            }

            descriptor.Members = ScanMembers(type, effectivePolicy, SurtrClassReference.Native(fullName));
            return descriptor;
        }

        private static SurtrNativeTypeAttribute? TypeAttribute(Type type)
        {
            foreach (var attribute in type.GetCustomAttributes(typeof(SurtrNativeTypeAttribute), inherit: false))
            {
                if (attribute is SurtrNativeTypeAttribute typed)
                    return typed;
            }

            return null;
        }

        private static string? GetBaseTypeDescriptor(Type type, SurtrNamingPolicy policy)
        {
            var baseType = type.BaseType;
            if (baseType is null || baseType == typeof(object) || baseType == typeof(ValueType) || baseType == typeof(Enum))
                return null;

            var attribute = TypeAttribute(baseType);
            return attribute is null ? null : SurtrTypeMapper.FullNameOf(baseType, attribute, policy);
        }

        private static NativeMemberDescriptor[] ScanMembers(Type type, SurtrNamingPolicy policy, SurtrClassReference selfDescriptor)
        {
            var members = new List<NativeMemberDescriptor>();

            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                if (ScanMethod(constructor, policy, isConstructor: true) is { } ctor)
                    members.Add(ctor);
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.IsAbstract)
                    continue;

                // IComparable.CompareTo is the one non-operator C# member with a Surtr operator
                // equivalent: its three-way int is exactly Surtr's `operator<=>`, from which Surtr
                // derives <, <=, >, >= and <=> itself.
                if (IsCompareTo(method))
                {
                    if (ScanComparison(method, policy) is { } comparison)
                        members.Add(comparison);
                    continue;
                }

                if (method.IsSpecialName)
                {
                    if (SurtrOperatorMapper.IsOperator(method.Name))
                    {
                        if (ScanOperator(method, policy) is { } op)
                            members.Add(op);
                    }
                    continue;
                }

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
                if (property.GetIndexParameters().Length > 0)
                    members.AddRange(ScanIndexer(property, policy, selfDescriptor));
                else if (ScanProperty(property, policy) is { } exposed)
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
                IsVirtual = method is MethodInfo virtualMethod && virtualMethod.IsVirtual,
                IsOverride = method is MethodInfo overrideMethod && overrideMethod.GetBaseDefinition() != overrideMethod,
                ReturnDescriptor = returnDescriptor.Descriptor,
                Parameters = surtrParameters.ToArray(),
                EntryPoint = SurtrReflectionInvoker.Create(slot),
            };
        }

        private static bool IsCompareTo(MethodInfo method)
            => method.Name == "CompareTo"
               && !method.IsStatic
               && method.GetParameters().Length == 1
               && method.ReturnType == typeof(int)
               && (typeof(IComparable).IsAssignableFrom(method.DeclaringType!)
                   || method.DeclaringType!.GetInterfaces().Any(static i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IComparable<>)));

        private static NativeMethodDescriptor? ScanComparison(MethodInfo method, SurtrNamingPolicy policy)
        {
            var ignore = method.GetCustomAttribute<SurtrNativeIgnoreAttribute>();
            var attribute = method.GetCustomAttribute<SurtrNativeMethodAttribute>();
            if (ignore is not null || (attribute is not null && !attribute.Expose))
                return null;

            var parameter = method.GetParameters()[0];
            var parameterDescriptor = SurtrTypeMapper.Map(parameter.ParameterType, policy);

            return new NativeMethodDescriptor
            {
                Name = "op_<=>",
                Visibility = attribute?.Visibility ?? SurtrInteropVisibility.Public,
                IsStatic = false,
                IsVirtual = method.IsVirtual,
                IsOverride = method.GetBaseDefinition() != method,
                ReturnDescriptor = SurtrClassReference.Integer.Descriptor,
                Parameters = new[]
                {
                    new NativeParameterDescriptor { Name = parameter.Name ?? "other", TypeDescriptor = parameterDescriptor.Descriptor },
                },
                EntryPoint = SurtrReflectionInvoker.Create(new ReflectionMemberSlot
                {
                    Kind = ReflectionMemberKind.Method,
                    Method = method,
                    IsStatic = false,
                    ResultDescriptor = SurtrClassReference.Integer,
                    Parameters = new[] { parameterDescriptor },
                }),
            };
        }

        private static NativeMethodDescriptor? ScanOperator(MethodInfo method, SurtrNamingPolicy policy)
        {
            var ignore = method.GetCustomAttribute<SurtrNativeIgnoreAttribute>();
            var attribute = method.GetCustomAttribute<SurtrNativeMethodAttribute>();
            if (ignore is not null || (attribute is not null && !attribute.Expose))
                return null;

            var parameters = method.GetParameters();
            var returnDescriptor = attribute?.ReturnDescriptor is { } declared
                ? SurtrClassReference.FromDescriptor(declared)
                : SurtrTypeMapper.Map(method.ReturnType, policy);

            string? operatorName = SurtrOperatorMapper.Map(method.Name, parameters.Length, returnDescriptor.Descriptor);
            if (operatorName is null)
                return null; // no Surtr equivalent (the generator emits a warning)

            var fullDescriptors = new SurtrClassReference[parameters.Length];
            var surtrParameters = new List<NativeParameterDescriptor>();

            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                var parameterAttribute = parameter.GetCustomAttribute<SurtrNativeParameterAttribute>();
                var descriptor = parameterAttribute?.TypeDescriptor is { } d
                    ? SurtrClassReference.FromDescriptor(d)
                    : SurtrTypeMapper.Map(parameter.ParameterType, policy);

                fullDescriptors[i] = descriptor;
                surtrParameters.Add(new NativeParameterDescriptor
                {
                    Name = parameterAttribute?.Name ?? SurtrNaming.Apply(parameter.Name, policy, SurtrNameKind.Member),
                    Description = parameterAttribute?.Description,
                    TypeDescriptor = descriptor.Descriptor,
                });
            }

            return new NativeMethodDescriptor
            {
                Name = operatorName,
                Visibility = attribute?.Visibility ?? SurtrInteropVisibility.Public,
                IsStatic = true,
                ReturnDescriptor = returnDescriptor.Descriptor,
                Parameters = surtrParameters.ToArray(),
                EntryPoint = SurtrReflectionInvoker.Create(new ReflectionMemberSlot
                {
                    Kind = ReflectionMemberKind.Method,
                    Method = method,
                    IsStatic = true,
                    ResultDescriptor = returnDescriptor,
                    Parameters = fullDescriptors,
                }),
            };
        }

        private static IEnumerable<NativeMethodDescriptor> ScanIndexer(PropertyInfo property, SurtrNamingPolicy policy, SurtrClassReference selfDescriptor)
        {
            var indexParameters = property.GetIndexParameters();
            if (indexParameters.Length != 1)
                yield break; // Surtr's operator[] is one-dimensional

            var elementDescriptor = SurtrTypeMapper.Map(property.PropertyType, policy);
            var indexDescriptor = SurtrTypeMapper.Map(indexParameters[0].ParameterType, policy);
            string indexName = indexParameters[0].Name ?? "index";

            if (property.GetMethod is { } getter && getter.IsPublic)
            {
                yield return new NativeMethodDescriptor
                {
                    Name = "op_[]",
                    IsStatic = true,
                    ReturnDescriptor = elementDescriptor.Descriptor,
                    Parameters = new[]
                    {
                        new NativeParameterDescriptor { Name = "self", TypeDescriptor = selfDescriptor.Descriptor },
                        new NativeParameterDescriptor { Name = indexName, TypeDescriptor = indexDescriptor.Descriptor },
                    },
                    EntryPoint = SurtrReflectionInvoker.Create(new ReflectionMemberSlot
                    {
                        Kind = ReflectionMemberKind.Method,
                        Method = getter,
                        IsStatic = false,
                        ResultDescriptor = elementDescriptor,
                        Parameters = new[] { indexDescriptor },
                    }),
                };
            }

            if (property.SetMethod is { } setter && setter.IsPublic)
            {
                yield return new NativeMethodDescriptor
                {
                    Name = "op_[]",
                    IsStatic = true,
                    ReturnDescriptor = SurtrClassReference.Void.Descriptor,
                    Parameters = new[]
                    {
                        new NativeParameterDescriptor { Name = "self", TypeDescriptor = selfDescriptor.Descriptor },
                        new NativeParameterDescriptor { Name = indexName, TypeDescriptor = indexDescriptor.Descriptor },
                        new NativeParameterDescriptor { Name = "value", TypeDescriptor = elementDescriptor.Descriptor },
                    },
                    EntryPoint = SurtrReflectionInvoker.Create(new ReflectionMemberSlot
                    {
                        Kind = ReflectionMemberKind.Method,
                        Method = setter,
                        IsStatic = false,
                        ResultDescriptor = SurtrClassReference.Void,
                        Parameters = new[] { indexDescriptor, elementDescriptor },
                    }),
                };
            }
        }

        private static string StripArity(string name)
        {
            int marker = name.IndexOf(SurtrClassReference.ArityMarker);
            return marker < 0 ? name : name.Substring(0, marker);
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
