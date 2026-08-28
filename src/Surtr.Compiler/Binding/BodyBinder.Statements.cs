#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;
using System;
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
                case TupleDeclarationStatementSyntax tupleDeclaration: return BindTupleDeclaration(tupleDeclaration);
                case IfStatementSyntax @if: return BindIf(@if);
                case WhileStatementSyntax @while: return BindWhile(@while);
                case ForStatementSyntax @for: return BindFor(@for);
                case ForInStatementSyntax forIn: return BindForIn(forIn);
                case SwitchStatementSyntax @switch: return BindSwitch(@switch);
                case TryStatementSyntax @try: return BindTry(@try);
                case UsingStatementSyntax @using: return BindUsing(@using);
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
        {
            // `(a, b) = value;` - the assignment form of destructuring (§4.5). It is a statement
            // shape, not an expression: the right side is evaluated once into a hidden temporary
            // and each target then reads its element off it.
            if (syntax.Expression is AssignmentExpressionSyntax
                {
                    Operator: AssignmentOperator.Assign,
                    Target: TupleLiteralExpressionSyntax targets,
                } assignment)
            {
                return BindTupleAssignment(syntax, assignment, targets);
            }

            return new BoundExpressionStatement(syntax, BindExpression(syntax.Expression));
        }

        /// <summary>
        /// Wraps a value about to be written to a <c>@Range</c>-marked member into a sequence that
        /// captures it, throws when it falls outside the declared bounds, and yields the captured
        /// value for the write that follows (§P4). Returns the value unchanged when no check
        /// applies.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The lowering is the same whether the write is a statement or an expression: the value is
        /// bound into a hidden temporary so a call with effects is evaluated once, the guard throws
        /// before the write, and the write itself is the ordinary assignment over the captured
        /// value. <see cref="BoundSequenceExpression"/> is what lets a statement shape sit where an
        /// expression is expected, so a nested assignment is guarded exactly like a top-level one.
        /// </para>
        /// <para>
        /// In a release build (<c>_rangeChecksEnabled</c> false) this never runs, so the check costs
        /// nothing. A missing exception class, or a member with no recorded range, degrades to no
        /// check at all.
        /// </para>
        /// </remarks>
        private BoundExpression RangeCheckValue(
            SyntaxNode syntax,
            BoundExpression value,
            Symbol member,
            TypeSymbol memberType)
        {
            if (!_rangeChecksEnabled)
                return value;

            double? low = BuiltInAttributes.RangeLow(member);
            double? high = BuiltInAttributes.RangeHigh(member);
            if (low is null && high is null)
                return value;

            if (memberType.NonNullable.SpecialType is not (SpecialType.Int or SpecialType.Float))
                return value;

            var temporary = DeclareLocal(NextRangeTempName(), memberType.NonNullable, isReadOnly: true, syntax.Span);

            BoundExpression? condition = null;
            var captured = new BoundLocalExpression(syntax, temporary);

            if (low is not null)
                condition = JoinRangeCondition(condition, syntax, BinaryOperator.Less, captured, low.Value);
            if (high is not null)
                condition = JoinRangeCondition(condition, syntax, BinaryOperator.Greater, captured, high.Value);

            if (condition is null)
                return value;

            var thrown = BuildLibraryException(
                syntax,
                "ArgumentOutOfRangeException",
                RangeMessage(member, low, high));

            if (thrown is null)
                return value;

            return new BoundSequenceExpression(
                syntax,
                new BoundBlockStatement(syntax, new BoundStatement[]
                {
                    new BoundLocalDeclarationStatement(syntax, temporary, value),
                    new BoundIfStatement(syntax, condition, new BoundThrowStatement(syntax, thrown), otherwise: null),
                }),
                captured,
                memberType.NonNullable);
        }

        /// <summary>
        /// Whether a write targets a <c>@Range</c>-marked numeric member, and what member that is.
        /// </summary>
        private static Symbol? RangedMember(BoundExpression target)
            => target switch
            {
                BoundFieldExpression field => field.Field,
                BoundPropertyExpression property => property.Property,
                _ => null,
            };

        private static TypeSymbol MemberType(Symbol member)
            => member switch
            {
                FieldSymbol field => field.Type,
                PropertySymbol property => property.Type,
                _ => throw new ArgumentOutOfRangeException(nameof(member)),
            };

        /// <summary>
        /// Adds one side of a range check to the running condition, widening the compared value to
        /// the float the bounds are expressed in (§P4).
        /// </summary>
        private BoundExpression? JoinRangeCondition(
            BoundExpression? current,
            SyntaxNode syntax,
            BinaryOperator op,
            BoundExpression value,
            double bound)
        {
            var compared = Widen(value, _factory.Float, syntax);
            var side = new BoundBinaryExpression(
                syntax,
                op,
                compared,
                new BoundLiteralExpression(syntax, _factory.Float, bound),
                _factory.Bool);

            return current is null
                ? side
                : new BoundBinaryExpression(syntax, BinaryOperator.LogicalOr, current, side, _factory.Bool);
        }

        /// <summary>The message an out-of-range assignment throws with.</summary>
        private static string RangeMessage(Symbol member, double? low, double? high)
        {
            string bounds = (low, high) switch
            {
                (double lo, double hi) => $"[{lo:G}, {hi:G}]",
                (double lo, null) => $"[{lo:G}, inf)",
                (null, double hi) => $"(-inf, {hi:G}]",
                _ => "a declared range",
            };

            return $"Assignment to '{member.Name}' is outside {bounds}.";
        }

        private int _rangeTemps;
        private string NextRangeTempName() => $"$range{_rangeTemps++}";

        private int _cseTemps;
        private string NextCseTempName() => $"$cse{_cseTemps++}";

        /// <summary>
        /// Binds a destructuring declaration: one hidden temporary holds the value, and every
        /// name becomes an ordinary local reading its own element off it (§4.5).
        /// </summary>
        /// <remarks>
        /// The lowering is entirely desugaring - no new bound node, no new opcode. The temporary is
        /// a tuple-typed local, so it lives in a frame range; each read is the constant-index
        /// element access §5.3 already defines; each name is declared exactly like any other
        /// local, which is what makes flow analysis, capture and emission all work unchanged.
        /// </remarks>
        private BoundStatement BindTupleDeclaration(TupleDeclarationStatementSyntax syntax)
        {
            var initializer = BindExpression(syntax.Initializer);
            if (!CheckDestructuringShape(syntax.Span, initializer.Type, syntax.Names.Count))
                return new BoundNopStatement(syntax);

            var tuple = (TupleTypeSymbol)initializer.Type.NonNullable;
            var temporary = DeclareLocal(NextDestructuringTempName(), initializer.Type, isReadOnly: true, syntax.Span);

            var statements = new List<BoundStatement>(syntax.Names.Count + 1)
            {
                new BoundLocalDeclarationStatement(syntax, temporary, initializer),
            };

            for (int i = 0; i < syntax.Names.Count; i++)
            {
                statements.Add(new BoundLocalDeclarationStatement(
                    syntax,
                    DeclareLocal(syntax.Names[i], tuple.ElementTypes[i], !syntax.IsMutable, syntax.Span),
                    ReadElement(syntax, temporary, tuple, i)));
            }

            return new BoundBlockStatement(syntax, statements);
        }

        /// <summary>Binds `(a, b) = value;` - the same desugaring, writing existing targets.</summary>
        private BoundStatement BindTupleAssignment(
            ExpressionStatementSyntax statement,
            AssignmentExpressionSyntax assignment,
            TupleLiteralExpressionSyntax targets)
        {
            var initializer = BindExpression(assignment.Value);

            if (!CheckDestructuringShape(statement.Span, initializer.Type, targets.Elements.Count))
                return new BoundNopStatement(statement);

            var tuple = (TupleTypeSymbol)initializer.Type.NonNullable;
            // A hidden declaration, not an assignment: the emitter learns a local's slot from its
            // declaration statement, and every target write below reads the temporary.
            var temporary = DeclareLocal(NextDestructuringTempName(), initializer.Type, isReadOnly: true, statement.Span);

            var statements = new List<BoundStatement>(targets.Elements.Count + 1)
            {
                new BoundLocalDeclarationStatement(statement, temporary, initializer),
            };

            for (int i = 0; i < targets.Elements.Count; i++)
            {
                var target = BindExpression(targets.Elements[i]);

                if (!target.IsAssignable)
                {
                    Report(
                        SurtrDiagnosticCode.InvalidDestructuring,
                        targets.Elements[i].Span,
                        $"'{targets.Elements[i]}' cannot be assigned by a destructuring pattern; only variables, parameters and fields can.");
                    continue;
                }

                statements.Add(new BoundExpressionStatement(
                    statement,
                    new BoundAssignmentExpression(targets.Elements[i], target, ReadElement(statement, temporary, tuple, i))));
            }

            return new BoundBlockStatement(statement, statements);
        }

        private int _destructuringTemps;

        private string NextDestructuringTempName() => $"$destructure{_destructuringTemps++}";

        private BoundExpression ReadElement(SyntaxNode syntax, LocalSymbol temporary, TupleTypeSymbol tuple, int index)
            => new BoundIndexExpression(
                syntax,
                new BoundLocalExpression(syntax, temporary),
                new BoundLiteralExpression(syntax, _factory.Int, (long)index),
                tuple.ElementTypes[index]);

        /// <summary>Whether <paramref name="value"/>'s type is a tuple matching the pattern's arity.</summary>
        private bool CheckDestructuringShape(SourceSpan span, TypeSymbol value, int arity)
        {
            if (value.IsError)
                return false;

            if (value.NonNullable is not TupleTypeSymbol tuple || tuple.ElementTypes.Count == 0)
            {
                Report(
                    SurtrDiagnosticCode.InvalidDestructuring,
                    span,
                    $"Cannot destructure '{value.ToDisplayString()}': only a tuple with at least one element can be taken apart.");
                return false;
            }

            if (tuple.ElementTypes.Count != arity)
            {
                Report(
                    SurtrDiagnosticCode.InvalidDestructuring,
                    span,
                    $"'{tuple.ToDisplayString()}' has {tuple.ElementTypes.Count} element(s), but the pattern binds {arity}.");
                return false;
            }

            return true;
        }

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

            if (declared?.NonNullable is NamedTypeSymbol declaredType)
                ReportIfObsoleteType(declaredType, syntax.Type!);

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

            // §4.2: the header takes `var`, never `let` - the step clause reassigns the binding on
            // every iteration, which is exactly what `let` (§1.1) forbids.
            if (syntax.Initializer is LocalDeclarationStatementSyntax { IsMutable: false } notMutable)
            {
                Report(
                    SurtrDiagnosticCode.InvalidForLoopBinding,
                    notMutable.Span,
                    $"'{notMutable.Name}' must be declared 'var' in a three-clause 'for' header, not 'let' - the step clause reassigns it on every iteration.");
            }

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
                if (declared.NonNullable is NamedTypeSymbol declaredType)
                    ReportIfObsoleteType(declaredType, syntax.VariableType);

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
            if (TryFindIterableElementType(sequence.Type.NonNullable, out var element))
                return element;

            if (sequence.Type.NonNullable.IsError)
                return _factory.ErrorType;

            Report(
                SurtrDiagnosticCode.NotSupportedOnType,
                syntax.Sequence.Span,
                $"'{sequence.Type.ToDisplayString()}' cannot be iterated; it is not a built-in collection and does not satisfy IIterable.");

            return _factory.ErrorType;
        }

        /// <summary>
        /// What one step of iterating <paramref name="type"/> would yield, without reporting when it
        /// cannot be iterated at all — <see cref="ElementTypeOf"/> is this plus the diagnostic
        /// <c>for-in</c> wants on failure; <c>array&lt;T&gt;(iterable)</c>'s constructor dispatch
        /// (§5.3.3) uses this directly and falls through to its own diagnostic instead, so "what
        /// counts as iterable" has exactly one definition rather than two that could drift apart.
        /// </summary>
        private bool TryFindIterableElementType(TypeSymbol type, out TypeSymbol elementType)
        {
            var nonNullable = type.NonNullable;

            switch (nonNullable)
            {
                case ArrayTypeSymbol array:
                    elementType = array.ElementType;
                    return true;

                case DictionaryTypeSymbol dictionary:
                    // A dict yields (K, V) pairs, matching what the runtime's iterator hands back.
                    elementType = _factory.Tuple(new[] { dictionary.KeyType, dictionary.ValueType });
                    return true;

                case NamedTypeSymbol named when named.SpecialType == SpecialType.String:
                    elementType = _factory.Char;
                    return true;

                case NamedTypeSymbol named when named.SpecialType == SpecialType.Range:
                    elementType = _factory.Int;
                    return true;
            }

            if (nonNullable.IsError)
            {
                elementType = _factory.ErrorType;
                return false;
            }

            foreach (var member in _lookup.Reachable(nonNullable))
            {
                if (member is MethodSymbol { Name: "iterate", Parameters.Count: 0 } iterate)
                {
                    foreach (var inner in _lookup.Reachable(iterate.ReturnType))
                    {
                        if (inner is PropertySymbol { Name: "current" } current)
                        {
                            elementType = current.Type;
                            return true;
                        }
                    }
                }
            }

            elementType = _factory.ErrorType;
            return false;
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

            var exceptionBase = ResolveBuiltInType("Exception", syntax.Span);

            var catches = new BoundCatchClause[syntax.Catches.Count];
            for (int i = 0; i < catches.Length; i++)
            {
                var clause = syntax.Catches[i];
                var exceptionType = _resolver.Resolve(clause.ExceptionType, _typeScope, _sourceName);
                if (exceptionType.NonNullable is NamedTypeSymbol caughtType)
                    ReportIfObsoleteType(caughtType, clause.ExceptionType);

                // §9: every catch clause names a real link in the Exception hierarchy, so matching
                // one against what the runtime raises is always a walk up a genuine chain.
                if (!exceptionType.IsError && !_conversions.IsAssignable(exceptionType, exceptionBase))
                {
                    Report(
                        SurtrDiagnosticCode.InvalidThrowableType,
                        clause.Span,
                        $"'{exceptionType.ToDisplayString()}' does not extend 'Exception', so a catch cannot name it.");
                }

                var previous = PushScope();
                var local = DeclareLocal(clause.VariableName, exceptionType, isReadOnly: true, clause.Span);
                var handler = BindStatement(clause.Body);
                PopScope(previous);

                catches[i] = new BoundCatchClause(local, handler);
            }

            // A `yield` is legal inside the protected block and inside a `catch` - what makes the
            // first safe is that a suspended body can now be closed (§9.2), which runs the pending
            // `finally`. Inside the `finally` itself it stays refused, and that is the one case
            // that cannot be made to work: a close unwinds the body by raising into it, so a
            // `finally` that suspends would answer a close with an element and leave a generator
            // alive after something was told it was disposed.
            _finallyDepth++;
            var finallyBlock = syntax.Finally is null ? null : BindStatement(syntax.Finally);
            _finallyDepth--;

            return new BoundTryStatement(syntax, body, catches, finallyBlock);
        }

        /// <summary>
        /// Binds <c>using</c> by desugaring it: each resource becomes a local, and the block
        /// becomes a <c>try</c> whose <c>finally</c> closes it (§9.2).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The lowering lives here rather than in the emitter because nothing about it is a
        /// decision about representation - unlike <c>for-in</c>, where whether a sequence walks by
        /// index or through a cursor genuinely is one. What comes out is nodes the emitter already
        /// knows, so <c>break</c>, <c>return</c> and an escaping exception all run the close through
        /// the machinery <c>try/finally</c> already has, with no fourth path to keep in step.
        /// </para>
        /// <para>
        /// Several resources nest rather than sharing one <c>finally</c>, which is what closes them
        /// in reverse order: the second may have been opened from the first, so the first has to
        /// outlive it. It also means a resource whose own opening throws leaves the ones before it
        /// closed, which a single flat <c>finally</c> would not.
        /// </para>
        /// </remarks>
        private BoundStatement BindUsing(UsingStatementSyntax syntax)
        {
            // One scope for the whole statement: a resource is visible to the ones after it and to
            // the body, and to nothing outside.
            var previous = PushScope();
            var statement = BindUsingResource(syntax, 0);
            PopScope(previous);

            return statement;
        }

        /// <summary>Binds one resource and everything it wraps, innermost last.</summary>
        private BoundStatement BindUsingResource(UsingStatementSyntax syntax, int index)
        {
            if (index == syntax.Resources.Count)
                return BindStatement(syntax.Body);

            var resource = syntax.Resources[index];
            var declaration = BindLocalDeclaration(resource);

            // Anything but a plain local declaration means the resource itself failed to bind, and
            // the reported diagnostic is the one worth having - wrapping the body in a close over a
            // local that does not exist would only add noise.
            if (declaration is not BoundLocalDeclarationStatement declared)
                return new BoundBlockStatement(syntax, new[] { declaration, BindUsingResource(syntax, index + 1) });

            var local = declared.Local;
            var disposable = ResolveBuiltInType("IDisposable", resource.Span);
            var type = local.Type;

            if (!type.IsError && !disposable.IsError && !_conversions.IsAssignable(type.NonNullable, disposable))
            {
                Report(
                    SurtrDiagnosticCode.NotDisposable,
                    resource.Span,
                    $"'{type.ToDisplayString()}' does not satisfy IDisposable, so a 'using' has nothing to close on the way out.");

                return new BoundBlockStatement(syntax, new[] { declaration, BindUsingResource(syntax, index + 1) });
            }

            var body = BindUsingResource(syntax, index + 1);
            var close = BuildDisposeCall(resource, local, type);

            return new BoundBlockStatement(
                syntax,
                new BoundStatement[] { declaration, new BoundTryStatement(syntax, body, System.Array.Empty<BoundCatchClause>(), close) });
        }

        /// <summary>Builds the <c>finally</c> body that closes one resource.</summary>
        /// <remarks>
        /// A nullable resource is guarded rather than refused, because a factory that answers null
        /// on failure is an ordinary shape and forcing a <c>!!</c> at the top of the block would
        /// turn "nothing to close" into a raise. A non-nullable one is called straight, since a
        /// reference typed non-nullable is one the binder has already established cannot be null.
        /// </remarks>
        private BoundStatement BuildDisposeCall(SyntaxNode syntax, LocalSymbol local, TypeSymbol type)
        {
            var dispose = _lookup.FindMethods(type.NonNullable, "dispose");

            if (dispose.Count == 0)
            {
                Report(
                    SurtrDiagnosticCode.NotDisposable,
                    syntax.Span,
                    $"'{type.ToDisplayString()}' satisfies IDisposable but has no 'dispose' to call.");

                return new BoundNopStatement(syntax);
            }

            BoundStatement close = new BoundExpressionStatement(
                syntax,
                new BoundCallExpression(
                    syntax,
                    new BoundLocalExpression(syntax, local),
                    dispose[0],
                    System.Array.Empty<BoundExpression>(),
                    isVirtual: dispose[0].Dispatch != MethodDispatch.Direct));

            if (!type.IsNullable)
                return close;

            return new BoundIfStatement(
                syntax,
                new BoundBinaryExpression(
                    syntax,
                    BinaryOperator.NotEqual,
                    new BoundLocalExpression(syntax, local),
                    new BoundLiteralExpression(syntax, type, null),
                    _factory.Bool),
                close,
                null);
        }

        private BoundStatement BindThrow(ThrowStatementSyntax syntax)
        {
            var value = BindExpression(syntax.Value);
            CheckThrowable(value, syntax.Span);

            return new BoundThrowStatement(syntax, value);
        }

        /// <summary>Binds <c>throw</c> used as an expression, typed <c>never</c> (§9).</summary>
        private BoundExpression BindThrowExpression(ThrowExpressionSyntax syntax)
        {
            var value = BindExpression(syntax.Value);
            CheckThrowable(value, syntax.Span);

            return new BoundThrowExpression(syntax, value, _factory.Never);
        }

        /// <summary>§9: <c>throw</c> only ever type-checks against an Exception-typed expression.</summary>
        private void CheckThrowable(BoundExpression value, SourceSpan span)
        {
            var exceptionBase = ResolveBuiltInType("Exception", span);

            // §9: `throw` only ever type-checks against an Exception-typed expression, so a
            // `catch (e: T)` anywhere is always matching against a real hierarchy.
            if (!value.Type.IsError && !_conversions.IsAssignable(value.Type, exceptionBase))
            {
                Report(
                    SurtrDiagnosticCode.InvalidThrowableType,
                    span,
                    $"'{value.Type.ToDisplayString()}' does not extend 'Exception', so it cannot be thrown.");
            }
        }

        private BoundStatement BindReturn(ReturnStatementSyntax syntax)
        {
            var expected = _method.ReturnType;

            // A generator's `return` ends the sequence, and may carry a result alongside it
            // (§3.7): what `yield from` evaluates to, and what `result` reads back. It is checked
            // against nothing, because a generator declares its *element* and has nowhere to write
            // a second type - so the value lands in an erased slot like any other `unknown`, and is
            // cast at the point of use. Handled here because the method's declared return is
            // `generator<T>`, against which the two forms would otherwise be checked backwards.
            if (_method.IsGenerator && _lambdas.Count == 0)
            {
                if (syntax.Value is null)
                    return new BoundReturnStatement(syntax, null);

                var result = BindExpression(syntax.Value);
                return new BoundReturnStatement(syntax, Convert(result, _factory.Unknown, syntax.Value.Span));
            }

            if (syntax.Value is null)
            {
                if (!expected.IsVoid && !expected.IsNever && !expected.IsError)
                {
                    Report(
                        SurtrDiagnosticCode.CannotConvert,
                        syntax.Span,
                        $"'{_method.Name}' returns '{expected.ToDisplayString()}', so this return needs a value.");
                }

                return new BoundReturnStatement(syntax, null);
            }

            if (expected.IsVoid || expected.IsNever)
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

        /// <summary>
        /// Binds a <c>yield</c>, against the element its generator declares (§3.7).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The three refusals are the three places there is no frame to suspend, and each is worth
        /// a distinct sentence rather than one generic message. Outside a generator there is no
        /// element to convert against; inside a nested lambda the frame that would be copied belongs
        /// to the lambda, which is a separate function with its own body; inside a <c>finally</c>
        /// the block is running <em>because</em> the generator is being closed, and suspending
        /// there would answer a close with an element. Binding continues past each one so the value
        /// is still checked and one mistake does not hide the next.
        /// </para>
        /// <para>
        /// The result type is always <c>unknown</c> - what a resumption carries in has no declared
        /// type to check against, since §3.7 makes a generator declare its element and gives it
        /// nowhere to name a second one.
        /// </para>
        /// </remarks>
        internal BoundExpression BindYield(YieldExpressionSyntax syntax)
        {
            _yieldCount++;

            if (_lambdas.Count > 0)
            {
                Report(
                    SurtrDiagnosticCode.InvalidYield,
                    syntax.Span,
                    "A 'yield' cannot appear inside a lambda: the lambda is a function of its own, so there is no generator frame here to suspend (§3.7).");

                return new BoundYieldExpression(syntax, _factory.Unknown, BindExpression(syntax.Value));
            }

            if (!_method.IsGenerator || _method.YieldType is not { } element)
            {
                Report(
                    SurtrDiagnosticCode.InvalidYield,
                    syntax.Span,
                    $"'{_method.Name}' is not a generator, so it cannot 'yield'. Declare it with 'generator' instead of 'fun' (§3.7).");

                return new BoundYieldExpression(syntax, _factory.Unknown, BindExpression(syntax.Value));
            }

            if (_finallyDepth > 0)
            {
                Report(
                    SurtrDiagnosticCode.InvalidYield,
                    syntax.Span,
                    "A 'yield' cannot appear inside a 'finally': a 'finally' runs while the generator is being closed, and answering a close with an element would leave it alive after something was told it was disposed (§3.7).");
            }

            if (!syntax.IsDelegating)
                return new BoundYieldExpression(syntax, _factory.Unknown, BindConverted(syntax.Value, element));

            // `yield from xs` hands out every element of `xs`, so what has to convert to this
            // generator's element is one step of `xs`, not `xs` itself. What counts as iterable is
            // the same question `for-in` asks, answered by the same helper - a second definition
            // here is exactly how the two would drift apart.
            var sequence = BindExpression(syntax.Value);

            if (!TryFindIterableElementType(sequence.Type.NonNullable, out var delegated))
            {
                if (!sequence.Type.NonNullable.IsError)
                {
                    Report(
                        SurtrDiagnosticCode.NotSupportedOnType,
                        syntax.Value.Span,
                        $"'{sequence.Type.ToDisplayString()}' cannot be delegated to; it is not a built-in collection and does not satisfy IIterable.");
                }

                return new BoundYieldExpression(syntax, _factory.Unknown, sequence, _factory.ErrorType);
            }

            // Classified here rather than at emit, like every other conversion: what converts is
            // each element of the sequence, so there is no expression to hang a conversion node on,
            // but the decision is still the binder's.
            var elementConversion = delegated.IsError || element.IsError
                ? Conversion.Identity
                : _conversions.ClassifyImplicitOnly(delegated, element);

            if (elementConversion.Kind == ConversionKind.None)
            {
                Report(
                    SurtrDiagnosticCode.CannotConvert,
                    syntax.Value.Span,
                    $"'{_method.Name}' yields '{element.ToDisplayString()}', so it cannot delegate to a sequence of '{delegated.ToDisplayString()}'.");

                elementConversion = Conversion.Identity;
            }

            return new BoundYieldExpression(syntax, _factory.Unknown, sequence, delegated, elementConversion);
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
