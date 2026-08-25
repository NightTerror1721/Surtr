#nullable enable

using Surtr.Compiler.Binding.Symbols;
using System;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Recognises the built-in attribute vocabulary (§11) on a bound symbol.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §11 leaves the vocabulary open, so the binder does not know any attribute by name — what it
    /// checks is only that a use names a class extending <c>Attribute</c>, exactly as
    /// <see cref="Binder"/> does for the root itself. The attributes the compiler gives meaning to
    /// are therefore recognised here, after binding, by their class's simple name against a
    /// declaration's recorded uses: <c>Obsolete</c> and <c>NoDiscard</c>, both declared by the
    /// runtime's built-in module and so in scope of every compilation.
    /// </para>
    /// <para>
    /// Recognition is by name rather than identity for the same reason
    /// <c>ExtendsAttribute</c> walks names instead of one known symbol: an attribute class written
    /// in source — a test's own <c>attribute class Obsolete</c>, or one from another module's image
    /// — means the same thing wherever its name reaches. A use whose arguments were rejected never
    /// gets here, because <c>BindAttributes</c> drops it before recording.
    /// </para>
    /// </remarks>
    internal static class BuiltInAttributes
    {
        /// <summary>The class name <c>@Obsolete(reason)</c> resolves to.</summary>
        internal const string Obsolete = "Obsolete";

        /// <summary>The class name <c>@NoDiscard(reason)</c> resolves to.</summary>
        internal const string NoDiscard = "NoDiscard";

        /// <summary>The recorded use of the named built-in on this symbol, if there is one.</summary>
        private static AttributeUse? Find(Symbol symbol, string name)
        {
            var attributes = symbol.Attributes;
            if (attributes.Count == 0)
                return null;

            for (int i = 0; i < attributes.Count; i++)
            {
                if (string.Equals(attributes[i].Type.Name, name, StringComparison.Ordinal))
                    return attributes[i];
            }

            return null;
        }

        /// <summary>The reason string a built-in use carries, or null when none was written.</summary>
        private static string? Reason(AttributeUse? use)
        {
            if (use is null || use.Arguments.Count == 0)
                return null;

            return use.Arguments[0] as string;
        }

        /// <summary>Whether this declaration is marked <c>@Obsolete</c>.</summary>
        internal static bool IsObsolete(Symbol symbol) => Find(symbol, Obsolete) is not null;

        /// <summary>The message an <c>@Obsolete</c> mark carries, quoted at every warning site.</summary>
        internal static string? ObsoleteReason(Symbol symbol) => Reason(Find(symbol, Obsolete));

        /// <summary>Whether this method is marked <c>@NoDiscard</c>.</summary>
        internal static bool IsNoDiscard(Symbol symbol) => Find(symbol, NoDiscard) is not null;

        /// <summary>The message an <c>@NoDiscard</c> mark carries, quoted when a result is dropped.</summary>
        internal static string? NoDiscardReason(Symbol symbol) => Reason(Find(symbol, NoDiscard));

        /// <summary>The text for an obsolete warning about <paramref name="symbol"/>'s use.</summary>
        internal static string ObsoleteMessage(Symbol symbol, string used)
        {
            string? reason = ObsoleteReason(symbol);
            return reason is null
                ? $"'{used}' is obsolete."
                : $"'{used}' is obsolete: {reason}";
        }
    }
}
