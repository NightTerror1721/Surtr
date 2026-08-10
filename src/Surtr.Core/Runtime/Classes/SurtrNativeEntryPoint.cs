#nullable enable

using Surtr.Runtime.Objects;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// The one signature every native function linked into Surtr must have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Native functions are not bound by their Surtr-level signature - they all share this
    /// single shape and unpack <paramref name="arguments"/> themselves. That keeps exactly one
    /// function-pointer cast in the interpreter's call path no matter what a function's declared
    /// Surtr signature is.
    /// </para>
    /// <para>
    /// This is a <em>managed</em> signature, called through a managed-convention function
    /// pointer, so linking a host function costs nothing beyond a direct call - there is no
    /// reverse-P/Invoke stub and no GC mode transition. The trade is that only managed static
    /// methods can be linked; a raw pointer into a C/C++ library is not callable this way.
    /// </para>
    /// <para>
    /// Because the convention is managed, the parameter can be an ordinary managed type rather
    /// than an erased one, and it is: <see cref="SurtrCallArguments"/> carries the runtime itself
    /// alongside the raw argument block, rather than the callee turning a <c>void*</c> back into
    /// an object - which would cost a handle dereference and a type check on every call, and would
    /// let a stray pointer through with no way to catch it. Reading an argument through it is
    /// plain, safe C#: no <c>unsafe</c>, and no <c>AllowUnsafeBlocks</c> on the assembly that
    /// declares the function, even though the type wraps a raw pointer internally.
    /// </para>
    /// </remarks>
    /// <param name="arguments">
    /// The call's arguments, and the runtime the call is running inside. For an instance method,
    /// argument 0 is the receiver.
    /// </param>
    /// <returns>
    /// The call's return value. A function declared to return nothing still has to return
    /// something down this one signature; by convention that is <see cref="SurtrValue.Null"/>,
    /// which the caller discards.
    /// </returns>
    public delegate SurtrValue SurtrNativeFunction(SurtrCallArguments arguments);

    /// <summary>
    /// A callable host function address, however it was obtained.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hosts link functions two ways: by taking the address of a static method directly
    /// (<c>&amp;MyStatic</c>), or by handing over an ordinary delegate. Both collapse into the
    /// same <see cref="IntPtr"/> here, and <see cref="Invoke"/> issues an identical indirect call
    /// for either - the interpreter never branches on where a pointer came from, and there is no
    /// delegate dispatch on the hot path.
    /// </para>
    /// <para>
    /// Every pointer stored here uses the <em>managed</em> calling convention. Passing an
    /// unmanaged address - a <c>GetProcAddress</c> result, or anything from
    /// <c>Marshal.GetFunctionPointerForDelegate</c> - is undefined behaviour that the compiler
    /// cannot catch, since both are just an <see cref="IntPtr"/> at this boundary. It may even
    /// appear to work on x64 Windows before breaking on x86, ARM or IL2CPP.
    /// </para>
    /// </remarks>
    public readonly unsafe struct SurtrNativeEntryPoint
    {
        private readonly IntPtr _pointer;
        private readonly SurtrNativeFunction? _keepAlive;

        private SurtrNativeEntryPoint(IntPtr pointer, SurtrNativeFunction? keepAlive)
        {
            _pointer = pointer;
            _keepAlive = keepAlive;
        }

        /// <summary>The raw address the interpreter calls through.</summary>
        public IntPtr Pointer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _pointer;
        }

        /// <summary>Whether this entry point actually points at anything.</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _pointer != IntPtr.Zero;
        }

        /// <summary>
        /// The delegate this entry point was resolved from, retained so the runtime cannot
        /// reclaim the method it points at. <see langword="null"/> when the host supplied an
        /// address directly.
        /// </summary>
        public SurtrNativeFunction? KeepAlive => _keepAlive;

        /// <summary>Whether this entry point was resolved from a delegate rather than a direct address.</summary>
        public bool IsDelegateBacked => _keepAlive is not null;

        #region Factories
        /// <summary>
        /// Links a static method by address. This is the preferred way to register a host
        /// function: it is resolved at compile time, needs no reflection, and is safe under AOT
        /// backends like IL2CPP.
        /// </summary>
        /// <remarks>
        /// Taking an address is itself an unsafe operation, so a host registering this way needs
        /// <c>AllowUnsafeBlocks</c> on the registration code - but only there. The function bodies
        /// stay safe, since <see cref="SurtrNativeFunction"/> has no pointer in its signature.
        /// <see cref="FromDelegate"/> avoids even that, at the cost of reflection.
        /// </remarks>
        /// <example>
        /// <code>
        /// static SurtrValue Print(SurtrCallArguments args) { ... }
        /// var entry = SurtrNativeEntryPoint.FromFunctionPointer(&amp;Print);
        /// </code>
        /// </example>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrNativeEntryPoint FromFunctionPointer(
            delegate*<SurtrCallArguments, SurtrValue> functionPointer)
            => new((IntPtr)functionPointer, null);

        /// <summary>
        /// Links an address that is already a managed-convention pointer to a method matching
        /// <see cref="SurtrNativeFunction"/>. Prefer the typed overload, which the compiler can
        /// check; nothing here can validate this one.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SurtrNativeEntryPoint FromFunctionPointer(IntPtr functionPointer)
            => new(functionPointer, null);

        /// <summary>
        /// Links a delegate by resolving the static method behind it to a managed function
        /// pointer. A convenience over the typed <c>FromFunctionPointer</c> overload, useful when
        /// the host builds its registration table dynamically or wants to stay entirely clear of
        /// <c>unsafe</c>.
        /// </summary>
        /// <remarks>
        /// The delegate must wrap a single <see langword="static"/> method. An instance method or
        /// a capturing lambda is rejected: its function pointer expects a hidden receiver as the
        /// first argument, which would not line up with <see cref="SurtrNativeFunction"/> and
        /// would corrupt the stack rather than fail cleanly.
        /// <para>
        /// This path resolves the pointer through reflection, which AOT backends may strip. When
        /// targeting IL2CPP, prefer taking the address directly.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="function"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="function"/> is multicast, or does not wrap a static method.</exception>
        public static SurtrNativeEntryPoint FromDelegate(SurtrNativeFunction function)
        {
            if (function is null)
                throw new ArgumentNullException(nameof(function));

            if (function.GetInvocationList().Length != 1)
                throw new ArgumentException("A native entry point needs a single target, not a multicast delegate.", nameof(function));

            if (function.Target is not null)
                throw new ArgumentException(
                    "A native entry point must wrap a static method. Instance methods and capturing lambdas " +
                    "carry a hidden receiver that does not match the native function signature.",
                    nameof(function));

            IntPtr pointer = function.Method.MethodHandle.GetFunctionPointer();
            return new SurtrNativeEntryPoint(pointer, function);
        }
        #endregion

        /// <summary>
        /// Calls the host function.
        /// </summary>
        /// <remarks>
        /// One indirect call, identical whether the pointer came from <c>&amp;Method</c> or from a
        /// delegate. The cast's calling convention must stay in lockstep with
        /// <see cref="SurtrNativeFunction"/>, which is managed.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrValue Invoke(SurtrCallArguments arguments)
            => ((delegate*<SurtrCallArguments, SurtrValue>)_pointer)(arguments);
    }
}
