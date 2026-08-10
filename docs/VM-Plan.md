# The Surtr VM: decisions, gaps and remaining work

Companion to `src/Surtr.Core/VM/`. The *why* behind each choice lives in the XML docs and
comments next to the code; this file is the map — what was decided, what the surrounding
system still owes the interpreter, and in what order to close it.

`docs/Language-Syntax.md` is the companion from the other direction: it specifies the surface
language, and §4 below collects everything that specification obliges the runtime to grow.

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

**Each trap names the library class it surfaces as**, on the exception it raises, so a Surtr
`catch` clause can name what the runtime raises rather than only what Surtr code threw. The pairing
lives beside the condition rather than at the catch site — see §4.2. One trap is deliberately
*not* mapped: exceeding the instruction budget leaves as a `SurtrBudgetExceededException`, which
the handler search never sees, because a program that could catch its own watchdog would give back
the only thing the budget promises.

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

All **214 opcodes** execute; 42 byte values remain free. The twelve added since are the nullable
primitive family (`PushAbsent`, `IsAbsent`, `IsPresent`, `JPA`/`JPAX`, `JPNA`/`JPNAX`), the boxing
pair that names a class (`BoxAs`, `BoxAsX`), range construction (`RangeNew`,
`RangeNewInclusive`), and `StrHash`. All were appended rather than filed next to their families,
because the enum value is the on-disk encoding. Verified by a throwaway harness covering:
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

Since then, two things have been built on top and are described in `CLAUDE.md` rather than here:
**`src/Surtr.Core/Bytecode/Emit/`**, the public builder API that is now the only supported way to
produce a chunk, and **`src/Surtr.Tests`**, which replaced the throwaway harness (this was Phase 1
of §5 below, and is done).

---

## 3. Known gaps

### 3.1 `InvokeInterface` does a linear scan — closed

Resolving the receiver's index for a contract went through `SurtrClass.IndexOfInterface`, a
reference-comparing scan of `Interfaces`, on a hot path.

**Closed with the second of the three options this section used to list**: each class now carries
`InterfaceIndexById`, an open-addressed table of `(interfaceId, index)` pairs in unmanaged memory,
sized to at least twice its interface count and rounded to a power of two. Resolving a contract is
a mask, a load and a compare, and the pairs are interleaved so a hit reads both halves off one
cache line. The interpreter writes the probe out by hand rather than calling `IndexOfInterface`,
which would be a real call from a method that size.

The third option — an opcode carrying the interface index — was rejected on inspection rather than
on cost: the *contract* is static at a call site, but the index is a property of the **receiver's**
class, and not knowing the receiver's class is the entire point of interface dispatch.

**Keying on the id turned a latent defect into a live one, which is how it was found.** Interface
ids are dense and were handed out from zero twice: once when the built-in module is linked, and
again by each `SurtrContext`. Reference comparison did not care; an id-keyed table does, and a
class implementing `IIterable` alongside its own first interface would have resolved one through
the other's block. A context now starts its numbering at `SurtrBuiltIns.ReservedInterfaceIds`.

This section still depends on `SurtrMethodInfo.DeclaringType` naming the declaring *interface* for
an interface method. The compiler must honour that.

### 3.2 Array element access pays two bounds checks

The explicit trap check, plus the CLR's own on the managed buffer, which the JIT cannot elide
because it compares against `Items.Length` rather than `Count`. Removing the second needs
`Unsafe.Add`, which netstandard2.1 does not carry without a NuGet package a Unity host would also
have to ship. Revisit if the target framework moves.

### 3.3 A module belongs to one runtime — closed twice over

String literals are patched with references from the heap that loaded them, native imports are
bound to that runtime's global table, and every class gets static storage the collector traces
through that runtime's registry — so loading the same `SurtrModule` instance into two runtimes
would corrupt the second. `LoadModule` rejects it.

What made this worth closing alongside §4.14 is that the two are the same defect from different
directions — one is state from the loading runtime baked into the module, the other was the
module's instructions carrying indices that only meant anything in one runtime.

**The restriction is now lifted at the level that mattered, by moving it.** `SurtrModuleImage`
(`Bytecode/Image/`) is a compiled module as bytes, and it can be instantiated any number of times:

