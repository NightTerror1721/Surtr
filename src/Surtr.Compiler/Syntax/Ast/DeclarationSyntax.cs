#nullable enable

using System.Collections.Generic;

namespace Surtr.Compiler.Syntax.Ast
{
    /// <summary>Visibility, per §3.1. <see cref="Default"/> means none was written.</summary>
    public enum Visibility
    {
        /// <summary>Nothing was written, so the declaration takes its context's default — <c>private</c> for a member, <c>internal</c> at module level.</summary>
        Default,

        /// <summary><c>private</c></summary>
        Private,

        /// <summary><c>protected</c></summary>
        Protected,

        /// <summary><c>internal</c> — module-scoped.</summary>
        Internal,

        /// <summary><c>public</c></summary>
        Public,
    }

    /// <summary>How a method dispatches, per §3.3. Maps onto the runtime's <c>SurtrMethodDispatch</c>.</summary>
    public enum DispatchModifier
    {
        /// <summary>No modifier: a non-virtual method, which is the default.</summary>
        None,

        /// <summary><c>virtual</c> — gets a vtable slot.</summary>
        Virtual,

        /// <summary><c>override</c> — replaces an inherited virtual member.</summary>
        Override,

        /// <summary><c>abstract</c> — declared with no body.</summary>
        Abstract,
    }

    /// <summary>An inlining request, per §3.6.</summary>
    public enum InlineModifier
    {
        /// <summary>Nothing was written; the compiler decides.</summary>
        None,

        /// <summary><c>inline</c> — a hint the compiler may decline.</summary>
        Inline,

        /// <summary><c>forceinline</c> — mandatory; an impossible case is a compile error.</summary>
        ForceInline,
    }

    /// <summary>Which kind of type a <see cref="TypeDeclarationSyntax"/> declares.</summary>
    /// <remarks>
    /// One node covers all five because they share a shape — modifiers, name, type parameters, base
    /// list, members — and differ only in the rules a later pass enforces. Five near-identical node
    /// types would carry no state of their own.
    /// </remarks>
    public enum TypeDeclarationKind
    {
        /// <summary><c>class</c> (§2.2).</summary>
        Class,

        /// <summary><c>value class</c> — one field, erased to it at runtime (§2.9).</summary>
        ValueClass,

        /// <summary><c>interface</c> (§2.3).</summary>
        Interface,

        /// <summary><c>enum</c> (§2.4).</summary>
        Enum,

        /// <summary><c>singleton</c> (§2.8).</summary>
        Singleton,
    }

    /// <summary>Base of every declaration.</summary>
    public abstract class DeclarationSyntax : SyntaxNode
    {
        /// <summary>The attributes attached to this declaration (§11).</summary>
        public IReadOnlyList<AttributeSyntax> Attributes { get; }

        /// <summary>The <c>///</c> doc comment lines preceding this declaration, in order (§12).</summary>
        public IReadOnlyList<string> DocComment { get; }

        /// <summary>The declared visibility, or <see cref="Visibility.Default"/>.</summary>
        public Visibility Visibility { get; }

        /// <summary>Initializes a declaration.</summary>
        /// <param name="span">The source the declaration covers.</param>
        /// <param name="attributes">The attributes attached to it.</param>
        /// <param name="docComment">The doc comment lines preceding it.</param>
        /// <param name="visibility">The declared visibility.</param>
        protected DeclarationSyntax(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility)
            : base(span)
        {
            Attributes = attributes;
            DocComment = docComment;
            Visibility = visibility;
        }
    }

    /// <summary>An attribute, <c>@Name(args)</c> (§11).</summary>
    public sealed class AttributeSyntax : SyntaxNode
    {
        /// <summary>The attribute's name.</summary>
        public string Name { get; }

        /// <summary>Its arguments, empty when written without parentheses.</summary>
        public IReadOnlyList<ExpressionSyntax> Arguments { get; }

        /// <summary>Initializes an attribute.</summary>
        /// <param name="span">The source the attribute covers.</param>
        /// <param name="name">The attribute's name.</param>
        /// <param name="arguments">Its arguments.</param>
        public AttributeSyntax(SourceSpan span, string name, IReadOnlyList<ExpressionSyntax> arguments) : base(span)
        {
            Name = name;
            Arguments = arguments;
        }
    }

