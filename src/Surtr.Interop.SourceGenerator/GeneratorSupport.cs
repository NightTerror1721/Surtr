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

        internal const string NativeConstructorAttribute = "Surtr.Interop.Attributes.SurtrNativeConstructorAttribute";
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

        /// <summary>
        /// Maps a C# <c>op_*</c> name to the Surtr operator name, or null when it has no Surtr
        /// equivalent. Mirrors <c>Surtr.Interop.SurtrOperatorMapper.Map</c>.
        /// </summary>
        internal static string? MapOperator(string csharpName, int parameterCount, string returnDescriptor)
        {
            switch (csharpName)
            {
                case "op_Addition": return "op_+";
                case "op_Subtraction": return parameterCount == 1 ? "op_-u" : "op_-";
                case "op_Multiply": return "op_*";
                case "op_Division": return "op_/";
                case "op_Modulus": return "op_%";
                case "op_BitwiseAnd": return "op_&";
                case "op_BitwiseOr": return "op_|";
                case "op_ExclusiveOr": return "op_^";
                case "op_LeftShift": return "op_<<";
                case "op_RightShift": return "op_>>";
                case "op_UnsignedRightShift": return "op_>>>";
                case "op_UnaryNegation": return "op_-u";
                case "op_LogicalNot": return "op_!";
                case "op_OnesComplement": return "op_~";
                case "op_Increment": return "op_++";
                case "op_Decrement": return "op_--";
                case "op_Equality": return "op_==";
                case "op_Explicit": return "op_as$" + returnDescriptor;
                default: return null;
            }
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

        /// <summary>
        /// Whether a string is a well-formed Surtr descriptor. Mirrors SurtrClassReference's grammar
        /// so a malformed TypeDescriptor/ReturnDescriptor is caught at compile time rather than at
        /// registration.
        /// </summary>
        internal static bool IsWellFormedDescriptor(string descriptor)
            => !string.IsNullOrEmpty(descriptor) && SkipDescriptor(descriptor, 0) == descriptor.Length;

        private static int SkipDescriptor(string descriptor, int index)
        {
            if (index >= descriptor.Length)
                return -1;

            switch (descriptor[index])
            {
                case 'I':
                case 'F':
                case 'B':
                case 'C':
                case 'S':
                case 'R':
                case 'E':
                case 'V':
                    return index + 1;

                case 'A':
                    return SkipDescriptor(descriptor, index + 1);

                case 'D':
                {
                    int afterKey = SkipDescriptor(descriptor, index + 1);
                    return afterKey < 0 ? -1 : SkipDescriptor(descriptor, afterKey);
                }

                case 'T':
                    return SkipList(descriptor, index + 1);

                case 'L':
                {
                    int afterList = SkipList(descriptor, index + 1);
                    return afterList < 0 ? -1 : SkipDescriptor(descriptor, afterList);
                }

                case 'O':
                case 'N':
                {
                    int afterName = SkipFullName(descriptor, index + 1, out int arity);
                    if (afterName < 0)
                        return -1;

                    for (int i = 0; i < arity; i++)
                    {
                        afterName = SkipDescriptor(descriptor, afterName);
                        if (afterName < 0)
                            return -1;
                    }

                    return afterName;
                }

                case 'G':
                case 'H':
                    if (index + 1 >= descriptor.Length || descriptor[index + 1] < '0' || descriptor[index + 1] > '9')
                        return -1;
                    return index + 2;

                case '?':
                    if (index + 1 >= descriptor.Length)
                        return -1;
                    return descriptor[index + 1] is 'I' or 'F' or 'B' or 'C' ? index + 2 : -1;

                default:
                    return -1;
            }
        }

        private static int SkipFullName(string descriptor, int index, out int arity)
        {
            arity = 0;

            while (index < descriptor.Length)
            {
                char symbol = descriptor[index];

                if (symbol == ';')
                    return index + 1;

                if (symbol == '.')
                {
                    arity = 0;
                    index++;
                    continue;
                }

                if (symbol == '`')
                {
                    index++;
                    int digits = 0;
                    while (index < descriptor.Length && descriptor[index] >= '0' && descriptor[index] <= '9')
                    {
                        arity = (arity * 10) + (descriptor[index] - '0');
                        index++;
                        digits++;
                    }

                    if (digits == 0)
                    {
                        arity = 0;
                        return -1;
                    }

                    continue;
                }

                index++;
            }

            arity = 0;
            return -1;
        }

        private static int SkipList(string descriptor, int index)
        {
            if (index >= descriptor.Length || descriptor[index] != '(')
                return -1;

            index++;
            while (index < descriptor.Length && descriptor[index] != ')')
            {
                index = SkipDescriptor(descriptor, index);
                if (index < 0)
                    return -1;
            }

            return index < descriptor.Length ? index + 1 : -1;
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
