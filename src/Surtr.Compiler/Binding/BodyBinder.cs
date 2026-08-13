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
    /// <summary>
    /// Binds one method body: every name resolved, every type checked, every conversion made
    /// explicit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the binder's third phase and runs one body at a time, in any order, because phase 2
    /// already settled every type's surface. Nothing here can add to a type or change a signature.
    /// </para>
    /// <para>
    /// Two scopes run in parallel and never mix, because §1.1 puts type names and value names in
    /// separate namespaces: the <em>type</em> scope comes from the declaration and is only read,
    /// while the <em>value</em> scope grows and shrinks with each block as locals come into view.
    /// </para>
    /// <para>
    /// Binding never returns null and never throws on bad input. An expression that cannot be
    /// understood becomes an error expression typed as the error type, which converts to and from
    /// everything silently — so one mistake reports once instead of cascading down every rule its
    /// value would have flowed through.
    /// </para>
    /// </remarks>
    public sealed partial class BodyBinder
    {
        private static readonly IReadOnlyList<BoundExpression> NoArguments = Array.Empty<BoundExpression>();

        private readonly TypeSymbolFactory _factory;
        private readonly TypeResolver _resolver;
        private readonly Conversions _conversions;
        private readonly MemberLookup _lookup;
        private readonly OverloadResolution _overloads;
        private readonly ConstantEvaluator _constants;
        private readonly SurtrDiagnosticBag _diagnostics;
        private readonly string _sourceName;

        private readonly Scope _typeScope;
        private readonly NamedTypeSymbol? _containingType;
        private readonly ModuleSymbol _module;
        private readonly MethodSymbol _method;

        // Modules a wildcard import brought in. §2.5 makes a module a container of members, so its
        // functions and variables are in scope unqualified exactly as its types are.
        private readonly IReadOnlyList<ModuleSymbol> _imported;

        private readonly HashSet<Symbol> _narrowed = new HashSet<Symbol>();

        private Scope _values;
        private int _loopDepth;
        private readonly List<string> _loopLabels = new List<string>();

        /// <summary>One lambda whose body is being bound, and what it has been found to capture.</summary>
        /// <remarks>
        /// A stack rather than a single frame, because a lambda inside a lambda reading an outer
        /// local makes <em>both</em> capture it: the inner one can only get the value from
        /// somewhere, and its only source is the outer body's own upvalue.
        /// </remarks>
        private sealed class LambdaFrame
        {
            public LambdaFrame(Scope boundary) => Boundary = boundary;

            /// <summary>The lambda's own scope; anything declared at or below it is not a capture.</summary>
            public Scope Boundary { get; }

            public List<Symbol> Captures { get; } = new List<Symbol>();

            /// <summary>
            /// Whether it reads the enclosing instance. Separate from <see cref="Captures"/> because
            /// <c>this</c> is not a symbol, and a lifted body is a static function that still has to
            /// receive it as an upvalue.
            /// </summary>
            public bool CapturesReceiver { get; set; }
        }

        private readonly List<LambdaFrame> _lambdas = new List<LambdaFrame>();

        internal BodyBinder(
            TypeSymbolFactory factory,
            TypeResolver resolver,
            Conversions conversions,
            MemberLookup lookup,
            OverloadResolution overloads,
            ConstantEvaluator constants,
            SurtrDiagnosticBag diagnostics,
            string sourceName,
            Scope typeScope,
            ModuleSymbol module,
            NamedTypeSymbol? containingType,
            MethodSymbol method,
            IReadOnlyList<ModuleSymbol>? imported = null)
        {
            _imported = imported ?? Array.Empty<ModuleSymbol>();
            _factory = factory;
            _resolver = resolver;
            _conversions = conversions;
            _lookup = lookup;
            _overloads = overloads;
            _constants = constants;
            _diagnostics = diagnostics;
            _sourceName = sourceName;
            _typeScope = typeScope;
            _module = module;
            _containingType = containingType;
            _method = method;

            _values = new Scope();
            foreach (var parameter in method.Parameters)
                _values.TryDeclare(parameter.Name, parameter);
        }

        /// <summary>The method whose body this binds.</summary>
        public MethodSymbol Method => _method;

        /// <summary>Binds a whole body.</summary>
        public BoundBlockStatement BindBody(BlockStatementSyntax body) => (BoundBlockStatement)BindBlock(body);

        /// <summary>
        /// Binds a field's initializer against the field's own type.
        /// </summary>
        /// <remarks>
        /// An initializer is not a body, but it is an expression in a member's scope and it runs —
        /// a static one from the declaring type's initializer, an instance one from every
        /// constructor. Binding it here rather than at emit is what puts its conversions in the
        /// tree like everything else's.
        /// </remarks>
        public BoundExpression BindInitializer(ExpressionSyntax syntax, TypeSymbol declared)
            => BindConverted(syntax, declared);

        /// <summary>Binds one enum case as the construction it is (§2.4).</summary>
        public BoundExpression BindEnumCase(EnumCaseSyntax syntax, NamedTypeSymbol @enum)
            => BindObjectCreation(syntax, syntax.Arguments, @enum);

        /// <summary>Binds a <c>singleton</c>'s one instance, which its module builds at load (§2.8).</summary>
        /// <remarks>
        /// A construction with no constructor, because §2.8 forbids one: what varies between two
        /// singletons is their field initializers, and those run from the constructor the emitter
        /// synthesises like any other class's.
        /// </remarks>
        public BoundExpression BindSingletonInstance(NamedTypeSymbol singleton, SyntaxNode syntax)
            => new BoundObjectCreationExpression(syntax, singleton, constructor: null, NoArguments);

        #region Scopes and lookup
        private Scope PushScope()
        {
            var previous = _values;
            _values = _values.CreateChild();
            return previous;
        }

        private void PopScope(Scope previous) => _values = previous;

        /// <summary>
        /// Declares a local, reporting a name already taken in the same block.
        /// </summary>
        /// <remarks>
        /// Shadowing an outer local is allowed and is what a nested block is for; declaring the
        /// same name twice in one block is not.
        /// </remarks>
        private LocalSymbol DeclareLocal(string name, TypeSymbol type, bool isReadOnly, SourceSpan span)
        {
            var local = new LocalSymbol(name, type, isReadOnly);

            if (!_values.TryDeclare(name, local))
            {
                Report(SurtrDiagnosticCode.DuplicateDeclaration, span, $"'{name}' is already declared in this block.");
            }

            return local;
        }

        /// <summary>
        /// Records that a lambda reads something declared outside it — on every lambda it is
        /// outside of.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A capture is copied into the closure rather than shared through a cell, which is only
        /// sound for something that never changes — so this is also where the effectively-final
        /// rule is enforced, at the point the capture happens rather than after the fact.
        /// </para>
        /// <para>
        /// The walk goes outwards and stops at the first lambda the symbol is inside, because every
        /// lambda beyond that one contains the declaration too. Stopping earlier is what would break
        /// nesting: an inner lambda's upvalue has to come from the outer body, so the outer lambda
        /// has to have captured it as well.
        /// </para>
        /// </remarks>
        private void NoteCapture(Symbol symbol, SourceSpan span)
        {
            if (_lambdas.Count == 0)
                return;

            var declaring = ScopeDeclaring(symbol);
            if (declaring is null)
                return;

            for (int i = _lambdas.Count - 1; i >= 0; i--)
            {
                var frame = _lambdas[i];

                // Anything the lambda itself declared is not a capture — including its parameters,
                // which live in the boundary scope rather than inside it.
                if (IsWithin(declaring, frame.Boundary))
                    return;

                if (symbol is LocalSymbol local)
                {
                    if (!local.IsReadOnly)
                    {
                        Report(
                            SurtrDiagnosticCode.NotAssignable,
                            span,
                            $"'{local.Name}' is reassigned, so a lambda cannot capture it; declare it 'let'.");

                        return;
                    }

                    local.IsCaptured = true;
                }

                if (!frame.Captures.Contains(symbol))
                    frame.Captures.Add(symbol);
            }
        }

        /// <summary>The innermost scope that declares <paramref name="symbol"/>, from where we are.</summary>
        private Scope? ScopeDeclaring(Symbol symbol)
        {
            for (Scope? scope = _values; scope is not null; scope = scope.Parent)
            {
                if (scope.LookupLocal(symbol.Name).Symbol is Symbol found && ReferenceEquals(found, symbol))
                    return scope;
            }

            return null;
        }

        /// <summary>Whether <paramref name="scope"/> is <paramref name="boundary"/> or nested inside it.</summary>
        private static bool IsWithin(Scope scope, Scope boundary)
        {
            for (Scope? walk = scope; walk is not null; walk = walk.Parent)
            {
                if (ReferenceEquals(walk, boundary))
                    return true;
            }

            return false;
        }

        /// <summary>Records that the enclosing instance was read, on every lambda that is inside it.</summary>
        private void NoteReceiverCapture()
        {
            foreach (var frame in _lambdas)
                frame.CapturesReceiver = true;
        }
        #endregion

        #region Nullability narrowing
        /// <summary>
        /// What a condition proves about a nullable local, for as long as it holds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only the shapes that carry their proof on their face: <c>x != null</c>, <c>x is T</c>,
        /// and the <c>&amp;&amp;</c> of two such. That is where the value is — a null check followed
        /// by a use — and stopping there keeps the rule something a reader can predict, which
        /// matters more here than covering one more shape.
        /// </para>
        /// <para>
        /// Narrowing is scoped to the branch and undone on the way out, so nothing leaks past the
        /// condition that proved it.
        /// </para>
        /// </remarks>
        private List<Symbol> NarrowingsFrom(ExpressionSyntax condition)
        {
            var narrowed = new List<Symbol>();
            Collect(condition, narrowed);
            return narrowed;

            void Collect(ExpressionSyntax syntax, List<Symbol> into)
            {
                switch (syntax)
                {
                    case BinaryExpressionSyntax { Operator: BinaryOperator.LogicalAnd } and:
                        Collect(and.Left, into);
                        Collect(and.Right, into);
                        return;

                    case BinaryExpressionSyntax { Operator: BinaryOperator.NotEqual } test:
                    {
                        if (IsNullLiteral(test.Right) && test.Left is IdentifierExpressionSyntax left)
                            Add(left.Name, into);
                        else if (IsNullLiteral(test.Left) && test.Right is IdentifierExpressionSyntax right)
                            Add(right.Name, into);

                        return;
                    }

                    case TypeTestExpressionSyntax { Operand: IdentifierExpressionSyntax operand }:
                        Add(operand.Name, into);
                        return;
                }
            }

            void Add(string name, List<Symbol> into)
            {
                var found = _values.Lookup(name).Symbol;
                if (found is LocalSymbol or ParameterSymbol)
                    into.Add(found);
            }
        }

        private static bool IsNullLiteral(ExpressionSyntax syntax)
            => syntax is LiteralExpressionSyntax { Literal.Type: TokenType.KeywordNull };

        private void PushNarrowings(List<Symbol> narrowed)
        {
            foreach (var symbol in narrowed)
                _narrowed.Add(symbol);
        }

        private void PopNarrowings(List<Symbol> narrowed)
        {
            foreach (var symbol in narrowed)
                _narrowed.Remove(symbol);
        }

        /// <summary>The type a symbol reads as here, which is its own unless a condition narrowed it.</summary>
        private TypeSymbol TypeOf(Symbol symbol, TypeSymbol declared)
            => _narrowed.Contains(symbol) ? declared.NonNullable : declared;
        #endregion

        #region Types from expressions
        /// <summary>
        /// Whether an expression is really a type name, which decides whether <c>Vec2(1, 2)</c> is
        /// a construction and whether <c>Suit.Hearts</c> is a static member.
        /// </summary>
        /// <remarks>
        /// Tried before value binding rather than after, because a name that resolves to a type is
        /// never also a value — §1.1's two namespaces make the question decidable rather than
        /// ambiguous.
        /// </remarks>
        private bool TryBindAsType(ExpressionSyntax syntax, out NamedTypeSymbol type)
        {
            type = null!;

            var path = new List<string>();
            if (!TryFlatten(syntax, path))
                return false;

            // A name that turns out to be an ordinary local is not a mistake, so this asks without
            // reporting and falls through to value binding when the answer is no.
            return _resolver.TryResolveTypeName(path, _typeScope, syntax.Span, out type);
        }

        private static bool TryFlatten(ExpressionSyntax syntax, List<string> path)
        {
            switch (syntax)
            {
                case IdentifierExpressionSyntax identifier:
                    path.Add(identifier.Name);
                    return true;

                case MemberAccessExpressionSyntax member when !member.IsNullConditional:
                    if (!TryFlatten(member.Target, path))
                        return false;

                    path.Add(member.Name);
                    return true;

                default:
                    return false;
            }
        }
        #endregion

        #region Conversions and diagnostics
        /// <summary>
        /// Converts an expression to what the surrounding context expects, reporting if it cannot.
        /// </summary>
        /// <remarks>
        /// The conversion becomes a node in the tree even when the source did not write one, so
        /// code generation never has to work out that an <c>int</c> argument needed widening to a
        /// <c>float</c> parameter — binding already decided.
        /// </remarks>
        private BoundExpression Convert(BoundExpression expression, TypeSymbol destination, SourceSpan span)
        {
            if (ReferenceEquals(expression.Type, destination) || destination.IsError || expression.Type.IsError)
                return expression;

            if (expression is BoundLiteralExpression { IsNull: true })
            {
                if (!_conversions.AcceptsNull(destination))
                {
                    Report(
                        SurtrDiagnosticCode.NullNotAllowed,
                        span,
                        $"'{destination.ToDisplayString()}' cannot hold null; write '{destination.ToDisplayString()}?' if it should.");

                    return new BoundErrorExpression(expression.Syntax, destination);
                }

                return new BoundLiteralExpression(expression.Syntax, destination, null);
            }

            var conversion = _conversions.Classify(expression.Type, destination);

            if (!conversion.IsImplicit)
            {
                Report(
                    SurtrDiagnosticCode.CannotConvert,
                    span,
                    conversion.Exists
                        ? $"'{expression.Type.ToDisplayString()}' does not become '{destination.ToDisplayString()}' on its own; write the cast."
                        : $"'{expression.Type.ToDisplayString()}' cannot be used where '{destination.ToDisplayString()}' is expected.");

                return new BoundErrorExpression(expression.Syntax, destination);
            }

            return conversion.IsIdentity
                ? expression
                : new BoundConversionExpression(expression.Syntax, expression, destination, conversion, isExplicit: false);
        }

        private void Report(SurtrDiagnosticCode code, SourceSpan span, string message)
            => _diagnostics.ReportError(code, message, _sourceName, span);

        private BoundErrorExpression Error(SyntaxNode syntax) => new BoundErrorExpression(syntax, _factory.ErrorType);

        private BoundErrorExpression Error(SyntaxNode syntax, SurtrDiagnosticCode code, string message)
        {
            Report(code, syntax.Span, message);
            return Error(syntax);
        }
        #endregion
    }
}
