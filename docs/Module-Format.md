# The `.surtrc` module format

The byte layout of a compiled Surtr module: what a compiler writes, what a runtime reads, and why
each part is shaped the way it is.

Implemented by `src/Surtr.Core/Bytecode/Image/`. `docs/Opcodes.md` describes the instructions that
live inside the code section, and `docs/Runtime-Model.md` describes the metadata the declaration
sections rebuild.

---

## 1. What an image is for

A `SurtrModule` is a live object: loading one patches its string literals with references from the
loading runtime's heap, binds every native member's body against that runtime's registrations (by
link name — §5), and hands its classes static storage the collector traces through that runtime's
registry. It belongs to exactly one runtime, and `LoadModule` rejects a second attempt.

**The image is the shareable form.** It answers two needs with one mechanism:

* **A compiler needs somewhere to put its output.** Without a file format a compiler can only hand
  a module straight to a runtime in the same process, which is not a compiler.
* **A module needs to be loadable into several runtimes.** `Instantiate()` builds a fresh
  `SurtrModule` each time, so a host running a sandbox per script loads the same bytes into all of
  them.

```csharp
var image = SurtrModuleImage.FromModule(builder.Build());

first.LoadModule(image);
second.LoadModule(image);
```

The alternative — splitting every piece of per-runtime state out of `SurtrModule` and reaching it
through an indirection — would have put a test on the hot path of every static field access to
answer a question a second `SurtrModule` answers for nothing. Sharing the bytes rather than the
object is what the JVM and the CLR do, for the same reason.

**Nothing in the library touches a filesystem.** `SurtrModuleImage` deals in `byte[]` and `Stream`;
`.surtrc` is a convention (`SurtrModuleImage.FileExtension`) and the host decides where bytes come
from. That matters because Surtr's host is Unity, where reading a file is a platform question.

```csharp
using (var file = File.Create("game.core" + SurtrModuleImage.FileExtension))
    image.WriteTo(file);

using (var file = File.OpenRead("game.core.surtrc"))
    runtime.LoadModule(SurtrModuleImage.FromStream(file));
```

---

## 2. Conventions

| | |
|---|---|
| Byte order | **Little-endian**, throughout. |
| `u8` | one byte |
| `bool` | one byte, `0` or `1` |
| `u16` | two bytes |
| `i32` | four bytes, signed |
| `u64` | eight bytes |
| `str` | an `i32` **index into the string table**, never inline text |
| `str?` | the same, where `-1` means absent |
| `T[]` | an `i32` count, then that many `T` back to back |

**Every name, descriptor, path and literal is a string-table index.** That is what keeps a format
this descriptor-heavy small: `Osurtr:Exception;` is written once no matter how many members mention
it, and comparing two references is comparing two integers.

An image is machine-written, so the reader is strict: anything that does not parse is a corrupt file
or a version mismatch, and both are reported as `SurtrImageFormatException` rather than guessed at.

---

## 3. Layout

### 3.1 Header

| Field | Type | Meaning |
|---|---|---|
| `magic` | `u64` | `0x444F4D5254525553` — `SURTRMOD` in ASCII, little-endian |
| `formatVersion` | `u16` | Currently **6**. A reader refuses anything else outright. |
| `strings` | `i32` count, then each: `i32` byte length + UTF-8 bytes | The string table. |

`formatVersion` counts changes to how a module is **framed**. It is normally separate from the
opcode set, which evolves under its own rule — every value in `OpCode.cs` is written out and final,
so a new instruction takes a free value and no already-written image means anything different.

**Version 2** added a type's own attribute list, written where a member's already was: a
`SurtrTypeInfo` extends `SurtrMemberInfo` and `Language-Syntax.md` §11 decorates a class as readily
as a field, so a class-level attribute that worked in the process that compiled it and vanished
through an image was the worst of both.

**Version 3** is the one exception to that separation, and the reason the rule now exists. The
instruction set was regrouped by family and renumbered once, deliberately, to fix its values before
anything shipped — so every code byte of a version 2 image means something different under this
reader. Refusing to load one is the whole point of the field; there is no upgrade path, and none is
wanted. Recompile.

**Version 4** retired the native-global mechanism: images no longer carry lists of host-global
variable and function imports, because module-level `native` members now travel as methods and
properties published by link name.

**Version 5** adds a per-generic-parameter constraint list to the `Class` and `Interface`
sections, written right after each type's `genericParameters`. Each bound travels as the descriptor
string it already is — `G<n>` included, so a bound naming the type's own parameter means the same
thing after the round trip. Nothing on an execution path reads the new table; it exists for the
compiler's `MetadataImporter`, tooling and host interop. A version 4 reader would misparse the
extra counts, so it is refused like every other older format.