    /// <summary>One parameter of a method, constructor, operator or lambda.</summary>
    public sealed class ParameterSyntax : SyntaxNode
    {
        /// <summary>The parameter's name.</summary>
        public string Name { get; }

        /// <summary>Its declared type, or <c>null</c> on a lambda parameter whose type comes from the target type (§5.9).</summary>
        public TypeSyntax? Type { get; }

        /// <summary>Its default value, or <c>null</c>. Defaults are trailing-only (§3.5).</summary>
        public ExpressionSyntax? DefaultValue { get; }

        /// <summary>True when declared with a trailing <c>...</c>. At most one, always last (§3.5).</summary>
        public bool IsVarargs { get; }

        /// <summary>Initializes a parameter.</summary>
        /// <param name="span">The source the parameter covers.</param>
        /// <param name="name">The parameter's name.</param>
        /// <param name="type">Its declared type, or <c>null</c>.</param>
        /// <param name="defaultValue">Its default value, or <c>null</c>.</param>
        /// <param name="isVarargs">True when declared with a trailing <c>...</c>.</param>
        public ParameterSyntax(SourceSpan span, string name, TypeSyntax? type, ExpressionSyntax? defaultValue, bool isVarargs)
            : base(span)
        {
            Name = name;
            Type = type;
            DefaultValue = defaultValue;
            IsVarargs = isVarargs;
        }
    }

    /// <summary>One generic type parameter with its inline constraints: <c>T : IComparable&lt;T&gt; &amp; IEquatable&lt;T&gt;</c> (§6).</summary>
    public sealed class TypeParameterSyntax : SyntaxNode
    {
        /// <summary>The type parameter's name.</summary>
        public string Name { get; }

        /// <summary>Its constraints, combined with <c>&amp;</c>. Empty when unconstrained.</summary>
        public IReadOnlyList<TypeSyntax> Constraints { get; }

        /// <summary>Initializes a type parameter.</summary>
        /// <param name="span">The source the parameter covers.</param>
        /// <param name="name">Its name.</param>
        /// <param name="constraints">Its constraints, or an empty list.</param>
        public TypeParameterSyntax(SourceSpan span, string name, IReadOnlyList<TypeSyntax> constraints) : base(span)
        {
            Name = name;
            Constraints = constraints;
        }
    }

    /// <summary>A whole source file: its imports and the declarations it contributes to its module (§2.1).</summary>
    /// <remarks>
    /// There is no module header — a file's module comes from its path — so this node carries no
    /// module name. Establishing that is the driver's job, not the parser's.
    /// </remarks>
    public sealed class CompilationUnitSyntax : SyntaxNode
    {
        /// <summary>The <c>import</c> statements at the top of the file.</summary>
        public IReadOnlyList<ImportSyntax> Imports { get; }

        /// <summary>The declarations in the file, in order.</summary>
        public IReadOnlyList<DeclarationSyntax> Declarations { get; }

        /// <summary>Initializes a compilation unit.</summary>
        /// <param name="span">The source the file covers.</param>
        /// <param name="imports">The imports.</param>
        /// <param name="declarations">The declarations.</param>
        public CompilationUnitSyntax(SourceSpan span, IReadOnlyList<ImportSyntax> imports, IReadOnlyList<DeclarationSyntax> declarations)
            : base(span)
        {
            Imports = imports;
            Declarations = declarations;
        }
    }

    /// <summary>An <c>import</c>, either of one name or of a whole module with <c>.*</c> (§2.1).</summary>
    public sealed class ImportSyntax : SyntaxNode
    {
        /// <summary>The dotted path's segments. For a wildcard import this is the module path alone.</summary>
        public IReadOnlyList<string> Path { get; }

        /// <summary>True when written with a trailing <c>.*</c>.</summary>
        public bool IsWildcard { get; }

        /// <summary>The name after <c>as</c>, or <see langword="null"/> when the import is unaliased.</summary>
        public string? Alias { get; }

        /// <summary>Initializes an import.</summary>
        /// <param name="span">The source the import covers.</param>
        /// <param name="path">The dotted path's segments.</param>
        /// <param name="isWildcard">True when written with a trailing <c>.*</c>.</param>
        /// <param name="alias">The name after <c>as</c>, if any.</param>
        public ImportSyntax(SourceSpan span, IReadOnlyList<string> path, bool isWildcard, string? alias = null) : base(span)
        {
            Path = path;
            IsWildcard = isWildcard;
            Alias = alias;
        }
    }

