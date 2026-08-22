#nullable enable

using System;

namespace Surtr.Interop.Attributes
{
    /// <summary>
    /// Optional base attribute for overriding a single exposed member's metadata. Absent entirely,
    /// a member is exposed with defaults derived from its C# declaration.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor |
                    AttributeTargets.Field | AttributeTargets.Property)]
    public class SurtrNativeMemberAttribute : Attribute
    {
        /// <summary>
        /// The Surtr name of the member, or <see langword="null"/> to derive it from the CLR name
        /// (through the effective naming policy). Wins over <see cref="NamingPolicy"/> if both set.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>Human-readable documentation for the exposed member.</summary>
        public string? Description { get; set; }

        /// <summary>
        /// How widely the member is visible from Surtr, or <see langword="null"/> to default to
        /// public (the CLR member's effective visibility is the usual source).
        /// </summary>
        public SurtrInteropVisibility? Visibility { get; set; }

        /// <summary>
        /// The naming policy applied to this member's name, overriding wider scopes. Ignored when
        /// <see cref="Name"/> is set.
        /// </summary>
        public SurtrNamingPolicy? NamingPolicy { get; set; }

        /// <summary>
        /// Whether the member is exposed. <see langword="false"/> hides it, exactly like
        /// <see cref="SurtrNativeIgnoreAttribute"/>.
        /// </summary>
        public bool Expose { get; set; } = true;
    }
}
