#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.Runtime.BuiltIns
{
    /// <summary>
    /// Covers the library half of the <c>surtr</c> module: the exception hierarchy §9 needs a legal
    /// throwable from, the core interfaces, <c>Math</c>, and attributes as real classes.
    /// </summary>
    public class SurtrStandardLibraryTests
    {
        #region The exception hierarchy

        [Fact]
        public void EveryStandardException_DerivesFromException()
        {
            SurtrBuiltIns.EnsureBuilt();

            var subclasses = new[]
            {
                SurtrBuiltIns.ArgumentException,
                SurtrBuiltIns.IndexOutOfRangeException,
                SurtrBuiltIns.KeyNotFoundException,
                SurtrBuiltIns.NullReferenceException,
                SurtrBuiltIns.DivideByZeroException,
                SurtrBuiltIns.InvalidCastException,
                SurtrBuiltIns.StackOverflowException,
                SurtrBuiltIns.InvalidOperationException,
            };

            foreach (var subclass in subclasses)
            {
                Assert.True(subclass.IsSubclassOf(SurtrBuiltIns.Exception), $"{subclass.Name} does not derive from Exception.");
                Assert.Equal(1, subclass.Depth);
            }
        }

        [Fact]
        public void Exception_CarriesOneMessageSlotThatSubclassesInherit()
        {
            SurtrBuiltIns.EnsureBuilt();

            // Inherited field slots keep their base-class index, which is what lets one helper
            // write the message of any exception class without knowing which one it is building.
            Assert.Equal(1, SurtrBuiltIns.Exception.InstanceSlotCount);
            Assert.Equal(1, SurtrBuiltIns.KeyNotFoundException.InstanceSlotCount);
            Assert.True(SurtrBuiltIns.Exception.TryGetProperty("message", out _));
        }

        [Fact]
        public void NewException_BuildsAnInstanceCarryingItsMessage()
        {
            using var runtime = new SurtrRuntime();

            var raised = runtime.NewException(SurtrBuiltIns.KeyNotFoundException, "no such key");

            Assert.Same(SurtrBuiltIns.KeyNotFoundException, raised.Class);

            var message = runtime.Resolve<SurtrString>(raised[SurtrStandardLibraryProbe.MessageSlot]);
            Assert.Equal("no such key", message!.Value);
        }

        [Fact]
        public void NewException_RefusesAClassOutsideTheHierarchy()
        {
            using var runtime = new SurtrRuntime();

            Assert.Throws<ArgumentException>(() => runtime.NewException(SurtrBuiltIns.Math, "nope"));
        }

        [Fact]
        public void AnExceptionIsAnOrdinaryClass_SoSurtrSourceCanExtendIt()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("game");
            builder.DefineClass("OutOfRangeException", SurtrBuiltIns.Exception.SelfReference);

            var module = builder.Build();
            runtime.LoadModule(module);

            Assert.True(module.TryGetClass("OutOfRangeException", out var declared));
            Assert.True(declared.IsSubclassOf(SurtrBuiltIns.Exception));

            // And it inherits the message slot rather than starting a layout of its own.
            Assert.Equal(1, declared.InstanceSlotCount);
        }

        #endregion

        #region Core interfaces

        [Theory]
        [InlineData("IIterable")]
        [InlineData("IIterator")]
        [InlineData("IComparable")]
        [InlineData("IEquatable")]
        public void TheCoreInterfaces_AreDeclaredAndGeneric(string name)
        {
            SurtrBuiltIns.EnsureBuilt();

            // Arity is part of the name a generic type is declared and looked up under, so the
            // table is keyed on the mangled form rather than on what source writes.
            string metadataName = SurtrClassReference.MangleArity(name, 1);

            Assert.False(SurtrBuiltIns.Module.TryGetInterface(name, out _));
            Assert.True(SurtrBuiltIns.Module.TryGetInterface(metadataName, out var contract));
            Assert.Equal(1, contract.GenericParameterCount);
            Assert.Equal("T", contract.GenericParameters[0]);
        }

        [Theory]
        [InlineData("IIterable")]
        [InlineData("IIterator")]
        [InlineData("IComparable")]
        [InlineData("IEquatable")]
        public void AGenericContractsSelfReferenceIsConstructedWithItsOwnParameter(string name)
        {
            SurtrBuiltIns.EnsureBuilt();

            Assert.True(
                SurtrBuiltIns.Module.TryGetInterface(SurtrClassReference.MangleArity(name, 1), out var contract));

            // Arity in the name says one argument follows, so there is no open form to write: the
            // contract names itself with its own parameter.
            Assert.Equal($"Osurtr:{name}`1;G0", contract.SelfReference.Descriptor);
            Assert.Equal(1, contract.SelfReference.GenericArity);
            Assert.Equal("G0", Assert.Single(contract.SelfReference.GetTypeArguments()).Descriptor);
            Assert.Equal($"surtr:{name}<T0>", contract.SelfReference.ToDisplayString());
        }

        [Fact]
        public void IIterator_IsTheTwoMemberCursorThatCanBeLoweredAway()
        {
            SurtrBuiltIns.EnsureBuilt();

            Assert.True(SurtrBuiltIns.Module.TryGetInterface("IIterator`1", out var iterator));

            Assert.True(iterator.TryGetMethods("moveNext", out var moveNext));
            Assert.Equal("B", moveNext[0].ReturnType.Reference.Descriptor);

            Assert.True(iterator.TryGetProperty("current", out var current));
            Assert.Equal("G0", current.PropertyType.Reference.Descriptor);
            Assert.Null(current.Setter);
        }

        [Fact]
        public void IIterable_ReturnsAnIterator()
        {
            SurtrBuiltIns.EnsureBuilt();

            Assert.True(SurtrBuiltIns.Module.TryGetInterface("IIterable`1", out var iterable));
            Assert.True(iterable.TryGetMethods("iterate", out var iterate));

            Assert.True(SurtrBuiltIns.Module.TryGetInterface("IIterator`1", out var iterator));
            Assert.Equal(iterator.SelfReference.Descriptor, iterate[0].ReturnType.Reference.Descriptor);
        }

        [Fact]
        public void IComparableAndIEquatable_TakeTheirOwnParameter()
        {
            SurtrBuiltIns.EnsureBuilt();

            Assert.True(SurtrBuiltIns.Module.TryGetInterface("IComparable`1", out var comparable));
            Assert.True(comparable.TryGetMethods("compareTo", out var compareTo));
            Assert.Equal("G0", compareTo[0].Parameters[0].ParameterType.Reference.Descriptor);
            Assert.Equal("I", compareTo[0].ReturnType.Reference.Descriptor);

            // The key erases it, which is what lets an implementation match the slot at all.
            Assert.Equal("compareTo(E)", compareTo[0].SignatureKey());

            Assert.True(SurtrBuiltIns.Module.TryGetInterface("IEquatable`1", out var equatable));
            Assert.True(equatable.TryGetMethods("equals", out var equals));
            Assert.Equal("G0", equals[0].Parameters[0].ParameterType.Reference.Descriptor);
            Assert.Equal("equals(E)", equals[0].SignatureKey());
        }

        #endregion

        #region Math

        [Fact]
        public void Math_IsStaticOnlyAndCannotBeInstantiated()
        {
            SurtrBuiltIns.EnsureBuilt();

            Assert.True(SurtrBuiltIns.Math.IsAbstract);

            foreach (var overloads in SurtrBuiltIns.Math.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                    Assert.True(overloads[i].IsStatic, $"Math.{overloads[i].Name} is not static.");
            }
        }

        [Theory]
        [InlineData("abs")]
        [InlineData("min")]
        [InlineData("max")]
        [InlineData("clamp")]
        [InlineData("sqrt")]
        [InlineData("pow")]
        [InlineData("sin")]
        [InlineData("cos")]
        public void Math_DeclaresTheMembersOrdinaryCodeReachesFor(string name)
        {
            SurtrBuiltIns.EnsureBuilt();
            Assert.True(SurtrBuiltIns.Math.TryGetMethods(name, out _));
        }

        [Fact]
        public void MathPow_ExistsBecauseTheOperatorTableHasNoExponentiation()
        {
            SurtrBuiltIns.EnsureBuilt();

            Assert.True(SurtrBuiltIns.Math.TryGetMethods("pow", out var pow));
            Assert.Equal("pow(FF)", pow[0].SignatureKey());
        }

        #endregion

        #region Attributes

        private static SurtrModule ModuleWithAttribute(out SurtrClassBuilder attributeClass, out SurtrMethodBuilder target)
        {
            var builder = new SurtrModuleBuilder("game");

            attributeClass = builder.DefineClass("Range", SurtrBuiltIns.Attribute.SelfReference);
            attributeClass.DefineField("low", SurtrClassReference.Integer);
            attributeClass.DefineField("high", SurtrClassReference.Integer);

            target = builder.DefineFunction("health", SurtrClassReference.Void);
            target.AddAttribute(builder.Attribute(attributeClass.SelfReference, SurtrConstant.Integer(0), SurtrConstant.Integer(100)));
            target.Code.ReturnVoid();

            return builder.Build();
        }

        [Fact]
        public void AnAttribute_IsInstantiatedWhenItsModuleLoads()
        {
            using var runtime = new SurtrRuntime();

            var module = ModuleWithAttribute(out _, out var target);
            runtime.LoadModule(module);

            var usage = target.Built!.Attributes[0];

            Assert.NotEqual(0, usage.Instance);

            var instance = runtime.Resolve<SurtrInstance>(SurtrValue.CreateReference(usage.Instance))!;
            Assert.Equal("Range", instance.Class.Name);
            Assert.Equal(0, instance[0].AsInt);
            Assert.Equal(100, instance[1].AsInt);
        }

        [Fact]
        public void AnAttributeInstance_SurvivesACollection()
        {
            using var runtime = new SurtrRuntime();

            var module = ModuleWithAttribute(out _, out var target);
            runtime.LoadModule(module);

            SurtrRefProbe.Collect(runtime);

            // Metadata is owned outright and never traced, so nothing reaches an attribute instance
            // *through* a member - the permanent root set is what keeps it alive.
            var usage = target.Built!.Attributes[0];
            Assert.NotNull(runtime.Resolve<SurtrInstance>(SurtrValue.CreateReference(usage.Instance)));
        }

        [Fact]
        public void AClassThatDoesNotDeriveFromAttribute_IsRejectedAtLoad()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("game");
            var notAnAttribute = builder.DefineClass("Plain");

            var target = builder.DefineFunction("health", SurtrClassReference.Void);
            target.AddAttribute(builder.Attribute(notAnAttribute.SelfReference));
            target.Code.ReturnVoid();

            var error = Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(builder.Build()));
            Assert.Contains("Attribute", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void MoreArgumentsThanTheAttributeHasFields_IsRejectedAtLoad()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("game");
            var attributeClass = builder.DefineClass("Marker", SurtrBuiltIns.Attribute.SelfReference);

            var target = builder.DefineFunction("health", SurtrClassReference.Void);
            target.AddAttribute(builder.Attribute(attributeClass.SelfReference, SurtrConstant.Integer(1)));
            target.Code.ReturnVoid();

            Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(builder.Build()));
        }

        [Fact]
        public void AMemberWithNoAttributes_CarriesAnEmptyList()
        {
            var builder = new SurtrModuleBuilder("game");
            var method = builder.DefineFunction("plain", SurtrClassReference.Void);
            method.Code.ReturnVoid();

            builder.Build();

            Assert.True(method.Built!.Attributes.IsEmpty);
        }

        [Fact]
        public void TryGetAttribute_FindsOneByItsClass()
        {
            using var runtime = new SurtrRuntime();

            var module = ModuleWithAttribute(out var attributeClass, out var target);
            runtime.LoadModule(module);

            Assert.True(target.Built!.TryGetAttribute(attributeClass.Class, out var found));
            Assert.Equal(2, found.Arguments.Length);
            Assert.False(target.Built!.TryGetAttribute(SurtrBuiltIns.Math, out _));
        }

        #endregion

        /// <summary>Mirrors the message slot the library declares, so a test can read it.</summary>
        private static class SurtrStandardLibraryProbe
        {
            internal const int MessageSlot = 0;
        }

        private static class SurtrRefProbe
        {
            internal static void Collect(SurtrRuntime runtime) => runtime.Collect(fullCollection: true);
        }
    }
}
