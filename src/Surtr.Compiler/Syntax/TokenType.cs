#nullable enable

namespace Surtr.Compiler.Syntax
{
    /// <summary>What kind of lexical unit a <see cref="Token"/> is.</summary>
    /// <remarks>
    /// <para>
    /// Derived from <c>docs/Language-Syntax.md</c>: the keyword block is exactly its §1.2 reserved
    /// word list, and the operator/punctuation block is exactly what its §5.7 precedence table and
    /// the surrounding sections spell. Nothing persists a <see cref="TokenType"/> to disk, so -
    /// unlike <c>OpCode</c> - there is no append-only constraint and members may be renumbered
    /// freely; the enum is grouped for reading, not for encoding.
    /// </para>
    /// <para>
    /// Three things deliberately do <em>not</em> appear here, each because the language spec says
    /// they are not keywords:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Type names.</b> <c>int</c>, <c>float</c>, <c>bool</c>, <c>char</c>, <c>string</c>,
    /// <c>void</c> and <c>range</c> lex as <see cref="Identifier"/>. §1.1 is explicit that type
    /// names are ordinary identifiers resolved in the type namespace, which is what lets a nested
    /// type shadow one.
    /// </description></item>
    /// <item><description>
    /// <b>Contextual keywords.</b> <c>this</c>, <c>super</c> and <c>value</c> lex as
    /// <see cref="Identifier"/> too (§3.2): they mean something specific only in the positions
    /// where they are legal, and stay usable as ordinary identifiers everywhere else, so
    /// recognising them is the parser's job.
    /// </description></item>
    /// <item><description>
    /// <b><c>as?</c>.</b> Scanned as <see cref="KeywordAs"/> followed by <see cref="Question"/>.
    /// A type must follow <c>as</c>, so a <c>?</c> directly after it cannot be read any other way
    /// and the parser can join the two with no adjacency test.
    /// </description></item>
    /// </list>
    /// <para>
    /// One thing the parser owes the lexer in return: every token that starts with <c>&gt;&gt;</c> —
    /// <see cref="ShiftRight"/>, <see cref="UnsignedShiftRight"/>, <see cref="ShiftRightAssign"/>
    /// and <see cref="UnsignedShiftRightAssign"/> — must be <b>split back apart in type-argument
    /// position</b>. Maximal munch turns the tail of <c>&lt;T : IComparable&lt;T&gt;&gt;</c> into one
    /// <c>&gt;&gt;</c>, and a third level of nesting into one <c>&gt;&gt;&gt;</c>. No amount of lexer
    /// cleverness fixes that without the parser's context; this is the same bargain Java and C#
    /// make.
    /// </para>
    /// </remarks>
    public enum TokenType : byte
    {
        /// <summary>
        /// Never produced. It occupies zero so a default-initialized <see cref="Token"/> cannot be
        /// mistaken for a real one - the lexer throws <c>SurtrLexerException</c> on malformed input
        /// rather than emitting a token that a later stage might not think to check.
        /// </summary>
        Invalid = 0,

        /// <summary>End of the source buffer. Always the last token produced.</summary>
        EndOfFile,

        /// <summary>An integer literal - decimal, <c>0x</c> hex or <c>0b</c> binary (§5.8).</summary>
        IntegerLiteral,

        /// <summary>A floating-point literal: a decimal point or an exponent is what makes one, never a suffix (§5.8).</summary>
        FloatLiteral,

        /// <summary>A string literal with no interpolation. Its payload is the fully decoded value.</summary>
        StringLiteral,

        /// <summary>
        /// A string literal containing at least one unescaped <c>$</c> (§5.2). Its payload is the
        /// <em>raw</em> inner text, escapes left intact, because splitting the literal into its
        /// text and expression parts means re-lexing the expressions - a parser-stage concern, and
        /// one that needs to still see <c>\$</c> to know which dollars were escaped.
        /// </summary>
        InterpolatedStringLiteral,

        /// <summary>A character literal. Its payload is the decoded character.</summary>
        CharacterLiteral,

