#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Checks the promise <c>@NoAlloc</c> (§11) makes on a bound body: that running it puts nothing
    /// on the heap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>@Pure</c>'s sibling on the memory axis, and checked on the same terms: the mark is a
    /// contract the compiler trusts rather than proves, so what this reports is the set of
    /// constructs that allocate visibly in the body itself, and a warning is what a violation is
    /// worth. A VM inside a frame budget is judged on allocation as much as on time, which is why a
    /// promise about it is worth being able to write down at all.
    /// </para>
    /// <para>
    /// Three limits are deliberate and worth stating, because each is a place the check is silent
    /// rather than satisfied:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Calls are not followed.</b> <c>s.substring(0, 1)</c> allocates inside the callee, and a
    /// local walk cannot see it. Making it transitive would need the fixed point <c>@Pure</c>'s
    /// folding uses <em>plus</em> a curated list of allocation-free natives — and <c>substring</c>
    /// is not one — so it is out of scope here rather than half-done.
    /// </description></item>
    /// <item><description>
    /// <b>Tuples are allowed.</b> A tuple is a value type: the emitter lowers one to a run of
    /// contiguous slots (§2.9), with no heap object to sweep. Worth revisiting only if one ever
    /// reaches a boxing site inside a body that promised not to allocate.
    /// </description></item>
    /// <item><description>
    /// <b>Invoking a closure is allowed</b>, where creating one is not: the allocation happened
    /// wherever the lambda was written, and that is where the report belongs.
    /// </description></item>
    /// </list>
    /// <para>
    /// Runs after binding for the same reason <c>FlowAnalysis</c> does — the bound tree is the form
    /// with a whole body in it, and every conversion, expansion and lowering decision is already
    /// settled there, so a construct is counted once and where it really is.
    /// </para>
    /// </remarks>
    public sealed class NoAllocCheck
    {
        private readonly SurtrDiagnosticBag _diagnostics;
        private readonly string _sourceName;
        private readonly MethodSymbol _method;

        private NoAllocCheck(MethodSymbol method, SurtrDiagnosticBag diagnostics, string sourceName)
        {
            _method = method;
            _diagnostics = diagnostics;
            _sourceName = sourceName;
        }

        /// <summary>Checks one <c>@NoAlloc</c> body, reporting each construct that allocates.</summary>
        /// <param name="method">The marked method or accessor.</param>
        /// <param name="body">Its bound body.</param>
        /// <param name="diagnostics">Where the warnings go.</param>
        /// <param name="sourceName">The file the body was written in.</param>
        public static void Verify(
            MethodSymbol method,
            BoundStatement body,
            SurtrDiagnosticBag diagnostics,
            string sourceName)
        {
            new NoAllocCheck(method, diagnostics, sourceName).Statement(body);
        }

        #region Walk
        private void Statement(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundBlockStatement block:
                    foreach (var inner in block.Statements)
                        Statement(inner);

                    return;

                case BoundExpressionStatement expression:
                    Expression(expression.Expression);
                    return;

                case BoundLocalDeclarationStatement declaration:
                    if (declaration.Initializer is not null)
                        Expression(declaration.Initializer);

                    return;

                case BoundIfStatement conditional:
                    Expression(conditional.Condition);
                    Statement(conditional.Then);

                    if (conditional.Else is not null)
                        Statement(conditional.Else);

                    return;

                case BoundWhileStatement loop:
                    Expression(loop.Condition);
                    Statement(loop.Body);
                    return;

                case BoundForStatement loop:
                    if (loop.Initializer is not null)
                        Statement(loop.Initializer);

                    if (loop.Condition is not null)
                        Expression(loop.Condition);

                    if (loop.Step is not null)
                        Expression(loop.Step);

                    Statement(loop.Body);
                    return;

                case BoundForInStatement loop:
                    Expression(loop.Sequence);
                    Statement(loop.Body);
                    return;

                case BoundSwitchStatement @switch:
                {
                    Expression(@switch.Subject);
                    foreach (var section in @switch.Sections)
                    {
                        foreach (var inner in section.Statements)
                            Statement(inner);
                    }

                    return;
                }

                case BoundTryStatement @try:
                {
                    Statement(@try.Body);
                    foreach (var clause in @try.Catches)
                        Statement(clause.Body);

                    if (@try.Finally is not null)
                        Statement(@try.Finally);

                    return;
                }

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
            }
        }

        private void Expression(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundObjectCreationExpression creation:
                {
                    // A `value class` is the exception a class-shaped construction has: §2.9 lays
                    // one out inline, so building one writes slots rather than an object.
                    if (!IsValueType(creation.Type))
                        Report(creation.Span, $"constructing '{creation.Type.ToDisplayString()}' allocates");

                    foreach (var argument in creation.Arguments)
                        Expression(argument);

                    return;
                }

                case BoundArrayLiteralExpression array:
                {
                    Report(array.Span, "an array literal allocates");
                    foreach (var element in array.Elements)
                        Expression(element);

                    return;
                }

                case BoundDictLiteralExpression dictionary:
                {
                    Report(dictionary.Span, "a dictionary literal allocates");
                    foreach (var entry in dictionary.Entries)
                    {
                        Expression(entry.Key);
                        Expression(entry.Value);
                    }

                    return;
                }

                case BoundCollectionCreationExpression collection:
                {
                    Report(collection.Span, $"building a '{collection.Type.ToDisplayString()}' allocates");

                    if (collection.Capacity is not null)
                        Expression(collection.Capacity);

                    return;
                }

                case BoundInterpolatedStringExpression interpolated:
                {
                    Report(interpolated.Span, "string interpolation allocates the string it builds");
                    foreach (var part in interpolated.Parts)
                        Expression(part);

                    return;
                }

                case BoundLambdaExpression lambda:
                    // Only the creation: a lambda's own body is a separate method, checked on its
                    // own terms if it carries the mark, and running it is the caller's cost rather
                    // than this body's.
                    Report(lambda.Span, "creating a closure allocates");
                    return;

                case BoundYieldExpression yield:
                {
                    Report(yield.Span, "a generator allocates its cursor and its suspended frame");

                    if (yield.Value is not null)
                        Expression(yield.Value);

                    return;
                }

                case BoundBinaryExpression binary:
                {
                    // §5.7 makes `+` on strings concatenation, and a new string is a new object.
                    // Read off the result rather than the operands, which is what catches
                    // `"n=" + count` where only one side is text.
                    if (binary.Operator == BinaryOperator.Add && binary.Type.SpecialType == SpecialType.String)
                        Report(binary.Span, "concatenating strings allocates the result");

                    Expression(binary.Left);
                    Expression(binary.Right);
                    return;
                }

                case BoundCallExpression call:
                {
                    if (call.Receiver is not null)
                        Expression(call.Receiver);

                    foreach (var argument in call.Arguments)
                        Expression(argument);

                    return;
                }

                case BoundAssignmentExpression assignment:
                    Expression(assignment.Target);
                    Expression(assignment.Value);
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

                case BoundNullConditionalExpression conditional:
                    Expression(conditional.Receiver);
                    Expression(conditional.Access);
                    return;

                case BoundNullAssertExpression assert:
                    Expression(assert.Operand);
                    return;

                case BoundIndexExpression index:
                    Expression(index.Target);
                    Expression(index.Index);
                    return;

                case BoundTupleLiteralExpression tuple:
                {
                    // No report: a tuple is a value type, laid out inline.
                    foreach (var element in tuple.Elements)
                        Expression(element);

                    return;
                }

                case BoundClosureInvocationExpression invocation:
                {
                    Expression(invocation.Callee);
                    foreach (var argument in invocation.Arguments)
                        Expression(argument);

                    return;
                }

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

                case BoundTypeTestExpression test:
                    Expression(test.Operand);
                    return;

                case BoundThrowExpression @throw:
                    Expression(@throw.Value);
                    return;

                case BoundFieldExpression field:
                    if (field.Receiver is not null)
                        Expression(field.Receiver);

                    return;

                case BoundPropertyExpression property:
                    if (property.Receiver is not null)
                        Expression(property.Receiver);

                    return;

                case BoundSequenceExpression sequence:
                    Statement(sequence.Statement);
                    Expression(sequence.Value);
                    return;
            }
        }
        #endregion

        /// <summary>
        /// Whether a construction lands inline rather than on the heap — a <c>value class</c>
        /// (§2.9), whose fields take the slots they are written into.
        /// </summary>
        private static bool IsValueType(TypeSymbol type)
            => type is NamedTypeSymbol named && named.TypeKind == TypeSymbolKind.ValueClass;

        private void Report(SourceSpan span, string what)
            => _diagnostics.ReportWarning(
                SurtrDiagnosticCode.AllocationInNoAllocBody,
                $"'{_method.Name}' is marked '@NoAlloc', but {what}.",
                _sourceName,
                span);
    }
}
