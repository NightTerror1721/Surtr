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

        #region @TestIgnore (§P9)

        [Fact]
        public void TestIgnoreIsRejectedOnAClass()
        {
            using var compilation = Compile(
                "@TestIgnore(\"later\")\n"
                    + "class Target { }");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeTargetMismatch);
        }

        [Fact]
        public void TestIgnoreIsRejectedOnAField()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @TestIgnore\n"
                    + "  public let n: int = 0;\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeTargetMismatch);
        }

        [Fact]
        public void TestIgnoreBesideTestIsAccepted()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @Test(\"one\")\n"
                    + "  @TestIgnore(\"flaky on CI\")\n"
                    + "  public fun first(): void { }\n"
                    + "}");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.IgnoreWithoutTest);
        }

        [Fact]
        public void ANonTextReasonCannotFillTestIgnore()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @Test\n"
                    + "  @TestIgnore(7)\n"
                    + "  public fun first(): void { }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeArgumentTypeMismatch);
        }

        [Fact]
        public void TestIgnoreWithoutTestWarnsBecauseThereIsNothingToSkip()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @TestIgnore(\"flaky\")\n"
                    + "  public fun notATest(): void { }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.IgnoreWithoutTest);
            Assert.False(compilation.HasErrors, "The mark is a warning, not an error.");
        }

        [Fact]
        public void AMethodWithNeitherMarkIsNeverAsked()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @Obsolete(\"old\")\n"
                    + "  public fun plain(): void { }\n"
                    + "}");

            AssertNoReports(compilation, SurtrDiagnosticCode.IgnoreWithoutTest);
        }

        #endregion

        #region @TestBefore/@TestAfter (§P10)

        [Fact]
        public void AFixtureIsRejectedOnAClass()
        {
            using var compilation = Compile(
                "@TestBefore\n"
                    + "class Target { }");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeTargetMismatch);
        }

        [Fact]
        public void AParameterlessVoidFixtureIsAccepted()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @TestBefore\n"
                    + "  public fun setUp(): void { }\n"
                    + "  @TestAfter\n"
                    + "  public static fun tearDown(): void { }\n"
                    + "}");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.InvalidTestFixture);
        }

        [Fact]
        public void AFixtureThatIsAlsoATestIsReported()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @Test\n"
                    + "  @TestBefore\n"
                    + "  public fun both(): void { }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.InvalidTestFixture);
            Assert.False(compilation.HasErrors, "A mixed role is a warning, not an error.");
        }

        [Fact]
        public void AFixtureTakingParametersIsReportedBecauseNothingWouldFillThem()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @TestBefore\n"
                    + "  public fun setUp(seed: int): void { }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.InvalidTestFixture);
        }

        [Fact]
        public void AFixtureReturningAValueIsReportedBecauseNothingReadsIt()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @TestAfter\n"
                    + "  public fun tearDown(): int { return 1; }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.InvalidTestFixture);
        }

        [Fact]
        public void AModuleLevelFixtureIsAccepted()
        {
            using var compilation = Compile(
                "@TestBefore\n"
                    + "public fun setUp(): void { }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.InvalidTestFixture);
        }

        #endregion

        #region @Benchmark (§P11)

        [Fact]
        public void BenchmarkIsRejectedOnAField()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @Benchmark\n"
                    + "  public let n: int = 0;\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeTargetMismatch);
        }

        [Fact]
        public void BenchmarkIsRejectedOnAClass()
        {
            using var compilation = Compile(
                "@Benchmark\n"
                    + "class Target { }");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeTargetMismatch);
        }

        [Fact]
        public void BenchmarkOnAMethodIsAccepted()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @Benchmark\n"
                    + "  public fun work(): void { }\n"
                    + "}");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.BenchmarkWithTest);
        }

        [Fact]
        public void AMethodThatIsBothATestAndABenchmarkIsReported()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @Test\n"
                    + "  @Benchmark\n"
                    + "  public fun both(): void { }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.BenchmarkWithTest);
            Assert.False(compilation.HasErrors, "A mixed role is a warning, not an error.");
        }

        #endregion

        #region @Throws (§P12)

        [Fact]
        public void ThrowsIsRejectedOnAField()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @Throws(\"ArgumentException\")\n"
                    + "  public let n: int = 0;\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeTargetMismatch);
        }

        [Fact]
        public void ThrowsNamingALibraryExceptionIsQuiet()
        {
            using var compilation = Compile(
                "@Throws(\"ArgumentException\")\n"
                    + "public fun parse(text: string): int { return 0; }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.ThrowsTypeNotException);
        }

        [Fact]
        public void ThrowsNamingTheRootItselfIsQuiet()
        {
            using var compilation = Compile(
                "@Throws(\"Exception\")\n"
                    + "public fun risky(): void { }");

            AssertNoReports(compilation, SurtrDiagnosticCode.ThrowsTypeNotException);
        }

        [Fact]
        public void ThrowsNamingAUserExceptionIsQuiet()
        {
            using var compilation = Compile(
                "public class ParseFailed : Exception {\n"
                    + "  public constructor(message: string) : super(message) { }\n"
                    + "}\n"
                    + "@Throws(\"ParseFailed\")\n"
                    + "public fun parse(): void { }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.ThrowsTypeNotException);
        }

        [Fact]
        public void ThrowsNamingSomethingThatIsNotATypeWarns()
        {
            using var compilation = Compile(
                "@Throws(\"NoSuchException\")\n"
                    + "public fun risky(): void { }");

            AssertReports(compilation, SurtrDiagnosticCode.ThrowsTypeNotException);
            Assert.False(compilation.HasErrors, "A stale name is documentation gone bad, not a build failure.");
        }

        [Fact]
        public void ThrowsNamingATypeThatIsNoExceptionWarns()
        {
            using var compilation = Compile(
                "public class Vec2 { }\n"
                    + "@Throws(\"Vec2\")\n"
                    + "public fun risky(): void { }");

            AssertReports(compilation, SurtrDiagnosticCode.ThrowsTypeNotException);
        }

        [Fact]
        public void TwoThrowsOnOneDeclarationAreBothRecorded()
        {
            using var compilation = Compile(
                "@Throws(\"ArgumentException\")\n"
                    + "@Throws(\"FormatException\")\n"
                    + "public fun parse(): void { }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.ThrowsTypeNotException);
        }

        [Fact]
        public void OnlyTheStaleOneOfTwoThrowsIsReported()
        {
            using var compilation = Compile(
                "@Throws(\"ArgumentException\")\n"
                    + "@Throws(\"Gone\")\n"
                    + "public fun parse(): void { }");

            Assert.Single(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.ThrowsTypeNotException);
        }

        #endregion

        #region @NoAlloc (§P13)

        [Fact]
        public void NoAllocIsRejectedOnAClass()
        {
            using var compilation = Compile(
                "@NoAlloc\n"
                    + "class Target { }");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeTargetMismatch);
        }

        [Fact]
        public void NoAllocIsAcceptedOnAProperty()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  private var n: int = 0;\n"
                    + "  @NoAlloc\n"
                    + "  public doubled: int { get { return n * 2; } }\n"
                    + "}");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.AllocationInNoAllocBody);
        }

        [Fact]
        public void AnArithmeticBodyKeepsItsNoAllocPromiseQuietly()
        {
            using var compilation = Compile(
                "@NoAlloc\n"
                    + "public fun lerp(a: float, b: float, t: float): float { return a + (b - a) * t; }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.AllocationInNoAllocBody);
        }

        [Fact]
        public void ConstructingAClassInANoAllocBodyIsReported()
        {
            using var compilation = Compile(
                "public class Node { }\n"
                    + "@NoAlloc\n"
                    + "public fun build(): Node { return Node(); }");

            AssertReports(compilation, SurtrDiagnosticCode.AllocationInNoAllocBody);
            Assert.False(compilation.HasErrors, "The mark is a contract, so a violation is a warning.");
        }

        [Fact]
        public void AnArrayLiteralInANoAllocBodyIsReported()
        {
            using var compilation = Compile(
                "@NoAlloc\n"
                    + "public fun three(): int[] { return [1, 2, 3]; }");

            AssertReports(compilation, SurtrDiagnosticCode.AllocationInNoAllocBody);
        }

        [Fact]
        public void ADictLiteralInANoAllocBodyIsReported()
        {
            using var compilation = Compile(
                "@NoAlloc\n"
                    + "public fun table(): {int: int} { return {1: 2}; }");

            AssertReports(compilation, SurtrDiagnosticCode.AllocationInNoAllocBody);
        }

        [Fact]
        public void StringInterpolationInANoAllocBodyIsReported()
        {
            using var compilation = Compile(
                "@NoAlloc\n"
                    + "public fun label(n: int): string { return \"n = ${n}\"; }");

            AssertReports(compilation, SurtrDiagnosticCode.AllocationInNoAllocBody);
        }

        [Fact]
        public void StringConcatenationInANoAllocBodyIsReported()
        {
            using var compilation = Compile(
                "@NoAlloc\n"
                    + "public fun label(n: int): string { return \"n = \" + n; }");

            AssertReports(compilation, SurtrDiagnosticCode.AllocationInNoAllocBody);
        }

        [Fact]
        public void ALambdaInANoAllocBodyIsReported()
        {
            using var compilation = Compile(
                "@NoAlloc\n"
                    + "public fun make(): (int) -> int { return (x: int) => x + 1; }");

            AssertReports(compilation, SurtrDiagnosticCode.AllocationInNoAllocBody);
        }

        /// <summary>
        /// A tuple is a value type, laid out inline (§2.9), so a body that only builds one keeps
        /// its promise — the stated v1 limit, and the reason the check reads the bound tree rather
        /// than the syntax.
        /// </summary>
        [Fact]
        public void ATupleInANoAllocBodyIsAllowedBecauseItIsAValueType()
        {
            using var compilation = Compile(
                "@NoAlloc\n"
                    + "public fun pair(a: int, b: int): (int, int) { return (a, b); }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.AllocationInNoAllocBody);
        }

        /// <summary>
        /// The other stated limit: a call is not followed, so an allocation inside the callee is
        /// invisible here. Left explicit so the silence is a documented choice rather than a hole
        /// nobody noticed.
        /// </summary>
        [Fact]
        public void ACallIsNotFollowedIntoTheCallee()
        {
            using var compilation = Compile(
                "public fun allocates(): int[] { return [1]; }\n"
                    + "@NoAlloc\n"
                    + "public fun caller(): int[] { return allocates(); }");

            AssertNoReports(compilation, SurtrDiagnosticCode.AllocationInNoAllocBody);
        }

        [Fact]
        public void AnUnmarkedBodyIsNeverWalked()
        {
            using var compilation = Compile(
                "public fun three(): int[] { return [1, 2, 3]; }");

            AssertNoReports(compilation, SurtrDiagnosticCode.AllocationInNoAllocBody);
        }

        [Fact]
        public void EveryAllocatingConstructInOneBodyIsReportedSeparately()
        {
            using var compilation = Compile(
                "@NoAlloc\n"
                    + "public fun several(): int {\n"
                    + "  let xs: int[] = [1];\n"
                    + "  let label: string = \"n = ${xs.length}\";\n"
                    + "  return xs.length + label.length;\n"
                    + "}");

            Assert.Equal(
                2,
                compilation.Diagnostics.Count(d => d.Code == SurtrDiagnosticCode.AllocationInNoAllocBody));
        }

        #endregion

        #region @Flags (§P14)

        [Fact]
        public void FlagsIsRejectedOnAClass()
        {
            using var compilation = Compile(
                "@Flags\n"
                    + "class Target { }");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeTargetMismatch);
        }

        [Fact]
        public void FlagsIsRejectedOnAMethod()
        {
            using var compilation = Compile(
                "class Target {\n"
                    + "  @Flags\n"
                    + "  public fun run(): void { }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.AttributeTargetMismatch);
        }

        [Fact]
        public void APlainFlagsEnumIsAccepted()
        {
            using var compilation = Compile(
                "@Flags\n"
                    + "enum Perm { Read, Write, Execute }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.InvalidFlagsEnum);
        }

        /// <summary>
        /// The representation is what refuses these (§P14): a <c>@Flags</c> value is one int, so
        /// there is no instance for a member to run on, no receiver for an interface to dispatch
        /// through, and no case for a constructor argument to build.
        /// </summary>
        [Fact]
        public void AFlagsEnumDeclaringAMemberIsRejected()
        {
            using var compilation = Compile(
                "@Flags\n"
                    + "enum Perm { Read, Write;\n"
                    + "  public fun describe(): string { return \"x\"; }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.InvalidFlagsEnum);
        }

        [Fact]
        public void AFlagsEnumImplementingAnInterfaceIsRejected()
        {
            using var compilation = Compile(
                "public interface INamed { fun describe(): string; }\n"
                    + "@Flags\n"
                    + "enum Perm : INamed { Read, Write;\n"
                    + "  public fun describe(): string { return \"x\"; }\n"
                    + "}");

            AssertReports(compilation, SurtrDiagnosticCode.InvalidFlagsEnum);
        }

        [Fact]
        public void AFlagsCaseWithConstructorArgumentsIsRejected()
        {
            using var compilation = Compile(
                "@Flags\n"
                    + "enum Perm { Read(1), Write(2) }");

            AssertReports(compilation, SurtrDiagnosticCode.InvalidFlagsEnum);
        }

        [Fact]
        public void AFlagsEnumWithMoreCasesThanBitsIsRejected()
        {
            var cases = new System.Text.StringBuilder();
            for (int i = 0; i < 32; i++)
                cases.Append(i == 0 ? "F0" : ", F" + i);

            using var compilation = Compile(
                "@Flags\n"
                    + "enum Wide { " + cases + " }");

            AssertReports(compilation, SurtrDiagnosticCode.InvalidFlagsEnum);
        }

        /// <summary>
        /// The 31-case ceiling is about implicit bits — a case past it would shift past an int. An
        /// explicit value may repeat a bit, which is exactly what lets a flags enum name more than
        /// 31 cases (§2.1).
        /// </summary>
        [Fact]
        public void AFlagsEnumWithExplicitValuesMayRepeatBitsPastTheThirtyFirstCase()
        {
            var cases = new System.Text.StringBuilder();
            for (int i = 0; i < 40; i++)
                cases.Append(i == 0 ? "F0 = " + (1 << (i % 5)) : ", F" + i + " = " + (1 << (i % 5)));

            using var compilation = Compile(
                "@Flags\n"
                    + "enum Wide { " + cases + " }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.InvalidFlagsEnum);
        }

        [Fact]
        public void CombiningTwoDifferentFlagsEnumsIsRejected()
        {
            using var compilation = Compile(
                "@Flags\n"
                    + "enum Perm { Read, Write }\n"
                    + "@Flags\n"
                    + "enum Slot { Head, Chest }\n"
                    + "public fun mix(): int { return (Perm.Read | Slot.Head) as int; }");

            Assert.True(compilation.HasErrors, "Two flag sets share no bit meanings, so a combination of them belongs to neither.");
        }

        [Fact]
        public void CombiningAnUnmarkedEnumIsStillRejected()
        {
            using var compilation = Compile(
                "enum Color { Red, Green }\n"
                    + "public fun mix(): Color { return Color.Red | Color.Green; }");

            Assert.True(compilation.HasErrors, "Without the mark a case is an instance, and instances do not combine.");
        }

        [Fact]
        public void AnIntDoesNotReachAFlagsEnumWithoutACast()
        {
            using var compilation = Compile(
                "@Flags\n"
                    + "enum Perm { Read, Write }\n"
                    + "public fun bad(): Perm { return 3; }");

            Assert.True(compilation.HasErrors, "An arbitrary int is not a combination of the enum's cases.");
        }

        /// <summary>
        /// From the migration a <c>@Flags</c> enum is a nominal value class (§2.4), so
        /// <c>apply(Perm)</c> and <c>apply(int)</c> are two real method table slots — the old
        /// erasure that made them collide is gone.
        /// </summary>
        [Fact]
        public void AnOverloadDifferingByAFlagsEnumAndIntIsAllowed()
        {
            using var compilation = Compile(
                "@Flags\n"
                    + "enum Perm { Read, Write }\n"
                    + "public fun apply(p: Perm): void { }\n"
                    + "public fun apply(n: int): void { }");

            AssertClean(compilation);
            AssertNoReports(compilation, SurtrDiagnosticCode.DuplicateOverload);
        }

        /// <summary>Non-regression: the int and bool arms of the bitwise block are untouched.</summary>
        [Fact]
        public void TheOrdinaryBitwiseOperandsStillResolve()
        {
            using var compilation = Compile(
                "public fun ints(a: int, b: int): int { return (a & b) | (a ^ b) | (a << 1) | (a >> 1) | (a >>> 1); }\n"
                    + "public fun bools(a: bool, b: bool): bool { return (a & b) | (a ^ b); }\n"
                    + "public fun complement(a: int): int { return ~a; }");

            AssertClean(compilation);
        }

        #endregion
    }
}
