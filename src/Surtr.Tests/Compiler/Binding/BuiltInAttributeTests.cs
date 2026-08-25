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
            // On a method, so the target check passes and the argument itself is what is wrong.
            using var compilation = Compile(
                "class Target {\n"
                    + "  @NoDiscard(7)\n"
                    + "  public fun run(): void { }\n"
                    + "}");

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

        [Fact]
        public void AnObsoleteClassWrittenAsAnAnnotationWarns()
        {
            using var compilation = Compile(
                "@Obsolete(\"use Fresh\")\n"
                    + "class Old { }\n"
                    + "public fun run(): void { let x: Old? = null; }");

            AssertReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        [Fact]
        public void AnObsoleteClassAsACastOrTypeTestTargetWarns()
        {
            using var compilation = Compile(
                "@Obsolete\n"
                    + "class Old { }\n"
                    + "public fun cast(o: unknown): unknown { return o as Old; }\n"
                    + "public fun test(o: unknown): bool { return o is Old; }");

            Assert.Equal(2, compilation.Diagnostics.Count(d => d.Code == SurtrDiagnosticCode.ObsoleteMemberUsed));
        }

        [Fact]
        public void AnObsoleteTypeArgumentWarns()
        {
            using var compilation = Compile(
                "@Obsolete\n"
                    + "class Old { }\n"
                    + "class Box<T> { public fun n(): int { return 1; } }\n"
                    + "public fun run(): int { return Box<Old>().n(); }");

            AssertReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        [Fact]
        public void AnObsoleteTypeUseInsideAnObsoleteDeclarationStaysQuiet()
        {
            using var compilation = Compile(
                "@Obsolete\n"
                    + "class Old { }\n"
                    + "@Obsolete\n"
                    + "class Migrating {\n"
                    + "  public fun run(): void { let x: Old? = null; }\n"
                    + "}");

            AssertNoReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        [Fact]
        public void DerivingFromAnObsoleteBaseWarns()
        {
            using var compilation = Compile(
                "@Obsolete(\"extend New instead\")\n"
                    + "class Base { }\n"
                    + "class Derived : Base { }");

            AssertReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        [Fact]
        public void DerivingFromAnObsoleteBaseIsQuietWhenTheDerivedIsObsoleteToo()
        {
            using var compilation = Compile(
                "@Obsolete\n"
                    + "class Base { }\n"
                    + "@Obsolete\n"
                    + "class Derived : Base { }");

            AssertNoReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        [Fact]
        public void AnObsoleteFieldTypeInADeclarationWarns()
        {
            using var compilation = Compile(
                "@Obsolete\n"
                    + "class Old { }\n"
                    + "class Holder {\n"
                    + "  public let o: Old? = null;\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        [Fact]
        public void AnObsoleteReturnTypeInADeclarationWarns()
        {
            using var compilation = Compile(
                "@Obsolete\n"
                    + "class Old { }\n"
                    + "class Maker {\n"
                    + "  public fun make(): Old { return Old(); }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.ObsoleteMemberUsed);
        }

        #endregion

        #region Built-in targets

        [Fact]
        public void RangeIsRejectedOnAMethod()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @Range(0, 100)\n"
                    + "  public fun health(): float { return 100.0; }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeTargetMismatch);
        }

        [Fact]
        public void RangeIsAcceptedOnAFloatFieldWithIntegerBounds()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @Range(0, 100)\n"
                    + "  public var health: float = 100.0;\n"
                    + "}");

            AssertClean(compilation);
        }

        [Fact]
        public void TestIsRejectedOnAFieldButAcceptedOnAMethod()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @Test\n"
                    + "  public let n: int = 0;\n"
                    + "}\n"
                    + "class Other {\n"
                    + "  @Test(\"works\")\n"
                    + "  public fun run(): void { }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeTargetMismatch);
        }

        [Fact]
        public void ValueIsRejectedOnAMethod()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @Value\n"
                    + "  public fun thing(): int { return 1; }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeTargetMismatch);
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

        #region @Pure (§P3)

        [Fact]
        public void APureBodyThatOnlyCallsPureFunctionsStaysQuiet()
        {
            using var compilation = Compile(
                "@Pure\n"
                    + "public fun helper(x: int): int { return x * 2; }\n"
                    + "@Pure\n"
                    + "public fun run(x: int): int { return helper(x) + 1; }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.PureContractViolated);
        }

        [Fact]
        public void APureBodyCallingAPureNativeBuiltInStaysQuiet()
        {
            using var compilation = Compile(
                "@Pure\n"
                    + "public fun first(s: string): char { return s.charAt(0); }\n"
                    + "@Pure\n"
                    + "public fun pick(xs: int[], i: int): int { return xs.get(i); }\n"
                    + "@Pure\n"
                    + "public fun middle(s: string): string { return s.substring(1, 2); }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.PureContractViolated);
        }

        [Fact]
        public void APureBodyCallingAMutatingNativeBuiltInStillWarns()
        {
            using var compilation = Compile(
                "@Pure\n"
                    + "public fun grow(xs: int[]): void { xs.push(1); }");

            Assert.True(
                compilation.Diagnostics.Count(d => d.Code == SurtrDiagnosticCode.PureContractViolated) == 1,
                "A mutating native call should warn: "
                    + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        [Fact]
        public void APureBodyCallingReflectionNativesStaysQuiet()
        {
            using var compilation = Compile(
                "@Pure\n"
                    + "public fun typeName(): string { return Type.of(5).name; }\n"
                    + "@Pure\n"
                    + "public fun memberCount(): int { return Type.of(5).members().length; }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.PureContractViolated);
        }

        [Fact]
        public void APureBodyReadingAnIteratorCurrentStaysQuiet()
        {
            using var compilation = Compile(
                "@Pure\n"
                    + "public fun peek(it: IIterator<int>): int { return it.current; }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.PureContractViolated);
        }

        [Fact]
        public void APureBodyCallingMoveNextStillWarns()
        {
            using var compilation = Compile(
                "@Pure\n"
                    + "public fun advance(it: IIterator<int>): bool { return it.moveNext(); }");

            Assert.True(
                compilation.Diagnostics.Count(d => d.Code == SurtrDiagnosticCode.PureContractViolated) == 1,
                "moveNext mutates the cursor, so it should warn: "
                    + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        [Fact]
        public void APureBodyCallingAnUnmarkedFunctionWarns()
        {
            using var compilation = Compile(
                "@Pure\n"
                    + "public fun pure(x: int): int { return x * 2; }\n"
                    + "public fun impure(x: int): int { return x + 1; }\n"
                    + "@Pure\n"
                    + "public fun run(x: int): int { return pure(x) + impure(x); }");

            Assert.True(
                compilation.Diagnostics.Count(d => d.Code == SurtrDiagnosticCode.PureContractViolated) == 1,
                "Only the call to 'impure' should warn: "
                    + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        [Fact]
        public void APropertyReadInAPureBodyDoesNotWarn()
        {
            // Reading `obj.x` runs the getter, but a read is the shape @Pure exists to protect;
            // the contract check treats method calls as the impure half, not property reads.
            using var compilation = Compile(
                "class Box {\n"
                    + "  public let value: int = 1;\n"
                    + "}\n"
                    + "@Pure\n"
                    + "public fun run(b: Box): int { return b.value + 1; }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.PureContractViolated);
        }

        [Fact]
        public void APureBodyAssigningAPublicFieldWarns()
        {
            using var compilation = Compile(
                "class Counter {\n"
                    + "  public var count: int = 0;\n"
                    + "  @Pure\n"
                    + "  public fun nudge(): int { count = count + 1; return count; }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.PureContractViolated);
        }

        [Fact]
        public void APureBodyAssigningAPrivateFieldIsQuiet()
        {
            // The report's phase 2 scopes the mutation check to fields another scope can see.
            using var compilation = Compile(
                "class Counter {\n"
                    + "  private var hidden: int = 0;\n"
                    + "  @Pure\n"
                    + "  public fun peek(): int { hidden = 5; return hidden; }\n"
                    + "}");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.PureContractViolated);
        }

        [Fact]
        public void ANonPureBodyIsNeverChecked()
        {
            using var compilation = Compile(
                "public fun impure(x: int): int { return x + 1; }\n"
                    + "public fun run(x: int): int { return impure(x); }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.PureContractViolated);
        }

        [Fact]
        public void APureChainOfMarkedCallsStaysQuiet()
        {
            // The report's own example shape — clamp01 built from max/min — compiles clean when
            // every callee carries the mark, which is exactly how the standard library's pure
            // functions are declared (§P3).
            using var compilation = Compile(
                "@Pure\n"
                    + "public fun max(a: float, b: float): float { return a > b ? a : b; }\n"
                    + "@Pure\n"
                    + "public fun min(a: float, b: float): float { return a < b ? a : b; }\n"
                    + "@Pure\n"
                    + "public fun clamp01(x: float): float { return max(0.0, min(1.0, x)); }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.PureContractViolated);
        }

        #endregion
    }
}
