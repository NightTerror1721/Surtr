#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// A first-class handle to a <see cref="SurtrClass"/> or <see cref="SurtrInterface"/>, behind
    /// Surtr's <c>Type</c>.
    /// </summary>
    /// <remarks>
    /// Class and interface metadata is owned outright and lives for its owner's whole lifetime,
    /// never traced and never registered with any entity registry of its own - see
    /// <c>SurtrBuiltIns</c>'s remarks on why. This is the one place a Surtr value carries a raw
    /// reference to it anyway, the same way <see cref="SurtrNativeObject"/> carries a raw
    /// reference to a host object: <see cref="Wrapped"/> is plain CLR state the collector does not
    /// have to trace, because what it points at outlives this object regardless. One of these is
    /// created at most once per distinct <see cref="Wrapped"/> per runtime - see
    /// <see cref="SurtrRuntime.GetOrCreateTypeValue"/>.
    /// </remarks>
    public sealed class SurtrTypeValue : SurtrObject
    {
        internal SurtrTypeValue(SurtrTypeInfo wrapped) : base(SurtrBuiltIns.Type)
        {
            Wrapped = wrapped;
        }

        /// <summary>The class or interface this value reflects.</summary>
        public SurtrTypeInfo Wrapped { get; }
    }
}
