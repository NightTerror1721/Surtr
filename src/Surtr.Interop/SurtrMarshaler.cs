#nullable enable

using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections;

namespace Surtr.Interop
{
    /// <summary>
    /// Converts between CLR values and <see cref="SurtrValue"/>, driven by a Surtr descriptor. Used
    /// by the reflection fallback; the source generator emits the same conversions inline as typed
    /// code, so nothing here is on that path.
    /// </summary>
    public static class SurtrMarshaler
    {
        /// <summary>Converts a CLR value to a Surtr value of <paramref name="descriptor"/>'s family.</summary>
        public static SurtrValue ToSurtr(SurtrRuntime runtime, object? value, SurtrClassReference descriptor)
        {
            if (value is null)
                return SurtrValue.Null;

            if (value is Delegate delegateValue)
                return SurtrDelegateMarshal.ToSurtr(runtime, delegateValue, descriptor);

            var code = descriptor.TypeCode;

            switch (code)
            {
                case SurtrValueTypeCode.Integer:
                    return SurtrValue.CreateInt(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));

                case SurtrValueTypeCode.Float:
                    return SurtrValue.CreateFloat(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));

                case SurtrValueTypeCode.Boolean:
                    return SurtrValue.CreateBool(Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture));

                case SurtrValueTypeCode.Character:
                    return SurtrValue.CreateChar(Convert.ToChar(value, System.Globalization.CultureInfo.InvariantCulture));

                case SurtrValueTypeCode.String:
                    return SurtrValue.CreateReference(runtime.InternString((string)value).GetSurtrReference());

                case SurtrValueTypeCode.Bytes:
                    return SurtrValue.CreateReference(runtime.NewBytes((byte[])value).GetSurtrReference());

                case SurtrValueTypeCode.Native:
                {
                    // An enum is its int from the migration (§2.7): marshaling a CLR enum is pure
                    // arithmetic, with no proxy, no root and no per-runtime cache — a combination
                    // of bits with no named case marshals exactly as well as a named one.
                    if (value.GetType().IsEnum)
                        return SurtrValue.CreateInt(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));

                    // A host class deriving from SurtrNativeObject is adopted as the entity itself;
                    // anything else is wrapped in a fresh proxy. One crossing point, both shapes.
                    return runtime.RegisterHost(value);
                }

                case SurtrValueTypeCode.Array:
                {
                    var element = descriptor.GetArrayElementType();
                    var array = (IList)value;
                    var result = runtime.NewArray(descriptor, array.Count);
                    for (int i = 0; i < array.Count; i++)
                        result[i] = ToSurtr(runtime, array[i], element);
                    return SurtrValue.CreateReference(result.GetSurtrReference());
                }

                default:
                    // Object or opaque: adopt it when it already is an entity, wrap it as a
                    // native proxy otherwise, and let the caller resolve later.
                    return runtime.RegisterHost(value);
            }
        }

        /// <summary>Converts a Surtr value to a CLR value of <paramref name="clrType"/>.</summary>
        public static object? ToClr(SurtrRuntime runtime, SurtrValue value, Type clrType, SurtrClassReference descriptor)
        {
            if (value.IsNullReference)
                return null;

            if (typeof(Delegate).IsAssignableFrom(clrType) && clrType != typeof(Delegate) && clrType != typeof(MulticastDelegate))
                return SurtrDelegateMarshal.ToClr(runtime, value, clrType);

            if (value.IsReference)
            {
                var code = descriptor.TypeCode;
                if (code == SurtrValueTypeCode.String)
                    return runtime.Resolve<SurtrString>(value)?.Text;

                if (code == SurtrValueTypeCode.Bytes)
                    return runtime.Resolve<SurtrBytes>(value)?.ToArray();

                // A proxy unwraps to its target; an adopted SurtrNativeObject is the host object
                // itself, and digging for a target would reach null or the wrong thing.
                var entity = runtime.Resolve<SurtrNativeObject>(value);
                if (entity is SurtrNativeProxy proxy)
                {
                    var target = proxy.Target;
                    if (target is not null)
                    {
                        if (clrType.IsInstanceOfType(target))
                            return target;

                        return Convert.ChangeType(target, clrType, System.Globalization.CultureInfo.InvariantCulture);
                    }

                    return null;
                }

                return clrType.IsInstanceOfType(entity) ? entity : null;
            }

            if (value.IsAbsent)
                return null;

            if (clrType.IsEnum)
                return Enum.ToObject(clrType, value.AsInt);

            if (clrType == typeof(string))
                return runtime.Resolve<SurtrString>(value)?.Text;

            return Convert.ChangeType(Primitive(value), clrType, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static object Primitive(SurtrValue value)
        {
            if (value.IsInt) return value.AsInt;
            if (value.IsFloat) return value.AsFloat;
            if (value.IsBool) return value.AsBool;
            if (value.IsChar) return value.AsChar;
            return value.Raw;
        }
    }
}
