#nullable enable

using Surtr.Interop.Attributes;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Interop
{
    /// <summary>
    /// The bridge's host-facing entry point: register scanned native types into a runtime, globally
    /// or per runtime, using either the source generator's emitted descriptors or the reflection
    /// fallback.
    /// </summary>
    public static class SurtrBridge
    {
        /// <summary>
        /// The naming policy applied when no narrower scope specifies one. Override per runtime,
        /// module, class or member to narrow it.
        /// </summary>
        public static SurtrNamingPolicy DefaultNamingPolicy { get; set; } = SurtrNamingPolicy.Surtr;

        /// <summary>Materializes one descriptor into <paramref name="runtime"/>.</summary>
        public static SurtrClass Register(SurtrRuntime runtime, NativeTypeDescriptor descriptor)
            => SurtrTypeMaterializer.Register(runtime, descriptor);

        /// <summary>
        /// Materializes every descriptor into <paramref name="runtime"/>, enums and base types first
        /// so type handles resolve in dependency order.
        /// </summary>
        public static IReadOnlyList<SurtrClass> RegisterAll(SurtrRuntime runtime, IEnumerable<NativeTypeDescriptor> descriptors)
        {
            if (runtime is null)
                throw new ArgumentNullException(nameof(runtime));

            if (descriptors is null)
                throw new ArgumentNullException(nameof(descriptors));

            var list = descriptors.ToList();
            var result = new List<SurtrClass>(list.Count);
            var registered = new HashSet<string>(StringComparer.Ordinal);

            foreach (var descriptor in list.Where(static d => d.Kind == NativeTypeKind.Enum))
            {
                result.Add(Register(runtime, descriptor));
                registered.Add(descriptor.FullName);
            }

            var remaining = list.Where(static d => d.Kind != NativeTypeKind.Enum).ToList();
            while (remaining.Count > 0)
            {
                bool progressed = false;

                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var descriptor = remaining[i];
                    if (descriptor.BaseType is null || registered.Contains(descriptor.BaseType))
                    {
                        result.Add(Register(runtime, descriptor));
                        registered.Add(descriptor.FullName);
                        remaining.RemoveAt(i);
                        progressed = true;
                    }
                }

                if (!progressed)
                    throw new InvalidOperationException("Native type base dependency is cyclic or unresolved.");
            }

            return result;
        }

        /// <summary>
        /// The reflection fallback: scans the given CLR types (marked with
        /// <see cref="SurtrNativeTypeAttribute"/>) and registers them, honoring
        /// <see cref="DefaultNamingPolicy"/> as the outermost scope.
        /// </summary>
        public static IReadOnlyList<SurtrClass> ScanAndRegister(SurtrRuntime runtime, params Type[] types)
        {
            if (types is null)
                throw new ArgumentNullException(nameof(types));

            var descriptors = new NativeTypeDescriptor[types.Length];
            for (int i = 0; i < types.Length; i++)
                descriptors[i] = SurtrReflectionScanner.Scan(types[i], DefaultNamingPolicy);

            return RegisterAll(runtime, descriptors);
        }
    }
}
