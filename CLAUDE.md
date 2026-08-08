# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Surtr is

Surtr is an embedded scripting language, written in C#, designed to be used inside Unity as a modern alternative to languages like Lua. It is strongly and statically typed.

Core design goals that shape most architectural decisions in this repo:

- **Own virtual machine.** Surtr compiles to its own opcodes and executes them on a custom VM, which runs alongside (and interoperates with) the standard C#/CLR runtime.
- **Unmanaged core.** The VM's core is intended to be unmanaged, making heavy use of `unsafe` C# wherever feasible, rather than relying on ordinary managed objects and GC-tracked memory.
- **Managed/unmanaged object registry.** Because the core is unmanaged but still needs to reference managed (CLR) objects, the project will need a registry that indexes managed objects, hands them an internal id usable from unmanaged code, and de-indexes them again — effectively a small, purpose-built garbage collector for that boundary.
- **Long-running project.** This is being built incrementally over a long timeframe; expect the codebase to grow substantially beyond the current skeleton.

## Performance is CRITICAL

This is a VM that runs inside a game engine's frame budget. Treat throughput as a hard requirement, not a nice-to-have, and prefer the faster construct even when the slower one reads better:

- **No hidden calls on hot paths.** Interface dispatch (`IReadOnlyDictionary<,>`, `IEnumerable<>`), virtual/abstract members, and delegates all cost an indirection the JIT usually cannot devirtualize. Prefer a `readonly` field set in the constructor over an abstract property, and a concrete `Dictionary<,>` accessor over an interface-typed one.
- **`[MethodImpl(MethodImplOptions.AggressiveInlining)]` on every small member** — property getters, thin wrappers, single-expression helpers.
- **Duplicate code rather than call out of a hot method.** The JIT gives up on inlining into large method bodies, so a helper called from a big loop stays a real call. In those cases inline it by hand and accept the repetition; a comment explaining *why* it is duplicated is worth more than the deduplication.
- **Cache anything derived.** If a value can be computed once at load time and read thereafter, store it — never recompute per call.
- **Avoid allocation on anything that runs per instruction, per call, or per collected object.** Prefer `struct`, `ref` returns, `Span<T>`, and the unmanaged helpers in `MemOps` / `SurtrNativeArray<T>` over managed temporaries.
- **Bounds checks count.** Where an index is provably in range, reach for `MemoryMarshal.GetReference` / `Unsafe` rather than paying for a check the JIT cannot elide.

Readability still matters for the compiler and tooling layers, which are not on the execution path. The rule above is about the runtime.

## The Surtr language model

Facts about the language itself (not just the implementation) that drive most of the type/metadata design:

- **Everything is an object.** Every value conceptually has its own `SurtrClass`, including primitives. `SurtrValue` / `SurtrRawValue` exist purely as a VM-level optimization so the interpreter can move primitives around without allocating or going through class metadata — they are a fast path, not a separate "non-object" tier in the language semantics.
- **Modules are the only top-level container.** A module can contain fields, properties, methods, classes and enums. A class can in turn contain fields, properties, methods and nested classes/enums.
- **There are no real globals in Surtr code.** "Global" only ever means module-level. The single exception is host-defined native variables and functions, which *are* genuinely global and can never be declared from Surtr source — only by the embedding host.
- **Strongly and statically typed**, so every member signature is fully known at compile time and type references are resolved from metadata rather than discovered at runtime.

### Inheritance and dispatch

A class extends at most one class and implements any number of interfaces. Interfaces are pure contracts — public abstract methods and properties only, no fields, no static members, and **no default implementations**; `SurtrInterface.AddMethod`/`AddProperty` reject anything else so the dispatch tables can assume it.

Methods are **non-virtual by default** (`SurtrMethodDispatch.Direct`) — a method is only virtual or abstract when it says so, and there is no implicit override. `SurtrMethodImplKind` (Bytecode / Native / Abstract) says *where the body is*; `SurtrMethodDispatch` (Direct / Virtual / Abstract) says *how the call resolves*. They are orthogonal, except that abstract dispatch always pairs with an abstract impl kind, since there is nothing to run.

