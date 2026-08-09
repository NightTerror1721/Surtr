#nullable enable

using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;

namespace Surtr.Bytecode.Emit
{
    /// <summary>A protected code range, opened by <see cref="SurtrMethodBuilder.BeginTry"/>.</summary>
    /// <remarks>
    /// Its bounds are labels rather than byte offsets because branch relaxation moves code around
    /// after the region has been closed: a position recorded while emitting would be stale by the
    /// time the handler table is written.
    /// </remarks>
    public readonly struct SurtrProtectedRegion
    {
        internal readonly SurtrLabel Start;
        internal readonly SurtrLabel End;

        internal SurtrProtectedRegion(SurtrLabel start, SurtrLabel end)
        {
            Start = start;
            End = end;
        }
    }

    /// <summary>
    /// Declares one bytecode method: its signature, its frame, its protected regions, and the
    /// instruction stream behind them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A method builder exists before its <see cref="SurtrBytecodeMethodInfo"/> does, and that is
    /// not an accident. The metadata snapshots the method's offset into its module's shared
    /// instruction stream when it is constructed, so it cannot exist until every body in the module
    /// has been emitted and laid out - yet a call site needs *something* to name its target while
    /// emitting. The builder is that something: it takes a method table slot up front
    /// (<see cref="Token"/>) and the real metadata is bound into it by
    /// <see cref="SurtrModuleBuilder.Build"/>.
    /// </para>
    /// <para>
    /// Frame slots are numbered here rather than counted by the caller. Arguments arrive as the
    /// first locals - the receiver at slot 0 on an instance method, then the declared parameters -
    /// and <see cref="DeclareLocal"/> hands out everything above them.
    /// </para>
    /// </remarks>
    public sealed class SurtrMethodBuilder
    {
        private readonly struct PendingHandler
        {
            public readonly SurtrProtectedRegion Region;
            public readonly SurtrLabel Handler;
            public readonly SurtrTypeHandle? CatchType;
            public readonly int Order;

            public PendingHandler(SurtrProtectedRegion region, SurtrLabel handler, SurtrTypeHandle? catchType, int order)
            {
                Region = region;
                Handler = handler;
                CatchType = catchType;
                Order = order;
            }
        }

        private readonly SurtrModuleBuilder _module;
        private readonly SurtrClassBuilder? _declaringClass;
        private readonly string _name;
        private readonly SurtrClassReference _returnType;
        private readonly SurtrParameterInfo[] _parameters;
        private readonly bool _static;
        private readonly SurtrVisibility _visibility;
        private readonly SurtrMethodDispatch _dispatch;
        private readonly SurtrMethodRole _role;
        private readonly bool _override;
        private readonly bool _sealed;
        private readonly int _argumentSlots;
        private readonly List<string?> _localNames = new List<string?>();
        private readonly List<PendingHandler> _handlers = new List<PendingHandler>();

        private SurtrCodeEmitter _code;
        private SurtrMethodToken _token;
        private SurtrBytecodeMethodInfo? _built;
        private byte[]? _body;

        internal SurtrMethodBuilder(
            SurtrModuleBuilder module,
            SurtrClassBuilder? declaringClass,
            string name,
            SurtrClassReference returnType,
            SurtrParameterInfo[] parameters,
            bool isStatic,
            SurtrVisibility visibility,
            SurtrMethodDispatch dispatch,
            SurtrMethodRole role,
            bool isOverride,
            bool isSealed = false)
        {
            _module = module;
            _declaringClass = declaringClass;
            _name = name;
            _returnType = returnType;
            _parameters = parameters;
            _static = isStatic;
            _visibility = visibility;
            _dispatch = dispatch;
            _role = role;
            _override = isOverride;
            _sealed = isSealed;

            // A module-level function has no receiver even though nothing declares it static in
            // the language: a module is not an object, so there is nothing to be a receiver of.
            _argumentSlots = parameters.Length + (declaringClass is not null && !isStatic ? 1 : 0);

            for (int i = 0; i < _argumentSlots; i++)
                _localNames.Add(i == 0 && HasReceiver ? "this" : parameters[i - (HasReceiver ? 1 : 0)].Name);

            _code = new SurtrCodeEmitter(this);
        }

