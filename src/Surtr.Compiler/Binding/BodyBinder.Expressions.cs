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
        /// <summary>
        /// Binds an expression, optionally against the type the surrounding context expects.
        /// </summary>
        /// <remarks>
        /// The expected type is a hint, not a requirement: it is what lets an empty array literal,
        /// a bare <c>null</c> and a lambda with untyped parameters know what they are. Checking
        /// that the result actually fits happens in <see cref="Convert"/>, at the point that knows
        /// which diagnostic to give.
        /// </remarks>
        public BoundExpression BindExpression(ExpressionSyntax syntax, TypeSymbol? expected = null)
        {
            switch (syntax)
            {
                case LiteralExpressionSyntax literal: return BindLiteral(literal, expected);
                case InterpolatedStringExpressionSyntax interpolated: return BindInterpolatedString(interpolated);
                case IdentifierExpressionSyntax identifier: return BindIdentifier(identifier);
                case ThisExpressionSyntax @this: return BindThis(@this, isSuper: false);
                case SuperExpressionSyntax super: return BindThis(super, isSuper: true);
                case BinaryExpressionSyntax binary: return BindBinary(binary, expected);
                case UnaryExpressionSyntax unary: return BindUnary(unary);
                case AssignmentExpressionSyntax assignment: return BindAssignment(assignment);
                case ConditionalExpressionSyntax conditional: return BindConditional(conditional, expected);
                case CallExpressionSyntax call: return BindCall(call);
                case IndexExpressionSyntax index: return BindIndex(index);
                case MemberAccessExpressionSyntax member: return BindMemberAccess(member);
                case CastExpressionSyntax cast: return BindCast(cast);
                case TypeTestExpressionSyntax test: return BindTypeTest(test);
                case LambdaExpressionSyntax lambda: return BindLambda(lambda, expected);
                case ArrayLiteralExpressionSyntax array: return BindArrayLiteral(array, expected);
                case DictLiteralExpressionSyntax dictionary: return BindDictLiteral(dictionary, expected);
                case TupleLiteralExpressionSyntax tuple: return BindTupleLiteral(tuple, expected);
                case SwitchExpressionSyntax @switch: return BindSwitchExpression(@switch, expected);
                default: return Error(syntax);
            }
        }

        /// <summary>Binds an expression and converts it to what the context requires.</summary>
        private BoundExpression BindConverted(ExpressionSyntax syntax, TypeSymbol destination)
            => Convert(BindExpression(syntax, destination), destination, syntax.Span);

        #region Literals
        private BoundExpression BindLiteral(LiteralExpressionSyntax syntax, TypeSymbol? expected)
        {
            var payload = syntax.Literal.Payload;

            switch (payload.Kind)
            {
                case TokenPayloadKind.Integer:
                {
                    // An integer literal against a float context is the one place the widening is
                    // free: the value is known, so nothing converts at run time.
                    if (expected is not null && expected.NonNullable.SpecialType == SpecialType.Float)
                        return new BoundLiteralExpression(syntax, _factory.Float, (double)payload.AsInteger);

                    return new BoundLiteralExpression(syntax, _factory.Int, payload.AsInteger);
                }

                case TokenPayloadKind.Float:
                    return new BoundLiteralExpression(syntax, _factory.Float, payload.AsFloat);

                case TokenPayloadKind.Character:
                    return new BoundLiteralExpression(syntax, _factory.Char, payload.AsCharacter);

                case TokenPayloadKind.String:
                    return new BoundLiteralExpression(syntax, _factory.String, payload.AsString);
            }

            switch (syntax.Literal.Type)
            {
                case TokenType.KeywordTrue: return new BoundLiteralExpression(syntax, _factory.Bool, true);
                case TokenType.KeywordFalse: return new BoundLiteralExpression(syntax, _factory.Bool, false);

                case TokenType.KeywordNull:
                {
                    // Typed by its context, and checked here because this is where the context is
                    // known - once the literal carries the destination's type, nothing downstream
                    // can tell it apart from an ordinary value of it.
                    if (expected is null)
                        return new BoundLiteralExpression(syntax, _factory.ErrorType, null);

                    if (!_conversions.AcceptsNull(expected))
                    {
                        return Error(
                            syntax,
                            SurtrDiagnosticCode.NullNotAllowed,
                            $"'{expected.ToDisplayString()}' cannot hold null; write '{expected.ToDisplayString()}?' if it should.");
                    }

                    return new BoundLiteralExpression(syntax, expected, null);
                }

                default:
                    return Error(syntax);
            }
        }

        private BoundExpression BindInterpolatedString(InterpolatedStringExpressionSyntax syntax)
        {
            var parts = new BoundExpression[syntax.Parts.Count];
            for (int i = 0; i < parts.Length; i++)
                parts[i] = BindExpression(syntax.Parts[i]);

            return new BoundInterpolatedStringExpression(syntax, _factory.String, parts);
        }
        #endregion

        #region Names
        private BoundExpression BindIdentifier(IdentifierExpressionSyntax syntax)
        {
            var found = _values.Lookup(syntax.Name);

            if (found.Symbol is LocalSymbol local)
            {
                NoteCapture(local, syntax.Span);
                var read = new BoundLocalExpression(syntax, local);
                return Narrow(read, local, local.Type, syntax);
            }

            if (found.Symbol is ParameterSymbol parameter)
            {
                NoteCapture(parameter, syntax.Span);
                var read = new BoundParameterExpression(syntax, parameter);
                return Narrow(read, parameter, parameter.Type, syntax);
            }

            // §2.8: a singleton is reached by the declaration's own name, and that name is a type
            // name — so this is the one place a type resolves to a value.
            if (TryBindAsType(syntax, out var named) && Singleton(syntax, named) is BoundExpression instance)
                return instance;

            // Then the type this body is in, then the module. A bare name never reaches an
            // enclosing type's members through an instance it does not have.
            if (_containingType is not null && BindImplicitMember(syntax, _containingType, syntax.Name) is BoundExpression member)
                return member;

            if (BindModuleMember(syntax, syntax.Name) is BoundExpression moduleMember)
                return moduleMember;

            return Error(syntax, SurtrDiagnosticCode.UnresolvedName, $"'{syntax.Name}' does not name anything in scope.");
        }

        /// <summary>
        /// Reads a symbol as the enclosing condition proved it to be, rather than as it was
        /// declared.
        /// </summary>
        /// <remarks>
        /// The narrowing is a conversion node, not a different type on the read: the local is still
        /// declared nullable, and only this occurrence of it is known not to be.
        /// </remarks>
        private BoundExpression Narrow(BoundExpression read, Symbol symbol, TypeSymbol declared, SyntaxNode syntax)
        {
            var narrowed = TypeOf(symbol, declared);
            if (ReferenceEquals(narrowed, declared))
                return read;

            return new BoundConversionExpression(
                syntax, read, narrowed, Conversion.Of(ConversionKind.ExplicitReference), isExplicit: false);
        }

        private BoundExpression? BindImplicitMember(SyntaxNode syntax, NamedTypeSymbol type, string name)
        {
            if (_lookup.FindField(type, name) is FieldSymbol field)
                return new BoundFieldExpression(syntax, field.IsStatic ? null : ImplicitThis(syntax, type), field);

            if (_lookup.FindProperty(type, name) is PropertySymbol property)
                return new BoundPropertyExpression(syntax, property.IsStatic ? null : ImplicitThis(syntax, type), property);

            return null;
        }

        private BoundExpression? BindModuleMember(SyntaxNode syntax, string name)
        {
            // This module first, so a local declaration shadows an imported one rather than racing
            // it — the same order the type scope already puts them in.
            if (BindMemberOf(_module, syntax, name) is BoundExpression own)
                return own;

            foreach (var imported in _imported)
            {
                if (BindMemberOf(imported, syntax, name) is BoundExpression member)
                    return member;
            }

            return null;
        }

        private static BoundExpression? BindMemberOf(ModuleSymbol module, SyntaxNode syntax, string name)
        {
            foreach (var field in module.Fields)
            {
                if (string.Equals(field.Name, name, StringComparison.Ordinal))
                    return new BoundFieldExpression(syntax, null, field);
            }

            foreach (var property in module.Properties)
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                    return new BoundPropertyExpression(syntax, null, property);
            }

            return null;
        }

        private BoundExpression ImplicitThis(SyntaxNode syntax, NamedTypeSymbol type)
        {
            if (_method.IsStatic)
            {
                Report(
                    SurtrDiagnosticCode.NoInstanceInScope,
                    syntax.Span,
                    $"'{_method.Name}' is static, so there is no instance to read this member from.");
            }

            return This(syntax, type, isSuper: false);
        }

        /// <summary>
        /// Builds a receiver read, noting it when it happens inside a lambda.
        /// </summary>
        /// <remarks>
        /// Every <c>this</c> goes through here so the note cannot be forgotten at one of the sites
        /// that build one implicitly. Erring towards noting costs an unread upvalue; erring the
        /// other way loses the receiver a lifted body needs.
        /// </remarks>
        private BoundThisExpression This(SyntaxNode syntax, TypeSymbol type, bool isSuper)
        {
            NoteReceiverCapture();
            return new BoundThisExpression(syntax, type, isSuper);
        }

        private BoundExpression BindThis(ExpressionSyntax syntax, bool isSuper)
        {
            if (_containingType is null || _method.IsStatic)
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.NoInstanceInScope,
                    isSuper ? "'super' needs an instance method to be written in." : "'this' needs an instance method to be written in.");
            }

            if (!isSuper)
                return This(syntax, _containingType, isSuper: false);

            if (_containingType.BaseType is not NamedTypeSymbol baseType)
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.NoInstanceInScope,
                    $"'{_containingType.Name}' has no base class, so 'super' names nothing.");
            }

            return This(syntax, baseType, isSuper: true);
        }

        /// <summary>
        /// A <c>singleton</c>'s instance, read from the static that holds it (§2.8).
        /// </summary>
        /// <remarks>
        /// Null for every other kind, so a caller can ask before falling through to the static
        /// member rules — which is exactly the order §2.8 needs, since a singleton's members are
        /// instance members reached through a type name.
        /// </remarks>
        private BoundExpression? Singleton(SyntaxNode syntax, NamedTypeSymbol type)
            => type.TypeKind == TypeSymbolKind.Singleton && type.SingletonInstance is FieldSymbol instance
                ? new BoundFieldExpression(syntax, null, instance)
                : null;

        private BoundExpression BindMemberAccess(MemberAccessExpressionSyntax syntax)
        {
            // `Suit.Hearts` is a static member, not a field on a value called Suit.
            if (TryBindAsType(syntax.Target, out var staticType))
            {
                if (Singleton(syntax.Target, staticType) is BoundExpression instance)
                    return BindInstanceMember(syntax, instance);

                return BindStaticMember(syntax, staticType);
            }

            var receiver = BindExpression(syntax.Target);
            if (receiver.Type.IsError)
                return Error(syntax);

            if (syntax.IsNullConditional && !receiver.Type.IsNullable)
            {
                Report(
                    SurtrDiagnosticCode.CannotConvert,
                    syntax.Span,
                    $"'{receiver.Type.ToDisplayString()}' cannot be null, so '?.' has nothing to guard against.");
            }

            return BindInstanceMember(syntax, receiver);
        }

        private BoundExpression BindInstanceMember(MemberAccessExpressionSyntax syntax, BoundExpression receiver)
        {
            var lookupType = receiver.Type.NonNullable;

            if (_lookup.FindField(lookupType, syntax.Name) is FieldSymbol field)
                return Nullable(new BoundFieldExpression(syntax, field.IsStatic ? null : receiver, field), syntax);

            if (_lookup.FindProperty(lookupType, syntax.Name) is PropertySymbol property)
                return Nullable(new BoundPropertyExpression(syntax, property.IsStatic ? null : receiver, property), syntax);

            return Error(
                syntax,
                SurtrDiagnosticCode.UnresolvedMember,
                $"'{receiver.Type.ToDisplayString()}' has no member called '{syntax.Name}'.");

            // `a?.b` yields null when `a` is, so the whole expression is nullable whatever `b` is.
            BoundExpression Nullable(BoundExpression bound, MemberAccessExpressionSyntax access)
                => access.IsNullConditional && !bound.Type.IsNullable
                    ? new BoundConversionExpression(
                        access, bound, bound.Type.Nullable, Conversion.Of(ConversionKind.ImplicitNullable), isExplicit: false)
                    : bound;
        }

        private BoundExpression BindStaticMember(MemberAccessExpressionSyntax syntax, NamedTypeSymbol type)
        {
            if (_lookup.FindField(type, syntax.Name) is FieldSymbol field && field.IsStatic)
                return new BoundFieldExpression(syntax, null, field);

            if (_lookup.FindProperty(type, syntax.Name) is PropertySymbol property && property.IsStatic)
                return new BoundPropertyExpression(syntax, null, property);

            return Error(
                syntax,
                SurtrDiagnosticCode.UnresolvedMember,
                $"'{type.ToDisplayString()}' has no static member called '{syntax.Name}'.");
        }
        #endregion

        #region Operators
        private BoundExpression BindBinary(BinaryExpressionSyntax syntax, TypeSymbol? expected)
        {
            if (syntax.Operator == BinaryOperator.NullCoalesce)
                return BindNullCoalesce(syntax, expected);

            // `x == null` types the literal from the other side, which is the one context a null
            // has here — and without it the comparison would be against the error type.
            var left = BindExpression(syntax.Left);
            var right = BindExpression(syntax.Right, IsNullLiteral(syntax.Right) ? left.Type.Nullable : null);

            if (IsNullLiteral(syntax.Left) && !right.Type.IsError)
                left = BindExpression(syntax.Left, right.Type.Nullable);

            if (left.Type.IsError || right.Type.IsError)
                return Error(syntax);

            var result = ResolveBinary(syntax, syntax.Operator, ref left, ref right);
            if (result is null)
            {
                // Nothing built in fits, so the operand types have to declare it themselves.
                if (TryBindUserOperator(syntax, syntax.Operator, left, right) is BoundExpression user)
                    return user;

                return Error(
                    syntax,
                    SurtrDiagnosticCode.OperatorNotDefined,
                    $"'{syntax.Operator}' is not defined for '{left.Type.ToDisplayString()}' and '{right.Type.ToDisplayString()}'.");
            }

            return new BoundBinaryExpression(syntax, syntax.Operator, left, right, result);
        }

        /// <summary>
        /// Works out what a built-in binary operator produces, widening the operands if it needs to.
        /// </summary>
        /// <remarks>
        /// The one interesting case is §5.7's: mixing an <c>int</c> with a <c>float</c> promotes the
        /// whole expression, so <c>7 / 2</c> truncates and <c>7 / 2.0</c> does not.
        /// </remarks>
        private TypeSymbol? ResolveBinary(SyntaxNode syntax, BinaryOperator @operator, ref BoundExpression left, ref BoundExpression right)
        {
            var l = left.Type.NonNullable;
            var r = right.Type.NonNullable;

            switch (@operator)
            {
                case BinaryOperator.Add:
                case BinaryOperator.Subtract:
                case BinaryOperator.Multiply:
                case BinaryOperator.Divide:
                case BinaryOperator.Modulo:
                {
                    // `+` on strings concatenates, and anything may be appended to a string.
                    if (@operator == BinaryOperator.Add
                        && (l.SpecialType == SpecialType.String || r.SpecialType == SpecialType.String))
                    {
                        return _factory.String;
                    }

                    return Arithmetic(syntax, ref left, ref right, l, r);
                }

                case BinaryOperator.ShiftLeft:
                case BinaryOperator.ShiftRight:
                case BinaryOperator.UnsignedShiftRight:
                case BinaryOperator.BitAnd:
                case BinaryOperator.BitOr:
                case BinaryOperator.BitXor:
                {
                    if (l.SpecialType == SpecialType.Int && r.SpecialType == SpecialType.Int)
                        return _factory.Int;

                    if (l.SpecialType == SpecialType.Bool && r.SpecialType == SpecialType.Bool
                        && @operator is BinaryOperator.BitAnd or BinaryOperator.BitOr or BinaryOperator.BitXor)
                    {
                        return _factory.Bool;
                    }

                    return null;
                }

                case BinaryOperator.LogicalAnd:
                case BinaryOperator.LogicalOr:
                    return l.SpecialType == SpecialType.Bool && r.SpecialType == SpecialType.Bool ? _factory.Bool : null;

                case BinaryOperator.Less:
                case BinaryOperator.LessEqual:
                case BinaryOperator.Greater:
                case BinaryOperator.GreaterEqual:
                {
                    if (Arithmetic(syntax, ref left, ref right, l, r) is not null)
                        return _factory.Bool;

                    // §5.7 lowers the relational operators on strings to compareTo, so they are
                    // defined here even though no opcode orders a string.
                    return l.SpecialType == SpecialType.String && r.SpecialType == SpecialType.String
                        ? _factory.Bool
                        : null;
                }

                case BinaryOperator.Compare:
                {
                    if (Arithmetic(syntax, ref left, ref right, l, r) is not null)
                        return _factory.Int;

                    return l.SpecialType == SpecialType.String && r.SpecialType == SpecialType.String
                        ? _factory.Int
                        : null;
                }

                case BinaryOperator.Equal:
                case BinaryOperator.NotEqual:
                {
                    if (Arithmetic(syntax, ref left, ref right, l, r) is not null)
                        return _factory.Bool;

                    // Anything compares to anything it could be, which includes null against a
                    // nullable and either direction of an upcast.
                    if (_conversions.IsAssignable(left.Type, right.Type) || _conversions.IsAssignable(right.Type, left.Type))
                        return _factory.Bool;

                    return null;
                }

                case BinaryOperator.ReferenceEqual:
                case BinaryOperator.ReferenceNotEqual:
                    return left.Type.IsReferenceType && right.Type.IsReferenceType ? _factory.Bool : null;

                case BinaryOperator.Range:
                case BinaryOperator.RangeInclusive:
                    return l.SpecialType == SpecialType.Int && r.SpecialType == SpecialType.Int ? _factory.Range : null;

                default:
                    return null;
            }
        }

        private TypeSymbol? Arithmetic(SyntaxNode syntax, ref BoundExpression left, ref BoundExpression right, TypeSymbol l, TypeSymbol r)
        {
            bool leftNumeric = l.SpecialType is SpecialType.Int or SpecialType.Float or SpecialType.Char;
            bool rightNumeric = r.SpecialType is SpecialType.Int or SpecialType.Float or SpecialType.Char;

            if (!leftNumeric || !rightNumeric)
                return null;

            if (l.SpecialType == SpecialType.Float || r.SpecialType == SpecialType.Float)
            {
                left = Widen(left, _factory.Float, syntax);
                right = Widen(right, _factory.Float, syntax);
                return _factory.Float;
            }

            if (l.SpecialType != r.SpecialType)
                return null;

            return l.SpecialType == SpecialType.Char ? _factory.Char : _factory.Int;
        }

        private BoundExpression Widen(BoundExpression expression, TypeSymbol destination, SyntaxNode syntax)
        {
            if (ReferenceEquals(expression.Type, destination))
                return expression;

            var conversion = _conversions.Classify(expression.Type, destination);
            return conversion.IsImplicit
                ? new BoundConversionExpression(syntax, expression, destination, conversion, isExplicit: false)
                : expression;
        }

        private BoundExpression? TryBindUserOperator(SyntaxNode syntax, BinaryOperator @operator, BoundExpression left, BoundExpression right)
        {
            string? name = OperatorNames.TryGetSymbol(TokenFor(@operator), out _)
                ? OperatorNames.For(TokenFor(@operator), 2)
                : null;

            if (name is null)
                return null;

            var candidates = new List<MethodSymbol>();
            candidates.AddRange(_lookup.FindMethods(left.Type, name));
            candidates.AddRange(_lookup.FindMethods(right.Type, name));

            if (candidates.Count == 0)
                return null;

            var result = _overloads.Resolve(
                candidates,
                new[] { new ArgumentInfo(left.Type), new ArgumentInfo(right.Type) });

            if (!result.IsResolved)
                return null;

            var method = result.Method!;
            return new BoundCallExpression(
                syntax,
                null,
                method,
                new[]
                {
                    Convert(left, method.Parameters[0].Type, syntax.Span),
                    Convert(right, method.Parameters[1].Type, syntax.Span),
                },
                isVirtual: false);
        }

        private static TokenType TokenFor(BinaryOperator @operator) => @operator switch
        {
            BinaryOperator.Add => TokenType.Plus,
            BinaryOperator.Subtract => TokenType.Minus,
            BinaryOperator.Multiply => TokenType.Star,
            BinaryOperator.Divide => TokenType.Slash,
            BinaryOperator.Modulo => TokenType.Percent,
            BinaryOperator.BitAnd => TokenType.Ampersand,
            BinaryOperator.BitOr => TokenType.Pipe,
            BinaryOperator.BitXor => TokenType.Caret,
            BinaryOperator.ShiftLeft => TokenType.ShiftLeft,
            BinaryOperator.ShiftRight => TokenType.ShiftRight,
            BinaryOperator.UnsignedShiftRight => TokenType.UnsignedShiftRight,
            BinaryOperator.Equal or BinaryOperator.NotEqual => TokenType.Equal,
            BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater
                or BinaryOperator.GreaterEqual or BinaryOperator.Compare => TokenType.Spaceship,
            _ => TokenType.EndOfFile,
        };

        private BoundExpression BindNullCoalesce(BinaryExpressionSyntax syntax, TypeSymbol? expected)
        {
            var left = BindExpression(syntax.Left, expected);
            var right = BindExpression(syntax.Right, expected ?? left.Type.NonNullable);

            if (left.Type.IsError || right.Type.IsError)
                return Error(syntax);

            // The whole point is that the result is not null, so the left's non-nullable form is
            // what the two sides have to agree on.
            var result = CommonType(left.Type.NonNullable, right.Type, syntax, "??");
            if (result is null)
                return Error(syntax);

            // The left is known non-null on the branch that uses it, so the narrowing is stated
            // rather than checked - asking Convert would reject exactly the case `??` exists for.
            var narrowed = ReferenceEquals(left.Type, result)
                ? left
                : (BoundExpression)new BoundConversionExpression(
                    syntax.Left, left, result, Conversion.Of(ConversionKind.ExplicitReference), isExplicit: false);

            return new BoundConditionalExpression(
                syntax,
                new BoundBinaryExpression(syntax, BinaryOperator.NotEqual, left, new BoundLiteralExpression(syntax, left.Type, null), _factory.Bool),
                narrowed,
                Convert(right, result, syntax.Right.Span),
                result);
        }

        private BoundExpression BindUnary(UnaryExpressionSyntax syntax)
        {
            var operand = BindExpression(syntax.Operand);
            if (operand.Type.IsError)
                return Error(syntax);

            var type = operand.Type.NonNullable;

            switch (syntax.Operator)
            {
                case UnaryOperator.Negate:
                    if (type.SpecialType is SpecialType.Int or SpecialType.Float)
                        return new BoundUnaryExpression(syntax, syntax.Operator, operand, type);
                    break;

                case UnaryOperator.Not:
                    if (type.SpecialType == SpecialType.Bool)
                        return new BoundUnaryExpression(syntax, syntax.Operator, operand, _factory.Bool);
                    break;

                case UnaryOperator.Complement:
                    if (type.SpecialType == SpecialType.Int)
                        return new BoundUnaryExpression(syntax, syntax.Operator, operand, _factory.Int);
                    break;

                case UnaryOperator.PreIncrement:
                case UnaryOperator.PreDecrement:
                case UnaryOperator.PostIncrement:
                case UnaryOperator.PostDecrement:
                {
                    if (!operand.IsAssignable)
                    {
                        return Error(
                            syntax,
                            SurtrDiagnosticCode.NotAssignable,
                            "'++' and '--' assign back to their operand, so it has to be assignable.");
                    }

                    if (type.SpecialType is SpecialType.Int or SpecialType.Float or SpecialType.Char)
                        return new BoundUnaryExpression(syntax, syntax.Operator, operand, type);

                    break;
                }

                case UnaryOperator.NullAssert:
                    // `!!` asserts, so the type it produces is the non-nullable one whether or not
                    // the assertion turns out to hold.
                    return new BoundConversionExpression(
                        syntax, operand, type, Conversion.Of(ConversionKind.ExplicitReference), isExplicit: true);
            }

            if (TryBindUserUnary(syntax, operand) is BoundExpression user)
                return user;

            return Error(
                syntax,
                SurtrDiagnosticCode.OperatorNotDefined,
                $"'{syntax.Operator}' is not defined for '{operand.Type.ToDisplayString()}'.");
        }

        private BoundExpression? TryBindUserUnary(UnaryExpressionSyntax syntax, BoundExpression operand)
        {
            var token = syntax.Operator switch
            {
                UnaryOperator.Negate => TokenType.Minus,
                UnaryOperator.Not => TokenType.LogicalNot,
                UnaryOperator.Complement => TokenType.Tilde,
                UnaryOperator.PreIncrement or UnaryOperator.PostIncrement => TokenType.Increment,
                UnaryOperator.PreDecrement or UnaryOperator.PostDecrement => TokenType.Decrement,
                _ => TokenType.EndOfFile,
            };

            if (!OperatorNames.TryGetSymbol(token, out _))
                return null;

            var candidates = _lookup.FindMethods(operand.Type, OperatorNames.For(token, 1));
            var result = _overloads.Resolve(candidates, new[] { new ArgumentInfo(operand.Type) });

            return result.IsResolved
                ? new BoundCallExpression(syntax, null, result.Method!, new[] { operand }, isVirtual: false)
                : null;
        }
        #endregion

        #region Assignment
        private BoundExpression BindAssignment(AssignmentExpressionSyntax syntax)
        {
            var target = BindExpression(syntax.Target);

            if (target.Type.IsError)
            {
                BindExpression(syntax.Value);
                return Error(syntax);
            }

            if (!target.IsAssignable && !IsInitialisingWrite(target))
            {
                BindExpression(syntax.Value);
                return Error(
                    syntax,
                    SurtrDiagnosticCode.NotAssignable,
                    "This cannot be assigned to; it is a value, a 'let', or a property with no setter.");
            }

            if (syntax.Operator == AssignmentOperator.Assign)
                return new BoundAssignmentExpression(syntax, target, BindConverted(syntax.Value, target.Type));

            // A compound assignment is expanded here, so nothing downstream needs a second form of
            // assignment or a second table of operators.
            var value = BindExpression(syntax.Value);
            if (value.Type.IsError)
                return Error(syntax);

            if (syntax.Operator == AssignmentOperator.NullCoalesce)
            {
                return new BoundAssignmentExpression(syntax, target, Convert(value, target.Type, syntax.Value.Span));
            }

            var @operator = Expand(syntax.Operator);
            var left = target;
            var right = value;

            var result = ResolveBinary(syntax, @operator, ref left, ref right);
            if (result is null)
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.OperatorNotDefined,
                    $"'{@operator}' is not defined for '{target.Type.ToDisplayString()}' and '{value.Type.ToDisplayString()}'.");
            }

            var combined = new BoundBinaryExpression(syntax, @operator, left, right, result);
            return new BoundAssignmentExpression(syntax, target, Convert(combined, target.Type, syntax.Span));
        }

        /// <summary>
        /// Whether a write to a <c>let</c> field is the one that gives it its value.
        /// </summary>
        /// <remarks>
        /// §3.2 makes a <c>let</c> field write-once rather than never-written: it is assigned either
        /// by an initializer or by a constructor, and a value class has no other way to be built at
        /// all. What makes it safe is that the receiver has to be the instance being constructed —
        /// a constructor writing <em>another</em> object's <c>let</c> is the case this excludes.
        /// </remarks>
        private bool IsInitialisingWrite(BoundExpression target)
        {
            if (_method.Role != MethodRole.Constructor
                || target is not BoundFieldExpression { Field.IsReadOnly: true, Field.IsStatic: false } field)
            {
                return false;
            }

            return field.Receiver is BoundThisExpression { IsSuper: false }
                && ReferenceEquals(field.Field.ContainingSymbol, _containingType);
        }

        private static BinaryOperator Expand(AssignmentOperator @operator) => @operator switch
        {
            AssignmentOperator.Add => BinaryOperator.Add,
            AssignmentOperator.Subtract => BinaryOperator.Subtract,
            AssignmentOperator.Multiply => BinaryOperator.Multiply,
            AssignmentOperator.Divide => BinaryOperator.Divide,
            AssignmentOperator.Modulo => BinaryOperator.Modulo,
            AssignmentOperator.BitAnd => BinaryOperator.BitAnd,
            AssignmentOperator.BitOr => BinaryOperator.BitOr,
            AssignmentOperator.BitXor => BinaryOperator.BitXor,
            AssignmentOperator.ShiftLeft => BinaryOperator.ShiftLeft,
            AssignmentOperator.ShiftRight => BinaryOperator.ShiftRight,
            _ => BinaryOperator.UnsignedShiftRight,
        };
        #endregion

        #region Calls
        private BoundExpression BindCall(CallExpressionSyntax syntax)
        {
            // `Vec2(1.0, 2.0)` constructs; there is no `new`.
            if (TryBindAsType(syntax.Callee, out var constructed))
                return BindObjectCreation(syntax, constructed);

            BoundExpression? receiver = null;
            string name;
            bool isVirtual = true;

            switch (syntax.Callee)
            {
                case IdentifierExpressionSyntax identifier:
                {
                    name = identifier.Name;

                    // A local holding a closure is invoked, not looked up as a method.
                    if (_values.Lookup(name).Symbol is Symbol value && value is LocalSymbol or ParameterSymbol)
                        return BindClosureInvocation(syntax, BindExpression(syntax.Callee));

                    receiver = _containingType is not null && !_method.IsStatic
                        ? This(syntax, _containingType, isSuper: false)
                        : null;

                    break;
                }

                case MemberAccessExpressionSyntax member:
                {
                    name = member.Name;

                    if (TryBindAsType(member.Target, out var staticOwner))
                    {
                        // A singleton's members are instance members reached through a type name
                        // (§2.8), so the instance is the receiver rather than nothing.
                        return Singleton(member.Target, staticOwner) is BoundExpression instance
                            ? BindMethodCall(syntax, instance, staticOwner, name, isVirtual: false)
                            : BindMethodCall(syntax, null, staticOwner, name, isVirtual: false);
                    }

                    receiver = BindExpression(member.Target);
                    isVirtual = receiver is not BoundThisExpression { IsSuper: true };
                    break;
                }

                default:
                    return BindClosureInvocation(syntax, BindExpression(syntax.Callee));
            }

            var owner = receiver?.Type.NonNullable ?? (TypeSymbol?)_containingType;

            if (owner is not null && _lookup.FindMethods(owner, name).Count > 0)
                return BindMethodCall(syntax, receiver, owner, name, isVirtual);

            if (DeclaresMethod(_module, name))
                return BindModuleCall(syntax, _module, name);

            foreach (var imported in _imported)
            {
                if (DeclaresMethod(imported, name))
                    return BindModuleCall(syntax, imported, name);
            }

            return Error(
                syntax,
                SurtrDiagnosticCode.UnresolvedName,
                $"'{name}' does not name a method in scope.");
        }

        private BoundExpression BindMethodCall(
            CallExpressionSyntax syntax,
            BoundExpression? receiver,
            TypeSymbol owner,
            string name,
            bool isVirtual)
        {
            var candidates = _lookup.FindMethods(owner, name);
            return Complete(syntax, receiver, candidates, name, isVirtual);
        }

        private static bool DeclaresMethod(ModuleSymbol module, string name)
        {
            foreach (var method in module.Methods)
            {
                if (string.Equals(method.Name, name, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private BoundExpression BindModuleCall(CallExpressionSyntax syntax, ModuleSymbol module, string name)
        {
            var candidates = new List<MethodSymbol>();
            foreach (var method in module.Methods)
            {
                if (string.Equals(method.Name, name, StringComparison.Ordinal))
                    candidates.Add(method);
            }

            return Complete(syntax, null, candidates, name, isVirtual: false);
        }

        private BoundExpression Complete(
            CallExpressionSyntax syntax,
            BoundExpression? receiver,
            IReadOnlyList<MethodSymbol> candidates,
            string name,
            bool isVirtual)
        {
            var arguments = new BoundExpression[syntax.Arguments.Count];
            var infos = new ArgumentInfo[syntax.Arguments.Count];

            for (int i = 0; i < arguments.Length; i++)
            {
                arguments[i] = BindExpression(syntax.Arguments[i].Value);
                infos[i] = new ArgumentInfo(arguments[i].Type, syntax.Arguments[i].Name);
            }

            var result = _overloads.Resolve(candidates, infos);

            switch (result.Status)
            {
                case OverloadStatus.Resolved:
                    break;

                case OverloadStatus.NoCandidates:
                    return Error(syntax, SurtrDiagnosticCode.UnresolvedName, $"'{name}' does not name a method in scope.");

                case OverloadStatus.Ambiguous:
                    return Error(
                        syntax,
                        SurtrDiagnosticCode.UnresolvedCall,
                        $"The call to '{name}' matches {result.Candidates.Count} overloads equally well; a cast has to say which.");

                default:
                    return Error(
                        syntax,
                        SurtrDiagnosticCode.UnresolvedCall,
                        $"No overload of '{name}' takes these arguments.");
            }

            var method = result.Method!;
            var ordered = OrderArguments(syntax, syntax.Arguments, method, arguments);

            // A call on a sealed type or through `super` can be bound directly, which is the
            // devirtualisation §2.2 calls out as a static fact rather than a guess.
            bool virtualCall = isVirtual
                && method.Dispatch != MethodDispatch.Direct
                && !(receiver?.Type.NonNullable is NamedTypeSymbol { IsSealed: true });

            return new BoundCallExpression(syntax, method.IsStatic ? null : receiver, method, ordered, virtualCall);
        }

        /// <summary>
        /// Puts arguments in parameter order, filling defaults and collecting varargs.
        /// </summary>
        /// <remarks>
        /// Done at binding rather than at emit so the tree carries one shape: by the time anything
        /// reads a call, its arguments line up with its parameters one for one.
        /// </remarks>
        private IReadOnlyList<BoundExpression> OrderArguments(
            SyntaxNode syntax,
            IReadOnlyList<ArgumentSyntax> written,
            MethodSymbol method,
            IReadOnlyList<BoundExpression> arguments)
        {
            var parameters = method.Parameters;
            if (parameters.Count == 0)
                return NoArguments;

            var ordered = new BoundExpression?[parameters.Count];
            var varargs = new List<BoundExpression>();
            int varargIndex = -1;

            for (int i = 0; i < parameters.Count; i++)
            {
                if (parameters[i].IsVararg)
                    varargIndex = i;
            }

            int positional = 0;
            for (int i = 0; i < arguments.Count; i++)
            {
                string? name = written[i].Name;

                if (name is not null)
                {
                    for (int p = 0; p < parameters.Count; p++)
                    {
                        if (string.Equals(parameters[p].Name, name, StringComparison.Ordinal))
                        {
                            ordered[p] = Convert(arguments[i], parameters[p].Type, written[i].Span);
                            break;
                        }
                    }

                    continue;
                }

                int target = positional++;

                if (varargIndex >= 0 && target >= varargIndex)
                {
                    var vararg = parameters[varargIndex];

                    if (target == varargIndex
                        && arguments.Count - i == 1
                        && _conversions.IsAssignable(arguments[i].Type, vararg.Type))
                    {
                        ordered[varargIndex] = Convert(arguments[i], vararg.Type, written[i].Span);
                        continue;
                    }

                    var element = ((ArrayTypeSymbol)vararg.Type).ElementType;
                    varargs.Add(Convert(arguments[i], element, written[i].Span));
                    continue;
                }

                if (target < parameters.Count)
                    ordered[target] = Convert(arguments[i], parameters[target].Type, written[i].Span);
            }

            if (varargIndex >= 0 && ordered[varargIndex] is null)
            {
                ordered[varargIndex] = new BoundArrayLiteralExpression(
                    syntax, parameters[varargIndex].Type, varargs.ToArray());
            }

            var result = new BoundExpression[parameters.Count];
            for (int i = 0; i < result.Length; i++)
                result[i] = ordered[i] ?? Omitted(syntax, parameters[i]);

            return result;
        }

        /// <summary>
        /// The value an argument nothing filled takes: the parameter's default, as a literal.
        /// </summary>
        /// <remarks>
        /// Materialised at the call site rather than looked up at run time, which is what §4.8 means
        /// by the interpreter trusting the arguments it is given — a call opcode carries a count and
        /// nothing else, so a defaulted argument has to be a real value on the stack.
        /// </remarks>
        private BoundExpression Omitted(SyntaxNode syntax, ParameterSymbol parameter)
        {
            if (!parameter.HasDefaultValue)
                return new BoundErrorExpression(syntax, parameter.Type);

            // An integer default reaching a float parameter widens here, exactly as a written
            // literal would: the value is known, so nothing converts at run time.
            if (parameter.DefaultValue is long widened && parameter.Type.NonNullable.SpecialType == SpecialType.Float)
                return new BoundLiteralExpression(syntax, parameter.Type, (double)widened);

            return new BoundLiteralExpression(syntax, parameter.Type, parameter.DefaultValue);
        }

        private BoundExpression BindObjectCreation(CallExpressionSyntax syntax, NamedTypeSymbol type)
            => BindObjectCreation(syntax, syntax.Arguments, type);

        /// <summary>
        /// Binds a construction from an argument list rather than from a call.
        /// </summary>
        /// <remarks>
        /// The list is a parameter because an enum case is a construction too (§2.4) and is written
        /// with no callee at all — <c>Hearts(1)</c> names its own enum, which nothing in the source
        /// repeats.
        /// </remarks>
        private BoundExpression BindObjectCreation(
            SyntaxNode syntax,
            IReadOnlyList<ArgumentSyntax> written,
            NamedTypeSymbol type)
        {
            if (type.IsAbstract)
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.NotSupportedOnType,
                    $"'{type.Name}' is abstract and cannot be constructed.");
            }

            var arguments = new BoundExpression[written.Count];
            var infos = new ArgumentInfo[written.Count];

            for (int i = 0; i < arguments.Length; i++)
            {
                arguments[i] = BindExpression(written[i].Value);
                infos[i] = new ArgumentInfo(arguments[i].Type, written[i].Name);
            }

            var constructors = new List<MethodSymbol>();
            foreach (var member in _lookup.MembersOf(type))
            {
                if (member is MethodSymbol method && method.Role == MethodRole.Constructor)
                    constructors.Add(method);
            }

            if (constructors.Count == 0)
            {
                if (arguments.Length == 0)
                    return new BoundObjectCreationExpression(syntax, type, null, NoArguments);

                return Error(
                    syntax,
                    SurtrDiagnosticCode.UnresolvedCall,
                    $"'{type.Name}' declares no constructor, so it takes no arguments.");
            }

            var result = _overloads.Resolve(constructors, infos);
            if (!result.IsResolved)
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.UnresolvedCall,
                    $"No constructor of '{type.Name}' takes these arguments.");
            }

            return new BoundObjectCreationExpression(
                syntax, type, result.Method, OrderArguments(syntax, written, result.Method!, arguments));
        }

        private BoundExpression BindClosureInvocation(CallExpressionSyntax syntax, BoundExpression callee)
        {
            if (callee.Type.NonNullable is not ClosureTypeSymbol closure)
            {
                foreach (var argument in syntax.Arguments)
                    BindExpression(argument.Value);

                return callee.Type.IsError
                    ? Error(syntax)
                    : Error(
                        syntax,
                        SurtrDiagnosticCode.NotSupportedOnType,
                        $"'{callee.Type.ToDisplayString()}' is not something that can be called.");
            }

            if (syntax.Arguments.Count != closure.ParameterTypes.Count)
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.UnresolvedCall,
                    $"'{closure.ToDisplayString()}' takes {closure.ParameterTypes.Count} argument(s), not {syntax.Arguments.Count}.");
            }

            var arguments = new BoundExpression[syntax.Arguments.Count];
            for (int i = 0; i < arguments.Length; i++)
                arguments[i] = BindConverted(syntax.Arguments[i].Value, closure.ParameterTypes[i]);

            return new BoundClosureInvocationExpression(syntax, callee, arguments, closure.ReturnType);
        }
        #endregion

        #region Indexing, casts and tests
        private BoundExpression BindIndex(IndexExpressionSyntax syntax)
        {
            var target = BindExpression(syntax.Target);
            var index = BindExpression(syntax.Index);

            if (target.Type.IsError || index.Type.IsError)
                return Error(syntax);

            switch (target.Type.NonNullable)
            {
                case ArrayTypeSymbol array:
                    return new BoundIndexExpression(syntax, target, Convert(index, _factory.Int, syntax.Index.Span), array.ElementType);

                case DictionaryTypeSymbol dictionary:
                    return new BoundIndexExpression(
                        syntax, target, Convert(index, dictionary.KeyType, syntax.Index.Span), dictionary.ValueType);

                case NamedTypeSymbol named when named.SpecialType == SpecialType.String:
                    return new BoundIndexExpression(syntax, target, Convert(index, _factory.Int, syntax.Index.Span), _factory.Char);

                case TupleTypeSymbol tuple:
                    return BindTupleIndex(syntax, target, index, tuple);
            }

            // Anything else has to declare `operator[]`.
            var candidates = _lookup.FindMethods(target.Type, OperatorNames.For(TokenType.LeftBracket, 1));
            var result = _overloads.Resolve(candidates, new[] { new ArgumentInfo(index.Type) });

            if (result.IsResolved)
            {
                return new BoundCallExpression(
                    syntax,
                    target,
                    result.Method!,
                    new[] { Convert(index, result.Method!.Parameters[0].Type, syntax.Index.Span) },
                    isVirtual: false);
            }

            return Error(
                syntax,
                SurtrDiagnosticCode.NotSupportedOnType,
                $"'{target.Type.ToDisplayString()}' cannot be indexed.");
        }

        /// <summary>
        /// Binds <c>t[0]</c>, whose index is part of the type rather than a value (§5.5).
        /// </summary>
        /// <remarks>
        /// A tuple's element type varies per index, so nothing could type <c>t[i]</c> for a running
        /// <c>i</c> — which is exactly why <c>tuple</c> declares no generic parameter and no
        /// <c>get(index)</c>. The index therefore has to fold here, and <c>TupGet</c> carries it as
        /// an immediate.
        /// </remarks>
        private BoundExpression BindTupleIndex(
            IndexExpressionSyntax syntax,
            BoundExpression target,
            BoundExpression index,
            TupleTypeSymbol tuple)
        {
            if (Unwrap(index) is not BoundLiteralExpression { Value: long ordinal })
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.InvalidTupleIndex,
                    $"'{tuple.ToDisplayString()}' holds a different type at each position, so it can only be indexed by a constant.");
            }

            if (ordinal < 0 || ordinal >= tuple.ElementTypes.Count)
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.InvalidTupleIndex,
                    $"'{tuple.ToDisplayString()}' has {tuple.ElementTypes.Count} element(s), so {ordinal} names none of them.");
            }

            return new BoundIndexExpression(syntax, target, index, tuple.ElementTypes[(int)ordinal]);
        }

        private BoundExpression BindCast(CastExpressionSyntax syntax)
        {
            var operand = BindExpression(syntax.Operand);
            var target = _resolver.Resolve(syntax.TargetType, _typeScope, _sourceName);

            if (operand.Type.IsError || target.IsError)
                return Error(syntax);

            // `as?` yields null on failure, so what it produces is the nullable form.
            var resultType = syntax.IsSafe ? target.Nullable : target;
            var conversion = _conversions.Classify(operand.Type, target);

            if (!conversion.Exists)
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.CannotConvert,
                    $"'{operand.Type.ToDisplayString()}' never becomes '{target.ToDisplayString()}', cast or not.");
            }

            return new BoundConversionExpression(syntax, operand, resultType, conversion, isExplicit: true)
            {
                IsSafe = syntax.IsSafe,
            };
        }

        private BoundExpression BindTypeTest(TypeTestExpressionSyntax syntax)
        {
            var operand = BindExpression(syntax.Operand);
            var tested = _resolver.Resolve(syntax.TargetType, _typeScope, _sourceName);

            return new BoundTypeTestExpression(syntax, operand, tested, _factory.Bool);
        }
        #endregion

        #region Conditionals and literals
        private BoundExpression BindConditional(ConditionalExpressionSyntax syntax, TypeSymbol? expected)
        {
            var condition = BindConverted(syntax.Condition, _factory.Bool);
            var whenTrue = BindExpression(syntax.WhenTrue, expected);
            var whenFalse = BindExpression(syntax.WhenFalse, expected);

            var type = expected ?? CommonType(whenTrue.Type, whenFalse.Type, syntax, "?:");
            if (type is null)
                return Error(syntax);

            return new BoundConditionalExpression(
                syntax,
                condition,
                Convert(whenTrue, type, syntax.WhenTrue.Span),
                Convert(whenFalse, type, syntax.WhenFalse.Span),
                type);
        }

        /// <summary>
        /// The type two branches agree on: whichever of them the other reaches.
        /// </summary>
        /// <remarks>
        /// Deliberately not a search for a common base class. With no root type and no variance,
        /// the only honest answer when neither reaches the other is that there is none — and saying
        /// so beats picking one and failing later.
        /// </remarks>
        private TypeSymbol? CommonType(TypeSymbol left, TypeSymbol right, SyntaxNode syntax, string what)
        {
            if (left.IsError || right.IsError)
                return _factory.ErrorType;

            if (ReferenceEquals(left, right))
                return left;

            if (_conversions.IsAssignable(right, left))
                return left;

            if (_conversions.IsAssignable(left, right))
                return right;

            Report(
                SurtrDiagnosticCode.NoCommonType,
                syntax.Span,
                $"The branches of '{what}' are '{left.ToDisplayString()}' and '{right.ToDisplayString()}', which have no type in common.");

            return null;
        }

        private BoundExpression BindArrayLiteral(ArrayLiteralExpressionSyntax syntax, TypeSymbol? expected)
        {
            var element = (expected?.NonNullable as ArrayTypeSymbol)?.ElementType;

            var elements = new BoundExpression[syntax.Elements.Count];
            for (int i = 0; i < elements.Length; i++)
                elements[i] = BindExpression(syntax.Elements[i], element);

            if (element is null)
            {
                if (elements.Length == 0)
                {
                    return Error(
                        syntax,
                        SurtrDiagnosticCode.CannotInferType,
                        "An empty array literal has no element type; write the type it is going into.");
                }

                element = elements[0].Type;
                for (int i = 1; i < elements.Length; i++)
                {
                    var common = CommonType(element, elements[i].Type, syntax.Elements[i], "the array literal");
                    if (common is null)
                        return Error(syntax);

                    element = common;
                }
            }

            for (int i = 0; i < elements.Length; i++)
                elements[i] = Convert(elements[i], element, syntax.Elements[i].Span);

            return new BoundArrayLiteralExpression(syntax, _factory.Array(element), elements);
        }

        private BoundExpression BindDictLiteral(DictLiteralExpressionSyntax syntax, TypeSymbol? expected)
        {
            var target = expected?.NonNullable as DictionaryTypeSymbol;

            var entries = new BoundDictEntry[syntax.Entries.Count];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new BoundDictEntry(
                    BindExpression(syntax.Entries[i].Key, target?.KeyType),
                    BindExpression(syntax.Entries[i].Value, target?.ValueType));
            }

            TypeSymbol? key = target?.KeyType;
            TypeSymbol? value = target?.ValueType;

            if (key is null || value is null)
            {
                if (entries.Length == 0)
                {
                    return Error(
                        syntax,
                        SurtrDiagnosticCode.CannotInferType,
                        "An empty dict literal has no key or value type; write the type it is going into.");
                }

                key ??= entries[0].Key.Type;
                value ??= entries[0].Value.Type;
            }

            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new BoundDictEntry(
                    Convert(entries[i].Key, key, syntax.Entries[i].Key.Span),
                    Convert(entries[i].Value, value, syntax.Entries[i].Value.Span));
            }

            return new BoundDictLiteralExpression(syntax, _factory.Dictionary(key, value), entries);
        }

        private BoundExpression BindTupleLiteral(TupleLiteralExpressionSyntax syntax, TypeSymbol? expected)
        {
            var target = expected?.NonNullable as TupleTypeSymbol;

            var elements = new BoundExpression[syntax.Elements.Count];
            var types = new TypeSymbol[elements.Length];

            for (int i = 0; i < elements.Length; i++)
            {
                var hint = target is not null && i < target.ElementTypes.Count ? target.ElementTypes[i] : null;
                elements[i] = BindExpression(syntax.Elements[i], hint);

                if (hint is not null)
                    elements[i] = Convert(elements[i], hint, syntax.Elements[i].Span);

                types[i] = elements[i].Type;
            }

            return new BoundTupleLiteralExpression(syntax, _factory.Tuple(types), elements);
        }

        private BoundExpression BindSwitchExpression(SwitchExpressionSyntax syntax, TypeSymbol? expected)
        {
            var subject = BindExpression(syntax.Subject);
            var arms = new BoundSwitchArm[syntax.Arms.Count];
            TypeSymbol? result = expected;

            for (int i = 0; i < arms.Length; i++)
            {
                var values = new BoundExpression[syntax.Arms[i].Values.Count];
                for (int v = 0; v < values.Length; v++)
                    values[v] = BindConverted(syntax.Arms[i].Values[v], subject.Type);

                var armResult = BindExpression(syntax.Arms[i].Result, expected);
                arms[i] = new BoundSwitchArm(values, armResult);

                if (result is null)
                {
                    result = armResult.Type;
                    continue;
                }

                var common = CommonType(result, armResult.Type, syntax.Arms[i].Result, "the switch arms");
                if (common is null)
                    return Error(syntax);

                result = common;
            }

            result ??= _factory.ErrorType;

            for (int i = 0; i < arms.Length; i++)
            {
                arms[i] = new BoundSwitchArm(
                    arms[i].Values,
                    Convert(arms[i].Result, result, syntax.Arms[i].Result.Span));
            }

            CheckExhaustive(syntax, subject, arms);
            return new BoundSwitchExpression(syntax, subject, arms, result);
        }

        /// <summary>
        /// Rejects a switch expression over an enum that neither lists every case nor has an
        /// <c>else</c> (§4.4).
        /// </summary>
        /// <remarks>
        /// Only for an enum, and only for the expression form. An enum's cases are fixed at its own
        /// declaration, so the set is knowable — and the point of the check is that adding a case
        /// later turns every switch that used to cover it into an error, rather than letting the new
        /// one fall silently through an existing <c>else</c>. The statement form is never required
        /// to produce a value, so it is unaffected.
        /// </remarks>
        private void CheckExhaustive(SwitchExpressionSyntax syntax, BoundExpression subject, IReadOnlyList<BoundSwitchArm> arms)
        {
            if (subject.Type.NonNullable is not NamedTypeSymbol { TypeKind: TypeSymbolKind.Enum } @enum)
                return;

            foreach (var arm in arms)
            {
                if (arm.IsDefault)
                    return;
            }

            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var arm in arms)
            {
                foreach (var value in arm.Values)
                {
                    if (Unwrap(value) is BoundFieldExpression field)
                        covered.Add(field.Field.Name);
                }
            }

            var missing = new List<string>();
            foreach (var member in @enum.Members)
            {
                if (member is FieldSymbol { IsStatic: true } @case && !covered.Contains(@case.Name))
                    missing.Add(@case.Name);
            }

            if (missing.Count == 0)
                return;

            Report(
                SurtrDiagnosticCode.SwitchNotExhaustive,
                syntax.Span,
                $"This switch does not cover {string.Join(", ", missing)}; list them or add an 'else' arm.");
        }

        private static BoundExpression Unwrap(BoundExpression expression)
            => expression is BoundConversionExpression conversion ? Unwrap(conversion.Operand) : expression;
        #endregion

        #region Lambdas
        private BoundExpression BindLambda(LambdaExpressionSyntax syntax, TypeSymbol? expected)
        {
            var target = expected?.NonNullable as ClosureTypeSymbol;

            var parameters = new ParameterSymbol[syntax.Parameters.Count];
            for (int i = 0; i < parameters.Length; i++)
            {
                var declared = syntax.Parameters[i];

                TypeSymbol type;
                if (declared.Type is not null)
                {
                    type = _resolver.Resolve(declared.Type, _typeScope, _sourceName);
                }
                else if (target is not null && i < target.ParameterTypes.Count)
                {
                    type = target.ParameterTypes[i];
                }
                else
                {
                    Report(
                        SurtrDiagnosticCode.CannotInferType,
                        declared.Span,
                        $"Nothing says what '{declared.Name}' is; write its type, or the closure type the lambda goes into.");

                    type = _factory.ErrorType;
                }

                parameters[i] = new ParameterSymbol(declared.Name, type, i);
            }

            // The lambda's own scope, and the boundary that makes everything outside it a capture.
            var outerValues = _values;

            _values = _values.CreateChild();
            var frame = new LambdaFrame(_values);
            _lambdas.Add(frame);

            foreach (var parameter in parameters)
                _values.TryDeclare(parameter.Name, parameter);

            BoundStatement body;
            TypeSymbol returnType;

            if (syntax.Body is not null)
            {
                var value = BindExpression(syntax.Body, target?.ReturnType);
                returnType = target?.ReturnType ?? value.Type;
                body = new BoundReturnStatement(syntax.Body, Convert(value, returnType, syntax.Body.Span));
            }
            else if (syntax.BlockBody is not null)
            {
                returnType = target?.ReturnType ?? _factory.Void;
                body = BindBlock(syntax.BlockBody);
            }
            else
            {
                returnType = _factory.Void;
                body = new BoundNopStatement(syntax);
            }

            _lambdas.RemoveAt(_lambdas.Count - 1);
            _values = outerValues;

            var parameterTypes = new TypeSymbol[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                parameterTypes[i] = parameters[i].Type;

            return new BoundLambdaExpression(
                syntax,
                _factory.Closure(parameterTypes, returnType),
                parameters,
                body,
                frame.Captures,
                frame.CapturesReceiver);
        }
        #endregion
    }
}
