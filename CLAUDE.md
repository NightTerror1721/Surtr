# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Surtr is

Surtr is an embedded scripting language, written in C#, designed to be used inside Unity as a modern alternative to languages like Lua. It is strongly and statically typed.

Core design goals that shape most architectural decisions in this repo:

- **Own virtual machine.** Surtr compiles to its own opcodes and executes them on a custom VM, which runs alongside (and interoperates with) the standard C#/CLR runtime.
- **Unmanaged core.** The VM's core is intended to be unmanaged, making heavy use of `unsafe` C# wherever feasible, rather than relying on ordinary managed objects and GC-tracked memory.
- **Managed/unmanaged object registry.** Because the core is unmanaged but still needs to reference managed (CLR) objects, the project will need a registry that indexes managed objects, hands them an internal id usable from unmanaged code, and de-indexes them again — effectively a small, purpose-built garbage collector for that boundary.
- **Long-running project.** This is being built incrementally over a long timeframe; expect the codebase to grow substantially beyond the current skeleton.

### The documentation map

This file is the orientation; each of these goes deep on one thing. Read the relevant one before changing that area, and update it in the same commit — a doc that contradicts the code is worse than no doc.

| Document | Covers |
|---|---|
| `docs/Language-Syntax.md` | The surface language, and the reasoning behind each choice. §1.2 is the authoritative reserved word list, §5.7 the operator table. |
| `docs/Runtime-Model.md` | How classes, methods, properties, enums, interfaces and modules fit together, what linking builds, and what the compiler owes the runtime. |
| `docs/Opcodes.md` | All 214 opcodes by family, with values, encodings and stack effects. Generated from `OpCode.cs`, which stays the source of truth. |
| `docs/Module-Format.md` | The `.surtrc` byte layout, and what is bound at load rather than written. |
| `docs/VM-Plan.md` | The interpreter's design decisions, the remaining gaps, and the ordered plan. |

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
R                         range of ints (both bounds int, so not parameterised)
A<elem>                   array            AI            -> int[]
D<key><value>             dictionary       DIS           -> {int: string}
T(<elem>...)              tuple            T(IF)         -> (int, float)
L(<param>...)<ret>        closure          L(II)F        -> (int, int) -> float
O<fullname>;<arg>...      Surtr class      Ogame.core:Entity.Handle;
N<fullname>;<arg>...      native type      NUnityEngine:GameObject;
G<digit>                  the declaring type's n-th generic parameter   G0
?<primitive>              nullable primitive                           ?I -> int?
fullname := modulePath ':' segment ('.' segment)*
segment  := typeName ('`' arity)?                Obox:Box`1;I  -> Box<int>
```

A generic type mangles its **arity into its name segment**, which is what makes arity part of a type's identity and is also why the argument list after `;` needs neither brackets nor a count — the reader has the arity before it reaches the arguments, so one pass with one character of lookahead still holds. Only the **last** segment's arity counts; earlier ones are qualification, since a type nested inside a generic one does not see its container's parameters. Backtick is illegal in a Surtr identifier, so a mangled name cannot collide with a declared one, and a non-generic type's descriptor is unchanged.

`G<digit>` and `?<primitive>` both keep the one-character-of-lookahead property: each is a fixed two-character form. `?` is legal only before a primitive, because a nullable *reference* needs no encoding — a reference is its 32-bit payload and null is already representable — and a second descriptor for a type that already has one would break descriptors being the canonical form for comparison and hashing.

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
| `SurtrIterator` | a collection + a position; a dict's keys snapshotted | built-in, shared |
| `SurtrNativeObject` / `SurtrNativeProxy` | a host CLR object; open for host subclassing | host-declared, or `SurtrBuiltIns.NativeObject` |

Rules that run through all of them:

