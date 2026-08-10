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
        /// <param name="location">Where in the source the declaration begins.</param>
        /// <param name="attributes">The attributes attached to it.</param>
        /// <param name="docComment">The doc comment lines preceding it.</param>
        /// <param name="visibility">The declared visibility.</param>
        protected DeclarationSyntax(SourceLocation location, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility)
            : base(location)
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
        /// <param name="location">Where in the source the attribute begins.</param>
        /// <param name="name">The attribute's name.</param>
        /// <param name="arguments">Its arguments.</param>
        public AttributeSyntax(SourceLocation location, string name, IReadOnlyList<ExpressionSyntax> arguments) : base(location)
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
        /// <param name="location">Where in the source the parameter begins.</param>
        /// <param name="name">The parameter's name.</param>
        /// <param name="type">Its declared type, or <c>null</c>.</param>
        /// <param name="defaultValue">Its default value, or <c>null</c>.</param>
        /// <param name="isVarargs">True when declared with a trailing <c>...</c>.</param>
        public ParameterSyntax(SourceLocation location, string name, TypeSyntax? type, ExpressionSyntax? defaultValue, bool isVarargs)
            : base(location)
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
        /// <param name="location">Where in the source the parameter begins.</param>
        /// <param name="name">Its name.</param>
        /// <param name="constraints">Its constraints, or an empty list.</param>
        public TypeParameterSyntax(SourceLocation location, string name, IReadOnlyList<TypeSyntax> constraints) : base(location)
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
        /// <param name="location">Where in the source the file begins.</param>
        /// <param name="imports">The imports.</param>
        /// <param name="declarations">The declarations.</param>
        public CompilationUnitSyntax(SourceLocation location, IReadOnlyList<ImportSyntax> imports, IReadOnlyList<DeclarationSyntax> declarations)
            : base(location)
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

        /// <summary>Initializes an import.</summary>
        /// <param name="location">Where in the source the import begins.</param>
        /// <param name="path">The dotted path's segments.</param>
        /// <param name="isWildcard">True when written with a trailing <c>.*</c>.</param>
        public ImportSyntax(SourceLocation location, IReadOnlyList<string> path, bool isWildcard) : base(location)
        {
            Path = path;
            IsWildcard = isWildcard;
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
        /// <param name="location">Where in the source the declaration begins.</param>
        /// <param name="attributes">Attributes attached to it.</param>
        /// <param name="docComment">Doc comment lines preceding it.</param>
        /// <param name="visibility">Its declared visibility.</param>
        /// <param name="name">The alias's name.</param>
        /// <param name="typeParameters">Its type parameters.</param>
        /// <param name="target">The type it names.</param>
        public AliasDeclarationSyntax(SourceLocation location, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility,
            string name, IReadOnlyList<TypeParameterSyntax> typeParameters, TypeSyntax target)
            : base(location, attributes, docComment, visibility)
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

        /// <summary>Initializes a type declaration.</summary>
        /// <param name="location">Where in the source the declaration begins.</param>
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
        public TypeDeclarationSyntax(SourceLocation location, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility,
            TypeDeclarationKind kind, string name, IReadOnlyList<TypeParameterSyntax> typeParameters, IReadOnlyList<TypeSyntax> baseTypes,
            IReadOnlyList<EnumCaseSyntax> enumCases, IReadOnlyList<DeclarationSyntax> members, bool isAbstract, bool isSealed, bool isStatic)
            : base(location, attributes, docComment, visibility)
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
        }
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
        /// <param name="location">Where in the source the case begins.</param>
        /// <param name="name">The case's name.</param>
        /// <param name="arguments">The constructor arguments.</param>
        /// <param name="docComment">Doc comment lines preceding it.</param>
        public EnumCaseSyntax(SourceLocation location, string name, IReadOnlyList<ArgumentSyntax> arguments, IReadOnlyList<string> docComment)
            : base(location)
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
        /// <param name="location">Where in the source the declaration begins.</param>
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
        public FieldDeclarationSyntax(SourceLocation location, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility,
            string name, TypeSyntax? type, ExpressionSyntax? initializer, bool isMutable, bool isConst, bool isStatic, bool isNative)
            : base(location, attributes, docComment, visibility)
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

        /// <summary>Initializes an accessor.</summary>
        /// <param name="location">Where in the source the accessor begins.</param>
        /// <param name="isGetter">True for <c>get</c>.</param>
        /// <param name="body">Its body, or <c>null</c>.</param>
        public AccessorSyntax(SourceLocation location, bool isGetter, BlockStatementSyntax? body) : base(location)
        {
            IsGetter = isGetter;
            Body = body;
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

        /// <summary>Initializes a property declaration.</summary>
        /// <param name="location">Where in the source the declaration begins.</param>
        /// <param name="attributes">Attributes attached to it.</param>
        /// <param name="docComment">Doc comment lines preceding it.</param>
        /// <param name="visibility">Its declared visibility.</param>
        /// <param name="name">The property's name.</param>
        /// <param name="type">Its type.</param>
        /// <param name="accessors">Its accessors.</param>
        /// <param name="isStatic">True when declared <c>static</c>.</param>
        /// <param name="dispatch">How it dispatches.</param>
        /// <param name="isSealed">True when the override was also declared <c>sealed</c>.</param>
        public PropertyDeclarationSyntax(SourceLocation location, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility,
            string name, TypeSyntax type, IReadOnlyList<AccessorSyntax> accessors, bool isStatic, DispatchModifier dispatch, bool isSealed)
            : base(location, attributes, docComment, visibility)
        {
            Name = name;
            Type = type;
            Accessors = accessors;
            IsStatic = isStatic;
            Dispatch = dispatch;
            IsSealed = isSealed;
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
        /// <param name="location">Where in the source the declaration begins.</param>
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
        public MethodDeclarationSyntax(SourceLocation location, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility,
            string name, IReadOnlyList<TypeParameterSyntax> typeParameters, IReadOnlyList<ParameterSyntax> parameters, TypeSyntax returnType,
            BlockStatementSyntax? body, bool isStatic, DispatchModifier dispatch, bool isSealed, InlineModifier inline, bool isConst, bool isNative)
            : base(location, attributes, docComment, visibility)
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
        /// <param name="location">Where in the source the declaration begins.</param>
        /// <param name="attributes">Attributes attached to it.</param>
        /// <param name="docComment">Doc comment lines preceding it.</param>
        /// <param name="visibility">Its declared visibility.</param>
        /// <param name="parameters">Its parameters.</param>
        /// <param name="chainArguments">The chained constructor's arguments, or <c>null</c>.</param>
        /// <param name="chainsToThis">True when the chain was <c>this(...)</c>.</param>
        /// <param name="body">Its body.</param>
        public ConstructorDeclarationSyntax(SourceLocation location, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment, Visibility visibility,
            IReadOnlyList<ParameterSyntax> parameters, IReadOnlyList<ArgumentSyntax>? chainArguments, bool chainsToThis, BlockStatementSyntax body)
            : base(location, attributes, docComment, visibility)
        {
            Parameters = parameters;
            ChainArguments = chainArguments;
            ChainsToThis = chainsToThis;
            Body = body;
        }
    }

    /// <summary>An operator overload (§5.6). Always public and static, so neither is written.</summary>
    public sealed class OperatorDeclarationSyntax : DeclarationSyntax
    {
        /// <summary>
        /// The overloaded operator's token type. <see cref="TokenType.KeywordAs"/> here means
        /// <c>operator as</c>, the explicit conversion form.
        /// </summary>
        public TokenType Operator { get; }

        /// <summary>Its parameters. The arity distinguishes the unary and binary forms of <c>-</c>, and the read and write forms of <c>[]</c>.</summary>
        public IReadOnlyList<ParameterSyntax> Parameters { get; }

        /// <summary>Its return type. For <c>operator as</c> this is the conversion's target.</summary>
        public TypeSyntax ReturnType { get; }

        /// <summary>Its body.</summary>
        public BlockStatementSyntax Body { get; }

        /// <summary>Initializes an operator declaration.</summary>
        /// <param name="location">Where in the source the declaration begins.</param>
        /// <param name="attributes">Attributes attached to it.</param>
        /// <param name="docComment">Doc comment lines preceding it.</param>
        /// <param name="op">The overloaded operator's token type.</param>
        /// <param name="parameters">Its parameters.</param>
        /// <param name="returnType">Its return type.</param>
        /// <param name="body">Its body.</param>
        public OperatorDeclarationSyntax(SourceLocation location, IReadOnlyList<AttributeSyntax> attributes, IReadOnlyList<string> docComment,
            TokenType op, IReadOnlyList<ParameterSyntax> parameters, TypeSyntax returnType, BlockStatementSyntax body)
            : base(location, attributes, docComment, Visibility.Public)
        {
            Operator = op;
            Parameters = parameters;
            ReturnType = returnType;
            Body = body;
        }
    }

    /// <summary>A <c>static { ... }</c> initializer block, in a type body or at module level (§3.2, §2.5).</summary>
    public sealed class StaticBlockDeclarationSyntax : DeclarationSyntax
    {
        /// <summary>The block's body.</summary>
        public BlockStatementSyntax Body { get; }

        /// <summary>Initializes a static block.</summary>
        /// <param name="location">Where in the source the block begins.</param>
        /// <param name="body">Its body.</param>
        public StaticBlockDeclarationSyntax(SourceLocation location, BlockStatementSyntax body)
            : base(location, new List<AttributeSyntax>(), new List<string>(), Visibility.Default)
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
        /// <param name="location">Where in the source it begins.</param>
        /// <param name="condition">The condition.</param>
        /// <param name="then">The declarations kept when the condition holds.</param>
        /// <param name="elseDeclarations">The declarations kept otherwise.</param>
        public ConstIfDeclarationSyntax(SourceLocation location, ExpressionSyntax condition, IReadOnlyList<DeclarationSyntax> then, IReadOnlyList<DeclarationSyntax> elseDeclarations)
            : base(location, new List<AttributeSyntax>(), new List<string>(), Visibility.Default)
        {
            Condition = condition;
            Then = then;
            Else = elseDeclarations;
        }
    }
}
