#nullable enable

using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax.Ast;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding.BoundTree
{
    /// <summary>A statement.</summary>
    public abstract class BoundStatement : BoundNode
    {
        private protected BoundStatement(SyntaxNode syntax) : base(syntax)
        {
        }
    }

    /// <summary>A statement that binds to nothing at all, such as a lone <c>;</c>.</summary>
    public sealed class BoundNopStatement : BoundStatement
    {
        internal BoundNopStatement(SyntaxNode syntax) : base(syntax)
        {
        }
    }

    /// <summary>A braced block, which is also a scope for the locals declared inside it.</summary>
    public sealed class BoundBlockStatement : BoundStatement
    {
        internal BoundBlockStatement(SyntaxNode syntax, IReadOnlyList<BoundStatement> statements) : base(syntax)
            => Statements = statements;

        /// <summary>The statements, in order.</summary>
        public IReadOnlyList<BoundStatement> Statements { get; }
    }

    /// <summary>An expression evaluated for its effect.</summary>
    public sealed class BoundExpressionStatement : BoundStatement
    {
        internal BoundExpressionStatement(SyntaxNode syntax, BoundExpression expression) : base(syntax)
            => Expression = expression;

        /// <summary>The expression.</summary>
        public BoundExpression Expression { get; }
    }

    /// <summary>A local declaration, with its type either written or inferred.</summary>
    public sealed class BoundLocalDeclarationStatement : BoundStatement
    {
        internal BoundLocalDeclarationStatement(SyntaxNode syntax, LocalSymbol local, BoundExpression? initializer)
            : base(syntax)
        {
            Local = local;
            Initializer = initializer;
        }

        /// <summary>The local being declared.</summary>
        public LocalSymbol Local { get; }

        /// <summary>Its initial value, if one was written.</summary>
        public BoundExpression? Initializer { get; }
    }

    /// <summary>An <c>if</c>, with its condition already checked to be a <c>bool</c>.</summary>
    public sealed class BoundIfStatement : BoundStatement
    {
        internal BoundIfStatement(
            SyntaxNode syntax,
            BoundExpression condition,
            BoundStatement then,
            BoundStatement? otherwise)
            : base(syntax)
        {
            Condition = condition;
            Then = then;
            Else = otherwise;
        }

        /// <summary>The condition.</summary>
        public BoundExpression Condition { get; }

        /// <summary>What runs when it holds.</summary>
        public BoundStatement Then { get; }

        /// <summary>What runs when it does not, if anything.</summary>
        public BoundStatement? Else { get; }
    }

    /// <summary>A <c>while</c> loop.</summary>
    public sealed class BoundWhileStatement : BoundStatement
    {
        internal BoundWhileStatement(SyntaxNode syntax, BoundExpression condition, BoundStatement body) : base(syntax)
        {
            Condition = condition;
            Body = body;
        }

        /// <summary>The condition.</summary>
        public BoundExpression Condition { get; }

        /// <summary>The body.</summary>
        public BoundStatement Body { get; }
    }

    /// <summary>A three-clause <c>for</c> loop.</summary>
    public sealed class BoundForStatement : BoundStatement
    {
        internal BoundForStatement(
            SyntaxNode syntax,
            BoundStatement? initializer,
            BoundExpression? condition,
            BoundExpression? step,
            BoundStatement body)
            : base(syntax)
        {
            Initializer = initializer;
            Condition = condition;
            Step = step;
            Body = body;
        }

        /// <summary>What runs once before the loop.</summary>
        public BoundStatement? Initializer { get; }

        /// <summary>The condition, or <see langword="null"/> for an unconditional loop.</summary>
        public BoundExpression? Condition { get; }

        /// <summary>What runs after each iteration.</summary>
        public BoundExpression? Step { get; }

        /// <summary>The body.</summary>
        public BoundStatement Body { get; }
    }

    /// <summary>A <c>for-in</c> loop over something iterable (§4.2).</summary>
    /// <remarks>
    /// Kept as one node rather than lowered here. A built-in collection lowers to an indexed loop
    /// and allocates no cursor, while anything else goes through <c>iterate()</c> — and which of
    /// those applies is a code-generation decision, not a binding one.
    /// </remarks>
    public sealed class BoundForInStatement : BoundStatement
    {
        internal BoundForInStatement(
            SyntaxNode syntax,
            LocalSymbol variable,
            BoundExpression sequence,
            BoundStatement body)
            : base(syntax)
        {
            Variable = variable;
            Sequence = sequence;
            Body = body;
        }

        /// <summary>The loop variable.</summary>
        public LocalSymbol Variable { get; }

        /// <summary>What is being walked.</summary>
        public BoundExpression Sequence { get; }

        /// <summary>The body.</summary>
        public BoundStatement Body { get; }
    }

    /// <summary>One section of a switch statement.</summary>
    public sealed class BoundSwitchSection
    {
        internal BoundSwitchSection(IReadOnlyList<BoundExpression> labels, IReadOnlyList<BoundStatement> statements)
        {
            Labels = labels;
            Statements = statements;
        }

        /// <summary>The values it matches, empty for the default section.</summary>
        public IReadOnlyList<BoundExpression> Labels { get; }

        /// <summary>Its statements.</summary>
        public IReadOnlyList<BoundStatement> Statements { get; }

        /// <summary>Whether this is the section nothing else matched.</summary>
        public bool IsDefault => Labels.Count == 0;
    }

    /// <summary>A <c>switch</c> statement.</summary>
    public sealed class BoundSwitchStatement : BoundStatement
    {
        internal BoundSwitchStatement(SyntaxNode syntax, BoundExpression subject, IReadOnlyList<BoundSwitchSection> sections)
            : base(syntax)
        {
            Subject = subject;
            Sections = sections;
        }

        /// <summary>What is being switched on.</summary>
        public BoundExpression Subject { get; }

        /// <summary>The sections, in order.</summary>
        public IReadOnlyList<BoundSwitchSection> Sections { get; }
    }

    /// <summary>One <c>catch</c> clause.</summary>
    public sealed class BoundCatchClause
    {
        internal BoundCatchClause(LocalSymbol exception, BoundStatement body)
        {
            Exception = exception;
            Body = body;
        }

        /// <summary>The local the caught object binds to, carrying the type that was matched.</summary>
        public LocalSymbol Exception { get; }

        /// <summary>The handler.</summary>
        public BoundStatement Body { get; }
    }

    /// <summary>A <c>try</c>, with its handlers and its <c>finally</c>.</summary>
    /// <remarks>
    /// <c>finally</c> stays a child here and is duplicated onto every exit path at emit, which is
    /// what keeps <c>Leave</c>/<c>EndFinally</c> out of the instruction set.
    /// </remarks>
    public sealed class BoundTryStatement : BoundStatement
    {
        internal BoundTryStatement(
            SyntaxNode syntax,
            BoundStatement body,
            IReadOnlyList<BoundCatchClause> catches,
            BoundStatement? finallyBlock)
            : base(syntax)
        {
            Body = body;
            Catches = catches;
            Finally = finallyBlock;
        }

        /// <summary>The protected block.</summary>
        public BoundStatement Body { get; }

        /// <summary>The handlers, in order.</summary>
        public IReadOnlyList<BoundCatchClause> Catches { get; }

        /// <summary>The block that runs on every exit path, if one was written.</summary>
        public BoundStatement? Finally { get; }
    }

    /// <summary>A <c>throw</c>.</summary>
    public sealed class BoundThrowStatement : BoundStatement
    {
        internal BoundThrowStatement(SyntaxNode syntax, BoundExpression value) : base(syntax) => Value = value;

        /// <summary>What is thrown.</summary>
        public BoundExpression Value { get; }
    }

    /// <summary>A <c>return</c>.</summary>
    public sealed class BoundReturnStatement : BoundStatement
    {
        internal BoundReturnStatement(SyntaxNode syntax, BoundExpression? value) : base(syntax) => Value = value;

        /// <summary>The value returned, or <see langword="null"/> from a void method.</summary>
        public BoundExpression? Value { get; }
    }

    /// <summary>A <c>break</c> or a <c>continue</c>.</summary>
    public sealed class BoundBreakStatement : BoundStatement
    {
        internal BoundBreakStatement(SyntaxNode syntax, bool isContinue, string? label) : base(syntax)
        {
            IsContinue = isContinue;
            Label = label;
        }

        /// <summary>Whether it continues rather than breaks.</summary>
        public bool IsContinue { get; }

        /// <summary>The loop it names, if it names one.</summary>
        public string? Label { get; }
    }

    /// <summary>One field's initial value, bound but not attached to any body.</summary>
    /// <remarks>
    /// <para>
    /// An initializer is not a statement in a method — it is written on the declaration — but it
    /// runs, and where it runs depends on what the field is: a static one from its declaring type's
    /// initializer, an instance one from the top of every constructor, and an enum case from the
    /// enum's own initializer. Keeping them in a list of their own rather than splicing them into a
    /// body during binding is what lets code generation decide that, which is where the decision
    /// belongs.
    /// </para>
    /// <para>
    /// An enum case is one of these too: <see cref="Value"/> is the construction and
    /// <see cref="Field"/> is the static that holds it, which is exactly what §2.4 says a case is.
    /// </para>
    /// </remarks>
    public sealed class BoundFieldInitializer
    {
        internal BoundFieldInitializer(FieldSymbol field, BoundExpression value, NamedTypeSymbol? declaringType, int order = 0)
        {
            Field = field;
            Value = value;
            DeclaringType = declaringType;
            Order = order;
        }

        /// <summary>The field being given a value.</summary>
        public FieldSymbol Field { get; }

        /// <summary>What it is set to.</summary>
        public BoundExpression Value { get; }

        /// <summary>The type declaring it, or <see langword="null"/> for a module-level variable.</summary>
        public NamedTypeSymbol? DeclaringType { get; }

        /// <summary>
        /// Where the declaration sat among its container's other initializers.
        /// </summary>
        /// <remarks>
        /// §2.5 and §3.2 run a <c>static { }</c> block interleaved with the field initializers, in
        /// the source position it appears — so the two lists have to be merged at emit, and one
        /// counter across both is what orders them.
        /// </remarks>
        public int Order { get; }
    }

    /// <summary>
    /// A <c>static { ... }</c> block, which runs from its container's static initializer (§2.5, §3.2).
    /// </summary>
    /// <remarks>
    /// It is not a member and has no symbol anything can name; what it has is a body and a position
    /// among the initializers it runs beside. Kept in a list of its own for the same reason
    /// <see cref="BoundFieldInitializer"/> is: <em>where</em> the code runs is a code-generation
    /// decision, and splicing it into a body during binding would take that decision away.
    /// </remarks>
    public sealed class BoundStaticBlock
    {
        internal BoundStaticBlock(BoundStatement body, NamedTypeSymbol? declaringType, ModuleSymbol module, int order)
        {
            Body = body;
            DeclaringType = declaringType;
            Module = module;
            Order = order;
        }

        /// <summary>The block itself.</summary>
        public BoundStatement Body { get; }

        /// <summary>The type declaring it, or <see langword="null"/> for a module-level block.</summary>
        public NamedTypeSymbol? DeclaringType { get; }

        /// <summary>The module it belongs to.</summary>
        public ModuleSymbol Module { get; }

        /// <summary>Where it sat among its container's initializers.</summary>
        public int Order { get; }
    }

    /// <summary>
    /// The <c>: super(...)</c> or <c>: this(...)</c> a constructor chains to (§3.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a statement inside the body, because it does not run inside it: a chain is evaluated
    /// first, and what follows it differs between the two forms. A <c>super</c> chain runs the base
    /// constructor and then this class's own instance initializers; a <c>this</c> chain runs another
    /// constructor of the same class, which already ran them, so they must not run twice.
    /// </para>
    /// <para>
    /// Keeping it beside the body rather than in it is what lets the emitter order those three
    /// pieces — chain, initializers, body — without having to recognise a call in the tree.
    /// </para>
    /// </remarks>
    public sealed class BoundConstructorChain
    {
        internal BoundConstructorChain(
            SyntaxNode syntax,
            MethodSymbol target,
            IReadOnlyList<BoundExpression> arguments,
            bool isThis)
        {
            Syntax = syntax;
            Target = target;
            Arguments = arguments;
            IsThis = isThis;
        }

        /// <summary>The syntax it was written on.</summary>
        public SyntaxNode Syntax { get; }

        /// <summary>The constructor being chained to.</summary>
        public MethodSymbol Target { get; }

        /// <summary>Its arguments, in parameter order.</summary>
        public IReadOnlyList<BoundExpression> Arguments { get; }

        /// <summary>Whether it was written <c>this(...)</c> rather than <c>super(...)</c>.</summary>
        public bool IsThis { get; }
    }

    /// <summary>A labelled statement, which a <c>break</c> or <c>continue</c> may name.</summary>
    public sealed class BoundLabeledStatement : BoundStatement
    {
        internal BoundLabeledStatement(SyntaxNode syntax, string label, BoundStatement statement) : base(syntax)
        {
            Label = label;
            Statement = statement;
        }

        /// <summary>The label.</summary>
        public string Label { get; }

        /// <summary>The statement it names.</summary>
        public BoundStatement Statement { get; }
    }
}
