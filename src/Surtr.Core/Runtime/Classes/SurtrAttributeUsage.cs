#nullable enable

using Surtr.Runtime.Objects;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// One <c>@Name(args)</c> written above a declaration: which attribute class it names, the
    /// constant arguments it was given, and - once its module is loaded - the instance built from
    /// them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An attribute is a real class rather than a bag of name/value pairs, so an attribute's own
    /// declaration is where its shape is stated and the type checker can hold a use site to it.
    /// The cost is that a usage is a constructor call rather than a record, and that call has to
    /// happen somewhere: it happens at module load, with the module's other statics, which is the
    /// only point where every class the attribute might name is already linked.
    /// </para>
    /// <para>
    /// The arguments stay <see cref="SurtrConstant"/>s rather than becoming values up front,
    /// because metadata is built before any runtime exists and outlives any one of them - the same
    /// reason a parameter's default value is held that way.
    /// </para>
    /// </remarks>
    public sealed class SurtrAttributeUsage
    {
        private readonly SurtrTypeHandle _attributeType;
        private readonly SurtrConstant[] _arguments;

        /// <summary>Records an attribute written above a declaration.</summary>
        public SurtrAttributeUsage(SurtrTypeHandle attributeType, params SurtrConstant[] arguments)
        {
            _attributeType = attributeType ?? throw new ArgumentNullException(nameof(attributeType));
            _arguments = arguments ?? Array.Empty<SurtrConstant>();
        }

        /// <summary>The attribute class this usage names.</summary>
        public SurtrTypeHandle AttributeType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _attributeType;
        }

        /// <summary>The constant arguments, in the order they were written.</summary>
        public ReadOnlySpan<SurtrConstant> Arguments
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _arguments;
        }

        /// <summary>
        /// The instance built when the declaring module was loaded, or
        /// <see cref="SurtrValue.NullRef"/> before that.
        /// </summary>
        /// <remarks>
        /// A single reference per usage, not per read: an attribute is metadata, so every host that
        /// asks for it gets the same object. It is rooted permanently for the runtime's life, which
        /// is the lifetime metadata wants.
        /// </remarks>
        public SurtrRef Instance { get; internal set; }
    }
}
