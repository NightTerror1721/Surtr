# Surtr.Stdlib

The Surtr standard library, as a project independent of both `Surtr.Core` and `Surtr.Compiler`:
the `.surtr` sources, the `.surtrc` images `Surtr.Stdlib.Tool` compiles them to (embedded straight
into this assembly, not shipped as loose files), the C# native bodies the expressible half still
needs (today, `Math`'s trig/float operations), and the loader (`SurtrStdlib.LoadAll`/`LoadInto`) a
host calls to put all of it into a `SurtrRuntime`. Optional and modular by construction: a host adds
this project, constructs its own `SurtrRuntime`, and only then decides whether — and how much of —
the standard library it wants loaded. The whole thing is `netstandard2.1`, the same target
`Surtr.Core` builds for, so a Unity host's drop-in is exactly two files:

```csharp
using var runtime = new SurtrRuntime();
SurtrStdlib.LoadAll(runtime); // everything, straight out of Surtr.Stdlib.dll's own resources
```

Copy `Surtr.Core.dll` and `Surtr.Stdlib.dll` into `Assets/Plugins` (or a UPM package) and that call
works with nothing else shipped alongside them — no `build/` folder, no `TextAsset`, no asset
pipeline entry. `SurtrStdlib.LoadAll(runtime, StdlibModules.Math)` loads only a category, for a
sandboxed host that wants less than everything.

## Why this project exists

`Surtr.Core`'s built-in module (`SurtrBuiltIns`) carries only what the language's own type system
needs to exist at all — the primitives, the collections, and the core interfaces `for-in`/`<=>`
lower onto. Everything else the language *assumes* — `Exception` to extend (§9), `Math`, collection
helpers, `StringBuilder` — is standard library, not core, and lives here instead: `Surtr.Core` has
no reference to this project and no knowledge that any of it exists. `Language-Syntax.md` §13.1
splits that library across two languages on one rule:

> native if it needs `unsafe`, a raw pointer or a VM service; Surtr otherwise.

`docs/Compiler-Plan.md` §10.2 lists *"the standard library is entirely C#... which is also the
largest program the compiler has never been asked to compile"* as the one thing still owed. This
project is that change, one module per file, with a growing share of it in real Surtr source.

## Why one module per file, not one module

The built-in `surtr` module is built and **linked once**, in `SurtrBuiltIns`'s static constructor,
and `LoadModule` rejects a second module at path `surtr`. A module image that re-declared the
built-ins would shadow them rather than extend them (see the `SurtrModuleImage` class remarks). So
the Surtr-written half cannot be a module named `surtr`, and it cannot be merged into the built-in
module (that module's tables are flattened at link time).

But a module at a *longer* path — `surtr.core.Exception`, `surtr.math.Math` — is a perfectly
ordinary module. `SurtrRuntime.TryResolveHandle` resolves a `surtr:Exception` reference against the
built-in module even when it comes from a separate loaded module, because the built-in is reached
by name rather than registered in the runtime's table. So each stdlib module is a **real, separate
module** that *extends the built-in `surtr` by reference* — verified: a `surtr.core.Exception`
subclass `IsSubclassOf(SurtrBuiltIns.Exception)` is `True`.

Each module is independently compilable and testable, and each is one `.surtrc` artefact. A file's
module path is its full location under `src/surtr/`, every directory segment plus the file name
(without extension) becoming a dotted segment — `src/surtr/math/Angle.surtr` is module
`surtr.math.Angle`, matching §2.1's "a module has no header, so where a file lives names it", with
the file name standing in as the final segment because the stdlib keeps one module per file.

## What is already in C# (do not re-implement)

The execution-path pieces that need VM services stay C#. Most of it stays in `Surtr.Core`, because
it is core object-model machinery, not library content:

| Piece | Where |
|---|---|
| `Exception` root (the `_message` slot, `message`, `toString`, the constructor) | `Surtr.Core`'s `SurtrStandardLibrary.DeclareException` |
| Core interfaces `IIterable<T>`/`IIterator<T>`/`IComparable<T>`/`IEquatable<T>` | `Surtr.Core`'s `SurtrStandardLibrary.DeclareCoreInterfaces` |
| Collection members (`array`/`tuple`/`dict`/`closure`), incl. `array.sort` | `Surtr.Core`'s `SurtrCompositeBuiltIns` |
| String members, incl. `string.format` | `Surtr.Core`'s `SurtrStringBuiltIn` |
| `bytes`, the built-in mutable byte buffer | `Surtr.Core`'s `SurtrBytesBuiltIn` |
| `Iterator` cursor | `Surtr.Core`'s `SurtrIteratorBuiltIns` |
| `Math`'s trig/float operations (`sin`, `cos`, `sqrt`, `pow`, `log`, `floor`, `hypot`, …) | **this project**'s `Native/SurtrMathNative.cs`, bound to `surtr.math.Math`'s `native fun` declarations by link name |
| `Profiler`'s `stopwatchTimestamp`, and `Debug`'s `debugPrint`/`debugDump`/`debugBreakpoint`/`debugStack`/`debugIsDebuggerAttached`, and `RuntimeInfo`'s native property getters | **this project**'s `Native/SurtrDiagnosticsNative.cs` |

`Math` and the diagnostics natives are the only members of that list that are *not* core
object-model content — they need C# only because they call into the CLR (`System.Math`,
`System.Diagnostics`, `Environment`), not because they need anything `Surtr.Core` alone can
provide. That is why they live here rather than in `SurtrBuiltIns`: nothing about them is more
fundamental to the language than `StringBuilder` or `LinkedList<T>` are, they just happen to still
be native. `abs`, `min`, `max`, `clamp`, `sign`, `lerp` and the `pi`/`tau`/`epsilon`-style constants
need none of that and are ordinary `const`/`const fun` Surtr in `Math.surtr` itself.

## The modules

Every module is a file under `src/surtr/`; its dotted path is its location relative to that root.

| Module | Source | Contents |
|---|---|---|
| `surtr.core.Exception` | `src/surtr/core/Exception.surtr` | Exception subclasses beyond the ones `SurtrBuiltIns` declares (`NotSupportedException`) — each a constructor and nothing else, the same shape the built-in subclasses have. |
| `surtr.core.byte` | `src/surtr/core/byte.surtr` | The `byte` value class: a type-safe, always-in-range wrapper over a single 8-bit value. |
| `surtr.math.Angle` | `src/surtr/math/Angle.surtr` | The `Angle` value class. |
| `surtr.math.Math` | `src/surtr/math/Math.surtr` | The float constants, the trig/float `native fun` declarations whose bodies `SurtrStdlib.LoadInto` publishes from `Native/SurtrMathNative.cs`, and ordinary Surtr logic over them (`abs`, `min`, `max`, `clamp`, `lerp`, `degreesToRadians`, …). The **only** `Math` the language has — `Surtr.Core` declares none. |
| `surtr.math.Vector` | `src/surtr/math/Vector.surtr` | `Vector2` and `Vector3` value classes. |
| `surtr.math.Quaternion` | `src/surtr/math/Quaternion.surtr` | `Quaternion` value class (depends on `Vector`). |
| `surtr.math.Random` | `src/surtr/math/Random.surtr` | `Random`, a pure-Surtr xorshift32 PRNG, host-seeded by default via a `native fun`. |
| `surtr.collections.Collection` | `src/surtr/collections/Collection.surtr` | `IReadOnlyCollection<T>`/`ICollection<T>`, and `ReadOnlyCollection<T>`, a read-only view over either. |
| `surtr.collections.List` | `src/surtr/collections/List.surtr` | `IReadOnlyList<T>`/`IList<T>`, `List<T>`, a `LinkedList<T>` implementing them, and `ReadOnlyList<T>`. |
| `surtr.collections.Stack` | `src/surtr/collections/Stack.surtr` | `IStack<T>`/`Stack<T>`. |
| `surtr.collections.Queue` | `src/surtr/collections/Queue.surtr` | `IQueue<T>`/`IDeque<T>`, `Queue<T>`, and `Deque<T>` (its own doubly-linked structure, not a `Queue<T>` subclass). |
| `surtr.collections.Set` | `src/surtr/collections/Set.surtr` | `IReadOnlySet<T>`/`ISet<T>`, `ReadOnlySet<T>`, `Set<T>`. |
| `surtr.collections.PriorityQueue` | `src/surtr/collections/PriorityQueue.surtr` | `IPriorityQueue<T>`/`PriorityQueue<T>`, a binary min-heap over a flat array. |
| `surtr.collections.Map` | `src/surtr/collections/Map.surtr` | `IReadOnlyMap<K,V>`/`IMap<K,V>`, `Map<K,V>` and `ReadOnlyMap<K,V>`, wrapping `{K: V}`. |
| `surtr.collections.Sequence` | `src/surtr/collections/Sequence.surtr` | `Sequence<T>`, a lazy LINQ-style pipeline (`map`/`filter`/`take`/`zip`/…) built on generators, plus `IIterable<T>` extension methods (`forEach`, `toList`, `toSet`, `reduce`, …). |
| `surtr.text.StringBuilder` | `src/surtr/text/StringBuilder.surtr` | The `StringBuilder` class. |
| `surtr.io.Enums` | `src/surtr/io/Enums.surtr` | `SeekOrigin`. |
| `surtr.io.Stream` | `src/surtr/io/Stream.surtr` | The `IWritableStream`/`IReadableStream`/`ISeekableStream` interfaces, the abstract `Stream` class, and `ObjectDisposedException`/`EndOfStreamException`. |
| `surtr.io.MemoryStream` | `src/surtr/io/MemoryStream.surtr` | `MemoryStream`, a `Stream` over an in-memory `bytes` buffer. |
| `surtr.io.BufferedStream` | `src/surtr/io/BufferedStream.surtr` | `BufferedStream`, a read/write-buffering `Stream` decorator. |
| `surtr.io.BinaryReader` / `surtr.io.BinaryWriter` | `src/surtr/io/BinaryReader.surtr`, `BinaryWriter.surtr` | Little-endian primitive reads/writes over a `Stream`. |
| `surtr.io.StreamReader` / `surtr.io.StreamWriter` | `src/surtr/io/StreamReader.surtr`, `StreamWriter.surtr` | UTF-8 text reads/writes over a `Stream`. |
| `surtr.diagnostics.Assert` | `src/surtr/diagnostics/Assert.surtr` | `AssertionException` and the `assert*` family, for tests. |
| `surtr.diagnostics.Contracts` | `src/surtr/diagnostics/Contracts.surtr` | Design-by-contract: `PreconditionException`/`PostconditionException`/`InvariantException` and the `require`/`ensure`/`invariant` helper families. |
| `surtr.diagnostics.Debug` | `src/surtr/diagnostics/Debug.surtr` | `print`/`dump`/`breakpoint`/`stackTrace`/`isDebuggerAttached`, over the natives in `Native/SurtrDiagnosticsNative.cs`. |
| `surtr.diagnostics.Log` | `src/surtr/diagnostics/Log.surtr` | `LogLevel`, `LogSink`, `Logger`, and a default-logger module surface (`info`/`warn`/`error`/…). |
| `surtr.diagnostics.Profiler` | `src/surtr/diagnostics/Profiler.surtr` | `Stopwatch`, `Profiler`/`ProfilerScope`/`ProfilerEntry`, over the native `stopwatchTimestamp`. |
| `surtr.diagnostics.RuntimeInfo` | `src/surtr/diagnostics/RuntimeInfo.surtr` | `native let` properties (`Platform`, `EngineVersion`, `ProcessorCount`, …) over the host/CLR. |

More modules can be added by dropping a `.surtr` file anywhere under `src/surtr/`; the build picks
it up automatically. The dividing line to keep: if a member needs `unsafe`, a raw pointer or a VM
service, it is C# (in `Native/`, following `Math`'s example); otherwise it is Surtr.
`docs/Plan-Revision-Stdlib.md` tracks known issues and planned additions across all of the above.

## How the standard library reaches a runtime

Everything below happens inside this one project — `Surtr.Core` is never involved beyond being an
ordinary `ProjectReference`, the same way `Surtr.Compiler` and every other consumer already depend
on it:

1. `Surtr.Stdlib.Tool` (a `net8.0` console app referencing only `Surtr.Compiler`) reads every
   `.surtr` file under `src/surtr/` and compiles them all as **one compilation** — one module per
   source file, each under its own module path (see the table above), so a module that imports a
   sibling (`surtr.collections.List` imports `surtr.collections.Collection`) resolves against the
   real sibling module rather than against a compilation that never had it. It reads sources
   from disk and references only the compiler, so `Surtr.Stdlib` can invoke it without a reference
   cycle back to itself.
2. The `BuildStdlibImages` target on `Surtr.Stdlib.csproj` runs the tool on every `dotnet build`,
   writing one `.surtrc` per source file into `build/` and a disassembled text rendering of each
   into `disasm/` — and then, in the same target, **embeds every `.surtrc` it just wrote as a
   resource of `Surtr.Stdlib.dll` itself**, under the logical name
   `Surtr.Stdlib.Images.<modulePath>.surtrc` (`Surtr.Stdlib.Images.surtr.math.Math.surtrc`, …). That
   embedding is why the target runs `BeforeTargets="BeforeBuild"` rather than `AfterTargets="Build"`
   the way it used to: an `<EmbeddedResource>` item only reaches the compiler's resource switch if
   it exists before resource-name preparation runs, which happens before `CoreCompile`, so the
   image has to be generated (and declared as a resource) *before* this project's own compile step,
   not after it. Two nested `<ItemGroup>`s do the declaring — `Include` first, `Update` second, to
   set the `<LogicalName>`/`<Link>` metadata — because `%(Filename)`/`%(Extension)` on a wildcard
   `Include`'s own children do not reliably self-resolve at target-execution time (a known MSBuild
   quirk); the same reference against an already-`Include`d item under `Update` resolves correctly
   per item.
