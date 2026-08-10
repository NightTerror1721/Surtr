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
        /// The host globals this module declares as <c>native</c> variables, by name and in
        /// module-local index order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A <c>native</c> declaration is a name and a signature the compiler checks call sites
        /// against; the body and the storage belong to the host. This table is what makes the
        /// binding happen <em>by name at load</em>: the alternative, which is what this used to be,
        /// was for the instruction to carry a direct index into the runtime's global table, which
        /// silently ties a compiled module to one host's registration order and gives
        /// <see cref="SurtrRuntime.LoadModule"/> nothing to fail on when a name was never
        /// registered at all.
        /// </para>
        /// <para>
        /// Kept as text rather than resolved in place because a module outlives any one runtime,
        /// and two runtimes may well number their globals differently.
        /// </para>
        /// </remarks>
        internal string[] NativeVariableImports;

        /// <summary>The host functions this module declares as <c>native</c>, by name and in module-local index order.</summary>
        internal string[] NativeFunctionImports;

        /// <summary>
        /// Where each of <see cref="NativeVariableImports"/> lives in the runtime's global storage,
        /// filled in at load.
        /// </summary>
        /// <remarks>
        /// Unmanaged, so <c>Ldg</c> reaches it without touching a managed array bound: the extra
        /// load this costs over the old direct index is the whole price of binding by name.
        /// </remarks>
        internal SurtrNativeArray<int> NativeVariableSlots;

        /// <summary>The resolved counterpart of <see cref="NativeFunctionImports"/>, filled in at load.</summary>
        internal SurtrNativeGlobalFunction[] NativeFunctionTable;

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
            NativeVariableImports = Array.Empty<string>();
            NativeFunctionImports = Array.Empty<string>();
            NativeFunctionTable = Array.Empty<SurtrNativeGlobalFunction>();
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
            NativeVariableSlots.Dispose();

            StringConstants = Array.Empty<string>();
            TypeTable = Array.Empty<SurtrTypeHandle>();
            FieldTable = Array.Empty<SurtrFieldInfo>();
            MethodTable = Array.Empty<SurtrMethodInfo>();
            ModuleTable = Array.Empty<SurtrModule>();
            NativeVariableImports = Array.Empty<string>();
            NativeFunctionImports = Array.Empty<string>();
            NativeFunctionTable = Array.Empty<SurtrNativeGlobalFunction>();

            _disposed = true;
        }
    }
}
