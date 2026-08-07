#nullable enable

using Surtr.Runtime.Objects;

namespace Surtr.Runtime.Classes
{
    /// <summary>Metadata for a property declared in a module or a class.</summary>
    public sealed class SurtrPropertyInfo : SurtrMemberInfo
    {
        private readonly SurtrClassReference _propertyType;
        private readonly SurtrMethodInfo? _getter;
        private readonly SurtrMethodInfo? _setter;

        /// <summary>Creates property metadata.</summary>
        public SurtrPropertyInfo(
            string name,
            SurtrClassReference propertyType,
            SurtrMethodInfo? getter,
            SurtrMethodInfo? setter,
            bool isStatic,
            SurtrVisibility visibility,
            SurtrClassReference declaringType)
            : base(name, isStatic, visibility, declaringType)
        {
            _propertyType = propertyType;
            _getter = getter;
            _setter = setter;
        }

        /// <inheritdoc/>
        public override SurtrMemberKind Kind => SurtrMemberKind.Property;

        /// <summary>The property's declared type.</summary>
        public SurtrClassReference PropertyType => _propertyType;

        /// <summary>The accessor invoked when reading the property, if it is readable.</summary>
        public SurtrMethodInfo? Getter => _getter;

        /// <summary>The accessor invoked when writing the property, if it is writable.</summary>
        public SurtrMethodInfo? Setter => _setter;

        internal override void VisitReferences(SurtrEntityMarker marker)
        {
            marker.Mark(_getter);
            marker.Mark(_setter);
        }
    }
}
