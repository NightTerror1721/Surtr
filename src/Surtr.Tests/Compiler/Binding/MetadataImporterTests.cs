#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.CodeGen;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using System.Linq;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// Covers the inverse of the descriptor emitter: metadata coming back in as symbols. The cases
    /// that matter are the ones where a descriptor is the only thing that still knows something —
    /// a nullable primitive, a type parameter's position, a constructed generic's arguments.
    /// </summary>
    public sealed class MetadataImporterTests
    {
        private static MetadataImporter Importer(out TypeSymbolFactory factory)
        {
            factory = new TypeSymbolFactory();
            return new MetadataImporter(factory);
        }

        #region Descriptors
        [Fact]
        public void ThePrimitivesComeBackAsTheFactorysOwn()
        {
            var importer = Importer(out var factory);

            Assert.Same(factory.Int, importer.Import(SurtrClassReference.Integer));
            Assert.Same(factory.Float, importer.Import(SurtrClassReference.Float));
            Assert.Same(factory.Bool, importer.Import(SurtrClassReference.Boolean));
            Assert.Same(factory.Char, importer.Import(SurtrClassReference.Character));
            Assert.Same(factory.String, importer.Import(SurtrClassReference.String));
            Assert.Same(factory.Range, importer.Import(SurtrClassReference.Range));
            Assert.Same(factory.Void, importer.Import(SurtrClassReference.Void));
        }

        [Fact]
        public void CompositesRebuildThroughTheFactorySoTheyStayInterned()
        {
            var importer = Importer(out var factory);

            Assert.Same(factory.Array(factory.Int), importer.Import(SurtrClassReference.FromDescriptor("AI")));
            Assert.Same(
                factory.Dictionary(factory.Int, factory.String),
                importer.Import(SurtrClassReference.FromDescriptor("DIS")));
            Assert.Same(
                factory.Tuple(new TypeSymbol[] { factory.Int, factory.Float }),
                importer.Import(SurtrClassReference.FromDescriptor("T(IF)")));
            Assert.Same(
                factory.Closure(new TypeSymbol[] { factory.Int, factory.Int }, factory.Float),
                importer.Import(SurtrClassReference.FromDescriptor("L(II)F")));
        }

        [Fact]
        public void ANullablePrimitiveComesBackNullable()
        {
            var importer = Importer(out var factory);

            Assert.Same(factory.Int.Nullable, importer.Import(SurtrClassReference.FromDescriptor("?I")));
            Assert.Same(factory.Array(factory.Int.Nullable), importer.Import(SurtrClassReference.FromDescriptor("A?I")));
        }

        [Fact]
        public void ANullableReferenceCannotComeBack()
        {
            var importer = Importer(out var factory);

            // The descriptor never carried it, so importing `S` gives the non-nullable type. This
            // is the loss the binder exists to avoid on the way out; on the way in it is a fact.
            Assert.Same(factory.String, importer.Import(SurtrClassReference.String));
        }

        [Fact]
        public void AnErasedSlotWithNoContextIsUnknown()
        {
            var importer = Importer(out var factory);

            Assert.Same(factory.Unknown, importer.Import(SurtrClassReference.Erased));
            Assert.Same(factory.Unknown, importer.Import(SurtrClassReference.GenericParameter(0)));
        }

        [Fact]
        public void AGenericParameterResolvesAgainstItsDeclaringType()
        {
            var importer = Importer(out var factory);

            var box = factory.DeclareType("Box", TypeSymbolKind.Class, new ModuleSymbol("box"));
            box.SetTypeParameters(new[]
            {
                factory.DeclareTypeParameter("T", box, 0),
                factory.DeclareTypeParameter("U", box, 1),
            });

            Assert.Same(box.TypeParameters[0], importer.Import(SurtrClassReference.GenericParameter(0), box));
            Assert.Same(box.TypeParameters[1], importer.Import(SurtrClassReference.GenericParameter(1), box));

            // Nested inside a composite, it still finds its position.
            Assert.Same(
                factory.Array(box.TypeParameters[0]),
                importer.Import(SurtrClassReference.FromDescriptor("AG0"), box));
        }

        [Fact]
        public void AnUnknownNameBecomesAnErrorTypeThatStillSaysWhatWasWritten()
        {
            var importer = Importer(out _);

            var imported = importer.Import(SurtrClassReference.Object("nowhere:Missing"));

            Assert.True(imported.IsError);
            Assert.Contains("Missing", imported.ToDisplayString());
        }
        #endregion

        #region Built-ins
        [Fact]
        public void TheBuiltInModuleIsAlwaysReachable()
        {
            var importer = Importer(out _);

            Assert.True(importer.KnowsModule(SurtrBuiltIns.ModulePath));
            Assert.True(importer.TryResolve("surtr:IIterable`1", out _));
        }

        [Fact]
        public void AGenericContractComesBackWithItsArityAndParameterNames()
        {
            var importer = Importer(out _);

            Assert.True(importer.TryResolve("surtr:IComparable`1", out var metadata));
            var symbol = importer.Import(metadata);

            // The arity lives in the metadata name and in the parameter list; the symbol keeps the
            // two apart, so the source name comes back unmangled.
            Assert.Equal("IComparable", symbol.Name);
            Assert.Equal("IComparable`1", symbol.MetadataName);
            Assert.Equal(1, symbol.Arity);
            Assert.Equal("T", symbol.TypeParameters[0].Name);
            Assert.Equal(TypeSymbolKind.Interface, symbol.TypeKind);
        }

        [Fact]
        public void AContractsMembersMentionItsOwnParameter()
        {
            var importer = Importer(out _);

            Assert.True(importer.TryResolve("surtr:IComparable`1", out var metadata));
            var symbol = importer.Import(metadata);

            var compareTo = symbol.Members.OfType<MethodSymbol>().Single(m => m.Name == "compareTo");

            Assert.Same(symbol.TypeParameters[0], compareTo.Parameters[0].Type);
            Assert.Equal(MethodDispatch.Abstract, compareTo.Dispatch);
        }

        [Fact]
        public void ImportingTheSameMetadataTwiceGivesOneSymbol()
        {
            var importer = Importer(out _);

            Assert.True(importer.TryResolve("surtr:IIterable`1", out var metadata));

            Assert.Same(importer.Import(metadata), importer.Import(metadata));
        }
        #endregion

        #region Whole modules
        [Fact]
        public void AClassComesBackWithItsShapeAndMembers()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("game.core");

            var entity = builder.DefineClass("Entity");
            entity.DefineField("id", SurtrClassReference.Integer, visibility: SurtrVisibility.Public);

            var derived = builder.DefineClass("Player", entity.SelfReference);
            var score = derived.DefineMethod("score", SurtrClassReference.Integer);
            score.Code.LoadInt(0);
            score.Code.ReturnValue();

            runtime.LoadModule(builder.Build());
            Assert.True(runtime.TryGetModule("game.core", out var module));

            var importer = Importer(out var factory);
            var symbol = importer.ImportModule(module);

            Assert.Equal("game.core", symbol.Path);

            var player = symbol.Types.Single(t => t.Name == "Player");
            var entitySymbol = symbol.Types.Single(t => t.Name == "Entity");

            Assert.Same(entitySymbol, player.BaseType);
            Assert.Equal("game.core:Player", player.FullMetadataName);

            var id = entitySymbol.Members.OfType<FieldSymbol>().Single(f => f.Name == "id");
            Assert.Same(factory.Int, id.Type);
            Assert.Equal(Accessibility.Public, id.Accessibility);
        }

        [Fact]
        public void AnImplementedContractComesBackAsTheSameSymbolTheContractDid()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("app");

            var contract = builder.DefineInterface("IThing");
            contract.DefineMethod("doThing", SurtrClassReference.Void);

            var holder = builder.DefineClass("Holder");
            holder.Implements(contract.SelfReference);
            holder.DefineMethod("doThing", SurtrClassReference.Void, dispatch: SurtrMethodDispatch.Virtual)
                .Code.ReturnVoid();

            runtime.LoadModule(builder.Build());
            Assert.True(runtime.TryGetModule("app", out var module));

            var importer = Importer(out _);
            var symbol = importer.ImportModule(module);

            var thing = symbol.Types.Single(t => t.Name == "IThing");
            var holderSymbol = symbol.Types.Single(t => t.Name == "Holder");

            Assert.Same(thing, Assert.Single(holderSymbol.Interfaces));
            Assert.Equal(TypeSymbolKind.Interface, thing.TypeKind);
        }

        [Fact]
        public void ASealedClassSaysSo()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("app");
            builder.DefineClass("Vec2", isSealed: true);

            runtime.LoadModule(builder.Build());
            Assert.True(runtime.TryGetModule("app", out var module));

            var importer = Importer(out _);
            var symbol = importer.ImportModule(module).Types.Single(t => t.Name == "Vec2");

            Assert.True(symbol.IsSealed);
            Assert.False(symbol.IsAbstract);
        }
        #endregion

        #region Round trip
        [Fact]
        public void EmittingASymbolAndImportingItBackGivesTheSameSymbol()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("game.core");
            builder.DefineClass("Entity");
            runtime.LoadModule(builder.Build());
            Assert.True(runtime.TryGetModule("game.core", out var module));

            var importer = Importer(out var factory);
            var entity = importer.ImportModule(module).Types.Single(t => t.Name == "Entity");

            var emitter = new DescriptorEmitter();

            // Everything the descriptor can carry survives the trip: the composite structure, the
            // primitives, and the name. Nullability of a reference does not, which is the point of
            // keeping the two models apart.
            foreach (TypeSymbol type in new TypeSymbol[]
            {
                factory.Int,
                factory.Int.Nullable,
                factory.String,
                factory.Array(factory.Int),
                factory.Dictionary(factory.String, factory.Array(factory.Int)),
                factory.Tuple(new TypeSymbol[] { factory.Int, factory.Float }),
                factory.Closure(new TypeSymbol[] { factory.Int }, factory.Void),
                entity,
            })
            {
                Assert.Same(type, importer.Import(emitter.Emit(type)));
            }
        }
        #endregion
    }
}