    /// <summary>A type alias, <c>alias IntMap&lt;V&gt; = {int: V};</c> (§2.7).</summary>
    public sealed class AliasDeclarationSyntax : DeclarationSyntax
    {
        /// <summary>The alias's name.</summary>
        public string Name { get; }

        /// <summary>Its type parameters, empty when not generic.</summary>
        public IReadOnlyList<TypeParameterSyntax> TypeParameters { get; }

        /// <summary>The type it names.</summary>
        public TypeSyntax Target { get; }

        /// <summary>Initializes an alias declaration.</summary>
        /// <param name="span">The source the declaration covers.</param>
        /// <param name="attributes">Attributes attached to it.</param>
        /// <param name="docComment">Doc comment lines preceding it.</param>
        /// <param name="visibility">Its declared visibility.</param>
        /// <param name="name">The alias's name.</param>
        /// <param name="typeParameters">Its type parameters.</param>
        /// <param name="target">The type it names.</param>
        public AliasDeclarationSyntax(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility,
            string name, IReadOnlyList<TypeParameterSyntax> typeParameters, TypeSyntax target)
            : base(span, attributes, docComment, visibility)
        {
            Name = name;
            TypeParameters = typeParameters;
            Target = target;
        }
    }

    /// <summary>A class, value class, interface, enum or singleton. <see cref="Kind"/> says which.</summary>
    public sealed class TypeDeclarationSyntax : DeclarationSyntax
    {
        /// <summary>Which kind of type this declares.</summary>
        public TypeDeclarationKind Kind { get; }

        /// <summary>The type's name.</summary>
        public string Name { get; }

        /// <summary>Its type parameters, empty when not generic.</summary>
        public IReadOnlyList<TypeParameterSyntax> TypeParameters { get; }

        /// <summary>
        /// The <c>:</c> list — the base class and any interfaces, undistinguished. §2.2 leaves
        /// telling them apart to the binder, since only metadata says which a name resolves to.
        /// </summary>
        public IReadOnlyList<TypeSyntax> BaseTypes { get; }

        /// <summary>The enum's cases, empty for every other kind.</summary>
        public IReadOnlyList<EnumCaseSyntax> EnumCases { get; }

        /// <summary>The members declared in the body.</summary>
        public IReadOnlyList<DeclarationSyntax> Members { get; }

        /// <summary>True when declared <c>abstract</c> (§3.3).</summary>
        public bool IsAbstract { get; }

        /// <summary>True when declared <c>sealed</c> (§2.2).</summary>
        public bool IsSealed { get; }

        /// <summary>True when declared <c>static</c> — legal only on a nested type.</summary>
        public bool IsStatic { get; }

        /// <summary>
        /// True when declared with the <c>attribute</c> keyword (§11) instead of a bare <c>class</c>
        /// — implicitly extends <c>Attribute</c>, so an explicit <c>: Attribute</c> is not needed
        /// (though extending it, or something that does, explicitly is still accepted).
        /// </summary>
        public bool IsAttribute { get; }

        /// <summary>
        /// The declaration kinds an <c>attribute</c> class may be written on, or
        /// <see cref="Ast.SurtrAttributeTargets.None"/> for no restriction — either because nothing was
        /// written in parentheses, or because the parenthesized list named a retention only.
        /// </summary>
        public SurtrAttributeTargets SurtrAttributeTargets { get; }

        /// <summary>
        /// True when the parenthesized list after <c>attribute</c> named <c>CompileTimeOnly</c> —
        /// the attribute is checked and then discarded, never reaching the compiled image. False
        /// (the default) means <c>Runtime</c>: it is emitted and readable through host reflection.
        /// </summary>
        public bool IsCompileTimeOnlyAttribute { get; }

