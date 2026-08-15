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
    }
}
