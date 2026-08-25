#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;

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
    /// </remarks>
    public static class SurtrTestRunner
    {
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
