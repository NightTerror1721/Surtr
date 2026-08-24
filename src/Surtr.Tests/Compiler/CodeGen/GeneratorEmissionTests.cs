#nullable enable

using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Tests.Compiler.CodeGen
{
    /// <summary>
    /// Generators from source to execution: <c>generator</c> declarations, <c>yield</c>, and what a
    /// <c>for-in</c> over one produces.
    /// </summary>
    /// <remarks>
    /// The opcode-level protocol is pinned in <c>SurtrVirtualMachineGeneratorTests</c>. What is
    /// tested here is the other half - that the compiler emits <em>against</em> that protocol
    /// correctly: the stub/body split, the element type, the two iteration paths agreeing, and the
    /// rules §3.7 puts on a declaration.
    /// </remarks>
    public sealed class GeneratorEmissionTests : IDisposable
    {
        private const string Root = "D:/proj/src";

        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
        }

        private SurtrRuntime Run(string source)
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

            var emitter = new Surtr.Compiler.CodeGen.ModuleEmitter(compilation, binder);

            Assert.True(
                emitter.TryEmit(),
                "Emission reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            return runtime;
        }

        /// <summary>Binds without emitting, for the tests that are about what is refused.</summary>
        private IReadOnlyList<SurtrDiagnostic> Diagnose(string source)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            var compilation = SurtrCompilation.Create(project);
            _owned.Add(compilation);

            var binder = compilation.Bind();
            binder.BindBodies();

            return compilation.Diagnostics.ToList();
        }

        private static SurtrMethodInfo Function(SurtrRuntime runtime, string name)
        {
            Assert.True(runtime.TryGetModule("game.core.Test", out var module), "No module 'game.core.Test' was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"'game.core.Test' declares no '{name}'.");
            return overloads[0];
        }

        private static int Int(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => runtime.Invoke(Function(runtime, name), arguments).AsInt;

        private static void AssertCode(IReadOnlyList<SurtrDiagnostic> diagnostics, SurtrDiagnosticCode code)
            => Assert.True(
                diagnostics.Any(d => d.Code == code),
                $"Expected {code}, got: " + string.Join("; ", diagnostics.Select(d => d.ToString())));

        #region The whole loop

        [Fact]
        public void AForInOverAGeneratorWalksEveryYield()
        {
            var runtime = Run(
                "generator countdown(from: int): int {\n"
                    + "    var i = from;\n"
                    + "    while (i > 0) {\n"
                    + "        yield i;\n"
                    + "        i = i - 1;\n"
                    + "    }\n"
                    + "}\n"
                    + "fun total(n: int): int {\n"
                    + "    var sum = 0;\n"
                    + "    for (x in countdown(n)) { sum = sum + x; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(15, Int(runtime, "total", SurtrValue.CreateInt(5))); // 5+4+3+2+1
        }

        [Fact]
        public void LocalsSurviveEverySuspension()
        {
            // The classic case from the plan: state that has to be carried across a `yield` and
            // read back correctly afterwards. Under strategy B it lives in the copied frame, so a
            // wrong answer here means the round trip lost it.
            var runtime = Run(
                "generator digits(n: int): int {\n"
                    + "    var x = n;\n"
                    + "    if (x == 0) { yield 0; }\n"
                    + "    while (x > 0) {\n"
                    + "        yield x % 10;\n"
                    + "        x = x / 10;\n"
                    + "    }\n"
                    + "}\n"
                    + "fun sumDigits(n: int): int {\n"
                    + "    var sum = 0;\n"
                    + "    for (d in digits(n)) { sum = sum + d; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(7, Int(runtime, "sumDigits", SurtrValue.CreateInt(4021)));
        }

        [Fact]
        public void CallingAGeneratorRunsNoneOfItsBody()
        {
            // §3.1: the call builds an object. If the body ran eagerly this would divide by zero.
            var runtime = Run(
                "generator boom(): int {\n"
                    + "    let bad = 1 / 0;\n"
                    + "    yield bad;\n"
                    + "}\n"
                    + "fun build(): int {\n"
                    + "    let g = boom();\n"
                    + "    return 42;\n"
                    + "}");

            Assert.Equal(42, Int(runtime, "build"));
        }

        [Fact]
        public void TheSameGeneratorObjectIsSingleUse()
        {
            // §12.2's mixed model: the object is walked once, and walking it again is refused
            // rather than silently producing nothing.
            var runtime = Run(
                "generator one(): int { yield 1; }\n"
                    + "fun twice(): int {\n"
                    + "    let g = one();\n"
                    + "    var sum = 0;\n"
                    + "    for (a in g) { sum = sum + a; }\n"
                    + "    for (b in g) { sum = sum + b; }\n"
                    + "    return sum;\n"
                    + "}");

            var thrown = Assert.Throws<SurtrExecutionException>(() => Int(runtime, "twice"));
            Assert.Contains("single-use", thrown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void CallingTheFunctionAgainRestarts()
        {
            // The other half of §12.2: a fresh call is a fresh generator, so two loops each walk
            // the whole sequence.
            var runtime = Run(
                "generator upTo(n: int): int {\n"
                    + "    var i = 1;\n"
                    + "    while (i <= n) { yield i; i = i + 1; }\n"
                    + "}\n"
                    + "fun twice(n: int): int {\n"
                    + "    var sum = 0;\n"
                    + "    for (a in upTo(n)) { sum = sum + a; }\n"
                    + "    for (b in upTo(n)) { sum = sum + b; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(12, Int(runtime, "twice", SurtrValue.CreateInt(3))); // (1+2+3) twice
        }

        [Fact]
        public void AReturnEndsTheSequenceEarly()
        {
            var runtime = Run(
                "generator upToFive(): int {\n"
                    + "    var i = 1;\n"
                    + "    while (true) {\n"
                    + "        if (i > 5) { return; }\n"
                    + "        yield i;\n"
                    + "        i = i + 1;\n"
                    + "    }\n"
                    + "}\n"
                    + "fun total(): int {\n"
                    + "    var sum = 0;\n"
                    + "    for (x in upToFive()) { sum = sum + x; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(15, Int(runtime, "total"));
        }

        [Fact]
        public void AGeneratorWithNoYieldIteratesNothing()
        {
            var runtime = Run(
                "generator empty(): int { }\n"
                    + "fun count(): int {\n"
                    + "    var n = 0;\n"
                    + "    for (x in empty()) { n = n + 1; }\n"
                    + "    return n;\n"
                    + "}");

            Assert.Equal(0, Int(runtime, "count"));
        }

        [Fact]
        public void AGeneratorMethodOnAClassSeesItsReceiver()
        {
            var runtime = Run(
                "class Counter {\n"
                    + "    private let _limit: int;\n"
                    + "    public constructor(limit: int) { this._limit = limit; }\n"
                    + "    public generator values(): int {\n"
                    + "        var i = 0;\n"
                    + "        while (i < this._limit) { yield i; i = i + 1; }\n"
                    + "    }\n"
                    + "}\n"
                    + "fun total(limit: int): int {\n"
                    + "    let c = Counter(limit);\n"
                    + "    var sum = 0;\n"
                    + "    for (x in c.values()) { sum = sum + x; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(6, Int(runtime, "total", SurtrValue.CreateInt(4))); // 0+1+2+3
        }

        [Fact]
        public void AGeneratorYieldingStringsCarriesItsElementType()
        {
            var runtime = Run(
                "generator names(): string {\n"
                    + "    yield \"ab\";\n"
                    + "    yield \"cde\";\n"
                    + "}\n"
                    + "fun totalLength(): int {\n"
                    + "    var n = 0;\n"
                    + "    for (s in names()) { n = n + s.length; }\n"
                    + "    return n;\n"
                    + "}");

            Assert.Equal(5, Int(runtime, "totalLength"));
        }

        #endregion

        #region The type, and the two iteration paths

        [Fact]
        public void AGeneratorsViewTypeIsGeneratorOfItsElement()
        {
            var runtime = Run(
                "generator one(): int { yield 1; }\n"
                    + "fun held(): int {\n"
                    + "    let g: generator<int> = one();\n"
                    + "    var sum = 0;\n"
                    + "    for (x in g) { sum = sum + x; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "held"));
            Assert.Equal("YI", Function(runtime, "one").ReturnType.Reference.Descriptor);
        }

        [Fact]
        public void AGeneratorSatisfiesIIterableSoTheContractPathWalksItToo()
        {
            // Assigned to the contract, the loop can no longer take the fast path and goes through
            // iterate()/moveNext()/current instead. It has to produce the same elements - that
            // agreement is the whole reason both paths exist.
            var runtime = Run(
                "generator upTo(n: int): int {\n"
                    + "    var i = 1;\n"
                    + "    while (i <= n) { yield i; i = i + 1; }\n"
                    + "}\n"
                    + "fun viaContract(n: int): int {\n"
                    + "    let xs: IIterable<int> = upTo(n);\n"
                    + "    var sum = 0;\n"
                    + "    for (x in xs) { sum = sum + x; }\n"
                    + "    return sum;\n"
                    + "}\n"
                    + "fun direct(n: int): int {\n"
                    + "    var sum = 0;\n"
                    + "    for (x in upTo(n)) { sum = sum + x; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(10, Int(runtime, "viaContract", SurtrValue.CreateInt(4)));
            Assert.Equal(10, Int(runtime, "direct", SurtrValue.CreateInt(4)));
        }

        [Fact]
        public void TheStubAndTheBodyAreTwoSeparateMethods()
        {
            var runtime = Run("generator one(): int { yield 1; }");

            Assert.True(runtime.TryGetModule("game.core.Test", out var module));

            // The stub keeps the declared name and returns the generator; the body is synthetic and
            // returns nothing. A call site names only the first, which is what makes calling a
            // generator an ordinary call.
            Assert.True(module.TryGetMethods("one", out var stub));
            Assert.Equal("YI", stub[0].ReturnType.Reference.Descriptor);

            Assert.True(module.TryGetMethods("$generator$one$0", out var body));
            Assert.Equal("V", body[0].ReturnType.Reference.Descriptor);
        }

        [Fact]
        public void AGeneratorIsAlsoAnIterator()
        {
            // §12.5: iterate() hands back the receiver, so a generator satisfies IIterator<T> as
            // well - which is what lets one be handed straight to something holding a cursor.
            var runtime = Run(
                "generator upTo(n: int): int {\n"
                    + "    var i = 1;\n"
                    + "    while (i <= n) { yield i; i = i + 1; }\n"
                    + "}\n"
                    + "fun drain(n: int): int {\n"
                    + "    let cursor: IIterator<int> = upTo(n);\n"
                    + "    var sum = 0;\n"
                    + "    while (cursor.moveNext()) { sum = sum + cursor.current; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(6, Int(runtime, "drain", SurtrValue.CreateInt(3)));
        }

        #endregion

        #region What §3.7 refuses

        [Fact]
        public void AYieldOutsideAGeneratorIsRejected()
            => AssertCode(Diagnose("fun f(): int { yield 1; return 0; }"), SurtrDiagnosticCode.InvalidYield);

        [Fact]
        public void AYieldInsideALambdaIsRejected()
            => AssertCode(
                Diagnose(
                    "generator f(): int {\n"
                        + "    let g = () => { yield 1; };\n"
                        + "    yield 2;\n"
                        + "}"),
                SurtrDiagnosticCode.InvalidYield);

        [Fact]
        public void AYieldInsideATryIsRejected()
            => AssertCode(
                Diagnose(
                    "generator f(): int {\n"
                        + "    try { yield 1; } catch (e: Exception) { }\n"
                        + "}"),
                SurtrDiagnosticCode.InvalidYield);

        [Fact]
        public void AYieldAfterATryIsFine()
        {
            var runtime = Run(
                "generator f(): int {\n"
                    + "    try { let x = 1; } catch (e: Exception) { }\n"
                    + "    yield 7;\n"
                    + "}\n"
                    + "fun total(): int {\n"
                    + "    var sum = 0;\n"
                    + "    for (x in f()) { sum = sum + x; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(7, Int(runtime, "total"));
        }

        [Fact]
        public void AGeneratorReturningVoidIsRejected()
            => AssertCode(Diagnose("generator f(): void { }"), SurtrDiagnosticCode.InvalidGeneratorDeclaration);

        [Fact]
        public void AGeneratorWithNoWrittenElementTypeIsRejected()
            => AssertCode(Diagnose("generator f() { yield 1; }"), SurtrDiagnosticCode.InvalidGeneratorDeclaration);

        [Fact]
        public void AnInlineGeneratorIsRejected()
            => AssertCode(
                Diagnose("inline generator f(): int { yield 1; }"),
                SurtrDiagnosticCode.InvalidGeneratorDeclaration);

        [Fact]
        public void ANativeGeneratorIsRejected()
            => AssertCode(
                Diagnose("native generator f(): int;"),
                SurtrDiagnosticCode.InvalidGeneratorDeclaration);

        [Fact]
        public void AConstGeneratorIsRejected()
            => AssertCode(
                Diagnose("const generator f(): int { yield 1; }"),
                SurtrDiagnosticCode.InvalidGeneratorDeclaration);

        [Fact]
        public void AGeneratorWithAnArrowBodyIsRejected()
            => AssertCode(Diagnose("generator f(): int => 1;"), SurtrDiagnosticCode.GeneratorNeedsABlockBody);

        [Fact]
        public void AReturnWithAValueInsideAGeneratorIsRejected()
            => AssertCode(
                Diagnose("generator f(): int { yield 1; return 2; }"),
                SurtrDiagnosticCode.CannotConvert);

        [Fact]
        public void AGeneratorThatNeverYieldsWarnsButCompiles()
        {
            var diagnostics = Diagnose("generator f(): int { }");

            AssertCode(diagnostics, SurtrDiagnosticCode.GeneratorNeverYields);
            Assert.DoesNotContain(diagnostics, d => d.Severity == SurtrDiagnosticSeverity.Error);
        }

        [Fact]
        public void AnElementThatDoesNotConvertIsRejected()
            => AssertCode(
                Diagnose("generator f(): int { yield \"nope\"; }"),
                SurtrDiagnosticCode.CannotConvert);

        #endregion

        #region Where a generator may be declared

        [Fact]
        public void AGeneratorInAnExtensionBlockWorks()
        {
            // §15: an extension's members go through the same member loop as a class's, so
            // `generator` costs nothing extra there - and the receiver is an ordinary first
            // parameter by the time it reaches emission.
            var runtime = Run(
                "class Box { public let n: int = 0; public constructor(n: int) { this.n = n; } }\n"
                    + "extension Box {\n"
                    + "    generator upTo(box: Box): int {\n"
                    + "        var i = 0;\n"
                    + "        while (i < box.n) { yield i; i = i + 1; }\n"
                    + "    }\n"
                    + "}\n"
                    + "fun total(n: int): int {\n"
                    + "    var sum = 0;\n"
                    + "    for (x in Box(n).upTo()) { sum = sum + x; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(6, Int(runtime, "total", SurtrValue.CreateInt(4)));
        }

        [Fact]
        public void AStaticGeneratorOnAClassWorks()
        {
            var runtime = Run(
                "class Numbers {\n"
                    + "    public static generator upTo(n: int): int {\n"
                    + "        var i = 1;\n"
                    + "        while (i <= n) { yield i; i = i + 1; }\n"
                    + "    }\n"
                    + "}\n"
                    + "fun total(n: int): int {\n"
                    + "    var sum = 0;\n"
                    + "    for (x in Numbers.upTo(n)) { sum = sum + x; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(10, Int(runtime, "total", SurtrValue.CreateInt(4)));
        }

        [Fact]
        public void TwoOverloadsOfOneGeneratorNameEachGetTheirOwnBody()
        {
            var runtime = Run(
                "generator upTo(n: int): int {\n"
                    + "    var i = 1;\n"
                    + "    while (i <= n) { yield i; i = i + 1; }\n"
                    + "}\n"
                    + "generator upTo(): int { yield 100; }\n"
                    + "fun both(n: int): int {\n"
                    + "    var sum = 0;\n"
                    + "    for (a in upTo(n)) { sum = sum + a; }\n"
                    + "    for (b in upTo()) { sum = sum + b; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(106, Int(runtime, "both", SurtrValue.CreateInt(3))); // (1+2+3) + 100
        }

        [Fact]
        public void AGenericGeneratorYieldsItsTypeParameter()
        {
            var runtime = Run(
                "generator twice<T>(value: T): T {\n"
                    + "    yield value;\n"
                    + "    yield value;\n"
                    + "}\n"
                    + "fun total(x: int): int {\n"
                    + "    var sum = 0;\n"
                    + "    for (v in twice<int>(x)) { sum = sum + v; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(14, Int(runtime, "total", SurtrValue.CreateInt(7)));
        }

        #endregion

        #region Exceptions

        [Fact]
        public void AnExceptionFromInsideTheBodySurfacesAtTheLoop()
        {
            var runtime = Run(
                "generator f(): int {\n"
                    + "    yield 1;\n"
                    + "    let bad = 1 / 0;\n"
                    + "    yield bad;\n"
                    + "}\n"
                    + "fun total(): int {\n"
                    + "    var sum = 0;\n"
                    + "    for (x in f()) { sum = sum + x; }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Throws<SurtrExecutionException>(() => Int(runtime, "total"));
        }

        [Fact]
        public void TheLoopCanCatchWhatTheBodyThrows()
        {
            // The consumer's own try/catch works normally - §5.4 only forbids a `yield` inside one.
            var runtime = Run(
                "generator f(): int {\n"
                    + "    yield 1;\n"
                    + "    let bad = 1 / 0;\n"
                    + "    yield bad;\n"
                    + "}\n"
                    + "fun total(): int {\n"
                    + "    var sum = 0;\n"
                    + "    try {\n"
                    + "        for (x in f()) { sum = sum + x; }\n"
                    + "    } catch (e: DivideByZeroException) {\n"
                    + "        sum = sum + 100;\n"
                    + "    }\n"
                    + "    return sum;\n"
                    + "}");

            Assert.Equal(101, Int(runtime, "total"));
        }

        #endregion
    }
}
