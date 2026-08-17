#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Stdlib.Native
{
    /// <summary>
    /// The C# bodies behind <c>surtr.math.Math</c>'s <c>native fun</c> declarations
    /// (<c>src/surtr/math/Math.surtr</c>) — the sixteen trig/float operations that need the CLR's
    /// <see cref="Math"/>, nothing else. Everything else <c>Math.surtr</c> exposes (<c>abs</c>,
    /// <c>min</c>, <c>max</c>, <c>clamp</c>, <c>sign</c>, the <c>pi</c>/<c>tau</c>/<c>epsilon</c>
    /// constants, ...) is ordinary <c>const fun</c>/<c>const</c> Surtr and needs no C# counterpart.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in <c>Surtr.Core</c> on purpose: <c>Math</c> is standard-library
    /// content, not core object-model content, so <c>Surtr.Core</c>'s built-in module carries only
    /// the primitives, collections and core interfaces that make up the language's own type system.
    /// A <see cref="Surtr.Runtime.Classes.SurtrNativeEntryPoint"/> only requires the target static
    /// method be visible to the caller taking its address, and every type this file touches
    /// (<see cref="SurtrCallArguments"/>, <see cref="SurtrValue"/>, <see cref="SurtrNativeEntryPoint"/>)
    /// is public on <c>Surtr.Core</c>'s surface, so no <c>InternalsVisibleTo</c> is needed to put
    /// this outside the assembly that declares them.
    /// </remarks>
    internal static unsafe class SurtrMathNative
    {
        // A module-level native has no receiver, so its first declared parameter is argument 0.
        // Internal: SurtrStdlib.RegisterNativeBodies publishes these under the link names
        // `surtr.math.Math`'s `native fun` declarations travel as.
        internal static SurtrValue MathSin(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Sin(arguments.GetValueUnchecked(0).AsFloat));

        internal static SurtrValue MathCos(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Cos(arguments.GetValueUnchecked(0).AsFloat));

        internal static SurtrValue MathTan(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Tan(arguments.GetValueUnchecked(0).AsFloat));

        internal static SurtrValue MathAsin(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Asin(arguments.GetValueUnchecked(0).AsFloat));

        internal static SurtrValue MathAcos(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Acos(arguments.GetValueUnchecked(0).AsFloat));

        internal static SurtrValue MathAtan(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Atan(arguments.GetValueUnchecked(0).AsFloat));

        internal static SurtrValue MathAtan2(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Atan2(arguments.GetValueUnchecked(0).AsFloat, arguments.GetValueUnchecked(1).AsFloat));

        internal static SurtrValue MathSqrt(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Sqrt(arguments.GetValueUnchecked(0).AsFloat));

        internal static SurtrValue MathPow(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Pow(arguments.GetValueUnchecked(0).AsFloat, arguments.GetValueUnchecked(1).AsFloat));

        internal static SurtrValue MathExp(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Exp(arguments.GetValueUnchecked(0).AsFloat));

        internal static SurtrValue MathLog(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Log(arguments.GetValueUnchecked(0).AsFloat));

        internal static SurtrValue MathLog10(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Log10(arguments.GetValueUnchecked(0).AsFloat));

        internal static SurtrValue MathFloor(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Floor(arguments.GetValueUnchecked(0).AsFloat));

        internal static SurtrValue MathCeil(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Ceiling(arguments.GetValueUnchecked(0).AsFloat));

        internal static SurtrValue MathRound(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Round(arguments.GetValueUnchecked(0).AsFloat, MidpointRounding.AwayFromZero));

        internal static SurtrValue MathHypot(SurtrCallArguments arguments)
            => SurtrValue.CreateFloat(Math.Sqrt(arguments.GetValueUnchecked(0).AsFloat * arguments.GetValueUnchecked(0).AsFloat
                + arguments.GetValueUnchecked(1).AsFloat * arguments.GetValueUnchecked(1).AsFloat));
    }
}
