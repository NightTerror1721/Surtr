#nullable enable

using Surtr.Runtime.Objects;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// A method whose body is compiled Surtr bytecode living in its module's chunk.
    /// </summary>
    /// <remarks>
    /// The body is identified by an index into the chunk rather than held here, so this metadata
    /// stays small and the whole module's instruction stream stays contiguous. Only the loader
    /// builds these - hosts contribute methods through <see cref="SurtrNativeMethodInfo"/>.
    /// </remarks>
    public sealed class SurtrBytecodeMethodInfo : SurtrMethodInfo
    {
        private readonly SurtrChunk _chunk;
        private readonly int _entryIndex;
        private readonly int _codeOffset;
        private readonly int _localCount;
        private readonly int _maxStackSize;
        private readonly int _argumentSlotCount;
        private readonly int _resultSlotCount;
        private SurtrExceptionHandler[] _handlers = System.Array.Empty<SurtrExceptionHandler>();

        internal SurtrBytecodeMethodInfo(
            string name,
            SurtrMethodDispatch dispatch,
            SurtrMethodRole role,
            bool isOverride,
            SurtrTypeHandle returnType,
            SurtrParameterInfo[] parameters,
            bool isStatic,
            SurtrVisibility visibility,
            SurtrTypeHandle? declaringType,
            SurtrChunk chunk,
            int entryIndex,
            int localCount,
            int maxStackSize,
            bool isSealed = false,
            string[]? genericParameters = null,
            string[][]? genericConstraints = null,
            bool isExtension = false,
            bool isBridge = false,
            int argumentSlotCount = -1,
            int resultSlotCount = -1)
            : base(name, SurtrMethodImplKind.Bytecode, dispatch, role, isOverride, returnType, parameters, isStatic, visibility, declaringType, isSealed, genericParameters, genericConstraints, isExtension, isBridge)
        {
            _chunk = chunk;
            _entryIndex = entryIndex;
            _localCount = localCount;
            _maxStackSize = maxStackSize;

            // The sentinel travels through untouched when nothing was baked: an image carries no
            // slot count, so ArgumentSlotCount has to fall through to the declared shape rather
            // than read as a bare parameter count - which would drop an instance method's
            // receiver and shift every cross-module call site against it by one.
            _argumentSlotCount = argumentSlotCount;

            // B6 (docs/Plan-Revision-Stdlib.md §2.6): the mirror of _argumentSlotCount, for exactly
            // the same reason. The base ResultSlotCount is dynamic - it asks returnType.ResolvedType
            // for a value class's linked FlattenedSlotWidth - which only exists once the declaring
            // module has been through SurtrTypeLinker at load time. A cross-module call emitted
            // *within the same compilation* (two modules built by one ModuleEmitter, neither loaded
            // into any runtime yet) reads this metadata before that ever happens, so the dynamic
            // path silently answered 1 for a multi-field value-class return - correct for every
            // other shape, wrong for exactly this one - and the caller's own stack tracking (sized
            // off the compiler's own, always-correct symbol-level width) underflowed against it.
            // SurtrMethodBuilder already carries the right width once ApplyValueLayout calls
            // SetResultSlots; baking it here is what makes it survive past Build().
            _resultSlotCount = resultSlotCount;

            // Snapshot the offset instead of indexing the chunk on every call. The table is
            // fixed once the loader has built it, so this can never drift.
            _codeOffset = chunk.MethodOffsets[entryIndex];
        }

        /// <summary>The chunk holding this method's instruction stream and access tables.</summary>
        internal SurtrChunk Chunk
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _chunk;
        }

        /// <summary>This method's slot in the chunk's method-offset table.</summary>
        public int EntryIndex
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _entryIndex;
        }

        /// <summary>How many local slots a frame for this method needs.</summary>
        public int LocalCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _localCount;
        }

        /// <summary>The deepest the operand stack gets while this method runs.</summary>
        public int MaxStackSize
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _maxStackSize;
        }

        /// <summary>
        /// How many stack slots a call site leaves for this method's arguments, receiver
        /// included - value-type parameters occupy their flattened width, so this is the sum of
        /// those widths rather than a plain parameter count. Metadata read back from an image
        /// carries no baked count and falls through to the declared shape.
        /// </summary>
        public override int ArgumentSlotCount
            => _argumentSlotCount >= 0 ? _argumentSlotCount : base.ArgumentSlotCount;

        /// <summary>
        /// How many stack slots one call to this method leaves behind - zero for void, the
        /// flattened width of an inline return, one for everything else. Baked at <c>Build()</c>
        /// time from the same width <see cref="ArgumentSlotCount"/> already bakes (B6,
        /// docs/Plan-Revision-Stdlib.md §2.6): the base, dynamic answer needs the declared return
        /// type's handle resolved, which a cross-module call emitted within the same compilation -
        /// before either module has been loaded into a runtime - cannot assume yet. Metadata read
        /// back from an image carries no baked count and falls through to the dynamic answer, the
        /// same as <see cref="ArgumentSlotCount"/>.
        /// </summary>
        public override int ResultSlotCount
            => _resultSlotCount >= 0 ? _resultSlotCount : base.ResultSlotCount;

        /// <summary>The byte offset into the chunk's code where this method's body starts.</summary>
        internal int CodeOffset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _codeOffset;
        }

        /// <summary>
        /// This method's protected regions, in search order: innermost first, and a type-specific
        /// handler ahead of a catch-all covering the same range. Empty for a method with no
        /// <c>try</c>, which is the case the interpreter checks first.
        /// </summary>
        internal SurtrExceptionHandler[] Handlers
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _handlers;
        }

        /// <summary>Attaches this method's protected regions. Only the emitter calls this, before the module is linked.</summary>
        /// <exception cref="System.InvalidOperationException">The method is already built.</exception>
        public void SetExceptionHandlers(SurtrExceptionHandler[] handlers)
        {
            ThrowIfBuilt();
            _handlers = handlers;
        }

        // Type references are handles owned by the module's table and the chunk is owned by the
        // module, so a bytecode method holds no entity references of its own to trace.
        internal override void VisitReferences(SurtrEntityMarker marker) { }
    }
}