`SurtrClass` and `SurtrInterface` share the `SurtrTypeInfo` base, so a `SurtrTypeHandle` can resolve to either and `Kind` distinguishes them with a field read rather than a cast.

The `internal` tables on `SurtrClass` are the runtime's view and are all **flattened at load time** — inherited entries are already folded in, so a lookup never walks the hierarchy. The name-keyed dictionaries alongside them are the compiler's view; nothing on the execution path may go through a name. Two details worth keeping:

- `Ancestors` is indexed by depth with `Ancestors[Depth] == this`, which makes a subtype test one compare plus one load at any hierarchy depth, instead of a walk.
- `ReferenceSlots` lists which instance slots hold a `SurtrRef`. Values are NaN-boxed and carry their own tag, so this is an optimization rather than a requirement — the point is that a statically typed language already knows which fields are references at compile time, so tracing should walk the k reference slots rather than tag-test all n. It also avoids NaN aliasing, where a raw double whose bits land in the tag range would read as a reference.

### Metadata has a build state

Every member, class, interface, module and chunk carries a `SurtrBuildState`: `UnderConstruction` → `Linking` → `Built`. Declaration APIs (`AddField`, `AddMethod`, `SetDeclaredInterfaces`, …) call `ThrowIfBuilt()` first, so nothing can be added once the tables have been flattened and slot indices handed out. `Linking` doubles as the cycle detector — meeting a type that is already linking means the hierarchy loops back on itself.

