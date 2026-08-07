#nullable enable

using Surtr.Runtime.Utilities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// Turns declared metadata into the flattened runtime tables the interpreter indexes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs once per type, after every <see cref="SurtrTypeHandle"/> the module mentions has been
    /// resolved and before anything executes. Linking is depth-first: a type pulls in its base
    /// class and interfaces first, because its own layout is built on top of theirs. The
    /// <see cref="SurtrBuildState.Linking"/> state doubles as the cycle detector - meeting a type
    /// that is already linking means the hierarchy loops back on itself.
    /// </para>
    /// <para>
    /// This is load-time code, not execution-path code, so it favours being obviously correct
    /// over being allocation-free. The dictionaries and signature strings it builds are thrown
    /// away as soon as a type is linked; what survives is the flat arrays.
    /// </para>
    /// </remarks>
    internal static class SurtrTypeLinker
    {
        #region Entry Points
        /// <summary>
        /// Links every type in a module and freezes it.
        /// </summary>
        /// <exception cref="InvalidOperationException">A hierarchy is cyclic, or a concrete class leaves an abstract member unimplemented.</exception>
        internal static void LinkModule(SurtrModule module)
        {
            if (module.IsBuilt)
                return;

            module.BeginLinking();

            // Interfaces first: a class's dispatch table needs their slot numbering to exist.
            int nextInterfaceId = 0;
            foreach (var contract in module.Interfaces)
                LinkInterface(contract, ref nextInterfaceId);

            foreach (var type in module.Classes)
                LinkClass(type, ref nextInterfaceId);

            module.Chunk.MarkBuilt();
            module.MarkBuilt();
        }

        /// <summary>Links a single interface and everything it extends.</summary>
        internal static void LinkInterface(SurtrInterface contract, ref int nextInterfaceId)
        {
            if (contract.IsBuilt)
                return;

            contract.BeginLinking();

            // ---- Extended interfaces, transitively closed --------------------------------
            var closure = new List<SurtrInterface>();
            foreach (var handle in contract.DeclaredExtendedInterfaces)
            {
                var extended = ResolveInterface(handle, contract.Name);
                LinkInterface(extended, ref nextInterfaceId);

                AddDistinct(closure, extended);
                for (int i = 0; i < extended.ExtendedInterfaces.Length; i++)
                    AddDistinct(closure, extended.ExtendedInterfaces[i]);
            }

            contract.ExtendedInterfaces = closure.ToArray();

            // ---- Method slots: inherited numbering first, then this interface's own ------
            var slots = new List<SurtrMethodInfo>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);

            for (int i = 0; i < contract.ExtendedInterfaces.Length; i++)
            {
                var extendedSlots = contract.ExtendedInterfaces[i].MethodSlots;
                for (int s = 0; s < extendedSlots.Length; s++)
                    TryAppendSlot(slots, seen, extendedSlots[s]);
            }

            foreach (var overloads in contract.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                    TryAppendSlot(slots, seen, overloads[i]);
            }

            contract.MethodSlots = slots.ToArray();
            contract.InterfaceId = nextInterfaceId++;

            foreach (var overloads in contract.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                    overloads[i].MarkBuilt();
            }

            foreach (var property in contract.Properties)
                property.MarkBuilt();

            contract.MarkBuilt();
        }

        /// <summary>Links a single class, pulling in its base class and interfaces first.</summary>
        internal static void LinkClass(SurtrClass type, ref int nextInterfaceId)
        {
            if (type.IsBuilt)
                return;

            type.BeginLinking();

            SurtrClass? baseType = ResolveBase(type);
            if (baseType is not null)
                LinkClass(baseType, ref nextInterfaceId);

            BuildAncestors(type, baseType);
            BuildInterfaceClosure(type, baseType, ref nextInterfaceId);
            BuildFieldLayout(type, baseType);
            BuildMethodTables(type, baseType);
            BuildInterfaceDispatch(type);
            VerifyConcrete(type);

            // Nested types are independent hierarchies; link them so the whole module ends up
            // frozen in one pass.
            foreach (var nested in type.NestedInterfaces)
                LinkInterface(nested, ref nextInterfaceId);

            foreach (var nested in type.NestedClasses)
                LinkClass(nested, ref nextInterfaceId);

            type.MarkBuilt();
        }
        #endregion

        #region Ancestors
        private static void BuildAncestors(SurtrClass type, SurtrClass? baseType)
        {
            if (baseType is null)
            {
                type.Ancestors = new[] { type };
                type.Depth = 0;
                return;
            }

            // Copy the base chain and append this class, so Ancestors[Depth] == this and any
            // ancestor sits at its own depth. That is what makes IsSubclassOf a single load.
            var baseAncestors = baseType.Ancestors;
            var ancestors = new SurtrClass[baseAncestors.Length + 1];
            Array.Copy(baseAncestors, ancestors, baseAncestors.Length);
            ancestors[baseAncestors.Length] = type;

            type.Ancestors = ancestors;
            type.Depth = baseAncestors.Length;
        }
        #endregion

        #region Interfaces
        private static void BuildInterfaceClosure(SurtrClass type, SurtrClass? baseType, ref int nextInterfaceId)
        {
            var closure = new List<SurtrInterface>();

            // Inherited first, so a base-typed itable index stays valid on a derived instance.
            if (baseType is not null)
            {
                var inherited = baseType.Interfaces;
                for (int i = 0; i < inherited.Length; i++)
                    AddDistinct(closure, inherited[i]);
            }

            foreach (var handle in type.DeclaredInterfaces)
            {
                var contract = ResolveInterface(handle, type.Name);
                LinkInterface(contract, ref nextInterfaceId);

                AddDistinct(closure, contract);
                for (int i = 0; i < contract.ExtendedInterfaces.Length; i++)
                    AddDistinct(closure, contract.ExtendedInterfaces[i]);
            }

            type.Interfaces = closure.ToArray();
        }
        #endregion

        #region Fields
        private static void BuildFieldLayout(SurtrClass type, SurtrClass? baseType)
        {
            var instanceFields = new List<SurtrFieldInfo>();

            // Inherited fields keep the slots the base gave them, so an access compiled against
            // the base type keeps working on a derived instance.
            if (baseType is not null)
                instanceFields.AddRange(baseType.InstanceFields);

            var staticFields = new List<SurtrFieldInfo>();

            foreach (var field in type.Fields)
            {
                if (field.IsStatic)
                {
                    field.SlotIndex = staticFields.Count;
                    staticFields.Add(field);
                }
                else
                {
                    field.SlotIndex = instanceFields.Count;
                    instanceFields.Add(field);
                }

                field.MarkBuilt();
            }

            type.InstanceFields = instanceFields.ToArray();
            type.StaticFields = staticFields.ToArray();
            type.InstanceSlotCount = instanceFields.Count;

            BuildReferenceSlots(type, instanceFields);

            type.StaticStorage.Dispose();
            type.StaticStorage = new SurtrNativeArray<SurtrRawValue>(staticFields.Count, zeroed: true);
        }

        private static void BuildReferenceSlots(SurtrClass type, List<SurtrFieldInfo> instanceFields)
        {
            // Which slots the collector has to follow. Derived from the declared types, which the
            // compiler already knows, rather than from the tags it could test at run time.
            int referenceCount = 0;
            for (int i = 0; i < instanceFields.Count; i++)
            {
                if (instanceFields[i].FieldType.Reference.TypeCode.IsReferenceType)
                    referenceCount++;
            }

            type.ReferenceSlots.Dispose();
            var slots = new SurtrNativeArray<int>(referenceCount);

            int next = 0;
            for (int i = 0; i < instanceFields.Count; i++)
            {
                var field = instanceFields[i];
                if (field.FieldType.Reference.TypeCode.IsReferenceType)
                    slots[next++] = field.SlotIndex;
            }

            type.ReferenceSlots = slots;
        }
        #endregion

        #region Methods
        private static void BuildMethodTables(SurtrClass type, SurtrClass? baseType)
        {
            var virtualMethods = new List<SurtrMethodInfo>();
            var slotsBySignature = new Dictionary<string, int>(StringComparer.Ordinal);

            // Start from the base vtable verbatim: an inherited slot must keep its index, since
            // call sites compiled against the base already reference it by number.
            if (baseType is not null)
            {
                var inherited = baseType.VirtualMethods;
                for (int i = 0; i < inherited.Length; i++)
                {
                    virtualMethods.Add(inherited[i]);
                    slotsBySignature[SignatureKey(inherited[i])] = i;
                }
            }

            var directMethods = new List<SurtrMethodInfo>();
            var staticMethods = new List<SurtrMethodInfo>();
            var constructors = new List<SurtrMethodInfo>();

            foreach (var overloads in type.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                {
                    var method = overloads[i];

                    switch (method.Role)
                    {
                        case SurtrMethodRole.Constructor:
                            constructors.Add(method);
                            method.MarkBuilt();
                            continue;

                        case SurtrMethodRole.StaticInitializer:
                            staticMethods.Add(method);
                            method.MarkBuilt();
                            continue;
                    }

                    if (method.IsStatic)
                    {
                        staticMethods.Add(method);
                    }
                    else if (!method.IsVirtualDispatch)
                    {
                        directMethods.Add(method);
                    }
                    else
                    {
                        PlaceInVTable(type, method, virtualMethods, slotsBySignature);
                    }

                    method.MarkBuilt();
                }
            }

            foreach (var property in type.Properties)
                property.MarkBuilt();

            type.VirtualMethods = virtualMethods.ToArray();
            type.DirectMethods = directMethods.ToArray();
            type.StaticMethods = staticMethods.ToArray();
            type.Constructors = constructors.ToArray();
        }

        private static void PlaceInVTable(
            SurtrClass type,
            SurtrMethodInfo method,
            List<SurtrMethodInfo> virtualMethods,
            Dictionary<string, int> slotsBySignature)
        {
            string key = SignatureKey(method);

            if (method.IsOverride)
            {
                if (!slotsBySignature.TryGetValue(key, out int slot))
                    throw new InvalidOperationException(
                        $"'{type.Name}.{method.Name}' is marked as an override but no base method matches its signature.");

                // Replacing in place is the whole point: every existing call site, and every
                // interface entry routed through this slot, picks up the override for free.
                virtualMethods[slot] = method;
                method.VTableSlot = slot;
                return;
            }

            if (slotsBySignature.ContainsKey(key))
                throw new InvalidOperationException(
                    $"'{type.Name}.{method.Name}' hides an inherited virtual method with the same signature; mark it as an override.");

            int newSlot = virtualMethods.Count;
            virtualMethods.Add(method);
            slotsBySignature[key] = newSlot;
            method.VTableSlot = newSlot;
        }
        #endregion

        #region Interface Dispatch
        private static void BuildInterfaceDispatch(SurtrClass type)
        {
            var interfaces = type.Interfaces;

            // Index every virtual method once, then answer each interface slot from that map
            // rather than rescanning the vtable per contract entry.
            var implementations = new Dictionary<string, int>(StringComparer.Ordinal);
            var virtualMethods = type.VirtualMethods;
            for (int i = 0; i < virtualMethods.Length; i++)
                implementations[SignatureKey(virtualMethods[i])] = i;

            int totalSlots = 0;
            for (int i = 0; i < interfaces.Length; i++)
                totalSlots += interfaces[i].MethodSlots.Length;

            type.InterfaceSlotOffsets.Dispose();
            type.InterfaceMethodSlots.Dispose();

            var offsets = new SurtrNativeArray<int>(interfaces.Length);
            var methodSlots = new SurtrNativeArray<int>(totalSlots);

            int next = 0;
            for (int i = 0; i < interfaces.Length; i++)
            {
                offsets[i] = next;

                var contract = interfaces[i];
                var contractSlots = contract.MethodSlots;

                for (int s = 0; s < contractSlots.Length; s++)
                {
                    string key = SignatureKey(contractSlots[s]);
                    if (!implementations.TryGetValue(key, out int vtableSlot))
                        throw new InvalidOperationException(
                            $"'{type.Name}' does not implement '{contract.Name}.{contractSlots[s].Name}'.");

                    // Store the vtable index, not the method: an override later in the hierarchy
                    // replaces the vtable entry and every interface routed here follows along.
                    methodSlots[next++] = vtableSlot;
                }
            }

            type.InterfaceSlotOffsets = offsets;
            type.InterfaceMethodSlots = methodSlots;
        }

        private static void VerifyConcrete(SurtrClass type)
        {
            if (type.IsAbstract)
                return;

            var virtualMethods = type.VirtualMethods;
            for (int i = 0; i < virtualMethods.Length; i++)
            {
                if (virtualMethods[i].ImplKind == SurtrMethodImplKind.Abstract)
                    throw new InvalidOperationException(
                        $"'{type.Name}' is not abstract but leaves '{virtualMethods[i].Name}' unimplemented.");
            }
        }
        #endregion

        #region Helpers
        /// <summary>
        /// Builds the key two methods must share to be considered the same slot: name plus
        /// parameter types.
        /// </summary>
        /// <remarks>
        /// The return type is deliberately excluded. Overriding a method may not change what it
        /// is called with, but allowing a narrower return type later only needs this key to stay
        /// stable, and including it would break that.
        /// </remarks>
        private static string SignatureKey(SurtrMethodInfo method)
        {
            var builder = new StringBuilder(method.Name);
            builder.Append('(');

            var parameters = method.Parameters;
            for (int i = 0; i < parameters.Length; i++)
                builder.Append(parameters[i].ParameterType.Reference.Descriptor);

            builder.Append(')');
            return builder.ToString();
        }

        private static void TryAppendSlot(List<SurtrMethodInfo> slots, Dictionary<string, int> seen, SurtrMethodInfo method)
        {
            string key = SignatureKey(method);
            if (seen.ContainsKey(key))
                return;

            seen[key] = slots.Count;
            slots.Add(method);
        }

        private static void AddDistinct(List<SurtrInterface> closure, SurtrInterface contract)
        {
            for (int i = 0; i < closure.Count; i++)
            {
                if (ReferenceEquals(closure[i], contract))
                    return;
            }

            closure.Add(contract);
        }

        private static SurtrClass? ResolveBase(SurtrClass type)
        {
            var handle = type.BaseType;
            if (handle is null)
                return null;

            if (!handle.IsResolved)
                throw new InvalidOperationException(
                    $"Base type '{handle.Reference.Descriptor}' of '{type.Name}' was not resolved before linking.");

            return handle.ResolvedClass
                ?? throw new InvalidOperationException(
                    $"'{type.Name}' cannot extend '{handle.Reference.Descriptor}', which is an interface.");
        }

        private static SurtrInterface ResolveInterface(SurtrTypeHandle handle, string dependentName)
        {
            if (!handle.IsResolved)
                throw new InvalidOperationException(
                    $"Interface '{handle.Reference.Descriptor}' of '{dependentName}' was not resolved before linking.");

            return handle.ResolvedInterface
                ?? throw new InvalidOperationException(
                    $"'{dependentName}' cannot implement '{handle.Reference.Descriptor}', which is a class.");
        }
        #endregion
    }
}
