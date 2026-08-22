#nullable enable

using Microsoft.CodeAnalysis;
using System.Text;

namespace Surtr.Interop.SourceGenerator
{
    /// <summary>
    /// Compile-time mirrors of <c>Surtr.Interop.Attributes.SurtrNaming</c> and
    /// <c>Surtr.Interop.SurtrTypeMapper</c>. Kept in step by hand on purpose, the same way the
    /// runtime and compiler share other rules (a generator cannot call the runtime's methods).
    /// </summary>
    internal static class GeneratorSupport
    {
        internal const string NativeTypeAttribute = "Surtr.Interop.Attributes.SurtrNativeTypeAttribute";
        internal const string NativeMethodAttribute = "Surtr.Interop.Attributes.SurtrNativeMethodAttribute";
        internal const string NativeFieldAttribute = "Surtr.Interop.Attributes.SurtrNativeFieldAttribute";
        internal const string NativePropertyAttribute = "Surtr.Interop.Attributes.SurtrNativePropertyAttribute";
        internal const string NativeParameterAttribute = "Surtr.Interop.Attributes.SurtrNativeParameterAttribute";
        internal const string NativeIgnoreAttribute = "Surtr.Interop.Attributes.SurtrNativeIgnoreAttribute";

        // Mirrors SurtrNamingPolicy values.
        internal const int PolicyDefault = 0;
        internal const int PolicySurtr = 1;
        internal const int PolicyPascalCase = 2;
        internal const int PolicyCamelCase = 3;
        internal const int PolicySnakeCase = 4;
        internal const int PolicyLowerCase = 5;
        internal const int PolicyUpperCase = 6;

        internal static string Apply(string name, int policy, bool isMember)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            if (policy == PolicyDefault)
                policy = PolicySurtr;

            switch (policy)
            {
                case PolicySurtr:
                    return isMember ? LowerFirst(name) : name;
                case PolicyPascalCase:
                    return name;
                case PolicyCamelCase:
                    return LowerFirst(name);
                case PolicySnakeCase:
                    return ToSnakeCase(name);
                case PolicyLowerCase:
                    return name.ToLowerInvariant();
                case PolicyUpperCase:
                    return name.ToUpperInvariant();
                default:
                    return name;
            }
        }

        private static string LowerFirst(string name)
        {
            char first = name[0];
            char lowered = char.ToLowerInvariant(first);
            return first == lowered ? name : lowered + name.Substring(1);
        }

        private static string ToSnakeCase(string name)
        {
            var builder = new StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                char current = name[i];
                if (char.IsUpper(current))
                {
                    bool previousIsLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                    bool nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);

                    if (builder.Length > 0 && builder[builder.Length - 1] != '_' && (previousIsLowerOrDigit || nextIsLower))
                        builder.Append('_');

                    builder.Append(char.ToLowerInvariant(current));
                }
                else
                {
                    builder.Append(current);
                }
            }

            return builder.ToString();
        }

        /// <summary>Derives a Surtr descriptor from an <see cref="ITypeSymbol"/>, mirroring SurtrTypeMapper.Map.</summary>
        internal static string MapType(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                    return "I";

                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                    return "F";

                case SpecialType.System_Boolean:
                    return "B";

                case SpecialType.System_Char:
                    return "C";

                case SpecialType.System_String:
                    return "S";

                case SpecialType.System_Void:
                    return "V";

                case SpecialType.System_Object:
                    return "Nsurtr:native;";
            }

            if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                var attribute = FindAttribute(enumType, NativeTypeAttribute);
                return "N" + (attribute is null ? enumType.Name : FullNameOf(enumType, attribute)) + ";";
            }

            if (type is IArrayTypeSymbol array)
                return "A" + MapType(array.ElementType);

            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
                return "?" + MapType(nullable.TypeArguments[0]);

            if (type.TypeKind == TypeKind.Delegate && type is INamedTypeSymbol { DelegateInvokeMethod: { } invoke })
            {
                var parameterTypes = new StringBuilder();
                foreach (var parameter in invoke.Parameters)
                    parameterTypes.Append(MapType(parameter.Type));

                return "L(" + parameterTypes + ")" + MapType(invoke.ReturnType);
            }

            if (type is INamedTypeSymbol named)
            {
                var attribute = FindAttribute(named, NativeTypeAttribute);
                if (attribute is not null)
                    return "N" + FullNameOf(named, attribute) + ";";
            }

            return "Nsurtr:native;";
        }

        internal static AttributeData? FindAttribute(ISymbol symbol, string fullName)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString() == fullName)
                    return attribute;
            }

            return null;
        }

        internal static string? GetStringNamed(AttributeData? attribute, string name)
        {
            if (attribute is null)
                return null;

            foreach (var pair in attribute.NamedArguments)
            {
                if (pair.Key == name && pair.Value.Value is string text)
                    return text;
            }

            return null;
        }

        internal static int? GetIntNamed(AttributeData? attribute, string name)
        {
            if (attribute is null)
                return null;

            foreach (var pair in attribute.NamedArguments)
            {
                if (pair.Key == name && pair.Value.Value is int value)
                    return value;
            }

            return null;
        }

        private static string FullNameOf(INamedTypeSymbol type, AttributeData attribute)
        {
            int policy = GetIntNamed(attribute, "NamingPolicy") ?? PolicyDefault;
            string? name = GetStringNamed(attribute, "Name") ?? Apply(type.Name, policy, isMember: false);
            string? module = GetStringNamed(attribute, "Module");
            return module is null ? name : module + ":" + name;
        }
    }
}
