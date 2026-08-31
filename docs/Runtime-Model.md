# The Surtr runtime model

How the runtime's pieces fit together: what a class, a method, a property, an enum and a module
each are, how they reach one another, and what changes between declaring them and running them.

`docs/VM-Plan.md` holds the interpreter's design decisions, `docs/Opcodes.md` the instruction set,
`docs/Module-Format.md` the on-disk form, and `docs/Language-Syntax.md` the surface language this
model has to express.

---

## 1. Two hierarchies, one root

Everything the runtime owns descends from `SurtrRuntimeEntity` — the base for anything that can be
handed a `SurtrRef`, the 32-bit integer handle unmanaged VM code addresses managed objects by.

```
SurtrRuntimeEntity
├── SurtrObject ................. a language-level value
│   ├── SurtrString, SurtrArray, SurtrTuple, SurtrDictionary
│   ├── SurtrClosure, SurtrRange, SurtrIterator, SurtrBoxed
│   ├── SurtrInstance ........... an instance of a class Surtr source declared
│   └── SurtrNativeObject
│       └── SurtrNativeProxy .... a host CLR object
│
├── SurtrModule ................. the only top-level container
└── SurtrMemberInfo ............. anything declared inside a module or a type
    ├── SurtrFieldInfo
    │   └── SurtrNativeFieldInfo  a host-owned field (getter/setter entry points)
    ├── SurtrPropertyInfo
    ├── SurtrMethodInfo
    │   ├── SurtrBytecodeMethodInfo
    │   ├── SurtrNativeMethodInfo
    │   └── SurtrAbstractMethodInfo
    └── SurtrTypeInfo
        ├── SurtrClass
        └── SurtrInterface
```

Two things about this shape are worth stating up front, because a lot follows from them.

**A type is a member.** `SurtrTypeInfo` extends `SurtrMemberInfo` because Surtr has no free-floating
types: every class and interface is declared inside a module or another type. So a nested type has a
visibility, a declaring type and attributes for the same reason a field does, with no special case.

**Metadata shares a root with values but is never registered.** `SurtrMemberInfo` is a
`SurtrRuntimeEntity`, yet class metadata is never given to an entity registry and is never traced.
It is *owned outright* — by `SurtrBuiltIns` for the built-ins, by `SurtrContext` for everything
else — and lives as long as its owner. It also **cannot** be registered: an entity holds a single
`SurtrRef`, so one shared class in two registries would have the second silently inherit the first's
id. This is why `SurtrObject.VisitReferences` does not mark an object's `Class`.

---

## 2. Modules

A module is the only top-level container, and the unit type resolution works against.

```
SurtrModule
├── path                "game.core", derived from the file's directory
├── TypeHandles         every type it mentions, interned — also its dependency list
├── Chunk               bytecode, pools, access tables
├── Fields / Properties / Methods / Classes / Interfaces      the compiler's view, by name
└── StaticFields / StaticStorage / Functions / StaticInitializer     the runtime's view, by index
```

**There are no true globals — not even a host-declared one.** A module-level variable *is* a static
of its module, and reaches its storage through the same `StaticFieldGet`/`StaticFieldSet` a class
static does — the module simply carries the same static tables a class does. A module-level
`native fun`/`native let`/`native var` is likewise an ordinary member, module-level or on a class,
carrying a **link name** the host publishes a body under with `SurtrRuntime.DefineNativeBody` — the
same mechanism a class's own native member uses. There used to be a genuinely separate global
namespace, host-declared native variables and functions that could never be written in Surtr
source, living in the runtime's own global table; that mechanism is retired, along with the table.

**A module belongs to one runtime.** Loading patches its string literals with references from that
runtime's heap, binds every native member's body against that runtime's registrations (by link
name), and hands its classes static storage the collector traces through that runtime's registry.
`LoadModule` rejects a second attempt. The shareable artefact is the *image*
(`docs/Module-Format.md`), which instantiates a fresh module per runtime.

---

## 3. Type references: names before objects

A member's signature cannot point at a `SurtrClass` instance. Class construction would become
circular and order-dependent, and metadata is built before any runtime exists. So a type reference
travels as **text** and is resolved later.

### 3.1 `SurtrClassReference` — the descriptor

A compact string, in a grammar that nests unambiguously and parses left to right with one character
of lookahead:

