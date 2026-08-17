#nullable enable

using Surtr.Compiler.Diagnostics;
using System.Collections.Generic;
using Surtr.Compiler.Syntax.Ast;

namespace Surtr.Compiler.Syntax
{
    public sealed partial class Parser
    {
        /// <summary>
        /// The modifiers gathered from a declaration's head, before the introducer keyword that
        /// says what kind of declaration it is (§3.2).
        /// </summary>
        private struct Modifiers
        {
            internal Visibility Visibility;
            internal bool IsStatic;
            internal bool IsSealed;
            internal bool IsAbstract;
            internal DispatchModifier Dispatch;
            internal InlineModifier Inline;
            internal bool IsConst;
            internal bool IsNative;
        }

        /// <summary>Parses one declaration, at module level or inside a type body.</summary>
        private DeclarationSyntax ParseDeclaration()
        {
            IReadOnlyList<string> docComment = ParseDocComment();

            // Captured before the attributes, so a declaration covers the ones written on it: they
            // are its first tokens, and they are child nodes, so a span that began after them would
            // leave a child outside the parent that owns it. The doc comment stays outside, since it
            // is carried as text rather than as a node.
            SourceLocation start = reader.CurrentLocation;
            IReadOnlyList<AttributeSyntax> attributes = ParseAttributes();

            // A declaration-level `const if` wraps declarations rather than carrying modifiers, so
            // it is checked before anything tries to read one (§7.3).
            if (reader.Check(TokenType.KeywordConst) && reader.CheckAt(1, TokenType.KeywordIf))
            {
                return ParseConstIfDeclaration();
            }

            // A `static { ... }` block, likewise, is not a modified declaration.
            if (reader.Check(TokenType.KeywordStatic) && reader.CheckAt(1, TokenType.LeftBrace))
            {
                reader.Advance();
                // Parsed first, for the same reason: the span has to close after the block, and
                // arguments evaluate left to right.
                BlockStatementSyntax body = ParseBlock();
                return new StaticBlockDeclarationSyntax(SpanFrom(start), body);
            }

            Modifiers modifiers = ParseModifiers();

            switch (reader.CurrentType)
            {
                case TokenType.KeywordClass:
                case TokenType.KeywordInterface:
                case TokenType.KeywordEnum:
                case TokenType.KeywordSingleton:
                    return ParseTypeDeclaration(start, docComment, attributes, modifiers, ToTypeKind(reader.CurrentType));

                case TokenType.KeywordAlias:
                    return ParseAliasDeclaration(start, docComment, attributes, modifiers);

                case TokenType.KeywordConstructor:
                    return ParseConstructor(start, docComment, attributes, modifiers);

                case TokenType.KeywordOperator:
                    return ParseOperator(start, docComment, attributes, modifiers);

                case TokenType.KeywordFun:
                    return ParseMethod(start, docComment, attributes, modifiers);

                case TokenType.KeywordLet:
                case TokenType.KeywordVar:
                    return ParseField(start, docComment, attributes, modifiers);

                default:
                    // `attribute class Foo` / `attribute(Targets) class Foo` (§11) is contextual the
                    // same way `value class` is: `attribute` is an ordinary identifier everywhere
                    // except right before a class declaration (with or without a target list).
                    if (CheckContextual("attribute")
                        && (reader.CheckAt(1, TokenType.KeywordClass) || reader.CheckAt(1, TokenType.LeftParen)))
                    {
                        return ParseAttributeClassDeclaration(start, docComment, attributes, modifiers);
                    }

                    // `value class` is contextual: `value` is an ordinary identifier and the
                    // `class` after it is what makes the declaration (§2.9).
                    if (CheckContextual("value") && reader.CheckAt(1, TokenType.KeywordClass))
                    {
                        reader.Advance();
                        return ParseTypeDeclaration(start, docComment, attributes, modifiers, TypeDeclarationKind.ValueClass);
                    }

                    // A `const` that reached here declares a compile-time binding (§7.1).
                    if (modifiers.IsConst && reader.Check(TokenType.Identifier))
                    {
                        return ParseField(start, docComment, attributes, modifiers);
                    }

                    // No introducer at all means a property — the whole disambiguation rule of §3.2.
                    if (reader.Check(TokenType.Identifier) && reader.CheckAt(1, TokenType.Colon))
                    {
                        return ParseProperty(start, docComment, attributes, modifiers);
                    }

                    throw reader.Error(SurtrDiagnosticCode.ExpectedDeclaration, $"Expected a declaration, found {reader.CurrentType}.");
            }
        }

