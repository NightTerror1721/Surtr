#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Tests.Compiler.CodeGen
{
    /// <summary>
    /// Covers §7's other half: folding a <c>const fun</c> by emitting its bytecode and running it on
    /// a real <see cref="Surtr.Runtime.SurtrRuntime"/>.
    /// </summary>
    /// <remarks>
    /// Every test here goes the whole way — source, binding, emission, a scratch module, a scratch
    /// runtime, and a value back — because that is the point of the design. Folding on the real
    /// interpreter is what makes compile-time and run-time semantics the same semantics, and a test
    /// that stopped short of running would not be testing that at all.
    /// </remarks>
    public sealed class ConstFolderTests
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

        private static void AssertNoErrors(SurtrCompilation compilation)
        {
            Assert.True(
                !compilation.HasErrors,
                "Unexpected: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        private static void AssertReports(SurtrCompilation compilation, SurtrDiagnosticCode code)
        {
            Assert.True(
                compilation.Diagnostics.Any(d => d.Code == code),
                $"Expected {code}, got: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        private static object? Fold(Binder binder, string name, params object?[] arguments)
        {
            var folder = binder.ConstFolder;
            Assert.NotNull(folder);

            var method = binder.Modules.Values
                .SelectMany(m => m.Methods)
                .Single(m => m.Name == name);

            Assert.True(
                folder!.TryFold(method, arguments, out object? value, out string failure),
                $"'{name}' did not fold: {failure}");

            return value;
        }

        private static string FoldFailure(Binder binder, string name, params object?[] arguments)
        {
            var folder = binder.ConstFolder;
            Assert.NotNull(folder);

            var method = binder.Modules.Values
                .SelectMany(m => m.Methods)
                .Single(m => m.Name == name);

            Assert.False(
                folder!.TryFold(method, arguments, out _, out string failure),
                $"'{name}' folded when it should not have.");

            return failure;
        }

        #region Folding a call
        [Fact]
        public void AConstFunctionIsFoldedByRunningItsRealBytecode()
        {
            using var binder = Bind(out var compilation, "const fun square(x: int): int { return x * x; }");

            AssertNoErrors(compilation);
            Assert.Equal(16L, Fold(binder, "square", 4L));
        }

        [Fact]
        public void AConstInitializerFoldsThroughACall()
        {
            using var binder = Bind(out var compilation,
                "const fun square(x: int): int { return x * x; }\nconst Sixteen: int = square(4);");

            AssertNoErrors(compilation);
            Assert.True(binder.Constants.TryGetValue("Sixteen", out object? value));
            Assert.Equal(16L, value);
        }

        [Fact]
        public void AConstFunctionMayCallAnother()
        {
            using var binder = Bind(out var compilation,
                "const fun square(x: int): int { return x * x; }\n"
                    + "const fun quad(x: int): int { return square(square(x)); }\n"
                    + "const Sixteen: int = quad(2);");

            AssertNoErrors(compilation);
            Assert.True(binder.Constants.TryGetValue("Sixteen", out object? value));
            Assert.Equal(16L, value);
        }

        [Fact]
        public void AConstFunctionMayRecurse()
        {
            using var binder = Bind(out var compilation,
                "const fun fib(n: int): int {\n"
                    + "  if (n < 2) { return n; }\n"
                    + "  return fib(n - 1) + fib(n - 2);\n"
                    + "}\n"
                    + "const Tenth: int = fib(10);");

            AssertNoErrors(compilation);
            Assert.True(binder.Constants.TryGetValue("Tenth", out object? value));
            Assert.Equal(55L, value);
        }

        [Fact]
        public void AConstFunctionFoldsAWholeLoop()
        {
            using var binder = Bind(out var compilation,
                "const fun triangular(n: int): int {\n"
                    + "  var total = 0;\n"
                    + "  for (i in 0..=n) { total = total + i; }\n"
                    + "  return total;\n"
                    + "}");

            AssertNoErrors(compilation);
            Assert.Equal(55L, Fold(binder, "triangular", 10L));
        }

        #region Enum expressions in constants (§2.3quater)

        /// <summary>A case read is a constant: it is the enum's value.</summary>
        [Fact]
        public void AConstInitializerFoldsAnEnumCaseRead()
        {
            using var binder = Bind(out var compilation,
                "enum Suit { Hearts, Spades }\nconst First: int = Suit.Hearts.value;");

            AssertNoErrors(compilation);
            Assert.True(binder.Constants.TryGetValue("First", out object? value));
            Assert.Equal(0L, value);
        }

        /// <summary><c>of(value)</c> is the inverse of <c>.value</c>, and folds to the matching case's value.</summary>
        [Fact]
        public void AConstInitializerFoldsOfValue()
        {
            using var binder = Bind(out var compilation,
                "enum Suit { Hearts, Spades }\nconst Second: int = Suit.of(1).value;");

            AssertNoErrors(compilation);
            Assert.True(binder.Constants.TryGetValue("Second", out object? value));
            Assert.Equal(1L, value);
        }

        /// <summary><c>of(name)</c> folds to the named case's value, and null for an unknown name.</summary>
        [Fact]
        public void AConstInitializerFoldsOfName()
        {
            using var binder = Bind(out var compilation,
                "enum Suit { Hearts, Spades }\n"
                    + "const Named: int = Suit.of(\"Spades\").value;\n"
                    + "const Unknown: bool = Suit.of(\"Clubs\") == null;");

            AssertNoErrors(compilation);
            Assert.True(binder.Constants.TryGetValue("Named", out object? named));
            Assert.Equal(1L, named);
            Assert.True(binder.Constants.TryGetValue("Unknown", out object? unknown));
            Assert.Equal(true, unknown);
        }

        /// <summary><c>toString</c> folds to the case's name.</summary>
        [Fact]
        public void AConstInitializerFoldsToString()
        {
            using var binder = Bind(out var compilation,
                "enum Suit { Hearts, Spades }\nconst Named: string = Suit.Spades.toString();");

            AssertNoErrors(compilation);
            Assert.True(binder.Constants.TryGetValue("Named", out object? value));
            Assert.Equal("Spades", value);
        }

        /// <summary>Instance members with a constant receiver fold by direct dispatch (§2.3quater).</summary>
        [Fact]
        public void AConstInitializerFoldsInstanceMembersOnACase()
        {
            using var binder = Bind(out var compilation,
                "enum Suit { Hearts, Spades }\n"
                    + "const Same: bool = Suit.Hearts.equals(Suit.Hearts);\n"
                    + "const Hash: int = Suit.Hearts.hashCode();\n"
                    + "const Order: int = Suit.Spades.compareTo(Suit.Hearts);");

            AssertNoErrors(compilation);
            Assert.True(binder.Constants.TryGetValue("Same", out object? same));
            Assert.Equal(true, same);
            Assert.True(binder.Constants.TryGetValue("Hash", out object? hash));
            Assert.Equal(0L, hash);
            Assert.True(binder.Constants.TryGetValue("Order", out object? order));
            Assert.Equal(1L, order);
        }

        /// <summary>A <c>@Flags</c> enum's <c>of(value)</c> is total even in a constant: any int is a representable combination.</summary>
        [Fact]
        public void AFlagsOfValueIsTotalInAConstant()
        {
            using var binder = Bind(out var compilation,
                "@Flags enum Perm { None = 0, Read = 1, Write = 2 }\nconst Combined: int = Perm.of(3).value;");

            AssertNoErrors(compilation);
            Assert.True(binder.Constants.TryGetValue("Combined", out object? value));
            Assert.Equal(3L, value);
        }

        #endregion

        [Fact]
        public void AConstFunctionFoldsOverStrings()
        {
            using var binder = Bind(out var compilation,
                "const fun greet(who: string): string { return \"hello, \" + who; }\n"
                    + "const Greeting: string = greet(\"world\");");

            AssertNoErrors(compilation);
            Assert.True(binder.Constants.TryGetValue("Greeting", out object? value));
            Assert.Equal("hello, world", value);
        }

        [Fact]
        public void AConstFunctionMayReturnAnArray()
        {
            using var binder = Bind(out var compilation,
                "const fun three(): int[] { return [1, 2, 3]; }");

            AssertNoErrors(compilation);

            var folded = Assert.IsType<object?[]>(Fold(binder, "three"));
            Assert.Equal(new object?[] { 1L, 2L, 3L }, folded);
        }

        [Fact]
        public void FoldingUsesTheInterpretersOwnArithmetic()
        {
            // The whole argument for running the VM rather than writing a second evaluator: integer
            // division truncates here because it truncates there, not because this agreed to.
            using var binder = Bind(out var compilation,
                "const fun half(x: int): int { return x / 2; }");

            AssertNoErrors(compilation);
            Assert.Equal(-3L, Fold(binder, "half", -7L));
        }
        #endregion

        #region The budget
        [Fact]
        public void ANonTerminatingConstFunctionIsADiagnosticRatherThanAHang()
        {
            using var binder = Bind(out var compilation,
                "const fun spin(): int {\n"
                    + "  var i = 0;\n"
                    + "  while (true) { i = i + 1; }\n"
                    + "  return i;\n"
                    + "}");

            string failure = FoldFailure(binder, "spin");
            Assert.Contains("did not finish", failure);
            _ = compilation;
        }

        [Fact]
        public void ExceedingTheBudgetDoesNotPoisonTheNextFold()
        {
            using var binder = Bind(out var compilation,
                "const fun spin(): int {\n"
                    + "  var i = 0;\n"
                    + "  while (true) { i = i + 1; }\n"
                    + "  return i;\n"
                    + "}\n"
                    + "const fun square(x: int): int { return x * x; }");

            AssertNoErrors(compilation);

            // The budget is left exhausted on purpose, so the folder has to re-arm it before every
            // run; if it did not, this second fold would abort instantly.
            _ = FoldFailure(binder, "spin");
            Assert.Equal(49L, Fold(binder, "square", 7L));
        }

        [Fact]
        public void AConstFunctionThatTrapsReportsWhatItRaised()
        {
            using var binder = Bind(out var compilation,
                "const fun bad(x: int): int { return x / 0; }");

            AssertNoErrors(compilation);
            Assert.Contains("failed at compile time", FoldFailure(binder, "bad", 1L));
        }
        #endregion

        #region The restrictions §7.2 puts on a const fun
        [Fact]
        public void AConstFunctionCannotBeVirtual()
        {
            Bind(out var compilation,
                "class Shape {\n  public virtual const fun area(): int { return 0; }\n}").Dispose();

            AssertReports(compilation, SurtrDiagnosticCode.InvalidConstFunction);
        }

        [Fact]
        public void AConstFunctionCannotCallAnOrdinaryOne()
        {
            Bind(out var compilation,
                "fun ordinary(): int { return 1; }\nconst fun folded(): int { return ordinary(); }").Dispose();

            AssertReports(compilation, SurtrDiagnosticCode.InvalidConstFunction);
        }

        [Fact]
        public void AConstFunctionCannotWriteAModuleVariable()
        {
            Bind(out var compilation,
                "var counter: int = 0;\nconst fun bump(): int { counter = counter + 1; return counter; }").Dispose();

            AssertReports(compilation, SurtrDiagnosticCode.InvalidConstFunction);
        }

        [Fact]
        public void AConstFunctionMayStillMutateItsOwnLocals()
        {
            using var binder = Bind(out var compilation,
                "const fun count(n: int): int {\n"
                    + "  var total = 0;\n"
                    + "  var i = 0;\n"
                    + "  while (i < n) { total = total + 2; i = i + 1; }\n"
                    + "  return total;\n"
                    + "}");

            AssertNoErrors(compilation);
            Assert.Equal(8L, Fold(binder, "count", 4L));
        }
        #endregion

        #region Where folding is required
        [Fact]
        public void AConstInitializerThatDoesNotFoldIsReported()
        {
            Bind(out var compilation, "const Broken: int = whatever;").Dispose();
            AssertReports(compilation, SurtrDiagnosticCode.NotAConstant);
        }

        [Fact]
        public void AStatementLevelConstIfMayCallAConstFunction()
        {
            using var binder = Bind(out var compilation,
                "const fun square(x: int): int { return x * x; }\n"
                    + "class Test {\n"
                    + "  public fun run(): int {\n"
                    + "    const if (square(4) == 16) { return 1; } else { return NoSuchThing.at(all); }\n"
                    + "  }\n"
                    + "}");

            // The untaken branch is never bound, which is exactly what makes `const if` usable — so
            // naming something that does not exist in it has to compile cleanly.
            AssertNoErrors(compilation);
        }

        [Fact]
        public void ADeclarationLevelConstIfCannotCallAConstFunction()
        {
            // §7.2's own note: folding needs the callee's emitted body, and a declaration-level
            // `const if` is answered before any signature exists. Saying so beats guessing.
            Bind(out var compilation,
                "const fun on(): bool { return true; }\nconst if (on()) { class Yes { } }").Dispose();

            AssertReports(compilation, SurtrDiagnosticCode.NotAConstant);
        }
        #endregion

        #region Emission boundaries
        [Fact]
        public void AFunctionTheEmitterCannotLowerIsDroppedAndSoIsItsCaller()
        {
            using var binder = Bind(out var compilation,
                "class Box { public let value: int = 1; }\n"
                    + "const fun make(): int { let b = Box(); return b.value; }\n"
                    + "const fun user(): int { return make(); }");

            // `make` allocates, which the const-evaluable subset does not lower - and `user` calls
            // it, so dropping one has to drop the other rather than leave a caller pointed at a stub.
            // Neither is a diagnostic on its own: §7.2 only requires a fold where a constant does.
            AssertNoErrors(compilation);
            Assert.Contains("make", FoldFailure(binder, "user"));
        }

        [Fact]
        public void ACompilationWithNoConstFunctionBuildsNoRuntime()
        {
            using var binder = Bind(out var compilation, "fun ordinary(): int { return 1; }");

            AssertNoErrors(compilation);
            Assert.Null(binder.ConstFolder);
        }
        #endregion

        #region Constructs the emitter lowers
        // Everything below runs on the interpreter, so each one checks the emitted bytes as much as
        // the lowering: a label marked at the wrong depth or a branch that does not reach fails at
        // emit, and a wrong opcode fails on the answer.

        [Fact]
        public void TheTernaryAndTheShortCircuitingOperators()
        {
            using var binder = Bind(out var compilation,
                "const fun pick(a: int, b: int): int { return a > b ? a : b; }\n"
                    + "const fun both(a: bool, b: bool): bool { return a && b; }\n"
                    + "const fun either(a: bool, b: bool): bool { return a || b; }");

            AssertNoErrors(compilation);

            Assert.Equal(9L, Fold(binder, "pick", 4L, 9L));
            Assert.Equal(false, Fold(binder, "both", true, false));
            Assert.Equal(true, Fold(binder, "either", false, true));
        }

        [Fact]
        public void ThreeWayCompareYieldsTheSignOfTheComparison()
        {
            using var binder = Bind(out var compilation,
                "const fun order(a: int, b: int): int { return a <=> b; }");

            AssertNoErrors(compilation);

            Assert.Equal(1L, Fold(binder, "order", 9L, 4L));
            Assert.Equal(0L, Fold(binder, "order", 4L, 4L));
            Assert.Equal(-1L, Fold(binder, "order", 1L, 4L));
        }

        [Fact]
        public void BothFormsOfSwitch()
        {
            using var binder = Bind(out var compilation,
                "const fun name(x: int): string {\n"
                    + "  return switch (x) {\n"
                    + "    1 -> \"one\",\n"
                    + "    2, 3 -> \"few\",\n"
                    + "    else -> \"many\",\n"
                    + "  };\n"
                    + "}\n"
                    + "const fun weight(x: int): int {\n"
                    + "  var w = 0;\n"
                    + "  switch (x) {\n"
                    + "    case 1:\n"
                    + "      w = 10;\n"
                    + "      break;\n"
                    + "    case 2:\n"
                    + "    case 3:\n"
                    + "      w = 20;\n"
                    + "      break;\n"
                    + "    default:\n"
                    + "      w = 30;\n"
                    + "  }\n"
                    + "  return w;\n"
                    + "}");

            AssertNoErrors(compilation);

            Assert.Equal("one", Fold(binder, "name", 1L));
            Assert.Equal("few", Fold(binder, "name", 3L));
            Assert.Equal("many", Fold(binder, "name", 7L));

            Assert.Equal(10L, Fold(binder, "weight", 1L));
            Assert.Equal(20L, Fold(binder, "weight", 3L));
            Assert.Equal(30L, Fold(binder, "weight", 7L));
        }

        [Fact]
        public void ALabelledBreakLeavesTheLoopItNames()
        {
            using var binder = Bind(out var compilation,
                "const fun search(limit: int): int {\n"
                    + "  var found = 0;\n"
                    + "  outer: for (i in 0..limit) {\n"
                    + "    for (j in 0..limit) {\n"
                    + "      if (i * j > 6) { found = i * 100 + j; break outer; }\n"
                    + "    }\n"
                    + "  }\n"
                    + "  return found;\n"
                    + "}");

            AssertNoErrors(compilation);

            // i=1, j=7 is the first product over six. An inner-only break would keep going and
            // finish at 901, so the answer is what proves the label was honoured.
            Assert.Equal(107L, Fold(binder, "search", 10L));
        }

        [Fact]
        public void AContinueInsideASwitchBelongsToTheEnclosingLoop()
        {
            // The one place a switch and a loop disagree about what a jump means: `break` leaves the
            // switch, `continue` has to look straight past it.
            using var binder = Bind(out var compilation,
                "const fun total(limit: int): int {\n"
                    + "  var sum = 0;\n"
                    + "  for (i in 0..limit) {\n"
                    + "    switch (i) {\n"
                    + "      case 2:\n"
                    + "        continue;\n"
                    + "      default:\n"
                    + "        break;\n"
                    + "    }\n"
                    + "    sum = sum + i;\n"
                    + "  }\n"
                    + "  return sum;\n"
                    + "}");

            AssertNoErrors(compilation);

            // 0 + 1 + 3 + 4, with 2 skipped.
            Assert.Equal(8L, Fold(binder, "total", 5L));
        }

        [Fact]
        public void StringInterpolationCallsEachPartsToString()
        {
            using var binder = Bind(out var compilation,
                "const fun describe(n: int, ok: bool): string { return \"n=${n} ok=${ok}\"; }");

            AssertNoErrors(compilation);
            Assert.Equal("n=7 ok=true", Fold(binder, "describe", 7L, true));
        }

        [Fact]
        public void CollectionsBuiltInsideTheCallAreReadBack()
        {
            using var binder = Bind(out var compilation,
                "const fun at(i: int): int { let xs = [10, 20, 30]; return xs[i]; }\n"
                    + "const fun lookup(k: string): int { let m = {\"a\": 1, \"b\": 2}; return m[k]; }\n"
                    + "const fun grow(): int[] { var xs = [0, 0]; xs[0] = 7; xs[1] = 8; return xs; }");

            AssertNoErrors(compilation);

            Assert.Equal(20L, Fold(binder, "at", 1L));
            Assert.Equal(2L, Fold(binder, "lookup", "b"));
            Assert.Equal(new object?[] { 7L, 8L }, Assert.IsType<object?[]>(Fold(binder, "grow")));
        }

        [Fact]
        public void AForInOverAnArrayWalksItByIndex()
        {
            using var binder = Bind(out var compilation,
                "const fun sum(): int {\n"
                    + "  var total = 0;\n"
                    + "  for (x in [4, 5, 6]) { total = total + x; }\n"
                    + "  return total;\n"
                    + "}");

            AssertNoErrors(compilation);
            Assert.Equal(15L, Fold(binder, "sum"));
        }

        [Fact]
        public void TheWideningIntToFloatIsAConversionInTheTree()
        {
            using var binder = Bind(out var compilation,
                "const fun half(n: int): float { return n / 2.0; }");

            AssertNoErrors(compilation);
            Assert.Equal(3.5, Fold(binder, "half", 7L));
        }

        [Fact]
        public void TheBitwiseOperatorsAndBothShifts()
        {
            using var binder = Bind(out var compilation,
                "const fun bits(): int { return (0xFF & 0x0F) | (1 << 4) ^ 2; }\n"
                    + "const fun arithmetic(): int { return -8 >> 2; }\n"
                    + "const fun logical(): int { return -8 >>> 28; }\n"
                    + "const fun complement(): int { return ~5; }");

            AssertNoErrors(compilation);

            Assert.Equal((0xFF & 0x0F) | ((1 << 4) ^ 2), (long)Fold(binder, "bits")!);
            Assert.Equal(-2L, Fold(binder, "arithmetic"));
            Assert.Equal((long)(int)(unchecked((uint)-8) >> 28), (long)Fold(binder, "logical")!);
            Assert.Equal(-6L, Fold(binder, "complement"));
        }
        #endregion

        #region Determinism
        [Fact]
        public void FoldingTheSameCallTwiceGivesTheSameAnswer()
        {
            using var binder = Bind(out var compilation,
                "const fun square(x: int): int { return x * x; }");

            AssertNoErrors(compilation);

            var results = new List<object?>();
            for (int i = 0; i < 3; i++)
                results.Add(Fold(binder, "square", 5L));

            Assert.All(results, value => Assert.Equal(25L, value));
        }
        #endregion
    }
}
