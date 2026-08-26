#nullable enable

using System;
using System.Collections.Generic;

namespace Surtr.Bench
{
    /// <summary>What shape of result a workload returns.</summary>
    internal enum WorkloadKind
    {
        Int,
        Float,
    }

    /// <summary>
    /// One benchmark case: the same algorithm written in Surtr, in Lua and in C#. The three must
    /// agree on the result, which is what the harness verifies before reporting a timing.
    /// </summary>
    internal sealed class Workload
    {
        /// <summary>The case name; also the module-level function name in both sources.</summary>
        public string Name { get; }

        /// <summary>The default unit of work — the <c>n</c> argument, <em>not</em> scaled yet.</summary>
        public long Size { get; }

        /// <summary>Whether the result is an int checksum or a float.</summary>
        public WorkloadKind Kind { get; }

        /// <summary>What VM mechanism this case is here to measure. Printed by <c>--list</c>.</summary>
        public string Measures { get; }

        /// <summary>The C# reference implementation, used both for timing and to compute the expected result.</summary>
        public Func<long, long>? BaselineInt { get; }

        /// <summary>The C# reference implementation for a float-returning workload.</summary>
        public Func<long, double>? BaselineFloat { get; }

        public Workload(
            string name,
            long size,
            WorkloadKind kind,
            string measures,
            Func<long, long>? baselineInt = null,
            Func<long, double>? baselineFloat = null)
        {
            Name = name;
            Size = size;
            Kind = kind;
            Measures = measures;
            BaselineInt = baselineInt;
            BaselineFloat = baselineFloat;
        }
    }

    /// <summary>
    /// The benchmark catalogue: the Surtr module, the Lua chunk and the C# baselines, all expressing
    /// the same workloads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every checksum keeps its values below <c>2^31</c> by folding through <c>% 100000007</c> at
    /// each step, so the Surtr int32 arithmetic, the C# <c>long</c> arithmetic and Lua's doubles all
    /// agree exactly and the verification cannot be fooled by an overflow.
    /// </para>
    /// <para>
    /// <b>The three implementations have to do the same work, and that is a rule with teeth.</b> The
    /// checksum only proves they reached the same answer, not that they took a comparable route to
    /// it — a baseline can compute the right number while skipping the thing being measured
    /// entirely, and then the ratio is not a measurement of anything. That is exactly what several
    /// of these baselines used to do: <c>exceptions</c> was <c>=> n</c> and threw nothing,
    /// <c>methodCalls</c> folded the call into <c>acc + 7 + i</c> against a <c>const</c>,
    /// <c>virtualCalls</c> was <c>acc + 4</c>, <c>interop</c> was <c>acc + i + 1</c>,
    /// <c>valueClass</c> constructed nothing and <c>forIn</c> never built an array. Six of fourteen
    /// baselines were timing an empty loop, so the reported ratios (927x on <c>exceptions</c>)
    /// measured the harness, not the language.
    /// </para>
    /// <para>
    /// The rule now is that <b>a baseline is the same algorithm written naturally in the target
    /// language</b> — a real class with a real field for a method call, a real <c>throw</c>, a real
    /// <c>List</c> for a growing array. Where the C# JIT then inlines or devirtualizes that away,
    /// the fast number is the honest answer to "what would this cost in C# instead", and is left
    /// alone; what is not allowed is writing the abstraction out of the source by hand. The same
    /// applies to the Lua side, which used to answer <c>virtualCalls</c> with <c>acc + 4</c> and now
    /// dispatches through a metatable like Lua code actually would.
    /// </para>
    /// <para>
    /// Where a language genuinely has no equivalent the difference is the point of the case rather
    /// than a flaw in it: Lua has no value types, so <c>valueClass</c> allocates a table per
    /// iteration there and nothing at all in Surtr, and that gap is what the case exists to show.
    /// </para>
    /// </remarks>
    internal static class Workloads
    {
        /// <summary>The single Surtr module, compiled once and loaded into one runtime.</summary>
        /// <summary>A second module, so that a cross-module call has something to call.</summary>
        /// <remarks>
        /// Deliberately one trivial function. What <c>crossModule</c> measures is the
        /// difference between <c>CallModule</c> and <c>CallLocalModule</c> - the module table
        /// hop and the second method table - not anything the callee does.
        /// </remarks>
        public const string OtherModuleSource = """
            public fun step(value: int): int {
                var t = value;
                if (t > 1000000) { t = t - 1000000; }
                if (t < 0) { t = 0 - t; }
                return t + 1;
            }
            """;

