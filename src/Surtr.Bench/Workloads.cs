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
        public const string ModuleSource = """
            value class EntityId {
                public let raw: int;
                public constructor(raw: int) { this.raw = raw; }
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

            class Triangle : ISides {
                public override fun sides(): int { return 3; }
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

            fun tuples(n: int): int {
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) {
                    let t = (i, i + 1);
                    acc = (acc + t[0] + t[1]) % 100000007;
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

            function fib(n)
                if n < 2 then return n end
                return fib(n - 1) + fib(n - 2)
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

            function tuples(n)
                local acc = 0
                for i = 0, n - 1 do
                    local t = {i, i + 1}
                    acc = (acc + t[1] + t[2]) % 100000007
                end
                return acc
            end
            """;

        private const long Modulus = 100000007;

        private static readonly Workload[] All = new[]
        {
            new Workload("fib", 24, WorkloadKind.Int, "recursive calls, frame setup", Fib),
            new Workload("intLoop", 1000000, WorkloadKind.Int, "integer arithmetic and branching", IntLoop),
            new Workload("floatLoop", 1000000, WorkloadKind.Float, "float arithmetic, NaN-boxed", baselineFloat: FloatLoop),
            new Workload("arrayFill", 50000, WorkloadKind.Int, "array growth via push", ArrayFill),
            new Workload("arrayIndex", 300000, WorkloadKind.Int, "ArrGet/ArrSet on a sized array", ArrayIndex),
            new Workload("dictOps", 30000, WorkloadKind.Int, "int-keyed dict, specialised store", DictOps),
            new Workload("dictMembers", 30000, WorkloadKind.Int, "dict member surface lowered to opcodes", DictMembers),
            new Workload("dictString", 300000, WorkloadKind.Int, "string-keyed dict, comparer path", DictString),
            new Workload("stringConcat", 1200, WorkloadKind.Int, "pairwise StrCat, quadratic by nature", StringConcat),
            new Workload("stringInterp", 100000, WorkloadKind.Int, "n-ary StrCat from interpolation", StringInterp),
            new Workload("stringOps", 300000, WorkloadKind.Int, "length and text equality", StringOps),
            new Workload("closures", 300000, WorkloadKind.Int, "closure invocation", Closures),
            new Workload("methodCalls", 300000, WorkloadKind.Int, "direct instance dispatch", MethodCalls),
            new Workload("virtualCalls", 300000, WorkloadKind.Int, "vtable dispatch", VirtualCalls),
            new Workload("interfaceCalls", 300000, WorkloadKind.Int, "interfaceId table dispatch", InterfaceCalls),
            new Workload("fieldAccess", 300000, WorkloadKind.Int, "instance field get/set", FieldAccess),
            new Workload("propertyAccess", 300000, WorkloadKind.Int, "get_x/set_x accessor pair", PropertyAccess),
            new Workload("exceptions", 8000, WorkloadKind.Int, "raise and handler-table search", Exceptions),
            new Workload("forIn", 50000, WorkloadKind.Int, "for-in lowered to an indexed loop", ForIn),
            new Workload("iterator", 50000, WorkloadKind.Int, "the general iterate()/moveNext() path", Iterator),
            new Workload("interop", 300000, WorkloadKind.Int, "host function call", Interop),
            new Workload("valueClass", 300000, WorkloadKind.Int, "value class, erased to its field", ValueClass),
            new Workload("generics", 300000, WorkloadKind.Int, "erased slot: box in, cast out", Generics),
            new Workload("allocation", 300000, WorkloadKind.Int, "object allocation and collection", Allocation),
            new Workload("switchDense", 300000, WorkloadKind.Int, "Switch jump table", SwitchDense),
            new Workload("typeTest", 300000, WorkloadKind.Int, "InstanceOf and CastOrNull", TypeTest),
            new Workload("nullable", 300000, WorkloadKind.Int, "nullable primitive, absent tag", Nullable),
            new Workload("enums", 300000, WorkloadKind.Int, "enum case access and comparison", Enums),
            new Workload("sortArray", 20000, WorkloadKind.Int, "native member re-entering the VM per compare", SortArray),
            new Workload("tuples", 300000, WorkloadKind.Int, "TupPack and TupGetC", Tuples),
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
        #endregion
    }
}
