#nullable enable

using Surtr.Runtime.Objects;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// A method whose body is host code, reached through a native entry point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whether the host linked a raw function pointer or an ordinary delegate is already settled
    /// inside <see cref="SurtrNativeEntryPoint"/>, so nothing downstream of this type has to care.
    /// </para>
    /// <para>
    /// <b>The body can arrive late.</b> A native method declared with an entry point is usable the
    /// moment it exists, which is what a host building a module in one process wants. One declared
    /// with only a <see cref="LinkName"/> is not: its body is supplied when the module is loaded,
    /// by whichever runtime is loading it. That second form is what lets a module carrying native
    /// members be written to an image at all - an address means nothing in another process, and a
    /// name means the same thing everywhere. It is the shape a <c>native</c> import already had for
    /// host globals, applied to a member.
    /// </para>
    /// </remarks>
    public sealed unsafe class SurtrNativeMethodInfo : SurtrMethodInfo
    {
        /// <summary>
        /// What an unbound method points at until its body arrives.
        /// </summary>
        /// <remarks>
        /// A real address rather than a null one, so that reaching an unbound method is an
        /// exception with something to read instead of a jump through zero that takes the process
        /// with it. It costs nothing: the interpreter makes the same indirect call it was always
        /// going to make, and the alternative - testing validity on every native call - would put a
        /// branch on the hot path to catch a mistake that <c>LoadModule</c> already refuses to let
        /// through.
        /// </remarks>
        private static readonly SurtrNativeEntryPoint UnboundBody =
            SurtrNativeEntryPoint.FromFunctionPointer(&ThrowUnbound);

        private SurtrNativeEntryPoint _entryPoint;
        private readonly string? _declaredLinkName;
        private string? _linkName;
        private bool _bound;

        /// <summary>Creates native method metadata bound to an already-linked entry point.</summary>
        /// <remarks>
        /// <c>linkName</c> is the name a host supplies the body under, or <see langword="null"/> to
        /// take the derived one; it only matters if the declaring module is ever written to an
        /// image.
        /// </remarks>
        public SurtrNativeMethodInfo(
            string name,
            SurtrMethodDispatch dispatch,
            SurtrMethodRole role,
            bool isOverride,
            SurtrTypeHandle returnType,
            SurtrParameterInfo[] parameters,
            bool isStatic,
            SurtrVisibility visibility,
            SurtrTypeHandle? declaringType,
            SurtrNativeEntryPoint entryPoint,
            bool isSealed = false,
            string? linkName = null)
            : base(name, SurtrMethodImplKind.Native, dispatch, role, isOverride, returnType, parameters, isStatic, visibility, declaringType, isSealed)
        {
            if (!entryPoint.IsValid)
                throw new ArgumentException($"Native method '{name}' was given a null entry point.", nameof(entryPoint));

            _entryPoint = entryPoint;
            _declaredLinkName = linkName;
            _bound = true;
        }

        /// <summary>
        /// Creates native method metadata whose body the loading runtime will supply, by name.
        /// </summary>
        /// <remarks>
        /// The form a module read from an image carries, and the form to declare when a module is
        /// meant to be written to one. Nothing may call the method until
        /// <see cref="SurtrRuntime.LoadModule(SurtrModule)"/> has bound it, and a load that cannot
        /// find the name fails there rather than at the call. <c>linkName</c> is the name the host
        /// registers the body under.
        /// </remarks>
        public SurtrNativeMethodInfo(
            string name,
            SurtrMethodDispatch dispatch,
            SurtrMethodRole role,
            bool isOverride,
            SurtrTypeHandle returnType,
            SurtrParameterInfo[] parameters,
            bool isStatic,
            SurtrVisibility visibility,
            SurtrTypeHandle? declaringType,
            string linkName,
            bool isSealed = false)
            : base(name, SurtrMethodImplKind.Native, dispatch, role, isOverride, returnType, parameters, isStatic, visibility, declaringType, isSealed)
        {
            if (string.IsNullOrEmpty(linkName))
                throw new ArgumentException($"Native method '{name}' has no entry point, so it needs a link name to be bound by.", nameof(linkName));

            _declaredLinkName = linkName;
            _entryPoint = UnboundBody;
        }

        /// <summary>The address the interpreter calls through, valid once the body has been bound.</summary>
        public SurtrNativeEntryPoint EntryPoint
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _entryPoint;
        }

        /// <summary>Whether a body has been supplied yet.</summary>
        /// <remarks>
        /// A flag of its own rather than a test of <see cref="EntryPoint"/>, because an unbound
        /// method does have an address - the one that reports the mistake.
        /// </remarks>
        public bool IsBound
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _bound;
        }

        /// <summary>
        /// The name a host supplies this method's body under.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Derived from the declaring type and the signature when the declaration did not give one:
        /// <c>game:Facade.answer()</c>. Derived rather than required because most native methods
        /// never leave the process that built them and would only be paying for a string; and
        /// derivable at all because a member's owner and signature already name it uniquely, which
        /// is the same reason they key the access tables.
        /// </para>
        /// <para>
        /// A host that intends to ship an image should give one explicitly all the same: a derived
        /// name changes if the class is renamed, and the registration would then fail at load in a
        /// build that no longer has the source to compare against.
        /// </para>
        /// </remarks>
        public string LinkName => _linkName ??= _declaredLinkName ?? DeriveLinkName();

        /// <summary>Whether the link name was written down rather than derived from the declaration.</summary>
        public bool HasDeclaredLinkName
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _declaredLinkName is not null;
        }

        /// <summary>Supplies the body, once, for a method declared by name only.</summary>
        /// <exception cref="InvalidOperationException">A body is already bound.</exception>
        internal void BindEntryPoint(SurtrNativeEntryPoint entryPoint)
        {
            if (_bound)
                throw new InvalidOperationException($"Native method '{Name}' already has a body bound.");

            if (!entryPoint.IsValid)
                throw new ArgumentException($"Native method '{Name}' cannot be bound to a null entry point.", nameof(entryPoint));

            _entryPoint = entryPoint;
            _bound = true;
        }

        private static SurtrValue ThrowUnbound(SurtrCallArguments arguments)
            => throw new InvalidOperationException(
                "A native method was called before its body was bound. Its module was never loaded into a runtime, " +
                "or was loaded into one that had not published the body under the method's link name.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string DeriveLinkName()
        {
            var declaringType = DeclaringType;

            // A native method always belongs to a type: a module-level native is a host global,
            // which lives in a different table and binds through a different path entirely.
            if (declaringType is null || !declaringType.Reference.TryGetFullName(out string owner))
                return Name + "." + SignatureKey();

            return owner + SurtrClassReference.NameSeparator + SignatureKey();
        }

        // The entry point is a raw address plus, at most, a delegate the CLR already tracks;
        // neither is a Surtr entity, so there is nothing here for the collector to trace.
        internal override void VisitReferences(SurtrEntityMarker marker) { }
    }
}
