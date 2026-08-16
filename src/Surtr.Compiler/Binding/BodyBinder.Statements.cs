#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax.Ast;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    public sealed partial class BodyBinder
    {
        /// <summary>Binds a statement.</summary>
        public BoundStatement BindStatement(StatementSyntax syntax)
        {
            switch (syntax)
            {
                case BlockStatementSyntax block: return BindBlock(block);
                case ExpressionStatementSyntax expression: return BindExpressionStatement(expression);
                case LocalDeclarationStatementSyntax local: return BindLocalDeclaration(local);
                case IfStatementSyntax @if: return BindIf(@if);
                case WhileStatementSyntax @while: return BindWhile(@while);
                case ForStatementSyntax @for: return BindFor(@for);
                case ForInStatementSyntax forIn: return BindForIn(forIn);
                case SwitchStatementSyntax @switch: return BindSwitch(@switch);
                case TryStatementSyntax @try: return BindTry(@try);
                case ThrowStatementSyntax @throw: return BindThrow(@throw);
                case ReturnStatementSyntax @return: return BindReturn(@return);
                case BreakStatementSyntax @break: return BindBreak(@break);
                case LabeledStatementSyntax labeled: return BindLabeled(labeled);
                default: return new BoundNopStatement(syntax);
            }
        }

        private BoundStatement BindBlock(BlockStatementSyntax syntax)
        {
            var previous = PushScope();

            var statements = new BoundStatement[syntax.Statements.Count];
            for (int i = 0; i < statements.Length; i++)
                statements[i] = BindStatement(syntax.Statements[i]);

            PopScope(previous);
            return new BoundBlockStatement(syntax, statements);
        }

        private BoundStatement BindExpressionStatement(ExpressionStatementSyntax syntax)
            => new BoundExpressionStatement(syntax, BindExpression(syntax.Expression));

        /// <summary>
        /// Binds a <c>var</c> or <c>let</c>, inferring its type from the initializer when none is
        /// written.
        /// </summary>
        /// <remarks>
        /// Inference is one-way: a written type also types the initializer, which is what lets
        /// <c>let xs: int[] = [];</c> work where a bare <c>let xs = [];</c> cannot.
        /// </remarks>
        private BoundStatement BindLocalDeclaration(LocalDeclarationStatementSyntax syntax)
        {
            TypeSymbol? declared = syntax.Type is null
                ? null
                : _resolver.Resolve(syntax.Type, _typeScope, _sourceName);

            BoundExpression? initializer = null;

            if (syntax.Initializer is not null)
            {
                initializer = BindExpression(syntax.Initializer, declared);

                if (declared is not null)
                    initializer = Convert(initializer, declared, syntax.Initializer.Span);
            }

            var type = declared ?? initializer?.Type;

            if (type is null)
            {
                Report(
                    SurtrDiagnosticCode.CannotInferType,
                    syntax.Span,
                    $"'{syntax.Name}' has no written type and nothing to infer one from.");

                type = _factory.ErrorType;
            }
            else if (declared is null && type.SpecialType == SpecialType.Void)
            {
                Report(
                    SurtrDiagnosticCode.CannotInferType,
                    syntax.Span,
                    $"'{syntax.Name}' would be void, which names no value.");

                type = _factory.ErrorType;
            }

            if (syntax.IsConst)
                return BindConstLocal(syntax, type, initializer);

            var local = DeclareLocal(syntax.Name, type, !syntax.IsMutable, syntax.Span);
            return new BoundLocalDeclarationStatement(syntax, local, initializer);
        }

        /// <summary>
        /// Binds a <c>const</c> local (§7.1): folded once here and substituted at every read
        /// thereafter, so it carries no local slot at all — the same "no slot" promise a module or
        /// class <c>const</c> makes, kept the same way (<c>BodyBinder.ResolveField</c> is the field
        /// counterpart of what <see cref="BodyBinder.BindIdentifier"/> does for one of these).
        /// </summary>
        /// <remarks>
        /// Folded over the initializer's own <em>syntax</em> via <see cref="_constants"/>, not
        /// registered into it: <see cref="ConstantEvaluator"/> is one flat, module-wide name table,
        /// which is correct for a module or class <c>const</c> (there is exactly one of each name in
        /// scope at a time) but would be wrong for a local — two different functions each declaring
        /// their own <c>const x</c> would otherwise collide. Reading the initializer's syntax
        /// directly still lets it reference an already-registered module or class <c>const</c>, or
        /// call a <c>const fun</c>; what it cannot do is reference an <em>earlier local</em> const in
        /// the same body, which is a real but narrow gap against the module/class case.
        /// </remarks>
        private BoundStatement BindConstLocal(LocalDeclarationStatementSyntax syntax, TypeSymbol type, BoundExpression? initializer)
        {
            // Checked independently of whether the initializer folds — a `const Vec2` initialized
            // from a call is wrong on its type alone, and would otherwise only ever be reported as
            // "did not fold" (nothing `TryEvaluate` produces is ever a composite value in the first
            // place, so a type mismatch here would otherwise hide behind that diagnostic instead of
            // naming the actual rule).
            var nonNullable = type.NonNullable;
            if (!nonNullable.IsPrimitive && nonNullable.SpecialType != SpecialType.String)
            {
                Report(
                    SurtrDiagnosticCode.InvalidConstType,
                    syntax.Span,
                    $"'{syntax.Name}' is const, so its type has to be a primitive or 'string' (§7.1), not '{type.ToDisplayString()}'.");
            }

            if (syntax.Initializer is null || !_constants.TryEvaluate(syntax.Initializer, out object? value))
            {
                Report(
                    SurtrDiagnosticCode.NotAConstant,
                    syntax.Span,
                    $"'{syntax.Name}' is const, so its initializer has to fold at compile time.");

                // A local still declared, so a later reference reports once here rather than a
                // second, unrelated "does not name anything in scope".
                var broken = DeclareLocal(syntax.Name, type, isReadOnly: true, syntax.Span);
                return new BoundLocalDeclarationStatement(syntax, broken, initializer);
            }

            var local = DeclareLocal(syntax.Name, type, isReadOnly: true, syntax.Span);
            _localConstants.Add(local, value);
            return new BoundNopStatement(syntax);
        }

        private BoundStatement BindIf(IfStatementSyntax syntax)
        {
            // §7.3: a `const if`'s untaken branch is removed before compilation proper. It is parsed
            // and never bound, which is the whole reason the feature works - a branch guarded on
            // one platform routinely names types this build does not have.
            if (syntax.IsConst)
            {
                if (!_constants.TryEvaluateCondition(syntax.Condition, out bool taken))
                {
                    Report(
                        SurtrDiagnosticCode.NotAConstant,
                        syntax.Condition.Span,
                        "A 'const if' condition has to fold to a bool at compile time.");

                    return new BoundNopStatement(syntax);
                }

                if (taken)
                    return BindStatement(syntax.Then);

                return syntax.Else is null ? new BoundNopStatement(syntax) : BindStatement(syntax.Else);
            }

            var condition = BindConverted(syntax.Condition, _factory.Bool);

            // What the condition proves holds only inside the branch it guards.
            var narrowings = NarrowingsFrom(syntax.Condition);
            PushNarrowings(narrowings);
            var then = BindStatement(syntax.Then);
            PopNarrowings(narrowings);

            var otherwise = syntax.Else is null ? null : BindStatement(syntax.Else);

            return new BoundIfStatement(syntax, condition, then, otherwise);
        }

        private BoundStatement BindWhile(WhileStatementSyntax syntax)
        {
            var condition = BindConverted(syntax.Condition, _factory.Bool);

            _loopDepth++;
            var body = BindStatement(syntax.Body);
            _loopDepth--;

            return new BoundWhileStatement(syntax, condition, body);
        }

        private BoundStatement BindFor(ForStatementSyntax syntax)
        {
            // The initializer's locals are in scope for the condition, the step and the body, and
            // nowhere after - so the whole loop gets one scope of its own.
            var previous = PushScope();

            var initializer = syntax.Initializer is null ? null : BindStatement(syntax.Initializer);
            var condition = syntax.Condition is null ? null : BindConverted(syntax.Condition, _factory.Bool);
            var step = syntax.Step is null ? null : BindExpression(syntax.Step);

            _loopDepth++;
            var body = BindStatement(syntax.Body);
            _loopDepth--;

            PopScope(previous);
            return new BoundForStatement(syntax, initializer, condition, step, body);
        }

        private BoundStatement BindForIn(ForInStatementSyntax syntax)
        {
            var sequence = BindExpression(syntax.Sequence);
            var element = ElementTypeOf(sequence, syntax);

            if (syntax.VariableType is not null)
            {
                var declared = _resolver.Resolve(syntax.VariableType, _typeScope, _sourceName);

                if (!element.IsError && !_conversions.IsAssignable(element, declared))
                {
                    Report(
                        SurtrDiagnosticCode.CannotConvert,
                        syntax.VariableType.Span,
                        $"The sequence yields '{element.ToDisplayString()}', which does not fit '{declared.ToDisplayString()}'.");
                }

                element = declared;
            }

            var previous = PushScope();
            var variable = DeclareLocal(syntax.VariableName, element, isReadOnly: true, syntax.Span);

            _loopDepth++;
            var body = BindStatement(syntax.Body);
            _loopDepth--;

            PopScope(previous);
            return new BoundForInStatement(syntax, variable, sequence, body);
        }

        /// <summary>
        /// What one step of a <c>for-in</c> yields.
        /// </summary>
        /// <remarks>
        /// The built-in collections answer structurally, which is also why a loop over one lowers to
        /// an indexed walk and allocates no cursor. Anything else has to satisfy
        /// <c>IIterable&lt;T&gt;</c>, and the element type is read off the <c>current</c> its
        /// iterator declares.
        /// </remarks>
        private TypeSymbol ElementTypeOf(BoundExpression sequence, ForInStatementSyntax syntax)
        {
            var type = sequence.Type.NonNullable;

            switch (type)
            {
                case ArrayTypeSymbol array:
                    return array.ElementType;

                case DictionaryTypeSymbol dictionary:
                    // A dict yields (K, V) pairs, matching what the runtime's iterator hands back.
                    return _factory.Tuple(new[] { dictionary.KeyType, dictionary.ValueType });

                case NamedTypeSymbol named when named.SpecialType == SpecialType.String:
                    return _factory.Char;

                case NamedTypeSymbol named when named.SpecialType == SpecialType.Range:
                    return _factory.Int;
            }

            if (type.IsError)
                return _factory.ErrorType;

            foreach (var member in _lookup.Reachable(type))
            {
                if (member is MethodSymbol { Name: "iterate", Parameters.Count: 0 } iterate)
                {
                    foreach (var inner in _lookup.Reachable(iterate.ReturnType))
                    {
                        if (inner is PropertySymbol { Name: "current" } current)
                            return current.Type;
                    }
                }
            }

            Report(
                SurtrDiagnosticCode.NotSupportedOnType,
                syntax.Sequence.Span,
                $"'{sequence.Type.ToDisplayString()}' cannot be iterated; it is not a built-in collection and does not satisfy IIterable.");

            return _factory.ErrorType;
        }

        private BoundStatement BindSwitch(SwitchStatementSyntax syntax)
        {
            var subject = BindExpression(syntax.Subject);
            var sections = new BoundSwitchSection[syntax.Sections.Count];

            // A switch body is one scope: a local declared in one section is visible in the next,
            // the way a fall-through language needs it to be.
            var previous = PushScope();
            _loopDepth++;

            for (int i = 0; i < sections.Length; i++)
            {
                var labels = new BoundExpression[syntax.Sections[i].Labels.Count];
                for (int l = 0; l < labels.Length; l++)
                    labels[l] = BindConverted(syntax.Sections[i].Labels[l], subject.Type);

                var statements = new BoundStatement[syntax.Sections[i].Statements.Count];
                for (int s = 0; s < statements.Length; s++)
                    statements[s] = BindStatement(syntax.Sections[i].Statements[s]);

                sections[i] = new BoundSwitchSection(labels, statements);
            }

            _loopDepth--;
            PopScope(previous);

            return new BoundSwitchStatement(syntax, subject, sections);
        }

        private BoundStatement BindTry(TryStatementSyntax syntax)
        {
            var body = BindStatement(syntax.Body);

            var catches = new BoundCatchClause[syntax.Catches.Count];
            for (int i = 0; i < catches.Length; i++)
            {
                var clause = syntax.Catches[i];
                var exceptionType = _resolver.Resolve(clause.ExceptionType, _typeScope, _sourceName);

                var previous = PushScope();
                var local = DeclareLocal(clause.VariableName, exceptionType, isReadOnly: true, clause.Span);
                var handler = BindStatement(clause.Body);
                PopScope(previous);

                catches[i] = new BoundCatchClause(local, handler);
            }

            var finallyBlock = syntax.Finally is null ? null : BindStatement(syntax.Finally);
            return new BoundTryStatement(syntax, body, catches, finallyBlock);
        }

        private BoundStatement BindThrow(ThrowStatementSyntax syntax)
            => new BoundThrowStatement(syntax, BindExpression(syntax.Value));

        private BoundStatement BindReturn(ReturnStatementSyntax syntax)
        {
            var expected = _method.ReturnType;

            if (syntax.Value is null)
            {
                if (!expected.IsVoid && !expected.IsError)
                {
                    Report(
                        SurtrDiagnosticCode.CannotConvert,
                        syntax.Span,
                        $"'{_method.Name}' returns '{expected.ToDisplayString()}', so this return needs a value.");
                }

                return new BoundReturnStatement(syntax, null);
            }

            if (expected.IsVoid)
            {
                BindExpression(syntax.Value);
                Report(
                    SurtrDiagnosticCode.CannotConvert,
                    syntax.Span,
                    $"'{_method.Name}' returns nothing, so this return cannot carry a value.");

                return new BoundReturnStatement(syntax, null);
            }

            return new BoundReturnStatement(syntax, BindConverted(syntax.Value, expected));
        }

        private BoundStatement BindBreak(BreakStatementSyntax syntax)
        {
            if (syntax.Label is not null)
            {
                if (!_loopLabels.Contains(syntax.Label))
                {
                    Report(
                        SurtrDiagnosticCode.JumpOutsideLoop,
                        syntax.Span,
                        $"No enclosing loop is labelled '{syntax.Label}'.");
                }
            }
            else if (_loopDepth == 0)
            {
                Report(
                    SurtrDiagnosticCode.JumpOutsideLoop,
                    syntax.Span,
                    syntax.IsContinue ? "'continue' needs a loop to be inside." : "'break' needs a loop or a switch to be inside.");
            }

            return new BoundBreakStatement(syntax, syntax.IsContinue, syntax.Label);
        }

        private BoundStatement BindLabeled(LabeledStatementSyntax syntax)
        {
            _loopLabels.Add(syntax.Label);
            var statement = BindStatement(syntax.Statement);
            _loopLabels.RemoveAt(_loopLabels.Count - 1);

            return new BoundLabeledStatement(syntax, syntax.Label, statement);
        }
    }
}
