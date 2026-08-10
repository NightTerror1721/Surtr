# Surtr language syntax

Living design document for Surtr's surface syntax — the textual language that compiles down to
the bytecode and metadata model already implemented (see `CLAUDE.md` and `docs/VM-Plan.md` for
the runtime semantics this syntax must express). Nothing here is final until it has a section;
sections get filled in as decisions are made, in the order they were decided, not necessarily the
order a reference manual would present them.

Where a decision is forced by something already built (e.g. the type descriptor grammar, the
class/interface/method model), that is noted — the syntax has to *say* what the runtime already
*means*, it can't redefine the semantics.

The surface syntax is now fully specified. **§14 is the running list of what it commits the
implementation to** — the runtime pieces several sections lean on that don't exist yet, and the
features deliberately deferred. Read it before treating this as a build plan.

---

## 1. Fundamentals

- **Syntax family: TypeScript/Kotlin-like.** Braces for blocks, `name: Type` ordering, modern
  keyword set. Chosen over a straight C#-clone because Surtr's type system already leans on
  postfix-style composition (descriptors nest as `container<param>`), and over Rust-like because
  Surtr does not need expression-oriented blocks or ownership syntax.
- **Statement terminator: `;` is mandatory.** No ASI. Keeps the grammar unambiguous and the parser
  simple — worth more than the keystrokes it costs, especially for a language whose front end
  doesn't exist yet and needs to stay easy to hand-write.
- **Declaration order: `name: Type`.** Applies uniformly to locals, fields, parameters and return
  types. Matches the family choice and reads left-to-right with type inference (`let x = 5;` vs
  `let x: int = 5;`).
- **Trailing commas are allowed everywhere a comma-separated list appears** — array, dict and
  tuple literals, parameter and argument lists, enum case lists, switch-expression arms, generic
  parameter and constraint lists. One rule with no exceptions to remember, and adding an entry to
  a multi-line list touches one line in a diff instead of two.
- **Mutability: `let` / `var`.** `let` declares an immutable binding (assign-once), `var` a
  mutable one. On a **local**, `let` means it can never be reassigned after its initializer. On a
  **field**, it means the field may only be assigned in a constructor (or, for a `static let`, in
  the static initializer) — C#'s `readonly`, not C#'s `const`. §5.4 covers what `let` does *not*
  mean for a collection. A third tier, `const`, is a value the *compiler* knows and the runtime
  never sees — see §7.

### 1.1 The built-in type names

These are fixed by the descriptor grammar `CLAUDE.md` already commits to, not chosen here — the
surface syntax just gives each descriptor symbol a spelling:

| Surface name | Descriptor | Notes |
|---|---|---|
| `int` | `I` | |
| `float` | `F` | |
| `bool` | `B` | literals `true` / `false` |
| `char` | `C` | |
| `string` | `S` | |
| `void` | `V` | **return position only** — `void` is deliberately not a type per `CLAUDE.md`, so a field, local or parameter can never be declared `void` |
| `range` | *(new — see §5.4)* | a half-open or closed interval of `int`s; the only built-in type here that the descriptor grammar does **not** already have a symbol for |
| `unknown` | `E` | holds any value; must be cast before use (§5.10) |

Composite built-ins (array, dictionary, tuple, closure) have no bare name — they're always written
in the parameterised forms in §5.3. There is no root `object` type; `unknown` is *not* one, and
§5.10 explains why the distinction matters.

A method returning nothing must still write `: void` explicitly; the return-type annotation is
never omitted, so a declaration's shape doesn't change based on what it returns.

Type names are **not** keywords — `int`, `string`, `range`, `unknown` and the rest are ordinary
identifiers resolved in the type namespace, which is what lets a module or class declare a nested
type that shadows one.

### 1.2 Reserved words

Hard-reserved, never usable as an identifier:

```
abstract   alias     as        break     case      catch       class     const
constructor          continue  default   else      enum        false     finally
for        forceinline         fun       if        import      in        inline
interface  internal  is        let       native    null        operator  override
private    protected public    return    sealed    singleton   static    switch
throw      true      try       var       virtual   while
```

Three words are **contextual**, not reserved (§3.2): `this`, `super` and `value` mean something
specific only where they are legal, and remain usable as ordinary identifiers everywhere else.
`value` carries two such roles — the incoming value in a property's `set` accessor, and the
`value class` declaration of §2.9 — and neither costs it its identifier status, because the second
is recognised by the `class` that has to follow it.

The list is deliberately short — it holds only what the grammar actually branches on. Notable
absences, each for a reason already decided above: no `new` (§5.5), no `object` (there is no root
type, §1.1), no `module`/`namespace` (§2.1 derives it from the path), no `extends`/`implements`
(§2.2 uses a single `:` list), no `abstract` on interface members (§2.3), and no `until` (§5.4
uses `..=`).

### 1.3 Naming conventions

Style, not grammar — nothing here is enforced by the compiler, but every example in this document
follows it.

| Kind | Case | Example |
|---|---|---|
| Class, interface, enum, nested type | PascalCase | `Entity`, `IShape`, `Suit`, `Entity.Handle` |
| Interface | PascalCase with `I` prefix | `IIterable`, `ICardSuit` |
| Enum case | PascalCase | `Suit.Hearts`, `State.Idle` |
| Method | camelCase | `move`, `describe`, `getKind` |
| Property | camelCase | `health`, `name` |
| Local, parameter, `var` field | camelCase | `itemCount`, `speed`, `activeCount` |
| Private field | camelCase with `_` prefix | `_name`, `_cache` |
| `static let`, module-level `let`, `const` | PascalCase | `MaxEntities`, `Vec2.Zero`, `Debug` |
| Type alias | PascalCase, like any other type name | `EntityId`, `IntMap` |

Members are camelCase rather than PascalCase, following Kotlin/Java/TS — the family this syntax is
modelled on — even though it diverges from the C#/Unity audience's habit. Types stay PascalCase, so
the two are never confusable at a use site.

**The `_` prefix on private fields is a consequence of that choice, not an independent one.** With
properties in camelCase, a property and the field backing it would otherwise want the same name:

```
private let _name: string;

public name: string {
    get { return _name; }        // without the prefix, `name` here means the property — infinite recursion
}
```

An auto-property (§3.4) hides its backing field entirely and never hits this, so the prefix only
really earns its keep on explicitly-backed properties — but applying it to every private field is
one rule instead of two, and makes "this identifier is a field, not a local" readable at a glance.

A `static let` reads as a constant and is named like one, in PascalCase. An *instance* `let` field
is not a constant — it varies per instance — so it stays camelCase like any other field.

---

## 2. Top-level declarations

### 2.1 Modules — no keyword, derived from file location

Source files use the **`.surtr`** extension. It's unambiguous — nothing else claims it, unlike
`.st` (Smalltalk, and SCADA Structured Text) or `.srt` (subtitles) — and the extra characters are
typed once, when the file is created.

There is no `module` header line in source. A file's module path is derived by the compiler from
its location relative to a configured project source root: directories map to path segments
joined by `.` (e.g. `Ogame/core/Entity.surtr` → module `Ogame.core`), the same way Go derives a
package from its containing directory. Every declaration in the file belongs to that module
automatically — no redundant line to keep in sync with the folder, and it matches the
`modulePath:typeName` shape the descriptor grammar already commits to. Multiple files in the same
directory contribute to the same module. The exact source-root configuration is a compiler/CLI
concern, not a syntax concern — revisit once the front end and its project format exist.

Another module's declarations come into scope through an `import` statement at the top of the
file, above any declarations:

```
import Ogame.core.Entity;
import Ogame.core.*;
```

A named import brings exactly that one type into unqualified scope; a wildcard import
(`ModulePath.*`) brings every top-level declaration in that module into scope at once. Either way,
a name can still be written fully qualified (`Ogame.core.Entity`) even without importing it — the
import is convenience, not a requirement to reference something. A colliding name pulled in from
two imports is a compile error at the point of use, not at the `import` line itself.

### 2.2 Classes

```
class Foo : Base, IBar, IBaz {
    ...
}
```

A single `:` list holds the (at most one) base class and any number of interfaces — nothing in
the syntax distinguishes which is which; that is resolved from each name's own metadata during
linking (a name resolving to a `SurtrClass` is the base, everything else must resolve to a
`SurtrInterface` or it's a compile error). This mirrors `SurtrTypeLinker`'s existing base-then-
interfaces linking order, so the grammar doesn't have to duplicate a distinction the linker
already makes. Omitting the `:` entirely means no base class — there is no implicit root `object`
type to inherit from (per `CLAUDE.md`), so a bare `class Foo { }` sits at depth 0.

Two modifiers apply to the class itself, and they are mutually exclusive:

- **`abstract class Foo`** — cannot be instantiated, may declare `abstract` members (§3.3).
- **`sealed class Vec2`** — cannot be extended. The concept already exists internally, since §2.4
  describes an enum as a sealed class; this just makes it writable. It is worth having beyond
  intent-signalling: a `sealed` class tells the compiler no override can ever exist, so a `virtual`
  member on one can be called directly instead of through its vtable slot — the kind of
  devirtualisation `CLAUDE.md`'s performance rules care about, available here as a static fact
  rather than a guess.

### 2.3 Interfaces

```
interface IBar {
    fun doThing(x: int): void;
    name: string { get; }
}
```

Members are signature-only: a trailing `;` instead of a body, and no `abstract` modifier — every
interface member is implicitly abstract and public, which is all `SurtrInterface.AddMethod` /
`AddProperty` accept anyway (fields, statics and default bodies are rejected there). Writing
`abstract` explicitly would just repeat something the declaration context already guarantees.

An interface may still contain **nested types** — a nested `enum`, `interface`, or `class` — even
though it can't contain static members. A nested type isn't a member with a body or storage the
way a field or a default-implemented method would be, so it doesn't reopen the "pure contract, no
state" rule; it just lets a related helper type live next to the contract it belongs to:

```
interface IShape {
    enum Kind { Circle, Square }
    fun getKind(): Kind;
}
```

### 2.4 Enums — Java-style, each value is a real instance

```
enum Suit : ICardSuit {
    Hearts("♥", true),
    Spades("♠", false),
    Diamonds("♦", true),
    Clubs("♣", false);

    private let _symbol: string;
    private let _isRed: bool;

    constructor(symbol: string, isRed: bool) {
        _symbol = symbol;
        _isRed = isRed;
    }

    fun describe(): string {
        return _isRed ? "red " + _symbol : "black " + _symbol;
    }
}
```

An enum is a sealed class with a fixed set of named static instances. Each case list entry is a
constructor call against the enum's own constructor; a case with no arguments (`enum Color { Red,
Green, Blue }`) just calls the implicit parameterless constructor. The `;` after the case list is
only required when member declarations follow it (same rule as Java), so the simple all-constant
form needs no trailing punctuation:

```
enum Color { Red, Green, Blue }
```

Enums can implement interfaces (`: ICardSuit` above) since each case is a genuine instance, but
cannot declare a base class — the enum class itself occupies that slot.

**Per-case method bodies (Java's anonymous-constant pattern) are not supported.** Behavior always
lives on the enum class itself, shared by every case — branch inside a method on `this` (or on a
field set per-case, like `_symbol`/`_isRed` above) if a case needs to behave differently. Generating
a real anonymous subclass per case is meaningfully more linker/metadata machinery for a feature
most enums won't need; because it would only ever add a new legal form alongside the existing one,
it can be revisited later without breaking anything written against this section.

### 2.5 Module-level members

A module isn't only a container for types. Per `CLAUDE.md`, "a module can contain fields,
properties, methods, classes and enums" — so functions, variables and properties can be declared
directly at file scope, outside any type:

```
let MaxEntities: int = 512;