```csharp
var image = SurtrModuleImage.FromModule(builder.Build());
first.LoadModule(image);
second.LoadModule(image);
```

The alternative was to split every piece of per-runtime state out of `SurtrModule` and reach it
through an indirection. That would have put a test on the hot path of every static access — one
indirect load is what §"the instruction set" buys with `SurtrFieldInfo.StaticAddress` — to answer a
question a second `SurtrModule` answers for nothing. Sharing the *bytes* rather than the object is
what the JVM and the CLR do for the same reason, and it is also the only form a compiler can write
to disk, which §4.8's build model needs anyway.

**A native member travels as a name.** A host writes modules too — some entirely native, some
mixing compiled Surtr bodies with host ones, which is the shape `Language-Syntax.md` §13.1 gives the
standard library. An address cannot travel and a name can, so a native method carries a
`LinkName` and each runtime publishes its own body under it:

```csharp
facade.DeclareNativeMethod("answer", SurtrClassReference.Integer, "host:Facade.answer()");
…
runtime.DefineNativeBody("host:Facade.answer()", SurtrNativeEntryPoint.FromFunctionPointer(&Answer));
```

A link name is derived from the owner and the signature (`host:Facade.answer()`) when the
declaration does not give one, so a host that never ships an image pays nothing for it. A name
nothing was published under fails the load, beside an unresolved type and an unregistered host
global, and for the same reason. Native *properties* need no separate mechanism — a property is
already a pair of `get_x`/`set_x` methods.

An unbound method points at a body that **reports the mistake** rather than at null. That costs
nothing — the interpreter makes the same indirect call — and the alternative, testing validity per
native call, would put a branch on the hot path to catch what `LoadModule` already refuses to let
through. The difference is between an exception naming the problem and an access violation taking
the process with it, which is what the regression test for this actually did before the trap
existed.

Two things still do not travel, both stated rather than worked around:

* **The built-in module**, which is process-wide and shared by every runtime. A copy read back from
  an image would shadow the real one rather than extend it.
* **A module-level member of *another* module** named in the field or method access table. Nothing
  on a module-level member records which module declares it, so an image cannot name one. Ordinary
  cross-module calls are unaffected — those go through the module reference table by path, which is
  resolved per runtime at load.

### 3.4 Static initializer ordering is declaration order

See §1.12. The compiler has to reject cross-initializer dependencies; nothing detects them today.

---

## 4. What the language syntax commits the runtime to

`docs/Language-Syntax.md` specifies the surface language, and several of its sections lean on
runtime capabilities that do not exist. This is that list, ordered by how much of the rest depends
on it. With one exception these are new obligations the language design took on rather than defects
in what was built — the exception is §4.14, which is §3.3's problem reached from another direction.

> **All of §4 is now implemented**, along with §3.3. What each section describes is what was built,
> so read them as the rationale behind the code rather than as work outstanding. Four things landed
> differently from how they were first written down, and each is noted in place: §4.1 turned out to
> be largely built already; §4.6 got real generic parameters on the built-ins rather than an
> erased placeholder; §4.7's budget is charged on control transfers rather than per instruction;
> and §4.15's attributes are real classes rather than name/value pairs. The one thing §4 did *not*
> settle, and still has not, is the compiler side — every obligation in §4.8 is still owed.

### 4.1 Member tables keyed by signature — mostly already built

`Language-Syntax.md` §3.5 allows method overloading, and this section used to record that as the
largest single metadata change the language asks for. Re-checked against the code, most of it is
already there:

* The three method tables are **overload groups**, not one method per name —
  `Dictionary<string, SurtrMethodInfo[]>` on `SurtrClass`, `SurtrInterface` and `SurtrModule`.
* `SurtrTypeLinker` already places vtable slots and matches `override` by **name plus parameter
  types**, through its private `SignatureKey`, which deliberately excludes the return type so a
  narrower return can be allowed later without moving a slot.

Two things remain, both small and both worth doing before the type checker leans on them:

* **`AddMethod` accepts a duplicate signature.** It appends to the overload group without
  comparing, so two members with identical parameter lists link into two vtable slots — the linker
  only notices when one of them is marked `override`. §3.5's rule 1 has to be enforced where the
  member is declared.
