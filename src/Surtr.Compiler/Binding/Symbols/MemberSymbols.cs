#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>How a call to a method resolves.</summary>
    /// <remarks>
    /// Orthogonal to where the body is: a virtual method may be implemented in bytecode or by the
    /// host, and both go through the same vtable slot. Methods are non-virtual by default (§3.3) —
    /// nothing is implicitly overridable.
    /// </remarks>
    public enum MethodDispatch
    {
        /// <summary>Bound at the call site, the default.</summary>
        Direct,

        /// <summary>Resolved through the receiver's vtable.</summary>
        Virtual,

        /// <summary>Virtual with no body to run; a derived type must supply one.</summary>
        Abstract,
    }

    /// <summary>What role a method plays, beyond being an ordinary one.</summary>
    public enum MethodRole
    {
        /// <summary>An ordinary method or module-level function.</summary>
        Normal,

        /// <summary>A constructor.</summary>
        Constructor,

        /// <summary>A static initializer, run eagerly at module load.</summary>
        StaticInitializer,

        /// <summary>An operator overload (§5.6).</summary>
        Operator,

        /// <summary>A property's <c>get_x</c> accessor.</summary>
        PropertyGetter,

        /// <summary>A property's <c>set_x</c> accessor.</summary>
        PropertySetter,
    }

    /// <summary>A method, module-level function, constructor, accessor or operator.</summary>
    public sealed class MethodSymbol : Symbol
    {
        private static readonly IReadOnlyList<ParameterSymbol> NoParameters = Array.Empty<ParameterSymbol>();
        private static readonly IReadOnlyList<TypeParameterSymbol> NoTypeParameters = Array.Empty<TypeParameterSymbol>();

        private IReadOnlyList<ParameterSymbol> _parameters = NoParameters;
        private IReadOnlyList<TypeParameterSymbol> _typeParameters = NoTypeParameters;

        /// <summary>Creates a method symbol. Its signature is filled in during binding.</summary>
        public MethodSymbol(string name, Symbol containingSymbol, TypeSymbol returnType)
        {
            Name = name;
            ContainingSymbol = containingSymbol;
            ReturnType = returnType;
        }

        /// <inheritdoc/>
        public override SymbolKind Kind => SymbolKind.Method;

        /// <inheritdoc/>
        public override string Name { get; }

        /// <inheritdoc/>
        public override Symbol? ContainingSymbol { get; }

        /// <summary>The declaring type, or <see langword="null"/> for a module-level function.</summary>
        public NamedTypeSymbol? ContainingType => ContainingSymbol as NamedTypeSymbol;

        /// <summary>The declared return type; <c>void</c> when it returns nothing.</summary>
        public TypeSymbol ReturnType { get; internal set; }

        /// <summary>The parameters, in order.</summary>
        public IReadOnlyList<ParameterSymbol> Parameters
        {
            get => _parameters;
            internal set => _parameters = value;
        }

        /// <summary>The method's own type parameters, for a generic method (§6).</summary>
        public IReadOnlyList<TypeParameterSymbol> TypeParameters
        {
            get => _typeParameters;
            internal set => _typeParameters = value;
        }

        /// <summary>Whether the method takes no receiver.</summary>
        public bool IsStatic { get; internal set; }

        /// <summary>Who may call it.</summary>
        public Accessibility Accessibility { get; internal set; } = Accessibility.Private;

        /// <summary>How a call to it resolves.</summary>
        public MethodDispatch Dispatch { get; internal set; } = MethodDispatch.Direct;

        /// <summary>What role it plays beyond being an ordinary method.</summary>
        public MethodRole Role { get; internal set; } = MethodRole.Normal;

        /// <summary>Whether it replaces a base class member.</summary>
        public bool IsOverride { get; internal set; }

        /// <summary>Whether it is <c>sealed</c>, so no further override is allowed.</summary>
        public bool IsSealed { get; internal set; }

        /// <summary>Whether the body lives in the host rather than in Surtr source.</summary>
        public bool IsNative { get; internal set; }

        /// <summary>Whether the body is spliced into each call site (§3.6).</summary>
        public bool IsInline { get; internal set; }

        /// <summary>Whether inlining is mandatory rather than a hint (§3.6).</summary>
        public bool IsForceInline { get; internal set; }

        /// <summary>Whether it may be evaluated at compile time (§7.3).</summary>
        public bool IsConst { get; internal set; }

        /// <summary>
        /// Whether the compiler synthesised it — a lambda body, a property accessor, a bridge —
        /// rather than the user writing it. Its name is ABI and follows the convention fixed in
        /// <c>docs/Compiler-Plan.md</c> §5.
        /// </summary>
        public bool IsSynthetic { get; internal set; }

        /// <summary>Whether the method declares type parameters of its own.</summary>
        public bool IsGeneric => _typeParameters.Count > 0;

        /// <summary>
        /// Whether this is an <c>operator as</c> (§5.6), whose overloads differ only by the type
        /// they convert <em>to</em>.
        /// </summary>
        /// <remarks>
        /// A signature key is name plus parameters and excludes the return, so two conversions from
        /// one source type would collide. The target joins the emitted name instead, which needs a
        /// descriptor and therefore happens at emit — this flag is what tells the emitter to do it.
        /// Within the binder, overload uniqueness compares <see cref="ReturnType"/> as well.
        /// </remarks>
        public bool IsConversion { get; internal set; }

        /// <summary>
        /// The metadata this method was imported from, or <see langword="null"/> when source
        /// declared it.
        /// </summary>
        /// <remarks>
        /// The one thing an imported symbol keeps of where it came from, and it is kept because a
        /// call site has to name a real method table entry: a call that resolved to a member of an
        /// already-built module or of the standard library cannot be emitted from the symbol alone.
        /// It is metadata, not a descriptor — reading it decodes nothing, so this stays clear of the
        /// rule that only <c>MetadataImporter</c> reads the runtime's encoding.
        /// </remarks>
        public Runtime.Classes.SurtrMethodInfo? ImportedFrom { get; internal set; }

        /// <inheritdoc/>
        public override string ToDisplayString()
        {
            var builder = new StringBuilder(Name);

            if (_typeParameters.Count > 0)
            {
                builder.Append('<');
                for (int i = 0; i < _typeParameters.Count; i++)
                {
                    if (i > 0)
                        builder.Append(", ");
                    builder.Append(_typeParameters[i].Name);
                }

                builder.Append('>');
            }

            builder.Append('(');
            for (int i = 0; i < _parameters.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");
                builder.Append(_parameters[i].Type.ToDisplayString());
            }

            return builder.Append("): ").Append(ReturnType.ToDisplayString()).ToString();
        }
    }

    /// <summary>A parameter of a method or a lambda.</summary>
    public sealed class ParameterSymbol : Symbol
    {
        /// <summary>Creates a parameter symbol.</summary>
        public ParameterSymbol(string name, TypeSymbol type, int ordinal, Symbol? containingSymbol = null)
        {
            Name = name;
            Type = type;
            Ordinal = ordinal;
            ContainingSymbol = containingSymbol;
        }

        /// <inheritdoc/>
        public override SymbolKind Kind => SymbolKind.Parameter;

        /// <inheritdoc/>
        public override string Name { get; }

        /// <inheritdoc/>
        public override Symbol? ContainingSymbol { get; }

        /// <summary>The declared type.</summary>
        public TypeSymbol Type { get; internal set; }

        /// <summary>Its position in the parameter list.</summary>
        public int Ordinal { get; }

        /// <summary>Whether a default was written for it (§3.5).</summary>
        public bool HasDefaultValue { get; internal set; }

        /// <summary>
        /// What that default folds to, in the compiler's own constant vocabulary.
        /// </summary>
        /// <remarks>
        /// §3.5 requires a default to be a compile-time constant, so this is the value itself rather
        /// than an expression: a call site that omits the argument emits it as a literal, and the
        /// declaration carries it in metadata so a module compiled later can do the same.
        /// </remarks>
        public object? DefaultValue { get; internal set; }

        /// <summary>Whether it collects the remaining arguments (§3.4).</summary>
        public bool IsVararg { get; internal set; }

        /// <inheritdoc/>
        public override string ToDisplayString() => Name + ": " + Type.ToDisplayString();
    }

    /// <summary>A field: an instance slot, a static, or a module-level variable.</summary>
    public sealed class FieldSymbol : Symbol
    {
        /// <summary>Creates a field symbol.</summary>
        public FieldSymbol(string name, Symbol containingSymbol, TypeSymbol type)
        {
            Name = name;
            ContainingSymbol = containingSymbol;
            Type = type;
        }

        /// <inheritdoc/>
        public override SymbolKind Kind => SymbolKind.Field;

        /// <inheritdoc/>
        public override string Name { get; }

        /// <inheritdoc/>
        public override Symbol? ContainingSymbol { get; }

        /// <summary>The declared type.</summary>
        public TypeSymbol Type { get; internal set; }

        /// <summary>Whether the field has no receiver.</summary>
        public bool IsStatic { get; internal set; }

        /// <summary>Whether it was declared <c>let</c> rather than <c>var</c>.</summary>
        public bool IsReadOnly { get; internal set; }

        /// <summary>Who may read it.</summary>
        public Accessibility Accessibility { get; internal set; } = Accessibility.Private;

        /// <summary>Whether the compiler synthesised it, such as a property's backing field.</summary>
        public bool IsSynthetic { get; internal set; }

        /// <summary>
        /// The metadata this field was imported from, or <see langword="null"/> when source
        /// declared it.
        /// </summary>
        /// <remarks>
        /// The field counterpart of <see cref="MethodSymbol.ImportedFrom"/>, and kept for the same
        /// reason: a read of an already-built module's static — an enum case, most often — has to
        /// name a real <c>SurtrFieldInfo</c>, which the symbol alone cannot reconstruct.
        /// </remarks>
        public Runtime.Classes.SurtrFieldInfo? ImportedFrom { get; internal set; }

        /// <inheritdoc/>
        public override string ToDisplayString() => Name + ": " + Type.ToDisplayString();
    }

    /// <summary>A property, which is a name in front of a pair of accessor methods.</summary>
    public sealed class PropertySymbol : Symbol
    {
        /// <summary>Creates a property symbol.</summary>
        public PropertySymbol(string name, Symbol containingSymbol, TypeSymbol type)
        {
            Name = name;
            ContainingSymbol = containingSymbol;
            Type = type;
        }

        /// <inheritdoc/>
        public override SymbolKind Kind => SymbolKind.Property;

        /// <inheritdoc/>
        public override string Name { get; }

        /// <inheritdoc/>
        public override Symbol? ContainingSymbol { get; }

        /// <summary>The declared type.</summary>
        public TypeSymbol Type { get; internal set; }

        /// <summary>The <c>get_x</c> accessor, or <see langword="null"/> for a set-only property.</summary>
        public MethodSymbol? Getter { get; internal set; }

        /// <summary>The <c>set_x</c> accessor, or <see langword="null"/> for a read-only property.</summary>
        public MethodSymbol? Setter { get; internal set; }

        /// <summary>The backing field, when the property is auto-implemented.</summary>
        public FieldSymbol? BackingField { get; internal set; }

        /// <summary>Whether the property has no receiver.</summary>
        public bool IsStatic { get; internal set; }

        /// <summary>Who may see it.</summary>
        public Accessibility Accessibility { get; internal set; } = Accessibility.Private;

        /// <inheritdoc/>
        public override string ToDisplayString() => Name + ": " + Type.ToDisplayString();
    }

    /// <summary>A local variable or <c>let</c> binding inside a body.</summary>
    public sealed class LocalSymbol : Symbol
    {
        /// <summary>Creates a local symbol.</summary>
        public LocalSymbol(string name, TypeSymbol type, bool isReadOnly)
        {
            Name = name;
            Type = type;
            IsReadOnly = isReadOnly;
        }

        /// <inheritdoc/>
        public override SymbolKind Kind => SymbolKind.Local;

        /// <inheritdoc/>
        public override string Name { get; }

        /// <summary>The declared or inferred type.</summary>
        public TypeSymbol Type { get; internal set; }

        /// <summary>Whether it was declared <c>let</c> rather than <c>var</c>.</summary>
        public bool IsReadOnly { get; }

        /// <summary>
        /// Whether a lambda captures it. Set by flow analysis, and only ever true for a local that
        /// is never reassigned — a lambda may capture only what is effectively final, which is what
        /// lets a capture be copied into the closure rather than shared through a cell.
        /// </summary>
        public bool IsCaptured { get; internal set; }

        /// <inheritdoc/>
        public override string ToDisplayString() => Name + ": " + Type.ToDisplayString();
    }

    /// <summary>
    /// A type alias (§2.7): another name for an existing type, and deliberately <em>not</em> a
    /// <see cref="TypeSymbol"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An alias is transparent — <c>EntityId</c> and <c>int</c> are the same type everywhere, and
    /// two members differing only by an alias are a duplicate rather than an overload. Modelling
    /// it as a type would mean every <c>is NamedTypeSymbol</c> in the binder had to remember to
    /// unwrap first, and the one that forgot would silently treat two identical types as different.
    /// Resolving a type reference through an alias therefore yields the target directly, and this
    /// symbol exists only so the declaration itself can be named, checked for cycles, and reported
    /// on.
    /// </para>
    /// <para>
    /// A <c>value class</c> (§2.9) is the opposite bargain and is modelled the opposite way: it is
    /// a real <see cref="NamedTypeSymbol"/> with <see cref="TypeSymbolKind.ValueClass"/>, distinct
    /// from the type it wraps, and it erases at emit rather than at resolution.
    /// </para>
    /// </remarks>
    public sealed class AliasSymbol : Symbol
    {
        private static readonly IReadOnlyList<TypeParameterSymbol> NoTypeParameters = Array.Empty<TypeParameterSymbol>();

        private IReadOnlyList<TypeParameterSymbol> _typeParameters = NoTypeParameters;

        /// <summary>Creates an alias symbol.</summary>
        public AliasSymbol(string name, Symbol containingSymbol)
        {
            Name = name;
            ContainingSymbol = containingSymbol;
        }

        /// <inheritdoc/>
        public override SymbolKind Kind => SymbolKind.Alias;

        /// <inheritdoc/>
        public override string Name { get; }

        /// <inheritdoc/>
        public override Symbol? ContainingSymbol { get; }

        /// <summary>The type this name stands for, once the declaration has been bound.</summary>
        public TypeSymbol? Target { get; internal set; }

        /// <summary>The alias's own type parameters, substituted at each use.</summary>
        public IReadOnlyList<TypeParameterSymbol> TypeParameters
        {
            get => _typeParameters;
            internal set => _typeParameters = value;
        }

        /// <summary>Who may use it.</summary>
        public Accessibility Accessibility { get; internal set; } = Accessibility.Internal;

        /// <inheritdoc/>
        public override string ToDisplayString()
            => Target is null ? Name : Name + " = " + Target.ToDisplayString();
    }
}
