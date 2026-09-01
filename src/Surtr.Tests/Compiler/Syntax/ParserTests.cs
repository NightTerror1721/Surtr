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

        /// <summary>Parses a source and asserts it produced no problems at all.</summary>
        private static CompilationUnitSyntax ParseWithoutErrors(string source)
        {
            Parser parser = new Parser(SurtrSourceBuffer.FromString(source));
            CompilationUnitSyntax unit = parser.ParseCompilationUnit();
            Assert.Empty(parser.Diagnostics);
            return unit;
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

        /// <summary>§2.4: a case may carry an explicit value after its arguments: `Hearts(1) = 5,`.</summary>
        [Fact]
        public void EnumParsesExplicitValuesAfterArguments()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "enum Suit { Hearts(\"h\") = 1, Spades, Clubs = 10; }");

            Assert.Equal(3, type.EnumCases.Count);
            Assert.Equal(1, type.EnumCases[0].ExplicitValue);
            Assert.Null(type.EnumCases[1].ExplicitValue);
            Assert.Equal(10, type.EnumCases[2].ExplicitValue);
            Assert.Equal("Spades", type.EnumCases[1].Name);
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

        /// <summary>§6: <c>out</c>/<c>in</c> annotate a parameter's direction, right where it is declared.</summary>
        [Fact]
        public void VarianceAnnotationsParse()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "interface Source<out T, in U, V> { }");

            Assert.Equal(VarianceModifier.Covariant, type.TypeParameters[0].Variance);
            Assert.Equal(VarianceModifier.Contravariant, type.TypeParameters[1].Variance);
            Assert.Equal(VarianceModifier.None, type.TypeParameters[2].Variance);
        }

        /// <summary><c>out</c> is contextual: a parameter genuinely named that still parses.</summary>
        [Fact]
        public void OutRemainsUsableAsAParameterName()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "class Box<out> { }");

            Assert.Equal("out", type.TypeParameters.Single().Name);
            Assert.Equal(VarianceModifier.None, type.TypeParameters.Single().Variance);
        }

        [Fact]
        public void ClosureAliasParsesAsClosureType()
        {
            AliasDeclarationSyntax alias = ParseSingle<AliasDeclarationSyntax>("alias Handler = (Entity, float) -> void;");

            ClosureTypeSyntax closure = Assert.IsType<ClosureTypeSyntax>(alias.Target);
            Assert.Equal(2, closure.ParameterTypes.Count);
        }

        /// <summary>§5.3: a tuple element name is written <c>name: type</c>, the dict's colon, not the C# spelling.</summary>
        [Fact]
        public void ATupleElementNameIsWrittenNameColonType()
        {
            AliasDeclarationSyntax alias = ParseSingle<AliasDeclarationSyntax>("alias Pair = (x: int, y: string);");

            TupleTypeSyntax tuple = Assert.IsType<TupleTypeSyntax>(alias.Target);
            Assert.Equal(new string?[] { "x", "y" }, tuple.ElementNames);
            Assert.Equal(2, tuple.ElementTypes.Count);
        }

        [Fact]
        public void ATupleElementWithoutANameStaysNull()
        {
            AliasDeclarationSyntax alias = ParseSingle<AliasDeclarationSyntax>("alias Pair = (x: int, string);");

            TupleTypeSyntax tuple = Assert.IsType<TupleTypeSyntax>(alias.Target);
            Assert.Equal(new string?[] { "x", null }, tuple.ElementNames);
        }

        /// <summary>A closure's parameters are positional; the arrow form has nowhere for a name to land.</summary>
        [Fact]
        public void ANamedClosureParameterIsRefused()
        {
            AssertRejected("alias F = (x: int) -> int;", SurtrDiagnosticCode.UnexpectedToken);
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

        /// <summary>§3.3: a constructor may take an arrow body — one expression statement of sugar.</summary>
        [Fact]
        public void ConstructorTakesAnArrowBody()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "class Dog { public let n: string; public constructor(n: string) => init(n); }");

            ConstructorDeclarationSyntax constructor = Assert
                .IsType<ConstructorDeclarationSyntax>(type.Members.OfType<ConstructorDeclarationSyntax>().Single());

            ExpressionStatementSyntax single = Assert.IsType<ExpressionStatementSyntax>(constructor.Body.Statements.Single());
            Assert.NotNull(single.Expression);
        }

        /// <summary>A chained constructor may take the arrow form too — the chain is independent of the body's shape.</summary>
        [Fact]
        public void AChainedConstructorTakesAnArrowBody()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "class Dog : Animal { public constructor(n: string) : super(n) => wake(); }");

            ConstructorDeclarationSyntax constructor = Assert.IsType<ConstructorDeclarationSyntax>(type.Members.Single());
            Assert.False(constructor.ChainsToThis);
            Assert.IsType<ExpressionStatementSyntax>(constructor.Body.Statements.Single());
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

        /// <summary>
        /// §3.3: an operator may take an arrow body, exactly as a method may — sugar for a block
        /// holding one <c>return</c>.
        /// </summary>
        [Theory]
        [InlineData("operator+(a: V, b: V): V => a;")]
        [InlineData("operator-(v: V): V => v;")]
        [InlineData("operator==(a: V, b: V): bool => true;")]
        [InlineData("operator[](i: int): float => 0.0;")]
        [InlineData("operator as Vec3(v: V) => Vec3();")]
        public void OperatorOverloadsTakeArrowBodies(string source)
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>($"class V {{ {source} }}");
            OperatorDeclarationSyntax declared = Assert.IsType<OperatorDeclarationSyntax>(type.Members.Single());

            BlockStatementSyntax body = declared.Body!;
            ReturnStatementSyntax single = Assert.IsType<ReturnStatementSyntax>(body.Statements.Single());
            Assert.NotNull(single.Value);
        }

        /// <summary>An abstract operator still ends at the semicolon; the arrow is never read as one.</summary>
        [Fact]
        public void AnAbstractOperatorHasNoBody()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>("interface I { operator+(a: I, b: I): I; }");
            OperatorDeclarationSyntax declared = Assert.IsType<OperatorDeclarationSyntax>(type.Members.Single());

            Assert.Null(declared.Body);
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

        /// <summary>§3.6: a constructor is never spliced, so `inline`/`forceinline` on one is rejected rather than silently ignored.</summary>
        [Fact]
        public void AConstructorRejectsInlineModifiers()
        {
            AssertRejected("class V { public inline constructor() { } }", SurtrDiagnosticCode.InvalidModifier);
            AssertRejected("class V { public forceinline constructor() { } }", SurtrDiagnosticCode.InvalidModifier);
            AssertRejected("class V { public noinline constructor() { } }", SurtrDiagnosticCode.InvalidModifier);
        }

        /// <summary>§3.6: `noinline` parses in the inline slot and reaches the declaration.</summary>
        [Fact]
        public void ANoinlineModifierParses()
        {
            MethodDeclarationSyntax method = ParseSingle<MethodDeclarationSyntax>(
                "noinline fun compute(x: int): int { return x * x + 7 * 3; }");

            Assert.Equal(InlineModifier.NoInline, method.Inline);
        }

        /// <summary>The three inline-family keywords are mutually exclusive.</summary>
        [Theory]
        [InlineData("inline noinline")]
        [InlineData("noinline inline")]
        [InlineData("forceinline noinline")]
        [InlineData("noinline forceinline")]
        public void NoinlineIsExclusiveWithTheOtherInlineModifiers(string pair)
        {
            AssertRejected($"class V {{ {pair} fun m(): int {{ return 1; }} }}", SurtrDiagnosticCode.InvalidModifier);
        }

        /// <summary>§3.2's order puts the inline family before `const`.</summary>
        [Fact]
        public void NoinlineIsWrittenBeforeConst()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "class V { noinline const fun k(): int { return 1; } }");

            Assert.Equal(InlineModifier.NoInline,
                Assert.IsType<MethodDeclarationSyntax>(type.Members.Single()).Inline);

            AssertRejected("class V { const noinline fun k(): int { return 1; } }", SurtrDiagnosticCode.InvalidModifier);
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

        /// <summary>§8: a lambda may write its return type, `(params): Ret => body`, reading like the `fun` it is.</summary>
        [Fact]
        public void LambdaReturnTypesMayBeWritten()
        {
            LambdaExpressionSyntax annotated = Assert.IsType<LambdaExpressionSyntax>(ParseExpression("(x: int): int => x * 2"));
            Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(annotated.ReturnType!).Path.Single());
            Assert.NotNull(annotated.Parameters.Single().Type);

            // The annotation works with an unwritten parameter type too.
            LambdaExpressionSyntax inferred = Assert.IsType<LambdaExpressionSyntax>(ParseExpression("(x): int => x * 2"));
            Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(inferred.ReturnType!).Path.Single());
            Assert.Null(inferred.Parameters.Single().Type);

            // ... and on a block-bodied lambda.
            LambdaExpressionSyntax block = Assert.IsType<LambdaExpressionSyntax>(ParseExpression("(x: int): int => { return x; }"));
            Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(block.ReturnType!).Path.Single());
            Assert.NotNull(block.BlockBody);
            Assert.Null(block.Body);
        }

        /// <summary>
        /// The `:` opens a return type, so the lookahead has to cross it — and whatever nesting
        /// it opens, like a closure return type — before the `=>` that ends the lambda.
        /// </summary>
        [Fact]
        public void AWrittenReturnTypeStillEndsAtTheFatArrow()
        {
            Assert.IsType<LambdaExpressionSyntax>(ParseExpression("(a, b): int => a + b"));
            Assert.IsType<LambdaExpressionSyntax>(ParseExpression("(): (int) -> int => x"));
            Assert.IsType<LambdaExpressionSyntax>(ParseExpression("(): {int: bool} => x"));
            Assert.IsType<LambdaExpressionSyntax>(ParseExpression("(): Box<Box<int>> => x"));
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

        /// <summary>
        /// §6: a static member of a generic class is reached through a construction, so
        /// <c>Box&lt;int&gt;.prop</c> parses as a member access whose target is a generic name.
        /// </summary>
        [Fact]
        public void AGenericNameWithArgumentsParsesAsAStaticMemberAccess()
        {
            MemberAccessExpressionSyntax access = Assert.IsType<MemberAccessExpressionSyntax>(
                ParseExpression("Box<int>.prop"));

            GenericNameExpressionSyntax target = Assert.IsType<GenericNameExpressionSyntax>(access.Target);
            Assert.Equal("Box", target.Name);
            Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(target.TypeArguments.Single()).Path.Single());
            Assert.Equal("prop", access.Name);
            Assert.False(access.IsNullConditional);
        }

        /// <summary>§6: the open form writes one wildcard per slot — <c>Box&lt;&gt;.prop</c>, <c>Box&lt;,&gt;.make()</c>.</summary>
        [Fact]
        public void AnOpenGenericNameWithEmptySlotsParsesAsWildcards()
        {
            MemberAccessExpressionSyntax access = Assert.IsType<MemberAccessExpressionSyntax>(
                ParseExpression("Box<,>.prop"));

            GenericNameExpressionSyntax target = Assert.IsType<GenericNameExpressionSyntax>(access.Target);
            Assert.Equal(2, target.TypeArguments.Count);
            Assert.All(target.TypeArguments, t => Assert.IsType<WildcardTypeSyntax>(t));
        }

        /// <summary>§6: a single empty slot is one wildcard — the arity 1 open form.</summary>
        [Fact]
        public void AGenericCallStillParsesWhenTheOpenFormWasAllowed()
        {
            CallExpressionSyntax call = Assert.IsType<CallExpressionSyntax>(
                ParseExpression("Box<int>(5)"));

            Assert.IsType<IdentifierExpressionSyntax>(call.Callee);
            Assert.Equal("int", Assert.IsType<NamedTypeSyntax>(call.TypeArguments.Single()).Path.Single());
        }

        /// <summary>§6: a generic method call on a static generic owner is a call whose callee is a member access.</summary>
        [Fact]
        public void AGenericStaticMethodCallParsesWithTheGenericNameAsReceiver()
        {
            CallExpressionSyntax call = Assert.IsType<CallExpressionSyntax>(
                ParseExpression("Box<int>.make(7)"));

            MemberAccessExpressionSyntax callee = Assert.IsType<MemberAccessExpressionSyntax>(call.Callee);
            Assert.IsType<GenericNameExpressionSyntax>(callee.Target);
            Assert.Equal("make", callee.Name);
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

        // ---- Fase 1 (docs/Plan-Roadmap-Novedades.md, propuesta 5): type patterns in switch -----

        [Fact]
        public void SwitchStatementParsesATypePatternLabel()
        {
            SwitchStatementSyntax statement = Assert.IsType<SwitchStatementSyntax>(
                ParseStatement("switch (shape) { case c is Circle: big(c); break; default: other(); }"));

            Assert.Single(statement.Sections[0].Labels);
            var test = Assert.IsType<TypeTestExpressionSyntax>(statement.Sections[0].Labels[0]);
            Assert.IsType<IdentifierExpressionSyntax>(test.Operand);
            Assert.Null(statement.Sections[0].Guards[0]);
        }

        [Fact]
        public void SwitchStatementParsesATypePatternLabelWithAGuard()
        {
            SwitchStatementSyntax statement = Assert.IsType<SwitchStatementSyntax>(
                ParseStatement("switch (shape) { case c is Circle if c.radius > 10.0: big(c); break; }"));

            Assert.NotNull(statement.Sections[0].Guards[0]);
        }

        [Fact]
        public void SwitchExpressionParsesATypePatternArmWithAGuard()
        {
            SwitchExpressionSyntax expression = Assert.IsType<SwitchExpressionSyntax>(
                ParseExpression("switch (shape) { c is Circle if c.radius > 10.0 -> \"big\", else -> \"other\", }"));

            var test = Assert.IsType<TypeTestExpressionSyntax>(Assert.Single(expression.Arms[0].Values));
            Assert.IsType<IdentifierExpressionSyntax>(test.Operand);
            Assert.NotNull(expression.Arms[0].Guard);
            Assert.Null(expression.Arms[1].Guard);
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

        // ---- §1 optional semicolons -----------------------------------------------------------

        /// <summary>§1: a line break terminates a statement, so a whole body can be `;`-less.</summary>
        [Fact]
        public void StatementsParseWithoutTrailingSemicolons()
        {
            CompilationUnitSyntax unit = ParseWithoutErrors(
                "fun f(): void {\n" +
                "    let a = 1\n" +
                "    let b = a + 1\n" +
                "    foo(a, b)\n" +
                "    return\n" +
                "}\n");

            MethodDeclarationSyntax method = Assert.IsType<MethodDeclarationSyntax>(unit.Declarations.Single());
            Assert.Equal(4, method.Body!.Statements.Count);
            Assert.IsType<LocalDeclarationStatementSyntax>(method.Body.Statements[0]);
            Assert.IsType<LocalDeclarationStatementSyntax>(method.Body.Statements[1]);
            Assert.IsType<ExpressionStatementSyntax>(method.Body.Statements[2]);
            Assert.IsType<ReturnStatementSyntax>(method.Body.Statements[3]);
        }

        /// <summary>§1: the same rule covers imports and module-level declarations.</summary>
        [Fact]
        public void DeclarationsParseWithoutTrailingSemicolons()
        {
            CompilationUnitSyntax unit = ParseWithoutErrors(
                "import Ogame.core.Entity\n" +
                "alias Handler = (Entity, float) -> void\n" +
                "let screenWidth: int = 640\n" +
                "fun run(): int { return screenWidth; }\n");

            Assert.Equal(3, unit.Declarations.Count);
            Assert.IsType<AliasDeclarationSyntax>(unit.Declarations[0]);
            Assert.IsType<FieldDeclarationSyntax>(unit.Declarations[1]);
            Assert.IsType<MethodDeclarationSyntax>(unit.Declarations[2]);
        }

        /// <summary>§1: a `;` stays legal, and two statements on one line still need one.</summary>
        [Theory]
        [InlineData("fun f(): void { let a = 1 let b = 2 }")]
        [InlineData("fun f(): void { foo() bar() }")]
        [InlineData("let a = 1 let b = 2")]
        [InlineData("let a = 1 fun b(): void { }")]
        public void TwoStatementsOnOneLineStillNeedASemicolon(string source)
        {
            AssertRejected(source, SurtrDiagnosticCode.UnexpectedToken);
        }

        /// <summary>§1: a bare `return` never reaches for an operand on the next line.</summary>
        [Fact]
        public void AReturnDoesNotReachForAnOperandOnTheNextLine()
        {
            MethodDeclarationSyntax method = ParseSingle<MethodDeclarationSyntax>(
                "fun f(): void {\n    return\n    foo()\n}");

            Assert.IsType<ReturnStatementSyntax>(method.Body!.Statements[0]);
            Assert.IsType<ExpressionStatementSyntax>(method.Body.Statements[1]);
        }

        /// <summary>§1: a `break` on its own line does not swallow the next line as a label.</summary>
        [Fact]
        public void ABreakDoesNotSwallowTheNextLineAsItsLabel()
        {
            MethodDeclarationSyntax method = ParseSingle<MethodDeclarationSyntax>(
                "fun f(): void { while (true) { break\n    foo() } }");

            WhileStatementSyntax loop = Assert.IsType<WhileStatementSyntax>(method.Body!.Statements.Single());
            BlockStatementSyntax body = Assert.IsType<BlockStatementSyntax>(loop.Body);
            Assert.IsType<BreakStatementSyntax>(body.Statements[0]);
            Assert.IsType<ExpressionStatementSyntax>(body.Statements[1]);
        }

        /// <summary>§1: an operator at the start of a line continues the statement above it, as in TypeScript.</summary>
        [Fact]
        public void AnOperatorOnTheNextLineContinuesTheExpression()
        {
            MethodDeclarationSyntax method = ParseSingle<MethodDeclarationSyntax>(
                "fun f(): int {\n    let x = 1 +\n        2\n    return x;\n}");

            LocalDeclarationStatementSyntax local = Assert.IsType<LocalDeclarationStatementSyntax>(method.Body!.Statements[0]);
            BinaryExpressionSyntax sum = Assert.IsType<BinaryExpressionSyntax>(local.Initializer);
            Assert.Equal(BinaryOperator.Add, sum.Operator);
            Assert.Equal(2, Assert.IsType<LiteralExpressionSyntax>(sum.Right).Literal.Payload.AsInteger);
        }

        /// <summary>§1: a `{` on the next line still opens the body — a line break is not a signature end.</summary>
        [Fact]
        public void ABraceOnTheNextLineStillOpensAMethodBody()
        {
            MethodDeclarationSyntax method = ParseSingle<MethodDeclarationSyntax>(
                "fun f(): int\n{\n    return 1\n}\n");

            Assert.NotNull(method.Body);
        }

        /// <summary>§1: a signature-only member ends at a line break, exactly as at a `;`.</summary>
        [Fact]
        public void SignatureOnlyMembersEndAtALineBreak()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "interface IBar {\n    fun doThing(x: int): void\n    fun other(): int\n}");

            Assert.All(type.Members.Cast<MethodDeclarationSyntax>(), method => Assert.Null(method.Body));
        }

        /// <summary>§1: auto-accessors end at a line break inside their braces.</summary>
        [Fact]
        public void AutoAccessorsEndAtALineBreak()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "class Foo {\n    public name: string {\n        get\n        set\n    }\n}");

            PropertyDeclarationSyntax property = Assert.IsType<PropertyDeclarationSyntax>(type.Members.Single());
            Assert.Equal(2, property.Accessors.Count);
            Assert.All(property.Accessors, accessor => Assert.Null(accessor.Body));
        }

        /// <summary>§1: arrow-bodied members end at a line break too.</summary>
        [Fact]
        public void ArrowBodiedMembersNeedNoSemicolon()
        {
            TypeDeclarationSyntax type = ParseSingle<TypeDeclarationSyntax>(
                "class V {\n    operator+(a: V, b: V): V => a\n    fun f(): int => 1\n}");

            Assert.IsType<OperatorDeclarationSyntax>(type.Members[0]);
            Assert.NotNull(Assert.IsType<MethodDeclarationSyntax>(type.Members[1]).Body);
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
        [InlineData("let x = 1 let y = 2")]
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

        /// <summary>
        /// Regression for B12 (docs/Plan-Revision-Stdlib.md §6.3d): <c>generator&lt;T&gt;</c> as a
        /// bare type annotation already parsed, but nested inside another type's own
        /// <c>&lt;...&gt;</c> - <c>array&lt;generator&lt;float&gt;&gt;(n)</c>,
        /// <c>List&lt;generator&lt;float&gt;&gt;()</c> - failed with <c>SURTR2003: Expected an
        /// expression, found KeywordGenerator</c>. Root cause: a generic call in expression
        /// position is disambiguated from a chain of <c>&lt;</c>/<c>&gt;</c> comparisons by a
        /// lookahead scan (<c>Parser.LooksLikeTypeArgumentList</c>/
        /// <c>Parser.LooksLikeGenericTypeOnlyAhead</c>, the latter used by <c>typeof</c>),
        /// and neither scan's allow-list of "tokens a type can be written with" included
        /// <c>KeywordGenerator</c> - the one type name that also lexes as its own keyword (§1.2) -
        /// even though the real type parser (<c>ParseCoreType</c>) already accepted it anywhere
        /// else. Hitting <c>generator</c> mid-scan made the lookahead give up and conclude the
        /// <c>&lt;</c> was a comparison, so the parser then tried to read <c>generator</c> as an
        /// expression operand and failed.
        /// </summary>
        [Fact]
        public void GeneratorTypeArgumentNestsInsideAnotherGenericTypeArgumentList()
        {
            ParseWithoutErrors("fun f(): void { let x = array<generator<float>>(4); }");
            ParseWithoutErrors("fun f(): void { let x = List<generator<float>>(); }");
            ParseWithoutErrors("fun f(): generator<float>[] { let x: generator<float>[] = array<generator<float>>(4); return x; }");

            // The same allow-list backs `typeof`'s own lookahead - not part of the reported bug,
            // but the identical mechanism, so pinned here too.
            ParseWithoutErrors("fun f(): bool { return typeof(int) == typeof(float); }");
        }
    }
}
