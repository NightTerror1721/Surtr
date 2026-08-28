#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax;
using Surtr.Runtime.BuiltIns;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Checks the restrictions §7.2 puts on a <c>const fun</c>, on its bound body.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The restrictions exist so that folding a call is the same thing as running it: no host
    /// <c>native</c> function, no mutation of anything outside the call, no I/O, and a body known
    /// statically — so neither <c>virtual</c> nor <c>abstract</c>, since folding has to know which
    /// body runs. An <em>instance</em> method is allowed when its receiver folds (§2.3quater): the
    /// enum's synthesized members are instance and const, and a fold passes the folded receiver as
    /// the first argument.
    /// </para>
    /// <para>
    /// Reported here rather than left to the folder, because these are properties of the
    /// <em>declaration</em>: they are wrong whether or not anything ever asks for a fold, and a
    /// diagnostic that only appeared at the first constant use would point at the wrong place.
    /// What the folder reports instead is the other kind of failure — the function is fine and this
    /// particular run did not finish, or produced something a constant cannot hold.
    /// </para>
    /// </remarks>
    public sealed class ConstFunctionCheck
    {
        private readonly SurtrDiagnosticBag _diagnostics;
        private readonly string _sourceName;
        private readonly MethodSymbol _method;

        private ConstFunctionCheck(MethodSymbol method, SurtrDiagnosticBag diagnostics, string sourceName)
        {
            _method = method;
            _diagnostics = diagnostics;
            _sourceName = sourceName;
        }

        /// <summary>Checks one <c>const fun</c>, reporting whatever breaks the rules.</summary>
        public static void Verify(
            MethodSymbol method,
            BoundStatement body,
            SurtrDiagnosticBag diagnostics,
            string sourceName)
        {
            var check = new ConstFunctionCheck(method, diagnostics, sourceName);

            if (method.Dispatch != MethodDispatch.Direct)
            {
                check.Report(
                    body.Span,
                    $"'{method.Name}' is const, so which body runs has to be known at compile time; it cannot be virtual or abstract.");
            }

            if (method.IsNative)
                check.Report(body.Span, $"'{method.Name}' is const, so it cannot be native — there is no bytecode to run.");

            check.Statement(body);
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
                case BoundCallExpression call:
                {
                    CheckCallee(call);

                    if (call.Receiver is not null)
                        Expression(call.Receiver);

                    foreach (var argument in call.Arguments)
                        Expression(argument);

                    return;
                }

                case BoundAssignmentExpression assignment:
                {
                    CheckTarget(assignment);
                    Expression(assignment.Value);
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
                {
                    foreach (var entry in dictionary.Entries)
                    {
                        Expression(entry.Key);
                        Expression(entry.Value);
                    }

                    return;
                }

                case BoundCollectionBuildExpression build:
                {
                    foreach (var argument in build.ConstructorArguments)
                        Expression(argument);

                    foreach (var fillArgs in build.FillArguments)
                    {
                        foreach (var argument in fillArgs)
                            Expression(argument);
                    }

                    return;
                }

                case BoundInterpolatedStringExpression interpolated:
                    foreach (var part in interpolated.Parts)
                        Expression(part);

                    return;

                case BoundObjectCreationExpression creation:
                    foreach (var argument in creation.Arguments)
                        Expression(argument);

                    return;

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

                case BoundSequenceExpression sequence:
                    // A range guard over a value is ordinary statements + one expression: the
                    // temporary's declaration and the `if` are checked like any others, and so is
                    // the captured value.
                    Statement(sequence.Statement);
                    Expression(sequence.Value);
                    return;
            }
        }
        #endregion

        #region Rules
        /// <summary>
        /// Whether a callee is one a folded run may reach.
        /// </summary>
        /// <remarks>
        /// Another <c>const fun</c>, or a member of the standard library — which is what §7.2's own
        /// example calls when it writes <c>table.push(...)</c>. What it rules out is a host
        /// <c>native</c> function (§10), whose body lives in whichever runtime the host registered
        /// it with and therefore does not exist inside the compiler at all.
        /// </remarks>
        private void CheckCallee(BoundCallExpression call)
        {
            var callee = call.Method;

            if (callee.IsConst || IsStandardLibrary(callee))
                return;

            _diagnostics.ReportError(
                SurtrDiagnosticCode.InvalidConstFunction,
                callee.IsNative
                    ? $"'{_method.Name}' is const, so it cannot call the native function '{callee.Name}'."
                    : $"'{_method.Name}' is const, so it may only call other const functions; '{callee.Name}' is not one.",
                _sourceName,
                call.Span);
        }

        /// <summary>
        /// Whether a member came from the built-in module, which every runtime shares.
        /// </summary>
        private static bool IsStandardLibrary(MethodSymbol method)
        {
            if (method.ImportedFrom is null)
                return false;

            for (Symbol? walk = method.ContainingSymbol; walk is not null; walk = walk.ContainingSymbol)
            {
                if (walk is ModuleSymbol module)
                    return string.Equals(module.Path, SurtrBuiltIns.ModulePath, System.StringComparison.Ordinal);
            }

            return false;
        }

        /// <summary>
        /// Rejects a write to anything that outlives the call.
        /// </summary>
        /// <remarks>
        /// A local, a parameter and an element of a collection the body itself built are all fine —
        /// they exist only for the duration of the fold. A field, a property and a module-level
        /// variable are not: writing one would make folding observable, and a construct whose result
        /// depends on whether the compiler chose to fold it is exactly what §7.2's purity
        /// restrictions exist to prevent.
        /// </remarks>
        private void CheckTarget(BoundAssignmentExpression assignment)
        {
            string? what = assignment.Target switch
            {
                BoundFieldExpression field => $"the field '{field.Field.Name}'",
                BoundPropertyExpression property => $"the property '{property.Property.Name}'",
                _ => null,
            };

            if (what is null)
                return;

            Report(assignment.Span, $"'{_method.Name}' is const, so it cannot assign to {what}; a const function mutates nothing outside itself.");
        }

        private void Report(SourceSpan span, string message)
            => _diagnostics.ReportError(SurtrDiagnosticCode.InvalidConstFunction, message, _sourceName, span);
        #endregion
    }
}
