#nullable enable

using Surtr.Runtime.Classes;

namespace Surtr.Tests.Runtime.Classes
{
    public class SurtrClassReferenceTests
    {
        #region Singletons & factories produce the expected descriptor text

        [Fact]
        public void PrimitiveSingletons_HaveTheirOneCharacterDescriptors()
        {
            Assert.Equal("I", SurtrClassReference.Integer.Descriptor);
            Assert.Equal("F", SurtrClassReference.Float.Descriptor);
            Assert.Equal("B", SurtrClassReference.Boolean.Descriptor);
            Assert.Equal("C", SurtrClassReference.Character.Descriptor);
            Assert.Equal("S", SurtrClassReference.String.Descriptor);
            Assert.Equal("E", SurtrClassReference.Erased.Descriptor);
            Assert.Equal("V", SurtrClassReference.Void.Descriptor);
        }

        [Fact]
        public void Array_NestsItsElementDescriptor()
        {
            Assert.Equal("AI", SurtrClassReference.Array(SurtrClassReference.Integer).Descriptor);
            Assert.Equal("AAI", SurtrClassReference.Array(SurtrClassReference.Array(SurtrClassReference.Integer)).Descriptor);
        }

        [Fact]
        public void Dictionary_NestsKeyThenValue()
        {
            var dict = SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.String);
            Assert.Equal("DIS", dict.Descriptor);
        }

