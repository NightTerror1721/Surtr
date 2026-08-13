#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Walks a bound body asking what can happen, rather than what things are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three questions, all of which need the whole body rather than one expression: can a
    /// statement be reached, is a local assigned everywhere it is read, and can a method finish
    /// without returning what it promised.
    /// </para>
    /// <para>
    /// It runs on the bound tree rather than on syntax, so it sees a body where names are resolved
    /// and a compound assignment has already been expanded — <c>x += 1</c> is a read of <c>x</c>
    /// followed by a write, and definite assignment gets that right for free.
    /// </para>
    /// <para>
    /// The analysis is deliberately not a fixed-point one over a control-flow graph. It walks the
    /// tree and joins the branches of an <c>if</c>, which is exact for straight-line code and
    /// conservative in a loop — a local assigned only inside a loop body is not treated as assigned
    /// after it, since nothing here proves the loop runs.
    /// </para>
    /// </remarks>
    public sealed class FlowAnalysis
    {
        private readonly SurtrDiagnosticBag _diagnostics;
        private readonly string _sourceName;
        private readonly MethodSymbol _method;

        private readonly HashSet<LocalSymbol> _assigned = new HashSet<LocalSymbol>();
        private readonly HashSet<LocalSymbol> _reported = new HashSet<LocalSymbol>();

        private bool _reachable = true;
        private bool _reportedUnreachable;

        private FlowAnalysis(SurtrDiagnosticBag diagnostics, string sourceName, MethodSymbol method)
        {
            _diagnostics = diagnostics;
            _sourceName = sourceName;
            _method = method;
        }

        /// <summary>Checks one body, reporting whatever it finds.</summary>
        public static void Analyze(
            MethodSymbol method,
            BoundStatement body,
            SurtrDiagnosticBag diagnostics,
            string sourceName)
        {
            // Parameters need no seeding: they arrive assigned by definition, and only a local is
            // ever tracked here - which is the whole difference between the two.
            var analysis = new FlowAnalysis(diagnostics, sourceName, method);
            analysis.Statement(body);

            if (analysis._reachable && !method.ReturnType.IsVoid && !method.ReturnType.IsError)
            {
                diagnostics.ReportError(
                    SurtrDiagnosticCode.NotAllPathsReturn,
                    $"'{method.Name}' returns '{method.ReturnType.ToDisplayString()}' but can finish without returning one.",
                    sourceName,
                    body.Span);
            }
        }

        #region Statements
        private void Statement(BoundStatement statement)
        {
            if (!_reachable && statement is not BoundNopStatement)
            {
                // One report per body: everything after the first unreachable statement is also
                // unreachable, and saying so once is the useful version.
                if (!_reportedUnreachable)
                {
                    _reportedUnreachable = true;
                    _diagnostics.ReportWarning(
                        SurtrDiagnosticCode.UnreachableCode,
                        "Nothing can reach this statement.",
                        _sourceName,
                        statement.Span);
                }
            }

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
                {
                    if (local.Initializer is not null)
                    {
                        Expression(local.Initializer);
                        _assigned.Add(local.Local);
                    }

                    return;
                }

                case BoundIfStatement @if:
                {
                    Expression(@if.Condition);

                    var before = new HashSet<LocalSymbol>(_assigned);

                    Statement(@if.Then);
                    var afterThen = new HashSet<LocalSymbol>(_assigned);
                    bool thenReachable = _reachable;

                    _assigned.Clear();
                    _assigned.UnionWith(before);
                    _reachable = true;

                    if (@if.Else is not null)
                        Statement(@if.Else);

                    bool elseReachable = _reachable;

                    // Only what both branches assigned is assigned after, and the statement after
                    // is reachable only if some branch can fall out of it.
                    if (@if.Else is null)
                    {
                        _assigned.Clear();
                        _assigned.UnionWith(before);
                        _reachable = true;
                    }
                    else
                    {
                        _assigned.IntersectWith(afterThen);
                        _reachable = thenReachable || elseReachable;
                    }

                    return;
                }

                case BoundWhileStatement @while:
                {
                    Expression(@while.Condition);

                    var before = new HashSet<LocalSymbol>(_assigned);
                    Statement(@while.Body);

                    // Nothing here proves the loop runs, so what its body assigned does not count
                    // after it.
                    _assigned.Clear();
                    _assigned.UnionWith(before);
                    _reachable = true;
                    return;
                }

                case BoundForStatement @for:
                {
                    if (@for.Initializer is not null)
                        Statement(@for.Initializer);

                    if (@for.Condition is not null)
                        Expression(@for.Condition);

                    var before = new HashSet<LocalSymbol>(_assigned);
                    Statement(@for.Body);

                    if (@for.Step is not null)
                        Expression(@for.Step);

                    _assigned.Clear();
                    _assigned.UnionWith(before);
                    _reachable = true;
                    return;
                }

                case BoundForInStatement forIn:
                {
                    Expression(forIn.Sequence);

                    var before = new HashSet<LocalSymbol>(_assigned);
                    _assigned.Add(forIn.Variable);
                    Statement(forIn.Body);

                    _assigned.Clear();
                    _assigned.UnionWith(before);
                    _reachable = true;
                    return;
                }

                case BoundSwitchStatement @switch:
                {
                    Expression(@switch.Subject);

                    var before = new HashSet<LocalSymbol>(_assigned);
                    foreach (var section in @switch.Sections)
                    {
                        _assigned.Clear();
                        _assigned.UnionWith(before);
                        _reachable = true;

                        foreach (var inner in section.Statements)
                            Statement(inner);
                    }

                    _assigned.Clear();
                    _assigned.UnionWith(before);
                    _reachable = true;
                    return;
                }

                case BoundTryStatement @try:
                {
                    var before = new HashSet<LocalSymbol>(_assigned);
                    Statement(@try.Body);
                    bool bodyReachable = _reachable;

                    foreach (var clause in @try.Catches)
                    {
                        // A handler starts from what was assigned before the protected block, since
                        // the throw could have come from its first statement.
                        _assigned.Clear();
                        _assigned.UnionWith(before);
                        _assigned.Add(clause.Exception);
                        _reachable = true;

                        Statement(clause.Body);
                        bodyReachable |= _reachable;
                    }

                    _assigned.Clear();
                    _assigned.UnionWith(before);
                    _reachable = bodyReachable;

                    if (@try.Finally is not null)
                        Statement(@try.Finally);

                    return;
                }

                case BoundReturnStatement @return:
                {
                    if (@return.Value is not null)
                        Expression(@return.Value);

                    _reachable = false;
                    return;
                }

                case BoundThrowStatement @throw:
                    Expression(@throw.Value);
                    _reachable = false;
                    return;

                case BoundBreakStatement:
                    _reachable = false;
                    return;

                case BoundLabeledStatement labeled:
                    Statement(labeled.Statement);
                    return;
            }
        }
        #endregion

        #region Expressions
        private void Expression(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundLocalExpression local:
                    Read(local.Local, local);
                    return;

                case BoundAssignmentExpression assignment:
                {
                    // The value first: `x = x + 1` reads before it writes, and the read is the one
                    // that has to be checked.
                    Expression(assignment.Value);

                    if (assignment.Target is BoundLocalExpression target)
                        _assigned.Add(target.Local);
                    else
                        Expression(assignment.Target);

                    return;
                }

                case BoundBinaryExpression binary:
                    Expression(binary.Left);
                    Expression(binary.Right);
                    return;

                case BoundUnaryExpression unary:
                    Expression(unary.Operand);
                    return;

                case BoundConversionExpression conversion:
                    Expression(conversion.Operand);
                    return;

                case BoundConditionalExpression conditional:
                    Expression(conditional.Condition);
                    Expression(conditional.WhenTrue);
                    Expression(conditional.WhenFalse);
                    return;

                case BoundCallExpression call:
                {
                    if (call.Receiver is not null)
                        Expression(call.Receiver);

                    foreach (var argument in call.Arguments)
                        Expression(argument);

                    return;
                }

                case BoundClosureInvocationExpression invocation:
                {
                    Expression(invocation.Callee);
                    foreach (var argument in invocation.Arguments)
                        Expression(argument);

                    return;
                }

                case BoundObjectCreationExpression creation:
                {
                    foreach (var argument in creation.Arguments)
                        Expression(argument);

                    return;
                }

                case BoundIndexExpression index:
                    Expression(index.Target);
                    Expression(index.Index);
                    return;

                case BoundFieldExpression field:
                {
                    if (field.Receiver is not null)
                        Expression(field.Receiver);

                    return;
                }

                case BoundPropertyExpression property:
                {
                    if (property.Receiver is not null)
                        Expression(property.Receiver);

                    return;
                }

                case BoundArrayLiteralExpression array:
                {
                    foreach (var element in array.Elements)
                        Expression(element);

                    return;
                }

                case BoundTupleLiteralExpression tuple:
                {
                    foreach (var element in tuple.Elements)
                        Expression(element);

                    return;
                }

                case BoundDictLiteralExpression dictionary:
                {
                    foreach (var entry in dictionary.Entries)
                    {
                        Expression(entry.Key);
                        Expression(entry.Value);
                    }

                    return;
                }

                case BoundInterpolatedStringExpression interpolated:
                {
                    foreach (var part in interpolated.Parts)
                        Expression(part);

                    return;
                }

                case BoundTypeTestExpression test:
                    Expression(test.Operand);
                    return;

                case BoundSwitchExpression @switch:
                {
                    Expression(@switch.Subject);
                    foreach (var arm in @switch.Arms)
                    {
                        foreach (var value in arm.Values)
                            Expression(value);

                        Expression(arm.Result);
                    }

                    return;
                }

                case BoundLambdaExpression:
                    // A lambda's body runs later, and captures only what is effectively final -
                    // which the binder already checked at the capture site.
                    return;
            }
        }

        private void Read(LocalSymbol local, BoundExpression at)
        {
            if (_assigned.Contains(local) || !_reported.Add(local))
                return;

            _diagnostics.ReportError(
                SurtrDiagnosticCode.UseBeforeAssignment,
                $"'{local.Name}' is read here on a path that has not assigned it.",
                _sourceName,
                at.Span);
        }
        #endregion
    }
}