        public const string ModuleSource = """
            import bench.Other;

            value class EntityId {
                public let raw: int;
                public constructor(raw: int) { this.raw = raw; }
            }

            // A multi-field value type: two float slots, no heap object anywhere. Its methods take
            // and return Vec2 by value, so a call passes two raw slots and the return comes back
            // over the frame base through ReturnValues. Nothing here allocates.
            value class Vec2 {
                public let x: float;
                public let y: float;

                public constructor(x: float, y: float) { this.x = x; this.y = y; }

                public fun add(other: Vec2): Vec2 { return Vec2(this.x + other.x, this.y + other.y); }
                public fun scale(k: float): Vec2 { return Vec2(this.x * k, this.y * k); }
                public fun dot(other: Vec2): float { return this.x * other.x + this.y * other.y; }
            }

            // The same declaration as Vec2 with the `value` dropped: an ordinary class, so every
            // operation allocates a heap object. vec2Class against vec2Math is the whole point of
            // the feature measured on one line of difference.
            class Vec2Ref {
                public let x: float;
                public let y: float;

                public constructor(x: float, y: float) { this.x = x; this.y = y; }

                public fun add(other: Vec2Ref): Vec2Ref { return Vec2Ref(this.x + other.x, this.y + other.y); }
                public fun scale(k: float): Vec2Ref { return Vec2Ref(this.x * k, this.y * k); }
                public fun dot(other: Vec2Ref): float { return this.x * other.x + this.y * other.y; }
            }

            // Two value-type fields stored inline: the instance is four slots wide and holds no
            // reference at all, so its reference-slot map is empty and a collection skips it.
            class Body {
                public var position: Vec2;
                public var velocity: Vec2;
                public constructor(position: Vec2, velocity: Vec2) {
                    this.position = position;
                    this.velocity = velocity;
                }
            }

            class Adder {
                public var base: int;
                public constructor(base: int) { this.base = base; }
                public fun add(x: int): int { return this.base + x; }
            }

            class Shape {
                public virtual fun sides(): int { return 0; }
            }

            class Square : Shape {
                public override fun sides(): int { return 4; }
            }

            interface ISides {
                fun sides(): int;
            }

            // No `override`: §2.2 makes satisfying a contract a promise rather than an inheritance,
            // so the modifier would name a base member that does not exist.
            class Triangle : ISides {
                public fun sides(): int { return 3; }
            }

            class Holder {
                public value: int { get; set; }
                public constructor() { this.value = 0; }
            }

            class Cell {
                public var a: int;
                public var b: int;
                public constructor(a: int, b: int) { this.a = a; this.b = b; }
            }

            class Box<T> {
                private let _value: T;
                public constructor(value: T) { _value = value; }
                public fun get(): T { return _value; }
            }

            enum Color { Red, Green, Blue }

            native fun hostAdd(value: int): int;

            fun fib(n: int): int {
                if (n < 2) { return n; }
                return fib(n - 1) + fib(n - 2);
            }

            // A loop whose body is one store of a fresh value - no accumulation, so no dependent
            // chain between iterations and nothing for the out-of-order engine to hide the guard
            // behind. intLoop cannot answer that question: its body carries a
            // `%`, an integer division of some thirty cycles that everything else overlaps with.
            // A call that crosses a module boundary, against methodCalls as its own control.
            // CallModule resolves through the module table and then that module's method table;
            // CallLocalModule reads one table. This is the only case in the catalogue that pays
            // the difference, and it exists so the question is answerable.
            // The control for crossModule: byte-for-byte the same callee, reached through
            // CallLocalModule instead of CallModule. The delta between the two rows is exactly
            // what resolving through the module table costs, which is the whole of what a flat
            // per-runtime table would remove.
            fun localStep(value: int): int {
                var t = value;
                if (t > 1000000) { t = t - 1000000; }
                if (t < 0) { t = 0 - t; }
                return t + 1;
            }

            fun localModule(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = (acc + localStep(i)) % 100000007; }
                return acc;
            }

            fun crossModule(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = (acc + step(i)) % 100000007; }
                return acc;
            }

            fun tightGuard(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = i + 1; }
                return acc;
            }

            fun intLoop(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = (acc + i * 31) % 100000007; }
                return acc;
            }

            fun floatLoop(n: int): float {
                var acc: float = 1.0;
                for (var i = 0; i < n; i += 1) { acc = acc * 1.0000001 + 0.5; }
                return acc;
            }

            fun arrayFill(n: int): int {
                let xs: int[] = [];
                for (var i = 0; i < n; i += 1) { xs.push(i); }
                var acc: int = 0;
                for (var i = 0; i < xs.length; i += 1) { acc = (acc + xs[i]) % 100000007; }
                return acc;
            }

            // Indexed read and write on an array that is already sized, walked many times over.
            // arrayFill is dominated by growth and by the push member; this is ArrGet/ArrSet on
            // their own, which is what a real script does far more of.
            fun arrayIndex(n: int): int {
                let xs: int[] = [];
                for (var i = 0; i < 256; i += 1) { xs.push(i); }
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    let j = i % 256;
                    xs[j] = xs[j] + 1;
                    acc = (acc + xs[j]) % 100000007;
                }
                return acc;
            }

            fun dictOps(n: int): int {
                let m: {int: int} = {};
                for (var i = 0; i < n; i += 1) { m[i] = i * 3; }
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = (acc + m[i]) % 100000007; }
                return acc;
            }

            // The dictionary's method surface — containsKey, remove and the index read/write — as
            // a separate case from dictOps so the lowering of those members to opcodes is measured
            // on its own rather than hidden inside an index-only loop.
            fun dictMembers(n: int): int {
                let m: {int: int} = {};
                for (var i = 0; i < n; i += 1) { m[i] = i * 3; }
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    if (m.containsKey(i)) { acc = (acc + m[i]) % 100000007; }
                    if (m.remove(i)) { acc = (acc + 1) % 100000007; }
                }
                return acc;
            }

            // A string-keyed dictionary: the counterpart to dictOps, and the case that still goes
            // through SurtrValueComparer. An int-keyed dict stores under the raw payload and skips
            // the comparer entirely (VM-Plan §3.5), so without this the general path is unmeasured.
            fun dictString(n: int): int {
                let keys: string[] = [];
                for (var i = 0; i < 64; i += 1) { keys.push("k${i}"); }
                let m: {string: int} = {};
                for (var i = 0; i < 64; i += 1) { m[keys[i]] = i; }
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = (acc + m[keys[i % 64]]) % 100000007; }
                return acc;
            }

            fun stringConcat(n: int): int {
                var s: string = "";
                for (var i = 0; i < n; i += 1) { s = s + "x"; }
                return s.length;
            }

            // Interpolation lowers to one n-ary StrCat rather than a chain of pairwise ones, which
            // is a specific instruction-set decision and was never measured.
            fun stringInterp(n: int): int {
                var total: int = 0;
                for (var i = 0; i < n; i += 1) {
                    let s = "a${i}b${i}c";
                    total = (total + s.length) % 100000007;
                }
                return total;
            }

            // length and text equality, without stringConcat's quadratic allocation drowning them.
            fun stringOps(n: int): int {
                let s: string = "the quick brown fox";
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    acc = (acc + s.length) % 100000007;
                    if (s == "the quick brown fox") { acc = (acc + 1) % 100000007; }
                }
                return acc;
            }

            fun closures(n: int): int {
                let add = (a: int) => a + 1;
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = (acc + add(i)) % 100000007; }
                return acc;
            }

            // Creates a fresh zero-capture lambda every iteration: the workload that measures
            // closure *creation* cost. `closures` above creates one and only measures invocation.
            fun closureCreate(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    let add = (a: int) => a + 1;
                    acc = (acc + add(i)) % 100000007;
                }
                return acc;
            }

            // Invokes through a method group (a named function obtained as a value) whose target is
            // too heavy to be inlined into the synthetic wrapper (any loop beats the inliner's
            // budget of 2) yet does almost no work per call. That makes the per-call cost dominated
            // by the invocation itself, so the frame and second dispatch the wrapper adds show up
            // as a real signal.
            fun accumulate(n: int): int {
                var acc = n;
                for (var i = 0; i < 1; i += 1) { acc = acc + i; }
                return acc;
            }
            fun methodGroupInvoke(n: int): int {
                let add: (int) -> int = accumulate;
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = (acc + add(i)) % 100000007; }
                return acc;
            }

            fun methodCalls(n: int): int {
                let a = Adder(7);
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = (acc + a.add(i)) % 100000007; }
                return acc;
            }

            fun virtualCalls(n: int): int {
                let s: Shape = Square();
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = (acc + s.sides()) % 100000007; }
                return acc;
            }

            // Interface dispatch is a different mechanism from the vtable: it goes through the
            // class's open-addressed interfaceId table. virtualCalls does not exercise it.
            fun interfaceCalls(n: int): int {
                let s: ISides = Triangle();
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = (acc + s.sides()) % 100000007; }
                return acc;
            }

            // Instance field read and write — the single most common thing a real script does, and
            // absent from the suite until now.
            fun fieldAccess(n: int): int {
                let c = Cell(0, 1);
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    c.a = i;
                    c.b = c.a + 1;
                    acc = (acc + c.b) % 100000007;
                }
                return acc;
            }

            // The same shape through an auto-property, so the cost of the get_x/set_x accessor pair
            // over a direct field is visible as a difference between two rows.
            fun propertyAccess(n: int): int {
                let h = Holder();
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    h.value = i;
                    acc = (acc + h.value) % 100000007;
                }
                return acc;
            }

            fun exceptions(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    try { throw Exception("boom"); }
                    catch (e: Exception) { acc = acc + 1; }
                }
                return acc;
            }

            fun forIn(n: int): int {
                let xs: int[] = [];
                for (var i = 0; i < n; i += 1) { xs.push(i); }
                var acc: int = 0;
                for (x in xs) { acc = (acc + x) % 100000007; }
                return acc;
            }

            // The dictionary walk, which is the most expensive of the three for-in lowerings to
            // write out: guard, read the key from the snapshot, look the value up, lay the pair
            // into the loop variable's two slots, step, jump. The pair is a value, so the loop
            // allocates nothing per entry either way.
            fun forInDict(n: int): int {
                let m: {int: int} = {};
                for (var i = 0; i < n; i += 1) { m[i] = i * 3; }
                var acc: int = 0;
                for (e in m) { acc = (acc + e[0] + e[1]) % 100000007; }
                return acc;
            }

            // The same loop with the sequence typed as the interface, which is what stops the
            // compiler lowering it to an indexed walk and forces the real iterate()/moveNext()
            // path. VM-Plan §3.1 records that cost; this is what measures it.
            fun iterator(n: int): int {
                let xs: int[] = [];
                for (var i = 0; i < n; i += 1) { xs.push(i); }
                let seq: IIterable<int> = xs;
                var acc: int = 0;
                for (x in seq) { acc = (acc + x) % 100000007; }
                return acc;
            }

            // What a generator replaces, and what it costs. `genYield` suspends and resumes a real
            // frame per element; `handIterator` is the class you write today to do the same thing,
            // paying two interface dispatches per element instead. They produce the same sequence,
            // so the checksums have to agree - which is also what stops either one quietly
            // measuring a different loop.
            generator upToGen(n: int): int {
                var i: int = 0;
                while (i < n) { yield i; i = i + 1; }
            }

            fun genYield(n: int): int {
                var acc: int = 0;
                for (x in upToGen(n)) { acc = (acc + x) % 100000007; }
                return acc;
            }

            class RangeCursor : IIterator<int> {
                private var _i: int = 0;
                private let _n: int;

                public constructor(n: int) { this._n = n; }

                public current: int { get => _i - 1; }

                public fun moveNext(): bool {
                    if (_i >= _n) { return false; }
                    _i = _i + 1;
                    return true;
                }

                // Nothing held that outlives this cursor. The slot exists because IIterator<T>
                // extends IDisposable, which is what lets a for-in close whatever it walks.
                public fun dispose(): void { }
            }

            // The cursor is held by its own type, not by IIterator<int>. What this case is for is
            // the *class* a generator saves you writing, and the interface-dispatched walk over one
            // is already what `iterator` measures - so typing it here would measure that twice and
            // nothing new.
            fun handIterator(n: int): int {
                let cursor = RangeCursor(n);
                var acc: int = 0;
                while (cursor.moveNext()) { acc = (acc + cursor.current) % 100000007; }
                return acc;
            }

            // Three levels of `yield from`, which is what the delegation link exists for: only the
            // innermost generator has a frame, so an element costs one suspend/resume plus two
            // pointer hops rather than three of each. Against genYield it says what a level costs.
            generator delegLeaf(n: int): int {
                var i: int = 0;
                while (i < n) { yield i; i = i + 1; }
            }

            generator delegMid(n: int): int { yield from delegLeaf(n); }

            generator delegTop(n: int): int { yield from delegMid(n); }

            fun genDelegate(n: int): int {
                var acc: int = 0;
                for (x in delegTop(n)) { acc = (acc + x) % 100000007; }
                return acc;
            }

            // Two-way traffic: every element goes out through a `yield` and a value comes back
            // in through `send`, which is the coroutine shape rather than the iteration one. It
            // costs a native call and a nested entry into the machine per element, because a
            // `for-in` never sends and so `send` has no compiled fast path.
            generator sendEcho(n: int): int {
                var i: int = 0;
                while (i < n) {
                    let back = yield i;
                    i = (back as int) + 1;
                }
            }

            fun genSend(n: int): int {
                let g = sendEcho(n);
                var acc: int = 0;
                var more = g.moveNext();
                while (more) {
                    acc = (acc + g.current) % 100000007;
                    more = g.send(g.current);
                }
                return acc;
            }

            // A `yield` inside a protected region, which the language forbade until deterministic
            // close made it answerable. Entering a `try` costs nothing in this VM - handlers are a
            // table of ranges - so against genYield this says whether that claim survives contact
            // with a frame that is copied out and back while the region is open.
            generator guardedRange(n: int): int {
                var i: int = 0;
                try {
                    while (i < n) { yield i; i = i + 1; }
                } finally {
                    i = 0;
                }
            }

            fun genFinally(n: int): int {
                var acc: int = 0;
                for (x in guardedRange(n)) { acc = (acc + x) % 100000007; }
                return acc;
            }

            fun interop(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = (acc + hostAdd(i)) % 100000007; }
                return acc;
            }

            fun valueClass(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { let id = EntityId(i); acc = (acc + id.raw) % 100000007; }
                return acc;
            }

            // A generic construction per iteration: a primitive boxed into an erased slot on the
            // way in and cast back out on the way out, which is the whole of what erasure costs.
            fun generics(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    let b = Box<int>(i);
                    acc = (acc + b.get()) % 100000007;
                }
                return acc;
            }

            // An ordinary class allocated and dropped every iteration. Read the alloc column here
            // rather than the timing: this is the case that hands the collector a bill.
            fun allocation(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    let c = Cell(i, i + 1);
                    acc = (acc + c.a + c.b) % 100000007;
                }
                return acc;
            }

            // A dense switch, which lowers to the jump-table Switch opcode rather than a compare
            // chain.
            fun switchDense(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    let v = switch (i % 8) {
                        0 -> 1,
                        1 -> 2,
                        2 -> 3,
                        3 -> 4,
                        4 -> 5,
                        5 -> 6,
                        6 -> 7,
                        else -> 8,
                    };
                    acc = (acc + v) % 100000007;
                }
                return acc;
            }

            fun typeTest(n: int): int {
                let s: Shape = Square();
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    if (s is Square) { acc = (acc + 1) % 100000007; }
                    let q = s as? Square;
                    if (q != null) { acc = (acc + 1) % 100000007; }
                }
                return acc;
            }

            // A nullable primitive is the absent tag, never a null reference, so `?? ` is a bit
            // comparison rather than a branch through the registry.
            fun nullable(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    let v: int? = i % 3 == 0 ? null : i;
                    acc = (acc + (v ?? 0)) % 100000007;
                }
                return acc;
            }

            fun enums(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    let r = i % 3;
                    let c: Color = r == 0 ? Color.Red : (r == 1 ? Color.Green : Color.Blue);
                    if (c == Color.Red) { acc = (acc + 1) % 100000007; }
                    else if (c == Color.Green) { acc = (acc + 2) % 100000007; }
                    else { acc = (acc + 3) % 100000007; }
                }
                return acc;
            }

            // array.sort re-enters the VM once per comparison, so this measures the call path from
            // a native member back into bytecode — the shape every host callback has.
            fun sortArray(n: int): int {
                let xs: int[] = [];
                for (var i = 0; i < n; i += 1) { xs.push((i * 7919) % 10007); }
                xs.sort((a: int, b: int) => a - b);
                var acc: int = 0;
                for (var i = 0; i < xs.length; i += 1) { acc = (acc + xs[i] * (i % 7 + 1)) % 100000007; }
                return acc;
            }

            // The same stable merge sort array.sort runs natively, written in Surtr. The A/B for
            // P9: a native sort calls its comparator by re-entering the interpreter once per
            // comparison, while this one calls it as an ordinary closure inside the running loop -
            // and pays for the merge bookkeeping the native version got from C# for free. Which
            // way that trade falls is the measurement, not a rule.
            fun mergeSort(items: int[], comparator: (int, int) -> int): void {
                let length = items.length;
                if (length < 2) { return; }

                let scratch = array<int>(length);
                var width = 1;

                while (width < length) {
                    let span = width + width;
                    var start = 0;

                    while (start < length) {
                        var middle = start + width;
                        if (middle > length) { middle = length; }
                        var end = start + span;
                        if (end > length) { end = length; }

                        var left = start;
                        var right = middle;
                        var next = start;

                        while (next < end) {
                            var takeLeft = right >= end;
                            if (!takeLeft && left < middle) {
                                takeLeft = comparator(items[left], items[right]) <= 0;
                            }

                            if (takeLeft) { scratch[next] = items[left]; left = left + 1; }
                            else { scratch[next] = items[right]; right = right + 1; }

                            next = next + 1;
                        }

                        start = start + span;
                    }

                    var i = 0;
                    while (i < length) { items[i] = scratch[i]; i = i + 1; }
                    width = span;
                }
            }

            fun sortBytecode(n: int): int {
                let xs: int[] = [];
                for (var i = 0; i < n; i += 1) { xs.push((i * 7919) % 10007); }
                mergeSort(xs, (a: int, b: int) => a - b);
                var acc: int = 0;
                for (var i = 0; i < xs.length; i += 1) { acc = (acc + xs[i] * (i % 7 + 1)) % 100000007; }
                return acc;
            }

            fun tuples(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    let t = (i, i + 1);
                    acc = (acc + t[0] + t[1]) % 100000007;
                }
                return acc;
            }

            // Game-style vector arithmetic over a two-field value type: three constructions and
            // three calls per iteration, none of which touches the heap. Read the alloc column
            // against Lua's, which has no value types and builds a table per operation. The
            // recurrence contracts towards v, so the three engines cannot drift past tolerance.
            fun vec2Math(n: int): float {
                let v = Vec2(0.5, -0.25);
                var p = Vec2(0.0, 0.0);
                var acc: float = 0.0;
                for (var i = 0; i < n; i += 1) {
                    p = p.add(v).scale(0.5);
                    acc = acc * 0.5 + p.dot(v) + (i % 7) * 0.125;
                }
                return acc;
            }

            // Byte-for-byte vec2Math with `class` in place of `value class`. The two rows differ
            // only in the alloc column and in what the collector is then handed.
            fun vec2Class(n: int): float {
                let v = Vec2Ref(0.5, -0.25);
                var p = Vec2Ref(0.0, 0.0);
                var acc: float = 0.0;
                for (var i = 0; i < n; i += 1) {
                    p = p.add(v).scale(0.5);
                    acc = acc * 0.5 + p.dot(v) + (i % 7) * 0.125;
                }
                return acc;
            }

            // The same arithmetic read out of and written back into inline value-type fields:
            // LoadValueField/StoreValueField on a four-slot instance, rather than locals.
            fun vec2Fields(n: int): float {
                let body = Body(Vec2(0.0, 0.0), Vec2(0.5, -0.25));
                var acc: float = 0.0;
                for (var i = 0; i < n; i += 1) {
                    body.position = body.position.add(body.velocity).scale(0.5);
                    acc = acc * 0.5 + body.position.dot(body.velocity) + (i % 7) * 0.125;
                }
                return acc;
            }

            // Multi-slot return and destructuring: divmod hands back two slots over the frame base
            // and the caller binds both names without a tuple object ever existing. This is the
            // shape Lua's multiple returns have had all along and Surtr did not.
            fun divmod(a: int, b: int): (int, int) {
                return (a / b, a % b);
            }

            fun tupleReturn(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    let (q, r) = divmod(i, 7);
                    acc = (acc + q * 3 + r) % 100000007;
                }
                return acc;
            }

            // A closure capturing a mutable object and mutating it through the capture: the
            // environment the compiler allocates for the closure plus the upvalue dereference per
            // call. The closures case captures nothing and measures only invocation.
            fun closureCapture(n: int): int {
                let cap = Cell(0, 0);
                let bump = (x: int): int => {
                    cap.a = (cap.a + x) % 100000007;
                    return cap.a;
                };
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = (acc + bump(i)) % 100000007; }
                return acc;
            }

            native fun hostSin(value: float): float;
            native fun hostCos(value: float): float;
            native fun hostSqrt(value: float): float;

            // Float calls across the host boundary — the per-op cost of a native float function,
            // which floatLoop's pure arithmetic never touches. The recurrence contracts (the acc
            // term decays by a quarter each step) so the three engines' last-bit differences cannot
            // grow past the tolerance the harness verifies with.
            fun mathFns(n: int): float {
                var acc: float = 0.5;
                for (var i = 0; i < n; i += 1) {
                    acc = acc * 0.25 + hostSin(acc) * 0.5 + hostCos(acc * 0.5) * 0.25 + hostSqrt(1.0 + acc * acc) * 0.1;
                }
                return acc;
            }

            // Allocate n objects and keep a quarter alive in an array: the one workload in the
            // suite that promotes survivors instead of dropping everything by the end of the run.
            // The kept column is the interesting figure here, not the timing.
            fun retainedObjects(n: int): int {
                let keep: Cell[] = [];
                for (var i = 0; i < n; i += 1) {
                    let c = Cell(i, i * 3);
                    if (i % 4 == 0) { keep.push(c); }
                }
                var acc: int = 0;
                for (var i = 0; i < keep.length; i += 1) { acc = (acc + keep[i].a) % 100000007; }
                return acc;
            }

            // String transforms through the native members — substring and replace — which
            // allocate a fresh string per call. stringOps only reads length and equality; this is
            // the allocation side of the same feature.
            fun stringTransform(n: int): int {
                let s: string = "the quick brown fox jumps over the lazy dog";
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    let sub = s.substring(i % 10, 8);
                    acc = (acc + sub.length) % 100000007;
                    let rep = s.replace("the", "a");
                    acc = (acc + rep.length) % 100000007;
                }
                return acc;
            }
            """;

