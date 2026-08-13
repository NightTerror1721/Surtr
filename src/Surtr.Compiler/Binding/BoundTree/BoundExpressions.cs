#nullable enable

using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding.BoundTree
{
    /// <summary>
    /// The base of the tree binding produces: the source's shape with every name resolved, every
    /// type known, and every conversion made explicit.
    /// </summary>
    /// <remarks>
    /// A bound node keeps the syntax it came from, so a later pass — code generation, or a
    /// diagnostic about something only it can see — can still point at the source. It keeps nothing
    /// else of the syntax: a name that resolved is a symbol here, not a string.
    /// </remarks>
    public abstract class BoundNode
    {
        private protected BoundNode(SyntaxNode syntax) => Syntax = syntax;

        /// <summary>The syntax this came from.</summary>
        public SyntaxNode Syntax { get; }

        /// <summary>Where in the source it is.</summary>
        public SourceSpan Span => Syntax.Span;
    }

    /// <summary>An expression, which always has a type.</summary>
    /// <remarks>
    /// Even a failed one: binding never returns null, so an expression that could not be understood
    /// is a <see cref="BoundErrorExpression"/> typed as the error type, and every rule that touches
    /// it stays quiet.
    /// </remarks>
    public abstract class BoundExpression : BoundNode
    {
        private protected BoundExpression(SyntaxNode syntax, TypeSymbol type) : base(syntax) => Type = type;

        /// <summary>What the expression evaluates to.</summary>
        public TypeSymbol Type { get; }

        /// <summary>Whether this expression can be assigned to.</summary>
        public virtual bool IsAssignable => false;
    }

    /// <summary>An expression that could not be bound. Reported once, then silent.</summary>
    public sealed class BoundErrorExpression : BoundExpression
    {
        internal BoundErrorExpression(SyntaxNode syntax, TypeSymbol type) : base(syntax, type)
        {
        }
    }

    /// <summary>A literal, with its value already parsed by the lexer.</summary>
    public sealed class BoundLiteralExpression : BoundExpression
    {
        internal BoundLiteralExpression(SyntaxNode syntax, TypeSymbol type, object? value) : base(syntax, type)
            => Value = value;

        /// <summary>The value, or <see langword="null"/> for the null literal.</summary>
        public object? Value { get; }

        /// <summary>Whether this is the null literal (§5.1), legal only against a nullable type.</summary>
        public bool IsNull => Value is null;
    }

    /// <summary>An interpolated string, already split into the parts that make it.</summary>
    public sealed class BoundInterpolatedStringExpression : BoundExpression
    {
        internal BoundInterpolatedStringExpression(SyntaxNode syntax, TypeSymbol type, IReadOnlyList<BoundExpression> parts)
            : base(syntax, type)
            => Parts = parts;

        /// <summary>The literal and interpolated pieces, in order.</summary>
        public IReadOnlyList<BoundExpression> Parts { get; }
    }

    /// <summary>A read of a local variable or a parameter.</summary>
    public sealed class BoundLocalExpression : BoundExpression
    {
        internal BoundLocalExpression(SyntaxNode syntax, LocalSymbol local) : base(syntax, local.Type)
            => Local = local;

        /// <summary>The local being read.</summary>
        public LocalSymbol Local { get; }

        /// <inheritdoc/>
        public override bool IsAssignable => !Local.IsReadOnly;
    }

    /// <summary>A read of a parameter.</summary>
    public sealed class BoundParameterExpression : BoundExpression
    {
        internal BoundParameterExpression(SyntaxNode syntax, ParameterSymbol parameter) : base(syntax, parameter.Type)
            => Parameter = parameter;

        /// <summary>The parameter being read.</summary>
        public ParameterSymbol Parameter { get; }

        /// <inheritdoc/>
        public override bool IsAssignable => true;
    }

    /// <summary>The receiver of an instance member, written or implied.</summary>
    public sealed class BoundThisExpression : BoundExpression
    {
        internal BoundThisExpression(SyntaxNode syntax, TypeSymbol type, bool isSuper) : base(syntax, type)
            => IsSuper = isSuper;

        /// <summary>Whether it was written <c>super</c>, which pins dispatch to the base class.</summary>
        public bool IsSuper { get; }
    }

    /// <summary>A field read.</summary>
    public sealed class BoundFieldExpression : BoundExpression
    {
        internal BoundFieldExpression(SyntaxNode syntax, BoundExpression? receiver, FieldSymbol field)
            : base(syntax, field.Type)
        {
            Receiver = receiver;
            Field = field;
        }

        /// <summary>The instance it is read from, or <see langword="null"/> for a static.</summary>
        public BoundExpression? Receiver { get; }

        /// <summary>The field.</summary>
        public FieldSymbol Field { get; }

        /// <inheritdoc/>
        public override bool IsAssignable => !Field.IsReadOnly;
    }

    /// <summary>A property read, which becomes a call to its getter.</summary>
    public sealed class BoundPropertyExpression : BoundExpression
    {
        internal BoundPropertyExpression(SyntaxNode syntax, BoundExpression? receiver, PropertySymbol property)
            : base(syntax, property.Type)
        {
            Receiver = receiver;
            Property = property;
        }

        /// <summary>The instance it is read from, or <see langword="null"/> for a static.</summary>
        public BoundExpression? Receiver { get; }

        /// <summary>The property.</summary>
        public PropertySymbol Property { get; }

        /// <inheritdoc/>
        public override bool IsAssignable => Property.Setter is not null;
    }

    /// <summary>A call to a method the call site resolved to.</summary>
    public sealed class BoundCallExpression : BoundExpression
    {
        internal BoundCallExpression(
            SyntaxNode syntax,
            BoundExpression? receiver,
            MethodSymbol method,
            IReadOnlyList<BoundExpression> arguments,
            bool isVirtual)
            : base(syntax, method.ReturnType)
        {
            Receiver = receiver;
            Method = method;
            Arguments = arguments;
            IsVirtual = isVirtual;
        }

        /// <summary>The receiver, or <see langword="null"/> for a static or module-level call.</summary>
        public BoundExpression? Receiver { get; }

        /// <summary>The method the call resolved to.</summary>
        public MethodSymbol Method { get; }

        /// <summary>
        /// The arguments, already in parameter order with defaults filled in and varargs collected.
        /// </summary>
        public IReadOnlyList<BoundExpression> Arguments { get; }

        /// <summary>
        /// Whether the call goes through the vtable. False for a <c>super</c> call and for anything
        /// on a sealed receiver, which is the devirtualisation §2.2 mentions.
        /// </summary>
        public bool IsVirtual { get; }
    }

    /// <summary>A closure held in a value being invoked (§8).</summary>
    /// <remarks>
    /// Separate from <see cref="BoundCallExpression"/> because there is no method to name: the
    /// callee is a value whose closure type says what it takes and returns, and dispatch reads the
    /// payload the closure carries rather than any call site's token.
    /// </remarks>
    public sealed class BoundClosureInvocationExpression : BoundExpression
    {
        internal BoundClosureInvocationExpression(
            SyntaxNode syntax,
            BoundExpression callee,
            IReadOnlyList<BoundExpression> arguments,
            TypeSymbol type)
            : base(syntax, type)
        {
            Callee = callee;
            Arguments = arguments;
        }

        /// <summary>The value holding the closure.</summary>
        public BoundExpression Callee { get; }

        /// <summary>The arguments, in order.</summary>
        public IReadOnlyList<BoundExpression> Arguments { get; }
    }

    /// <summary>An object being created: <c>Vec2(1.0, 2.0)</c>.</summary>
    public sealed class BoundObjectCreationExpression : BoundExpression
    {
        internal BoundObjectCreationExpression(
            SyntaxNode syntax,
            NamedTypeSymbol type,
            MethodSymbol? constructor,
            IReadOnlyList<BoundExpression> arguments)
            : base(syntax, type)
        {
            Constructor = constructor;
            Arguments = arguments;
        }

        /// <summary>The constructor, or <see langword="null"/> for a type that declares none.</summary>
        public MethodSymbol? Constructor { get; }

        /// <summary>The arguments, in parameter order.</summary>
        public IReadOnlyList<BoundExpression> Arguments { get; }
    }

    /// <summary>A built-in binary operation between two primitives, strings or references.</summary>
    public sealed class BoundBinaryExpression : BoundExpression
    {
        internal BoundBinaryExpression(
            SyntaxNode syntax,
            BinaryOperator @operator,
            BoundExpression left,
            BoundExpression right,
            TypeSymbol type)
            : base(syntax, type)
        {
            Operator = @operator;
            Left = left;
            Right = right;
        }

        /// <summary>Which operation.</summary>
        public BinaryOperator Operator { get; }

        /// <summary>The left operand.</summary>
        public BoundExpression Left { get; }

        /// <summary>The right operand.</summary>
        public BoundExpression Right { get; }
    }

    /// <summary>A built-in unary operation.</summary>
    public sealed class BoundUnaryExpression : BoundExpression
    {
        internal BoundUnaryExpression(SyntaxNode syntax, UnaryOperator @operator, BoundExpression operand, TypeSymbol type)
            : base(syntax, type)
        {
            Operator = @operator;
            Operand = operand;
        }

        /// <summary>Which operation.</summary>
        public UnaryOperator Operator { get; }

        /// <summary>The operand.</summary>
        public BoundExpression Operand { get; }
    }

    /// <summary>An assignment, with any compound operator already applied to the value.</summary>
    public sealed class BoundAssignmentExpression : BoundExpression
    {
        internal BoundAssignmentExpression(SyntaxNode syntax, BoundExpression target, BoundExpression value)
            : base(syntax, target.Type)
        {
            Target = target;
            Value = value;
        }

        /// <summary>What is assigned to.</summary>
        public BoundExpression Target { get; }

        /// <summary>
        /// The value assigned. A compound assignment is already expanded, so <c>x += 1</c> arrives
        /// here as <c>x + 1</c> and nothing downstream needs a second form of assignment.
        /// </summary>
        public BoundExpression Value { get; }
    }

    /// <summary>A conditional expression, or the null-coalescing form.</summary>
    public sealed class BoundConditionalExpression : BoundExpression
    {
        internal BoundConditionalExpression(
            SyntaxNode syntax,
            BoundExpression condition,
            BoundExpression whenTrue,
            BoundExpression whenFalse,
            TypeSymbol type)
            : base(syntax, type)
        {
            Condition = condition;
            WhenTrue = whenTrue;
            WhenFalse = whenFalse;
        }

        /// <summary>The condition.</summary>
        public BoundExpression Condition { get; }

        /// <summary>The value when it holds.</summary>
        public BoundExpression WhenTrue { get; }

        /// <summary>The value when it does not.</summary>
        public BoundExpression WhenFalse { get; }
    }

    /// <summary>An indexed read: <c>xs[i]</c>.</summary>
    public sealed class BoundIndexExpression : BoundExpression
    {
        internal BoundIndexExpression(SyntaxNode syntax, BoundExpression target, BoundExpression index, TypeSymbol type)
            : base(syntax, type)
        {
            Target = target;
            Index = index;
        }

        /// <summary>What is being indexed.</summary>
        public BoundExpression Target { get; }

        /// <summary>The index.</summary>
        public BoundExpression Index { get; }

        /// <inheritdoc/>
        public override bool IsAssignable => true;
    }

    /// <summary>A conversion made explicit, whether or not it was written.</summary>
    public sealed class BoundConversionExpression : BoundExpression
    {
        internal BoundConversionExpression(
            SyntaxNode syntax,
            BoundExpression operand,
            TypeSymbol type,
            Conversion conversion,
            bool isExplicit)
            : base(syntax, type)
        {
            Operand = operand;
            Conversion = conversion;
            IsExplicit = isExplicit;
        }

        /// <summary>The value being converted.</summary>
        public BoundExpression Operand { get; }

        /// <summary>Which conversion applies.</summary>
        public Conversion Conversion { get; }

        /// <summary>Whether the source wrote it, rather than the binder inserting it.</summary>
        public bool IsExplicit { get; }

        /// <summary>Whether a failed conversion yields null instead of throwing (<c>as?</c>).</summary>
        public bool IsSafe { get; internal set; }
    }

    /// <summary>An <c>is</c> test, which evaluates to <c>bool</c> and casts nothing.</summary>
    public sealed class BoundTypeTestExpression : BoundExpression
    {
        internal BoundTypeTestExpression(SyntaxNode syntax, BoundExpression operand, TypeSymbol testedType, TypeSymbol type)
            : base(syntax, type)
        {
            Operand = operand;
            TestedType = testedType;
        }

        /// <summary>The value being tested.</summary>
        public BoundExpression Operand { get; }

        /// <summary>The type it is tested against.</summary>
        public TypeSymbol TestedType { get; }
    }

    /// <summary>A lambda, whose body is lifted to a static synthetic method at emit.</summary>
    public sealed class BoundLambdaExpression : BoundExpression
    {
        internal BoundLambdaExpression(
            SyntaxNode syntax,
            TypeSymbol type,
            IReadOnlyList<ParameterSymbol> parameters,
            BoundStatement body,
            IReadOnlyList<Symbol> captured)
            : base(syntax, type)
        {
            Parameters = parameters;
            Body = body;
            Captured = captured;
        }

        /// <summary>Its parameters.</summary>
        public IReadOnlyList<ParameterSymbol> Parameters { get; }

        /// <summary>Its body, always a statement so an expression body and a block are one shape.</summary>
        public BoundStatement Body { get; }

        /// <summary>
        /// The locals and parameters it reads from outside itself, which travel as construction
        /// arguments to the closure.
        /// </summary>
        public IReadOnlyList<Symbol> Captured { get; }
    }

    /// <summary>An array literal.</summary>
    public sealed class BoundArrayLiteralExpression : BoundExpression
    {
        internal BoundArrayLiteralExpression(SyntaxNode syntax, TypeSymbol type, IReadOnlyList<BoundExpression> elements)
            : base(syntax, type)
            => Elements = elements;

        /// <summary>The elements, in order.</summary>
        public IReadOnlyList<BoundExpression> Elements { get; }
    }

    /// <summary>A tuple literal.</summary>
    public sealed class BoundTupleLiteralExpression : BoundExpression
    {
        internal BoundTupleLiteralExpression(SyntaxNode syntax, TypeSymbol type, IReadOnlyList<BoundExpression> elements)
            : base(syntax, type)
            => Elements = elements;

        /// <summary>The elements, in order.</summary>
        public IReadOnlyList<BoundExpression> Elements { get; }
    }

    /// <summary>One key/value pair of a dictionary literal.</summary>
    public readonly struct BoundDictEntry
    {
        internal BoundDictEntry(BoundExpression key, BoundExpression value)
        {
            Key = key;
            Value = value;
        }

        /// <summary>The key.</summary>
        public BoundExpression Key { get; }

        /// <summary>The value.</summary>
        public BoundExpression Value { get; }
    }

    /// <summary>A dictionary literal.</summary>
    public sealed class BoundDictLiteralExpression : BoundExpression
    {
        internal BoundDictLiteralExpression(SyntaxNode syntax, TypeSymbol type, IReadOnlyList<BoundDictEntry> entries)
            : base(syntax, type)
            => Entries = entries;

        /// <summary>The entries, in order.</summary>
        public IReadOnlyList<BoundDictEntry> Entries { get; }
    }

    /// <summary>One arm of a switch expression.</summary>
    public sealed class BoundSwitchArm
    {
        internal BoundSwitchArm(IReadOnlyList<BoundExpression> values, BoundExpression result)
        {
            Values = values;
            Result = result;
        }

        /// <summary>The values it matches, empty for the default arm.</summary>
        public IReadOnlyList<BoundExpression> Values { get; }

        /// <summary>What it evaluates to.</summary>
        public BoundExpression Result { get; }

        /// <summary>Whether this is the arm nothing else matched.</summary>
        public bool IsDefault => Values.Count == 0;
    }

    /// <summary>A switch expression.</summary>
    public sealed class BoundSwitchExpression : BoundExpression
    {
        internal BoundSwitchExpression(
            SyntaxNode syntax,
            BoundExpression subject,
            IReadOnlyList<BoundSwitchArm> arms,
            TypeSymbol type)
            : base(syntax, type)
        {
            Subject = subject;
            Arms = arms;
        }

        /// <summary>What is being switched on.</summary>
        public BoundExpression Subject { get; }

        /// <summary>The arms, in order.</summary>
        public IReadOnlyList<BoundSwitchArm> Arms { get; }
    }
}
