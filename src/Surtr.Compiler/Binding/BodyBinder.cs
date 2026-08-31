#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.CodeGen;
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

        /// <summary>
        /// Whether this build asks for <c>@Range</c> runtime checks (§P4). Backed by the
        /// <c>Debug</c> build constant: defined, the binder inserts a range check on every
        /// assignment to a ranged field or property-set; absent, the check costs nothing at all.
        /// </summary>
        private readonly bool _rangeChecksEnabled;

        /// <summary>Folds <c>@Pure</c> calls with constant arguments, when this compilation builds one.</summary>
        private readonly ConstFolder? _pureFolder;

        // Modules a wildcard import brought in. §2.5 makes a module a container of members, so its
        // functions and variables are in scope unqualified exactly as its types are. A named or
        // selective import that reached a module-level member carries a filter naming exactly
        // those members, so nothing else from the module leaks into bare-name resolution.
        private readonly IReadOnlyList<ImportedModule> _imported;

        /// <summary>
        /// What a symbol reads as right now — the type a condition proved for it, keyed by the
        /// symbol itself (so shadowing, already handled by <see cref="Scope"/>, needs no second
        /// mechanism). Empty is the un-narrowed state: every read is the declared type.
        /// </summary>
        private Dictionary<Symbol, TypeSymbol> _narrowed = new Dictionary<Symbol, TypeSymbol>();

        /// <summary>
        /// One snapshot of <see cref="_narrowed"/> per branch being bound, so popping a branch
        /// restores exactly what held before it — a nested <c>if</c> cannot lose the narrowing an
        /// outer one proved.
        /// </summary>
        private readonly Stack<Dictionary<Symbol, TypeSymbol>> _narrowingStack = new Stack<Dictionary<Symbol, TypeSymbol>>();

        /// <summary>
        /// A local declared <c>const</c> (§7.1), by the folded value it reads as everywhere it is
        /// used. Keyed by the symbol itself rather than by name, so shadowing across nested blocks —
        /// already handled correctly by <see cref="Scope"/> for an ordinary local — needs no second
        /// mechanism here: two different <see cref="LocalSymbol"/> instances named the same never
        /// collide, unlike the module-wide <see cref="ConstantEvaluator"/>'s flat name table.
        /// </summary>
        private readonly Dictionary<LocalSymbol, object?> _localConstants = new Dictionary<LocalSymbol, object?>();

        private Scope _values;
        private int _loopDepth;
        private readonly List<string> _loopLabels = new List<string>();

        /// <summary>
        /// How many enclosing <c>finally</c> blocks a statement is inside (§3.7's yield rule).
        /// </summary>
        /// <remarks>
        /// A <c>finally</c> and not a <c>try</c>, which is the whole of what §9.2 bought: a
        /// suspended generator can now be closed, and closing one runs its pending <c>finally</c>
        /// blocks, so a <c>yield</c> inside a <c>try</c> no longer leaves work that nothing is
        /// obliged to run. Inside the <c>finally</c> itself it stays refused - that block runs
        /// <em>during</em> a close, and suspending there would answer a close with an element.
        /// </remarks>
        private int _finallyDepth;

        /// <summary>
        /// How many <c>yield</c>s this body has, so a generator that never yields can be reported.
        /// </summary>
        /// <remarks>
        /// Counted here rather than found by a later walk because the binder is already visiting
        /// every statement, and because the answer is about the <em>declaration</em> - it is
        /// reported once, against the generator, not at any statement.
        /// </remarks>
        private int _yieldCount;

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
            IReadOnlyList<ImportedModule>? imported = null,
            bool rangeChecksEnabled = false,
            ConstFolder? pureFolder = null)
        {
            _imported = imported ?? Array.Empty<ImportedModule>();
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
            _rangeChecksEnabled = rangeChecksEnabled;
            _pureFolder = pureFolder;

            _values = new Scope();
            foreach (var parameter in method.Parameters)
                _values.TryDeclare(parameter.Name, parameter);
        }

        /// <summary>The method whose body this binds.</summary>
        public MethodSymbol Method => _method;

        /// <summary>Binds a whole body.</summary>
        public BoundBlockStatement BindBody(BlockStatementSyntax body)
        {
            var bound = (BoundBlockStatement)BindBlock(body);

            // Legal - an empty generator is a genuine base case, the way `inorder(null)` wants to
            // produce nothing - but far more often an omission, so it warns rather than passing in
            // silence. Reported against the declaration, since there is no statement to point at.
            if (_method.IsGenerator && _yieldCount == 0)
            {
                _diagnostics.ReportWarning(
                    SurtrDiagnosticCode.GeneratorNeverYields,
                    $"'{_method.Name}' is a generator but contains no 'yield', so iterating it produces nothing.",
                    _sourceName,
                    body.Span);
            }

            return bound;
        }

        /// <summary>
        /// Binds a field's initializer against the field's own type.
        /// </summary>
        /// <remarks>
        /// An initializer is not a body, but it is an expression in a member's scope and it runs —
        /// a static one from the declaring type's initializer, an instance one from every
        /// constructor. Binding it here rather than at emit is what puts its conversions in the
        /// tree like everything else's. A <c>@Range</c> mark on the field wraps the value in its
        /// guard, so a field that is declared out of range fails at construction rather than
        /// silently.
        /// </remarks>
        public BoundExpression BindInitializer(ExpressionSyntax syntax, TypeSymbol declared, FieldSymbol field)
        {
            var value = BindConverted(syntax, declared);
            return RangeCheckValue(syntax, value, field, field.Type);
        }

        /// <summary>Binds one enum case as the construction it is (§2.4).</summary>
        public BoundExpression BindEnumCase(EnumCaseSyntax syntax, NamedTypeSymbol @enum)
        {
            int value = CaseValueOf(syntax, @enum);

            // §2.2: an enum is a value class whose first field is `value`, so a case whose enum
            // has no fields besides it IS that value — the case reads as the literal, which is
            // what makes `Suit.Hearts` usable wherever a constant is. That covers a @Flags enum
            // (its value is a single bit) and a plain enum that carries nothing.
            if (@enum.IsFlagsEnum || !HasUserInstanceFields(@enum))
                return new BoundLiteralExpression(syntax, @enum, (long)value);

            // A multi-field enum builds a real value: the case's constructor arguments, with the
            // case value attached so the emitter can fill the `value` slot of the constructed
            // block (§2.2) — the field is never a constructor parameter.
            return WithEnumValue(BindObjectCreation(syntax, syntax.Arguments, @enum), value);
        }

        /// <summary>
        /// Rebuilds a construction carrying the enum case value it stores in the synthetic
        /// <c>value</c> field, so the emitter can put it in the right slot of the built block.
        /// </summary>
        private static BoundExpression WithEnumValue(BoundExpression construction, int value)
            => construction is BoundObjectCreationExpression creation
                ? new BoundObjectCreationExpression(creation.Syntax, (NamedTypeSymbol)creation.Type, creation.Constructor, creation.Arguments, value)
                : construction;

        /// <summary>Whether the enum declares any instance field besides the synthetic <c>value</c>.</summary>
        private static bool HasUserInstanceFields(NamedTypeSymbol @enum)
        {
            int fields = 0;
            foreach (var member in @enum.Members)
            {
                if (member is FieldSymbol { IsStatic: false })
                {
                    fields++;
                    if (fields > 1)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A case's value in its enum: the <c>= n</c> written on it, or its position in the
        /// declaration, which for a <c>@Flags</c> enum is the bit it stands for.
        /// </summary>
        /// <remarks>
        /// Read off the enum's own member list rather than passed in, so it agrees with the value
        /// the declaration phase stored on the case's field — the two coming apart would give one
        /// case two values with nothing to notice.
        /// </remarks>
        private static int CaseValueOf(EnumCaseSyntax syntax, NamedTypeSymbol @enum)
        {
            int ordinal = 0;

            foreach (var member in @enum.Members)
            {
                if (member is not FieldSymbol { IsStatic: true } @case || !ReferenceEquals(@case.Type, @enum))
                    continue;

                if (string.Equals(@case.Name, syntax.Name, StringComparison.Ordinal))
                    return @case.EnumValue ?? (1 << ordinal);

                ordinal++;
            }

            return 1 << ordinal;
        }

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
        /// Declares a local, reporting a name that is already taken anywhere it can be seen from.
        /// </summary>
        /// <remarks>
        /// §4.4 makes shadowing an outer local an error rather than a nested block's privilege: two
        /// locals of the same name carry no information, so one of them is dead or the author lost
        /// track. A field is the deliberate exception — fields are not in this chain at all, which is
        /// what leaves <c>this.x = x</c> legal without a rule of its own.
        /// </remarks>
        private LocalSymbol DeclareLocal(string name, TypeSymbol type, bool isReadOnly, SourceSpan span)
        {
            var local = new LocalSymbol(name, type, isReadOnly);
            ReportIfTaken(name, span);

            // Declared even when it was taken, so what follows reads the declaration that was
            // written rather than the one it shadowed - one report instead of a cascade.
            _values.TryDeclare(name, local);
            return local;
        }

        /// <summary>Reports a name a nearer or an enclosing scope has already given to something.</summary>
        private void ReportIfTaken(string name, SourceSpan span)
        {
            if (_values.LookupLocal(name).IsFound)
            {
                Report(SurtrDiagnosticCode.DuplicateDeclaration, span, $"'{name}' is already declared in this block.");
                return;
            }

            if (_values.Lookup(name).IsFound)
            {
                Report(
                    SurtrDiagnosticCode.DuplicateDeclaration,
                    span,
                    $"'{name}' already exists in an enclosing scope, and a local may not shadow another local.");
            }
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

        #region Nullability and type narrowing
        /// <summary>
        /// What a condition proves about a nullable or typed local while the condition holds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Only the shapes that carry their proof on their face: <c>x != null</c>, <c>x is T</c>,
        /// and the <c>&amp;&amp;</c> of two such. That is where the value is — a null check followed
        /// by a use — and stopping there keeps the rule something a reader can predict, which
        /// matters more here than covering one more shape. A <c>== null</c> proves the value null,
        /// which no read wants, and the positive half of an <c>||</c> does not say which side was
        /// true, so neither records anything.
        /// </para>
        /// <para>
        /// <c>x is T</c> narrows the <em>type</em>, not just the nullability: inside the branch the
        /// value reads as <c>T</c>, which is what makes a downcast to <c>T</c> the check's own
        /// consequence rather than a written cast.
        /// </para>
        /// <para>
        /// Narrowing is scoped to the branch and undone on the way out, so nothing leaks past the
        /// condition that proved it — except a guard clause, where the branch is a <c>return</c> or
        /// a <c>throw</c> and the condition's negation therefore holds for the rest of the block.
        /// </para>
        /// </remarks>
        private Dictionary<Symbol, TypeSymbol> NarrowingsFrom(ExpressionSyntax condition)
        {
            var narrowed = new Dictionary<Symbol, TypeSymbol>();
            Collect(condition, narrowed);
            return narrowed;
        }

        /// <summary>
        /// What the false half of <paramref name="condition"/> proves — the mirror of
        /// <see cref="NarrowingsFrom"/>, used for the <c>else</c> of an <c>if</c>, the second half
        /// of an <c>||</c>, and the statement after a guard clause.
        /// </summary>
        private Dictionary<Symbol, TypeSymbol> NegatedNarrowingsFrom(ExpressionSyntax condition)
        {
            var narrowed = new Dictionary<Symbol, TypeSymbol>();
            CollectNegated(condition, narrowed);
            return narrowed;
        }

        private void Collect(ExpressionSyntax syntax, Dictionary<Symbol, TypeSymbol> into)
        {
            switch (syntax)
            {
                case BinaryExpressionSyntax { Operator: BinaryOperator.LogicalAnd } and:
                    Collect(and.Left, into);
                    Collect(and.Right, into);
                    return;

                case BinaryExpressionSyntax { Operator: BinaryOperator.NotEqual } test:
                    AddNonNullable(test.Left, test.Right, into);
                    return;

                case TypeTestExpressionSyntax { Operand: IdentifierExpressionSyntax operand } test:
                    AddTyped(operand.Name, test.TargetType, into);
                    return;
            }
        }

        private void CollectNegated(ExpressionSyntax syntax, Dictionary<Symbol, TypeSymbol> into)
        {
            switch (syntax)
            {
                // !(x == null) is x != null.
                case BinaryExpressionSyntax { Operator: BinaryOperator.Equal } test:
                    AddNonNullable(test.Left, test.Right, into);
                    return;

                // !(a || b) is !a && !b: both sides are false.
                case BinaryExpressionSyntax { Operator: BinaryOperator.LogicalOr } or:
                    CollectNegated(or.Left, into);
                    CollectNegated(or.Right, into);
                    return;
            }
        }

        private void AddNonNullable(ExpressionSyntax left, ExpressionSyntax right, Dictionary<Symbol, TypeSymbol> into)
        {
            if (IsNullLiteral(right) && left is IdentifierExpressionSyntax leftName)
                Add(leftName.Name, symbol => DeclaredTypeOf(symbol).NonNullable, into);
            else if (IsNullLiteral(left) && right is IdentifierExpressionSyntax rightName)
                Add(rightName.Name, symbol => DeclaredTypeOf(symbol).NonNullable, into);
        }

        private void AddTyped(string name, TypeSyntax targetType, Dictionary<Symbol, TypeSymbol> into)
        {
            var tested = _resolver.Resolve(targetType, _typeScope, _sourceName);
            if (tested.IsError)
                return;

            Add(name, symbol =>
            {
                var declared = DeclaredTypeOf(symbol);
                // Only the tested type when it fits inside the declared one — `x is T` proves T,
                // and only T, when it holds. An unrelated `is` can never be true; the branch is
                // dead, so it reads as the plain non-nullable form and nothing the emitter emits
                // ever runs.
                return !ReferenceEquals(tested, declared) && _conversions.IsAssignable(tested, declared)
                    ? tested
                    : declared.NonNullable;
            }, into);
        }

        private static TypeSymbol DeclaredTypeOf(Symbol symbol) => symbol switch
        {
            LocalSymbol local => local.Type,
            ParameterSymbol parameter => parameter.Type,
            _ => throw new ArgumentOutOfRangeException(nameof(symbol)),
        };

        private void Add(string name, Func<Symbol, TypeSymbol> typeOf, Dictionary<Symbol, TypeSymbol> into)
        {
            var found = _values.Lookup(name).Symbol;
            if (found is LocalSymbol or ParameterSymbol)
                into[found] = typeOf(found);
        }

        private static bool IsNullLiteral(ExpressionSyntax syntax)
            => syntax is LiteralExpressionSyntax { Literal.Type: TokenType.KeywordNull };

        /// <summary>Snapshot of the current narrowings, saved and restored around a branch or a block.</summary>
        private Dictionary<Symbol, TypeSymbol> SnapshotNarrowings() => new Dictionary<Symbol, TypeSymbol>(_narrowed);

        private void RestoreNarrowings(Dictionary<Symbol, TypeSymbol> snapshot) => _narrowed = snapshot;

        private void PushNarrowings(Dictionary<Symbol, TypeSymbol> narrowings)
        {
            var snapshot = SnapshotNarrowings();
            foreach (var entry in narrowings)
                _narrowed[entry.Key] = entry.Value;
            _narrowingStack.Push(snapshot);
        }

        private void PopNarrowings() => _narrowed = _narrowingStack.Pop();

        /// <summary>Proves a condition's negation for the rest of the enclosing block — a guard clause.</summary>
        private void KeepNarrowings(Dictionary<Symbol, TypeSymbol> narrowings)
        {
            foreach (var entry in narrowings)
                _narrowed[entry.Key] = entry.Value;
        }

        /// <summary>Whether a statement always leaves the enclosing block, never falling through.</summary>
        private static bool Terminates(BoundStatement statement) => statement switch
        {
            BoundReturnStatement or BoundThrowStatement => true,
            BoundBlockStatement block => block.Statements.Count > 0 && Terminates(block.Statements[block.Statements.Count - 1]),
            BoundLabeledStatement labeled => Terminates(labeled.Statement),
            BoundIfStatement @if => @if.Else is not null && Terminates(@if.Then) && Terminates(@if.Else),
            _ => false,
        };

        /// <summary>A write to a local or a parameter stops whatever a condition proved about it.</summary>
        private void InvalidateNarrowing(BoundExpression target)
        {
            switch (target)
            {
                case BoundLocalExpression local:
                    _narrowed.Remove(local.Local);
                    break;
                case BoundParameterExpression parameter:
                    _narrowed.Remove(parameter.Parameter);
                    break;
            }
        }

        /// <summary>The type a symbol reads as here, which is its own unless a condition narrowed it.</summary>
        private TypeSymbol TypeOf(Symbol symbol, TypeSymbol declared)
            => _narrowed.TryGetValue(symbol, out var narrowed) ? narrowed : declared;
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

        /// <summary>
        /// Resolves a callee that names a generic type <em>declaration</em> — the form a construction
        /// takes before its type arguments are settled (§6).
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="TryBindAsType"/> and deliberately so: that one answers "is this a
        /// type", and a generic declaration with no arguments is not one — <c>Box</c> is not a type,
        /// <c>Box&lt;int&gt;</c> is. What it is, is the only thing a construction site can start from,
        /// so this answers the narrower question and the caller finishes the job.
        /// </remarks>
        private bool TryBindAsGenericDefinition(ExpressionSyntax syntax, out List<NamedTypeSymbol> definitions)
        {
            definitions = null!;

            var path = new List<string>();
            if (!TryFlatten(syntax, path) || path.Count != 1)
                return false;

            var found = _typeScope.Lookup(path[0]);

            // Every arity under the name, not the first: §6 makes `Result<T>` and `Result<T, E>` two
            // unrelated declarations that share a spelling, and which one a construction means is
            // decided by what fills its parameters rather than by declaration order.
            foreach (var candidate in found.IsAmbiguous ? found.Candidates : Single(found.Symbol))
            {
                if (candidate is NamedTypeSymbol { Arity: > 0 } named && !named.IsConstructed)
                    (definitions ??= new List<NamedTypeSymbol>()).Add(named);
            }

            return definitions is not null;

            static IReadOnlyList<Symbol> Single(Symbol? symbol)
                => symbol is null ? System.Array.Empty<Symbol>() : new[] { symbol };
        }

        /// <summary>
        /// Resolves a generic type name written in expression position — the target of a static
        /// member access: <c>Box&lt;int&gt;.prop</c> or the open <c>Box&lt;&gt;.prop</c>. The written
        /// arguments construct the type; a wildcard slot names the open declaration of that arity.
        /// </summary>
        /// <remarks>
        /// Only the declaration is searched, exactly like <see cref="TryBindAsGenericDefinition"/>:
        /// a name that also names a constructed type in the scope is not a definition. And only a
        /// generic declaration answers — arity part of identity, so a matching arity is required.
        /// </remarks>
        private bool TryBindGenericName(GenericNameExpressionSyntax syntax, out NamedTypeSymbol type)
        {
            type = null!;

            var found = _typeScope.Lookup(syntax.Name);
            bool wildcard = false;
            foreach (var argument in syntax.TypeArguments)
            {
                if (argument is WildcardTypeSyntax)
                    wildcard = true;
            }

            foreach (var candidate in found.IsAmbiguous ? found.Candidates : Single(found.Symbol))
            {
                if (candidate is not NamedTypeSymbol { Arity: > 0 } named || named.IsConstructed)
                    continue;

                if (named.Arity != syntax.TypeArguments.Count)
                    continue;

                if (wildcard)
                {
                    // The open form: the declaration itself, whose statics are shared by every
                    // construction (erasure — one class, one table, one body).
                    type = named;
                    return true;
                }

                var arguments = new TypeSymbol[syntax.TypeArguments.Count];
                for (int i = 0; i < arguments.Length; i++)
                {
                    arguments[i] = _resolver.Resolve(syntax.TypeArguments[i], _typeScope, _sourceName);
                    if (arguments[i].IsError)
                        return false;
                }

                type = named.Construct(arguments);
                return true;
            }

            return false;

            static IReadOnlyList<Symbol> Single(Symbol? symbol)
                => symbol is null ? System.Array.Empty<Symbol>() : new[] { symbol };
        }

        /// <summary>
        /// Whether a static member reached through the <em>open</em> form of a generic type —
        /// <c>Box&lt;&gt;.member</c> — depends on the type's own parameters, in which case the access
        /// has to name a construction (<c>Box&lt;int&gt;.member</c>) so the type is substituted.
        /// </summary>
        private bool MemberDependsOnOpenType(NamedTypeSymbol openType, TypeSymbol memberType)
        {
            if (openType.IsConstructed || openType.Arity == 0)
                return false;

            var substitution = _factory.BeginSubstitution();
            foreach (var parameter in openType.TypeParameters)
                substitution.Add(parameter, _factory.ErrorType);

            return !ReferenceEquals(memberType.Substitute(substitution.ToSubstitution()), memberType);
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
            if (destination.IsError)
                return expression;

            // A `null` literal bound with no expected type (as every argument is, before overload
            // resolution has picked a parameter to convert it against - see BindArguments) carries
            // ErrorType as a placeholder for "not yet contextualized", not a genuine binding
            // failure. That has to be told apart from a real error *before* the general IsError
            // bail-out below, or this case would return the still-untyped literal unconverted
            // instead of ever reaching the null-specific handling that exists for exactly this.
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

            if (ReferenceEquals(expression.Type, destination) || expression.Type.IsError)
                return expression;

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
