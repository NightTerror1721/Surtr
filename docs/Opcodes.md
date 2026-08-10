# The Surtr instruction set

Every opcode the virtual machine executes, by family, with its numeric value, byte layout and
stack effect.

`src/Surtr.Core/Bytecode/OpCode.cs` is the source of truth and carries the same three-part
documentation on each member; this file is that content laid out for reading, plus the parts that
only make sense across the whole set. `docs/VM-Plan.md` has the *why* behind the interpreter's
shape, and `docs/Module-Format.md` describes the file these bytes live in.

**214 opcodes are defined, `0x00` through `0xD5`. The 42 values `0xD6`–`0xFF` are free.**

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

The enum's members are implicitly numbered from `Nop = 0x00`, and **that number is the on-disk
encoding**. Inserting a member in the middle renumbers everything after it and silently invalidates
every already-compiled module.

**The set is therefore append-only.** `Bytecode/Image/` writes chunks to bytes
(`docs/Module-Format.md`), so every value in this document is on disk somewhere. A new opcode goes
at the **end**, whatever family it belongs to — which is why the tail of the enum reads less tidily
than its middle, and why the tables below are ordered by family rather than by value.

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

**There is no separate opcode for calling host code.** Where a call lands is a property of the
method the call site names, not of the call site, and the interpreter reads it anyway because a
virtual call can resolve onto a native override. Every `Invoke` and `Call` reaches bytecode and host
bodies alike. `CallGlobalNative` is the exception, and only because host globals live in a different
*table*, not because they are native.

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

The one instruction with a fixed numeric value, because it is the one whose value anything else might assume.

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
| `0x05` | `PushNull` | `opcode(1)` · 1 byte | `... -> ..., null` | Pushes the null reference. |
| `0x06` | `PushI8` | `opcode(1) value(1)` · 2 bytes | `... -> ..., int` | Pushes a signed 8-bit integer literal, sign-extended to a full integer value. The narrowest way to materialise a small literal without touching the constant pool. |
| `0x07` | `PushI16` | `opcode(1) value(2)` · 3 bytes | `... -> ..., int` | Pushes a signed 16-bit integer literal, sign-extended to a full integer value. |
| `0x08` | `PushI32` | `opcode(1) value(4)` · 5 bytes | `... -> ..., int` | Pushes a signed 32-bit integer literal. |
| `0x09` | `Pop` | `opcode(1)` · 1 byte | `..., value -> ...` | Discards the value on top of the stack. How a call's unused return value is dropped in statement position. |

## Load / Store Operations

