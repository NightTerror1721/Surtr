#nullable enable

using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Tests.Compiler.CodeGen
{
    /// <summary>
    /// The disposal protocol from source to execution: <c>IDisposable</c>, <c>using</c>, and the
    /// close a <c>for-in</c> emits on every way out of a loop.
    /// </summary>
    /// <remarks>
    /// What these pin is the promise <c>docs/Plan-Disposicion.md</c> §5.3 makes, which is narrower
    /// than it looks: the language guarantees a close for the two shapes that <em>consume</em> a
    /// resource - a <c>for-in</c> and a <c>using</c> - and guarantees nothing for one that is
    /// stored and abandoned. So the interesting cases are the four ways out of a loop, not the
    /// happy path.
    /// </remarks>
    public sealed class DisposalTests : IDisposable
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

        /// <summary>A resource whose <c>dispose</c> records that it ran, in a module-level counter.</summary>
        private const string Resource =
            "var closes: int = 0;\n"
            + "class Handle : IDisposable {\n"
            + "    public let id: int;\n"
            + "    public constructor(id: int) { this.id = id; }\n"
            + "    public fun dispose(): void { closes = closes + id; }\n"
            + "}\n";

        #region using

        [Fact]
        public void UsingClosesItsResourceOnTheWayOut()
        {
            var runtime = Run(
                Resource
                    + "fun run(): int {\n"
                    + "    using (let h = Handle(1)) { }\n"
                    + "    return closes;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void UsingClosesEvenWhenTheBodyReturns()
        {
            // The case the protected region cannot cover on its own: a `return` leaves the frame,
            // not the region, so the close has to be on the pending stack a `finally` uses.
            var runtime = Run(
                Resource
                    + "fun inner(): int {\n"
                    + "    using (let h = Handle(1)) { return 7; }\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "    let answer = inner();\n"
                    + "    return answer * 10 + closes;\n"
                    + "}");

            Assert.Equal(71, Int(runtime, "run"));
        }

        [Fact]
        public void UsingClosesWhenAnExceptionLeavesTheBlock()
        {
            var runtime = Run(
                Resource
                    + "fun run(): int {\n"
                    + "    try {\n"
                    + "        using (let h = Handle(1)) { throw InvalidOperationException(\"x\"); }\n"
                    + "    } catch (e: Exception) { }\n"
                    + "    return closes;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void SeveralResourcesCloseInReverseOrder()
        {
            // The second resource may have been opened from the first, so the first has to outlive
            // it. Recorded as a decimal string rather than a sum, which a sum could not tell apart.
            var runtime = Run(
                "var order: string = \"\";\n"
                    + "class Handle : IDisposable {\n"
                    + "    public let id: int;\n"
                    + "    public constructor(id: int) { this.id = id; }\n"
                    + "    public fun dispose(): void { order = order + \"$id\"; }\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "    using (let a = Handle(1), let b = Handle(2), let c = Handle(3)) { }\n"
                    + "    return order == \"321\" ? 1 : 0;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ANullResourceIsSkippedRatherThanRaising()
        {
            var runtime = Run(
                Resource
                    + "fun pick(want: bool): Handle? => want ? Handle(1) : null;\n"
                    + "fun run(): int {\n"
                    + "    using (let h = pick(false)) { }\n"
                    + "    return closes;\n"
                    + "}");

            Assert.Equal(0, Int(runtime, "run"));
        }

        [Fact]
        public void AResourceThatIsNotDisposableIsRejected()
            => AssertCode(
                Diagnose("class Plain { }\nfun run(): void { using (let p = Plain()) { } }"),
                SurtrDiagnosticCode.NotDisposable);

        [Fact]
        public void AResourceDeclaredVarIsRejected()
            => AssertCode(
                Diagnose("fun run(): void { using (var p = 1) { } }"),
                SurtrDiagnosticCode.InvalidUsingResource);

        #endregion

        #region for-in

        [Fact]
        public void AForInOverAGeneratorClosesItOnABreak()
        {
            // The whole point of the protocol: a `finally` inside a generator body runs when the
            // loop walks away from it, not only when the body is driven to its end.
            var runtime = Run(
                "var closed: int = 0;\n"
                    + "generator counted(): int {\n"
                    + "    try {\n"
                    + "        yield 1;\n"
                    + "        yield 2;\n"
                    + "        yield 3;\n"
                    + "    } finally {\n"
                    + "        closed = 1;\n"
                    + "    }\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "    for (x in counted()) { break; }\n"
                    + "    return closed;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AForInOverAGeneratorClosesItOnAReturn()
        {
            var runtime = Run(
                "var closed: int = 0;\n"
                    + "generator counted(): int {\n"
                    + "    try { yield 1; yield 2; } finally { closed = 1; }\n"
                    + "}\n"
                    + "fun inner(): int {\n"
                    + "    for (x in counted()) { return x; }\n"
                    + "    return 0;\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "    let first = inner();\n"
                    + "    return first * 10 + closed;\n"
                    + "}");

            Assert.Equal(11, Int(runtime, "run"));
        }

        [Fact]
        public void AGeneratorDrivenToItsEndStillRunsItsFinallyExactlyOnce()
        {
            // Closing an exhausted generator is a no-op, so the ordinary end of a loop must not run
            // the block a second time.
            var runtime = Run(
                "var closes: int = 0;\n"
                    + "generator counted(): int {\n"
                    + "    try { yield 1; yield 2; } finally { closes = closes + 1; }\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "    for (x in counted()) { }\n"
                    + "    return closes;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AForInThroughTheContractClosesItsCursorToo()
        {
            // The general path: the generator travels as an IIterable<int>, so the loop only knows
            // its cursor as an IIterator<int>. That is precisely why the cursor contract extends
            // IDisposable - otherwise this loop would have nothing to call.
            var runtime = Run(
                "var closed: int = 0;\n"
                    + "generator counted(): int {\n"
                    + "    try { yield 1; yield 2; yield 3; } finally { closed = 1; }\n"
                    + "}\n"
                    + "fun run(): int {\n"
                    + "    let source: IIterable<int> = counted();\n"
                    + "    for (x in source) { break; }\n"
                    + "    return closed;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        #endregion
    }
}
