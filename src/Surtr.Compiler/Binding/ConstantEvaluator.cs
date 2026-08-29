#nullable enable

using Surtr.Compiler.Compilation;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Folds a call to a <c>const fun</c>, when one has been made available (§7.2).
    /// </summary>
    /// <param name="name">The function's name, as written at the call site.</param>
    /// <param name="arguments">Its arguments, already folded and in source order.</param>
    /// <param name="value">What the call evaluated to.</param>
    /// <returns>Whether the call folded.</returns>
    public delegate bool ConstCallFolder(string name, IReadOnlyList<object?> arguments, out object? value);

    /// <summary>
    /// Folds an enum expression — a case read or a call on the synthesized API (§2.3quater) — when
    /// the compiler has bound the enum. <c>null</c> arguments means a plain member read (a case);
    /// a non-null array means a call, with the folded receiver as <paramref name="receiverValue"/>.
    /// </summary>
    public delegate bool EnumConstantFolder(string enumName, object? receiverValue, string member, object?[]? arguments, out object? value);

    /// <summary>
    /// Folds an expression to a value at compile time, over syntax rather than over bound nodes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Over syntax because of where the first caller is: a declaration-level <c>const if</c> (§7.3)
    /// decides which <em>declarations exist</em>, so it has to be answered in the declaration phase
    /// — before any type has members and long before a body could be bound. Nothing is available at
    /// that point except literals, the constants the build defined (§7.4), and other <c>const</c>
    /// bindings whose initializers are themselves constant.
    /// </para>
    /// <para>
    /// A call to a <c>const fun</c> (§7.2) is the one thing here that is <em>not</em> folded by this
    /// type: it is handed to <see cref="CallFolder"/>, which runs the callee's real bytecode on a
    /// real VM so that compile-time and run-time semantics cannot drift. That folder only exists
    /// once bodies have been bound, which is exactly why a declaration-level <c>const if</c> cannot
    /// call one and everything later can.
    /// </para>
    /// </remarks>
    public sealed class ConstantEvaluator
    {
        private readonly Dictionary<string, object?> _values = new Dictionary<string, object?>(StringComparer.Ordinal);
        private readonly Dictionary<string, ExpressionSyntax> _pending = new Dictionary<string, ExpressionSyntax>(StringComparer.Ordinal);
        private readonly HashSet<string> _evaluating = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Creates an evaluator over the constants a build defines.</summary>
        public ConstantEvaluator(IReadOnlyDictionary<string, BuildConstant> buildConstants)
        {
            foreach (var pair in buildConstants)
                _values[pair.Key] = pair.Value.Value;
        }

        /// <summary>
        /// What folds a <c>const fun</c> call, once the compilation can run one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Null until bodies have been bound, which is the ordering §7.2's own note about the build
        /// pipeline predicts: folding a call needs the callee's <em>emitted body</em>, so a
        /// declaration-level <c>const if</c> — answered before any signature exists — cannot call
        /// one, and says so rather than guessing. Everything later can: a <c>const</c> initializer,
        /// a statement-level <c>const if</c>, and an ordinary call with constant arguments.
        /// </para>
        /// <para>
        /// It is a delegate rather than a reference to the folder because resolving the name at the
        /// call site is the binder's job, and pulling that in here would make this type depend on
        /// scopes it deliberately knows nothing about.
        /// </para>
        /// </remarks>
        public ConstCallFolder? CallFolder { get; set; }

        /// <summary>What folds an enum case read or a call on an enum's synthesized API (§2.3quater).</summary>
        /// <remarks>
        /// Null until the binder has bound the enums, which is what this folder needs to resolve the
        /// enum's cases. The evaluator itself stays type-less: it recognises the shape
        /// (<c>Suit.Hearts</c>, <c>Suit.of(...)</c>, <c>Suit.Hearts.toString()</c>) and hands the
        /// parts to this delegate, which knows the actual enums.
        /// </remarks>
        public EnumConstantFolder? EnumFolder { get; set; }

        /// <summary>The value of a named constant, folded on demand.</summary>
        public bool TryGetValue(string name, out object? value) => TryName(name, out value);

        /// <summary>
        /// Records a <c>const</c> binding, to be folded if something asks for it.
        /// </summary>
        /// <remarks>
        /// Lazily, because a <c>const</c> may be written after the <c>const if</c> that reads it,
        /// and because most of them are never asked for at all.
        /// </remarks>
        public void AddConstant(string name, ExpressionSyntax initializer)
        {
            if (!_values.ContainsKey(name))
                _pending[name] = initializer;
        }

        /// <summary>Folds an expression, or reports that it does not fold.</summary>
        public bool TryEvaluate(ExpressionSyntax syntax, out object? value)
        {
            switch (syntax)
            {
                case LiteralExpressionSyntax literal:
                    return TryLiteral(literal, out value);

                case IdentifierExpressionSyntax identifier:
                    return TryName(identifier.Name, out value);

                case UnaryExpressionSyntax unary:
                    return TryUnary(unary, out value);

                case BinaryExpressionSyntax binary:
                    return TryBinary(binary, out value);

                case ConditionalExpressionSyntax conditional:
                {
                    if (TryEvaluate(conditional.Condition, out object? condition) && condition is bool taken)
                        return TryEvaluate(taken ? conditional.WhenTrue : conditional.WhenFalse, out value);

                    break;
                }

                case CallExpressionSyntax call:
                    return TryCall(call, out value) || TryEnumCall(call, out value);

                case MemberAccessExpressionSyntax member:
                    return TryEnumMember(member, out value, out _);

                case DefinedExpressionSyntax defined:
                    return TryDefined(defined, out value);
            }

            value = null;
            return false;
        }

        /// <summary>
        /// Folds <c>defined(Path)</c> (§7.4): <see langword="true"/> when a build constant or a
        /// source <c>const</c> of that name exists, <see langword="false"/> otherwise - and crucially
        /// no error when it does not, which is the whole point of the operator. The direct reference
        /// <c>const if (Missing)</c> is a hard failure; <c>defined(Missing)</c> is the soft test.
        /// </summary>
        private bool TryDefined(DefinedExpressionSyntax syntax, out object? value)
        {
            value = IsDefined(syntax.Path);
            return true;
        }

        /// <summary>Whether a dotted name names a constant the build or the source supplied.</summary>
        private bool IsDefined(IReadOnlyList<string> path)
        {
            string name = string.Join(".", path);
            return _values.ContainsKey(name) || _pending.ContainsKey(name);
        }

        /// <summary>
        /// Folds a call, which is only ever a <c>const fun</c> with constant arguments (§7.2).
        /// </summary>
        /// <remarks>
        /// Every argument has to fold first: a <c>const fun</c> is folded exactly when it can be,
        /// and otherwise compiles and runs as an ordinary function — so failing here is not an error
        /// on its own. It becomes one only where the position <em>requires</em> a constant, which is
        /// where the caller reports it.
        /// </remarks>
        private bool TryCall(CallExpressionSyntax syntax, out object? value)
        {
            value = null;

            if (CallFolder is null || syntax.Callee is not IdentifierExpressionSyntax callee)
                return false;

            var arguments = new object?[syntax.Arguments.Count];
            for (int i = 0; i < arguments.Length; i++)
            {
                // A named argument would have to be reordered against a signature, which is
                // overload resolution's job and not something folding over syntax can do.
                if (syntax.Arguments[i].Name is not null)
                    return false;

                if (!TryEvaluate(syntax.Arguments[i].Value, out arguments[i]))
                    return false;
            }

            return CallFolder(callee.Name, arguments, out value);
        }

        /// <summary>
        /// Folds a call whose callee is a member access on an enum — <c>Suit.of(2)</c>,
        /// <c>Suit.Hearts.toString()</c>, <c>Suit.Hearts.equals(Suit.Hearts)</c> (§2.3quater).
        /// </summary>
        private bool TryEnumCall(CallExpressionSyntax call, out object? value)
        {
            value = null;

            if (EnumFolder is null || call.Callee is not MemberAccessExpressionSyntax access)
                return false;

            object? receiverValue = null;
            string enumName;

            if (access.Target is IdentifierExpressionSyntax root)
            {
                // A static member: `Suit.of(...)`.
                enumName = root.Name;
            }
            else if (access.Target is MemberAccessExpressionSyntax nested && TryEnumMember(nested, out receiverValue, out enumName))
            {
                // An instance member: `Suit.Hearts.toString()` — the receiver folds to the enum's value.
            }
            else
            {
                return false;
            }

            var arguments = new object?[call.Arguments.Count];
            for (int i = 0; i < arguments.Length; i++)
            {
                if (call.Arguments[i].Name is not null || !TryEvaluate(call.Arguments[i].Value, out arguments[i]))
                    return false;
            }

            return EnumFolder(enumName, receiverValue, access.Name, arguments, out value);
        }

        /// <summary>
        /// Folds a plain member access that names an enum case — <c>Suit.Hearts</c> — or a
        /// <c>.value</c> read on one. Reports the enum's name so an enclosing call can dispatch.
        /// </summary>
        private bool TryEnumMember(MemberAccessExpressionSyntax access, out object? value, out string enumName)
        {
            value = null;
            enumName = string.Empty;

            if (EnumFolder is null)
                return false;

            if (access.Target is IdentifierExpressionSyntax root)
            {
                enumName = root.Name;
                return EnumFolder(enumName, null, access.Name, arguments: null, out value);
            }

            // `Suit.Hearts.value` or `Suit.of(1).value`: the inner expression folds to the enum's
            // value, and `.value` is the value itself for a bare enum.
            if (access.Name == "value" && TryEvaluate(access.Target, out object? receiverValue) && receiverValue is not null)
            {
                enumName = string.Empty;
                value = receiverValue;
                return true;
            }

            return false;
        }

        /// <summary>Folds an expression that has to be a <c>bool</c>, as a condition does.</summary>
        public bool TryEvaluateCondition(ExpressionSyntax syntax, out bool result)
        {
            if (TryEvaluate(syntax, out object? value) && value is bool boolean)
            {
                result = boolean;
                return true;
            }

            result = false;
            return false;
        }

        private static bool TryLiteral(LiteralExpressionSyntax syntax, out object? value)
        {
            var payload = syntax.Literal.Payload;

            switch (payload.Kind)
            {
                case TokenPayloadKind.Integer: value = payload.AsInteger; return true;
                case TokenPayloadKind.Float: value = payload.AsFloat; return true;
                case TokenPayloadKind.Character: value = payload.AsCharacter; return true;
                case TokenPayloadKind.String: value = payload.AsString; return true;
            }

            switch (syntax.Literal.Type)
            {
                case TokenType.KeywordTrue: value = true; return true;
                case TokenType.KeywordFalse: value = false; return true;
                case TokenType.KeywordNull: value = null; return true;
                default: value = null; return false;
            }
        }

        private bool TryName(string name, out object? value)
        {
            if (_values.TryGetValue(name, out value))
                return true;

            if (!_pending.TryGetValue(name, out var initializer))
                return false;

            // A const defined in terms of itself has no value; saying so beats recursing forever.
            if (!_evaluating.Add(name))
            {
                value = null;
                return false;
            }

            try
            {
                if (!TryEvaluate(initializer, out value))
                    return false;

                _values[name] = value;
                return true;
            }
            finally
            {
                _evaluating.Remove(name);
            }
        }

        private bool TryUnary(UnaryExpressionSyntax syntax, out object? value)
        {
            value = null;

            if (!TryEvaluate(syntax.Operand, out object? operand))
                return false;

            switch (syntax.Operator)
            {
                case UnaryOperator.Not when operand is bool b: value = !b; return true;
                case UnaryOperator.Negate when operand is long i: value = -i; return true;
                case UnaryOperator.Negate when operand is double d: value = -d; return true;
                case UnaryOperator.Complement when operand is long c: value = ~c; return true;
                default: return false;
            }
        }

        private bool TryBinary(BinaryExpressionSyntax syntax, out object? value)
        {
            value = null;

            // Short-circuiting is folded rather than evaluated, so `false && somethingUnknown`
            // still folds - which is what makes a guarded condition usable.
            if (syntax.Operator is BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr)
            {
                if (!TryEvaluate(syntax.Left, out object? shortCircuit) || shortCircuit is not bool left)
                    return false;

                bool stops = syntax.Operator == BinaryOperator.LogicalAnd ? !left : left;
                if (stops)
                {
                    value = left;
                    return true;
                }

                if (!TryEvaluate(syntax.Right, out object? other) || other is not bool right)
                    return false;

                value = right;
                return true;
            }

            if (!TryEvaluate(syntax.Left, out object? l) || !TryEvaluate(syntax.Right, out object? r))
                return false;

            switch (syntax.Operator)
            {
                case BinaryOperator.Equal: value = Equals(l, r); return true;
                case BinaryOperator.NotEqual: value = !Equals(l, r); return true;
            }

            if (l is string || r is string)
            {
                if (syntax.Operator == BinaryOperator.Add)
                {
                    value = Text(l) + Text(r);
                    return true;
                }

                return false;
            }

            if (l is long li && r is long ri)
                return TryIntegers(syntax.Operator, li, ri, out value);

            if (ToDouble(l) is double ld && ToDouble(r) is double rd)
                return TryFloats(syntax.Operator, ld, rd, out value);

            return false;
        }

        private static bool TryIntegers(BinaryOperator @operator, long left, long right, out object? value)
        {
            value = null;

            switch (@operator)
            {
                case BinaryOperator.Add: value = left + right; return true;
                case BinaryOperator.Subtract: value = left - right; return true;
                case BinaryOperator.Multiply: value = left * right; return true;
                case BinaryOperator.Divide: if (right == 0) return false; value = left / right; return true;
                case BinaryOperator.Modulo: if (right == 0) return false; value = left % right; return true;
                case BinaryOperator.BitAnd: value = left & right; return true;
                case BinaryOperator.BitOr: value = left | right; return true;
                case BinaryOperator.BitXor: value = left ^ right; return true;
                case BinaryOperator.ShiftLeft: value = left << (int)(right & 31); return true;
                case BinaryOperator.ShiftRight: value = left >> (int)(right & 31); return true;
                case BinaryOperator.Less: value = left < right; return true;
                case BinaryOperator.LessEqual: value = left <= right; return true;
                case BinaryOperator.Greater: value = left > right; return true;
                case BinaryOperator.GreaterEqual: value = left >= right; return true;
                case BinaryOperator.Compare: value = (long)left.CompareTo(right); return true;
                default: return false;
            }
        }

        private static bool TryFloats(BinaryOperator @operator, double left, double right, out object? value)
        {
            value = null;

            switch (@operator)
            {
                case BinaryOperator.Add: value = left + right; return true;
                case BinaryOperator.Subtract: value = left - right; return true;
                case BinaryOperator.Multiply: value = left * right; return true;
                case BinaryOperator.Divide: value = left / right; return true;
                case BinaryOperator.Less: value = left < right; return true;
                case BinaryOperator.LessEqual: value = left <= right; return true;
                case BinaryOperator.Greater: value = left > right; return true;
                case BinaryOperator.GreaterEqual: value = left >= right; return true;
                default: return false;
            }
        }

        private static double? ToDouble(object? value) => value switch
        {
            long i => i,
            double d => d,
            char c => c,
            _ => null,
        };

        private static string Text(object? value) => value switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            _ => value.ToString() ?? string.Empty,
        };
    }
}
