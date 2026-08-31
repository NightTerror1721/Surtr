#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Bytecode.Image;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.IO;

namespace Surtr.Tests.Bytecode.Image
{
    /// <summary>
    /// Covers the module image: that a compiled module survives a round trip through bytes, and
    /// that the same bytes can be loaded into as many runtimes as you like.
    /// </summary>
    public class SurtrModuleImageTests
    {
        /// <summary>A module with one function returning 41 + 1, for the cases that only need something to run.</summary>
        private static SurtrModule AddingModule(string path = "app")
        {
            var builder = new SurtrModuleBuilder(path);

            var answer = builder.DefineFunction("answer", SurtrClassReference.Integer);
            answer.Code.LoadInt(41).LoadInt(1).Add().ReturnValue();

            return builder.Build();
        }

        #region Round trip

        [Fact]
        public void AnImage_CarriesItsModulesPath()
        {
            var image = SurtrModuleImage.FromModule(AddingModule("game.core"));

            Assert.Equal("game.core", image.Path);
            Assert.Equal("game.core", SurtrModuleImage.FromBytes(image.ToBytes()).Path);
        }

        [Fact]
        public void ARoundTrippedModule_StillRuns()
        {
            var image = SurtrModuleImage.FromModule(AddingModule());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetMethods("answer", out var overloads));
            Assert.Equal(42, runtime.Invoke(overloads[0]).AsInt);
        }

        [Fact]
        public void BytesSurviveAStream()
        {
            var image = SurtrModuleImage.FromModule(AddingModule());

            using var buffer = new MemoryStream();
            image.WriteTo(buffer);
            buffer.Position = 0;

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(SurtrModuleImage.FromStream(buffer));

            Assert.True(module.TryGetMethods("answer", out var overloads));
            Assert.Equal(42, runtime.Invoke(overloads[0]).AsInt);
        }

        /// <summary>Writing an image of a module read from an image produces the same bytes.</summary>
        [Fact]
        public void WritingIsStable()
        {
            var first = SurtrModuleImage.FromModule(AddingModule());
            var second = SurtrModuleImage.FromModule(first.Instantiate());

            Assert.Equal(first.ToBytes(), second.ToBytes());
        }

        #endregion

        #region Many runtimes

        /// <summary>
        /// The point of the whole exercise: one compiled module, any number of runtimes, each
        /// instantiating its own because loading is what ties a module to a heap.
        /// </summary>
        [Fact]
        public void OneImage_LoadsIntoSeveralRuntimes()
        {
            var image = SurtrModuleImage.FromModule(AddingModule());

            using var first = new SurtrRuntime();
            using var second = new SurtrRuntime();

            var inFirst = first.LoadModule(image);
            var inSecond = second.LoadModule(image);

            Assert.NotSame(inFirst, inSecond);

            Assert.True(inFirst.TryGetMethods("answer", out var firstAnswer));
            Assert.True(inSecond.TryGetMethods("answer", out var secondAnswer));

            Assert.Equal(42, first.Invoke(firstAnswer[0]).AsInt);
            Assert.Equal(42, second.Invoke(secondAnswer[0]).AsInt);
        }

        /// <summary>
        /// Each runtime gets its own static storage, which is exactly what sharing one
        /// <c>SurtrModule</c> could not have given them.
        /// </summary>
        [Fact]
        public void EachRuntime_GetsItsOwnStatics()
        {
            var builder = new SurtrModuleBuilder("counters");

            var count = builder.DefineVariable("count", SurtrClassReference.Integer);

            var bump = builder.DefineFunction("bump", SurtrClassReference.Integer);
            bump.Code
                .LoadStaticField(count)
                .LoadInt(1)
                .Add()
                .Dup()
                .StoreStaticField(count)
                .ReturnValue();

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var first = new SurtrRuntime();
            using var second = new SurtrRuntime();

            var inFirst = first.LoadModule(image);
            var inSecond = second.LoadModule(image);

            Assert.True(inFirst.TryGetMethods("bump", out var firstBump));
            Assert.True(inSecond.TryGetMethods("bump", out var secondBump));

            Assert.Equal(1, first.Invoke(firstBump[0]).AsInt);
            Assert.Equal(2, first.Invoke(firstBump[0]).AsInt);

            // The second runtime starts from zero, not from where the first left off.
            Assert.Equal(1, second.Invoke(secondBump[0]).AsInt);
        }

