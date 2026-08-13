# Compiler Plan

**Working document.** This is the ordered plan for `src/Surtr.Compiler`, written down so nothing in
it depends on being remembered. It is the compiler-side counterpart to `docs/VM-Plan.md`, which
covers the runtime: where that document says "the compiler owes X", this one says when X gets built.

Delete or fold this into `CLAUDE.md` once the front end is finished — it is a plan, not a
description of what exists, and a plan that outlives its execution is just a stale doc.

---

## 0. Where things stand

| Layer | State |
|---|---|
| Instruction set, metadata, registry, object model, built-ins, stdlib, interpreter, emitter | Done |
| Module image (`.surtrc`), multi-runtime loading, native members by link name | Done |
| `Syntax/` — source buffer, reader, tokens, lexer, AST, parser | Done, spec-complete |
| `Diagnostics/` — spans, codes, accumulating bag, two-boundary recovery | Done (Step 0) |
| `Binding/Symbols/` — the type and symbol model | Done (Step 1) |
| `CodeGen/DescriptorEmitter.cs` — the gate to the runtime's encoding | Done (Step 1) |
| `Binding/Symbols/OperatorNames.cs`, `SyntheticNames.cs` — the ABI names | Done (§6) |
| `Compilation/` — project, module grouping, dependency order | Done (Step 2) |
| `Binding/MetadataImporter.cs` — metadata in, as symbols | Done (Step 2) |
| `Binding/Binder.cs`, `Scope.cs`, `TypeResolver.cs`, `SignatureSet.cs` | Done (Step 3, phases 1–2) |
| `Binding/Conversions.cs`, `MemberLookup.cs`, `OverloadResolution.cs` | Done (Step 3, phase 3 rules) |
| `Binding/BoundTree/`, `BodyBinder.*.cs` | Done (Step 3, phase 3) |
| `Binding/FlowAnalysis.cs`, `ConstantEvaluator.cs` | Done (Step 3, phase 3) |
| `CodeGen/MethodBodyEmitter.cs` — bound tree onto `SurtrCodeEmitter` | Done for the const-evaluable subset (Step 4) |
| `CodeGen/ConstFolder.cs`, `Binding/ConstFunctionCheck.cs` | Done (Step 4) |
| `CodeGen/` — the rest of lowering, and a whole module emitted | **Not started** |

**Steps 3 and 4 are complete.** 1517 tests green. `Sample.surtr` exercises every construct in the language and round-trips through
lex + parse.

Everything `docs/VM-Plan.md` §4 asked the *runtime* for is implemented. §4.8 — the list of things
the runtime deliberately does not do because the compiler must — is entirely owed, and is
reproduced in §7 below so the plan is self-contained.

---

## 1. Step 1 — The symbol and type model (`Binding/Symbols`) — **done**

**The point:** the compiler needs its own type representation, and it is *not* `SurtrClassReference`.

A descriptor is the runtime's canonical form, and it is canonical precisely because it throws away
what the runtime does not need. Use it as the binder's type and `int?` collapses into `int`,
`Box<int>` into `Box<string>`, and `EntityId` into `int` — at which point the type checker has
nothing left to check. The descriptor is an *output*.

### Deliverables

* `TypeSymbol` — the binder's type. Carries:
  * the declaring symbol and its type arguments (a *constructed* type is a symbol plus a
    substitution, not a separate declaration),
  * nullability, which for a primitive is a distinct type and for a reference is a flow fact,
  * alias transparency — an alias (`Language-Syntax.md` §2.7) is *equal to* its target, so it must
    compare through while still printing as itself in diagnostics,
  * value-class identity — a `value class` (§2.9) is a distinct type that erases to its field, so it
    must compare *not* equal to its field's type while lowering to it.
* `MethodSymbol`, `FieldSymbol`, `PropertySymbol`, `ParameterSymbol`, `LocalSymbol`,
  `TypeParameterSymbol`, `ModuleSymbol`.
* Well-known types: the built-ins and stdlib, reachable by name without a lookup each time.
* **The one gate to the runtime:** `TypeSymbol → SurtrClassReference`, applied only at emit.
  Nothing in `Binding/` may call it; if a binding decision needs a descriptor, the decision is wrong.

### How it came out

* **Type identity is reference identity.** `TypeSymbolFactory` interns every type, so comparison
  never walks a structure. Type constructors are internal for that reason: one built outside the
  factory would compare unequal to its own twin and quietly break every check resting on this.
