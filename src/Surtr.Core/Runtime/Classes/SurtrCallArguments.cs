#nullable enable

using Surtr.Runtime.Objects;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// The arguments passed to one call into a <see cref="SurtrNativeFunction"/>, plus the
    /// <see cref="SurtrRuntime"/> the call is running inside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Wraps a raw pointer and length rather than a <see cref="Span{T}"/> - the same two machine
    /// words, but a domain type instead of a generic one, so the call site reads as "the i-th
    /// argument as a string" instead of "index a span, then unbox the tag". Carrying the runtime
    /// alongside the arguments (rather than as a second delegate parameter) means every accessor
    /// that needs the entity registry - <see cref="Get{T}"/>, <see cref="GetString"/>, and so on -
    /// has it without the call site repeating it, and a native function taking no arguments still
    /// reaches the runtime through <see cref="Runtime"/>.
    /// </para>
    /// <para>
    /// A <see langword="readonly ref struct"/> on purpose: it can never be boxed, stored in a
    /// field, captured by a lambda, or held across an <see langword="await"/>, so it cannot
    /// outlive the stack frame that owns the memory <see cref="Pointer"/> addresses. That is the
    /// same guarantee <see cref="Span{T}"/> gives a raw pointer, applied to a type with domain
    /// methods on top of it.
    /// </para>
    /// <para>
    /// Every accessor comes in two tiers. The default is bounds-checked and, for entity lookups,
    /// null/type-checked with a clear exception - the tier a host writing its own native function
    /// should reach for, since nothing has verified its call site the way a Surtr call site is
    /// verified at compile time. The <c>*Unchecked</c> tier skips every check and is sound only
    /// when the caller already knows the index and type are right, which every built-in native
    /// method does: <c>InvokeNative</c> only ever reaches one after the compiler matched its
    /// declared Surtr signature against the call. That is the tier the built-ins in
    /// <c>Runtime/BuiltIns</c> use throughout.
    /// </para>
    /// </remarks>
    public readonly unsafe ref struct SurtrCallArguments
    {
        private readonly SurtrRawValue* _pointer;
        private readonly int _length;
        private readonly int _capacity;
        private readonly SurtrRuntime _runtime;

        /// <summary>
        /// Wraps a contiguous block of <paramref name="length"/> argument values, addressed by
        /// <paramref name="pointer"/>, for a call running on <paramref name="runtime"/>.
        /// </summary>
        /// <remarks>
        /// Public rather than internal, since a host that wants to call a
        /// <see cref="SurtrNativeEntryPoint"/> directly - to invoke a closure it is holding, for
        /// instance - has to be able to build a call frame the same way the interpreter does.
        /// <paramref name="pointer"/> must stay valid for at least as long as this value is used,
        /// which the ref-struct-ness of this type only enforces up to the boundary of the current
        /// stack frame - if it addresses pinned or stack memory that outlives that, it remains the
        /// caller's responsibility to keep it valid. The block is assumed writable up to exactly
        /// <paramref name="length"/> slots; use the capacity-taking overload when results wider
        /// than the arguments will be written.
        /// </remarks>
        public SurtrCallArguments(SurtrRuntime runtime, SurtrRawValue* pointer, int length)
        {
            _runtime = runtime;
            _pointer = pointer;
            _length = length;
            _capacity = length;
        }

        /// <summary>
        /// Wraps the call's arguments with room to answer: the first <paramref name="length"/>
        /// slots are the arguments, and up to <paramref name="capacity"/> slots from the same base
        /// may be written with results.
        /// </summary>
        /// <remarks>
        /// This is the interpreter's own shape: the data stack above the argument base is frame
        /// scratch the callee's budget already reserved, so an inline result (a tuple, a
        /// multi-field value class) or any multi-slot answer always has somewhere to land, zero
        /// arguments included.
        /// </remarks>
        public SurtrCallArguments(SurtrRuntime runtime, SurtrRawValue* pointer, int length, int capacity)
        {
            if (capacity < length)
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "The writable capacity cannot be smaller than the argument count.");

            _runtime = runtime;
            _pointer = pointer;
            _length = length;
            _capacity = capacity;
        }

        /// <summary>The runtime this call is running inside.</summary>
        public SurtrRuntime Runtime
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _runtime;
        }

        /// <summary>How many arguments were passed. For an instance method, this includes the receiver at index 0.</summary>
        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _length;
        }

        /// <summary>
        /// How many slots starting at slot 0 may be written - the arguments plus whatever scratch
        /// the caller reserved for results. Every <c>Return</c>/<c>WriteResult</c> member checks
        /// against this, not against <see cref="Length"/>.
        /// </summary>
        public int Capacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _capacity;
        }

        /// <summary>
        /// The raw pointer this call's arguments start at.
        /// </summary>
        /// <remarks>
        /// The escape hatch every other member exists to make unnecessary: everything above reads
        /// through this without exposing it. Using the value this returns still requires the
        /// caller's own <see langword="unsafe"/> context, regardless of this type's own - a type
        /// being declared <see langword="unsafe"/> lets its members use pointers without repeating
        /// the modifier, it does not grant that ability to callers.
        /// </remarks>
        public SurtrRawValue* Pointer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _pointer;
        }

        /// <summary>A safe, bounds-checked view over the same memory, for code that wants a <see cref="Span{T}"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<SurtrRawValue> AsSpan() => new ReadOnlySpan<SurtrRawValue>(_pointer, _length);

        #region Checked Access
        /// <summary>The raw value at <paramref name="index"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Length)</c>.</exception>
        public SurtrRawValue this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_length)
                    throw new ArgumentOutOfRangeException(nameof(index), index, $"Argument index is out of range for a call of {_length} argument(s).");

                return _pointer[index];
            }
        }

        /// <summary>The value at <paramref name="index"/>, still NaN-boxed.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrValue GetValue(int index) => SurtrValue.FromRaw(this[index]);

        /// <summary>The value at <paramref name="index"/> as an int.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrInt GetInt(int index) => GetValue(index).AsInt;

        /// <summary>The value at <paramref name="index"/> as a float.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrFloat GetFloat(int index) => GetValue(index).AsFloat;

        /// <summary>The value at <paramref name="index"/> as a bool.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrBool GetBool(int index) => GetValue(index).AsBool;

        /// <summary>The value at <paramref name="index"/> as a char.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrChar GetChar(int index) => GetValue(index).AsChar;

        /// <summary>The value at <paramref name="index"/> as a raw entity reference.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrRef GetReference(int index) => GetValue(index).AsReference;

        /// <summary>
        /// The value at <paramref name="index"/>, unwrapped to its primitive whether it arrived
        /// raw or boxed.
        /// </summary>
        /// <remarks>
        /// For a receiver: the language treats a boxed <c>5</c> and an unboxed <c>5</c> as the
        /// same value of the same class, so a method on the primitive classes has to be reachable
        /// through either representation. See <see cref="SurtrBoxed"/>.
        /// </remarks>
        /// <exception cref="ArgumentException">The value is a reference, but not to a <see cref="SurtrBoxed"/>.</exception>
        public SurtrValue GetPrimitive(int index)
        {
            var value = GetValue(index);
            if (!value.IsReference)
                return value;

            return Resolve<SurtrBoxed>(index)?.Value
                ?? throw new ArgumentException($"Argument {index} is a reference, but not a boxed primitive.", nameof(index));
        }

        /// <summary>The entity the value at <paramref name="index"/> names, as <typeparamref name="T"/>, or null if it is not a live reference to one.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T? Resolve<T>(int index) where T : SurtrRuntimeEntity => _runtime.Resolve<T>(this[index]);

        /// <summary>The entity the value at <paramref name="index"/> names, as <typeparamref name="T"/>.</summary>
        /// <exception cref="ArgumentException">The value is not a live reference to a <typeparamref name="T"/>.</exception>
        public T Get<T>(int index) where T : SurtrRuntimeEntity
            => Resolve<T>(index) ?? throw new ArgumentException($"Argument {index} is not a {typeof(T).Name} (or is null).", nameof(index));

        /// <summary>The string at <paramref name="index"/>. Sugar for <c>Get&lt;SurtrString&gt;</c>, since it is by far the most common argument type.</summary>
        /// <exception cref="ArgumentException">The value is not a live reference to a <see cref="SurtrString"/>.</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrString GetString(int index) => Get<SurtrString>(index);
        #endregion

        #region Returning Results
        /// <summary>
        /// Writes one result and answers the count a native function returns: one.
        /// </summary>
        /// <remarks>
        /// The whole of the ordinary return in one call - <c>return args.Return(value);</c> is a
        /// complete single-value body's last line. The result lands in slot 0, overwriting the
        /// first argument, per the in-place convention.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Return(SurtrValue value)
        {
            WriteResult(0, value);
            return 1;
        }

        /// <summary>
        /// Writes a whole block of results from slot 0 and answers how many it wrote - what an
        /// inline value's body returns, or any function answering several slots at once.
        /// </summary>
        public int Return(params SurtrValue[] values)
            => Return(values.AsSpan());

        /// <summary>The span form of <see cref="Return(SurtrValue[])"/>.</summary>
        public int Return(ReadOnlySpan<SurtrValue> values)
        {
            if (values.Length > _capacity)
                throw new ArgumentOutOfRangeException(nameof(values), values.Length, $"A result of {values.Length} slot(s) does not fit this call's writable capacity of {_capacity}.");

            for (int i = 0; i < values.Length; i++)
                _pointer[i] = values[i].Raw;

            return values.Length;
        }

        /// <summary>
        /// Writes result <paramref name="index"/> without returning the count - for bodies that
        /// fill several slots through their own loop and answer once at the end.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside <c>[0, Capacity)</c>.</exception>
        public void WriteResult(int index, SurtrValue value)
        {
            if ((uint)index >= (uint)_capacity)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Result index is out of range for a call with a writable capacity of {_capacity}.");

            _pointer[index] = value.Raw;
        }

        /// <summary>The unchecked tier of <see cref="WriteResult"/>. Sound when the index is known in range.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteResultUnchecked(int index, SurtrValue value) => _pointer[index] = value.Raw;
        #endregion

        #region Unchecked Access
        /// <summary>
        /// The raw value at <paramref name="index"/>, without a bounds check.
        /// </summary>
        /// <remarks>
        /// Sound only when <paramref name="index"/> is already known to be in range - which every
        /// built-in native method can rely on, since the interpreter only reaches one after the
        /// compiler matched its declared Surtr signature against the call site.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrRawValue GetRawUnchecked(int index) => _pointer[index];

        /// <summary>The value at <paramref name="index"/>, still NaN-boxed, without a bounds check.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrValue GetValueUnchecked(int index) => SurtrValue.FromRaw(_pointer[index]);

        /// <summary>
        /// The entity the value at <paramref name="index"/> names, as <typeparamref name="T"/>,
        /// without a bounds check, a null check, or a checked cast.
        /// </summary>
        /// <remarks>Sound under the same conditions as <see cref="GetRawUnchecked"/>, plus that the value is known to be a live reference to a <typeparamref name="T"/>.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetUnchecked<T>(int index) where T : SurtrRuntimeEntity => _runtime.Dereference<T>(_pointer[index]);

        /// <summary>
        /// The value at <paramref name="index"/>, unwrapped to its primitive whether it arrived
        /// raw or boxed, without a bounds check.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrValue GetPrimitiveUnchecked(int index)
        {
            var value = SurtrValue.FromRaw(_pointer[index]);
            return value.IsReference ? _runtime.Dereference<SurtrBoxed>(_pointer[index]).Value : value;
        }
        #endregion
    }
}
