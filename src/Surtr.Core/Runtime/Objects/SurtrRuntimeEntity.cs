#nullable enable

using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
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
