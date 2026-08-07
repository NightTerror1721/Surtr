#nullable enable

using Surtr.Runtime.Objects;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>Metadata for a field declared in a module or a class.</summary>
    public sealed class SurtrFieldInfo : SurtrMemberInfo
    {
        private readonly SurtrTypeHandle _fieldType;
        private readonly bool _readOnly;

        /// <summary>
        /// This field's index within its declaring type's instance layout, or within that type's
        /// static storage when the field is static. Assigned by the linker, which is the only
        /// thing that knows how many slots the base class already claimed.
        /// </summary>
        internal int SlotIndex = -1;

        /// <summary>Creates field metadata.</summary>
        public SurtrFieldInfo(
            string name,
            SurtrTypeHandle fieldType,
            bool isStatic,
            bool isReadOnly,
            SurtrVisibility visibility,
            SurtrTypeHandle? declaringType)
            : base(name, SurtrMemberKind.Field, isStatic, visibility, declaringType)
        {
            _fieldType = fieldType;
            _readOnly = isReadOnly;
        }

        /// <summary>The field's declared type.</summary>
        public SurtrTypeHandle FieldType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _fieldType;
        }

        /// <summary>Whether the field can only be assigned during construction.</summary>
        public bool IsReadOnly
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _readOnly;
        }

        /// <summary>
        /// The field's index within its instance layout, or within the declaring type's static
        /// storage when <see cref="SurtrMemberInfo.IsStatic"/> is set. Lets the interpreter reach
        /// a field by offset instead of by name. <c>-1</c> until the declaring type is linked.
        /// </summary>
        public int Slot
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => SlotIndex;
        }

        // Type references are handles owned by the module's table, so a field holds no entity
        // references of its own for the collector to trace.
        internal override void VisitReferences(SurtrEntityMarker marker) { }
    }
}
