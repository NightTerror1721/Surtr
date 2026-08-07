#nullable enable

using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;

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
    public sealed class SurtrModule : SurtrRuntimeEntity
    {
        private readonly string _path;

        private readonly Dictionary<string, SurtrFieldInfo> _fields;
        private readonly Dictionary<string, SurtrPropertyInfo> _properties;
        private readonly Dictionary<string, SurtrMethodInfo[]> _methods;
        private readonly Dictionary<string, SurtrClass> _classes;

        /// <summary>Creates an empty module with the given dot-separated path.</summary>
        public SurtrModule(string path)
        {
            _path = path;

            _fields = new Dictionary<string, SurtrFieldInfo>(StringComparer.Ordinal);
            _properties = new Dictionary<string, SurtrPropertyInfo>(StringComparer.Ordinal);
            _methods = new Dictionary<string, SurtrMethodInfo[]>(StringComparer.Ordinal);
            _classes = new Dictionary<string, SurtrClass>(StringComparer.Ordinal);
        }

        /// <summary>The module's dot-separated path, as it appears before the <see cref="SurtrClassReference.ModuleSeparator"/> in a full name.</summary>
        public string Path => _path;

        /// <summary>The module-level fields, keyed by name.</summary>
        public IReadOnlyDictionary<string, SurtrFieldInfo> Fields => _fields;

        /// <summary>The module-level properties, keyed by name.</summary>
        public IReadOnlyDictionary<string, SurtrPropertyInfo> Properties => _properties;

        /// <summary>The module-level methods, keyed by name. Overloads share a name, so each entry is a group.</summary>
        public IReadOnlyDictionary<string, SurtrMethodInfo[]> Methods => _methods;

        /// <summary>The classes and enums declared directly in this module, keyed by name.</summary>
        public IReadOnlyDictionary<string, SurtrClass> Classes => _classes;

        /// <summary>
        /// Walks the dot-separated type path of a full name (for example <c>Outer.Nested</c>)
        /// down through this module's classes.
        /// </summary>
        /// <returns>The class the path names, or <see langword="null"/> if any segment is missing.</returns>
        public SurtrClass? FindClass(string typePath)
        {
            int start = 0;
            SurtrClass? current = null;

            while (start <= typePath.Length)
            {
                int separator = typePath.IndexOf(SurtrClassReference.NameSeparator, start);
                int end = separator < 0 ? typePath.Length : separator;
                string segment = typePath.Substring(start, end - start);

                var scope = current is null ? _classes : (IReadOnlyDictionary<string, SurtrClass>)current.NestedClasses;
                if (!scope.TryGetValue(segment, out current))
                    return null;

                if (separator < 0)
                    return current;

                start = separator + 1;
            }

            return current;
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
        }
    }
}
