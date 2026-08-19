# The Surtr instruction set

Every opcode the virtual machine executes, by family, with its numeric value, byte layout and
stack effect.

`src/Surtr.Core/Bytecode/OpCode.cs` is the source of truth and carries the same three-part
documentation on each member; this file is that content laid out for reading, plus the parts that
only make sense across the whole set. `docs/VM-Plan.md` has the *why* behind the interpreter's
shape, and `docs/Module-Format.md` describes the file these bytes live in.

**225 opcodes are defined, spanning `0x00` through `0xE6`.** Six values inside that span —
`0x2C`–`0x2F` (the old `Ldg`/`LdgX`/`Stg`/`StgX`) and `0xAA`–`0xAB` (the old
`CallGlobalNative`/`CallGlobalNativeX`) — are **retired**: they used to cover the host-globals
mechanism, which is gone now that a `native` member (module-level or on a class) is an ordinary
member reached through the same tables and call opcodes as any other. A retired value is never
reused — reusing one would make an old module silently execute a different instruction — so those
six numbers simply have no opcode and never will. The 25 values `0xE7`–`0xFF`, plus the six retired
ones, are what is free.

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
family, in the order this document presents it, and each member's value is spelled rather than
implied. That is what makes the two questions independent: where an opcode is *filed* is a
readability decision, and what it is *numbered* is the format. Implicit numbering welded the two
together — a member inserted in the middle renumbered everything after it, silently — which is why
the set used to grow at the tail regardless of family, and why its tail read as a pile of
afterthoughts.

**A new opcode takes a free value and is filed with its family.** `0xE7` through `0xFF` are
unassigned. A retired value stays retired rather than being reused, because handing an old number
to a new instruction would make an existing module execute something else entirely. There are
golden-value tests over the whole table (`src/Surtr.Tests/Bytecode/OpCodeValueTests.cs`), so
renumbering is a thing someone has to come and do on purpose.

The values were assigned once, when the set was regrouped, and that pass bumped
`SurtrModuleImage.FormatVersion` to 3 so an image written before it is refused rather than
misread.

---

## 3. The calling convention, in one place

Six opcodes take `argsCount` and `retCount` immediates, and both mean the same thing everywhere:

* **`argsCount` counts every incoming stack slot, receiver included.** On an instance call the
  receiver *is* argument 0. That is what makes the callee's frame base `sp - argsCount` for every
  kind of call — one subtraction, no branch — and what makes `Ldl0` read `this`.
* **`retCount` is 0 or 1.** Several values are returned by packing a tuple. A call site that does
  not want a result asks for none rather than popping one, so the frame protocol drops it on return.
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

Pure shuffling: no operand is interpreted, no tag is inspected. `Dup`/`Swap` come in single and double forms because a two-slot pattern is what the emitter meets when it has to keep a receiver under an argument, and doing it with two single-slot shuffles would cost two dispatches instead of one.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x01` | `Dup` | `opcode(1)` · 1 byte | `..., value -> ..., value, value` | Duplicates the value on top of the stack. |
| `0x02` | `Dup2` | `opcode(1)` · 1 byte | `..., a, b -> ..., a, b, a, b` | Duplicates the top two values, preserving their order. |
| `0x03` | `Swap` | `opcode(1)` · 1 byte | `..., a, b -> ..., b, a` | Exchanges the top two values. |
| `0x04` | `Swap2` | `opcode(1)` · 1 byte | `..., a, b, c, d -> ..., c, d, a, b` | Exchanges the top two pairs of values, keeping each pair's internal order. |
| `0x05` | `Pop` | `opcode(1)` · 1 byte | `..., value -> ...` | Discards the value on top of the stack. How a call's unused return value is dropped in statement position. |

## Constants and Literals

Everything that materialises a value out of nothing: the inline literal forms, which carry their value in the instruction itself, and the constant pool, whose first ten slots have a dedicated opcode each. A literal that fits inline never touches the pool, which is what keeps `Ldc0`…`Ldc9` available for the values that cannot — floats and strings.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x06` | `PushNull` | `opcode(1)` · 1 byte | `... -> ..., null` | Pushes the null reference. This is the null reference. The absence of a nullable primitive is `PushAbsent`, and the two carry different tags on purpose. |
| `0x07` | `PushTrue` | `opcode(1)` · 1 byte | `... -> ..., bool` | Pushes the boolean `true`. There are exactly two booleans, so each gets an opcode and neither costs a constant pool slot. The pool's first ten slots are the cheapest storage in the format and are better spent on values that cannot be pushed inline at all. |
| `0x08` | `PushFalse` | `opcode(1)` · 1 byte | `... -> ..., bool` | Pushes the boolean `false`. |
| `0x09` | `PushI8` | `opcode(1) value(1)` · 2 bytes | `... -> ..., int` | Pushes a signed 8-bit integer literal, sign-extended to a full integer value. The narrowest way to materialise a small literal without touching the constant pool. |
| `0x0A` | `PushI16` | `opcode(1) value(2)` · 3 bytes | `... -> ..., int` | Pushes a signed 16-bit integer literal, sign-extended to a full integer value. |
| `0x0B` | `PushI32` | `opcode(1) value(4)` · 5 bytes | `... -> ..., int` | Pushes a signed 32-bit integer literal. |
| `0x0C` | `PushChar` | `opcode(1) value(2)` · 3 bytes | `... -> ..., char` | Pushes a character literal, carried inline as a UTF-16 code unit. The immediate is unsigned and covers the whole code unit range, so every character literal fits inline and none reaches the constant pool. The pushed value carries the char tag, which is what decides the class it reports and which box `BoxChar` makes. |
| `0x0D` | `PushAbsent` | `opcode(1) typeCode(1)` · 2 bytes | `... -> ..., absent` | Pushes the "no value" state of a nullable primitive. The immediate is the `SurtrValueTypeCode` of the primitive that is missing, so the value can say what it is the absence of. It is never the null reference: that is `PushNull`, and the two carry different tags on purpose. |
| `0x0E` | `Ldc0` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 0. |
| `0x0F` | `Ldc1` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 1. |
| `0x10` | `Ldc2` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 2. |
| `0x11` | `Ldc3` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 3. |
| `0x12` | `Ldc4` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 4. |
| `0x13` | `Ldc5` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 5. |
| `0x14` | `Ldc6` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 6. |
| `0x15` | `Ldc7` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 7. |
| `0x16` | `Ldc8` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 8. |
| `0x17` | `Ldc9` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads constant 9. |
| `0x18` | `LdcS` | `opcode(1) constIdx(1)` · 2 bytes | `... -> ..., value` | Loads a constant using a 1-byte index, for the first 256 pool entries. |
| `0x19` | `Ldc` | `opcode(1) constIdx(2)` · 3 bytes | `... -> ..., value` | Loads the constant at `constIdx` from the chunk's constant pool. |
| `0x1A` | `LdcX` | `opcode(1) constIdx(4)` · 5 bytes | `... -> ..., value` | Loads a constant using a 4-byte index, for pools larger than 65536 entries. |

