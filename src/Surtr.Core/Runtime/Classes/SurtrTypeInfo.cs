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
        private string[][] _genericConstraints = [];

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
        /// Names and arity only - the parameters are erased, so there is nothing else to keep
        /// about their identity. What they are for is letting a member's signature name one,
        /// through <see cref="SurtrClassReference.GenericParameter"/>: a descriptor names one
        /// concrete type, and without a parameter list there is no way to write "the element type
        /// of whatever this array is", which is what kept <c>push</c> and <c>get</c> from being
        /// declarable at all. What the declaration also demanded of a parameter - its bounds - is
        /// kept separately, in <see cref="GenericConstraints"/>.
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

        /// <summary>
        /// The bounds declared against each generic parameter, in declaration order - one list per
        /// parameter, each entry a descriptor naming the bound (<c>Osurtr:IComparable`1;G0</c> for
        /// <c>&lt;T : IComparable&lt;T&gt;&gt;</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The counterpart of <see cref="GenericParameters"/>: names and arity are what let a
        /// member signature mention its own parameters, and the constraints are the rest of what
        /// the compiler checked the moment a construction was validated. Nothing on an execution
        /// path reads either - slot layout sees <c>G&lt;n&gt;</c> as a reference regardless - so
        /// this exists for the compiler's <c>MetadataImporter</c>, for tooling, and for host
        /// interop: a module loaded from an image can answer what <c>Box&lt;T&gt;</c> demanded of
        /// <c>T</c> without re-compiling the declaration.
        /// </para>
        /// </remarks>
        public string[][] GenericConstraints
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _genericConstraints;
        }

        /// <summary>
        /// Declares this type's generic parameter constraints, one list per parameter in the same
        /// order <see cref="SetGenericParameters"/> declared them.
        /// </summary>
        /// <exception cref="InvalidOperationException">The type is already built.</exception>
        /// <exception cref="ArgumentException">
        /// The table's length does not match the parameter count, or an entry is null or empty.
        /// </exception>
        public void SetGenericConstraints(params string[][] constraints)
        {
            ThrowIfBuilt();

            if (constraints is null)
                throw new ArgumentNullException(nameof(constraints));

            if (constraints.Length != _genericParameters.Length)
                throw new ArgumentException(
                    $"'{Name}' declares {_genericParameters.Length} generic parameter(s), but {constraints.Length} constraint list(s) were supplied.",
                    nameof(constraints));

            for (int i = 0; i < constraints.Length; i++)
            {
                if (constraints[i] is null)
                    throw new ArgumentException($"Constraint list {i} of '{Name}' is null.", nameof(constraints));

                for (int j = 0; j < constraints[i].Length; j++)
                {
                    if (string.IsNullOrEmpty(constraints[i][j]))
                        throw new ArgumentException(
                            $"Constraint {j} of parameter {i} on '{Name}' is empty; a constraint is a descriptor.",
                            nameof(constraints));
                }
            }

            _genericConstraints = constraints;
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