var activeCount: int = 0;

fun clamp(value: int, min: int, max: int): int {
    return value < min ? min : value > max ? max : value;
}
```

These are what Surtr means by "global", and the language model is explicit that it means nothing
stronger: **there are no true globals**, only module-level members. That isn't just a scoping rule,
it's the storage model — `CLAUDE.md` notes that `StaticFieldGet`/`StaticFieldSet` cover statics
*and* module-level variables precisely because a module variable **is** a static of its module and
reaches its storage the same way. Consequently:

- A module-level `let`/`var` is initialized in the module's own static initializer, in declaration
  order, and runs eagerly at module load — the same rule §3.2 gives for a class's statics, and for
  the same reason.
- `static` is not written on a module-level member. There is no instance of a module for it to
  contrast with, so the modifier would carry no information.
- Visibility works as in §3.1, with `internal` (module-scoped) as the default.

The one genuinely global category is host-declared `native` functions and variables, which can
never be written in Surtr source at all — see §10.

**A module body holds declarations only — there are no loose statements.** Initialization logic
that doesn't fit a field initializer goes in a module-level `static { ... }` block, the same
construct §3.2 gives a class, and it runs at module load in its source position among the other
initializers:

```
let MaxEntities: int = 512;

var lookup: {string: int} = {};

static {
    lookup["idle"] = 0;
    lookup["active"] = 1;
}
```

**There is no `main`.** Surtr is embedded, so the host decides what to call and when: it loads a
module and then invokes whatever it wants by name through `SurtrRuntime.Invoke`, typically many
different entry points over a program's life (an update hook, a spawn callback, an event handler)
rather than one. A single conventional entry point would fit a standalone interpreter, not a
scripting language a game engine drives. Everything module load itself runs is the static
initializer described above — which is why `CLAUDE.md` can specify that statics initialize eagerly
at load, classes before the module, in declaration order.

### 2.6 Nested types and qualified names

A class may nest classes and enums; an interface may nest classes, interfaces and enums (§2.3).
Nesting is written by declaring the type inside the body, and a nested type is named from outside
by qualifying it with its container:

```
public class Entity {
    public enum State { Idle, Active, Dead }

    public class Handle {
        public let id: int;
        public constructor(id: int) { this.id = id; }
    }

    private var state: Entity.State = Entity.State.Idle;
}

let h = Entity.Handle(7);
let s: Entity.State = Entity.State.Active;
```

The `.` separator is not a choice made here — it's what the descriptor grammar already encodes.
`CLAUDE.md` gives a full name as `modulePath ':' typeName ('.' nestedTypeName)*`, so
`Ogame.core:Entity.Handle;` is exactly the descriptor for the `Handle` above. The surface syntax
just writes the same path with the module part resolved by scope (§2.1) instead of spelled out.

**`.` is the only member-access operator, at every level.** The same token reaches a nested type
from its container, a static member from its type, a case from its enum, and an instance member
from a value:

| Expression | Reaches |
|---|---|
| `Entity.Handle` | a nested type |
| `Entity.State.Idle` | an enum case (a static instance of the enum, per §2.4) |
| `Vec2.Zero` | a static field or property |
| `Math.clamp(x, 0, 1)` | a static method |
| `entity.state` | an instance field |

There is no separate `::` for statics or `.`-vs-`->` distinction to learn. Nothing is ambiguous
because a name resolves to exactly one kind of thing — a type, a value, or a member of the thing
on its left — and the compiler knows which at every step.

Visibility applies to a nested type the same as to any other member (§3.1): declared inside a type
it defaults to `private`, so `Entity.Handle` above is only reachable outside `Entity` if declared
`public` or `internal`.

### 2.7 Type aliases

```
alias EntityId = int;
alias IntMap<V> = {int: V};
alias Handler = (Entity, float) -> void;

public class World {
    private alias Bucket = {EntityId: Entity[]};

    private var _buckets: Bucket = {};
}
```

An alias introduces **another name for an existing type, not a new type**. `EntityId` and `int` are
the same type everywhere and interchangeable without conversion — this is `using` in C# and
`typedef` in C, not a Rust newtype. When the point is for the type checker to keep the two *apart*,
the construct wanted is a `value class` (§2.9), which is also erased at runtime but is a distinct
type at compile time.

**An alias costs nothing at runtime.** It is resolved and erased during compilation, down to the
descriptor its target names, so no `SurtrClass`, no handle and no entry in any table ever
corresponds to one. The runtime cannot tell that `Bucket` above was ever written.

- **Aliases may take type parameters** (`alias IntMap<V> = {int: V};`), substituted at each use.
  Composites are where a type is most tiring to write out, so excluding them would miss the point.
- **They may be declared at module level or as a member of a class or interface**, taking a
  visibility like any other member (§3.1) — `private` by default inside a type, `internal` at
  module level.
- **An alias may target another alias.** A cycle among them is a compile error, detected the same
  way `SurtrBuildState.Linking` detects a hierarchy that loops back on itself.

**One consequence worth stating, because it interacts with overloading (§3.5).** Since an alias is
not a distinct type, two members whose parameter lists differ only by an alias have *the same*
signature, and declaring both is a duplicate-member error rather than an overload:

```
fun store(id: int): void { ... }
fun store(id: EntityId): void { ... }   // error: same signature as the above
```

### 2.8 Singletons

```
singleton Registry : IRegistry {
    private var _entries: {string: int} = {};

    public fun register(name: string, id: int): void {
        _entries[name] = id;
    }
}

Registry.register("player", 1);
let r: IRegistry = Registry;      // it is a value, and it satisfies the interface
```

A `singleton` declares a type with exactly one instance, created when its module loads, reached by
the declaration's own name. It may hold state, implement interfaces, and — the part that matters —
**be passed around as a value**.

**There is no `static class`, and that is a deliberate absence.** A module (§2.5) already *is* a
container of fields, properties and functions with no instance, so `static class Math { }` would be
a second mechanism for something the language already has — the same duplication §2.2 avoided by
not distinguishing base classes from interfaces the linker already tells apart. Grouping is
therefore done with a module, which per §2.1 means a directory, Go-style.

A singleton is not that. A module cannot implement an interface and cannot be passed to a function
expecting one, so `Registry` above is genuinely beyond what module-level members express; that gap,
not organisation, is what this declaration is for.

- It cannot declare a constructor (nothing chooses when it is built) and cannot be extended.
- Its members follow §3, with the same visibility defaults as a class.
- It is initialized with the module's other statics — eagerly at load, in declaration order (§2.5).
- Whether it can be a base for anything, or nest inside a type, follows the ordinary rules: it
  cannot be extended, and it may be declared at module level or nested like any other type (§2.6).

### 2.9 Value classes

```
value class EntityId {
    public let value: int;

    constructor(value: int) {
        this.value = value;
    }

    public fun isValid(): bool {
        return value >= 0;
    }
}

fun despawn(id: EntityId): void { ... }

despawn(EntityId(7));
despawn(7);            // error: an int is not an EntityId
```

A `value class` wraps exactly one field. The type checker treats it as **a type of its own**, so an
`EntityId` and an `int` are not interchangeable — but at runtime it is **erased to the field it
wraps**, so passing one allocates nothing and costs exactly what passing the underlying value
costs.

This is what a transparent alias (§2.7) deliberately is not, and it is why the "strong alias" idea
is spelled this way rather than as a second kind of `alias`: expressed as a class it can carry
methods, implement interfaces, and be constructed, which a bare aliasing form could not.

- **Exactly one field**, declared `let`. A value class with two would have nothing to erase to.
- It may declare methods, properties and a constructor, but **cannot extend or be extended** — it
  has no room for an instance layout to inherit or add to.
- Its field may be any type, including another value class.

**Where the erasure stops.** A value class is erased *where the type is statically known*. Flowing
into a slot that only knows it holds a reference — an erased generic parameter (§6), an `unknown`
(§5.10), or a variable typed as an interface it implements — it must **box**, exactly as a
primitive does in the same position, and the boxed form is a real object with a real class. That is
the same bargain Kotlin's `value class` makes, and it is unavoidable: those slots hold a reference
by definition, so something has to be the reference.

The practical consequence is that a value class is free in the code that names its type, and costs
a boxing allocation in the code that does not — so it pays off for ids, quantities and handles
threaded through concretely-typed code, and pays nothing back if it spends its life inside a
generic container.

---

## 3. Class members

### 3.1 Visibility

Four levels, C#-style: `public`, `private`, `protected`, `internal` (module-scoped). Following the
same precedent this whole axis was chosen from: **class members default to `private`**, and
**top-level declarations (a class/interface/enum written directly in a module) default to
`internal`** — nothing is accidentally exposed across a module boundary, and nothing is
accidentally exposed outside a type without saying so.

### 3.2 Modifier order

A consistent left-to-right order for every member:

```
<visibility>? <static>? <sealed>? <virtual|override|abstract>? <inline|forceinline>? <const>? <let|var|constructor|fun|alias|operator>? <name> ...
```

The introducer keyword is what tells the member kinds apart: `let`/`var` a field, `fun` a method,
`constructor` a constructor, `alias` a type alias (§2.7), `operator` an operator overload (§5.6),
and **no introducer at all** a property — `age: int { get; set; }` is a property precisely because
nothing precedes the name. That's why the slot is optional in the grammar above, and it's the whole
disambiguation rule; a field always carries `let` or `var`, so `name: Type` with neither can only
be a property.

Not every modifier is legal on every kind. `operator` in particular takes **none** of them: an
overload is always public and always static, so §5.6 has them implied rather than written.

```
public class Animal : Base, IBar {
    private let _name: string;
    internal var count: int = 0;

