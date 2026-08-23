#nullable enable

using Surtr.Interop.Attributes;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Surtr.Interop
{
    /// <summary>
    /// How one CLR struct exposed with <c>Inline = true</c> maps onto a run of Surtr slots, and the
    /// conversions in both directions across that run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An inline value type has no object behind it: a <c>Vector3</c> argument arrives as three
    /// consecutive slots on the stack, not as a reference to something the registry knows. So the
    /// reflection fallback cannot resolve a receiver or an argument the way it does for a native
    /// class - it has to <b>rebuild the CLR struct out of the slots</b> on the way in, and take one
    /// apart into slots on the way out. This type is that mapping, computed once per struct.
    /// </para>
    /// <para>
    /// The slot order is the order <see cref="Type.GetFields(BindingFlags)"/> reports, which is the
    /// same order <c>SurtrReflectionScanner</c> declares the Surtr fields in. Nothing guarantees
    /// that order across runtimes, and nothing has to: both sides ask the same API about the same
    /// type in the same process, so they cannot disagree with each other. What matters is that the
    /// layout and the declaration agree, not what the order happens to be.
    /// </para>
    /// </remarks>
    internal sealed class SurtrValueLayout
    {
        private SurtrValueLayout(Type clrType, SurtrValueSlot[] slots, int width)
        {
            ClrType = clrType;
            Slots = slots;
            Width = width;
        }

        /// <summary>The CLR struct this layout rebuilds.</summary>
        internal Type ClrType { get; }

        /// <summary>One entry per CLR instance field, in slot order.</summary>
        internal SurtrValueSlot[] Slots { get; }

        /// <summary>How many contiguous Surtr slots the whole struct occupies.</summary>
        internal int Width { get; }

        private static readonly Dictionary<Type, SurtrValueLayout> Cache = new Dictionary<Type, SurtrValueLayout>();
        private static readonly object CacheLock = new object();

        /// <summary>
        /// Whether <paramref name="type"/> is a struct exposed as an inline value type.
        /// </summary>
        internal static bool IsInlineStruct(Type type)
            => type.IsValueType
               && !type.IsEnum
               && !type.IsPrimitive
               && TypeAttribute(type) is { Inline: true };

        /// <summary>The layout for <paramref name="type"/>, or null when it is not an inline struct.</summary>
        internal static SurtrValueLayout? For(Type type, SurtrNamingPolicy policy)
        {
            if (!IsInlineStruct(type))
                return null;

            lock (CacheLock)
            {
                if (Cache.TryGetValue(type, out var cached))
                    return cached;

                var built = Build(type, policy);
                Cache[type] = built;
                return built;
            }
        }

        private static SurtrValueLayout Build(Type type, SurtrNamingPolicy policy)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var slots = new SurtrValueSlot[fields.Length];
            int width = 0;

            for (int i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var attribute = field.GetCustomAttribute<SurtrNativeFieldAttribute>();
                var memberPolicy = attribute?.NamingPolicy ?? policy;

                // Built inside the lock, and a struct cannot contain itself, so the recursion is
                // finite and cannot re-enter this type.
                var nested = IsInlineStruct(field.FieldType) ? BuildCached(field.FieldType, memberPolicy) : null;

                var descriptor = attribute?.TypeDescriptor is { } declared
                    ? SurtrClassReference.FromDescriptor(declared)
                    : SurtrTypeMapper.Map(field.FieldType, memberPolicy);

                slots[i] = new SurtrValueSlot(field, descriptor, nested, nested?.Width ?? 1);
                width += slots[i].Width;
            }

            return new SurtrValueLayout(type, slots, width);
        }

        private static SurtrValueLayout BuildCached(Type type, SurtrNamingPolicy policy)
        {
            if (Cache.TryGetValue(type, out var cached))
                return cached;

            var built = Build(type, policy);
            Cache[type] = built;
            return built;
        }

        /// <summary>
        /// Rebuilds the CLR struct from <see cref="Width"/> slots starting at <paramref name="offset"/>.
        /// </summary>
        /// <remarks>
        /// The struct is built boxed and handed back boxed, because that is the only form reflection
        /// can set a field on and the only form <see cref="MethodBase.Invoke(object, object[])"/>
        /// takes. The box is this method's own and never escapes into Surtr, so it costs one
        /// allocation per call into a native member - the price of the reflection fallback, which
        /// the source generator's typed path does not pay.
        /// </remarks>
        internal object Read(SurtrRuntime runtime, SurtrCallArguments arguments, int offset)
        {
            object boxed = Activator.CreateInstance(ClrType)!;
            int at = offset;

            foreach (var slot in Slots)
            {
                object? value = slot.Nested is null
                    ? SurtrMarshaler.ToClr(runtime, arguments.GetValue(at), slot.Field.FieldType, slot.Descriptor)
                    : slot.Nested.Read(runtime, arguments, at);

                slot.Field.SetValue(boxed, value);
                at += slot.Width;
            }

            return boxed;
        }

        /// <summary>
        /// Takes <paramref name="boxed"/> apart into <see cref="Width"/> entries of
        /// <paramref name="destination"/>, starting at <paramref name="offset"/>.
        /// </summary>
        /// <remarks>
        /// Writes into a buffer rather than straight into the call's result slots on purpose: the
        /// in-place convention requires every input to be read before the first write, and the
        /// results alias the arguments. The caller fills the buffer, then commits it.
        /// </remarks>
        internal void Write(SurtrRuntime runtime, object boxed, SurtrValue[] destination, int offset)
        {
            int at = offset;

            foreach (var slot in Slots)
            {
                object? value = slot.Field.GetValue(boxed);

                if (slot.Nested is null)
                    destination[at] = SurtrMarshaler.ToSurtr(runtime, value, slot.Descriptor);
                else
                    slot.Nested.Write(runtime, value!, destination, at);

                at += slot.Width;
            }
        }

        private static SurtrNativeTypeAttribute? TypeAttribute(Type type)
        {
            foreach (var attribute in type.GetCustomAttributes(typeof(SurtrNativeTypeAttribute), inherit: false))
            {
                if (attribute is SurtrNativeTypeAttribute typed)
                    return typed;
            }

            return null;
        }
    }

    /// <summary>One CLR field of an inline struct, and the slots it occupies.</summary>
    internal sealed class SurtrValueSlot
    {
        internal SurtrValueSlot(FieldInfo field, SurtrClassReference descriptor, SurtrValueLayout? nested, int width)
        {
            Field = field;
            Descriptor = descriptor;
            Nested = nested;
            Width = width;
        }

        /// <summary>The CLR field this reads and writes.</summary>
        internal FieldInfo Field { get; }

        /// <summary>The Surtr type of the field, for the scalar conversion.</summary>
        internal SurtrClassReference Descriptor { get; }

        /// <summary>The field's own layout, when it is itself an inline struct.</summary>
        internal SurtrValueLayout? Nested { get; }

        /// <summary>How many slots this field takes: its nested width, or one.</summary>
        internal int Width { get; }
    }
}