        /// <summary>Maps a type-introducing keyword to its kind.</summary>
        private static TypeDeclarationKind ToTypeKind(TokenType type)
        {
            switch (type)
            {
                case TokenType.KeywordInterface: return TypeDeclarationKind.Interface;
                case TokenType.KeywordEnum: return TypeDeclarationKind.Enum;
                case TokenType.KeywordSingleton: return TypeDeclarationKind.Singleton;
                default: return TypeDeclarationKind.Class;
            }
        }

        /// <summary>Reads the modifier run in the order §3.2 fixes, rejecting a repeat of any one.</summary>
        private Modifiers ParseModifiers()
        {
            Modifiers modifiers = default;

            while (true)
            {
                switch (reader.CurrentType)
                {
                    case TokenType.KeywordPublic:
                    case TokenType.KeywordPrivate:
                    case TokenType.KeywordProtected:
                    case TokenType.KeywordInternal:
                        if (modifiers.Visibility != Visibility.Default)
                        {
                            throw reader.Error(SurtrDiagnosticCode.InvalidModifier, "A declaration can only carry one visibility.");
                        }

                        modifiers.Visibility = ToVisibility(reader.Advance().Type);
                        continue;

                    case TokenType.KeywordStatic:
                        modifiers.IsStatic = true;
                        reader.Advance();
                        continue;

                    case TokenType.KeywordSealed:
                        modifiers.IsSealed = true;
                        reader.Advance();
                        continue;

                    case TokenType.KeywordAbstract:
                        modifiers.IsAbstract = true;
                        modifiers.Dispatch = DispatchModifier.Abstract;
                        reader.Advance();
                        continue;

                    case TokenType.KeywordVirtual:
                        modifiers.Dispatch = DispatchModifier.Virtual;
                        reader.Advance();
                        continue;

                    case TokenType.KeywordOverride:
                        modifiers.Dispatch = DispatchModifier.Override;
                        reader.Advance();
                        continue;

                    case TokenType.KeywordInline:
                        modifiers.Inline = InlineModifier.Inline;
                        reader.Advance();
                        continue;

                    case TokenType.KeywordForceInline:
                        modifiers.Inline = InlineModifier.ForceInline;
                        reader.Advance();
                        continue;

                    case TokenType.KeywordNative:
                        modifiers.IsNative = true;
                        reader.Advance();
                        continue;

                    case TokenType.KeywordConst:
                        // A `const if` is a branch, not a modified declaration.
                        if (reader.CheckAt(1, TokenType.KeywordIf))
                        {
                            return modifiers;
                        }

                        modifiers.IsConst = true;
                        reader.Advance();
                        continue;

                    default:
                        return modifiers;
                }
            }
        }

        /// <summary>
        /// The modifiers an accessor may carry on top of its property's (§3.2, §3.4). Unlike
        /// <see cref="Modifiers"/>, static/const/native have no per-accessor meaning, so they are not
        /// part of this run at all.
        /// </summary>
        private struct AccessorModifiers
        {
            internal Visibility Visibility;
            internal bool HasDispatch;
            internal DispatchModifier Dispatch;
            internal bool IsSealed;
            internal InlineModifier Inline;
        }

