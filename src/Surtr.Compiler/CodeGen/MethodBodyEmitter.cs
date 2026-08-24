#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax.Ast;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.CodeGen
{
    /// <summary>
    /// Turns a bound body into bytecode, onto the <see cref="SurtrCodeEmitter"/> of one
    /// <see cref="SurtrMethodBuilder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two invariants hold throughout. <see cref="Expression"/> leaves exactly one value on the
    /// operand stack, unless the expression's type is <c>void</c>, in which case it leaves none;
    /// <see cref="Statement"/> leaves the stack exactly as it found it. Everything else � how deep
    /// the stack gets, how wide a branch has to be, how many frame slots the body needs � is the
    /// emitter's own job and is never computed here.
    /// </para>
    /// <para>
    /// Anything it cannot lower raises <see cref="SurtrEmitException"/> rather than emitting
    /// something approximate. Being loud about the boundary is the point, and it is what let Step 4
    /// build the const-evaluable slice of this and know exactly where the slice ended.
    /// </para>
    /// <para>
    /// Three lowerings are worth knowing before reading: a <c>finally</c> is emitted on every exit
    /// path plus a catch-all that re-raises, which is what keeps <c>Leave</c>/<c>EndFinally</c> out
    /// of the instruction set; a lambda becomes a static module-level function whose captures are
    /// the closure's upvalues; and a <c>for-in</c> walks a built-in collection by index and only
    /// goes through <c>iterate()</c> for something that satisfies <c>IIterable&lt;T&gt;</c> without
    /// being one.
    /// </para>
    /// </remarks>
    public sealed class MethodBodyEmitter
    {
        /// <summary>Where a <c>break</c> and a <c>continue</c> inside one enclosing construct go.</summary>
        /// <remarks>
        /// A <c>switch</c> pushes one of these too, because �4.3 makes <c>break</c> leave the switch
        /// � but it is not a loop, so a <c>continue</c> inside it belongs to whatever loop encloses
        /// the switch and has to look straight past this entry. That is what
        /// <see cref="IsLoop"/> is for.
        /// </remarks>
        private readonly struct JumpTargets
        {
            public JumpTargets(SurtrLabel breakTarget, SurtrLabel continueTarget, bool isLoop, string? label, int finallyDepth)
            {
                Break = breakTarget;
                Continue = continueTarget;
                IsLoop = isLoop;
                Label = label;
                FinallyDepth = finallyDepth;
            }

            public SurtrLabel Break { get; }

            public SurtrLabel Continue { get; }

            /// <summary>Whether a <c>continue</c> may target this, which only a loop may.</summary>
            public bool IsLoop { get; }

            /// <summary>The name a labelled <c>break</c> may reach it by (�4.2).</summary>
            public string? Label { get; }

            /// <summary>
            /// How many <c>finally</c> blocks were in scope here, so a jump out knows how many it
            /// has to run on the way.
            /// </summary>
            public int FinallyDepth { get; }
        }

        /// <summary>One <c>inline</c> call site being spliced (�3.6).</summary>
        private readonly struct InlineFrame
        {
            public InlineFrame(
                MethodSymbol method,
                SurtrLabel exit,
                SurtrLocal result,
                bool hasResult,
                SurtrLocal? receiver,
                int finallyDepth)
            {
                Method = method;
                Exit = exit;
                Result = result;
                HasResult = hasResult;
                Receiver = receiver;
                FinallyDepth = finallyDepth;
            }

            /// <summary>The method being spliced, which is also what stops it splicing itself.</summary>
            public MethodSymbol Method { get; }

            /// <summary>Where a <c>return</c> in the spliced body goes instead of leaving the frame.</summary>
            public SurtrLabel Exit { get; }

            /// <summary>The slot the spliced body's result lands in.</summary>
            public SurtrLocal Result { get; }

            /// <summary>Whether it produces one.</summary>
            public bool HasResult { get; }

            /// <summary>The slot holding its receiver, for an instance method.</summary>
            public SurtrLocal? Receiver { get; }

            /// <summary>How many <c>finally</c> blocks were in scope when the splice started.</summary>
            public int FinallyDepth { get; }
        }

        /// <summary>How deep <c>inline</c> may splice before it gives up.</summary>
        /// <remarks>
        /// �3.6 makes inlining a request rather than a promise, so a ceiling is legal. Mutual
        /// recursion between two inline functions would otherwise expand forever, and the
        /// self-reference check alone does not catch it.
        /// </remarks>
        private const int MaxInlineDepth = 8;

        private readonly SurtrMethodBuilder _method;
        private readonly MethodSymbol _symbol;
        private readonly EmitContext _context;
        private readonly IReadOnlyDictionary<Symbol, int>? _captures;

        // On a lifted lambda body: which upvalue the enclosing instance arrived in.
        private readonly int? _receiverUpValue;

        private readonly Dictionary<LocalSymbol, SurtrLocal> _locals = new Dictionary<LocalSymbol, SurtrLocal>();
        private readonly Dictionary<ParameterSymbol, SurtrLocal> _splicedParameters = new Dictionary<ParameterSymbol, SurtrLocal>();
        private readonly List<JumpTargets> _jumps = new List<JumpTargets>();
        private readonly List<InlineFrame> _inlines = new List<InlineFrame>();
        private readonly List<BoundStatement> _finallies = new List<BoundStatement>();

        // Set by a labelled statement and consumed by the loop it labels, so `outer: for (...)` can
        // be reached by `break outer` without the loop node itself carrying the name.
        private string? _pendingLabel;

        /// <summary>Creates an emitter for one method's body.</summary>
        /// <param name="method">The method whose frame and instruction stream this fills in.</param>
        /// <param name="symbol">The method as the binder sees it, for its parameters and return type.</param>
        /// <param name="context">What every symbol in the body became.</param>
        /// <param name="captures">
        /// For a lifted lambda body: which upvalue index each captured symbol arrives in. Null for
        /// an ordinary method, which captures nothing.
        /// </param>
        /// <param name="receiverUpValue">
        /// For a lifted lambda body that reads the enclosing instance: which upvalue it arrived in.
        /// </param>
        public MethodBodyEmitter(
            SurtrMethodBuilder method,
            MethodSymbol symbol,
            EmitContext context,
            IReadOnlyDictionary<Symbol, int>? captures = null,
            int? receiverUpValue = null)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _captures = captures;
            _receiverUpValue = receiverUpValue;
        }

        private SurtrCodeEmitter Code => _method.Code;

        private DescriptorEmitter Descriptors => _context.Descriptors;

        /// <summary>Emits a whole body, and the fall-off return every method needs.</summary>
        /// <exception cref="SurtrEmitException">The body uses something not lowered yet.</exception>
        public void Emit(BoundStatement body)
        {
            Statement(body);

            // Flow analysis already rejected a value-returning method that can fall off its end, so
            // whatever reaches here returns nothing and needs the instruction saying so.
            if (Code.IsReachable)
                Code.ReturnVoid();
        }

        /// <summary>
        /// Emits a statement into a body something else finishes.
        /// </summary>
        /// <remarks>
        /// For the pieces the compiler splices in front of a body it did not write: a constructor's
        /// instance field initializers, and a static initializer's assignments. Each is a real
        /// statement and goes through the same lowering, but none of them ends a method.
        /// </remarks>
        public void EmitFragment(BoundStatement fragment) => Statement(fragment);

        #region Statements
        /// <summary>The node being lowered, which is the span a failure here belongs to.</summary>
        private SyntaxNode? _at;

        private void Statement(BoundStatement statement)
        {
            var previous = _at;
            _at = statement.Syntax;

            Lower(statement);
            _at = previous;
        }

        private void Lower(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundNopStatement:
                    return;

                case BoundBlockStatement block:
                    foreach (var inner in block.Statements)
                        Statement(inner);

                    return;

                case BoundExpressionStatement expression:
                    EffectOnly(expression.Expression);
                    return;

                case BoundLocalDeclarationStatement declaration:
                    EmitLocalDeclaration(declaration);
                    return;

                case BoundIfStatement conditional:
                    EmitIf(conditional);
                    return;

                case BoundWhileStatement loop:
                    EmitWhile(loop);
                    return;

                case BoundForStatement loop:
                    EmitFor(loop);
                    return;

                case BoundForInStatement loop:
                    EmitForIn(loop);
                    return;

                case BoundSwitchStatement @switch:
                    EmitSwitchStatement(@switch);
                    return;

                case BoundTryStatement @try:
                    EmitTry(@try);
                    return;

                case BoundReturnStatement @return:
                    EmitReturn(@return);
                    return;

                case BoundYieldStatement yield:
                    EmitYield(yield);
                    return;

                case BoundThrowStatement @throw:
                    Expression(@throw.Value);
                    Code.Throw();
                    return;

                case BoundBreakStatement jump:
                    EmitBreak(jump);
                    return;

                case BoundLabeledStatement labeled:
                    _pendingLabel = labeled.Label;
                    Statement(labeled.Statement);
                    _pendingLabel = null;
                    return;

                default:
                    throw Unsupported(statement.GetType().Name);
            }
        }

        /// <summary>
        /// Emits an expression for what it does rather than for what it produces.
        /// </summary>
        /// <remarks>
        /// A call asks for no result rather than producing one and popping it: the frame protocol
        /// drops a discarded return on the way out, so the <c>Pop</c> would be a real instruction
        /// buying nothing.
        /// </remarks>
        private void EffectOnly(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundCallExpression call when !call.Method.ReturnType.IsVoid:
                    EmitCall(call, discardResult: true);
                    return;

                case BoundClosureInvocationExpression invocation when !invocation.Type.IsVoid:
                    EmitClosureInvocation(invocation, discardResult: true);
                    return;

                // An assignment leaves its value because it is an expression; in statement position
                // there is nothing to leave it for.
                case BoundAssignmentExpression assignment:
                    EmitAssignment(assignment, keepValue: false);
                    return;

                // `i++;` and a `for` loop's step clause. Prefix and postfix differ only in which
                // value they leave behind, and here neither leaves one � so the distinction the
                // long form exists to make has nothing to make it about, and the update is one
                // instruction.
                case BoundUnaryExpression unary when IsIncrementOrDecrement(unary.Operator):
                    if (TryEmitInPlaceStep(unary))
                        return;

                    break;

                // `a?.f();` still has to skip the call when `a` is null, but neither path leaves a
                // value � so the guard is emitted without one rather than pushed and popped.
                case BoundNullConditionalExpression access:
                    EmitNullConditional(access, discardResult: true);
                    return;
            }

            Expression(expression);

            if (!expression.Type.IsVoid && !expression.Type.IsNever)
            {
                // A discarded value occupies its whole inline width: popping one slot of a
                // two-slot block would strand the rest under whatever runs next.
                if (TryMultiSlotWidth(expression.Type, out int width))
                {
                    var scratch = _method.DeclareLocals("$discard", width);
                    _slotWidthsByIndex[scratch.Index] = width;
                    Code.StoreValueLocal(scratch.Index, width);
                }
                else
                {
                    Code.Pop();
                }
            }
        }

        private void EmitLocalDeclaration(BoundLocalDeclarationStatement declaration)
        {
            var slot = Declare(declaration.Local);

            if (declaration.Initializer is null)
                return;

            Expression(declaration.Initializer);
            EmitStoreLocal(slot);
        }

        private void EmitIf(BoundIfStatement conditional)
        {
            var otherwise = Code.NewLabel();

            EmitConditionalJump(conditional.Condition, otherwise);
            Statement(conditional.Then);

            if (conditional.Else is null)
            {
                Code.MarkLabel(otherwise);
                return;
            }

            var end = Code.NewLabel();

            if (Code.IsReachable)
                Code.Jump(end);

            Code.MarkLabel(otherwise);
            Statement(conditional.Else);
            Code.MarkLabel(end);
        }

        private void EmitWhile(BoundWhileStatement loop)
        {
            var top = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);

            // `while (true)` tests nothing: flow analysis already reads it as a loop only a `break`
            // leaves, and emitting the test anyway would put a load and a branch on every iteration
            // to ask a question with one answer.
            if (!IsAlwaysTrue(loop.Condition))
                EmitConditionalJump(loop.Condition, end);

            // `continue` re-tests the condition, so it targets the top rather than the body.
            PushLoop(top, end);
            Statement(loop.Body);
            PopTargets();

            if (Code.IsReachable)
                Code.Jump(top);

            Code.MarkLabel(end);
        }

        private void EmitFor(BoundForStatement loop)
        {
            if (loop.Initializer is not null)
                Statement(loop.Initializer);

            var top = Code.NewLabel();
            var step = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);

            if (loop.Condition is not null && !IsAlwaysTrue(loop.Condition))
                EmitConditionalJump(loop.Condition, end);

            // `continue` runs the step clause before re-testing, which is the whole reason the loop
            // needs a target distinct from its top.
            PushLoop(step, end);
            Statement(loop.Body);
            PopTargets();

            Code.MarkLabel(step);

            if (loop.Step is not null)
                EffectOnly(loop.Step);

            Code.Jump(top);
            Code.MarkLabel(end);
        }

        /// <summary>Whether a loop's condition is written so that it never fails.</summary>
        /// <remarks>
        /// The same rule <see cref="FlowAnalysis"/> applies, and it has to be: that one decides
        /// whether the code after the loop is reachable, and this one decides whether the loop can
        /// reach it. Two different answers would be a body the analysis approved and the emitter
        /// left a way out of.
        /// </remarks>
        private static bool IsAlwaysTrue(BoundExpression condition)
            => condition is BoundLiteralExpression { Value: bool value } && value;

        /// <summary>
        /// Emits a condition as a branch to <paramref name="target"/>, taken when the condition is
        /// false � jumping over an <c>if</c>'s body, or out of a <c>while</c>/<c>for</c>.
        /// </summary>
        /// <remarks>
        /// Fuses a plain built-in comparison straight into the matching <c>JP&lt;cmp&gt;</c> opcode
        /// (�Opcodes � the family that "fuses a comparison and a branch, so the boolean never
        /// reaches the stack") instead of the naive `Compare` + `JumpIfFalse` pair. Anything else �
        /// a compound condition (`&amp;&amp;`, `||`), a user operator's call (already a
        /// <see cref="BoundCallExpression"/> by the time it reaches here, per Fix 1/Fix 5), or a
        /// condition that already special-cases inside <see cref="EmitBinary"/> (an absence test, or
        /// string ordering, which lowers to <c>compareTo</c>) � falls back to evaluating it as an
        /// ordinary boolean and testing the result, unchanged from before.
        /// </remarks>
        private void EmitConditionalJump(BoundExpression condition, SurtrLabel target)
        {
            // Each of these recognises a shape that plain `Expression(condition)` would otherwise
            // turn into a boolean on the stack, immediately tested by the `JumpIfFalse` below - and
            // fuses the test and the branch into one dispatch instead, the same idea
            // `TryFusedComparison` already applies to an ordinary comparison.
            if (condition is BoundBinaryExpression binary)
            {
                if (TryEmitAbsenceBranch(binary, target))
                    return;

                if (TryGetNullCheckOperand(binary, out var nullOperand, out bool checksForNull))
                {
                    Expression(nullOperand);

                    // `JumpIfNull`/`JumpIfNotNull` say "jump when true"; a false condition is what
                    // sends control to `target` here, so `x == null` (true means null) takes the
                    // not-null branch and `x != null` takes the null one - the mirror of the
                    // negation every other fused comparison below applies.
                    if (checksForNull)
                        Code.JumpIfNotNull(target);
                    else
                        Code.JumpIfNull(target);

                    return;
                }
            }

            // `x is T` has no negated fused opcode to jump on directly - only `JPInstanceOf` exists,
            // which jumps when the test is true - so the false path is an explicit fall-through
            // past an unconditional jump instead of the single dispatch a negated form would give.
            if (condition is BoundTypeTestExpression test)
            {
                Expression(test.Operand);

                var isInstance = Code.NewLabel();
                Code.JumpIfInstanceOf(Descriptors.Emit(test.TestedType.NonNullable), isInstance);
                Code.Jump(target);
                Code.MarkLabel(isInstance);
                return;
            }

            if (TryFusedComparison(condition, out var comparison, out var operandType, out var left, out var right))
            {
                Expression(left);
                Expression(right);

                // The opcode says "jump when true"; a false condition is what sends control to
                // `target` here, so the branch tests the negation of what was written.
                Code.JumpIfCompare(Negate(comparison), operandType, target);
                return;
            }

            Expression(condition);
            Code.JumpIfFalse(target);
        }

        /// <summary>
        /// Recognises a condition that <see cref="EmitBinary"/>'s ordinary comparison path would
        /// turn into a single `Compare` opcode, and reports the operands and comparison a fused
        /// branch would need instead. See <see cref="EmitConditionalJump"/> for what falls back.
        /// </summary>
        private bool TryFusedComparison(
            BoundExpression condition,
            out SurtrComparison comparison,
            out SurtrValueTypeCode operandType,
            out BoundExpression left,
            out BoundExpression right)
        {
            comparison = default;
            operandType = default;
            left = null!;
            right = null!;

            if (condition is not BoundBinaryExpression binary)
                return false;

            bool isOrdering;

            switch (binary.Operator)
            {
                case BinaryOperator.Equal: comparison = SurtrComparison.Equal; isOrdering = false; break;
                case BinaryOperator.NotEqual: comparison = SurtrComparison.NotEqual; isOrdering = false; break;
                case BinaryOperator.Less: comparison = SurtrComparison.Less; isOrdering = true; break;
                case BinaryOperator.LessEqual: comparison = SurtrComparison.LessOrEqual; isOrdering = true; break;
                case BinaryOperator.Greater: comparison = SurtrComparison.Greater; isOrdering = true; break;
                case BinaryOperator.GreaterEqual: comparison = SurtrComparison.GreaterOrEqual; isOrdering = true; break;

                case BinaryOperator.ReferenceEqual:
                case BinaryOperator.ReferenceNotEqual:
                    comparison = binary.Operator == BinaryOperator.ReferenceEqual
                        ? SurtrComparison.Equal
                        : SurtrComparison.NotEqual;
                    operandType = SurtrValueTypeCode.Object;
                    left = binary.Left;
                    right = binary.Right;
                    return true;

                default:
                    return false;
            }

            // TryEmitAbsenceTest intercepts exactly this shape before EmitBinary's ordinary path
            // ever runs � a fused branch has to defer to it the same way.
            if (binary.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual
                && ((IsNullLiteral(binary.Right) && IsNullablePrimitive(binary.Left.Type))
                    || (IsNullLiteral(binary.Left) && IsNullablePrimitive(binary.Right.Type))))
            {
                return false;
            }

            operandType = TypeCodeOf(binary.Left.Type);

            // String equality has a fused opcode (JPStrEQ/JPStrNE); string ordering does not � it
            // lowers to compareTo (EmitStringOrdering), so only equality fuses here.
            if (operandType == SurtrValueTypeCode.String && isOrdering)
                return false;

            // Equality on a still-abstract type parameter needs the runtime's own value comparer
            // (DynEQ/DynNE - see ComparisonOpCode), which has no fused branch form of its own. This
            // is already the rare, allocating-adjacent path a generic body without an equality
            // constraint takes, so falling back to the ordinary Compare-then-JumpIfFalse shape costs
            // one extra dispatch on a path that was never going to be fast.
            if (operandType == SurtrValueTypeCode.Erased)
                return false;

            // An inline-typed operand (a range, a multi-field value class, a tuple) compares
            // structurally through EmitValueClassEquality - a per-family fused branch does not
            // exist for it and would read the wrong slot count anyway.
            if (SlotCountOfType(binary.Left.Type) > 1 || SlotCountOfType(binary.Right.Type) > 1)
                return false;

            left = binary.Left;
            right = binary.Right;
            return true;
        }

        private static SurtrComparison Negate(SurtrComparison comparison) => comparison switch
        {
            SurtrComparison.Equal => SurtrComparison.NotEqual,
            SurtrComparison.NotEqual => SurtrComparison.Equal,
            SurtrComparison.Less => SurtrComparison.GreaterOrEqual,
            SurtrComparison.LessOrEqual => SurtrComparison.Greater,
            SurtrComparison.Greater => SurtrComparison.LessOrEqual,
            SurtrComparison.GreaterOrEqual => SurtrComparison.Less,
            _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
        };

        /// <summary>
        /// Lowers a <c>for-in</c>, by index wherever that is possible.
        /// </summary>
        /// <remarks>
        /// �4.2 defines <c>for-in</c> against <c>IIterable&lt;T&gt;</c>, and every built-in
        /// collection really does satisfy it � but walking one by index allocates no cursor and
        /// costs two instructions per step, so the contract is what makes an <c>int[]</c> assignable
        /// to an <c>IIterable&lt;int&gt;</c> rather than what a loop over one goes through. That
        /// choice is left here rather than taken in the binder for exactly this reason.
        /// </remarks>
        private void EmitForIn(BoundForInStatement loop)
        {
            var sequence = loop.Sequence.Type.NonNullable;

            if (sequence.SpecialType == SpecialType.Range)
            {
                EmitForInRange(loop);
                return;
            }

            switch (sequence.TypeKind)
            {
                case TypeSymbolKind.Array:
                    EmitForInIndexed(loop, SurtrIterationKind.Array);
                    return;

                case TypeSymbolKind.Tuple:
                    EmitForInIndexed(loop, SurtrIterationKind.Tuple);
                    return;

                case TypeSymbolKind.Dictionary:
                    EmitForInDictionary(loop, (DictionaryTypeSymbol)sequence);
                    return;

                case TypeSymbolKind.Generator:
                    EmitForInGenerator(loop, (GeneratorTypeSymbol)sequence);
                    return;
            }

            if (sequence.SpecialType == SpecialType.String)
            {
                EmitForInIndexed(loop, SurtrIterationKind.String);
                return;
            }

            EmitForInIterable(loop);
        }

        /// <summary>Which indexed walk a built-in collection takes.</summary>
        private enum SurtrIterationKind
        {
            Array,
            String,
            Tuple,
        }

        /// <summary>
        /// Walks a range without ever building one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A range written inline in a loop header is the case the old heap form's documentation
        /// says must not allocate: both bounds are already on the stack, so the loop reads them
        /// into two slots and counts between them.
        /// </para>
        /// <para>
        /// A range that arrived some other way is walked from its own block: the variable starts
        /// at slot 0, and the limit is derived from slot 1 under control of slot 2's flag - no
        /// pack, no call, no length getter. The exclusive limit is <c>end - 1</c> except where
        /// that subtraction would wrap (<c>end == int.MinValue</c>, which only an empty range can
        /// carry), where the loop is laid out already finished instead. Either way one comparison
        /// against <c>limit</c> ends the loop, so emptiness costs a single failed test.
        /// </para>
        /// </remarks>
        private void EmitForInRange(BoundForInStatement loop)
        {
            var variable = Declare(loop.Variable);
            var limit = _method.DeclareLocal("$limit");

            // Which comparison ends the loop: an inclusive limit is exited past (`>`), an
            // exclusive one is exited at (`>=`). The inline header knows its form statically;
            // the escaped branch folds the flag into the limit itself, so it always exits past.
            bool limitIsInclusive;

            if (loop.Sequence is BoundBinaryExpression
                {
                    Operator: BinaryOperator.Range or BinaryOperator.RangeInclusive
                } bounds)
            {
                limitIsInclusive = bounds.Operator == BinaryOperator.RangeInclusive;
                Expression(bounds.Left);
                EmitStoreLocal(variable);
                Expression(bounds.Right);
                EmitStoreLocal(limit);
            }
            else
            {
                limitIsInclusive = true;

                int baseSlot = EnsureLocalRange(loop.Sequence, RangeSlotWidth);

                Code.LoadLocalField(baseSlot, 0);
                EmitStoreLocal(variable);

                var exclusive = Code.NewLabel();
                var subtract = Code.NewLabel();
                var finish = Code.NewLabel();

                Code.LoadLocalField(baseSlot, 2);
                Code.JumpIfFalse(exclusive);

                // Inclusive: the bound is its own limit.
                Code.LoadLocalField(baseSlot, 1);
                EmitStoreLocal(limit);
                Code.Jump(finish);

                Code.MarkLabel(exclusive);
                Code.LoadLocalField(baseSlot, 1);
                Code.LoadInt(int.MinValue);
                Code.JumpIfCompare(SurtrComparison.NotEqual, SurtrValueTypeCode.Integer, subtract);

                // end == int.MinValue and exclusive: nothing can have been inside, so start the
                // walk already past it rather than subtract one into a wrap.
                Code.LoadInt(1);
                EmitStoreLocal(variable);
                Code.LoadInt(0);
                EmitStoreLocal(limit);
                Code.Jump(finish);

                Code.MarkLabel(subtract);
                Code.LoadLocalField(baseSlot, 1);
                Code.LoadInt(1);
                Code.Subtract(SurtrValueTypeCode.Integer);
                EmitStoreLocal(limit);

                Code.MarkLabel(finish);
            }

            var top = Code.NewLabel();
            var step = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            EmitLoadLocal(variable);
            EmitLoadLocal(limit);
            Code.JumpIfCompare(
                limitIsInclusive ? SurtrComparison.Greater : SurtrComparison.GreaterOrEqual,
                SurtrValueTypeCode.Integer,
                end);

            PushLoop(step, end);
            Statement(loop.Body);
            PopTargets();

            Code.MarkLabel(step);
            Code.IncrementLocal(variable, 1);
            Code.Jump(top);
            Code.MarkLabel(end);
        }

        private SurtrMethodInfo RangeAccessor(string property)
            => SurtrBuiltIns.Range.TryGetMethods(MemberNames.Getter(property), out var overloads) && overloads.Length == 1
                ? overloads[0]
                : throw Unsupported($"a for-in over a range, because 'range.{property}' could not be found");

        private void EmitForInIndexed(BoundForInStatement loop, SurtrIterationKind kind)
        {
            var source = _method.DeclareLocal("$sequence");
            var index = _method.DeclareLocal("$index");
            var variable = Declare(loop.Variable);

            Expression(loop.Sequence);

            // A tuple sequence is a block now, but the walk indexes it dynamically - and the
            // frame has no addressing mode for a dynamic offset into a local range. Packing once
            // at loop entry buys the whole indexed walk on the boxed form, whose elements the
            // collector already follows.
            if (kind == SurtrIterationKind.Tuple
                && loop.Sequence.Type.NonNullable is TupleTypeSymbol { ElementTypes.Count: > 0 } tuple)
            {
                Code.PackTuple(Descriptors.Emit(tuple), ValueTypeLayout.WidthOfType(tuple));
            }

            EmitStoreLocal(source);
            Code.LoadInt(0);
            EmitStoreLocal(index);

            var top = Code.NewLabel();
            var step = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            EmitLoadLocal(index);
            EmitLoadLocal(source);
            Length(kind);
            Code.JumpIfCompare(SurtrComparison.GreaterOrEqual, SurtrValueTypeCode.Integer, end);

            EmitLoadLocal(source);
            EmitLoadLocal(index);
            Element(kind);
            // An element read off a collection's own storage arrives as the boxed form when its
            // type stores inline - the collection keeps one reference per element - so the
            // walk's variable, which holds the value itself, unpacks it.
            UnpackIfMultiSlot(loop.Variable.Type);
            EmitStoreLocal(variable);

            PushLoop(step, end);
            Statement(loop.Body);
            PopTargets();

            Code.MarkLabel(step);
            Code.IncrementLocal(index, 1);
            Code.Jump(top);
            Code.MarkLabel(end);
        }

        private void Length(SurtrIterationKind kind)
        {
            switch (kind)
            {
                case SurtrIterationKind.Array: Code.ArrLen(); return;
                case SurtrIterationKind.String: Code.StrLen(); return;
                default: Code.TupLen(); return;
            }
        }

        private void Element(SurtrIterationKind kind)
        {
            switch (kind)
            {
                case SurtrIterationKind.Array: Code.ArrGet(); return;
                case SurtrIterationKind.String: Code.StrGet(); return;
                default: Code.TupGet(); return;
            }
        }

        /// <summary>
        /// Walks a dictionary over a snapshot of its keys, yielding <c>(K, V)</c> pairs.
        /// </summary>
        /// <remarks>
        /// The snapshot is what the built-in iterator does too, and for the same reason: a walk that
        /// read the live table would have to say what happens when the body inserts. Taking the keys
        /// once makes that question have an answer � the loop sees the keys the dictionary had when
        /// it started.
        /// </remarks>
        private void EmitForInDictionary(BoundForInStatement loop, DictionaryTypeSymbol dictionary)
        {
            if (loop.Variable.Type.NonNullable is not TupleTypeSymbol pair || pair.ElementTypes.Count != 2)
                throw Unsupported($"a for-in over '{dictionary.ToDisplayString()}' whose variable is not a (key, value) pair");

            var source = _method.DeclareLocal("$dict");
            var keys = _method.DeclareLocal("$keys");
            var index = _method.DeclareLocal("$index");
            var key = _method.DeclareLocal("$key");
            var value = _method.DeclareLocal("$value");
            var variable = Declare(loop.Variable);

            Expression(loop.Sequence);
            EmitStoreLocal(source);
            EmitLoadLocal(source);
            Code.DictionaryKeys(SurtrClassReference.Array(Descriptors.Emit(dictionary.KeyType)));
            EmitStoreLocal(keys);
            Code.LoadInt(0);
            EmitStoreLocal(index);

            var top = Code.NewLabel();
            var step = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            EmitLoadLocal(index);
            EmitLoadLocal(keys);
            Code.ArrLen();
            Code.JumpIfCompare(SurtrComparison.GreaterOrEqual, SurtrValueTypeCode.Integer, end);

            // The key is read from the snapshot once and reused � for the value lookup and for the
            // packed pair � instead of re-indexing the snapshot's array a second time. The pair is
            // still materialised per iteration, because the loop variable's own type is a tuple and
            // the body reads it as one (�4.2 has no destructuring for-in); what is avoided is the
            // second array read, which carries a bounds check the first one already paid.
            EmitLoadLocal(keys);
            EmitLoadLocal(index);
            Code.ArrGet();
            EmitStoreLocal(key);

            EmitLoadLocal(source);
            EmitLoadLocal(key);
            Code.DictGet();
            EmitStoreLocal(value);

            EmitLoadLocal(key);
            EmitLoadLocal(value);

            // The pair is a value now: loading key then value lays out exactly the flattened
            // block, so storing it into the loop variable's own range - wider than one slot,
            // which is what makes this a block store rather than a pack - needs nothing else.
            // No object per iteration any more.
            EmitStoreLocal(variable);

            PushLoop(step, end);
            Statement(loop.Body);
            PopTargets();

            Code.MarkLabel(step);
            Code.IncrementLocal(index, 1);
            Code.Jump(top);
            Code.MarkLabel(end);
        }

        /// <summary>
        /// Walks anything that satisfies <c>IIterable&lt;T&gt;</c> through its cursor (�4.2).
        /// </summary>
        /// <remarks>
        /// The general path, and the only one that allocates. Every call goes through the interface
        /// dispatch table rather than a vtable slot, because the receiver's own class is not what
        /// the loop was written against.
        /// <para>
        /// A <c>value class</c> receiver must be boxed first, exactly as <see cref="EmitCall"/>
        /// boxes one before a call that might resolve through its class: the loop's value is the
        /// erased field (e.g. <c>Sequence&lt;T&gt;</c>'s closure), which is not an object and has no
        /// interface table for <c>CallInterface</c> to dispatch on. Boxing to the boxed form makes
        /// the receiver a real object whose class carries the implemented <c>IIterable&lt;T&gt;</c>.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Walks a generator through its own opcodes rather than through <c>IIterable&lt;T&gt;</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same bargain §4.2 already makes for an array: the contract is what makes a
        /// <c>generator&lt;int&gt;</c> assignable to an <c>IIterable&lt;int&gt;</c>, and this is what
        /// a loop over one actually runs. Going through the contract would cost, per element, an
        /// interface dispatch, a native call and a nested entry into the interpreter - which is
        /// most of what generators exist to save over the hand-written iterators they replace.
        /// </para>
        /// <para>
        /// <c>GenIterate</c> is emitted once, above the loop, and is the compiled copy of the check
        /// <c>iterate()</c> makes: a generator object is single-use, so walking one that has already
        /// started traps rather than quietly iterating nothing (§12.2).
        /// </para>
        /// </remarks>
        private void EmitForInGenerator(BoundForInStatement loop, GeneratorTypeSymbol sequence)
        {
            var cursor = _method.DeclareLocal("$generator");
            var variable = Declare(loop.Variable);

            Expression(loop.Sequence);
            Code.GenIterate();
            EmitStoreLocal(cursor);

            var top = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            EmitLoadLocal(cursor);
            Code.GenResume();
            Code.JumpIfFalse(end);

            EmitLoadLocal(cursor);
            Code.GenCurrent();

            // `Yield` wrote this slot from a body compiled against the declared element, so a
            // primitive is raw unless the declaration erased it, in which case it is a box - the
            // same two representations `Unerase` already resolves from the value's own tag. Nothing
            // is boxed here: a generator whose whole point is to be cheaper than the iterator class
            // it replaces cannot afford an allocation per element.
            Unerase(loop.Variable.Type);

            EmitStoreLocal(variable);

            PushLoop(top, end);
            Statement(loop.Body);
            PopTargets();

            if (Code.IsReachable)
                Code.Jump(top);

            Code.MarkLabel(end);
        }

        private void EmitForInIterable(BoundForInStatement loop)
        {
            var iterate = ContractMethod(SurtrBuiltIns.IIterable, "iterate");
            var moveNext = ContractMethod(SurtrBuiltIns.IIterator, "moveNext");
            var current = ContractMethod(SurtrBuiltIns.IIterator, MemberNames.Getter("current"));

            var cursor = _method.DeclareLocal("$iterator");
            var variable = Declare(loop.Variable);

            Expression(loop.Sequence);
            BoxIfMultiSlot(loop.Sequence.Type);
            Code.CallInterface(iterate);
            EmitStoreLocal(cursor);

            var top = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            EmitLoadLocal(cursor);
            Code.CallInterface(moveNext);
            Code.JumpIfFalse(end);

            EmitLoadLocal(cursor);
            Code.CallInterface(current);

            // `current` is typed by the contract's own parameter, so it reads back erased - but
            // what it hands back is the collection's own storage, and a built-in collection stores
            // a primitive raw (an int pushed into an int[] is never boxed on the way in), while a
            // collection built from scratch inside a still-generic body stores primitives already
            // boxed (§1.11). The receiver reached through `CallInterface` is only known to satisfy
            // the contract, not which of the two it is - which is exactly the ambiguity `Unerase`
            // resolves from the value's own tag. It used to be normalised away by boxing first;
            // that was one allocation per element on every walk through a contract, for a question
            // `UnboxDynamic` answers for free.
            Unerase(loop.Variable.Type);

            EmitStoreLocal(variable);

            PushLoop(top, end);
            Statement(loop.Body);
            PopTargets();

            if (Code.IsReachable)
                Code.Jump(top);

            Code.MarkLabel(end);
        }

        private SurtrMethodInfo ContractMethod(SurtrInterface contract, string name)
            => contract.TryGetMethods(name, out var overloads) && overloads.Length == 1
                ? overloads[0]
                : throw Unsupported($"a for-in, because '{contract.Name}.{name}' could not be found");

        /// <summary>
        /// Emits a switch statement, picking the encoding from what its labels are.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An integer or character subject with constant labels goes through
        /// <see cref="SurtrCodeEmitter.SwitchOn"/>, which picks a jump table or a binary-searched
        /// key table for itself. A string subject hashes first, since
        /// <c>SurtrString.ComputeHash</c> depends only on the text and so gives the same answer in
        /// the compiler as at run time � that is the whole reason <c>StrHash</c> exists.
        /// </para>
        /// <para>
        /// Everything else is a chain of comparisons. That covers <c>bool</c>, <c>float</c> and an
        /// enum, and for an enum it is also the <em>right</em> shape: a case is a singleton
        /// instance, so matching one is a reference compare, and switching on an ordinal would need
        /// a member the enum does not have.
        /// </para>
        /// </remarks>
        private void EmitSwitchStatement(BoundSwitchStatement @switch)
        {
            var subject = _method.DeclareLocal("$subject");

            Expression(@switch.Subject);
            EmitStoreLocal(subject);

            var end = Code.NewLabel();
            var bodies = new SurtrLabel[@switch.Sections.Count];
            var labels = new List<BoundExpression>[@switch.Sections.Count];
            SurtrLabel? fallback = null;

            for (int i = 0; i < bodies.Length; i++)
            {
                bodies[i] = Code.NewLabel();
                labels[i] = new List<BoundExpression>(@switch.Sections[i].Labels);

                if (@switch.Sections[i].IsDefault)
                    fallback = bodies[i];
            }

            EmitDispatch(@switch.Subject.Type, subject, bodies, labels, fallback ?? end);

            // A section runs to its own end: �4.3 makes fall-through explicit, so nothing here
            // continues into the next one. `break` leaves the switch; `continue` looks past it to
            // whatever loop encloses it, which is what pushing this as a non-loop arranges.
            PushTargets(end, end, isLoop: false);

            for (int i = 0; i < bodies.Length; i++)
            {
                Code.MarkLabel(bodies[i]);

                foreach (var inner in @switch.Sections[i].Statements)
                    Statement(inner);

                if (Code.IsReachable)
                    Code.Jump(end);
            }

            PopTargets();
            Code.MarkLabel(end);
        }

        /// <summary>
        /// Emits whatever gets control from a saved subject to the arm that matches it.
        /// </summary>
        private void EmitDispatch(
            TypeSymbol subjectType,
            SurtrLocal subject,
            SurtrLabel[] arms,
            List<BoundExpression>[] labels,
            SurtrLabel fallback)
        {
            var family = TypeCodeOf(subjectType);

            if (family is SurtrValueTypeCode.Integer or SurtrValueTypeCode.Character
                && TryCollectIntegerCases(arms, labels, out var cases))
            {
                EmitLoadLocal(subject);

                if (family == SurtrValueTypeCode.Character)
                    Code.Convert(SurtrValueTypeCode.Character, SurtrValueTypeCode.Integer);

                Code.SwitchOn(cases, fallback);
                return;
            }

            if (family == SurtrValueTypeCode.String && TryEmitStringDispatch(subject, arms, labels, fallback))
                return;

            for (int i = 0; i < arms.Length; i++)
            {
                foreach (var label in labels[i])
                {
                    EmitLoadLocal(subject);
                    Expression(label);
                    Code.JumpIfCompare(SurtrComparison.Equal, family, arms[i]);
                }
            }

            Code.Jump(fallback);
        }

        /// <summary>
        /// Collects every arm's constant key, or gives up if one of them is not a constant.
        /// </summary>
        /// <remarks>
        /// A duplicate key would make <see cref="SurtrCodeEmitter.SwitchOn"/> throw, and the binder
        /// does not reject one � so it is caught here and the chain takes over, which matches the
        /// first arm exactly as the source reads.
        /// </remarks>
        private static bool TryCollectIntegerCases(
            SurtrLabel[] arms,
            List<BoundExpression>[] labels,
            out List<SurtrSwitchCase> cases)
        {
            cases = new List<SurtrSwitchCase>();
            var seen = new HashSet<int>();

            for (int i = 0; i < arms.Length; i++)
            {
                foreach (var label in labels[i])
                {
                    if (ConstantOf(label) is not object value)
                        return false;

                    int key;
                    switch (value)
                    {
                        case long integer when integer >= int.MinValue && integer <= int.MaxValue:
                            key = (int)integer;
                            break;

                        case char character:
                            key = character;
                            break;

                        default:
                            return false;
                    }

                    if (!seen.Add(key))
                        return false;

                    cases.Add(new SurtrSwitchCase(key, arms[i]));
                }
            }

            return cases.Count > 0;
        }

        /// <summary>
        /// Emits a string switch as a hash lookup with an equality confirmation.
        /// </summary>
        /// <remarks>
        /// Two texts may hash alike, so a hash arm is not an answer � it is a shortlist. Each
        /// distinct hash gets a block that compares the subject against every label sharing it and
        /// falls through to the default, which is what makes a collision cost one extra compare
        /// instead of being a miscompile.
        /// </remarks>
        private bool TryEmitStringDispatch(
            SurtrLocal subject,
            SurtrLabel[] arms,
            List<BoundExpression>[] labels,
            SurtrLabel fallback)
        {
            var byHash = new Dictionary<int, List<(string Text, SurtrLabel Arm)>>();
            var written = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < arms.Length; i++)
            {
                foreach (var label in labels[i])
                {
                    if (ConstantOf(label) is not string text || !written.Add(text))
                        return false;

                    int hash = SurtrString.ComputeHash(text);
                    if (!byHash.TryGetValue(hash, out var bucket))
                        byHash.Add(hash, bucket = new List<(string, SurtrLabel)>());

                    bucket.Add((text, arms[i]));
                }
            }

            if (byHash.Count == 0)
                return false;

            var cases = new List<SurtrSwitchCase>(byHash.Count);
            var confirmations = new List<(SurtrLabel Label, List<(string Text, SurtrLabel Arm)> Bucket)>(byHash.Count);

            foreach (var pair in byHash)
            {
                var confirm = Code.NewLabel();
                cases.Add(new SurtrSwitchCase(pair.Key, confirm));
                confirmations.Add((confirm, pair.Value));
            }

            EmitLoadLocal(subject);
            Code.StrHash();
            Code.SwitchOn(cases, fallback);

            foreach (var (label, bucket) in confirmations)
            {
                Code.MarkLabel(label);

                foreach (var (text, arm) in bucket)
                {
                    EmitLoadLocal(subject);
                    Code.LoadString(text);
                    Code.JumpIfCompare(SurtrComparison.Equal, SurtrValueTypeCode.String, arm);
                }

                Code.Jump(fallback);
            }

            return true;
        }

        /// <summary>
        /// Emits a <c>try</c>, its handlers and its <c>finally</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The instruction set has no <c>finally</c>, deliberately: the block is emitted on each
        /// normal exit path � falling off the try, a <c>return</c>, a <c>break</c> leaving it � plus
        /// once more behind a catch-all that runs it and re-raises. That is what javac does, and
        /// what keeps <c>Leave</c>/<c>EndFinally</c> out of the set.
        /// </para>
        /// <para>
        /// The catch-all's protected range covers the handlers too, not just the guarded block,
        /// because a <c>finally</c> has to run when a <c>catch</c> throws. The type-specific
        /// handlers cover only the guarded block, so a <c>catch</c> can never catch what it itself
        /// raised.
        /// </para>
        /// </remarks>
        private void EmitTry(BoundTryStatement @try)
        {
            var end = Code.NewLabel();
            var guarded = _method.BeginTry();
            SurtrProtectedRegion? everything = @try.Finally is null ? null : _method.BeginTry();

            PushFinally(@try.Finally);
            Statement(@try.Body);
            PopFinally(@try.Finally);

            _method.EndTry(guarded);

            if (Code.IsReachable)
            {
                RunFinally(@try.Finally);
                Code.Jump(end);
            }

            foreach (var clause in @try.Catches)
            {
                var handler = Code.NewLabel();
                Code.MarkHandler(handler);
                _method.AddCatch(guarded, Descriptors.Emit(clause.Exception.Type.NonNullable), handler);

                EmitStoreLocal(Declare(clause.Exception));

                PushFinally(@try.Finally);
                Statement(clause.Body);
                PopFinally(@try.Finally);

                if (Code.IsReachable)
                {
                    RunFinally(@try.Finally);
                    Code.Jump(end);
                }
            }

            if (everything is SurtrProtectedRegion fault)
            {
                _method.EndTry(fault);

                var rethrow = Code.NewLabel();
                Code.MarkHandler(rethrow);
                _method.AddCatchAll(fault, rethrow);

                var raised = _method.DeclareLocal("$raised");
                EmitStoreLocal(raised);
                RunFinally(@try.Finally);
                EmitLoadLocal(raised);
                Code.Throw();
            }

            Code.MarkLabel(end);
        }

        /// <summary>Emits one <c>finally</c> body inline, if there is one.</summary>
        private void RunFinally(BoundStatement? block)
        {
            if (block is not null)
                Statement(block);
        }

        private void PushFinally(BoundStatement? block)
        {
            if (block is not null)
                _finallies.Add(block);
        }

        private void PopFinally(BoundStatement? block)
        {
            if (block is not null)
                _finallies.RemoveAt(_finallies.Count - 1);
        }

        /// <summary>Emits every <c>finally</c> a jump out to <paramref name="depth"/> passes through.</summary>
        private void UnwindTo(int depth)
        {
            for (int i = _finallies.Count - 1; i >= depth; i--)
                Statement(_finallies[i]);
        }

        /// <summary>
        /// Emits a <c>yield</c>: one value, then a suspension (§3.7).
        /// </summary>
        /// <remarks>
        /// A single slot leaves the frame, so an element whose type is wider than one - a
        /// multi-field <c>value class</c>, a tuple - is boxed on the way out, exactly as the
        /// interface path's <c>current</c> would have to box it. One representation rather than two
        /// that would have to agree; keeping it wide on the compiled path is a measurable
        /// optimisation for later, not a correctness question.
        /// </remarks>
        private void EmitYield(BoundYieldStatement yield)
        {
            if (yield.IsDelegating)
            {
                EmitYieldFrom(yield);
                return;
            }

            Expression(yield.Value);
            BoxIfMultiSlot(yield.Value.Type);
            Code.Yield();
        }

        /// <summary>
        /// Emits a <c>yield from</c>: every element of a sequence, in order (§3.7).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two lowerings, picked by the operand's static type, and the split is the same one §4.2
        /// makes for <c>for-in</c>. A <c>generator&lt;T&gt;</c> becomes a <b>link</b>: the
        /// delegating generator suspends without a frame and every later resume walks straight to
        /// the innermost one, so an N-deep chain costs one frame copy per element rather than N.
        /// Anything else becomes the <b>loop</b> the delegation means - <c>for (x in it) yield x;</c>
        /// written out - because an array or a user cursor has no frame to link to.
        /// </para>
        /// <para>
        /// The loop is emitted here rather than lowered in the binder for the same reason
        /// <c>for-in</c> is: which of the two applies is a fact about representation, and the binder
        /// deals in types.
        /// </para>
        /// </remarks>
        private void EmitYieldFrom(BoundYieldStatement yield)
        {
            var sequence = yield.Value.Type.NonNullable;

            // The link hands the inner generator's own values straight to the consumer, so it is
            // only available when they need no changing on the way. An `int` sequence delegated to
            // by a `float` generator has to convert each element, which is the loop's job.
            if (sequence.TypeKind == TypeSymbolKind.Generator
                && yield.DelegatedConversion.Kind == ConversionKind.Identity)
            {
                Expression(yield.Value);
                Code.GenDelegate();
                return;
            }

            // The general path. Deliberately built from the pieces a `for-in` over the same
            // expression would emit, so a sequence that is delegated to and one that is looped over
            // cannot disagree about what iterating it means.
            var iterate = ContractMethod(SurtrBuiltIns.IIterable, "iterate");
            var moveNext = ContractMethod(SurtrBuiltIns.IIterator, "moveNext");
            var current = ContractMethod(SurtrBuiltIns.IIterator, MemberNames.Getter("current"));

            var cursor = _method.DeclareLocal("$delegated");

            Expression(yield.Value);
            BoxIfMultiSlot(yield.Value.Type);
            Code.CallInterface(iterate);
            EmitStoreLocal(cursor);

            var top = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            EmitLoadLocal(cursor);
            Code.CallInterface(moveNext);
            Code.JumpIfFalse(end);

            EmitLoadLocal(cursor);
            Code.CallInterface(current);
            Unerase(yield.DelegatedElementType!);

            // Each element converts to the declaring generator's own element, by the conversion the
            // binder already classified for exactly this loop.
            if (yield.DelegatedConversion.Kind != ConversionKind.Identity)
                EmitConversionTail(yield.DelegatedConversion, yield.DelegatedElementType!, _symbol.YieldType!);

            BoxIfMultiSlot(_symbol.YieldType!);
            Code.Yield();

            Code.Jump(top);
            Code.MarkLabel(end);
        }

        private void EmitReturn(BoundReturnStatement @return)
        {
            // A spliced body's `return` leaves the splice, not the frame � �3.6 makes inlining
            // invisible, and a real Ret here would end the caller.
            if (_inlines.Count > 0)
            {
                var frame = _inlines[_inlines.Count - 1];

                if (@return.Value is not null && frame.HasResult)
                {
                    Expression(@return.Value);
                    EmitStoreLocal(frame.Result);
                }
                else if (@return.Value is not null)
                {
                    EffectOnly(@return.Value);
                }

                UnwindTo(frame.FinallyDepth);
                Code.Jump(frame.Exit);
                return;
            }

            if (@return.Value is null)
            {
                UnwindTo(0);
                Code.ReturnVoid();
                return;
            }

            // The value is computed before the `finally` runs, which is what makes a `finally` that
            // touches the returned local unable to change what was already returned.
            if (_finallies.Count > 0)
            {
                var result = DeclareTemp("$result", @return.Value.Type);
                Expression(@return.Value);
                EmitStoreLocal(result);
                UnwindTo(0);
                EmitLoadLocal(result);
                EmitReturnOf(@return.Value.Type);
                return;
            }

            Expression(@return.Value);
            EmitReturnOf(@return.Value.Type);
        }

        /// <summary>Returns whatever is on top of the operand stack, in the form its width demands.</summary>
        /// <remarks>
        /// A multi-field value class returns as a contiguous block - one <c>ReturnValues</c> -
        /// while everything else keeps the single-slot return. The caller needs no counterpart:
        /// it knows the callee's declared type, so it knows how many slots came back.
        /// </remarks>
        private void EmitReturnOf(TypeSymbol returnType)
        {
            int width = SlotCountOfType(returnType);

            if (width > 1)
                Code.ReturnValues(width);
            else
                Code.ReturnValue();
        }

        private void EmitBreak(BoundBreakStatement jump)
        {
            for (int i = _jumps.Count - 1; i >= 0; i--)
            {
                // A `continue` never means a switch, and a label is only ever a loop's.
                if (jump.IsContinue && !_jumps[i].IsLoop)
                    continue;

                if (jump.Label is not null && !string.Equals(_jumps[i].Label, jump.Label, StringComparison.Ordinal))
                    continue;

                UnwindTo(_jumps[i].FinallyDepth);
                Code.Jump(jump.IsContinue ? _jumps[i].Continue : _jumps[i].Break);
                return;
            }

            // The binder already rejected a jump outside a loop, so reaching here means the label
            // was bound against a loop this emitter did not push - a bug, not bad input.
            throw Unsupported($"a '{(jump.IsContinue ? "continue" : "break")}' with no loop to leave");
        }

        private void PushLoop(SurtrLabel continueTarget, SurtrLabel breakTarget)
            => PushTargets(continueTarget, breakTarget, isLoop: true);

        private void PushTargets(SurtrLabel continueTarget, SurtrLabel breakTarget, bool isLoop)
        {
            // Only a loop takes the pending label: `outer: switch (...)` names nothing a jump can
            // reach, so leaving it pending would silently attach it to the next loop instead.
            _jumps.Add(new JumpTargets(breakTarget, continueTarget, isLoop, isLoop ? _pendingLabel : null, _finallies.Count));

            if (isLoop)
                _pendingLabel = null;
        }

        private void PopTargets() => _jumps.RemoveAt(_jumps.Count - 1);
        #endregion

        #region Expressions
        private void Expression(BoundExpression expression)
        {
            var previous = _at;
            _at = expression.Syntax;

            Lower(expression);
            _at = previous;
        }

        private void Lower(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundLiteralExpression literal:
                    EmitLiteral(literal);
                    return;

                case BoundLocalExpression local:
                    LoadSymbol(local.Local, () => EmitLoadLocal(Slot(local.Local)));
                    return;

                case BoundParameterExpression parameter:
                    LoadSymbol(parameter.Parameter, () => EmitLoadLocal(ParameterSlot(parameter.Parameter)));
                    return;

                case BoundThisExpression:
                    LoadReceiver();
                    return;

                case BoundConversionExpression conversion:
                    EmitConversion(conversion);
                    return;

                case BoundBinaryExpression binary:
                    EmitBinary(binary);
                    return;

                case BoundUnaryExpression unary:
                    EmitUnary(unary);
                    return;

                case BoundAssignmentExpression assignment:
                    EmitAssignment(assignment, keepValue: true);
                    return;

                case BoundConditionalExpression conditional:
                    EmitConditional(conditional);
                    return;

                case BoundCallExpression call:
                    EmitCall(call, discardResult: false);
                    return;

                case BoundClosureInvocationExpression invocation:
                    EmitClosureInvocation(invocation, discardResult: false);
                    return;

                case BoundObjectCreationExpression creation:
                    EmitObjectCreation(creation);
                    return;

                case BoundFieldExpression field:
                    EmitFieldRead(field);
                    return;

                case BoundPropertyExpression property:
                    EmitPropertyRead(property);
                    return;

                case BoundLambdaExpression lambda:
                    EmitLambda(lambda);
                    return;

                case BoundIndexExpression index:
                    EmitIndexRead(index);
                    return;

                case BoundArrayLiteralExpression array:
                    EmitArrayLiteral(array);
                    return;

                case BoundTupleLiteralExpression tuple:
                    EmitTupleLiteral(tuple);
                    return;

                case BoundDictLiteralExpression dictionary:
                    EmitDictLiteral(dictionary);
                    return;

                case BoundCollectionCreationExpression collection:
                    EmitCollectionCreation(collection);
                    return;

                case BoundInterpolatedStringExpression interpolated:
                    EmitInterpolatedString(interpolated);
                    return;

                case BoundTypeTestExpression test:
                    Expression(test.Operand);
                    Code.TestInstanceOf(Descriptors.Emit(test.TestedType.NonNullable));
                    return;

                case BoundTypeOfExpression typeOf:
                    EmitTypeOf(typeOf);
                    return;

                case BoundModuleOfExpression moduleOf:
                    EmitModuleOf(moduleOf);
                    return;

                case BoundSwitchExpression @switch:
                    EmitSwitchExpression(@switch);
                    return;

                case BoundThrowExpression @throw:
                    // `throw` as an expression lowers to exactly what the statement form does:
                    // evaluate the value, then Throw. The flow ends there, and the emitter's
                    // MarkLabel joins tolerate a branch that falls out into nothing.
                    Expression(@throw.Value);
                    Code.Throw();
                    return;

                case BoundNullConditionalExpression conditionalAccess:
                    EmitNullConditional(conditionalAccess, discardResult: false);
                    return;

                case BoundNullAssertExpression assertion:
                    EmitNullAssert(assertion);
                    return;

                case BoundConditionalReceiver:
                    EmitLoadLocal(
                        _conditionalReceivers.Count > 0
                            ? _conditionalReceivers[_conditionalReceivers.Count - 1]
                            : throw Unsupported("a '?.' receiver outside the access it belongs to"));

                    return;

                case BoundErrorExpression:
                    throw Unsupported("an expression that failed to bind, so the compilation should have stopped at its diagnostics");

                default:
                    throw Unsupported(expression.GetType().Name);
            }
        }

        private void EmitLiteral(BoundLiteralExpression literal)
        {
            switch (literal.Value)
            {
                // �5.1: absence in a nullable primitive is its own tagged value, not a null reference.
                // A reference is its 32-bit payload, so a null one and a present `0` would be the
                // same value � which is exactly what the absent tag exists to keep apart.
                case null when IsNullablePrimitive(literal.Type):
                    Code.PushAbsent(TypeCodeOf(literal.Type));
                    return;

                case null: Code.LoadNull(); return;
                case bool value: Code.LoadBool(value); return;
                case char value: Code.LoadChar(value); return;
                case string value: Code.LoadString(value); return;
                case double value: Code.LoadFloat(value); return;

                case long value:
                {
                    // A literal wider than the machine's int would silently truncate, and the binder
                    // has no narrowing rule that would have caught it.
                    if (value < int.MinValue || value > int.MaxValue)
                        throw Unsupported($"the integer literal {value}, which does not fit in an int");

                    Code.LoadInt((int)value);
                    return;
                }

                default:
                    throw Unsupported($"a literal of CLR type '{literal.Value.GetType().Name}'");
            }
        }

        /// <summary>
        /// <c>typeof(X)</c>. The static form needs no operand at all - <see cref="Code"/>.<c>LoadTypeOf</c>
        /// resolves entirely from the pool. The instance form's operand is either already a
        /// non-nullable primitive (the binder leaves it unconverted exactly when it is) or already
        /// converted to <c>unknown</c>, boxing where boxing is actually needed - a primitive here
        /// can only mean its class is statically known and can never differ at run time, so the
        /// value is evaluated for its side effects and discarded rather than read by
        /// <c>GetTypeOfValue</c>, which is what lets <c>typeof(5)</c> skip the box
        /// <c>Type.of(5)</c> always pays for through its <c>unknown</c> parameter.
        /// </summary>
        private void EmitTypeOf(BoundTypeOfExpression typeOf)
        {
            if (typeOf.TargetType is TypeSymbol staticTarget)
            {
                Code.LoadTypeOf(Descriptors.Emit(staticTarget.NonNullable));
                return;
            }

            var operand = typeOf.Operand!;
            Expression(operand);

            if (operand.Type.IsPrimitive)
            {
                Code.Pop();
                Code.LoadTypeOf(Descriptors.Emit(operand.Type.NonNullable));
                return;
            }

            Code.GetTypeOfValue();
        }

        /// <summary>
        /// <c>moduleof(ModulePath)</c>. Always static: the binder already resolved the path to a
        /// <see cref="ModuleSymbol"/>, so there is nothing left to evaluate -
        /// <see cref="SurtrCodeEmitter.LoadModuleOf(SurtrModule)"/> picks between the current
        /// module and the module table entirely from the target's own identity.
        /// </summary>
        private void EmitModuleOf(BoundModuleOfExpression moduleOf)
        {
            if (!_context.TryGetModule(moduleOf.Module.Path, out var target))
                throw Unsupported($"moduleof('{moduleOf.Module.Path}'), which is neither being emitted here nor already built");

            Code.LoadModuleOf(target);
        }

        private void EmitConversion(BoundConversionExpression conversion)
        {
            var from = conversion.Operand.Type;
            var to = conversion.Type;

            // �4.8 hands `as?` to the compiler: test, then either keep the value or produce null.
            if (conversion.IsSafe)
            {
                EmitSafeCast(conversion);
                return;
            }

            if (conversion.Conversion.Kind == ConversionKind.UserDefined)
            {
                // �5.6's `operator as` is an ordinary static call whose one argument is the value.
                Expression(conversion.Operand);
                EmitDirectCall(
                    conversion.Conversion.Method
                        ?? throw Unsupported("a user-defined conversion with no operator behind it"),
                    discardResult: false);

                return;
            }

            // `null` reaching a nullable primitive is the absent tag, not a null reference: �5.1 makes
            // absence a value of its own precisely so it costs no allocation, and a null reference is
            // its 32-bit payload � which would make an absent `int?` and a present `0` the same value.
            if (conversion.Operand is BoundLiteralExpression { Value: null } && IsNullablePrimitive(to))
            {
                Code.PushAbsent(TypeCodeOf(to));
                return;
            }

            Expression(conversion.Operand);
            EmitConversionTail(conversion.Conversion, from, to);
        }

        /// <summary>
        /// The part of a conversion that runs once the value it applies to is already on the stack.
        /// </summary>
        /// <remarks>
        /// Split out of <see cref="EmitConversion"/> so a collection cast constructor's per-element
        /// loop (<see cref="EmitCollectionCreation"/>) can apply the same conversion to a value it
        /// just read with <c>TupGetC</c>/<c>ArrGet</c> � there is no <see cref="BoundConversionExpression"/>
        /// node mid-loop for it to be the operand of, only the classified <see cref="Conversion"/>
        /// the binder already worked out. Every caller from that loop is restricted to
        /// <see cref="Conversion.IsImplicit"/> (�5.6 makes a user-defined <c>operator as</c>
        /// explicit-only), so only the identity/nullable/reference/numeric/erasure branches below are
        /// ever reached from there.
        /// </remarks>
        private void EmitConversionTail(Conversion conversion, TypeSymbol from, TypeSymbol to)
        {
            switch (conversion.Kind)
            {
                case ConversionKind.Identity:
                case ConversionKind.ImplicitNullable:
                    // Nothing to emit: a primitive widening into its own nullable form keeps the
                    // same representation, and a reference is its payload either way.
                    return;

                case ConversionKind.ImplicitReference:
                    // �6.3: a value class is erased to its field, so reaching a slot that holds a
                    // reference � an interface it implements � is where it becomes a real object.
                    // A tuple reaching one packs into its boxed form the same way. A primitive
                    // reaching a contract it satisfies is the same story: the interface dispatch
                    // goes through the receiver's vtable, which only an object has, so the raw
                    // int becomes its boxed form first.
                    if (!BoxIfMultiSlot(from))
                        Code.Box(TypeCodeOf(from));

                    return;

                case ConversionKind.ImplicitNumeric:
                case ConversionKind.ExplicitNumeric:
                    Code.Convert(TypeCodeOf(from), TypeCodeOf(to));
                    return;

                case ConversionKind.ImplicitErasure:
                    // �1.11's first obligation: a value reaching a slot that only holds a reference
                    // has to become one - packed, for an inline value; boxed, for everything else.
                    // Box emits nothing for a reference already.
                    if (!BoxIfMultiSlot(from))
                        Code.Box(TypeCodeOf(from));

                    return;

                case ConversionKind.ExplicitErasure:
                    Unerase(to);
                    return;

                case ConversionKind.ExplicitReference:
                {
                    // `T?` narrowing back to `T` names the same class, so there is nothing to check
                    // that the cast would not accept anyway.
                    if (ReferenceEquals(from.NonNullable, to.NonNullable))
                        return;

                    Code.CastTo(Descriptors.Emit(to.NonNullable));
                    return;
                }

                default:
                    throw Unsupported($"a {conversion.Kind} conversion");
            }
        }

        /// <summary>Reads a concrete type back out of an erased slot � �1.11's second obligation.</summary>
        private void Unerase(TypeSymbol target)
        {
            var bare = target.NonNullable;

            if (bare.SpecialType == SpecialType.Unknown || bare.TypeKind == TypeSymbolKind.TypeParameter)
                return;

            if (TryMultiSlotWidth(bare, out int unboxWidth))
            {
                if (bare is TupleTypeSymbol)
                {
                    // The value crossed the boundary as the boxed form the calling site packed;
                    // there is no class to cast to - a tuple's shape lives in its descriptor -
                    // so the unpack itself is the whole check.
                    Code.TupUnpack(unboxWidth);
                    return;
                }

                Code.CastTo(Descriptors.Emit(bare));
                Code.UnboxValue(unboxWidth);
                return;
            }

            if (bare.TypeKind == TypeSymbolKind.ValueClass)
            {
                Code.CastTo(Descriptors.EmitBoxedForm((NamedTypeSymbol)bare));
                Code.Unbox();
                return;
            }

            // A primitive read back out of an erased slot arrives in one of two representations,
            // and the reader cannot tell which. The compiler boxes a primitive on the way into an
            // erased slot (§1.11), so most of them are boxes - but a built-in's own storage never
            // was: an `int[]`'s elements are raw, and `IIterator<T>.current` over one hands the raw
            // value straight back through a slot declared `G0`. `UnboxDynamic` reads the value's
            // own tag and covers both, where `CastTo` + `Unbox` assumes a box and would misread the
            // raw case. It also allocates nothing, which is why the `BoxDynamic` that used to
            // normalise this away at each read site is gone: boxing a raw value only to unbox it
            // cost one allocation per element on every walk through a contract.
            if (bare.IsPrimitive && !bare.IsVoid)
            {
                Code.UnboxDynamic();
                return;
            }

            Code.CastTo(Descriptors.Emit(bare));
        }

        /// <summary>
        /// Boxes a tuple value as the <see cref="Surtr.Runtime.Objects.SurtrTuple"/> it presents as
        /// wherever a slot holds one reference, and says whether it did.
        /// </summary>
        /// <remarks>
        /// The boxed-form half of the tuple boundary (�5.5 as lowered): a block reaching an array
        /// element, a dictionary key, an erased parameter or any other one-reference slot packs
        /// into the ordinary tuple object, whose comparer and collector already work. The mirror
        /// read side is <see cref="UnpackIfTuple"/>.
        /// </remarks>
        private bool BoxIfTuple(TypeSymbol type)
        {
            if (type.NonNullable is not TupleTypeSymbol tuple || tuple.ElementTypes.Count == 0)
                return false;

            Code.PackTuple(Descriptors.Emit(tuple), SlotCountOfType(tuple));
            return true;
        }

        /// <summary>
        /// Boxes a <c>range</c> as the object it presents as, and says whether it did - the
        /// packed-form half of the range boundary, mirroring <see cref="BoxIfTuple"/>.
        /// </summary>
        /// <remarks>
        /// A range is three raw slots inline (�2.9); a one-reference slot needs the registered
        /// <c>SurtrRange</c> object, which is what every path that walks boxed values already
        /// walks and what its own native members read.
        /// </remarks>
        private bool BoxIfRange(TypeSymbol type)
        {
            if (type.NonNullable.SpecialType != SpecialType.Range)
                return false;

            Code.RangePack();
            return true;
        }

        /// <summary>
        /// Boxes whatever this type stores inline - a tuple, a multi-field value class or a
        /// range - before it crosses into a one-reference slot. Answers whether anything was emitted.
        /// </summary>
        private bool BoxIfMultiSlot(TypeSymbol type)
            => BoxIfValueClass(type) || BoxIfTuple(type) || BoxIfRange(type);

        /// <summary>The mirror of <see cref="BoxIfTuple"/>: turns a packed reference back into its block.</summary>
        private bool UnpackIfTuple(TypeSymbol type)
        {
            if (type.NonNullable is not TupleTypeSymbol tuple || tuple.ElementTypes.Count == 0)
                return false;

            Code.TupUnpack(SlotCountOfType(tuple));
            return true;
        }

        /// <summary>The mirror of <see cref="BoxIfRange"/>: a packed range becomes its block again.</summary>
        private bool UnpackIfRange(TypeSymbol type)
        {
            if (type.NonNullable.SpecialType != SpecialType.Range)
                return false;

            Code.RangeUnpack();
            return true;
        }

        /// <summary>
        /// Unboxes whatever this type stores inline - the mirror of <see cref="BoxIfMultiSlot"/>
        /// on the way back out of a one-reference slot.
        /// </summary>
        private bool UnpackIfMultiSlot(TypeSymbol type)
        {
            if (UnpackIfTuple(type) || UnpackIfRange(type))
                return true;

            if (type.NonNullable is NamedTypeSymbol { TypeKind: TypeSymbolKind.ValueClass } valueClass)
            {
                // A wrapper erased to an inline value unboxes as that value does.
                if (valueClass.UnderlyingType is TypeSymbol erased
                    && ValueTypeLayout.WidthOfType(erased.NonNullable) > 1)
                {
                    return UnpackIfMultiSlot(erased.NonNullable);
                }

                if (ValueTypeLayout.IsBlockValueClass(valueClass)
                    && ValueTypeLayout.TryGet(valueClass, out var layout, out _))
                {
                    Code.UnboxValue(layout.Width);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Boxes a <c>value class</c> as the class it presents as, and says whether it did.
        /// </summary>
        /// <remarks>
        /// The whole of the erased-value rule in one place. Where a value class's type is
        /// statically known it is the field it wraps and nothing happens; where the slot holds a
        /// reference the box has to name the real class, because the erased value is precisely the
        /// thing that no longer says what it was. A one-field wrapper erased to an inline value - a
        /// <c>Window</c> over a range, say - boxes exactly as the value it erases to, since its
        /// slots ARE that value's slots.
        /// </remarks>
        private bool BoxIfValueClass(TypeSymbol type)
        {
            if (type.NonNullable is not NamedTypeSymbol { TypeKind: TypeSymbolKind.ValueClass } valueClass)
                return false;

            if (valueClass.UnderlyingType is TypeSymbol erasedTo
                && ValueTypeLayout.WidthOfType(erasedTo.NonNullable) > 1)
            {
                return BoxIfRange(erasedTo.NonNullable)
                    || BoxIfTuple(erasedTo.NonNullable)
                    || BoxIfValueClass(erasedTo.NonNullable);
            }

            if (ValueTypeLayout.IsBlockValueClass(valueClass))
            {
                if (!ValueTypeLayout.TryGet(valueClass, out var boxLayout, out var boxError))
                    throw Unsupported(boxError!);

                Code.BoxValue(_context.Module.Type(Descriptors.Emit(valueClass)), boxLayout.Width);
                return true;
            }

            Code.BoxAs(_context.Module.Type(Descriptors.EmitBoxedForm(valueClass)));
            return true;
        }

        /// <summary>
        /// Boxes a call's receiver only where the callee might be reached through a vtable slot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// �6.3: a direct dispatch calls the exact method body regardless of the receiver's runtime
        /// type, so there is nothing for a box to be looked up on � the erased field reaches the
        /// callee unboxed. A method whose own <see cref="MethodSymbol.Dispatch"/> is not
        /// <see cref="MethodDispatch.Direct"/> may still be reached that way (a devirtualised call
        /// on a sealed value class already goes through <c>CallSpecial</c>, which does not consult
        /// the receiver's class either), so this boxes a little more than the strict minimum there �
        /// safe, per �6.3, where boxing less would not be. What matters is that this is the exact
        /// same test <see cref="LoadReceiver"/> makes to decide whether to unbox a value class
        /// receiver, so the two can never disagree about which convention a value class body was
        /// compiled against.
        /// </para>
        /// <para>
        /// A scalar primitive reaching one of its own <c>Virtual</c> members (�13.2's
        /// <c>compareTo</c>/<c>equals</c>, the only ones a built-in ever declares that way) needs
        /// the identical treatment for the identical reason: <c>InvokeVirtual</c>/<c>InvokeInterface</c>
        /// resolve the receiver's class through the entity registry, which only a boxed value is in.
        /// <see cref="BoxIfValueClass"/> only recognises a <c>value class</c>, so the fallback boxes
        /// whatever is left with <see cref="Code"/>'s ordinary <c>Box</c>, which is already a no-op
        /// for a receiver that is a reference already (a built-in class, or a generic parameter that
        /// was boxed on its way into its erased slot) � nothing here has to tell those cases apart
        /// from a primitive's own.
        /// </para>
        /// </remarks>
        private void BoxReceiverForCall(MethodSymbol method, TypeSymbol receiverType)
        {
            // A multi-field value class's box crosses the call as one reference slot while its
            // callee frame claims the whole width - the two conventions cannot both hold (�6.3).
            // Refused at the call site rather than emitted into a mis-sliced frame.
            if (method.Dispatch != MethodDispatch.Direct
                && receiverType.NonNullable is NamedTypeSymbol { TypeKind: TypeSymbolKind.ValueClass } boxed
                && ValueTypeLayout.WidthOfType(boxed) > 1)
            {
                throw Unsupported(
                    "a non-Direct call whose receiver is a multi-field value class: that receiver convention does not exist yet");
            }

            // A range receiver crosses as its three-slot block for a direct call - that is the
            // convention its own native members are built against - and packs only where the
            // call must resolve through a registry-resolved receiver.
            if (receiverType.NonNullable.SpecialType == SpecialType.Range)
            {
                if (method.Dispatch != MethodDispatch.Direct)
                    Code.RangePack();
                return;
            }

            if (method.Dispatch != MethodDispatch.Direct && !BoxIfValueClass(receiverType))
                Code.Box(TypeCodeOf(receiverType.NonNullable));
        }

        /// <summary>
        /// Emits <c>as?</c>: one <c>CastOrNull</c> to a reference type, and a tested unbox to a
        /// primitive.
        /// </summary>
        /// <remarks>
        /// <c>CastOrNull</c> exists because a reference target needs nothing else � the failure
        /// answer occupies the same slot as the subject, so the whole conversion is one type test.
        /// A primitive target still needs the branch: the success path unboxes and the failure path
        /// has no unboxed value to give, so the two arms differ by more than which value they
        /// carry.
        /// </remarks>
        private void EmitSafeCast(BoundConversionExpression conversion)
        {
            var target = conversion.Type.NonNullable;

            if (!target.IsPrimitive || target.IsVoid)
            {
                Expression(conversion.Operand);
                Code.CastToOrNull(Descriptors.Emit(target));
                return;
            }

            var value = _method.DeclareLocal("$candidate");
            var isInstance = Code.NewLabel();
            var end = Code.NewLabel();

            // JPInstanceOf jumps on true, so - unlike TestInstanceOf + JumpIfFalse, which always
            // materializes the bool - the "is an instance" arm is the jump target and the "is not"
            // arm is the fall-through, saving the intermediate boolean on the common (successful) path.
            Expression(conversion.Operand);
            EmitStoreLocal(value);
            EmitLoadLocal(value);
            Code.JumpIfInstanceOf(Descriptors.Emit(target), isInstance);

            // This branch is only reached for a primitive `target`, so the result type is always a
            // nullable primitive - its "no value" is the absent tag (�5.1), never a null reference.
            // A caller testing the result with `??`/`== null` reads the *static* type to pick
            // JPA/IsAbsent over JumpIfNull/IsNull (EmitNullCoalesce, TryEmitAbsenceTest/Branch), so
            // pushing a null reference here would go unrecognised as absence and read back its
            // all-zero payload as a present `0` instead.
            Code.PushAbsent(TypeCodeOf(target));
            Code.Jump(end);

            Code.MarkLabel(isInstance);
            EmitLoadLocal(value);
            Code.Unbox();

            Code.MarkLabel(end);
        }

        private void EmitBinary(BoundBinaryExpression binary)
        {
            if (binary.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual
                    or BinaryOperator.ReferenceEqual or BinaryOperator.ReferenceNotEqual
                && (SlotCountOfType(binary.Left.Type) > 1 || SlotCountOfType(binary.Right.Type) > 1))
            {
                EmitValueClassEquality(binary);
                return;
            }

            switch (binary.Operator)
            {
                case BinaryOperator.LogicalAnd:
                case BinaryOperator.LogicalOr:
                    EmitShortCircuit(binary);
                    return;

                case BinaryOperator.NullCoalesce:
                    EmitNullCoalesce(binary);
                    return;
            }

            // Absence is a *tag*, and only the tagged opcodes can see it. Left to the ordinary
            // comparison this becomes `PushAbsent` against EQ/NE, which are the integer opcodes and
            // compare the 32-bit payload alone � while PushAbsent carries the missing primitive's
            // type code in exactly that payload. An `int?` holding 1 then has the same payload as
            // absent-int (SurtrValueTypeCode.Integer == 1) and reads as null; a `char?` holding
            // '' would do the same. On the float side it fails the other way, since
            // absent-float is a NaN and FEQ answers false however it is asked.
            if (TryEmitAbsenceTest(binary))
                return;

            // `x == null`/`x != null`/`x === null`/`x !== null` against a reference (a class, an
            // array, a string, ...) needs neither the null literal on the stack nor a two-operand
            // comparison: a reference's nullness is its own tag (�5.1), which `IsNull`/`IsNotNull`
            // read off the one operand directly. Left to the general path below this would push
            // `PushNull` and run `REQ`/`RNE` - or, for a string, `StrEQ`/`StrNE`'s text comparison,
            // which happens to be null-safe (�Opcodes) but still does more work than asking the tag.
            if (TryGetNullCheckOperand(binary, out var nullCheckOperand, out bool checksForNull))
            {
                Expression(nullCheckOperand);

                if (checksForNull)
                    Code.IsNull();
                else
                    Code.IsNotNull();

                return;
            }

            var operands = TypeCodeOf(binary.Left.Type);

            // �4.8 hands ordering on strings to the compiler: there is no opcode that orders one,
            // and `compareTo` is what the language says the operators mean.
            if (operands == SurtrValueTypeCode.String && IsOrdering(binary.Operator))
            {
                EmitStringOrdering(binary);
                return;
            }

            if (binary.Operator == BinaryOperator.Compare)
            {
                EmitThreeWayCompare(binary);
                return;
            }

            // `a + b + c` is one concatenation, not two: joined pairwise it allocates an
            // intermediate nothing reads, and copies `a` twice. The whole spine goes in one go.
            if (binary.Operator == BinaryOperator.Add && operands == SurtrValueTypeCode.String)
            {
                EmitStringConcat(binary);
                return;
            }

            Expression(binary.Left);
            Expression(binary.Right);

            switch (binary.Operator)
            {
                case BinaryOperator.Add:
                    Code.Add(operands);
                    return;

                case BinaryOperator.Subtract: Code.Subtract(operands); return;
                case BinaryOperator.Multiply: Code.Multiply(operands); return;
                case BinaryOperator.Divide: Code.Divide(operands); return;
                case BinaryOperator.Modulo: Code.Remainder(operands); return;

                case BinaryOperator.BitAnd: Code.And(); return;
                case BinaryOperator.BitOr: Code.Or(); return;
                case BinaryOperator.BitXor: Code.Xor(); return;
                case BinaryOperator.ShiftLeft: Code.Shl(); return;

                // `>>` keeps the sign and `>>>` fills with zeroes, which is Sar and Shr in that
                // order � the opcodes are named after the machine operation, not after the token.
                case BinaryOperator.ShiftRight: Code.Sar(); return;
                case BinaryOperator.UnsignedShiftRight: Code.Shr(); return;

                case BinaryOperator.Equal: Code.Compare(SurtrComparison.Equal, operands); return;
                case BinaryOperator.NotEqual: Code.Compare(SurtrComparison.NotEqual, operands); return;
                case BinaryOperator.Less: Code.Compare(SurtrComparison.Less, operands); return;
                case BinaryOperator.LessEqual: Code.Compare(SurtrComparison.LessOrEqual, operands); return;
                case BinaryOperator.Greater: Code.Compare(SurtrComparison.Greater, operands); return;
                case BinaryOperator.GreaterEqual: Code.Compare(SurtrComparison.GreaterOrEqual, operands); return;

                case BinaryOperator.ReferenceEqual:
                    Code.Compare(SurtrComparison.Equal, SurtrValueTypeCode.Object);
                    return;

                case BinaryOperator.ReferenceNotEqual:
                    Code.Compare(SurtrComparison.NotEqual, SurtrValueTypeCode.Object);
                    return;

                case BinaryOperator.Range: Code.RangeNew(); return;
                case BinaryOperator.RangeInclusive: Code.RangeNewInclusive(); return;

                default:
                    throw Unsupported($"the operator '{binary.Operator}'");
            }
        }

        private static bool IsOrdering(BinaryOperator @operator) => @operator
            is BinaryOperator.Less or BinaryOperator.LessEqual
            or BinaryOperator.Greater or BinaryOperator.GreaterEqual
            or BinaryOperator.Compare;

        /// <summary>Lowers <c>a &lt; b</c> and its siblings on strings to <c>compareTo</c> against zero.</summary>
        private void EmitStringOrdering(BoundBinaryExpression binary)
        {
            Expression(binary.Left);
            Expression(binary.Right);
            Code.Call(StringCompareTo());

            if (binary.Operator == BinaryOperator.Compare)
                return;

            Code.LoadInt(0);
            Code.Compare(
                binary.Operator switch
                {
                    BinaryOperator.Less => SurtrComparison.Less,
                    BinaryOperator.LessEqual => SurtrComparison.LessOrEqual,
                    BinaryOperator.Greater => SurtrComparison.Greater,
                    _ => SurtrComparison.GreaterOrEqual,
                },
                SurtrValueTypeCode.Integer);
        }

        /// <summary>How many operands one <c>StrCat</c> can take: its count immediate is a byte.</summary>
        private const int MaxConcatOperands = 255;

        /// <summary>Emits a whole <c>+</c> spine over strings as one counted concatenation.</summary>
        /// <remarks>
        /// The spine is walked in order, so operands are evaluated left to right exactly as the
        /// pairwise emission did � flattening changes what is allocated, not when anything runs.
        /// A part that is not a string is converted through its <c>toString</c> first, the same
        /// conversion an interpolation applies: �5.7 lets anything be appended to a string, and
        /// <c>StrCat</c> takes strings only.
        /// </remarks>
        private void EmitStringConcat(BoundBinaryExpression binary)
        {
            var parts = new List<BoundExpression>();
            FlattenStringConcat(binary, parts);

            int pending = 0;

            foreach (var part in parts)
            {
                EmitAsString(part);

                if (++pending == MaxConcatOperands)
                {
                    Code.StrCat(MaxConcatOperands);
                    pending = 1;
                }
            }

            if (pending > 1)
                Code.StrCat(pending);
        }

        private void FlattenStringConcat(BoundExpression expression, List<BoundExpression> parts)
        {
            // Only a string-typed `+` is a concatenation; anything else � a user-defined operator,
            // an interpolation, a call returning a string � is one operand of this one.
            if (expression is BoundBinaryExpression { Operator: BinaryOperator.Add } nested &&
                TypeCodeOf(nested.Type) == SurtrValueTypeCode.String)
            {
                FlattenStringConcat(nested.Left, parts);
                FlattenStringConcat(nested.Right, parts);
                return;
            }

            parts.Add(expression);
        }

        private SurtrMethodInfo StringCompareTo()
            => SurtrBuiltIns.String.TryGetMethods("compareTo", out var overloads) && overloads.Length == 1
                ? overloads[0]
                : throw Unsupported("an ordering on strings, because 'string.compareTo' could not be found");

        /// <summary>Emits <c>&amp;&amp;</c> and <c>||</c>, which evaluate their right side only if they have to.</summary>
        private void EmitShortCircuit(BoundBinaryExpression binary)
        {
            bool isAnd = binary.Operator == BinaryOperator.LogicalAnd;
            var shortCircuit = Code.NewLabel();
            var end = Code.NewLabel();

            Expression(binary.Left);
            Code.Dup();

            if (isAnd)
                Code.JumpIfFalse(shortCircuit);
            else
                Code.JumpIfTrue(shortCircuit);

            // The duplicate only survives on the short-circuiting path; the other path throws it
            // away and the right operand becomes the result.
            Code.Pop();
            Expression(binary.Right);
            Code.Jump(end);
            Code.MarkLabel(shortCircuit);
            Code.MarkLabel(end);
        }

        /// <summary>
        /// Emits a <c>?.</c> access: the receiver once, then the access only if it was not null (�5.1).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The receiver goes into a slot rather than being duplicated on the stack, for two reasons.
        /// It has to be readable at whatever depth the access reaches it � an argument list may sit
        /// between them � and both paths have to leave the stack at the same depth, which the emitter
        /// checks at the join. A <c>Dup</c>/<c>Pop</c> pair would have to be balanced by hand at every
        /// shape of access instead.
        /// </para>
        /// <para>
        /// What the null path pushes depends on the member: a nullable primitive is the absent tag,
        /// not a null reference, since those are deliberately different values in the encoding.
        /// </para>
        /// </remarks>
        private void EmitNullConditional(BoundNullConditionalExpression access, bool discardResult)
        {
            var slot = _method.DeclareLocal("$safe$" + _conditionalReceivers.Count);

            Expression(access.Receiver);
            EmitStoreLocal(slot);

            var whenNull = Code.NewLabel();
            var end = Code.NewLabel();

            EmitLoadLocal(slot);

            if (IsNullablePrimitive(access.Receiver.Type))
                Code.JPA(whenNull);
            else
                Code.JumpIfNull(whenNull);

            _conditionalReceivers.Add(slot);

            bool hasValue = !access.Type.IsVoid && !discardResult;

            if (access.Access is BoundCallExpression call)
                EmitCall(call, discardResult: !hasValue);
            else if (hasValue)
                Expression(access.Access);
            else
                Statement(new BoundExpressionStatement(access.Syntax, access.Access));

            _conditionalReceivers.RemoveAt(_conditionalReceivers.Count - 1);

            Code.Jump(end);
            Code.MarkLabel(whenNull);

            if (hasValue)
                PushAbsentValue(access.Type);

            Code.MarkLabel(end);
        }

        /// <summary>
        /// Emits <c>x!!</c>: the value, and a raise on the path where it turned out to be null (�5.1).
        /// </summary>
        /// <remarks>
        /// The operand stays on the stack and a duplicate is what the test consumes, so the value
        /// passes through untouched on the path that matters. The raising path ends in <c>Throw</c>,
        /// which is why the join needs no balancing: nothing falls out of it.
        /// </remarks>
        private void EmitNullAssert(BoundNullAssertExpression assertion)
        {
            Expression(assertion.Operand);

            if (assertion.Thrown is null)
                return;

            var ok = Code.NewLabel();

            Code.Dup();

            if (IsNullablePrimitive(assertion.Operand.Type))
                Code.JPNA(ok);
            else
                Code.JumpIfNotNull(ok);

            Code.Pop();
            Expression(assertion.Thrown);
            Code.Throw();
            Code.MarkLabel(ok);
        }

        /// <summary>Pushes the "no value" of a type: the absent tag for a primitive, null otherwise.</summary>
        private void PushAbsentValue(TypeSymbol type)
        {
            if (IsNullablePrimitive(type))
                Code.PushAbsent(TypeCodeOf(type));
            else
                Code.LoadNull();
        }

        /// <summary>
        /// Whether a type's "no value" is the absent tag rather than a null reference (�5.1).
        /// </summary>
        private static bool IsNullablePrimitive(TypeSymbol type)
            => type.IsNullable && type.NonNullable.SpecialType is SpecialType.Int or SpecialType.Float
                or SpecialType.Bool or SpecialType.Char;

        /// <summary>
        /// Emits <c>x == null</c> / <c>x != null</c> on a nullable primitive as a tag test, and
        /// reports whether it did.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the only correct way to ask the question. The comparison opcodes are chosen by
        /// operand family, so a nullable primitive picks the primitive ones � <c>EQ</c>/<c>NE</c>
        /// for the integer family, which compare the low 32 bits because int, bool and char share
        /// a representation and differ only in their tag. Absence differs from a present value in
        /// nothing <em>but</em> its tag, so that comparison cannot see it, and the payload it does
        /// see is the type code <c>PushAbsent</c> put there: an <c>int?</c> holding
        /// <c>SurtrValueTypeCode.Integer</c> � that is, 1 � compares equal to null.
        /// </para>
        /// <para>
        /// <c>IsAbsent</c> and <c>IsPresent</c> test the tag, which is the whole reason they are in
        /// the instruction set. The value is pushed once and answered in one instruction, so this
        /// is also a byte shorter than the pair it replaces.
        /// </para>
        /// </remarks>
        private bool TryEmitAbsenceTest(BoundBinaryExpression binary)
        {
            if (binary.Operator is not (BinaryOperator.Equal or BinaryOperator.NotEqual))
                return false;

            // Either side may be the literal � `null == x` is as legal as `x == null`.
            BoundExpression? value = null;
            if (IsNullLiteral(binary.Right) && IsNullablePrimitive(binary.Left.Type))
                value = binary.Left;
            else if (IsNullLiteral(binary.Left) && IsNullablePrimitive(binary.Right.Type))
                value = binary.Right;

            if (value is null)
                return false;

            Expression(value);

            if (binary.Operator == BinaryOperator.Equal)
                Code.IsAbsent();
            else
                Code.IsPresent();

            return true;
        }

        /// <summary>
        /// The branch-fusing twin of <see cref="TryEmitAbsenceTest"/>: <c>x == null</c>/<c>x != null</c>
        /// against a nullable primitive, used as a branch condition, fuses straight into
        /// <c>JPA</c>/<c>JPNA</c> instead of pushing the absence test's boolean and testing it with a
        /// separate <c>JumpIfFalse</c>.
        /// </summary>
        private bool TryEmitAbsenceBranch(BoundBinaryExpression binary, SurtrLabel target)
        {
            if (binary.Operator is not (BinaryOperator.Equal or BinaryOperator.NotEqual))
                return false;

            BoundExpression? value = null;
            if (IsNullLiteral(binary.Right) && IsNullablePrimitive(binary.Left.Type))
                value = binary.Left;
            else if (IsNullLiteral(binary.Left) && IsNullablePrimitive(binary.Right.Type))
                value = binary.Right;

            if (value is null)
                return false;

            Expression(value);

            // JPA/JPNA say "jump when true"; a false condition is what sends control to `target`
            // here, so `== null` (true means absent) takes the present branch (JPNA) and `!= null`
            // takes the absent one (JPA) - the mirror of TryEmitAbsenceTest's IsAbsent/IsPresent
            // choice above.
            if (binary.Operator == BinaryOperator.Equal)
                Code.JPNA(target);
            else
                Code.JPA(target);

            return true;
        }

        /// <summary>
        /// Whether an expression is the <c>null</c> literal, looking through the conversion the
        /// binder wraps it in to give it the other operand's type.
        /// </summary>
        private static bool IsNullLiteral(BoundExpression expression) => expression switch
        {
            BoundLiteralExpression { Value: null } => true,
            BoundConversionExpression conversion => IsNullLiteral(conversion.Operand),
            _ => false,
        };

        /// <summary>
        /// Recognises <c>x == null</c>/<c>x != null</c>/<c>x === null</c>/<c>x !== null</c> (either
        /// operand order) against a reference-typed <paramref name="binary"/>, and reports which
        /// operand to test and which sense to test it in - the shared detection
        /// <see cref="EmitBinary"/>'s value-producing <c>IsNull</c>/<c>IsNotNull</c> lowering and
        /// <see cref="EmitConditionalJump"/>'s <c>JPN</c>/<c>JPNN</c> branch fusion both need.
        /// </summary>
        /// <remarks>
        /// A nullable primitive is deliberately excluded: its "no value" is the absent tag, not a
        /// null reference (�5.1), so it takes <see cref="TryEmitAbsenceTest"/>/
        /// <see cref="TryEmitAbsenceBranch"/> instead - the two are never both applicable to the
        /// same comparison, since one requires the non-literal operand to be nullable-primitive and
        /// the other requires it not to be.
        /// </remarks>
        private static bool TryGetNullCheckOperand(BoundBinaryExpression binary, out BoundExpression operand, out bool checksForNull)
        {
            operand = null!;
            checksForNull = false;

            bool isEquality = binary.Operator is BinaryOperator.Equal or BinaryOperator.ReferenceEqual;
            bool isInequality = binary.Operator is BinaryOperator.NotEqual or BinaryOperator.ReferenceNotEqual;

            if (!isEquality && !isInequality)
                return false;

            if (IsNullLiteral(binary.Right) && !IsNullablePrimitive(binary.Left.Type))
                operand = binary.Left;
            else if (IsNullLiteral(binary.Left) && !IsNullablePrimitive(binary.Right.Type))
                operand = binary.Right;
            else
                return false;

            checksForNull = isEquality;
            return true;
        }

        // A stack, because `a?.b?.c` nests one guarded access inside another and each has its own
        // receiver slot; the innermost is the one a placeholder reads.
        private readonly List<SurtrLocal> _conditionalReceivers = new List<SurtrLocal>();

        private void EmitNullCoalesce(BoundBinaryExpression binary)
        {
            var otherwise = Code.NewLabel();
            var end = Code.NewLabel();

            Expression(binary.Left);
            Code.Dup();

            // A nullable primitive says "no value" with the absent tag rather than with a null
            // payload, so asking the wrong question would let an absent `int?` through as a value.
            if (IsNullablePrimitive(binary.Left.Type))
                Code.JPA(otherwise);
            else
                Code.JumpIfNull(otherwise);

            Code.Jump(end);
            Code.MarkLabel(otherwise);
            Code.Pop();
            Expression(binary.Right);
            Code.MarkLabel(end);
        }

        /// <summary>
        /// Emits <c>&lt;=&gt;</c> over a primitive family, which has no opcode of its own.
        /// </summary>
        /// <remarks>
        /// �5.7 makes it yield an <c>int</c> whose sign is the answer, so two comparisons and a
        /// subtraction give it exactly.
        /// </remarks>
        private void EmitThreeWayCompare(BoundBinaryExpression binary)
        {
            var operands = TypeCodeOf(binary.Left.Type);

            var left = DeclareTemp("$left", binary.Left.Type);
            var right = DeclareTemp("$right", binary.Right.Type);

            Expression(binary.Left);
            EmitStoreLocal(left);
            Expression(binary.Right);
            EmitStoreLocal(right);

            EmitLoadLocal(left);
            EmitLoadLocal(right);
            Code.Compare(SurtrComparison.Greater, operands);
            Code.Convert(SurtrValueTypeCode.Boolean, SurtrValueTypeCode.Integer);

            EmitLoadLocal(left);
            EmitLoadLocal(right);
            Code.Compare(SurtrComparison.Less, operands);
            Code.Convert(SurtrValueTypeCode.Boolean, SurtrValueTypeCode.Integer);

            Code.Subtract(SurtrValueTypeCode.Integer);
        }

        /// <summary>
        /// Emits <c>==</c>/<c>!=</c> over a multi-field value class: field-wise comparison in
        /// declaration order, short-circuiting on the first slot that already decides.
        /// </summary>
        /// <remarks>
        /// A value has no identity, so <c>===</c> over one is refused outright - there is nothing
        /// for a reference comparison to compare. Each slot compares with its own family's opcode,
        /// exactly as the fused branches choose per operand type; the failing path re-computes the
        /// flipped answer rather than spilling the chain's partial results.
        /// </remarks>
        private void EmitValueClassEquality(BoundBinaryExpression binary)
        {
            if (binary.Operator is BinaryOperator.ReferenceEqual or BinaryOperator.ReferenceNotEqual)
                throw Unsupported("'===' over a value: a value has no identity to compare (use '==')");

            var valueType = SlotCountOfType(binary.Left.Type) > 1
                ? binary.Left.Type.NonNullable
                : binary.Right.Type.NonNullable;

            // A one-field wrapper erased to an inline value compares as the value it erases to -
            // its slots are that value's, and its field list has nothing of its own to walk.
            while (valueType is NamedTypeSymbol { TypeKind: TypeSymbolKind.ValueClass } wrapper
                && wrapper.UnderlyingType is TypeSymbol erased
                && ValueTypeLayout.WidthOfType(valueType) > 1)
            {
                valueType = erased.NonNullable;
            }

            // A range compares structurally before any class-shaped walk can look for instance
            // fields it does not have.
            if (valueType.SpecialType == SpecialType.Range)
            {
                EmitRangeEquality(binary);
                return;
            }

            if (valueType is not NamedTypeSymbol named)
            {
                if (valueType is TupleTypeSymbol)
                {
                    EmitTupleEquality(binary, (TupleTypeSymbol)valueType);
                    return;
                }

                throw Unsupported($"comparing values of '{binary.Left.Type.ToDisplayString()}'");
            }

            if (!ValueTypeLayout.TryGet(named, out var layout, out var layoutError))
                throw Unsupported(layoutError!);

            int leftBase = EnsureLocalRange(binary.Left, layout.Width);
            int rightBase = EnsureLocalRange(binary.Right, layout.Width);

            // The chain stores its verdict in a bool temp: every path reaches the join at depth
            // zero over it, which is what the emitter's label-join check requires.
            var verdict = _method.DeclareLocal("$eq");

            var unequal = Code.NewLabel();
            var done = Code.NewLabel();

            for (int i = 0; i < layout.Fields.Length; i++)
            {
                int offset = layout.Offsets[i];
                int width = layout.FieldWidths[i];
                bool last = i == layout.Fields.Length - 1;

                if (width > 1)
                {
                    Code.LoadValueLocal(leftBase + offset, width);
                    Code.LoadValueLocal(rightBase + offset, width);
                }
                else
                {
                    Code.LoadLocalField(leftBase, offset);
                    Code.LoadLocalField(rightBase, offset);
                }

                Code.Compare(SurtrComparison.Equal, TypeCodeOf(layout.Fields[i].Type));

                if (!last)
                {
                    Code.JumpIfFalse(unequal);
                }
                else
                {
                    EmitStoreLocal(verdict);
                    Code.Jump(done);
                }
            }

            Code.MarkLabel(unequal);
            Code.LoadInt(0);
            Code.Convert(SurtrValueTypeCode.Integer, SurtrValueTypeCode.Boolean);
            EmitStoreLocal(verdict);

            Code.MarkLabel(done);
            EmitLoadLocal(verdict);

            if (binary.Operator is BinaryOperator.NotEqual)
                Code.Inv();
        }

        /// <summary>
        /// Structural equality between two tuples: slot by slot against each element's own family,
        /// recursing through nested tuples and value classes exactly as the flattened layout lays
        /// them out.
        /// </summary>
        /// <remarks>
        /// Same verdict-temp shape the value-class walk uses, so every path reaches the join at
        /// depth zero over one bool. An element that is itself inline compares recursively before
        /// the chain may short-circuit - the nesting is flat storage, so the recursion is an
        /// inlined walk over consecutive slots, never a call.
        /// </remarks>
        private void EmitTupleEquality(BoundBinaryExpression binary, TupleTypeSymbol tuple)
        {
            int width = ValueTypeLayout.WidthOfType(tuple);
            int leftBase = EnsureLocalRange(binary.Left, width);
            int rightBase = EnsureLocalRange(binary.Right, width);

            var verdict = _method.DeclareLocal("$eq");

            var unequal = Code.NewLabel();
            var done = Code.NewLabel();

            int offset = 0;
            for (int i = 0; i < tuple.ElementTypes.Count; i++)
            {
                var elementType = tuple.ElementTypes[i];
                bool last = i == tuple.ElementTypes.Count - 1;

                // Leaves exactly one bool on the stack, whether it compared a single slot or
                // walked a nested block's own elements.
                EmitSlotCompare(leftBase + offset, rightBase + offset, elementType, unequal);

                if (!last)
                {
                    Code.JumpIfFalse(unequal);
                }
                else
                {
                    EmitStoreLocal(verdict);
                    Code.Jump(done);
                }

                offset += ValueTypeLayout.WidthOfType(elementType);
            }

            Code.MarkLabel(unequal);
            Code.LoadInt(0);
            Code.Convert(SurtrValueTypeCode.Integer, SurtrValueTypeCode.Boolean);
            EmitStoreLocal(verdict);

            Code.MarkLabel(done);
            EmitLoadLocal(verdict);

            if (binary.Operator is BinaryOperator.NotEqual)
                Code.Inv();
        }

        /// <summary>
        /// Emits one slot-or-block comparison, leaving its bool on the stack. Answers whether the
        /// comparison was a nested structural walk (which manages its own branches internally and
        /// still leaves exactly one bool).
        /// </summary>
        private bool EmitSlotCompare(int leftSlot, int rightSlot, TypeSymbol elementType, SurtrLabel unequal)
        {
            if (TryMultiSlotWidth(elementType, out int elementWidth))
            {
                if (elementType.NonNullable is TupleTypeSymbol nested)
                {
                    int offset = 0;
                    for (int i = 0; i < nested.ElementTypes.Count; i++)
                    {
                        var inner = nested.ElementTypes[i];
                        EmitSlotCompare(leftSlot + offset, rightSlot + offset, inner, unequal);
                        Code.JumpIfFalse(unequal);
                        offset += ValueTypeLayout.WidthOfType(inner);
                    }

                    Code.LoadInt(1);
                    Code.Convert(SurtrValueTypeCode.Integer, SurtrValueTypeCode.Boolean);
                    return true;
                }

                // A nested multi-field value class compares with the same per-field rule its own
                // == would use: one Compare per field, all of them against their families.
                if (elementType.NonNullable is NamedTypeSymbol valueClass && ValueTypeLayout.TryGet(valueClass, out var layout, out _))
                {
                    for (int i = 0; i < layout.Fields.Length; i++)
                    {
                        Code.LoadValueLocal(leftSlot + layout.Offsets[i], layout.FieldWidths[i]);
                        Code.LoadValueLocal(rightSlot + layout.Offsets[i], layout.FieldWidths[i]);
                        Code.Compare(SurtrComparison.Equal, TypeCodeOf(layout.Fields[i].Type));
                        Code.JumpIfFalse(unequal);
                    }

                    Code.LoadInt(1);
                    Code.Convert(SurtrValueTypeCode.Integer, SurtrValueTypeCode.Boolean);
                    return true;
                }
            }

            Code.LoadLocalField(leftSlot, 0);
            Code.LoadLocalField(rightSlot, 0);
            Code.Compare(SurtrComparison.Equal, TypeCodeOf(elementType));
            return false;
        }

        /// <summary>
        /// Structural equality between two ranges: bounds and flag, slot by slot - the same walk
        /// the value-class comparison runs, over the three slots a range is.
        /// </summary>
        /// <remarks>
        /// Two equal ranges compare equal whatever identity their packs would carry, which is the
        /// point of the range being a value: <c>(0..3) == (0..3)</c> answers true, and
        /// <c>0..3</c> against <c>0..=3</c> answers false on the flag alone.
        /// </remarks>
        private void EmitRangeEquality(BoundBinaryExpression binary)
        {
            int leftBase = EnsureLocalRange(binary.Left, RangeSlotWidth);
            int rightBase = EnsureLocalRange(binary.Right, RangeSlotWidth);

            var verdict = _method.DeclareLocal("$eq");

            var unequal = Code.NewLabel();
            var done = Code.NewLabel();

            Code.LoadLocalField(leftBase, 0);
            Code.LoadLocalField(rightBase, 0);
            Code.Compare(SurtrComparison.Equal, SurtrValueTypeCode.Integer);
            Code.JumpIfFalse(unequal);

            Code.LoadLocalField(leftBase, 1);
            Code.LoadLocalField(rightBase, 1);
            Code.Compare(SurtrComparison.Equal, SurtrValueTypeCode.Integer);
            Code.JumpIfFalse(unequal);

            Code.LoadLocalField(leftBase, 2);
            Code.LoadLocalField(rightBase, 2);
            Code.Compare(SurtrComparison.Equal, SurtrValueTypeCode.Boolean);

            EmitStoreLocal(verdict);
            Code.Jump(done);

            Code.MarkLabel(unequal);
            Code.LoadInt(0);
            Code.Convert(SurtrValueTypeCode.Integer, SurtrValueTypeCode.Boolean);
            EmitStoreLocal(verdict);

            Code.MarkLabel(done);
            EmitLoadLocal(verdict);

            if (binary.Operator is BinaryOperator.NotEqual)
                Code.Inv();
        }

        /// <summary>How many slots one inline range occupies: start, end, inclusive.</summary>
        private const int RangeSlotWidth = 3;

        private void EmitUnary(BoundUnaryExpression unary)
        {
            switch (unary.Operator)
            {
                case UnaryOperator.Negate:
                    Expression(unary.Operand);
                    Code.Negate(TypeCodeOf(unary.Operand.Type));
                    return;

                case UnaryOperator.Not:
                    Expression(unary.Operand);
                    Code.Inv();
                    return;

                case UnaryOperator.Complement:
                    Expression(unary.Operand);
                    Code.Not();
                    return;

                case UnaryOperator.NullAssert:
                    // �5.1 asserts rather than converts, and the assertion is what a cast to the
                    // same class already performs.
                    Expression(unary.Operand);
                    return;

                case UnaryOperator.PreIncrement:
                case UnaryOperator.PreDecrement:
                case UnaryOperator.PostIncrement:
                case UnaryOperator.PostDecrement:
                    EmitIncrement(unary);
                    return;

                default:
                    throw Unsupported($"the unary operator '{unary.Operator}'");
            }
        }

        /// <summary>
        /// Emits <c>++</c> and <c>--</c>, which read, combine and write back.
        /// </summary>
        /// <remarks>
        /// Not expanded in the bound tree the way a compound assignment is, because the two forms
        /// differ in which value they leave behind � a distinction that only exists at emit.
        /// </remarks>
        private void EmitIncrement(BoundUnaryExpression unary)
        {
            bool isPost = unary.Operator is UnaryOperator.PostIncrement or UnaryOperator.PostDecrement;
            bool isIncrement = unary.Operator is UnaryOperator.PreIncrement or UnaryOperator.PostIncrement;

            var family = TypeCodeOf(unary.Operand.Type);
            var before = DeclareTemp("$before", unary.Operand.Type);

            Expression(unary.Operand);
            EmitStoreLocal(before);

            EmitLoadLocal(before);

            if (family == SurtrValueTypeCode.Float)
                Code.LoadFloat(1.0);
            else
                Code.LoadInt(1);

            if (isIncrement)
                Code.Add(family);
            else
                Code.Subtract(family);

            var after = DeclareTemp("$after", unary.Operand.Type);
            EmitStoreLocal(after);

            Store(unary.Operand, () => EmitLoadLocal(after));
            EmitLoadLocal(isPost ? before : after);
        }

        /// <summary>
        /// Emits an assignment, leaving its value behind only where something wants it.
        /// </summary>
        /// <remarks>
        /// A compound assignment arrived expanded, so there is one form to emit rather than
        /// thirteen. <paramref name="keepValue"/> is what separates <c>a = b = 0</c> from a bare
        /// <c>x = 1;</c>, and keeping them apart avoids a store-and-reload on the common one.
        /// </remarks>
        private void EmitAssignment(BoundAssignmentExpression assignment, bool keepValue)
        {
            if (!keepValue)
            {
                if (TryEmitInPlaceIncrement(assignment))
                    return;

                Store(assignment.Target, () => Expression(assignment.Value));
                return;
            }

            var value = DeclareTemp("$assigned", assignment.Value.Type);
            Expression(assignment.Value);
            EmitStoreLocal(value);

            Store(assignment.Target, () => EmitLoadLocal(value));
            EmitLoadLocal(value);
        }

        /// <summary>
        /// Recognises <c>i = i + k</c> over an integer local and emits it as one instruction.
        /// </summary>
        /// <remarks>
        /// �5.7's compound assignments arrive expanded � <c>i += 1</c> is already <c>i = i + 1</c>
        /// in the bound tree � so this one shape covers both spellings, and covers the step of
        /// every hand-written counted loop with it. Only statement position qualifies: an
        /// assignment whose value something reads has to leave that value behind, and
        /// <c>IncLocal</c> leaves nothing.
        /// </remarks>
        private bool TryEmitInPlaceIncrement(BoundAssignmentExpression assignment)
        {
            if (assignment.Value is not BoundBinaryExpression binary)
                return false;

            if (binary.Operator is not (BinaryOperator.Add or BinaryOperator.Subtract))
                return false;

            if (TypeCodeOf(assignment.Target.Type) != SurtrValueTypeCode.Integer)
                return false;

            if (binary.Right is not BoundLiteralExpression { Value: long written })
                return false;

            if (!TryLocalSlot(assignment.Target, out int slot) ||
                !TryLocalSlot(binary.Left, out int operand) ||
                slot != operand)
            {
                return false;
            }

            long delta = binary.Operator == BinaryOperator.Subtract ? -written : written;

            if (delta < int.MinValue || delta > int.MaxValue)
                return false;

            // Out-of-range slots and deltas fall back to load-add-store inside the emitter, which
            // is what this path would otherwise have emitted anyway.
            Code.IncrementLocal(slot, (int)delta);
            return true;
        }

        private static bool IsIncrementOrDecrement(UnaryOperator @operator) => @operator
            is UnaryOperator.PreIncrement or UnaryOperator.PostIncrement
            or UnaryOperator.PreDecrement or UnaryOperator.PostDecrement;

        /// <summary>Emits a discarded <c>++</c> or <c>--</c> over an integer local in place.</summary>
        private bool TryEmitInPlaceStep(BoundUnaryExpression unary)
        {
            if (TypeCodeOf(unary.Operand.Type) != SurtrValueTypeCode.Integer)
                return false;

            if (!TryLocalSlot(unary.Operand, out int slot))
                return false;

            bool up = unary.Operator is UnaryOperator.PreIncrement or UnaryOperator.PostIncrement;

            Code.IncrementLocal(slot, up ? 1 : -1);
            return true;
        }

        /// <summary>The frame slot an expression names, when it names one directly.</summary>
        private bool TryLocalSlot(BoundExpression expression, out int slot)
        {
            switch (expression)
            {
                case BoundLocalExpression local:
                    slot = Slot(local.Local).Index;
                    return true;

                case BoundParameterExpression parameter:
                    slot = ParameterSlot(parameter.Parameter).Index;
                    return true;

                default:
                    slot = -1;
                    return false;
            }
        }

        /// <summary>
        /// Writes to whatever an assignment names, with <paramref name="value"/> pushing the value
        /// at the point the target's own instruction expects it.
        /// </summary>
        /// <remarks>
        /// A callback rather than a value already on the stack, because the receiver and the index
        /// have to be evaluated <em>before</em> it for a field or an indexed write, and after it for
        /// nothing � so the one order that works is the one each target dictates.
        /// </remarks>
        private void Store(BoundExpression target, Action value)
        {
            switch (target)
            {
                case BoundLocalExpression local:
                    value();
                    EmitStoreLocal(Slot(local.Local));
                    return;

                case BoundParameterExpression parameter:
                    value();
                    EmitStoreLocal(ParameterSlot(parameter.Parameter));
                    return;

                case BoundFieldExpression field:
                {
                    var info = Field(field.Field);

                    if (field.Field.IsStatic)
                    {
                        value();

                        // A static holding an inline value receives the whole block; the storage
                        // underneath is the widened range the linker laid out.
                        if (TryMultiSlotWidth(field.Field.Type, out int staticWidth))
                            Code.StoreValueStatic(info, staticWidth);
                        else
                            Code.StoreStaticField(info);

                        return;
                    }

                    var receiver = field.Receiver ?? throw Unsupported($"a write to '{field.Field.Name}' with no receiver");
                    Expression(receiver);
                    value();

                    if (TryMultiSlotWidth(field.Field.Type, out int width))
                        Code.StoreValueField(info, width);
                    else
                        Code.StoreField(info);

                    return;
                }

                case BoundPropertyExpression property:
                {
                    var setter = property.Property.Setter
                        ?? throw Unsupported($"a write to '{property.Property.Name}', which has no setter");

                    if (TryInlineAutoAccessorSet(property.Property, property.Receiver, property.IsVirtualSet, value))
                        return;

                    if (TryInlinePropertySetter(property.Property, property.Receiver, property.IsVirtualSet, value))
                        return;

                    if (!property.Property.IsStatic)
                    {
                        var receiver = property.Receiver ?? throw Unsupported($"a write to '{property.Property.Name}' with no receiver");
                        Expression(receiver);
                        BoxReceiverForCall(setter, receiver.Type);
                    }

                    value();

                    // The mirror of what UnerasedCallResult does on a read: a setter declared
                    // against a contract's own parameter (`IBox<T>.value`) takes an erased slot
                    // however concrete the receiver's construction is, so the value has to become a
                    // reference on the way in. The binder writes no conversion node here - it
                    // checked the assignment against the property's *substituted* type, which is
                    // already `int` - so this is the only place that can know.
                    ErasedCallArgument(setter);

                    EmitResolvedCall(setter, virtualCall: property.IsVirtualSet, discardResult: true);
                    return;
                }

                case BoundIndexExpression index:
                {
                    Expression(index.Target);
                    Expression(index.Index);
                    // The key crosses into one-reference storage exactly as the value does:
                    // a range or tuple key is stored packed.
                    BoxIfMultiSlot(index.Index.Type);
                    value();
                    UnboxIfStillErased(index.Type);
                    // One-reference storage keeps an inline value boxed.
                    BoxIfMultiSlot(index.Type);

                    var owner = index.Target.Type.NonNullable;
                    if (owner.TypeKind == TypeSymbolKind.Array)
                        Code.ArrSet();
                    else if (owner.TypeKind == TypeSymbolKind.Dictionary)
                        Code.DictSet();
                    else
                        throw Unsupported($"an indexed write to '{index.Target.Type.ToDisplayString()}'");

                    return;
                }

                default:
                    throw Unsupported($"an assignment to {target.GetType().Name}");
            }
        }

        private void EmitConditional(BoundConditionalExpression conditional)
        {
            var otherwise = Code.NewLabel();
            var end = Code.NewLabel();

            Expression(conditional.Condition);
            Code.JumpIfFalse(otherwise);
            Expression(conditional.WhenTrue);
            Code.Jump(end);
            Code.MarkLabel(otherwise);
            Expression(conditional.WhenFalse);
            Code.MarkLabel(end);
        }

        /// <summary>
        /// Whether this type occupies more than one frame slot inline - a multi-field value class
        /// or a non-empty tuple - and if so, its flattened width.
        /// </summary>
        private static bool TryMultiSlotWidth(TypeSymbol type, out int width)
            => ValueTypeLayout.IsInlineType(type, out width) && width > 1;

        private void EmitFieldRead(BoundFieldExpression field)
        {
            var info = Field(field.Field);

            if (field.Field.IsStatic)
            {
                // An enum case is exactly this: a static, read-only field of the enum's own type
                // holding the one instance its static initializer built. A static whose declared
                // type is an inline value reads its whole block instead, from the widened storage
                // the linker gave it.
                if (TryMultiSlotWidth(field.Field.Type, out int staticWidth))
                    Code.LoadValueStatic(info, staticWidth);
                else
                    Code.LoadStaticField(info);

                UnerasedFieldResult(field.Field);
                return;
            }

            var receiver = field.Receiver ?? throw Unsupported($"a read of '{field.Field.Name}' with no receiver");

            // An inline value class lives as a block of slots: reading a field of one reads
            // the sub-slot at the field's flattened offset - directly out of the frame range when
            // the receiver already has a home, spilled to a temp first when it does not.
            if (receiver.Type.NonNullable is NamedTypeSymbol receiverValue && ValueTypeLayout.IsBlockValueClass(receiverValue))
            {
                if (!ValueTypeLayout.TryGet(receiverValue, out var layout, out var layoutError))
                    throw Unsupported(layoutError!);

                int fieldIndex = Array.IndexOf(layout.Fields, field.Field);
                if (fieldIndex < 0)
                    throw Unsupported($"a read of '{field.Field.Name}', which is not an instance field of '{receiverValue.Name}'");

                int offset = layout.Offsets[fieldIndex];
                int width = layout.FieldWidths[fieldIndex];
                int baseSlot = EnsureLocalRange(receiver, layout.Width);

                if (width > 1)
                    Code.LoadValueLocal(baseSlot + offset, width);
                else
                    Code.LoadLocalField(baseSlot, offset);

                UnerasedFieldResult(field.Field);
                return;
            }

            // A value class is its one field, so reading that field off one is the value itself �
            // there is no instance to load from (�2.9). A field declared against the class's own
            // type parameter is still an erased slot, so a value that reached it was boxed on the
            // way in and has to come back out the same way any other erased field does.
            if (receiver.Type.NonNullable.TypeKind == TypeSymbolKind.ValueClass)
            {
                Expression(receiver);
                UnerasedFieldResult(field.Field);
                return;
            }

            // A field whose declared type is a multi-field value class holds the value inline:
            // reading it moves the whole block out of the instance (or static storage) rather
            // than lifting one slot through a boxed reference. Sub-slot reads of that block go
            // back through the receiver branch above, which sums the absolute offset.
            Expression(receiver);
            if (TryMultiSlotWidth(field.Field.Type, out int fieldWidth))
                Code.LoadValueField(info, fieldWidth);
            else
                Code.LoadField(info);

            UnerasedFieldResult(field.Field);
        }

        /// <summary>
        /// Reads a field declared against its own class's type parameter back out the same way
        /// <see cref="UnerasedCallResult"/> does for a generic method's result (�1.11's second
        /// obligation).
        /// </summary>
        /// <remarks>
        /// A field typed <c>T</c> is a real erased slot � unlike an <c>array</c>/<c>dict</c> element,
        /// it has no dedicated opcode bypassing the erasure convention, so a value reaching it was
        /// boxed on the way in (<c>ConversionTarget</c>) and has to be cast-and-unboxed on the way
        /// back out, exactly as a written <c>as</c> does. <c>field.Type</c> already reads the
        /// substituted type (<c>int</c> for a <c>Box&lt;int&gt;</c> receiver); <c>original</c> is the
        /// unsubstituted declaration, whose parameter is still bare <c>T</c> when this read needed
        /// substituting at all.
        /// </remarks>
        private void UnerasedFieldResult(FieldSymbol field)
        {
            var original = field.OriginalDefinition ?? field;
            if (original.Type.NonNullable is TypeParameterSymbol)
                Unerase(field.Type);
        }

        private void EmitPropertyRead(BoundPropertyExpression property)
        {
            // Every built-in collection's `length` is a native getter, but each has a dedicated
            // opcode that reads the count in one dispatch with no frame � the same thing, matched by
            // the getter's identity so a user `length` on another type is untouched. `ArrLen`/
            // `TupLen` are also what the `for-in` lowering over an array/tuple already reads the
            // count with; this is the same opcode reaching the same answer from a property read.
            if (IsDictionaryLength(property.Property))
            {
                if (property.Property.IsStatic)
                    throw Unsupported($"a read of 'dict.{property.Property.Name}', which is not static");

                Expression(property.Receiver!);
                Code.DictLen();
                return;
            }

            if (IsArrayLength(property.Property))
            {
                Expression(property.Receiver!);
                Code.ArrLen();
                return;
            }

            if (IsStringLength(property.Property))
            {
                Expression(property.Receiver!);
                Code.StrLen();
                return;
            }

            if (IsTupleLength(property.Property))
            {
                var receiver = property.Receiver ?? throw Unsupported($"a read of 'tuple.{property.Property.Name}' with no receiver");

                // A tuple's arity is static at every well-typed site: fold it into the
                // instruction instead of dispatching to the boxed form's own length.
                if (receiver.Type.NonNullable is TupleTypeSymbol typed)
                {
                    Code.LoadInt(typed.ElementTypes.Count);
                    return;
                }

                Expression(receiver);
                Code.TupLen();
                return;
            }

            // A range is its own three-slot block, so reading `start`, `end` or `isInclusive` off
            // one is a sub-slot read - the same lowering a multi-field value class field takes.
            // No frame, no pack, no call; the other range members still reach their native bodies
            // with the block as receiver.
            if (!property.Property.IsStatic && TryRangeSlotRead(property))
                return;

            if (TryInlineAutoAccessorGet(property.Property, property.Receiver, property.IsVirtualGet, discardResult: false))
                return;

            if (TryInlinePropertyGetter(property))
                return;

            var getter = property.Property.Getter
                ?? throw Unsupported($"a read of '{property.Property.Name}', which has no getter");

            if (!property.Property.IsStatic)
            {
                var receiver = property.Receiver ?? throw Unsupported($"a read of '{property.Property.Name}' with no receiver");
                Expression(receiver);
                BoxReceiverForCall(getter, receiver.Type);
            }

            EmitResolvedCall(getter, virtualCall: property.IsVirtualGet, discardResult: false);

            // The same second obligation §1.11 puts on a generic method's result, and for exactly
            // the same reason: a property declared against a contract's own parameter
            // (`IIterator<T>.current`) is compiled once with an erased return, so the bridge hands
            // back a box however concrete the receiver's construction is. `EmitCall` has always
            // done this; a property read reaches the very same getter by a different route, and
            // without it `cursor.current` on an `IIterator<int>` leaves a reference where an `int`
            // is expected - which the interpreter then adds as its payload rather than trapping.
            UnerasedCallResult(getter);
        }

        /// <summary>The name a built-in collection's <c>length</c> getter is emitted under.</summary>
        private const string LengthGetterName = "get_length";

        private static bool IsDictionaryLength(PropertySymbol property)
            => property.Getter is { } getter && IsDictionaryMember(getter, LengthGetterName);

        /// <summary>Whether <paramref name="property"/> is, by identity, the built-in array's <c>length</c>.</summary>
        private static bool IsArrayLength(PropertySymbol property)
            => property.Getter is { } getter && IsArrayMember(getter, LengthGetterName);

        /// <summary>Whether <paramref name="property"/> is, by identity, the built-in string's <c>length</c>.</summary>
        private static bool IsStringLength(PropertySymbol property)
            => property.Getter is { } getter && IsStringMember(getter, LengthGetterName);

        /// <summary>Whether <paramref name="property"/> is, by identity, the built-in tuple's <c>length</c>.</summary>
        private static bool IsTupleLength(PropertySymbol property)
            => property.Getter is { } getter && IsTupleMember(getter, LengthGetterName);

        /// <summary>
        /// Replaces a read of an auto-property by the field load that is its whole body (�3.4, �3.6).
        /// </summary>
        /// <remarks>
        /// An auto-accessor has no bound body for <see cref="TryInline"/> to splice, so the read is
        /// lowered here, in the shape <see cref="ModuleEmitter.EmitAutoAccessor"/> would have given
        /// the body: a static one is the backing field, an instance one is the field off the
        /// receiver. Only an accessor proven non-virtual at this access can be replaced � a
        /// <c>Direct</c> one always qualifies, and a <c>virtual</c>/<c>override</c> one qualifies
        /// exactly where <see cref="BoundPropertyExpression.IsVirtualGet"/> already devirtualised it
        /// (a sealed receiver, <c>super</c>, or a <c>sealed override</c>) � either way nothing below
        /// this access can change which body runs. A value class's receiver is the wrapped field,
        /// not an instance to read a field from (�2.9).
        /// </remarks>
        private bool TryInlineAutoAccessorGet(PropertySymbol property, BoundExpression? receiver, bool isVirtualGet, bool discardResult)
        {
            var getter = property.Getter;
            if (getter is null || isVirtualGet || !IsAutoAccessor(property, getter))
                return false;

            // �3.6: `noinline` refuses the backing-field fold too � the access stays a call to the
            // accessor the module really carries, which is what "no folding" has to mean when the
            // body is one instruction.
            if (getter.IsNoInline)
                return false;

            if (property.IsStatic)
            {
                Code.LoadStaticField(Field(property.BackingField!));
                if (discardResult)
                    Code.Pop();
                return true;
            }

            if (receiver is null || receiver.Type.NonNullable.TypeKind == TypeSymbolKind.ValueClass)
                return false;

            Expression(receiver);
            Code.LoadField(Field(property.BackingField!));
            if (discardResult)
                Code.Pop();
            return true;
        }

        /// <summary>
        /// Replaces a write to an auto-property by the field store that is its whole body (�3.4, �3.6).
        /// </summary>
        /// <remarks>
        /// The inverse of <see cref="TryInlineAutoAccessorGet"/>: a static one stores the backing
        /// field, an instance one stores the field off the receiver. The value is still emitted by
        /// the caller's <paramref name="value"/> callback, between the receiver and the store, which
        /// is the order every other write target uses.
        /// </remarks>
        private bool TryInlineAutoAccessorSet(PropertySymbol property, BoundExpression? receiver, bool isVirtualSet, Action value)
        {
            var setter = property.Setter;
            if (setter is null || isVirtualSet || !IsAutoAccessor(property, setter))
                return false;

            // �3.6: the write-side twin of the getter's `noinline` guard above.
            if (setter.IsNoInline)
                return false;

            if (property.IsStatic)
            {
                value();
                Code.StoreStaticField(Field(property.BackingField!));
                return true;
            }

            if (receiver is null || receiver.Type.NonNullable.TypeKind == TypeSymbolKind.ValueClass)
                return false;

            Expression(receiver);
            value();
            Code.StoreField(Field(property.BackingField!));
            return true;
        }

        /// <summary>Whether an accessor is one the emitter supplies against a backing field, rather than one a body was bound for.</summary>
        private bool IsAutoAccessor(PropertySymbol property, MethodSymbol accessor)
            => property.BackingField is not null
                && !accessor.IsNative
                && (_context.Bodies is null || !_context.Bodies.ContainsKey(accessor));

        /// <summary>
        /// Splices an explicit getter at its read site (�3.6), by the hint written on the property or
        /// by the cost heuristic, whichever lets it in.
        /// </summary>
        /// <remarks>
        /// A property read reaches the getter through <see cref="EmitPropertyRead"/>, not through
        /// <see cref="EmitCall"/> � so without this the <c>inline</c> a property declares would never
        /// be honoured. The getter is shaped as the zero-argument call it is, and the rest is the
        /// ordinary splice. Only a getter proven non-virtual at this access can be replaced � see
        /// <see cref="BoundPropertyExpression.IsVirtualGet"/> � exactly as the auto-accessor path
        /// requires.
        /// </remarks>
        private bool TryInlinePropertyGetter(BoundPropertyExpression property)
        {
            var getter = property.Property.Getter;
            if (getter is null)
                return false;

            // �3.6: `noinline` on the accessor (or the property it inherits it from) keeps the read
            // a real call � no splice by hint or heuristic.
            if (getter.IsNoInline)
                return false;

            // An extension property's getter (�15) takes its receiver as an ordinary declared
            // parameter, not out-of-band the way `property.Receiver` is here � the synthetic
            // zero-argument call below assumes the opposite (`Arguments` empty, receiver carried
            // separately), so splicing it in would bind that empty list against a getter that
            // actually declares one parameter. `EmitPropertyRead`'s general path already knows how
            // to push the receiver as that parameter; only the splice is unsafe.
            if (getter.ExtensionTargetType is not null)
                return false;

            // A still-virtual or native getter can never be spliced - the synthetic zero-argument
            // call built below always claims non-virtual, so TryInline's own dispatch guard cannot
            // catch it and this check has to stand in for it. forceinline still has to fail loudly
            // here rather than silently fall through to an ordinary (possibly virtual) call the way
            // the `inline` hint is allowed to.
            if (property.IsVirtualGet || getter.IsNative)
            {
                if (getter.IsForceInline)
                    throw Unsupported($"'forceinline {getter.Name}', whose body is not available to splice");

                return false;
            }

            if (!getter.IsInline && !getter.IsForceInline)
            {
                if (_context.Bodies is null || !_context.Bodies.TryGetValue(getter, out var body))
                    return false;

                if (_context.InlineCostOf(body) > InlineCost.DefaultThreshold)
                    return false;
            }

            var call = new BoundCallExpression(property.Syntax, property.Receiver, getter, Array.Empty<BoundExpression>(), isVirtual: false);
            if (TryInline(call, discardResult: false))
                return true;

            if (getter.IsForceInline)
                throw Unsupported($"'forceinline {getter.Name}', whose body is not available to splice");

            return false;
        }

        /// <summary>
        /// Splices an explicit setter at its write site (�3.6), by the hint written on the property
        /// or by the cost heuristic, whichever lets it in � the write-side twin of
        /// <see cref="TryInlinePropertyGetter"/>.
        /// </summary>
        /// <remarks>
        /// A property write reaches the setter through <see cref="Store"/>, which does not always
        /// have a <see cref="BoundExpression"/> for the value to hand <see cref="TryInline"/> � a
        /// compound assignment or an increment/decrement has already lowered it into a plain local
        /// by the time <see cref="Store"/> runs, and only hands over an <c>Action</c> that emits
        /// whatever the value turned out to be. <see cref="TryInlineSetterBody"/> below is that same
        /// splice, built directly against <paramref name="value"/> instead of a bound argument list.
        /// Only a setter proven non-virtual at this access can be replaced, exactly as the getter
        /// and the auto-accessor paths require.
        /// </remarks>
        private bool TryInlinePropertySetter(PropertySymbol property, BoundExpression? receiver, bool isVirtualSet, Action value)
        {
            var setter = property.Setter;
            if (setter is null)
                return false;

            // �3.6: `noinline` on the accessor (or the property it inherits it from) keeps the
            // write a real call � no splice by hint or heuristic.
            if (setter.IsNoInline)
                return false;

            // Same reason as the matching guard in TryInlinePropertyGetter: an extension property's
            // setter (�15) declares the receiver as an ordinary parameter ahead of `value`, which
            // TryInlineSetterBody's splice does not know to supply.
            if (setter.ExtensionTargetType is not null)
                return false;

            // Mirrors the same guard on TryInlinePropertyGetter, for the same reason: a still-
            // virtual or native setter can never be spliced, but forceinline still has to fail
            // loudly here instead of silently falling through to an ordinary (possibly virtual)
            // call.
            if (isVirtualSet || setter.IsNative)
            {
                if (setter.IsForceInline)
                    throw Unsupported($"'forceinline {setter.Name}', whose body is not available to splice");

                return false;
            }

            if (!setter.IsInline && !setter.IsForceInline)
            {
                if (_context.Bodies is null || !_context.Bodies.TryGetValue(setter, out var costBody))
                    return false;

                if (_context.InlineCostOf(costBody) > InlineCost.DefaultThreshold)
                    return false;
            }

            if (TryInlineSetterBody(setter, receiver, value))
                return true;

            if (setter.IsForceInline)
                throw Unsupported($"'forceinline {setter.Name}', whose body is not available to splice");

            return false;
        }

        /// <summary>
        /// The actual splice for <see cref="TryInlinePropertySetter"/>, once the hint or the
        /// heuristic has already let the setter in. Mirrors <see cref="TryInline"/>'s guards and
        /// receiver handling, but feeds the setter's one parameter from <paramref name="value"/>
        /// rather than a bound argument, and never carries a result � a setter always returns void,
        /// so there is nothing here that plays <see cref="TryInline"/>'s tail-return or result-local
        /// role.
        /// </summary>
        private bool TryInlineSetterBody(MethodSymbol setter, BoundExpression? receiver, Action value)
        {
            if (_context.Bodies is null || !_context.Bodies.TryGetValue(setter, out var body))
                return false;

            if (_inlines.Count >= MaxInlineDepth)
                return false;

            // Guards against a setter splicing itself, directly or through another inline function -
            // see TryInline's identical checks for why.
            if (ReferenceEquals(setter, _symbol))
                return false;

            foreach (var frame in _inlines)
            {
                if (ReferenceEquals(frame.Method, setter))
                    return false;
            }

            SurtrLocal? receiverSlot = null;
            if (receiver is not null)
            {
                var slot = _method.HasReceiver ? DeclareTemp("$inlineThis", _symbol.ContainingType!) : _method.DeclareLocal("$inlineThis");
                Expression(receiver);
                EmitStoreLocal(slot);
                receiverSlot = slot;
            }

            var valueSlot = DeclareTemp("$inline$" + setter.Parameters[0].Name, setter.Parameters[0].Type);
            value();
            EmitStoreLocal(valueSlot);
            _splicedParameters[setter.Parameters[0]] = valueSlot;

            var exit = Code.NewLabel();
            _inlines.Add(new InlineFrame(setter, exit, default, false, receiverSlot, _finallies.Count));
            Statement(body);
            _inlines.RemoveAt(_inlines.Count - 1);

            if (Code.IsReachable)
                Code.Jump(exit);

            Code.MarkLabel(exit);
            return true;
        }

        private void EmitObjectCreation(BoundObjectCreationExpression creation)
        {
            var type = (NamedTypeSymbol)creation.Type.NonNullable;

            if (type.TypeKind == TypeSymbolKind.ValueClass)
            {
                EmitValueClassCreation(creation, type);
                return;
            }

            // An instance-factory constructor - every native class's - creates the object itself:
            // no receiver crosses the wire, its parameters start at slot 0, and the new reference
            // comes back over that same slot. There is nothing for ObjNew to allocate and no
            // receiver to run a body against, so the call is flat and its result is the
            // expression's value. The metadata-level convention is "a constructor whose return
            // names its class rather than void", which at symbol level is Role plus a non-void
            // return.
            if (creation.Constructor is { } factory
                && factory.Role == MethodRole.Constructor
                && !factory.ReturnType.IsVoid)
            {
                foreach (var argument in creation.Arguments)
                    Expression(argument);

                EmitResolvedCall(factory, virtualCall: false, discardResult: false);
                return;
            }

            Code.NewObject(Descriptors.Emit(type));

            if (creation.Constructor is null)
            {
                // A type whose fields have initializers got a constructor the source never wrote,
                // and skipping it would leave every instance holding zeroes. It has no symbol, so
                // both halves have to be asked: a builder inside this module, metadata across a
                // module boundary.
                if (_context.TryGetDefaultConstructor(type, out var synthesised))
                {
                    Code.Dup();
                    Code.Call(synthesised, discardResult: true);
                }
                else if (_context.TryGetBuiltDefaultConstructor(type, out var built))
                {
                    Code.Dup();
                    Code.Call(built, discardResult: true);
                }

                return;
            }

            // The constructor returns nothing, so the instance has to survive it: duplicate it, let
            // the call consume one copy as its receiver, and the other is the expression's value.
            Code.Dup();

            foreach (var argument in creation.Arguments)
                Expression(argument);

            EmitResolvedCall(creation.Constructor, virtualCall: false, discardResult: true);
        }

        /// <summary>
        /// Builds a <c>value class</c>, which allocates nothing (�2.9).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A value class <em>is</em> the field it wraps wherever its type is statically known, so
        /// <c>ObjNew</c> would be exactly wrong: it would allocate an instance of the erased type,
        /// which for <c>EntityId</c> over an <c>int</c> is <c>int</c> itself. What the construction
        /// evaluates to is whatever its constructor puts in that field.
        /// </para>
        /// <para>
        /// So the constructor is spliced rather than called, and the shape it has to have is
        /// <c>this.field = expression</c> � one assignment, nothing else. That is what a value
        /// class's constructor is for, and anything wider is refused rather than approximated,
        /// because there is no object for a second statement to observe.
        /// </para>
        /// </remarks>
        private void EmitValueClassCreation(BoundObjectCreationExpression creation, NamedTypeSymbol type)
        {
            // An instance-factory constructor - every native class's, and an inline struct's
            // [SurtrNativeConstructor] - creates the value itself: no receiver crosses the wire,
            // its parameters start at slot 0, and the result comes back over that same slot. The
            // call is flat and its result is the expression's value. Checked before the block
            // lowering below, because a factory carries logic of its own: lowering `V2(x, y)`
            // straight to a field block would silently drop it.
            if (creation.Constructor is { } factory
                && factory.Role == MethodRole.Constructor
                && !factory.ReturnType.IsVoid)
            {
                foreach (var argument in creation.Arguments)
                    Expression(argument);

                EmitResolvedCall(factory, virtualCall: false, discardResult: false);
                return;
            }

            if (ValueTypeLayout.IsMultiField(type))
            {
                EmitMultiValueClassCreation(creation, type);
                return;
            }

            if (creation.Constructor is not MethodSymbol constructor)
            {
                // A value class that declared no constructor was given one by the binder taking the
                // type of its single field; the binder already converted the one argument against
                // that field type, so the value to wrap is simply the argument. (The binding
                // guarantees exactly one argument here � zero or several never reach emission.)
                if (creation.Arguments.Count == 1)
                {
                    Expression(creation.Arguments[0]);
                    return;
                }

                throw Unsupported(
                    $"building a '{type.Name}' with no constructor, which leaves nothing to put in the field it wraps");
            }

            // A construction of a generic value class carries the substituted clone (�6), whose
            // parameters are new symbols and whose body is keyed by the declaration � bodies are
            // bound once against it, never against a view. So the body and the spliced assignment's
            // parameters both come from the declaration, and each argument maps onto the
            // *declaration's* parameter, which is the one the assignment's expression references.
            var original = constructor.OriginalDefinition ?? constructor;

            if (_context.Bodies is null
                || !_context.Bodies.TryGetValue(original, out var body)
                || WrappedValue(body) is not BoundExpression wrapped)
            {
                throw Unsupported(
                    $"building a '{type.Name}', whose constructor is not a single assignment to the field it wraps");
            }

            // The canonical value-class constructor is `this._field = value;` � the wrapped value is
            // a direct read of the one parameter. Then the construction *is* the argument, so it is
            // emitted straight rather than spliced through a temp local: the splice would pay a
            // `Stl $value$...; Ldl $value$...` round-trip (and a local slot) for a value that is
            // never used more than once or combined with anything. This is the shape every wrapper
            // (EntityId, Angle, Sequence<T> over a closure, ...) is written in, so it is the one
            // that must be free.
            if (creation.Arguments.Count == 1
                && wrapped is BoundParameterExpression { Parameter: var read }
                && ReferenceEquals(read, original.Parameters[0]))
            {
                Expression(creation.Arguments[0]);
                return;
            }

            for (int i = 0; i < creation.Arguments.Count; i++)
            {
                var slot = _method.DeclareLocal("$value$" + original.Parameters[i].Name);
                Expression(creation.Arguments[i]);
                EmitStoreLocal(slot);
                _splicedParameters[original.Parameters[i]] = slot;
            }

            Expression(wrapped);
        }

        /// <summary>The right-hand side of a value class constructor's one assignment.</summary>
        private static BoundExpression? WrappedValue(BoundStatement body)
        {
            var statements = body is BoundBlockStatement block ? block.Statements : new BoundStatement[] { body };

            BoundExpression? found = null;
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case BoundNopStatement:
                        continue;

                    case BoundExpressionStatement
                    {
                        Expression: BoundAssignmentExpression
                        {
                            Target: BoundFieldExpression { Receiver: BoundThisExpression },
                        } assignment,
                    } when found is null:
                        found = assignment.Value;
                        continue;

                    default:
                        return null;
                }
            }

            return found;
        }

        /// <summary>
        /// Builds a multi-field value class: every constructor assignment evaluates straight onto
        /// the operand stack, in field order, leaving exactly the block one value occupies.
        /// </summary>
        /// <remarks>
        /// No temp holds the value under construction - there is nothing to take its address of,
        /// and a second kind of statement in the constructor would observe half-built slots. The
        /// shape is therefore strict: one <c>this.field = expression</c> per instance field, no
        /// reads of <c>this</c> on any right-hand side, nothing else.
        /// </remarks>
        private void EmitMultiValueClassCreation(BoundObjectCreationExpression creation, NamedTypeSymbol type)
        {
            if (creation.Constructor is not MethodSymbol constructor)
                throw Unsupported($"building a '{type.Name}', which declares several fields and so needs a constructor that assigns each one");

            var original = constructor.OriginalDefinition ?? constructor;

            if (_context.Bodies is null
                || !_context.Bodies.TryGetValue(original, out var body)
                || !TryGetFieldAssignments(body, out var assignments))
            {
                throw Unsupported(
                    $"building a '{type.Name}': its constructor must be exactly one 'this.field = expression' per field, with no other statements");
            }

            if (!ValueTypeLayout.TryGet(type, out var layout, out var layoutError))
                throw Unsupported(layoutError!);

            if (assignments.Count != layout.Fields.Length)
                throw Unsupported($"building a '{type.Name}': its constructor assigns {assignments.Count} field(s), but the class declares {layout.Fields.Length}");

            for (int i = 0; i < creation.Arguments.Count; i++)
            {
                var slot = DeclareTemp("$value$" + original.Parameters[i].Name, original.Parameters[i].Type);
                Expression(creation.Arguments[i]);
                EmitStoreLocal(slot);
                _splicedParameters[original.Parameters[i]] = slot;
            }

            // Emit in declaration order, whatever order the constructor wrote the assignments in,
            // so the stack block matches the flattened layout the runtime links.
            var ordered = new List<(int Index, BoundExpression Value)>(assignments.Count);
            foreach (var entry in assignments)
                ordered.Add((Array.IndexOf(layout.Fields, entry.Field), entry.Value));

            ordered.Sort((x, y) => x.Index.CompareTo(y.Index));

            foreach (var entry in ordered)
                Expression(entry.Value);
        }

        /// <summary>Reads a multi-field value class constructor as its ordered list of field assignments.</summary>
        private static bool TryGetFieldAssignments(
            BoundStatement body,
            out List<(FieldSymbol Field, BoundExpression Value)> assignments)
        {
            assignments = new List<(FieldSymbol, BoundExpression)>();

            var statements = body is BoundBlockStatement block ? block.Statements : new BoundStatement[] { body };

            foreach (var statement in statements)
            {
                if (statement is not BoundExpressionStatement
                    {
                        Expression: BoundAssignmentExpression
                        {
                            Target: BoundFieldExpression { Receiver: BoundThisExpression, Field: var target },
                            Value: var value,
                        },
                    })
                {
                    return false;
                }

                assignments.Add((target, value));
            }

            return true;
        }

        /// <summary>
        /// Emits a call, choosing between splicing it, folding it and really making it.
        /// </summary>
        private void EmitCall(BoundCallExpression call, bool discardResult)
        {
            // �3.6: a `noinline` callee refuses every optional fold of its invocations � the const
            // fold below and the splice paths further down. What runs is the declaration itself, as
            // a real call, at every site. The folds �7 makes *mandatory* (a `const` initializer, a
            // `const if` condition) never reach here; those are evaluation semantics, not
            // optimization, and no modifier vetoes them.
            if (!call.Method.IsNoInline && TryFoldConstCall(call, discardResult))
                return;

            // A method on a built-in collection is a native body the compiler could emit a call to,
            // but each of these operations also has a dedicated opcode that does the same thing in
            // one dispatch and no frame. Where the callee is one of them, this call site takes the
            // opcode � the member is matched by identity so a user type that happens to declare its
            // own `remove` is not confused with the built-in's. A local method never is one (its
            // `ImportedFrom` is null), so the three Try* dispatches are gated on a single
            // precomputed identity set instead of probing each name against the built-in classes.
            if (call.Method.ImportedFrom is { } imported && OpcodeableMembers.Value.Contains(imported))
            {
                if (TryEmitDictionaryOperation(call, discardResult))
                    return;

                if (TryEmitArrayOperation(call, discardResult))
                    return;

                if (TryEmitStringOperation(call, discardResult))
                    return;
            }

            if (call.Method.IsForceInline)
            {
                if (TryInline(call, discardResult))
                    return;

                throw Unsupported($"'forceinline fun {call.Method.Name}', whose body is not available to splice");
            }

            // `inline` is a hint and the heuristic a guess: a body either of them names that cannot
            // be spliced falls through to a real call rather than failing the whole module. A
            // `noinline` callee reaches neither branch � it is exclusive with both hints at parse
            // time, and the heuristic is asked only when nothing was written.
            if (!call.Method.IsNoInline && (call.Method.IsInline || ShouldInlineByCost(call)))
            {
                if (TryInline(call, discardResult))
                    return;
            }

            if (call.Receiver is not null)
            {
                Expression(call.Receiver);

                // �6.3: a value class is the field it wraps, and a field is not something to
                // dispatch on � so a call that might resolve through the receiver's class boxes
                // first, and `this` inside the callee unwraps. A direct dispatch needs neither.
                BoxReceiverForCall(call.Method, call.Receiver.Type);
            }

            foreach (var argument in call.Arguments)
                Expression(argument);

            EmitResolvedCall(call.Method, call.IsVirtual, discardResult);

            if (!discardResult)
                UnerasedCallResult(call.Method);
        }

        /// <summary>
        /// Reads a generic method's result back out the same way any other erased slot is read
        /// (�1.11's second obligation), when the call left one on the stack.
        /// </summary>
        /// <remarks>
        /// <c>SubstituteGenericCandidates</c> replaces a generic method with the concrete view its
        /// arguments infer (�6) before binding ever sees it, so <c>call.Method.ReturnType</c> already
        /// reads <c>int</c> where the declaration reads <c>T</c> - correct for type checking, since
        /// that is genuinely what a caller gets back, but silent about the fact that the declaration
        /// is compiled once, generically, and so its own return slot is erased regardless of what a
        /// given call substituted <c>T</c> to. <see cref="Unerase"/> is the same cast-and-unbox
        /// <c>ExplicitErasure</c> already performs for a written <c>as</c>; the only new part is
        /// noticing a plain call needs it too, from <see cref="MethodSymbol.OriginalDefinition"/>
        /// rather than from a conversion node nothing here asked the binder to write one for.
        /// </remarks>
        private void UnerasedCallResult(MethodSymbol method)
        {
            var original = method.OriginalDefinition ?? method;
            if (original.ReturnType.NonNullable is TypeParameterSymbol)
                Unerase(method.ReturnType);
        }

        /// <summary>
        /// Boxes a single argument on its way into an erased parameter slot - §1.11's first
        /// obligation, and the exact mirror of <see cref="UnerasedCallResult"/>.
        /// </summary>
        /// <remarks>
        /// Only for a one-parameter callee reached without an argument list of its own, which in
        /// practice means a property setter. An ordinary call gets this from the binder, which
        /// writes a conversion node for every argument; an assignment through a property does not,
        /// because the binder checked it against the property's substituted type and found nothing
        /// to convert. <c>BoxDynamic</c> rather than a typed box for the same reason the read side
        /// uses <c>Unerase</c>: the value may already be a reference, and this is a no-op then.
        /// </remarks>
        private void ErasedCallArgument(MethodSymbol method)
        {
            var original = method.OriginalDefinition ?? method;

            if (original.Parameters.Count == 1
                && original.Parameters[0].Type.NonNullable is TypeParameterSymbol)
            {
                Code.BoxDynamic();
            }
        }

        /// <summary>
        /// Emits the call instruction for a method whose receiver and arguments are already on the
        /// stack.
        /// </summary>
        /// <remarks>
        /// Where a call lands is a property of the callee, not of the call site � the interpreter
        /// reads it off the method it names � so nothing here picks between bytecode and host code.
        /// What it does pick between is the four <em>tables</em> a callee can live in: this module's
        /// method builders, another module's functions, an interface's slots, and everything else.
        /// </remarks>
        private void EmitResolvedCall(MethodSymbol method, bool virtualCall, bool discardResult)
        {
            if (_context.TryGetBuilder(method, out var local))
            {
                // The one case where the call site rather than the callee decides: a `super` call,
                // or one on a sealed receiver, names a virtual method and must not go through the
                // vtable � an override calling its base would otherwise call itself.
                if (!virtualCall && !method.IsStatic && method.ContainingType is not null && method.Dispatch != MethodDispatch.Direct)
                    Code.CallSpecial(local, discardResult);
                else
                    Code.Call(local, discardResult);

                return;
            }

            var built = _context.Resolve(method)
                ?? throw Unsupported($"a call to '{method.Name}', which is neither being emitted here nor already built");

            if (_context.IsInterfaceMethod(method))
            {
                Code.CallInterface(built, discardResult);
                return;
            }

            // A module-level member records no owner, so an access-table entry cannot name one in
            // another module (`docs/VM-Plan.md` �3.3) - a cross-module call goes through the module
            // reference table by path instead. Both halves are asked: a module built earlier in this
            // compilation, and one referenced as an image.
            if (method.ContainingType is null
                && (_context.TryGetForeignModule(method, out var owner) || _context.TryGetReferencedModule(method, out owner)))
            {
                Code.CallExternal(owner, built, discardResult);
                return;
            }

            if (virtualCall && !method.IsStatic && method.ContainingType is not null)
            {
                Code.CallVirtual(built, discardResult);
                return;
            }

            Code.Call(built, discardResult);
        }

        /// <summary>Emits a call whose arguments are already emitted and which takes no receiver.</summary>
        private void EmitDirectCall(MethodSymbol method, bool discardResult)
            => EmitResolvedCall(method, virtualCall: false, discardResult);

        /// <summary>
        /// Emits an argument bound for the built-in <c>array</c>/<c>dict</c>'s own <c>G0</c>/<c>K</c>/
        /// <c>V</c>-typed member � a key, a value, an index target � the way
        /// <see cref="TryEmitDictionaryOperation"/>/<see cref="TryEmitArrayOperation"/> need it.
        /// </summary>
        /// <remarks>
        /// <c>ConversionTarget</c> (<c>BodyBinder.Expressions.cs</c>) converts an argument reaching a
        /// bare type-parameter-typed parameter against <c>unknown</c> rather than the substituted
        /// type, so a real generic method's own erased frame slot gets the box it needs. Array and
        /// dict declare their element/key/value members the same way (<c>G0</c>/<c>K</c>/<c>V</c>,
        /// per <c>docs/Runtime-Model.md</c>) for signature matching, but the opcodes these two
        /// methods emit instead of a real call � <c>ArrSet</c>, <c>DictSet</c>, <c>ArrPush</c>, � �
        /// read and write the collection's native <c>SurtrValue</c> storage directly and were never
        /// erased to begin with (<c>docs/VM-Plan.md</c> �3.5's "no per-element type tags"), so the
        /// box that conversion produces is not just unneeded here, it is actively wrong: it stores a
        /// boxed reference where the opcode expects the raw value, and a later <c>DictGet</c>/
        /// <c>ArrGet</c> hands that reference back as if it were the value itself. Stripping the
        /// erasure box and emitting the boxed operand's own pre-erasure expression restores the raw
        /// value these opcodes have always expected; nothing else about the conversion (an <c>int</c>
        /// literal reaching a <c>float</c> element, say) runs through this branch, so any conversion
        /// that is not the erasure artifact is left untouched.
        /// <para>
        /// A second, narrower case reaches the same problem from the other side: an argument whose
        /// own static type is <em>already</em> the bare type parameter � <c>item: T</c> passed to
        /// <c>_items.push(item)</c> from inside the generic body that declares <c>T</c> � converts
        /// against it by <c>Identity</c>, since source and destination are the same unsubstituted
        /// symbol, so there is no <c>ImplicitErasure</c> node here to strip. But <c>item</c> is still
        /// boxed, the same way any <c>T</c>-typed value at rest inside a still-generic body is (an
        /// argument or field write across the erasure boundary boxes on the way in). <see
        /// cref="UnboxIfStillErased"/> is what unwinds that for the write, the mirror of
        /// <see cref="BoxIfStillErased"/> on the read.
        /// </para>
        /// </remarks>
        private void EmitCollectionOperand(BoundExpression argument)
        {
            // An operand storing inline cannot cross into the collection's one-reference storage
            // raw: it packs once pushed. The type that decides is the operand's OWN - the
            // collection's members are declared against their bare G0/K/V parameters, so the
            // conversion sitting on top usually reads 'unknown' by the time it reaches here.
            if (argument is BoundConversionExpression { Conversion.Kind: ConversionKind.ImplicitErasure } conversion)
            {
                Expression(conversion.Operand);
                BoxIfMultiSlot(conversion.Operand.Type);
                return;
            }

            Expression(argument);
            BoxIfMultiSlot(argument.Type);
            UnboxIfStillErased(argument.Type);
        }

        /// <summary>
        /// Replaces a call to a member of the built-in <c>dict</c> by the opcode that does the same
        /// thing, so a host of these operations need not pay for a native frame.
        /// </summary>
        /// <remarks>
        /// The members lowered here are the ones with a dedicated opcode of identical semantics:
        /// <c>clear</c>, <c>get</c>, <c>set</c>, <c>containsKey</c>, <c>remove</c>, <c>keys</c> and
        /// <c>values</c>. <c>get</c>/<c>set</c> reach the same <c>DictGet</c>/<c>DictSet</c> the
        /// index form <c>m[k]</c> already does � lowered here too, rather than left to duplicate a
        /// native call the index form already avoids. <c>length</c> is handled separately, in
        /// <see cref="EmitPropertyRead"/>.
        /// </remarks>
        private bool TryEmitDictionaryOperation(BoundCallExpression call, bool discardResult)
        {
            if (call.Receiver is null)
                return false;

            if (IsDictionaryMember(call.Method, "clear"))
            {
                Expression(call.Receiver);
                Code.DictClear();
                return true;
            }

            if (IsDictionaryMember(call.Method, "get"))
            {
                Expression(call.Receiver);
                EmitCollectionOperand(call.Arguments[0]);
                Code.DictGet();
                if (discardResult)
                    Code.Pop();
                else
                {
                    UnpackIfMultiSlot(call.Method.ReturnType);
                    BoxIfStillErased(call.Method.ReturnType);
                }
                return true;
            }

            if (IsDictionaryMember(call.Method, "set"))
            {
                Expression(call.Receiver);
                EmitCollectionOperand(call.Arguments[0]);
                EmitCollectionOperand(call.Arguments[1]);
                Code.DictSet();
                return true;
            }

            if (IsDictionaryMember(call.Method, "containsKey"))
            {
                Expression(call.Receiver);
                EmitCollectionOperand(call.Arguments[0]);
                Code.DictIn();
                if (discardResult)
                    Code.Pop();
                return true;
            }

            if (IsDictionaryMember(call.Method, "remove"))
            {
                Expression(call.Receiver);
                EmitCollectionOperand(call.Arguments[0]);
                Code.DictDel();
                if (discardResult)
                    Code.Pop();
                return true;
            }

            if (IsDictionaryMember(call.Method, "keys"))
            {
                Expression(call.Receiver);
                Code.DictionaryKeys(Descriptors.Emit(call.Method.ReturnType.NonNullable));
                if (discardResult)
                    Code.Pop();
                return true;
            }

            if (IsDictionaryMember(call.Method, "values"))
            {
                Expression(call.Receiver);
                Code.DictionaryValues(Descriptors.Emit(call.Method.ReturnType.NonNullable));
                if (discardResult)
                    Code.Pop();
                return true;
            }

            return false;
        }

        /// <summary>
        /// The array twin of <see cref="TryEmitDictionaryOperation"/>: replaces a call to a member
        /// of the built-in <c>array</c> that has a dedicated opcode of identical semantics �
        /// <c>get</c>, <c>set</c>, <c>push</c>, <c>pop</c>, <c>insert</c>, <c>removeAt</c>,
        /// <c>clear</c>, <c>indexOf</c> and <c>contains</c>. <c>length</c> is handled separately, in
        /// <see cref="EmitPropertyRead"/>; <c>reverse</c>, <c>reserve</c>, <c>truncate</c>,
        /// <c>remove(value)</c> and <c>sort</c> have no opcode of their own and stay real calls.
        /// </summary>
        private bool TryEmitArrayOperation(BoundCallExpression call, bool discardResult)
        {
            if (call.Receiver is null)
                return false;

            if (IsArrayMember(call.Method, "get"))
            {
                Expression(call.Receiver);
                Expression(call.Arguments[0]);
                Code.ArrGet();
                if (discardResult)
                    Code.Pop();
                else
                {
                    UnpackIfMultiSlot(call.Method.ReturnType);
                    BoxIfStillErased(call.Method.ReturnType);
                }
                return true;
            }

            if (IsArrayMember(call.Method, "set"))
            {
                Expression(call.Receiver);
                Expression(call.Arguments[0]);
                EmitCollectionOperand(call.Arguments[1]);
                Code.ArrSet();
                return true;
            }

            if (IsArrayMember(call.Method, "push"))
            {
                Expression(call.Receiver);
                EmitCollectionOperand(call.Arguments[0]);
                Code.ArrPush();
                return true;
            }

            if (IsArrayMember(call.Method, "pop"))
            {
                Expression(call.Receiver);
                Code.ArrPop();
                if (discardResult)
                    Code.Pop();
                else
                    UnpackIfMultiSlot(call.Method.ReturnType);
                return true;
            }

            if (IsArrayMember(call.Method, "insert"))
            {
                Expression(call.Receiver);
                Expression(call.Arguments[0]);
                EmitCollectionOperand(call.Arguments[1]);
                Code.ArrInsert();
                return true;
            }

            if (IsArrayMember(call.Method, "removeAt"))
            {
                Expression(call.Receiver);
                Expression(call.Arguments[0]);
                Code.ArrRemoveAt();
                return true;
            }

            if (IsArrayMember(call.Method, "clear"))
            {
                Expression(call.Receiver);
                Code.ArrClear();
                return true;
            }

            if (IsArrayMember(call.Method, "indexOf"))
            {
                Expression(call.Receiver);
                EmitCollectionOperand(call.Arguments[0]);
                Code.ArrIndexOf();
                if (discardResult)
                    Code.Pop();
                return true;
            }

            if (IsArrayMember(call.Method, "contains"))
            {
                Expression(call.Receiver);
                EmitCollectionOperand(call.Arguments[0]);
                Code.ArrIn();
                if (discardResult)
                    Code.Pop();
                return true;
            }

            return false;
        }

        /// <summary>
        /// The string twin of <see cref="TryEmitDictionaryOperation"/>: <c>charAt</c> is exactly the
        /// index form <c>s[i]</c> under another name, so it reaches the same <c>StrGet</c>.
        /// <c>length</c> is handled separately, in <see cref="EmitPropertyRead"/>; every other
        /// string method (<c>substring</c>, <c>repeat</c>, �) has no opcode of its own.
        /// </summary>
        private bool TryEmitStringOperation(BoundCallExpression call, bool discardResult)
        {
            if (call.Receiver is null)
                return false;

            if (IsStringMember(call.Method, "charAt"))
            {
                Expression(call.Receiver);
                Expression(call.Arguments[0]);
                Code.StrGet();
                if (discardResult)
                    Code.Pop();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether <paramref name="method"/> is, by identity, the built-in dictionary's member of
        /// the given <paramref name="name"/>.
        /// </summary>
        /// <remarks>
        /// Identity is the point: an imported symbol keeps the very <c>SurtrMethodInfo</c> the
        /// built-in declares, and that survives the generic substitution <c>MemberLookup</c>
        /// performs � so a constructed <c>{K: V}</c> and its definition share one
        /// <c>ImportedFrom</c>. Comparing it by reference keeps a user class that declares its own
        /// <c>remove</c>/<c>clear</c>/� from being mistaken for the dictionary's.
        /// </remarks>
        private static bool IsDictionaryMember(MethodSymbol method, string name)
            => method.ImportedFrom is { } imported
               && ReferenceEquals(imported, DictionaryMethod(name));

        /// <summary>The single overload of a named built-in dictionary method, or <see langword="null"/>.</summary>
        private static SurtrMethodInfo? DictionaryMethod(string name)
            => SurtrBuiltIns.Dictionary.TryGetMethods(name, out var overloads) && overloads.Length == 1
                ? overloads[0]
                : null;

        /// <summary>The array twin of <see cref="IsDictionaryMember"/> � same identity reasoning.</summary>
        private static bool IsArrayMember(MethodSymbol method, string name)
            => method.ImportedFrom is { } imported
               && ReferenceEquals(imported, ArrayMethod(name));

        /// <summary>The single overload of a named built-in array method, or <see langword="null"/>.</summary>
        private static SurtrMethodInfo? ArrayMethod(string name)
            => SurtrBuiltIns.Array.TryGetMethods(name, out var overloads) && overloads.Length == 1
                ? overloads[0]
                : null;

        /// <summary>The string twin of <see cref="IsDictionaryMember"/> � same identity reasoning.</summary>
        private static bool IsStringMember(MethodSymbol method, string name)
            => method.ImportedFrom is { } imported
               && ReferenceEquals(imported, StringMethod(name));

        /// <summary>The single overload of a named built-in string method, or <see langword="null"/>.</summary>
        private static SurtrMethodInfo? StringMethod(string name)
            => SurtrBuiltIns.String.TryGetMethods(name, out var overloads) && overloads.Length == 1
                ? overloads[0]
                : null;

        /// <summary>The tuple twin of <see cref="IsDictionaryMember"/> � same identity reasoning.</summary>
        private static bool IsTupleMember(MethodSymbol method, string name)
            => method.ImportedFrom is { } imported
               && ReferenceEquals(imported, TupleMethod(name));

        /// <summary>The single overload of a named built-in tuple member, or <see langword="null"/>.</summary>
        private static SurtrMethodInfo? TupleMethod(string name)
            => SurtrBuiltIns.Tuple.TryGetMethods(name, out var overloads) && overloads.Length == 1
                ? overloads[0]
                : null;

        /// <summary>Whether this getter is one of the built-in range members the emitter reads as a sub-slot.</summary>
        private static bool IsRangeSlotGetter(MethodSymbol method, string property)
            => method.ImportedFrom is { } imported
               && ReferenceEquals(imported, RangeSlotGetter(property));

        /// <summary>The single overload of a named built-in range getter, or <see langword="null"/>.</summary>
        private static SurtrMethodInfo? RangeSlotGetter(string property)
            => SurtrBuiltIns.Range.TryGetMethods(MemberNames.Getter(property), out var overloads) && overloads.Length == 1
                ? overloads[0]
                : null;

        /// <summary>
        /// Lowers a read of the range's pure-slot members - <c>start</c>, <c>end</c>,
        /// <c>isInclusive</c> - to one sub-slot read off the receiver's block, and answers whether
        /// it did. Everything else on <c>range</c> keeps its native body.
        /// </summary>
        private bool TryRangeSlotRead(BoundPropertyExpression property)
        {
            var getter = property.Property.Getter;
            if (getter is null || property.IsVirtualGet || property.Receiver is null || getter.ExtensionTargetType is not null)
                return false;

            int offset;
            if (IsRangeSlotGetter(getter, "start"))
                offset = 0;
            else if (IsRangeSlotGetter(getter, "end"))
                offset = 1;
            else if (IsRangeSlotGetter(getter, "isInclusive"))
                offset = 2;
            else
                return false;

            int baseSlot = EnsureLocalRange(property.Receiver, RangeSlotWidth);
            Code.LoadLocalField(baseSlot, offset);
            return true;
        }

        /// <summary>
        /// The built-in members this emitter can lower to a dedicated opcode, keyed by their
        /// <c>SurtrMethodInfo</c> identity � the same set the <c>Is*Member</c> checks name. Built
        /// once, so a call site decides in one set lookup whether any of the <c>Try*</c> operations
        /// could apply.
        /// </summary>
        private static readonly Lazy<HashSet<SurtrMethodInfo>> OpcodeableMembers = new(() =>
        {
            var members = new HashSet<SurtrMethodInfo>();
            AddSingleOverloads(members, SurtrBuiltIns.Dictionary, "clear", "get", "set", "containsKey", "remove", "keys", "values");
            AddSingleOverloads(members, SurtrBuiltIns.Array, "get", "set", "push", "pop", "insert", "removeAt", "clear", "indexOf", "contains");
            AddSingleOverloads(members, SurtrBuiltIns.String, "charAt");
            return members;
        });

        private static void AddSingleOverloads(HashSet<SurtrMethodInfo> members, SurtrClass type, params string[] names)
        {
            foreach (var name in names)
            {
                if (type.TryGetMethods(name, out var overloads) && overloads.Length == 1)
                    members.Add(overloads[0]);
            }
        }

        /// <summary>
        /// Replaces a call to a <c>const fun</c> with constant arguments by the value it folds to
        /// (�7.2).
        /// </summary>
        /// <remarks>
        /// This is where �7.2's promise becomes observable: the callee still exists and can still be
        /// called at run time, but a call the compiler could answer does not survive into the
        /// bytecode. A fold that fails is not an error � the call is simply emitted.
        /// </remarks>
        private bool TryFoldConstCall(BoundCallExpression call, bool discardResult)
        {
            if (discardResult || !call.Method.IsConst || _context.Folder is null || call.Receiver is not null)
                return false;

            var arguments = new object?[call.Arguments.Count];
            for (int i = 0; i < arguments.Length; i++)
            {
                if (ConstantOf(call.Arguments[i]) is not object constant)
                    return false;

                arguments[i] = constant;
            }

            if (!_context.Folder.TryFold(call.Method, arguments, out object? value, out _))
                return false;

            switch (value)
            {
                case long or double or bool or char or string:
                    EmitLiteral(new BoundLiteralExpression(call.Syntax, call.Type, value));
                    return true;

                // An array or a tuple folded to a CLR array, and materialising one takes more than
                // a literal: it would have to be rebuilt element by element, and doing that here
                // would emit the allocation the call already performs. Not worth it.
                default:
                    return false;
            }
        }

        /// <summary>
        /// Whether the default heuristic � no <c>inline</c> written � still wants this body spliced
        /// (�3.6).
        /// </summary>
        /// <remarks>
        /// The cheap guards <see cref="TryInline"/> would apply are checked here too, so a body the
        /// splice could not reach is not walked for nothing; the guards stay authoritative and are
        /// re-checked there, since the cost walk can never be wrong in the permissive direction.
        /// </remarks>
        private bool ShouldInlineByCost(BoundCallExpression call)
        {
            if (call.IsVirtual || call.Method.IsNative || call.Method.Role == MethodRole.Constructor)
                return false;

            if (_context.Bodies is null || !_context.Bodies.TryGetValue(call.Method, out var body))
                return false;

            if (ReferenceEquals(call.Method, _symbol))
                return false;

            foreach (var frame in _inlines)
            {
                if (ReferenceEquals(frame.Method, call.Method))
                    return false;
            }

            return _context.InlineCostOf(body) <= InlineCost.DefaultThreshold;
        }

        /// <summary>
        /// Splices an <c>inline</c> call site (�3.6), if the body is available and it is safe to.
        /// </summary>
        /// <remarks>
        /// Arguments land in real slots first, so an argument written once is evaluated once
        /// whatever the body does with the parameter. A <c>return</c> inside the splice becomes a
        /// jump to the splice's own exit rather than a <c>Ret</c>, which is what makes inlining
        /// invisible to the caller.
        /// </remarks>
        private bool TryInline(BoundCallExpression call, bool discardResult)
        {
            // The authoritative `noinline` guard (�3.6): every path here goes through it, whatever
            // the hint or heuristic at the call site decided.
            if (call.Method.IsNoInline)
                return false;

            if (_context.Bodies is null || !_context.Bodies.TryGetValue(call.Method, out var body))
                return false;

            if (_inlines.Count >= MaxInlineDepth || call.IsVirtual)
                return false;

            // A constructor is never spliced: what runs is not its body alone but the chain and the
            // initializers the emitter prepends to it, so the splice would silently skip the base's
            // construction. A `super(...)` call names exactly such a body.
            if (call.Method.Role == MethodRole.Constructor)
                return false;

            // Nor is a generator, and for a sharper version of the same reason: what a call to one
            // runs is the stub, which builds an object and returns; the body this lookup finds is
            // the suspendable one, which belongs in a frame of its own. Splicing it would run the
            // generator's own statements in the caller's frame and put a `Yield` where nothing
            // resumed anything. §3.7 already refuses an `inline` generator at the declaration, but
            // the cost heuristic asks nothing about what was written, so the refusal has to live
            // here too.
            if (call.Method.IsGenerator)
                return false;

            // A body that splices itself, directly or through another inline function, would expand
            // forever � and its locals would collide, since a symbol maps to one slot.
            if (ReferenceEquals(call.Method, _symbol))
                return false;

            foreach (var frame in _inlines)
            {
                if (ReferenceEquals(frame.Method, call.Method))
                    return false;
            }

            SurtrLocal? receiver = null;
            if (call.Receiver is not null)
            {
                var slot = _method.HasReceiver ? DeclareTemp("$inlineThis", _symbol.ContainingType!) : _method.DeclareLocal("$inlineThis");
                Expression(call.Receiver);
                EmitStoreLocal(slot);
                receiver = slot;
            }

            for (int i = 0; i < call.Arguments.Count; i++)
            {
                var parameter = call.Method.Parameters[i];
                var slot = DeclareTemp("$inline$" + parameter.Name, parameter.Type);

                Expression(call.Arguments[i]);
                EmitStoreLocal(slot);
                _splicedParameters[parameter] = slot;
            }

            bool hasResult = !call.Method.ReturnType.IsVoid && !discardResult;

            // A body whose only statement is a single `return` never needs the exit-label/result-
            // local machinery below at all: there is no earlier `return` for a jump to skip past, so
            // the value can stay on the evaluation stack exactly as an ordinary expression's would,
            // instead of paying a store immediately followed by its own reload. The frame is still
            // pushed � with an unused exit/result, since nothing here ever reaches EmitReturn to read
            // them � purely so a call nested inside the value expression still sees this method as
            // "already being spliced" and refuses to splice it again (the cycle guard above).
            if (body is BoundBlockStatement { Statements: [BoundReturnStatement tailReturn] })
            {
                _inlines.Add(new InlineFrame(call.Method, default, default, false, receiver, _finallies.Count));

                if (tailReturn.Value is not null)
                {
                    if (hasResult)
                        Expression(tailReturn.Value);
                    else
                        EffectOnly(tailReturn.Value);
                }

                _inlines.RemoveAt(_inlines.Count - 1);
                return true;
            }

            var result = hasResult ? DeclareTemp("$inlineResult", _symbol.ReturnType) : default;
            var exit = Code.NewLabel();

            _inlines.Add(new InlineFrame(call.Method, exit, result, hasResult, receiver, _finallies.Count));
            Statement(body);
            _inlines.RemoveAt(_inlines.Count - 1);

            // Falling off the end of a spliced void body is an exit like any other, and one the
            // label has to be reachable from or the emitter would reject the join.
            if (Code.IsReachable)
                Code.Jump(exit);

            Code.MarkLabel(exit);

            if (hasResult)
                EmitLoadLocal(result);

            return true;
        }

        private void EmitClosureInvocation(BoundClosureInvocationExpression invocation, bool discardResult)
        {
            Expression(invocation.Callee);

            foreach (var argument in invocation.Arguments)
                Expression(argument);

            Code.CallClosure(invocation.Arguments.Count, hasResult: !invocation.Type.IsVoid && !discardResult);
        }

        /// <summary>
        /// Lifts a lambda to a static function of this module and builds a closure over it (�8).
        /// </summary>
        /// <remarks>
        /// Static, never an instance method on a synthesised class: <c>SurtrClosure</c> already
        /// copies the dispatch payload out flat, and the captures are the closure's upvalues, so a
        /// class would add a receiver with nothing to put in it. A lambda that reads the enclosing
        /// instance captures it like anything else, which is what
        /// <see cref="BoundLambdaExpression.CapturesReceiver"/> records.
        /// </remarks>
        private void EmitLambda(BoundLambdaExpression lambda)
        {
            // A direct method-group conversion is sugar for the function itself, so its value can
            // be built straight over the target method: no $lambda$ wrapper is lifted, an indirect
            // call through the value dispatches the target directly instead of a synthetic forwarder
            // that would pay a second frame and a second dispatch, and the value is the canonical
            // one every site resolving to that method shares. The wrapper path remains the fallback
            // when the target is not a method of this module (its builder is not reachable here).
            if (lambda.DirectTarget is { } direct && _context.TryGetBuilder(direct, out var targetBuilder))
            {
                Code.NewFunctionFor(targetBuilder);
                return;
            }

            var closure = (ClosureTypeSymbol)lambda.Type.NonNullable;

            var parameters = new SurtrParameterInfo[lambda.Parameters.Count];
            for (int i = 0; i < parameters.Length; i++)
                parameters[i] = _context.Module.Parameter(lambda.Parameters[i].Name, Descriptors.Emit(lambda.Parameters[i].Type));

            string name = SyntheticNames.Lambda(
                _symbol.Name,
                _context.NextSyntheticIndex(SyntheticNames.LambdaCategory, _symbol.Name));

            var lifted = _context.Module.DefineFunction(
                name,
                Descriptors.Emit(closure.ReturnType),
                parameters,
                SurtrVisibility.Private);

            var captures = new Dictionary<Symbol, int>();
            int next = 0;

            if (lambda.CapturesReceiver)
                next++;

            foreach (var captured in lambda.Captured)
            {
                // An upvalue is one slot; an inline value is several. Capturing one would need
                // the packed form at the capture point and an unpack on every read inside - a
                // design of its own, deferred rather than approximated.
                if (IsInlineCaptured(captured))
                    throw Unsupported($"a lambda capturing '{captured.Name}', whose type stores inline as several slots");

                captures[captured] = next++;
            }

            // The lifted body is emitted now rather than queued: it has its own builder and its own
            // instruction stream, so nothing about the caller's is disturbed by recursing.
            new MethodBodyEmitter(
                lifted,
                LambdaSymbol(lambda, closure),
                _context,
                captures,
                lambda.CapturesReceiver ? 0 : (int?)null)
                .Emit(lambda.Body);

            if (lambda.CapturesReceiver)
                LoadReceiver();

            foreach (var captured in lambda.Captured)
                LoadCaptured(captured);

            int captureCount = captures.Count + (lambda.CapturesReceiver ? 1 : 0);

            // A lambda that captures nothing is a pure function: emitting the canonical function
            // value (NewFunction) hands back the one shared closure for the lifted method instead
            // of allocating a fresh object per evaluation. The value is still a closure of the
            // same signature, so it coexists with capturing lambdas under the same type.
            if (captureCount == 0)
                Code.NewFunctionFor(lifted);
            else
                Code.NewClosureFor(lifted, captureCount);
        }

        /// <summary>The symbol a lifted body is emitted against, for its parameters and its name.</summary>
        private MethodSymbol LambdaSymbol(BoundLambdaExpression lambda, ClosureTypeSymbol closure)
        {
            var symbol = new MethodSymbol(_symbol.Name, _symbol.ContainingSymbol!, closure.ReturnType)
            {
                Parameters = lambda.Parameters,
            };

            return symbol;
        }

        /// <summary>Pushes something the enclosing body owns, so a closure can capture it.</summary>
        private void LoadCaptured(Symbol captured)
        {
            switch (captured)
            {
                case LocalSymbol local:
                    LoadSymbol(local, () => EmitLoadLocal(Slot(local)));
                    return;

                case ParameterSymbol parameter:
                    LoadSymbol(parameter, () => EmitLoadLocal(ParameterSlot(parameter)));
                    return;

                default:
                    throw Unsupported($"a lambda capturing {captured.GetType().Name}");
            }
        }

        /// <summary>Whether a captured symbol's type stores inline as more than one slot.</summary>
        private static bool IsInlineCaptured(Symbol captured)
            => captured switch
            {
                LocalSymbol local => ValueTypeLayout.IsInlineType(local.Type, out _),
                ParameterSymbol parameter => ValueTypeLayout.IsInlineType(parameter.Type, out _),
                _ => false,
            };

        private void EmitIndexRead(BoundIndexExpression index)
        {
            var target = index.Target.Type.NonNullable;

            // �5.3 makes a tuple index a constant � the binder folds it and hands back a literal �
            // so it belongs in the instruction rather than on the stack. The tuple itself is a
            // block now: spill its base into a frame range (free when it already lives in one)
            // and read the element at its own flattened offset, exactly as a value-class field
            // read does.
            if (target is TupleTypeSymbol constantTuple && index.Index is BoundLiteralExpression { Value: long ordinal })
            {
                if (!ValueTypeLayout.IsInlineType(constantTuple, out int tupleWidth) || ordinal < 0 || ordinal >= constantTuple.ElementTypes.Count)
                    throw Unsupported($"an index {ordinal} into '{constantTuple.ToDisplayString()}'");

                int baseSlot = EnsureLocalRange(index.Target, tupleWidth);

                int offset = 0;
                for (int i = 0; i < ordinal; i++)
                    offset += ValueTypeLayout.WidthOfType(constantTuple.ElementTypes[i]);

                var elementType = constantTuple.ElementTypes[(int)ordinal];
                if (TryMultiSlotWidth(elementType, out int elementWidth))
                    Code.LoadValueLocal(baseSlot + offset, elementWidth);
                else
                    Code.LoadLocalField(baseSlot, offset);

                BoxIfStillErased(index.Type);
                return;
            }

            Expression(index.Target);
            Expression(index.Index);
            // A key of an inline type reaches the collection packed, mirroring the write side.
            BoxIfMultiSlot(index.Index.Type);

            switch (target.TypeKind)
            {
                case TypeSymbolKind.Array:
                    Code.ArrGet();
                    UnpackIfMultiSlot(index.Type);
                    BoxIfStillErased(index.Type);
                    return;
                case TypeSymbolKind.Dictionary:
                    Code.DictGet();
                    UnpackIfMultiSlot(index.Type);
                    BoxIfStillErased(index.Type);
                    return;
                case TypeSymbolKind.Tuple:
                    Code.TupGet();
                    BoxIfStillErased(index.Type);
                    return;
            }

            if (target.SpecialType == SpecialType.String)
            {
                Code.StrGet();
                return;
            }

            throw Unsupported($"indexing '{index.Target.Type.ToDisplayString()}'");
        }

        /// <summary>
        /// Boxes a value read straight off a collection's native storage when it is still typed by
        /// the declaring generic's own bare type parameter � <c>self[i]</c> off a <c>T[]</c> inside
        /// the body that declares <c>T</c>, say.
        /// </summary>
        /// <remarks>
        /// <c>ArrGet</c>/<c>DictGet</c>/<c>TupGet</c>/<c>TupleElement</c> read the collection's
        /// storage directly (�3.5's "no per-element type tags"), which is the right raw value once
        /// <c>T</c> is substituted to a concrete type � an <c>int[]</c>'s own indexer needs no box,
        /// which is exactly what <see cref="EmitCollectionOperand"/> restores on the write side. But
        /// while <c>T</c> is still the declaring generic's own bare parameter, this body is compiled
        /// once for every <c>T</c>, so a value leaving through it has to become a reference the same
        /// way one reaching a generic parameter does on the way in (<c>ConversionTarget</c> in
        /// <c>BodyBinder.Expressions.cs</c>) � except the compiler has no concrete type to pick
        /// <c>BoxInt</c> from <c>BoxFloat</c> with here, since the collection this <c>T[]</c> names
        /// might be a concretely-typed one flowing in from a call site (raw storage) or one built
        /// from scratch inside this very generic body (already-boxed storage, �1.11). <see
        /// cref="Surtr.Bytecode.OpCode.BoxDynamic"/> is exactly the opcode for that: it reads the value's own tag
        /// instead of a static type, and is a no-op when the value is already a reference. The read
        /// and the later <c>Unerase</c> a caller applies (<see cref="UnerasedCallResult"/>, the loop
        /// variable in <see cref="EmitForInIterable"/>) are the two ends of the same erased slot.
        /// </remarks>
        private void BoxIfStillErased(TypeSymbol type)
        {
            if (type.NonNullable is TypeParameterSymbol)
                Code.BoxDynamic();
        }

        /// <summary>
        /// Unboxes a value bound for a collection's native storage when it is still typed by the
        /// declaring generic's own bare type parameter, the mirror of <see cref="BoxIfStillErased"/>.
        /// </summary>
        /// <remarks>
        /// A <c>T</c>-typed value at rest inside a still-generic body is always boxed - it arrived
        /// that way across an erasure boundary (an argument, a field read) and nothing along the way
        /// had reason to undo it. But the array/dict/tuple storage it is about to be written into was
        /// never boxed to begin with, regardless of whether the collection's own compile-time element
        /// type is concrete or still abstract (�3.5's "no per-element type tags" is a property of the
        /// storage, not of how erased the accessing body happens to be). <see
        /// cref="Surtr.Bytecode.OpCode.UnboxDynamic"/> is a no-op for anything that is not a boxed primitive, so this
        /// is safe to call whenever the static type says <c>T</c>, without knowing in advance whether
        /// the value on the stack actually needs it.
        /// </remarks>
        private void UnboxIfStillErased(TypeSymbol type)
        {
            if (type.NonNullable is TypeParameterSymbol)
                Code.UnboxDynamic();
        }

        private void EmitArrayLiteral(BoundArrayLiteralExpression array)
        {
            foreach (var element in array.Elements)
            {
                Expression(element);
                UnboxIfStillErased(element.Type);
                // An element storing inline crosses into one-reference storage: it packs.
                BoxIfMultiSlot(element.Type);
            }

            // One immediate carries both the descriptor the object keeps and the element family its
            // slots are initialised from, so an empty literal still knows what it is.
            Code.PackArray(Descriptors.Emit(array.Type.NonNullable), array.Elements.Count);
        }

        /// <summary>
        /// Builds a tuple: every element evaluates straight onto the operand stack, in order,
        /// leaving exactly the block one tuple value occupies.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Nothing is packed here any more. A tuple is a value type (�5.5 as lowered): its
        /// elements <em>are</em> the value's slots, so the literal is the block - the same shape
        /// every multi-field construction already leaves behind. Where the value meets a slot that
        /// holds one reference - an array element, a dict key, an erased parameter - the boundary
        /// boxes it (<see cref="BoxIfTuple"/>), which is what keeps <see cref="Surtr.Runtime.Objects.SurtrTuple"/>
        /// alive as the boxed form.
        /// </para>
        /// </remarks>
        private void EmitTupleLiteral(BoundTupleLiteralExpression tuple)
        {
            // The empty tuple has no block to leave behind - its width is the one slot of the
            // boxed form - so it packs exactly as it always did.
            if (tuple.Elements.Count == 0)
            {
                Code.PackTuple(Descriptors.Emit(tuple.Type.NonNullable), 0);
                return;
            }

            foreach (var element in tuple.Elements)
            {
                Expression(element);
                UnboxIfStillErased(element.Type);
            }
        }

        private void EmitDictLiteral(BoundDictLiteralExpression dictionary)
        {
            foreach (var entry in dictionary.Entries)
            {
                Expression(entry.Key);
                UnboxIfStillErased(entry.Key.Type);
                BoxIfMultiSlot(entry.Key.Type);
                Expression(entry.Value);
                UnboxIfStillErased(entry.Value.Type);
                BoxIfMultiSlot(entry.Value.Type);
            }

            Code.PackDictionary(Descriptors.Emit(dictionary.Type.NonNullable), dictionary.Entries.Count);
        }

        /// <summary>
        /// Emits a construction of <c>array</c>, <c>dict</c> or <c>tuple</c> through their nameable
        /// generic form (�5.3). Every shape folds to the same allocation opcodes the equivalent
        /// literal already uses � never <c>ObjNew</c> � plus at most one native call
        /// (<see cref="BoundCollectionCreationExpression.ReserveMethod"/>, the one shape that has no
        /// single-opcode fold available).
        /// </summary>
        private void EmitCollectionCreation(BoundCollectionCreationExpression creation)
        {
            var type = Descriptors.Emit(creation.Type.NonNullable);

            switch (creation.Kind)
            {
                case CollectionCreationKind.ArrayEmpty:
                    // Identical to what an empty `[]` literal already emits (EmitArrayLiteral) �
                    // routed through the same ArrPack rather than re-derived.
                    Code.PackArray(type, 0);
                    return;

                case CollectionCreationKind.TupleEmpty:
                    Code.PackTuple(type, 0);
                    return;

                case CollectionCreationKind.DictEmpty:
                    Code.NewDictionary(type);
                    return;

                case CollectionCreationKind.ArrayCapacity:
                    EmitArrayCapacity(creation, type);
                    return;

                case CollectionCreationKind.DictCapacity:
                    // DictNew has no capacity operand, so this is the one shape that does not fold
                    // to a single opcode: allocate empty, then dup + call dict's own existing
                    // `reserve` � the same "dup, call a void-returning instance method, keep one
                    // copy" idiom EmitObjectCreation already uses for a synthesized default
                    // constructor.
                    Code.NewDictionary(type);
                    Code.Dup();
                    Expression(creation.Capacity!);
                    EmitResolvedCall(creation.ReserveMethod!, virtualCall: false, discardResult: true);
                    return;

                case CollectionCreationKind.ArrayFromTuple:
                    EmitArrayFromTuple(creation, type);
                    return;

                case CollectionCreationKind.TupleFromArray:
                    EmitTupleFromArray(creation, type);
                    return;

                case CollectionCreationKind.ArraySizeDefault:
                    EmitArraySizeDefault(creation, type);
                    return;

                case CollectionCreationKind.ArrayCopy:
                    EmitArrayCopy(creation, type);
                    return;

                case CollectionCreationKind.ArrayFromIterable:
                    EmitArrayFromIterable(creation, type);
                    return;

                case CollectionCreationKind.DictFromPairs:
                    EmitDictFromPairs(creation, type);
                    return;

                case CollectionCreationKind.DictFromParallelArrays:
                    EmitDictFromParallelArrays(creation, type);
                    return;

                default:
                    throw Unsupported($"a {creation.Kind} collection construction");
            }
        }

        private void EmitArrayCapacity(BoundCollectionCreationExpression creation, SurtrClassReference type)
        {
            // A written literal folds straight to ArrNewX � the addressing mode Opcodes.md already
            // documents for exactly this, "for arrays of statically known size" � with zero runtime
            // work; anything else pushes the runtime value and falls back to the stack-popping ArrNew.
            if (creation.Capacity is BoundLiteralExpression { Value: long constant }
                && constant >= 0
                && constant <= int.MaxValue)
            {
                Code.NewArray(type, (int)constant);
                return;
            }

            Expression(creation.Capacity!);
            Code.NewArray(type);
        }

        private void EmitArrayFromTuple(BoundCollectionCreationExpression creation, SurtrClassReference type)
        {
            var tupleType = (TupleTypeSymbol)creation.Source!.Type.NonNullable;
            var elementType = ((ArrayTypeSymbol)creation.Type.NonNullable).ElementType;
            var conversions = creation.ElementConversions!;

            // The source is a block; spill it into a range so each element can be read at its
            // own flattened offset.
            int slot = EnsureLocalRange(creation.Source, ValueTypeLayout.WidthOfType(tupleType));

            // Element 0 first, ..., element N-1 last: ArrPack pops in the same order EmitArrayLiteral
            // already pushes for a written literal, "the deepest popped value becomes element 0."
            for (int i = 0; i < conversions.Count; i++)
            {
                int offset = 0;
                for (int e = 0; e < i; e++)
                    offset += ValueTypeLayout.WidthOfType(tupleType.ElementTypes[e]);

                var sourceElement = tupleType.ElementTypes[i];
                if (TryMultiSlotWidth(sourceElement, out int elementWidth))
                    Code.LoadValueLocal(slot + offset, elementWidth);
                else
                    Code.LoadLocalField(slot, offset);

                EmitConversionTail(conversions[i], sourceElement, elementType);
                BoxIfMultiSlot(elementType);
            }

            Code.PackArray(type, conversions.Count);
        }

        private void EmitTupleFromArray(BoundCollectionCreationExpression creation, SurtrClassReference type)
        {
            var tupleType = (TupleTypeSymbol)creation.Type.NonNullable;
            var conversions = creation.ElementConversions!;

            var slot = _method.DeclareLocal("$collect");
            Expression(creation.Source!);
            EmitStoreLocal(slot);

            // The library not declaring InvalidCastException is treated the same way EmitNullAssert
            // treats a missing NullReferenceException: the check is skipped rather than left with
            // nothing to throw, so a mismatched length falls through unchecked instead of failing to
            // compile.
            if (creation.Thrown is not null)
            {
                var ok = Code.NewLabel();

                EmitLoadLocal(slot);
                Code.ArrLen();
                Code.LoadInt(conversions.Count);
                Code.JumpIfCompare(SurtrComparison.Equal, SurtrValueTypeCode.Integer, ok);

                Expression(creation.Thrown);
                Code.Throw();
                Code.MarkLabel(ok);
            }

            for (int i = 0; i < conversions.Count; i++)
            {
                EmitLoadLocal(slot);
                Code.LoadInt(i);
                Code.ArrGet();

                var targetElement = tupleType.ElementTypes[i];

                // The array's own storage keeps one reference per element - an inline value sits
                // there boxed - so the slot this element will occupy in the block gets the value
                // itself.
                UnpackIfMultiSlot(targetElement);

                EmitConversionTail(conversions[i], ((ArrayTypeSymbol)creation.Source!.Type.NonNullable).ElementType, targetElement);
            }

            // No pack: the elements just pushed ARE the resulting value's block.
        }

        /// <summary>
        /// <c>array&lt;T&gt;(size, defaultValue)</c> for a non-zero (or non-constant) default � the
        /// zero-value case never reaches here, folded onto <see cref="EmitArrayCapacity"/> instead
        /// (which already zero-fills) back in the binder. Every loop method below follows the same
        /// hand-rolled counted-loop idiom <c>EmitForInIndexed</c>/<c>EmitForInRange</c> already use �
        /// no shared "emit a counted loop" helper exists anywhere in this emitter, and none of these
        /// synthesized loops has a user-visible body to give <c>break</c>/<c>continue</c> targets to,
        /// so none of them call <c>PushLoop</c>/<c>PopTargets</c> either.
        /// </summary>
        private void EmitArraySizeDefault(BoundCollectionCreationExpression creation, SurtrClassReference type)
        {
            var arraySlot = _method.DeclareLocal("$collect");
            var defaultSlot = _method.DeclareLocal("$default");
            var indexSlot = _method.DeclareLocal("$index");

            // Zero-filled by ArrNew/ArrNewX already; the loop below overwrites every slot with the
            // real default, evaluated once up front rather than once per index.
            EmitArrayCapacity(creation, type);
            EmitStoreLocal(arraySlot);

            Expression(creation.DefaultValue!);
            EmitStoreLocal(defaultSlot);

            Code.LoadInt(0);
            EmitStoreLocal(indexSlot);

            var top = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            EmitLoadLocal(indexSlot);
            EmitLoadLocal(arraySlot);
            Code.ArrLen();
            Code.JumpIfCompare(SurtrComparison.GreaterOrEqual, SurtrValueTypeCode.Integer, end);

            EmitLoadLocal(arraySlot);
            EmitLoadLocal(indexSlot);
            EmitLoadLocal(defaultSlot);
            Code.ArrSet();

            Code.IncrementLocal(indexSlot, 1);
            Code.Jump(top);
            Code.MarkLabel(end);
            EmitLoadLocal(arraySlot);
        }

        /// <summary>
        /// <c>array&lt;T&gt;(anotherArray)</c> � checked ahead of <see cref="EmitArrayFromIterable"/>
        /// in the binder precisely so this faster, non-interface-dispatch path is what an array
        /// argument actually takes: one <c>ArrLen</c>, one runtime <c>ArrNew</c>, then indexed
        /// <c>ArrGet</c>/<c>ArrSet</c> � never <c>CallInterface</c>.
        /// </summary>
        private void EmitArrayCopy(BoundCollectionCreationExpression creation, SurtrClassReference type)
        {
            var elementType = ((ArrayTypeSymbol)creation.Type.NonNullable).ElementType;
            var sourceElementType = ((ArrayTypeSymbol)creation.Source!.Type.NonNullable).ElementType;
            var conversion = creation.ElementConversions![0];

            var sourceSlot = _method.DeclareLocal("$collect");
            var destSlot = _method.DeclareLocal("$collectDest");
            var indexSlot = _method.DeclareLocal("$index");

            Expression(creation.Source);
            EmitStoreLocal(sourceSlot);

            EmitLoadLocal(sourceSlot);
            Code.ArrLen();
            Code.NewArray(type);
            EmitStoreLocal(destSlot);

            Code.LoadInt(0);
            EmitStoreLocal(indexSlot);

            var top = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            EmitLoadLocal(indexSlot);
            EmitLoadLocal(sourceSlot);
            Code.ArrLen();
            Code.JumpIfCompare(SurtrComparison.GreaterOrEqual, SurtrValueTypeCode.Integer, end);

            EmitLoadLocal(destSlot);
            EmitLoadLocal(indexSlot);
            EmitLoadLocal(sourceSlot);
            EmitLoadLocal(indexSlot);
            Code.ArrGet();
            EmitConversionTail(conversion, sourceElementType, elementType);
            Code.ArrSet();

            Code.IncrementLocal(indexSlot, 1);
            Code.Jump(top);
            Code.MarkLabel(end);
            EmitLoadLocal(destSlot);
        }

        /// <summary>
        /// <c>array&lt;T&gt;(anIterable)</c> � the lowest-priority, general-purpose shape, walking
        /// the source through <c>IIterable&lt;T&gt;</c> exactly as <c>EmitForInIterable</c> already
        /// does (interface dispatch on <c>iterate</c>/<c>moveNext</c>/<c>current</c>, the same
        /// <c>Unerase</c> rule for a reference element), pushing each result rather than storing to a
        /// loop variable. The destination starts empty and growable since the source's length is not
        /// known ahead of time for a general iterable.
        /// </summary>
        private void EmitArrayFromIterable(BoundCollectionCreationExpression creation, SurtrClassReference type)
        {
            var elementType = ((ArrayTypeSymbol)creation.Type.NonNullable).ElementType;
            var sourceElementType = creation.SourceElementType!;
            var conversion = creation.ElementConversions![0];

            var iterate = ContractMethod(SurtrBuiltIns.IIterable, "iterate");
            var moveNext = ContractMethod(SurtrBuiltIns.IIterator, "moveNext");
            var current = ContractMethod(SurtrBuiltIns.IIterator, MemberNames.Getter("current"));

            var destSlot = _method.DeclareLocal("$collectDest");
            var cursorSlot = _method.DeclareLocal("$iterator");

            Code.PackArray(type, 0);
            EmitStoreLocal(destSlot);

            Expression(creation.Source!);
            // An inline source (a range, a value class) has to reach the interface dispatch as
            // the object its class carries IIterable on - a block cannot be dispatched through.
            BoxIfMultiSlot(creation.Source!.Type);
            Code.CallInterface(iterate);
            EmitStoreLocal(cursorSlot);

            var top = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            EmitLoadLocal(cursorSlot);
            Code.CallInterface(moveNext);
            Code.JumpIfFalse(end);

            EmitLoadLocal(destSlot);
            EmitLoadLocal(cursorSlot);
            Code.CallInterface(current);

            // Same normalization `EmitForInIterable` needs: `current` reads back erased, but
            // whether the receiver already boxed it or is a built-in handing back raw storage is
            // not something the contract call site can tell � `BoxDynamic` decides from the value's
            // own tag, a no-op if it was already a reference, and `Unerase` can then run
            // unconditionally.
            Code.BoxDynamic();
            Unerase(sourceElementType);

            EmitConversionTail(conversion, sourceElementType, elementType);
            Code.ArrPush();

            Code.Jump(top);
            Code.MarkLabel(end);
            EmitLoadLocal(destSlot);
        }

        /// <summary>
        /// <c>{K:V}(pairs)</c> � the pair read twice (once per slot) through a temp local, the same
        /// "evaluate once, use more than once" idiom every other cast/copy shape here already uses,
        /// rather than juggling a duplicate mid-stack.
        /// </summary>
        private void EmitDictFromPairs(BoundCollectionCreationExpression creation, SurtrClassReference type)
        {
            var dictType = (DictionaryTypeSymbol)creation.Type.NonNullable;
            var pairType = (TupleTypeSymbol)((ArrayTypeSymbol)creation.Source!.Type.NonNullable).ElementType;
            var conversions = creation.ElementConversions!;

            var sourceSlot = _method.DeclareLocal("$collect");
            var dictSlot = _method.DeclareLocal("$collectDict");
            var indexSlot = _method.DeclareLocal("$index");
            var pairSlot = _method.DeclareLocal("$pair");

            Expression(creation.Source);
            EmitStoreLocal(sourceSlot);

            Code.NewDictionary(type);
            EmitStoreLocal(dictSlot);

            Code.LoadInt(0);
            EmitStoreLocal(indexSlot);

            var top = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            EmitLoadLocal(indexSlot);
            EmitLoadLocal(sourceSlot);
            Code.ArrLen();
            Code.JumpIfCompare(SurtrComparison.GreaterOrEqual, SurtrValueTypeCode.Integer, end);

            EmitLoadLocal(sourceSlot);
            EmitLoadLocal(indexSlot);
            Code.ArrGet();
            EmitStoreLocal(pairSlot);

            EmitLoadLocal(dictSlot);
            EmitLoadLocal(pairSlot);
            Code.TupleElement(0);
            EmitConversionTail(conversions[0], pairType.ElementTypes[0], dictType.KeyType);
            EmitLoadLocal(pairSlot);
            Code.TupleElement(1);
            EmitConversionTail(conversions[1], pairType.ElementTypes[1], dictType.ValueType);
            Code.DictSet();

            Code.IncrementLocal(indexSlot, 1);
            Code.Jump(top);
            Code.MarkLabel(end);
            EmitLoadLocal(dictSlot);
        }

        /// <summary>
        /// <c>{K:V}(keys, values)</c> � no element conversions: the binder only takes this path on an
        /// exact element-type match (arrays are invariant, �6), so nothing here needs
        /// <see cref="EmitConversionTail"/> the way every other cast/copy/pairs shape does.
        /// </summary>
        private void EmitDictFromParallelArrays(BoundCollectionCreationExpression creation, SurtrClassReference type)
        {
            var keysSlot = _method.DeclareLocal("$collect");
            var valuesSlot = _method.DeclareLocal("$collect2");
            var dictSlot = _method.DeclareLocal("$collectDict");
            var indexSlot = _method.DeclareLocal("$index");

            Expression(creation.Source!);
            EmitStoreLocal(keysSlot);
            Expression(creation.Source2!);
            EmitStoreLocal(valuesSlot);

            // Same "skip the check if the library doesn't declare the exception" idiom EmitNullAssert
            // and EmitTupleFromArray already established.
            if (creation.Thrown is not null)
            {
                var ok = Code.NewLabel();

                EmitLoadLocal(keysSlot);
                Code.ArrLen();
                EmitLoadLocal(valuesSlot);
                Code.ArrLen();
                Code.JumpIfCompare(SurtrComparison.Equal, SurtrValueTypeCode.Integer, ok);

                Expression(creation.Thrown);
                Code.Throw();
                Code.MarkLabel(ok);
            }

            Code.NewDictionary(type);
            EmitStoreLocal(dictSlot);

            Code.LoadInt(0);
            EmitStoreLocal(indexSlot);

            var top = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            EmitLoadLocal(indexSlot);
            EmitLoadLocal(keysSlot);
            Code.ArrLen();
            Code.JumpIfCompare(SurtrComparison.GreaterOrEqual, SurtrValueTypeCode.Integer, end);

            EmitLoadLocal(dictSlot);
            EmitLoadLocal(keysSlot);
            EmitLoadLocal(indexSlot);
            Code.ArrGet();
            EmitLoadLocal(valuesSlot);
            EmitLoadLocal(indexSlot);
            Code.ArrGet();
            Code.DictSet();

            Code.IncrementLocal(indexSlot, 1);
            Code.Jump(top);
            Code.MarkLabel(end);
            EmitLoadLocal(dictSlot);
        }

        /// <summary>
        /// Emits an interpolated string as one concatenation over all of its parts.
        /// </summary>
        /// <remarks>
        /// Every primitive already declares a native <c>toString</c>, so a non-string part is a call
        /// to that rather than a new opcode � which also means interpolation means exactly what
        /// writing <c>.toString()</c> means.
        /// <para>
        /// One <c>StrCat</c> over n parts rather than n - 1 over two each: joined pairwise, every
        /// intermediate result is a string nothing reads and the leading part is copied once per
        /// hole. The counted form allocates the answer and fills it.
        /// </para>
        /// </remarks>
        private void EmitInterpolatedString(BoundInterpolatedStringExpression interpolated)
        {
            if (interpolated.Parts.Count == 0)
            {
                Code.LoadString(string.Empty);
                return;
            }

            int pending = 0;

            for (int i = 0; i < interpolated.Parts.Count; i++)
            {
                EmitAsString(interpolated.Parts[i]);

                // The count is one byte, so an interpolation with more parts than that folds what
                // it has so far and carries the result in as the next group's first operand.
                if (++pending == MaxConcatOperands)
                {
                    Code.StrCat(MaxConcatOperands);
                    pending = 1;
                }
            }

            if (pending > 1)
                Code.StrCat(pending);
        }

        private void EmitAsString(BoundExpression part)
        {
            Expression(part);

            var typeCode = TypeCodeOf(part.Type);
            if (typeCode == SurtrValueTypeCode.String)
                return;

            var owner = typeCode switch
            {
                SurtrValueTypeCode.Integer => SurtrBuiltIns.Integer,
                SurtrValueTypeCode.Float => SurtrBuiltIns.Float,
                SurtrValueTypeCode.Boolean => SurtrBuiltIns.Boolean,
                SurtrValueTypeCode.Character => SurtrBuiltIns.Character,
                _ => throw Unsupported($"interpolating a '{part.Type.ToDisplayString()}'"),
            };

            if (!owner.TryGetMethods("toString", out var overloads) || overloads.Length != 1)
                throw Unsupported($"interpolating a '{part.Type.ToDisplayString()}', whose toString could not be found");

            Code.Call(overloads[0]);
        }

        /// <summary>
        /// Emits a switch expression, which unlike the statement form always produces a value.
        /// </summary>
        private void EmitSwitchExpression(BoundSwitchExpression @switch)
        {
            var subject = _method.DeclareLocal("$subject");

            Expression(@switch.Subject);
            EmitStoreLocal(subject);

            var end = Code.NewLabel();
            var arms = new SurtrLabel[@switch.Arms.Count];
            var labels = new List<BoundExpression>[@switch.Arms.Count];
            SurtrLabel? fallback = null;

            for (int i = 0; i < arms.Length; i++)
            {
                arms[i] = Code.NewLabel();
                labels[i] = new List<BoundExpression>(@switch.Arms[i].Values);

                if (@switch.Arms[i].IsDefault)
                    fallback = arms[i];
            }

            // With no `else`, the binder has already established that the arms cover every case of a
            // non-nullable enum (�4.3), so the last arm is what is left over once the others have
            // been tested � and testing it as well would be comparing against the only value the
            // subject can still be. This is the whole point of checking exhaustiveness: the form
            // that needs no fallback is the form the check exists to allow.
            if (fallback is null)
            {
                if (arms.Length == 0)
                    throw Unsupported("a switch expression with no arms");

                fallback = arms[arms.Length - 1];
                labels[arms.Length - 1].Clear();
            }

            EmitDispatch(@switch.Subject.Type, subject, arms, labels, fallback.Value);

            // Every arm produces one value, so they all have to leave the stack at the same depth �
            // which is exactly what the emitter checks when the label joins them.
            var result = DeclareTemp("$switchResult", @switch.Arms[0].Result.Type);

            for (int i = 0; i < arms.Length; i++)
            {
                Code.MarkLabel(arms[i]);
                Expression(@switch.Arms[i].Result);
                EmitStoreLocal(result);
                Code.Jump(end);
            }

            Code.MarkLabel(end);
            EmitLoadLocal(result);
        }
        #endregion

        #region Frame, captures and types

        /// <summary>Slot width per frame index the emitter itself claimed: parameters first, then temps.</summary>
        private readonly Dictionary<int, int> _slotWidthsByIndex = new();
        private bool _frameWidthsRegistered;

        /// <summary>
        /// Loads a local range onto the operand stack - one slot for everything ordinary, a
        /// contiguous block for a value-type variable.
        /// </summary>
        /// <summary>
        /// Emits a generator's stub: the whole of what calling a generator function does (§3.7).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The stub is why a call to a generator is an ordinary call. It has the generator's own
        /// name, parameters and declared return - <c>generator&lt;T&gt;</c> - and its body pushes
        /// its arguments, builds the object and returns it, without running a line of what the
        /// source wrote. That lives in a hidden second method, which is what
        /// <paramref name="body"/> names.
        /// </para>
        /// <para>
        /// Doing it this way rather than emitting <c>GenNew</c> at each call site is what keeps
        /// generators out of the metadata: the caller needs no flag saying "this one is special",
        /// because the stub's declared return already says what it hands back - and it is what
        /// lets a generator be <c>virtual</c> or satisfy a contract, since the stub dispatches like
        /// any other method.
        /// </para>
        /// </remarks>
        public void EmitGeneratorFactory(SurtrMethodToken body)
        {
            EnsureFrameWidths();

            // The generator's own type, not the element's: the object keeps it, and a host or a
            // diagnostic reading `YI` back gets `generator<int>` rather than a bare family symbol.
            var generatorType = _context.Module.Type(Descriptors.Emit(_symbol.ReturnType));

            int slots = 0;

            if (!_symbol.IsStatic)
            {
                EmitLoadLocal(_method.Receiver);
                slots += WidthOf(_method.Receiver);
            }

            for (int i = 0; i < _symbol.Parameters.Count; i++)
            {
                var slot = _method.Parameter(i);
                EmitLoadLocal(slot);
                slots += WidthOf(slot);
            }

            if (slots > byte.MaxValue)
                throw Unsupported($"a generator taking {slots} argument slots, which is more than one byte can count");

            Code.GenNew(body, generatorType, slots);
            Code.ReturnValue();
        }

        private int WidthOf(SurtrLocal local)
            => _slotWidthsByIndex.TryGetValue(local.Index, out int width) && width > 1 ? width : 1;

        private void EmitLoadLocal(SurtrLocal local)
        {
            EnsureFrameWidths();

            if (_slotWidthsByIndex.TryGetValue(local.Index, out int width) && width > 1)
                Code.LoadValueLocal(local.Index, width);
            else
                Code.LoadLocal(local);
        }

        /// <summary>Pops whatever a local range holds back off the operand stack.</summary>
        private void EmitStoreLocal(SurtrLocal local)
        {
            EnsureFrameWidths();

            if (_slotWidthsByIndex.TryGetValue(local.Index, out int width) && width > 1)
                Code.StoreValueLocal(local.Index, width);
            else
                Code.StoreLocal(local);
        }

        /// <summary>
        /// Registers how wide every argument slot block is, once: the receiver of an instance
        /// method on a multi-field value class occupies that value's whole width, and every
        /// parameter occupies its own type's.
        /// </summary>
        private void EnsureFrameWidths()
        {
            if (_frameWidthsRegistered)
                return;

            _frameWidthsRegistered = true;

            if (_method.HasReceiver && _symbol.ContainingType is NamedTypeSymbol receiverType && ValueTypeLayout.WidthOfType(receiverType) > 1)
            {
                if (!ValueTypeLayout.TryGet(receiverType, out var receiverLayout, out var receiverError))
                    throw Unsupported(receiverError!);

                _slotWidthsByIndex[0] = receiverLayout.Width;
            }

            foreach (var parameter in _symbol.Parameters)
            {
                var slot = ParameterSlot(parameter);
                int width = SlotCountOfType(parameter.Type);
                if (width > 1)
                    _slotWidthsByIndex[slot.Index] = width;
            }
        }

        /// <summary>How many slots one value of this type occupies inline: its flattened width when it is an inline type (multi-field value class or tuple), one otherwise.</summary>
        private static int SlotCountOfType(TypeSymbol type) => ValueTypeLayout.WidthOfType(type);

        /// <summary>Claims a temporary sized to hold a value of this type.</summary>
        private SurtrLocal DeclareTemp(string name, TypeSymbol type)
        {
            int width = SlotCountOfType(type);
            var slot = width == 1 ? _method.DeclareLocal(name) : _method.DeclareLocals(name, width);

            if (width > 1)
                _slotWidthsByIndex[slot.Index] = width;

            return slot;
        }

        /// <summary>The base slot of a value-typed expression's storage, spilling it to a fresh temp when it does not already live in one.</summary>
        private int EnsureLocalRange(BoundExpression expression, int width)
        {
            switch (expression)
            {
                case BoundLocalExpression local:
                    return Slot(local.Local).Index;

                case BoundParameterExpression parameter:
                    return ParameterSlot(parameter.Parameter).Index;

                default:
                {
                    var spilled = _method.DeclareLocals("$vt", width);
                    _slotWidthsByIndex[spilled.Index] = width;
                    Expression(expression);
                    EmitStoreLocal(spilled);
                    return spilled.Index;
                }
            }
        }

        private SurtrLocal Declare(LocalSymbol local)
        {
            if (_locals.TryGetValue(local, out var existing))
                return existing;

            int width = SlotCountOfType(local.Type);
            var slot = width == 1 ? _method.DeclareLocal(local.Name) : _method.DeclareLocals(local.Name, width);

            if (width > 1)
                _slotWidthsByIndex[slot.Index] = width;

            _locals.Add(local, slot);
            return slot;
        }

        private SurtrLocal Slot(LocalSymbol local)
            => _locals.TryGetValue(local, out var slot)
                ? slot
                : throw Unsupported($"a read of '{local.Name}' before its declaration was emitted");

        /// <summary>The slot a parameter lives in, which a splice redirects to its own temporary.</summary>
        private SurtrLocal ParameterSlot(ParameterSymbol parameter)
        {
            if (_splicedParameters.TryGetValue(parameter, out var spliced))
                return spliced;

            if (!ReferenceEquals(parameter.ContainingSymbol, _symbol) && parameter.ContainingSymbol is not null)
                throw Unsupported($"a read of '{parameter.Name}', which belongs to another method");

            // An instance operator's first parameter is its receiver (�5.6), and the runtime keeps
            // the receiver as an implicit slot rather than a declared parameter � so parameter 0 is
            // local 0, and every later parameter shifts down by one to match.
            if (_symbol.Role == MethodRole.Operator && !_symbol.IsStatic)
                return parameter.Ordinal == 0 ? _method.Receiver : _method.Parameter(parameter.Ordinal - 1);

            return _method.Parameter(parameter.Ordinal);
        }

        /// <summary>
        /// Pushes a local or parameter, from an upvalue when a lambda captured it and from its own
        /// slot otherwise.
        /// </summary>
        private void LoadSymbol(Symbol symbol, Action fromSlot)
        {
            if (_captures is not null && _captures.TryGetValue(symbol, out int upValue))
            {
                Code.UpValueGet(upValue);
                return;
            }

            fromSlot();
        }

        /// <summary>Pushes the enclosing instance, wherever this body gets one from.</summary>
        private void LoadReceiver()
        {
            if (_receiverUpValue is int upValue)
            {
                Code.UpValueGet(upValue);
                return;
            }

            if (_inlines.Count > 0 && _inlines[_inlines.Count - 1].Receiver is SurtrLocal spliced)
            {
                EmitLoadLocal(spliced);
                return;
            }

            if (!_method.HasReceiver)
                throw Unsupported("'this', in a body with no receiver");

            EmitLoadLocal(_method.Receiver);

            // Inside a value class's own method the receiver arrived boxed exactly when this
            // method's own dispatch might have been resolved through its class � the same test
            // BoxReceiverForCall makes at every call site, so the two can never disagree about
            // which convention this body was compiled against. A direct dispatch never boxes on
            // the way in, so there is nothing here to unwrap.
            if (_symbol.ContainingType is NamedTypeSymbol { TypeKind: TypeSymbolKind.ValueClass }
                && _symbol.Dispatch != MethodDispatch.Direct)
                Code.Unbox();
        }

        private SurtrFieldInfo Field(FieldSymbol field)
            => _context.Resolve(field)
                ?? throw Unsupported($"a use of '{field.Name}', which no module being built declares");

        /// <summary>
        /// The value a bound expression folds to, for the emitter's own decisions � a switch key, a
        /// const-fun argument.
        /// </summary>
        /// <remarks>
        /// A literal, a conversion the binder wrapped one in, or a unary/binary expression built out
        /// of more of the same � so a <c>const fun</c> argument like <c>2 + 3</c> folds here too, not
        /// only a literal written directly. This works over the <em>bound</em> tree rather than
        /// syntax on purpose: a bound operand has already gone through the binder's own name
        /// resolution (locals, parameters, and � since a <c>const</c> field folds to a literal at
        /// bind time already � even a module or class constant), so nothing here can answer a
        /// local's name from an unrelated same-named constant the way a second, syntax-based lookup
        /// against <c>ConstantEvaluator</c>'s flat, module-wide name table could. It still does not
        /// duplicate that evaluator's full reach � no calls, no conditionals � only the arithmetic a
        /// `const fun` argument realistically needs one instruction lower than its declaration.
        /// </remarks>
        private static object? ConstantOf(BoundExpression expression) => expression switch
        {
            BoundLiteralExpression literal => literal.Value,
            BoundConversionExpression { Conversion.Kind: ConversionKind.Identity } identity => ConstantOf(identity.Operand),
            BoundConversionExpression { Conversion.Kind: ConversionKind.ImplicitNumeric } widened =>
                ConstantOf(widened.Operand) is long widenedInt ? (double)widenedInt : null,
            BoundBinaryExpression binary => ConstantBinary(binary),
            BoundUnaryExpression unary => ConstantUnary(unary),
            _ => null,
        };

        /// <summary>Folds a binary expression once both its operands already fold. See <see cref="ConstantOf"/>.</summary>
        private static object? ConstantBinary(BoundBinaryExpression binary)
        {
            if (ConstantOf(binary.Left) is not object left || ConstantOf(binary.Right) is not object right)
                return null;

            if (left is long li && right is long ri)
            {
                return binary.Operator switch
                {
                    BinaryOperator.Add => li + ri,
                    BinaryOperator.Subtract => li - ri,
                    BinaryOperator.Multiply => li * ri,
                    BinaryOperator.Divide => ri != 0 ? li / ri : (object?)null,
                    BinaryOperator.Modulo => ri != 0 ? li % ri : (object?)null,
                    BinaryOperator.BitAnd => li & ri,
                    BinaryOperator.BitOr => li | ri,
                    BinaryOperator.BitXor => li ^ ri,
                    BinaryOperator.ShiftLeft => li << (int)(ri & 31),
                    BinaryOperator.ShiftRight => li >> (int)(ri & 31),
                    _ => null,
                };
            }

            if (left is double || right is double)
            {
                double ld = left is double ldd ? ldd : (long)left;
                double rd = right is double rdd ? rdd : (long)right;

                return binary.Operator switch
                {
                    BinaryOperator.Add => ld + rd,
                    BinaryOperator.Subtract => ld - rd,
                    BinaryOperator.Multiply => ld * rd,
                    BinaryOperator.Divide => ld / rd,
                    _ => null,
                };
            }

            return null;
        }

        /// <summary>Folds a unary expression once its operand already folds. See <see cref="ConstantOf"/>.</summary>
        private static object? ConstantUnary(BoundUnaryExpression unary)
        {
            if (ConstantOf(unary.Operand) is not object operand)
                return null;

            return (unary.Operator, operand) switch
            {
                (UnaryOperator.Not, bool b) => !b,
                (UnaryOperator.Negate, long i) => -i,
                (UnaryOperator.Negate, double d) => -d,
                (UnaryOperator.Complement, long c) => ~c,
                _ => null,
            };
        }

        /// <summary>
        /// The operand family a type belongs to, which is what every arithmetic, comparison and
        /// conversion opcode is chosen by.
        /// </summary>
        internal static SurtrValueTypeCode TypeCodeOf(TypeSymbol type)
        {
            var bare = type.NonNullable;

            switch (bare.SpecialType)
            {
                case SpecialType.Int: return SurtrValueTypeCode.Integer;
                case SpecialType.Float: return SurtrValueTypeCode.Float;
                case SpecialType.Bool: return SurtrValueTypeCode.Boolean;
                case SpecialType.Char: return SurtrValueTypeCode.Character;
                case SpecialType.String: return SurtrValueTypeCode.String;
                case SpecialType.Range: return SurtrValueTypeCode.Range;
                case SpecialType.Void: return SurtrValueTypeCode.Void;
                case SpecialType.Unknown: return SurtrValueTypeCode.Erased;
            }

            switch (bare.TypeKind)
            {
                case TypeSymbolKind.Array: return SurtrValueTypeCode.Array;
                case TypeSymbolKind.Tuple: return SurtrValueTypeCode.Tuple;
                case TypeSymbolKind.Dictionary: return SurtrValueTypeCode.Dictionary;
                case TypeSymbolKind.Closure: return SurtrValueTypeCode.Closure;
                case TypeSymbolKind.TypeParameter: return SurtrValueTypeCode.Erased;
                case TypeSymbolKind.Native: return SurtrValueTypeCode.Native;

                case TypeSymbolKind.ValueClass:
                {
                    // A value class is the field it wraps wherever its type is statically known,
                    // which is exactly where an opcode has to be chosen.
                    var underlying = ((NamedTypeSymbol)bare).UnderlyingType;
                    return underlying is null ? SurtrValueTypeCode.Object : TypeCodeOf(underlying);
                }

                default: return SurtrValueTypeCode.Object;
            }
        }

        /// <summary>
        /// The failure every un-lowered construct raises, pointed at the construct itself.
        /// </summary>
        /// <remarks>
        /// The span comes from <see cref="_at"/> rather than from an argument, so that the forty-odd
        /// places that give up on something do not each have to remember to say where they were.
        /// What keeps it accurate is that <see cref="Statement"/> and <see cref="Expression"/>
        /// restore it on the way out: a node that finished lowering is no longer where we are, so by
        /// the time its parent gives up, this names the parent.
        /// </remarks>
        private SurtrEmitException Unsupported(string what)
            => new SurtrEmitException(
                $"'{_symbol.Name}' uses {what}, which code generation does not lower yet.",
                _at?.Span ?? default);
        #endregion
    }
}
