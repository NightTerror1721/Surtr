#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;

namespace Surtr.Tests.Runtime.Objects
{
    public class SurtrTupleTests
    {
        [Fact]
        public void ArityConstructor_StartsWithZeroedUntaggedSlots()
        {
            using var runtime = new SurtrRuntime();
            var tuple = runtime.NewTuple(SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.Integer), arity: 2);

            Assert.Equal(2, tuple.Length);
            Assert.False(tuple.IsEmpty);

            // A fresh slot is an untagged zero (C#'s array default), not an explicitly tagged
            // null reference - SurtrValue.IsNullReference requires the reference tag, which a
            // freshly allocated slot does not carry. Only the low 32 bits (AsReference) read as
            // "null" here, which is what lets the VM treat a fresh slot as null without knowing
            // its declared type.
            Assert.Equal(0UL, tuple[0].Raw);
            Assert.False(tuple[0].IsReference);
            Assert.False(tuple[0].IsNullReference);
            Assert.Equal(SurtrValue.NullRef, tuple[0].AsReference);
        }

        [Fact]
        public void ElementsConstructor_TakesOwnershipOfTheGivenArray()
        {
            using var runtime = new SurtrRuntime();
            var elements = new[] { SurtrValue.CreateInt(1), SurtrValue.CreateInt(2) };
            var tuple = runtime.NewTuple(SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.Integer), elements);

            Assert.Equal(1, tuple[0].AsInt);
            Assert.Equal(2, tuple[1].AsInt);
        }

        [Fact]
        public void EmptyTuple_HasZeroLength()
        {
            using var runtime = new SurtrRuntime();
            var tuple = runtime.NewTuple(SurtrClassReference.Tuple(), arity: 0);

            Assert.Equal(0, tuple.Length);
            Assert.True(tuple.IsEmpty);
        }

        [Fact]
        public void IsInRange_ReflectsArity()
        {
            using var runtime = new SurtrRuntime();
            var tuple = runtime.NewTuple(SurtrClassReference.Tuple(SurtrClassReference.Integer), arity: 1);

            Assert.True(tuple.IsInRange(0));
            Assert.False(tuple.IsInRange(1));
            Assert.False(tuple.IsInRange(-1));
        }

        [Fact]
        public void SetDuringPack_WritesTheGivenSlot()
        {
            using var runtime = new SurtrRuntime();
            var tuple = runtime.NewTuple(SurtrClassReference.Tuple(SurtrClassReference.Integer, SurtrClassReference.Integer), arity: 2);

            tuple.SetDuringPack(0, SurtrValue.CreateInt(10));
            tuple.SetDuringPack(1, SurtrValue.CreateInt(20));

            Assert.Equal(10, tuple[0].AsInt);
            Assert.Equal(20, tuple[1].AsInt);
        }

        [Fact]
        public void VisitReferences_KeepsEveryElementAlive_WhileRooted()
        {
            using var runtime = new SurtrRuntime();
            var a = runtime.NewString("a");
            var b = runtime.NewString("b");
            SurtrValue aRef = SurtrValue.CreateReference(a.GetSurtrReference());
            SurtrValue bRef = SurtrValue.CreateReference(b.GetSurtrReference());

            var tuple = runtime.NewTuple(
                SurtrClassReference.Tuple(SurtrClassReference.String, SurtrClassReference.String),
                new[] { runtime.ValueOf(a), runtime.ValueOf(b) });

            runtime.AddRoot(tuple);
            runtime.Collect();

            Assert.NotNull(runtime.Resolve<SurtrString>(aRef));
            Assert.NotNull(runtime.Resolve<SurtrString>(bRef));
        }

        [Fact]
        public void VisitReferences_DoesNotKeepElementsAlive_OnceTheTupleIsUnreachable()
        {
            using var runtime = new SurtrRuntime();
            var a = runtime.NewString("a");
            SurtrValue aRef = SurtrValue.CreateReference(a.GetSurtrReference());

            runtime.NewTuple(SurtrClassReference.Tuple(SurtrClassReference.String), new[] { runtime.ValueOf(a) });
            // Never rooted.

            runtime.Collect();

            Assert.Null(runtime.Resolve<SurtrString>(aRef));
        }
    }
}
