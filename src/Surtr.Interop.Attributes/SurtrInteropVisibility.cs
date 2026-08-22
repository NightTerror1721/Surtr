#nullable enable

namespace Surtr.Interop.Attributes
{
    /// <summary>
    /// How widely an exposed member is visible from Surtr. Mirrors Surtr's own visibility model so
    /// the attributes assembly stays free of any reference to <c>Surtr.Core</c>.
    /// </summary>
    public enum SurtrInteropVisibility : byte
    {
        /// <summary>Visible only inside the declaring type.</summary>
        Private = 0,

        /// <summary>Visible inside the declaring module.</summary>
        Internal = 1,

        /// <summary>Visible to the declaring type and anything deriving from it.</summary>
        Protected = 2,

        /// <summary>Visible everywhere.</summary>
        Public = 3,
    }
}