* **Nullability is a flag on `TypeSymbol`, not a wrapper.** A wrapper would have to be unwrapped at
  every `is NamedTypeSymbol` in the binder, and the one that forgot would be a silent bug. Each
  type and its nullable twin are linked duals, created once.
* **An alias is not a `TypeSymbol`; a value class is.** §2.7 makes an alias transparent, so it
  resolves to its target and `AliasSymbol` exists only to be declared, checked for cycles and
  reported on. §2.9 makes a `value class` distinct, so it is a `NamedTypeSymbol` that erases at
  emit rather than at resolution. They look alike in source and are opposite here on purpose.
* **A generic has three related symbols**: the definition, which owns all declaration state; a
  construction, interned per definition; and a nullable form. The latter two read everything but
  their arguments and nullability through the definition, so filling a type in during binding's
  second phase is done once and seen by all three.
* `ToDisplayString()` prints source spelling (`int[]`, `(int, float)`, `Box<int>?`) and is the only
  string a diagnostic may use to name a symbol.
* Nothing here is on the execution path, so the runtime's performance rules do not apply.

### What the gate does that nothing earlier may

`CodeGen/DescriptorEmitter.cs` is the only place a descriptor is produced, and it lives in
`CodeGen/` so that reaching for one during binding reads as the layering violation it is. Three
things are erased there and nowhere before it:

* **Reference nullability** — a reference is its payload and null is already representable, so
  `Foo?` and `Foo` are one descriptor. Only a nullable *primitive* keeps its `?`.
* **Value classes** — erased to the wrapped field where the type is statically known, with
  `EmitBoxedForm` naming the real class for the slots that hold a reference.
* **A generic method's type parameters** — `G<n>` names the declaring *type*'s n-th parameter and
  nothing indexes into a method's own list, so those emit as plain `E`.

`DescriptorEmitterTests.TypesTheBinderKeepsApartCanStillShareADescriptor` pins the whole argument
for the layer: three pairs the type checker must separate, all collapsing at emit.

---

## 2. Step 2 — `Compilation` and the metadata importer — **done**

### How it came out

* **`ModulePath`** derives a module from where a file lives (§2.1): directories become segments,
  prefixed by the project's `RootModulePath`. Every segment must be a legal identifier, because an
  `import` has to be able to name it — a directory called `my-module` is rejected here rather than
  producing a module no source file could reach. A file at the root with no root module path is
  rejected too: an empty path would produce descriptors like `:Entity`.
* **`SurtrProject`** carries what is not source — source root, referenced images and modules, host
  types, and the build constants `const if` reads (§7.4).
* **`ModuleDependencyGraph`** accumulates rather than being computed once. Imports declare most
  edges and are known as soon as a file is parsed, but §2.1 also lets a name be written fully
  qualified with no import, and that edge only appears once the binder resolves it — so the graph
  takes additions and the check can be re-run. Its ordering is sorted, so two modules with no
  dependency between them come out in the same order every build.
* **A cycle is a hard error** naming the whole loop, with the first module repeated at the end so it
  reads as one. Static initializers run eagerly at load in dependency order, so a cycle has no valid
  order to pick.
* **`MetadataImporter`** turns built metadata into symbols — the built-ins (always reachable, since
  their module is process-wide), a module compiled earlier, and the host's own types. It caches a
  type's shell *before* reading its members, so a class whose method mentions its own type does not
  recurse forever.

### The importer is the other side of the gate

It is the **only** place a descriptor is read, as `CodeGen/DescriptorEmitter` is the only place one
is written. That is a boundary, not a shortcut: metadata is the form a dependency arrives in, so
something has to decode it, and confining that to one type is what keeps the rest of `Binding`
working in symbols.

What cannot come back is exactly what the emitter threw away: a nullable reference arrives
non-nullable, a `value class` arrives as the class it boxes into, and an alias never existed. What
*does* survive is everything the descriptor was given room for — a nullable primitive's `?`, a type
parameter's position, and a constructed generic's arguments.

### Notes

* Import is lazy per type and cached by metadata identity, so a large host surface is not paid for
  on every compile.
* An imported symbol and a source symbol are indistinguishable to the binder, which is the point.

---

## 3. Step 3 — The binder, in phases — **phases 1 and 2 done**

