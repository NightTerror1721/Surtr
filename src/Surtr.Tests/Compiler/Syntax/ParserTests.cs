#nullable enable

using System.Collections.Generic;
using System.Linq;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;

namespace Surtr.Tests.Compiler.Syntax
{
    /// <summary>
    /// Covers the parser against docs/Language-Syntax.md. The cases that carry their weight are
    /// the ambiguities the grammar cannot settle alone — a lambda against a tuple, a block against
    /// a dict literal, a property against a field, and the <c>&gt;&gt;</c> the lexer hands back
    /// whole.
    /// </summary>
    public sealed class ParserTests
    {
        private static CompilationUnitSyntax Parse(string source)
        {
            return new Parser(SurtrSourceBuffer.FromString(source)).ParseCompilationUnit();
        }

        private static T ParseSingle<T>(string source) where T : DeclarationSyntax
        {
            return Assert.IsType<T>(Parse(source).Declarations.Single());
        }

        /// <summary>Wraps an expression in enough scaffolding to reach it, then digs it back out.</summary>
        private static ExpressionSyntax ParseExpression(string expression)
        {
            MethodDeclarationSyntax method = ParseSingle<MethodDeclarationSyntax>($"fun f(): void {{ let x = {expression}; }}");
            LocalDeclarationStatementSyntax local = Assert.IsType<LocalDeclarationStatementSyntax>(method.Body!.Statements.Single());
            return local.Initializer!;
        }

        private static StatementSyntax ParseStatement(string statement)
        {
            MethodDeclarationSyntax method = ParseSingle<MethodDeclarationSyntax>($"fun f(): void {{ {statement} }}");
            return method.Body!.Statements.Single();
        }

        /// <summary>Asserts that a source is rejected, and with which diagnostic.</summary>
        /// <remarks>
        /// Asserting the code rather than the message: a code is the stable name for a problem, and
        /// a test keyed on wording breaks the next time the wording improves.
        /// </remarks>
        private static void AssertRejected(string source, SurtrDiagnosticCode expected)
        {
            Parser parser = new Parser(SurtrSourceBuffer.FromString(source));
            parser.ParseCompilationUnit();

            Assert.True(parser.Diagnostics.HasErrors, $"'{source}' was expected to be rejected.");
            Assert.Contains(parser.Diagnostics, diagnostic => diagnostic.Code == expected);
        }

        // ---- §2.1 imports --------------------------------------------------------------------

        [Fact]
        public void ImportsParseNamedAndWildcardForms()
        {
            CompilationUnitSyntax unit = Parse("import Ogame.core.Entity;\nimport Ogame.core.*;");

            Assert.Equal(2, unit.Imports.Count);
            Assert.Equal(new[] { "Ogame", "core", "Entity" }, unit.Imports[0].Path);
            Assert.False(unit.Imports[0].IsWildcard);
            Assert.Equal(new[] { "Ogame", "core" }, unit.Imports[1].Path);
            Assert.True(unit.Imports[1].IsWildcard);
        }

        // ---- §2.2–§2.9 type declarations -----------------------------------------------------

        [Fact]
        public void ClassKeepsBaseListUndifferentiated()
        {
            // §2.2: only metadata can say which name is the base class, so the parser keeps all
            // of them in one list rather than guessing.
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>("public sealed class Foo : Base, IBar, IBaz { }");

            Assert.Equal(TypeDeclarationKind.Class, type.Kind);
            Assert.Equal(Visibility.Public, type.Visibility);
            Assert.True(type.IsSealed);
            Assert.Equal(3, type.BaseTypes.Count);
        }

        [Theory]
        [InlineData("class Foo { }", TypeDeclarationKind.Class)]
        [InlineData("interface Foo { }", TypeDeclarationKind.Interface)]
        [InlineData("enum Foo { }", TypeDeclarationKind.Enum)]
        [InlineData("singleton Foo { }", TypeDeclarationKind.Singleton)]
        [InlineData("value class Foo { }", TypeDeclarationKind.ValueClass)]
        public void EveryTypeKindParses(string source, TypeDeclarationKind expected)
        {
            Assert.Equal(expected, ParseSingle<TypeDeclarationSyntax>(source).Kind);
        }

        /// <summary>§2.9: `value` is contextual, so it must still work as an ordinary name.</summary>
        [Fact]
        public void ValueRemainsUsableAsAnIdentifier()
        {
            FieldDeclarationSyntax field = ParseSingle<FieldDeclarationSyntax>("let value: int = 1;");

            Assert.Equal("value", field.Name);
        }

        [Fact]
        public void EnumParsesCasesWithArgumentsThenMembers()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "enum Suit : ICardSuit { Hearts(\"h\", true), Spades(\"s\", false); private let _s: string; }");

