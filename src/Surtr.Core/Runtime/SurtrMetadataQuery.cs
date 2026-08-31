#nullable enable

using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;
using System.Text;

namespace Surtr.Runtime
{
    /// <summary>
    /// Introspection over Surtr metadata: finding an exact member by signature, describing a
    /// member's full name and signature for humans, enumerating everything a module or type
    /// declares, and the small builder-assist helpers a host uses when hand-registering native
    /// metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one of these closes a gap between what a compiler-facing consumer of
    /// <see cref="SurtrModule"/>/<see cref="SurtrClass"/>/<see cref="SurtrInterface"/> already has
    /// (name-keyed lookup returning an <em>overload group</em>, never an exact member; no
    /// "everything this declares" in one call; no human-readable signature anywhere in the
    /// metadata model) and what a host embedding Surtr for tooling, a debugger, or a script
    /// console needs. Nothing here is on any execution path - it exists for hosts, not for the
    /// interpreter.
    /// </para>
    /// <para>
    /// The reflection Surtr source itself sees through <c>Type</c>/<c>Module</c>/<c>Member</c>
    /// (<c>SurtrReflectionBuiltIns</c>/<c>SurtrModuleReflectionBuiltIns</c>) answers a narrower set
    /// of the same questions from inside a running script; this is the equivalent surface for a
    /// host written in C#, and both share <see cref="IsSynthetic"/> rather than each keeping their
    /// own copy of the "starts with <c>$</c>" rule.
    /// </para>
    /// </remarks>
    public static class SurtrMetadataQuery
    {
        /// <summary>
        /// Whether <paramref name="name"/> is a compiler-synthesized member name - a leading
        /// <c>$</c>, per the <c>$category$context[$index]</c> naming scheme
        /// (<c>CodeGen/ModuleEmitter</c>'s "Two naming conventions are ABI").
        /// </summary>
        public static bool IsSynthetic(string name) => name.Length > 0 && name[0] == '$';

        #region Finding an exact member

        /// <summary>The method on <paramref name="module"/> named <paramref name="name"/> whose parameters match exactly, or <see langword="null"/>.</summary>
        public static SurtrMethodInfo? FindMethod(SurtrModule module, string name, params SurtrClassReference[] parameterTypes)
            => module.TryGetMethods(name, out var overloads) ? FindExactOverload(overloads, parameterTypes) : null;

        /// <summary>The method declared directly on <paramref name="type"/> named <paramref name="name"/> whose parameters match exactly, or <see langword="null"/>.</summary>
        public static SurtrMethodInfo? FindMethod(SurtrClass type, string name, params SurtrClassReference[] parameterTypes)
            => type.TryGetMethods(name, out var overloads) ? FindExactOverload(overloads, parameterTypes) : null;

        /// <summary>The method declared directly on <paramref name="type"/> named <paramref name="name"/> whose parameters match exactly, or <see langword="null"/>.</summary>
        public static SurtrMethodInfo? FindMethod(SurtrInterface type, string name, params SurtrClassReference[] parameterTypes)
            => type.TryGetMethods(name, out var overloads) ? FindExactOverload(overloads, parameterTypes) : null;

