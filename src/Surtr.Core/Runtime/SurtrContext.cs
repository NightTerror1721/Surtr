#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Runtime.Utilities;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime
{
    /// <summary>
    /// The mutable state one <see cref="SurtrRuntime"/> owns: its object registry, the host
    /// surface published into it, the modules loaded into it, and the roots keeping all of that
    /// alive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Internal and a struct on purpose. It is the runtime's guts, not its API - everything a host
    /// is meant to touch is a method on <see cref="SurtrRuntime"/>, and everything the interpreter
    /// is meant to touch is a field here reached through
    /// <see cref="SurtrRuntime.Context"/>, which returns it by reference so no part of it is ever
    /// copied.
    /// </para>
    /// <para>
    /// What is <em>not</em> here is as deliberate as what is: the built-in classes. They are
    /// process-wide singletons on <see cref="BuiltIns.SurtrBuiltIns"/>, shared by every context, so
    /// two runtimes in the same process agree on what <c>string</c> means and a native entry point
    /// written against one works in the other.
    /// </para>
    /// </remarks>
    internal unsafe struct SurtrContext : IRuntimeResource<int>
    {
        private const int InitialRootCapacity = 16;

        /// <summary>The runtime's object heap: every collectable value, addressed by <see cref="SurtrRef"/>.</summary>
        internal SurtrEntityRegistry EntityRegistry;

        /// <summary>Every loaded module, keyed by its dot-separated path.</summary>
        internal Dictionary<string, SurtrModule> Modules;

        /// <summary>Every host-declared native class, keyed by the full name in its descriptor.</summary>
        internal Dictionary<string, SurtrClass> NativeClasses;

        /// <summary>
        /// Bodies the host has published for native members, keyed by link name.
        /// </summary>
        /// <remarks>
        /// A module read from an image carries the name and the signature of each of its native
        /// members, and the address has to come from whichever runtime is loading it.
        /// </remarks>
        internal Dictionary<string, SurtrNativeEntryPoint> NativeBodies;

        /// <summary>
        /// Type handles for signatures the host declares outside any module - native class
        /// members. Interned here for the same reason a module interns its own: one handle per
        /// distinct descriptor, resolved once.
        /// </summary>
        internal SurtrTypeHandleTable HostTypeHandles;

        /// <summary>
        /// Text-to-object table backing <see cref="SurtrRuntime.InternString"/>, so one piece of
        /// text is one <see cref="SurtrString"/> for the runtime's lifetime.
        /// </summary>
        internal Dictionary<string, SurtrString> InternedStrings;

        /// <summary>
        /// Text-to-object table backing <see cref="SurtrRuntime.GetOrCreateTypeValue(SurtrTypeInfo)"/>,
        /// so <c>typeof</c> and <c>Type.of</c> alike return the one shared <c>Type</c> value for a
        /// given class or interface within this runtime.
        /// </summary>
        /// <remarks>
        /// Keyed by reference identity - every <see cref="SurtrTypeInfo"/> is interned once by its
        /// owner, so no custom comparer is needed. Lives here rather than on the metadata itself
        /// because the metadata is process-wide and shared across runtimes, while the entity
        /// registry a <see cref="SurtrTypeValue"/> is registered in is not.
        /// </remarks>
        internal Dictionary<SurtrTypeInfo, SurtrTypeValue> TypeValueCache;

        /// <summary>
        /// Text-to-object table backing
        /// <see cref="SurtrRuntime.GetOrCreateTypeValue(SurtrTypeInfo, SurtrClassReference)"/> for
        /// <em>constructed</em> generics — <c>typeof(Box&lt;int&gt;)</c> and
        /// <c>Type.get("Obox:Box`1;I")</c> — so one construction is one <c>Type</c> value, distinct
        /// from every other construction of the same class.
        /// </summary>
        /// <remarks>
        /// Keyed by the full descriptor string, which is exactly what distinguishes one
        /// construction from another. Lives here rather than on the metadata because the metadata
        /// is process-wide and shared across runtimes, while the entity registry a
        /// <see cref="SurtrTypeValue"/> is registered in is not.
        /// </remarks>
        internal Dictionary<string, SurtrTypeValue> ConstructedTypeValueCache;

        /// <summary>
        /// Text-to-object table backing <see cref="SurtrRuntime.GetOrCreateModuleValue"/>, so
        /// <c>moduleof</c> and <c>Module.get</c>/<c>Module.tryGet</c> alike return the one shared
        /// <c>Module</c> value for a given <see cref="SurtrModule"/> within this runtime.
        /// </summary>
        /// <remarks>Keyed by reference identity, the same reasoning as <see cref="TypeValueCache"/>.</remarks>
        internal Dictionary<SurtrModule, SurtrModuleValue> ModuleValueCache;

        /// <summary>
        /// Entities kept alive regardless of reachability, as raw reference values ready to hand
        /// to the collector.
        /// </summary>
        /// <remarks>
        /// Stored pre-boxed rather than as entities because that is the shape
        /// <see cref="SurtrEntityRegistry.CollectGarbage"/> wants, and a rooted entity's
        /// <see cref="SurtrRef"/> cannot change while it is rooted - a root is by definition never
        /// released - so there is nothing to keep in sync.
        /// </remarks>
        internal SurtrRawValue[] Roots;

        /// <summary>How many entries of <see cref="Roots"/> are live.</summary>
        internal int RootCount;

        /// <summary>
        /// The static storage of every class and module linked into this runtime, so a collection
        /// can trace it.
        /// </summary>
        /// <remarks>
        /// Registered at link time rather than discovered per collection: walking every loaded
        /// module's class tree to find the storage would turn a constant-time hand-off into a tree
        /// walk on every collection, and the set only ever grows when a module is loaded.
        /// </remarks>
        internal SurtrStaticBlock[] StaticBlocks;

        /// <summary>How many entries of <see cref="StaticBlocks"/> are live.</summary>
        internal int StaticBlockCount;

        /// <summary>
        /// Hands out the dense interface ids the linker assigns, across every module and native
        /// class this context links, so two interfaces from different modules never collide.
        /// </summary>
        /// <remarks>
        /// Starts at <see cref="BuiltIns.SurtrBuiltIns.ReservedInterfaceIds"/>, not at zero: the
        /// built-in interfaces were numbered before any runtime existed, and a class implementing
        /// one of those alongside one of its own keys both into the same dispatch table.
        /// </remarks>
        internal int NextInterfaceId;

        /// <inheritdoc/>
        public RuntimeResourceState ResourceState { get; private set; }

        /// <inheritdoc/>
        /// <param name="initialEntityCapacity">How many objects the registry should be sized for up front.</param>
        /// <exception cref="InvalidOperationException">The context is already initialized.</exception>
        public void Initialize(int initialEntityCapacity)
        {
            if (ResourceState.IsInitialized)
                throw new InvalidOperationException("SurtrContext is already initialized.");

            EntityRegistry = default;
            EntityRegistry.Initialize(initialEntityCapacity);

            Modules = new Dictionary<string, SurtrModule>(StringComparer.Ordinal);
            NativeClasses = new Dictionary<string, SurtrClass>(StringComparer.Ordinal);
            NativeBodies = new Dictionary<string, SurtrNativeEntryPoint>(StringComparer.Ordinal);
            HostTypeHandles = new SurtrTypeHandleTable();
            InternedStrings = new Dictionary<string, SurtrString>(StringComparer.Ordinal);
            TypeValueCache = new Dictionary<SurtrTypeInfo, SurtrTypeValue>();
            ConstructedTypeValueCache = new Dictionary<string, SurtrTypeValue>(StringComparer.Ordinal);
            ModuleValueCache = new Dictionary<SurtrModule, SurtrModuleValue>();

            Roots = new SurtrRawValue[InitialRootCapacity];
            RootCount = 0;
            StaticBlocks = new SurtrStaticBlock[InitialRootCapacity];
            StaticBlockCount = 0;
            NextInterfaceId = BuiltIns.SurtrBuiltIns.ReservedInterfaceIds;

            // Only after every allocation above has succeeded, so a failed init cannot leave a
            // half-alive context behind - the same rule the registry follows.
            ResourceState = RuntimeResourceState.Initialized;
        }

        /// <summary>Registers a block of static storage for the collector to trace.</summary>
        /// <remarks>Blocks holding no reference-typed slot are skipped: tracing one would visit nothing.</remarks>
        internal void AddStaticBlock(SurtrRawValue* values, int* referenceSlots, int referenceSlotCount)
        {
            if (referenceSlotCount == 0)
                return;

            if (StaticBlockCount == StaticBlocks.Length)
                Array.Resize(ref StaticBlocks, StaticBlocks.Length * 2);

            StaticBlocks[StaticBlockCount++] = new SurtrStaticBlock(values, referenceSlots, referenceSlotCount);
        }

        /// <summary>The live prefix of <see cref="StaticBlocks"/>, ready to pass to the collector.</summary>
        internal readonly ReadOnlySpan<SurtrStaticBlock> LiveStaticBlocks
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new ReadOnlySpan<SurtrStaticBlock>(StaticBlocks, 0, StaticBlockCount);
        }

        /// <summary>Adds a raw reference to the permanent root set.</summary>
        internal void AddRoot(SurtrRawValue root)
        {
            EnsureRootCapacity(RootCount + 1);
            Roots[RootCount++] = root;
        }

        /// <summary>
        /// Grows <see cref="Roots"/> to hold at least <paramref name="capacity"/> entries.
        /// </summary>
        /// <remarks>
        /// Also used to borrow slack past <see cref="RootCount"/>: a collection stages the caller's
        /// transient roots there so the collector can be handed one contiguous span without
        /// allocating a merged array on every collection.
        /// </remarks>
        internal void EnsureRootCapacity(int capacity)
        {
            if (capacity <= Roots.Length)
                return;

            int grown = Roots.Length == 0 ? InitialRootCapacity : Roots.Length * 2;
            if (grown < capacity)
                grown = capacity;

            Array.Resize(ref Roots, grown);
        }

        /// <summary>Drops a raw reference from the permanent root set.</summary>
        /// <returns><see langword="false"/> if it was not rooted.</returns>
        internal bool RemoveRoot(SurtrRawValue root)
        {
            var roots = Roots;
            for (int i = 0; i < RootCount; i++)
            {
                if (roots[i] != root)
                    continue;

                // Order is meaningless here, so fill the hole from the end rather than shifting.
                RootCount--;
                roots[i] = roots[RootCount];
                roots[RootCount] = 0;
                return true;
            }

            return false;
        }

        /// <summary>The live prefix of <see cref="Roots"/>, ready to pass to the collector.</summary>
        internal readonly ReadOnlySpan<SurtrRawValue> LiveRoots
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new ReadOnlySpan<SurtrRawValue>(Roots, 0, RootCount);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (ResourceState.IsDisposed)
                return;

            // Modules own unmanaged buffers (chunk pools, per-class static storage and dispatch
            // tables); the registry owns its own. Neither is reachable by a finalizer once this
            // context is gone, so both are released here.
            if (Modules is not null)
            {
                foreach (var module in Modules.Values)
                    module.Dispose();

                Modules.Clear();
            }

            if (NativeClasses is not null)
            {
                foreach (var nativeClass in NativeClasses.Values)
                    nativeClass.Dispose();

                NativeClasses.Clear();
            }

            // Entry points hold at most a delegate the CLR already tracks, so dropping the table is
            // the whole of releasing them.
            NativeBodies?.Clear();

            EntityRegistry.Dispose();

            InternedStrings?.Clear();
            TypeValueCache?.Clear();
            ModuleValueCache?.Clear();
            Roots = Array.Empty<SurtrRawValue>();
            RootCount = 0;

            // The blocks point into storage the modules and classes above have just released.
            StaticBlocks = Array.Empty<SurtrStaticBlock>();
            StaticBlockCount = 0;

            ResourceState = RuntimeResourceState.Disposed;
        }
    }
}
