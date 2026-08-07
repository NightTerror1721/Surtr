#nullable enable

using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// A pure contract: a set of public abstract methods and properties a class promises to
    /// implement. A class extends at most one class but may implement any number of interfaces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Interfaces carry no state and no bodies - no fields, no default methods, no static
    /// members. Everything declared on one is public and abstract, so an interface never appears
    /// in a dispatch path itself; it only supplies the slot numbering that a class's interface
    /// dispatch table maps onto its own vtable.
    /// </para>
    /// <para>
    /// <see cref="MethodSlots"/> is that numbering, and it is the reason interfaces need
    /// identity: a class implementing this interface stores one vtable index per entry, in this
    /// exact order.
    /// </para>
    /// </remarks>
    public sealed class SurtrInterface : SurtrTypeInfo
    {
        private readonly Dictionary<string, SurtrMethodInfo[]> _methods;
        private readonly Dictionary<string, SurtrPropertyInfo> _properties;

        /// <summary>
        /// Every interface this one extends, transitively closed and flattened by the loader.
        /// A class implementing this interface implicitly implements all of these too.
        /// </summary>
        internal SurtrInterface[] ExtendedInterfaces;

        /// <summary>
        /// The contract in slot order: entry <c>i</c> is interface method slot <c>i</c>. Includes
        /// the slots inherited from <see cref="ExtendedInterfaces"/>, so one interface has one
        /// flat numbering.
        /// </summary>
        internal SurtrMethodInfo[] MethodSlots;

        /// <summary>
        /// Dense id assigned at load time, so a class can find this interface in its dispatch
        /// table by integer compare instead of by reference or name.
        /// </summary>
        internal int InterfaceId = -1;

        /// <summary>The interfaces this one declares that it extends, by unresolved handle.</summary>
        internal SurtrTypeHandle[] DeclaredExtendedInterfaces = Array.Empty<SurtrTypeHandle>();

        /// <summary>Creates interface metadata with no members yet.</summary>
        public SurtrInterface(
            string name,
            SurtrClassReference selfReference,
            SurtrVisibility visibility,
            SurtrTypeHandle? declaringType)
            : base(name, SurtrMemberKind.Interface, selfReference, visibility, declaringType)
        {
            _methods = new Dictionary<string, SurtrMethodInfo[]>(StringComparer.Ordinal);
            _properties = new Dictionary<string, SurtrPropertyInfo>(StringComparer.Ordinal);

            ExtendedInterfaces = Array.Empty<SurtrInterface>();
            MethodSlots = Array.Empty<SurtrMethodInfo>();
        }

        /// <summary>How many method slots this contract declares, inherited ones included.</summary>
        public int MethodSlotCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => MethodSlots.Length;
        }

        /// <summary>Looks up the overload group declared on this interface under a name.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetMethods(string name, out SurtrMethodInfo[] overloads) => _methods.TryGetValue(name, out overloads!);

        /// <summary>Looks up a property declared on this interface.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetProperty(string name, out SurtrPropertyInfo property) => _properties.TryGetValue(name, out property!);

        /// <summary>The overload groups declared directly on this interface.</summary>
        public Dictionary<string, SurtrMethodInfo[]>.ValueCollection Methods => _methods.Values;

        /// <summary>The properties declared directly on this interface.</summary>
        public Dictionary<string, SurtrPropertyInfo>.ValueCollection Properties => _properties.Values;

        /// <summary>
        /// Adds a method to the contract.
        /// </summary>
        /// <exception cref="ArgumentException">The method is not public and abstract, or it is static.</exception>
        public void AddMethod(SurtrMethodInfo method)
        {
            ThrowIfBuilt();

            // Interfaces are contracts and nothing else: no bodies, no state, no hidden members.
            // Rejecting here means the dispatch tables can assume it downstream.
            if (method.Dispatch != SurtrMethodDispatch.Abstract)
                throw new ArgumentException($"Interface method '{Name}.{method.Name}' must be abstract; default implementations are not supported.", nameof(method));

            if (method.Visibility != SurtrVisibility.Public)
                throw new ArgumentException($"Interface method '{Name}.{method.Name}' must be public.", nameof(method));

            if (method.IsStatic)
                throw new ArgumentException($"Interface method '{Name}.{method.Name}' cannot be static.", nameof(method));

            if (_methods.TryGetValue(method.Name, out var existing))
            {
                Array.Resize(ref existing, existing.Length + 1);
                existing[^1] = method;
                _methods[method.Name] = existing;
            }
            else
            {
                _methods.Add(method.Name, new[] { method });
            }
        }

        /// <summary>
        /// Adds a property to the contract.
        /// </summary>
        /// <exception cref="ArgumentException">The property is not public and abstract, or it is static.</exception>
        public void AddProperty(SurtrPropertyInfo property)
        {
            ThrowIfBuilt();

            if (property.Visibility != SurtrVisibility.Public)
                throw new ArgumentException($"Interface property '{Name}.{property.Name}' must be public.", nameof(property));

            if (property.IsStatic)
                throw new ArgumentException($"Interface property '{Name}.{property.Name}' cannot be static.", nameof(property));

            // A property is dispatched through its accessors, so the abstractness rule lands on
            // them rather than on the property itself.
            if (property.Getter is { Dispatch: not SurtrMethodDispatch.Abstract })
                throw new ArgumentException($"Interface property getter '{Name}.{property.Name}' must be abstract.", nameof(property));

            if (property.Setter is { Dispatch: not SurtrMethodDispatch.Abstract })
                throw new ArgumentException($"Interface property setter '{Name}.{property.Name}' must be abstract.", nameof(property));

            _properties.Add(property.Name, property);
        }

        /// <summary>Declares the interfaces this one extends, by unresolved handle.</summary>
        /// <exception cref="InvalidOperationException">The interface is already built.</exception>
        public void SetDeclaredExtendedInterfaces(SurtrTypeHandle[] interfaces)
        {
            ThrowIfBuilt();
            DeclaredExtendedInterfaces = interfaces;
        }

        internal override void VisitReferences(SurtrEntityMarker marker)
        {
            foreach (var overloads in _methods.Values)
            {
                for (int i = 0; i < overloads.Length; i++)
                    marker.Mark(overloads[i]);
            }

            foreach (var property in _properties.Values)
                marker.Mark(property);
        }
    }
}
