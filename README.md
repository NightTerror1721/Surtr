# Surtr

An embedded scripting language written in C#, with strong static typing, designed to run inside
**Unity** as a modern alternative to languages like Lua.

Surtr is not a Lua clone with a different face. It is a statically typed, compiled language with
its **own virtual machine**: `.surtr` source compiles to `.surtrc` module images, which a host (a
game, an editor tool, a CLI) loads into a `SurtrRuntime` and calls into, alongside the CLR. Because
the host is C#, every host function and host-declared type can be bound straight into the language
with no FFI trampoline — Surtr speaks to C# the way a library calls its own classes.

---

## What Surtr is

Surtr is built around a handful of deliberate design goals that shape every part of the codebase:

- **Own virtual machine.** Source compiles to Surtr's own opcodes and runs on a custom stack
  machine that executes *alongside* the standard C#/CLR runtime. Surtr is not a hosted interpreter
  over C# objects; it is a real VM with its own value model, heap, and collector.
- **Strong, static typing.** Every member signature is fully known at compile time, so the runtime
  never discovers a type by name, never tags per element, and never boxes on the hot path. Static
  typing is what lets the VM be fast: the compiler already settled the type of every value, so the
  interpreter just moves bits.
- **Built for the frame budget.** This is a VM that runs inside a game engine's frame. Allocation
  per instruction is treated as a defect, hot-path dispatch is a single `switch` with no hidden
  virtual/interface/delegate indirection, and derived data is cached at load time rather than
  recomputed.
- **An unmanaged core.** The VM leans on `unsafe` C# wherever feasible — an unmanaged data stack,
  raw buffers for metadata — with a purpose-built **entity registry** (a small garbage collector)
  as the single managed/unmanaged boundary. CLR objects referenced by the VM are indexed into the
  registry, handed an internal id usable from unmanaged code, traced, and de-indexed again.
- **A long-running project.** The language, compiler, runtime and tooling are being built
  incrementally, and the codebase is expected to keep growing substantially.

The whole toolchain is written in C# and builds as ordinary .NET assemblies (`netstandard2.1` for
everything meant to ship into Unity, `net8.0` for the standalone tools), so a Unity host's drop-in
is literally a pair of DLLs in `Assets/Plugins`.

---

## A taste of the language

```surtr
// Ogame/core/Entity.surtr  ->  module Ogame.core.Entity (derived from the file's location)
import Ogame.core.Vec2;

value class Health {
    public let value: int;
    public constructor(value: int) { this.value = value; }
}

enum Kind { Player, Mob }                    // Java-style: each case is a real instance

public sealed class Entity {
    public let id: int;                    // assign-once field
    public var position: Vec2;             // mutable field
    public native fun log(message: string): void;   // body supplied by the host

    public constructor(id: int, position: Vec2) {
        this.id = id;
        this.position = position;
    }

    public fun describe(): string {
        return "entity #$id at $position";  // string interpolation
    }

    public fun moveTowards(target: Vec2, maxStep: float): void {
        let d = target - this.position;
        if (d.length() <= maxStep) { this.position = target; }
    }
}
```

```csharp
// The host side: build a runtime, give `native fun log` a body, run a module.
using var runtime = new SurtrRuntime();

runtime.DefineNativeBody("Ogame.core.Entity.log",
    SurtrNativeEntryPoint.FromFunctionPointer(&Log));

SurtrModule module = runtime.LoadModule(image);            // a .surtrc image
module.TryGetMethods("describe", out SurtrMethodInfo[] overloads);
SurtrValue result = runtime.Invoke(overloads[0], entityValue); // "entity #1 at ..."
```

---

## The language

The surface syntax is TypeScript/Kotlin-flavoured: braces for blocks, `name: Type` annotation
order, a modern keyword set, optional `;` statement terminators (a line break ends a statement),
and trailing commas allowed everywhere a comma-separated list appears. Source files use the
**`.surtr`** extension.

### Modules and imports

A module is a file, and there is **no module header** — a file's module path is derived from its
location relative to the project's source root:

- `Ogame/core/Entity.surtr` is module `Ogame.core.Entity`
- a module-level *type* inside it is fully qualified as `Ogame.core.Entity.Entity`

Other modules come into scope with an `import` at the top of the file:

```surtr
import Ogame.core.Entity;                   // one module (one file)
import Ogame.core.*;                        // every module under the directory, recursively
import Ogame.core.Entity as Core;           // alias a whole module (Core.Entity)
import Ogame.core.Shapes.{Entity, Vec2};    // selected names from one module
import Ogame.math.Math.add;                 // a module-level *function*
import module Ogame.math.Math;              // a whole module's surface, no submodules
export import module Ogame.core.graphics.Mesh;   // re-export an aggregation
```

A name can always be written fully qualified even without an import; importing is convenience, not
a requirement. A colliding name pulled in from two imports is an error at the point of use.

### Types

Built-ins are spelled `int`, `float`, `bool`, `char`, `string`, `void` (return position only),
`range` (a half-open or closed interval of ints) and `unknown` (a top that holds anything but must
be cast before use). Composites:

| Syntax | Meaning |
|---|---|
| `T[]` (or `array<T>`) | array |
| `{K: V}` (or `dict<K, V>`) | dictionary |
| `(T1, T2, ...)` (or `tuple<...>`) | tuple |
| `(T1, ...) -> R` | closure / function type |
| `T?` | nullable (primitive or reference) |

Generics are written `Box<int>` with constraints like `T : IComparable<T> & IEquatable<T>`; they
are **erased** at compile time the way Java's are — checked and then discarded, with the runtime
only ever seeing one class per declaration. There is no root `object` class: a bare
`class Foo { }` sits at depth 0 in its own hierarchy.

### Declarations

```surtr
alias EntityId = int;                       // type alias: transparent
value class Health { public let value: int; }              // one field: erased to it, one slot
value class Vec2 { public let x: float; public let y: float; }  // two fields: two inline slots, no object
singleton Registry : IRegistry { ... }                     // class + one static instance

interface IShape {
    fun getKind(): Kind;
    name: string { get; }                   // property contract
}

enum Suit : ICardSuit {                     // Java-style: each case is a real instance
    Hearts("♥", true), Spades("♠", false);
    private let _symbol: string;
    private let _isRed: bool;
    constructor(symbol: string, isRed: bool) { _symbol = symbol; _isRed = isRed; }
}

abstract class Animal { public abstract fun speak(): string; }
class Dog : Animal { public sealed override fun speak(): string { return "Woof"; } }

native fun log(message: string): void;      // host-supplied body
native let ScreenWidth: int;                // host-owned, read-only from Surtr
native var TimeScale: float;                // host-owned, writable from Surtr

const fun square(x: int): int { return x * x; }           // folded at compile time
const if (Debug) { ... }                                   // conditional compilation, no preprocessor

@Range(0, 100)                                               // attribute, Java-style
public inline fun clamp<T : IComparable<T>>(v: T?, lo: T, hi: T): T? { ... }

fun format(pattern: string, args: string...): string { ... }   // varargs
fun spawn(x: float, y: float, hp: int = 100): Entity { ... }   // parameter defaults
```

Things to notice:

- **Mutability is explicit.** `let` binds once (a `readonly` field, not a compile-time constant);
  `var` is mutable; `const` is a value the compiler knows and the runtime never sees.
- **No `new`.** Instances are constructed by calling the type name: `Vec2(1.0, 2.0)`. There is no
  `static class` either — a module already is a container of members.
- **No real globals, anywhere.** "Global" only ever means module-level. A module can hold fields,
  properties, methods, classes and enums; a class can hold fields, properties, methods and nested
  types.
- **Explicit nullability.** `Type?` is a distinct type; `?.` and `??` work as expected, `as?` is a
  safe cast, `!!` is a "give me the value or raise" operator, and `x is T` narrows a variable's
  type inside the branch it guards.

### Operators and expressions

The operator table covers the usual arithmetic, bitwise, comparison and assignment forms plus
C-family extras: `===`/`!==` identity comparison, `??`/`??=`, `?:`, and a `<=>` three-way comparison
that returns an `int`. Operator overloading uses spelled-out names:

```surtr
operator+(a: Vec2, b: Vec2): Vec2 { ... }
operator==(a: Vec2, b: Vec2): bool { ... }
operator<=>(a: Vec2, b: Vec2): int { ... }
operator[](i: int): float { ... }            // indexer, both get and set forms
operator as Vec3(v: Vec2) { ... }            // explicit conversion
```