        /// <summary>Reads the modifier run an accessor may carry before its `get`/`set` keyword.</summary>
        private AccessorModifiers ParseAccessorModifiers()
        {
            AccessorModifiers modifiers = default;

            while (true)
            {
                switch (reader.CurrentType)
                {
                    case TokenType.KeywordPublic:
                    case TokenType.KeywordPrivate:
                    case TokenType.KeywordProtected:
                    case TokenType.KeywordInternal:
                        if (modifiers.Visibility != Visibility.Default)
                        {
                            throw reader.Error(SurtrDiagnosticCode.InvalidModifier, "An accessor can only carry one visibility.");
                        }

                        modifiers.Visibility = ToVisibility(reader.Advance().Type);
                        continue;

                    case TokenType.KeywordSealed:
                        modifiers.HasDispatch = true;
                        modifiers.IsSealed = true;
                        reader.Advance();
                        continue;

                    case TokenType.KeywordAbstract:
                        modifiers.HasDispatch = true;
                        modifiers.Dispatch = DispatchModifier.Abstract;
                        reader.Advance();
                        continue;

                    case TokenType.KeywordVirtual:
                        modifiers.HasDispatch = true;
                        modifiers.Dispatch = DispatchModifier.Virtual;
                        reader.Advance();
                        continue;

                    case TokenType.KeywordOverride:
                        modifiers.HasDispatch = true;
                        modifiers.Dispatch = DispatchModifier.Override;
                        reader.Advance();
                        continue;

                    case TokenType.KeywordInline:
                        modifiers.Inline = InlineModifier.Inline;
                        reader.Advance();
                        continue;

                    case TokenType.KeywordForceInline:
                        modifiers.Inline = InlineModifier.ForceInline;
                        reader.Advance();
                        continue;

                    default:
                        return modifiers;
                }
            }
        }

        private static Visibility ToVisibility(TokenType type)
        {
            switch (type)
            {
                case TokenType.KeywordPublic: return Visibility.Public;
                case TokenType.KeywordPrivate: return Visibility.Private;
                case TokenType.KeywordProtected: return Visibility.Protected;
                default: return Visibility.Internal;
            }
        }

        /// <summary>Parses a class, value class, interface, enum or singleton (§2.2–§2.4, §2.8, §2.9).</summary>
        private DeclarationSyntax ParseTypeDeclaration(SourceLocation start, IReadOnlyList<string> docComment,
            IReadOnlyList<AttributeSyntax> attributes, Modifiers modifiers, TypeDeclarationKind kind,
            bool isAttribute = false, SurtrAttributeTargets attributeTargets = SurtrAttributeTargets.None, bool isCompileTimeOnlyAttribute = false)
        {
            reader.Advance();

            string name = reader.ExpectIdentifier("a type name");
            IReadOnlyList<TypeParameterSyntax> typeParameters = ParseTypeParameterList();

            // §2.2: the `:` list mixes the base class and interfaces, and only metadata can say
            // which a name resolves to, so the parser keeps them together.
            List<TypeSyntax> baseTypes = new List<TypeSyntax>();
            if (reader.Match(TokenType.Colon))
            {
                do
                {
                    baseTypes.Add(ParseType());
                }
                while (reader.Match(TokenType.Comma));
            }

            reader.Expect(TokenType.LeftBrace, "'{' to open the type body");

            List<EnumCaseSyntax> cases = kind == TypeDeclarationKind.Enum
                ? ParseEnumCases()
                : new List<EnumCaseSyntax>();

            List<DeclarationSyntax> members = new List<DeclarationSyntax>();
            while (!reader.Check(TokenType.RightBrace) && !reader.Check(TokenType.EndOfFile))
            {
                members.Add(ParseDeclaration());
            }

            reader.Expect(TokenType.RightBrace, "'}' to close the type body");

            return new TypeDeclarationSyntax(SpanFrom(start), attributes, docComment, modifiers.Visibility, kind, name,
                typeParameters, baseTypes, cases, members, modifiers.IsAbstract, modifiers.IsSealed, modifiers.IsStatic,
                isAttribute, attributeTargets, isCompileTimeOnlyAttribute);
        }

