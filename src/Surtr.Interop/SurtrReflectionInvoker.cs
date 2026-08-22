#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Reflection;
using System.Reflection.Emit;

namespace Surtr.Interop
{
    internal enum ReflectionMemberKind
    {
        Method,
        FieldGetter,
        FieldSetter,
        PropertyGetter,
        PropertySetter,
    }

    /// <summary>The per-member dispatch record the reflection fallback embeds in a generated shim.</summary>
    internal sealed class ReflectionMemberSlot
    {
        internal ReflectionMemberKind Kind;
        internal MethodInfo? Method;
        internal FieldInfo? Field;
        internal bool IsStatic;
        internal SurtrClassReference ResultDescriptor;
        internal SurtrClassReference[] Parameters = Array.Empty<SurtrClassReference>();
    }

    /// <summary>The statically-reachable table of reflection dispatch records.</summary>
    internal static class SurtrReflectionSlots
    {
        internal static ReflectionMemberSlot[] Slots = Array.Empty<ReflectionMemberSlot>();
        internal static readonly object Lock = new object();
    }

    /// <summary>
    /// The public entry point a reflection shim calls. It has to be public because the shim lives in
    /// a Reflection.Emit assembly and so cannot reach internal members of this one; it receives the
    /// slot index and forwards to the per-slot dispatcher.
    /// </summary>
    public static class SurtrReflectionDispatch
    {
        /// <summary>Invokes the member recorded at <paramref name="index"/>.</summary>
        public static SurtrValue Invoke(SurtrCallArguments args, int index)
            => SurtrReflectionInvoker.InvokeSlot(args, SurtrReflectionSlots.Slots[index]);
    }

    /// <summary>
    /// Builds <see cref="SurtrNativeEntryPoint"/>s for the reflection fallback by emitting a static
    /// shim per member in a Reflection.Emit assembly. This is the fallback's only answer to the
    /// bridge's function-pointer convention, which forbids capturing state: the shim loads the
    /// member's slot index and forwards to <see cref="SurtrReflectionDispatch.Invoke"/>. A
    /// <see cref="DynamicMethod"/> cannot do this - its MethodHandle yields no function pointer - so
    /// the shim is a real method on an emitted type. Not AOT-safe by design.
    /// </summary>
    internal static class SurtrReflectionInvoker
    {
        private static readonly MethodInfo DispatchMethod =
            typeof(SurtrReflectionDispatch).GetMethod(nameof(SurtrReflectionDispatch.Invoke), BindingFlags.Public | BindingFlags.Static)!;

        private static readonly AssemblyBuilder ShimAssembly =
            AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Surtr.ReflectionShims"), AssemblyBuilderAccess.Run);

        private static readonly ModuleBuilder ShimModule = ShimAssembly.DefineDynamicModule("Shims");

        internal static SurtrNativeEntryPoint Create(ReflectionMemberSlot slot)
        {
            int index;
            lock (SurtrReflectionSlots.Lock)
            {
                index = SurtrReflectionSlots.Slots.Length;
                Array.Resize(ref SurtrReflectionSlots.Slots, index + 1);
                SurtrReflectionSlots.Slots[index] = slot;
            }

            var typeBuilder = ShimModule.DefineType("Shim" + index);
            var methodBuilder = typeBuilder.DefineMethod(
                "Invoke",
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(SurtrValue),
                new[] { typeof(SurtrCallArguments) });

            var il = methodBuilder.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4, index);
            il.Emit(OpCodes.Call, DispatchMethod);
            il.Emit(OpCodes.Ret);

            var methodInfo = typeBuilder.CreateType()!.GetMethod("Invoke")!;
            var function = (SurtrNativeFunction)Delegate.CreateDelegate(typeof(SurtrNativeFunction), methodInfo);
            return SurtrNativeEntryPoint.FromDelegate(function);
        }

        internal static SurtrValue InvokeSlot(SurtrCallArguments args, ReflectionMemberSlot slot)
        {
            var runtime = args.Runtime;

            switch (slot.Kind)
            {
                case ReflectionMemberKind.FieldGetter:
                {
                    object? target = slot.IsStatic ? null : Receiver(args, 0);
                    object? value = slot.Field!.GetValue(target);
                    return SurtrMarshaler.ToSurtr(runtime, value, slot.ResultDescriptor);
                }

                case ReflectionMemberKind.FieldSetter:
                {
                    object? target = slot.IsStatic ? null : Receiver(args, 0);
                    object? value = SurtrMarshaler.ToClr(runtime, args.GetValue(slot.IsStatic ? 0 : 1), slot.Field!.FieldType, slot.ResultDescriptor);
                    slot.Field!.SetValue(target, value);
                    return SurtrValue.Null;
                }

                case ReflectionMemberKind.PropertyGetter:
                {
                    object? target = slot.IsStatic ? null : Receiver(args, 0);
                    object? value = slot.Method!.Invoke(target, null);
                    return SurtrMarshaler.ToSurtr(runtime, value, slot.ResultDescriptor);
                }

                case ReflectionMemberKind.PropertySetter:
                {
                    object? target = slot.IsStatic ? null : Receiver(args, 0);
                    var parameter = slot.Method!.GetParameters()[0];
                    object? value = SurtrMarshaler.ToClr(runtime, args.GetValue(slot.IsStatic ? 0 : 1), parameter.ParameterType, slot.ResultDescriptor);
                    slot.Method!.Invoke(target, new[] { value });
                    return SurtrValue.Null;
                }

                default:
                    return InvokeMethodSlot(args, slot);
            }
        }

        private static SurtrValue InvokeMethodSlot(SurtrCallArguments args, ReflectionMemberSlot slot)
        {
            var runtime = args.Runtime;
            var method = slot.Method!;
            var parameters = method.GetParameters();
            var clrArguments = new object?[parameters.Length];

            int offset = slot.IsStatic ? 0 : 1;

            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (parameter.IsOut || parameter.ParameterType.IsByRef)
                    continue;

                clrArguments[i] = SurtrMarshaler.ToClr(runtime, args.GetValue(i + offset), parameter.ParameterType, slot.Parameters[i]);
            }

            object? receiver = slot.IsStatic ? null : Receiver(args, 0);
            object? result = method.Invoke(receiver, clrArguments);

            int outCount = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].IsOut)
                    outCount++;
            }

            if (outCount == 0)
                return SurtrMarshaler.ToSurtr(runtime, result, slot.ResultDescriptor);

            bool voidReturn = method.ReturnType == typeof(void);

            if (outCount == 1 && voidReturn)
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].IsOut)
                        return SurtrMarshaler.ToSurtr(runtime, clrArguments[i], slot.ResultDescriptor);
                }
            }

            var elementTypes = slot.ResultDescriptor.GetTupleElementTypes();
            var elements = new SurtrValue[elementTypes.Length];
            int next = 0;

            if (!voidReturn)
                elements[next++] = SurtrMarshaler.ToSurtr(runtime, result, elementTypes[next]);

            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].IsOut)
                    elements[next++] = SurtrMarshaler.ToSurtr(runtime, clrArguments[i], elementTypes[next]);
            }

            var tuple = runtime.NewTuple(slot.ResultDescriptor, elements);
            return SurtrValue.CreateReference(tuple.GetSurtrReference());
        }

        private static object? Receiver(SurtrCallArguments args, int index)
        {
            var entity = args.Runtime.Resolve<SurtrNativeObject>(args.GetValue(index));
            return entity?.Target;
        }
    }
}