* **There are two different signature keys in the codebase.** `SurtrMethodInfo.ToSignature()`
  builds a closure descriptor that *includes* the return type; the linker's `SignatureKey` excludes
  it. They answer different questions and neither is wrong, but nothing says so at either site, and
  keying a member table off the wrong one silently admits an illegal overload pair.

Nothing on the execution path is affected — a call site still names a resolved
`SurtrMethodInfo`, and the runtime tables it reaches are index-keyed, not name-keyed.

### 4.2 There is no standard library

`SurtrBuiltIns` stops at the primitives and collections. The language assumes considerably more
(`Language-Syntax.md` §12), none of which exists:

| Needed | Wanted by |
|---|---|
| `Exception` + its hierarchy, with `message` | `throw`/`catch` (§8) — the language has *no* legal throwable today |
| `IIterable<T>` / `IIterator<T>` | `for-in` (§4.2) |
| `IComparable<T>` / `IEquatable<T>` | generic constraints (§6), `operator<=>` (§5.6) |
| `Math` and friends | ordinary use; also §5.7, which has no `**` operator by design |

The library lives in the `surtr` module — the same one `SurtrBuiltIns` already builds into — and
splits between C# and Surtr source on one rule: native if it needs `unsafe`, a raw pointer or a VM
service; Surtr otherwise.

**The trap-to-exception mapping is the part that couples back to this document.** §1.9 fixes what
traps; each of those conditions needs a class to surface as, and that pairing has to be decided
against §1.9's list rather than independently of it.

**And that mapping is not just a table to choose.** Today every trap, and everything host code
throws, is wrapped by `SurtrRuntime.WrapNative` into a `SurtrNativeProxy` whose class is
`SurtrBuiltIns.NativeObject` (§1.8). `TryEnterHandler` then tests that class with `IsSubclassOf` /
`Implements`, so once `Exception` exists, **`catch (e: Exception)` still will not catch a single VM
trap** — only a catch-all with no declared type will, which is not what `Language-Syntax.md` §13.3
describes. Closing it means the wrap site has to name a real Surtr class per trap condition: a
change in `Execute`'s catch clauses and in the cold trap helpers, not only a new set of classes in
the library.

### 4.3 Nullable primitives need a reserved tag

`Language-Syntax.md` §5.1 makes `int?`, `bool?`, `char?` and `float?` first-class *without*
boxing — a null primitive is a plain value-stack slot, not a `SurtrBoxed` on the heap, because
paying an allocation and an entity id to represent absence is exactly the per-value cost the
performance rules forbid.

The encoding has room: `SurtrValue`'s 16-bit tag claims 5 of 16 nibbles, so one more reserved tag
covers it. The work is (a) claiming that tag, (b) updating `IsFloat`, whose current definition is
"tag outside `[TagMaskInt, TagMaskReference]`" and would otherwise swallow it, (c) null-check and
coalesce opcodes, and (d) `SurtrClassReference` plumbing for the new value-type family.

Tracing needs no change: `SurtrEntityRegistry.Mark` and every `VisitReferences` walk test for the
*exact* reference tag, so a distinct tag is inert to the collector for free — the same reasoning
as §1.10.

### 4.4 `range` needs a descriptor symbol and a built-in class

`Language-Syntax.md` §5.4 makes ranges first-class values (`let r = 0..10;`), so `range` needs a
symbol — `R` is free — and a class. Both bounds are `int` and it is not parameterised, so it is a
bare symbol like the primitives, not a nesting form like `A`/`D`/`T`/`L`.

The compiler owes the other half: `for (i in <lo>..<hi>)` written inline in a loop header must
lower to a counted loop over two `int`s with **no range object allocated**. Only a range that
escapes into a variable, parameter or return is materialised.

### 4.5 `for-in` lowering

The general path goes through `IIterable<T>` (§4.2 above), which means an interface call per
iteration on top of an iterator object per loop. That is unacceptable per iteration, so
`Language-Syntax.md` §4.2 requires the compiler to emit a direct indexed loop, with no interface
call and no iterator object, for the shapes it can prove: an inline range, a statically-known
`array`/`tuple`/`dict`, and any `sealed` type.

