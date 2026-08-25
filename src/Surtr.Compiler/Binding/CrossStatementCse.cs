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
            return pass.RewriteBlock(body, clearOnExit: false);
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
            public BoundStatement RewriteBlock(BoundStatement body, bool clearOnExit)
            {
                if (body is not BoundBlockStatement block)
                    return body;

                var parent = _available;
                if (clearOnExit)
                    _available = new Dictionary<string, Entry>();

                var rewritten = new BoundStatement[block.Statements.Count];
                for (int i = 0; i < rewritten.Length; i++)
                    rewritten[i] = RewriteSequential(block.Statements[i]);

                if (clearOnExit)
                {
                    // Whatever the nested run left may not dominate the caller's later statements,
                    // and the nested block itself could have written anything, so the caller's
                    // table is dropped too.
                    _available = parent;
                    parent.Clear();
                }

                return new BoundBlockStatement(block.Syntax, rewritten);
            }

            private BoundStatement RewriteSequential(BoundStatement statement)
            {
                switch (statement)
                {
                    case BoundIfStatement conditional:
                    {
                        var rewritten = new BoundIfStatement(
                            conditional.Syntax,
                            conditional.Condition,
                            RewriteBlock(conditional.Then, clearOnExit: true),
                            conditional.Else is null ? null : RewriteBlock(conditional.Else, clearOnExit: true));
                        _available.Clear();
                        return rewritten;
                    }

                    case BoundWhileStatement loop:
                    {
                        var rewritten = new BoundWhileStatement(
                            loop.Syntax,
                            loop.Condition,
                            RewriteBlock(loop.Body, clearOnExit: true));
                        _available.Clear();
                        return rewritten;
                    }

                    case BoundForStatement loop:
                    {
                        var rewritten = new BoundForStatement(
                            loop.Syntax,
                            loop.Initializer,
                            loop.Condition,
                            loop.Step,
                            RewriteBlock(loop.Body, clearOnExit: true));
                        _available.Clear();
                        return rewritten;
                    }

                    case BoundForInStatement loop:
                    {
                        var rewritten = new BoundForInStatement(
                            loop.Syntax,
                            loop.Variable,
                            loop.Sequence,
                            RewriteBlock(loop.Body, clearOnExit: true));
                        _available.Clear();
                        return rewritten;
                    }

                    case BoundSwitchStatement @switch:
                    {
                        var sections = new BoundSwitchSection[@switch.Sections.Count];
                        for (int i = 0; i < sections.Length; i++)
                        {
                            var section = @switch.Sections[i];
                            var body = RewriteBlock(new BoundBlockStatement(@switch.Syntax, section.Statements), clearOnExit: true);
                            sections[i] = new BoundSwitchSection(section.Labels, ((BoundBlockStatement)body).Statements);
                        }

                        _available.Clear();
                        return new BoundSwitchStatement(@switch.Syntax, @switch.Subject, sections);
                    }

                    case BoundTryStatement @try:
                    {
                        var catches = new BoundCatchClause[@try.Catches.Count];
                        for (int i = 0; i < catches.Length; i++)
                        {
                            catches[i] = new BoundCatchClause(
                                @try.Catches[i].Exception,
                                RewriteBlock(@try.Catches[i].Body, clearOnExit: true));
                        }

                        var rewritten = new BoundTryStatement(
                            @try.Syntax,
                            RewriteBlock(@try.Body, clearOnExit: true),
                            catches,
                            @try.Finally is null ? null : RewriteBlock(@try.Finally, clearOnExit: true));
                        _available.Clear();
                        return rewritten;
                    }

                    case BoundLabeledStatement labeled:
                    {
                        var rewritten = new BoundLabeledStatement(
                            labeled.Syntax,
                            labeled.Label,
                            RewriteBlock(labeled.Statement, clearOnExit: true));
                        _available.Clear();
                        return rewritten;
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

                if (writes.Count == 0)
                    return;

                List<string>? dead = null;
                foreach (var pair in _available)
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
                    _available.Remove(key);
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