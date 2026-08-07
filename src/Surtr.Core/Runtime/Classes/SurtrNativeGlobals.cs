#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// A global variable defined by the embedding host.
    /// </summary>
    /// <remarks>
    /// These are the only genuinely global names in Surtr - everything a Surtr program declares
    /// belongs to a module. The compiler needs <see cref="VariableType"/> to type-check uses;
    /// the interpreter needs <see cref="Address"/> to read and write the storage directly,
    /// without a call through an accessor.
    /// </remarks>
    public sealed unsafe class SurtrNativeGlobalVariable
    {
        private readonly string _name;
        private readonly SurtrTypeHandle _variableType;
        private readonly void* _address;
        private readonly bool _readOnly;

        /// <summary>Binds a host global to the storage at <paramref name="address"/>.</summary>
        /// <remarks>
        /// The storage must outlive the runtime and must not move, so it has to be unmanaged
        /// memory or a pinned allocation - an unpinned managed object's address is not stable.
        /// </remarks>
        public SurtrNativeGlobalVariable(string name, SurtrTypeHandle variableType, void* address, bool isReadOnly)
        {
            if (address is null)
                throw new ArgumentException($"Native global '{name}' was given a null address.", nameof(address));

            _name = name;
            _variableType = variableType;
            _address = address;
            _readOnly = isReadOnly;
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

        /// <summary>The address of the host's storage for this variable.</summary>
        public void* Address
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _address;
        }

        /// <summary>Whether Surtr code may only read this variable.</summary>
        public bool IsReadOnly
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _readOnly;
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
    }

    /// <summary>
    /// Everything the host has published globally: the shared surface the compiler resolves
    /// unqualified names against and the interpreter dispatches through.
    /// </summary>
    /// <remarks>
    /// Not tied to any module, because host globals outlive and cross all of them. Surtr code
    /// can never add to this table - only the host can, before or between loads.
    /// </remarks>
    public sealed class SurtrNativeGlobalTable
    {
        private readonly Dictionary<string, SurtrNativeGlobalVariable> _variables;
        private readonly Dictionary<string, SurtrNativeGlobalFunction> _functions;

        /// <summary>Creates an empty table.</summary>
        public SurtrNativeGlobalTable()
        {
            _variables = new Dictionary<string, SurtrNativeGlobalVariable>(StringComparer.Ordinal);
            _functions = new Dictionary<string, SurtrNativeGlobalFunction>(StringComparer.Ordinal);
        }

        // Concrete collections rather than IReadOnlyDictionary: an interface-typed lookup costs a
        // dispatch the JIT can't devirtualize, and foreach over the interface boxes the struct
        // enumerator. Name resolution against this table runs constantly during compilation.

        /// <summary>The registered global variables.</summary>
        public Dictionary<string, SurtrNativeGlobalVariable>.ValueCollection Variables => _variables.Values;

        /// <summary>The registered global functions.</summary>
        public Dictionary<string, SurtrNativeGlobalFunction>.ValueCollection Functions => _functions.Values;

        /// <summary>Publishes a global variable.</summary>
        /// <exception cref="InvalidOperationException">A variable with that name is already registered.</exception>
        public void Register(SurtrNativeGlobalVariable variable)
        {
            if (_variables.ContainsKey(variable.Name))
                throw new InvalidOperationException($"Native global variable '{variable.Name}' is already registered.");

            _variables.Add(variable.Name, variable);
        }

        /// <summary>Publishes a global function.</summary>
        /// <exception cref="InvalidOperationException">A function with that name is already registered.</exception>
        public void Register(SurtrNativeGlobalFunction function)
        {
            if (_functions.ContainsKey(function.Name))
                throw new InvalidOperationException($"Native global function '{function.Name}' is already registered.");

            _functions.Add(function.Name, function);
        }

        /// <summary>Looks up a global variable by name.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetVariable(string name, out SurtrNativeGlobalVariable variable)
            => _variables.TryGetValue(name, out variable!);

        /// <summary>Looks up a global function by name.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetFunction(string name, out SurtrNativeGlobalFunction function)
            => _functions.TryGetValue(name, out function!);
    }
}
