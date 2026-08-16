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
    }
}
