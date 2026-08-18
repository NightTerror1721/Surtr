#nullable enable

namespace Surtr.Compiler.Diagnostics
{
    /// <summary>
    /// Identifies what a diagnostic is about, independently of how it is worded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A code is a <em>stable</em> name for a problem. Message text is written for a person and
    /// gets reworded; a test asserting on it breaks for no reason, a suppression keyed on it stops
    /// working, and a translation has nothing to key off. So everything that reports goes through
    /// one of these, and the message carries the specifics.
    /// </para>
    /// <para>
    /// The numbers are grouped by stage and are <b>append-only within a group</b>: a code that has
    /// been published is a name someone may have written down. Lexical problems are 1xxx and
    /// syntactic ones 2xxx; 3xxx is reserved for binding and 4xxx for code generation, so those
    /// stages can be added without renumbering these.
    /// </para>
    /// <para>
    /// The set is deliberately no larger than what the front end actually produces. Inventing a
    /// vocabulary ahead of the passes that would use it would mean guessing at distinctions that
    /// only a real binder can tell are worth making.
    /// </para>
    /// </remarks>
    public enum SurtrDiagnosticCode
    {
        /// <summary>No code. Never reported.</summary>
        None = 0,

        #region Lexical — 1xxx

        /// <summary>A character that begins no token in the language.</summary>
        UnexpectedCharacter = 1001,

        /// <summary>A <c>/*</c> that the file ends before closing.</summary>
        UnterminatedComment = 1002,

        /// <summary>A string literal the line or the file ends before closing.</summary>
        UnterminatedStringLiteral = 1003,

        /// <summary>A character literal holding no character, or more than one.</summary>
        InvalidCharacterLiteral = 1004,

        /// <summary>A literal broken across lines, which neither string nor character literals allow.</summary>
        LiteralSpansLines = 1005,

        /// <summary>An escape sequence that is unrecognised, incomplete, or missing its digits.</summary>
        InvalidEscapeSequence = 1006,

        /// <summary>A numeric literal that does not parse: no digits, or a malformed float.</summary>
        InvalidNumericLiteral = 1007,

        /// <summary>A numeric literal too large for the type it would have.</summary>
        NumericLiteralOutOfRange = 1008,

        #endregion

        #region Syntactic — 2xxx

        /// <summary>A token where the grammar required a different one.</summary>
        UnexpectedToken = 2001,

        /// <summary>A token that begins no declaration, where one was required.</summary>
        ExpectedDeclaration = 2002,

        /// <summary>A token that begins no expression, where one was required.</summary>
        ExpectedExpression = 2003,

        /// <summary>A token that begins no type, where one was required.</summary>
        ExpectedType = 2004,

        /// <summary>A modifier repeated, contradicted, or written where it carries no information.</summary>
        InvalidModifier = 2005,

        /// <summary>An operator overload for a token that cannot be overloaded, or written in the wrong shape.</summary>
        InvalidOperatorDeclaration = 2006,

        /// <summary>A type argument list that is not closed, or closed by a token ending in <c>=</c>.</summary>
        UnclosedTypeArgumentList = 2007,

        /// <summary>A constructor header chaining to something other than <c>super</c> or <c>this</c>.</summary>
        InvalidConstructorChain = 2008,

        /// <summary>A <c>try</c> with neither a <c>catch</c> nor a <c>finally</c>.</summary>
        IncompleteTryStatement = 2009,

        /// <summary>A declaration used as the unbraced body of an <c>if</c> or a loop, where it could never be read.</summary>
        DeclarationAsEmbeddedStatement = 2010,

        /// <summary>A malformed <c>$name</c> or <c>${ ... }</c> inside an interpolated string.</summary>
        InvalidInterpolation = 2011,

        #endregion

        #region Binding — 3xxx

        /// <summary>
        /// A source file whose location does not give it a usable module path (§2.1): outside the
        /// source root, at the root with no root module path configured, or in a directory whose
        /// name is not a legal identifier.
        /// </summary>
        InvalidModulePath = 3001,