        /// <summary>Initializes a type declaration.</summary>
        /// <param name="span">The source the declaration covers.</param>
        /// <param name="attributes">Attributes attached to it.</param>
        /// <param name="docComment">Doc comment lines preceding it.</param>
        /// <param name="visibility">Its declared visibility.</param>
        /// <param name="kind">Which kind of type this declares.</param>
        /// <param name="name">The type's name.</param>
        /// <param name="typeParameters">Its type parameters.</param>
        /// <param name="baseTypes">The <c>:</c> list.</param>
        /// <param name="enumCases">The enum's cases, or an empty list.</param>
        /// <param name="members">The members declared in the body.</param>
        /// <param name="isAbstract">True when declared <c>abstract</c>.</param>
        /// <param name="isSealed">True when declared <c>sealed</c>.</param>
        /// <param name="isStatic">True when declared <c>static</c>.</param>
        /// <param name="isAttribute">True when declared with the <c>attribute</c> keyword.</param>
        /// <param name="attributeTargets">The declaration kinds it may be written on, or none for unrestricted.</param>
        /// <param name="isCompileTimeOnlyAttribute">True when its retention is <c>CompileTimeOnly</c>.</param>
        public TypeDeclarationSyntax(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility,
            TypeDeclarationKind kind, string name, IReadOnlyList<TypeParameterSyntax> typeParameters, IReadOnlyList<TypeSyntax> baseTypes,
            IReadOnlyList<EnumCaseSyntax> enumCases, IReadOnlyList<DeclarationSyntax> members, bool isAbstract, bool isSealed, bool isStatic,
            bool isAttribute = false, SurtrAttributeTargets attributeTargets = SurtrAttributeTargets.None, bool isCompileTimeOnlyAttribute = false)
            : base(span, attributes, docComment, visibility)
        {
            Kind = kind;
            Name = name;
            TypeParameters = typeParameters;
            BaseTypes = baseTypes;
            EnumCases = enumCases;
            Members = members;
            IsAbstract = isAbstract;
            IsSealed = isSealed;
            IsStatic = isStatic;
            IsAttribute = isAttribute;
            SurtrAttributeTargets = attributeTargets;
            IsCompileTimeOnlyAttribute = isCompileTimeOnlyAttribute;
        }
    }

    /// <summary>
    /// The declaration kinds an <c>attribute</c> class (§11) may be written on. <see cref="None"/>
    /// means no restriction was declared, not "nothing is allowed" — an attribute with no target
    /// list may be written anywhere any attribute could be written before this existed.
    /// </summary>
    [System.Flags]
    public enum SurtrAttributeTargets
    {
        /// <summary>No restriction declared.</summary>
        None = 0,

        /// <summary><c>class</c>, <c>value class</c> or <c>singleton</c>.</summary>
        Class = 1 << 0,

        /// <summary><c>interface</c>.</summary>
        Interface = 1 << 1,

        /// <summary><c>enum</c>.</summary>
        Enum = 1 << 2,

        /// <summary>A field (<c>let</c>/<c>var</c>), instance or module-level.</summary>
        Field = 1 << 3,

        /// <summary>A property, instance or module-level.</summary>
        Property = 1 << 4,

        /// <summary>A method, constructor, or module-level function.</summary>
        Method = 1 << 5,
    }

    /// <summary>One case of an enum: a name plus the arguments to the enum's constructor (§2.4).</summary>
    public sealed class EnumCaseSyntax : SyntaxNode
    {
        /// <summary>The case's name.</summary>
        public string Name { get; }

        /// <summary>The constructor arguments, empty when written bare.</summary>
        public IReadOnlyList<ArgumentSyntax> Arguments { get; }

        /// <summary>The <c>///</c> doc comment lines preceding this case.</summary>
        public IReadOnlyList<string> DocComment { get; }

        /// <summary>Initializes an enum case.</summary>
        /// <param name="span">The source the case covers.</param>
        /// <param name="name">The case's name.</param>
        /// <param name="arguments">The constructor arguments.</param>
        /// <param name="docComment">Doc comment lines preceding it.</param>
        public EnumCaseSyntax(SourceSpan span, string name, IReadOnlyList<ArgumentSyntax> arguments, IReadOnlyList<string> docComment)
            : base(span)
        {
            Name = name;
            Arguments = arguments;
            DocComment = docComment;
        }
    }

    /// <summary>A field, or a module-level variable: <c>let</c>, <c>var</c>, or <c>const</c> (§3.2, §2.5, §7.1).</summary>
    public sealed class FieldDeclarationSyntax : DeclarationSyntax
    {
        /// <summary>The field's name.</summary>
        public string Name { get; }

        /// <summary>Its declared type, or <c>null</c> when inferred from the initializer.</summary>
        public TypeSyntax? Type { get; }

        /// <summary>Its initializer, or <c>null</c>.</summary>
        public ExpressionSyntax? Initializer { get; }

