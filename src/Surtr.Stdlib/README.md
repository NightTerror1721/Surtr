# Surtr.Stdlib

The Surtr-written half of the standard library, organised as **one module per `.surtr` file**
(`surtr.exceptions.Exceptions`, `surtr.math.Angle`, `surtr.math.Math`, …), plus
`Surtr.Stdlib.Tool`, the tool that compiles each file to its own `.surtrc` image.

## Why this project exists

The standard library lives under the **`surtr` module path** — the same module `SurtrBuiltIns`
builds the primitives, collections and core interfaces into — so `string` and `Exception` are
siblings rather than residents of different worlds (`Language-Syntax.md` §13). §13.1 splits the
library across two languages on one rule:

> native if it needs `unsafe`, a raw pointer or a VM service; Surtr otherwise.

Today **everything is C#** (`Surtr.Core/Runtime/BuiltIns/SurtrStandardLibrary.cs`). That file's own
comment calls moving the expressible half across "a later, mechanical change: the classes keep
their names, their layout and their descriptors" — and `docs/Compiler-Plan.md` §10.2 lists *"the
standard library is entirely C#, where §13.1 puts the exception hierarchy below the root in Surtr
— which is also the largest program the compiler has never been asked to compile"* as the one thing
still owed. This project is that change.

## Why one module per file, not one module

