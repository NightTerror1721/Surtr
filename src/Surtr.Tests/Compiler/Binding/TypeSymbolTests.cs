#nullable enable

using Surtr.Compiler.Binding.Symbols;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// Covers the compiler's own type model. The cases that carry weight are the ones a descriptor
    /// could not express: that <c>int?</c> and <c>int</c> are different types, that
    /// <c>Box&lt;int&gt;</c> and <c>Box&lt;string&gt;</c> are, that an alias is not a type at all,
    /// and that a <c>value class</c> is one despite erasing.
    /// </summary>
    public sealed class TypeSymbolTests
    {
        private static NamedTypeSymbol DeclareGeneric(
            TypeSymbolFactory factory,
            ModuleSymbol module,
            string name,
            params string[] parameterNames)
        {
            var type = factory.DeclareType(name, TypeSymbolKind.Class, module);

            var parameters = new TypeParameterSymbol[parameterNames.Length];
            for (int i = 0; i < parameterNames.Length; i++)
                parameters[i] = factory.DeclareTypeParameter(parameterNames[i], type, i);

            type.SetTypeParameters(parameters);
            return type;
        }

        #region Interning
        [Fact]
        public void AnArrayTypeIsInternedByItsElement()
        {
            var factory = new TypeSymbolFactory();

            Assert.Same(factory.Array(factory.Int), factory.Array(factory.Int));
            Assert.NotSame(factory.Array(factory.Int), factory.Array(factory.String));
            Assert.Same(
                factory.Array(factory.Array(factory.Int)),
                factory.Array(factory.Array(factory.Int)));
        }

        [Fact]
        public void ADictionaryTypeIsInternedByBothHalves()
        {
            var factory = new TypeSymbolFactory();

            Assert.Same(
                factory.Dictionary(factory.Int, factory.String),
                factory.Dictionary(factory.Int, factory.String));

            Assert.NotSame(
                factory.Dictionary(factory.Int, factory.String),
                factory.Dictionary(factory.String, factory.Int));
        }

        [Fact]
        public void ATupleTypeIsInternedByItsElements()
        {
            var factory = new TypeSymbolFactory();

            var first = factory.Tuple(new TypeSymbol[] { factory.Int, factory.Float });
            var second = factory.Tuple(new TypeSymbol[] { factory.Int, factory.Float });
            var reordered = factory.Tuple(new TypeSymbol[] { factory.Float, factory.Int });

            Assert.Same(first, second);
            Assert.NotSame(first, reordered);
        }

        /// <summary>§5.3: names never join a tuple's signature, so a named tuple is a distinct object interned by its (types, names) pair.</summary>
        [Fact]
        public void ATupleWithElementNamesIsInternedByNameAndShape()
        {
            var factory = new TypeSymbolFactory();

            var unnamed = factory.Tuple(new TypeSymbol[] { factory.Int, factory.String });
            var named = factory.Tuple(new TypeSymbol[] { factory.Int, factory.String }, new string?[] { "x", "y" });
            var same = factory.Tuple(new TypeSymbol[] { factory.Int, factory.String }, new string?[] { "x", "y" });
            var renamed = factory.Tuple(new TypeSymbol[] { factory.Int, factory.String }, new string?[] { "a", "b" });
            var reordered = factory.Tuple(new TypeSymbol[] { factory.String, factory.Int }, new string?[] { "x", "y" });

            Assert.Same(named, same);
            Assert.NotSame(unnamed, named);
            Assert.NotSame(named, renamed);
            Assert.NotSame(named, reordered);
        }

        /// <summary>§5.3: a position written without a name is <see langword="null"/>, and a tuple whose names are all null is the unnamed one.</summary>
        [Fact]
        public void AAllNullElementNamesCollapseToTheUnnamedTuple()
        {
            var factory = new TypeSymbolFactory();

            Assert.Same(
                factory.Tuple(new TypeSymbol[] { factory.Int }, new string?[] { null }),
                factory.Tuple(new TypeSymbol[] { factory.Int }));
        }

        [Fact]
        public void AClosureTypeIsInternedByItsWholeShape()
        {
            var factory = new TypeSymbolFactory();

            var parameters = new TypeSymbol[] { factory.Int, factory.Int };

            Assert.Same(
                factory.Closure(parameters, factory.Float),
                factory.Closure(new TypeSymbol[] { factory.Int, factory.Int }, factory.Float));

            // Same parameters, different return: a different type.
            Assert.NotSame(
                factory.Closure(parameters, factory.Float),
                factory.Closure(parameters, factory.Int));
        }

        [Fact]
        public void TheErrorTypeIsInternedByName()
        {
            var factory = new TypeSymbolFactory();

            Assert.Same(factory.Error("Missing"), factory.Error("Missing"));
            Assert.NotSame(factory.Error("Missing"), factory.Error("AlsoMissing"));
            Assert.True(factory.Error("Missing").IsError);
        }
        #endregion

        #region Nullability
        [Fact]
        public void ANullableTypeIsOneSymbolHoweverItIsReached()
        {
            var factory = new TypeSymbolFactory();

            Assert.Same(factory.Int.Nullable, factory.Int.Nullable);
            Assert.Same(factory.Array(factory.Int).Nullable, factory.Array(factory.Int).Nullable);
        }

        [Fact]
        public void NullabilityRoundTrips()
        {
            var factory = new TypeSymbolFactory();

            Assert.Same(factory.Int, factory.Int.Nullable.NonNullable);
            Assert.Same(factory.Int.Nullable, factory.Int.Nullable.Nullable);
            Assert.NotSame(factory.Int, factory.Int.Nullable);
        }

        [Fact]
        public void ANullableTypeKeepsEverythingElseAboutItself()
        {
            var factory = new TypeSymbolFactory();

            var nullable = factory.Int.Nullable;

            Assert.True(nullable.IsNullable);
            Assert.Equal(SpecialType.Int, nullable.SpecialType);
            Assert.False(nullable.IsReferenceType);
            Assert.Equal(TypeSymbolKind.Class, nullable.TypeKind);
        }

        [Fact]
        public void OnlyThePrimitivesAreValueTypes()
        {
            var factory = new TypeSymbolFactory();

            Assert.True(factory.Int.IsPrimitive);
            Assert.True(factory.Float.IsPrimitive);
            Assert.True(factory.Bool.IsPrimitive);
            Assert.True(factory.Char.IsPrimitive);

            // string is a reference despite being a built-in, and so is an erased slot.
            Assert.True(factory.String.IsReferenceType);
            Assert.True(factory.Range.IsReferenceType);
            Assert.True(factory.Unknown.IsReferenceType);
            Assert.True(factory.Array(factory.Int).IsReferenceType);
        }

        [Fact]
        public void VoidNamesNoValueAtAll()
        {
            var factory = new TypeSymbolFactory();

            Assert.True(factory.Void.IsVoid);
            Assert.False(factory.Int.IsVoid);
        }
        #endregion

        #region Arity and construction
        [Fact]
        public void ArityIsPartOfTheNameTheRuntimeSees()
        {
            var factory = new TypeSymbolFactory();
            var module = new ModuleSymbol("game.core");

            var plain = factory.DeclareType("Entity", TypeSymbolKind.Class, module);
            var generic = DeclareGeneric(factory, module, "Box", "T");

            Assert.Equal("Entity", plain.MetadataName);
            Assert.Equal("game.core:Entity", plain.FullMetadataName);

            Assert.Equal("Box`1", generic.MetadataName);
            Assert.Equal("game.core:Box`1", generic.FullMetadataName);
        }

        [Fact]
        public void TwoAritiesOfTheSameNameAreUnrelatedTypes()
        {
            var factory = new TypeSymbolFactory();
            var module = new ModuleSymbol("game.core");

            var one = DeclareGeneric(factory, module, "Result", "T");
            var two = DeclareGeneric(factory, module, "Result", "T", "E");

            Assert.NotSame(one, two);
            Assert.Equal(1, one.Arity);
            Assert.Equal(2, two.Arity);
            Assert.NotEqual(one.FullMetadataName, two.FullMetadataName);
        }

        [Fact]
        public void ANestedTypeCarriesItsContainerInItsFullName()
        {
            var factory = new TypeSymbolFactory();
            var module = new ModuleSymbol("game.core");

            var entity = factory.DeclareType("Entity", TypeSymbolKind.Class, module);
            var handle = factory.DeclareType("Handle", TypeSymbolKind.Class, module, entity);

            Assert.Equal("game.core:Entity.Handle", handle.FullMetadataName);
        }

        [Fact]
        public void ConstructingWithTheSameArgumentsGivesTheSameType()
        {
            var factory = new TypeSymbolFactory();
            var box = DeclareGeneric(factory, new ModuleSymbol("game.core"), "Box", "T");

            var ofInt = box.Construct(new TypeSymbol[] { factory.Int });
            var alsoOfInt = box.Construct(new TypeSymbol[] { factory.Int });
            var ofString = box.Construct(new TypeSymbol[] { factory.String });

            Assert.Same(ofInt, alsoOfInt);
            Assert.NotSame(ofInt, ofString);
            Assert.Same(box, ofInt.Definition);
            Assert.Same(box, ofString.Definition);
        }

        [Fact]
        public void ConstructingWithTheWrongNumberOfArgumentsThrows()
        {
            var factory = new TypeSymbolFactory();
            var box = DeclareGeneric(factory, new ModuleSymbol("game.core"), "Box", "T");

            Assert.Throws<System.ArgumentException>(
                () => box.Construct(new TypeSymbol[] { factory.Int, factory.String }));
        }

        [Fact]
        public void AConstructionAndItsNullableFormShareTheDeclarationState()
        {
            var factory = new TypeSymbolFactory();
            var module = new ModuleSymbol("game.core");

            var baseType = factory.DeclareType("Container", TypeSymbolKind.Class, module);
            var box = DeclareGeneric(factory, module, "Box", "T");

            var ofInt = box.Construct(new TypeSymbol[] { factory.Int });
            var nullable = (NamedTypeSymbol)ofInt.Nullable;

            // Filled in after both were created: the declaration owns it, so both see it.
            box.BaseType = baseType;
            box.IsSealed = true;

            Assert.Same(baseType, ofInt.BaseType);
            Assert.Same(baseType, nullable.BaseType);
            Assert.True(nullable.IsSealed);
            Assert.Same(box, nullable.Definition);
            Assert.Single(nullable.TypeArguments);
        }

        [Fact]
        public void OnlyTheDeclarationMayDeclareTypeParameters()
        {
            var factory = new TypeSymbolFactory();
            var box = DeclareGeneric(factory, new ModuleSymbol("game.core"), "Box", "T");
            var ofInt = box.Construct(new TypeSymbol[] { factory.Int });

            Assert.True(box.IsDefinition);
            Assert.False(ofInt.IsDefinition);
            Assert.Throws<System.InvalidOperationException>(
                () => ofInt.SetTypeParameters(System.Array.Empty<TypeParameterSymbol>()));
        }
        #endregion

        #region Substitution
        [Fact]
        public void SubstitutionRewritesTypeParametersWhereverTheyAreNested()
        {
            var factory = new TypeSymbolFactory();
            var box = DeclareGeneric(factory, new ModuleSymbol("game.core"), "Box", "T");
            var parameter = box.TypeParameters[0];

            // {int: T[]} with T := string
            var declared = factory.Dictionary(factory.Int, factory.Array(parameter));

            var builder = factory.BeginSubstitution();
            builder.Add(parameter, factory.String);
            var substituted = builder.ToSubstitution().Apply(declared);

            Assert.Same(factory.Dictionary(factory.Int, factory.Array(factory.String)), substituted);
        }

        [Fact]
        public void SubstitutionRebuildsAConstructionsArguments()
        {
            var factory = new TypeSymbolFactory();
            var module = new ModuleSymbol("game.core");

            var box = DeclareGeneric(factory, module, "Box", "T");
            var pair = DeclareGeneric(factory, module, "Pair", "A", "B");
            var parameter = box.TypeParameters[0];

            var declared = pair.Construct(new TypeSymbol[] { factory.Int, parameter });

            var builder = factory.BeginSubstitution();
            builder.Add(parameter, factory.String);
            var substituted = builder.ToSubstitution().Apply(declared);

            Assert.Same(pair.Construct(new TypeSymbol[] { factory.Int, factory.String }), substituted);
        }

        [Fact]
        public void SubstitutionLeavesATypeWithNoParametersAlone()
        {
            var factory = new TypeSymbolFactory();
            var box = DeclareGeneric(factory, new ModuleSymbol("game.core"), "Box", "T");

            var declared = factory.Array(factory.Int);

            var builder = factory.BeginSubstitution();
            builder.Add(box.TypeParameters[0], factory.String);

            Assert.Same(declared, builder.ToSubstitution().Apply(declared));
        }

        [Fact]
        public void AConstructionCanProduceTheSubstitutionItsMembersAreReadThrough()
        {
            var factory = new TypeSymbolFactory();
            var box = DeclareGeneric(factory, new ModuleSymbol("game.core"), "Box", "T");
            var parameter = box.TypeParameters[0];

            var ofInt = box.Construct(new TypeSymbol[] { factory.Int });
            var substitution = ofInt.SubstitutionFromArguments(factory);

            Assert.Same(factory.Int, substitution.Apply(parameter));
            Assert.Same(factory.Array(factory.Int), substitution.Apply(factory.Array(parameter)));
        }

        [Fact]
        public void ATypesParameterAndAMethodsParameterAreDifferentSymbols()
        {
            var factory = new TypeSymbolFactory();
            var box = DeclareGeneric(factory, new ModuleSymbol("game.core"), "Box", "T");

            var method = new MethodSymbol("map", box, factory.Void);
            var methodParameter = factory.DeclareTypeParameter("T", method, 0);
            method.TypeParameters = new[] { methodParameter };

            var typeParameter = box.TypeParameters[0];

            // Same name, same ordinal, different declaring symbol - so substituting one must not
            // touch the other.
            Assert.NotSame(typeParameter, methodParameter);
            Assert.False(typeParameter.IsMethodTypeParameter);
            Assert.True(methodParameter.IsMethodTypeParameter);

            var builder = factory.BeginSubstitution();
            builder.Add(typeParameter, factory.String);
            var substitution = builder.ToSubstitution();

            Assert.Same(factory.String, substitution.Apply(typeParameter));
            Assert.Same(methodParameter, substitution.Apply(methodParameter));
        }
        #endregion

        #region Aliases and value classes
        [Fact]
        public void AnAliasIsNotAType()
        {
            var module = new ModuleSymbol("game.core");
            var factory = new TypeSymbolFactory();

            var alias = new AliasSymbol("EntityId", module) { Target = factory.Int };

            Assert.Equal(SymbolKind.Alias, alias.Kind);
            Assert.IsNotAssignableFrom<TypeSymbol>(alias);
            Assert.Same(factory.Int, alias.Target);
        }

        [Fact]
        public void AValueClassIsADistinctTypeFromTheOneItWraps()
        {
            var factory = new TypeSymbolFactory();
            var module = new ModuleSymbol("game.core");

            var entityId = factory.DeclareType("EntityId", TypeSymbolKind.ValueClass, module);
            entityId.UnderlyingType = factory.Int;

            Assert.NotSame(factory.Int, entityId);
            Assert.Equal(TypeSymbolKind.ValueClass, entityId.TypeKind);
            Assert.Same(factory.Int, entityId.UnderlyingType);
        }
        #endregion

        #region Display
        [Fact]
        public void DisplayStringsUseSourceSpelling()
        {
            var factory = new TypeSymbolFactory();
            var box = DeclareGeneric(factory, new ModuleSymbol("game.core"), "Box", "T");

            Assert.Equal("int[]", factory.Array(factory.Int).ToDisplayString());
            Assert.Equal("{int: string}", factory.Dictionary(factory.Int, factory.String).ToDisplayString());
            Assert.Equal("(int, float)", factory.Tuple(new TypeSymbol[] { factory.Int, factory.Float }).ToDisplayString());
            Assert.Equal(
                "(x: int, y: string)",
                factory.Tuple(new TypeSymbol[] { factory.Int, factory.String }, new string?[] { "x", "y" }).ToDisplayString());
            Assert.Equal(
                "(x: int, string)",
                factory.Tuple(new TypeSymbol[] { factory.Int, factory.String }, new string?[] { "x", null }).ToDisplayString());
            Assert.Equal(
                "(int, int) -> float",
                factory.Closure(new TypeSymbol[] { factory.Int, factory.Int }, factory.Float).ToDisplayString());

            Assert.Equal("int?", factory.Int.Nullable.ToDisplayString());
            Assert.Equal("string[]?", factory.Array(factory.String).Nullable.ToDisplayString());

            Assert.Equal("Box<T>", box.ToDisplayString());
            Assert.Equal("Box<int>", box.Construct(new TypeSymbol[] { factory.Int }).ToDisplayString());
            Assert.Equal("Box<int>?", box.Construct(new TypeSymbol[] { factory.Int }).Nullable.ToDisplayString());
        }

        [Fact]
        public void AMethodDisplaysItsWholeSignature()
        {
            var factory = new TypeSymbolFactory();
            var module = new ModuleSymbol("game.core");
            var entity = factory.DeclareType("Entity", TypeSymbolKind.Class, module);

            var method = new MethodSymbol("move", entity, factory.Void)
            {
                Parameters = new[]
                {
                    new ParameterSymbol("dx", factory.Float, 0),
                    new ParameterSymbol("dy", factory.Float, 1),
                },
            };

            Assert.Equal("move(float, float): void", method.ToDisplayString());
        }
        #endregion
    }
}