```
I F B C S                 int, float, bool, char, string
R                         range of ints
E                         an erased generic parameter
A<elem>                   array            AI              -> int[]
D<key><value>             dictionary       DIS             -> {int: string}
T(<elem>...)              tuple            T(IF)           -> (int, float)
L(<param>...)<ret>        closure          L(II)F          -> (int, int) -> float
O<fullname>;<arg>...      Surtr type       Ogame.core:Entity.Handle;
N<fullname>;<arg>...      host type        NUnityEngine:GameObject;
G<digit>                  the declaring type's n-th generic parameter    G0
H<digit>                  the declaring method's n-th generic parameter  H0
?<primitive>              nullable primitive                            ?I -> int?
V                         void — legal only as a closure's return
fullname := modulePath ':' segment ('.' segment)*
segment  := typeName ('`' arity)?               Obox:Box`1;I -> Box<int>
```

Why a descriptor rather than a dotted name: composite types nest. `Array<Dictionary<int, string>>`
needs a real bracket-and-comma parser; `ADIS` does not. The `:` separating module path from type
path lets a resolver split in O(1) instead of probing prefixes to find where the module ends.

**A generic type's arity is mangled into its name segment**, and its arguments follow the name
terminator. Three consequences, all deliberate:

- **Arity is part of identity.** `Box<T>` and `Box<T, U>` are unrelated declarations with unrelated
  `SurtrClass` instances, and each is looked up in its module under the mangled name.
- **Arguments are not.** `Obox:Box`1;I` and `Obox:Box`1;S` share a full name and resolve to the
  *same* `SurtrClass`, exactly as `AI` and `AS` both resolve to `SurtrBuiltIns.Array`. Nothing is
  reified: one class, one method table, one compiled body. What the arguments buy is that a
  signature can tell `f(Box<int>)` from `f(Box<string>)`.
- **Only the last segment's arity counts.** A type nested inside a generic one does not see its
  container's parameters, so `Obox:Box`1.Entry;` names an `Entry` that takes nothing. Reading the
  arity is a side effect of scanning to the terminator, which keeps the whole grammar one pass.
- **A bound is metadata, not a rule left in the compiler.** What `<T : IComparable<T>>` demanded of
  `T` travels as descriptor strings on the type (`SurtrTypeInfo.GenericConstraints`, one list per
  parameter in declaration order), so a module read back from an image can still answer the
  question without re-compiling the declaration. The compiler's `MetadataImporter` rebuilds
  `TypeParameterSymbol.Constraints` from them, and tooling and host interop can read them directly.
  Nothing on an execution path does — slot layout sees `G<n>` as a reference regardless — which is
  the same bargain `GenericParameters` already makes.
- **A method's parameters are the same idea, under their own symbol.** `H<n>` names the declaring
  method's n-th parameter — distinct from `G<n>` so the two can never be confused in a descriptor —
  and `SurtrMethodInfo` carries `GenericParameters`/`GenericConstraints` in the same shape as a
  type. Same customers (the importer, tooling, interop), same non-customers (the execution path):
  erasure means an `H0` slot is a plain reference.
- **The reflection surface is the one runtime reader of those tables.** `Type` exposes
  `genericParameterCount`, `genericParameters()`, `genericConstraints()` and — for a value reached
  from a closed construction, whose `SurtrTypeValue` retains the descriptor that named it —
  `genericArguments()`. One class is shared by every construction, so `SurtrRuntime` caches one
  `Type` value per class plus one per distinct construction descriptor; `SurtrClassReference`
  distinguishes the two with `ContainsOpenParameter()`. Nothing else in the runtime reads them —
  the tables exist for the importer, tooling, interop and this one API.

Backtick is illegal in a Surtr identifier, so a mangled name can never collide with a declared one,
and a non-generic type's descriptor is byte-for-byte what it always was. A generic type has **no
open form**: a name promising one argument and supplying none is malformed, so a generic contract
names itself with its own parameters — `IIterable<T>`'s self reference is `Osurtr:IIterable`1;G0`.

Descriptors are the **canonical form** for comparison, hashing and bytecode. `ToDisplayString()`
exists for diagnostics only — never key off it.

### 3.2 `SurtrTypeHandle` — the descriptor plus what it resolved to

A handle pairs a reference with the `SurtrTypeInfo` it names, starting unresolved. Handles are
**interned per module** in a `SurtrTypeHandleTable`, so resolution runs once per distinct type — and
so a module's handle table doubles as its dependency list. Loading a module is exactly "resolve
every handle, then link"; anything still unresolved is a load failure rather than a surprise
mid-execution.

A handle can resolve to a class *or* an interface. That is why both share `SurtrTypeInfo` and why
`Kind` distinguishes them with a field read rather than a cast.

---

## 4. Classes

`SurtrClass` carries two completely different views of the same declarations, and keeping them
straight is the single most important thing about it.

| | Shape | Who reads it | When it is built |
|---|---|---|---|
| **The compiler's view** | name-keyed dictionaries: `TryGetField`, `TryGetMethods`, `TryGetProperty`, `TryGetNestedClass` | a compiler, tooling, a host | as members are declared |
| **The runtime's view** | flat arrays indexed by a small integer: `InstanceFields`, `VirtualMethods`, `Interfaces`, … | the interpreter | by the linker, at load |

**Nothing on the execution path ever goes through a name.** The runtime tables are *flattened*:
inherited entries are already folded in, so a lookup never walks the hierarchy.

### 4.1 The tables, and what each one buys

