#nullable enable

using Surtr.Runtime.Objects;
using System;

namespace Surtr.Runtime.Classes
{
    /// <summary>A single declared parameter of a <see cref="SurtrMethodInfo"/>.</summary>
    public readonly struct SurtrParameterInfo
    {
        private readonly string _name;
        private readonly SurtrClassReference _parameterType;

        /// <summary>Creates parameter metadata.</summary>
        public SurtrParameterInfo(string name, SurtrClassReference parameterType)
        {
            _name = name;
            _parameterType = parameterType;
        }

        /// <summary>The parameter's declared name.</summary>
        public string Name => _name;

        /// <summary>The parameter's declared type.</summary>
        public SurtrClassReference ParameterType => _parameterType;
    }

    /// <summary>Metadata for a method declared in a module or a class.</summary>
    public sealed class SurtrMethodInfo : SurtrMemberInfo
    {
        private readonly SurtrClassReference _returnType;
        private readonly SurtrParameterInfo[] _parameters;

        /// <summary>Creates method metadata.</summary>
        public SurtrMethodInfo(
            string name,
            SurtrClassReference returnType,
            SurtrParameterInfo[] parameters,
            bool isStatic,
            SurtrVisibility visibility,
            SurtrClassReference declaringType)
            : base(name, isStatic, visibility, declaringType)
        {
            _returnType = returnType;
            _parameters = parameters;
        }

        /// <inheritdoc/>
        public override SurtrMemberKind Kind => SurtrMemberKind.Method;

        /// <summary>The method's declared return type.</summary>
        public SurtrClassReference ReturnType => _returnType;

        /// <summary>The method's declared parameters, in order.</summary>
        public ReadOnlySpan<SurtrParameterInfo> Parameters => _parameters;

        /// <summary>
        /// The method's signature expressed as a closure descriptor, so overloads can be told
        /// apart by a single string comparison.
        /// </summary>
        public SurtrClassReference ToSignature()
        {
            var parameterTypes = new SurtrClassReference[_parameters.Length];
            for (int i = 0; i < _parameters.Length; i++)
                parameterTypes[i] = _parameters[i].ParameterType;

            return SurtrClassReference.Closure(_returnType, parameterTypes);
        }

        // Signatures are descriptors, and the bytecode body holds no entity handles yet, so
        // there is nothing here for the collector to trace.
        internal override void VisitReferences(SurtrEntityMarker marker) { }
    }
}
