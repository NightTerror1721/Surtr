#nullable enable

using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// What a <see cref="SurtrTypeHandle"/> can resolve to: a class or an interface.
    /// </summary>
    /// <remarks>
    /// Both are declared inside a module or another type, so both are members. They share this
    /// base so a type reference does not have to know in advance which one it names -
    /// <see cref="SurtrMemberInfo.Kind"/> tells them apart with a field read, no cast or type
    /// check needed on a hot path.
    /// </remarks>
    public abstract class SurtrTypeInfo : SurtrMemberInfo
    {
        private readonly SurtrClassReference _selfReference;

        private protected SurtrTypeInfo(
            string name,
            SurtrMemberKind kind,
            SurtrClassReference selfReference,
            SurtrVisibility visibility,
            SurtrTypeHandle? declaringType)
            : base(name, kind, isStatic: false, visibility, declaringType)
        {
            _selfReference = selfReference;
        }

        /// <summary>The descriptor other metadata uses to refer to this type.</summary>
        public SurtrClassReference SelfReference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _selfReference;
        }

        /// <summary>Whether this type is an interface rather than a class.</summary>
        public bool IsInterface
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Kind == SurtrMemberKind.Interface;
        }
    }
}