Closing §3.1 made the general path cheaper and did not remove the need for the special cases. The
built-ins now genuinely implement `IIterable<T>` — one shared `iterator` class walks all five, and
a dict yields `(K, V)` pairs over a snapshot of its keys — so the contract holds for an `int[]` as
much as for a user collection, while a compiled loop over one still touches none of it.

### 4.6 The element-polymorphic built-in members — closed with real generic parameters

`CLAUDE.md` already names this as a known gap and it is now blocking surface syntax: `push`, `pop`,
`get`, `set`, `indexOf`, `keys` and the rest have no expressible signature, because a descriptor
names one concrete type and there is no way to write "the element type of whatever this array is".

The behaviour exists as ordinary methods on `SurtrArray`/`SurtrDictionary`/`SurtrTuple` for the
interpreter to call from `ArrGet`, `DictSet` and friends. What was missing was a descriptor form for
a built-in's *own* type parameter.

**Resolved by giving the built-ins real generic parameters** rather than by reusing the erased
descriptor. `SurtrTypeInfo` carries a parameter list, `array` declares `T` and `dict` declares
`K`/`V`, and the descriptor `G0`/`G1` names the declaring type's n-th parameter — one symbol plus
one digit, so it parses in the same single pass with one character of lookahead as everything else.
It resolves to `SurtrBuiltIns.Erased`, so the runtime representation is exactly what `E` would have
been and no layout, no tracing and no dispatch path changed; what it adds is the one thing `E`
throws away, *which* parameter it is, and that is what lets `int[].push("x")` be rejected against
metadata alone.

`push`, `pop`, `get`, `set`, `insert`, `indexOf`, `contains`, `remove` on `array`, and `get`, `set`,
`containsKey`, `remove`, `keys`, `values` on `dict`, are declared against them. `length` is now the
uniform spelling on all four collections — `dict` used to answer `count`. `tuple` and `closure`
declare no parameters and keep the thin surface: both are parameterised by a *list* whose length
varies per value, and a tuple's element type varies per index, so no fixed parameter could name what
`get(index)` returns.

### 4.7 The compiler runs the VM at compile time

`Language-Syntax.md` §7 adds `const`, `const fun` and `const if`. The decision that matters here is
how a `const fun` is folded: **by emitting its bytecode and executing it on this interpreter**,
rather than by writing a second constant-folding evaluator inside the compiler. Two evaluators
would have to agree about integer overflow, string equality, and every entry in §1.9's trap
policy — and would silently diverge the first time one of them didn't.

What that asks of the runtime is small, because the pieces already exist:

* **An instruction budget on a run.** A `const fun` may loop, so it may loop forever, and a
  compiler that hangs is not acceptable. `SurtrRuntime.InstructionBudget` is the ceiling; exceeding
  it raises a `SurtrBudgetExceededException` that no Surtr `catch` can take.

  **It is charged on control transfers, not per instruction.** Straight-line code always reaches a
  return, so the only way to run forever is to keep transferring control - every jump and switch
  arm ends at a shared charging label, and so does frame entry. That leaves the dispatch path
  byte-for-byte what it was before the budget existed, which a per-instruction decrement would not
  have: this is the hottest loop in the system and it is not the place to spend a register and a
  branch on something only a compiler uses. The rule this asks of the switch, and the only one, is
  that a new opcode moving `ip` by an offset must end at `Branched` rather than `Dispatch`.
* **`ResetExecution` between evaluations**, which already exists and already leaves the machine
  clean. Note that it does *not* restore the budget: exceeding it leaves it exhausted rather than
  cleared, so a host re-arms before each evaluation and there is no window where the ceiling
  silently stops applying.

The hard part is on the compiler's side and is recorded in `Language-Syntax.md` §14.2: const
evaluation has to run as its own earlier pass, because `const if` decides what gets emitted at all,
which means the declare → emit → `Build()` → `LoadModule` order has to run twice over different
subsets of a module.

### 4.8 Compiler obligations, recorded so they are not lost

None of these need runtime work; all of them are things the runtime assumes and will not check.

* **Box a primitive into an erased slot, and `Cast` reading one back out** (§1.11).
* **Emit `finally` on every exit path**, plus a catch-all that runs it and re-raises (§1.8).
* **Reject cross-initializer dependencies** (§1.12, §3.4).
* **Honour `SurtrMethodInfo.DeclaringType` naming the declaring interface** for interface methods
  (§3.1).