- **Storage is managed, not `SurtrNativeArray`.** These are collectable values, and the registry sweeps by dropping its reference — there is no finalization hook — so an unmanaged buffer owned by one would leak on every collection. Unmanaged arrays belong to *metadata*, which is disposed explicitly.
- **No per-element type tags.** Static typing means the compiler already knows an `int[]` from a `string[]`, and NaN boxing means each element self-describes to the collector. What each composite keeps instead is one interned `TypeReference` descriptor (`AI`, `T(IS)`, `DIS`, `L(II)F`) naming its whole parameterised type — full information for diagnostics and host interop at one field per object rather than one per element.
- **Class metadata is never registered with the entity registry** and is never traced. It is owned outright — by `SurtrBuiltIns` for the built-ins, by `SurtrContext` for everything else — and lives as long as its owner, which is why `SurtrObject.VisitReferences` does not mark `Class`. It also *cannot* be registered: an entity holds a single `SurtrRef`, so one shared class in two registries would have the second silently inherit the first's id.
- **`SurtrValueComparer` decides equality**, not raw bits, and lives one-per-runtime. Bits are too strict for strings (two objects, same text, one key) and boxes (a boxed 5 *is* an unboxed 5, in both directions), and too loose for floats (`+0.0`/`-0.0`, NaN). Tuples compare structurally because immutability makes that stable; every other composite compares by identity.

### The built-in classes

`SurtrBuiltIns` holds one process-wide `SurtrClass` per family, built once in a static constructor into a module named `surtr` and linked before any runtime exists. Shared rather than per-context so two runtimes agree on what `string` means and a native entry point registered against one works in the other.

**One class covers every parameterisation** — `AI` and `AS` are both `SurtrBuiltIns.Array` — because a language with no dynamic top type settles element types at compile time. Their `SelfReference` is correspondingly the bare family symbol (`A`, `D`, `T`, `L`), deliberately *not* a well-formed descriptor: it names the family and says nothing about parameters, which is exactly what the class knows. There is no root `object` class; every built-in sits at depth 0 in its own hierarchy.

Members are native methods linked by function pointer via `SurtrBuiltInTypeBuilder`, `Direct` dispatch by default (nothing extends a built-in, so a vtable slot would be an indirection with one occupant). The exception is a member satisfying an interface: interface dispatch resolves through the receiver's vtable, so `iterate`, `moveNext` and `get_current` are declared `Virtual` or the linker could not find them. Properties also emit `get_x`/`set_x` accessor methods, CLR-style, so the linker sees them.

**`array`, `string`, `tuple`, `dict` and `range` implement `IIterable<T>`** and hand back a shared `iterator` class, so the contract `for-in` is defined by (`Language-Syntax.md` §4.2) is one every collection actually satisfies rather than one only user code can keep. A compiled `for-in` over any of them still lowers to an indexed loop and never allocates a cursor; the contract exists so an `int[]` can flow into an `IIterable<int>`. A dict yields `(K, V)` pairs, walked over a snapshot of its keys.

A member implementing a generic interface is matched on the **erased** signature: `SurtrMethodInfo.SignatureKey()` writes `G<n>` as `E`, because after erasure they are the same slot and an implementation could otherwise never line up with the contract's. The other half of that bargain is Java's: a class wanting both `compareTo(Vec2)` and `IComparable<Vec2>` needs the compiler to emit a bridge.

**`array` and `dict` declare real generic parameters** — `T`, and `K`/`V` — and their element-polymorphic members are declared against them through the descriptor `G<n>`, which names the declaring type's n-th parameter. `G0` resolves to `SurtrBuiltIns.Erased`, so the runtime representation is exactly what `E` would have been and no layout, tracing or dispatch path knows the difference; what it adds is *which* parameter it is, which is what lets `int[].push("x")` be rejected against metadata alone. `push`, `pop`, `get`, `set`, `insert`, `indexOf`, `contains`, `remove`, `keys` and `values` are declared this way, and `length` is the uniform spelling of size on all four collections.

`tuple` and `closure` declare no parameters and keep the thin surface, deliberately: both are parameterised by a *list* whose length varies per value, and a tuple's element type varies per index, so no fixed parameter could name what `get(index)` returns. Element access there stays `TupGet` with a statically known index.

## The runtime and its context

`SurtrRuntime` is Surtr's `lua_State`: the one object a host holds. It owns a `SurtrContext` (internal struct, reached by `ref` so nothing copies it) holding the entity registry, the host global table, loaded modules, host-declared native classes, the interned-string table, the permanent root set, and the shared interface-id counter.