String interpolation is `"entity #$id"` or `"${expr}"`. Compound assignments (`x += 1`) expand to
their long form. There is no user-defined *implicit* conversion — `operator as` is explicit only.

### Control flow

```surtr
for (i in 0..=10) { ... }                    // inclusive range
for (j in 0..items.length) { ... }           // half-open range
outer: for (i in 0..=10) { continue outer; break outer; }   // labelled loops
for (p in pairs) { ... }                     // for-in over any IIterable<T>
while (cond) { ... }

let label = switch (v) {                     // switch as an expression
    1 -> "one",
    2, 3 -> "two or three",
    else -> "other",
};

switch (acc) {                               // switch as a statement
    case 1: case 2: break;
    default: break;
}

try { risky(); }
catch (e: OutOfRangeException) { log(e.message); }
finally { cleanup(); }

throw OutOfRangeException("bad");            // throw is also an expression
```

`for-in` over any collection is *defined by* the `IIterable<T>` contract the built-in `array`,
`string`, `tuple`, `dict` and `range` all implement — but a compiled loop over any of them still
lowers to an indexed loop and never allocates a cursor.

### Compile-time evaluation

`const`, `const fun` and `const if` move work to compile time — including conditional compilation,
with no preprocessor. A `const fun` is folded by **running its real bytecode on a real VM** rather
than by a second evaluator in the compiler, so compile-time and runtime semantics cannot drift.
`inline`/`forceinline` splice a body into a call site; `@Attributes` decorate any declaration and
are themselves real classes.

---

## The compiler

`src/Surtr.Compiler` is a complete front end written in C# (`netstandard2.1`, so it can sit beside
the runtime inside Unity):

- **Lexing & parsing** — `Syntax/` produces a typed token stream and a full AST, with spans that
  run from first to last token so tooling can underline a construct. The parser recovers from
  errors at declaration and statement boundaries instead of giving up.
- **Binding** — `Binding/` resolves types and members in phases (declaration → hierarchy/members →
  bodies), because a signature can name a type declared later. This is where overload resolution,
  member lookup, conversions, generic inference, flow analysis, nullability narrowing and switch
  exhaustiveness all live.
- **Code generation** — `CodeGen/` lowers each bound body to bytecode through `SurtrCodeEmitter`
  and writes whole modules as **`.surtrc` images** (`SurtrModuleImage`, format-versioned). Type
  signatures travel as compact descriptor strings (see below), and native members travel as names.

### Type descriptors

Member signatures refer to types through compact **descriptor strings** — a JVM/CLR-style encoding
that nests unambiguously and parses in one left-to-right pass with a single character of lookahead:

```
I F B C S          int, float, boolean, character, string
R                  range of ints
A<elem>            array            AI         -> int[]
D<key><value>      dictionary       DIS        -> {int: string}
T(<elem>...)       tuple            T(IF)      -> (int, float)
L(<param>...)<ret> closure          L(II)F     -> (int, int) -> float
O<fullname>;<arg>...  Surtr class   Ogame.core:Entity.Handle;
N<fullname>;<arg>...  native type   NUnityEngine:GameObject;
G<digit>           the declaring type's n-th generic parameter   G0
?<primitive>       nullable primitive                              ?I -> int?
V                  void (closure return only)
```

These descriptors are the canonical form for comparison, hashing and bytecode — and they are exactly
what the C# interop bridge accepts when you override a member's type (`TypeDescriptor`/
`ReturnDescriptor`).

### Building

The compiler is wrapped by two CLI tools:

```
surtrc build [path]      # a .surtrproj file, a directory holding one, or a source tree -> .surtrc images
surtr run <path> <module.path> <function> [args...]   # load .surtrc images into a real runtime and call a function
surtr list <path>        # list every module-level function a path declares (there is no main)
```

