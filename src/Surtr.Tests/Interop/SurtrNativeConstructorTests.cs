#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Bytecode.Image;
using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Interop;
using Surtr.Interop.Attributes;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Linq;
using Xunit;

namespace Surtr.Tests.Interop
{
    /// <summary>
    /// The native-construction protocol: a constructor crosses the wire as an instance factory -
    /// no receiver, parameters from slot 0, the new instance written over that same slot - so
    /// Surtr source can reach host constructors with construction syntax and get the real CLR
    /// object back, not an <c>ObjNew</c> shell.
    /// </summary>
    public unsafe class SurtrNativeConstructorTests
    {
        [SurtrNativeType(Module = "host", Name = "Gauge")]
        public class GaugeHost
        {
            public static GaugeHost? Last;

            public readonly int Start;

            public GaugeHost(int start)
            {
                Start = start;
                Last = this;
            }

            public int Stepped() => Start + 1;
        }

        private static int NewGauge(SurtrCallArguments args)
        {
            var gauge = new GaugeHost(args.GetInt(0));
            var wrapped = args.Runtime.WrapNative(gauge);
            return args.Return(SurtrValue.CreateReference(wrapped.GetSurtrReference()));
        }

        private static int Stepped(SurtrCallArguments args)
        {
            var gauge = args.Runtime.Resolve<SurtrNativeObject>(args[0])!.TargetAs<GaugeHost>()!;
            return args.Return(SurtrValue.CreateInt(gauge.Stepped()));
        }

        private const string GaugeCtorLink = "host:Gauge.ctor";
        private const string GaugeSteppedLink = "host:Gauge.stepped";

        /// <summary>A module declaring <c>Gauge</c>, its instance-factory constructor and one method.</summary>
        private static SurtrModuleImage GaugeImage()
        {
            var builder = new SurtrModuleBuilder("host");
            var gauge = builder.DefineClass("Gauge");

            gauge.DeclareNativeConstructor(
                GaugeCtorLink,
                new[] { new SurtrParameterInfo("start", builder.TypeHandle(SurtrClassReference.Integer)) });
            gauge.DeclareNativeMethod(
                "stepped",
                SurtrClassReference.Integer,
                GaugeSteppedLink);

            return SurtrModuleImage.FromModule(builder.Build());
        }

        private const string DriverSource =
            "import host.*;\n"
            + "fun make(v: int): int {\n"
            + "    let g = Gauge(v);\n"
            + "    return g.stepped();\n"
            + "}\n";

        /// <summary>
        /// The whole story in order: source writes <c>Gauge(v)</c>; the emitter emits a flat call
        /// to the factory rather than <c>ObjNew</c> plus a receiver; the entry point builds the CLR
        /// object; the reference comes back as the expression's value; and the method call after it
        /// reaches that same object.
        /// </summary>
        [Fact]
        public void SourceConstructsTheRealClrObject()
        {
            var project = new SurtrProject(sourceRoot: ".");
            project.AddSourceFile("driver.surtr", "driver", DriverSource);
            project.AddReference(GaugeImage());

            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(
                !compilation.Diagnostics.HasErrors,
                "Binding reported: " + string.Join("; ", compilation.Diagnostics));

            var emitter = new ModuleEmitter(compilation, binder);
            var driverImages = emitter.EmitImages();

            using var runtime = new SurtrRuntime();
            runtime.DefineNativeBody(GaugeCtorLink, SurtrNativeEntryPoint.FromDelegate(NewGauge));
            runtime.DefineNativeBody(GaugeSteppedLink, SurtrNativeEntryPoint.FromDelegate(Stepped));
            runtime.LoadModule(GaugeImage());

            foreach (var image in driverImages)
                runtime.LoadModule(image);

            Assert.True(runtime.TryGetModule("driver", out var module));
            Assert.True(module.TryGetMethods("make", out var overloads));

            Assert.Equal(6, runtime.Invoke(overloads[0], SurtrValue.CreateInt(5)).AsInt);
            Assert.NotNull(GaugeHost.Last);
            Assert.Equal(5, GaugeHost.Last!.Start);
        }

        /// <summary>
        /// The reflection scanner exposes a real CLR constructor with the factory wire shape: its
        /// declared return names the class, which is what makes every call site count its
        /// arguments without a receiver.
        /// </summary>
        [Fact]
        public void TheScannerExposesAConstructorWithTheFactoryWireShape()
        {
            using var runtime = new SurtrRuntime();
            var descriptor = SurtrReflectionScanner.Scan(typeof(GaugeHost));
            var type = SurtrBridge.Register(runtime, descriptor);

            Assert.True(type.TryGetMethods("ctor", out var overloads));
            var ctor = overloads[0];

            Assert.True(ctor.IsConstructor);
            Assert.Equal(type.SelfReference.Descriptor, ctor.ReturnType.Reference.Descriptor);
            Assert.Equal(1, ctor.ArgumentSlotCount);

            // And it runs: invoking the entry point directly answers the wrapped instance.
            var result = runtime.Invoke(ctor, SurtrValue.CreateInt(41));
            Assert.True(result.IsReference);
            Assert.Same(GaugeHost.Last, runtime.Resolve<SurtrNativeObject>(result)!.TargetAs<GaugeHost>());
            Assert.Equal(41, GaugeHost.Last!.Start);
        }

        private struct V2Factory
        {
            public float X;
            public float Y;
        }

        [SurtrNativeType(Module = "host", Name = "V", Inline = true)]
        private struct V2
        {
            public float X;
            public float Y;

            [SurtrNativeConstructor]
            public static V2 Of(float x, float y) => new V2 { X = x, Y = y };
        }

        /// <summary>
        /// A factory marked [SurtrNativeConstructor] on an inline value type is exposed as the
        /// type's constructor: its result is the struct itself, written as the flat block that
        /// construction syntax expects to find.
        /// </summary>
        [Fact]
        public void AnInlineFactoryIsExposedAsTheTypesConstructor()
        {
            using var runtime = new SurtrRuntime();
            var type = SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(V2)));

            Assert.True(type.TryGetMethods("ctor", out var overloads));
            var ctor = overloads[0];
            Assert.True(ctor.IsConstructor);
            Assert.Equal(2, ctor.ArgumentSlotCount);
            Assert.Equal(2, ctor.ResultSlotCount);

            var results = new SurtrValue[2];
            Assert.True(runtime.TryInvoke(
                ctor,
                new[] { SurtrValue.CreateFloat(1.5f), SurtrValue.CreateFloat(2.5f) },
                results));

            Assert.Equal(1.5f, results[0].AsFloat, 6);
            Assert.Equal(2.5f, results[1].AsFloat, 6);
        }
    }
}
