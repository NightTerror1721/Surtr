#nullable enable

using System;

namespace Surtr.Interop.Attributes
{
    /// <summary>
    /// Exposes a static factory method as a Surtr constructor for an inline value type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Surtr constructor is reached by allocating first and running the body against the new
    /// instance as its receiver, and an inline value has nothing to allocate and no receiver to
    /// fill - it <em>is</em> its result. Ordinary instance constructors therefore cannot be exposed
    /// on an inline type; a static factory covers the case exactly, and this attribute is what
    /// makes Surtr source reach it with construction syntax (<c>Vec3(1.0, 2.0, 3.0)</c>) instead of
    /// a bare static call.
    /// </para>
    /// <para>
    /// Only valid on a public static method whose declaring type carries
    /// <see cref="SurtrNativeTypeAttribute"/> with <c>Inline = true</c> and whose return type is
    /// that same type. Anything else is refused when the type is scanned - the attribute is
    /// exclusive to inline value types.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SurtrNativeConstructorAttribute : Attribute
    {
        /// <summary>A human-readable description, surfaced to tooling.</summary>
        public string? Description { get; set; }
    }
}
