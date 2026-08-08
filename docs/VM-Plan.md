# The Surtr VM: decisions, gaps and remaining work

Companion to `src/Surtr.Core/VM/`. The *why* behind each choice lives in the XML docs and
comments next to the code; this file is the map — what was decided, what the surrounding
system still owes the interpreter, and in what order to close it.

---

## 1. Decisions

### 1.1 Dispatch: one switch, not a table of function pointers

**Chosen: a single `switch` over the opcode byte inside one large method**, with `ip`, `sp`,
the frame base and the current chunk's pools all held in locals, and every case ending in
`goto Dispatch`.

A `delegate*<…>[256]` table costs a real call per instruction. C# cannot turn that call into a
tail-jump, so every handler pays a prologue and epilogue, and — worse — the machine state has
to be spilled to memory across it and reloaded inside. The switch compiles to a bounds check
plus an indirect jump through a jump table, which is one predicted branch and nothing else.

C# cannot express *replicated* dispatch (a computed `goto` per opcode, the classic
direct-threading trick), because `goto case` needs a compile-time constant. One shared indirect
branch is therefore the ceiling in this language; under IL2CPP the switch becomes a C++ switch
that the backend turns into a jump table too.

`Run` is marked `[MethodImpl((MethodImplOptions)512)]` — `AggressiveOptimization`, which
netstandard2.1 does not name. It keeps a method of this size out of tiered compilation.

### 1.2 Two stacks, both fixed size

| | Storage | Why |
|---|---|---|
| Data stack | unmanaged `SurtrRawValue*` | Holds no managed references, and the collector scans it through a raw pointer. |
| Call stack | managed `SurtrCallFrame[]` | A frame holds its chunk, method and closure; the CLR has to keep those alive. |

**Neither grows.** A growable data stack would have to be addressed by index rather than by
pointer: a reallocation would dangle every `sp` already spilled in a *suspended* dispatch loop,
which is exactly what a re-entrant native call leaves behind.

### 1.3 The VM is internal; the runtime is the host surface

`SurtrVirtualMachine` is `internal`. A host holding one could push onto the data stack between
calls or start a run at an arbitrary frame, and every invariant the interpreter depends on would
become the host's to maintain. The public surface is `SurtrRuntime.Invoke`,
`SurtrRuntime.InvokeClosure` and `SurtrRuntime.ResetExecution` — each a complete operation that
pushes, runs and cleans up, so the stack is never left in a state the host has to reason about.

### 1.4 Arguments arrive on the stack, Lua-style

That is where a call already puts them. A bytecode call site leaves `a1…aN` on the stack, and the
callee's frame simply *starts underneath them* — the arguments become locals `0…N-1` in place, and
entering a call copies nothing. A native call wraps the same region directly. The span overloads on
`SurtrRuntime` are that copy, made explicit and confined to the host boundary.

### 1.5 The calling convention

```
   ... | arg0 | arg1 | … | argN-1 |          <- sp
       ^
       frame base == locals[0] == where results are written
```

* **`argsCount` counts every incoming slot, receiver included.** On an instance call the receiver
  *is* argument 0, which makes the frame base `sp - argsCount` for every kind of call — one
  subtraction, no branch. It is also what makes `Ldl0` read `this` and `arguments[0]` the receiver
  in a native entry point.
* **Locals above the arguments are zeroed** on entry, so a collection can never read a slot the
  program has not written and retain what the previous call left there.
* **The frame occupies `[base, base + LocalCount + MaxStackSize)`**, checked against the stack
  limit **once per call**. That is the only stack-overflow check in the interpreter.
* **Returns land at the frame base.** `retCount` (0 or 1) is recorded on the callee's frame.
  Several values are returned by packing a tuple.
* **`InvokeClosure`** is the one call whose target sits *below* its arguments; they are slid down
  over it rather than fixing the stack up on every return path. The closure stays rooted through
  the frame's entry in the machine's root array.

### 1.6 There is no separate opcode for calling host code

`InvokeNative`, `InvokeNativeX`, `InvokeStaticNative` and `InvokeStaticNativeX` were removed.

Where a call lands is a property of the `SurtrMethodInfo` the call site names, not of the call
site — and the interpreter has to read it anyway, because a **virtual call can resolve onto a
native override**. The `ImplKind` test therefore has to exist in the shared entry sequence no
matter what, so paying it for direct calls too costs one byte load and a branch that predicts
perfectly at any given call site, while removing four opcodes, four switch arms, and the
compiler's obligation to know where a body lives before it can pick an opcode.

`CallGlobalNative` stays, not because its target is native but because host globals live in a
different table (`Globals.FunctionTable`) — a different namespace, not a different body kind.

### 1.7 Re-entrancy

