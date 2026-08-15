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
    /// definition location. A card is one fenced declaration followed by lines describing it.
    /// </summary>
    public static class HoverFormatter
    {
        /// <summary>The language a fenced block is tagged with, so an editor colours it.</summary>
        private const string LanguageId = "surtr";

        /// <summary>
        /// Ends a line of description. Markdown treats a bare newline as a space, so without the two
        /// trailing spaces every line of a card runs together into one paragraph.
        /// </summary>
        private const string Break = "  \n";

        /// <summary>
        /// Wraps a declaration in a fenced block tagged with the language.
        /// </summary>
        /// <remarks>
        /// This is what gets a hover coloured: a client renders a tagged block with the same grammar
        /// it highlights the file with, while inline code only comes out monospaced. Both spellings
        /// were in use — <c>SymbolResolver</c> fences the source text it reads, this type used to
        /// emit inline code — so the same hover looked different depending on which of the two
        /// answered it.
        /// </remarks>
        private static string Fence(string code) => "```" + LanguageId + "\n" + code + "\n```";

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
                    return Fence(local.Name + ": " + local.Type.ToDisplayString()) + Break + "local variable";

                case ParameterSymbol parameter:
                    return Fence(parameter.Name + ": " + parameter.Type.ToDisplayString()) + Break + "parameter";

                case NamedTypeSymbol type:
                    return FormatType(type);

                case AliasSymbol alias:
                    return FormatAlias(alias);

                case TypeParameterSymbol typeParameter:
                    return FormatTypeParameter(typeParameter);

                default:
                    return Fence(symbol.Name);
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

            return Fence(type.ToDisplayString()) + Break + kindLabel;
        }

        public static string FormatAlias(AliasSymbol alias)
        {
            string declaration = alias.Target is not null
                ? "alias " + alias.Name + " = " + alias.Target.ToDisplayString()
                : "alias " + alias.Name;

            return Fence(declaration) + Break + "type alias";
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

            return Fence(name) + Break + kind;
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
            builder.Append(Fence(heading));

            string? modifiers = MethodModifiers(method);
            if (modifiers is not null)
                builder.Append(Break).Append(modifiers);

            builder.Append(Break).Append(ContainingLabel(method.ContainingSymbol, "method", "function"));
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
            builder.Append(Fence(field.Name + ": " + field.Type.ToDisplayString()));

            var modifiers = new List<string>();
            if (field.IsStatic)
                modifiers.Add("static");
            if (field.IsReadOnly)
                modifiers.Add("readonly");
            if (field.IsNative)
                modifiers.Add("native");
            if (modifiers.Count > 0)
                builder.Append(Break).Append(string.Join(" ", modifiers));

            builder.Append(Break).Append(ContainingLabel(field.ContainingSymbol, "field", "variable"));
            return builder.ToString();
        }

        private static string FormatProperty(PropertySymbol property)
        {
            var accessors = new List<string>();
            if (property.Getter is not null)
                accessors.Add("get;");
            if (property.Setter is not null)
                accessors.Add("set;");

            var builder = new StringBuilder();
            string declaration = property.Name + ": " + property.Type.ToDisplayString();
            if (accessors.Count > 0)
                declaration += " { " + string.Join(" ", accessors) + " }";

            builder.Append(Fence(declaration));

            if (property.IsStatic)
                builder.Append(Break).Append("static");

            builder.Append(Break).Append(ContainingLabel(property.ContainingSymbol, "property", "property"));
            return builder.ToString();
        }

        private static string FormatNamedType(NamedTypeSymbol type)
        {
            string kind = TypeKindLabel(type.TypeKind);

            var builder = new StringBuilder();
            builder.Append(Fence(kind + " " + type.ToDisplayString()));

            var relations = new List<string>();
            if (type.BaseType is not null)
                relations.Add("extends `" + type.BaseType.ToDisplayString() + "`");
            if (type.Interfaces.Count > 0)
                relations.Add("implements " + "`" + string.Join("`, `", TypeNames(type.Interfaces)) + "`");
            if (relations.Count > 0)
                builder.Append(Break).Append(string.Join(", ", relations));

            if (type.ContainingModule is not null && type.SpecialType == SpecialType.None)
                builder.Append(Break).Append("in module `" + type.ContainingModule.Path + "`");

            return builder.ToString();
        }

        private static string FormatTypeParameter(TypeParameterSymbol typeParameter)
        {
            var builder = new StringBuilder();
            builder.Append(Fence(typeParameter.Name));

            if (typeParameter.Constraints.Count > 0)
            {
                var names = new List<string>();
                foreach (TypeSymbol constraint in typeParameter.Constraints)
                    names.Add("`" + constraint.ToDisplayString() + "`");
                builder.Append(Break).Append("constraint: " + string.Join(", ", names));
            }

            builder.Append(Break).Append("type parameter");
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
