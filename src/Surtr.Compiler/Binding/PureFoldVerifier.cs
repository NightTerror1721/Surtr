#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Decides whether a <c>@Pure</c> function may be folded at compile time (§P3 fase 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The phase-2 check (<see cref="FlowAnalysis"/>) proves what a <c>@Pure</c> body does not
    /// <em>write</em> and what it does not <em>call</em>, but folding the call away demands more:
    /// the folded value has to be identical to whatever the function would return at run time, in
    /// any program state. That fails for a body that reads mutable observable state — a module
    /// <c>var</c>, a non-<c>let</c> field, a property getter — or that reaches out through a call,
    /// because a callee's referential transparency is not guaranteed by the caller's mark.
    /// </para>
    /// <para>
    /// This check is therefore deliberately narrower than phase 2: no calls of any kind (a
    /// transitive reachability proof is a later refinement), no property reads, no construction,
    /// no read of mutable state, no write outside a local. What is left is a pure expression over
    /// the function's own parameters, locals and compile-time constants — the shape the stdlib's
    /// arithmetic helpers are written in, and the shape that is safe to fold into a literal.
    /// </para>
    /// </remarks>
    internal static class PureFoldVerifier
    {
        /// <summary>Whether the bound body of a <c>@Pure</c> function is safe to fold.</summary>
        public static bool IsFoldable(MethodSymbol method, BoundStatement body)
        {
            if (method.ReturnType.IsVoid || method.ReturnType.IsNever || method.TypeParameters.Count > 0)
                return false;

            var check = new Checker(method);
            check.Statement(body);
            return check._ok;
        }

        private sealed class Checker
        {
            private readonly MethodSymbol _method;

            public Checker(MethodSymbol method) => _method = method;

            public bool _ok = true;

            public void Statement(BoundStatement statement)
            {
                if (!_ok)
                    return;

                switch (statement)
                {
                    case BoundBlockStatement block:
                        foreach (var inner in block.Statements)
                            Statement(inner);
                        return;

                    case BoundExpressionStatement expression:
                        Expression(expression.Expression);
                        return;

                    case BoundLocalDeclarationStatement local:
                        if (local.Initializer is not null)
                            Expression(local.Initializer);
                        return;

                    case BoundIfStatement @if:
                        Expression(@if.Condition);
                        Statement(@if.Then);
                        if (@if.Else is not null)
                            Statement(@if.Else);
                        return;

                    case BoundWhileStatement @while:
                        Expression(@while.Condition);
                        Statement(@while.Body);
                        return;

                    case BoundForStatement @for:
                        if (@for.Initializer is not null)
                            Statement(@for.Initializer);
                        if (@for.Condition is not null)
                            Expression(@for.Condition);
                        if (@for.Step is not null)
                            Expression(@for.Step);
                        Statement(@for.Body);
                        return;

                    case BoundForInStatement forIn:
                        Expression(forIn.Sequence);
                        Statement(forIn.Body);
                        return;

                    case BoundSwitchStatement @switch:
                        Expression(@switch.Subject);
                        foreach (var section in @switch.Sections)
                        {
                            foreach (var label in section.Labels)
                                Expression(label);
                            foreach (var inner in section.Statements)
                                Statement(inner);
                        }
                        return;

                    case BoundTryStatement @try:
                        Statement(@try.Body);
                        foreach (var clause in @try.Catches)
                            Statement(clause.Body);
                        if (@try.Finally is not null)
                            Statement(@try.Finally);
                        return;

                    case BoundReturnStatement @return:
                        if (@return.Value is not null)
                            Expression(@return.Value);
                        return;

                    case BoundThrowStatement @throw:
                        Expression(@throw.Value);
                        return;

                    case BoundLabeledStatement labeled:
                        Statement(labeled.Statement);
                        return;

                    case BoundNopStatement:
                        return;

                    default:
                        Reject();
                        return;
                }
            }

            public void Expression(BoundExpression expression)
            {
                if (!_ok)
                    return;

                switch (expression)
                {
                    case BoundLiteralExpression:
                    case BoundLocalExpression:
                    case BoundParameterExpression:
                        return;

                    case BoundConversionExpression conversion:
                        Expression(conversion.Operand);
                        return;

                    case BoundBinaryExpression binary:
                        Expression(binary.Left);
                        Expression(binary.Right);
                        return;

                    case BoundUnaryExpression unary:
                        Expression(unary.Operand);
                        return;

                    case BoundConditionalExpression conditional:
                        Expression(conditional.Condition);
                        Expression(conditional.WhenTrue);
                        Expression(conditional.WhenFalse);
                        return;

                    case BoundNullConditionalExpression conditional:
                        Expression(conditional.Receiver);
                        Expression(conditional.Access);
                        return;

                    case BoundIndexExpression index:
                        Expression(index.Target);
                        Expression(index.Index);
                        return;

                    case BoundArrayLiteralExpression array:
                        foreach (var element in array.Elements)
                            Expression(element);
                        return;

                    case BoundTupleLiteralExpression tuple:
                        foreach (var element in tuple.Elements)
                            Expression(element);
                        return;

                    case BoundDictLiteralExpression dictionary:
                        foreach (var entry in dictionary.Entries)
                        {
                            Expression(entry.Key);
                            Expression(entry.Value);
                        }
                        return;

                    case BoundInterpolatedStringExpression interpolated:
                        foreach (var part in interpolated.Parts)
                            Expression(part);
                        return;

                    case BoundTypeTestExpression test:
                        Expression(test.Operand);
                        return;

                    case BoundTypeOfExpression:
                    case BoundModuleOfExpression:
                        return;

                    case BoundAssignmentExpression assignment:
                        // A write confined to a local is invisible outside the call, so it stays
                        // pure; a write to a field is observable and cannot be folded away.
                        if (assignment.Target is BoundLocalExpression)
                            Expression(assignment.Value);
                        else
                            Reject();
                        return;

                    case BoundFieldExpression field:
                        // A `let`/`const` field is fixed for the program's life, so reading it is
                        // deterministic; a `var` field (a module variable, a static slot) can change
                        // under the call and is not.
                        if (field.Field.IsReadOnly)
                        {
                            if (field.Receiver is not null)
                                Expression(field.Receiver);
                            return;
                        }
                        Reject();
                        return;

                    case BoundSequenceExpression sequence:
                        Statement(sequence.Statement);
                        Expression(sequence.Value);
                        return;

                    case BoundLambdaExpression:
                        // A lambda is a value; nothing runs until something calls it, and a call is
                        // what the check above rejects.
                        return;

                    case BoundCallExpression:
                    case BoundClosureInvocationExpression:
                    case BoundObjectCreationExpression:
                    case BoundPropertyExpression:
                    case BoundThisExpression:
                    case BoundYieldExpression:
                    case BoundNullAssertExpression:
                    case BoundThrowExpression:
                    case BoundSwitchExpression:
                        Reject();
                        return;

                    default:
                        Reject();
                        return;
                }
            }

            private void Reject() => _ok = false;
        }
    }
}