1. All machine state lives on the instance; the loop only *caches* it in locals.
2. `_sp` and the executing frame's `IP` are published before every transfer into host code, every
   allocation, and every trap.
3. `Run(entryDepth)` runs until the call depth falls back to where it started.

A nested run pushes its frames *above* the current one and returns at its own depth. Verified end
to end: a host global that re-enters the VM and returns through two levels.

### 1.8 Exceptions

**Handler tables, not handler opcodes.** `SurtrBytecodeMethodInfo.Handlers` holds
`SurtrExceptionHandler` entries — a protected code range, a handler offset, and a catch type (null
means catch-all). Entering a `try` emits nothing and costs nothing; only a raise pays, and it pays a
walk over a handful of entries. A push/pop-handler pair would charge every `try` the program ever
enters, almost all of which complete normally.

**Two speeds, one search.**

* A Surtr `Throw` never becomes a CLR exception while a handler is in reach: the machine walks its
  own frames and jumps to the handler. A caught exception costs a table scan, not the microseconds
  a CLR throw costs.
* A trap the VM raises, and anything host code throws, arrive as CLR exceptions. `Execute` catches
  them, wraps the object (a CLR exception becomes a `SurtrNativeProxy`, so `catch (native e)` sees
  it like anything else) and feeds it through the same search.
* Only an exception nothing catches leaves, as `SurtrThrownException` carrying the raised object's
  reference — which stays rooted until `ResetExecution`.

**Composition across native calls** falls out of stopping the search at `entryDepth`: a run started
from inside a native function unwinds only its own frames, then lets the exception leave as a CLR
one so it travels back out through that native frame and resumes the search in the run below.

**`finally` is the compiler's job**, exactly as it is for javac: emit the block on each normal exit
path, plus a catch-all handler that runs it and re-raises with `Throw`. That keeps the interpreter
free of a second unwinding mode, and keeps `Leave`/`EndFinally` out of the instruction set.

The `try` lives in `Execute`, wrapped around `Run`, rather than inside the dispatch loop — a
protected region spanning the loop would constrain how the JIT keeps `ip` and `sp` in registers, to
buy something the frame protocol already provides.

### 1.9 Validation policy

Not checked (static typing already paid for it): local/constant/pool indices, argument counts,
receiver nullness, the concrete type behind a reference, per-push stack room.

Trapped, each from a `NoInlining` cold helper: division by zero and `int.MinValue / -1`, negative
integer exponents, array/string/tuple index out of range, popping an empty array, `DictGet` on a
missing key, a failed `Cast`, stack and call-depth overflow, an invalid opcode byte.

Defined rather than trapped, because defining them is free: shift counts mask to `& 31`; `F2I`
saturates with NaN → 0 (deterministic across x64 and ARM, which an unchecked C# cast is not);
`FDiv` follows IEEE 754; a null receiver hits the CLR's own null check.

### 1.10 A reference is its 32-bit payload

`IsNull`, `REQ`, `JPN` and friends compare the low 32 bits and ignore the tag, so a zeroed slot and
an explicitly tagged null are the *same* reference. That is what lets fresh locals read as null
without the VM knowing their declared type. The collector still ignores an untagged zero, because it
names nothing.

Where the tag *does* matter — a value handed to a native function, or boxed — `ArrNew` fills a fresh
array with its element family's correctly tagged zero. Floats and references need no fill at all:
`0.0` is all-zero bits, and an untagged zero already reads as null.

### 1.11 Generics are erased, and erasure needs a name

Surtr's generics are checked by the compiler and discarded, as Java's are. That leaves exactly one
thing the runtime must still answer: what a field or parameter declared `T` looks like in memory,
since instance layout and the reference-slot map are built from declared types.

The answer is `SurtrValueTypeCode.Erased`, descriptor symbol **`E`**, resolving to
`SurtrBuiltIns.Erased`. It sits inside the reference-type range, so an erased slot is always a
reference, always traced, and `IsReferenceType` stays a range compare. Two different type parameters
of the same class erase to the same descriptor, exactly as on the JVM.

What that costs the compiler, and what it must therefore emit:

* **box primitives** flowing into an erased slot, and
* **insert a `Cast`** when reading one back out.

No opcode, no metadata and no dispatch path needs to know a generic existed. `Array`, `Dictionary`,
`Tuple` and `Closure` were already erased at the class level — one shared class per family — so
nothing there changes.

### 1.12 Static initializers run at load

Eagerly, when their module is loaded: each class's first (declaration order, nested types
included), then the module's own. Lazy initialization is what Java does, and it buys
initialization-order independence at the price of a "has this run yet" test on every static access
and every static call, forever, to answer a question that is false exactly once. Loading a module is
a controlled event in an embedded language, so the cost belongs there.