Phases exist because a member's signature can name a type declared later in the file, or in another
file of the same module. One pass cannot do it.

1. **Declaration** — **done**. Walks every compilation unit, creates a symbol per declared type and
   alias, and populates scopes. No signature is looked at.
2. **Hierarchy and members** — **done**. Resolves base classes, interfaces and every member
   signature against the complete set, and detects inheritance cycles.
3. **Bodies** — **not started**. Binding statements and expressions, one method at a time.

After phase 2 every type's surface is known, which is exactly the state `MetadataImporter` produces
for a module compiled earlier — so a source type and an imported one become interchangeable, which
is what phase 3 needs.

### How phases 1 and 2 came out

* **`Scope`** is a chain, innermost first, so a nearer declaration shadows a further one without any
  level knowing what the others hold. **Imports get a scope of their own** between a module's
  declarations and the built-ins: a local declaration then shadows an imported name, while two
  wildcard imports still collide with each other — and that collision is reported *at the use*,
  which §2.1 asks for explicitly.
* **Types and members are separate namespaces** (§1.1 makes a type name an ordinary identifier
  resolved in the type namespace), so a scope is built per namespace rather than holding both.
* **A scope holds several candidates under one name**, because arity is part of identity:
  `Result<T>` and `Result<T, E>` both answer to `Result` and the argument count picks between them.
  A duplicate is therefore a name *and* an arity that already exist.
* **`TypeResolver`** never returns null — an unresolved name yields the error type, reports once,
  and every rule that touches it afterwards stays quiet. A dotted name is read first as a nested
  type reached through something in scope and only then as a fully qualified `module.Type`, because
  §2.6 makes `.` the qualifier at every level and nothing in the syntax says where the module ends.
* **Aliases resolve lazily**, so one may target another declared later, and a cycle is caught by
  meeting an alias already being resolved — the same shape `SurtrBuildState.Linking` uses.
* **`SignatureSet` compares emitted signatures, not written ones.** Three things collapse on the
  way, and each is a pair that would otherwise collide in a real method table: a type parameter
  erases, a *reference's* nullability is not in the descriptor, and a `value class` erases to the
  field it wraps. A nullable primitive stays distinct. An alias needs no rule — §2.7 already makes
  it resolve to its target. A conversion is the exception in the other direction: `operator as` is
  overloaded on its target, so the return joins the key here as the target descriptor joins the name
  at emit.

### Phase 3, part one — the rules a body is checked against — **done**

These come first because they are where the decisions are, and because they are testable without a
single bound node.

* **`Conversions`** classifies how one type reaches another. The implicit set is small and fixed,
  and every part of it follows from a decision taken elsewhere: `int` → `float` is the only implicit
  numeric widening (§5.7); `T` → `T?` costs nothing (§5.1); a derived type reaches its base and the
  contracts it satisfies; anything reaches an erased slot and nothing comes back out without a cast
  (§5.10). Generics and arrays are **invariant**, because §6 supports no declaration-site variance.
  A `value class` reaches nothing, which is the whole point of it against a transparent alias
  (§2.9). A user-defined conversion is found on *either* end, per §5.6, and is never implicit.
  The error type converts both ways silently, so one bad name does not report twice.
* **`MemberLookup`** walks base classes then interfaces, and builds the *substituted* view of a
  constructed generic's members — one `Box<T>.get()` symbol exists, but on a `Box<int>` receiver it
  reads as returning `int`. Cached per construction.
* **`OverloadResolution`** implements §3.5's rules 2 through 4. Rule 1 is a property of a
  declaration, not a call, and is checked once by `SignatureSet`. Specificity is decided **per
  argument** rather than by a score: a candidate wins only if it is at least as good everywhere and
  strictly better somewhere, since a score would let one exact match outvote two conversions — a
  silent pick dressed up as a rule. The winner is then re-checked against every other candidate,
  because beating the one it happened to be compared against is not beating all of them.

### Phase 3, part two — the bound tree and the body binder — **done**

`Binding/BoundTree/` holds the tree; `BodyBinder` (partial, split by expressions and statements)
walks a body onto it. `Binder.BindBodies()` runs it, separately from `Bind()`, because phases 1 and
2 answer what every type *is* — which is all a tool needs for navigation or metadata — and one
body's binding cannot affect another's.

Five things the tree settles so nothing downstream has to:

