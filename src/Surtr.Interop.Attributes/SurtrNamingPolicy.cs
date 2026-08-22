#nullable enable

namespace Surtr.Interop.Attributes
{
    /// <summary>
    /// How a CLR name is adapted when it becomes a Surtr type or member name.
    /// </summary>
    public enum SurtrNamingPolicy
    {
        /// <summary>
        /// The language's own convention: <see cref="Surtr"/>.
        /// </summary>
        Default = 0,

        /// <summary>
        /// Types keep PascalCase; members are adapted to camelCase. This is the "adapt the names
        /// where Surtr would" default, since C# writes members PascalCase and Surtr camelCase.
        /// </summary>
        Surtr = 1,

        /// <summary>Names are used exactly as written in C# (no adaptation).</summary>
        PascalCase = 2,

        /// <summary>Type and member names have their first letter lowered.</summary>
        CamelCase = 3,

        /// <summary>Names are converted to snake_case (DoWork becomes do_work).</summary>
        SnakeCase = 4,

        /// <summary>Names are lowercased entirely.</summary>
        LowerCase = 5,

        /// <summary>Names are uppercased entirely.</summary>
        UpperCase = 6,
    }
}