        /// <summary>
        /// Modules that depend on one another in a cycle. Static initializers run eagerly at load
        /// in dependency order, and a cycle has no valid order.
        /// </summary>
        ModuleCycle = 3002,

        /// <summary>An <c>import</c> naming a module or type that nothing in the compilation provides.</summary>
        UnresolvedImport = 3003,

        /// <summary>Two declarations of the same name where only one can exist.</summary>
        DuplicateDeclaration = 3004,

        /// <summary>A name that resolves to nothing in scope.</summary>
        UnresolvedName = 3005,

        /// <summary>
        /// A name two imports both bring into scope. Reported at the use, not at the
        /// <c>import</c> line, since importing both is only a problem if the name is written (§2.1).
        /// </summary>
        AmbiguousName = 3006,

        /// <summary>A generic type given a number of type arguments its declaration does not take.</summary>
        WrongTypeArgumentCount = 3007,

        /// <summary>A class hierarchy that loops back on itself.</summary>
        InheritanceCycle = 3008,

        /// <summary>
        /// A <c>:</c> list that names two classes, extends a <c>sealed</c> one, or puts something
        /// that is neither a class nor an interface where one was needed (§2.2).
        /// </summary>
        InvalidBaseType = 3009,

        /// <summary>Aliases that define one another in a cycle (§2.7).</summary>
        AliasCycle = 3010,

        /// <summary>A <c>value class</c> that does not wrap exactly one <c>let</c> field, or that extends something (§2.9).</summary>
        InvalidValueClass = 3011,

        /// <summary>
        /// An interface member that is not a public abstract method or property: a field, a static,
        /// or a body (§2.3).
        /// </summary>
        InvalidInterfaceMember = 3012,

        /// <summary>A module member with the same name as a build-defined constant (§7.4).</summary>
        BuildConstantShadowed = 3013,

        /// <summary>Two overloads that no signature could tell apart (§3.5).</summary>
        DuplicateOverload = 3014,

        /// <summary>More type parameters than the single-digit descriptor form can encode.</summary>
        TooManyTypeParameters = 3015,

        /// <summary>A value used where its type does not reach the one expected.</summary>
        CannotConvert = 3016,

        /// <summary>A member name the receiver's type does not have.</summary>
        UnresolvedMember = 3017,

        /// <summary>A call whose arguments fit no overload, or fit two equally well (§3.5).</summary>
        UnresolvedCall = 3018,

        /// <summary>Something called, indexed or iterated that does not support it.</summary>
        NotSupportedOnType = 3019,

        /// <summary>An operator applied to operand types it is not defined for (§5.7).</summary>
        OperatorNotDefined = 3020,

        /// <summary>An assignment to something that cannot be assigned: a <c>let</c>, or a value.</summary>
        NotAssignable = 3021,

        /// <summary><c>this</c> or <c>super</c> written where there is no instance.</summary>
        NoInstanceInScope = 3022,

        /// <summary><c>null</c> written against a type that cannot hold it (§5.1).</summary>
        NullNotAllowed = 3023,

        /// <summary>A type that has to be inferred and cannot be.</summary>
        CannotInferType = 3024,

        /// <summary>A <c>break</c> or <c>continue</c> with no loop to leave, or naming one that is not there.</summary>
        JumpOutsideLoop = 3025,

        /// <summary>Branches of one expression whose types have nothing in common.</summary>
        NoCommonType = 3026,

        /// <summary>A local read on a path that has not assigned it yet.</summary>
        UseBeforeAssignment = 3027,

        /// <summary>A statement nothing can reach.</summary>
        UnreachableCode = 3028,

        /// <summary>A method that can finish without returning the value it promises.</summary>
        NotAllPathsReturn = 3029,

        /// <summary>A type argument that does not satisfy its parameter's constraints (§6).</summary>
        ConstraintNotSatisfied = 3030,

        /// <summary>A switch expression over an enum that neither lists every case nor has an <c>else</c> (§4.4).</summary>
        SwitchNotExhaustive = 3031,