A `.surtrproj` file is one directive per line (no JSON dependency, since the compiler targets
Unity's BCL surface):

```
root    = src
module  = game
output  = build
define  Debug = true
reference ../engine/engine.surtrc
```

Because Surtr has **no `main`**, a host is expected to name whatever it wants called — `surtr run`
is the smallest host that does that from a shell, and `surtr list` is the discovery command a
"no main" language needs.

---

## The runtime and the VM

`src/Surtr.Core` (`netstandard2.1`, `AllowUnsafeBlocks`) is everything the VM treats as real.

### `SurtrRuntime` — the one object a host holds

`SurtrRuntime` is Surtr's `lua_State`: the whole public surface over the object heap, the loaded
modules, the host global table, and the entry points a host calls (`Invoke`, `InvokeClosure`,
`LoadModule`, `DefineNativeBody`, `Collect`, `ConfigureGc`, ...). Several runtimes can coexist in
one process with completely separate heaps and modules, and they still agree on what `string` or
`array` means, because the built-in classes are process-wide.

```csharp
using var runtime = new SurtrRuntime();          // 1024-entry heap by default
runtime.Invoke(method, args);                    // enter the VM
runtime.ResetExecution();                        // after a thrown exception leaves a frame
runtime.Collect();                               // run the registry's collector
```

Execution on a runtime is single-threaded, like a `lua_State`; disposal is the host's job (with a
finalizer as a backstop).

### Values are NaN-boxed