**Version 6** extends the same idea to methods: every `Method` entry now carries its own
`genericParameters` and per-parameter constraint lists, written after `parameters` and before the
`implKind` tail. The method-level parameter descriptor `H<n>` accompanies it — distinct from the
type-level `G<n>`, so a signature says which parameter it means without knowing the declaring
member. Both stay off every execution path; a version 5 reader would read the extra counts as the
bytecode fields, so it is refused like every other older format.

The string table is written in front of the body but is only complete once the body has been walked,
so the writer builds the body into a buffer first and prepends the table.

### 3.2 Module

| Field | Type |
|---|---|
| `path` | `str` |

Then the chunk (§3.3), then the declarations (§3.4).

### 3.3 Chunk

| Field | Type | Notes |
|---|---|---|
| `code` | `u8[]` | The instruction stream every bytecode method in the module points into. One contiguous block. |
| `constants` | `u64[]` | The inline constant pool, as raw NaN-boxed values. |
| `methodOffsets` | `i32[]` | Where each method's body starts in `code`, indexed by entry index. |
| `stringLiterals` | `{ text: str, slot: i32 }[]` | The text, and the constant-pool slot a reference to it is patched into at load. |
| `typeTable` | `str[]` | Type descriptors the bytecode names by index. |
| `moduleTable` | `str[]` | Other modules this one names, **by path** — every one it calls into (`CallModule`/`CallModuleX`), plus every one it names through `moduleof` (`LoadModule`/`LoadModuleX`) without necessarily calling anything in it. A module naming itself through `moduleof` never adds an entry here — see `LoadCurrentModule` in `docs/Opcodes.md`. |
| `fieldTable` | `MemberRef[]` | Fields the bytecode names by index. |
| `methodTable` | `SignedMemberRef[]` | Call targets the bytecode names by index. |

**The slots' current contents are deliberately not written.** A string-literal slot holds a
reference into the heap of whichever runtime last loaded the module, which means nothing anywhere
else; the text is what travels, and loading interns it and patches the reference back in. That is
why `Ldc` stays one indexed load and needs no idea that strings are different.

**The module table travels as paths, not instances**, for the same reason: the module a call should
land in is whichever one the *loading* runtime has under that path.

#### `MemberRef` and `SignedMemberRef`

| Field | Type | Notes |
|---|---|---|
| `ownerKind` | `u8` | `0` — declared by a type; `1` — declared at module level |
| `ownerDescriptor` | `str` | Present only when `ownerKind` is `0`. |
| `name` | `str` | |
| `signatureKey` | `str` | `SignedMemberRef` only — a method's overload, as `SurtrMethodInfo.SignatureKey` spells it. |

An access-table entry is written as **the name of what it points at**, not as a link. A member
declared in a class is named by its declaring type's descriptor, which carries the module path with
it, so an entry pointing into another module — or into a built-in — travels fine and is bound when
the module loads.

The signature key is what tells overloads apart, and it is the same key the linker matches an
`override` on. A call site written against one overload therefore cannot bind to another.

### 3.4 Declarations

Module level, in this order:

| Section | Type |
|---|---|
| fields | `Field[]` |
| properties | `Property[]` |
| methods | `Method[]` |
| classes | `Class[]` |
| interfaces | `Interface[]` |

Properties are written before methods but **attached after** them by the reader, because a property
points at accessors that are ordinary methods of the same owner.

#### `Field`

| Field | Type |
|---|---|
| `name` | `str` |
| `type` | `str` (descriptor) |
| `isStatic` | `bool` |
| `isReadOnly` | `bool` |
| `visibility` | `u8` |
| `attributes` | `Attribute[]` |

#### `Property`

| Field | Type | Notes |
|---|---|---|
| `name` | `str` | |
| `type` | `str` | |
| `isStatic` | `bool` | |
| `visibility` | `u8` | |
| `hasGetter` | `bool` | followed by the getter's `signatureKey: str` when set |
| `hasSetter` | `bool` | followed by the setter's `signatureKey: str` when set |
| `attributes` | `Attribute[]` | |

A property carries no bodies of its own — the accessors are ordinary `get_x`/`set_x` methods in the
same type's method section, and the signature key is how the reader finds them again.

#### `Method`

