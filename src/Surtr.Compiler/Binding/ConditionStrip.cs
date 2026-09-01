#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax.Ast;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Drops invocations of declarations marked <c>@Condition(expr)</c> when <c>expr</c> folds to
    /// <see langword="false"/> at compile time - Surtr's take on C#'s <c>[Conditional]</c> (§11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs over the bound tree, after binding and before flow analysis, so the analysis and every
    /// later pass see the final shape: a stripped call is simply gone. A declaration whose condition
    /// is <see langword="true"/>, or that carries no <c>@Condition</c> at all, is left exactly as it
    /// was - the mark changes nothing about a live call.
    /// </para>
    /// <para>
    /// Two call shapes are stripped. A call used as a statement (<c>log()</c>) becomes a no-op, the
    /// way C#'s <c>[Conditional]</c> does - arguments and receiver included, because a dropped trace
    /// must not run anything. A call or property read that yields a value in expression position is
    /// replaced by that type's compile-time default: a primitive's zero, or <see langword="null"/> for
    /// a reference or nullable. A <c>@Condition</c> method returning a value class and used for its
    /// result is left alone rather than invented a wrong default - it should be called for its effect
    /// (as a statement) instead.
    /// </para>
    /// </remarks>
    internal static class ConditionStrip
    {
        /// <summary>Rewrites a whole body, removing the calls the condition turns off.</summary>
        /// <param name="body">The bound body to rewrite.</param>
        public static BoundStatement Rewrite(BoundStatement body)
        {
            var pass = new Pass();
            return pass.RewriteStatement(body);
        }

        private sealed class Pass
        {
            public BoundStatement RewriteStatement(BoundStatement statement)
            {
                switch (statement)
                {
                    case BoundNopStatement:
                    case BoundThrowStatement:
                        return statement;

                    case BoundBlockStatement block:
                    {
                        var rewritten = new BoundStatement[block.Statements.Count];
                        for (int i = 0; i < rewritten.Length; i++)
                            rewritten[i] = RewriteStatement(block.Statements[i]);
                        return new BoundBlockStatement(block.Syntax, rewritten);
                    }

                    case BoundExpressionStatement expression:
                        return RewriteExpressionStatement(expression);

                    case BoundLocalDeclarationStatement { Initializer: not null } declaration:
                    {
                        var initializer = RewriteExpression(declaration.Initializer);
                        return ReferenceEquals(initializer, declaration.Initializer)
                            ? statement
                            : new BoundLocalDeclarationStatement(declaration.Syntax, declaration.Local, initializer);
                    }

                    case BoundLocalDeclarationStatement:
                        return statement;

                    case BoundReturnStatement { Value: not null } @return:
                    {
                        var value = RewriteExpression(@return.Value);
                        return ReferenceEquals(value, @return.Value)
                            ? statement
                            : new BoundReturnStatement(@return.Syntax, value);
                    }

                    case BoundReturnStatement:
                        return statement;

                    case BoundIfStatement @if:
                        return new BoundIfStatement(
                            @if.Syntax,
                            RewriteExpression(@if.Condition),
                            RewriteStatement(@if.Then),
                            @if.Else is null ? null : RewriteStatement(@if.Else));

                    case BoundWhileStatement loop:
                        return new BoundWhileStatement(
                            loop.Syntax,
                            RewriteExpression(loop.Condition),
                            RewriteStatement(loop.Body));

                    case BoundForStatement loop:
                        return new BoundForStatement(
                            loop.Syntax,
                            loop.Initializer is null ? null : RewriteStatement(loop.Initializer),
                            loop.Condition is null ? null : RewriteExpression(loop.Condition),
                            loop.Step is null ? null : RewriteExpression(loop.Step),
                            RewriteStatement(loop.Body));

                    case BoundForInStatement loop:
                        return new BoundForInStatement(
                            loop.Syntax,
                            loop.Variable,
                            RewriteExpression(loop.Sequence),
                            RewriteStatement(loop.Body));

                    case BoundSwitchStatement @switch:
                    {
                        var sections = new BoundSwitchSection[@switch.Sections.Count];
                        for (int i = 0; i < sections.Length; i++)
                        {
                            var section = @switch.Sections[i];
                            var inner = new BoundStatement[section.Statements.Count];
                            for (int j = 0; j < inner.Length; j++)
                                inner[j] = RewriteStatement(section.Statements[j]);
                            sections[i] = new BoundSwitchSection(section.Labels, inner, section.PatternLocal, section.Guard);
                        }

                        return new BoundSwitchStatement(@switch.Syntax, RewriteExpression(@switch.Subject), sections);
                    }

                    case BoundTryStatement @try:
                    {
                        var catches = new BoundCatchClause[@try.Catches.Count];
                        for (int i = 0; i < catches.Length; i++)
                        {
                            var clause = @try.Catches[i];
                            catches[i] = new BoundCatchClause(clause.Exception, RewriteStatement(clause.Body));
                        }

                        return new BoundTryStatement(
                            @try.Syntax,
                            RewriteStatement(@try.Body),
                            catches,
                            @try.Finally is null ? null : RewriteStatement(@try.Finally));
                    }

                    case BoundLabeledStatement labeled:
                        return new BoundLabeledStatement(labeled.Syntax, labeled.Label, RewriteStatement(labeled.Statement));

                    default:
                        return statement;
                }
            }

            private BoundStatement RewriteExpressionStatement(BoundExpressionStatement statement)
            {
                // A call used for its effect: the whole thing disappears.
                if (statement.Expression is BoundCallExpression call && IsDisabled(call.Method))
                    return new BoundNopStatement(statement.Syntax);

                // A property set used for its effect: the assignment disappears.
                if (statement.Expression is BoundAssignmentExpression assignment
                    && assignment.Target is BoundPropertyExpression setProperty
                    && IsDisabled(setProperty.Property))
                {
                    return new BoundNopStatement(statement.Syntax);
                }

                var rewritten = RewriteExpression(statement.Expression);
                return ReferenceEquals(rewritten, statement.Expression)
                    ? statement
                    : new BoundExpressionStatement(statement.Syntax, rewritten);
            }

            private BoundExpression RewriteExpression(BoundExpression expression)
            {
                switch (expression)
                {
                    case BoundCallExpression call:
                    {
                        if (IsDisabled(call.Method))
                            return DefaultFor(call);

                        var receiver = call.Receiver is null ? null : RewriteExpression(call.Receiver);
                        List<BoundExpression>? rewrittenArguments = null;

                        for (int i = 0; i < call.Arguments.Count; i++)
                        {
                            var substituted = RewriteExpression(call.Arguments[i]);
                            if (ReferenceEquals(substituted, call.Arguments[i]))
                                continue;

                            rewrittenArguments ??= new List<BoundExpression>(call.Arguments);
                            rewrittenArguments[i] = substituted;
                        }

                        if (rewrittenArguments is null && ReferenceEquals(receiver, call.Receiver))
                            return call;

                        return new BoundCallExpression(
                            call.Syntax,
                            receiver ?? call.Receiver,
                            call.Method,
                            rewrittenArguments ?? call.Arguments,
                            call.IsVirtual);
                    }

                    case BoundPropertyExpression property:
                    {
                        if (IsDisabled(property.Property))
                            return DefaultFor(property);

                        var receiver = property.Receiver is null ? null : RewriteExpression(property.Receiver);
                        return ReferenceEquals(receiver, property.Receiver)
                            ? property
                            : new BoundPropertyExpression(
                                property.Syntax,
                                receiver,
                                property.Property,
                                property.IsVirtualGet,
                                property.IsVirtualSet);
                    }

                    case BoundAssignmentExpression assignment:
                    {
                        // The value side can hold a stripped call; the target is left as written, since
                        // a target property that is itself off is handled where the assignment is the
                        // whole statement (RewriteExpressionStatement), not nested inside another value.
                        var value = RewriteExpression(assignment.Value);
                        return ReferenceEquals(value, assignment.Value)
                            ? assignment
                            : new BoundAssignmentExpression(assignment.Syntax, assignment.Target, value);
                    }

                    case BoundBinaryExpression binary:
                    {
                        var left = RewriteExpression(binary.Left);
                        var right = RewriteExpression(binary.Right);
                        return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                            ? binary
                            : new BoundBinaryExpression(binary.Syntax, binary.Operator, left, right, binary.Type);
                    }

                    case BoundUnaryExpression unary:
                    {
                        var operand = RewriteExpression(unary.Operand);
                        return ReferenceEquals(operand, unary.Operand)
                            ? unary
                            : new BoundUnaryExpression(unary.Syntax, unary.Operator, operand, unary.Type);
                    }

                    case BoundConversionExpression conversion:
                    {
                        var operand = RewriteExpression(conversion.Operand);
                        return ReferenceEquals(operand, conversion.Operand)
                            ? conversion
                            : new BoundConversionExpression(conversion.Syntax, operand, conversion.Type, conversion.Conversion, conversion.IsExplicit);
                    }

                    case BoundConditionalExpression conditional:
                    {
                        var condition = RewriteExpression(conditional.Condition);
                        var whenTrue = RewriteExpression(conditional.WhenTrue);
                        var whenFalse = RewriteExpression(conditional.WhenFalse);
                        return ReferenceEquals(condition, conditional.Condition)
                                && ReferenceEquals(whenTrue, conditional.WhenTrue)
                                && ReferenceEquals(whenFalse, conditional.WhenFalse)
                            ? conditional
                            : new BoundConditionalExpression(conditional.Syntax, condition, whenTrue, whenFalse, conditional.Type);
                    }

                    case BoundNullConditionalExpression nullConditional:
                    {
                        var accessed = RewriteExpression(nullConditional.Access);
                        return ReferenceEquals(accessed, nullConditional.Access)
                            ? nullConditional
                            : new BoundNullConditionalExpression(nullConditional.Syntax, nullConditional.Receiver, accessed, nullConditional.Type);
                    }

                    case BoundIndexExpression index:
                    {
                        var target = RewriteExpression(index.Target);
                        var argument = RewriteExpression(index.Index);
                        return ReferenceEquals(target, index.Target) && ReferenceEquals(argument, index.Index)
                            ? index
                            : new BoundIndexExpression(index.Syntax, target, argument, index.Type);
                    }

                    case BoundInterpolatedStringExpression interpolated:
                    {
                        var parts = RewriteInterpolatedParts(interpolated.Parts);
                        return ReferenceEquals(parts, interpolated.Parts)
                            ? interpolated
                            : new BoundInterpolatedStringExpression(interpolated.Syntax, interpolated.Type, parts);
                    }

                    default:
                        return expression;
                }
            }

            private IReadOnlyList<BoundExpression> RewriteInterpolatedParts(IReadOnlyList<BoundExpression> parts)
            {
                List<BoundExpression>? rewritten = null;
                for (int i = 0; i < parts.Count; i++)
                {
                    var substituted = RewriteExpression(parts[i]);
                    if (ReferenceEquals(substituted, parts[i]))
                        continue;

                    rewritten ??= new List<BoundExpression>(parts);
                    rewritten[i] = substituted;
                }

                return (IReadOnlyList<BoundExpression>?)rewritten ?? parts;
            }

            /// <summary>Whether <paramref name="symbol"/> is <c>@Condition(false)</c> - off in this build.</summary>
            private static bool IsDisabled(Symbol symbol) => !BuiltInAttributes.IsConditionEnabled(symbol);

            /// <summary>
            /// The compile-time default for a stripped expression: a primitive's zero, or
            /// <see langword="null"/> for a reference or nullable. A value class has no safe default here
            /// and is left unstripped by the caller, so this is only ever asked for a strip-safe type.
            /// </summary>
            private static BoundExpression DefaultFor(BoundExpression original)
            {
                TypeSymbol type = original.Type;
                object? value = type.NonNullable.SpecialType switch
                {
                    SpecialType.Int => 0L,
                    SpecialType.Float => 0.0,
                    SpecialType.Bool => false,
                    SpecialType.Char => '\0',
                    _ => null,
                };

                return new BoundLiteralExpression(original.Syntax, type, value);
            }
        }
    }
}
