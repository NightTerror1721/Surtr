#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.Symbols;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// Covers which types reach which. Everything here follows from three decisions taken
    /// elsewhere: generics are invariant (§6), there are no user-defined implicit conversions
    /// (§5.6), and <c>unknown</c> is the erased slot with a surface name (§5.10).
    /// </summary>
    public sealed class ConversionsTests
    {
        private static Conversions Setup(out TypeSymbolFactory factory)
        {
            factory = new TypeSymbolFactory();
            return new Conversions(factory);
        }

        private static NamedTypeSymbol Declare(
            TypeSymbolFactory factory,
            string name,
            TypeSymbolKind kind = TypeSymbolKind.Class)
            => factory.DeclareType(name, kind, new ModuleSymbol("game.core"));

        #region Identity and numerics
        [Fact]
        public void ATypeReachesItself()
        {
            var conversions = Setup(out var factory);

            Assert.True(conversions.Classify(factory.Int, factory.Int).IsIdentity);
            Assert.True(conversions.IsAssignable(factory.String, factory.String));
        }

        [Fact]
        public void IntWidensToFloatAndNotBack()
        {
            var conversions = Setup(out var factory);

            Assert.Equal(ConversionKind.ImplicitNumeric, conversions.Classify(factory.Int, factory.Float).Kind);
            Assert.False(conversions.IsAssignable(factory.Float, factory.Int));

            // Narrowing exists, but has to be written.
            Assert.Equal(ConversionKind.ExplicitNumeric, conversions.Classify(factory.Float, factory.Int).Kind);
        }

        [Fact]
        public void TheOtherPrimitivesDoNotWidenSilently()
        {
            // §5.7 names exactly one implicit numeric conversion; anything else would enlarge the
            // candidate set overload resolution has to search.
            var conversions = Setup(out var factory);

            Assert.False(conversions.IsAssignable(factory.Char, factory.Int));
            Assert.False(conversions.IsAssignable(factory.Bool, factory.Int));
            Assert.False(conversions.IsAssignable(factory.Int, factory.Char));
        }
        #endregion

        #region Nullability
        [Fact]
        public void AValueReachesItsOwnNullableForm()
        {
            var conversions = Setup(out var factory);

            Assert.Equal(ConversionKind.ImplicitNullable, conversions.Classify(factory.Int, factory.Int.Nullable).Kind);
            Assert.Equal(ConversionKind.ImplicitNullable, conversions.Classify(factory.String, factory.String.Nullable).Kind);
        }

        [Fact]
        public void ANullableDoesNotReachItsNonNullableFormSilently()
        {
            // That narrowing is what `!!` asserts, and an assertion is written, not inferred.
            var conversions = Setup(out var factory);

            Assert.False(conversions.IsAssignable(factory.Int.Nullable, factory.Int));
            Assert.True(conversions.Classify(factory.Int.Nullable, factory.Int).Exists);
        }

        [Fact]
        public void NullGoesOnlyWhereNullIsAllowed()
        {
            var conversions = Setup(out var factory);

            Assert.False(conversions.AcceptsNull(factory.String));
            Assert.False(conversions.AcceptsNull(factory.Int));

            Assert.True(conversions.AcceptsNull(factory.String.Nullable));
            Assert.True(conversions.AcceptsNull(factory.Int.Nullable));

            // An erased slot holds a reference, and a reference can already be null.
            Assert.True(conversions.AcceptsNull(factory.Unknown));
        }
        #endregion

        #region References
        [Fact]
        public void ADerivedTypeReachesItsBase()
        {
            var conversions = Setup(out var factory);

            var animal = Declare(factory, "Animal");
            var dog = Declare(factory, "Dog");
            dog.BaseType = animal;

            Assert.Equal(ConversionKind.ImplicitReference, conversions.Classify(dog, animal).Kind);
            Assert.Equal(ConversionKind.ExplicitReference, conversions.Classify(animal, dog).Kind);
        }

        [Fact]
        public void AClassReachesAContractItSatisfies()
        {
            var conversions = Setup(out var factory);

            var contract = Declare(factory, "IThing", TypeSymbolKind.Interface);
            var holder = Declare(factory, "Holder");
            holder.Interfaces = new[] { contract };

            Assert.True(conversions.IsAssignable(holder, contract));
            Assert.False(conversions.IsAssignable(contract, holder));
        }

        [Fact]
        public void TheWalkGoesAllTheWayUp()
        {
            var conversions = Setup(out var factory);

            var contract = Declare(factory, "IThing", TypeSymbolKind.Interface);
            var animal = Declare(factory, "Animal");
            animal.Interfaces = new[] { contract };

            var dog = Declare(factory, "Dog");
            dog.BaseType = animal;

            var puppy = Declare(factory, "Puppy");
            puppy.BaseType = dog;

            Assert.True(conversions.IsSubtype(puppy, animal));
            Assert.True(conversions.IsSubtype(puppy, contract));
            Assert.False(conversions.IsSubtype(animal, puppy));
        }

        [Fact]
        public void TwoUnrelatedTypesDoNotReachOneAnother()
        {
            var conversions = Setup(out var factory);

            Assert.False(conversions.Classify(Declare(factory, "A"), Declare(factory, "B")).Exists);
        }
        #endregion

        #region Erasure and generics
        [Fact]
        public void AnythingReachesTheErasedSlot()
        {
            var conversions = Setup(out var factory);

            Assert.Equal(ConversionKind.ImplicitErasure, conversions.Classify(factory.Int, factory.Unknown).Kind);
            Assert.Equal(ConversionKind.ImplicitErasure, conversions.Classify(factory.String, factory.Unknown).Kind);
        }

        [Fact]
        public void NothingComesBackOutOfTheErasedSlotWithoutACast()
        {
            // §5.10: `unknown` holds anything but must be cast before use.
            var conversions = Setup(out var factory);

            Assert.False(conversions.IsAssignable(factory.Unknown, factory.Int));
            Assert.Equal(ConversionKind.ExplicitErasure, conversions.Classify(factory.Unknown, factory.Int).Kind);
        }

        [Fact]
        public void ATypeParameterIsAnErasedSlotToo()
        {
            var conversions = Setup(out var factory);

            var box = Declare(factory, "Box");
            box.SetTypeParameters(new[] { factory.DeclareTypeParameter("T", box, 0) });

            var parameter = box.TypeParameters[0];

            Assert.Equal(ConversionKind.ImplicitErasure, conversions.Classify(factory.Int, parameter).Kind);
            Assert.False(conversions.IsAssignable(parameter, factory.Int));
        }

        [Fact]
        public void GenericsAreInvariant()
        {
            // §6 supports no declaration-site variance, so one construction reaches only itself.
            var conversions = Setup(out var factory);

            var animal = Declare(factory, "Animal");
            var dog = Declare(factory, "Dog");
            dog.BaseType = animal;

            var box = Declare(factory, "Box");
            box.SetTypeParameters(new[] { factory.DeclareTypeParameter("T", box, 0) });

            var ofDog = box.Construct(new TypeSymbol[] { dog });
            var ofAnimal = box.Construct(new TypeSymbol[] { animal });

            Assert.False(conversions.IsAssignable(ofDog, ofAnimal));
            Assert.True(conversions.IsAssignable(ofDog, ofDog));
        }

        [Fact]
        public void ArraysAreInvariantToo()
        {
            var conversions = Setup(out var factory);

            var animal = Declare(factory, "Animal");
            var dog = Declare(factory, "Dog");
            dog.BaseType = animal;

            Assert.False(conversions.IsAssignable(factory.Array(dog), factory.Array(animal)));
        }
        #endregion

        #region Value classes and user conversions
        [Fact]
        public void AValueClassAndTheTypeItWrapsAreNotInterchangeable()
        {
            // §2.9 is the whole point of the construct: `despawn(7)` is an error where an EntityId
            // is expected, which a transparent alias could never give you.
            var conversions = Setup(out var factory);

            var entityId = Declare(factory, "EntityId", TypeSymbolKind.ValueClass);
            entityId.UnderlyingType = factory.Int;

            Assert.False(conversions.IsAssignable(factory.Int, entityId));
            Assert.False(conversions.IsAssignable(entityId, factory.Int));
        }

        [Fact]
        public void AUserConversionIsFoundButNeverImplicit()
        {
            var conversions = Setup(out var factory);

            var vec2 = Declare(factory, "Vec2");
            var vec3 = Declare(factory, "Vec3");

            var toVec3 = new MethodSymbol(OperatorNames.Conversion, vec2, vec3)
            {
                IsStatic = true,
                IsConversion = true,
                Role = MethodRole.Operator,
                Parameters = new[] { new ParameterSymbol("v", vec2, 0) },
            };

            vec2.Members = new Symbol[] { toVec3 };

            var conversion = conversions.Classify(vec2, vec3);

            Assert.Equal(ConversionKind.UserDefined, conversion.Kind);
            Assert.Same(toVec3, conversion.Method);
            Assert.False(conversion.IsImplicit);
        }

        [Fact]
        public void AConversionIsFoundOnEitherEnd()
        {
            // §5.6 puts each direction on whichever type declares it, so both ends are searched.
            var conversions = Setup(out var factory);

            var vec2 = Declare(factory, "Vec2");
            var vec3 = Declare(factory, "Vec3");

            var fromVec2 = new MethodSymbol(OperatorNames.Conversion, vec3, vec3)
            {
                IsStatic = true,
                IsConversion = true,
                Role = MethodRole.Operator,
                Parameters = new[] { new ParameterSymbol("v", vec2, 0) },
            };

            vec3.Members = new Symbol[] { fromVec2 };

            Assert.Equal(ConversionKind.UserDefined, conversions.Classify(vec2, vec3).Kind);
        }
        #endregion

        #region Error recovery
        [Fact]
        public void TheErrorTypeReachesEverythingSilently()
        {
            var conversions = Setup(out var factory);

            Assert.True(conversions.IsAssignable(factory.Error("Missing"), factory.Int));
            Assert.True(conversions.IsAssignable(factory.Int, factory.Error("Missing")));
            Assert.True(conversions.AcceptsNull(factory.Error("Missing")));
        }

        [Fact]
        public void VoidReachesNothing()
        {
            var conversions = Setup(out var factory);

            Assert.False(conversions.Classify(factory.Void, factory.Int).Exists);
            Assert.False(conversions.Classify(factory.Int, factory.Void).Exists);
        }
        #endregion
    }
}
