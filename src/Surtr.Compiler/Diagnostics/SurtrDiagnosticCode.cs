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

        #endregion

        #region Code generation — 4xxx

        /// <summary>A construct code generation cannot lower yet.</summary>
        NotLowered = 4001,

        #endregion
    }
}
