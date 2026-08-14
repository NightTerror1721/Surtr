#nullable enable

using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Rejects a static initializer that reads a static which has not been given its value yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs/VM-Plan.md</c> §1.12 runs every static initializer eagerly at load, in declaration
    /// order, each class's before the module's own. That is what keeps a static read from paying a
    /// "has this run yet" test forever to answer a question that is false exactly once — and the
    /// price of it is an ordering the runtime cannot police: an initializer reading a static
    /// declared after it reads a zero, silently. §3.4 makes rejecting that the compiler's job, and
    /// this is it.
    /// </para>
    /// <para>
    /// Position is a pair — which container, and where inside it — because both orderings are real.
    /// Between containers it is declaration order with the module last, since a module-level
    /// variable is a static of its module and its initializer runs after every class's. Inside one
    /// it is the same counter that already interleaves a <c>static { }</c> block with the field
    /// initializers around it, which is what makes "the field below me" and "the field above me"
    /// different answers.
    /// </para>
    /// <para>
    /// Only statics of the <em>same</em> module are checked, and that is not an approximation: a
    /// module reaches another only by depending on it, dependencies load first, and a cycle is
    /// already a hard error — so anything a fragment can name in another module has finished
    /// initializing before this one starts.
    /// </para>
    /// <para>
    /// The walk follows calls, so a fragment that reaches a static through a function is caught the
    /// same as one that reads it outright. It stops at a lambda: §8 makes one a value, and its body
    /// runs when something invokes it rather than when the initializer builds it.
    /// </para>
    /// </remarks>
    public sealed class InitializerOrder
    {
        /// <summary>Where something sits in the order the runtime initializes a module in.</summary>
        private readonly struct Position
        {
            public Position(int container, int within)
            {
                Container = container;
                Within = within;
            }

            /// <summary>The container's index, with the module itself last.</summary>
            public int Container { get; }

            /// <summary>Where inside that container, among its initializers and static blocks.</summary>
            public int Within { get; }

            public bool IsBefore(Position other)
                => Container != other.Container ? Container < other.Container : Within < other.Within;
        }

        /// <summary>One piece of code that runs at load, and where in the order it runs.</summary>
        public readonly struct Fragment
        {
            /// <summary>Creates a fragment.</summary>
            public Fragment(
                ModuleSymbol module,
                NamedTypeSymbol? containingType,
                FieldSymbol? field,
                int order,
                BoundNode body,
                string sourceName)
            {
                Module = module;
                ContainingType = containingType;
                Field = field;
                Order = order;
                Body = body;
                SourceName = sourceName;
            }

            /// <summary>The module whose load runs it.</summary>
            public ModuleSymbol Module { get; }

            /// <summary>The type declaring it, or <see langword="null"/> for a module-level one.</summary>
            public NamedTypeSymbol? ContainingType { get; }

            /// <summary>The static it gives a value to, or <see langword="null"/> for a block.</summary>
            public FieldSymbol? Field { get; }

            /// <summary>Where it sat among its container's initializers.</summary>
            public int Order { get; }

            /// <summary>What runs — an initializer's value, or a block's body.</summary>
            public BoundNode Body { get; }

            /// <summary>The file it was written in.</summary>
            public string SourceName { get; }
        }

        private readonly IReadOnlyDictionary<MethodSymbol, BoundStatement> _bodies;
        private readonly SurtrDiagnosticBag _diagnostics;

        private readonly Dictionary<NamedTypeSymbol, int> _containers = new Dictionary<NamedTypeSymbol, int>();
        private readonly Dictionary<FieldSymbol, Position> _ready = new Dictionary<FieldSymbol, Position>();
        private readonly HashSet<MethodSymbol> _walked = new HashSet<MethodSymbol>();

        private ModuleSymbol _module = null!;
        private Position _at;
        private string _sourceName = string.Empty;
        private bool _reported;

        private InitializerOrder(
            IReadOnlyDictionary<MethodSymbol, BoundStatement> bodies,
            SurtrDiagnosticBag diagnostics)
        {
            _bodies = bodies;
            _diagnostics = diagnostics;
        }

        /// <summary>Checks every module's load-time fragments, reporting whatever reads too early.</summary>
        public static void Check(
            IEnumerable<ModuleSymbol> modules,
            IReadOnlyList<Fragment> fragments,
            IReadOnlyDictionary<MethodSymbol, BoundStatement> bodies,
            SurtrDiagnosticBag diagnostics)
        {
            foreach (var module in modules)
                new InitializerOrder(bodies, diagnostics).CheckModule(module, fragments);
        }

        private void CheckModule(ModuleSymbol module, IReadOnlyList<Fragment> fragments)
        {
            _module = module;

            int next = 0;
            foreach (var type in module.Types)
                Number(type, ref next);

            // The module's own initializer runs after every class's, so it sits one past the last.
            int moduleContainer = next;

            foreach (var fragment in fragments)
            {
                if (!ReferenceEquals(fragment.Module, module) || fragment.Field is not FieldSymbol field)
                    continue;

                if (field.IsStatic)
                    _ready[field] = PositionOf(fragment, moduleContainer);
            }

            foreach (var fragment in fragments)
            {
                if (!ReferenceEquals(fragment.Module, module))
                    continue;

                // An instance field's initializer runs from a constructor, not at load.
                if (fragment.Field is FieldSymbol declared && !declared.IsStatic)
                    continue;

                _at = PositionOf(fragment, moduleContainer);
                _sourceName = fragment.SourceName;
                _reported = false;
                _walked.Clear();

                Walk(fragment.Body);
            }
        }

        private void Number(NamedTypeSymbol type, ref int next)
        {
            _containers[type] = next++;

            // Depth-first with the container first, which is the order the runtime walks its classes
            // and their nested ones in.
            foreach (var nested in type.NestedTypes)
                Number(nested, ref next);
        }

        private Position PositionOf(Fragment fragment, int moduleContainer)
        {
            if (fragment.ContainingType is NamedTypeSymbol type && _containers.TryGetValue(type, out int container))
                return new Position(container, fragment.Order);

            return new Position(moduleContainer, fragment.Order);
        }

        private void Read(FieldSymbol field, SourceSpan span)
        {
            if (_reported || !field.IsStatic || field.ImportedFrom is not null)
                return;

            // Another module's statics are already initialized: it loaded first, or nothing here
            // could name it.
            if (!ReferenceEquals(ModuleOf(field), _module))
                return;

            if (!_ready.TryGetValue(field, out var ready) || ready.IsBefore(_at))
                return;

            // One report per fragment: a walk that reaches a static too early usually reaches
            // several, and they are all the same mistake.
            _reported = true;

            _diagnostics.ReportError(
                SurtrDiagnosticCode.InitializerOutOfOrder,
                $"'{Describe(field)}' has not been given its value yet; static initializers run at load in "
                    + "declaration order, so this reads a zero. Move the declaration above this one.",
                _sourceName,
                span);
        }

        private static string Describe(FieldSymbol field)
            => field.ContainingSymbol is NamedTypeSymbol type ? type.Name + "." + field.Name : field.Name;

        private static ModuleSymbol? ModuleOf(FieldSymbol field)
        {
            for (Symbol? walk = field.ContainingSymbol; walk is not null; walk = walk.ContainingSymbol)
            {
                if (walk is ModuleSymbol module)
                    return module;
            }

            return null;
        }

        private void Enter(MethodSymbol? method)
        {
            if (method is null || _reported)
                return;

            // The declaration owns the body; a substituted view of a generic's member is the same
            // code (§8).
            var definition = method.OriginalDefinition ?? method;

            if (!_walked.Add(definition) || !_bodies.TryGetValue(definition, out var body))
                return;

            Walk(body);
        }

        #region Walking
        private void Walk(BoundNode? node)
        {
            if (node is null || _reported)
                return;

            switch (node)
            {
                case BoundBlockStatement block:
                    foreach (var inner in block.Statements)
                        Walk(inner);

                    return;

                case BoundExpressionStatement statement:
                    Walk(statement.Expression);
                    return;

                case BoundLocalDeclarationStatement local:
                    Walk(local.Initializer);
                    return;

                case BoundIfStatement @if:
                    Walk(@if.Condition);
                    Walk(@if.Then);
                    Walk(@if.Else);
                    return;

                case BoundWhileStatement @while:
                    Walk(@while.Condition);
                    Walk(@while.Body);
                    return;

                case BoundForStatement @for:
                    Walk(@for.Initializer);
                    Walk(@for.Condition);
                    Walk(@for.Step);
                    Walk(@for.Body);
                    return;

                case BoundForInStatement forIn:
                    Walk(forIn.Sequence);
                    Walk(forIn.Body);
                    return;

                case BoundSwitchStatement @switch:
                {
                    Walk(@switch.Subject);

                    foreach (var section in @switch.Sections)
                    {
                        foreach (var label in section.Labels)
                            Walk(label);

                        foreach (var inner in section.Statements)
                            Walk(inner);
                    }

                    return;
                }

                case BoundTryStatement @try:
                {
                    Walk(@try.Body);

                    foreach (var clause in @try.Catches)
                        Walk(clause.Body);

                    Walk(@try.Finally);
                    return;
                }

                case BoundThrowStatement @throw:
                    Walk(@throw.Value);
                    return;

                case BoundReturnStatement @return:
                    Walk(@return.Value);
                    return;

                case BoundLabeledStatement labeled:
                    Walk(labeled.Statement);
                    return;

                case BoundFieldExpression field:
                {
                    Read(field.Field, field.Span);
                    Walk(field.Receiver);
                    return;
                }

                case BoundPropertyExpression property:
                {
                    Enter(property.Property.Getter);
                    Walk(property.Receiver);
                    return;
                }

                case BoundCallExpression call:
                {
                    Walk(call.Receiver);

                    foreach (var argument in call.Arguments)
                        Walk(argument);

                    Enter(call.Method);
                    return;
                }

                case BoundObjectCreationExpression creation:
                {
                    foreach (var argument in creation.Arguments)
                        Walk(argument);

                    Enter(creation.Constructor);
                    return;
                }

                case BoundClosureInvocationExpression invocation:
                {
                    Walk(invocation.Callee);

                    foreach (var argument in invocation.Arguments)
                        Walk(argument);

                    return;
                }

                case BoundAssignmentExpression assignment:
                    Walk(assignment.Value);
                    Walk(assignment.Target);
                    return;

                case BoundBinaryExpression binary:
                    Walk(binary.Left);
                    Walk(binary.Right);
                    return;

                case BoundUnaryExpression unary:
                    Walk(unary.Operand);
                    return;

                case BoundConversionExpression conversion:
                    Walk(conversion.Operand);
                    return;

                case BoundConditionalExpression conditional:
                    Walk(conditional.Condition);
                    Walk(conditional.WhenTrue);
                    Walk(conditional.WhenFalse);
                    return;

                case BoundNullConditionalExpression conditional:
                    Walk(conditional.Receiver);
                    Walk(conditional.Access);
                    return;

                case BoundNullAssertExpression assert:
                    Walk(assert.Operand);
                    return;

                case BoundIndexExpression index:
                    Walk(index.Target);
                    Walk(index.Index);
                    return;

                case BoundTypeTestExpression test:
                    Walk(test.Operand);
                    return;

                case BoundArrayLiteralExpression array:
                {
                    foreach (var element in array.Elements)
                        Walk(element);

                    return;
                }

                case BoundTupleLiteralExpression tuple:
                {
                    foreach (var element in tuple.Elements)
                        Walk(element);

                    return;
                }

                case BoundDictLiteralExpression dictionary:
                {
                    foreach (var entry in dictionary.Entries)
                    {
                        Walk(entry.Key);
                        Walk(entry.Value);
                    }

                    return;
                }

                case BoundInterpolatedStringExpression interpolated:
                {
                    foreach (var part in interpolated.Parts)
                        Walk(part);

                    return;
                }

                case BoundSwitchExpression @switch:
                {
                    Walk(@switch.Subject);

                    foreach (var arm in @switch.Arms)
                    {
                        foreach (var value in arm.Values)
                            Walk(value);

                        Walk(arm.Result);
                    }

                    return;
                }

                case BoundLambdaExpression:
                    // §8 makes a lambda a value: what it captures is copied here, but its body runs
                    // when something invokes it, which is not at load.
                    return;
            }
        }
        #endregion
    }
}
