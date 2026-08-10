#nullable enable

using System.Collections.Generic;

namespace Surtr.Compiler.Syntax.Ast
{
    /// <summary>Base of every statement.</summary>
    public abstract class StatementSyntax : SyntaxNode
    {
        /// <summary>Initializes the node with the position it starts at.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        protected StatementSyntax(SourceLocation location) : base(location)
        {
        }
    }

    /// <summary>A braced block, which introduces a scope (§4.4).</summary>
    public sealed class BlockStatementSyntax : StatementSyntax
    {
        /// <summary>The statements it contains, in order.</summary>
        public IReadOnlyList<StatementSyntax> Statements { get; }

        /// <summary>Initializes a block.</summary>
        /// <param name="location">Where in the source the block begins.</param>
        /// <param name="statements">The statements it contains.</param>
        public BlockStatementSyntax(SourceLocation location, IReadOnlyList<StatementSyntax> statements) : base(location)
        {
            Statements = statements;
        }
    }

    /// <summary>An expression evaluated for its effect: a call, an assignment, an increment.</summary>
    public sealed class ExpressionStatementSyntax : StatementSyntax
    {
        /// <summary>The expression.</summary>
        public ExpressionSyntax Expression { get; }

        /// <summary>Initializes an expression statement.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        /// <param name="expression">The expression.</param>
        public ExpressionStatementSyntax(SourceLocation location, ExpressionSyntax expression) : base(location)
        {
            Expression = expression;
        }
    }

    /// <summary>A local declaration: <c>let x = 1;</c>, <c>var y: int;</c>, <c>const Z: int = 3;</c>.</summary>
    public sealed class LocalDeclarationStatementSyntax : StatementSyntax
    {
        /// <summary>The declared name.</summary>
        public string Name { get; }

        /// <summary>The declared type, or <c>null</c> when it is inferred from the initializer (§5.9).</summary>
        public TypeSyntax? Type { get; }

        /// <summary>The initializer, or <c>null</c> when the binding is declared without one.</summary>
        public ExpressionSyntax? Initializer { get; }

        /// <summary>True for <c>var</c>, false for <c>let</c> and <c>const</c>.</summary>
        public bool IsMutable { get; }

        /// <summary>True for <c>const</c> — a compile-time value (§7.1).</summary>
        public bool IsConst { get; }

        /// <summary>Initializes a local declaration.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        /// <param name="name">The declared name.</param>
        /// <param name="type">The declared type, or <c>null</c>.</param>
        /// <param name="initializer">The initializer, or <c>null</c>.</param>
        /// <param name="isMutable">True for <c>var</c>.</param>
        /// <param name="isConst">True for <c>const</c>.</param>
        public LocalDeclarationStatementSyntax(SourceLocation location, string name, TypeSyntax? type, ExpressionSyntax? initializer, bool isMutable, bool isConst)
            : base(location)
        {
            Name = name;
            Type = type;
            Initializer = initializer;
            IsMutable = isMutable;
            IsConst = isConst;
        }
    }

    /// <summary>An <c>if</c>, with or without an <c>else</c>. <see cref="IsConst"/> distinguishes <c>const if</c> (§7.3).</summary>
    public sealed class IfStatementSyntax : StatementSyntax
    {
        /// <summary>The condition.</summary>
        public ExpressionSyntax Condition { get; }

        /// <summary>The branch taken when the condition holds.</summary>
        public StatementSyntax Then { get; }

        /// <summary>The <c>else</c> branch, or <c>null</c>. An <c>else if</c> is another <see cref="IfStatementSyntax"/> here.</summary>
        public StatementSyntax? Else { get; }

        /// <summary>
        /// True for <c>const if</c>: the condition is evaluated at compile time and the untaken
        /// branch is discarded without being bound or type-checked (§7.3).
        /// </summary>
        public bool IsConst { get; }

        /// <summary>Initializes an <c>if</c>.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        /// <param name="condition">The condition.</param>
        /// <param name="then">The branch taken when the condition holds.</param>
        /// <param name="elseBranch">The <c>else</c> branch, or <c>null</c>.</param>
        /// <param name="isConst">True for <c>const if</c>.</param>
        public IfStatementSyntax(SourceLocation location, ExpressionSyntax condition, StatementSyntax then, StatementSyntax? elseBranch, bool isConst)
            : base(location)
        {
            Condition = condition;
            Then = then;
            Else = elseBranch;
            IsConst = isConst;
        }
    }

    /// <summary>A <c>while</c> loop.</summary>
    public sealed class WhileStatementSyntax : StatementSyntax
    {
        /// <summary>The condition, tested before each iteration.</summary>
        public ExpressionSyntax Condition { get; }

        /// <summary>The loop body.</summary>
        public StatementSyntax Body { get; }

