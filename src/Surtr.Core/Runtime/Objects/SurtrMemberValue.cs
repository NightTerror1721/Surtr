#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// A first-class handle to a <see cref="SurtrMemberInfo"/>, behind Surtr's <c>Member</c>.
    /// </summary>
    /// <remarks>
    /// Wraps the exact declaration a <c>Type.members()</c> call found - one field, one property,
    /// or one overload of a method - rather than its name. A method's overloads share a name, so
    /// re-resolving one from a name alone could not tell them apart; keeping the declaration
    /// itself sidesteps that and means <see cref="Wrapped"/>'s own
    /// <see cref="SurtrMemberInfo.Attributes"/> is always exactly the right ones.
    /// </remarks>
    public sealed class SurtrMemberValue : SurtrObject
    {
        internal SurtrMemberValue(SurtrMemberInfo wrapped) : base(SurtrBuiltIns.Member)
        {
            Wrapped = wrapped;
        }

        /// <summary>The declaration this value reflects.</summary>
        public SurtrMemberInfo Wrapped { get; }
    }
}