        private static SurtrMethodInfo? FindExactOverload(SurtrMethodInfo[] overloads, SurtrClassReference[] parameterTypes)
        {
            foreach (var candidate in overloads)
            {
                if (candidate.ParameterCount != parameterTypes.Length)
                    continue;

                var parameters = candidate.Parameters;
                bool matches = true;

                for (int i = 0; i < parameters.Length; i++)
                {
                    // Descriptor comparison, not display-string: two descriptors are the canonical
                    // form for equality everywhere else in the codebase (SurtrClassReference's own
                    // rule), so an exact-signature lookup follows the same rule rather than
                    // inventing a second notion of "the same type".
                    if (!string.Equals(parameters[i].ParameterType.Reference.Descriptor, parameterTypes[i].Descriptor, StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return candidate;
            }

            return null;
        }

        #endregion

        #region Human-readable names and signatures

        /// <summary>
        /// A method's signature the way a human would write it: <c>foo(x: int, y: string): bool</c>.
        /// </summary>
        /// <remarks>
        /// Nothing in the metadata model renders this today: <see cref="SurtrMethodInfo.SignatureKey"/>
        /// is a comparison key (erasure-written, no return type, no parameter names) and
        /// <see cref="SurtrMethodInfo.ToSignature"/> is the closure-typed view of the method. This
        /// is the third thing, purely for display.
        /// </remarks>
        public static string DescribeSignature(SurtrMethodInfo method)
        {
            var builder = new StringBuilder(method.Name).Append('(');
            var parameters = method.Parameters;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(parameters[i].Name).Append(": ").Append(parameters[i].ParameterType.Reference.ToDisplayString());

                if (parameters[i].IsVarargs)
                    builder.Append("...");
            }

            builder.Append(')');

            var returnType = method.ReturnType.Reference;
            if (returnType.TypeCode != SurtrValueTypeCode.Void)
                builder.Append(": ").Append(returnType.ToDisplayString());

            return builder.ToString();
        }

        /// <summary>The fully-qualified name of a module-level member: <c>module.path:name</c>, with a parameter-type suffix when it names an overloaded method.</summary>
        public static string FullName(SurtrModule module, SurtrMemberInfo member) => Combine(module.Path, member);

        /// <summary>The fully-qualified name of a member declared on a class or interface: <c>Owner:name</c>, with a parameter-type suffix when it names an overloaded method.</summary>
        public static string FullName(SurtrTypeInfo owner, SurtrMemberInfo member)
        {
            string container = owner.SelfReference.TryGetFullName(out string full) ? full : owner.Name;
            return Combine(container, member);
        }

        private static string Combine(string container, SurtrMemberInfo member)
        {
            if (member is not SurtrMethodInfo method || method.ParameterCount == 0)
                return container + ":" + member.Name;

            var builder = new StringBuilder(container).Append(':').Append(member.Name).Append('(');
            var parameters = method.Parameters;

            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(parameters[i].ParameterType.Reference.ToDisplayString());
            }

            return builder.Append(')').ToString();
        }

        #endregion

        #region Enumerating everything

        /// <summary>Every field, property and method declared directly on a module.</summary>
        public static IEnumerable<SurtrMemberInfo> AllMembers(SurtrModule module, bool includeSynthetic = false)
        {
            foreach (var field in module.Fields)
            {
                if (includeSynthetic || !IsSynthetic(field.Name))
                    yield return field;
            }

            foreach (var property in module.Properties)
            {
                if (includeSynthetic || !IsSynthetic(property.Name))
                    yield return property;
            }

            foreach (var overloads in module.Methods)
            {
                foreach (var method in overloads)
                {
                    if (includeSynthetic || !IsSynthetic(method.Name))
                        yield return method;
                }
            }
        }

        /// <summary>Every field, property and method declared on a class, optionally including its nested classes' members.</summary>
        public static IEnumerable<SurtrMemberInfo> AllMembers(SurtrClass type, bool recursive = true, bool includeSynthetic = false)
        {
            foreach (var field in type.Fields)
            {
                if (includeSynthetic || !IsSynthetic(field.Name))
                    yield return field;
            }

            foreach (var property in type.Properties)
            {
                if (includeSynthetic || !IsSynthetic(property.Name))
                    yield return property;
            }

            foreach (var overloads in type.Methods)
            {
                foreach (var method in overloads)
                {
                    if (includeSynthetic || !IsSynthetic(method.Name))
                        yield return method;
                }
            }

            if (!recursive)
                yield break;

            foreach (var nested in type.NestedClasses)
            {
                foreach (var member in AllMembers(nested, recursive: true, includeSynthetic))
                    yield return member;
            }
        }

        /// <summary>Every class, enum and interface a module declares, optionally descending into nested types.</summary>
        public static IEnumerable<SurtrTypeInfo> AllTypes(SurtrModule module, bool recursive = true)
        {
            foreach (var type in module.Classes)
            {
                yield return type;

                if (recursive)
                {
                    foreach (var nested in AllNestedTypes(type))
                        yield return nested;
                }
            }

            foreach (var contract in module.Interfaces)
                yield return contract;
        }

        private static IEnumerable<SurtrTypeInfo> AllNestedTypes(SurtrClass type)
        {
            foreach (var nested in type.NestedClasses)
            {
                yield return nested;

                foreach (var deeper in AllNestedTypes(nested))
                    yield return deeper;
            }

            foreach (var contract in type.NestedInterfaces)
                yield return contract;
        }

        #endregion

        #region Building metadata by hand

        /// <summary>
        /// Builds parameter metadata from a descriptor string in one call - the combination a host
        /// hand-registering a native method reaches for most often, instead of going through
        /// <see cref="SurtrClassReference.FromDescriptor"/> and <see cref="SurtrRuntime.Parameter(string, SurtrClassReference)"/> separately.
        /// </summary>
        public static SurtrParameterInfo Parameter(SurtrRuntime runtime, string name, string descriptor)
            => runtime.Parameter(name, SurtrClassReference.FromDescriptor(descriptor));

        #endregion
    }
}
