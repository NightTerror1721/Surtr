#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax.Ast;
using System;
using System.Collections.Generic;
using System.Text;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Eliminates a <c>@Pure</c> call that is repeated across statements of a straight-line run
    /// (§P3 fase 3): the first occurrence stores its result in a local, and later occurrences read
    /// that local instead of calling again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the classic available-expression CSE, kept deliberately small. It tracks, for one
    /// run of straight-line statements, which pure calls have already been evaluated and which local
    /// holds each result. A later statement that contains the same call reads the local instead.
    /// Two things stop a reuse: a write to anything the call's arguments read (the value would have
    /// changed), and any control flow at all — a nested block or a branch may write operands or
    /// mean the first evaluation never dominated the second, so the whole table is dropped at a
    /// control-flow boundary. What survives is CSE across <c>let a = f(x); ...; let b = f(x);</c>
    /// with nothing between but ordinary statements.
    /// </para>
    /// <para>
    /// Only a call to a foldable <c>@Pure</c> function (referentially transparent by the same gate
    /// the folder applies) with side-effect-free arguments qualifies: reusing a value instead of
    /// re-running is only sound when neither the callee nor the arguments have effects.
    /// </para>
    /// </remarks>
    internal static class CrossStatementCse
    {
        /// <summary>Rewrites a whole body, removing duplicate pure calls across its blocks.</summary>
        /// <param name="body">The bound body.</param>
        /// <param name="isFoldable">Whether a method may be common-subexpression-eliminated.</param>
        public static BoundStatement Rewrite(BoundStatement body, Func<MethodSymbol, bool> isFoldable)
        {
            var pass = new Pass(isFoldable);
            return pass.RewriteBlock(body);
        }

        /// <summary>One evaluated pure call: the local holding its result and everything it reads.</summary>
        private sealed class Entry
        {
            public LocalSymbol Local = null!;
            public HashSet<Symbol> Reads = new HashSet<Symbol>();
        }

        private sealed class Pass
        {
            private readonly Func<MethodSymbol, bool> _isFoldable;
            private Dictionary<string, Entry> _available = new Dictionary<string, Entry>();

            public Pass(Func<MethodSymbol, bool> isFoldable) => _isFoldable = isFoldable;

            /// <summary>Rewrites a block's straight-line run; <paramref name="clearOnExit"/> drops the caller's table.</summary>
            public BoundStatement RewriteBlock(BoundStatement body)
            {
                if (body is not BoundBlockStatement block)
                    return body;

                var rewritten = new BoundStatement[block.Statements.Count];
                for (int i = 0; i < rewritten.Length; i++)
                    rewritten[i] = RewriteSequential(block.Statements[i]);

                return new BoundBlockStatement(block.Syntax, rewritten);
            }

            private BoundStatement RewriteSequential(BoundStatement statement)
            {
                switch (statement)
                {
                    case BoundIfStatement conditional:
                    {
                        var parent = _available;
                        var thenWrites = new HashSet<Symbol>();
                        var then = RewriteBody(conditional.Then, inheritAvailable: true, thenWrites);
                        var elseWrites = new HashSet<Symbol>();
                        var elseBody = conditional.Else is null
                            ? null
                            : RewriteBody(conditional.Else, inheritAvailable: true, elseWrites);

                        // An entry available before the `if` dominates code after it, so it
                        // survives — unless a branch wrote one of its operands.
                        Kill(parent, thenWrites);
                        Kill(parent, elseWrites);
                        _available = parent;

                        return new BoundIfStatement(conditional.Syntax, conditional.Condition, then, elseBody);
                    }

                    case BoundWhileStatement loop:
                    {
                        var parent = _available;
                        var bodyWrites = new HashSet<Symbol>();
                        var body = RewriteBody(loop.Body, inheritAvailable: false, bodyWrites);

                        // The loop may run zero times, but the entry before it always ran, so the
                        // entry dominates after the loop unless the body wrote one of its operands.
                        Kill(parent, bodyWrites);
                        _available = parent;

                        return new BoundWhileStatement(loop.Syntax, loop.Condition, body);
                    }

                    case BoundForStatement loop:
                    {
                        var parent = _available;
                        var bodyWrites = new HashSet<Symbol>();
                        var body = RewriteBody(loop.Body, inheritAvailable: false, bodyWrites);

                        Kill(parent, bodyWrites);
                        _available = parent;

                        return new BoundForStatement(loop.Syntax, loop.Initializer, loop.Condition, loop.Step, body);
                    }

                    case BoundForInStatement loop:
                    {
                        var parent = _available;
                        var bodyWrites = new HashSet<Symbol>();
                        var body = RewriteBody(loop.Body, inheritAvailable: false, bodyWrites);

                        Kill(parent, bodyWrites);
                        _available = parent;

                        return new BoundForInStatement(loop.Syntax, loop.Variable, loop.Sequence, body);
                    }

                    case BoundSwitchStatement @switch:
                    {
                        var parent = _available;
                        var sections = new BoundSwitchSection[@switch.Sections.Count];
                        for (int i = 0; i < sections.Length; i++)
                        {
                            var section = @switch.Sections[i];
                            var sectionWrites = new HashSet<Symbol>();
                            var body = RewriteBody(
                                new BoundBlockStatement(@switch.Syntax, section.Statements),
                                inheritAvailable: true,
                                sectionWrites);

                            Kill(parent, sectionWrites);
                            sections[i] = new BoundSwitchSection(section.Labels, ((BoundBlockStatement)body).Statements);
                        }

                        _available = parent;
                        return new BoundSwitchStatement(@switch.Syntax, @switch.Subject, sections);
                    }

                    case BoundTryStatement @try:
                    {
                        var parent = _available;
                        var bodyWrites = new HashSet<Symbol>();
                        var body = RewriteBody(@try.Body, inheritAvailable: true, bodyWrites);
                        Kill(parent, bodyWrites);

                        var catches = new BoundCatchClause[@try.Catches.Count];
                        for (int i = 0; i < catches.Length; i++)
                        {
                            var catchWrites = new HashSet<Symbol>();
                            catches[i] = new BoundCatchClause(
                                @try.Catches[i].Exception,
                                RewriteBody(@try.Catches[i].Body, inheritAvailable: true, catchWrites));
                            Kill(parent, catchWrites);
                        }

                        var finallyWrites = new HashSet<Symbol>();
                        var finallyBlock = @try.Finally is null
                            ? null
                            : RewriteBody(@try.Finally, inheritAvailable: true, finallyWrites);
                        Kill(parent, finallyWrites);

                        _available = parent;
                        return new BoundTryStatement(@try.Syntax, body, catches, finallyBlock);
                    }

                    case BoundLabeledStatement labeled:
                    {
                        var parent = _available;
                        var innerWrites = new HashSet<Symbol>();
                        var inner = RewriteBody(labeled.Statement, inheritAvailable: true, innerWrites);

                        Kill(parent, innerWrites);
                        _available = parent;

                        return new BoundLabeledStatement(labeled.Syntax, labeled.Label, inner);
                    }

                    default:
                    {
                        var substituted = SubstituteStatement(statement);
                        ApplyKills(substituted);
                        AddProducers(substituted);
                        return substituted;
                    }
                }
            }

            /// <summary>
            /// Rewrites one control-flow body, starting from a fresh table, and reports every write
            /// it performs so the caller can kill the entries the construct invalidates.
            /// </summary>
            /// <param name="body">The branch, loop body, section or catch to rewrite.</param>
            /// <param name="inheritAvailable">
            /// Whether the entries available before the construct dominate the body's entry. True
            /// for a branch of an <c>if</c>, a <c>switch</c> section, a <c>try</c> body or catch — a
            /// straight-line successor. False for a loop body, whose back-edge makes a pre-loop
            /// entry unsound inside.
            /// </param>
            /// <param name="writesOut">Every symbol the body writes, however deep.</param>
            private BoundStatement RewriteBody(BoundStatement body, bool inheritAvailable, HashSet<Symbol> writesOut)
            {
                var parent = _available;
                _available = inheritAvailable
                    ? new Dictionary<string, Entry>(parent)
                    : new Dictionary<string, Entry>();

                BoundStatement rewritten = body is BoundBlockStatement block
                    ? RewriteBlock(block)
                    : RewriteSequential(body);

                CollectSubtreeWrites(rewritten, writesOut);
                _available = parent;
                return rewritten;
            }

            #region Substitution
            private BoundStatement SubstituteStatement(BoundStatement statement)
            {
                switch (statement)
                {
                    case BoundLocalDeclarationStatement { Initializer: not null } declaration:
                    {
                        var initializer = SubstituteExpression(declaration.Initializer);
                        return ReferenceEquals(initializer, declaration.Initializer)
                            ? statement
                            : new BoundLocalDeclarationStatement(declaration.Syntax, declaration.Local, initializer);
                    }

                    case BoundExpressionStatement expression:
                    {
                        var substituted = SubstituteExpression(expression.Expression);
                        return ReferenceEquals(substituted, expression.Expression)
                            ? statement
                            : new BoundExpressionStatement(expression.Syntax, substituted);
                    }

                    case BoundReturnStatement { Value: not null } @return:
                    {
                        var value = SubstituteExpression(@return.Value);
                        return ReferenceEquals(value, @return.Value)
                            ? statement
                            : new BoundReturnStatement(@return.Syntax, value);
                    }

                    default:
                        return statement;
                }
            }

            private BoundExpression SubstituteExpression(BoundExpression expression)
            {
                if (expression is BoundCallExpression call && _available.TryGetValue(KeyOf(call), out var entry))
                    return new BoundLocalExpression(expression.Syntax, entry.Local);

                switch (expression)
                {
                    case BoundCallExpression callExpression:
                    {
                        var receiver = callExpression.Receiver is null ? null : SubstituteExpression(callExpression.Receiver);
                        List<BoundExpression>? rewrittenArguments = null;

                        for (int i = 0; i < callExpression.Arguments.Count; i++)
                        {
                            var substituted = SubstituteExpression(callExpression.Arguments[i]);
                            if (ReferenceEquals(substituted, callExpression.Arguments[i]))
                                continue;

                            rewrittenArguments ??= new List<BoundExpression>(callExpression.Arguments);
                            rewrittenArguments[i] = substituted;
                        }

                        if (rewrittenArguments is null && ReferenceEquals(receiver, callExpression.Receiver))
                            return callExpression;

                        return new BoundCallExpression(
                            callExpression.Syntax,
                            receiver ?? callExpression.Receiver,
                            callExpression.Method,
                            rewrittenArguments ?? callExpression.Arguments,
                            callExpression.IsVirtual);
                    }

                    case BoundBinaryExpression binary:
                    {
                        var left = SubstituteExpression(binary.Left);
                        var right = SubstituteExpression(binary.Right);
                        return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                            ? binary
                            : new BoundBinaryExpression(binary.Syntax, binary.Operator, left, right, binary.Type);
                    }

                    case BoundUnaryExpression unary:
                    {
                        var operand = SubstituteExpression(unary.Operand);
                        return ReferenceEquals(operand, unary.Operand)
                            ? unary
                            : new BoundUnaryExpression(unary.Syntax, unary.Operator, operand, unary.Type);
                    }

                    case BoundConversionExpression conversion:
                    {
                        var operand = SubstituteExpression(conversion.Operand);
                        return ReferenceEquals(operand, conversion.Operand)
                            ? conversion
                            : new BoundConversionExpression(conversion.Syntax, operand, conversion.Type, conversion.Conversion, conversion.IsExplicit);
                    }

                    default:
                        return expression;
                }
            }
            #endregion

            #region Producers and kills
            private void AddProducers(BoundStatement statement)
            {
                if (statement is not BoundLocalDeclarationStatement { Initializer: not null } declaration)
                    return;

                if (Unwrap(declaration.Initializer) is not BoundCallExpression call || !IsFoldablePureCall(call))
                    return;

                var entry = new Entry { Local = declaration.Local };
                CollectReads(call, entry.Reads);
                _available[KeyOf(call)] = entry;
            }

            private void ApplyKills(BoundStatement statement)
            {
                var writes = new HashSet<Symbol>();
                CollectStatementWrites(statement, writes);
                Kill(_available, writes);
            }

            /// <summary>Drops every entry whose local or operand a write touched.</summary>
            private static void Kill(Dictionary<string, Entry> available, HashSet<Symbol> writes)
            {
                if (writes.Count == 0)
                    return;

                List<string>? dead = null;
                foreach (var pair in available)
                {
                    if (writes.Contains(pair.Value.Local))
                    {
                        (dead ??= new List<string>()).Add(pair.Key);
                        continue;
                    }

                    foreach (var read in pair.Value.Reads)
                    {
                        if (!writes.Contains(read))
                            continue;

                        (dead ??= new List<string>()).Add(pair.Key);
                        break;
                    }
                }

                if (dead is null)
                    return;

                foreach (var key in dead)
                    available.Remove(key);
            }

            private bool IsFoldablePureCall(BoundCallExpression call)
            {
                if (!call.Method.IsStatic || call.IsVirtual || !_isFoldable(call.Method))
                    return false;

                foreach (var argument in call.Arguments)
                {
                    if (!PureFoldVerifier.IsPureArgument(argument))
                        return false;
                }

                return true;
            }

            private static void CollectStatementWrites(BoundStatement statement, HashSet<Symbol> writes)
            {
                switch (statement)
                {
                    case BoundExpressionStatement expression:
                        CollectExpressionWrites(expression.Expression, writes);
                        return;

                    case BoundLocalDeclarationStatement { Initializer: not null } declaration:
                        CollectExpressionWrites(declaration.Initializer, writes);
                        return;

                    case BoundReturnStatement { Value: not null } @return:
                        CollectExpressionWrites(@return.Value, writes);
                        return;

                    default:
                        return;
                }
            }

            /// <summary>Every write a whole statement subtree performs, control flow included.</summary>
            private static void CollectSubtreeWrites(BoundStatement statement, HashSet<Symbol> writes)
            {
                switch (statement)
                {
                    case BoundBlockStatement block:
                        foreach (var inner in block.Statements)
                            CollectSubtreeWrites(inner, writes);
                        return;

                    case BoundExpressionStatement expression:
                        CollectExpressionWrites(expression.Expression, writes);
                        return;

                    case BoundLocalDeclarationStatement { Initializer: not null } declaration:
                        CollectExpressionWrites(declaration.Initializer, writes);
                        return;

                    case BoundReturnStatement { Value: not null } @return:
                        CollectExpressionWrites(@return.Value, writes);
                        return;

                    case BoundIfStatement @if:
                        CollectExpressionWrites(@if.Condition, writes);
                        CollectSubtreeWrites(@if.Then, writes);
                        if (@if.Else is not null)
                            CollectSubtreeWrites(@if.Else, writes);
                        return;

                    case BoundWhileStatement loop:
                        CollectExpressionWrites(loop.Condition, writes);
                        CollectSubtreeWrites(loop.Body, writes);
                        return;

                    case BoundForStatement loop:
                        if (loop.Initializer is not null)
                            CollectSubtreeWrites(loop.Initializer, writes);
                        if (loop.Condition is not null)
                            CollectExpressionWrites(loop.Condition, writes);
                        if (loop.Step is not null)
                            CollectExpressionWrites(loop.Step, writes);
                        CollectSubtreeWrites(loop.Body, writes);
                        return;

                    case BoundForInStatement loop:
                        CollectExpressionWrites(loop.Sequence, writes);
                        CollectSubtreeWrites(loop.Body, writes);
                        return;

                    case BoundSwitchStatement @switch:
                        CollectExpressionWrites(@switch.Subject, writes);
                        foreach (var section in @switch.Sections)
                        {
                            foreach (var inner in section.Statements)
                                CollectSubtreeWrites(inner, writes);
                        }
                        return;

                    case BoundTryStatement @try:
                        CollectSubtreeWrites(@try.Body, writes);
                        foreach (var clause in @try.Catches)
                            CollectSubtreeWrites(clause.Body, writes);
                        if (@try.Finally is not null)
                            CollectSubtreeWrites(@try.Finally, writes);
                        return;

                    case BoundLabeledStatement labeled:
                        CollectSubtreeWrites(labeled.Statement, writes);
                        return;

                    case BoundThrowStatement @throw:
                        CollectExpressionWrites(@throw.Value, writes);
                        return;

                    default:
                        return;
                }
            }

            private static void CollectExpressionWrites(BoundExpression expression, HashSet<Symbol> writes)
            {
                switch (expression)
                {
                    case BoundAssignmentExpression assignment:
                        CollectTarget(assignment.Target, writes);
                        CollectExpressionWrites(assignment.Value, writes);
                        return;

                    case BoundUnaryExpression { Operator: var op } unary when op
                        is UnaryOperator.PreIncrement or UnaryOperator.PostIncrement
                        or UnaryOperator.PreDecrement or UnaryOperator.PostDecrement:
                        CollectTarget(unary.Operand, writes);
                        return;

                    case BoundBinaryExpression binary:
                        CollectExpressionWrites(binary.Left, writes);
                        CollectExpressionWrites(binary.Right, writes);
                        return;

                    case BoundCallExpression call:
                        if (call.Receiver is not null)
                            CollectExpressionWrites(call.Receiver, writes);
                        foreach (var argument in call.Arguments)
                            CollectExpressionWrites(argument, writes);
                        return;

                    default:
                        return;
                }
            }

            private static void CollectTarget(BoundExpression target, HashSet<Symbol> writes)
            {
                switch (target)
                {
                    case BoundLocalExpression local:
                        writes.Add(local.Local);
                        return;

                    case BoundParameterExpression parameter:
                        writes.Add(parameter.Parameter);
                        return;

                    case BoundFieldExpression field:
                        writes.Add(field.Field);
                        return;

                    default:
                        return;
                }
            }
            #endregion

            #region Structural key and reads
            private static string KeyOf(BoundCallExpression call)
            {
                var builder = new StringBuilder();
                AppendKey(builder, call);
                return builder.ToString();
            }

            private static void AppendKey(StringBuilder builder, BoundExpression expression)
            {
                switch (expression)
                {
                    case BoundCallExpression call:
                        builder.Append("call[").Append(call.Method.ToDisplayString()).Append('/').Append(call.Arguments.Count).Append(']');
                        foreach (var argument in call.Arguments)
                        {
                            builder.Append('<');
                            AppendKey(builder, argument);
                            builder.Append('>');
                        }
                        return;

                    case BoundLiteralExpression literal:
                        builder.Append("lit:").Append(literal.Value).Append('@').Append(literal.Type.ToDisplayString());
                        return;

                    case BoundLocalExpression local:
                        builder.Append("loc:").Append(local.Local.GetHashCode());
                        return;

                    case BoundParameterExpression parameter:
                        builder.Append("par:").Append(parameter.Parameter.GetHashCode());
                        return;

                    case BoundConversionExpression conversion:
                        AppendKey(builder, conversion.Operand);
                        return;

                    case BoundBinaryExpression binary:
                        builder.Append('(');
                        AppendKey(builder, binary.Left);
                        builder.Append(binary.Operator);
                        AppendKey(builder, binary.Right);
                        builder.Append(')');
                        return;

                    case BoundUnaryExpression unary:
                        builder.Append(unary.Operator);
                        AppendKey(builder, unary.Operand);
                        return;

                    case BoundFieldExpression field:
                        builder.Append("fld:").Append(field.Field.GetHashCode());
                        if (field.Receiver is not null)
                        {
                            builder.Append('@');
                            AppendKey(builder, field.Receiver);
                        }
                        return;

                    default:
                        builder.Append(expression.GetType().Name).Append('#');
                        return;
                }
            }

            private static void CollectReads(BoundExpression expression, HashSet<Symbol> reads)
            {
                switch (expression)
                {
                    case BoundLocalExpression local:
                        reads.Add(local.Local);
                        return;

                    case BoundParameterExpression parameter:
                        reads.Add(parameter.Parameter);
                        return;

                    case BoundFieldExpression field:
                        reads.Add(field.Field);
                        if (field.Receiver is not null)
                            CollectReads(field.Receiver, reads);
                        return;

                    case BoundCallExpression call:
                        if (call.Receiver is not null)
                            CollectReads(call.Receiver, reads);
                        foreach (var argument in call.Arguments)
                            CollectReads(argument, reads);
                        return;

                    case BoundBinaryExpression binary:
                        CollectReads(binary.Left, reads);
                        CollectReads(binary.Right, reads);
                        return;

                    case BoundUnaryExpression unary:
                        CollectReads(unary.Operand, reads);
                        return;

                    case BoundConversionExpression conversion:
                        CollectReads(conversion.Operand, reads);
                        return;

                    default:
                        return;
                }
            }

            private static BoundExpression Unwrap(BoundExpression expression)
                => expression is BoundConversionExpression conversion ? Unwrap(conversion.Operand) : expression;
            #endregion
        }
    }
}