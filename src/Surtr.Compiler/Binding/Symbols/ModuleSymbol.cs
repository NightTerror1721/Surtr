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
        private IReadOnlyList<AliasSymbol> _aliases = Array.Empty<AliasSymbol>();

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

        /// <summary>
        /// Every type declared here under a name, which is a list rather than one symbol because
        /// arity is part of a type's identity: <c>Result&lt;T&gt;</c> and <c>Result&lt;T, E&gt;</c>
        /// are two declarations sharing a source name.
        /// </summary>
        public IReadOnlyList<NamedTypeSymbol> FindTypes(string name)
        {
            if (_byName is null)
            {
                _byName = new Dictionary<string, List<NamedTypeSymbol>>(StringComparer.Ordinal);
                foreach (var type in _types)
                {
                    if (!_byName.TryGetValue(type.Name, out var bucket))
                    {
                        bucket = new List<NamedTypeSymbol>();
                        _byName.Add(type.Name, bucket);
                    }

                    bucket.Add(type);
                }
            }

            return _byName.TryGetValue(name, out var found)
                ? found
                : (IReadOnlyList<NamedTypeSymbol>)Array.Empty<NamedTypeSymbol>();
        }

        /// <summary>The module-level variables.</summary>
        public IReadOnlyList<FieldSymbol> Fields
        {
            get => _fields;
            internal set => _fields = value;
        }

        /// <summary>The module-level properties.</summary>
        public IReadOnlyList<PropertySymbol> Properties
        {
            get => _properties;
            internal set => _properties = value;
        }

        /// <summary>The module-level functions.</summary>
        public IReadOnlyList<MethodSymbol> Methods
        {
            get => _methods;
            internal set => _methods = value;
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

        /// <summary>The module-level type aliases, which are erased before anything is emitted.</summary>
        public IReadOnlyList<AliasSymbol> Aliases
        {
            get => _aliases;
            internal set => _aliases = value;
        }

        /// <inheritdoc/>
        public override string ToDisplayString() => Path;
    }
}
