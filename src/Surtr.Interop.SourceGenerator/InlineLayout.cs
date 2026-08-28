#nullable enable

using Microsoft.CodeAnalysis;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Interop.SourceGenerator
{
    /// <summary>
    /// The compile-time twin of <c>Surtr.Interop.SurtrValueLayout</c>: how a struct exposed with
    /// <c>[SurtrNativeType(Inline = true)]</c> maps onto a run of Surtr slots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reflection fallback works this out at scan time from <c>FieldInfo</c>s; the generator has
    /// to reach the same answer from Roslyn symbols, because the shim it emits reads and writes
    /// those slots by index and the two paths must produce identical metadata. Both walk the public
    /// instance fields in declaration order, which is the order the Surtr fields are declared in.
    /// </para>
    /// <para>
    /// Unlike the reflection layout this one emits <em>typed</em> code: the shim builds the struct
    /// with an object initializer and reads its fields directly, so nothing boxes and nothing goes
    /// through reflection. That is the whole advantage of the generated path over the fallback.
    /// </para>
    /// </remarks>
    internal sealed class InlineLayout
    {
        private InlineLayout(INamedTypeSymbol type, InlineSlot[] slots, int width)
        {
            Type = type;
            Slots = slots;
            Width = width;
        }

        /// <summary>The struct this layout describes.</summary>
        internal INamedTypeSymbol Type { get; }

        /// <summary>One entry per public instance field, in slot order.</summary>
        internal InlineSlot[] Slots { get; }

        /// <summary>How many contiguous slots the whole struct occupies.</summary>
        internal int Width { get; }

        /// <summary>Whether <paramref name="type"/> is a struct exposed as an inline value type.</summary>
        internal static bool IsInline(ITypeSymbol type)
            => type is INamedTypeSymbol { IsValueType: true, TypeKind: TypeKind.Struct } named
               && GeneratorSupport.FindAttribute(named, GeneratorSupport.NativeTypeAttribute) is { } attribute
               && GetBoolNamed(attribute, "Inline");

        /// <summary>The layout for <paramref name="type"/>, or null when it is not an inline struct.</summary>
        internal static InlineLayout? For(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol named || !IsInline(named))
                return null;

            var fields = Fields(named);
            var slots = new InlineSlot[fields.Count];
            int width = 0;

            for (int i = 0; i < fields.Count; i++)
            {
                var nested = For(fields[i].Type);
                slots[i] = new InlineSlot(fields[i], nested, nested?.Width ?? 1);
                width += slots[i].Width;
            }

            return new InlineLayout(named, slots, width);
        }

        /// <summary>The fields that become slots, in declaration order.</summary>
        internal static List<IFieldSymbol> Fields(INamedTypeSymbol type)
            => type.GetMembers()
                .OfType<IFieldSymbol>()
                .Where(static f => f.DeclaredAccessibility == Accessibility.Public
                                   && !f.IsStatic
                                   && !f.IsConst
                                   && !f.IsImplicitlyDeclared)
                .ToList();

        internal static bool GetBoolNamed(AttributeData? attribute, string name)
        {
            if (attribute is null)
                return false;

            foreach (var pair in attribute.NamedArguments)
            {
                if (pair.Key == name && pair.Value.Value is bool value)
                    return value;
            }

            return false;
        }
    }

    /// <summary>One field of an inline struct, and the slots it occupies.</summary>
    internal sealed class InlineSlot
    {
        internal InlineSlot(IFieldSymbol field, InlineLayout? nested, int width)
        {
            Field = field;
            Nested = nested;
            Width = width;
        }

        /// <summary>The CLR field this reads and writes.</summary>
        internal IFieldSymbol Field { get; }

        /// <summary>The field's own layout, when it is itself an inline struct.</summary>
        internal InlineLayout? Nested { get; }

        /// <summary>How many slots this field takes: its nested width, or one.</summary>
        internal int Width { get; }
    }
}
