#nullable enable

using Surtr.Bytecode;
using Surtr.Bytecode.Emit;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Diagnostics;

namespace Surtr.Bench
{
    /// <summary>
    /// Measures what the <see cref="OpCode.Ext"/> prefix costs per dispatch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The extended instruction space rests on one number: how much a nested indirect branch
    /// costs relative to the interpreter's own. Everything else - which superinstructions are
    /// worth a prefix, and which specialisations must stay out of the extended space entirely -
    /// follows from it, and the rule of thumb ("about one dispatch") is an estimate until
    /// something weighs it. This weighs it.
    /// </para>
    /// <para>
    /// The experiment is deliberately null: two hand-emitted functions, byte-identical except
    /// that one loads its locals with <see cref="OpCode.LdlS"/> and the other with
    /// <see cref="SurtrExtOpCode.Probe"/>, which does exactly the same thing through the prefix.
    /// Same loop, same locals, same arithmetic, same number of instructions executed. The whole
    /// difference between the two timings is the prefix, divided by how many prefixed
    /// instructions ran.
    /// </para>
    /// <para>
    /// It is not a <c>Workload</c> because a workload is Surtr source, and no Surtr source can
    /// name <see cref="SurtrExtOpCode.Probe"/> - the compiler never emits it. Reaching for the
    /// builders directly is the point.
    /// </para>
    /// </remarks>
    internal static class PrefixTax
    {
        /// <summary>How many local loads the loop body performs per iteration.</summary>
        /// <remarks>
        /// The signal is the tax times this number, so a larger body separates it from timer
        /// noise. Eight keeps the body well inside a cache line's worth of instructions while
        /// still making the loop step a small fraction of what is being measured.
        /// </remarks>
        private const int LoadsPerIteration = 8;

        public static int Run(long iterations, int samples)
        {
            Console.WriteLine("prefix tax — the price of one Ext dispatch");
            Console.WriteLine();
            Console.WriteLine($"  {iterations:N0} iterations x {LoadsPerIteration} loads = {iterations * LoadsPerIteration:N0} measured dispatches per sample");
            Console.WriteLine($"  {samples} samples, median reported");
            Console.WriteLine();

            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("prefixtax");
            var plain = Emit(builder, "plain", prefixed: false);
            var prefixed = Emit(builder, "prefixed", prefixed: true);

            runtime.LoadModule(builder.Build());

            var arguments = new[] { SurtrValue.CreateInt((int)iterations) };

            // Both bodies must answer the same thing, or they are not the same experiment.
            long plainAnswer = runtime.Invoke(plain.Built!, arguments).AsInt;
            long prefixedAnswer = runtime.Invoke(prefixed.Built!, arguments).AsInt;

            if (plainAnswer != prefixedAnswer)
            {
                Console.Error.WriteLine($"the two bodies disagree: plain={plainAnswer}, prefixed={prefixedAnswer}. The experiment is not null.");
                return 1;
            }

            // Warm up both, so neither pays for the other's tiering or first-touch page faults.
            for (int i = 0; i < 3; i++)
            {
                runtime.Invoke(plain.Built!, arguments);
                runtime.Invoke(prefixed.Built!, arguments);
            }

            var plainSamples = new double[samples];
            var prefixedSamples = new double[samples];

            // Interleaved rather than one block each: a thermal or frequency drift halfway
            // through then hits both columns instead of only the second one.
            for (int i = 0; i < samples; i++)
            {
                plainSamples[i] = TimeOne(runtime, plain.Built!, arguments);
                prefixedSamples[i] = TimeOne(runtime, prefixed.Built!, arguments);
            }

            double plainMs = Median(plainSamples);
            double prefixedMs = Median(prefixedSamples);

            double deltaMs = prefixedMs - plainMs;
            double taxNs = deltaMs * 1_000_000.0 / (iterations * LoadsPerIteration);

            double plainSpread = Spread(plainSamples);
            double prefixedSpread = Spread(prefixedSamples);

            Console.WriteLine($"  LdlS       {plainMs,9:F3} ms   spread {plainSpread,5:P1}");
            Console.WriteLine($"  Ext/Probe  {prefixedMs,9:F3} ms   spread {prefixedSpread,5:P1}");
            Console.WriteLine();
            Console.WriteLine($"  prefix tax {taxNs,9:F3} ns per prefixed dispatch");
            Console.WriteLine();

            double worstSpread = Math.Max(plainSpread, prefixedSpread);
            if (worstSpread > 0.10)
                Console.WriteLine($"  WARNING: spread is {worstSpread:P1}; raise --iters or quiet the machine before trusting this.");

            // The verdict the design document asks for, stated here so a run answers the question
            // it was run to answer rather than leaving a number to be interpreted later.
            Console.WriteLine("  verdict:");
            if (taxNs <= 0.8)
                Console.WriteLine("    cheap. Every group in docs/Plan-Opcodes-Extendidos.md §5 clears the bar, fused single-dispatch savers included.");
            else if (taxNs <= 1.5)
                Console.WriteLine("    as modelled. Superinstructions (groups A, B, C) win comfortably; group D's one-dispatch savers are marginal and need their own A/B.");
            else
                Console.WriteLine("    expensive. Only the loop superinstructions of group A are clearly worth a prefix; consider spending primary values 0xF0-0xFE on them instead.");

            return 0;
        }

