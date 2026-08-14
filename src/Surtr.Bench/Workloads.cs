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

        /// <summary>The C# reference implementation, used both for timing and to compute the expected result.</summary>
        public Func<long, long>? BaselineInt { get; }

        /// <summary>The C# reference implementation for a float-returning workload.</summary>
        public Func<long, double>? BaselineFloat { get; }

        public Workload(
            string name,
            long size,
            WorkloadKind kind,
            Func<long, long>? baselineInt = null,
            Func<long, double>? baselineFloat = null)
        {
            Name = name;
            Size = size;
            Kind = kind;
            BaselineInt = baselineInt;
            BaselineFloat = baselineFloat;
        }
    }

    /// <summary>
    /// The benchmark catalogue: the Surtr module, the Lua chunk and the C# baselines, all expressing
    /// the same thirteen workloads.
    /// </summary>
    /// <remarks>
    /// Every checksum keeps its values below <c>2^31</c> by folding through <c>% 100000007</c> at
    /// each step, so the Surtr int32 arithmetic, the C# <c>long</c> arithmetic and Lua's doubles all
    /// agree exactly and the verification cannot be fooled by an overflow.
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

            fun dictOps(n: int): int {
                let m: {int: int} = {};
                for (var i = 0; i < n; i += 1) { m[i] = i * 3; }
                var acc: int = 0;
                for (var i = 0; i < n; i += 1) { acc = (acc + m[i]) % 100000007; }
                return acc;
            }

            fun stringConcat(n: int): int {
                var s: string = "";
                for (var i = 0; i < n; i += 1) { s = s + "x"; }
                return s.length;
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
            """;

        /// <summary>The equivalent Lua chunk, loaded once into one MoonSharp script.</summary>
        public const string LuaSource = """
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

            function dictOps(n)
                local m = {}
                for i = 0, n - 1 do m[i] = i * 3 end
                local acc = 0
                for i = 0, n - 1 do acc = (acc + m[i]) % 100000007 end
                return acc
            end

            function stringConcat(n)
                local s = ""
                for i = 0, n - 1 do s = s .. "x" end
                return #s
            end

            function closures(n)
                local add = function(a) return a + 1 end
                local acc = 0
                for i = 0, n - 1 do acc = (acc + add(i)) % 100000007 end
                return acc
            end

            function methodCalls(n)
                local base = 7
                local add = function(x) return base + x end
                local acc = 0
                for i = 0, n - 1 do acc = (acc + add(i)) % 100000007 end
                return acc
            end

            function virtualCalls(n)
                local acc = 0
                for i = 0, n - 1 do acc = (acc + 4) % 100000007 end
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

            function hostAdd(value) return value + 1 end

            function interop(n)
                local acc = 0
                for i = 0, n - 1 do acc = (acc + hostAdd(i)) % 100000007 end
                return acc
            end

            function valueClass(n)
                local acc = 0
                for i = 0, n - 1 do acc = (acc + i) % 100000007 end
                return acc
            end
            """;

        private static readonly Workload[] All = new[]
        {
            new Workload("fib", 24, WorkloadKind.Int, Fib),
            new Workload("intLoop", 1000000, WorkloadKind.Int, IntLoop),
            new Workload("floatLoop", 1000000, WorkloadKind.Float, baselineFloat: FloatLoop),
            new Workload("arrayFill", 50000, WorkloadKind.Int, ArrayFill),
            new Workload("dictOps", 30000, WorkloadKind.Int, DictOps),
            new Workload("stringConcat", 1200, WorkloadKind.Int, StringConcat),
            new Workload("closures", 300000, WorkloadKind.Int, Closures),
            new Workload("methodCalls", 300000, WorkloadKind.Int, MethodCalls),
            new Workload("virtualCalls", 300000, WorkloadKind.Int, VirtualCalls),
            new Workload("exceptions", 8000, WorkloadKind.Int, Exceptions),
            new Workload("forIn", 50000, WorkloadKind.Int, ForIn),
            new Workload("interop", 300000, WorkloadKind.Int, Interop),
            new Workload("valueClass", 300000, WorkloadKind.Int, ValueClass),
        };

        public static IReadOnlyList<Workload> AllWorkloads => All;

        private static long Fib(long n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);

        private static long IntLoop(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + i * 31) % 100000007;
            return acc;
        }

        private static double FloatLoop(long n)
        {
            double acc = 1.0;
            for (long i = 0; i < n; i++)
                acc = acc * 1.0000001 + 0.5;
            return acc;
        }

        private static long ArrayFill(long n)
        {
            var xs = new long[n];
            for (long i = 0; i < n; i++)
                xs[i] = i;
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + xs[i]) % 100000007;
            return acc;
        }

        private static long DictOps(long n)
        {
            var m = new Dictionary<long, long>();
            for (long i = 0; i < n; i++)
                m[i] = i * 3;
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + m[i]) % 100000007;
            return acc;
        }

        private static long StringConcat(long n)
        {
            string s = "";
            for (long i = 0; i < n; i++)
                s = s + "x";
            return s.Length;
        }

        private static long Closures(long n)
        {
            Func<long, long> add = i => i + 1;
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + add(i)) % 100000007;
            return acc;
        }

        private static long MethodCalls(long n)
        {
            const long b = 7;
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + b + i) % 100000007;
            return acc;
        }

        private static long VirtualCalls(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + 4) % 100000007;
            return acc;
        }

        private static long Exceptions(long n) => n;

        private static long ForIn(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + i) % 100000007;
            return acc;
        }

        private static long Interop(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + i + 1) % 100000007;
            return acc;
        }

        private static long ValueClass(long n)
        {
            long acc = 0;
            for (long i = 0; i < n; i++)
                acc = (acc + i) % 100000007;
            return acc;
        }
    }
}
