#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Threading;

namespace Surtr.Stdlib.Native
{
    /// <summary>
    /// The C# body behind <c>surtr.math.Random</c>'s one <c>native fun</c>
    /// (<c>src/surtr/math/Random.surtr</c>): a host-entropy seed for the parameterless constructor.
    /// The generator itself (xorshift32) is ordinary Surtr - determinism given a seed is the whole
    /// point of a PRNG a game replays or a test fixes, so only *picking* an unpredictable seed needs
    /// the host at all.
    /// </summary>
    internal static unsafe class SurtrRandomNative
    {
        // Never 0: xorshift32's state must never be zero, since 0 is a fixed point (it maps to
        // itself forever). Ticks alone would collide across two Random() built in the same tick on
        // a fast timer; XORing in a process-wide counter keeps successive calls distinct even then.
        private static int _counter;

        internal static int RandomSeed(SurtrCallArguments arguments)
        {
            int counter = Interlocked.Increment(ref _counter);
            int seed = Environment.TickCount ^ (int)DateTime.UtcNow.Ticks ^ (counter * unchecked((int)0x9E3779B9));
            return arguments.Return(SurtrValue.CreateInt(seed == 0 ? 1 : seed));
        }
    }
}
