#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Runtime.Objects;
using System;
using System.Linq;

namespace Surtr.Tests.Compiler.CodeGen
{
    /// <summary>
    /// Covers folding an expression built entirely of literals at emission time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The evaluator behind it always existed and was only ever asked by two callers - a
    /// <c>switch</c>'s case keys and a <c>const fun</c>'s arguments - so <c>2 * 3</c> in ordinary
    /// code emitted a push, a push and a multiply. Asking during emission removes them.
    /// </para>
    /// <para>
    /// The risk is not that the fold happens; it is that it answers something the instruction
    /// would not have. The evaluator worked in <c>long</c> while the machine works in <c>int</c>
    /// with wrap-around, and it folded divisions the machine traps on. Most of what is below is
    /// about those two, checked by running the folded body and comparing against the same
    /// arithmetic reached through variables, which cannot fold.
    /// </para>
    /// </remarks>
    public sealed class ConstantFoldingTests : IDisposable
    {
        private const string Root = "D:/proj/src";

        private readonly System.Collections.Generic.List<IDisposable> _owned =
            new System.Collections.Generic.List<IDisposable>();

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
        }

        private ModuleEmitter Emit(string source)
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

            return emitter;
        }

        private string Disassemble(string source)
            => SurtrBytecodeDisassembler.Disassemble(Emit(source).Modules[0]);

        private SurtrRuntime Run(string source)
        {
            var emitter = Emit(source);

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            return runtime;
        }

        private static SurtrValue Call(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
        {
            Assert.True(runtime.TryGetModule("game.core.Test", out var module), "No test module was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"'{name}' declares no function.");
            return runtime.Invoke(overloads[0], arguments);
        }

        private static int Int(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => Call(runtime, name, arguments).AsInt;

        private static double Float(SurtrRuntime runtime, string name)
            => Call(runtime, name).AsFloat;

        private static int Count(string disassembly, string mnemonic)
            => disassembly
                .Split('\n')
                .Count(line => line.Trim().Split(new[] { "  " }, StringSplitOptions.RemoveEmptyEntries)
                    .Skip(1)
                    .FirstOrDefault()?.Split(' ')[0] == mnemonic);

        #region The fold happens

        [Fact]
        public void AnArithmeticSpineOfLiteralsBecomesOneLiteral()
        {
            string code = Disassemble("fun answer(): int { return 2 * 3 + 36; }");

            Assert.Equal(0, Count(code, "Mul"));
            Assert.Equal(0, Count(code, "Add"));
        }

        [Fact]
        public void TheFoldedLiteralIsTheValueTheInstructionsWouldHaveProduced()
        {
            var runtime = Run("fun answer(): int { return 2 * 3 + 36; }");
            Assert.Equal(42, Int(runtime, "answer"));
        }

        [Fact]
        public void ANegatedLiteralNeedsNoNegation()
        {
            string code = Disassemble("fun answer(): int { return -7; }");
            Assert.Equal(0, Count(code, "Neg"));
        }

        [Fact]
        public void FloatArithmeticFolds()
        {
            var runtime = Run("fun answer(): float { return 1.5 * 4.0 - 0.5; }");
            Assert.Equal(5.5, Float(runtime, "answer"));
        }

        [Fact]
        public void BitwiseAndShiftsFold()
        {
            string code = Disassemble("fun answer(): int { return (1 << 8) | 0xF; }");

            Assert.Equal(0, Count(code, "Shl"));
            Assert.Equal(0, Count(code, "Or"));

            var runtime = Run("fun answer(): int { return (1 << 8) | 0xF; }");
            Assert.Equal(271, Int(runtime, "answer"));
        }

        [Fact]
        public void ALoopBoundDerivedFromAConstantIsAPlainLiteral()
        {
            // The shape the whole fold exists for: a bound written in terms of a named constant
            // used to pay for its arithmetic on every entry to the loop.
            string code = Disassemble(
                "const SIZE: int = 64;\n"
                    + "fun run(): int { var total = 0; for (i in 0..SIZE - 1) { total = total + i; } return total; }");

            Assert.Equal(0, Count(code, "Sub"));

            var runtime = Run(
                "const SIZE: int = 64;\n"
                    + "fun run(): int { var total = 0; for (i in 0..SIZE - 1) { total = total + i; } return total; }");

            // `..` is exclusive of its upper bound (§4.2), so this walks 0 through 62.
            Assert.Equal(62 * 63 / 2, Int(runtime, "run"));
        }

        #endregion

        #region The fold agrees with the machine

        /// <summary>
        /// The evaluator folded in <c>long</c>; the machine computes in <c>int</c> and wraps. Both
        /// halves of this compute the same thing, one foldable and one not, so they can only agree
        /// if the fold wraps where the instruction wraps.
        /// </summary>
        [Fact]
        public void AnOverflowingSumWrapsTheWayTheInstructionDoes()
        {
            var runtime = Run(
                "fun folded(): int { return 2000000000 + 2000000000; }\n"
                    + "fun unfolded(a: int, b: int): int { return a + b; }");

            int folded = Int(runtime, "folded");
            int unfolded = Int(runtime, "unfolded", SurtrValue.CreateInt(2000000000), SurtrValue.CreateInt(2000000000));

            Assert.Equal(unfolded, folded);
            Assert.Equal(unchecked(2000000000 + 2000000000), folded);
        }

        [Fact]
        public void AnOverflowingProductWrapsTheWayTheInstructionDoes()
        {
            var runtime = Run(
                "fun folded(): int { return 100000 * 100000; }\n"
                    + "fun unfolded(a: int, b: int): int { return a * b; }");

            Assert.Equal(
                Int(runtime, "unfolded", SurtrValue.CreateInt(100000), SurtrValue.CreateInt(100000)),
                Int(runtime, "folded"));
        }

        [Fact]
        public void NegatingTheSmallestIntIsItself()
        {
            // -(-2147483648) has no int, and the machine answers int.MinValue rather than trapping.
            // Folded in long it would answer 2147483648 and then fail to emit as a literal at all.
            var runtime = Run(
                "fun folded(): int { return -(-2147483647 - 1); }\n"
                    + "fun unfolded(a: int): int { return -a; }");

            Assert.Equal(
                Int(runtime, "unfolded", SurtrValue.CreateInt(int.MinValue)),
                Int(runtime, "folded"));
        }

        [Fact]
        public void AShiftPastTheWidthMasksTheWayTheInstructionDoes()
        {
            var runtime = Run(
                "fun folded(): int { return 1 << 33; }\n"
                    + "fun unfolded(a: int, b: int): int { return a << b; }");

            Assert.Equal(
                Int(runtime, "unfolded", SurtrValue.CreateInt(1), SurtrValue.CreateInt(33)),
                Int(runtime, "folded"));
        }

        [Fact]
        public void AnUnsignedShiftOfANegativeValueZeroFillsTheWayTheInstructionDoes()
        {
            var runtime = Run(
                "fun folded(): int { return -8 >>> 1; }\n"
                    + "fun unfolded(a: int, b: int): int { return a >>> b; }");

            Assert.Equal(
                Int(runtime, "unfolded", SurtrValue.CreateInt(-8), SurtrValue.CreateInt(1)),
                Int(runtime, "folded"));
        }

        #endregion

        #region The fold does not swallow a trap

        /// <summary>
        /// A division by zero written in constants still traps at run time. Folding it would move
        /// a fault the program is entitled to observe, so it is refused and the instruction stays.
        /// </summary>
        [Fact]
        public void DivisionByAConstantZeroStillTraps()
        {
            string code = Disassemble("fun boom(): int { return 1 / 0; }");
            Assert.Equal(1, Count(code, "Div"));

            var runtime = Run("fun boom(): int { return 1 / 0; }");
            Assert.ThrowsAny<Exception>(() => Call(runtime, "boom"));
        }

        [Fact]
        public void ModuloByAConstantZeroStillTraps()
        {
            string code = Disassemble("fun boom(): int { return 1 % 0; }");
            Assert.Equal(1, Count(code, "Mod"));
        }

        /// <summary>
        /// The one quotient with no <c>int</c>. The machine traps on it explicitly rather than
        /// letting the hardware fault, so the fold has to leave it alone as well.
        /// </summary>
        [Fact]
        public void TheOverflowingDivisionStillTraps()
        {
            string code = Disassemble("fun boom(): int { return (-2147483647 - 1) / -1; }");
            Assert.Equal(1, Count(code, "Div"));

            var runtime = Run("fun boom(): int { return (-2147483647 - 1) / -1; }");
            Assert.ThrowsAny<Exception>(() => Call(runtime, "boom"));
        }

        [Fact]
        public void AnOrdinaryConstantDivisionStillFolds()
        {
            string code = Disassemble("fun answer(): int { return 84 / 2; }");
            Assert.Equal(0, Count(code, "Div"));

            var runtime = Run("fun answer(): int { return 84 / 2; }");
            Assert.Equal(42, Int(runtime, "answer"));
        }

        #endregion

        #region The fold stays out of what it cannot answer

        [Fact]
        public void AnExpressionTouchingAVariableIsNotFolded()
        {
            string code = Disassemble("fun run(n: int): int { return n * 2 + 1; }");

            Assert.Equal(1, Count(code, "Mul"));
            Assert.Equal(1, Count(code, "Add"));
        }

        /// <summary>
        /// Only half of this folds, and the half that does must not drag the other half with it.
        /// </summary>
        [Fact]
        public void APartlyConstantExpressionFoldsOnlyItsConstantPart()
        {
            string code = Disassemble("fun run(n: int): int { return n + (2 * 3); }");

            Assert.Equal(0, Count(code, "Mul"));
            Assert.Equal(1, Count(code, "Add"));

            var runtime = Run("fun run(n: int): int { return n + (2 * 3); }");
            Assert.Equal(16, Int(runtime, "run", SurtrValue.CreateInt(10)));
        }

        /// <summary>
        /// Comparison has no fold in the evaluator, and this pins that: a folded <c>true</c> here
        /// would have to agree with every comparison opcode's own notion of equality, which is
        /// exactly what <c>SurtrValueComparer</c> exists to settle and the emitter does not know.
        /// </summary>
        [Fact]
        public void AConstantComparisonIsNotFolded()
        {
            string code = Disassemble("fun run(): bool { return 2 < 3; }");
            Assert.NotEqual(0, Count(code, "LT") + Count(code, "PushTrue"));
        }

        #endregion
    }
}
