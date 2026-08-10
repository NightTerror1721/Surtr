#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;

namespace Surtr.Tests.Runtime.Classes
{
    /// <summary>
    /// Covers the numbering behind interface dispatch: an id is what a receiver's class is keyed
    /// on, so two contracts sharing one would resolve to each other's methods.
    /// </summary>
    public class SurtrInterfaceIdTests
    {
        /// <summary>
        /// The built-in interfaces are numbered once, process-wide, before any runtime exists; a
        /// runtime has to carry on from there rather than restart, or the first interface any
        /// module declares collides with <c>IIterator</c>.
        /// </summary>
        [Fact]
        public void AUserInterface_DoesNotReuseABuiltInsId()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("app");
            builder.DefineInterface("IThing");
            runtime.LoadModule(builder.Build());

            Assert.True(runtime.TryGetModule("app", out var module));
            Assert.True(module.TryGetInterface("IThing", out var thing));

            // The reservation has to be a real one, or everything below passes vacuously.
            Assert.True(SurtrBuiltIns.ReservedInterfaceIds >= 4);
            Assert.True(thing.InterfaceId >= SurtrBuiltIns.ReservedInterfaceIds);

            foreach (var builtIn in SurtrBuiltIns.Module.Interfaces)
                Assert.NotEqual(builtIn.InterfaceId, thing.InterfaceId);
        }

        /// <summary>
        /// A class implementing both a built-in contract and its own resolves each to the right
        /// block of its dispatch table — which is exactly what a shared id would break.
        /// </summary>
        [Fact]
        public void AClassImplementingBoth_KeepsTheirDispatchBlocksApart()
        {
            using var runtime = new SurtrRuntime();

            var builder = new SurtrModuleBuilder("app");

            var thing = builder.DefineInterface("IThing");
            thing.DefineMethod("doThing", SurtrClassReference.Void);

            var iterableReference = SurtrClassReference.Object("surtr:IIterable");
            var iteratorReference = SurtrClassReference.Object("surtr:IIterator");

            var holder = builder.DefineClass("Holder");
            holder.Implements(thing.SelfReference, iterableReference);

            holder.DefineMethod("doThing", SurtrClassReference.Void, dispatch: SurtrMethodDispatch.Virtual)
                .Code.ReturnVoid();

            holder.DefineMethod("iterate", iteratorReference, dispatch: SurtrMethodDispatch.Virtual)
                .Code.PushNull().ReturnValue();

            runtime.LoadModule(builder.Build());

            Assert.True(runtime.TryGetModule("app", out var module));
            Assert.True(module.TryGetClass("Holder", out var built));
            Assert.True(module.TryGetInterface("IThing", out var contract));
            Assert.True(SurtrBuiltIns.Module.TryGetInterface("IIterable", out var iterable));

            Assert.Equal("doThing", MethodThrough(built, contract, "doThing"));
            Assert.Equal("iterate", MethodThrough(built, iterable, "iterate"));
        }

        private static string MethodThrough(SurtrClass type, SurtrInterface contract, string name)
        {
            Assert.True(contract.TryGetMethods(name, out var declared));

            int index = type.IndexOfInterface(contract);
            Assert.True(index >= 0, $"{type.Name} does not carry {contract.Name} in its dispatch table.");

            return type.GetInterfaceMethod(index, declared[0].VTableSlot).Name;
        }
    }
}
