#nullable enable

using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    public abstract class SurtrRuntimeEntity
    {
        internal SurtrRef SurtrRef = SurtrValue.NullRef;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrRef GetSurtrReference() => SurtrRef;

        internal abstract void VisitReferences(SurtrEntityMarker marker);
    }
}