        /// <summary>An expression required to be constant that does not fold (§7).</summary>
        NotAConstant = 3032,

        /// <summary>A <c>const fun</c> that breaks one of the restrictions §7.2 puts on one.</summary>
        InvalidConstFunction = 3033,

        /// <summary>
        /// A <c>const fun</c> the compiler tried to fold and could not run: it threw, exceeded the
        /// instruction budget, or uses something the const evaluator cannot emit yet (§7.2).
        /// </summary>
        ConstEvaluationFailed = 3034,

        /// <summary>
        /// A tuple indexed by something other than a constant inside its arity (§5.5). A tuple's
        /// element type varies per index, so the index is part of the type rather than a value.
        /// </summary>
        InvalidTupleIndex = 3035,

        /// <summary>
        /// A <c>native</c> declaration that breaks §10: one written somewhere other than module
        /// scope, or one given a body or an initializer the host is supposed to own.
        /// </summary>
        InvalidNativeDeclaration = 3036,

        /// <summary>
        /// A constructor that chains to nothing whose base declares no parameterless constructor
        /// (§3.2). An omitted chain means the base's parameterless one, so where there is none the
        /// omission names nothing and the base would go unconstructed.
        /// </summary>
        BaseConstructorUnreachable = 3037,

        /// <summary>
        /// A generic call whose type arguments neither the arguments nor the call site settle (§6).
        /// Inference is local and one-directional, so where it cannot decide, the call writes them.
        /// </summary>
        CannotInferTypeArgument = 3038,

        /// <summary>
        /// A member reached from somewhere its accessibility does not allow (§3.1): a
        /// <c>private</c> one from outside its type, a <c>protected</c> one from outside its
        /// hierarchy, or an <c>internal</c> one from another module.
        /// </summary>
        Inaccessible = 3039,

        /// <summary>
        /// An <c>@Name(args)</c> naming something that is not a class extending <c>Attribute</c>
        /// (§11).
        /// </summary>
        InvalidAttribute = 3040,

        /// <summary>
        /// A build's own settings are wrong rather than its source: a project file that is not
        /// there, a directive it does not understand, or a reference that names no image (§14.2).
        /// </summary>
        ProjectFileInvalid = 3041,

        /// <summary>
        /// A static initializer reading a static that has not been given its value yet
        /// (<c>docs/VM-Plan.md</c> §1.12): initializers run at load in declaration order, classes
        /// before the module, so reading one declared later reads a zero rather than a value.
        /// </summary>
        InitializerOutOfOrder = 3042,

        /// <summary>
        /// A concrete class leaves a member unimplemented — one required by an interface it
        /// implements (§2.3), or one it inherits <c>abstract</c> from a base class (§2.2) — instead
        /// of supplying a body or declaring itself <c>abstract</c> in turn.
        /// </summary>
        /// <remarks>
        /// <c>SurtrTypeLinker</c> also rejects this, but only when a module is <em>loaded</em>; this
        /// is the same check run early enough to name the source class rather than surface as a
        /// runtime <see cref="System.InvalidOperationException"/> with no span.
        /// </remarks>
        MissingImplementation = 3043,

        /// <summary>
        /// An <c>override</c> (or interface implementation, written <c>override</c>, §2.2) whose
        /// signature does not match what it replaces — the same name and arity, but a parameter or
        /// the return that differs once the contract is read as the construction the class declares.
        /// The runtime cannot catch this: <c>SurtrTypeLinker</c> matches by name plus <em>erased</em>
        /// parameter types, blind to a concrete type argument like the <c>int</c> in
        /// <c>IReadOnlyCollection&lt;int&gt;</c>, and it excludes the return type entirely — so a
        /// class implementing the <c>int</c> construction with members typed on its own <c>T</c>
        /// links cleanly and then misbehaves at a call site compiled against the contract.
        /// </summary>
        OverrideSignatureMismatch = 3050,

        /// <summary>
        /// An operator overload's arity or return type does not match what §5.6's table fixes for
        /// the token it overloads — checked at the declaration, not left to surface only as an
        /// unreachable overload the first time something tries to use it.
        /// </summary>
        InvalidOperatorSignature = 3044,