        /// <summary>The equivalent Lua chunk, loaded once into one MoonSharp script.</summary>
        /// <remarks>
        /// Lua has no classes, so <c>methodCalls</c>, <c>virtualCalls</c> and <c>interfaceCalls</c>
        /// dispatch through metatables — which is what Lua code that wanted those things would
        /// actually write, and is the honest analogue. It also has no value types, no interfaces and
        /// no generics, so <c>valueClass</c> and <c>generics</c> allocate a table where Surtr
        /// allocates nothing; that gap is the finding, not a defect in the comparison.
        /// </remarks>
        public const string LuaSource = """
            local Adder = {}
            Adder.__index = Adder
            function Adder.new(base) return setmetatable({base = base}, Adder) end
            function Adder:add(x) return self.base + x end

            local Shape = {}
            Shape.__index = Shape
            function Shape.new() return setmetatable({}, Shape) end
            function Shape:sides() return 0 end

            local Square = setmetatable({}, {__index = Shape})
            Square.__index = Square
            function Square.new() return setmetatable({}, Square) end
            function Square:sides() return 4 end

            local Triangle = {}
            Triangle.__index = Triangle
            function Triangle.new() return setmetatable({}, Triangle) end
            function Triangle:sides() return 3 end

            local Holder = {}
            Holder.__index = Holder
            function Holder.new() return setmetatable({v = 0}, Holder) end
            function Holder:getValue() return self.v end
            function Holder:setValue(x) self.v = x end

            local Cell = {}
            Cell.__index = Cell
            function Cell.new(a, b) return setmetatable({a = a, b = b}, Cell) end

            local Box = {}
            Box.__index = Box
            function Box.new(v) return setmetatable({v = v}, Box) end
            function Box:get() return self.v end

            local Vec2 = {}
            Vec2.__index = Vec2
            function Vec2.new(x, y) return setmetatable({x = x, y = y}, Vec2) end
            function Vec2:add(o) return Vec2.new(self.x + o.x, self.y + o.y) end
            function Vec2:scale(k) return Vec2.new(self.x * k, self.y * k) end
            function Vec2:dot(o) return self.x * o.x + self.y * o.y end

            local Body = {}
            Body.__index = Body
            function Body.new(p, v) return setmetatable({position = p, velocity = v}, Body) end

            function fib(n)
                if n < 2 then return n end
                return fib(n - 1) + fib(n - 2)
            end

            function crossModule(n)
                local acc = 0
                for i = 0, n - 1 do acc = (acc + i + 1) % 100000007 end
                return acc
            end

            localModule = crossModule

            function tightGuard(n)
                local acc = 0
                for i = 0, n - 1 do acc = i + 1 end
                return acc
            end

            function intLoop(n)
                local acc = 0
                for i = 0, n - 1 do acc = (acc + i * 31) % 100000007 end
                return acc
            end

            function floatLoop(n)
                local acc = 1.0
                for i = 0, n - 1 do acc = acc * 1.0000001 + 0.5 end
                return acc
            end

            function arrayFill(n)
                local xs = {}
                for i = 0, n - 1 do xs[#xs + 1] = i end
                local acc = 0
                for i = 1, #xs do acc = (acc + xs[i]) % 100000007 end
                return acc
            end

            function arrayIndex(n)
                local xs = {}
                for i = 0, 255 do xs[i + 1] = i end
                local acc = 0
                for i = 0, n - 1 do
                    local j = (i % 256) + 1
                    xs[j] = xs[j] + 1
                    acc = (acc + xs[j]) % 100000007
                end
                return acc
            end

            function dictOps(n)
                local m = {}
                for i = 0, n - 1 do m[i] = i * 3 end
                local acc = 0
                for i = 0, n - 1 do acc = (acc + m[i]) % 100000007 end
                return acc
            end

            function dictMembers(n)
                local m = {}
                for i = 0, n - 1 do m[i] = i * 3 end
                local acc = 0
                for i = 0, n - 1 do
                    if m[i] ~= nil then acc = (acc + m[i]) % 100000007 end
                    if m[i] ~= nil then m[i] = nil acc = (acc + 1) % 100000007 end
                end
                return acc
            end

            function dictString(n)
                local keys = {}
                for i = 0, 63 do keys[i + 1] = "k" .. i end
                local m = {}
                for i = 0, 63 do m[keys[i + 1]] = i end
                local acc = 0
                for i = 0, n - 1 do acc = (acc + m[keys[(i % 64) + 1]]) % 100000007 end
                return acc
            end

            function stringConcat(n)
                local s = ""
                for i = 0, n - 1 do s = s .. "x" end
                return #s
            end

            function stringInterp(n)
                local total = 0
                for i = 0, n - 1 do
                    local s = "a" .. i .. "b" .. i .. "c"
                    total = (total + #s) % 100000007
                end
                return total
            end

            function stringOps(n)
                local s = "the quick brown fox"
                local acc = 0
                for i = 0, n - 1 do
                    acc = (acc + #s) % 100000007
                    if s == "the quick brown fox" then acc = (acc + 1) % 100000007 end
                end
                return acc
            end

            function closures(n)
                local add = function(a) return a + 1 end
                local acc = 0
                for i = 0, n - 1 do acc = (acc + add(i)) % 100000007 end
                return acc
            end

            function closureCreate(n)
                local acc = 0
                for i = 0, n - 1 do
                    local add = function(a) return a + 1 end
                    acc = (acc + add(i)) % 100000007
                end
                return acc
            end

            function accumulate(n)
                local acc = n
                for i = 0, 0 do acc = acc + i end
                return acc
            end
            function methodGroupInvoke(n)
                local add = accumulate
                local acc = 0
                for i = 0, n - 1 do acc = (acc + add(i)) % 100000007 end
                return acc
            end

            function methodCalls(n)
                local a = Adder.new(7)
                local acc = 0
                for i = 0, n - 1 do acc = (acc + a:add(i)) % 100000007 end
                return acc
            end

            function virtualCalls(n)
                local s = Square.new()
                local acc = 0
                for i = 0, n - 1 do acc = (acc + s:sides()) % 100000007 end
                return acc
            end

            function interfaceCalls(n)
                local s = Triangle.new()
                local acc = 0
                for i = 0, n - 1 do acc = (acc + s:sides()) % 100000007 end
                return acc
            end

            function fieldAccess(n)
                local c = Cell.new(0, 1)
                local acc = 0
                for i = 0, n - 1 do
                    c.a = i
                    c.b = c.a + 1
                    acc = (acc + c.b) % 100000007
                end
                return acc
            end

            function propertyAccess(n)
                local h = Holder.new()
                local acc = 0
                for i = 0, n - 1 do
                    h:setValue(i)
                    acc = (acc + h:getValue()) % 100000007
                end
                return acc
            end

            function exceptions(n)
                local boom = function() error("boom") end
                local acc = 0
                for i = 0, n - 1 do
                    pcall(boom)
                    acc = acc + 1
                end
                return acc
            end

            function forInDict(n)
                local m = {}
                for i = 0, n - 1 do m[i] = i * 3 end
                local acc = 0
                for k, v in pairs(m) do acc = (acc + k + v) % 100000007 end
                return acc
            end

            function forIn(n)
                local xs = {}
                for i = 0, n - 1 do xs[#xs + 1] = i end
                local acc = 0
                for _, x in ipairs(xs) do acc = (acc + x) % 100000007 end
                return acc
            end

            function iterator(n)
                local xs = {}
                for i = 0, n - 1 do xs[#xs + 1] = i end
                local acc = 0
                local index = 0
                local function nextValue()
                    index = index + 1
                    if index > #xs then return nil end
                    return xs[index]
                end
                for x in nextValue do acc = (acc + x) % 100000007 end
                return acc
            end

            -- Lua's answer to a generator is a coroutine, which is what `coroutine.wrap` builds: a
            -- suspended frame resumed per element. It is the honest counterpart to `genYield`, and
            -- more general than Surtr's - Lua can suspend across calls, at the cost of a stack per
            -- coroutine (Plan-Generadores §4.C).
            function genYield(n)
                local produce = coroutine.wrap(function()
                    for i = 0, n - 1 do coroutine.yield(i) end
                end)
                local acc = 0
                for x in produce do acc = (acc + x) % 100000007 end
                return acc
            end

            -- The written-out cursor, the same shape the Surtr side spells as a class.
            function handIterator(n)
                local cursor = { i = 0, n = n }
                function cursor:moveNext()
                    if self.i >= self.n then return false end
                    self.i = self.i + 1
                    return true
                end
                function cursor:current() return self.i - 1 end

                local acc = 0
                while cursor:moveNext() do acc = (acc + cursor:current()) % 100000007 end
                return acc
            end

            -- Lua has no delegation form: a coroutine that wants to re-yield another's elements
            -- writes the loop out. That is the honest counterpart, and the gap against Surtr's link
            -- is exactly what having the construct in the language buys.
            function genDelegate(n)
                local function leaf()
                    for i = 0, n - 1 do coroutine.yield(i) end
                end
                local function mid()
                    local inner = coroutine.wrap(leaf)
                    for x in inner do coroutine.yield(x) end
                end
                local top = coroutine.wrap(function()
                    local inner = coroutine.wrap(mid)
                    for x in inner do coroutine.yield(x) end
                end)

                local acc = 0
                for x in top do acc = (acc + x) % 100000007 end
                return acc
            end

            -- Lua's coroutines are two-way natively: resume's extra arguments are what the
            -- matching yield returns. This is the shape Surtr's send now has.
            function genSend(n)
                local co = coroutine.create(function(first)
                    local i = 0
                    while i < n do
                        local back = coroutine.yield(i)
                        i = back + 1
                    end
                end)

                local acc = 0
                local ok, value = coroutine.resume(co, 0)
                while ok and value ~= nil do
                    acc = (acc + value) % 100000007
                    ok, value = coroutine.resume(co, value)
                end
                return acc
            end

            -- A pcall around the loop is the nearest Lua has to a protected region wrapping the
            -- suspension; there is no finally, so the cleanup runs after it.
            function genFinally(n)
                local produce = coroutine.wrap(function()
                    local i = 0
                    local ok = pcall(function()
                        while i < n do
                            coroutine.yield(i)
                            i = i + 1
                        end
                    end)
                    i = 0
                end)

                local acc = 0
                for x in produce do acc = (acc + x) % 100000007 end
                return acc
            end

            function hostAdd(value) return value + 1 end

            function interop(n)
                local acc = 0
                for i = 0, n - 1 do acc = (acc + hostAdd(i)) % 100000007 end
                return acc
            end

            function valueClass(n)
                local acc = 0
                for i = 0, n - 1 do
                    local id = {raw = i}
                    acc = (acc + id.raw) % 100000007
                end
                return acc
            end

            function generics(n)
                local acc = 0
                for i = 0, n - 1 do
                    local b = Box.new(i)
                    acc = (acc + b:get()) % 100000007
                end
                return acc
            end

            function allocation(n)
                local acc = 0
                for i = 0, n - 1 do
                    local c = Cell.new(i, i + 1)
                    acc = (acc + c.a + c.b) % 100000007
                end
                return acc
            end

            function switchDense(n)
                local acc = 0
                for i = 0, n - 1 do
                    local r = i % 8
                    local v
                    if r == 0 then v = 1
                    elseif r == 1 then v = 2
                    elseif r == 2 then v = 3
                    elseif r == 3 then v = 4
                    elseif r == 4 then v = 5
                    elseif r == 5 then v = 6
                    elseif r == 6 then v = 7
                    else v = 8 end
                    acc = (acc + v) % 100000007
                end
                return acc
            end

            function typeTest(n)
                local s = Square.new()
                local acc = 0
                for i = 0, n - 1 do
                    if getmetatable(s) == Square then acc = (acc + 1) % 100000007 end
                    local q = nil
                    if getmetatable(s) == Square then q = s end
                    if q ~= nil then acc = (acc + 1) % 100000007 end
                end
                return acc
            end

            function nullable(n)
                local acc = 0
                for i = 0, n - 1 do
                    local v = nil
                    if i % 3 ~= 0 then v = i end
                    acc = (acc + (v or 0)) % 100000007
                end
                return acc
            end

            function enums(n)
                local Red, Green, Blue = 0, 1, 2
                local acc = 0
                for i = 0, n - 1 do
                    local r = i % 3
                    local c
                    if r == 0 then c = Red elseif r == 1 then c = Green else c = Blue end
                    if c == Red then acc = (acc + 1) % 100000007
                    elseif c == Green then acc = (acc + 2) % 100000007
                    else acc = (acc + 3) % 100000007 end
                end
                return acc
            end

            function sortArray(n)
                local xs = {}
                for i = 0, n - 1 do xs[i + 1] = (i * 7919) % 10007 end
                table.sort(xs, function(a, b) return a < b end)
                local acc = 0
                for i = 1, #xs do acc = (acc + xs[i] * ((i - 1) % 7 + 1)) % 100000007 end
                return acc
            end

            -- Lua has one sort, so both Surtr sort cases compare against the same Lua row: the
            -- question the A/B asks is Surtr-internal.
            sortBytecode = sortArray

            function tuples(n)
                local acc = 0
                for i = 0, n - 1 do
                    local t = {i, i + 1}
                    acc = (acc + t[1] + t[2]) % 100000007
                end
                return acc
            end

            function vec2Math(n)
                local v = Vec2.new(0.5, -0.25)
                local p = Vec2.new(0.0, 0.0)
                local acc = 0.0
                for i = 0, n - 1 do
                    p = p:add(v):scale(0.5)
                    acc = acc * 0.5 + p:dot(v) + (i % 7) * 0.125
                end
                return acc
            end

            -- Lua has no value types, so this is vec2Math's body a second time: the pair that is
            -- an A/B in Surtr and in C# is one implementation here, which is itself the finding.
            function vec2Class(n)
                local v = Vec2.new(0.5, -0.25)
                local p = Vec2.new(0.0, 0.0)
                local acc = 0.0
                for i = 0, n - 1 do
                    p = p:add(v):scale(0.5)
                    acc = acc * 0.5 + p:dot(v) + (i % 7) * 0.125
                end
                return acc
            end

            function vec2Fields(n)
                local body = Body.new(Vec2.new(0.0, 0.0), Vec2.new(0.5, -0.25))
                local acc = 0.0
                for i = 0, n - 1 do
                    body.position = body.position:add(body.velocity):scale(0.5)
                    acc = acc * 0.5 + body.position:dot(body.velocity) + (i % 7) * 0.125
                end
                return acc
            end

            -- Multiple returns are native to Lua, so this is the one case where Lua's own idiom is
            -- exactly what the new Surtr convention does rather than an approximation of it.
            function divmod(a, b)
                return math.floor(a / b), a % b
            end

            function tupleReturn(n)
                local acc = 0
                for i = 0, n - 1 do
                    local q, r = divmod(i, 7)
                    acc = (acc + q * 3 + r) % 100000007
                end
                return acc
            end

            function closureCapture(n)
                local cap = {a = 0}
                local function bump(x)
                    cap.a = (cap.a + x) % 100000007
                    return cap.a
                end
                local acc = 0
                for i = 0, n - 1 do acc = (acc + bump(i)) % 100000007 end
                return acc
            end

            function mathFns(n)
                local acc = 0.5
                for i = 0, n - 1 do
                    acc = acc * 0.25 + math.sin(acc) * 0.5 + math.cos(acc * 0.5) * 0.25 + math.sqrt(1.0 + acc * acc) * 0.1
                end
                return acc
            end

            function retainedObjects(n)
                local keep = {}
                for i = 0, n - 1 do
                    local c = {a = i, b = i * 3}
                    if i % 4 == 0 then keep[#keep + 1] = c end
                end
                local acc = 0
                for i = 1, #keep do acc = (acc + keep[i].a) % 100000007 end
                return acc
            end

            function stringTransform(n)
                local s = "the quick brown fox jumps over the lazy dog"
                local acc = 0
                for i = 0, n - 1 do
                    local sub = string.sub(s, (i % 10) + 1, (i % 10) + 8)
                    acc = (acc + #sub) % 100000007
                    local rep = string.gsub(s, "the", "a")
                    acc = (acc + #rep) % 100000007
                end
                return acc
            end
            """;