- **Loading a module** is "resolve every handle in its `TypeHandles`, then link". That table is the module's dependency list, so anything still unresolved afterwards is a load failure rather than a mid-execution surprise.
- **Interface ids are handed out from the context**, not restarted per module — `SurtrTypeLinker.LinkModule` has an overload taking `ref int nextInterfaceId` for exactly this.
- **Roots** are pre-boxed raw values (the shape the collector wants). A collection stages the caller's transient roots in the root buffer's slack past `RootCount`, so merging them needs no allocation.
- Interned strings are rooted permanently: use `InternString` for text a program is *built from*, `NewString` for text it *computes*.
- The alias names in `GlobalUsings.cs` (`SurtrRawValue`, `SurtrRef`, …) do not flow to consumers — host code outside the assembly sees `ulong` and `int`.

## The instruction set

`src/Surtr.Core/Bytecode/OpCode.cs` holds the VM's complete instruction set — currently **214 opcodes**, leaving 42 free values in the `byte` space. It is the authoritative reference; don't restate opcode semantics elsewhere, link to it.

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

Pool indices refer to the tables on the declaring module's `SurtrChunk`. Trap behaviour is now pinned down by the interpreter — see the validation policy in `docs/VM-Plan.md` §1.9 for what traps, what is defined, and what is deliberately unchecked.

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

What is erased is the *substitution*, not the generic. `docs/Compiler-Plan.md` §8 settles three separable levels: **arity is part of type identity**, mangled into the emitted name (`` Box`1 ``), so `Box<T>` and `Box<T, U>` are unrelated types; **type arguments live in the descriptor** after the name terminator (`` Obox:Box`1;I ``), so `Box<int>` and `Box<string>` are different descriptors resolving to the same `SurtrClass`, exactly as `AI` and `AS` both resolve to `SurtrBuiltIns.Array`; and **nothing is reified** — one class, one method table, one compiled body per declaration, so `Box<int>.get()` and `Box<string>.get()` are the same `SurtrMethodInfo`. Arity in the name is what lets the argument list need neither brackets nor a count: the reader knows how many descriptors follow before it reaches them, so parsing stays single-pass with one character of lookahead. `SurtrMethodInfo.SignatureKey()` keeps erasing, but it only rewrites `G<n>` — the declaring type's own parameters — and never a concrete argument, which is what keeps `SurtrTypeLinker` free of substitution while still letting `f(Box<int>)` and `f(Box<string>)` be real overloads. `SurtrModule`'s type keys and `SurtrTypeHandleTable`'s interning needed no change for any of it — a full name is an opaque string to both, and resolution stops at the terminator, which is exactly why two constructions land on one class. A **nested type does not see its container's parameters** (the static-nested rule), which is what makes the argument count the *last* name segment's arity rather than a sum.

`docs/VM-Plan.md` carries the full rationale for every decision above, the remaining gaps (array access pays two bounds checks), what the language spec obliges the runtime to add, and the ordered plan. Interface dispatch no longer scans: each class carries an open-addressed `interfaceId → index` table, and a context numbers its own interfaces starting at `SurtrBuiltIns.ReservedInterfaceIds` so a user contract can never collide with a built-in one.

## The bytecode emitter

`src/Surtr.Core/Bytecode/Emit/` is the only supported way to produce a chunk. It is **public** even though `SurtrChunk` stays `internal`, so a front end can live in its own assembly; the builders are the seam.

The shape of using it is always **declare → emit → `Build()` → `LoadModule`**, and that order is forced by the runtime rather than chosen. `SurtrBytecodeMethodInfo` snapshots `chunk.MethodOffsets[entryIndex]` *in its constructor*, so no method metadata can exist until every body in the module has been emitted and laid out — yet a call site has to name its target while emitting. The two are reconciled by handing out a method-table slot at declaration time (`SurtrMethodBuilder.Token`) and binding the real metadata into it in `Build()`. Everything else about the layering follows from that one constraint.

| Type | Owns |
|---|---|
| `SurtrModuleBuilder` | the constant pool, the four access tables, the declarations, and `Build()` |
| `SurtrClassBuilder` / `SurtrInterfaceBuilder` | one type's members; `SurtrPropertyBuilder` wires `get_x`/`set_x` |
| `SurtrMethodBuilder` | one signature, its frame slots and its protected regions |
| `SurtrCodeEmitter` | the instruction stream, labels, branches and stack tracking |
| `SurtrBytecodeDisassembler` | renders a built module, for tests and for debugging an emitter |

