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
        DelegateInvoke,
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
        internal Delegate? DelegateValue;

        /// <summary>
        /// The receiver's inline layout, when the member is declared on a struct exposed with
        /// <c>Inline = true</c>. Null for a static member and for an ordinary native class.
        /// </summary>
        /// <remarks>
        /// An inline receiver is not a reference the registry can resolve - it is the block itself,
        /// sitting in the argument slots - so it has to be rebuilt rather than looked up.
        /// </remarks>
        internal SurtrValueLayout? ReceiverLayout;

        /// <summary>
        /// Per-parameter inline layouts, parallel to <see cref="Parameters"/>. An entry is null
        /// where the parameter takes a single slot, which is the usual case.
        /// </summary>
        internal SurtrValueLayout?[] ParameterLayouts = Array.Empty<SurtrValueLayout?>();

        /// <summary>The result's inline layout, when the member returns an inline struct.</summary>
        internal SurtrValueLayout? ResultLayout;
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
        public static int Invoke(SurtrCallArguments args, int index)
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
                typeof(int),
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

        internal static int InvokeSlot(SurtrCallArguments args, ReflectionMemberSlot slot)
        {
            var runtime = args.Runtime;

            switch (slot.Kind)
            {
                case ReflectionMemberKind.FieldGetter:
                {
                    object? target = slot.IsStatic ? null : Receiver(args, 0);
                    object? value = slot.Field!.GetValue(target);
                    return args.Return(SurtrMarshaler.ToSurtr(runtime, value, slot.ResultDescriptor));
                }

                case ReflectionMemberKind.FieldSetter:
                {
                    object? target = slot.IsStatic ? null : Receiver(args, 0);
                    object? value = SurtrMarshaler.ToClr(runtime, args.GetValue(slot.IsStatic ? 0 : 1), slot.Field!.FieldType, slot.ResultDescriptor);
                    slot.Field!.SetValue(target, value);
                    return 0;
                }

                case ReflectionMemberKind.PropertyGetter:
                {
                    object? target = ReceiverOf(args, slot);
                    object? value = slot.Method!.Invoke(target, null);

                    // An inline result is its own flat block, the same as a method's.
                    if (slot.ResultLayout is { } layout)
                    {
                        var block = new SurtrValue[layout.Width];
                        layout.Write(runtime, value!, block, 0);
                        return args.Return(block);
                    }

                    return args.Return(SurtrMarshaler.ToSurtr(runtime, value, slot.ResultDescriptor));
                }

                case ReflectionMemberKind.PropertySetter:
                {
                    // A setter on an inline receiver writes to a copy that is discarded the moment
                    // this returns, so it cannot exist: an inline value's fields are read-only, and
                    // the scanner never declares one. Guarded here because the invoker is reached
                    // through a raw function pointer and a wrong slot would fail silently.
                    if (slot.ReceiverLayout is not null)
                    {
                        throw new InvalidOperationException(
                            "An inline value type has no writable member: a write would land on a copy that is "
                            + "discarded when the call returns.");
                    }

                    object? target = slot.IsStatic ? null : Receiver(args, 0);
                    var parameter = slot.Method!.GetParameters()[0];
                    object? value = SurtrMarshaler.ToClr(runtime, args.GetValue(slot.IsStatic ? 0 : 1), parameter.ParameterType, slot.ResultDescriptor);
                    slot.Method!.Invoke(target, new[] { value });
                    return 0;
                }

                case ReflectionMemberKind.DelegateInvoke:
                {
                    var delegateType = slot.DelegateValue!.GetType();
                    var invoke = delegateType.GetMethod("Invoke")!;
                    var parameters = invoke.GetParameters();
                    var clrArguments = new object?[parameters.Length];

                    for (int i = 0; i < parameters.Length; i++)
                        clrArguments[i] = SurtrMarshaler.ToClr(runtime, args.GetValue(i), parameters[i].ParameterType, slot.Parameters[i]);

                    object? result = invoke.Invoke(slot.DelegateValue, clrArguments);
                    return args.Return(SurtrMarshaler.ToSurtr(runtime, result, slot.ResultDescriptor));
                }

                default:
                    return InvokeMethodSlot(args, slot);
            }
        }

        private static int InvokeMethodSlot(SurtrCallArguments args, ReflectionMemberSlot slot)
        {
            var runtime = args.Runtime;
            var method = slot.Method!;
            var parameters = method.GetParameters();
            var clrArguments = new object?[parameters.Length];

            // An argument is not necessarily a slot: a parameter typed as an inline struct occupies
            // its whole block, so the walk advances by each parameter's width rather than by one.
            // The receiver is the same - an inline one takes its block's width off the front.
            int at = slot.IsStatic ? 0 : slot.ReceiverLayout?.Width ?? 1;

            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (parameter.IsOut || parameter.ParameterType.IsByRef)
                    continue;

                var layout = i < slot.ParameterLayouts.Length ? slot.ParameterLayouts[i] : null;

                if (layout is null)
                {
                    clrArguments[i] = SurtrMarshaler.ToClr(runtime, args.GetValue(at), parameter.ParameterType, slot.Parameters[i]);
                    at += 1;
                }
                else
                {
                    clrArguments[i] = layout.Read(runtime, args, at);
                    at += layout.Width;
                }
            }

            // An inline receiver is the block in the argument slots, not a reference the registry
            // can resolve, so it is rebuilt rather than looked up.
            object? result = method.Invoke(ReceiverOf(args, slot), clrArguments);

            int outCount = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].IsOut)
                    outCount++;
            }

            if (outCount == 0)
            {
                // An inline result is written as its own flat block: the declared return is a value
                // type, so `ResultSlotCount` is its width and the caller copies that many slots.
                if (slot.ResultLayout is { } resultLayout)
                {
                    var block = new SurtrValue[resultLayout.Width];
                    resultLayout.Write(runtime, result!, block, 0);
                    return args.Return(block);
                }

                return args.Return(SurtrMarshaler.ToSurtr(runtime, result, slot.ResultDescriptor));
            }

            bool voidReturn = method.ReturnType == typeof(void);

            if (outCount == 1 && voidReturn)
            {
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].IsOut)
                        return args.Return(SurtrMarshaler.ToSurtr(runtime, clrArguments[i], slot.ResultDescriptor));
                }
            }

            var elementTypes = slot.ResultDescriptor.GetTupleElementTypes();

            // One element marshals to exactly one slot, so the tuple's flattened width has to equal
            // its element count for the block written below to be the width the callee declared. A
            // nested-tuple element breaks that, and only a hand-written ReturnDescriptor can produce
            // one — so it is refused here rather than silently returning the wrong number of slots.
            if (elementTypes.Length != slot.ResultDescriptor.GetTupleFlattenedSlotWidth())
            {
                throw new InvalidOperationException(
                    $"The result descriptor '{slot.ResultDescriptor.Descriptor}' flattens to more slots than it has elements; "
                    + "a native method with out-parameters cannot declare a nested tuple as its result.");
            }

            var elements = new SurtrValue[elementTypes.Length];
            int next = 0;

            // `elements[next++] = f(elementTypes[next])` reads the wrong descriptor: C# sequences the
            // left-hand index before the right-hand operand, so the increment lands first and every
            // element was marshalled against its successor's type - the last one indexing past the
            // end. The increment is its own statement now so the two indices cannot drift apart.
            if (!voidReturn)
            {
                elements[next] = SurtrMarshaler.ToSurtr(runtime, result, elementTypes[next]);
                next++;
            }

            for (int i = 0; i < parameters.Length; i++)
            {
                if (!parameters[i].IsOut)
                    continue;

                elements[next] = SurtrMarshaler.ToSurtr(runtime, clrArguments[i], elementTypes[next]);
                next++;
            }

            // Written as a flat block, not as a reference to a packed SurtrTuple. A tuple is a value
            // type: `ResultSlotCount` is its flattened width, so the caller copies that many slots
            // back and a single reference in slot 0 would leave the rest of the block as whatever
            // the stack happened to hold. Every input was read above, before this first write, which
            // is what the in-place convention requires.
            return args.Return(elements);
        }

        /// <summary>
        /// The receiver a member runs against: an inline value type's is rebuilt from the block in
        /// the argument slots, everything else's is resolved as an entity.
        /// </summary>
        private static object? ReceiverOf(SurtrCallArguments args, ReflectionMemberSlot slot)
        {
            if (slot.IsStatic)
                return null;

            return slot.ReceiverLayout is { } layout
                ? layout.Read(args.Runtime, args, 0)
                : Receiver(args, 0);
        }

        private static object? Receiver(SurtrCallArguments args, int index)
        {
            var entity = args.Runtime.Resolve<SurtrNativeObject>(args.GetValue(index));
            return entity?.Target;
        }
    }
}