        [Fact]
        public void Tuple_WrapsElementsInParentheses()
        {
            var tuple = SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.Float);
            Assert.Equal("T(IF)", tuple.Descriptor);
        }

        [Fact]
        public void Tuple_WithNoElements_IsTheEmptyTuple()
        {
            Assert.Equal("T()", SurtrClassReference.Tuple().Descriptor);
        }

        [Fact]
        public void Closure_WrapsParametersThenAppendsReturnType()
        {
            var closure = SurtrClassReference.Closure(SurtrClassReference.Float, SurtrClassReference.Integer, SurtrClassReference.Integer);
            Assert.Equal("L(II)F", closure.Descriptor);
        }

        [Fact]
        public void Closure_WithNoParameters_ReturningVoid()
        {
            Assert.Equal("L()V", SurtrClassReference.Closure(SurtrClassReference.Void).Descriptor);
        }

        [Fact]
        public void Object_WrapsFullNameBetweenSymbolAndTerminator()
        {
            var reference = SurtrClassReference.Object("game.core:Entity.Handle");
            Assert.Equal("Ogame.core:Entity.Handle;", reference.Descriptor);
        }

        [Fact]
        public void Native_WrapsFullNameBetweenSymbolAndTerminator()
        {
            var reference = SurtrClassReference.Native("UnityEngine:GameObject");
            Assert.Equal("NUnityEngine:GameObject;", reference.Descriptor);
        }

        [Fact]
        public void FromDescriptor_WrapsTextVerbatim_WithoutValidating()
        {
            var reference = SurtrClassReference.FromDescriptor("not a real descriptor");
            Assert.Equal("not a real descriptor", reference.Descriptor);
            Assert.False(SurtrClassReference.IsWellFormed(reference.Descriptor));
        }

        #endregion

        #region IsValid / TypeCode

        [Fact]
        public void Default_IsInvalidAndHasNoTypeCode()
        {
            SurtrClassReference reference = default;

            Assert.False(reference.IsValid);
            Assert.Equal(string.Empty, reference.Descriptor);
            Assert.Equal(SurtrValueTypeCode.Invalid, reference.TypeCode);
        }

        [Fact]
        public void FromDescriptor_Empty_IsInvalid()
        {
            Assert.False(SurtrClassReference.FromDescriptor("").IsValid);
        }

        [Theory]
        [InlineData("I", SurtrValueTypeCode.Integer)]
        [InlineData("F", SurtrValueTypeCode.Float)]
        [InlineData("B", SurtrValueTypeCode.Boolean)]
        [InlineData("C", SurtrValueTypeCode.Character)]
        [InlineData("S", SurtrValueTypeCode.String)]
        [InlineData("AI", SurtrValueTypeCode.Array)]
        [InlineData("DIS", SurtrValueTypeCode.Dictionary)]
        [InlineData("T(I)", SurtrValueTypeCode.Tuple)]
        [InlineData("L(I)F", SurtrValueTypeCode.Closure)]
        [InlineData("Ogame:Foo;", SurtrValueTypeCode.Object)]
        [InlineData("Nhost:Foo;", SurtrValueTypeCode.Native)]
        [InlineData("E", SurtrValueTypeCode.Erased)]
        [InlineData("V", SurtrValueTypeCode.Void)]
        public void TypeCode_ReadsTheLeadingSymbol(string descriptor, SurtrValueTypeCode expected)
        {
            Assert.Equal(expected, SurtrClassReference.FromDescriptor(descriptor).TypeCode);
        }

        [Fact]
        public void TypeCode_OfAnUnrecognizedSymbol_IsInvalid()
        {
            Assert.Equal(SurtrValueTypeCode.Invalid, SurtrClassReference.FromDescriptor("Z").TypeCode);
        }

        #endregion

        #region Composite accessors round-trip what the factories built

        [Fact]
        public void GetArrayElementType_UnwrapsOneLevel()
        {
            var array = SurtrClassReference.Array(SurtrClassReference.Integer);
            Assert.Equal(SurtrClassReference.Integer, array.GetArrayElementType());
        }

        [Fact]
        public void GetArrayElementType_OfANestedArray_ReturnsTheInnerArrayReference()
        {
            var inner = SurtrClassReference.Array(SurtrClassReference.Integer);
            var outer = SurtrClassReference.Array(inner);

            Assert.Equal(inner, outer.GetArrayElementType());
        }

        [Fact]
        public void GetDictionaryKeyAndValueTypes_SplitAtTheFirstDescriptorBoundary()
        {
            var dict = SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.String);

            Assert.Equal(SurtrClassReference.Integer, dict.GetDictionaryKeyType());
            Assert.Equal(SurtrClassReference.String, dict.GetDictionaryValueType());
        }

        [Fact]
        public void GetDictionaryKeyAndValueTypes_WithACompositeKey_UsesOneCharacterLookahead()
        {
            // D followed by "AI" (array<int>) then "S" (string) - only parseable because the
            // reader knows to recurse into the array rather than stop at the first character.
            var dict = SurtrClassReference.Dictionary(SurtrClassReference.Array(SurtrClassReference.Integer), SurtrClassReference.String);

            Assert.Equal("DAIS", dict.Descriptor);
            Assert.Equal(SurtrClassReference.Array(SurtrClassReference.Integer), dict.GetDictionaryKeyType());
            Assert.Equal(SurtrClassReference.String, dict.GetDictionaryValueType());
        }

        [Fact]
        public void GetTupleElementTypes_ReturnsElementsInOrder()
        {
            var tuple = SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.Float, SurtrClassReference.Boolean);
            var elements = tuple.GetTupleElementTypes();

            Assert.Equal(new[] { SurtrClassReference.Integer, SurtrClassReference.Float, SurtrClassReference.Boolean }, elements);
        }

        [Fact]
        public void GetTupleElementTypes_OfTheEmptyTuple_ReturnsNoElements()
        {
            Assert.Empty(SurtrClassReference.Tuple().GetTupleElementTypes());
        }

        [Fact]
        public void GetClosureParameterTypesAndReturnType_RoundTrip()
        {
            var closure = SurtrClassReference.Closure(SurtrClassReference.Float, SurtrClassReference.Integer, SurtrClassReference.Integer);

            Assert.Equal(new[] { SurtrClassReference.Integer, SurtrClassReference.Integer }, closure.GetClosureParameterTypes());
            Assert.Equal(SurtrClassReference.Float, closure.GetClosureReturnType());
        }

        [Fact]
        public void GetClosureParameterTypesAndReturnType_WithNoParameters()
        {
            var closure = SurtrClassReference.Closure(SurtrClassReference.Void);

            Assert.Empty(closure.GetClosureParameterTypes());
            Assert.Equal(SurtrClassReference.Void, closure.GetClosureReturnType());
        }

        [Fact]
        public void CompositeAccessors_ParseNestedCompositesInsideAList()
        {
            // A tuple of (array<int>, closure() -> void): exercises the single-character
            // lookahead across two different composite kinds inside the same element list.
            var tuple = SurtrClassReference.Tuple(
                SurtrClassReference.Array(SurtrClassReference.Integer),
                SurtrClassReference.Closure(SurtrClassReference.Void));

            Assert.Equal("T(AIL()V)", tuple.Descriptor);

            var elements = tuple.GetTupleElementTypes();
            Assert.Equal(2, elements.Length);
            Assert.Equal(SurtrClassReference.Array(SurtrClassReference.Integer), elements[0]);
            Assert.Equal(SurtrClassReference.Closure(SurtrClassReference.Void), elements[1]);
        }

        #endregion

        #region Full names

        [Fact]
        public void TryGetFullName_OnAnObjectReference_ExtractsTheNameWithoutSymbolOrTerminator()
        {
            var reference = SurtrClassReference.Object("game.core:Entity.Handle");

            Assert.True(reference.TryGetFullName(out string fullName));
            Assert.Equal("game.core:Entity.Handle", fullName);
        }

        [Fact]
        public void TryGetFullName_OnANativeReference_ExtractsTheName()
        {
            var reference = SurtrClassReference.Native("UnityEngine:GameObject");

            Assert.True(reference.TryGetFullName(out string fullName));
            Assert.Equal("UnityEngine:GameObject", fullName);
        }

        [Fact]
        public void TryGetFullName_OnAPrimitive_Fails()
        {
            Assert.False(SurtrClassReference.Integer.TryGetFullName(out string fullName));
            Assert.Equal(string.Empty, fullName);
        }

        [Fact]
        public void TryGetFullName_WithoutATerminator_Fails()
        {
            var reference = SurtrClassReference.FromDescriptor("Ogame.core:Entity");

            Assert.False(reference.TryGetFullName(out _));
        }

        [Fact]
        public void TryGetFullName_WithAnEmptyName_Fails()
        {
            var reference = SurtrClassReference.FromDescriptor("O;");

            Assert.False(reference.TryGetFullName(out _));
        }

        [Theory]
        [InlineData("game.core:Entity.Handle", "game.core", "Entity.Handle")]
        [InlineData(":Entity", "", "Entity")]
        [InlineData("game.core:", "game.core", "")]
        public void TrySplitFullName_SplitsAtTheModuleSeparator(string fullName, string expectedModule, string expectedType)
        {
            Assert.True(SurtrClassReference.TrySplitFullName(fullName, out string modulePath, out string typePath));
            Assert.Equal(expectedModule, modulePath);
            Assert.Equal(expectedType, typePath);
        }

        [Fact]
        public void TrySplitFullName_WithNoSeparator_ReturnsFalseAndTheWholeNameAsTypePath()
        {
            Assert.False(SurtrClassReference.TrySplitFullName("NoModule", out string modulePath, out string typePath));
            Assert.Equal(string.Empty, modulePath);
            Assert.Equal("NoModule", typePath);
        }

        #endregion

        #region SkipDescriptor / IsWellFormed

        [Theory]
        [InlineData("I")]
        [InlineData("F")]
        [InlineData("B")]
        [InlineData("C")]
        [InlineData("S")]
        [InlineData("E")]
        [InlineData("V")]
        [InlineData("AI")]
        [InlineData("AAI")]
        [InlineData("DIS")]
        [InlineData("DAIS")]
        [InlineData("T()")]
        [InlineData("T(IF)")]
        [InlineData("L()V")]
        [InlineData("L(II)F")]
        [InlineData("Ogame.core:Entity.Handle;")]
        [InlineData("Nhost:Type;")]
        public void IsWellFormed_AcceptsEachValidShape(string descriptor)
        {
            Assert.True(SurtrClassReference.IsWellFormed(descriptor));
        }

        [Theory]
        [InlineData("")]
        [InlineData("Z")]
        [InlineData("A")]
        [InlineData("DI")]
        [InlineData("T(I")]
        [InlineData("L(I")]
        [InlineData("L(I)")]
        [InlineData("Ogame.core:Entity")]
        [InlineData("Nhost:Type")]
        [InlineData("II")]
        [InlineData("AI ")]
        public void IsWellFormed_RejectsEachMalformedOrTrailingShape(string descriptor)
        {
            Assert.False(SurtrClassReference.IsWellFormed(descriptor));
        }

        [Fact]
        public void SkipDescriptor_OutOfRangeIndex_ReturnsNegativeOne()
        {
            Assert.Equal(-1, SurtrClassReference.SkipDescriptor("I", 5));
            Assert.Equal(-1, SurtrClassReference.SkipDescriptor("I", -1));
        }

        [Fact]
        public void SkipDescriptor_StopsAtTheDescriptorBoundary_IgnoringWhatFollows()
        {
            // Two back-to-back primitives: skipping the first must stop after it, not consume
            // the second - this is exactly the one-character-lookahead property the composite
            // accessors depend on.
            Assert.Equal(1, SurtrClassReference.SkipDescriptor("IF", 0));
        }

        #endregion

        #region ToDisplayString (diagnostics only - never the comparison key)

        [Theory]
        [InlineData("I", "int")]
        [InlineData("F", "float")]
        [InlineData("B", "bool")]
        [InlineData("C", "char")]
        [InlineData("S", "string")]
        [InlineData("E", "?")]
        [InlineData("V", "void")]
        public void ToDisplayString_OfAPrimitive(string descriptor, string expected)
        {
            Assert.Equal(expected, SurtrClassReference.FromDescriptor(descriptor).ToDisplayString());
        }

        [Fact]
        public void ToDisplayString_OfAnArray_AppendsBrackets()
        {
            Assert.Equal("int[]", SurtrClassReference.Array(SurtrClassReference.Integer).ToDisplayString());
        }

        [Fact]
        public void ToDisplayString_OfADictionary_UsesBraceColonSyntax()
        {
            var dict = SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.String);
            Assert.Equal("{int: string}", dict.ToDisplayString());
        }

        [Fact]
        public void ToDisplayString_OfATuple_UsesCommaSeparatedParens()
        {
            var tuple = SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.Float);
            Assert.Equal("(int, float)", tuple.ToDisplayString());
        }

        [Fact]
        public void ToDisplayString_OfTheEmptyTuple()
        {
            Assert.Equal("()", SurtrClassReference.Tuple().ToDisplayString());
        }

        [Fact]
        public void ToDisplayString_OfAClosure_UsesArrowSyntax()
        {
            var closure = SurtrClassReference.Closure(SurtrClassReference.Float, SurtrClassReference.Integer, SurtrClassReference.Integer);
            Assert.Equal("(int, int) -> float", closure.ToDisplayString());
        }

        [Fact]
        public void ToDisplayString_OfAnObjectReference_ShowsTheFullNameVerbatim()
        {
            var reference = SurtrClassReference.Object("game.core:Entity.Handle");
            Assert.Equal("game.core:Entity.Handle", reference.ToDisplayString());
        }

        #endregion

        #region Equality & hashing use the canonical descriptor, not display form

        [Fact]
        public void References_WithTheSameDescriptorText_AreEqual_RegardlessOfConstructionPath()
        {
            SurtrClassReference viaSingleton = SurtrClassReference.Integer;
            SurtrClassReference viaFromDescriptor = SurtrClassReference.FromDescriptor("I");

            Assert.Equal(viaSingleton, viaFromDescriptor);
            Assert.True(viaSingleton == viaFromDescriptor);
            Assert.False(viaSingleton != viaFromDescriptor);
            Assert.Equal(viaSingleton.GetHashCode(), viaFromDescriptor.GetHashCode());
        }

        [Fact]
        public void References_WithDifferentDescriptorText_AreNotEqual()
        {
            Assert.NotEqual(SurtrClassReference.Integer, SurtrClassReference.Float);
            Assert.True(SurtrClassReference.Integer != SurtrClassReference.Float);
        }

        [Fact]
        public void ComparisonIsOrdinal_NotCaseInsensitive()
        {
            var lower = SurtrClassReference.FromDescriptor("ogame:foo;");
            var upper = SurtrClassReference.FromDescriptor("Ogame:foo;");

            Assert.NotEqual(lower, upper);
        }

        [Fact]
        public void Default_Equals_Default()
        {
            SurtrClassReference left = default;
            SurtrClassReference right = default;

            Assert.Equal(left, right);
            Assert.Equal(0, left.GetHashCode());
        }

        [Fact]
        public void Default_DoesNotEqual_FromDescriptorOfEmptyString()
        {
            // Both are "invalid" (IsValid is false for both, since it treats null and "" alike),
            // but Equals compares the underlying string with ordinal semantics, where null and ""
            // are distinct - so equality is strictly narrower than "both invalid".
            SurtrClassReference left = default;
            SurtrClassReference right = SurtrClassReference.FromDescriptor("");

            Assert.False(left.IsValid);
            Assert.False(right.IsValid);
            Assert.NotEqual(left, right);
        }

        #endregion
    }
}
