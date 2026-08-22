#nullable enable

using System;

namespace Surtr.Interop.Attributes
{
    /// <summary>
    /// Excludes an otherwise-exposed public member. A shorthand for
    /// <see cref="SurtrNativeMemberAttribute.Expose"/> set to <see langword="false"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor |
                    AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class SurtrNativeIgnoreAttribute : Attribute
    {
    }
}