        /// <summary>
        /// Parses <c>attribute</c>'s optional <c>(Targets, ...)</c> list, then the class declaration
        /// itself (§11) — an <c>attribute</c> class is a class in every other respect.
        /// </summary>
        private DeclarationSyntax ParseAttributeClassDeclaration(SourceLocation start, IReadOnlyList<string> docComment,
            IReadOnlyList<AttributeSyntax> attributes, Modifiers modifiers)
        {
            reader.Advance(); // 'attribute'

            SurtrAttributeTargets targets = SurtrAttributeTargets.None;
            bool isCompileTimeOnly = false;

            if (reader.Check(TokenType.LeftParen))
            {
                reader.Advance();

                if (reader.Check(TokenType.RightParen))
                {
                    throw reader.Error(SurtrDiagnosticCode.UnexpectedToken,
                        "An attribute's target list cannot be empty; omit the parentheses entirely for no restriction.");
                }

                do
                {
                    string name = reader.ExpectIdentifier("a target ('Class', 'Interface', 'Enum', 'Field', 'Property', 'Method') or 'CompileTimeOnly'");

                    if (name == "CompileTimeOnly")
                    {
                        if (isCompileTimeOnly)
                        {
                            throw reader.Error(SurtrDiagnosticCode.InvalidModifier, "An attribute's retention can only be written once.");
                        }

                        isCompileTimeOnly = true;
                        continue;
                    }

                    SurtrAttributeTargets target = ToAttributeTarget(name);
                    if (target == SurtrAttributeTargets.None)
                    {
                        throw reader.Error(SurtrDiagnosticCode.UnexpectedToken, $"'{name}' is not a target an attribute can carry.");
                    }

                    if ((targets & target) != 0)
                    {
                        throw reader.Error(SurtrDiagnosticCode.InvalidModifier, $"'{name}' is already in this attribute's target list.");
                    }

                    targets |= target;
                }
                while (reader.Match(TokenType.Comma));

                reader.Expect(TokenType.RightParen, "')' to close the attribute's target list");
            }

            return ParseTypeDeclaration(start, docComment, attributes, modifiers, TypeDeclarationKind.Class,
                isAttribute: true, attributeTargets: targets, isCompileTimeOnlyAttribute: isCompileTimeOnly);
        }

        private static SurtrAttributeTargets ToAttributeTarget(string name) => name switch
        {
            "Class" => SurtrAttributeTargets.Class,
            "Interface" => SurtrAttributeTargets.Interface,
            "Enum" => SurtrAttributeTargets.Enum,
            "Field" => SurtrAttributeTargets.Field,
            "Property" => SurtrAttributeTargets.Property,
            "Method" => SurtrAttributeTargets.Method,
            _ => SurtrAttributeTargets.None,
        };

        /// <summary>
        /// Parses an enum's case list. Per §2.4 the trailing <c>;</c> is only needed when members
        /// follow, so a bare <c>{ Red, Green }</c> ends at the closing brace instead.
        /// </summary>
        private List<EnumCaseSyntax> ParseEnumCases()
        {
            List<EnumCaseSyntax> cases = new List<EnumCaseSyntax>();

            while (true)
            {
                IReadOnlyList<string> caseDoc = ParseDocComment();

                if (!reader.Check(TokenType.Identifier))
                {
                    break;
                }

                SourceLocation start = reader.CurrentLocation;
                string name = reader.Advance().ToString();

                IReadOnlyList<ArgumentSyntax> arguments = reader.Check(TokenType.LeftParen)
                    ? ParseArgumentList()
                    : new List<ArgumentSyntax>();

                cases.Add(new EnumCaseSyntax(SpanFrom(start), name, arguments, caseDoc));

                if (!reader.Match(TokenType.Comma))
                {
                    break;
                }
            }

            reader.Match(TokenType.Semicolon);
            return cases;
        }

