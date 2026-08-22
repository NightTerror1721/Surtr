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

                case SurtrValueTypeCode.Native:
                {
                    var enumType = value.GetType();
                    if (enumType.IsEnum && SurtrInteropState.For(runtime).TryGetEnumCache(enumType, out var cache))
                        return SurtrValue.CreateReference(cache.GetReference(value));

                    return SurtrValue.CreateReference(runtime.WrapNative(value).GetSurtrReference());
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
                    // Object or opaque: wrap as a native proxy and let the caller resolve later.
                    return SurtrValue.CreateReference(runtime.WrapNative(value).GetSurtrReference());
            }
        }

        /// <summary>Converts a Surtr value to a CLR value of <paramref name="clrType"/>.</summary>
        public static object? ToClr(SurtrRuntime runtime, SurtrValue value, Type clrType, SurtrClassReference descriptor)
        {
            if (value.IsNullReference)
                return null;

            if (value.IsReference)
            {
                var code = descriptor.TypeCode;
                if (code == SurtrValueTypeCode.String)
                    return runtime.Resolve<SurtrString>(value)?.Text;

                if (code == SurtrValueTypeCode.Native && clrType.IsEnum)
                {
                    if (SurtrInteropState.For(runtime).TryGetEnumCache(clrType, out var cache))
                        return cache.FromReference(value);
                }

                var entity = runtime.Resolve<SurtrNativeObject>(value);
                if (entity?.Target is not null)
                {
                    var target = entity.Target;
                    if (clrType.IsInstanceOfType(target))
                        return target;

                    return Convert.ChangeType(target, clrType, System.Globalization.CultureInfo.InvariantCulture);
                }

                return null;
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
