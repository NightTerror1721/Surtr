#nullable enable

using Surtr.Runtime.Utilities;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Surtr.Runtime.Objects
{
    using Age = byte;

    /// <summary>
    /// One block of statically allocated slots a collection has to trace: a class's static fields,
    /// or a module's variables.
    /// </summary>
    /// <remarks>
    /// Static storage is unmanaged and reachable from no object, so nothing else in the heap points
    /// at it. Unless a collection walks it explicitly, anything a static field is the only owner of
    /// would be swept out from under a running program. Registered once per linked type, and shaped
    /// like the host global table for the same reason: the slots that can hold a reference are
    /// known from declared types, so the walk marks unconditionally instead of tag-testing.
    /// </remarks>
    internal readonly unsafe struct SurtrStaticBlock
    {
        /// <summary>The block's slots.</summary>
        internal readonly SurtrRawValue* Values;

        /// <summary>Which of those slots hold a reference.</summary>
        internal readonly int* ReferenceSlots;

        /// <summary>How many entries <see cref="ReferenceSlots"/> has.</summary>
        internal readonly int ReferenceSlotCount;

        internal SurtrStaticBlock(SurtrRawValue* values, int* referenceSlots, int referenceSlotCount)
        {
            Values = values;
            ReferenceSlots = referenceSlots;
            ReferenceSlotCount = referenceSlotCount;
        }
    }

    internal unsafe struct SurtrEntityRegistry : IRuntimeResource<int>
    {
        private const int InitialMarkStackCapacity = 16;

        internal SurtrRuntimeEntity?[] Entities;

        private SurtrRef* _freeIds;
        private ulong* _marks;
        private SurtrRef* _marksStack;
        private Age* _ages;

        private int _capacity;
        private int _freeCount;
        private int _nextId;
        private int _markTop;
        private int _markStackCapacity;

        private long _totalCollections;
        private long _totalFullCollections;
        private long _totalNurseryCollections;
        private long _totalCollectedEntities;
        private long _lastCollectionElapsedTicks;

        // Automatic collection. The policy is folded into the fields below at configuration time so
        // the hot path - Register - pays for arming the pending flag with nothing but a compare:
        // in Manual mode the threshold is long.MaxValue and the branch is never taken.
        private SurtrGcPolicy _policy;
        private long _allocationThreshold;
        private int _liveEntityThresholdPercent;
        private int _nurseryFrequency;

        /// <summary>How many entities were registered since the last collection.</summary>
        private long _allocationsSinceLastCollection;

        /// <summary>How many collections have run since the last full sweep.</summary>
        private int _collectionsSinceFull;

        /// <summary>Armed when a threshold has been crossed; drained at the next safepoint.</summary>
        internal bool GcPending;

        public readonly int Capacity => _capacity;

        /// <summary>
        /// How many entities are registered right now.
        /// </summary>
        /// <remarks>
        /// Derived from the id watermark and the free list rather than kept as a counter, so
        /// <see cref="Register"/> pays nothing for it: ids are handed out from the free list first
        /// and from <c>_nextId</c> only when that is empty, which makes everything below the
        /// watermark either live or free and nothing else. Id 0 is the null reference and is never
        /// handed out, hence the -1.
        /// </remarks>
        public readonly int LiveCount => _nextId - 1 - _freeCount;

        public readonly long TotalCollections => _totalCollections;
        public readonly long TotalFullCollections => _totalFullCollections;
        public readonly long TotalNurseryCollections => _totalNurseryCollections;
        public readonly long TotalCollectedEntities => _totalCollectedEntities;
        public readonly double LastCollectionElapsedMilliseconds => _lastCollectionElapsedTicks / (double)TimeSpan.TicksPerMillisecond;

        public RuntimeResourceState ResourceState { get; private set; }

        /// <summary>
        /// Number of 64-bit words needed to hold one mark bit per entity slot. The mark
        /// array is a bitset rather than a byte array: 8x less memory to touch on the
        /// wholesale clear that starts every <see cref="CollectGarbage"/> call, and 8x
        /// fewer cache lines while marking/sweeping large registries.
        /// </summary>
        private readonly int MarkWordCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (_capacity + 63) >> 6;
        }


        public void Initialize(int initialCapacity = 1024)
        {
            if (ResourceState.IsInitialized)
                throw new InvalidOperationException("SurtrEntityRegistry is already initialized.");

            _capacity = Math.Max(initialCapacity, 16);
            Entities = new SurtrRuntimeEntity?[_capacity];

            _freeIds = MemOps.Allocate<SurtrRef>((nuint)_capacity);
            _marks = MemOps.Allocate<ulong>((nuint)MarkWordCount);
            _ages = MemOps.Allocate<Age>((nuint)_capacity);

            _markStackCapacity = InitialMarkStackCapacity;
            _marksStack = MemOps.Allocate<SurtrRef>((nuint)_markStackCapacity);

            _freeCount = 0;
            _markTop = 0;
            _nextId = 1; // Start from 1, as 0 is reserved for SurtrNullRef

            // A registry used directly defaults to Manual; a runtime's context reconfigures it to
            // Automatic before anything runs. Either way the folds below are what the hot path reads.
            _allocationsSinceLastCollection = 0;
            _collectionsSinceFull = 0;
            GcPending = false;
            ConfigurePolicy(SurtrGcPolicy.Manual);

            // Only flip to Initialized once every allocation above has actually
            // succeeded, so a failed init can't leave the registry half-alive.
            ResourceState = RuntimeResourceState.Initialized;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrRef Register(SurtrRuntimeEntity? entity)
        {
            if (entity is null)
                return SurtrValue.NullRef;

            if (entity.SurtrRef != SurtrValue.NullRef)
                return entity.SurtrRef;

            SurtrRef newId;
            if (_freeCount > 0)
            {
                _freeCount--;
                newId = _freeIds[_freeCount];
            }
            else
            {
                newId = _nextId++;
                if (newId >= _capacity)
                    ExpandCapacity();
            }

            Entities[newId] = entity;
            entity.SurtrRef = newId;

            _ages[newId] = 0;

            // The one thing Register pays toward automatic collection: count the allocation and,
            // when the count has crossed the folded threshold, arm the pending flag. Manual mode
            // folds the threshold to long.MaxValue, so the branch below is never taken and predicts
            // perfectly. The sweep itself never runs here - an allocation is often mid-construction
            // (an array's elements are filled after it is registered), so collecting inline could
            // reclaim the object being built; the interpreter drains the flag at its next safepoint.
            if (++_allocationsSinceLastCollection >= _allocationThreshold)
                GcPending = true;

            return newId;
        }

        /// <summary>Replaces the policy the collector runs under, folding its thresholds for the hot path.</summary>
        /// <remarks>
        /// Folding keeps <see cref="Register"/> to a single always-false compare in
        /// <see cref="SurtrGcMode.Manual"/>: the allocation threshold becomes
        /// <see cref="long.MaxValue"/> and the live-entity threshold becomes <c>0</c>. Manual mode
        /// never arms the pending flag this way; capacity growth still does, but with the live
        /// threshold folded to zero that arm is gated off too.
        /// </remarks>
        internal void ConfigurePolicy(in SurtrGcPolicy policy)
        {
            _policy = policy;
            _allocationThreshold = policy.Mode == SurtrGcMode.Manual ? long.MaxValue : policy.AllocationThreshold;
            _liveEntityThresholdPercent = policy.Mode == SurtrGcMode.Manual ? 0 : policy.LiveEntityThresholdPercent;
            _nurseryFrequency = Math.Max(1, policy.NurseryFrequency);
        }

        /// <summary>The policy this registry currently collects under.</summary>
        internal SurtrGcPolicy Policy => _policy;

        /// <summary>How many entities were registered since the last collection.</summary>
        internal long AllocationsSinceLastCollection => _allocationsSinceLastCollection;

        /// <summary>
        /// Whether the collection due at the next safepoint should be a full sweep, per
        /// <see cref="SurtrGcPolicy.NurseryFrequency"/> and the live-entity pressure.
        /// </summary>
        /// <remarks>
        /// Called only at a safepoint (cold), so computing the live-entity pressure here costs
        /// nothing on any hot path.
        /// </remarks>
        internal bool ShouldCollectFull()
        {
            if (_liveEntityThresholdPercent != 0
                && (_nextId - 1 - _freeCount) * 100 >= _capacity * _liveEntityThresholdPercent)
                return true;

            return _collectionsSinceFull >= _nurseryFrequency - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly SurtrRuntimeEntity? Get(SurtrRef id)
        {
            if (id <= 0 || id >= _nextId)
                return null;
            return Entities[id];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T? GetAs<T>(SurtrRef id) where T : SurtrRuntimeEntity
        {
            if (id <= 0 || id >= _nextId)
                return null;
            return (T?)Entities[id];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly T GetUnsafe<T>(SurtrRef id) where T : SurtrRuntimeEntity
        {
            return (T)Entities[id]!;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Release(SurtrRef @ref)
        {
            if (@ref <= 0 || @ref >= _nextId)
                return;

            var entity = Entities[@ref];
            if (entity is null)
                return;

            Entities[@ref] = null;
            entity.SurtrRef = SurtrValue.NullRef;
            _ages[@ref] = 0;
            _freeIds[_freeCount] = @ref;
            _freeCount++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Mark(SurtrRef @ref)
        {
            if (@ref <= 0 || @ref >= _nextId)
                return;

            int word = @ref >> 6;
            ulong bit = 1UL << (@ref & 63);

            if ((_marks[word] & bit) != 0 || Entities[@ref] is null)
                return;

            _marks[word] |= bit;

            if (_markTop >= _markStackCapacity)
                ExpandMarkStack();
            _marksStack[_markTop++] = @ref;
        }

        /// <summary>
        /// Marks every entity reachable from the given roots and releases the rest.
        /// </summary>
        /// <param name="stackStart">The first slot of the interpreter's evaluation stack.</param>
        /// <param name="stackTop">One past the last live slot of the evaluation stack.</param>
        /// <param name="staticBlocks">
        /// The static storage of every linked class and module - see <see cref="SurtrStaticBlock"/>.
        /// Nothing in the heap points at these, so they have to be walked explicitly.
        /// </param>
        /// <param name="explicitRoots">
        /// Anything else the caller is keeping alive that is not in one of the tables above -
        /// values held by in-flight native calls, host-side pins, and so on.
        /// </param>
        /// <param name="fullCollection">
        /// <see langword="true"/> to sweep every unreachable entity; <see langword="false"/> to
        /// spare any that has survived a previous collection.
        /// </param>
        /// <returns>How many entities were released.</returns>
        /// <remarks>
        /// <para>
        /// Every reference-typed slot a <c>SurtrStaticBlock</c> lists is marked unconditionally,
        /// mirroring how a class's instance references are traced. A slot list is built from the
        /// statics' declared types at link time, so this loop marks without tag-testing the way the
        /// stack loop has to.
        /// </para>
        /// </remarks>
        public int CollectGarbage(
            SurtrRawValue* stackStart,
            SurtrRawValue* stackTop,
            ReadOnlySpan<SurtrStaticBlock> staticBlocks,
            ReadOnlySpan<SurtrRawValue> explicitRoots,
            bool fullCollection)
        {
            long started = Stopwatch.GetTimestamp();

            MemOps.Clear(_marks, (nuint)(MarkWordCount * sizeof(ulong)));
            _markTop = 0;

            SurtrRawValue* currentStackValue = stackStart;
            while (currentStackValue < stackTop)
            {
                MarkIfReference(*currentStackValue);
                currentStackValue++;
            }

            for (int i = 0; i < staticBlocks.Length; i++)
            {
                var block = staticBlocks[i];
                for (int s = 0; s < block.ReferenceSlotCount; s++)
                    Mark((SurtrRef)block.Values[block.ReferenceSlots[s]]);
            }

            fixed (SurtrRawValue* rootStartRef = explicitRoots)
            {
                int len = explicitRoots.Length;
                for (int i = 0; i < len; i++)
                    MarkIfReference(rootStartRef[i]);
            }

            var entities = Entities;
            var marker = new SurtrEntityMarker(ref this);
            while (_markTop > 0)
            {
                SurtrRef @ref = _marksStack[--_markTop];
                entities[@ref]?.VisitReferences(marker);
            }

            int released = 0;
            for (SurtrRef @ref = 1; @ref < _nextId; @ref++)
            {
                var entity = entities[@ref];
                if (entity is null)
                    continue;

                int word = @ref >> 6;
                ulong bit = 1UL << (@ref & 63);

                if ((_marks[word] & bit) != 0)
                {
                    if (_ages[@ref] < Age.MaxValue)
                        _ages[@ref]++;
                    continue;
                }

                if (!fullCollection && _ages[@ref] > 0)
                    continue;

                entity.SurtrRef = SurtrValue.NullRef;
                entities[@ref] = null;
                _ages[@ref] = 0;
                _freeIds[_freeCount] = @ref;
                _freeCount++;
                released++;
            }

            _lastCollectionElapsedTicks = Stopwatch.GetTimestamp() - started;
            _totalCollections++;
            _totalCollectedEntities += released;

            if (fullCollection)
            {
                _totalFullCollections++;
                _collectionsSinceFull = 0;
            }
            else
            {
                _totalNurseryCollections++;
                _collectionsSinceFull++;
            }

            // Whatever armed the pending flag has been drained: the sweep has just run, so the
            // allocation counter restarts and the flag must not re-trigger at the next safepoint
            // without fresh pressure.
            _allocationsSinceLastCollection = 0;
            GcPending = false;

            return released;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void MarkIfReference(SurtrRawValue value)
        {
            if ((value & SurtrValue.TagMask) == SurtrValue.TagMaskReference)
                Mark((SurtrRef)value);
        }

        public void Dispose()
        {
            if (ResourceState.IsDisposed)
                return;

            if (_freeIds != null)
            {
                Marshal.FreeHGlobal((IntPtr)_freeIds);
                _freeIds = null;
            }

            if (_marks != null)
            {
                Marshal.FreeHGlobal((IntPtr)_marks);
                _marks = null;
            }

            if (_marksStack != null)
            {
                Marshal.FreeHGlobal((IntPtr)_marksStack);
                _marksStack = null;
            }

            if (_ages != null)
            {
                Marshal.FreeHGlobal((IntPtr)_ages);
                _ages = null;
            }

            Entities = [];
            _capacity = 0;
            _freeCount = 0;
            _nextId = 0;
            _markTop = 0;
            _markStackCapacity = 0;
            _allocationsSinceLastCollection = 0;
            _collectionsSinceFull = 0;
            GcPending = false;
            _policy = default;

            ResourceState = RuntimeResourceState.Disposed;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ExpandCapacity()
        {
            // Capacity is about to double, which is the collector's best pressure signal: the live
            // set has outgrown the registry, so a collection is due. Gated on the live-entity
            // threshold, which ConfigurePolicy folds to zero in Manual mode - so a manual runtime
            // never arms the flag this way. Arming here is cold and cannot re-enter: the sweep
            // still runs at a safepoint.
            if (_liveEntityThresholdPercent != 0)
                GcPending = true;

            int newCapacity = _capacity * 2;

            Array.Resize(ref Entities, newCapacity);

            _freeIds = MemOps.Reallocate(_freeIds, (nuint)newCapacity);
            _ages = MemOps.Reallocate(_ages, (nuint)newCapacity);

            // _capacity must already reflect newCapacity before MarkWordCount is read,
            // so the bitset is resized to fit the *new* entity count.
            _capacity = newCapacity;
            _marks = MemOps.Reallocate(_marks, (nuint)MarkWordCount);

            // None of the newly grown regions need zero-initialization: _freeIds slots
            // are only ever read after Release/CollectGarbage writes them (a stack
            // invariant guarded by _freeCount), _marks is wholesale-cleared at the top
            // of every CollectGarbage call, and _ages is always set to 0 by Register
            // before a slot is ever inspected.
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ExpandMarkStack()
        {
            int newCapacity = _markStackCapacity * 2;
            _marksStack = MemOps.Reallocate(_marksStack, (nuint)newCapacity);
            _markStackCapacity = newCapacity;
        }
    }

    /// <summary>
    /// A restricted, indirect handle to a <see cref="SurtrEntityRegistry"/> that exposes only
    /// marking, for use from <see cref="SurtrRuntimeEntity.VisitReferences"/> during a
    /// collection.
    /// </summary>
    /// <remarks>
    /// <see cref="SurtrEntityRegistry"/> can't be handed out via a ref field: ref fields need
    /// runtime support unavailable on netstandard2.1, and a pointer is off the table too since
    /// its entity array is managed, so the registry isn't an unmanaged type. A length-1
    /// <see cref="Span{T}"/> sidesteps both: it's backed by the runtime's own byref-capable
    /// Span&lt;T&gt; (not a ref field declared here), and Span&lt;T&gt; doesn't require T to be
    /// unmanaged, so it works with the managed array in tow. No heap allocation, no copy of
    /// the registry - marking writes straight through to the caller's storage.
    /// </remarks>
    public readonly ref struct SurtrEntityMarker
    {
        private readonly Span<SurtrEntityRegistry> _registry;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SurtrEntityMarker(ref SurtrEntityRegistry registry)
        {
            _registry = MemoryMarshal.CreateSpan(ref registry, 1);
        }

        /// <summary>
        /// The registry behind this marker, reached without the span's bounds check.
        /// </summary>
        /// <remarks>
        /// The span is length 1 by construction, so index 0 is always valid, but the JIT can't
        /// prove that at a call site and would emit a compare-and-branch on every mark. Marking
        /// runs once per reachable reference in a collection, so that check is worth removing.
        /// </remarks>
        private readonly ref SurtrEntityRegistry Registry
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref MemoryMarshal.GetReference(_registry);
        }

        /// <summary>Marks the entity identified by <paramref name="ref"/> as reachable.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Mark(SurtrRef @ref) => Registry.Mark(@ref);

        /// <summary>Marks the entity referenced by <paramref name="value"/> as reachable, if <paramref name="value"/> is itself a reference.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Mark(SurtrValue value)
        {
            if ((value.Raw & SurtrValue.TagMask) == SurtrValue.TagMaskReference)
                Registry.Mark((SurtrRef)value.Raw);
        }

        /// <summary>Marks <paramref name="entity"/> as reachable, if it isn't <see langword="null"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Mark(SurtrRuntimeEntity? entity)
        {
            if (entity is null)
                return;
            Registry.Mark(entity.SurtrRef);
        }
    }
}
