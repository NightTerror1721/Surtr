#nullable enable

using System;

namespace Surtr.Interop.Attributes
{
    /// <summary>
    /// Overrides the metadata of an exposed field.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class SurtrNativeFieldAttribute : SurtrNativeMemberAttribute
    {
        /// <summary>
        /// Whether the field is read-only from Surtr. <see langword="false"/> by default.
        /// </summary>
        public bool ReadOnly { get; set; }

        /// <summary>
        /// The Surtr descriptor of the field's type, overriding the one derived from the CLR field
        /// type. The CLR value is converted to that type on read and back on write.
        /// </summary>
        public string? TypeDescriptor { get; set; }
    }
}
