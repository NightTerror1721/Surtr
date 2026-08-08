#nullable enable

using Surtr.Runtime.Objects;
using Surtr.Runtime.Utilities;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// The runtime metadata for a Surtr class: the resolved counterpart of a
    /// <see cref="SurtrClassReference"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A class is itself a member, because every class is declared inside either a module or
    /// another class - Surtr has no free-floating types. It extends at most one class and
    /// implements any number of interfaces.
    /// </para>
    /// <para>
    /// The name-keyed dictionaries below are the compiler's view: they answer "what is called
    /// <c>x</c>". The <c>internal</c> tables are the runtime's view: flattened arrays the
    /// interpreter indexes with a small integer, built once by the loader. Nothing on the
    /// execution path should ever go through a name.
    /// </para>
    /// </remarks>
    public sealed class SurtrClass : SurtrTypeInfo, IDisposable
    {
        private readonly SurtrValueTypeCode _typeCode;
        private readonly SurtrTypeHandle? _baseType;
        private readonly bool _abstract;

        private readonly Dictionary<string, SurtrFieldInfo> _fields;
        private readonly Dictionary<string, SurtrPropertyInfo> _properties;
        private readonly Dictionary<string, SurtrMethodInfo[]> _methods;
        private readonly Dictionary<string, SurtrClass> _nestedClasses;
        private readonly Dictionary<string, SurtrInterface> _nestedInterfaces;

        /// <summary>The interfaces this class declares, by name, before the loader resolves them.</summary>
        internal SurtrTypeHandle[] DeclaredInterfaces;

        private bool _disposed;

        #region Runtime Access Tables
        // Every table here is flattened at load time: it already contains whatever was inherited,
        // so a lookup never walks the hierarchy. Managed metadata has to live in ordinary arrays;
        // the pure-integer and raw-value tables use SurtrNativeArray so the interpreter can index
        // unmanaged memory directly.

        /// <summary>
        /// This class's ancestor chain, indexed by depth: <c>Ancestors[0]</c> is the root and
        /// <c>Ancestors[Depth]</c> is this class. Turns a subtype test into a compare and one
        /// load - see <see cref="IsSubclassOf"/>.
        /// </summary>
        internal SurtrClass[] Ancestors;

        /// <summary>How many classes sit above this one; also this class's index into <see cref="Ancestors"/>.</summary>
        internal int Depth;

        /// <summary>
        /// Every interface this class satisfies, transitively closed: interfaces of interfaces
        /// and interfaces inherited from ancestors are already folded in.
        /// </summary>
        internal SurtrInterface[] Interfaces;

        /// <summary>
        /// Instance field layout, indexed by <see cref="SurtrFieldInfo.Slot"/>. Inherited fields
        /// come first and keep their base-class slots, so a base-typed access works unchanged on
        /// a derived instance.
        /// </summary>
        internal SurtrFieldInfo[] InstanceFields;

        /// <summary>How many slots an instance of this class occupies. What the allocator needs.</summary>
        internal int InstanceSlotCount;

        /// <summary>
        /// Which instance slots hold a <see cref="SurtrRef"/>, in ascending order.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Purely an optimization, not a correctness requirement: values are NaN-boxed, so the
        /// collector could recover the same answer by testing each slot's tag. This table exists
        /// because the language is statically typed and the compiler already knows which fields
        /// are references, so re-deriving it at run time is wasted work. Tracing walks the k
        /// reference slots instead of all n slots, with no per-slot branch to mispredict.
        /// </para>
        /// <para>
        /// It also sidesteps NaN aliasing. A raw double whose bits happen to land in the tag
        /// range would read as a reference; ordinary arithmetic yields quiet NaNs outside it, but
        /// bits arriving from the host or from bit manipulation are not bounded by that. A table
        /// derived from static types cannot be fooled that way.
        /// </para>
        /// </remarks>
        internal SurtrNativeArray<int> ReferenceSlots;

        /// <summary>Static field layout, indexed by <see cref="SurtrFieldInfo.Slot"/>. Not inherited - each class owns its own statics.</summary>
        internal SurtrFieldInfo[] StaticFields;

        /// <summary>Backing storage for this class's static fields, indexed by the same slots as <see cref="StaticFields"/>.</summary>
        internal SurtrNativeArray<SurtrRawValue> StaticStorage;

        /// <summary>
        /// Which slots of <see cref="StaticStorage"/> hold a reference, in ascending order.
        /// </summary>
        /// <remarks>
        /// The static counterpart of <see cref="ReferenceSlots"/>, and it exists for a harder
        /// reason: static storage is unmanaged and reachable from no object, so unless a collection
        /// walks it explicitly, anything a static field is the sole owner of would be swept.
        /// </remarks>
        internal SurtrNativeArray<int> ReferenceStaticSlots;

        /// <summary>
        /// This class's parameterless static initializer, or <see langword="null"/> if it declares
        /// none. Run once when the declaring module is loaded.
        /// </summary>
        internal SurtrMethodInfo? StaticInitializer;

        /// <summary>
        /// Virtual method table, indexed by <see cref="SurtrMethodInfo.VTableSlot"/>. Inherited
        /// slots keep their index and an override replaces the entry in place, which is what
        /// makes a virtual call one load plus an indirect jump.
        /// </summary>
        internal SurtrMethodInfo[] VirtualMethods;

        /// <summary>Non-virtual instance methods, bound directly at compile time.</summary>
        internal SurtrMethodInfo[] DirectMethods;

        /// <summary>Static methods declared on this class.</summary>
        internal SurtrMethodInfo[] StaticMethods;

        /// <summary>
        /// Constructors declared on this class. Never virtual and never inherited, so they get
        /// their own table rather than sharing the static one.
        /// </summary>
        internal SurtrMethodInfo[] Constructors;

        /// <summary>
        /// Where each interface's block starts inside <see cref="InterfaceMethodSlots"/>, aligned
        /// with <see cref="Interfaces"/>.
        /// </summary>
        internal SurtrNativeArray<int> InterfaceSlotOffsets;

        /// <summary>
        /// The interface dispatch table, flattened: for interface <c>i</c> and interface method
        /// slot <c>m</c>, <c>InterfaceMethodSlots[InterfaceSlotOffsets[i] + m]</c> is the index
        /// into <see cref="VirtualMethods"/> that implements it.
        /// </summary>
        /// <remarks>
        /// Storing vtable indices rather than method references keeps this a flat block of ints
        /// in unmanaged memory, and means an override automatically applies to every interface
        /// that routes through the same slot.
        /// </remarks>
        internal SurtrNativeArray<int> InterfaceMethodSlots;
        #endregion

        /// <summary>Creates class metadata with empty tables, to be filled in by the loader.</summary>
        public SurtrClass(
            string name,
            SurtrValueTypeCode typeCode,
            SurtrClassReference selfReference,
            SurtrTypeHandle? baseType,
            bool isAbstract,
            SurtrVisibility visibility,
            SurtrTypeHandle? declaringType)
            : base(name, SurtrMemberKind.Class, selfReference, visibility, declaringType)
        {
            _typeCode = typeCode;
            _baseType = baseType;
            _abstract = isAbstract;

            _fields = new Dictionary<string, SurtrFieldInfo>(StringComparer.Ordinal);
            _properties = new Dictionary<string, SurtrPropertyInfo>(StringComparer.Ordinal);
            _methods = new Dictionary<string, SurtrMethodInfo[]>(StringComparer.Ordinal);
            _nestedClasses = new Dictionary<string, SurtrClass>(StringComparer.Ordinal);
            _nestedInterfaces = new Dictionary<string, SurtrInterface>(StringComparer.Ordinal);

            // Zeroed here rather than left implicit: the loader overwrites both once it has
            // walked the hierarchy and laid the instance out.
            Depth = 0;
            InstanceSlotCount = 0;

            DeclaredInterfaces = Array.Empty<SurtrTypeHandle>();
            Ancestors = Array.Empty<SurtrClass>();
            Interfaces = Array.Empty<SurtrInterface>();
            InstanceFields = Array.Empty<SurtrFieldInfo>();
            StaticFields = Array.Empty<SurtrFieldInfo>();
            VirtualMethods = Array.Empty<SurtrMethodInfo>();
            DirectMethods = Array.Empty<SurtrMethodInfo>();
            StaticMethods = Array.Empty<SurtrMethodInfo>();
            Constructors = Array.Empty<SurtrMethodInfo>();
        }

        /// <summary>Which <see cref="SurtrValueTypeCode"/> family this class belongs to.</summary>
        public SurtrValueTypeCode TypeCode
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _typeCode;
        }

        /// <summary>The class this one derives from, or <see langword="null"/> if it has no base.</summary>
        public SurtrTypeHandle? BaseType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _baseType;
        }

        /// <summary>Whether this class cannot be instantiated directly.</summary>
        public bool IsAbstract
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _abstract;
        }

        #region Runtime Queries
        /// <summary>
        /// Whether this class is <paramref name="other"/> or derives from it.
        /// </summary>
        /// <remarks>
        /// Constant time regardless of hierarchy depth: if <paramref name="other"/> really is an
        /// ancestor then it sits at its own depth in this class's chain, so one bounds compare
        /// and one load settle it. No loop, no walking base pointers.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsSubclassOf(SurtrClass other)
        {
            int otherDepth = other.Depth;
            return otherDepth <= Depth && ReferenceEquals(Ancestors[otherDepth], other);
        }

        /// <summary>Whether this class satisfies <paramref name="contract"/>.</summary>
        public bool Implements(SurtrInterface contract)
        {
            var interfaces = Interfaces;
            for (int i = 0; i < interfaces.Length; i++)
            {
                if (ReferenceEquals(interfaces[i], contract))
                    return true;
            }

            return false;
        }

        /// <summary>Finds an interface's position in this class's dispatch table.</summary>
        /// <returns>The index into <see cref="Interfaces"/>, or <c>-1</c> if not implemented.</returns>
        internal int IndexOfInterface(SurtrInterface contract)
        {
            var interfaces = Interfaces;
            for (int i = 0; i < interfaces.Length; i++)
            {
                if (ReferenceEquals(interfaces[i], contract))
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Resolves an interface call to the method that implements it, given the interface's
        /// index from <see cref="IndexOfInterface"/> and a slot from the interface's contract.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SurtrMethodInfo GetInterfaceMethod(int interfaceIndex, int interfaceSlot)
            => VirtualMethods[InterfaceMethodSlots[InterfaceSlotOffsets[interfaceIndex] + interfaceSlot]];

        /// <summary>Resolves a virtual call to the method currently occupying a vtable slot.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SurtrMethodInfo GetVirtualMethod(int slot) => VirtualMethods[slot];
        #endregion

        #region Name Lookup
        // Concrete TryGet/value-collection members instead of IReadOnlyDictionary. An
        // interface-typed lookup costs a dispatch the JIT can't devirtualize, and foreach over
        // the interface boxes the struct enumerator; going through the concrete Dictionary keeps
        // both free. These are for the compiler and tooling - the runtime uses the tables above.

        /// <summary>Looks up a field declared directly on this class.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetField(string name, out SurtrFieldInfo field) => _fields.TryGetValue(name, out field!);

        /// <summary>Looks up a property declared directly on this class.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetProperty(string name, out SurtrPropertyInfo property) => _properties.TryGetValue(name, out property!);

        /// <summary>Looks up the overload group declared directly on this class under a name.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetMethods(string name, out SurtrMethodInfo[] overloads) => _methods.TryGetValue(name, out overloads!);

        /// <summary>Looks up a class or enum declared directly inside this class.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetNestedClass(string name, out SurtrClass nestedClass) => _nestedClasses.TryGetValue(name, out nestedClass!);

        /// <summary>Looks up an interface declared directly inside this class.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetNestedInterface(string name, out SurtrInterface nestedInterface) => _nestedInterfaces.TryGetValue(name, out nestedInterface!);

        /// <summary>The fields declared directly on this class.</summary>
        public Dictionary<string, SurtrFieldInfo>.ValueCollection Fields => _fields.Values;

        /// <summary>The properties declared directly on this class.</summary>
        public Dictionary<string, SurtrPropertyInfo>.ValueCollection Properties => _properties.Values;

        /// <summary>The overload groups declared directly on this class.</summary>
        public Dictionary<string, SurtrMethodInfo[]>.ValueCollection Methods => _methods.Values;

        /// <summary>The classes and enums declared directly inside this class.</summary>
        public Dictionary<string, SurtrClass>.ValueCollection NestedClasses => _nestedClasses.Values;

        /// <summary>The interfaces declared directly inside this class.</summary>
        public Dictionary<string, SurtrInterface>.ValueCollection NestedInterfaces => _nestedInterfaces.Values;
        #endregion

        #region Declaration
        // Everything here mutates, so everything here is only legal while the class is under
        // construction. Once the linker has flattened the tables, letting a member appear would
        // silently invalidate every slot index already handed out.

        /// <summary>Declares a field on this class.</summary>
        /// <exception cref="InvalidOperationException">The class is already built.</exception>
        public void AddField(SurtrFieldInfo field)
        {
            ThrowIfBuilt();
            _fields.Add(field.Name, field);
        }

        /// <summary>Declares a property on this class.</summary>
        /// <exception cref="InvalidOperationException">The class is already built.</exception>
        public void AddProperty(SurtrPropertyInfo property)
        {
            ThrowIfBuilt();
            _properties.Add(property.Name, property);
        }

        /// <summary>Declares a method on this class, joining any existing overload group of the same name.</summary>
        /// <exception cref="InvalidOperationException">The class is already built.</exception>
        public void AddMethod(SurtrMethodInfo method)
        {
            ThrowIfBuilt();

            if (_methods.TryGetValue(method.Name, out var overloads))
            {
                Array.Resize(ref overloads, overloads.Length + 1);
                overloads[^1] = method;
                _methods[method.Name] = overloads;
            }
            else
            {
                _methods.Add(method.Name, new[] { method });
            }
        }

        /// <summary>Declares a nested class or enum.</summary>
        /// <exception cref="InvalidOperationException">The class is already built.</exception>
        public void AddNestedClass(SurtrClass nestedClass)
        {
            ThrowIfBuilt();
            _nestedClasses.Add(nestedClass.Name, nestedClass);
        }

        /// <summary>Declares a nested interface.</summary>
        /// <exception cref="InvalidOperationException">The class is already built.</exception>
        public void AddNestedInterface(SurtrInterface nestedInterface)
        {
            ThrowIfBuilt();
            _nestedInterfaces.Add(nestedInterface.Name, nestedInterface);
        }

        /// <summary>Declares the interfaces this class implements, by unresolved handle.</summary>
        /// <exception cref="InvalidOperationException">The class is already built.</exception>
        public void SetDeclaredInterfaces(SurtrTypeHandle[] interfaces)
        {
            ThrowIfBuilt();
            DeclaredInterfaces = interfaces;
        }
        #endregion

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

            foreach (var nested in _nestedInterfaces.Values)
                marker.Mark(nested);
        }

        /// <summary>Releases the unmanaged tables and static storage this class owns.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            ReferenceSlots.Dispose();
            StaticStorage.Dispose();
            ReferenceStaticSlots.Dispose();
            InterfaceSlotOffsets.Dispose();
            InterfaceMethodSlots.Dispose();

            foreach (var nested in _nestedClasses.Values)
                nested.Dispose();

            _disposed = true;
        }
    }
}
