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
    /// Which category of the Surtr-written stdlib to load — one flag per top-level directory
    /// under <c>Surtr.Stdlib/src/surtr/</c> (<c>collections/</c>, <c>core/</c>, <c>math/</c>,
    /// <c>text/</c>), so a sandboxed host can load only what it means to expose rather than
    /// everything <c>Surtr.Stdlib.Tool</c> compiled.
    /// </summary>
    /// <remarks>
    /// Coarse-grained on purpose: today no stdlib module imports another (confirmed by grep
    /// across <c>Surtr.Stdlib/src</c> — every module only names the built-in <c>surtr</c>
    /// module), so a category is exactly the unit a selection needs. If that stops being true,
    /// <see cref="SurtrStdlib.LoadInto(SurtrRuntime, IReadOnlyList{SurtrModuleImage})"/>'s
    /// fixed-point retry loop still makes an incomplete selection fail cleanly — a module
    /// naming one this selection left out simply never resolves — rather than loading with a
    /// silent hole.
    /// </remarks>
    [Flags]
    public enum StdlibModules
    {
        /// <summary>Nothing selected.</summary>
        None = 0,

        /// <summary><c>surtr/core/</c> — <c>Contracts</c>, <c>Exception</c>.</summary>
        Core = 1 << 0,

        /// <summary><c>surtr/math/</c> — <c>Math</c>, <c>Angle</c>.</summary>
        Math = 1 << 1,

        /// <summary><c>surtr/collections/</c> — <c>Collection</c>, <c>List</c>.</summary>
        Collections = 1 << 2,

        /// <summary><c>surtr/text/</c> — <c>StringBuilder</c>.</summary>
        Text = 1 << 3,

        /// <summary>Every category — equivalent to the unfiltered <c>LoadInto</c> overloads.</summary>
        All = Core | Math | Collections | Text,
    }

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
        /// <see cref="LoadInto(SurtrRuntime, IReadOnlyList{SurtrModuleImage})"/>, restricted to the
        /// images whose module falls under one of <paramref name="selection"/>'s categories.
        /// </summary>
        /// <remarks>
        /// Filters by each image's own <see cref="SurtrModuleImage.Path"/> rather than asking the
        /// caller to pre-sort them — a host handing this every stdlib image it has (embedded,
        /// loaded from disk, however it got them) can select a sandboxed subset with nothing more
        /// than the flag it wants.
        /// </remarks>
        /// <param name="runtime">The runtime to load into.</param>
        /// <param name="images">Every stdlib image available; only the ones <paramref name="selection"/> names are loaded.</param>
        /// <param name="selection">Which categories to load.</param>
        public static void LoadInto(SurtrRuntime runtime, IReadOnlyList<SurtrModuleImage> images, StdlibModules selection)
        {
            if (images is null)
                throw new ArgumentNullException(nameof(images));

            if (selection == StdlibModules.All)
            {
                LoadInto(runtime, images);
                return;
            }

            var selected = new List<SurtrModuleImage>();
            foreach (var image in images)
            {
                if (IsSelected(image.Path, selection))
                    selected.Add(image);
            }

            LoadInto(runtime, selected);
        }

        /// <summary>
        /// <see cref="LoadInto(SurtrRuntime, IReadOnlyList{SurtrModuleImage}, StdlibModules)"/> over
        /// raw image bytes — the shape embedded resources arrive in.
        /// </summary>
        public static void LoadInto(SurtrRuntime runtime, IEnumerable<byte[]> images, StdlibModules selection)
        {
            if (images is null)
                throw new ArgumentNullException(nameof(images));

            LoadInto(runtime, images.Select(bytes => SurtrModuleImage.FromBytes(bytes)).ToList(), selection);
        }

        /// <summary>
        /// Whether a stdlib module's path falls under one of <paramref name="selection"/>'s
        /// categories - <c>surtr.math.Math</c>'s second segment, <c>math</c>, against
        /// <see cref="StdlibModules.Math"/>.
        /// </summary>
        private static bool IsSelected(string modulePath, StdlibModules selection)
        {
            int firstDot = modulePath.IndexOf('.');
            if (firstDot < 0)
                return false;

            int secondDot = modulePath.IndexOf('.', firstDot + 1);
            string category = secondDot < 0
                ? modulePath.Substring(firstDot + 1)
                : modulePath.Substring(firstDot + 1, secondDot - firstDot - 1);

            var flag = category switch
            {
                "core" => StdlibModules.Core,
                "math" => StdlibModules.Math,
                "collections" => StdlibModules.Collections,
                "text" => StdlibModules.Text,
                _ => StdlibModules.None,
            };

            return flag != StdlibModules.None && (selection & flag) != 0;
        }

        /// <summary>
        /// Publishes the body every stdlib image can ask for, under the link name its declaration
        /// travels as.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One entry per module-level <c>native fun</c> currently declared in the Surtr-written
        /// stdlib. A module-level native's link name is its <em>module path plus member name</em>
        /// (§10) — <c>surtr.math.Math.sin</c>, not <c>sin</c> — so two modules declaring a
        /// same-named native never bind against the same body. A link name that grows a body
        /// elsewhere and stops needing one here is harmless to keep; one that stops being published
        /// here while its declaration still exists fails the load, which is the point.
        /// </para>
        /// <para>
        /// Internal rather than private so a test can call it directly against a throwaway runtime
        /// and compare what it registers against <c>Surtr.Stdlib/build/native-link-names.txt</c> —
        /// the flat list <c>Surtr.Stdlib.Tool</c> writes alongside the images themselves, of every
        /// native link name it actually compiled. That comparison is the drift detector: a
        /// <c>native fun</c> added to the stdlib source without a matching entry added here shows
        /// up as a name the manifest has and this method does not, before anyone loads a runtime
        /// and discovers it the hard way.
        /// </para>
        /// </remarks>
        internal static unsafe void RegisterNativeBodies(SurtrRuntime runtime)
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