The price is ordering: an initializer that reads another class's statics only sees them if that
class was declared first. Cross-initializer dependencies are the compiler's to reject.

Because nothing has to be triggered at a call site, **`InvokeStatic` no longer carries a type
index** — it is now `methodIdx(2) argsCount(1) retCount(1)`, two bytes shorter, and `InvokeStaticX`
widens the method index rather than the type index.

### 1.13 Switch has its own opcodes

`Switch` is a dense jump table (`low`, `count`, default, then one offset per case): one bounds check
and one indexed load whatever the number of cases. `SwitchLookup` is the sparse form — sorted keys,
binary-searched — for when a dense table would be mostly padding. Both measure their offsets from
their **own opcode byte**, unlike every other branch, because a variable-length instruction has no
fixed "next address" to measure from at emit time.

---

## 2. What is implemented

All **202 opcodes** execute; 54 byte values remain free. Verified by a throwaway harness covering:
integer and float arithmetic, loops with backward jumps, intra-module calls, typed array
allocation and element ops, dictionaries, strings and interned literals, closures with upvalues,
module-level variables and their static initializer, both switch forms, tag conversions, catching a
VM trap, catching a Surtr `throw`, catching one raised across a call boundary, an uncaught throw
arriving at the host with its object intact, native calls re-entering the VM, and a collection with
a live stack that spares module statics and literals.

Changes made outside `VM/` to get there:

* `SurtrChunk.ModuleTable` — `CallModule`'s `moduleIdx` had nothing to index.
* `SurtrChunk.StringConstantSlots` + `SurtrRuntime.MaterializeStringConstants` — literals become
  interned entities at load and are patched into the constant pool, so `Ldc` stays one load.
* `SurtrTypeLinker.LinkModuleMembers` — module fields and methods are laid out, given slots, and
  frozen. Previously they were never linked at all.
* `SurtrModule.StaticStorage` / `SurtrClass.ReferenceStaticSlots` / `SurtrStaticBlock` — static
  storage is unmanaged and reachable from no object, so a collection now walks it explicitly.
  Without this, anything a static field solely owned was swept.
* `SurtrFieldInfo.StaticAddress` — the linker resolves each static's slot address, so
  `StaticFieldGet` is one indirect load with no class-versus-module test.
* `SurtrExceptionHandler`, `SurtrBytecodeMethodInfo.SetExceptionHandlers`.
* `SurtrValueTypeCode.Erased`, descriptor `E`, `SurtrBuiltIns.Erased`.
* Interface method slots are numbered into their (otherwise unused) `VTableSlot` at link time.
* `SurtrArray.InitializeLength`.

---

## 3. Known gaps

### 3.1 `InvokeInterface` does a linear scan

Resolving the receiver's index for a contract goes through `SurtrClass.IndexOfInterface`, a
reference-comparing scan of `Interfaces`. `k` is small in practice, but it is on a hot path.
Options, cheapest first: scan an `int[]` of interface *ids*; give each class a direct map from
interface id to index; or append an opcode carrying the interface index, which the compiler knows
statically.

It also depends on `SurtrMethodInfo.DeclaringType` naming the declaring *interface* for an interface
method. The compiler must honour that.

### 3.2 Array element access pays two bounds checks

The explicit trap check, plus the CLR's own on the managed buffer, which the JIT cannot elide
because it compares against `Items.Length` rather than `Count`. Removing the second needs
`Unsafe.Add`, which netstandard2.1 does not carry without a NuGet package a Unity host would also
have to ship. Revisit if the target framework moves.

### 3.3 A module belongs to one runtime

String literals are patched with references from the heap that loaded them, so loading the same
`SurtrModule` instance into two runtimes would corrupt the second. `LoadModule` does not reject it
yet.

### 3.4 Static initializer ordering is declaration order

See §1.12. The compiler has to reject cross-initializer dependencies; nothing detects them today.

---

## 4. Remaining work, in order

**Phase 1 — a real test project.** The harness that validated all of this was a throwaway. Its
cases are the right starting set. Add `src/Surtr.Tests` and record the command in `CLAUDE.md`.

**Phase 2 — close §3.3 and §3.4** with explicit checks at load, which are cheap and turn two silent
corruptions into clear failures.

**Phase 3 — measure, then optimise.** Not before. Candidates: §3.1, §3.2, and an inline cache on
`InvokeVirtual` if profiling shows monomorphic call sites dominating. All local changes; none
disturbs the frame protocol.

**Phase 4 — diagnostics.** Frames already carry enough for a stack trace (method, chunk, saved
`IP`), and the handler search already walks them. `SurtrThrownException` does not yet include one.
Cheap to add, and it costs nothing until something raises.