        private const long Modulus = 100000007;

        private static readonly Workload[] All = new[]
        {
            new Workload("fib", 24, WorkloadKind.Int, "recursive calls, frame setup", Fib),
            new Workload("intLoop", 1000000, WorkloadKind.Int, "integer arithmetic and branching", IntLoop),
            new Workload("tightGuard", 1000000, WorkloadKind.Int, "a counted loop whose body is one store: the guard is a real fraction of it", TightGuard),
            new Workload("floatLoop", 1000000, WorkloadKind.Float, "float arithmetic, NaN-boxed", baselineFloat: FloatLoop),
            new Workload("mathFns", 100000, WorkloadKind.Float, "float calls across the native boundary", baselineFloat: MathFns),
            new Workload("arrayFill", 50000, WorkloadKind.Int, "array growth via push", ArrayFill),
            new Workload("arrayIndex", 300000, WorkloadKind.Int, "ArrGet/ArrSet on a sized array", ArrayIndex),
            new Workload("dictOps", 30000, WorkloadKind.Int, "int-keyed dict, specialised store", DictOps),
            new Workload("dictMembers", 30000, WorkloadKind.Int, "dict member surface lowered to opcodes", DictMembers),
            new Workload("dictString", 300000, WorkloadKind.Int, "string-keyed dict, comparer path", DictString),
            new Workload("stringConcat", 1200, WorkloadKind.Int, "pairwise StrCat, quadratic by nature", StringConcat),
            new Workload("stringInterp", 100000, WorkloadKind.Int, "n-ary StrCat from interpolation", StringInterp),
            new Workload("stringOps", 300000, WorkloadKind.Int, "length and text equality", StringOps),
            new Workload("stringTransform", 100000, WorkloadKind.Int, "substring/replace native calls, allocating per call", StringTransform),
            new Workload("closures", 300000, WorkloadKind.Int, "closure invocation", Closures),
            new Workload("closureCreate", 300000, WorkloadKind.Int, "zero-capture closure creation per iteration", ClosureCreate),
            new Workload("methodGroupInvoke", 300000, WorkloadKind.Int, "invocation through a method-group value, non-inlinable target", MethodGroupInvoke),
            new Workload("closureCapture", 300000, WorkloadKind.Int, "closure environment + upvalue read/write", ClosureCapture),
            new Workload("methodCalls", 300000, WorkloadKind.Int, "direct instance dispatch", MethodCalls),
            new Workload("localModule", 300000, WorkloadKind.Int, "the same call inside one module: the control for crossModule", CrossModule),
            new Workload("crossModule", 300000, WorkloadKind.Int, "a call that crosses a module boundary: two table hops instead of one", CrossModule),
            new Workload("virtualCalls", 300000, WorkloadKind.Int, "vtable dispatch", VirtualCalls),
            new Workload("interfaceCalls", 300000, WorkloadKind.Int, "interfaceId table dispatch", InterfaceCalls),
            new Workload("fieldAccess", 300000, WorkloadKind.Int, "instance field get/set", FieldAccess),
            new Workload("propertyAccess", 300000, WorkloadKind.Int, "get_x/set_x accessor pair", PropertyAccess),
            new Workload("exceptions", 8000, WorkloadKind.Int, "raise and handler-table search", Exceptions),
            new Workload("forIn", 50000, WorkloadKind.Int, "for-in lowered to an indexed loop", ForIn),
            new Workload("forInDict", 50000, WorkloadKind.Int, "for-in over a dictionary: key snapshot, value lookup and pair per entry", ForInDict),
            new Workload("iterator", 50000, WorkloadKind.Int, "the general iterate()/moveNext() path", Iterator),
            new Workload("genYield", 50000, WorkloadKind.Int, "generator: suspend and resume a frame per element", GenYield),
            new Workload("handIterator", 50000, WorkloadKind.Int, "the cursor class a generator replaces", HandIterator),
            new Workload("genDelegate", 50000, WorkloadKind.Int, "three levels of yield from, through the delegation link", GenDelegate),
            new Workload("genSend", 50000, WorkloadKind.Int, "coroutine: a value injected at every yield", GenSend),
            new Workload("genFinally", 50000, WorkloadKind.Int, "generator suspending inside a try/finally", GenFinally),
            new Workload("interop", 300000, WorkloadKind.Int, "host function call", Interop),
            new Workload("valueClass", 300000, WorkloadKind.Int, "value class, erased to its field", ValueClass),
            new Workload("generics", 300000, WorkloadKind.Int, "erased slot: box in, cast out", Generics),
            new Workload("allocation", 300000, WorkloadKind.Int, "object allocation and collection", Allocation),
            new Workload("retainedObjects", 100000, WorkloadKind.Int, "allocating with survivors kept alive", RetainedObjects),
            new Workload("switchDense", 300000, WorkloadKind.Int, "Switch jump table", SwitchDense),
            new Workload("typeTest", 300000, WorkloadKind.Int, "InstanceOf and CastOrNull", TypeTest),
            new Workload("nullable", 300000, WorkloadKind.Int, "nullable primitive, absent tag", Nullable),
            new Workload("enums", 300000, WorkloadKind.Int, "enum case access and comparison", Enums),
            new Workload("sortArray", 20000, WorkloadKind.Int, "native member re-entering the VM per compare", SortArray),
            new Workload("sortBytecode", 20000, WorkloadKind.Int, "the same merge sort in Surtr: no boundary per compare, but the merge costs bytecode", SortArray),
            new Workload("tuples", 300000, WorkloadKind.Int, "tuple literal and element read, inline slots", Tuples),
            new Workload("vec2Math", 300000, WorkloadKind.Float, "multi-field value type: construct, pass and return by value", baselineFloat: Vec2Math),
            new Workload("vec2Fields", 300000, WorkloadKind.Float, "value-type fields stored inline in an instance", baselineFloat: Vec2Fields),
            new Workload("vec2Class", 300000, WorkloadKind.Float, "vec2Math with a reference class: the A/B for the alloc column", baselineFloat: Vec2Class),
            new Workload("tupleReturn", 300000, WorkloadKind.Int, "multi-slot return and destructuring, no tuple object", TupleReturn),
        };

