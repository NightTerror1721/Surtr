#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Tests.Compiler.CodeGen
{
    /// <summary>
    /// Pins the instructions the emitter chooses, where the choice is the point.
    /// </summary>
    /// <remarks>
    /// <see cref="ModuleEmitterTests"/> deliberately stops at what a program computes rather than at
    /// how it is encoded, and that is right for nearly everything: the encoding is the emitter's to
    /// pick, and asserting on it pins a decision instead of a meaning. These are the cases where the
    /// decision <em>is</em> the meaning of the change. Reverting `StrCat` to pairwise, or a tuple
    /// index to a push, or `as?` to a spill and two type tests, leaves every answer identical and
    /// gives back everything the instruction was added for — so something has to notice.
    /// </remarks>
    public sealed class LoweringChoiceTests : IDisposable
    {
        private const string Root = "D:/proj/src";

        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
        }

        /// <summary>Compiles one module and renders its bytecode.</summary>
        private string Disassemble(string source)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            var compilation = SurtrCompilation.Create(project);
            _owned.Add(compilation);

            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(
                !compilation.HasErrors,
                "Binding reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var emitter = new ModuleEmitter(compilation, binder);

            Assert.True(
                emitter.TryEmit(),
                "Emission reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            return SurtrBytecodeDisassembler.Disassemble(emitter.Modules[0]);
        }

        private static int Count(string disassembly, string mnemonic)
            => disassembly
                .Split('\n')
                .Count(line => line.Trim().Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1)
                    .FirstOrDefault()?.Split(' ')[0] == mnemonic);

        #region String concatenation

        [Fact]
        public void AnInterpolationIsOneConcatenationOverAllItsParts()
        {
            // Four parts: a, "-", b, "!". Pairwise this would be three StrCats and two strings
            // nothing reads.
            string code = Disassemble("fun run(a: string, b: string): string { return \"${a}-${b}!\"; }");

            Assert.Equal(1, Count(code, "StrCat"));
            Assert.Contains("StrCat 4", code, StringComparison.Ordinal);
        }

        [Fact]
        public void AChainOfStringAddsIsOneConcatenation()
        {
            string code = Disassemble("fun run(a: string, b: string, c: string): string { return a + b + c; }");

            Assert.Equal(1, Count(code, "StrCat"));
            Assert.Contains("StrCat 3", code, StringComparison.Ordinal);
        }

        /// <summary>Parentheses regroup the tree and must not change what is emitted.</summary>
        [Fact]
        public void ARightNestedChainFlattensToo()
        {
            string code = Disassemble("fun run(a: string, b: string, c: string): string { return a + (b + c); }");

            Assert.Equal(1, Count(code, "StrCat"));
            Assert.Contains("StrCat 3", code, StringComparison.Ordinal);
        }

        #endregion

        #region Literals

        [Fact]
        public void BooleanAndCharacterLiteralsAreCarriedInline()
        {
            string code = Disassemble(
                "fun yes(): bool { return true; }\n"
                    + "fun no(): bool { return false; }\n"
                    + "fun letter(): char { return 'q'; }");

            Assert.Equal(1, Count(code, "PushTrue"));
            Assert.Equal(1, Count(code, "PushFalse"));
            Assert.Contains("PushChar 'q'", code, StringComparison.Ordinal);

            // The whole point of the inline forms: none of the three reaches the pool.
            Assert.Contains("constants: 0", code, StringComparison.Ordinal);
        }

        #endregion

        #region Tuples

        [Fact]
        public void ATupleIndexIsAnImmediateRatherThanAPush()
        {
            string code = Disassemble(
                "fun run(): string { let p: (int, string) = (3, \"origin\"); return p[1]; }");

            Assert.Equal(1, Count(code, "TupGetC"));
            Assert.Equal(0, Count(code, "TupGet"));
        }

        #endregion

        #region Safe casts

        [Fact]
        public void ASafeCastToAReferenceTypeIsOneTypeTest()
        {
            string code = Disassemble(
                "class Animal { }\nclass Dog : Animal { }\n"
                    + "fun run(a: Animal): Dog? { return a as? Dog; }");

            Assert.Equal(1, Count(code, "CastOrNull"));
            Assert.Equal(0, Count(code, "InstanceOf"));
            Assert.Equal(0, Count(code, "Cast"));
        }

        /// <summary>
        /// A safe cast to a primitive still needs the branch (§3.6's remark on <c>EmitSafeCast</c>:
        /// the success path unboxes, the failure path has nothing to unbox), but it fuses to
        /// <c>JPInstanceOf</c> now instead of materializing <c>InstanceOf</c>'s bool and testing it
        /// with a separate <c>JumpIfFalse</c>.
        /// </summary>
        [Fact]
        public void ASafeCastToAPrimitiveTypeFusesToJPInstanceOf()
        {
            string code = Disassemble("fun run(u: unknown): int? { return u as? int; }");

            Assert.Equal(1, Count(code, "JPInstanceOf"));
            Assert.Equal(0, Count(code, "InstanceOf"));
            Assert.Equal(0, Count(code, "JPZ"));
        }

        #endregion

        #region Null and instanceof checks

        /// <summary>
        /// `x == null` against a reference needs neither the null literal on the stack nor a
        /// two-operand comparison: <c>IsNull</c> reads the one operand's own tag (§5.1). Value
        /// correctness is <see cref="ModuleEmitterTests.ANullEqualityOnAReferenceComputesCorrectly"/>.
        /// </summary>
        [Fact]
        public void ANullEqualityOnAReferenceUsesIsNull()
        {
            string code = Disassemble(
                "class Box { }\nfun run(a: Box?): bool { return a == null; }");

            Assert.Equal(1, Count(code, "IsNull"));
            Assert.Equal(0, Count(code, "PushNull"));
            Assert.Equal(0, Count(code, "REQ"));
        }

        [Fact]
        public void ANullInequalityOnAReferenceUsesIsNotNull()
        {
            string code = Disassemble(
                "class Box { }\nfun run(a: Box?): bool { return a != null; }");

            Assert.Equal(1, Count(code, "IsNotNull"));
            Assert.Equal(0, Count(code, "PushNull"));
            Assert.Equal(0, Count(code, "RNE"));
        }

        /// <summary><c>===</c>/<c>!==</c> against <c>null</c> mean exactly the same thing as
        /// <c>==</c>/<c>!=</c> there - there is only one null to be identical to - so they fuse the
        /// same way.</summary>
        [Fact]
        public void AReferenceIdentityToNullUsesIsNull()
        {
            string code = Disassemble(
                "class Box { }\nfun run(a: Box?): bool { return a === null; }");

            Assert.Equal(1, Count(code, "IsNull"));
            Assert.Equal(0, Count(code, "JPREQ"));
        }

        /// <summary>
        /// A string is a reference too, and `s == null` should ask the tag directly rather than run
        /// `StrEQ`'s text comparison against an operand that is always null (null-safe, but doing
        /// more work than asking the tag).
        /// </summary>
        [Fact]
        public void AStringNullCheckUsesIsNullNotStrEQ()
        {
            string code = Disassemble("fun run(s: string?): bool { return s == null; }");

            Assert.Equal(1, Count(code, "IsNull"));
            Assert.Equal(0, Count(code, "StrEQ"));
        }

        /// <summary>
        /// The branch-condition twin of <see cref="ANullEqualityOnAReferenceUsesIsNull"/>: used as
        /// an `if`, the comparison fuses straight into `JPNN`/`JPN` (the family that keeps the
        /// boolean off the stack entirely), not `PushNull` + a reference comparison, fused or not.
        /// Value correctness is
        /// <see cref="ModuleEmitterTests.ANullEqualityBranchComputesCorrectly"/>.
        /// </summary>
        [Fact]
        public void ANullEqualityBranchFusesToJPNN()
        {
            string code = Disassemble(
                "class Box { }\nfun run(a: Box?): int { if (a == null) { return 1; } return 0; }");

            Assert.Equal(1, Count(code, "JPNN"));
            Assert.Equal(0, Count(code, "PushNull"));
            Assert.Equal(0, Count(code, "IsNull"));
            Assert.Equal(0, Count(code, "JPZ"));
        }

        [Fact]
        public void ANullInequalityBranchFusesToJPN()
        {
            string code = Disassemble(
                "class Box { }\nfun run(a: Box?): int { if (a != null) { return 1; } return 0; }");

            Assert.Equal(1, Count(code, "JPN"));
            Assert.Equal(0, Count(code, "PushNull"));
        }

        /// <summary>
        /// A nullable *primitive*'s absence is a tag, not a null reference (§5.1), so its branch
        /// fuses to `JPA`/`JPNA` instead - the branch-condition twin of `TryEmitAbsenceTest`
        /// (`IsAbsent`/`IsPresent`), which already covered the value-producing case. Value
        /// correctness is
        /// <see cref="ModuleEmitterTests.ANullablePrimitiveNullCheckBranchComputesCorrectly"/>.
        /// </summary>
        [Fact]
        public void ANullablePrimitiveEqualityBranchFusesToJPNA()
        {
            string code = Disassemble(
                "fun run(n: int?): int { if (n == null) { return 1; } return 0; }");

            Assert.Equal(1, Count(code, "JPNA"));
            Assert.Equal(0, Count(code, "IsAbsent"));
            Assert.Equal(0, Count(code, "JPZ"));
            Assert.Equal(0, Count(code, "PushAbsent"));
        }

        /// <summary>
        /// `x is T` used as a branch condition fuses to `JPInstanceOf` instead of materializing
        /// `InstanceOf`'s bool and testing it with `JumpIfFalse`. Value correctness is
        /// <see cref="ModuleEmitterTests.AnInstanceOfBranchComputesCorrectly"/>.
        /// </summary>
        [Fact]
        public void AnInstanceOfBranchFusesToJPInstanceOf()
        {
            string code = Disassemble(
                "class Animal { }\nclass Dog : Animal { }\n"
                    + "fun run(a: Animal): int { if (a is Dog) { return 1; } return 0; }");

            Assert.Equal(1, Count(code, "JPInstanceOf"));
            Assert.Equal(0, Count(code, "InstanceOf"));
            Assert.Equal(0, Count(code, "JPZ"));
        }

        #endregion

        #region Counted loops

        [Fact]
        public void AnIncrementInStatementPositionUpdatesTheLocalInPlace()
        {
            string code = Disassemble(
                "fun run(): int { var i: int = 0; while (i < 10) { i = i + 1; } return i; }");

            Assert.Equal(1, Count(code, "IncLocal"));
        }

        [Fact]
        public void ACompoundSubtractionIsTheSameInstruction()
        {
            string code = Disassemble(
                "fun run(): int { var i: int = 10; while (i > 0) { i -= 2; } return i; }");

            Assert.Contains("by -2", code, StringComparison.Ordinal);
        }

        /// <summary>
        /// An assignment whose value something reads has to leave it behind, and `IncLocal` leaves
        /// nothing — so the peephole has to decline rather than lose the value.
        /// </summary>
        [Fact]
        public void AnIncrementWhoseValueIsReadKeepsTheLongForm()
        {
            string code = Disassemble(
                "fun run(): int { var i: int = 0; var j: int = (i = i + 1); return i + j; }");

            Assert.Equal(0, Count(code, "IncLocal"));
        }

        /// <summary>
        /// Prefix and postfix differ only in which value they leave behind, and a discarded one
        /// leaves neither — so a `for` step and a bare `i++;` are the same instruction.
        /// </summary>
        [Theory]
        [InlineData("for (var i: int = 0; i < 10; i++) { }")]
        [InlineData("for (var i: int = 0; i < 10; ++i) { }")]
        [InlineData("var i: int = 0; while (i < 10) { i++; }")]
        public void ADiscardedIncrementIsOneInstruction(string body)
        {
            string code = Disassemble("fun run(): int { " + body + " return 0; }");

            Assert.Equal(1, Count(code, "IncLocal"));
            Assert.Contains("by 1", code, StringComparison.Ordinal);
        }

        /// <summary>A `++` whose value is read still has to produce one.</summary>
        [Fact]
        public void AnIncrementWhoseValueIsReadStaysTheLongForm()
        {
            string code = Disassemble("fun run(): int { var i: int = 0; return i++; }");

            Assert.Equal(0, Count(code, "IncLocal"));
        }

        [Fact]
        public void AForInOverAnArrayStepsWithTheFusedInstruction()
        {
            string code = Disassemble(
                "fun run(xs: int[]): int { var total: int = 0; for (x in xs) { total = total + x; } return total; }");

            // The loop counter's step. `total = total + x` is not one: its right side is no constant.
            Assert.Equal(1, Count(code, "IncLocal"));
        }

        #endregion

        #region Dictionary operations

        /// <summary>
        /// A member on the built-in <c>dict</c> with a dedicated opcode of identical semantics is
        /// lowered to that opcode rather than to a call of its native body — so <c>m.length</c> is
        /// one <c>DictLen</c>, not a getter call through a native frame.
        /// </summary>
        [Fact]
        public void ALengthOnADictionaryIsDictLen()
        {
            string code = Disassemble(
                "fun run(m: {int: int}): int { return m.length; }");

            Assert.Equal(1, Count(code, "DictLen"));
            Assert.Equal(0, Count(code, "CallModule"));
        }

        [Fact]
        public void AContainsKeyOnADictionaryIsDictIn()
        {
            string code = Disassemble(
                "fun run(m: {int: int}, k: int): bool { return m.containsKey(k); }");

            Assert.Equal(1, Count(code, "DictIn"));
        }

        [Fact]
        public void ARemoveOnADictionaryIsDictDel()
        {
            string code = Disassemble(
                "fun run(m: {int: int}, k: int): bool { return m.remove(k); }");

            Assert.Equal(1, Count(code, "DictDel"));
        }

        [Fact]
        public void AClearOnADictionaryIsDictClear()
        {
            string code = Disassemble(
                "fun run(m: {int: int}): void { m.clear(); }");

            Assert.Equal(1, Count(code, "DictClear"));
        }

        [Fact]
        public void AValuesOnADictionaryIsDictValues()
        {
            string code = Disassemble(
                "fun run(m: {int: string}): string[] { return m.values(); }");

            Assert.Equal(1, Count(code, "DictValues"));
        }

        [Fact]
        public void AKeysOnADictionaryIsDictKeys()
        {
            string code = Disassemble(
                "fun run(m: {int: string}): int[] { return m.keys(); }");

            Assert.Equal(1, Count(code, "DictKeys"));
        }

        /// <summary>
        /// A <c>containsKey</c> or <c>remove</c> in statement position has its bool discarded, but
        /// the value is still produced by <c>DictIn</c>/<c>DictDel</c> — so a pop has to follow it
        /// for the statement to leave the stack as it found it.
        /// </summary>
        [Fact]
        public void ADiscardedContainsKeyStillPopsTheBool()
        {
            string code = Disassemble(
                "fun run(m: {int: int}, k: int): void { m.containsKey(k); }");

            Assert.Equal(1, Count(code, "DictIn"));
        }

        /// <summary>A user class that declares its own <c>length</c> is not the dictionary's.</summary>
        [Fact]
        public void AUserLengthIsNotLowered()
        {
            string code = Disassemble(
                "class Box { public var n: int; public fun length(): int { return this.n; } }\n"
                    + "fun run(b: Box): int { return b.length(); }");

            Assert.Equal(0, Count(code, "DictLen"));
        }

        #endregion

        #region Fused comparison branches
        // A branch skips the `then`/loop body when the *written* condition is false, so the fused
        // opcode always tests the condition's negation (`a == b` skips on JPNE, `a < b` skips on
        // JPGE, and so on) — one instruction doing what `Compare` + `JumpIfFalse` did in two.

        [Fact]
        public void AnIfOnAPlainComparisonFusesIntoOneBranch()
        {
            string code = Disassemble("fun run(a: int, b: int): int { if (a == b) { return 1; } return 0; }");

            Assert.Equal(1, Count(code, "JPNE"));
            Assert.Equal(0, Count(code, "EQ"));
            Assert.Equal(0, Count(code, "NE"));
            Assert.Equal(0, Count(code, "JPZ"));
        }

        [Fact]
        public void AWhileOnAPlainComparisonFuses()
        {
            string code = Disassemble(
                "fun run(): int { var i: int = 0; while (i < 10) { i = i + 1; } return i; }");

            Assert.Equal(1, Count(code, "JPGE"));
            Assert.Equal(0, Count(code, "LT"));
            Assert.Equal(0, Count(code, "GE"));
        }

        [Fact]
        public void AThreeClauseForOnAPlainComparisonFuses()
        {
            string code = Disassemble(
                "fun run(): int { var n: int = 0; for (var i: int = 0; i < 5; i = i + 1) { n = n + i; } return n; }");

            Assert.Equal(1, Count(code, "JPGE"));
            Assert.Equal(0, Count(code, "LT"));
        }

        [Fact]
        public void AFloatComparisonFusesToTheFloatFamily()
        {
            string code = Disassemble("fun run(a: float, b: float): int { if (a < b) { return 1; } return 0; }");

            Assert.Equal(1, Count(code, "JPFGE"));
            Assert.Equal(0, Count(code, "FLT"));
        }

        [Fact]
        public void AStringEqualityFuses()
        {
            string code = Disassemble("fun run(a: string, b: string): int { if (a == b) { return 1; } return 0; }");

            Assert.Equal(1, Count(code, "JPStrNE"));
        }

        /// <summary>String *ordering* lowers to `compareTo`, which has no fused branch of its own.</summary>
        [Fact]
        public void AStringOrderingDoesNotFuse()
        {
            string code = Disassemble("fun run(a: string, b: string): int { if (a < b) { return 1; } return 0; }");

            Assert.Equal(0, Count(code, "JPStrLT"));
            Assert.Equal(0, Count(code, "JPStrGE"));
            Assert.Equal(1, Count(code, "JPZ"));
        }

        [Fact]
        public void AReferenceEqualityFusesToTheReferenceFamily()
        {
            string code = Disassemble(
                "class Box { }\nfun run(a: Box, b: Box): int { if (a === b) { return 1; } return 0; }");

            Assert.Equal(1, Count(code, "JPRNE"));
        }

        /// <summary>A class with no `operator==` still compares by identity (Fix 1), and that fuses too.</summary>
        [Fact]
        public void PlainEqualityOnAReferenceTypeFuses()
        {
            string code = Disassemble(
                "class Box { }\nfun run(a: Box, b: Box): int { if (a == b) { return 1; } return 0; }");

            Assert.Equal(1, Count(code, "JPRNE"));
        }

        /// <summary>A compound condition never was one comparison to begin with, so it stays unfused.</summary>
        [Fact]
        public void ACompoundConditionDoesNotFuse()
        {
            string code = Disassemble(
                "fun run(a: int, b: int, c: bool): int { if (a == b && c) { return 1; } return 0; }");

            Assert.Equal(0, Count(code, "JPEQ"));
            Assert.Equal(0, Count(code, "JPNE"));
            Assert.Equal(1, Count(code, "EQ"));
        }

        /// <summary>
        /// A declared `operator==` is a call by the time this reaches codegen (Fix 1), not a
        /// comparison — nothing here has to know that to avoid fusing it; it simply is not the
        /// shape this fusion recognises.
        /// </summary>
        [Fact]
        public void AUserEqualityOperatorDoesNotFuse()
        {
            string code = Disassemble(
                "class Vec2 {\n"
                    + "  public let x: int;\n"
                    + "  constructor(x: int) { this.x = x; }\n"
                    + "  operator==(a: Vec2, b: Vec2): bool { return a.x == b.x; }\n"
                    + "}\n"
                    + "fun run(a: Vec2, b: Vec2): int { if (a == b) { return 1; } return 0; }");

            Assert.Equal(0, Count(code, "JPREQ"));
            Assert.Equal(0, Count(code, "JPRNE"));
            Assert.Equal(1, Count(code, "JPZ"));
        }

        /// <summary>
        /// A user `operator&lt;=&gt;` stays a call — nothing here has to know it exists to leave it
        /// alone. What *does* fuse is the zero-comparison Fix 5 wraps its `int` result in (`&lt;` is
        /// `compareTo(...) &lt; 0`), which is an ordinary integer comparison like any other by the
        /// time it reaches here — a small bonus from the two fixes composing rather than something
        /// this one had to special-case.
        /// </summary>
        [Fact]
        public void AUserSpaceshipCallDoesNotFuseButItsZeroComparisonDoes()
        {
            string code = Disassemble(
                "class Score {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "  operator<=>(a: Score, b: Score): int { return a.value - b.value; }\n"
                    + "}\n"
                    + "fun run(a: Score, b: Score): int { if (a < b) { return 1; } return 0; }");

            // `<` negates to `>=` against zero, fused into one branch off the call's own result.
            Assert.Equal(1, Count(code, "JPGE"));
            Assert.Equal(0, Count(code, "JPZ"));
        }

        /// <summary>`!=` fuses to `JPEQ` — the negation of what was written, same as every other case.</summary>
        [Fact]
        public void NotEqualFusesToEqualsOpcode()
        {
            string code = Disassemble("fun run(a: int, b: int): int { if (a != b) { return 1; } return 0; }");

            Assert.Equal(1, Count(code, "JPEQ"));
            Assert.Equal(0, Count(code, "JPNE"));
        }

        #endregion

        #region Inline splicing (§3.6)

        /// <summary>
        /// A spliced body whose only statement is a single `return` needs no exit label and no
        /// `$inlineResult` local: nothing else in the body could jump past it, so before this fix
        /// the splice still paid for a trivial `JP` to the very next instruction and a store
        /// immediately followed by its own reload. Value correctness for this exact shape is
        /// <see cref="ModuleEmitterTests.AnInlineFunctionIsSplicedIntoItsCallSite"/>.
        /// </summary>
        [Fact]
        public void ASingleStatementInlineBodyEmitsNoTrivialJump()
        {
            string code = Disassemble(
                "inline fun double(x: int): int { return x * 2; }\n"
                    + "fun run(a: int): int { return double(a) + 1; }");

            Assert.Equal(0, Count(code, "JP"));
            Assert.Equal(0, Count(code, "JPX"));
        }

        /// <summary>
        /// A body with more than one `return` still needs the exit label and the result local to
        /// join them — the fast path above only ever applies to the single-tail-return shape. Value
        /// correctness is <see cref="ModuleEmitterTests.AMultiReturnInlineBodyStillJoinsCorrectly"/>.
        /// </summary>
        [Fact]
        public void AMultiReturnInlineBodyStillUsesTheExitLabel()
        {
            string code = Disassemble(
                "inline fun sign(x: int): int { if (x < 0) { return -1; } return 1; }\n"
                    + "fun run(a: int): int { return sign(a); }");

            Assert.True(Count(code, "JP") + Count(code, "JPX") >= 1);
        }

        /// <summary>
        /// The cost heuristic (§3.6) splices a body no <c>inline</c> was written on, when the body is
        /// cheap enough — so a call site for a two-instruction function no longer survives into the
        /// bytecode at all. Value correctness is
        /// <see cref="ModuleEmitterTests.ATrivialFunctionIsSplicedWithoutAnyModifier"/>.
        /// </summary>
        [Fact]
        public void ATrivialFunctionIsSplicedByDefault()
        {
            string code = Disassemble(
                "fun twice(x: int): int { return x + x; }\n"
                    + "fun run(a: int): int { return twice(a); }");

            Assert.Equal(0, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// A body above the default threshold still calls without <c>inline</c> — three binary
        /// operations cost three, past the default allowance of two.
        /// </summary>
        [Fact]
        public void ABodyAboveTheDefaultThresholdStillCallsWithoutInline()
        {
            string code = Disassemble(
                "fun heavy(x: int): int { return x * x + 7 * 3; }\n"
                    + "fun run(a: int): int { return heavy(a); }");

            Assert.Equal(1, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// <c>inline</c> raises the allowance: the same body that the default heuristic declines —
        /// a branch and two returns, five by the cost model — splices when the hint is written.
        /// </summary>
        [Fact]
        public void AnInlineHintSplicesABodyTooLargeForTheDefaultHeuristic()
        {
            string code = Disassemble(
                "inline fun moderate(x: int): int { if (x < 0) { return -1; } return 1; }\n"
                    + "fun run(a: int): int { return moderate(a); }");

            Assert.Equal(0, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// An auto-property's read is the field load that is its whole body (§3.6), and its write the
        /// matching store — neither pays for a frame. Value correctness is
        /// <see cref="ModuleEmitterTests.AnAutoPropertyReadAndWriteLowerToTheBackingField"/>.
        /// </summary>
        [Fact]
        public void AnAutoPropertyIsAccessedByFieldOpcodeNotByACall()
        {
            string code = Disassemble(
                "class A { public n: int { get; set; } }\n"
                    + "fun run(): int { let a = A(); a.n = 9; return a.n; }");

            Assert.Equal(0, Count(code, "InvokeSpecial"));
        }

        /// <summary>
        /// A virtual auto-property must still dispatch: an override on a subclass has to run, so the
        /// field-load lowering is only sound where the accessor is non-virtual.
        /// </summary>
        [Fact]
        public void AVirtualAutoPropertyReadStillDispatches()
        {
            string code = Disassemble(
                "class A { public virtual n: int { get; set; } }\n"
                    + "fun run(a: A): int { return a.n; }");

            Assert.Equal(1, Count(code, "InvokeVirtual"));
        }

        /// <summary>
        /// The <c>inline</c> hint reaches a property's accessors: a getter whose body the default
        /// heuristic would decline — five by the cost model — still splices at the read site when
        /// the property declares <c>inline</c>.
        /// </summary>
        [Fact]
        public void AnInlinePropertyGetterIsSplicedAtItsCallSite()
        {
            string body = "if (this._n < 0) { return 0; } return this._n;";

            string plain = Disassemble(
                "class A { private let _n: int; public constructor(n: int) { this._n = n; } public n: int { get { "
                    + body + " } } }\nfun run(a: A): int { return a.n; }");
            Assert.Equal(1, Count(plain, "InvokeSpecial"));

            string inline = Disassemble(
                "class A { private let _n: int; public constructor(n: int) { this._n = n; } public inline n: int { get { "
                    + body + " } } }\nfun run(a: A): int { return a.n; }");
            Assert.Equal(0, Count(inline, "InvokeSpecial"));
        }

        /// <summary>
        /// <c>forceinline</c> splices even a body well above the <c>inline</c> threshold (8) —
        /// unlike <c>inline</c>, which is a hint the heuristic can still decline. Value correctness
        /// is <see cref="ModuleEmitterTests.AForceInlineFunctionSplicesRegardlessOfCost"/>.
        /// </summary>
        [Fact]
        public void AForceInlineFunctionSplicesEvenAboveTheInlineThreshold()
        {
            string code = Disassemble(
                "forceinline fun heavy(x: int): int {\n"
                    + "  if (x < 0) { return -1; }\n"
                    + "  if (x == 0) { return 0; }\n"
                    + "  return x * x + x + 1;\n"
                    + "}\n"
                    + "fun run(a: int): int { return heavy(a); }");

            Assert.Equal(0, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// The write-side twin of <see cref="AnInlinePropertyGetterIsSplicedAtItsCallSite"/>: a
        /// computed setter splices at its write site too, once <c>forceinline</c> is written on the
        /// property. Value correctness is
        /// <see cref="ModuleEmitterTests.AForceInlinePropertySetterSplicesAndAppliesItsBody"/>.
        /// </summary>
        [Fact]
        public void AForceInlinePropertySetterIsSplicedAtItsCallSite()
        {
            string code = Disassemble(
                "class A {\n"
                    + "  public var _n: int;\n"
                    + "  public forceinline n: int { get { return this._n; } set { this._n = value * 2; } }\n"
                    + "}\n"
                    + "fun run(): int { let a = A(); a.n = 5; return a._n; }");

            Assert.Equal(0, Count(code, "InvokeSpecial"));
        }

        #endregion

        #region Const function folding (§7.2)

        /// <summary>
        /// A literal argument folds a `const fun` call away entirely, per §7.2 — the baseline this
        /// region's other cases compare against.
        /// </summary>
        [Fact]
        public void AConstFunCallWithALiteralArgumentFoldsAway()
        {
            string code = Disassemble(
                "const fun square(x: int): int { return x * x; }\nfun run(): int { return square(5); }");

            Assert.Equal(0, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// An argument that is itself a constant expression — not a literal written directly — used
        /// to defeat folding entirely: `ConstantOf` only ever recognised a literal, so `2 + 3` fell
        /// straight through to an ordinary call even though it is exactly as constant as `5`.
        /// </summary>
        [Fact]
        public void AConstFunCallWithAConstantExpressionArgumentStillFolds()
        {
            string code = Disassemble(
                "const fun square(x: int): int { return x * x; }\nfun run(): int { return square(2 + 3); }");

            Assert.Equal(0, Count(code, "CallLocalModule"));
        }

        /// <summary>
        /// A variable argument is genuinely not constant, and still has to call. The body is kept
        /// above the default inline threshold so the heuristic does not splice it and make the call
        /// vanish for a different reason.
        /// </summary>
        [Fact]
        public void AConstFunCallWithAVariableArgumentDoesNotFold()
        {
            string code = Disassemble(
                "const fun square(x: int): int { return x * x + 7 * 3; }\nfun run(a: int): int { return square(a); }");

            Assert.Equal(1, Count(code, "CallLocalModule"));
        }

        #endregion

        #region Value class boxing (§6.3)

        /// <summary>
        /// A direct call to a value class's own method — no interface involved — never needed the
        /// receiver's class at all: the target is resolved at compile time either way. Boxing it
        /// first and unboxing it again on entry used to be unconditional regardless.
        /// </summary>
        [Fact]
        public void ADirectCallOnAValueClassDoesNotBoxTheReceiver()
        {
            string code = Disassemble(
                "value class EntityId {\n"
                    + "  public let raw: int;\n"
                    + "  public constructor(raw: int) { this.raw = raw; }\n"
                    + "  public fun doubled(): int { return this.raw * 2; }\n"
                    + "}\n"
                    + "fun run(): int { let id = EntityId(21); return id.doubled(); }");

            Assert.Equal(0, Count(code, "BoxAs"));
            Assert.Equal(0, Count(code, "Unbox"));
        }

        /// <summary>
        /// A computed property's accessors are calls too, and used to skip the (then-unconditional)
        /// box entirely at the call site while the getter still unboxed on entry — a latent mismatch
        /// this scope closes the same way as an ordinary method call.
        /// </summary>
        [Fact]
        public void AValueClassComputedPropertyDoesNotBoxTheReceiver()
        {
            string code = Disassemble(
                "value class EntityId {\n"
                    + "  public let raw: int;\n"
                    + "  public constructor(raw: int) { this.raw = raw; }\n"
                    + "  public doubled: int { get { return this.raw * 2; } }\n"
                    + "}\n"
                    + "fun run(): int { let id = EntityId(21); return id.doubled; }");

            Assert.Equal(0, Count(code, "BoxAs"));
            Assert.Equal(0, Count(code, "Unbox"));
        }

        /// <summary>Flowing the same value class into an erased slot still has to box — §6.3 unchanged.</summary>
        [Fact]
        public void AValueClassIntoAnErasedSlotStillBoxes()
        {
            string code = Disassemble(
                "value class EntityId {\n"
                    + "  public let raw: int;\n"
                    + "  public constructor(raw: int) { this.raw = raw; }\n"
                    + "}\n"
                    + "fun run(): unknown { let id = EntityId(21); let u: unknown = id; return u; }");

            Assert.Equal(1, Count(code, "BoxAs"));
        }

        #endregion

        #region Built-in default constructors

        /// <summary>
        /// A parameterless `int()`/`float()`/`bool()`/`char()`/`string()` is exactly that type's
        /// default value - a primitive or a string has no instance layout for `ObjNew` to allocate,
        /// so this has to fold to the same shape a literal does, never `ObjNew`. Value correctness
        /// is <see cref="ModuleEmitterTests.ParameterlessPrimitiveAndStringConstructorsAreDefaults"/>.
        /// </summary>
        [Fact]
        public void ParameterlessPrimitiveConstructionsNeverAllocate()
        {
            string code = Disassemble(
                "fun run(): int {\n"
                    + "  let a = int(); let b = float(); let c = bool(); let d = char(); let e = string();\n"
                    + "  return a + e.length;\n"
                    + "}");

            Assert.Equal(0, Count(code, "ObjNew"));
            Assert.Equal(0, Count(code, "ObjNewX"));
        }

        /// <summary>
        /// A parameterless `range()` has no instance layout either — it is the same two operands
        /// `RangeNew` builds, here both zero. Value correctness is
        /// <see cref="ModuleEmitterTests.AParameterlessRangeIsEmpty"/>.
        /// </summary>
        [Fact]
        public void ParameterlessRangeConstructionUsesRangeNewNotObjNew()
        {
            string code = Disassemble("fun run(): range { return range(); }");

            Assert.Equal(0, Count(code, "ObjNew"));
            Assert.Equal(1, Count(code, "RangeNew"));
        }

        #endregion

        #region Built-in member opcode substitution

        [Fact]
        public void ArrayLengthUsesArrLenNotACall()
        {
            string code = Disassemble("fun run(xs: int[]): int { return xs.length; }");

            Assert.Equal(1, Count(code, "ArrLen"));
            Assert.Equal(0, Count(code, "InvokeSpecial"));
            Assert.Equal(0, Count(code, "InvokeStatic"));
        }

        [Fact]
        public void ArrayMutatorsUseTheirOpcodesNotACall()
        {
            string code = Disassemble(
                "fun run(xs: int[]): int {\n"
                    + "  xs.push(9);\n"
                    + "  xs.set(0, xs.get(0) + 1);\n"
                    + "  xs.insert(1, 5);\n"
                    + "  xs.removeAt(2);\n"
                    + "  let found = xs.indexOf(9);\n"
                    + "  let has = xs.contains(5);\n"
                    + "  let last = xs.pop();\n"
                    + "  xs.clear();\n"
                    + "  return found + (has ? 1 : 0) + last;\n"
                    + "}");

            Assert.Equal(1, Count(code, "ArrPush"));
            Assert.Equal(1, Count(code, "ArrSet"));
            Assert.Equal(1, Count(code, "ArrGet"));
            Assert.Equal(1, Count(code, "ArrInsert"));
            Assert.Equal(1, Count(code, "ArrRemoveAt"));
            Assert.Equal(1, Count(code, "ArrIndexOf"));
            Assert.Equal(1, Count(code, "ArrIn"));
            Assert.Equal(1, Count(code, "ArrPop"));
            Assert.Equal(1, Count(code, "ArrClear"));
            Assert.Equal(0, Count(code, "InvokeSpecial"));
            Assert.Equal(0, Count(code, "InvokeStatic"));
        }

        [Fact]
        public void StringLengthAndCharAtUseTheirOpcodesNotACall()
        {
            string code = Disassemble("fun run(s: string): bool { return s.length > 0 && s.charAt(0) == 'h'; }");

            Assert.Equal(1, Count(code, "StrLen"));
            Assert.Equal(1, Count(code, "StrGet"));
            Assert.Equal(0, Count(code, "InvokeSpecial"));
        }

        [Fact]
        public void TupleLengthUsesTupLenNotACall()
        {
            string code = Disassemble("fun run(t: (int, string)): int { return t.length; }");

            Assert.Equal(1, Count(code, "TupLen"));
            Assert.Equal(0, Count(code, "InvokeSpecial"));
        }

        [Fact]
        public void DictGetAndSetUseDictGetAndDictSetNotACall()
        {
            string code = Disassemble(
                "fun run(m: {string: int}): int {\n"
                    + "  m.set(\"x\", m.get(\"x\") + 1);\n"
                    + "  return m.get(\"x\");\n"
                    + "}");

            Assert.Equal(2, Count(code, "DictGet"));
            Assert.Equal(1, Count(code, "DictSet"));
            Assert.Equal(0, Count(code, "InvokeSpecial"));
        }

        #endregion

        #region Nameable collection constructors (§5.3.1) — never ObjNew, folded wherever an opcode exists

        /// <summary>Value correctness is <see cref="ModuleEmitterTests.ArrayEmptyConstructorIsEmpty"/>.</summary>
        [Fact]
        public void ArrayEmptyConstructorUsesArrPackNotObjNew()
        {
            string code = Disassemble("fun run(): int { let xs = array<int>(); return xs.length; }");

            Assert.Equal(1, Count(code, "ArrPack"));
            Assert.Equal(0, Count(code, "ObjNew"));
            Assert.Equal(0, Count(code, "InvokeSpecial"));
        }

        /// <summary>
        /// A written constant folds to the single-instruction ArrNewX form — the addressing mode
        /// documented for exactly this, "for arrays of statically known size" — never a call and
        /// never ObjNew. Value correctness is
        /// <see cref="ModuleEmitterTests.ArrayCapacityConstructorZeroFillsToTheGivenLength"/>.
        /// </summary>
        [Fact]
        public void ArrayCapacityConstructorWithAConstantUsesArrNewX()
        {
            string code = Disassemble("fun run(): int { let xs = array<int>(5); return xs.length; }");

            Assert.Equal(1, Count(code, "ArrNewX"));
            Assert.Equal(0, Count(code, "ArrNew"));
            Assert.Equal(0, Count(code, "ObjNew"));
            Assert.Equal(0, Count(code, "InvokeSpecial"));
        }

        /// <summary>
        /// A runtime size cannot be a compile-time immediate, so this falls back to the
        /// stack-popping ArrNew form rather than ArrNewX — still one opcode, still never ObjNew.
        /// Value correctness is
        /// <see cref="ModuleEmitterTests.ArrayCapacityConstructorWorksWithARuntimeSizeToo"/>.
        /// </summary>
        [Fact]
        public void ArrayCapacityConstructorWithARuntimeValueUsesArrNew()
        {
            string code = Disassemble("fun run(n: int): int { let xs = array<int>(n); return xs.length; }");

            Assert.Equal(1, Count(code, "ArrNew"));
            Assert.Equal(0, Count(code, "ArrNewX"));
            Assert.Equal(0, Count(code, "ObjNew"));
        }

        /// <summary>Value correctness is <see cref="ModuleEmitterTests.DictEmptyConstructorIsEmptyAndStillUsable"/>.</summary>
        [Fact]
        public void DictEmptyConstructorUsesDictNewNotObjNew()
        {
            string code = Disassemble("fun run(): int { let m = dict<int, string>(); return m.length; }");

            Assert.Equal(1, Count(code, "DictNew"));
            Assert.Equal(0, Count(code, "ObjNew"));
            Assert.Equal(0, Count(code, "InvokeSpecial"));
        }

        /// <summary>
        /// The one shape with no single-opcode fold: DictNew has no capacity operand, so this is
        /// DictNew plus exactly one call to dict's own already-declared <c>reserve</c> — still never
        /// ObjNew. Value correctness is
        /// <see cref="ModuleEmitterTests.DictCapacityConstructorStaysEmptyUntilSomethingIsSet"/>.
        /// </summary>
        [Fact]
        public void DictCapacityConstructorUsesDictNewPlusExactlyOneReserveCall()
        {
            string code = Disassemble("fun run(): int { let m = dict<int, string>(32); return m.length; }");

            Assert.Equal(1, Count(code, "DictNew"));
            Assert.Equal(1, Count(code, "InvokeSpecial"));
            Assert.Equal(0, Count(code, "ObjNew"));
        }

        /// <summary>
        /// A tuple's arity is always known at compile time, so array-from-tuple never needs a
        /// runtime length check — no comparison, no branch, just the reads and the pack. Value
        /// correctness is <see cref="ModuleEmitterTests.ArrayFromTupleCastReadsEveryElementInOrder"/>.
        /// </summary>
        [Fact]
        public void ArrayFromTupleCastUsesTupGetCAndArrPackWithNoRuntimeCheck()
        {
            // .get(0), not .length: .length would itself emit an ArrLen unrelated to the
            // construction, muddying the very count this test exists to pin.
            string code = Disassemble("fun run(): int { let a = array<int>((10, 20, 30)); return a.get(0); }");

            Assert.Equal(3, Count(code, "TupGetC"));
            Assert.Equal(1, Count(code, "ArrPack"));
            Assert.Equal(0, Count(code, "ArrLen"));
            Assert.Equal(0, Count(code, "ObjNew"));
            Assert.Equal(0, Count(code, "InvokeSpecial"));
            Assert.Equal(0, Count(code, "InvokeStatic"));
        }

        /// <summary>
        /// Tuple-from-array is the one cast direction with a runtime fact to check — the array's
        /// actual length — so exactly one ArrLen precedes the unrolled reads, still with no call.
        /// Value correctness is
        /// <see cref="ModuleEmitterTests.TupleFromArrayCastReadsEveryElementIntoItsSlot"/> and
        /// <see cref="ModuleEmitterTests.TupleFromArrayArityMismatchThrowsInvalidCastException"/>.
        /// </summary>
        [Fact]
        public void TupleFromArrayCastUsesOneArrLenCheckThenArrGetAndTupPack()
        {
            string code = Disassemble(
                "fun run(xs: int[]): int { let t = tuple<int, int, int>(xs); return t[0]; }");

            Assert.Equal(1, Count(code, "ArrLen"));
            Assert.Equal(3, Count(code, "ArrGet"));
            Assert.Equal(1, Count(code, "TupPack"));
            // Neither ObjNew nor InvokeSpecial is asserted away here: the InvalidCastException the
            // length-mismatch trap raises is a real class instance, so allocating it and calling its
            // constructor legitimately uses both. What matters is that the tuple itself never does —
            // TupPack is its only allocation, above.
        }

        #endregion

        #region Nameable primitive/string/range constructors (§5.3.2) — sugar for existing opcodes/calls

        /// <summary>Value correctness is <see cref="ModuleEmitterTests.APrimitiveConstructorConvertsBetweenPrimitives"/>.</summary>
        [Fact]
        public void APrimitiveConstructorBetweenNumericTypesUsesOnlyTheConversionOpcode()
        {
            string code = Disassemble("fun run(x: float): int { return int(x); }");

            Assert.Equal(1, Count(code, "F2I"));
            Assert.Equal(0, Count(code, "ObjNew"));
            Assert.Equal(0, Count(code, "InvokeSpecial"));
            Assert.Equal(0, Count(code, "InvokeStatic"));
        }

        /// <summary>
        /// char(code: int) is decision #4: no validation, so it has to be indistinguishable from
        /// `code as char` at the opcode level — one I2C, nothing guarding it.
        /// </summary>
        [Fact]
        public void ACharConstructorFromIntUsesOnlyI2CWithNoGuard()
        {
            string code = Disassemble("fun run(x: int): char { return char(x); }");

            Assert.Equal(1, Count(code, "I2C"));
            Assert.Equal(0, Count(code, "ObjNew"));
            Assert.Equal(0, Count(code, "JPZ"));
            Assert.Equal(0, Count(code, "JPZX"));
        }

        /// <summary>
        /// The tuple copy constructor is a pure identity fold — not even a Dup. A local-to-local copy
        /// still has to store the value somewhere, but nothing about the *construction itself* emits
        /// an allocation or a call.
        /// </summary>
        [Fact]
        public void TheTupleCopyConstructorEmitsNoAllocationAtAll()
        {
            string code = Disassemble("fun run(t: (int, int)): int { let t2 = tuple<int, int>(t); return t2[0]; }");

            Assert.Equal(0, Count(code, "TupPack"));
            Assert.Equal(0, Count(code, "ObjNew"));
            Assert.Equal(0, Count(code, "InvokeSpecial"));
        }

        /// <summary>
        /// A written zero default is exactly ArrayCapacity, reused unchanged — no loop, no ArrSet.
        /// Value correctness is <see cref="ModuleEmitterTests.ArraySizeDefaultConstructorFillsEveryElement"/>.
        /// </summary>
        [Fact]
        public void ArraySizeConstructorWithAWrittenZeroDefaultUsesArrNewXWithNoLoop()
        {
            string code = Disassemble("fun run(): int { let a = array<int>(5, 0); return a.length; }");

            Assert.Equal(1, Count(code, "ArrNewX"));
            Assert.Equal(0, Count(code, "ArrSet"));
            Assert.Equal(0, Count(code, "ObjNew"));
        }

        /// <summary>A non-zero default cannot fold onto ArrayCapacity, so this is the genuine loop: one ArrSet, executed a runtime-determined number of times.</summary>
        [Fact]
        public void ArraySizeConstructorWithANonZeroDefaultUsesExactlyOneArrSetInALoop()
        {
            string code = Disassemble("fun run(): int { let a = array<int>(5, -1); return a.length; }");

            Assert.Equal(1, Count(code, "ArrSet"));
            Assert.Equal(0, Count(code, "ObjNew"));
        }

        /// <summary>
        /// The copy constructor takes the fast indexed path — ArrLen, a runtime ArrNew, ArrGet/ArrSet
        /// — never the tuple-cast opcodes (ArrPack, TupGetC), which would mean it had been confused
        /// with array&lt;T&gt;(aTuple).
        /// </summary>
        [Fact]
        public void ArrayCopyConstructorUsesIndexedCopyNeverTupleCastOpcodes()
        {
            string code = Disassemble("fun run(xs: int[]): int { let copy = array<int>(xs); return copy.length; }");

            // 3 ArrLen: once to size the destination allocation, once per pass through the loop
            // condition (one instruction, evaluated every iteration at runtime), and once for the
            // explicit `copy.length` in the return.
            Assert.Equal(3, Count(code, "ArrLen"));
            Assert.Equal(1, Count(code, "ArrNew"));
            Assert.Equal(1, Count(code, "ArrGet"));
            Assert.Equal(1, Count(code, "ArrSet"));
            Assert.Equal(0, Count(code, "ArrPack"));
            Assert.Equal(0, Count(code, "TupGetC"));
        }

        /// <summary>
        /// The general iterable path walks the source through interface dispatch and ArrPush — never
        /// ArrGet/ArrLen on the source, since a generic IIterable&lt;T&gt; has no indexed access at
        /// all and no length known ahead of time.
        /// </summary>
        [Fact]
        public void ArrayFromIterableConstructorUsesInterfaceDispatchAndArrPushOnly()
        {
            string code = Disassemble("fun run(): int { let a = array<int>(0..5); return a.length; }");

            Assert.Equal(3, Count(code, "InvokeInterface"));
            Assert.Equal(1, Count(code, "ArrPush"));
            Assert.Equal(0, Count(code, "ArrGet"));
            Assert.Equal(0, Count(code, "ObjNew"));
        }

        #endregion
    }
}
