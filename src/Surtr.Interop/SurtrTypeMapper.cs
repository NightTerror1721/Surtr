#nullable enable

using Surtr.Interop.Attributes;
using Surtr.Runtime.Classes;
using System;
using System.Reflection;

namespace Surtr.Interop
{
    /// <summary>
    /// Derives a canonical Surtr descriptor from a CLR type, and the full name of a native type from
    /// its <see cref="SurtrNativeTypeAttribute"/>. Shared by the reflection scanner; the source
    /// generator mirrors this at compile time.
    /// </summary>
    public static class SurtrTypeMapper
    {
        /// <summary>Derives the descriptor for a CLR type, without reading any override attribute.</summary>
        public static SurtrClassReference Map(Type type, SurtrNamingPolicy policy)
        {
            if (type == typeof(sbyte) || type == typeof(byte) || type == typeof(short)
                || type == typeof(ushort) || type == typeof(int) || type == typeof(uint)
                || type == typeof(long) || type == typeof(ulong))
                return SurtrClassReference.Integer;

            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return SurtrClassReference.Float;

            if (type == typeof(bool))
                return SurtrClassReference.Boolean;

            if (type == typeof(char))
                return SurtrClassReference.Character;

            if (type == typeof(string))
                return SurtrClassReference.String;

            if (type == typeof(object) || type == typeof(void))
                return type == typeof(void) ? SurtrClassReference.Void : SurtrClassReference.Native("surtr:native");

            var nullable = Nullable.GetUnderlyingType(type);
            if (nullable is not null)
                return SurtrClassReference.Nullable(Map(nullable, policy));

            if (type.IsArray)
            {
                var element = type.GetElementType()!;
                return SurtrClassReference.Array(Map(element, policy));
            }

            if (type.IsEnum)
            {
                var attribute = type.GetCustomAttribute<SurtrNativeTypeAttribute>();
                return SurtrClassReference.Native(attribute is null ? type.Name : FullNameOf(type, attribute, policy));
            }

            if (typeof(Delegate).IsAssignableFrom(type) && type != typeof(Delegate) && type != typeof(MulticastDelegate))
            {
                var invoke = type.GetMethod("Invoke")!;
                return MapClosure(invoke, policy);
            }

            var typeAttribute = type.GetCustomAttribute<SurtrNativeTypeAttribute>();
            if (typeAttribute is not null)
                return SurtrClassReference.Native(FullNameOf(type, typeAttribute, policy));

            return SurtrClassReference.Native("surtr:native");
        }

        /// <summary>
        /// The full name a native type's descriptor carries: <c>Module:Name</c> or bare <c>Name</c>.
        /// </summary>
        public static string FullNameOf(Type type, SurtrNativeTypeAttribute attribute, SurtrNamingPolicy policy)
        {
            var effective = attribute.NamingPolicy ?? policy;
            string name = attribute.Name ?? SurtrNaming.Apply(type.Name, effective, SurtrNameKind.Type);
            return attribute.Module is null ? name : attribute.Module + ":" + name;
        }

        private static SurtrClassReference MapClosure(MethodInfo invoke, SurtrNamingPolicy policy)
        {
            var parameters = invoke.GetParameters();
            var parameterTypes = new SurtrClassReference[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                parameterTypes[i] = Map(parameters[i].ParameterType, policy);

            return SurtrClassReference.Closure(Map(invoke.ReturnType, policy), parameterTypes);
        }
    }
}