The built-in `surtr` module is built and **linked once** in `SurtrBuiltIns`'s static constructor,
and `LoadModule` rejects a second module at path `surtr`. A module image that re-declared the
built-ins would shadow them rather than extend them (see the `SurtrModuleImage` class remarks). So
the Surtr-written half cannot be a module named `surtr`, and it cannot be merged into the built-in
module (that module's tables are flattened at link time).

But a module at a *longer* path — `surtr.exceptions.Exceptions`, `surtr.math.Angle` — is a
perfectly ordinary module. `SurtrRuntime.TryResolveHandle` (`SurtrRuntime.cs`) resolves a
`surtr:Exception` reference against the built-in module even when it comes from a separate loaded
module, because the built-in is reached by name rather than registered in the runtime's table. So
each stdlib module is a **real, separate module** that *extends the built-in `surtr` by reference* —
verified: a `surtr.exceptions.Exceptions` subclass `IsSubclassOf(SurtrBuiltIns.Exception)` is
`True`.

Each module is independently compilable and testable, and each is one `.surtrc` artefact. A file's
module path is its full location under `src/surtr/`, every directory segment plus the file name
(without extension) becoming a dotted segment — `src/surtr/math/Angle.surtr` is module
`surtr.math.Angle`, matching §2.1's "a module has no header, so where a file lives names it", with
the file name standing in as the final segment because the stdlib keeps one module per file.

## What is already in C# (do not re-implement)

The native half is complete and stays where it is. It is the *execution-path* code that needs VM
services, so it must be C#:

| Piece | Where |
|---|---|
| `Exception` root (the `_message` slot, `message`, `toString`, the constructor) | `SurtrStandardLibrary.DeclareException` |
| `Math` (all float/trig functions, `pi`/`tau`/`epsilon`) | `SurtrStandardLibrary.DeclareMath` |
| Core interfaces `IIterable<T>`/`IIterator<T>`/`IComparable<T>`/`IEquatable<T>` | `SurtrStandardLibrary.DeclareCoreInterfaces` |
| Collection members (`array`/`tuple`/`dict`/`closure`), incl. `array.sort` | `SurtrCompositeBuiltIns` |
| String members, incl. `string.format` | `SurtrStringBuiltIn` |
| `iterator` cursor | `SurtrIteratorBuiltIns` |

## The modules and their minimum contents

| Module | Sources | Contents |
|---|---|---|
| `surtr.exceptions.Exceptions` | `src/surtr/exceptions/Exceptions.surtr` | The exception subclasses below the root (`ArgumentException`, `IndexOutOfRangeException`, `KeyNotFoundException`, `NullReferenceException`, `DivideByZeroException`, `InvalidCastException`, `StackOverflowException`, `InvalidOperationException`). Each is a constructor and nothing else — the class a `catch` names is what distinguishes them. These are the classes a VM trap surfaces as (`Language-Syntax.md` §13.3). |
| `surtr.math.Angle` | `src/surtr/math/Angle.surtr` | The `Angle` value class. |
| `surtr.math.Math` | `src/surtr/math/Math.surtr` | The float constants, the trig/float `native fun` declarations (`sin`, `cos`, `atan2`, `sqrt`, `pow`, `log`, `floor`, `ceil`, `round`, `hypot`, …) whose bodies `SurtrStdlib.LoadInto` publishes by link name, and ordinary logic over them (`abs`, `min`, `max`, `clamp`, `lerp`, …). |

More modules can be added by dropping a `.surtr` file anywhere under `src/surtr/`; the build picks
it up automatically.

The dividing line to keep: if a member needs `unsafe`, a raw pointer or a VM service, it is C#;
otherwise it is Surtr.

## How the Surtr half reaches the runtime

`Surtr.Stdlib` is **tooling that compiles**, nothing more. It turns the `.surtr` sources into
images; it does not load them. `Surtr.Core` cannot reference this project (that would make the core
depend on the compiler), so the produced bytes must reach the core some other way:

1. `Surtr.Stdlib.Tool` (a `net8.0` console app that references only `Surtr.Compiler`) reads every
   `.surtr` file under `src/surtr/` and compiles each to its own `.surtrc` image. Because it
   references only the compiler and reads the sources from disk, `Surtr.Stdlib` can invoke it
   without any reference cycle.
2. The `BuildStdlibImages` target on `Surtr.Stdlib.csproj` runs the tool on every `dotnet build`,
   writing one `.surtrc` per source file into `build/` (`surtr.exceptions.Exceptions.surtrc`,
   `surtr.math.Angle.surtrc`, `surtr.math.Math.surtrc`) and a disassembled text rendering of each
   into `disasm/` (`surtr.exceptions.Exceptions.txt`, `surtr.math.Angle.txt`,
   `surtr.math.Math.txt`).
3. Those images are transported to a runtime by whatever means the host chooses — files read from
   `build/` (as `SurtrStdlibTests.cs` does, against the *committed* images, not a freshly compiled
   one), or a host's own embedded resources. What they cannot be is embedded as resources inside
   `Surtr.Core.csproj`'s own build: producing them needs a working `Surtr.Compiler`, which itself
   needs a built `Surtr.Core` — so one `dotnet build` of `Surtr.Core` cannot both compile the
   stdlib and bake the result into the very assembly that compiling it depends on. A `ProjectReference`
   from `Surtr.Core` to `Surtr.Stdlib` (even with `ReferenceOutputAssembly=false`, ordering only)
   was tried and hangs for exactly this reason: `Surtr.Stdlib`'s build shells out to
   `Surtr.Stdlib.Tool`, which needs to build `Surtr.Compiler` and `Surtr.Core` itself, and the
   outer `Surtr.Core` build is already in progress. The images stay **committed to the repo**
   under `build/` instead, regenerated by hand (or by CI, once one exists) whenever the `.surtr`
   sources change — the same tradeoff a self-hosted compiler's bootstrap always makes.
4. At runtime, `SurtrStdlib.LoadInto(runtime, images)` (`Surtr.Core/Runtime/SurtrStdlib.cs`) is the
   loader: it publishes every `native` body the images declare (under the link names their
   declarations travel as — `surtr.math.Math.sin`, `surtr.math.Math.cos`, …) and then
   `Instantiate()`-s and `LoadModule()`-s each stdlib module in order. A module-level `native fun`
   in a stdlib image binds to the same C# body the built-in `surtr:Math` class was built with, by
   link name. `LoadInto(runtime, images, selection)` takes a `StdlibModules` flag
   (`Core`/`Math`/`Collections`/`Text`/`All`) and loads only the images under the matching
   `surtr/<category>/` directories — for a sandboxed host that wants less than everything.

Because each module is a normal module, no merge into the built-in `surtr` module is needed —
the linker and `TryResolveHandle` handle the cross-module references for free.

## Build

The `Surtr.Stdlib.csproj` is a **source container** with a build target; it has no C# of its own.
The `netstandard2.1` project cannot reference the `net8.0` tool (the target framework is narrower),
so the tool is launched with `dotnet run --project` rather than referenced. No cycle and no build
recursion: the tool references only `Surtr.Compiler`, never this project, so running it does not
rebuild this assembly.

To rebuild and regenerate the images:

```
dotnet build src/Surtr.Stdlib/Surtr.Stdlib.csproj
```

The `BuildStdlibImages` target runs automatically (after `Build`) and, for each source file, writes
one `.surtrc` into `src/Surtr.Stdlib/build/` and one disassembled text rendering into
`src/Surtr.Stdlib/disasm/`, named after the module path the way `SurtrBuild` names its output —
`surtr.exceptions.Exceptions.surtrc`/`surtr.exceptions.Exceptions.txt`,
`surtr.math.Angle.surtrc`/`surtr.math.Angle.txt`, `surtr.math.Math.surtrc`/`surtr.math.Math.txt`.
`build/` is where you'd point `Surtr.Run` or consume the images; `disasm/` is the human-checkable
view of exactly what got compiled (each method's opcodes, offsets and branch targets). It also
writes `build/native-link-names.txt` — the flat, sorted list of every native link name it found
across all of them — which is what `SurtrStdlibTests.EveryNativeLinkNameTheStdlibBuildCompiledIsRegistered`
compares against `SurtrStdlib.RegisterNativeBodies` to catch a `native fun` added to the source
without a matching C# body registered for it.

The disassembly comes from `SurtrBytecodeDisassembler.Disassemble` over the emitter-built module,
not over a re-instantiated image — the name tables a disassembler renders are only populated on the
emitter-built form, and that form is precisely what was just serialized into the `.surtrc`, so the
two can't drift.

The tool is also in `Surtr.sln`, so building the whole solution rebuilds it and regenerates the
images too.

## What remains to implement (in `Surtr.Core`)

1. **Embedding the images as resources inside a host's own assembly** is the host's job, not
   `Surtr.Core`'s — see "How the Surtr half reaches the runtime" above for why `Surtr.Core.csproj`
   itself cannot do this (the bootstrap circularity a `ProjectReference` back to `Surtr.Stdlib`
   runs into). `SurtrStdlib.LoadInto`'s `IEnumerable<byte[]>` overloads are the shape a host's own
   embedded resources arrive in once it does; `StdlibModules` lets it embed and load a subset.
2. **The trap-to-class mapping** (`docs/VM-Plan.md` §1.9 × §13.3) is what actually makes
   `catch (e: IndexOutOfRangeException)` work; until the wrap sites in `Execute` name these real
   classes, only a catch-all matches. That coupling is tracked in `docs/VM-Plan.md` §4.2 and is
   part of this work.
