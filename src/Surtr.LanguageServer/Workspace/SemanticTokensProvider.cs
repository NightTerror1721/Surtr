#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;
using Surtr.LanguageServer.Protocol;

namespace Surtr.LanguageServer.Workspace
{
    /// <summary>
    /// The <c>textDocument/semanticTokens/full</c> handler — the one place this server can resolve
    /// a contextual keyword (§1.2: <c>this</c>, <c>super</c>, <c>value</c>, <c>attribute</c>, plus
    /// the by-position-only <c>get</c>/<c>set</c>, §3.4) by the same rule the real parser and binder
    /// use, rather than the position-blind regex lookahead <c>vscode-surtr/syntaxes/surtr.tmLanguage.json</c>
    /// has to settle for. A client merges semantic tokens over its TextMate grammar, so this only
    /// needs to cover what a regex genuinely cannot: it does not re-tag comments, strings, literals
    /// or ordinary identifiers, all of which the grammar already gets right on its own.
    /// </summary>
    /// <remarks>
    /// Everything here is tagged as the single <c>keyword</c> token type with no modifiers — the
    /// point of this pass is *which spans* are really a contextual keyword, not a second type
    /// hierarchy (hover and the "in module ..." card already answer where a type comes from).
    /// </remarks>
    public static class SemanticTokensProvider
    {
        /// <summary>The fixed, single-entry legend this server declares in its capabilities.</summary>
        public static readonly string[] TokenTypes = { "keyword" };

        public static readonly string[] TokenModifiers = Array.Empty<string>();

        private const int KeywordTokenType = 0;

        public static SemanticTokens Compute(CompilationSnapshot snapshot, string filePath, string text)
        {
            var spans = new List<SourceSpan>();

            if (!snapshot.IsEmpty && snapshot.Binder is not null)
                CollectThisAndSuper(snapshot.Binder, filePath, text.Length, spans);

            var unit = snapshot.UnitFor(filePath);
            if (unit is not null)
            {
                var tokens = new Lexer(SurtrSourceBuffer.FromString(text, filePath)).Tokenize();
                CollectDeclarationKeywords(unit.Syntax.Declarations, tokens, spans);
            }

            return Encode(spans, text);
        }

        // ------------------------------------------------------------------------------------
        // this / super — resolved from the bound tree, so a variable that happens to be named
        // "this" or "super" outside a member body (legal per §1.2 - both are contextual, not
        // reserved) never produces a BoundThisExpression and never appears here.
        // ------------------------------------------------------------------------------------

        private static void CollectThisAndSuper(Binder binder, string filePath, int textLength, List<SourceSpan> spans)
        {
            foreach (var pair in binder.Bodies)
            {
                if (!binder.BodyFiles.TryGetValue(pair.Key, out string? bodyFile) || !SameFile(bodyFile, filePath))
                    continue;
                if (pair.Value.Span.End > textLength)
                    continue;

                WalkStatement(pair.Value, spans);
            }

            foreach (var initializer in binder.FieldInitializers)
            {
                if (initializer.Value.Span.End <= textLength)
                    WalkExpression(initializer.Value, spans);
            }

            foreach (var block in binder.StaticBlocks)
                WalkStatement(block.Body, spans);

            foreach (var chain in binder.ConstructorChains.Values)
            {
                foreach (var argument in chain.Arguments)
                    WalkExpression(argument, spans);
            }
        }

