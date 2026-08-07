#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>Where a method's body actually lives.</summary>
    public enum SurtrMethodImplKind : byte
    {
        /// <summary>Compiled Surtr bytecode, executed by the VM.</summary>
        Bytecode = 0,

        /// <summary>A host function reached through a native entry point.</summary>
        Native = 1,

        /// <summary>No body at all - a contract to be satisfied by a derived class.</summary>
        Abstract = 2,
    }

    /// <summary>
    /// How a call to a method is resolved.
    /// </summary>
    /// <remarks>
    /// Orthogonal to <see cref="SurtrMethodImplKind"/>, which says where the body lives: a
    /// <see cref="Virtual"/> method still has a body, it is just reached through the vtable.
    /// The one dependency between them is that <see cref="Abstract"/> dispatch always pairs with
    /// <see cref="SurtrMethodImplKind.Abstract"/>, since there is nothing to run.
    /// </remarks>
    public enum SurtrMethodDispatch : byte
    {
        /// <summary>
        /// Bound at compile time and called directly. This is the default: a method is only
        /// virtual or abstract when it says so.
        /// </summary>
        Direct = 0,

        /// <summary>Has a body, but derived classes may replace it; reached through the vtable.</summary>
        Virtual = 1,

        /// <summary>Has no body and must be implemented by a derived class; reached through the vtable.</summary>
        Abstract = 2,
    }

    /// <summary>
    /// What part a method plays in its declaring type.
    /// </summary>
    /// <remarks>
    /// A field rather than a subclass on purpose. Subclassing already models one axis - where the
    /// body lives, per <see cref="SurtrMethodImplKind"/> - and a constructor can be written in
    /// bytecode or supplied natively just like any other method. Making the role a second
    /// subclass axis would multiply the two together into a bytecode-constructor and a
    /// native-constructor type that add no data of their own. Base-constructor chaining does not
    /// change that: it is a call instruction in the body, exactly as it is on the JVM, not extra
    /// metadata.
    /// </remarks>
    public enum SurtrMethodRole : byte
    {
        /// <summary>An ordinary method.</summary>
        Normal = 0,

        /// <summary>An instance constructor.</summary>
        Constructor = 1,

        /// <summary>The parameterless static initializer that fills a class's static storage.</summary>
        StaticInitializer = 2,
    }

    /// <summary>A single declared parameter of a <see cref="SurtrMethodInfo"/>.</summary>
    public readonly struct SurtrParameterInfo
    {
        private readonly string _name;
        private readonly SurtrTypeHandle _parameterType;

        /// <summary>Creates parameter metadata.</summary>
        public SurtrParameterInfo(string name, SurtrTypeHandle parameterType)
        {
            _name = name;
            _parameterType = parameterType;
        }

        /// <summary>The parameter's declared name.</summary>
        public string Name
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _name;
        }

        /// <summary>The parameter's declared type.</summary>
        public SurtrTypeHandle ParameterType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _parameterType;
        }
    }

    /// <summary>
    /// Metadata shared by every method declared in a module or a class, regardless of whether it
    /// is implemented in bytecode or natively.
    /// </summary>
    /// <remarks>
    /// Bytecode and native methods live side by side in the same class: both are
    /// <see cref="SurtrMethodInfo"/>, so member tables, overload groups and property accessors
    /// hold them interchangeably and only the interpreter's dispatch switch cares about
    /// <see cref="ImplKind"/>.
    /// </remarks>
    public abstract class SurtrMethodInfo : SurtrMemberInfo
    {
        private readonly SurtrTypeHandle _returnType;
        private readonly SurtrParameterInfo[] _parameters;
        private readonly SurtrMethodImplKind _implKind;
        private readonly SurtrMethodDispatch _dispatch;
        private readonly SurtrMethodRole _role;
        private readonly bool _override;
        private SurtrClassReference _signature;

        /// <summary>
        /// This method's index in its declaring class's virtual method table, or
        /// <c>-1</c> when <see cref="Dispatch"/> is <see cref="SurtrMethodDispatch.Direct"/>.
        /// Assigned by the loader while it builds the vtable.
        /// </summary>
        internal int VTableSlot = -1;

        private protected SurtrMethodInfo(
            string name,
            SurtrMethodImplKind implKind,
            SurtrMethodDispatch dispatch,
            SurtrMethodRole role,
            bool isOverride,
            SurtrTypeHandle returnType,
            SurtrParameterInfo[] parameters,
            bool isStatic,
            SurtrVisibility visibility,
            SurtrTypeHandle? declaringType)
            : base(name, SurtrMemberKind.Method, isStatic, visibility, declaringType)
        {
            // Constructors are never inherited, so they can never be dispatched through a vtable.
            // Catching it here keeps the loader from ever having to consider the combination.
            if (role != SurtrMethodRole.Normal && dispatch != SurtrMethodDispatch.Direct)
                throw new ArgumentException($"Method '{name}' is a {role} and cannot be virtual or abstract.", nameof(dispatch));

            if (role == SurtrMethodRole.Constructor && isStatic)
                throw new ArgumentException($"Constructor '{name}' cannot be static.", nameof(isStatic));

            if (role == SurtrMethodRole.StaticInitializer && (!isStatic || parameters.Length != 0))
                throw new ArgumentException($"Static initializer '{name}' must be static and take no parameters.", nameof(role));

            _implKind = implKind;
            _dispatch = dispatch;
            _role = role;
            _override = isOverride;
            _returnType = returnType;
            _parameters = parameters;
        }

        /// <summary>
        /// Where this method's body lives.
        /// </summary>
        /// <remarks>
        /// A field rather than an abstract property: the interpreter reads this on every
        /// dispatch, so it has to be a load, not a virtual call.
        /// </remarks>
        public SurtrMethodImplKind ImplKind
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _implKind;
        }

        /// <summary>How a call to this method is resolved. Defaults to <see cref="SurtrMethodDispatch.Direct"/>.</summary>
        public SurtrMethodDispatch Dispatch
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _dispatch;
        }

        /// <summary>What part this method plays in its declaring type. Defaults to <see cref="SurtrMethodRole.Normal"/>.</summary>
        public SurtrMethodRole Role
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _role;
        }

        /// <summary>Whether this method is an instance constructor.</summary>
        public bool IsConstructor
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _role == SurtrMethodRole.Constructor;
        }

        /// <summary>Whether this method replaces a virtual or abstract one inherited from a base class.</summary>
        public bool IsOverride
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _override;
        }

        /// <summary>Whether calls to this method go through the vtable rather than being bound directly.</summary>
        public bool IsVirtualDispatch
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _dispatch != SurtrMethodDispatch.Direct;
        }

        /// <summary>Whether this method has no body and must be implemented by a derived class.</summary>
        public bool IsAbstract
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _dispatch == SurtrMethodDispatch.Abstract;
        }

        /// <summary>The method's declared return type.</summary>
        public SurtrTypeHandle ReturnType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _returnType;
        }

        /// <summary>The method's declared parameters, in order.</summary>
        public ReadOnlySpan<SurtrParameterInfo> Parameters
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _parameters;
        }

        /// <summary>How many parameters the method declares.</summary>
        public int ParameterCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _parameters.Length;
        }

        /// <summary>
        /// The method's signature expressed as a closure descriptor, so overloads can be told
        /// apart by a single string comparison.
        /// </summary>
        /// <remarks>
        /// Built once on first use and cached: the descriptor never changes, and constructing it
        /// costs an array plus a <c>StringBuilder</c>. Racing callers may each build one, but
        /// they all produce the same text, so the worst case is a discarded duplicate.
        /// </remarks>
        public SurtrClassReference ToSignature()
        {
            if (_signature.IsValid)
                return _signature;

            return _signature = BuildSignature();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private SurtrClassReference BuildSignature()
        {
            var parameterTypes = new SurtrClassReference[_parameters.Length];
            for (int i = 0; i < _parameters.Length; i++)
                parameterTypes[i] = _parameters[i].ParameterType.Reference;

            return SurtrClassReference.Closure(_returnType.Reference, parameterTypes);
        }
    }
}
