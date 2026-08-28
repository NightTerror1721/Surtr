#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax.Ast;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Builds the members every enum answers to without declaring them (§2.4, §2.3): <c>equals</c>,
    /// <c>hashCode</c>, <c>toString</c>, <c>values</c>, the two <c>of</c> forms, <c>compareTo</c>
    /// and <c>operator&lt;=&gt;</c>, plus the implicit <c>IEquatable&lt;E&gt;</c> and
    /// <c>IComparable&lt;E&gt;</c> contracts. These are real methods — created by the binder, given
    /// bound bodies, emitted into the image like any other — so they are callable, overridable by
    /// declaring one's own (for the ones that are not reserved), and consistent with the <c>==</c>/
    /// <c>!=</c> operators and relational forms the same representation already answers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every body is a closed form over the enum's own <c>value</c> field and its cases: no native
    /// calls, no tables — <c>toString</c> and <c>of(name)</c> are chains of comparisons against the
    /// folded case values, <c>values()</c> is an array literal, and the ordering members compare
    /// the <c>value</c> slot. That is what keeps the bodies inside the <c>@Pure</c>/<c>@NoAlloc</c>
    /// promises the marks make, and inside the compile-time subset later phases rely on.
    /// </para>
    /// <para>
    /// The <c>value</c> field is never a constructor parameter, so these bodies read it the way a
    /// user's would: as the first instance field, whose slot is the flattened block's base.
    /// </para>
    /// </remarks>
    internal static class EnumMemberSynthesizer
    {
        /// <summary>The name a case's reverse lookup and display answer under.</summary>
        internal const string ValueFieldName = "value";

        internal const string EqualsName = "equals";
        internal const string HashCodeName = "hashCode";
        internal const string ToStringName = "toString";
        internal const string ValuesName = "values";
        internal const string OfName = "of";
        internal const string CompareToName = "compareTo";

        /// <summary>The name the <c>&lt;=></c> operator is declared under (§5.6).</summary>
        internal static string SpaceshipName
            => OperatorNames.For(Syntax.TokenType.Spaceship, 2);

        /// <summary>The syntax position every synthesized bound node gets.</summary>
        private static readonly SyntaxNode NoSyntax = null!;

        /// <summary>Builds <c>return expr;</c> wrapped in a block, all with the null (synthetic) syntax.</summary>
        private static BoundStatement ReturnBlock(BoundExpression value)
            => new BoundBlockStatement(NoSyntax, new BoundStatement[] { new BoundReturnStatement(NoSyntax, value) });

        /// <summary>A <c>this</c> reference of the given enum, for an instance member's receiver.</summary>
        private static BoundExpression This(NamedTypeSymbol type)
            => new BoundThisExpression(NoSyntax, type, isSuper: false);

        /// <summary>Reads <c>this.field</c>.</summary>
        private static BoundExpression ThisField(BoundExpression receiver, FieldSymbol field)
            => new BoundFieldExpression(NoSyntax, receiver, field);

        /// <summary>The enum's synthetic <c>value</c> field (§2.4).</summary>
        internal static FieldSymbol ValueField(NamedTypeSymbol type)
        {
            foreach (var member in type.Definition.Members)
            {
                if (member is FieldSymbol { IsStatic: false, Name: ValueFieldName } field)
                    return field;
            }

            throw new InvalidOperationException($"Enum '{type.Name}' has no synthetic 'value' field.");
        }

        /// <summary>The cases of an enum, in declaration order.</summary>
        internal static List<FieldSymbol> CasesOf(NamedTypeSymbol type)
        {
            var cases = new List<FieldSymbol>();
            foreach (var member in type.Definition.Members)
            {
                if (member is FieldSymbol { IsStatic: true } field && ReferenceEquals(field.Type, type))
                    cases.Add(field);
            }

            return cases;
        }

        /// <summary>An integer literal.</summary>
        private static BoundExpression IntLiteral(TypeSymbolFactory factory, long value)
            => new BoundLiteralExpression(NoSyntax, factory.Int, value);

        /// <summary>
        /// The instance equality body: one comparison per field, the same walk <c>==</c> emits.
        /// </summary>
        internal static BoundStatement EqualsBody(
            TypeSymbolFactory factory,
            NamedTypeSymbol type,
            List<FieldSymbol> fields,
            MethodSymbol method)
        {
            var receiver = This(type);
            var other = new BoundParameterExpression(NoSyntax, method.Parameters[0]);

            BoundExpression? walk = null;
            for (int i = 0; i < fields.Count; i++)
            {
                var pair = new BoundBinaryExpression(
                    NoSyntax,
                    BinaryOperator.Equal,
                    ThisField(receiver, fields[i]),
                    ThisField(other, fields[i]),
                    factory.Bool);

                walk = walk is null ? pair : new BoundBinaryExpression(NoSyntax, BinaryOperator.LogicalAnd, walk, pair, factory.Bool);
            }

            return ReturnBlock(walk!);
        }

        /// <summary>
        /// The <c>hashCode</c> body: the <c>value</c> itself for a bare enum (the same hash an
        /// int answers with), FNV-1a over the per-field hashes once the enum carries fields.
        /// </summary>
        internal static BoundStatement HashCodeBody(
            TypeSymbolFactory factory,
            MemberLookup lookup,
            NamedTypeSymbol type,
            List<FieldSymbol> fields,
            MethodSymbol method)
        {
            var receiver = This(type);

            if (fields.Count == 1)
                return ReturnBlock(ThisField(receiver, fields[0]));

            BoundExpression hash = IntLiteral(factory, 0);
            for (int i = 0; i < fields.Count; i++)
            {
                var fieldHash = FieldHash(factory, lookup, ThisField(receiver, fields[i]), fields[i].Type, i);
                var multiplied = new BoundBinaryExpression(NoSyntax, BinaryOperator.Multiply, hash, IntLiteral(factory, 16777619L), factory.Int);
                hash = new BoundBinaryExpression(NoSyntax, BinaryOperator.BitXor, multiplied, fieldHash, factory.Int);
            }

            return ReturnBlock(hash);
        }

        /// <summary>
        /// The <c>toString</c> body: a chain over the folded case values returning each case's
        /// name, then the fallback <c>DisplayName(value)</c> for a value no case names.
        /// </summary>
        internal static BoundStatement ToStringBody(
            TypeSymbolFactory factory,
            NamedTypeSymbol type,
            List<FieldSymbol> cases,
            MethodSymbol method)
        {
            var receiver = This(type);
            var value = ThisField(receiver, ValueField(type));

            var statements = new List<BoundStatement>(cases.Count + 1);
            foreach (var @case in cases)
            {
                var condition = new BoundBinaryExpression(
                    NoSyntax,
                    BinaryOperator.Equal,
                    value,
                    IntLiteral(factory, @case.EnumValue ?? 0),
                    factory.Bool);

                statements.Add(new BoundIfStatement(
                    NoSyntax,
                    condition,
                    ReturnBlock(new BoundLiteralExpression(NoSyntax, factory.String, @case.Name)),
                    otherwise: null));
            }

            // `DisplayName(value)`: the fallback interpolates, which is why toString is @Pure but
            // never @NoAlloc (§2.3bis).
            var name = new BoundLiteralExpression(NoSyntax, factory.String, type.ToDisplayString() + "(");
            var rendered = new BoundBinaryExpression(NoSyntax, BinaryOperator.Add, value, new BoundLiteralExpression(NoSyntax, factory.String, ")"), factory.String);
            statements.Add(new BoundReturnStatement(NoSyntax, new BoundBinaryExpression(NoSyntax, BinaryOperator.Add, name, rendered, factory.String)));

            return new BoundBlockStatement(NoSyntax, statements);
        }

        /// <summary>
        /// The <c>values()</c> body: an array literal over the cases in declaration order — fresh
        /// per call (§6.7), which is why it carries no <c>@Pure</c>.
        /// </summary>
        internal static BoundStatement ValuesBody(
            TypeSymbolFactory factory,
            NamedTypeSymbol type,
            List<FieldSymbol> cases,
            MethodSymbol method)
        {
            var elements = new BoundExpression[cases.Count];
            for (int i = 0; i < cases.Count; i++)
                elements[i] = new BoundFieldExpression(NoSyntax, null, cases[i]);

            return ReturnBlock(new BoundArrayLiteralExpression(NoSyntax, factory.Array(type), elements));
        }

        /// <summary>
        /// The <c>of(value)</c> body: a chain over the folded case values returning the matching
        /// case or null. A <c>@Flags</c> enum is total — every int is a representable combination
        /// — so its body is the cast, never null (§2.3).
        /// </summary>
        internal static BoundStatement OfValueBody(
            TypeSymbolFactory factory,
            NamedTypeSymbol type,
            List<FieldSymbol> cases,
            MethodSymbol method,
            bool isFlags)
        {
            var argument = new BoundParameterExpression(NoSyntax, method.Parameters[0]);

            if (isFlags)
            {
                var cast = new BoundConversionExpression(
                    NoSyntax,
                    argument,
                    method.ReturnType,
                    Conversion.Of(ConversionKind.ExplicitNumeric),
                    isExplicit: true);
                return ReturnBlock(cast);
            }

            var statements = new List<BoundStatement>(cases.Count + 1);
            foreach (var @case in cases)
            {
                var condition = new BoundBinaryExpression(
                    NoSyntax,
                    BinaryOperator.Equal,
                    argument,
                    IntLiteral(factory, @case.EnumValue ?? 0),
                    factory.Bool);

                statements.Add(new BoundIfStatement(
                    NoSyntax,
                    condition,
                    ReturnBlock(new BoundFieldExpression(NoSyntax, null, @case)),
                    otherwise: null));
            }

            statements.Add(new BoundReturnStatement(NoSyntax, new BoundLiteralExpression(NoSyntax, method.ReturnType, null)));
            return new BoundBlockStatement(NoSyntax, statements);
        }

        /// <summary>
        /// The <c>of(name)</c> body: an exact, case-sensitive search by name, or null — the inverse
        /// of <c>toString</c> for names that exist.
        /// </summary>
        internal static BoundStatement OfNameBody(
            TypeSymbolFactory factory,
            NamedTypeSymbol type,
            List<FieldSymbol> cases,
            MethodSymbol method)
        {
            var argument = new BoundParameterExpression(NoSyntax, method.Parameters[0]);

            var statements = new List<BoundStatement>(cases.Count + 1);
            foreach (var @case in cases)
            {
                var condition = new BoundBinaryExpression(
                    NoSyntax,
                    BinaryOperator.Equal,
                    argument,
                    new BoundLiteralExpression(NoSyntax, factory.String, @case.Name),
                    factory.Bool);

                statements.Add(new BoundIfStatement(
                    NoSyntax,
                    condition,
                    ReturnBlock(new BoundFieldExpression(NoSyntax, null, @case)),
                    otherwise: null));
            }

            statements.Add(new BoundReturnStatement(NoSyntax, new BoundLiteralExpression(NoSyntax, method.ReturnType, null)));
            return new BoundBlockStatement(NoSyntax, statements);
        }

        /// <summary>The <c>compareTo(other)</c> body: the sign of <c>value - other.value</c>.</summary>
        internal static BoundStatement CompareToBody(
            TypeSymbolFactory factory,
            NamedTypeSymbol type,
            MethodSymbol method)
        {
            var receiver = This(type);
            var other = new BoundParameterExpression(NoSyntax, method.Parameters[0]);
            var value = ValueField(type);

            var comparison = new BoundBinaryExpression(
                NoSyntax,
                BinaryOperator.Compare,
                ThisField(receiver, value),
                ThisField(other, value),
                factory.Int);

            return ReturnBlock(comparison);
        }

        /// <summary>
        /// The <c>operator&lt;=&gt;(a, b)</c> body: the sign of <c>a.value - b.value</c>. The four
        /// relational forms reduce it against zero at the call site (§5.6), so this is the one
        /// operator the enum needs for all of them to work.
        /// </summary>
        internal static BoundStatement SpaceshipBody(
            TypeSymbolFactory factory,
            NamedTypeSymbol type,
            MethodSymbol method)
        {
            var a = new BoundParameterExpression(NoSyntax, method.Parameters[0]);
            var b = new BoundParameterExpression(NoSyntax, method.Parameters[1]);
            var value = ValueField(type);

            var comparison = new BoundBinaryExpression(
                NoSyntax,
                BinaryOperator.Compare,
                ThisField(a, value),
                ThisField(b, value),
                factory.Int);

            return ReturnBlock(comparison);
        }

        /// <summary>A per-field integer hash, defined so equal fields always produce the same value.</summary>
        /// <remarks>
        /// Mirror of <c>ValueMemberSynthesizer.FieldHash</c>: numeric fields hash as themselves,
        /// strings by their length, everything else by a position-dependent constant — so equal
        /// <c>equals</c>-values hash equal by construction.
        /// </remarks>
        private static BoundExpression FieldHash(
            TypeSymbolFactory factory,
            MemberLookup lookup,
            BoundExpression fieldValue,
            TypeSymbol fieldType,
            int position)
        {
            if (fieldType.NonNullable.SpecialType == SpecialType.Int)
                return fieldValue;

            if (fieldType.NonNullable.SpecialType == SpecialType.String)
            {
                if (lookup.FindProperty(fieldType.NonNullable, "length") is PropertySymbol length)
                    return new BoundPropertyExpression(NoSyntax, fieldValue, length, isVirtualGet: false, isVirtualSet: false);
            }

            return IntLiteral(factory, position + 1);
        }
    }
}