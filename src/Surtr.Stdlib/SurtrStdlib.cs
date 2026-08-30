#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Stdlib.Native;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Surtr.Stdlib
{
    /// <summary>
    /// Which category of the Surtr-written stdlib to load â€” one flag per top-level directory
    /// under <c>Surtr.Stdlib/src/surtr/</c> (<c>collections/</c>, <c>core/</c>, <c>math/</c>,
    /// <c>text/</c>), so a sandboxed host can load only what it means to expose rather than
    /// everything <c>Surtr.Stdlib.Tool</c> compiled.
    /// </summary>
    /// <remarks>
    /// Coarse-grained by design, and independent: every exception the collections throw is one of
    /// the trap-mapped classes the built-in <c>surtr</c> module declares (implicitly in scope in
    /// every file), so no category reaches into another. A module importing a sibling inside its
    /// own category (<c>List</c> imports <c>Collection</c>) loads under either flag; the
    /// fixed-point retry loop underneath <see cref="SurtrStdlib.LoadInto(SurtrRuntime,
    /// IReadOnlyList{SurtrModuleImage})"/> remains as the backstop for a cross-category import
    /// this enum has not been told about.
    /// </remarks>
    [Flags]
    public enum StdlibModules
    {
        /// <summary>Nothing selected.</summary>
        None = 0,

        /// <summary><c>surtr/core/</c> — <c>Exception</c>.</summary>
        Core = 1 << 0,

        /// <summary><c>surtr/math/</c> â€” <c>Math</c>, <c>Angle</c>.</summary>
        Math = 1 << 1,

        /// <summary>
        /// <c>surtr/collections/</c> â€” <c>Collection</c>, <c>List</c>, <c>Queue</c>,
        /// <c>Sequence</c>, <c>Set</c>, <c>Stack</c>.
        /// </summary>
        Collections = 1 << 2,

        /// <summary><c>surtr/text/</c> â€” <c>StringBuilder</c>.</summary>
        Text = 1 << 3,

        /// <summary><c>surtr/io/</c> â€” <c>Enums</c>, <c>Stream</c>.</summary>
        Io = 1 << 4,

        /// <summary><c>surtr/diagnostics/</c> â€” <c>Assert</c>.</summary>
        Diagnostics = 1 << 5,

        /// <summary>Every category â€” equivalent to the unfiltered <c>LoadInto</c> overloads.</summary>
        All = Core | Math | Collections | Text | Io | Diagnostics,
    }

    /// <summary>
    /// Loads the Surtr-written standard library into a runtime: publishes every <c>native</c> body
    /// its images declare, then loads the images.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Surtr.Core</c> has no knowledge of any of this â€” its built-in module carries only the
    /// primitives, collections and core interfaces that make up the language's own type system
    /// (<c>Runtime/BuiltIns/SurtrBuiltIns.cs</c>). The standard library, <c>Math</c> included, is
    /// entirely this project's concern: optional, modular, and reached only by calling into here
    /// after constructing a <see cref="SurtrRuntime"/>. Nothing about that split needs
    /// <c>InternalsVisibleTo</c> or any other assembly-boundary special-casing â€” every
    /// <c>Surtr.Core</c> type this loader and <see cref="SurtrMathNative"/> touch
    /// (<c>SurtrRuntime</c>, <c>SurtrModuleImage</c>, <c>SurtrNativeEntryPoint</c>,
    /// <c>SurtrCallArguments</c>, <c>SurtrValue</c>) is already public, so <c>Surtr.Stdlib</c>
    /// consumes <c>Surtr.Core</c> the same way any other host would.
    /// </para>
    /// <para>
    /// The library is split across two languages on one rule (<c>Language-Syntax.md</c> Â§13.1):
    /// "native if it needs <c>unsafe</c>, a raw pointer or a VM service; Surtr otherwise". The
    /// native half lives in <see cref="Surtr.Stdlib.Native"/> â€” today just <c>Math</c>'s sixteen
    /// trig/float operations (<see cref="SurtrMathNative"/>) â€” compiled straight into this
    /// assembly. The Surtr half is compiled to <c>.surtrc</c> images by <c>Surtr.Stdlib.Tool</c> â€”
    /// <c>surtr.math.Math</c>, <c>surtr.core.Exception</c>, and so on â€” and reaches a runtime
    /// through here.
    /// </para>
    /// <para>
    /// A <c>native fun</c>/<c>native let</c>/<c>native var</c> travels in its image as a
    /// <c>SurtrNativeMethodInfo.LinkName</c> and nothing else (Â§10): the address can only come
    /// from the process doing the loading, so this publishes the bodies first, under the link
    /// names the images were compiled with, and then loads. A link name this does not publish
    /// fails the load exactly where an unregistered one does â€”
    /// <see cref="SurtrRuntime.LoadModule(SurtrModuleImage)"/> is the one place a <c>native</c>
    /// declaration binds to a host body.
    /// </para>
    /// <para>
    /// Modules load in the order given, retrying what does not yet resolve until nothing more can
    /// be made to â€” the same fixed-point pass <c>Surtr.Run</c>'s module set uses, because an image
    /// carries no dependency list until it is instantiated. The stdlib build output is sorted, and
    /// today a module references at most a sibling in the same category, so order rarely matters;
    /// the retry is what keeps it true as the modules grow into each other.
    /// </para>
    /// <para>
    /// The <c>.surtrc</c> images travel <em>inside this assembly</em>, embedded as resources by the
    /// <c>BuildStdlibImages</c> MSBuild target (<c>Surtr.Stdlib.csproj</c>) under the logical name
    /// scheme <see cref="EmbeddedResourcePrefix"/> reads back â€” so <see cref="LoadAll(SurtrRuntime)"/>
    /// and its <see cref="StdlibModules"/> overload work from nothing but a runtime, no file path or
    /// asset system required. That makes a Unity drop-in exactly "copy <c>Surtr.Core.dll</c> and
    /// <c>Surtr.Stdlib.dll</c> into <c>Assets/Plugins</c>" â€” <c>Assembly.GetManifestResourceStream</c>
    /// is a plain reflection read with nothing IL2CPP treats specially, unlike codegen-driven
    /// reflection. The <see cref="SurtrModuleImage"/>/<c>byte[]</c> overloads below still exist for a
    /// host that wants to source images another way (a fresh build, a different subset, its own
    /// asset pipeline).
    /// </para>
    /// </remarks>
    public static class SurtrStdlib
    {
        /// <summary>
        /// The manifest resource name every embedded stdlib image starts with â€” <c>Surtr.Stdlib.
        /// Images.surtr.math.Math.surtrc</c> for <c>surtr.math.Math</c> â€” followed by
        /// <see cref="SurtrModuleImage.FileExtension"/>. Fixed independently of <c>RootNamespace</c>
        /// or where <c>build/</c> lives on disk: <c>Surtr.Stdlib.csproj</c>'s
        /// <c>&lt;LogicalName&gt;</c> metadata sets it explicitly, so this string and that MSBuild
        /// target are the two halves of one contract and must agree.
        /// </summary>
        private const string EmbeddedResourcePrefix = "Surtr.Stdlib.Images.";

        /// <summary>
        /// Loads every stdlib image embedded in this assembly into <paramref name="runtime"/>.
        /// </summary>
        /// <remarks>
        /// The batteries-included entry point: after <c>new SurtrRuntime()</c>, this is the whole
        /// standard library in one call, no images to source or transport by hand.
        /// </remarks>
        /// <param name="runtime">The runtime to load into.</param>
        /// <exception cref="InvalidOperationException">A module's type handles could not be resolved, or it declares a native member this loader does not publish.</exception>
        public static void LoadAll(SurtrRuntime runtime) => LoadInto(runtime, EmbeddedImages());

        /// <summary>
        /// <see cref="LoadAll(SurtrRuntime)"/>, restricted to the embedded images under one or more
        /// of <paramref name="selection"/>'s categories.
        /// </summary>
        /// <param name="runtime">The runtime to load into.</param>
        /// <param name="selection">Which categories to load.</param>
        public static void LoadAll(SurtrRuntime runtime, StdlibModules selection) => LoadInto(runtime, EmbeddedImages(), selection);

        /// <summary>
        /// Every stdlib <c>.surtrc</c> image embedded in this assembly, read back through
        /// <see cref="Assembly.GetManifestResourceStream(string)"/>.
        /// </summary>
        /// <remarks>
        /// Filters <see cref="Assembly.GetManifestResourceNames"/> by <see cref="EmbeddedResourcePrefix"/>
        /// and <see cref="SurtrModuleImage.FileExtension"/> rather than tracking an explicit list, so
        /// a module added to <c>src/surtr/</c> is picked up the moment its image is embedded, with
        /// nothing here to update. A module's own path (used by the <see cref="StdlibModules"/>
        /// overloads) comes back out of the image itself once decoded
        /// (<see cref="SurtrModuleImage.Path"/>), so the resource name past the fixed prefix is never
        /// parsed - only used to recognise which manifest resources are stdlib images at all.
        /// </remarks>
        private static List<SurtrModuleImage> EmbeddedImages()
        {
            var assembly = typeof(SurtrStdlib).Assembly;
            var images = new List<SurtrModuleImage>();

            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith(EmbeddedResourcePrefix, StringComparison.Ordinal)
                    || !name.EndsWith(SurtrModuleImage.FileExtension, StringComparison.Ordinal))
                    continue;

                using Stream resource = assembly.GetManifestResourceStream(name)!;
                using var buffer = new MemoryStream();
                resource.CopyTo(buffer);
                images.Add(SurtrModuleImage.FromBytes(buffer.ToArray()));
            }

            return images;
        }

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
        /// â€” the shape embedded resources arrive in.
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
        /// caller to pre-sort them â€” a host handing this every stdlib image it has (embedded,
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
        /// raw image bytes â€” the shape embedded resources arrive in.
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
                "io" => StdlibModules.Io,
                "diagnostics" => StdlibModules.Diagnostics,
                _ => StdlibModules.None,
            };

            return flag != StdlibModules.None && (selection & flag) != 0;
        }

        /// <summary>
        /// <see cref="LoadInto(SurtrRuntime, IReadOnlyList{SurtrModuleImage})"/>, restricted to the
        /// images <paramref name="predicate"/> accepts by module path - full control over which
        /// individual modules load, for a host that wants finer granularity than
        /// <see cref="StdlibModules"/>'s per-category flags give (a single collection, not the
        /// whole <see cref="StdlibModules.Collections"/> category).
        /// </summary>
        /// <param name="runtime">The runtime to load into.</param>
        /// <param name="images">Every stdlib image available; only the ones <paramref name="predicate"/> accepts are loaded.</param>
        /// <param name="predicate">Given a module's path (<c>surtr.collections.Stack</c>), whether to load it.</param>
        public static void LoadInto(SurtrRuntime runtime, IReadOnlyList<SurtrModuleImage> images, Func<string, bool> predicate)
        {
            if (images is null)
                throw new ArgumentNullException(nameof(images));

            if (predicate is null)
                throw new ArgumentNullException(nameof(predicate));

            var selected = new List<SurtrModuleImage>();
            foreach (var image in images)
            {
                if (predicate(image.Path))
                    selected.Add(image);
            }

            LoadInto(runtime, selected);
        }

        /// <summary>
        /// Publishes the body every stdlib image can ask for, under the link name its declaration
        /// travels as.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One entry per module-level <c>native fun</c> currently declared in the Surtr-written
        /// stdlib â€” today all sixteen of them <c>surtr.math.Math</c>'s trig/float operations,
        /// bodied by <see cref="SurtrMathNative"/> in this same assembly. A module-level native's
        /// link name is its <em>module path plus member name</em> (Â§10) â€” <c>surtr.math.Math.sin</c>,
        /// not <c>sin</c> â€” so two modules declaring a same-named native never bind against the same
        /// body. A link name that grows a body elsewhere and stops needing one here is harmless to
        /// keep; one that stops being published here while its declaration still exists fails the
        /// load, which is the point.
        /// </para>
        /// <para>
        /// Internal rather than private so a test can call it directly against a throwaway runtime
        /// and compare what it registers against <c>Surtr.Stdlib/build/native-link-names.txt</c> â€”
        /// the flat list <c>Surtr.Stdlib.Tool</c> writes alongside the images themselves, of every
        /// native link name it actually compiled. That comparison is the drift detector: a
        /// <c>native fun</c> added to the stdlib source without a matching entry added here shows
        /// up as a name the manifest has and this method does not, before anyone loads a runtime
        /// and discovers it the hard way.
        /// </para>
        /// </remarks>
        internal static unsafe void RegisterNativeBodies(SurtrRuntime runtime)
        {
            runtime.DefineNativeBody("surtr.math.Math.sin", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathSin));
            runtime.DefineNativeBody("surtr.math.Math.cos", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathCos));
            runtime.DefineNativeBody("surtr.math.Math.tan", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathTan));
            runtime.DefineNativeBody("surtr.math.Math.asin", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathAsin));
            runtime.DefineNativeBody("surtr.math.Math.acos", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathAcos));
            runtime.DefineNativeBody("surtr.math.Math.atan", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathAtan));
            runtime.DefineNativeBody("surtr.math.Math.atan2", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathAtan2));
            runtime.DefineNativeBody("surtr.math.Math.sqrt", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathSqrt));
            runtime.DefineNativeBody("surtr.math.Math.pow", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathPow));
            runtime.DefineNativeBody("surtr.math.Math.exp", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathExp));
            runtime.DefineNativeBody("surtr.math.Math.log", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathLog));
            runtime.DefineNativeBody("surtr.math.Math.log10", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathLog10));
            runtime.DefineNativeBody("surtr.math.Math.floor", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathFloor));
            runtime.DefineNativeBody("surtr.math.Math.ceil", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathCeil));
            runtime.DefineNativeBody("surtr.math.Math.round", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathRound));
            runtime.DefineNativeBody("surtr.math.Math.hypot", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrMathNative.MathHypot));

            // Random
            runtime.DefineNativeBody("surtr.math.Random.randomSeed", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrRandomNative.RandomSeed));

            // ── surtr.diagnostics ───────────────────────────────────────────

            // Profiler
            runtime.DefineNativeBody("surtr.diagnostics.Profiler.stopwatchTimestamp", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrDiagnosticsNative.StopwatchTimestamp));

            // Debug
            runtime.DefineNativeBody("surtr.diagnostics.Debug.debugPrint", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrDiagnosticsNative.DebugPrint));
            runtime.DefineNativeBody("surtr.diagnostics.Debug.debugDump", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrDiagnosticsNative.DebugDump));
            runtime.DefineNativeBody("surtr.diagnostics.Debug.debugBreakpoint", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrDiagnosticsNative.DebugBreakpoint));
            runtime.DefineNativeBody("surtr.diagnostics.Debug.debugStack", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrDiagnosticsNative.DebugStack));
            runtime.DefineNativeBody("surtr.diagnostics.Debug.debugIsDebuggerAttached", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrDiagnosticsNative.DebugIsDebuggerAttached));

            // RuntimeInfo (native let property getters)
            runtime.DefineNativeBody("surtr.diagnostics.RuntimeInfo.get_Platform", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrDiagnosticsNative.RuntimeInfoGetPlatform));
            runtime.DefineNativeBody("surtr.diagnostics.RuntimeInfo.get_EngineVersion", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrDiagnosticsNative.RuntimeInfoGetEngineVersion));
            runtime.DefineNativeBody("surtr.diagnostics.RuntimeInfo.get_RuntimeVersion", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrDiagnosticsNative.RuntimeInfoGetRuntimeVersion));
            runtime.DefineNativeBody("surtr.diagnostics.RuntimeInfo.get_CpuArchitecture", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrDiagnosticsNative.RuntimeInfoGetCpuArchitecture));
            runtime.DefineNativeBody("surtr.diagnostics.RuntimeInfo.get_IsDebugBuild", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrDiagnosticsNative.RuntimeInfoGetIsDebugBuild));
            runtime.DefineNativeBody("surtr.diagnostics.RuntimeInfo.get_ProcessorCount", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrDiagnosticsNative.RuntimeInfoGetProcessorCount));
            runtime.DefineNativeBody("surtr.diagnostics.RuntimeInfo.get_WorkingSet", SurtrNativeEntryPoint.FromFunctionPointer(&SurtrDiagnosticsNative.RuntimeInfoGetWorkingSet));
        }
    }
}