            Assert.Equal(2, type.EnumCases.Count);
            Assert.Equal("Hearts", type.EnumCases[0].Name);
            Assert.Equal(2, type.EnumCases[0].Arguments.Count);
            Assert.Single(type.Members);
        }

        /// <summary>§2.4: the trailing `;` is only needed when members follow.</summary>
        [Fact]
        public void BareEnumNeedsNoTrailingSemicolon()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>("enum Color { Red, Green, Blue }");

            Assert.Equal(3, type.EnumCases.Count);
            Assert.Empty(type.Members);
        }

        [Fact]
        public void GenericAliasParses()
        {
            AliasDeclarationSyntax alias = ParseSingle<AliasDeclarationSyntax>("alias IntMap<V> = {int: V};");

            Assert.Equal("IntMap", alias.Name);
            Assert.Single(alias.TypeParameters);
            Assert.IsType<DictTypeSyntax>(alias.Target);
        }

        [Fact]
        public void ClosureAliasParsesAsClosureType()
        {
            AliasDeclarationSyntax alias = ParseSingle<AliasDeclarationSyntax>("alias Handler = (Entity, float) -> void;");

            ClosureTypeSyntax closure = Assert.IsType<ClosureTypeSyntax>(alias.Target);
            Assert.Equal(2, closure.ParameterTypes.Count);
        }

        // ---- §3 members ----------------------------------------------------------------------

        /// <summary>§3.2: the introducer keyword is the whole disambiguation rule.</summary>
        [Fact]
        public void MissingIntroducerMakesAProperty()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "class Foo { private let _n: string; public name: string { get; set; } public age: int { get { return 1; } } }");

            Assert.IsType<FieldDeclarationSyntax>(type.Members[0]);

            PropertyDeclarationSyntax auto = Assert.IsType<PropertyDeclarationSyntax>(type.Members[1]);
            Assert.Equal(2, auto.Accessors.Count);
            Assert.All(auto.Accessors, accessor => Assert.Null(accessor.Body));

            PropertyDeclarationSyntax custom = Assert.IsType<PropertyDeclarationSyntax>(type.Members[2]);
            Assert.NotNull(custom.Accessors.Single().Body);
        }

        [Fact]
        public void SealedOverrideParsesAsBothModifiers()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "class Dog : Animal { public sealed override fun speak(): string { return \"w\"; } }");

            MethodDeclarationSyntax method = Assert.IsType<MethodDeclarationSyntax>(type.Members.Single());
            Assert.Equal(DispatchModifier.Override, method.Dispatch);
            Assert.True(method.IsSealed);
        }

        [Fact]
        public void ConstructorParsesItsChain()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "class Dog : Animal { public constructor(n: string) : super(n) { } }");

            ConstructorDeclarationSyntax constructor = Assert.IsType<ConstructorDeclarationSyntax>(type.Members.Single());
            Assert.False(constructor.ChainsToThis);
            Assert.Single(constructor.ChainArguments!);
        }

        [Fact]
        public void SignatureOnlyMemberHasNoBody()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>("interface IBar { fun doThing(x: int): void; }");

            Assert.Null(Assert.IsType<MethodDeclarationSyntax>(type.Members.Single()).Body);
        }

        [Fact]
        public void ParametersCarryDefaultsAndVarargs()
        {
            MethodDeclarationSyntax method = ParseSingle<MethodDeclarationSyntax>(
                "fun format(pattern: string, args: string...): string { return pattern; }");

            Assert.False(method.Parameters[0].IsVarargs);
            Assert.True(method.Parameters[1].IsVarargs);

            MethodDeclarationSyntax withDefault = ParseSingle<MethodDeclarationSyntax>(
                "fun spawn(x: float, hp: int = 100): void { }");

            Assert.Null(withDefault.Parameters[0].DefaultValue);
            Assert.NotNull(withDefault.Parameters[1].DefaultValue);
        }

        // ---- §5.6 operators ------------------------------------------------------------------

        [Theory]
        [InlineData("operator+(a: V, b: V): V { }", TokenType.Plus)]
        [InlineData("operator-(v: V): V { }", TokenType.Minus)]
        [InlineData("operator>>>(a: V, b: int): V { }", TokenType.UnsignedShiftRight)]
        [InlineData("operator<=>(a: V, b: V): int { }", TokenType.Spaceship)]
        [InlineData("operator++(v: V): V { }", TokenType.Increment)]
        [InlineData("operator!(v: V): bool { }", TokenType.LogicalNot)]
        [InlineData("operator[](i: int): float { }", TokenType.LeftBracket)]
        [InlineData("operator as Vec3(v: V) { }", TokenType.KeywordAs)]
        public void OperatorOverloadsParse(string source, TokenType expected)
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>($"class V {{ {source} }}");

            Assert.Equal(expected, Assert.IsType<OperatorDeclarationSyntax>(type.Members.Single()).Operator);
        }

        /// <summary>§5.6: a conversion's target is written after the keyword and is its return type.</summary>
        [Fact]
        public void ConversionOperatorTakesItsTargetAsReturnType()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>("class V { operator as Vec3(v: V) { } }");
            OperatorDeclarationSyntax declared = Assert.IsType<OperatorDeclarationSyntax>(type.Members.Single());

            Assert.Equal(TokenType.KeywordAs, declared.Operator);
            Assert.Equal("Vec3", Assert.IsType<NamedTypeSyntax>(declared.ReturnType).Path.Single());
        }

        /// <summary>
        /// §5.6: two conversions from the same operand are told apart by their target, which is
        /// the whole reason the target moved in front of the parameter list.
        /// </summary>
        [Fact]
        public void ConversionsFromOneOperandDifferByTarget()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "class V { operator as Vec3(v: V) { } operator as string(v: V) { } }");

            string[] targets = type.Members
                .Cast<OperatorDeclarationSyntax>()
                .Select(op => ((NamedTypeSyntax)op.ReturnType).Path.Single())
                .ToArray();

            Assert.Equal(new[] { "Vec3", "string" }, targets);
        }

        /// <summary>A composite target keeps its suffixes: `operator as int[]` converts to an array.</summary>
        [Fact]
        public void ConversionTargetMayBeComposite()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>("class V { operator as int[](v: V) { } }");
            OperatorDeclarationSyntax declared = Assert.IsType<OperatorDeclarationSyntax>(type.Members.Single());

            Assert.IsType<ArrayTypeSyntax>(declared.ReturnType);
        }

        /// <summary>The pre-§5.6 spelling, with the target as a trailing return type, is rejected outright.</summary>
        [Fact]
        public void ConversionRejectsTrailingReturnTypeForm()
        {
            AssertRejected("class V { operator as(v: V): Vec3 { } }", SurtrDiagnosticCode.InvalidOperatorDeclaration);
        }

        /// <summary>§5.6: an overload is always public and static by default, so writing either is an error, not a redundancy.</summary>
        [Fact]
        public void OperatorRejectsRedundantModifiers()
        {
            AssertRejected("class V { public static operator+(a: V, b: V): V { } }", SurtrDiagnosticCode.InvalidModifier);
            AssertRejected("class V { static operator+(a: V, b: V): V { } }", SurtrDiagnosticCode.InvalidModifier);
        }

        /// <summary>A dispatch modifier makes an operator an instance method, so the parser keeps it rather than rejecting it.</summary>
        [Fact]
        public void ADispatchModifierOnAnOperatorParses()
        {
            OperatorDeclarationSyntax declared = ParseSingle<TypeDeclarationSyntax>(
                "class V { virtual operator+(a: V, b: V): V { } }").Members
                .Cast<OperatorDeclarationSyntax>()
                .Single();

            Assert.Equal(DispatchModifier.Virtual, declared.Dispatch);
            Assert.NotNull(declared.Body);
        }

        /// <summary>An abstract operator ends at the semicolon, exactly as an abstract method does.</summary>
        [Fact]
        public void AnAbstractOperatorIsBodyless()
        {
            OperatorDeclarationSyntax declared = ParseSingle<TypeDeclarationSyntax>(
                "class V { abstract operator+(a: V, b: V): V; }").Members
                .Cast<OperatorDeclarationSyntax>()
                .Single();

            Assert.Equal(DispatchModifier.Abstract, declared.Dispatch);
            Assert.Null(declared.Body);
        }

        /// <summary><c>sealed</c> is a legitimate operator modifier: it closes a virtual operator's branch.</summary>
        [Fact]
        public void SealedOnAnOperatorParses()
        {
            OperatorDeclarationSyntax declared = ParseSingle<TypeDeclarationSyntax>(
                "class V { sealed override operator+(a: V, b: V): V { } }").Members
                .Cast<OperatorDeclarationSyntax>()
                .Single();

            Assert.True(declared.IsSealed);
            Assert.Equal(DispatchModifier.Override, declared.Dispatch);
        }

        [Fact]
        public void NonOverloadableOperatorIsRejected()
        {
            AssertRejected("class V { operator&&(a: V, b: V): V { } }", SurtrDiagnosticCode.InvalidOperatorDeclaration);
        }

        // ---- §5.7 precedence -----------------------------------------------------------------

        /// <summary>Multiplicative binds tighter than additive: `a + b * c` is `a + (b * c)`.</summary>
        [Fact]
        public void PrecedenceNestsTighterOperatorsDeeper()
        {
            BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a + b * c"));

            Assert.Equal(BinaryOperator.Add, root.Operator);
            Assert.Equal(BinaryOperator.Multiply, Assert.IsType<BinaryExpressionSyntax>(root.Right).Operator);
        }

        /// <summary>Left-associativity: `a - b - c` is `(a - b) - c`, not `a - (b - c)`.</summary>
        [Fact]
        public void BinaryOperatorsAreLeftAssociative()
        {
            BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a - b - c"));

            Assert.IsType<BinaryExpressionSyntax>(root.Left);
            Assert.IsType<IdentifierExpressionSyntax>(root.Right);
        }

        /// <summary>Assignment is right-associative: `a = b = c` is `a = (b = c)`.</summary>
        [Fact]
        public void AssignmentIsRightAssociative()
        {
            StatementSyntax statement = ParseStatement("a = b = c;");
            ExpressionSyntax expression = Assert.IsType<ExpressionStatementSyntax>(statement).Expression;

            AssignmentExpressionSyntax outer = Assert.IsType<AssignmentExpressionSyntax>(expression);
            Assert.IsType<AssignmentExpressionSyntax>(outer.Value);
        }

        /// <summary>§5.7: `&lt;=&gt;` binds tighter than the relational operators.</summary>
        [Fact]
        public void SpaceshipBindsTighterThanRelational()
        {
            BinaryExpressionSyntax root = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a <=> b < c"));

            Assert.Equal(BinaryOperator.Less, root.Operator);
            Assert.Equal(BinaryOperator.Compare, Assert.IsType<BinaryExpressionSyntax>(root.Left).Operator);
        }

        [Fact]
        public void BothRightShiftsParseAsDistinctOperators()
        {
            Assert.Equal(BinaryOperator.ShiftRight, Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a >> b")).Operator);
            Assert.Equal(BinaryOperator.UnsignedShiftRight, Assert.IsType<BinaryExpressionSyntax>(ParseExpression("a >>> b")).Operator);
        }

        [Fact]
        public void CastAndTypeTestParse()
        {
            Assert.False(Assert.IsType<CastExpressionSyntax>(ParseExpression("x as Dog")).IsSafe);
            Assert.True(Assert.IsType<CastExpressionSyntax>(ParseExpression("x as? Dog")).IsSafe);
            Assert.IsType<TypeTestExpressionSyntax>(ParseExpression("x is Dog"));
        }

        [Fact]
        public void PostfixOperatorsChainOntoTheirTarget()
        {
            Assert.Equal(UnaryOperator.NullAssert, Assert.IsType<UnaryExpressionSyntax>(ParseExpression("a?.b!!")).Operator);
            Assert.True(Assert.IsType<MemberAccessExpressionSyntax>(ParseExpression("a?.b")).IsNullConditional);
            Assert.Equal(UnaryOperator.PostIncrement, Assert.IsType<UnaryExpressionSyntax>(ParseExpression("a++")).Operator);
            Assert.Equal(UnaryOperator.PreIncrement, Assert.IsType<UnaryExpressionSyntax>(ParseExpression("++a")).Operator);
        }

        // ---- the ambiguities -----------------------------------------------------------------

        /// <summary>
        /// A lambda and a tuple are identical up to the closing paren, so nothing but scanning
        /// ahead for the `=&gt;` tells them apart.
        /// </summary>
        [Fact]
        public void LambdaAndTupleAreToldApartByTheArrow()
        {
            Assert.IsType<TupleLiteralExpressionSyntax>(ParseExpression("(a, b)"));
            Assert.IsType<LambdaExpressionSyntax>(ParseExpression("(a, b) => a + b"));

            // One parenthesized element is a grouping, not a one-element tuple.
            Assert.IsType<IdentifierExpressionSyntax>(ParseExpression("(a)"));
            Assert.IsType<LambdaExpressionSyntax>(ParseExpression("(a) => a"));
        }

        [Fact]
        public void LambdaParameterTypesAreOptional()
        {
            LambdaExpressionSyntax inferred = Assert.IsType<LambdaExpressionSyntax>(ParseExpression("(x) => x * 2"));
            Assert.Null(inferred.Parameters.Single().Type);

            LambdaExpressionSyntax annotated = Assert.IsType<LambdaExpressionSyntax>(ParseExpression("(x: int) => x * 2"));
            Assert.NotNull(annotated.Parameters.Single().Type);

            LambdaExpressionSyntax block = Assert.IsType<LambdaExpressionSyntax>(ParseExpression("(x: int) => { return x; }"));
            Assert.NotNull(block.BlockBody);
            Assert.Null(block.Body);
        }

        /// <summary>§5.4: position decides. A `{` where a statement is due is a block; in expression position it is a dict.</summary>
        [Fact]
        public void BraceMeansBlockInStatementPositionAndDictInExpressionPosition()
        {
            Assert.IsType<BlockStatementSyntax>(ParseStatement("{ }"));
            Assert.IsType<DictLiteralExpressionSyntax>(ParseExpression("{ \"a\": 1 }"));
        }

        /// <summary>
        /// The debt the lexer left the parser: `Box&lt;Box&lt;int&gt;&gt;` closes as one
        /// `&gt;&gt;` token, and three levels as one `&gt;&gt;&gt;`.
        /// </summary>
        [Theory]
        [InlineData("Box<int>")]
        [InlineData("Box<Box<int>>")]
        [InlineData("Box<Box<Box<int>>>")]
        [InlineData("Box<Box<Box<Box<int>>>>")]
        [InlineData("{int: Box<Box<int>>}")]
        public void NestedGenericsCloseCorrectly(string type)
        {
            FieldDeclarationSyntax field = ParseSingle<FieldDeclarationSyntax>($"let x: {type} = y;");

            Assert.NotNull(field.Type);
        }

        [Fact]
        public void NestedGenericsKeepTheirStructure()
        {
            FieldDeclarationSyntax field = ParseSingle<FieldDeclarationSyntax>("let x: Box<Box<Box<int>>> = y;");

            NamedTypeSyntax outer = Assert.IsType<NamedTypeSyntax>(field.Type);
            NamedTypeSyntax middle = Assert.IsType<NamedTypeSyntax>(outer.TypeArguments.Single());
            NamedTypeSyntax inner = Assert.IsType<NamedTypeSyntax>(middle.TypeArguments.Single());

            Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(inner.TypeArguments.Single()).Path.Single());
        }

        /// <summary>A generic constraint list closes with the same `&gt;&gt;` problem (§6).</summary>
        [Fact]
        public void GenericConstraintsParse()
        {
            MethodDeclarationSyntax method = ParseSingle<MethodDeclarationSyntax>(
                "fun max<T : IComparable<T> & IEquatable<T>>(a: T, b: T): T { return a; }");

            TypeParameterSyntax parameter = method.TypeParameters.Single();
            Assert.Equal("T", parameter.Name);
            Assert.Equal(2, parameter.Constraints.Count);
        }

        // ---- §4 statements -------------------------------------------------------------------

        /// <summary>§4.1: an `else` binds to the nearest unmatched `if`.</summary>
        [Fact]
        public void ElseBindsToTheNearestIf()
        {
            IfStatementSyntax outer = Assert.IsType<IfStatementSyntax>(ParseStatement("if (a) if (b) x(); else y();"));

            Assert.Null(outer.Else);
            Assert.NotNull(Assert.IsType<IfStatementSyntax>(outer.Then).Else);
        }

        /// <summary>§4.1: braces are optional, but a declaration may not be an unbraced body.</summary>
        [Fact]
        public void UnbracedBodyRejectsADeclaration()
        {
            Assert.IsType<ReturnStatementSyntax>(Assert.IsType<IfStatementSyntax>(ParseStatement("if (a) return;")).Then);
            AssertRejected("fun f(): void { if (a) let x = 1; }", SurtrDiagnosticCode.DeclarationAsEmbeddedStatement);
        }

        [Fact]
        public void BothForFormsParse()
        {
            // Both keywords parse — `let` is rejected by the binder rather than by the grammar,
            // since what makes it wrong is the step clause reassigning it (§4.2).
            Assert.IsType<ForStatementSyntax>(ParseStatement("for (var i = 0; i < n; i += 1) { }"));
            Assert.IsType<ForStatementSyntax>(ParseStatement("for (let i = 0; i < n; i += 1) { }"));
            Assert.IsType<ForInStatementSyntax>(ParseStatement("for (item in items) { }"));
            Assert.IsType<ForInStatementSyntax>(ParseStatement("for (i in 0..10) { }"));
            Assert.IsType<ForStatementSyntax>(ParseStatement("for (;;) { }"));
        }

        [Fact]
        public void LabeledLoopAndTargetedBreakParse()
        {
            LabeledStatementSyntax labeled = Assert.IsType<LabeledStatementSyntax>(
                ParseStatement("outer: for (a in b) { break outer; }"));

            Assert.Equal("outer", labeled.Label);

            ForInStatementSyntax loop = Assert.IsType<ForInStatementSyntax>(labeled.Statement);
            BlockStatementSyntax body = Assert.IsType<BlockStatementSyntax>(loop.Body);
            Assert.Equal("outer", Assert.IsType<BreakStatementSyntax>(body.Statements.Single()).Label);
        }

        [Fact]
        public void SwitchStatementGroupsLabelsAndAllowsFallthrough()
        {
            SwitchStatementSyntax statement = Assert.IsType<SwitchStatementSyntax>(
                ParseStatement("switch (x) { case 1: case 2: y(); break; default: z(); }"));

            Assert.Equal(2, statement.Sections.Count);
            Assert.Equal(2, statement.Sections[0].Labels.Count);
            Assert.True(statement.Sections[1].IsDefault);
        }

        [Fact]
        public void SwitchExpressionParsesArmsAndElse()
        {
            SwitchExpressionSyntax expression = Assert.IsType<SwitchExpressionSyntax>(
                ParseExpression("switch (x) { 1 -> \"one\", 2, 3 -> \"more\", else -> \"other\", }"));

            Assert.Equal(3, expression.Arms.Count);
            Assert.Equal(2, expression.Arms[1].Values.Count);
            Assert.True(expression.Arms[2].IsElse);
        }

        [Fact]
        public void TryCatchFinallyParses()
        {
            TryStatementSyntax statement = Assert.IsType<TryStatementSyntax>(
                ParseStatement("try { a(); } catch (e: FooException) { b(); } catch (e: Exception) { c(); } finally { d(); }"));

            Assert.Equal(2, statement.Catches.Count);
            Assert.Equal("e", statement.Catches[0].VariableName);
            Assert.NotNull(statement.Finally);
        }

        [Fact]
        public void TryWithNeitherCatchNorFinallyIsRejected()
        {
            AssertRejected("fun f(): void { try { a(); } }", SurtrDiagnosticCode.IncompleteTryStatement);
        }

        // ---- §7 compile-time evaluation ------------------------------------------------------

        [Fact]
        public void ConstBindingAndConstFunctionParse()
        {
            Assert.True(ParseSingle<FieldDeclarationSyntax>("const MaxEntities: int = 512;").IsConst);
            Assert.True(ParseSingle<MethodDeclarationSyntax>("const fun square(x: int): int { return x * x; }").IsConst);
        }

        [Fact]
        public void ConstIfParsesAsAStatement()
        {
            IfStatementSyntax statement = Assert.IsType<IfStatementSyntax>(ParseStatement("const if (Debug) { a(); } else { b(); }"));

            Assert.True(statement.IsConst);
            Assert.NotNull(statement.Else);
        }

        /// <summary>§7.3: the declaration-level form is the one that replaces `#if`.</summary>
        [Fact]
        public void ConstIfParsesAtDeclarationLevel()
        {
            ConstIfDeclarationSyntax declaration = ParseSingle<ConstIfDeclarationSyntax>(
                "const if (Debug) { fun log(m: string): void { } } else { fun log(m: string): void { } }");

            Assert.Single(declaration.Then);
            Assert.Single(declaration.Else);
        }

        [Fact]
        public void ConstIfChainsThroughElse()
        {
            ConstIfDeclarationSyntax declaration = ParseSingle<ConstIfDeclarationSyntax>(
                "const if (A) { let x: int = 1; } else const if (B) { let y: int = 2; }");

            Assert.IsType<ConstIfDeclarationSyntax>(declaration.Else.Single());
        }

        // ---- §10 native, §11 attributes, §12 doc comments -------------------------------------

        [Fact]
        public void NativeDeclarationsParseWithoutBodies()
        {
            Assert.True(ParseSingle<MethodDeclarationSyntax>("native fun log(message: string): void;").IsNative);

            FieldDeclarationSyntax readOnly = ParseSingle<FieldDeclarationSyntax>("native let ScreenWidth: int;");
            Assert.True(readOnly.IsNative);
            Assert.False(readOnly.IsMutable);

            Assert.True(ParseSingle<FieldDeclarationSyntax>("native var TimeScale: float;").IsMutable);
        }

        [Fact]
        public void AttributesAndDocCommentsAttachToTheDeclarationBelow()
        {
            MethodDeclarationSyntax method = ParseSingle<MethodDeclarationSyntax>(
                "/// Moves it.\n/// @param dx offset\n@Obsolete(\"use moveTo\")\n@Pure\npublic fun move(dx: float): void { }");

            Assert.Equal(2, method.DocComment.Count);
            Assert.Equal("Moves it.", method.DocComment[0]);
            Assert.Equal(2, method.Attributes.Count);
            Assert.Equal("Obsolete", method.Attributes[0].Name);
            Assert.Single(method.Attributes[0].Arguments);
            Assert.Empty(method.Attributes[1].Arguments);
        }

        // ---- §5.2 interpolation ---------------------------------------------------------------

        [Fact]
        public void InterpolatedStringSplitsIntoTextAndExpressions()
        {
            InterpolatedStringExpressionSyntax interpolated = Assert.IsType<InterpolatedStringExpressionSyntax>(
                ParseExpression("\"Hello, $name! You have ${cart.length} items.\""));

            Assert.Equal(5, interpolated.Parts.Count);
            Assert.IsType<LiteralExpressionSyntax>(interpolated.Parts[0]);
            Assert.Equal("name", Assert.IsType<IdentifierExpressionSyntax>(interpolated.Parts[1]).Name);
            Assert.IsType<MemberAccessExpressionSyntax>(interpolated.Parts[3]);
        }

        [Fact]
        public void EscapedDollarStaysLiteralText()
        {
            Assert.IsType<LiteralExpressionSyntax>(ParseExpression("\"cost: \\$5\""));
        }

        // ---- calls ----------------------------------------------------------------------------

        [Fact]
        public void NamedArgumentsParseAfterPositionalOnes()
        {
            CallExpressionSyntax call = Assert.IsType<CallExpressionSyntax>(ParseExpression("spawn(1.0, y: 2.0, hp: 50)"));

            Assert.Null(call.Arguments[0].Name);
            Assert.Equal("y", call.Arguments[1].Name);
            Assert.Equal("hp", call.Arguments[2].Name);
        }

        /// <summary>§5.5: constructing an instance is an ordinary call — there is no `new`.</summary>
        [Fact]
        public void ConstructionIsAnOrdinaryCall()
        {
            Assert.IsType<CallExpressionSyntax>(ParseExpression("Vec2(1.0, 2.0)"));
        }

        [Fact]
        public void TrailingCommasAreAcceptedThroughout()
        {
            Assert.IsType<ArrayLiteralExpressionSyntax>(ParseExpression("[1, 2, 3,]"));
            Assert.IsType<DictLiteralExpressionSyntax>(ParseExpression("{ \"a\": 1, }"));
            Assert.IsType<CallExpressionSyntax>(ParseExpression("f(1, 2,)"));
        }

        // ---- errors ---------------------------------------------------------------------------

        [Theory]
        [InlineData("let x = 1")]
        [InlineData("class Foo {")]
        [InlineData("fun f(): void { let = 1; }")]
        [InlineData("fun f(): void { if a { } }")]
        [InlineData("fun f(): void { let x: Box<int = 1; }")]
        public void MalformedSourceIsReportedAndRecoveredFrom(string source)
        {
            Parser parser = new Parser(SurtrSourceBuffer.FromString(source));
            parser.ParseCompilationUnit();

            Assert.True(parser.Diagnostics.HasErrors);

            SurtrDiagnostic diagnostic = parser.Diagnostics[0];
            Assert.True(diagnostic.Span.Start.Line >= 1);
            Assert.StartsWith("SURTR2", diagnostic.Id);
            Assert.Contains("<memory>", diagnostic.ToString());
        }

        // ---- recovery -------------------------------------------------------------------------

        /// <summary>
        /// The point of recovery: a broken declaration is reported, and the ones after it are still
        /// parsed rather than being hidden behind it.
        /// </summary>
        [Fact]
        public void ABrokenDeclarationDoesNotHideTheOnesAfterIt()
        {
            Parser parser = new Parser(SurtrSourceBuffer.FromString(
                "fun a(): void { }\n" +
                "fun b(: void { }\n" +
                "fun c(): void { }\n"));

            CompilationUnitSyntax unit = parser.ParseCompilationUnit();

            Assert.True(parser.Diagnostics.HasErrors);

            string[] parsed = unit.Declarations
                .OfType<MethodDeclarationSyntax>()
                .Select(method => method.Name)
                .ToArray();

            Assert.Contains("a", parsed);
            Assert.Contains("c", parsed);
        }

        /// <summary>Several independent problems are all reported, not just the first.</summary>
        [Fact]
        public void SeveralBrokenDeclarationsAreAllReported()
        {
            Parser parser = new Parser(SurtrSourceBuffer.FromString(
                "fun a(: void { }\n" +
                "fun b(: void { }\n"));

            parser.ParseCompilationUnit();

            Assert.True(parser.Diagnostics.ErrorCount >= 2);
        }

        /// <summary>
        /// A statement that does not parse costs its statement, not the method around it.
        /// </summary>
        [Fact]
        public void ABrokenStatementDoesNotDiscardItsMethod()
        {
            Parser parser = new Parser(SurtrSourceBuffer.FromString(
                "fun f(): void {\n" +
                "    let a = 1;\n" +
                "    let = 2;\n" +
                "    let c = 3;\n" +
                "}\n"));

            CompilationUnitSyntax unit = parser.ParseCompilationUnit();

            Assert.True(parser.Diagnostics.HasErrors);

            MethodDeclarationSyntax method = Assert.IsType<MethodDeclarationSyntax>(unit.Declarations.Single());
            string[] locals = method.Body!.Statements
                .OfType<LocalDeclarationStatementSyntax>()
                .Select(local => local.Name)
                .ToArray();

            Assert.Contains("a", locals);
            Assert.Contains("c", locals);
        }

        /// <summary>Recovery must terminate, whatever it is handed.</summary>
        [Theory]
        [InlineData("}}}}")]
        [InlineData("class")]
        [InlineData("fun fun fun")]
        [InlineData("{{{{{{")]
        [InlineData(")")]
        public void RecoveryTerminatesOnAnyGarbage(string source)
        {
            Parser parser = new Parser(SurtrSourceBuffer.FromString(source));
            parser.ParseCompilationUnit();

            Assert.True(parser.Diagnostics.HasErrors);
        }

        /// <summary>A file with nothing wrong reports nothing.</summary>
        [Fact]
        public void AValidFileReportsNothing()
        {
            Parser parser = new Parser(SurtrSourceBuffer.FromString("fun f(): int { return 1; }"));
            parser.ParseCompilationUnit();

            Assert.Empty(parser.Diagnostics);
            Assert.False(parser.Diagnostics.HasErrors);
        }

        /// <summary>The lexer and the parser report into the same bag, in source order.</summary>
        [Fact]
        public void LexicalAndSyntacticProblemsShareOneBag()
        {
            Parser parser = new Parser(SurtrSourceBuffer.FromString("fun f(): void { let a = #; }\nfun b(: void { }"));
            parser.ParseCompilationUnit();

            Assert.Contains(parser.Diagnostics, d => d.Id.StartsWith("SURTR1"));
            Assert.Contains(parser.Diagnostics, d => d.Id.StartsWith("SURTR2"));
        }

        // ---- spans ----------------------------------------------------------------------------

        /// <summary>A node covers its whole construct, not just the token it started at.</summary>
        [Fact]
        public void ANodeSpansItsWholeConstruct()
        {
            const string source = "fun f(): int { return 1; }";

            CompilationUnitSyntax unit = new Parser(SurtrSourceBuffer.FromString(source)).ParseCompilationUnit();
            MethodDeclarationSyntax method = Assert.IsType<MethodDeclarationSyntax>(unit.Declarations.Single());

            Assert.Equal(0, method.Span.Start.Position);
            Assert.Equal(source.Length, method.Span.End);
        }

        /// <summary>A binary expression spans both operands, so an error about it underlines both.</summary>
        [Fact]
        public void ABinaryExpressionSpansBothOperands()
        {
            BinaryExpressionSyntax sum = Assert.IsType<BinaryExpressionSyntax>(ParseExpression("alpha + beta"));

            Assert.Equal("alpha".Length + " + ".Length + "beta".Length, sum.Span.Length);
            Assert.Equal(sum.Left.Span.Start.Position, sum.Span.Start.Position);
            Assert.Equal(sum.Right.Span.End, sum.Span.End);
        }

        /// <summary>A suffixed type spans the type it wraps plus the suffix.</summary>
        [Fact]
        public void ASuffixedTypeSpansTheWholeType()
        {
            MethodDeclarationSyntax method = ParseSingle<MethodDeclarationSyntax>("fun f(): int[] { }");
            ArrayTypeSyntax array = Assert.IsType<ArrayTypeSyntax>(method.ReturnType);

            Assert.Equal(array.ElementType.Span.Start.Position, array.Span.Start.Position);
            Assert.Equal("int[]".Length, array.Span.Length);
        }

        /// <summary>
        /// An expression written around a left operand starts at that operand, not at the operator
        /// that introduced it.
        /// </summary>
        /// <remarks>
        /// Each of these parses its left side before it knows which node it is building, so it is
        /// easy to capture the start position one token too late and span only the operator's own
        /// half — <c>.value</c> for an access, <c>= v</c> for an assignment. That leaves a node whose
        /// span does not cover its own child, which reads as a missing feature rather than a wrong
        /// underline: anything walking the tree by position prunes the subtree the cursor is in.
        /// </remarks>
        [Theory]
        [InlineData("target.member")]
        [InlineData("target?.member")]
        [InlineData("target++")]
        [InlineData("target--")]
        [InlineData("target!!")]
        [InlineData("target is int")]
        [InlineData("target as int")]
        [InlineData("target as? int")]
        [InlineData("target ? 1 : 2")]
        public void AnExpressionSpansTheOperandItWasBuiltAround(string expression)
        {
            ExpressionSyntax parsed = ParseExpression(expression);

            // The operand is written first, so the node has to start where the whole expression does.
            Assert.Equal(0, parsed.Span.Start.Position - LeadingOffset(expression));
            Assert.Equal(expression.Length, parsed.Span.Length);
        }

        /// <summary>An assignment spans its target, which is written before the <c>=</c>.</summary>
        [Fact]
        public void AnAssignmentSpansItsTarget()
        {
            var statement = Assert.IsType<ExpressionStatementSyntax>(ParseStatement("target.member = 1;"));
            AssignmentExpressionSyntax assignment = Assert.IsType<AssignmentExpressionSyntax>(statement.Expression);

            Assert.Equal(assignment.Target.Span.Start.Position, assignment.Span.Start.Position);
            Assert.Equal(assignment.Value.Span.End, assignment.Span.End);
        }

        /// <summary>Where the expression under test begins inside the wrapper it is parsed in.</summary>
        private static int LeadingOffset(string expression)
            => $"fun f(): void {{ let x = ".Length;
    }
}