```
Ancestors[]            the chain by depth, with Ancestors[Depth] == this
Depth                  how many classes sit above this one
Interfaces[]           every interface satisfied, transitively closed
InterfaceIndexById     interfaceId -> index into Interfaces, open-addressed
InterfaceSlotOffsets   where each interface's block starts in InterfaceMethodSlots
InterfaceMethodSlots   flattened: (interface, contract slot) -> vtable index
InstanceFields[]       instance layout, indexed by SurtrFieldInfo.Slot
InstanceSlotCount      what the allocator needs
ReferenceSlots         which instance slots hold a reference
StaticFields[]         static layout
StaticStorage          the unmanaged block behind them
ReferenceStaticSlots   which static slots hold a reference
VirtualMethods[]       the vtable, indexed by SurtrMethodInfo.VTableSlot
DirectMethods[]        non-virtual instance methods
StaticMethods[]        statics
Constructors[]         never inherited, so never in the vtable
StaticInitializer      run once, at load
```

Four of these are worth explaining, because each is a deliberate trade:

**`Ancestors` is indexed by depth**, with `Ancestors[Depth] == this`. If a type really is an
ancestor, it sits at its own depth in this chain — so `IsSubclassOf` is one bounds compare and one
load at any hierarchy depth, rather than a walk up base pointers.

**`ReferenceSlots` lists which instance slots hold a reference.** It is an optimisation, not a
requirement: values are NaN-boxed, so the collector *could* tag-test every slot. It exists because a
statically typed language already knows which fields are references, so tracing walks the k
reference slots instead of branching on all n. It also sidesteps **NaN aliasing** — a raw double
whose bits land in the tag range would read as a reference, and a table derived from static types
cannot be fooled that way.

**`InterfaceMethodSlots` stores vtable *indices*, not method references.** That keeps it a flat
block of ints in unmanaged memory, and it means an override later in the hierarchy replaces one
vtable entry and every interface routed through it follows along for free.

**`InterfaceIndexById` is an open-addressed table of `(interfaceId, index)` pairs.** Resolving a
contract on a receiver has to happen per interface call, and the receiver's class is exactly what a
call site does not know — so the index cannot be an immediate the way a vtable slot is. A mask, a
load and a compare replaces what used to be a scan.

### 4.2 What is inherited, and what is not

| | Inherited | Keeps its base index |
|---|---|---|
| Instance fields | yes | **yes** |
| Vtable slots | yes | **yes** |
| Interfaces | yes (and transitively closed) | yes |
| Static fields | no — each class owns its own | — |
| Constructors | no | — |

The two "keeps its base index" rows are load-bearing invariants the interpreter depends on: a field
access or a call site **compiled against the base type keeps working on a derived instance**, with
no adjustment. An override replaces its vtable entry *in place*, which is also what makes an
override automatically apply to every interface routed through that slot.

---

## 5. Methods

A method carries three orthogonal axes, and only one of them is modelled by subclassing.

| Axis | Type | Values |
|---|---|---|
| **Where the body lives** | subclass | `SurtrBytecodeMethodInfo` · `SurtrNativeMethodInfo` · `SurtrAbstractMethodInfo` |
| **How a call resolves** | `SurtrMethodDispatch` field | `Direct` · `Virtual` · `Abstract` |
| **What part it plays** | `SurtrMethodRole` field | `Normal` · `Constructor` · `StaticInitializer` |

Only the first is a subclass because only it adds state — a bytecode method has a chunk and an
offset, a native one an entry point, an abstract one nothing. Making dispatch or role a second
subclass axis would multiply against the first and produce types carrying no data of their own.
Keep new axes as fields unless they genuinely add state.

The axes are independent except for one pairing: **abstract dispatch always goes with an abstract
impl kind**, since there is nothing to run. A method is `Direct` by default — Surtr has no implicit
override, so a method is virtual only when it says so.

### 5.1 Where each kind ends up

```
role == Constructor          -> Constructors[]        never inherited, never virtual
role == StaticInitializer    -> StaticMethods[] and StaticInitializer
IsStatic                     -> StaticMethods[]
!IsVirtualDispatch           -> DirectMethods[]       bound at compile time
otherwise                    -> VirtualMethods[]      placed by PlaceInVTable
```

### 5.2 Signatures and overloads

The three method tables are **overload groups** — `Dictionary<string, SurtrMethodInfo[]>` — not one
method per name. Two keys answer two different questions:

* **`SignatureKey()`** — name plus every parameter descriptor, *excluding the return type*. This
  answers "is this the same member". It is what `AddMethod` rejects duplicates on, what the linker
  matches an `override` on, and what an image names an overload by. Excluding the return is what
  lets `Language-Syntax.md` §3.5 say two members differing only in return type are an error rather
  than an overload — they produce the same key.
* **`ToSignature()`** — the whole type as a closure descriptor, return included. This answers "what
  type is this method".

Parameter descriptors in the key are written **erased**: a `G0` is spelled `E`. After erasure they
are the same slot, and without that a class could never implement a generic interface —
`IComparable` declares `compareTo(G0)`, and an implementation naming the same erased slot would miss
the contract's slot by spelling alone. The other half of that bargain is Java's: a class wanting
both `compareTo(Vec2)` and `IComparable<Vec2>` needs the compiler to emit a bridge.