Reading and writing the three storages a frame can reach: the constant pool, the frame's own locals, and the host's globals. The density of this family is the point — a local access is the commonest instruction in any program, so slots 0–5 get a dedicated opcode with no immediate at all, slots up to 255 get a one-byte form, and only past that does an index cost two bytes.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x0A` | `Ldc` | `opcode(1) constIdx(2)` · 3 bytes | `... -> ..., value` | Loads the constant at constIdx from the chunk's constant pool. |
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
| `0x15` | `LdcX` | `opcode(1) constIdx(4)` · 5 bytes | `... -> ..., value` | Loads a constant using a 4-byte index, for pools larger than 65536 entries. |
| `0x16` | `LdcS` | `opcode(1) constIdx(1)` · 2 bytes | `... -> ..., value` | Loads a constant using a 1-byte index, for the first 256 pool entries. |
| `0x17` | `Ldl` | `opcode(1) localIdx(2)` · 3 bytes | `... -> ..., value` | Loads the local variable at localIdx from the current frame. |
| `0x18` | `Ldl0` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 0. On an instance method this is the receiver. |
| `0x19` | `Ldl1` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 1. |
| `0x1A` | `Ldl2` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 2. |
| `0x1B` | `Ldl3` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 3. |
| `0x1C` | `Ldl4` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 4. |
| `0x1D` | `Ldl5` | `opcode(1)` · 1 byte | `... -> ..., value` | Loads local 5. |
| `0x1E` | `LdlS` | `opcode(1) localIdx(1)` · 2 bytes | `... -> ..., value` | Loads a local using a 1-byte index, for the first 256 slots of the frame. |
| `0x1F` | `Ldg` | `opcode(1) globalIdx(2)` · 3 bytes | `... -> ..., value` | Reads a host-defined global variable. Indexes the native global table, the only truly global namespace in Surtr. A direct indexed load off that table's value storage - the host reaches the same slot through an accessor, but bytecode does not. |
| `0x20` | `LdgX` | `opcode(1) globalIdx(4)` · 5 bytes | `... -> ..., value` | Reads a host-defined global variable using a 4-byte index. |
| `0x21` | `Stl` | `opcode(1) localIdx(2)` · 3 bytes | `..., value -> ...` | Pops a value and stores it into the local at localIdx. |
| `0x22` | `Stl0` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 0. |
| `0x23` | `Stl1` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 1. |
| `0x24` | `Stl2` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 2. |
| `0x25` | `Stl3` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 3. |
| `0x26` | `Stl4` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 4. |
| `0x27` | `Stl5` | `opcode(1)` · 1 byte | `..., value -> ...` | Pops a value into local 5. |
| `0x28` | `StlS` | `opcode(1) localIdx(1)` · 2 bytes | `..., value -> ...` | Pops a value into a local using a 1-byte index. |
| `0x29` | `Stg` | `opcode(1) globalIdx(2)` · 3 bytes | `..., value -> ...` | Pops a value and writes it into a host-defined global variable. The compiler must reject this against a global the host registered as read-only. |
| `0x2A` | `StgX` | `opcode(1) globalIdx(4)` · 5 bytes | `..., value -> ...` | Pops a value into a host-defined global variable using a 4-byte index. |

## Arithmetic Operations

Two parallel sets: untagged opcodes for the integer family (`int`, `bool` and `char` share one representation) and `F`-prefixed ones for floats. There is no promotion here — a mixed expression is the compiler's problem, and it inserts the conversion.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x2B` | `Add` | `opcode(1)` · 1 byte | `..., a, b -> ..., a + b` | Integer addition. |
| `0x2C` | `FAdd` | `opcode(1)` · 1 byte | `..., a, b -> ..., a + b` | Floating-point addition. |
| `0x2D` | `Sub` | `opcode(1)` · 1 byte | `..., a, b -> ..., a - b` | Integer subtraction. The deeper operand is the minuend, so the result is a - b, not b - a. |
| `0x2E` | `FSub` | `opcode(1)` · 1 byte | `..., a, b -> ..., a - b` | Floating-point subtraction. |
| `0x2F` | `Mul` | `opcode(1)` · 1 byte | `..., a, b -> ..., a * b` | Integer multiplication. |
| `0x30` | `FMul` | `opcode(1)` · 1 byte | `..., a, b -> ..., a * b` | Floating-point multiplication. |
| `0x31` | `Div` | `opcode(1)` · 1 byte | `..., a, b -> ..., a / b` | Integer division. Division by zero has no defined result yet and needs a trap decision. |
| `0x32` | `FDiv` | `opcode(1)` · 1 byte | `..., a, b -> ..., a / b` | Floating-point division. Division by zero follows IEEE 754 and yields an infinity or NaN rather than trapping. |
| `0x33` | `Mod` | `opcode(1)` · 1 byte | `..., a, b -> ..., a % b` | Integer remainder. As with `Div`, a zero divisor still needs a defined behaviour. |
| `0x34` | `FMod` | `opcode(1)` · 1 byte | `..., a, b -> ..., a % b` | Floating-point remainder. |
| `0x35` | `Pow` | `opcode(1)` · 1 byte | `..., a, b -> ..., a ** b` | Integer exponentiation. Raises the deeper operand to the power of the top one. A negative exponent has no integer result and needs a defined behaviour. |
| `0x36` | `FPow` | `opcode(1)` · 1 byte | `..., a, b -> ..., a ** b` | Floating-point exponentiation. |
| `0x37` | `Neg` | `opcode(1)` · 1 byte | `..., a -> ..., -a` | Integer negation. |
| `0x38` | `FNeg` | `opcode(1)` · 1 byte | `..., a -> ..., -a` | Floating-point negation. Flips the sign bit, so it also turns zero into negative zero. |
| `0x39` | `Inv` | `opcode(1)` · 1 byte | `..., a -> ..., !a` | Logical negation of a boolean. This is the boolean operator. The bitwise complement is `Not`. |

## Comparison Operations

