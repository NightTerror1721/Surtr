#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using System.Linq;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// Covers the questions that need a whole body rather than one expression: what can be reached,
    /// what is assigned where it is read, whether a method can finish without returning, and what a
    /// condition proves about a nullable for as long as it holds.
    /// </summary>
    public sealed class FlowAnalysisTests
    {
        private const string Root = "D:/proj/src";

        private static SurtrCompilation Bind(string source, params (string Name, BuildConstant Value)[] constants)
        {
            var project = new SurtrProject(Root);
            foreach (var constant in constants)
                project.Define(constant.Name, constant.Value);

            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();
            return compilation;
        }

        private static SurtrCompilation BindIn(string statements, string returns = "void", string extra = "")
            => Bind(extra + "\nclass Test {\n  public fun run(): " + returns + " {\n" + statements + "\n  }\n}");

        private static void AssertNoErrors(SurtrCompilation compilation)
            => Assert.True(
                !compilation.HasErrors,
                "Unexpected: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

        private static void AssertReports(SurtrCompilation compilation, SurtrDiagnosticCode code)
            => Assert.True(
                compilation.Diagnostics.Any(d => d.Code == code),
                $"Expected {code}, got: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

        #region Definite assignment
        [Fact]
        public void ALocalReadBeforeItIsAssignedIsReported()
        {
            AssertReports(BindIn("var a: int;\nlet b = a;"), SurtrDiagnosticCode.UseBeforeAssignment);
        }

        [Fact]
        public void AssigningFirstIsFine()
        {
            AssertNoErrors(BindIn("var a: int;\na = 1;\nlet b = a;"));
        }

        [Fact]
        public void AParameterArrivesAssigned()
        {
            AssertNoErrors(Bind("class Test { public fun run(a: int): int { return a; } }"));
        }

        [Fact]
        public void AssigningInOnlyOneBranchIsNotEnough()
        {
            AssertReports(
                BindIn("var a: int;\nif (true) { a = 1; }\nlet b = a;"),
                SurtrDiagnosticCode.UseBeforeAssignment);
        }

        [Fact]
        public void AssigningInBothBranchesIs()
        {
            AssertNoErrors(BindIn("var a: int;\nif (true) { a = 1; } else { a = 2; }\nlet b = a;"));
        }

        [Fact]
        public void ALoopBodyDoesNotCountAsHavingRun()
        {
            // Nothing here proves the loop runs, so what its body assigned does not survive it.
            AssertReports(
                BindIn("var a: int;\nwhile (false) { a = 1; }\nlet b = a;"),
                SurtrDiagnosticCode.UseBeforeAssignment);
        }

        [Fact]
        public void ACompoundAssignmentReadsBeforeItWrites()
        {
            AssertReports(BindIn("var a: int;\na += 1;"), SurtrDiagnosticCode.UseBeforeAssignment);
        }

        [Fact]
        public void ALoopVariableIsAssignedInsideItsLoop()
        {
            AssertNoErrors(BindIn("let xs: int[] = [];\nfor (x in xs) { let a = x; }"));
        }

        [Fact]
        public void ACaughtExceptionIsAssignedInItsHandler()
        {
            AssertNoErrors(BindIn("try { } catch (e: Exception) { let m = e; }"));
        }

        [Fact]
        public void OneLocalIsReportedOnceHoweverOftenItIsRead()
        {
            var compilation = BindIn("var a: int;\nlet b = a;\nlet c = a;\nlet d = a;");

            Assert.Single(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UseBeforeAssignment);
        }
        #endregion

        #region Reachability and returns
        [Fact]
        public void AStatementAfterAReturnIsUnreachable()
        {
            var compilation = Bind("class Test { public fun run(): int { return 1; let a = 2; } }");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnreachableCode);
        }

        [Fact]
        public void UnreachableCodeIsAWarningNotAnError()
        {
            var compilation = Bind("class Test { public fun run(): int { return 1; let a = 2; } }");

            var warning = compilation.Diagnostics.Single(d => d.Code == SurtrDiagnosticCode.UnreachableCode);
            Assert.Equal(SurtrDiagnosticSeverity.Warning, warning.Severity);
        }

        [Fact]
        public void AStatementAfterAThrowIsUnreachableToo()
        {
            var compilation = BindIn("throw Exception(\"x\");\nlet a = 1;");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnreachableCode);
        }

        [Fact]
        public void OnlyTheFirstUnreachableStatementIsReported()
        {
            var compilation = Bind("class Test { public fun run(): int { return 1; let a = 2; let b = 3; let c = 4; } }");

            Assert.Single(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnreachableCode);
        }

        [Fact]
        public void AMethodThatCanFinishWithoutReturningIsReported()
        {
            AssertReports(
                Bind("class Test { public fun run(): int { let a = 1; } }"),
                SurtrDiagnosticCode.NotAllPathsReturn);
        }

        [Fact]
        public void ReturningOnEveryBranchIsEnough()
        {
            AssertNoErrors(Bind("class Test { public fun run(): int { if (true) { return 1; } else { return 2; } } }"));
        }

        [Fact]
        public void ReturningOnOnlyOneBranchIsNot()
        {
            AssertReports(
                Bind("class Test { public fun run(): int { if (true) { return 1; } } }"),
                SurtrDiagnosticCode.NotAllPathsReturn);
        }

        [Fact]
        public void AVoidMethodNeedsNoReturn()
        {
            AssertNoErrors(Bind("class Test { public fun run(): void { let a = 1; } }"));
        }

        [Fact]
        public void ThrowingCountsAsNotFinishing()
        {
            AssertNoErrors(Bind("class Test { public fun run(): int { throw Exception(\"x\"); } }"));
        }
        #endregion

        #region Terminating shapes
        [Fact]
        public void AnEndlessLoopWithAReturnInsideIsEnough()
        {
            AssertNoErrors(Bind("class Test { public fun run(): int { while (true) { return 1; } } }"));
        }

        [Fact]
        public void AnEndlessLoopThatReturnsNothingStillFinishesNothing()
        {
            AssertNoErrors(Bind("class Test { public fun run(): int { while (true) { let a = 1; } } }"));
        }

        [Fact]
        public void ABreakIsAWayOutOfAnEndlessLoop()
        {
            AssertReports(
                Bind("class Test { public fun run(): int { while (true) { break; } } }"),
                SurtrDiagnosticCode.NotAllPathsReturn);
        }

        [Fact]
        public void AContinueIsNot()
        {
            AssertNoErrors(Bind("class Test { public fun run(): int { while (true) { continue; } } }"));
        }

        [Fact]
        public void ABreakLeavesTheLoopItIsIn()
        {
            // The inner `break` is the inner loop's way out, not the outer one's.
            AssertNoErrors(Bind(
                "class Test { public fun run(): int { while (true) { for (i in 0..3) { break; } } } }"));
        }

        [Fact]
        public void ALabelledBreakLeavesTheLoopItNames()
        {
            AssertReports(
                Bind(
                    "class Test { public fun run(): int {\n"
                    + "  outer: while (true) { while (true) { break outer; } }\n"
                    + "} }"),
                SurtrDiagnosticCode.NotAllPathsReturn);
        }

        [Fact]
        public void AForWithNoConditionIsEndlessToo()
        {
            AssertNoErrors(Bind("class Test { public fun run(): int { for (;;) { return 1; } } }"));
        }

        [Fact]
        public void NothingRunsAfterAnEndlessLoop()
        {
            var compilation = BindIn("while (true) { }\nlet a = 1;");

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnreachableCode);
        }

        [Fact]
        public void ASwitchWhoseEverySectionReturnsIsEnough()
        {
            AssertNoErrors(Bind(
                "class Test { public fun run(v: int): int {\n"
                + "  switch (v) { case 1: return 1; default: return 0; }\n"
                + "} }"));
        }

        [Fact]
        public void WithoutADefaultSectionSomethingAlwaysFallsOut()
        {
            AssertReports(
                Bind(
                    "class Test { public fun run(v: int): int {\n"
                    + "  switch (v) { case 1: return 1; case 2: return 2; }\n"
                    + "} }"),
                SurtrDiagnosticCode.NotAllPathsReturn);
        }

        [Fact]
        public void ABreakOutOfASwitchIsAWayOutToo()
        {
            AssertReports(
                Bind(
                    "class Test { public fun run(v: int): int {\n"
                    + "  switch (v) { case 1: break; default: return 0; }\n"
                    + "} }"),
                SurtrDiagnosticCode.NotAllPathsReturn);
        }

        [Fact]
        public void ASectionRunningOffTheEndOfTheSwitchIsOne()
        {
            AssertReports(
                Bind(
                    "class Test { public fun run(v: int): int {\n"
                    + "  switch (v) { case 1: return 1; default: let a = 0; }\n"
                    + "} }"),
                SurtrDiagnosticCode.NotAllPathsReturn);
        }
        #endregion

        #region Initializer order (VM-Plan §1.12, §3.4)
        [Fact]
        public void ReadingAStaticOfAClassDeclaredEarlierIsFine()
        {
            AssertNoErrors(Bind(
                "class First { public static let Value: int = 5; }\n"
                + "class Second { public static let Copy: int = First.Value; }"));
        }

        [Fact]
        public void ReadingOneDeclaredLaterIsReported()
        {
            AssertReports(
                Bind(
                    "class First { public static let Copy: int = Second.Value; }\n"
                    + "class Second { public static let Value: int = 5; }"),
                SurtrDiagnosticCode.InitializerOutOfOrder);
        }

        /// <summary>A module's own initializer runs after every class's, so this reads a zero.</summary>
        [Fact]
        public void AClassReadingAModuleVariableIsReported()
        {
            AssertReports(
                Bind("class First { public static let Copy: int = Base; }\nlet Base: int = 5;"),
                SurtrDiagnosticCode.InitializerOutOfOrder);
        }

        [Fact]
        public void TheModuleReadingAClassIsNot()
        {
            AssertNoErrors(Bind("class First { public static let Value: int = 5; }\nlet Copy: int = First.Value;"));
        }

        [Fact]
        public void OrderInsideOneClassCountsToo()
        {
            AssertReports(
                Bind("class C {\n  public static let A: int = B;\n  public static let B: int = 5;\n}"),
                SurtrDiagnosticCode.InitializerOutOfOrder);
        }

        [Fact]
        public void ReadingOneAlreadyWrittenIsFine()
        {
            AssertNoErrors(Bind("class C {\n  public static let A: int = 8;\n  public static let B: int = A;\n}"));
        }

        [Fact]
        public void ReachingItThroughACallIsCaught()
        {
            AssertReports(
                Bind(
                    "class First { public static let Copy: int = make(); }\n"
                    + "class Second { public static let Value: int = 5; }\n"
                    + "fun make(): int { return Second.Value; }"),
                SurtrDiagnosticCode.InitializerOutOfOrder);
        }

        [Fact]
        public void AStaticBlockIsCheckedAsWell()
        {
            AssertReports(
                Bind(
                    "class First {\n  public static var Copy: int = 0;\n  static { Copy = Second.Value; }\n}\n"
                    + "class Second { public static let Value: int = 5; }"),
                SurtrDiagnosticCode.InitializerOutOfOrder);
        }

        /// <summary>
        /// §2.4 makes an enum case a static its own type initializes, so it obeys the same order.
        /// </summary>
        [Fact]
        public void AnEnumCaseIsAStaticLikeAnyOther()
        {
            AssertReports(
                Bind(
                    "class Table { public static let Default: Suit = Suit.Hearts; }\n"
                    + "enum Suit { Hearts, Spades }"),
                SurtrDiagnosticCode.InitializerOutOfOrder);

            AssertNoErrors(Bind(
                "enum Suit { Hearts, Spades }\n"
                + "class Table { public static let Default: Suit = Suit.Hearts; }"));
        }

        /// <summary>§8: a lambda is a value here and a body later, so what it reads is not read now.</summary>
        [Fact]
        public void ALambdaIsNotRunAtLoad()
        {
            AssertNoErrors(Bind(
                "class First { public static let Make: () -> int = () => Second.Value; }\n"
                + "class Second { public static let Value: int = 5; }"));
        }

        /// <summary>An instance field's initializer runs from a constructor, not at load.</summary>
        [Fact]
        public void AnInstanceInitializerIsNotChecked()
        {
            AssertNoErrors(Bind(
                "class First { public var Copy: int = Second.Value; public constructor() { } }\n"
                + "class Second { public static let Value: int = 5; }"));
        }
        #endregion

        #region Nullability narrowing
        [Fact]
        public void ANullCheckNarrowsInsideTheBranch()
        {
            AssertNoErrors(BindIn("let a: int? = null;\nif (a != null) { let b: int = a; }"));
        }

        [Fact]
        public void TheNarrowingDoesNotLeakPastTheBranch()
        {
            AssertReports(
                BindIn("let a: int? = null;\nif (a != null) { }\nlet b: int = a;"),
                SurtrDiagnosticCode.CannotConvert);
        }

        [Fact]
        public void NullOnTheLeftNarrowsToo()
        {
            AssertNoErrors(BindIn("let a: int? = null;\nif (null != a) { let b: int = a; }"));
        }

        [Fact]
        public void TwoChecksJoinedByAndBothNarrow()
        {
            AssertNoErrors(BindIn(
                "let a: int? = null;\nlet b: int? = null;\n"
                + "if (a != null && b != null) { let c: int = a; let d: int = b; }"));
        }

        [Fact]
        public void ATypeTestNarrowsAsWell()
        {
            AssertNoErrors(BindIn("let a: int? = null;\nif (a is int) { let b: int = a; }"));
        }

        [Fact]
        public void WithoutTheCheckItDoesNotFit()
        {
            AssertReports(BindIn("let a: int? = null;\nlet b: int = a;"), SurtrDiagnosticCode.CannotConvert);
        }
        #endregion

        #region Generic constraints
        [Fact]
        public void AnArgumentThatSatisfiesItsBoundIsAccepted()
        {
            AssertNoErrors(Bind(
                "interface IThing { }\n"
                + "class Widget : IThing { }\n"
                + "class Box<T : IThing> { }\n"
                + "class Test { public var b: Box<Widget>; }"));
        }

        [Fact]
        public void AnArgumentThatDoesNotIsReported()
        {
            AssertReports(
                Bind(
                    "interface IThing { }\n"
                    + "class Widget { }\n"
                    + "class Box<T : IThing> { }\n"
                    + "class Test { public var b: Box<Widget>; }"),
                SurtrDiagnosticCode.ConstraintNotSatisfied);
        }

        [Fact]
        public void ABoundMayNameTheParameterItBounds()
        {
            // `<T : IComparable<T>>` is the shape §6 writes, and it only resolves once every type
            // exists - which is why constraints are bound in a pass of their own.
            AssertNoErrors(Bind(
                "class Vec2 : IComparable<Vec2> {\n"
                + "  public override fun compareTo(other: Vec2): int { return 0; }\n"
                + "}\n"
                + "class Sorter<T : IComparable<T>> { }\n"
                + "class Test { public var s: Sorter<Vec2>; }"));
        }

        [Fact]
        public void EveryBoundIsChecked()
        {
            AssertReports(
                Bind(
                    "interface IA { }\ninterface IB { }\n"
                    + "class Widget : IA { }\n"
                    + "class Box<T : IA & IB> { }\n"
                    + "class Test { public var b: Box<Widget>; }"),
                SurtrDiagnosticCode.ConstraintNotSatisfied);
        }

        [Fact]
        public void AnUnconstrainedParameterTakesAnything()
        {
            AssertNoErrors(Bind("class Box<T> { }\nclass Test { public var b: Box<int>; }"));
        }
        #endregion

        #region Switch exhaustiveness
        [Fact]
        public void ASwitchExpressionOverAnEnumHasToCoverIt()
        {
            AssertReports(
                Bind(
                    "enum Suit { Hearts, Spades, Clubs }\n"
                    + "class Test { public fun run(s: Suit): int { return switch (s) { Suit.Hearts -> 1, }; } }"),
                SurtrDiagnosticCode.SwitchNotExhaustive);
        }

        [Fact]
        public void ListingEveryCaseIsEnough()
        {
            AssertNoErrors(Bind(
                "enum Suit { Hearts, Spades }\n"
                + "class Test { public fun run(s: Suit): int {\n"
                + "  return switch (s) { Suit.Hearts -> 1, Suit.Spades -> 2, };\n"
                + "} }"));
        }

        [Fact]
        public void AnElseArmCoversWhateverIsLeft()
        {
            AssertNoErrors(Bind(
                "enum Suit { Hearts, Spades, Clubs }\n"
                + "class Test { public fun run(s: Suit): int {\n"
                + "  return switch (s) { Suit.Hearts -> 1, else -> 0, };\n"
                + "} }"));
        }

        [Fact]
        public void TheDiagnosticNamesWhatIsMissing()
        {
            var compilation = Bind(
                "enum Suit { Hearts, Spades, Clubs }\n"
                + "class Test { public fun run(s: Suit): int { return switch (s) { Suit.Hearts -> 1, }; } }");

            var missing = compilation.Diagnostics.Single(d => d.Code == SurtrDiagnosticCode.SwitchNotExhaustive);
            Assert.Contains("Spades", missing.Message);
            Assert.Contains("Clubs", missing.Message);
        }

        [Fact]
        public void ASwitchOverSomethingOpenIsNotChecked()
        {
            // Only an enum's cases are fixed at its own declaration, so only an enum can be covered.
            AssertNoErrors(Bind(
                "class Test { public fun run(v: int): int { return switch (v) { 1 -> 1, else -> 0, }; } }"));
        }

        [Fact]
        public void TheStatementFormIsNeverRequiredToBeExhaustive()
        {
            AssertNoErrors(Bind(
                "enum Suit { Hearts, Spades }\n"
                + "class Test { public fun run(s: Suit): void { switch (s) { case Suit.Hearts: break; } } }"));
        }
        #endregion

        #region const if
        [Fact]
        public void ADeclarationLevelConstIfKeepsOnlyTheTakenBranch()
        {
            var compilation = Bind(
                "const if (Debug) {\n  class Verbose { }\n} else {\n  class Quiet { }\n}",
                ("Debug", BuildConstant.Bool(true)));

            AssertNoErrors(compilation);

            var binder = compilation.Bind();
            Assert.Contains(binder.Modules["game.core.Test"].Types, t => t.Name == "Verbose");
            Assert.DoesNotContain(binder.Modules["game.core.Test"].Types, t => t.Name == "Quiet");
        }

        [Fact]
        public void TheUntakenBranchIsNeverBound()
        {
            // §7.3: it must be syntactically valid Surtr and nothing else about it is verified,
            // which is what lets it name host types this build does not have.
            AssertNoErrors(Bind(
                "const if (Debug) {\n  class Ok { }\n} else {\n  class Broken : NoSuchType { }\n}",
                ("Debug", BuildConstant.Bool(true))));
        }

        [Fact]
        public void AConditionComparingAStringFolds()
        {
            var compilation = Bind(
                "const if (Platform == \"IL2CPP\") {\n  class Native { }\n}",
                ("Platform", BuildConstant.String("IL2CPP")));

            AssertNoErrors(compilation);
            Assert.Contains(compilation.Bind().Modules["game.core.Test"].Types, t => t.Name == "Native");
        }

        [Fact]
        public void AConditionThatDoesNotFoldIsReported()
        {
            AssertReports(
                Bind("const if (someRuntimeThing) {\n  class Ok { }\n}"),
                SurtrDiagnosticCode.NotAConstant);
        }

        [Fact]
        public void AConstBindingIsUsableInACondition()
        {
            var compilation = Bind(
                "const Threshold: int = 10;\n"
                + "const if (Threshold > 5) {\n  class Big { }\n}");

            AssertNoErrors(compilation);
            Assert.Contains(compilation.Bind().Modules["game.core.Test"].Types, t => t.Name == "Big");
        }

        [Fact]
        public void AConstMayBeWrittenBelowTheConstIfThatReadsIt()
        {
            var compilation = Bind(
                "const if (Threshold > 5) {\n  class Big { }\n}\n"
                + "const Threshold: int = 10;");

            AssertNoErrors(compilation);
            Assert.Contains(compilation.Bind().Modules["game.core.Test"].Types, t => t.Name == "Big");
        }

        [Fact]
        public void AStatementLevelConstIfBindsOnlyTheTakenBranch()
        {
            AssertNoErrors(Bind(
                "class Test { public fun run(): void {\n"
                + "  const if (Debug) { let a = 1; } else { let b = nothingHere; }\n"
                + "} }",
                ("Debug", BuildConstant.Bool(true))));
        }

        [Fact]
        public void ShortCircuitingFoldsEvenWhenTheOtherSideWouldNot()
        {
            AssertNoErrors(Bind(
                "const if (Debug && Verbose) {\n  class Both { }\n}",
                ("Debug", BuildConstant.Bool(false)),
                ("Verbose", BuildConstant.Bool(true))));
        }
        #endregion
    }
}
