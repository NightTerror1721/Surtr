#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using System.Collections.Generic;

namespace Surtr.Compiler.CodeGen
{
    /// <summary>
    /// Estimates the cost of splicing a bound body into a call site, so the emitter can decide
    /// without the source declaring anything (§3.6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The estimate is a weighted count over the bound tree rather than a byte length, because what
    /// matters is how much work the call site gains and how likely the splice is to grow: a call
    /// inside the body is expensive not for its size but because it drags another dispatch in, a
    /// <c>try</c> or a loop because it multiplies the exit paths. Cheap leaves — a literal, a local
    /// read, a conversion to the same type — cost nothing, so the common accessor shapes score the
    /// minimum.
    /// </para>
    /// <para>
    /// A body that is not a single trailing <c>return</c> pays the exit-label machinery
    /// <see cref="MethodBodyEmitter.TryInline"/> adds for it, so a multi-return body can never look
    /// cheaper than the shape that splices without any of it.
    /// </para>
    /// </remarks>
    internal static class InlineCost
    {
        /// <summary>What an unmodified method may cost to splice.</summary>
        public const int DefaultThreshold = 2;

        /// <summary>What an <c>inline</c> method may cost to splice.</summary>
        public const int InlineThreshold = 8;

        /// <summary>Whether <paramref name="body"/> is worth splicing at the given cost allowance.</summary>
        public static bool WorthInline(BoundStatement body, bool declaredInline)
        {
            int limit = declaredInline ? InlineThreshold : DefaultThreshold;
            return Estimate(body) <= limit;
        }

        /// <summary>The cost of splicing <paramref name="body"/> into a call site.</summary>
        public static int Estimate(BoundStatement body)
        {
            // The one shape that splices with no exit label and no result local — a single trailing
            // return — is exactly the shape the fast path in TryInline looks for, so its cost is the
            // value's alone. Every other body pays the machinery, added here once rather than per
            // statement.
            if (IsSingleReturn(body, out BoundExpression? value))
                return value is null ? 0 : EstimateExpression(value);

            return Statement(body) + 2;
        }

        private static bool IsSingleReturn(BoundStatement body, out BoundExpression? value)
        {
            if (body is BoundBlockStatement { Statements: [BoundReturnStatement single] })
            {
                value = single.Value;
                return true;
            }

            value = null;
            return false;
        }

        private static int Statement(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundNopStatement:
                    return 0;

                case BoundBlockStatement block:
                {
                    int total = 0;
                    foreach (var nested in block.Statements)
                        total += Statement(nested);
                    return total;
                }

                case BoundExpressionStatement expression:
                    return EstimateExpression(expression.Expression);

                case BoundLocalDeclarationStatement declaration:
                    return declaration.Initializer is null ? 0 : EstimateExpression(declaration.Initializer);

                case BoundIfStatement conditional:
                    return EstimateExpression(conditional.Condition)
                        + Statement(conditional.Then)
                        + (conditional.Else is null ? 0 : Statement(conditional.Else));

                case BoundWhileStatement loop:
                    return EstimateExpression(loop.Condition) + Statement(loop.Body) + 1;

                case BoundForStatement loop:
                {
                    int total = 2;
                    if (loop.Initializer is not null)
                        total += Statement(loop.Initializer);
                    if (loop.Condition is not null)
                        total += EstimateExpression(loop.Condition);
                    if (loop.Step is not null)
                        total += EstimateExpression(loop.Step);
                    return total + Statement(loop.Body);
                }

                case BoundForInStatement loop:
                    return EstimateExpression(loop.Sequence) + Statement(loop.Body) + 2;

                case BoundSwitchStatement @switch:
                {
                    int total = EstimateExpression(@switch.Subject) + 1;
                    foreach (var section in @switch.Sections)
                    {
                        foreach (var label in section.Labels)
                            total += EstimateExpression(label);
                        foreach (var nested in section.Statements)
                            total += Statement(nested);
                    }

                    return total;
                }

                case BoundTryStatement @try:
                {
                    int total = Statement(@try.Body) + 3;
                    foreach (var handler in @try.Catches)
                        total += Statement(handler.Body);
                    if (@try.Finally is not null)
                        total += Statement(@try.Finally);
                    return total;
                }

                case BoundThrowStatement @throw:
                    return EstimateExpression(@throw.Value) + 1;

                case BoundReturnStatement @return:
                    return @return.Value is null ? 0 : EstimateExpression(@return.Value);

                case BoundBreakStatement:
                    return 1;

                case BoundLabeledStatement labeled:
                    return Statement(labeled.Statement);

                default:
                    return 1;
            }
        }

