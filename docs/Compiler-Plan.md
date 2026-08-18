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
| `CodeGen/MethodBodyEmitter.cs` — bound tree onto `SurtrCodeEmitter` | Done (Steps 4 and 5) |
| `CodeGen/ConstFolder.cs`, `Binding/ConstFunctionCheck.cs` | Done (Step 4) |
| `CodeGen/ModuleEmitter.cs`, `EmitContext.cs` — a whole module emitted, and its image | Done (Step 5) |

**Steps 3, 4 and 5 are complete.** 1710 tests green, and Surtr source compiles to a module that
loads into a runtime and runs. `Sample.surtr` exercises every construct in the language and
round-trips through lex + parse.

**But the front end is not finished against the specification**, and §10 is the list of what is
still owed — written down after an audit that ran ~90 programs through the whole pipeline rather
than reading the code. Eight of the things that audit found were *silent*: the program compiled,
loaded, and answered wrongly. Those eight are now closed and covered by tests (§10.1); the rest are
not, and are listed so nothing rests on being remembered (§10.2).

Everything `docs/VM-Plan.md` §4 asked the *runtime* for is implemented. §4.8 — the list of things
the runtime deliberately does not do because the compiler must — is reproduced in §7 below so the
plan is self-contained.

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

## 5. Step 5 — Lowering and code generation — **done**

Surtr source now becomes a real `SurtrModule`, loads into a real `SurtrRuntime`, and runs.
`CodeGen/ModuleEmitter` drives it and `CodeGen/MethodBodyEmitter` lowers each body;
`CodeGen/EmitContext` is what they share.

### The two orderings, both forced rather than chosen

**Between modules** it is `SurtrCompilation.LoadOrder`, because a call into another module names an
entry in *that* module's method table, which does not exist until it has been built. That is also
why §2's module cycle is a hard error rather than something emission could work around.

**Within a module** it is declare → emit → `Build()`, because `SurtrBytecodeMethodInfo` snapshots its
body's offset in its constructor. So every type, field, property and method signature is declared
first, then every body is emitted against the tokens, then the chunk is laid out.

### What the emitter synthesises, and why it is here rather than in the binder

Each is a decision about where code *runs*, not about what a program means:

* an **auto-property**'s `$backing$x` field and its two trivial accessors — and either accessor
  being bare is enough, since §3.4 lets `{ get; set { ... } }` mix them;
* a **static initializer** per type carrying its static field initializers and, for an enum, its
  cases — one emitter across all of them, emitting *fragments*, since letting any one finish the
  method would leave every later one unreachable;
* the **instance field initializers**, at the top of every constructor — with a parameterless one
  synthesised when a class has initializers and declares none, which every creation site then has
  to call because the binder saw no constructor to resolve to;
* `override` **dropped where it names no base method**: §2.2 makes a contract a promise rather than
  an inheritance, both are written `override` in Surtr, and `SurtrTypeLinker` rejects an override
  with no base entry to replace.

### Lowerings — all done

| Source construct | Lowers to |
|---|---|
| `for-in` over any built-in collection | an indexed loop, no iterator allocated |
| `for-in` over an `IIterable<T>` | `iterate()` + `moveNext()`/`current` through the dispatch table |
| `finally` | the block on every exit path, plus a catch-all that runs it and re-raises |
| `try`/`catch` | protected regions on the method builder, plus handler labels |
| `as?` | `CastOrNull` to a reference type; `InstanceOf` + branch + `Unbox` to a primitive |
| `<=>` and the relational operators on `string` | a call to native `string.compareTo` |
| string `switch` | `StrHash` + `SwitchOn` + an equality confirm per hash |
| a `+` spine or an interpolation over strings | one counted `StrCat`, so one allocation rather than n − 1 |
| a tuple element read | `TupGetC`, the index as an immediate — §5.3 already made it a constant |
| a discarded `i++`, `i -= k` or a `for` step over an `int` local | `IncLocal`, one dispatch that never touches the operand stack |
| `dict` member calls: `m.clear()`, `m.containsKey(k)`, `m.remove(k)`, `m.keys()`, `m.values()`, and the `m.length` read | the `DictClear` / `DictIn` / `DictDel` / `DictKeys` / `DictValues` / `DictLen` opcodes, skipping the native dispatch |
| an integer or `char` `switch` | `SwitchOn`, which picks a jump table or a key table |
| lambda | a **static synthetic method** plus a closure whose upvalues are its captures |
| object creation, field and property access, `this`/`super` | `ObjNew`, `FieldGet`/`FieldSet`, the accessor calls |
| `inline` / `forceinline` (§3.6) | the body spliced into the call site |
| type alias (§2.7) | its target's descriptor; transparent |
| `value class` (§2.9) | its single field; `BoxAs` only where it flows into a reference slot |
| enum case | a static field of the enum's own type, built by the enum's initializer |
| a folded `const fun` call in an ordinary body | the constant itself |

Three of those took a decision worth recording:

* **A `switch` over an enum stays a chain of reference comparisons.** A case is a singleton
  instance, so matching one *is* a reference compare — switching on an ordinal would need a member
  the enum does not have, to save nothing.
* **A `value class`'s constructor is spliced, not called.** `ObjNew` would allocate an instance of
  the *erased* type, which for an `EntityId` over an `int` is `int` itself. So the constructor has
  to be one assignment to the wrapped field, and the construction evaluates to that assignment's
  right-hand side. Anything wider is refused rather than approximated, because there is no object
  for a second statement to observe.
