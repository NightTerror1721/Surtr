#nullable enable

using System;

namespace Surtr.Interop.Attributes
{
    /// <summary>
    /// Marks a CLR class, struct or enum for exposure to Surtr as a native type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Once marked, every public member is exposed with metadata derived from the C# signature;
    /// member-level attributes (<see cref="SurtrNativeMethodAttribute"/>,
    /// <see cref="SurtrNativeFieldAttribute"/>, <see cref="SurtrNativePropertyAttribute"/>,
    /// <see cref="SurtrNativeIgnoreAttribute"/>) override individual details.
    /// </para>
    /// <para>
    /// Apply multiple times with <see cref="TypeArguments"/> to expose several closed forms of a
    /// generic type. A generic type marked without <see cref="TypeArguments"/> is skipped: only
    /// closed forms are exposable.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, AllowMultiple = true)]
    public sealed class SurtrNativeTypeAttribute : Attribute
    {
        /// <summary>
        /// The Surtr module path the type is declared in (for example <c>game.entities</c>), or
        /// <see langword="null"/> to register the type globally rather than in a module.
        /// </summary>
        public string? Module { get; set; }

        /// <summary>
        /// The Surtr name of the type, or <see langword="null"/> to use the CLR type's simple name.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>Human-readable documentation for the exposed type.</summary>
        public string? Description { get; set; }

        /// <summary>
        /// The naming policy applied to this type's name, overriding wider scopes. Leave
        /// <see langword="null"/> to inherit the enclosing scope's policy.
        /// </summary>
        public SurtrNamingPolicy? NamingPolicy { get; set; }

        /// <summary>
        /// The closed-form type arguments this application exposes, for a generic type. Each entry
        /// is a <c>typeof(...)</c>; the attribute is applied once per closed form to expose.
        /// </summary>
        public Type[]? TypeArguments { get; set; }
    }
}