## Local Variables

The frame's own slots, and the densest family in the set — local access is the commonest instruction in any program, so slots 0–5 get an opcode with no immediate at all, slots up to 255 a one-byte form, and only past that does an index cost two bytes. `IncLocal` is the one instruction here that both reads and writes, and it exists because a counted loop is the shape that pays for it.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x1B` | `Ldl0` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 0. On an instance method this is the receiver. |
| `0x1C` | `Ldl1` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 1. |
| `0x1D` | `Ldl2` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 2. |
| `0x1E` | `Ldl3` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 3. |
| `0x1F` | `Ldl4` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 4. |
| `0x20` | `Ldl5` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 5. |
| `0x21` | `LdlS` | `opcode(1) localIdx(1)` · 2 bytes | `... -> ..., value` | Loads a local using a 1-byte index, for the first 256 slots of the frame. |
| `0x22` | `Ldl` | `opcode(1) localIdx(2)` · 3 bytes | `... -> ..., value` | Loads the local variable at `localIdx` from the current frame. |
| `0x23` | `Stl0` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 0. |
| `0x24` | `Stl1` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 1. |
| `0x25` | `Stl2` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 2. |
| `0x26` | `Stl3` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 3. |
| `0x27` | `Stl4` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 4. |
| `0x28` | `Stl5` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 5. |
| `0x29` | `StlS` | `opcode(1) localIdx(1)` · 2 bytes | `..., value -> ...` | Pops a value into a local using a 1-byte index. |
| `0x2A` | `Stl` | `opcode(1) localIdx(2)` · 3 bytes | `..., value -> ...` | Pops a value and stores it into the local at `localIdx`. |
| `0x2B` | `IncLocal` | `opcode(1) localIdx(1) delta(1)` · 3 bytes | `... -> ...` | Adds a signed 8-bit constant to an integer local, in place. The whole of `i += 1` in one instruction. Written out it is `Ldl`, `PushI8`, `Add`, `Stl` - four dispatches, up to eight bytes, and two round trips through the operand stack - for an update that never needs to leave the frame. The delta is signed, so a decrement is the same instruction. The local is read and written as an integer and comes back tagged as one; a slot holding anything else has no defined result, and the compiler is what guarantees it does not. Locals past 255, and deltas outside a signed byte, fall back to the long form - `SurtrCodeEmitter.IncrementLocal` decides which. |

## Field Operations

Instance fields reach their storage through a slot index resolved when the declaring type was linked, so a read is an offset into the instance rather than a name lookup. Statics are a separate pair rather than a flag on the same opcode, because folding them together would put a static/instance test on one of the hottest instructions in the set — and a module-level variable is a static of its module, reaching its storage the same way.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x30` | `FieldGet` | `opcode(1) fieldIdx(2)` · 3 bytes | `..., obj -> ..., value` | Reads an instance field. The field table entry carries the slot index, so the read is a direct offset into the instance rather than a name lookup. A null receiver hits the CLR null check and surfaces as `NullReferenceException`. |
| `0x31` | `FieldSet` | `opcode(1) fieldIdx(2)` · 3 bytes | `..., obj, value -> ...` | Writes an instance field. The compiler must reject this against a read-only field outside a constructor. |
| `0x32` | `StaticFieldGet` | `opcode(1) fieldIdx(2)` · 3 bytes | `... -> ..., value` | Reads a static field, or a module-level variable. No receiver, which is why this cannot be folded into `FieldGet` - doing so would put a static/instance test on one of the hottest instructions in the set. Module-level variables are the same thing: Surtr has no true globals, so a module variable is a static of its module and reaches its storage the same way. The field table entry carries the address of the slot itself, resolved when the declaring type was linked, so this is one indirect load. |
| `0x33` | `StaticFieldGetX` | `opcode(1) fieldIdx(4)` · 5 bytes | `... -> ..., value` | Reads a static field using a 4-byte field index. |
| `0x34` | `StaticFieldSet` | `opcode(1) fieldIdx(2)` · 3 bytes | `..., value -> ...` | Writes a static field, or a module-level variable. The compiler must reject this against a read-only field outside its static initializer. |
| `0x35` | `StaticFieldSetX` | `opcode(1) fieldIdx(4)` · 5 bytes | `..., value -> ...` | Writes a static field using a 4-byte field index. |

## Upvalue Operations

A closure's captured values. There is no setter, and that is a language decision rather than a gap: a capture is copied rather than shared, so the compiler only ever captures what is never reassigned.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x36` | `UpValueGet` | `opcode(1) upvalueIdx(1)` · 2 bytes | `... -> ..., value` | Reads a captured variable from the currently executing closure. Only valid inside a closure body. There is deliberately no matching setter: a capture is copied rather than shared, so the compiler only ever captures what is never reassigned. |

## Arithmetic Operations

