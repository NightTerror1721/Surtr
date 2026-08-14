#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax.Ast;

namespace Surtr.LanguageServer.Workspace
{
    /// <summary>
    /// Renders a symbol or type as the markdown shown in a hover and used as the label for a
    /// definition location. Every line starts with the value in <c>**...**</c>, which is what an
    /// LSP client renders as the signature; the lines after it describe what the thing is.
    /// </summary>
    public static class HoverFormatter
    {
        public static string FormatSymbol(Symbol symbol)
        {
            switch (symbol)
            {
                case MethodSymbol method:
                    return FormatMethod(method);

                case FieldSymbol field:
                    return FormatField(field);

                case PropertySymbol property:
                    return FormatProperty(property);

                case LocalSymbol local:
                    return "**`" + local.Name + "`** : `" + local.Type.ToDisplayString() + "`"
                        + Environment.NewLine + "local variable";

                case ParameterSymbol parameter:
                    return "**`" + parameter.Name + "`** : `" + parameter.Type.ToDisplayString() + "`"
                        + Environment.NewLine + "parameter";

                case NamedTypeSymbol type:
                    return FormatType(type);

                case AliasSymbol alias:
                    return FormatAlias(alias);

                case TypeParameterSymbol typeParameter:
                    return FormatTypeParameter(typeParameter);

                default:
                    return "**`" + symbol.Name + "`**";
            }
        }

        public static string FormatType(TypeSymbol type)
        {
            if (type is TypeParameterSymbol typeParameter)
                return FormatTypeParameter(typeParameter);

            if (type is NamedTypeSymbol named)
                return FormatNamedType(named);

            string kindLabel = type.TypeKind switch
            {
                TypeSymbolKind.Array => "array",
                TypeSymbolKind.Dictionary => "dictionary",
                TypeSymbolKind.Tuple => "tuple",
                TypeSymbolKind.Closure => "closure",
                _ => "type",
            };

            return "**`" + type.ToDisplayString() + "`**"
                + Environment.NewLine + kindLabel;
        }

        public static string FormatAlias(AliasSymbol alias)
        {
            var builder = new StringBuilder();
            builder.Append("**`").Append(alias.Name).Append("`**");
            if (alias.Target is not null)
                builder.Append(" : `").Append(alias.Target.ToDisplayString()).Append('`');
            builder.Append(Environment.NewLine).Append("type alias");
            return builder.ToString();
        }

        /// <summary>The label card for a built-in primitive reached by name rather than by symbol.</summary>
        public static string BuiltInLabel(string name)
        {
            string kind;
            switch (name)
            {
                case "int":
                case "float":
                case "bool":
                case "char":
                    kind = "primitive type";
                    break;
                case "string":
                    kind = "built-in string";
                    break;
                case "range":
                    kind = "built-in range";
                    break;
                case "unknown":
                    kind = "unknown: holds anything, cast before use";
                    break;
                default:
                    kind = "built-in type";
                    break;
            }

            return "**`" + name + "`**" + Environment.NewLine + kind;
        }

        /// <summary>What a written composite type reference is, for a hover on an annotation.</summary>
        public static string DescribeTypeShape(TypeSyntax syntax)
        {
            string kind;
            switch (syntax)
            {
                case ArrayTypeSyntax: kind = "array"; break;
                case DictTypeSyntax: kind = "dictionary"; break;
                case TupleTypeSyntax: kind = "tuple"; break;
                case ClosureTypeSyntax: kind = "closure"; break;
                case NullableTypeSyntax: kind = "nullable"; break;
                default: kind = "type"; break;
            }

            return kind;
        }

        private static string FormatMethod(MethodSymbol method)
        {
            string heading = MethodHeading(method);

            var builder = new StringBuilder();
            builder.Append("**`").Append(heading).Append('`');

            string? modifiers = MethodModifiers(method);
            if (modifiers is not null)
                builder.Append(Environment.NewLine).Append(modifiers);

            builder.Append(Environment.NewLine).Append(ContainingLabel(method.ContainingSymbol, "method", "function"));
            return builder.ToString();
        }

