#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Runtime
{
    /// <summary>
    /// Loads the Surtr-written half of the standard library into a runtime: publishes every
    /// <c>native</c> body its images declare, then loads the images.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stdlib is split across two languages on one rule (<c>Language-Syntax.md</c> §13.1):
    /// "native if it needs <c>unsafe</c>, a raw pointer or a VM service; Surtr otherwise". The C#
    /// half is built into the process-wide built-in module in <see cref="SurtrBuiltIns"/>. The Surtr
    /// half is compiled to <c>.surtrc</c> images by <c>Surtr.Stdlib.Tool</c> — <c>surtr.math.Math</c>,
    /// <c>surtr.exceptions.Exceptions</c>, and so on — and reaches a runtime through here.
    /// </para>
    /// <para>
    /// A <c>native fun</c>/<c>native let</c>/<c>native var</c> travels in its image as a
    /// <see cref="SurtrNativeMethodInfo.LinkName"/> and nothing else (§10): the address can only
    /// come from the process doing the loading, so this publishes the bodies first, under the link
    /// names the images were compiled with, and then loads. A link name this does not publish fails
    /// the load exactly where an unregistered one does — <see cref="SurtrRuntime.LoadModule(SurtrModuleImage)"/>
    /// is the one place a <c>native</c> declaration binds to a host body.
    /// </para>
    /// <para>
    /// Modules load in the order given, retrying what does not yet resolve until nothing more can
    /// be made to — the same fixed-point pass <c>Surtr.Run</c>'s module set uses, because an image
    /// carries no dependency list until it is instantiated. The stdlib build output is sorted, and
    /// today every module only references the built-in <c>surtr</c> module, so order rarely
    /// matters; the retry is what keeps it true once the modules start referencing each other.
    /// </para>
    /// </remarks>
    public static class SurtrStdlib
    {
        /// <summary>
        /// Loads every <paramref name="images"/> into <paramref name="runtime"/> after registering
        /// the native bodies the stdlib's images declare.
        /// </summary>
        /// <param name="runtime">The runtime to load into. Must be the runtime the images are for.</param>
        /// <param name="images">The stdlib images, in load order (build order). Each is instantiated once, for this runtime.</param>
        /// <exception cref="InvalidOperationException">A module's type handles could not be resolved, or it declares a native member this loader does not publish.</exception>
        public static void LoadInto(SurtrRuntime runtime, IReadOnlyList<SurtrModuleImage> images)
        {
            if (runtime is null)
                throw new ArgumentNullException(nameof(runtime));

            if (images is null)
                throw new ArgumentNullException(nameof(images));

            RegisterNativeBodies(runtime);

            var pending = new List<SurtrModuleImage>(images);
            var failures = new List<string>();

            while (pending.Count > 0)
            {
                var stillPending = new List<SurtrModuleImage>();

                foreach (var image in pending)
                {
                    try
                    {
                        runtime.LoadModule(image);
                    }
                    catch (InvalidOperationException exception)
                    {
                        failures.Add($"'{image.Path}': {exception.Message}");
                        stillPending.Add(image);
                    }
                }

                if (stillPending.Count == pending.Count)
                {
                    throw new InvalidOperationException(
                        $"{stillPending.Count} stdlib module(s) could not be loaded:\n"
                        + string.Join("\n", failures.OrderBy(failure => failure, StringComparer.Ordinal)));
                }

                pending = stillPending;
            }
        }

        /// <summary>
        /// <see cref="LoadInto(SurtrRuntime, IReadOnlyList{SurtrModuleImage})"/> over raw image bytes
        /// — the shape embedded resources arrive in.
        /// </summary>
        public static void LoadInto(SurtrRuntime runtime, IEnumerable<byte[]> images)
        {
            if (images is null)
                throw new ArgumentNullException(nameof(images));

            LoadInto(runtime, images.Select(bytes => SurtrModuleImage.FromBytes(bytes)).ToList());
        }

        /// <summary>
        /// Publishes the body every stdlib image can ask for, under the link name its declaration
        /// travels as.
        /// </summary>
        /// <remarks>
        /// One entry per module-level <c>native fun</c> currently declared in the Surtr-written
        /// stdlib. A module-level native's link name is its <em>module path plus member name</em>
        /// (§10) — <c>surtr.math.Math.sin</c>, not <c>sin</c> — so two modules declaring a
        /// same-named native never bind against the same body. A link name that grows a body
        /// elsewhere and stops needing one here is harmless to keep; one that stops being published
        /// here while its declaration still exists fails the load, which is the point.
        /// </remarks>
        private static unsafe void RegisterNativeBodies(SurtrRuntime runtime)
        {
            // The bodies are the same ones the built-in `surtr:Math` class was built with; a
            // module-level native in `surtr.math.Math` binds by name to the very same static method.
            // Only the plumbing differs: here the link name carries the module path, and the
            // registration happens per runtime at load instead of once in the built-in's static
            // constructor.
            runtime.DefineNativeBody("surtr.math.Math.sin", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathSin));
            runtime.DefineNativeBody("surtr.math.Math.cos", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathCos));
            runtime.DefineNativeBody("surtr.math.Math.tan", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathTan));
            runtime.DefineNativeBody("surtr.math.Math.asin", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathAsin));
            runtime.DefineNativeBody("surtr.math.Math.acos", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathAcos));
            runtime.DefineNativeBody("surtr.math.Math.atan", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathAtan));
            runtime.DefineNativeBody("surtr.math.Math.atan2", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathAtan2));
            runtime.DefineNativeBody("surtr.math.Math.sqrt", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathSqrt));
            runtime.DefineNativeBody("surtr.math.Math.pow", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathPow));
            runtime.DefineNativeBody("surtr.math.Math.exp", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathExp));
            runtime.DefineNativeBody("surtr.math.Math.log", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathLog));
            runtime.DefineNativeBody("surtr.math.Math.log10", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathLog10));
            runtime.DefineNativeBody("surtr.math.Math.floor", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathFloor));
            runtime.DefineNativeBody("surtr.math.Math.ceil", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathCeil));
            runtime.DefineNativeBody("surtr.math.Math.round", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathRound));
            runtime.DefineNativeBody("surtr.math.Math.hypot", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrStandardLibrary.MathHypot));
        }
    }
}
