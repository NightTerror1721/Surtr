#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;

namespace Surtr.LanguageServer.Workspace
{
    /// <summary>What a hover or a definition resolved to: text to show, and where the declaration is.</summary>
    public sealed class ResolvedHit
    {
        /// <summary>The markdown to display, or <see langword="null"/> when there is nothing to say.</summary>
        public string? Markdown { get; set; }

        /// <summary>The anchor (the identifier the cursor is on), as an offset and length.</summary>
        public int AnchorStart { get; set; }

        public int AnchorLength { get; set; }

        /// <summary>The declaration's file, when the resolved symbol is declared in this workspace.</summary>
        public string? DefinitionFile { get; set; }

        /// <summary>The declaration's span in that file, as an offset and length.</summary>
        public int DefinitionStart { get; set; }

        public int DefinitionLength { get; set; }

        public bool HasDefinition => DefinitionFile is not null;
    }

    /// <summary>
    /// Turns a cursor position in a file into something an editor can show: the signature of the
    /// method, field, property, local or parameter the cursor names, or the declaration it was
    /// written on. Three passes, in order:
    /// <list type="number">
    /// <item>the <b>bound tree</b> â€” the deepest bound node whose written name is the hovered
    /// identifier answers with the exact symbol binding produced;</item>
    /// <item>the <b>declaration</b> â€” when the cursor sits on a declaration site (a method's name,
    /// a parameter, a type annotation), the source itself answers;</item>
    /// <item>the <b>type-name</b> heuristic â€” a bare identifier that names a declared type or alias
    /// in the module gets that type's card.</item>
    /// </list>
    /// A body position almost always lands on pass one, which is why the language features resolve
    /// exactly: an <c>int</c> reaching a <c>float</c> parameter hovers with the substituted type,
    /// and a call on a constructed generic hovers with the substituted signature.
    /// </summary>
    public static class SymbolResolver
    {
        /// <summary>Resolves the identifier under a cursor position.</summary>
        public static ResolvedHit? Resolve(CompilationSnapshot snapshot, string filePath, string text, int position)
        {
            var tokens = new Lexer(SurtrSourceBuffer.FromString(text, filePath)).Tokenize();
            Token? hovered = TokenAt(tokens, position);
            if (hovered is null)
                return null;

            Token anchor;
            if (IsNameLike(hovered.Value))
            {
                anchor = hovered.Value;
            }
            else if (NameInsideInterpolation(text, hovered.Value, position) is Token inside)
            {
                anchor = inside;
            }
            else
            {
                return null;
            }

            Hit? hit = BoundHit(snapshot, filePath, text, tokens, position, anchor)
                ?? DeclarationHit(snapshot, filePath, text, tokens, position, anchor)
                ?? TypeNameHit(snapshot, filePath, text, tokens, position, anchor);

            if (hit is null)
                return null;

            return new ResolvedHit
            {
                Markdown = hit.Markdown,
                AnchorStart = hit.AnchorStart,
                AnchorLength = hit.AnchorLength,
                DefinitionFile = hit.DefinitionFile,
                DefinitionStart = hit.DefinitionStart,
                DefinitionLength = hit.DefinitionLength,
            };
        }

        /// <summary>One resolved hit, before it is copied out to the public shape.</summary>
        private sealed class Hit
        {
            public string? Markdown { get; set; }

            public int AnchorStart { get; set; }

            public int AnchorLength { get; set; }

            public string? DefinitionFile { get; set; }

            public int DefinitionStart { get; set; }

            public int DefinitionLength { get; set; }
        }

        // ------------------------------------------------------------------------------------
        // Pass one: the bound tree.
        // ------------------------------------------------------------------------------------

        private static Hit? BoundHit(CompilationSnapshot snapshot, string filePath, string text, List<Token> tokens, int position, Token anchor)
        {
            if (snapshot.IsEmpty || snapshot.Binder is null)
                return null;

            Best best = default;

            foreach (var pair in snapshot.Binder.Bodies)
            {
                // Walk only the bodies written in this file; a span from another file has its own
                // coordinate space and must not be interpreted against this one.
                if (!snapshot.Binder.BodyFiles.TryGetValue(pair.Key, out string? bodyFile))
                    continue;

                if (!string.Equals(Path.GetFullPath(bodyFile), Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase))
                    continue;

                WalkStatement(pair.Value, position, anchor, tokens, ref best, snapshot);
            }

            foreach (var initializer in snapshot.Binder.FieldInitializers)
            {
                if (initializer.Value.Span.End <= text.Length)
                    WalkExpression(initializer.Value, position, anchor, tokens, ref best, snapshot);
            }

            foreach (var block in snapshot.Binder.StaticBlocks)
                WalkStatement(block.Body, position, anchor, tokens, ref best, snapshot);

            foreach (var chain in snapshot.Binder.ConstructorChains.Values)
            {
                foreach (var argument in chain.Arguments)
                    WalkExpression(argument, position, anchor, tokens, ref best, snapshot);
            }

            return best.Hit;
        }

        /// <summary>The deepest candidate found so far, preferring a symbol over a bare type.</summary>
        private struct Best
        {
            public Hit? Hit;

            public int SpanLength;

            public int Priority;

            public void Consider(Hit hit, int spanLength, int priority)
            {
                // A smaller span is a deeper node; a higher priority is a symbol over a bare type.
                if (Hit is null || priority > Priority || (priority == Priority && spanLength < SpanLength))
                {
                    Hit = hit;
                    SpanLength = spanLength;
                    Priority = priority;
                }
            }
        }

