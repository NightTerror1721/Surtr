#nullable enable

using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding.Symbols
{
    /// <summary>
    /// A module: the only top-level container the language has (§2.1), and the thing a directory
    /// of <c>.surtr</c> files becomes.
    /// </summary>
    /// <remarks>
    /// A module holds fields, properties, methods, classes, enums and aliases. What look like
    /// globals in source are module-level members, which the runtime stores as statics of the
    /// module — Surtr has no true globals except the ones the host declares.
    /// </remarks>
    public sealed class ModuleSymbol : Symbol
    {
        private IReadOnlyList<NamedTypeSymbol> _types = Array.Empty<NamedTypeSymbol>();
        private IReadOnlyList<FieldSymbol> _fields = Array.Empty<FieldSymbol>();
        private IReadOnlyList<PropertySymbol> _properties = Array.Empty<PropertySymbol>();
        private IReadOnlyList<MethodSymbol> _methods = Array.Empty<MethodSymbol>();
        private IReadOnlyList<MethodSymbol> _extensionMethods = Array.Empty<MethodSymbol>();
        private IReadOnlyList<PropertySymbol> _extensionProperties = Array.Empty<PropertySymbol>();
        private IReadOnlyList<AliasSymbol> _aliases = Array.Empty<AliasSymbol>();
        private IReadOnlyList<ImportedModule> _reExportedModules = Array.Empty<ImportedModule>();
        private IReadOnlyList<NamedTypeSymbol> _reExportedTypes = Array.Empty<NamedTypeSymbol>();

        /// <summary>Creates a module symbol for a dotted module path.</summary>
        public ModuleSymbol(string path)
        {
            Path = path;

            int lastDot = path.LastIndexOf('.');
            Name = lastDot < 0 ? path : path.Substring(lastDot + 1);
        }

        /// <inheritdoc/>
        public override SymbolKind Kind => SymbolKind.Module;

        /// <inheritdoc/>
        public override string Name { get; }

        /// <summary>The full dotted path, which is what a descriptor's full name starts with.</summary>
        public string Path { get; }

        /// <summary>The classes, enums, value classes and singletons declared in this module.</summary>
        public IReadOnlyList<NamedTypeSymbol> Types
        {
            get => _types;
            internal set
            {
                _types = value;
                _byName = null;
            }
        }

        private Dictionary<string, List<NamedTypeSymbol>>? _byName;
        private Dictionary<string, List<MethodSymbol>>? _methodsByName;
        private Dictionary<string, FieldSymbol>? _fieldsByName;
        private Dictionary<string, PropertySymbol>? _propertiesByName;

        /// <summary>
        /// Every type declared here under a name, which is a list rather than one symbol because
        /// arity is part of a type's identity: <c>Result&lt;T&gt;</c> and <c>Result&lt;T, E&gt;</c>
        /// are two declarations sharing a source name. Types re-exported as this module's own
        /// (<c>export import</c>, §2.1) are folded in, so a qualified <c>Aggregator.Type</c> names
        /// them as if they were declared here.
        /// </summary>
        public IReadOnlyList<NamedTypeSymbol> FindTypes(string name)
        {
            if (_byName is null)
            {
                _byName = new Dictionary<string, List<NamedTypeSymbol>>(StringComparer.Ordinal);
                foreach (var type in _types)
                {
                    AddTypeToIndex(type);
                }

                foreach (var type in _reExportedTypes)
                {
                    AddTypeToIndex(type);
                }
            }

            return _byName.TryGetValue(name, out var found)
                ? found
                : (IReadOnlyList<NamedTypeSymbol>)Array.Empty<NamedTypeSymbol>();
        }

        private void AddTypeToIndex(NamedTypeSymbol type)
        {
            if (!_byName!.TryGetValue(type.Name, out var bucket))
            {
                bucket = new List<NamedTypeSymbol>();
                _byName.Add(type.Name, bucket);
            }

            bucket.Add(type);
        }

        /// <summary>The module-level variables.</summary>
        public IReadOnlyList<FieldSymbol> Fields
        {
            get => _fields;
            internal set
            {
                _fields = value;
                _fieldsByName = null;
            }
        }

        /// <summary>The module-level properties.</summary>
        public IReadOnlyList<PropertySymbol> Properties
        {
            get => _properties;
            internal set
            {
                _properties = value;
                _propertiesByName = null;
            }
        }

        /// <summary>The module-level functions.</summary>
        public IReadOnlyList<MethodSymbol> Methods
        {
            get => _methods;
            internal set
            {
                _methods = value;
                _methodsByName = null;
            }
        }

        /// <summary>
        /// Every method declared here under a name, in declaration order. The indexed twin of
        /// <see cref="Methods"/> for the resolution paths that already know the name they want —
        /// built lazily and invalidated by the setter, exactly like <see cref="FindTypes"/>.
        /// </summary>
        public IReadOnlyList<MethodSymbol> FindMethods(string name)
        {
            if (_methodsByName is null)
            {
                _methodsByName = new Dictionary<string, List<MethodSymbol>>(StringComparer.Ordinal);
                foreach (var method in _methods)
                {
                    if (!_methodsByName.TryGetValue(method.Name, out var bucket))
                    {
                        bucket = new List<MethodSymbol>();
                        _methodsByName.Add(method.Name, bucket);
                    }

                    bucket.Add(method);
                }
            }

            return _methodsByName.TryGetValue(name, out var found)
                ? found
                : (IReadOnlyList<MethodSymbol>)Array.Empty<MethodSymbol>();
        }

        /// <summary>
        /// The field declared here under a name, or <see langword="null"/>. The indexed twin of
        /// <see cref="Fields"/>; a name wins the first declaration, matching the linear scan it
        /// replaces (a duplicate field is a compile error anyway).
        /// </summary>
        public FieldSymbol? FindField(string name)
        {
            if (_fieldsByName is null)
            {
                _fieldsByName = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);
                foreach (var field in _fields)
                {
                    if (!_fieldsByName.ContainsKey(field.Name))
                        _fieldsByName.Add(field.Name, field);
                }
            }

            return _fieldsByName.TryGetValue(name, out var found) ? found : null;
        }

        /// <summary>
        /// The property declared here under a name, or <see langword="null"/>. The indexed twin of
        /// <see cref="Properties"/>; a name wins the first declaration, matching the linear scan it
        /// replaces (a duplicate property is a compile error anyway).
        /// </summary>
        public PropertySymbol? FindProperty(string name)
        {
            if (_propertiesByName is null)
            {
                _propertiesByName = new Dictionary<string, PropertySymbol>(StringComparer.Ordinal);
                foreach (var property in _properties)
                {
                    if (!_propertiesByName.ContainsKey(property.Name))
                        _propertiesByName.Add(property.Name, property);
                }
            }

            return _propertiesByName.TryGetValue(name, out var found) ? found : null;
        }

        /// <summary>
        /// Every method declared inside an <c>extension</c> block anywhere in this module (§15) —
        /// at module level or nested inside a class, in declaration order. Kept apart from
        /// <see cref="Methods"/> so ordinary bare-name resolution never sees one: an extension is only
        /// ever tried as a fallback on an explicit receiver (§15.3), never as a plain module function.
        /// </summary>
        public IReadOnlyList<MethodSymbol> ExtensionMethods
        {
            get => _extensionMethods;
            internal set => _extensionMethods = value;
        }

        /// <summary>
        /// Every property declared inside an <c>extension</c> block anywhere in this module (§15),
        /// the property counterpart of <see cref="ExtensionMethods"/> — kept apart from
        /// <see cref="Properties"/> for the same reason.
        /// </summary>
        public IReadOnlyList<PropertySymbol> ExtensionProperties
        {
            get => _extensionProperties;
            internal set => _extensionProperties = value;
        }

        /// <summary>The module-level type aliases, which are erased before anything is emitted.</summary>
        public IReadOnlyList<AliasSymbol> Aliases
        {
            get => _aliases;
            internal set => _aliases = value;
        }

        /// <summary>
        /// The modules this one re-exports as its own (<c>export import module X.Y;</c>, §2.1) —
        /// a consumer that imports this module sees those modules' members too, as if they were
        /// declared here. The re-exported modules' <em>types</em> are folded into <see cref="Types"/>
        /// directly; this list is what carries their module-level members (functions and variables)
        /// and what lets a wildcard import of this module reach them. A whole-module re-export has
        /// no member filter; a re-export through a named/selective member import names exactly the
        /// members it re-exposed.
        /// </summary>
        public IReadOnlyList<ImportedModule> ReExportedModules
        {
            get => _reExportedModules;
            internal set => _reExportedModules = value;
        }

        /// <summary>
        /// The types this module re-exports by name (<c>export import X.Y;</c>, §2.1) — the same
        /// symbols their declaring module owns, never copies, so a qualified
        /// <c>Aggregator.Type</c> names them as if declared here. Kept apart from <see cref="Types"/>
        /// so the emitter still sees only the types this module truly declares.
        /// </summary>
        public IReadOnlyList<NamedTypeSymbol> ReExportedTypes
        {
            get => _reExportedTypes;
            internal set
            {
                _reExportedTypes = value;
                _byName = null;
            }
        }

        /// <inheritdoc/>
        public override string ToDisplayString() => Path;
    }
}
