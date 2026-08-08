#nullable enable

using Surtr.Runtime.Objects;
using Surtr.Runtime.Utilities;
using System;
using System.Collections.Generic;

using SurtrRawValue = System.UInt64;
using SurtrRef = System.Int32;

namespace Surtr.Tests.Runtime.Objects
{
    /// <summary>
    /// A registry entity whose reachable set is whatever a test wires up through
    /// <see cref="References"/>, so a collection's transitive walk can be pinned down exactly
    /// without needing a real object graph (strings, arrays, ...).
    /// </summary>
    internal sealed class FakeEntity : SurtrRuntimeEntity
    {
        public List<FakeEntity> References { get; } = new();

        internal override void VisitReferences(SurtrEntityMarker marker)
        {
            foreach (var reference in References)
                marker.Mark(reference);
        }
    }

    internal sealed class OtherFakeEntity : SurtrRuntimeEntity
    {
        internal override void VisitReferences(SurtrEntityMarker marker) { }
    }

    public unsafe class SurtrEntityRegistryTests
    {
        // Not disposed via `using`: SurtrEntityRegistry is a mutable struct wrapping unmanaged
        // buffers, and copying it (as a `using var` binding plus a second local would do) would
        // desynchronize its int counters from the shared buffers. Every test therefore owns
        // exactly one local variable and disposes it itself.
        private static SurtrEntityRegistry CreateRegistry(int capacity = 16)
        {
            var registry = new SurtrEntityRegistry();
            registry.Initialize(capacity);
            return registry;
        }

        /// <summary>Runs a collection with no stack, host globals or static blocks - just the given explicit roots.</summary>
        private static int Collect(ref SurtrEntityRegistry registry, ReadOnlySpan<SurtrRawValue> explicitRoots, bool fullCollection)
            => registry.CollectGarbage(null, null, null, null, 0, ReadOnlySpan<SurtrStaticBlock>.Empty, explicitRoots, fullCollection);

        private static SurtrRawValue RootOf(SurtrRuntimeEntity entity)
            => SurtrValue.CreateReference(entity.GetSurtrReference()).Raw;

        #region Initialize