Integer and float forms of each operation, paired. They are separate opcodes because the two representations differ and a single one would have to test a tag the compiler already knows the answer to. The untagged forms cover `int`, `bool` and `char`, which share one representation.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x37` | `Add` | `opcode(1)` · 1 byte | `..., a, b -> ..., a + b` | Integer addition. |
| `0x38` | `FAdd` | `opcode(1)` · 1 byte | `..., a, b -> ..., a + b` | Floating-point addition. |
| `0x39` | `Sub` | `opcode(1)` · 1 byte | `..., a, b -> ..., a - b` | Integer subtraction. The deeper operand is the minuend, so the result is `a - b`, not `b - a`. |
| `0x3A` | `FSub` | `opcode(1)` · 1 byte | `..., a, b -> ..., a - b` | Floating-point subtraction. |
| `0x3B` | `Mul` | `opcode(1)` · 1 byte | `..., a, b -> ..., a * b` | Integer multiplication. |
| `0x3C` | `FMul` | `opcode(1)` · 1 byte | `..., a, b -> ..., a * b` | Floating-point multiplication. |
| `0x3D` | `Div` | `opcode(1)` · 1 byte | `..., a, b -> ..., a / b` | Integer division. Division by zero has no defined result yet and needs a trap decision. |
| `0x3E` | `FDiv` | `opcode(1)` · 1 byte | `..., a, b -> ..., a / b` | Floating-point division. Division by zero follows IEEE 754 and yields an infinity or NaN rather than trapping. |
| `0x3F` | `Mod` | `opcode(1)` · 1 byte | `..., a, b -> ..., a % b` | Integer remainder. As with `Div`, a zero divisor still needs a defined behaviour. |
| `0x40` | `FMod` | `opcode(1)` · 1 byte | `..., a, b -> ..., a % b` | Floating-point remainder. |
| `0x41` | `Pow` | `opcode(1)` · 1 byte | `..., a, b -> ..., a ** b` | Integer exponentiation. Raises the deeper operand to the power of the top one. A negative exponent has no integer result and needs a defined behaviour. |
| `0x42` | `FPow` | `opcode(1)` · 1 byte | `..., a, b -> ..., a ** b` | Floating-point exponentiation. |
| `0x43` | `Neg` | `opcode(1)` · 1 byte | `..., a -> ..., -a` | Integer negation. |
| `0x44` | `FNeg` | `opcode(1)` · 1 byte | `..., a -> ..., -a` | Floating-point negation. Flips the sign bit, so it also turns zero into negative zero. |

## Bitwise and Logical Operations

The bit operations, plus the one boolean operator that is not a comparison. `Not` and `Inv` are the pair worth keeping straight: `Not` is the bitwise complement, `Inv` the logical negation.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x45` | `And` | `opcode(1)` · 1 byte | `..., a, b -> ..., a & b` | Bitwise AND. |
| `0x46` | `Or` | `opcode(1)` · 1 byte | `..., a, b -> ..., a \| b` | Bitwise OR. |
| `0x47` | `Xor` | `opcode(1)` · 1 byte | `..., a, b -> ..., a ^ b` | Bitwise exclusive OR. |
| `0x48` | `Not` | `opcode(1)` · 1 byte | `..., a -> ..., ~a` | Bitwise complement. This is the bitwise operator. The boolean negation is `Inv`. |
| `0x49` | `Shl` | `opcode(1)` · 1 byte | `..., a, b -> ..., a << b` | Left shift. Shifts the deeper operand left by the top one. Shift counts at or above the operand width still need a defined behaviour. |
| `0x4A` | `Shr` | `opcode(1)` · 1 byte | `..., a, b -> ..., a >>> b` | Logical right shift, filling with zeroes. Does not preserve the sign - a negative value becomes a large positive one. The sign-preserving form is `Sar`. |
| `0x4B` | `Sar` | `opcode(1)` · 1 byte | `..., a, b -> ..., a >> b` | Arithmetic right shift, replicating the sign bit. Keeps the sign, so a negative value stays negative. The zero-filling form is `Shr`. |
| `0x4C` | `Inv` | `opcode(1)` · 1 byte | `..., a -> ..., !a` | Logical negation of a boolean. This is the boolean operator. The bitwise complement is `Not`. |

## Comparison Operations

Five operand families, each with its own opcodes: integers (which also cover `bool` and `char`), floats under IEEE 754, references by identity, strings by text, and a still-abstract generic type parameter by the runtime's own value comparer. Strings, references and the dynamic family carry equality only — ordering a string is a call to `string.compareTo`, which is what the language says the operators mean, and an unconstrained type parameter has no ordering to give.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x4D` | `EQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Integer equality. Also covers bools and chars, which share the integer representation. |
| `0x4E` | `NE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Integer inequality. |
| `0x4F` | `GT` | `opcode(1)` · 1 byte | `..., a, b -> ..., a > b` | Integer greater-than. |
| `0x50` | `GE` | `opcode(1)` · 1 byte | `..., a, b -> ..., a >= b` | Integer greater-than-or-equal. |
| `0x51` | `LT` | `opcode(1)` · 1 byte | `..., a, b -> ..., a < b` | Integer less-than. |
| `0x52` | `LE` | `opcode(1)` · 1 byte | `..., a, b -> ..., a <= b` | Integer less-than-or-equal. |
| `0x53` | `FEQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Floating-point equality. IEEE 754 semantics, so NaN compares unequal to everything including itself. |
| `0x54` | `FNE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Floating-point inequality. NaN compares unequal to everything, so this yields true when either side is NaN. |
| `0x55` | `FGT` | `opcode(1)` · 1 byte | `..., a, b -> ..., a > b` | Floating-point greater-than. False whenever either operand is NaN. |
| `0x56` | `FGE` | `opcode(1)` · 1 byte | `..., a, b -> ..., a >= b` | Floating-point greater-than-or-equal. False whenever either operand is NaN. |
| `0x57` | `FLT` | `opcode(1)` · 1 byte | `..., a, b -> ..., a < b` | Floating-point less-than. False whenever either operand is NaN. |
| `0x58` | `FLE` | `opcode(1)` · 1 byte | `..., a, b -> ..., a <= b` | Floating-point less-than-or-equal. False whenever either operand is NaN. |
| `0x59` | `REQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Reference identity. Compares handles, not contents - two equal strings in different objects are not identical. |
| `0x5A` | `RNE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Reference non-identity. |
| `0x5B` | `StrEQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | String equality by text. The counterpart to `REQ` for the one reference type Surtr compares by value. Its own opcode rather than a call to `string.equals`, because `==` on strings is common enough that a call per comparison would show. Two null strings are equal; a null and a non-null are not. |
| `0x5C` | `StrNE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | String inequality by text. |
| `0xE4` | `DynEQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Value equality decided at runtime by each operand's own tag, for a generic type parameter's own slot. What `==` lowers to when neither operand's static type is a family with a dedicated form - a bare, still-abstract type parameter, most commonly. `==` is value equality everywhere in Surtr (§5.7 of Language-Syntax.md), which for a boxed primitive means a boxed `5` equals an unboxed `5` and two independently boxed `5`s equal each other - exactly what `REQ` gets wrong, since two different boxes are two different entities. Reaches the same `SurtrValueComparer` every dictionary and array search already uses. |
| `0xE5` | `DynNE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | The negation of `DynEQ`. |

## Null and Absence Tests

Two different questions that look alike in source and are nothing alike in the value representation. A reference is its 32-bit payload, so `IsNull` ignores the tag; a nullable primitive's absence *is* a tag, so `IsAbsent` reads nothing else. Testing one with the other's opcode would answer wrongly for an `int` of value zero.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x5D` | `IsNull` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Tests whether the top value is the null reference. |
| `0x5E` | `IsNotNull` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Tests whether the top value is a non-null reference. |
| `0x5F` | `IsAbsent` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Replaces a nullable primitive with whether it holds no value. Tests the tag, not the payload, which is exactly why it cannot be `IsNull`. A reference is its 32-bit payload, so `IsNull` ignores the tag - and an `int` of value zero would answer that test the same way a null does. |
| `0x60` | `IsPresent` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Replaces a nullable primitive with whether it holds a value. |