Four operand families, because comparing is where representations stop agreeing: untagged for integers, `F` for floats (IEEE 754, so `NaN` compares false against everything), `R` for reference identity, `Str` for text. Ordering exists only for the numeric ones; references and strings answer equality and nothing else.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x3A` | `EQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Integer equality. Also covers bools and chars, which share the integer representation. |
| `0x3B` | `FEQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Floating-point equality. IEEE 754 semantics, so NaN compares unequal to everything including itself. |
| `0x3C` | `REQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Reference identity. Compares handles, not contents - two equal strings in different objects are not identical. |
| `0x3D` | `StrEQ` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | String equality by text. The counterpart to `REQ` for the one reference type Surtr compares by value. Its own opcode rather than a call to string.equals, because == on strings is common enough that a call per comparison would show. Two null strings are equal; a null and a non-null are not. |
| `0x3E` | `NE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Integer inequality. |
| `0x3F` | `FNE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Floating-point inequality. NaN compares unequal to everything, so this yields true when either side is NaN. |
| `0x40` | `RNE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | Reference non-identity. |
| `0x41` | `StrNE` | `opcode(1)` · 1 byte | `..., a, b -> ..., bool` | String inequality by text. |
| `0x42` | `GT` | `opcode(1)` · 1 byte | `..., a, b -> ..., a > b` | Integer greater-than. |
| `0x43` | `FGT` | `opcode(1)` · 1 byte | `..., a, b -> ..., a > b` | Floating-point greater-than. False whenever either operand is NaN. |
| `0x44` | `GE` | `opcode(1)` · 1 byte | `..., a, b -> ..., a >= b` | Integer greater-than-or-equal. |
| `0x45` | `FGE` | `opcode(1)` · 1 byte | `..., a, b -> ..., a >= b` | Floating-point greater-than-or-equal. False whenever either operand is NaN. |
| `0x46` | `LT` | `opcode(1)` · 1 byte | `..., a, b -> ..., a < b` | Integer less-than. |
| `0x47` | `FLT` | `opcode(1)` · 1 byte | `..., a, b -> ..., a < b` | Floating-point less-than. False whenever either operand is NaN. |
| `0x48` | `LE` | `opcode(1)` · 1 byte | `..., a, b -> ..., a <= b` | Integer less-than-or-equal. |
| `0x49` | `FLE` | `opcode(1)` · 1 byte | `..., a, b -> ..., a <= b` | Floating-point less-than-or-equal. False whenever either operand is NaN. |
| `0x4A` | `IsNull` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Tests whether the top value is the null reference. |
| `0x4B` | `IsNotNull` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Tests whether the top value is a non-null reference. |
| `0x4C` | `InstanceOf` | `opcode(1) typeIdx(2)` · 3 bytes | `..., a -> ..., bool` | Tests whether the top value is an instance of the type at typeIdx. The type is an immediate, not a stack operand. Resolves through the class's ancestor chain for classes and its interface table for interfaces. |
| `0x4D` | `InstanceOfX` | `opcode(1) typeIdx(4)` · 5 bytes | `..., a -> ..., bool` | Tests instance-of using a 4-byte type index. |

## Bitwise Operations

Integer-only, and the two right shifts are genuinely different instructions rather than a flag: `Sar` replicates the sign bit and `Shr` fills with zeroes. Shift counts mask to `& 31`, so an over-wide count is defined rather than undefined.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x4E` | `And` | `opcode(1)` · 1 byte | `..., a, b -> ..., a & b` | Bitwise AND. |
| `0x4F` | `Or` | `opcode(1)` · 1 byte | `..., a, b -> ..., a | b` | Bitwise OR. |
| `0x50` | `Xor` | `opcode(1)` · 1 byte | `..., a, b -> ..., a ^ b` | Bitwise exclusive OR. |
| `0x51` | `Not` | `opcode(1)` · 1 byte | `..., a -> ..., ~a` | Bitwise complement. This is the bitwise operator. The boolean negation is `Inv`. |
| `0x52` | `Shl` | `opcode(1)` · 1 byte | `..., a, b -> ..., a << b` | Left shift. Shifts the deeper operand left by the top one. Shift counts at or above the operand width still need a defined behaviour. |
| `0x53` | `Shr` | `opcode(1)` · 1 byte | `..., a, b -> ..., a >>> b` | Logical right shift, filling with zeroes. Does not preserve the sign - a negative value becomes a large positive one. The sign-preserving form is `Sar`. |
| `0x54` | `Sar` | `opcode(1)` · 1 byte | `..., a, b -> ..., a >> b` | Arithmetic right shift, replicating the sign bit. Keeps the sign, so a negative value stays negative. The zero-filling form is `Shr`. |

## Conversion Operations

Only `int` pairs with every other family, so a mixed conversion is two instructions — `char` to `float` is `C2I` then `I2F`. The boxing opcodes sit here because boxing is the conversion from a value to a reference.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x55` | `I2F` | `opcode(1)` · 1 byte | `..., a -> ..., float` | Widens an integer to a float. |
| `0x56` | `F2I` | `opcode(1)` · 1 byte | `..., a -> ..., int` | Narrows a float to an integer. Lossy. The rounding mode, and what happens for NaN or out-of-range values, still need to be pinned down. |
| `0x57` | `I2C` | `opcode(1)` · 1 byte | `..., a -> ..., char` | Retags an integer as a character. Int, bool and char share one representation, so this changes only the value's tag and truncates the payload to 16 bits. The tag still matters: it is what decides which class the value reports and which box `BoxChar` versus `BoxInt` produces. |
| `0x58` | `C2I` | `opcode(1)` · 1 byte | `..., a -> ..., int` | Retags a character as an integer. Always exact - every character fits an integer. |
| `0x59` | `I2B` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Converts an integer to a boolean. Normalises as well as retags - any non-zero integer becomes true, so the payload is always 0 or 1 afterwards. That normalisation is what lets every boolean opcode treat the payload as a bit. |
| `0x5A` | `B2I` | `opcode(1)` · 1 byte | `..., a -> ..., int` | Retags a boolean as an integer, giving 0 or 1. |
| `0x5B` | `BoxInt` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes an integer into a heap object. Allocates, so the result is a collectable reference rather than an inline value. |
| `0x5C` | `BoxFloat` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes a float into a heap object. |
| `0x5D` | `BoxBool` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes a boolean into a heap object. |
| `0x5E` | `BoxChar` | `opcode(1)` · 1 byte | `..., a -> ..., ref` | Boxes a character into a heap object. |
| `0x5F` | `Unbox` | `opcode(1)` · 1 byte | `..., ref -> ..., value` | Unwraps a boxed value back to its inline representation. Recovers whichever primitive was boxed - the tag on the boxed value says which, so no per-type opcode is needed on the way back. |
| `0x60` | `Cast` | `opcode(1) typeIdx(2)` · 3 bytes | `..., a -> ..., a` | Casts the top value to the type at typeIdx. The type is an immediate, not a stack operand. This is a checked reference cast: the value is unchanged on success; a failure traps as `InvalidCastException`. |
| `0x61` | `CastX` | `opcode(1) typeIdx(4)` · 5 bytes | `..., a -> ..., a` | Casts using a 4-byte type index. |

## String Operations

Thin on purpose: a string is a CLR string wearing a class, and everything richer than length, concatenation and indexing is a native method on the `string` built-in rather than an instruction.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x62` | `StrLen` | `opcode(1)` · 1 byte | `..., str -> ..., int` | Pushes the length of a string in characters. |
| `0x63` | `StrCat` | `opcode(1)` · 1 byte | `..., a, b -> ..., a + b` | Concatenates two strings. The deeper operand comes first in the result. Allocates a new string. |
| `0x64` | `StrGet` | `opcode(1)` · 1 byte | `..., str, index -> ..., char` | Reads the character at an index of a string. An out-of-range index traps as `IndexOutOfRangeException`. |

