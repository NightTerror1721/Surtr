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
    /// <see cref="Statement"/> leaves the stack exactly as it found it. Everything else — how deep
    /// the stack gets, how wide a branch has to be, how many frame slots the body needs — is the
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
        /// A <c>switch</c> pushes one of these too, because §4.3 makes <c>break</c> leave the switch
        /// — but it is not a loop, so a <c>continue</c> inside it belongs to whatever loop encloses
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

            /// <summary>The name a labelled <c>break</c> may reach it by (§4.2).</summary>
            public string? Label { get; }

            /// <summary>
            /// How many <c>finally</c> blocks were in scope here, so a jump out knows how many it
            /// has to run on the way.
            /// </summary>
            public int FinallyDepth { get; }
        }

        /// <summary>One <c>inline</c> call site being spliced (§3.6).</summary>
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
        /// §3.6 makes inlining a request rather than a promise, so a ceiling is legal. Mutual
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
                // value they leave behind, and here neither leaves one — so the distinction the
                // long form exists to make has nothing to make it about, and the update is one
                // instruction.
                case BoundUnaryExpression unary when IsIncrementOrDecrement(unary.Operator):
                    if (TryEmitInPlaceStep(unary))
                        return;

                    break;

                // `a?.f();` still has to skip the call when `a` is null, but neither path leaves a
                // value — so the guard is emitted without one rather than pushed and popped.
                case BoundNullConditionalExpression access:
                    EmitNullConditional(access, discardResult: true);
                    return;
            }

            Expression(expression);

            if (!expression.Type.IsVoid)
                Code.Pop();
        }

        private void EmitLocalDeclaration(BoundLocalDeclarationStatement declaration)
        {
            var slot = Declare(declaration.Local);

            if (declaration.Initializer is null)
                return;

            Expression(declaration.Initializer);
            Code.StoreLocal(slot);
        }

        private void EmitIf(BoundIfStatement conditional)
        {
            var otherwise = Code.NewLabel();

            Expression(conditional.Condition);
            Code.JumpIfFalse(otherwise);
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
            {
                Expression(loop.Condition);
                Code.JumpIfFalse(end);
            }

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
            {
                Expression(loop.Condition);
                Code.JumpIfFalse(end);
            }

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
        /// Lowers a <c>for-in</c>, by index wherever that is possible.
        /// </summary>
        /// <remarks>
        /// §4.2 defines <c>for-in</c> against <c>IIterable&lt;T&gt;</c>, and every built-in
        /// collection really does satisfy it — but walking one by index allocates no cursor and
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
        /// A range written inline in a loop header is the case <c>RangeNew</c>'s own documentation
        /// says must not allocate: both bounds are already on the stack, so the loop reads them into
        /// two slots and counts between them. A range that arrived some other way is walked from its
        /// <c>start</c> for its <c>length</c> — which is the one reading that needs no branch on
        /// whether the range was written <c>..</c> or <c>..=</c>, since <c>length</c> already knows.
        /// </remarks>
        private void EmitForInRange(BoundForInStatement loop)
        {
            var variable = Declare(loop.Variable);
            var limit = _method.DeclareLocal("$limit");
            bool inclusive;

            if (loop.Sequence is BoundBinaryExpression
                {
                    Operator: BinaryOperator.Range or BinaryOperator.RangeInclusive
                } bounds)
            {
                inclusive = bounds.Operator == BinaryOperator.RangeInclusive;
                Expression(bounds.Left);
                Code.StoreLocal(variable);
                Expression(bounds.Right);
                Code.StoreLocal(limit);
            }
            else
            {
                inclusive = false;
                var range = _method.DeclareLocal("$range");

                Expression(loop.Sequence);
                Code.StoreLocal(range);

                Code.LoadLocal(range);
                Code.Call(RangeAccessor("start"));
                Code.StoreLocal(variable);

                Code.LoadLocal(range);
                Code.Call(RangeAccessor("start"));
                Code.LoadLocal(range);
                Code.Call(RangeAccessor("length"));
                Code.Add(SurtrValueTypeCode.Integer);
                Code.StoreLocal(limit);
            }

            var top = Code.NewLabel();
            var step = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            Code.LoadLocal(variable);
            Code.LoadLocal(limit);
            Code.JumpIfCompare(
                inclusive ? SurtrComparison.Greater : SurtrComparison.GreaterOrEqual,
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
            Code.StoreLocal(source);
            Code.LoadInt(0);
            Code.StoreLocal(index);

            var top = Code.NewLabel();
            var step = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            Code.LoadLocal(index);
            Code.LoadLocal(source);
            Length(kind);
            Code.JumpIfCompare(SurtrComparison.GreaterOrEqual, SurtrValueTypeCode.Integer, end);

            Code.LoadLocal(source);
            Code.LoadLocal(index);
            Element(kind);
            Code.StoreLocal(variable);

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
        /// once makes that question have an answer — the loop sees the keys the dictionary had when
        /// it started.
        /// </remarks>
        private void EmitForInDictionary(BoundForInStatement loop, DictionaryTypeSymbol dictionary)
        {
            if (loop.Variable.Type.NonNullable is not TupleTypeSymbol pair || pair.ElementTypes.Count != 2)
                throw Unsupported($"a for-in over '{dictionary.ToDisplayString()}' whose variable is not a (key, value) pair");

            var source = _method.DeclareLocal("$dict");
            var keys = _method.DeclareLocal("$keys");
            var index = _method.DeclareLocal("$index");
            var variable = Declare(loop.Variable);

            Expression(loop.Sequence);
            Code.StoreLocal(source);
            Code.LoadLocal(source);
            Code.DictionaryKeys(SurtrClassReference.Array(Descriptors.Emit(dictionary.KeyType)));
            Code.StoreLocal(keys);
            Code.LoadInt(0);
            Code.StoreLocal(index);

            var top = Code.NewLabel();
            var step = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            Code.LoadLocal(index);
            Code.LoadLocal(keys);
            Code.ArrLen();
            Code.JumpIfCompare(SurtrComparison.GreaterOrEqual, SurtrValueTypeCode.Integer, end);

            // The pair is packed per iteration, which is what the variable's own type says it is.
            Code.LoadLocal(keys);
            Code.LoadLocal(index);
            Code.ArrGet();
            Code.LoadLocal(source);
            Code.LoadLocal(keys);
            Code.LoadLocal(index);
            Code.ArrGet();
            Code.DictGet();
            Code.PackTuple(Descriptors.Emit(pair), 2);
            Code.StoreLocal(variable);

            PushLoop(step, end);
            Statement(loop.Body);
            PopTargets();

            Code.MarkLabel(step);
            Code.IncrementLocal(index, 1);
            Code.Jump(top);
            Code.MarkLabel(end);
        }

        /// <summary>
        /// Walks anything that satisfies <c>IIterable&lt;T&gt;</c> through its cursor (§4.2).
        /// </summary>
        /// <remarks>
        /// The general path, and the only one that allocates. Every call goes through the interface
        /// dispatch table rather than a vtable slot, because the receiver's own class is not what
        /// the loop was written against.
        /// </remarks>
        private void EmitForInIterable(BoundForInStatement loop)
        {
            var iterate = ContractMethod(SurtrBuiltIns.IIterable, "iterate");
            var moveNext = ContractMethod(SurtrBuiltIns.IIterator, "moveNext");
            var current = ContractMethod(SurtrBuiltIns.IIterator, MemberNames.Getter("current"));

            var cursor = _method.DeclareLocal("$iterator");
            var variable = Declare(loop.Variable);

            Expression(loop.Sequence);
            Code.CallInterface(iterate);
            Code.StoreLocal(cursor);

            var top = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            Code.LoadLocal(cursor);
            Code.CallInterface(moveNext);
            Code.JumpIfFalse(end);

            Code.LoadLocal(cursor);
            Code.CallInterface(current);

            // `current` is typed by the contract's own parameter, so it comes back erased — but
            // what it hands back is the collection's own storage, and a built-in collection stores
            // a primitive raw: `an int pushed into an int[] is never boxed on the way`. So a
            // reference element is checked and a primitive one is already what it should be. It
            // cannot be both, and `Cast` reads its subject as a reference unconditionally, so
            // casting a raw int would read whatever entity its value happens to number.
            if (loop.Variable.Type.NonNullable.IsReferenceType && !loop.Variable.Type.NonNullable.IsVoid)
                Unerase(loop.Variable.Type);

            Code.StoreLocal(variable);

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
        /// the compiler as at run time — that is the whole reason <c>StrHash</c> exists.
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
            Code.StoreLocal(subject);

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

            // A section runs to its own end: §4.3 makes fall-through explicit, so nothing here
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
                Code.LoadLocal(subject);

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
                    Code.LoadLocal(subject);
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
        /// does not reject one — so it is caught here and the chain takes over, which matches the
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
        /// Two texts may hash alike, so a hash arm is not an answer — it is a shortlist. Each
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

            Code.LoadLocal(subject);
            Code.StrHash();
            Code.SwitchOn(cases, fallback);

            foreach (var (label, bucket) in confirmations)
            {
                Code.MarkLabel(label);

                foreach (var (text, arm) in bucket)
                {
                    Code.LoadLocal(subject);
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
        /// normal exit path — falling off the try, a <c>return</c>, a <c>break</c> leaving it — plus
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

                Code.StoreLocal(Declare(clause.Exception));

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
                Code.StoreLocal(raised);
                RunFinally(@try.Finally);
                Code.LoadLocal(raised);
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

        private void EmitReturn(BoundReturnStatement @return)
        {
            // A spliced body's `return` leaves the splice, not the frame — §3.6 makes inlining
            // invisible, and a real Ret here would end the caller.
            if (_inlines.Count > 0)
            {
                var frame = _inlines[_inlines.Count - 1];

                if (@return.Value is not null && frame.HasResult)
                {
                    Expression(@return.Value);
                    Code.StoreLocal(frame.Result);
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
                var result = _method.DeclareLocal("$result");
                Expression(@return.Value);
                Code.StoreLocal(result);
                UnwindTo(0);
                Code.LoadLocal(result);
                Code.ReturnValue();
                return;
            }

            Expression(@return.Value);
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
                    LoadSymbol(local.Local, () => Code.LoadLocal(Slot(local.Local)));
                    return;

                case BoundParameterExpression parameter:
                    LoadSymbol(parameter.Parameter, () => Code.LoadLocal(ParameterSlot(parameter.Parameter)));
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

                case BoundInterpolatedStringExpression interpolated:
                    EmitInterpolatedString(interpolated);
                    return;

                case BoundTypeTestExpression test:
                    Expression(test.Operand);
                    Code.TestInstanceOf(Descriptors.Emit(test.TestedType.NonNullable));
                    return;

                case BoundSwitchExpression @switch:
                    EmitSwitchExpression(@switch);
                    return;

                case BoundNullConditionalExpression conditionalAccess:
                    EmitNullConditional(conditionalAccess, discardResult: false);
                    return;

                case BoundNullAssertExpression assertion:
                    EmitNullAssert(assertion);
                    return;

                case BoundConditionalReceiver:
                    Code.LoadLocal(
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
                // §5.1: absence in a nullable primitive is its own tagged value, not a null reference.
                // A reference is its 32-bit payload, so a null one and a present `0` would be the
                // same value — which is exactly what the absent tag exists to keep apart.
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

        private void EmitConversion(BoundConversionExpression conversion)
        {
            var from = conversion.Operand.Type;
            var to = conversion.Type;

            // §4.8 hands `as?` to the compiler: test, then either keep the value or produce null.
            if (conversion.IsSafe)
            {
                EmitSafeCast(conversion);
                return;
            }

            if (conversion.Conversion.Kind == ConversionKind.UserDefined)
            {
                // §5.6's `operator as` is an ordinary static call whose one argument is the value.
                Expression(conversion.Operand);
                EmitDirectCall(
                    conversion.Conversion.Method
                        ?? throw Unsupported("a user-defined conversion with no operator behind it"),
                    discardResult: false);

                return;
            }

            // `null` reaching a nullable primitive is the absent tag, not a null reference: §5.1 makes
            // absence a value of its own precisely so it costs no allocation, and a null reference is
            // its 32-bit payload — which would make an absent `int?` and a present `0` the same value.
            if (conversion.Operand is BoundLiteralExpression { Value: null } && IsNullablePrimitive(to))
            {
                Code.PushAbsent(TypeCodeOf(to));
                return;
            }

            Expression(conversion.Operand);

            switch (conversion.Conversion.Kind)
            {
                case ConversionKind.Identity:
                case ConversionKind.ImplicitNullable:
                    // Nothing to emit: a primitive widening into its own nullable form keeps the
                    // same representation, and a reference is its payload either way.
                    return;

                case ConversionKind.ImplicitReference:
                    // §6.3: a value class is erased to its field, so reaching a slot that holds a
                    // reference — an interface it implements — is where it becomes a real object.
                    BoxIfValueClass(from);
                    return;

                case ConversionKind.ImplicitNumeric:
                case ConversionKind.ExplicitNumeric:
                    Code.Convert(TypeCodeOf(from), TypeCodeOf(to));
                    return;

                case ConversionKind.ImplicitErasure:
                    // §1.11's first obligation: a value reaching a slot that only holds a reference
                    // has to become one. Box emits nothing for a reference already.
                    if (!BoxIfValueClass(from))
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
                    throw Unsupported($"a {conversion.Conversion.Kind} conversion");
            }
        }

        /// <summary>Reads a concrete type back out of an erased slot — §1.11's second obligation.</summary>
        private void Unerase(TypeSymbol target)
        {
            var bare = target.NonNullable;

            if (bare.SpecialType == SpecialType.Unknown || bare.TypeKind == TypeSymbolKind.TypeParameter)
                return;

            if (bare.TypeKind == TypeSymbolKind.ValueClass)
            {
                Code.CastTo(Descriptors.EmitBoxedForm((NamedTypeSymbol)bare));
                Code.Unbox();
                return;
            }

            Code.CastTo(Descriptors.Emit(bare));

            if (bare.IsPrimitive && !bare.IsVoid)
                Code.Unbox();
        }

        /// <summary>
        /// Boxes a <c>value class</c> as the class it presents as, and says whether it did.
        /// </summary>
        /// <remarks>
        /// The whole of §6.3 in one place. Where a value class's type is statically known it is the
        /// field it wraps and nothing happens; where the slot holds a reference the box has to name
        /// the real class, because the erased value is precisely the thing that no longer says what
        /// it was.
        /// </remarks>
        private bool BoxIfValueClass(TypeSymbol type)
        {
            if (type.NonNullable is not NamedTypeSymbol { TypeKind: TypeSymbolKind.ValueClass } valueClass)
                return false;

            Code.BoxAs(_context.Module.Type(Descriptors.EmitBoxedForm(valueClass)));
            return true;
        }

        /// <summary>
        /// Emits <c>as?</c>: one <c>CastOrNull</c> to a reference type, and a tested unbox to a
        /// primitive.
        /// </summary>
        /// <remarks>
        /// <c>CastOrNull</c> exists because a reference target needs nothing else — the failure
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
            var failed = Code.NewLabel();
            var end = Code.NewLabel();

            Expression(conversion.Operand);
            Code.StoreLocal(value);
            Code.LoadLocal(value);
            Code.TestInstanceOf(Descriptors.Emit(target));
            Code.JumpIfFalse(failed);

            Code.LoadLocal(value);
            Code.Unbox();

            Code.Jump(end);
            Code.MarkLabel(failed);
            Code.LoadNull();
            Code.MarkLabel(end);
        }

        private void EmitBinary(BoundBinaryExpression binary)
        {
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

            var operands = TypeCodeOf(binary.Left.Type);

            // §4.8 hands ordering on strings to the compiler: there is no opcode that orders one,
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
                // order — the opcodes are named after the machine operation, not after the token.
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
        /// pairwise emission did — flattening changes what is allocated, not when anything runs.
        /// </remarks>
        private void EmitStringConcat(BoundBinaryExpression binary)
        {
            var parts = new List<BoundExpression>();
            FlattenStringConcat(binary, parts);

            int pending = 0;

            foreach (var part in parts)
            {
                Expression(part);

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
            // Only a string-typed `+` is a concatenation; anything else — a user-defined operator,
            // an interpolation, a call returning a string — is one operand of this one.
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
        /// Emits a <c>?.</c> access: the receiver once, then the access only if it was not null (§5.1).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The receiver goes into a slot rather than being duplicated on the stack, for two reasons.
        /// It has to be readable at whatever depth the access reaches it — an argument list may sit
        /// between them — and both paths have to leave the stack at the same depth, which the emitter
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
            Code.StoreLocal(slot);

            var whenNull = Code.NewLabel();
            var end = Code.NewLabel();

            Code.LoadLocal(slot);

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
        /// Emits <c>x!!</c>: the value, and a raise on the path where it turned out to be null (§5.1).
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
        /// Whether a type's "no value" is the absent tag rather than a null reference (§5.1).
        /// </summary>
        private static bool IsNullablePrimitive(TypeSymbol type)
            => type.IsNullable && type.NonNullable.SpecialType is SpecialType.Int or SpecialType.Float
                or SpecialType.Bool or SpecialType.Char;

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
        /// §5.7 makes it yield an <c>int</c> whose sign is the answer, so two comparisons and a
        /// subtraction give it exactly.
        /// </remarks>
        private void EmitThreeWayCompare(BoundBinaryExpression binary)
        {
            var operands = TypeCodeOf(binary.Left.Type);

            var left = _method.DeclareLocal("$left");
            var right = _method.DeclareLocal("$right");

            Expression(binary.Left);
            Code.StoreLocal(left);
            Expression(binary.Right);
            Code.StoreLocal(right);

            Code.LoadLocal(left);
            Code.LoadLocal(right);
            Code.Compare(SurtrComparison.Greater, operands);
            Code.Convert(SurtrValueTypeCode.Boolean, SurtrValueTypeCode.Integer);

            Code.LoadLocal(left);
            Code.LoadLocal(right);
            Code.Compare(SurtrComparison.Less, operands);
            Code.Convert(SurtrValueTypeCode.Boolean, SurtrValueTypeCode.Integer);

            Code.Subtract(SurtrValueTypeCode.Integer);
        }

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
                    // §5.1 asserts rather than converts, and the assertion is what a cast to the
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
        /// differ in which value they leave behind — a distinction that only exists at emit.
        /// </remarks>
        private void EmitIncrement(BoundUnaryExpression unary)
        {
            bool isPost = unary.Operator is UnaryOperator.PostIncrement or UnaryOperator.PostDecrement;
            bool isIncrement = unary.Operator is UnaryOperator.PreIncrement or UnaryOperator.PostIncrement;

            var family = TypeCodeOf(unary.Operand.Type);
            var before = _method.DeclareLocal("$before");

            Expression(unary.Operand);
            Code.StoreLocal(before);

            Code.LoadLocal(before);

            if (family == SurtrValueTypeCode.Float)
                Code.LoadFloat(1.0);
            else
                Code.LoadInt(1);

            if (isIncrement)
                Code.Add(family);
            else
                Code.Subtract(family);

            var after = _method.DeclareLocal("$after");
            Code.StoreLocal(after);

            Store(unary.Operand, () => Code.LoadLocal(after));
            Code.LoadLocal(isPost ? before : after);
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

            var value = _method.DeclareLocal("$assigned");
            Expression(assignment.Value);
            Code.StoreLocal(value);

            Store(assignment.Target, () => Code.LoadLocal(value));
            Code.LoadLocal(value);
        }

        /// <summary>
        /// Recognises <c>i = i + k</c> over an integer local and emits it as one instruction.
        /// </summary>
        /// <remarks>
        /// §5.7's compound assignments arrive expanded — <c>i += 1</c> is already <c>i = i + 1</c>
        /// in the bound tree — so this one shape covers both spellings, and covers the step of
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
        /// nothing — so the one order that works is the one each target dictates.
        /// </remarks>
        private void Store(BoundExpression target, Action value)
        {
            switch (target)
            {
                case BoundLocalExpression local:
                    value();
                    Code.StoreLocal(Slot(local.Local));
                    return;

                case BoundParameterExpression parameter:
                    value();
                    Code.StoreLocal(ParameterSlot(parameter.Parameter));
                    return;

                case BoundFieldExpression field:
                {
                    // §10: a `native var` is the host's storage, reached through this module's import
                    // table rather than through a static slot it does not have.
                    if (field.Field.IsNative)
                    {
                        value();
                        Code.StoreGlobal(field.Field.Name);
                        return;
                    }

                    var info = Field(field.Field);

                    if (field.Field.IsStatic)
                    {
                        value();
                        Code.StoreStaticField(info);
                        return;
                    }

                    Expression(field.Receiver ?? throw Unsupported($"a write to '{field.Field.Name}' with no receiver"));
                    value();
                    Code.StoreField(info);
                    return;
                }

                case BoundPropertyExpression property:
                {
                    var setter = property.Property.Setter
                        ?? throw Unsupported($"a write to '{property.Property.Name}', which has no setter");

                    if (!property.Property.IsStatic)
                        Expression(property.Receiver ?? throw Unsupported($"a write to '{property.Property.Name}' with no receiver"));

                    value();
                    EmitResolvedCall(setter, virtualCall: setter.Dispatch != MethodDispatch.Direct, discardResult: true);
                    return;
                }

                case BoundIndexExpression index:
                {
                    Expression(index.Target);
                    Expression(index.Index);
                    value();

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

        private void EmitFieldRead(BoundFieldExpression field)
        {
            // §10: a host global has no field slot to read — it lives in the runtime's global table
            // and the module names it by an import bound when the module loads.
            if (field.Field.IsNative)
            {
                Code.LoadGlobal(field.Field.Name);
                return;
            }

            var info = Field(field.Field);

            if (field.Field.IsStatic)
            {
                // An enum case is exactly this: a static, read-only field of the enum's own type
                // holding the one instance its static initializer built.
                Code.LoadStaticField(info);
                return;
            }

            var receiver = field.Receiver ?? throw Unsupported($"a read of '{field.Field.Name}' with no receiver");

            // A value class is its one field, so reading that field off one is the value itself —
            // there is no instance to load from (§2.9).
            if (receiver.Type.NonNullable.TypeKind == TypeSymbolKind.ValueClass)
            {
                Expression(receiver);
                return;
            }

            Expression(receiver);
            Code.LoadField(info);
        }

        private void EmitPropertyRead(BoundPropertyExpression property)
        {
            var getter = property.Property.Getter
                ?? throw Unsupported($"a read of '{property.Property.Name}', which has no getter");

            if (!property.Property.IsStatic)
                Expression(property.Receiver ?? throw Unsupported($"a read of '{property.Property.Name}' with no receiver"));

            EmitResolvedCall(getter, virtualCall: getter.Dispatch != MethodDispatch.Direct, discardResult: false);
        }

        private void EmitObjectCreation(BoundObjectCreationExpression creation)
        {
            var type = (NamedTypeSymbol)creation.Type.NonNullable;

            if (type.TypeKind == TypeSymbolKind.ValueClass)
            {
                EmitValueClassCreation(creation, type);
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
        /// Builds a <c>value class</c>, which allocates nothing (§2.9).
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
        /// <c>this.field = expression</c> — one assignment, nothing else. That is what a value
        /// class's constructor is for, and anything wider is refused rather than approximated,
        /// because there is no object for a second statement to observe.
        /// </para>
        /// </remarks>
        private void EmitValueClassCreation(BoundObjectCreationExpression creation, NamedTypeSymbol type)
        {
            if (creation.Constructor is not MethodSymbol constructor)
            {
                throw Unsupported(
                    $"building a '{type.Name}' with no constructor, which leaves nothing to put in the field it wraps");
            }

            if (_context.Bodies is null
                || !_context.Bodies.TryGetValue(constructor, out var body)
                || WrappedValue(body) is not BoundExpression wrapped)
            {
                throw Unsupported(
                    $"building a '{type.Name}', whose constructor is not a single assignment to the field it wraps");
            }

            for (int i = 0; i < creation.Arguments.Count; i++)
            {
                var slot = _method.DeclareLocal("$value$" + constructor.Parameters[i].Name);
                Expression(creation.Arguments[i]);
                Code.StoreLocal(slot);
                _splicedParameters[constructor.Parameters[i]] = slot;
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
        /// Emits a call, choosing between splicing it, folding it and really making it.
        /// </summary>
        private void EmitCall(BoundCallExpression call, bool discardResult)
        {
            if (TryFoldConstCall(call, discardResult))
                return;

            if (call.Method.IsInline || call.Method.IsForceInline)
            {
                if (TryInline(call, discardResult))
                    return;

                if (call.Method.IsForceInline)
                    throw Unsupported($"'forceinline fun {call.Method.Name}', whose body is not available to splice");
            }

            if (call.Receiver is not null)
            {
                Expression(call.Receiver);

                // §6.3: a value class is the field it wraps, and a field is not something to
                // dispatch on — so a call on one boxes first, and `this` inside the callee unwraps.
                BoxIfValueClass(call.Receiver.Type);
            }

            foreach (var argument in call.Arguments)
                Expression(argument);

            EmitResolvedCall(call.Method, call.IsVirtual, discardResult);
        }

        /// <summary>
        /// Emits the call instruction for a method whose receiver and arguments are already on the
        /// stack.
        /// </summary>
        /// <remarks>
        /// Where a call lands is a property of the callee, not of the call site — the interpreter
        /// reads it off the method it names — so nothing here picks between bytecode and host code.
        /// What it does pick between is the four <em>tables</em> a callee can live in: this module's
        /// method builders, another module's functions, an interface's slots, and everything else.
        /// </remarks>
        private void EmitResolvedCall(MethodSymbol method, bool virtualCall, bool discardResult)
        {
            // §10's one genuinely global category. `CallGlobalNative` is not about the body being
            // native — a native class member is called like anything else — but about the target
            // living in the host's table rather than in a module's, which is a different namespace.
            if (method.IsNative && method.ContainingType is null)
            {
                Code.CallGlobal(
                    method.Name,
                    method.Parameters.Count,
                    discardResult || method.ReturnType.IsVoid ? 0 : 1);

                return;
            }

            if (_context.TryGetBuilder(method, out var local))
            {
                // The one case where the call site rather than the callee decides: a `super` call,
                // or one on a sealed receiver, names a virtual method and must not go through the
                // vtable — an override calling its base would otherwise call itself.
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
            // another module (`docs/VM-Plan.md` §3.3) - a cross-module call goes through the module
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
        /// Replaces a call to a <c>const fun</c> with constant arguments by the value it folds to
        /// (§7.2).
        /// </summary>
        /// <remarks>
        /// This is where §7.2's promise becomes observable: the callee still exists and can still be
        /// called at run time, but a call the compiler could answer does not survive into the
        /// bytecode. A fold that fails is not an error — the call is simply emitted.
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
        /// Splices an <c>inline</c> call site (§3.6), if the body is available and it is safe to.
        /// </summary>
        /// <remarks>
        /// Arguments land in real slots first, so an argument written once is evaluated once
        /// whatever the body does with the parameter. A <c>return</c> inside the splice becomes a
        /// jump to the splice's own exit rather than a <c>Ret</c>, which is what makes inlining
        /// invisible to the caller.
        /// </remarks>
        private bool TryInline(BoundCallExpression call, bool discardResult)
        {
            if (_context.Bodies is null || !_context.Bodies.TryGetValue(call.Method, out var body))
                return false;

            if (_inlines.Count >= MaxInlineDepth || call.IsVirtual)
                return false;

            // A body that splices itself, directly or through another inline function, would expand
            // forever — and its locals would collide, since a symbol maps to one slot.
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
                var slot = _method.DeclareLocal("$inlineThis");
                Expression(call.Receiver);
                Code.StoreLocal(slot);
                receiver = slot;
            }

            for (int i = 0; i < call.Arguments.Count; i++)
            {
                var parameter = call.Method.Parameters[i];
                var slot = _method.DeclareLocal("$inline$" + parameter.Name);

                Expression(call.Arguments[i]);
                Code.StoreLocal(slot);
                _splicedParameters[parameter] = slot;
            }

            bool hasResult = !call.Method.ReturnType.IsVoid && !discardResult;
            var result = hasResult ? _method.DeclareLocal("$inlineResult") : default;
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
                Code.LoadLocal(result);

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
        /// Lifts a lambda to a static function of this module and builds a closure over it (§8).
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
                captures[captured] = next++;

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

            Code.NewClosureFor(lifted, captures.Count + (lambda.CapturesReceiver ? 1 : 0));
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
                    LoadSymbol(local, () => Code.LoadLocal(Slot(local)));
                    return;

                case ParameterSymbol parameter:
                    LoadSymbol(parameter, () => Code.LoadLocal(ParameterSlot(parameter)));
                    return;

                default:
                    throw Unsupported($"a lambda capturing {captured.GetType().Name}");
            }
        }

        private void EmitIndexRead(BoundIndexExpression index)
        {
            var target = index.Target.Type.NonNullable;

            // §5.3 makes a tuple index a constant — the binder folds it and hands back a literal —
            // so it belongs in the instruction rather than on the stack.
            if (target.TypeKind == TypeSymbolKind.Tuple && index.Index is BoundLiteralExpression { Value: long ordinal })
            {
                Expression(index.Target);
                Code.TupleElement((int)ordinal);
                return;
            }

            Expression(index.Target);
            Expression(index.Index);

            switch (target.TypeKind)
            {
                case TypeSymbolKind.Array: Code.ArrGet(); return;
                case TypeSymbolKind.Dictionary: Code.DictGet(); return;
                case TypeSymbolKind.Tuple: Code.TupGet(); return;
            }

            if (target.SpecialType == SpecialType.String)
            {
                Code.StrGet();
                return;
            }

            throw Unsupported($"indexing '{index.Target.Type.ToDisplayString()}'");
        }

        private void EmitArrayLiteral(BoundArrayLiteralExpression array)
        {
            foreach (var element in array.Elements)
                Expression(element);

            // One immediate carries both the descriptor the object keeps and the element family its
            // slots are initialised from, so an empty literal still knows what it is.
            Code.PackArray(Descriptors.Emit(array.Type.NonNullable), array.Elements.Count);
        }

        private void EmitTupleLiteral(BoundTupleLiteralExpression tuple)
        {
            foreach (var element in tuple.Elements)
                Expression(element);

            Code.PackTuple(Descriptors.Emit(tuple.Type.NonNullable), tuple.Elements.Count);
        }

        private void EmitDictLiteral(BoundDictLiteralExpression dictionary)
        {
            foreach (var entry in dictionary.Entries)
            {
                Expression(entry.Key);
                Expression(entry.Value);
            }

            Code.PackDictionary(Descriptors.Emit(dictionary.Type.NonNullable), dictionary.Entries.Count);
        }

        /// <summary>
        /// Emits an interpolated string as one concatenation over all of its parts.
        /// </summary>
        /// <remarks>
        /// Every primitive already declares a native <c>toString</c>, so a non-string part is a call
        /// to that rather than a new opcode — which also means interpolation means exactly what
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
            Code.StoreLocal(subject);

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
            // non-nullable enum (§4.3), so the last arm is what is left over once the others have
            // been tested — and testing it as well would be comparing against the only value the
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

            // Every arm produces one value, so they all have to leave the stack at the same depth —
            // which is exactly what the emitter checks when the label joins them.
            var result = _method.DeclareLocal("$switchResult");

            for (int i = 0; i < arms.Length; i++)
            {
                Code.MarkLabel(arms[i]);
                Expression(@switch.Arms[i].Result);
                Code.StoreLocal(result);
                Code.Jump(end);
            }

            Code.MarkLabel(end);
            Code.LoadLocal(result);
        }
        #endregion

        #region Frame, captures and types
        private SurtrLocal Declare(LocalSymbol local)
        {
            if (_locals.TryGetValue(local, out var existing))
                return existing;

            var slot = _method.DeclareLocal(local.Name);
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
                Code.LoadLocal(spliced);
                return;
            }

            if (!_method.HasReceiver)
                throw Unsupported("'this', in a body with no receiver");

            Code.LoadLocal(_method.Receiver);

            // Inside a value class's own method the receiver arrived boxed, because that is the
            // only shape a call could dispatch on. Unwrapping it here is what makes `this` mean the
            // wrapped value everywhere in the language, so no other rule has to know about the box.
            if (_symbol.ContainingType is NamedTypeSymbol { TypeKind: TypeSymbolKind.ValueClass })
                Code.Unbox();
        }

        private SurtrFieldInfo Field(FieldSymbol field)
            => _context.Resolve(field)
                ?? throw Unsupported($"a use of '{field.Name}', which no module being built declares");

        /// <summary>
        /// The value a bound expression folds to, for the emitter's own decisions — a switch key, a
        /// const-fun argument.
        /// </summary>
        /// <remarks>
        /// Deliberately only literals and the conversions the binder wrapped them in. Anything
        /// wider belongs to <c>ConstantEvaluator</c>, which folds over syntax and is the layer that
        /// already answers this question for the language.
        /// </remarks>
        private static object? ConstantOf(BoundExpression expression) => expression switch
        {
            BoundLiteralExpression literal => literal.Value,
            BoundConversionExpression { Conversion.Kind: ConversionKind.Identity } conversion => ConstantOf(conversion.Operand),
            _ => null,
        };

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
