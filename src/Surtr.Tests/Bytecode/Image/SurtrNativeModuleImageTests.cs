#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Bytecode.Image;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.Bytecode.Image
{
    /// <summary>
    /// Covers modules that carry native members: the ones a host writes outright, and the hybrids
    /// that mix compiled Surtr bodies with host ones.
    /// </summary>
    /// <remarks>
    /// The thing being pinned down throughout is that a native body travels as a <em>name</em> and
    /// never as an address. An image written in one process is read in another, where the address
    /// means nothing and the name means the same thing — so the declaration says what the member
    /// looks like and what it is called, and each runtime publishes its own body under that name.
    /// </remarks>
    public unsafe class SurtrNativeModuleImageTests
    {
        #region Host entry points

        private static SurtrValue Answer(SurtrCallArguments arguments) => SurtrValue.CreateInt(42);

        private static SurtrValue Rejected(SurtrCallArguments arguments) => SurtrValue.CreateInt(-1);

        private static SurtrValue Doubled(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetInt(0) * 2);

        private static SurtrValue Tripled(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetInt(0) * 3);

        /// <summary>Reads slot 0 of the receiver, for the native-property cases.</summary>
        private static SurtrValue ReadSlot(SurtrCallArguments arguments)
            => arguments.GetUnchecked<SurtrInstance>(0)[0];

        /// <summary>Writes slot 0 of the receiver.</summary>
        private static SurtrValue WriteSlot(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrInstance>(0)[0] = arguments.GetValueUnchecked(1);
            return SurtrValue.Null;
        }

        private static SurtrValue Construct(SurtrCallArguments arguments)
        {
            arguments.GetUnchecked<SurtrInstance>(0)[0] = arguments.GetValueUnchecked(1);
            return SurtrValue.Null;
        }

        private static SurtrNativeEntryPoint Entry(delegate*<SurtrCallArguments, SurtrValue> body)
            => SurtrNativeEntryPoint.FromFunctionPointer(body);

        #endregion

        #region A module written entirely by the host

        private const string AnswerLink = "host:Facade.answer()";

        /// <summary>A module with nothing but a native member, declared by name.</summary>
        private static SurtrModuleImage HostOnlyImage()
        {
            var builder = new SurtrModuleBuilder("host");

            var facade = builder.DefineClass("Facade");
            facade.DeclareNativeMethod("answer", SurtrClassReference.Integer, AnswerLink, isStatic: true);

            return SurtrModuleImage.FromModule(builder.Build());
        }

        [Fact]
        public void AHostOnlyModule_RoundTripsAndRuns()
        {
            var image = HostOnlyImage();

            using var runtime = new SurtrRuntime();
            runtime.DefineNativeBody(AnswerLink, Entry(&Answer));

            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetClass("Facade", out var facade));
            Assert.True(facade.TryGetMethods("answer", out var overloads));
            Assert.Equal(42, runtime.Invoke(overloads[0]).AsInt);
        }

        /// <summary>
        /// The point of the whole exercise: one image, several runtimes, each publishing its own
        /// body. The bodies need not even be the same function.
        /// </summary>
        [Fact]
        public void EachRuntime_SuppliesItsOwnBody()
        {
            var image = HostOnlyImage();

            using var first = new SurtrRuntime();
            using var second = new SurtrRuntime();

            first.DefineNativeBody(AnswerLink, Entry(&Answer));
            second.DefineNativeBody(AnswerLink, Entry(&Rejected));

            var inFirst = first.LoadModule(image);
            var inSecond = second.LoadModule(image);

            Assert.True(inFirst.TryGetClass("Facade", out var firstFacade));
            Assert.True(inSecond.TryGetClass("Facade", out var secondFacade));

            Assert.True(firstFacade.TryGetMethods("answer", out var firstAnswer));
            Assert.True(secondFacade.TryGetMethods("answer", out var secondAnswer));

            Assert.Equal(42, first.Invoke(firstAnswer[0]).AsInt);
            Assert.Equal(-1, second.Invoke(secondAnswer[0]).AsInt);
        }

        /// <summary>A body that was never published fails the load, naming what to publish.</summary>
        [Fact]
        public void AnUnpublishedBody_FailsAtLoad()
        {
            var image = HostOnlyImage();

            using var runtime = new SurtrRuntime();

            var failure = Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(image));

            Assert.Contains("answer", failure.Message);
            Assert.Contains(AnswerLink, failure.Message);
            Assert.Contains("DefineNativeBody", failure.Message);
        }

        /// <summary>Publishing again replaces the body, so reloading after a change is not an error.</summary>
        [Fact]
        public void PublishingTwice_ReplacesTheBody()
        {
            using var runtime = new SurtrRuntime();

            runtime.DefineNativeBody(AnswerLink, Entry(&Rejected));
            runtime.DefineNativeBody(AnswerLink, Entry(&Answer));

            var module = runtime.LoadModule(HostOnlyImage());

            Assert.True(module.TryGetClass("Facade", out var facade));
            Assert.True(facade.TryGetMethods("answer", out var overloads));
            Assert.Equal(42, runtime.Invoke(overloads[0]).AsInt);
        }

        #endregion

        #region Hybrid modules

        private const string DoubleLink = "app:Maths.double(I)";

        /// <summary>
        /// One module, two halves: a bytecode function that calls a native method declared beside
        /// it. This is the shape Language-Syntax.md §13.1 describes for the standard library.
        /// </summary>
        private static SurtrModuleImage HybridImage()
        {
            var builder = new SurtrModuleBuilder("app");

            var maths = builder.DefineClass("Maths");
            var doubling = maths.DeclareNativeMethod(
                "double",
                SurtrClassReference.Integer,
                DoubleLink,
                new[] { builder.Parameter("value", SurtrClassReference.Integer) },
                isStatic: true);

            // Compiled Surtr calling into the host half of its own module.
            var quadruple = builder.DefineFunction(
                "quadruple",
                SurtrClassReference.Integer,
                new[] { builder.Parameter("value", SurtrClassReference.Integer) });

            quadruple.Code
                .LoadLocal(quadruple.Parameter(0))
                .Call(doubling)
                .Call(doubling)
                .ReturnValue();

            return SurtrModuleImage.FromModule(builder.Build());
        }

        [Fact]
        public void AHybridModule_RoundTripsAndItsHalvesReachEachOther()
        {
            var image = HybridImage();

            using var runtime = new SurtrRuntime();
            runtime.DefineNativeBody(DoubleLink, Entry(&Doubled));

            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetMethods("quadruple", out var overloads));
            Assert.Equal(40, runtime.Invoke(overloads[0], SurtrValue.CreateInt(10)).AsInt);
        }

        /// <summary>
        /// The bytecode half is fixed by the image; the host half is whatever the runtime
        /// published. Swapping the body changes the answer without recompiling anything.
        /// </summary>
        [Fact]
        public void AHybridModule_TakesItsNativeHalfFromTheRuntime()
        {
            var image = HybridImage();

            using var doubling = new SurtrRuntime();
            using var tripling = new SurtrRuntime();

            doubling.DefineNativeBody(DoubleLink, Entry(&Doubled));
            tripling.DefineNativeBody(DoubleLink, Entry(&Tripled));

            var inDoubling = doubling.LoadModule(image);
            var inTripling = tripling.LoadModule(image);

            Assert.True(inDoubling.TryGetMethods("quadruple", out var first));
            Assert.True(inTripling.TryGetMethods("quadruple", out var second));

            Assert.Equal(40, doubling.Invoke(first[0], SurtrValue.CreateInt(10)).AsInt);
            Assert.Equal(90, tripling.Invoke(second[0], SurtrValue.CreateInt(10)).AsInt);
        }

        #endregion

        #region Native properties and constructors

        private const string GetterLink = "props:Box.get_value()";
        private const string SetterLink = "props:Box.set_value(I)";
        private const string CtorLink = "props:Box.ctor(I)";

        /// <summary>A class whose constructor and whole property are host code.</summary>
        private static SurtrModuleImage NativePropertyImage()
        {
            var builder = new SurtrModuleBuilder("props");

            var box = builder.DefineClass("Box");
            box.DefineField("_value", SurtrClassReference.Integer);

            box.DeclareNativeConstructor(
                CtorLink,
                new[] { builder.Parameter("value", SurtrClassReference.Integer) });

            box.DefineProperty("value", SurtrClassReference.Integer)
                .DeclareNativeGetter(GetterLink)
                .DeclareNativeSetter(SetterLink);

            return SurtrModuleImage.FromModule(builder.Build());
        }

        private static void PublishBoxBodies(SurtrRuntime runtime)
        {
            runtime.DefineNativeBody(CtorLink, Entry(&Construct));
            runtime.DefineNativeBody(GetterLink, Entry(&ReadSlot));
            runtime.DefineNativeBody(SetterLink, Entry(&WriteSlot));
        }

        [Fact]
        public void ANativePropertySurvivesTheRoundTrip()
        {
            var image = NativePropertyImage();

            using var runtime = new SurtrRuntime();
            PublishBoxBodies(runtime);

            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetClass("Box", out var box));
            Assert.True(box.TryGetProperty("value", out var property));

            Assert.NotNull(property.Getter);
            Assert.NotNull(property.Setter);
            Assert.Equal(SurtrMethodImplKind.Native, property.Getter!.ImplKind);
            Assert.Equal(SurtrMethodImplKind.Native, property.Setter!.ImplKind);
        }

        [Fact]
        public void ANativePropertyReadsAndWritesThroughItsHostBodies()
        {
            var image = NativePropertyImage();

            using var runtime = new SurtrRuntime();
            PublishBoxBodies(runtime);

            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetClass("Box", out var box));
            Assert.True(box.TryGetMethods("ctor", out var constructors));
            Assert.True(box.TryGetProperty("value", out var property));

            var instance = runtime.NewInstance(box);
            SurtrValue receiver = runtime.ValueOf(instance);

            runtime.Invoke(constructors[0], receiver, SurtrValue.CreateInt(7));
            Assert.Equal(7, runtime.Invoke(property.Getter!, receiver).AsInt);

            runtime.Invoke(property.Setter!, receiver, SurtrValue.CreateInt(9));
            Assert.Equal(9, runtime.Invoke(property.Getter!, receiver).AsInt);
        }

        private const string ModuleGetterLink = "globals:get_value()";
        private const string ModuleSetterLink = "globals:set_value(I)";

        private static int _moduleValue;
        private static SurtrValue ReadModuleValue(SurtrCallArguments arguments) => SurtrValue.CreateInt(_moduleValue);
        private static SurtrValue WriteModuleValue(SurtrCallArguments arguments)
        {
            _moduleValue = arguments.GetInt(0);
            return SurtrValue.Null;
        }

        /// <summary>A module-level property whose whole accessor pair is host code — no receiver.</summary>
        private static SurtrModuleImage ModuleLevelNativePropertyImage()
        {
            var builder = new SurtrModuleBuilder("globals");

            builder.DefineProperty("value", SurtrClassReference.Integer)
                .DeclareNativeGetter(ModuleGetterLink)
                .DeclareNativeSetter(ModuleSetterLink);

            return SurtrModuleImage.FromModule(builder.Build());
        }

        /// <summary>
        /// A module-level `native` property has no receiver, but it is still an ordinary member
        /// (§10): the compiler already relies on exactly this for `native let`/`native var` at
        /// module scope, so the builder has to accept it rather than reject it as a leftover
        /// host-global concept.
        /// </summary>
        [Fact]
        public void AModuleLevelNativeAccessor_RoundTripsAndRuns()
        {
            var image = ModuleLevelNativePropertyImage();

            using var runtime = new SurtrRuntime();
            _moduleValue = 5;
            runtime.DefineNativeBody(ModuleGetterLink, Entry(&ReadModuleValue));
            runtime.DefineNativeBody(ModuleSetterLink, Entry(&WriteModuleValue));

            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetMethods("get_value", out var getters));
            Assert.True(module.TryGetMethods("set_value", out var setters));

            Assert.Equal(5, runtime.Invoke(getters[0]).AsInt);

            runtime.Invoke(setters[0], SurtrValue.CreateInt(9));
            Assert.Equal(9, _moduleValue);
        }

        #endregion

        #region Link names

        /// <summary>
        /// A link name is derived from the owner and the signature when the declaration does not
        /// give one, so a host that never ships an image pays nothing for it.
        /// </summary>
        [Fact]
        public void ALinkNameIsDerivedWhenNotDeclared()
        {
            var builder = new SurtrModuleBuilder("derived");

            var facade = builder.DefineClass("Facade");
            var method = facade.DefineNativeMethod(
                "answer",
                SurtrClassReference.Integer,
                Entry(&Answer),
                new[] { builder.Parameter("value", SurtrClassReference.Integer) },
                isStatic: true);

            Assert.False(method.HasDeclaredLinkName);
            Assert.Equal("derived:Facade.answer(I)", method.LinkName);
        }

        [Fact]
        public void ADeclaredLinkNameIsKept()
        {
            var builder = new SurtrModuleBuilder("named");

            var facade = builder.DefineClass("Facade");
            var method = facade.DefineNativeMethod(
                "answer",
                SurtrClassReference.Integer,
                Entry(&Answer),
                isStatic: true,
                linkName: "my.custom.name");

            Assert.True(method.HasDeclaredLinkName);
            Assert.Equal("my.custom.name", method.LinkName);
        }

        /// <summary>
        /// Overloads get distinct derived names, because the signature is part of what names a
        /// member — the same reason it keys the access tables.
        /// </summary>
        [Fact]
        public void OverloadsDeriveDistinctLinkNames()
        {
            var builder = new SurtrModuleBuilder("over");

            var facade = builder.DefineClass("Facade");

            var withInt = facade.DefineNativeMethod(
                "read", SurtrClassReference.Integer, Entry(&Answer),
                new[] { builder.Parameter("value", SurtrClassReference.Integer) }, isStatic: true);

            var withString = facade.DefineNativeMethod(
                "read", SurtrClassReference.Integer, Entry(&Answer),
                new[] { builder.Parameter("value", SurtrClassReference.String) }, isStatic: true);

            Assert.NotEqual(withInt.LinkName, withString.LinkName);
            Assert.Equal("over:Facade.read(I)", withInt.LinkName);
            Assert.Equal("over:Facade.read(S)", withString.LinkName);
        }

        /// <summary>A method built with an address is already bound, so nothing is asked of the runtime.</summary>
        [Fact]
        public void AnAlreadyLinkedMethodNeedsNoRegistration()
        {
            var builder = new SurtrModuleBuilder("linked");

            var facade = builder.DefineClass("Facade");
            var method = facade.DefineNativeMethod(
                "answer", SurtrClassReference.Integer, Entry(&Answer), isStatic: true);

            Assert.True(method.IsBound);

            using var runtime = new SurtrRuntime();
            var module = builder.Build();
            runtime.LoadModule(module);

            Assert.True(module.TryGetClass("Facade", out var facade2));
            Assert.True(facade2.TryGetMethods("answer", out var overloads));
            Assert.Equal(42, runtime.Invoke(overloads[0]).AsInt);
        }

        /// <summary>
        /// A module the host linked directly still writes an image, using the derived names — so
        /// "I built this with pointers" and "I want to ship it" are not exclusive.
        /// </summary>
        [Fact]
        public void ALocallyLinkedModuleStillWritesAnImage()
        {
            var builder = new SurtrModuleBuilder("linked");

            var facade = builder.DefineClass("Facade");
            var method = facade.DefineNativeMethod(
                "answer", SurtrClassReference.Integer, Entry(&Answer), isStatic: true);

            string linkName = method.LinkName;
            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            runtime.DefineNativeBody(linkName, Entry(&Answer));

            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetClass("Facade", out var rebuilt));
            Assert.True(rebuilt.TryGetMethods("answer", out var overloads));
            Assert.Equal(42, runtime.Invoke(overloads[0]).AsInt);
        }

        #endregion

        #region Shape

        /// <summary>A native member on a nested class is bound too — binding walks the whole tree.</summary>
        [Fact]
        public void ANestedClassesNativeMemberIsBound()
        {
            const string link = "nest:Outer.Inner.answer()";

            var builder = new SurtrModuleBuilder("nest");

            var outer = builder.DefineClass("Outer");
            outer.DefineNestedClass("Inner")
                .DeclareNativeMethod("answer", SurtrClassReference.Integer, link, isStatic: true);

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            runtime.DefineNativeBody(link, Entry(&Answer));

            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetClass("Outer", out var rebuiltOuter));
            Assert.True(rebuiltOuter.TryGetNestedClass("Inner", out var inner));
            Assert.True(inner.TryGetMethods("answer", out var overloads));
            Assert.Equal(42, runtime.Invoke(overloads[0]).AsInt);
        }

        /// <summary>A native member can be virtual, and an interface routed through it dispatches.</summary>
        [Fact]
        public void ANativeMemberCanSatisfyAnInterface()
        {
            const string link = "iface:Impl.describe()";

            var builder = new SurtrModuleBuilder("iface");

            var contract = builder.DefineInterface("IThing");
            contract.DefineMethod("describe", SurtrClassReference.Integer);

            var impl = builder.DefineClass("Impl");
            impl.Implements(contract.SelfReference);
            impl.DeclareNativeMethod(
                "describe", SurtrClassReference.Integer, link, dispatch: SurtrMethodDispatch.Virtual);

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            runtime.DefineNativeBody(link, Entry(&Answer));

            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetClass("Impl", out var rebuilt));
            Assert.True(module.TryGetInterface("IThing", out var rebuiltContract));
            Assert.True(rebuilt.Implements(rebuiltContract));

            var instance = runtime.NewInstance(rebuilt);
            Assert.True(rebuilt.TryGetMethods("describe", out var overloads));
            Assert.Equal(42, runtime.Invoke(overloads[0], runtime.ValueOf(instance)).AsInt);
        }

        /// <summary>Writing an image of a module read from one produces the same bytes, natives included.</summary>
        [Fact]
        public void WritingANativeModuleIsStable()
        {
            var first = HybridImage();
            var second = SurtrModuleImage.FromModule(first.Instantiate());

            Assert.Equal(first.ToBytes(), second.ToBytes());
        }

        [Fact]
        public void ABodyCannotBeBoundTwice()
        {
            var builder = new SurtrModuleBuilder("twice");

            var facade = builder.DefineClass("Facade");
            var method = facade.DefineNativeMethod(
                "answer", SurtrClassReference.Integer, Entry(&Answer), isStatic: true);

            Assert.Throws<InvalidOperationException>(() => method.BindEntryPoint(Entry(&Rejected)));
        }

        [Fact]
        public void PublishingANullBodyIsRejected()
        {
            using var runtime = new SurtrRuntime();

            Assert.Throws<ArgumentException>(() => runtime.DefineNativeBody("x", default));
            Assert.Throws<ArgumentException>(() => runtime.DefineNativeBody("", Entry(&Answer)));
        }

        /// <summary>
        /// A closure copies the address out flat, so a body bound later would never be seen — an
        /// unbound method is caught where the closure is made rather than where it is called.
        /// </summary>
        [Fact]
        public void AClosureOverAnUnboundMethodIsRejected()
        {
            var builder = new SurtrModuleBuilder("unbound");

            var facade = builder.DefineClass("Facade");
            var method = facade.DeclareNativeMethod(
                "answer", SurtrClassReference.Integer, "unbound:Facade.answer()", isStatic: true);

            builder.Build();

            using var runtime = new SurtrRuntime();

            var failure = Assert.Throws<ArgumentException>(() => runtime.NewClosure(method));
            Assert.Contains("unbound:Facade.answer()", failure.Message);
        }

        /// <summary>
        /// Late binding is not an image-only feature: a host that wants to declare the shape now
        /// and supply the body per runtime can do that without serializing anything.
        /// </summary>
        [Fact]
        public void AModuleDeclaredByNameBindsWithoutAnImage()
        {
            const string link = "direct:Facade.answer()";

            static SurtrModule Build()
            {
                var builder = new SurtrModuleBuilder("direct");
                builder.DefineClass("Facade")
                    .DeclareNativeMethod("answer", SurtrClassReference.Integer, link, isStatic: true);

                return builder.Build();
            }

            using var runtime = new SurtrRuntime();
            runtime.DefineNativeBody(link, Entry(&Answer));

            var module = Build();
            runtime.LoadModule(module);

            Assert.True(module.TryGetClass("Facade", out var facade));
            Assert.True(facade.TryGetMethods("answer", out var overloads));
            Assert.Equal(42, runtime.Invoke(overloads[0]).AsInt);
        }

        #endregion
    }
}