## Array Operations

Every allocating opcode carries the whole parameterised type, not the element type — one immediate then gives both the descriptor the object keeps and the family its slots are initialised from.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x65` | `ArrNew` | `opcode(1) typeIdx(2)` · 3 bytes | `..., size -> ..., array` | Allocates an array of the type at typeIdx, whose length is taken from the stack. TypeIdx names the whole parameterised type - AI, AS, ADIS - not the element type alone, so one immediate carries both the descriptor the object keeps and the element family the elements are initialised from. Elements start at that family's zero: 0, 0.0, false, '\0' or null. |
| `0x66` | `ArrNewX` | `opcode(1) typeIdx(2) size(4)` · 7 bytes | `... -> ..., array` | Allocates an array whose length is an immediate. Not a widened `ArrNew` but a different addressing mode - the length moves from the stack into the instruction, for arrays of statically known size. |
| `0x67` | `ArrPack` | `opcode(1) typeIdx(2) size(2)` · 5 bytes | `..., v1, ..., vN -> ..., array` | Pops size values and packs them into a new array. What an array literal compiles to. The deepest popped value becomes element 0, matching `TupPack`. |
| `0x68` | `ArrLen` | `opcode(1)` · 1 byte | `..., arr -> ..., int` | Pushes an array's length. |
| `0x69` | `ArrGet` | `opcode(1)` · 1 byte | `..., arr, index -> ..., value` | Reads an array element. An out-of-range index traps as `IndexOutOfRangeException`. |
| `0x6A` | `ArrSet` | `opcode(1)` · 1 byte | `..., arr, index, value -> ...` | Writes an array element. Consumes all three operands and pushes nothing. |
| `0x6B` | `ArrPush` | `opcode(1)` · 1 byte | `..., arr, value -> ...` | Appends a value to an array, growing it. An opcode rather than a method on the array built-in because there is no way to write its signature - a descriptor names one concrete type, and "the element type of whatever this array is" is not expressible. The same reasoning covers every opcode from here to `ArrIndexOf`, and their dictionary counterparts. |
| `0x6C` | `ArrPop` | `opcode(1)` · 1 byte | `..., arr -> ..., value` | Removes and pushes an array's last element. Popping an empty array traps. |
| `0x6D` | `ArrInsert` | `opcode(1)` · 1 byte | `..., arr, index, value -> ...` | Inserts a value at an index, shifting everything after it up. An index equal to the length appends; anything beyond it traps. |
| `0x6E` | `ArrRemoveAt` | `opcode(1)` · 1 byte | `..., arr, index -> ...` | Removes the element at an index, shifting everything after it down. |
| `0x6F` | `ArrClear` | `opcode(1)` · 1 byte | `..., arr -> ...` | Drops every element of an array. |
| `0x70` | `ArrIndexOf` | `opcode(1)` · 1 byte | `..., arr, value -> ..., int` | Pushes the index of the first element equal to a value, or -1. Equality is the runtime's value semantics, not raw bits, so two distinct string objects holding the same text match. Linear scan. |
| `0x71` | `ArrIn` | `opcode(1)` · 1 byte | `..., arr, value -> ..., bool` | Tests whether an array contains a value. Linear scan, so cost grows with the array. |
| `0x72` | `ArrNIn` | `opcode(1)` · 1 byte | `..., arr, value -> ..., bool` | Tests whether an array does not contain a value. Exists as its own opcode so the negated form costs no extra instruction. |

## Tuple Operations

A tuple is immutable, so there is no setter: it is packed once and read thereafter. `TupGet` takes its index as an immediate because a tuple's element type varies per position, which means the compiler has to know the index to type the expression at all.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x73` | `TupPack` | `opcode(1) typeIdx(2) size(1)` · 4 bytes | `..., v1, ..., vN -> ..., tuple` | Pops size values and packs them into a tuple of the type at typeIdx. The deepest popped value becomes element 0. Caps arity at 255. typeIdx names the shape - T(IS) - which is the only place a tuple's element types are recorded, since elements carry no type of their own. |
| `0x74` | `TupUnpack` | `opcode(1) size(1)` · 2 bytes | `..., tuple -> ..., v1, ..., vN` | Expands a tuple into size separate stack entries. Element 0 ends up deepest, so packing and unpacking round-trip. |
| `0x75` | `TupLen` | `opcode(1)` · 1 byte | `..., tup -> ..., int` | Pushes a tuple's arity. |
| `0x76` | `TupGet` | `opcode(1)` · 1 byte | `..., tup, index -> ..., value` | Reads a tuple element. There is no matching setter - tuples are immutable once packed. |

## Dictionary Operations