        /// <summary>True for <c>var</c>, false for <c>let</c> and <c>const</c>.</summary>
        public bool IsMutable { get; }

        /// <summary>True for <c>const</c> — a compile-time value, implicitly static (§7.1).</summary>
        public bool IsConst { get; }

        /// <summary>True when declared <c>static</c>.</summary>
        public bool IsStatic { get; }

        /// <summary>True when declared <c>native</c> — a host-provided global with no initializer (§10).</summary>
        public bool IsNative { get; }

        /// <summary>Initializes a field declaration.</summary>
        /// <param name="span">The source the declaration covers.</param>
        /// <param name="attributes">Attributes attached to it.</param>
        /// <param name="docComment">Doc comment lines preceding it.</param>
        /// <param name="visibility">Its declared visibility.</param>
        /// <param name="name">The field's name.</param>
        /// <param name="type">Its declared type, or <c>null</c>.</param>
        /// <param name="initializer">Its initializer, or <c>null</c>.</param>
        /// <param name="isMutable">True for <c>var</c>.</param>
        /// <param name="isConst">True for <c>const</c>.</param>
        /// <param name="isStatic">True when declared <c>static</c>.</param>
        /// <param name="isNative">True when declared <c>native</c>.</param>
        public FieldDeclarationSyntax(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility,
            string name, TypeSyntax? type, ExpressionSyntax? initializer, bool isMutable, bool isConst, bool isStatic, bool isNative)
            : base(span, attributes, docComment, visibility)
        {
            Name = name;
            Type = type;
            Initializer = initializer;
            IsMutable = isMutable;
            IsConst = isConst;
            IsStatic = isStatic;
            IsNative = isNative;
        }
    }

    /// <summary>One accessor of a property. A <c>null</c> <see cref="Body"/> means the auto-generated form (§3.4).</summary>
    public sealed class AccessorSyntax : SyntaxNode
    {
        /// <summary>True for <c>get</c>, false for <c>set</c>.</summary>
        public bool IsGetter { get; }

        /// <summary>The accessor's body, or <c>null</c> when it was written bare and the compiler generates it.</summary>
        public BlockStatementSyntax? Body { get; }

        /// <summary>
        /// Its own visibility, written directly on this accessor (e.g. <c>private set</c>) rather than
        /// on the property. <see cref="Visibility.Default"/> means none was written, so it inherits
        /// the property's (§3.2, §3.4) — an accessor's own visibility, when written, must be strictly
        /// narrower than the property's.
        /// </summary>
        public Visibility Visibility { get; }

        /// <summary>
        /// Whether this accessor wrote any of its own <c>virtual</c>/<c>override</c>/<c>abstract</c>/
        /// <c>sealed</c> — when false, <see cref="Dispatch"/> and <see cref="IsSealed"/> are
        /// meaningless and the accessor inherits the property's dispatch and sealed-ness as a pair.
        /// </summary>
        public bool HasOwnDispatch { get; }

        /// <summary>This accessor's own dispatch, meaningful only when <see cref="HasOwnDispatch"/>.</summary>
        public DispatchModifier Dispatch { get; }

        /// <summary>Whether this accessor's own <c>override</c> was also <c>sealed</c>.</summary>
        public bool IsSealed { get; }

        /// <summary>
        /// Its own <c>inline</c>/<c>forceinline</c> hint. <see cref="InlineModifier.None"/> means none
        /// was written, so it inherits the property's (§3.6).
        /// </summary>
        public InlineModifier Inline { get; }

        /// <summary>Initializes an accessor.</summary>
        /// <param name="span">The source the accessor covers.</param>
        /// <param name="isGetter">True for <c>get</c>.</param>
        /// <param name="body">Its body, or <c>null</c>.</param>
        /// <param name="visibility">Its own visibility, or <see cref="Visibility.Default"/> to inherit the property's.</param>
        /// <param name="hasOwnDispatch">Whether this accessor wrote its own dispatch/sealed modifiers.</param>
        /// <param name="dispatch">Its own dispatch, meaningful only when <paramref name="hasOwnDispatch"/>.</param>
        /// <param name="isSealed">Whether its own <c>override</c> was also <c>sealed</c>.</param>
        /// <param name="inline">Its own inline hint, or <see cref="InlineModifier.None"/> to inherit the property's.</param>
        public AccessorSyntax(
            SourceSpan span,
            bool isGetter,
            BlockStatementSyntax? body,
            Visibility visibility = Visibility.Default,
            bool hasOwnDispatch = false,
            DispatchModifier dispatch = DispatchModifier.None,
            bool isSealed = false,
            InlineModifier inline = InlineModifier.None)
            : base(span)
        {
            IsGetter = isGetter;
            Body = body;
            Visibility = visibility;
            HasOwnDispatch = hasOwnDispatch;
            Dispatch = dispatch;
            IsSealed = isSealed;
            Inline = inline;
        }
    }

