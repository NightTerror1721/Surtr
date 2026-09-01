#nullable enable

using Surtr.Run;
using Surtr.Runtime;
using Surtr.Stdlib;
using System;

namespace Surtr.Tests.Run
{
    /// <summary>
    /// Covers <see cref="ReplSession"/> headlessly (no real console), per
    /// <c>docs/Plan-Roadmap-Novedades.md</c>'s Fase 0 verification: declaring a variable and reading
    /// it back on a later line, a failed declaration leaving the previous session intact, and a
    /// side-effecting statement running exactly once - never replayed on a later, unrelated
    /// submission, unlike the accumulated declarations.
    /// </summary>
    public sealed class ReplSessionTests
    {
        [Fact]
        public void BlankInput_IsANoOp()
        {
            using var runtime = new SurtrRuntime();
            var session = new ReplSession(runtime);

            var outcome = session.Submit("   ");

            Assert.True(outcome.Success);
            Assert.Null(outcome.Printed);
        }

        [Fact]
        public void ABareExpression_PrintsItsValue()
        {
            using var runtime = new SurtrRuntime();
            var session = new ReplSession(runtime);

            var outcome = session.Submit("2 + 2 * 3");

            Assert.True(outcome.Success);
            Assert.Equal("8", outcome.Printed);
        }

        [Fact]
        public void DeclaringAVariable_ThenReadingItOnALaterLine_Works()
        {
            using var runtime = new SurtrRuntime();
            var session = new ReplSession(runtime);

            var declared = session.Submit("let x: int = 5;");
            Assert.True(declared.Success, declared.Error);
            Assert.Null(declared.Printed);

            var read = session.Submit("x + 1");
            Assert.True(read.Success, read.Error);
            Assert.Equal("6", read.Printed);
        }

        [Fact]
        public void DeclaringAFunction_ThenCallingItOnALaterLine_Works()
        {
            using var runtime = new SurtrRuntime();
            var session = new ReplSession(runtime);

            Assert.True(session.Submit("fun add(a: int, b: int): int => a + b;").Success);

            var result = session.Submit("add(2, 3)");
            Assert.True(result.Success, result.Error);
            Assert.Equal("5", result.Printed);
        }

        [Fact]
        public void ALetOrVarWithNoExplicitType_FailsWithAClearMessage_NotTheCompilersConfusingOne()
        {
            using var runtime = new SurtrRuntime();
            var session = new ReplSession(runtime);

            var outcome = session.Submit("let x = 5;");

            Assert.False(outcome.Success);
            Assert.Contains("explicit type", outcome.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AFailedDeclaration_LeavesThePreviousSessionIntact()
        {
            using var runtime = new SurtrRuntime();
            var session = new ReplSession(runtime);

            Assert.True(session.Submit("let x: int = 5;").Success);

            var broken = session.Submit("let y: int = ;");
            Assert.False(broken.Success);
            Assert.NotNull(broken.Error);

            // The typo above must not have replaced the active session - x is still there, and y
            // was never declared at all.
            var stillThere = session.Submit("x");
            Assert.True(stillThere.Success, stillThere.Error);
            Assert.Equal("5", stillThere.Printed);

            var neverDeclared = session.Submit("y");
            Assert.False(neverDeclared.Success);
        }

        [Fact]
        public void AStatementWithASideEffect_RunsExactlyOnceAndIsNeverReplayed()
        {
            using var runtime = new SurtrRuntime();
            var session = new ReplSession(runtime);

            Assert.True(session.Submit("var counter: int = 0;").Success);

            Assert.True(session.Submit("counter = counter + 1;").Success);
            Assert.Equal("1", session.Submit("counter").Printed);

            // A second, unrelated submission must not re-run the first statement - each eval module
            // is invoked exactly once and then never touched again.
            Assert.True(session.Submit("counter = counter + 1;").Success);
            Assert.Equal("2", session.Submit("counter").Printed);
        }

        [Fact]
        public void ABareStatementWithNoValue_PrintsNothing()
        {
            using var runtime = new SurtrRuntime();
            var session = new ReplSession(runtime);

            Assert.True(session.Submit("var counter: int = 0;").Success);

            var outcome = session.Submit("counter = counter + 1;");
            Assert.True(outcome.Success, outcome.Error);
            Assert.Null(outcome.Printed);
        }

        [Fact]
        public void AnUnresolvableExpression_ReportsDiagnosticsWithoutThrowing()
        {
            using var runtime = new SurtrRuntime();
            var session = new ReplSession(runtime);

            var outcome = session.Submit("thisNameWasNeverDeclared");

            Assert.False(outcome.Success);
            Assert.NotNull(outcome.Error);
        }

        [Fact]
        public void ARuntimeTrap_IsReportedAndTheSessionSurvivesToTheNextLine()
        {
            using var runtime = new SurtrRuntime();
            var session = new ReplSession(runtime);

            var trapped = session.Submit("1 / 0");
            Assert.False(trapped.Success);
            Assert.NotNull(trapped.Error);

            // The runtime is reset after a trap (ReplSession.InvokeAndDescribe) - a later,
            // unrelated line must still work.
            var recovered = session.Submit("1 + 1");
            Assert.True(recovered.Success, recovered.Error);
            Assert.Equal("2", recovered.Printed);
        }

        [Fact]
        public void ADeclarationImportingTheStdlib_WorksThroughTheSession()
        {
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadAll(runtime);
            var session = new ReplSession(runtime);

            var imported = session.Submit("import surtr.math.Math;");
            Assert.True(imported.Success, imported.Error);

            var result = session.Submit("floor(3.7)");
            Assert.True(result.Success, result.Error);
            Assert.Equal("3", result.Printed);
        }

        [Fact]
        public void ADeclarationUsingAnEarlierImport_SeesIt_NotJustABareStatementDoes()
        {
            // Regression: SubmitDeclaration's candidate source used to omit _imports entirely,
            // so a later `let`/`var`/`fun`/`class` naming an imported type failed to resolve it
            // even though a bare statement (the test above) already worked - the two paths built
            // their candidate source differently and only one of them remembered the imports.
            using var runtime = new SurtrRuntime();
            SurtrStdlib.LoadAll(runtime);
            var session = new ReplSession(runtime);

            Assert.True(session.Submit("import surtr.text.Regex;").Success);

            var declared = session.Submit("let re: Regex = Regex.compile(\"a+\").unwrap();");
            Assert.True(declared.Success, declared.Error);

            var used = session.Submit("re.isMatch(\"aaa\")");
            Assert.True(used.Success, used.Error);
            Assert.Equal("true", used.Printed);
        }
    }
}
