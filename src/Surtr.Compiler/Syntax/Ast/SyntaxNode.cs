#nullable enable

namespace Surtr.Compiler.Syntax.Ast
{
    /// <summary>Base of every node the parser produces.</summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="Token"/> — which is a flat struct precisely because nothing about a token
    /// differs by kind except its four fields — an AST node's *shape* differs by kind: an `if` has
    /// a condition and two branches, a call has a callee and arguments. A class hierarchy is
    /// therefore the right tool here, and the reasoning that kept <see cref="Token"/> flat is what
    /// says so.
    /// </para>
    /// <para>
    /// Nodes are immutable: every child is set in the constructor and exposed through a get-only
    /// property. A later pass that needs to annotate a tree (binding, lowering) builds its own
    /// side table keyed by node rather than mutating the syntax, so a tree can be walked twice and
    /// mean the same thing both times.
    /// </para>
    /// <para>
    /// Where an axis carries no data of its own it is a field on a shared node rather than a
    /// subclass — <see cref="Ast.BinaryOperator"/> and <see cref="TypeDeclarationKind"/> are the
    /// two big ones. That mirrors what <c>CLAUDE.md</c> says about
    /// <c>SurtrMethodDispatch</c>/<c>SurtrMethodRole</c> staying plain fields instead of becoming a
    /// second subclassing axis that would multiply against the first.
    /// </para>
    /// </remarks>
    public abstract class SyntaxNode
    {
        /// <summary>Where in the source this node begins.</summary>
        public SourceLocation Location { get; }

        /// <summary>Initializes the node with the position it starts at.</summary>
        /// <param name="location">Where in the source the node begins.</param>
        protected SyntaxNode(SourceLocation location)
        {
            Location = location;
        }
    }

    /// <summary>A binary operator, as written in source.</summary>
    /// <remarks>
    /// One enum rather than one node type per operator: the operands and the position are all a
    /// binary expression holds, so twenty subclasses would differ only in a name. §5.7 of
    /// <c>docs/Language-Syntax.md</c> is the authority on how these bind.
    /// </remarks>
    public enum BinaryOperator
    {
        /// <summary><c>+</c></summary>
        Add,

        /// <summary><c>-</c></summary>
        Subtract,

        /// <summary><c>*</c></summary>
        Multiply,

        /// <summary><c>/</c> — truncating between two <c>int</c>s.</summary>
        Divide,

        /// <summary><c>%</c></summary>
        Modulo,

        /// <summary><c>&lt;&lt;</c></summary>
        ShiftLeft,

        /// <summary><c>&gt;&gt;</c> — arithmetic, sign-replicating.</summary>
        ShiftRight,

        /// <summary><c>&gt;&gt;&gt;</c> — logical, zero-filling.</summary>
        UnsignedShiftRight,

        /// <summary><c>&amp;</c></summary>
        BitAnd,

        /// <summary><c>|</c></summary>
        BitOr,

        /// <summary><c>^</c></summary>
        BitXor,

        /// <summary><c>&amp;&amp;</c> — short-circuits.</summary>
        LogicalAnd,

        /// <summary><c>||</c> — short-circuits.</summary>
        LogicalOr,

        /// <summary><c>==</c> — value equality.</summary>
        Equal,

        /// <summary><c>!=</c></summary>
        NotEqual,

        /// <summary><c>===</c> — reference identity.</summary>
        ReferenceEqual,

        /// <summary><c>!==</c></summary>
        ReferenceNotEqual,

        /// <summary><c>&lt;</c></summary>
        Less,

        /// <summary><c>&lt;=</c></summary>
        LessEqual,

        /// <summary><c>&gt;</c></summary>
        Greater,

        /// <summary><c>&gt;=</c></summary>
        GreaterEqual,

        /// <summary><c>&lt;=&gt;</c> — three-way comparison, yielding <c>int</c>.</summary>
        Compare,

        /// <summary><c>??</c> — null-coalescing.</summary>
        NullCoalesce,

        /// <summary><c>..</c> — range, upper bound exclusive.</summary>
        Range,

        /// <summary><c>..=</c> — range, upper bound inclusive.</summary>
        RangeInclusive,
    }

    /// <summary>A unary operator, as written in source.</summary>
    public enum UnaryOperator
    {
        /// <summary>Prefix <c>-</c>.</summary>
        Negate,

        /// <summary>Prefix <c>!</c> — logical negation.</summary>
        Not,

        /// <summary>Prefix <c>~</c> — bitwise complement.</summary>
        Complement,

        /// <summary>Prefix <c>++</c>; evaluates to the updated value.</summary>
        PreIncrement,

        /// <summary>Prefix <c>--</c>; evaluates to the updated value.</summary>
        PreDecrement,

        /// <summary>Postfix <c>++</c>; evaluates to the value before the update.</summary>
        PostIncrement,

        /// <summary>Postfix <c>--</c>; evaluates to the value before the update.</summary>
        PostDecrement,

        /// <summary>Postfix <c>!!</c> — asserts a nullable value is non-null right now.</summary>
        NullAssert,
    }

    /// <summary>The operator of an assignment, including the compound forms.</summary>
    public enum AssignmentOperator
    {
        /// <summary><c>=</c></summary>
        Assign,

        /// <summary><c>+=</c></summary>
        Add,

        /// <summary><c>-=</c></summary>
        Subtract,

        /// <summary><c>*=</c></summary>
        Multiply,

        /// <summary><c>/=</c></summary>
        Divide,

        /// <summary><c>%=</c></summary>
        Modulo,

        /// <summary><c>&amp;=</c></summary>
        BitAnd,

        /// <summary><c>|=</c></summary>
        BitOr,

        /// <summary><c>^=</c></summary>
        BitXor,

        /// <summary><c>&lt;&lt;=</c></summary>
        ShiftLeft,

        /// <summary><c>&gt;&gt;=</c></summary>
        ShiftRight,

        /// <summary><c>&gt;&gt;&gt;=</c></summary>
        UnsignedShiftRight,

        /// <summary><c>??=</c></summary>
        NullCoalesce,
    }
}
