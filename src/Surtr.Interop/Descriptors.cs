#nullable enable

using Surtr.Interop.Attributes;
using Surtr.Runtime.Classes;
using System;
using System.Reflection;

namespace Surtr.Interop
{
    /// <summary>What kind of CLR declaration a native type descriptor describes.</summary>
    public enum NativeTypeKind
    {
        /// <summary>A class.</summary>
        Class = 0,

        /// <summary>
        /// A struct: boxed into a regular Surtr class by default, or laid out as an inline value
        /// type when <see cref="NativeTypeDescriptor.IsInline"/> is set.
        /// </summary>
        Struct = 1,

        /// <summary>An enum, exposed as a Surtr enum (sealed class with named cases).</summary>
        Enum = 2,

        /// <summary>A delegate, exposed as a Surtr closure type.</summary>
        Delegate = 3,
    }

    /// <summary>
    /// The intermediate model of one CLR type to expose, produced by the source generator's emitted
    /// code or by the reflection scanner, and consumed by the materializer. It carries everything the
    /// materializer needs - final names, Surtr descriptors, and the native entry points - so the
    /// materializer does not care where the descriptor came from.
    /// </summary>
    public sealed class NativeTypeDescriptor
    {
        /// <summary>The full name the type's descriptor carries: <c>Module:Name</c> or bare <c>Name</c>.</summary>
        public string FullName = string.Empty;

        /// <summary>The Surtr module path, or null for a globally-registered type.</summary>
        public string? Module;

        /// <summary>The type's Surtr name, without qualification.</summary>
        public string Name = string.Empty;

        /// <summary>Optional documentation.</summary>
        public string? Description;

        /// <summary>What kind of CLR declaration this describes.</summary>
        public NativeTypeKind Kind;

        /// <summary>The full name of the native base class, if any.</summary>
        public string? BaseType;

        /// <summary>Closed-form type arguments, as Surtr descriptors. Empty for non-generic types.</summary>
        public string[] TypeArguments = Array.Empty<string>();

        /// <summary>The exposed members, in declaration order.</summary>
        public NativeMemberDescriptor[] Members = Array.Empty<NativeMemberDescriptor>();

        /// <summary>Enum case names, in declaration order. Only for <see cref="NativeTypeKind.Enum"/>.</summary>
        public string[] EnumCases = Array.Empty<string>();

        /// <summary>
        /// The boxed CLR values backing <see cref="EnumCases"/>, in the same order. Only for
        /// <see cref="NativeTypeKind.Enum"/>; the materializer caches one object per value.
        /// </summary>
        public object[] EnumValues = Array.Empty<object>();

        /// <summary>
        /// Whether a <see cref="NativeTypeKind.Struct"/> is exposed as an inline value type rather
        /// than boxed into an ordinary class.
        /// </summary>
        /// <remarks>
        /// Set from <c>[SurtrNativeType(Inline = true)]</c>. When it is on, the type's storage is
        /// Surtr's: <see cref="NativeValueFieldDescriptor"/> entries in <see cref="Members"/> claim
        /// real slots in declaration order, and the CLR struct is rebuilt from them only when a
        /// native member needs one.
        /// </remarks>
        public bool IsInline;

        /// <summary>
        /// The CLR type this descriptor was scanned from, when it is an inline value type.
        /// </summary>
        /// <remarks>
        /// The marshaler needs the runtime <see cref="Type"/> to rebuild a struct out of slots, and
        /// a descriptor is otherwise pure data with no CLR handle in it. Null for everything the
        /// marshaler never has to reconstruct.
        /// </remarks>
        public Type? ClrType;
    }

    /// <summary>Base metadata for one exposed member.</summary>
    public abstract class NativeMemberDescriptor
    {
        /// <summary>The member's Surtr name (already adapted by the naming policy).</summary>
        public string Name = string.Empty;

        /// <summary>Optional documentation.</summary>
        public string? Description;