`SurtrTypeLinker` populates every runtime table, depth-first (base class and interfaces first, since a type's layout is built on top of theirs). It is load-time code and deliberately favours obvious correctness over allocation-freedom; the dictionaries and signature strings it builds are discarded once a type is linked. Two invariants it maintains and that the interpreter depends on: inherited field slots and inherited vtable slots **keep their base-class indices**, so a call site or field access compiled against the base keeps working on a derived instance. Overrides replace the vtable entry in place, which is also what makes an override automatically apply to every interface routed through that slot.

Methods carry three orthogonal axes. Only one of them — where the body lives — is modelled by subclassing (`SurtrBytecodeMethodInfo` / `SurtrNativeMethodInfo` / `SurtrAbstractMethodInfo`). `SurtrMethodDispatch` and `SurtrMethodRole` (normal / constructor / static initializer) are plain fields, because making either a second subclass axis would multiply against the first and produce types that carry no data of their own. Keep new axes as fields unless they genuinely add state.

### Type references are descriptor strings

Member signatures refer to types through `SurtrClassReference`, which wraps a compact **descriptor string** — not a `SurtrClass` instance (that would make class construction circular and order-dependent) and not a `SurtrRef` (those only exist after an entity is registered, which happens after construction).

The encoding is a JVM/CLR-style descriptor rather than a dotted C#-style full name, and the deciding reason is that composite types nest: `Array`, `Dictionary`, `Tuple` and `Closure` are all parameterized. Descriptors nest unambiguously and parse in one left-to-right pass with a single character of lookahead, where `Array<Dictionary<int, string>>` would need a real bracket-and-comma parser.

```
I F B C S                 integer, float, boolean, character, string
A<elem>                   array            AI            -> int[]
D<key><value>             dictionary       DIS           -> {int: string}
T(<elem>...)              tuple            T(IF)         -> (int, float)
L(<param>...)<ret>        closure          L(II)F        -> (int, int) -> float
O<fullname>;              Surtr class      Ogame.core:Entity.Handle;
N<fullname>;              native type      NUnityEngine:GameObject;
fullname := modulePath ':' typeName ('.' nestedTypeName)*
```

`V` (void) is a twelfth symbol that is deliberately *not* a type: it is only legal as a closure descriptor's return. It exists because every `SurtrMethodInfo` needs a return reference and a `ReturnVoid` method has nothing else to name. It resolves to `SurtrBuiltIns.Void` — an abstract, memberless marker class filling the same role as `System.Void` in the CLR — so that no type handle in the system is ever permanently unbound.

The `:` separating module path from type path is deliberate: the resolver splits it in O(1) instead of probing prefixes to find where the module ends. Descriptors are the canonical form for comparison, hashing and bytecode; `ToDisplayString()` exists purely for diagnostics — never key off it.

A descriptor becomes a real class through `SurtrTypeHandle`, which pairs the reference with the `SurtrClass` it names. Handles start unresolved (`null`) and are interned per module in a `SurtrTypeHandleTable`, so resolution runs once per distinct type and a module's handle table doubles as its dependency list.

### Native entry points use the *managed* calling convention

Every host function linked into Surtr has one fixed shape — `SurtrNativeFunction`, taking `(SurtrCallArguments arguments)` — so the interpreter has exactly one function-pointer cast on its call path regardless of a function's Surtr-level signature. `SurtrNativeEntryPoint` stores it as a plain address and `Invoke` issues one indirect call, so nothing downstream branches on how the host linked it.

The pointer is a **managed** `delegate*<...>`, not `delegate* unmanaged[...]<...>`. Surtr's host is always C#/Unity, so host functions are managed static methods: calling them directly avoids the reverse-P/Invoke stub and its GC mode transition entirely, and sidesteps IL2CPP's `[MonoPInvokeCallback]` restriction. The cost is that a raw pointer into a C/C++ library cannot be linked.

**Never** put an unmanaged address in a `SurtrNativeEntryPoint` — a `GetProcAddress` result or anything from `Marshal.GetFunctionPointerForDelegate`. Both sides are just `IntPtr`, so the compiler cannot catch the mismatch, and on x64 Windows it may even appear to work before breaking on x86, ARM or IL2CPP.

`FromFunctionPointer(&Method)` is the preferred registration path: compile-time, no reflection, AOT-safe. `FromDelegate` is a convenience for dynamically built registration tables — it requires a non-multicast delegate over a `static` method (an instance method or capturing lambda carries a hidden receiver that would corrupt the stack, so it is rejected up front) and resolves through reflection, which AOT backends may strip.

For reference, `delegate* unmanaged<...>` does not compile on `netstandard2.1` at all (CS8889); only the explicit `delegate* unmanaged[Cdecl]<...>` form does, should an unmanaged path ever be needed.

Because the convention is managed, the parameter can be an ordinary managed type rather than an erased one, and it is:

```csharp
delegate SurtrValue SurtrNativeFunction(SurtrCallArguments arguments);
```

`SurtrCallArguments` (`Runtime/Classes/SurtrCallArguments.cs`) is a `readonly unsafe ref struct` wrapping a raw `SurtrRawValue*` + length, plus the `SurtrRuntime` the call is running on. Being a `ref struct` is load-bearing, not decorative: it can never be boxed, stored in a field, captured by a lambda, or held across an `await`, so it cannot outlive the stack frame that owns the pointer's memory — the same guarantee `Span<T>` gives a raw pointer, on a domain type with accessor methods instead of a generic indexer.

The runtime arrives **as itself** inside the struct, not as a `void*` the callee turns back into an object. An erased context would cost a `GCHandle` dereference plus a `castclass` on every call (worse under IL2CPP), would let a bogus pointer through with no way to catch it, and would force the runtime to hold a weak self-handle just so the pointer didn't root it forever. Carrying it in `SurtrCallArguments.Runtime` means the collector keeps it alive for the call for free, and a native function taking zero Surtr-level arguments still reaches it — there is no second `runtime` parameter to be redundant with it.

Every accessor comes in two tiers, and this is the point of the type over a bare `Span<T>`:

- **Checked** (`this[int]`, `GetInt`, `GetValue`, `Resolve<T>`, `Get<T>`, `GetString`, …) — bounds-checked, and for entity lookups, null/type-checked with a clear exception. The tier a host writing its own native function should reach for: nothing has verified its call site the way a Surtr call site is verified at compile time.
- **Unchecked** (`GetRawUnchecked`, `GetUnchecked<T>`, `GetPrimitiveUnchecked`) — skips every check. Sound only when the index and type are already known good, which every built-in native method in `Runtime/BuiltIns` can rely on: `InvokeNative` only ever reaches one after the compiler matched its declared Surtr signature against the call site. This is the tier the built-ins use throughout, so they pay nothing beyond what the pre-`SurtrCallArguments` code already paid.

A host's function body needs **no `unsafe` and no `AllowUnsafeBlocks`** even though `SurtrCallArguments` wraps a pointer internally — none of the checked accessors expose one. The one exception is `Pointer`, an explicit escape hatch: its return type forces the *caller* into their own `unsafe` context to use it, regardless of the struct's own `unsafe` declaration (a type being `unsafe` lets its own members use pointers freely; it grants nothing to callers). `FromDelegate` keeps registration itself unsafe-free too; `FromFunctionPointer(&Method)` needs `unsafe` only at that one line, and remains the AOT/IL2CPP-safe path.

`arguments[0]` is the receiver for instance methods. A method declared to return nothing still returns something down this one signature — by convention `SurtrValue.Null`, which the caller discards.

## Runtime objects

`Runtime/Objects` holds everything the VM treats as a language-level value. All of it derives from `SurtrObject`, which carries the object's `SurtrClass` plus a cached copy of that class's `TypeCode` — one byte duplicated so a family test is a load off the object already in cache instead of a second hop into metadata.

| Type | Holds | Class |
|---|---|---|
| `SurtrString` | a CLR `string` + its cached hash | built-in, shared |
| `SurtrArray` | growable `SurtrValue[]` + count | built-in, shared |
| `SurtrTuple` | fixed `SurtrValue[]`, immutable | built-in, shared |
| `SurtrDictionary` | `Dictionary<SurtrValue, SurtrValue>` under the runtime's comparer | built-in, shared |
| `SurtrClosure` | method + captured values, with the dispatch payload copied out flat | built-in, shared |
| `SurtrBoxed` | one primitive `SurtrValue` | the *same* class the unboxed primitive has |
| `SurtrInstance` | `SurtrValue[]` field slots | whatever Surtr source declared |
| `SurtrNativeObject` / `SurtrNativeProxy` | a host CLR object; open for host subclassing | host-declared, or `SurtrBuiltIns.NativeObject` |

Rules that run through all of them:

- **Storage is managed, not `SurtrNativeArray`.** These are collectable values, and the registry sweeps by dropping its reference — there is no finalization hook — so an unmanaged buffer owned by one would leak on every collection. Unmanaged arrays belong to *metadata*, which is disposed explicitly.
- **No per-element type tags.** Static typing means the compiler already knows an `int[]` from a `string[]`, and NaN boxing means each element self-describes to the collector. What each composite keeps instead is one interned `TypeReference` descriptor (`AI`, `T(IS)`, `DIS`, `L(II)F`) naming its whole parameterised type — full information for diagnostics and host interop at one field per object rather than one per element.
- **Class metadata is never registered with the entity registry** and is never traced. It is owned outright — by `SurtrBuiltIns` for the built-ins, by `SurtrContext` for everything else — and lives as long as its owner, which is why `SurtrObject.VisitReferences` does not mark `Class`. It also *cannot* be registered: an entity holds a single `SurtrRef`, so one shared class in two registries would have the second silently inherit the first's id.
- **`SurtrValueComparer` decides equality**, not raw bits, and lives one-per-runtime. Bits are too strict for strings (two objects, same text, one key) and boxes (a boxed 5 *is* an unboxed 5, in both directions), and too loose for floats (`+0.0`/`-0.0`, NaN). Tuples compare structurally because immutability makes that stable; every other composite compares by identity.

### The built-in classes

`SurtrBuiltIns` holds one process-wide `SurtrClass` per family, built once in a static constructor into a module named `surtr` and linked before any runtime exists. Shared rather than per-context so two runtimes agree on what `string` means and a native entry point registered against one works in the other.

**One class covers every parameterisation** — `AI` and `AS` are both `SurtrBuiltIns.Array` — because a language with no dynamic top type settles element types at compile time. Their `SelfReference` is correspondingly the bare family symbol (`A`, `D`, `T`, `L`), deliberately *not* a well-formed descriptor: it names the family and says nothing about parameters, which is exactly what the class knows. There is no root `object` class; every built-in sits at depth 0 in its own hierarchy.

Members are native methods linked by function pointer via `SurtrBuiltInTypeBuilder`, always `Direct` dispatch (nothing extends a built-in, so a vtable slot would be an indirection with one occupant). Properties also emit `get_x`/`set_x` accessor methods, CLR-style, so the linker sees them.

**Known gap:** `array`, `tuple`, `dict` and `closure` carry a much thinner member surface than `string` or the primitives, because a descriptor names one concrete type and there is no way to write "the element type of whatever this array is". `push`, `pop`, `get`, `set`, `indexOf`, `keys` and the rest of the element-polymorphic surface therefore have no expressible signature. The behaviour exists as ordinary methods on `SurtrArray`/`SurtrDictionary`/`SurtrTuple` for the interpreter to call from `ArrGet`, `DictSet`, `TupGet` and friends; closing the gap for Surtr *source* needs a descriptor form for a built-in's own type parameter.

## The runtime and its context

`SurtrRuntime` is Surtr's `lua_State`: the one object a host holds. It owns a `SurtrContext` (internal struct, reached by `ref` so nothing copies it) holding the entity registry, the host global table, loaded modules, host-declared native classes, the interned-string table, the permanent root set, and the shared interface-id counter.

- **Loading a module** is "resolve every handle in its `TypeHandles`, then link". That table is the module's dependency list, so anything still unresolved afterwards is a load failure rather than a mid-execution surprise.
- **Interface ids are handed out from the context**, not restarted per module — `SurtrTypeLinker.LinkModule` has an overload taking `ref int nextInterfaceId` for exactly this.
- **Roots** are pre-boxed raw values (the shape the collector wants). A collection stages the caller's transient roots in the root buffer's slack past `RootCount`, so merging them needs no allocation.
- Interned strings are rooted permanently: use `InternString` for text a program is *built from*, `NewString` for text it *computes*.
- The alias names in `GlobalUsings.cs` (`SurtrRawValue`, `SurtrRef`, …) do not flow to consumers — host code outside the assembly sees `ulong` and `int`.

## The instruction set

`src/Surtr.Core/Bytecode/OpCode.cs` holds the VM's complete instruction set — currently **202 opcodes**, leaving 54 free values in the `byte` space. It is the authoritative reference; don't restate opcode semantics elsewhere, link to it.

Surtr is a stack machine. Operands come from the evaluation stack; pool indices, jump offsets and argument counts are encoded inline after the opcode byte as little-endian immediates.

**The enum value is the on-disk encoding.** Members are implicitly numbered from `Nop = 0x00`, so inserting one in the middle renumbers everything after it and silently invalidates every previously compiled chunk. Treat the list as append-only.

Naming conventions that run through the whole set:

| Affix | Meaning |
|---|---|
| `F` prefix | float operands (untagged opcodes cover int/bool/char, which share a representation) |
| `R` prefix | reference identity rather than value comparison |
| `Str` prefix | string operands compared by text rather than by identity |
| `X` suffix | widens an immediate to 4 bytes |
| `S` suffix | narrows an immediate to 1 byte |
| trailing digit | dedicated opcode for that fixed index, no immediate at all |

**There is no separate opcode for calling host code.** Where a call lands is a property of the `SurtrMethodInfo` the call site names, not of the call site, and the interpreter reads it anyway — a virtual call can resolve onto a native override, so the `ImplKind` test exists in the shared entry sequence regardless. Every `Invoke`/`Call` reaches bytecode and host bodies alike, for one byte load and a perfectly predicted branch. `CallGlobalNative` is the exception, and only because host globals live in a different *table*, not because they are native.

Allocation opcodes carry the full parameterised type (`ArrNew`, `ArrNewX`, `ArrPack`, `TupPack`, `DictNew`, `DictPack`, `DictKeys`, `DictValues`): one immediate gives both the descriptor the object keeps and the element family its slots are initialised from. `StaticFieldGet`/`StaticFieldSet` cover statics *and* module-level variables — Surtr has no true globals, so a module variable is a static of its module and reaches its storage the same way. `Switch`/`SwitchLookup` measure their offsets from their own opcode byte, unlike every other branch, because a variable-length instruction has no fixed "next address" at emit time.

Every member is documented with the same three-part `///` block, and new opcodes must follow it: **Encoding** (byte layout as `opcode(1) name(width)` plus total length), **Stack** (`before -> after`, `...` for the untouched remainder, rightmost entry is the top), and **Notes** only where behaviour isn't obvious from the name.

Pool indices refer to the tables on the declaring module's `SurtrChunk`. Trap behaviour is now pinned down by the interpreter — see the table in `docs/VM-Plan.md` §1.6 for what traps, what is defined, and what is deliberately unchecked.

## The virtual machine

`src/Surtr.Core/VM/` executes bytecode. `SurtrVirtualMachine` is **internal** — a host that could reach it could push onto the data stack between calls or start a run at an arbitrary frame, and every invariant the interpreter relies on would become the host's to maintain. The public surface is `SurtrRuntime.Invoke`, `InvokeClosure` and `ResetExecution`, each a complete operation. The runtime owns exactly one machine, because its data stack is a collection root and `Collect()` can only be correct with a single stack to scan. Execution on a runtime is single-threaded, like a `lua_State`.

- **Two stacks, both fixed size.** The data stack is unmanaged `SurtrRawValue` (the collector scans it through a raw pointer); the call stack is a managed `SurtrCallFrame[]` (a frame holds its chunk, method and closure, which the CLR has to keep alive). Neither grows: a reallocation would dangle every `sp` spilled in a suspended dispatch loop, which is exactly what a re-entrant native call leaves behind.
- **One `switch`, not a table of function pointers.** A function-pointer table costs a real call per instruction that C# cannot turn into a tail-jump, plus spilling `ip`/`sp`/the frame's pools across it. Everything hot lives in locals of `Execute`, and every opcode body is written out where it is used — never call a helper from the dispatch loop. The two shared call sequences at the bottom are reached by `goto`, not by a call.
- **One calling convention.** Arguments are already on the stack and the callee's frame starts underneath them, so entering a call copies nothing. `argsCount` counts every incoming slot, **receiver included**, which makes the frame base `sp - argsCount` for every kind of call. `retCount` is 0 or 1. Stack room is checked once per call against the callee's `MaxStackSize` — never per push.
- **Re-entrancy is the point of the frame protocol.** `sp` and the executing frame's `IP` are published before every transfer into host code, and `Execute(entryDepth)` returns when the depth falls back to where it started, so a native function can call back into the VM and unwind cleanly.
- **A reference is its 32-bit payload**, not its tag. That makes a zeroed slot and an explicit null the same reference, which is why fresh locals read as null without the VM knowing their declared type. Where the tag *does* matter — a value handed to a native function, or boxed — `ArrNew` fills a fresh array with its element family's correctly tagged zero.
- **Exceptions are handler tables, not handler opcodes.** `SurtrBytecodeMethodInfo.Handlers` holds protected ranges, so entering a `try` emits nothing and costs nothing; only a raise pays. A Surtr `Throw` never becomes a CLR exception while a handler is in reach — the machine walks its own frames. A VM trap or anything host code throws arrives as a CLR exception, gets wrapped as an object, and goes through the same search; only what nothing catches leaves, as `SurtrThrownException`. **`finally` is the compiler's job**: emit the block on each exit path plus a catch-all that runs it and re-raises, exactly as javac does — that keeps `Leave`/`EndFinally` out of the instruction set.
- **Static initializers run eagerly at module load**, classes before the module, in declaration order. Lazy initialization would cost a "has this run" test on every static access forever to answer a question that is false exactly once. That is also why `InvokeStatic` carries no type index.

## Generics are erased

Generics are a compile-time construct, checked and then discarded, as Java's are. The runtime answers exactly one question about them: what a field declared `T` looks like in memory, since instance layout and the reference-slot map are built from declared types. That answer is `SurtrValueTypeCode.Erased` (descriptor symbol `E`, resolving to `SurtrBuiltIns.Erased`), which sits inside the reference-type range — an erased slot is always a reference, always traced, and `IsReferenceType` stays a range compare.

The compiler owes two things in exchange, the same two Java's does: **box primitives** flowing into an erased slot, and **insert a `Cast`** when reading one back out. No opcode, metadata table or dispatch path needs to know a generic existed.

`docs/VM-Plan.md` carries the full rationale for every decision above, the remaining gaps (interface dispatch does a linear scan, array access pays two bounds checks, a module is tied to the runtime that loaded it), and the ordered plan.

## Commands

Build the whole solution:

```
dotnet build Surtr.sln
```

Build just the core library:

```
dotnet build src/Surtr.Core/Surtr.Core.csproj
```

Run the test suite:

```
dotnet test Surtr.sln
```

There is no lint config or CI yet — add commands here once those exist rather than assuming a standard `dotnet format` invocation applies.

## Architecture / structure

- `Surtr.sln` — solution root; `src/<ProjectName>/<ProjectName>.csproj` is the layout for every project (core library, test project, future Roslyn source generator).
- `Directory.Build.props` — MSBuild settings shared by *every* project in the solution (`LangVersion`, `Nullable`, etc.). Put cross-project settings here, not per-project settings. `ImplicitUsings` is deliberately left off (default/disabled) — usings must be written explicitly in every file.
- `src/Surtr.Core` — the main library, built for `netstandard2.1` so the compiled DLL can be dropped into Unity (2021.2+, including IL2CPP) as a plain managed assembly. `AllowUnsafeBlocks` is enabled here specifically because the VM/registry work described above depends on it; don't assume other projects (e.g. a future source generator) need or should have it. `LangVersion` is inherited as `latest` from `Directory.Build.props` — targeting `netstandard2.1` does not cap the C# language version, since `TargetFramework` (runtime/BCL surface) and `LangVersion` (compiler syntax) are independent settings. Only watch for C# features that need runtime support beyond what's in the BCL (e.g. default interface methods), since Unity's Mono/IL2CPP backends can behave unreliably there even though the code compiles fine.
- `src/Surtr.Tests` — xUnit test project, targeting `net8.0` (it runs standalone under the .NET SDK, not inside Unity, so it isn't bound to `netstandard2.1` the way the core library is). `AllowUnsafeBlocks` is enabled here too, since chunk-building tests poke at the same unmanaged surface. `GenerateDocumentationFile` is turned back off here — a test assembly is never consumed as a library, so `CS1591` would just be noise. `Surtr.Core` grants it `[InternalsVisibleTo("Surtr.Tests")]` (in `AssemblyInfo.cs`) so tests can reach `internal` types like `SurtrVirtualMachine` and `SurtrChunk` directly, alongside exercising the public `SurtrRuntime` surface. Folder layout mirrors `src/Surtr.Core` (`Runtime/Objects`, `Runtime/Classes`, `VM`, …) so a test's location tells you what it covers.

Inside `src/Surtr.Core`: `Bytecode/` is the instruction set, `Runtime/Classes/` the type metadata and linker, `Runtime/Objects/` the runtime values and the entity registry, `Runtime/BuiltIns/` the shared built-in classes and their native members, `Runtime/Utilities/` the unmanaged helpers, `VM/` the interpreter.

The instruction set, the metadata layer, the object registry, the runtime object model, the built-in classes and the interpreter exist. **The compiler does not** — nothing produces bytecode yet, so a chunk has to be built by hand to run anything (as the tests in `src/Surtr.Tests` do). Building it is the next major piece of work; the remaining runtime-side gaps are listed in `docs/VM-Plan.md` §3.

Because nothing persists a chunk yet, the opcode set is still being *shaped* rather than extended: new members go next to their family, and the append-only rule in `OpCode.cs` takes effect the moment anything serializes bytecode.

## Coding conventions

- Every `.cs` file starts with `#nullable enable`, even though `Nullable` is already `enable` at the project level via `Directory.Build.props`. This is intentional and non-negotiable — don't remove it as "redundant".
- No `ImplicitUsings` — write out every `using` directive explicitly in each file (see above).
- Any documentation of a type, method, property, or field must use `///` XML doc comments (`<summary>`, `<remarks>`, `<param>`, etc.), never a plain `//` block sitting above the declaration — that's what lets Visual Studio's IntelliSense pick it up. This is about *format*, not coverage: it doesn't mandate documenting every member. Plain `//` comments are still the right tool for a short, non-obvious implementation note *inside* a method body (a specific line or block), since those aren't documenting a declaration and `///` can't attach to arbitrary statements anyway.