## Conversion Operations

The primitive conversions, all between `int` and one other family — anything else routes through `int` in two instructions, which is what the emitter's `Convert` helper does. Only `F2I` and `I2C` lose information; the rest change a tag.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x61` | `I2F` | `opcode(1)` · 1 byte | `..., a -> ..., float` | Widens an integer to a float. |
| `0x62` | `F2I` | `opcode(1)` · 1 byte | `..., a -> ..., int` | Narrows a float to an integer. Lossy, and pinned down rather than an unchecked C# cast, whose behaviour for an out-of-range double is platform-defined and would differ between x64 and ARM (§1.9). Truncates toward zero in range; saturates to `int.MinValue`/`int.MaxValue` outside it; `NaN` converts to `0`. |
| `0x63` | `I2C` | `opcode(1)` · 1 byte | `..., a -> ..., char` | Retags an integer as a character. Int, bool and char share one representation, so this changes only the value's tag and truncates the payload to 16 bits. The tag still matters: it is what decides which class the value reports and which box `BoxChar` versus `BoxInt` produces. |
| `0x64` | `C2I` | `opcode(1)` · 1 byte | `..., a -> ..., int` | Retags a character as an integer. Always exact - every character fits an integer. |
| `0x65` | `I2B` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Converts an integer to a boolean. Normalises as well as retags - any non-zero integer becomes `true`, so the payload is always 0 or 1 afterwards. That normalisation is what lets every boolean opcode treat the payload as a bit. |
| `0x66` | `B2I` | `opcode(1)` · 1 byte | `..., a -> ..., int` | Retags a boolean as an integer, giving 0 or 1. |

## Boxing Operations

Turning a primitive into a collectable reference and back. The `Box*` family carries no type index because a boxed primitive keeps the class the unboxed one already had; `BoxAs` exists for the one case where it cannot — a `value class`, which is erased to the field it wraps and so no longer says what it was. Unboxing is one opcode either way, because the box's own value carries its tag.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x67` | `BoxInt` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes an integer into a heap object. Allocates, so the result is a collectable reference rather than an inline value. |
| `0x68` | `BoxFloat` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes a float into a heap object. |
| `0x69` | `BoxBool` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes a boolean into a heap object. |
| `0x6A` | `BoxChar` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes a character into a heap object. |
| `0x6B` | `BoxAs` | `opcode(1) typeIdx(2)` · 3 bytes | `..., a -> ..., ref` | Boxes the value on top of the stack as an instance of a named class. What a `value class` boxes through. The `Box*` family carries no type index because a boxed primitive takes the class the unboxed primitive already had; a value class is erased to the field it wraps, so where it has to become a reference the class it should present as is exactly the thing the value no longer says. Unboxing is still `Unbox`: the box's own value carries its tag. |
| `0x6C` | `BoxAsX` | `opcode(1) typeIdx(4)` · 5 bytes | `..., a -> ..., ref` | Boxes as a named class, with a 4-byte type index. |
| `0x6D` | `Unbox` | `opcode(1)` · 1 byte | `..., ref -> ..., value` | Unwraps a boxed value back to its inline representation. Recovers whichever primitive was boxed - the tag on the boxed value says which, so no per-type opcode is needed on the way back. |
| `0xE3` | `BoxDynamic` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes whatever primitive is on top of the stack, chosen by its own tag rather than the compiler's. A no-op when the subject is already a reference (including `null`). Exists for a generic type parameter's own erased slot: a value crossing one through a body compiled once for every substitution of `T` may already be boxed or may still be a built-in's own raw storage, and nothing at the call site can tell which - only the value's own tag can. `Unbox` already reads that tag on the way back out; this is its mirror on the way in. |
| `0xE6` | `UnboxDynamic` | `opcode(1)` · 1 byte | `..., a -> ..., value` | The mirror of `BoxDynamic`: unboxes only a boxed primitive, leaving everything else (a raw primitive, or a reference that is not a box at all - an ordinary object, array, string) untouched. Exists for the write side of the same erased slot `BoxDynamic` reads from: a value statically typed by a bare generic parameter arrives already boxed, but the collection's own native storage it is written into was never boxed to begin with and must not become the one element that is. |

## Type Tests and Casts

One question asked three ways, because what the call site wants done on a mismatch differs: `InstanceOf` answers it, `Cast` insists on it, and `CastOrNull` accepts either answer. `CastOrNull` is what `as?` lowers to, and it is one type test where the alternative lowering costs two.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x6E` | `InstanceOf` | `opcode(1) typeIdx(2)` · 3 bytes | `..., a -> ..., bool` | Tests whether the top value is an instance of the type at `typeIdx`. The type is an immediate, not a stack operand. Resolves through the class's ancestor chain for classes and its interface table for interfaces. |
| `0x6F` | `InstanceOfX` | `opcode(1) typeIdx(4)` · 5 bytes | `..., a -> ..., bool` | Tests instance-of using a 4-byte type index. |
| `0x70` | `Cast` | `opcode(1) typeIdx(2)` · 3 bytes | `..., a -> ..., a` | Casts the top value to the type at `typeIdx`. The type is an immediate, not a stack operand. This is a checked reference cast: the value is unchanged on success; a failure traps as `InvalidCastException`. The non-throwing form is `CastOrNull`. |
| `0x71` | `CastX` | `opcode(1) typeIdx(4)` · 5 bytes | `..., a -> ..., a` | Casts using a 4-byte type index. |
| `0x72` | `CastOrNull` | `opcode(1) typeIdx(2)` · 3 bytes | `..., a -> ..., a \| null` | Keeps the top value if it is an instance of the type at `typeIdx`, and replaces it with null otherwise. What `as?` lowers to. `Cast` traps on a mismatch and `InstanceOf` discards the value to answer about it, so a non-throwing cast written from those two costs a spill to a local, two type tests and a branch. This costs one type test. A null subject stays null, which is the same answer either way, and matching resolves through the ancestor chain for a class and the interface table for a contract, exactly as `Cast` does. |
| `0x73` | `CastOrNullX` | `opcode(1) typeIdx(4)` · 5 bytes | `..., a -> ..., a \| null` | Casts or yields null, with a 4-byte type index. |
| `0xDD` | `LoadType` | `opcode(1) typeIdx(2)` · 3 bytes | `... -> ..., type` | Pushes the `Type` value for the compile-time-known type at `typeIdx`. What the static form of `typeof` lowers to - `typeof(SomeClass)` or `typeof(ISomeInterface)`, neither of which reads any value off the stack. The type is an immediate, resolved once at module load through `typeTable[typeIdx]` exactly as `InstanceOf` resolves its own. Allocates only the first time a given type is asked for on this runtime - the runtime caches one `Type` object per class or interface, so a repeated `typeof` on the same type is a cache hit, not a fresh entity every call. |
| `0xDE` | `LoadTypeX` | `opcode(1) typeIdx(4)` · 5 bytes | `... -> ..., type` | Loads the compile-time-known type's `Type` value, with a 4-byte type index. |
| `0xDF` | `GetTypeOfValue` | `opcode(1)` · 1 byte | `..., ref -> ..., type` | Reads the class of the value on top of the stack and pushes its `Type`. What the instance form of `typeof` lowers to when the operand's static type cannot say the answer by itself - reads `.Class` off the reference exactly as `InstanceOf`'s reference half does. The subject is never checked for null, matching `FieldGet` and the native `Type.of` this replaces. A primitive operand never reaches this at all - the compiler lowers `typeof` straight to `LoadType` against that type instead, skipping both the box and this read. |

## Module Access

What `moduleof(ModulePath)` lowers to - always the static form, since `moduleof` has no instance form over an arbitrary value (§2.1). `LoadModule`/`LoadModuleX` name another module through the chunk's module access table, the same `moduleTable` `CallModule`/`CallModuleX` already read - naming a module through `moduleof` and calling into it share one interned entry, so this table now holds "modules named, not only modules called." `LoadCurrentModule` exists because a module does not reach itself through that table - the same rule `CallLocalModule` already follows for a call - so `moduleof` on the module's own path reads the owning module straight off the executing chunk instead.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xE0` | `LoadModule` | `opcode(1) moduleIdx(2)` · 3 bytes | `... -> ..., module` | Pushes the `Module` value for another module, named by its slot in the module table. The target must already be loaded and linked. Allocates only the first time a given module is asked for on this runtime - the runtime caches one `Module` object per `SurtrModule`, the same as `LoadType` does for `Type`. |
| `0xE1` | `LoadModuleX` | `opcode(1) moduleIdx(4)` · 5 bytes | `... -> ..., module` | Loads another module's `Module` value, with a 4-byte module index. |
| `0xE2` | `LoadCurrentModule` | `opcode(1)` · 1 byte | `... -> ..., module` | Pushes the `Module` value for the module this frame's chunk belongs to - what `moduleof` lowers to when the path names the same module emitting it. |

