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

        [Fact]
        public void ANestedBlockMayShadow()
        {
            BindIn(out var compilation, "let a = 1;\n{ let a = \"x\"; }");
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
