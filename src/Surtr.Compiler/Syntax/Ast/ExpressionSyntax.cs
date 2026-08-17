#nullable enable

using System.Collections.Generic;

namespace Surtr.Compiler.Syntax.Ast
{
    /// <summary>Base of every expression.</summary>
    public abstract class ExpressionSyntax : SyntaxNode
    {
        /// <summary>Initializes the node with the position it starts at.</summary>
        /// <param name="span">The source the expression covers.</param>
        protected ExpressionSyntax(SourceSpan span) : base(span)
        {
        }
    }

    /// <summary>A literal: a number, string, character, <c>true</c>, <c>false</c> or <c>null</c>.</summary>
    /// <remarks>
    /// The token is kept whole rather than decoded into a value, because
    /// <see cref="Token.Payload"/> already carries the decoded form and the token also carries the
    /// lexeme a diagnostic wants to quote.
    /// </remarks>
    public sealed class LiteralExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The literal token, payload included.</summary>
        public Token Literal { get; }

        /// <summary>Initializes a literal.</summary>
        /// <param name="literal">The literal token.</param>
        public LiteralExpressionSyntax(Token literal) : base(literal.Span)
        {
            Literal = literal;
        }
    }

    /// <summary>
    /// An interpolated string. Its parts alternate between literal text and spliced expressions;
    /// see <see cref="Parts"/>.
    /// </summary>
    public sealed class InterpolatedStringExpressionSyntax : ExpressionSyntax
    {
        /// <summary>
        /// The pieces of the literal in source order. A <see cref="LiteralExpressionSyntax"/> over
        /// a string token is literal text; anything else is a spliced expression.
        /// </summary>
        public IReadOnlyList<ExpressionSyntax> Parts { get; }

        /// <summary>Initializes an interpolated string.</summary>
        /// <param name="span">The source the literal covers.</param>
        /// <param name="parts">The alternating text and expression parts.</param>
        public InterpolatedStringExpressionSyntax(SourceSpan span, IReadOnlyList<ExpressionSyntax> parts)
            : base(span)
        {
            Parts = parts;
        }
    }

    /// <summary>A bare name: a local, parameter, field, type or module segment. Which of those it is, is the binder's to decide.</summary>
    public sealed class IdentifierExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The identifier's text.</summary>
        public string Name { get; }

        /// <summary>Initializes an identifier reference.</summary>
        /// <param name="span">The source the identifier covers.</param>
        /// <param name="name">The identifier's text.</param>
        public IdentifierExpressionSyntax(SourceSpan span, string name) : base(span)
        {
            Name = name;
        }
    }

    /// <summary>The contextual keyword <c>this</c> — the receiver of an instance member.</summary>
    public sealed class ThisExpressionSyntax : ExpressionSyntax
    {
        /// <summary>Initializes a <c>this</c> reference.</summary>
        /// <param name="span">The source it covers.</param>
        public ThisExpressionSyntax(SourceSpan span) : base(span)
        {
        }
    }

    /// <summary>The contextual keyword <c>super</c> — the base-class receiver.</summary>
    public sealed class SuperExpressionSyntax : ExpressionSyntax
    {
        /// <summary>Initializes a <c>super</c> reference.</summary>
        /// <param name="span">The source it covers.</param>
        public SuperExpressionSyntax(SourceSpan span) : base(span)
        {
        }
    }

    /// <summary>A binary operation. The operator is a field rather than a subclass; see <see cref="SyntaxNode"/>.</summary>
    public sealed class BinaryExpressionSyntax : ExpressionSyntax
    {
        /// <summary>Which operator this is.</summary>
        public BinaryOperator Operator { get; }

        /// <summary>The left operand.</summary>
        public ExpressionSyntax Left { get; }

        /// <summary>The right operand.</summary>
        public ExpressionSyntax Right { get; }

        /// <summary>Initializes a binary operation.</summary>
        /// <param name="span">The source the expression covers.</param>
        /// <param name="op">Which operator this is.</param>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        public BinaryExpressionSyntax(SourceSpan span, BinaryOperator op, ExpressionSyntax left, ExpressionSyntax right)
            : base(span)
        {
            Operator = op;
            Left = left;
            Right = right;
        }
    }

    /// <summary>A unary operation, prefix or postfix. <see cref="Operator"/> says which.</summary>
    public sealed class UnaryExpressionSyntax : ExpressionSyntax
    {
        /// <summary>Which operator this is, and whether it was prefix or postfix.</summary>
        public UnaryOperator Operator { get; }

        /// <summary>The operand.</summary>
        public ExpressionSyntax Operand { get; }

        /// <summary>Initializes a unary operation.</summary>
        /// <param name="span">The source the expression covers.</param>
        /// <param name="op">Which operator this is.</param>
        /// <param name="operand">The operand.</param>
        public UnaryExpressionSyntax(SourceSpan span, UnaryOperator op, ExpressionSyntax operand) : base(span)
        {
            Operator = op;
            Operand = operand;
        }
    }

    /// <summary>An assignment, including the compound forms.</summary>
    public sealed class AssignmentExpressionSyntax : ExpressionSyntax
    {
        /// <summary>Which assignment operator this is.</summary>
        public AssignmentOperator Operator { get; }

        /// <summary>The assignment target.</summary>
        public ExpressionSyntax Target { get; }

        /// <summary>The value being assigned.</summary>
        public ExpressionSyntax Value { get; }

        /// <summary>Initializes an assignment.</summary>
        /// <param name="span">The source the expression covers.</param>
        /// <param name="op">Which assignment operator this is.</param>
        /// <param name="target">The assignment target.</param>
        /// <param name="value">The value being assigned.</param>
        public AssignmentExpressionSyntax(SourceSpan span, AssignmentOperator op, ExpressionSyntax target, ExpressionSyntax value)
            : base(span)
        {
            Operator = op;
            Target = target;
            Value = value;
        }
    }

    /// <summary>The ternary conditional, <c>c ? a : b</c>.</summary>
    public sealed class ConditionalExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The condition.</summary>
        public ExpressionSyntax Condition { get; }

        /// <summary>The value when the condition holds.</summary>
        public ExpressionSyntax WhenTrue { get; }

        /// <summary>The value when it does not.</summary>
        public ExpressionSyntax WhenFalse { get; }

        /// <summary>Initializes a ternary conditional.</summary>
        /// <param name="span">The source the expression covers.</param>
        /// <param name="condition">The condition.</param>
        /// <param name="whenTrue">The value when the condition holds.</param>
        /// <param name="whenFalse">The value when it does not.</param>
        public ConditionalExpressionSyntax(SourceSpan span, ExpressionSyntax condition, ExpressionSyntax whenTrue, ExpressionSyntax whenFalse)
            : base(span)
        {
            Condition = condition;
            WhenTrue = whenTrue;
            WhenFalse = whenFalse;
        }
    }

    /// <summary>One argument at a call site, optionally named (§3.5).</summary>
    public sealed class ArgumentSyntax : SyntaxNode
    {
        /// <summary>The parameter name this argument was written against, or <c>null</c> when positional.</summary>
        public string? Name { get; }

        /// <summary>The argument's value.</summary>
        public ExpressionSyntax Value { get; }

        /// <summary>Initializes an argument.</summary>
        /// <param name="span">The source the argument covers.</param>
        /// <param name="name">The parameter name, or <c>null</c> when positional.</param>
        /// <param name="value">The argument's value.</param>
        public ArgumentSyntax(SourceSpan span, string? name, ExpressionSyntax value) : base(span)
        {
            Name = name;
            Value = value;
        }
    }

    /// <summary>A call. Constructing an instance is one of these too — §5.5 has no <c>new</c>.</summary>
    public sealed class CallExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The expression being called.</summary>
        public ExpressionSyntax Callee { get; }

        /// <summary>Explicit type arguments, empty when none were written.</summary>
        public IReadOnlyList<TypeSyntax> TypeArguments { get; }

        /// <summary>The arguments, in source order.</summary>
        public IReadOnlyList<ArgumentSyntax> Arguments { get; }

        /// <summary>Initializes a call.</summary>
        /// <param name="span">The source the expression covers.</param>
        /// <param name="callee">The expression being called.</param>
        /// <param name="typeArguments">Explicit type arguments, or an empty list.</param>
        /// <param name="arguments">The arguments, in source order.</param>
        public CallExpressionSyntax(SourceSpan span, ExpressionSyntax callee, IReadOnlyList<TypeSyntax> typeArguments, IReadOnlyList<ArgumentSyntax> arguments)
            : base(span)
        {
            Callee = callee;
            TypeArguments = typeArguments;
            Arguments = arguments;
        }
    }

    /// <summary>An index, <c>a[i]</c>. Always one-dimensional (§5.6).</summary>
    public sealed class IndexExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The expression being indexed.</summary>
        public ExpressionSyntax Target { get; }

        /// <summary>The index.</summary>
        public ExpressionSyntax Index { get; }

        /// <summary>Initializes an index expression.</summary>
        /// <param name="span">The source the expression covers.</param>
        /// <param name="target">The expression being indexed.</param>
        /// <param name="index">The index.</param>
        public IndexExpressionSyntax(SourceSpan span, ExpressionSyntax target, ExpressionSyntax index) : base(span)
        {
            Target = target;
            Index = index;
        }
    }

    /// <summary>Member access, <c>a.b</c> or <c>a?.b</c>.</summary>
    public sealed class MemberAccessExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The receiver.</summary>
        public ExpressionSyntax Target { get; }

        /// <summary>The member's name.</summary>
        public string Name { get; }

        /// <summary>True when written <c>?.</c>, which short-circuits to null instead of faulting.</summary>
        public bool IsNullConditional { get; }

        /// <summary>Initializes a member access.</summary>
        /// <param name="span">The source the expression covers.</param>
        /// <param name="target">The receiver.</param>
        /// <param name="name">The member's name.</param>
        /// <param name="isNullConditional">True when written <c>?.</c>.</param>
        public MemberAccessExpressionSyntax(SourceSpan span, ExpressionSyntax target, string name, bool isNullConditional)
            : base(span)
        {
            Target = target;
            Name = name;
            IsNullConditional = isNullConditional;
        }
    }

    /// <summary>An explicit cast, <c>x as T</c> or <c>x as? T</c>.</summary>
    public sealed class CastExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The value being cast.</summary>
        public ExpressionSyntax Operand { get; }

        /// <summary>The target type.</summary>
        public TypeSyntax TargetType { get; }

        /// <summary>True when written <c>as?</c>, which yields null on failure instead of throwing.</summary>
        public bool IsSafe { get; }

        /// <summary>Initializes a cast.</summary>
        /// <param name="span">The source the expression covers.</param>
        /// <param name="operand">The value being cast.</param>
        /// <param name="targetType">The target type.</param>
        /// <param name="isSafe">True when written <c>as?</c>.</param>
        public CastExpressionSyntax(SourceSpan span, ExpressionSyntax operand, TypeSyntax targetType, bool isSafe)
            : base(span)
        {
            Operand = operand;
            TargetType = targetType;
            IsSafe = isSafe;
        }
    }

    /// <summary>A type test, <c>x is T</c>. Does not narrow <c>x</c>; see §5.7.</summary>
    public sealed class TypeTestExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The value being tested.</summary>
        public ExpressionSyntax Operand { get; }

        /// <summary>The type being tested against.</summary>
        public TypeSyntax TargetType { get; }

        /// <summary>Initializes a type test.</summary>
        /// <param name="span">The source the expression covers.</param>
        /// <param name="operand">The value being tested.</param>
        /// <param name="targetType">The type being tested against.</param>
        public TypeTestExpressionSyntax(SourceSpan span, ExpressionSyntax operand, TypeSyntax targetType) : base(span)
        {
            Operand = operand;
            TargetType = targetType;
        }
    }

    /// <summary>
    /// <c>typeof(X)</c>: the compile-time-known type <c>X</c> names, or - when <c>X</c> is not a
    /// type name at all - the runtime type of the value it evaluates to. Exactly one of
    /// <see cref="TypeOperand"/> and <see cref="Operand"/> is set.
    /// </summary>
    /// <remarks>
    /// The parser only ever routes to <see cref="TypeOperand"/> for a shape that could never also
    /// be an expression - a generic argument list, since arbitrary Surtr expressions do not have
    /// one. Everything else, including a bare or dotted name that could equally be a value, parses
    /// as <see cref="Operand"/> through the ordinary expression grammar - a call, an arithmetic
    /// expression or a literal all need that grammar and none of it is valid type syntax, so unlike
    /// <c>is</c>/<c>as</c> (which only ever take a type on their right), <c>typeof</c> cannot parse
    /// its argument as a <see cref="TypeSyntax"/> unconditionally. Which of the two a bare name in
    /// <see cref="Operand"/> actually means is a scope question the parser has no way to answer, so
    /// it is left to the binder (see <c>BodyBinder.BindTypeOf</c>, which tries
    /// <c>TryBindAsType</c> first).
    /// </remarks>
    public sealed class TypeOfExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The type as written, when the shape could never also be a value (has generic arguments). <see langword="null"/> otherwise.</summary>
        public TypeSyntax? TypeOperand { get; }

        /// <summary>The operand as an expression, when the shape might also be a type name. <see langword="null"/> when <see cref="TypeOperand"/> is set.</summary>
        public ExpressionSyntax? Operand { get; }

        /// <summary>Initializes a <c>typeof</c> expression over an unambiguous type shape.</summary>
        /// <param name="span">The source the expression covers.</param>
        /// <param name="typeOperand">The type as written.</param>
        public TypeOfExpressionSyntax(SourceSpan span, TypeSyntax typeOperand) : base(span)
        {
            TypeOperand = typeOperand;
        }

        /// <summary>Initializes a <c>typeof</c> expression over an expression that might also name a type.</summary>
        /// <param name="span">The source the expression covers.</param>
        /// <param name="operand">The operand as an expression.</param>
        public TypeOfExpressionSyntax(SourceSpan span, ExpressionSyntax operand) : base(span)
        {
            Operand = operand;
        }
    }

    /// <summary>A lambda, <c>(x) =&gt; expr</c> or <c>(x) =&gt; { ... }</c>.</summary>
    public sealed class LambdaExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The parameters. A parameter's type may be absent when a target type supplies it (§5.9).</summary>
        public IReadOnlyList<ParameterSyntax> Parameters { get; }

        /// <summary>The body when written as a single expression, otherwise <c>null</c>.</summary>
        public ExpressionSyntax? Body { get; }

        /// <summary>The body when written as a block, otherwise <c>null</c>.</summary>
        public BlockStatementSyntax? BlockBody { get; }

        /// <summary>Initializes a lambda.</summary>
        /// <param name="span">The source the expression covers.</param>
        /// <param name="parameters">The parameters.</param>
        /// <param name="body">The expression body, or <c>null</c>.</param>
        /// <param name="blockBody">The block body, or <c>null</c>.</param>
        public LambdaExpressionSyntax(SourceSpan span, IReadOnlyList<ParameterSyntax> parameters, ExpressionSyntax? body, BlockStatementSyntax? blockBody)
            : base(span)
        {
            Parameters = parameters;
            Body = body;
            BlockBody = blockBody;
        }
    }

    /// <summary>An array literal, <c>[1, 2, 3]</c>.</summary>
    public sealed class ArrayLiteralExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The elements, in order. Empty means the type must come from context (§5.9).</summary>
        public IReadOnlyList<ExpressionSyntax> Elements { get; }

        /// <summary>Initializes an array literal.</summary>
        /// <param name="span">The source the literal covers.</param>
        /// <param name="elements">The elements, in order.</param>
        public ArrayLiteralExpressionSyntax(SourceSpan span, IReadOnlyList<ExpressionSyntax> elements) : base(span)
        {
            Elements = elements;
        }
    }

    /// <summary>One <c>key: value</c> pair of a dictionary literal.</summary>
    public sealed class DictEntrySyntax : SyntaxNode
    {
        /// <summary>The key.</summary>
        public ExpressionSyntax Key { get; }

        /// <summary>The value.</summary>
        public ExpressionSyntax Value { get; }

        /// <summary>Initializes a dictionary entry.</summary>
        /// <param name="span">The source the entry covers.</param>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        public DictEntrySyntax(SourceSpan span, ExpressionSyntax key, ExpressionSyntax value) : base(span)
        {
            Key = key;
            Value = value;
        }
    }

    /// <summary>A dictionary literal, <c>{ "a": 1 }</c>. Legal in expression position only (§5.4).</summary>
    public sealed class DictLiteralExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The entries, in order.</summary>
        public IReadOnlyList<DictEntrySyntax> Entries { get; }

        /// <summary>Initializes a dictionary literal.</summary>
        /// <param name="span">The source the literal covers.</param>
        /// <param name="entries">The entries, in order.</param>
        public DictLiteralExpressionSyntax(SourceSpan span, IReadOnlyList<DictEntrySyntax> entries) : base(span)
        {
            Entries = entries;
        }
    }

    /// <summary>A tuple literal, <c>(1, "a")</c>. Always two or more elements — one would be a parenthesized expression.</summary>
    public sealed class TupleLiteralExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The elements, in order.</summary>
        public IReadOnlyList<ExpressionSyntax> Elements { get; }

        /// <summary>Initializes a tuple literal.</summary>
        /// <param name="span">The source the literal covers.</param>
        /// <param name="elements">The elements, in order.</param>
        public TupleLiteralExpressionSyntax(SourceSpan span, IReadOnlyList<ExpressionSyntax> elements) : base(span)
        {
            Elements = elements;
        }
    }

    /// <summary>One arm of a switch expression: <c>1, 2 -&gt; "x"</c> or <c>else -&gt; "y"</c>.</summary>
    public sealed class SwitchExpressionArmSyntax : SyntaxNode
    {
        /// <summary>The values this arm matches. Empty means it is the <c>else</c> arm.</summary>
        public IReadOnlyList<ExpressionSyntax> Values { get; }

        /// <summary>The arm's result.</summary>
        public ExpressionSyntax Result { get; }

        /// <summary>True when this is the <c>else</c> arm.</summary>
        public bool IsElse => Values.Count == 0;

        /// <summary>Initializes a switch-expression arm.</summary>
        /// <param name="span">The source the arm covers.</param>
        /// <param name="values">The values matched, or an empty list for <c>else</c>.</param>
        /// <param name="result">The arm's result.</param>
        public SwitchExpressionArmSyntax(SourceSpan span, IReadOnlyList<ExpressionSyntax> values, ExpressionSyntax result)
            : base(span)
        {
            Values = values;
            Result = result;
        }
    }

    /// <summary>The expression form of <c>switch</c> (§4.3), which produces a value and does not fall through.</summary>
    public sealed class SwitchExpressionSyntax : ExpressionSyntax
    {
        /// <summary>The value being switched on.</summary>
        public ExpressionSyntax Subject { get; }

        /// <summary>The arms, in source order.</summary>
        public IReadOnlyList<SwitchExpressionArmSyntax> Arms { get; }

        /// <summary>Initializes a switch expression.</summary>
        /// <param name="span">The source the expression covers.</param>
        /// <param name="subject">The value being switched on.</param>
        /// <param name="arms">The arms, in source order.</param>
        public SwitchExpressionSyntax(SourceSpan span, ExpressionSyntax subject, IReadOnlyList<SwitchExpressionArmSyntax> arms)
            : base(span)
        {
            Subject = subject;
            Arms = arms;
        }
    }
}
