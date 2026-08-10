#nullable enable

using System;
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
        private string[] _genericParameters = [];

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

        /// <summary>
        /// The names of this type's generic parameters, in declaration order. Empty on a type that
        /// declares none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Names and arity only - the parameters are erased, so there is nothing else to keep.
        /// What they are for is letting a member's signature name one, through
        /// <see cref="SurtrClassReference.GenericParameter"/>: a descriptor names one concrete
        /// type, and without a parameter list there is no way to write "the element type of
        /// whatever this array is", which is what kept <c>push</c> and <c>get</c> from being
        /// declarable at all.
        /// </para>
        /// <para>
        /// A compiler substitutes the receiver's actual type arguments at each use site and checks
        /// against those; the runtime never does, because by then every slot is a reference and
        /// the answer would change nothing.
        /// </para>
        /// </remarks>
        public ReadOnlySpan<string> GenericParameters
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _genericParameters;
        }

        /// <summary>How many generic parameters this type declares.</summary>
        public int GenericParameterCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _genericParameters.Length;
        }

        /// <summary>Declares this type's generic parameters by name, in order.</summary>
        /// <exception cref="InvalidOperationException">The type is already built.</exception>
        public void SetGenericParameters(params string[] names)
        {
            ThrowIfBuilt();

            if (names is null)
                throw new ArgumentNullException(nameof(names));

            if (names.Length > 10)
                throw new ArgumentException(
                    $"'{Name}' declares {names.Length} generic parameters; a descriptor can name at most ten.",
                    nameof(names));

            _genericParameters = names;
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
