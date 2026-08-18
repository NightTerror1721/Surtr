#nullable enable

using Surtr.Runtime.Objects;
using Surtr.Runtime.Utilities;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// A Surtr module: the only top-level container in the language, and the unit type
    /// resolution works against.
    /// </summary>
    /// <remarks>
    /// Surtr has no true globals - everything a program declares lives in a module, either
    /// directly or inside one of the module's classes. The only genuinely global names are
    /// host-defined native variables and functions, which are never declared from Surtr source
    /// and so are not held here.
    /// </remarks>
    public sealed class SurtrModule : SurtrRuntimeEntity, IDisposable
    {
        private readonly string _path;
        private readonly SurtrTypeHandleTable _typeHandles;
        private readonly SurtrChunk _chunk;

        private readonly Dictionary<string, SurtrFieldInfo> _fields;
        private readonly Dictionary<string, SurtrPropertyInfo> _properties;
        private readonly Dictionary<string, SurtrMethodInfo[]> _methods;
        private readonly Dictionary<string, SurtrClass> _classes;
        private readonly Dictionary<string, SurtrInterface> _interfaces;

        private bool _loaded;
        private bool _emitted;
        private SurtrBuildState _buildState;

        #region Runtime Access Tables
        // A module is the outermost declaration scope, and Surtr has no true globals, so a
        // module-level variable *is* a static - it just belongs to the module rather than to a
        // class. These tables are therefore the exact counterparts of SurtrClass's, built by the
        // same linker pass, so `StaticFieldGet` reaches a module variable and a class static
        // through one code path.

        /// <summary>Module-level variables in slot order, indexed by <see cref="SurtrFieldInfo.Slot"/>.</summary>
        internal SurtrFieldInfo[] StaticFields;

        /// <summary>Backing storage for <see cref="StaticFields"/>.</summary>
        internal SurtrNativeArray<SurtrRawValue> StaticStorage;

        /// <summary>Which slots of <see cref="StaticStorage"/> hold a reference, and so have to be traced.</summary>
        internal SurtrNativeArray<int> ReferenceStaticSlots;

        /// <summary>Module-level methods, flattened out of the name-keyed overload groups.</summary>
        internal SurtrMethodInfo[] Functions;

        /// <summary>
        /// This module's parameterless static initializer, or <see langword="null"/> if it declares
        /// none. Run once when the module is loaded, after every class in it has been initialized.
        /// </summary>
        internal SurtrMethodInfo? StaticInitializer;
        #endregion

        /// <summary>Creates an empty module with the given dot-separated path.</summary>
        public SurtrModule(string path)
        {
            _path = path;
            _typeHandles = new SurtrTypeHandleTable();
            _chunk = new SurtrChunk();
            _chunk.OwningModule = this;

            _fields = new Dictionary<string, SurtrFieldInfo>(StringComparer.Ordinal);
            _properties = new Dictionary<string, SurtrPropertyInfo>(StringComparer.Ordinal);
            _methods = new Dictionary<string, SurtrMethodInfo[]>(StringComparer.Ordinal);
            _classes = new Dictionary<string, SurtrClass>(StringComparer.Ordinal);
            _interfaces = new Dictionary<string, SurtrInterface>(StringComparer.Ordinal);

            StaticFields = Array.Empty<SurtrFieldInfo>();
            Functions = Array.Empty<SurtrMethodInfo>();
        }

        /// <summary>The module's dot-separated path, as it appears before the <see cref="SurtrClassReference.ModuleSeparator"/> in a full name.</summary>
        public string Path => _path;

        /// <summary>
        /// Every type this module mentions, interned. Doubles as its dependency list: loading the
        /// module means binding every handle in here.
        /// </summary>
        public SurtrTypeHandleTable TypeHandles => _typeHandles;

        /// <summary>The module's bytecode, constant pools and access tables.</summary>
        internal SurtrChunk Chunk => _chunk;

        /// <summary>Whether the runtime context has loaded this module and bound its type handles.</summary>
        public bool IsLoaded => _loaded;

        /// <summary>
        /// Whether every body has been emitted and laid out into the chunk - what
        /// <c>SurtrModuleBuilder.Build</c> finishing means.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="IsBuilt"/>, which is about <em>linking</em> and only becomes
        /// true once a runtime has loaded the module. A module can be written to an image between
        /// the two, and telling "the emitter is done" apart from "nobody has emitted anything yet"
        /// needs a signal of its own - the chunk's tables are legitimately empty for a module that
        /// declares nothing.
        /// </remarks>
        public bool IsEmitted => _emitted;

        /// <summary>Records that the emitter has finished laying out this module's bodies.</summary>
        internal void MarkEmitted() => _emitted = true;

        /// <summary>How far this module is between being declared and being ready to run.</summary>
        public SurtrBuildState BuildState
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _buildState;
        }

        /// <summary>Whether every type in this module has been linked and the module is frozen.</summary>
        public bool IsBuilt
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _buildState == SurtrBuildState.Built;
        }

        /// <summary>Moves this module into <see cref="SurtrBuildState.Linking"/>, rejecting a cycle.</summary>
        internal void BeginLinking()
        {
            if (_buildState == SurtrBuildState.Linking)
                throw new InvalidOperationException($"Cyclic dependency detected while linking module '{_path}'.");

            _buildState = SurtrBuildState.Linking;
        }

        /// <summary>Freezes this module. Every later declaration attempt throws.</summary>
        internal void MarkBuilt() => _buildState = SurtrBuildState.Built;

        private void ThrowIfBuilt()
        {
            if (_buildState == SurtrBuildState.Built)
                throw new InvalidOperationException($"Module '{_path}' is already built and can no longer be modified.");
        }

        /// <summary>Marks the module as loaded once its type handles have all been bound.</summary>
        internal void MarkLoaded() => _loaded = true;

        /// <summary>Unbinds every type handle and marks the module unloaded.</summary>
        internal void MarkUnloaded()
        {
            _typeHandles.UnresolveAll();
            _loaded = false;
        }

        // As in SurtrClass: concrete accessors instead of IReadOnlyDictionary, so lookups don't
        // pay an interface dispatch and enumeration doesn't box the struct enumerator.

        /// <summary>Looks up a module-level field.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetField(string name, out SurtrFieldInfo field) => _fields.TryGetValue(name, out field!);

        /// <summary>Looks up a module-level property.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetProperty(string name, out SurtrPropertyInfo property) => _properties.TryGetValue(name, out property!);

        /// <summary>Looks up the module-level overload group under a name.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetMethods(string name, out SurtrMethodInfo[] overloads) => _methods.TryGetValue(name, out overloads!);

        /// <summary>Looks up a class or enum declared directly in this module.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetClass(string name, out SurtrClass type) => _classes.TryGetValue(name, out type!);

        /// <summary>Looks up an interface declared directly in this module.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetInterface(string name, out SurtrInterface type) => _interfaces.TryGetValue(name, out type!);

        /// <summary>The interfaces declared directly in this module.</summary>
        public Dictionary<string, SurtrInterface>.ValueCollection Interfaces => _interfaces.Values;

        /// <summary>The module-level fields.</summary>
        public Dictionary<string, SurtrFieldInfo>.ValueCollection Fields => _fields.Values;

        /// <summary>The module-level properties.</summary>
        public Dictionary<string, SurtrPropertyInfo>.ValueCollection Properties => _properties.Values;

        /// <summary>The module-level overload groups.</summary>
        public Dictionary<string, SurtrMethodInfo[]>.ValueCollection Methods => _methods.Values;

        /// <summary>The classes and enums declared directly in this module.</summary>
        public Dictionary<string, SurtrClass>.ValueCollection Classes => _classes.Values;

        #region Declaration
        /// <summary>Declares a module-level field.</summary>
        /// <exception cref="InvalidOperationException">The module is already built.</exception>
        public void AddField(SurtrFieldInfo field)
        {
            ThrowIfBuilt();
            _fields.Add(field.Name, field);
        }

        /// <summary>Declares a module-level property.</summary>
        /// <exception cref="InvalidOperationException">The module is already built.</exception>
        public void AddProperty(SurtrPropertyInfo property)
        {
            ThrowIfBuilt();
            _properties.Add(property.Name, property);
        }

        /// <summary>Declares a module-level method, joining any existing overload group of the same name.</summary>
        /// <exception cref="InvalidOperationException">The module is already built.</exception>
        public void AddMethod(SurtrMethodInfo method)
        {
            ThrowIfBuilt();

            _methods.TryGetValue(method.Name, out var overloads);
            _methods[method.Name] = SurtrMethodInfo.AppendOverload(overloads, method, Path);
        }

        /// <summary>Declares a class or enum directly in this module.</summary>
        /// <exception cref="InvalidOperationException">The module is already built.</exception>
        public void AddClass(SurtrClass type)
        {
            ThrowIfBuilt();
            _classes.Add(type.Name, type);
        }

        /// <summary>Declares an interface directly in this module.</summary>
        /// <exception cref="InvalidOperationException">The module is already built.</exception>
        public void AddInterface(SurtrInterface type)
        {
            ThrowIfBuilt();
            _interfaces.Add(type.Name, type);
        }
        #endregion

        /// <summary>
        /// Walks the dot-separated type path of a full name (for example <c>Outer.Nested</c>)
        /// down through this module's classes.
        /// </summary>
        /// <returns>The class the path names, or <see langword="null"/> if any segment is missing.</returns>
        public SurtrClass? FindClass(string typePath)
        {
            // The overwhelmingly common case is an unnested type, where the whole path is one
            // segment and can go straight into the dictionary with no substring at all.
            if (typePath.IndexOf(SurtrClassReference.NameSeparator) < 0)
                return _classes.TryGetValue(typePath, out var type) ? type : null;

            return FindNestedClass(typePath);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private SurtrClass? FindNestedClass(string typePath)
        {
            int start = 0;
            SurtrClass? current = null;

            while (true)
            {
                int separator = typePath.IndexOf(SurtrClassReference.NameSeparator, start);
                int end = separator < 0 ? typePath.Length : separator;

                // netstandard2.1 has no span-keyed dictionary lookup, so each nested segment
                // costs one substring. Only paths that actually nest pay it, and only at load
                // time - resolved handles are what the interpreter uses afterwards.
                string segment = typePath.Substring(start, end - start);

                bool found = current is null
                    ? _classes.TryGetValue(segment, out current)
                    : current.TryGetNestedClass(segment, out current);

                if (!found)
                {
                    // A type nested inside an *interface* has no container to walk into: nesting is
                    // stored on SurtrClass and a contract holds none. `Language-Syntax.md` §2.3
                    // allows one anyway - a nested type carries no state, so it does not reopen the
                    // "pure contract" rule - and §2.6 makes nesting qualification, so such a type is
                    // declared under its whole dotted name. One probe, only on a path the walk
                    // already failed, and only at load.
                    return _classes.TryGetValue(typePath, out var qualified) ? qualified : null;
                }

                if (separator < 0)
                    return current;

                start = separator + 1;
            }
        }

        internal override void VisitReferences(SurtrEntityMarker marker)
        {
            foreach (var field in _fields.Values)
                marker.Mark(field);

            foreach (var property in _properties.Values)
                marker.Mark(property);

            foreach (var overloads in _methods.Values)
            {
                for (int i = 0; i < overloads.Length; i++)
                    marker.Mark(overloads[i]);
            }

            foreach (var type in _classes.Values)
                marker.Mark(type);

            foreach (var type in _interfaces.Values)
                marker.Mark(type);
        }

        /// <summary>Releases the unmanaged buffers the module's chunk and classes own.</summary>
        public void Dispose()
        {
            MarkUnloaded();

            // Nested types are disposed by their declaring class, so only the top level here.
            foreach (var type in _classes.Values)
                type.Dispose();

            StaticStorage.Dispose();
            ReferenceStaticSlots.Dispose();
            _chunk.Dispose();
        }
    }
}
