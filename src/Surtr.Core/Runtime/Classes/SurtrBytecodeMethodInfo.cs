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
            bool isSealed = false)
            : base(name, SurtrMethodImplKind.Bytecode, dispatch, role, isOverride, returnType, parameters, isStatic, visibility, declaringType, isSealed)
        {
            _chunk = chunk;
            _entryIndex = entryIndex;
            _localCount = localCount;
            _maxStackSize = maxStackSize;

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
