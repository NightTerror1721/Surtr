#nullable enable

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

        // Deliberately empty rather than marking Class. Class metadata is not a collectable
        // value: modules, classes and members are owned by the runtime's managed tables for its
        // whole lifetime and are never handed a SurtrRef, so marking one would be a call per
        // object per collection that can only ever hit the `ref <= 0` early-out. Subclasses
        // override this to visit the values they actually hold, and none of them needs to chain
        // back to here.
        internal override void VisitReferences(SurtrEntityMarker marker) { }
    }
}
