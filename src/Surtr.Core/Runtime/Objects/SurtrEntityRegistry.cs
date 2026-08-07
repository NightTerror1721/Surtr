#nullable enable

using Surtr.Runtime.Utilities;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Surtr.Runtime.Objects
{
    using Age = byte;

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

        public readonly int Capacity => _capacity;
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

            return newId;
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

        public int CollectGarbage(
            SurtrRawValue* stackStart,
            SurtrRawValue* stackTop,
            SurtrRef* globalFunctionStart,
            int globalFunctionCount,
            SurtrRawValue* globalVariableStart,
            int globalVariableCount,
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

            for (int i = 0; i < globalFunctionCount; i++)
                Mark(globalFunctionStart[i]);

            for (int i = 0; i < globalVariableCount; i++)
                MarkIfReference(globalVariableStart[i]);

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
                _totalFullCollections++;
            else
                _totalNurseryCollections++;

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

            ResourceState = RuntimeResourceState.Disposed;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void ExpandCapacity()
        {
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

        /// <summary>Marks the entity identified by <paramref name="ref"/> as reachable.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Mark(SurtrRef @ref) => _registry[0].Mark(@ref);

        /// <summary>Marks the entity referenced by <paramref name="value"/> as reachable, if <paramref name="value"/> is itself a reference.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Mark(SurtrValue value)
        {
            if ((value.Raw & SurtrValue.TagMask) == SurtrValue.TagMaskReference)
                _registry[0].Mark((SurtrRef)value.Raw);
        }

        /// <summary>Marks <paramref name="entity"/> as reachable, if it isn't <see langword="null"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly void Mark(SurtrRuntimeEntity? entity)
        {
            if (entity is null)
                return;
            _registry[0].Mark(entity.SurtrRef);
        }
    }
}
