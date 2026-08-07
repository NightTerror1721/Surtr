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
    /// Whether the host linked a raw function pointer or an ordinary delegate is already settled
    /// inside <see cref="SurtrNativeEntryPoint"/>, so nothing downstream of this type has to care.
    /// </remarks>
    public sealed class SurtrNativeMethodInfo : SurtrMethodInfo
    {
        private readonly SurtrNativeEntryPoint _entryPoint;

        /// <summary>Creates native method metadata bound to an already-linked entry point.</summary>
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
            SurtrNativeEntryPoint entryPoint)
            : base(name, SurtrMethodImplKind.Native, dispatch, role, isOverride, returnType, parameters, isStatic, visibility, declaringType)
        {
            if (!entryPoint.IsValid)
                throw new ArgumentException($"Native method '{name}' was given a null entry point.", nameof(entryPoint));

            _entryPoint = entryPoint;
        }

        /// <summary>The address the interpreter calls through.</summary>
        public SurtrNativeEntryPoint EntryPoint
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _entryPoint;
        }

        // The entry point is a raw address plus, at most, a delegate the CLR already tracks;
        // neither is a Surtr entity, so there is nothing here for the collector to trace.
        internal override void VisitReferences(SurtrEntityMarker marker) { }
    }
}
