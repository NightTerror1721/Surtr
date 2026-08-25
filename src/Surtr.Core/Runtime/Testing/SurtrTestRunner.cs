#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
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
                    foreach (var cls in module.Classes)
                        RunClass(runtime, cls, suite: null, results);
                }
            }

            return results;
        }

        private static void RunClass(SurtrRuntime runtime, SurtrClass cls, string? suite, List<SurtrTestResult> results)
        {
            string suiteName = NameOf(cls, SurtrBuiltIns.TestSuite) ?? suite ?? cls.Name;

            foreach (var overloads in cls.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                {
                    var method = overloads[i];
                    if (!IsTest(method))
                        continue;

                    results.Add(Invoke(runtime, cls, method, suiteName));
                }
            }

            foreach (var nested in cls.NestedClasses)
                RunClass(runtime, nested, suiteName, results);
        }

        private static bool IsTest(SurtrMethodInfo method)
            => method.IsConstructor is false
                && method.Parameters.Length == 0
                && method.TryGetAttribute(SurtrBuiltIns.Test, out _);

        private static SurtrTestResult Invoke(SurtrRuntime runtime, SurtrClass cls, SurtrMethodInfo method, string suite)
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

            try
            {
                if (method.IsStatic)
                {
                    runtime.Invoke(method);
                }
                else
                {
                    var receiver = runtime.NewInstance(cls);
                    var asValue = Surtr.Runtime.Objects.SurtrValue.CreateReference(receiver.GetSurtrReference());

                    if (ParameterlessConstructorOf(cls) is SurtrMethodInfo ctor)
                        runtime.Invoke(ctor, asValue);

                    runtime.Invoke(method, asValue);
                }

                return new SurtrTestResult(suite, name, SurtrTestOutcome.Passed, failure: null, skipReason: null);
            }
            catch (Exception exception)
            {
                return new SurtrTestResult(suite, name, SurtrTestOutcome.Failed, exception.Message, skipReason: null);
            }
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
    }
}