### 5.3 Slot widths: `ArgumentSlotCount` and `ResultSlotCount`

A parameter is not necessarily a slot. A multi-field `value class` and a tuple travel as **`n`
contiguous raw slots** everywhere the VM moves values, so a method's *arity* and its *stack
footprint* are two different numbers, and the metadata carries both:

* **`ArgumentSlotCount`** — how many stack slots a call site must leave for the arguments,
  **receiver included**: the sum of every argument's flattened width, which comes to
  `ParameterCount + 1` for an ordinary instance method and more as soon as a value type appears.
  The receiver is in the count without exception: the frame base is `sp - argsCount` for every kind
  of call, and that one subtraction is only correct if the receiver is counted. An instance method
  **on** a multi-field value class receives its block unboxed, so the receiver contributes that
  block's width rather than one reference — the same rule the compiler's `ApplyValueLayout`
  applies, and the two have to agree or a call emitted against the metadata would not match the
  frame the callee expects. A varargs parameter is one slot whatever its element type, since the
  caller packs the surplus into an array. Metadata read back from an image falls through to this
  derived form when the writer left the sentinel intact, so the count is derived rather than
  trusted blindly.

  The width is **computed on every read rather than cached**, because it consults
  `SurtrTypeHandle.ResolvedType`: before its module's handles resolve there is no layout to read,
  so an unresolved value class falls back to one slot, and a value cached at construction would
  freeze that fallback forever. It costs nothing to derive it — **nothing on the execution path
  reads it**, since the interpreter takes `argsCount` from the instruction; the only consumer is
  the emitter, deciding what to write into a call site. That is also why this being wrong for
  native methods went unnoticed for so long: a compiled method overrides the property with the
  width its emitter computed, so only a *host-declared* native over a multi-field value class was
  mis-sized, and nothing crashed at the point the mistake was made.
* **`ResultSlotCount`** — how many operand-stack slots one call leaves behind: zero for `void`, the
  flattened width for a tuple (from its descriptor) or a multi-field value class (from its linked
  layout), and one for everything else.

**`ResultSlotCount` is not the call opcode's `retCount` immediate**, and conflating the two is the
one mistake this area invites. `retCount` stays the frame protocol's 0/1 gate — *does this call site
want the result at all* — and is unchanged from before value types existed. The width of that one
result rides the callee's declared type, and is what the caller's stack accounting and the host
boundary read. The callee is what emits it, through `ReturnValues`.

Where a block is **packed back into an object** is a short and deliberate list: elements of an array
or a dictionary, dictionary keys, erasure slots (`G0`, `unknown`), and the host boundary
(`SurtrRuntime.Invoke`/`InvokeClosure` flatten arguments on the way in and re-pack results on the
way out). Everywhere else — locals, fields, statics, parameters, returns, the operand stack — the
block stays a block and nothing allocates.

### 5.4 Parameters

`SurtrParameterInfo` carries everything a call site needs to be checked against a member declared in
*another module*, where overload resolution works from metadata rather than a syntax tree:

* a **name**, so a named argument can find it;
* a **default value** (a `SurtrConstant`), so an omitted trailing argument can be filled in;
* a **varargs** mark, so a surplus can be absorbed.

Three shape rules are enforced where the member is declared, not where it is called: defaults are
trailing-only, varargs is last and at most one, and varargs cannot follow a default. A call site
reading this metadata is entitled to assume the shape holds — overload resolution walks the list
once and stops at the first optional parameter, which is only sound if defaults really are trailing.

None of it reaches the interpreter: a call arrives with its arguments already filled in and its
varargs array already packed.

### 5.5 Native methods

A native method's body is a host function reached through `SurtrNativeEntryPoint`, and every host
function has one fixed shape:

```csharp
delegate int SurtrNativeFunction(SurtrCallArguments arguments);
```

so the interpreter has exactly one function-pointer cast on its call path regardless of the
method's Surtr-level signature. The pointer is a **managed** `delegate*`, not
`delegate* unmanaged[…]`: Surtr's host is always C#/Unity, so calling directly avoids the
reverse-P/Invoke stub and its GC transition, and sidesteps IL2CPP's `[MonoPInvokeCallback]`
restriction. Never put an unmanaged address in one.

`arguments[0]` is the receiver for an instance method — argument zero like any other.

**The return is a slot count, and the results are written in place.** The body writes its results
over the argument block it was handed, starting at slot 0, and answers how many slots it wrote —
Lua's multiple-returns bargain. `arguments.Return(value)` is that whole protocol for the ordinary
case: it writes slot 0 and answers 1. A function declared to return nothing writes nothing and
answers 0. A result wider than one slot — a tuple, a multi-field `value class` — is simply several
consecutive slots, exactly as it travels everywhere else, so **there is no second entry-point
signature and no separate results pointer**: one shape still covers every native body, which is the
property the whole call path was built on.