`src/Surtr.Core/Bytecode/Image/` turns a built module into bytes and back. `SurtrModuleImage` is the portable artefact — what a compiler writes to disk, and what makes one compiled module loadable into **as many runtimes as you like**: `image.Instantiate()` hands each runtime its own `SurtrModule`, because loading is what ties one to a heap, a global table and a set of static storage. Everything naming something outside the module — the module reference table, and any access-table entry pointing at another type's member — travels as text and is bound in `LoadModule` next to the type handles, through `SurtrPendingMember`.

**A native member travels as a name too**, so a module written entirely by the host, or mixing compiled Surtr with C# (`Language-Syntax.md` §13.1's standard library), is an image like any other. `SurtrNativeMethodInfo.LinkName` is what the image carries — derived from the owner and signature (`host:Facade.answer()`) unless declared — and each runtime publishes its own body with `SurtrRuntime.DefineNativeBody`. Declare one with `DeclareNativeMethod`/`DeclareNativeGetter` for a module meant to travel, or `DefineNativeMethod` with an entry point for one built and loaded in the same process. A name nothing published fails the load; an unbound method points at a body that says so, rather than at null. Native *properties* need no separate mechanism — a property is already a pair of `get_x`/`set_x` methods. What still cannot travel is the built-in module (process-wide; a copy would shadow it) and a module-level member of another module (nothing records which module owns one).

`SurtrCodeEmitter` has three tiers, and a compiler should live in the third:

1. **Raw** — `Emit(OpCode, pop, push)`, `EmitU8`/`EmitI16`/`EmitI32`. Writes what it is told, validates nothing.
2. **One method per opcode**, named after it (`Ldl0()`, `ArrPack(type, size)`, `JPZ(label)`), taking its exact operands. Deliberately literal: `JPZ` emits `JPZ` and *fails* if its target is out of reach rather than quietly becoming `JPZX`.
3. **Grouped helpers** that pick the encoding — `LoadLocal`, `LoadConstant`, `LoadInt`, `Add(typeCode)`, `Compare(comparison, typeCode)`, `JumpIfCompare`, `Call`, `SwitchOn`, `Convert`, `Box`. This is where "which of `Ldl0`…`Ldl5`/`LdlS`/`Ldl`" is decided once instead of at every emit site.

Three things the emitter computes rather than asks for, each because getting it wrong by hand is unrecoverable:

- **`LocalCount` and `MaxStackSize`.** Every tier-two method declares its own pop/push. This pair is the *only* stack-overflow check the interpreter makes, so a hand-supplied wrong value corrupts the stack with nothing to catch it.
- **Stack agreement at labels.** Every path into a label must arrive at the same depth; a mismatch throws at the instruction that caused it. Handler labels are marked with `MarkHandler`, which sets depth 1 — the unwinder clears the frame's stack and pushes the raised object.
- **Branch width.** `SurtrJumpWidth.Auto` starts short and widens what does not reach, re-running to a fixed point because widening one branch moves everything after it. Only `Auto` relaxes: a pinned short branch fails instead. This is also why a protected region records its bounds as *labels* — `SurtrExceptionHandler` offsets are chunk-absolute and are only resolvable after relaxation and after the bodies are concatenated.

Two invariants worth not breaking: every method a builder declares goes into its own module's method table whether or not anything local calls it (a cross-module call reads the *callee's* table), and the pools deduplicate — the low indices have dedicated single-byte opcodes behind them, so duplicates would push real entries out of that range.

`Call(SurtrMethodInfo)` infers its dispatch opcode except for one case: a declaring type is carried as a descriptor, and a descriptor does not say whether it names a class or a contract until load. Methods declared through `SurtrInterfaceBuilder` are recognised because the module builder remembers them; anything else needs `CallInterface`.

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

Inside `src/Surtr.Core`: `Bytecode/` is the instruction set and `Bytecode/Emit/` the emitter, `Runtime/Classes/` the type metadata and linker, `Runtime/Objects/` the runtime values and the entity registry, `Runtime/BuiltIns/` the shared built-in classes and their native members, `Runtime/Utilities/` the unmanaged helpers, `VM/` the interpreter.