Keyed by the runtime's own value comparer, so two strings with the same text are one key. `DictGet` traps on a missing key rather than answering null, because null is a legal value to have stored.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x77` | `DictNew` | `opcode(1) typeIdx(2)` · 3 bytes | `... -> ..., dict` | Allocates an empty dictionary of the type at typeIdx. TypeIdx names the whole pair - DIS for {int: string}. |
| `0x78` | `DictPack` | `opcode(1) typeIdx(2) count(2)` · 5 bytes | `..., k1, v1, ..., kN, vN -> ..., dict` | Pops count key/value pairs and packs them into a new dictionary. What a dictionary literal compiles to. Later pairs overwrite earlier ones on a duplicate key, as `DictSet` does. |
| `0x79` | `DictLen` | `opcode(1)` · 1 byte | `..., dict -> ..., int` | Pushes the number of entries in a dictionary. |
| `0x7A` | `DictGet` | `opcode(1)` · 1 byte | `..., dict, key -> ..., value` | Reads the value stored under a key. A missing key needs a defined behaviour - trap, or push null. |
| `0x7B` | `DictSet` | `opcode(1)` · 1 byte | `..., dict, key, value -> ...` | Stores a value under a key, inserting or replacing. |
| `0x7C` | `DictDel` | `opcode(1)` · 1 byte | `..., dict, key -> ..., bool` | Removes the entry stored under a key. Pushes whether an entry was actually removed, so a caller that does not care can drop it with `Pop` and one that does needs no second lookup. |
| `0x7D` | `DictClear` | `opcode(1)` · 1 byte | `..., dict -> ...` | Drops every entry of a dictionary. |
| `0x7E` | `DictKeys` | `opcode(1) typeIdx(2)` · 3 bytes | `..., dict -> ..., array` | Collects a dictionary's keys into a new array of the type at typeIdx. The array's own type has to be named here because it cannot be derived at run time - the dictionary knows DIS, but building AI from it would mean parsing a descriptor on every call. In the dictionary's own iteration order. |
| `0x7F` | `DictValues` | `opcode(1) typeIdx(2)` · 3 bytes | `..., dict -> ..., array` | Collects a dictionary's values into a new array of the type at typeIdx. In the same order as `DictKeys`, so the two line up element for element. |
| `0x80` | `DictIn` | `opcode(1)` · 1 byte | `..., dict, key -> ..., bool` | Tests whether a dictionary holds a key. |
| `0x81` | `DictNIn` | `opcode(1)` · 1 byte | `..., dict, key -> ..., bool` | Tests whether a dictionary does not hold a key. |

## Object Operations

Allocation only. A constructor is an ordinary call the compiler emits afterwards, exactly as it is on the JVM, so nothing here knows constructors exist.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x82` | `ObjNew` | `opcode(1) typeIdx(2)` · 3 bytes | `... -> ..., obj` | Allocates an uninitialised instance of the class at typeIdx. Allocation only. The instance is sized from the class's instance slot count and zeroed; a constructor still has to be invoked separately, normally with `InvokeSpecial`. Instantiating an abstract class must be rejected. |
| `0x83` | `ObjNewX` | `opcode(1) typeIdx(4)` · 5 bytes | `... -> ..., obj` | Allocates an instance using a 4-byte type index. |

## Field Operations

Instance access is one indexed load into the object; static access is one indirect load through an address the linker resolved, which is why there is no test for where a static's storage lives.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x84` | `FieldGet` | `opcode(1) fieldIdx(2)` · 3 bytes | `..., obj -> ..., value` | Reads an instance field. The field table entry carries the slot index, so the read is a direct offset into the instance rather than a name lookup. A null receiver hits the CLR null check and surfaces as `NullReferenceException`. |
| `0x85` | `FieldSet` | `opcode(1) fieldIdx(2)` · 3 bytes | `..., obj, value -> ...` | Writes an instance field. The compiler must reject this against a read-only field outside a constructor. |
| `0x86` | `StaticFieldGet` | `opcode(1) fieldIdx(2)` · 3 bytes | `... -> ..., value` | Reads a static field, or a module-level variable. No receiver, which is why this cannot be folded into `FieldGet` - doing so would put a static/instance test on one of the hottest instructions in the set. Module-level variables are the same thing: Surtr has no true globals, so a module variable is a static of its module and reaches its storage the same way. The field table entry carries the address of the slot itself, resolved when the declaring type was linked, so this is one indirect load. |
| `0x87` | `StaticFieldGetX` | `opcode(1) fieldIdx(4)` · 5 bytes | `... -> ..., value` | Reads a static field using a 4-byte field index. |
| `0x88` | `StaticFieldSet` | `opcode(1) fieldIdx(2)` · 3 bytes | `..., value -> ...` | Writes a static field, or a module-level variable. The compiler must reject this against a read-only field outside its static initializer. |
| `0x89` | `StaticFieldSetX` | `opcode(1) fieldIdx(4)` · 5 bytes | `..., value -> ...` | Writes a static field using a 4-byte field index. |

## Closure Operations

Building a closure captures by value, popping the captures off the stack. There is no matching setter for an upvalue anywhere in the set — that is what makes a capture a snapshot rather than a shared cell.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x8A` | `NewClosure` | `opcode(1) functionIdx(2) upvaluesCount(1)` · 4 bytes | `..., u1, ..., uN -> ..., closure` | Captures upvalues and builds a closure over the function at functionIdx. Pops exactly upvaluesCount values, deepest becoming upvalue 0, which is the numbering `UpValueGet` uses. Caps captures at 255. |
| `0x8B` | `NewClosureX` | `opcode(1) functionIdx(4) upvaluesCount(1)` · 6 bytes | `..., u1, ..., uN -> ..., closure` | Builds a closure using a 4-byte function index. |

## Upvalue Operations

Read-only, and only one instruction, for the reason above.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x8C` | `UpValueGet` | `opcode(1) upvalueIdx(1)` · 2 bytes | `... -> ..., value` | Reads a captured variable from the currently executing closure. Only valid inside a closure body. There is no matching setter, so captures are read-only as the set stands. |

