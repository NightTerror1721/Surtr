#nullable enable

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// An access-table entry a deserialized chunk has not bound yet: the member it should point
    /// at, named the way an image names one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The emitter fills the field and method tables with the metadata objects themselves, because
    /// it has them. An image cannot: it holds text, and the object it should end up pointing at
    /// depends on which runtime is loading it - a call into another module has to reach *that*
    /// runtime's copy of it. So the entries travel as names and are bound in
    /// <c>SurtrRuntime.LoadModule</c>, next to where type handles are bound, and for the same
    /// reason.
    /// </para>
    /// <para>
    /// A <see langword="null"/> <see cref="OwnerDescriptor"/> means the member is declared at
    /// module level rather than on a type - which, for an image, always means the module being
    /// loaded.
    /// </para>
    /// </remarks>
    internal readonly struct SurtrPendingMember
    {
        /// <summary>The descriptor of the type declaring the member, or <see langword="null"/> for a module-level one.</summary>
        internal readonly string? OwnerDescriptor;

        /// <summary>The member's declared name.</summary>
        internal readonly string Name;

        /// <summary>
        /// The overload this names, as <see cref="SurtrMethodInfo.SignatureKey"/> spells it, or
        /// <see langword="null"/> for a field.
        /// </summary>
        internal readonly string? SignatureKey;

        internal SurtrPendingMember(string? ownerDescriptor, string name, string? signatureKey)
        {
            OwnerDescriptor = ownerDescriptor;
            Name = name;
            SignatureKey = signatureKey;
        }
    }
}
