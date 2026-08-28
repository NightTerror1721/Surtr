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
    /// The <c>textDocument/semanticTokens/full</c> handler — the one place this server can resolve
    /// a contextual keyword (§1.2: <c>this</c>, <c>super</c>, <c>value</c>, <c>attribute</c>, plus
    /// the by-position-only <c>get</c>/<c>set</c>, §3.4) by the same rule the real parser and binder
    /// use, rather than the position-blind regex lookahead <c>vscode-surtr/syntaxes/surtr.tmLanguage.json</c>
    /// has to settle for. A client merges semantic tokens over its TextMate grammar, so this only
    /// needs to cover what a regex genuinely cannot: it does not re-tag comments, strings, literals
    /// or ordinary identifiers, all of which the grammar already gets right on its own.
    /// </summary>
    /// <remarks>
    /// The pass resolves what a regex grammar genuinely cannot: which spans are really a contextual
    /// keyword (§1.2/§3.4, including the variance <c>out</c>), which identifiers name a type in any
    /// position, which type parameters are declarations, and which names are an enum's cases (§2.4).
    /// Everything else — comments, strings, literals, operators and ordinary identifiers — stays the
    /// grammar's. Contextual keywords ride the same <c>keyword</c> slot as the reserved words, so a
    /// theme colours them alike; the modifier/variable slots are not used for them.
    /// </remarks>
    public static class SemanticTokensProvider
    {
        /// <summary>
        /// The legend this server declares. Broad enough that the one pass can answer the four
        /// things a regex grammar genuinely cannot: which spans are really a contextual keyword
        /// (§1.2) rather than a position-blind lookahead guess, which identifiers name a type in
        /// *any* position (a regex only sees type positions it can spell out), which type
        /// parameters are declarations, and which names an enum declares as its cases (§2.4). The
        /// client merges these over the TextMate grammar, so the grammar still owns comments,
        /// strings, literals, operators and ordinary identifiers.
        /// </summary>
        public static readonly string[] TokenTypes =
        {
            "keyword",
            "type",
            "typeParameter",
            "function",
            "modifier",
            "enumMember",
        };

        public static readonly string[] TokenModifiers = Array.Empty<string>();

        private const int KeywordTokenType = 0;
        private const int TypeTokenType = 1;
        private const int TypeParameterTokenType = 2;
        private const int FunctionTokenType = 3;
        private const int ModifierTokenType = 4;
        private const int EnumMemberTokenType = 5;

        /// <summary>A semantic span plus the token type it is tagged with.</summary>
        private readonly struct TaggedSpan
        {
            public TaggedSpan(SourceSpan span, int tokenType)
            {
                Span = span;
                TokenType = tokenType;
            }

            public SourceSpan Span { get; }

            public int TokenType { get; }
        }

        public static SemanticTokens Compute(CompilationSnapshot snapshot, string filePath, string text)
        {
            var spans = new List<TaggedSpan>();

            // One lex for the whole request — previously the bound-tree walk and the declaration
            // passes each tokenized the document again.
            var tokens = new Lexer(SurtrSourceBuffer.FromString(text, filePath)).Tokenize();

            if (!snapshot.IsEmpty && snapshot.Binder is not null)
                CollectThisSuperAndExpressionTypes(snapshot.Binder, filePath, text, tokens, spans);

            var unit = snapshot.UnitFor(filePath);
            if (unit is not null)
            {
                CollectDeclarationKeywords(unit.Syntax.Declarations, tokens, spans);
                CollectTypePositions(unit.Syntax.Declarations, tokens, spans);
            }

            return Encode(spans, text);
        }

        // ------------------------------------------------------------------------------------
        // this / super — resolved from the bound tree, so a variable that happens to be named
        // "this" or "super" outside a member body (legal per §1.2 - both are contextual, not
        // reserved) never produces a BoundThisExpression and never appears here. An *implicit*
        // receiver (a bare field read `_head`) is also a BoundThisExpression, but its span covers
        // the member's own identifier rather than the word "this" - the text check below is what
        // keeps such a read out of the keyword/this pass.
        // ------------------------------------------------------------------------------------

        private static void CollectThisSuperAndExpressionTypes(Binder binder, string filePath, string text, List<Token> tokens, List<TaggedSpan> spans)
        {

            foreach (var pair in binder.Bodies)
            {
                if (!binder.BodyFiles.TryGetValue(pair.Key, out string? bodyFile) || !SameFile(bodyFile, filePath))
                    continue;
                if (pair.Value.Span.End > text.Length)
                    continue;

                WalkStatement(pair.Value, text, tokens, spans);
            }

            foreach (var initializer in binder.FieldInitializers)
            {
                if (initializer.Value.Span.End <= text.Length)
                    WalkExpression(initializer.Value, text, tokens, spans);
            }

            foreach (var block in binder.StaticBlocks)
                WalkStatement(block.Body, text, tokens, spans);

            foreach (var chain in binder.ConstructorChains.Values)
            {
                foreach (var argument in chain.Arguments)
                    WalkExpression(argument, text, tokens, spans);
            }
        }

        private static void WalkStatement(BoundStatement statement, string text, List<Token> tokens, List<TaggedSpan> spans)
        {
            switch (statement)
            {
                case BoundBlockStatement block:
                    foreach (var child in block.Statements)
                        WalkStatement(child, text, tokens, spans);
                    break;

                case BoundLocalDeclarationStatement declaration:
                    if (declaration.Syntax is Surtr.Compiler.Syntax.Ast.LocalDeclarationStatementSyntax local
                        && local.Type is not null)
                    {
                        TagType(local.Type, tokens, spans);
                    }

                    if (declaration.Initializer is not null)
                        WalkExpression(declaration.Initializer, text, tokens, spans);
                    break;

                case BoundExpressionStatement expressionStatement:
                    WalkExpression(expressionStatement.Expression, text, tokens, spans);
                    break;

                case BoundIfStatement ifStatement:
                    WalkExpression(ifStatement.Condition, text, tokens, spans);
                    WalkStatement(ifStatement.Then, text, tokens, spans);
                    if (ifStatement.Else is not null)
                        WalkStatement(ifStatement.Else, text, tokens, spans);
                    break;

                case BoundWhileStatement whileStatement:
                    WalkExpression(whileStatement.Condition, text, tokens, spans);
                    WalkStatement(whileStatement.Body, text, tokens, spans);
                    break;

                case BoundForStatement forStatement:
                    if (forStatement.Initializer is not null)
                        WalkStatement(forStatement.Initializer, text, tokens, spans);
                    if (forStatement.Condition is not null)
                        WalkExpression(forStatement.Condition, text, tokens, spans);
                    if (forStatement.Step is not null)
                        WalkExpression(forStatement.Step, text, tokens, spans);
                    WalkStatement(forStatement.Body, text, tokens, spans);
                    break;

                case BoundForInStatement forIn:
                    WalkExpression(forIn.Sequence, text, tokens, spans);
                    WalkStatement(forIn.Body, text, tokens, spans);
                    break;

                case BoundSwitchStatement switchStatement:
                    WalkExpression(switchStatement.Subject, text, tokens, spans);
                    foreach (var section in switchStatement.Sections)
                    {
                        foreach (var label in section.Labels)
                            WalkExpression(label, text, tokens, spans);
                        foreach (var child in section.Statements)
                            WalkStatement(child, text, tokens, spans);
                    }
                    break;

                case BoundTryStatement tryStatement:
                    WalkStatement(tryStatement.Body, text, tokens, spans);
                    foreach (var clause in tryStatement.Catches)
                        WalkStatement(clause.Body, text, tokens, spans);
                    if (tryStatement.Finally is not null)
                        WalkStatement(tryStatement.Finally, text, tokens, spans);
                    break;

                case BoundThrowStatement throwStatement:
                    WalkExpression(throwStatement.Value, text, tokens, spans);
                    break;

                case BoundReturnStatement returnStatement:
                    if (returnStatement.Value is not null)
                        WalkExpression(returnStatement.Value, text, tokens, spans);
                    break;

                case BoundLabeledStatement labeled:
                    WalkStatement(labeled.Statement, text, tokens, spans);
                    break;
            }
        }

        private static void WalkExpression(BoundExpression expression, string text, List<Token> tokens, List<TaggedSpan> spans)
        {
            if (expression is BoundThisExpression thisExpression)
            {
                SourceSpan span = thisExpression.Span;
                if (span.End <= text.Length && IsThisOrSuper(text, span))
                    spans.Add(new TaggedSpan(span, KeywordTokenType));
            }

            switch (expression)
            {
                case BoundObjectCreationExpression creation:
                    TagTypeSymbol(creation.Type, creation.Syntax.Span, tokens, spans, FunctionTokenType);
                    break;

                case BoundTypeTestExpression typeTest:
                    TagTypeSymbol(typeTest.TestedType, typeTest.Syntax.Span, tokens, spans, TypeTokenType);
                    break;

                case BoundConversionExpression conversion:
                    if (conversion.IsExplicit)
                        TagTypeSymbol(conversion.Type, conversion.Syntax.Span, tokens, spans, TypeTokenType);
                    break;

                case BoundTypeOfExpression typeOf:
                    if (typeOf.TargetType is TypeSymbol target)
                        TagTypeSymbol(target, typeOf.Syntax.Span, tokens, spans, TypeTokenType);
                    break;

                case BoundFieldExpression field:
                    // An enum case use (§2.4) is a read of the case's static field; tag the written
                    // name the same as the declaration, so a use renders like the case it names.
                    if (field.Field.EnumValue is not null
                        && FindNameToken(field.Span, tokens, field.Field.Name) is Token fieldToken)
                    {
                        spans.Add(new TaggedSpan(fieldToken.Span, EnumMemberTokenType));
                    }

                    break;
            }

            foreach (var child in ChildrenOf(expression))
                WalkExpression(child, text, tokens, spans);
        }

        /// <summary>Whether a span's text is exactly the word <c>this</c> or <c>super</c>.</summary>
        private static bool IsThisOrSuper(string text, SourceSpan span)
        {
            string word = text.Substring(span.Start.Position, span.Length);
            return word == "this" || word == "super";
        }

        /// <summary>
        /// Tags the identifier that spells <paramref name="type"/>'s name inside
        /// <paramref name="nodeSpan"/> — the written name of a <c>typeof(X)</c>, an <c>is</c>/
        /// <c>as</c> target, or a construction's callee. The first identifier whose text equals the
        /// type's name is the one written; a qualified callee (<c>game.util.Thing.Thing(9)</c>)
        /// resolves the same way.
        /// </summary>
        private static void TagTypeSymbol(TypeSymbol type, SourceSpan nodeSpan, List<Token> tokens, List<TaggedSpan> spans, int tokenType)
        {
            if (type is not NamedTypeSymbol named || named.Name.Length == 0)
                return;

            if (FindNameToken(nodeSpan, tokens, named.Name) is Token token)
                spans.Add(new TaggedSpan(token.Span, tokenType));

            // The type arguments of a construction (`Box<Foo>(5)`) name types too; each argument's
            // identifier is tagged the same way, so a non-built-in argument never falls through to
            // the grammar as a plain variable.
            foreach (var argument in named.TypeArguments)
            {
                if (argument is NamedTypeSymbol argumentNamed
                    && argumentNamed.Name.Length > 0
                    && !IsBuiltinName(argumentNamed.Name)
                    && FindNameToken(nodeSpan, tokens, argumentNamed.Name) is Token argumentToken)
                {
                    spans.Add(new TaggedSpan(argumentToken.Span, tokenType));
                }
            }
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

                case BoundYieldExpression yieldExpression:
                    yield return yieldExpression.Value;
                    break;

                case BoundThrowExpression throwExpression:
                    yield return throwExpression.Value;
                    break;

                case BoundSequenceExpression sequence:
                    yield return sequence.Value;
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

        private static void CollectDeclarationKeywords(IReadOnlyList<DeclarationSyntax> declarations, List<Token> tokens, List<TaggedSpan> spans)
        {
            foreach (var declaration in declarations)
            {
                switch (declaration)
                {
                    case TypeDeclarationSyntax type:
                        if (type.Kind == TypeDeclarationKind.ValueClass)
                        {
                            if (FindKeywordToken(type.Span, tokens, "value") is Token valueToken)
                                spans.Add(new TaggedSpan(valueToken.Span, KeywordTokenType));
                        }

                        if (type.IsAttribute)
                        {
                            if (FindKeywordToken(type.Span, tokens, "attribute") is Token attributeToken)
                                spans.Add(new TaggedSpan(attributeToken.Span, KeywordTokenType));
                        }

                        TagAccessorKeywords(type.Members, tokens, spans);
                        CollectDeclarationKeywords(type.Members, tokens, spans);
                        break;

                    case ExtensionDeclarationSyntax extension:
                        // An extension (§15) may declare properties with `get`/`set` accessors too;
                        // they ride the same modifier slot as a type member's.
                        TagAccessorKeywords(extension.Members, tokens, spans);
                        CollectDeclarationKeywords(extension.Members, tokens, spans);
                        break;
                }
            }
        }

        private static void TagAccessorKeywords(IReadOnlyList<DeclarationSyntax> members, List<Token> tokens, List<TaggedSpan> spans)
        {
            foreach (var member in members)
            {
                if (member is not PropertyDeclarationSyntax property)
                    continue;

                foreach (var accessor in property.Accessors)
                {
                    string keyword = accessor.IsGetter ? "get" : "set";
                    if (FindKeywordToken(accessor.Span, tokens, keyword) is Token accessorToken)
                        spans.Add(new TaggedSpan(accessorToken.Span, KeywordTokenType));
                }
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

        /// <summary>Tags each enum case's written name (§2.4) as an enum member.</summary>
        /// <remarks>
        /// A regex grammar only sees the case list as ordinary identifiers, and nothing else in this
        /// pass covers it — cases are not members (they have no declaration node of their own), so
        /// the walk over <c>type.Members</c> never visits them. Tagging the name keeps it out of the
        /// plain-identifier bucket without disturbing the grammar's handling of the enum body.
        /// </remarks>
        private static void TagEnumCaseNames(TypeDeclarationSyntax type, List<Token> tokens, List<TaggedSpan> spans)
        {
            foreach (var @case in type.EnumCases)
            {
                if (FindNameToken(@case.Span, tokens, @case.Name) is Token nameToken)
                    spans.Add(new TaggedSpan(nameToken.Span, EnumMemberTokenType));
            }
        }

        // ------------------------------------------------------------------------------------
        // Types in type positions — walked from the parsed declaration syntax, because a type
        // annotation (field, property, parameter, return, base list, constraint, catch binding,
        // alias target) has no bound-expression node for the walk above to find. Every written
        // type reference is tagged as a type, every type parameter's declaration name as a type
        // parameter; built-in primitives are left to the grammar's own `storage.type.primitive`.
        // ------------------------------------------------------------------------------------

        private static void CollectTypePositions(IReadOnlyList<DeclarationSyntax> declarations, List<Token> tokens, List<TaggedSpan> spans)
        {
            foreach (var declaration in declarations)
                CollectTypePositions(declaration, tokens, spans);
        }

        private static void CollectTypePositions(DeclarationSyntax declaration, List<Token> tokens, List<TaggedSpan> spans)
        {
            switch (declaration)
            {
                case TypeDeclarationSyntax type:
                    TagTypeParameters(type.TypeParameters, tokens, spans);
                    foreach (var baseType in type.BaseTypes)
                        TagType(baseType, tokens, spans);
                    TagEnumCaseNames(type, tokens, spans);
                    foreach (var member in type.Members)
                        CollectTypePositions(member, tokens, spans);
                    break;

                case ExtensionDeclarationSyntax extension:
                    TagType(extension.TargetType, tokens, spans);
                    TagTypeParameters(extension.TypeParameters, tokens, spans);
                    foreach (var member in extension.Members)
                        CollectTypePositions(member, tokens, spans);
                    break;

                case FieldDeclarationSyntax field:
                    if (field.Type is not null)
                        TagType(field.Type, tokens, spans);
                    break;

                case PropertyDeclarationSyntax property:
                    TagType(property.Type, tokens, spans);
                    break;

                case MethodDeclarationSyntax method:
                    if (method.ReturnType is not null)
                        TagType(method.ReturnType, tokens, spans);
                    TagTypeParameters(method.TypeParameters, tokens, spans);
                    foreach (var parameter in method.Parameters)
                    {
                        if (parameter.Type is not null)
                            TagType(parameter.Type, tokens, spans);
                    }

                    break;

                case ConstructorDeclarationSyntax constructor:
                    foreach (var parameter in constructor.Parameters)
                    {
                        if (parameter.Type is not null)
                            TagType(parameter.Type, tokens, spans);
                    }

                    break;

                case OperatorDeclarationSyntax @operator:
                    TagType(@operator.ReturnType, tokens, spans);
                    foreach (var parameter in @operator.Parameters)
                    {
                        if (parameter.Type is not null)
                            TagType(parameter.Type, tokens, spans);
                    }

                    break;

                case AliasDeclarationSyntax alias:
                    TagType(alias.Target, tokens, spans);
                    TagTypeParameters(alias.TypeParameters, tokens, spans);
                    break;

                case ConstIfDeclarationSyntax constIf:
                    foreach (var branch in constIf.Then)
                        CollectTypePositions(branch, tokens, spans);
                    foreach (var branch in constIf.Else)
                        CollectTypePositions(branch, tokens, spans);
                    break;
            }
        }

        /// <summary>
        /// Tags a type parameter's own declaration name, its variance annotation when it wrote one,
        /// and each of its constraint types.
        /// </summary>
        /// <remarks>
        /// The parameter's span starts at the annotation, so when one was written the first token
        /// inside the span <em>is</em> the variance word. Only <c>out</c> needs this pass: the
        /// parser reads it as an ordinary identifier, and without the tag it would fall through to
        /// the grammar's variable rule. <c>in</c> is reserved outright (for-in owns the word), so
        /// the grammar already colours it everywhere.
        /// </remarks>
        private static void TagTypeParameters(IReadOnlyList<TypeParameterSyntax> parameters, List<Token> tokens, List<TaggedSpan> spans)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.Variance == VarianceModifier.Covariant && FindKeywordToken(parameter.Span, tokens, "out") is Token outToken)
                    spans.Add(new TaggedSpan(outToken.Span, KeywordTokenType));

                if (FindNameToken(parameter.Span, tokens, parameter.Name) is Token nameToken)
                    spans.Add(new TaggedSpan(nameToken.Span, TypeParameterTokenType));

                foreach (var constraint in parameter.Constraints)
                    TagType(constraint, tokens, spans);
            }
        }

        /// <summary>Tags every written name of a type syntax, recursing into its arguments and wrappers.</summary>
        private static void TagType(TypeSyntax syntax, List<Token> tokens, List<TaggedSpan> spans)
        {
            switch (syntax)
            {
                case NamedTypeSyntax named:
                    foreach (string segment in named.Path)
                    {
                        if (IsBuiltinName(segment))
                            continue;

                        if (FindNameToken(named.Span, tokens, segment) is Token segmentToken)
                            spans.Add(new TaggedSpan(segmentToken.Span, TypeTokenType));
                    }

                    foreach (var argument in named.TypeArguments)
                        TagType(argument, tokens, spans);
                    break;

                case ArrayTypeSyntax array:
                    TagType(array.ElementType, tokens, spans);
                    break;

                case DictTypeSyntax dict:
                    TagType(dict.KeyType, tokens, spans);
                    TagType(dict.ValueType, tokens, spans);
                    break;

                case TupleTypeSyntax tuple:
                    foreach (var element in tuple.ElementTypes)
                        TagType(element, tokens, spans);
                    break;

                case ClosureTypeSyntax closure:
                    foreach (var parameterType in closure.ParameterTypes)
                        TagType(parameterType, tokens, spans);
                    TagType(closure.ReturnType, tokens, spans);
                    break;

                case NullableTypeSyntax nullable:
                    TagType(nullable.ElementType, tokens, spans);
                    break;
            }
        }

        /// <summary>The first identifier token inside a span whose text is exactly <paramref name="name"/>.</summary>
        private static Token? FindNameToken(SourceSpan span, List<Token> tokens, string name)
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

        private static bool IsBuiltinName(string name)
            => name is "int" or "float" or "bool" or "char" or "string" or "bytes" or "void" or "range" or "unknown";

        // ------------------------------------------------------------------------------------
        // Encoding
        // ------------------------------------------------------------------------------------

        private static SemanticTokens Encode(List<TaggedSpan> spans, string text)
        {
            spans.Sort((a, b) => a.Span.Start.Position.CompareTo(b.Span.Start.Position));

            var lines = TextLines.Index(text);
            var data = new List<int>(spans.Count * 5);

            int previousLine = 0;
            int previousChar = 0;

            // Two passes tagging the same span (e.g. a field's annotation type and a construction
            // sharing a name) would otherwise emit two tokens at one position; the first wins.
            int lastStart = -1;
            int lastEnd = -1;

            foreach (var tagged in spans)
            {
                if (tagged.Span.Start.Position == lastStart && tagged.Span.End == lastEnd)
                    continue;

                var position = lines.PositionAt(tagged.Span.Start.Position);
                int deltaLine = position.Line - previousLine;
                int deltaChar = deltaLine == 0 ? position.Character - previousChar : position.Character;

                data.Add(deltaLine);
                data.Add(deltaChar);
                data.Add(tagged.Span.Length);
                data.Add(tagged.TokenType);
                data.Add(0);

                previousLine = position.Line;
                previousChar = position.Character;
                lastStart = tagged.Span.Start.Position;
                lastEnd = tagged.Span.End;
            }

            return new SemanticTokens { Data = data };
        }
    }
}
