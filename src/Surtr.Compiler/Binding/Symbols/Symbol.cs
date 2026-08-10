#nullable enable

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>What kind of declaration a <see cref="Symbol"/> stands for.</summary>
    public enum SymbolKind
    {
        /// <summary>A module: the only top-level container in the language (§2.1).</summary>
        Module,

        /// <summary>A type, of any of the forms <see cref="TypeSymbolKind"/> lists.</summary>
        Type,

        /// <summary>A type alias, which resolves away and never reaches the runtime (§2.7).</summary>
        Alias,

        /// <summary>A method, function, constructor, accessor or operator.</summary>
        Method,

        /// <summary>A field, whether an instance slot, a static, or a module-level variable.</summary>
        Field,

        /// <summary>A property, which is a pair of accessor methods with a name in front.</summary>
        Property,

        /// <summary>A parameter of a method or a lambda.</summary>
        Parameter,

        /// <summary>A local variable or a `let` binding inside a body.</summary>
        Local,
    }

    /// <summary>Who may see a declaration (§3.1).</summary>
    public enum Accessibility
    {
        /// <summary>Visible only inside the declaring type.</summary>
        Private,

        /// <summary>Visible inside the declaring type and everything that extends it.</summary>
        Protected,

        /// <summary>Visible inside the declaring module.</summary>
        Internal,

        /// <summary>Visible everywhere.</summary>
        Public,
    }

    /// <summary>
    /// The base of everything the binder names: modules, types, members, parameters and locals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Symbols are the compiler's own model and are deliberately *not* the runtime's metadata.
    /// A <c>SurtrClassReference</c> descriptor is canonical precisely because it discards what the
    /// runtime does not need — reference nullability, which alias was written, whether a
    /// <c>value class</c> was involved, and which type arguments a generic was given. Every one of
    /// those is something the type checker has to be able to see, so the descriptor is an
    /// <em>output</em> of binding, produced once at emit and never consulted before it.
    /// </para>
    /// <para>
    /// None of this is on an execution path, so <c>CLAUDE.md</c>'s performance rules do not apply
    /// here: interface-typed collections and virtual members are the right tools and are used
    /// freely.
    /// </para>
    /// </remarks>
    public abstract class Symbol
    {
        /// <summary>What kind of declaration this symbol stands for.</summary>
        public abstract SymbolKind Kind { get; }

        /// <summary>
        /// The name as written in source. Structural types (arrays, tuples, closures,
        /// dictionaries) have none, and return their source spelling instead.
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// The symbol this one is declared inside: a module for a top-level type, a type for a
        /// member, a method for a parameter. <see langword="null"/> for a module and for types
        /// with no declaration site of their own.
        /// </summary>
        public virtual Symbol? ContainingSymbol => null;

        /// <summary>
        /// How this symbol reads in a diagnostic — source spelling, never a descriptor.
        /// </summary>
        /// <remarks>
        /// A user who wrote <c>EntityId</c> must not be shown <c>int</c>, and one who wrote
        /// <c>(int, string)</c> must not be shown <c>T(IS)</c>. This is the only string a
        /// diagnostic may use to name a symbol.
        /// </remarks>
        public abstract string ToDisplayString();

        /// <inheritdoc/>
        public override string ToString() => ToDisplayString();
    }
}