## Control Flow Operations

Every branch offset is signed and relative to the instruction *following* the branch, so a negative offset goes backwards — the shape of every loop. The compare-and-branch forms exist because nearly every condition a compiler emits feeds exactly one branch, so materialising the boolean would cost a dispatch and a stack slot for nothing. `Switch` and `SwitchLookup` are the exception to the offset rule: theirs are measured from their own opcode byte, since a variable-length instruction has no fixed "next address" at emit time.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x74` | `JP` | `opcode(1) relativeOffset(2)` · 3 bytes | `... -> ...` | Branches unconditionally. |
| `0x75` | `JPX` | `opcode(1) relativeOffset(4)` · 5 bytes | `... -> ...` | Branches unconditionally, with a 4-byte offset. |
| `0x76` | `JPZ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., cond -> ...` | Branches if the popped condition is false. The offset is signed and relative to the instruction following this one, so a negative value branches backwards - the shape of every loop. |
| `0x77` | `JPZX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., cond -> ...` | Branches if the popped condition is false, with a 4-byte offset. |
| `0x78` | `JPNZ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., cond -> ...` | Branches if the popped condition is true. |
| `0x79` | `JPNZX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., cond -> ...` | Branches if the popped condition is true, with a 4-byte offset. |
| `0x7A` | `JPN` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., value -> ...` | Branches if the popped value is the null reference. |
| `0x7B` | `JPNX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., value -> ...` | Branches if the popped value is null, with a 4-byte offset. |
| `0x7C` | `JPNN` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., value -> ...` | Branches if the popped value is a non-null reference. |
| `0x7D` | `JPNNX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., value -> ...` | Branches if the popped value is non-null, with a 4-byte offset. |
| `0x7E` | `JPA` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a -> ...` | Pops a value and branches if it is an absent primitive. What `??` and `?.` lower to over a nullable primitive. |
| `0x7F` | `JPAX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a -> ...` | Pops a value and branches if it is an absent primitive, with a 4-byte offset. |
| `0x80` | `JPNA` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a -> ...` | Pops a value and branches if it is a present primitive. |
| `0x81` | `JPNAX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a -> ...` | Pops a value and branches if it is a present primitive, with a 4-byte offset. |
| `0x82` | `JPEQ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped integers are equal. Fuses a comparison and a branch, so the boolean never reaches the stack. This is why the whole compare-and-branch family exists alongside the plain comparisons. |
| `0x83` | `JPEQX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped integers are equal, with a 4-byte offset. |
| `0x84` | `JPNE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped integers differ. |
| `0x85` | `JPNEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped integers differ, with a 4-byte offset. |
| `0x86` | `JPGT` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped integer is greater than the top one. Taken when `a > b`. |
| `0x87` | `JPGTX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on integer greater-than, with a 4-byte offset. |
| `0x88` | `JPGE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped integer is greater than or equal to the top one. |
| `0x89` | `JPGEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on integer greater-or-equal, with a 4-byte offset. |
| `0x8A` | `JPLT` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped integer is less than the top one. |
| `0x8B` | `JPLTX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on integer less-than, with a 4-byte offset. |
| `0x8C` | `JPLE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped integer is less than or equal to the top one. |
| `0x8D` | `JPLEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on integer less-or-equal, with a 4-byte offset. |
| `0x8E` | `JPFEQ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped floats are equal. Never taken when either operand is NaN. |
| `0x8F` | `JPFEQX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped floats are equal, with a 4-byte offset. |
| `0x90` | `JPFNE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped floats differ. Always taken when either operand is NaN. |
| `0x91` | `JPFNEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped floats differ, with a 4-byte offset. |
| `0x92` | `JPFGT` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped float is greater than the top one. Never taken when either operand is NaN. |
| `0x93` | `JPFGTX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on float greater-than, with a 4-byte offset. |
| `0x94` | `JPFGE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped float is greater than or equal to the top one. Never taken when either operand is NaN. |
| `0x95` | `JPFGEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on float greater-or-equal, with a 4-byte offset. |
| `0x96` | `JPFLT` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped float is less than the top one. Never taken when either operand is NaN. |
| `0x97` | `JPFLTX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on float less-than, with a 4-byte offset. |
| `0x98` | `JPFLE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped float is less than or equal to the top one. Never taken when either operand is NaN. |
| `0x99` | `JPFLEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on float less-or-equal, with a 4-byte offset. |
| `0x9A` | `JPREQ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped references are identical. |
| `0x9B` | `JPREQX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped references are identical, with a 4-byte offset. |
| `0x9C` | `JPRNE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped references are not identical. |
| `0x9D` | `JPRNEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped references are not identical, with a 4-byte offset. |
| `0x9E` | `JPStrEQ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped strings hold the same text. |
| `0x9F` | `JPStrEQX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped strings hold the same text, with a 4-byte offset. |
| `0xA0` | `JPStrNE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped strings hold different text. |
| `0xA1` | `JPStrNEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped strings hold different text, with a 4-byte offset. |
| `0xA2` | `JPInstanceOf` | `opcode(1) typeIdx(2) relativeOffset(2)` · 5 bytes | `..., value -> ...` | Branches if the popped value is an instance of the type at `typeIdx`. Carries two immediates, so this is the widest of the 2-byte-offset branches. Fuses `InstanceOf` with a branch, which is the shape a type switch compiles to. |
| `0xA3` | `JPInstanceOfX` | `opcode(1) typeIdx(4) relativeOffset(4)` · 9 bytes | `..., value -> ...` | Branches on instance-of, with 4-byte type index and offset. |
| `0xA4` | `Switch` | `opcode(1) low(4) count(4) defaultOffset(4) offsets(4 * count)` · 13 + 4n bytes | `..., value -> ...` | Branches through a jump table indexed by a contiguous range of integers. The popped value selects `offsets[value - low]`; anything outside `[low, low + count)` takes `defaultOffset`. One bounds check and one indexed load, whatever the number of cases - which is the whole reason a `switch` is not just a chain of `JPEQ`. Every offset here is relative to this instruction's own opcode byte, unlike the ordinary branches, which are relative to the instruction that follows them. A variable-length instruction has no fixed "next" address to measure from at emit time. The same applies to `SwitchLookup`. |
| `0xA5` | `SwitchLookup` | `opcode(1) count(4) defaultOffset(4) (key(4) offset(4)) * count` · 9 + 8n bytes | `..., value -> ...` | Branches by searching a sorted table of integer keys. The counterpart to `Switch` for sparse cases, where a dense table would be mostly padding. Keys must be sorted ascending; the interpreter binary-searches them, so lookup is logarithmic rather than the linear scan a chain of comparisons costs. Offsets are measured from this instruction's opcode byte. |

## Call Operations

Every form shares one calling convention: `argsCount` counts every incoming slot with the receiver included, `retCount` is 0 or 1, and the callee's frame starts underneath its arguments so entering a call copies nothing. There is no opcode for calling host code, without exception — where a call lands is a property of the method it names, which the interpreter reads anyway because a virtual call can resolve onto a native override. A `native fun` declared at module scope is called with `CallLocalModule`/`CallModule` exactly like a compiled one; `0xAA`/`0xAB` used to be a `CallGlobalNative`/`CallGlobalNativeX` exception for a host-defined global function living in a table of its own, and are retired along with the rest of that mechanism.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xA6` | `CallLocalModule` | `opcode(1) functionIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., a1, ..., aN -> ..., result?` | Calls a module-level function declared in the current module. Pops exactly `argsCount` values, deepest being the first parameter, and pushes `retCount` results. Skipping the module table is what makes this the cheap case for intra-module calls. |
| `0xA7` | `CallLocalModuleX` | `opcode(1) functionIdx(4) argsCount(1) retCount(1)` · 7 bytes | `..., a1, ..., aN -> ..., result?` | Calls a function in the current module, with a 4-byte function index. |
| `0xA8` | `CallModule` | `opcode(1) moduleIdx(2) functionIdx(2) argsCount(1) retCount(1)` · 7 bytes | `..., a1, ..., aN -> ..., result?` | Calls a module-level function in another module. The target module must already be loaded and linked. |
| `0xA9` | `CallModuleX` | `opcode(1) moduleIdx(4) functionIdx(4) argsCount(1) retCount(1)` · 11 bytes | `..., a1, ..., aN -> ..., result?` | Calls a function in another module, with 4-byte module and function indices. The longest instruction in the set. |
| `0xAC` | `InvokeVirtual` | `opcode(1) methodIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., obj, a1, ..., aN -> ..., result?` | Invokes an instance method through the receiver's virtual method table. The method table entry supplies a vtable slot, so dispatch is one load plus an indirect call - the receiver's runtime class decides which override runs. A null receiver hits the CLR null check and surfaces as `NullReferenceException`. |
| `0xAD` | `InvokeSpecial` | `opcode(1) methodIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., obj, a1, ..., aN -> ..., result?` | Invokes an instance method without virtual dispatch. Binds exactly the method named in the table, ignoring any override. This is how constructors and explicit base calls are issued. |
| `0xAE` | `InvokeStatic` | `opcode(1) methodIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., a1, ..., aN -> ..., result?` | Invokes a static method. No receiver is popped. It carries no type index: the method entry already knows its declaring class, and static initializers run when their module is loaded rather than on first touch, so there is nothing for the interpreter to trigger here. |
| `0xAF` | `InvokeStaticX` | `opcode(1) methodIdx(4) argsCount(1) retCount(1)` · 7 bytes | `..., a1, ..., aN -> ..., result?` | Invokes a static method, with a 4-byte method index. |
| `0xB0` | `InvokeInterface` | `opcode(1) methodIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., obj, a1, ..., aN -> ..., result?` | Invokes a method through an interface contract. Resolves through the receiver class's interface dispatch table, which maps an interface slot to a vtable slot - one extra indirection over `InvokeVirtual`. |
| `0xB1` | `InvokeClosure` | `opcode(1) argsCount(1) retCount(1)` · 3 bytes | `..., closure, a1, ..., aN -> ..., result?` | Calls a closure taken from the stack. The only call form with no index immediate - the target comes from the stack, so it is resolved entirely at run time. A null closure hits the CLR null check and surfaces as `NullReferenceException`. |
| `0xB2` | `NewClosure` | `opcode(1) functionIdx(2) upvaluesCount(1)` · 4 bytes | `..., u1, ..., uN -> ..., closure` | Captures upvalues and builds a closure over the function at `functionIdx`. Pops exactly `upvaluesCount` values, deepest becoming upvalue 0, which is the numbering `UpValueGet` uses. Caps captures at 255. |
| `0xB3` | `NewClosureX` | `opcode(1) functionIdx(4) upvaluesCount(1)` · 6 bytes | `..., u1, ..., uN -> ..., closure` | Builds a closure using a 4-byte function index. |

