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

        /// <summary>
        /// Where each entry of <see cref="StringConstants"/> lands in <see cref="Constants"/>:
        /// <c>StringConstantSlots[i]</c> is the constant-pool index that holds the reference to the
        /// string object built from <c>StringConstants[i]</c>.
        /// </summary>
        /// <remarks>
        /// A string literal cannot be emitted as a constant the way an integer can: what bytecode
        /// needs is a <see cref="SurtrRef"/>, and a reference only exists once an object has been
        /// registered with a runtime's heap - which happens when the module is loaded, long after
        /// the chunk was built. So the emitter reserves a pool slot per literal and records it here,
        /// and loading interns the text and patches the reference in. <c>Ldc</c> stays one indexed
        /// load and needs no idea that strings are different.
        /// <para>
        /// It also ties a chunk to one runtime: the references patched in belong to that runtime's
        /// heap, so a <see cref="SurtrModule"/> cannot be loaded into two runtimes at once.
        /// </para>
        /// </remarks>
        internal SurtrNativeArray<int> StringConstantSlots;

        /// <summary>Type access table: the handles the bytecode refers to by index.</summary>
        internal SurtrTypeHandle[] TypeTable;

        /// <summary>Field access table: the fields the bytecode refers to by index.</summary>
        internal SurtrFieldInfo[] FieldTable;

        /// <summary>Method access table: the call targets the bytecode refers to by index.</summary>
        internal SurtrMethodInfo[] MethodTable;

        /// <summary>
        /// Module access table: the other modules this one calls into, indexed by the
        /// <c>moduleIdx</c> immediate of <c>CallModule</c>.
        /// </summary>
        /// <remarks>
        /// A cross-module call names its target in two steps - which module, then which of that
        /// module's entries - because the callee's <see cref="SurtrMethodInfo"/> lives in the
        /// callee's own <see cref="MethodTable"/>, not in this one. Resolving the module half here
        /// keeps the caller's method table free of entries it cannot type-check locally, and it is
        /// what makes <c>CallModule</c> two indexed loads rather than a lookup by path.
        /// </remarks>
        internal SurtrModule[] ModuleTable;

        /// <summary>
        /// The paths behind <see cref="ModuleTable"/> when this chunk came from an image, in the
        /// same order; empty for a chunk the emitter produced.
        /// </summary>
        /// <remarks>
        /// A serialized module cannot name another module by instance - the instance it should
        /// name is whichever one the <em>loading</em> runtime has under that path, and there may be
        /// several. So an image writes paths and the load resolves them.
        /// </remarks>
        internal string[] PendingModulePaths;

        /// <summary>What <see cref="FieldTable"/> should point at, when this chunk came from an image.</summary>
        internal SurtrPendingMember[] PendingFields;

        /// <summary>What <see cref="MethodTable"/> should point at, when this chunk came from an image.</summary>
        internal SurtrPendingMember[] PendingMethods;

        /// <summary>Whether this chunk still has by-name references waiting to be bound at load.</summary>
        internal bool HasPendingReferences =>
            PendingModulePaths.Length != 0 || PendingFields.Length != 0 || PendingMethods.Length != 0;

        private bool _disposed;
        private SurtrBuildState _buildState;

        /// <summary>Creates an empty chunk with no code and no tables.</summary>
        internal SurtrChunk()
        {
            StringConstants = Array.Empty<string>();
            TypeTable = Array.Empty<SurtrTypeHandle>();
            FieldTable = Array.Empty<SurtrFieldInfo>();
            MethodTable = Array.Empty<SurtrMethodInfo>();
            ModuleTable = Array.Empty<SurtrModule>();
            PendingModulePaths = Array.Empty<string>();
            PendingFields = Array.Empty<SurtrPendingMember>();
            PendingMethods = Array.Empty<SurtrPendingMember>();
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
            StringConstantSlots.Dispose();

            StringConstants = Array.Empty<string>();
            TypeTable = Array.Empty<SurtrTypeHandle>();
            FieldTable = Array.Empty<SurtrFieldInfo>();
            MethodTable = Array.Empty<SurtrMethodInfo>();
            ModuleTable = Array.Empty<SurtrModule>();
            PendingModulePaths = Array.Empty<string>();
            PendingFields = Array.Empty<SurtrPendingMember>();
            PendingMethods = Array.Empty<SurtrPendingMember>();

            _disposed = true;
        }
    }
}
