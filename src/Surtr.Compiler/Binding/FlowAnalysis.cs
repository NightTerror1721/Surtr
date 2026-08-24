#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using System;
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
    /// <para>
    /// Two shapes end a method without a <c>return</c> of their own and both are recognised: a
    /// <c>switch</c> with a default section whose every section returns, and a loop whose condition
    /// never fails and which nothing <c>break</c>s out of. Both come down to the same question —
    /// what ways out does this construct have — which is why one stack of break targets answers it
    /// for loops and switches alike.
    /// </para>
    /// </remarks>
    public sealed class FlowAnalysis
    {
        private readonly SurtrDiagnosticBag _diagnostics;
        private readonly string _sourceName;
        private readonly MethodSymbol _method;

        private readonly HashSet<LocalSymbol> _assigned = new HashSet<LocalSymbol>();
        private readonly HashSet<LocalSymbol> _reported = new HashSet<LocalSymbol>();

        private readonly List<BreakTarget> _targets = new List<BreakTarget>();
        private string? _pendingLabel;

        private bool _reachable = true;
        private bool _reportedUnreachable;

        /// <summary>
        /// One construct a <c>break</c> leaves, and whether anything was found that leaves it.
        /// </summary>
        /// <remarks>
        /// This is what separates a loop that can finish from one that cannot. A loop whose
        /// condition never fails is left by a <c>break</c> or not at all — so whether one was
        /// reached decides whether the statement after it runs, and asking that needs the whole body
        /// rather than the header.
        /// </remarks>
        private sealed class BreakTarget
        {
            public BreakTarget(string? label) => Label = label;

            /// <summary>The label written on it, if it was given one.</summary>
            public string? Label { get; }

            /// <summary>Whether a reachable <c>break</c> named it.</summary>
            public bool Broken { get; set; }
        }

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

            // A generator's declared return is `generator<T>`, but its body never returns one:
            // falling off the end is how a generator ends, exactly as it is for a void method
            // (§3.7). Asking it to return its own view type would report every correct generator.
            if (analysis._reachable
                && !method.IsGenerator
                && !method.ReturnType.IsVoid && !method.ReturnType.IsNever && !method.ReturnType.IsError)
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

            // A label belongs to the statement it wraps rather than to the block it sits in, so it
            // is taken here and reaches exactly one construct.
            string? label = _pendingLabel;
            _pendingLabel = null;

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
                    var target = Push(label);
                    Statement(@while.Body);
                    Pop();

                    // Nothing here proves the loop runs, so what its body assigned does not count
                    // after it.
                    _assigned.Clear();
                    _assigned.UnionWith(before);
                    _reachable = !IsAlwaysTrue(@while.Condition) || target.Broken;
                    return;
                }

                case BoundForStatement @for:
                {
                    if (@for.Initializer is not null)
                        Statement(@for.Initializer);

                    if (@for.Condition is not null)
                        Expression(@for.Condition);

                    var before = new HashSet<LocalSymbol>(_assigned);
                    var target = Push(label);
                    Statement(@for.Body);
                    Pop();

                    if (@for.Step is not null)
                        Expression(@for.Step);

                    // A missing condition is the same shape as one that is always true: §4.2's
                    // `for (;;)` is written to never fail, so a `break` is the only way out.
                    bool endless = @for.Condition is null || IsAlwaysTrue(@for.Condition);

                    _assigned.Clear();
                    _assigned.UnionWith(before);
                    _reachable = !endless || target.Broken;
                    return;
                }

                case BoundForInStatement forIn:
                {
                    Expression(forIn.Sequence);

                    var before = new HashSet<LocalSymbol>(_assigned);
                    _assigned.Add(forIn.Variable);

                    // Pushed even though a `for-in` always finishes: a `break` inside it has to stop
                    // here rather than count against whatever encloses it.
                    Push(label);
                    Statement(forIn.Body);
                    Pop();

                    _assigned.Clear();
                    _assigned.UnionWith(before);
                    _reachable = true;
                    return;
                }

                case BoundSwitchStatement @switch:
                {
                    Expression(@switch.Subject);

                    var before = new HashSet<LocalSymbol>(_assigned);
                    var target = Push(label);

                    // A subject nothing matches leaves the switch untouched, so one with no default
                    // section always finishes whatever its sections do.
                    bool completes = !HasDefault(@switch);

                    for (int i = 0; i < @switch.Sections.Count; i++)
                    {
                        _assigned.Clear();
                        _assigned.UnionWith(before);
                        _reachable = true;

                        foreach (var inner in @switch.Sections[i].Statements)
                            Statement(inner);

                        // Running off any other section runs into the next one; running off the last
                        // is running off the switch, which is the shape that makes an exhaustive
                        // `switch` whose sections all return a terminating statement.
                        if (_reachable && i == @switch.Sections.Count - 1)
                            completes = true;
                    }

                    Pop();

                    _assigned.Clear();
                    _assigned.UnionWith(before);
                    _reachable = completes || target.Broken;
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

                // A yield hands a value out and comes back: unlike a return it does not end the
                // flow, so everything after it stays reachable and every local it reads counts as
                // read there.
                case BoundYieldStatement yield:
                    Expression(yield.Value);
                    return;

                case BoundThrowStatement @throw:
                    Expression(@throw.Value);
                    _reachable = false;
                    return;

                case BoundBreakStatement jump:
                {
                    // Only a `break` leaves. A `continue` re-enters the loop, so it says nothing
                    // about whether the statement after it can run.
                    if (_reachable && !jump.IsContinue)
                        Break(jump.Label);

                    _reachable = false;
                    return;
                }

                case BoundLabeledStatement labeled:
                    _pendingLabel = labeled.Label;
                    Statement(labeled.Statement);
                    return;
            }
        }

        private BreakTarget Push(string? label)
        {
            var target = new BreakTarget(label);
            _targets.Add(target);
            return target;
        }

        private void Pop() => _targets.RemoveAt(_targets.Count - 1);

        /// <summary>Records that control leaves whichever construct a <c>break</c> names.</summary>
        /// <remarks>
        /// A label nothing here carries marks every enclosing construct rather than none: it names
        /// something further out — a labelled block, say — and leaving that leaves everything
        /// between here and it.
        /// </remarks>
        private void Break(string? label)
        {
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                if (label is null || string.Equals(_targets[i].Label, label, StringComparison.Ordinal))
                {
                    _targets[i].Broken = true;
                    return;
                }
            }

            foreach (var target in _targets)
                target.Broken = true;
        }

        private static bool HasDefault(BoundSwitchStatement @switch)
        {
            foreach (var section in @switch.Sections)
            {
                if (section.IsDefault)
                    return true;
            }

            return false;
        }

        /// <summary>Whether a loop's condition is written so that it never fails.</summary>
        /// <remarks>
        /// Only a literal, deliberately. What this decides is whether the code after a loop is
        /// reachable, and a rule a reader cannot apply by eye would turn a missing <c>return</c>
        /// into a puzzle — <c>while (true)</c> is the shape the language has always meant by it.
        /// </remarks>
        private static bool IsAlwaysTrue(BoundExpression condition)
            => condition is BoundLiteralExpression { Value: bool value } && value;
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
                    {
                        Expression(conditional.Condition);

                        // The branches join like an `if`: a throw in one branch must not mark the
                        // code after the conditional unreachable, and only what both branches
                        // assign is assigned after.
                        var before = new HashSet<LocalSymbol>(_assigned);

                        Expression(conditional.WhenTrue);
                        var afterTrue = new HashSet<LocalSymbol>(_assigned);
                        bool trueReachable = _reachable;

                        _assigned.Clear();
                        _assigned.UnionWith(before);
                        _reachable = true;

                        Expression(conditional.WhenFalse);
                        bool falseReachable = _reachable;

                        _assigned.IntersectWith(afterTrue);
                        _reachable = trueReachable || falseReachable;
                        return;
                    }

                case BoundThrowExpression @throw:
                    Expression(@throw.Value);
                    _reachable = false;
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

                    // Arms join like an `if`: a throw in one arm must not mark the code after
                    // the switch unreachable, and only what every arm assigns is assigned after.
                    var before = new HashSet<LocalSymbol>(_assigned);
                    HashSet<LocalSymbol>? after = null;
                    bool anyReachable = false;

                    foreach (var arm in @switch.Arms)
                    {
                        foreach (var value in arm.Values)
                            Expression(value);

                        Expression(arm.Result);

                        if (after is null)
                            after = new HashSet<LocalSymbol>(_assigned);
                        else
                            after.IntersectWith(_assigned);

                        anyReachable = anyReachable || _reachable;

                        _assigned.Clear();
                        _assigned.UnionWith(before);
                        _reachable = true;
                    }

                    if (after is null)
                    {
                        _assigned.Clear();
                        _assigned.UnionWith(before);
                    }
                    else
                    {
                        _assigned.Clear();
                        _assigned.UnionWith(after);
                    }

                    _reachable = anyReachable;
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
