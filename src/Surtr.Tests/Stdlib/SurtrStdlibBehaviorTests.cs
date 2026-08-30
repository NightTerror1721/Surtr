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

        /// <summary>
        /// Compiles two source modules together (as opposed to <see cref="BuildAndLoad"/>'s one
        /// driver module plus stdlib files) and loads both. What B6's regressions below need: a
        /// real cross-module caller, not the same-module path B6 never broke.
        /// </summary>
        private SurtrRuntime BuildTwoModules(string otroSource, string probeSource)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/otro/otromodulo.surtr", "otromodulo", otroSource);
            project.AddSourceFile(Root + "/game/core/probe.surtr", "probe", probeSource);

            var compilation = SurtrCompilation.Create(project);
            _owned.Add(compilation);
            var binder = compilation.Bind();
            binder.BindBodies();
            Assert.True(!compilation.HasErrors, "Binding reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var emitter = new ModuleEmitter(compilation, binder);
            Assert.True(emitter.TryEmit(), "Emission reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);
            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            return runtime;
        }

        private const string OtroModuloSource =
            "public value class Vector2 { public let x: float; public let y: float;\n"
                + "    public constructor(x: float, y: float) { this.x = x; this.y = y; } }\n"
                + "public fun scaleIt(a: Vector2, s: float): Vector2 => Vector2(a.x * s, a.y * s);\n"
                + "public fun makeIt(s: float): Vector2 => Vector2(s, s);\n"
                + "public fun sumIt(a: Vector2): float => a.x + a.y;\n";

        /// <summary>
        /// A cross-module call returning (but not taking) a multi-field value class, called from a
        /// separate driver module. Trivial enough to be auto-inlined by <c>ShouldInlineByCost</c>,
        /// so on its own this never actually exercised the real CallExternal path B6 lived in - kept
        /// as a baseline the other B6 tests below contrast with.
        /// </summary>
        [Fact]
        public void CrossModuleCallReturningValueClassWorks()
        {
            var runtime = BuildTwoModules(OtroModuloSource,
                "import otromodulo;\n"
                    + "fun run(): float { let v = makeIt(3.0); return v.x; }\n");

            Assert.Equal(3.0, runtime.Invoke(FunctionIn(runtime, "probe", "run")).AsFloat);
        }

        /// <summary>The mirror of the above: taking (but not returning) a multi-field value class.</summary>
        [Fact]
        public void CrossModuleCallTakingValueClassWorks()
        {
            var runtime = BuildTwoModules(OtroModuloSource,
                "import otromodulo;\n"
                    + "fun run(): float { let s = sumIt(Vector2(1.0, 2.0)); return s; }\n");

            Assert.Equal(3.0, runtime.Invoke(FunctionIn(runtime, "probe", "run")).AsFloat);
        }

        /// <summary>
        /// Regression for B6 (docs/Plan-Revision-Stdlib.md §2.6): the exact minimal repro from the
        /// doc. A cross-module call taking AND returning a multi-field value class used to crash
        /// emission of the *caller* ("Operand stack underflow") - <c>scaleIt</c> here is complex
        /// enough that the inliner leaves it as a real call, which is what actually exercised the
        /// bug (see <see cref="CrossModuleCallReturningValueClassWorks"/>'s remark: the trivial
        /// single-expression shape never did, regardless of which side the value class was on).
        /// </summary>
        [Fact]
        public void CrossModuleCallTakingAndReturningValueClassWorks()
        {
            var runtime = BuildTwoModules(OtroModuloSource,
                "import otromodulo;\n"
                    + "fun run(): float { let v = scaleIt(Vector2(1.0, 2.0), 3.0); return v.x; }\n"
                    + "fun runY(): float { let v = scaleIt(Vector2(1.0, 2.0), 3.0); return v.y; }\n");

            Assert.Equal(3.0, runtime.Invoke(FunctionIn(runtime, "probe", "run")).AsFloat);
            Assert.Equal(6.0, runtime.Invoke(FunctionIn(runtime, "probe", "runY")).AsFloat);
        }

        /// <summary>
        /// Confirms B6's real shape: a value-class-returning cross-module call underflows whenever
        /// it is NOT auto-inlined, regardless of whether any argument is also a value class. A large,
        /// non-trivial body (<c>ShouldInlineByCost</c> refuses to splice it) that only ever takes a
        /// scalar still hits the same bug the doc's own repro does - confirming the "takes AND
        /// returns" framing described which examples happened to trigger it, not the actual cause.
        /// </summary>
        [Fact]
        public void CrossModuleCallReturningValueClassWorksWhenNotInlined()
        {
            var otro =
                "public value class Vector2 { public let x: float; public let y: float;\n"
                    + "    public constructor(x: float, y: float) { this.x = x; this.y = y; } }\n"
                    + "public fun makeItBig(s: float): Vector2 {\n"
                    + "    var a = s; var b = s; var c = s; var d = s; var e = s;\n"
                    + "    a += 1.0; b += 2.0; c += 3.0; d += 4.0; e += 5.0;\n"
                    + "    a *= 2.0; b *= 2.0; c *= 2.0; d *= 2.0; e *= 2.0;\n"
                    + "    let total = a + b + c + d + e;\n"
                    + "    return Vector2(total, total);\n"
                    + "}\n";

            var runtime = BuildTwoModules(otro,
                "import otromodulo;\n"
                    + "fun run(): float { let v = makeItBig(3.0); return v.x; }\n");

            // a=b=c=d=e=3.0, +1..+5, *2 => (4,5,6,7,8)*2 = (8,10,12,14,16), total=60
            Assert.Equal(60.0, runtime.Invoke(FunctionIn(runtime, "probe", "run")).AsFloat);
        }

        private static SurtrMethodInfo FunctionIn(SurtrRuntime runtime, string modulePath, string name)
        {
            Assert.True(runtime.TryGetModule(modulePath, out var module), $"No module '{modulePath}' was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"'{modulePath}' declares no '{name}'.");
            return overloads[0];
        }

        private static SurtrMethodInfo Function(SurtrRuntime runtime, string name) => FunctionIn(runtime, "test", name);

        private static int Int(SurtrRuntime runtime, string name)
            => runtime.Invoke(Function(runtime, name)).AsInt;

        private static bool Bool(SurtrRuntime runtime, string name)
            => runtime.Invoke(Function(runtime, name)).AsBool;

        private static double Float(SurtrRuntime runtime, string name)
            => runtime.Invoke(Function(runtime, name)).AsFloat;

        private static bool BoolIn(SurtrRuntime runtime, string modulePath, string name)
            => runtime.Invoke(FunctionIn(runtime, modulePath, name)).AsBool;

        /// <summary>
        /// Regression for B9 (docs/Plan-Revision-Stdlib.md §2.9), with the minimal repro from the
        /// doc rather than through <c>Sequence&lt;T&gt;</c>: a non-generic <c>int?</c> comparing
        /// against <c>null</c> always worked (<c>runConcrete</c>); the exact same comparison through
        /// a generic method's substituted <c>T?</c> return did not (<c>runGeneric</c>). Also checks
        /// the present side explicitly with a value of <c>0</c> (<c>runPresent</c>/
        /// <c>runPresentNotNull</c>) - the fix tells a genuine absent value apart from a present
        /// primitive that happens to be zero, and a naive fix that tested the returned reference's
        /// raw payload for zero (rather than its own tag) would pass every other test here and still
        /// misreport this one.
        /// </summary>
        [Fact]
        public void GenericMethodNullablePrimitiveReturnComparesCorrectlyAgainstNull()
        {
            var runtime = BuildAndLoad(
                "fun getNullConcrete(): int? { return null; }\n"
                    + "fun runConcrete(): bool { return getNullConcrete() == null; }\n"
                    + "fun getNullGeneric<T>(): T? { return null; }\n"
                    + "fun runGeneric(): bool { return getNullGeneric<int>() == null; }\n"
                    + "fun getPresentGeneric<T>(v: T): T? { return v; }\n"
                    + "fun runPresent(): bool { let r = getPresentGeneric<int>(0); return r == 0; }\n"
                    + "fun runPresentNotNull(): bool { let r = getPresentGeneric<int>(0); return r != null; }\n");

            Assert.True(Bool(runtime, "runConcrete"));
            Assert.True(Bool(runtime, "runGeneric"));
            Assert.True(Bool(runtime, "runPresent"));
            Assert.True(Bool(runtime, "runPresentNotNull"));
        }

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

        // ── C2: ReadOnlyCollection<T> / asReadOnly() ─────────────────────────

        /// <summary>
        /// `ReadOnlyCollection&lt;T&gt;` used to be `private` with no caller anywhere in the stdlib.
        /// Now `List&lt;T&gt;.asReadOnly()`/`Set&lt;T&gt;.asReadOnly()` construct one, and the view
        /// stays live over the underlying collection rather than snapshotting it. Exercises
        /// `contains()` through the interface too - see <see cref="SetIsSubsetOfWorksAcrossTwoInstances"/>
        /// for why that used to crash unconditionally (B5, fixed alongside this test).
        /// </summary>
        [Fact]
        public void ListAndSetAsReadOnlyStayLiveOverTheSource()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.List;\n"
                    + "import surtr.collections.Set;\n"
                    + "fun run(): bool {\n"
                    + "    let list = List<int>();\n"
                    + "    let view = list.asReadOnly();\n"
                    + "    if (view.length != 0) return false;\n"
                    + "    list.add(1); list.add(2);\n"
                    + "    if (view.length != 2 || !view.contains(2)) return false;\n"
                    + "    var sum = 0;\n"
                    + "    for (x in view) sum = sum + x;\n"
                    + "    if (sum != 3) return false;\n"
                    + "\n"
                    + "    let s = Set<int>();\n"
                    + "    let setView = s.asReadOnly();\n"
                    + "    s.add(5);\n"
                    + "    return setView.length == 1 && setView.contains(5);\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr", "collections/Set.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        /// <summary>
        /// Regression for B5 (docs/Plan-Revision-Stdlib.md): every `ReadOnlySet&lt;T&gt;`/`Set&lt;T&gt;`
        /// method taking an `IReadOnlySet&lt;T&gt;`/`ISet&lt;T&gt;` parameter and calling a
        /// `T`-parameterised method on it (`other.contains(item)`) used to crash the VM with
        /// `InvalidCastException: A '&lt;T&gt;' cannot be cast to 'erased'`, for every element type -
        /// a compiler bug (`ModuleEmitter.Narrow`, `src/Surtr.Compiler/CodeGen/ModuleEmitter.cs`),
        /// not a stdlib one. A generic class keeps one compiled body regardless of instantiation
        /// (§6), so a member's own class-level type parameter is still erased in that body; the
        /// interface bridge `Narrow` emits to read a contract slot's erased argument back into the
        /// concrete parameter type had no case for the destination itself still being a bare type
        /// parameter, so it fell into the general "cast to a concrete type" path and tried to cast an
        /// already-erased value to the very marker class (`SurtrBuiltIns.Erased`) nothing is ever "a
        /// subclass of". Exercises both set-vs-set (`isSubsetOf`) and set-vs-set-of-strings, since
        /// the bug did not depend on the element type at all.
        /// </summary>
        [Fact]
        public void SetIsSubsetOfWorksAcrossTwoInstances()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Set;\n"
                    + "fun run(): bool {\n"
                    + "    let a = Set<int>();\n"
                    + "    a.add(1); a.add(2);\n"
                    + "    let b = Set<int>();\n"
                    + "    b.add(1); b.add(2); b.add(3);\n"
                    + "    if (!a.isSubsetOf(b)) return false;\n"
                    + "    if (b.isSubsetOf(a)) return false;\n"
                    + "\n"
                    + "    let sa = Set<string>();\n"
                    + "    sa.add(\"x\"); sa.add(\"y\");\n"
                    + "    let sb = Set<string>();\n"
                    + "    sb.add(\"x\"); sb.add(\"y\"); sb.add(\"z\");\n"
                    + "    return sa.isSubsetOf(sb);\n"
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

        // ── Fase 5: Angle ─────────────────────────────────────────────────────

        [Fact]
        public void AngleConvertsBetweenDegreesAndRadians()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Angle;\n"
                    + "fun run(): bool {\n"
                    + "    let a = Angle.fromDegrees(180.0);\n"
                    + "    if (a.radians < 3.14159 || a.radians > 3.14160) return false;\n"
                    + "    let d = a.degrees;\n"
                    + "    return d > 179.999 && d < 180.001;\n"
                    + "}\n",
                "math/Math.surtr", "math/Angle.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void AngleNormalizedWrapsIntoZeroToTwoPi()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Angle;\n"
                    + "fun run(): bool {\n"
                    + "    let a = Angle.fromDegrees(-90.0).normalized();\n"
                    + "    let d = a.degrees;\n"
                    + "    return d > 269.999 && d < 270.001;\n"
                    + "}\n",
                "math/Math.surtr", "math/Angle.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void AngleOperatorsAndComparison()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Angle;\n"
                    + "fun run(): bool {\n"
                    + "    let a = Angle.fromDegrees(30.0);\n"
                    + "    let b = Angle.fromDegrees(60.0);\n"
                    + "    let sum = a + b;\n"
                    + "    if (sum.degrees < 89.999 || sum.degrees > 90.001) return false;\n"
                    + "    if (!(a < b)) return false;\n"
                    + "    if (a == b) return false;\n"
                    + "    return (a * 2.0).degrees > 59.999 && (a * 2.0).degrees < 60.001;\n"
                    + "}\n",
                "math/Math.surtr", "math/Angle.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        // ── Fase 5: Vector2 / Vector3 ────────────────────────────────────────

        [Fact]
        public void Vector2ArithmeticLengthAndDot()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Vector;\n"
                    + "fun run(): bool {\n"
                    + "    let a = Vector2(3.0, 4.0);\n"
                    + "    if (a.length() < 4.999 || a.length() > 5.001) return false;\n"
                    + "    let b = a + Vector2(1.0, 1.0);\n"
                    + "    if (b.x != 4.0 || b.y != 5.0) return false;\n"
                    + "    let scaled = a * 2.0;\n"
                    + "    if (scaled.x != 6.0 || scaled.y != 8.0) return false;\n"
                    + "    return a.dot(a) > 24.999 && a.dot(a) < 25.001;\n"
                    + "}\n",
                "math/Vector.surtr", "math/Math.surtr", "math/Angle.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void Vector2NormalizedHasUnitLength()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Vector;\n"
                    + "fun run(): bool {\n"
                    + "    let n = Vector2(3.0, 4.0).normalized();\n"
                    + "    return n.length() > 0.999 && n.length() < 1.001;\n"
                    + "}\n",
                "math/Vector.surtr", "math/Math.surtr", "math/Angle.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void Vector3CrossProductIsPerpendicularToBothInputs()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Vector;\n"
                    + "fun run(): bool {\n"
                    + "    let x = Vector3.right;\n"
                    + "    let y = Vector3.up;\n"
                    + "    let z = x.cross(y);\n"
                    + "    if (z.dot(x) < -0.001 || z.dot(x) > 0.001) return false;\n"
                    + "    if (z.dot(y) < -0.001 || z.dot(y) > 0.001) return false;\n"
                    + "    return z.length() > 0.999 && z.length() < 1.001;\n"
                    + "}\n",
                "math/Vector.surtr", "math/Math.surtr", "math/Angle.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void Vector3LerpInterpolatesLinearly()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Vector;\n"
                    + "fun run(): bool {\n"
                    + "    let mid = Vector3.lerp(Vector3.zero, Vector3(10.0, 20.0, 30.0), 0.5);\n"
                    + "    return mid.x == 5.0 && mid.y == 10.0 && mid.z == 15.0;\n"
                    + "}\n",
                "math/Vector.surtr", "math/Math.surtr", "math/Angle.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        // ── Fase 5: Quaternion ───────────────────────────────────────────────

        [Fact]
        public void QuaternionIdentityRotationLeavesAVectorUnchanged()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Vector;\n"
                    + "import surtr.math.Quaternion;\n"
                    + "fun run(): bool {\n"
                    + "    let v = Vector3(1.0, 2.0, 3.0);\n"
                    + "    let r = Quaternion.identity.rotate(v);\n"
                    + "    return r.x > 0.999 && r.x < 1.001 && r.y > 1.999 && r.y < 2.001 && r.z > 2.999 && r.z < 3.001;\n"
                    + "}\n",
                "math/Vector.surtr", "math/Quaternion.surtr", "math/Math.surtr", "math/Angle.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        /// <summary>
        /// A 180-degree rotation is its own convention-independent case: rotating `right` around
        /// `forward` by half a turn lands on `-right` under either winding, so this does not depend
        /// on getting the handedness of the rotation right, only its magnitude.
        /// </summary>
        [Fact]
        public void QuaternionRotates180DegreesCorrectly()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Vector;\n"
                    + "import surtr.math.Quaternion;\n"
                    + "import surtr.math.Angle;\n"
                    + "fun run(): bool {\n"
                    + "    let q = Quaternion.fromAxisAngle(Vector3.forward, Angle.fromDegrees(180.0));\n"
                    + "    let r = q.rotate(Vector3.right);\n"
                    + "    return r.x > -1.001 && r.x < -0.999 && r.y > -0.001 && r.y < 0.001;\n"
                    + "}\n",
                "math/Vector.surtr", "math/Quaternion.surtr", "math/Math.surtr", "math/Angle.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        /// <summary>Composing two half-rotations should agree with one full rotation.</summary>
        [Fact]
        public void QuaternionCompositionMatchesASingleEquivalentRotation()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Vector;\n"
                    + "import surtr.math.Quaternion;\n"
                    + "import surtr.math.Angle;\n"
                    + "fun run(): bool {\n"
                    + "    let axis = Vector3.up;\n"
                    + "    let half = Quaternion.fromAxisAngle(axis, Angle.fromDegrees(45.0));\n"
                    + "    let full = Quaternion.fromAxisAngle(axis, Angle.fromDegrees(90.0));\n"
                    + "    let composed = half * half;\n"
                    + "    let v = Vector3.right;\n"
                    + "    let a = composed.rotate(v);\n"
                    + "    let b = full.rotate(v);\n"
                    + "    let diff = a - b;\n"
                    + "    return diff.length() < 0.001;\n"
                    + "}\n",
                "math/Vector.surtr", "math/Quaternion.surtr", "math/Math.surtr", "math/Angle.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        /// <summary>
        /// Regression for B6 (docs/Plan-Revision-Stdlib.md §2.6): a driver in its own module,
        /// importing `surtr.math.Vector` the way any real caller would, adds two vectors together -
        /// which used to crash emission with an operand stack underflow, since `operator+` both
        /// takes and returns a multi-field value class across a module boundary.
        /// </summary>
        [Fact]
        public void VectorArithmeticFromAnotherModuleWorks()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Vector;\n"
                    + "fun run(): float {\n"
                    + "    let a = Vector2(1.0, 2.0);\n"
                    + "    let b = a + Vector2(1.0, 1.0);\n"
                    + "    return b.x;\n"
                    + "}\n",
                "math/Math.surtr", "math/Angle.surtr", "math/Vector.surtr");

            Assert.Equal(2.0, Float(runtime, "run"));
        }

        // ── Fase 5: Random ───────────────────────────────────────────────────

        [Fact]
        public void RandomWithTheSameSeedProducesTheSameSequence()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Random;\n"
                    + "fun run(): bool {\n"
                    + "    let a = Random(42);\n"
                    + "    let b = Random(42);\n"
                    + "    for (var i = 0; i < 20; i++) {\n"
                    + "        if (a.nextInt() != b.nextInt()) return false;\n"
                    + "    }\n"
                    + "    return true;\n"
                    + "}\n",
                "math/Random.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void RandomNextIntRespectsItsBounds()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Random;\n"
                    + "fun run(): bool {\n"
                    + "    let r = Random(1);\n"
                    + "    for (var i = 0; i < 500; i++) {\n"
                    + "        let n = r.nextInt(10);\n"
                    + "        if (n < 0 || n >= 10) return false;\n"
                    + "    }\n"
                    + "    for (var i = 0; i < 500; i++) {\n"
                    + "        let n = r.nextInt(5, 15);\n"
                    + "        if (n < 5 || n >= 15) return false;\n"
                    + "    }\n"
                    + "    return true;\n"
                    + "}\n",
                "math/Random.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void RandomNextFloatIsWithinZeroToOne()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Random;\n"
                    + "fun run(): bool {\n"
                    + "    let r = Random(7);\n"
                    + "    for (var i = 0; i < 500; i++) {\n"
                    + "        let f = r.nextFloat();\n"
                    + "        if (f < 0.0 || f >= 1.0) return false;\n"
                    + "    }\n"
                    + "    return true;\n"
                    + "}\n",
                "math/Random.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void RandomWithoutASeedIsUnpredictableButUsable()
        {
            var runtime = BuildAndLoad(
                "import surtr.math.Random;\n"
                    + "fun run(): bool {\n"
                    + "    let r = Random();\n"
                    + "    let n = r.nextInt(100);\n"
                    + "    return n >= 0 && n < 100;\n"
                    + "}\n",
                "math/Random.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        // ── Fase 6: PriorityQueue<T> ─────────────────────────────────────────

        [Fact]
        public void PriorityQueueDequeuesInAscendingPriorityOrder()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.PriorityQueue;\n"
                    + "fun run(): bool {\n"
                    + "    let q = PriorityQueue<string>();\n"
                    + "    q.enqueue(\"c\", 3.0);\n"
                    + "    q.enqueue(\"a\", 1.0);\n"
                    + "    q.enqueue(\"b\", 2.0);\n"
                    + "    if (q.dequeue() != \"a\") return false;\n"
                    + "    if (q.dequeue() != \"b\") return false;\n"
                    + "    if (q.dequeue() != \"c\") return false;\n"
                    + "    return q.isEmpty;\n"
                    + "}\n",
                "collections/PriorityQueue.surtr", "collections/Collection.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void PriorityQueueHandlesManyOutOfOrderInsertions()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.PriorityQueue;\n"
                    + "fun run(): bool {\n"
                    + "    let q = PriorityQueue<int>();\n"
                    + "    var i = 200;\n"
                    + "    while (i > 0) {\n"
                    + "        i -= 1;\n"
                    + "        q.enqueue(i, i.toFloat());\n"
                    + "    }\n"
                    + "    var expected = 0;\n"
                    + "    while (!q.isEmpty) {\n"
                    + "        if (q.dequeue() != expected) return false;\n"
                    + "        expected += 1;\n"
                    + "    }\n"
                    + "    return expected == 200;\n"
                    + "}\n",
                "collections/PriorityQueue.surtr", "collections/Collection.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void PriorityQueuePeekDoesNotRemove()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.PriorityQueue;\n"
                    + "fun run(): bool {\n"
                    + "    let q = PriorityQueue<int>();\n"
                    + "    q.enqueue(5, 5.0);\n"
                    + "    q.enqueue(1, 1.0);\n"
                    + "    if (q.peek() != 1) return false;\n"
                    + "    if (q.length != 2) return false;\n"
                    + "    return q.peek() == 1;\n"
                    + "}\n",
                "collections/PriorityQueue.surtr", "collections/Collection.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void PriorityQueueDequeueOnEmptyThrows()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.PriorityQueue;\n"
                    + "fun run(): int { return PriorityQueue<int>().dequeue(); }\n",
                "collections/PriorityQueue.surtr", "collections/Collection.surtr");

            Assert.Throws<Surtr.VM.SurtrThrownException>(() => runtime.Invoke(Function(runtime, "run")));
        }

        [Fact]
        public void PriorityQueueClearEmptiesIt()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.PriorityQueue;\n"
                    + "fun run(): bool {\n"
                    + "    let q = PriorityQueue<int>();\n"
                    + "    q.enqueue(1, 1.0);\n"
                    + "    q.enqueue(2, 2.0);\n"
                    + "    q.clear();\n"
                    + "    return q.isEmpty && q.length == 0;\n"
                    + "}\n",
                "collections/PriorityQueue.surtr", "collections/Collection.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void PriorityQueueContainsAndCopyTo()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.PriorityQueue;\n"
                    + "fun run(): bool {\n"
                    + "    let q = PriorityQueue<int>();\n"
                    + "    q.enqueue(10, 3.0);\n"
                    + "    q.enqueue(20, 1.0);\n"
                    + "    q.enqueue(30, 2.0);\n"
                    + "    if (!q.contains(20)) return false;\n"
                    + "    if (q.contains(99)) return false;\n"
                    + "    let dest = array<int>(3);\n"
                    + "    q.copyTo(dest, 0);\n"
                    + "    var sum = 0;\n"
                    + "    for (var i = 0; i < 3; i++) sum += dest[i];\n"
                    + "    return sum == 60;\n"
                    + "}\n",
                "collections/PriorityQueue.surtr", "collections/Collection.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void PriorityQueueOfVectorsAcrossModules()
        {
            // Also exercises T = a multi-field value class (Vector2) as the queue's element type -
            // still fine, since PriorityQueue<T> never names a concrete value class in any signature.
            var runtime = BuildAndLoad(
                "import surtr.math.Vector;\n"
                    + "import surtr.collections.PriorityQueue;\n"
                    + "fun run(): float {\n"
                    + "    let q = PriorityQueue<Vector2>();\n"
                    + "    q.enqueue(Vector2(9.0, 9.0), 2.0);\n"
                    + "    q.enqueue(Vector2(1.0, 1.0), 1.0);\n"
                    + "    return q.dequeue().x;\n"
                    + "}\n",
                "math/Vector.surtr", "math/Math.surtr", "math/Angle.surtr",
                "collections/PriorityQueue.surtr", "collections/Collection.surtr");

            Assert.Equal(1.0, Float(runtime, "run"));
        }

        // ── B7 regression ────────────────────────────────────────────────────

        /// <summary>
        /// Regression for B7 (docs/Plan-Revision-Stdlib.md §2.7): an interface-dispatched method
        /// (no `override` written, so the class's own member is `Direct` and the interface slot is
        /// filled by a compiler-synthesized bridge) taking a 2+-element tuple parameter used to
        /// crash emission of the bridge itself with an operand stack underflow - nothing had to call
        /// it. Exercises both the direct call (bypasses the bridge entirely) and a call through an
        /// interface-typed reference (goes through the bridge), so both paths are covered.
        /// </summary>
        [Fact]
        public void InterfaceDispatchedMethodWithTwoElementTupleParameterWorks()
        {
            var runtime = BuildAndLoad(
                "public interface IThing<K, V> { fun contains(item: (K, V)): bool; }\n"
                    + "public class Box<K, V> : IThing<K, V> {\n"
                    + "    public constructor() { }\n"
                    + "    public fun contains(item: (K, V)): bool {\n"
                    + "        if (item[0] == item[0]) return true;\n"
                    + "        return item[1] == item[1];\n"
                    + "    }\n"
                    + "}\n"
                    + "fun run(): bool { let b = Box<string, int>(); return b.contains((\"a\", 1)); }\n"
                    + "fun runViaInterface(): bool {\n"
                    + "    let b = Box<string, int>();\n"
                    + "    let t: IThing<string, int> = b;\n"
                    + "    return t.contains((\"a\", 1));\n"
                    + "}\n");

            Assert.True(Bool(runtime, "run"));
            Assert.True(Bool(runtime, "runViaInterface"));
        }

        // ── Fase 6: Map<K,V> ─────────────────────────────────────────────────
        //
        // IMap<K,V>/IReadOnlyMap<K,V> deliberately do not extend IReadOnlyCollection<(K,V)> - doing
        // so would require implementing `contains(item: (K,V)): bool`, and B7 (see
        // docs/Plan-Revision-Stdlib.md) crashes any interface-dispatched method taking a 2+-element
        // tuple parameter. IMap<K,V> only needs IIterable<(K,V)> for `for (pair in map)`, so this is
        // the one collections/ interface pair that stands alone rather than joining the
        // IReadOnlyCollection<T> hierarchy. These tests use BuildAndLoad (a real driver in its own
        // `test` module) specifically to exercise that interface dispatch across a module boundary.

        [Fact]
        public void MapSetGetAndContainsKey()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Map;\n"
                    + "fun run(): bool {\n"
                    + "    let m = Map<string, int>();\n"
                    + "    m.set(\"a\", 1);\n"
                    + "    m.set(\"b\", 2);\n"
                    + "    if (!m.containsKey(\"a\")) return false;\n"
                    + "    if (m.containsKey(\"z\")) return false;\n"
                    + "    return m.get(\"b\") == 2;\n"
                    + "}\n",
                "collections/Map.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void MapIndexerReadsAndWrites()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Map;\n"
                    + "fun run(): int {\n"
                    + "    let m = Map<string, int>();\n"
                    + "    m[\"x\"] = 10;\n"
                    + "    m[\"x\"] = m[\"x\"] + 5;\n"
                    + "    return m[\"x\"];\n"
                    + "}\n",
                "collections/Map.surtr");

            Assert.Equal(15, Int(runtime, "run"));
        }

        [Fact]
        public void MapRemoveAndClear()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Map;\n"
                    + "fun run(): bool {\n"
                    + "    let m = Map<string, int>();\n"
                    + "    m.set(\"a\", 1);\n"
                    + "    m.set(\"b\", 2);\n"
                    + "    if (!m.remove(\"a\")) return false;\n"
                    + "    if (m.remove(\"a\")) return false;\n"
                    + "    if (m.containsKey(\"a\")) return false;\n"
                    + "    m.clear();\n"
                    + "    return m.isEmpty && m.length == 0;\n"
                    + "}\n",
                "collections/Map.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void MapIteratesRealKeyValuePairsWithReferenceTypedValues()
        {
            // Exercises for-in over a Map from another module - the highest-risk path for B7, since
            // it drives Map<K,V>.iterate() through the interface (IIterable<(K,V)>) rather than
            // calling the concrete class directly. V = string here deliberately - see B8 just below
            // for why V = a primitive (int/float/bool/char) is a separate, currently broken case.
            var runtime = BuildAndLoad(
                "import surtr.collections.Map;\n"
                    + "fun run(): int {\n"
                    + "    let m = Map<string, string>();\n"
                    + "    m.set(\"a\", \"x\");\n"
                    + "    m.set(\"b\", \"yy\");\n"
                    + "    m.set(\"c\", \"zzz\");\n"
                    + "    var sum = 0;\n"
                    + "    for (pair in m) sum += pair[1].length;\n"
                    + "    return sum;\n"
                    + "}\n",
                "collections/Map.surtr");

            Assert.Equal(6, Int(runtime, "run"));
        }

        /// <summary>
        /// Regression for B8 (docs/Plan-Revision-Stdlib.md §2.8): a dict field whose declared type is
        /// <c>{K: V}</c> with BOTH K and V still generic (as opposed to Set&lt;T&gt;'s own
        /// <c>{T: bool}</c>, where only the key side is generic and the value side, <c>bool</c>, is
        /// concrete) used to silently return corrupted data for a primitive V once it round-tripped
        /// through <c>values()</c>/iteration. A single scalar <c>get(key)</c> was never affected,
        /// which is what makes <see cref="MapSetGetAndContainsKey"/> and
        /// <see cref="MapIndexerReadsAndWrites"/> pass regardless.
        /// </summary>
        [Fact]
        public void MapIterationWithPrimitiveIntValuesCurrentlyReturnsCorruptedData()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Map;\n"
                    + "fun run(): int {\n"
                    + "    let m = Map<string, int>();\n"
                    + "    m.set(\"a\", 1);\n"
                    + "    m.set(\"b\", 2);\n"
                    + "    m.set(\"c\", 3);\n"
                    + "    var sum = 0;\n"
                    + "    for (pair in m) sum += pair[1];\n"
                    + "    return sum;\n"
                    + "}\n",
                "collections/Map.surtr");

            Assert.Equal(6, Int(runtime, "run"));
        }

        [Fact]
        public void MapKeysAndValuesArrays()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Map;\n"
                    + "fun run(): bool {\n"
                    + "    let m = Map<string, int>();\n"
                    + "    m.set(\"a\", 1);\n"
                    + "    m.set(\"b\", 2);\n"
                    + "    let ks = m.keys();\n"
                    + "    let vs = m.values();\n"
                    + "    return ks.length == 2 && vs.length == 2;\n"
                    + "}\n",
                "collections/Map.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        /// <summary>
        /// Regression for B8, exercising <c>values()</c> directly (not through <c>for-in</c>): with
        /// K and V both still generic on <c>Map&lt;K,V&gt;</c>'s own <c>{K: V}</c> field, a primitive
        /// V's array used to come back with each element still boxed from the erasure boundary
        /// <c>_dict.set</c> crosses, read back as if it were the raw value itself. Indexes the result
        /// directly (<c>vs[0]</c>) rather than looping, and sums via <c>for-in</c> too, so both the
        /// indexer and the loop path are covered.
        /// </summary>
        [Fact]
        public void MapValuesOfPrimitiveIntReturnsRealNumbers()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Map;\n"
                    + "fun first(): int {\n"
                    + "    let m = Map<string, int>();\n"
                    + "    m.set(\"a\", 1);\n"
                    + "    return m.values()[0];\n"
                    + "}\n"
                    + "fun sum(): int {\n"
                    + "    let m = Map<string, int>();\n"
                    + "    m.set(\"a\", 1);\n"
                    + "    m.set(\"b\", 2);\n"
                    + "    m.set(\"c\", 3);\n"
                    + "    var total = 0;\n"
                    + "    for (v in m.values()) total += v;\n"
                    + "    return total;\n"
                    + "}\n",
                "collections/Map.surtr");

            Assert.Equal(1, Int(runtime, "first"));
            Assert.Equal(6, Int(runtime, "sum"));
        }

        [Fact]
        public void MapAsReadOnlyStaysLiveAndHidesMutation()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Map;\n"
                    + "fun run(): bool {\n"
                    + "    let m = Map<string, int>();\n"
                    + "    m.set(\"a\", 1);\n"
                    + "    let ro = m.asReadOnly();\n"
                    + "    if (ro.length != 1) return false;\n"
                    + "    m.set(\"b\", 2);\n"
                    + "    return ro.length == 2 && ro.get(\"b\") == 2;\n"
                    + "}\n",
                "collections/Map.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void MapOfVectorValuesAcrossModules()
        {
            // T = a multi-field value class (Vector2) as the map's VALUE type - fine, since
            // IMap<K,V>'s own signatures never name a concrete value class, only G0/G1.
            var runtime = BuildAndLoad(
                "import surtr.math.Vector;\n"
                    + "import surtr.collections.Map;\n"
                    + "fun run(): float {\n"
                    + "    let m = Map<string, Vector2>();\n"
                    + "    m.set(\"origin\", Vector2(1.0, 2.0));\n"
                    + "    return m.get(\"origin\").y;\n"
                    + "}\n",
                "math/Vector.surtr", "math/Math.surtr", "math/Angle.surtr", "collections/Map.surtr");

            Assert.Equal(2.0, Float(runtime, "run"));
        }

        // ── Fase 7: List<T> ampliaciones ─────────────────────────────────────

        [Fact]
        public void ListSortSortsInPlaceAndIgnoresSpareCapacity()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.List;\n"
                    + "fun run(): bool {\n"
                    + "    let l = List<int>(2);\n"
                    + "    l.add(3); l.add(1); l.add(4); l.add(1); l.add(5);\n"
                    + "    l.sort((a, b) => a - b);\n"
                    + "    if (l.length != 5) return false;\n"
                    + "    let expected = [1, 1, 3, 4, 5];\n"
                    + "    for (var i = 0; i < 5; i++) { if (l.get(i) != expected[i]) return false; }\n"
                    + "    return true;\n"
                    + "}\n",
                "collections/List.surtr", "collections/Collection.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void ListReverseReversesInPlace()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.List;\n"
                    + "fun run(): bool {\n"
                    + "    let l = List<int>(); l.add(1); l.add(2); l.add(3);\n"
                    + "    l.reverse();\n"
                    + "    return l.get(0) == 3 && l.get(1) == 2 && l.get(2) == 1;\n"
                    + "}\n",
                "collections/List.surtr", "collections/Collection.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void ListAddRangeAppendsFromAnyIterable()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.List;\n"
                    + "fun run(): int {\n"
                    + "    let l = List<int>(); l.add(1);\n"
                    + "    l.addRange([2, 3, 4]);\n"
                    + "    var sum = 0;\n"
                    + "    for (x in l) sum += x;\n"
                    + "    return sum;\n"
                    + "}\n",
                "collections/List.surtr", "collections/Collection.surtr");

            Assert.Equal(10, Int(runtime, "run"));
        }

        [Fact]
        public void ListLastIndexOfFindsTheLastOccurrence()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.List;\n"
                    + "fun run(): int {\n"
                    + "    let l = List<int>(); l.add(1); l.add(2); l.add(1); l.add(2);\n"
                    + "    return l.lastIndexOf(1);\n"
                    + "}\n",
                "collections/List.surtr", "collections/Collection.surtr");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void ListToArrayIsExactlySizedNotCapacitySized()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.List;\n"
                    + "fun run(): int {\n"
                    + "    let l = List<int>(16); l.add(1); l.add(2);\n"
                    + "    return l.toArray().length;\n"
                    + "}\n",
                "collections/List.surtr", "collections/Collection.surtr");

            Assert.Equal(2, Int(runtime, "run"));
        }

        // ── Fase 7: StringBuilder ampliaciones ───────────────────────────────

        [Fact]
        public void StringBuilderCapacityIsExposedSeparatelyFromLength()
        {
            var runtime = BuildAndLoad(
                "import surtr.text.StringBuilder;\n"
                    + "fun run(): bool {\n"
                    + "    let sb = StringBuilder(16);\n"
                    + "    sb.append(\"hi\");\n"
                    + "    return sb.length == 2 && sb.capacity >= 16;\n"
                    + "}\n",
                "text/StringBuilder.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void StringBuilderInsertShiftsExistingContentRight()
        {
            var runtime = BuildAndLoad(
                "import surtr.text.StringBuilder;\n"
                    + "fun run(): string {\n"
                    + "    let sb = StringBuilder(); sb.append(\"helloworld\");\n"
                    + "    sb.insert(5, \" \");\n"
                    + "    return sb.toString();\n"
                    + "}\n",
                "text/StringBuilder.surtr");

            Assert.Equal("hello world", runtime.Resolve<Surtr.Runtime.Objects.SurtrString>(runtime.Invoke(Function(runtime, "run")))!.Text);
        }

        [Fact]
        public void StringBuilderRemoveDeletesARange()
        {
            var runtime = BuildAndLoad(
                "import surtr.text.StringBuilder;\n"
                    + "fun run(): string {\n"
                    + "    let sb = StringBuilder(); sb.append(\"hello world\");\n"
                    + "    sb.remove(5, 6);\n"
                    + "    return sb.toString();\n"
                    + "}\n",
                "text/StringBuilder.surtr");

            Assert.Equal("hello", runtime.Resolve<Surtr.Runtime.Objects.SurtrString>(runtime.Invoke(Function(runtime, "run")))!.Text);
        }

        [Fact]
        public void StringBuilderReplaceSwapsARangeForADifferentLengthString()
        {
            var runtime = BuildAndLoad(
                "import surtr.text.StringBuilder;\n"
                    + "fun run(): string {\n"
                    + "    let sb = StringBuilder(); sb.append(\"hello world\");\n"
                    + "    sb.replace(6, 5, \"there!!\");\n"
                    + "    return sb.toString();\n"
                    + "}\n",
                "text/StringBuilder.surtr");

            Assert.Equal("hello there!!", runtime.Resolve<Surtr.Runtime.Objects.SurtrString>(runtime.Invoke(Function(runtime, "run")))!.Text);
        }

        [Fact]
        public void StringBuilderIndexOfCharAndString()
        {
            var runtime = BuildAndLoad(
                "import surtr.text.StringBuilder;\n"
                    + "fun run(): bool {\n"
                    + "    let sb = StringBuilder(); sb.append(\"hello world\");\n"
                    + "    if (sb.indexOf('w') != 6) return false;\n"
                    + "    if (sb.indexOf('z') != -1) return false;\n"
                    + "    if (sb.indexOf(\"world\") != 6) return false;\n"
                    + "    return sb.indexOf(\"xyz\") == -1;\n"
                    + "}\n",
                "text/StringBuilder.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void StringBuilderSubstringReturnsASlice()
        {
            var runtime = BuildAndLoad(
                "import surtr.text.StringBuilder;\n"
                    + "fun run(): string {\n"
                    + "    let sb = StringBuilder(); sb.append(\"hello world\");\n"
                    + "    return sb.substring(6, 5);\n"
                    + "}\n",
                "text/StringBuilder.surtr");

            Assert.Equal("world", runtime.Resolve<Surtr.Runtime.Objects.SurtrString>(runtime.Invoke(Function(runtime, "run")))!.Text);
        }

        // ── Fase 7: Sequence<T> ampliaciones ──────────────────────────────────

        [Fact]
        public void SequenceMinAndMax()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Sequence;\n"
                    + "fun run(): bool {\n"
                    + "    let seq = Sequence<int>.of(3, 1, 4, 1, 5);\n"
                    + "    return seq.min((a, b) => a - b) == 1 && seq.max((a, b) => a - b) == 5;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr", "collections/Set.surtr", "collections/Map.surtr", "collections/Sequence.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        /// <summary>
        /// Regression for B9 (docs/Plan-Revision-Stdlib.md §2.9): a generic method's <c>T?</c>
        /// return, instantiated to a primitive (confirmed with <c>int</c>), used to lose its
        /// "absent" tag by the time the caller compared it to <c>null</c> - a concrete, non-generic
        /// <c>int?</c> always compared correctly, but the exact same value routed through a generic
        /// method's substituted return type did not. Not new to <c>min</c>/<c>max</c> - the
        /// pre-existing <c>firstOrNull()</c> had the same gap (see
        /// <see cref="SequenceFirstOrNullOnEmptySequenceCurrentlyReturnsFalseNotNull"/>), just never
        /// had a test exercising an empty, primitive-typed sequence before.
        /// </summary>
        [Fact]
        public void SequenceMinOnEmptySequenceCurrentlyReturnsFalseNotNull()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Sequence;\n"
                    + "fun run(): bool {\n"
                    + "    return Sequence<int>.empty.min((a, b) => a - b) == null;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr", "collections/Set.surtr", "collections/Map.surtr", "collections/Sequence.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        /// <summary>B9 again (see the remarks above) - regression for the pre-existing gap in firstOrNull().</summary>
        [Fact]
        public void SequenceFirstOrNullOnEmptySequenceCurrentlyReturnsFalseNotNull()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Sequence;\n"
                    + "fun run(): bool {\n"
                    + "    return Sequence<int>.empty.firstOrNull() == null;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr", "collections/Set.surtr", "collections/Map.surtr", "collections/Sequence.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void SequenceSumIntsAndAverageInts()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Sequence;\n"
                    + "fun run(): bool {\n"
                    + "    let seq = Sequence<int>.of(1, 2, 3, 4);\n"
                    + "    if (seq.sumInts() != 10) return false;\n"
                    + "    let avg = seq.averageInts();\n"
                    + "    return avg > 2.499 && avg < 2.501;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr", "collections/Set.surtr", "collections/Map.surtr", "collections/Sequence.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void SequenceSumFloatsAndAverageFloats()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Sequence;\n"
                    + "fun run(): bool {\n"
                    + "    let seq = Sequence<float>.of(1.0, 2.0, 3.0);\n"
                    + "    let sum = seq.sumFloats();\n"
                    + "    if (sum < 5.999 || sum > 6.001) return false;\n"
                    + "    let avg = seq.averageFloats();\n"
                    + "    return avg > 1.999 && avg < 2.001;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr", "collections/Set.surtr", "collections/Map.surtr", "collections/Sequence.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void SequenceGroupByGroupsPreservingEncounterOrder()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Sequence;\n"
                    + "fun run(): bool {\n"
                    + "    let groups = Sequence<int>.of(1, 2, 3, 4, 5, 6).groupBy<int>((x) => x % 2);\n"
                    + "    let evens = groups.get(0);\n"
                    + "    let odds = groups.get(1);\n"
                    + "    if (evens.length != 3 || odds.length != 3) return false;\n"
                    + "    return evens.get(0) == 2 && odds.get(0) == 1;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr", "collections/Set.surtr", "collections/Map.surtr", "collections/Sequence.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void SequenceDistinctByKeepsFirstPerKey()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Sequence;\n"
                    + "fun run(): bool {\n"
                    + "    let result = Sequence<int>.of(1, 2, 3, 4, 5).distinctBy<int>((x) => x % 2).toArray();\n"
                    + "    return result.length == 2 && result[0] == 1 && result[1] == 2;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr", "collections/Set.surtr", "collections/Map.surtr", "collections/Sequence.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void SequenceSortByAndSortByDescending()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Sequence;\n"
                    + "fun run(): bool {\n"
                    + "    let asc = Sequence<int>.of(3, 1, 2).sortBy<int>((x) => -x, (a, b) => a - b).toArray();\n"
                    + "    let desc = Sequence<int>.of(1, 2, 3).sortByDescending<int>((x) => x, (a, b) => a - b).toArray();\n"
                    + "    if (asc[0] != 3 || asc[1] != 2 || asc[2] != 1) return false;\n"
                    + "    return desc[0] == 3 && desc[1] == 2 && desc[2] == 1;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr", "collections/Set.surtr", "collections/Map.surtr", "collections/Sequence.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void SequenceJoinToStringUsesTheGivenSelectorAndSeparator()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Sequence;\n"
                    + "fun run(): string {\n"
                    + "    return Sequence<int>.of(1, 2, 3).joinToString(\", \", (x) => x.toString());\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr", "collections/Set.surtr", "collections/Map.surtr", "collections/Sequence.surtr");

            Assert.Equal("1, 2, 3", runtime.Resolve<Surtr.Runtime.Objects.SurtrString>(runtime.Invoke(Function(runtime, "run")))!.Text);
        }

        [Fact]
        public void SequenceElementAtLastAndLastOrNull()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Sequence;\n"
                    + "fun run(): bool {\n"
                    + "    let seq = Sequence<int>.of(10, 20, 30);\n"
                    + "    if (seq.elementAt(1) != 20) return false;\n"
                    + "    if (seq.last() != 30) return false;\n"
                    + "    return seq.lastOrNull() == 30;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr", "collections/Set.surtr", "collections/Map.surtr", "collections/Sequence.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        /// <summary>B9 again (see the remarks on SequenceMinOnEmptySequenceCurrentlyReturnsFalseNotNull) - regression for the same gap in lastOrNull().</summary>
        [Fact]
        public void SequenceLastOrNullOnEmptySequenceCurrentlyReturnsFalseNotNull()
        {
            var runtime = BuildAndLoad(
                "import surtr.collections.Sequence;\n"
                    + "fun run(): bool {\n"
                    + "    return Sequence<int>.empty.lastOrNull() == null;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr", "collections/Set.surtr", "collections/Map.surtr", "collections/Sequence.surtr");

            Assert.True(Bool(runtime, "run"));
        }

        [Fact]
        public void SequenceTerminalOpsWorkThroughTheIIterableExtensionsToo()
        {
            // Exercises the IIterable<T> extension wrappers directly on a List<int> (not a
            // Sequence<T>), the same way forEach/toList/etc. above already do.
            var runtime = BuildAndLoad(
                "import surtr.collections.List;\n"
                    + "import surtr.collections.Sequence;\n"
                    + "fun run(): bool {\n"
                    + "    let l = List<int>(); l.add(1); l.add(2); l.add(3);\n"
                    + "    if (l.sumInts() != 6) return false;\n"
                    + "    if (l.min((a, b) => a - b) != 1) return false;\n"
                    + "    if (l.max((a, b) => a - b) != 3) return false;\n"
                    + "    if (l.last() != 3) return false;\n"
                    + "    return l.elementAt(1) == 2;\n"
                    + "}\n",
                "collections/Collection.surtr", "collections/List.surtr", "collections/Set.surtr", "collections/Map.surtr", "collections/Sequence.surtr");

            Assert.True(Bool(runtime, "run"));
        }
    }
}