        /// <summary>A string literal is interned into each runtime's own heap.</summary>
        [Fact]
        public void EachRuntime_InternsItsOwnLiterals()
        {
            var builder = new SurtrModuleBuilder("greeting");

            var hello = builder.DefineFunction("hello", SurtrClassReference.String);
            hello.Code.LoadString("hei").ReturnValue();

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var first = new SurtrRuntime();
            using var second = new SurtrRuntime();

            var inFirst = first.LoadModule(image);
            var inSecond = second.LoadModule(image);

            Assert.True(inFirst.TryGetMethods("hello", out var firstHello));
            Assert.True(inSecond.TryGetMethods("hello", out var secondHello));

            var fromFirst = first.Resolve<SurtrString>(first.Invoke(firstHello[0]));
            var fromSecond = second.Resolve<SurtrString>(second.Invoke(secondHello[0]));

            Assert.Equal("hei", fromFirst!.Text);
            Assert.Equal("hei", fromSecond!.Text);
            Assert.NotSame(fromFirst, fromSecond);
        }

        /// <summary>A module object is still single-use; the image is what is reusable.</summary>
        [Fact]
        public void AModuleInstance_IsStillLoadableOnlyOnce()
        {
            var image = SurtrModuleImage.FromModule(AddingModule());
            var module = image.Instantiate();

            using var first = new SurtrRuntime();
            using var second = new SurtrRuntime();

            first.LoadModule(module);

            Assert.Throws<InvalidOperationException>(() => second.LoadModule(module));
        }

        #endregion

        #region Declarations

        [Fact]
        public void ClassesSurviveWithTheirLayoutAndBehaviour()
        {
            var builder = new SurtrModuleBuilder("shapes");

            var vec = builder.DefineClass("Vec2");
            var x = vec.DefineField("x", SurtrClassReference.Float);
            var y = vec.DefineField("y", SurtrClassReference.Float);

            var constructor = vec.DefineConstructor(new[]
            {
                builder.Parameter("x", SurtrClassReference.Float),
                builder.Parameter("y", SurtrClassReference.Float),
            });

            constructor.Code
                .LoadLocal(constructor.Receiver).LoadLocal(constructor.Parameter(0)).StoreField(x)
                .LoadLocal(constructor.Receiver).LoadLocal(constructor.Parameter(1)).StoreField(y)
                .ReturnVoid();

            var sum = vec.DefineMethod("sum", SurtrClassReference.Float);
            sum.Code
                .LoadLocal(sum.Receiver).LoadField(x)
                .LoadLocal(sum.Receiver).LoadField(y)
                .FAdd()
                .ReturnValue();

            var make = builder.DefineFunction("make", SurtrClassReference.Float);
            make.Code
                .NewObject(vec.SelfReference)
                .Dup()
                .LoadFloat(1.5)
                .LoadFloat(2.25)
                .Call(constructor, discardResult: true)
                .Call(sum)
                .ReturnValue();

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetClass("Vec2", out var rebuilt));
            Assert.Equal(2, rebuilt.InstanceSlotCount);
            Assert.True(rebuilt.TryGetMethods("sum", out _));

