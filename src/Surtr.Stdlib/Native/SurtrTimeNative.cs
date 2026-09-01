#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;

namespace Surtr.Stdlib.Native
{
    /// <summary>
    /// The C# body behind <c>surtr.time.DateTime</c>'s one <c>native fun</c>
    /// (<c>src/surtr/time/DateTime.surtr</c>): the current wall-clock time. Everything else -
    /// arithmetic, comparison, <c>Duration</c> - is ordinary Surtr over the sampled value, the same
    /// split <c>surtr.math.Random</c> uses for its entropy seed.
    /// </summary>
    internal static unsafe class SurtrTimeNative
    {
        internal static int CurrentUnixSeconds(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateFloat(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0));
    }
}