        /// <summary>
        /// A <c>const</c>'s declared type is not a primitive or <c>string</c> (§7.1) — the only
        /// shapes a value fixed at compile time and substituted at every use site can be.
        /// </summary>
        InvalidConstType = 3045,

        /// <summary>
        /// A no-arg or capacity construction attempted on <c>tuple&lt;...&gt;</c> where its declared
        /// arity is not 0 (§5.3). A tuple's arity is part of its type, fixed at every position, so
        /// there is nothing a size-changing constructor could mean — every element has to come from
        /// somewhere, either written out or cast from an <c>array&lt;T&gt;</c> of the right length.
        /// </summary>
        TupleArityFixed = 3046,

        /// <summary>
        /// A construction of <c>array&lt;T&gt;</c>, <c>dict&lt;K,V&gt;</c> or <c>tuple&lt;...&gt;</c>
        /// through their nameable form (§5.3) whose argument(s) name none of the shapes that type
        /// supports — a capacity, a matching source collection to cast/build from, a size-and-default
        /// pair, or a pair of parallel arrays — including any attempt to cast into or out of
        /// <c>dict&lt;K,V&gt;</c> other than from pairs or parallel arrays, which this pass does not
        /// support.
        /// </summary>
        CollectionCastNotSupported = 3047,

        /// <summary>
        /// One element or key/value of an <c>array&lt;T&gt;</c>/<c>dict&lt;K,V&gt;</c>/<c>tuple&lt;...&gt;</c>
        /// constructor's source (a cast, a size-and-default fill, an iterable, a pairs array, or
        /// parallel arrays) has no implicit conversion to the slot it would fill. Checked per element
        /// rather than as a single pass/fail, so the diagnostic names the one position that does not
        /// line up.
        /// </summary>
        CollectionElementConversionMissing = 3048,

        /// <summary>
        /// A construction of a primitive, <c>string</c> or <c>range</c> through its nameable form
        /// (§5.3.2) whose argument(s) match none of the shapes that type supports — a conversion from
        /// another primitive, a parse from <c>string</c>, or one of <c>string</c>'s/<c>range</c>'s own
        /// composing shapes. One code shared across every built-in scalar, the same way
        /// <see cref="CollectionCastNotSupported"/> is shared across array/dict/tuple, rather than one
        /// per type.
        /// </summary>
        NoBuiltInConstructorMatch = 3049,

        /// <summary>
        /// An accessor's own visibility (§3.2, §3.4), written directly on <c>get</c>/<c>set</c>, is
        /// not strictly narrower than the property's — either it is the same, which the accessor
        /// inherits for free by writing nothing, or it is wider, which would let a caller reach
        /// through the accessor something the property itself already hides from them.
        /// </summary>
        AccessorVisibilityNotNarrower = 3051,

        /// <summary>
        /// An <c>@Name(...)</c> use (§11) whose attribute class was declared <c>attribute(...)</c>
        /// with a target list that does not include the kind of declaration it was written on —
        /// e.g. a method-only attribute written above a field.
        /// </summary>
        AttributeTargetMismatch = 3052,

        /// <summary>
        /// Two <c>import ... as Name;</c> lines in the same module declare the same alias (§2.1).
        /// Unlike a named/wildcard import colliding with another, this is reported at the
        /// <c>import</c> line itself rather than at the point of use, because an alias has no
        /// other declaration it could be shadowing - two imports simply claimed the same name.
        /// </summary>
        DuplicateModuleAlias = 3053,

        /// <summary>
        /// A <c>moduleof(ModulePath)</c> path (§2.1) does not name any module known to this
        /// compilation - neither directly nor through a declared alias.
        /// </summary>
        UnresolvedModuleOf = 3054,

        #endregion

        #region Code generation — 4xxx

        /// <summary>A construct code generation cannot lower yet.</summary>
        NotLowered = 4001,

        #endregion
    }
}