        private static void WalkStatement(BoundStatement statement, List<SourceSpan> spans)
        {
            switch (statement)
            {
                case BoundBlockStatement block:
                    foreach (var child in block.Statements)
                        WalkStatement(child, spans);
                    break;

                case BoundLocalDeclarationStatement declaration:
                    if (declaration.Initializer is not null)
                        WalkExpression(declaration.Initializer, spans);
                    break;

                case BoundExpressionStatement expressionStatement:
                    WalkExpression(expressionStatement.Expression, spans);
                    break;

                case BoundIfStatement ifStatement:
                    WalkExpression(ifStatement.Condition, spans);
                    WalkStatement(ifStatement.Then, spans);
                    if (ifStatement.Else is not null)
                        WalkStatement(ifStatement.Else, spans);
                    break;

                case BoundWhileStatement whileStatement:
                    WalkExpression(whileStatement.Condition, spans);
                    WalkStatement(whileStatement.Body, spans);
                    break;

                case BoundForStatement forStatement:
                    if (forStatement.Initializer is not null)
                        WalkStatement(forStatement.Initializer, spans);
                    if (forStatement.Condition is not null)
                        WalkExpression(forStatement.Condition, spans);
                    if (forStatement.Step is not null)
                        WalkExpression(forStatement.Step, spans);
                    WalkStatement(forStatement.Body, spans);
                    break;

                case BoundForInStatement forIn:
                    WalkExpression(forIn.Sequence, spans);
                    WalkStatement(forIn.Body, spans);
                    break;

                case BoundSwitchStatement switchStatement:
                    WalkExpression(switchStatement.Subject, spans);
                    foreach (var section in switchStatement.Sections)
                    {
                        foreach (var label in section.Labels)
                            WalkExpression(label, spans);
                        foreach (var child in section.Statements)
                            WalkStatement(child, spans);
                    }
                    break;

                case BoundTryStatement tryStatement:
                    WalkStatement(tryStatement.Body, spans);
                    foreach (var clause in tryStatement.Catches)
                        WalkStatement(clause.Body, spans);
                    if (tryStatement.Finally is not null)
                        WalkStatement(tryStatement.Finally, spans);
                    break;

                case BoundThrowStatement throwStatement:
                    WalkExpression(throwStatement.Value, spans);
                    break;

                case BoundReturnStatement returnStatement:
                    if (returnStatement.Value is not null)
                        WalkExpression(returnStatement.Value, spans);
                    break;

                case BoundLabeledStatement labeled:
                    WalkStatement(labeled.Statement, spans);
                    break;
            }
        }

        private static void WalkExpression(BoundExpression expression, List<SourceSpan> spans)
        {
            if (expression is BoundThisExpression thisExpression)
                spans.Add(thisExpression.Span);

            foreach (var child in ChildrenOf(expression))
                WalkExpression(child, spans);
        }

        /// <summary>The immediate sub-expressions of a node — same shape as the other LSP walkers.</summary>
        private static IEnumerable<BoundExpression> ChildrenOf(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundCallExpression call:
                    if (call.Receiver is not null)
                        yield return call.Receiver;
                    foreach (var argument in call.Arguments)
                        yield return argument;
                    break;

                case BoundNullConditionalExpression conditional:
                    yield return conditional.Receiver;
                    yield return conditional.Access;
                    break;

                case BoundClosureInvocationExpression invocation:
                    yield return invocation.Callee;
                    foreach (var argument in invocation.Arguments)
                        yield return argument;
                    break;

                case BoundObjectCreationExpression creation:
                    foreach (var argument in creation.Arguments)
                        yield return argument;
                    break;

                case BoundCollectionCreationExpression collection:
                    if (collection.Capacity is not null)
                        yield return collection.Capacity;
                    if (collection.Source is not null)
                        yield return collection.Source;
                    if (collection.Source2 is not null)
                        yield return collection.Source2;
                    if (collection.DefaultValue is not null)
                        yield return collection.DefaultValue;
                    if (collection.Thrown is not null)
                        yield return collection.Thrown;
                    break;

                case BoundBinaryExpression binary:
                    yield return binary.Left;
                    yield return binary.Right;
                    break;

                case BoundUnaryExpression unary:
                    yield return unary.Operand;
                    break;

                case BoundAssignmentExpression assignment:
                    yield return assignment.Target;
                    yield return assignment.Value;
                    break;

                case BoundConditionalExpression conditionalExpression:
                    yield return conditionalExpression.Condition;
                    yield return conditionalExpression.WhenTrue;
                    yield return conditionalExpression.WhenFalse;
                    break;

                case BoundIndexExpression index:
                    yield return index.Target;
                    yield return index.Index;
                    break;

                case BoundNullAssertExpression assertion:
                    yield return assertion.Operand;
                    if (assertion.Thrown is not null)
                        yield return assertion.Thrown;
                    break;

                case BoundConversionExpression conversion:
                    yield return conversion.Operand;
                    break;

                case BoundTypeTestExpression typeTest:
                    yield return typeTest.Operand;
                    break;

                case BoundTypeOfExpression typeOf:
                    if (typeOf.Operand is not null)
                        yield return typeOf.Operand;
                    break;

                case BoundArrayLiteralExpression array:
                    foreach (var element in array.Elements)
                        yield return element;
                    break;

                case BoundTupleLiteralExpression tuple:
                    foreach (var element in tuple.Elements)
                        yield return element;
                    break;

                case BoundDictLiteralExpression dict:
                    foreach (var entry in dict.Entries)
                    {
                        yield return entry.Key;
                        yield return entry.Value;
                    }
                    break;

                case BoundInterpolatedStringExpression interpolated:
                    foreach (var part in interpolated.Parts)
                        yield return part;
                    break;

                case BoundSwitchExpression switchExpression:
                    foreach (var arm in switchExpression.Arms)
                    {
                        foreach (var value in arm.Values)
                            yield return value;
                        yield return arm.Result;
                    }
                    break;

                case BoundFieldExpression field:
                    if (field.Receiver is not null)
                        yield return field.Receiver;
                    break;

                case BoundPropertyExpression property:
                    if (property.Receiver is not null)
                        yield return property.Receiver;
                    break;
            }
        }