    /// <summary>A property (§3.4). Recognised by having no introducer keyword before its name (§3.2).</summary>
    public sealed class PropertyDeclarationSyntax : DeclarationSyntax
    {
        /// <summary>The property's name.</summary>
        public string Name { get; }

        /// <summary>Its type.</summary>
        public TypeSyntax Type { get; }

        /// <summary>Its accessors, in source order.</summary>
        public IReadOnlyList<AccessorSyntax> Accessors { get; }

        /// <summary>True when declared <c>static</c>.</summary>
        public bool IsStatic { get; }

        /// <summary>How it dispatches (§3.3).</summary>
        public DispatchModifier Dispatch { get; }

        /// <summary>True when an <c>override</c> was also declared <c>sealed</c> (§3.3).</summary>
        public bool IsSealed { get; }

        /// <summary>The <c>inline</c>/<c>forceinline</c> hint (§3.6), applied to its accessors.</summary>
        public InlineModifier Inline { get; }

        /// <summary>True when declared <c>native</c> — an accessor published by link name, not a body.</summary>
        public bool IsNative { get; }

        /// <summary>Initializes a property declaration.</summary>
        /// <param name="span">The source the declaration covers.</param>
        /// <param name="attributes">Attributes attached to it.</param>
        /// <param name="docComment">Doc comment lines preceding it.</param>
        /// <param name="visibility">Its declared visibility.</param>
        /// <param name="name">The property's name.</param>
        /// <param name="type">Its type.</param>
        /// <param name="accessors">Its accessors.</param>
        /// <param name="isStatic">True when declared <c>static</c>.</param>
        /// <param name="dispatch">How it dispatches.</param>
        /// <param name="isSealed">True when the override was also declared <c>sealed</c>.</param>
        /// <param name="inline">The inline hint to apply to its accessors.</param>
        /// <param name="isNative">True when its accessors are published by link name.</param>
        public PropertyDeclarationSyntax(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility,
            string name, TypeSyntax type, IReadOnlyList<AccessorSyntax> accessors, bool isStatic, DispatchModifier dispatch, bool isSealed,
            InlineModifier inline, bool isNative)
            : base(span, attributes, docComment, visibility)
        {
            Name = name;
            Type = type;
            Accessors = accessors;
            IsStatic = isStatic;
            Dispatch = dispatch;
            IsSealed = isSealed;
            Inline = inline;
            IsNative = isNative;
        }
    }

    /// <summary>A method, or a module-level function (§3.2, §2.5).</summary>
    public sealed class MethodDeclarationSyntax : DeclarationSyntax
    {
        /// <summary>The method's name.</summary>
        public string Name { get; }

        /// <summary>Its type parameters, empty when not generic.</summary>
        public IReadOnlyList<TypeParameterSyntax> TypeParameters { get; }

        /// <summary>Its parameters, in order.</summary>
        public IReadOnlyList<ParameterSyntax> Parameters { get; }

        /// <summary>Its return type. Always written out, <c>void</c> included (§1.1).</summary>
        public TypeSyntax ReturnType { get; }

        /// <summary>Its body, or <c>null</c> when abstract, native, or an interface member.</summary>
        public BlockStatementSyntax? Body { get; }

        /// <summary>True when declared <c>static</c>.</summary>
        public bool IsStatic { get; }

        /// <summary>How it dispatches (§3.3).</summary>
        public DispatchModifier Dispatch { get; }

        /// <summary>True when an <c>override</c> was also declared <c>sealed</c> (§3.3).</summary>
        public bool IsSealed { get; }

        /// <summary>Its inlining request (§3.6).</summary>
        public InlineModifier Inline { get; }