        private static int EstimateExpression(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundLiteralExpression:
                case BoundLocalExpression:
                case BoundParameterExpression:
                case BoundThisExpression:
                case BoundConditionalReceiver:
                case BoundErrorExpression:
                    return 0;

                case BoundInterpolatedStringExpression interpolated:
                {
                    int total = 1;
                    foreach (var part in interpolated.Parts)
                        total += EstimateExpression(part);
                    return total;
                }

                case BoundFieldExpression field:
                    return (field.Receiver is null ? 0 : EstimateExpression(field.Receiver)) + 1;

                case BoundPropertyExpression property:
                    return (property.Receiver is null ? 0 : EstimateExpression(property.Receiver)) + 2;

                case BoundCallExpression call:
                {
                    int total = 4;
                    if (call.Receiver is not null)
                        total += EstimateExpression(call.Receiver);
                    foreach (var argument in call.Arguments)
                        total += EstimateExpression(argument);
                    return total;
                }

                case BoundNullConditionalExpression access:
                    return EstimateExpression(access.Receiver) + EstimateExpression(access.Access) + 2;

                case BoundNullAssertExpression assertion:
                    return EstimateExpression(assertion.Operand) + 1;

                case BoundThrowExpression @throw:
                    return EstimateExpression(@throw.Value) + 1;

                case BoundSequenceExpression sequence:
                    // The statement splices into the caller like any block, and the value it yields
                    // is what the inline site reads — both paid for.
                    return Statement(sequence.Statement) + EstimateExpression(sequence.Value) + 1;

                case BoundClosureInvocationExpression invocation:
                {
                    int total = 4 + EstimateExpression(invocation.Callee);
                    foreach (var argument in invocation.Arguments)
                        total += EstimateExpression(argument);
                    return total;
                }

                case BoundObjectCreationExpression creation:
                {
                    int total = 2;
                    foreach (var argument in creation.Arguments)
                        total += EstimateExpression(argument);
                    return total;
                }

                case BoundBinaryExpression binary:
                    return EstimateExpression(binary.Left) + EstimateExpression(binary.Right) + 1;

                case BoundUnaryExpression unary:
                    return EstimateExpression(unary.Operand) + 1;

                case BoundAssignmentExpression assignment:
                    return EstimateExpression(assignment.Target) + EstimateExpression(assignment.Value) + 1;

                case BoundConditionalExpression conditional:
                    return EstimateExpression(conditional.Condition)
                        + EstimateExpression(conditional.WhenTrue)
                        + EstimateExpression(conditional.WhenFalse) + 1;

                case BoundIndexExpression index:
                    return EstimateExpression(index.Target) + EstimateExpression(index.Index) + 1;

                case BoundConversionExpression conversion:
                    return EstimateExpression(conversion.Operand) + (conversion.Conversion.IsIdentity ? 0 : 1);

                case BoundTypeTestExpression test:
                    return EstimateExpression(test.Operand) + 1;

                case BoundLambdaExpression lambda:
                    return Statement(lambda.Body) + 4;

                case BoundArrayLiteralExpression array:
                {
                    int total = 1;
                    foreach (var element in array.Elements)
                        total += EstimateExpression(element);
                    return total;
                }

                case BoundTupleLiteralExpression tuple:
                {
                    int total = 1;
                    foreach (var element in tuple.Elements)
                        total += EstimateExpression(element);
                    return total;
                }

                case BoundDictLiteralExpression dict:
                {
                    int total = 1;
                    foreach (var entry in dict.Entries)
                        total += EstimateExpression(entry.Key) + EstimateExpression(entry.Value);
                    return total;
                }

                case BoundCollectionBuildExpression build:
                {
                    int total = 2;
                    foreach (var argument in build.ConstructorArguments)
                        total += EstimateExpression(argument);
                    foreach (var fillArgs in build.FillArguments)
                    {
                        total += 1;
                        foreach (var argument in fillArgs)
                            total += EstimateExpression(argument);
                    }
                    return total;
                }

                case BoundSwitchExpression @switch:
                {
                    int total = EstimateExpression(@switch.Subject) + 1;
                    foreach (var arm in @switch.Arms)
                    {
                        foreach (var armValue in arm.Values)
                            total += EstimateExpression(armValue);
                        total += EstimateExpression(arm.Result);
                    }

                    return total;
                }

                default:
                    return 1;
            }
        }
    }
}