* **`for-in` through `IIterable<T>` does not `Cast` a primitive element.** A built-in collection
  stores a primitive raw — "an int pushed into an `int[]` is never boxed on the way" — while `Cast`
  reads its subject as a reference unconditionally, so casting one would check whichever entity its
  value happens to number. A reference element is checked; a primitive one already is what it
  should be.

* **`dict` member calls lower only when they are the built-in's own members, matched by identity.**
  `clear`/`containsKey`/`remove`/`keys`/`values` and the `length` read become their opcodes
  (`DictClear`/`DictIn`/`DictDel`/`DictKeys`/`DictValues`/`DictLen`) because each has a dedicated
  opcode; `get`/`set` are not lowered because indexing already reaches `DictGet`/`DictSet`, and
  `isEmpty` because there is no opcode for it. The match is `MethodSymbol.ImportedFrom` compared by
  identity against `SurtrBuiltIns.Dictionary`'s members — the same test `RangeAccessor` and
  `StringCompareTo` use — so a user class declaring a same-named method is never mistaken for the
  built-in. When the returned value is discarded, the emit pops it (`DictIn` and `DictDel` both
  leave   a bool). This is measurement-driven, not blanket: it exists so `dictMembers` is not measured
  as an index-only loop while the member surface still pays a native call. A `for-in` over a `dict`
  gets the same treatment on its snapshot: the key is read out of the `DictKeys` array once and the
  value is read under it with one `DictGet`, instead of a second array read per iteration.

### Notes

* A lambda is a static module-level function whose captures are the closure's **upvalues**, never an
  instance method on a synthesised class — the runtime already copies the dispatch payload flat.
  `this` is not a symbol and so cannot sit in a capture list, which is what
  `BoundLambdaExpression.CapturesReceiver` records.
* `SurtrCodeEmitter.CallSpecial(SurtrMethodBuilder)` is new and exists for one case: a `super` call
  names a virtual method and must not go through the vtable, or an override calling its base would
  call itself. Every other call takes its dispatch from the callee.
* Every declared method goes into its own module's method table whether or not anything local calls
  it: a cross-module call reads the *callee's* table.

### The five that were left, and how each came out

* **Parameter defaults (§3.5).** Folded through `ConstantEvaluator` — twice, once before any body is
  bound so an ordinary call site can emit one, and again once a `const fun` can be run. A call site
  that omits an argument materialises the value as a literal, because §4.8 makes filling a default
  the call site's job and a call opcode carries a count and nothing else. The declaration still
  records it, so a module compiled later can omit the argument too, and `MetadataImporter` reads it
  back — an image that dropped it would make a defaulted parameter mandatory downstream.
* **`singleton` (§2.8).** A sealed class plus a synthetic `$instance$Registry` static of its own
  type, built by the class's initializer like an enum case. That static is the whole feature: §1.1
  puts type names and value names in separate namespaces, so `Registry` resolves as a *type* and has
  to be read through the instance to become a value — which is why this is the one place in the
  binder where a type name answers as one. A constructor is rejected, since nothing would choose
  when to run it.
* **Bridges.** For each generic contract slot the class fills with a typed member, a second member
  named after the contract's, taking the erased parameters, that casts and forwards — virtually, so
  an override further down still wins. It is emitted, never bound, so nothing in source can name it
  and `SignatureSet` never sees it as a duplicate. `SurtrClassReference.Erase` is new and shared with
  `SurtrMethodInfo.SignatureKey`, because the compiler has to produce exactly the descriptor the
  linker compares and two copies of that rule would agree until one was edited. **Generalised
  later**: the same shape now also fires whenever a `Direct` member (no erasure involved at all)
  fills a contract slot, which is what makes `override` optional for interface satisfaction
  (`Language-Syntax.md` §3.3).
* **A call on a `value class`.** The receiver boxes with `BoxAs` only where the callee might be
  reached through its class — a method whose own dispatch is not `Direct` — and `this` inside such
  a callee unwraps to match; a `Direct` method (the common case: nothing but interface satisfaction
  ever makes a value class method non-`Direct`, since it cannot be extended) needs neither, on
  either side of the call. `BoxReceiverForCall` and `LoadReceiver` share the one test that decides
  this, so a method's body and every caller of it can never disagree about which convention it was
  compiled against. §6.3 records how the interface case closed.
* **Nested lambda captures.** `NoteCapture` walks a *stack* of lambda frames outwards and stops at
  the first one the symbol is inside. An inner lambda's upvalue has to come from the outer body, so
  the outer lambda has to have captured it too; stopping at the innermost boundary is exactly what
  used to lose it. The receiver follows the same rule.

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
| the static holding a `singleton`'s instance | `$instance$Registry` |

The index appears only where one context can hold several: a method may hold many lambdas, a
property has exactly one backing field.

**Two names another layer looks for are deliberately excluded.** A property lowers to
`get_x`/`set_x`, which is what `SurtrTypeLinker` wires a property up by. And a **bridge carries the
contract method's own name** — `compareTo`, not `$bridge$compareTo$0`: the linker matches a contract
slot on `SignatureKey()`, which is name plus erased parameters, so a bridge under any other name
fills no slot at all. That correction retires the `$bridge$` convention this section used to name;
nothing had emitted one, so no image carries it.

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
* a call to one of its own methods whose dispatch is `Direct` — including a computed property's
  `get`/`set`, which are calls too. `BoxReceiverForCall` (`MethodBodyEmitter.cs`) is the single test
  this and every other boxing site below it agree with.

