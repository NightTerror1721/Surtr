#nullable enable

using Surtr.Compiler.Binding.Symbols;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Surtr.Compiler.CodeGen
{
    /// <summary>
    /// The flattened slot layout of a multi-field <c>value class</c>: how many frame slots one
    /// value occupies, and where each field starts inside the block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A field whose type is a primitive or a reference occupies one slot; a field whose type is
    /// another multi-field value class occupies that value's whole width, flattened in place -
    /// which is why a read of <c>outer.inner.x</c> is one addition away from the block's base.
    /// The layout mirrors what the runtime linker computes from the emitted metadata, and both
    /// derive it from the same declared field types, so the two cannot disagree.
    /// </para>
    /// <para>
    /// Computed on demand and cached per symbol. The visiting set turns a self-referential
    /// declaration into an error instead of a hang; the slot cap mirrors the one byte of
    /// immediate every call carries its argument count in.
    /// </para>
    /// </remarks>
    internal static class ValueTypeLayout
    {
        /// <summary>How many slots one inline value may occupy across a call boundary.</summary>
        internal const int MaxSlots = 254;

        /// <summary>
        /// Whether values of this type live inline as more than one slot - a multi-field value
        /// class, a tuple of at least one element, or a <c>range</c> - and if so, the value's
        /// flattened width.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the one predicate every width decision asks: locals, temporaries, parameters,
        /// returns, field storage and the equality walk all branch on it, so a type either rides
        /// the whole inline machinery or stays a single-slot reference.
        /// </para>
        /// <para>
        /// The empty tuple is deliberately excluded: with nothing to flatten there is no block to
        /// move, and threading a zero-width value through opcodes that all count at least one slot
        /// would buy nothing over keeping it boxed - an arity-zero <c>TupPack</c> is already free
        /// of per-element work. A nested tuple flattens into its parent's block exactly as a
        /// nested value class does; a still-abstract element (<c>G0</c>) contributes its erased
        /// single slot, which is what crosses erasure boundaries anyway.
        /// </para>
        /// <para>
        /// A range is always exactly three slots - start, end, inclusive - by its own descriptor,
        /// the same answer the runtime linker gives for the type wherever it links storage.
        /// </para>
        /// </remarks>
        internal static bool IsInlineType(TypeSymbol type, out int width)
        {
            var bare = type.NonNullable;

            if (bare.SpecialType == SpecialType.Range)
            {
                width = 3;
                return true;
            }

            if (bare is NamedTypeSymbol named && named.TypeKind is TypeSymbolKind.ValueClass or TypeSymbolKind.Enum)
            {
                // A one-field wrapper is erased to the field it wraps - so it occupies exactly
                // that field's own width. `EntityId` over an `int` stays one slot; a wrapper
                // over a range or a tuple rides the whole block, which is what every load and
                // store of the wrapper already moves once the width says so. An enum never sets
                // `UnderlyingType` (§6.1: its descriptor stays nominal), so it always falls
                // through to the field-based width below.
                if (named.UnderlyingType is TypeSymbol underlying)
                {
                    width = WidthOfType(underlying.NonNullable);
                    return width > 1;
                }

                if (IsMultiField(named))
                {
                    width = TryGet(named, out var layout, out _) ? layout.Width : 1;
                    return width > 1;
                }
            }

            if (bare is TupleTypeSymbol tuple && tuple.ElementTypes.Count > 0)
            {
                int total = 0;
                foreach (var element in tuple.ElementTypes)
                {
                    total += WidthOfType(element);
                    if (total > MaxSlots)
                        break;
                }

                width = total;
                return true;
            }

            width = 1;
            return false;
        }

        /// <summary>
        /// Whether this named value class lives as a multi-field slot block rather than erasing
        /// to its single field - the shapes whose own layout the emitter reads per field. An
        /// enum is always block-shaped once it has user fields; a single-field enum (just its
        /// synthetic <c>value</c>) rides one slot without erasing.
        /// </summary>
        internal static bool IsBlockValueClass(NamedTypeSymbol type)
            => type.TypeKind is TypeSymbolKind.ValueClass or TypeSymbolKind.Enum
               && type.UnderlyingType is null
               && IsMultiField(type);

        /// <summary>How many slots one value of this type occupies inline: its flattened width when it is an inline type, one otherwise.</summary>
        internal static int WidthOfType(TypeSymbol type)
            => IsInlineType(type, out int width) ? width : 1;

        internal sealed class Layout
        {
            /// <summary>The whole block's width, all nested values flattened.</summary>
            public readonly int Width;

            /// <summary>The instance fields, in declaration order.</summary>
            public readonly FieldSymbol[] Fields;

            /// <summary>Per instance field, the offset where its block starts.</summary>
            public readonly int[] Offsets;

            /// <summary>Per instance field, how many slots it occupies.</summary>
            public readonly int[] FieldWidths;

            public Layout(int width, FieldSymbol[] fields, int[] offsets, int[] fieldWidths)
            {
                Width = width;
                Fields = fields;
                Offsets = offsets;
                FieldWidths = fieldWidths;
            }
        }

        private static readonly ConditionalWeakTable<NamedTypeSymbol, Layout> Cache = new();

        [ThreadStatic]
        private static HashSet<NamedTypeSymbol>? _visiting;

        /// <summary>
        /// Whether this named type is a value class with several fields - one that lives as a slot
        /// block rather than erasing to its single field.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Counts the declared instance fields rather than trusting
        /// <see cref="NamedTypeSymbol.UnderlyingType"/> alone: a substituted generic clone carries
        /// the value-class kind but not the original's underlying field, and a one-field wrapper
        /// must stay on its erasure path even through a clone.
        /// </para>
        /// <para>
        /// An enum is included: from §2.4's migration it is a value class whose synthetic
        /// <c>value</c> field is always present, so an enum with user fields is block-shaped and
        /// one without is a single slot.
        /// </para>
        /// </remarks>
        internal static bool IsMultiField(NamedTypeSymbol type)
        {
            if (type.TypeKind is not (TypeSymbolKind.ValueClass or TypeSymbolKind.Enum)
                || type.UnderlyingType is not null)
            {
                return false;
            }

            int instanceFields = 0;
            foreach (var member in type.Members)
            {
                if (member is FieldSymbol { IsStatic: false })
                {
                    instanceFields++;
                    if (instanceFields > 1)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Computes (or recovers) the layout of <paramref name="type"/>, reporting through
        /// <paramref name="error"/> when no finite layout fits the slot budget.
        /// </summary>
        internal static bool TryGet(NamedTypeSymbol type, out Layout layout, out string? error)
        {
            if (Cache.TryGetValue(type, out layout!))
            {
                error = null;
                return true;
            }

            _visiting ??= new HashSet<NamedTypeSymbol>();

            if (!_visiting.Add(type))
            {
                error = $"'{type.Name}' contains itself; no finite value layout can hold it.";
                return false;
            }

            try
            {
                var fields = new List<FieldSymbol>();
                foreach (var member in type.Members)
                {
                    if (member is FieldSymbol { IsStatic: false } field)
                        fields.Add(field);
                }

                // A substituted clone of a one-field wrapper has no members of its own - it stays
                // on the erasure path and never reaches here.
                if (fields.Count == 0)
                {
                    error = $"'{type.Name}' declares no instance fields.";
                    return false;
                }

                var offsets = new int[fields.Count];
                var widths = new int[fields.Count];

                int offset = 0;
                for (int i = 0; i < fields.Count; i++)
                {
                    var fieldType = fields[i].Type.NonNullable;

                    int width;
                    if (fieldType.SpecialType == SpecialType.Range)
                    {
                        // A range's width rides its descriptor alone - three slots, always.
                        width = 3;
                    }
                    else if (fieldType is TupleTypeSymbol fieldTuple && fieldTuple.ElementTypes.Count > 0)
                    {
                        int total = 0;
                        foreach (var element in fieldTuple.ElementTypes)
                            total += WidthOfType(element);

                        width = Math.Min(total, MaxSlots);
                    }
                    else if (fieldType is NamedTypeSymbol nested
                             && nested.TypeKind is TypeSymbolKind.ValueClass or TypeSymbolKind.Enum)
                    {
                        // A nested wrapper rides the value it erases to; a multi-field class
                        // keeps its own error path, so an unflattenable declaration refuses here
                        // rather than shrinking to one silent slot. An enum field flattens the
                        // same way, since an enum is now a value class.
                        if (nested.UnderlyingType is TypeSymbol erasedTo)
                        {
                            width = WidthOfType(erasedTo.NonNullable);
                        }
                        else if (!TryGet(nested, out var inner, out var innerError))
                        {
                            error = innerError;
                            return false;
                        }
                        else
                        {
                            width = inner.Width;
                        }
                    }
                    else
                    {
                        width = 1;
                    }

                    offsets[i] = offset;
                    widths[i] = width;
                    offset += width;
                }

                if (offset > MaxSlots)
                {
                    error = $"Value type '{type.Name}' flattens to {offset} slots; the limit is {MaxSlots}, because a call carries its arguments in one byte of immediate.";
                    return false;
                }

                layout = new Layout(offset, fields.ToArray(), offsets, widths);
                Cache.Add(type, layout);
                error = null;
                return true;
            }
            finally
            {
                _visiting.Remove(type);
            }
        }
    }
}
