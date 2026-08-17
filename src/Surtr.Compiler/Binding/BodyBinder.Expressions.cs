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
                case IdentifierExpressionSyntax identifier: return BindIdentifier(identifier, expected);
                case ThisExpressionSyntax @this: return BindThis(@this, isSuper: false);
                case SuperExpressionSyntax super: return BindThis(super, isSuper: true);
                case BinaryExpressionSyntax binary: return BindBinary(binary, expected);
                case UnaryExpressionSyntax unary: return BindUnary(unary);
                case AssignmentExpressionSyntax assignment: return BindAssignment(assignment);
                case ConditionalExpressionSyntax conditional: return BindConditional(conditional, expected);
                // The expected type reaches a call only to settle a generic construction's arguments
                // (§6): `let b: Box<int> = Box();` has nothing else to infer them from.
                case CallExpressionSyntax call: return BindCall(call, expected);
                case IndexExpressionSyntax index: return BindIndex(index);
                case MemberAccessExpressionSyntax member: return BindMemberAccess(member, expected);
                case CastExpressionSyntax cast: return BindCast(cast);
                case TypeTestExpressionSyntax test: return BindTypeTest(test);
                case TypeOfExpressionSyntax typeOf: return BindTypeOf(typeOf);
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
        private BoundExpression BindIdentifier(IdentifierExpressionSyntax syntax, TypeSymbol? expected = null)
        {
            var found = _values.Lookup(syntax.Name);

            if (found.Symbol is LocalSymbol local)
            {
                // A const local carries no slot at all (§7.1) and is not a capturable value either
                // — folding it here is what keeps it out of both.
                if (_localConstants.TryGetValue(local, out object? constant))
                    return new BoundLiteralExpression(syntax, local.Type, constant);

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

            // §8: a bare name that names a method, where a closure is expected, is sugar for a
            // lambda that calls it — nothing else here resolved the name, so a method group is the
            // last thing tried rather than something that could shadow a field or a property.
            if (TryBindMethodGroup(syntax, expected, MethodCandidatesForBareName(syntax.Name), receiverSyntax: null) is BoundExpression group)
                return group;

            return Error(syntax, SurtrDiagnosticCode.UnresolvedName, $"'{syntax.Name}' does not name anything in scope.");
        }

        /// <summary>
        /// Every method a bare name could name: the containing type's (instance ones only where
        /// <c>this</c> is actually available), then this module's own, then each wildcard import's —
        /// the same order <see cref="BindModuleMember"/> already resolves a field or property in.
        /// </summary>
        private List<MethodSymbol> MethodCandidatesForBareName(string name)
        {
            var candidates = new List<MethodSymbol>();

            if (_containingType is not null)
            {
                foreach (var candidate in _lookup.FindMethods(_containingType, name))
                {
                    if (candidate.IsStatic || !_method.IsStatic)
                        candidates.Add(candidate);
                }
            }

            AddModuleMethods(_module, name, candidates);
            foreach (var imported in _imported)
                AddModuleMethods(imported, name, candidates);

            return candidates;
        }

        private static void AddModuleMethods(ModuleSymbol module, string name, List<MethodSymbol> candidates)
        {
            foreach (var method in module.Methods)
            {
                if (string.Equals(method.Name, name, StringComparison.Ordinal))
                    candidates.Add(method);
            }
        }

        /// <summary>
        /// §8's method-group-to-closure conversion: when the context expects a closure and one of
        /// <paramref name="candidates"/> matches its shape, this is sugar for a lambda that calls it
        /// — capture tracking, dispatch and emission are then exactly a lambda's, for free.
        /// </summary>
        /// <param name="syntax">The name or member access naming the method group.</param>
        /// <param name="expected">The type the surrounding context expects, or <see langword="null"/>.</param>
        /// <param name="candidates">Every method the name could resolve to, in priority order.</param>
        /// <param name="receiverSyntax">
        /// The receiver's own syntax for an instance method reached through one (<c>obj.method</c>),
        /// bound fresh inside the synthesized lambda so an outer local it names is captured the same
        /// way any other lambda capture is — never <see langword="null"/> together with a static
        /// candidate reading it, since a static candidate never does. <see langword="null"/> for a
        /// bare name, where an instance candidate reads the implicit <c>this</c> instead.
        /// </param>
        private BoundExpression? TryBindMethodGroup(
            SyntaxNode syntax, TypeSymbol? expected, IReadOnlyList<MethodSymbol> candidates, ExpressionSyntax? receiverSyntax)
        {
            if (expected?.NonNullable is not ClosureTypeSymbol closure)
                return null;

            foreach (var candidate in candidates)
            {
                if (MatchesClosureShape(candidate, closure))
                    return BindMethodGroupLambda(syntax, closure, candidate, receiverSyntax);
            }

            return null;
        }

        /// <summary>
        /// Whether a method could stand in for a closure of this shape: same arity, every parameter
        /// accepts what the closure's would carry, and the returns agree on voidness (a void closure
        /// never reads a result, so wrapping a value-returning method into one would leave the
        /// runtime's own return-count expectation mismatched against what the method actually
        /// pushes) and, when both carry a value, the method's is assignable to the closure's.
        /// </summary>
        private bool MatchesClosureShape(MethodSymbol method, ClosureTypeSymbol closure)
        {
            if (method.Parameters.Count != closure.ParameterTypes.Count)
                return false;

            if (closure.ReturnType.IsVoid != method.ReturnType.IsVoid)
                return false;

            for (int i = 0; i < method.Parameters.Count; i++)
            {
                if (!_conversions.IsAssignable(closure.ParameterTypes[i], method.Parameters[i].Type))
                    return false;
            }

            return closure.ReturnType.IsVoid || _conversions.IsAssignable(method.ReturnType, closure.ReturnType);
        }

        /// <summary>
        /// Builds the lambda a method-group conversion is sugar for: <c>(p0, p1, ...) =&gt;
        /// method(p0, p1, ...)</c>, receiver included where there is one. Goes through the exact
        /// scaffolding <see cref="BindLambda"/> does — a fresh scope pushed onto <see cref="_lambdas"/>
        /// before anything in the body binds — so a captured local or the enclosing instance is
        /// noted exactly as it would be for a lambda a caller wrote by hand.
        /// </summary>
        private BoundExpression BindMethodGroupLambda(
            SyntaxNode syntax, ClosureTypeSymbol closure, MethodSymbol method, ExpressionSyntax? receiverSyntax)
        {
            var parameters = new ParameterSymbol[closure.ParameterTypes.Count];
            for (int i = 0; i < parameters.Length; i++)
                parameters[i] = new ParameterSymbol("$" + i, closure.ParameterTypes[i], i);

            var outerValues = _values;
            _values = _values.CreateChild();
            var frame = new LambdaFrame(_values);
            _lambdas.Add(frame);

            for (int i = 0; i < parameters.Length; i++)
                _values.TryDeclare(parameters[i].Name, parameters[i]);

            BoundExpression? receiver = method.IsStatic
                ? null
                : receiverSyntax is not null
                    ? BindExpression(receiverSyntax)
                    : ImplicitThis(syntax, (NamedTypeSymbol)method.ContainingSymbol!);

            var arguments = new BoundExpression[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                arguments[i] = Convert(new BoundParameterExpression(syntax, parameters[i]), method.Parameters[i].Type, syntax.Span);
            }

            bool isVirtual = !method.IsStatic && method.ContainingType is not null && method.Dispatch != MethodDispatch.Direct;
            var call = new BoundCallExpression(syntax, receiver, method, arguments, isVirtual);

            BoundStatement body = closure.ReturnType.IsVoid
                ? new BoundExpressionStatement(syntax, call)
                : new BoundReturnStatement(syntax, Convert(call, closure.ReturnType, syntax.Span));

            _lambdas.RemoveAt(_lambdas.Count - 1);
            _values = outerValues;

            var parameterTypes = new TypeSymbol[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                parameterTypes[i] = parameters[i].Type;

            return new BoundLambdaExpression(
                syntax, _factory.Closure(parameterTypes, closure.ReturnType), parameters, body, frame.Captures, frame.CapturesReceiver);
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

        /// <summary>
        /// Reads a field, folding a <c>const</c> one straight into the literal it evaluates to
        /// (§7.1) rather than a read of a slot — a <c>const</c> field never reaches
        /// <c>ModuleEmitter</c> as a real one, so every read of it has to be resolved here instead.
        /// </summary>
        /// <param name="syntax">The access expression's own syntax, for the bound node's span.</param>
        /// <param name="receiver">
        /// Already resolved by the caller exactly as it would be for an ordinary field — typically
        /// <c>field.IsStatic ? null : ...</c> — so a static field's receiver expression is never
        /// evaluated just to be discarded here.
        /// </param>
        /// <param name="field">The field being read.</param>
        private BoundExpression ResolveField(SyntaxNode syntax, BoundExpression? receiver, FieldSymbol field)
        {
            if (field.IsConst && _constants.TryGetValue(field.Name, out object? value))
                return new BoundLiteralExpression(syntax, field.Type, value);

            return new BoundFieldExpression(syntax, receiver, field);
        }

        private BoundExpression? BindImplicitMember(SyntaxNode syntax, NamedTypeSymbol type, string name)
        {
            if (_lookup.FindField(type, name) is FieldSymbol field)
            {
                RequireAccessible(field, field.Accessibility, field.Name, syntax);
                return ResolveField(syntax, field.IsStatic ? null : ImplicitThis(syntax, type), field);
            }

            if (_lookup.FindProperty(type, name) is PropertySymbol property)
            {
                RequireAccessible(property, property.Accessibility, property.Name, syntax);
                return new BoundPropertyExpression(syntax, property.IsStatic ? null : ImplicitThis(syntax, type), property);
            }

            return null;
        }

        /// <summary>
        /// Reports a member this body may not reach (§3.1), and carries on binding.
        /// </summary>
        /// <remarks>
        /// Reported rather than hidden: a member that exists and is out of reach is a different
        /// mistake from one that does not exist, and saying which is most of the value. Binding
        /// continues with the member anyway, so one protection-level error does not cascade into
        /// every expression its value would have flowed through.
        /// </remarks>
        private void RequireAccessible(Symbol member, Accessibility accessibility, string name, SyntaxNode syntax)
        {
            if (AccessCheck.IsAccessible(member, accessibility, _containingType, _module))
                return;

            Report(
                SurtrDiagnosticCode.Inaccessible,
                syntax.Span,
                $"'{name}' is {Describe(accessibility)}, so it cannot be reached from here.");
        }

        /// <summary>Reports a type this body may not name (§3.1).</summary>
        private void RequireAccessibleType(NamedTypeSymbol type, SyntaxNode syntax)
        {
            if (AccessCheck.IsAccessible(type, type.Accessibility, _containingType, _module))
                return;

            Report(
                SurtrDiagnosticCode.Inaccessible,
                syntax.Span,
                $"'{type.Name}' is {Describe(type.Accessibility)}, so it cannot be named from here.");
        }

        private static string Describe(Accessibility accessibility) => accessibility switch
        {
            Accessibility.Private => "private",
            Accessibility.Protected => "protected",
            Accessibility.Internal => "internal to its module",
            _ => "public",
        };

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

        private BoundExpression? BindMemberOf(ModuleSymbol module, SyntaxNode syntax, string name)
        {
            foreach (var field in module.Fields)
            {
                if (string.Equals(field.Name, name, StringComparison.Ordinal))
                {
                    RequireAccessible(field, field.Accessibility, field.Name, syntax);
                    return ResolveField(syntax, null, field);
                }
            }

            foreach (var property in module.Properties)
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                {
                    RequireAccessible(property, property.Accessibility, property.Name, syntax);
                    return new BoundPropertyExpression(syntax, null, property);
                }
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

        private BoundExpression BindMemberAccess(MemberAccessExpressionSyntax syntax, TypeSymbol? expected = null)
        {
            // `Suit.Hearts` is a static member, not a field on a value called Suit.
            if (TryBindAsType(syntax.Target, out var staticType))
            {
                if (Singleton(syntax.Target, staticType) is BoundExpression instance)
                    return BindInstanceMember(syntax, instance, expected);

                return BindStaticMember(syntax, staticType, expected);
            }

            var receiver = BindExpression(syntax.Target);
            if (receiver.Type.IsError)
                return Error(syntax);

            if (syntax.IsNullConditional)
                RequireNullable(receiver, syntax);

            return BindInstanceMember(syntax, receiver, expected);
        }

        /// <summary>
        /// Builds a construction of a standard-library exception the compiler raises itself (§13.3).
        /// </summary>
        /// <remarks>
        /// Resolved through the ordinary type scope, which §13 puts the whole <c>surtr</c> module in —
        /// so this finds the same class a <c>catch</c> clause naming it would. A compilation whose
        /// library does not declare it gets <see langword="null"/> rather than a diagnostic: the
        /// operator still means what it means, and refusing to compile over a missing library class
        /// would be a worse failure than the one it is guarding against.
        /// </remarks>
        private BoundExpression? BuildLibraryException(SyntaxNode syntax, string name, string message)
        {
            if (_typeScope.Lookup(name).Symbol is not NamedTypeSymbol type)
                return null;

            foreach (var member in _lookup.MembersOf(type))
            {
                if (member is not MethodSymbol { Role: MethodRole.Constructor } constructor
                    || constructor.Parameters.Count != 1
                    || constructor.Parameters[0].Type.SpecialType != SpecialType.String)
                {
                    continue;
                }

                return new BoundObjectCreationExpression(
                    syntax,
                    type,
                    constructor,
                    new BoundExpression[] { new BoundLiteralExpression(syntax, _factory.String, message) });
            }

            return null;
        }

        /// <summary>Reports a <c>?.</c> whose receiver could not have been null in the first place.</summary>
        private void RequireNullable(BoundExpression receiver, MemberAccessExpressionSyntax syntax)
        {
            if (receiver.Type.IsNullable || receiver.Type.IsError)
                return;

            Report(
                SurtrDiagnosticCode.CannotConvert,
                syntax.Span,
                $"'{receiver.Type.ToDisplayString()}' cannot be null, so '?.' has nothing to guard against.");
        }

        private BoundExpression BindInstanceMember(MemberAccessExpressionSyntax syntax, BoundExpression receiver, TypeSymbol? expected = null)
        {
            var lookupType = receiver.Type.NonNullable;

            // §5.1: `a?.b` evaluates `a` once and short-circuits to null, so the access is built over
            // a stand-in for the receiver and the guard wraps the pair.
            var accessed = syntax.IsNullConditional
                ? new BoundConditionalReceiver(syntax.Target, lookupType)
                : receiver;

            if (_lookup.FindField(lookupType, syntax.Name) is FieldSymbol field)
            {
                RequireAccessible(field, field.Accessibility, field.Name, syntax);
                return Guard(ResolveField(syntax, field.IsStatic ? null : accessed, field), syntax);
            }

            if (_lookup.FindProperty(lookupType, syntax.Name) is PropertySymbol property)
            {
                RequireAccessible(property, property.Accessibility, property.Name, syntax);
                return Guard(new BoundPropertyExpression(syntax, property.IsStatic ? null : accessed, property), syntax);
            }

            // §8: `obj.method` where a closure is expected is sugar for a lambda calling it — never
            // tried under `?.`, since a closure built from a short-circuited receiver has nothing
            // sound to close over.
            if (!syntax.IsNullConditional
                && TryBindMethodGroup(syntax, expected, _lookup.FindMethods(lookupType, syntax.Name), syntax.Target) is BoundExpression group)
            {
                return group;
            }

            return Error(
                syntax,
                SurtrDiagnosticCode.UnresolvedMember,
                $"'{receiver.Type.ToDisplayString()}' has no member called '{syntax.Name}'.");

            BoundExpression Guard(BoundExpression bound, MemberAccessExpressionSyntax access)
                => access.IsNullConditional ? NullConditional(access, receiver, bound) : bound;
        }

        /// <summary>
        /// Wraps an access whose receiver was written with <c>?.</c> (§5.1).
        /// </summary>
        /// <remarks>
        /// The whole expression is nullable whatever the member's own type is, which is the half of
        /// <c>?.</c> the type checker sees; the other half is the short-circuit, and that is the
        /// emitter's. A <c>void</c> access stays <c>void</c> — there is no value for null to stand in
        /// for, and only the skip matters.
        /// </remarks>
        private BoundExpression NullConditional(SyntaxNode syntax, BoundExpression receiver, BoundExpression access)
            => new BoundNullConditionalExpression(
                syntax,
                receiver,
                access,
                access.Type.IsVoid || access.Type.IsError ? access.Type : access.Type.Nullable);

        private BoundExpression BindStaticMember(MemberAccessExpressionSyntax syntax, NamedTypeSymbol type, TypeSymbol? expected = null)
        {
            RequireAccessibleType(type, syntax);

            if (_lookup.FindField(type, syntax.Name) is FieldSymbol field && field.IsStatic)
            {
                RequireAccessible(field, field.Accessibility, field.Name, syntax);
                return ResolveField(syntax, null, field);
            }

            if (_lookup.FindProperty(type, syntax.Name) is PropertySymbol property && property.IsStatic)
            {
                RequireAccessible(property, property.Accessibility, property.Name, syntax);
                return new BoundPropertyExpression(syntax, null, property);
            }

            // §8: `Type.method` where a closure is expected is sugar for a lambda calling it —
            // static candidates only, since accessing a member through a type name never has a
            // receiver for an instance one to read.
            if (TryBindMethodGroup(syntax, expected, StaticMethodsOf(type, syntax.Name), receiverSyntax: null) is BoundExpression group)
                return group;

            return Error(
                syntax,
                SurtrDiagnosticCode.UnresolvedMember,
                $"'{type.ToDisplayString()}' has no static member called '{syntax.Name}'.");
        }

        private List<MethodSymbol> StaticMethodsOf(NamedTypeSymbol type, string name)
        {
            var candidates = new List<MethodSymbol>();
            foreach (var candidate in _lookup.FindMethods(type, name))
            {
                if (candidate.IsStatic)
                    candidates.Add(candidate);
            }

            return candidates;
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

            // A declared `operator==` has to be tried before the built-in fallback: two operands of
            // the same class type are always "assignable to each other" (identity), which would
            // otherwise make ResolveBinary succeed first and the overload unreachable. `!=` reuses
            // the same lookup (both map to `op_==`, per TokenFor) and negates the result.
            if (syntax.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual
                && TryBindUserOperator(syntax, syntax.Operator, left, right) is BoundExpression userEquality)
            {
                return syntax.Operator == BinaryOperator.NotEqual
                    ? new BoundUnaryExpression(syntax, UnaryOperator.Not, userEquality, _factory.Bool)
                    : userEquality;
            }

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
            BoundExpression call = BindOperatorCall(syntax, left, new[] { right }, method);

            // `<`, `<=`, `>` and `>=` are all declared through `operator<=>` alone (§5.6) — the user
            // writes only the three-way form, so the relational ones compare its `int` result
            // against zero here. `TokenFor` maps all five to the same lookup, so `call` above is
            // already the `compareTo`-shaped result for every one of them; `Compare` itself (`<=>`)
            // is the one case that is already the answer and needs no further wrapping.
            return @operator switch
            {
                BinaryOperator.Less or BinaryOperator.LessEqual
                    or BinaryOperator.Greater or BinaryOperator.GreaterEqual =>
                        new BoundBinaryExpression(
                            syntax, @operator, call, new BoundLiteralExpression(syntax, _factory.Int, 0L), _factory.Bool),
                _ => call,
            };
        }

        /// <summary>
        /// Builds the call an operator expression lowers to. A plain operator is a static method
        /// taking every operand; one declared <c>virtual</c>/<c>override</c>/<c>abstract</c>, or in
        /// an interface, is an instance method whose receiver is the first parameter (§5.6) — so
        /// the call goes through the receiver instead of naming a static, which is what lets the
        /// dispatch reach a vtable slot or an interface's method slots like any other method call.
        /// </summary>
        private BoundExpression BindOperatorCall(
            SyntaxNode syntax,
            BoundExpression receiver,
            IReadOnlyList<BoundExpression> operands,
            MethodSymbol method)
        {
            if (method.IsStatic)
            {
                // Static: the receiver is argument zero like every other operand, and the call
                // names the method directly.
                var arguments = new BoundExpression[operands.Count + 1];
                arguments[0] = Convert(receiver, method.Parameters[0].Type, syntax.Span);
                for (int i = 0; i < operands.Count; i++)
                    arguments[i + 1] = Convert(operands[i], method.Parameters[i + 1].Type, syntax.Span);

                return new BoundCallExpression(syntax, null, method, arguments, isVirtual: false);
            }

            // Instance: parameter zero is the receiver and the call is dispatched through it —
            // virtually when the operator dispatches and the receiver could be overridden.
            var boundReceiver = Convert(receiver, method.Parameters[0].Type, syntax.Span);

            var callArguments = new BoundExpression[operands.Count];
            for (int i = 0; i < operands.Count; i++)
                callArguments[i] = Convert(operands[i], method.Parameters[i + 1].Type, syntax.Span);

            bool virtualCall = method.Dispatch != MethodDispatch.Direct
                && !(boundReceiver.Type.NonNullable is NamedTypeSymbol { IsSealed: true });

            return new BoundCallExpression(syntax, boundReceiver, method, callArguments, virtualCall);
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
                    // the assertion turns out to hold — and §5.1 makes it throw where it does not,
                    // which is what separates it from a silent cast.
                    return new BoundNullAssertExpression(
                        syntax,
                        operand,
                        type,
                        BuildLibraryException(
                            syntax,
                            "NullReferenceException",
                            $"'{operand.Type.ToDisplayString()}' was null where '!!' asserted it was not."));
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
                ? BindOperatorCall(syntax, operand, Array.Empty<BoundExpression>(), result.Method!)
                : null;
        }
        #endregion

        #region Assignment
        private BoundExpression BindAssignment(AssignmentExpressionSyntax syntax)
        {
            // §5.6's write form. Taken before the target is bound, because binding it would bind the
            // *read* operator — a call, which is not something to assign to.
            if (syntax.Operator == AssignmentOperator.Assign
                && syntax.Target is IndexExpressionSyntax indexed
                && BindIndexedWrite(syntax, indexed) is BoundExpression write)
            {
                return write;
            }

            var target = BindExpression(syntax.Target);

            if (target.Type.IsError)
            {
                BindExpression(syntax.Value);
                return Error(syntax);
            }

            if (!target.IsAssignable && !IsInitialisingWrite(target))
            {
                BindExpression(syntax.Value);

                // A `let` names itself, because the fix is one word and the shape it comes up in
                // most is a loop counter: §4.2's three-clause `for` reassigns its variable, so it
                // takes `var` while a `for-in` variable is rebound per step and needs neither.
                return Error(
                    syntax,
                    SurtrDiagnosticCode.NotAssignable,
                    target is BoundLocalExpression { Local.IsReadOnly: true } local
                        ? $"'{local.Local.Name}' is declared 'let', which is assign-once; declare it 'var' to reassign it."
                        : "This cannot be assigned to; it is a value, a 'let', or a property with no setter.");
            }

            // The property was already found reachable at resolution time (`RequireAccessible`
            // above `target` was bound), using the property's own — widest — accessibility. A
            // setter narrower than that (§3.4's per-accessor visibility) needs its own, stricter
            // check here, at the one place every write actually goes through.
            if (target is BoundPropertyExpression { Property.Setter: MethodSymbol setter } propertyTarget)
            {
                RequireAccessible(setter, setter.Accessibility, propertyTarget.Property.Name, syntax.Target);
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
        private BoundExpression BindCall(CallExpressionSyntax syntax, TypeSymbol? expected = null)
        {
            // `array<T>(...)`/`dict<K,V>(...)`/`tuple<...>(...)` construct too, but never through
            // ordinary object creation — array/dict/tuple resolve to ArrayTypeSymbol/
            // DictionaryTypeSymbol/TupleTypeSymbol (TypeResolver.Apply), never a NamedTypeSymbol, so
            // neither TryBindAsType nor TryBindAsGenericDefinition below would ever recognize them.
            // This has to run first and unconditionally intercept the three names, or a shadowing
            // user declaration under the same name would never get a chance to fall through to them.
            if (TryBindBuiltInCollectionCall(syntax, expected, out var collection))
                return collection;

            // `Vec2(1.0, 2.0)` constructs; there is no `new`.
            if (TryBindAsType(syntax.Callee, out var constructed))
                return BindObjectCreation(syntax, constructed);

            // `Box(5)` and `Box<int>(5)` construct too, but the type they name is a declaration
            // rather than a type until its arguments are settled — which is what this does.
            if (TryBindAsGenericDefinition(syntax.Callee, out var definition))
                return BindGenericObjectCreation(syntax, definition, expected);

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

                    // And so is a field or property holding one (§8): a closure is a value, and where
                    // it is kept says nothing about how it is called. Methods come first, since a
                    // method of that name is what a bare call usually means.
                    if (HoldsClosure(name) && _lookup.FindMethods((TypeSymbol?)_containingType ?? _factory.ErrorType, name).Count == 0)
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
                        var instance = Singleton(member.Target, staticOwner);

                        if (ClosureValue(staticOwner, instance, name, member) is BoundExpression stored)
                            return BindClosureInvocation(syntax, stored);

                        return BindMethodCall(syntax, instance, staticOwner, name, isVirtual: false);
                    }

                    receiver = BindExpression(member.Target);
                    isVirtual = receiver is not BoundThisExpression { IsSuper: true };

                    // §5.1 again, for `a?.f()`: the call is what the guard protects, so the receiver
                    // it sees is the stand-in and the guard wraps the call.
                    if (member.IsNullConditional && !receiver.Type.IsError)
                    {
                        RequireNullable(receiver, member);

                        var lookupType = receiver.Type.NonNullable;
                        var standIn = new BoundConditionalReceiver(member.Target, lookupType);

                        BoundExpression guarded;

                        if (_lookup.FindMethods(lookupType, name).Count > 0)
                            guarded = BindMethodCall(syntax, standIn, lookupType, name, isVirtual);
                        else if (ClosureValue(lookupType, standIn, name, member) is BoundExpression guardedValue)
                            guarded = BindClosureInvocation(syntax, guardedValue);
                        else
                            guarded = Error(
                                syntax,
                                SurtrDiagnosticCode.UnresolvedName,
                                $"'{receiver.Type.ToDisplayString()}' has no method called '{name}'.");

                        return guarded.Type.IsError ? guarded : NullConditional(syntax, receiver, guarded);
                    }

                    break;
                }

                default:
                    return BindClosureInvocation(syntax, BindExpression(syntax.Callee));
            }

            var owner = receiver?.Type.NonNullable ?? (TypeSymbol?)_containingType;

            if (owner is not null && _lookup.FindMethods(owner, name).Count > 0)
                return BindMethodCall(syntax, receiver, owner, name, isVirtual);

            // A member holding a closure is a callee too (§8), and is looked at only once no method
            // answers to the name.
            if (owner is not null && ClosureValue(owner, receiver, name, syntax.Callee) is BoundExpression held)
                return BindClosureInvocation(syntax, held);

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

        /// <summary>
        /// The closure a callee names, when what it names is a value rather than a method (§8).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A closure is a first-class value, so where one is kept says nothing about how it is
        /// called: a field, a property and a local are all callees, and only a bare local was
        /// recognised as one. What decides is the owner — a method of that name wins, since that is
        /// what a call usually means, and only when the owner declares none does a closure-typed
        /// member answer.
        /// </para>
        /// <para>
        /// The receiver is passed in rather than bound here, because the three shapes that reach
        /// this differ by exactly that: a type name has none, a singleton has its instance, and
        /// <c>a?.f()</c> has the stand-in the guard reads its receiver through.
        /// </para>
        /// </remarks>
        private BoundExpression? ClosureValue(
            TypeSymbol owner,
            BoundExpression? receiver,
            string name,
            SyntaxNode syntax)
        {
            var lookupType = owner.NonNullable;

            if (lookupType.IsError || _lookup.FindMethods(lookupType, name).Count > 0)
                return null;

            if (_lookup.FindField(lookupType, name) is FieldSymbol field
                && field.Type.NonNullable is ClosureTypeSymbol)
            {
                RequireAccessible(field, field.Accessibility, field.Name, syntax);
                return new BoundFieldExpression(syntax, field.IsStatic ? null : receiver, field);
            }

            if (_lookup.FindProperty(lookupType, name) is PropertySymbol property
                && property.Type.NonNullable is ClosureTypeSymbol)
            {
                RequireAccessible(property, property.Accessibility, property.Name, syntax);
                return new BoundPropertyExpression(syntax, property.IsStatic ? null : receiver, property);
            }

            return null;
        }

        /// <summary>
        /// Whether a bare name reaches a field or property of closure type, on this type or on this
        /// module.
        /// </summary>
        private bool HoldsClosure(string name)
        {
            if (_containingType is not null)
            {
                if (_lookup.FindField(_containingType, name)?.Type.NonNullable is ClosureTypeSymbol)
                    return true;

                if (_lookup.FindProperty(_containingType, name)?.Type.NonNullable is ClosureTypeSymbol)
                    return true;
            }

            foreach (var field in _module.Fields)
            {
                if (string.Equals(field.Name, name, StringComparison.Ordinal))
                    return field.Type.NonNullable is ClosureTypeSymbol;
            }

            foreach (var property in _module.Properties)
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                    return property.Type.NonNullable is ClosureTypeSymbol;
            }

            return false;
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
            BindArguments(syntax.Arguments, out var arguments, out var infos);

            candidates = Accessible(candidates, name, syntax);
            candidates = SubstituteGenericCandidates(syntax, candidates, infos, name);
            var result = _overloads.Resolve(candidates, infos);

            switch (result.Status)
            {
                case OverloadStatus.Resolved:
                    break;

                // A deferred lambda is deliberately left unbound on these paths. Its parameter types
                // were going to come from the overload that did not exist, so binding it would report
                // that it has none — three diagnostics for one mistake, and the two extra ones point
                // at the lambda rather than at the call that is actually wrong.
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
            var ordered = OrderArguments(
                syntax, syntax.Arguments, method, BindDeferredLambdas(syntax.Arguments, arguments, method));

            // A call on a sealed type or through `super` can be bound directly, which is the
            // devirtualisation §2.2 calls out as a static fact rather than a guess.
            bool virtualCall = isVirtual
                && method.Dispatch != MethodDispatch.Direct
                && !(receiver?.Type.NonNullable is NamedTypeSymbol { IsSealed: true });

            return new BoundCallExpression(syntax, method.IsStatic ? null : receiver, method, ordered, virtualCall);
        }

        /// <summary>
        /// Drops the candidates this body may not reach, and reports when that leaves none (§3.1).
        /// </summary>
        /// <remarks>
        /// Filtered rather than checked after the fact, because accessibility is part of what a name
        /// means at a call site: a <c>private</c> overload alongside a <c>public</c> one must not win
        /// and then be rejected — the public one was what the call meant. Reporting is left for the
        /// case where filtering took everything, which is the one where the author needs to know that
        /// the member exists and is out of reach rather than that it does not exist.
        /// </remarks>
        private IReadOnlyList<MethodSymbol> Accessible(
            IReadOnlyList<MethodSymbol> candidates,
            string name,
            SyntaxNode syntax)
        {
            List<MethodSymbol>? reachable = null;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (AccessCheck.IsAccessible(candidates[i], candidates[i].Accessibility, _containingType, _module))
                {
                    reachable?.Add(candidates[i]);
                    continue;
                }

                if (reachable is null)
                {
                    reachable = new List<MethodSymbol>(candidates.Count);
                    for (int j = 0; j < i; j++)
                        reachable.Add(candidates[j]);
                }
            }

            if (reachable is null)
                return candidates;

            if (reachable.Count == 0 && candidates.Count > 0)
            {
                Report(
                    SurtrDiagnosticCode.Inaccessible,
                    syntax.Span,
                    $"'{name}' is {Describe(candidates[0].Accessibility)}, so it cannot be reached from here.");
            }

            return reachable;
        }

        /// <summary>
        /// Binds a call's arguments, leaving a lambda that has nothing to go on for later.
        /// </summary>
        /// <remarks>
        /// §5.9 lets a lambda's parameters go unwritten "where a target type supplies them", and at a
        /// call site the target type is the parameter of whichever overload wins — which is not known
        /// until the arguments are. The circle is broken by not binding those lambdas yet: they enter
        /// overload resolution as an arity, and are bound once, afterwards, against the parameter that
        /// took them. Binding one now and again later would report everything it found twice.
        /// </remarks>
        private void BindArguments(
            IReadOnlyList<ArgumentSyntax> written,
            out BoundExpression?[] arguments,
            out ArgumentInfo[] infos)
        {
            arguments = new BoundExpression?[written.Count];
            infos = new ArgumentInfo[written.Count];

            for (int i = 0; i < arguments.Length; i++)
            {
                if (NeedsTargetType(written[i].Value) is LambdaExpressionSyntax lambda)
                {
                    infos[i] = ArgumentInfo.Lambda(lambda.Parameters.Count, _factory.ErrorType, written[i].Name);
                    continue;
                }

                arguments[i] = BindExpression(written[i].Value);
                infos[i] = new ArgumentInfo(arguments[i]!.Type, written[i].Name);
            }
        }

        /// <summary>
        /// The lambda an argument is, when it cannot be bound without being told its parameter types.
        /// </summary>
        private static LambdaExpressionSyntax? NeedsTargetType(ExpressionSyntax syntax)
        {
            if (syntax is not LambdaExpressionSyntax lambda)
                return null;

            foreach (var parameter in lambda.Parameters)
            {
                if (parameter.Type is null)
                    return lambda;
            }

            return null;
        }

        /// <summary>
        /// Binds the lambdas that were left for the winning overload to type.
        /// </summary>
        /// <param name="written">The argument list as written.</param>
        /// <param name="arguments">The bound arguments, with a hole where each deferred lambda sits.</param>
        /// <param name="method">
        /// The overload that won, or <see langword="null"/> when none did — in which case the lambdas
        /// are bound against nothing, so that what they are missing is reported rather than swallowed
        /// by a call that failed for a reason the author cannot see.
        /// </param>
        private BoundExpression[] BindDeferredLambdas(
            IReadOnlyList<ArgumentSyntax> written,
            BoundExpression?[] arguments,
            MethodSymbol? method)
        {
            var filled = new BoundExpression[arguments.Length];

            for (int i = 0; i < arguments.Length; i++)
            {
                filled[i] = arguments[i]
                    ?? BindExpression(written[i].Value, method is null ? null : ParameterFor(method, written, i));
            }

            return filled;
        }

        /// <summary>The parameter type an argument lands in, following a name where one was written.</summary>
        private static TypeSymbol? ParameterFor(MethodSymbol method, IReadOnlyList<ArgumentSyntax> written, int index)
        {
            if (written[index].Name is string name)
            {
                foreach (var parameter in method.Parameters)
                {
                    if (string.Equals(parameter.Name, name, StringComparison.Ordinal))
                        return parameter.Type;
                }

                return null;
            }

            // Positional, and a named argument may only follow positional ones (§3.5), so the index
            // is the position.
            return index < method.Parameters.Count ? method.Parameters[index].Type : null;
        }

        /// <summary>
        /// Replaces each generic candidate with the substituted view its arguments infer (§6).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Before overload resolution rather than after, and that is the whole design: applicability,
        /// specificity, the argument conversions and the call's own type are all decided against
        /// concrete types, so nothing downstream has to know a type parameter was ever involved. The
        /// alternative — resolving against the open signature and substituting afterwards — would ask
        /// overload resolution whether an <c>int</c> converts to <c>T</c>, which has no answer.
        /// </para>
        /// <para>
        /// A candidate whose parameters cannot all be inferred is dropped rather than reported here.
        /// It may be one overload of several, and the call reports once, at the end, if nothing was
        /// applicable — reporting per candidate would turn one mistake into a list.
        /// </para>
        /// </remarks>
        private IReadOnlyList<MethodSymbol> SubstituteGenericCandidates(
            CallExpressionSyntax syntax,
            IReadOnlyList<MethodSymbol> candidates,
            IReadOnlyList<ArgumentInfo> arguments,
            string name)
        {
            bool anyGeneric = false;
            for (int i = 0; i < candidates.Count && !anyGeneric; i++)
                anyGeneric = candidates[i].TypeParameters.Count > 0;

            if (!anyGeneric)
                return candidates;

            var written = ResolveWrittenTypeArguments(syntax);
            var substituted = new List<MethodSymbol>(candidates.Count);

            foreach (var candidate in candidates)
            {
                if (candidate.TypeParameters.Count == 0)
                {
                    substituted.Add(candidate);
                    continue;
                }

                if (written is not null)
                {
                    // Written out at the call site, so there is nothing to infer — only to check.
                    if (written.Count != candidate.TypeParameters.Count)
                        continue;

                    substituted.Add(Construct(candidate, written, syntax));
                    continue;
                }

                var declared = new TypeSymbol[candidate.Parameters.Count];
                for (int i = 0; i < declared.Length; i++)
                    declared[i] = candidate.Parameters[i].Type;

                var supplied = new TypeSymbol?[declared.Length];
                for (int i = 0; i < supplied.Length && i < arguments.Count; i++)
                    supplied[i] = arguments[i].Name is null ? arguments[i].Type : null;

                if (TypeInference.TryInfer(candidate.TypeParameters, declared, supplied, _factory, out var inferred, out _))
                    substituted.Add(Construct(candidate, inferred, syntax));
            }

            if (substituted.Count == 0)
            {
                Report(
                    SurtrDiagnosticCode.CannotInferTypeArgument,
                    syntax.Span,
                    $"The type arguments of '{name}' cannot be inferred from these arguments; write them at the call.");
            }

            return substituted;
        }

        /// <summary>
        /// Substitutes a generic method with the arguments a call site settled on, checking each
        /// against the parameter's bounds.
        /// </summary>
        private MethodSymbol Construct(MethodSymbol method, IReadOnlyList<TypeSymbol> arguments, SyntaxNode syntax)
        {
            CheckConstraints(method.TypeParameters, arguments, syntax);

            return _lookup.SubstituteMethod(
                method,
                TypeInference.Substitution(method.TypeParameters, arguments, _factory));
        }

        /// <summary>
        /// Checks each type argument against its parameter's bounds, substituted (§6).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Substituted, because a bound is written in terms of the parameters it constrains:
        /// <c>&lt;T : IComparable&lt;T&gt;&gt;</c> asked of a <c>Vec2</c> is asking about
        /// <c>IComparable&lt;Vec2&gt;</c>, not about <c>IComparable&lt;T&gt;</c>. The same rule
        /// <c>TypeResolver</c> applies to a type written out, applied where the arguments were
        /// inferred instead — a construction nobody wrote the arguments for is still a construction,
        /// and its bounds are not optional because the compiler filled them in.
        /// </para>
        /// </remarks>
        private void CheckConstraints(
            IReadOnlyList<TypeParameterSymbol> parameters,
            IReadOnlyList<TypeSymbol> arguments,
            SyntaxNode syntax)
        {
            var substitution = TypeInference.Substitution(parameters, arguments, _factory);

            for (int i = 0; i < parameters.Count && i < arguments.Count; i++)
            {
                if (parameters[i].Constraints.Count == 0 || arguments[i].IsError)
                    continue;

                foreach (var bound in parameters[i].Constraints)
                {
                    var wanted = substitution.Apply(bound);

                    if (wanted.IsError || _conversions.IsAssignable(arguments[i], wanted))
                        continue;

                    Report(
                        SurtrDiagnosticCode.ConstraintNotSatisfied,
                        syntax.Span,
                        $"'{arguments[i].ToDisplayString()}' does not satisfy '{parameters[i].Name} : {wanted.ToDisplayString()}'.");
                }
            }
        }

        /// <summary>
        /// The type arguments written at a call site, or <see langword="null"/> when none were.
        /// </summary>
        private IReadOnlyList<TypeSymbol>? ResolveWrittenTypeArguments(CallExpressionSyntax syntax)
        {
            if (syntax.TypeArguments.Count == 0)
                return null;

            var resolved = new TypeSymbol[syntax.TypeArguments.Count];
            for (int i = 0; i < resolved.Length; i++)
                resolved[i] = _resolver.Resolve(syntax.TypeArguments[i], _typeScope, _sourceName);

            return resolved;
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

        #region Nameable collection constructors
        /// <summary>
        /// Recognizes a call to <c>array&lt;T&gt;</c>/<c>dict&lt;K,V&gt;</c>/<c>tuple&lt;...&gt;</c>
        /// through their nameable generic form (§5.3) and binds the shape it names — empty,
        /// capacity, or a cast between array and tuple.
        /// </summary>
        /// <remarks>
        /// Guarded by reference identity against the same three built-in symbols
        /// <see cref="TypeResolver"/>'s own redirect checks against, so a user's own shadowing
        /// declaration under one of these names is never touched — it is not reference-equal to the
        /// built-in and so falls straight through to <see cref="TryBindAsType"/>/
        /// <see cref="TryBindAsGenericDefinition"/> unchanged. Has to run before both of those: array/
        /// dict/tuple resolve to <c>ArrayTypeSymbol</c>/<c>DictionaryTypeSymbol</c>/<c>TupleTypeSymbol</c>
        /// (<see cref="TypeResolver.Apply"/>'s redirect), never a <c>NamedTypeSymbol</c>, so neither of
        /// those two would ever recognize them as constructible on their own.
        /// </remarks>
        private bool TryBindBuiltInCollectionCall(CallExpressionSyntax syntax, TypeSymbol? expected, out BoundExpression result)
        {
            result = null!;

            var path = new List<string>();
            if (!TryFlatten(syntax.Callee, path) || path.Count != 1)
                return false;

            if (_typeScope.Lookup(path[0]).Symbol is not NamedTypeSymbol named)
                return false;

            if (ReferenceEquals(named, _resolver.Importer.ArrayType))
            {
                result = BindArrayCreation(syntax, expected);
                return true;
            }

            if (ReferenceEquals(named, _resolver.Importer.DictionaryType))
            {
                result = BindDictCreation(syntax, expected);
                return true;
            }

            if (ReferenceEquals(named, _resolver.Importer.TupleType))
            {
                result = BindTupleCreation(syntax, expected);
                return true;
            }

            return false;
        }

        /// <summary>Binds <c>array&lt;T&gt;()</c>, <c>array&lt;T&gt;(n)</c> and <c>array&lt;T&gt;(aTuple)</c>.</summary>
        private BoundExpression BindArrayCreation(CallExpressionSyntax syntax, TypeSymbol? expected)
        {
            TypeSymbol elementType;

            if (ResolveWrittenTypeArguments(syntax) is IReadOnlyList<TypeSymbol> written)
            {
                if (written.Count != 1)
                {
                    return Error(
                        syntax,
                        SurtrDiagnosticCode.WrongTypeArgumentCount,
                        $"'array' takes 1 type argument, not {written.Count}.");
                }

                elementType = written[0];
            }
            else if (expected?.NonNullable is ArrayTypeSymbol targetArray)
            {
                elementType = targetArray.ElementType;
            }
            else
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.CannotInferTypeArgument,
                    "Nothing says what 'array<T>' holds; write its type argument, or the type it goes into.");
            }

            var arrayType = _factory.Array(elementType);
            var arguments = syntax.Arguments;

            if (arguments.Count == 0)
                return new BoundCollectionCreationExpression(syntax, arrayType, CollectionCreationKind.ArrayEmpty);

            if (arguments.Count == 1 && arguments[0].Name is null)
            {
                var argument = BindExpression(arguments[0].Value);

                if (!argument.Type.IsNullable && argument.Type.SpecialType == SpecialType.Int)
                {
                    return new BoundCollectionCreationExpression(
                        syntax, arrayType, CollectionCreationKind.ArrayCapacity, capacity: argument);
                }

                if (argument.Type.NonNullable is TupleTypeSymbol sourceTuple)
                {
                    if (!TryClassifyElementwise(syntax, sourceTuple.ElementTypes, elementType, out var conversions))
                        return Error(syntax);

                    return new BoundCollectionCreationExpression(
                        syntax, arrayType, CollectionCreationKind.ArrayFromTuple, source: argument, elementConversions: conversions);
                }

                // The copy constructor, array<T>(anotherArray) — checked before the generic iterable
                // fallback below, since an array is itself IIterable<T> and this path is the faster
                // one: no interface dispatch, just ArrLen/ArrGet/ArrSet.
                if (argument.Type.NonNullable is ArrayTypeSymbol sourceArray)
                {
                    var elementConversion = _conversions.Classify(sourceArray.ElementType, elementType);
                    if (elementConversion.IsImplicit)
                    {
                        return new BoundCollectionCreationExpression(
                            syntax, arrayType, CollectionCreationKind.ArrayCopy,
                            source: argument, elementConversions: new[] { elementConversion });
                    }

                    return Error(
                        syntax,
                        SurtrDiagnosticCode.CollectionElementConversionMissing,
                        $"'{sourceArray.ElementType.ToDisplayString()}' has no implicit conversion to '{elementType.ToDisplayString()}'.");
                }

                // The lowest-priority shape: anything reaching here is not already an array or a
                // tuple, so this is where a range, a dict (as (K,V) pairs), a string (as char) or a
                // user IIterable<T> gets its chance — the exact same "what does iterating this yield"
                // question for-in already answers, reused rather than redefined.
                if (TryFindIterableElementType(argument.Type.NonNullable, out var iterableElementType))
                {
                    var elementConversion = _conversions.Classify(iterableElementType, elementType);
                    if (elementConversion.IsImplicit)
                    {
                        return new BoundCollectionCreationExpression(
                            syntax, arrayType, CollectionCreationKind.ArrayFromIterable,
                            source: argument, elementConversions: new[] { elementConversion }, sourceElementType: iterableElementType);
                    }

                    return Error(
                        syntax,
                        SurtrDiagnosticCode.CollectionElementConversionMissing,
                        $"'{iterableElementType.ToDisplayString()}' has no implicit conversion to '{elementType.ToDisplayString()}'.");
                }

                return Error(
                    syntax,
                    SurtrDiagnosticCode.CollectionCastNotSupported,
                    $"'array<{elementType.ToDisplayString()}>' cannot be built from '{argument.Type.ToDisplayString()}'; write a capacity ('int'), a matching tuple, another 'array<T>', or something implementing 'IIterable<T>'.");
            }

            if (arguments.Count == 2 && arguments[0].Name is null && arguments[1].Name is null)
            {
                var size = BindExpression(arguments[0].Value);

                if (!size.Type.IsNullable && size.Type.SpecialType == SpecialType.Int)
                {
                    var defaultValue = Convert(BindExpression(arguments[1].Value, elementType), elementType, arguments[1].Span);

                    // The zero-value fast path: array<T>(n, T's own zero) is exactly the already-
                    // existing ArrayCapacity shape, which already zero-fills via ArrNewX/ArrNew — reused
                    // unchanged rather than looping to write zeros by hand. Deliberately narrow: only a
                    // literal already typed as the element family's own zero qualifies, not anything
                    // reaching zero through an inserted conversion, which stays on the general loop.
                    if (IsElementFamilyZeroLiteral(defaultValue, elementType))
                    {
                        return new BoundCollectionCreationExpression(
                            syntax, arrayType, CollectionCreationKind.ArrayCapacity, capacity: size);
                    }

                    return new BoundCollectionCreationExpression(
                        syntax, arrayType, CollectionCreationKind.ArraySizeDefault, capacity: size, defaultValue: defaultValue);
                }
            }

            return Error(
                syntax,
                SurtrDiagnosticCode.UnresolvedCall,
                $"'array<{elementType.ToDisplayString()}>' takes no arguments, one 'int' capacity, one matching tuple/array/iterable, or ('int' size, '{elementType.ToDisplayString()}' default).");
        }

        private static bool IsElementFamilyZeroLiteral(BoundExpression expression, TypeSymbol elementType)
        {
            if (expression is not BoundLiteralExpression literal)
                return false;

            return elementType.NonNullable.SpecialType switch
            {
                SpecialType.Int => literal.Value is 0L,
                SpecialType.Float => literal.Value is 0.0,
                SpecialType.Bool => literal.Value is false,
                SpecialType.Char => literal.Value is '\0',
                _ => elementType.IsReferenceType && literal.Value is null,
            };
        }

        /// <summary>
        /// Binds <c>dict&lt;K,V&gt;()</c> and <c>dict&lt;K,V&gt;(n)</c>. Casting into or out of a
        /// dict is explicitly out of scope (§5.3) — it has no natural single source collection the
        /// way array and tuple have each other.
        /// </summary>
        private BoundExpression BindDictCreation(CallExpressionSyntax syntax, TypeSymbol? expected)
        {
            TypeSymbol keyType;
            TypeSymbol valueType;

            if (ResolveWrittenTypeArguments(syntax) is IReadOnlyList<TypeSymbol> written)
            {
                if (written.Count != 2)
                {
                    return Error(
                        syntax,
                        SurtrDiagnosticCode.WrongTypeArgumentCount,
                        $"'dict' takes 2 type arguments, not {written.Count}.");
                }

                keyType = written[0];
                valueType = written[1];
            }
            else if (expected?.NonNullable is DictionaryTypeSymbol targetDict)
            {
                keyType = targetDict.KeyType;
                valueType = targetDict.ValueType;
            }
            else
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.CannotInferTypeArgument,
                    "Nothing says what 'dict<K,V>' holds; write its type arguments, or the type it goes into.");
            }

            var dictType = _factory.Dictionary(keyType, valueType);
            var arguments = syntax.Arguments;

            if (arguments.Count == 0)
                return new BoundCollectionCreationExpression(syntax, dictType, CollectionCreationKind.DictEmpty);

            if (arguments.Count == 1 && arguments[0].Name is null)
            {
                var argument = BindExpression(arguments[0].Value);

                if (!argument.Type.IsNullable && argument.Type.SpecialType == SpecialType.Int)
                {
                    // dict<K,V>(n) is the one shape that does not fold to a single opcode: DictNew
                    // takes no capacity operand, so this reuses dict's own existing `reserve` native
                    // method — the same one `someDict.reserve(n)` written by hand would call.
                    var reserve = _lookup.FindMethods(dictType, "reserve");
                    if (reserve.Count == 0)
                    {
                        return Error(
                            syntax,
                            SurtrDiagnosticCode.CollectionCastNotSupported,
                            "'dict' declares no 'reserve' method for the compiler to call; a capacity constructor has nothing to fold to.");
                    }

                    return new BoundCollectionCreationExpression(
                        syntax, dictType, CollectionCreationKind.DictCapacity, capacity: argument, reserveMethod: reserve[0]);
                }

                if (argument.Type.NonNullable is ArrayTypeSymbol pairsArray
                    && pairsArray.ElementType.NonNullable is TupleTypeSymbol pairType
                    && pairType.ElementTypes.Count == 2)
                {
                    var keyConversion = _conversions.Classify(pairType.ElementTypes[0], keyType);
                    var valueConversion = _conversions.Classify(pairType.ElementTypes[1], valueType);

                    if (keyConversion.IsImplicit && valueConversion.IsImplicit)
                    {
                        return new BoundCollectionCreationExpression(
                            syntax, dictType, CollectionCreationKind.DictFromPairs,
                            source: argument, elementConversions: new[] { keyConversion, valueConversion });
                    }

                    return Error(
                        syntax,
                        SurtrDiagnosticCode.CollectionElementConversionMissing,
                        $"'{pairType.ToDisplayString()}' pairs don't convert to a ('{keyType.ToDisplayString()}', '{valueType.ToDisplayString()}') entry.");
                }

                return Error(
                    syntax,
                    SurtrDiagnosticCode.CollectionCastNotSupported,
                    $"'dict<{keyType.ToDisplayString()}, {valueType.ToDisplayString()}>' cannot be built from '{argument.Type.ToDisplayString()}'; write a capacity ('int') or an array of ('{keyType.ToDisplayString()}', '{valueType.ToDisplayString()}') pairs.");
            }

            if (arguments.Count == 2 && arguments[0].Name is null && arguments[1].Name is null)
            {
                var keys = BindExpression(arguments[0].Value);
                var values = BindExpression(arguments[1].Value);

                // Arrays are invariant (§6), so this is exact-match only — a K[] argument for a
                // dict<K,V> key array, never something merely convertible to K.
                if (keys.Type.NonNullable is ArrayTypeSymbol keyArray && ReferenceEquals(keyArray.ElementType, keyType)
                    && values.Type.NonNullable is ArrayTypeSymbol valueArray && ReferenceEquals(valueArray.ElementType, valueType))
                {
                    var thrown = BuildLibraryException(
                        syntax,
                        "ArgumentException",
                        $"'keys' and 'values' must have the same length to build a '{dictType.ToDisplayString()}'.");

                    return new BoundCollectionCreationExpression(
                        syntax, dictType, CollectionCreationKind.DictFromParallelArrays,
                        source: keys, source2: values, thrown: thrown);
                }

                return Error(
                    syntax,
                    SurtrDiagnosticCode.CollectionCastNotSupported,
                    $"'dict<{keyType.ToDisplayString()}, {valueType.ToDisplayString()}>' needs a '{keyType.ToDisplayString()}[]' and a '{valueType.ToDisplayString()}[]', not '{keys.Type.ToDisplayString()}' and '{values.Type.ToDisplayString()}'.");
            }

            return Error(
                syntax,
                SurtrDiagnosticCode.UnresolvedCall,
                $"'dict<{keyType.ToDisplayString()}, {valueType.ToDisplayString()}>' takes no arguments, one 'int' capacity, one array of pairs, or a (keys, values) array pair.");
        }

        /// <summary>
        /// Binds <c>tuple&lt;&gt;()</c> and <c>tuple&lt;...&gt;(anArray)</c>. There is no capacity
        /// constructor: a tuple's arity is part of its type, not requested at construction (§5.3).
        /// </summary>
        private BoundExpression BindTupleCreation(CallExpressionSyntax syntax, TypeSymbol? expected)
        {
            IReadOnlyList<TypeSymbol> elementTypes;

            if (ResolveWrittenTypeArguments(syntax) is IReadOnlyList<TypeSymbol> written)
            {
                elementTypes = written;
            }
            else if (syntax.Arguments.Count == 0)
            {
                // The 0-argument shape needs no inference at all: it can only ever mean the unit
                // tuple, the same way writing `tuple<>()` explicitly does.
                elementTypes = System.Array.Empty<TypeSymbol>();
            }
            else if (expected?.NonNullable is TupleTypeSymbol targetTuple)
            {
                elementTypes = targetTuple.ElementTypes;
            }
            else
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.CannotInferTypeArgument,
                    "Nothing says what 'tuple<...>' holds; a tuple's arity can't be read off a runtime array's length, so write its type arguments, or the type it goes into.");
            }

            var tupleType = _factory.Tuple(elementTypes);
            var arguments = syntax.Arguments;

            if (arguments.Count == 0)
            {
                if (elementTypes.Count == 0)
                    return new BoundCollectionCreationExpression(syntax, tupleType, CollectionCreationKind.TupleEmpty);

                return Error(
                    syntax,
                    SurtrDiagnosticCode.TupleArityFixed,
                    $"'{tupleType.ToDisplayString()}' has no no-arg constructor; every element is part of the type. Cast it from an 'array<T>' of length {elementTypes.Count}, or write all {elementTypes.Count} elements as a literal.");
            }

            if (arguments.Count == 1 && arguments[0].Name is null)
            {
                var argument = BindExpression(arguments[0].Value);

                // The copy constructor, (T1,T2)(pair: (T1,T2)): tuples are immutable and
                // TypeSymbolFactory interns structurally, so "the same tuple type" is reference
                // identity, not mere convertibility — when it holds, the source value already IS the
                // value this construction would build, and returning it unwrapped is exact, not an
                // approximation. A source that would need widening (e.g. (int,int) into (float,float))
                // is NOT reference-equal and falls through to the positional path below, which builds
                // a genuine new tuple with real per-element conversions.
                if (ReferenceEquals(argument.Type.NonNullable, tupleType))
                    return argument;

                if (!argument.Type.IsNullable && argument.Type.SpecialType == SpecialType.Int)
                {
                    return Error(
                        syntax,
                        SurtrDiagnosticCode.TupleArityFixed,
                        $"'{tupleType.ToDisplayString()}' has no capacity constructor; a tuple's arity is fixed by its type, not requested at construction.");
                }

                if (argument.Type.NonNullable is ArrayTypeSymbol sourceArray)
                {
                    if (!TryClassifyElementwise(syntax, sourceArray.ElementType, elementTypes, out var conversions))
                        return Error(syntax);

                    var thrown = BuildLibraryException(
                        syntax,
                        "InvalidCastException",
                        $"An array cast to '{tupleType.ToDisplayString()}' must have exactly {elementTypes.Count} element(s).");

                    return new BoundCollectionCreationExpression(
                        syntax, tupleType, CollectionCreationKind.TupleFromArray, source: argument, elementConversions: conversions, thrown: thrown);
                }

                return Error(
                    syntax,
                    SurtrDiagnosticCode.CollectionCastNotSupported,
                    $"'{tupleType.ToDisplayString()}' cannot be built from '{argument.Type.ToDisplayString()}'; write an 'array<T>' whose elements all convert to the tuple's slots.");
            }

            // The explicit positional constructor, (T1,...,Tn)(v1,...,vn): exactly the arity the
            // tuple's own type declares, every argument positional (a tuple has no parameter names to
            // write against). Arity 1 is deliberately excluded — it stays inside the branch above,
            // shared with the capacity-rejection and array-cast checks, since a single-element tuple
            // type is vanishingly rare and not worth a second dispatch path for.
            if (arguments.Count == elementTypes.Count && arguments.Count >= 2 && AllPositional(arguments))
                return BindTupleExplicitPositional(syntax, arguments, tupleType, elementTypes);

            return Error(
                syntax,
                SurtrDiagnosticCode.UnresolvedCall,
                $"'{tupleType.ToDisplayString()}' takes no arguments (only when its arity is 0), one matching array or same-typed tuple, or exactly {elementTypes.Count} positional element(s).");
        }

        private static bool AllPositional(IReadOnlyList<ArgumentSyntax> arguments)
        {
            for (int i = 0; i < arguments.Count; i++)
            {
                if (arguments[i].Name is not null)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Binds <c>(T1,...,Tn)(v1,...,vn)</c> exactly as the tuple literal <c>(v1,...,vn)</c> would
        /// bind — same per-element hint-then-<see cref="Convert"/> logic, same
        /// <see cref="BoundTupleLiteralExpression"/> node — reached from a second syntactic path
        /// rather than duplicated, since a written constructor call and a literal mean the same thing
        /// once the arity and element types line up.
        /// </summary>
        private BoundExpression BindTupleExplicitPositional(
            SyntaxNode syntax,
            IReadOnlyList<ArgumentSyntax> arguments,
            TupleTypeSymbol tupleType,
            IReadOnlyList<TypeSymbol> elementTypes)
        {
            var elements = new BoundExpression[arguments.Count];

            for (int i = 0; i < arguments.Count; i++)
            {
                var hint = elementTypes[i];
                var element = BindExpression(arguments[i].Value, hint);
                elements[i] = Convert(element, hint, arguments[i].Span);
            }

            return new BoundTupleLiteralExpression(syntax, tupleType, elements);
        }

        /// <summary>
        /// Checks that every element of a homogeneous array/tuple cast has an implicit conversion to
        /// its target slot, collecting one <see cref="Conversion"/> per element for the emitter to
        /// apply after it reads that element off the stack — never a user-defined <c>operator as</c>,
        /// which §5.6 makes explicit-only, so only <see cref="Conversion.IsImplicit"/> ever passes.
        /// </summary>
        private bool TryClassifyElementwise(
            SyntaxNode syntax,
            IReadOnlyList<TypeSymbol> sourceTypes,
            TypeSymbol targetType,
            out IReadOnlyList<Conversion> conversions)
        {
            var built = new Conversion[sourceTypes.Count];
            bool ok = true;

            for (int i = 0; i < sourceTypes.Count; i++)
            {
                var conversion = _conversions.Classify(sourceTypes[i], targetType);
                if (!conversion.IsImplicit)
                {
                    Report(
                        SurtrDiagnosticCode.CollectionElementConversionMissing,
                        syntax.Span,
                        $"Element {i}'s type '{sourceTypes[i].ToDisplayString()}' has no implicit conversion to '{targetType.ToDisplayString()}'.");
                    ok = false;
                    continue;
                }

                built[i] = conversion;
            }

            conversions = built;
            return ok;
        }

        /// <summary>The one-source-type-to-many-slots direction of <see cref="TryClassifyElementwise(SyntaxNode, IReadOnlyList{TypeSymbol}, TypeSymbol, out IReadOnlyList{Conversion})"/>.</summary>
        private bool TryClassifyElementwise(
            SyntaxNode syntax,
            TypeSymbol sourceType,
            IReadOnlyList<TypeSymbol> targetTypes,
            out IReadOnlyList<Conversion> conversions)
        {
            var built = new Conversion[targetTypes.Count];
            bool ok = true;

            for (int i = 0; i < targetTypes.Count; i++)
            {
                var conversion = _conversions.Classify(sourceType, targetTypes[i]);
                if (!conversion.IsImplicit)
                {
                    Report(
                        SurtrDiagnosticCode.CollectionElementConversionMissing,
                        syntax.Span,
                        $"Slot {i}'s type '{targetTypes[i].ToDisplayString()}' has no implicit conversion from '{sourceType.ToDisplayString()}'.");
                    ok = false;
                    continue;
                }

                built[i] = conversion;
            }

            conversions = built;
            return ok;
        }
        #endregion

        private BoundExpression BindObjectCreation(CallExpressionSyntax syntax, NamedTypeSymbol type)
            => BindObjectCreation(syntax, syntax.Arguments, type);

        /// <summary>
        /// Builds a construction of a generic type, settling its type arguments first (§6).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Three sources, in the order they are believed. Written at the call site
        /// (<c>Box&lt;int&gt;(5)</c>) settles it outright. Otherwise the type being assigned to does,
        /// which is what makes <c>let b: Box&lt;int&gt; = Box();</c> work with no constructor argument
        /// to infer from — the same target typing §5.9 gives an empty <c>[]</c>. Failing both, the
        /// constructor's own arguments are unified against its declared parameters.
        /// </para>
        /// <para>
        /// A construction with nothing to infer from is an error rather than a guess, and the
        /// diagnostic says to write the arguments — the same trade §5.9 makes for a bare <c>[]</c>.
        /// </para>
        /// </remarks>
        private BoundExpression BindGenericObjectCreation(
            CallExpressionSyntax syntax,
            List<NamedTypeSymbol> definitions,
            TypeSymbol? expected)
        {
            string name = definitions[0].Name;

            if (ResolveWrittenTypeArguments(syntax) is IReadOnlyList<TypeSymbol> written)
            {
                foreach (var definition in definitions)
                {
                    if (definition.Arity == written.Count)
                        return BindObjectCreation(syntax, syntax.Arguments, definition.Construct(written));
                }

                return Error(
                    syntax,
                    SurtrDiagnosticCode.WrongTypeArgumentCount,
                    $"Nothing called '{name}' takes {written.Count} type argument(s).");
            }

            if (expected?.NonNullable is NamedTypeSymbol { IsConstructed: true } target)
            {
                foreach (var definition in definitions)
                {
                    if (ReferenceEquals(definition.Definition, target.Definition))
                        return BindObjectCreation(syntax, syntax.Arguments, target);
                }
            }

            foreach (var definition in definitions)
            {
                if (!TryInferFromConstructor(syntax, definition, out var inferred))
                    continue;

                // Inferred rather than written, so nothing recorded a construction site for the
                // resolver to verify later — the bounds are checked here instead.
                CheckConstraints(definition.TypeParameters, inferred, syntax);
                return BindObjectCreation(syntax, syntax.Arguments, definition.Construct(inferred));
            }

            return Error(
                syntax,
                SurtrDiagnosticCode.CannotInferTypeArgument,
                $"Nothing says what '{name}' is being built with; write its type arguments, or the type it goes into.");
        }

        /// <summary>
        /// Infers a generic type's arguments from what its constructor was given.
        /// </summary>
        /// <remarks>
        /// The arguments are bound here and thrown away: what comes back is the type, and binding
        /// them again against the constructed type is what puts the right conversions in the tree.
        /// Binding twice is affordable because it is once per construction site, and the alternative
        /// — carrying half-bound arguments through the constructor resolution — would mean two paths
        /// into <see cref="BindObjectCreation(SyntaxNode, IReadOnlyList{ArgumentSyntax}, NamedTypeSymbol)"/>
        /// that could disagree.
        /// </remarks>
        private bool TryInferFromConstructor(
            CallExpressionSyntax syntax,
            NamedTypeSymbol definition,
            out TypeSymbol[] arguments)
        {
            arguments = System.Array.Empty<TypeSymbol>();

            var supplied = new TypeSymbol?[syntax.Arguments.Count];
            for (int i = 0; i < supplied.Length; i++)
            {
                supplied[i] = syntax.Arguments[i].Name is null
                    ? Speculative(syntax.Arguments[i].Value)
                    : null;
            }

            foreach (var member in definition.Members)
            {
                if (member is not MethodSymbol { Role: MethodRole.Constructor } constructor
                    || !CouldTake(constructor, supplied.Length))
                {
                    continue;
                }

                var declared = new TypeSymbol[constructor.Parameters.Count];
                for (int i = 0; i < declared.Length; i++)
                    declared[i] = constructor.Parameters[i].Type;

                if (TypeInference.TryInfer(definition.TypeParameters, declared, supplied, _factory, out arguments, out _))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a constructor could take that many positional arguments, so inference is worth
        /// trying against it.
        /// </summary>
        /// <remarks>
        /// A rough filter rather than applicability: the real check happens once the type is
        /// constructed and overload resolution runs against it. All it has to avoid is unifying an
        /// argument against a parameter that was never going to receive it — so it lets through a
        /// call that stops short of a defaulted or varargs tail, and nothing else.
        /// </remarks>
        private static bool CouldTake(MethodSymbol constructor, int positional)
        {
            var parameters = constructor.Parameters;

            if (positional > parameters.Count)
                return parameters.Count > 0 && parameters[parameters.Count - 1].IsVararg;

            for (int i = positional; i < parameters.Count; i++)
            {
                if (!parameters[i].HasDefaultValue && !parameters[i].IsVararg)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Binds an expression only to learn its type, reporting nothing.
        /// </summary>
        /// <remarks>
        /// Inference has to know what an argument <em>is</em> before the type it is being passed to
        /// exists, so the argument is bound before there is anything to check it against. Whatever
        /// that binding would have complained about is complained about later, when the same
        /// expression is bound for real against a settled parameter type — reporting here would
        /// report it twice.
        /// </remarks>
        private TypeSymbol? Speculative(ExpressionSyntax syntax)
        {
            int before = _diagnostics.Count;

            var bound = BindExpression(syntax);

            _diagnostics.TruncateTo(before);
            return bound.Type.IsError ? null : bound.Type;
        }

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
            // A primitive, `string` or `range` has no instance layout for `ObjNew` to allocate — a
            // primitive and a string are never a `SurtrInstance` at all, and a `range` is the two
            // operands `RangeNew` builds. Left unhandled, `int()`/`string()`/`range()` used to bind
            // and emit anyway (declaring no constructors and taking no arguments satisfies
            // TryResolveConstructor below), silently reading back the entity reference `ObjNew`
            // allocated as raw NaN-boxed bits. A parameterless construction is also exactly the
            // type's own default value, so giving it that meaning costs nothing and is what the
            // parens would otherwise promise and fail to deliver.
            if (written.Count == 0 && TryBuiltInDefaultValue(syntax, type) is BoundExpression defaultValue)
                return defaultValue;

            // Every other shape a primitive/string/range constructor can take (§5.3.2) — a
            // conversion from another primitive, a parse from string, or one of string's/range's own
            // composing shapes — none of which TryResolveConstructor below could ever satisfy, since
            // none of these six types declares a real SurtrMethodRole.Constructor.
            if (written.Count > 0 && TryBindBuiltInScalarCreation(syntax, written, type, out var scalarCreation))
                return scalarCreation;

            if (type.SpecialType is SpecialType.Void or SpecialType.Unknown)
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.NotSupportedOnType,
                    $"'{type.Name}' names no real value, so nothing can be constructed as one.");
            }

            if (type.IsAbstract)
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.NotSupportedOnType,
                    $"'{type.Name}' is abstract and cannot be constructed.");
            }

            // The resolver asks "is this name a type" without reporting, since a name that turns out
            // to be a local is not a mistake — so a construction is where the answer is finally used
            // and where the type's own visibility has to be asked (§3.1).
            RequireAccessibleType(type, syntax);

            if (!TryResolveConstructor(syntax, written, type, out var constructor, out var arguments))
                return Error(syntax);

            return new BoundObjectCreationExpression(syntax, type, constructor, arguments);
        }

        /// <summary>
        /// The zero-cost default a <em>parameterless</em> construction of a primitive, <c>string</c>
        /// or <c>range</c> means — the same value a fresh, uninitialized slot of that type already
        /// reads as, made explicit and constant-folded away entirely (no <c>ObjNew</c>, no
        /// allocation). <see langword="null"/> for anything else, which falls through to ordinary
        /// constructor resolution — and, for <c>void</c>/<c>unknown</c>, to the explicit rejection
        /// just below this call.
        /// </summary>
        private BoundExpression? TryBuiltInDefaultValue(SyntaxNode syntax, NamedTypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.Int: return new BoundLiteralExpression(syntax, _factory.Int, 0L);
                case SpecialType.Float: return new BoundLiteralExpression(syntax, _factory.Float, 0.0);
                case SpecialType.Bool: return new BoundLiteralExpression(syntax, _factory.Bool, false);
                case SpecialType.Char: return new BoundLiteralExpression(syntax, _factory.Char, '\0');
                case SpecialType.String: return new BoundLiteralExpression(syntax, _factory.String, "");

                // `range()` has no written bounds to mean anything else, so its default is the same
                // shape `0..0` binds to: an empty range, the "nothing" a fresh range-typed slot
                // would be closest to.
                case SpecialType.Range:
                    return new BoundBinaryExpression(
                        syntax,
                        BinaryOperator.Range,
                        new BoundLiteralExpression(syntax, _factory.Int, 0L),
                        new BoundLiteralExpression(syntax, _factory.Int, 0L),
                        _factory.Range);

                default:
                    return null;
            }
        }

        #region Nameable primitive/string/range constructors (§5.3.2)
        /// <summary>
        /// Dispatches a construction of one of the six scalar built-ins with at least one argument —
        /// the parameterless case is <see cref="TryBuiltInDefaultValue"/>'s. Every one of these binds
        /// to something that already exists: a conversion identical to the equivalent <c>as</c> cast,
        /// or an ordinary call to a native method the type already declares (or, for the handful of
        /// shapes nothing composes in one step — string parsing, <c>string(char,count)</c>,
        /// <c>string(char[])</c>, <c>range.toString()</c> — a small native this pass adds).
        /// </summary>
        private bool TryBindBuiltInScalarCreation(
            SyntaxNode syntax,
            IReadOnlyList<ArgumentSyntax> written,
            NamedTypeSymbol type,
            out BoundExpression result)
        {
            switch (type.SpecialType)
            {
                case SpecialType.Int: result = BindIntCreation(syntax, written); return true;
                case SpecialType.Float: result = BindFloatCreation(syntax, written); return true;
                case SpecialType.Bool: result = BindBoolCreation(syntax, written); return true;
                case SpecialType.Char: result = BindCharCreation(syntax, written); return true;
                case SpecialType.String: result = BindStringCreation(syntax, written); return true;
                case SpecialType.Range: result = BindRangeCreation(syntax, written); return true;
                default:
                    result = null!;
                    return false;
            }
        }

        private BoundExpression BindIntCreation(SyntaxNode syntax, IReadOnlyList<ArgumentSyntax> written)
        {
            if (written.Count == 1 && written[0].Name is null)
            {
                var argument = BindExpression(written[0].Value);

                if (TryBindPrimitiveConversion(syntax, argument, _factory.Int, out var conversion))
                    return conversion;

                if (argument.Type.NonNullable.SpecialType == SpecialType.String
                    && TryBindNativeSugarCall(syntax, null, _factory.Int, "parseStrict", new[] { argument }) is BoundExpression parsed)
                {
                    return parsed;
                }
            }
            else if (written.Count == 2 && written[0].Name is null && written[1].Name is null)
            {
                var text = BindExpression(written[0].Value);
                var radix = BindExpression(written[1].Value);

                if (text.Type.NonNullable.SpecialType == SpecialType.String
                    && !radix.Type.IsNullable && radix.Type.SpecialType == SpecialType.Int
                    && TryBindNativeSugarCall(syntax, null, _factory.Int, "parseStrict", new[] { text, radix }) is BoundExpression parsedRadix)
                {
                    return parsedRadix;
                }
            }

            return Error(
                syntax,
                SurtrDiagnosticCode.NoBuiltInConstructorMatch,
                "'int' takes no arguments, one 'float'/'char'/'bool'/'int'/'string' argument, or a ('string', 'int' radix) pair.");
        }

        private BoundExpression BindFloatCreation(SyntaxNode syntax, IReadOnlyList<ArgumentSyntax> written)
        {
            if (written.Count == 1 && written[0].Name is null)
            {
                var argument = BindExpression(written[0].Value);

                if (TryBindPrimitiveConversion(syntax, argument, _factory.Float, out var conversion))
                    return conversion;

                if (argument.Type.NonNullable.SpecialType == SpecialType.String
                    && TryBindNativeSugarCall(syntax, null, _factory.Float, "parseStrict", new[] { argument }) is BoundExpression parsed)
                {
                    return parsed;
                }
            }

            return Error(
                syntax,
                SurtrDiagnosticCode.NoBuiltInConstructorMatch,
                "'float' takes no arguments, or one 'int'/'char'/'bool'/'float'/'string' argument.");
        }

        private BoundExpression BindBoolCreation(SyntaxNode syntax, IReadOnlyList<ArgumentSyntax> written)
        {
            if (written.Count == 1 && written[0].Name is null)
            {
                var argument = BindExpression(written[0].Value);

                if (TryBindPrimitiveConversion(syntax, argument, _factory.Bool, out var conversion))
                    return conversion;

                if (argument.Type.NonNullable.SpecialType == SpecialType.String
                    && TryBindNativeSugarCall(syntax, null, _factory.Bool, "parseStrict", new[] { argument }) is BoundExpression parsed)
                {
                    return parsed;
                }
            }

            return Error(
                syntax,
                SurtrDiagnosticCode.NoBuiltInConstructorMatch,
                "'bool' takes no arguments, or one 'int'/'float'/'char'/'bool'/'string' argument.");
        }

        private BoundExpression BindCharCreation(SyntaxNode syntax, IReadOnlyList<ArgumentSyntax> written)
        {
            if (written.Count == 1 && written[0].Name is null)
            {
                var argument = BindExpression(written[0].Value);

                // int(v: char)'s reverse: no validation, exactly like `code as char` — the code unit
                // is truncated to 16 bits, never checked, since decision #4 keeps this constructor
                // and the cast it is sugar for behaving identically.
                if (TryBindPrimitiveConversion(syntax, argument, _factory.Char, out var conversion))
                    return conversion;

                if (argument.Type.NonNullable.SpecialType == SpecialType.String
                    && TryBindNativeSugarCall(syntax, null, _factory.Char, "parseStrict", new[] { argument }) is BoundExpression parsed)
                {
                    return parsed;
                }
            }

            return Error(
                syntax,
                SurtrDiagnosticCode.NoBuiltInConstructorMatch,
                "'char' takes no arguments, or one 'int'/'float'/'bool'/'char'/'string' argument.");
        }

        private BoundExpression BindStringCreation(SyntaxNode syntax, IReadOnlyList<ArgumentSyntax> written)
        {
            if (written.Count == 1 && written[0].Name is null)
            {
                var argument = BindExpression(written[0].Value);
                var argumentType = argument.Type.NonNullable;

                if (argumentType.SpecialType is SpecialType.Int or SpecialType.Float or SpecialType.Bool or SpecialType.Char or SpecialType.Range)
                {
                    // Sugar over the toString() every one of these already declares — string(v) means
                    // exactly what v.toString() means, for any of the five.
                    if (TryBindNativeSugarCall(syntax, argument, argumentType, "toString", System.Array.Empty<BoundExpression>()) is BoundExpression sugared)
                        return sugared;
                }
                else if (argumentType is ArrayTypeSymbol arrayArgument && arrayArgument.ElementType.NonNullable.SpecialType == SpecialType.Char)
                {
                    if (TryBindNativeSugarCall(syntax, null, _factory.String, "fromCharArray", new[] { argument }) is BoundExpression fromChars)
                        return fromChars;
                }
            }
            else if (written.Count == 2 && written[0].Name is null && written[1].Name is null)
            {
                var value = BindExpression(written[0].Value);
                var count = BindExpression(written[1].Value);

                if (!value.Type.IsNullable && value.Type.SpecialType == SpecialType.Char
                    && !count.Type.IsNullable && count.Type.SpecialType == SpecialType.Int
                    && TryBindNativeSugarCall(syntax, null, _factory.String, "fromCharRepeated", new[] { value, count }) is BoundExpression repeated)
                {
                    return repeated;
                }
            }
            else if (written.Count == 3 && written[0].Name is null && written[1].Name is null && written[2].Name is null)
            {
                var chars = BindExpression(written[0].Value);
                var offset = BindExpression(written[1].Value);
                var length = BindExpression(written[2].Value);

                if (chars.Type.NonNullable is ArrayTypeSymbol charSliceArray && charSliceArray.ElementType.NonNullable.SpecialType == SpecialType.Char
                    && !offset.Type.IsNullable && offset.Type.SpecialType == SpecialType.Int
                    && !length.Type.IsNullable && length.Type.SpecialType == SpecialType.Int
                    && TryBindNativeSugarCall(syntax, null, _factory.String, "fromCharArraySlice", new[] { chars, offset, length }) is BoundExpression sliced)
                {
                    return sliced;
                }
            }

            return Error(
                syntax,
                SurtrDiagnosticCode.NoBuiltInConstructorMatch,
                "'string' takes no arguments, one 'int'/'float'/'bool'/'char'/'range'/'char[]' argument, a ('char', 'int' count) pair, or a ('char[]', 'int' offset, 'int' length) slice.");
        }

        private BoundExpression BindRangeCreation(SyntaxNode syntax, IReadOnlyList<ArgumentSyntax> written)
        {
            if (written.Count == 2 && written[0].Name is null && written[1].Name is null)
            {
                var start = Convert(BindExpression(written[0].Value), _factory.Int, written[0].Span);
                var end = Convert(BindExpression(written[1].Value), _factory.Int, written[1].Span);

                return new BoundBinaryExpression(syntax, BinaryOperator.Range, start, end, _factory.Range);
            }

            if (written.Count == 3 && written[0].Name is null && written[1].Name is null && written[2].Name is null)
            {
                var isInclusive = BindExpression(written[2].Value);

                if (!isInclusive.Type.IsNullable && isInclusive.Type.SpecialType == SpecialType.Bool)
                {
                    var start = Convert(BindExpression(written[0].Value), _factory.Int, written[0].Span);
                    var end = Convert(BindExpression(written[1].Value), _factory.Int, written[1].Span);

                    // A written true/false settles which opcode at bind time, zero runtime cost - the
                    // same fold `range(start,end)` gets, just picking between Range/RangeInclusive.
                    // A genuine runtime bool falls back to an ordinary ternary between the two forms,
                    // ordinary because BoundConditionalExpression already exists and needs nothing new.
                    if (isInclusive is BoundLiteralExpression { Value: bool constant })
                    {
                        return new BoundBinaryExpression(
                            syntax,
                            constant ? BinaryOperator.RangeInclusive : BinaryOperator.Range,
                            start,
                            end,
                            _factory.Range);
                    }

                    var inclusiveRange = new BoundBinaryExpression(syntax, BinaryOperator.RangeInclusive, start, end, _factory.Range);
                    var exclusiveRange = new BoundBinaryExpression(syntax, BinaryOperator.Range, start, end, _factory.Range);

                    return new BoundConditionalExpression(syntax, isInclusive, inclusiveRange, exclusiveRange, _factory.Range);
                }
            }

            return Error(
                syntax,
                SurtrDiagnosticCode.NoBuiltInConstructorMatch,
                "'range' takes ('int', 'int') bounds, or ('int', 'int', 'bool' isInclusive).");
        }

        /// <summary>
        /// Binds a primitive-construction argument as the same conversion <c>argument as target</c>
        /// would produce, if one exists — a nameable primitive constructor is sugar for the
        /// equivalent explicit cast (§5.3.2), never a separate rule with its own semantics. Returns
        /// <see langword="false"/>, binding nothing, when no such conversion exists, so the caller is
        /// free to try a different shape (a string parse) before giving up.
        /// </summary>
        private bool TryBindPrimitiveConversion(SyntaxNode syntax, BoundExpression argument, TypeSymbol target, out BoundExpression result)
        {
            var conversion = _conversions.Classify(argument.Type, target);

            if (!conversion.Exists)
            {
                result = null!;
                return false;
            }

            result = new BoundConversionExpression(syntax, argument, target, conversion, isExplicit: true);
            return true;
        }

        /// <summary>
        /// Builds an ordinary call to an already-declared native method, found by name and arity —
        /// the shared mechanism every "constructor is sugar for a call" shape in §5.3.2 goes through.
        /// Arity-only matching is enough: every native this reaches is declared exactly once per
        /// arity, so there is no real overload set for <see cref="OverloadResolution"/> to pick
        /// between the way a user-written call site might need.
        /// </summary>
        private BoundExpression? TryBindNativeSugarCall(
            SyntaxNode syntax,
            BoundExpression? receiver,
            TypeSymbol owner,
            string name,
            IReadOnlyList<BoundExpression> arguments)
        {
            foreach (var method in _lookup.FindMethods(owner, name))
            {
                if (method.Parameters.Count != arguments.Count)
                    continue;

                return new BoundCallExpression(syntax, method.IsStatic ? null : receiver, method, arguments, isVirtual: false);
            }

            return null;
        }
        #endregion

        /// <summary>
        /// Binds a <c>: super(...)</c> or <c>: this(...)</c> chain against the constructors of the
        /// type it names (§3.2).
        /// </summary>
        /// <remarks>
        /// Deliberately not routed through <see cref="BindObjectCreation(SyntaxNode, IReadOnlyList{ArgumentSyntax}, NamedTypeSymbol)"/>:
        /// a chain allocates nothing, and the base it reaches is very often <c>abstract</c> — which a
        /// construction is right to refuse and a chain has to allow.
        /// </remarks>
        public BoundConstructorChain? BindConstructorChain(
            ConstructorDeclarationSyntax syntax,
            NamedTypeSymbol target,
            bool isThis)
        {
            var written = syntax.ChainArguments ?? Array.Empty<ArgumentSyntax>();

            if (!TryResolveConstructor(syntax, written, target, out var constructor, out var arguments))
                return null;

            if (constructor is null)
            {
                // Chaining to a type that declares no constructor is only legal with no arguments,
                // and then there is nothing to call: the initializers the base does have run from
                // the constructor the emitter synthesises for it.
                return null;
            }

            return new BoundConstructorChain(syntax, constructor, arguments, isThis);
        }

        /// <summary>
        /// Resolves which constructor of <paramref name="type"/> an argument list means, and binds
        /// the arguments into parameter order.
        /// </summary>
        /// <returns>
        /// <see langword="false"/> when nothing applied, in which case a diagnostic has been
        /// reported. A <see langword="true"/> with a null constructor means the type declares none
        /// and none was needed.
        /// </returns>
        private bool TryResolveConstructor(
            SyntaxNode syntax,
            IReadOnlyList<ArgumentSyntax> written,
            NamedTypeSymbol type,
            out MethodSymbol? constructor,
            out IReadOnlyList<BoundExpression> ordered)
        {
            constructor = null;
            ordered = NoArguments;

            BindArguments(written, out var arguments, out var infos);

            var constructors = new List<MethodSymbol>();
            foreach (var member in _lookup.MembersOf(type))
            {
                if (member is MethodSymbol method && method.Role == MethodRole.Constructor)
                    constructors.Add(method);
            }

            if (constructors.Count == 0)
            {
                if (arguments.Length == 0)
                    return true;

                Report(
                    SurtrDiagnosticCode.UnresolvedCall,
                    syntax.Span,
                    $"'{type.Name}' declares no constructor, so it takes no arguments.");

                return false;
            }

            constructors = new List<MethodSymbol>(Accessible(constructors, type.Name, syntax));
            if (constructors.Count == 0)
                return false;

            var result = _overloads.Resolve(constructors, infos);
            if (!result.IsResolved)
            {
                Report(
                    SurtrDiagnosticCode.UnresolvedCall,
                    syntax.Span,
                    $"No constructor of '{type.Name}' takes these arguments.");

                return false;
            }

            constructor = result.Method;
            ordered = OrderArguments(syntax, written, result.Method!, BindDeferredLambdas(written, arguments, result.Method!));
            return true;
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

            // Anything else has to declare `operator[]` (§5.6). An overload is always static, so the
            // receiver is its first parameter and the read form takes two — the same shape every
            // other binary overload has, where `a + b` passes both operands.
            if (BindIndexOperator(syntax, target, index, value: null) is BoundExpression indexed)
                return indexed;

            return Error(
                syntax,
                SurtrDiagnosticCode.NotSupportedOnType,
                $"'{target.Type.ToDisplayString()}' cannot be indexed.");
        }

        /// <summary>
        /// Binds <c>t[i] = v</c> where <c>t</c> declares an <c>operator[]</c> write form (§5.6).
        /// </summary>
        /// <remarks>
        /// Only a plain assignment. A compound one would have to read, combine and write with the
        /// receiver and the index evaluated once each, and §5.6 says nothing about that — so it is
        /// left to report through the ordinary path rather than given semantics nobody specified.
        /// </remarks>
        private BoundExpression? BindIndexedWrite(AssignmentExpressionSyntax syntax, IndexExpressionSyntax indexed)
        {
            var target = BindExpression(indexed.Target);
            if (target.Type.IsError || target.Type.NonNullable is ArrayTypeSymbol or DictionaryTypeSymbol or TupleTypeSymbol)
                return null;

            if (_lookup.FindMethods(target.Type, OperatorNames.For(TokenType.LeftBracket, 1)).Count == 0)
                return null;

            var index = BindExpression(indexed.Index);
            var value = BindExpression(syntax.Value);

            if (BindIndexOperator(syntax, target, index, value) is not BoundExpression call)
            {
                return Error(
                    syntax,
                    SurtrDiagnosticCode.UnresolvedCall,
                    $"'{target.Type.ToDisplayString()}' declares no 'operator[]' taking these and a value to write.");
            }

            return call;
        }

        /// <summary>
        /// Binds a user-declared <c>operator[]</c>, in its read or its write form (§5.6).
        /// </summary>
        /// <remarks>
        /// <para>
        /// One method for both, because they differ only by what is passed: the read form is
        /// <c>(receiver, index)</c> and the write form is <c>(receiver, index, value)</c> returning
        /// nothing. §5.6's table counts the operands the <em>expression</em> has — one index for a
        /// read, an index and a value for a write — while a declaration also names the receiver,
        /// since an overload is always static.
        /// </para>
        /// <para>
        /// Returns <see langword="null"/> rather than reporting, so the caller can say what it was
        /// trying to do: "cannot be indexed" reads better at a read than at a write.
        /// </para>
        /// </remarks>
        private BoundExpression? BindIndexOperator(
            SyntaxNode syntax,
            BoundExpression target,
            BoundExpression index,
            BoundExpression? value)
        {
            var candidates = _lookup.FindMethods(target.Type, OperatorNames.For(TokenType.LeftBracket, 1));
            if (candidates.Count == 0)
                return null;

            var arguments = value is null
                ? new[] { new ArgumentInfo(target.Type), new ArgumentInfo(index.Type) }
                : new[] { new ArgumentInfo(target.Type), new ArgumentInfo(index.Type), new ArgumentInfo(value.Type) };

            var result = _overloads.Resolve(candidates, arguments);
            if (!result.IsResolved)
                return null;

            var method = result.Method!;
            var bound = value is null
                ? new[] { index }
                : new[] { index, value };

            return BindOperatorCall(syntax, target, bound, method);
        }

        /// <summary>
        /// Binds <c>t[0]</c>, whose index is part of the type rather than a value (§5.3).
        /// </summary>
        /// <remarks>
        /// <para>
        /// A tuple's element type varies per index, so nothing could type <c>t[i]</c> for a running
        /// <c>i</c> — which is exactly why <c>tuple</c> declares no generic parameter and no
        /// <c>get(index)</c>. The index therefore has to fold here, and what it folds to becomes the
        /// expression: <c>TupGetC</c> carries its index as an immediate, so leaving a
        /// <c>const</c>'s declaration to be read at run time would emit a load for something the
        /// instruction can spell itself.
        /// </para>
        /// <para>
        /// A range check is part of the same answer rather than an extra rule. The arity is in the
        /// type, so an index past the end names no element and there is nothing to give the
        /// expression as a type.
        /// </para>
        /// </remarks>
        private BoundExpression BindTupleIndex(
            IndexExpressionSyntax syntax,
            BoundExpression target,
            BoundExpression index,
            TupleTypeSymbol tuple)
        {
            if (!TryFoldOrdinal(syntax.Index, index, out long ordinal))
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

            return new BoundIndexExpression(
                syntax,
                target,
                new BoundLiteralExpression(syntax.Index, _factory.Int, ordinal),
                tuple.ElementTypes[(int)ordinal]);
        }

        /// <summary>What a tuple index is, when it is anything §7 calls a compile-time constant.</summary>
        private bool TryFoldOrdinal(ExpressionSyntax written, BoundExpression bound, out long ordinal)
        {
            if (Unwrap(bound) is BoundLiteralExpression { Value: long literal })
            {
                ordinal = literal;
                return true;
            }

            // §7.1 makes a `const` binding a compile-time constant, and a tuple index is a position
            // that wants one — so the evaluator that answers a `const if` answers this too.
            if (_constants.TryEvaluate(written, out object? value))
            {
                switch (value)
                {
                    case long folded: ordinal = folded; return true;
                    case int folded: ordinal = folded; return true;
                }
            }

            ordinal = 0;
            return false;
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

        /// <summary>
        /// <c>typeof(X)</c>. The parser already settled the one shape that could never also be an
        /// expression (a generic argument list, see <c>Parser.ParseTypeOf</c>) - what is left here
        /// is <see cref="TypeOfExpressionSyntax.Operand"/>, an ordinary expression that might
        /// nonetheless be a bare or dotted type name, since §1.1 keeps type names and value names in
        /// separate namespaces. Resolved type-first through <see cref="TryBindAsType"/>, the same
        /// order and for the same reason it already settles the identical ambiguity everywhere else
        /// in this binder (singletons, construction, static member access): a name that resolves to
        /// a type is never also the value this call site would otherwise have bound, so this is a
        /// decidable question, not a coin flip. Anything that is not a plain name/member-access
        /// chain - a call, a literal, an arithmetic expression - can never resolve this way at all,
        /// since <see cref="TryBindAsType"/> only ever looks at one.
        /// </summary>
        private BoundExpression BindTypeOf(TypeOfExpressionSyntax syntax)
        {
            var resultType = ResolveBuiltInType("Type", syntax.Span);

            if (syntax.TypeOperand is TypeSyntax typeOperand)
            {
                var target = _resolver.Resolve(typeOperand, _typeScope, _sourceName);
                return new BoundTypeOfExpression(syntax, target, null, resultType);
            }

            var operandSyntax = syntax.Operand!;

            if (TryBindAsType(operandSyntax, out var asType))
                return new BoundTypeOfExpression(syntax, asType, null, resultType);

            var operand = BindExpression(operandSyntax);

            // A primitive's runtime class can never differ from its static one, so there is
            // nothing to read at run time - leaving it unconverted is what lets the emitter
            // recognise the case and skip both the box and the runtime class read that Type.of's
            // own `unknown` parameter always pays for. Everything else erases to `unknown`, the
            // same conversion an ordinary argument of that type goes through - a reference already
            // needs none of it, and only a nullable primitive or a value class actually costs a box.
            if (!operand.Type.IsPrimitive || operand.Type.IsNullable)
                operand = Convert(operand, _factory.Unknown, operandSyntax.Span);

            return new BoundTypeOfExpression(syntax, null, operand, resultType);
        }

        /// <summary>Resolves a built-in type by name, the same way <c>attribute class</c> resolves <c>Attribute</c> with no explicit base.</summary>
        private TypeSymbol ResolveBuiltInType(string name, SourceSpan span)
            => _resolver.Resolve(new NamedTypeSyntax(span, new[] { name }, System.Array.Empty<TypeSyntax>()), _typeScope, _sourceName);
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
            foreach (var arm in arms)
            {
                if (arm.IsDefault)
                    return;
            }

            // Everything else has an open set of values, so it needs an `else` to say what a switch
            // produces when nothing matched — a switch *expression* has to produce something.
            // Reported here rather than at emit, where it used to surface as "not lowered yet": it
            // is a property of the program, not a construct the compiler has not got to.
            if (subject.Type.NonNullable is not NamedTypeSymbol { TypeKind: TypeSymbolKind.Enum } @enum
                || subject.Type.IsNullable)
            {
                Report(
                    SurtrDiagnosticCode.SwitchNotExhaustive,
                    syntax.Span,
                    subject.Type.IsNullable
                        ? $"'{subject.Type.ToDisplayString()}' can also be null, so this switch needs an 'else' arm."
                        : $"'{subject.Type.ToDisplayString()}' has no fixed set of values, so this switch needs an 'else' arm.");

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

            // A lambda's parameter is a name in the enclosing body's chain like any other, so §4.4's
            // rule reaches it: the body it belongs to is the same one, and the frame it will run on
            // holds both.
            for (int i = 0; i < parameters.Length; i++)
            {
                ReportIfTaken(parameters[i].Name, syntax.Parameters[i].Span);
                _values.TryDeclare(parameters[i].Name, parameters[i]);
            }

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
