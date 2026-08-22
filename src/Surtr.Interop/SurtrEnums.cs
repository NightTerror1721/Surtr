#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Interop
{
    /// <summary>
    /// Public enum marshaling used by generated shims. The per-runtime cache lives behind
    /// <c>SurtrInteropState</c>, which is internal, so generated code goes through this facade.
    /// </summary>
    public static class SurtrEnums
    {
        /// <summary>Converts a boxed CLR enum value to the Surtr reference of its cached case object.</summary>
        public static SurtrValue ToSurtr(SurtrRuntime runtime, object boxedValue)
        {
            var type = boxedValue.GetType();
            if (SurtrInteropState.For(runtime).TryGetEnumCache(type, out var cache))
                return SurtrValue.CreateReference(cache.GetReference(boxedValue));

            return SurtrValue.CreateInt(Convert.ToInt32(boxedValue, System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>Converts a Surtr enum reference back to the CLR enum value.</summary>
        public static TEnum ToClr<TEnum>(SurtrRuntime runtime, SurtrValue value) where TEnum : struct, Enum
        {
            var type = typeof(TEnum);
            if (SurtrInteropState.For(runtime).TryGetEnumCache(type, out var cache))
                return (TEnum)cache.FromReference(value);

            return (TEnum)Enum.ToObject(type, value.AsInt);
        }
    }
}