The in-place convention has exactly one rule for a body to keep: **read every input before the
first write.** Results alias the arguments, so a body that writes slot 0 and then reads argument 1
has read whatever it just wrote when the two overlap. Reading first costs nothing and the built-ins
all do it; the checked writers (`Return`, `WriteResult`) bound-check against the block's writable
capacity, but they cannot tell a stale read from a fresh one.

**A body can arrive late.** A method declared with only a `LinkName` is bound when its module is
loaded, by whichever runtime is loading it, through `SurtrRuntime.DefineNativeBody`. That is what
lets a module carrying native members travel in an image. Until it is bound it points at a body that
*reports the mistake* rather than at null.

### 5.6 Native fields and native enums (host bridge)

Two host-facing runtime extensions mirror the native-method story for state and for enums.

**Native fields.** `SurtrNativeFieldInfo : SurtrFieldInfo` is a field whose value lives in the host,
reached through native getter and setter entry points instead of a Surtr slot. It owns no slot in the
instance layout and no static storage — the linker (`BuildFieldLayout`) skips it — and
`FieldGet`/`FieldSet`/`StaticFieldGet`/`StaticFieldSet` recognize it and route the read or write
through the entry points. This is the C# bridge's counterpart of a host field, exposed as a real
Surtr *field* (not lowered to an accessor pair the way a source-level `native let`/`native var` is —
see `Language-Syntax.md` §10; the language has no native-field spelling, only the bridge does). The
getter receives the receiver as argument 0; the setter receives the receiver and the value; a static
native field's entry points receive no receiver. Declared with
`SurtrRuntime.DefineNativeField(...)`.

**Native enums.** `SurtrRuntime.DefineNativeEnum(fullName)` builds a `SurtrClass` with `isEnum: true`
(the native counterpart of a source enum), and `DefineNativeEnumCase(class, name, instance)` adds one
case backed by a cached, rooted `SurtrNativeObject` wrapping the boxed host value. The cached objects
are wired into the case fields' static storage when `FinishNativeClass` links the enum, so `MyEnum.A`
in Surtr resolves to the one shared object and an exhaustive `switch` compiles to a dense jump table.
The C# bridge caches one object per value (the CLR does not cache boxed enums), keyed by underlying
value, so marshaling is O(1) with no boxing per call.

---

## 6. Properties

A property is not a storage kind. It is a name attached to **accessor methods**, declared as
ordinary `get_x`/`set_x` methods exactly as the CLR does it:

```
SurtrPropertyInfo
├── PropertyType
├── Getter -> SurtrMethodInfo?      the class's own get_x
└── Setter -> SurtrMethodInfo?      the class's own set_x
```

The accessors go into the declaring type's method tables and get linked with everything else — a
property whose accessors were in no table would be the one member the linker never sees. Everything
that applies to a method therefore applies to an accessor: it can be virtual, it can be native, it
can satisfy an interface, and it travels in an image as a method.

A get-only property has **no setter at all**, not a private one. An auto-property is a compiler
construct: it synthesises a private backing field and two trivial bodies, and nothing in the runtime
knows the difference.

---

## 7. Interfaces

An interface is a **pure contract**: public abstract methods and properties only. No fields, no
statics, no default implementations — `AddMethod`/`AddProperty` reject anything else, so the
dispatch tables can assume it.

```
SurtrInterface
├── DeclaredExtendedInterfaces[]    by handle, before linking
├── ExtendedInterfaces[]            transitively closed, by the linker
├── MethodSlots[]                   the contract in slot order, inherited slots included
└── InterfaceId                     dense, assigned at link time
```

**Slot numbering is flat per interface.** An extended interface's methods keep the indices their
declaring interface gave them, and this interface's own are numbered after. So one interface has one
numbering, and a call site naming slot *n* means the same thing whichever sub-interface the receiver
was reached through.

Interface method slots are stored in each method's otherwise-unused `VTableSlot`, since an interface
method never occupies a class vtable slot itself.

**Ids are handed out from the context, not restarted per module** — and a context starts its
numbering at `SurtrBuiltIns.ReservedInterfaceIds`, because the built-in interfaces were numbered
before any runtime existed. Without that, the first interface any module declared would collide with
`IIterator`, and the collision would show up not as an error but as a call landing in the wrong
method.

### How an interface call resolves

```
InvokeInterface methodIdx argsCount retCount

  contract      = methodTable[methodIdx].DeclaringType          the interface
  contractIndex = receiverClass.InterfaceIndexById[contract.InterfaceId]     mask + load
  vtableSlot    = receiverClass.InterfaceMethodSlots[
                      receiverClass.InterfaceSlotOffsets[contractIndex]
                      + declaredMethod.VTableSlot ]
  target        = receiverClass.VirtualMethods[vtableSlot]
```

One extra indirection over a virtual call. This depends on `SurtrMethodInfo.DeclaringType` naming
the declaring **interface** for an interface method, which the compiler must honour.

---

## 8. Enums

An enum is not a separate kind of thing. It is **a sealed class with a fixed set of named static
instances** — exactly what `Language-Syntax.md` §2.4 describes and exactly how the metadata stores
it:

* `SurtrMemberKind.Enum` rather than `Class`, so a compiler reading another module's metadata can
  tell.
* Implicitly sealed. It cannot declare a base class, because the enum class itself occupies that
  slot.
* Each case is a **static, read-only field of the enum's own type**, holding an instance the static
  initializer constructs. A case with arguments is a constructor call against the enum's own
  constructor.

`SurtrEnumCaseInfo` adds the one thing a field cannot carry: an **ordinal**, assigned by
`AddEnumCase` in declaration order and never accepted from outside. It exists for one reason — an
exhaustive `switch` over an enum has to compile to a dense jump table, and the cases are references,
which a table cannot index on. With the ordinal it is a `FieldGet` plus an ordinary `Switch`.

It is a struct rather than a class because it is three fields with no identity, always read out of
the array on `SurtrClass.EnumCases`.

---

## 9. Build state: three phases, one cycle detector

Every member, class, interface, module and chunk carries a `SurtrBuildState`:

```
UnderConstruction  ──►  Linking  ──►  Built
```

* **`UnderConstruction`** — declarations are accepted. `AddField`, `AddMethod`,
  `SetDeclaredInterfaces` and friends call `ThrowIfBuilt()` first.
* **`Linking`** — the linker is working on it. Meeting a type that is *already* linking means the
  hierarchy loops back on itself, so the state doubles as the cycle detector at no extra cost.
* **`Built`** — the tables are flattened and slot indices have been handed out. Nothing can be added
  afterwards, because a member appearing now would silently invalidate every index already given
  out.

`SurtrModule` carries a second, independent flag: **`IsEmitted`**, set by
`SurtrModuleBuilder.Build()`. It answers "has the emitter finished laying out the bodies", which is
a different question from "has this been linked" — a module can be written to an image between the
two, and a module that declares nothing has legitimately empty tables either way.

---

## 10. Linking

`SurtrTypeLinker` turns declarations into the runtime's tables. It runs **depth-first: base class and
interfaces first**, since a type's layout is built on top of theirs. It is load-time code and
deliberately favours obvious correctness over allocation-freedom — the dictionaries and signature
strings it builds are discarded once a type is linked.

For each class, in order:

1. **Resolve the base**, rejecting an unresolved handle, an interface used as a base, and a sealed
   base.
2. **`BuildAncestors`** — copy the base chain, append this class.
3. **`BuildInterfaceClosure`** — inherited first (so a base-typed itable index stays valid on a
   derived instance), then declared ones and everything they extend.
4. **`BuildInterfaceIndex`** — the id-to-index probe table.
5. **`BuildFieldLayout`** — inherited fields keep their base slots, then this class's; static
   storage is allocated and each static field is handed the **address** of its own slot, so
   `StaticFieldGet` is one indirect load with no test for where the storage lives.
6. **`BuildMethodTables`** — start from the base vtable verbatim, then place each method.
7. **`BuildInterfaceDispatch`** — index the vtable once by signature, then answer every contract
   slot from that map, rejecting a contract the class does not satisfy.
8. **`VerifyConcrete`** — a non-abstract class may not leave an abstract method in its vtable.
9. **Nested types**, then `MarkBuilt()`.

`PlaceInVTable` is where `override` and `sealed` are enforced: an `override` with no matching base
signature is rejected, so is one overriding a `sealed override`, and so is a method that would
silently hide an inherited virtual without saying `override`.

---

## 11. Values at run time

Metadata describes; `Runtime/Objects` is what actually flows through the interpreter.

### 11.1 `SurtrValue` — the fast path

A NaN-boxed 64-bit value. A primitive is carried **as itself**, with a tag, and never allocates:
`SurtrValue`/`SurtrRawValue` exist purely so the interpreter can move primitives around without
going through class metadata. They are not a separate non-object tier in the language — every value
conceptually has a `SurtrClass`, and `SurtrBuiltIns.ForValue` answers which by reading the tag.

**A reference is its 32-bit payload**, not its tag, so a zeroed slot and an explicit null are the
same reference — which is what lets fresh locals read as null without the VM knowing their declared
type.

### 11.2 `SurtrObject` — everything collectable

| Type | Holds | Class |
|---|---|---|
| `SurtrString` | a CLR string + its cached hash | built-in, shared |
| `SurtrArray` | growable `SurtrValue[]` + count | built-in, shared |
| `SurtrTuple` | fixed `SurtrValue[]`, immutable | built-in, shared |
| `SurtrDictionary` | `Dictionary<SurtrValue, SurtrValue>` under the runtime's comparer, or `Dictionary<int, SurtrValue>` when the declared key is `int` | built-in, shared |
| `SurtrClosure` | method + captured values, dispatch payload copied out flat | built-in, shared |
| `SurtrRange` | two ints and an inclusivity flag | built-in, shared |
| `SurtrIterator` | a collection + a position | built-in, shared |
| `SurtrBoxed` | one primitive value | the *same* class the unboxed primitive has |
| `SurtrInstance` | `SurtrValue[]` field slots | whatever Surtr source declared |
| `SurtrNativeProxy` | a host CLR object | host-declared, or `SurtrBuiltIns.NativeObject` |