* **Every conversion is a node**, written or not. An `int` argument reaching a `float` parameter
  carries its widening, so code generation never rediscovers one.
* **Arguments arrive in parameter order**, with named ones reordered and varargs collected into an
  array literal. By the time anything reads a call, its arguments line up one for one.
* **A compound assignment is expanded**: `x += 1` arrives as `x = x + 1`, so nothing needs a second
  form of assignment or a second table of operators.
* **Devirtualisation is decided here.** A call through `super`, or on a receiver whose type is
  `sealed`, is marked non-virtual — §2.2's static fact rather than a guess.
* **`for-in` is *not* lowered.** Whether a sequence walks by index or through `iterate()` is a
  code-generation decision; binding only settles what one step yields.

Two rules the binder enforces that are flow-shaped rather than type-shaped, and are done because
they are decided at the point they happen rather than by a later pass:

* **A lambda captures only what is never reassigned.** A capture is copied into the closure rather
  than shared through a cell, so a `var` is rejected at the capture site.
* **Type inference is one-way.** A written type also types the initializer, which is what lets
  `let xs: int[] = [];` work where a bare `let xs = [];` cannot — the empty literal is reported.

### Phase 3, part three — flow, constraints, exhaustiveness and `const if` — **done**

* **`FlowAnalysis`** runs on the bound tree after each body is bound, which is the form that has a
  whole body to ask about. Three questions: what can be reached, whether a local is assigned
  everywhere it is read, and whether a method can finish without returning what it promised.
  Deliberately *not* a fixed-point analysis over a control-flow graph: it walks the tree and joins
  the branches of an `if`, which is exact for straight-line code and conservative in a loop — a
  local assigned only inside a loop body is not treated as assigned after it, since nothing proves
  the loop runs. Running on the bound tree also means a compound assignment is already expanded, so
  `x += 1` reads before it writes for free.
* **Nullability narrowing** is in the binder rather than in the flow pass, because it changes what
  an expression *is* rather than what can happen. Only the shapes that carry their proof on their
  face — `x != null`, `x is T`, and the `&&` of two such — and only inside the branch they guard.
  Stopping there keeps the rule predictable, which matters more than one more shape.
* **Generic constraints** are bound in a pass of their own after the hierarchy, since
  `<T : IComparable<T>>` names a type whose own hierarchy is still being resolved while signatures
  are bound. They are checked in another pass at the end, against the *substituted* bound —
  `Sorter<Vec2>` asks whether `Vec2` satisfies `IComparable<Vec2>`, not `IComparable<T>`.
* **Switch exhaustiveness** applies to the expression form over an enum only. An enum's cases are
  fixed at its own declaration, so the set is knowable, and the point is that adding a case later
  turns every switch that covered it into an error rather than letting the new one fall silently
  through an `else`. The statement form is never required to produce a value and is unaffected.
* **`const if`** (§7.3) resolves at declaration level by flattening the member list before anything
  walks it, so a member in an untaken branch does not exist in any sense; and at statement level by
  binding only the taken branch. The untaken branch is **never bound**, which is the whole reason
  the feature works — a branch guarded on one platform routinely names types this build lacks.

`ConstantEvaluator` folds over *syntax*, not over bound nodes, because a declaration-level
`const if` decides which declarations exist and has to be answered before any type has members. It
handles literals, build constants (§7.4), other `const` bindings, and the operators — and
short-circuits, so `false && somethingUnknown` still folds. It is **not** §7.2's general evaluator:
that one folds a `const fun` by running its emitted body on a real VM so compile-time and run-time
semantics cannot drift, and it is Step 4.

### Notes

* Every diagnostic gets a code in the 3xxx range, append-only, asserted on by code in tests.

---

## 4. Step 4 — Const evaluation — **done**

`Language-Syntax.md` §7 — `const`, `const fun`, `const if` — folded by **running the emitted bytecode
on the real VM**, not by a second evaluator in the compiler. Two evaluators would drift, and the
drift would show up as a program that means one thing at compile time and another at run time.

### How it came out

* **`CodeGen/MethodBodyEmitter`** turns a bound body into bytecode. It exists here rather than in
  Step 5 because folding needs it: the compiler cannot fold anything until it can *emit* something,
  so Step 4 had to build the part of Step 5 it depends on. What it covers is the const-evaluable
  subset §7.2 defines — loops, conditionals, locals, arithmetic, strings, both switch forms, locally
  built arrays, tuples and dicts, and calls. Everything outside it raises `SurtrEmitException`
  rather than emitting something approximate, and that list is exactly §5's lowering table.
