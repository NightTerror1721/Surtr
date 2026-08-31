#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Decides which <c>@Pure</c> functions may be folded or common-subexpression-eliminated at
    /// compile time (§P3 fase 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The phase-2 check (<see cref="FlowAnalysis"/>) proves what a <c>@Pure</c> body does not
    /// <em>write</em> and what it does not <em>call</em>, but folding a call away demands more: the
    /// folded value has to be identical to whatever the function would return at run time, in any
    /// program state. That fails for a body that reads mutable observable state — a module
    /// <c>var</c>, a non-<c>let</c> field, a property getter — or that reaches out through a call
    /// whose referential transparency is not itself established.
    /// </para>
    /// <para>
    /// <see cref="PassesLocalChecks"/> therefore inspects one body and rejects everything
    /// observably impure except <em>direct calls to other functions</em>, which it records. The
    /// caller (the binder) closes the gate over the whole compilation as a greatest fixed point: a
    /// function is foldable when its body passes local checks <em>and</em> every call it makes
    /// targets a foldable function. That makes a cycle of mutually-recursive pure functions
    /// foldable while a single impure leaf disqualifies every caller that reaches it.
    /// </para>
    /// <para>
    /// <see cref="IsPureArgument"/> is the same inspection for one expression, with calls rejected:
    /// it is what a common-subexpression elimination needs, because evaluating an argument once
    /// instead of twice is only safe when the argument itself has no effects.
    /// </para>
    /// </remarks>
    internal static class PureFoldVerifier
    {
        /// <summary>
        /// Whether a body passes every local purity check, recording each direct call it makes.
        /// </summary>
        /// <remarks>
        /// Calls are allowed here — their targets are the caller's responsibility — but everything
        /// else a body could reach out through is not: no closure invocation (a closure captures
        /// state this check cannot see), no construction, no property read, no read of mutable
        /// state, no write outside a local.
        /// </remarks>
        public static bool PassesLocalChecks(
            MethodSymbol method,
            BoundStatement body,
            out HashSet<MethodSymbol> called)
        {
            called = new HashSet<MethodSymbol>();

            if (method.ReturnType.IsVoid || method.ReturnType.IsNever || method.TypeParameters.Count > 0)
                return false;

            var check = new Checker(method, allowCalls: true, called);
            check.Statement(body);
            return check._ok;
        }

        /// <summary>
        /// Whether one expression is safe to evaluate once and reuse: no calls, no writes, no reads
        /// of mutable state, no property reads, no construction.
        /// </summary>
        public static bool IsPureArgument(BoundExpression expression)
        {
            var check = new Checker(null, allowCalls: false, null);
            check.Expression(expression);
            return check._ok;
        }

        private sealed class Checker
        {
            private readonly MethodSymbol? _method;
            private readonly bool _allowCalls;
            private readonly HashSet<MethodSymbol>? _called;

            public Checker(MethodSymbol? method, bool allowCalls, HashSet<MethodSymbol>? called)
            {
                _method = method;
                _allowCalls = allowCalls;
                _called = called;
            }

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

                    case BoundCollectionBuildExpression build:
                        foreach (var argument in build.ConstructorArguments)
                            Expression(argument);
                        foreach (var fillArgs in build.FillArguments)
                        {
                            foreach (var argument in fillArgs)
                                Expression(argument);
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

                    case BoundCallExpression call:
                        // Under local checks a call is the one escape hatch: it records the target
                        // for the fixed point to close, and inspects what the call reaches (its
                        // receiver and arguments) for everything this check still rejects. As an
                        // argument, a call is an effect and is rejected outright.
                        if (_allowCalls)
                        {
                            _called?.Add(call.Method);
                            if (call.Receiver is not null)
                                Expression(call.Receiver);
                            foreach (var argument in call.Arguments)
                                Expression(argument);
                            return;
                        }
                        Reject();
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