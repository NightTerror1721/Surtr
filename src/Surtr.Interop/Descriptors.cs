#nullable enable

using Surtr.Interop.Attributes;
using Surtr.Runtime.Classes;
using System;

namespace Surtr.Interop
{
    /// <summary>What kind of CLR declaration a native type descriptor describes.</summary>
    public enum NativeTypeKind
    {
        /// <summary>A class.</summary>
        Class = 0,

        /// <summary>A struct, boxed into a regular Surtr class.</summary>
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
