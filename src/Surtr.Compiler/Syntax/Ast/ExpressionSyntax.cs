#nullable enable

using System.Collections.Generic;

namespace Surtr.Compiler.Syntax.Ast
{
    /// <summary>Base of every expression.</summary>
    public abstract class ExpressionSyntax : SyntaxNode
    {
        /// <summary>Initializes the node with the position it starts at.</summary>
        /// <param name="location">Where in the source the expression begins.</param>
        protected ExpressionSyntax(SourceLocation location) : base(location)
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
        public LiteralExpressionSyntax(Token literal) : base(literal.Location)
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
        /// <param name="location">Where in the source the literal begins.</param>
        /// <param name="parts">The alternating text and expression parts.</param>
        public InterpolatedStringExpressionSyntax(SourceLocation location, IReadOnlyList<ExpressionSyntax> parts)
            : base(location)
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
        /// <param name="location">Where in the source the identifier begins.</param>
        /// <param name="name">The identifier's text.</param>
        public IdentifierExpressionSyntax(SourceLocation location, string name) : base(location)
        {
            Name = name;
        }
    }

    /// <summary>The contextual keyword <c>this</c> — the receiver of an instance member.</summary>
    public sealed class ThisExpressionSyntax : ExpressionSyntax
    {
        /// <summary>Initializes a <c>this</c> reference.</summary>
        /// <param name="location">Where in the source it begins.</param>
        public ThisExpressionSyntax(SourceLocation location) : base(location)
        {
        }
    }

    /// <summary>The contextual keyword <c>super</c> — the base-class receiver.</summary>
    public sealed class SuperExpressionSyntax : ExpressionSyntax
    {
        /// <summary>Initializes a <c>super</c> reference.</summary>
        /// <param name="location">Where in the source it begins.</param>
        public SuperExpressionSyntax(SourceLocation location) : base(location)
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
        /// <param name="location">Where in the source the expression begins.</param>
        /// <param name="op">Which operator this is.</param>
        /// <param name="left">The left operand.</param>
        /// <param name="right">The right operand.</param>
        public BinaryExpressionSyntax(SourceLocation location, BinaryOperator op, ExpressionSyntax left, ExpressionSyntax right)
            : base(location)
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
        /// <param name="location">Where in the source the expression begins.</param>
        /// <param name="op">Which operator this is.</param>
        /// <param name="operand">The operand.</param>
        public UnaryExpressionSyntax(SourceLocation location, UnaryOperator op, ExpressionSyntax operand) : base(location)
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
        /// <param name="location">Where in the source the expression begins.</param>
        /// <param name="op">Which assignment operator this is.</param>
        /// <param name="target">The assignment target.</param>
        /// <param name="value">The value being assigned.</param>
        public AssignmentExpressionSyntax(SourceLocation location, AssignmentOperator op, ExpressionSyntax target, ExpressionSyntax value)
            : base(location)
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
        /// <param name="location">Where in the source the expression begins.</param>
        /// <param name="condition">The condition.</param>
        /// <param name="whenTrue">The value when the condition holds.</param>
        /// <param name="whenFalse">The value when it does not.</param>
        public ConditionalExpressionSyntax(SourceLocation location, ExpressionSyntax condition, ExpressionSyntax whenTrue, ExpressionSyntax whenFalse)
            : base(location)
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
        /// <param name="location">Where in the source the argument begins.</param>
        /// <param name="name">The parameter name, or <c>null</c> when positional.</param>
        /// <param name="value">The argument's value.</param>
        public ArgumentSyntax(SourceLocation location, string? name, ExpressionSyntax value) : base(location)
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
        /// <param name="location">Where in the source the expression begins.</param>
        /// <param name="callee">The expression being called.</param>
        /// <param name="typeArguments">Explicit type arguments, or an empty list.</param>
        /// <param name="arguments">The arguments, in source order.</param>
        public CallExpressionSyntax(SourceLocation location, ExpressionSyntax callee, IReadOnlyList<TypeSyntax> typeArguments, IReadOnlyList<ArgumentSyntax> arguments)
            : base(location)
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
        /// <param name="location">Where in the source the expression begins.</param>
        /// <param name="target">The expression being indexed.</param>
        /// <param name="index">The index.</param>
        public IndexExpressionSyntax(SourceLocation location, ExpressionSyntax target, ExpressionSyntax index) : base(location)
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
        /// <param name="location">Where in the source the expression begins.</param>
        /// <param name="target">The receiver.</param>
        /// <param name="name">The member's name.</param>
        /// <param name="isNullConditional">True when written <c>?.</c>.</param>
        public MemberAccessExpressionSyntax(SourceLocation location, ExpressionSyntax target, string name, bool isNullConditional)
            : base(location)
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
        /// <param name="location">Where in the source the expression begins.</param>
        /// <param name="operand">The value being cast.</param>
        /// <param name="targetType">The target type.</param>
        /// <param name="isSafe">True when written <c>as?</c>.</param>
        public CastExpressionSyntax(SourceLocation location, ExpressionSyntax operand, TypeSyntax targetType, bool isSafe)
            : base(location)
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
        /// <param name="location">Where in the source the expression begins.</param>
        /// <param name="operand">The value being tested.</param>
        /// <param name="targetType">The type being tested against.</param>
        public TypeTestExpressionSyntax(SourceLocation location, ExpressionSyntax operand, TypeSyntax targetType) : base(location)
        {
            Operand = operand;
            TargetType = targetType;
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
        /// <param name="location">Where in the source the expression begins.</param>
        /// <param name="parameters">The parameters.</param>
        /// <param name="body">The expression body, or <c>null</c>.</param>
        /// <param name="blockBody">The block body, or <c>null</c>.</param>
        public LambdaExpressionSyntax(SourceLocation location, IReadOnlyList<ParameterSyntax> parameters, ExpressionSyntax? body, BlockStatementSyntax? blockBody)
            : base(location)
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
        /// <param name="location">Where in the source the literal begins.</param>
        /// <param name="elements">The elements, in order.</param>
        public ArrayLiteralExpressionSyntax(SourceLocation location, IReadOnlyList<ExpressionSyntax> elements) : base(location)
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
        /// <param name="location">Where in the source the entry begins.</param>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        public DictEntrySyntax(SourceLocation location, ExpressionSyntax key, ExpressionSyntax value) : base(location)
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
        /// <param name="location">Where in the source the literal begins.</param>
        /// <param name="entries">The entries, in order.</param>
        public DictLiteralExpressionSyntax(SourceLocation location, IReadOnlyList<DictEntrySyntax> entries) : base(location)
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
        /// <param name="location">Where in the source the literal begins.</param>
        /// <param name="elements">The elements, in order.</param>
        public TupleLiteralExpressionSyntax(SourceLocation location, IReadOnlyList<ExpressionSyntax> elements) : base(location)
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
        /// <param name="location">Where in the source the arm begins.</param>
        /// <param name="values">The values matched, or an empty list for <c>else</c>.</param>
        /// <param name="result">The arm's result.</param>
        public SwitchExpressionArmSyntax(SourceLocation location, IReadOnlyList<ExpressionSyntax> values, ExpressionSyntax result)
            : base(location)
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
        /// <param name="location">Where in the source the expression begins.</param>
        /// <param name="subject">The value being switched on.</param>
        /// <param name="arms">The arms, in source order.</param>
        public SwitchExpressionSyntax(SourceLocation location, ExpressionSyntax subject, IReadOnlyList<SwitchExpressionArmSyntax> arms)
            : base(location)
        {
            Subject = subject;
            Arms = arms;
        }
    }
}