            Assert.True(module.TryGetMethods("make", out var makeAgain));
            Assert.Equal(3.75, runtime.Invoke(makeAgain[0]).AsFloat);
        }

        [Fact]
        public void EnumsKeepTheirCasesAndOrdinals()
        {
            var builder = new SurtrModuleBuilder("cards");

            var suit = builder.DefineEnum("Suit", SurtrBuiltIns.Enum.SelfReference);
            suit.DefineEnumCase("Hearts", 0);
            suit.DefineEnumCase("Spades", 1);
            suit.DefineEnumCase("Clubs", 4);

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetClass("Suit", out var rebuilt));
            Assert.True(rebuilt.IsEnum);
            Assert.True(rebuilt.IsSealed);

            var cases = rebuilt.EnumCases;
            Assert.Equal(3, cases.Length);
            Assert.Equal("Hearts", cases[0].Name);
            Assert.Equal(0, cases[0].Ordinal);
            Assert.Equal("Clubs", cases[2].Name);
            Assert.Equal(2, cases[2].Ordinal);

            // The value travels explicitly (§2.4): it is the key an exhaustive switch dispatches
            // on, so the round trip must preserve it, not re-derive it from position.
            Assert.Equal(0, cases[0].Value);
            Assert.Equal(1, cases[1].Value);
            Assert.Equal(4, cases[2].Value);
        }

        [Fact]
        public void InterfacesAndTheirImplementationsSurvive()
        {
            var builder = new SurtrModuleBuilder("contracts");

            var shape = builder.DefineInterface("IShape");
            shape.DefineMethod("area", SurtrClassReference.Float);

            var square = builder.DefineClass("Square");
            square.Implements(shape.SelfReference);

            var area = square.DefineMethod("area", SurtrClassReference.Float, dispatch: SurtrMethodDispatch.Virtual);
            area.Code.LoadFloat(4.0).ReturnValue();

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetInterface("IShape", out var rebuiltShape));
            Assert.True(module.TryGetClass("Square", out var rebuiltSquare));
            Assert.True(rebuiltSquare.Implements(rebuiltShape));
        }

        /// <summary>
        /// The declaration-site <c>out</c>/<c>in</c> annotation (§6) rides next to the parameter
        /// names it annotates, so a module read back from an image answers subtype questions the
        /// same way its source would have.
        /// </summary>
        [Fact]
        public void GenericVarianceSurvivesTheRoundTrip()
        {
            var builder = new SurtrModuleBuilder("coll");

            var cell = builder.DefineClass("Cell");
            cell.Class.SetGenericParameters("T", "U");
            // The constraint table rides with the parameter list - one entry per parameter, even
            // an empty one - so an unbounded declaration still declares its (empty) bounds.
            cell.Class.SetGenericConstraints(Array.Empty<string>(), Array.Empty<string>());
            cell.Class.SetGenericVariance(SurtrGenericVariance.Invariant, SurtrGenericVariance.Covariant);

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetClass("Cell", out var rebuiltCell));
            var variance = rebuiltCell.GenericVariance.ToArray();
            Assert.Equal(2, variance.Length);
            Assert.Equal(SurtrGenericVariance.Invariant, variance[0]);
            Assert.Equal(SurtrGenericVariance.Covariant, variance[1]);
        }

        [Fact]
        public void GenericVarianceSurvivesTheRoundTripOnInterfaces()
        {
            var builder = new SurtrModuleBuilder("coll");

            var iterable = builder.DefineInterface("IIterable");
            iterable.Interface.SetGenericParameters("T");
            iterable.Interface.SetGenericConstraints(Array.Empty<string>());
            iterable.Interface.SetGenericVariance(SurtrGenericVariance.Covariant);

            var comparer = builder.DefineInterface("IComparer");
            comparer.Interface.SetGenericParameters("T");
            comparer.Interface.SetGenericConstraints(Array.Empty<string>());
            comparer.Interface.SetGenericVariance(SurtrGenericVariance.Contravariant);

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetInterface("IIterable", out var rebuiltIterable));
            Assert.Equal(SurtrGenericVariance.Covariant, Assert.Single(rebuiltIterable.GenericVariance.ToArray()));

            Assert.True(module.TryGetInterface("IComparer", out var rebuiltComparer));
            Assert.Equal(SurtrGenericVariance.Contravariant, Assert.Single(rebuiltComparer.GenericVariance.ToArray()));
        }

        [Fact]
        public void NestedTypesKeepTheirQualifiedNames()
        {
            var builder = new SurtrModuleBuilder("game");

            var entity = builder.DefineClass("Entity");
            var handle = entity.DefineNestedClass("Handle");
            handle.DefineField("id", SurtrClassReference.Integer);

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetClass("Entity", out var rebuilt));
            Assert.True(rebuilt.TryGetNestedClass("Handle", out var rebuiltHandle));
            Assert.Equal("Ogame:Entity.Handle;", rebuiltHandle.SelfReference.Descriptor);
        }

        /// <summary>
        /// A generic type's bounds ride with the parameters that declared them, so a module read
        /// back from an image can still answer what the declaration demanded of its parameters.
        /// </summary>
        [Fact]
        public void GenericConstraintsSurviveTheRoundTrip()
        {
            var builder = new SurtrModuleBuilder("box");

            var box = builder.DefineClass("Box");
            box.Class.SetGenericParameters("T", "U");
            box.Class.SetGenericConstraints(
                new[] { "Osurtr:IComparable`1;G0" },
                System.Array.Empty<string>());

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetClass("Box", out var rebuilt));
            Assert.Equal("T", rebuilt.GenericParameters[0]);
            Assert.Equal("U", rebuilt.GenericParameters[1]);
            Assert.Equal("Osurtr:IComparable`1;G0", Assert.Single(rebuilt.GenericConstraints[0]));
            Assert.Empty(rebuilt.GenericConstraints[1]);
        }

        /// <summary>
        /// A generic method's own parameters and their bounds ride with it, exactly the way a
        /// type's do — so a module read back from an image can still check a call site against the
        /// bound the declaring module wrote.
        /// </summary>
        [Fact]
        public void GenericMethodParametersAndConstraintsSurviveTheRoundTrip()
        {
            var builder = new SurtrModuleBuilder("util");

            var pick = builder.DefineFunction("pick", SurtrClassReference.Void);
            pick.DeclareGenericParameters(
                new[] { "T" },
                new[] { new[] { "Osurtr:IComparable`1;H0" } });
            pick.Code.ReturnVoid();

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetMethods("pick", out var overloads));
            var rebuilt = Assert.Single(overloads);

            Assert.Equal("T", Assert.Single(rebuilt.GenericParameters));
            // The bound names the method's own parameter through H0, which survives verbatim.
            Assert.Equal("Osurtr:IComparable`1;H0", Assert.Single(Assert.Single(rebuilt.GenericConstraints)));
        }

        [Fact]
        public void ParameterDefaultsAndVarargsSurvive()
        {
            var builder = new SurtrModuleBuilder("calls");

            var spawn = builder.DefineFunction(
                "spawn",
                SurtrClassReference.Void,
                new[]
                {
                    builder.Parameter("x", SurtrClassReference.Float),
                    builder.Parameter("hp", SurtrClassReference.Integer, SurtrConstant.Integer(100)),
                });

            spawn.Code.ReturnVoid();

            var log = builder.DefineFunction(
                "log",
                SurtrClassReference.Void,
                new[]
                {
                    builder.Parameter("pattern", SurtrClassReference.String),
                    builder.VarargsParameter("args", SurtrClassReference.String),
                });

            log.Code.ReturnVoid();

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetMethods("spawn", out var spawnAgain));
            Assert.Equal(1, spawnAgain[0].RequiredParameterCount);
            Assert.Equal(100, spawnAgain[0].Parameters[1].DefaultValue.Value.AsInt);

            Assert.True(module.TryGetMethods("log", out var logAgain));
            Assert.True(logAgain[0].HasVarargs);
        }

        [Fact]
        public void ExceptionHandlersSurvive()
        {
            var builder = new SurtrModuleBuilder("guarded");

            var risky = builder.DefineFunction("risky", SurtrClassReference.Integer);
            var code = risky.Code;

            var handler = code.NewLabel();
            var done = code.NewLabel();

            var region = risky.BeginTry();
            code.LoadInt(1).LoadInt(0).Div().Jump(done);
            risky.EndTry(region);

            code.MarkHandler(handler);
            code.Pop().LoadInt(-1);

            code.MarkPosition(done);
            code.ReturnValue();

            risky.AddCatch(region, SurtrClassReference.Object("surtr:DivideByZeroException"), handler);

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetMethods("risky", out var overloads));
            Assert.Equal(-1, runtime.Invoke(overloads[0]).AsInt);
        }

        [Fact]
        public void PropertiesKeepTheirAccessors()
        {
            var builder = new SurtrModuleBuilder("props");

            var counter = builder.DefineClass("Counter");
            var backing = counter.DefineField("_value", SurtrClassReference.Integer);

            var property = counter.DefineProperty("value", SurtrClassReference.Integer);
            var getter = property.DefineGetter();
            getter.Code.LoadLocal(getter.Receiver).LoadField(backing).ReturnValue();

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetClass("Counter", out var rebuilt));
            Assert.True(rebuilt.TryGetProperty("value", out var rebuiltProperty));
            Assert.NotNull(rebuiltProperty.Getter);
            Assert.Null(rebuiltProperty.Setter);
            Assert.Equal("get_value", rebuiltProperty.Getter!.Name);
        }

        /// <summary>A call into a built-in binds by name at load, like any other foreign member.</summary>
        [Fact]
        public void ACallIntoABuiltInRebinds()
        {
            var builder = new SurtrModuleBuilder("texts");

            SurtrBuiltIns.EnsureBuilt();
            Assert.True(SurtrBuiltIns.String.TryGetMethods("toUpper", out var toUpper));

            var shout = builder.DefineFunction("shout", SurtrClassReference.String);
            shout.Code.LoadString("hei").Call(toUpper[0]).ReturnValue();

            var image = SurtrModuleImage.FromModule(builder.Build());

            using var runtime = new SurtrRuntime();
            var module = runtime.LoadModule(image);

            Assert.True(module.TryGetMethods("shout", out var overloads));
            Assert.Equal("HEI", runtime.Resolve<SurtrString>(runtime.Invoke(overloads[0]))!.Text);
        }

        /// <summary>
        /// A cross-module call names its target by path, and each runtime binds it to its own copy
        /// of the callee — which is the whole reason the module table travels as text.
        /// </summary>
        [Fact]
        public void ACrossModuleCall_RebindsPerRuntime()
        {
            var libraryBuilder = new SurtrModuleBuilder("lib");
            var twice = libraryBuilder.DefineFunction(
                "twice",
                SurtrClassReference.Integer,
                new[] { libraryBuilder.Parameter("value", SurtrClassReference.Integer) });

            twice.Code.LoadLocal(twice.Parameter(0)).LoadLocal(twice.Parameter(0)).Add().ReturnValue();
            var library = libraryBuilder.Build();

            var appBuilder = new SurtrModuleBuilder("app");
            var run = appBuilder.DefineFunction("run", SurtrClassReference.Integer);
            run.Code.LoadInt(21).CallExternal(library, twice.Built!).ReturnValue();

            var libraryImage = SurtrModuleImage.FromModule(library);
            var appImage = SurtrModuleImage.FromModule(appBuilder.Build());

            using var first = new SurtrRuntime();
            using var second = new SurtrRuntime();

            foreach (var runtime in new[] { first, second })
            {
                runtime.LoadModule(libraryImage);
                var app = runtime.LoadModule(appImage);

                Assert.True(app.TryGetMethods("run", out var overloads));
                Assert.Equal(42, runtime.Invoke(overloads[0]).AsInt);
            }
        }

        /// <summary>A module naming one that is not loaded fails at load, not at the call.</summary>
        [Fact]
        public void ACallIntoAnAbsentModule_FailsAtLoad()
        {
            var libraryBuilder = new SurtrModuleBuilder("lib");
            var noop = libraryBuilder.DefineFunction("noop", SurtrClassReference.Void);
            noop.Code.ReturnVoid();
            var library = libraryBuilder.Build();

            var appBuilder = new SurtrModuleBuilder("app");
            var run = appBuilder.DefineFunction("run", SurtrClassReference.Void);
            run.Code.CallExternal(library, noop.Built!, discardResult: true).ReturnVoid();

            var appImage = SurtrModuleImage.FromModule(appBuilder.Build());

            using var runtime = new SurtrRuntime();

            var failure = Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(appImage));
            Assert.Contains("lib", failure.Message);
        }

        #endregion

        #region Rejections

        [Fact]
        public void AnUnbuiltModule_CannotBeWritten()
        {
            Assert.Throws<ArgumentException>(() => SurtrModuleImage.FromModule(new SurtrModule("empty")));
        }

        /// <summary>
        /// The built-in module is process-wide and shared. A copy of it read back from an image
        /// would shadow the real one rather than extend it, so it is not an image.
        /// </summary>
        [Fact]
        public void TheBuiltInModule_CannotBeWritten()
        {
            SurtrBuiltIns.EnsureBuilt();

            var failure = Assert.Throws<ArgumentException>(() => SurtrModuleImage.FromModule(SurtrBuiltIns.Module));
            Assert.Contains("process-wide", failure.Message);
        }

        [Theory]
        [InlineData(new byte[] { 1, 2, 3 })]
        [InlineData(new byte[0])]
        public void BytesThatAreNotAnImage_AreRejected(byte[] bytes)
        {
            Assert.Throws<SurtrImageFormatException>(() => SurtrModuleImage.FromBytes(bytes));
        }

        [Fact]
        public void AnImageOfAnotherVersion_IsRejected()
        {
            byte[] bytes = SurtrModuleImage.FromModule(AddingModule()).ToBytes();

            // The version sits right behind the magic.
            bytes[8] = 0xFF;
            bytes[9] = 0xFF;

            var failure = Assert.Throws<SurtrImageFormatException>(() => SurtrModuleImage.FromBytes(bytes));
            Assert.Contains("format version", failure.Message);
        }

        [Fact]
        public void ATruncatedImage_IsRejected()
        {
            byte[] bytes = SurtrModuleImage.FromModule(AddingModule()).ToBytes();
            var cut = new byte[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, cut, 0, cut.Length);

            Assert.Throws<SurtrImageFormatException>(() =>
            {
                var image = SurtrModuleImage.FromBytes(cut);
                image.Instantiate();
            });
        }

        #endregion
    }
}