        /// <summary>The module this method is being emitted into.</summary>
        public SurtrModuleBuilder Module => _module;

        /// <summary>The class this method belongs to, or <see langword="null"/> for a module-level function.</summary>
        public SurtrClassBuilder? DeclaringClass => _declaringClass;

        /// <summary>The method's declared name.</summary>
        public string Name => _name;

        /// <summary>The instruction stream to emit the body into.</summary>
        public SurtrCodeEmitter Code => _code;

        /// <summary>Whether this is a module-level function rather than a class member.</summary>
        public bool IsModuleLevel => _declaringClass is null;

        /// <summary>Whether the method belongs to its type rather than to instances of it.</summary>
        public bool IsStatic => _static;

        /// <summary>How calls to this method resolve.</summary>
        public SurtrMethodDispatch Dispatch => _dispatch;

        /// <summary>What part this method plays in its declaring type.</summary>
        public SurtrMethodRole Role => _role;

        /// <summary>The descriptor naming the method's return type.</summary>
        public SurtrClassReference ReturnTypeReference => _returnType;

        /// <summary>How many parameters the method declares, the receiver excluded.</summary>
        public int ParameterCount => _parameters.Length;

        /// <summary>Whether slot 0 of the frame is a receiver.</summary>
        public bool HasReceiver => _declaringClass is not null && !_static;

        /// <summary>
        /// How many stack slots a call site must leave for this method - the declared parameters
        /// plus the receiver, when there is one.
        /// </summary>
        /// <remarks>This is the <c>argsCount</c> immediate every call opcode carries.</remarks>
        public int ArgumentSlotCount => _argumentSlots;

        /// <summary>How many slots this method's frame needs, arguments included.</summary>
        public int LocalCount => _localNames.Count;

        /// <summary>This method's slot in the module's method access table.</summary>
        /// <remarks>
        /// Handed out when the method is declared, long before the metadata behind it exists, so
        /// that a body emitted now can call a method whose own body has not been written yet.
        /// </remarks>
        public SurtrMethodToken Token => _token;

        /// <summary>
        /// The metadata this builder produced, or <see langword="null"/> until
        /// <see cref="SurtrModuleBuilder.Build"/> has run.
        /// </summary>
        public SurtrBytecodeMethodInfo? Built => _built;

        internal SurtrParameterInfo[] Parameters => _parameters;

        internal SurtrVisibility Visibility => _visibility;

        internal bool IsOverride => _override;

        internal void AssignToken(SurtrMethodToken token) => _token = token;

        #region Frame

        /// <summary>The receiver slot, which is local 0 on an instance method.</summary>
        /// <exception cref="InvalidOperationException">The method has no receiver.</exception>
        public SurtrLocal Receiver
        {
            get
            {
                if (!HasReceiver)
                    throw new InvalidOperationException($"'{_name}' is static or module-level and has no receiver.");

                return new SurtrLocal(0);
            }
        }

        /// <summary>The slot holding the parameter declared at <paramref name="index"/>.</summary>
        public SurtrLocal Parameter(int index)
        {
            if ((uint)index >= (uint)_parameters.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"'{_name}' declares {_parameters.Length} parameters.");

            return new SurtrLocal(index + (HasReceiver ? 1 : 0));
        }

        /// <summary>Claims another frame slot, above the arguments and any local claimed before it.</summary>
        /// <param name="name">An optional name, for diagnostics only.</param>
        public SurtrLocal DeclareLocal(string? name = null)
        {
            _localNames.Add(name);
            return new SurtrLocal(_localNames.Count - 1);
        }

        /// <summary>The name a slot was declared with, or <see langword="null"/> if it has none.</summary>
        public string? NameOfLocal(SurtrLocal local) => _localNames[local.Index];

        #endregion

        #region Protected regions

        /// <summary>Opens a protected region at the current position.</summary>
        public SurtrProtectedRegion BeginTry()
        {
            var start = _code.NewLabel();
            var end = _code.NewLabel();
            _code.MarkPosition(start);
            return new SurtrProtectedRegion(start, end);
        }

        /// <summary>Closes a protected region at the current position.</summary>
        public SurtrMethodBuilder EndTry(SurtrProtectedRegion region)
        {
            _code.MarkPosition(region.End);
            return this;
        }