## Control Flow Operations

The largest family, and almost all of it is the fused compare-and-branch forms: `JPLT` instead of `LT` followed by `JPZ`. A comparison that only feeds a branch never needs its boolean to reach the stack, and that is the shape of nearly every condition a compiler emits. Every branch has a two-byte and a four-byte form; the emitter picks the narrow one and widens only what does not reach.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0x8D` | `JPZ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., cond -> ...` | Branches if the popped condition is false. The offset is signed and relative to the instruction following this one, so a negative value branches backwards - the shape of every loop. |
| `0x8E` | `JPNZ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., cond -> ...` | Branches if the popped condition is true. |
| `0x8F` | `JPN` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., value -> ...` | Branches if the popped value is the null reference. |
| `0x90` | `JPNN` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., value -> ...` | Branches if the popped value is a non-null reference. |
| `0x91` | `JP` | `opcode(1) relativeOffset(2)` · 3 bytes | `... -> ...` | Branches unconditionally. |
| `0x92` | `JPZX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., cond -> ...` | Branches if the popped condition is false, with a 4-byte offset. |
| `0x93` | `JPNZX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., cond -> ...` | Branches if the popped condition is true, with a 4-byte offset. |
| `0x94` | `JPNX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., value -> ...` | Branches if the popped value is null, with a 4-byte offset. |
| `0x95` | `JPNNX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., value -> ...` | Branches if the popped value is non-null, with a 4-byte offset. |
| `0x96` | `JPX` | `opcode(1) relativeOffset(4)` · 5 bytes | `... -> ...` | Branches unconditionally, with a 4-byte offset. |
| `0x97` | `JPEQ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped integers are equal. Fuses a comparison and a branch, so the boolean never reaches the stack. This is why the whole compare-and-branch family exists alongside the plain comparisons. |
| `0x98` | `JPFEQ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped floats are equal. Never taken when either operand is NaN. |
| `0x99` | `JPREQ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped references are identical. |
| `0x9A` | `JPStrEQ` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped strings hold the same text. |
| `0x9B` | `JPEQX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped integers are equal, with a 4-byte offset. |
| `0x9C` | `JPFEQX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped floats are equal, with a 4-byte offset. |
| `0x9D` | `JPREQX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped references are identical, with a 4-byte offset. |
| `0x9E` | `JPStrEQX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped strings hold the same text, with a 4-byte offset. |
| `0x9F` | `JPNE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped integers differ. |
| `0xA0` | `JPFNE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped floats differ. Always taken when either operand is NaN. |
| `0xA1` | `JPRNE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped references are not identical. |
| `0xA2` | `JPStrNE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the two popped strings hold different text. |
| `0xA3` | `JPNEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped integers differ, with a 4-byte offset. |
| `0xA4` | `JPFNEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped floats differ, with a 4-byte offset. |
| `0xA5` | `JPRNEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped references are not identical, with a 4-byte offset. |
| `0xA6` | `JPStrNEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches if the two popped strings hold different text, with a 4-byte offset. |
| `0xA7` | `JPGT` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped integer is greater than the top one. Taken when a > b. |
| `0xA8` | `JPFGT` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped float is greater than the top one. Never taken when either operand is NaN. |
| `0xA9` | `JPGTX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on integer greater-than, with a 4-byte offset. |
| `0xAA` | `JPFGTX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on float greater-than, with a 4-byte offset. |
| `0xAB` | `JPGE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped integer is greater than or equal to the top one. |
| `0xAC` | `JPFGE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped float is greater than or equal to the top one. Never taken when either operand is NaN. |
| `0xAD` | `JPGEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on integer greater-or-equal, with a 4-byte offset. |
| `0xAE` | `JPFGEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on float greater-or-equal, with a 4-byte offset. |
| `0xAF` | `JPLT` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped integer is less than the top one. |
| `0xB0` | `JPFLT` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped float is less than the top one. Never taken when either operand is NaN. |
| `0xB1` | `JPLTX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on integer less-than, with a 4-byte offset. |
| `0xB2` | `JPFLTX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on float less-than, with a 4-byte offset. |
| `0xB3` | `JPLE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped integer is less than or equal to the top one. |
| `0xB4` | `JPFLE` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a, b -> ...` | Branches if the deeper popped float is less than or equal to the top one. Never taken when either operand is NaN. |
| `0xB5` | `JPLEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on integer less-or-equal, with a 4-byte offset. |
| `0xB6` | `JPFLEX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a, b -> ...` | Branches on float less-or-equal, with a 4-byte offset. |
| `0xB7` | `JPInstanceOf` | `opcode(1) typeIdx(2) relativeOffset(2)` · 5 bytes | `..., value -> ...` | Branches if the popped value is an instance of the type at typeIdx. Carries two immediates, so this is the widest of the 2-byte-offset branches. Fuses `InstanceOf` with a branch, which is the shape a type switch compiles to. |
| `0xB8` | `JPInstanceOfX` | `opcode(1) typeIdx(4) relativeOffset(4)` · 9 bytes | `..., value -> ...` | Branches on instance-of, with 4-byte type index and offset. |
| `0xB9` | `Switch` | `opcode(1) low(4) count(4) defaultOffset(4) offsets(4 * count)` · 13 + 4n bytes | `..., value -> ...` | Branches through a jump table indexed by a contiguous range of integers. The popped value selects offsets[value - low]; anything outside [low, low + count) takes defaultOffset. One bounds check and one indexed load, whatever the number of cases - which is the whole reason a switch is not just a chain of `JPEQ`. <para> Every offset here is relative to <em>this instruction's own opcode byte</em>, unlike the ordinary branches, which are relative to the instruction that follows them. A variable-length instruction has no fixed "next" address to measure from at emit time. The same applies to `SwitchLookup`. </para> |
| `0xBA` | `SwitchLookup` | `opcode(1) count(4) defaultOffset(4) (key(4) offset(4)) * count` · 9 + 8n bytes | `..., value -> ...` | Branches by searching a sorted table of integer keys. The counterpart to `Switch` for sparse cases, where a dense table would be mostly padding. Keys must be sorted ascending; the interpreter binary-searches them, so lookup is logarithmic rather than the linear scan a chain of comparisons costs. Offsets are measured from this instruction's opcode byte. |

## Call Operations

Where a call *lands* — a module, another module, or the host's global table. This is the axis that is about namespaces, not about dispatch.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xBB` | `CallLocalModule` | `opcode(1) functionIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., a1, ..., aN -> ..., result?` | Calls a module-level function declared in the current module. Pops exactly argsCount values, deepest being the first parameter, and pushes retCount results. Skipping the module table is what makes this the cheap case for intra-module calls. |
| `0xBC` | `CallLocalModuleX` | `opcode(1) functionIdx(4) argsCount(1) retCount(1)` · 7 bytes | `..., a1, ..., aN -> ..., result?` | Calls a function in the current module, with a 4-byte function index. |
| `0xBD` | `CallModule` | `opcode(1) moduleIdx(2) functionIdx(2) argsCount(1) retCount(1)` · 7 bytes | `..., a1, ..., aN -> ..., result?` | Calls a module-level function in another module. The target module must already be loaded and linked. |
| `0xBE` | `CallModuleX` | `opcode(1) moduleIdx(4) functionIdx(4) argsCount(1) retCount(1)` · 11 bytes | `..., a1, ..., aN -> ..., result?` | Calls a function in another module, with 4-byte module and function indices. The longest instruction in the set. |
| `0xBF` | `CallGlobalNative` | `opcode(1) functionIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., a1, ..., aN -> ..., result?` | Calls a host-defined global function. Dispatches through the native entry point, a managed function pointer, so the call costs no marshalling transition. |
| `0xC0` | `CallGlobalNativeX` | `opcode(1) functionIdx(4) argsCount(1) retCount(1)` · 7 bytes | `..., a1, ..., aN -> ..., result?` | Calls a host-defined global function, with a 4-byte function index. |

