#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;
using Surtr.LanguageServer.Protocol;

namespace Surtr.LanguageServer.Workspace
{
    /// <summary>
    /// The <c>textDocument/inlayHint</c> handler: the grey hints an editor renders inline next to
    /// code. Three shapes, all resolved from the bound tree so the type shown is the substituted
    /// one:
    /// <list type="number">
    /// <item>an <b>inferred type</b> on a local whose annotation was omitted (<c>let x = 5</c>
    /// hints <c>: int</c> after the name) — only when the binder had to infer it, never when the
    /// source already says it;</item>
    /// <item>a <b>return type</b> on a lambda, after its body;</item>
    /// <item>a <b>parameter name</b> on a positional argument whose value does not already say
    /// which parameter it fills — a literal, or a variable whose name differs from the parameter's
    /// (<c>spawn(1.0, hp)</c> hints <c>x:</c> and <c>hp:</c>).</item>
    /// </list>
    /// </summary>
    public static class InlayHintProvider
    {
        public static IReadOnlyList<InlayHint> Compute(CompilationSnapshot snapshot, string filePath, string text)
        {
            var hints = new List<InlayHint>();
            if (snapshot.IsEmpty || snapshot.Binder is null)
                return hints;

            var tokens = new Lexer(SurtrSourceBuffer.FromString(text, filePath)).Tokenize();
            var lines = TextLines.Index(text);

            foreach (var pair in snapshot.Binder.Bodies)
            {
                if (!snapshot.Binder.BodyFiles.TryGetValue(pair.Key, out string? bodyFile)
                    || !string.Equals(Path.GetFullPath(bodyFile), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                WalkStatement(pair.Value, text, tokens, lines, hints);
            }

            foreach (var initializer in snapshot.Binder.FieldInitializers)
            {
                if (initializer.Value.Span.End <= text.Length)
                    WalkExpression(initializer.Value, text, tokens, lines, hints);
            }

            foreach (var block in snapshot.Binder.StaticBlocks)
                WalkStatement(block.Body, text, tokens, lines, hints);

            foreach (var chain in snapshot.Binder.ConstructorChains.Values)
            {
                foreach (var argument in chain.Arguments)
                    WalkExpression(argument, text, tokens, lines, hints);
            }

            return hints;
        }

        // ------------------------------------------------------------------------------------
        // Walking
        // ------------------------------------------------------------------------------------

        private static void WalkStatement(BoundStatement statement, string text, List<Token> tokens, TextLines lines, List<InlayHint> hints)
        {
            switch (statement)
            {
                case BoundBlockStatement block:
                    foreach (var child in block.Statements)
                        WalkStatement(child, text, tokens, lines, hints);
                    break;

                case BoundLocalDeclarationStatement declaration:
                    HintInferredType(declaration, text, tokens, lines, hints);
                    if (declaration.Initializer is not null)
                        WalkExpression(declaration.Initializer, text, tokens, lines, hints);
                    break;

                case BoundExpressionStatement expressionStatement:
                    WalkExpression(expressionStatement.Expression, text, tokens, lines, hints);
                    break;

                case BoundIfStatement ifStatement:
                    WalkExpression(ifStatement.Condition, text, tokens, lines, hints);
                    WalkStatement(ifStatement.Then, text, tokens, lines, hints);
                    if (ifStatement.Else is not null)
                        WalkStatement(ifStatement.Else, text, tokens, lines, hints);
                    break;

                case BoundWhileStatement whileStatement:
                    WalkExpression(whileStatement.Condition, text, tokens, lines, hints);
                    WalkStatement(whileStatement.Body, text, tokens, lines, hints);
                    break;

                case BoundForStatement forStatement:
                    if (forStatement.Initializer is not null)
                        WalkStatement(forStatement.Initializer, text, tokens, lines, hints);
                    if (forStatement.Condition is not null)
                        WalkExpression(forStatement.Condition, text, tokens, lines, hints);
                    if (forStatement.Step is not null)
                        WalkExpression(forStatement.Step, text, tokens, lines, hints);
                    WalkStatement(forStatement.Body, text, tokens, lines, hints);
                    break;

                case BoundForInStatement forIn:
                    if (forIn.Variable.Type is not null && !forIn.Variable.Type.IsVoid
                        && FirstNameToken(forIn.Syntax.Span, tokens, forIn.Variable.Name) is Token forInName)
                    {
                        HintAt(forInName.Span.End, ": " + forIn.Variable.Type.ToDisplayString(), InlayHintKinds.Type, lines, hints);
                    }

                    WalkExpression(forIn.Sequence, text, tokens, lines, hints);
                    WalkStatement(forIn.Body, text, tokens, lines, hints);
                    break;

                case BoundSwitchStatement switchStatement:
                    WalkExpression(switchStatement.Subject, text, tokens, lines, hints);
                    foreach (var section in switchStatement.Sections)
                    {
                        foreach (var label in section.Labels)
                            WalkExpression(label, text, tokens, lines, hints);
                        foreach (var child in section.Statements)
                            WalkStatement(child, text, tokens, lines, hints);
                    }

                    break;

                case BoundTryStatement tryStatement:
                    WalkStatement(tryStatement.Body, text, tokens, lines, hints);
                    foreach (var clause in tryStatement.Catches)
                        WalkStatement(clause.Body, text, tokens, lines, hints);
                    if (tryStatement.Finally is not null)
                        WalkStatement(tryStatement.Finally, text, tokens, lines, hints);
                    break;

                case BoundThrowStatement throwStatement:
                    WalkExpression(throwStatement.Value, text, tokens, lines, hints);
                    break;

                case BoundReturnStatement returnStatement:
                    if (returnStatement.Value is not null)
                        WalkExpression(returnStatement.Value, text, tokens, lines, hints);
                    break;

                case BoundLabeledStatement labeled:
                    WalkStatement(labeled.Statement, text, tokens, lines, hints);
                    break;
            }
        }

        private static void WalkExpression(BoundExpression expression, string text, List<Token> tokens, TextLines lines, List<InlayHint> hints)
        {
            switch (expression)
            {
                case BoundLambdaExpression lambda:
                    HintLambdaReturnType(lambda, text, lines, hints);
                    WalkStatement(lambda.Body, text, tokens, lines, hints);
                    break;

                case BoundCallExpression call:
                    HintParameterNames(call.Syntax as CallExpressionSyntax, call.Method.Parameters, lines, hints);
                    if (call.Receiver is not null)
                        WalkExpression(call.Receiver, text, tokens, lines, hints);
                    foreach (var argument in call.Arguments)
                        WalkExpression(argument, text, tokens, lines, hints);
                    break;

                case BoundObjectCreationExpression creation:
                    if (creation.Constructor is not null)
                        HintParameterNames(creation.Syntax as CallExpressionSyntax, creation.Constructor.Parameters, lines, hints);
                    foreach (var argument in creation.Arguments)
                        WalkExpression(argument, text, tokens, lines, hints);
                    break;

                case BoundClosureInvocationExpression invocation:
                    foreach (var argument in invocation.Arguments)
                        WalkExpression(argument, text, tokens, lines, hints);
                    break;

                case BoundNullConditionalExpression conditional:
                    WalkExpression(conditional.Receiver, text, tokens, lines, hints);
                    WalkExpression(conditional.Access, text, tokens, lines, hints);
                    break;

                case BoundNullAssertExpression assert:
                    WalkExpression(assert.Operand, text, tokens, lines, hints);
                    if (assert.Thrown is not null)
                        WalkExpression(assert.Thrown, text, tokens, lines, hints);
                    break;

                case BoundBinaryExpression binary:
                    WalkExpression(binary.Left, text, tokens, lines, hints);
                    WalkExpression(binary.Right, text, tokens, lines, hints);
                    break;

                case BoundUnaryExpression unary:
                    WalkExpression(unary.Operand, text, tokens, lines, hints);
                    break;

                case BoundAssignmentExpression assignment:
                    WalkExpression(assignment.Target, text, tokens, lines, hints);
                    WalkExpression(assignment.Value, text, tokens, lines, hints);
                    break;

                case BoundConditionalExpression conditionalExpression:
                    WalkExpression(conditionalExpression.Condition, text, tokens, lines, hints);
                    WalkExpression(conditionalExpression.WhenTrue, text, tokens, lines, hints);
                    WalkExpression(conditionalExpression.WhenFalse, text, tokens, lines, hints);
                    break;

                case BoundIndexExpression index:
                    WalkExpression(index.Target, text, tokens, lines, hints);
                    WalkExpression(index.Index, text, tokens, lines, hints);
                    break;

                case BoundConversionExpression conversion:
                    WalkExpression(conversion.Operand, text, tokens, lines, hints);
                    break;

                case BoundTypeTestExpression typeTest:
                    WalkExpression(typeTest.Operand, text, tokens, lines, hints);
                    break;

                case BoundTypeOfExpression typeOf:
                    if (typeOf.Operand is BoundExpression typeOfOperand)
                        WalkExpression(typeOfOperand, text, tokens, lines, hints);
                    break;

                case BoundArrayLiteralExpression array:
                    foreach (var element in array.Elements)
                        WalkExpression(element, text, tokens, lines, hints);
                    break;

                case BoundTupleLiteralExpression tuple:
                    foreach (var element in tuple.Elements)
                        WalkExpression(element, text, tokens, lines, hints);
                    break;

                case BoundDictLiteralExpression dict:
                    foreach (var entry in dict.Entries)
                    {
                        WalkExpression(entry.Key, text, tokens, lines, hints);
                        WalkExpression(entry.Value, text, tokens, lines, hints);
                    }

                    break;

                case BoundSwitchExpression switchExpression:
                    WalkExpression(switchExpression.Subject, text, tokens, lines, hints);
                    foreach (var arm in switchExpression.Arms)
                    {
                        foreach (var value in arm.Values)
                            WalkExpression(value, text, tokens, lines, hints);
                        WalkExpression(arm.Result, text, tokens, lines, hints);
                    }

                    break;

                case BoundInterpolatedStringExpression interpolated:
                    foreach (var part in interpolated.Parts)
                        WalkExpression(part, text, tokens, lines, hints);
                    break;

                case BoundFieldExpression field:
                    if (field.Receiver is not null)
                        WalkExpression(field.Receiver, text, tokens, lines, hints);
                    break;

                case BoundPropertyExpression property:
                    if (property.Receiver is not null)
                        WalkExpression(property.Receiver, text, tokens, lines, hints);
                    break;

                case BoundYieldExpression yieldExpression:
                    WalkExpression(yieldExpression.Value, text, tokens, lines, hints);
                    break;

                case BoundThrowExpression throwExpression:
                    WalkExpression(throwExpression.Value, text, tokens, lines, hints);
                    break;

                case BoundSequenceExpression sequence:
                    WalkStatement(sequence.Statement, text, tokens, lines, hints);
                    WalkExpression(sequence.Value, text, tokens, lines, hints);
                    break;
            }
        }

        // ------------------------------------------------------------------------------------
        // The three hint shapes
        // ------------------------------------------------------------------------------------

        /// <summary>A <c>: Type</c> hint after a local whose type was inferred, not written.</summary>
        /// <remarks>
        /// A local initialized directly by a <c>yield</c> or <c>yield from</c> is skipped: its
        /// inferred type is always <c>unknown</c>, and repeating that on every line of a coroutine
        /// is noise — the cast the type demands is already underlined by any use that needs one.
        /// Inference to <c>unknown</c> from anything else keeps hinting exactly as before.
        /// </remarks>
        private static void HintInferredType(
            BoundLocalDeclarationStatement declaration, string text, List<Token> tokens, TextLines lines, List<InlayHint> hints)
        {
            if (declaration.Syntax is not LocalDeclarationStatementSyntax localSyntax
                || localSyntax.Type is not null
                || declaration.Local.Type is null
                || declaration.Local.Type.IsVoid)
            {
                return;
            }

            if (declaration.Initializer is BoundYieldExpression)
                return;

            Token? nameToken = FirstNameToken(localSyntax.Span, tokens, declaration.Local.Name);
            if (nameToken is null)
                return;

            HintAt(nameToken.Value.Span.End, ": " + declaration.Local.Type.ToDisplayString(), InlayHintKinds.Type, lines, hints);
        }

        /// <summary>A <c>: ReturnType</c> hint after a lambda's body, when its return is not <c>void</c>.</summary>
        private static void HintLambdaReturnType(BoundLambdaExpression lambda, string text, TextLines lines, List<InlayHint> hints)
        {
            if (lambda.Type is not ClosureTypeSymbol closure || closure.ReturnType.IsVoid)
                return;

            HintAt(lambda.Syntax.Span.End, ": " + closure.ReturnType.ToDisplayString(), InlayHintKinds.Type, lines, hints);
        }

        /// <summary>
        /// A <c>name:</c> hint before a positional argument that does not identify its parameter —
        /// the value is a literal, or a variable whose name differs from the parameter's. An
        /// already-named argument (<c>spawn(x: 1.0)</c>) is left alone.
        /// </summary>
        private static void HintParameterNames(CallExpressionSyntax? callSyntax, IReadOnlyList<ParameterSymbol> parameters, TextLines lines, List<InlayHint> hints)
        {
            if (callSyntax is null || parameters.Count == 0)
                return;

            int count = Math.Min(callSyntax.Arguments.Count, parameters.Count);
            for (int i = 0; i < count; i++)
            {
                ArgumentSyntax argument = callSyntax.Arguments[i];
                if (argument.Name is not null)
                    continue;

                ParameterSymbol parameter = parameters[i];
                if (parameter.Name.Length == 0)
                    continue;

                if (argument.Value is IdentifierExpressionSyntax identifier
                    && identifier.Name == parameter.Name)
                {
                    continue;
                }

                HintAt(argument.Value.Span.Start.Position, parameter.Name + ":", InlayHintKinds.Parameter, lines, hints, paddingRight: true);
            }
        }

        /// <summary>The first identifier token in a span whose text is exactly <paramref name="name"/>.</summary>
        private static Token? FirstNameToken(SourceSpan span, List<Token> tokens, string name)
        {
            foreach (var token in tokens)
            {
                if (token.Span.Start.Position >= span.End)
                    break;
                if (token.Span.End <= span.Start.Position)
                    continue;

                if (token.Type == TokenType.Identifier && token.Lexeme.ToString() == name)
                    return token;
            }

            return null;
        }

        private static void HintAt(int offset, string label, int kind, TextLines lines, List<InlayHint> hints, bool paddingRight = false)
        {
            if (offset < 0)
                return;

            var position = lines.PositionAt(offset);
            hints.Add(new InlayHint
            {
                Position = new Position(position.Line, position.Character),
                Label = label,
                Kind = kind,
                PaddingRight = paddingRight,
            });
        }
    }
}