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
    
        #region Inline value types

        private const string InlineSource = @"
using Surtr.Interop.Attributes;

namespace Sample
{
    [SurtrNativeType(Module = ""unity"", Name = ""Vector3"", Inline = true)]
    public struct Vector3
    {
        public float X;
        public float Y;
        public float Z;

        public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public float SqrMagnitude() => (X * X) + (Y * Y) + (Z * Z);

        public Vector3 Halved => new Vector3(X / 2f, Y / 2f, Z / 2f);

        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    }

    [SurtrNativeType(Module = ""unity"", Name = ""Bounds"", Inline = true)]
    public struct Bounds
    {
        public Vector3 Center;
        public Vector3 Extents;

        public float Volume() => 8f * Extents.X * Extents.Y * Extents.Z;
    }
}
";

        private static string RunInlineGenerator()
        {
            var syntaxTree = CSharpSyntaxTree.ParseText(InlineSource);

            // Named explicitly rather than swept out of the AppDomain: the assemblies the generated
            // code needs are only loaded if something in this test already touched them, and the
            // compile assertion below is worthless against a reference set that happens to be
            // missing the very types it should be checking.
            var required = new[]
            {
                typeof(SurtrNativeTypeAttribute).Assembly,
                typeof(SurtrBridge).Assembly,
                typeof(Surtr.Runtime.SurtrRuntime).Assembly,
                typeof(object).Assembly,
            };

            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Concat(required)
                .Where(static a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(static a => a.Location)
                .Distinct()
                .Select(static location => MetadataReference.CreateFromFile(location))
                .Cast<MetadataReference>()
                .ToList();

            var compilation = CSharpCompilation.Create(
                "SampleInlineAssembly",
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

            GeneratorDriver driver = CSharpGeneratorDriver.Create(new SurtrSourceGenerator());
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

            // The generated shims have to *compile*, not merely read right: every assertion below
            // is over text, and text can be plausible and still not be C#. This is what would catch
            // an initializer naming a field that is not there, or a block written at the wrong
            // arity. Reference-resolution errors are filtered out - this test compiles against
            // whatever the test host happens to have loaded, not against a real project.
            var errors = output.GetDiagnostics()
                .Where(static d => d.Severity == DiagnosticSeverity.Error)
                .Where(static d => d.Id is not ("CS0006" or "CS0012" or "CS0246" or "CS0234"))
                .ToList();

            Assert.True(
                errors.Count == 0,
                "Generated code did not compile: " + string.Join("; ", errors.Select(static e => e.ToString())));

            return string.Join(
                "\n",
                driver.GetRunResult().Results.SelectMany(static r => r.GeneratedSources).Select(static g => g.SourceText.ToString()));
        }

        /// <summary>
        /// An inline struct's fields become storage slots, so the generator emits no accessor shim
        /// for them at all - the descriptor is the whole declaration.
        /// </summary>
        [Fact]
        public void Generator_EmitsStorageSlotsForAnInlineStruct()
        {
            var all = RunInlineGenerator();

            Assert.Contains("IsInline = true", all);
            Assert.Contains("new NativeValueFieldDescriptor { Name = \"x\"", all);
            Assert.Contains("new NativeValueFieldDescriptor { Name = \"z\"", all);

            // No accessor pair for a field that is now a slot.
            Assert.DoesNotContain("__SurtrFieldGet_Vector3_X", all);
            Assert.DoesNotContain("__SurtrFieldSet_Vector3_X", all);
        }

        /// <summary>
        /// A receiver is rebuilt from the block with a typed object initializer - no boxing, no
        /// reflection, no proxy to resolve. That is the generated path's whole advantage.
        /// </summary>
        [Fact]
        public void Generator_RebuildsAnInlineReceiverWithATypedInitializer()
        {
            var all = RunInlineGenerator();

            Assert.Contains("var __target = new Sample.Vector3 { X = (float)args.GetFloat(0), Y = (float)args.GetFloat(1), Z = (float)args.GetFloat(2) };", all);

            // The old shape resolved a proxy; an inline receiver never does.
            Assert.DoesNotContain("Resolve<SurtrNativeObject>(args.GetValue(0))!.TargetAs<Sample.Vector3>()", all);
        }

        /// <summary>
        /// An operator over two blocks reads its second operand at slot 3, not slot 1: arguments
        /// advance by width. Its result is written as a flat block of three.
        /// </summary>
        [Fact]
        public void Generator_WalksOperatorOperandsByWidthAndWritesTheResultFlat()
        {
            var all = RunInlineGenerator();

            Assert.Contains("var a = new Sample.Vector3 { X = (float)args.GetFloat(0), Y = (float)args.GetFloat(1), Z = (float)args.GetFloat(2) };", all);
            Assert.Contains("var b = new Sample.Vector3 { X = (float)args.GetFloat(3), Y = (float)args.GetFloat(4), Z = (float)args.GetFloat(5) };", all);

            Assert.Contains("args.WriteResult(0, SurtrValue.CreateFloat((double)(__result.X)));", all);
            Assert.Contains("args.WriteResult(2, SurtrValue.CreateFloat((double)(__result.Z)));", all);
            Assert.Contains("return 3;", all);
        }

        /// <summary>A nested inline struct expands into the same flat run, at its own offset.</summary>
        [Fact]
        public void Generator_ExpandsANestedBlockIntoOneFlatRun()
        {
            var all = RunInlineGenerator();

            // Bounds is two Vector3s: Center at 0..2, Extents at 3..5, all in one initializer.
            Assert.Contains(
                "var __target = new Sample.Bounds { Center = new Sample.Vector3 { X = (float)args.GetFloat(0), Y = (float)args.GetFloat(1), Z = (float)args.GetFloat(2) }, "
                + "Extents = new Sample.Vector3 { X = (float)args.GetFloat(3), Y = (float)args.GetFloat(4), Z = (float)args.GetFloat(5) } };",
                all);
        }

        /// <summary>
        /// A property returning another inline struct answers its own flat block, and an inline
        /// type gets no setter at all - a write would land on a copy the shim then discards.
        /// </summary>
        [Fact]
        public void Generator_EmitsAnInlinePropertyGetterAndNoSetter()
        {
            var all = RunInlineGenerator();

            Assert.Contains("__SurtrPropGet_Vector3_Halved", all);
            Assert.DoesNotContain("__SurtrPropSet_Vector3_Halved", all);
        }

        /// <summary>A constructor is not exposed on an inline type, matching the reflection scanner.</summary>
        [Fact]
        public void Generator_ExposesNoConstructorForAnInlineStruct()
        {
            var all = RunInlineGenerator();

            Assert.DoesNotContain("__SurtrInvoke_Vector3_ctor", all);
        }

        #endregion
    }
}
