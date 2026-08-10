#nullable enable

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>
    /// The names the runtime looks for, as opposed to the ones the compiler invents.
    /// </summary>
    /// <remarks>
    /// These are not in <see cref="SyntheticNames"/>'s scheme on purpose. A property's accessors are
    /// what <c>SurtrTypeLinker</c> matches on when it wires a property up, and a constructor's name
    /// is what <c>SurtrClassBuilder.DefineConstructor</c> defaults to — marking either as
    /// compiler-made would hide it from the layer that has to find it.
    /// </remarks>
    public static class MemberNames
    {
        /// <summary>What a constructor is called in the method table.</summary>
        public const string Constructor = "ctor";

        /// <summary>Prefixes a property's read accessor.</summary>
        public const string GetterPrefix = "get_";

        /// <summary>Prefixes a property's write accessor.</summary>
        public const string SetterPrefix = "set_";

        /// <summary>The read accessor for a property.</summary>
        public static string Getter(string propertyName) => GetterPrefix + propertyName;

        /// <summary>The write accessor for a property.</summary>
        public static string Setter(string propertyName) => SetterPrefix + propertyName;
    }
}