| Field | Type |
|---|---|
| `name` | `str` |
| `returnType` | `str` |
| `implKind` | `u8` — `0` bytecode, `1` native, `2` abstract |
| `dispatch` | `u8` — `0` direct, `1` virtual, `2` abstract |
| `role` | `u8` — `0` normal, `1` constructor, `2` static initializer |
| `visibility` | `u8` |
| `isStatic` | `bool` |
| `isOverride` | `bool` |
| `isSealed` | `bool` |
| `parameters` | `Parameter[]` |
| `genericParameters` | `str[]` | The method's own parameter names; empty for a non-generic method. |
| `constraints` | per parameter: `i32` count + `str[]` | As on a class — the bounds each parameter declared, as descriptors (`H<n>` included, e.g. `Osurtr:IComparable`1;H0`). Written only when `genericParameters` is non-empty; one list per parameter, empty where the parameter is unconstrained. |

Then a tail that depends on `implKind`:

| `implKind` | Tail |
|---|---|
| **bytecode** | `entryIndex: i32`, `localCount: i32`, `maxStackSize: i32`, `handlers: Handler[]` |
| **native** | `linkName: str` |
| **abstract** | *(nothing)* |

and finally `attributes: Attribute[]`.

`localCount` and `maxStackSize` travel because the interpreter checks stack room once per call
against them, and recomputing them would mean re-deriving the emitter's stack analysis at load.

**A native method is written as a name.** See §5.

#### `Parameter`

| Field | Type | Notes |
|---|---|---|
| `name` | `str` | A named argument at a call site finds its parameter by this. |
| `type` | `str` | For a varargs parameter this is the **element** type; the body sees an array of it. |
| `isVarargs` | `bool` | |
| `defaultValue` | `Constant` | |

#### `Handler`

| Field | Type | Notes |
|---|---|---|
| `tryStart` | `i32` | Chunk-absolute, not method-relative. |
| `tryEnd` | `i32` | |
| `handlerOffset` | `i32` | |
| `catchType` | `str?` | `-1` means catch-all. |

Offsets are absolute within the module's chunk because every method is already an offset into one
shared instruction stream. The table is written in search order: innermost first, and a
type-specific handler ahead of a catch-all over the same range.

#### `Constant`

| Field | Type |
|---|---|
| `kind` | `u8` — `0` none, `1` int, `2` float, `3` bool, `4` char, `5` string, `6` null |
| payload | nothing for *none* and *null*; `str` for *string*; `u64` for every primitive |

A string constant travels as text rather than as a value, because a string has no reference until
some runtime interns it — the same reason `SurtrConstant` exists at all.

#### `Attribute`

| Field | Type |
|---|---|
| `attributeType` | `str` (descriptor) |
| `arguments` | `Constant[]` |

#### `Class`

| Field | Type | Notes |
|---|---|---|
| `name` | `str` | Unqualified. |
| `typeCode` | `u8` | |
| `visibility` | `u8` | |
| `isAbstract` | `bool` | |
| `isSealed` | `bool` | |
| `isEnum` | `bool` | |
| `baseType` | `str?` | `-1` for a class with no base — there is no root `object`. |
| `interfaces` | `str[]` | Declared, not the transitive closure; the linker builds that. |
| `genericParameters` | `str[]` | Names only, one per parameter. |
| `constraints` | per parameter: `i32` count + `str[]` | The bounds each parameter declared, as descriptors (`G<n>` included, e.g. `Osurtr:IComparable`1;G0`). Written only when `genericParameters` is non-empty; one list per parameter, empty where the parameter is unconstrained. |
| `enumCases` | `{ name: str, visibility: u8 }[]` | |
| `fields` | `Field[]` | Enum-case backing fields excluded — see below. |
| `properties` | `Property[]` | |
| `methods` | `Method[]` | |
| `nestedClasses` | `Class[]` | |
| `nestedInterfaces` | `Interface[]` | |

**A class's full name is rebuilt from where it sits, not written.** `game:Entity.Handle` is the
module path, the enclosing names and this one — a name and a position determine it, and two
spellings of one thing can disagree.

**An enum case's ordinal is not written either.** The cases are replayed through `AddEnumCase` in
declaration order, which is what assigns them. Writing the ordinal and trusting it would let a
hand-edited image renumber a dense `switch` into taking the wrong arm. Their backing fields are
excluded from the field section for the same reason: `AddEnumCase` creates them.

#### `Interface`

| Field | Type |
|---|---|
| `name` | `str` |
| `visibility` | `u8` |
| `extendedInterfaces` | `str[]` |
| `genericParameters` | `str[]` | Names only, one per parameter. |
| `constraints` | per parameter: `i32` count + `str[]` | As on a class — the bounds each parameter declared, as descriptors. Written only when `genericParameters` is non-empty. |
| `methods` | `Method[]` |
| `properties` | `Property[]` |

Every member is abstract by construction, so no interface method carries a body tail.

---

## 4. What is rebuilt when, and why

Reading an image happens in two stages, and the split is not arbitrary — it is exactly the line
between what a module *is* and what a module *is to a particular runtime*.

**`Instantiate()` rebuilds everything self-contained.** Classes, interfaces, fields, properties,
methods, handlers, attributes, and the chunk's code and pools. None of it depends on a runtime: a
class and its bodies mean the same thing wherever they are loaded.