        private static bool SameFile(string a, string b)
            => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------------------------
        // value / attribute / get / set — resolved from the parsed declaration syntax, which
        // already carries exactly the position rule §1.2/§3.4 describe: recognized only where the
        // grammar branches on them, never merely because the text matches.
        // ------------------------------------------------------------------------------------

        private static void CollectDeclarationKeywords(IReadOnlyList<DeclarationSyntax> declarations, List<Token> tokens, List<SourceSpan> spans)
        {
            foreach (var declaration in declarations)
            {
                if (declaration is not TypeDeclarationSyntax type)
                    continue;

                if (type.Kind == TypeDeclarationKind.ValueClass)
                {
                    if (FindKeywordToken(type.Span, tokens, "value") is Token valueToken)
                        spans.Add(valueToken.Span);
                }

                if (type.IsAttribute)
                {
                    if (FindKeywordToken(type.Span, tokens, "attribute") is Token attributeToken)
                        spans.Add(attributeToken.Span);
                }

                foreach (var member in type.Members)
                {
                    if (member is PropertyDeclarationSyntax property)
                    {
                        foreach (var accessor in property.Accessors)
                        {
                            string keyword = accessor.IsGetter ? "get" : "set";
                            if (FindKeywordToken(accessor.Span, tokens, keyword) is Token accessorToken)
                                spans.Add(accessorToken.Span);
                        }
                    }
                }

                CollectDeclarationKeywords(type.Members, tokens, spans);
            }
        }

        /// <summary>The first token inside a span whose text is exactly <paramref name="lexeme"/>.</summary>
        private static Token? FindKeywordToken(SourceSpan span, List<Token> tokens, string lexeme)
        {
            foreach (var token in tokens)
            {
                if (token.Span.Start.Position >= span.End)
                    break;
                if (token.Span.Start.Position < span.Start.Position)
                    continue;

                if (token.Type == TokenType.Identifier && token.Lexeme.Span.SequenceEqual(lexeme.AsSpan()))
                    return token;
            }

            return null;
        }

        // ------------------------------------------------------------------------------------
        // Encoding
        // ------------------------------------------------------------------------------------

        private static SemanticTokens Encode(List<SourceSpan> spans, string text)
        {
            spans.Sort((a, b) => a.Start.Position.CompareTo(b.Start.Position));

            var lines = TextLines.Index(text);
            var data = new List<int>(spans.Count * 5);

            int previousLine = 0;
            int previousChar = 0;

            foreach (var span in spans)
            {
                var position = lines.PositionAt(span.Start.Position);
                int deltaLine = position.Line - previousLine;
                int deltaChar = deltaLine == 0 ? position.Character - previousChar : position.Character;

                data.Add(deltaLine);
                data.Add(deltaChar);
                data.Add(span.Length);
                data.Add(KeywordTokenType);
                data.Add(0);

                previousLine = position.Line;
                previousChar = position.Character;
            }

            return new SemanticTokens { Data = data };
        }
    }
}