The instruction set, the metadata layer, the object registry, the runtime object model, the built-in classes, the standard library, the interpreter and the emitter exist. The compiler lexes, parses, binds and — for the const-evaluable subset — **emits and runs**, which is how a `const fun` folds. What it does not do yet is turn a whole *module* into calls on `SurtrModuleBuilder`: a module with types, fields and properties in it is still assembled by hand, just against the emitter rather than against raw bytes.

**Everything `docs/VM-Plan.md` §4 asked the runtime for is implemented**: parameter defaults and varargs, `sealed`, enums with per-case ordinals, generic parameters on the built-ins, nullable primitives, `range`, the standard library with its exception hierarchy and the trap-to-class mapping, per-module native imports bound by name at load, class-naming boxing for value classes, attributes as real classes, and an instruction budget. What remains is the compiler side — §4.8 of that document is entirely owed.

**`docs/Language-Syntax.md` is the specification `src/Surtr.Compiler` implements** — the complete surface syntax, with the reasoning behind each choice and the runtime obligations each one creates. Read it before touching the compiler; §1.2 is the authoritative reserved word list and §5.7 the authoritative operator table. Surtr source files use the `.surtr` extension.

The language has three compile-time-only constructs worth knowing about before reading anything else, because they mean source and runtime do not correspond one-to-one: **type aliases** (§2.7) erase to their target's descriptor, **`inline`/`forceinline`** (§3.6) splice a body into a call site, and **`const`/`const fun`/`const if`** (§7) move work to compile time — including conditional compilation, with no preprocessor. The last of those is folded by *running the emitted bytecode on the real VM* rather than by a second evaluator in the compiler, so compile-time and runtime semantics cannot drift; `docs/VM-Plan.md` §4.7 covers what that costs.

Three absences are deliberate and worth not re-proposing. There is **no `static class`** — a module already is a container of members with no instance (§2.5), so `singleton` (§2.8) exists only for the thing modules genuinely cannot do: implement an interface and be passed as a value. There is **no `any`** — `unknown` (§5.10) holds anything but must be cast before use, and is `SurtrValueTypeCode.Erased` with a surface name, so it costs the runtime nothing. And there are **no user-defined implicit conversions** — `operator as` (§5.6) is explicit-only, because overload resolution already has `int` → `float` as its hard case.

Two type-shaped things are erased but not equivalent: a **type alias** (§2.7) is transparent, so `EntityId` and `int` are interchangeable, while a **`value class`** (§2.9) wraps one field and *is* a distinct type to the compiler, erased to that field at runtime — free where its type is statically known, boxed where it flows into an erased or interface-typed slot. `value` stays a contextual keyword: it is the `class` after it that makes the declaration.

What exists in `src/Surtr.Compiler` today: `Syntax/` holds the source buffer, the character reader, the token model, **the lexer**, **the AST** (`Syntax/Ast/`) and **the parser** (`Parser.*.cs`, partial by what each file parses); `Diagnostics/` holds the spans, codes and bag described above. All of it is complete against the spec and covered by `src/Surtr.Tests/Compiler/Syntax`, including `Sample.surtr` — a file exercising every construct in the language, lexed and parsed end to end. `Binding/Symbols/` holds the compiler's own type and symbol model, `CodeGen/DescriptorEmitter.cs` the single gate from it to the runtime's encoding, and `Compilation/` everything that has to be settled before binding — deriving each file's module from where it lives, ordering the modules, and importing referenced metadata through `Binding/MetadataImporter.cs`. `Binding/Binder.cs` runs the first two of the binder's three phases: **declaration** (a symbol per declared type and alias, no signature looked at) and **hierarchy and members** (base types, interfaces and every member signature, resolved against the complete set). Bodies bind onto `Binding/BoundTree/`, and `CodeGen/MethodBodyEmitter.cs` turns one into bytecode — **for the const-evaluable subset**, which is as far as folding a `const fun` needs. **Emitting a whole module is what remains**: nothing yet declares types, fields and properties onto a `SurtrModuleBuilder` or writes an image.