* **`CodeGen/ConstFolder`** owns the one runtime the compiler ever loads. It emits every const
  function into a single scratch module — which makes a call between two of them an ordinary
  `CallLocalModule`, with no cross-module table and no load order to arrange — then runs one on
  demand. `ResetExecution` after every failure, and the budget re-armed before every run, because
  exceeding it leaves it *exhausted* rather than cleared.
* **Emission runs to a fixed point.** A function the emitter cannot lower is dropped, and dropping
  it makes every function that *calls* it fail to emit in turn, so the round repeats until nothing
  new drops. That is what keeps one unsupported construct from quietly changing what a caller
  computes: a caller of a dropped function is itself dropped, never emitted against a stub.
* **`Binding/ConstFunctionCheck`** reports §7.2's restrictions — not `virtual` or `abstract`, not
  `native`, no receiver, no write to a field or property, and no call to anything but another const
  function or the standard library. They are properties of the *declaration*, so they are reported
  against it whether or not anything ever asks for a fold. What the folder reports instead is the
  other kind of failure: the function is fine and this particular run did not finish.
* **§7.2's example calls `table.push(...)`**, so "may not call `native` functions" means a *host*
  one (§10), not the standard library — whose bodies are process-wide and exist inside the compiler
  already. `MethodSymbol.ImportedFrom` is what lets a call site name one: the single thing an
  imported symbol keeps of where it came from.
* **`const` initializers are checked** (§7.1). Nothing forced a constant to be folded before — most
  are read by a `const if` or by nothing at all — so `VerifyConstantDeclarations` is what turns "did
  not fold" into a diagnostic instead of a silently missing value.

### The ordering, which is the whole difficulty

§7.2 predicted it: folding needs the callee's *emitted body*, so const evaluation cannot happen at
one point in the pipeline. What settles it is binding bodies in two rounds — **every `const fun`
first**, then the folder is built, then everything else. So:

| Position | Can it call a `const fun`? |
|---|---|
| a **declaration-level** `const if` (§7.3) | **no** — answered in the declaration phase, before any signature exists |
| a **statement-level** `const if` | yes |
| a `const` initializer (§7.1) | yes |
| an ordinary call with constant arguments | not yet — see below |

The first row is a real limitation and it is reported rather than guessed at. The last is deferred
on purpose: §7.2 says such a call *is* folded, but until Step 5 emits ordinary bodies there is no
emitted code for the fold to change, so it belongs where it becomes observable.

### What is left

* Fold a `const fun` call with constant arguments inside an ordinary body, at Step 5.
* `static let Sines: float[] = buildSineTable(256);` folds today through `ConstFolder.TryFold`;
  **materialising** the result into the module's static initializer is Step 5's.
* Resolution inside a constant expression is by name and arity across the whole compilation, which
  is the fidelity `ConstantEvaluator` already had for a constant's own name. Two const functions
  answering to one name and arity make the call ambiguous rather than arbitrary.

### Notes

* `docs/VM-Plan.md` §4.7 covers what this costs and why it is still the right trade.
* Const evaluation is the one place the compiler loads a runtime, and it is behind one type. A
  compilation that declares no `const fun` builds none.

---

## 5. Step 5 — Lowering and code generation

Onto `SurtrModuleBuilder`, honouring the declare → emit → `Build()` → `LoadModule` order the emitter
forces. `CodeGen/MethodBodyEmitter` already exists and is where this continues — Step 4 built the
const-evaluable subset of it, and everything below is what it still refuses.

### Already lowered (Step 4)

Locals and parameters, every arithmetic, bitwise and comparison operator, `&&`/`||` with their
short circuit, `??`, `<=>` on a primitive family, the ternary, `if`/`while`/`for`, `break` and
`continue` including the labelled forms, `return`, `throw`, both switch forms, `for-in` over a range
or an array, array/tuple/dict literals, indexed read and write, interpolated strings, conversions
including boxing into an erased slot and the `Cast` back out, a call to a local function, and a call
to an imported one through `MethodSymbol.ImportedFrom`.

