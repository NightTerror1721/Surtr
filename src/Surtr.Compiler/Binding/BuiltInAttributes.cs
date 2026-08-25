#nullable enable

using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax.Ast;
using System;
using System.Collections.Generic;

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

        /// <summary>The class name <c>@Range(lo, hi)</c> resolves to.</summary>
        internal const string Range = "Range";

        /// <summary>The class name <c>@Value</c> resolves to — structural equality for a class.</summary>
        internal const string Value = "Value";

        /// <summary>The class name <c>@Export("name")</c> resolves to.</summary>
        internal const string Export = "Export";

        /// <summary>The class name <c>@Test("name")</c> resolves to.</summary>
        internal const string Test = "Test";

        /// <summary>The class name <c>@TestSuite("name")</c> resolves to.</summary>
        internal const string TestSuite = "TestSuite";

        /// <summary>The class name <c>@Pure</c> resolves to.</summary>
        internal const string Pure = "Pure";

        /// <summary>The class name <c>@MainThread</c> resolves to.</summary>
        internal const string MainThread = "MainThread";

        /// <summary>The class name <c>@ThreadSafe</c> resolves to.</summary>
        internal const string ThreadSafe = "ThreadSafe";

        /// <summary>
        /// Where each built-in may be written. The classes arrive as imported metadata, whose
        /// symbols carry no target list of their own (that is a declaration-side fact), so the
        /// restriction travels here beside the recognition that already lives in this file.
        /// </summary>
        private static readonly Dictionary<string, SurtrAttributeTargets> TargetsByBuiltinName =
            new(StringComparer.Ordinal)
            {
                [Obsolete] = SurtrAttributeTargets.Class | SurtrAttributeTargets.Interface | SurtrAttributeTargets.Enum
                    | SurtrAttributeTargets.Field | SurtrAttributeTargets.Property | SurtrAttributeTargets.Method,
                [NoDiscard] = SurtrAttributeTargets.Method,
                [Range] = SurtrAttributeTargets.Field | SurtrAttributeTargets.Property,
                [Value] = SurtrAttributeTargets.Class,
                [Export] = SurtrAttributeTargets.Class | SurtrAttributeTargets.Field | SurtrAttributeTargets.Property,
                [Test] = SurtrAttributeTargets.Method,
                [TestSuite] = SurtrAttributeTargets.Class,
                [Pure] = SurtrAttributeTargets.Method | SurtrAttributeTargets.Property,
                [MainThread] = SurtrAttributeTargets.Method | SurtrAttributeTargets.Property | SurtrAttributeTargets.Class,
                [ThreadSafe] = SurtrAttributeTargets.Method | SurtrAttributeTargets.Class,
            };

        /// <summary>
        /// The target list a built-in attribute's documentation fixes, when the name is one.
        /// </summary>
        internal static bool TryGetTargets(string attributeName, out SurtrAttributeTargets targets)
            => TargetsByBuiltinName.TryGetValue(attributeName, out targets);

        /// <summary>
        /// Whether an attribute use reaches the compiled image: <c>CompileTimeOnly</c> never does,
        /// and neither does <c>@Value</c>, whose whole meaning is spent inside the compiler.
        /// </summary>
        internal static bool ReachesImage(NamedTypeSymbol attributeType)
            => !attributeType.IsCompileTimeOnlyAttribute
                && !string.Equals(attributeType.Name, Value, StringComparison.Ordinal);

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

        /// <summary>
        /// Whether this class opts into structural equality with <c>@Value</c>. Checked on the
        /// class's own uses; a base class's mark does not spread — identity stays the default for
        /// a subclass that says nothing.
        /// </summary>
        internal static bool IsMarkedValue(Symbol symbol) => Find(symbol, Value) is not null;

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

        /// <summary>
        /// Whether this declaration is marked <c>@Pure</c>: a promise it mutates no observable
        /// state and returns the same result for the same arguments.
        /// </summary>
        internal static bool IsPure(Symbol symbol) => Find(symbol, Pure) is not null;

        /// <summary>
        /// The lower bound a <c>@Range(lo, hi)</c> mark fixes, or <see langword="null"/> when no
        /// bound was written on that side. Bounds are floats (§P4), so the folded constant arrives
        /// as a <see langword="double"/> — or a <see langword="long"/> before the widening a float
        /// slot applies to an integer argument.
        /// </summary>
        internal static double? RangeLow(Symbol symbol)
        {
            var use = Find(symbol, Range);
            if (use is null || use.Arguments.Count == 0)
                return null;
            return AsDouble(use.Arguments[0]);
        }

        /// <summary>The upper bound a <c>@Range(lo, hi)</c> mark fixes, or <see langword="null"/>.</summary>
        internal static double? RangeHigh(Symbol symbol)
        {
            var use = Find(symbol, Range);
            if (use is null || use.Arguments.Count < 2)
                return null;
            return AsDouble(use.Arguments[1]);
        }

        private static double? AsDouble(object? value) => value switch
        {
            double d => d,
            long l => l,
            _ => null,
        };
    }
}