**The binder's phases exist because a signature can name a type declared later**, in the same file or another file of the same module, so one pass cannot do it. After phase 2 a type's surface is fully known, which is exactly the state `MetadataImporter` produces for a module compiled earlier — a source type and an imported one are then interchangeable. `Scope` is a chain, innermost first, with **imports in a scope of their own** between a module's declarations and the built-ins: a local declaration shadows an imported name, while two wildcard imports still collide, and that collision is reported *at the use* as §2.1 requires. A scope holds several candidates per name because arity is part of identity, so `Result<T>` and `Result<T, E>` both answer to `Result` and the argument count picks. `TypeResolver` never returns null — an unresolved name yields the error type and reports once — and reads a dotted name as a nested type before a fully qualified one, since §2.6 makes `.` the qualifier at every level.

**A body binds onto a bound tree that settles five things downstream never has to.** `Binding/BoundTree/` holds the nodes and `BodyBinder` walks a body onto them, run by `Binder.BindBodies()` separately from `Bind()` — phases 1 and 2 answer what every type *is*, which is all a tool needs for metadata, and one body cannot affect another. In the tree: **every conversion is a node**, written or not, so an `int` argument reaching a `float` parameter carries its widening; **arguments arrive in parameter order**, named ones reordered and varargs collected; **a compound assignment is expanded** (`x += 1` becomes `x = x + 1`), so nothing needs a second table of operators; **devirtualisation is decided** — a `super` call or one on a `sealed` receiver is marked non-virtual, §2.2's static fact; and **`for-in` is deliberately not lowered**, since whether a sequence walks by index or through `iterate()` is a codegen decision. A lambda captures only what is never reassigned, checked at the capture site because a capture is copied rather than shared.

**Flow questions run after binding, on the bound tree, because that is the form with a whole body in it.** `FlowAnalysis` asks what can be reached, whether a local is assigned everywhere it is read, and whether a method can finish without returning — walking the tree and joining an `if`'s branches rather than solving a fixed point over a control-flow graph, so it is exact for straight-line code and conservative in a loop. **Nullability narrowing is in the binder instead**, because it changes what an expression *is* rather than what can happen: `x != null`, `x is T` and the `&&` of two such, inside the branch they guard and nowhere else. **Generic constraints** get two passes of their own — bound after the hierarchy, since `<T : IComparable<T>>` names a type still being resolved, and checked against the *substituted* bound, so `Sorter<Vec2>` asks about `IComparable<Vec2>`. **Switch exhaustiveness** covers the expression form over an enum only, since only an enum's cases are fixed at its declaration. `ConstantEvaluator` folds over *syntax* rather than bound nodes, because a declaration-level `const if` decides which declarations exist and must be answered before any type has members; the untaken branch is never bound, which is what lets it name types this build lacks.

**A `const fun` is folded by running its real bytecode on a real VM** (§7.2), not by a second evaluator — two of them would have to agree about integer overflow, string equality and every trap in `docs/VM-Plan.md` §1.9, and would silently diverge the first time they didn't. `CodeGen/ConstFolder` is the one place the compiler loads a runtime: it emits every const function into a **single scratch module**, so a call between two of them is an ordinary `CallLocalModule`, then invokes one on demand under `SurtrRuntime.InstructionBudget` — re-armed before every run, because exceeding it leaves the budget *exhausted* rather than cleared, and followed by `ResetExecution` so one failure does not poison the next. Emission runs to a **fixed point**: a body the emitter cannot lower is dropped, which makes every body that *calls* it fail to emit too, so a caller of a dropped function is itself dropped rather than left pointing at a stub. A compilation declaring no `const fun` builds no runtime at all.

**The ordering is the whole difficulty, and `BindBodies` settles it by binding in two rounds** — every `const fun` first, then the folder is built, then everything else. So a `const` initializer and a statement-level `const if` can call one, and a **declaration-level** `const if` cannot: it is answered in the declaration phase, before any signature exists, and says so rather than guessing. `Binding/ConstFunctionCheck` reports §7.2's restrictions against the *declaration* — not `virtual` or `abstract`, not `native`, no receiver, no write to a field or property, and no call to anything but another const function or the standard library. "Not `native`" means a **host** function (§10): §7.2's own example writes `table.push(...)`, and the built-in bodies are process-wide, so they are reachable. `MethodSymbol.ImportedFrom` — the one thing an imported symbol keeps of where it came from — is what lets a call site name one.

