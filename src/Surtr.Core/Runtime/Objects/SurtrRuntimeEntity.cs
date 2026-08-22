#nullable enable

using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// An entity that owns unmanaged storage it must release when it leaves the registry.
    /// </summary>
    /// <remarks>
    /// The registry sweeps by dropping its reference to an entity - there is no finalization hook -
    /// so an unmanaged buffer owned by a collectable object would leak on every collection unless
    /// the sweep can tell the object to free it. This interface is that hook: <see cref="ReleaseBuffer"/>
    /// is called once, when the entity is released by a sweep or an explicit <see cref="SurtrEntityRegistry.Release"/>,
    /// and must be idempotent (the object may be released exactly once, but the call is the only
    /// place that is guaranteed).
    /// </remarks>
    public interface ISurtrNativeBufferOwner
    {
        /// <summary>Frees the unmanaged buffer this entity owns. Idempotent.</summary>
        void ReleaseBuffer();
    }

    /// <summary>
    /// Base type for managed objects that are registered with a <see cref="SurtrEntityRegistry"/>
    /// and addressed from unmanaged VM code via an integer <see cref="SurtrRef"/> handle.
    /// </summary>
    public abstract class SurtrRuntimeEntity
    {
        internal SurtrRef SurtrRef = SurtrValue.NullRef;

        /// <summary>Gets the <see cref="SurtrRef"/> handle this entity is registered under, or <see cref="SurtrValue.NullRef"/> if unregistered.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrRef GetSurtrReference() => SurtrRef;

        internal abstract void VisitReferences(SurtrEntityMarker marker);
    }
}