3. `SurtrStdlib.cs` (namespace `Surtr.Stdlib`, this project) is the loader. `RegisterNativeBodies`
   publishes every native body a stdlib image can ask for (wiring `Native/SurtrMathNative.cs`'s
   methods to their link names — `surtr.math.Math.sin`, `surtr.math.Math.cos`, …).
   `SurtrStdlib.LoadAll(runtime)` reads every embedded `.surtrc` back via
   `Assembly.GetManifestResourceStream` and loads it; `LoadAll(runtime, selection)` filters by a
   `StdlibModules` flag (`Core`/`Math`/`Collections`/`Text`/`All`) first. `LoadInto(runtime, images)`
   and its `IEnumerable<byte[]>`/`StdlibModules` overloads still exist underneath for a host that
   wants to source images another way (a freshly compiled set, its own transport, a subset picked
   by hand) — `LoadAll` is `LoadInto` plus "read the images from this assembly's own resources"
   and nothing else. Either way, loading retries to a fixed point since an image carries no
   dependency list until instantiated.

Because each module is a normal module, no merge into the built-in `surtr` module is needed — the
linker and `TryResolveHandle` handle the cross-module references for free.

## Build

`Surtr.Stdlib.csproj` is a `netstandard2.1` class library like `Surtr.Core` — it has real C# of its
own now (`SurtrStdlib.cs`, `Native/SurtrMathNative.cs`) plus the same build-time image-compilation
target it always had. It references `Surtr.Core` via an ordinary `ProjectReference` (every type the
loader and the native bodies touch — `SurtrRuntime`, `SurtrModuleImage`, `SurtrNativeEntryPoint`,
`SurtrCallArguments`, `SurtrValue` — is public, so this needs no `InternalsVisibleTo`). It cannot
reference `Surtr.Stdlib.Tool` (`net8.0` is a wider target framework than this project's
`netstandard2.1`), so the tool is launched with `dotnet run --project` instead of referenced — no
cycle and no build recursion, since the tool references only `Surtr.Compiler`, never this project.

