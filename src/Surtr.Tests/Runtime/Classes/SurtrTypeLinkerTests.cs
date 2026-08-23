#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.Runtime.Classes
{
    public unsafe class SurtrTypeLinkerTests
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
            SurtrInterface[]? interfaces = null)
        {
            var selfReference = SurtrClassReference.Object($"test:{name}");
            var baseHandle = baseClass is null ? null : HandleFor(module, baseClass);

            var type = new SurtrClass(name, SurtrValueTypeCode.Object, selfReference, baseHandle, isAbstract, SurtrVisibility.Public, declaringType: null);

            if (interfaces is { Length: > 0 })
            {
                var handles = new SurtrTypeHandle[interfaces.Length];
                for (int i = 0; i < interfaces.Length; i++)
                    handles[i] = HandleFor(module, interfaces[i]);
                type.SetDeclaredInterfaces(handles);
            }

            module.AddClass(type);
            return type;
        }

        private static SurtrInterface DefineInterface(SurtrModule module, string name, SurtrInterface[]? extends = null)
        {
            var selfReference = SurtrClassReference.Object($"test:{name}");
            var contract = new SurtrInterface(name, selfReference, SurtrVisibility.Public, declaringType: null);

            if (extends is { Length: > 0 })
            {
                var handles = new SurtrTypeHandle[extends.Length];
                for (int i = 0; i < extends.Length; i++)
                    handles[i] = HandleFor(module, extends[i]);
                contract.SetDeclaredExtendedInterfaces(handles);
            }

            module.AddInterface(contract);
            return contract;
        }

        private static SurtrFieldInfo Field(SurtrModule module, string name, SurtrClassReference type, bool isStatic = false, bool isReadOnly = false)
            => new(name, HandleFor(module, type), isStatic, isReadOnly, SurtrVisibility.Public, declaringType: null);

        private static SurtrParameterInfo Param(SurtrModule module, string name, SurtrClassReference type)
            => new(name, HandleFor(module, type));

        // A named static method, not a lambda: even a non-capturing lambda may be compiled as an
        // instance method on a compiler-generated singleton cache class, which would give it a
        // non-null Target and trip FromDelegate's "must be static" check. A method group
        // conversion carries no such ambiguity.
        private static int StubBody(SurtrCallArguments arguments) => arguments.Return(SurtrValue.Null);

        private static SurtrNativeEntryPoint Stub() => SurtrNativeEntryPoint.FromDelegate(StubBody);

        private static SurtrNativeMethodInfo NativeMethod(
            SurtrModule module,
            string name,
            SurtrMethodDispatch dispatch = SurtrMethodDispatch.Direct,
            bool isOverride = false,
            SurtrMethodRole role = SurtrMethodRole.Normal,
            bool isStatic = false,
            SurtrParameterInfo[]? parameters = null,
            SurtrClassReference? returnType = null)
            => new(
                name,
                dispatch,
                role,
                isOverride,
                HandleFor(module, returnType ?? SurtrClassReference.Void),
                parameters ?? Array.Empty<SurtrParameterInfo>(),
                isStatic,
                SurtrVisibility.Public,
                declaringType: null,
                Stub());

        private static SurtrAbstractMethodInfo AbstractMethod(
            SurtrModule module,
            string name,
            SurtrParameterInfo[]? parameters = null,
            SurtrClassReference? returnType = null)
            => new(
                name,
                HandleFor(module, returnType ?? SurtrClassReference.Void),
                parameters ?? Array.Empty<SurtrParameterInfo>(),
                SurtrVisibility.Public,
                declaringType: null);

        #endregion

        #region Ancestors & subtype tests

        [Fact]
        public void ARootClass_IsItsOwnOnlyAncestor_AtDepthZero()
        {
            var module = NewModule();
            var a = DefineClass(module, "A");

            SurtrTypeLinker.LinkModule(module);

            Assert.Equal(new[] { a }, a.Ancestors);
            Assert.Equal(0, a.Depth);
            Assert.True(a.IsSubclassOf(a));
        }

        [Fact]
        public void AThreeLevelHierarchy_BuildsAncestorsIndexedByDepth()
        {
            var module = NewModule();
            var a = DefineClass(module, "A");
            var b = DefineClass(module, "B", baseClass: a);
            var c = DefineClass(module, "C", baseClass: b);

            SurtrTypeLinker.LinkModule(module);

            Assert.Equal(new[] { a, b, c }, c.Ancestors);
            Assert.Equal(2, c.Depth);

            Assert.True(c.IsSubclassOf(a));
            Assert.True(c.IsSubclassOf(b));
            Assert.True(c.IsSubclassOf(c));
            Assert.False(a.IsSubclassOf(b));
            Assert.False(a.IsSubclassOf(c));
        }

        #endregion

        #region Field layout & static storage

        [Fact]
        public void InheritedInstanceFields_KeepTheBaseClasssSlotIndices()
        {
            var module = NewModule();
            var a = DefineClass(module, "A");
            var x = Field(module, "x", SurtrClassReference.Integer);
            a.AddField(x);

            var b = DefineClass(module, "B", baseClass: a);
            var y = Field(module, "y", SurtrClassReference.Integer);
            b.AddField(y);

            SurtrTypeLinker.LinkModule(module);

            Assert.Equal(0, x.Slot);
            Assert.Equal(1, y.Slot);

            Assert.Single(a.InstanceFields);
            Assert.Equal(new[] { x, y }, b.InstanceFields);

            // The base's field object itself is reused, not a copy - a field access compiled
            // against A keeps naming the exact same slot on a B instance.
            Assert.Same(x, b.InstanceFields[0]);
            Assert.Equal(2, b.InstanceSlotCount);
            Assert.Equal(1, a.InstanceSlotCount);
        }

        [Fact]
        public void ReferenceSlots_OnlyListsFieldsOfAReferenceType()
        {
            var module = NewModule();
            var a = DefineClass(module, "A");
            a.AddField(Field(module, "name", SurtrClassReference.String));   // reference
            a.AddField(Field(module, "count", SurtrClassReference.Integer)); // value
            a.AddField(Field(module, "tag", SurtrClassReference.String));    // reference

            SurtrTypeLinker.LinkModule(module);

            Assert.Equal(2, a.ReferenceSlots.Length);
            Assert.Equal(0, a.ReferenceSlots[0]); // "name"
            Assert.Equal(2, a.ReferenceSlots[1]); // "tag"
        }

        [Fact]
        public void StaticFields_AreNotInherited_AndEachClassOwnsItsOwnStorage()
        {
            var module = NewModule();
            var a = DefineClass(module, "A");
            a.AddField(Field(module, "counter", SurtrClassReference.Integer, isStatic: true));

            var b = DefineClass(module, "B", baseClass: a);
            b.AddField(Field(module, "name", SurtrClassReference.String, isStatic: true));

            SurtrTypeLinker.LinkModule(module);

            Assert.Single(a.StaticFields);
            Assert.Single(b.StaticFields);
            Assert.NotEqual(a.StaticFields[0].Name, b.StaticFields[0].Name);
        }

        [Fact]
        public void StaticStorage_ReferenceSlots_AreCompactedToOnlyReferenceTypedStatics()
        {
            var module = NewModule();
            var a = DefineClass(module, "A");
            a.AddField(Field(module, "label", SurtrClassReference.String, isStatic: true));  // slot 0, reference
            a.AddField(Field(module, "count", SurtrClassReference.Integer, isStatic: true)); // slot 1, value
            a.AddField(Field(module, "tag", SurtrClassReference.String, isStatic: true));    // slot 2, reference

            SurtrTypeLinker.LinkModule(module);

            Assert.Equal(2, a.ReferenceStaticSlots.Length);
            Assert.Equal(0, a.ReferenceStaticSlots[0]);
            Assert.Equal(2, a.ReferenceStaticSlots[1]);
        }

        [Fact]
        public void StaticAddress_PointsAtTheFieldsOwnSlot_InTheClasssStorage()
        {
            var module = NewModule();
            var a = DefineClass(module, "A");
            var first = Field(module, "first", SurtrClassReference.Integer, isStatic: true);
            var second = Field(module, "second", SurtrClassReference.Integer, isStatic: true);
            a.AddField(first);
            a.AddField(second);

            SurtrTypeLinker.LinkModule(module);

            *first.StaticAddress = SurtrValue.CreateInt(11).Raw;
            *second.StaticAddress = SurtrValue.CreateInt(22).Raw;

            Assert.Equal(SurtrValue.CreateInt(11).Raw, a.StaticStorage[0]);
            Assert.Equal(SurtrValue.CreateInt(22).Raw, a.StaticStorage[1]);
        }

        #endregion

        #region Virtual dispatch: inheritance and override

        [Fact]
        public void ARootVirtualMethod_GetsSlotZero()
        {
            var module = NewModule();
            var a = DefineClass(module, "A");
            var speak = NativeMethod(module, "speak", dispatch: SurtrMethodDispatch.Virtual);
            a.AddMethod(speak);

            SurtrTypeLinker.LinkModule(module);

            Assert.Equal(0, speak.VTableSlot);
            Assert.Single(a.VirtualMethods);
            Assert.Same(speak, a.VirtualMethods[0]);
        }

        [Fact]
        public void AnOverride_ReplacesTheBaseEntryInPlace_AtTheSameSlot()
        {
            var module = NewModule();
            var a = DefineClass(module, "A");
            var baseSpeak = NativeMethod(module, "speak", dispatch: SurtrMethodDispatch.Virtual);
            a.AddMethod(baseSpeak);

            var b = DefineClass(module, "B", baseClass: a);
            var overrideSpeak = NativeMethod(module, "speak", dispatch: SurtrMethodDispatch.Virtual, isOverride: true);
            b.AddMethod(overrideSpeak);

            SurtrTypeLinker.LinkModule(module);

            Assert.Equal(0, overrideSpeak.VTableSlot);
            Assert.Same(overrideSpeak, b.VirtualMethods[0]);

            // The base's own table is untouched by the derived class's override.
            Assert.Same(baseSpeak, a.VirtualMethods[0]);
        }

        [Fact]
        public void OverrideMatching_IgnoresReturnType_ButRequiresParametersToMatch()
        {
            var module = NewModule();
            var a = DefineClass(module, "A");
            a.AddMethod(NativeMethod(module, "speak", dispatch: SurtrMethodDispatch.Virtual, returnType: SurtrClassReference.Integer));

            var b = DefineClass(module, "B", baseClass: a);
            var overrideSpeak = NativeMethod(module, "speak", dispatch: SurtrMethodDispatch.Virtual, isOverride: true, returnType: SurtrClassReference.Float);
            b.AddMethod(overrideSpeak);

            SurtrTypeLinker.LinkModule(module);

            Assert.Equal(0, overrideSpeak.VTableSlot);
            Assert.Same(overrideSpeak, b.VirtualMethods[0]);
        }

        [Fact]
        public void OverrideWithNoMatchingBaseSignature_Throws()
        {
            var module = NewModule();
            var a = DefineClass(module, "A");
            a.AddMethod(NativeMethod(module, "speak", dispatch: SurtrMethodDispatch.Virtual));

            var b = DefineClass(module, "B", baseClass: a);
            b.AddMethod(NativeMethod(
                module, "speak", dispatch: SurtrMethodDispatch.Virtual, isOverride: true,
                parameters: new[] { Param(module, "volume", SurtrClassReference.Integer) }));

            Assert.Throws<InvalidOperationException>(() => SurtrTypeLinker.LinkModule(module));
        }

        [Fact]
        public void ANewVirtualMethod_HidingAnInheritedSignature_WithoutOverride_Throws()
        {
            var module = NewModule();
            var a = DefineClass(module, "A");
            a.AddMethod(NativeMethod(module, "speak", dispatch: SurtrMethodDispatch.Virtual));

            var b = DefineClass(module, "B", baseClass: a);
            b.AddMethod(NativeMethod(module, "speak", dispatch: SurtrMethodDispatch.Virtual, isOverride: false));

            Assert.Throws<InvalidOperationException>(() => SurtrTypeLinker.LinkModule(module));
        }

        [Fact]
        public void DirectAndStaticMethods_DoNotOccupyVTableSlots()
        {
            var module = NewModule();
            var a = DefineClass(module, "A");
            var direct = NativeMethod(module, "helper", dispatch: SurtrMethodDispatch.Direct);
            var staticMethod = NativeMethod(module, "create", dispatch: SurtrMethodDispatch.Direct, isStatic: true);
            a.AddMethod(direct);
            a.AddMethod(staticMethod);

            SurtrTypeLinker.LinkModule(module);

            Assert.Empty(a.VirtualMethods);
            Assert.Equal(-1, direct.VTableSlot);
            Assert.Equal(-1, staticMethod.VTableSlot);
            Assert.Contains(direct, a.DirectMethods);
            Assert.Contains(staticMethod, a.StaticMethods);
        }

        #endregion

        #region Interfaces

        [Fact]
        public void InterfaceExtendingTwoInterfaces_KeepsEachInheritedMethodsOwnSlotNumbering()
        {
            var module = NewModule();
            var ia = DefineInterface(module, "IA");
            var foo = AbstractMethod(module, "foo");
            ia.AddMethod(foo);

            var ib = DefineInterface(module, "IB");
            var bar = AbstractMethod(module, "bar");
            ib.AddMethod(bar);

            var ic = DefineInterface(module, "IC", extends: new[] { ia, ib });

            int nextInterfaceId = 0;
            SurtrTypeLinker.LinkInterface(ia, ref nextInterfaceId);
            SurtrTypeLinker.LinkInterface(ib, ref nextInterfaceId);
            SurtrTypeLinker.LinkInterface(ic, ref nextInterfaceId);

            Assert.Equal(new SurtrMethodInfo[] { foo, bar }, ic.MethodSlots);

            // foo keeps the slot IA gave it (0), and bar keeps the slot IB gave it (0) - not
            // the position (1) it happens to occupy inside IC's own flattened MethodSlots.
            Assert.Equal(0, foo.VTableSlot);
            Assert.Equal(0, bar.VTableSlot);
        }

        [Fact]
        public void ExtendedInterfaces_AreTransitivelyClosed()
        {
            var module = NewModule();
            var ia = DefineInterface(module, "IA");
            ia.AddMethod(AbstractMethod(module, "foo"));

            var ib = DefineInterface(module, "IB");
            ib.AddMethod(AbstractMethod(module, "bar"));

            var ic = DefineInterface(module, "IC", extends: new[] { ia, ib });
            var id = DefineInterface(module, "ID", extends: new[] { ic });

            int nextInterfaceId = 0;
            SurtrTypeLinker.LinkInterface(ia, ref nextInterfaceId);
            SurtrTypeLinker.LinkInterface(ib, ref nextInterfaceId);
            SurtrTypeLinker.LinkInterface(ic, ref nextInterfaceId);
            SurtrTypeLinker.LinkInterface(id, ref nextInterfaceId);

            Assert.Contains(ia, id.ExtendedInterfaces);
            Assert.Contains(ib, id.ExtendedInterfaces);
            Assert.Contains(ic, id.ExtendedInterfaces);
        }

        [Fact]
        public void AClassImplementingAnInterface_DispatchesThroughItsVTable()
        {
            var module = NewModule();
            var greeter = DefineInterface(module, "IGreeter");
            greeter.AddMethod(AbstractMethod(module, "greet"));

            var a = DefineClass(module, "A", interfaces: new[] { greeter });
            var greet = NativeMethod(module, "greet", dispatch: SurtrMethodDispatch.Virtual);
            a.AddMethod(greet);

            SurtrTypeLinker.LinkModule(module);

            int index = a.IndexOfInterface(greeter);
            Assert.True(index >= 0);
            Assert.Same(greet, a.GetInterfaceMethod(index, 0));
        }

        [Fact]
        public void AClassMissingAnInterfaceMember_ThrowsAtLinkTime()
        {
            var module = NewModule();
            var greeter = DefineInterface(module, "IGreeter");
            greeter.AddMethod(AbstractMethod(module, "greet"));

            DefineClass(module, "A", interfaces: new[] { greeter });
            // No matching "greet" method added.

            Assert.Throws<InvalidOperationException>(() => SurtrTypeLinker.LinkModule(module));
        }

        [Fact]
        public void ADerivedClass_InheritsItsBasesInterfaceImplementations()
        {
            var module = NewModule();
            var greeter = DefineInterface(module, "IGreeter");
            greeter.AddMethod(AbstractMethod(module, "greet"));

            var a = DefineClass(module, "A", interfaces: new[] { greeter });
            a.AddMethod(NativeMethod(module, "greet", dispatch: SurtrMethodDispatch.Virtual));

            var b = DefineClass(module, "B", baseClass: a);

            SurtrTypeLinker.LinkModule(module);

            Assert.Contains(greeter, b.Interfaces);
            Assert.True(b.Implements(greeter));
        }

        #endregion

        #region Concrete/abstract verification

        [Fact]
        public void AnAbstractClass_MayLeaveAnAbstractMethodUnimplemented()
        {
            var module = NewModule();
            var a = DefineClass(module, "A", isAbstract: true);
            a.AddMethod(AbstractMethod(module, "speak"));

            SurtrTypeLinker.LinkModule(module);

            Assert.Equal(SurtrMethodImplKind.Abstract, a.VirtualMethods[0].ImplKind);
        }

        [Fact]
        public void AConcreteClass_LeavingAnInheritedAbstractMethodUnimplemented_Throws()
        {
            var module = NewModule();
            var a = DefineClass(module, "A", isAbstract: true);
            a.AddMethod(AbstractMethod(module, "speak"));

            DefineClass(module, "B", baseClass: a); // does not implement "speak"

            Assert.Throws<InvalidOperationException>(() => SurtrTypeLinker.LinkModule(module));
        }

        [Fact]
        public void AConcreteClass_ImplementingAnInheritedAbstractMethod_Links()
        {
            var module = NewModule();
            var a = DefineClass(module, "A", isAbstract: true);
            a.AddMethod(AbstractMethod(module, "speak"));

            var b = DefineClass(module, "B", baseClass: a);
            b.AddMethod(NativeMethod(module, "speak", dispatch: SurtrMethodDispatch.Virtual, isOverride: true));

            SurtrTypeLinker.LinkModule(module);

            Assert.Equal(SurtrMethodImplKind.Native, b.VirtualMethods[0].ImplKind);
        }

        #endregion

        #region Cycle detection

        [Fact]
        public void ACyclicClassHierarchy_ThrowsInsteadOfLoopingForever()
        {
            var module = NewModule();

            var selfReferenceA = SurtrClassReference.Object("test:A");
            var selfReferenceB = SurtrClassReference.Object("test:B");
            var handleA = module.TypeHandles.GetOrAdd(selfReferenceA);
            var handleB = module.TypeHandles.GetOrAdd(selfReferenceB);

            var a = new SurtrClass("A", SurtrValueTypeCode.Object, selfReferenceA, handleB, false, SurtrVisibility.Public, null);
            var b = new SurtrClass("B", SurtrValueTypeCode.Object, selfReferenceB, handleA, false, SurtrVisibility.Public, null);
            handleA.Resolve(a);
            handleB.Resolve(b);

            module.AddClass(a);
            module.AddClass(b);

            Assert.Throws<InvalidOperationException>(() => SurtrTypeLinker.LinkModule(module));
        }

        [Fact]
        public void ABaseTypeHandle_ThatWasNeverResolved_ThrowsAClearError()
        {
            var module = NewModule();
            var unresolvedHandle = module.TypeHandles.GetOrAdd(SurtrClassReference.Object("test:Missing"));

            var a = new SurtrClass("A", SurtrValueTypeCode.Object, SurtrClassReference.Object("test:A"), unresolvedHandle, false, SurtrVisibility.Public, null);
            module.AddClass(a);

            Assert.Throws<InvalidOperationException>(() => SurtrTypeLinker.LinkModule(module));
        }

        #endregion

        #region Module-level members

        [Fact]
        public void AModuleLevelField_ThatIsNotStatic_Throws()
        {
            var module = NewModule();
            module.AddField(Field(module, "x", SurtrClassReference.Integer, isStatic: false));

            Assert.Throws<InvalidOperationException>(() => SurtrTypeLinker.LinkModule(module));
        }

        [Fact]
        public void AModuleLevelMethod_ThatIsVirtual_Throws()
        {
            var module = NewModule();
            module.AddMethod(NativeMethod(module, "foo", dispatch: SurtrMethodDispatch.Virtual));

            Assert.Throws<InvalidOperationException>(() => SurtrTypeLinker.LinkModule(module));
        }

        [Fact]
        public void AModuleStaticInitializer_IsKeptSeparateFromOrdinaryFunctions()
        {
            var module = NewModule();
            var initializer = NativeMethod(module, "<init>", role: SurtrMethodRole.StaticInitializer, isStatic: true);
            var function = NativeMethod(module, "foo", isStatic: true);
            module.AddMethod(initializer);
            module.AddMethod(function);

            SurtrTypeLinker.LinkModule(module);

            Assert.Same(initializer, module.StaticInitializer);
            Assert.DoesNotContain(initializer, module.Functions);
            Assert.Contains(function, module.Functions);
        }

        [Fact]
        public void ModuleLevelStaticFields_AreLaidOutAndTracedTheSameWayClassStaticsAre()
        {
            var module = NewModule();
            module.AddField(Field(module, "label", SurtrClassReference.String, isStatic: true));
            module.AddField(Field(module, "count", SurtrClassReference.Integer, isStatic: true));

            SurtrTypeLinker.LinkModule(module);

            Assert.Equal(2, module.StaticFields.Length);
            Assert.Equal(1, module.ReferenceStaticSlots.Length);
            Assert.Equal(0, module.ReferenceStaticSlots[0]); // "label", the only reference-typed static
        }

        #endregion

        #region Nested types

        [Fact]
        public void NestedClasses_AreLinkedAsPartOfLinkingTheEnclosingClass()
        {
            var module = NewModule();
            var outer = DefineClass(module, "Outer");

            var innerSelfReference = SurtrClassReference.Object("test:Outer.Inner");
            var inner = new SurtrClass("Inner", SurtrValueTypeCode.Object, innerSelfReference, null, false, SurtrVisibility.Public, null);
            outer.AddNestedClass(inner);

            SurtrTypeLinker.LinkModule(module);

            Assert.True(inner.IsBuilt);
            Assert.Equal(new[] { inner }, inner.Ancestors);
        }

        #endregion
    }
}
