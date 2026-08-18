#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Tests.VM
{
    public class SurtrVirtualMachineCallTests
    {
        #region Shared helpers

        // Named static methods, not lambdas: even a non-capturing lambda may compile as an
        // instance method on a compiler-generated singleton cache class, which gives it a
        // non-null Target and trips SurtrNativeEntryPoint.FromDelegate's "must be static" check.
        // A method group conversion carries no such ambiguity (see SurtrTypeLinkerTests for where
        // this was first found).
        private static SurtrValue Return999(SurtrCallArguments arguments) => SurtrValue.CreateInt(999);

        private static SurtrNativeMethodInfo NativeMethod(
            SurtrModule module,
            string name,
            SurtrMethodDispatch dispatch,
            bool isOverride,
            SurtrNativeEntryPoint entryPoint,
            int parameterCount = 0)
        {
            var parameters = new SurtrParameterInfo[parameterCount];
            for (int i = 0; i < parameterCount; i++)
                parameters[i] = new SurtrParameterInfo($"p{i}", module.TypeHandles.GetOrAdd(SurtrClassReference.Integer));

            return new SurtrNativeMethodInfo(
                name, dispatch, SurtrMethodRole.Normal, isOverride,
                module.TypeHandles.GetOrAdd(SurtrClassReference.Integer), parameters,
                isStatic: dispatch == SurtrMethodDispatch.Direct, SurtrVisibility.Public, declaringType: null, entryPoint);
        }

        private static SurtrAbstractMethodInfo AbstractMethod(SurtrModule module, string name, SurtrTypeHandle declaringType)
            => new(name, module.TypeHandles.GetOrAdd(SurtrClassReference.Integer), Array.Empty<SurtrParameterInfo>(), SurtrVisibility.Public, declaringType);

        /// <summary>Builds a standalone bytecode method that just returns <paramref name="value"/>.</summary>
        private static SurtrBytecodeMethodInfo BuildConstantMethod(int value)
        {
            var module = new SurtrModule("callee");
            var builder = new BytecodeBuilder().Op(OpCode.PushI32).I32(value).Op(OpCode.ReturnValue);
            return builder.Build(module, localCount: 0, maxStackSize: 4);
        }

        #endregion

        #region CallLocalModule / InvokeSpecial / InvokeStatic - all load from the local method table

        [Theory]
        [InlineData(OpCode.CallLocalModule)]
        [InlineData(OpCode.InvokeSpecial)]
        [InlineData(OpCode.InvokeStatic)]
        public void TwoByteMethodTableCall_InvokesTheTargetMethod(OpCode op)
        {
            using var runtime = new SurtrRuntime();
            var callee = BuildConstantMethod(77);

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(callee);

            builder.Op(op).I16(methodIndex).U8(0).U8(1).Op(OpCode.ReturnValue);

            var caller = builder.Build(callerModule, localCount: 0, maxStackSize: 4);
            Assert.Equal(77, runtime.Invoke(caller).AsInt);
        }

        [Theory]
        [InlineData(OpCode.CallLocalModuleX)]
        [InlineData(OpCode.InvokeStaticX)]
        public void FourByteMethodTableCall_InvokesTheTargetMethod(OpCode op)
        {
            using var runtime = new SurtrRuntime();
            var callee = BuildConstantMethod(88);

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(callee);

            builder.Op(op).I32(methodIndex).U8(0).U8(1).Op(OpCode.ReturnValue);

            var caller = builder.Build(callerModule, localCount: 0, maxStackSize: 4);
            Assert.Equal(88, runtime.Invoke(caller).AsInt);
        }

        [Fact]
        public void Call_PassesArgumentsThroughToTheCallee()
        {
            using var runtime = new SurtrRuntime();

            var calleeModule = new SurtrModule("callee");
            var callee = new BytecodeBuilder()
                .Op(OpCode.Ldl0).Op(OpCode.Ldl1).Op(OpCode.Add).Op(OpCode.ReturnValue)
                .Build(calleeModule, localCount: 2, maxStackSize: 4);

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(callee);

            builder
                .Op(OpCode.PushI32).I32(3).Op(OpCode.PushI32).I32(4)
                .Op(OpCode.InvokeStatic).I16(methodIndex).U8(2).U8(1)
                .Op(OpCode.ReturnValue);

            var caller = builder.Build(callerModule, localCount: 0, maxStackSize: 8);
            Assert.Equal(7, runtime.Invoke(caller).AsInt);
        }

        #endregion

        #region CallModule - a two-step lookup through another module's own method table

        [Fact]
        public void CallModule_InvokesAMethodThroughAnotherModulesTable()
        {
            using var runtime = new SurtrRuntime();
            var calleeModule = new SurtrModule("callee");
            var calleeMethod = new BytecodeBuilder().Op(OpCode.PushI32).I32(55).Op(OpCode.ReturnValue)
                .Build(calleeModule, localCount: 0, maxStackSize: 4);

            // The callee's own chunk names itself at index 0 in its method table - CallModule
            // resolves through the target module's table, not the caller's.
            calleeModule.Chunk.MethodTable = new SurtrMethodInfo[] { calleeMethod };

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int moduleIndex = builder.AddModule(calleeModule);

            builder.Op(OpCode.CallModule).I16(moduleIndex).I16(0).U8(0).U8(1).Op(OpCode.ReturnValue);

            var caller = builder.Build(callerModule, localCount: 0, maxStackSize: 4);
            Assert.Equal(55, runtime.Invoke(caller).AsInt);
        }

        [Fact]
        public void CallModuleX_UsesFourByteIndices()
        {
            using var runtime = new SurtrRuntime();
            var calleeModule = new SurtrModule("callee");
            var calleeMethod = new BytecodeBuilder().Op(OpCode.PushI32).I32(66).Op(OpCode.ReturnValue)
                .Build(calleeModule, localCount: 0, maxStackSize: 4);

            calleeModule.Chunk.MethodTable = new SurtrMethodInfo[] { calleeMethod };

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int moduleIndex = builder.AddModule(calleeModule);

            builder.Op(OpCode.CallModuleX).I32(moduleIndex).I32(0).U8(0).U8(1).Op(OpCode.ReturnValue);

            var caller = builder.Build(callerModule, localCount: 0, maxStackSize: 4);
            Assert.Equal(66, runtime.Invoke(caller).AsInt);
        }

        #endregion

        #region InvokeVirtual - resolves through the receiver's own vtable, override included

        [Fact]
        public void InvokeVirtual_OnABaseInstance_CallsTheBasesBytecodeBody()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");

            var baseType = VmMetadataHelpers.DefineClass(module, "A");
            var baseModule = new SurtrModule("A-body");
            var baseSpeak = new BytecodeBuilder().Op(OpCode.PushI32).I32(111).Op(OpCode.ReturnValue)
                .Build(baseModule, localCount: 1, maxStackSize: 4, dispatch: SurtrMethodDispatch.Virtual, name: "speak");
            baseType.AddMethod(baseSpeak);

            SurtrTypeLinker.LinkModule(module);

            var instance = runtime.NewInstance(baseType);

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(baseSpeak);

            builder.LoadReference(instance).Op(OpCode.InvokeVirtual).I16(methodIndex).U8(1).U8(1).Op(OpCode.ReturnValue);
            var caller = builder.Build(callerModule, localCount: 0, maxStackSize: 8);

            Assert.Equal(111, runtime.Invoke(caller).AsInt);
        }

        [Fact]
        public void InvokeVirtual_OnADerivedInstance_ResolvesToTheOverride_EvenWhenItIsNative()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");

            var baseType = VmMetadataHelpers.DefineClass(module, "A");
            var baseModule = new SurtrModule("A-body");
            var baseSpeak = new BytecodeBuilder().Op(OpCode.PushI32).I32(111).Op(OpCode.ReturnValue)
                .Build(baseModule, localCount: 1, maxStackSize: 4, dispatch: SurtrMethodDispatch.Virtual, name: "speak");
            baseType.AddMethod(baseSpeak);

            var derivedType = VmMetadataHelpers.DefineClass(module, "B", baseClass: baseType);
            var overrideSpeak = NativeMethod(
                module, "speak", SurtrMethodDispatch.Virtual, isOverride: true,
                SurtrNativeEntryPoint.FromDelegate(Return999));
            derivedType.AddMethod(overrideSpeak);

            SurtrTypeLinker.LinkModule(module);

            var instance = runtime.NewInstance(derivedType);

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            // The call site names the *base's* declared method - only its VTableSlot matters here.
            int methodIndex = builder.AddMethod(baseSpeak);

            builder.LoadReference(instance).Op(OpCode.InvokeVirtual).I16(methodIndex).U8(1).U8(1).Op(OpCode.ReturnValue);
            var caller = builder.Build(callerModule, localCount: 0, maxStackSize: 8);

            Assert.Equal(999, runtime.Invoke(caller).AsInt);
        }

        #endregion

        #region InvokeInterface - one extra indirection through the class's interface dispatch block

        [Fact]
        public void InvokeInterface_ResolvesThroughTheClasssDispatchTable()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");

            var greeterSelfRef = SurtrClassReference.Object("test:IGreeter");
            var greeter = new SurtrInterface("IGreeter", greeterSelfRef, SurtrVisibility.Public, declaringType: null);
            var greeterHandle = VmMetadataHelpers.HandleFor(module, greeter);
            var greet = AbstractMethod(module, "greet", greeterHandle);
            greeter.AddMethod(greet);
            module.AddInterface(greeter);

            var implementor = VmMetadataHelpers.DefineClass(module, "A", interfaces: new[] { greeter });
            var implementorModule = new SurtrModule("A-body");
            var greetImpl = new BytecodeBuilder().Op(OpCode.PushI32).I32(42).Op(OpCode.ReturnValue)
                .Build(implementorModule, localCount: 1, maxStackSize: 4, dispatch: SurtrMethodDispatch.Virtual, name: "greet");
            implementor.AddMethod(greetImpl);

            SurtrTypeLinker.LinkModule(module);

            var instance = runtime.NewInstance(implementor);

            var callerModule = new SurtrModule("caller");
            var builder = new BytecodeBuilder();
            int methodIndex = builder.AddMethod(greet); // the *interface's* method, not the class's

            builder.LoadReference(instance).Op(OpCode.InvokeInterface).I16(methodIndex).U8(1).U8(1).Op(OpCode.ReturnValue);
            var caller = builder.Build(callerModule, localCount: 0, maxStackSize: 8);

            Assert.Equal(42, runtime.Invoke(caller).AsInt);
        }

        #endregion
    }
}
