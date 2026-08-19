#nullable enable

using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.CodeGen;
using System;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// Covers the one gate between the symbol model and the runtime's type encoding. The cases
    /// worth having are the ones where the descriptor deliberately loses what the binder kept:
    /// a nullable reference, a value class, and a generic method's type parameter.
    /// </summary>
    public sealed class DescriptorEmitterTests
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

        #region Built-ins
        [Fact]
        public void ThePrimitivesEmitTheirOwnSymbol()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            Assert.Equal("I", emitter.EmitDescriptor(factory.Int));
            Assert.Equal("F", emitter.EmitDescriptor(factory.Float));
            Assert.Equal("B", emitter.EmitDescriptor(factory.Bool));
            Assert.Equal("C", emitter.EmitDescriptor(factory.Char));
        }

        [Fact]
        public void TheReferenceBuiltInsEmitTheirOwnSymbol()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            Assert.Equal("S", emitter.EmitDescriptor(factory.String));
            Assert.Equal("R", emitter.EmitDescriptor(factory.Range));
            Assert.Equal("V", emitter.EmitDescriptor(factory.Void));
        }

        [Fact]
        public void UnknownEmitsTheErasedSymbol()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            Assert.Equal("E", emitter.EmitDescriptor(factory.Unknown));
        }

        [Fact]
        public void CompositesNest()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            Assert.Equal("AI", emitter.EmitDescriptor(factory.Array(factory.Int)));
            Assert.Equal("AAS", emitter.EmitDescriptor(factory.Array(factory.Array(factory.String))));
            Assert.Equal("DIS", emitter.EmitDescriptor(factory.Dictionary(factory.Int, factory.String)));
            Assert.Equal("DIAS", emitter.EmitDescriptor(factory.Dictionary(factory.Int, factory.Array(factory.String))));

            Assert.Equal("T(IF)", emitter.EmitDescriptor(factory.Tuple(new TypeSymbol[] { factory.Int, factory.Float })));
            Assert.Equal(
                "L(II)F",
                emitter.EmitDescriptor(factory.Closure(new TypeSymbol[] { factory.Int, factory.Int }, factory.Float)));
            Assert.Equal("L()V", emitter.EmitDescriptor(factory.Closure(Array.Empty<TypeSymbol>(), factory.Void)));
        }
        #endregion

        #region Nullability
        [Fact]
        public void ANullablePrimitiveKeepsItsMarker()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            Assert.Equal("?I", emitter.EmitDescriptor(factory.Int.Nullable));
            Assert.Equal("?F", emitter.EmitDescriptor(factory.Float.Nullable));
            Assert.Equal("?B", emitter.EmitDescriptor(factory.Bool.Nullable));
            Assert.Equal("?C", emitter.EmitDescriptor(factory.Char.Nullable));
        }

        [Fact]
        public void ANullableReferenceIsIndistinguishableFromANonNullableOne()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();
            var module = new ModuleSymbol("game.core");
            var entity = factory.DeclareType("Entity", TypeSymbolKind.Class, module);

            // A reference is its payload and null is already representable, so `?` adds nothing -
            // which is exactly why the binder cannot use descriptors as its types.
            Assert.Equal("S", emitter.EmitDescriptor(factory.String.Nullable));
            Assert.Equal("AI", emitter.EmitDescriptor(factory.Array(factory.Int).Nullable));
            Assert.Equal("Ogame.core:Entity;", emitter.EmitDescriptor(entity.Nullable));

            Assert.NotSame(factory.String, factory.String.Nullable);
        }

        [Fact]
        public void ANullablePrimitiveInsideACompositeStillCarriesItsMarker()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            Assert.Equal("A?I", emitter.EmitDescriptor(factory.Array(factory.Int.Nullable)));
        }
        #endregion

        #region Named types
        [Fact]
        public void ANonGenericTypeEmitsExactlyTheDescriptorItAlwaysDid()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();
            var module = new ModuleSymbol("game.core");

            var entity = factory.DeclareType("Entity", TypeSymbolKind.Class, module);
            var handle = factory.DeclareType("Handle", TypeSymbolKind.Class, module, entity);

            Assert.Equal("Ogame.core:Entity;", emitter.EmitDescriptor(entity));
            Assert.Equal("Ogame.core:Entity.Handle;", emitter.EmitDescriptor(handle));
        }

        [Fact]
        public void ANativeTypeEmitsTheNativeSymbol()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            var gameObject = factory.DeclareType(
                "GameObject", TypeSymbolKind.Native, new ModuleSymbol("UnityEngine"));

            Assert.Equal("NUnityEngine:GameObject;", emitter.EmitDescriptor(gameObject));
        }
        #endregion

        #region Generics
        [Fact]
        public void ArityIsMangledIntoTheNameAndArgumentsFollowTheTerminator()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();
            var module = new ModuleSymbol("box");

            var box = DeclareGeneric(factory, module, "Box", "T");

            Assert.Equal("Obox:Box`1;I", emitter.EmitDescriptor(box.Construct(new TypeSymbol[] { factory.Int })));
            Assert.Equal("Obox:Box`1;S", emitter.EmitDescriptor(box.Construct(new TypeSymbol[] { factory.String })));
        }

        [Fact]
        public void TwoAritiesOfOneNameEmitDifferentDescriptors()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();
            var module = new ModuleSymbol("box");

            var one = DeclareGeneric(factory, module, "Result", "T");
            var two = DeclareGeneric(factory, module, "Result", "T", "E");

            Assert.Equal("Obox:Result`1;I", emitter.EmitDescriptor(one.Construct(new TypeSymbol[] { factory.Int })));
            Assert.Equal(
                "Obox:Result`2;IS",
                emitter.EmitDescriptor(two.Construct(new TypeSymbol[] { factory.Int, factory.String })));
        }

        [Fact]
        public void TypeArgumentsNestWithoutBracketsOrCounts()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();
            var module = new ModuleSymbol("box");

            var box = DeclareGeneric(factory, module, "Box", "T");
            var pair = DeclareGeneric(factory, module, "Pair", "A", "B");

            var nested = pair.Construct(new TypeSymbol[]
            {
                factory.Int,
                box.Construct(new TypeSymbol[] { factory.String }),
            });

            // The arity in each name is what tells a reader how many descriptors to expect, so the
            // whole thing still parses left to right with one character of lookahead.
            Assert.Equal("Obox:Pair`2;IObox:Box`1;S", emitter.EmitDescriptor(nested));
        }

        [Fact]
        public void AnOpenDeclarationEmitsItsOwnParameters()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            var box = DeclareGeneric(factory, new ModuleSymbol("box"), "Box", "T");

            Assert.Equal("Obox:Box`1;G0", emitter.EmitDescriptor(box));
        }

        [Fact]
        public void ATypeParameterEmitsItsPositionAndAMethodsDoesToo()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            var box = DeclareGeneric(factory, new ModuleSymbol("box"), "Box", "T", "U");

            Assert.Equal("G0", emitter.EmitDescriptor(box.TypeParameters[0]));
            Assert.Equal("G1", emitter.EmitDescriptor(box.TypeParameters[1]));

            // A method's parameters keep their position under their own symbol, so an importer can
            // tell "the declaring method's first parameter" from "the declaring type's first" —
            // which is what lets a call site's inference and constraints survive the image.
            var method = new MethodSymbol("map", box, factory.Void);
            var methodParameter = factory.DeclareTypeParameter("R", method, 0);

            Assert.Equal("H0", emitter.EmitDescriptor(methodParameter));
        }

        [Fact]
        public void ATypeParameterInsideACompositeStillEmitsItsPosition()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            var box = DeclareGeneric(factory, new ModuleSymbol("box"), "Box", "T");
            var parameter = box.TypeParameters[0];

            Assert.Equal("AG0", emitter.EmitDescriptor(factory.Array(parameter)));
            Assert.Equal("DIAG0", emitter.EmitDescriptor(factory.Dictionary(factory.Int, factory.Array(parameter))));
        }

        [Fact]
        public void AMethodParameterInsideACompositeStillEmitsItsPosition()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            var box = factory.DeclareType("Box", TypeSymbolKind.Class, new ModuleSymbol("box"));
            var method = new MethodSymbol("map", box, factory.Void);
            var parameter = factory.DeclareTypeParameter("R", method, 0);

            Assert.Equal("AH0", emitter.EmitDescriptor(factory.Array(parameter)));
            Assert.Equal(
                "DIAH0",
                emitter.EmitDescriptor(factory.Dictionary(factory.Int, factory.Array(parameter))));
            Assert.Equal(
                "L(H0)V",
                emitter.EmitDescriptor(factory.Closure(new[] { parameter }, factory.Void)));
        }
        #endregion

        #region Value classes
        [Fact]
        public void AValueClassErasesToTheFieldItWraps()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();
            var module = new ModuleSymbol("game.core");

            var entityId = factory.DeclareType("EntityId", TypeSymbolKind.ValueClass, module);
            entityId.UnderlyingType = factory.Int;

            Assert.Equal("I", emitter.EmitDescriptor(entityId));

            // The nullability rides along onto the erased form: `EntityId?` over an int is `int?`.
            Assert.Equal("?I", emitter.EmitDescriptor(entityId.Nullable));
        }

        [Fact]
        public void AValueClassWrappingAValueClassErasesAllTheWayDown()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();
            var module = new ModuleSymbol("game.core");

            var entityId = factory.DeclareType("EntityId", TypeSymbolKind.ValueClass, module);
            entityId.UnderlyingType = factory.Int;

            var playerId = factory.DeclareType("PlayerId", TypeSymbolKind.ValueClass, module);
            playerId.UnderlyingType = entityId;

            Assert.Equal("I", emitter.EmitDescriptor(playerId));
        }

        [Fact]
        public void AValueClassHasABoxedFormThatIsARealClass()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();
            var module = new ModuleSymbol("game.core");

            var entityId = factory.DeclareType("EntityId", TypeSymbolKind.ValueClass, module);
            entityId.UnderlyingType = factory.Int;

            // Where it flows into a slot that holds a reference, something has to be the reference.
            Assert.Equal("Ogame.core:EntityId;", emitter.EmitBoxedForm(entityId).Descriptor);
        }

        [Fact]
        public void OnlyAValueClassHasABoxedForm()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            var entity = factory.DeclareType("Entity", TypeSymbolKind.Class, new ModuleSymbol("game.core"));

            Assert.Throws<ArgumentException>(() => emitter.EmitBoxedForm(entity));
        }

        [Fact]
        public void AValueClassWithNoBoundFieldCannotBeEmitted()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            var entityId = factory.DeclareType("EntityId", TypeSymbolKind.ValueClass, new ModuleSymbol("game.core"));

            Assert.Throws<InvalidOperationException>(() => emitter.EmitDescriptor(entityId));
        }
        #endregion

        #region Contract
        [Fact]
        public void TheErrorTypeNeverReachesEmission()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            Assert.Throws<InvalidOperationException>(() => emitter.EmitDescriptor(factory.Error("Missing")));
        }

        [Fact]
        public void EmittingTheSameTypeTwiceGivesTheSameReference()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();

            var type = factory.Dictionary(factory.Int, factory.Array(factory.String));

            Assert.Equal(emitter.Emit(type).Descriptor, emitter.Emit(type).Descriptor);
        }

        [Fact]
        public void TypesTheBinderKeepsApartCanStillShareADescriptor()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();
            var module = new ModuleSymbol("game.core");

            var entityId = factory.DeclareType("EntityId", TypeSymbolKind.ValueClass, module);
            entityId.UnderlyingType = factory.Int;

            var box = DeclareGeneric(factory, module, "Box", "T");

            // Three pairs the type checker must keep apart, all collapsing at emit. This is the
            // whole reason the binder has its own model rather than using descriptors.
            Assert.NotSame(factory.Int, entityId);
            Assert.Equal(emitter.EmitDescriptor(factory.Int), emitter.EmitDescriptor(entityId));

            Assert.NotSame(factory.String, factory.String.Nullable);
            Assert.Equal(emitter.EmitDescriptor(factory.String), emitter.EmitDescriptor(factory.String.Nullable));

            Assert.NotSame(factory.Unknown, box.TypeParameters[0]);
            Assert.Equal("E", emitter.EmitDescriptor(factory.Unknown));
        }
        #endregion
    }
}
