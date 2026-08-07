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