A `SurtrValue` is exactly 8 bytes: an `int`, `float`, `bool`, `char`, or entity reference packed
into one word. The top 16 bits hold a type tag (or ride inside a NaN float's payload), so a
primitive moves around the VM without ever touching the heap or going through class metadata.
Nullable primitives (`int?`, `float?`, ...) are first-class: absence is a reserved tag in an
ordinary value slot, never a heap allocation. A reference is its 32-bit payload — which is why a
zeroed slot and an explicit null are the same reference, and fresh locals read as null without the
VM knowing their declared type.

### The object model

Everything the VM treats as a language-level value derives from `SurtrObject`:

| Type | Holds |
|---|---|
| `SurtrString` | a CLR `string` plus its cached hash |
| `SurtrArray` | a growable `SurtrValue[]` |
| `SurtrTuple` | a fixed `SurtrValue[]` — the *boxed* form of a tuple, for the boundaries that need an object |
| `SurtrDictionary` | a `{K: V}` (with a specialised `int`-keyed store that skips the comparer) |
| `SurtrClosure` | a method plus captured values |
| `SurtrBoxed` | one primitive, under the *same* class the unboxed value has |
| `SurtrInstance` | the field slots of a class Surtr source declared — and the boxed form of a `value class` |
| `SurtrIterator` | a collection plus a position |
| `SurtrNativeObject` / `SurtrNativeProxy` | a host CLR object |

"Everything is an object" is the language's model; `SurtrValue` is purely a VM fast path so the
interpreter can move primitives without allocating or consulting metadata. Class metadata is owned
outright by its owner (the built-ins, or a runtime's context) and is never registered with the
collector.

**Value types have no row in that table, which is the point.** A `value class` and a tuple are
**`n` contiguous raw slots** — in a local, in a parameter, in a return, in an instance's field
block, in a static's storage — with no heap object, no entity id and nothing for the collector to
sweep. A one-field value class is one slot and erases to the field it wraps; a multi-field one is a
run, with a nested value type's slots folded into it. They become objects only at the boundaries
that hold a reference by definition — an array or dictionary element, a dictionary key, an erased
generic slot or `unknown`, an interface-typed variable, and the host boundary — where `BoxValue`
allocates an ordinary instance whose fields take the slots verbatim, so every path that already
walks instances needs no special case.

### The entity registry (the collector)

The registry indexes managed objects, hands each one an internal id usable from unmanaged code, and
de-indexes them again — a small, purpose-built garbage collector for the managed/unmanaged
boundary. Collection is explicit (`runtime.Collect()`), can be tuned through `SurtrGcPolicy`
(`ConfigureGc`), and only ever runs when the VM is idle: a collection can only be correct with a
single stack to scan, so the runtime owns exactly one machine.

### The interpreter

`SurtrVirtualMachine` is **internal** — the runtime's engine, not its API, so a host cannot corrupt
the stack. Its design:

- **Two fixed-size stacks.** The data stack is an unmanaged `SurtrRawValue` buffer (scanned by the
  collector through a raw pointer); the call stack is a managed `SurtrCallFrame[]`. Neither grows —
  a reallocation would dangle every `sp` spilled in a suspended dispatch loop.
- **One `switch`, not a table of function pointers.** Everything hot lives in locals of `Execute`,
  and every opcode body is written out where it is used — no helper calls from the dispatch loop.
- **One calling convention.** Arguments are already on the stack, the callee's frame starts
  underneath them, and `argsCount` counts the receiver too. Stack room is checked once per call
  against the callee's `MaxStackSize`, never per push.
- **Re-entrancy is the point of the frame protocol.** `sp` and the executing frame's `IP` are
  published before every transfer into host code, so a native function can call back into the VM
  and unwind cleanly.
- **Exceptions are handler tables, not handler opcodes.** Protected ranges live on the method; a
  `try` emits nothing and costs nothing, only a raise pays. `finally` is the compiler's job — the
  compiler emits the block on each exit path plus a catch-all, exactly as javac does.
- **Static initializers run eagerly at module load**, classes before the module, in declaration
  order — a "has this run" test on every static access forever costs more than one eager run.
- **Generics are erased**, Java-style: box primitives flowing into an erased slot, and insert a
  `Cast` when reading one back out. No opcode or dispatch path knows a generic existed.
- **Interface dispatch is an open-addressed `interfaceId → index` table** per class, numbered from
  a reserved range so a user contract can never collide with a built-in one.

The instruction set — **227 opcodes** in `src/Surtr.Core/Bytecode/OpCode.cs` — is a stack machine:
operands come from the evaluation stack; pool indices, jump offsets and argument counts are encoded
inline as little-endian immediates. There is **no separate opcode for calling host code**: where a
call lands is a property of the method the call site names, so a virtual call can resolve onto a
native override through the exact same call path.

### The built-in classes and the standard library

The built-in `surtr` module (process-wide, built once) carries only what the language's own type
system needs to exist: the primitives, the collections, and the core interfaces `IIterable<T>`,
`IIterator<T>`, `IComparable<T>`, `IEquatable<T>` that `for-in` and `<=>` lower onto. Everything
else the language assumes — `Exception` to extend, `Math`, collection helpers, `StringBuilder` —
is **standard library**, not core.

`src/Surtr.Stdlib` is that library, one module per `.surtr` file:

| Module | Contents |
|---|---|
| `surtr.math.Math` | float constants, trig/float `native fun`s, and ordinary Surtr logic (`abs`, `min`, `max`, `clamp`, `lerp`, `sign`, ...) |
| `surtr.math.Angle` | the `Angle` value class |
| `surtr.core.Exception` | exception subclasses beyond the built-in set |
| `surtr.core.Contracts` | `IDisposable<T>` |
| `surtr.collections.*` | `ICollection<T>`, `IList<T>`, helpers, `LinkedList<T>`, `Stack` |
| `surtr.text.StringBuilder` | the `StringBuilder` class |
| `surtr.diagnostics.Assert`, `surtr.io.*`, `surtr.random.*` | further modules as the library grows |

At build time a small tool compiles all of it into `.surtrc` images and **embeds them as resources
inside `Surtr.Stdlib.dll` itself**, so a host loads it with nothing but a runtime:

```csharp
using var runtime = new SurtrRuntime();
SurtrStdlib.LoadAll(runtime);                        // everything
SurtrStdlib.LoadAll(runtime, StdlibModules.Math);    // or just one category
```

The library is split across two languages on one rule: **native if it needs `unsafe`, a raw pointer
or a VM service; Surtr otherwise.** Today only `Math`'s sixteen trig/float operations are C#
(`Native/SurtrMathNative.cs`, bound by link name); the rest is real Surtr source, which doubles as
the largest program the compiler is asked to compile.

---

## Interop: how Surtr talks to C#

There are two layers, and both are deliberate.

### 1. The language-level `native` surface

`native` is a modifier on an ordinary member — a method, a property's accessor(s), or a module-level
function/variable — saying *where the body lives* (host code) rather than *how the declaration is
shaped*. A `native` member is a signature with no Surtr body; it lands in the **same method table**
as every other member, and a call to it is an ordinary call opcode.

```surtr
native fun log(message: string): void;        // module scope
native let ScreenWidth: int;                  // host-owned, read-only from Surtr
native var TimeScale: float;                  // host-owned, writable from Surtr

class Sprite {
    public fun move(dx: float, dy: float): void { this.setPosition(dx, dy); }  // compiled
    public native fun setPosition(dx: float, dy: float): void;                 // host
}
```

The host supplies the body under a **link name**, derived from the owner and signature
(`game:Sprite.setPosition(FF)`) or, for a module-level member, from the module path plus name
(`surtr.math.Math.sin`). That prefix is what keeps two same-named natives from binding against one
shared body. A module naming a native the host never published a body for **fails to load** — an
unbound method points at a body that says so, rather than at null.

```csharp
runtime.DefineNativeBody("surtr.math.Math.sin",
    SurtrNativeEntryPoint.FromFunctionPointer(&Sin));   // AOT-safe: compile-time, no reflection
```

Every host function has **one fixed shape**, so the interpreter has exactly one function-pointer
cast on its call path regardless of a function's Surtr-level signature:

```csharp
delegate SurtrValue SurtrNativeFunction(SurtrCallArguments arguments);
```

- The pointer is a **managed** `delegate*<...>`, not `delegate* unmanaged[...]<...>` — Surtr's host
  is always C#/Unity, so calling a managed static method directly avoids the reverse-P/Invoke stub,
  its GC mode transition, and IL2CPP's `[MonoPInvokeCallback]` restriction. Never put a raw
  unmanaged address in a `SurtrNativeEntryPoint`.
- `SurtrCallArguments` is a `readonly unsafe ref struct` wrapping a raw `SurtrRawValue*` plus the
  `SurtrRuntime` the call runs on. Being a `ref struct` is load-bearing: it can never be boxed,
  stored, captured, or held across an `await`, so it cannot outlive the stack frame that owns the
  pointer's memory.
- Accessors come in two tiers: **checked** (`this[int]`, `GetInt`, `Resolve<T>`, `GetString`, ...)
  for a host writing its own native functions, and **unchecked** (`GetRawUnchecked`,
  `GetUnchecked<T>`) for built-ins whose call site the compiler already verified. A host's body
  needs no `unsafe` unless it reaches for `Pointer`, the explicit escape hatch.
- `arguments[0]` is the receiver for instance methods; a method declared to return nothing still
  returns something down this one signature — `SurtrValue.Null`, by convention.
- A host can also register whole **native classes** (`DefineNativeClass`, `DefineNativeEnum`,
  `DefineNativeField`) directly against a runtime.

### 2. The C# bridge (`Surtr.Interop`)

Layered on top of the native surface is a **declarative bridge**: decorate CLR classes, structs and
enums with attributes and expose them to Surtr as first-class native types, with binding at compile
time (a Roslyn source generator, AOT-safe) or by reflection (runtime fallback).

```csharp
using Surtr.Interop.Attributes;

[SurtrNativeType(Module = "game")]                 // null Module = registered globally
public class Player {
    public int Health;
    public string Name { get; set; } = "";
    public int TakeDamage(int amount) => Health -= amount;
}
```

```csharp
using var runtime = new SurtrRuntime();

Surtr.Interop.SurtrBindings.RegisterAll(runtime);      // source generator path (AOT-safe)
// or
Surtr.Interop.SurtrBridge.ScanAndRegister(runtime, typeof(Player));   // reflection fallback
```

The package split keeps Unity happy: user code only references `Surtr.Interop.Attributes`
(`netstandard2.0`, zero dependencies); `Surtr.Interop` and the generator are host-side concerns.

- **Members.** A CLR *field* becomes a real native field (`SurtrNativeFieldInfo`), a CLR *enum*
  becomes a native enum with one cached object per value, and CLR *properties* and *methods* become
  native properties and methods. By default every public member is exposed with metadata derived
  from the C# signature; member attributes override individual details. `[SurtrNativeIgnore]`
  hides a member.
- **Marshaling.** Enums marshal O(1) with no boxing (cached, rooted instances wired into the
  cases); `out` parameters fold into the return (a tuple); `ref`/`in` are rejected; delegates
  become Surtr closures; structs are boxed and appear as ordinary classes; indexers and operators
  map when there is an equivalent. Generic types are supported as **closed forms** only
  (`TypeArguments`). Descriptor-based overrides (`TypeDescriptor`, `ReturnDescriptor`) accept the
  canonical descriptor strings above.
- **Naming.** `SurtrNamingPolicy` adapts C# names to the language's conventions (`Default`/`Surtr`
  keeps types PascalCase and adapts members to camelCase: `Add` -> `add`), with a precedence chain:
  global < runtime < module < class < member.
- **AOT / IL2CPP.** The generator emits static shims and links them with
  `SurtrNativeEntryPoint.FromFunctionPointer(&shim)` — no reflection. It requires
  `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in the consuming project (the "Allow unsafe code"
  checkbox on the Unity Assembly Definition holding the decorated types). The reflection fallback
  emits shims with `AssemblyBuilder` and is **not** AOT-safe — use it only where the generator
  isn't available.

---

## Editor tooling

- **`surtr-lsp`** (`src/Surtr.LanguageServer`, C# `net8.0`, stdio) is a Language Server speaking
  LSP over stdio. It runs the **real compiler binder**, so its answers are type-accurate and
  cross-file: hover signatures, go-to-definition, semantic tokens, inlay hints (`let x = 5` →
  `x : int`), diagnostics re-published on every edit, completions and code actions.
- **`vscode-surtr`** is the VS Code extension wiring that server up, plus a TextMate grammar,
  snippets, and language configuration (bracket colorization, auto-closing pairs). Point it at the
  built server with the `surtr.languageServer.path` setting, or drop `surtr-lsp` on your `PATH`.

---

## Repository layout

| Project | What it is |
|---|---|
| `src/Surtr.Core` | the runtime: bytecode, emitter, images, object model, built-ins, collector, interpreter. `netstandard2.1`, `unsafe`. |
| `src/Surtr.Compiler` | the front end: lexer, parser, binder, flow analysis, codegen, `SurtrBuild`. `netstandard2.1`. |
| `src/Surtr.Cli` | `surtrc` — the build command. |
| `src/Surtr.Run` | `surtr` — loads `.surtrc` images and calls into them; `surtr list`. |
| `src/Surtr.Stdlib` | the Surtr-written standard library plus its embedded `.surtrc` images and `SurtrStdlib.LoadAll`. |
| `src/Surtr.Stdlib.Tool` | compiles the stdlib source to images at build time. |
| `src/Surtr.Interop` | the C# bridge: model, marshaler, materializer, reflection fallback. |
| `src/Surtr.Interop.Attributes` | `[SurtrNativeType]` and friends (`netstandard2.0`, zero dependencies). |
| `src/Surtr.Interop.SourceGenerator` | the Roslyn analyzer that emits AOT-safe shims and the bindings catalog. |
| `src/Surtr.LanguageServer` | the LSP server. |
| `src/Surtr.Tests` | the xUnit suite (mirrors `Surtr.Core`'s folder layout). |
| `src/Surtr.Bench` | the benchmark harness. |
| `vscode-surtr` | the VS Code extension. |
| `Directory.Build.props` | shared MSBuild settings for every project. |

Inside `Surtr.Core`: `Bytecode/` is the instruction set, `Bytecode/Emit/` the emitter and
`Bytecode/Image/` the `.surtrc` serializer; `Runtime/Classes/` the type metadata and linker,
`Runtime/Objects/` the runtime values and the entity registry, `Runtime/BuiltIns/` the shared
built-in classes, `Runtime/Utilities/` the unmanaged helpers, `VM/` the interpreter.

---

## Building, testing, benchmarking

```bash
dotnet build Surtr.sln                     # everything, stdlib images included
dotnet test Surtr.sln                      # the xUnit suite
dotnet run --project src/Surtr.Bench -c Release    # benchmark the VM
```

The benchmark harness runs **30 cases, each written three times over** — in Surtr, in Lua
(MoonSharp), in LuaJIT, and as a natural C# baseline — and the three must agree on a checksum or the
run fails. It reports time, allocation (`alloc` is a first-class column — a VM inside a frame budget
is judged on allocation as much as on time), and a `spread` column that says whether a ratio is
trustworthy. On a recent run Surtr was roughly **17–20× faster than MoonSharp** (a managed,
all-objects Lua), about **3× slower than LuaJIT** (a highly optimized native JIT), and within a few
single-digit multiples of the natural C# baseline — with `dictString` beating LuaJIT outright and
`int`-keyed dictionary ops landing near C#. Always use `-c Release`: the Debug build roughly halves
Surtr's throughput.

The harness's csproj sets `TieredCompilationQuickJitForLoops=false` deliberately — under default
tiered compilation the interpreter loop itself is measured at tier 0 (unoptimized) for whole runs,
and the honest configuration for Surtr's real home (Unity's Mono JIT and IL2CPP AOT have no
tiering to warm up).

---

## License

Surtr is licensed under the **GNU Lesser General Public License v3** (LGPL-3.0). See
[`LICENSE`](LICENSE).