Every object carries its `SurtrClass` plus a **cached copy of that class's `TypeCode`** — one byte
duplicated so a family test is a load off the object already in cache rather than a second hop into
metadata.

Rules that run through all of them:

* **Storage is managed, not `SurtrNativeArray`.** These are collectable, and the registry sweeps by
  dropping its reference — there is no finalization hook — so an unmanaged buffer owned by one would
  leak on every collection. Unmanaged arrays belong to *metadata*, which is disposed explicitly.
* **No per-element type tags.** Static typing means the compiler already knows an `int[]` from a
  `string[]`, and NaN boxing means each element self-describes to the collector. What each composite
  keeps instead is one interned descriptor naming its whole parameterised type.
* **`SurtrValueComparer` decides equality**, not raw bits, and lives one per runtime. Bits are too
  strict for strings (two objects, same text, one key) and boxes (a boxed 5 *is* an unboxed 5), and
  too loose for floats (`+0.0`/`-0.0`, `NaN`). A dictionary reaches it only through
  `IEqualityComparer<T>`, which is a dispatch — so a `{int: V}` dictionary skips it entirely and
  keys on the raw payload under the BCL's own comparer, since the compiler has already proved every
  key is an `int`. See `docs/VM-Plan.md` §3.5 for what keeps that an optimisation rather than a
  change of semantics.

---

## 12. The built-in classes

`SurtrBuiltIns` holds one **process-wide** `SurtrClass` per family, built once in a static
constructor into a module named `surtr`, linked before any runtime exists.

Shared rather than per-context so that two runtimes agree on what `string` means and a native entry
point registered against one works in the other. It gets away with being shared because it has **no
per-runtime state**: no static fields, and no string literals to patch. It is deliberately *not* in
any runtime's module table — `TryResolveHandle` reaches it specially — because disposing a runtime
disposes every module that is.

**One class covers every parameterisation.** `AI` and `AS` are both `SurtrBuiltIns.Array`, because a
language with no dynamic top type settles element types at compile time. Their `SelfReference` is
correspondingly the bare family symbol (`A`, `D`, `T`, `L`), deliberately *not* a well-formed
descriptor: it names the family and says nothing about parameters, which is exactly what the class
knows.

**`object` is the root every class extends by default.** Stateless (no fields, no constructors) and
declared first among the built-ins, so `Declare`/`DeclareObject` can default every other one's base
to it. It carries `equals`/`hashCode`/`toString` as `Virtual` members, delegating to
`SurtrValueComparer` so `x.equals(y)` and `x == y` can never disagree. Every primitive and built-in
composite extends it too, and is declared `sealed` — nothing extends `int` or `array` by name, so a
call site whose receiver's static type is one of them devirtualises for free, the same as any other
`sealed` class. `Enum` and `ValueType` are two more stateless classes between it and, respectively,
every concrete enum and every `value class` — `CLAUDE.md`'s "The built-in classes" section has the
rest, including why neither declares `equals`/`hashCode`/`toString` of its own (their concrete
subclasses already get real ones from `EnumMemberSynthesizer`/`ValueMemberSynthesizer`).

`array` and `dict` declare **real generic parameters** — `T`, and `K`/`V` — and their
element-polymorphic members are declared against them through `G<n>`. `G0` resolves to
`SurtrBuiltIns.Erased`, so the runtime representation is exactly what `E` would have been and no
layout, tracing or dispatch path knows the difference; what it adds is *which* parameter it is,
which is what lets `int[].push("x")` be rejected against metadata alone.

`array`, `string`, `tuple`, `dict` and `range` implement `IIterable<T>`, so the contract `for-in` is
defined by is one every collection actually satisfies. A compiled `for-in` over any of them still
lowers to an indexed loop and never allocates a cursor — the contract exists so an `int[]` can flow
into an `IIterable<int>`, not to make loops slower.

---

## 13. The runtime and its context

`SurtrRuntime` is Surtr's `lua_State`: the one object a host holds. It owns a `SurtrContext` — an
internal struct, reached by `ref` so nothing copies it — holding:

```
EntityRegistry     the object heap, addressed by SurtrRef
NativeBodies       host bodies for native members, by link name
Modules            loaded modules, by path
NativeClasses      host-declared native classes, by full name
HostTypeHandles    handles for signatures the host declares outside any module
InternedStrings    text -> one SurtrString, for the runtime's life
Roots              entities kept alive regardless of reachability
StaticBlocks       every class's and module's static storage, so a collection can trace it
NextInterfaceId    the shared counter, starting past the built-ins' reservation
```

* **Interned strings are rooted permanently.** Use `InternString` for text a program is *built
  from*, `NewString` for text it *computes*.
* **Roots are pre-boxed raw values** — the shape the collector wants. A collection stages the
  caller's transient roots in the root buffer's slack past `RootCount`, so merging needs no
  allocation.
