#nullable enable

using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Surtr.Bench
{
    /// <summary>
    /// What machine a run happened on, captured so two runs can be told apart. A benchmark's numbers
    /// are meaningless without the hardware, OS, runtime and GC configuration that produced them: a
    /// median by itself says "this fast on this one machine", and which machine is exactly what the
    /// header and the CSV meta are for.
    /// </summary>
    internal static class SystemInfo
    {
        /// <summary>
        /// One line describing the environment, suitable for the run header and for a
        /// <c>#</c>-prefixed meta line in the CSV. Every field is best-effort and falls back rather
        /// than failing the run — the fingerprint is there to help, never to gate.
        /// </summary>
        public static string FingerprintLine()
        {
            string gc = (System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")
                + "/" + System.Runtime.GCSettings.LatencyMode.ToString().ToLowerInvariant();

            return string.Join(" | ",
                CpuDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.FrameworkDescription,
                "cores " + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
                "gc " + gc,
                "utc " + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }

        private static string CpuDescription
        {
            get
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                        return (string?)key?.GetValue("ProcessorNameString") ?? FallbackCpuName();
                    }

                    if (OperatingSystem.IsLinux())
                    {
                        foreach (string line in File.ReadLines("/proc/cpuinfo"))
                        {
                            if (line.StartsWith("model name", StringComparison.Ordinal))
                            {
                                int colon = line.IndexOf(':');
                                return colon >= 0 ? line.Substring(colon + 1).Trim() : FallbackCpuName();
                            }
                        }
                    }

                    return FallbackCpuName();
                }
                catch
                {
                    return FallbackCpuName();
                }
            }
        }

        private static string FallbackCpuName()
            => RuntimeInformation.ProcessArchitecture.ToString();
    }
}