#nullable enable

using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// The runtime metadata for a Surtr class: the resolved counterpart of a
    /// <see cref="SurtrClassReference"/>.
    /// </summary>
    /// <remarks>
    /// A class is itself a member, because every class is declared inside either a module or
    /// another class - Surtr has no free-floating types. <see cref="SelfReference"/> is the
    /// descriptor other metadata uses to point back here before resolution has run.
    /// </remarks>
    public sealed class SurtrClass : SurtrMemberInfo
    {
        private readonly SurtrValueTypeCode _typeCode;
        private readonly SurtrClassReference _selfReference;
        private readonly SurtrClassReference _baseType;

        private readonly Dictionary<string, SurtrFieldInfo> _fields;
        private readonly Dictionary<string, SurtrPropertyInfo> _properties;
        private readonly Dictionary<string, SurtrMethodInfo[]> _methods;
        private readonly Dictionary<string, SurtrClass> _nestedClasses;

        /// <summary>Creates class metadata.</summary>
        public SurtrClass(
            string name,
            SurtrValueTypeCode typeCode,
            SurtrClassReference selfReference,
            SurtrClassReference baseType,
            SurtrVisibility visibility,
            SurtrClassReference declaringType)
            : base(name, isStatic: false, visibility, declaringType)
        {
            _typeCode = typeCode;
            _selfReference = selfReference;
            _baseType = baseType;

            _fields = new Dictionary<string, SurtrFieldInfo>(StringComparer.Ordinal);
            _properties = new Dictionary<string, SurtrPropertyInfo>(StringComparer.Ordinal);
            _methods = new Dictionary<string, SurtrMethodInfo[]>(StringComparer.Ordinal);
            _nestedClasses = new Dictionary<string, SurtrClass>(StringComparer.Ordinal);
        }

        /// <inheritdoc/>
        public override SurtrMemberKind Kind => SurtrMemberKind.Class;

        /// <summary>Which <see cref="SurtrValueTypeCode"/> family this class belongs to.</summary>
        public SurtrValueTypeCode TypeCode => _typeCode;

        /// <summary>The descriptor other metadata uses to refer to this class.</summary>
        public SurtrClassReference SelfReference => _selfReference;

        /// <summary>The class this one derives from, or a default reference if it has no base.</summary>
        public SurtrClassReference BaseType => _baseType;

        /// <summary>The fields declared directly on this class, keyed by name.</summary>
        public IReadOnlyDictionary<string, SurtrFieldInfo> Fields => _fields;

        /// <summary>The properties declared directly on this class, keyed by name.</summary>
        public IReadOnlyDictionary<string, SurtrPropertyInfo> Properties => _properties;

        /// <summary>The methods declared directly on this class, keyed by name. Overloads share a name, so each entry is a group.</summary>
        public IReadOnlyDictionary<string, SurtrMethodInfo[]> Methods => _methods;

        /// <summary>The classes and enums declared directly inside this class, keyed by name.</summary>
        public IReadOnlyDictionary<string, SurtrClass> NestedClasses => _nestedClasses;

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

            foreach (var nested in _nestedClasses.Values)
                marker.Mark(nested);
        }
    }
}
