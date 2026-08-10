#nullable enable

using Surtr.Compiler.Binding.Symbols;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Finds a member on a type, following base classes and interfaces, and reading it as the
    /// receiver's own type arguments make it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A member is declared once, on the open generic, so <c>Box&lt;T&gt;.get()</c> is one symbol
    /// no matter how many constructions exist — that is what erasure means and it is deliberate.
    /// What the <em>compiler</em> owes on top is the substituted view: on a <c>Box&lt;int&gt;</c>
    /// receiver, <c>get()</c> returns <c>int</c> and <c>set("x")</c> is a type error. This builds
    /// that view once per construction and caches it.
    /// </para>
    /// <para>
    /// The walk goes base classes first, then interfaces, because a class member is what a call
    /// binds to and a contract only says one must exist.
    /// </para>
    /// </remarks>
    public sealed class MemberLookup
    {
        private static readonly IReadOnlyList<MethodSymbol> NoMethods = Array.Empty<MethodSymbol>();

        private readonly TypeSymbolFactory _factory;

        private readonly Dictionary<NamedTypeSymbol, IReadOnlyList<Symbol>> _substituted =
            new Dictionary<NamedTypeSymbol, IReadOnlyList<Symbol>>();

        /// <summary>Creates a lookup over one compilation's types.</summary>
        public MemberLookup(TypeSymbolFactory factory) => _factory = factory;

        /// <summary>Every method of that name reachable on the type, nearest declaration first.</summary>
        public IReadOnlyList<MethodSymbol> FindMethods(TypeSymbol type, string name)
        {
            List<MethodSymbol>? found = null;

            foreach (var member in Reachable(type))
            {
                if (member is MethodSymbol method && string.Equals(method.Name, name, StringComparison.Ordinal))
                    (found ??= new List<MethodSymbol>()).Add(method);
            }

            return found ?? NoMethods;
        }

        /// <summary>The nearest field of that name, or <see langword="null"/>.</summary>
        public FieldSymbol? FindField(TypeSymbol type, string name)
        {
            foreach (var member in Reachable(type))
            {
                if (member is FieldSymbol field && string.Equals(field.Name, name, StringComparison.Ordinal))
                    return field;
            }

            return null;
        }

        /// <summary>The nearest property of that name, or <see langword="null"/>.</summary>
        public PropertySymbol? FindProperty(TypeSymbol type, string name)
        {
            foreach (var member in Reachable(type))
            {
                if (member is PropertySymbol property && string.Equals(property.Name, name, StringComparison.Ordinal))
                    return property;
            }

            return null;
        }

        /// <summary>
        /// Every member reachable on a type, in lookup order: its own first, then its bases, then
        /// the contracts it satisfies.
        /// </summary>
        public IEnumerable<Symbol> Reachable(TypeSymbol type)
        {
            if (type.NonNullable is not NamedTypeSymbol named)
                yield break;

            var seen = new HashSet<NamedTypeSymbol>();
            var queue = new Queue<NamedTypeSymbol>();
            queue.Enqueue(named);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!seen.Add(current))
                    continue;

                foreach (var member in MembersOf(current))
                    yield return member;

                if (current.BaseType is NamedTypeSymbol baseType)
                    queue.Enqueue(baseType);

                foreach (var contract in current.Interfaces)
                    queue.Enqueue(contract);
            }
        }

        /// <summary>The members of a type as its own type arguments make them.</summary>
        public IReadOnlyList<Symbol> MembersOf(NamedTypeSymbol type)
        {
            if (!type.IsConstructed)
                return type.Members;

            if (_substituted.TryGetValue(type, out var cached))
                return cached;

            var substitution = type.SubstitutionFromArguments(_factory);
            if (substitution.IsEmpty)
                return type.Members;

            var members = new List<Symbol>(type.Members.Count);
            foreach (var member in type.Members)
                members.Add(Substitute(member, type, substitution));

            _substituted.Add(type, members);
            return members;
        }

        private Symbol Substitute(Symbol member, NamedTypeSymbol owner, TypeSubstitution substitution)
        {
            switch (member)
            {
                case FieldSymbol field:
                {
                    var type = substitution.Apply(field.Type);
                    if (ReferenceEquals(type, field.Type))
                        return field;

                    return new FieldSymbol(field.Name, owner, type)
                    {
                        IsStatic = field.IsStatic,
                        IsReadOnly = field.IsReadOnly,
                        Accessibility = field.Accessibility,
                        IsSynthetic = field.IsSynthetic,
                    };
                }

                case PropertySymbol property:
                {
                    var type = substitution.Apply(property.Type);
                    if (ReferenceEquals(type, property.Type))
                        return property;

                    return new PropertySymbol(property.Name, owner, type)
                    {
                        IsStatic = property.IsStatic,
                        Accessibility = property.Accessibility,
                        Getter = property.Getter is null ? null : (MethodSymbol)Substitute(property.Getter, owner, substitution),
                        Setter = property.Setter is null ? null : (MethodSymbol)Substitute(property.Setter, owner, substitution),
                    };
                }

                case MethodSymbol method:
                    return Substitute(method, owner, substitution);

                default:
                    return member;
            }
        }

        private MethodSymbol Substitute(MethodSymbol method, NamedTypeSymbol owner, TypeSubstitution substitution)
        {
            var returnType = substitution.Apply(method.ReturnType);

            bool changed = !ReferenceEquals(returnType, method.ReturnType);
            var parameters = new ParameterSymbol[method.Parameters.Count];

            for (int i = 0; i < parameters.Length; i++)
            {
                var original = method.Parameters[i];
                var type = substitution.Apply(original.Type);
                changed |= !ReferenceEquals(type, original.Type);
                parameters[i] = original;

                if (!ReferenceEquals(type, original.Type))
                {
                    parameters[i] = new ParameterSymbol(original.Name, type, i)
                    {
                        HasDefaultValue = original.HasDefaultValue,
                        IsVararg = original.IsVararg,
                    };
                }
            }

            if (!changed)
                return method;

            var substituted = new MethodSymbol(method.Name, owner, returnType)
            {
                IsStatic = method.IsStatic,
                Accessibility = method.Accessibility,
                Dispatch = method.Dispatch,
                Role = method.Role,
                IsOverride = method.IsOverride,
                IsSealed = method.IsSealed,
                IsNative = method.IsNative,
                IsInline = method.IsInline,
                IsForceInline = method.IsForceInline,
                IsConst = method.IsConst,
                IsSynthetic = method.IsSynthetic,
                IsConversion = method.IsConversion,
                TypeParameters = method.TypeParameters,
                Parameters = parameters,
            };

            return substituted;
        }
    }
}