Two of those are deliberately provisional. **A switch is a chain of comparisons**, not
`SurtrCodeEmitter.SwitchOn`: a jump table needs every label to be an integer key known at emit, and
the general case needs the enum-ordinal and string lowerings below — picking the encoding is one
decision, made once, here. And **`for-in` over a range only works when the range is written
inline**, which is what `RangeNew`'s own documentation asks for.

### Lowerings owed

| Source construct | Lowers to |
|---|---|
| `for-in` over a built-in collection other than an array | an indexed loop, no iterator allocated |
| `for-in` over an `IIterable<T>` | `iterate()` + `moveNext()`/`current` through the vtable |
| `finally` | the block on every exit path, plus a catch-all that runs it and re-raises |
| `try`/`catch` | protected regions on the method builder, plus handler labels |
| `as?` | `InstanceOf` + branch |
| `<=>` and relational operators on `string` | a call to native `string.compareTo` |
| string `switch` | `StrHash` + numeric switch + equality confirm |
| a dense integer `switch` | `SwitchOn`, which picks a jump table or a key table |
| lambda | a **static synthetic method** plus a closure allocation |
| object creation, field and property access, `this`/`super` | `ObjNew`, `FieldGet`/`FieldSet`, the accessor calls |
| `inline` / `forceinline` (§3.6) | the body spliced into the call site |
| type alias (§2.7) | its target's descriptor; transparent |
| `value class` (§2.9) | its single field, boxed only where it flows into a reference slot |
| property access | the `get_x`/`set_x` call the linker sees |
| enum case | its ordinal |
| a folded `const fun` call in an ordinary body | the constant itself |

### Naming conventions to fix *before* the first emit

Synthetic members go into the module's real method table and travel in the image, so their names are
ABI. Pick once, write them down here, and never change them:

* Lambda bodies.
* Auto-property backing fields.
* Bridge methods into a generic interface's erased slot.
* Closure display/capture holders, if any.

The constraint is only that they cannot collide with a legal Surtr identifier, so a character no
identifier may contain is the whole mechanism. `<` and `>` are what javac and Roslyn both use.

### Notes

* A lambda is a static method with its captures passed as construction arguments to `SurtrClosure`,
  never an instance method on a synthesised class — the runtime already copies the dispatch payload
  flat, so there is nothing for a class to add.
* Every declared method goes into its own module's method table whether or not anything local calls
  it: a cross-module call reads the *callee's* table.

---

## 6. ABI decisions — settled

All of these are on disk the moment the first image is written. They are decided; changing one
later invalidates every `.surtrc` written against it.

### 6.1 Operator names

An overloaded operator is declared under its own symbol behind `op_`: `op_+`, `op_==`, `op_<=>`,
`op_[]`, `op_!`, `op_++`. An identifier is `letter|_` then `letterOrDigit|_`, so every one of these
is **unspellable in source** — no prefix has to be reserved to keep a user declaration from
colliding, and a disassembly reads without translation, which `op_Addition` would not.

Unary `-` is `op_-u`. That suffix is cosmetic: §5.6 separates the two forms by arity and so does a
signature key, since one takes a parameter and the other takes two. It exists so a disassembly line
says which without the reader counting. Indexed read and write share `op_[]` for the same reason.

**`operator as` is the one that cannot be named from its token.** It is overloaded on its *target*,
and a signature key is name plus parameters and excludes the return, so two conversions from one
source type would collide. The target's descriptor joins the name — `op_as$Ogame.core:Vec3;` —
**at emit only**, via `DescriptorEmitter.EmitMethodName`. In the binder the method is plainly
`op_as` with `IsConversion` set, because naming it earlier would need a descriptor.

`Binding/Symbols/OperatorNames.cs`.

### 6.2 Synthetic member names

Shape: `$category$context[$index]`. One rule — **a leading `$` means the compiler made it** — which
is greppable and, `$` being illegal in an identifier, cannot collide.

| Member | Name |
|---|---|
| a lambda's lifted body | `$lambda$move$0` |
| an auto-property's backing field | `$backing$health` |
| a bridge into a generic interface's erased slot | `$bridge$compareTo$0` |

The index appears only where one context can hold several: a method may hold many lambdas, a
property has exactly one backing field.

**Property accessors are deliberately excluded.** A property lowers to `get_x`/`set_x`, and those
are the names `SurtrTypeLinker` looks for when it wires one up — marking them would hide them from
the layer that has to find them.

