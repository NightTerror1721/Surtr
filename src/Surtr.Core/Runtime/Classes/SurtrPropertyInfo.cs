#nullable enable

using Surtr.Runtime.Objects;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// Metadata for a property declared in a module or a class.
    /// </summary>
    /// <remarks>
    /// The accessors are plain <see cref="SurtrMethodInfo"/>, so a property can mix
    /// implementations freely - a bytecode getter alongside a native setter is just two
    /// different subclasses in the same pair of fields.
    /// </remarks>
    public sealed class SurtrPropertyInfo : SurtrMemberInfo
    {
        private readonly SurtrTypeHandle _propertyType;
        private readonly SurtrMethodInfo? _getter;
        private readonly SurtrMethodInfo? _setter;

        /// <summary>Creates property metadata.</summary>
        public SurtrPropertyInfo(
            string name,
            SurtrTypeHandle propertyType,
            SurtrMethodInfo? getter,
            SurtrMethodInfo? setter,
            bool isStatic,
            SurtrVisibility visibility,
            SurtrTypeHandle? declaringType)
            : base(name, SurtrMemberKind.Property, isStatic, visibility, declaringType)
        {
            _propertyType = propertyType;
            _getter = getter;
            _setter = setter;
        }

        /// <summary>The property's declared type.</summary>
        public SurtrTypeHandle PropertyType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _propertyType;
        }

        /// <summary>The accessor invoked when reading the property, if it is readable.</summary>
        public SurtrMethodInfo? Getter
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _getter;
        }

        /// <summary>The accessor invoked when writing the property, if it is writable.</summary>
        public SurtrMethodInfo? Setter
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _setter;
        }

        internal override void VisitReferences(SurtrEntityMarker marker)
        {
            marker.Mark(_getter);
            marker.Mark(_setter);
        }
    }
}