**The rules a body is checked against.** `Conversions` classifies how one type reaches another — the implicit set is small and every part follows from a decision taken elsewhere (`int` → `float` is the only implicit widening, generics and arrays are invariant since §6 has no variance, a `value class` reaches nothing, anything reaches an erased slot and nothing returns without a cast). `MemberLookup` walks bases then interfaces and builds the substituted view of a constructed generic's members, so `Box<int>.get()` reads as returning `int` though one symbol exists. `OverloadResolution` does §3.5's rules 2–4 — rule 1 belongs to a declaration and is `SignatureSet`'s job — deciding specificity **per argument** rather than by a score, and re-checking the winner against every candidate rather than only the one it beat.

**`SignatureSet` compares emitted signatures, not written ones**, which is what catches overloads that would collide in a real method table with nothing left to diagnose them: a type parameter erases (Java's "same erasure"), a *reference's* nullability is not in the descriptor, and a `value class` erases to the field it wraps. A nullable primitive stays distinct; an alias needs no rule, since §2.7 already resolves it to its target; and `operator as` goes the other way, putting its target in the key because a signature key excludes the return. `docs/Compiler-Plan.md` is the ordered plan for the rest, and the place open ABI decisions get settled before they reach disk.

**The binder has its own types, and a descriptor is an output.** `TypeSymbol` is not a `SurtrClassReference`, because a descriptor is canonical precisely by discarding what the type checker needs: reference nullability, which alias was written, that a `value class` was involved, and which type arguments a generic was given. `int?`/`int`, `Box<int>`/`Box<string>` and `EntityId`/`int` all have to stay apart while binding and all collapse at emit — `DescriptorEmitterTests.TypesTheBinderKeepsApartCanStillShareADescriptor` pins exactly that. The emitter lives in `CodeGen/` rather than `Binding/` so that reaching for a descriptor too early reads as the layering violation it is.

**A descriptor is written in exactly one place and read in exactly one other.** `DescriptorEmitter` (in `CodeGen/`) is the way out; `MetadataImporter` (in `Binding/`) is the way in, because metadata is the form a dependency arrives in and something has to decode it. Confining both to one type each is what keeps everything between them working in symbols. What cannot come back in is precisely what the emitter drops — a nullable reference, a `value class`, an alias — while a nullable primitive's `?`, a type parameter's position and a constructed generic's arguments all survive, which is the whole reason the descriptor was given room for them.

**A module is a directory, not a declaration** (`Language-Syntax.md` §2.1). `Compilation/ModulePath.cs` derives one from a file's location relative to the project's source root, and rejects a directory whose name is not a legal identifier — no `import` could name the module it would create. `ModuleDependencyGraph` accumulates edges rather than computing them once, because imports are known at parse time but a fully-qualified name with no import is an edge only the binder discovers; a cycle is a hard error naming the whole loop, since static initializers run eagerly at load in dependency order and a cycle has no order to pick.

**Two naming conventions are ABI and are already fixed**, because a name goes into a real table and travels in the image. An overloaded operator is its own symbol behind `op_` (`op_+`, `op_<=>`, `op_[]`, `op_-u` for the unary form) — unspellable in source, so nothing has to be reserved to protect it; `operator as` is `op_as` in the binder and gains its target's descriptor only at emit (`op_as$Ogame.core:Vec3;`), since a signature key excludes the return and two conversions from one source type would otherwise collide. A synthetic member is `$category$context[$index]` — `$lambda$move$0`, `$backing$health`, `$bridge$compareTo$0` — one rule, a leading `$` means the compiler made it. Property accessors are deliberately *not* in that scheme: `get_x`/`set_x` are what `SurtrTypeLinker` looks for. See `OperatorNames` and `SyntheticNames`, and `docs/Compiler-Plan.md` §6 for the value-class boxing sites.

Three properties of the model are load-bearing. **Type identity is reference identity**: every type is interned by `TypeSymbolFactory`, so comparing two never walks a structure and a dictionary keyed on one needs no comparer — which is also why type constructors are internal, since a type built outside the factory would compare unequal to its own twin. **Nullability is a flag, not a wrapper**, so a nullable type still *is* its own kind of type and no `is NamedTypeSymbol` in the binder has to remember to unwrap; each type and its nullable twin are linked duals, created once. And **an alias is not a `TypeSymbol` at all** (§2.7 makes it transparent, so two members differing only by an alias are a duplicate rather than an overload) while a **`value class` is** (§2.9 makes it distinct, erasing at emit rather than at resolution) — the two look similar in source and are deliberately opposite in the model.

**Diagnostics are collected, not thrown.** `Syntax/SourceSpan.cs` gives every token and every AST node a start *and* an end, which is what lets a tool underline a construct rather than point at its first character; a node's span runs from its first token to its last, built by the parser's one `SpanFrom` helper. `Diagnostics/` holds `SurtrDiagnostic` (a stable `SurtrDiagnosticCode`, a severity, a message, a span), and `SurtrDiagnosticBag`, which the lexer and parser share so one bag holds everything wrong with a file. **`Parser.ParseCompilationUnit` does not throw on a syntax error** — check `Parser.Diagnostics`, or call `ThrowIfErrors()` for the simple behaviour.

Recovery happens at two boundaries, both chosen because resynchronising anywhere else is guesswork: a **declaration** that fails is skipped to the next `;` or introducer keyword at brace depth zero, and a **statement** that fails is skipped to the next `;` or statement keyword. The lexer recovers too, and skips a failed *literal* whole rather than a character — otherwise the closing quote of a bad string opens another one and the second complaint is caused by the first. Diagnostic codes are append-only within their group (1xxx lexical, 2xxx syntactic, 3xxx reserved for binding, 4xxx for code generation): a published code is a name someone may have written down. Assert on codes in tests, never on message text.

Four things about the front end that are not obvious from the code:

- **The lexer hands back `>>`, `>>>` and their `=` forms whole**, because maximal munch cannot know it is inside a type argument list. `TokenReader.ConsumeTypeArgumentClose` repays that, taking one `>` at a time and refusing to step over an unconsumed one. The `=`-suffixed shapes are rejected with a message asking for a space rather than synthesising a token the lexer never produced.
- **Type names and contextual keywords lex as `Identifier`.** `int`/`string`/`void`/`range`/`unknown` are ordinary identifiers per §1.1, and `this`/`super`/`value` per §3.2, so the parser recognises them by text. `as?` likewise arrives as `as` then `?`.
- **Three ambiguities are settled by lookahead, not grammar**: a lambda against a tuple (scan balanced parens for the `=>`), a block against a dict literal (§5.4 makes it positional), and a member's kind (§3.2's introducer keyword).
- **A production still aborts by throwing `SurtrParserException`** — that is control flow, not failure. The recovery points catch it, resynchronise and carry on, and the diagnostic it already reported joins whatever else the file has wrong. It only reaches a caller through `ThrowIfErrors()`, or from a narrower entry point with no boundary to recover at.