        public static IReadOnlyList<Workload> AllWorkloads => All;

        #region The C# baselines
        // Each is the same algorithm an ordinary C# program would use for the job. Where the JIT
        // then inlines the abstraction away that is C#'s honest answer and is left alone; what is
        // not allowed is writing the abstraction out of the source by hand, which is what six of
        // these used to do. See the remarks on this class.

        private sealed class Adder
        {
            public long Base;
            public Adder(long @base) => Base = @base;
            public long Add(long x) => Base + x;
        }

        private class Shape
        {
            public virtual long Sides() => 0;
        }

        private sealed class Square : Shape
        {
            public override long Sides() => 4;
        }

        private interface ISides
        {
            long Sides();
        }

        private sealed class Triangle : ISides
        {
            public long Sides() => 3;
        }

        private sealed class Holder
        {
            public long Value { get; set; }
        }

        private sealed class Cell
        {
            public long A;
            public long B;
            public Cell(long a, long b) { A = a; B = b; }
        }

        private sealed class Box<T>
        {
            private readonly T _value;
            public Box(T value) => _value = value;
            public T Get() => _value;
        }

        private readonly struct EntityId
        {
            public readonly long Raw;
            public EntityId(long raw) => Raw = raw;
        }

        private enum Color
        {
            Red,
            Green,
            Blue,
        }

