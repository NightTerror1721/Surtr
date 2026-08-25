#nullable enable

using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using System.Linq;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// Covers the built-in side of §11: the argument checks every <c>@Name(...)</c> use pays
    /// against its class's declared fields, and the vocabulary the compiler itself gives meaning to
    /// (<c>@Obsolete</c>, <c>@NoDiscard</c>), which is declared once by the runtime's built-in
    /// module and so needs no declaration of the user's own.
    /// </summary>
    public sealed class BuiltInAttributeTests
    {
        private const string Root = "D:/proj/src";

        private static SurtrCompilation Compile(params (string Path, string Source)[] files)
        {
            var project = new SurtrProject(Root);
            foreach (var file in files)
                project.AddSourceFile(Root + "/" + file.Path, file.Source);

            var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();
            return compilation;
        }

        private static SurtrCompilation Compile(string source)
            => Compile(("game/core/Test.surtr", source));

        private static void AssertClean(SurtrCompilation compilation)
        {
            Assert.True(
                !compilation.HasErrors,
                "Unexpected error: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        private static void AssertReports(SurtrCompilation compilation, SurtrDiagnosticCode code)
        {
            Assert.True(
                compilation.Diagnostics.Any(d => d.Code == code),
                $"Expected {code}, got: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        private static void AssertNoReports(SurtrCompilation compilation, SurtrDiagnosticCode code)
        {
            Assert.DoesNotContain(compilation.Diagnostics, d => d.Code == code);
        }

        #region Argument checks (§11 prerequisite)

        [Fact]
        public void MoreArgumentsThanDeclaredFieldsIsReported()
        {
            using var compilation = Compile(
                "attribute class Pair { public let lo: int = 0; public let hi: int = 0; }\n"
                    + "@Pair(1, 2, 3)\n"
                    + "class Target { }");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeArgumentCountMismatch);
        }

        [Fact]
        public void AnArgumentMatchingItsFieldIsAccepted()
        {
            using var compilation = Compile(
                "attribute class Pair { public let lo: int = 0; public let hi: int = 0; }\n"
                    + "@Pair(1, 2)\n"
                    + "class Target { }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.AttributeArgumentCountMismatch);
            AssertNoReports(compilation, SurtrDiagnosticCode.AttributeArgumentTypeMismatch);
        }

        [Fact]
        public void FewerArgumentsThanFieldsIsFineBecauseTheRestKeepTheirDefaults()
        {
            using var compilation = Compile(
                "attribute class Pair { public let lo: int = 0; public let hi: int = 0; }\n"
                    + "@Pair(1)\n"
                    + "class Target { }");

            AssertClean(compilation);
        }

        [Fact]
        public void ATextArgumentCannotFillAnIntField()
        {
            using var compilation = Compile(
                "attribute class Named { public let name: string = \"\"; }\n"
                    + "@Named(7)\n"
                    + "class Target { }");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeArgumentTypeMismatch);
        }

        [Fact]
        public void AnIntegerWidensIntoAFloatFieldLikeAnyImplicitConversion()
        {
            using var compilation = Compile(
                "attribute class Bounds { public let lo: float = 0.0; public let hi: float = 0.0; }\n"
                    + "@Bounds(0, 100)\n"
                    + "class Target { }");

            AssertClean(compilation);
        }

        [Fact]
        public void TheMismatchingPositionIsTheOneReported()
        {
            using var compilation = Compile(
                "attribute class Mixed { public let name: string = \"\"; public let limit: int = 0; }\n"
                    + "@Mixed(\"ok\", true)\n"
                    + "class Target { }");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeArgumentTypeMismatch);
        }

        [Fact]
        public void NullDoesNotFillAPrimitiveField()
        {
            using var compilation = Compile(
                "attribute class Counted { public let n: int = 0; }\n"
                    + "@Counted(null)\n"
                    + "class Target { }");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeArgumentTypeMismatch);
        }

        [Fact]
        public void StaticAndConstMembersDoNotCountAsSlots()
        {
            using var compilation = Compile(
                "attribute class Guarded {\n"
                    + "  public let n: int = 0;\n"
                    + "  public const version: int = 1;\n"
                    + "}\n"
                    + "@Guarded(5)\n"
                    + "class Target { }");

            AssertClean(compilation);
        }

        [Fact]
        public void AUseOnAModuleFunctionIsCheckedTheSameWay()
        {
            using var compilation = Compile(
                "attribute class Solo { public let why: string = \"\"; }\n"
                    + "@Solo()\n"
                    + "public fun work(): void { }");

            AssertClean(compilation);
        }

        [Fact]
        public void ABuiltInUseWithTwoArgumentsReportsTheCount()
        {
            using var compilation = Compile(
                "@Obsolete(\"a\", \"b\")\n"
                    + "class Target { }");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeArgumentCountMismatch);
        }

        [Fact]
        public void ABuiltInUseWithANonTextReasonReportsTheType()
        {
            using var compilation = Compile(
                "@NoDiscard(7)\n"
                    + "class Target { }");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeArgumentTypeMismatch);
        }

        #endregion

        #region @Obsolete

        [Fact]
        public void TheBuiltInVocabularyNeedsNoDeclarationOfTheUsersOwn()
        {
            using var compilation = Compile(
                "@Obsolete(\"use work2\")\n"
                    + "public fun work(): void { }\n"
                    + "public fun run(): void { work(); }");

            AssertClean(compilation);
            AssertReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        [Fact]
        public void AnUnmarkedTwinOfAMarkedDeclarationStaysQuiet()
        {
            using var compilation = Compile(
                "public fun work(): void { }\n"
                    + "public fun run(): void { work(); }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        [Fact]
        public void TheWarningCarriesTheWrittenReason()
        {
            using var compilation = Compile(
                "@Obsolete(\"use moveTo(dx, dy)\")\n"
                    + "public fun move(x: float, y: float): void { }\n"
                    + "public fun run(): void { move(1.0, 2.0); }");

            Assert.True(
                compilation.Diagnostics.Any(d =>
                    d.Code == SurtrDiagnosticCode.ObsoleteMemberUsed
                    && d.Message.Contains("use moveTo(dx, dy)")),
                "Expected the reason text in: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        [Fact]
        public void AnObsoleteCallerIsNotNaggedAboutItsOwnKind()
        {
            using var compilation = Compile(
                "@Obsolete(\"use work2\")\n"
                    + "public fun work(): void { }\n"
                    + "@Obsolete(\"use run2\")\n"
                    + "public fun run(): void { work(); }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        [Fact]
        public void AnObsoleteMethodInsideAnObsoleteClassIsQuietInsideItToo()
        {
            using var compilation = Compile(
                "@Obsolete\n"
                    + "class Legacy {\n"
                    + "  public fun step(): int { return 1; }\n"
                    + "  public fun run(): int { return step(); }\n"
                    + "}\n"
                    + "public fun run(): int { return Legacy().run(); }");

            // The class's own body is silent; the outside caller of the class is what warns.
            Assert.Single(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        [Fact]
        public void ReadingAnObsoleteFieldWarnsWhereverItIsRead()
        {
            // Both reads warn: the one in another method of the declaring class, and the outer
            // one - the mark retires the field, not just its reach from outside.
            using var compilation = Compile(
                "class Box {\n"
                    + "  @Obsolete(\"read label instead\")\n"
                    + "  public let name: string = \"\";\n"
                    + "  public fun read(): string { return name; }\n"
                    + "}\n"
                    + "public fun run(): string { return Box().name; }");

            Assert.True(
                compilation.Diagnostics.Count(d => d.Code == SurtrDiagnosticCode.ObsoleteMemberUsed) == 2,
                "Expected both reads to warn: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        [Fact]
        public void ConstructingAnObsoleteClassWarnsAtTheConstructionSite()
        {
            using var compilation = Compile(
                "@Obsolete(\"switch to Fresh\")\n"
                    + "class Stale { }\n"
                    + "public fun run(): void { Stale(); }");

            Assert.True(
                compilation.Diagnostics.Any(d =>
                    d.Code == SurtrDiagnosticCode.ObsoleteMemberUsed
                    && d.Message.Contains("switch to Fresh")),
                "Expected the type's reason at the construction: "
                    + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        [Fact]
        public void WritingAnObsoletePropertyWarnsLikeReadingIt()
        {
            using var compilation = Compile(
                "class Gauge {\n"
                    + "  @Obsolete(\"set level directly\")\n"
                    + "  public level: int { get => 0; set { } }\n"
                    + "}\n"
                    + "public fun run(): void { Gauge().level = 3; }");

            AssertReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        [Fact]
        public void BetweenEquallyGoodOverloadsTheOneWithoutTheMarkWinsQuietly()
        {
            // Both overloads take nothing and default their own parameter, so §3.5's rules tie -
            // which is exactly where an @Obsolete mark is allowed to speak.
            using var compilation = Compile(
                "@Obsolete(\"use pick\")\n"
                    + "public fun pick(x: int = 1): int { return x; }\n"
                    + "public fun pick(y: float = 2.0): float { return y; }\n"
                    + "public fun run(): float { return pick(); }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        [Fact]
        public void AnObsoleteOverloadWithNothingToYieldToStillWarns()
        {
            using var compilation = Compile(
                "@Obsolete(\"pass the size\")\n"
                    + "public fun fill(): int { return 0; }\n"
                    + "public fun run(): int { return fill(); }");

            AssertReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        [Fact]
        public void TheMarkDoesNotStealACallTheArgumentsAlreadyDecided()
        {
            // pick(1) fits the int overload exactly and the float one only by widening, so §3.5
            // decides before any mark is consulted - the call keeps meaning the obsolete overload,
            // and says so with the warning rather than silently switching targets.
            using var compilation = Compile(
                "@Obsolete(\"use pickf\")\n"
                    + "public fun pick(x: int): int { return x; }\n"
                    + "public fun pick(y: float): float { return y; }\n"
                    + "public fun run(): int { return pick(1); }");

            AssertReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        #endregion

        #region @NoDiscard

        [Fact]
        public void ADroppedResultFromANoDiscardFunctionWarns()
        {
            using var compilation = Compile(
                "@NoDiscard(\"el Result indica si parseó\")\n"
                    + "public fun tryParse(text: string): bool { return true; }\n"
                    + "public fun load(): void { tryParse(\"x\"); }");

            Assert.True(
                compilation.Diagnostics.Any(d =>
                    d.Code == SurtrDiagnosticCode.NoDiscardResultUnused
                    && d.Message.Contains("el Result indica si parseó")),
                "Expected the reason in: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        [Fact]
        public void AnAssignedResultStaysQuiet()
        {
            using var compilation = Compile(
                "@NoDiscard\n"
                    + "public fun compute(): int { return 1; }\n"
                    + "public fun load(): int { let value = compute(); return value; }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.NoDiscardResultUnused);
        }

        [Fact]
        public void AResultFedIntoAnotherCallIsNotDropped()
        {
            using var compilation = Compile(
                "@NoDiscard\n"
                    + "public fun compute(): int { return 1; }\n"
                    + "public fun take(value: int): void { }\n"
                    + "public fun load(): void { take(compute()); }");

            AssertNoReports(compilation, SurtrDiagnosticCode.NoDiscardResultUnused);
        }

        [Fact]
        public void AnUnmarkedFunctionMayBeCalledForEffect()
        {
            using var compilation = Compile(
                "public fun compute(): int { return 1; }\n"
                    + "public fun load(): void { compute(); }");

            AssertClean(compilation);
        }

        [Fact]
        public void AVoidFunctionCarryingTheMarkNeverWarns()
        {
            // The mark on a void function has nothing to guard - there is no result to drop -
            // so the use site stays silent rather than nagging about nothing.
            using var compilation = Compile(
                "@NoDiscard\n"
                    + "public fun flush(): void { }\n"
                    + "public fun load(): void { flush(); }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.NoDiscardResultUnused);
        }

        [Fact]
        public void ADroppedResultWithoutAReasonStillSaysWhatToDo()
        {
            using var compilation = Compile(
                "@NoDiscard\n"
                    + "public fun compute(): int { return 1; }\n"
                    + "public fun load(): void { compute(); }");

            Assert.True(
                compilation.Diagnostics.Any(d =>
                    d.Code == SurtrDiagnosticCode.NoDiscardResultUnused
                    && d.Message.Contains("assign it")),
                "Expected guidance in: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        [Fact]
        public void ACallInsideAChainHasItsValueUsed()
        {
            using var compilation = Compile(
                "@NoDiscard\n"
                    + "public fun open(): Handle { return Handle(); }\n"
                    + "class Handle {\n"
                    + "  public fun close(): int { return 0; }\n"
                    + "}\n"
                    + "public fun load(): void { open().close(); }");

            AssertNoReports(compilation, SurtrDiagnosticCode.NoDiscardResultUnused);
        }

        #endregion
    }
}