Runtime-side gaps are in `docs/VM-Plan.md` §3; what the language design newly obliges the runtime to grow is `docs/VM-Plan.md` §4, and §5 orders all of it into a build plan.

The VM opcode suites in `src/Surtr.Tests/VM` predate the emitter and still use their own `BytecodeBuilder`, which pokes at `SurtrChunk` directly. That is deliberate: an opcode test should exercise the byte layout it is testing, not whatever the emitter decided to emit. New tests that are *about* a whole module belong in `src/Surtr.Tests/Bytecode/Emit` and should go through `SurtrModuleBuilder`.

**The append-only rule in `OpCode.cs` is now in force.** `Bytecode/Image/` serializes bytecode, so the enum value of every existing member is on disk somewhere: inserting one in the middle renumbers everything after it and silently invalidates every image already written. New opcodes go at the end, whatever family they belong to, and `SurtrModuleImage.FormatVersion` covers changes to how a module is *framed* rather than to what runs inside it.

## Coding conventions

- Every `.cs` file starts with `#nullable enable`, even though `Nullable` is already `enable` at the project level via `Directory.Build.props`. This is intentional and non-negotiable — don't remove it as "redundant".
- No `ImplicitUsings` — write out every `using` directive explicitly in each file (see above).
- Any documentation of a type, method, property, or field must use `///` XML doc comments (`<summary>`, `<remarks>`, `<param>`, etc.), never a plain `//` block sitting above the declaration — that's what lets Visual Studio's IntelliSense pick it up. This is about *format*, not coverage: it doesn't mandate documenting every member. Plain `//` comments are still the right tool for a short, non-obvious implementation note *inside* a method body (a specific line or block), since those aren't documenting a declaration and `///` can't attach to arbitrary statements anyway.
