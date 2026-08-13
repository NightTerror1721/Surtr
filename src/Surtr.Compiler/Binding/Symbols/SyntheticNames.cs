#nullable enable

using System;
using System.Globalization;

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>
    /// The names the compiler gives members the user did not write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A synthetic member goes into its module's real method or field table and travels in the
    /// image, so its name is ABI: changing the convention invalidates every <c>.surtrc</c> already
    /// written against it. One rule covers all of them — <b>a leading <see cref="Marker"/> means
    /// the compiler made it</b> — which makes them greppable, and which cannot collide with
    /// anything a user can declare, since an identifier is <c>letter|_</c> then
    /// <c>letterOrDigit|_</c>.
    /// </para>
    /// <para>
    /// The shape is <c>$category$context[$index]</c>. The index appears only where more than one of
    /// a kind can share a context: a method may hold several lambdas, a property has exactly one
    /// backing field.
    /// </para>
    /// <para>
    /// <b>Property accessors are deliberately not here</b>, and neither is a bridge into a generic
    /// interface's erased slot. Both are names another layer <em>looks for</em>: a property lowers
    /// to <c>get_x</c>/<c>set_x</c>, which is what <c>SurtrTypeLinker</c> wires a property up by,
    /// and a bridge occupies a contract slot keyed on name plus erased parameters — so it has to
    /// carry the contract method's own name or it fills no slot at all. Marking either would hide
    /// it from the layer that has to find it.
    /// </para>
    /// </remarks>
    public static class SyntheticNames
    {
        /// <summary>Starts and separates every synthetic name. Illegal in a Surtr identifier.</summary>
        public const char Marker = '$';

        /// <summary>The category of a lambda's lifted body.</summary>
        public const string LambdaCategory = "lambda";

        /// <summary>The category of an auto-property's backing field.</summary>
        public const string BackingCategory = "backing";

        /// <summary>The category of the static field holding a <c>singleton</c>'s one instance.</summary>
        public const string InstanceCategory = "instance";

        /// <summary>
        /// The name of the static field holding a <c>singleton</c>'s instance (§2.8).
        /// </summary>
        /// <remarks>
        /// A singleton is reached by the declaration's own name, which is a <em>type</em> name — so
        /// the value has to live somewhere the type does not, and a static of the singleton's own
        /// class is the one place that needs no second container. The field is synthetic because
        /// nothing in the source declares it and nothing may write it.
        /// </remarks>
        /// <param name="typeName">The singleton's declared name.</param>
        public static string Instance(string typeName) => Build(InstanceCategory, typeName);

        /// <summary>
        /// The name of a lambda's body, which is lifted to a static method of the declaring module.
        /// </summary>
        /// <remarks>
        /// Static, never an instance method on a synthesised class: <c>SurtrClosure</c> already
        /// copies the dispatch payload out flat and captures travel as construction arguments, so a
        /// class would add a receiver with nothing to put in it.
        /// </remarks>
        /// <param name="context">The member the lambda was written inside.</param>
        /// <param name="index">Its position among that member's lambdas, from zero.</param>
        public static string Lambda(string context, int index) => Build(LambdaCategory, context, index);

        /// <summary>The name of an auto-property's backing field.</summary>
        /// <param name="propertyName">The property it backs.</param>
        public static string BackingField(string propertyName) => Build(BackingCategory, propertyName);

        /// <summary>Whether a name was produced by this class rather than written in source.</summary>
        public static bool IsSynthetic(string name) => name.Length > 0 && name[0] == Marker;

        private static string Build(string category, string context)
            => string.Concat(Marker.ToString(), category, Marker.ToString(), context);

        private static string Build(string category, string context, int index)
        {
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index), index, "A synthetic member's index cannot be negative.");

            return string.Concat(
                Build(category, context),
                Marker.ToString(),
                index.ToString(CultureInfo.InvariantCulture));
        }
    }
}