## Return Operations

Two forms rather than one with a count, because the count is known at every call site and the frame protocol already carries `retCount`. Several values are returned by packing a tuple.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xB4` | `ReturnVoid` | `opcode(1)` · 1 byte | `... -> ...` | Returns from the current function without a value. Discards the frame; anything left on its operand stack is dropped. |
| `0xB5` | `ReturnValue` | `opcode(1)` · 1 byte | `..., result -> ...` | Returns from the current function with a single value. Pops one value and hands it to the caller. Returning several values means packing them into a tuple first, since there is no multi-value return instruction. |

## Exception Operations

One opcode, because a protected range lives in a table on the method rather than in the instruction stream — so entering a `try` emits nothing and costs nothing, and only a raise pays. `finally` is the compiler's job: emit the block on each exit path plus a catch-all that runs it and re-raises.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xB6` | `Throw` | `opcode(1)` · 1 byte | `..., exception -> ` (the frame does not continue) | Raises the object on top of the stack as an exception. Control leaves this instruction and does not come back. The interpreter unwinds frame by frame looking for a handler whose protected range covers the raising instruction and whose caught type matches, clears that frame's operand stack, pushes the exception, and resumes at the handler. There is deliberately no opcode for entering or leaving a `try`. Protected ranges live in a table on the method, so a `try` that never throws costs exactly nothing - where a push/pop-handler pair would cost two instructions on every entry. `finally` is the compiler's job: emit the block on each normal exit path, plus a catch-all handler that runs it and re-raises with this opcode. That is what javac does, and it keeps the interpreter free of a second unwinding mode. A trap the VM itself raises - a bad index, a division by zero - and an exception thrown by host code are both catchable the same way: they are wrapped as objects and unwound through the same tables. |