`Binding/Symbols/SyntheticNames.cs`.

### 6.3 Where a value class boxes

A `value class` is erased to the field it wraps where its type is statically known, and becomes a
real object with its own `SurtrClass` where something has to be a reference. The runtime side is
already built: `BoxAs`/`BoxAsX` box with a type index, and `SurtrValueComparer` compares a box's
class, so a boxed `EntityId(7)` and a boxed `int` 7 are not equal. What the compiler owes is
knowing every site. A missed one is a type confusion, not a slow path.

**Boxes** — the slot holds a reference and nothing else can:

* assignment or argument passing into an **erased generic parameter** (`T`, descriptor `G<n>`/`E`);
* the same into an **`unknown`** (§5.10), which is the erased slot with a surface name;
* the same into a variable, parameter or return typed as an **interface the value class
  implements**;
* an element of a collection whose element type is erased — `T[]` where `T := EntityId`, a dict
  value of type `T`, a tuple element of type `T`, a closure parameter or return of type `T`;
* a **capture** by a lambda whose closure slot is erased;
* a receiver for an **interface-dispatched** call on it.

**Does not box** — the type is statically known, so there is nothing to be a reference for:

* a local, parameter, field or return declared as the value class itself;
* an element of `EntityId[]`, which is an `int[]` — the element type is statically known, so the
  array's descriptor is `AI` and there is no per-element decision to make;
* a **direct** call to one of its own methods, or a call to a method it declares that satisfies an
  interface but is reached without going through that interface.

Reading one back out of an erased slot is the mirror obligation: a `Cast` to the value class, then
unwrap. That is the same pair §7 already lists for primitives, applied to one more type.

### 6.4 A nested type does not see its container's parameters

Java's static-nested rule. A type declared inside a generic one is qualification only:
`Box<T>.Entry` is an `Entry` of arity zero, and if it needs an element type it declares its own.
This is what makes a descriptor's argument count the **last** name segment's arity rather than a sum,
and it keeps every construction site from having to supply the container's arguments as well.

---

## 7. What the runtime deliberately does not do (VM-Plan §4.8)

Reproduced so this plan stands alone. Each of these is a compiler obligation, not a runtime gap:

* **Box a primitive flowing into an erased slot, and emit a `Cast` reading one back out.**
* **Emit `finally` on every exit path**, plus a catch-all that runs it and re-raises. This is what
  keeps `Leave`/`EndFinally` out of the instruction set.
* **Emit a bridge into a generic interface's erased slot.** `SignatureKey()` writes `G<n>` as `E`, so
  a class wanting both `compareTo(Vec2)` and `IComparable<Vec2>` needs two members: the typed one and
  a bridge that casts and forwards.
* **Reject instantiating an `abstract` class.** `ObjNew` does not check.
* **Reject overriding a `sealed` member and extending a `sealed` class.**
* **Lower `<=>` and the relational operators on `string`** to `string.compareTo`.
* **Lower `as?`** to `InstanceOf` plus a branch.
* **Check argument counts, types and defaults at the call site.** The interpreter trusts them.

---

## 8. Decided: how much of a generic survives compilation

Three separable levels, previously conflated under the single word "erasure". **(a) and (b) are
adopted; (c) is rejected.**

**(a) Arity is part of type identity — adopted.** `Box<T>` and `Box<T, U>` are different types, with
different `SurtrClass` instances and different entries in `SurtrModule`'s tables. Arity is mangled
into the emitted type name, CLR-style. Costs the runtime nothing: a full name is an opaque string to
every path that handles it, and `SurtrTypeInfo.GenericParameters` already stores names and arity and
the image already serializes them.

**(b) Type arguments live in the descriptor — adopted.** `Box<int>` and `Box<string>` are different
*descriptors* resolving to the same `SurtrClass`, exactly as `AI` and `AS` both already resolve to
`SurtrBuiltIns.Array`. This is what the built-in collections have and user generics do not, and it is
the level that fixes overload collisions, diagnostics and host interop.

**(c) Reified type arguments — rejected.** An instance knowing it is a `Box<int>` needs either a
per-instance type-argument vector or per-instantiation layout and vtables. It is the only level that
would remove boxing from a generic field, and it is the only one that costs.

### Encoding

