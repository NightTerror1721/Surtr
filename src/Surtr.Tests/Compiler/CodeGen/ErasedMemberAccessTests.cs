#nullable enable

using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Surtr.Tests.Compiler.CodeGen
{
    /// <summary>
    /// Members declared against a contract's own type parameter, read and written through the
    /// contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §1.11 makes the compiler owe two things wherever a value crosses an erased slot: box it on
    /// the way in, and cast it back on the way out. A <em>call</em> got both - the binder writes a
    /// conversion node for each argument, and <c>UnerasedCallResult</c> handles the return - but a
    /// <em>property</em> reached the same erased slot by a different route and got neither, because
    /// the binder checks a property access against the property's <em>substituted</em> type and so
    /// finds nothing to convert.
    /// </para>
    /// <para>
    /// Both halves failed silently rather than loudly, which is why this file exists. A read left a
    /// reference where an <c>int</c> was expected and the interpreter went on to do arithmetic on
    /// its payload - an entity id - so <c>IIterator&lt;int&gt;.current</c> answered small, plausible,
    /// wrong numbers. A write put a raw primitive where a reference was expected and the next cast
    /// dereferenced it. Every assertion here uses values that cannot be reached by accident from an
    /// entity id.
    /// </para>
    /// </remarks>
    public sealed class ErasedMemberAccessTests : IDisposable
    {
        private const string Root = "D:/proj/src";

        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
        }

        private int Run(string source, string function, params SurtrValue[] arguments)
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

            Assert.True(runtime.TryGetModule("game.core.Test", out var loaded));
            Assert.True(loaded.TryGetMethods(function, out var overloads));

            return runtime.Invoke(overloads[0], arguments).AsInt;
        }

        [Fact]
        public void ReadingAPropertyThroughAGenericContractUnboxesTheResult()
        {
            // The original repro: a cursor walked through IIterator<int>. Before the fix this
            // answered 65 for n = 10 - the sum of the ten boxes' entity ids - instead of 45.
            Assert.Equal(45, Run(
                "class RangeCursor : IIterator<int> {\n"
                    + "    private var _i: int = 0;\n"
                    + "    private let _n: int;\n"
                    + "    public constructor(n: int) { this._n = n; }\n"
                    + "    public current: int { get => _i - 1; }\n"
                    + "    public fun dispose(): void { }\n"
                    + "    public fun moveNext(): bool {\n"
                    + "        if (_i >= _n) { return false; }\n"
                    + "        _i = _i + 1;\n"
                    + "        return true;\n"
                    + "    }\n"
                    + "}\n"
                    + "fun walk(n: int): int {\n"
                    + "    let cursor: IIterator<int> = RangeCursor(n);\n"
                    + "    var acc: int = 0;\n"
                    + "    while (cursor.moveNext()) { acc = acc + cursor.current; }\n"
                    + "    return acc;\n"
                    + "}",
                "walk",
                SurtrValue.CreateInt(10)));
        }

        [Fact]
        public void TheSameReadThroughTheConcreteTypeStillWorks()
        {
            // The half that was already right, kept so a fix on the erased path cannot break it.
            Assert.Equal(45, Run(
                "class RangeCursor : IIterator<int> {\n"
                    + "    private var _i: int = 0;\n"
                    + "    private let _n: int;\n"
                    + "    public constructor(n: int) { this._n = n; }\n"
                    + "    public current: int { get => _i - 1; }\n"
                    + "    public fun dispose(): void { }\n"
                    + "    public fun moveNext(): bool {\n"
                    + "        if (_i >= _n) { return false; }\n"
                    + "        _i = _i + 1;\n"
                    + "        return true;\n"
                    + "    }\n"
                    + "}\n"
                    + "fun walk(n: int): int {\n"
                    + "    let cursor = RangeCursor(n);\n"
                    + "    var acc: int = 0;\n"
                    + "    while (cursor.moveNext()) { acc = acc + cursor.current; }\n"
                    + "    return acc;\n"
                    + "}",
                "walk",
                SurtrValue.CreateInt(10)));
        }

        [Fact]
        public void WritingAPropertyThroughAGenericContractBoxesTheValue()
        {
            // The mirror: before the fix the raw primitive reached a slot the accessor's own cast
            // then dereferenced, which surfaced as a null reference rather than a wrong number.
            Assert.Equal(77, Run(
                "interface IBox<T> {\n"
                    + "    value: T { get; set; }\n"
                    + "}\n"
                    + "class IntBox : IBox<int> {\n"
                    + "    private var _v: int = 0;\n"
                    + "    public value: int { get { return _v; } set { _v = value; } }\n"
                    + "}\n"
                    + "fun roundTrip(n: int): int {\n"
                    + "    let b: IBox<int> = IntBox();\n"
                    + "    b.value = n;\n"
                    + "    return b.value;\n"
                    + "}",
                "roundTrip",
                SurtrValue.CreateInt(77)));
        }

        [Fact]
        public void AFloatCrossesTheSameSlotBothWays()
        {
            // A second primitive family, because the box on the way in is chosen from the value's
            // own tag rather than from a static type - so `int` passing is not evidence for `float`.
            Assert.Equal(1, Run(
                "interface IBox<T> {\n"
                    + "    value: T { get; set; }\n"
                    + "}\n"
                    + "class FloatBox : IBox<float> {\n"
                    + "    private var _v: float = 0.0;\n"
                    + "    public value: float { get { return _v; } set { _v = value; } }\n"
                    + "}\n"
                    + "fun roundTrip(n: int): int {\n"
                    + "    let b: IBox<float> = FloatBox();\n"
                    + "    b.value = 2.5;\n"
                    + "    return b.value == 2.5 ? 1 : 0;\n"
                    + "}",
                "roundTrip",
                SurtrValue.CreateInt(0)));
        }

        [Fact]
        public void AReferenceElementNeedsNoBoxAndIsUnaffected()
        {
            // Nothing is boxed or unboxed when the substituted type is already a reference; the
            // cast the read inserts is a check, and it has to pass.
            Assert.Equal(5, Run(
                "interface IBox<T> {\n"
                    + "    value: T { get; set; }\n"
                    + "}\n"
                    + "class TextBox : IBox<string> {\n"
                    + "    private var _v: string = \"\";\n"
                    + "    public value: string { get { return _v; } set { _v = value; } }\n"
                    + "}\n"
                    + "fun roundTrip(n: int): int {\n"
                    + "    let b: IBox<string> = TextBox();\n"
                    + "    b.value = \"hello\";\n"
                    + "    return b.value.length;\n"
                    + "}",
                "roundTrip",
                SurtrValue.CreateInt(0)));
        }

        [Fact]
        public void AMethodReturningTThroughAContractWasAlreadyRight()
        {
            // The path the binder's own conversion nodes and UnerasedCallResult already covered.
            // Here as the control: it is what the property paths were supposed to match.
            Assert.Equal(9, Run(
                "interface ISource<T> {\n"
                    + "    fun take(): T;\n"
                    + "}\n"
                    + "class Nine : ISource<int> {\n"
                    + "    public fun take(): int { return 9; }\n"
                    + "}\n"
                    + "fun read(n: int): int {\n"
                    + "    let s: ISource<int> = Nine();\n"
                    + "    return s.take();\n"
                    + "}",
                "read",
                SurtrValue.CreateInt(0)));
        }

        [Fact]
        public void TheStdlibsOwnIteratorsWalkThroughTheContract()
        {
            // The twelve iterators in Sequence.surtr have exactly the shape that was miscompiled -
            // a `current` property declared against IIterator<T>. Their own tests reach them
            // through Sequence's methods; this walks one through the contract directly, which is
            // the way that was broken.
            string collections = RepoRoot() + "/src/Surtr.Stdlib/src/surtr/collections";

            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "import surtr.collections.Sequence;\n"
                    + "fun walk(n: int): int {\n"
                    + "    let cursor: IIterator<int> = Sequence<int>.of(4, 5, 6).iterate();\n"
                    + "    var acc: int = 0;\n"
                    + "    while (cursor.moveNext()) { acc = acc + cursor.current; }\n"
                    + "    return acc;\n"
                    + "}");

            project.AddSourceFile(
                Root + "/surtr/collections/Collection.surtr",
                "surtr.collections.Collection",
                File.ReadAllText(collections + "/Collection.surtr"));
            project.AddSourceFile(
                Root + "/surtr/collections/List.surtr",
                "surtr.collections.List",
                File.ReadAllText(collections + "/List.surtr"));
            project.AddSourceFile(
                Root + "/surtr/collections/Set.surtr",
                "surtr.collections.Set",
                File.ReadAllText(collections + "/Set.surtr"));
            project.AddSourceFile(
                Root + "/surtr/collections/Sequence.surtr",
                "surtr.collections.Sequence",
                File.ReadAllText(collections + "/Sequence.surtr"));

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

            Assert.True(runtime.TryGetModule("game.core.Test", out var loaded));
            Assert.True(loaded.TryGetMethods("walk", out var overloads));

            Assert.Equal(15, runtime.Invoke(overloads[0], SurtrValue.CreateInt(0)).AsInt);
        }

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Surtr.sln")))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Could not locate the repo root from " + AppContext.BaseDirectory);
        }
    }
}
