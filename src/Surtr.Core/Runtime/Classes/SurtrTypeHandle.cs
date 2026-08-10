#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// The bridge between an unresolved <see cref="SurtrClassReference"/> descriptor and the
    /// managed <see cref="SurtrClass"/> it eventually names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A handle starts life resolved to <see langword="null"/>: metadata is built while classes
    /// are still being declared, so nothing can point at a real class yet. Resolution happens
    /// once, when the owning module is loaded into the runtime context and every descriptor in
    /// its <see cref="SurtrTypeHandleTable"/> is looked up.
    /// </para>
    /// <para>
    /// Handles are interned per table, so every use of a given descriptor shares one instance.
    /// That means resolution runs once per distinct type rather than once per mention, and two
    /// handles from the same table can be compared by reference instead of by string.
    /// </para>
    /// </remarks>
    public sealed class SurtrTypeHandle
    {
        private readonly SurtrClassReference _reference;
        private SurtrTypeInfo? _resolvedType;

        internal SurtrTypeHandle(SurtrClassReference reference) => _reference = reference;

        /// <summary>The descriptor this handle was built from. Always available, resolved or not.</summary>
        public SurtrClassReference Reference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _reference;
        }

        /// <summary>
        /// The class or interface this handle names, or <see langword="null"/> until the owning
        /// module is loaded. Check <see cref="SurtrMemberInfo.Kind"/> to tell the two apart.
        /// </summary>
        public SurtrTypeInfo? ResolvedType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _resolvedType;
        }

        /// <summary>The class this handle names, or <see langword="null"/> if unresolved or if it names an interface.</summary>
        public SurtrClass? ResolvedClass
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _resolvedType as SurtrClass;
        }

        /// <summary>The interface this handle names, or <see langword="null"/> if unresolved or if it names a class.</summary>
        public SurtrInterface? ResolvedInterface
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _resolvedType as SurtrInterface;
        }

        /// <summary>Whether <see cref="ResolvedType"/> has been filled in.</summary>
        public bool IsResolved
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _resolvedType is not null;
        }

        /// <summary>The type family this handle names, read from the descriptor without needing resolution.</summary>
        public SurtrValueTypeCode TypeCode
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _reference.TypeCode;
        }

        /// <summary>
        /// Binds this handle to the type it names. Called by the loader once, while bringing a
        /// module into the runtime context.
        /// </summary>
        /// <exception cref="InvalidOperationException">The handle was already resolved.</exception>
        internal void Resolve(SurtrTypeInfo resolvedType)
        {
            if (_resolvedType is not null)
                throw new InvalidOperationException($"Type handle '{_reference.Descriptor}' is already resolved.");

            _resolvedType = resolvedType;
        }

        /// <summary>Drops the resolved type, returning the handle to its unloaded state.</summary>
        internal void Unresolve() => _resolvedType = null;

        /// <inheritdoc/>
        public override string ToString()
            => IsResolved ? _reference.Descriptor : _reference.Descriptor + " (unresolved)";
    }

    /// <summary>
    /// The interning table that owns every <see cref="SurtrTypeHandle"/> a module mentions.
    /// </summary>
    /// <remarks>
    /// Metadata construction funnels every type mention through <see cref="GetOrAdd"/>, so the
    /// table doubles as the module's complete list of external dependencies: resolving a module
    /// is exactly "walk this table and bind every entry".
    /// </remarks>
    public sealed class SurtrTypeHandleTable
    {
        private readonly Dictionary<string, SurtrTypeHandle> _handles;

        /// <summary>Creates an empty table.</summary>
        public SurtrTypeHandleTable()
            => _handles = new Dictionary<string, SurtrTypeHandle>(StringComparer.Ordinal);

        /// <summary>How many distinct types the owning module mentions.</summary>
        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _handles.Count;
        }

        /// <summary>
        /// Every handle in the table. Typed as the concrete collection so <c>foreach</c> uses the
        /// struct enumerator instead of boxing one behind <see cref="IEnumerable{T}"/>.
        /// </summary>
        public Dictionary<string, SurtrTypeHandle>.ValueCollection Handles => _handles.Values;

        /// <summary>
        /// Returns the shared handle for <paramref name="reference"/>, creating an unresolved one
        /// the first time that descriptor is seen.
        /// </summary>
        public SurtrTypeHandle GetOrAdd(SurtrClassReference reference)
        {
            string descriptor = reference.Descriptor;
            if (_handles.TryGetValue(descriptor, out var handle))
                return handle;

            handle = new SurtrTypeHandle(reference);
            _handles.Add(descriptor, handle);
            return handle;
        }

        /// <summary>Looks up an existing handle by descriptor without creating one.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(string descriptor, out SurtrTypeHandle handle)
            => _handles.TryGetValue(descriptor, out handle!);

        /// <summary>Whether every handle in the table has been bound to a class.</summary>
        public bool IsFullyResolved
        {
            get
            {
                foreach (var handle in _handles.Values)
                {
                    if (!handle.IsResolved)
                        return false;
                }

                return true;
            }
        }

        /// <summary>Returns the descriptors of every handle still waiting to be bound, for diagnostics.</summary>
        public List<string> GetUnresolvedDescriptors()
        {
            var unresolved = new List<string>();
            foreach (var pair in _handles)
            {
                if (!pair.Value.IsResolved)
                    unresolved.Add(pair.Key);
            }

            return unresolved;
        }

        /// <summary>Returns every handle to its unloaded state, for when a module is unloaded.</summary>
        internal void UnresolveAll()
        {
            foreach (var handle in _handles.Values)
                handle.Unresolve();
        }
    }
}
