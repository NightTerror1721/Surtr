#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.Runtime.BuiltIns
{
    /// <summary>
    /// Covers the generic parameters the collection built-ins declare and the element-polymorphic
    /// members that were undeclarable without them.
    /// </summary>
    public class SurtrBuiltInGenericsTests
    {
        private static SurtrMethodInfo MemberOf(SurtrClass type, string name)
        {
            Assert.True(type.TryGetMethods(name, out var overloads), $"'{type.Name}' declares no member named '{name}'.");
            Assert.Single(overloads);
            return overloads[0];
        }

        #region The descriptor form

        [Fact]
        public void AGenericParameterDescriptor_IsTheSymbolAndOneDigit()
        {
            Assert.Equal("G0", SurtrClassReference.GenericParameter(0).Descriptor);
            Assert.Equal("G9", SurtrClassReference.GenericParameter(9).Descriptor);
            Assert.Throws<ArgumentOutOfRangeException>(() => SurtrClassReference.GenericParameter(10));
        }

        [Fact]
        public void AGenericParameter_ReportsItsIndexAndErasesForLayout()
        {
            var parameter = SurtrClassReference.GenericParameter(1);

            Assert.True(parameter.TryGetGenericParameterIndex(out int index));
            Assert.Equal(1, index);

            // Erased is what it *is* at run time: a reference slot, traced like any other.
            Assert.Equal(SurtrValueTypeCode.Erased, parameter.TypeCode);
            Assert.True(parameter.TypeCode.IsReferenceType);
        }

        [Fact]
        public void APlainDescriptor_IsNotAGenericParameter()
        {
            Assert.False(SurtrClassReference.Integer.TryGetGenericParameterIndex(out _));
            Assert.False(SurtrClassReference.Erased.TryGetGenericParameterIndex(out _));
        }

        [Fact]
        public void AGenericParameter_NestsInsideACompositeDescriptor()
        {
            var arrayOfT = SurtrClassReference.Array(SurtrClassReference.GenericParameter(0));

            Assert.Equal("AG0", arrayOfT.Descriptor);
            Assert.True(SurtrClassReference.IsWellFormed(arrayOfT.Descriptor));
            Assert.Equal("G0", arrayOfT.GetArrayElementType().Descriptor);
        }

        [Fact]
        public void AGenericParameterWithoutItsDigit_IsMalformed()
        {
            Assert.False(SurtrClassReference.IsWellFormed("G"));
            Assert.False(SurtrClassReference.IsWellFormed("GX"));
        }

        #endregion

        #region What the built-ins declare

        [Fact]
        public void ArrayAndDictionary_DeclareTheirParameters()
        {
            SurtrBuiltIns.EnsureBuilt();

            Assert.Equal(1, SurtrBuiltIns.Array.GenericParameterCount);
            Assert.Equal("T", SurtrBuiltIns.Array.GenericParameters[0]);

            Assert.Equal(2, SurtrBuiltIns.Dictionary.GenericParameterCount);
            Assert.Equal("K", SurtrBuiltIns.Dictionary.GenericParameters[0]);
            Assert.Equal("V", SurtrBuiltIns.Dictionary.GenericParameters[1]);
        }

        [Fact]
        public void TupleAndClosure_DeclareNoneBecauseTheirParameterisationIsVariadic()
        {
            SurtrBuiltIns.EnsureBuilt();

            Assert.Equal(0, SurtrBuiltIns.Tuple.GenericParameterCount);
            Assert.Equal(0, SurtrBuiltIns.Closure.GenericParameterCount);
        }

        [Fact]
        public void ArrayMembers_NameTheElementType()
        {
            SurtrBuiltIns.EnsureBuilt();

            Assert.Equal("G0", MemberOf(SurtrBuiltIns.Array, "get").ReturnType.Reference.Descriptor);
            Assert.Equal("G0", MemberOf(SurtrBuiltIns.Array, "pop").ReturnType.Reference.Descriptor);
            Assert.Equal("G0", MemberOf(SurtrBuiltIns.Array, "push").Parameters[0].ParameterType.Reference.Descriptor);

            // The declarations keep G0; the signature key erases it, since G0 and E are the same
            // slot once the compiler has checked the parameterisation away.
            Assert.Equal("push(E)", MemberOf(SurtrBuiltIns.Array, "push").SignatureKey());
            Assert.Equal("set(IE)", MemberOf(SurtrBuiltIns.Array, "set").SignatureKey());
            Assert.Equal("indexOf(E)", MemberOf(SurtrBuiltIns.Array, "indexOf").SignatureKey());
        }

        [Fact]
        public void DictionaryMembers_NameBothParameters()
        {
            SurtrBuiltIns.EnsureBuilt();

            Assert.Equal("G0", MemberOf(SurtrBuiltIns.Dictionary, "get").Parameters[0].ParameterType.Reference.Descriptor);
            Assert.Equal("G1", MemberOf(SurtrBuiltIns.Dictionary, "get").ReturnType.Reference.Descriptor);
            Assert.Equal("get(E)", MemberOf(SurtrBuiltIns.Dictionary, "get").SignatureKey());
            Assert.Equal("set(EE)", MemberOf(SurtrBuiltIns.Dictionary, "set").SignatureKey());
            Assert.Equal("AG0", MemberOf(SurtrBuiltIns.Dictionary, "keys").ReturnType.Reference.Descriptor);
            Assert.Equal("AG1", MemberOf(SurtrBuiltIns.Dictionary, "values").ReturnType.Reference.Descriptor);
        }

        [Fact]
        public void EveryCollection_AnswersItsSizeUnderTheSameName()
        {
            SurtrBuiltIns.EnsureBuilt();

            Assert.True(SurtrBuiltIns.Array.TryGetProperty("length", out _));
            Assert.True(SurtrBuiltIns.Tuple.TryGetProperty("length", out _));
            Assert.True(SurtrBuiltIns.String.TryGetProperty("length", out _));
            Assert.True(SurtrBuiltIns.Dictionary.TryGetProperty("length", out _));

            // And no leftover second spelling of the same question.
            Assert.False(SurtrBuiltIns.Dictionary.TryGetProperty("count", out _));
        }

        [Fact]
        public void AGenericParameterHandle_ResolvesToTheErasedClass()
        {
            using var runtime = new SurtrRuntime();

            var handle = runtime.TypeHandle(SurtrClassReference.GenericParameter(0));

            Assert.True(handle.IsResolved);
            Assert.Same(SurtrBuiltIns.Erased, handle.ResolvedClass);
        }

        #endregion

        #region The members run

        [Fact]
        public void ArrayPushPopAndGet_RoundTripThroughTheDeclaredMembers()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));

            var push = (SurtrNativeMethodInfo)MemberOf(SurtrBuiltIns.Array, "push");
            var get = (SurtrNativeMethodInfo)MemberOf(SurtrBuiltIns.Array, "get");
            var pop = (SurtrNativeMethodInfo)MemberOf(SurtrBuiltIns.Array, "pop");

            SurtrValue self = SurtrValue.CreateReference(array.GetSurtrReference());

            Invoke(runtime, push, self, SurtrValue.CreateInt(7));
            Invoke(runtime, push, self, SurtrValue.CreateInt(9));

            Assert.Equal(2, array.Count);
            Assert.Equal(9, Invoke(runtime, get, self, SurtrValue.CreateInt(1)).AsInt);
            Assert.Equal(9, Invoke(runtime, pop, self).AsInt);
            Assert.Equal(1, array.Count);
        }

        [Fact]
        public void APushedPrimitive_IsNotBoxedOnTheWayIn()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));

            var push = (SurtrNativeMethodInfo)MemberOf(SurtrBuiltIns.Array, "push");
            Invoke(runtime, push, SurtrValue.CreateReference(array.GetSurtrReference()), SurtrValue.CreateInt(7));

            // The parameter is erased, but erasure is a compile-time story: the slot still holds a
            // tagged int, not a reference to a box.
            Assert.True(array[0].IsInt);
            Assert.Equal(7, array[0].AsInt);
        }

        [Fact]
        public void ArrayGet_OutOfRange_Traps()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            var get = (SurtrNativeMethodInfo)MemberOf(SurtrBuiltIns.Array, "get");

            Assert.Throws<ArgumentOutOfRangeException>(() => Invoke(
                runtime, get, SurtrValue.CreateReference(array.GetSurtrReference()), SurtrValue.CreateInt(0)));
        }

        [Fact]
        public void ArrayPop_OnAnEmptyArray_Traps()
        {
            using var runtime = new SurtrRuntime();
            var array = runtime.NewArray(SurtrClassReference.Array(SurtrClassReference.Integer));
            var pop = (SurtrNativeMethodInfo)MemberOf(SurtrBuiltIns.Array, "pop");

            Assert.Throws<InvalidOperationException>(() => Invoke(
                runtime, pop, SurtrValue.CreateReference(array.GetSurtrReference())));
        }

        [Fact]
        public void DictionaryKeys_CarryTheKeyTypeOfTheDictionaryTheyCameFrom()
        {
            using var runtime = new SurtrRuntime();

            var dictionaryType = SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.String);
            var dictionary = runtime.NewDictionary(dictionaryType);
            dictionary.Set(SurtrValue.CreateInt(1), SurtrValue.CreateReference(runtime.NewString("a").GetSurtrReference()));

            var keys = (SurtrNativeMethodInfo)MemberOf(SurtrBuiltIns.Dictionary, "keys");
            SurtrValue result = Invoke(runtime, keys, SurtrValue.CreateReference(dictionary.GetSurtrReference()));

            var keyArray = runtime.Resolve<SurtrArray>(result)!;
            Assert.Equal("AI", keyArray.TypeReference.Descriptor);
            Assert.Equal(1, keyArray.Count);
            Assert.Equal(1, keyArray[0].AsInt);
        }

        [Fact]
        public void DictionaryGet_OnAMissingKey_Traps()
        {
            using var runtime = new SurtrRuntime();
            var dictionary = runtime.NewDictionary(
                SurtrClassReference.Dictionary(SurtrClassReference.Integer, SurtrClassReference.Integer));

            var get = (SurtrNativeMethodInfo)MemberOf(SurtrBuiltIns.Dictionary, "get");

            Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => Invoke(
                runtime, get, SurtrValue.CreateReference(dictionary.GetSurtrReference()), SurtrValue.CreateInt(3)));
        }

        private static unsafe SurtrValue Invoke(SurtrRuntime runtime, SurtrNativeMethodInfo method, params SurtrValue[] arguments)
        {
            // ulong, not SurtrRawValue: the alias is a global using inside Surtr.Core and
            // deliberately does not flow to consumers.
            var raw = new ulong[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
                raw[i] = arguments[i].Raw;

            fixed (ulong* pointer = raw)
            {
                // The answer lands in place over slot 0; zero would mean the body wrote nothing.
                int count = method.EntryPoint.Invoke(new SurtrCallArguments(runtime, pointer, raw.Length, Math.Max(raw.Length, 1)));
                return count > 0 ? SurtrValue.FromRaw(pointer[0]) : SurtrValue.Null;
            }
        }

        #endregion
    }
}
