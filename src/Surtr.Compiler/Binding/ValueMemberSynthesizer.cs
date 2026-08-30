#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax.Ast;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Builds the value members a <c>@Value</c> class gets without declaring them (§11.1): the
    /// structural <c>equals</c>, a combined <c>hashCode</c>, and a <c>toString</c>. These are real
    /// methods — created by the binder, given bound bodies, emitted into the image like any other
    /// — rather than lowering at a call site, so they are callable, overridable by writing one's
    /// own, and consistent with the <c>==</c>/<c>!=</c> operators the same mark already turns
    /// structural.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The names are the same real names <c>object</c> declares (the language's root class,
    /// stateless and extended by every class by default): <c>equals</c>, <c>hashCode</c>,
    /// <c>toString</c>. A user declaring one of those on their own <c>@Value</c> class shadows this
    /// synthesis outright - the check that gates synthesis (<c>AddValueMembers</c> in
    /// <c>Binder.cs</c>) looks for that name among the class's own members first, so writing your
    /// own is what "overridable by writing one's own" means here; nothing is emitted twice.
    /// </para>
    /// <para>
    /// Three invariants tie the synthesized members to the operator: <c>equals</c> and <c>==</c>
    /// share one field-by-field algorithm, so they always agree; <c>hashCode</c> is an FNV-1a fold
    /// over a per-field hash, so equal values hash equal (the hard requirement for a hash — the
    /// reverse is allowed to collide); and <c>toString</c> renders <c>Name(field=value, ...)</c>.
    /// </para>
    /// </remarks>
    internal static class ValueMemberSynthesizer
    {
        internal const string EqualsName = "equals";
        internal const string HashCodeName = "hashCode";
        internal const string ToDisplayStringName = "toString";

        /// <summary>
        /// The syntax position every synthesized bound node gets. These methods have no source to
        /// point at; the emitter already tolerates a null syntax (it defaults the span for a
        /// diagnostic), so one shared sentinel keeps the builder free of per-call null forgiving.
        /// </summary>
        private static readonly SyntaxNode NoSyntax = null!;

        /// <summary>The instance state equality and display walk: each class's own fields, base first.</summary>
        /// <remarks>
        /// Backing fields count, because an auto-property's storage is the value its reader sees;
        /// consts are no slot at all (§7.1) and the compiler's other synthetics name nothing an
        /// author wrote. The mirror of what <see cref="BodyBinder"/> walks for <c>==</c>, so the
        /// two never disagree on what makes two values equal.
        /// </remarks>
        internal static List<FieldSymbol> FieldsOf(NamedTypeSymbol type)
        {
            var fields = new List<FieldSymbol>();

            void Collect(NamedTypeSymbol current)
            {
                foreach (Symbol member in current.Definition.Members)
                {
                    if (member is not FieldSymbol field || field.IsStatic || field.IsConst)
                        continue;

                    if (field.IsSynthetic && !IsABackingField(current, field))
                        continue;

                    fields.Add(field);
                }

                if (current.BaseType?.NonNullable is NamedTypeSymbol baseType && baseType.TypeKind == TypeSymbolKind.Class)
                    Collect(baseType);
            }

            Collect(type);
            return fields;
        }

        private static bool IsABackingField(NamedTypeSymbol type, FieldSymbol candidate)
        {
            foreach (Symbol member in type.Definition.Members)
            {
                if (member is PropertySymbol property && ReferenceEquals(property.BackingField, candidate))
                    return true;
            }

            return false;
        }

        /// <summary>Builds <c>return expr;</c> wrapped in a block, all with the null (synthetic) syntax.</summary>
        private static BoundStatement ReturnBlock(BoundExpression value)
            => new BoundBlockStatement(NoSyntax, new BoundStatement[] { new BoundReturnStatement(NoSyntax, value) });

        /// <summary>A <c>this</c> reference of the given class, for the synthetic member's receiver.</summary>
        private static BoundExpression This(TypeSymbolFactory factory, NamedTypeSymbol type)
            => new BoundThisExpression(NoSyntax, type, isSuper: false);

        /// <summary>Reads <c>this.field</c>.</summary>
        private static BoundExpression ThisField(BoundExpression receiver, FieldSymbol field)
            => new BoundFieldExpression(NoSyntax, receiver, field);

        /// <summary>The string a field renders as: its <c>toString()</c> for primitives, itself for strings, the nested <c>toString</c> for a <c>@Value</c>.</summary>
        private static BoundExpression DisplayOf(TypeSymbolFactory factory, MemberLookup lookup, BoundExpression fieldValue, TypeSymbol fieldType)
        {
            switch (fieldType.NonNullable.SpecialType)
            {
                case SpecialType.Int:
                case SpecialType.Float:
                case SpecialType.Bool:
                case SpecialType.Char:
                case SpecialType.Range:
                {
                    foreach (var toString in lookup.FindMethods(fieldType.NonNullable, "toString"))
                    {
                        if (toString.Parameters.Count == 0)
                            return new BoundCallExpression(NoSyntax, fieldValue, toString, Array.Empty<BoundExpression>(), isVirtual: false);
                    }

                    break;
                }

                case SpecialType.String:
                    return fieldValue;
            }

            if (fieldType.NonNullable is NamedTypeSymbol fieldClass
                && fieldClass.TypeKind == TypeSymbolKind.Class
                && BuiltInAttributes.IsMarkedValue(fieldClass))
            {
                foreach (var display in lookup.FindMethods(fieldClass, ToDisplayStringName))
                {
                    if (display.Parameters.Count == 0)
                        return new BoundCallExpression(NoSyntax, fieldValue, display, Array.Empty<BoundExpression>(), isVirtual: false);
                }
            }

            return new BoundLiteralExpression(NoSyntax, factory.String, "(...)");
        }

        /// <summary>Concatenates <paramref name="parts"/> with <c>+</c> (strings).</summary>
        private static BoundExpression Concat(TypeSymbolFactory factory, IReadOnlyList<BoundExpression> parts)
        {
            var result = parts[0];
            for (int i = 1; i < parts.Count; i++)
                result = new BoundBinaryExpression(NoSyntax, BinaryOperator.Add, result, parts[i], factory.String);

            return result;
        }

        /// <summary>An integer literal, for hashing constants and separators.</summary>
        private static BoundExpression IntLiteral(TypeSymbolFactory factory, long value)
            => new BoundLiteralExpression(NoSyntax, factory.Int, value);

        /// <summary>A per-field integer hash, defined so equal fields always produce the same value.</summary>
        /// <remarks>
        /// Numeric fields hash as themselves, strings by their length (deterministic — equal
        /// strings share it), a nested <c>@Value</c> by its own <c>hashCode</c>, and everything
        /// else — <c>bool</c>, <c>float</c>, a reference — by a position-dependent constant. The
        /// invariant that matters, equal <c>equals</c>-values hash equal, holds by construction;
        /// collisions beyond that are a quality trade rather than a correctness one.
        /// </remarks>
        private static BoundExpression FieldHash(
            TypeSymbolFactory factory,
            MemberLookup lookup,
            BoundExpression fieldValue,
            TypeSymbol fieldType,
            int position)
        {
            switch (fieldType.NonNullable.SpecialType)
            {
                case SpecialType.Int:
                    return fieldValue;

                case SpecialType.String:
                {
                    foreach (var length in lookup.FindProperty(fieldType.NonNullable, "length") is PropertySymbol p ? new[] { p } : Array.Empty<PropertySymbol>())
                        return new BoundPropertyExpression(NoSyntax, fieldValue, length, isVirtualGet: false, isVirtualSet: false);

                    break;
                }
            }

            if (fieldType.NonNullable is NamedTypeSymbol fieldClass
                && fieldClass.TypeKind == TypeSymbolKind.Class
                && BuiltInAttributes.IsMarkedValue(fieldClass))
            {
                foreach (var hashCode in lookup.FindMethods(fieldClass, HashCodeName))
                {
                    if (hashCode.Parameters.Count == 0)
                        return new BoundCallExpression(NoSyntax, fieldValue, hashCode, Array.Empty<BoundExpression>(), isVirtual: false);
                }
            }

            return IntLiteral(factory, position + 1);
        }

        /// <summary>
        /// Builds the <c>equals(other: object?)</c> body: exactly the structural comparison
        /// <c>==</c> means, guarded by a real type test rather than a same-type overload.
        /// </summary>
        /// <remarks>
        /// A real override of <c>object.equals</c> (§4.8/<c>Compiler-Plan.md</c>), not a same-name
        /// overload typed against this class: the parameter is <c>object?</c>, so <c>other</c> is
        /// narrowed with an <c>is</c> test before any field on it is read - a value of a different
        /// (or no) type answers <see langword="false"/> the same way a mismatched
        /// <c>ReferenceEqual</c> would, without ever reaching the field walk.
        /// </remarks>
        internal static BoundStatement EqualsBody(
            TypeSymbolFactory factory,
            NamedTypeSymbol type,
            List<FieldSymbol> fields,
            MethodSymbol method)
        {
            var receiver = This(factory, type);
            var argument = new BoundParameterExpression(NoSyntax, method.Parameters[0]);

            var same = new BoundBinaryExpression(NoSyntax, BinaryOperator.ReferenceEqual, receiver, argument, factory.Bool);

            BoundExpression guarded = new BoundTypeTestExpression(NoSyntax, argument, type, factory.Bool);
            var typedArgument = new BoundConversionExpression(
                NoSyntax, argument, type, Conversion.Of(ConversionKind.ExplicitReference), isExplicit: false);

            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                var pair = new BoundBinaryExpression(
                    NoSyntax, BinaryOperator.Equal,
                    ThisField(receiver, field),
                    ThisField(typedArgument, field),
                    factory.Bool);
                guarded = new BoundBinaryExpression(NoSyntax, BinaryOperator.LogicalAnd, guarded, pair, factory.Bool);
            }

            return ReturnBlock(new BoundBinaryExpression(NoSyntax, BinaryOperator.LogicalOr, same, guarded, factory.Bool));
        }

        /// <summary>Builds the <c>hashCode()</c> body: FNV-1a over the per-field hashes.</summary>
        internal static BoundStatement HashCodeBody(
            TypeSymbolFactory factory,
            MemberLookup lookup,
            NamedTypeSymbol type,
            List<FieldSymbol> fields,
            MethodSymbol method)
        {
            var receiver = This(factory, type);
            BoundExpression hash = IntLiteral(factory, 0);

            for (int i = 0; i < fields.Count; i++)
            {
                var fieldHash = FieldHash(factory, lookup, ThisField(receiver, fields[i]), fields[i].Type, i);
                var multiplied = new BoundBinaryExpression(NoSyntax, BinaryOperator.Multiply, hash, IntLiteral(factory, 16777619L), factory.Int);
                hash = new BoundBinaryExpression(NoSyntax, BinaryOperator.BitXor, multiplied, fieldHash, factory.Int);
            }

            return ReturnBlock(hash);
        }

        /// <summary>Builds the <c>toString()</c> body: <c>Name(field=value, ...)</c>.</summary>
        internal static BoundStatement ToDisplayStringBody(
            TypeSymbolFactory factory,
            MemberLookup lookup,
            NamedTypeSymbol type,
            List<FieldSymbol> fields,
            MethodSymbol method)
        {
            var receiver = This(factory, type);

            var parts = new List<BoundExpression>
            {
                new BoundLiteralExpression(NoSyntax, factory.String, type.Name + "("),
            };

            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0)
                    parts.Add(new BoundLiteralExpression(NoSyntax, factory.String, ", "));

                parts.Add(new BoundLiteralExpression(NoSyntax, factory.String, fields[i].Name + "="));
                parts.Add(DisplayOf(factory, lookup, ThisField(receiver, fields[i]), fields[i].Type));
            }

            parts.Add(new BoundLiteralExpression(NoSyntax, factory.String, ")"));

            return ReturnBlock(Concat(factory, parts));
        }
    }
}
