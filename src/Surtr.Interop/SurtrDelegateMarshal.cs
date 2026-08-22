#nullable enable

using Surtr.Interop.Attributes;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Surtr.Interop
{
    /// <summary>
    /// Marshals CLR delegates to Surtr closures and back. A delegate becomes a
    /// <see cref="SurtrClosure"/> whose body is a native method invoking the delegate; a Surtr closure
    /// becomes a CLR delegate that forwards to the runtime's closure invocation. The closure
    /// direction uses the reflection fallback's shim machinery (non-AOT); the delegate direction
    /// compiles an expression (also non-AOT), which is fine because the source generator emits its
    /// own AOT-safe capturing lambdas and does not call these.
    /// </summary>
    public static class SurtrDelegateMarshal
    {
        /// <summary>Wraps a CLR delegate as a Surtr closure of <paramref name="closureDescriptor"/>'s signature.</summary>
        public static SurtrValue ToSurtr(SurtrRuntime runtime, Delegate value, SurtrClassReference closureDescriptor)
        {
            if (value is null)
                return SurtrValue.Null;

            var invoke = value.GetType().GetMethod("Invoke")!;
            var parameters = invoke.GetParameters();

            var parameterDescriptors = new SurtrClassReference[parameters.Length];
            var parameterInfos = new SurtrParameterInfo[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                parameterDescriptors[i] = SurtrTypeMapper.Map(parameters[i].ParameterType, SurtrNamingPolicy.Default);
                parameterInfos[i] = new SurtrParameterInfo(parameters[i].Name ?? "p" + i, runtime.TypeHandle(parameterDescriptors[i]));
            }

            var returnDescriptor = SurtrTypeMapper.Map(invoke.ReturnType, SurtrNamingPolicy.Default);

            var slot = new ReflectionMemberSlot
            {
                Kind = ReflectionMemberKind.DelegateInvoke,
                DelegateValue = value,
                ResultDescriptor = returnDescriptor,
                Parameters = parameterDescriptors,
            };

            var entryPoint = SurtrReflectionInvoker.Create(slot);

            var method = new SurtrNativeMethodInfo(
                "invoke",
                SurtrMethodDispatch.Direct,
                SurtrMethodRole.Normal,
                isOverride: false,
                runtime.TypeHandle(returnDescriptor),
                parameterInfos,
                isStatic: false,
                SurtrVisibility.Public,
                declaringType: null,
                entryPoint);

            var closure = runtime.NewClosure(method, Array.Empty<SurtrValue>(), closureDescriptor);
            runtime.AddRoot(closure);
            return SurtrValue.CreateReference(closure.GetSurtrReference());
        }

        /// <summary>Builds a CLR delegate of <paramref name="delegateType"/> that invokes a Surtr closure.</summary>
        public static object? ToClr(SurtrRuntime runtime, SurtrValue value, Type delegateType)
        {
            var closure = runtime.Resolve<SurtrClosure>(value);
            return closure is null ? null : ClosureToDelegate(runtime, closure, delegateType);
        }

        private static object ClosureToDelegate(SurtrRuntime runtime, SurtrClosure closure, Type delegateType)
        {
            var invoke = delegateType.GetMethod("Invoke")!;
            var parameters = invoke.GetParameters();

            var parameterTypes = new Type[parameters.Length];
            var parameterDescriptors = new SurtrClassReference[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                parameterTypes[i] = parameters[i].ParameterType;
                parameterDescriptors[i] = i < closure.TargetMethod.ParameterCount
                    ? closure.TargetMethod.Parameters[i].ParameterType.Reference
                    : SurtrTypeMapper.Map(parameterTypes[i], SurtrNamingPolicy.Default);
            }

            var holder = new ClosureInvoker
            {
                Runtime = runtime,
                Closure = closure,
                ParameterTypes = parameterTypes,
                ParameterDescriptors = parameterDescriptors,
                ReturnType = invoke.ReturnType,
                ReturnDescriptor = closure.TargetMethod.ReturnType.Reference,
            };

            var arguments = parameters.Select(static p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var objectArray = Expression.NewArrayInit(typeof(object), Array.ConvertAll(arguments, a => Expression.Convert(a, typeof(object))));
            var call = Expression.Call(Expression.Constant(holder), ClosureInvoker.InvokeMethod, objectArray);

            Expression body = invoke.ReturnType == typeof(void) ? call : Expression.Convert(call, invoke.ReturnType);
            return Expression.Lambda(delegateType, body, arguments).Compile();
        }

        private sealed class ClosureInvoker
        {
            public SurtrRuntime Runtime = null!;
            public SurtrClosure Closure = null!;
            public Type[] ParameterTypes = Array.Empty<Type>();
            public SurtrClassReference[] ParameterDescriptors = Array.Empty<SurtrClassReference>();
            public Type ReturnType = typeof(void);
            public SurtrClassReference ReturnDescriptor;

            public static readonly MethodInfo InvokeMethod = typeof(ClosureInvoker).GetMethod(nameof(Invoke))!;

            public object? Invoke(object?[] arguments)
            {
                var values = new SurtrValue[arguments.Length];
                for (int i = 0; i < arguments.Length; i++)
                    values[i] = SurtrMarshaler.ToSurtr(Runtime, arguments[i], ParameterDescriptors[i]);

                var result = Runtime.InvokeClosure(Closure, values);
                return SurtrMarshaler.ToClr(Runtime, result, ReturnType, ReturnDescriptor);
            }
        }
    }
}
