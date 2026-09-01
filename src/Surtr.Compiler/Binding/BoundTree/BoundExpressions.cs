#nullable enable

using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding.BoundTree
{
    /// <summary>
    /// The base of the tree binding produces: the source's shape with every name resolved, every
    /// type known, and every conversion made explicit.
    /// </summary>
    /// <remarks>
    /// A bound node keeps the syntax it came from, so a later pass — code generation, or a
    /// diagnostic about something only it can see — can still point at the source. It keeps nothing
    /// else of the syntax: a name that resolved is a symbol here, not a string.
    /// </remarks>
    public abstract class BoundNode
    {
        private protected BoundNode(SyntaxNode syntax) => Syntax = syntax;

        /// <summary>The syntax this came from.</summary>
        public SyntaxNode Syntax { get; }

        /// <summary>Where in the source it is.</summary>
        public SourceSpan Span => Syntax.Span;
    }

    /// <summary>An expression, which always has a type.</summary>
    /// <remarks>
    /// Even a failed one: binding never returns null, so an expression that could not be understood
    /// is a <see cref="BoundErrorExpression"/> typed as the error type, and every rule that touches
    /// it stays quiet.
    /// </remarks>
    public abstract class BoundExpression : BoundNode
    {
        private protected BoundExpression(SyntaxNode syntax, TypeSymbol type) : base(syntax) => Type = type;

        /// <summary>What the expression evaluates to.</summary>
        public TypeSymbol Type { get; }

        /// <summary>Whether this expression can be assigned to.</summary>
        public virtual bool IsAssignable => false;
    }

    /// <summary>An expression that could not be bound. Reported once, then silent.</summary>
    public sealed class BoundErrorExpression : BoundExpression
    {
        internal BoundErrorExpression(SyntaxNode syntax, TypeSymbol type) : base(syntax, type)
        {
        }
    }

    /// <summary>A literal, with its value already parsed by the lexer.</summary>
    public sealed class BoundLiteralExpression : BoundExpression
    {
        internal BoundLiteralExpression(SyntaxNode syntax, TypeSymbol type, object? value) : base(syntax, type)
            => Value = value;

        /// <summary>The value, or <see langword="null"/> for the null literal.</summary>
        public object? Value { get; }

        /// <summary>Whether this is the null literal (§5.1), legal only against a nullable type.</summary>
        public bool IsNull => Value is null;
    }

    /// <summary>An interpolated string, already split into the parts that make it.</summary>
    public sealed class BoundInterpolatedStringExpression : BoundExpression
    {
        internal BoundInterpolatedStringExpression(SyntaxNode syntax, TypeSymbol type, IReadOnlyList<BoundExpression> parts)
            : base(syntax, type)
            => Parts = parts;

        /// <summary>The literal and interpolated pieces, in order.</summary>
        public IReadOnlyList<BoundExpression> Parts { get; }
    }

    /// <summary>A read of a local variable or a parameter.</summary>
    public sealed class BoundLocalExpression : BoundExpression
    {
        internal BoundLocalExpression(SyntaxNode syntax, LocalSymbol local) : base(syntax, local.Type)
            => Local = local;

        /// <summary>The local being read.</summary>
        public LocalSymbol Local { get; }

        /// <inheritdoc/>
        public override bool IsAssignable => !Local.IsReadOnly;
    }

    /// <summary>A read of a parameter.</summary>
    public sealed class BoundParameterExpression : BoundExpression
    {
        internal BoundParameterExpression(SyntaxNode syntax, ParameterSymbol parameter) : base(syntax, parameter.Type)
            => Parameter = parameter;

        /// <summary>The parameter being read.</summary>
        public ParameterSymbol Parameter { get; }

        /// <inheritdoc/>
        public override bool IsAssignable => true;
    }

    /// <summary>The receiver of an instance member, written or implied.</summary>
    public sealed class BoundThisExpression : BoundExpression
    {
        internal BoundThisExpression(SyntaxNode syntax, TypeSymbol type, bool isSuper) : base(syntax, type)
            => IsSuper = isSuper;

        /// <summary>Whether it was written <c>super</c>, which pins dispatch to the base class.</summary>
        public bool IsSuper { get; }
    }

    /// <summary>A field read.</summary>
    public sealed class BoundFieldExpression : BoundExpression
    {
        internal BoundFieldExpression(SyntaxNode syntax, BoundExpression? receiver, FieldSymbol field)
            : base(syntax, field.Type)
        {
            Receiver = receiver;
            Field = field;
        }

        /// <summary>The instance it is read from, or <see langword="null"/> for a static.</summary>
        public BoundExpression? Receiver { get; }

        /// <summary>The field.</summary>
        public FieldSymbol Field { get; }

        /// <inheritdoc/>
        public override bool IsAssignable => !Field.IsReadOnly;
    }

    /// <summary>A property read, which becomes a call to its getter.</summary>
    public sealed class BoundPropertyExpression : BoundExpression
    {
        internal BoundPropertyExpression(
            SyntaxNode syntax,
            BoundExpression? receiver,
            PropertySymbol property,
            bool isVirtualGet,
            bool isVirtualSet)
            : base(syntax, property.Type)
        {
            Receiver = receiver;
            Property = property;
            IsVirtualGet = isVirtualGet;
            IsVirtualSet = isVirtualSet;
        }

        /// <summary>The instance it is read from, or <see langword="null"/> for a static.</summary>
        public BoundExpression? Receiver { get; }

        /// <summary>The property.</summary>
        public PropertySymbol Property { get; }

        /// <summary>
        /// Whether a read goes through the getter's vtable slot. False for a static property, a
        /// `Direct` getter, a getter reached through `super`, one declared `sealed override`, or
        /// one on a sealed receiver — the same devirtualisation §2.2/§3.3 give an ordinary call
        /// (<see cref="BoundCallExpression.IsVirtual"/>), computed once here rather than
        /// re-derived at every accessor call site.
        /// </summary>
        public bool IsVirtualGet { get; }

        /// <summary>The setter's counterpart to <see cref="IsVirtualGet"/>.</summary>
        public bool IsVirtualSet { get; }

        /// <inheritdoc/>
        public override bool IsAssignable => Property.Setter is not null;
    }

    /// <summary>A call to a method the call site resolved to.</summary>
    public sealed class BoundCallExpression : BoundExpression
    {
        internal BoundCallExpression(
            SyntaxNode syntax,
            BoundExpression? receiver,
            MethodSymbol method,
            IReadOnlyList<BoundExpression> arguments,
            bool isVirtual)
            : base(syntax, method.ReturnType)
        {
            Receiver = receiver;
            Method = method;
            Arguments = arguments;
            IsVirtual = isVirtual;
        }

        /// <summary>The receiver, or <see langword="null"/> for a static or module-level call.</summary>
        public BoundExpression? Receiver { get; }

        /// <summary>The method the call resolved to.</summary>
        public MethodSymbol Method { get; }

        /// <summary>
        /// The arguments, already in parameter order with defaults filled in and varargs collected.
        /// </summary>
        public IReadOnlyList<BoundExpression> Arguments { get; }

        /// <summary>
        /// Whether the call goes through the vtable. False for a <c>super</c> call and for anything
        /// on a sealed receiver, which is the devirtualisation §2.2 mentions.
        /// </summary>
        public bool IsVirtual { get; }
    }

    /// <summary>
    /// A <c>?.</c> access: the receiver is evaluated once, and the access happens only if it is not
    /// null (§5.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A node of its own rather than a flag on each kind of access, because what makes <c>?.</c> what
    /// it is has nothing to do with which member is reached — it is that the receiver is evaluated
    /// exactly once and that a null one short-circuits the whole access. Both facts belong to the
    /// access as a unit, and a flag on a field read would have to be repeated on a property read and
    /// on a call and mean the same thing three times.
    /// </para>
    /// <para>
    /// <see cref="Access"/> reads its receiver through a
    /// <see cref="BoundConditionalReceiver"/> standing for the value already evaluated, which is what
    /// keeps <c>make()?.name</c> from calling <c>make()</c> twice. A chain nests: the receiver of the
    /// outer access is itself one of these.
    /// </para>
    /// </remarks>
    public sealed class BoundNullConditionalExpression : BoundExpression
    {
        internal BoundNullConditionalExpression(
            SyntaxNode syntax,
            BoundExpression receiver,
            BoundExpression access,
            TypeSymbol type)
            : base(syntax, type)
        {
            Receiver = receiver;
            Access = access;
        }

        /// <summary>The receiver being tested, evaluated once.</summary>
        public BoundExpression Receiver { get; }

        /// <summary>The access performed when the receiver is not null.</summary>
        public BoundExpression Access { get; }
    }

    /// <summary>
    /// Stands for the already-evaluated receiver inside a <see cref="BoundNullConditionalExpression"/>.
    /// </summary>
    public sealed class BoundConditionalReceiver : BoundExpression
    {
        internal BoundConditionalReceiver(SyntaxNode syntax, TypeSymbol type) : base(syntax, type)
        {
        }
    }

    /// <summary>
    /// A <c>!!</c> assertion: the operand, checked to be present right now (§5.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type it produces is the non-nullable one, which is the half the type checker cares about —
    /// but §5.1 says it <em>throws</em> when the assertion does not hold, and that is the half that
    /// makes it worth writing. Without the check the operator is a silent cast, which fails later and
    /// somewhere else, and the whole point of an escape hatch is that it fails where it was written.
    /// </para>
    /// <para>
    /// <see cref="Thrown"/> is bound here rather than built at emit because it is an ordinary
    /// construction of a library class, and the emitter has no way to resolve a name.
    /// </para>
    /// </remarks>
    public sealed class BoundNullAssertExpression : BoundExpression
    {
        internal BoundNullAssertExpression(SyntaxNode syntax, BoundExpression operand, TypeSymbol type, BoundExpression? thrown)
            : base(syntax, type)
        {
            Operand = operand;
            Thrown = thrown;
        }

        /// <summary>The value being asserted.</summary>
        public BoundExpression Operand { get; }

        /// <summary>
        /// The exception raised when it is null, or <see langword="null"/> when the standard library
        /// this compilation sees declares no <c>NullReferenceException</c> to raise.
        /// </summary>
        public BoundExpression? Thrown { get; }
    }

    /// <summary>A closure held in a value being invoked (§8).</summary>
    /// <remarks>
    /// Separate from <see cref="BoundCallExpression"/> because there is no method to name: the
    /// callee is a value whose closure type says what it takes and returns, and dispatch reads the
    /// payload the closure carries rather than any call site's token.
    /// </remarks>
    public sealed class BoundClosureInvocationExpression : BoundExpression
    {
        internal BoundClosureInvocationExpression(
            SyntaxNode syntax,
            BoundExpression callee,
            IReadOnlyList<BoundExpression> arguments,
            TypeSymbol type)
            : base(syntax, type)
        {
            Callee = callee;
            Arguments = arguments;
        }

        /// <summary>The value holding the closure.</summary>
        public BoundExpression Callee { get; }

        /// <summary>The arguments, in order.</summary>
        public IReadOnlyList<BoundExpression> Arguments { get; }
    }

    /// <summary>An object being created: <c>Vec2(1.0, 2.0)</c>.</summary>
    public sealed class BoundObjectCreationExpression : BoundExpression
    {
        internal BoundObjectCreationExpression(
            SyntaxNode syntax,
            NamedTypeSymbol type,
            MethodSymbol? constructor,
            IReadOnlyList<BoundExpression> arguments,
            long? enumValue = null)
            : base(syntax, type)
        {
            Constructor = constructor;
            Arguments = arguments;
            EnumValue = enumValue;
        }

        /// <summary>The constructor, or <see langword="null"/> for a type that declares none.</summary>
        public MethodSymbol? Constructor { get; }

        /// <summary>The arguments, in parameter order.</summary>
        public IReadOnlyList<BoundExpression> Arguments { get; }

        /// <summary>
        /// The value an enum case construction stores in its synthetic <c>value</c> field (§2.2),
        /// or <see langword="null"/> when this is not an enum case's construction. The field is
        /// never a constructor parameter — the emitter fills it as the first slot of the built
        /// block — so the value rides on the node itself.
        /// </summary>
        public long? EnumValue { get; }
    }

    /// <summary>A built-in binary operation between two primitives, strings or references.</summary>
    public sealed class BoundBinaryExpression : BoundExpression
    {
        internal BoundBinaryExpression(
            SyntaxNode syntax,
            BinaryOperator @operator,
            BoundExpression left,
            BoundExpression right,
            TypeSymbol type)
            : base(syntax, type)
        {
            Operator = @operator;
            Left = left;
            Right = right;
        }

        /// <summary>Which operation.</summary>
        public BinaryOperator Operator { get; }

        /// <summary>The left operand.</summary>
        public BoundExpression Left { get; }

        /// <summary>The right operand.</summary>
        public BoundExpression Right { get; }
    }

    /// <summary>A built-in unary operation.</summary>
    public sealed class BoundUnaryExpression : BoundExpression
    {
        internal BoundUnaryExpression(SyntaxNode syntax, UnaryOperator @operator, BoundExpression operand, TypeSymbol type)
            : base(syntax, type)
        {
            Operator = @operator;
            Operand = operand;
        }

        /// <summary>Which operation.</summary>
        public UnaryOperator Operator { get; }

        /// <summary>The operand.</summary>
        public BoundExpression Operand { get; }
    }

    /// <summary>An assignment, with any compound operator already applied to the value.</summary>
    public sealed class BoundAssignmentExpression : BoundExpression
    {
        internal BoundAssignmentExpression(SyntaxNode syntax, BoundExpression target, BoundExpression value)
            : base(syntax, target.Type)
        {
            Target = target;
            Value = value;
        }

        /// <summary>What is assigned to.</summary>
        public BoundExpression Target { get; }

        /// <summary>
        /// The value assigned. A compound assignment is already expanded, so <c>x += 1</c> arrives
        /// here as <c>x + 1</c> and nothing downstream needs a second form of assignment.
        /// </summary>
        public BoundExpression Value { get; }
    }

    /// <summary>A conditional expression, or the null-coalescing form.</summary>
    public sealed class BoundConditionalExpression : BoundExpression
    {
        internal BoundConditionalExpression(
            SyntaxNode syntax,
            BoundExpression condition,
            BoundExpression whenTrue,
            BoundExpression whenFalse,
            TypeSymbol type)
            : base(syntax, type)
        {
            Condition = condition;
            WhenTrue = whenTrue;
            WhenFalse = whenFalse;
        }

        /// <summary>The condition.</summary>
        public BoundExpression Condition { get; }

        /// <summary>The value when it holds.</summary>
        public BoundExpression WhenTrue { get; }

        /// <summary>The value when it does not.</summary>
        public BoundExpression WhenFalse { get; }
    }

    /// <summary>An indexed read: <c>xs[i]</c>.</summary>
    public sealed class BoundIndexExpression : BoundExpression
    {
        internal BoundIndexExpression(SyntaxNode syntax, BoundExpression target, BoundExpression index, TypeSymbol type)
            : base(syntax, type)
        {
            Target = target;
            Index = index;
        }

        /// <summary>What is being indexed.</summary>
        public BoundExpression Target { get; }

        /// <summary>The index.</summary>
        public BoundExpression Index { get; }

        /// <inheritdoc/>
        /// <remarks>
        /// An array slot and a dictionary entry can be written; a tuple element and a character of
        /// a string cannot, both being immutable once built (§5.5). There is no <c>TupSet</c> for
        /// exactly that reason.
        /// </remarks>
        public override bool IsAssignable
            => Target.Type.NonNullable.TypeKind is TypeSymbolKind.Array or TypeSymbolKind.Dictionary;
    }

    /// <summary>A conversion made explicit, whether or not it was written.</summary>
    public sealed class BoundConversionExpression : BoundExpression
    {
        internal BoundConversionExpression(
            SyntaxNode syntax,
            BoundExpression operand,
            TypeSymbol type,
            Conversion conversion,
            bool isExplicit)
            : base(syntax, type)
        {
            Operand = operand;
            Conversion = conversion;
            IsExplicit = isExplicit;
        }

        /// <summary>The value being converted.</summary>
        public BoundExpression Operand { get; }

        /// <summary>Which conversion applies.</summary>
        public Conversion Conversion { get; }

        /// <summary>Whether the source wrote it, rather than the binder inserting it.</summary>
        public bool IsExplicit { get; }

        /// <summary>Whether a failed conversion yields null instead of throwing (<c>as?</c>).</summary>
        public bool IsSafe { get; internal set; }
    }

    /// <summary>An <c>is</c> test, which evaluates to <c>bool</c> and casts nothing.</summary>
    public sealed class BoundTypeTestExpression : BoundExpression
    {
        internal BoundTypeTestExpression(SyntaxNode syntax, BoundExpression operand, TypeSymbol testedType, TypeSymbol type)
            : base(syntax, type)
        {
            Operand = operand;
            TestedType = testedType;
        }

        /// <summary>The value being tested.</summary>
        public BoundExpression Operand { get; }

        /// <summary>The type it is tested against.</summary>
        public TypeSymbol TestedType { get; }
    }

    /// <summary>
    /// <c>typeof(X)</c>. Exactly one of <see cref="TargetType"/> and <see cref="Operand"/> is set -
    /// the static form names a type directly and reads nothing at run time, the instance form
    /// evaluates an expression and reads its class. <see cref="BoundExpression.Type"/> is always
    /// the built-in <c>Type</c> class, whichever form this is.
    /// </summary>
    public sealed class BoundTypeOfExpression : BoundExpression
    {
        internal BoundTypeOfExpression(SyntaxNode syntax, TypeSymbol? targetType, BoundExpression? operand, TypeSymbol type)
            : base(syntax, type)
        {
            TargetType = targetType;
            Operand = operand;
        }

        /// <summary>The type named directly, for the static form. <see langword="null"/> for the instance form.</summary>
        public TypeSymbol? TargetType { get; }

        /// <summary>The value whose runtime type is read, for the instance form. <see langword="null"/> for the static form.</summary>
        public BoundExpression? Operand { get; }
    }

    /// <summary>
    /// <c>moduleof(ModulePath)</c> - the module a compile-time-known dotted path names (§2.1).
    /// Always static, unlike <see cref="BoundTypeOfExpression"/>: there is no instance form over
    /// an arbitrary value, so a resolved <see cref="ModuleSymbol"/> is the only shape this node
    /// ever carries.
    /// </summary>
    public sealed class BoundModuleOfExpression : BoundExpression
    {
        internal BoundModuleOfExpression(SyntaxNode syntax, ModuleSymbol module, TypeSymbol type)
            : base(syntax, type)
        {
            Module = module;
        }

        /// <summary>The module the path resolved to.</summary>
        public ModuleSymbol Module { get; }
    }

    /// <summary>A lambda, whose body is lifted to a static synthetic method at emit.</summary>
    public sealed class BoundLambdaExpression : BoundExpression
    {
        internal BoundLambdaExpression(
            SyntaxNode syntax,
            TypeSymbol type,
            IReadOnlyList<ParameterSymbol> parameters,
            BoundStatement body,
            IReadOnlyList<Symbol> captured,
            bool capturesReceiver,
            MethodSymbol? directTarget = null)
            : base(syntax, type)
        {
            Parameters = parameters;
            Body = body;
            Captured = captured;
            CapturesReceiver = capturesReceiver;
            DirectTarget = directTarget;
        }

        /// <summary>Its parameters.</summary>
        public IReadOnlyList<ParameterSymbol> Parameters { get; }

        /// <summary>Its body, always a statement so an expression body and a block are one shape.</summary>
        public BoundStatement Body { get; }

        /// <summary>
        /// The locals and parameters it reads from outside itself, which travel as construction
        /// arguments to the closure.
        /// </summary>
        public IReadOnlyList<Symbol> Captured { get; }

        /// <summary>
        /// Whether it reads the enclosing instance, written or implied.
        /// </summary>
        /// <remarks>
        /// Recorded here rather than left for the emitter to find, because <c>this</c> is not a
        /// symbol and so cannot sit in <see cref="Captured"/> — and because a closure's upvalues are
        /// fixed when it is built, so the emitter has to know before it pushes any of them. The
        /// lifted body is a static function, so the receiver has to arrive as a capture like
        /// anything else.
        /// </remarks>
        public bool CapturesReceiver { get; }

        /// <summary>
        /// The method this lambda is a direct method-group conversion of, or <c>null</c> for a
        /// lambda written by hand.
        /// </summary>
        /// <remarks>
        /// A method group whose conversion is exact — a static target whose parameter and return
        /// types are exactly the closure's — is sugar for "the function itself", so its function
        /// value can be built straight over the target instead of lifting a synthetic
        /// <c>$lambda$</c> forwarding method. The emitter skips the lift when this is set and the
        /// target's method builder is reachable.
        /// </remarks>
        public MethodSymbol? DirectTarget { get; }
    }

    /// <summary>An array literal.</summary>
    public sealed class BoundArrayLiteralExpression : BoundExpression
    {
        internal BoundArrayLiteralExpression(SyntaxNode syntax, TypeSymbol type, IReadOnlyList<BoundExpression> elements)
            : base(syntax, type)
            => Elements = elements;

        /// <summary>The elements, in order.</summary>
        public IReadOnlyList<BoundExpression> Elements { get; }
    }

    /// <summary>A tuple literal.</summary>
    public sealed class BoundTupleLiteralExpression : BoundExpression
    {
        internal BoundTupleLiteralExpression(SyntaxNode syntax, TypeSymbol type, IReadOnlyList<BoundExpression> elements)
            : base(syntax, type)
            => Elements = elements;

        /// <summary>The elements, in order.</summary>
        public IReadOnlyList<BoundExpression> Elements { get; }
    }

    /// <summary>One key/value pair of a dictionary literal.</summary>
    public readonly struct BoundDictEntry
    {
        internal BoundDictEntry(BoundExpression key, BoundExpression value)
        {
            Key = key;
            Value = value;
        }

        /// <summary>The key.</summary>
        public BoundExpression Key { get; }

        /// <summary>The value.</summary>
        public BoundExpression Value { get; }
    }

    /// <summary>A dictionary literal.</summary>
    public sealed class BoundDictLiteralExpression : BoundExpression
    {
        internal BoundDictLiteralExpression(SyntaxNode syntax, TypeSymbol type, IReadOnlyList<BoundDictEntry> entries)
            : base(syntax, type)
            => Entries = entries;

        /// <summary>The entries, in order.</summary>
        public IReadOnlyList<BoundDictEntry> Entries { get; }
    }

    /// <summary>
    /// A collection literal built over a named type's <c>each</c> constructor (§5.x) —
    /// <c>List&lt;int&gt;[1, 2, 3]</c>, <c>List&lt;int&gt;(32)[1, 2, 3]</c>,
    /// <c>Map&lt;string, int&gt;{ "x": 10 }</c>, or a target-typed <c>let l: List&lt;int&gt; = [1, 2, 3]</c>.
    /// The emitter lowers it to <c>ObjNew</c> + the constructor + one <c>$fill$</c> call per
    /// element/entry — never a copy constructor or a materialized intermediate array.
    /// </summary>
    public sealed class BoundCollectionBuildExpression : BoundExpression
    {
        internal BoundCollectionBuildExpression(
            SyntaxNode syntax,
            NamedTypeSymbol type,
            MethodSymbol constructor,
            MethodSymbol fillMethod,
            IReadOnlyList<BoundExpression> constructorArguments,
            IReadOnlyList<IReadOnlyList<BoundExpression>> fillArguments)
            : base(syntax, type)
        {
            Constructor = constructor;
            FillMethod = fillMethod;
            ConstructorArguments = constructorArguments;
            FillArguments = fillArguments;
        }

        /// <summary>The <c>each</c> constructor chosen — one per literal, by arity and argument list.</summary>
        public MethodSymbol Constructor { get; }

        /// <summary>The private <c>$fill$...</c> method called once per element/entry.</summary>
        public MethodSymbol FillMethod { get; }

        /// <summary>The constructor's arguments, in parameter order.</summary>
        public IReadOnlyList<BoundExpression> ConstructorArguments { get; }

        /// <summary>
        /// One list per element/entry: a single value for <c>[ ... ]</c>, a key/value pair for
        /// <c>{ ... }</c>. Each list fills one <c>$fill$</c> call, in literal order.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<BoundExpression>> FillArguments { get; }
    }

    /// <summary>
    /// Which shape a <see cref="BoundCollectionCreationExpression"/> takes. Kept as a flag rather
    /// than inferred from which optional fields are populated, since two kinds (<c>ArrayEmpty</c>
    /// and the unit <c>TupleEmpty</c>) populate none of them at all.
    /// </summary>
    public enum CollectionCreationKind
    {
        /// <summary>An empty array — <c>array&lt;T&gt;()</c>. Emits the same as a <c>[]</c> literal.</summary>
        ArrayEmpty,

        /// <summary>A zero-filled array of a given length — <c>array&lt;T&gt;(n)</c>.</summary>
        ArrayCapacity,

        /// <summary>An array built by reading every element of a tuple — <c>array&lt;T&gt;(aTuple)</c>.</summary>
        ArrayFromTuple,

        /// <summary>An empty dictionary — <c>dict&lt;K,V&gt;()</c>.</summary>
        DictEmpty,

        /// <summary>An empty dictionary with a reserved capacity — <c>dict&lt;K,V&gt;(n)</c>.</summary>
        DictCapacity,

        /// <summary>The 0-arity/unit tuple — <c>tuple&lt;&gt;()</c>.</summary>
        TupleEmpty,

        /// <summary>A tuple built by reading every element of an array — <c>tuple&lt;...&gt;(anArray)</c>.</summary>
        TupleFromArray,

        /// <summary>
        /// A length-<c>size</c> array where every slot holds <c>defaultValue</c> —
        /// <c>array&lt;T&gt;(size, defaultValue)</c>, only for the shapes that can't fold onto the
        /// zero-filling <see cref="ArrayCapacity"/> (a non-zero, or non-constant, default).
        /// </summary>
        ArraySizeDefault,

        /// <summary>An array copied element-by-element from another array — <c>array&lt;T&gt;(anotherArray)</c>.</summary>
        ArrayCopy,

        /// <summary>An array built by walking a generic <c>IIterable&lt;T&gt;</c> — <c>array&lt;T&gt;(anIterable)</c>.</summary>
        ArrayFromIterable,

        /// <summary>A dict built from an array of <c>(K,V)</c> pairs — <c>{K:V}(pairs)</c>.</summary>
        DictFromPairs,

        /// <summary>A dict built from two parallel arrays — <c>{K:V}(keys, values)</c>.</summary>
        DictFromParallelArrays,
    }

    /// <summary>
    /// A construction of <c>array</c>, <c>dict</c> or <c>tuple</c> through their nameable generic
    /// form — never through <see cref="BoundObjectCreationExpression"/>, whose emission is
    /// unconditionally <c>ObjNew</c>, which none of these three ever go through (CLAUDE.md's
    /// runtime-objects table: they are <c>SurtrArray</c>/<c>SurtrDictionary</c>/<c>SurtrTuple</c>,
    /// not a <c>SurtrInstance</c>). Every shape folds to the same allocation opcodes the equivalent
    /// literal already uses, plus at most one native call (<see cref="ReserveMethod"/>).
    /// </summary>
    public sealed class BoundCollectionCreationExpression : BoundExpression
    {
        internal BoundCollectionCreationExpression(
            SyntaxNode syntax,
            TypeSymbol type,
            CollectionCreationKind kind,
            BoundExpression? capacity = null,
            BoundExpression? source = null,
            BoundExpression? source2 = null,
            BoundExpression? defaultValue = null,
            IReadOnlyList<Conversion>? elementConversions = null,
            MethodSymbol? reserveMethod = null,
            BoundExpression? thrown = null,
            TypeSymbol? sourceElementType = null)
            : base(syntax, type)
        {
            Kind = kind;
            Capacity = capacity;
            Source = source;
            Source2 = source2;
            DefaultValue = defaultValue;
            ElementConversions = elementConversions;
            ReserveMethod = reserveMethod;
            Thrown = thrown;
            SourceElementType = sourceElementType;
        }

        /// <summary>Which shape this construction takes.</summary>
        public CollectionCreationKind Kind { get; }

        /// <summary>The requested length (array) or capacity hint (dict). Only set for the two Capacity kinds and <see cref="CollectionCreationKind.ArraySizeDefault"/>.</summary>
        public BoundExpression? Capacity { get; }

        /// <summary>
        /// The tuple, array or iterable being read from — for <see cref="CollectionCreationKind.ArrayFromTuple"/>,
        /// <see cref="CollectionCreationKind.TupleFromArray"/>, <see cref="CollectionCreationKind.ArrayCopy"/>,
        /// <see cref="CollectionCreationKind.ArrayFromIterable"/> and <see cref="CollectionCreationKind.DictFromPairs"/>
        /// (the pairs array); bound once, since the emitter reads it more than once (once per
        /// element, plus <c>ArrLen</c> on the runtime-length directions).
        /// </summary>
        public BoundExpression? Source { get; }

        /// <summary>The values array, for <see cref="CollectionCreationKind.DictFromParallelArrays"/> — <see cref="Source"/> is the keys array.</summary>
        public BoundExpression? Source2 { get; }

        /// <summary>The fill value, for <see cref="CollectionCreationKind.ArraySizeDefault"/>. Evaluated once, before the loop.</summary>
        public BoundExpression? DefaultValue { get; }

        /// <summary>
        /// One conversion per element/slot, in order, for the shapes that read an existing element
        /// into a new one — always <see cref="Conversion.IsImplicit"/>, since none of these ever
        /// consider a user-defined <c>operator as</c> (§5.6 makes those explicit-only). For
        /// <see cref="CollectionCreationKind.DictFromPairs"/> this is exactly two entries, key then
        /// value, regardless of the source array's runtime length.
        /// </summary>
        public IReadOnlyList<Conversion>? ElementConversions { get; }

        /// <summary>The built-in <c>dict</c>'s own <c>reserve</c> method, for <see cref="CollectionCreationKind.DictCapacity"/>.</summary>
        public MethodSymbol? ReserveMethod { get; }

        /// <summary>
        /// The exception construction to raise on a runtime shape mismatch —
        /// <c>InvalidCastException</c> for <see cref="CollectionCreationKind.TupleFromArray"/>'s arity
        /// check, <c>ArgumentException</c> for <see cref="CollectionCreationKind.DictFromParallelArrays"/>'s
        /// length check.
        /// </summary>
        public BoundExpression? Thrown { get; }

        /// <summary>
        /// What one step of <see cref="Source"/> yields, for <see cref="CollectionCreationKind.ArrayFromIterable"/>
        /// only. Every other kind can read its source element type straight off <see cref="Source"/>'s
        /// own static type (an array's element type, a pair tuple's slots) — a generic iterable
        /// cannot, since "what walking this yields" is a derived fact (<c>TryFindIterableElementType</c>),
        /// not something its own <see cref="BoundExpression.Type"/> encodes directly.
        /// </summary>
        public TypeSymbol? SourceElementType { get; }
    }

    /// <summary>One arm of a switch expression.</summary>
    public sealed class BoundSwitchArm
    {
        internal BoundSwitchArm(
            IReadOnlyList<BoundExpression> values,
            BoundExpression result,
            LocalSymbol? patternLocal = null,
            BoundExpression? guard = null)
        {
            Values = values;
            Result = result;
            PatternLocal = patternLocal;
            Guard = guard;
        }

        /// <summary>The values it matches, empty for the default arm and for a pattern arm.</summary>
        public IReadOnlyList<BoundExpression> Values { get; }

        /// <summary>What it evaluates to.</summary>
        public BoundExpression Result { get; }

        /// <summary>
        /// The local a type-pattern value (<c>x is Dog -&gt; ...</c>) binds, narrowed to the tested
        /// type and scoped to this arm alone. Null for an ordinary value arm.
        /// </summary>
        public LocalSymbol? PatternLocal { get; }

        /// <summary>The pattern's optional <c>if</c> guard. Only ever set when <see cref="PatternLocal"/> is.</summary>
        public BoundExpression? Guard { get; }

        /// <summary>Whether this is the arm nothing else matched.</summary>
        public bool IsDefault => Values.Count == 0 && PatternLocal is null;
    }

    /// <summary>A switch expression.</summary>
    public sealed class BoundSwitchExpression : BoundExpression
    {
        internal BoundSwitchExpression(
            SyntaxNode syntax,
            BoundExpression subject,
            IReadOnlyList<BoundSwitchArm> arms,
            TypeSymbol type)
            : base(syntax, type)
        {
            Subject = subject;
            Arms = arms;
        }

        /// <summary>What is being switched on.</summary>
        public BoundExpression Subject { get; }

        /// <summary>The arms, in order.</summary>
        public IReadOnlyList<BoundSwitchArm> Arms { get; }
    }

    /// <summary>
    /// A <c>throw</c> in expression position. Typed <c>never</c> — the bottom type — so it is
    /// assignable to whatever the surrounding expression needs and contributes nothing to a
    /// <c>?:</c>, <c>??</c> or switch-expression's common type.
    /// </summary>
    public sealed class BoundThrowExpression : BoundExpression
    {
        internal BoundThrowExpression(SyntaxNode syntax, BoundExpression value, TypeSymbol type)
            : base(syntax, type)
        {
            Value = value;
        }

        /// <summary>The thrown value.</summary>
        public BoundExpression Value { get; }
    }

    /// <summary>
    /// A <c>yield</c> or <c>yield from</c>, whose value is what the resumption carried back in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An expression rather than a statement, because a generator is a coroutine (§3.7): a
    /// <c>yield</c> evaluates to what <c>send(v)</c> injected, and a <c>yield from</c> to what the
    /// generator it delegated to returned. Both are <c>unknown</c> - a generator's declaration names
    /// its <em>element</em>, and there is nowhere in it to write a second type, so the value comes
    /// back erased and is cast at the point of use like anything else that holds anything (§5.10).
    /// </para>
    /// <para>
    /// The statement form costs nothing for being an expression: the emitter drops the resumed value
    /// where nothing reads it, which means not emitting the instruction that would have pushed it.
    /// </para>
    /// <para>
    /// The plain form's <see cref="Value"/> already carries its conversion to the declared element
    /// type, the way a <c>return</c>'s does - a <c>yield</c> is checked against
    /// <c>MethodSymbol.YieldType</c> by exactly the rules a <c>return</c> is checked against a
    /// return type, so nothing downstream needs a second set.
    /// </para>
    /// </remarks>
    public sealed class BoundYieldExpression : BoundExpression
    {
        internal BoundYieldExpression(
            SyntaxNode syntax,
            TypeSymbol type,
            BoundExpression value,
            TypeSymbol? delegatedElementType = null,
            Conversion delegatedConversion = default)
            : base(syntax, type)
        {
            Value = value;
            DelegatedElementType = delegatedElementType;
            DelegatedConversion = delegatedConversion;
        }

        /// <summary>The element handed out, or the sequence delegated to when <see cref="IsDelegating"/>.</summary>
        public BoundExpression Value { get; }

        /// <summary>
        /// What one step of <see cref="Value"/> yields, when this is a <c>yield from</c>; otherwise
        /// <see langword="null"/>.
        /// </summary>
        /// <remarks>
        /// Kept beside the node rather than recomputed at emit, because working it out means asking
        /// what counts as iterable - the same question <c>for-in</c> asks, and one the emitter has
        /// no business answering twice. The delegation is <em>not</em> lowered here, for the same
        /// reason <c>for-in</c> is not: whether it becomes a link or a loop depends on the operand's
        /// type, and that is a code generation decision.
        /// </remarks>
        public TypeSymbol? DelegatedElementType { get; }

        /// <summary>
        /// How one delegated element reaches the declaring generator's own element type.
        /// </summary>
        /// <remarks>
        /// A conversion the binder classifies rather than the emitter, like every other conversion
        /// in the tree - there is simply nowhere to hang a node, since what converts is each element
        /// of a sequence rather than the expression written. It is also what decides the lowering:
        /// only an identity conversion can become a delegation link, because a link hands the inner
        /// generator's own values straight to the consumer with nothing in between.
        /// </remarks>
        public Conversion DelegatedConversion { get; }

        /// <summary>True for the <c>yield from</c> form.</summary>
        public bool IsDelegating => DelegatedElementType is not null;
    }

    /// <summary>
    /// A statement run for its effects followed by an expression whose value this node yields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What an expression cannot express on its own is sequencing with a captured value. A range
    /// check (§P4) is the shape that needs it: the assignment's value has to be evaluated once into
    /// a temporary, guarded against the field's declared bounds, and only then written - and the
    /// write is itself an expression whose value something above may read. The binder lowers that
    /// whole sequence into one of these nodes, and every walker from flow analysis to the emitter
    /// treats it as "run the statement, then evaluate the value".
    /// </para>
    /// <para>
    /// The statement is a <see cref="BoundBlockStatement"/> in practice - the temporary's
    /// declaration and the guard's <c>if</c> - and <see cref="Value"/> is the temporary read, so
    /// the node never changes what the statement's constructs mean; it only gives a statement a
    /// place to sit where an expression is expected.
    /// </para>
    /// </remarks>
    public sealed class BoundSequenceExpression : BoundExpression
    {
        internal BoundSequenceExpression(SyntaxNode syntax, BoundStatement statement, BoundExpression value, TypeSymbol type)
            : base(syntax, type)
        {
            Statement = statement;
            Value = value;
        }

        /// <summary>The statement run before the value is produced.</summary>
        public BoundStatement Statement { get; }

        /// <summary>The value this node yields after the statement has run.</summary>
        public BoundExpression Value { get; }
    }
}