        /// <summary>An identifier - which includes every type name and every contextual keyword (see the remarks on this enum).</summary>
        Identifier,

        /// <summary>
        /// A <c>///</c> documentation comment (§11). Ordinary <c>//</c> and <c>/* */</c> comments
        /// are trivia and never reach the token stream; a doc comment carries meaning about the
        /// declaration that follows it, so discarding it in the lexer would make it unrecoverable.
        /// </summary>
        DocComment,

        /// <summary><c>abstract</c> - a class that cannot be instantiated, or a member with no body (§3.3).</summary>
        KeywordAbstract,

        /// <summary><c>alias</c> - declares a transparent type alias, erased at compile time (§2.7).</summary>
        KeywordAlias,

        /// <summary><c>as</c> - explicit cast; followed by <see cref="Question"/> it is the safe form (§5.7).</summary>
        KeywordAs,

        /// <summary><c>break</c> - leaves a loop, or ends a <c>switch</c> case (§4.2, §4.3).</summary>
        KeywordBreak,

        /// <summary><c>case</c> - an arm of the statement form of <c>switch</c> (§4.3).</summary>
        KeywordCase,

        /// <summary><c>catch</c> - handles one exception type (§8).</summary>
        KeywordCatch,

        /// <summary><c>class</c> (§2.2).</summary>
        KeywordClass,

        /// <summary>
        /// <c>const</c> - a compile-time value (§7.1), a foldable function (§7.2), or a
        /// compile-time branch when it prefixes <see cref="KeywordIf"/> (§7.3).
        /// </summary>
        KeywordConst,

        /// <summary><c>constructor</c> - Surtr names constructors with a keyword rather than repeating the class name (§3.2).</summary>
        KeywordConstructor,

        /// <summary><c>continue</c> - skips to the next iteration (§4.2).</summary>
        KeywordContinue,

        /// <summary><c>default</c> - the fallback arm of a statement <c>switch</c> (§4.3).</summary>
        KeywordDefault,

        /// <summary><c>else</c> - an <c>if</c> alternative (§4.1), and the fallback arm of an expression <c>switch</c> (§4.3).</summary>
        KeywordElse,

        /// <summary><c>enum</c> (§2.4).</summary>
        KeywordEnum,

        /// <summary><c>false</c> - a boolean literal.</summary>
        KeywordFalse,

        /// <summary><c>finally</c> (§8).</summary>
        KeywordFinally,

        /// <summary><c>for</c> - both the three-clause and the <c>for-in</c> form (§4.2).</summary>
        KeywordFor,

        /// <summary><c>forceinline</c> - inlining is mandatory; impossible cases are a compile error, never a silent call (§3.6).</summary>
        KeywordForceInline,

        /// <summary><c>fun</c> - introduces a method or a module-level function (§3.2, §2.5).</summary>
        KeywordFun,

        /// <summary><c>if</c> (§4.1).</summary>
        KeywordIf,

        /// <summary><c>import</c> - brings another module's declarations into scope (§2.1).</summary>
        KeywordImport,

        /// <summary><c>in</c> - the <c>for-in</c> separator (§4.2).</summary>
        KeywordIn,

        /// <summary><c>inline</c> - a hint the compiler may decline, unlike <see cref="KeywordForceInline"/> (§3.6).</summary>
        KeywordInline,

        /// <summary><c>interface</c> (§2.3).</summary>
        KeywordInterface,

        /// <summary><c>internal</c> - module-scoped visibility, and the default for a top-level declaration (§3.1).</summary>
        KeywordInternal,

        /// <summary><c>is</c> - type test, without casting (§5.7).</summary>
        KeywordIs,

        /// <summary><c>moduleof</c> - the module a compile-time-known path names (§2.1).</summary>
        KeywordModuleOf,

        /// <summary><c>let</c> - an immutable binding (§1).</summary>
        KeywordLet,

        /// <summary><c>typeof</c> - the compile-time-known type of a name, or the runtime type of a value (§11).</summary>
        KeywordTypeOf,

        /// <summary><c>native</c> - declares a host-provided function or variable, with no body (§9).</summary>
        KeywordNative,