        /// <summary>True when declared <c>const</c> — foldable at compile time (§7.2).</summary>
        public bool IsConst { get; }

        /// <summary>True when declared <c>native</c> — the body lives on the host side (§10).</summary>
        public bool IsNative { get; }

        /// <summary>Initializes a method declaration.</summary>
        /// <param name="span">The source the declaration covers.</param>
        /// <param name="attributes">Attributes attached to it.</param>
        /// <param name="docComment">Doc comment lines preceding it.</param>
        /// <param name="visibility">Its declared visibility.</param>
        /// <param name="name">The method's name.</param>
        /// <param name="typeParameters">Its type parameters.</param>
        /// <param name="parameters">Its parameters.</param>
        /// <param name="returnType">Its return type.</param>
        /// <param name="body">Its body, or <c>null</c>.</param>
        /// <param name="isStatic">True when declared <c>static</c>.</param>
        /// <param name="dispatch">How it dispatches.</param>
        /// <param name="isSealed">True when the override was also declared <c>sealed</c>.</param>
        /// <param name="inline">Its inlining request.</param>
        /// <param name="isConst">True when declared <c>const</c>.</param>
        /// <param name="isNative">True when declared <c>native</c>.</param>
        public MethodDeclarationSyntax(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility,
            string name, IReadOnlyList<TypeParameterSyntax> typeParameters, IReadOnlyList<ParameterSyntax> parameters, TypeSyntax returnType,
            BlockStatementSyntax? body, bool isStatic, DispatchModifier dispatch, bool isSealed, InlineModifier inline, bool isConst, bool isNative)
            : base(span, attributes, docComment, visibility)
        {
            Name = name;
            TypeParameters = typeParameters;
            Parameters = parameters;
            ReturnType = returnType;
            Body = body;
            IsStatic = isStatic;
            Dispatch = dispatch;
            IsSealed = isSealed;
            Inline = inline;
            IsConst = isConst;
            IsNative = isNative;
        }
    }

    /// <summary>A constructor, including the <c>: super(...)</c> or <c>: this(...)</c> chain in its header (§3.2).</summary>
    public sealed class ConstructorDeclarationSyntax : DeclarationSyntax
    {
        /// <summary>Its parameters, in order.</summary>
        public IReadOnlyList<ParameterSyntax> Parameters { get; }

        /// <summary>The chained constructor's arguments, or <c>null</c> when no chain was written.</summary>
        public IReadOnlyList<ArgumentSyntax>? ChainArguments { get; }

        /// <summary>True when the chain was <c>this(...)</c> rather than <c>super(...)</c>.</summary>
        public bool ChainsToThis { get; }

        /// <summary>Its body.</summary>
        public BlockStatementSyntax Body { get; }

        /// <summary>Initializes a constructor declaration.</summary>
        /// <param name="span">The source the declaration covers.</param>
        /// <param name="attributes">Attributes attached to it.</param>
        /// <param name="docComment">Doc comment lines preceding it.</param>
        /// <param name="visibility">Its declared visibility.</param>
        /// <param name="parameters">Its parameters.</param>
        /// <param name="chainArguments">The chained constructor's arguments, or <c>null</c>.</param>
        /// <param name="chainsToThis">True when the chain was <c>this(...)</c>.</param>
        /// <param name="body">Its body.</param>
        public ConstructorDeclarationSyntax(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility,
            IReadOnlyList<ParameterSyntax> parameters, IReadOnlyList<ArgumentSyntax>? chainArguments, bool chainsToThis, BlockStatementSyntax body)
            : base(span, attributes, docComment, visibility)
        {
            Parameters = parameters;
            ChainArguments = chainArguments;
            ChainsToThis = chainsToThis;
            Body = body;
        }
    }

    /// <summary>
    /// An operator overload (§5.6). Always public; <c>static</c> is the default, and a dispatch
    /// modifier or an interface declaration makes it an instance method whose receiver is the first
    /// parameter.
    /// </summary>
    public sealed class OperatorDeclarationSyntax : DeclarationSyntax
    {
        /// <summary>
        /// The overloaded operator's token type. <see cref="TokenType.KeywordAs"/> here means
        /// <c>operator as</c>, the explicit conversion form.
        /// </summary>
        public TokenType Operator { get; }

        /// <summary>Its parameters. The arity distinguishes the unary and binary forms of <c>-</c>, and the read and write forms of <c>[]</c>.</summary>
        public IReadOnlyList<ParameterSyntax> Parameters { get; }

