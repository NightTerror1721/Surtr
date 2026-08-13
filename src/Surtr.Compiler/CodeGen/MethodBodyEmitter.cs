#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax.Ast;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
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
    /// This exists because <c>const fun</c> folding needs it. §7.2 settles that a constant is folded
    /// by <em>running the function's real bytecode on the real interpreter</em> rather than by a
    /// second evaluator written in the compiler — so the compiler cannot fold anything until it can
    /// emit something, and Step 4 has to build the part of Step 5 it depends on.
    /// </para>
    /// <para>
    /// What it covers today is therefore the <b>const-evaluable subset</b> §7.2 defines: loops,
    /// conditionals, locals, arithmetic, strings, locally built arrays, tuples and dicts, and calls
    /// to other functions in the same module. Everything outside that — a lambda, a
    /// <c>try</c>/<c>finally</c>, an instance receiver, an <c>as?</c>, an interface call — raises
    /// <see cref="SurtrEmitException"/> rather than emitting something approximate. Being loud about
    /// the boundary is the point: a const fold that silently computed the wrong thing would be worse
    /// than one that refuses, and the list of refusals is exactly the lowering table in
    /// <c>docs/Compiler-Plan.md</c> §5.
    /// </para>
    /// <para>
    /// Two invariants hold throughout. <see cref="Expression"/> leaves exactly one value on the
    /// operand stack, unless the expression's type is <c>void</c>, in which case it leaves none;
    /// <see cref="Statement"/> leaves the stack exactly as it found it. Everything else — how deep
    /// the stack gets, how wide a branch has to be, how many frame slots the body needs — is the
    /// emitter's own job and is never computed here.
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
            public JumpTargets(SurtrLabel breakTarget, SurtrLabel continueTarget, bool isLoop, string? label)
            {
                Break = breakTarget;
                Continue = continueTarget;
                IsLoop = isLoop;
                Label = label;
            }

            public SurtrLabel Break { get; }

            public SurtrLabel Continue { get; }

            /// <summary>Whether a <c>continue</c> may target this, which only a loop may.</summary>
            public bool IsLoop { get; }

            /// <summary>The name a labelled <c>break</c> may reach it by (§4.2).</summary>
            public string? Label { get; }
        }

        private readonly SurtrMethodBuilder _method;
        private readonly MethodSymbol _symbol;
        private readonly DescriptorEmitter _descriptors;
        private readonly IReadOnlyDictionary<MethodSymbol, SurtrMethodBuilder> _functions;

        private readonly Dictionary<LocalSymbol, SurtrLocal> _locals = new Dictionary<LocalSymbol, SurtrLocal>();
        private readonly List<JumpTargets> _jumps = new List<JumpTargets>();

        // Set by a labelled statement and consumed by the loop it labels, so `outer: for (...)` can
        // be reached by `break outer` without the loop node itself carrying the name.
        private string? _pendingLabel;

        /// <summary>Creates an emitter for one method's body.</summary>
        /// <param name="method">The method whose frame and instruction stream this fills in.</param>
        /// <param name="symbol">The method as the binder sees it, for its parameters and return type.</param>
        /// <param name="descriptors">The one gate from a <see cref="TypeSymbol"/> to a descriptor.</param>
        /// <param name="functions">Every method a call site here may name, by symbol.</param>
        public MethodBodyEmitter(
            SurtrMethodBuilder method,
            MethodSymbol symbol,
            DescriptorEmitter descriptors,
            IReadOnlyDictionary<MethodSymbol, SurtrMethodBuilder> functions)
        {
            _method = method ?? throw new ArgumentNullException(nameof(method));
            _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
            _descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
            _functions = functions ?? throw new ArgumentNullException(nameof(functions));
        }

        private SurtrCodeEmitter Code => _method.Code;

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

        #region Statements
        private void Statement(BoundStatement statement)
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
            if (expression is BoundCallExpression call && !call.Method.ReturnType.IsVoid)
            {
                EmitCall(call, discardResult: true);
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
            Expression(loop.Condition);
            Code.JumpIfFalse(end);

            // `continue` re-tests the condition, so it targets the top rather than the body.
            PushLoop(top, end);
            Statement(loop.Body);
            PopTargets();

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

            if (loop.Condition is not null)
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

        /// <summary>
        /// Lowers a <c>for-in</c> to an indexed loop.
        /// </summary>
        /// <remarks>
        /// §4.2 defines <c>for-in</c> against <c>IIterable&lt;T&gt;</c>, but a built-in collection
        /// walks by index and allocates no cursor — which is exactly why the bound tree leaves the
        /// choice to here. The const-evaluable subset only ever meets a range, an array or a string,
        /// so those are what this covers; anything reached through <c>iterate()</c> belongs to
        /// Step 5.
        /// </remarks>
        private void EmitForIn(BoundForInStatement loop)
        {
            var sequence = loop.Sequence.Type.NonNullable;

            if (sequence.SpecialType == SpecialType.Range)
            {
                EmitForInRange(loop);
                return;
            }

            if (sequence.TypeKind == TypeSymbolKind.Array || sequence.SpecialType == SpecialType.String)
            {
                EmitForInIndexed(loop, sequence.SpecialType == SpecialType.String);
                return;
            }

            throw Unsupported($"a for-in over '{loop.Sequence.Type.ToDisplayString()}'");
        }

        /// <summary>
        /// Walks a range without ever building one.
        /// </summary>
        /// <remarks>
        /// A range written inline in a loop header is the case <c>RangeNew</c>'s own documentation
        /// says must not allocate: both bounds are already on the stack, so the loop reads them into
        /// two slots and counts between them.
        /// </remarks>
        private void EmitForInRange(BoundForInStatement loop)
        {
            if (loop.Sequence is not BoundBinaryExpression { Operator: BinaryOperator.Range or BinaryOperator.RangeInclusive } bounds)
                throw Unsupported("a for-in over a range that is not written inline");

            var variable = Declare(loop.Variable);
            var limit = _method.DeclareLocal("$limit");

            Expression(bounds.Left);
            Code.StoreLocal(variable);
            Expression(bounds.Right);
            Code.StoreLocal(limit);

            var top = Code.NewLabel();
            var step = Code.NewLabel();
            var end = Code.NewLabel();

            Code.MarkLabel(top);
            Code.LoadLocal(variable);
            Code.LoadLocal(limit);
            Code.JumpIfCompare(
                bounds.Operator == BinaryOperator.RangeInclusive ? SurtrComparison.Greater : SurtrComparison.GreaterOrEqual,
                SurtrValueTypeCode.Integer,
                end);

            PushLoop(step, end);
            Statement(loop.Body);
            PopTargets();

            Code.MarkLabel(step);
            Code.LoadLocal(variable);
            Code.LoadInt(1);
            Code.Add(SurtrValueTypeCode.Integer);
            Code.StoreLocal(variable);
            Code.Jump(top);
            Code.MarkLabel(end);
        }

        private void EmitForInIndexed(BoundForInStatement loop, bool isString)
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

            if (isString)
                Code.StrLen();
            else
                Code.ArrLen();

            Code.JumpIfCompare(SurtrComparison.GreaterOrEqual, SurtrValueTypeCode.Integer, end);

            Code.LoadLocal(source);
            Code.LoadLocal(index);

            if (isString)
                Code.StrGet();
            else
                Code.ArrGet();

            Code.StoreLocal(variable);

            PushLoop(step, end);
            Statement(loop.Body);
            PopTargets();

            Code.MarkLabel(step);
            Code.LoadLocal(index);
            Code.LoadInt(1);
            Code.Add(SurtrValueTypeCode.Integer);
            Code.StoreLocal(index);
            Code.Jump(top);
            Code.MarkLabel(end);
        }

        /// <summary>
        /// Emits a switch statement as a chain of comparisons against one saved subject.
        /// </summary>
        /// <remarks>
        /// Deliberately not <see cref="SurtrCodeEmitter.SwitchOn"/> yet. A jump table needs every
        /// label to be an integer key known here, and the general case — a string subject, an enum
        /// case read from a static — needs the lowerings Step 5 owes (§5's table). A chain is right
        /// for every subject family and is what a const fun's handful of arms would compile to
        /// anyway; picking the encoding is a Step 5 decision made once, not one made twice.
        /// </remarks>
        private void EmitSwitchStatement(BoundSwitchStatement @switch)
        {
            var subject = _method.DeclareLocal("$subject");
            var typeCode = TypeCodeOf(@switch.Subject.Type);

            Expression(@switch.Subject);
            Code.StoreLocal(subject);

            var end = Code.NewLabel();
            var bodies = new SurtrLabel[@switch.Sections.Count];
            SurtrLabel? fallback = null;

            for (int i = 0; i < @switch.Sections.Count; i++)
            {
                bodies[i] = Code.NewLabel();
                var section = @switch.Sections[i];

                if (section.IsDefault)
                {
                    fallback = bodies[i];
                    continue;
                }

                foreach (var label in section.Labels)
                {
                    Code.LoadLocal(subject);
                    Expression(label);
                    Code.JumpIfCompare(SurtrComparison.Equal, typeCode, bodies[i]);
                }
            }

            Code.Jump(fallback ?? end);

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

        private void EmitReturn(BoundReturnStatement @return)
        {
            if (@return.Value is null)
            {
                Code.ReturnVoid();
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
            _jumps.Add(new JumpTargets(breakTarget, continueTarget, isLoop, isLoop ? _pendingLabel : null));

            if (isLoop)
                _pendingLabel = null;
        }

        private void PopTargets() => _jumps.RemoveAt(_jumps.Count - 1);
        #endregion

        #region Expressions
        private void Expression(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundLiteralExpression literal:
                    EmitLiteral(literal);
                    return;

                case BoundLocalExpression local:
                    Code.LoadLocal(Slot(local.Local));
                    return;

                case BoundParameterExpression parameter:
                    Code.LoadLocal(_method.Parameter(parameter.Parameter.Ordinal));
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
                    EmitAssignment(assignment);
                    return;

                case BoundConditionalExpression conditional:
                    EmitConditional(conditional);
                    return;

                case BoundCallExpression call:
                    EmitCall(call, discardResult: false);
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
                    Code.TestInstanceOf(_descriptors.Emit(test.TestedType));
                    return;

                case BoundSwitchExpression @switch:
                    EmitSwitchExpression(@switch);
                    return;

                default:
                    throw Unsupported(expression.GetType().Name);
            }
        }

        private void EmitLiteral(BoundLiteralExpression literal)
        {
            switch (literal.Value)
            {
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
            Expression(conversion.Operand);

            var from = conversion.Operand.Type;
            var to = conversion.Type;

            switch (conversion.Conversion.Kind)
            {
                case ConversionKind.Identity:
                case ConversionKind.ImplicitNullable:
                case ConversionKind.ImplicitReference:
                    // Nothing to emit: a reference is its payload either way, and a primitive
                    // widening into its own nullable form keeps the same representation.
                    return;

                case ConversionKind.ImplicitNumeric:
                case ConversionKind.ExplicitNumeric:
                    Code.Convert(TypeCodeOf(from), TypeCodeOf(to));
                    return;

                case ConversionKind.ImplicitErasure:
                    // §1.11's first obligation: a primitive reaching a slot that only holds a
                    // reference has to become one. Box emits nothing for a reference already.
                    Code.Box(TypeCodeOf(from));
                    return;

                case ConversionKind.ExplicitErasure:
                {
                    // And its mirror: the checked cast, then the unwrap when what comes back out is
                    // a primitive.
                    var target = to.NonNullable;
                    Code.CastTo(_descriptors.Emit(target));

                    if (target.IsPrimitive && !target.IsVoid)
                        Code.Unbox();

                    return;
                }

                case ConversionKind.ExplicitReference:
                {
                    // `T?` narrowing back to `T` names the same class, so there is nothing to check
                    // that the cast would not accept anyway.
                    if (ReferenceEquals(from.NonNullable, to.NonNullable))
                        return;

                    Code.CastTo(_descriptors.Emit(to.NonNullable));
                    return;
                }

                default:
                    throw Unsupported($"a {conversion.Conversion.Kind} conversion");
            }
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

                case BinaryOperator.Compare:
                    EmitThreeWayCompare(binary);
                    return;
            }

            Expression(binary.Left);
            Expression(binary.Right);

            // The operand family, not the result's: `a < b` yields a bool but compares two ints.
            var operands = TypeCodeOf(binary.Left.Type);

            switch (binary.Operator)
            {
                case BinaryOperator.Add:
                    if (operands == SurtrValueTypeCode.String)
                        Code.StrCat();
                    else
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

        private void EmitNullCoalesce(BoundBinaryExpression binary)
        {
            var otherwise = Code.NewLabel();
            var end = Code.NewLabel();

            Expression(binary.Left);
            Code.Dup();
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
        /// subtraction give it exactly. On <c>string</c> it is a call to <c>compareTo</c> instead,
        /// which is one of the lowerings §4.8 records as the compiler's.
        /// </remarks>
        private void EmitThreeWayCompare(BoundBinaryExpression binary)
        {
            var operands = TypeCodeOf(binary.Left.Type);

            if (operands == SurtrValueTypeCode.String)
                throw Unsupported("'<=>' on a string, which lowers to string.compareTo");

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

                default:
                    throw Unsupported($"the unary operator '{unary.Operator}'");
            }
        }

        /// <summary>
        /// Emits an assignment, which is an expression and therefore leaves its value behind.
        /// </summary>
        /// <remarks>
        /// A compound assignment arrived expanded, so there is one form to emit rather than
        /// thirteen. The duplicate is what makes <c>a = b = 0</c> and a bare <c>x = 1;</c> the same
        /// shape — the statement form pops it, which the frame protocol makes free for a call and
        /// one byte otherwise.
        /// </remarks>
        private void EmitAssignment(BoundAssignmentExpression assignment)
        {
            switch (assignment.Target)
            {
                case BoundLocalExpression local:
                    Expression(assignment.Value);
                    Code.Dup();
                    Code.StoreLocal(Slot(local.Local));
                    return;

                case BoundParameterExpression parameter:
                    Expression(assignment.Value);
                    Code.Dup();
                    Code.StoreLocal(_method.Parameter(parameter.Parameter.Ordinal));
                    return;

                case BoundIndexExpression index:
                {
                    var value = _method.DeclareLocal("$value");

                    Expression(assignment.Value);
                    Code.StoreLocal(value);

                    Expression(index.Target);
                    Expression(index.Index);
                    Code.LoadLocal(value);

                    var target = index.Target.Type.NonNullable;
                    if (target.TypeKind == TypeSymbolKind.Array)
                        Code.ArrSet();
                    else if (target.TypeKind == TypeSymbolKind.Dictionary)
                        Code.DictSet();
                    else
                        throw Unsupported($"an indexed write to '{index.Target.Type.ToDisplayString()}'");

                    Code.LoadLocal(value);
                    return;
                }

                default:
                    throw Unsupported($"an assignment to {assignment.Target.GetType().Name}");
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
        /// Emits a call, to a function being emitted here or to one that arrived as metadata.
        /// </summary>
        /// <remarks>
        /// Both shapes end in <see cref="SurtrCodeEmitter.Call(SurtrMethodInfo, bool)"/> or its
        /// builder overload, which pick the dispatch opcode from the callee rather than from the
        /// call site — the interpreter reads where a call lands off the method it names, so there is
        /// no separate opcode for reaching host code and none is chosen here.
        /// </remarks>
        private void EmitCall(BoundCallExpression call, bool discardResult)
        {
            if (_functions.TryGetValue(call.Method, out var local))
            {
                if (call.Receiver is not null)
                    throw Unsupported($"a call on a receiver ('{call.Method.Name}')");

                foreach (var argument in call.Arguments)
                    Expression(argument);

                Code.Call(local, discardResult);
                return;
            }

            if (call.Method.ImportedFrom is not SurtrMethodInfo imported)
                throw Unsupported($"a call to '{call.Method.Name}', which is neither being emitted here nor imported");

            if (call.Receiver is not null)
                Expression(call.Receiver);

            foreach (var argument in call.Arguments)
                Expression(argument);

            if (call.IsVirtual)
                Code.CallVirtual(imported, discardResult);
            else
                Code.Call(imported, discardResult);
        }

        private void EmitIndexRead(BoundIndexExpression index)
        {
            Expression(index.Target);
            Expression(index.Index);

            var target = index.Target.Type.NonNullable;

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
            Code.PackArray(_descriptors.Emit(array.Type.NonNullable), array.Elements.Count);
        }

        private void EmitTupleLiteral(BoundTupleLiteralExpression tuple)
        {
            foreach (var element in tuple.Elements)
                Expression(element);

            Code.PackTuple(_descriptors.Emit(tuple.Type.NonNullable), tuple.Elements.Count);
        }

        private void EmitDictLiteral(BoundDictLiteralExpression dictionary)
        {
            foreach (var entry in dictionary.Entries)
            {
                Expression(entry.Key);
                Expression(entry.Value);
            }

            Code.PackDictionary(_descriptors.Emit(dictionary.Type.NonNullable), dictionary.Entries.Count);
        }

        /// <summary>
        /// Emits an interpolated string as a left-to-right chain of concatenations.
        /// </summary>
        /// <remarks>
        /// Every primitive already declares a native <c>toString</c>, so a non-string part is a call
        /// to that rather than a new opcode — which also means interpolation means exactly what
        /// writing <c>.toString()</c> means.
        /// </remarks>
        private void EmitInterpolatedString(BoundInterpolatedStringExpression interpolated)
        {
            if (interpolated.Parts.Count == 0)
            {
                Code.LoadString(string.Empty);
                return;
            }

            for (int i = 0; i < interpolated.Parts.Count; i++)
            {
                EmitAsString(interpolated.Parts[i]);

                if (i > 0)
                    Code.StrCat();
            }
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
            var typeCode = TypeCodeOf(@switch.Subject.Type);

            Expression(@switch.Subject);
            Code.StoreLocal(subject);

            var end = Code.NewLabel();
            var arms = new SurtrLabel[@switch.Arms.Count];
            SurtrLabel? fallback = null;

            for (int i = 0; i < @switch.Arms.Count; i++)
            {
                arms[i] = Code.NewLabel();
                var arm = @switch.Arms[i];

                if (arm.IsDefault)
                {
                    fallback = arms[i];
                    continue;
                }

                foreach (var value in arm.Values)
                {
                    Code.LoadLocal(subject);
                    Expression(value);
                    Code.JumpIfCompare(SurtrComparison.Equal, typeCode, arms[i]);
                }
            }

            if (fallback is null)
            {
                // Exhaustiveness was checked over an enum only, so anything else needs something to
                // produce when nothing matched.
                throw Unsupported("a switch expression with no 'else' arm");
            }

            Code.Jump(fallback.Value);

            for (int i = 0; i < arms.Length; i++)
            {
                Code.MarkLabel(arms[i]);
                Expression(@switch.Arms[i].Result);

                if (i < arms.Length - 1)
                    Code.Jump(end);
            }

            Code.MarkLabel(end);
        }
        #endregion

        #region Frame and types
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

        private SurtrEmitException Unsupported(string what)
            => new SurtrEmitException($"'{_symbol.Name}' uses {what}, which code generation does not lower yet.");
        #endregion
    }
}