        /// <summary><c>null</c> - legal only against a nullable type (§5.1).</summary>
        KeywordNull,

        /// <summary><c>operator</c> - prefixes the token being overloaded, as in <c>operator+</c> (§5.6).</summary>
        KeywordOperator,

        /// <summary><c>override</c> - mandatory when replacing a virtual member; there is no implicit override (§3.3).</summary>
        KeywordOverride,

        /// <summary><c>private</c> - the default for a class member (§3.1).</summary>
        KeywordPrivate,

        /// <summary><c>protected</c> (§3.1).</summary>
        KeywordProtected,

        /// <summary><c>public</c> (§3.1).</summary>
        KeywordPublic,

        /// <summary><c>return</c>.</summary>
        KeywordReturn,

        /// <summary><c>sealed</c> - a class nothing may extend, which lets the compiler devirtualise (§2.2).</summary>
        KeywordSealed,

        /// <summary><c>singleton</c> - a type with exactly one instance, which unlike a module can implement interfaces and be passed as a value (§2.8).</summary>
        KeywordSingleton,

        /// <summary><c>static</c> - a type-level member, or a static initializer block (§3.2, §2.5).</summary>
        KeywordStatic,

        /// <summary><c>switch</c> - both the statement and the expression form (§4.3).</summary>
        KeywordSwitch,

        /// <summary><c>throw</c> (§8).</summary>
        KeywordThrow,

        /// <summary><c>true</c> - a boolean literal.</summary>
        KeywordTrue,

        /// <summary><c>try</c> (§8).</summary>
        KeywordTry,

        /// <summary><c>var</c> - a mutable binding (§1).</summary>
        KeywordVar,

        /// <summary><c>virtual</c> - gives a method a vtable slot; methods are non-virtual by default (§3.3).</summary>
        KeywordVirtual,

        /// <summary><c>while</c> (§4.2).</summary>
        KeywordWhile,

        /// <summary><c>(</c></summary>
        LeftParen,

        /// <summary><c>)</c></summary>
        RightParen,

        /// <summary><c>{</c> - a block, a dict literal or a dict type, depending on position (§5.3, §5.4).</summary>
        LeftBrace,

        /// <summary><c>}</c></summary>
        RightBrace,

        /// <summary><c>[</c> - indexing, an array literal, or an array type's suffix.</summary>
        LeftBracket,

        /// <summary><c>]</c></summary>
        RightBracket,

        /// <summary><c>;</c> - the mandatory statement terminator (§1).</summary>
        Semicolon,

        /// <summary><c>,</c></summary>
        Comma,

        /// <summary><c>.</c> - the only member-access operator, at every level (§2.6).</summary>
        Dot,

        /// <summary><c>:</c> - type annotations, base lists, named arguments, switch cases, ternary.</summary>
        Colon,

        /// <summary><c>@</c> - introduces an attribute (§10).</summary>
        At,

        /// <summary><c>..</c> - a range, upper bound exclusive (§5.4).</summary>
        DotDot,

        /// <summary><c>..=</c> - a range, upper bound inclusive (§5.4).</summary>
        DotDotEquals,

        /// <summary><c>...</c> - marks a varargs parameter (§3.5).</summary>
        Ellipsis,

        /// <summary><c>-&gt;</c> - a closure <em>type</em> (§5.3) and a switch-expression arm (§4.3).</summary>
        Arrow,

        /// <summary><c>=&gt;</c> - a lambda <em>value</em> (§7). Deliberately distinct from <see cref="Arrow"/>.</summary>
        FatArrow,

        /// <summary><c>+</c> - addition, and string concatenation.</summary>
        Plus,

        /// <summary><c>-</c> - subtraction, and unary negation.</summary>
        Minus,

        /// <summary><c>*</c></summary>
        Star,

        /// <summary><c>/</c> - truncating between two <c>int</c>s, real division once a <c>float</c> is involved (§5.7).</summary>
        Slash,

        /// <summary><c>%</c></summary>
        Percent,