        /// <summary>
        /// Its return type — and, for <c>operator as</c>, the conversion's target, which §5.6
        /// writes after the keyword rather than after the parameters.
        /// </summary>
        /// <remarks>
        /// One property rather than two, because a conversion's target <em>is</em> what it
        /// returns: a separate <c>ConversionTarget</c> would hold the same type under a second
        /// name, and <see cref="Operator"/> already says which spelling produced it.
        /// </remarks>
        public TypeSyntax ReturnType { get; }

        /// <summary>How the operator dispatches (§3.3): <c>virtual</c>, <c>override</c> or <c>abstract</c> make it an instance method.</summary>
        public DispatchModifier Dispatch { get; }

        /// <summary>Whether <c>sealed</c> was written, which closes a virtual operator's branch.</summary>
        public bool IsSealed { get; }

        /// <summary>Its body, or <see langword="null"/> when the declaration ends at <c>;</c> — an abstract operator.</summary>
        public BlockStatementSyntax? Body { get; }

        /// <summary>Initializes an operator declaration.</summary>
        /// <param name="span">The source the declaration covers.</param>
        /// <param name="attributes">Attributes attached to it.</param>
        /// <param name="docComment">Doc comment lines preceding it.</param>
        /// <param name="op">The overloaded operator's token type.</param>
        /// <param name="parameters">Its parameters.</param>
        /// <param name="returnType">Its return type.</param>
        /// <param name="dispatch">Its dispatch modifier, if one was written.</param>
        /// <param name="isSealed">Whether <c>sealed</c> was written.</param>
        /// <param name="body">Its body, or <see langword="null"/> for an abstract operator.</param>
        public OperatorDeclarationSyntax(SourceSpan span, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment,
            TokenType op, IReadOnlyList<ParameterSyntax> parameters, TypeSyntax returnType,
            DispatchModifier dispatch, bool isSealed, BlockStatementSyntax? body)
            : base(span, attributes, docComment, Visibility.Public)
        {
            Operator = op;
            Parameters = parameters;
            ReturnType = returnType;
            Dispatch = dispatch;
            IsSealed = isSealed;
            Body = body;
        }
    }

    /// <summary>A <c>static { ... }</c> initializer block, in a type body or at module level (§3.2, §2.5).</summary>
    public sealed class StaticBlockDeclarationSyntax : DeclarationSyntax
    {
        /// <summary>The block's body.</summary>
        public BlockStatementSyntax Body { get; }

        /// <summary>Initializes a static block.</summary>
        /// <param name="span">The source the block covers.</param>
        /// <param name="body">Its body.</param>
        public StaticBlockDeclarationSyntax(SourceSpan span, BlockStatementSyntax body)
            : base(span, new List<AttributeSyntax>(), new List<string>(), Visibility.Default)
        {
            Body = body;
        }
    }

    /// <summary>
    /// A <c>const if</c> wrapping declarations rather than statements (§7.3). This is the form that
    /// replaces <c>#if</c>: a member in the untaken branch does not exist at all.
    /// </summary>
    public sealed class ConstIfDeclarationSyntax : DeclarationSyntax
    {
        /// <summary>The condition, which must fold to a constant.</summary>
        public ExpressionSyntax Condition { get; }

        /// <summary>The declarations kept when the condition holds.</summary>
        public IReadOnlyList<DeclarationSyntax> Then { get; }

        /// <summary>The declarations kept otherwise, empty when no <c>else</c> was written.</summary>
        public IReadOnlyList<DeclarationSyntax> Else { get; }

        /// <summary>Initializes a declaration-level <c>const if</c>.</summary>
        /// <param name="span">The source it covers.</param>
        /// <param name="condition">The condition.</param>
        /// <param name="then">The declarations kept when the condition holds.</param>
        /// <param name="elseDeclarations">The declarations kept otherwise.</param>
        public ConstIfDeclarationSyntax(SourceSpan span, ExpressionSyntax condition, IReadOnlyList<DeclarationSyntax> then, IReadOnlyList<DeclarationSyntax> elseDeclarations)
            : base(span, new List<AttributeSyntax>(), new List<string>(), Visibility.Default)
        {
            Condition = condition;
            Then = then;
            Else = elseDeclarations;
        }
    }
}