**Closed.** A method satisfying an interface no longer has to give up `Direct` dispatch to do it
(`Language-Syntax.md` §3.3): `override` is optional there, and a plain `Direct` member is answered
for by a synthetic bridge occupying the interface's slot instead. `EmitBridge` (`ModuleEmitter.cs`)
is that same two-entry-point shape this section used to describe as owed — a thin bridge occupying
the vtable slot, unboxing the receiver (a value class is sealed by §2.9, so every direct-typed call
reaches the same `Direct` body either way) and forwarding via a direct call to the real, unboxed-
receiver body — now built as the general mechanism for interface satisfaction rather than a
value-class-only fix.
`LoweringChoiceTests.AValueClassMethodSatisfyingAnInterfaceWithoutOverrideDoesNotBoxOnADirectCall`
pins it.

Reading one back out of an erased slot is the mirror obligation: a `Cast` to the value class, then
unwrap. That is the same pair §7 already lists for primitives, applied to one more type.

### 6.4 A nested type does not see its container's parameters

Java's static-nested rule. A type declared inside a generic one is qualification only:
`Box<T>.Entry` is an `Entry` of arity zero, and if it needs an element type it declares its own.
This is what makes a descriptor's argument count the **last** name segment's arity rather than a sum,
and it keeps every construction site from having to supply the container's arguments as well.

---

## 7. What the runtime deliberately does not do (VM-Plan §4.8)

Reproduced so this plan stands alone. Each of these is a compiler obligation, not a runtime gap, and
**every one of them is now met** — the list stays because it is what the runtime is entitled to
assume, and anything that stops holding is a miscompile rather than a missing feature:

* **Box a primitive flowing into an erased slot, and emit a `Cast` reading one back out.** With one
  exception the runtime's own design forces: a built-in collection stores a primitive raw, so what
  comes back out of `IIterator.current` is not a box and must not be cast.
* **Emit `finally` on every exit path**, plus a catch-all that runs it and re-raises. This is what
  keeps `Leave`/`EndFinally` out of the instruction set.
* **Emit a bridge into a generic interface's erased slot.** `SignatureKey()` writes `G<n>` as `E`, so
  a class wanting both `compareTo(Vec2)` and `IComparable<Vec2>` needs two members: the typed one and
  a bridge that casts and forwards.
* **Reject instantiating an `abstract` class.** `ObjNew` does not check.
* **Reject overriding a `sealed` member and extending a `sealed` class.** The linker replaces the
  vtable entry either way, so `Binder.CheckSealedOverrides` is the only thing between `sealed` and a
  member that says it closes its branch and does not.
* **Lower `<=>` and the relational operators on `string`** to `string.compareTo`.
* **Lower `as?`** to `CastOrNull`, or to `InstanceOf` plus a branch where the target is a primitive
  and the success path has to unbox.
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

## 8b. Decided: array/dict/tuple get a nameable, callable identifier too

`array`, `dict` and `tuple` were writable only through `T[]`, `{K: V}` and `(T1, ..., Tn)` — §5.3's
symbolic forms, which read as the value they accept but aren't identifiers, so nothing could ever be
*called* `int[]`. §5.5 has no `new`; a construction is an ordinary call on a name. A type with no
name is a type that can never be constructed. `dict`'s own `reserve` doc comment already named this
gap directly, calling itself "the utility 'constructor' a dict cannot spell as one." `array<T>`,
`dict<K, V>` and `tuple<T1, ..., Tn>` (§5.3.1) close it: real, callable identifiers for the exact
same types the symbolic forms already name.

### Not a literal `alias`

The obvious first instinct — declare `alias array<T> = T[];` and get `array<T>` for free from §2.7's
existing machinery — doesn't work, and it's worth recording why so it isn't tried again. `array` and
`dict` are *already* real generic `NamedTypeSymbol`s reachable by name in the global scope
(`Binder.SeedGlobalScope`), and that identity is load-bearing: `MemberLookup.BackingType` constructs
`array` with a composite's element type to find `push`/`pop`/`get`/`set` at all — `int[].push(3)`
being a type error against metadata alone depends on `array` staying exactly what it is today. A
transparent alias declared under the same name would mean two different things by "`array`" at the
same scope depth — the built-in class *and* a stand-in for `T[]` — which is a collision, not a
convenience.

