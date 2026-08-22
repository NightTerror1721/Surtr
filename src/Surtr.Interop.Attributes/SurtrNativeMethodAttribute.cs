#nullable enable

using System;

namespace Surtr.Interop.Attributes
{
    /// <summary>
    /// Overrides the metadata of an exposed method or constructor.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor)]
    public sealed class SurtrNativeMethodAttribute : SurtrNativeMemberAttribute
    {
        /// <summary>
        /// The Surtr descriptor of the method's return type, overriding the one derived from the
        /// CLR return type. The returned CLR value is converted to that type. A <c>void</c> return
        /// is <c>V</c>; see Surtr's descriptor grammar.
        /// </summary>
        public string? ReturnDescriptor { get; set; }
    }
}
