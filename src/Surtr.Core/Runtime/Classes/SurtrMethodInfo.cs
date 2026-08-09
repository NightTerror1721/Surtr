#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Text;

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
    /// <remarks>
    /// Carries everything a call site needs to be checked against a member declared in another
    /// module: the name, so a named argument can find it; a default, so an omitted trailing
    /// argument can be filled in; and whether it is the varargs parameter, so a surplus of
    /// arguments can be absorbed. Overload resolution (<c>Language-Syntax.md</c> §3.5) needs all
    /// three, and none of them reaches the interpreter - a call arrives with its arguments already
    /// filled in and its varargs array already packed.
    /// </remarks>
    public readonly struct SurtrParameterInfo
    {
        private readonly string _name;
        private readonly SurtrTypeHandle _parameterType;
        private readonly SurtrConstant _defaultValue;
        private readonly bool _varargs;

        /// <summary>Creates metadata for an ordinary required parameter.</summary>
        public SurtrParameterInfo(string name, SurtrTypeHandle parameterType)
        {
            _name = name;
            _parameterType = parameterType;
            _defaultValue = SurtrConstant.None;
            _varargs = false;
        }

        /// <summary>Creates parameter metadata carrying a default value, a varargs mark, or both.</summary>
        public SurtrParameterInfo(string name, SurtrTypeHandle parameterType, SurtrConstant defaultValue, bool isVarargs = false)
        {
            if (isVarargs && defaultValue.HasValue)
                throw new ArgumentException($"Varargs parameter '{name}' cannot also have a default value.", nameof(defaultValue));

            _name = name;
            _parameterType = parameterType;
            _defaultValue = defaultValue;
            _varargs = isVarargs;
        }

        /// <summary>The parameter's declared name.</summary>
        public string Name
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _name;
        }

        /// <summary>
        /// The parameter's declared type. For a varargs parameter this is the <em>element</em>
        /// type, matching the source spelling <c>args: string...</c>; the body sees an array of it.
        /// </summary>
        public SurtrTypeHandle ParameterType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _parameterType;
        }

        /// <summary>The value a call site uses when it omits this argument.</summary>
        public SurtrConstant DefaultValue
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _defaultValue;
        }

        /// <summary>Whether a call site may omit this argument.</summary>
        public bool HasDefault
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _defaultValue.HasValue;
        }

        /// <summary>Whether this parameter absorbs every surplus argument into an array.</summary>
        public bool IsVarargs
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _varargs;
        }

        /// <summary>
        /// Checks a whole parameter list against the three shape rules in
        /// <c>Language-Syntax.md</c> §3.5.
        /// </summary>
        /// <remarks>
        /// Enforced where the member is declared rather than where it is called, because a call
        /// site reading this metadata is entitled to assume the shape holds - overload resolution
        /// walks the list once and stops at the first default, which is only sound if defaults
        /// really are trailing.
        /// </remarks>
        /// <exception cref="ArgumentException">The list breaks one of the rules.</exception>
        internal static void Validate(SurtrParameterInfo[] parameters, string memberName)
        {
            bool seenDefault = false;

            for (int i = 0; i < parameters.Length; i++)
            {
                ref readonly SurtrParameterInfo parameter = ref parameters[i];

                if (parameter.IsVarargs)
                {
                    if (i != parameters.Length - 1)
                        throw new ArgumentException(
                            $"'{memberName}' declares varargs parameter '{parameter.Name}', which must be the last parameter.",
                            nameof(parameters));

                    // A varargs parameter is allowed to follow required ones and is the one
                    // parameter that may legally lack a default after a default has appeared -
                    // except that it cannot, because a positional call could never reach it.
                    if (seenDefault)
                        throw new ArgumentException(
                            $"'{memberName}' declares varargs parameter '{parameter.Name}' after a defaulted parameter; a positional call could never reach it.",
                            nameof(parameters));

                    continue;
                }

                if (parameter.HasDefault)
                    seenDefault = true;
                else if (seenDefault)
                    throw new ArgumentException(
                        $"'{memberName}' declares required parameter '{parameter.Name}' after a defaulted one; defaults must be trailing.",
                        nameof(parameters));
            }
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
        private readonly bool _sealed;
        private SurtrClassReference _signature;
        private string? _signatureKey;

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
            SurtrTypeHandle? declaringType,
            bool isSealed = false)
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

            // `sealed` says "nothing below may redefine this", which only means something about a
            // slot that already exists and is already reachable through a vtable. On a `virtual`
            // or `abstract` member it would contradict the modifier next to it, and on a direct
            // one it would say nothing - so `Language-Syntax.md` §3.3 makes it legal only
            // together with `override`, and that is checked here rather than left to the compiler.
            if (isSealed && !isOverride)
                throw new ArgumentException(
                    $"Method '{name}' is marked sealed without being an override; sealed is only legal on an override.",
                    nameof(isSealed));

            SurtrParameterInfo.Validate(parameters, name);

            _implKind = implKind;
            _dispatch = dispatch;
            _role = role;
            _override = isOverride;
            _sealed = isSealed;
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

        /// <summary>
        /// Whether this override closes its branch of the hierarchy: nothing below may override it
        /// again, so from here down the implementation is statically certain.
        /// </summary>
        public bool IsSealed
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _sealed;
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
        /// How many arguments a call site must supply: the leading run of parameters that have
        /// neither a default nor the varargs mark.
        /// </summary>
        /// <remarks>
        /// Derived rather than stored, but the walk stops at the first optional parameter because
        /// <see cref="SurtrParameterInfo.Validate"/> guarantees the rest are optional too.
        /// </remarks>
        public int RequiredParameterCount
        {
            get
            {
                var parameters = _parameters;
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parameters[i].HasDefault || parameters[i].IsVarargs)
                        return i;
                }

                return parameters.Length;
            }
        }

        /// <summary>Whether the last parameter absorbs a surplus of arguments into an array.</summary>
        public bool HasVarargs
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _parameters.Length != 0 && _parameters[^1].IsVarargs;
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

        /// <summary>
        /// The key two methods must share to occupy the same slot: the name, then every parameter
        /// descriptor.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The return type is deliberately excluded, which is what makes this a different question
        /// from <see cref="ToSignature"/> - that one describes the method's whole type, this one
        /// answers "is this the same member". Overriding may not change what a method is called
        /// with, but allowing a narrower return later only needs this key to stay stable, and
        /// including the return would break that.
        /// </para>
        /// <para>
        /// It is also why <c>Language-Syntax.md</c> §3.5 can say two members differing only in
        /// return type are an error rather than an overload: they produce the same key here.
        /// One method rather than two private copies, because the linker and the declaration-time
        /// duplicate check have to agree exactly or an illegal overload pair slips through.
        /// </para>
        /// </remarks>
        public string SignatureKey() => _signatureKey ??= BuildSignatureKey();

        /// <summary>
        /// Adds <paramref name="method"/> to an overload group, rejecting one that repeats a
        /// signature already in it.
        /// </summary>
        /// <remarks>
        /// Shared by class, interface and module declaration so all three enforce
        /// <c>Language-Syntax.md</c> §3.5's first rule identically. Declaration-time code, run once
        /// per member, so a linear scan over a group that is almost always one entry long is the
        /// right shape.
        /// </remarks>
        /// <exception cref="ArgumentException">The group already holds that signature.</exception>
        internal static SurtrMethodInfo[] AppendOverload(SurtrMethodInfo[]? overloads, SurtrMethodInfo method, string ownerName)
        {
            if (overloads is null)
                return new[] { method };

            string key = method.SignatureKey();
            for (int i = 0; i < overloads.Length; i++)
            {
                if (string.Equals(overloads[i].SignatureKey(), key, StringComparison.Ordinal))
                    throw new ArgumentException(
                        $"'{ownerName}' already declares a member with the signature '{key}'.",
                        nameof(method));
            }

            var grown = new SurtrMethodInfo[overloads.Length + 1];
            Array.Copy(overloads, grown, overloads.Length);
            grown[^1] = method;
            return grown;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private string BuildSignatureKey()
        {
            string name = Name;
            var builder = new StringBuilder(name.Length + 2 + (_parameters.Length * 4));
            builder.Append(name).Append('(');

            for (int i = 0; i < _parameters.Length; i++)
                builder.Append(_parameters[i].ParameterType.Reference.Descriptor);

            return builder.Append(')').ToString();
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
