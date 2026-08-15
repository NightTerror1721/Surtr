#nullable enable

using Surtr.Compiler.Binding.Symbols;
using System;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Whether one place in a program may reach a member declared in another (§3.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four levels, and each answers a different question about <em>where</em> the use is:
    /// <c>public</c> asks nothing, <c>internal</c> asks which module, <c>private</c> asks which type,
    /// and <c>protected</c> asks which type or which hierarchy. So the check takes both halves of a
    /// use site's context — the type a body is in, and the module it belongs to — and nothing else.
    /// </para>
    /// <para>
    /// A module-level member has no container smaller than its module, so <c>private</c> and
    /// <c>internal</c> mean the same thing there. That is not a special case being smuggled in: §2.5
    /// says a module-level member <em>is</em> a static of its module, and a module has no instance
    /// and no inside, so there is no narrower scope for <c>private</c> to name.
    /// </para>
    /// <para>
    /// Nothing here reports. A caller decides whether an inaccessible member is an error or merely a
    /// candidate to drop — which is the difference between a call with one overload and a call with
    /// several, and only the caller knows which it has.
    /// </para>
    /// </remarks>
    internal static class AccessCheck
    {
        /// <summary>Whether <paramref name="member"/> is reachable from the given context.</summary>
        /// <param name="member">The member being reached.</param>
        /// <param name="accessibility">Its declared accessibility.</param>
        /// <param name="fromType">The type the use sits in, or <see langword="null"/> at module level.</param>
        /// <param name="fromModule">The module the use sits in.</param>
        internal static bool IsAccessible(
            Symbol member,
            Accessibility accessibility,
            NamedTypeSymbol? fromType,
            ModuleSymbol? fromModule)
        {
            if (accessibility == Accessibility.Public)
                return true;

            switch (member.ContainingSymbol)
            {
                case NamedTypeSymbol declaring:
                    switch (accessibility)
                    {
                        case Accessibility.Private:
                            return SharesOutermostType(fromType, declaring);

                        case Accessibility.Protected:
                            return SharesOutermostType(fromType, declaring) || Inherits(fromType, declaring);

                        default:
                            return SameModule(declaring.ContainingModule, fromModule);
                    }

                case ModuleSymbol declaringModule:
                    return SameModule(declaringModule, fromModule);

                default:
                    // Nothing owns it — a lambda's parameter, a local. Those are reached by scope
                    // rather than by accessibility, and scope already answered.
                    return true;
            }
        }

        /// <summary>
        /// Whether two types are the same declaration, or nested inside one outermost declaration.
        /// </summary>
        /// <remarks>
        /// A nested type sees its container's privates and a container sees its nested types', the way
        /// C# and Java both have it: what <c>private</c> names is a declaration's whole text, and a
        /// nested type is written inside that text. Compared by definition, since a construction of a
        /// generic is a different symbol from the declaration it came from (§6).
        /// </remarks>
        private static bool SharesOutermostType(NamedTypeSymbol? left, NamedTypeSymbol right)
            => left is not null && ReferenceEquals(Outermost(left), Outermost(right));

        private static NamedTypeSymbol Outermost(NamedTypeSymbol type)
        {
            var walk = type.Definition;
            while (walk.ContainingType is NamedTypeSymbol container)
                walk = container.Definition;

            return walk;
        }

        /// <summary>Whether the using type is <paramref name="declaring"/> or extends it.</summary>
        private static bool Inherits(NamedTypeSymbol? from, NamedTypeSymbol declaring)
        {
            for (var walk = from; walk is not null; walk = walk.BaseType)
            {
                if (ReferenceEquals(walk.Definition, declaring.Definition))
                    return true;
            }

            return false;
        }

        private static bool SameModule(ModuleSymbol? declaring, ModuleSymbol? from)
            => declaring is null
                || from is null
                || string.Equals(declaring.Path, from.Path, StringComparison.Ordinal);
    }
}