        /// <summary>Parses <c>alias Name&lt;T&gt; = Type;</c> (§2.7).</summary>
        private DeclarationSyntax ParseAliasDeclaration(SourceLocation start, IReadOnlyList<string> docComment,
            IReadOnlyList<AttributeSyntax> attributes, Modifiers modifiers)
        {
            reader.Advance();

            string name = reader.ExpectIdentifier("an alias name");
            IReadOnlyList<TypeParameterSyntax> typeParameters = ParseTypeParameterList();

            reader.Expect(TokenType.Assign, "'=' in the alias declaration");
            TypeSyntax target = ParseType();
            reader.Expect(TokenType.Semicolon, "';' after the alias");

            return new AliasDeclarationSyntax(SpanFrom(start), attributes, docComment, modifiers.Visibility, name, typeParameters, target);
        }

        /// <summary>Parses a field, or a module-level variable (§3.2, §2.5, §7.1).</summary>
        private DeclarationSyntax ParseField(SourceLocation start, IReadOnlyList<string> docComment,
            IReadOnlyList<AttributeSyntax> attributes, Modifiers modifiers)
        {
            bool isMutable = false;

            if (reader.Check(TokenType.KeywordLet) || reader.Check(TokenType.KeywordVar))
            {
                isMutable = reader.CurrentType == TokenType.KeywordVar;
                reader.Advance();
            }

            string name = reader.ExpectIdentifier("a field name");
            TypeSyntax? type = reader.Match(TokenType.Colon) ? ParseType() : null;
            ExpressionSyntax? initializer = reader.Match(TokenType.Assign) ? ParseExpression() : null;

            reader.Expect(TokenType.Semicolon, "';' after the field");

            return new FieldDeclarationSyntax(SpanFrom(start), attributes, docComment, modifiers.Visibility, name, type,
                initializer, isMutable, modifiers.IsConst, modifiers.IsStatic, modifiers.IsNative);
        }

        /// <summary>
        /// Lowers an arrow-bodied member (§3.3, §3.4) to a one-statement block, so nothing
        /// downstream of the parser needs to know the sugar was used. A <c>void</c>-returning
        /// method and a property setter wrap the expression as a statement rather than a
        /// <c>return</c> — a literal <c>return &lt;value&gt;</c> there is rejected by
        /// <c>BodyBinder.BindReturn</c>, the same reason C# treats an expression-bodied member as
        /// a statement rather than sugar for a written <c>return</c>.
        /// </summary>
        private BlockStatementSyntax ParseArrowBody(bool returnsVoid)
        {
            SourceLocation arrowStart = reader.CurrentLocation;
            reader.Advance(); // '=>'

            SourceLocation expressionStart = reader.CurrentLocation;
            ExpressionSyntax expression = ParseExpression();
            reader.Expect(TokenType.Semicolon, "';' after the expression body");

            SourceSpan statementSpan = SpanFrom(expressionStart);
            StatementSyntax statement = returnsVoid
                ? new ExpressionStatementSyntax(statementSpan, expression)
                : new ReturnStatementSyntax(statementSpan, expression);

            return new BlockStatementSyntax(SpanFrom(arrowStart), new StatementSyntax[] { statement });
        }

