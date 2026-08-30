#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Stdlib;
using System;
using System.IO;
using System.Linq;

namespace Surtr.Tests.Stdlib
{
    /// <summary>
    /// Regression coverage for <c>docs/Plan-Revision-Stdlib.md</c>: each test compiles a small
    /// driver alongside the real <c>.surtr</c> sources it exercises (not the committed
    /// <c>.surtrc</c> images - <see cref="SurtrStdlibTests"/> covers that transport path already),
    /// so a fix landing in a stdlib source file is what these assert against, with no build step in
    /// between to go stale.
    /// </summary>
    public sealed class SurtrStdlibBehaviorTests : IDisposable
    {
        private const string Root = "D:/proj/src";

        private readonly System.Collections.Generic.List<IDisposable> _owned = new System.Collections.Generic.List<IDisposable>();

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
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

        /// <summary>Reads a stdlib source file and derives its real module path from its location.</summary>
        private static (string ModulePath, string Text) StdlibSource(string relativePath)
        {
            string modulePath = "surtr." + relativePath.Replace(".surtr", string.Empty).Replace('/', '.');
            string text = File.ReadAllText(RepoRoot() + "/src/Surtr.Stdlib/src/surtr/" + relativePath);
            return (modulePath, text);
        }

        /// <summary>
        /// Compiles <paramref name="driverSource"/> (module <c>test</c>) together with the given
        /// stdlib sources (each under its real module path, so imports and native link names both
        /// resolve exactly as they do for the real build), and loads the result into a fresh runtime
        /// with every stdlib native body published.
        /// </summary>
        private SurtrRuntime BuildAndLoad(string driverSource, params string[] stdlibRelativePaths)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", "test", driverSource);

            foreach (string relative in stdlibRelativePaths)
            {
                var (modulePath, text) = StdlibSource(relative);
                project.AddSourceFile(Root + "/surtr/" + relative, modulePath, text);
            }

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

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            SurtrStdlib.RegisterNativeBodies(runtime);

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            return runtime;
        }