Arity in the name means the argument list needs no brackets and no count — the parser reads the
arity on its way to the name terminator and then reads exactly that many descriptors. One
left-to-right pass with one character of lookahead, like every other symbol.

```
Box<int>                ->  Obox:Box`1;I
Box<int, string>        ->  Obox:Box`2;IS
Box<T>   (inside Box)   ->  Obox:Box`1;G0
Pair<int, Box<string>>  ->  Opair:Pair`2;IObox:Box`1;S
```

A non-generic type carries no backtick and no arguments, so **its descriptor is byte-identical to
today's**. Backtick is not a legal Surtr identifier character, so the mangling cannot collide with a
declared name.

### What is and is not distinguished

`SurtrMethodInfo.SignatureKey()` keeps erasing, but it erases only `G<n>` — "the declaring type's
n-th parameter" — and never touches a concrete argument. So:

| | Distinct? | |
|---|---|---|
| `Box<T>` vs `Box<T, U>` | **yes** | different classes, by (a) |
| `f(b: Box<int>)` vs `f(b: Box<string>)` | **yes** | different parameter descriptors, by (b) |
| `Box<int>.get()` vs `Box<string>.get()` | **no** | one class, one method table, one compiled body |

The third row is the definition of not reifying, not a gap. The compiler still sees them as
different: `Box<int>`'s `TypeSymbol` carries the substitution `T -> int`, so `get()` is typed `int`
there and `Box<int>.set("x")` is a type error. That is Step 1's job and asks nothing of the runtime.

### What this still costs

A `Wrapper<T, U>` declaring both `f(a: Box<T>)` and `f(b: Box<U>)` collides: both erase to
`` Obox:Box`1;E ``. Exactly Java's "same erasure" wart, and it comes from erasing `G<n>`, not from
(b). If it ever bites, the fix is to split the key in two — an unerased identity key for overload
uniqueness, an erased slot key for interface matching — but that is a second dictionary in the
linker and is not worth adding ahead of a real case.

Keeping `SignatureKey()` erased is what keeps `SurtrTypeLinker` untouched: matching an
implementation to a generic contract's slot stays a dictionary lookup rather than a substitution,
and the bridge obligation stays exactly as §7 already states it.

### Why now

The descriptor grammar is **on disk**; the linker's slot-matching rule is **not**. The information is
expensive to add later and the decision about how much of it to *use* stays cheap to change. And
right now the change is free: no `.surtrc` exists that contains an `O`-form descriptor of a generic
type, because there is no compiler yet and the only generic declarations are `array`, `dict` (which
use the `A`/`D` grammar branch, not `O`) and the standard library's contracts, which are rebuilt on
every start.

### Implementation — done

`SurtrClassReference` parses and displays the form; `SurtrClassReference.Constructed`,
`MangleArity`, `ArityOf`, `GenericArity` and `GetTypeArguments` are the surface. `SurtrModule`'s
type keys and `SurtrTypeHandleTable`'s interning needed **no change at all**: a full name is an
opaque string to both, and resolution reads the name up to the terminator and ignores what follows,
which is precisely why two constructions land on one `SurtrClass`.

The standard library's contracts are now declared as `IIterable`1`, `IIterator`1`, `IComparable`1`
and `IEquatable`1`, each naming itself with its own parameter (`Osurtr:IIterable`1;G0`). There is
no open form to write: a name promising one argument and supplying none is malformed.

---

## 9. Order

1. ~~Step 1 — symbols~~ **done**, together with §8's decision, which is what its emit gate produces.
2. ~~The runtime half of §8~~ **done** — descriptor parsing, display, arity-mangled contract names.
3. ~~§6's ABI decisions~~ **done** — operator names, synthetic names, boxing sites, nested types.
4. ~~Step 2 — compilation and import~~ **done**.
5. ~~Step 3 — binder~~ **done**, all three phases.
6. ~~Step 4 — const evaluation of `const fun` (§7.2), on a scratch runtime~~ **done**, together
   with the bound-tree emitter it needed.
7. Step 5 — the rest of lowering, and emitting a whole module.

Steps 4 and 5 did overlap, exactly as predicted, and in both directions: `const if` needed a
literal-only folder inside Step 3, and folding a `const fun` needed a real emitter inside Step 4.
What is left of Step 5 is the lowering table in §5 plus the module-level work — declaring types,
fields, properties and methods on a `SurtrModuleBuilder`, and writing the image.
