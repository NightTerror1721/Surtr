#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// The root of everything the VM treats as a language-level object: the built-in composites,
    /// boxed primitives, instances of Surtr classes, and host objects alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every object knows its own <see cref="SurtrClass"/>, because everything in Surtr is an
    /// object and a value has to be able to answer what it is without the interpreter having
    /// tracked it. <see cref="SurtrValue"/> is the exception that proves the rule: a NaN-boxed
    /// primitive carries no class pointer, only a tag, because it never leaves the interpreter's
    /// hands. The moment such a value has to be treated as an object it becomes a
    /// <see cref="SurtrBoxed"/> - which carries exactly the same class the unboxed primitive
    /// would have.
    /// </para>
    /// <para>
    /// <see cref="TypeCode"/> is a copy of the class's own, not a second source of truth. Almost
    /// every dispatch the interpreter makes over an object ("is this an array?") only needs the
    /// family, and duplicating one byte here turns that into a field read on the object already
    /// in cache instead of a second dereference into class metadata.
    /// </para>
    /// </remarks>
    public abstract class SurtrObject : SurtrRuntimeEntity
    {
        /// <summary>The class this object is an instance of. Never null.</summary>
        internal readonly SurtrClass Class;

        /// <summary>
        /// <see cref="Class"/>'s type family, cached so a family test costs one load off the
        /// object rather than a hop through class metadata.
        /// </summary>
        internal readonly SurtrValueTypeCode TypeCode;

        private protected SurtrObject(SurtrClass @class)
        {
            Class = @class;
            TypeCode = @class.TypeCode;
        }

        /// <summary>The class this object is an instance of.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrClass GetClass() => Class;

        /// <summary>The type family this object belongs to.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrValueTypeCode GetTypeCode() => TypeCode;

        /// <summary>Whether this object is an instance of <paramref name="type"/> or of anything deriving from it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInstanceOf(SurtrClass type) => Class.IsSubclassOf(type);

        /// <summary>
        /// <c>equals</c>, resolved once against this object's own class and taken the fast way
        /// where nothing overrode it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For C# code holding an untyped/erased value - a dictionary keyed by <c>unknown</c>, an
        /// array searched by <c>indexOf</c> over an erased element type, anywhere the static type
        /// that would pick a specialised comparison is already gone - this is the one place to ask
        /// "does this object's class actually override <c>equals</c>, and if so, what does it say".
        /// A statically-typed call site never reaches this at all: the compiler already resolved
        /// <c>x.equals(y)</c> to a real <c>InvokeVirtual</c> at compile time, the same as any other
        /// virtual call, and this exists for the C# side of the boundary that has no such site to
        /// resolve.
        /// </para>
        /// <para>
        /// <b>The fast path is the common case.</b> <c>Class.VirtualMethods[slot]</c> is still
        /// <see cref="SurtrObjectBuiltIn.EqualsMethod"/> itself - the same reference every class
        /// inherits until something overrides it - for the overwhelming majority of objects, which
        /// answers with a bare reference compare and no VM call at all - the same thing the
        /// inherited default's own native body would have answered, just without paying to get
        /// there. It deliberately does <b>not</b> reach back into <see cref="SurtrValueComparer"/>
        /// for that: <see cref="SurtrValueComparer.ReferencesEqual"/>'s own default case is what
        /// calls here, so looping back into the comparer would be this method calling its own
        /// caller. Only when the slot resolves to something else - a real override a class wrote -
        /// does this reach for <see cref="SurtrRuntime.Invoke(SurtrMethodInfo, SurtrValue[])"/>,
        /// which is the one path genuinely able to run whatever that override says.
        /// </para>
        /// </remarks>
        public bool EqualsOverridable(SurtrRuntime runtime, SurtrValue other)
        {
            var resolved = Class.VirtualMethods[SurtrObjectBuiltIn.EqualsMethod.VTableSlot];

            if (ReferenceEquals(resolved, SurtrObjectBuiltIn.EqualsMethod))
                return other.IsReference && GetSurtrReference() == other.AsReference;

            var self = SurtrValue.CreateReference(GetSurtrReference());
            return runtime.Invoke(resolved, self, other).AsBool;
        }

        /// <summary>The <c>hashCode</c> counterpart of <see cref="EqualsOverridable"/>, same rule and same reason not to call back into the comparer on the fast path.</summary>
        public int HashCodeOverridable(SurtrRuntime runtime)
        {
            var resolved = Class.VirtualMethods[SurtrObjectBuiltIn.HashCodeMethod.VTableSlot];

            if (ReferenceEquals(resolved, SurtrObjectBuiltIn.HashCodeMethod))
                return GetSurtrReference();

            var self = SurtrValue.CreateReference(GetSurtrReference());
            return runtime.Invoke(resolved, self).AsInt;
        }

        /// <summary>
        /// The <c>toString</c> counterpart of <see cref="EqualsOverridable"/>, same rule - the fast
        /// path is the class's bare name, matching what the inherited default's own body answers.
        /// </summary>
        public string ToStringOverridable(SurtrRuntime runtime)
        {
            var resolved = Class.VirtualMethods[SurtrObjectBuiltIn.ToStringMethod.VTableSlot];

            if (ReferenceEquals(resolved, SurtrObjectBuiltIn.ToStringMethod))
                return Class.Name;

            var self = SurtrValue.CreateReference(GetSurtrReference());
            var result = runtime.Invoke(resolved, self);
            return runtime.Resolve<SurtrString>(result)?.Value ?? Class.Name;
        }

        // Deliberately empty rather than marking Class. Class metadata is not a collectable
        // value: modules, classes and members are owned by the runtime's managed tables for its
        // whole lifetime and are never handed a SurtrRef, so marking one would be a call per
        // object per collection that can only ever hit the `ref <= 0` early-out. Subclasses
        // override this to visit the values they actually hold, and none of them needs to chain
        // back to here.
        internal override void VisitReferences(SurtrEntityMarker marker) { }
    }
}
