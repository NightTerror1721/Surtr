#nullable enable

using Surtr.Runtime.Classes;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// An instance of a class declared in Surtr source: a class pointer plus a flat block of
    /// field slots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only state is <see cref="Fields"/>, indexed by <see cref="SurtrFieldInfo.Slot"/>. The
    /// layout is entirely the class's business - the linker flattens inherited fields in first and
    /// keeps their base-class slot numbers - so an instance carries no layout of its own and a
    /// <c>FieldGet</c> compiled against a base type indexes a derived instance unchanged.
    /// </para>
    /// <para>
    /// Unlike every built-in, this type has no built-in class behind it: its class is whatever the
    /// program declared, which is also why the allocator has to be told the slot count rather than
    /// knowing it.
    /// </para>
    /// </remarks>
    public sealed class SurtrInstance : SurtrObject
    {
        /// <summary>The instance's field storage, indexed by slot.</summary>
        internal readonly SurtrValue[] Fields;

        internal SurtrInstance(SurtrClass @class, int fieldCount) : base(@class)
        {
            Fields = fieldCount > 0 ? new SurtrValue[fieldCount] : Array.Empty<SurtrValue>();
        }

        /// <summary>Allocates an instance sized from its class's own layout, as <c>ObjNew</c> does.</summary>
        internal SurtrInstance(SurtrClass @class) : this(@class, @class.InstanceSlotCount) { }

        /// <summary>How many field slots this instance has.</summary>
        public int SlotCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Fields.Length;
        }

        /// <summary>Reads or writes a field slot. The caller is responsible for the range check.</summary>
        public ref SurtrValue this[int slot]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref Fields[slot];
        }

        internal override void VisitReferences(SurtrEntityMarker marker)
        {
            // Walk only the slots the class says hold references, rather than tag-testing all of
            // them: the compiler already knows which fields are reference-typed, so ReferenceSlots
            // turns tracing an instance into k marks instead of n branches - and it cannot be
            // fooled by a float whose bits land in the reference tag range.
            ref var slots = ref Class.ReferenceSlots;
            var fields = Fields;

            for (int i = 0; i < slots.Length; i++)
                marker.Mark(fields[slots[i]].AsReference);
        }
    }
}
