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

        /// <summary>
        /// Exposes a struct as a Surtr <b>value type</b> - a run of contiguous slots - rather than
        /// boxing it into a heap object behind a reference. Ignored on a class or an enum.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the difference between a <c>Vector3</c> that costs an allocation every time it
        /// crosses into Surtr and one that costs nothing. Inline, the struct's fields become real
        /// Surtr slots: reading <c>v.x</c> is a slot read that never enters host code, passing a
        /// <c>Vector3</c> copies three slots, and the CLR struct is rebuilt only when a native
        /// member actually needs one.
        /// </para>
        /// <para>
        /// <b>Opt-in, and deliberately so.</b> An inline value has no identity - two copies of the
        /// same <c>Vector3</c> cannot be told apart - so <c>===</c> stops meaning anything, its
        /// fields become read-only, and it can never be null. Those are the semantics a value type
        /// should have, but they are not the ones a boxed struct had, so flipping every struct over
        /// silently would change what existing host code means.
        /// </para>
        /// <para>
        /// A struct is eligible when every exposed instance field is a Surtr primitive - an
        /// integer, float, boolean or character - or another struct already exposed with
        /// <c>Inline = true</c>. A field of any other type has no inline representation, and the
        /// scanner refuses the type rather than silently exposing half of it. A nested inline
        /// struct must be registered first, the same ordering a base class already needs.
        /// </para>
        /// </remarks>
        public bool Inline { get; set; }
    }
}