        /// <summary>A method's one-line signature, as <c>fun name(x: int): int</c>.</summary>
        public static string MethodHeading(MethodSymbol method)
        {
            string name;
            string typeParameters = method.TypeParameters.Count > 0
                ? "<" + JoinNames(method.TypeParameters) + ">"
                : string.Empty;

            switch (method.Role)
            {
                case MethodRole.Constructor:
                    name = "constructor";
                    break;
                case MethodRole.Operator:
                    name = "operator " + OperatorSymbol(method.Name);
                    break;
                case MethodRole.PropertyGetter:
                    return "get " + propertyNameOf(method) + "() : " + method.ReturnType.ToDisplayString();
                case MethodRole.PropertySetter:
                    return "set " + propertyNameOf(method) + "(" + ParameterText(method, 0) + ")";
                default:
                    name = "fun " + method.Name + typeParameters;
                    break;
            }

            string parameters = ParametersText(method);
            string suffix = method.ReturnType.IsVoid ? string.Empty : " : " + method.ReturnType.ToDisplayString();
            return name + parameters + suffix;
        }

        private static string? MethodModifiers(MethodSymbol method)
        {
            var parts = new List<string>();
            if (method.IsStatic)
                parts.Add("static");
            if (method.IsConst)
                parts.Add("const");
            if (method.IsInline)
                parts.Add("inline");
            if (method.IsForceInline)
                parts.Add("forceinline");
            if (method.IsNative)
                parts.Add("native");
            if (method.IsOverride)
                parts.Add("override");
            else if (method.Dispatch == MethodDispatch.Abstract)
                parts.Add("abstract");
            else if (method.Dispatch == MethodDispatch.Virtual)
                parts.Add("virtual");

            return parts.Count == 0 ? null : string.Join(" ", parts);
        }

        private static string ParametersText(MethodSymbol method)
        {
            var parts = new List<string>();
            for (int i = 0; i < method.Parameters.Count; i++)
                parts.Add(ParameterText(method, i));
            return "(" + string.Join(", ", parts) + ")";
        }

        /// <summary>One parameter's rendered text, as <c>name : type</c> with any default or vararg marker.</summary>
        public static string ParameterText(MethodSymbol method, int index)
        {
            ParameterSymbol parameter = method.Parameters[index];
            string prefix = parameter.IsVararg ? "..." : string.Empty;
            string suffix = parameter.HasDefaultValue ? " = " + FormatDefault(parameter.DefaultValue) : string.Empty;
            return prefix + parameter.Name + " : " + parameter.Type.ToDisplayString() + suffix;
        }

        private static string FormatField(FieldSymbol field)
        {
            var builder = new StringBuilder();
            builder.Append("**`").Append(field.Name).Append("`** : `").Append(field.Type.ToDisplayString()).Append('`');

            var modifiers = new List<string>();
            if (field.IsStatic)
                modifiers.Add("static");
            if (field.IsReadOnly)
                modifiers.Add("readonly");
            if (field.IsNative)
                modifiers.Add("native");
            if (modifiers.Count > 0)
                builder.Append(Environment.NewLine).Append(string.Join(" ", modifiers));

            builder.Append(Environment.NewLine).Append(ContainingLabel(field.ContainingSymbol, "field", "variable"));
            return builder.ToString();
        }

        private static string FormatProperty(PropertySymbol property)
        {
            var builder = new StringBuilder();
            builder.Append("**`").Append(property.Name).Append("`** : `").Append(property.Type.ToDisplayString()).Append('`');

            if (property.IsStatic)
                builder.Append(Environment.NewLine).Append("static");

            var accessors = new List<string>();
            if (property.Getter is not null)
                accessors.Add("get");
            if (property.Setter is not null)
                accessors.Add("set");
            if (accessors.Count > 0)
                builder.Append(Environment.NewLine).Append(string.Join(" / ", accessors));

            builder.Append(Environment.NewLine).Append(ContainingLabel(property.ContainingSymbol, "property", "property"));
            return builder.ToString();
        }