        /// <summary>How widely the member is visible from Surtr.</summary>
        public SurtrInteropVisibility Visibility = SurtrInteropVisibility.Public;

        /// <summary>Whether the member belongs to the type rather than to its instances.</summary>
        public bool IsStatic;
    }

    /// <summary>One exposed method or constructor.</summary>
    public sealed class NativeMethodDescriptor : NativeMemberDescriptor
    {
        /// <summary>The Surtr return descriptor, or null to derive from the CLR return type.</summary>
        public string? ReturnDescriptor;

        /// <summary>The declared parameters, in order.</summary>
        public NativeParameterDescriptor[] Parameters = Array.Empty<NativeParameterDescriptor>();

        /// <summary>Whether this is an instance constructor.</summary>
        public bool IsConstructor;

        /// <summary>Whether the method dispatches through the vtable.</summary>
        public bool IsVirtual;

        /// <summary>Whether the method overrides a virtual member of a base native class.</summary>
        public bool IsOverride;

        /// <summary>The host entry point that executes the body.</summary>
        public SurtrNativeEntryPoint EntryPoint;

        /// <summary>The link name a host binds the body under, if the type ever travels in an image.</summary>
        public string? LinkName;
    }

    /// <summary>One exposed field, backed by native getter/setter entry points.</summary>
    public sealed class NativeFieldDescriptor : NativeMemberDescriptor
    {
        /// <summary>The Surtr field type descriptor, or null to derive from the CLR field type.</summary>
        public string? TypeDescriptor;

        /// <summary>Whether writes are rejected.</summary>
        public bool ReadOnly;

        /// <summary>The host entry point that reads the field.</summary>
        public SurtrNativeEntryPoint Getter;

        /// <summary>The host entry point that writes the field.</summary>
        public SurtrNativeEntryPoint Setter;
    }

    /// <summary>
    /// One field of an inline value type: a real slot in the type's block, not an accessor pair
    /// into host code.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="NativeFieldDescriptor"/>, and the opposite trade. A native
    /// field owns no slot and reads through entry points, which is right when the CLR object is the
    /// storage. A value field <em>is</em> the storage, so reading it costs one slot access and no
    /// transition - which is the whole reason to expose a struct inline. Fields claim their slots
    /// in the order they appear in <see cref="NativeTypeDescriptor.Members"/>, and that is the
    /// order the marshaler rebuilds the CLR struct in.
    /// </remarks>
    public sealed class NativeValueFieldDescriptor : NativeMemberDescriptor
    {
        /// <summary>The Surtr field type descriptor.</summary>
        public string TypeDescriptor = string.Empty;

        /// <summary>The CLR field this slot mirrors, which the marshaler reads and writes.</summary>
        public FieldInfo? Field;
    }

    /// <summary>One exposed property, backed by native accessor entry points.</summary>
    public sealed class NativePropertyDescriptor : NativeMemberDescriptor
    {
        /// <summary>The Surtr property type descriptor, or null to derive from the CLR property type.</summary>
        public string? TypeDescriptor;

        /// <summary>Whether a public getter is exposed.</summary>
        public bool HasGetter;

        /// <summary>Whether a public setter is exposed.</summary>
        public bool HasSetter;

        /// <summary>The host entry point for the getter, when <see cref="HasGetter"/>.</summary>
        public SurtrNativeEntryPoint Getter;

        /// <summary>The host entry point for the setter, when <see cref="HasSetter"/>.</summary>
        public SurtrNativeEntryPoint Setter;
    }

    /// <summary>One declared parameter of a method or constructor.</summary>
    public sealed class NativeParameterDescriptor
    {
        /// <summary>The parameter's Surtr name.</summary>
        public string Name = string.Empty;

        /// <summary>Optional documentation.</summary>
        public string? Description;

        /// <summary>The Surtr parameter type descriptor, or null to derive from the CLR parameter type.</summary>
        public string? TypeDescriptor;

        /// <summary>Whether the parameter was declared <c>out</c> and folded into the return.</summary>
        public bool IsOut;
    }
}
