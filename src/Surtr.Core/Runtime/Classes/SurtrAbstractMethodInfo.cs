#nullable enable

using Surtr.Runtime.Objects;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// A method with no body: an abstract member of a class, or any member of an interface.
    /// </summary>
    /// <remarks>
    /// The other two implementations answer "where is the body"; this one exists because there
    /// genuinely isn't one. Calling it directly is always a bug - the loader is expected to
    /// overwrite its vtable slot with a real implementation from a derived class, and to reject a
    /// concrete class that leaves any slot still pointing here.
    /// </remarks>
    public sealed class SurtrAbstractMethodInfo : SurtrMethodInfo
    {
        /// <summary>Creates metadata for a method that declares a contract and nothing else.</summary>
        public SurtrAbstractMethodInfo(
            string name,
            SurtrTypeHandle returnType,
            SurtrParameterInfo[] parameters,
            SurtrVisibility visibility,
            SurtrTypeHandle? declaringType,
            string[]? genericParameters = null,
            string[][]? genericConstraints = null,
            bool isExtension = false,
            bool isBridge = false)
            : base(
                name,
                SurtrMethodImplKind.Abstract,
                SurtrMethodDispatch.Abstract,
                // A body-less method is always an ordinary contract member: a constructor or a
                // static initializer without a body would be meaningless.
                SurtrMethodRole.Normal,
                isOverride: false,
                returnType,
                parameters,
                isStatic: false,
                visibility,
                declaringType,
                genericParameters: genericParameters,
                genericConstraints: genericConstraints,
                isExtension: isExtension,
                isBridge: isBridge)
        {
        }

        // Nothing but handles owned elsewhere, so no entity references to trace.
        internal override void VisitReferences(SurtrEntityMarker marker) { }
    }
}