        /// <summary><c>=</c></summary>
        Assign,

        /// <summary><c>+=</c></summary>
        PlusAssign,

        /// <summary><c>-=</c></summary>
        MinusAssign,

        /// <summary><c>*=</c></summary>
        StarAssign,

        /// <summary><c>/=</c></summary>
        SlashAssign,

        /// <summary><c>%=</c></summary>
        PercentAssign,

        /// <summary><c>&amp;=</c></summary>
        AmpersandAssign,

        /// <summary><c>|=</c></summary>
        PipeAssign,

        /// <summary><c>^=</c></summary>
        CaretAssign,

        /// <summary><c>&lt;&lt;=</c></summary>
        ShiftLeftAssign,

        /// <summary><c>&gt;&gt;=</c> - which, like <see cref="ShiftRight"/>, the parser may have to split in type-argument position.</summary>
        ShiftRightAssign,

        /// <summary><c>&gt;&gt;&gt;=</c> - which, like <see cref="UnsignedShiftRight"/>, the parser may have to split in type-argument position.</summary>
        UnsignedShiftRightAssign,

        /// <summary><c>??=</c></summary>
        NullCoalesceAssign,

        /// <summary><c>==</c> - value equality, routed through the runtime's comparer (§5.7).</summary>
        Equal,

        /// <summary><c>!=</c></summary>
        NotEqual,

        /// <summary><c>===</c> - reference identity, ignoring any <c>operator==</c> overload (§5.7).</summary>
        ReferenceEqual,

        /// <summary><c>!==</c></summary>
        ReferenceNotEqual,

        /// <summary><c>&lt;</c> - also opens a type-argument list.</summary>
        Less,

        /// <summary><c>&gt;</c> - also closes a type-argument list.</summary>
        Greater,

        /// <summary><c>&lt;=</c></summary>
        LessEqual,

        /// <summary><c>&gt;=</c></summary>
        GreaterEqual,

        /// <summary><c>&lt;=&gt;</c> - three-way comparison. Only ever appears as <c>operator&lt;=&gt;</c> (§5.6).</summary>
        Spaceship,

        /// <summary><c>&amp;&amp;</c></summary>
        LogicalAnd,

        /// <summary><c>||</c></summary>
        LogicalOr,

        /// <summary><c>!</c></summary>
        LogicalNot,

        /// <summary><c>&amp;</c> - bitwise and, and the separator between multiple generic bounds (§6).</summary>
        Ampersand,

        /// <summary><c>|</c> - bitwise or.</summary>
        Pipe,

        /// <summary><c>^</c> - bitwise xor.</summary>
        Caret,

        /// <summary><c>~</c> - bitwise complement.</summary>
        Tilde,

        /// <summary><c>&lt;&lt;</c></summary>
        ShiftLeft,

        /// <summary><c>&gt;&gt;</c> - arithmetic (sign-replicating) right shift, VM opcode <c>Sar</c>. The parser must split it in type-argument position (see the remarks on this enum).</summary>
        ShiftRight,

        /// <summary>
        /// <c>&gt;&gt;&gt;</c> - logical (zero-filling) right shift, VM opcode <c>Shr</c>. Surtr's
        /// <c>int</c> is signed, so this is not the same operator as <see cref="ShiftRight"/>.
        /// Three closing angle brackets produce this token, so the parser must split it in
        /// type-argument position too.
        /// </summary>
        UnsignedShiftRight,

        /// <summary><c>++</c> - prefix and postfix both exist (§5.7).</summary>
        Increment,

        /// <summary><c>--</c> - prefix and postfix both exist (§5.7).</summary>
        Decrement,

        /// <summary><c>?</c> - a nullable type suffix (§5.1), the ternary, and the tail of <c>as?</c>.</summary>
        Question,

        /// <summary><c>?.</c> - safe navigation (§5.1).</summary>
        QuestionDot,

        /// <summary><c>??</c> - null-coalescing (§5.1).</summary>
        NullCoalesce,

        /// <summary><c>!!</c> - null assertion (§5.1).</summary>
        BangBang,
    }
}
