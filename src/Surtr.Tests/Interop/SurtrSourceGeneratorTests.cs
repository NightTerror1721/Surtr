#nullable enable

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Surtr.Interop.SourceGenerator;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Interop;
using Surtr.Interop.Attributes;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Surtr.Tests.Interop
{
    public class SurtrSourceGeneratorTests
    {
        private const string Source = @"
using Surtr.Interop.Attributes;

namespace Sample
{
    [SurtrNativeType]
    public class Calculator
    {
        public int Add(int a, int b) => a + b;

        public int Count;

        public string Label { get; set; } = ""x"";

        public static Calculator operator +(Calculator a, Calculator b) => a;

        public static bool operator true(Calculator a) => true;

        public static bool operator false(Calculator a) => false;

        public void AddRef(ref int x) { }

        public bool TryGet(out int value) { value = 42; return true; }
    }

    [SurtrNativeType]
    public enum LogLevel
    {
        Debug,
        Info,
        Error,
    }
}
";

        private static Compilation RunGenerator(out GeneratorDriverRunResult result)
        {
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(static a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(static a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                .ToList();

            // Force-load the interop assemblies (typeof triggers load) and reference them explicitly,
            // so the generated code's `Surtr.Interop`/`Surtr.Core` names resolve even if nothing in
            // this test had touched those assemblies yet.
            references.Add(MetadataReference.CreateFromFile(typeof(Surtr.Runtime.SurtrRuntime).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(Surtr.Interop.SurtrBridge).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(Surtr.Interop.Attributes.SurtrNativeTypeAttribute).Assembly.Location));

            var syntaxTree = CSharpSyntaxTree.ParseText(Source);
            var compilation = CSharpCompilation.Create(
                "Sample",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(new SurtrSourceGenerator());
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

            result = driver.GetRunResult();
            return output;
        }

        [Fact]
        public void Generator_EmitsBindingsAndRegistration()
        {
            var compilation = RunGenerator(out var result);

            var generated = result.Results.SelectMany(static r => r.GeneratedSources).ToList();
            Assert.NotEmpty(generated);

            var names = generated.Select(static g => g.HintName).ToList();
            Assert.Contains(names, static n => n.EndsWith("Bindings.g.cs"));
            Assert.Contains(names, static n => n.Contains("Calculator"));

            var errors = compilation.GetDiagnostics()
                .Where(static d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            Assert.Empty(errors);
        }

        [Fact]
        public void Generator_AdaptsMemberNamesToSurtrConvention()
        {
            RunGenerator(out var result);

            var all = string.Join("\n", result.Results.SelectMany(static r => r.GeneratedSources).Select(static g => g.SourceText.ToString()));
            Assert.Contains("\"add\"", all);
            Assert.Contains("\"count\"", all);
            Assert.Contains("\"label\"", all);
            Assert.Contains("SurtrBindings", all);
        }

        [Fact]
        public void GeneratedBindings_RegisterAndInvoke()
        {
            // The generated catalog and descriptors are exercised through the reflection fallback's
            // equivalent path in SurtrInteropTests; here we just prove the generated code is
            // well-formed enough to compile and contains the entry-point wiring.
            RunGenerator(out var result);

            var all = string.Join("\n", result.Results.SelectMany(static r => r.GeneratedSources).Select(static g => g.SourceText.ToString()));
            Assert.Contains("SurtrNativeEntryPoint.FromFunctionPointer", all);
            Assert.Contains("SurtrBridge.Register", all);
        }

        [Fact]
        public void Generator_MapsOperatorsAndFoldsOut()
        {
            RunGenerator(out var result);

            var all = string.Join("\n", result.Results.SelectMany(static r => r.GeneratedSources).Select(static g => g.SourceText.ToString()));
            Assert.Contains("\"op_+\"", all);
            Assert.Contains("T(BI)", all); // TryGet's folded return: (bool, int)
        }

        /// <summary>
        /// The shim for a method with out-parameters writes its results as a flat block of slots.
        /// </summary>
        /// <remarks>
        /// It used to pack a <c>SurtrTuple</c> and answer one slot, which was correct until a tuple
        /// became a value type: <c>ResultSlotCount</c> is the flattened width, so the caller copies
        /// that many slots back and a lone reference in slot 0 leaves the rest of the block holding
        /// whatever the stack had there. The reflection fallback had the same defect.
        /// </remarks>
        [Fact]
        public void Generator_WritesOutParameterResultsAsAFlatBlock()
        {
            RunGenerator(out var result);

            var all = string.Join("\n", result.Results.SelectMany(static r => r.GeneratedSources).Select(static g => g.SourceText.ToString()));

            Assert.Contains("args.WriteResult(0, __slot0);", all);
            Assert.Contains("args.WriteResult(1, __slot1);", all);
            Assert.Contains("return 2;", all);

            // Nothing packs a tuple to answer an out-parameter any more.
            Assert.DoesNotContain("Runtime.NewTuple", all);
        }

        [Fact]
        public void Generator_ReportsWarningsForUnsupportedMembers()
        {
            RunGenerator(out var result);

            var diagnostics = result.Diagnostics
                .Where(static d => d.Id == "SURTRINTEROP001")
                .ToList();

            Assert.Contains(diagnostics, static d => d.GetMessage().Contains("op_True"));
            Assert.Contains(diagnostics, static d => d.GetMessage().Contains("AddRef"));
        }

        private const string InvalidSource = @"
using Surtr.Interop.Attributes;

namespace Sample
{
    [SurtrNativeType]
    public class Broken
    {
        [SurtrNativeField(TypeDescriptor = ""NOT_A_DESCRIPTOR"")]
        public int X;

        public int Bad([SurtrNativeParameter(TypeDescriptor = ""X"")] int p) => p;
    }

    [SurtrNativeType(TypeArguments = new[] { typeof(int) })]
    public class WrongArity<T, U>
    {
    }

    [SurtrNativeType]
    public static class StaticOnly
    {
        public static int Value;
    }
}
";

        [Fact]
        public void Generator_ReportsErrorsForInvalidConfigurations()
        {
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(static a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(static a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
                .ToList();
            references.Add(MetadataReference.CreateFromFile(typeof(Surtr.Runtime.SurtrRuntime).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(Surtr.Interop.SurtrBridge).Assembly.Location));
            references.Add(MetadataReference.CreateFromFile(typeof(Surtr.Interop.Attributes.SurtrNativeTypeAttribute).Assembly.Location));

            var syntaxTree = CSharpSyntaxTree.ParseText(InvalidSource);
            var compilation = CSharpCompilation.Create(
                "Sample",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(new SurtrSourceGenerator());
            var output = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
            var diagnostics = output.GetRunResult().Diagnostics.ToList();

            Assert.Contains(diagnostics, static d => d.Id == "SURTRINTEROP002"); // invalid descriptor
            Assert.Contains(diagnostics, static d => d.Id == "SURTRINTEROP003"); // arity mismatch
            Assert.Contains(diagnostics, static d => d.Id == "SURTRINTEROP004"); // static type
        }
    }
}