    public name: string {
        get { return _name; }
        set { _name = value; }
    }

    public age: int { get; set; }   // auto-property: compiler generates the backing field

    public constructor(name: string) {
        _name = name;
    }

    public virtual fun speak(): string {
        return "...";
    }

    protected static fun helper(): int {
        return 42;
    }
}

class Dog : Animal {
    public constructor(name: string) : super(name) { }

    public override fun speak(): string {
        return "Woof";
    }
}
```

- **`super(...)`** in a constructor's header chains to the base class constructor (must be the
  first thing evaluated, same as C#/Java); a constructor that omits it implicitly calls the base's
  parameterless constructor if one exists. **`this(...)`** in the same position chains to another
  constructor on the same class instead. `super.speak()` calls a base implementation explicitly
  from inside an override.
- **`override` is mandatory** when replacing a virtual member (no implicit override — see §3.3);
  the compiler rejects a method that matches a base signature without either `override` or a
  visibly different signature, so accidental shadowing can't happen silently.
- **Static field initializers run in declaration order**, exactly as `CLAUDE.md` describes for the
  runtime's eager static initializers — a `static var count: int = 0;` compiles into that class's
  static initializer body, alongside every other static field initializer in the class, in source
  order. An explicit `static { ... }` block is also allowed for init logic that doesn't fit a
  single field initializer (e.g. populating a static lookup table); it runs interleaved with the
  field initializers, in the source position it appears.
- **Instance field initializers run at the top of every constructor**, in declaration order, before
  that constructor's own body — so `internal var count: int = 0;` above is initialized on each new
  instance regardless of which constructor built it. A constructor that chains to another with
  `this(...)` does *not* re-run them; the chained-to constructor already did.
- **Three contextual keywords** appear in member bodies: `this` is the receiver of an instance
  member (and the disambiguator when a parameter shadows a field, per §4.4), `super` is the
  base-class receiver described above, and `value` is the incoming value inside a property's `set`
  accessor. All three are contextual — they mean this only in the positions where they're legal,
  and are ordinary identifiers elsewhere.

### 3.3 Method dispatch

No modifier = `Direct` (non-virtual) — the default per `CLAUDE.md`. `virtual` marks a method
overridable and gives it a vtable slot; `override` is required on every member that replaces one;
`abstract` declares a member with no body, legal only inside a class itself marked `abstract`.
This maps directly onto the existing `SurtrMethodDispatch` triad (`Direct` / `Virtual` /
`Abstract`) with no fourth case to invent.

`abstract` on the **class** is its own explicit, mandatory modifier — `abstract class Foo { ... }`
— rather than something inferred from the class containing an abstract member. Requiring it
explicitly lets a class be non-instantiable *without* any abstract members (a common pattern: a
base meant only to be extended, every member already implemented) and the compiler still demands
the keyword be present the moment any member is `abstract`, so the two never drift apart silently.

**`sealed override` closes a branch of the hierarchy.** An override is overridable again by
default; prefixing it with `sealed` stops that, and anything further down that tries is rejected:

```
class Dog : Animal {
    public sealed override fun speak(): string {
        return "Woof";
    }
}
```

The word is `sealed` rather than a fourth concept like `final` because §2.2 already gives it
exactly this meaning on a class — nothing below may redefine this — and the payoff is the same
one: from that point down the implementation is statically certain, so a call on a `Dog` or any
subclass can skip the vtable and, per §3.6, becomes a candidate for inlining. `sealed` is only
legal together with `override`; on a `virtual` or `abstract` member it would contradict itself, and
on a non-virtual one it would say nothing.

### 3.4 Properties

`name: string { get; set; }` is an auto-property: the compiler synthesizes a private backing field
and trivial `get_name`/`set_name` bodies. Replacing either accessor's `;` with a `{ ... }` body
switches that accessor to custom logic while leaving the other one auto-generated if still bare
(`{ get; set { ... } }` is legal). A get-only property (`{ get; }` or `{ get { ... } }` with no
`set`) has no setter at all, not a private one — assigning to it outside a constructor is a compile
error. This is exactly the `get_x`/`set_x` accessor-method shape `SurtrPropertyBuilder` already
wires for built-ins, applied to user-declared classes too.

### 3.5 Signatures: overloading and parameter lists

```
fun log(message: string): void { ... }
fun log(code: int): void { ... }                       // overload — different parameter list

fun spawn(x: float, y: float, hp: int = 100): Entity { ... }   // default value

spawn(1.0, 2.0);                                       // hp = 100
spawn(1.0, 2.0, 50);                                   // positional
spawn(x: 1.0, y: 2.0, hp: 50);                         // named arguments

fun format(pattern: string, args: string...): string { ... }   // varargs
format("{0} {1}", "a", "b");
```

**Overloading is allowed**: several members of a type may share a name as long as their parameter
lists differ. This is the most expensive decision in this document — see the note at the end of the
section on what it costs — but it's what the C#/Kotlin family the syntax is modelled on does, and
working around its absence pushes the problem into member names instead (`logString`, `logInt`).

Rules, in the order the compiler applies them:

1. **Overloads must differ in their parameter list**, not merely in return type. Two members
   differing only by return type are a compile error, since no call site could choose between them.
2. **A candidate is applicable** if the call's arguments can fill its parameters — after defaults
   supply any trailing omissions and after varargs absorbs any surplus — and every argument's type
   is the parameter's type or implicitly convertible to it (`int` → `float` is the conversion that
   makes this non-trivial).
3. **The most specific applicable candidate wins.** An exact type match beats one requiring an
   implicit conversion. A candidate that needs neither defaults nor varargs beats one that does,
   and a non-varargs candidate always beats a varargs one.
4. **Ambiguity is an error, never a silent pick.** If two candidates are equally specific, the call
   is rejected and the call site must disambiguate with a cast.

Parameter list rules:

- **Defaults are trailing only.** Once a parameter has a default, every parameter after it must
  too — otherwise a positional call couldn't skip one without skipping the rest. A default value
  must be a compile-time constant.
- **Named arguments come after positional ones.** `spawn(1.0, y: 2.0)` is fine; `spawn(x: 1.0, 2.0)`
  is not. Once naming starts it continues to the end of the call. Named arguments may appear in
  any order among themselves and may skip defaulted parameters. The `name: value` shape does not
  collide with a lambda's `(name: Type)` parameter list even though both sit inside parens: an
  argument list only ever appears immediately after a callee expression, and a lambda's parameter
  list only ever appears at the start of one — so the parser already knows which it is reading
  before it reaches the `:`.
- **Varargs is at most one parameter, always last**, and cannot itself have a default. Inside the
  body it is an array of the declared element type.
- **`override` matches on the full signature**, so an overload set is inherited and overridden
  member by member; each overload occupies its own vtable slot.

**Two costs this section knowingly accepts.** First, member tables can no longer be keyed by name
alone — `CLAUDE.md` describes `SurtrClass`'s name-keyed dictionaries as "the compiler's view", and
overloading means those keys must carry the signature. That is a real metadata change, tracked in
§14.1. Second, **a varargs call allocates an array per invocation**, which is precisely the kind of
per-call cost `CLAUDE.md`'s performance rules warn about — so varargs is a convenience for
diagnostics-style and host-facing APIs (`format`, `log`), and should be kept off anything that runs
inside a frame budget. The runtime itself never needs it: `SurtrCallArguments` already gives a
native function a counted argument span with no array in sight.

### 3.6 Inlining: `inline` and `forceinline`

```
inline fun clamp(v: int, lo: int, hi: int): int {
    return v < lo ? lo : v > hi ? hi : v;
}

forceinline fun dot(a: Vec2, b: Vec2): float {
    return a.x * b.x + a.y * b.y;
}
```

Inlining splices a callee's body into its call site instead of emitting a call. It is worth having
as an explicit control because a Surtr call is not free: per `CLAUDE.md`'s calling convention every
call checks stack room against the callee's `MaxStackSize`, pushes a frame, reads the target's
`ImplKind`, and returns through the frame base. For a two-line vector helper called inside a loop,
that machinery dwarfs the work.

- **`inline` is a hint.** The compiler weighs it against size and may decline.
- **`forceinline` is mandatory.** The compiler skips its size and cost heuristics entirely — and
  if inlining is *impossible* rather than merely unattractive, it is a **compile error naming the
  reason**, never a silent fallback to a normal call. A `forceinline` that quietly did nothing
  would fail exactly when you most wanted to know.

Four things make inlining genuinely impossible, and they are limits rather than policy, so
`forceinline` rejects them at the declaration:

| Rejected | Why |
|---|---|
| Recursive, directly or mutually | Expansion would not terminate |
| `native` | The body is a host C# method reached through a function pointer, not bytecode to splice |
| `abstract` | There is no body |
| `virtual`, or an interface member | The target is only known per call site; a polymorphic site has no single body to splice |

A **non-virtual method on a `sealed` class (§2.2) is the ideal case**, and this is part of what
that modifier buys: the receiver's implementation is statically certain, so the call needs neither
a vtable slot nor a guard.

Three mechanical consequences, none of which the runtime has to know about:

- **The caller's frame absorbs the callee's locals**, so its `LocalCount` and `MaxStackSize` both
  grow. `SurtrCodeEmitter` computes both rather than accepting them (per `CLAUDE.md`), so this
  needs no hand-maintained bookkeeping — which is exactly why that was worth making automatic.
- **A callee carrying exception handlers can still be inlined**, but its `SurtrExceptionHandler`
  ranges have to be remapped into the caller's table, since handler offsets are chunk-absolute.
- **An inlined call leaves no frame**, so it cannot appear in a stack trace. That is the usual
  bargain, and it matters for `docs/VM-Plan.md`'s diagnostics phase.

**Across a module boundary, inlining needs the callee's bytecode at compile time.** A cross-module
call reads the *callee's* method table, so a `forceinline` on a method in a module compiled
separately and only loaded later has nothing to splice. Whether that is an error or a silent
fallback depends on the build model, which does not exist yet — see §14.

`inline` and `const` (§7) are independent and compose: `const` decides whether a call happens at
compile time at all, `inline` decides how a call that survives to runtime is emitted.

---

## 4. Control flow

### 4.1 If / else and the ternary

Standard C-family form. Parens around the condition are **mandatory**, matching every other
construct in this section:

```
if (condition) {
    ...
} else if (otherCondition) {
    ...
} else {
    ...
}

