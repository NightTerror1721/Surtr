#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Surtr.Runtime.Testing
{
    /// <summary>What became of one discovered <c>@Test</c> method.</summary>
    public enum SurtrTestOutcome
    {
        /// <summary>The body ran to completion.</summary>
        Passed = 0,

        /// <summary>An exception escaped the body.</summary>
        Failed = 1,

        /// <summary>
        /// The method carried <c>@TestIgnore</c>, so it was discovered and reported but never run.
        /// </summary>
        Skipped = 2,
    }

    /// <summary>The outcome of running one <c>@Test</c> method.</summary>
    public sealed class SurtrTestResult
    {
        internal SurtrTestResult(string suite, string name, SurtrTestOutcome outcome, string? failure, string? skipReason)
        {
            Suite = suite;
            Name = name;
            Outcome = outcome;
            Failure = failure;
            SkipReason = skipReason;
        }

        /// <summary>The suite it belongs to: its <c>@TestSuite</c> name, or the class name.</summary>
        public string Suite { get; }

        /// <summary>Its <c>@Test</c> name, or the method name.</summary>
        public string Name { get; }

        /// <summary>Which of the three things happened to it.</summary>
        public SurtrTestOutcome Outcome { get; }

        /// <summary>
        /// Whether the method ran to completion. A skipped test is not passed — it did not run —
        /// so a summary that only counts this reports it the way a failure would not be reported
        /// twice; <see cref="Outcome"/> is what separates the two.
        /// </summary>
        public bool Passed => Outcome == SurtrTestOutcome.Passed;

        /// <summary>Whether <c>@TestIgnore</c> kept it from running.</summary>
        public bool Skipped => Outcome == SurtrTestOutcome.Skipped;

        /// <summary>When it failed, the message of the exception that escaped it.</summary>
        public string? Failure { get; }

        /// <summary>When it was skipped, the reason its <c>@TestIgnore</c> carried, if any.</summary>
        public string? SkipReason { get; }
    }

    /// <summary>The measurement of one <c>@Benchmark</c> method.</summary>
    /// <remarks>
    /// Deliberately not a <see cref="SurtrTestResult"/> with extra fields: a benchmark answers a
    /// different question — how long, not whether — and folding it into the test outcomes would put
    /// timings on every passing test and a pass/fail on every measurement.
    /// </remarks>
    public sealed class SurtrBenchmarkResult
    {
        internal SurtrBenchmarkResult(
            string suite,
            string name,
            int iterations,
            double medianMilliseconds,
            double minimumMilliseconds,
            double totalMilliseconds,
            string? failure)
        {
            Suite = suite;
            Name = name;
            Iterations = iterations;
            MedianMilliseconds = medianMilliseconds;
            MinimumMilliseconds = minimumMilliseconds;
            TotalMilliseconds = totalMilliseconds;
            Failure = failure;
        }

        /// <summary>The suite it belongs to: its class's <c>@TestSuite</c> name, or the class name.</summary>
        public string Suite { get; }

        /// <summary>The method's name.</summary>
        public string Name { get; }

        /// <summary>How many measured calls were made, warmup excluded.</summary>
        public int Iterations { get; }

        /// <summary>
        /// The middle measurement. The headline number rather than the mean, for the usual reason:
        /// one call that lost its slice to the host is an outlier a mean carries and a median does
        /// not.
        /// </summary>
        public double MedianMilliseconds { get; }

        /// <summary>The fastest measurement — the closest thing to an uninterrupted run.</summary>
        public double MinimumMilliseconds { get; }

        /// <summary>Every measured call together, warmup excluded.</summary>
        public double TotalMilliseconds { get; }

        /// <summary>The median expressed per call, which is the shape a comparison is usually read in.</summary>
        public double NanosecondsPerOperation => MedianMilliseconds * 1_000_000.0;

        /// <summary>When a call threw, the message that escaped it; the measurement is then meaningless.</summary>
        public string? Failure { get; }

        /// <summary>Whether every call completed, so the numbers mean something.</summary>
        public bool Measured => Failure is null;
    }

    /// <summary>
    /// Discovers and runs the <c>@Test</c>/<c>@TestSuite</c> tests in a set of loaded modules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entirely reflection-driven, the way §11.1 promises: it walks the module's classes and
    /// methods and filters on the built-in <c>Test</c> mark, so a Surtr project gains a test
    /// story without any compiler change - the mark, the image round-trip and the materialization
    /// all already exist. A suite's readable name comes from its <c>@TestSuite</c> mark when one
    /// is written, falling back to the class name; each test's comes from its <c>@Test</c> mark,
    /// falling back to the method name.
    /// </para>
    /// <para>
    /// A static test is invoked directly; an instance test runs through a freshly allocated
    /// instance, its parameterless constructor first when the class declares one, so field
    /// initializers run the way they would in a hand-driven run. Any exception escaping a test
    /// is reported as its failure.
    /// </para>
    /// <para>
    /// A test also carrying <c>@TestIgnore</c> is discovered and reported as
    /// <see cref="SurtrTestOutcome.Skipped"/>, with the reason the mark carries, and its body never
    /// runs. Skipping at report time rather than at discovery is deliberate: a skip nobody can see
    /// is indistinguishable from a deleted test.
    /// </para>
    /// <para>
    /// <c>@TestBefore</c> and <c>@TestAfter</c> are per-test fixtures, the standard default: they
    /// run around <em>each</em> test in their scope rather than once around the group. A fixture
    /// declared in a class wraps that class's own tests; one declared at module level wraps every
    /// test in the module, its classes included. Both scopes run in discovery order, module before
    /// class on the way in and class before module on the way out, and the test and its fixtures
    /// share one instance — which is what lets a <c>@TestBefore</c> set up state the test reads.
    /// An <c>@TestAfter</c> runs whatever happened, a <c>@TestBefore</c> that threw included, and
    /// the first failure is the one reported.
    /// </para>
    /// <para>
    /// Module-level functions are discovered too, not just class members: a module is §2.5's only
    /// top-level container, so a loose <c>@Test fun</c> is as ordinary a test as one inside a
    /// class. Its suite is the module's path, there being no <c>@TestSuite</c> to name it.
    /// </para>
    /// <para>
    /// <c>@Benchmark</c> is discovered by a pass of its own — <see cref="RunBenchmarks(SurtrRuntime, SurtrModule[])"/> —
    /// because how long is a different question from whether, and a host usually wants one of the
    /// two rather than both at once.
    /// </para>
    /// </remarks>
    public static class SurtrTestRunner
    {
        /// <summary>Untimed calls a benchmark makes first when the caller names no count.</summary>
        private const int DefaultWarmup = 8;

        /// <summary>Timed calls a benchmark makes when the caller names no count.</summary>
        private const int DefaultIterations = 32;

        /// <summary>Runs every <c>@Test</c> in the given modules, in discovery order.</summary>
        public static IReadOnlyList<SurtrTestResult> Run(SurtrRuntime runtime, params SurtrModule[] modules)
        {
            var results = new List<SurtrTestResult>();
            if (runtime is null)
                throw new ArgumentNullException(nameof(runtime));

            if (modules is not null)
            {
                foreach (var module in modules)
                {
                    // The module's own fixtures, collected once and handed to every test in it -
                    // including the ones inside its classes, which is what makes module scope mean
                    // "all of them" rather than "the loose ones".
                    var moduleFixtures = FixtureSet.Of(module.Methods);

                    foreach (var overloads in module.Methods)
                    {
                        for (int i = 0; i < overloads.Length; i++)
                        {
                            if (IsTest(overloads[i]))
                            {
                                results.Add(Invoke(
                                    runtime,
                                    cls: null,
                                    overloads[i],
                                    module.Path,
                                    moduleFixtures,
                                    FixtureSet.Empty));
                            }
                        }
                    }

                    foreach (var cls in module.Classes)
                        RunClass(runtime, cls, suite: null, moduleFixtures, results);
                }
            }

            return results;
        }

        private static void RunClass(
            SurtrRuntime runtime,
            SurtrClass cls,
            string? suite,
            in FixtureSet moduleFixtures,
            List<SurtrTestResult> results)
        {
            string suiteName = NameOf(cls, SurtrBuiltIns.TestSuite) ?? suite ?? cls.Name;

            // A class's own fixtures, and only its own: an outer class's instance fixture could not
            // run on a nested class's instance anyway, so declaring-class scope is the only rule
            // that holds for both kinds. The suite *name* still travels down, since that is a
            // grouping rather than something to call.
            var fixtures = FixtureSet.Of(cls.Methods);

            foreach (var overloads in cls.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                {
                    var method = overloads[i];
                    if (!IsTest(method))
                        continue;

                    results.Add(Invoke(runtime, cls, method, suiteName, moduleFixtures, fixtures));
                }
            }

            foreach (var nested in cls.NestedClasses)
                RunClass(runtime, nested, suiteName, moduleFixtures, results);
        }

        private static bool IsTest(SurtrMethodInfo method)
            => method.IsConstructor is false
                && method.Parameters.Length == 0
                && method.TryGetAttribute(SurtrBuiltIns.Test, out _);

        private static SurtrTestResult Invoke(
            SurtrRuntime runtime,
            SurtrClass? cls,
            SurtrMethodInfo method,
            string suite,
            in FixtureSet moduleFixtures,
            in FixtureSet typeFixtures)
        {
            string name = NameOf(method, SurtrBuiltIns.Test) ?? method.Name;

            // Discovered, reported, not run: the mark's whole point is that the test stays visible
            // in the report - a skip nobody can see is a deleted test - so this answers before any
            // instance is allocated rather than by filtering the method out of discovery.
            if (method.TryGetAttribute(SurtrBuiltIns.TestIgnore, out var ignored))
            {
                return new SurtrTestResult(
                    suite,
                    name,
                    SurtrTestOutcome.Skipped,
                    failure: null,
                    skipReason: ignored.Arguments.Length > 0 ? ignored.Arguments[0].Text : null);
            }

            // One instance for the whole run rather than one per call: the fixtures and the test
            // share it, which is the only thing that makes a @TestBefore able to set up state the
            // test then reads.
            SurtrInstance? instance = null;
            SurtrValue receiver = default;
            string? failure = null;

            try
            {
                if (cls is not null && NeedsInstance(method, typeFixtures))
                {
                    instance = runtime.NewInstance(cls);
                    receiver = SurtrValue.CreateReference(instance.GetSurtrReference());

                    if (ParameterlessConstructorOf(cls) is SurtrMethodInfo ctor)
                        runtime.Invoke(ctor, receiver);
                }

                RunAll(runtime, moduleFixtures.Before, instance, receiver);
                RunAll(runtime, typeFixtures.Before, instance, receiver);
                InvokeOne(runtime, method, instance, receiver);
            }
            catch (Exception exception)
            {
                failure = exception.Message;
            }

            // Whatever happened above, a @TestBefore that threw included: the guarantee is what
            // makes acquiring something in a before safe, and a fixture that only ran on the happy
            // path would be no guarantee at all. The first failure is the one reported - a release
            // that fails because the acquire did is a consequence, not the cause.
            try
            {
                RunAll(runtime, typeFixtures.After, instance, receiver);
                RunAll(runtime, moduleFixtures.After, instance, receiver);
            }
            catch (Exception exception)
            {
                failure ??= exception.Message;
            }

            return failure is null
                ? new SurtrTestResult(suite, name, SurtrTestOutcome.Passed, failure: null, skipReason: null)
                : new SurtrTestResult(suite, name, SurtrTestOutcome.Failed, failure, skipReason: null);
        }

        /// <summary>
        /// Whether this test needs an instance to run on: its own method is one, or a fixture that
        /// wraps it is - a static test beside an instance fixture still has to be given the
        /// receiver the fixture will write to.
        /// </summary>
        private static bool NeedsInstance(SurtrMethodInfo method, in FixtureSet fixtures)
            => !method.IsStatic || AnyInstance(fixtures.Before) || AnyInstance(fixtures.After);

        private static bool AnyInstance(List<SurtrMethodInfo>? fixtures)
        {
            if (fixtures is null)
                return false;

            for (int i = 0; i < fixtures.Count; i++)
            {
                if (!fixtures[i].IsStatic)
                    return true;
            }

            return false;
        }

        private static void RunAll(
            SurtrRuntime runtime,
            List<SurtrMethodInfo>? fixtures,
            SurtrInstance? instance,
            SurtrValue receiver)
        {
            if (fixtures is null)
                return;

            for (int i = 0; i < fixtures.Count; i++)
                InvokeOne(runtime, fixtures[i], instance, receiver);
        }

        /// <summary>
        /// Calls one test or fixture. An instance member with no instance to run on is skipped
        /// rather than refused: the only way to reach that is a construction that already threw,
        /// and that failure is the one worth reporting.
        /// </summary>
        private static void InvokeOne(
            SurtrRuntime runtime,
            SurtrMethodInfo method,
            SurtrInstance? instance,
            SurtrValue receiver)
        {
            if (method.IsStatic)
                runtime.Invoke(method);
            else if (instance is not null)
                runtime.Invoke(method, receiver);
        }

        private static SurtrMethodInfo? ParameterlessConstructorOf(SurtrClass cls)
        {
            foreach (var overloads in cls.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                {
                    if (overloads[i].IsConstructor && overloads[i].Parameters.Length == 0)
                        return overloads[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Runs every <c>@Benchmark</c> in the given modules, warming up and then timing, with the
        /// default counts.
        /// </summary>
        public static IReadOnlyList<SurtrBenchmarkResult> RunBenchmarks(SurtrRuntime runtime, params SurtrModule[] modules)
            => RunBenchmarks(runtime, DefaultWarmup, DefaultIterations, modules);

        /// <summary>
        /// Runs every <c>@Benchmark</c> in the given modules, in discovery order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A pass of its own rather than a branch inside <see cref="Run"/>, because the two answer
        /// different questions and a host usually wants one of them: a test suite that silently
        /// took a hundred timed calls per benchmark would be a slow test suite, and a benchmark run
        /// that also reported passes and failures would be a test run.
        /// </para>
        /// <para>
        /// An instance benchmark builds its receiver once, before the warmup, so construction and
        /// field initialization are not what gets measured. Fixtures are deliberately not run
        /// around a benchmark: inside the loop they would be measured, and outside it they would
        /// mean something different from what <c>@TestBefore</c> promises — per-benchmark setup is
        /// a concept the vocabulary does not have yet.
        /// </para>
        /// <para>
        /// The warmup exists for the same reason <c>Surtr.Bench</c>'s does: a method is promoted
        /// out of tier 0 after a few dozen calls, so the first ones measure the JIT rather than the
        /// code. The counts are the caller's, since what is enough depends entirely on how long one
        /// call takes.
        /// </para>
        /// </remarks>
        /// <param name="runtime">The runtime the modules are loaded into.</param>
        /// <param name="warmup">Untimed calls made first; may be zero.</param>
        /// <param name="iterations">Timed calls; at least one.</param>
        /// <param name="modules">The modules to walk.</param>
        public static IReadOnlyList<SurtrBenchmarkResult> RunBenchmarks(
            SurtrRuntime runtime,
            int warmup,
            int iterations,
            params SurtrModule[] modules)
        {
            if (runtime is null)
                throw new ArgumentNullException(nameof(runtime));

            if (warmup < 0)
                throw new ArgumentOutOfRangeException(nameof(warmup), "A warmup cannot be negative.");

            if (iterations < 1)
                throw new ArgumentOutOfRangeException(nameof(iterations), "A benchmark needs at least one timed call.");

            var results = new List<SurtrBenchmarkResult>();

            if (modules is not null)
            {
                foreach (var module in modules)
                {
                    foreach (var overloads in module.Methods)
                    {
                        for (int i = 0; i < overloads.Length; i++)
                        {
                            if (IsBenchmark(overloads[i]))
                                results.Add(Measure(runtime, cls: null, overloads[i], module.Path, warmup, iterations));
                        }
                    }

                    foreach (var cls in module.Classes)
                        MeasureClass(runtime, cls, suite: null, warmup, iterations, results);
                }
            }

            return results;
        }

        private static void MeasureClass(
            SurtrRuntime runtime,
            SurtrClass cls,
            string? suite,
            int warmup,
            int iterations,
            List<SurtrBenchmarkResult> results)
        {
            string suiteName = NameOf(cls, SurtrBuiltIns.TestSuite) ?? suite ?? cls.Name;

            foreach (var overloads in cls.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                {
                    if (IsBenchmark(overloads[i]))
                        results.Add(Measure(runtime, cls, overloads[i], suiteName, warmup, iterations));
                }
            }

            foreach (var nested in cls.NestedClasses)
                MeasureClass(runtime, nested, suiteName, warmup, iterations, results);
        }

        private static bool IsBenchmark(SurtrMethodInfo method)
            => method.IsConstructor is false
                && method.Parameters.Length == 0
                && method.TryGetAttribute(SurtrBuiltIns.Benchmark, out _);

        private static SurtrBenchmarkResult Measure(
            SurtrRuntime runtime,
            SurtrClass? cls,
            SurtrMethodInfo method,
            string suite,
            int warmup,
            int iterations)
        {
            var samples = new double[iterations];
            double total = 0.0;

            try
            {
                SurtrInstance? instance = null;
                SurtrValue receiver = default;

                if (cls is not null && !method.IsStatic)
                {
                    instance = runtime.NewInstance(cls);
                    receiver = SurtrValue.CreateReference(instance.GetSurtrReference());

                    if (ParameterlessConstructorOf(cls) is SurtrMethodInfo ctor)
                        runtime.Invoke(ctor, receiver);
                }

                for (int i = 0; i < warmup; i++)
                    InvokeOne(runtime, method, instance, receiver);

                var clock = new Stopwatch();

                for (int i = 0; i < iterations; i++)
                {
                    clock.Restart();
                    InvokeOne(runtime, method, instance, receiver);
                    clock.Stop();

                    double elapsed = clock.Elapsed.TotalMilliseconds;
                    samples[i] = elapsed;
                    total += elapsed;
                }
            }
            catch (Exception exception)
            {
                return new SurtrBenchmarkResult(suite, method.Name, iterations, 0.0, 0.0, 0.0, exception.Message);
            }

            Array.Sort(samples);

            return new SurtrBenchmarkResult(
                suite,
                method.Name,
                iterations,
                medianMilliseconds: samples[samples.Length / 2],
                minimumMilliseconds: samples[0],
                totalMilliseconds: total,
                failure: null);
        }

        private static string? NameOf(SurtrMemberInfo member, SurtrClass attributeClass)
            => member.TryGetAttribute(attributeClass, out var usage) && usage.Arguments.Length > 0
                ? usage.Arguments[0].Text
                : null;

        /// <summary>
        /// The <c>@TestBefore</c> and <c>@TestAfter</c> methods one scope declares, in discovery
        /// order.
        /// </summary>
        /// <remarks>
        /// Both lists stay <see langword="null"/> until something goes in one, because the case
        /// that matters is the common one: a scope declaring no fixture at all should cost a null
        /// check per test rather than two empty lists per class.
        /// </remarks>
        private readonly struct FixtureSet
        {
            private FixtureSet(List<SurtrMethodInfo>? before, List<SurtrMethodInfo>? after)
            {
                Before = before;
                After = after;
            }

            /// <summary>The scope that declares nothing — what a module-level test has for a type.</summary>
            internal static FixtureSet Empty => default;

            /// <summary>What runs before each test in this scope.</summary>
            internal List<SurtrMethodInfo>? Before { get; }

            /// <summary>What runs after each test in this scope.</summary>
            internal List<SurtrMethodInfo>? After { get; }

            /// <summary>Collects the fixtures out of one scope's method table.</summary>
            internal static FixtureSet Of(Dictionary<string, SurtrMethodInfo[]>.ValueCollection methods)
            {
                List<SurtrMethodInfo>? before = null;
                List<SurtrMethodInfo>? after = null;

                foreach (var overloads in methods)
                {
                    for (int i = 0; i < overloads.Length; i++)
                    {
                        var method = overloads[i];
                        if (method.IsConstructor || method.Parameters.Length != 0)
                            continue;

                        if (method.TryGetAttribute(SurtrBuiltIns.TestBefore, out _))
                            (before ??= new List<SurtrMethodInfo>()).Add(method);

                        if (method.TryGetAttribute(SurtrBuiltIns.TestAfter, out _))
                            (after ??= new List<SurtrMethodInfo>()).Add(method);
                    }
                }

                return new FixtureSet(before, after);
            }
        }
    }
}