        private static double TimeOne(SurtrRuntime runtime, SurtrMethodInfo method, SurtrValue[] arguments)
        {
            var watch = Stopwatch.StartNew();
            runtime.Invoke(method, arguments);
            watch.Stop();
            return watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>
        /// Emits <c>f(n) = sum over i in [0, n) of (LoadsPerIteration * i)</c>, with every load of
        /// <c>i</c> going through either <see cref="OpCode.LdlS"/> or
        /// <see cref="SurtrExtOpCode.Probe"/>.
        /// </summary>
        /// <remarks>
        /// The padding locals exist so the accumulator and the counter land past slot 5, where the
        /// dedicated single-byte forms stop: without them the grouped helper would pick
        /// <c>Ldl0</c>..<c>Ldl5</c> for the plain body and the comparison would be against a
        /// one-byte instruction rather than against the two-byte one the probe actually mirrors.
        /// </remarks>
        private static SurtrMethodBuilder Emit(SurtrModuleBuilder builder, string name, bool prefixed)
        {
            var parameters = new[]
            {
                new SurtrParameterInfo("n", builder.TypeHandle(SurtrClassReference.Integer))
            };

            var method = builder.DefineFunction(name, SurtrClassReference.Integer, parameters);

            for (int i = 0; i < 6; i++)
                method.DeclareLocal("$pad" + i);

            var accumulator = method.DeclareLocal("acc");
            var counter = method.DeclareLocal("i");

            int accumulatorSlot = accumulator.Index;
            int counterSlot = counter.Index;
            int limitSlot = method.Parameter(0).Index;

            var code = method.Code;

            code.LoadInt(0).StoreLocal(accumulator);
            code.LoadInt(0).StoreLocal(counter);

            var top = code.NewLabel();
            var end = code.NewLabel();

            code.MarkLabel(top);
            code.LoadLocal(counter);
            code.LoadLocal(method.Parameter(0));
            code.JumpIfCompare(SurtrComparison.GreaterOrEqual, SurtrValueTypeCode.Integer, end);

            // The measured region: LoadsPerIteration loads of the counter, folded into one value.
            for (int i = 0; i < LoadsPerIteration; i++)
            {
                if (prefixed)
                    code.Probe(counterSlot);
                else
                    code.LdlS(counterSlot);

                if (i > 0)
                    code.Add(SurtrValueTypeCode.Integer);
            }

            code.LoadLocal(accumulator);
            code.Add(SurtrValueTypeCode.Integer);
            code.StoreLocal(accumulator);

            code.IncrementLocal(counter, 1);
            code.Jump(top);

            code.MarkLabel(end);
            code.LoadLocal(accumulator);
            code.ReturnValue();

            // Read once so the unused-variable analysis does not hide a slot mistake: both must be
            // addressable as single bytes, which is what the probe's encoding assumes.
            if (accumulatorSlot > byte.MaxValue || counterSlot > byte.MaxValue || limitSlot > byte.MaxValue)
                throw new InvalidOperationException("the probe body outgrew single-byte slot operands.");

            return method;
        }

        private static double Median(double[] samples)
        {
            var sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            return sorted.Length % 2 == 1
                ? sorted[sorted.Length / 2]
                : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;
        }

        private static double Spread(double[] samples)
        {
            double min = double.MaxValue;
            double max = double.MinValue;

            foreach (double sample in samples)
            {
                if (sample < min) min = sample;
                if (sample > max) max = sample;
            }

            return min <= 0 ? 0 : (max - min) / min;
        }
    }
}
