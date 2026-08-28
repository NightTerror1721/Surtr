#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Interop
{
    /// <summary>
    /// Public enum marshaling used by generated shims. From the migration an enum is its int
    /// (§2.4, §2.7): converting a CLR enum to a Surtr value and back is pure arithmetic — no
    /// proxy, no per-runtime cache, no "not registered" failure for a combination of bits that no
    /// case names.
    /// </summary>
    public static class SurtrEnums
    {
        /// <summary>Converts a boxed CLR enum value to the Surtr int it is.</summary>
        public static SurtrValue ToSurtr(SurtrRuntime runtime, object boxedValue)
            => SurtrValue.CreateInt(Convert.ToInt32(boxedValue, System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>Converts a Surtr enum value back to the CLR enum it names.</summary>
        public static TEnum ToClr<TEnum>(SurtrRuntime runtime, SurtrValue value) where TEnum : struct, Enum
            => (TEnum)Enum.ToObject(typeof(TEnum), value.AsInt);
    }
}