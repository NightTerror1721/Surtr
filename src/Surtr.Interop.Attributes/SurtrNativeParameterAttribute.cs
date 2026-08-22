#nullable enable

using System;

namespace Surtr.Interop.Attributes
{
    /// <summary>
    /// Overrides the metadata of a single method or constructor parameter.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class SurtrNativeParameterAttribute : Attribute
    {
        /// <summary>
        /// The Surtr name of the parameter, or <see langword="null"/> to derive it from the CLR
        /// parameter name.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>Human-readable documentation for the parameter.</summary>
        public string? Description { get; set; }

        /// <summary>
        /// The Surtr descriptor of the parameter's type, overriding the one derived from the CLR
        /// parameter type. The CLR value is converted to that type.
        /// </summary>
        public string? TypeDescriptor { get; set; }
    }
}
