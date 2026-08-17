#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// Covers the binder's third phase: names in a body resolved, types checked, and every
    /// conversion made explicit in the tree so code generation never has to work one out.
    /// </summary>
    public sealed class BodyBinderTests
    {
        private const string Root = "D:/proj/src";

        private static Binder Bind(out SurtrCompilation compilation, string source)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();
            return binder;
        }

        /// <summary>Wraps statements in a method, so a test can be about the body alone.</summary>
        private static Binder BindIn(out SurtrCompilation compilation, string statements, string extra = "")
            => Bind(out compilation, extra + "\nclass Test {\n  public fun run(): void {\n" + statements + "\n  }\n}");

        private static BoundBlockStatement Body(Binder binder, string name = "run")
            => (BoundBlockStatement)binder.Bodies.Single(b => b.Key.Name == name).Value;

        private static IEnumerable<BoundNode> Walk(BoundNode node)
        {
            yield return node;

            foreach (var child in Children(node))
            {
                foreach (var descendant in Walk(child))
                    yield return descendant;
            }
        }

        private static IEnumerable<BoundNode> Children(BoundNode node)
        {
            switch (node)
            {
                case BoundBlockStatement block: return block.Statements;
                case BoundExpressionStatement statement: return new BoundNode[] { statement.Expression };
                case BoundLocalDeclarationStatement local:
                    return local.Initializer is null ? System.Array.Empty<BoundNode>() : new BoundNode[] { local.Initializer };
                case BoundIfStatement @if:
                    return @if.Else is null
                        ? new BoundNode[] { @if.Condition, @if.Then }
                        : new BoundNode[] { @if.Condition, @if.Then, @if.Else };
                case BoundWhileStatement @while: return new BoundNode[] { @while.Condition, @while.Body };
                case BoundForInStatement forIn: return new BoundNode[] { forIn.Sequence, forIn.Body };
                case BoundForStatement @for: return new BoundNode[] { @for.Body };
                case BoundTryStatement @try:
                    return @try.Catches.Select(c => c.Body).Prepend<BoundNode>(@try.Body);
                case BoundLabeledStatement labeled: return new BoundNode[] { labeled.Statement };
                case BoundSwitchStatement @switch:
                    return @switch.Sections.SelectMany(s => s.Statements).Prepend<BoundNode>(@switch.Subject);
                case BoundReturnStatement @return:
                    return @return.Value is null ? System.Array.Empty<BoundNode>() : new BoundNode[] { @return.Value };
                case BoundBinaryExpression binary: return new BoundNode[] { binary.Left, binary.Right };
                case BoundUnaryExpression unary: return new BoundNode[] { unary.Operand };
                case BoundAssignmentExpression assignment: return new BoundNode[] { assignment.Target, assignment.Value };
                case BoundConversionExpression conversion: return new BoundNode[] { conversion.Operand };
                case BoundCallExpression call:
                    return call.Receiver is null ? call.Arguments : call.Arguments.Prepend<BoundNode>(call.Receiver);
                case BoundObjectCreationExpression creation: return creation.Arguments;
                case BoundIndexExpression index: return new BoundNode[] { index.Target, index.Index };
                case BoundConditionalExpression conditional:
                    return new BoundNode[] { conditional.Condition, conditional.WhenTrue, conditional.WhenFalse };
                case BoundArrayLiteralExpression array: return array.Elements;
                case BoundClosureInvocationExpression invocation: return invocation.Arguments.Prepend<BoundNode>(invocation.Callee);
                default: return System.Array.Empty<BoundNode>();
            }
        }

        private static T First<T>(Binder binder, string name = "run") where T : BoundNode
            => Walk(Body(binder, name)).OfType<T>().First();

        private static void AssertNoErrors(SurtrCompilation compilation)
            => Assert.True(
                !compilation.HasErrors,
                "Unexpected: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

        private static void AssertReports(SurtrCompilation compilation, SurtrDiagnosticCode code)
            => Assert.True(
                compilation.Diagnostics.Any(d => d.Code == code),
                $"Expected {code}, got: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

        #region Locals
        [Fact]
        public void ALocalTakesTheTypeOfItsInitializer()
        {
            var binder = BindIn(out var compilation, "let a = 1;\nlet b = 1.5;\nlet c = \"x\";\nlet d = true;");

            AssertNoErrors(compilation);

            var locals = Walk(Body(binder)).OfType<BoundLocalDeclarationStatement>().ToList();

            Assert.Same(compilation.TypeFactory.Int, locals[0].Local.Type);
            Assert.Same(compilation.TypeFactory.Float, locals[1].Local.Type);
            Assert.Same(compilation.TypeFactory.String, locals[2].Local.Type);
            Assert.Same(compilation.TypeFactory.Bool, locals[3].Local.Type);
        }

        [Fact]
        public void AWrittenTypeAlsoTypesTheInitializer()
        {
            // Inference is one-way, which is what lets an empty literal know what it is.
            var binder = BindIn(out var compilation, "let xs: int[] = [];");

            AssertNoErrors(compilation);
            Assert.Same(
                compilation.TypeFactory.Array(compilation.TypeFactory.Int),
                Walk(Body(binder)).OfType<BoundLocalDeclarationStatement>().Single().Local.Type);
        }

        [Fact]
        public void AnEmptyLiteralWithNothingToInferFromIsReported()
        {
            BindIn(out var compilation, "let xs = [];");
            AssertReports(compilation, SurtrDiagnosticCode.CannotInferType);
        }

        [Fact]
        public void ALocalWithNeitherTypeNorInitializerIsReported()
        {
            BindIn(out var compilation, "var a;");
            AssertReports(compilation, SurtrDiagnosticCode.CannotInferType);
        }

        [Fact]
        public void ALetCannotBeAssignedTo()
        {
            BindIn(out var compilation, "let a = 1;\na = 2;");
            AssertReports(compilation, SurtrDiagnosticCode.NotAssignable);
        }

        [Fact]
        public void AVarCan()
        {
            BindIn(out var compilation, "var a = 1;\na = 2;");
            AssertNoErrors(compilation);
        }

        [Fact]
        public void TwoLocalsOfOneNameInOneBlockCollide()
        {
            BindIn(out var compilation, "let a = 1;\nlet a = 2;");
            AssertReports(compilation, SurtrDiagnosticCode.DuplicateDeclaration);
        }

        /// <summary>§4.4: two locals of one name carry no information, wherever the inner one sits.</summary>
        [Fact]
        public void ANestedBlockMayNotShadow()
        {
            BindIn(out var compilation, "let a = 1;\n{ let a = \"x\"; }");
            AssertReports(compilation, SurtrDiagnosticCode.DuplicateDeclaration);
        }

        /// <summary>A lambda's parameter is a name in the same body, so §4.4 reaches it too.</summary>
        [Fact]
        public void ALambdaParameterMayNotShadowALocal()
        {
            BindIn(out var compilation, "let n = 1;\nlet f: (int) -> int = (n: int) => n;");
            AssertReports(compilation, SurtrDiagnosticCode.DuplicateDeclaration);
        }

        /// <summary>§4.4's exception: a field is not in the value scope chain at all.</summary>
        [Fact]
        public void AParameterMayShadowAField()
        {
            Bind(
                out var compilation,
                "class Vec {\n"
                    + "    public let x: float;\n"
                    + "    public constructor(x: float) { this.x = x; }\n"
                    + "}");

            AssertNoErrors(compilation);
        }

        [Fact]
        public void ALocalGoesOutOfScopeWithItsBlock()
        {
            BindIn(out var compilation, "{ let a = 1; }\nlet b = a;");
            AssertReports(compilation, SurtrDiagnosticCode.UnresolvedName);
        }
        #endregion

        #region Operators
        [Fact]
        public void IntArithmeticStaysInt()
        {
            var binder = BindIn(out var compilation, "let a = 7 / 2;");

            AssertNoErrors(compilation);
            Assert.Same(compilation.TypeFactory.Int, First<BoundBinaryExpression>(binder).Type);
        }

        [Fact]
        public void MixingAFloatPromotesTheWholeExpression()
        {
            // §5.7: `7 / 2` truncates and `7 / 2.0` does not.
            var binder = BindIn(out var compilation, "let a = 7 / 2.0;");

            AssertNoErrors(compilation);

            var binary = First<BoundBinaryExpression>(binder);
            Assert.Same(compilation.TypeFactory.Float, binary.Type);

            // The widening is in the tree, so nothing downstream has to rediscover it.
            Assert.Same(compilation.TypeFactory.Float, binary.Left.Type);
        }

        [Fact]
        public void AnIntLiteralAgainstAFloatContextIsAlreadyAFloat()
        {
            var binder = BindIn(out var compilation, "let a: float = 1;");

            AssertNoErrors(compilation);
            Assert.Same(
                compilation.TypeFactory.Float,
                Walk(Body(binder)).OfType<BoundLiteralExpression>().First().Type);
        }

        [Fact]
        public void ComparisonsAreBool()
        {
            var binder = BindIn(out var compilation, "let a = 1 < 2;\nlet b = 1 == 2;\nlet c = 1 <=> 2;");

            AssertNoErrors(compilation);

            var binaries = Walk(Body(binder)).OfType<BoundBinaryExpression>().ToList();
            Assert.Same(compilation.TypeFactory.Bool, binaries[0].Type);
            Assert.Same(compilation.TypeFactory.Bool, binaries[1].Type);
            Assert.Same(compilation.TypeFactory.Int, binaries[2].Type);
        }

        [Fact]
        public void StringConcatenationIsAString()
        {
            var binder = BindIn(out var compilation, "let a = \"x\" + 1;");

            AssertNoErrors(compilation);
            Assert.Same(compilation.TypeFactory.String, First<BoundBinaryExpression>(binder).Type);
        }

        [Fact]
        public void AnOperatorWithNoMeaningForItsOperandsIsReported()
        {
            BindIn(out var compilation, "let a = true - false;");
            AssertReports(compilation, SurtrDiagnosticCode.OperatorNotDefined);
        }

        [Fact]
        public void LogicalOperatorsNeedBooleans()
        {
            BindIn(out var compilation, "let a = 1 && 2;");
            AssertReports(compilation, SurtrDiagnosticCode.OperatorNotDefined);
        }

        [Fact]
        public void ACompoundAssignmentExpandsToTheOperationItNames()
        {
            var binder = BindIn(out var compilation, "var a = 1;\na += 2;");

            AssertNoErrors(compilation);

            var assignment = First<BoundAssignmentExpression>(binder);
            var binary = Assert.IsType<BoundBinaryExpression>(assignment.Value);

            Assert.Equal(Surtr.Compiler.Syntax.Ast.BinaryOperator.Add, binary.Operator);
        }

        [Fact]
        public void AUserDefinedOperatorIsFound()
        {
            var binder = Bind(out var compilation,
                "class Vec2 {\n"
                + "  operator+(a: Vec2, b: Vec2): Vec2 { return a; }\n"
                + "}\n"
                + "class Test { public fun run(): void { let p = Vec2(); let q = p + p; } }");

            AssertNoErrors(compilation);
            Assert.Equal("op_+", First<BoundCallExpression>(binder).Method.Name);
        }
        #endregion

        #region Members
        [Fact]
        public void AFieldIsReachedThroughAnImplicitThis()
        {
            var binder = Bind(out var compilation,
                "class Entity {\n"
                + "  private var _hp: int = 0;\n"
                + "  public fun run(): void { let a = _hp; }\n"
                + "}");

            AssertNoErrors(compilation);

            var field = First<BoundFieldExpression>(binder);
            Assert.Equal("_hp", field.Field.Name);
            Assert.IsType<BoundThisExpression>(field.Receiver);
        }

        [Fact]
        public void AStaticMemberIsReachedWithoutAnInstance()
        {
            var binder = Bind(out var compilation,
                "class Config { public static var Limit: int = 4; }\n"
                + "class Test { public fun run(): void { let a = Config.Limit; } }");

            AssertNoErrors(compilation);

            var field = First<BoundFieldExpression>(binder);
            Assert.Equal("Limit", field.Field.Name);
            Assert.Null(field.Receiver);
        }

        [Fact]
        public void AMemberTheTypeDoesNotHaveIsReported()
        {
            Bind(out var compilation,
                "class Entity { }\nclass Test { public fun run(): void { let e = Entity(); let a = e.nope; } }");

            AssertReports(compilation, SurtrDiagnosticCode.UnresolvedMember);
        }

        [Fact]
        public void ThisIsNotAvailableInAStaticMethod()
        {
            Bind(out var compilation, "class Test { public static fun run(): void { let a = this; } }");
            AssertReports(compilation, SurtrDiagnosticCode.NoInstanceInScope);
        }
        #endregion

        #region Calls
        [Fact]
        public void ACallResolvesToTheOverloadItsArgumentsFit()
        {
            var binder = Bind(out var compilation,
                "class Log {\n"
                + "  public fun write(code: int): void { }\n"
                + "  public fun write(message: string): void { }\n"
                + "  public fun run(): void { write(\"x\"); }\n"
                + "}");

            AssertNoErrors(compilation);

            var call = First<BoundCallExpression>(binder);
            Assert.Same(compilation.TypeFactory.String, call.Method.Parameters[0].Type);
        }

        [Fact]
        public void ArgumentsComeOutInParameterOrder()
        {
            var binder = Bind(out var compilation,
                "class Spawner {\n"
                + "  public fun spawn(x: float, y: float): void { }\n"
                + "  public fun run(): void { spawn(y: 2.0, x: 1.0); }\n"
                + "}");

            AssertNoErrors(compilation);

            // Named arguments may be written in any order; the tree carries them in parameter order
            // so nothing downstream has to reorder them.
            var call = First<BoundCallExpression>(binder);
            Assert.Equal(2, call.Arguments.Count);
            Assert.Equal(1.0, Assert.IsType<BoundLiteralExpression>(call.Arguments[0]).Value);
            Assert.Equal(2.0, Assert.IsType<BoundLiteralExpression>(call.Arguments[1]).Value);
        }

        [Fact]
        public void AWideningArgumentCarriesItsConversion()
        {
            var binder = Bind(out var compilation,
                "class Scaler {\n"
                + "  public fun scale(by: float): void { }\n"
                + "  public fun run(): void { scale(2); }\n"
                + "}");

            AssertNoErrors(compilation);
            Assert.Same(compilation.TypeFactory.Float, First<BoundCallExpression>(binder).Arguments[0].Type);
        }

        [Fact]
        public void ACallThatFitsNoOverloadIsReported()
        {
            Bind(out var compilation,
                "class Log {\n"
                + "  public fun write(code: int): void { }\n"
                + "  public fun run(): void { write(true); }\n"
                + "}");

            AssertReports(compilation, SurtrDiagnosticCode.UnresolvedCall);
        }

        [Fact]
        public void ConstructionNeedsNoKeyword()
        {
            var binder = Bind(out var compilation,
                "class Vec2 { constructor(x: float, y: float) { } }\n"
                + "class Test { public fun run(): void { let v = Vec2(1.0, 2.0); } }");

            AssertNoErrors(compilation);

            var creation = First<BoundObjectCreationExpression>(binder);
            Assert.Equal("Vec2", creation.Type.Name);
            Assert.Equal(MemberNames.Constructor, creation.Constructor!.Name);
        }

        [Fact]
        public void AnAbstractTypeCannotBeConstructed()
        {
            Bind(out var compilation,
                "abstract class Animal { }\nclass Test { public fun run(): void { let a = Animal(); } }");

            AssertReports(compilation, SurtrDiagnosticCode.NotSupportedOnType);
        }

        [Fact]
        public void ACallOnASealedReceiverIsBoundDirectly()
        {
            // §2.2: sealed tells the compiler no override can exist, so the vtable is skipped.
            var binder = Bind(out var compilation,
                "sealed class Vec2 { public virtual fun length(): float { return 0.0; } }\n"
                + "class Test { public fun run(): void { let v = Vec2(); let l = v.length(); } }");

            AssertNoErrors(compilation);
            Assert.False(First<BoundCallExpression>(binder).IsVirtual);
        }

        [Fact]
        public void AVirtualCallOnAnOpenReceiverGoesThroughTheVtable()
        {
            var binder = Bind(out var compilation,
                "class Animal { public virtual fun speak(): void { } }\n"
                + "class Test { public fun run(): void { let a = Animal(); a.speak(); } }");

            AssertNoErrors(compilation);
            Assert.True(First<BoundCallExpression>(binder).IsVirtual);
        }
        #endregion

        #region Closures
        [Fact]
        public void ALambdaTakesItsTypeFromWhereItGoes()
        {
            var binder = BindIn(out var compilation, "let f: (int) -> int = (x) => x + 1;");

            AssertNoErrors(compilation);

            var lambda = First<BoundLambdaExpression>(binder);
            Assert.Same(compilation.TypeFactory.Int, lambda.Parameters[0].Type);
        }

        [Fact]
        public void ALambdaWithNothingToInferFromIsReported()
        {
            BindIn(out var compilation, "let f = (x) => x;");
            AssertReports(compilation, SurtrDiagnosticCode.CannotInferType);
        }

        [Fact]
        public void AClosureIsInvokedThroughItsValue()
        {
            var binder = BindIn(out var compilation, "let f: (int) -> int = (x: int) => x;\nlet a = f(1);");

            AssertNoErrors(compilation);
            Assert.Same(compilation.TypeFactory.Int, First<BoundClosureInvocationExpression>(binder).Type);
        }

        [Fact]
        public void ALambdaCapturesWhatItReadsFromOutside()
        {
            var binder = BindIn(out var compilation, "let n = 2;\nlet f: (int) -> int = (x: int) => x + n;");

            AssertNoErrors(compilation);

            var lambda = First<BoundLambdaExpression>(binder);
            Assert.Equal("n", Assert.Single(lambda.Captured).Name);
        }

        [Fact]
        public void ALambdaDoesNotCaptureItsOwnParameters()
        {
            var binder = BindIn(out var compilation, "let f: (int) -> int = (x: int) => x + 1;");

            AssertNoErrors(compilation);
            Assert.Empty(First<BoundLambdaExpression>(binder).Captured);
        }

        [Fact]
        public void ALambdaCannotCaptureSomethingReassigned()
        {
            // A capture is copied into the closure rather than shared, which is only sound for
            // something that never changes.
            BindIn(out var compilation, "var n = 2;\nlet f: (int) -> int = (x: int) => x + n;");
            AssertReports(compilation, SurtrDiagnosticCode.NotAssignable);
        }

        /// <summary>§8: where a closure is kept says nothing about how it is called.</summary>
        [Fact]
        public void AClosureInAStaticIsInvokedThroughItsTypeName()
        {
            var binder = Bind(
                out var compilation,
                "class First { public static let Make: () -> int = () => 5; }\n"
                    + "class Test { public fun run(): int { return First.Make(); } }");

            AssertNoErrors(compilation);
            Assert.Same(compilation.TypeFactory.Int, First<BoundClosureInvocationExpression>(binder).Type);
        }

        [Fact]
        public void AClosureInAnInstanceFieldIsInvokedThroughTheReceiver()
        {
            var binder = BindIn(
                out var compilation,
                "let b = Box();\nlet a = b.handler(2);",
                extra: "class Box { public let handler: (int) -> int = (x: int) => x; }");

            AssertNoErrors(compilation);
            Assert.Same(compilation.TypeFactory.Int, First<BoundClosureInvocationExpression>(binder).Type);
        }

        /// <summary>A method of that name is what a call usually means, so it wins.</summary>
        [Fact]
        public void AMethodBeatsAClosureFieldOfTheSameName()
        {
            var binder = BindIn(
                out var compilation,
                "let a = Box().f();",
                extra: "class Box {\n"
                    + "  public let f: () -> int = () => 2;\n"
                    + "  public fun f(): int { return 1; }\n"
                    + "}");

            AssertNoErrors(compilation);
            Assert.Equal("f", First<BoundCallExpression>(binder).Method.Name);
            Assert.Empty(Walk(Body(binder)).OfType<BoundClosureInvocationExpression>());
        }

        [Fact]
        public void AFieldThatHoldsNoClosureIsStillNotCallable()
        {
            BindIn(out var compilation, "let a = Box().n();", extra: "class Box { public let n: int = 1; }");
            AssertReports(compilation, SurtrDiagnosticCode.UnresolvedName);
        }

        /// <summary>§5.1: the guard wraps the invocation, so a null receiver calls nothing.</summary>
        [Fact]
        public void ANullConditionalReachesAClosureMemberToo()
        {
            var binder = BindIn(
                out var compilation,
                "let b: Box? = Box();\nlet a = b?.handler();",
                extra: "class Box { public let handler: () -> int = () => 7; }");

            AssertNoErrors(compilation);

            var guarded = First<BoundNullConditionalExpression>(binder);
            Assert.IsType<BoundClosureInvocationExpression>(guarded.Access);
            Assert.True(guarded.Type.IsNullable);
        }
        #endregion

        #region Casts, tests and indexing
        [Fact]
        public void ASafeCastYieldsANullableResult()
        {
            var binder = Bind(out var compilation,
                "class Animal { }\nclass Dog : Animal { }\n"
                + "class Test { public fun run(): void { let a = Animal(); let d = a as? Dog; } }");

            AssertNoErrors(compilation);

            var conversion = Walk(Body(binder)).OfType<BoundConversionExpression>().First(c => c.IsSafe);
            Assert.True(conversion.Type.IsNullable);
        }

        [Fact]
        public void ACastThatCouldNeverWorkIsReported()
        {
            BindIn(out var compilation, "let a = \"x\" as int;");
            AssertReports(compilation, SurtrDiagnosticCode.CannotConvert);
        }

        [Fact]
        public void APrimitiveConstructorIsSugarForTheEquivalentCast()
        {
            // §5.3.2: int(aFloat) is meant to be the literal same conversion `aFloat as int`
            // classifies to — same Kind, same result type — reached from a second syntax rather
            // than a rule of its own.
            var binder = BindIn(out var compilation, "let a = int(3.9);\nlet b = 3.9 as int;");

            AssertNoErrors(compilation);

            var conversions = Walk(Body(binder)).OfType<BoundConversionExpression>().ToList();
            Assert.Equal(2, conversions.Count);
            Assert.Equal(conversions[1].Conversion.Kind, conversions[0].Conversion.Kind);
            Assert.Same(conversions[1].Type, conversions[0].Type);
        }

        [Fact]
        public void TheTupleCopyConstructorIsAnIdentityFold()
        {
            // (T1,T2)(pair: (T1,T2)) has to bind to the argument itself, unwrapped — not a new node
            // that merely evaluates to the same value.
            var binder = BindIn(out var compilation, "let t1 = (1, 2);\nlet t2 = tuple<int, int>(t1);");

            AssertNoErrors(compilation);

            var locals = Walk(Body(binder)).OfType<BoundLocalDeclarationStatement>().ToList();
            var t1Local = locals[0].Local;
            var t2AsLocal = Assert.IsType<BoundLocalExpression>(locals[1].Initializer);

            Assert.Same(t1Local, t2AsLocal.Local);
        }

        [Fact]
        public void APrimitiveConstructorWithNoMatchingShapeIsReported()
        {
            BindIn(out var compilation, "let a = int(true, false);");
            AssertReports(compilation, SurtrDiagnosticCode.NoBuiltInConstructorMatch);
        }

        [Fact]
        public void ATypeTestIsBool()
        {
            var binder = Bind(out var compilation,
                "class Animal { }\nclass Test { public fun run(): void { let a = Animal(); let b = a is Animal; } }");

            AssertNoErrors(compilation);
            Assert.Same(compilation.TypeFactory.Bool, First<BoundTypeTestExpression>(binder).Type);
        }

        [Fact]
        public void IndexingReadsTheElementType()
        {
            var binder = BindIn(out var compilation,
                "let xs: int[] = [1, 2];\nlet a = xs[0];\n"
                + "let m: {string: float} = {};\nlet b = m[\"k\"];\n"
                + "let s = \"abc\";\nlet c = s[0];");

            AssertNoErrors(compilation);

            var indexes = Walk(Body(binder)).OfType<BoundIndexExpression>().ToList();
            Assert.Same(compilation.TypeFactory.Int, indexes[0].Type);
            Assert.Same(compilation.TypeFactory.Float, indexes[1].Type);
            Assert.Same(compilation.TypeFactory.Char, indexes[2].Type);
        }

        [Fact]
        public void IndexingSomethingThatCannotBeIsReported()
        {
            BindIn(out var compilation, "let a = 1;\nlet b = a[0];");
            AssertReports(compilation, SurtrDiagnosticCode.NotSupportedOnType);
        }

        [Fact]
        public void ATupleIsIndexedByPosition()
        {
            var binder = BindIn(out var compilation, "let t = (1, \"x\", 2.0);\nlet a = t[1];\nlet b = t[2];");

            AssertNoErrors(compilation);

            var indexes = Walk(Body(binder)).OfType<BoundIndexExpression>().ToList();
            Assert.Same(compilation.TypeFactory.String, indexes[0].Type);
            Assert.Same(compilation.TypeFactory.Float, indexes[1].Type);
        }

        [Fact]
        public void ATupleIndexHasToBeAConstant()
        {
            BindIn(out var compilation, "let t = (1, 2);\nlet i = 0;\nlet a = t[i];");
            AssertReports(compilation, SurtrDiagnosticCode.InvalidTupleIndex);
        }

        [Fact]
        public void ATupleIndexPastItsArityIsReported()
        {
            BindIn(out var compilation, "let t = (1, 2);\nlet a = t[2];");
            AssertReports(compilation, SurtrDiagnosticCode.InvalidTupleIndex);
        }

        [Fact]
        public void ATupleElementCannotBeAssignedTo()
        {
            BindIn(out var compilation, "let t = (1, 2);\nt[0] = 3;");
            AssertReports(compilation, SurtrDiagnosticCode.NotAssignable);
        }

        /// <summary>§7.1 makes a <c>const</c> a compile-time constant, so it is one here too.</summary>
        [Fact]
        public void AConstIsAConstantEnoughToIndexATuple()
        {
            var binder = BindIn(
                out var compilation,
                "let t = (1, \"x\");\nlet a = t[Second];",
                extra: "const Second: int = 1;");

            AssertNoErrors(compilation);

            var index = Walk(Body(binder)).OfType<BoundIndexExpression>().Single();
            Assert.Same(compilation.TypeFactory.String, index.Type);
        }

        /// <summary>§4.2 yields a dict's entries as pairs, and this is what reads one.</summary>
        [Fact]
        public void ADictEntryIsAPairReadByPosition()
        {
            var binder = BindIn(
                out var compilation,
                "let scores: {string: int} = {};\nfor (entry in scores) { let k = entry[0]; let v = entry[1]; }");

            AssertNoErrors(compilation);

            var indexes = Walk(Body(binder)).OfType<BoundIndexExpression>().ToList();
            Assert.Same(compilation.TypeFactory.String, indexes[0].Type);
            Assert.Same(compilation.TypeFactory.Int, indexes[1].Type);
        }
        #endregion

        #region The built-in collections' own members
        [Fact]
        public void AnArraysMembersAreReachableAndTakeItsElementType()
        {
            var binder = BindIn(out var compilation, "let xs: int[] = [1];\nlet n = xs.length;\nxs.push(2);");

            AssertNoErrors(compilation);

            var length = Walk(Body(binder)).OfType<BoundPropertyExpression>().Single();
            Assert.Equal("length", length.Property.Name);
            Assert.Same(compilation.TypeFactory.Int, length.Type);

            var push = Walk(Body(binder)).OfType<BoundCallExpression>().Single(c => c.Method.Name == "push");
            Assert.Same(compilation.TypeFactory.Int, push.Method.Parameters[0].Type);
        }

        [Fact]
        public void AnArraysElementTypeIsCheckedAtTheCallSite()
        {
            BindIn(out var compilation, "let xs: int[] = [1];\nxs.push(\"x\");");
            AssertReports(compilation, SurtrDiagnosticCode.UnresolvedCall);
        }

        [Fact]
        public void APoppedElementReadsAsTheElementType()
        {
            var binder = BindIn(out var compilation, "let xs: string[] = [\"a\"];\nlet s = xs.pop();");

            AssertNoErrors(compilation);
            Assert.Same(
                compilation.TypeFactory.String,
                Walk(Body(binder)).OfType<BoundCallExpression>().Single(c => c.Method.Name == "pop").Type);
        }

        [Fact]
        public void ADictionarysMembersSubstituteBothParameters()
        {
            var binder = BindIn(out var compilation,
                "let m: {string: int} = {};\nlet v = m.get(\"k\");\nlet ks = m.keys();");

            AssertNoErrors(compilation);

            var calls = Walk(Body(binder)).OfType<BoundCallExpression>().ToList();
            Assert.Same(compilation.TypeFactory.Int, calls.Single(c => c.Method.Name == "get").Type);
            Assert.Same(
                compilation.TypeFactory.Array(compilation.TypeFactory.String),
                calls.Single(c => c.Method.Name == "keys").Type);
        }

        [Fact]
        public void AStringsMembersAreReachable()
        {
            var binder = BindIn(out var compilation, "let s = \"abc\";\nlet n = s.length;\nlet t = s.substring(0, 2);");

            AssertNoErrors(compilation);
            Assert.Same(
                compilation.TypeFactory.String,
                Walk(Body(binder)).OfType<BoundCallExpression>().Single(c => c.Method.Name == "substring").Type);
        }

        [Fact]
        public void APrimitivesMembersAreReachable()
        {
            var binder = BindIn(out var compilation, "let n = 7;\nlet s = n.toString();");

            AssertNoErrors(compilation);
            Assert.Same(
                compilation.TypeFactory.String,
                Walk(Body(binder)).OfType<BoundCallExpression>().Single(c => c.Method.Name == "toString").Type);
        }

        [Fact]
        public void ATupleKeepsTheThinSurfaceItDeclares()
        {
            var binder = BindIn(out var compilation, "let t = (1, 2);\nlet n = t.length;");

            AssertNoErrors(compilation);
            Assert.Same(compilation.TypeFactory.Int, Walk(Body(binder)).OfType<BoundPropertyExpression>().Single().Type);
        }

        [Fact]
        public void ASubstitutedMemberStillNamesTheMetadataItCameFrom()
        {
            // The substituted view is a fresh symbol, and a call site that could not name a real
            // method table entry would bind and then fail to emit.
            var binder = BindIn(out var compilation, "let xs: int[] = [1];\nxs.push(2);");

            AssertNoErrors(compilation);
            Assert.NotNull(
                Walk(Body(binder)).OfType<BoundCallExpression>().Single(c => c.Method.Name == "push").Method.ImportedFrom);
        }
        #endregion

        #region Statements
        [Fact]
        public void AConditionHasToBeABool()
        {
            BindIn(out var compilation, "if (1) { }");
            AssertReports(compilation, SurtrDiagnosticCode.CannotConvert);
        }

        [Fact]
        public void AReturnIsCheckedAgainstTheMethodsType()
        {
            Bind(out var compilation, "class Test { public fun run(): int { return \"x\"; } }");
            AssertReports(compilation, SurtrDiagnosticCode.CannotConvert);
        }

        [Fact]
        public void AVoidMethodCannotReturnAValue()
        {
            Bind(out var compilation, "class Test { public fun run(): void { return 1; } }");
            AssertReports(compilation, SurtrDiagnosticCode.CannotConvert);
        }

        [Fact]
        public void AReturningMethodNeedsOne()
        {
            Bind(out var compilation, "class Test { public fun run(): int { return; } }");
            AssertReports(compilation, SurtrDiagnosticCode.CannotConvert);
        }

        [Fact]
        public void BreakNeedsALoop()
        {
            BindIn(out var compilation, "break;");
            AssertReports(compilation, SurtrDiagnosticCode.JumpOutsideLoop);
        }

        /// <summary>§4.2: a three-clause <c>for</c> reassigns its variable, so it takes <c>var</c>.</summary>
        [Fact]
        public void AThreeClauseForCounterCannotBeALet()
        {
            BindIn(out var compilation, "for (let i = 0; i < 3; i += 1) { }");

            var reported = compilation.Diagnostics.Single(d => d.Code == SurtrDiagnosticCode.NotAssignable);
            Assert.Contains("'var'", reported.Message);
        }

        [Fact]
        public void WithVarItBinds()
        {
            BindIn(out var compilation, "for (var i = 0; i < 3; i += 1) { }");
            AssertNoErrors(compilation);
        }

        /// <summary>A <c>for-in</c> variable is rebound per step, which is what makes it assign-once.</summary>
        [Fact]
        public void AForInVariableCannotBeWrittenTo()
        {
            BindIn(out var compilation, "for (i in 0..3) { i = 9; }");
            AssertReports(compilation, SurtrDiagnosticCode.NotAssignable);
        }

        [Fact]
        public void BreakInsideALoopIsFine()
        {
            BindIn(out var compilation, "while (true) { break; }");
            AssertNoErrors(compilation);
        }

        [Fact]
        public void ForInReadsTheElementTypeOfWhatItWalks()
        {
            var binder = BindIn(out var compilation,
                "let xs: int[] = [];\nfor (x in xs) { let a = x; }");

            AssertNoErrors(compilation);
            Assert.Same(compilation.TypeFactory.Int, First<BoundForInStatement>(binder).Variable.Type);
        }

        [Fact]
        public void ForInOverADictYieldsPairs()
        {
            var binder = BindIn(out var compilation, "let m: {string: int} = {};\nfor (e in m) { }");

            AssertNoErrors(compilation);

            var element = Assert.IsType<TupleTypeSymbol>(First<BoundForInStatement>(binder).Variable.Type);
            Assert.Same(compilation.TypeFactory.String, element.ElementTypes[0]);
            Assert.Same(compilation.TypeFactory.Int, element.ElementTypes[1]);
        }

        [Fact]
        public void ForInOverARangeYieldsInts()
        {
            var binder = BindIn(out var compilation, "for (i in 0..10) { }");

            AssertNoErrors(compilation);
            Assert.Same(compilation.TypeFactory.Int, First<BoundForInStatement>(binder).Variable.Type);
        }

        [Fact]
        public void ForInOverSomethingNotIterableIsReported()
        {
            BindIn(out var compilation, "let a = 1;\nfor (x in a) { }");
            AssertReports(compilation, SurtrDiagnosticCode.NotSupportedOnType);
        }

        [Fact]
        public void ACatchBindsItsExceptionToTheTypeItMatched()
        {
            var binder = BindIn(out var compilation, "try { } catch (e: Exception) { let m = e; }");

            AssertNoErrors(compilation);

            var local = Walk(Body(binder)).OfType<BoundLocalDeclarationStatement>().First();
            Assert.Equal("Exception", ((NamedTypeSymbol)local.Local.Type).Name);
        }
        #endregion

        #region Nullability
        [Fact]
        public void NullNeedsATypeThatCanHoldIt()
        {
            BindIn(out var compilation, "let a: string = null;");
            AssertReports(compilation, SurtrDiagnosticCode.NullNotAllowed);
        }

        [Fact]
        public void ANullableTypeCanHoldIt()
        {
            BindIn(out var compilation, "let a: string? = null;");
            AssertNoErrors(compilation);
        }

        [Fact]
        public void TheBranchesOfATernaryHaveToAgree()
        {
            BindIn(out var compilation, "let a = true ? 1 : \"x\";");
            AssertReports(compilation, SurtrDiagnosticCode.NoCommonType);
        }

        [Fact]
        public void ATernaryTakesTheTypeBothBranchesReach()
        {
            var binder = BindIn(out var compilation, "let a = true ? 1 : 2.0;");

            AssertNoErrors(compilation);
            Assert.Same(compilation.TypeFactory.Float, First<BoundConditionalExpression>(binder).Type);
        }

        [Fact]
        public void NullCoalescingProducesTheNonNullableType()
        {
            var binder = BindIn(out var compilation, "let a: int? = null;\nlet b = a ?? 0;");

            AssertNoErrors(compilation);

            var local = Walk(Body(binder)).OfType<BoundLocalDeclarationStatement>().Last();
            Assert.Same(compilation.TypeFactory.Int, local.Local.Type);
        }
        #endregion
    }
}
