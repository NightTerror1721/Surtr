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
    /// <see cref="SurtrRuntime.GetOrCreateTypeValue(SurtrTypeInfo)"/>.
    /// </remarks>
    public sealed class SurtrTypeValue : SurtrObject
    {
        private readonly SurtrClassReference _reference;

        internal SurtrTypeValue(SurtrTypeInfo wrapped) : base(SurtrBuiltIns.Type)
        {
            Wrapped = wrapped;
        }

        /// <summary>
        /// Creates a <c>Type</c> value for a constructed generic — <c>typeof(Box&lt;int&gt;)</c>
        /// or <c>Type.get("Obox:Box`1;I")</c> — keeping the descriptor that named the
        /// construction, so <c>genericArguments</c> can answer which one it is. Never used for an
        /// open form: a descriptor whose arguments are the declaration's own parameters is the
        /// class itself, not a construction.
        /// </summary>
        internal SurtrTypeValue(SurtrTypeInfo wrapped, SurtrClassReference reference) : base(SurtrBuiltIns.Type)
        {
            Wrapped = wrapped;
            _reference = reference;
        }

        /// <summary>The class or interface this value reflects.</summary>
        public SurtrTypeInfo Wrapped { get; }

        /// <summary>
        /// The descriptor this value came from — <c>Obox:Box`1;I</c> for a construction, or a
        /// default/invalid reference when the value was reached from an instance (<c>Type.of</c>,
        /// <c>typeof(x)</c>), which cannot carry one. <c>genericArguments</c> is empty exactly when
        /// this is not valid or names an open form.
        /// </summary>
        public SurtrClassReference Reference => _reference;
    }
}