let label = isRed ? "red" : "black";
```

**Braces around a body are optional**, as in C#/Java — a single statement may follow directly.
This is what keeps a guard clause to one line, which is the case it exists for:

```
if (target == null) return;

for (item in items)
    process(item);
```

The cost of allowing it is the dangling-else ambiguity, resolved the conventional way: **an `else`
binds to the nearest unmatched `if`**. In the following, the `else` belongs to the inner `if`,
regardless of how it is indented:

```
if (a)
    if (b) X();
    else Y();     // belongs to `if (b)`, not `if (a)`
```

The same applies to `while` and both `for` forms. Only a *statement* may follow without braces — a
declaration may not, so `if (x) let y = 1;` is rejected: the binding would go out of scope
immediately and could never be read, so it is always a mistake.

### 4.2 Loops

The classic three-clause form, `while`, and a `for-in` form over both collections and ranges (see
§5.4 for range literal syntax):

```
while (condition) {
    ...
}

for (let i = 0; i < items.length; i += 1) {
    ...
}

for (item in items) {
    ...
}

for (i in 0..items.length) {
    ...
}
```

**What `for-in` can iterate is defined by an interface, `IIterable<T>`** (§13.2), so a user class can
be iterated exactly like a built-in by implementing it. The alternative — hard-coding `for-in` to
the built-in collections — would have been faster in the narrow sense but would leave every
user-defined collection permanently second-class.

The performance concern that raises is real and is handled by lowering rather than by restricting
the language: interface dispatch is a linear scan today (`docs/VM-Plan.md`), and paying that per
iteration would be unacceptable. So **the compiler special-cases the shapes it can prove**, and
emits a direct indexed loop with no interface call and no iterator object for:

- an inline range (`for (i in 0..n)`, per §5.4),
- an `array`, `tuple` or `dict` whose static type is known at the loop,
- any `sealed` type (§2.2) whose implementation it can therefore resolve exactly.

Everything else goes through `IIterable<T>`. The general path exists so the language is uniform;
the special cases exist so the common path costs nothing.

`break` and `continue` target the nearest enclosing loop by default, as in every C-family
language, but a loop can be given a label to `break`/`continue` a specific outer loop from inside
a nested one:

```
outer: for (row in grid) {
    for (cell in row) {
        if (cell.isBlocked) {
            continue outer;
        }
        if (cell.isGoal) {
            break outer;
        }
    }
}
```

### 4.3 Switch — both a statement and an expression form

**Statement form** — C#-style, `case`/`break`, explicit fallthrough by omitting `break`. Compiles
directly onto the existing `Switch`/`SwitchLookup` opcodes:

```
switch (x) {
    case 1:
        ...
        break;
    case 2:
    case 3:
        ...
        break;
    default:
        ...
}
```

**Expression form** — arrow arms, no fallthrough, produces a value and therefore needs the
trailing `;` any expression statement/initializer needs:

```
let label = switch (x) {
    1 -> "one",
    2, 3 -> "two or three",
    else -> "other",
};
```

**The expression form checks exhaustiveness for closed types.** Switching over an `enum` can omit
`else` if every case is listed as an arm; the compiler rejects a non-exhaustive expression-switch
with no `else` the same way it would reject a missing `return` on some path. This is a real safety
net specifically because enum cases are fixed at the enum's own declaration (there's no way to add
a case from outside it) — adding a new case later turns every switch that used to be exhaustive
over that enum into a compile error until it's updated, rather than the new case silently falling
through an existing `else`. The statement form is unaffected by this — `switch` as a statement
never requires a `default`, since it isn't required to produce a value.

**Neither form does pattern matching beyond value/type equality** — no destructuring, no type
patterns as a case in a value-`switch`. `catch (e: T)` (§9) already matches by declared exception
type, which is as far as this goes for now; a `switch (x)` case is always compared to `x` by
value. Building real pattern matching is a substantially bigger piece of compiler design than
anything else in this document, and nothing here needs it yet — deferred until it's a concrete
need rather than spec'd speculatively; adding case forms later is additive to the grammar already
fixed here, not a breaking change to it.

### 4.4 Blocks and scoping

A `{ ... }` block introduces a scope. A local declared in one is visible from its declaration to
the end of that block, and not before it — there is no hoisting.

**A local may not shadow another local.** Redeclaring a name that already exists in an enclosing
scope is a compile error, because it is almost always a mistake rather than an intention:

```
let count = 0;
if (x) {
    let count = 5;      // error: `count` already exists in an enclosing scope
}
```

**A parameter or local may shadow a field**, and this is the one case where shadowing is normal
rather than suspect — it is what makes the conventional constructor readable, with `this.` as the
disambiguator (§3.2):

```
class Vec2 {
    public let x: float;

    public constructor(x: float) {
        this.x = x;       // parameter `x` shadows the field; `this.` reaches the field
    }
}
```

The asymmetry is deliberate. Two locals with the same name in nested scopes carry no information —
one of them is dead or the author lost track. A parameter matching the field it initializes carries
quite a lot, and forbidding it would push every constructor into inventing a second vocabulary
(`newX`, `xParam`) for no benefit.

In practice this comes up mainly for **non-private** fields, since §1.3's `_` prefix on private
fields means a parameter and the field it initializes rarely collide there — `_name = name;` needs
no `this.` at all.

---

## 5. Expressions, operators and literals

### 5.1 Nullability is explicit: `Type?`

A plain reference type (`string`, `Foo`, `int[]`, ...) cannot hold `null` — the compiler rejects
any assignment or parameter that could carry one. Appending `?` (`string?`, `Foo?`) opts a type
into nullability. Three operators work with nullable types:

- **`?.`** — safe navigation; short-circuits to `null` if the receiver is `null` instead of
  faulting.
- **`??`** — null-coalescing; `a ?? b` evaluates to `a` if non-null, otherwise `b`.
- **`!!`** — null-assertion (Kotlin-style); asserts a nullable value is non-null right now,
  throwing if it isn't. An escape hatch for call sites that know more than the type checker does,
  not the default way to consume a nullable.

This is a compile-time discipline only — at runtime a reference is still just its 32-bit payload
either way (`CLAUDE.md` §"The virtual machine"), so `Type?` costs nothing beyond the flow analysis
that enforces it.

**Primitives are nullable too, natively — not through boxing.** `int?`, `bool?`, `char?`, `float?`
are first-class: a null primitive never touches `SurtrBoxed` or the heap, it's a plain value-stack
slot the same as its non-nullable counterpart, just able to also represent "no value." This was a
deliberate choice against the more obvious "auto-box like an erased-slot primitive already does" —
boxing exists to satisfy a *reference-typed* slot (an erased generic parameter), which is a
different problem than "this concretely-typed field can be absent," and paying an allocation plus
an entity-registry id just to represent absence would be exactly the kind of hidden per-value cost
`CLAUDE.md`'s performance rules rule out.

This is possible without disturbing anything already built because the NaN-boxing tag space isn't
full: `SurtrValue`'s 16-bit tag currently claims 5 of 16 possible nibbles (Int/Float-implicit/
Bool/Char/Reference), leaving room for one more reserved tag meaning "null primitive," distinct
from all five existing ones and therefore automatically inert to the collector — `SurtrEntityRegistry.Mark`
and every `VisitReferences` walk already only ever look for the exact reference tag, so a value
carrying a different tag is invisible to tracing without any changes there. `int?`/`bool?`/`char?`
get this for free, at zero cost, the same way `CLAUDE.md` already treats a zeroed reference slot
as `null` "without the VM knowing their declared type." `float?` costs one bit pattern reserved out
of the same currently-unclaimed range — a present `float?` stays bit-identical to a plain `float`
(any real double, any genuine NaN included), only the one reserved "this is the null state" pattern
is carved out, which touches the `IsFloat` boundary check and needs care but no redesign.

Boxing a nullable primitive is still exactly what happens if it needs to flow into an erased
generic slot — same as a non-nullable primitive does today — so `?` doesn't create a second boxing
path, it just means "no value" is representable before boxing ever enters the picture.

**This is a runtime/value-representation decision, not only a syntax one** — the actual VM change
(reserving the tag, updating `IsFloat`, wiring the null-check/coalesce opcodes, `SurtrClassReference`
plumbing for the new value-type family) is out of scope for this document and isn't implemented
yet. It belongs in `docs/VM-Plan.md`'s gap list once work on it starts; what's settled here is only
the source-level contract: `?` means the same thing, uniformly, whether the type after it is a
primitive or a reference.

### 5.2 String interpolation

Double-quoted string literals interpolate directly, Kotlin-style — no separate template-literal
delimiter:

```
let name = "Freyr";
let msg = "Hello, $name! You have ${cart.length} items.";
```

`$identifier` splices a bare variable/field reference; `${ expression }` splices an arbitrary
expression. `\$` escapes a literal dollar sign. There is exactly one string-literal syntax in the
language — no backtick-delimited alternate form to keep in sync with it.

### 5.3 Composite types: arrays, dicts, tuples, closures

Every composite surface type mirrors the shape of a value of that type, so a type annotation and
the literal it accepts read as the same picture:

| Kind | Type syntax | Value literal | Underlying descriptor |
|---|---|---|---|
| Array | `int[]`, `string[]` | `[1, 2, 3]` | `A<elem>` |
| Dictionary | `{int: string}` | `{1: "a", 2: "b"}` | `D<key><value>` |
| Tuple | `(int, string)` | `(1, "a")` | `T(<elem>...)` |
| Closure | `(int, int) -> float` | `(x: int, y: int) => x + y` | `L(<param>...)<ret>` |

Arrays get the terse `T[]` suffix (TS/C#-style) rather than a generic `Array<T>` spelling, since
they're by far the most common composite type and don't need the extra ceremony. Dicts have no
natural one-type suffix, so they use braces around a `Key: Value` pair: `{int: string}` reads as
"the type of `{1: "a"}`" exactly the way `int[]` reads as "the type of `[1, 2, 3]`".

This is a real grammar decision, not just a mnemonic, and it's worth spelling out why it's
unambiguous: a dict-type `{K : V}` is a *self-delimiting* production — the parser reads `{`, a
type, `:`, a type, `}` and the production is complete, so it never needs to guess where a
composite type ends. That matters because `{` already means two other things elsewhere in the
grammar (a statement block, and a dict *literal* per §5.4) — but a **type** never appears in a
position where a **block** or an **expression** is also legal, so there's no slot where the parser
would have to choose between them. A field's dict-typed accessor block is a concrete case of this:

```
private cache: {int: string} { get; set; }
```

`{int: string}` closes at its own matching `}`; whatever follows (`{ get; set; }` here) is parsed
fresh as whatever the grammar expects next, with no lookahead trick required.

Tuple and closure types reuse their literal/lambda shape outright rather than a generic name like
`Tuple<int, string>` or `Func<int, int, float>` — `(int, string)` is a tuple type exactly because
`(1, "a")` is a tuple value, and `(int, int) -> float` is a closure type because
`(x: int, y: int) => ...` is how you write one. `->` (not `=>`) in the type keeps "the type of a
function" visually distinct from "a function value" even though they're always used in matching
positions.

### 5.4 Collection and range literals

```
let nums: int[] = [1, 2, 3];
let scores: {string: int} = { "alice": 10, "bob": 7 };
let point: (int, int) = (3, 4);