* **Static storage is registered at link time**, not discovered per collection: it is unmanaged and
  reachable from no object, so unless a collection walks it explicitly, anything a static field
  solely owns would be swept.

---

## 14. Loading a module, end to end

```
LoadModule(module)
 ├─ reject a duplicate path, or a module already loaded elsewhere
 ├─ register it              (its own types must be findable while resolving)
 ├─ resolve every type handle    ── the handle table is the dependency list
 ├─ bind pending access tables   ── images only: names -> objects
 ├─ bind native bodies           ── every native member, module-level or on a class, by link name
 ├─ LINK every type              ── SurtrTypeLinker, depth-first
 ├─ materialise string literals  ── intern, patch into the constant pool
 ├─ materialise attributes       ── one instance per usage, rooted permanently
 ├─ register static blocks       ── so the collector can trace them
 ├─ mark loaded, retry host handles
 └─ run static initializers      ── each class's, then the module's
```

**Static initializers run eagerly, at load, classes before the module, in declaration order.** Lazy
initialization is what Java does, and it buys initialization-order independence at the price of a
"has this run yet" test on every static access forever, to answer a question that is false exactly
once. Loading a module is a controlled event in an embedded language, so the cost belongs there.

The price is ordering: an initializer that reads another class's statics only sees them if that
class was declared first. **Cross-initializer dependencies are the compiler's to reject** — nothing
at load detects them.

That eagerness is also why `InvokeStatic` carries no type index: nothing has to be triggered at a
call site.

---

## 15. What the compiler owes this model

Things the runtime assumes and will not check. **The compiler meets all of them** — this is a list
of standing obligations, not of outstanding work, so anything here that stops holding is a
miscompile rather than a missing feature:

* **Box a primitive flowing into an erased slot, and `Cast` reading one back out.**
* **Emit `finally` on every exit path**, plus a catch-all that runs it and re-raises. There is no
  `Leave`/`EndFinally`.
* **Reject cross-initializer dependencies.**
* **Name the declaring *interface*** in an interface method's `DeclaringType`.
* **Reject instantiating an `abstract` class** — `ObjNew` does not test it.
* **Emit a bridge** into a generic interface's erased slot where a typed overload is also wanted.
* **Devirtualise on a `sealed` class and below a `sealed override`**, which is most of what those
  modifiers are for.
* **Lower `for-in` over a built-in to an indexed loop**, never through `IIterable`.
* **Lower a range written inline in a loop header to two ints**, allocating nothing.

`docs/VM-Plan.md` §4.8 is the authoritative list.

---

## 16. The Surtr-written standard library (`SurtrStdlib`)

`Surtr.Stdlib/src/surtr/` holds the half of the standard library written in Surtr itself rather
than C# (`Language-Syntax.md` §13.1's rule: native only where a member needs `unsafe`, a raw
pointer or a VM service). `Surtr.Stdlib.Tool` compiles each `.surtr` file to its own `.surtrc`
image, one module per file, committed under `Surtr.Stdlib/build/` — the images are checked into
the repository, not produced on demand at `Surtr.Core`'s own build time. That is a deliberate
consequence of what compiling them needs: a working `Surtr.Compiler`, which itself needs a
built `Surtr.Core` — so a single build cannot both compile the stdlib and embed the result into
the very `Surtr.Core.dll` that compiling it depends on. Regenerate the committed images with
`dotnet build src/Surtr.Stdlib/Surtr.Stdlib.csproj` whenever the `.surtr` sources change.

`SurtrStdlib.LoadInto` (`Surtr.Core/Runtime/SurtrStdlib.cs`) is the loader: given the images
(however a host obtained them — files on disk, its own embedded resources, wherever), it
publishes every `native` link name they declare and loads them with a fixed-point retry, since
an image carries no dependency list until it is instantiated.

**Selective loading** (`StdlibModules`, a `[Flags]` enum: `Core`, `Math`, `Collections`, `Text`,
`All`) lets a sandboxed host load only some of it — `LoadInto(runtime, images, selection)`
filters by each image's own module path (`surtr.math.Math`'s second segment, `math`, against
`StdlibModules.Math`) before delegating to the unfiltered overload. Coarse-grained by design, and
independent: every exception the collections throw is one of the trap-mapped classes the built-in
`surtr` module declares (`InvalidOperationException` among them — §13.3's set stays built-in
precisely so a same-named twin can never split catch-by-type in two), and those names are in scope
in every file without an import, so no category reaches into another. The fixed-point retry loop
remains as the backstop for any cross-category import a future module adds.

**Drift detection**: `Surtr.Stdlib.Tool` also writes `native-link-names.txt` next to the
images — the flat, sorted list of every native link name it actually compiled. A test in
`Surtr.Tests` (`SurtrStdlibTests.EveryNativeLinkNameTheStdlibBuildCompiledIsRegistered`) compares
that list against what `SurtrStdlib.RegisterNativeBodies` publishes, so a `native fun` added to
the stdlib source without a matching C# body registered there fails the test suite instead of
only failing once a host loads a runtime and hits the missing link.