        /// <summary>Initializes a <c>while</c> loop.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        /// <param name="condition">The condition.</param>
        /// <param name="body">The loop body.</param>
        public WhileStatementSyntax(SourceLocation location, ExpressionSyntax condition, StatementSyntax body) : base(location)
        {
            Condition = condition;
            Body = body;
        }
    }

    /// <summary>The three-clause <c>for</c>. Every clause is optional.</summary>
    public sealed class ForStatementSyntax : StatementSyntax
    {
        /// <summary>The initializer clause, or <c>null</c>.</summary>
        public StatementSyntax? Initializer { get; }

        /// <summary>The condition clause, or <c>null</c> — which means it never terminates on its own.</summary>
        public ExpressionSyntax? Condition { get; }

        /// <summary>The iteration clause, or <c>null</c>.</summary>
        public ExpressionSyntax? Step { get; }

        /// <summary>The loop body.</summary>
        public StatementSyntax Body { get; }

        /// <summary>Initializes a three-clause <c>for</c>.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        /// <param name="initializer">The initializer clause, or <c>null</c>.</param>
        /// <param name="condition">The condition clause, or <c>null</c>.</param>
        /// <param name="step">The iteration clause, or <c>null</c>.</param>
        /// <param name="body">The loop body.</param>
        public ForStatementSyntax(SourceLocation location, StatementSyntax? initializer, ExpressionSyntax? condition, ExpressionSyntax? step, StatementSyntax body)
            : base(location)
        {
            Initializer = initializer;
            Condition = condition;
            Step = step;
            Body = body;
        }
    }

    /// <summary>A <c>for-in</c> loop over a collection or a range (§4.2).</summary>
    public sealed class ForInStatementSyntax : StatementSyntax
    {
        /// <summary>The loop variable's name.</summary>
        public string VariableName { get; }

        /// <summary>The loop variable's declared type, or <c>null</c> when inferred from the sequence.</summary>
        public TypeSyntax? VariableType { get; }

        /// <summary>The sequence being iterated.</summary>
        public ExpressionSyntax Sequence { get; }

        /// <summary>The loop body.</summary>
        public StatementSyntax Body { get; }

        /// <summary>Initializes a <c>for-in</c> loop.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        /// <param name="variableName">The loop variable's name.</param>
        /// <param name="variableType">The loop variable's declared type, or <c>null</c>.</param>
        /// <param name="sequence">The sequence being iterated.</param>
        /// <param name="body">The loop body.</param>
        public ForInStatementSyntax(SourceLocation location, string variableName, TypeSyntax? variableType, ExpressionSyntax sequence, StatementSyntax body)
            : base(location)
        {
            VariableName = variableName;
            VariableType = variableType;
            Sequence = sequence;
            Body = body;
        }
    }

    /// <summary>One <c>case</c> or <c>default</c> section of a statement <c>switch</c>.</summary>
    /// <remarks>
    /// Several <c>case</c> labels may share one section's body, which is how §4.3 spells the
    /// grouped form; a section with no statements falls through to the next.
    /// </remarks>
    public sealed class SwitchSectionSyntax : SyntaxNode
    {
        /// <summary>The values labelling this section. Empty means <c>default</c>.</summary>
        public IReadOnlyList<ExpressionSyntax> Labels { get; }

        /// <summary>The statements in this section.</summary>
        public IReadOnlyList<StatementSyntax> Statements { get; }

        /// <summary>True when this is the <c>default</c> section.</summary>
        public bool IsDefault => Labels.Count == 0;

        /// <summary>Initializes a switch section.</summary>
        /// <param name="location">Where in the source the section begins.</param>
        /// <param name="labels">The values labelling it, or an empty list for <c>default</c>.</param>
        /// <param name="statements">The statements in this section.</param>
        public SwitchSectionSyntax(SourceLocation location, IReadOnlyList<ExpressionSyntax> labels, IReadOnlyList<StatementSyntax> statements)
            : base(location)
        {
            Labels = labels;
            Statements = statements;
        }
    }

    /// <summary>The statement form of <c>switch</c> (§4.3), which allows fallthrough and needs no <c>default</c>.</summary>
    public sealed class SwitchStatementSyntax : StatementSyntax
    {
        /// <summary>The value being switched on.</summary>
        public ExpressionSyntax Subject { get; }

        /// <summary>The sections, in source order.</summary>
        public IReadOnlyList<SwitchSectionSyntax> Sections { get; }

        /// <summary>Initializes a switch statement.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        /// <param name="subject">The value being switched on.</param>
        /// <param name="sections">The sections, in source order.</param>
        public SwitchStatementSyntax(SourceLocation location, ExpressionSyntax subject, IReadOnlyList<SwitchSectionSyntax> sections)
            : base(location)
        {
            Subject = subject;
            Sections = sections;
        }
    }

