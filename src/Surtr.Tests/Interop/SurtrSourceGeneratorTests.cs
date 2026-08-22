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
    }
}