**`LoadModule` binds everything that names the outside world**, in this order:

1. **Type handles** — every descriptor the module mentions, against the runtime's loaded modules,
   its host-declared native classes, and the built-ins. The handle table is the module's dependency
   list, so anything still unresolved afterwards is a load failure rather than a surprise mid-run.
2. **Access tables** — `moduleTable` by path, `fieldTable` and `methodTable` by owner, name and
   signature key. Held until now as `SurtrPendingMember`.
3. **Native bodies** — every native member, module-level or on a class, by link name (§5). This is
   the one and only place a `native` declaration binds to a host body; there is no second, module-
   level-only mechanism beside it.
4. **Linking** — the type linker flattens every table: ancestors, interface closures and dispatch,
   field layout, vtables.
5. **String literals** — interned into the runtime's heap and patched into the constant pool.
6. **Attributes** — one instance per usage, rooted permanently.
7. **Static storage** — registered with the collector.
8. **Static initializers** — each class's, then the module's.

Steps 1–3 are all the same idea: **an image names things, and a load turns names into objects.**

---

## 5. Native members travel as names

A host writes modules too — some entirely native, some mixing compiled Surtr bodies with host ones,
which is the shape `Language-Syntax.md` §13.1 gives the standard library. An address cannot travel
between processes and a name can, so a native method carries a **link name** and each runtime
publishes its own body under it:

```csharp
facade.DeclareNativeMethod("answer", SurtrClassReference.Integer, "host:Facade.answer()");
…
runtime.DefineNativeBody("host:Facade.answer()", SurtrNativeEntryPoint.FromFunctionPointer(&Answer));
```

* A link name is **derived** from the owner and the signature (`host:Facade.answer(I)`) when the
  declaration does not give one, so a host that never ships an image pays nothing for it. A host
  that does ship one should give the name explicitly, because a derived name changes if the class
  is renamed.
* A module-level native derives `<modulePath>.<name>` (`surtr.math.Math.sin`) rather than the bare
  name, so two modules declaring a same-named `native fun` bind against distinct link names instead
  of silently sharing one body. The module path is the module-level member's owning scope, the same
  way the full type name is a class member's.
* A name nothing was published under **fails the load**, beside an unresolved type.
* **Native properties need no separate mechanism.** A property is already a pair of `get_x`/`set_x`
  methods, so making them native is making two methods native.
* An unbound method points at a body that **reports the mistake**, not at null. That costs nothing —
  the interpreter makes the same indirect call — and the difference is between an exception naming
  the problem and an access violation taking the process with it.

**A `native` declaration in Surtr source is a member, module-level or on a class, never a
standalone "host global" form** (`Language-Syntax.md` §10) — a `native fun`/`native let`/`native
var` at module scope compiles to exactly the shape described above, a `SurtrNativeMethodInfo` (or
a property pair of them) with a link name. There used to be a second, module-level-only mechanism
— a per-module native import table, bound to a separate runtime-wide global table — that the
compiler's own output never went through; that mechanism is retired, and the compiler's output
does now contain native members, the same shape a host writing a module by hand already had.

---

## 6. What cannot travel

Two things, both stated rather than worked around.

**The built-in module.** It is process-wide and shared by every runtime, deliberately outside any
runtime's module table. A copy read back from an image would shadow the real one rather than extend
it, so writing one is rejected. It gets away with being shared precisely because it has no
per-runtime state: no static fields, and no string literals to patch.

**A module-level member of *another* module**, named in the field or method access table. Nothing on
a module-level member records which module declares it, so an image cannot name one. Ordinary
cross-module calls are unaffected — those go through the module reference table by path. This is a
limitation of the metadata rather than of the format, and closing it means giving a module-level
member a back-pointer to its module.

---

## 7. Compatibility rules

For anyone changing the format or the instruction set:

1. **Opcode values are written out and final.** `OpCode.cs` spells every value, so nothing is
   renumbered by inserting a member. A new opcode takes a free value from the tail of the byte
   space — 0xDD onwards — and is filed with its family; a retired value stays retired rather than
   being handed to something else.
2. **A layout change bumps `formatVersion`.** Readers refuse a version they do not know rather than
   attempting a partial read; there is no forward compatibility and none is promised.
3. **A section's field order is part of the format.** The reader is a straight sequential pass with
   no lengths to skip by, which is what keeps it simple and strict — and what means fields cannot be
   reordered without a version bump.
4. **`SurtrString.ComputeHash` is frozen.** It is not the CLR's string hash but FNV-1a over the text,
   because a compiler hashes a `switch`'s case labels at build time and the program hashes the
   subject at run time, in another process. Changing it would make every compiled string switch take
   the wrong arm. There are golden-value tests guarding it.
