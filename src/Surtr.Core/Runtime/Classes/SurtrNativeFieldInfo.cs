#nullable enable

using Surtr.Runtime.Objects;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// A field whose value lives in the host, reached through native getter and setter entry
    /// points rather than stored in a Surtr slot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A native field owns no slot in its declaring class's instance layout and no entry in static
    /// storage: the interpreter's <c>FieldGet</c>/<c>FieldSet</c> (and their static counterparts)
    /// recognize it and route the read or write through the host entry points instead. The getter
    /// receives the receiver as argument 0 and returns the value; the setter receives the receiver
    /// and the value. A static native field's entry points receive no receiver - the host reaches
    /// its own static. This is the bridge's counterpart of a host field, exposed as a Surtr field
    /// rather than lowered to an accessor pair.
    /// </para>
    /// <para>
    /// <c>ReadOnly</c> fields carry a throwing setter, so a write that the compiler should never
    /// emit still fails loudly instead of silently corrupting nothing.
    /// </para>
    /// </remarks>
    public sealed unsafe class SurtrNativeFieldInfo : SurtrFieldInfo
    {
        private static readonly SurtrNativeEntryPoint ReadOnlySetter =
            SurtrNativeEntryPoint.FromFunctionPointer(&ThrowReadOnly);

        private readonly SurtrNativeEntryPoint _getter;
        private readonly SurtrNativeEntryPoint _setter;

        /// <summary>Creates a native field with getter and setter entry points.</summary>
        /// <param name="name">The field's declared Surtr name.</param>
        /// <param name="fieldType">The field's declared Surtr type.</param>
        /// <param name="isStatic">Whether the field belongs to the type rather than to its instances.</param>
        /// <param name="isReadOnly">Whether writes are rejected; a read-only field ignores <paramref name="setter"/>.</param>
        /// <param name="visibility">How widely the field is visible.</param>
        /// <param name="declaringType">The type that declares the field.</param>
        /// <param name="getter">The host entry point that reads the value.</param>
        /// <param name="setter">The host entry point that writes the value.</param>
        /// <exception cref="ArgumentException"><paramref name="getter"/> is a null entry point.</exception>
        public SurtrNativeFieldInfo(
            string name,
            SurtrTypeHandle fieldType,
            bool isStatic,
            bool isReadOnly,
            SurtrVisibility visibility,
            SurtrTypeHandle? declaringType,
            SurtrNativeEntryPoint getter,
            SurtrNativeEntryPoint setter)
            : base(name, fieldType, isStatic, isReadOnly, visibility, declaringType)
        {
            if (!getter.IsValid)
                throw new ArgumentException($"Native field '{name}' was given a null getter entry point.", nameof(getter));

            _getter = getter;
            _setter = isReadOnly ? ReadOnlySetter : setter;
        }

        /// <summary>The host entry point that reads the field.</summary>
        public SurtrNativeEntryPoint Getter
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _getter;
        }

        /// <summary>The host entry point that writes the field, or a throwing stub when read-only.</summary>
        public SurtrNativeEntryPoint Setter
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _setter;
        }

        private static int ThrowReadOnly(SurtrCallArguments arguments)
            => throw new InvalidOperationException("A read-only native field cannot be written to.");

        // Entry points hold at most a delegate the CLR already tracks; no Surtr entity to trace.
        internal override void VisitReferences(SurtrEntityMarker marker) { }
    }
}