    /// <summary>One <c>catch</c> clause.</summary>
    public sealed class CatchClauseSyntax : SyntaxNode
    {
        /// <summary>The name bound to the caught exception.</summary>
        public string VariableName { get; }

        /// <summary>The exception type this clause matches.</summary>
        public TypeSyntax ExceptionType { get; }

        /// <summary>The handler body.</summary>
        public BlockStatementSyntax Body { get; }

        /// <summary>Initializes a catch clause.</summary>
        /// <param name="location">Where in the source the clause begins.</param>
        /// <param name="variableName">The name bound to the caught exception.</param>
        /// <param name="exceptionType">The exception type matched.</param>
        /// <param name="body">The handler body.</param>
        public CatchClauseSyntax(SourceLocation location, string variableName, TypeSyntax exceptionType, BlockStatementSyntax body)
            : base(location)
        {
            VariableName = variableName;
            ExceptionType = exceptionType;
            Body = body;
        }
    }

    /// <summary>A <c>try</c> with its <c>catch</c> clauses and optional <c>finally</c> (§9).</summary>
    public sealed class TryStatementSyntax : StatementSyntax
    {
        /// <summary>The protected block.</summary>
        public BlockStatementSyntax Body { get; }

        /// <summary>The catch clauses, tried top to bottom.</summary>
        public IReadOnlyList<CatchClauseSyntax> Catches { get; }

        /// <summary>The <c>finally</c> block, or <c>null</c>.</summary>
        public BlockStatementSyntax? Finally { get; }

        /// <summary>Initializes a try statement.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        /// <param name="body">The protected block.</param>
        /// <param name="catches">The catch clauses.</param>
        /// <param name="finallyBlock">The <c>finally</c> block, or <c>null</c>.</param>
        public TryStatementSyntax(SourceLocation location, BlockStatementSyntax body, IReadOnlyList<CatchClauseSyntax> catches, BlockStatementSyntax? finallyBlock)
            : base(location)
        {
            Body = body;
            Catches = catches;
            Finally = finallyBlock;
        }
    }

    /// <summary>A <c>throw</c>.</summary>
    public sealed class ThrowStatementSyntax : StatementSyntax
    {
        /// <summary>The value thrown; its type must extend <c>Exception</c> (§9).</summary>
        public ExpressionSyntax Value { get; }

        /// <summary>Initializes a throw statement.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        /// <param name="value">The value thrown.</param>
        public ThrowStatementSyntax(SourceLocation location, ExpressionSyntax value) : base(location)
        {
            Value = value;
        }
    }

    /// <summary>A <c>return</c>, with or without a value.</summary>
    public sealed class ReturnStatementSyntax : StatementSyntax
    {
        /// <summary>The returned value, or <c>null</c> for a bare <c>return</c>.</summary>
        public ExpressionSyntax? Value { get; }

        /// <summary>Initializes a return statement.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        /// <param name="value">The returned value, or <c>null</c>.</param>
        public ReturnStatementSyntax(SourceLocation location, ExpressionSyntax? value) : base(location)
        {
            Value = value;
        }
    }

    /// <summary>A <c>break</c> or <c>continue</c>, optionally naming an enclosing labelled loop (§4.2).</summary>
    public sealed class BreakStatementSyntax : StatementSyntax
    {
        /// <summary>True for <c>continue</c>, false for <c>break</c>.</summary>
        public bool IsContinue { get; }

        /// <summary>The target loop's label, or <c>null</c> for the nearest enclosing loop.</summary>
        public string? Label { get; }

        /// <summary>Initializes a break or continue.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        /// <param name="isContinue">True for <c>continue</c>.</param>
        /// <param name="label">The target loop's label, or <c>null</c>.</param>
        public BreakStatementSyntax(SourceLocation location, bool isContinue, string? label) : base(location)
        {
            IsContinue = isContinue;
            Label = label;
        }
    }

    /// <summary>A labelled statement, <c>outer: for (...) { ... }</c>.</summary>
    public sealed class LabeledStatementSyntax : StatementSyntax
    {
        /// <summary>The label.</summary>
        public string Label { get; }

        /// <summary>The statement it labels.</summary>
        public StatementSyntax Statement { get; }

        /// <summary>Initializes a labelled statement.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        /// <param name="label">The label.</param>
        /// <param name="statement">The statement it labels.</param>
        public LabeledStatementSyntax(SourceLocation location, string label, StatementSyntax statement) : base(location)
        {
            Label = label;
            Statement = statement;
        }
    }

    /// <summary>A lone <c>;</c>.</summary>
    public sealed class EmptyStatementSyntax : StatementSyntax
    {
        /// <summary>Initializes an empty statement.</summary>
        /// <param name="location">Where in the source the statement begins.</param>
        public EmptyStatementSyntax(SourceLocation location) : base(location)
        {
        }
    }
}
