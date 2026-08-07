#nullable enable

using Surtr.Runtime.Objects;

namespace Surtr.Runtime.Classes
{
    /// <summary>Metadata for a field declared in a module or a class.</summary>
    public sealed class SurtrFieldInfo : SurtrMemberInfo
    {
        private readonly SurtrClassReference _fieldType;
        private readonly bool _readOnly;

        /// <summary>Creates field metadata.</summary>
        public SurtrFieldInfo(
            string name,
            SurtrClassReference fieldType,
            bool isStatic,
            bool isReadOnly,
            SurtrVisibility visibility,
            SurtrClassReference declaringType)
            : base(name, isStatic, visibility, declaringType)
        {
            _fieldType = fieldType;
            _readOnly = isReadOnly;
        }

        /// <inheritdoc/>
        public override SurtrMemberKind Kind => SurtrMemberKind.Field;

        /// <summary>The field's declared type.</summary>
        public SurtrClassReference FieldType => _fieldType;

        /// <summary>Whether the field can only be assigned during construction.</summary>
        public bool IsReadOnly => _readOnly;

        // Type references are descriptors, not entity handles, so a field owns no entity
        // references for the collector to trace.
        internal override void VisitReferences(SurtrEntityMarker marker) { }
    }
}