        private static void WalkStatement(BoundStatement statement, int position, Token anchor, List<Token> tokens, ref Best best, CompilationSnapshot snapshot)
        {
            if (!statement.Span.Contains(position))
                return;

            switch (statement)
            {
                case BoundBlockStatement block:
                    foreach (var child in block.Statements)
                        WalkStatement(child, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundExpressionStatement expression:
                    WalkExpression(expression.Expression, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundLocalDeclarationStatement declaration:
                    if (NameTokenMatchesFirst(declaration.Syntax.Span, anchor, tokens))
                        best.Consider(FromSymbol(declaration.Local, declaration.Syntax.Span, snapshot), declaration.Syntax.Span.Length, PrioritySymbol);
                    if (declaration.Initializer is not null)
                        WalkExpression(declaration.Initializer, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundIfStatement ifStatement:
                    WalkExpression(ifStatement.Condition, position, anchor, tokens, ref best, snapshot);
                    WalkStatement(ifStatement.Then, position, anchor, tokens, ref best, snapshot);
                    if (ifStatement.Else is not null)
                        WalkStatement(ifStatement.Else, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundWhileStatement whileStatement:
                    WalkExpression(whileStatement.Condition, position, anchor, tokens, ref best, snapshot);
                    WalkStatement(whileStatement.Body, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundForStatement forStatement:
                    if (forStatement.Initializer is not null)
                        WalkStatement(forStatement.Initializer, position, anchor, tokens, ref best, snapshot);
                    if (forStatement.Condition is not null)
                        WalkExpression(forStatement.Condition, position, anchor, tokens, ref best, snapshot);
                    if (forStatement.Step is not null)
                        WalkExpression(forStatement.Step, position, anchor, tokens, ref best, snapshot);
                    WalkStatement(forStatement.Body, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundForInStatement forIn:
                    WalkExpression(forIn.Sequence, position, anchor, tokens, ref best, snapshot);
                    WalkStatement(forIn.Body, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundSwitchStatement switchStatement:
                    WalkExpression(switchStatement.Subject, position, anchor, tokens, ref best, snapshot);
                    foreach (var section in switchStatement.Sections)
                    {
                        foreach (var label in section.Labels)
                            WalkExpression(label, position, anchor, tokens, ref best, snapshot);
                        foreach (var child in section.Statements)
                            WalkStatement(child, position, anchor, tokens, ref best, snapshot);
                    }

                    break;

                case BoundTryStatement tryStatement:
                    WalkStatement(tryStatement.Body, position, anchor, tokens, ref best, snapshot);
                    foreach (var clause in tryStatement.Catches)
                        WalkStatement(clause.Body, position, anchor, tokens, ref best, snapshot);
                    if (tryStatement.Finally is not null)
                        WalkStatement(tryStatement.Finally, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundThrowStatement throwStatement:
                    WalkExpression(throwStatement.Value, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundReturnStatement returnStatement:
                    if (returnStatement.Value is not null)
                        WalkExpression(returnStatement.Value, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundLabeledStatement labeled:
                    WalkStatement(labeled.Statement, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundNopStatement:
                case BoundBreakStatement:
                default:
                    break;
            }
        }

        /// <summary>Visits an expression and everything under it, looking for the hovered name.</summary>
        /// <remarks>
        /// <para>
        /// Deliberately not pruned on whether the node's own span holds the cursor, which is a
        /// robustness choice rather than a necessary one. A parent whose span fails to cover a child
        /// makes pruning discard the very subtree the cursor is in, and the symptom is invisible: a
        /// hover that answers everywhere except over one kind of sub-expression. Every span covers
        /// its children today, but nothing enforces that, and hover should not be what fails when a
        /// new production gets it wrong.
        /// </para>
        /// <para>
        /// Nothing is matched more loosely as a result: both name tests require the anchor to fall
        /// within the node being considered, so a node the cursor is outside of still cannot claim
        /// it. The statement walk keeps its own containment check, which bounds the work to the one
        /// statement the cursor is in.
        /// </para>
        /// </remarks>
        private static void WalkExpression(BoundExpression expression, int position, Token anchor, List<Token> tokens, ref Best best, CompilationSnapshot snapshot)
        {
            switch (expression)
            {
                case BoundErrorExpression:
                case BoundLiteralExpression:
                case BoundConditionalReceiver:
                    break;

                case BoundLocalExpression local:
                    ConsiderName(local, local.Local, local.Span, anchor, tokens, ref best, snapshot);
                    break;

                case BoundParameterExpression parameter:
                    ConsiderName(parameter, parameter.Parameter, parameter.Span, anchor, tokens, ref best, snapshot);
                    break;

                case BoundThisExpression thisExpression:
                    ConsiderName(thisExpression, thisExpression.Type, thisExpression.Span, anchor, tokens, ref best, snapshot);
                    break;

                case BoundFieldExpression field:
                    ConsiderName(field, field.Field, field.Span, anchor, tokens, ref best, snapshot);
                    if (field.Receiver is not null)
                        WalkExpression(field.Receiver, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundPropertyExpression property:
                    ConsiderName(property, property.Property, property.Span, anchor, tokens, ref best, snapshot);
                    if (property.Receiver is not null)
                        WalkExpression(property.Receiver, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundCallExpression call:
                    ConsiderName(call, call.Method, call.Span, anchor, tokens, ref best, snapshot);
                    if (call.Receiver is not null)
                        WalkExpression(call.Receiver, position, anchor, tokens, ref best, snapshot);
                    foreach (var argument in call.Arguments)
                        WalkExpression(argument, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundNullConditionalExpression conditional:
                    WalkExpression(conditional.Receiver, position, anchor, tokens, ref best, snapshot);
                    WalkExpression(conditional.Access, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundNullAssertExpression assert:
                    WalkExpression(assert.Operand, position, anchor, tokens, ref best, snapshot);
                    if (assert.Thrown is not null)
                        WalkExpression(assert.Thrown, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundClosureInvocationExpression invocation:
                    ConsiderName(invocation, invocation.Type, invocation.Span, anchor, tokens, ref best, snapshot);
                    WalkExpression(invocation.Callee, position, anchor, tokens, ref best, snapshot);
                    foreach (var argument in invocation.Arguments)
                        WalkExpression(argument, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundObjectCreationExpression creation:
                    ConsiderName(creation, creation.Type, creation.Span, anchor, tokens, ref best, snapshot);
                    if (creation.Constructor is not null)
                        ConsiderName(creation, creation.Constructor, creation.Span, anchor, tokens, ref best, snapshot);
                    foreach (var argument in creation.Arguments)
                        WalkExpression(argument, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundCollectionCreationExpression collection:
                    // `array<T>`/`dict<K,V>`/`tuple<...>` resolve straight onto the composite
                    // TypeSymbol the equivalent symbolic form (`T[]`, `{K:V}`, `(T1,...)`) would, so
                    // there is no separate callee node naming it - the identifier ("array", "dict" or
                    // "tuple") is always the call's first name token, which is what NameTokenMatchesLast
                    // would miss here: the last identifier-like token in the span is a type argument
                    // (e.g. the "int" in "array<int>(5)"), not the callee.
                    if (NameTokenMatchesFirst(collection.Syntax.Span, anchor, tokens))
                        best.Consider(FromType(collection.Type, collection.Syntax.Span), collection.Syntax.Span.Length, PriorityType);
                    if (collection.Capacity is not null)
                        WalkExpression(collection.Capacity, position, anchor, tokens, ref best, snapshot);
                    if (collection.Source is not null)
                        WalkExpression(collection.Source, position, anchor, tokens, ref best, snapshot);
                    if (collection.Source2 is not null)
                        WalkExpression(collection.Source2, position, anchor, tokens, ref best, snapshot);
                    if (collection.DefaultValue is not null)
                        WalkExpression(collection.DefaultValue, position, anchor, tokens, ref best, snapshot);
                    if (collection.Thrown is not null)
                        WalkExpression(collection.Thrown, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundBinaryExpression binary:
                    WalkExpression(binary.Left, position, anchor, tokens, ref best, snapshot);
                    WalkExpression(binary.Right, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundUnaryExpression unary:
                    WalkExpression(unary.Operand, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundAssignmentExpression assignment:
                    WalkExpression(assignment.Target, position, anchor, tokens, ref best, snapshot);
                    WalkExpression(assignment.Value, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundConditionalExpression conditionalExpression:
                    WalkExpression(conditionalExpression.Condition, position, anchor, tokens, ref best, snapshot);
                    WalkExpression(conditionalExpression.WhenTrue, position, anchor, tokens, ref best, snapshot);
                    WalkExpression(conditionalExpression.WhenFalse, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundIndexExpression index:
                    WalkExpression(index.Target, position, anchor, tokens, ref best, snapshot);
                    WalkExpression(index.Index, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundConversionExpression conversion:
                    if (conversion.IsExplicit)
                        ConsiderName(conversion, conversion.Type, conversion.Span, anchor, tokens, ref best, snapshot);
                    WalkExpression(conversion.Operand, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundTypeTestExpression typeTest:
                    ConsiderName(typeTest, typeTest.TestedType, typeTest.Span, anchor, tokens, ref best, snapshot);
                    WalkExpression(typeTest.Operand, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundTypeOfExpression typeOf:
                    if (typeOf.TargetType is TypeSymbol typeOfTarget)
                        ConsiderName(typeOf, typeOfTarget, typeOf.Span, anchor, tokens, ref best, snapshot);
                    if (typeOf.Operand is BoundExpression typeOfOperand)
                        WalkExpression(typeOfOperand, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundLambdaExpression lambda:
                    WalkStatement(lambda.Body, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundArrayLiteralExpression array:
                    foreach (var element in array.Elements)
                        WalkExpression(element, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundTupleLiteralExpression tuple:
                    foreach (var element in tuple.Elements)
                        WalkExpression(element, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundDictLiteralExpression dict:
                    foreach (var entry in dict.Entries)
                    {
                        WalkExpression(entry.Key, position, anchor, tokens, ref best, snapshot);
                        WalkExpression(entry.Value, position, anchor, tokens, ref best, snapshot);
                    }

                    break;

                case BoundCollectionBuildExpression build:
                    foreach (var argument in build.ConstructorArguments)
                        WalkExpression(argument, position, anchor, tokens, ref best, snapshot);

                    foreach (var fillArgs in build.FillArguments)
                    {
                        foreach (var argument in fillArgs)
                            WalkExpression(argument, position, anchor, tokens, ref best, snapshot);
                    }

                    break;

                case BoundSwitchExpression switchExpression:
                    WalkExpression(switchExpression.Subject, position, anchor, tokens, ref best, snapshot);
                    foreach (var arm in switchExpression.Arms)
                    {
                        foreach (var value in arm.Values)
                            WalkExpression(value, position, anchor, tokens, ref best, snapshot);
                        WalkExpression(arm.Result, position, anchor, tokens, ref best, snapshot);
                    }

                    break;

                case BoundInterpolatedStringExpression interpolated:
                    foreach (var part in interpolated.Parts)
                        WalkExpression(part, position, anchor, tokens, ref best, snapshot);
                    break;

                // A yield's operand is an ordinary expression; without this case nothing written
                // inside it resolves — a hover on `compute` in `let x = yield compute(1);` would
                // answer nothing at all. The yield node itself has no name to offer.
                case BoundYieldExpression yieldExpression:
                    WalkExpression(yieldExpression.Value, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundThrowExpression throwExpression:
                    WalkExpression(throwExpression.Value, position, anchor, tokens, ref best, snapshot);
                    break;

                case BoundSequenceExpression sequence:
                    WalkStatement(sequence.Statement, position, anchor, tokens, ref best, snapshot);
                    WalkExpression(sequence.Value, position, anchor, tokens, ref best, snapshot);
                    break;

                default:
                    break;
            }
        }

        /// <summary>
        /// Whether the anchor token is the name the node is written with. The written name of a
        /// member access or call is its <em>last</em> identifier; the written name of a declaration
        /// is its <em>first</em>. Comparing token spans rather than text is what keeps a hover on
        /// <c>player</c> in <c>player.hp</c> from resolving to <c>hp</c>.
        /// </summary>
        private static bool NameTokenMatchesLast(SourceSpan nodeSpan, Token anchor, List<Token> tokens)
        {
            Token? last = LastNameToken(nodeSpan, tokens);
            return last.HasValue && last.Value.Span.Equals(anchor.Span);
        }

        /// <summary>Whether the hovered token is the one spelling this node's symbol.</summary>
        /// <remarks>
        /// <para>
        /// Taking the last name token of a node's span only identifies the symbol when the node ends
        /// with its own name, which is one shape among several: a call runs on through its argument
        /// list, so the last name in <c>arr.push(t.val2)</c> is <c>val2</c> and the method the node
        /// actually names can never match. That left a hover on any called method, and on any
        /// receiver in front of one, resolving to nothing at all.
        /// </para>
        /// <para>
        /// Matching the name by text within the node's own span covers every shape instead. Two
        /// occurrences of one name stay apart because the anchor has to fall inside the node being
        /// considered, and <see cref="Best"/> keeps the smallest span — in <c>this.value = value</c>
        /// the field's span holds only the left one and the parameter's only the right.
        /// </para>
        /// </remarks>
        private static bool NameTokenMatchesSymbol(SourceSpan nodeSpan, object symbolOrType, Token anchor)
        {
            string name = symbolOrType switch
            {
                TypeSymbol type => type.Name,

                // A constructor is written as its class's name (`Vec2(1.0, 2.0)`), never as its
                // symbol name ("ctor"), so the hover anchor must be compared against the type it
                // constructs for the constructor to claim it.
                MethodSymbol method when method.Role == MethodRole.Constructor =>
                    method.ContainingType?.Name ?? method.Name,

                Symbol symbol => symbol.Name,
                _ => string.Empty,
            };

            if (name.Length == 0)
                return false;

            return anchor.Span.Start.Position >= nodeSpan.Start.Position
                && anchor.Span.End <= nodeSpan.End
                && string.Equals(anchor.Lexeme.ToString(), name, StringComparison.Ordinal);
        }

        private static bool NameTokenMatchesFirst(SourceSpan nodeSpan, Token anchor, List<Token> tokens)
        {
            Token? first = FirstNameToken(nodeSpan, tokens);
            return first.HasValue && first.Value.Span.Equals(anchor.Span);
        }

        private static void ConsiderName(BoundNode node, object symbolOrType, SourceSpan nodeSpan, Token anchor, List<Token> tokens, ref Best best, CompilationSnapshot snapshot)
        {
            if (!NameTokenMatchesLast(node.Syntax.Span, anchor, tokens)
                && !NameTokenMatchesSymbol(node.Syntax.Span, symbolOrType, anchor))
            {
                return;
            }

            Hit hit;
            int priority;
            switch (symbolOrType)
            {
                case TypeSymbol type:
                    // TypeSymbol derives from Symbol, so this case must come first: a type hovered
                    // as a type stays a type card, not a method's signature.
                    hit = FromType(type, nodeSpan);
                    priority = PriorityType;
                    break;

                case Symbol symbol:
                    hit = FromSymbol(symbol, nodeSpan, snapshot);
                    priority = PrioritySymbol;
                    break;

                default:
                    return;
            }

            best.Consider(hit, nodeSpan.Length, priority);
        }

        private const int PriorityType = 0;
        private const int PrioritySymbol = 1;

        private static bool IsNameLike(Token token)
            => token.Type == TokenType.Identifier || token.Type == TokenType.KeywordConstructor;

        /// <summary>
        /// The name the cursor is on inside an interpolated literal, or <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The lexer hands an interpolated literal back whole — splitting it means parsing the
        /// spliced expressions, which is the parser's job — so a cursor anywhere inside one lands on
        /// a string token and never on the name under it. The name has to be recovered from the text
        /// to have an anchor at all.
        /// </para>
        /// <para>
        /// What makes the anchor usable is that the parser scans a <c>${...}</c> hole in place, so
        /// the nodes inside it are spanned against this same file: an anchor cut from the text lines
        /// up with them. Only an interpolated literal is opened up this way. An ordinary string is
        /// text, and a hover over the word <c>total</c> in <c>"total"</c> should stay silent.
        /// </para>
        /// </remarks>
        private static Token? NameInsideInterpolation(string text, Token literal, int position)
        {
            if (literal.Type != TokenType.InterpolatedStringLiteral)
                return null;

            if (position < literal.Span.Start.Position || position >= literal.Span.End || position >= text.Length)
                return null;

            if (!IsIdentifierPart(text[position]))
                return null;

            int start = position;
            while (start > literal.Span.Start.Position && IsIdentifierPart(text[start - 1]))
                start--;

            int end = position;
            while (end + 1 < literal.Span.End && IsIdentifierPart(text[end + 1]))
                end++;

            // A run starting with a digit is a number, not a name.
            if (!IsIdentifierStart(text[start]))
                return null;

            // A literal cannot span lines, so the column is a count of characters from its start.
            var location = new SourceLocation(
                literal.Span.Start.Line,
                literal.Span.Start.Column + (start - literal.Span.Start.Position),
                start);

            return new Token(TokenType.Identifier, text.AsMemory(start, end - start + 1), location);
        }

        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

        private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>The token a position falls on, if any.</summary>
        private static Token? TokenAt(List<Token> tokens, int position)
        {
            foreach (var token in tokens)
            {
                if (token.Span.Contains(position))
                    return token;
            }

            return null;
        }

        /// <summary>The first identifier-like token inside a span, or <see langword="null"/>.</summary>
        private static Token? FirstNameToken(SourceSpan span, List<Token> tokens)
        {
            foreach (var token in tokens)
            {
                if (token.Span.End <= span.Start.Position)
                    continue;
                if (token.Span.Start.Position >= span.End)
                    break;
                if (IsNameLike(token))
                    return token;
            }

            return null;
        }

        /// <summary>The last identifier-like token inside a span, or <see langword="null"/>.</summary>
        private static Token? LastNameToken(SourceSpan span, List<Token> tokens)
        {
            Token? last = null;
            foreach (var token in tokens)
            {
                if (token.Span.Start.Position >= span.End)
                    break;
                if (token.Span.End > span.Start.Position && IsNameLike(token))
                    last = token;
            }

            return last;
        }

        // ------------------------------------------------------------------------------------
        // Pass two: a declaration site.
        // ------------------------------------------------------------------------------------

        private static Hit? DeclarationHit(CompilationSnapshot snapshot, string filePath, string text, List<Token> tokens, int position, Token anchor)
        {
            if (snapshot.IsEmpty)
                return null;

            var unit = snapshot.UnitFor(filePath);
            if (unit is null)
                return null;

            // An enum case (§2.4) is not a declaration, so the declaration walk below never sees
            // it; answer the case's own card before the containing type claims the position.
            var enumCase = FindContainingEnumCase(unit.Syntax, position);
            if (enumCase.Case is not null)
                return EnumCaseHit((enumCase.Case, enumCase.Containing!), anchor);

            var declaration = FindContainingDeclaration(unit.Syntax, position);
            if (declaration is null)
                return null;

            // A parameter the cursor is on answers with its own declaration.
            foreach (var parameter in ParametersOf(declaration))
            {
                if (parameter.Span.Contains(position))
                {
                    string typeText = parameter.Type is null ? "unknown" : TextOf(text, parameter.Type.Span);
                    string label = "let " + parameter.Name + ": " + typeText + (parameter.IsVarargs ? " ..." : string.Empty);
                    return new Hit
                    {
                        Markdown = "```surtr\n" + label + "\n```\n\nparameter of " + ShortName(declaration) + ".",
                        AnchorStart = anchor.Span.Start.Position,
                        AnchorLength = anchor.Span.Length,
                    };
                }
            }

            // A type the cursor is on answers with that type's card.
            var typeSyntax = InnermostType(declaration, position);
            if (typeSyntax is not null)
                return TypeCard(snapshot, filePath, text, typeSyntax, anchor);

            // The header, not the body: the declaration's own signature.
            if (IsInHeader(declaration, position, tokens))
            {
                string signature = SignatureText(declaration, text, tokens);
                if (signature.Length > 0)
                {
                    return new Hit
                    {
                        Markdown = "```surtr\n" + signature + "\n```",
                        AnchorStart = anchor.Span.Start.Position,
                        AnchorLength = anchor.Span.Length,
                    };
                }
            }

            return null;
        }

        /// <summary>The innermost declaration containing a position, top-down through type members.</summary>
        private static DeclarationSyntax? FindContainingDeclaration(CompilationUnitSyntax unit, int position)
        {
            foreach (var declaration in unit.Declarations)
            {
                var found = FindContainingDeclaration(declaration, position);
                if (found is not null)
                    return found;
            }

            return null;
        }

        private static DeclarationSyntax? FindContainingDeclaration(DeclarationSyntax declaration, int position)
        {
            if (!declaration.Span.Contains(position))
                return null;

            switch (declaration)
            {
                case TypeDeclarationSyntax type:
                    foreach (var member in type.Members)
                    {
                        var found = FindContainingDeclaration(member, position);
                        if (found is not null)
                            return found;
                    }

                    break;

                case ConstIfDeclarationSyntax constIf:
                    foreach (var branch in constIf.Then)
                    {
                        var found = FindContainingDeclaration(branch, position);
                        if (found is not null)
                            return found;
                    }

                    foreach (var branch in constIf.Else)
                    {
                        var found = FindContainingDeclaration(branch, position);
                        if (found is not null)
                            return found;
                    }

                    break;
            }

            return declaration;
        }

        /// <summary>
        /// The enum case whose span holds a position, with the enum declaring it. An enum case is a
        /// <see cref="EnumCaseSyntax"/> rather than a <see cref="DeclarationSyntax"/>, so the ordinary
        /// declaration walk (<see cref="FindContainingDeclaration"/>) can never land on one.
        /// </summary>
        private static (EnumCaseSyntax? Case, TypeDeclarationSyntax? Containing) FindContainingEnumCase(CompilationUnitSyntax unit, int position)
        {
            foreach (var declaration in unit.Declarations)
            {
                var found = FindContainingEnumCase(declaration, position);
                if (found.Case is not null)
                    return found;
            }

            return (null, null);
        }

        private static (EnumCaseSyntax? Case, TypeDeclarationSyntax? Containing) FindContainingEnumCase(DeclarationSyntax declaration, int position)
        {
            if (!declaration.Span.Contains(position))
                return (null, null);

            switch (declaration)
            {
                case TypeDeclarationSyntax type:
                    foreach (var @case in type.EnumCases)
                    {
                        if (@case.Span.Contains(position))
                            return (@case, type);
                    }

                    foreach (var member in type.Members)
                    {
                        var found = FindContainingEnumCase(member, position);
                        if (found.Case is not null)
                            return found;
                    }

                    break;

                case ConstIfDeclarationSyntax constIf:
                    foreach (var branch in constIf.Then)
                    {
                        var found = FindContainingEnumCase(branch, position);
                        if (found.Case is not null)
                            return found;
                    }

                    foreach (var branch in constIf.Else)
                    {
                        var found = FindContainingEnumCase(branch, position);
                        if (found.Case is not null)
                            return found;
                    }

                    break;
            }

            return (null, null);
        }

        /// <summary>A hover card for the declaration site of an enum case (§2.4).</summary>
        /// <remarks>
        /// Built from the written syntax — the case's name, its explicit <c>= n</c> value when one
        /// was written, and the enum declaring it. Like the other declaration-site hovers it carries
        /// no definition of its own: a position on the declaration already is the definition.
        /// </remarks>
        private static Hit? EnumCaseHit((EnumCaseSyntax Case, TypeDeclarationSyntax Containing) found, Token anchor)
        {
            var @case = found.Case;
            var builder = new System.Text.StringBuilder();
            builder.Append("```surtr\n");
            builder.Append(@case.Name).Append(" : ").Append(found.Containing.Name);
            builder.Append("\n```");

            if (@case.ExplicitValue is long value)
                builder.Append("  \n").Append("enum case = ").Append(value);

            return new Hit
            {
                Markdown = builder.ToString(),
                AnchorStart = anchor.Span.Start.Position,
                AnchorLength = anchor.Span.Length,
            };
        }

        private static IEnumerable<ParameterSyntax> ParametersOf(DeclarationSyntax declaration)
            => declaration switch
            {
                MethodDeclarationSyntax method => method.Parameters,
                ConstructorDeclarationSyntax constructor => constructor.Parameters,
                OperatorDeclarationSyntax @operator => @operator.Parameters,
                _ => Array.Empty<ParameterSyntax>(),
            };

        /// <summary>The innermost type written anywhere in a declaration, or <see langword="null"/>.</summary>
        private static TypeSyntax? InnermostType(DeclarationSyntax declaration, int position)
        {
            TypeSyntax? best = null;

            void Consider(TypeSyntax? candidate)
            {
                if (candidate is null)
                    return;
                if (!candidate.Span.Contains(position))
                    return;
                if (best is null || candidate.Span.Length < best.Span.Length)
                    best = candidate;
            }

            switch (declaration)
            {
                case MethodDeclarationSyntax method:
                    Consider(method.ReturnType);
                    foreach (var parameter in method.Parameters)
                        Consider(parameter.Type);
                    foreach (var parameter in method.TypeParameters)
                    {
                        foreach (var constraint in parameter.Constraints)
                            Consider(constraint);
                    }

                    break;

                case ConstructorDeclarationSyntax constructor:
                    foreach (var parameter in constructor.Parameters)
                        Consider(parameter.Type);
                    break;

                case OperatorDeclarationSyntax @operator:
                    Consider(@operator.ReturnType);
                    foreach (var parameter in @operator.Parameters)
                        Consider(parameter.Type);
                    break;

                case FieldDeclarationSyntax field:
                    Consider(field.Type);
                    break;

                case PropertyDeclarationSyntax property:
                    Consider(property.Type);
                    break;

                case AliasDeclarationSyntax alias:
                    Consider(alias.Target);
                    foreach (var parameter in alias.TypeParameters)
                    {
                        foreach (var constraint in parameter.Constraints)
                            Consider(constraint);
                    }

                    break;

                case TypeDeclarationSyntax type:
                    foreach (var baseType in type.BaseTypes)
                        Consider(baseType);
                    foreach (var parameter in type.TypeParameters)
                    {
                        foreach (var constraint in parameter.Constraints)
                            Consider(constraint);
                    }

                    break;
            }

            return best;
        }

        /// <summary>Whether a position sits in a declaration's signature rather than its body.</summary>
        private static bool IsInHeader(DeclarationSyntax declaration, int position, List<Token> tokens)
        {
            if (!declaration.Span.Contains(position))
                return false;

            foreach (var token in tokens)
            {
                if (token.Span.Start.Position < declaration.Span.Start.Position)
                    continue;
                if (token.Span.Start.Position >= declaration.Span.End)
                    break;

                if (token.Type == TokenType.LeftBrace)
                    return position < token.Span.Start.Position;
            }

            return true;
        }

        /// <summary>A declaration's signature as one line, or an empty string when it has none readable.</summary>
        private static string SignatureText(DeclarationSyntax declaration, string text, List<Token> tokens)
        {
            int end = declaration.Span.End;
            foreach (var token in tokens)
            {
                if (token.Span.Start.Position < declaration.Span.Start.Position)
                    continue;
                if (token.Span.Start.Position >= declaration.Span.End)
                    break;

                if (token.Type == TokenType.LeftBrace || token.Type == TokenType.Semicolon)
                {
                    end = token.Span.Start.Position;
                    break;
                }
            }

            if (end <= declaration.Span.Start.Position)
                return string.Empty;

            string signature = text.Substring(declaration.Span.Start.Position, end - declaration.Span.Start.Position);
            signature = System.Text.RegularExpressions.Regex.Replace(signature, @"\s+", " ").Trim();
            return signature;
        }

        private static string ShortName(DeclarationSyntax declaration)
            => declaration switch
            {
                MethodDeclarationSyntax method => "method `" + method.Name + "`",
                ConstructorDeclarationSyntax => "constructor",
                OperatorDeclarationSyntax => "operator",
                FieldDeclarationSyntax field => "field `" + field.Name + "`",
                PropertyDeclarationSyntax property => "property `" + property.Name + "`",
                AliasDeclarationSyntax alias => "alias `" + alias.Name + "`",
                TypeDeclarationSyntax type => "type `" + type.Name + "`",
                _ => "declaration",
            };

        private static string TextOf(string text, SourceSpan span)
            => span.Start.Position >= 0 && span.End <= text.Length
                ? text.Substring(span.Start.Position, span.Length)
                : string.Empty;

        // ------------------------------------------------------------------------------------
        // Pass three: a bare name that names a type or an alias.
        // ------------------------------------------------------------------------------------

        private static Hit? TypeNameHit(CompilationSnapshot snapshot, string filePath, string text, List<Token> tokens, int position, Token anchor)
        {
            if (snapshot.IsEmpty || snapshot.Binder is null)
                return null;

            string name = anchor.Lexeme.ToString();
            var unit = snapshot.UnitFor(filePath);
            if (unit is null)
                return null;

            if (!snapshot.Binder.Modules.TryGetValue(unit.ModulePath, out var module))
                return null;

            foreach (var type in module.FindTypes(name))
            {
                var (file, start, length) = FindTypeDeclaration(type, snapshot);
                return new Hit
                {
                    Markdown = HoverFormatter.FormatType(type),
                    AnchorStart = anchor.Span.Start.Position,
                    AnchorLength = anchor.Span.Length,
                    DefinitionFile = file,
                    DefinitionStart = start,
                    DefinitionLength = length,
                };
            }

            foreach (var alias in module.Aliases)
            {
                if (alias.Name == name)
                {
                    var (file, start, length) = FindAliasDeclaration(module, alias, snapshot);
                    return new Hit
                    {
                        Markdown = HoverFormatter.FormatAlias(alias),
                        AnchorStart = anchor.Span.Start.Position,
                        AnchorLength = anchor.Span.Length,
                        DefinitionFile = file,
                        DefinitionStart = start,
                        DefinitionLength = length,
                    };
                }
            }

            // A fully-qualified type name `module.Type`: resolve the prefix to a module.
            int lastDot = name.LastIndexOf('.');
            if (lastDot > 0 && lastDot < name.Length - 1)
            {
                string modulePath = name.Substring(0, lastDot);
                string typeName = name.Substring(lastDot + 1);
                if (snapshot.Binder.Modules.TryGetValue(modulePath, out var foreign))
                {
                    foreach (var type in foreign.FindTypes(typeName))
                    {
                        var (file, start, length) = FindTypeDeclaration(type, snapshot);
                        return new Hit
                        {
                            Markdown = HoverFormatter.FormatType(type),
                            AnchorStart = anchor.Span.Start.Position,
                            AnchorLength = anchor.Span.Length,
                            DefinitionFile = file,
                            DefinitionStart = start,
                            DefinitionLength = length,
                        };
                    }
                }
            }

            // A module-level member the bound tree never names: a `const` read is folded straight
            // to a literal by the binder (§7.1), so no BoundFieldExpression exists for it and the
            // bound walk above has nothing to claim. Resolve the name against the module's own
            // fields — a const, or a let/var whose read some other pass missed — so hover and
            // go-to-definition still answer for it.
            foreach (var field in module.Fields)
            {
                if (field.Name != name)
                    continue;

                var (fieldFile, fieldStart, fieldLength) = FindSymbolDeclaration(field, snapshot);
                return new Hit
                {
                    Markdown = HoverFormatter.FormatSymbol(field),
                    AnchorStart = anchor.Span.Start.Position,
                    AnchorLength = anchor.Span.Length,
                    DefinitionFile = fieldFile,
                    DefinitionStart = fieldStart,
                    DefinitionLength = fieldLength,
                };
            }

            // A class-level `const` read (`Box.SIZE`) folds the same way, and the receiver names
            // the type that holds the constant — resolve `Type.CONST` when the tokens right before
            // the anchor spell exactly that shape.
            if (QualifiedConstOf(snapshot, module, tokens, anchor, name) is Hit qualified)
                return qualified;

            return null;
        }

        /// <summary>
        /// Resolves <c>Type.CONST</c> — the written receiver immediately before the hovered name —
        /// to a constant field of that type, when the bound tree already failed (a const read
        /// folds to a literal and leaves no field node for the walk to claim). Returns
        /// <see langword="null"/> for anything that is not a bare <c>Type.CONST</c>.
        /// </summary>
        private static Hit? QualifiedConstOf(CompilationSnapshot snapshot, ModuleSymbol module, List<Token> tokens, Token anchor, string name)
        {
            int index = tokens.FindIndex(t => t.Span.Equals(anchor.Span));
            if (index < 2
                || tokens[index - 1].Type != TokenType.Dot
                || tokens[index - 2].Type != TokenType.Identifier)
            {
                return null;
            }

            string receiver = tokens[index - 2].Lexeme.ToString();
            foreach (var type in module.FindTypes(receiver))
            {
                foreach (var member in type.Definition.Members)
                {
                    if (member is not FieldSymbol field || !field.IsConst || field.Name != name)
                        continue;

                    var (file, start, length) = FindSymbolDeclaration(field, snapshot);
                    return new Hit
                    {
                        Markdown = HoverFormatter.FormatSymbol(field),
                        AnchorStart = anchor.Span.Start.Position,
                        AnchorLength = anchor.Span.Length,
                        DefinitionFile = file,
                        DefinitionStart = start,
                        DefinitionLength = length,
                    };
                }
            }

            return null;
        }

        /// <summary>A type card from a written type reference, used when no bound node was involved.</summary>
        private static Hit? TypeCard(CompilationSnapshot snapshot, string filePath, string text, TypeSyntax typeSyntax, Token anchor)
        {
            if (typeSyntax is not NamedTypeSyntax named)
            {
                return new Hit
                {
                    Markdown = "```surtr\n" + TextOf(text, typeSyntax.Span) + "\n```\n\n" + HoverFormatter.DescribeTypeShape(typeSyntax),
                    AnchorStart = anchor.Span.Start.Position,
                    AnchorLength = anchor.Span.Length,
                };
            }

            string name = named.Path[named.Path.Count - 1];

            // Built-ins get a label without needing a declaration. BuiltInLabel already fences the
            // type's own name, so re-fencing the written source text here would show the type twice.
            string? builtIn = HoverFormatter.BuiltInLabel(name);
            if (builtIn is not null)
            {
                return new Hit
                {
                    Markdown = builtIn,
                    AnchorStart = anchor.Span.Start.Position,
                    AnchorLength = anchor.Span.Length,
                };
            }

            // A named type the module declares — or reaches through an import (§2.1: wildcard,
            // selective, a directory wildcard's submodule, or a plain named one) — gets its real
            // card. A type-annotation position (field, property, parameter, return, base type,
            // constraint) has no bound-expression node for pass one to find, so this is the only
            // pass that ever sees a type named solely through an import.
            var unit = snapshot.UnitFor(filePath);
            if (unit is not null && snapshot.Binder is not null)
            {
                // `Alias.Type` (§2.1, Fase 7): resolved against the alias's target module before
                // anything else, since the last-segment name alone says nothing about the alias.
                if (named.Path.Count >= 2 && TryResolveThroughAlias(snapshot, unit, named.Path, out var aliased))
                {
                    var (aliasedFile, aliasedStart, aliasedLength) = FindTypeDeclaration(aliased, snapshot);
                    return new Hit
                    {
                        Markdown = HoverFormatter.FormatType(aliased),
                        AnchorStart = anchor.Span.Start.Position,
                        AnchorLength = anchor.Span.Length,
                        DefinitionFile = aliasedFile,
                        DefinitionStart = aliasedStart,
                        DefinitionLength = aliasedLength,
                    };
                }

                if (snapshot.Binder.Modules.TryGetValue(unit.ModulePath, out var module))
                {
                    var candidates = module.FindTypes(name);
                    if (candidates.Count == 0)
                        candidates = FindTypesInImports(snapshot, unit, module, name);

                    foreach (var type in candidates)
                    {
                        var (file, start, length) = FindTypeDeclaration(type, snapshot);
                        return new Hit
                        {
                            Markdown = HoverFormatter.FormatType(type),
                            AnchorStart = anchor.Span.Start.Position,
                            AnchorLength = anchor.Span.Length,
                            DefinitionFile = file,
                            DefinitionStart = start,
                            DefinitionLength = length,
                        };
                    }
                }

                // §13: the standard library sits in the outermost scope regardless of what this
                // file imports, so a built-in class or interface (`Exception`, `IIterable`, ...)
                // used unqualified is looked up there rather than falling through unresolved. It
                // has no `.surtr` source, so `FindTypeDeclaration` correctly finds no file for it.
                if (snapshot.Binder.GlobalScope.Lookup(name).Symbol is NamedTypeSymbol builtInType)
                {
                    var (file, start, length) = FindTypeDeclaration(builtInType, snapshot);
                    return new Hit
                    {
                        Markdown = HoverFormatter.FormatType(builtInType),
                        AnchorStart = anchor.Span.Start.Position,
                        AnchorLength = anchor.Span.Length,
                        DefinitionFile = file,
                        DefinitionStart = start,
                        DefinitionLength = length,
                    };
                }
            }

            return new Hit
            {
                Markdown = "```surtr\n" + TextOf(text, typeSyntax.Span) + "\n```",
                AnchorStart = anchor.Span.Start.Position,
                AnchorLength = anchor.Span.Length,
            };
        }

        /// <summary>
        /// A named type reached through one of this file's imports, if any names it — a wildcard
        /// (exact module or a submodule nested under it, §2.1's Fase 9), a selective list
        /// (Fase 8), or a plain named import. A module alias (Fase 7) is handled separately by
        /// <see cref="TryResolveThroughAlias"/>, since it needs the receiver segment this method
        /// never sees.
        /// </summary>
        private static IReadOnlyList<NamedTypeSymbol> FindTypesInImports(
            CompilationSnapshot snapshot, Surtr.Compiler.Compilation.SurtrSourceUnit unit, ModuleSymbol current, string name)
        {
            var binder = snapshot.Binder!;

            foreach (var import in unit.Syntax.Imports)
            {
                if (import.Alias is not null)
                    continue;

                if (import.IsModule)
                {
                    // `import module X.Y;` (§2.1) brings a whole module's surface, the way a
                    // wildcard over exactly that one module would.
                    string modulePath = string.Join(".", import.Path);
                    if (modulePath != current.Path && binder.Modules.TryGetValue(modulePath, out var whole))
                    {
                        var wholeTypes = whole.FindTypes(name);
                        if (wholeTypes.Count > 0)
                            return wholeTypes;
                    }

                    continue;
                }

                if (import.IsWildcard)
                {
                    string importedPath = string.Join(".", import.Path);

                    if (importedPath != current.Path && binder.Modules.TryGetValue(importedPath, out var imported))
                    {
                        var direct = imported.FindTypes(name);
                        if (direct.Count > 0)
                            return direct;
                    }

                    string dotted = importedPath + ".";
                    foreach (var pair in binder.Modules)
                    {
                        if (pair.Key == current.Path || !pair.Key.StartsWith(dotted, StringComparison.Ordinal))
                            continue;

                        var nested = pair.Value.FindTypes(name);
                        if (nested.Count > 0)
                            return nested;
                    }

                    continue;
                }

                if (import.Members is not null)
                {
                    if (!import.Members.Contains(name))
                        continue;

                    string listedPath = string.Join(".", import.Path);
                    if (binder.Modules.TryGetValue(listedPath, out var listed))
                    {
                        var candidates = listed.FindTypes(name);
                        if (candidates.Count > 0)
                            return candidates;
                    }

                    continue;
                }

                if (import.Path.Count > 0 && import.Path[import.Path.Count - 1] == name)
                {
                    string namedModulePath = string.Join(".", import.Path.Take(import.Path.Count - 1));
                    if (binder.Modules.TryGetValue(namedModulePath, out var namedModule))
                    {
                        var candidates = namedModule.FindTypes(name);
                        if (candidates.Count > 0)
                            return candidates;
                    }
                }

                // `import ModulePath;` with nothing after it resolves as a whole module when the
                // full path names one (§2.1) — the same wildcard surface as `import ModulePath.*;`,
                // re-exports included. The binder treats this as a module, so the resolver must too.
                string wholePath = string.Join(".", import.Path);
                if (wholePath != current.Path && binder.Modules.TryGetValue(wholePath, out var wholeModule))
                {
                    var wholeTypes = wholeModule.FindTypes(name);
                    if (wholeTypes.Count > 0)
                        return wholeTypes;
                }
            }

            return Array.Empty<NamedTypeSymbol>();
        }

        /// <summary>
        /// <c>Alias.Type</c> (§2.1, Fase 7), resolved against the module <c>import X as Alias;</c>
        /// named. Only one segment past the alias is chased — a nested type reached through an
        /// alias (<c>Alias.Outer.Inner</c>) is a rarer shape this pass does not follow.
        /// </summary>
        private static bool TryResolveThroughAlias(
            CompilationSnapshot snapshot, Surtr.Compiler.Compilation.SurtrSourceUnit unit, IReadOnlyList<string> path, out NamedTypeSymbol type)
        {
            foreach (var import in unit.Syntax.Imports)
            {
                if (import.Alias != path[0])
                    continue;

                string aliasedPath = string.Join(".", import.Path);
                if (!snapshot.Binder!.Modules.TryGetValue(aliasedPath, out var aliased))
                    continue;

                var candidates = aliased.FindTypes(path[1]);
                if (candidates.Count > 0)
                {
                    type = candidates[0];
                    return true;
                }
            }

            type = null!;
            return false;
        }

        // ------------------------------------------------------------------------------------
        // From a symbol to a card and a declaration.
        // ------------------------------------------------------------------------------------

        private static Hit FromSymbol(Symbol symbol, SourceSpan anchorSpan, CompilationSnapshot snapshot)
        {
            var (file, start, length) = FindSymbolDeclaration(symbol, snapshot);
            return new Hit
            {
                Markdown = HoverFormatter.FormatSymbol(symbol),
                AnchorStart = anchorSpan.Start.Position,
                AnchorLength = anchorSpan.Length,
                DefinitionFile = file,
                DefinitionStart = start,
                DefinitionLength = length,
            };
        }

        private static Hit FromType(TypeSymbol type, SourceSpan anchorSpan)
            => new Hit
            {
                Markdown = HoverFormatter.FormatType(type),
                AnchorStart = anchorSpan.Start.Position,
                AnchorLength = anchorSpan.Length,
            };

        /// <summary>
        /// Finds the source declaration of a symbol declared in this workspace, as (file, start,
        /// length). Returns a null file for an imported or host symbol, which has no source here.
        /// </summary>
        private static (string? File, int Start, int Length) FindSymbolDeclaration(Symbol symbol, CompilationSnapshot snapshot)
        {
            switch (symbol)
            {
                case MethodSymbol method:
                    if (method.OriginalDefinition is not null)
                        method = method.OriginalDefinition;
                    if (method.ImportedFrom is not null)
                        return (null, 0, 0);
                    return FindMethodDeclaration(method, snapshot);

                case FieldSymbol field:
                    if (field.OriginalDefinition is not null)
                        field = field.OriginalDefinition;
                    if (field.ImportedFrom is not null)
                        return (null, 0, 0);
                    if (field.ContainingSymbol is NamedTypeSymbol { TypeKind: TypeSymbolKind.Enum } containing)
                        return FindEnumCaseDeclaration(field.Name, containing, snapshot);
                    return FindNamedMemberDeclaration(field.ContainingSymbol, field.Name, (d, n) => d is FieldDeclarationSyntax f && f.Name == n, snapshot);

                case PropertySymbol property:
                    return FindNamedMemberDeclaration(
                        property.ContainingSymbol, property.Name, (d, n) => d is PropertyDeclarationSyntax p && p.Name == n, snapshot,
                        isExtensionMember: property.ExtensionTargetType is not null);

                case NamedTypeSymbol type:
                    return FindTypeDeclaration(type, snapshot);

                case AliasSymbol alias:
                    return FindAliasDeclaration(alias.ContainingSymbol, alias, snapshot);

                default:
                    return (null, 0, 0);
            }
        }

        private static (string? File, int Start, int Length) FindMethodDeclaration(MethodSymbol method, CompilationSnapshot snapshot)
        {
            // An extension method (§15) has no `ContainingType` — it is emitted as an ordinary
            // module-level function, `ContainingSymbol` names the declaring module rather than the
            // type it extends — so `MatchesParent` (built around a real member's `TypeDeclarationSyntax`
            // parent) cannot be asked here the way it is below: it would read `ContainingType is null`
            // as "declared at module level" and accept the first same-named, same-arity module
            // function it finds, real or not. Matching "declared inside *an* extension block" instead
            // is what `AllDeclarations` now yields a parent for.
            bool isExtensionMethod = method.ExtensionTargetType is not null;

            foreach (var unit in UnitsOf(method.ContainingSymbol, snapshot))
            {
                foreach (var (declaration, parent) in AllDeclarations(unit.Syntax))
                {
                    if (declaration is not MethodDeclarationSyntax methodSyntax
                        || methodSyntax.Name != method.Name
                        || methodSyntax.Parameters.Count != method.Parameters.Count)
                    {
                        continue;
                    }

                    bool parentMatches = isExtensionMethod
                        ? parent is ExtensionDeclarationSyntax
                        : MatchesParent(parent, method.ContainingType);

                    if (!parentMatches)
                        continue;

                    SourceSpan nameSpan = NameSpanOf(unit.File.Text, methodSyntax, methodSyntax.Name);
                    return (unit.File.Path, nameSpan.Start.Position, nameSpan.Length);
                }
            }

            return (null, 0, 0);
        }

        private static (string? File, int Start, int Length) FindNamedMemberDeclaration(
            Symbol? containing,
            string name,
            Func<DeclarationSyntax, string, bool> matches,
            CompilationSnapshot snapshot,
            bool isExtensionMember = false)
        {
            foreach (var unit in UnitsOf(containing, snapshot))
            {
                foreach (var (declaration, parent) in AllDeclarations(unit.Syntax))
                {
                    if (!matches(declaration, name))
                        continue;

                    bool parentMatches = isExtensionMember
                        ? parent is ExtensionDeclarationSyntax
                        : MatchesParent(parent, containing as NamedTypeSymbol);

                    if (!parentMatches)
                        continue;

                    SourceSpan nameSpan = NameSpanOf(unit.File.Text, declaration, name);
                    return (unit.File.Path, nameSpan.Start.Position, nameSpan.Length);
                }
            }

            return (null, 0, 0);
        }

        private static (string? File, int Start, int Length) FindTypeDeclaration(NamedTypeSymbol type, CompilationSnapshot snapshot)
        {
            foreach (var unit in UnitsOf(type.ContainingSymbol, snapshot))
            {
                foreach (var (declaration, parent) in AllDeclarations(unit.Syntax))
                {
                    if (declaration is TypeDeclarationSyntax typeSyntax
                        && typeSyntax.Name == type.Name
                        && typeSyntax.TypeParameters.Count == type.TypeParameters.Count
                        && MatchesParent(parent, type.ContainingType))
                    {
                        SourceSpan nameSpan = NameSpanOf(unit.File.Text, typeSyntax, typeSyntax.Name);
                        return (unit.File.Path, nameSpan.Start.Position, nameSpan.Length);
                    }
                }
            }

            return (null, 0, 0);
        }

        /// <summary>
        /// The source declaration of an enum case (§2.4), which is an <see cref="EnumCaseSyntax"/>
        /// rather than a <see cref="FieldDeclarationSyntax"/>. The case field's own card is reached
        /// through the enum's declaration, so the written name's span is found on the matching case.
        /// </summary>
        private static (string? File, int Start, int Length) FindEnumCaseDeclaration(string name, NamedTypeSymbol @enum, CompilationSnapshot snapshot)
        {
            foreach (var unit in UnitsOf(@enum, snapshot))
            {
                foreach (var (declaration, parent) in AllDeclarations(unit.Syntax))
                {
                    if (declaration is not TypeDeclarationSyntax typeSyntax
                        || typeSyntax.Name != @enum.Name
                        || typeSyntax.TypeParameters.Count != @enum.TypeParameters.Count
                        || !MatchesParent(parent, @enum.ContainingType))
                    {
                        continue;
                    }

                    foreach (var @case in typeSyntax.EnumCases)
                    {
                        if (@case.Name != name)
                            continue;

                        SourceSpan nameSpan = NameSpanOf(unit.File.Text, @case, @case.Name);
                        return (unit.File.Path, nameSpan.Start.Position, nameSpan.Length);
                    }
                }
            }

            return (null, 0, 0);
        }

        private static (string? File, int Start, int Length) FindAliasDeclaration(Symbol? containing, AliasSymbol alias, CompilationSnapshot snapshot)
        {
            foreach (var unit in UnitsOf(containing, snapshot))
            {
                foreach (var (declaration, parent) in AllDeclarations(unit.Syntax))
                {
                    if (declaration is AliasDeclarationSyntax aliasSyntax && aliasSyntax.Name == alias.Name)
                    {
                        SourceSpan nameSpan = NameSpanOf(unit.File.Text, aliasSyntax, aliasSyntax.Name);
                        return (unit.File.Path, nameSpan.Start.Position, nameSpan.Length);
                    }
                }
            }

            return (null, 0, 0);
        }

        private static IEnumerable<Surtr.Compiler.Compilation.SurtrSourceUnit> UnitsOf(Symbol? containing, CompilationSnapshot snapshot)
        {
            if (containing is null || snapshot.Compilation is null)
                yield break;

            Symbol? current = containing;
            while (current is not null && current is not ModuleSymbol)
                current = current.ContainingSymbol;

            if (current is not ModuleSymbol module)
                yield break;

            if (snapshot.Compilation.Modules.TryGetValue(module.Path, out var sourceModule))
            {
                foreach (var unit in sourceModule.Units)
                    yield return unit;
            }
        }

        private static bool MatchesParent(DeclarationSyntax? parent, NamedTypeSymbol? containingType)
        {
            if (containingType is null)
                return parent is null;

            if (parent is not TypeDeclarationSyntax typeSyntax)
                return false;

            return typeSyntax.Name == containingType.Name
                && typeSyntax.TypeParameters.Count == containingType.TypeParameters.Count;
        }

        /// <summary>
        /// Every declaration in a unit, with its immediate declaring node — a
        /// <see cref="TypeDeclarationSyntax"/> for an ordinary member, an
        /// <see cref="ExtensionDeclarationSyntax"/> for one declared inside an <c>extension</c> block
        /// (§15), or <see langword="null"/> at module level.
        /// </summary>
        private static IEnumerable<(DeclarationSyntax Declaration, DeclarationSyntax? Parent)> AllDeclarations(CompilationUnitSyntax unit)
        {
            foreach (var declaration in unit.Declarations)
            {
                yield return (declaration, null);

                if (declaration is TypeDeclarationSyntax type)
                {
                    foreach (var member in type.Members)
                    {
                        yield return (member, type);

                        // An `extension` block may itself be nested inside a class (§15.2), narrowing
                        // its visibility without gaining a second receiver.
                        if (member is ExtensionDeclarationSyntax nestedExtension)
                        {
                            foreach (var extensionMember in nestedExtension.Members)
                                yield return (extensionMember, nestedExtension);
                        }
                    }
                }
                else if (declaration is ExtensionDeclarationSyntax extension)
                {
                    foreach (var member in extension.Members)
                        yield return (member, extension);
                }
            }
        }

        /// <summary>The declaration's name token span, from its file's text.</summary>
        private static SourceSpan NameSpanOf(string fileText, SyntaxNode declaration, string name)
        {
            var tokens = new Lexer(SurtrSourceBuffer.FromString(fileText, declaration.Span.ToString())).Tokenize();
            Token? first = FirstNameToken(declaration.Span, tokens);
            if (first.HasValue && first.Value.Lexeme.ToString() == name)
                return first.Value.Span;

            return declaration.Span;
        }
    }
}