* **Devirtualise calls on a `sealed` type** — `Language-Syntax.md` §2.2 justifies the modifier
  partly on this, and §3.6 makes it the ideal case for inlining, so it should actually happen.
* **Inline at the bytecode level** for `inline`/`forceinline` (`Language-Syntax.md` §3.6),
  remapping the callee's `SurtrExceptionHandler` ranges into the caller's chunk-absolute table.
  The emitter computing `LocalCount` and `MaxStackSize` (rather than accepting them) is what makes
  a merged frame safe here.
* **Erase type aliases to their target descriptor** (`Language-Syntax.md` §2.7). Nothing reaches
  the runtime, which is the point — but it does mean two signatures differing only by an alias
  collide, and the compiler has to say so.
* **Resolve operator overloads at the use site** (`Language-Syntax.md` §5.6), including the forms
  derived from a single declaration: compound assignment from each arithmetic and bitwise operator,
  `!=` from `==`, all four relational operators from `<=>`, and both the prefix and postfix form of
  `++`/`--` from one declaration, expanded into an assignment back to the operand. `operator as` is
  an explicit conversion only, so it never enters overload resolution.
* **Devirtualise below a `sealed override`** (`Language-Syntax.md` §3.3), which closes a branch of
  the hierarchy the same way a `sealed` class closes the whole of one.
* **Lower `<=>` on built-in types** (`Language-Syntax.md` §5.7). It is a surface operator, not a
  new instruction — but "to the comparison opcodes that already exist" holds only for the numeric
  ones. `string` has `StrEQ`/`StrNE` and no ordering opcode at all, so `<=>` and all four
  relational operators over strings lower to a call to the existing native `string.compareTo`.
* **Lower `as?` to `InstanceOf` plus a branch** (`Language-Syntax.md` §5.7). There is no
  non-throwing cast opcode, and it does not obviously need one; the cost is two type tests on the
  success path, which is worth measuring before a third cast opcode is added to the set.
* **Reject instantiating an `abstract` class.** `ObjNew` resolves its type index and allocates with
  no `IsAbstract` test — consistent with §1.9, but nothing else checks it either.
* **Emit a bridge into a generic interface's erased slot.** `SurtrMethodInfo.SignatureKey()` writes
  `G<n>` as `E`, so an implementation is bound to a contract slot by the erased parameter list —
  which is what lets a class implement `IComparable<T>` at all. A class that also wants a
  `compareTo(other: Vec2)` its own callers can bind directly therefore needs two members: the
  typed one, and a bridge occupying the erased slot that casts and forwards. Exactly javac's
  obligation, for exactly its reason.

### 4.9 `unknown` is the erased slot with a surface name

`Language-Syntax.md` §5.10 adds `unknown`, and it needs **no runtime work at all** — which is why
it was the version worth adding. It resolves to `SurtrValueTypeCode.Erased` and descriptor `E`, the
representation §1.11 already defines for a generic type parameter, so an `unknown` slot is a
reference, is traced, and keeps `IsReferenceType` a range compare.

Two things follow, both already true of erasure:

* The compiler boxes a primitive flowing into an `unknown` and emits a `Cast` reading one out —
  §1.11's two obligations, unchanged.
* **It is not a top type.** There is no root class (`CLAUDE.md`), so `unknown` sits above nothing
  in `Ancestors`; assignability to it is a compiler rule, not a subtype relation the linker builds
  or the interpreter walks. Nothing in the class hierarchy changes.

`Language-Syntax.md` §2.8's `singleton` is likewise ordinary: one class, one instance created with
the module's other statics (§1.12), reached by name.

### 4.10 `>>>` makes an already-implemented opcode reachable

`Shr` (logical, zero-filling) and `Sar` (arithmetic, sign-replicating) both exist and both execute.
Until `Language-Syntax.md` §5.7 added `>>>`, only one of them had a surface spelling — `>>` mapped
to `Sar` and **`Shr` was unreachable from Surtr entirely**. The mapping is now `>>` → `Sar`,
`>>>` → `Shr`, and there is nothing to implement.

Worth noting while here: `Shl`'s XML doc still says over-wide shift counts "still need a defined
behaviour", but §1.9 settled that — they mask to `& 31`. The comment is stale, not the behaviour.

