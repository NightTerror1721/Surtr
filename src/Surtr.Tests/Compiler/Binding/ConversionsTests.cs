#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.Symbols;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// Covers which types reach which. Everything here follows from three decisions taken
    /// elsewhere: generics are invariant by default with opt-in declaration-site variance (§6),
    /// there are no user-defined implicit conversions (§5.6), and <c>unknown</c> is the erased slot
    /// with a surface name (§5.10).
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

        /// <summary>
        /// `T` runs unboxed at the interpreter and erases to a reference at emit, but neither of
        /// those is a fact the type checker owes anything to: to <see cref="Conversions"/> a type
        /// parameter is an ordinary invariant type, and the only thing that reaches a `T`-typed slot
        /// is `T` itself — never a concrete type, and never a different parameter, no matter what
        /// either is constrained to. `unknown` (<see cref="AnythingReachesTheErasedSlot"/>) is the
        /// one slot that is genuinely a wildcard; `T` only looks like one after substitution erases
        /// it, which is a runtime fact and not a compile-time one.
        /// </summary>
        [Fact]
        public void ATypeParameterAcceptsOnlyItself()
        {
            var conversions = Setup(out var factory);

            var box = Declare(factory, "Box");
            box.SetTypeParameters(new[] { factory.DeclareTypeParameter("T", box, 0) });
            var parameter = box.TypeParameters[0];

            var other = Declare(factory, "Other");
            other.SetTypeParameters(new[] { factory.DeclareTypeParameter("U", other, 0) });
            var unrelatedParameter = other.TypeParameters[0];

            Assert.Equal(ConversionKind.None, conversions.Classify(factory.Int, parameter).Kind);
            Assert.Equal(ConversionKind.None, conversions.Classify(factory.String, parameter).Kind);
            Assert.False(conversions.IsAssignable(factory.Int, parameter));

            // A different parameter is not implicitly assignable either — it just still reaches an
            // erased destination the same explicit way any other erased source would (§1.11).
            Assert.False(conversions.IsAssignable(unrelatedParameter, parameter));

            Assert.True(conversions.IsAssignable(parameter, parameter));
        }

        /// <summary>`T` widens to its own `T?` exactly like any other type does (§5.1) — nothing more.</summary>
        [Fact]
        public void ATypeParameterWidensToItsOwnNullableForm()
        {
            var conversions = Setup(out var factory);

            var box = Declare(factory, "Box");
            box.SetTypeParameters(new[] { factory.DeclareTypeParameter("T", box, 0) });
            var parameter = box.TypeParameters[0];

            Assert.Equal(ConversionKind.ImplicitNullable, conversions.Classify(parameter, parameter.Nullable).Kind);
            Assert.False(conversions.IsAssignable(parameter.Nullable, parameter));
        }

        /// <summary>A concrete type still reads back out of a `T`-typed slot only through a cast.</summary>
        [Fact]
        public void ReadingAConcreteTypeOutOfATypeParameterStillNeedsACast()
        {
            var conversions = Setup(out var factory);

            var box = Declare(factory, "Box");
            box.SetTypeParameters(new[] { factory.DeclareTypeParameter("T", box, 0) });
            var parameter = box.TypeParameters[0];

            Assert.False(conversions.IsAssignable(parameter, factory.Int));
            Assert.Equal(ConversionKind.ExplicitErasure, conversions.Classify(parameter, factory.Int).Kind);
        }

        [Fact]
        public void GenericsAreInvariant()
        {
            // §6's default is still invariance: one construction reaches only itself unless the
            // declaration annotated a parameter out/in (see the variance regions below).
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

        /// <summary>Declares a generic type whose parameters carry the given variances (§6).</summary>
        private static NamedTypeSymbol DeclareVariant(
            TypeSymbolFactory factory,
            string name,
            params TypeParameterVariance[] variances)
        {
            var symbol = Declare(factory, name);
            var parameters = new TypeParameterSymbol[variances.Length];
            for (int i = 0; i < variances.Length; i++)
            {
                parameters[i] = factory.DeclareTypeParameter("T" + i, symbol, i);
                parameters[i].Variance = variances[i];
            }

            symbol.SetTypeParameters(parameters);
            return symbol;
        }

        #region Declared variance

        /// <summary>
        /// The one place total invariance opens up: a construction widens along an <c>out</c>
        /// annotation and nothing else — and only because the declaration asked for it.
        /// </summary>
        [Fact]
        public void ACovariantConstructionWidensAlongItsAnnotation()
        {
            var conversions = Setup(out var factory);

            var animal = Declare(factory, "Animal");
            var dog = Declare(factory, "Dog");
            dog.BaseType = animal;

            var producer = DeclareVariant(factory, "Producer", TypeParameterVariance.Covariant);

            Assert.True(conversions.IsAssignable(
                producer.Construct(new TypeSymbol[] { dog }),
                producer.Construct(new TypeSymbol[] { animal })));

            // Widening is one-way: the annotation promises production, not consumption.
            Assert.False(conversions.IsAssignable(
                producer.Construct(new TypeSymbol[] { animal }),
                producer.Construct(new TypeSymbol[] { dog })));
        }

        /// <summary>A comparer of animals compares dogs: <c>in</c> flips the direction.</summary>
        [Fact]
        public void AContravariantConstructionNarrowsAlongItsAnnotation()
        {
            var conversions = Setup(out var factory);

            var animal = Declare(factory, "Animal");
            var dog = Declare(factory, "Dog");
            dog.BaseType = animal;

            var consumer = DeclareVariant(factory, "Consumer", TypeParameterVariance.Contravariant);

            Assert.True(conversions.IsAssignable(
                consumer.Construct(new TypeSymbol[] { animal }),
                consumer.Construct(new TypeSymbol[] { dog })));

            Assert.False(conversions.IsAssignable(
                consumer.Construct(new TypeSymbol[] { dog }),
                consumer.Construct(new TypeSymbol[] { animal })));
        }

        /// <summary>Variance is opt-in per parameter: whatever was not annotated stays exact.</summary>
        [Fact]
        public void AnUnannotatedParameterStaysInvariant()
        {
            var conversions = Setup(out var factory);

            var animal = Declare(factory, "Animal");
            var dog = Declare(factory, "Dog");
            dog.BaseType = animal;

            var pair = DeclareVariant(factory, "Pair", TypeParameterVariance.Invariant, TypeParameterVariance.Covariant);

            var ofDog = pair.Construct(new TypeSymbol[] { dog, dog });
            var widened = pair.Construct(new TypeSymbol[] { dog, animal });
            Assert.True(conversions.IsAssignable(ofDog, widened));

            // The invariant first slot refuses to move even though the second one widens.
            Assert.False(conversions.IsAssignable(
                pair.Construct(new TypeSymbol[] { dog, dog }),
                pair.Construct(new TypeSymbol[] { animal, animal })));
        }

        /// <summary>
        /// The restricted matching is what keeps variance sound under erasure: an implicit
        /// conversion that would box differently — int to float — must not relate two
        /// constructions, because no per-element conversion exists at run time to apply it.
        /// </summary>
        [Fact]
        public void CovarianceNeverRidesANumericWidening()
        {
            var conversions = Setup(out var factory);

            var producer = DeclareVariant(factory, "Producer", TypeParameterVariance.Covariant);

            Assert.True(conversions.IsAssignable(factory.Int, factory.Float));
            Assert.False(conversions.IsAssignable(
                producer.Construct(new TypeSymbol[] { factory.Int }),
                producer.Construct(new TypeSymbol[] { factory.Float })));
        }

        /// <summary>Nullability moves one way only, argument by argument as for whole types.</summary>
        [Fact]
        public void VarianceArgumentsStillMoveOnlyTowardNullable()
        {
            var conversions = Setup(out var factory);

            var animal = Declare(factory, "Animal");

            var producer = DeclareVariant(factory, "Producer", TypeParameterVariance.Covariant);

            // A nullable element read into a nullable slot is fine; the reverse promises values
            // that include null where none were allowed.
            var ofAnimal = producer.Construct(new TypeSymbol[] { animal });
            var ofNullableAnimal = producer.Construct(new TypeSymbol[] { animal.Nullable });

            Assert.True(conversions.IsAssignable(ofAnimal, ofNullableAnimal));
            Assert.False(conversions.IsAssignable(ofNullableAnimal, ofAnimal));
        }

        /// <summary>A covariant element carrying another construction composes.</summary>
        [Fact]
        public void CovarianceComposesThroughNestedConstructions()
        {
            var conversions = Setup(out var factory);

            var animal = Declare(factory, "Animal");
            var dog = Declare(factory, "Dog");
            dog.BaseType = animal;

            var producer = DeclareVariant(factory, "Producer", TypeParameterVariance.Covariant);

            var nestedOfDog = producer.Construct(new TypeSymbol[] { producer.Construct(new TypeSymbol[] { dog }) });
            var nestedOfAnimal = producer.Construct(new TypeSymbol[] { producer.Construct(new TypeSymbol[] { animal }) });

            Assert.True(conversions.IsAssignable(nestedOfDog, nestedOfAnimal));
            Assert.False(conversions.IsAssignable(nestedOfAnimal, nestedOfDog));
        }

        /// <summary>
        /// The walk already reads every base «as seen from» its construction, so variance applies
        /// at whichever ancestor the question lands on — not only when both sides share it.
        /// </summary>
        [Fact]
        public void VarianceAppliesThroughTheHierarchyWalk()
        {
            var conversions = Setup(out var factory);

            var animal = Declare(factory, "Animal");
            var dog = Declare(factory, "Dog");
            dog.BaseType = animal;

            var iterable = DeclareVariant(factory, "IIterable", TypeParameterVariance.Covariant);
            var collection = Declare(factory, "ICollection", TypeSymbolKind.Interface);
            collection.SetTypeParameters(new[] { factory.DeclareTypeParameter("T", collection, 0) });
            collection.Interfaces = new[] { iterable.Construct(new TypeSymbol[] { collection.TypeParameters[0] }) };

            var list = Declare(factory, "List");
            list.SetTypeParameters(new[] { factory.DeclareTypeParameter("T", list, 0) });
            list.Interfaces = new[] { collection.Construct(new TypeSymbol[] { list.TypeParameters[0] }) };

            var listOfDog = list.Construct(new TypeSymbol[] { dog });
            var iterableOfAnimal = iterable.Construct(new TypeSymbol[] { animal });

            Assert.True(conversions.IsSubtype(listOfDog, iterableOfAnimal));
        }
        #endregion

        #region Structural variance

        /// <summary>
        /// Closures are contravariant in their inputs and covariant in their output — the case of
        /// highest daily value, since handlers and mappers are everywhere.
        /// </summary>
        [Fact]
        public void AClosureWidensAgainstItsInputsAndAlongItsOutput()
        {
            var conversions = Setup(out var factory);

            var animal = Declare(factory, "Animal");
            var dog = Declare(factory, "Dog");
            dog.BaseType = animal;

            var handleAnyAnimal = factory.Closure(new TypeSymbol[] { animal }, factory.Int);
            var handleOnlyDogs = factory.Closure(new TypeSymbol[] { dog }, factory.Int);

            // A handler that handles any animal serves wherever a dog handler is asked for —
            // never the reverse, which would hand it cats.
            Assert.True(conversions.IsAssignable(handleAnyAnimal, handleOnlyDogs));
            Assert.False(conversions.IsAssignable(handleOnlyDogs, handleAnyAnimal));

            // And the output still has to narrow in the ordinary direction.
            var produceAnimal = factory.Closure(new TypeSymbol[] { factory.Int }, animal);
            var produceDog = factory.Closure(new TypeSymbol[] { factory.Int }, dog);
            Assert.True(conversions.IsAssignable(produceDog, produceAnimal));
            Assert.False(conversions.IsAssignable(produceAnimal, produceDog));
        }

        [Fact]
        public void AChangedArityIsStillNoConversion()
        {
            var conversions = Setup(out var factory);

            var one = factory.Closure(new TypeSymbol[] { factory.Int }, factory.Int);
            var two = factory.Closure(new TypeSymbol[] { factory.Int, factory.Int }, factory.Int);
            Assert.False(conversions.IsAssignable(one, two));

            var pair = factory.Tuple(new TypeSymbol[] { factory.Int, factory.String });
            var triple = factory.Tuple(new TypeSymbol[] { factory.Int, factory.String, factory.String });
            Assert.False(conversions.IsAssignable(pair, triple));
        }

        /// <summary>Tuples widen element by element, coherently with their structural equality.</summary>
        [Fact]
        public void TuplesWidenElementByElement()
        {
            var conversions = Setup(out var factory);

            var animal = Declare(factory, "Animal");
            var dog = Declare(factory, "Dog");
            dog.BaseType = animal;

            var specific = factory.Tuple(new TypeSymbol[] { dog, animal });
            var wider = factory.Tuple(new TypeSymbol[] { animal, animal });

            Assert.True(conversions.IsAssignable(specific, wider));
            Assert.False(conversions.IsAssignable(wider, specific));
        }

        /// <summary>§5.3: names never join the signature, so a named tuple and its unnamed twin are the same type — identity, not a reference conversion that would box.</summary>
        [Fact]
        public void ANamedTupleAndItsUnnamedTwinAreTheSameType()
        {
            var conversions = Setup(out var factory);

            var unnamed = factory.Tuple(new TypeSymbol[] { factory.Int, factory.String });
            var named = factory.Tuple(new TypeSymbol[] { factory.Int, factory.String }, new string?[] { "x", "y" });
            var renamed = factory.Tuple(new TypeSymbol[] { factory.Int, factory.String }, new string?[] { "a", "b" });

            Assert.True(conversions.Classify(unnamed, named).IsIdentity);
            Assert.True(conversions.Classify(named, unnamed).IsIdentity);
            Assert.True(conversions.Classify(named, renamed).IsIdentity);
            Assert.True(conversions.IsAssignable(named, unnamed));

            // Different element types keep widening as ordinary tuples.
            var widened = factory.Tuple(new TypeSymbol[] { factory.Float, factory.String }, new string?[] { "x", "y" });
            Assert.False(conversions.IsAssignable(named, widened));
        }

        /// <summary>A generator only yields, so it widens exactly like a covariant construction.</summary>
        [Fact]
        public void GeneratorsWidenWithWhatTheyYield()
        {
            var conversions = Setup(out var factory);

            var animal = Declare(factory, "Animal");
            var dog = Declare(factory, "Dog");
            dog.BaseType = animal;

            Assert.True(conversions.IsAssignable(factory.Generator(dog), factory.Generator(animal)));
            Assert.False(conversions.IsAssignable(factory.Generator(animal), factory.Generator(dog)));
        }
        #endregion

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
