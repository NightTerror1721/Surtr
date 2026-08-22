#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Surtr.Interop
{
    /// <summary>
    /// Host state the bridge attaches to a runtime without keeping it alive: enum value caches. A
    /// <see cref="SurtrRef"/> belongs to one runtime's heap, so the cached case objects - and the
    /// value-to-reference maps built over them - must live per runtime rather than per process.
    /// </summary>
    internal static class SurtrInteropState
    {
        private static readonly ConditionalWeakTable<SurtrRuntime, RuntimeInteropState> States =
            new ConditionalWeakTable<SurtrRuntime, RuntimeInteropState>();

        /// <summary>Returns the per-runtime bridge state, creating it on first use.</summary>
        public static RuntimeInteropState For(SurtrRuntime runtime)
            => States.GetValue(runtime, static r => new RuntimeInteropState(r));
    }

    /// <summary>The bridge state attached to one runtime.</summary>
    internal sealed class RuntimeInteropState
    {
        private readonly SurtrRuntime _runtime;
        private readonly Dictionary<Type, SurtrEnumCache> _enumCaches = new Dictionary<Type, SurtrEnumCache>();

        public RuntimeInteropState(SurtrRuntime runtime) => _runtime = runtime;

        public SurtrRuntime Runtime => _runtime;

        /// <summary>Registers the enum cache for a CLR enum type.</summary>
        public void AddEnumCache(Type enumType, SurtrEnumCache cache) => _enumCaches[enumType] = cache;

        /// <summary>The enum cache for a CLR enum type, or null if none was registered.</summary>
        public bool TryGetEnumCache(Type enumType, out SurtrEnumCache cache)
            => _enumCaches.TryGetValue(enumType, out cache!);
    }

    /// <summary>
    /// The cached, per-runtime identity of a CLR enum's values. The CLR does not cache boxed enums
    /// (reference equality across boxing is unstable), so the bridge caches one
    /// <see cref="SurtrNativeObject"/> per value - created once at registration and rooted - and maps
    /// the underlying value to its reference, so marshaling is O(1) with no boxing on the hot path.
    /// </summary>
    public sealed class SurtrEnumCache
    {
        private readonly SurtrRuntime _runtime;
        private readonly Type _enumType;
        private readonly Dictionary<long, SurtrRef> _byValue;

        internal SurtrEnumCache(SurtrRuntime runtime, Type enumType, IEnumerable<KeyValuePair<object, SurtrRef>> entries)
        {
            _runtime = runtime;
            _enumType = enumType;
            _byValue = new Dictionary<long, SurtrRef>();

            foreach (var entry in entries)
                _byValue[Convert.ToInt64(entry.Key, System.Globalization.CultureInfo.InvariantCulture)] = entry.Value;
        }

        /// <summary>The Surtr reference naming the given enum value's cached object.</summary>
        public SurtrRef GetReference(object boxedValue)
        {
            long key = Convert.ToInt64(boxedValue, System.Globalization.CultureInfo.InvariantCulture);
            if (_byValue.TryGetValue(key, out var reference))
                return reference;

            throw new InvalidOperationException(
                $"Enum value '{boxedValue}' of '{_enumType.Name}' is not registered; the enum was not materialized into this runtime.");
        }

        /// <summary>The boxed CLR enum value a Surtr reference names.</summary>
        public object FromReference(SurtrValue value)
        {
            var entity = _runtime.Resolve<SurtrNativeObject>(value);
            if (entity is null || entity.Target is null)
                throw new InvalidOperationException($"The value is not a live '{_enumType.Name}' enum reference.");

            return entity.Target;
        }
    }
}