### 4.11 Value classes are the one feature that reaches into the object model

`Language-Syntax.md` §2.9 adds `value class`: a single-field wrapper that is a distinct type to the
compiler and erased to its field at runtime. Everything else in §4 either adds metadata or asks the
compiler to do more work; this one changes what an instance *is* in some positions and not others,
which makes it the most invasive of the language additions.

* **Where the static type is known, there is no object.** A `value class EntityId` wrapping an
  `int` passes as an `int`: no `SurtrInstance`, no entity id, no allocation.
* **Where it flows into a slot that holds a reference** — an erased generic parameter (§1.11), an
  `unknown`, or a variable typed as an interface it implements — it must box into a real object
  with a real `SurtrClass`. That is the same requirement §1.11 already places on primitives, so the
  machinery exists; what is new is that the *same declared type* is sometimes a bare value and
  sometimes an object, and the compiler has to know which at every site.
* Consequently a value class needs a real `SurtrClass` built for it regardless, used only by the
  boxed form.

The open design question is whether the boxed form can reuse `SurtrBoxed` (which today holds one
primitive `SurtrValue` and takes its class from the unboxed primitive) or needs a sibling that
carries the value class's own class instead. `SurtrBoxed` looks close to sufficient, and settling
that is the first step of implementing the feature.

Two facts move that question further than they look like they do. `SurtrBoxed`'s constructor
already **takes the class as a parameter** rather than deriving it, so the object side needs
nothing new — but the **`Box*` opcodes carry no type index**: `BoxInt` and its siblings encode as a
bare `opcode(1)`, so bytecode has no way to say *which* class to box into. A value class therefore
needs a boxing opcode that takes a type index, or a different construction path, and that — not the
object layout — is the first decision.

The second is that `SurtrValueComparer` compares boxes **by content**, so a boxed `EntityId(7)` and
a boxed `int` 7 would compare equal and hash alike. `Language-Syntax.md` §2.9 makes them distinct
types, so the comparer has to compare a box's class too, the moment a box can hold something that
is not a primitive.

### 4.12 Parameter metadata stops at name and type

`SurtrParameterInfo` carries a name and a `SurtrTypeHandle`, and nothing else.
`Language-Syntax.md` §3.5 declares three things about a parameter list; only one of them survives
into metadata:

| Feature | Representable today |
|---|---|
| Named arguments (`spawn(x: 1.0, y: 2.0)`) | **Yes** — the name is there |
| Default values (`hp: int = 100`) | No |
| Varargs (`args: string...`) | No |

Both gaps bite specifically **across a module boundary**, which is where overload resolution has to
work from metadata rather than from a syntax tree the compiler just built. §3.5's rule 2 (a
candidate is applicable *after* defaults supply trailing omissions and varargs absorbs the surplus)
and rule 3 (a candidate needing neither beats one that does, and a non-varargs candidate always
beats a varargs one) are both unanswerable against a parameter list that cannot say which of its
entries are optional.

Neither costs the interpreter anything: a call site arrives with its arguments already filled in
and its varargs array already packed. A default value is a compile-time constant restricted to
primitives and `string` (§7.1 of the syntax document), so it fits in a `SurtrValue` beside the
handle; varargs is one bit on the last parameter. This is metadata and emitter work only.

### 4.13 `sealed` and enum-ness are not in the metadata

Two declaration facts the syntax fixes have nowhere to live, and both are read by a *compiler
looking at another module's metadata* rather than by the interpreter.

* **`sealed`.** `SurtrClass` carries `IsAbstract` and no counterpart, and `SurtrMethodInfo` has no
  mark for a `sealed override`. `Language-Syntax.md` §2.2 and §3.3 justify the modifier mainly on
  devirtualisation — which §4.8 above already records as a compiler obligation, and which cannot be
  honoured against a type the compiler did not itself just parse. The linker also has no way to
  reject a class extending a sealed one.
* **Enum-ness.** `SurtrMemberKind.Enum` exists and is never used: `SurtrClass` passes
  `SurtrMemberKind.Class` to its base unconditionally, and `SurtrModuleBuilder.DefineClass` has no
  parameter for it. `Language-Syntax.md` §2.4 makes an enum a sealed class with a fixed set of
  named static instances, and §4.3 checks a `switch` expression for exhaustiveness over one — which
  needs both "this class is an enum" and its case list, in declaration order, out of metadata.

