#nullable enable

using Surtr.Runtime.Objects;
using Surtr.Runtime.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// A global variable defined by the embedding host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the only genuinely global names in Surtr - everything a Surtr program declares
    /// belongs to a module. This object is pure metadata: the compiler needs
    /// <see cref="VariableType"/> to type-check uses and <see cref="IsReadOnly"/> to reject
    /// writes. The value itself lives in the owning table's
    /// <see cref="SurtrNativeGlobalTable.VariableTable"/> at <see cref="Index"/>.
    /// </para>
    /// <para>
    /// The table owns the storage rather than the host handing over an address, because a global
    /// is written from both sides - <c>Stg</c> from bytecode, <see cref="SurtrNativeGlobalTable.SetValue(int, SurtrValue)"/>
    /// from the host - so the value has to be a NaN-boxed <see cref="SurtrValue"/> in a location
    /// the collector can trace. It also removes a whole class of host mistakes: a moving or
    /// short-lived address, or storage the wrong size for a boxed value.
    /// </para>
    /// </remarks>
    public sealed class SurtrNativeGlobalVariable
    {
        private readonly string _name;
        private readonly SurtrTypeHandle _variableType;
        private readonly bool _readOnly;
        private readonly bool _isReference;
        private int _index = -1;

        /// <summary>Declares a host global of the given type.</summary>
        /// <param name="name">The name Surtr code refers to the variable by.</param>
        /// <param name="variableType">The variable's declared type.</param>
        /// <param name="isReadOnly">Whether Surtr code may only read it. The host can always write it.</param>
        public SurtrNativeGlobalVariable(string name, SurtrTypeHandle variableType, bool isReadOnly)
        {
            _name = name ?? throw new ArgumentNullException(nameof(name));
            _variableType = variableType ?? throw new ArgumentNullException(nameof(variableType));
            _readOnly = isReadOnly;

            // Cached at declaration time: this is read once per global per collection, and it is
            // what puts the variable in the table's reference-slot list.
            _isReference = variableType.TypeCode.IsReferenceType;
        }

        /// <summary>The name Surtr code refers to this variable by.</summary>
        public string Name
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _name;
        }

        /// <summary>The variable's declared type.</summary>
        public SurtrTypeHandle VariableType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _variableType;
        }

        /// <summary>
        /// Whether Surtr code may only read this variable. The host is never restricted by it -
        /// a read-only global is exactly how a host publishes something it updates itself, such
        /// as a frame's delta time.
        /// </summary>
        public bool IsReadOnly
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _readOnly;
        }

        /// <summary>
        /// Whether this variable's declared type is a reference type, and so whether a collection
        /// has to trace its slot.
        /// </summary>
        public bool IsReference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _isReference;
        }

        /// <summary>
        /// This variable's frozen slot in <see cref="SurtrNativeGlobalTable.VariableTable"/>, or
        /// <c>-1</c> if it has not been registered yet.
        /// </summary>
        /// <remarks>
        /// Assigned exactly once, by <see cref="SurtrNativeGlobalTable.Register(SurtrNativeGlobalVariable)"/>.
        /// Host globals are never unregistered, so an index handed out to compiled bytecode stays
        /// valid for the lifetime of the process.
        /// </remarks>
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _index;
        }

        /// <summary>Freezes this variable's table index. Called exactly once, by <see cref="SurtrNativeGlobalTable"/>.</summary>
        /// <exception cref="InvalidOperationException">The variable is already registered.</exception>
        internal void AssignIndex(int index)
        {
            if (_index != -1)
                throw new InvalidOperationException($"Native global variable '{_name}' is already registered with index {_index}.");

            _index = index;
        }
    }

    /// <summary>
    /// A global function defined by the embedding host.
    /// </summary>
    /// <remarks>
    /// Carries both halves the two front ends need: a full signature for the compiler to
    /// type-check calls against, and a <see cref="SurtrNativeEntryPoint"/> for the interpreter
    /// to call through.
    /// </remarks>
    public sealed class SurtrNativeGlobalFunction
    {
        private readonly string _name;
        private readonly SurtrTypeHandle _returnType;
        private readonly SurtrParameterInfo[] _parameters;
        private readonly SurtrNativeEntryPoint _entryPoint;
        private SurtrClassReference _signature;
        private int _index = -1;

        /// <summary>Binds a host global function to an already-linked entry point.</summary>
        public SurtrNativeGlobalFunction(
            string name,
            SurtrTypeHandle returnType,
            SurtrParameterInfo[] parameters,
            SurtrNativeEntryPoint entryPoint)
        {
            if (!entryPoint.IsValid)
                throw new ArgumentException($"Native global function '{name}' was given a null entry point.", nameof(entryPoint));

            _name = name;
            _returnType = returnType;
            _parameters = parameters;
            _entryPoint = entryPoint;
        }

        /// <summary>The name Surtr code calls this function by.</summary>
        public string Name
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _name;
        }

        /// <summary>The function's declared return type.</summary>
        public SurtrTypeHandle ReturnType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _returnType;
        }

        /// <summary>The function's declared parameters, in order.</summary>
        public ReadOnlySpan<SurtrParameterInfo> Parameters
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _parameters;
        }

        /// <summary>How many parameters the function declares.</summary>
        public int ParameterCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _parameters.Length;
        }

        /// <summary>The address the interpreter calls through.</summary>
        public SurtrNativeEntryPoint EntryPoint
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _entryPoint;
        }

        /// <summary>
        /// The function's signature as a closure descriptor, for overload and compatibility
        /// checks. Built once on first use and cached, since constructing it allocates.
        /// </summary>
        public SurtrClassReference ToSignature()
        {
            if (_signature.IsValid)
                return _signature;

            return _signature = BuildSignature();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private SurtrClassReference BuildSignature()
        {
            var parameterTypes = new SurtrClassReference[_parameters.Length];
            for (int i = 0; i < _parameters.Length; i++)
                parameterTypes[i] = _parameters[i].ParameterType.Reference;

            return SurtrClassReference.Closure(_returnType.Reference, parameterTypes);
        }

        /// <summary>
        /// This function's frozen slot in <see cref="SurtrNativeGlobalTable.FunctionTable"/>, or
        /// <c>-1</c> if it has not been registered yet.
        /// </summary>
        /// <remarks>
        /// Assigned exactly once, by <see cref="SurtrNativeGlobalTable.Register(SurtrNativeGlobalFunction)"/>.
        /// Host globals are never unregistered, so an index handed out to compiled bytecode stays
        /// valid for the lifetime of the process.
        /// </remarks>
        internal int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _index;
        }

        /// <summary>Freezes this function's table index. Called exactly once, by <see cref="SurtrNativeGlobalTable"/>.</summary>
        /// <exception cref="InvalidOperationException">The function is already registered.</exception>
        internal void AssignIndex(int index)
        {
            if (_index != -1)
                throw new InvalidOperationException($"Native global function '{_name}' is already registered with index {_index}.");

            _index = index;
        }
    }

    /// <summary>
    /// Everything the host has published globally: the shared surface the compiler resolves
    /// unqualified names against and the interpreter dispatches through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not tied to any module, because host globals outlive and cross all of them. Surtr code
    /// can never add to this table - only the host can, before or between loads, and it can do
    /// so at any time: registration is not a one-shot build step the way class/module linking is.
    /// </para>
    /// <para>
    /// <see cref="Variables"/> and <see cref="Functions"/> are the compiler's view, keyed by name.
    /// <see cref="VariableTable"/> and <see cref="FunctionTable"/> are the runtime's view: every
    /// registration grows them and hands the new entry a slot index
    /// (<see cref="SurtrNativeGlobalVariable.Index"/> / <see cref="SurtrNativeGlobalFunction.Index"/>),
    /// frozen for the entry's lifetime since nothing can ever unregister. Compiled bytecode then
    /// carries that index instead of a name, so a global access opcode is a direct table read
    /// instead of a dictionary lookup by string.
    /// </para>
    /// <para>
    /// The table also owns the globals' storage. Both sides read and write them - bytecode through
    /// <c>Ldg</c>/<c>Stg</c> straight off <see cref="VariableTable"/>, the host through
    /// <see cref="GetValue(int)"/> and <see cref="SetValue(int, SurtrValue)"/> - so the value has
    /// to live somewhere both can reach and the collector can trace, which rules out storage the
    /// host merely points at.
    /// </para>
    /// </remarks>
    public sealed unsafe class SurtrNativeGlobalTable : IDisposable
    {
        private readonly Dictionary<string, SurtrNativeGlobalVariable> _variables;
        private readonly Dictionary<string, SurtrNativeGlobalFunction> _functions;
        private readonly List<SurtrNativeGlobalVariable> _variablesByIndex;
        private bool _disposed;

        /// <summary>
        /// Backing storage for every registered global variable, indexed by
        /// <see cref="SurtrNativeGlobalVariable.Index"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Each entry is one NaN-boxed <see cref="SurtrValue"/>, so <c>Ldg</c> and <c>Stg</c> are a
        /// single indexed load or store off this pointer - no dictionary, no walk through the
        /// managed <see cref="SurtrNativeGlobalVariable"/>, and no indirection through an address
        /// the host supplied. Same shape as <see cref="SurtrClass.StaticStorage"/>, which is the
        /// module-level equivalent.
        /// </para>
        /// <para>
        /// The table owns this memory, which is why the host reads and writes through
        /// <see cref="GetValue(int)"/> / <see cref="SetValue(int, SurtrValue)"/> rather than
        /// keeping a pointer: registration reallocates, so any address handed out earlier would
        /// dangle the moment the next global is declared.
        /// </para>
        /// </remarks>
        internal SurtrNativeArray<SurtrRawValue> VariableTable;

        /// <summary>
        /// The indices into <see cref="VariableTable"/> of the globals whose declared type is a
        /// reference type: the only host globals a garbage collection has to trace.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Compacted at registration time for the same reason <see cref="SurtrClass.ReferenceSlots"/>
        /// exists, and it is deliberately the same shape. Surtr is statically typed, so whether a
        /// global can ever hold a <see cref="SurtrRef"/> is settled the moment it is declared; a
        /// collection then walks the k globals that can root something rather than tag-testing all
        /// n, and cannot be fooled by NaN aliasing - a raw <c>float</c> whose bits land in the
        /// reference tag range.
        /// </para>
        /// <para>
        /// Slot indices rather than addresses, because <see cref="VariableTable"/> is reallocated on
        /// every registration; a cached pointer list would have to be rebuilt each time and would be
        /// silently stale if it were not.
        /// </para>
        /// </remarks>
        internal SurtrNativeArray<int> ReferenceVariableSlots;

        /// <summary>
        /// Every registered global function, indexed by <see cref="SurtrNativeGlobalFunction.Index"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Unlike <see cref="VariableTable"/> this cannot be a native array: a function carries
        /// managed metadata (parameters, return type, cached signature) alongside its entry point,
        /// the same reason class and module method tables (see <see cref="SurtrClass.DirectMethods"/>)
        /// are plain arrays rather than unmanaged ones.
        /// </para>
        /// <para>
        /// Deliberately *not* a garbage collection root source. A host global function is a bare
        /// code address plus compile-time metadata - it is never a registered
        /// <c>SurtrRuntimeEntity</c>, so it has no <see cref="SurtrRef"/> to mark, and it captures
        /// nothing that could hold one. A global of *closure type* is a different thing entirely: it
        /// lives in <see cref="VariableTable"/> and is traced through
        /// <see cref="ReferenceVariableSlots"/> like any other reference-typed global.
        /// </para>
        /// </remarks>
        internal SurtrNativeGlobalFunction[] FunctionTable;

        /// <summary>Creates an empty table.</summary>
        public SurtrNativeGlobalTable()
        {
            _variables = new Dictionary<string, SurtrNativeGlobalVariable>(StringComparer.Ordinal);
            _functions = new Dictionary<string, SurtrNativeGlobalFunction>(StringComparer.Ordinal);
            _variablesByIndex = new List<SurtrNativeGlobalVariable>();
            FunctionTable = Array.Empty<SurtrNativeGlobalFunction>();
        }

        // Concrete collections rather than IReadOnlyDictionary: an interface-typed lookup costs a
        // dispatch the JIT can't devirtualize, and foreach over the interface boxes the struct
        // enumerator. Name resolution against this table runs constantly during compilation.

        /// <summary>The registered global variables.</summary>
        public Dictionary<string, SurtrNativeGlobalVariable>.ValueCollection Variables => _variables.Values;

        /// <summary>The registered global functions.</summary>
        public Dictionary<string, SurtrNativeGlobalFunction>.ValueCollection Functions => _functions.Values;

        /// <summary>How many global variables are registered, and so the length of the value table.</summary>
        public int VariableCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _variablesByIndex.Count;
        }

        /// <summary>How many global functions are registered.</summary>
        public int FunctionCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => FunctionTable.Length;
        }

        /// <summary>Publishes a global variable and freezes its runtime table index.</summary>
        /// <exception cref="InvalidOperationException">A variable with that name is already registered.</exception>
        public void Register(SurtrNativeGlobalVariable variable)
        {
            if (_variables.ContainsKey(variable.Name))
                throw new InvalidOperationException($"Native global variable '{variable.Name}' is already registered.");

            int index = _variablesByIndex.Count;
            variable.AssignIndex(index);
            _variables.Add(variable.Name, variable);
            _variablesByIndex.Add(variable);

            // Growing by one on every call is fine here: registration happens at host setup time,
            // not per-frame, so it never competes with the performance budget the VM itself has to
            // meet.
            VariableTable.Resize(index + 1);

            // Explicitly, not by zeroing: a zeroed SurtrRawValue carries no tag, so it reads back as
            // the float 0.0 - which for anything but a float global is a value of the wrong type
            // sitting in the slot before the first write.
            VariableTable[index] = DefaultRawValue(variable.VariableType.TypeCode);

            // Classified once, here, rather than by tag-testing every global on every collection.
            if (variable.IsReference)
            {
                int rootIndex = ReferenceVariableSlots.Length;
                ReferenceVariableSlots.Resize(rootIndex + 1);
                ReferenceVariableSlots[rootIndex] = index;
            }
        }

        /// <summary>The value a global of the given type holds before anything writes to it.</summary>
        private static SurtrRawValue DefaultRawValue(SurtrValueTypeCode typeCode) => typeCode switch
        {
            SurtrValueTypeCode.Integer => SurtrValue.CreateInt(0).Raw,
            SurtrValueTypeCode.Float => SurtrValue.CreateFloat(0.0).Raw,
            SurtrValueTypeCode.Boolean => SurtrValue.False.Raw,
            SurtrValueTypeCode.Character => SurtrValue.CreateChar('\0').Raw,
            _ => SurtrValue.Null.Raw,
        };

        /// <summary>Publishes a global function and freezes its runtime table index.</summary>
        /// <exception cref="InvalidOperationException">A function with that name is already registered.</exception>
        public void Register(SurtrNativeGlobalFunction function)
        {
            if (_functions.ContainsKey(function.Name))
                throw new InvalidOperationException($"Native global function '{function.Name}' is already registered.");

            int index = _functions.Count;
            function.AssignIndex(index);
            _functions.Add(function.Name, function);

            Array.Resize(ref FunctionTable, index + 1);
            FunctionTable[index] = function;
        }

        /// <summary>Looks up a global variable by name.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetVariable(string name, out SurtrNativeGlobalVariable variable)
            => _variables.TryGetValue(name, out variable!);

        /// <summary>Looks up a global variable by its frozen table index.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrNativeGlobalVariable GetVariable(int index) => _variablesByIndex[index];

        /// <summary>Looks up a global function by name.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetFunction(string name, out SurtrNativeGlobalFunction function)
            => _functions.TryGetValue(name, out function!);

        #region Host Access
        // The host's read/write surface. Bytecode goes through Ldg/Stg, which index VariableTable
        // directly; these are the same access for code on the other side of the boundary, which
        // cannot see the internal table. Prefer the index overloads in anything that runs per
        // frame - the host can cache SurtrNativeGlobalVariable.Index once and skip the hash lookup
        // forever, since indices are frozen at registration.

        /// <summary>Reads the current value of the global at <paramref name="index"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">No global is registered at that index.</exception>
        public SurtrValue GetValue(int index)
        {
            if ((uint)index >= (uint)_variablesByIndex.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "No native global variable is registered at that index.");

            return new SurtrValue(VariableTable[index]);
        }

        /// <summary>Reads the current value of <paramref name="variable"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrValue GetValue(SurtrNativeGlobalVariable variable) => GetValue(variable.Index);

        /// <summary>Reads the current value of the global named <paramref name="name"/>.</summary>
        /// <returns><see langword="false"/> if no global by that name is registered.</returns>
        public bool TryGetValue(string name, out SurtrValue value)
        {
            if (!_variables.TryGetValue(name, out var variable))
            {
                value = SurtrValue.Null;
                return false;
            }

            value = new SurtrValue(VariableTable[variable.Index]);
            return true;
        }

        /// <summary>Writes <paramref name="value"/> into the global at <paramref name="index"/>.</summary>
        /// <remarks>
        /// <see cref="SurtrNativeGlobalVariable.IsReadOnly"/> is not enforced here: it constrains
        /// Surtr code only, and the compiler is what rejects a <c>Stg</c> against a read-only
        /// global. The host owns the variable and always writes it through this path.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">No global is registered at that index.</exception>
        public void SetValue(int index, SurtrValue value)
        {
            if ((uint)index >= (uint)_variablesByIndex.Count)
                throw new ArgumentOutOfRangeException(nameof(index), index, "No native global variable is registered at that index.");

            AssertAssignable(_variablesByIndex[index], value);
            VariableTable[index] = value.Raw;
        }

        /// <summary>Writes <paramref name="value"/> into <paramref name="variable"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetValue(SurtrNativeGlobalVariable variable, SurtrValue value) => SetValue(variable.Index, value);

        /// <summary>Writes <paramref name="value"/> into the global named <paramref name="name"/>.</summary>
        /// <returns><see langword="false"/> if no global by that name is registered.</returns>
        public bool TrySetValue(string name, SurtrValue value)
        {
            if (!_variables.TryGetValue(name, out var variable))
                return false;

            AssertAssignable(variable, value);
            VariableTable[variable.Index] = value.Raw;
            return true;
        }

        /// <summary>
        /// Debug-only guard that the value being stored matches the global's declared type.
        /// </summary>
        /// <remarks>
        /// A mistyped write into a reference-typed global is the dangerous case: the collector
        /// trusts <see cref="ReferenceVariableSlots"/> and marks whatever it finds in those slots
        /// without a tag test, so a stray float there would be read as an entity id. This catches
        /// it in development and compiles away in Release, where the host is expected to have
        /// been fixed already.
        /// </remarks>
        [Conditional("DEBUG")]
        private static void AssertAssignable(SurtrNativeGlobalVariable variable, SurtrValue value)
        {
            bool ok = variable.VariableType.TypeCode switch
            {
                SurtrValueTypeCode.Integer => value.IsInt,
                SurtrValueTypeCode.Float => value.IsFloat,
                SurtrValueTypeCode.Boolean => value.IsBool,
                SurtrValueTypeCode.Character => value.IsChar,
                _ => value.IsReference,
            };

            Debug.Assert(ok, $"Native global '{variable.Name}' is declared as {variable.VariableType.Reference.ToDisplayString()}, but the value being written is not of that type.");
        }
        #endregion

        /// <summary>Releases the native variable tables. The managed tables need no cleanup.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            VariableTable.Dispose();
            ReferenceVariableSlots.Dispose();
            FunctionTable = Array.Empty<SurtrNativeGlobalFunction>();
            _disposed = true;
        }
    }
}
