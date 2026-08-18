#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// A first-class handle to a <see cref="SurtrModule"/>, behind Surtr's <c>Module</c> - what
    /// <c>moduleof</c> and <c>Module.get</c>/<c>Module.tryGet</c> return.
    /// </summary>
    /// <remarks>
    /// Same pattern as <see cref="SurtrTypeValue"/>, and for the same reason: <see cref="Wrapped"/>
    /// is a plain CLR reference the collector does not have to trace, because a loaded
    /// <see cref="SurtrModule"/> is owned outright by the runtime's module table for as long as the
    /// runtime lives and is never itself registered with the entity registry - only the values
    /// held in its own static storage are. One of these is created at most once per distinct
    /// <see cref="Wrapped"/> per runtime - see <see cref="SurtrRuntime.GetOrCreateModuleValue"/>.
    /// </remarks>
    public sealed class SurtrModuleValue : SurtrObject
    {
        internal SurtrModuleValue(SurtrModule wrapped) : base(SurtrBuiltIns.ModuleType)
        {
            Wrapped = wrapped;
        }

        /// <summary>The module this value reflects.</summary>
        public SurtrModule Wrapped { get; }
    }
}