## Object Operations

Allocation only. The instance is sized from the class's slot count and zeroed; a constructor is a separate `InvokeSpecial`.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xB7` | `ObjNew` | `opcode(1) typeIdx(2)` · 3 bytes | `... -> ..., obj` | Allocates an uninitialised instance of the class at `typeIdx`. Allocation only. The instance is sized from the class's instance slot count and zeroed; a constructor still has to be invoked separately, normally with `InvokeSpecial`. Instantiating an abstract class must be rejected. |
| `0xB8` | `ObjNewX` | `opcode(1) typeIdx(4)` · 5 bytes | `... -> ..., obj` | Allocates an instance using a 4-byte type index. |

## String Operations

Strings are the one reference type Surtr compares by value, and the one with enough traffic to earn opcodes rather than calls. `StrCat` takes a count so a whole interpolation, or a chain of `+`, becomes one instruction and one allocation instead of one per join.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xB9` | `StrLen` | `opcode(1)` · 1 byte | `..., str -> ..., int` | Pushes the length of a string in characters. |
| `0xBA` | `StrGet` | `opcode(1)` · 1 byte | `..., str, index -> ..., char` | Reads the character at an index of a string. An out-of-range index traps as `IndexOutOfRangeException`. |
| `0xBB` | `StrCat` | `opcode(1) count(1)` · 2 bytes | `..., s1, ..., sN -> ..., string` | Concatenates the top `count` strings into one. The deepest popped operand comes first in the result. `count` is at least two and at most 255. The count is the whole point of the encoding. A chain of two-operand concatenations builds every intermediate result: `a + b + c + d` allocates three strings and copies the prefix four times over, and an interpolation with n holes is that shape by construction. One instruction with a count allocates exactly one string of exactly the right length, which is what a compiler should emit for a whole `+` spine or a whole interpolation - see `SurtrCodeEmitter.StrCat(int)`. |
| `0xBC` | `StrHash` | `opcode(1)` · 1 byte | `..., str -> ..., hash` | Replaces a string with its hash. Reads the hash `SurtrString` computed once on first need and cached, so this is a load rather than a walk over the text on every use. The value is `ComputeHash`'s, which depends only on the text - that is what lets a compiler hash a `switch`'s case labels at build time and have them still match at run time, in another process. This exists for that lowering: hash, `SwitchLookup`, then `StrEQ` to settle collisions. |

## Array Operations

Allocation carries the whole parameterised type as one immediate, so a single index gives both the descriptor the object keeps and the element family its slots are initialised from. The mutating members are opcodes rather than methods on the `array` built-in because their signatures are not writable: a descriptor names one concrete type, and "the element type of whatever this array is" is not one.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xBD` | `ArrNew` | `opcode(1) typeIdx(2)` · 3 bytes | `..., size -> ..., array` | Allocates an array of the type at `typeIdx`, whose length is taken from the stack. `typeIdx` names the whole parameterised type - `AI`, `AS`, `ADIS` - not the element type alone, so one immediate carries both the descriptor the object keeps and the element family the elements are initialised from. Elements start at that family's zero: `0`, `0.0`, `false`, `'\0'` or null. |
| `0xBE` | `ArrNewX` | `opcode(1) typeIdx(2) size(4)` · 7 bytes | `... -> ..., array` | Allocates an array whose length is an immediate. Not a widened `ArrNew` but a different addressing mode - the length moves from the stack into the instruction, for arrays of statically known size. |
| `0xBF` | `ArrPack` | `opcode(1) typeIdx(2) size(2)` · 5 bytes | `..., v1, ..., vN -> ..., array` | Pops `size` values and packs them into a new array. What an array literal compiles to. The deepest popped value becomes element 0, matching `TupPack`. |
| `0xC0` | `ArrLen` | `opcode(1)` · 1 byte | `..., arr -> ..., int` | Pushes an array's length. |
| `0xC1` | `ArrGet` | `opcode(1)` · 1 byte | `..., arr, index -> ..., value` | Reads an array element. An out-of-range index traps as `IndexOutOfRangeException`. |
| `0xC2` | `ArrSet` | `opcode(1)` · 1 byte | `..., arr, index, value -> ...` | Writes an array element. Consumes all three operands and pushes nothing. |
| `0xC3` | `ArrPush` | `opcode(1)` · 1 byte | `..., arr, value -> ...` | Appends a value to an array, growing it. An opcode rather than a method on the `array` built-in because there is no way to write its signature - a descriptor names one concrete type, and "the element type of whatever this array is" is not expressible. The same reasoning covers every opcode from here to `ArrIndexOf`, and their dictionary counterparts. |
| `0xC4` | `ArrPop` | `opcode(1)` · 1 byte | `..., arr -> ..., value` | Removes and pushes an array's last element. Popping an empty array traps. |
| `0xC5` | `ArrInsert` | `opcode(1)` · 1 byte | `..., arr, index, value -> ...` | Inserts a value at an index, shifting everything after it up. An index equal to the length appends; anything beyond it traps. |
| `0xC6` | `ArrRemoveAt` | `opcode(1)` · 1 byte | `..., arr, index -> ...` | Removes the element at an index, shifting everything after it down. |
| `0xC7` | `ArrClear` | `opcode(1)` · 1 byte | `..., arr -> ...` | Drops every element of an array. |
| `0xC8` | `ArrIndexOf` | `opcode(1)` · 1 byte | `..., arr, value -> ..., int` | Pushes the index of the first element equal to a value, or `-1`. Equality is the runtime's value semantics, not raw bits, so two distinct string objects holding the same text match. Linear scan. |
| `0xC9` | `ArrIn` | `opcode(1)` · 1 byte | `..., arr, value -> ..., bool` | Tests whether an array contains a value. Linear scan, so cost grows with the array. |
| `0xCA` | `ArrNIn` | `opcode(1)` · 1 byte | `..., arr, value -> ..., bool` | Tests whether an array does not contain a value. Exists as its own opcode so the negated form costs no extra instruction. |