        /// <summary>
        /// Routes anything of type <paramref name="catchType"/> raised inside
        /// <paramref name="region"/> to <paramref name="handler"/>.
        /// </summary>
        /// <remarks>
        /// The handler's own label is marked with <see cref="SurtrCodeEmitter.MarkHandler"/>, which
        /// is what tells the stack tracker it starts with the raised object and nothing else.
        /// </remarks>
        public SurtrMethodBuilder AddCatch(SurtrProtectedRegion region, SurtrClassReference catchType, SurtrLabel handler)
        {
            _handlers.Add(new PendingHandler(region, handler, _module.TypeHandle(catchType), _handlers.Count));
            return this;
        }

        /// <summary>
        /// Routes anything raised inside <paramref name="region"/> to <paramref name="handler"/>,
        /// whatever its type.
        /// </summary>
        /// <remarks>
        /// Also how a <c>finally</c> is expressed: run the block, then re-raise with <c>Throw</c>.
        /// The instruction set has no <c>finally</c> of its own - the block is emitted on each
        /// normal exit path, exactly as javac does it.
        /// </remarks>
        public SurtrMethodBuilder AddCatchAll(SurtrProtectedRegion region, SurtrLabel handler)
        {
            _handlers.Add(new PendingHandler(region, handler, null, _handlers.Count));
            return this;
        }

        #endregion

        #region Build

        /// <summary>Resolves the body's labels and hands back its bytes, still method-relative.</summary>
        internal byte[] FinishCode() => _body ??= _code.Finish();

        /// <summary>The deepest the operand stack gets in the finished body.</summary>
        internal int MaxStackSize => _code.MaxStackDepth;

        /// <summary>
        /// Builds the metadata, once the module's chunk knows where this body starts.
        /// </summary>
        internal SurtrBytecodeMethodInfo Build(SurtrChunk chunk, int entryIndex, SurtrTypeHandle? declaringType)
        {
            _built = new SurtrBytecodeMethodInfo(
                _name,
                _dispatch,
                _role,
                _override,
                _module.TypeHandle(_returnType),
                _parameters,
                _static,
                _visibility,
                declaringType,
                chunk,
                entryIndex,
                LocalCount,
                _code.MaxStackDepth,
                _sealed);

            return _built;
        }

        /// <summary>
        /// Translates the pending regions into the handler table, with offsets moved into the
        /// chunk's own address space.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The table is searched top to bottom, so it has to be ordered innermost first, with a
        /// type-specific handler ahead of a catch-all over the same range. Nesting means
        /// containment, so a narrower range is always the inner one - sorting by width gets the
        /// first half right, and the declaration order breaks the tie for the second.
        /// </para>
        /// <para>
        /// <see cref="SurtrExceptionHandler"/> offsets are absolute within the module's chunk, not
        /// relative to the method, because every method is already an offset into one shared
        /// instruction stream. Hence <paramref name="codeOffset"/>.
        /// </para>
        /// </remarks>
        internal SurtrExceptionHandler[] BuildHandlers(int codeOffset)
        {
            if (_handlers.Count == 0)
                return Array.Empty<SurtrExceptionHandler>();

            var ordered = _handlers.ToArray();
            Array.Sort(ordered, (left, right) =>
            {
                int leftWidth = _code.PositionOf(left.Region.End) - _code.PositionOf(left.Region.Start);
                int rightWidth = _code.PositionOf(right.Region.End) - _code.PositionOf(right.Region.Start);

                int byWidth = leftWidth.CompareTo(rightWidth);
                return byWidth != 0 ? byWidth : left.Order.CompareTo(right.Order);
            });

            var handlers = new SurtrExceptionHandler[ordered.Length];
            for (int i = 0; i < ordered.Length; i++)
            {
                handlers[i] = new SurtrExceptionHandler(
                    codeOffset + _code.PositionOf(ordered[i].Region.Start),
                    codeOffset + _code.PositionOf(ordered[i].Region.End),
                    codeOffset + _code.PositionOf(ordered[i].Handler),
                    ordered[i].CatchType);
            }

            return handlers;
        }

        #endregion
    }
}