        private static string FormatNamedType(NamedTypeSymbol type)
        {
            string kind = TypeKindLabel(type.TypeKind);

            var builder = new StringBuilder();
            string display = type.ToDisplayString();
            builder.Append("**`").Append(display).Append("`**");
            builder.Append(Environment.NewLine).Append(kind);

            var relations = new List<string>();
            if (type.BaseType is not null)
                relations.Add("extends `" + type.BaseType.ToDisplayString() + "`");
            if (type.Interfaces.Count > 0)
                relations.Add("implements " + "`" + string.Join("`, `", TypeNames(type.Interfaces)) + "`");
            if (relations.Count > 0)
                builder.Append(Environment.NewLine).Append(string.Join(", ", relations));

            if (type.ContainingModule is not null && type.SpecialType == SpecialType.None)
                builder.Append(Environment.NewLine).Append("in module `" + type.ContainingModule.Path + "`");

            return builder.ToString();
        }

        private static string FormatTypeParameter(TypeParameterSymbol typeParameter)
        {
            var builder = new StringBuilder();
            builder.Append("**`").Append(typeParameter.Name).Append("`**");

            if (typeParameter.Constraints.Count > 0)
            {
                var names = new List<string>();
                foreach (TypeSymbol constraint in typeParameter.Constraints)
                    names.Add("`" + constraint.ToDisplayString() + "`");
                builder.Append(Environment.NewLine).Append("constraint: " + string.Join(", ", names));
            }

            builder.Append(Environment.NewLine).Append("type parameter");
            return builder.ToString();
        }

        private static string TypeKindLabel(TypeSymbolKind kind)
        {
            switch (kind)
            {
                case TypeSymbolKind.Class: return "class";
                case TypeSymbolKind.Interface: return "interface";
                case TypeSymbolKind.Enum: return "enum";
                case TypeSymbolKind.ValueClass: return "value class";
                case TypeSymbolKind.Singleton: return "singleton";
                case TypeSymbolKind.Native: return "native type";
                default: return "type";
            }
        }

        private static string ContainingLabel(Symbol? containing, string memberWord, string moduleWord)
        {
            if (containing is NamedTypeSymbol type)
                return memberWord + " of `" + type.ToDisplayString() + "`";
            if (containing is ModuleSymbol module)
                return moduleWord + " in module `" + module.Path + "`";
            return moduleWord + " in module";
        }

        /// <summary>Renders a user-written name like <c>op_+</c> back to its source spelling.</summary>
        private static string OperatorSymbol(string name)
        {
            string symbol = name.Substring(OperatorNames.Prefix.Length);
            if (symbol.EndsWith(OperatorNames.UnarySuffix, StringComparison.Ordinal))
                symbol = symbol.Substring(0, symbol.Length - OperatorNames.UnarySuffix.Length);
            return symbol;
        }

        private static string FormatDefault(object? value)
        {
            if (value is null)
                return "null";
            return value switch
            {
                bool boolean => boolean ? "true" : "false",
                char character => "'" + character + "'",
                string text => "\"" + text + "\"",
                _ => value.ToString() ?? "null",
            };
        }

        private static string propertyNameOf(MethodSymbol accessor)
        {
            string name = accessor.Name;
            return name.StartsWith("get_", StringComparison.Ordinal) ? name.Substring(4) : name;
        }

        private static IEnumerable<string> TypeNames(IReadOnlyList<NamedTypeSymbol> types)
        {
            foreach (NamedTypeSymbol type in types)
                yield return type.ToDisplayString();
        }

        private static string JoinNames(IReadOnlyList<TypeParameterSymbol> parameters)
        {
            var names = new List<string>();
            foreach (TypeParameterSymbol parameter in parameters)
                names.Add(parameter.Name);
            return string.Join(", ", names);
        }
    }
}