        /// <summary>Parses a property, auto or with explicit accessors (§3.4).</summary>
        private DeclarationSyntax ParseProperty(SourceLocation start, IReadOnlyList<string> docComment,
            IReadOnlyList<AttributeSyntax> attributes, Modifiers modifiers)
        {
            string name = reader.Advance().ToString();
            reader.Expect(TokenType.Colon, "':' before the property type");
            TypeSyntax type = ParseType();

            // The short form of a read-only property (§3.4): `x: int => field;` is sugar for a
            // single `get => field;` accessor, skipping the braces entirely.
            if (reader.Check(TokenType.FatArrow))
            {
                BlockStatementSyntax getterBody = ParseArrowBody(returnsVoid: false);
                var getter = new AccessorSyntax(getterBody.Span, isGetter: true, getterBody);

                return new PropertyDeclarationSyntax(SpanFrom(start), attributes, docComment, modifiers.Visibility, name, type,
                    new[] { getter }, modifiers.IsStatic, modifiers.Dispatch, modifiers.IsSealed, modifiers.Inline, modifiers.IsNative);
            }

            // An interface's property is signature-only (§2.3), so the accessor block may be
            // followed by nothing at all.
            reader.Expect(TokenType.LeftBrace, "'{' to open the property accessors");

            List<AccessorSyntax> accessors = new List<AccessorSyntax>();
            while (!reader.Check(TokenType.RightBrace) && !reader.Check(TokenType.EndOfFile))
            {
                SourceLocation accessorStart = reader.CurrentLocation;

                // An accessor may carry its own modifier run before `get`/`set` (§3.2, §3.4) —
                // visibility, `virtual`/`override`/`abstract`/`sealed`, and `inline`/`forceinline` —
                // independently of the property's own. Whatever it does not write inherits the
                // property's.
                AccessorModifiers accessorModifiers = ParseAccessorModifiers();

                if (!reader.Check(TokenType.Identifier))
                {
                    throw reader.Error(SurtrDiagnosticCode.UnexpectedToken, "Expected 'get' or 'set'.");
                }

                bool isGetter = CheckContextual("get");
                if (!isGetter && !CheckContextual("set"))
                {
                    throw reader.Error(SurtrDiagnosticCode.UnexpectedToken, "Expected 'get' or 'set'.");
                }

                reader.Advance();

                // A bare accessor is the auto-generated form; a braced one has a real body; an
                // arrow one (§3.4) is sugar for the braced form — a getter returns its expression,
                // a setter evaluates it for effect, the same rule ParseArrowBody applies to a method.
                BlockStatementSyntax? body = null;
                if (reader.Check(TokenType.FatArrow))
                {
                    body = ParseArrowBody(returnsVoid: !isGetter);
                }
                else if (reader.Check(TokenType.LeftBrace))
                {
                    body = ParseBlock();
                }
                else
                {
                    reader.Expect(TokenType.Semicolon, "';' after the accessor");
                }

                accessors.Add(new AccessorSyntax(
                    SpanFrom(accessorStart), isGetter, body,
                    accessorModifiers.Visibility, accessorModifiers.HasDispatch, accessorModifiers.Dispatch,
                    accessorModifiers.IsSealed, accessorModifiers.Inline));
            }

            reader.Expect(TokenType.RightBrace, "'}' to close the property accessors");

            return new PropertyDeclarationSyntax(SpanFrom(start), attributes, docComment, modifiers.Visibility, name, type,
                accessors, modifiers.IsStatic, modifiers.Dispatch, modifiers.IsSealed, modifiers.Inline, modifiers.IsNative);
        }

        /// <summary>Parses a method, or a module-level function (§3.2, §2.5).</summary>
        private DeclarationSyntax ParseMethod(SourceLocation start, IReadOnlyList<string> docComment,
            IReadOnlyList<AttributeSyntax> attributes, Modifiers modifiers)
        {
            reader.Advance();

            string name = reader.ExpectIdentifier("a method name");
            IReadOnlyList<TypeParameterSyntax> typeParameters = ParseTypeParameterList();
            IReadOnlyList<ParameterSyntax> parameters = ParseParameterList();

            reader.Expect(TokenType.Colon, "':' before the return type");
            TypeSyntax returnType = ParseType();

            // No body means abstract, native, or an interface member — all signature-only (§2.3).
            // An arrow body (§3.3) is sugar for a block holding one statement — a `return` for a
            // value-returning method, an expression statement for a `void` one.
            BlockStatementSyntax? body = null;
            if (reader.Check(TokenType.FatArrow))
            {
                bool returnsVoid = returnType is NamedTypeSyntax namedReturnType
                    && namedReturnType.Path.Count == 1
                    && namedReturnType.Path[0] == "void";
                body = ParseArrowBody(returnsVoid);
            }
            else if (!reader.Match(TokenType.Semicolon))
            {
                body = ParseBlock();
            }

            return new MethodDeclarationSyntax(SpanFrom(start), attributes, docComment, modifiers.Visibility, name, typeParameters,
                parameters, returnType, body, modifiers.IsStatic, modifiers.Dispatch, modifiers.IsSealed,
                modifiers.Inline, modifiers.IsConst, modifiers.IsNative);
        }

