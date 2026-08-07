#nullable enable

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// How far along a piece of metadata is between "being declared" and "ready to run".
    /// </summary>
    /// <remarks>
    /// Metadata is mutable while it is being declared and frozen afterwards, so the runtime
    /// tables can be built once and then read without any locking or revalidation. The
    /// intermediate <see cref="Linking"/> state is what makes cycle detection free: hierarchies
    /// are linked depth-first, so meeting a type that is already linking means the graph loops
    /// back on itself.
    /// </remarks>
    public enum SurtrBuildState : byte
    {
        /// <summary>Still being declared. Members can be added; runtime tables are empty and must not be read.</summary>
        UnderConstruction = 0,

        /// <summary>Currently having its tables built. Encountering this state again means a cyclic dependency.</summary>
        Linking = 1,

        /// <summary>Fully built. Runtime tables are populated and nothing may be mutated any more.</summary>
        Built = 2,
    }
}
