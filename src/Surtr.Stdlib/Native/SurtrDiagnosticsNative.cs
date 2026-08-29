#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Diagnostics;
using System.Text;

namespace Surtr.Stdlib.Native
{
    /// <summary>
    /// The C# bodies behind the diagnostics native declarations:
    /// <c>surtr.diagnostics.Profiler.stopwatchTimestamp</c>,
    /// <c>surtr.diagnostics.Debug.debugPrint/debugDump/debugBreakpoint/debugStack/debugIsDebuggerAttached</c>,
    /// and <c>surtr.diagnostics.RuntimeInfo</c>'s native property getters.
    /// </summary>
    internal static unsafe class SurtrDiagnosticsNative
    {
        // ── Profiler ──────────────────────────────────────────────────────

        /// <summary>
        /// Returns the current high-resolution timestamp in milliseconds.
        /// Link name: <c>surtr.diagnostics.Profiler.stopwatchTimestamp</c>.
        /// </summary>
        internal static int StopwatchTimestamp(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateFloat(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency));

        // ── Debug ─────────────────────────────────────────────────────────

        /// <summary>
        /// Writes a message to the debug output (System.Diagnostics.Debug / Debug.WriteLine).
        /// Link name: <c>surtr.diagnostics.Debug.debugPrint</c>.
        /// </summary>
        internal static int DebugPrint(SurtrCallArguments arguments)
        {
            string message = arguments.GetString(0).ToString();
            System.Diagnostics.Debug.WriteLine(message);
            return 0;
        }

        /// <summary>
        /// Returns a string representation of the given value for diagnostic dumping.
        /// Link name: <c>surtr.diagnostics.Debug.debugDump</c>.
        /// </summary>
        internal static int DebugDump(SurtrCallArguments arguments)
        {
            var value = arguments.GetValue(0);
            string text = value.ToString() ?? "(null)";
            return arguments.Return(SurtrValue.CreateReference(arguments.Runtime.NewString(text).GetSurtrReference()));
        }

        /// <summary>
        /// Triggers a debugger breakpoint (Debugger.Break).
        /// Link name: <c>surtr.diagnostics.Debug.debugBreakpoint</c>.
        /// </summary>
        internal static int DebugBreakpoint(SurtrCallArguments arguments)
        {
            Debugger.Break();
            return 0;
        }

        /// <summary>
        /// Returns the current stack trace as a string.
        /// Link name: <c>surtr.diagnostics.Debug.debugStack</c>.
        /// </summary>
        internal static int DebugStack(SurtrCallArguments arguments)
        {
            var trace = new StackTrace(1, true);
            return arguments.Return(SurtrValue.CreateReference(arguments.Runtime.NewString(trace.ToString()).GetSurtrReference()));
        }

        /// <summary>
        /// Returns whether a debugger is currently attached.
        /// Link name: <c>surtr.diagnostics.Debug.debugIsDebuggerAttached</c>.
        /// </summary>
        internal static int DebugIsDebuggerAttached(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateBool(Debugger.IsAttached));

        // ── RuntimeInfo property getters ───────────────────────────────────

        /// <summary>
        /// Returns the operating system platform name.
        /// Link name: <c>surtr.diagnostics.RuntimeInfo.get_Platform</c>.
        /// </summary>
        internal static int RuntimeInfoGetPlatform(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateReference(arguments.Runtime.NewString(Environment.OSVersion.Platform.ToString()).GetSurtrReference()));

        /// <summary>
        /// Returns the Surtr engine/compiler version string.
        /// Link name: <c>surtr.diagnostics.RuntimeInfo.get_EngineVersion</c>.
        /// </summary>
        internal static int RuntimeInfoGetEngineVersion(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateReference(arguments.Runtime.NewString("0.1.0").GetSurtrReference()));

        /// <summary>
        /// Returns the .NET / CLR runtime version string.
        /// Link name: <c>surtr.diagnostics.RuntimeInfo.get_RuntimeVersion</c>.
        /// </summary>
        internal static int RuntimeInfoGetRuntimeVersion(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateReference(arguments.Runtime.NewString(Environment.Version.ToString()).GetSurtrReference()));

        /// <summary>
        /// Returns the CPU architecture string.
        /// Link name: <c>surtr.diagnostics.RuntimeInfo.get_CpuArchitecture</c>.
        /// </summary>
        internal static int RuntimeInfoGetCpuArchitecture(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateReference(arguments.Runtime.NewString(
                Environment.Is64BitProcess ? "x64" : "x86").GetSurtrReference()));

        /// <summary>
        /// Returns whether this is a debug build.
        /// Link name: <c>surtr.diagnostics.RuntimeInfo.get_IsDebugBuild</c>.
        /// </summary>
        internal static int RuntimeInfoGetIsDebugBuild(SurtrCallArguments arguments)
        {
#if DEBUG
            return arguments.Return(SurtrValue.CreateBool(true));
#else
            return arguments.Return(SurtrValue.CreateBool(false));
#endif
        }

        /// <summary>
        /// Returns the number of processors available.
        /// Link name: <c>surtr.diagnostics.RuntimeInfo.get_ProcessorCount</c>.
        /// </summary>
        internal static int RuntimeInfoGetProcessorCount(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(Environment.ProcessorCount));

        /// <summary>
        /// Returns the working set size in bytes.
        /// Link name: <c>surtr.diagnostics.RuntimeInfo.get_WorkingSet</c>.
        /// </summary>
        internal static int RuntimeInfoGetWorkingSet(SurtrCallArguments arguments)
            => arguments.Return(SurtrValue.CreateInt(Environment.WorkingSet));
    }
}
