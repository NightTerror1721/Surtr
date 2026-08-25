#nullable enable

using Surtr.Runtime.Objects;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>What kind of declaration a <see cref="SurtrMemberInfo"/> describes.</summary>
    public enum SurtrMemberKind : byte
    {
        /// <summary>Not a valid member.</summary>
        Invalid = 0,

        /// <summary>A field.</summary>
        Field = 1,

        /// <summary>A property.</summary>
        Property = 2,

        /// <summary>A method.</summary>
        Method = 3,

        /// <summary>A nested class.</summary>
        Class = 4,

        /// <summary>A nested enum.</summary>
        Enum = 5,

        /// <summary>A nested interface.</summary>
        Interface = 6,
    }

    /// <summary>The declaration-site variance a generic type parameter carries (§6).</summary>
    public enum SurtrGenericVariance : byte
    {
        /// <summary>The parameter accepts one exact argument. The default.</summary>
        Invariant = 0,

        /// <summary>Written <c>out T</c>: <c>C&lt;Derived&gt;</c> is a <c>C&lt;Base&gt;</c>.</summary>
        Covariant = 1,

        /// <summary>Written <c>in T</c>: <c>C&lt;Base&gt;</c> is a <c>C&lt;Derived&gt;</c>.</summary>
        Contravariant = 2,
    }

    /// <summary>How widely a member is visible.</summary>
    public enum SurtrVisibility : byte
    {
        /// <summary>Visible only inside the declaring class.</summary>
        Private = 0,

        /// <summary>Visible inside the declaring module.</summary>
        Internal = 1,

        /// <summary>Visible to the declaring class and anything deriving from it.</summary>
        Protected = 2,

        /// <summary>Visible everywhere.</summary>
        Public = 3,
    }

    /// <summary>
    /// Base metadata for anything declared inside a module or a class.
    /// </summary>
    /// <remarks>
    /// Every type this metadata mentions is held as a <see cref="SurtrTypeHandle"/>, which starts
    /// out unresolved, so a whole module's members can be built before any class exists as an
    /// object and before any of them is registered with an entity registry.
    /// </remarks>
    public abstract class SurtrMemberInfo : SurtrRuntimeEntity
    {
        private readonly string _name;
        private readonly SurtrTypeHandle? _declaringType;
        private readonly SurtrMemberKind _kind;
        private readonly SurtrVisibility _visibility;
        private readonly bool _static;
        private SurtrAttributeUsage[] _attributes = Array.Empty<SurtrAttributeUsage>();
        private SurtrBuildState _buildState;

        private protected SurtrMemberInfo(
            string name,
            SurtrMemberKind kind,
            bool isStatic,
            SurtrVisibility visibility,
            SurtrTypeHandle? declaringType)
        {
            _name = name;
            _kind = kind;
            _static = isStatic;
            _visibility = visibility;
            _declaringType = declaringType;
        }

        /// <summary>The member's declared name, without any qualification.</summary>
        public string Name
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _name;
        }

        /// <summary>Whether the member belongs to the type itself rather than to instances of it.</summary>
        public bool IsStatic
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _static;
        }

        /// <summary>How widely the member is visible.</summary>
        public SurtrVisibility Visibility
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _visibility;
        }

        /// <summary>The type that declares this member, or <see langword="null"/> for module-level members.</summary>
        public SurtrTypeHandle? DeclaringType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _declaringType;
        }

        /// <summary>
        /// What kind of declaration this metadata describes.
        /// </summary>
        /// <remarks>
        /// A field set by each subclass's constructor rather than an abstract property, so
        /// reading it is one load instead of a virtual dispatch the JIT would have to devirtualize.
        /// </remarks>
        public SurtrMemberKind Kind
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _kind;
        }

        /// <summary>
        /// The attributes written above this declaration, in source order. Empty on the
        /// overwhelming majority of members, which is why it costs one reference to a shared empty
        /// array rather than a list.
        /// </summary>
        public ReadOnlySpan<SurtrAttributeUsage> Attributes
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _attributes;
        }

        /// <summary>Records an attribute written above this declaration.</summary>
        /// <exception cref="InvalidOperationException">The member is already built.</exception>
        public void AddAttribute(SurtrAttributeUsage attribute)
        {
            ThrowIfBuilt();

            if (attribute is null)
                throw new ArgumentNullException(nameof(attribute));

            var grown = new SurtrAttributeUsage[_attributes.Length + 1];
            Array.Copy(_attributes, grown, _attributes.Length);
            grown[^1] = attribute;
            _attributes = grown;
        }

        /// <summary>
        /// Finds the first attribute of <paramref name="attributeClass"/> on this member.
        /// </summary>
        /// <returns><see langword="true"/> if the member carries one.</returns>
        public bool TryGetAttribute(SurtrClass attributeClass, out SurtrAttributeUsage attribute)
        {
            var attributes = _attributes;
            for (int i = 0; i < attributes.Length; i++)
            {
                if (ReferenceEquals(attributes[i].AttributeType.ResolvedClass, attributeClass))
                {
                    attribute = attributes[i];
                    return true;
                }
            }

            attribute = null!;
            return false;
        }

        /// <summary>How far this member is between being declared and being ready to run.</summary>
        public SurtrBuildState BuildState
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _buildState;
        }

        /// <summary>Whether this member is frozen and its runtime tables are safe to read.</summary>
        public bool IsBuilt
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _buildState == SurtrBuildState.Built;
        }

        /// <summary>Moves this member into <see cref="SurtrBuildState.Linking"/>, rejecting a cycle.</summary>
        internal void BeginLinking()
        {
            if (_buildState == SurtrBuildState.Linking)
                throw new InvalidOperationException($"Cyclic dependency detected while linking '{_name}'.");

            _buildState = SurtrBuildState.Linking;
        }

        /// <summary>Freezes this member. Every later mutation attempt throws.</summary>
        internal void MarkBuilt() => _buildState = SurtrBuildState.Built;

        /// <summary>
        /// Guards a mutation. Declaration-time APIs call this first so a member can never be
        /// changed once something has started depending on its built tables.
        /// </summary>
        /// <exception cref="InvalidOperationException">The member is already built.</exception>
        private protected void ThrowIfBuilt()
        {
            if (_buildState == SurtrBuildState.Built)
                throw new InvalidOperationException($"'{_name}' is already built and can no longer be modified.");
        }
    }
}