The case list also wants an **ordinal per case**, for the reason in §4.16: a dense switch over an
enum has nothing to index on otherwise.

### 4.14 There is no per-module native import table

`Language-Syntax.md` §10 promises two things about a `native` declaration: it gets "a slot in the
module's native import table — distinct from the module's regular call table", and a module
declaring a `native` the host never registered "fails to load, the same way an unresolved
`SurtrTypeHandle` does". Neither exists.

`Ldg`, `Stg` and `CallGlobalNative` encode a **direct index into the runtime's global table**, and
`SurtrChunk` has a type, field, method and module table but no native one. Two consequences:

* Nothing binds a native by name at load, so `LoadModule` has nothing to fail on — a module naming
  a global the host never registered runs happily until the instruction is reached.
* A compiled module is **tied to one host's registration order**, since the index means whatever
  that runtime's table happened to hold. That is §3.3 arrived at from a different direction, and
  the two want fixing together: a per-module table of names resolved against `Globals` at load,
  with the instruction indexing the module's table instead of the runtime's.

This is the one entry in §4 that is also a defect in what was built, rather than purely a new
obligation.

### 4.15 Attributes have nowhere to live

`Language-Syntax.md` §11 fixes `@Name(args)` on any declaration and names host reflection as one of
its two audiences — a Unity host reading an attribute back to expose a field to the inspector.
There is no attribute storage anywhere in `Runtime/Classes`: not on `SurtrMemberInfo`, not on
`SurtrTypeInfo`, not on `SurtrParameterInfo`.

That makes it runtime work, unlike the rest of §11. What had to exist first was somewhere to put
them and a host-facing way to read them back; *which* attributes exist stays open after that,
exactly as `Language-Syntax.md` §14.3 leaves it.

**Built as real attribute classes**, not as name/value pairs. `SurtrBuiltIns.Attribute` is the
abstract root every attribute class extends, a `SurtrAttributeUsage` records which class a
declaration named and the constant arguments it was given, and `SurtrMemberInfo.Attributes` holds
them. The instance is built when the declaring module loads — with the module's other statics, for
the same reason those run there, and never lazily on first read. Arguments fill the attribute's
fields positionally rather than through a constructor call, because running bytecode during a load
would mean executing before the module is marked loaded.

Attribute instances are **rooted permanently**. Class metadata is owned outright and is never
registered with the entity registry, so there is nothing for a collection to reach an attribute
instance *through*; the root set is what keeps it alive, and metadata's lifetime is the runtime's.

### 4.16 `Switch` indexes integers only

`Switch` is a dense table over `[low, low + count)` and `SwitchLookup` a sorted-key binary search,
both over an `int` popped off the stack (§1.13). `Language-Syntax.md` §4.3 puts no type restriction
on a `switch`, and two ordinary cases have nothing to lower onto:

* **`switch` over a `string`.** ~~Only `StrEQ`/`StrNE` exist, so it degrades to a compare chain.~~
  **Closed by `StrHash`**, which replaces a string with its hash in one load, so the usual
  lowering — hash, `SwitchLookup`, then `StrEQ` to settle collisions — is now expressible. Closing
  it turned out to need more than reaching the cached hash: `SurtrString.Hash` was
  `string.GetHashCode()`, which .NET Core seeds *per process*, so a compiler hashing the case
  labels and a program hashing the subject would have disagreed on every run but the one that
  built the module. The hash is now FNV-1a over the text and nothing else. That trades away
  hash-flooding resistance, which an embedded language running its host's own scripts was never
  buying anything with, for compiled bytecode that means the same thing in every process — which
  is the entire point of compiling it.
* **`switch` over an enum.** Cases are instances (§2.4), so the keys are references. A dense table
  needs an ordinal, which is the metadata §4.13 asks for; with it, this is `FieldGet` plus an
  ordinary `Switch`.

Neither is a new instruction. Both are lowerings that need one thing from the other side, and the
enum one is why §4.13's case list is not optional.

---

## 5. Remaining work, in order

**~~Phase 1 — a real test project.~~ Done.** `src/Surtr.Tests` exists and `CLAUDE.md` records the
command. The bytecode emitter (`Bytecode/Emit/`) landed alongside it.

