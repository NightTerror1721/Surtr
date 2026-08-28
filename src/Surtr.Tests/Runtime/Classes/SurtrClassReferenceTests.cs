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
            Assert.Equal("G0", SurtrClassReference.GenericParameter(0).Descriptor);
            Assert.Equal("H0", SurtrClassReference.MethodGenericParameter(0).Descriptor);
            Assert.Equal("?I", SurtrClassReference.Nullable(SurtrClassReference.Integer).Descriptor);
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
        [InlineData("X", SurtrValueTypeCode.Bytes)]
        [InlineData("AI", SurtrValueTypeCode.Array)]
        [InlineData("DIS", SurtrValueTypeCode.Dictionary)]
        [InlineData("T(I)", SurtrValueTypeCode.Tuple)]
        [InlineData("L(I)F", SurtrValueTypeCode.Closure)]
        [InlineData("Ogame:Foo;", SurtrValueTypeCode.Object)]
        [InlineData("Nhost:Foo;", SurtrValueTypeCode.Native)]
        [InlineData("E", SurtrValueTypeCode.Erased)]
        [InlineData("G0", SurtrValueTypeCode.Erased)]
        [InlineData("H0", SurtrValueTypeCode.Erased)]
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
        [InlineData("G0")]
        [InlineData("H0")]
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
        [InlineData("G")]
        [InlineData("H")]
        [InlineData("Hx")]
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
        [InlineData("X", "bytes")]
        [InlineData("E", "unknown")]
        [InlineData("V", "void")]
        [InlineData("G0", "T0")]
        [InlineData("H0", "T0")]
        [InlineData("?I", "int?")]
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

        #region Method generic parameters (H<n>)
        // docs/Runtime-Model.md §3.1: a signature mentions a method's own type parameter through
        // H<n>, distinct from the declaring type's G<n> so the two can never be confused.

        [Fact]
        public void MethodGenericParameter_WritesTheIndexAndRoundsTrip()
        {
            var parameter = SurtrClassReference.MethodGenericParameter(2);

            Assert.Equal("H2", parameter.Descriptor);
            Assert.Equal(SurtrValueTypeCode.Erased, parameter.TypeCode);
            Assert.True(SurtrClassReference.IsWellFormed(parameter.Descriptor));
        }

        [Theory]
        [InlineData("H0", 0)]
        [InlineData("H1", 1)]
        [InlineData("H9", 9)]
        public void TryGetMethodGenericParameterIndex_ReadsSingleDigitIndices(string descriptor, int expected)
        {
            Assert.True(SurtrClassReference.FromDescriptor(descriptor).TryGetMethodGenericParameterIndex(out int index));
            Assert.Equal(expected, index);
        }

        [Theory]
        [InlineData("G0")]
        [InlineData("I")]
        [InlineData("E")]
        [InlineData("H")]
        [InlineData("Hx")]
        [InlineData("AH0")]
        public void TryGetMethodGenericParameterIndex_FailsOnAnythingElse(string descriptor)
        {
            Assert.False(SurtrClassReference.FromDescriptor(descriptor).TryGetMethodGenericParameterIndex(out int index));
            Assert.Equal(-1, index);
        }

        [Fact]
        public void GenericParameterAndMethodGenericParameter_AreDistinctDescriptors()
        {
            var typeParameter = SurtrClassReference.GenericParameter(0);
            var methodParameter = SurtrClassReference.MethodGenericParameter(0);

            Assert.NotEqual(typeParameter, methodParameter);
            Assert.True(typeParameter.TryGetGenericParameterIndex(out int _));
            Assert.False(methodParameter.TryGetGenericParameterIndex(out int _));
        }

        [Fact]
        public void Erase_MethodParameter_BecomesTheSameErasedDescriptorAsATypes()
        {
            // H0 erases to E exactly as G0 does - a signature key or a slot layout sees the same
            // thing either way, which is the whole point of the two being distinct forms of one
            // idea (docs/Compiler-Plan.md §8).
            Assert.Equal(
                SurtrClassReference.Erase(SurtrClassReference.MethodGenericParameter(0)),
                SurtrClassReference.Erase(SurtrClassReference.GenericParameter(0)));
            Assert.Equal("E", SurtrClassReference.Erase(SurtrClassReference.MethodGenericParameter(0)).Descriptor);
        }

        [Theory]
        [InlineData("H0", "E")]
        [InlineData("AH0", "AE")]
        [InlineData("DIS", "DIS")]
        [InlineData("L(H0)V", "L(E)V")]
        [InlineData("T(IG0H0)", "T(IEE)")]
        [InlineData("Ogame:Box`1;H0", "Ogame:Box`1;E")]
        [InlineData("Nhost:Foo;G0H0", "Nhost:Foo;EE")]
        public void Erase_RewritesEveryParameterInTheDescriptorButNeverInsideAFullName(string descriptor, string expected)
        {
            Assert.Equal(expected, SurtrClassReference.Erase(SurtrClassReference.FromDescriptor(descriptor)).Descriptor);
        }

        [Fact]
        public void Erase_LeavesAFullNameThatMerelyContainsHUntouched()
        {
            // "H0" as a type name inside a full name is not a parameter mention - the eraser
            // skips the full name of O/N descriptors whole, exactly as it does for G.
            Assert.Equal(
                "Ogame:H0;",
                SurtrClassReference.Erase(SurtrClassReference.FromDescriptor("Ogame:H0;")).Descriptor);
        }

        [Fact]
        public void ToDisplayString_OfAMethodParameter_ReadsLikeAnyTypeParameter()
        {
            Assert.Equal("T0", SurtrClassReference.MethodGenericParameter(0).ToDisplayString());
        }

        [Theory]
        [InlineData("G0", true)]
        [InlineData("H2", true)]
        [InlineData("AG0", true)]
        [InlineData("L(H0)V", true)]
        [InlineData("Obox:Box`1;G0", true)]
        [InlineData("Obox:Box`1;H0", true)]
        [InlineData("Obox:Pair`2;IObox:Box`1;G0", true)]
        [InlineData("I", false)]
        [InlineData("S", false)]
        [InlineData("AI", false)]
        [InlineData("DIS", false)]
        [InlineData("Obox:Box`1;I", false)]
        [InlineData("Obox:Box`1;S", false)]
        [InlineData("Ogame:H0;", false)]
        public void ContainsOpenParameter_DetectsAParameterMentionAnywhereButAFullName(string descriptor, bool expected)
        {
            Assert.Equal(expected, SurtrClassReference.FromDescriptor(descriptor).ContainsOpenParameter());
        }

        #endregion

        #region Generic types
        // docs/Compiler-Plan.md §8: arity is part of a type's identity and is mangled into its
        // name; the type arguments follow the name terminator with neither brackets nor a count,
        // because the arity already said how many to expect.

        [Fact]
        public void MangleArity_LeavesANonGenericNameAlone()
        {
            Assert.Equal("Entity", SurtrClassReference.MangleArity("Entity", 0));
            Assert.Equal("Box`1", SurtrClassReference.MangleArity("Box", 1));
            Assert.Equal("Pair`2", SurtrClassReference.MangleArity("Pair", 2));
        }

        [Fact]
        public void ArityOf_ReadsOnlyTheLastSegment()
        {
            // A type nested inside a generic one does not see its container's parameters, so only
            // the segment being named counts and the earlier ones are qualification.
            Assert.Equal(0, SurtrClassReference.ArityOf("game.core:Entity"));
            Assert.Equal(1, SurtrClassReference.ArityOf("box:Box`1"));
            Assert.Equal(2, SurtrClassReference.ArityOf("box:Pair`2"));
            Assert.Equal(0, SurtrClassReference.ArityOf("box:Box`1.Entry"));
            Assert.Equal(1, SurtrClassReference.ArityOf("box:Box`1.Pair`1"));
        }

        [Fact]
        public void Constructed_WritesTheArgumentsAfterTheTerminator()
        {
            Assert.Equal(
                "Obox:Box`1;I",
                SurtrClassReference.Constructed("box:Box`1", SurtrClassReference.Integer).Descriptor);

            Assert.Equal(
                "Obox:Pair`2;IS",
                SurtrClassReference.Constructed("box:Pair`2", SurtrClassReference.Integer, SurtrClassReference.String)
                    .Descriptor);
        }

        [Fact]
        public void Constructed_RejectsAnArgumentCountTheNameDisagreesWith()
        {
            Assert.Throws<System.ArgumentException>(
                () => SurtrClassReference.Constructed("box:Box`1"));

            Assert.Throws<System.ArgumentException>(
                () => SurtrClassReference.Constructed(
                    "box:Box`1", SurtrClassReference.Integer, SurtrClassReference.String));
        }

        [Fact]
        public void ANestedTypeInAGenericContainerTakesNoArgumentsOfItsOwn()
        {
            var entry = SurtrClassReference.Constructed("box:Box`1.Entry");

            Assert.Equal("Obox:Box`1.Entry;", entry.Descriptor);
            Assert.True(SurtrClassReference.IsWellFormed(entry.Descriptor));
            Assert.Equal(0, entry.GenericArity);
        }

        [Fact]
        public void AConstructedDescriptorIsWellFormedAndSkipsWhole()
        {
            // The point of putting arity in the name: this parses left to right with one character
            // of lookahead, and a nested construction ends exactly where its arguments do.
            const string nested = "Obox:Pair`2;IObox:Box`1;S";

            Assert.True(SurtrClassReference.IsWellFormed(nested));
            Assert.Equal(nested.Length, SurtrClassReference.SkipDescriptor(nested, 0));

            // The same construction as an array element leaves the array's descriptor well formed.
            Assert.True(SurtrClassReference.IsWellFormed("A" + nested));
        }

        [Fact]
        public void AMissingArgumentMakesTheDescriptorMalformed()
        {
            // A name that promises one argument and supplies none is not an "open" form - it is
            // unreadable, which is why a generic contract names itself with its own parameter.
            Assert.False(SurtrClassReference.IsWellFormed("Obox:Box`1;"));
            Assert.False(SurtrClassReference.IsWellFormed("Obox:Pair`2;I"));
            Assert.False(SurtrClassReference.IsWellFormed("Obox:Box`;I"));
        }

        [Fact]
        public void GetTypeArguments_ReturnsEachArgumentWhole()
        {
            var pair = SurtrClassReference.FromDescriptor("Obox:Pair`2;IObox:Box`1;S");

            Assert.Equal(2, pair.GenericArity);

            var arguments = pair.GetTypeArguments();
            Assert.Equal(2, arguments.Length);
            Assert.Equal("I", arguments[0].Descriptor);
            Assert.Equal("Obox:Box`1;S", arguments[1].Descriptor);
        }

        [Fact]
        public void TwoConstructionsOfOneDeclarationShareAFullName()
        {
            var ofInt = SurtrClassReference.Constructed("box:Box`1", SurtrClassReference.Integer);
            var ofString = SurtrClassReference.Constructed("box:Box`1", SurtrClassReference.String);

            // Different descriptors, so they are different types to a signature - but one name, so
            // both resolve to the same SurtrClass. Nothing is reified.
            Assert.NotEqual(ofInt, ofString);

            Assert.True(ofInt.TryGetFullName(out string left));
            Assert.True(ofString.TryGetFullName(out string right));
            Assert.Equal(left, right);
        }

        [Fact]
        public void ADisplayStringHidesTheManglingAndShowsTheArguments()
        {
            Assert.Equal(
                "box:Box<int>",
                SurtrClassReference.Constructed("box:Box`1", SurtrClassReference.Integer).ToDisplayString());

            Assert.Equal(
                "box:Pair<int, string>",
                SurtrClassReference
                    .Constructed("box:Pair`2", SurtrClassReference.Integer, SurtrClassReference.String)
                    .ToDisplayString());

            Assert.Equal(
                "box:Box<box:Box<string>>",
                SurtrClassReference.Constructed(
                    "box:Box`1",
                    SurtrClassReference.Constructed("box:Box`1", SurtrClassReference.String)).ToDisplayString());

            Assert.Equal(
                "box:Box.Entry",
                SurtrClassReference.Constructed("box:Box`1.Entry").ToDisplayString());
        }

        [Fact]
        public void ANonGenericDescriptorIsUnchangedByAllOfThis()
        {
            var entity = SurtrClassReference.Object("game.core:Entity");

            Assert.Equal("Ogame.core:Entity;", entity.Descriptor);
            Assert.Equal(0, entity.GenericArity);
            Assert.Empty(entity.GetTypeArguments());
            Assert.True(SurtrClassReference.IsWellFormed(entity.Descriptor));
        }
        #endregion
    }
}
