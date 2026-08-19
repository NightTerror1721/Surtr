#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.Runtime.Classes
{
    /// <summary>
    /// Covers the declaration facts metadata has to carry for a compiler reading another module:
    /// <c>sealed</c> on a class and on an override, enum-ness with its per-case ordinal, and the
    /// duplicate-signature rule that keeps an overload group legal.
    /// </summary>
    public class SurtrDeclarationModifierTests
    {
        #region Test fixture helpers

        private static SurtrModule NewModule(string path = "test") => new(path);

        private static SurtrTypeHandle HandleFor(SurtrModule module, SurtrTypeInfo type)
        {
            var handle = module.TypeHandles.GetOrAdd(type.SelfReference);
            if (!handle.IsResolved)
                handle.Resolve(type);
            return handle;
        }

        private static SurtrTypeHandle HandleFor(SurtrModule module, SurtrClassReference reference)
            => module.TypeHandles.GetOrAdd(reference);

        private static SurtrClass DefineClass(
            SurtrModule module,
            string name,
            SurtrClass? baseClass = null,
            bool isAbstract = false,
            bool isSealed = false,
            bool isEnum = false)
        {
            var type = new SurtrClass(
                name,
                SurtrValueTypeCode.Object,
                SurtrClassReference.Object($"test:{name}"),
                baseClass is null ? null : HandleFor(module, baseClass),
                isAbstract,
                SurtrVisibility.Public,
                declaringType: null,
                isSealed,
                isEnum);

            module.AddClass(type);
            return type;
        }

        private static SurtrValue StubBody(SurtrCallArguments arguments) => SurtrValue.Null;

        private static SurtrNativeEntryPoint Stub() => SurtrNativeEntryPoint.FromDelegate(StubBody);

        private static SurtrMethodInfo Method(
            SurtrModule module,
            SurtrClass owner,
            string name,
            SurtrMethodDispatch dispatch = SurtrMethodDispatch.Direct,
            bool isOverride = false,
            bool isSealed = false,
            SurtrClassReference[]? parameterTypes = null)
        {
            parameterTypes ??= Array.Empty<SurtrClassReference>();
            var parameters = new SurtrParameterInfo[parameterTypes.Length];
            for (int i = 0; i < parameterTypes.Length; i++)
                parameters[i] = new SurtrParameterInfo("p" + i, HandleFor(module, parameterTypes[i]));

            return new SurtrNativeMethodInfo(
                name,
                dispatch,
                SurtrMethodRole.Normal,
                isOverride,
                HandleFor(module, SurtrClassReference.Void),
                parameters,
                isStatic: false,
                SurtrVisibility.Public,
                HandleFor(module, owner),
                Stub(),
                isSealed);
        }

        #endregion

        #region sealed

        [Fact]
        public void AClass_ReportsWhetherItIsSealed()
        {
            var module = NewModule();

            Assert.False(DefineClass(module, "Open").IsSealed);
            Assert.True(DefineClass(module, "Closed", isSealed: true).IsSealed);
        }

        [Fact]
        public void AbstractAndSealed_AreMutuallyExclusive()
        {
            var module = NewModule();

            Assert.Throws<ArgumentException>(() => DefineClass(module, "Impossible", isAbstract: true, isSealed: true));
        }

        [Fact]
        public void ExtendingASealedClass_IsRejectedAtLink()
        {
            var module = NewModule();
            var closed = DefineClass(module, "Closed", isSealed: true);
            DefineClass(module, "Derived", baseClass: closed);

            var error = Assert.Throws<InvalidOperationException>(() => SurtrTypeLinker.LinkModule(module));
            Assert.Contains("sealed", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void SealedWithoutOverride_IsRejectedAtTheMethod()
        {
            var module = NewModule();
            var owner = DefineClass(module, "Owner");

            Assert.Throws<ArgumentException>(() => Method(
                module, owner, "speak", SurtrMethodDispatch.Virtual, isOverride: false, isSealed: true));
        }

        [Fact]
        public void OverridingASealedOverride_IsRejectedAtLink()
        {
            var module = NewModule();

            var animal = DefineClass(module, "Animal");
            animal.AddMethod(Method(module, animal, "speak", SurtrMethodDispatch.Virtual));

            var dog = DefineClass(module, "Dog", baseClass: animal);
            dog.AddMethod(Method(module, dog, "speak", SurtrMethodDispatch.Virtual, isOverride: true, isSealed: true));

            var puppy = DefineClass(module, "Puppy", baseClass: dog);
            puppy.AddMethod(Method(module, puppy, "speak", SurtrMethodDispatch.Virtual, isOverride: true));

            var error = Assert.Throws<InvalidOperationException>(() => SurtrTypeLinker.LinkModule(module));
            Assert.Contains("sealed", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ASealedOverride_LinksAndKeepsItsBaseSlot()
        {
            var module = NewModule();

            var animal = DefineClass(module, "Animal");
            animal.AddMethod(Method(module, animal, "speak", SurtrMethodDispatch.Virtual));

            var dog = DefineClass(module, "Dog", baseClass: animal);
            var sealedOverride = Method(module, dog, "speak", SurtrMethodDispatch.Virtual, isOverride: true, isSealed: true);
            dog.AddMethod(sealedOverride);

            SurtrTypeLinker.LinkModule(module);

            Assert.True(sealedOverride.IsSealed);
            Assert.Equal(0, sealedOverride.VTableSlot);
        }

        #endregion

        #region Enums

        [Fact]
        public void AnEnum_IsSealedAndReportsItsKind()
        {
            var module = NewModule();
            var suit = DefineClass(module, "Suit", isEnum: true);

            Assert.True(suit.IsEnum);
            Assert.True(suit.IsSealed);
            Assert.Equal(SurtrMemberKind.Enum, suit.Kind);
        }

        [Fact]
        public void AnEnum_CannotDeclareABaseClass()
        {
            var module = NewModule();
            var animal = DefineClass(module, "Animal");

            Assert.Throws<ArgumentException>(() => new SurtrClass(
                "Bad",
                SurtrValueTypeCode.Object,
                SurtrClassReference.Object("test:Bad"),
                HandleFor(module, animal),
                false,
                SurtrVisibility.Public,
                null,
                isSealed: false,
                isEnum: true));
        }

        [Fact]
        public void EnumCases_TakeOrdinalsInDeclarationOrder()
        {
            var module = NewModule();
            var suit = DefineClass(module, "Suit", isEnum: true);
            var selfHandle = HandleFor(module, suit);

            suit.AddEnumCase(new SurtrFieldInfo("Hearts", selfHandle, true, true, SurtrVisibility.Public, selfHandle));
            suit.AddEnumCase(new SurtrFieldInfo("Spades", selfHandle, true, true, SurtrVisibility.Public, selfHandle));
            suit.AddEnumCase(new SurtrFieldInfo("Clubs", selfHandle, true, true, SurtrVisibility.Public, selfHandle));

            var cases = suit.EnumCases;
            Assert.Equal(3, cases.Length);

            Assert.Equal("Hearts", cases[0].Name);
            Assert.Equal(0, cases[0].Ordinal);
            Assert.Equal("Clubs", cases[2].Name);
            Assert.Equal(2, cases[2].Ordinal);

            // The case is reachable as an ordinary static field too - the storage is not special.
            Assert.True(suit.TryGetField("Spades", out var spades));
            Assert.True(spades.IsStatic);
        }

        [Fact]
        public void ANonEnumClass_RefusesEnumCases()
        {
            var module = NewModule();
            var plain = DefineClass(module, "Plain");
            var handle = HandleFor(module, plain);

            Assert.Throws<InvalidOperationException>(() => plain.AddEnumCase(
                new SurtrFieldInfo("Nope", handle, true, true, SurtrVisibility.Public, handle)));
        }

        [Fact]
        public void AnEnumCase_MustBeStatic()
        {
            var module = NewModule();
            var suit = DefineClass(module, "Suit", isEnum: true);
            var handle = HandleFor(module, suit);

            Assert.Throws<ArgumentException>(() => suit.AddEnumCase(
                new SurtrFieldInfo("Hearts", handle, false, true, SurtrVisibility.Public, handle)));
        }

        [Fact]
        public void AClassWithNoCases_ReportsAnEmptyCaseList()
        {
            var module = NewModule();
            Assert.True(DefineClass(module, "Plain").EnumCases.IsEmpty);
        }

        #endregion

        #region Overload groups reject a repeated signature

        [Fact]
        public void TwoOverloadsWithDifferentParameters_BothLand()
        {
            var module = NewModule();
            var owner = DefineClass(module, "Log");

            owner.AddMethod(Method(module, owner, "log", parameterTypes: new[] { SurtrClassReference.String }));
            owner.AddMethod(Method(module, owner, "log", parameterTypes: new[] { SurtrClassReference.Integer }));

            Assert.True(owner.TryGetMethods("log", out var overloads));
            Assert.Equal(2, overloads.Length);
        }

        [Fact]
        public void ASecondMemberWithTheSameSignature_IsRejected()
        {
            var module = NewModule();
            var owner = DefineClass(module, "Log");

            owner.AddMethod(Method(module, owner, "log", parameterTypes: new[] { SurtrClassReference.String }));

            Assert.Throws<ArgumentException>(() => owner.AddMethod(
                Method(module, owner, "log", parameterTypes: new[] { SurtrClassReference.String })));
        }

        [Fact]
        public void SignatureKey_IgnoresTheReturnTypeSoAnOverrideKeepsItsSlot()
        {
            var module = NewModule();
            var owner = DefineClass(module, "Owner");

            var returnsVoid = new SurtrNativeMethodInfo(
                "get", SurtrMethodDispatch.Direct, SurtrMethodRole.Normal, false,
                HandleFor(module, SurtrClassReference.Void),
                Array.Empty<SurtrParameterInfo>(),
                false, SurtrVisibility.Public, HandleFor(module, owner), Stub());

            var returnsInt = new SurtrNativeMethodInfo(
                "get", SurtrMethodDispatch.Direct, SurtrMethodRole.Normal, false,
                HandleFor(module, SurtrClassReference.Integer),
                Array.Empty<SurtrParameterInfo>(),
                false, SurtrVisibility.Public, HandleFor(module, owner), Stub());

            Assert.Equal(returnsVoid.SignatureKey(), returnsInt.SignatureKey());

            // Which is exactly why declaring both is an error rather than an overload.
            owner.AddMethod(returnsVoid);
            Assert.Throws<ArgumentException>(() => owner.AddMethod(returnsInt));
        }

        [Fact]
        public void SignatureKey_NamesTheMemberAndItsParameterDescriptors()
        {
            var module = NewModule();
            var owner = DefineClass(module, "Owner");

            var method = Method(
                module, owner, "store",
                parameterTypes: new[] { SurtrClassReference.Integer, SurtrClassReference.String });

            Assert.Equal("store(IS)", method.SignatureKey());
        }

        [Fact]
        public void SignatureKey_ErasesATypesAndAMethodsParameterToTheSameThing()
        {
            // G0 and H0 are distinct descriptors but the key erases both to E, so a class method
            // f<T>(T) and a module function f(unknown) cannot share a slot without being caught
            // here - the same collision SignatureSet reports at compile time.
            var module = NewModule();
            var owner = DefineClass(module, "Owner");

            var byTypeParameter = Method(
                module, owner, "f",
                parameterTypes: new[] { SurtrClassReference.GenericParameter(0) });
            var byMethodParameter = Method(
                module, owner, "f",
                parameterTypes: new[] { SurtrClassReference.MethodGenericParameter(0) });
            var byErased = Method(
                module, owner, "f",
                parameterTypes: new[] { SurtrClassReference.Erased });

            Assert.Equal("f(E)", byTypeParameter.SignatureKey());
            Assert.Equal(byTypeParameter.SignatureKey(), byMethodParameter.SignatureKey());
            Assert.Equal(byTypeParameter.SignatureKey(), byErased.SignatureKey());

            // Which is exactly why declaring the second and third is an error rather than an overload.
            owner.AddMethod(byTypeParameter);
            Assert.Throws<ArgumentException>(() => owner.AddMethod(byMethodParameter));
            Assert.Throws<ArgumentException>(() => owner.AddMethod(byErased));
        }

        [Fact]
        public void AModuleLevelFunction_IsHeldToTheSameRule()
        {
            var module = NewModule();
            var owner = DefineClass(module, "Owner");

            module.AddMethod(Method(module, owner, "helper", parameterTypes: new[] { SurtrClassReference.Integer }));

            Assert.Throws<ArgumentException>(() => module.AddMethod(
                Method(module, owner, "helper", parameterTypes: new[] { SurtrClassReference.Integer })));
        }

        #endregion
    }
}