To rebuild and regenerate the images:

```
dotnet build src/Surtr.Stdlib/Surtr.Stdlib.csproj
```

The `BuildStdlibImages` target runs automatically (before this project's own compile step) and, for
each source file, writes one `.surtrc` into `src/Surtr.Stdlib/build/` and one disassembled text
rendering into `src/Surtr.Stdlib/disasm/`, named after the module path the way `SurtrBuild` names
its output — and embeds every `.surtrc` it wrote as a resource of `Surtr.Stdlib.dll` itself (see
"How the standard library reaches a runtime" above). It also writes `build/native-link-names.txt` —
the flat, sorted list of every native link name it found across all of them — which is what
`SurtrStdlibTests.EveryNativeLinkNameTheStdlibBuildCompiledIsRegistered`
(`src/Surtr.Tests/Stdlib/SurtrStdlibTests.cs`) compares against `SurtrStdlib.RegisterNativeBodies`
to catch a `native fun` added to the source without a matching C# body registered for it.

The disassembly comes from `SurtrBytecodeDisassembler.Disassemble` over the emitter-built module,
not over a re-instantiated image — the name tables a disassembler renders are only populated on the
emitter-built form, and that form is precisely what was just serialized into the `.surtrc`, so the
two can't drift.

The tool is also in `Surtr.sln`, so building the whole solution rebuilds it and regenerates the
images too.

## What remains to implement

Nothing structural is open on this project's side. Two things were closed here and are worth not
re-discovering:

- **The trap-to-class mapping** (`docs/VM-Plan.md` §1.9 × §13.3) lives in `Surtr.Core` and is
  done: every wrap site in `Execute` raises the built-in class the trap names, so
  `catch (e: IndexOutOfRangeException)` matches without a catch-all. The classes this module
  declares are the ones *beyond* that set (`NotSupportedException`), for user code to throw and
  catch among themselves.
- **`InvalidOperationException` deliberately does NOT live here.** It is one of the trap-mapped
  built-ins, and a same-named twin in this module split catch-by-type in two: `Stack.pop()` threw
  the twin while the VM raised the built-in, so a natural `catch` matched only one of them. The
  collections throw the built-in name, which is implicitly in scope everywhere; keep it that way.
