#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;
using System;
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
        private bool _disposed;

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
            get => _virtualMachine ??= new SurtrVirtualMachine(this);
        }

        /// <summary>The host's global variables and functions.</summary>
        public SurtrNativeGlobalTable Globals
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _context.Globals;
        }

        /// <summary>How many objects the heap currently holds room for.</summary>
        public int HeapCapacity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _context.EntityRegistry.Capacity;
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

        /// <summary>Builds a closure over <paramref name="method"/>, capturing <paramref name="upValues"/> by value.</summary>
        /// <exception cref="ArgumentException"><paramref name="method"/> has no body.</exception>
        public SurtrClosure NewClosure(SurtrMethodInfo method, SurtrValue[]? upValues = null, SurtrClassReference typeReference = default)
        {
            var value = new SurtrClosure(
                typeReference.IsValid ? typeReference : method.ToSignature(),
                method,
                upValues ?? Array.Empty<SurtrValue>());

            _context.EntityRegistry.Register(value);
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
        #endregion

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
        #endregion

        #region Type Handles
        /// <summary>
        /// Interns a type handle for a host-declared signature and resolves it if it can be
        /// resolved yet.
        /// </summary>
        /// <remarks>
        /// Built-ins bind immediately, since they exist before any runtime does. A reference to a
        /// Surtr class only binds once its module is loaded, so a handle taken before that stays
        /// unresolved and is picked up by <see cref="LoadModule"/>.
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

        /// <summary>
        /// Binds <paramref name="handle"/> to the type its descriptor names, if that type is
        /// known.
        /// </summary>
        /// <returns><see langword="true"/> if the handle is resolved when this returns.</returns>
        internal bool TryResolveHandle(SurtrTypeHandle handle)
        {
            if (handle.IsResolved)
                return true;

            var reference = handle.Reference;
            var typeCode = reference.TypeCode;

            // Every built-in family collapses onto one shared class, whatever it is parameterised
            // by: AI and AS are both the array class, because an array's element type is a static
            // fact the compiler enforces, not something the object carries.
            if (typeCode.IsPrimitive || typeCode.IsBuiltIn || typeCode.IsVoid || typeCode.IsErased)
            {
                handle.Resolve(SurtrBuiltIns.ForTypeCode(typeCode));
                return true;
            }

            if (!reference.TryGetFullName(out string fullName))
                return false;

            if (typeCode.IsNative)
            {
                if (!_context.NativeClasses.TryGetValue(fullName, out var nativeClass))
                    return false;

                handle.Resolve(nativeClass);
                return true;
            }

            if (!typeCode.IsObject)
                return false;

            SurtrClassReference.TrySplitFullName(fullName, out string modulePath, out string typePath);
            if (!_context.Modules.TryGetValue(modulePath, out var module))
                return false;

            var declared = module.FindClass(typePath);
            if (declared is not null)
            {
                handle.Resolve(declared);
                return true;
            }

            // Interfaces are named by the same O-descriptor as classes - a type reference does not
            // know in advance which one it will turn out to be - so a name that is not a class is
            // tried as one before giving up.
            if (module.TryGetInterface(typePath, out var contract))
            {
                handle.Resolve(contract);
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

                SurtrTypeLinker.LinkModule(module, ref _context.NextInterfaceId);

                MaterializeStringConstants(module);
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
        /// <exception cref="InvalidOperationException">A native class with that full name is already declared.</exception>
        public SurtrClass DefineNativeClass(string fullName, SurtrClass? baseClass = null)
        {
            if (_context.NativeClasses.ContainsKey(fullName))
                throw new InvalidOperationException($"A native class named '{fullName}' is already declared.");

            if (baseClass is not null && baseClass.TypeCode != SurtrValueTypeCode.Native)
                throw new ArgumentException($"'{baseClass.Name}' is not a native class.", nameof(baseClass));

            var reference = SurtrClassReference.Native(fullName);
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

        /// <summary>Links a native class, freezing its tables so instances can be created.</summary>
        public void FinishNativeClass(SurtrClass nativeClass)
        {
            RetryHostHandles();
            SurtrTypeLinker.LinkClass(nativeClass, ref _context.NextInterfaceId);
        }

        /// <summary>Looks up a host-declared native class by its full name.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetNativeClass(string fullName, out SurtrClass nativeClass)
            => _context.NativeClasses.TryGetValue(fullName, out nativeClass!);

        /// <summary>Publishes a host global variable and freezes its table index.</summary>
        public SurtrNativeGlobalVariable DefineGlobal(string name, SurtrClassReference variableType, bool isReadOnly = false)
        {
            var variable = new SurtrNativeGlobalVariable(name, TypeHandle(variableType), isReadOnly);
            _context.Globals.Register(variable);
            return variable;
        }

        /// <summary>Publishes a host global function and freezes its table index.</summary>
        public SurtrNativeGlobalFunction DefineGlobalFunction(
            string name,
            SurtrClassReference returnType,
            SurtrParameterInfo[] parameters,
            SurtrNativeEntryPoint entryPoint)
        {
            var function = new SurtrNativeGlobalFunction(name, TypeHandle(returnType), parameters, entryPoint);
            _context.Globals.Register(function);
            return function;
        }
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
            => VirtualMachine.Call(method, arguments ?? Array.Empty<SurtrValue>());

        /// <summary>Calls a Surtr method with arguments the caller already has in a span.</summary>
        public SurtrValue Invoke(SurtrMethodInfo method, ReadOnlySpan<SurtrValue> arguments)
            => VirtualMachine.Call(method, arguments);

        /// <summary>Calls a closure and returns its result.</summary>
        public SurtrValue InvokeClosure(SurtrClosure closure, params SurtrValue[] arguments)
            => VirtualMachine.CallClosure(closure, arguments ?? Array.Empty<SurtrValue>());

        /// <summary>Calls a closure with arguments the caller already has in a span.</summary>
        public SurtrValue InvokeClosure(SurtrClosure closure, ReadOnlySpan<SurtrValue> arguments)
            => VirtualMachine.CallClosure(closure, arguments);

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
            var globals = _context.Globals;

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
                globals.VariableTable.Pointer,
                globals.ReferenceVariableSlots.Pointer,
                globals.ReferenceVariableSlots.Length,
                _context.LiveStaticBlocks,
                new ReadOnlySpan<SurtrRawValue>(_context.Roots, 0, rootCount + extraCount),
                fullCollection);
        }

        /// <summary>Statistics about the collections this runtime has run.</summary>
        public long TotalCollections
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _context.EntityRegistry.TotalCollections;
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