        /// <summary>Parses a constructor and its optional <c>: super(...)</c> or <c>: this(...)</c> chain (§3.2).</summary>
        private DeclarationSyntax ParseConstructor(SourceLocation start, IReadOnlyList<string> docComment,
            IReadOnlyList<AttributeSyntax> attributes, Modifiers modifiers)
        {
            reader.Advance();
            IReadOnlyList<ParameterSyntax> parameters = ParseParameterList();

            IReadOnlyList<ArgumentSyntax>? chainArguments = null;
            bool chainsToThis = false;

            if (reader.Match(TokenType.Colon))
            {
                if (CheckContextual("this"))
                {
                    chainsToThis = true;
                }
                else if (!CheckContextual("super"))
                {
                    throw reader.Error(SurtrDiagnosticCode.InvalidConstructorChain, "A constructor chain must call 'super' or 'this'.");
                }

                reader.Advance();
                chainArguments = ParseArgumentList();
            }

            BlockStatementSyntax body = ParseBlock();
            return new ConstructorDeclarationSyntax(SpanFrom(start), attributes, docComment, modifiers.Visibility,
                parameters, chainArguments, chainsToThis, body);
        }

        /// <summary>
        /// Parses an operator overload (§5.6). Public is implied and never written; <c>static</c> is
        /// the default, and a dispatch modifier (<c>virtual</c>, <c>override</c>, <c>abstract</c>) or
        /// <c>sealed</c> makes the operator an instance method whose receiver is its first parameter.
        /// </summary>
        private DeclarationSyntax ParseOperator(SourceLocation start, IReadOnlyList<string> docComment,
            IReadOnlyList<AttributeSyntax> attributes, Modifiers modifiers)
        {
            if (modifiers.Visibility != Visibility.Default || modifiers.IsStatic)
            {
                throw reader.Error(SurtrDiagnosticCode.InvalidModifier, "An operator overload is always public; 'static' is never written.", start);
            }

            reader.Advance();

            TokenType op = reader.CurrentType;
            TypeSyntax? conversionTarget = null;

            // `operator[]` is the one whose token is a pair rather than a single operator.
            if (op == TokenType.LeftBracket)
            {
                reader.Advance();
                reader.Expect(TokenType.RightBracket, "']' in 'operator[]'");
            }
            else if (op == TokenType.KeywordAs)
            {
                reader.Advance();

                if (reader.Check(TokenType.LeftParen))
                {
                    throw reader.Error(SurtrDiagnosticCode.InvalidOperatorDeclaration, "A conversion names its target after 'as': write 'operator as Vec3(v: Vec2)'.");
                }

                conversionTarget = ParseType();
            }
            else if (IsOverloadableOperator(op))
            {
                reader.Advance();
            }
            else
            {
                throw reader.Error(SurtrDiagnosticCode.InvalidOperatorDeclaration, $"'{reader.Current}' cannot be overloaded.");
            }

            IReadOnlyList<ParameterSyntax> parameters = ParseParameterList();

            // A conversion has already named its target, and writing it again as a return type
            // could only ever disagree with the first spelling. Every other overload still writes
            // one, because nothing else in its declaration says what it produces.
            TypeSyntax returnType;
            if (conversionTarget is null)
            {
                reader.Expect(TokenType.Colon, "':' before the operator's return type");
                returnType = ParseType();
            }
            else
            {
                returnType = conversionTarget;
            }

            // No body means an abstract operator, declared for a subclass to override — signature-only,
            // exactly as a method's (§3.2). An interface's operators are always written this way.
            BlockStatementSyntax? operatorBody = null;
            if (!reader.Match(TokenType.Semicolon))
            {
                operatorBody = ParseBlock();
            }

            return new OperatorDeclarationSyntax(SpanFrom(start), attributes, docComment, op, parameters, returnType,
                modifiers.Dispatch, modifiers.IsSealed, operatorBody);
        }