## Tuple Operations

Fixed arity, immutable once packed, and element types recorded only in the packed type's descriptor. A tuple index is always statically known — an element's type depends on which one it is — so `TupGetC` carries it as an immediate and is the form a compiler emits; `TupGet` remains for a lowered `for-in`, whose index is a loop counter.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xCB` | `TupPack` | `opcode(1) typeIdx(2) size(1)` · 4 bytes | `..., v1, ..., vN -> ..., tuple` | Pops `size` values and packs them into a tuple of the type at `typeIdx`. The deepest popped value becomes element 0. Caps arity at 255. `typeIdx` names the shape - `T(IS)` - which is the only place a tuple's element types are recorded, since elements carry no type of their own. |
| `0xCC` | `TupUnpack` | `opcode(1) size(1)` · 2 bytes | `..., tuple -> ..., v1, ..., vN` | Expands a tuple into `size` separate stack entries. Element 0 ends up deepest, so packing and unpacking round-trip. |
| `0xCD` | `TupLen` | `opcode(1)` · 1 byte | `..., tup -> ..., int` | Pushes a tuple's arity. |
| `0xCE` | `TupGet` | `opcode(1)` · 1 byte | `..., tup, index -> ..., value` | Reads a tuple element at an index taken from the stack. There is no matching setter - tuples are immutable once packed. A written tuple index is always a constant, so this is not what an element access compiles to; `TupGetC` is. What needs this form is a lowered `for-in`, whose index is a loop counter. An out-of-range index traps as `IndexOutOfRangeException`. |
| `0xCF` | `TupGetC` | `opcode(1) index(1)` · 2 bytes | `..., tup -> ..., value` | Reads the tuple element at an immediate index. The form a compiler emits for `t.0` or `t[1]`, since a tuple index has to be a constant for the element's type to be known - which is the same reason there is no setter. One byte of immediate replaces a whole push, and the value never reaches the stack to be popped again. A tuple's arity is capped at 255 by `TupPack`, so the one-byte index reaches every element there can be and needs no wide form. An out-of-range index traps as `IndexOutOfRangeException`, as `TupGet` does. |

## Dictionary Operations

Keyed under the runtime's own value comparer, so two distinct string objects holding the same text are one key. `DictKeys` and `DictValues` name the array type they build, because deriving it from the dictionary's descriptor would mean parsing one per call.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xD0` | `DictNew` | `opcode(1) typeIdx(2)` · 3 bytes | `... -> ..., dict` | Allocates an empty dictionary of the type at `typeIdx`. `typeIdx` names the whole pair - `DIS` for `{int: string}`. |
| `0xD1` | `DictPack` | `opcode(1) typeIdx(2) count(2)` · 5 bytes | `..., k1, v1, ..., kN, vN -> ..., dict` | Pops `count` key/value pairs and packs them into a new dictionary. What a dictionary literal compiles to. Later pairs overwrite earlier ones on a duplicate key, as `DictSet` does. |
| `0xD2` | `DictLen` | `opcode(1)` · 1 byte | `..., dict -> ..., int` | Pushes the number of entries in a dictionary. |
| `0xD3` | `DictGet` | `opcode(1)` · 1 byte | `..., dict, key -> ..., value` | Reads the value stored under a key. A missing key needs a defined behaviour - trap, or push null. |
| `0xD4` | `DictSet` | `opcode(1)` · 1 byte | `..., dict, key, value -> ...` | Stores a value under a key, inserting or replacing. |
| `0xD5` | `DictDel` | `opcode(1)` · 1 byte | `..., dict, key -> ..., bool` | Removes the entry stored under a key. Pushes whether an entry was actually removed, so a caller that does not care can drop it with `Pop` and one that does needs no second lookup. |
| `0xD6` | `DictClear` | `opcode(1)` · 1 byte | `..., dict -> ...` | Drops every entry of a dictionary. |
| `0xD7` | `DictKeys` | `opcode(1) typeIdx(2)` · 3 bytes | `..., dict -> ..., array` | Collects a dictionary's keys into a new array of the type at `typeIdx`. The array's own type has to be named here because it cannot be derived at run time - the dictionary knows `DIS`, but building `AI` from it would mean parsing a descriptor on every call. In the dictionary's own iteration order. |
| `0xD8` | `DictValues` | `opcode(1) typeIdx(2)` · 3 bytes | `..., dict -> ..., array` | Collects a dictionary's values into a new array of the type at `typeIdx`. In the same order as `DictKeys`, so the two line up element for element. |
| `0xD9` | `DictIn` | `opcode(1)` · 1 byte | `..., dict, key -> ..., bool` | Tests whether a dictionary holds a key. |
| `0xDA` | `DictNIn` | `opcode(1)` · 1 byte | `..., dict, key -> ..., bool` | Tests whether a dictionary does not hold a key. |

## Range Operations

A range that genuinely escapes into a variable, a parameter or a return. One written inline in a `for-in` header never reaches these — the compiler lowers that to a counted loop over two ints.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xDB` | `RangeNew` | `opcode(1)` · 1 byte | `..., lo, hi -> ..., ref` | Builds a range from two int bounds, excluding the upper one. Allocates. A range written inline in a `for-in` header must never reach this - the compiler lowers that to a counted loop over two ints - so this is for a range that genuinely escapes into a variable, a parameter or a return. |
| `0xDC` | `RangeNewInclusive` | `opcode(1)` · 1 byte | `..., lo, hi -> ..., ref` | Builds a range from two int bounds, including the upper one. The `..=` form. A separate opcode rather than an increment at the call site because `hi` may be `int.MaxValue`, where incrementing would wrap. |