**~~Phase 2 — close §3.3 and §3.4.~~ §3.3 done.** `LoadModule` rejects a module that is already
loaded, which turns a silent corruption into a clear failure. §3.4 stays open and stays the
compiler's: nothing detects a cross-initializer dependency, and nothing at load can.

**Phase 3 — the front end, against the syntax spec.** The **lexer, AST and parser are done**, in
`src/Surtr.Compiler/Syntax/`, covering `Language-Syntax.md` end to end and verified against a
sample file exercising every construct in it.

**Diagnostics are done too**, and were done first because every pass after this one reports through
them: `SourceSpan` on every token and node, stable `SurtrDiagnosticCode`s, an accumulating
`SurtrDiagnosticBag` the lexer and parser share, and recovery at declaration and statement
boundaries. Parsing no longer throws on a syntax error — a production still aborts by throwing, but
that is control flow the recovery points catch. The lexer recovers too, skipping a failed literal
whole so that its closing quote never opens another one.

**Binding is next** — name resolution, type checking, overload resolution — and then lowering onto
`Bytecode/Emit`. What the parser still leaves for later is every rule that needs more than syntax to
check, which is all of them: a `sealed` on a non-`override`, an `abstract` member in a
non-`abstract` class, two overloads differing only by an alias.

**~~Phase 4 — the runtime work the language needs.~~ Done.** Every item in §4 is implemented and
covered by `src/Surtr.Tests`. What landed, in the order it landed:

1. **§4.12, §4.13 and §4.1's leftovers — the metadata batch.** Parameter defaults and varargs with
   §3.5's three shape rules enforced at the declaration; `sealed` on a class and on an override,
   with the linker rejecting both ways of violating it; enums as their own member kind, with a case
   list carrying the ordinal an exhaustive switch indexes on; a duplicate signature rejected at
   `AddMethod`; and one signature key shared by the linker and that check instead of two that could
   disagree.
2. **§4.6, real generic parameters on the built-ins**, and with them the whole element-polymorphic
   collection surface, `length` uniform across all four.
3. **§4.3, §4.4 and §4.11 — the value-representation trio.** The absent-primitive tag with the
   float boundary moved to match, `range` as a first-class built-in, and a boxing pair that names
   the class it presents as, with `SurtrValueComparer` comparing a box's class so a boxed value
   class is not equal to a boxed primitive with the same bits.
4. **§4.14 and §3.3 — binding by name at load.** A per-module native import table, resolved against
   the host's globals when the module loads, so a missing name fails there rather than at the
   instruction that would have reached it, and a compiled module stops depending on one host's
   registration order.
5. **§4.2, the standard library and the trap mapping.** `Exception` and the seven classes §13.3
   names, the four core interfaces, `Math` — and every trap now raising the library class it names,
   so `catch (e: Exception)` finally takes what the VM raises. A host exception with no counterpart
   stays a native proxy rather than being forced into a class it is not.
6. **§4.15, attributes as real classes**, instantiated at load and rooted permanently.
7. **§4.7's instruction budget**, charged on control transfers so the dispatch path is unchanged.

Two things surfaced only once this was running, and both are fixed: the budget abort was catchable
by a Surtr catch-all, which handed a spinning program an unlimited run; and the built-in module was
not reachable from a runtime's module table, so nothing could extend `Exception`.

**Phase 4b — what §4 did not close.** §4.8 is untouched and still entirely owed: every item on it is
the compiler's. §4.16 is now closed on the runtime's side — `StrHash` exists and the hash behind it
is deterministic — so both halves of it are lowerings the compiler owes, the enum one needing only
the ordinal that already exists.

**Phase 5 — measure, then optimise.** Not before. Candidates: §3.1, §3.2, and an inline cache on
`InvokeVirtual` if profiling shows monomorphic call sites dominating. All local changes; none
disturbs the frame protocol. §4.5 raises the priority of §3.1, since `for-in`'s general path goes
through interface dispatch.

**Phase 6 — diagnostics.** Frames already carry enough for a stack trace (method, chunk, saved
`IP`), and the handler search already walks them. `SurtrThrownException` does not yet include one.
Cheap to add, and it costs nothing until something raises.