        /// <summary>
        /// The overloads whose token is the whole declaration. Everything absent here is absent
        /// for a stated reason — except <c>[]</c> and <c>as</c>, which are absent because each
        /// carries something after the token and so is recognised by the caller instead.
        /// </summary>
        private static bool IsOverloadableOperator(TokenType type)
        {
            switch (type)
            {
                case TokenType.Plus:
                case TokenType.Minus:
                case TokenType.Star:
                case TokenType.Slash:
                case TokenType.Percent:
                case TokenType.Ampersand:
                case TokenType.Pipe:
                case TokenType.Caret:
                case TokenType.ShiftLeft:
                case TokenType.ShiftRight:
                case TokenType.UnsignedShiftRight:
                case TokenType.LogicalNot:
                case TokenType.Tilde:
                case TokenType.Increment:
                case TokenType.Decrement:
                case TokenType.Equal:
                case TokenType.Spaceship:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Parses a parameter list, with defaults and varargs (§3.5).</summary>
        private IReadOnlyList<ParameterSyntax> ParseParameterList()
        {
            reader.Expect(TokenType.LeftParen, "'(' to open the parameters");

            List<ParameterSyntax> parameters = new List<ParameterSyntax>();
            while (!reader.Check(TokenType.RightParen))
            {
                SourceLocation start = reader.CurrentLocation;
                string name = reader.ExpectIdentifier("a parameter name");

                reader.Expect(TokenType.Colon, "':' before the parameter type");
                TypeSyntax type = ParseType();

                bool isVarargs = reader.Match(TokenType.Ellipsis);
                ExpressionSyntax? defaultValue = reader.Match(TokenType.Assign) ? ParseExpression() : null;

                parameters.Add(new ParameterSyntax(SpanFrom(start), name, type, defaultValue, isVarargs));

                if (!reader.Match(TokenType.Comma))
                {
                    break;
                }
            }

            reader.Expect(TokenType.RightParen, "')' to close the parameters");
            return parameters;
        }

        /// <summary>Parses a declaration-level <c>const if</c>, the form that replaces <c>#if</c> (§7.3).</summary>
        private DeclarationSyntax ParseConstIfDeclaration()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.KeywordConst, "'const'");
            reader.Expect(TokenType.KeywordIf, "'if'");
            reader.Expect(TokenType.LeftParen, "'(' after 'const if'");
            ExpressionSyntax condition = ParseExpression();
            reader.Expect(TokenType.RightParen, "')' after the condition");

            IReadOnlyList<DeclarationSyntax> then = ParseDeclarationGroup();
            IReadOnlyList<DeclarationSyntax> otherwise = new List<DeclarationSyntax>();

            if (reader.Match(TokenType.KeywordElse))
            {
                // `else const if` chains, exactly as an ordinary `else if` does.
                otherwise = reader.Check(TokenType.KeywordConst)
                    ? new List<DeclarationSyntax> { ParseConstIfDeclaration() }
                    : ParseDeclarationGroup();
            }

            return new ConstIfDeclarationSyntax(SpanFrom(start), condition, then, otherwise);
        }

        /// <summary>Parses the braced group of declarations forming one branch of a declaration-level <c>const if</c>.</summary>
        private IReadOnlyList<DeclarationSyntax> ParseDeclarationGroup()
        {
            reader.Expect(TokenType.LeftBrace, "'{' to open the declarations");

            List<DeclarationSyntax> declarations = new List<DeclarationSyntax>();
            while (!reader.Check(TokenType.RightBrace) && !reader.Check(TokenType.EndOfFile))
            {
                declarations.Add(ParseDeclaration());
            }

            reader.Expect(TokenType.RightBrace, "'}' to close the declarations");
            return declarations;
        }
    }
}