        private static long Fib(long n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

        private static long CrossModule(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + i + 1) % Modulus;
            return acc;
        }

        private static long TightGuard(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = i + 1;
            return acc;
        }

        private static long IntLoop(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + i * 31) % Modulus;
            return acc;
        }

        private static double FloatLoop(long n)
        {
            double acc = 1.0;
            for (long i = 0; i < n; i++)
                acc = acc * 1.0000001 + 0.5;
            return acc;
        }

        // A List rather than a sized array: the Surtr side grows through push, and preallocating
        // would leave the growth this case exists to measure out of the baseline entirely.
        private static long ArrayFill(long n)
        {
            var xs = new List<long>();
            for (long i = 0; i < n; i++)
                xs.Add(i);
            long acc = 0;
            for (int i = 0; i < xs.Count; i++)
                acc = (acc + xs[i]) % Modulus;
            return acc;
        }

        private static long ArrayIndex(long n)
        {
            var xs = new List<long>();
            for (long i = 0; i < 256; i++)
                xs.Add(i);
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                int j = (int)(i % 256);
                xs[j] = xs[j] + 1;
                acc = (acc + xs[j]) % Modulus;
            }
            return acc;
        }

        private static long DictOps(long n)
        {
            var m = new Dictionary<long, long>();
            for (long i = 0; i < n; i++)
                m[i] = i * 3;
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + m[i]) % Modulus;
            return acc;
        }

        private static long DictMembers(long n)
        {
            var m = new Dictionary<long, long>();
            for (long i = 0; i < n; i++)
                m[i] = i * 3;
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                if (m.ContainsKey(i))
                    acc = (acc + m[i]) % Modulus;
                if (m.Remove(i))
                    acc = (acc + 1) % Modulus;
            }
            return acc;
        }

        private static long DictString(long n)
        {
            var keys = new List<string>();
            for (long i = 0; i < 64; i++)
                keys.Add("k" + i);
            var m = new Dictionary<string, long>();
            for (int i = 0; i < 64; i++)
                m[keys[i]] = i;
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + m[keys[(int)(i % 64)]]) % Modulus;
            return acc;
        }

        private static long StringConcat(long n)
        {
            string s = "";
            for (long i = 0; i < n; i++)
                s = s + "x";
            return s.Length;
        }

        private static long StringInterp(long n)
        {
            long total = 0;
            for (long i = 0; i < n; i++)
            {
                string s = "a" + i + "b" + i + "c";
                total = (total + s.Length) % Modulus;
            }
            return total;
        }

        private static long StringOps(long n)
        {
            const string s = "the quick brown fox";
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                acc = (acc + s.Length) % Modulus;
                if (s == "the quick brown fox")
                    acc = (acc + 1) % Modulus;
            }
            return acc;
        }

        private static long Closures(long n)
        {
            Func<long, long> add = a => a + 1;
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + add(i)) % Modulus;
            return acc;
        }

        private static long ClosureCreate(long n)
        {
            // A non-capturing lambda is a single cached delegate in C#, so the closure is created
            // once by the compiler and the loop only calls it — the same ideal the Surtr fast path
            // targets. The baseline measures the call, and the byte counters expose what each
            // engine's closure-creation path really allocates.
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                Func<long, long> add = a => a + 1;
                acc = (acc + add(i)) % Modulus;
            }
            return acc;
        }

        private static long Accumulate(long n)
        {
            long acc = n;
            for (long i = 0; i < 1; i++)
                acc += i;
            return acc;
        }

        private static long MethodGroupInvoke(long n)
        {
            // The C# compiler binds a method group to the delegate pointing straight at the method
            // (ldftn), no forwarding stub — the behaviour the Surtr direct binding replicates.
            Func<long, long> add = Accumulate;
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + add(i)) % Modulus;
            return acc;
        }

        private static long MethodCalls(long n)
        {
            var a = new Adder(7);
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + a.Add(i)) % Modulus;
            return acc;
        }

        private static long VirtualCalls(long n)
        {
            Shape s = new Square();
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + s.Sides()) % Modulus;
            return acc;
        }

        private static long InterfaceCalls(long n)
        {
            ISides s = new Triangle();
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + s.Sides()) % Modulus;
            return acc;
        }

        private static long FieldAccess(long n)
        {
            var c = new Cell(0, 1);
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                c.A = i;
                c.B = c.A + 1;
                acc = (acc + c.B) % Modulus;
            }
            return acc;
        }

        private static long PropertyAccess(long n)
        {
            var h = new Holder();
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                h.Value = i;
                acc = (acc + h.Value) % Modulus;
            }
            return acc;
        }

        private static long Exceptions(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                try
                {
                    throw new InvalidOperationException("boom");
                }
                catch (InvalidOperationException)
                {
                    acc = acc + 1;
                }
            }
            return acc;
        }

        private static long ForIn(long n)
        {
            var xs = new List<long>();
            for (long i = 0; i < n; i++)
                xs.Add(i);
            long acc = 0;
            foreach (long x in xs)
                acc = (acc + x) % Modulus;
            return acc;
        }

        private static long ForInDict(long n)
        {
            var m = new Dictionary<long, long>();
            for (long i = 0; i < n; i++)
                m[i] = i * 3;
            long acc = 0;
            foreach (var entry in m)
                acc = (acc + entry.Key + entry.Value) % Modulus;
            return acc;
        }

        // Typed as the interface, so the enumerator is reached through IEnumerable<T> and boxed —
        // the closest C# has to the path Surtr takes when the sequence is not statically an array.
        private static long Iterator(long n)
        {
            var xs = new List<long>();
            for (long i = 0; i < n; i++)
                xs.Add(i);
            IEnumerable<long> seq = xs;
            long acc = 0;
            foreach (long x in seq)
                acc = (acc + x) % Modulus;
            return acc;
        }

        // C#'s generator is `yield return`, compiled to a state machine (Plan-Generadores §4.A) -
        // the other implementation strategy, measured against the copied frame Surtr chose.
        private static IEnumerable<long> UpToGen(long n)
        {
            for (long i = 0; i < n; i++)
                yield return i;
        }

        private static long GenYield(long n)
        {
            long acc = 0;
            foreach (long x in UpToGen(n))
                acc = (acc + x) % Modulus;
            return acc;
        }

        // C# has no delegation form either - `yield return` cannot re-yield a sequence - so each
        // level writes the foreach out, which is the shape Surtr's loop lowering also takes when
        // the operand is not a generator.
        private static IEnumerable<long> DelegLeaf(long n)
        {
            for (long i = 0; i < n; i++)
                yield return i;
        }

        private static IEnumerable<long> DelegMid(long n)
        {
            foreach (long x in DelegLeaf(n))
                yield return x;
        }

        private static IEnumerable<long> DelegTop(long n)
        {
            foreach (long x in DelegMid(n))
                yield return x;
        }

        private static long GenDelegate(long n)
        {
            long acc = 0;
            foreach (long x in DelegTop(n))
                acc = (acc + x) % Modulus;
            return acc;
        }

        // C# iterators are one-way: `yield return` has no value coming back, so the honest
        // counterpart to a send loop is an explicit cursor the driver hands a value to each step.
        // Writing it as an IEnumerable would measure a different program.
        private sealed class EchoCursor
        {
            private readonly long _n;

            public EchoCursor(long n) => _n = n;

            public long Current { get; private set; }

            public bool MoveNext(long sent)
            {
                long next = Current == 0 && !_started ? 0 : sent + 1;
                _started = true;

                if (next >= _n)
                    return false;

                Current = next;
                return true;
            }

            private bool _started;
        }

        private static long GenSend(long n)
        {
            var cursor = new EchoCursor(n);
            long acc = 0;

            while (cursor.MoveNext(cursor.Current))
                acc = (acc + cursor.Current) % Modulus;

            return acc;
        }

        // The `try/finally` around the suspension, which C# does allow in an iterator - the
        // `finally` runs when the enumerator is disposed, which is exactly the guarantee Surtr
        // just grew.
        private static IEnumerable<long> GuardedRange(long n)
        {
            long i = 0;

            try
            {
                while (i < n)
                {
                    yield return i;
                    i++;
                }
            }
            finally
            {
                i = 0;
            }
        }

        private static long GenFinally(long n)
        {
            long acc = 0;
            foreach (long x in GuardedRange(n))
                acc = (acc + x) % Modulus;
            return acc;
        }

        /// <summary>The cursor written by hand, which is what a generator saves you writing.</summary>
        private sealed class RangeCursor
        {
            private long _i;
            private readonly long _n;

            public RangeCursor(long n) => _n = n;

            public long Current => _i - 1;

            public bool MoveNext()
            {
                if (_i >= _n)
                    return false;

                _i++;
                return true;
            }
        }

        private static long HandIterator(long n)
        {
            var cursor = new RangeCursor(n);
            long acc = 0;
            while (cursor.MoveNext())
                acc = (acc + cursor.Current) % Modulus;
            return acc;
        }

        private static long HostAdd(long value) => value + 1;

        private static long Interop(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + HostAdd(i)) % Modulus;
            return acc;
        }

        private static long ValueClass(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                var id = new EntityId(i);
                acc = (acc + id.Raw) % Modulus;
            }
            return acc;
        }

        private static long Generics(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                var b = new Box<long>(i);
                acc = (acc + b.Get()) % Modulus;
            }
            return acc;
        }

        private static long Allocation(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                var c = new Cell(i, i + 1);
                acc = (acc + c.A + c.B) % Modulus;
            }
            return acc;
        }

        private static long SwitchDense(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                long v = (i % 8) switch
                {
                    0 => 1,
                    1 => 2,
                    2 => 3,
                    3 => 4,
                    4 => 5,
                    5 => 6,
                    6 => 7,
                    _ => 8,
                };
                acc = (acc + v) % Modulus;
            }
            return acc;
        }

        private static long TypeTest(long n)
        {
            Shape s = new Square();
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                if (s is Square)
                    acc = (acc + 1) % Modulus;
                var q = s as Square;
                if (q != null)
                    acc = (acc + 1) % Modulus;
            }
            return acc;
        }

        private static long Nullable(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                long? v = i % 3 == 0 ? null : i;
                acc = (acc + (v ?? 0)) % Modulus;
            }
            return acc;
        }

        private static long Enums(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                long r = i % 3;
                Color c = r == 0 ? Color.Red : (r == 1 ? Color.Green : Color.Blue);
                if (c == Color.Red)
                    acc = (acc + 1) % Modulus;
                else if (c == Color.Green)
                    acc = (acc + 2) % Modulus;
                else
                    acc = (acc + 3) % Modulus;
            }
            return acc;
        }

        private static long SortArray(long n)
        {
            var xs = new List<long>();
            for (long i = 0; i < n; i++)
                xs.Add((i * 7919) % 10007);
            xs.Sort((a, b) => a.CompareTo(b));
            long acc = 0;
            for (int i = 0; i < xs.Count; i++)
                acc = (acc + xs[i] * (i % 7 + 1)) % Modulus;
            return acc;
        }

        private static long Tuples(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                var t = (i, i + 1);
                acc = (acc + t.Item1 + t.Item2) % Modulus;
            }
            return acc;
        }

        // A real C# struct, which is what a C# program would reach for here. The JIT will keep it
        // in registers and the whole loop allocates nothing — that is C#'s honest answer to the
        // same question Surtr's value types answer, and is the number worth comparing against.
        private readonly struct Vec2
        {
            public readonly double X;
            public readonly double Y;

            public Vec2(double x, double y)
            {
                X = x;
                Y = y;
            }

            public Vec2 Add(Vec2 other) => new Vec2(X + other.X, Y + other.Y);
            public Vec2 Scale(double k) => new Vec2(X * k, Y * k);
            public double Dot(Vec2 other) => X * other.X + Y * other.Y;
        }

        // The reference twin of the struct above, for the vec2Class A/B. C# answers the same
        // question Surtr does here, and the JIT will not sink these allocations away.
        private sealed class Vec2Ref
        {
            public readonly double X;
            public readonly double Y;

            public Vec2Ref(double x, double y)
            {
                X = x;
                Y = y;
            }

            public Vec2Ref Add(Vec2Ref other) => new Vec2Ref(X + other.X, Y + other.Y);
            public Vec2Ref Scale(double k) => new Vec2Ref(X * k, Y * k);
            public double Dot(Vec2Ref other) => X * other.X + Y * other.Y;
        }

        private sealed class Body
        {
            public Vec2 Position;
            public Vec2 Velocity;

            public Body(Vec2 position, Vec2 velocity)
            {
                Position = position;
                Velocity = velocity;
            }
        }

        private static double Vec2Math(long n)
        {
            var v = new Vec2(0.5, -0.25);
            var p = new Vec2(0.0, 0.0);
            double acc = 0.0;
            for (long i = 0; i < n; i++)
            {
                p = p.Add(v).Scale(0.5);
                acc = acc * 0.5 + p.Dot(v) + (i % 7) * 0.125;
            }
            return acc;
        }

        private static double Vec2Class(long n)
        {
            var v = new Vec2Ref(0.5, -0.25);
            var p = new Vec2Ref(0.0, 0.0);
            double acc = 0.0;
            for (long i = 0; i < n; i++)
            {
                p = p.Add(v).Scale(0.5);
                acc = acc * 0.5 + p.Dot(v) + (i % 7) * 0.125;
            }
            return acc;
        }

        private static double Vec2Fields(long n)
        {
            var body = new Body(new Vec2(0.0, 0.0), new Vec2(0.5, -0.25));
            double acc = 0.0;
            for (long i = 0; i < n; i++)
            {
                body.Position = body.Position.Add(body.Velocity).Scale(0.5);
                acc = acc * 0.5 + body.Position.Dot(body.Velocity) + (i % 7) * 0.125;
            }
            return acc;
        }

        private static (long Quotient, long Remainder) DivMod(long a, long b) => (a / b, a % b);

        private static long TupleReturn(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                var (q, r) = DivMod(i, 7);
                acc = (acc + q * 3 + r) % Modulus;
            }
            return acc;
        }

        // A local function capturing a mutable holder and mutating it through the capture, the C#
        // shape closest to what Surtr's closureCapture does. The JIT may keep the holder in a
        // register and fuse the capture away entirely — that is C#'s honest answer and is left alone.
        private static long ClosureCapture(long n)
        {
            var cap = new Cell(0, 0);
            Func<long, long> bump = x =>
            {
                cap.A = (cap.A + x) % Modulus;
                return cap.A;
            };
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + bump(i)) % Modulus;
            return acc;
        }

        private static double MathFns(long n)
        {
            double acc = 0.5;
            for (long i = 0; i < n; i++)
                acc = acc * 0.25 + Math.Sin(acc) * 0.5 + Math.Cos(acc * 0.5) * 0.25 + Math.Sqrt(1.0 + acc * acc) * 0.1;
            return acc;
        }

        private static long RetainedObjects(long n)
        {
            var keep = new List<Cell>();
            for (long i = 0; i < n; i++)
            {
                var c = new Cell(i, i * 3);
                if (i % 4 == 0)
                    keep.Add(c);
            }
            long acc = 0;
            for (int i = 0; i < keep.Count; i++)
                acc = (acc + keep[i].A) % Modulus;
            return acc;
        }

        private static long StringTransform(long n)
        {
            const string s = "the quick brown fox jumps over the lazy dog";
            long acc = 0;
            for (long i = 0; i < n; i++)
            {
                string sub = s.Substring((int)(i % 10), 8);
                acc = (acc + sub.Length) % Modulus;
                string rep = s.Replace("the", "a");
                acc = (acc + rep.Length) % Modulus;
            }
            return acc;
        }
        #endregion
    }
}
