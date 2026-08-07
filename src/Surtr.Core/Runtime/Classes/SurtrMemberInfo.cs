#nullable enable

using Surtr.Runtime.Objects;

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
    /// Every type this metadata mentions is held as a <see cref="SurtrClassReference"/> rather
    /// than a resolved class, so a whole module's members can be built before any class exists
    /// as an object and before any of them is registered with an entity registry.
    /// </remarks>
    public abstract class SurtrMemberInfo : SurtrRuntimeEntity
    {
        private readonly string _name;
        private readonly bool _static;
        private readonly SurtrVisibility _visibility;
        private readonly SurtrClassReference _declaringType;

        private protected SurtrMemberInfo(
            string name,
            bool isStatic,
            SurtrVisibility visibility,
            SurtrClassReference declaringType)
        {
            _name = name;
            _static = isStatic;
            _visibility = visibility;
            _declaringType = declaringType;
        }

        /// <summary>The member's declared name, without any qualification.</summary>
        public string Name => _name;

        /// <summary>Whether the member belongs to the type itself rather than to instances of it.</summary>
        public bool IsStatic => _static;

        /// <summary>How widely the member is visible.</summary>
        public SurtrVisibility Visibility => _visibility;

        /// <summary>The type that declares this member. Module-level members carry a default (invalid) reference.</summary>
        public SurtrClassReference DeclaringType => _declaringType;

        /// <summary>What kind of declaration this metadata describes.</summary>
        public abstract SurtrMemberKind Kind { get; }
    }
}
