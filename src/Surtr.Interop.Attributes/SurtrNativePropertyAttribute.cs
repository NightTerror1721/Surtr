#nullable enable

using System;

namespace Surtr.Interop.Attributes
{
    /// <summary>
    /// Overrides the metadata of an exposed property. Read-only is not a property here: it is
    /// derived from whether the CLR property exposes a public getter and setter (getter only means
    /// read-only).
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class SurtrNativePropertyAttribute : SurtrNativeMemberAttribute
    {
        /// <summary>
        /// The Surtr descriptor of the property's type, overriding the one derived from the CLR
        /// property type. The CLR value is converted to that type on read and back on write.
        /// </summary>
        public string? TypeDescriptor { get; set; }
    }
}
