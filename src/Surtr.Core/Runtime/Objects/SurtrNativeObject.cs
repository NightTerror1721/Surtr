#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// A host object seen from inside Surtr: the <see cref="SurtrValueTypeCode.Native"/>
    /// counterpart of <see cref="SurtrInstance"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where a <see cref="SurtrInstance"/> stores its state in Surtr slots the VM owns, a native
    /// object stores nothing: its state lives in the CLR object it points at, and its class's
    /// methods are native entry points that reach that object through this handle. That is what
    /// lets a Unity <c>GameObject</c> or any other host type appear in Surtr code as a first-class
    /// object, with a <see cref="SurtrRef"/> the collector can trace, without the VM knowing
    /// anything about its layout.
    /// </para>
    /// <para>
    /// Left open for the host to derive from, which is the point: a host writing a proxy for a
    /// specific type wants its own typed fields (a resolved component, a cached handle, an id)
    /// rather than one <see cref="object"/> it has to cast on every call. <see cref="Target"/> is a
    /// plain field set at construction rather than an abstract property, so reading it from a
    /// native entry point is a load and not a virtual call; a subclass that keeps its state in its
    /// own fields can pass <see langword="null"/> and ignore it.
    /// </para>
    /// <para>
    /// <see cref="SurtrNativeProxy"/> is the general case for hosts that just need to park a
    /// reference.
    /// </para>
    /// </remarks>
    public abstract class SurtrNativeObject : SurtrObject
    {
        /// <summary>The host object behind this handle, or <see langword="null"/> for a subclass holding its own state.</summary>
        protected readonly object? NativeTarget;

        /// <summary>Creates a native object of <paramref name="class"/>, pointing at <paramref name="target"/>.</summary>
        /// <exception cref="ArgumentException"><paramref name="class"/> is not a native class.</exception>
        protected SurtrNativeObject(SurtrClass @class, object? target) : base(@class)
        {
            if (@class.TypeCode != SurtrValueTypeCode.Native)
                throw new ArgumentException(
                    $"'{@class.Name}' is a {@class.TypeCode} class; a native object needs a class declared as {nameof(SurtrValueTypeCode.Native)}.",
                    nameof(@class));

            NativeTarget = target;
        }

        /// <summary>The host object behind this handle.</summary>
        public object? Target
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => NativeTarget;
        }

        /// <summary>The host object behind this handle, as <typeparamref name="T"/>, or null if it is not one.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T? TargetAs<T>() where T : class => NativeTarget as T;

        // The host object is a CLR object the CLR's own collector keeps alive through this field;
        // it is not a Surtr entity and has no SurtrRef. A subclass holding Surtr values in its own
        // fields must override this and mark them.
        internal override void VisitReferences(SurtrEntityMarker marker) { }
    }

    /// <summary>
    /// The catch-all native object: a Surtr handle to an arbitrary CLR <see cref="object"/>.
    /// </summary>
    /// <remarks>
    /// For everything the host wants to pass through Surtr without describing it - an opaque
    /// payload carried from one host call to another, or a type not worth writing a proxy class
    /// for. Its class is <see cref="SurtrBuiltIns.NativeObject"/>, the one native class that exists
    /// without the host declaring anything, so it has no members of its own: the only thing Surtr
    /// code can do with one is hold it and hand it back.
    /// </remarks>
    public sealed class SurtrNativeProxy : SurtrNativeObject
    {
        internal SurtrNativeProxy(object? target) : base(SurtrBuiltIns.NativeObject, target) { }

        internal SurtrNativeProxy(SurtrClass @class, object? target) : base(@class, target) { }

        /// <inheritdoc/>
        public override string ToString() => NativeTarget?.ToString() ?? "null";
    }
}
