#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;

namespace Surtr.Tests.Runtime.Objects
{
    public class SurtrInstanceTests
    {
        private static SurtrTypeHandle HandleFor(SurtrModule module, SurtrClassReference reference)
            => module.TypeHandles.GetOrAdd(reference);

        private static SurtrFieldInfo Field(SurtrModule module, string name, SurtrClassReference type, bool isStatic = false)
            => new(name, HandleFor(module, type), isStatic, isReadOnly: false, SurtrVisibility.Public, declaringType: null);

        private static SurtrClass LinkedClass(SurtrModule module, string name, params SurtrFieldInfo[] fields)
        {
            var type = new SurtrClass(name, SurtrValueTypeCode.Object, SurtrClassReference.Object($"test:{name}"), null, false, SurtrVisibility.Public, null);
            foreach (var field in fields)
                type.AddField(field);

            module.AddClass(type);
            return type;
        }

        [Fact]
        public void SlotCount_MatchesTheClassesInstanceLayout()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var a = LinkedClass(module, "A", Field(module, "x", SurtrClassReference.Integer), Field(module, "y", SurtrClassReference.Integer));
            SurtrTypeLinker.LinkModule(module);

            var instance = runtime.NewInstance(a);

            Assert.Equal(2, instance.SlotCount);
        }

        [Fact]
        public void Indexer_ReadsAndWritesTheGivenSlot()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var a = LinkedClass(module, "A", Field(module, "x", SurtrClassReference.Integer));
            SurtrTypeLinker.LinkModule(module);

            var instance = runtime.NewInstance(a);
            instance[0] = SurtrValue.CreateInt(99);

            Assert.Equal(99, instance[0].AsInt);
        }

        [Fact]
        public void VisitReferences_TracesOnlyDeclaredReferenceSlots_NotWhateverBitsALooseSlotHappensToHold()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");

            // Slot 0 is an int (not traced); slot 1 is a string (traced).
            var a = LinkedClass(module, "A",
                Field(module, "untracedSlot", SurtrClassReference.Integer),
                Field(module, "trackedSlot", SurtrClassReference.String));
            SurtrTypeLinker.LinkModule(module);

            var instance = runtime.NewInstance(a);

            var decoy = runtime.NewString("decoy");
            SurtrValue decoyRef = SurtrValue.CreateReference(decoy.GetSurtrReference());

            var real = runtime.NewString("real");
            SurtrValue realRef = SurtrValue.CreateReference(real.GetSurtrReference());

            // Plants a reference-shaped bit pattern in the *untracked* int slot, pointing at
            // "decoy". If VisitReferences ever tag-tested slots instead of walking the class's
            // declared ReferenceSlots, this would keep "decoy" alive by accident.
            instance[0] = decoyRef;
            instance[1] = runtime.ValueOf(real);

            runtime.AddRoot(instance);
            runtime.Collect();

            Assert.Null(runtime.Resolve<SurtrString>(decoyRef));
            Assert.NotNull(runtime.Resolve<SurtrString>(realRef));
        }

        [Fact]
        public void VisitReferences_OnAClassWithNoReferenceFields_TracesNothing()
        {
            using var runtime = new SurtrRuntime();
            var module = new SurtrModule("test");
            var a = LinkedClass(module, "A", Field(module, "count", SurtrClassReference.Integer));
            SurtrTypeLinker.LinkModule(module);

            var instance = runtime.NewInstance(a);

            var decoy = runtime.NewString("decoy");
            SurtrValue decoyRef = SurtrValue.CreateReference(decoy.GetSurtrReference());
            instance[0] = decoyRef;

            runtime.AddRoot(instance);
            runtime.Collect();

            Assert.Null(runtime.Resolve<SurtrString>(decoyRef));
        }
    }
}
