# The Surtr instruction set

Every opcode the virtual machine executes, by family, with its numeric value, byte layout and
stack effect.

`src/Surtr.Core/Bytecode/OpCode.cs` is the source of truth and carries the same three-part
documentation on each member; this file is that content laid out for reading, plus the parts that
only make sense across the whole set. `docs/VM-Plan.md` has the *why* behind the interpreter's
shape, and `docs/Module-Format.md` describes the file these bytes live in.

**240 opcodes are defined, numbered contiguously from `0x00` (`Nop`) through `0xEF`
(`GenResumed`), family by family. Above them, `0xF0` through `0xFE` are free and `0xFF` is `Ext`,
a prefix rather than an instruction: it opens a second 256-value space, documented in §8.**
The set has been renumbered into that shape once, deliberately: seven instructions with no
producer (`Dup2`, `Swap`, `Swap2`, `Pow`, `FPow`, `ArrNIn`, `DictNIn`) plus the long-retired
host-globals opcodes had left holes nothing could ever fill, so rather than grow around them the
whole set was laid out again - same names, same semantics, new numbers. Every opcode byte in an
image written before that reset means something different now, which is why
`SurtrModuleImage.FormatVersion` was bumped to 10 and a reader refuses anything older instead of
misreading it. There is no upgrade path; recompile. The version has moved on since - it is **13**
now - for reasons that are about how a module is framed rather than about opcode values, except
for the last bump, which opened the extension prefix (§8).

---

## 1. How to read an entry

Surtr is a **stack machine**. Instructions take their operands from the evaluation stack and leave
their results there. Anything that cannot come from the stack — a pool index, a jump offset, an
argument count — is encoded inline after the opcode byte as an **immediate**.

* **Encoding** is written `opcode(1) name(width)`, with the total instruction length after it. Every
  immediate is **little-endian**, and every one is unsigned unless the entry says otherwise (jump
  offsets are signed).
* **Stack** is written `before -> after`. `...` is the untouched remainder of the stack, and the
  **rightmost entry is the top**. A `result?` means the instruction pushes a result only when its
  `retCount` immediate says so.
* A trailing `?` on an operand name means it may be absent.

### Naming conventions

These run through the whole set, so an unfamiliar opcode is usually readable from its name alone.

| Affix | Meaning |
|---|---|
| `F` prefix | Float operands. Untagged opcodes cover `int`, `bool` and `char`, which share one representation. |
| `R` prefix | Reference **identity**, not value equality. |
| `Str` prefix | String operands compared by their **text**. |
| `X` suffix | Widens an immediate to 4 bytes, for a pool or a jump distance that outgrew the 2-byte form. |
| `S` suffix | Narrows an immediate to 1 byte, for the common small-index case. |
| `C` suffix | Moves an operand off the stack and into the instruction as a constant — `TupGetC`. |
| trailing digit | A dedicated opcode for that fixed index, carrying no immediate at all. |

### Why there are so many near-duplicates

Three axes multiply out across the set, and each one buys something measurable:

1. **Immediate width.** `Ldl0`…`Ldl5`, `LdlS`, `Ldl` are the same operation at four sizes. Local
   access is the commonest instruction in any program, so the frequent case is made to cost one
   byte.
2. **Operand family.** `Add` and `FAdd` are separate because integers and floats have different
   representations, and a single opcode would have to test a tag it does not need to — the compiler
   already knows.
3. **Fused compare-and-branch.** `JPLT` exists so that `LT` followed by `JPZ` — two dispatches and a
   boolean that only ever feeds a branch — becomes one. Nearly every condition a compiler emits has
   this shape.

The emitter's third tier (`SurtrCodeEmitter.Helpers`) picks the encoding, so a compiler writes
`LoadLocal(slot)` and `JumpIfCompare(…)` and never chooses between these by hand.

---

## 2. The numeric values are the format

An opcode's number **is the on-disk encoding**: `Bytecode/Image/` writes chunks to bytes
(`docs/Module-Format.md`), so every value in this document is on disk somewhere.

**Every value is written out in `OpCode.cs`, and every value is final.** The set is laid out by
family, numbered contiguously in the order this document presents it, and each member's value is
spelled rather than implied. That is what makes the two questions independent: where an opcode is
*filed* is a readability decision, and what it is *numbered* is the format. Implicit numbering
welded the two together - a member inserted in the middle renumbered everything after it,
silently.

**A new opcode takes a free value at the end and is filed with its family.** The free values are
`0xF0` through `0xFF`, all after the last assigned one, so filing a new instruction with its
family means writing its members in family order while its *number* comes from the tail. When the
tail itself was a pile of holes left by retired instructions, that policy could not hold - which
is why the set carries a second rule:

**Renumbering is a decision someone comes here to make on purpose.** The set has been reset into
one contiguous run once - same names, same semantics, new numbers - reclaiming the retired values
that had accumulated instead of growing around them forever. Every opcode byte in an image
written before such a reset names a different instruction afterwards, so each pass bumps
`SurtrModuleImage.FormatVersion` - to 3 the first time, to 10 the second - and a reader refuses
an older image rather than misread it. There is no upgrade path; recompile. The golden-value
tests over the whole table (`src/Surtr.Tests/Bytecode/OpCodeValueTests.cs`) are what make the
reset a recorded act rather than an accident.

---


---

## 3. The calling convention, in one place

Six opcodes take `argsCount` and `retCount` immediates, and both mean the same thing everywhere:

* **`argsCount` counts every incoming stack slot, receiver included.** On an instance call the
  receiver *is* argument 0. That is what makes the callee's frame base `sp - argsCount` for every
  kind of call — one subtraction, no branch — and what makes `Ldl0` read `this`.
* **`retCount` is 0 or 1, and it counts *results*, not slots.** A call site that does not want a
  result asks for none rather than popping one, so the frame protocol drops it on return. How
  *wide* the one result is — one slot for every reference and every primitive, `n` for a value
  type or a tuple — is a fact about the callee's declared return type, not about the call site:
  the callee ends with `ReturnValues` and emits its own slot count. Nothing in the call encoding
  changed to allow multi-slot returns, which is the whole reason they could be added without
  touching a single existing call site. The width itself lives on the method as its
  `ResultSlotCount`; do not read `retCount` as one.
* Arguments are **already in place**: the callee's frame starts underneath them and they become its
  locals `0…N-1` without being copied.
* Stack room is checked **once per call** against the callee's `MaxStackSize`. That is the only
  stack-overflow check in the interpreter, which is why the emitter computes `MaxStackSize` rather
  than accepting it.

**There is no separate opcode for calling host code, without exception.** Where a call lands is a
property of the method the call site names, not of the call site, and the interpreter reads it
anyway because a virtual call can resolve onto a native override. Every `Invoke` and `Call` reaches
bytecode and host bodies alike — a `native` member, module-level or on a class, lands in the same
method table as any other and is reached through `CallLocalModule`/`CallModule` like any other
module-level function. There used to be a `CallGlobalNative` exception for a host-defined global
function living in a table of its own; that mechanism (and the global-variable one behind
`Ldg`/`Stg`) is retired — see the note on retired values at the top of this document.

---

## 4. Branches

Every branch offset is **signed and relative to the instruction following the branch**, so a
negative offset goes backwards — the shape of every loop. Short forms carry two bytes, `X` forms
carry four; the emitter starts short and widens to a fixed point, since widening one branch moves
everything after it.

**`Switch` and `SwitchLookup` are the exception**: their offsets are measured from their **own
opcode byte**. A variable-length instruction has no fixed "next address" to measure from at emit
time.

---

## 5. What traps, what is defined, and what is not checked

Static typing has already paid for most validation, so the interpreter deliberately does not repeat
it. **Not checked at all**: local, constant and pool indices; argument counts; the concrete type
behind a reference; per-push stack room.

**Trapped**, each from a cold helper, and each naming the library class it surfaces as — so a Surtr
`catch` can name what the runtime raises:

| Condition | Surfaces as |
|---|---|
| Integer division or remainder by zero, and `int.MinValue / -1` | `DivideByZeroException` |
| Negative integer exponent | `ArgumentException` |
| Array, string or tuple index out of range | `IndexOutOfRangeException` |
| Popping an empty array | `InvalidOperationException` |
| `DictGet` on a missing key | `KeyNotFoundException` |
| A failed `Cast` | `InvalidCastException` |
| Call-depth or data-stack overflow | `StackOverflowException` |
| An invalid opcode byte | `InvalidOperationException` |
| A null receiver (through the CLR's own null check) | `NullReferenceException` |

**Defined rather than trapped**, because defining them is free: shift counts mask to `& 31`; `F2I`
saturates and maps `NaN` to 0, which is deterministic across x64 and ARM where an unchecked cast is
not; `FDiv` follows IEEE 754.

One condition is deliberately **not** catchable: exceeding the instruction budget leaves as a
`SurtrBudgetExceededException` that the handler search never sees. A program able to catch its own
watchdog would give back the only thing the budget promises.

---

## 6. A reference is its 32-bit payload

`IsNull`, `REQ`, `JPN` and their siblings compare the low 32 bits and ignore the tag, so **a zeroed
slot and an explicitly tagged null are the same reference**. That is what lets a fresh local read as
null without the interpreter knowing its declared type.

Where the tag does matter — a value handed to a native function, or boxed — `ArrNew` fills a new
array with its element family's correctly tagged zero. Floats and references need no fill: `0.0` is
all-zero bits, and an untagged zero already reads as null.

---

## 7. The instruction set
<!-- The tables below are generated from OpCode.cs; edit the XML docs there, not here. -->


## Nop

Zero, and the only value that was never going to be anything else: a zeroed byte has to mean "do nothing" for a partially written stream to fail safely, and it is the one value other code might assume.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x00` | `Nop` | `opcode(1)` · 1 byte | `... -> ...` | Does nothing. Useful as a patch target when the emitter has to overwrite an instruction in place. |


## Stack Operations

Pure shuffling: no operand is interpreted, no tag is inspected. (`Dup2`, `Swap` and `Swap2` used to
live here; nothing ever emitted them, and the renumbering reclaimed their values.)

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x01` | `Dup` | `opcode(1)` · 1 byte | `..., value -> ..., value, value` | Duplicates the value on top of the stack. |
| `0x02` | `Pop` | `opcode(1)` · 1 byte | `..., value -> ...` | Discards the value on top of the stack. How a call's unused return value is dropped in statement position. |


## Constants and Literals

Everything that materialises a value out of nothing: the inline literal forms, which carry their value in the instruction itself, and the constant pool, whose first ten slots have a dedicated opcode each. A literal that fits inline never touches the pool, which is what keeps `Ldc0`…`Ldc9` available for the values that cannot — floats and strings.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x03` | `PushNull` | `opcode(1)` · 1 byte | `... -> ..., null` | Pushes the null reference. This is the null reference. The absence of a nullable primitive is `PushAbsent`, and the two carry different tags on purpose. |
| `0x04` | `PushTrue` | `opcode(1)` · 1 byte | `... -> ..., bool` | Pushes the boolean `true`. There are exactly two booleans, so each gets an opcode and neither costs a constant pool slot. The pool's first ten slots are the cheapest storage in the format and are better spent on values that cannot be pushed inline at all. |
| `0x05` | `PushFalse` | `opcode(1)` · 1 byte | `... -> ..., bool` | Pushes the boolean `false`. |
| `0x06` | `PushI8` | `opcode(1) value(1)` · 2 bytes | `... -> ..., int` | Pushes a signed 8-bit integer literal, sign-extended to a full integer value. The narrowest way to materialise a small literal without touching the constant pool. |
| `0x07` | `PushI16` | `opcode(1) value(2)` · 3 bytes | `... -> ..., int` | Pushes a signed 16-bit integer literal, sign-extended to a full integer value. |
| `0x08` | `PushI32` | `opcode(1) value(4)` · 5 bytes | `... -> ..., int` | Pushes a signed 32-bit integer literal. |
| `0x09` | `PushChar` | `opcode(1) value(2)` · 3 bytes | `... -> ..., char` | Pushes a character literal, carried inline as a UTF-16 code unit. The immediate is unsigned and covers the whole code unit range, so every character literal fits inline and none reaches the constant pool. The pushed value carries the char tag, which is what decides the class it reports and which box `BoxChar` makes. |
| `0x0A` | `PushAbsent` | `opcode(1) typeCode(1)` · 2 bytes | `... -> ..., absent` | Pushes the "no value" state of a nullable primitive. The immediate is the `SurtrValueTypeCode` of the primitive that is missing, so the value can say what it is the absence of. It is never the null reference: that is `PushNull`, and the two carry different tags on purpose. |
| `0x0B` | `Ldc0` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 0. |
| `0x0C` | `Ldc1` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 1. |
| `0x0D` | `Ldc2` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 2. |
| `0x0E` | `Ldc3` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 3. |
| `0x0F` | `Ldc4` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 4. |
| `0x10` | `Ldc5` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 5. |
| `0x11` | `Ldc6` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 6. |
| `0x12` | `Ldc7` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 7. |
| `0x13` | `Ldc8` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 8. |
| `0x14` | `Ldc9` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 9. |
| `0x15` | `LdcS` | `opcode(1) constIdx(1)` · 2 bytes | `... -> ..., value` | Loads a constant using a 1-byte index, for the first 256 pool entries. |
| `0x16` | `Ldc` | `opcode(1) constIdx(2)` · 3 bytes | `... -> ..., value` | Loads the constant at `constIdx` from the chunk's constant pool. |
| `0x17` | `LdcX` | `opcode(1) constIdx(4)` · 5 bytes | `... -> ..., value` | Loads a constant using a 4-byte index, for pools larger than 65536 entries. |


## Local Variables

The frame's own slots, and the densest family in the set — local access is the commonest instruction in any program, so slots 0–5 get an opcode with no immediate at all, slots up to 255 a one-byte form, and only past that does an index cost two bytes. `IncLocal` is the one instruction here that both reads and writes, and it exists because a counted loop is the shape that pays for it.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x18` | `Ldl0` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 0. On an instance method this is the receiver. |
| `0x19` | `Ldl1` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 1. |
| `0x1A` | `Ldl2` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 2. |
| `0x1B` | `Ldl3` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 3. |
| `0x1C` | `Ldl4` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 4. |
| `0x1D` | `Ldl5` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 5. |
| `0x1E` | `LdlS` | `opcode(1) localIdx(1)` · 2 bytes | `... -> ..., value` | Loads a local using a 1-byte index, for the first 256 slots of the frame. |
| `0x1F` | `Ldl` | `opcode(1) localIdx(2)` · 3 bytes | `... -> ..., value` | Loads the local variable at `localIdx` from the current frame. |
| `0x20` | `Stl0` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 0. |
| `0x21` | `Stl1` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 1. |
| `0x22` | `Stl2` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 2. |
| `0x23` | `Stl3` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 3. |
| `0x24` | `Stl4` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 4. |
| `0x25` | `Stl5` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 5. |
| `0x26` | `StlS` | `opcode(1) localIdx(1)` · 2 bytes | `..., value -> ...` | Pops a value into a local using a 1-byte index. |
| `0x27` | `Stl` | `opcode(1) localIdx(2)` · 3 bytes | `..., value -> ...` | Pops a value and stores it into the local at `localIdx`. |
| `0x28` | `IncLocal` | `opcode(1) localIdx(1) delta(1)` · 3 bytes | `... -> ...` | Adds a signed 8-bit constant to an integer local, in place. The whole of `i += 1` in one instruction. Written out it is `Ldl`, `PushI8`, `Add`, `Stl` - four dispatches, up to eight bytes, and two round trips through the operand stack - for an update that never needs to leave the frame. The delta is signed, so a decrement is the same instruction. The local is read and written as an integer and comes back tagged as one; a slot holding anything else has no defined result, and the compiler is what guarantees it does not. Locals past 255, and deltas outside a signed byte, fall back to the long form - `SurtrCodeEmitter.IncrementLocal` decides which. |


## Field Operations

Instance fields reach their storage through a slot index resolved when the declaring type was linked, so a read is an offset into the instance rather than a name lookup. Statics are a separate pair rather than a flag on the same opcode, because folding them together would put a static/instance test on one of the hottest instructions in the set — and a module-level variable is a static of its module, reaching its storage the same way.

A **native field** (a `SurtrNativeFieldInfo`, declared by the host bridge, not by Surtr source) has no slot and no static storage; each of the six opcodes below first tests the field table entry and, when it is native, calls the field's getter or setter entry point instead of touching a slot. The test is a single type check on the entry the opcode already loaded, so the ordinary field path is unchanged.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x29` | `FieldGet` | `opcode(1) fieldIdx(2)` · 3 bytes | `..., obj -> ..., value` | Reads an instance field. The field table entry carries the slot index, so the read is a direct offset into the instance rather than a name lookup. A null receiver hits the CLR null check and surfaces as `NullReferenceException`. |
| `0x2A` | `FieldSet` | `opcode(1) fieldIdx(2)` · 3 bytes | `..., obj, value -> ...` | Writes an instance field. The compiler must reject this against a read-only field outside a constructor. |
| `0x2B` | `StaticFieldGet` | `opcode(1) fieldIdx(2)` · 3 bytes | `... -> ..., value` | Reads a static field, or a module-level variable. No receiver, which is why this cannot be folded into `FieldGet` - doing so would put a static/instance test on one of the hottest instructions in the set. Module-level variables are the same thing: Surtr has no true globals, so a module variable is a static of its module and reaches its storage the same way. The field table entry carries the address of the slot itself, resolved when the declaring type was linked, so this is one indirect load. |
| `0x2C` | `StaticFieldGetX` | `opcode(1) fieldIdx(4)` · 5 bytes | `... -> ..., value` | Reads a static field using a 4-byte field index. |
| `0x2D` | `StaticFieldSet` | `opcode(1) fieldIdx(2)` · 3 bytes | `..., value -> ...` | Writes a static field, or a module-level variable. The compiler must reject this against a read-only field outside its static initializer. |
| `0x2E` | `StaticFieldSetX` | `opcode(1) fieldIdx(4)` · 5 bytes | `..., value -> ...` | Writes a static field using a 4-byte field index. |


## Upvalue Operations

A closure's captured values. There is no setter, and that is a language decision rather than a gap: a capture is copied rather than shared, so the compiler only ever captures what is never reassigned.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x2F` | `UpValueGet` | `opcode(1) upvalueIdx(1)` · 2 bytes | `... -> ..., value` | Reads a captured variable from the currently executing closure. Only valid inside a closure body. There is deliberately no matching setter: a capture is copied rather than shared, so the compiler only ever captures what is never reassigned. |


## Value Type Operations

Everything a value wider than one slot needs, and deliberately nothing more. A multi-slot value is **`n` contiguous slots** wherever it lives - on the operand stack, in a local range, inside an instance's flattened field block, inside a static's storage - so every one of these instructions is a block copy between two of those places, with `n` carried as an immediate. The compiler knows the layout statically, so the interpreter never resolves a type to move bytes.

Three properties make the family this small. **The stack needs no help**: it is already untyped 8-byte slots, so `n` slots pushed are `n` slots, and the collector's tag test already traces each of them correctly with nothing changed. **The layout is flat**: the linker folds a nested value type's slots into consecutive slots of its container, so reaching a sub-field is address arithmetic the compiler already did - which is why reading *one* slot of a multi-slot value keeps using `Ldl`, `FieldGet` and `StaticFieldGet` at the summed absolute index rather than needing forms of its own. And **the boxed form is an ordinary instance**: `BoxValue` produces a normal object of the named class whose field slots take the `n` stack slots verbatim, so field access, virtual dispatch and the collector's reference-slot walk all already know how to handle one.

A one-field `value class` never reaches any of this. It erases to the field it wraps, occupies one slot, and keeps using `Ldl`/`FieldGet`/`BoxAs` exactly as before - which is what made adding the family a purely additive change.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x30` | `LoadValueLocal` | `opcode(1) localIdx(2) n(1)` · 4 bytes | `... -> ..., s1, ..., sn` | Copies a multi-slot value out of a local range onto the operand stack. The whole block moves; nothing is resolved and no tag is inspected. What a load of a variable whose declared type occupies more than one slot lowers to - a one-slot type keeps using `Ldl` and its dedicated forms. The count travels in the instruction so the interpreter never resolves a type to move bytes. |
| `0x31` | `StoreValueLocal` | `opcode(1) localIdx(2) n(1)` · 4 bytes | `..., s1, ..., sn -> ...` | Pops a multi-slot value into a local range. The mirror of `LoadValueLocal`, and what an assignment to such a variable lowers to - copying the block is exactly the copy-on-assignment semantics of a value type. |
| `0x32` | `LoadLocalField` | `opcode(1) localIdx(2) offset(2)` · 5 bytes | `... -> ..., value` | Reads one slot at a fixed offset inside a local range. What reading one field of a multi-slot local lowers to - `v.x` reads slot `local + 0` without moving the rest of the value anywhere. The offset is absolute within the frame (local index plus field offset, already summed by the compiler), so the read is one indexed load off the frame base. |
| `0x33` | `StoreLocalField` | `opcode(1) localIdx(2) offset(2)` · 5 bytes | `..., value -> ...` | Writes one slot at a fixed offset inside a local range. The write side of `LoadLocalField`. In practice only the compiler's constructor splice emits it - fields of a value class are `let`, so assignment is construction - but the opcode itself does not care who writes or when. |
| `0x38` | `BoxValue` | `opcode(1) typeIdx(2) n(1)` · 4 bytes | `..., s1, ..., sn -> ..., ref` | Boxes a multi-slot value on top of the stack as an instance of its class. What a value type flowing into a reference-typed slot boxes through. The box is an ordinary instance of the named class whose field slots receive the `n` stack slots verbatim - which is why every existing path that walks instances (field access, virtual dispatch, the collector through the class's reference-slot map) already knows how to walk a boxed value. Allocates, so it routes through the safepoint like every other allocation opcode. |
| `0x39` | `UnboxValue` | `opcode(1) n(1)` · 2 bytes | `..., ref -> ..., s1, ..., sn` | Copies the field slots of a boxed value back onto the operand stack. The mirror of `BoxValue`. The count is an immediate because the compiler knows the subject's layout statically; the receiver itself is not re-checked - a cast to the value type has already run by the time this executes, exactly as `Unbox` assumes its subject is a box. |
| `0x34` | `LoadValueField` | `opcode(1) fieldIdx(2) n(1)` · 4 bytes | `..., obj -> ..., s1, ..., sn` | Copies a multi-slot value out of an instance's flattened field block. What reading a field whose declared type is a multi-field value class lowers to (`enemy.position`). The field's own slot index is where the block starts inside the instance - the linker flattened nested value types into consecutive slots, so the copy is one indexed base plus a run. Reading one sub-slot of that value keeps using `FieldGet` at the summed absolute slot; this moves the whole block. |
| `0x35` | `StoreValueField` | `opcode(1) fieldIdx(2) n(1)` · 4 bytes | `..., obj, s1, ..., sn -> ...` | Pops a receiver and a multi-slot value into an instance's flattened field block. The write side of `LoadValueField`, and what every assignment to such a field lowers to - including the constructor splice, which is the only writer a `let` field ever gets. Copying the block is the value type's copy-on-assignment semantics showing at its storage boundary. |
| `0x36` | `LoadValueStatic` | `opcode(1) fieldIdx(2) n(1)` · 4 bytes | `... -> ..., s1, ..., sn` | Copies a multi-slot value out of a static's flattened storage. The static counterpart of `LoadValueField`. A static whose declared type is a multi-field value class owns `n` consecutive slots in its owner's static storage, addressed from the slot the linker bound - so the read is one indirect base plus a run, exactly what `StaticFieldGet` does for one slot. |
| `0x37` | `StoreValueStatic` | `opcode(1) fieldIdx(2) n(1)` · 4 bytes | `..., s1, ..., sn -> ...` | Pops a multi-slot value into a static's flattened storage. The write side of `LoadValueStatic` - what an assignment to such a static, and the static initializer that seeds it, both lower to. |


## Arithmetic Operations

Integer and float forms of each operation, paired. They are separate opcodes because the two representations differ and a single one would have to test a tag the compiler already knows the answer to. The untagged forms cover `int`, `bool` and `char`, which share one representation.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x3C` | `Add` | `opcode(1)` · 1 byte | `..., a, b -> ..., a + b` | Integer addition. |
| `0x3D` | `FAdd` | `opcode(1)` · 1 byte | `..., a, b -> ..., a + b` | Floating-point addition. |
| `0x3E` | `Sub` | `opcode(1)` · 1 byte | `..., a, b -> ..., a - b` | Integer subtraction. The deeper operand is the minuend, so the result is `a - b`, not `b - a`. |
| `0x3F` | `FSub` | `opcode(1)` · 1 byte | `..., a, b -> ..., a - b` | Floating-point subtraction. |
| `0x40` | `Mul` | `opcode(1)` · 1 byte | `..., a, b -> ..., a * b` | Integer multiplication. |
| `0x41` | `FMul` | `opcode(1)` · 1 byte | `..., a, b -> ..., a * b` | Floating-point multiplication. |
| `0x42` | `Div` | `opcode(1)` · 1 byte | `..., a, b -> ..., a / b` | Integer division. Division by zero has no defined result yet and needs a trap decision. |
| `0x43` | `FDiv` | `opcode(1)` · 1 byte | `..., a, b -> ..., a / b` | Floating-point division. Division by zero follows IEEE 754 and yields an infinity or NaN rather than trapping. |
| `0x44` | `Mod` | `opcode(1)` · 1 byte | `..., a, b -> ..., a % b` | Integer remainder. As with `Div`, a zero divisor still needs a defined behaviour. |
| `0x45` | `FMod` | `opcode(1)` · 1 byte | `..., a, b -> ..., a % b` | Floating-point remainder. |

(`Pow` and `FPow` used to live here; the language has no exponentiation operator - `pow` is a
native library function reached through an ordinary call - so nothing ever emitted them. The
renumbering reclaimed their values.)

| `0x46` | `Neg` | `opcode(1)` · 1 byte | `..., a -> ..., -a` | Integer negation. |
| `0x47` | `FNeg` | `opcode(1)` · 1 byte | `..., a -> ..., -a` | Floating-point negation. Flips the sign bit, so it also turns zero into negative zero. |


## Bitwise and Logical Operations

The bit operations, plus the one boolean operator that is not a comparison. `Not` and `Inv` are the pair worth keeping straight: `Not` is the bitwise complement, `Inv` the logical negation.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x48` | `And` | `opcode(1)` · 1 byte | `..., a, b -> ..., a & b` | Bitwise AND. |
| `0x49` | `Or` | `opcode(1)` · 1 byte | `..., a, b -> ..., a \| b` | Bitwise OR. |
| `0x4A` | `Xor` | `opcode(1)` · 1 byte | `..., a, b -> ..., a ^ b` | Bitwise exclusive OR. |
| `0x4B` | `Not` | `opcode(1)` · 1 byte | `..., a -> ..., ~a` | Bitwise complement. This is the bitwise operator. The boolean negation is `Inv`. |
| `0x4C` | `Shl` | `opcode(1)` · 1 byte | `..., a, b -> ..., a << b` | Left shift. Shifts the deeper operand left by the top one. Shift counts at or above the operand width still need a defined behaviour. |
| `0x4D` | `Shr` | `opcode(1)` · 1 byte | `..., a, b -> ..., a >>> b` | Logical right shift, filling with zeroes. Does not preserve the sign - a negative value becomes a large positive one. The sign-preserving form is `Sar`. |
| `0x4E` | `Sar` | `opcode(1)` · 1 byte | `..., a, b -> ..., a >> b` | Arithmetic right shift, replicating the sign bit. Keeps the sign, so a negative value stays negative. The zero-filling form is `Shr`. |
| `0x4F` | `Inv` | `opcode(1)` · 1 byte | `..., a -> ..., !a` | Logical negation of a boolean. This is the boolean operator. The bitwise complement is `Not`. |


## Comparison Operations

Five operand families, each with its own opcodes: integers (which also cover `bool` and `char`), floats under IEEE 754, references by identity, strings by text, and a still-abstract generic type parameter by the runtime's own value comparer. Strings, references and the dynamic family carry equality only — ordering a string is a call to `string.compareTo`, which is what the language says the operators mean, and an unconstrained type parameter has no ordering to give.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x50` | `EQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Integer equality. Also covers bools and chars, which share the integer representation. |
| `0x51` | `NE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Integer inequality. |
| `0x52` | `GT` | `opcode(1)` · 1 byte | `..., a, b -> ..., a > b` | Integer greater-than. |
| `0x53` | `GE` | `opcode(1)` · 1 byte | `..., a, b -> ..., a >= b` | Integer greater-than-or-equal. |
| `0x54` | `LT` | `opcode(1)` · 1 byte | `..., a, b -> ..., a < b` | Integer less-than. |
| `0x55` | `LE` | `opcode(1)` · 1 byte | `..., a, b -> ..., a <= b` | Integer less-than-or-equal. |
| `0x56` | `FEQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Floating-point equality. IEEE 754 semantics, so NaN compares unequal to everything including itself. |
| `0x57` | `FNE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Floating-point inequality. NaN compares unequal to everything, so this yields true when either side is NaN. |
| `0x58` | `FGT` | `opcode(1)` · 1 byte | `..., a, b -> ..., a > b` | Floating-point greater-than. False whenever either operand is NaN. |
| `0x59` | `FGE` | `opcode(1)` · 1 byte | `..., a, b -> ..., a >= b` | Floating-point greater-than-or-equal. False whenever either operand is NaN. |
| `0x5A` | `FLT` | `opcode(1)` · 1 byte | `..., a, b -> ..., a < b` | Floating-point less-than. False whenever either operand is NaN. |
| `0x5B` | `FLE` | `opcode(1)` · 1 byte | `..., a, b -> ..., a <= b` | Floating-point less-than-or-equal. False whenever either operand is NaN. |
| `0x5C` | `REQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Reference identity. Compares handles, not contents - two equal strings in different objects are not identical. |
| `0x5D` | `RNE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Reference non-identity. |
| `0x5E` | `StrEQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | String equality by text. The counterpart to `REQ` for the one reference type Surtr compares by value. Its own opcode rather than a call to `string.equals`, because `==` on strings is common enough that a call per comparison would show. Two null strings are equal; a null and a non-null are not. |
| `0x5F` | `StrNE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | String inequality by text. |
| `0x60` | `DynEQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Value equality decided at runtime by each operand's own tag, for a generic type parameter's own slot. What `==` lowers to when neither operand's static type is a family with a dedicated form - a bare, still-abstract type parameter, most commonly. `==` is value equality everywhere in Surtr (§5.7 of Language-Syntax.md), which for a boxed primitive means a boxed `5` equals an unboxed `5` and two independently boxed `5`s equal each other - exactly what `REQ` gets wrong, since two different boxes are two different entities. Reaches the same `SurtrValueComparer` every dictionary and array search already uses. |
| `0x61` | `DynNE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | The negation of `DynEQ`. |


## Null and Absence Tests

Two different questions that look alike in source and are nothing alike in the value representation. A reference is its 32-bit payload, so `IsNull` ignores the tag; a nullable primitive's absence *is* a tag, so `IsAbsent` reads nothing else. Testing one with the other's opcode would answer wrongly for an `int` of value zero.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x62` | `IsNull` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Tests whether the top value is the null reference. |
| `0x63` | `IsNotNull` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Tests whether the top value is a non-null reference. |
| `0x64` | `IsAbsent` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Replaces a nullable primitive with whether it holds no value. Tests the tag, not the payload, which is exactly why it cannot be `IsNull`. A reference is its 32-bit payload, so `IsNull` ignores the tag - and an `int` of value zero would answer that test the same way a null does. |
| `0x65` | `IsPresent` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Replaces a nullable primitive with whether it holds a value. |


## Conversion Operations

The primitive conversions, all between `int` and one other family — anything else routes through `int` in two instructions, which is what the emitter's `Convert` helper does. Only `F2I` and `I2C` lose information; the rest change a tag.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x66` | `I2F` | `opcode(1)` · 1 byte | `..., a -> ..., float` | Widens an integer to a float. |
| `0x67` | `F2I` | `opcode(1)` · 1 byte | `..., a -> ..., int` | Narrows a float to an integer. Lossy, and pinned down rather than an unchecked C# cast, whose behaviour for an out-of-range double is platform-defined and would differ between x64 and ARM (§1.9). Truncates toward zero in range; saturates to `int.MinValue`/`int.MaxValue` outside it; `NaN` converts to `0`. |
| `0x68` | `I2C` | `opcode(1)` · 1 byte | `..., a -> ..., char` | Retags an integer as a character. Int, bool and char share one representation, so this changes only the value's tag and truncates the payload to 16 bits. The tag still matters: it is what decides which class the value reports and which box `BoxChar` versus `BoxInt` produces. |
| `0x69` | `C2I` | `opcode(1)` · 1 byte | `..., a -> ..., int` | Retags a character as an integer. Always exact - every character fits an integer. |
| `0x6A` | `I2B` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Converts an integer to a boolean. Normalises as well as retags - any non-zero integer becomes `true`, so the payload is always 0 or 1 afterwards. That normalisation is what lets every boolean opcode treat the payload as a bit. |
| `0x6B` | `B2I` | `opcode(1)` · 1 byte | `..., a -> ..., int` | Retags a boolean as an integer, giving 0 or 1. |


## Boxing Operations

Turning a primitive into a collectable reference and back. The `Box*` family carries no type index because a boxed primitive keeps the class the unboxed one already had; `BoxAs` exists for the one case where it cannot — a `value class`, which is erased to the field it wraps and so no longer says what it was. Unboxing is one opcode either way, because the box's own value carries its tag.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x6C` | `BoxInt` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes an integer into a heap object. Allocates, so the result is a collectable reference rather than an inline value. |
| `0x6D` | `BoxFloat` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes a float into a heap object. |
| `0x6E` | `BoxBool` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes a boolean into a heap object. |
| `0x6F` | `BoxChar` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes a character into a heap object. |
| `0x70` | `BoxAs` | `opcode(1) typeIdx(2)` · 3 bytes | `..., a -> ..., ref` | Boxes the value on top of the stack as an instance of a named class. What a `value class` boxes through. The `Box*` family carries no type index because a boxed primitive takes the class the unboxed primitive already had; a value class is erased to the field it wraps, so where it has to become a reference the class it should present as is exactly the thing the value no longer says. Unboxing is still `Unbox`: the box's own value carries its tag. |
| `0x71` | `BoxAsX` | `opcode(1) typeIdx(4)` · 5 bytes | `..., a -> ..., ref` | Boxes as a named class, with a 4-byte type index. |
| `0x72` | `Unbox` | `opcode(1)` · 1 byte | `..., ref -> ..., value` | Unwraps a boxed value back to its inline representation. Recovers whichever primitive was boxed - the tag on the boxed value says which, so no per-type opcode is needed on the way back. |
| `0x73` | `BoxDynamic` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes whatever primitive is on top of the stack, chosen by its own tag rather than the compiler's. A no-op when the subject is already a reference (including `null`). Exists for a generic type parameter's own erased slot: a value crossing one through a body compiled once for every substitution of `T` may already be boxed or may still be a built-in's own raw storage, and nothing at the call site can tell which - only the value's own tag can. `Unbox` already reads that tag on the way back out; this is its mirror on the way in. |
| `0x74` | `UnboxDynamic` | `opcode(1)` · 1 byte | `..., a -> ..., value` | The mirror of `BoxDynamic`: unboxes only a boxed primitive, leaving everything else (a raw primitive, or a reference that is not a box at all - an ordinary object, array, string) untouched. Exists for the write side of the same erased slot `BoxDynamic` reads from: a value statically typed by a bare generic parameter arrives already boxed, but the collection's own native storage it is written into was never boxed to begin with and must not become the one element that is. |


## Range Operations

A range that genuinely escapes into a variable, a parameter or a return. One written inline in a `for-in` header never reaches these — the compiler lowers that to a counted loop over two ints.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x75` | `RangeNew` | `opcode(1)` – 1 byte | `..., lo, hi -> ..., lo, hi, 0` | Lays out a range block from two int bounds, excluding the upper one. A range is an inline value - three raw slots (start, end, inclusive flag), none of them a reference - so this allocates nothing; it only materialises the flag its operator baked in. A range written inline in a `for-in` header must never even reach this - the compiler lowers that to a counted loop over two ints - so this is for a range that genuinely escapes into a variable, a parameter or a return. |
| `0x76` | `RangeNewInclusive` | `opcode(1)` – 1 byte | `..., lo, hi -> ..., lo, hi, 1` | Lays out the `..=` form - the mirror of `RangeNew`, differing only in the flag it writes. A separate opcode rather than an increment at the call site because `hi` may be `int.MaxValue`, where incrementing would wrap. |
| `0x3A` | `RangePack` | `opcode(1)` – 1 byte | `..., lo, hi, flag -> ..., ref` | Packs a range block into the heap object it presents as: what a range crossing into a one-reference slot (an array element, a dictionary key or value, an erased parameter) boxes through. The result is an ordinary registered `SurtrRange`, which every path that walks boxed values already walks and which the range's own native members read when a call reaches them. Allocates, so it routes through the safepoint like every other packing opcode. |
| `0x3B` | `RangeUnpack` | `opcode(1)` – 1 byte | `..., ref -> ..., lo, hi, flag` | The mirror of `RangePack`, on the way back out of a one-reference slot: the block replaces the reference. No allocation, so no safepoint. |


## String Operations

Strings are the one reference type Surtr compares by value, and the one with enough traffic to earn opcodes rather than calls. `StrCat` takes a count so a whole interpolation, or a chain of `+`, becomes one instruction and one allocation instead of one per join.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x77` | `StrLen` | `opcode(1)` · 1 byte | `..., str -> ..., int` | Pushes the length of a string in characters. |
| `0x78` | `StrGet` | `opcode(1)` · 1 byte | `..., str, index -> ..., char` | Reads the character at an index of a string. An out-of-range index traps as `IndexOutOfRangeException`. |
| `0x79` | `StrCat` | `opcode(1) count(1)` · 2 bytes | `..., s1, ..., sN -> ..., string` | Concatenates the top `count` strings into one. The deepest popped operand comes first in the result. `count` is at least two and at most 255. The count is the whole point of the encoding. A chain of two-operand concatenations builds every intermediate result: `a + b + c + d` allocates three strings and copies the prefix four times over, and an interpolation with n holes is that shape by construction. One instruction with a count allocates exactly one string of exactly the right length, which is what a compiler should emit for a whole `+` spine or a whole interpolation - see `SurtrCodeEmitter.StrCat(int)`. |
| `0x7A` | `StrHash` | `opcode(1)` · 1 byte | `..., str -> ..., hash` | Replaces a string with its hash. Reads the hash `SurtrString` computed once on first need and cached, so this is a load rather than a walk over the text on every use. The value is `ComputeHash`'s, which depends only on the text - that is what lets a compiler hash a `switch`'s case labels at build time and have them still match at run time, in another process. This exists for that lowering: hash, `SwitchLookup`, then `StrEQ` to settle collisions. |


## Array Operations

Allocation carries the whole parameterised type as one immediate, so a single index gives both the descriptor the object keeps and the element family its slots are initialised from. The mutating members are opcodes rather than methods on the `array` built-in because their signatures are not writable: a descriptor names one concrete type, and "the element type of whatever this array is" is not one.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x7B` | `ArrNew` | `opcode(1) typeIdx(2)` · 3 bytes | `..., size -> ..., array` | Allocates an array of the type at `typeIdx`, whose length is taken from the stack. `typeIdx` names the whole parameterised type - `AI`, `AS`, `ADIS` - not the element type alone, so one immediate carries both the descriptor the object keeps and the element family the elements are initialised from. Elements start at that family's zero: `0`, `0.0`, `false`, `'\0'` or null. |
| `0x7C` | `ArrNewX` | `opcode(1) typeIdx(2) size(4)` · 7 bytes | `... -> ..., array` | Allocates an array whose length is an immediate. Not a widened `ArrNew` but a different addressing mode - the length moves from the stack into the instruction, for arrays of statically known size. |
| `0x7D` | `ArrPack` | `opcode(1) typeIdx(2) size(2)` · 5 bytes | `..., v1, ..., vN -> ..., array` | Pops `size` values and packs them into a new array. What an array literal compiles to. The deepest popped value becomes element 0, matching `TupPack`. |
| `0x7E` | `ArrLen` | `opcode(1)` · 1 byte | `..., arr -> ..., int` | Pushes an array's length. |
| `0x7F` | `ArrGet` | `opcode(1)` · 1 byte | `..., arr, index -> ..., value` | Reads an array element. An out-of-range index traps as `IndexOutOfRangeException`. |
| `0x80` | `ArrSet` | `opcode(1)` · 1 byte | `..., arr, index, value -> ...` | Writes an array element. Consumes all three operands and pushes nothing. |
| `0x81` | `ArrPush` | `opcode(1)` · 1 byte | `..., arr, value -> ...` | Appends a value to an array, growing it. An opcode rather than a method on the `array` built-in because there is no way to write its signature - a descriptor names one concrete type, and "the element type of whatever this array is" is not expressible. The same reasoning covers every opcode from here to `ArrIndexOf`, and their dictionary counterparts. |
| `0x82` | `ArrPop` | `opcode(1)` · 1 byte | `..., arr -> ..., value` | Removes and pushes an array's last element. Popping an empty array traps. |
| `0x83` | `ArrInsert` | `opcode(1)` · 1 byte | `..., arr, index, value -> ...` | Inserts a value at an index, shifting everything after it up. An index equal to the length appends; anything beyond it traps. |
| `0x84` | `ArrRemoveAt` | `opcode(1)` · 1 byte | `..., arr, index -> ...` | Removes the element at an index, shifting everything after it down. |
| `0x85` | `ArrClear` | `opcode(1)` · 1 byte | `..., arr -> ...` | Drops every element of an array. |
| `0x86` | `ArrIndexOf` | `opcode(1)` · 1 byte | `..., arr, value -> ..., int` | Pushes the index of the first element equal to a value, or `-1`. Equality is the runtime's value semantics, not raw bits, so two distinct string objects holding the same text match. Linear scan. |
| `0x87` | `ArrIn` | `opcode(1)` · 1 byte | `..., arr, value -> ..., bool` | Tests whether an array contains a value. Linear scan, so cost grows with the array. |

(`ArrNIn` used to live here; a negated membership test lowers to the plain test plus `Inv`, so
nothing ever emitted it, and the same went for the dictionary's old `DictNIn`. The renumbering
reclaimed both values.)


## Tuple Operations

Fixed arity, immutable once packed, and element types recorded only in the packed type's descriptor. A tuple index is always statically known — an element's type depends on which one it is — so `TupGetC` carries it as an immediate and is the form a compiler emits; `TupGet` remains for a lowered `for-in`, whose index is a loop counter.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x88` | `TupPack` | `opcode(1) typeIdx(2) size(1)` · 4 bytes | `..., v1, ..., vN -> ..., tuple` | Pops `size` values and packs them into a tuple of the type at `typeIdx`. The deepest popped value becomes element 0. Caps arity at 255. `typeIdx` names the shape - `T(IS)` - which is the only place a tuple's element types are recorded, since elements carry no type of their own. |
| `0x89` | `TupUnpack` | `opcode(1) size(1)` · 2 bytes | `..., tuple -> ..., v1, ..., vN` | Expands a tuple into `size` separate stack entries. Element 0 ends up deepest, so packing and unpacking round-trip. |
| `0x8A` | `TupLen` | `opcode(1)` · 1 byte | `..., tup -> ..., int` | Pushes a tuple's arity. |
| `0x8B` | `TupGet` | `opcode(1)` · 1 byte | `..., tup, index -> ..., value` | Reads a tuple element at an index taken from the stack. There is no matching setter - tuples are immutable once packed. A written tuple index is always a constant, so this is not what an element access compiles to; `TupGetC` is. What needs this form is a lowered `for-in`, whose index is a loop counter. An out-of-range index traps as `IndexOutOfRangeException`. |
| `0x8C` | `TupGetC` | `opcode(1) index(1)` · 2 bytes | `..., tup -> ..., value` | Reads the tuple element at an immediate index. The form a compiler emits for `t.0` or `t[1]`, since a tuple index has to be a constant for the element's type to be known - which is the same reason there is no setter. One byte of immediate replaces a whole push, and the value never reaches the stack to be popped again. A tuple's arity is capped at 255 by `TupPack`, so the one-byte index reaches every element there can be and needs no wide form. An out-of-range index traps as `IndexOutOfRangeException`, as `TupGet` does. |


## Dictionary Operations

Keyed under the runtime's own value comparer, so two distinct string objects holding the same text are one key. `DictKeys` and `DictValues` name the array type they build, because deriving it from the dictionary's descriptor would mean parsing one per call.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x8D` | `DictNew` | `opcode(1) typeIdx(2)` · 3 bytes | `... -> ..., dict` | Allocates an empty dictionary of the type at `typeIdx`. `typeIdx` names the whole pair - `DIS` for `{int: string}`. |
| `0x8E` | `DictPack` | `opcode(1) typeIdx(2) count(2)` · 5 bytes | `..., k1, v1, ..., kN, vN -> ..., dict` | Pops `count` key/value pairs and packs them into a new dictionary. What a dictionary literal compiles to. Later pairs overwrite earlier ones on a duplicate key, as `DictSet` does. |
| `0x8F` | `DictLen` | `opcode(1)` · 1 byte | `..., dict -> ..., int` | Pushes the number of entries in a dictionary. |
| `0x90` | `DictGet` | `opcode(1)` · 1 byte | `..., dict, key -> ..., value` | Reads the value stored under a key. A missing key needs a defined behaviour - trap, or push null. |
| `0x91` | `DictSet` | `opcode(1)` · 1 byte | `..., dict, key, value -> ...` | Stores a value under a key, inserting or replacing. |
| `0x92` | `DictDel` | `opcode(1)` · 1 byte | `..., dict, key -> ..., bool` | Removes the entry stored under a key. Pushes whether an entry was actually removed, so a caller that does not care can drop it with `Pop` and one that does needs no second lookup. |
| `0x93` | `DictClear` | `opcode(1)` · 1 byte | `..., dict -> ...` | Drops every entry of a dictionary. |
| `0x94` | `DictKeys` | `opcode(1) typeIdx(2)` · 3 bytes | `..., dict -> ..., array` | Collects a dictionary's keys into a new array of the type at `typeIdx`. The array's own type has to be named here because it cannot be derived at run time - the dictionary knows `DIS`, but building `AI` from it would mean parsing a descriptor on every call. In the dictionary's own iteration order. |
| `0x95` | `DictValues` | `opcode(1) typeIdx(2)` · 3 bytes | `..., dict -> ..., array` | Collects a dictionary's values into a new array of the type at `typeIdx`. In the same order as `DictKeys`, so the two line up element for element. |
| `0x96` | `DictIn` | `opcode(1)` · 1 byte | `..., dict, key -> ..., bool` | Tests whether a dictionary holds a key. |


## Type Tests and Casts

One question asked three ways, because what the call site wants done on a mismatch differs: `InstanceOf` answers it, `Cast` insists on it, and `CastOrNull` accepts either answer. `CastOrNull` is what `as?` lowers to, and it is one type test where the alternative lowering costs two.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x97` | `InstanceOf` | `opcode(1) typeIdx(2)` · 3 bytes | `..., a -> ..., bool` | Tests whether the top value is an instance of the type at `typeIdx`. The type is an immediate, not a stack operand. Resolves through the class's ancestor chain for classes and its interface table for interfaces. |
| `0x98` | `InstanceOfX` | `opcode(1) typeIdx(4)` · 5 bytes | `..., a -> ..., bool` | Tests instance-of using a 4-byte type index. |
| `0x99` | `Cast` | `opcode(1) typeIdx(2)` · 3 bytes | `..., a -> ..., a` | Casts the top value to the type at `typeIdx`. The type is an immediate, not a stack operand. This is a checked reference cast: the value is unchanged on success; a failure traps as `InvalidCastException`. The non-throwing form is `CastOrNull`. |
| `0x9A` | `CastX` | `opcode(1) typeIdx(4)` · 5 bytes | `..., a -> ..., a` | Casts using a 4-byte type index. |
| `0x9B` | `CastOrNull` | `opcode(1) typeIdx(2)` · 3 bytes | `..., a -> ..., a \| null` | Keeps the top value if it is an instance of the type at `typeIdx`, and replaces it with null otherwise. What `as?` lowers to. `Cast` traps on a mismatch and `InstanceOf` discards the value to answer about it, so a non-throwing cast written from those two costs a spill to a local, two type tests and a branch. This costs one type test. A null subject stays null, which is the same answer either way, and matching resolves through the ancestor chain for a class and the interface table for a contract, exactly as `Cast` does. |
| `0x9C` | `CastOrNullX` | `opcode(1) typeIdx(4)` · 5 bytes | `..., a -> ..., a \| null` | Casts or yields null, with a 4-byte type index. |
| `0x9D` | `LoadType` | `opcode(1) typeIdx(2)` · 3 bytes | `... -> ..., type` | Pushes the `Type` value for the compile-time-known type at `typeIdx`. What the static form of `typeof` lowers to - `typeof(SomeClass)` or `typeof(ISomeInterface)`, neither of which reads any value off the stack. The type is an immediate, resolved once at module load through `typeTable[typeIdx]` exactly as `InstanceOf` resolves its own. Allocates only the first time a given type is asked for on this runtime - the runtime caches one `Type` object per class or interface, so a repeated `typeof` on the same type is a cache hit, not a fresh entity every call. |
| `0x9E` | `LoadTypeX` | `opcode(1) typeIdx(4)` · 5 bytes | `... -> ..., type` | Loads the compile-time-known type's `Type` value, with a 4-byte type index. |
| `0x9F` | `GetTypeOfValue` | `opcode(1)` · 1 byte | `..., ref -> ..., type` | Reads the class of the value on top of the stack and pushes its `Type`. What the instance form of `typeof` lowers to when the operand's static type cannot say the answer by itself - reads `.Class` off the reference exactly as `InstanceOf`'s reference half does. The subject is never checked for null, matching `FieldGet` and the native `Type.of` this replaces. A primitive operand never reaches this at all - the compiler lowers `typeof` straight to `LoadType` against that type instead, skipping both the box and this read. |


## Module Access

What `moduleof(ModulePath)` lowers to - always the static form, since `moduleof` has no instance form over an arbitrary value (§2.1). `LoadModule`/`LoadModuleX` name another module through the chunk's module access table, the same `moduleTable` `CallModule`/`CallModuleX` already read - naming a module through `moduleof` and calling into it share one interned entry, so this table now holds "modules named, not only modules called." `LoadCurrentModule` exists because a module does not reach itself through that table - the same rule `CallLocalModule` already follows for a call - so `moduleof` on the module's own path reads the owning module straight off the executing chunk instead.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xA0` | `LoadModule` | `opcode(1) moduleIdx(2)` · 3 bytes | `... -> ..., module` | Pushes the `Module` value for another module, named by its slot in the module table. The target must already be loaded and linked. Allocates only the first time a given module is asked for on this runtime - the runtime caches one `Module` object per `SurtrModule`, the same as `LoadType` does for `Type`. |
| `0xA1` | `LoadModuleX` | `opcode(1) moduleIdx(4)` · 5 bytes | `... -> ..., module` | Loads another module's `Module` value, with a 4-byte module index. |
| `0xA2` | `LoadCurrentModule` | `opcode(1)` · 1 byte | `... -> ..., module` | Pushes the `Module` value for the module this frame's chunk belongs to - what `moduleof` lowers to when the path names the same module emitting it. |


## Object Operations

Allocation only. The instance is sized from the class's slot count and zeroed; a constructor is a separate `InvokeSpecial`.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xA3` | `ObjNew` | `opcode(1) typeIdx(2)` · 3 bytes | `... -> ..., obj` | Allocates an uninitialised instance of the class at `typeIdx`. Allocation only. The instance is sized from the class's instance slot count and zeroed; a constructor still has to be invoked separately, normally with `InvokeSpecial`. Instantiating an abstract class must be rejected. |
| `0xA4` | `ObjNewX` | `opcode(1) typeIdx(4)` · 5 bytes | `... -> ..., obj` | Allocates an instance using a 4-byte type index. |


## Control Flow Operations

Every branch offset is signed and relative to the instruction *following* the branch, so a negative offset goes backwards — the shape of every loop. The compare-and-branch forms exist because nearly every condition a compiler emits feeds exactly one branch, so materialising the boolean would cost a dispatch and a stack slot for nothing. `Switch` and `SwitchLookup` are the exception to the offset rule: theirs are measured from their own opcode byte, since a variable-length instruction has no fixed "next address" at emit time.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xA5` | `JP` | `opcode(1) relativeOffset(2)` · 3 bytes | `... -> ...` | Branches unconditionally. |
| `0xA6` | `JPX` | `opcode(1) relativeOffset(4)` · 5 bytes | `... -> ...` | Branches unconditionally, with a 4-byte offset. |
| `0xA7` | `JPZ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., cond -> ...` | Branches if the popped condition is false. The offset is signed and relative to the instruction following this one, so a negative value branches backwards - the shape of every loop. |
| `0xA8` | `JPZX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., cond -> ...` | Branches if the popped condition is false, with a 4-byte offset. |
| `0xA9` | `JPNZ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., cond -> ...` | Branches if the popped condition is true. |
| `0xAA` | `JPNZX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., cond -> ...` | Branches if the popped condition is true, with a 4-byte offset. |
| `0xAB` | `JPN` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., value -> ...` | Branches if the popped value is the null reference. |
| `0xAC` | `JPNX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., value -> ...` | Branches if the popped value is null, with a 4-byte offset. |
| `0xAD` | `JPNN` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., value -> ...` | Branches if the popped value is a non-null reference. |
| `0xAE` | `JPNNX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., value -> ...` | Branches if the popped value is non-null, with a 4-byte offset. |
| `0xAF` | `JPA` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a -> ...` | Pops a value and branches if it is an absent primitive. What `??` and `?.` lower to over a nullable primitive. |
| `0xB0` | `JPAX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a -> ...` | Pops a value and branches if it is an absent primitive, with a 4-byte offset. |
| `0xB1` | `JPNA` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a -> ...` | Pops a value and branches if it is a present primitive. |
| `0xB2` | `JPNAX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a -> ...` | Pops a value and branches if it is a present primitive, with a 4-byte offset. |
| `0xB3` | `JPEQ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped integers are equal. Fuses a comparison and a branch, so the boolean never reaches the stack. This is why the whole compare-and-branch family exists alongside the plain comparisons. |
| `0xB4` | `JPEQX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped integers are equal, with a 4-byte offset. |
| `0xB5` | `JPNE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped integers differ. |
| `0xB6` | `JPNEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped integers differ, with a 4-byte offset. |
| `0xB7` | `JPGT` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped integer is greater than the top one. Taken when `a > b`. |
| `0xB8` | `JPGTX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on integer greater-than, with a 4-byte offset. |
| `0xB9` | `JPGE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped integer is greater than or equal to the top one. |
| `0xBA` | `JPGEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on integer greater-or-equal, with a 4-byte offset. |
| `0xBB` | `JPLT` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped integer is less than the top one. |
| `0xBC` | `JPLTX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on integer less-than, with a 4-byte offset. |
| `0xBD` | `JPLE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped integer is less than or equal to the top one. |
| `0xBE` | `JPLEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on integer less-or-equal, with a 4-byte offset. |
| `0xBF` | `JPFEQ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped floats are equal. Never taken when either operand is NaN. |
| `0xC0` | `JPFEQX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped floats are equal, with a 4-byte offset. |
| `0xC1` | `JPFNE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped floats differ. Always taken when either operand is NaN. |
| `0xC2` | `JPFNEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped floats differ, with a 4-byte offset. |
| `0xC3` | `JPFGT` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped float is greater than the top one. Never taken when either operand is NaN. |
| `0xC4` | `JPFGTX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on float greater-than, with a 4-byte offset. |
| `0xC5` | `JPFGE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped float is greater than or equal to the top one. Never taken when either operand is NaN. |
| `0xC6` | `JPFGEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on float greater-or-equal, with a 4-byte offset. |
| `0xC7` | `JPFLT` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped float is less than the top one. Never taken when either operand is NaN. |
| `0xC8` | `JPFLTX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on float less-than, with a 4-byte offset. |
| `0xC9` | `JPFLE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped float is less than or equal to the top one. Never taken when either operand is NaN. |
| `0xCA` | `JPFLEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on float less-or-equal, with a 4-byte offset. |
| `0xCB` | `JPREQ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped references are identical. |
| `0xCC` | `JPREQX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped references are identical, with a 4-byte offset. |
| `0xCD` | `JPRNE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped references are not identical. |
| `0xCE` | `JPRNEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped references are not identical, with a 4-byte offset. |
| `0xCF` | `JPStrEQ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped strings hold the same text. |
| `0xD0` | `JPStrEQX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped strings hold the same text, with a 4-byte offset. |
| `0xD1` | `JPStrNE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped strings hold different text. |
| `0xD2` | `JPStrNEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped strings hold different text, with a 4-byte offset. |
| `0xD3` | `JPInstanceOf` | `opcode(1) typeIdx(2) relativeOffset(2)` · 5 bytes | `..., value -> ...` | Branches if the popped value is an instance of the type at `typeIdx`. Carries two immediates, so this is the widest of the 2-byte-offset branches. Fuses `InstanceOf` with a branch, which is the shape a type switch compiles to. |
| `0xD4` | `JPInstanceOfX` | `opcode(1) typeIdx(4) relativeOffset(4)` · 9 bytes | `..., value -> ...` | Branches on instance-of, with 4-byte type index and offset. |
| `0xD5` | `Switch` | `opcode(1) low(4) count(4) defaultOffset(4) offsets(4 * count)` · 13 + 4n bytes | `..., value -> ...` | Branches through a jump table indexed by a contiguous range of integers. The popped value selects `offsets[value - low]`; anything outside `[low, low + count)` takes `defaultOffset`. One bounds check and one indexed load, whatever the number of cases - which is the whole reason a `switch` is not just a chain of `JPEQ`. Every offset here is relative to this instruction's own opcode byte, unlike the ordinary branches, which are relative to the instruction that follows them. A variable-length instruction has no fixed "next" address to measure from at emit time. The same applies to `SwitchLookup`. |
| `0xD6` | `SwitchLookup` | `opcode(1) count(4) defaultOffset(4) (key(4) offset(4)) * count` · 9 + 8n bytes | `..., value -> ...` | Branches by searching a sorted table of integer keys. The counterpart to `Switch` for sparse cases, where a dense table would be mostly padding. Keys must be sorted ascending; the interpreter binary-searches them, so lookup is logarithmic rather than the linear scan a chain of comparisons costs. Offsets are measured from this instruction's opcode byte. |


## Call Operations

Every form shares one calling convention: `argsCount` counts every incoming slot with the receiver included, `retCount` is 0 or 1, and the callee's frame starts underneath its arguments so entering a call copies nothing. There is no opcode for calling host code, without exception — where a call lands is a property of the method it names, which the interpreter reads anyway because a virtual call can resolve onto a native override. A `native fun` declared at module scope is called with `CallLocalModule`/`CallModule` exactly like a compiled one; `0xAA`/`0xAB` used to be a `CallGlobalNative`/`CallGlobalNativeX` exception for a host-defined global function living in a table of its own, and that mechanism, and its two values, are long gone - reclaimed by the renumbering.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xD7` | `CallLocalModule` | `opcode(1) functionIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., a1, ..., aN -> ..., result?` | Calls a module-level function declared in the current module. Pops exactly `argsCount` values, deepest being the first parameter, and pushes `retCount` results. Skipping the module table is what makes this the cheap case for intra-module calls. |
| `0xD8` | `CallLocalModuleX` | `opcode(1) functionIdx(4) argsCount(1) retCount(1)` · 7 bytes | `..., a1, ..., aN -> ..., result?` | Calls a function in the current module, with a 4-byte function index. |
| `0xD9` | `CallModule` | `opcode(1) moduleIdx(2) functionIdx(2) argsCount(1) retCount(1)` · 7 bytes | `..., a1, ..., aN -> ..., result?` | Calls a module-level function in another module. The target module must already be loaded and linked. |
| `0xDA` | `CallModuleX` | `opcode(1) moduleIdx(4) functionIdx(4) argsCount(1) retCount(1)` · 11 bytes | `..., a1, ..., aN -> ..., result?` | Calls a function in another module, with 4-byte module and function indices. The longest instruction in the set. |
| `0xDB` | `InvokeVirtual` | `opcode(1) methodIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., obj, a1, ..., aN -> ..., result?` | Invokes an instance method through the receiver's virtual method table. The method table entry supplies a vtable slot, so dispatch is one load plus an indirect call - the receiver's runtime class decides which override runs. A null receiver hits the CLR null check and surfaces as `NullReferenceException`. |
| `0xDC` | `InvokeSpecial` | `opcode(1) methodIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., obj, a1, ..., aN -> ..., result?` | Invokes an instance method without virtual dispatch. Binds exactly the method named in the table, ignoring any override. This is how constructors and explicit base calls are issued. |
| `0xDD` | `InvokeStatic` | `opcode(1) methodIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., a1, ..., aN -> ..., result?` | Invokes a static method. No receiver is popped. It carries no type index: the method entry already knows its declaring class, and static initializers run when their module is loaded rather than on first touch, so there is nothing for the interpreter to trigger here. |
| `0xDE` | `InvokeStaticX` | `opcode(1) methodIdx(4) argsCount(1) retCount(1)` · 7 bytes | `..., a1, ..., aN -> ..., result?` | Invokes a static method, with a 4-byte method index. |
| `0xDF` | `InvokeInterface` | `opcode(1) methodIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., obj, a1, ..., aN -> ..., result?` | Invokes a method through an interface contract. Resolves through the receiver class's interface dispatch table, which maps an interface slot to a vtable slot - one extra indirection over `InvokeVirtual`. |
| `0xE0` | `InvokeClosure` | `opcode(1) argsCount(1) retCount(1)` · 3 bytes | `..., closure, a1, ..., aN -> ..., result?` | Calls a closure taken from the stack. The only call form with no index immediate - the target comes from the stack, so it is resolved entirely at run time. A null closure hits the CLR null check and surfaces as `NullReferenceException`. |
| `0xE1` | `NewClosure` | `opcode(1) functionIdx(2) upvaluesCount(1)` · 4 bytes | `..., u1, ..., uN -> ..., closure` | Captures upvalues and builds a closure over the function at `functionIdx`. Pops exactly `upvaluesCount` values, deepest becoming upvalue 0, which is the numbering `UpValueGet` uses. Caps captures at 255. |
| `0xE2` | `NewClosureX` | `opcode(1) functionIdx(4) upvaluesCount(1)` · 6 bytes | `..., u1, ..., uN -> ..., closure` | Builds a closure using a 4-byte function index. |


## Function Operations

The zero-capture half of the closure family. A lambda that captures nothing has no per-evaluation state, so it does not need a fresh object per evaluation either - the runtime builds one `SurtrClosure` for that method, caches it and roots it, and every site that asks gets the same reference. A body that captures anything still goes through `NewClosure`.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xE3` | `NewFunction` | `opcode(1) functionIdx(2)` · 3 bytes | `... -> ..., ref` | Builds the canonical zero-capture function value for the method at `functionIdx`. The value is the one shared `SurtrClosure` for that method within this runtime - created, cached and rooted the first time any site asks - so nothing is allocated on the heap for an evaluation. It is the same type a capturing closure over the same signature has, which is what lets a lambda that captures nothing coexist with one that does under one type. The compiler emits this only when the lambda is stateless. |
| `0xE4` | `NewFunctionX` | `opcode(1) functionIdx(4)` · 5 bytes | `... -> ..., ref` | Builds a canonical function value using a 4-byte function index. |


## Return Operations

Three forms, split by how wide the result is rather than by how many results there are — there is only ever one, and whether the call site wants it is what `retCount` answers. `ReturnVoid` returns nothing, `ReturnValue` returns the one-slot case that covers every reference and every primitive, and `ReturnValues` returns a block of `n` contiguous slots for a result whose declared type is wider than one: a multi-field `value class`, or a tuple. The width is a property of the callee, so it travels in the callee's own instruction and no call site had to change to gain multi-slot returns.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xE5` | `ReturnVoid` | `opcode(1)` · 1 byte | `... -> ...` | Returns from the current function without a value. Discards the frame; anything left on its operand stack is dropped. |
| `0xE6` | `ReturnValue` | `opcode(1)` · 1 byte | `..., result -> ...` | Returns from the current function with a single value. Pops one value and hands it to the caller. A result wider than one slot returns through `ReturnValues` instead; this form is the one-slot case, which is every reference and every primitive. |
| `0xE7` | `ReturnValues` | `opcode(1) n(1)` · 2 bytes | `..., s1, ..., sn -> ...` | Returns from the current function with several contiguous values. Pops `n` contiguous slots and writes them at the frame base when the caller asked for a result (the call's `retCount` immediate is non-zero); discards them otherwise. What a method whose declared return type occupies more than one slot returns: the callee emits its own result slot count, so neither the call site nor the frame protocol changes — `retCount` still answers zero or one *results*, and the width of that one result is a fact about the callee's type. A one-slot return keeps using `ReturnValue`, and returning nothing stays `ReturnVoid`. |


## Exception Operations

One opcode, because a protected range lives in a table on the method rather than in the instruction stream — so entering a `try` emits nothing and costs nothing, and only a raise pays. `finally` is the compiler's job: emit the block on each exit path plus a catch-all that runs it and re-raises.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xE8` | `Throw` | `opcode(1)` · 1 byte | `..., exception -> ` (the frame does not continue) | Raises the object on top of the stack as an exception. Control leaves this instruction and does not come back. The interpreter unwinds frame by frame looking for a handler whose protected range covers the raising instruction and whose caught type matches, clears that frame's operand stack, pushes the exception, and resumes at the handler. There is deliberately no opcode for entering or leaving a `try`. Protected ranges live in a table on the method, so a `try` that never throws costs exactly nothing - where a push/pop-handler pair would cost two instructions on every entry. `finally` is the compiler's job: emit the block on each normal exit path, plus a catch-all handler that runs it and re-raises with this opcode. That is what javac does, and it keeps the interpreter free of a second unwinding mode. A trap the VM itself raises - a bad index, a division by zero - and an exception thrown by host code are both catchable the same way: they are wrapped as objects and unwound through the same tables. |


## Generator Operations

Seven opcodes covering `generator` and `yield` (`Language-Syntax.md` §3.7). The split between them mirrors `iterate`/`moveNext`/`current` on purpose: a generator satisfies both `IIterable<T>` and `IIterator<T>`, so a `for-in` over one can go through those contract members — the general path, for a generator travelling as an interface — or lower to these, which do the same work without an interface dispatch, a native call and a re-entry into the machine per element. It is the same division §4.2 already makes between an indexed loop and the contract.

Suspension is a **frame copy**, not a compiler-built state machine: `Yield` copies the live frame — locals plus pending operands — out of the data stack into the generator, and `GenResume` copies it back at whatever base is free then. Locals keep their indices because every access is frame-base-relative, which is what makes a frame relocatable at all. The price is the restriction every language in this family accepts: a `yield` must be lexically inside the generator, never inside something it calls, because that frame is gone by then. `docs/Plan-Generadores.md` §4 has the full rationale and the two strategies that were not chosen.

A generator is also a **coroutine**, and the rest of that surface is deliberately not here.
`send`, `raise`, `dispose` and `result` are native methods on the built-in `generator` class, reached
through the ordinary call opcodes — a `for-in` never injects a value, so none of them belongs on a
compiled hot path, and the general path a `moveNext` already takes is the right cost for them.
`return expr;` inside a generator body is `ReturnValue` with a branch the interpreter takes when the
frame carries a generator, which is a path no module written before it existed can reach, because
the binder refused `return expr;` in a generator outright.

A generator is reached through a **stub**: the compiler emits two methods per declaration, one carrying the generator's own name and declared return `Y<elem>` whose whole body is `GenNew` plus a return, and one hidden body holding the `yield`s. So a call to a generator is an ordinary call — no metadata flag, no dedicated call opcode, and `virtual`/contract dispatch works because the stub dispatches like any method.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xE9` | `GenNew` | `opcode(1) methodIdx(2) typeIdx(2) argsCount(1)` · 6 bytes | `..., a1, ..., aN -> ..., generator` | Builds a generator over the body method at `methodIdx`, without running any of it. Pops exactly `argsCount` slots, receiver included, and stores them as the generator's incoming arguments; the body does not start until something resumes it, which is what makes calling a generator function free of side effects. `methodIdx` names the compiler's hidden body method, never the stub that emits this. `typeIdx` carries the whole parameterised type (`YI`, `YS`) for the same reason `ArrNew` does: the body's own return descriptor says nothing about what it yields, and the object has to keep its type. Allocates, so it routes through the safepoint. |
| `0xEA` | `GenIterate` | `opcode(1)` · 1 byte | `..., generator -> ..., generator` | Checks that a generator has not been iterated yet, leaving it where it was. The compiled path's copy of what `iterate()` checks, emitted once per loop rather than per element. A generator object is single-use, so walking one that has already started traps with `InvalidOperationException` instead of quietly iterating nothing; restarting means calling the generator function again. |
| `0xEB` | `GenResume` | `opcode(1)` · 1 byte | `..., generator -> ..., bool` | Resumes a generator until its next `yield` or its end. `true` if the body yielded, in which case the value is held on the generator and `GenCurrent` reads it; `false` if it finished. The frame is rebuilt in the running machine rather than through a nested run, so a resume costs a block copy and a frame entry. The generator's own stack slot becomes the result slot, which is also what keeps it reachable for the whole resume. Resuming one that is already running traps with `InvalidOperationException`; resuming an exhausted one answers `false`. |
| `0xEC` | `GenCurrent` | `opcode(1)` · 1 byte | `..., generator -> ..., value` | Reads the value a generator's last `yield` produced. Only meaningful after a `GenResume` that answered `true`. Reads the same field the native `current` accessor reads, so the two paths cannot disagree about what the last element was. |
| `0xED` | `Yield` | `opcode(1)` · 1 byte | `..., value -> ` (the frame is suspended, not popped to a result) | Suspends the executing generator, handing back one value. Copies the live frame into the generator, records where to resume, writes the answer into the slot the resumer left below the frame, and returns control to whoever resumed it. To the resumer this behaves exactly like a return, which is what lets one `Yield` serve both the compiled fast path and a resume driven by host code. A value wider than one slot is boxed by the compiler on the way in, so this always moves exactly one slot. Legal only in a body reached through a resume; the compiler guarantees that by never emitting it anywhere else. |
| `0xEE` | `GenDelegate` | `opcode(1)` · 1 byte | `..., inner -> ` (the outer frame is suspended, and the inner one entered) | Delegates the executing generator to another one, and resumes that one now — what `yield from` lowers to when the operand is statically a generator. The outer generator's frame is copied out once and a link to `inner` recorded on it; from then on a resume walks the chain straight to the innermost generator that still has a frame, so an N-deep delegation costs one frame copy per element rather than N. When the inner ends, the return path finds the link and enters the outer's frame at the very same base and answer slot, so a consumer never learns a delegation happened. Delegating to an exhausted generator produces nothing and simply continues; delegating to a running one — directly or around a cycle — traps with `InvalidOperationException`. Any other iterable is lowered to a loop of ordinary `Yield`s instead, since there is no frame to link to. |
| `0xEF` | `GenResumed` | `opcode(1)` · 1 byte | `... -> ..., value` | Pushes the value the executing generator's last suspension was resumed with. What makes `yield` and `yield from` *expressions*: for a `yield` it is what `send(v)` injected, and for a `yield from` it is what the delegated-to generator returned — in both cases "the value that flowed back in when this suspension ended", which is why one opcode reads both. The statement forms emit `Yield` or `GenDelegate` alone and pay nothing for this, which is why the suspension opcodes keep the stack effect they were given rather than growing one. Every resumption that carries nothing clears the field first, so a stale injection can never be read as a fresh one. Always `unknown`: a generator declares its element (§3.7) and has nowhere to name a second type. |

---

## 8. The extended space

`0xFF` is not an instruction. It is a **prefix**: the byte after it is a `SurtrExtOpCode`, an
independent 256-value space with its own enum, its own disassembler decoder and its own nested
`switch` in the interpreter. It sits at the very top of the byte space rather than at the first
free value so the primary set stays contiguous and can keep growing upward into `0xF0`–`0xFE`.
`0xFF` *inside* the extended space is reserved as a second prefix, so the space can be extended
again without another format decision.

```
0xFF  sub(1)  <immediates>
```

Offsets are measured from the end of the instruction, prefix included, like every branch except
`Switch`.

### What may live here

A prefixed instruction costs one extra byte, one extra load and one extra indirect branch. That
was measured rather than assumed — `surtrbench --prefix-tax` runs a null experiment, two
hand-emitted bodies identical but for the dispatch path — and on a Ryzen 9800X3D under .NET 8 it
comes to **0.44–0.48 ns**, against roughly **1 ns** for a dispatch saved. Half the estimate: the
nested `switch` is a separate prediction site with a much narrower target distribution than the
main one, and the predictor exploits that.

So the admission rule is:

> **An extended opcode must save at least one dispatch.** One saved dispatch (~1 ns) against one
> prefix (~0.46 ns) wins comfortably. What loses is an opcode that saves *no* dispatch and only
> removes a type test, a tag compare or a bounds check — about 0.25 ns of saving against 0.46 ns
> of prefix.

In practice that makes this the space for **superinstructions and fusions**: a whole emitted
sequence collapsed into one instruction. A specialisation reaches it only fused with the operand
loads around it, where the fusion is what pays. Anything whose entire benefit is smaller than one
dispatch belongs in the free primary values instead — which is why those are held in reserve
rather than spent.

Every member here must also: charge the step budget through `Branched` if it transfers control;
carry a 4-byte-offset `X` twin if it branches, so jump relaxation can widen it; and take slot
operands in one byte, which the emitter guarantees by falling back to the classic sequence when a
slot does not fit.

### Naming

| Affix | Meaning |
|---|---|
| `LL` suffix | Both operands are read from frame slots rather than from the stack. |
| `LI` suffix | Left operand from a slot, right operand an immediate. |
| `Next` suffix | A loop step: increment, test, and branch backwards. Falling through is the loop's exit. |

### Instructions

**Loop steps.** The family below is the whole per-element overhead of a `for-in` in one
instruction. Each sits at the *bottom* of its loop: it steps the index, tests it, fetches the
element into a slot and branches back into the body — and falling through is the loop's exit, so
the exit label follows the instruction immediately. An indexed walk is entered by jumping straight
to the step with the index at −1, which is safe because that index is a compiler temporary; the
range family cannot do that (its counter is the one the program declared, and starting it one below
its bound would wrap at `int.MinValue`), so it keeps a header guard for the first iteration and
fuses only the step.

None of them skips a bounds check. The test that decides whether to continue *is* the test that
would have checked the read, so the read cannot be out of range — that is what makes these fusions
rather than unchecked instructions, and why the validation policy in §5 is untouched.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x00` | `Probe` | `0xFF sub(1) localIdx(1)` · 3 bytes | `... -> ..., value` | Pushes a local, exactly as `LdlS` does. The one member of this space that is not meant to be useful: the compiler never emits it, and it exists to *measure* the prefix. Running a hot loop's local loads through it instead of `LdlS` changes exactly one thing — the dispatch path — so the delta is the prefix's price with nothing else mixed in. Keeping it means that price can be re-measured on new hardware or a new backend rather than assumed from the last time anyone checked. `src/Surtr.Bench/PrefixTax.cs` is the harness. |

| `0x01` | `ArrForNext` | `0xFF sub(1) srcSlot(1) idxSlot(1) varSlot(1) offset(2)` · 7 bytes | `... -> ...` | Steps an indexed walk over an array. Increments the index, reloads the array's `Count` (the body may have pushed, and the walk is defined to see that), and while the index is in range writes the element into `varSlot` and branches back. Written out this is ten dispatches per element: `Ldl idx · Ldl src · ArrLen · JPGE end` to guard, `Ldl src · Ldl idx · ArrGet · Stl var` to read, and `IncLocal · Jump` to step. Emitted only when the loop variable occupies one slot. Measured at **−47 %** on `forIn`. |
| `0x02` | `ArrForNextX` | `... offset(4)` · 9 bytes | `... -> ...` | What relaxation rewrites `ArrForNext` into when the body it reaches back over outgrew a signed 2-byte offset. |
| `0x03` | `StrForNext` | `0xFF sub(1) srcSlot(1) idxSlot(1) varSlot(1) offset(2)` · 7 bytes | `... -> ...` | The same over a string; the element written is a character. |
| `0x04` | `StrForNextX` | `... offset(4)` · 9 bytes | `... -> ...` | The wide form. |
| `0x05` | `TupForNext` | `0xFF sub(1) srcSlot(1) idxSlot(1) varSlot(1) offset(2)` · 7 bytes | `... -> ...` | The same over a tuple. The source is the boxed tuple the lowering packs once at loop entry, since the frame has no addressing mode for a dynamic offset into a local range. |
| `0x06` | `TupForNextX` | `... offset(4)` · 9 bytes | `... -> ...` | The wide form. |
| `0x07` | `DictForNext` | `0xFF sub(1) keysSlot(1) idxSlot(1) dictSlot(1) pairSlot(1) offset(2)` · 8 bytes | `... -> ...` | The largest fusion in the set. Written out, a dictionary walk's step is **seventeen** dispatches: four to guard the index against the key snapshot, four to read the key out of it, four to look the value up, three to lay the pair into the loop variable's two slots, and two to step and jump. This does all of it — and it is the one member of the family that writes two slots, `pairSlot` and `pairSlot + 1`, because the loop variable is always a `(K, V)` pair. The specialised int-keyed store is chosen inside the body, so the `dict`-with-`int`-keys fast path costs nothing extra here. The absent-key trap is kept: the body can delete a key the snapshot still lists. Measured at **−20 %** on `forInDict`. |
| `0x08` | `DictForNextX` | `... offset(4)` · 10 bytes | `... -> ...` | The wide form. |
| `0x09` | `ForRangeNextLE` | `0xFF sub(1) varSlot(1) limitSlot(1) offset(2)` · 6 bytes | `... -> ...` | Steps a counted loop over an inclusive range: increments the loop variable **unconditionally** — which is what `IncLocal` plus a top-of-loop guard did, so the value left behind is unchanged — and branches back while it is `<=` the limit. Overflow wraps, exactly as the written-out form wrapped. Five dispatches into one. |
| `0x0A` | `ForRangeNextLEX` | `... offset(4)` · 8 bytes | `... -> ...` | The wide form. |
| `0x0B` | `ForRangeNextLT` | `0xFF sub(1) varSlot(1) limitSlot(1) offset(2)` · 6 bytes | `... -> ...` | The exclusive-bound twin: branches back while the incremented variable is `<` the limit. Two opcodes rather than one plus a normalised limit, because normalising an exclusive bound means `limit - 1`, which wraps at `int.MinValue` — the exact case the escaped-range lowering already handles by hand. The emitter knows statically which form it has. |
| `0x0C` | `ForRangeNextLTX` | `... offset(4)` · 8 bytes | `... -> ...` | The wide form. |

`docs/Plan-Opcodes-Extendidos.md` carries the cost model, the catalogue of what is planned here and
the measurement protocol behind all of it.