The fix keeps the two questions separate. Ordinary name lookup (`scope.Lookup("array")`) is
completely untouched, and so is `MemberLookup.BackingType`, which never goes through `TypeResolver`
at all. The redirect happens one step later, only once a full type *application* has resolved
(`TypeResolver.Apply`, after `array`'s arity check against its one written argument already passed):
at that point, and only then, the constructed `NamedTypeSymbol` `array` would otherwise produce is
swapped for the identical, structurally-interned `ArrayTypeSymbol` the symbolic form `T[]` already
produces — reference-equal, not merely convertible. `tuple` needs the same swap but earlier, before
its arity check: its imported declaration has Arity 0 (§4.6 of `docs/VM-Plan.md` — a tuple is
parameterised by a per-value list, not a per-declaration parameter), so without a bypass every
non-empty `tuple<...>` would be rejected as a type nothing else declares before ever reaching the
interesting code.

### Constructor shapes and their folds

Three shapes, chosen to need no VM/opcode work at all — everything below reuses an opcode
`docs/Opcodes.md` already documents:

| Shape | `array<T>` | `dict<K, V>` | `tuple<...>` |
|---|---|---|---|
| Empty | `ArrPack(type, 0)` — same as `[]` | `DictNew(type)` | `TupPack(type, 0)` — arity 0 only |
| Capacity | `ArrNewX(type, n)` when `n` is a written constant, else runtime `ArrNew(type)` | `DictNew(type)` + a call to `reserve` | not supported — arity is fixed by the type |
| Cast | from `tuple<T,...>`: N × `TupGetC(i)` + `ArrPack` | not supported | from `array<T>`: `ArrLen` check + trap, then N × `ArrGet` + `TupPack` |

Two things worth being explicit about, since a symmetric API easily suggests a symmetry the
implementation doesn't have. **Capacity means something different per collection.** `array<T>(n)` is
`new T[n]`-style — a real length-`n` array, zero-filled — because that is exactly what the existing,
previously-unused `ArrNewX` opcode already means ("for arrays of statically known size" per its own
doc comment), and folding onto an opcode that already says exactly this is a better fit than
inventing a reserve-style meaning that would need a second opcode nothing calls today. `dict<K, V>(n)`
*is* reserve-style — length 0, capacity hint — because `DictNew` allocates only ever an empty dict;
there is no `DictNewX`, and one was deliberately not added for this. The two-step fold (`DictNew`
plus a call to `dict`'s own already-declared `reserve`) is the one shape here that isn't a single
instruction, and that's an accepted asymmetry, not an oversight — the alternative was a new opcode
whose only reason to exist was matching `array`'s shape, at the cost of touching the interpreter, the
disassembler and `docs/Opcodes.md` for it.

**Casting is array↔tuple only.** Of the composite pairs, it's the one with a total, natural
correspondence in both directions without inventing a pairing convention: a tuple's arity is always
a compile-time fact, so array→tuple only needs one runtime check (the array's actual length) and
tuple→array needs none at all. Casting into or out of `dict<K, V>` was left out rather than guessed
at — the closest natural source, an array of key/value pairs, still needs a real compiled loop
(the array's length isn't known until run time, so it can't unroll into a fixed instruction sequence
the way the tuple direction does), and nothing asked for it.

### Implementation

`Binding/TypeResolver.cs`'s `Apply` carries the redirect, guarded by `ReferenceEquals` against
`MetadataImporter.ArrayType`/`DictionaryType`/`TupleType` so a user's own shadowing declaration is
never affected. `Binding/BodyBinder.Expressions.cs` adds a dispatch seam ahead of
`TryBindAsType`/`TryBindAsGenericDefinition` in `BindCall`, since neither of those recognizes an
`ArrayTypeSymbol`/`DictionaryTypeSymbol`/`TupleTypeSymbol` callee at all — both are written against
`NamedTypeSymbol`. Binding produces a dedicated `BoundCollectionCreationExpression`
(`Binding/BoundTree/BoundExpressions.cs`) rather than reusing `BoundObjectCreationExpression`, whose
emission is unconditionally `ObjNew` — flatly wrong for a type that is never a `SurtrInstance`.
`CodeGen/MethodBodyEmitter.cs`'s `EmitCollectionCreation` is the fold, one branch per shape, sharing
`EmitConversionTail` (split out of `EmitConversion` for exactly this) with the per-element cast
loops, and reusing the same "skip the check if the library doesn't declare the exception" idiom
`EmitNullAssert` already established for `!!`.

---

## 8c. Decided: constructors for every primitive, `string` and `range`, and the array/dict shapes with a runtime length

§8b gave `array`/`dict`/`tuple` a nameable, callable identifier. This closes the rest of the user's
constructor list (`docs/Language-Syntax.md` §5.3.2, §5.3.3, and the tuple addendum in §5.3.1): every
ordered conversion among `int`/`float`/`char`/`bool`, parsing any of them from `string`, `string`
built from any of the five other scalars, `range`'s two non-stepped forms, and five array/dict shapes
whose source has a runtime rather than compile-time length.

### Almost none of it is new mechanism

The load-bearing realization, worth stating plainly because it is what kept this from being a much
larger change: most of these "constructors" are not new machinery at all, they are new *names* for
conversions and calls the compiler already had.

| Shape | Reuses |
|---|---|
| `int(aFloat)`, and every other primitive↔primitive pair | The exact `BoundConversionExpression` `Conversions.ClassifyExplicit`'s existing `ExplicitNumeric` rule already builds for `as` — same node, same opcode, same edge cases, reached from a second syntax. |
| `string(anInt)`, `string(aFloat)`, `string(aBool)`, `string(aChar)`, `string(aRange)` | An ordinary call to the `toString()` each already declares (`range`'s is the one new native method this needed). |
| `range(a, b)`, `range(a, b, true\|false)` | The same `BoundBinaryExpression` `a..b`/`a..=b` already produces; a runtime third argument is an ordinary `BoundConditionalExpression` between the two, needing no new emission at all. |
| `tuple<T1,...>(v1,...,vn)` | The tuple literal's own binding logic, reached from a second syntax path. |
| `(T1,T2)(pair: (T1,T2))` | Nothing — reference-equality on the interned tuple type makes this a pure identity fold; the argument already *is* the value. |

What's genuinely new is small by comparison: five throwing `parseStrict`/`fromChar*` native methods
(`Runtime/BuiltIns/SurtrPrimitiveBuiltIns.cs`, `SurtrStringBuiltIn.cs`), one new exception class, and
five `CollectionCreationKind` variants whose source has a length only known at run time and so need a
genuine emitted loop rather than the compile-time-unrolled shapes §8b's own kinds use.

### Two decisions worth recording, since each fills a gap nothing had settled before

**`float`→`int` truncates toward zero, saturates outside `int`'s range, and reads `NaN` as `0`.**
This was not a new rule to invent: `SurtrVirtualMachine.cs`'s `F2I` handler already did exactly this,
determinism across x64/ARM being the reason it exists (an unchecked C# cast of an out-of-range double
is platform-defined). Only the opcode's own doc comment and its mirror in `docs/Opcodes.md` were
stale, still saying "still needs to be pinned down" when `docs/VM-Plan.md` §1.9 already described the
real, already-shipped behavior — a self-contradiction between the project's own docs that this
closes, not a VM change.

**A new `FormatException`, kept separate from `ArgumentException`.** Parsing throws on malformed
text; nothing before this needed a class for "the argument was the right *shape* but the wrong
*content*." `ArgumentException` stays for "the argument itself is wrong" — a `radix` outside
`[2, 36]`, or `keys`/`values` of different lengths in `dict<K,V>(keys, values)` — a distinction
`docs/Language-Syntax.md` §5.3.2 states directly rather than leaving to be inferred per call site.
Declared exactly like the other eight exception classes: natively in `SurtrBuiltIns.cs` via the
existing `DeclareExceptionSubclass`, mirrored one-for-one in `Exceptions.surtr`, with one new arm in
`SurtrBuiltIns.ExceptionClassFor` mapping `System.FormatException` onto it — the same mapping every
native method that throws a CLR exception already relies on to surface as a catchable Surtr object.

### Why the five array/dict shapes need a real loop

`array<T>(size, defaultValue)`, `array<T>(anotherArray)`, `array<T>(anIterable)`,
`dict<K,V>(pairs)` and `dict<K,V>(keys, values)` all read from something whose length is a runtime
value, unlike §8b's tuple-cast shapes, whose arity is always a compile-time fact and so unroll into a
straight-line instruction sequence. Each new `CollectionCreationKind` therefore emits a genuine
counted loop (`Code.NewLabel`/`MarkLabel`/`JumpIfCompare`/`IncrementLocal`) — the same hand-rolled
idiom `EmitForInIndexed`/`EmitForInRange`/`EmitForInIterable` already use, since no shared
"emit a counted loop" helper exists anywhere in this emitter and none of these synthesized loops has
a user-visible body to give `break`/`continue` targets to. `array<T>(anotherArray)` and
`array<T>(anIterable)` are dispatched in priority order in the binder — an array argument takes the
faster indexed-copy path (`ArrLen`/`ArrGet`/`ArrSet`, no interface dispatch) and only something that
isn't already an array, a tuple or an `int` falls to the general `IIterable<T>` walk, which reuses
`TryFindIterableElementType` — a non-reporting extraction of the same "what does iterating this
yield" question `for-in` binding already answers, factored out so the two never have a chance to
disagree about what counts as iterable.

### Implementation

`Runtime/BuiltIns/SurtrPrimitiveBuiltIns.cs` gains `parseStrict` on `int` (two overloads, one with a
hand-written radix parser — `Convert.ToInt32(string,int)` only covers bases 2/8/10/16), `float`,
`bool` and `char`; `SurtrStringBuiltIn.cs` gains `fromCharRepeated`/`fromCharArray`/`fromCharArraySlice`;
`SurtrCompositeBuiltIns.cs` gains `range.toString()`. `Binding/BodyBinder.Expressions.cs` adds one
dispatch method per scalar type plus two shared helpers — `TryBindPrimitiveConversion` (wraps
`Conversions.Classify` in a `BoundConversionExpression` exactly as `BindCast` does) and
`TryBindNativeSugarCall` (finds an already-declared native by name and arity and builds the
`BoundCallExpression` by hand, since a constructor's own argument list doesn't always line up 1:1
with the native's parameter list once a receiver is involved) — hooked into `BindObjectCreation`
right after the existing parameterless-default case. `BoundCollectionCreationExpression` gains
`Source2` (the values array, for `DictFromParallelArrays`), `DefaultValue` and `SourceElementType`
(what walking a generic `IIterable<T>` yields, since unlike an array or a pair tuple this can't be
read off the source's own static type). `CodeGen/MethodBodyEmitter.cs`'s `EmitCollectionCreation`
gains the five loop-emitting branches described above.

---

## 9. Order

1. ~~Step 1 — symbols~~ **done**, together with §8's decision, which is what its emit gate produces.
2. ~~The runtime half of §8~~ **done** — descriptor parsing, display, arity-mangled contract names.
3. ~~§6's ABI decisions~~ **done** — operator names, synthetic names, boxing sites, nested types.
4. ~~Step 2 — compilation and import~~ **done**.
5. ~~Step 3 — binder~~ **done**, all three phases.
6. ~~Step 4 — const evaluation of `const fun` (§7.2), on a scratch runtime~~ **done**, together
   with the bound-tree emitter it needed.
7. ~~Step 5 — the rest of lowering, and emitting a whole module~~ **done**.

Steps 4 and 5 did overlap, exactly as predicted, and in both directions: `const if` needed a
literal-only folder inside Step 3, and folding a `const fun` needed a real emitter inside Step 4 —
which is why Step 5 started with most of a body emitter already written.

Everything §5 once listed as owed is emitted, including the five it had left over — parameter
defaults, `singleton`, bridges, calls on a `value class`, and nested lambda captures.

8. Step 6 — the audit's findings. §10.1 is closed; §10.2 is what remains before the front end really
   is finished against the specification.

---

## 10. What the audit found

Steps 1 through 5 were each checked against the code they added. What none of them checked was the
specification *as a whole*, on programs, and that is what §10 exists for: every construct
`Language-Syntax.md` describes, compiled and run. The findings split cleanly in two.

### 10.1 The silent defects — closed

Each of these compiled, loaded and returned the wrong answer, with no diagnostic anywhere. They are
worth listing even though they are fixed, because most of them were the same mistake: **a node the
parser produced that nothing downstream read**. A parser that records a construct and a binder that
never asks for it produce exactly this — working syntax with no semantics — and nothing in the build
complains, because every layer is internally consistent.

The last one is the same mistake seen from the other side: not a construct nobody read, but a
construct whose *absence* nobody checked.

| | Was | Now |
|---|---|---|
| `: super(...)` / `: this(...)` | Parsed into `ChainArguments`, read by nothing. Base constructors never ran. | Bound in a pass after every signature exists, emitted before the instance initializers; `this(...)` suppresses them, since the constructor it chains to already ran them. |
| `static { }` | `StaticBlockDeclarationSyntax` built and dropped. | Bound and emitted into its container's static initializer, merged with the field initializers by declaration position — which is what §2.5 means by "interleaved". |
| a class from another module | Its synthesised constructor lived in the emitting module's `EmitContext`, so a creation site elsewhere allocated and ran nothing. | Carried across modules as metadata. It has no symbol, so it cannot travel the way every other member does. |
| `?.` | Typed as nullable and emitted as a plain access: a null receiver faulted. | Lowered through `BoundNullConditionalExpression`, whose receiver is evaluated once into a slot and read through a placeholder. |
| `!!` | Emitted the operand and nothing else. | Raises the library's `NullReferenceException`, resolved in the binder because the emitter cannot resolve a name. |
| `native let` / `native var` / module-level `native fun` (§10) | Compiled to ordinary module statics — the module loaded with no host at all and read zeroes. | Declared as an ordinary member (a property or a method) carrying a link name, the same shape a class's own native member has; a name nothing published fails the load. (This itself first landed on a since-retired per-module native import table reached with `Ldg`/`Stg`/`CallGlobalNative` — see `docs/VM-Plan.md` §4.14 — before being folded into the general native-member mechanism.) |
| a varargs parameter | Typed as its *element* type in the binder, so applicability never absorbed a surplus and an empty varargs packed an array typed `string`. | Typed as the array §3.5 says the body sees. `MetadataImporter` rebuilds it, since metadata carries the element type. |
| a base with no parameterless constructor | An omitted chain silently reached nothing, so the base went unconstructed. | Reported at the constructor that omits it, or at the class when it declares none — `Binder.CheckBaseConstructorIsReachable`. |

Two decisions inside that work are worth keeping:

* **Absence in a nullable primitive is the absent tag, never a null reference.** A reference is its
  32-bit payload, so a null one and a present `0` would otherwise be the same value — which is
  exactly what §5.1 gave the encoding a second tag to avoid. The null *literal* pushes it, which is
  what makes `n == null` a bit comparison and needs no rule of its own in the comparison path.
* **A class declaring no constructor gets one whenever its base needs constructing**, decided from
  symbols rather than from what has been emitted — a derived class may be declared before its base,
  and the answer must not depend on which.

### 10.1b Generics, from declarable to usable

§8 settled how much of a generic survives compilation and the descriptor side was built; the binder's
half was not, so a generic could be *declared* and nothing else. Constructing one, calling a generic
method, and reading a member off a type parameter were all errors — §6's own
`max<T : IComparable<T>>` example did not compile. What closed it:

* **A bound is what a type parameter reaches through.** `MemberLookup.Reachable` walks a parameter's
  constraints; an unconstrained one reaches nothing, since there is no root class to fall back to.
  The bounds themselves were the actual defect: a *method*'s type parameters are declared while its
  signature binds, which is after the pass that resolved bounds had already run, so every one of them
  stayed unbounded. Bound resolution now picks up where it left off, and runs again afterwards.
* **A construction settles its arguments from three sources, in this order**: written at the call
  (`Box<int>(5)`), the type it is going into (`let b: Box<int> = Box();` — §5.9's target typing, which
  is the only source when there is no argument to look at), and unification against the constructor's
  own parameters. Nothing to infer from is an error naming what to write, not a guess.
* **A generic call is substituted *before* overload resolution**, which is the whole design:
  applicability, specificity, the argument conversions and the call's type are then all decided
  against concrete types, and nothing downstream knows a type parameter was involved. Resolving
  against the open signature instead would ask whether an `int` converts to a `T`, which has no
  answer.
* **`TypeInference` is one mechanism for both**, a structural walk with first-binding-wins and no
  lattice. Two answers for one parameter is a refusal rather than a widening — §3.5's "no silent pick"
  applied to inference.
* **A substituted member now knows its declaration.** `Box<int>.get()` is a clone of the declaration
  typed as that construction sees it, and only the declaration was ever declared onto a builder;
  `OriginalDefinition` is the way back. Erasure is exactly what makes that the right answer — one
  class, one method table, one compiled body.
* **Explicit type arguments parse in expression position** through a bounded token scan: the generic
  reading is taken only when the angles balance and a `(` follows, so `a < b` stays a comparison.
  Nothing is consumed and nothing is reported when the scan fails, or a comparison would report the
  errors of the type argument list it was never trying to be.
* **Bounds are checked wherever a construction happens**, including one the compiler inferred and one
  written inside a body — the latter had been recorded and never verified, because the verifying pass
  ran at the end of the member phase and a body binds after it.

What generics still do not do is listed in §10.2: variance stays deliberately absent (§6), and
inference stays single-pass by choice rather than by omission.

### 10.1c A lambda typed by where it goes

§5.9 lets a lambda's parameters go unwritten "where a target type supplies them", and §8 says that
is most of the time, "since a lambda is usually being passed to a typed parameter". It worked from a
variable's annotation and nowhere else, so §8's own `items.sort((a, b) => ...)` did not compile.

**The circle is the whole problem**: the lambda's parameter types come from the overload, and the
overload is picked from the argument types. It is broken by not binding those lambdas yet. One enters
overload resolution as an *arity* — `ArgumentInfo.Lambda` — applicability asks only whether the
parameter is a closure taking that many, and the lambda is bound once, afterwards, against the
parameter that took it. Binding it eagerly and again later would report everything in its body twice.

Three consequences worth keeping:

* **Arity is all applicability can ask**, so two overloads taking closures of the same arity tie and
  §3.5's rule 4 makes that an ambiguity. That is the honest answer: a reader could not tell them
  apart either.
* **A lambda of the wrong arity fails the call and only the call.** Binding it anyway would report
  that its parameters have no types — pointing at the lambda rather than at the call that is wrong.
* A lambda whose parameters *are* written is bound eagerly as before, so nothing about the existing
  path moved.

Found alongside it and fixed: **a field or property holding a closure could not be invoked**. A local
or parameter could, and where the closure is kept says nothing about how it is called (§8) — a
method of the same name still wins, since that is what a bare call usually means.

### 10.1d Accessibility, from decoration to rule

All four modifiers reached metadata and governed nothing: a `private` field read from outside its
class, a `private` method called, an `internal` function invoked from another module. There was not
even a diagnostic code reserved for it. `Binding/AccessCheck.cs` is the rule, and it takes both halves
of a use site's context — the type a body is in and the module it belongs to — because that is what
the four levels each ask about.

* **A member is filtered, not judged.** An inaccessible overload is dropped from the candidate set
  before resolution, so a `public` overload is not shadowed by a `private` one it was never competing
  with. Reporting is left for the case where filtering took everything, which is the one where the
  author needs to hear that the member exists and is out of reach rather than that it does not exist.
* **A type is checked where its name is finally used**, not where it is resolved: the resolver asks
  "is this name a type" without reporting, since a name that turns out to be a local is not a
  mistake. So a construction and a static access ask, and `TypeResolver` asks for every other
  position — one check in `Apply`, which a name in scope, a fully qualified one and each step of a
  nested one all funnel through. A qualified name is §2.1's convenience, not a loophole.
* **A module-level member's `private` and `internal` mean the same thing**, because §2.5 makes it a
  static of its module and a module has no inside for `private` to name.
* **`private` names a declaration's whole text**, so one instance reaches another's, a nested type
  reaches its container's, and a container reaches its nested type's — the rule C# and Java share.
* Type visibility was not modelled at all: `NamedTypeSymbol` had no accessibility and the emitter
  wrote `Public` for every type it built. It is now declared, imported and emitted.

Four tests changed with it, all of the same kind: they were written when visibility governed nothing,
and each was relying on a cross-module or cross-type reach that §3.1 and §2.6 spell out as needing
`public`.

### 10.1e The last five

Five items closed together, because each was small and none was alone:

* **A nested type in an interface** (§2.3) is declared at module level under its qualified name.
  Nesting is stored on `SurtrClass` and a contract holds none, while §2.6 makes nesting
  *qualification* — so `module:IShape.Kind` is what the descriptor already said, and declaring it
  under that name is what makes the module's key agree. `SurtrModule.FindClass` probes the whole
  dotted path once the segment walk fails, which costs a lookup only where one already failed. A
  nested type in an interface defaults to **public**, since §2.3 makes every interface member so.
* **An interface property keeps its setter.** The builder's default is read-only, so a
  `{ get; set; }` on a contract silently lost half of itself and every assignment through it named a
  method the interface did not declare. And a property satisfying a contract now drops its
  `override` the way a method already did.
* **An exhaustive `switch` expression** emits: with no `else`, the binder has already established
  that the arms cover every case of a non-nullable enum, so the last arm is what is left once the
  others are tested. The check and the lowering are two halves of one feature — the form the check
  exists to allow was the form that did not compile. Anything without a fixed set of values is now
  told it needs an `else` at *binding*, where it is a fact about the program, rather than at emit.
* **`operator[]`** reaches its use site. An overload is always static, so the read form takes
  `(receiver, index)` and the write form `(receiver, index, value)` — §5.6's table counts the
  operands the *expression* has, and a declaration also names the receiver. A compound assignment
  through an indexer is deliberately not given semantics nobody specified.
* **Attributes** are bound and emitted (§11). The class is resolved by name and checked to extend
  `Attribute`; the arguments fold through the constant evaluator, since the instance is built when
  the module loads and nothing runs before that. Type-level attributes needed the image format to
  carry them, which is the one **`formatVersion` bump** in this work: 1 → 2.

### 10.1f The build model

§14.2's last item, and deliberately thin. `SurtrProjectFile` reads one directive per line and
`SurtrBuild` does the only part that was actually missing: find the sources, and write what came out.
`src/Surtr.Cli` is `surtrc build [path]` over it.

The format is a line at a time because the alternative is a dependency: the compiler targets
`netstandard2.1` so it can sit beside the runtime in Unity, where a JSON serializer is a package the
host would also have to ship — for six settings that is a bad trade. Building a `SurtrProject` in
memory stays the primary API, and nothing here caches, watches or does incremental work: those are a
host's questions, and answering them badly here would be worse than not answering.

Referencing an image found the last real gap. A cross-module call goes through the caller's module
reference table by path, because a module-level member records no owner (`docs/VM-Plan.md` §3.3) —
but the emitter only knew the owning module for one built earlier *in the same compilation*. For a
referenced image it fell through to the access table, which cannot name one. Two halves fixed it: the
emitter asks the importer for the built module behind a path, and `SurtrModuleBuilder.ExternalMethod`
matches the callee against the target's *pending* entries when its method table is still empty —
which is what a module read from an image has until a runtime loads it.

### 10.1g Flow, order, and the two contradictions

Four smaller things, and the one place the specification disagreed with itself.

* **Two terminating shapes reach `FlowAnalysis`**, and they turn out to be one question asked twice:
  what ways out does this construct have. A `switch` with a default section whose every section
  returns has none, and neither does a loop whose condition never fails and which nothing `break`s
  out of — so one stack of break targets answers both, and a `continue` deliberately marks nothing,
  since it re-enters the loop rather than leaving it. A `break` naming a label no target carries
  marks every enclosing one, because it names something further out and leaving that leaves
  everything between. `while (true)` also stopped emitting its test: the analysis reads it as a loop
  only a `break` leaves, and the emitter has to agree or it would leave a way out of a body the
  analysis approved.
* **A local may no longer shadow a local** (§4.4). The check moved from the innermost scope to the
  whole chain, which reaches a lambda's parameters too — a lambda's body is the same body, and the
  frame it runs on holds both. §4.4's exception needed no rule at all: fields are not in the value
  chain, which is what already left `this.x = x` legal.
* **Cross-initializer dependencies are rejected** (`docs/VM-Plan.md` §1.12, §3.4). Position is a
  pair — which container, and where inside it — because both orderings are real: between containers
  it is declaration order with the module last, and inside one it is the counter that already
  interleaves a `static { }` block with the initializers around it. Only the *same* module is
  checked, and that is exact rather than approximate: a module reaches another only by depending on
  it, dependencies load first, and a cycle is already a hard error. The walk follows calls and stops
  at a lambda, which is a value here and a body later.
* **An emit failure points at the construct that caused it.** `SurtrEmitException` carries a span,
  filled from the node the emitter is lowering rather than from forty call sites — `Statement` and
  `Expression` restore it on the way out, so by the time a parent gives up, it names the parent. The
  report is per member, so a module with two un-lowered lines reports both; it still does not build
  that module or emit the ones after it, because a half-emitted body is not a module and every
  module after it names entries in this one's method table.

And the two contradictions, both settled in favour of the language rather than the example:

* **§4.2's `for (let i = 0; ...; i += 1)` becomes `var`.** `let` is assign-once and a three-clause
  loop is built on reassigning one binding — its step clause *is* the reassignment. A `for-in`
  variable is the opposite and needs no keyword: it is rebound once per step, which is what
  assign-once describes. Giving the header an exception would have made `let` a spelling rather than
  a guarantee. Writing to one now names itself and says to write `var`.
* **Tuple element access is written down** (§5.3): `t[0]`, by a compile-time constant, `const`
  bindings included — which is one thing the implementation did *not* do until now, since a `const`
  binds as a read rather than a literal. The constant is the type rule showing through rather than a
  restriction on the syntax: a tuple holds a different type per position, so `t[i]` for a running
  `i` has no type to give the expression, which is the same fact that leaves `tuple` without a
  generic parameter or a `get(index)`. An index past the end is a compile error, so there is no
  bounds check to pay for.

One more thing turned up while checking the above and is closed too: **a closure held in a member
could not be called through it.** `f()` on a local worked and `First.Make()` did not — a call whose
callee is a member access looked for a method of that name and stopped, rather than falling back to
the value. §8 makes a closure a value, so where it is kept says nothing about how it is called;
`ClosureValue` is the one rule, applied at each of the three shapes that reach it — a type name with
no receiver, a singleton with its instance, and `a?.f()` with the stand-in the guard reads through.
A method of that name still wins, since that is what a call usually means.

### 10.2 What is still owed

Not silent — each reports, refuses to compile, or is visibly absent — but each is the specification
promising something the compiler does not do.

* **The standard library is entirely C#**, where §13.1 puts the exception hierarchy below the root in
  Surtr — which is also the largest program the compiler has never been asked to compile.