        private static SurtrMethodInfo Function(SurtrRuntime runtime, string name)
        {
            Assert.True(runtime.TryGetModule("test", out var module), "No 'test' module was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"'test' declares no '{name}'.");
            return overloads[0];
        }

        private static int Int(SurtrRuntime runtime, string name)
            => runtime.Invoke(Function(runtime, name)).AsInt;

        private static bool Bool(SurtrRuntime runtime, string name)
            => runtime.Invoke(Function(runtime, name)).AsBool;

        // ── B1: StringBuilder ────────────────────────────────────────────────

        /// <summary>
        /// A fresh StringBuilder used to report `length == initialCapacity` (16 phantom NUL chars)
        /// because the constructor allocated `array&lt;char&gt;(initialCapacity)` - which allocates
        /// by length, not by reserve - and nothing tracked real content length separately.
        /// </summary>
        [Fact]
        public void AFreshStringBuilderIsEmpty()
        {
            var runtime = BuildAndLoad(
                "import surtr.text.StringBuilder;\n"
                    + "fun run(): int { return StringBuilder().length; }\n",
                "text/StringBuilder.surtr");

            Assert.Equal(0, Int(runtime, "run"));
        }

        /// <summary>
        /// The other half of B1: `toString()` used to return the real content prefixed with
        /// `initialCapacity` NUL characters instead of just the appended text.
        /// </summary>
        [Fact]
        public void StringBuilderToStringHasNoPhantomPrefix()
        {
            var runtime = BuildAndLoad(
                "import surtr.text.StringBuilder;\n"
                    + "fun run(): bool {\n"
                    + "    let sb = StringBuilder();\n"
                    + "    sb.append(\"hi\");\n"
                    + "    return sb.toString() == \"hi\" && sb.length == 2;\n"
                    + "}\n",
                "text/StringBuilder.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        /// <summary>Growth past the initial capacity still preserves every character, in order.</summary>
        [Fact]
        public void StringBuilderGrowsPastInitialCapacityCorrectly()
        {
            var runtime = BuildAndLoad(
                "import surtr.text.StringBuilder;\n"
                    + "fun run(): bool {\n"
                    + "    let sb = StringBuilder(2);\n"
                    + "    for (var i = 0; i < 20; i++) sb.appendChar('a');\n"
                    + "    if (sb.length != 20) return false;\n"
                    + "    for (var i = 0; i < 20; i++) { if (sb[i] != 'a') return false; }\n"
                    + "    return true;\n"
                    + "}\n",
                "text/StringBuilder.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        /// <summary>`operator[]` get/set (D1's sibling fix, added onto StringBuilder alongside B1).</summary>
        [Fact]
        public void StringBuilderIndexerReadsAndWrites()
        {
            var runtime = BuildAndLoad(
                "import surtr.text.StringBuilder;\n"
                    + "fun run(): bool {\n"
                    + "    let sb = StringBuilder();\n"
                    + "    sb.append(\"hi\");\n"
                    + "    sb[0] = 'H';\n"
                    + "    return sb[0] == 'H' && sb.toString() == \"Hi\";\n"
                    + "}\n",
                "text/StringBuilder.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        // ── B2: Profiler / Stopwatch ─────────────────────────────────────────

        /// <summary>
        /// `Stopwatch.start()`/`stop()` used to only flip a `_running` flag - `stopwatchTimestamp()`
        /// was declared but never called, so a profiled scope always measured zero regardless of how
        /// much work ran inside it.
        /// </summary>
        [Fact]
        public void ProfilerScopeMeasuresRealElapsedTime()
        {
            var runtime = BuildAndLoad(
                "import surtr.diagnostics.Profiler;\n"
                    + "fun run(): bool {\n"
                    + "    let p = Profiler();\n"
                    + "    let scope = p.beginScope(\"work\");\n"
                    + "    var x = 0;\n"
                    + "    for (var i = 0; i < 300000; i++) { x = x + i; }\n"
                    + "    scope.dispose();\n"
                    + "    return p.getEntry(0).elapsed > 0.0;\n"
                    + "}\n",
                "diagnostics/Profiler.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        /// <summary>A Stopwatch queried while still running reports a live, growing elapsed time.</summary>
        [Fact]
        public void StopwatchElapsedGrowsWhileRunning()
        {
            var runtime = BuildAndLoad(
                "import surtr.diagnostics.Profiler;\n"
                    + "fun run(): bool {\n"
                    + "    let sw = Stopwatch();\n"
                    + "    sw.start();\n"
                    + "    var x = 0;\n"
                    + "    for (var i = 0; i < 300000; i++) { x = x + i; }\n"
                    + "    let live = sw.elapsed;\n"
                    + "    sw.stop();\n"
                    + "    return live > 0.0 && sw.elapsed >= live;\n"
                    + "}\n",
                "diagnostics/Profiler.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        // ── B3: BinaryReader ─────────────────────────────────────────────────

        /// <summary>
        /// A stream that ends after one byte used to make `readInt()` silently fold EOF's -1 into
        /// the shift/OR chain and return garbage. It must now throw instead.
        /// </summary>
        [Fact]
        public void ReadIntThrowsOnATruncatedStreamInsteadOfReturningGarbage()
        {
            var runtime = BuildAndLoad(
                "import surtr.io.Stream;\n"
                    + "import surtr.io.BinaryReader;\n"
                    + "import surtr.io.MemoryStream;\n"
                    + "fun run(): bool {\n"
                    + "    let ms = MemoryStream(bytes.repeat(7, 1), true);\n"
                    + "    let reader = BinaryReader(ms);\n"
                    + "    var threw = false;\n"
                    + "    try { reader.readInt(); }\n"
                    + "    catch (e: EndOfStreamException) { threw = true; }\n"
                    + "    return threw;\n"
                    + "}\n",
                "core/byte.surtr", "io/Enums.surtr", "io/Stream.surtr", "io/MemoryStream.surtr", "io/BinaryReader.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        /// <summary>A clean EOF before any byte of the value is read stays a soft `0`, not a throw.</summary>
        [Fact]
        public void ReadIntAtCleanEofReturnsZero()
        {
            var runtime = BuildAndLoad(
                "import surtr.io.BinaryReader;\n"
                    + "import surtr.io.MemoryStream;\n"
                    + "fun run(): bool {\n"
                    + "    let ms = MemoryStream(bytes.repeat(0, 0), true);\n"
                    + "    let reader = BinaryReader(ms);\n"
                    + "    return reader.readInt() == 0;\n"
                    + "}\n",
                "core/byte.surtr", "io/Enums.surtr", "io/Stream.surtr", "io/MemoryStream.surtr", "io/BinaryReader.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        /// <summary>
        /// `readBytes(count)` past the end of the stream used to return a `count`-length buffer
        /// silently zero-padded past what was actually read. It must now report the real length.
        /// </summary>
        [Fact]
        public void ReadBytesPastEndOfStreamReturnsOnlyWhatWasRead()
        {
            var runtime = BuildAndLoad(
                "import surtr.io.BinaryReader;\n"
                    + "import surtr.io.MemoryStream;\n"
                    + "fun run(): bool {\n"
                    + "    let ms = MemoryStream(bytes.repeat(9, 3), true);\n"
                    + "    let reader = BinaryReader(ms);\n"
                    + "    let result = reader.readBytes(10);\n"
                    + "    return result.length == 3;\n"
                    + "}\n",
                "core/byte.surtr", "io/Enums.surtr", "io/Stream.surtr", "io/MemoryStream.surtr", "io/BinaryReader.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        // ── D4: ObjectDisposedException ──────────────────────────────────────

        /// <summary>
        /// Every io/ type used to throw a generic `InvalidOperationException("... is closed")` by
        /// hand; a disposed stream must now raise the dedicated `ObjectDisposedException`, so a
        /// caller can `catch` that case specifically.
        /// </summary>
        [Fact]
        public void ADisposedMemoryStreamThrowsObjectDisposedException()
        {
            var runtime = BuildAndLoad(
                "import surtr.io.Stream;\n"
                    + "import surtr.io.MemoryStream;\n"
                    + "fun run(): bool {\n"
                    + "    let ms = MemoryStream(4);\n"
                    + "    ms.dispose();\n"
                    + "    var threw = false;\n"
                    + "    try { ms.readByte(); }\n"
                    + "    catch (e: ObjectDisposedException) { threw = true; }\n"
                    + "    return threw;\n"
                    + "}\n",
                "core/byte.surtr", "io/Enums.surtr", "io/Stream.surtr", "io/MemoryStream.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        // ── B4: Set.copyTo ───────────────────────────────────────────────────

        /// <summary>
        /// Copying an empty set into an empty array at index 0 used to throw
        /// `IndexOutOfRangeException` because the guard rejected `arrayIndex >= array.length` even
        /// when there is nothing to copy.
        /// </summary>
        [Fact]
        public void CopyingAnEmptySetIntoAnEmptyArrayDoesNotThrow()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Set;\n"
                    + "fun run(): bool {\n"
                    + "    let s = Set<int>();\n"
                    + "    let target: int[] = [];\n"
                    + "    s.copyTo(target, 0);\n"
                    + "    return true;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/Set.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        // ── D1: List<T> operator[] ───────────────────────────────────────────

        /// <summary>`List&lt;T&gt;` gets the same `operator[]` its sibling `LinkedList&lt;T&gt;` already had.</summary>
        [Fact]
        public void ListSupportsIndexerReadAndWrite()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.List;\n"
                    + "fun run(): bool {\n"
                    + "    let list = List<int>();\n"
                    + "    list.add(10); list.add(20);\n"
                    + "    if (list[1] != 20) return false;\n"
                    + "    list[1] = 99;\n"
                    + "    return list[1] == 99;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        // ── D3: Stack<T> iteration order ─────────────────────────────────────

        /// <summary>
        /// Iterating a Stack used to walk bottom-to-top (insertion order), disagreeing with what a
        /// sequence of `pop()` calls would hand back. It must now walk top-to-bottom.
        /// </summary>
        [Fact]
        public void StackIteratesInPopOrder()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Stack;\n"
                    + "fun run(): bool {\n"
                    + "    let s = Stack<int>();\n"
                    + "    s.push(1); s.push(2); s.push(3);\n"
                    + "    var expected = 3;\n"
                    + "    for (var x in s) {\n"
                    + "        if (x != expected) return false;\n"
                    + "        expected = expected - 1;\n"
                    + "    }\n"
                    + "    return expected == 0;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/Stack.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        // ── D2: Deque<T> as its own doubly-linked structure ──────────────────

        /// <summary>
        /// Exercises both ends of the standalone `Deque&lt;T&gt;` (no longer a `Queue&lt;T&gt;`
        /// subclass): push/pop at the front and back, and the length/iteration/ICollection surface
        /// it still owes `IDeque&lt;T&gt;`.
        /// </summary>
        [Fact]
        public void DequeWorksAtBothEndsAndThroughTheCollectionContract()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Queue;\n"
                    + "fun run(): int {\n"
                    + "    let d = Deque<int>();\n"
                    + "    d.enqueueBack(2); d.enqueueBack(3);\n"
                    + "    d.enqueueFront(1);\n"
                    + "    if (d.length != 3) return 1;\n"
                    + "    if (d.front != 1 || d.back != 3) return 2;\n"
                    + "    if (d.dequeueFront() != 1) return 3;\n"
                    + "    if (d.dequeueBack() != 3) return 4;\n"
                    + "    if (d.length != 1 || d.dequeueFront() != 2) return 5;\n"
                    + "    if (!d.isEmpty) return 6;\n"
                    + "    d.enqueueBack(10); d.enqueueBack(20); d.enqueueBack(30);\n"
                    + "    var sum = 0;\n"
                    + "    for (var x in d) sum = sum + x;\n"
                    + "    if (sum != 60) return 7;\n"
                    + "    if (!d.contains(20) || d.contains(99)) return 8;\n"
                    + "    return 0;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/Queue.surtr");

            Assert.Equal(0, Int(runtime, "run"));
        }

        /// <summary>Queue&lt;T&gt; itself (untouched by D2) keeps working exactly as before.</summary>
        [Fact]
        public void QueueStillWorksAfterDequeWasSeparatedFromIt()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Queue;\n"
                    + "fun run(): bool {\n"
                    + "    let q = Queue<int>();\n"
                    + "    q.enqueue(1); q.enqueue(2); q.enqueue(3);\n"
                    + "    if (q.dequeue() != 1) return false;\n"
                    + "    var sum = 0;\n"
                    + "    for (var x in q) sum = sum + x;\n"
                    + "    return sum == 5;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/Queue.surtr");

            Assert.True(Bool(runtime, "run"));
        }
    }
}