        [Fact]
        public void Initialize_TwiceOnTheSameRegistry_Throws()
        {
            var registry = CreateRegistry();
            try
            {
                Assert.Throws<InvalidOperationException>(() => registry.Initialize());
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void Initialize_ClampsCapacityToAMinimumOfSixteen()
        {
            var registry = CreateRegistry(capacity: 1);
            try
            {
                Assert.Equal(16, registry.Capacity);
            }
            finally
            {
                registry.Dispose();
            }
        }

        #endregion

        #region Register / Get / Release

        [Fact]
        public void Register_ANullEntity_ReturnsNullRef()
        {
            var registry = CreateRegistry();
            try
            {
                Assert.Equal(SurtrValue.NullRef, registry.Register(null));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void Register_AssignsAPositiveRef_AndGetReturnsTheSameEntity()
        {
            var registry = CreateRegistry();
            try
            {
                var entity = new FakeEntity();

                SurtrRef reference = registry.Register(entity);

                Assert.True(reference > 0);
                Assert.Equal(reference, entity.GetSurtrReference());
                Assert.Same(entity, registry.Get(reference));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void Register_TheSameEntityTwice_ReturnsTheSameRefWithoutReRegistering()
        {
            var registry = CreateRegistry();
            try
            {
                var entity = new FakeEntity();

                SurtrRef first = registry.Register(entity);
                SurtrRef second = registry.Register(entity);

                Assert.Equal(first, second);
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(9999)]
        public void Get_OutOfRangeId_ReturnsNull(int id)
        {
            var registry = CreateRegistry();
            try
            {
                registry.Register(new FakeEntity());

                Assert.Null(registry.Get(id));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void GetAs_MatchingType_ReturnsTheEntity()
        {
            var registry = CreateRegistry();
            try
            {
                SurtrRef reference = registry.Register(new FakeEntity());

                Assert.NotNull(registry.GetAs<FakeEntity>(reference));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void GetAs_WrongType_ThrowsRatherThanReturningNull()
        {
            // GetAs<T> casts directly ((T?)Entities[id]) rather than using an `as`-style safe
            // cast, so a type mismatch is a loud InvalidCastException, not a silent null - it is
            // the "checked" tier, not a TryGet.
            var registry = CreateRegistry();
            try
            {
                SurtrRef reference = registry.Register(new FakeEntity());

                Assert.Throws<InvalidCastException>(() => registry.GetAs<OtherFakeEntity>(reference));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void Release_ClearsTheEntitysBackReference_AndFreesTheSlot()
        {
            var registry = CreateRegistry();
            try
            {
                var entity = new FakeEntity();
                SurtrRef reference = registry.Register(entity);

                registry.Release(reference);

                Assert.Equal(SurtrValue.NullRef, entity.GetSurtrReference());
                Assert.Null(registry.Get(reference));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void Release_AnAlreadyFreeOrOutOfRangeId_IsANoOp()
        {
            var registry = CreateRegistry();
            try
            {
                registry.Release(0);
                registry.Release(-5);
                registry.Release(500);
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void Release_ThenRegister_ReusesFreedIds_InLastFreedFirstOrder()
        {
            var registry = CreateRegistry();
            try
            {
                SurtrRef a = registry.Register(new FakeEntity());
                SurtrRef b = registry.Register(new FakeEntity());
                SurtrRef c = registry.Register(new FakeEntity());

                registry.Release(b);
                registry.Release(a);

                // The free list is a stack: the most recently released id comes back first.
                var first = new FakeEntity();
                var second = new FakeEntity();
                SurtrRef firstReused = registry.Register(first);
                SurtrRef secondReused = registry.Register(second);

                Assert.Equal(a, firstReused);
                Assert.Equal(b, secondReused);
                Assert.Same(first, registry.Get(a));
                Assert.Same(second, registry.Get(b));
                Assert.NotEqual(c, firstReused);
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void Capacity_ExpandsPastTheInitialSize_PreservingAlreadyRegisteredEntities()
        {
            var registry = CreateRegistry(capacity: 16);
            try
            {
                var first = new FakeEntity();
                SurtrRef firstRef = registry.Register(first);

                for (int i = 0; i < 32; i++)
                    registry.Register(new FakeEntity());

                Assert.True(registry.Capacity > 16);
                Assert.Same(first, registry.Get(firstRef));
            }
            finally
            {
                registry.Dispose();
            }
        }

        #endregion

        #region CollectGarbage - explicit roots and sweeping

        [Fact]
        public void CollectGarbage_ReleasesAnUnreachableEntity()
        {
            var registry = CreateRegistry();
            try
            {
                var entity = new FakeEntity();
                registry.Register(entity);

                int released = Collect(ref registry, ReadOnlySpan<SurtrRawValue>.Empty, fullCollection: true);

                Assert.Equal(1, released);
                Assert.Null(registry.Get(entity.GetSurtrReference()));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void CollectGarbage_SparesAnEntityNamedByAnExplicitRoot()
        {
            var registry = CreateRegistry();
            try
            {
                var entity = new FakeEntity();
                SurtrRef reference = registry.Register(entity);

                SurtrRawValue[] roots = { RootOf(entity) };
                int released = Collect(ref registry, roots, fullCollection: true);

                Assert.Equal(0, released);
                Assert.Same(entity, registry.Get(reference));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void CollectGarbage_TracesTransitivelyThroughVisitReferences()
        {
            var registry = CreateRegistry();
            try
            {
                var a = new FakeEntity();
                var b = new FakeEntity();
                var c = new FakeEntity();
                registry.Register(a);
                registry.Register(b);
                registry.Register(c);
                a.References.Add(b);
                b.References.Add(c);

                int released = Collect(ref registry, new[] { RootOf(a) }, fullCollection: true);

                Assert.Equal(0, released);
                Assert.NotNull(registry.Get(a.GetSurtrReference()));
                Assert.NotNull(registry.Get(b.GetSurtrReference()));
                Assert.NotNull(registry.Get(c.GetSurtrReference()));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void CollectGarbage_ReleasesEverythingBeyondWhatIsReachable()
        {
            var registry = CreateRegistry();
            try
            {
                var reachable = new FakeEntity();
                var unreachable = new FakeEntity();
                registry.Register(reachable);
                registry.Register(unreachable);

                int released = Collect(ref registry, new[] { RootOf(reachable) }, fullCollection: true);

                Assert.Equal(1, released);
                Assert.NotNull(registry.Get(reachable.GetSurtrReference()));
                Assert.Null(registry.Get(unreachable.GetSurtrReference()));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void CollectGarbage_CollectsAnUnreachableCycle()
        {
            var registry = CreateRegistry();
            try
            {
                var a = new FakeEntity();
                var b = new FakeEntity();
                registry.Register(a);
                registry.Register(b);
                a.References.Add(b);
                b.References.Add(a);

                int released = Collect(ref registry, ReadOnlySpan<SurtrRawValue>.Empty, fullCollection: true);

                Assert.Equal(2, released);
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void CollectGarbage_SparesAReachableCycle()
        {
            var registry = CreateRegistry();
            try
            {
                var a = new FakeEntity();
                var b = new FakeEntity();
                registry.Register(a);
                registry.Register(b);
                a.References.Add(b);
                b.References.Add(a);

                int released = Collect(ref registry, new[] { RootOf(a) }, fullCollection: true);

                Assert.Equal(0, released);
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void CollectGarbage_ANonReferenceExplicitRoot_IsIgnored()
        {
            var registry = CreateRegistry();
            try
            {
                registry.Register(new FakeEntity());

                // A raw int/float/bool/char value passed as a "root" carries no entity to mark
                // and must not be misread as one.
                SurtrRawValue[] roots = { SurtrValue.CreateInt(42).Raw };
                int released = Collect(ref registry, roots, fullCollection: true);

                Assert.Equal(1, released);
            }
            finally
            {
                registry.Dispose();
            }
        }

        #endregion

        #region CollectGarbage - stack, host globals and static blocks

        [Fact]
        public void CollectGarbage_MarksReferencesLiveOnTheDataStack()
        {
            var registry = CreateRegistry();
            try
            {
                var entity = new FakeEntity();
                registry.Register(entity);

                SurtrRawValue* stack = stackalloc SurtrRawValue[4];
                stack[0] = SurtrValue.CreateInt(1).Raw;
                stack[1] = RootOf(entity);

                int released = registry.CollectGarbage(
                    stack, stack + 2, null, null, 0, ReadOnlySpan<SurtrStaticBlock>.Empty, ReadOnlySpan<SurtrRawValue>.Empty, fullCollection: true);

                Assert.Equal(0, released);
                Assert.NotNull(registry.Get(entity.GetSurtrReference()));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void CollectGarbage_IgnoresStackSlotsPastStackTop()
        {
            var registry = CreateRegistry();
            try
            {
                var entity = new FakeEntity();
                registry.Register(entity);

                SurtrRawValue* stack = stackalloc SurtrRawValue[4];
                stack[2] = RootOf(entity); // beyond [stackStart, stackTop)

                int released = registry.CollectGarbage(
                    stack, stack + 1, null, null, 0, ReadOnlySpan<SurtrStaticBlock>.Empty, ReadOnlySpan<SurtrRawValue>.Empty, fullCollection: true);

                Assert.Equal(1, released);
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void CollectGarbage_MarksReferencesThroughTheHostGlobalTable()
        {
            var registry = CreateRegistry();
            try
            {
                var entity = new FakeEntity();
                registry.Register(entity);

                SurtrRawValue* globals = stackalloc SurtrRawValue[3];
                globals[0] = SurtrValue.CreateInt(7).Raw; // not a reference slot; must not be read as one
                globals[2] = RootOf(entity);

                int* referenceSlots = stackalloc int[1] { 2 };

                int released = registry.CollectGarbage(
                    null, null, globals, referenceSlots, 1, ReadOnlySpan<SurtrStaticBlock>.Empty, ReadOnlySpan<SurtrRawValue>.Empty, fullCollection: true);

                Assert.Equal(0, released);
                Assert.NotNull(registry.Get(entity.GetSurtrReference()));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void CollectGarbage_MarksReferencesThroughStaticBlocks()
        {
            var registry = CreateRegistry();
            try
            {
                var entity = new FakeEntity();
                registry.Register(entity);

                SurtrRawValue* values = stackalloc SurtrRawValue[2];
                values[1] = RootOf(entity);
                int* referenceSlots = stackalloc int[1] { 1 };

                var block = new SurtrStaticBlock(values, referenceSlots, 1);
                Span<SurtrStaticBlock> blocks = stackalloc SurtrStaticBlock[1] { block };

                int released = registry.CollectGarbage(
                    null, null, null, null, 0, blocks, ReadOnlySpan<SurtrRawValue>.Empty, fullCollection: true);

                Assert.Equal(0, released);
                Assert.NotNull(registry.Get(entity.GetSurtrReference()));
            }
            finally
            {
                registry.Dispose();
            }
        }

        #endregion

        #region Nursery vs. full collection (age-based sparing)

        [Fact]
        public void NurseryCollection_SparesAnAgedEntity_EvenAfterItBecomesUnreachable()
        {
            var registry = CreateRegistry();
            try
            {
                var entity = new FakeEntity();
                registry.Register(entity);

                // Rooted through a nursery collection: it survives and its age becomes > 0.
                Collect(ref registry, new[] { RootOf(entity) }, fullCollection: false);

                // Now unreachable, but a nursery collection only sweeps entities that have never
                // survived a prior collection - an aged entity is spared regardless of reachability.
                int released = Collect(ref registry, ReadOnlySpan<SurtrRawValue>.Empty, fullCollection: false);

                Assert.Equal(0, released);
                Assert.NotNull(registry.Get(entity.GetSurtrReference()));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void FullCollection_ReleasesAnAgedEntity_OnceItIsUnreachable()
        {
            var registry = CreateRegistry();
            try
            {
                var entity = new FakeEntity();
                registry.Register(entity);

                Collect(ref registry, new[] { RootOf(entity) }, fullCollection: false);

                int released = Collect(ref registry, ReadOnlySpan<SurtrRawValue>.Empty, fullCollection: true);

                Assert.Equal(1, released);
                Assert.Null(registry.Get(entity.GetSurtrReference()));
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void NurseryCollection_ReclaimsAnEntityThatHasNeverSurvivedACollection()
        {
            var registry = CreateRegistry();
            try
            {
                var entity = new FakeEntity();
                registry.Register(entity);

                // Never rooted, so its age is still 0 - a nursery collection may reclaim it.
                int released = Collect(ref registry, ReadOnlySpan<SurtrRawValue>.Empty, fullCollection: false);

                Assert.Equal(1, released);
            }
            finally
            {
                registry.Dispose();
            }
        }

        [Fact]
        public void Age_SaturatesInsteadOfWrapping_SoALongLivedEntityStaysSpared()
        {
            var registry = CreateRegistry();
            try
            {
                var entity = new FakeEntity();
                registry.Register(entity);

                var root = new[] { RootOf(entity) };
                // Age is a byte; run well past 255 nursery collections while rooted to make sure
                // the counter clamps at its max instead of wrapping back around to 0.
                for (int i = 0; i < 300; i++)
                    Collect(ref registry, root, fullCollection: false);

                int released = Collect(ref registry, ReadOnlySpan<SurtrRawValue>.Empty, fullCollection: false);

                Assert.Equal(0, released);
                Assert.NotNull(registry.Get(entity.GetSurtrReference()));
            }
            finally
            {
                registry.Dispose();
            }
        }

        #endregion

        #region Metrics

        [Fact]
        public void Metrics_CountCollectionsAndReleasesSeparatelyByKind()
        {
            var registry = CreateRegistry();
            try
            {
                registry.Register(new FakeEntity());
                registry.Register(new FakeEntity());

                Collect(ref registry, ReadOnlySpan<SurtrRawValue>.Empty, fullCollection: false);
                Collect(ref registry, ReadOnlySpan<SurtrRawValue>.Empty, fullCollection: true);

                Assert.Equal(2, registry.TotalCollections);
                Assert.Equal(1, registry.TotalNurseryCollections);
                Assert.Equal(1, registry.TotalFullCollections);
                Assert.Equal(2, registry.TotalCollectedEntities);
                Assert.True(registry.LastCollectionElapsedMilliseconds >= 0);
            }
            finally
            {
                registry.Dispose();
            }
        }

        #endregion

        #region Mark stack growth

        [Fact]
        public void CollectGarbage_GrowsTheMarkStack_PastItsInitialCapacity()
        {
            var registry = CreateRegistry(capacity: 64);
            try
            {
                // The mark stack starts at capacity 16; rooting more entities than that directly
                // forces it to grow mid-collection, before anything has been drained.
                const int entityCount = 40;
                var roots = new SurtrRawValue[entityCount];
                for (int i = 0; i < entityCount; i++)
                {
                    var entity = new FakeEntity();
                    registry.Register(entity);
                    roots[i] = RootOf(entity);
                }

                int released = Collect(ref registry, roots, fullCollection: true);

                Assert.Equal(0, released);
            }
            finally
            {
                registry.Dispose();
            }
        }

        #endregion

        #region Dispose

        [Fact]
        public void Dispose_ClearsCapacityAndEntities()
        {
            var registry = CreateRegistry();
            registry.Register(new FakeEntity());

            registry.Dispose();

            Assert.Equal(0, registry.Capacity);
            Assert.Empty(registry.Entities);
        }

        [Fact]
        public void Dispose_CalledTwice_IsSafe()
        {
            var registry = CreateRegistry();

            registry.Dispose();
            registry.Dispose();
        }

        #endregion
    }
}
