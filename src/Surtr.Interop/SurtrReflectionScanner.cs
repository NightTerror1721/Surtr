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

            if (attribute.Inline && descriptor.Kind == NativeTypeKind.Struct)
            {
                descriptor.IsInline = true;
                descriptor.ClrType = type;
            }
            else if (attribute.Inline)
            {
                throw new InvalidOperationException(
                    $"'{type.FullName}' is marked Inline, but only a struct has an inline value representation.");
            }

            descriptor.Members = ScanMembers(type, effectivePolicy, SurtrClassReference.Native(fullName), descriptor.IsInline);

            if (descriptor.IsInline && !descriptor.Members.Any(static m => m is NativeValueFieldDescriptor))
            {
                throw new InvalidOperationException(
                    $"'{type.FullName}' is marked Inline but exposes no instance field, so it has no slots to be. "
                    + "An inline value type is its fields.");
            }

            return descriptor;
        }

        /// <summary>
        /// Fills in the inline layouts a dispatch record needs to walk its arguments by width.
        /// </summary>
        /// <remarks>
        /// Without these the invoker maps parameter <c>i</c> to slot <c>i</c>, which is right only
        /// while every argument is one slot wide. A parameter, a receiver or a result typed as an
        /// inline struct occupies its whole block, so each one that does carries the layout that
        /// rebuilds it. Computed once here, at scan time, rather than per call.
        /// </remarks>
        private static ReflectionMemberSlot WithLayouts(ReflectionMemberSlot slot, MethodBase method, SurtrNamingPolicy policy)
        {
            if (!slot.IsStatic && method.DeclaringType is { } owner)
                slot.ReceiverLayout = SurtrValueLayout.For(owner, policy);

            var parameters = method.GetParameters();
            var layouts = new SurtrValueLayout?[parameters.Length];
            bool any = slot.ReceiverLayout is not null;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].IsOut || parameters[i].ParameterType.IsByRef)
                    continue;

                layouts[i] = SurtrValueLayout.For(parameters[i].ParameterType, policy);
                any |= layouts[i] is not null;
            }

            if (any)
                slot.ParameterLayouts = layouts;

            if (method is MethodInfo info)
                slot.ResultLayout = SurtrValueLayout.For(info.ReturnType, policy);

            return slot;
        }

        /// <summary>
        /// Whether a field of <paramref name="fieldType"/> can live in an inline block.
        /// </summary>
        /// <remarks>
        /// A Surtr primitive occupies one slot and needs no indirection; another inline struct
        /// folds its own slots into the run. Everything else - a string, an array, a class, a
        /// boxed struct - is a reference to something the CLR struct owns, and reconstructing that
        /// struct out of slots would mean deciding who owns the referent. Refused rather than
        /// half-exposed, which is what the v1 scope buys.
        /// </remarks>
        private static bool IsInlineFieldType(Type fieldType, SurtrNamingPolicy policy)
        {
            var code = SurtrTypeMapper.Map(fieldType, policy).TypeCode;
            if (code is SurtrValueTypeCode.Integer or SurtrValueTypeCode.Float
                or SurtrValueTypeCode.Boolean or SurtrValueTypeCode.Character)
            {
                return true;
            }

            return fieldType.IsValueType && !fieldType.IsEnum && TypeAttribute(fieldType) is { Inline: true };
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

        private static NativeMemberDescriptor[] ScanMembers(Type type, SurtrNamingPolicy policy, SurtrClassReference selfDescriptor, bool inline)
        {
            var members = new List<NativeMemberDescriptor>();

            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                // An inline value type's constructor is deliberately not exposed. A Surtr
                // constructor is reached by allocating first and running the body against the new
                // instance as its receiver, and an inline value has nothing to allocate and no
                // receiver to fill - it *is* its result. Wiring that means a construction protocol
                // this layer does not have yet (see the note in ScanMembers' own remarks), and a
                // static factory covers it exactly: a static method returning the struct already
                // works, and returns the block flat.
                if (inline)
                    continue;

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
                // An inline type's *instance* fields are its storage, so they become real slots
                // rather than accessor pairs. A static one is not part of any block - it belongs to
                // the type, not to a value of it - so it keeps the ordinary native-field shape.
                if (inline && !field.IsStatic)
                {
                    if (ScanValueField(type, field, policy) is { } slot)
                        members.Add(slot);

                    continue;
                }

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

            var slot = WithLayouts(
                new ReflectionMemberSlot
                {
                    Kind = ReflectionMemberKind.Method,
                    Method = method as MethodInfo,
                    IsStatic = isStatic,
                    ResultDescriptor = returnDescriptor,
                    Parameters = fullDescriptors,
                },
                method,
                memberPolicy);

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
                EntryPoint = SurtrReflectionInvoker.Create(WithLayouts(
                    new ReflectionMemberSlot
                    {
                        Kind = ReflectionMemberKind.Method,
                        Method = method,
                        IsStatic = false,
                        ResultDescriptor = SurtrClassReference.Integer,
                        Parameters = new[] { parameterDescriptor },
                    },
                    method,
                    policy)),
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
                EntryPoint = SurtrReflectionInvoker.Create(WithLayouts(
                    new ReflectionMemberSlot
                    {
                        Kind = ReflectionMemberKind.Method,
                        Method = method,
                        IsStatic = true,
                        ResultDescriptor = returnDescriptor,
                        Parameters = fullDescriptors,
                    },
                    method,
                    policy)),
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

        /// <summary>
        /// Scans one instance field of an inline value type into the slot it becomes.
        /// </summary>
        /// <remarks>
        /// A field the host asked to ignore is refused rather than skipped: skipping it would drop
        /// a slot out of the middle of the block, and the CLR struct could no longer be rebuilt
        /// from what is left. An inline type is all of its fields or none of them.
        /// </remarks>
        private static NativeValueFieldDescriptor? ScanValueField(Type owner, FieldInfo field, SurtrNamingPolicy policy)
        {
            var ignore = field.GetCustomAttribute<SurtrNativeIgnoreAttribute>();
            var attribute = field.GetCustomAttribute<SurtrNativeFieldAttribute>();

            if (ignore is not null || (attribute is not null && !attribute.Expose))
            {
                throw new InvalidOperationException(
                    $"'{owner.FullName}.{field.Name}' cannot be hidden: '{owner.FullName}' is an inline value type, "
                    + "so every instance field is one of its slots and dropping one would leave the CLR struct "
                    + "impossible to rebuild.");
            }

            var memberPolicy = attribute?.NamingPolicy ?? policy;

            var typeReference = attribute?.TypeDescriptor is { } declared
                ? SurtrClassReference.FromDescriptor(declared)
                : SurtrTypeMapper.Map(field.FieldType, memberPolicy);

            if (attribute?.TypeDescriptor is null && !IsInlineFieldType(field.FieldType, memberPolicy))
            {
                throw new InvalidOperationException(
                    $"'{owner.FullName}.{field.Name}' is a '{field.FieldType.Name}', which has no inline representation. "
                    + "An inline value type's fields must be Surtr primitives or other structs exposed with Inline = true.");
            }

            return new NativeValueFieldDescriptor
            {
                Name = attribute?.Name ?? SurtrNaming.Apply(field.Name, memberPolicy, SurtrNameKind.Member),
                Description = attribute?.Description,
                Visibility = attribute?.Visibility ?? SurtrInteropVisibility.Public,
                IsStatic = false,
                TypeDescriptor = typeReference.Descriptor,
                Field = field,
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
                descriptor.Getter = SurtrReflectionInvoker.Create(WithLayouts(new ReflectionMemberSlot { Kind = ReflectionMemberKind.PropertyGetter, Method = property.GetMethod, IsStatic = isStatic, ResultDescriptor = typeReference }, property.GetMethod!, memberPolicy));

            if (hasSetter)
                descriptor.Setter = SurtrReflectionInvoker.Create(WithLayouts(new ReflectionMemberSlot { Kind = ReflectionMemberKind.PropertySetter, Method = property.SetMethod, IsStatic = isStatic, ResultDescriptor = typeReference }, property.SetMethod!, memberPolicy));

            return descriptor;
        }
    }
}
