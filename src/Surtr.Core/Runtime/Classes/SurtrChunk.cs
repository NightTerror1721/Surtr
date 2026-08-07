#nullable enable

using Surtr.Runtime.Utilities;
using System;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// Everything a module's compiled code needs at run time: the bytecode itself, its constant
    /// pools, and the access tables the opcodes index into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One chunk per module. A bytecode method is then just an offset into <see cref="Code"/>,
    /// which keeps method metadata tiny and keeps every instruction stream in one contiguous
    /// block instead of scattered per-method allocations.
    /// </para>
    /// <para>
    /// The access tables exist so the bytecode can carry small integer operands instead of
    /// names: an opcode says "field 7", the interpreter reads <see cref="FieldTable"/><c>[7]</c>.
    /// Resolution cost is paid once at load time rather than on every execution.
    /// </para>
    /// <para>
    /// Fields are exposed directly rather than through properties so the interpreter can take
    /// their addresses and index them without going through a call. The unmanaged pools live in
    /// <see cref="SurtrNativeArray{T}"/>; the tables holding managed metadata have to stay
    /// ordinary arrays, since their elements are not unmanaged types.
    /// </para>
    /// </remarks>
    internal sealed class SurtrChunk : IDisposable
    {
        /// <summary>The instruction stream every bytecode method in the module points into.</summary>
        internal SurtrNativeArray<byte> Code;

        /// <summary>Inline constant pool, indexed by the operand of constant-loading opcodes.</summary>
        internal SurtrNativeArray<SurtrRawValue> Constants;

        /// <summary>Start offset into <see cref="Code"/> for each bytecode method, indexed by entry index.</summary>
        internal SurtrNativeArray<int> MethodOffsets;

        /// <summary>String constants, which can't live in an unmanaged pool.</summary>
        internal string[] StringConstants;

        /// <summary>Type access table: the handles the bytecode refers to by index.</summary>
        internal SurtrTypeHandle[] TypeTable;

        /// <summary>Field access table: the fields the bytecode refers to by index.</summary>
        internal SurtrFieldInfo[] FieldTable;

        /// <summary>Method access table: the call targets the bytecode refers to by index.</summary>
        internal SurtrMethodInfo[] MethodTable;

        private bool _disposed;
        private SurtrBuildState _buildState;

        /// <summary>Creates an empty chunk with no code and no tables.</summary>
        internal SurtrChunk()
        {
            StringConstants = Array.Empty<string>();
            TypeTable = Array.Empty<SurtrTypeHandle>();
            FieldTable = Array.Empty<SurtrFieldInfo>();
            MethodTable = Array.Empty<SurtrMethodInfo>();
        }

        /// <summary>Whether the chunk's unmanaged buffers have been released.</summary>
        internal bool IsDisposed => _disposed;

        /// <summary>How far this chunk is between being emitted and being ready to execute.</summary>
        internal SurtrBuildState BuildState => _buildState;

        /// <summary>Whether the chunk is frozen and its tables are safe to index.</summary>
        internal bool IsBuilt => _buildState == SurtrBuildState.Built;

        /// <summary>Freezes the chunk once the emitter has finished filling its tables.</summary>
        internal void MarkBuilt() => _buildState = SurtrBuildState.Built;

        /// <summary>Guards a mutation of the chunk's code or tables.</summary>
        /// <exception cref="InvalidOperationException">The chunk is already built.</exception>
        internal void ThrowIfBuilt()
        {
            if (_buildState == SurtrBuildState.Built)
                throw new InvalidOperationException("Chunk is already built and can no longer be modified.");
        }

        /// <summary>Releases every unmanaged buffer the chunk owns and drops its managed tables.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            Code.Dispose();
            Constants.Dispose();
            MethodOffsets.Dispose();

            StringConstants = Array.Empty<string>();
            TypeTable = Array.Empty<SurtrTypeHandle>();
            FieldTable = Array.Empty<SurtrFieldInfo>();
            MethodTable = Array.Empty<SurtrMethodInfo>();

            _disposed = true;
        }
    }
}
