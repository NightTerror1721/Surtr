#nullable enable

using Surtr.Interop;
using Surtr.Interop.Attributes;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using Xunit;

namespace Surtr.Tests.Interop
{
    /// <summary>
    /// A host class registered as a native type that derives from <see cref="SurtrNativeObject"/>
    /// is already a Surtr entity: crossing it into the runtime adopts the object itself rather
    /// than wrapping it in a <see cref="SurtrNativeProxy"/>, and reading it back never digs for a
    /// proxy's target. Every crossing answers the same reference, the way an enum's cached cases
    /// do.
    /// </summary>
    public class SurtrNativeObjectAdoptionTests
    {
        [SurtrNativeType(Module = "host", Name = "Gauge")]
        public class GaugeFacade : SurtrNativeObject
        {
            public GaugeFacade(int seed)
                // A facade the host builds has no runtime to ask for its materialized class, so
                // it carries the one native class that always exists - exactly what a subclass
                // holding its own state instead of a wrapped target is for.
                : base(SurtrBuiltIns.NativeObject, null)
            {
                Seed = seed;
            }

            public int Seed { get; }

            public int Twice() => Seed * 2;

            public GaugeFacade Restyled() => new GaugeFacade(Seed + 100);
        }

        [SurtrNativeType(Module = "host", Name = "Plain")]
        public class PlainHost
        {
            public int Value() => 1;
        }

        private static readonly SurtrClassReference GaugeDescriptor = SurtrClassReference.Native("host:Gauge");

        [Fact]
        public void ConstructingARegisteredFacadeAnswersTheObjectItself()
        {
            using var runtime = new SurtrRuntime();
            var type = SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(GaugeFacade)));

            Assert.True(type.TryGetMethods("ctor", out var constructors));
            var built = runtime.Invoke(constructors[0], SurtrValue.CreateInt(21));

            var entity = runtime.Resolve<SurtrNativeObject>(built);
            var facade = Assert.IsType<GaugeFacade>(entity);
            Assert.IsNotType<SurtrNativeProxy>(entity);
            Assert.Equal(21, facade.Seed);
        }

        [Fact]
        public void CallingAMethodOnAnAdoptedFacadeReachesTheFacadeItself()
        {
            using var runtime = new SurtrRuntime();
            var type = SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(GaugeFacade)));

            Assert.True(type.TryGetMethods("ctor", out var constructors));
            var built = runtime.Invoke(constructors[0], SurtrValue.CreateInt(21));

            Assert.True(type.TryGetMethods("twice", out var twice));
            Assert.Equal(42, runtime.Invoke(twice[0], built).AsInt);
        }

        [Fact]
        public void AnAdoptedFacadeKeepsOneIdentityAcrossEveryCrossing()
        {
            using var runtime = new SurtrRuntime();
            var type = SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(GaugeFacade)));
            var facade = new GaugeFacade(7);

            var first = runtime.RegisterHost(facade);
            var second = SurtrMarshaler.ToSurtr(runtime, facade, GaugeDescriptor);

            Assert.True(first.IsReference);
            Assert.Equal(first.AsReference, second.AsReference);

            // And reading it back hands over the object itself, not a shell around it.
            var roundTripped = SurtrMarshaler.ToClr(runtime, first, typeof(GaugeFacade), GaugeDescriptor);
            Assert.Same(facade, roundTripped);
        }

        [Fact]
        public void AnObjectTypedReadOfAnAdoptedFacadeAnswersTheFacade()
        {
            using var runtime = new SurtrRuntime();
            SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(GaugeFacade)));

            var facade = new GaugeFacade(9);
            var value = runtime.RegisterHost(facade);

            Assert.Same(facade, runtime.HostValueOf(value));
            Assert.Same(facade, SurtrMarshaler.ToClr(runtime, value, typeof(object), GaugeDescriptor));
        }

        [Fact]
        public void APlainClassStillCrossesBehindAProxy()
        {
            using var runtime = new SurtrRuntime();
            SurtrBridge.Register(runtime, SurtrReflectionScanner.Scan(typeof(PlainHost)));

            var plain = new PlainHost();
            var value = runtime.RegisterHost(plain);

            var entity = runtime.Resolve<SurtrNativeObject>(value);
            var proxy = Assert.IsType<SurtrNativeProxy>(entity);
            Assert.Same(plain, proxy.Target);
        }

        [Fact]
        public void RegisteringNullAnswersNull()
        {
            using var runtime = new SurtrRuntime();
            Assert.True(runtime.RegisterHost(null).IsNullReference);
            Assert.Null(runtime.HostValueOf(SurtrValue.Null));
        }
    }
}