## Method Operations

How a call *resolves* — directly, through a vtable, through an interface, or through a closure. There is deliberately no opcode for calling host code: where a body lives is a property of the method the call site names, and the interpreter reads it anyway because a virtual call can resolve onto a native override.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xC1` | `InvokeVirtual` | `opcode(1) methodIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., obj, a1, ..., aN -> ..., result?` | Invokes an instance method through the receiver's virtual method table. The method table entry supplies a vtable slot, so dispatch is one load plus an indirect call - the receiver's runtime class decides which override runs. A null receiver hits the CLR null check and surfaces as `NullReferenceException`. |
| `0xC2` | `InvokeSpecial` | `opcode(1) methodIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., obj, a1, ..., aN -> ..., result?` | Invokes an instance method without virtual dispatch. Binds exactly the method named in the table, ignoring any override. This is how constructors and explicit base calls are issued. |
| `0xC3` | `InvokeStatic` | `opcode(1) methodIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., a1, ..., aN -> ..., result?` | Invokes a static method. No receiver is popped. It carries no type index: the method entry already knows its declaring class, and static initializers run when their module is loaded rather than on first touch, so there is nothing for the interpreter to trigger here. |
| `0xC4` | `InvokeStaticX` | `opcode(1) methodIdx(4) argsCount(1) retCount(1)` · 7 bytes | `..., a1, ..., aN -> ..., result?` | Invokes a static method, with a 4-byte method index. |
| `0xC5` | `InvokeInterface` | `opcode(1) methodIdx(2) argsCount(1) retCount(1)` · 5 bytes | `..., obj, a1, ..., aN -> ..., result?` | Invokes a method through an interface contract. Resolves through the receiver class's interface dispatch table, which maps an interface slot to a vtable slot - one extra indirection over `InvokeVirtual`. |
| `0xC6` | `InvokeClosure` | `opcode(1) argsCount(1) retCount(1)` · 3 bytes | `..., closure, a1, ..., aN -> ..., result?` | Calls a closure taken from the stack. The only call form with no index immediate - the target comes from the stack, so it is resolved entirely at run time. A null closure hits the CLR null check and surfaces as `NullReferenceException`. |

## Exception Operations

One instruction. Entering a `try` emits nothing at all — a protected region is a row in the method's handler table, so only a raise pays.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xC7` | `Throw` | `opcode(1)` · 1 byte | `..., exception ->  (the frame does not continue)` | Raises the object on top of the stack as an exception. Control leaves this instruction and does not come back. The interpreter unwinds frame by frame looking for a handler whose protected range covers the raising instruction and whose caught type matches, clears that frame's operand stack, pushes the exception, and resumes at the handler. <para> There is deliberately no opcode for entering or leaving a try. Protected ranges live in a table on the method, so a try that never throws costs exactly nothing - where a push/pop-handler pair would cost two instructions on every entry. finally is the compiler's job: emit the block on each normal exit path, plus a catch-all handler that runs it and re-raises with this opcode. That is what javac does, and it keeps the interpreter free of a second unwinding mode. </para> <para> A trap the VM itself raises - a bad index, a division by zero - and an exception thrown by host code are both catchable the same way: they are wrapped as objects and unwound through the same tables. </para> |