for (i in 0..10)  { ... }   // exclusive: 0..9
for (i in 0..=10) { ... }   // inclusive: 0..10
```

Ranges follow Rust's convention (`..` exclusive of the upper bound, `..=` inclusive) rather than
Kotlin's (`..` inclusive, `until` exclusive) because the exclusive form is what a length-bounded
`for` loop wants by default — `for (i in 0..arr.length)` reads correctly without an off-by-one.

**A range is a first-class value of type `range`**, not merely `for-in` header syntax. It can be
bound, passed and returned like anything else:

```
let visible: range = 0..itemsPerPage;

fun rows(count: int): range {
    return 0..count;
}

for (i in rows(10)) { ... }
```

Both bounds are `int`; there is no `float` or `char` range. That keeps `range` a bare descriptor
symbol rather than a parameterised type like `A`/`D`/`T`/`L`, which in turn keeps it out of the
nesting grammar entirely.

Two consequences worth being explicit about, because "first-class" is the more expensive of the
two options that were on the table here:

- **It needs a new descriptor symbol.** `range` is the one entry in §1.1's table that the encoding
  in `CLAUDE.md` has no letter for, so adding it means claiming one (`R` is free) and adding a
  matching built-in class. That is real runtime work, tracked in §14.1.
- **`for-in` over a range must not allocate.** A `range` is an object, and allocating one per loop
  entry would be exactly the kind of hidden per-iteration cost `CLAUDE.md`'s performance rules
  forbid. The compiler is therefore required to lower `for (i in <lo>..<hi>)` — where the range
  expression is written inline in the header — into a plain counted loop over two `int`s, with no
  range object ever created. Only a range that genuinely escapes into a variable, parameter or
  return value is materialised as one.

A dict literal at the very start of a statement is syntactically ambiguous with a block, and it's
resolved the same way JS resolves it: **a `{` in statement position always starts a block**, never
a dict literal. Writing one bare as an expression statement therefore needs wrapping parens —
`({ "a": 1 });` — which in practice never comes up, because a dict literal appears as an
initializer, an argument or a return value, all of which are expression positions where no block
is legal and no ambiguity exists:

```
let scores = { "alice": 10 };        // fine: expression position
configure({ "mode": 1 });            // fine: argument
{ "a": 1 };                          // parsed as a block, not a dict — needs ( ) to be a literal
```

**`let` on a collection is shallow, C#-`readonly`-array style**: it stops `nums` from being
rebound to a different array, but the array's contents stay mutable in place (`nums[0] = 9;`
still compiles even when `nums` was declared with `let`). A genuinely immutable collection would
need either a second, frozen array type or a runtime mutability flag on every array/dict/tuple
object — real additions to the object model in `Runtime/Objects` for a guarantee nothing has asked
for yet, so it's left for later rather than designed in now. `SurtrTuple` is the one exception,
and it isn't one *because* of this decision — it's already immutable by construction, per
`CLAUDE.md`'s object-model table, independent of how it was bound.

### 5.5 Constructing instances: no `new`

A constructor call looks like an ordinary function call — `Foo(name)`, matching every example in
§2–§3 — with no `new` keyword. There's no syntactic distinction between instantiating a class and
calling a function, which fits a language where every value already has a `SurtrClass`; the two
aren't different *kinds* of operation the way they are in a language with a value/reference-type
split at the syntax level.

### 5.6 Operator overloading

```
class Vec2 {
    public let x: float;
    public let y: float;

    constructor(x: float, y: float) {
        this.x = x;
        this.y = y;
    }

    operator+(a: Vec2, b: Vec2): Vec2 {
        return Vec2(a.x + b.x, a.y + b.y);
    }

    operator-(v: Vec2): Vec2 {
        return Vec2(-v.x, -v.y);
    }

    operator==(a: Vec2, b: Vec2): bool {
        return a.x == b.x && a.y == b.y;
    }

    operator as(v: Vec2): Vec3 {
        return Vec3(v.x, v.y, 0.0);
    }
}
```

An overload is declared with `operator` followed by the token it overloads, taking the operator's
operands as ordinary parameters. `operator` is an introducer keyword in its own right (§3.2) —
there is no `fun`, no `static` and no `public`, because **an overload is always public and always
static and can be nothing else**, so writing either would be three tokens that carry no
information. A use site resolves through it: `a + b` becomes `Vec2.operator+(a, b)` when both
operands are `Vec2`. This is aimed squarely at game-math types (`Vec2`/`Vec3`/`Quaternion` and
friends), where writing `a.add(b)` everywhere would cost real readability for no benefit.

**What may be overloaded**, and what each declaration gives you for free:

| Declare | Arity | Also gives you |
|---|---|---|
| `operator+` `operator-` `operator*` `operator/` `operator%` | 2 | the matching compound assignment (`+=`, `-=`, `*=`, `/=`, `%=`) |
| `operator&` `operator\|` `operator^` `operator<<` `operator>>` `operator>>>` | 2 | the matching compound assignment (`&=`, `\|=`, `^=`, `<<=`, `>>=`, `>>>=`) |
| `operator-` | 1 | unary negation, told apart from binary `-` by arity |
| `operator!` `operator~` | 1 | — |
| `operator++` `operator--` | 1 | both the prefix and the postfix form |
| `operator==` | 2 | `!=`, as its negation |
| `operator<=>` | 2 | all four of `<`, `<=`, `>`, `>=` — and `<=>` itself is usable directly (§5.7) |
| `operator[]` | 1 | indexed read |
| `operator[]` returning `void` | 2 | indexed write: the index, then the value |
| `operator as` | 1 | an explicit cast to the declared return type |

Four of these need their behaviour pinned down, because the declaration alone does not say it:

- **`operator<=>`** is a three-way comparison returning `int` — negative, zero or positive, the
  shape of a `compareTo`. Declaring the four relational operators separately is not possible, and
  that is deliberate: they are not independent, and letting a type claim that `a < b` and `a >= b`
  are both true would make sorting and range logic quietly wrong.
- **`operator++`/`operator--`** take the value and *return* the new one; they do not mutate their
  operand. The compiler expands `x++` into an assignment back to `x`, which is what makes one
  declaration serve both the prefix and the postfix form — the difference between them is which
  value the surrounding expression sees, not which function runs.
- **`operator as`** is an **explicit** conversion only: it applies where the source writes
  `v as Vec3`, never on its own. User-defined *implicit* conversions were deliberately left out —
  §3.5's overload resolution already has `int` → `float` as its hard case, and letting user types
  join in would multiply the candidate set and turn ambiguity diagnostics into guesswork. The
  target type is the declared return type, so both directions of a conversion are separate
  declarations.
- **`operator[]` is one-dimensional.** The read form takes exactly one index; the write form takes
  the index and then the value, and returns `void`. There is no multidimensional indexing anywhere
  in Surtr — an array descriptor is `A<elem>` and a dictionary is `D<key><value>`, both with a
  single key, so `int[][]` is an array *of arrays* rather than a 2D array. C#'s `a[i, j]` has no
  counterpart, and giving the overload an arity the rest of the language cannot express would only
  create a form nothing else could consume.

**What may not be overloaded**, each for a specific reason:

- **`&&` and `||`** — they short-circuit, so an overload would have to receive an unevaluated
  operand. Nothing in the calling convention can express that.
- **`===` and `!==`** — reference identity is exactly the thing that is supposed to be beyond a
  type's reach (§5.7). If `===` were overloadable there would be no way left to ask whether two
  references are the same object.
- **`??`, `?.`, `!!`** — nullability is a compile-time discipline (§5.1), not a runtime operation a
  type participates in.
- **Assignment `=` itself**, which would make it impossible to reason about what a binding holds.
- **`true`/`false`**, C#'s pair for using a custom type directly in a condition. A condition takes
  a `bool`, full stop: with `operator as` available, `if (x as bool)` says the same thing and says
  it visibly. In C# that pair exists mostly to serve tri-state SQL-like types and to let `&&`/`||`
  work on custom types — a corner almost nobody reaches, in exchange for making every condition
  site check for a user-defined operator.

At least one operand must be the declaring type: a type cannot define how two types that are both
foreign to it interact.

### 5.7 Operators and precedence

Standard C-family set, closest to C#'s own table, with two deliberate omissions and one addition
already decided in earlier sections:

- **No `**` exponentiation operator.** `2 ** 10` isn't legal; use a `Pow`-style function. Matches
  C#/Java, and avoids the one operator in the whole table that would need right-associativity
  while everything else here is left-associative.
- **`/` between two `int`s truncates** (`7 / 2` is `3`); mixing an `int` and a `float` operand
  promotes the whole expression to `float` division (`7 / 2.0` is `3.5`). Matches C#/Java/Kotlin
  exactly — no surprise for the target audience.
- **Explicit cast is postfix `as`**, Kotlin/TS-style: `let d = obj as Dog;`, not a C-style prefix
  `(Dog) obj`. Fits the family already chosen and avoids a second, ambiguity-prone use of
  parentheses. It pairs with a safe form, `as?`, which evaluates to `null` on a failed cast instead
  of throwing — the same shape `?.`/`??` (§5.1) already give the rest of the nullable-safety
  family, so `obj as? Dog` reads exactly like the rest of that group.
- **`is` tests a type without casting**: `if (x is Dog) { ... }`, evaluating to `bool`. It adds no
  machinery — it is the same walk up `Ancestors` that `catch` clause matching already performs
  (§9), and `CLAUDE.md` notes that walk is one compare plus one load at any depth because
  `Ancestors[Depth] == this`. `x is Dog` does *not* narrow `x`'s static type inside the branch;
  narrowing is type-flow analysis, which belongs with pattern matching (§14.3) rather than here, so
  the body still needs `x as Dog` to use it as one.

From lowest to highest precedence (adjacent rows share a precedence level; unary and assignment
are right-associative, everything else is left-associative):

| Precedence | Operators | Notes |
|---|---|---|
| 1 (lowest) | `= += -= *= /= %= &= \|= ^= <<= >>= >>>= ??=` | assignment, right-associative |
| 2 | `?:` | ternary |
| 3 | `??` | null-coalescing |
| 4 | `\|\|` | logical or |
| 5 | `&&` | logical and |
| 6 | `\|` | bitwise or |
| 7 | `^` | bitwise xor |
| 8 | `&` | bitwise and |
| 9 | `== != === !==` | equality — see below |
| 10 | `< <= > >=` `is` | relational and type test |
| 11 | `<=>` | three-way comparison — see below |
| 12 | `..` `..=` | range construction |
| 13 | `<< >> >>>` | shift — see below |
| 14 | `+ -` | additive (also the overloadable slot from §5.6) |
| 15 | `* / %` | multiplicative |
| 16 | `as` `as?` | cast |
| 17 | `! - ~ ++ --` (prefix) | unary not / negate / bitwise-not / pre-inc/dec, right-associative |
| 18 (highest) | `++ --` (postfix) `.` `?.` `!!` `()` `[]` | postfix inc/dec, member access, call, index |

`&&`/`||` short-circuit as usual. `++`/`--` exist in both prefix and postfix form with the
conventional C-family semantics (prefix evaluates to the updated value, postfix to the value
before the update).

**`<=>` is an ordinary expression operator**, not only the declaration form in §5.6. `a <=> b`
evaluates to an `int` — negative, zero or positive — on the built-in ordered types as well as on
any type declaring `operator<=>`, so an ordering can be obtained in one comparison instead of two.
It sits tighter than the relational operators and looser than shift, following C++20, which is the
only placement that makes `a <=> b < c` parse the way it reads. On a built-in it lowers to the
comparison opcodes that already exist; no new instruction is involved.

**The two right shifts are distinct, and both already exist in the VM.** `>>` is *arithmetic*: it
replicates the sign bit, so a negative value stays negative (opcode `Sar`). `>>>` is *logical*: it
fills with zeroes, so a negative value becomes a large positive one (opcode `Shr`). Surtr's `int`
is 32-bit and signed, which is exactly when the distinction matters — packing bit fields or mixing
a hash, where sign extension corrupts the result. Both opcodes were already implemented; before
`>>>` was added to the surface syntax, `Shr` had no spelling at all and was unreachable from Surtr.
Shift counts mask to `& 31` (`docs/VM-Plan.md` §1.9), so an over-wide count is defined rather than
undefined.

**There are two equality families, and the split is inherited from the runtime rather than
invented here.** `==`/`!=` are *value* equality: they go through the runtime's `SurtrValueComparer`
(one per runtime, per `CLAUDE.md`), which is why two distinct `SurtrString` objects holding the
same text compare equal, a boxed `5` equals an unboxed `5`, and tuples compare structurally — and
which is also what an `operator==` overload (§5.6) hooks into. `===`/`!==` are *reference
identity*: same object or not, ignoring any overload, mapping onto the `R`-prefixed opcodes
`CLAUDE.md` already documents for exactly this. On a primitive the two coincide; the distinction
only means something for reference types.

### 5.8 Literal grammar

```
let a = 42;                           // int
let b = 3.14;                         // float — a decimal point (or an exponent) is what makes
let c = 6.02e23;                      //   a literal float; no suffix character is involved
let d = 0x2A;                         // hex, int
let e = 0b0010_1010;                  // binary, int
let f = 1_000_000;                    // digit-group separators, any base

let g = 'a';                          // char
let h = '\n';                         // char, escape sequence
let i = "line1\nline2\t\"quoted\"";   // string, escape sequences

let t = true;                         // bool — the two literals are `true` and `false`
let n = null;                         // the null literal (legal only against a `Type?`, per §5.1)
```

Digit-group separators (`_`) are allowed anywhere between digits, in any of the three bases, and
are purely visual — `1_000_000` and `1000000` are the same literal. Float-vs-int is inferred from
the presence of a decimal point or an exponent (`e`/`E`), never from a suffix character. Standard
C-family escapes apply inside both `char` and `string` literals: `\n \t \r \\ \' \" \0` plus
`\uXXXX` for a Unicode code point — the same set `"..."` interpolation in §5.2 already assumes for
its own `\$` escape.

### 5.9 Type inference and its limits

Inference is **local and one-directional**: a declaration's type is inferred from its initializer,
and an expression's type may be informed by the type it is being assigned *to*, but nothing is
inferred from how a name is used later.

```
let a = 42;                        // int
let b = Vec2(1.0, 2.0);            // Vec2
let c: int[] = [];                 // fine — the annotation supplies the element type
let d = [];                        // error: nothing determines the element type
let e = [1, 2, 3];                 // int[] — inferred from the elements
configure({});                     // fine if Configure's parameter is a dict type
```

An empty `[]` or `{}` carries no element type of its own, so it is legal only where something else
supplies one — an annotation, a parameter type, or a declared return type. Where nothing does, it
is an error rather than a deferred decision: inferring it from a later use would need backtracking
inference, and the diagnostics that produces when it fails are famously hard to read.

The same target-typing rule is what lets lambda parameters go unannotated (§8). Annotation is
mandatory in exactly three places, all of them cases where there is nothing to infer *from*: a
member's declared type and return type (a signature is the contract, so it is always written out),
a parameter, and a `let`/`var` with no initializer.

### 5.10 `unknown`

```
native fun parseJson(text: string): unknown;

let raw: unknown = parseJson(payload);

if (raw is int) {
    let n = raw as int;
}

let count: int = raw;        // error: an `unknown` must be cast before use
```

`unknown` holds a value of any type and lets you do **nothing** with it until you cast. It is not
an escape from static typing — it is a way to say "the type is not known *here*" while keeping the
obligation to establish it before use. `is` (§5.7) tests it, `as` and `as?` extract from it.

**There is no `any`.** The TypeScript-style companion that also switches type checking *off* was
considered and left out: a value you can call anything on and assign anywhere propagates through a
codebase, and it contradicts the premise that every member signature is fully known at compile time
— which is what lets the runtime resolve calls from metadata instead of discovering them.

Two things make this nearly free rather than a new pillar of the type system:

- **It reuses the erased slot that already exists.** `unknown` is `SurtrValueTypeCode.Erased`,
  descriptor `E`, given a surface name — the same representation `CLAUDE.md` already defines for a
  generic type parameter. So an `unknown` slot is always a reference, always traced, and
  `IsReferenceType` stays a range compare. Primitives box on the way in and the compiler inserts a
  `Cast` on the way out, exactly the two obligations §6 already places on it for generics.
- **It is not a supertype.** There is no root class in Surtr (§1.1), so `unknown` cannot sit above
  anything in `Ancestors`; assignability *to* `unknown` is a rule the compiler applies, not a
  subtype relation the runtime walks. Nothing in the class hierarchy or the linker changes, which
  is precisely why a real top type was not the design chosen.

The motivating cases are host interop where a native function returns something the signature
cannot name, and genuinely heterogeneous data. Where the type *is* known, a generic (§6) says so
better and costs the same.

---

## 6. Generics

```
class Box<T> {
    private let _value: T;

    constructor(value: T) {
        _value = value;
    }

    fun get(): T {
        return _value;
    }
}

fun max<T : IComparable<T>>(a: T, b: T): T {
    return a.compareTo(b) >= 0 ? a : b;
}
```

Type parameters take angle brackets, as in every language in the family. A constraint is written
inline against the parameter it bounds (`<T : IComparable<T>>`), not in a trailing `where` clause —
the common case is one simple bound, and putting it right next to the parameter it constrains
reads better there than split across the declaration. Multiple bounds on one parameter combine
with `&`: `<T : IEquatable<T> & IComparable<T>>`. A `where`-style trailing clause for cases this
doesn't cover well (many parameters, long bounds) is deliberately not added yet — revisit only if
the inline form turns out to be insufficient in practice.

**Generics are erased**, per `CLAUDE.md` — this section is only about the source-level ceremony of
declaring and constraining a type parameter, not about their runtime representation. The two
obligations `CLAUDE.md` places on the compiler (box a primitive flowing into an erased slot, cast
on the way back out) are exactly that: compiler-generated, invisible in source. There is no `box`
or `unbox` operator to write — a program using generics looks exactly like it would in a language
with reified ones.

**Declaration-site variance (`out T` / `in T`) is not supported.** Every generic type parameter is
invariant, matching pre-wildcards Java rather than Kotlin. Adding variance annotations later is
backward-compatible — unannotated existing code keeps meaning exactly what it means today — so
this is deferred rather than designed now, in line with not building type-checking machinery ahead
of a concrete need for it.

---

## 7. Compile-time evaluation

Three constructs share one idea: work the compiler does so the runtime doesn't have to. They sit
next to generics (§6) for that reason — both are things the VM never learns happened.

### 7.1 `const` bindings

```
const MaxEntities: int = 512;
const Greeting: string = "hello";
const Doubled: int = MaxEntities * 2;

public class Physics {
    const Gravity: float = -9.81;
}
```

A `const` is a value fixed at compile time. It differs from `static let` in *when* it exists:
a `static let` is a real storage slot written by the module's static initializer at load (§2.5),
while a `const` is folded into every place it is used and has no slot at all.

- Its initializer must be a **constant expression**: literals, other `const`s, operators over them,
  and calls to `const fun` (§7.2).
- A `const` is implicitly static — there is no per-instance constant — so `static` is not written
  on one.
- It takes a visibility like any member, and is named in PascalCase (§1.3).

**Its type must be a primitive or `string`**, exactly as in C#. That is not a limitation to work
around, because the thing it seems to rule out — a precomputed table — is already covered without
it. A composite value cannot be substituted at a use site anyway (each use would need its own
object, which is not what a constant means), and `static let` plus a `const fun` gets the whole
benefit:

```
static let Sines: float[] = buildSineTable(256);
```

That initializer is a `const fun` call with constant arguments, so §7.2 already guarantees it is
folded: the compiler runs it, and the module's static initializer merely materialises the finished
array at load rather than computing anything. Giving `const` a second, different meaning for
composite types would have bought nothing and made the keyword mean two things depending on its
type.

### 7.2 `const fun`

```
const fun square(x: int): int {
    return x * x;
}

const fun buildSineTable(size: int): float[] {
    var table: float[] = [];
    for (i in 0..size) {
        table.push(sin((i / size) * TwoPi));
    }
    return table;
}

const Sixteen: int = square(4);                    // folded at compile time
static let Sines: float[] = buildSineTable(256);   // also folded; materialised once at load
let n = square(runtimeValue);                      // an ordinary call
```

A `const fun` is a function the compiler *may* evaluate, following C++'s `constexpr` and Rust's
`const fn` rather than `consteval`: **if every argument is a constant expression it is folded, and
otherwise it compiles and runs as an ordinary function.** One declaration serves both uses, instead
of forcing a duplicate for the runtime case. In a position that *requires* a constant — a `const`
initializer, a `const if` condition — folding is mandatory and failing to fold is an error.

**What a body may contain:** loops, conditionals, local variables, locally-created arrays, strings
and dicts, and calls to other `const fun`s. What it may not: `native` functions (§10), mutation of
anything non-local, and any I/O. A `const fun` may not be `virtual` or `abstract` either, since
folding requires knowing statically which body runs.

**`const` implies neither `inline` nor `forceinline`** (§3.6), in either direction, and the two
should not be conflated. They apply to disjoint situations: when a call is folded there is no call
left to inline, and when it is *not* folded — because some argument was not constant — the decision
about inlining it is an ordinary size-and-cost one that has nothing to do with the function being
const-evaluable. The example above is the case in point: `buildSineTable` loops over 256 elements,
so an unfolded call to it is exactly the kind that should *not* be spliced into every call site.
Write `const forceinline fun` when a function genuinely wants both.

What is true, and is worth the compiler exploiting, is narrower: **a `const fun` is guaranteed
pure**, since its restrictions rule out native calls, non-local mutation and I/O. That makes an
unfolded call safe to hoist out of a loop or to reuse across identical argument lists, and makes it
a better inlining candidate than an arbitrary function of the same size. But that is a heuristic
the compiler may apply, not a modifier the language implies — it stays a size-and-cost judgement,
just one made with more information.

**How it is evaluated is the interesting part, and it is why this is affordable at all.** Surtr
already has a VM. Rather than writing a second, separate constant-folding interpreter in the
compiler — and keeping the two agreeing about integer overflow, string equality and every trap in
`docs/VM-Plan.md` §1.9 — the compiler emits the function's bytecode and *runs it*, on the same
interpreter the program will run on. Compile-time and runtime semantics then cannot drift, because
they are the same code.

Two things follow from that, both real:

- **Evaluation is bounded by an instruction budget.** A `const fun` can loop, so it can loop
  forever; the evaluating run carries a step limit and aborts with a compile error rather than
  hanging the compiler. `SurtrRuntime.ResetExecution` already exists to leave the machine clean
  afterwards.
- **It puts a cycle in the build pipeline.** `CLAUDE.md`'s emitter order is
  declare → emit → `Build()` → `LoadModule`, but const evaluation needs a *built* function before
  the code that calls it can be emitted — and with `const if`, the folded result decides what gets
  emitted at all. The const-evaluable subset therefore has to be built and evaluated as its own
  earlier pass. This is the hardest part of the feature and is tracked in §14.

### 7.3 `const if`

```
const if (Debug) {
    log("verbose");
}

const if (Platform == "IL2CPP") {
    fun allocate(size: int): Buffer { ... }
} else {
    fun allocate(size: int): Buffer { ... }
}
```

`const if` takes a condition that must be a constant expression, and the branch not taken is
**removed before compilation proper** — this is Surtr's answer to `#if`, without a preprocessor.

**The untaken branch is parsed but never bound or type-checked.** It must be syntactically valid
Surtr; nothing else about it is verified. That is a deliberate trade, and it is the whole reason
the feature is usable: a branch guarded on one platform routinely names host types and `native`
functions that do not exist in the build being compiled, and a rule that type-checked it anyway
(C++'s `if constexpr` outside templates) would only ever be an optimisation, never conditional
compilation. The cost, stated plainly: **a typo in a dead branch survives until a build that turns
it on.**

`const if` is legal in two places:

- **As a statement**, anywhere an `if` is. It introduces a scope exactly like `if` does, so a
  binding declared inside it is not visible after it.
- **At declaration level**, in a module or a type body, wrapping declarations. A member in an
  untaken branch does not exist in any sense — no field slot, no vtable entry, no metadata, no
  entry in a member table. This is the form that actually replaces `#if`, and it is why the scope
  rule above is not a limitation: when a declaration has to outlive the condition, it goes here.

There is no `const else if` spelling to learn — `else` may be followed by another `const if`,
exactly as with an ordinary `if`.

### 7.4 Build-defined constants

Conditional compilation needs facts from outside the source — the equivalent of `UNITY_EDITOR`.
The build configuration may define named constants, and they behave **exactly as if declared
`const` at the top of every module**:

```
// The build defines: Debug = true, Platform = "IL2CPP"

const if (Debug) { ... }
const if (Platform == "IL2CPP") { ... }
```

They need no import and no special syntax at the use site, because they are not a separate
mechanism — they are ordinary `const`s that the compiler, rather than a source file, declares.
Everything in §7.1 through §7.3 applies to them unchanged.

Two rules keep them predictable:

- **A build constant always has a value.** There is no "is this defined" test, and referencing an
  undefined name is an ordinary undefined-name error. An optional flag is defined as `false` by the
  build rather than left absent, which means a typo in a flag name is caught instead of quietly
  evaluating to "not defined" — the classic `#ifdef` failure.
- **A module may not declare a member with the same name.** Shadowing a build flag would be
  invisible at the use site and is rejected rather than resolved.

Where the build gets them from — a project file, a CLI switch, the host embedding the compiler —
is a build-model question, and the build model does not exist yet (§14).

---

## 8. Closures and functions as values

```
let add = (x: int, y: int) => x + y;
let log = (msg: string) => { print(msg); };

let double: (int) -> int = (x: int) => x * 2;
```

A lambda is a `(params) => expr` for a single expression, or `(params) => { ... }` for a block
body with explicit `return`s; its type is the `(T1, T2) -> R` shape from §5.3, which a `let`/`var`
can be annotated with the same way any other type can.

**Parameter annotations may be omitted where a target type supplies them** (§5.9) — which is most
of the time, since a lambda is usually being assigned to a typed binding or passed to a typed
parameter:

```
let double: (int) -> int = (x) => x * 2;      // x is int, from the annotation on `double`
items.sort((a, b) => a.score - b.score);      // a and b from sort's parameter type

let f = (x) => x * 2;                          // error: nothing determines x's type
let g = (x: int) => x * 2;                     // fine
```

A lambda's return type is always inferred from its body and is never written.

**Captures are by value, not by reference to the variable slot** — this isn't a syntax choice, it
falls directly out of how `SurtrClosure` is already built (`CLAUDE.md`: "method + captured values,
with the dispatch payload copied out flat"). A closure copies out the *value* each captured local
holds at the moment the closure literal is evaluated; there is no shared cell a closure and its
enclosing scope both mutate through. For a captured reference (an object), that value is the
reference itself, so the *object* is still shared — mutating its fields through the closure is
visible outside, exactly as in C#/Java. What's specifically not possible is a closure observing a
later *reassignment* of the outer `var` it closed over, because that would require the closure to
hold the slot, not a snapshot of what was in it.

To keep that from being a silent surprise, **a `var` a closure has captured must be effectively
final from the closure's point of view**: reassigning it anywhere after the closure literal that
captured it is a compile error, the same restriction Java places on captured locals, for the same
reason. This is the syntax layer being honest about a constraint the object model already imposes,
not a new restriction invented for its own sake.

---

## 9. Exceptions

```
class OutOfRangeException : Exception {
    constructor(message: string) : super(message) { }
}

try {
    doSomething();
} catch (e: OutOfRangeException) {
    log(e.message);
} catch (e: Exception) {
    log("unexpected: " + e.message);
} finally {
    cleanup();
}

throw OutOfRangeException("index 5 out of range");
```

Every thrown value's type must extend the built-in `Exception` class — `throw` only type-checks
against an `Exception`-typed expression, so a `catch (e: T)` is always matching against a real
hierarchy rather than an arbitrary object. Multiple `catch` clauses stack and are tried top to
bottom, first assignable match wins (a walk up `Ancestors`, same mechanism as any other subtype
test) — same as C#/Java, no union-typed catch.

`try`/`catch`/`finally` here is purely the *source* form; how it lowers is already decided by
`CLAUDE.md` and isn't repeated as a new decision: a protected region becomes an entry in
`SurtrBytecodeMethodInfo.Handlers` (so entering `try` costs nothing), and `finally` is emitted by
the compiler on every exit path plus a catch-all that runs it and re-raises — there's no
`Leave`/`EndFinally` opcode because the source-to-bytecode lowering does that work instead.

---

## 10. Native/host interop surface

```
native fun log(message: string): void;
native let ScreenWidth: int;      // host-owned, read-only from Surtr
native var TimeScale: float;      // host-owned, writable from Surtr

fun report(): void {
    log("width is $ScreenWidth");
    TimeScale = 0.5;
}
```

A `native` declaration is a signature with no body, in the same "just the shape, no
implementation" spirit as an interface member (§2.3) — it cannot legally be given one; the body
lives on the host side, wired through `SurtrNativeFunction`/`FromFunctionPointer`. What it gives
the compiler is a name and a type to check call sites against, and a slot in the module's native
import table — distinct from the module's regular call table, matching `CLAUDE.md`'s note that
`CallGlobalNative` is the one opcode split out by *which table* the target lives in, not by
`ImplKind`. A module that declares a `native` the host never registers under that exact name fails
to load, the same way an unresolved `SurtrTypeHandle` does.

`native` declarations live at module scope only — host globals are genuinely global per
`CLAUDE.md` ("the single exception is host-defined native variables and functions"), not
per-class, so there's no `native` member inside a `class`/`interface` body.

**`native let` is read-only, `native var` is writable** from Surtr, mirroring the same distinction
everywhere else in the language. The host chooses which it registers, so a value it needs to keep
authority over (a frame counter, a screen dimension) is exposed as `let` and can only change on the
host's own terms, while genuinely shared state (`TimeScale`) can be exposed as `var`. There is no
third form — a host global that Surtr should never see simply isn't registered.

---

## 11. Attributes and annotations

```
@Obsolete("use moveTo instead")
public fun move(dx: float, dy: float): void { ... }

@Range(0, 100)
public health: int { get; set; }

class Component {
    @SerializeField
    private var _speed: float = 5.0;
}
```

Java-style, `@Name(args)` directly above the declaration it applies to — not C#'s bracketed
`[Name(args)]`. Reads as metadata *attached to* the next line rather than a bracketed clause that
could be mistaken for an array-typed something, and keeps the one `[` / `]` pair in the language
meaning exactly one thing (array indexing/type, §5.3/§5.4). An attribute can decorate any
declaration — class, interface, enum, field, property, method, parameter — the same set `///` doc
comments attach to. Concretely, this is aimed at two audiences: compiler/tooling directives
(`@Obsolete`, `@Deprecated`-style warnings) and future Unity interop, where a host embedding Surtr
will want to reflect on attributes to do things like expose a field to the inspector. Exactly which
attributes exist and how the host reads them back is a separate, later design question — this
section only fixes the source-level syntax for attaching one.

---

## 12. Comments and documentation

`//` and `/* ... */` follow the same convention `CLAUDE.md` already mandates project-wide for the
C# implementation: `//` line comments, `/* ... */` block comments (non-nesting, same as C/C#/Java —
a nested `/*` inside a block comment doesn't start a new one, the first `*/` closes it).

**Doc comments are `///`, JSDoc-style rather than XML** — a deliberate departure from the C# side's
convention, because Surtr source isn't C# and doesn't need to feed a `GenerateDocumentationFile`
pipeline the way the host library does; a lighter tag syntax is less to type for the same
information:

```
/// Moves the entity by the given offset.
/// @param dx horizontal offset, in world units
/// @param dy vertical offset, in world units
/// @returns the entity's new position
public fun move(dx: float, dy: float): Vec2 { ... }
```

A `///` block attaches to the declaration immediately following it — class, interface, enum,
field, property, method, or enum case. The first line(s) with no `@tag` are the summary; `@param`,
`@returns`, `@throws` (naming an `Exception` type it can raise) are the tags expected to come up
most, though the exact full tag set is left open until a doc-generation tool actually consumes
them.

---

## 13. The standard library

Several sections above lean on types that don't exist yet: §9 requires an `Exception` to extend,
§6 constrains on `IComparable<T>`, §4.2 iterates through `IIterable<T>`. They belong to a standard
library, and it lives in the **`surtr` module** — the same module name `CLAUDE.md` already gives
the built-in classes, extended rather than joined by a second one, so `string` and `Exception` are
siblings rather than residents of different worlds.

Everything here is imported implicitly. `surtr` is in scope in every file without an `import`
line, which is what lets §5.1's `string` and §9's `Exception` be written unqualified everywhere in
this document.

### 13.1 What goes in C# and what goes in Surtr

The library is written in both, and the dividing line is not taste:

- **C# (native, via `SurtrNativeFunction`)** for anything that touches VM internals, allocates, or
  sits on a hot path — the primitive and collection members, `Math`, string manipulation. These are
  the members `SurtrBuiltInTypeBuilder` already links by function pointer, and `CLAUDE.md`'s rule
  that built-in members are always `Direct` dispatch applies to all of them.
- **Surtr source** for everything expressible in the language itself — the exception hierarchy
  below the root, helper types, anything whose body is ordinary logic over other library calls.
  Writing these in Surtr is also the first real test of the compiler, which is a second reason to
  prefer it wherever performance doesn't forbid it.

The rule of thumb: if it needs `unsafe`, a raw pointer or a VM service, it's native; otherwise it's
Surtr.

### 13.2 Core interfaces

| Interface | Members | Used by |
|---|---|---|
| `IIterable<T>` | `iterate(): IIterator<T>` | `for-in` (§4.2) |
| `IIterator<T>` | `moveNext(): bool`, `current: T { get; }` | the above |
| `IComparable<T>` | `compareTo(other: T): int` | ordering, `operator<=>` (§5.6) |
| `IEquatable<T>` | `equals(other: T): bool` | value equality alongside `operator==` |

`IIterator<T>` is deliberately the classic two-member cursor rather than a coroutine or a
generator: it is a shape the compiler can pattern-match and lower into a plain indexed loop for the
cases §4.2 lists, which a generator-based protocol would not be.

### 13.3 The exception hierarchy

`Exception` is the root of everything throwable (§9). It carries at minimum a `Message: string`
and the standard subclasses that a VM trap maps onto, so a Surtr `catch` can name what the runtime
raises rather than only what Surtr code threw:

```
Exception
├── ArgumentException
├── IndexOutOfRangeException      ← array/string index traps
├── KeyNotFoundException          ← dict lookup
├── NullReferenceException        ← null receiver
├── DivideByZeroException
├── InvalidCastException          ← a failed `as` (§5.7)
└── StackOverflowException        ← the interpreter's per-call stack check
```

The mapping from VM trap to exception class is the part that has to be settled alongside
`docs/VM-Plan.md` §1.9's validation policy, not independently of it — that is what says which
conditions trap at all.

### 13.4 Naming convention for collection members

Built-in collections expose their size as **`length`**, uniformly — `array`, `tuple`, `string` and
`dict` alike. One name, no rule about which container uses which word. This is the piece
`CLAUDE.md` flags as a known gap: `array`/`dict`/`tuple`/`closure` cannot yet *declare* their
element-polymorphic members (`push`, `get`, `keys`, …) because a descriptor names one concrete type
and there is no way to write "the element type of whatever this array is". Closing that gap needs a
descriptor form for a built-in's own type parameter, and until it exists this section can name the
convention but not the full member list — see §14.

---

## 14. Open questions

Nothing here is deleted — an entry is only ever resolved and moved up into the section it belongs
to. Grouped by what each one blocks.

The surface syntax itself is fully specified. What remains is implementation work, tracked
elsewhere, and vocabulary that waits on a consumer.

### 14.1 Runtime work this syntax commits to

**Tracked in `docs/VM-Plan.md` §4**, which is the authoritative list — it sits next to the trap
table, the value representation and the linker decisions each item has to be reconciled with, and
`docs/VM-Plan.md` §5 orders them into the build plan.

> **All of it is now implemented.** The list below is what the syntax asked the runtime for and got;
> read it as a map of where each feature's support lives rather than as work outstanding. What is
> *not* done is the compiler side — §14.2 is untouched, and so is every compiler obligation in
> `docs/VM-Plan.md` §4.8.

In summary, the syntax obliged the runtime to grow:

- **a standard library** (§13 — `Exception` above all, since §9 currently has no legal throwable),
  and with it a mapping from each VM trap to the class it surfaces as, since a trap presents as a
  wrapped host object today and no `catch` naming a Surtr type would ever match one;
- **metadata for what a declaration says and nothing currently stores**: parameter defaults and
  varargs (§3.5), `sealed` (§2.2, §3.3), enum-ness and a per-case ordinal (§2.4, §4.3), and
  attributes (§11);
- **natively-tagged nullable primitives** (§5.1);
- **a `range` type** (§5.4) and the `for-in` lowering that keeps it from allocating (§4.2);
- **a per-module native import table**, so a `native` declaration binds by name at load and a
  missing one fails there rather than at the instruction that reaches it (§10);
- **a boxing path that can name a class**, for `value class` (§2.9);
- **a descriptor form for a built-in's own type parameter** (§13.4);
- **an instruction budget on a run**, so a `const fun` cannot hang the compiler (§7.2).

**Signature-keyed member tables are no longer on this list.** §3.5's overloading turned out to be
most of the way built already: the runtime's method tables are overload groups, and the linker
matches `override` on name plus parameter types. `docs/VM-Plan.md` §4.1 records the two small
pieces that remain.

### 14.2 Compiler architecture this syntax commits to

Unlike §14.1 these need no VM change, but they are not small, and two of them constrain the
compiler's overall shape rather than living inside one pass.

- **The const-evaluation pass** (§7.2). Const folding runs the emitted bytecode on the real VM, so
  the compiler needs a pipeline stage that builds and evaluates the const-evaluable subset of a
  module *before* the rest of it is emitted — because `const if` (§7.3) decides what gets emitted
  at all. `CLAUDE.md`'s declare → emit → `Build()` → `LoadModule` order describes one pass; this
  needs that order run twice, over different subsets. It also needs the instruction budget and a
  clean `ResetExecution` between evaluations.
- **Discarding an untaken `const if` branch before binding** (§7.3), including at declaration
  level, where the branch's members must never reach a member table or a field layout.
- **Bytecode inlining** (§3.6) — the splice itself, remapping a callee's `SurtrExceptionHandler`
  ranges into the caller's chunk-absolute table, and diagnosing an impossible `forceinline` with
  the reason.
- **The build model.** §7.4 has the build define constants, and §3.6 needs a callee's bytecode
  available to inline across a module boundary. Neither has anywhere to come from yet: there is no
  project format, no notion of a compilation unit larger than a file, and §2.1 already deferred the
  source-root configuration for the same reason. All three want answering together.

### 14.3 Vocabulary, waiting on a consumer

- **The full `///` tag set** (§12) — `@param`/`@returns`/`@throws` are named as expected; the
  complete list waits on a doc tool that actually consumes them.
- **Which attributes exist** and how a host reads them back (§11) — the syntax for attaching one is
  fixed, the vocabulary isn't.

### 14.4 Deferred language features

Each of these was considered and deliberately left out, and each is **additive** — adding it later
does not invalidate anything written against this document.

- **Pattern matching** (§4.3) — type patterns in `switch`, destructuring, and the type narrowing
  that would let `if (x is Dog)` (§5.7) make `x` a `Dog` inside the branch.
- **Declaration-site generic variance**, `out T` / `in T` (§6).
- **Per-case enum bodies**, Java's anonymous-constant pattern (§2.4).
- **Deeply immutable collections** (§5.4) — `let` is shallow, and a frozen collection would need
  either a second array type or a runtime mutability flag.
- **A `where`-style trailing constraint clause** (§6), if the inline form proves insufficient.
- **Multidimensional indexing** (§5.6) — `a[i, j]`. Nothing in the type system expresses it today:
  an array is `A<elem>` and a dictionary `D<key><value>`, both single-key, so it would need a new
  composite type before an operator for it could mean anything.
