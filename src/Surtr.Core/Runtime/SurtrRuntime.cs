#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Runtime.Utilities;
using Surtr.VM;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime
{
    /// <summary>
    /// One live Surtr runtime: the object heap, the host surface published into it, the modules
    /// loaded into it, and the entry point for everything a host does with the language.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is Surtr's equivalent of a <c>lua_State</c> - the single object a host holds on to.
    /// Everything mutable lives in its <see cref="SurtrContext"/>, which stays internal; this type
    /// is the whole public surface over it. Several runtimes can coexist in one process with
    /// completely separate heaps, globals and modules, and they still agree on what <c>string</c>
    /// or <c>array</c> means, because the built-in classes are process-wide (see
    /// <see cref="SurtrBuiltIns"/>).
    /// </para>
    /// <para>
    /// It is also the first argument every native entry point receives, passed as itself.
    /// <see cref="SurtrNativeFunction"/> uses the managed calling convention, so there is no
    /// reason to erase it to a <c>void*</c> and turn it back with a handle dereference and a type
    /// check on every call - and passing it directly means the collector keeps it alive for the
    /// duration of a call for free, with no handle to allocate, weaken or free.
    /// </para>
    /// <para>
    /// Disposal is the caller's job, since the unmanaged buffers under the heap and the loaded
    /// modules are not something the CLR's collector knows about. The finalizer is a backstop for
    /// an abandoned runtime, so a missed <see cref="Dispose"/> is a delay rather than a leak.
    /// </para>
    /// </remarks>
    public sealed unsafe class SurtrRuntime : IDisposable
    {
        /// <summary>How many objects a runtime's registry is sized for when no capacity is given.</summary>
        public const int DefaultEntityCapacity = 1024;

        private SurtrContext _context;
        private readonly SurtrValueComparer _valueComparer;
        private SurtrVirtualMachine? _virtualMachine;
        private long _pendingInstructionBudget;
        private bool _disposed;

        // Native enum case values awaiting their static slot, keyed by declaring class. Filled by
        // DefineNativeEnumCase (before linking, when AddEnumCase is legal) and drained by
        // FinishNativeClass (after linking, when the case fields' static addresses exist).
        private readonly Dictionary<SurtrClass, Dictionary<string, long>> _nativeEnumValues =
            new Dictionary<SurtrClass, Dictionary<string, long>>();

        // Host objects adopted as entities in their own right (RegisterHost), keyed by the CLR
        // instance itself: the entry dies with the key, which is what bounds the root each one
        // holds to the lifetime of the object it was made for.
        private readonly ConditionalWeakTable<object, SurtrNativeObject> _adoptedNatives =
            new ConditionalWeakTable<object, SurtrNativeObject>();

        /// <summary>Creates and initializes a runtime with the default heap capacity.</summary>
        public SurtrRuntime() : this(DefaultEntityCapacity) { }

        /// <summary>Creates and initializes a runtime.</summary>
        /// <param name="initialEntityCapacity">How many objects the heap should be sized for up front.</param>
        public SurtrRuntime(int initialEntityCapacity)
        {
            // Touching the built-ins here rather than lazily on first allocation keeps their
            // one-time construction out of whatever frame first happens to need a string.
            SurtrBuiltIns.EnsureBuilt();

            _context.Initialize(initialEntityCapacity);
            _valueComparer = new SurtrValueComparer(this);
        }

        /// <summary>Releases the unmanaged buffers of a runtime the host forgot to dispose.</summary>
        ~SurtrRuntime() => ReleaseResources();

        #region State
        /// <summary>The runtime's internal state, by reference so nothing copies it.</summary>
        internal ref SurtrContext Context
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref _context;
        }

        /// <summary>How this runtime decides when two values are equal, and how it hashes them.</summary>
        public SurtrValueComparer ValueComparer
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _valueComparer;
        }

        /// <summary>
        /// The one machine that executes bytecode against this runtime, created on first use.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Internal: the machine is the runtime's engine, not its API. A host that could reach it
        /// could push onto the data stack between calls, or start a run at an arbitrary frame, and
        /// every invariant the interpreter relies on - a balanced stack, a frame protocol that
        /// unwinds to the depth it started at - would become the host's problem to maintain. What
        /// the host gets instead is <see cref="Invoke(SurtrMethodInfo, SurtrValue[])"/> and its
        /// siblings, which push, call and clean up as one operation.
        /// </para>
        /// <para>
        /// One per runtime, not one per call site, because its data stack is a garbage collection
        /// root: <see cref="Collect(bool)"/> has to be able to find every value the interpreter is
        /// holding, and it can only do that if there is exactly one stack to look at. Execution on a
        /// runtime is single-threaded for the same reason a <c>lua_State</c> is.
        /// </para>
        /// <para>
        /// Lazy rather than built in the constructor so a host that only uses the runtime as an
        /// object heap - registering native types, holding values across native calls - never pays
        /// for a stack it does not execute on.
        /// </para>
        /// </remarks>
        internal SurtrVirtualMachine VirtualMachine
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                var machine = _virtualMachine;
                if (machine is null)
                {
                    machine = new SurtrVirtualMachine(this);
                    machine.StepBudget = _pendingInstructionBudget;
                    _virtualMachine = machine;
                }

                return machine;
            }
        }

        /// <summary>
        /// How many instructions the next run may execute before it aborts, or <c>0</c> - the
        /// default - for no limit.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For a host that evaluates untrusted or possibly non-terminating code, and specifically
        /// for a compiler folding a <c>const fun</c>: folding runs the function's real bytecode on
        /// this interpreter rather than on a second evaluator, so the two can never disagree about
        /// overflow, string equality or any trap - but a function that loops may loop forever, and
        /// a compiler that hangs is not acceptable. Exceeding the budget raises a
        /// <see cref="SurtrExecutionException"/>, which the handler search treats like any other
        /// trap.
        /// </para>
        /// <para>
        /// The budget is consumed as it runs and is <em>not</em> restored between calls, so a host
        /// evaluating several constants sets it again before each one -
        /// <see cref="ResetExecution"/> is the natural place, and already leaves the machine clean.
        /// </para>
        /// </remarks>
        public long InstructionBudget
        {
            get => _virtualMachine?.StepBudget ?? _pendingInstructionBudget;
            set
            {
                _pendingInstructionBudget = value < 0 ? 0 : value;

                // The machine is built lazily, so a budget set before anything has run has to
                // wait somewhere until there is a machine to set it on.
                if (_virtualMachine is not null)
                    _virtualMachine.StepBudget = _pendingInstructionBudget;
            }
        }

        /// <summary>How many objects the heap currently holds room for.</summary>
        public int HeapCapacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _context.EntityRegistry.Capacity;
        }

        /// <summary>How many objects the heap holds right now.</summary>
        public int LiveObjectCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _context.EntityRegistry.LiveCount;
        }

        /// <summary>
        /// How many objects every collection so far has reclaimed, in total.
        /// </summary>
        /// <remarks>
        /// Paired with <see cref="LiveObjectCount"/> this gives how many objects a stretch of
        /// execution allocated, without a counter on the registration path: what is live now, plus
        /// what has been reclaimed since, less what was live before.
        /// </remarks>
        public long TotalCollectedObjects
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _context.EntityRegistry.TotalCollectedEntities;
        }

        /// <summary>Whether the runtime has been disposed and is no longer usable.</summary>
        public bool IsDisposed
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _disposed;
        }
        #endregion

        #region Object Construction
        // Every factory registers what it builds before returning it. An unregistered object has
        // no SurtrRef, so it cannot be named by a SurtrValue and cannot be reached from bytecode
        // at all - handing one back would just be a way to produce something unusable.

        /// <summary>Allocates a string.</summary>
        public SurtrString NewString(string text)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));

            var value = new SurtrString(text);
            _context.EntityRegistry.Register(value);
            return value;
        }

        /// <summary>
        /// Allocates a bytes buffer that takes ownership of <paramref name="data"/>.
        /// </summary>
        /// <remarks>
        /// The public door a host crosses when it wants to hand a CLR <c>byte[]</c> into a Surtr
        /// script: the buffer keeps the array as its backing storage, copy-free, exactly like
        /// <see cref="NewString"/> wraps its text. A script-native body that builds a buffer from
        /// scratch uses the internal value-shaped helpers instead.
        /// </remarks>
        public SurtrBytes NewBytes(byte[] data)
        {
            if (data is null)
                throw new ArgumentNullException(nameof(data));

            var value = new SurtrBytes(data);
            _context.EntityRegistry.Register(value);
            return value;
        }

        /// <summary>
        /// Returns the one shared string object for <paramref name="text"/>, creating it the first
        /// time.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For text whose identity should be stable: string constants out of a chunk, member and
        /// key names, anything compared often. Interning makes reference identity agree with text
        /// equality for those, so <c>REQ</c> answers correctly and a dictionary lookup skips
        /// straight past the character comparison on a hit.
        /// </para>
        /// <para>
        /// Interned strings are rooted for the runtime's lifetime and never collected, which is
        /// the trade: use <see cref="NewString"/> for text a program computes, and this for text a
        /// program is built from.
        /// </para>
        /// </remarks>
        public SurtrString InternString(string text)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));

            if (_context.InternedStrings.TryGetValue(text, out var existing))
                return existing;

            var value = new SurtrString(text);
            SurtrRef reference = _context.EntityRegistry.Register(value);
            _context.InternedStrings.Add(text, value);
            _context.AddRoot(SurtrValue.CreateReference(reference).Raw);
            return value;
        }

        /// <summary>Allocates an empty array.</summary>
        /// <param name="typeReference">The array's full descriptor, for example <c>AI</c>. Optional.</param>
        /// <param name="capacity">How many elements to make room for up front.</param>
        public SurtrArray NewArray(SurtrClassReference typeReference = default, int capacity = 0)
        {
            var value = new SurtrArray(typeReference, capacity);
            _context.EntityRegistry.Register(value);
            return value;
        }

        /// <summary>Packs an already-built element block into a tuple, taking ownership of the array.</summary>
        public SurtrTuple NewTuple(SurtrClassReference typeReference, SurtrValue[] elements)
        {
            var value = new SurtrTuple(typeReference, elements);
            _context.EntityRegistry.Register(value);
            return value;
        }

        /// <summary>
        /// Allocates a tuple of <paramref name="arity"/> null elements, to be filled by
        /// <c>TupPack</c>. An arity of zero is legal and yields the empty tuple.
        /// </summary>
        public SurtrTuple NewTuple(SurtrClassReference typeReference, int arity)
        {
            var value = new SurtrTuple(typeReference, arity);
            _context.EntityRegistry.Register(value);
            return value;
        }

        /// <summary>Allocates an empty dictionary keyed under this runtime's value semantics.</summary>
        public SurtrDictionary NewDictionary(SurtrClassReference typeReference = default, int capacity = 0)
        {
            var value = new SurtrDictionary(typeReference, _valueComparer, capacity);
            _context.EntityRegistry.Register(value);
            return value;
        }

        /// <summary>Allocates a range, as <c>RangeNew</c> and <c>RangeNewInclusive</c> do.</summary>
        /// <param name="start">The lower bound, always included.</param>
        /// <param name="end">The upper bound, as written.</param>
        /// <param name="inclusive">Whether <paramref name="end"/> is part of the range: the <c>..=</c> form.</param>
        /// <remarks>
        /// For a range that genuinely escapes into a value. A range written inline in a loop header
        /// must not reach this - the compiler lowers that to a counted loop over two ints, with no
        /// object at all (<c>Language-Syntax.md</c> §5.4).
        /// </remarks>
        public SurtrRange NewRange(SurtrInt start, SurtrInt end, bool inclusive = false)
        {
            var value = new SurtrRange(start, end, inclusive);
            _context.EntityRegistry.Register(value);
            return value;
        }

        /// <summary>
        /// Allocates a cursor over one of the built-in collections, as <c>iterate()</c> does.
        /// </summary>
        /// <param name="kind">Which kind of source is being walked.</param>
        /// <param name="source">The collection to walk. Must match <paramref name="kind"/>.</param>
        /// <param name="keys">
        /// A dictionary's keys, snapshotted at this moment. Required for
        /// <see cref="SurtrIteratorKind.Dictionary"/> and meaningless otherwise.
        /// </param>
        /// <remarks>
        /// This is the general iteration path, which a compiled <c>for-in</c> over a built-in
        /// should never reach - see <see cref="SurtrIterator"/>. It is public because a host
        /// handing Surtr code an <c>IIterable</c> needs the same door the built-ins go through.
        /// </remarks>
        public SurtrIterator NewIterator(SurtrIteratorKind kind, SurtrObject source, SurtrValue[]? keys = null)
        {
            if (source is null)
                throw new ArgumentNullException(nameof(source));

            if (kind == SurtrIteratorKind.Dictionary && keys is null)
                throw new ArgumentException("A dictionary iterator walks a snapshot of its keys, which must be supplied.", nameof(keys));

            var value = new SurtrIterator(kind, source, keys);
            _context.EntityRegistry.Register(value);
            return value;
        }

        /// <summary>
        /// Returns the one shared <c>Type</c> value for a class or interface within this runtime,
        /// as <c>typeof</c> and <c>Type.of</c>/<c>Type.members</c>/<c>Type.baseType</c> all do.
        /// </summary>
        /// <remarks>
        /// Creates and permanently roots it the first time this runtime is asked about
        /// <paramref name="wrapped"/>, and returns the cached object on every call after that -
        /// see <see cref="SurtrContext.TypeValueCache"/>. Rooted the same way an interned string
        /// is: the cache dictionary itself is never traced, so an entry the collector could
        /// otherwise reclaim would leave a stale id behind. The cache is bounded by how many
        /// distinct classes and interfaces a program actually asks about, not by how many times it
        /// asks, so rooting every entry for the runtime's lifetime is cheap rather than a leak.
        /// </remarks>
        public SurtrTypeValue GetOrCreateTypeValue(SurtrTypeInfo wrapped)
        {
            if (_context.TypeValueCache.TryGetValue(wrapped, out var existing))
                return existing;

            var value = new SurtrTypeValue(wrapped);
            SurtrRef reference = _context.EntityRegistry.Register(value);
            _context.TypeValueCache.Add(wrapped, value);
            _context.AddRoot(SurtrValue.CreateReference(reference).Raw);
            return value;
        }

        /// <summary>
        /// The one shared <c>Type</c> value for a <em>construction</em> — <c>typeof(Box&lt;int&gt;)</c>
        /// or <c>Type.get("Obox:Box`1;I")</c> — which keeps the descriptor that named it, so
        /// <c>genericArguments</c> can answer which construction it is.
        /// </summary>
        /// <remarks>
        /// A construction is keyed by its full descriptor string: the same class shared by every
        /// construction still gets one distinct <c>Type</c> value per closed form, so
        /// <c>Type.get("...I")</c> never equals <c>Type.get("...S")</c>. An <em>open</em> form —
        /// a descriptor whose arguments are the declaration's own parameters, as
        /// <c>typeof(Box)</c> emits — is not a construction at all and falls back to the shared
        /// class value, the same one <c>Type.of(instancia)</c> and <c>GetTypeOfValue</c> reach.
        /// Rooted and cached exactly like <see cref="GetOrCreateTypeValue(SurtrTypeInfo)"/>.
        /// </remarks>
        public SurtrTypeValue GetOrCreateTypeValue(SurtrTypeInfo wrapped, SurtrClassReference reference)
        {
            if (!reference.IsValid || reference.GenericArity == 0 || reference.ContainsOpenParameter())
                return GetOrCreateTypeValue(wrapped);

            string key = reference.Descriptor;
            if (_context.ConstructedTypeValueCache.TryGetValue(key, out var existing))
                return existing;

            var value = new SurtrTypeValue(wrapped, reference);
            SurtrRef handle = _context.EntityRegistry.Register(value);
            _context.ConstructedTypeValueCache.Add(key, value);
            _context.AddRoot(SurtrValue.CreateReference(handle).Raw);
            return value;
        }

        /// <summary>
        /// Wraps a <see cref="SurtrModule"/> as a first-class <c>Module</c> value, as <c>moduleof</c>
        /// and <c>Module.get</c>/<c>Module.tryGet</c> do.
        /// </summary>
        /// <remarks>
        /// Same caching and rooting as <see cref="GetOrCreateTypeValue(SurtrTypeInfo)"/>: created and permanently
        /// rooted the first time this runtime is asked about <paramref name="wrapped"/>, and the
        /// cached object returned on every call after that - see
        /// <see cref="SurtrContext.ModuleValueCache"/>.
        /// </remarks>
        public SurtrModuleValue GetOrCreateModuleValue(SurtrModule wrapped)
        {
            if (_context.ModuleValueCache.TryGetValue(wrapped, out var existing))
                return existing;

            var value = new SurtrModuleValue(wrapped);
            SurtrRef reference = _context.EntityRegistry.Register(value);
            _context.ModuleValueCache.Add(wrapped, value);
            _context.AddRoot(SurtrValue.CreateReference(reference).Raw);
            return value;
        }

        /// <summary>Wraps a declaration as a first-class <c>Member</c> value, as <c>Type.members</c> does.</summary>
        public SurtrMemberValue NewMemberValue(SurtrMemberInfo wrapped)
        {
            var value = new SurtrMemberValue(wrapped);
            _context.EntityRegistry.Register(value);
            return value;
        }

        /// <summary>Builds a closure over <paramref name="method"/>, capturing <paramref name="upValues"/> by value.</summary>
        /// <exception cref="ArgumentException"><paramref name="method"/> has no body.</exception>
        public SurtrClosure NewClosure(SurtrMethodInfo method, SurtrValue[]? upValues = null, SurtrClassReference typeReference = default)
        {
            // A closure with nothing to capture is a pure function, so the stateless fast path and
            // the capturing one meet here: with no upvalues and no custom type, the host gets the
            // one shared closure for the method (see GetOrCreateFunctionValue), the same value
            // every zero-capture lambda in the language resolves to. A caller that explicitly
            // passes captures - or a custom type - still gets a fresh object, exactly as before.
            if ((upValues is null || upValues.Length == 0) && !typeReference.IsValid)
                return GetOrCreateFunctionValue(method);

            var value = new SurtrClosure(
                typeReference.IsValid ? typeReference : method.ToSignature(),
                method,
                upValues ?? Array.Empty<SurtrValue>());

            _context.EntityRegistry.Register(value);
            return value;
        }

        /// <summary>
        /// Returns the one shared <c>SurtrClosure</c> for a method within this runtime - the value
        /// every evaluation of that method as a zero-capture function resolves to.
        /// </summary>
        /// <remarks>
        /// Creates, registers and permanently roots it the first time this runtime is asked about
        /// <paramref name="method"/>, and returns the cached object on every call after that - see
        /// <see cref="SurtrContext.FunctionValueCache"/>. Rooted for the same reason a
        /// <see cref="SurtrTypeValue"/> is: the cache dictionary itself is never traced, so an
        /// entry the collector could otherwise reclaim would leave a stale id behind, and the cache
        /// is bounded by how many distinct zero-capture methods a program actually uses, so rooting
        /// every entry for the runtime's lifetime is cheap rather than a leak.
        /// </remarks>
        /// <exception cref="ArgumentException"><paramref name="method"/> has no body.</exception>
        public SurtrClosure GetOrCreateFunctionValue(SurtrMethodInfo method)
        {
            if (_context.FunctionValueCache.TryGetValue(method, out var existing))
                return existing;

            var value = new SurtrClosure(method.ToSignature(), method, Array.Empty<SurtrValue>());
            SurtrRef reference = _context.EntityRegistry.Register(value);
            _context.FunctionValueCache.Add(method, value);
            _context.AddRoot(SurtrValue.CreateReference(reference).Raw);
            return value;
        }

        /// <summary>Allocates a zeroed instance of <paramref name="class"/>, as <c>ObjNew</c> does.</summary>
        /// <exception cref="ArgumentException"><paramref name="class"/> is abstract or has not been linked.</exception>
        public SurtrInstance NewInstance(SurtrClass @class)
        {
            if (@class.IsAbstract)
                throw new ArgumentException($"'{@class.Name}' is abstract and cannot be instantiated.", nameof(@class));

            if (!@class.IsBuilt)
                throw new ArgumentException($"'{@class.Name}' has not been linked; its instance layout is not known yet.", nameof(@class));

            var value = new SurtrInstance(@class);
            _context.EntityRegistry.Register(value);
            return value;
        }

        /// <summary>Boxes a primitive, as <c>BoxInt</c>, <c>BoxFloat</c> and <c>BoxBool</c> do.</summary>
        /// <exception cref="ArgumentException"><paramref name="value"/> is a reference rather than a primitive.</exception>
        public SurtrBoxed Box(SurtrValue value)
        {
            if (value.IsReference)
                throw new ArgumentException("Only primitives can be boxed; the value given is already a reference.", nameof(value));

            var boxed = new SurtrBoxed(SurtrBuiltIns.ForValue(value), value);
            _context.EntityRegistry.Register(boxed);
            return boxed;
        }

        /// <summary>Wraps an arbitrary host object so Surtr code can carry it around.</summary>
        public SurtrNativeProxy WrapNative(object? target)
        {
            var value = new SurtrNativeProxy(target);
            _context.EntityRegistry.Register(value);
            return value;
        }

        /// <summary>
        /// Builds an exception object of <paramref name="exceptionClass"/> carrying
        /// <paramref name="message"/>.
        /// </summary>
        /// <remarks>
        /// The message goes straight into the slot <c>Exception</c> declares rather than through a
        /// constructor call: this is reached from inside a trap, where re-entering the interpreter
        /// to run a constructor would mean raising an exception from the middle of raising one.
        /// Every subclass inherits that slot at the same index, which is what lets one helper build
        /// any of them.
        /// </remarks>
        /// <exception cref="ArgumentException"><paramref name="exceptionClass"/> does not derive from <c>Exception</c>.</exception>
        public SurtrInstance NewException(SurtrClass exceptionClass, string message)
        {
            if (exceptionClass is null)
                throw new ArgumentNullException(nameof(exceptionClass));

            if (!exceptionClass.IsSubclassOf(SurtrBuiltIns.Exception))
                throw new ArgumentException(
                    $"'{exceptionClass.Name}' does not derive from Exception and cannot be raised.",
                    nameof(exceptionClass));

            var instance = new SurtrInstance(exceptionClass);
            _context.EntityRegistry.Register(instance);

            instance[SurtrStandardLibrary.MessageSlot] =
                SurtrValue.CreateReference(NewString(message).GetSurtrReference());

            return instance;
        }

        /// <summary>Wraps a host object as an instance of a host-declared native class.</summary>
        public SurtrNativeProxy WrapNative(SurtrClass nativeClass, object? target)
        {
            var value = new SurtrNativeProxy(nativeClass, target);
            _context.EntityRegistry.Register(value);
            return value;
        }

        /// <summary>
        /// Registers a native object the host built itself - typically an instance of its own
        /// <see cref="SurtrNativeObject"/> subclass - and returns its handle.
        /// </summary>
        public SurtrRef RegisterNative(SurtrNativeObject nativeObject)
            => _context.EntityRegistry.Register(nativeObject);

        /// <summary>
        /// The host object <paramref name="value"/> carries, as a Surtr value: an instance of a
        /// host class deriving from <see cref="SurtrNativeObject"/> is adopted as the entity
        /// itself, and anything else is wrapped in a <see cref="SurtrNativeProxy"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A class the bridge registers as a native type is normally a plain CLR object the
        /// runtime cannot touch directly, so every crossing wraps it in a proxy whose target it
        /// is. A host class deriving from <see cref="SurtrNativeObject"/> needs none of that: the
        /// object already <em>is</em> a Surtr entity, so wrapping would bury it inside a second,
        /// shell entity and reading it back through the proxy's target would reach null or the
        /// wrong object. This is the one crossing point both shapes share.
        /// </para>
        /// <para>
        /// Adoption is cached and rooted: one CLR instance answers the same reference for every
        /// crossing of every call, the way an enum's case objects do. The root is what keeps the
        /// registry from sweeping the entity while the host still holds the CLR object and could
        /// hand it back; the cache entry dies with that object, and with it the last strong
        /// reference to the entity. Host-authored facade types are a bounded set, so the pin is
        /// bounded too. An opaque value keeps today's behavior - wrapped fresh each time,
        /// collectable like any proxy.
        /// </para>
        /// </remarks>
        public SurtrValue RegisterHost(object? value)
        {
            if (value is null)
                return SurtrValue.Null;

            if (value is SurtrNativeObject native)
            {
                if (_adoptedNatives.TryGetValue(value, out var adopted))
                    return ValueOf(adopted);

                _context.EntityRegistry.Register(native);
                AddRoot(native);
                _adoptedNatives.Add(value, native);
                return ValueOf(native);
            }

            return ValueOf(WrapNative(value));
        }

        /// <summary>
        /// The CLR object behind <paramref name="value"/>: the wrapped target of a proxy, or the
        /// adopted <see cref="SurtrNativeObject"/> itself when the reference names one directly.
        /// </summary>
        public object? HostValueOf(SurtrValue value)
        {
            var entity = Resolve<SurtrNativeObject>(value);
            if (entity is null)
                return null;

            return entity is SurtrNativeProxy proxy ? proxy.Target : entity;
        }
        #endregion

        /// <summary>
        /// Runs <paramref name="generator"/> until its next <c>yield</c> or its end.
        /// </summary>
        /// <remarks>
        /// Internal because it is one half of an operation: the value produced is left on the
        /// generator for <c>current</c> to read, so a host driving one by hand would have to keep
        /// the two in step itself. The built-in <c>moveNext</c> is the supported way in, and a
        /// compiled loop does not come through here at all - it lowers to <c>GenResume</c>.
        /// </remarks>
        /// <returns><see langword="true"/> if the body yielded; <see langword="false"/> if it finished.</returns>
        internal bool ResumeGenerator(SurtrGenerator generator)
            => VirtualMachine.ResumeGenerator(generator);

        /// <summary>Resumes <paramref name="generator"/> with a value its <c>yield</c> evaluates to.</summary>
        /// <remarks>Internal for the same reason as <see cref="ResumeGenerator"/>: the built-in <c>send</c> is the way in.</remarks>
        internal bool SendToGenerator(SurtrGenerator generator, SurtrValue value)
            => VirtualMachine.SendToGenerator(generator, value);

        /// <summary>Raises <paramref name="exception"/> inside <paramref name="generator"/> where it is suspended.</summary>
        internal bool RaiseInGenerator(SurtrGenerator generator, SurtrRef exception)
            => VirtualMachine.RaiseInGenerator(generator, exception);

        /// <summary>Ends <paramref name="generator"/>, running whatever <c>finally</c> blocks it has pending.</summary>
        internal void DisposeGenerator(SurtrGenerator generator)
            => VirtualMachine.DisposeGenerator(generator);

        #region Value Access
        /// <summary>The value naming <paramref name="entity"/>, or null if it is not registered.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrValue ValueOf(SurtrRuntimeEntity? entity)
            => entity is null ? SurtrValue.Null : SurtrValue.CreateReference(entity.GetSurtrReference());

        /// <summary>The entity <paramref name="value"/> names, or null if it is not a live reference.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrRuntimeEntity? Resolve(SurtrValue value)
            => value.IsReference ? _context.EntityRegistry.Get(value.AsReference) : null;

        /// <summary>
        /// The entity <paramref name="value"/> names as <typeparamref name="T"/>, or null if it is
        /// not a live reference to one.
        /// </summary>
        public T? Resolve<T>(SurtrValue value) where T : SurtrRuntimeEntity
            => value.IsReference ? _context.EntityRegistry.Get(value.AsReference) as T : null;

        /// <summary>
        /// The entity an argument names as <typeparamref name="T"/>, or null if it is not a live
        /// reference to one.
        /// </summary>
        /// <remarks>
        /// The overload a host's <see cref="SurtrNativeFunction"/> wants, since its arguments
        /// arrive as raw values: <c>runtime.Resolve&lt;SurtrString&gt;(arguments[0])</c> reads the
        /// receiver in one call. Checked, unlike the internal fast path the built-ins use.
        /// </remarks>
        public T? Resolve<T>(SurtrRawValue raw) where T : SurtrRuntimeEntity
        {
            var value = SurtrValue.FromRaw(raw);
            return value.IsReference ? _context.EntityRegistry.Get(value.AsReference) as T : null;
        }

        /// <summary>
        /// Resolves a raw reference straight to <typeparamref name="T"/>, checking nothing.
        /// </summary>
        /// <remarks>
        /// The form built-in native entry points use. The compiler has already proved the argument
        /// is a live reference of the right type, so re-checking it on every call would be paying
        /// twice for what static typing already bought.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal T Dereference<T>(SurtrRawValue raw) where T : SurtrRuntimeEntity
            => _context.EntityRegistry.GetUnsafe<T>((SurtrRef)raw);

        /// <summary>Allocates a string and returns a value naming it, ready to return from a native entry point.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SurtrValue NewStringValue(string text)
            => SurtrValue.CreateReference(_context.EntityRegistry.Register(new SurtrString(text)));

        /// <summary>Allocates an empty bytes buffer with room for <paramref name="capacity"/> bytes.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SurtrValue NewBytesValue(int capacity)
            => SurtrValue.CreateReference(_context.EntityRegistry.Register(new SurtrBytes(capacity)));

        /// <summary>Allocates a bytes buffer that takes ownership of <paramref name="data"/> and returns a value naming it.</summary>
        /// <remarks>
        /// Takes ownership rather than copying, so a host handing a <c>byte[]</c> across the
        /// boundary pays no copy. The buffer keeps whatever capacity <paramref name="data"/> has,
        /// so its <c>capacity</c> property may read larger than its <c>length</c>.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SurtrValue NewBytesValue(byte[] data)
            => SurtrValue.CreateReference(_context.EntityRegistry.Register(new SurtrBytes(data)));
        #endregion

        #region Type Handles
        /// <summary>
        /// Interns a type handle for a host-declared signature and resolves it if it can be
        /// resolved yet.
        /// </summary>
        /// <remarks>
        /// Built-ins bind immediately, since they exist before any runtime does. A reference to a
        /// Surtr class only binds once its module is loaded, so a handle taken before that stays
        /// unresolved and is picked up by <see cref="LoadModule(SurtrModule)"/>.
        /// </remarks>
        public SurtrTypeHandle TypeHandle(SurtrClassReference reference)
        {
            var handle = _context.HostTypeHandles.GetOrAdd(reference);
            TryResolveHandle(handle);
            return handle;
        }

        /// <summary>Builds parameter metadata against this runtime's handle table.</summary>
        public SurtrParameterInfo Parameter(string name, SurtrClassReference parameterType)
            => new SurtrParameterInfo(name, TypeHandle(parameterType));

        /// <summary>Builds metadata for a parameter a call site may omit.</summary>
        public SurtrParameterInfo Parameter(string name, SurtrClassReference parameterType, SurtrConstant defaultValue)
            => new SurtrParameterInfo(name, TypeHandle(parameterType), defaultValue);

        /// <summary>
        /// Builds metadata for the varargs parameter, whose declared type is the element type -
        /// the body sees an array of it.
        /// </summary>
        public SurtrParameterInfo VarargsParameter(string name, SurtrClassReference elementType)
            => new SurtrParameterInfo(name, TypeHandle(elementType), SurtrConstant.None, isVarargs: true);

        /// <summary>
        /// Binds <paramref name="handle"/> to the type its descriptor names, if that type is
        /// known.
        /// </summary>
        /// <returns><see langword="true"/> if the handle is resolved when this returns.</returns>
        internal bool TryResolveHandle(SurtrTypeHandle handle)
        {
            if (handle.IsResolved)
                return true;

            if (!TryResolveReference(handle.Reference, out var resolved))
                return false;

            handle.Resolve(resolved!);
            return true;
        }

        /// <summary>
        /// Resolves a descriptor to the metadata it names, against this runtime's loaded modules,
        /// the built-in module, and any host-declared native classes.
        /// </summary>
        /// <remarks>
        /// The same resolution every type handle in a loading module goes through, factored out so
        /// it can also run from a raw descriptor string with no handle behind it -
        /// <c>Type.get</c>/<c>Type.tryGet</c> are the other caller.
        /// </remarks>
        internal bool TryResolveReference(SurtrClassReference reference, out SurtrTypeInfo? resolved)
        {
            resolved = null;
            var typeCode = reference.TypeCode;

            // Every built-in family collapses onto one shared class, whatever it is parameterised
            // by: AI and AS are both the array class, because an array's element type is a static
            // fact the compiler enforces, not something the object carries.
            if (typeCode.IsPrimitive || typeCode.IsBuiltIn || typeCode.IsVoid || typeCode.IsErased)
            {
                resolved = SurtrBuiltIns.ForTypeCode(typeCode);
                return true;
            }

            if (!reference.TryGetFullName(out string fullName))
                return false;

            if (typeCode.IsNative)
            {
                if (!_context.NativeClasses.TryGetValue(fullName, out var nativeClass))
                    return false;

                resolved = nativeClass;
                return true;
            }

            if (!typeCode.IsObject)
                return false;

            SurtrClassReference.TrySplitFullName(fullName, out string modulePath, out string typePath);
            if (!_context.Modules.TryGetValue(modulePath, out var module))
            {
                // The built-in module is implicitly in scope in every file, which is what lets
                // `Exception` and `Attribute` be written unqualified - but it is process-wide and
                // deliberately *not* in this runtime's table, because disposing a runtime disposes
                // every module that is. So it is reached here instead of being registered.
                if (!string.Equals(modulePath, SurtrBuiltIns.ModulePath, StringComparison.Ordinal))
                    return false;

                module = SurtrBuiltIns.Module;
            }

            var declared = module.FindClass(typePath);
            if (declared is not null)
            {
                resolved = declared;
                return true;
            }

            // Interfaces are named by the same O-descriptor as classes - a type reference does not
            // know in advance which one it will turn out to be - so a name that is not a class is
            // tried as one before giving up.
            if (module.TryGetInterface(typePath, out var contract))
            {
                resolved = contract;
                return true;
            }

            return false;
        }
        #endregion

        #region Modules
        /// <summary>
        /// Brings a module into the runtime: binds every type it mentions, then links every type
        /// it declares.
        /// </summary>
        /// <remarks>
        /// The module's own handle table is its dependency list, so loading is exactly "resolve
        /// every entry, then link". Anything still unresolved afterwards names a type no loaded
        /// module declares, which is a load failure rather than something to discover mid-execution.
        /// </remarks>
        /// <exception cref="InvalidOperationException">A module with that path is already loaded, or a type it mentions cannot be found.</exception>
        public void LoadModule(SurtrModule module)
        {
            if (module is null)
                throw new ArgumentNullException(nameof(module));

            if (_context.Modules.ContainsKey(module.Path))
                throw new InvalidOperationException($"A module is already loaded at path '{module.Path}'.");

            // A module carries state that belongs to the runtime that loaded it - string literals
            // patched with references from its heap, native imports bound to its global table - so
            // loading the same instance twice would corrupt the second silently. Rejecting it is
            // the whole fix; a module meant for two runtimes is built twice.
            if (module.IsLoaded)
                throw new InvalidOperationException(
                    $"Module '{module.Path}' is already loaded into a runtime and cannot be loaded into another.");

            // Registered before resolving, because a module's types are almost always mentioned by
            // the module itself and would otherwise be unfindable.
            _context.Modules.Add(module.Path, module);

            try
            {
                foreach (var handle in module.TypeHandles.Handles)
                {
                    if (!TryResolveHandle(handle))
                        throw new InvalidOperationException(
                            $"Module '{module.Path}' refers to '{handle.Reference.ToDisplayString()}' ({handle.Reference.Descriptor}), which no loaded module declares.");
                }

                BindPendingReferences(module);
                BindNativeBodies(module);

                SurtrTypeLinker.LinkModule(module, ref _context.NextInterfaceId);

                MaterializeStringConstants(module);
                MaterializeAttributes(module);
                RegisterStaticBlocks(module);

                module.MarkLoaded();

                // Host signatures taken before this module existed can bind now.
                RetryHostHandles();

                RunStaticInitializers(module);
            }
            catch
            {
                _context.Modules.Remove(module.Path);
                throw;
            }
        }

        /// <summary>
        /// Loads a module from an image, instantiating a fresh one for this runtime.
        /// </summary>
        /// <remarks>
        /// The overload to reach for when the same compiled module is wanted in more than one
        /// runtime. An image can be instantiated any number of times; a
        /// <see cref="SurtrModule"/> is loadable exactly once, because loading is what ties it to a
        /// heap, a global table and a set of static storage - see <see cref="Bytecode.Image.SurtrModuleImage"/>.
        /// </remarks>
        /// <returns>The module this runtime now holds.</returns>
        public SurtrModule LoadModule(Bytecode.Image.SurtrModuleImage image)
        {
            if (image is null)
                throw new ArgumentNullException(nameof(image));

            var module = image.Instantiate();

            try
            {
                LoadModule(module);
            }
            catch
            {
                module.Dispose();
                throw;
            }

            return module;
        }

        /// <summary>
        /// Binds the by-name access-table entries a module read from an image still carries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Runs after every type handle is resolved and before linking, because what it needs is
        /// exactly what handle resolution just produced - the class behind each descriptor - and
        /// what it produces is what the interpreter will index. A module the emitter built has
        /// nothing pending and skips all of it.
        /// </para>
        /// <para>
        /// Members are found by name and, for a method, by signature key. That is the same key the
        /// linker matches an override on, so a call site written against one overload cannot bind
        /// to another.
        /// </para>
        /// </remarks>
        private void BindPendingReferences(SurtrModule module)
        {
            var chunk = module.Chunk;
            if (!chunk.HasPendingReferences)
                return;

            var modulePaths = chunk.PendingModulePaths;
            for (int i = 0; i < modulePaths.Length; i++)
            {
                if (!TryGetModule(modulePaths[i], out var referenced))
                    throw new InvalidOperationException(
                        $"Module '{module.Path}' calls into '{modulePaths[i]}', which is not loaded.");

                chunk.ModuleTable[i] = referenced;
            }

            var pendingFields = chunk.PendingFields;
            for (int i = 0; i < pendingFields.Length; i++)
                chunk.FieldTable[i] = ResolvePendingField(module, pendingFields[i]);

            var pendingMethods = chunk.PendingMethods;
            for (int i = 0; i < pendingMethods.Length; i++)
                chunk.MethodTable[i] = ResolvePendingMethod(module, pendingMethods[i]);

            chunk.PendingModulePaths = Array.Empty<string>();
            chunk.PendingFields = Array.Empty<SurtrPendingMember>();
            chunk.PendingMethods = Array.Empty<SurtrPendingMember>();
        }

        private SurtrFieldInfo ResolvePendingField(SurtrModule module, in SurtrPendingMember pending)
        {
            if (pending.OwnerDescriptor is null)
            {
                if (module.TryGetField(pending.Name, out var moduleField))
                    return moduleField;

                throw new InvalidOperationException(
                    $"Module '{module.Path}' names module-level field '{pending.Name}', which it does not declare.");
            }

            var owner = ResolvePendingOwner(module, pending.OwnerDescriptor);

            if (owner is SurtrClass declaring && declaring.TryGetField(pending.Name, out var field))
                return field;

            throw new InvalidOperationException(
                $"Module '{module.Path}' names field '{pending.Name}' on '{pending.OwnerDescriptor}', which does not declare it.");
        }

        private SurtrMethodInfo ResolvePendingMethod(SurtrModule module, in SurtrPendingMember pending)
        {
            SurtrMethodInfo[]? overloads;

            if (pending.OwnerDescriptor is null)
            {
                if (!module.TryGetMethods(pending.Name, out overloads))
                    throw new InvalidOperationException(
                        $"Module '{module.Path}' names module-level function '{pending.Name}', which it does not declare.");
            }
            else
            {
                var owner = ResolvePendingOwner(module, pending.OwnerDescriptor);

                bool found = owner is SurtrClass declaring
                    ? declaring.TryGetMethods(pending.Name, out overloads)
                    : ((SurtrInterface)owner).TryGetMethods(pending.Name, out overloads);

                if (!found)
                    throw new InvalidOperationException(
                        $"Module '{module.Path}' names method '{pending.Name}' on '{pending.OwnerDescriptor}', which does not declare it.");
            }

            for (int i = 0; i < overloads!.Length; i++)
            {
                if (string.Equals(overloads[i].SignatureKey(), pending.SignatureKey, StringComparison.Ordinal))
                    return overloads[i];
            }

            throw new InvalidOperationException(
                $"Module '{module.Path}' names the overload '{pending.SignatureKey}' of '{pending.Name}', which no declaration matches.");
        }

        /// <summary>
        /// Gives every native member that is still waiting for one the body this runtime published
        /// under its link name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Runs before linking, so a member with no body is rejected before anything can reach it
        /// - a static initializer runs at the end of this same load, and it can call one.
        /// </para>
        /// <para>
        /// A method that already carries an entry point is left alone. That is the module a host
        /// built in this process, where the address was known at declaration; it is not a second
        /// mechanism, just the case where the binding already happened.
        /// </para>
        /// </remarks>
        private void BindNativeBodies(SurtrModule module)
        {
            // Module level too, not only classes. Nothing in the emitter declares a native
            // function there - a host global is what Surtr calls that - but SurtrModule.AddMethod
            // is public, and a member the binder skipped would be an unbound address the
            // interpreter jumps to.
            foreach (var overloads in module.Methods)
                BindNativeBodiesIn(module, module.Path, overloads);

            foreach (var type in module.Classes)
                BindNativeBodiesOn(module, type);
        }

        private void BindNativeBodiesOn(SurtrModule module, SurtrClass type)
        {
            foreach (var overloads in type.Methods)
                BindNativeBodiesIn(module, type.Name, overloads);

            foreach (var nested in type.NestedClasses)
                BindNativeBodiesOn(module, nested);
        }

        private void BindNativeBodiesIn(SurtrModule module, string ownerName, SurtrMethodInfo[] overloads)
        {
            for (int i = 0; i < overloads.Length; i++)
            {
                if (overloads[i] is not SurtrNativeMethodInfo native || native.IsBound)
                    continue;

                if (!_context.NativeBodies.TryGetValue(native.LinkName, out var entryPoint))
                    throw new InvalidOperationException(
                        $"Module '{module.Path}' declares native member '{ownerName}.{native.Name}', whose body this runtime has no registration for. " +
                        $"Publish it with DefineNativeBody(\"{native.LinkName}\", …) before loading the module.");

                native.BindEntryPoint(entryPoint);
            }
        }

        private SurtrTypeInfo ResolvePendingOwner(SurtrModule module, string descriptor)
        {
            var handle = module.TypeHandles.GetOrAdd(SurtrClassReference.FromDescriptor(descriptor));

            if (!handle.IsResolved && !TryResolveHandle(handle))
                throw new InvalidOperationException(
                    $"Module '{module.Path}' names a member of '{descriptor}', which no loaded module declares.");

            return handle.ResolvedType!;
        }

        /// <summary>
        /// Builds one instance per attribute usage in the module and roots it permanently.
        /// </summary>
        /// <remarks>
        /// <para>
        /// At load, with the module's other statics, for the same reason they run there: every
        /// class an attribute could name is linked by now, and a host reading an attribute back
        /// should never be the thing that triggers its construction.
        /// </para>
        /// <para>
        /// Rooted permanently rather than traced from the member: class metadata is owned outright
        /// and is never registered with the entity registry, so there is nothing for a collection
        /// to reach an attribute instance <em>through</em>. The root set is what keeps it alive,
        /// and metadata's lifetime is the runtime's.
        /// </para>
        /// <para>
        /// Arguments fill the attribute's instance slots positionally rather than going through a
        /// constructor call: running bytecode here would mean executing during load, before the
        /// module is marked loaded, and an attribute's constructor has nothing to do but assign.
        /// </para>
        /// </remarks>
        private void MaterializeAttributes(SurtrModule module)
        {
            // A module itself carries none: the syntax attaches an attribute to a declaration, and
            // a module is derived from a directory rather than declared.
            foreach (var field in module.Fields)
                MaterializeAttributesOn(field);

            foreach (var property in module.Properties)
                MaterializeAttributesOn(property);

            foreach (var overloads in module.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                    MaterializeAttributesOn(overloads[i]);
            }

            foreach (var type in module.Classes)
                MaterializeAttributesOnType(type);

            foreach (var contract in module.Interfaces)
                MaterializeAttributesOn(contract);
        }

        private void MaterializeAttributesOnType(SurtrClass type)
        {
            MaterializeAttributesOn(type);

            foreach (var field in type.Fields)
                MaterializeAttributesOn(field);

            foreach (var property in type.Properties)
                MaterializeAttributesOn(property);

            foreach (var overloads in type.Methods)
            {
                for (int i = 0; i < overloads.Length; i++)
                    MaterializeAttributesOn(overloads[i]);
            }

            foreach (var nested in type.NestedClasses)
                MaterializeAttributesOnType(nested);

            foreach (var nested in type.NestedInterfaces)
                MaterializeAttributesOn(nested);
        }

        private void MaterializeAttributesOn(SurtrMemberInfo member)
        {
            var attributes = member.Attributes;

            for (int i = 0; i < attributes.Length; i++)
            {
                var usage = attributes[i];
                if (usage.Instance != SurtrValue.NullRef)
                    continue;

                var attributeClass = usage.AttributeType.ResolvedClass
                    ?? throw new InvalidOperationException(
                        $"Attribute '{usage.AttributeType.Reference.ToDisplayString()}' on '{member.Name}' did not resolve to a class.");

                if (!attributeClass.IsSubclassOf(SurtrBuiltIns.Attribute))
                    throw new InvalidOperationException(
                        $"'{attributeClass.Name}' is used as an attribute on '{member.Name}' but does not derive from Attribute.");

                var instance = new SurtrInstance(attributeClass);
                SurtrRef reference = _context.EntityRegistry.Register(instance);

                var arguments = usage.Arguments;
                if (arguments.Length > instance.SlotCount)
                    throw new InvalidOperationException(
                        $"Attribute '{attributeClass.Name}' on '{member.Name}' was given {arguments.Length} arguments but has {instance.SlotCount} fields.");

                for (int argument = 0; argument < arguments.Length; argument++)
                    instance[argument] = arguments[argument].Materialize(this);

                AddRoot(instance);
                usage.Instance = reference;
            }
        }

        /// <summary>
        /// Turns the chunk's CLR string literals into real string objects and patches their
        /// references into the constant pool.
        /// </summary>
        /// <remarks>
        /// Interned rather than merely allocated, so two literals with the same text are one
        /// object: that makes reference identity agree with text equality for constants, which is
        /// what lets <c>REQ</c> answer correctly on them and keeps them rooted for the runtime's
        /// life - exactly the lifetime a literal wants.
        /// </remarks>
        private void MaterializeStringConstants(SurtrModule module)
        {
            var chunk = module.Chunk;
            var literals = chunk.StringConstants;
            int count = chunk.StringConstantSlots.Length;

            for (int i = 0; i < count; i++)
            {
                int slot = chunk.StringConstantSlots[i];
                SurtrRef reference = InternString(literals[i]).GetSurtrReference();
                chunk.Constants[slot] = SurtrValue.CreateReference(reference).Raw;
            }
        }

        /// <summary>
        /// Tells the collector about the static storage this module just had laid out, its own and
        /// each of its classes'.
        /// </summary>
        private void RegisterStaticBlocks(SurtrModule module)
        {
            _context.AddStaticBlock(
                module.StaticStorage.Pointer,
                module.ReferenceStaticSlots.Pointer,
                module.ReferenceStaticSlots.Length);

            foreach (var declared in module.Classes)
                RegisterClassStaticBlocks(declared);
        }

        private void RegisterClassStaticBlocks(SurtrClass declared)
        {
            _context.AddStaticBlock(
                declared.StaticStorage.Pointer,
                declared.ReferenceStaticSlots.Pointer,
                declared.ReferenceStaticSlots.Length);

            foreach (var nested in declared.NestedClasses)
                RegisterClassStaticBlocks(nested);
        }

        /// <summary>
        /// Runs every static initializer the module brought in: each class's first, then the
        /// module's own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Eagerly at load, not lazily on first touch. Lazy initialization is what Java does, and it
        /// buys initialization-order independence at the price of a "has this run yet" test on every
        /// static field access and every static call - on the hot path, forever, to answer a
        /// question that is false exactly once. Loading a module is a controlled event in an
        /// embedded language, so the cost belongs there instead.
        /// </para>
        /// <para>
        /// The price is ordering: initializers run in declaration order, classes before the module,
        /// so one that reads another class's statics only sees them if that class was declared
        /// first. Cross-initializer dependencies are the compiler's to reject.
        /// </para>
        /// </remarks>
        private void RunStaticInitializers(SurtrModule module)
        {
            foreach (var declared in module.Classes)
                RunClassStaticInitializers(declared);

            if (module.StaticInitializer is not null)
                VirtualMachine.Call(module.StaticInitializer, 0);
        }

        private void RunClassStaticInitializers(SurtrClass declared)
        {
            if (declared.StaticInitializer is not null)
                VirtualMachine.Call(declared.StaticInitializer, 0);

            foreach (var nested in declared.NestedClasses)
                RunClassStaticInitializers(nested);
        }

        /// <summary>Looks up a loaded module by path.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetModule(string path, out SurtrModule module)
            => _context.Modules.TryGetValue(path, out module!);

        /// <summary>Every module loaded into this runtime, for <c>Module.submodules()</c> to scan by path prefix.</summary>
        /// <remarks>
        /// The built-in module (<see cref="SurtrBuiltIns.ModulePath"/>) is deliberately absent - it
        /// is process-wide and never registered in this runtime's own table, the same reason
        /// <see cref="TryResolveHandle"/> reaches it as a special case rather than through here.
        /// </remarks>
        public IReadOnlyCollection<SurtrModule> LoadedModules => _context.Modules.Values;

        private void RetryHostHandles()
        {
            foreach (var handle in _context.HostTypeHandles.Handles)
                TryResolveHandle(handle);
        }
        #endregion

        #region Host Types
        /// <summary>
        /// Declares a native class: the Surtr-side face of a host type.
        /// </summary>
        /// <remarks>
        /// Returned still under construction, so the host can hang native methods and properties on
        /// it, and finished with <see cref="FinishNativeClass"/>. Instances of it are
        /// <see cref="SurtrNativeObject"/>s - either <see cref="SurtrNativeProxy"/> or the host's
        /// own subclass.
        /// </remarks>
        /// <param name="fullName">The name its descriptor carries, for example <c>UnityEngine:GameObject</c>.</param>
        /// <param name="baseClass">The native class it extends, if any.</param>
        /// <param name="typeArguments">The descriptors of a closed generic construction, if any.</param>
        /// <exception cref="InvalidOperationException">A native class with that full name is already declared.</exception>
        public SurtrClass DefineNativeClass(string fullName, SurtrClass? baseClass = null, SurtrClassReference[]? typeArguments = null)
        {
            if (_context.NativeClasses.ContainsKey(fullName))
                throw new InvalidOperationException($"A native class named '{fullName}' is already declared.");

            if (baseClass is not null && baseClass.TypeCode != SurtrValueTypeCode.Native)
                throw new ArgumentException($"'{baseClass.Name}' is not a native class.", nameof(baseClass));

            var reference = typeArguments is null || typeArguments.Length == 0
                ? SurtrClassReference.Native(fullName)
                : SurtrClassReference.ConstructedNative(fullName, typeArguments);
            SurtrClassReference.TrySplitFullName(fullName, out _, out string typePath);

            var declared = new SurtrClass(
                typePath,
                SurtrValueTypeCode.Native,
                reference,
                baseClass is null ? null : TypeHandle(baseClass.SelfReference),
                isAbstract: false,
                SurtrVisibility.Public,
                declaringType: null);

            _context.NativeClasses.Add(fullName, declared);

            // Bind the handle now so anything already referring to this type by descriptor - a
            // global's declared type, another native class's member signature - picks it up.
            TypeHandle(reference);
            return declared;
        }

        /// <summary>
        /// Declares a native <b>value</b> class: the Surtr-side face of a host struct, laid out as
        /// a run of contiguous slots rather than as a heap object behind a reference.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The difference from <see cref="DefineNativeClass"/> is where the data lives. A native
        /// class is a <see cref="SurtrNativeObject"/> wrapping a CLR instance, and every member
        /// access crosses into host code to reach a field of it. A native value class is the other
        /// way round: <b>Surtr owns the storage</b>. Its fields are real slots declared with
        /// <see cref="DefineValueField"/>, so reading one is a slot read that never calls the host
        /// at all, and the CLR struct is rebuilt from those slots only when a native member
        /// actually needs one. That is what makes a host struct free to pass around - the point of
        /// mapping <c>Vector3</c> this way rather than boxing it into a proxy.
        /// </para>
        /// <para>
        /// The class is <see cref="SurtrValueTypeCode.Object"/> rather than
        /// <see cref="SurtrValueTypeCode.Native"/>, and deliberately: the type code describes the
        /// <em>boxed</em> form, and boxing an inline block produces an ordinary
        /// <see cref="SurtrInstance"/> holding those same slots - not a proxy around a CLR object
        /// that the block was never backed by. This is exactly the shape a compiled
        /// <c>value class</c> has, which is the point: nothing downstream - the linker's layout,
        /// the collector's reference-slot walk, the boxing opcodes - needs to know the declaration
        /// came from a host rather than from Surtr source.
        /// </para>
        /// <para>
        /// A value type has no identity to inherit through, so there is no base class parameter;
        /// <see cref="SurtrTypeLinker"/> refuses one outright. Returned still under construction:
        /// add its fields, hang its native methods on it, and finish it with
        /// <see cref="FinishNativeClass"/>, which is what computes the flattened width.
        /// </para>
        /// </remarks>
        /// <param name="fullName">The name its descriptor carries, for example <c>UnityEngine:Vector3</c>.</param>
        /// <param name="typeArguments">The descriptors of a closed generic construction, if any.</param>
        /// <exception cref="InvalidOperationException">A native class with that full name is already declared.</exception>
        public SurtrClass DefineNativeValueClass(string fullName, SurtrClassReference[]? typeArguments = null)
        {
            if (_context.NativeClasses.ContainsKey(fullName))
                throw new InvalidOperationException($"A native class named '{fullName}' is already declared.");

            var reference = typeArguments is null || typeArguments.Length == 0
                ? SurtrClassReference.Native(fullName)
                : SurtrClassReference.ConstructedNative(fullName, typeArguments);
            SurtrClassReference.TrySplitFullName(fullName, out _, out string typePath);

            var declared = new SurtrClass(
                typePath,
                SurtrValueTypeCode.Object,
                reference,
                baseType: null,
                isAbstract: false,
                SurtrVisibility.Public,
                declaringType: null)
            {
                IsValueType = true,
            };

            _context.NativeClasses.Add(fullName, declared);

            TypeHandle(reference);
            return declared;
        }

        /// <summary>
        /// Declares one storage field of a native value class - a real slot in its inline block,
        /// not an accessor pair into host code.
        /// </summary>
        /// <remarks>
        /// The counterpart to <see cref="DefineNativeField"/>, and the opposite trade. A native
        /// field owns no slot and reads through entry points, which is right when the CLR object
        /// is the storage; a value field <em>is</em> the storage, so reading it costs one slot
        /// access and no transition. Fields are read-only from Surtr because an inline value is
        /// immutable (a copy has no identity to write back through), and they claim their slots in
        /// declaration order - which is the order the marshaler has to rebuild the CLR struct in.
        /// </remarks>
        /// <param name="valueClass">A class declared by <see cref="DefineNativeValueClass"/>.</param>
        /// <param name="name">The field's Surtr name.</param>
        /// <param name="fieldType">The field's declared type.</param>
        /// <param name="visibility">How widely the field is visible.</param>
        /// <exception cref="ArgumentException"><paramref name="valueClass"/> is not a value class.</exception>
        public SurtrFieldInfo DefineValueField(
            SurtrClass valueClass,
            string name,
            SurtrClassReference fieldType,
            SurtrVisibility visibility = SurtrVisibility.Public)
        {
            if (valueClass is null)
                throw new ArgumentNullException(nameof(valueClass));

            if (!valueClass.IsValueType)
                throw new ArgumentException($"'{valueClass.Name}' is not a value class, so it has no inline block to place a field in.", nameof(valueClass));

            var field = new SurtrFieldInfo(
                name,
                TypeHandle(fieldType),
                isStatic: false,
                isReadOnly: true,
                visibility,
                TypeHandle(valueClass.SelfReference));

            valueClass.AddField(field);
            return field;
        }

        /// <summary>
        /// Declares a native enum: the Surtr-side face of a host enum, as a sealed class with a
        /// fixed set of named static instances.
        /// </summary>
        /// <remarks>
        /// Mirrors <see cref="DefineNativeClass"/> but builds the class with <c>isEnum: true</c>, so
        /// it carries <see cref="SurtrClass.EnumCases"/> and an exhaustive <c>switch</c> over it
        /// compiles to a dense jump table. Cases are added with
        /// <see cref="DefineNativeEnumCase(SurtrClass, string, SurtrNativeObject)"/> before
        /// <see cref="FinishNativeClass"/> links the class.
        /// </remarks>
        /// <param name="fullName">The name its descriptor carries, for example <c>Game:LogLevel</c>.</param>
        /// <param name="typeArguments">The descriptors of a closed generic construction, if any.</param>
        /// <exception cref="InvalidOperationException">A native class with that full name is already declared.</exception>
        public SurtrClass DefineNativeEnum(string fullName, SurtrClassReference[]? typeArguments = null)
        {
            if (_context.NativeClasses.ContainsKey(fullName))
                throw new InvalidOperationException($"A native class named '{fullName}' is already declared.");

            var reference = typeArguments is null || typeArguments.Length == 0
                ? SurtrClassReference.Native(fullName)
                : SurtrClassReference.ConstructedNative(fullName, typeArguments);
            SurtrClassReference.TrySplitFullName(fullName, out _, out string typePath);

            var declared = new SurtrClass(
                typePath,
                SurtrValueTypeCode.Native,
                reference,
                baseType: null,
                isAbstract: false,
                SurtrVisibility.Public,
                declaringType: null,
                isSealed: true,
                isEnum: true)
            {
                // An enum is a value class whose first field is the synthetic `value` (§2.4); the
                // linker reads this flag to lay it out as one flattened slot.
                IsValueType = true,
            };

            _context.NativeClasses.Add(fullName, declared);
            TypeHandle(reference);
            return declared;
        }

        /// <summary>
        /// Declares one case of a native enum, backed by a static field holding its value.
        /// </summary>
        /// <remarks>
        /// An enum is a value class from the migration (§2.4), so a case is its <c>value</c>: the
        /// static field is created here and the value is written into it when
        /// <see cref="FinishNativeClass"/> links the enum. No proxy, no cache — the value is an
        /// int, which is what every operation on the enum already reads.
        /// </remarks>
        /// <exception cref="ArgumentException"><paramref name="enumClass"/> is not an enum.</exception>
        /// <exception cref="InvalidOperationException">The enum is already built.</exception>
        public SurtrEnumCaseInfo DefineNativeEnumCase(SurtrClass enumClass, string name, long value)
        {
            if (enumClass is null)
                throw new ArgumentNullException(nameof(enumClass));

            if (!enumClass.IsEnum)
                throw new ArgumentException($"'{enumClass.Name}' is not an enum and cannot declare enum cases.", nameof(enumClass));

            var selfHandle = TypeHandle(enumClass.SelfReference);
            var field = new SurtrFieldInfo(name, selfHandle, isStatic: true, isReadOnly: true, SurtrVisibility.Public, selfHandle);
            var caseInfo = enumClass.AddEnumCase(field, checked((int)value));

            if (!_nativeEnumValues.TryGetValue(enumClass, out var cases))
            {
                cases = new Dictionary<string, long>(StringComparer.Ordinal);
                _nativeEnumValues.Add(enumClass, cases);
            }

            cases[name] = value;
            return caseInfo;
        }

        /// <summary>
        /// Declares a native field: a field whose value lives in the host, reached through native
        /// getter and setter entry points rather than a Surtr slot.
        /// </summary>
        /// <remarks>
        /// The getter receives the receiver (for an instance field) and returns the value; the setter
        /// receives the receiver and the value. A static native field's entry points receive no
        /// receiver. A read-only field's setter is ignored and replaced with a throwing stub.
        /// </remarks>
        /// <exception cref="InvalidOperationException">A native class with that full name is already declared.</exception>
        public SurtrNativeFieldInfo DefineNativeField(
            SurtrClass nativeClass,
            string name,
            SurtrClassReference fieldType,
            SurtrNativeEntryPoint getter,
            SurtrNativeEntryPoint setter,
            bool isStatic = false,
            bool isReadOnly = false,
            SurtrVisibility visibility = SurtrVisibility.Public)
        {
            if (nativeClass is null)
                throw new ArgumentNullException(nameof(nativeClass));

            var field = new SurtrNativeFieldInfo(
                name,
                TypeHandle(fieldType),
                isStatic,
                isReadOnly,
                visibility,
                TypeHandle(nativeClass.SelfReference),
                getter,
                setter);

            nativeClass.AddField(field);
            return field;
        }

        /// <summary>Links a native class, freezing its tables so instances can be created.</summary>
        public void FinishNativeClass(SurtrClass nativeClass)
        {
            RetryHostHandles();
            SurtrTypeLinker.LinkClass(nativeClass, ref _context.NextInterfaceId);
            SealNativeEnumValues(nativeClass);
        }

        /// <summary>
        /// Writes each pending native enum case's value into its static field, now that linking has
        /// laid the static storage out and resolved every field address. An enum case is the value
        /// itself from the migration (§2.4), so the write is one int.
        /// </summary>
        private void SealNativeEnumValues(SurtrClass enumClass)
        {
            if (!_nativeEnumValues.TryGetValue(enumClass, out var cases))
                return;

            foreach (var pair in cases)
            {
                if (enumClass.TryGetField(pair.Key, out var field))
                    *field.StaticAddress = SurtrValue.CreateInt(checked((int)pair.Value)).Raw;
            }

            _nativeEnumValues.Remove(enumClass);
        }

        /// <summary>Looks up a host-declared native class by its full name.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetNativeClass(string fullName, out SurtrClass nativeClass)
            => _context.NativeClasses.TryGetValue(fullName, out nativeClass!);

        /// <summary>
        /// Publishes the body of a native member, under the name its declaration links against.
        /// </summary>
        /// <remarks>
        /// <para>
        /// What a module carrying native members needs in order to be loadable at all when it came
        /// from an image: the image holds the name and the signature, and the address can only come
        /// from the process doing the loading. Register every body a module needs <em>before</em>
        /// loading it - a name nothing was published under fails the load, next to where an
        /// unresolved type does, and for the same reason.
        /// </para>
        /// <para>
        /// Publishing the same name twice replaces the body, which is what makes re-registering
        /// after a reload harmless rather than an error to work around.
        /// </para>
        /// </remarks>
        /// <param name="linkName">The name declarations bind against, as <see cref="SurtrNativeMethodInfo.LinkName"/> spells it.</param>
        /// <param name="entryPoint">The host function to call.</param>
        public void DefineNativeBody(string linkName, SurtrNativeEntryPoint entryPoint)
        {
            if (string.IsNullOrEmpty(linkName))
                throw new ArgumentException("A native body needs a link name to be published under.", nameof(linkName));

            if (!entryPoint.IsValid)
                throw new ArgumentException($"The body published for '{linkName}' is a null entry point.", nameof(entryPoint));

            _context.NativeBodies[linkName] = entryPoint;
        }

        /// <summary>Looks up a native member body this runtime has been given.</summary>
        public bool TryGetNativeBody(string linkName, out SurtrNativeEntryPoint entryPoint)
            => _context.NativeBodies.TryGetValue(linkName, out entryPoint);
        #endregion

        #region Execution
        // The host's whole execution surface. Each of these is one complete operation - push the
        // arguments, run, hand back the result - so the data stack is never left in a state the
        // host has to reason about, and the interpreter's frame protocol stays entirely internal.

        /// <summary>Calls a Surtr method and returns its result.</summary>
        /// <remarks>
        /// For an instance method, <paramref name="arguments"/><c>[0]</c> is the receiver: it is
        /// argument zero like any other, which is the same convention a native entry point sees.
        /// A method that returns nothing answers <see cref="SurtrValue.Null"/>.
        /// </remarks>
        /// <exception cref="VM.SurtrExecutionException">The call trapped, or raised an exception nothing caught.</exception>
        public SurtrValue Invoke(SurtrMethodInfo method, params SurtrValue[] arguments)
            => Invoke(method, (ReadOnlySpan<SurtrValue>)(arguments ?? Array.Empty<SurtrValue>()));

        /// <summary>Calls a Surtr method with arguments the caller already has in a span.</summary>
        /// <remarks>
        /// This is the boundary where the two representations meet. The callee's frame counts
        /// slots - a parameter whose type stores inline claims its whole width - while the host
        /// speaks one <see cref="SurtrValue"/> per argument; and the same in reverse for the
        /// result. Both directions translate here: inline arguments arrive as the boxed form the
        /// host holds and are flattened into their blocks before the call, and an inline result
        /// comes back boxed rather than leaving the data stack dirty.
        /// </remarks>
        public SurtrValue Invoke(SurtrMethodInfo method, ReadOnlySpan<SurtrValue> arguments)
        {
            bool nativeAnswer = method.ImplKind == SurtrMethodImplKind.Native;
            bool inlineResult = ResultSlotCount(method) > 1;

            // A bytecode method with an ordinary one-slot answer keeps the shortest path: the
            // interpreter hands back its return value directly. Everything else - natives, whose
            // answers now land on the data stack by convention, and inline blocks - goes through
            // the slot-copying boundary below.
            if (!nativeAnswer
                && !inlineResult
                && !HasInlineParameters(method))
            {
                return VirtualMachine.Call(method, arguments);
            }

            int slotCount = Math.Max(ResultSlotCount(method), nativeAnswer ? 1 : 0);
            Span<SurtrRawValue> results = slotCount switch
            {
                <= 0 => stackalloc SurtrRawValue[1],
                <= 32 => stackalloc SurtrRawValue[slotCount],
                _ => new SurtrRawValue[slotCount],
            };

            VirtualMachine.CallForResults(
                method,
                HasInlineParameters(method) ? FlattenArguments(method, arguments) : arguments,
                results);

            if (slotCount <= 1)
                return slotCount == 0 ? SurtrValue.Null : SurtrValue.FromRaw(results[0]);

            // A value-class result boxes into an ordinary instance of its own class; a tuple
            // result re-packs into the SurtrTuple the host can resolve. Either way the caller
            // gets one value back, exactly as it would have before values stored inline.
            if (method.ReturnType.ResolvedType is SurtrClass { IsValueType: true } valueClass)
            {
                var instance = NewInstance(valueClass);
                for (int i = 0; i < slotCount; i++)
                    instance[i] = SurtrValue.FromRaw(results[i]);

                return SurtrValue.CreateReference(instance.GetSurtrReference());
            }

            var elements = new SurtrValue[slotCount];
            for (int i = 0; i < slotCount; i++)
                elements[i] = SurtrValue.FromRaw(results[i]);

            var tuple = new SurtrTuple(method.ReturnType.Reference, elements);
            _context.EntityRegistry.Register(tuple);
            return SurtrValue.CreateReference(tuple.GetSurtrReference());
        }

        /// <summary>Calls a closure and returns its result.</summary>
        public SurtrValue InvokeClosure(SurtrClosure closure, params SurtrValue[] arguments)
            => InvokeClosure(closure, (ReadOnlySpan<SurtrValue>)(arguments ?? Array.Empty<SurtrValue>()));

        /// <summary>
        /// Calls a closure and returns its result - the same representation boundary
        /// <see cref="Invoke(SurtrMethodInfo, ReadOnlySpan{SurtrValue})"/> implements.
        /// </summary>
        public SurtrValue InvokeClosure(SurtrClosure closure, ReadOnlySpan<SurtrValue> arguments)
        {
            int slotCount = closure.TargetMethod.ResultSlotCount;
            Span<SurtrRawValue> results = slotCount switch
            {
                <= 0 => stackalloc SurtrRawValue[1],
                <= 32 => stackalloc SurtrRawValue[slotCount],
                _ => new SurtrRawValue[slotCount],
            };

            VirtualMachine.CallClosureForResults(closure, arguments, results);

            if (slotCount <= 1)
                return slotCount == 0 ? SurtrValue.Null : SurtrValue.FromRaw(results[0]);

            var elements = new SurtrValue[slotCount];
            for (int i = 0; i < slotCount; i++)
                elements[i] = SurtrValue.FromRaw(results[i]);

            if (closure.TargetMethod.ReturnType.Reference.TypeCode == SurtrValueTypeCode.Tuple)
            {
                var tuple = new SurtrTuple(closure.TargetMethod.ReturnType.Reference, elements);
                _context.EntityRegistry.Register(tuple);
                return SurtrValue.CreateReference(tuple.GetSurtrReference());
            }

            if (closure.TargetMethod.ReturnType.ResolvedType is SurtrClass { IsValueType: true } valueClass)
            {
                var instance = NewInstance(valueClass);
                for (int i = 0; i < slotCount; i++)
                    instance[i] = elements[i];

                return SurtrValue.CreateReference(instance.GetSurtrReference());
            }

            throw new InvalidOperationException(
                $"A closure returning '{closure.TargetMethod.ReturnType.Reference.Descriptor}' cannot answer {slotCount} slots.");
        }

        /// <summary>
        /// Calls a method and copies every slot of its result into <paramref name="results"/>.
        /// </summary>
        /// <remarks>
        /// The multi-slot shape of <see cref="Invoke(SurtrMethodInfo, ReadOnlySpan{SurtrValue})"/>:
        /// a method whose declared return type occupies more than one slot answers through here,
        /// one slot per element, deepest field first - the same order the callee's
        /// <c>ReturnValues</c> writes them. A single-value or void method works too, answering one
        /// or zero slots, so a host written against this overload never needs the other form.
        /// </remarks>
        /// <returns>
        /// <see langword="false"/> only when the method's result does not fit
        /// <paramref name="results"/>; the call still ran.
        /// </returns>
        /// <exception cref="VM.SurtrExecutionException">The call trapped, or raised an exception nothing caught.</exception>
        public bool TryInvoke(SurtrMethodInfo method, ReadOnlySpan<SurtrValue> arguments, Span<SurtrValue> results)
        {
            int slotCount = ResultSlotCount(method);
            if (slotCount == 0)
            {
                VirtualMachine.Call(method, arguments);
                return true;
            }

            if (results.Length < slotCount)
                return false;

            Span<SurtrRawValue> raw = slotCount <= 32 ? stackalloc SurtrRawValue[slotCount] : new SurtrRawValue[slotCount];
            VirtualMachine.CallForResults(method, arguments, raw);

            for (int i = 0; i < slotCount; i++)
                results[i] = SurtrValue.FromRaw(raw[i]);

            return true;
        }

        /// <summary>
        /// How many data-stack slots one call to <paramref name="method"/> leaves behind: zero for
        /// void, the flattened width of an inline return (a value type or a tuple), one for
        /// everything else.
        /// </summary>
        private static int ResultSlotCount(SurtrMethodInfo method) => method.ResultSlotCount;

        /// <summary>
        /// Whether any declared parameter stores inline - which is what makes the host's one
        /// <c>SurtrValue</c>-per-argument shape need translating before a call.
        /// </summary>
        private static bool HasInlineParameters(SurtrMethodInfo method)
        {
            foreach (var parameter in method.Parameters)
            {
                if (parameter.IsVarargs)
                    continue;

                var reference = parameter.ParameterType.Reference;
                if (reference.TypeCode == SurtrValueTypeCode.Tuple && SlotWidthOf(reference) > 0)
                    return true;

                if (parameter.ParameterType.ResolvedType is SurtrClass { IsValueType: true } value
                    && value.FlattenedSlotWidth > 1)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Translates the host's boxed argument values into the slot layout the callee's frame
        /// counts: every inline parameter's packed value is flattened into its consecutive slots.
        /// The receiver of an instance method is never translated - it is an ordinary object.
        /// </summary>
        private SurtrValue[] FlattenArguments(SurtrMethodInfo method, ReadOnlySpan<SurtrValue> arguments)
        {
            bool hasReceiver = !method.IsStatic && method.DeclaringType is not null;
            var flat = new List<SurtrValue>(arguments.Length + 4);

            for (int i = 0; i < arguments.Length; i++)
            {
                int parameterIndex = hasReceiver ? i - 1 : i;

                if (parameterIndex < 0 || parameterIndex >= method.Parameters.Length)
                {
                    flat.Add(arguments[i]);
                    continue;
                }

                var parameter = method.Parameters[parameterIndex];
                if (parameter.IsVarargs || !TryFlattenInline(parameter.ParameterType, arguments[i], flat))
                    flat.Add(arguments[i]);
            }

            return flat.ToArray();
        }

        /// <summary>
        /// Flattens one boxed inline value into its slots, answering whether it did. Anything not
        /// storing inline - or not actually holding the boxed form - passes through untouched.
        /// </summary>
        private bool TryFlattenInline(SurtrTypeHandle type, SurtrValue value, List<SurtrValue> flat)
        {
            var reference = type.Reference;

            if (reference.TypeCode == SurtrValueTypeCode.Tuple && Resolve<SurtrTuple>(value) is SurtrTuple tuple)
            {
                var elements = reference.GetTupleElementTypes();
                for (int i = 0; i < elements.Length && i < tuple.Length; i++)
                {
                    if (!TryFlattenReference(elements[i], tuple[i], flat))
                        flat.Add(tuple[i]);
                }

                return true;
            }

            if (type.ResolvedType is SurtrClass { IsValueType: true, FlattenedSlotWidth: > 1 } valueClass
                && Resolve<SurtrInstance>(value) is SurtrInstance instance)
            {
                for (int i = 0; i < valueClass.FlattenedSlotWidth && i < instance.SlotCount; i++)
                    flat.Add(instance[i]);

                return true;
            }

            return false;
        }

        /// <summary>The element form of <see cref="TryFlattenInline"/>, driven by a bare descriptor.</summary>
        private bool TryFlattenReference(SurtrClassReference reference, SurtrValue value, List<SurtrValue> flat)
        {
            if (reference.TypeCode == SurtrValueTypeCode.Tuple && Resolve<SurtrTuple>(value) is SurtrTuple nested)
            {
                var elements = reference.GetTupleElementTypes();
                for (int i = 0; i < elements.Length && i < nested.Length; i++)
                {
                    if (!TryFlattenReference(elements[i], nested[i], flat))
                        flat.Add(nested[i]);
                }

                return true;
            }

            // A class-typed element only widens the block when its linked layout says so; without
            // a resolved handle there is nothing to consult, and one slot is the safe answer.
            return false;
        }

        /// <summary>The flattened width of a tuple descriptor: the sum of its elements' own widths.</summary>
        private static int SlotWidthOf(SurtrClassReference reference)
        {
            const int maxSlots = 254;
            int total = 0;

            foreach (var element in reference.GetTupleElementTypes())
            {
                total += element.TypeCode == SurtrValueTypeCode.Tuple ? SlotWidthOf(element) : 1;

                if (total > maxSlots)
                    throw new InvalidOperationException(
                        $"The tuple '{reference.Descriptor}' flattens to more than {maxSlots} slots.");
            }

            return total;
        }

        /// <summary>
        /// Discards whatever a failed call left on the interpreter's stacks.
        /// </summary>
        /// <remarks>
        /// An exception that escapes <see cref="Invoke(SurtrMethodInfo, SurtrValue[])"/> leaves the
        /// machine mid-frame, because unwinding stopped as soon as it was clear no handler would
        /// match. A host that intends to keep using the runtime after catching one calls this first.
        /// </remarks>
        public void ResetExecution() => _virtualMachine?.Reset();
        #endregion

        #region Garbage Collection
        /// <summary>
        /// Keeps <paramref name="entity"/> alive regardless of whether anything can reach it.
        /// </summary>
        /// <remarks>
        /// What a host uses to hold on to an object across calls, in place of the VM stack that
        /// would otherwise be rooting it. Rooting is permanent until
        /// <see cref="RemoveRoot"/>.
        /// </remarks>
        /// <exception cref="ArgumentException"><paramref name="entity"/> is not registered.</exception>
        public void AddRoot(SurtrRuntimeEntity entity)
        {
            SurtrRef reference = entity.GetSurtrReference();
            if (reference == SurtrValue.NullRef)
                throw new ArgumentException("Only a registered entity can be rooted.", nameof(entity));

            _context.AddRoot(SurtrValue.CreateReference(reference).Raw);
        }

        /// <summary>Stops keeping <paramref name="entity"/> alive unconditionally.</summary>
        /// <returns><see langword="false"/> if it was not rooted.</returns>
        public bool RemoveRoot(SurtrRuntimeEntity entity)
            => _context.RemoveRoot(SurtrValue.CreateReference(entity.GetSurtrReference()).Raw);

        /// <summary>
        /// Replaces the policy the collector runs under.
        /// </summary>
        /// <remarks>
        /// A runtime collects on its own by default; pass <see cref="SurtrGcPolicy.Manual"/> to
        /// restore the purely host-driven behaviour. The policy is folded at configuration time, so
        /// a manual runtime's allocation path costs no more than it ever did. See
        /// <see cref="SurtrGcPolicy"/>.
        /// </remarks>
        public void ConfigureGc(in SurtrGcPolicy policy)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SurtrRuntime));

            _context.EntityRegistry.ConfigurePolicy(policy);
        }

        /// <summary>The policy this runtime's collector currently runs under.</summary>
        public SurtrGcPolicy GcPolicy
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _context.EntityRegistry.Policy;
        }

        /// <summary>How many entities were registered since the last collection.</summary>
        public long AllocationsSinceLastCollection
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _context.EntityRegistry.AllocationsSinceLastCollection;
        }

        /// <summary>
        /// Collects unreachable objects, tracing from the host globals and the explicit roots.
        /// </summary>
        /// <remarks>
        /// Traces the interpreter's data stack too, whenever this runtime has a
        /// <see cref="VirtualMachine"/>. That is what makes this safe to call from inside a native
        /// entry point: everything the interpreter is holding lives on that one stack, and the
        /// machine publishes its top before every transfer into host code. A runtime that has never
        /// executed anything has no stack to scan and takes the same path as before.
        /// </remarks>
        /// <param name="fullCollection">
        /// <see langword="true"/> to sweep every unreachable object; <see langword="false"/> to
        /// spare anything that has already survived a collection.
        /// </param>
        /// <returns>How many objects were released.</returns>
        public int Collect(bool fullCollection = true)
        {
            var machine = _virtualMachine;
            if (machine is null)
                return Collect(null, null, ReadOnlySpan<SurtrRawValue>.Empty, fullCollection);

            // The frame roots carry the closures the live frames are running, which InvokeClosure
            // takes off the stack before entering their bodies.
            return Collect(machine.StackBase, machine.StackTop, machine.FrameRoots, fullCollection);
        }

        /// <summary>
        /// Collects unreachable objects, tracing from an evaluation stack as well as from the host
        /// globals and the explicit roots.
        /// </summary>
        /// <param name="stackStart">The first slot of the evaluation stack, or null if none is live.</param>
        /// <param name="stackTop">One past the last live slot of the evaluation stack.</param>
        /// <param name="extraRoots">Anything else being kept alive for the duration of the call.</param>
        /// <param name="fullCollection">Whether to sweep everything unreachable, or spare survivors.</param>
        /// <returns>How many objects were released.</returns>
        public int Collect(
            SurtrRawValue* stackStart,
            SurtrRawValue* stackTop,
            ReadOnlySpan<SurtrRawValue> extraRoots,
            bool fullCollection)
        {
            // The runtime's own roots (interned strings, anything the host pinned) and the
            // caller's transient ones have to reach the collector as one span. Staging the
            // transients in the root buffer's slack past RootCount keeps that free of an
            // allocation on every collection; RootCount is untouched, so the borrowed tail simply
            // stops existing when this returns.
            int rootCount = _context.RootCount;
            int extraCount = extraRoots.Length;

            if (extraCount > 0)
            {
                _context.EnsureRootCapacity(rootCount + extraCount);
                extraRoots.CopyTo(new Span<SurtrRawValue>(_context.Roots, rootCount, extraCount));
            }

            return _context.EntityRegistry.CollectGarbage(
                stackStart,
                stackTop,
                _context.LiveStaticBlocks,
                new ReadOnlySpan<SurtrRawValue>(_context.Roots, 0, rootCount + extraCount),
                fullCollection);
        }

        /// <summary>
        /// Runs the collection a policy has asked for, at a machine safepoint.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The interpreter's only job on its hot path is to arm a flag (see
        /// <see cref="SurtrEntityRegistry.GcPending"/>); the sweep itself is deferred here, to a
        /// native boundary or the dispatch backstop, where the machine has already published its
        /// stack top and every value the program is using is on the stack. That is the same
        /// contract <see cref="Collect(bool)"/> relies on, so a policy-driven sweep is a plain
        /// call into it with the full/nursery choice the policy's <see cref="SurtrGcPolicy.NurseryFrequency"/>
        /// dictates.
        /// </para>
        /// <para>
        /// Cold by construction: it is only reached once the pending flag is armed, so keeping the
        /// body out of the dispatch loop's register allocation is free.
        /// </para>
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void CollectAtSafepoint()
        {
            ref SurtrEntityRegistry registry = ref _context.EntityRegistry;
            if (!registry.GcPending)
                return;

            // The sweep drains the flag and resets the allocation counter, so a single call covers
            // both the threshold that armed it and the pressure behind a nested arm.
            Collect(registry.ShouldCollectFull());
        }

        /// <summary>Statistics about the collections this runtime has run.</summary>
        public long TotalCollections
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _context.EntityRegistry.TotalCollections;
        }

        /// <summary>How many of the collections this runtime has run were full sweeps.</summary>
        public long TotalFullCollections
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _context.EntityRegistry.TotalFullCollections;
        }

        /// <summary>How many of the collections this runtime has run were nursery sweeps.</summary>
        public long TotalNurseryCollections
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _context.EntityRegistry.TotalNurseryCollections;
        }

        /// <summary>How long the most recent collection took.</summary>
        public double LastCollectionMilliseconds
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _context.EntityRegistry.LastCollectionElapsedMilliseconds;
        }
        #endregion

        /// <summary>Releases the runtime's heap, its loaded modules and every unmanaged buffer under them.</summary>
        public void Dispose()
        {
            ReleaseResources();
            GC.SuppressFinalize(this);
        }

        private void ReleaseResources()
        {
            if (_disposed)
                return;

            _disposed = true;
            _virtualMachine?.Dispose();
            _virtualMachine = null;
            _context.Dispose();
        }
    }
}