## Return Operations

Two forms rather than one with a count, because the count is known at emit time and a branchless return path is worth an opcode.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xC8` | `ReturnVoid` | `opcode(1)` · 1 byte | `... -> ...` | Returns from the current function without a value. Discards the frame; anything left on its operand stack is dropped. |
| `0xC9` | `ReturnValue` | `opcode(1)` · 1 byte | `..., result -> ...` | Returns from the current function with a single value. Pops one value and hands it to the caller. Returning several values means packing them into a tuple first, since there is no multi-value return instruction. |

## Nullable Primitive Operations

A null primitive is a reserved tag, not a boxed object: `int?` costs a value slot exactly as `int` does. These are the instructions that produce and test that tag.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xCA` | `PushAbsent` | `opcode(1) typeCode(1)` · 2 bytes | `... -> ..., absent` | Pushes the "no value" state of a nullable primitive. The immediate is the SurtrValueTypeCode of the primitive that is missing, so the value can say what it is the absence of. It is never the null <em>reference</em>: that is `PushNull`, and the two carry different tags on purpose. |
| `0xCB` | `IsAbsent` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Replaces a nullable primitive with whether it holds no value. Tests the tag, not the payload, which is exactly why it cannot be `IsNull`. A reference is its 32-bit payload, so IsNull ignores the tag - and an int of value zero would answer that test the same way a null does. |
| `0xCC` | `IsPresent` | `opcode(1)` · 1 byte | `..., a -> ..., bool` | Replaces a nullable primitive with whether it holds a value. |
| `0xCD` | `JPA` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a -> ...` | Pops a value and branches if it is an absent primitive. What ?? and ?. lower to over a nullable primitive. |
| `0xCE` | `JPAX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a -> ...` | Pops a value and branches if it is an absent primitive, with a 4-byte offset. |
| `0xCF` | `JPNA` | `opcode(1) relativeOffset(2)` · 3 bytes | `..., a -> ...` | Pops a value and branches if it is a present primitive. |
| `0xD0` | `JPNAX` | `opcode(1) relativeOffset(4)` · 5 bytes | `..., a -> ...` | Pops a value and branches if it is a present primitive, with a 4-byte offset. |

## Value Class Operations

Boxing that names the class to present as, which is what a `value class` needs — the same bits have to become an `EntityId` rather than an `int`.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xD1` | `BoxAs` | `opcode(1) typeIdx(2)` · 3 bytes | `..., a -> ..., ref` | Boxes the value on top of the stack as an instance of a named class. What a value class boxes through. The Box* family carries no type index because a boxed primitive takes the class the unboxed primitive already had; a value class is erased to the field it wraps, so where it has to become a reference the class it should present as is exactly the thing the value no longer says. Unboxing is still `Unbox`: the box's own value carries its tag. |
| `0xD2` | `BoxAsX` | `opcode(1) typeIdx(4)` · 5 bytes | `..., a -> ..., ref` | Boxes as a named class, with a 4-byte type index. |

## Range Operations

Materialising a range, for the cases where one escapes into a variable. A range written inline in a `for-in` header must never reach these: the compiler lowers that to a counted loop over two ints.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xD3` | `RangeNew` | `opcode(1)` · 1 byte | `..., lo, hi -> ..., ref` | Builds a range from two int bounds, excluding the upper one. Allocates. A range written inline in a for-in header must never reach this - the compiler lowers that to a counted loop over two ints - so this is for a range that genuinely escapes into a variable, a parameter or a return. |
| `0xD4` | `RangeNewInclusive` | `opcode(1)` · 1 byte | `..., lo, hi -> ..., ref` | Builds a range from two int bounds, including the upper one. The ..= form. A separate opcode rather than an increment at the call site because hi may be int.MaxValue, where incrementing would wrap. |

## String Hashing

One instruction, existing for one lowering: `switch` over strings.

| Value | Opcode | Encoding | Stack | What it does |
|---|---|---|---|---|
| `0xD5` | `StrHash` | `opcode(1)` · 1 byte | `..., str -> ..., hash` | Replaces a string with its hash. Reads the hash `SurtrString` computed once at construction, so this is a load rather than a walk over the text. The value is `ComputeHash`'s, which depends only on the text - that is what lets a compiler hash a switch's case labels at build time and have them still match at run time, in another process. This exists for that lowering: hash, SwitchLookup, then StrEQ to settle collisions. |
