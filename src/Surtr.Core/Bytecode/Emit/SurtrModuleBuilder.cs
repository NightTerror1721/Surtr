#nullable enable

using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Runtime.Utilities;
using System;
using System.Collections.Generic;

namespace Surtr.Bytecode.Emit
{
    /// <summary>
    /// Builds a whole module: its constant pool, its access tables, everything it declares, and
    /// the single instruction stream every one of its bodies lives in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order this class enforces is not a style choice, it falls out of how the runtime is
    /// wired. A module's compiled code is one contiguous block and a method is an offset into it,
    /// and <see cref="SurtrBytecodeMethodInfo"/> reads that offset when it is <em>constructed</em> -
    /// so no method metadata can exist until every body in the module has been emitted and laid
    /// out. Meanwhile a call site has to name its target while emitting. The two are reconciled by
    /// handing out a method table slot at declaration time (<see cref="SurtrMethodBuilder.Token"/>)
    /// and filling that slot in <see cref="Build"/>.
    /// </para>
    /// <para>
    /// So the shape of using this is always: declare types and members, emit bodies against the
    /// tokens, call <see cref="Build"/>, then hand the module to
    /// <c>SurtrRuntime.LoadModule</c> - which resolves the type handles, links every type, interns
    /// the string literals into the pool and runs the static initializers.
    /// </para>
    /// <para>
    /// Every pool and table deduplicates, so a compiler can ask for the same type, field, method or
    /// literal as many times as it likes and get the same slot back. That matters beyond size: the
    /// low pool indices have dedicated single-byte opcodes behind them, and duplicates would push
    /// real entries out of that range.
    /// </para>
    /// </remarks>
    public sealed class SurtrModuleBuilder
    {
        private readonly SurtrModule _module;

        private readonly List<SurtrRawValue> _constants = new List<SurtrRawValue>();
        private readonly Dictionary<SurtrRawValue, int> _constantSlots = new Dictionary<SurtrRawValue, int>();

        private readonly List<string> _stringLiterals = new List<string>();
        private readonly List<int> _stringSlots = new List<int>();
        private readonly Dictionary<string, int> _stringByText = new Dictionary<string, int>(StringComparer.Ordinal);

        private readonly List<SurtrTypeHandle> _typeTable = new List<SurtrTypeHandle>();
        private readonly List<string> _nativeVariableImports = new List<string>();
        private readonly Dictionary<string, int> _nativeVariableSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<string> _nativeFunctionImports = new List<string>();
        private readonly Dictionary<string, int> _nativeFunctionSlots = new Dictionary<string, int>(StringComparer.Ordinal);

        private readonly Dictionary<string, int> _typeSlots = new Dictionary<string, int>(StringComparer.Ordinal);

        private readonly List<SurtrFieldInfo> _fieldTable = new List<SurtrFieldInfo>();
        private readonly Dictionary<SurtrFieldInfo, int> _fieldSlots = new Dictionary<SurtrFieldInfo, int>();

        private readonly List<SurtrMethodInfo?> _methodTable = new List<SurtrMethodInfo?>();
        private readonly List<SurtrMethodBuilder?> _methodTablePending = new List<SurtrMethodBuilder?>();
        private readonly Dictionary<SurtrMethodInfo, int> _methodSlots = new Dictionary<SurtrMethodInfo, int>();

        private readonly List<SurtrModule> _moduleTable = new List<SurtrModule>();
        private readonly Dictionary<SurtrModule, int> _moduleSlots = new Dictionary<SurtrModule, int>();

        private readonly HashSet<SurtrMethodInfo> _interfaceMethods = new HashSet<SurtrMethodInfo>();

        private readonly List<SurtrMethodBuilder> _functions = new List<SurtrMethodBuilder>();
        private readonly List<SurtrClassBuilder> _classes = new List<SurtrClassBuilder>();
        private readonly List<SurtrInterfaceBuilder> _interfaces = new List<SurtrInterfaceBuilder>();
        private readonly List<SurtrPropertyBuilder> _properties = new List<SurtrPropertyBuilder>();

        private bool _built;

        /// <summary>Starts a new module at <paramref name="path"/>.</summary>
        public SurtrModuleBuilder(string path) : this(new SurtrModule(path)) { }

        /// <summary>Builds into an existing, still-empty module.</summary>
        public SurtrModuleBuilder(SurtrModule module)
        {
            _module = module ?? throw new ArgumentNullException(nameof(module));

            if (module.IsBuilt)
                throw new ArgumentException($"Module '{module.Path}' is already built.", nameof(module));
        }

        /// <summary>The module being filled in.</summary>
        public SurtrModule Module => _module;

        /// <summary>The module's dot-separated path.</summary>
        public string Path => _module.Path;

        /// <summary>Whether <see cref="Build"/> has run.</summary>
        public bool IsBuilt => _built;

        #region Constant pool

        /// <summary>Interns a raw value into the constant pool.</summary>
        /// <remarks>
        /// Deduplicated by the encoded bits, which is exactly the right granularity: two values
        /// that encode identically are the same constant, and <c>0.0</c> and <c>-0.0</c> do not.
        /// </remarks>
        public SurtrConstantToken Constant(SurtrValue value) => ConstantRaw(value.Raw);

        /// <summary>Interns an integer constant.</summary>
        /// <remarks>
        /// Rarely what a compiler wants: <see cref="SurtrCodeEmitter.LoadInt"/> carries small
        /// integers inline and costs no slot at all.
        /// </remarks>
        public SurtrConstantToken ConstantInt(int value) => ConstantRaw(SurtrValue.CreateInt(value).Raw);

        /// <summary>Interns a float constant.</summary>
        public SurtrConstantToken ConstantFloat(double value) => ConstantRaw(SurtrValue.CreateFloat(value).Raw);

        /// <summary>Interns a boolean constant.</summary>
        public SurtrConstantToken ConstantBool(bool value) => ConstantRaw(SurtrValue.CreateBool(value).Raw);

        /// <summary>Interns a character constant.</summary>
        public SurtrConstantToken ConstantChar(char value) => ConstantRaw(SurtrValue.CreateChar(value).Raw);

        /// <summary>
        /// Reserves a pool slot for a string literal, to be filled with a real string object when
        /// the module is loaded.
        /// </summary>
        /// <remarks>
        /// A string cannot be emitted as a constant the way an integer can: what bytecode needs is
        /// a reference, and a reference only exists once an object has been registered with a
        /// runtime's heap - which happens at load, long after the chunk was built. So the slot is
        /// reserved here and the text recorded alongside it; loading interns the text and patches
        /// the reference in, leaving <c>Ldc</c> as one indexed load that knows nothing about
        /// strings.
        /// </remarks>
        public SurtrConstantToken StringLiteral(string text)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));

            if (_stringByText.TryGetValue(text, out int existing))
                return new SurtrConstantToken(existing);

            ThrowIfBuilt();

            // Deliberately not registered in the raw-value index: the placeholder written here is
            // overwritten at load, so matching another constant against it would alias two
            // unrelated entries.
            int slot = _constants.Count;
            _constants.Add(SurtrValue.Null.Raw);

            _stringLiterals.Add(text);
            _stringSlots.Add(slot);
            _stringByText.Add(text, slot);

            return new SurtrConstantToken(slot);
        }

        private SurtrConstantToken ConstantRaw(SurtrRawValue raw)
        {
            if (_constantSlots.TryGetValue(raw, out int existing))
                return new SurtrConstantToken(existing);

            ThrowIfBuilt();

            int slot = _constants.Count;
            _constants.Add(raw);
            _constantSlots.Add(raw, slot);
            return new SurtrConstantToken(slot);
        }

        #endregion

        #region Access tables

        /// <summary>
        /// Interns a type handle for <paramref name="reference"/> without claiming a table slot.
        /// </summary>
        /// <remarks>
        /// For types a member's metadata names - a field's type, a catch type - as opposed to types
        /// an instruction names. The module's handle table is its dependency list either way, so
        /// loading resolves this too; it just does not need an index the bytecode can carry.
        /// </remarks>
        public SurtrTypeHandle TypeHandle(SurtrClassReference reference) => _module.TypeHandles.GetOrAdd(reference);

        /// <summary>Builds metadata for a required parameter against this module's handle table.</summary>
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
        /// Declares a host global this module reads or writes as a <c>native</c> variable, and
        /// interns it into the module's import table.
        /// </summary>
        /// <remarks>
        /// Nothing is resolved here: the name is checked against the host's globals when the module
        /// is loaded, and a name nobody registered fails the load rather than the instruction that
        /// would have reached it.
        /// </remarks>
        public SurtrNativeVariableToken NativeVariable(string name)
        {
            if (name is null)
                throw new ArgumentNullException(nameof(name));

            if (_nativeVariableSlots.TryGetValue(name, out int existing))
                return new SurtrNativeVariableToken(existing);

            ThrowIfBuilt();

            int slot = _nativeVariableImports.Count;
            _nativeVariableImports.Add(name);
            _nativeVariableSlots.Add(name, slot);
            return new SurtrNativeVariableToken(slot);
        }

        /// <summary>Declares a host function this module calls, and interns it into the import table.</summary>
        public SurtrNativeFunctionToken NativeFunction(string name)
        {
            if (name is null)
                throw new ArgumentNullException(nameof(name));

            if (_nativeFunctionSlots.TryGetValue(name, out int existing))
                return new SurtrNativeFunctionToken(existing);

            ThrowIfBuilt();

            int slot = _nativeFunctionImports.Count;
            _nativeFunctionImports.Add(name);
            _nativeFunctionSlots.Add(name, slot);
            return new SurtrNativeFunctionToken(slot);
        }

        /// <summary>
        /// Builds an attribute usage against this module's handle table, ready to be attached to a
        /// declaration.
        /// </summary>
        /// <remarks>
        /// The arguments fill the attribute class's fields positionally, and the instance is built
        /// when the module loads - not here, and not on first read.
        /// </remarks>
        public SurtrAttributeUsage Attribute(SurtrClassReference attributeType, params SurtrConstant[] arguments)
            => new SurtrAttributeUsage(TypeHandle(attributeType), arguments);

        /// <summary>Interns a type into the chunk's type access table.</summary>
        public SurtrTypeToken Type(SurtrClassReference reference)
        {
            string descriptor = reference.Descriptor;

            if (_typeSlots.TryGetValue(descriptor, out int existing))
                return new SurtrTypeToken(existing);

            ThrowIfBuilt();

            int slot = _typeTable.Count;
            _typeTable.Add(TypeHandle(reference));
            _typeSlots.Add(descriptor, slot);
            return new SurtrTypeToken(slot);
        }

        /// <summary>Interns an already-known type into the chunk's type access table.</summary>
        public SurtrTypeToken Type(SurtrTypeInfo type)
        {
            if (type is null)
                throw new ArgumentNullException(nameof(type));

            var token = Type(type.SelfReference);
            var handle = _typeTable[token.Index];

            // A host class or a built-in is a real object here and now, so the handle can be bound
            // straight away rather than waiting for the load-time resolver to find it by name.
            if (!handle.IsResolved)
                handle.Resolve(type);

            return token;
        }

        /// <summary>Interns a field into the chunk's field access table.</summary>
        public SurtrFieldToken Field(SurtrFieldInfo field)
        {
            if (field is null)
                throw new ArgumentNullException(nameof(field));

            if (_fieldSlots.TryGetValue(field, out int existing))
                return new SurtrFieldToken(existing);

            ThrowIfBuilt();

            int slot = _fieldTable.Count;
            _fieldTable.Add(field);
            _fieldSlots.Add(field, slot);
            return new SurtrFieldToken(slot);
        }

        /// <summary>Interns an already-built method into the chunk's method access table.</summary>
        public SurtrMethodToken Method(SurtrMethodInfo method)
        {
            if (method is null)
                throw new ArgumentNullException(nameof(method));

            if (_methodSlots.TryGetValue(method, out int existing))
                return new SurtrMethodToken(existing);

            ThrowIfBuilt();

            int slot = _methodTable.Count;
            _methodTable.Add(method);
            _methodTablePending.Add(null);
            _methodSlots.Add(method, slot);
            return new SurtrMethodToken(slot);
        }

        /// <summary>The table slot a method declared on this builder already holds.</summary>
        public SurtrMethodToken Method(SurtrMethodBuilder method)
            => method is null ? throw new ArgumentNullException(nameof(method)) : method.Token;

        /// <summary>Interns another module into the chunk's module access table.</summary>
        public SurtrModuleToken ModuleReference(SurtrModule other)
        {
            if (other is null)
                throw new ArgumentNullException(nameof(other));

            if (ReferenceEquals(other, _module))
                throw new ArgumentException("A module does not reach its own functions through the module table; use CallLocalModule.", nameof(other));

            if (_moduleSlots.TryGetValue(other, out int existing))
                return new SurtrModuleToken(existing);

            ThrowIfBuilt();

            int slot = _moduleTable.Count;
            _moduleTable.Add(other);
            _moduleSlots.Add(other, slot);
            return new SurtrModuleToken(slot);
        }

        /// <summary>
        /// Names a function in another module: which module, and which of <em>its</em> method table
        /// entries.
        /// </summary>
        /// <remarks>
        /// The second index is the callee's, not this module's, because a cross-module call reads
        /// the target's own table. The lookup is a scan, which is fine at build time and is the
        /// reason every method a builder declares is put in its module's table whether or not
        /// anything local calls it.
        /// </remarks>
        /// <exception cref="ArgumentException">The target module has not been built, or does not carry that method in its table.</exception>
        public SurtrExternalMethodToken ExternalMethod(SurtrModule other, SurtrMethodInfo callee)
        {
            if (other is null)
                throw new ArgumentNullException(nameof(other));

            if (callee is null)
                throw new ArgumentNullException(nameof(callee));

            var token = ModuleReference(other);
            var table = other.Chunk.MethodTable;

            for (int i = 0; i < table.Length; i++)
            {
                if (ReferenceEquals(table[i], callee))
                    return new SurtrExternalMethodToken(token.Index, i);
            }

            // A module read from an image has a method table sized but empty until it is loaded:
            // its entries travel as names and are bound against the runtime (§3.3). A compiler
            // referencing that image has nothing to compare by identity, so it matches the pending
            // entry instead — the same slot, named rather than resolved, which is exactly what the
            // instruction encodes.
            var pending = other.Chunk.PendingMethods;
            string wanted = callee.SignatureKey();

            for (int i = 0; i < pending.Length; i++)
            {
                if (table[i] is not null || pending[i].OwnerDescriptor is not null)
                    continue;

                if (string.Equals(pending[i].Name, callee.Name, StringComparison.Ordinal)
                    && string.Equals(pending[i].SignatureKey, wanted, StringComparison.Ordinal))
                {
                    return new SurtrExternalMethodToken(token.Index, i);
                }
            }

            throw new ArgumentException(
                $"Module '{other.Path}' does not carry '{callee.Name}' in its method table, so no cross-module call can name it.",
                nameof(callee));
        }

        /// <summary>Whether <paramref name="method"/> was declared on an interface built here.</summary>
        /// <remarks>
        /// A method's declaring type is a descriptor, and a descriptor does not say whether it
        /// names a class or a contract until the module is loaded - so this record is what lets
        /// <see cref="SurtrCodeEmitter.Call(SurtrMethodInfo, bool)"/> reach for
        /// <c>InvokeInterface</c> on its own.
        /// </remarks>
        public bool IsInterfaceMethod(SurtrMethodInfo method) => _interfaceMethods.Contains(method);

        internal void RegisterInterfaceMethod(SurtrMethodInfo method)
        {
            _interfaceMethods.Add(method);
            Method(method);
        }

        internal void RegisterMethod(SurtrMethodBuilder method)
        {
            ThrowIfBuilt();

            int slot = _methodTable.Count;
            _methodTable.Add(null);
            _methodTablePending.Add(method);
            method.AssignToken(new SurtrMethodToken(slot));
        }

        #endregion

        #region Declaration

        /// <summary>
        /// Declares a module-level variable.
        /// </summary>
        /// <remarks>
        /// Surtr has no true globals, so a module-level variable is a static of its module and
        /// reaches its storage through <c>StaticFieldGet</c>/<c>StaticFieldSet</c> exactly as a
        /// class static does.
        /// </remarks>
        public SurtrFieldInfo DefineVariable(
            string name,
            SurtrClassReference variableType,
            bool isReadOnly = false,
            SurtrVisibility visibility = SurtrVisibility.Public)
        {
            var field = new SurtrFieldInfo(name, TypeHandle(variableType), true, isReadOnly, visibility, null);
            _module.AddField(field);
            return field;
        }

        /// <summary>Declares a module-level function.</summary>
        /// <remarks>
        /// Static, always: a module is not an object, so there is no receiver for a module-level
        /// function to take. Its calls are <c>CallLocalModule</c> from inside the module and
        /// <c>CallModule</c> from outside.
        /// </remarks>
        public SurtrMethodBuilder DefineFunction(
            string name,
            SurtrClassReference returnType,
            SurtrParameterInfo[]? parameters = null,
            SurtrVisibility visibility = SurtrVisibility.Public)
        {
            var builder = new SurtrMethodBuilder(
                this, null, name, returnType,
                parameters ?? Array.Empty<SurtrParameterInfo>(),
                true, visibility, SurtrMethodDispatch.Direct, SurtrMethodRole.Normal, false);

            _functions.Add(builder);
            RegisterMethod(builder);
            return builder;
        }

        /// <summary>Declares the module's static initializer, which runs after every class's own.</summary>
        public SurtrMethodBuilder DefineStaticInitializer(string name = "minit")
        {
            var builder = new SurtrMethodBuilder(
                this, null, name, SurtrClassReference.Void,
                Array.Empty<SurtrParameterInfo>(),
                true, SurtrVisibility.Private, SurtrMethodDispatch.Direct, SurtrMethodRole.StaticInitializer, false);

            _functions.Add(builder);
            RegisterMethod(builder);
            return builder;
        }

        /// <summary>Declares a module-level property, whose accessors become module-level functions.</summary>
        public SurtrPropertyBuilder DefineProperty(
            string name,
            SurtrClassReference propertyType,
            SurtrVisibility visibility = SurtrVisibility.Public)
        {
            var property = new SurtrPropertyBuilder(this, null, null, name, propertyType, true, visibility);
            _properties.Add(property);
            return property;
        }

        /// <summary>Declares a class directly in this module.</summary>
        public SurtrClassBuilder DefineClass(
            string name,
            SurtrClassReference? baseClass = null,
            bool isAbstract = false,
            SurtrVisibility visibility = SurtrVisibility.Public,
            bool isSealed = false)
        {
            ThrowIfBuilt();

            var builder = new SurtrClassBuilder(this, null, name, baseClass, isAbstract, visibility, isSealed);
            _module.AddClass(builder.Class);
            _classes.Add(builder);
            return builder;
        }

        /// <summary>
        /// Declares an enum directly in this module: a sealed class with a fixed set of named
        /// static instances.
        /// </summary>
        /// <remarks>
        /// Cases go on the returned builder through <see cref="SurtrClassBuilder.DefineEnumCase"/>,
        /// and the instances are built by the enum's static initializer - a case is a constructor
        /// call against the enum's own constructor, not a separate kind of member.
        /// </remarks>
        public SurtrClassBuilder DefineEnum(string name, SurtrVisibility visibility = SurtrVisibility.Public)
        {
            ThrowIfBuilt();

            var builder = new SurtrClassBuilder(this, null, name, null, false, visibility, isSealed: true, isEnum: true);
            _module.AddClass(builder.Class);
            _classes.Add(builder);
            return builder;
        }

        /// <summary>Declares an interface directly in this module.</summary>
        public SurtrInterfaceBuilder DefineInterface(string name, SurtrVisibility visibility = SurtrVisibility.Public)
        {
            ThrowIfBuilt();

            var builder = new SurtrInterfaceBuilder(this, null, name, visibility, null);
            _module.AddInterface(builder.Interface);
            _interfaces.Add(builder);
            return builder;
        }

        internal SurtrMethodBuilder CreateAccessor(
            string name,
            SurtrClassReference returnType,
            SurtrParameterInfo[] parameters,
            SurtrVisibility visibility)
            => DefineFunction(name, returnType, parameters, visibility);

        #endregion

        #region Build

        /// <summary>
        /// Lays out every body, builds the metadata that depends on that layout, and freezes the
        /// chunk.
        /// </summary>
        /// <returns>The module, ready to be handed to <c>SurtrRuntime.LoadModule</c>.</returns>
        public SurtrModule Build()
        {
            ThrowIfBuilt();

            var chunk = _module.Chunk;

            // Module functions first, then each class's methods depth-first. The order only has to
            // be stable - an entry index means nothing beyond "this method's slot in the offset
            // table" - but keeping it predictable makes a disassembly readable.
            var methods = new List<SurtrMethodBuilder>(_functions);
            foreach (var declared in _classes)
                declared.CollectMethods(methods);

            var bodies = new byte[methods.Count][];
            var offsets = new int[methods.Count];

            int total = 0;
            for (int i = 0; i < methods.Count; i++)
            {
                bodies[i] = methods[i].FinishCode();
                offsets[i] = total;
                total += bodies[i].Length;
            }

            chunk.Code = new SurtrNativeArray<byte>(total);
            for (int i = 0; i < methods.Count; i++)
            {
                var body = bodies[i];
                int start = offsets[i];
                for (int b = 0; b < body.Length; b++)
                    chunk.Code[start + b] = body[b];
            }

            // Written before any metadata is constructed: SurtrBytecodeMethodInfo snapshots its own
            // offset out of this table in its constructor.
            chunk.MethodOffsets = new SurtrNativeArray<int>(methods.Count);
            for (int i = 0; i < methods.Count; i++)
                chunk.MethodOffsets[i] = offsets[i];

            for (int i = 0; i < methods.Count; i++)
            {
                var method = methods[i];
                var built = method.Build(chunk, i, method.DeclaringClass?.SelfHandle);

                var handlers = method.BuildHandlers(offsets[i]);
                if (handlers.Length != 0)
                    built.SetExceptionHandlers(handlers);
            }

            foreach (var function in _functions)
                _module.AddMethod(function.Built!);

            foreach (var declared in _classes)
                declared.AttachMembers();

            foreach (var property in _properties)
                _module.AddProperty(property.Build());

            chunk.Constants = new SurtrNativeArray<SurtrRawValue>(_constants.Count);
            for (int i = 0; i < _constants.Count; i++)
                chunk.Constants[i] = _constants[i];

            chunk.StringConstants = _stringLiterals.ToArray();
            chunk.StringConstantSlots = new SurtrNativeArray<int>(_stringSlots.Count);
            for (int i = 0; i < _stringSlots.Count; i++)
                chunk.StringConstantSlots[i] = _stringSlots[i];

            chunk.NativeVariableImports = _nativeVariableImports.ToArray();
            chunk.NativeFunctionImports = _nativeFunctionImports.ToArray();
            chunk.TypeTable = _typeTable.ToArray();
            chunk.FieldTable = _fieldTable.ToArray();
            chunk.ModuleTable = _moduleTable.ToArray();

            var methodTable = new SurtrMethodInfo[_methodTable.Count];
            for (int i = 0; i < methodTable.Length; i++)
            {
                methodTable[i] = _methodTable[i] ?? _methodTablePending[i]!.Built!;
            }

            chunk.MethodTable = methodTable;

            _module.MarkEmitted();

            _built = true;
            return _module;
        }

        private void ThrowIfBuilt()
        {
            if (_built)
                throw new InvalidOperationException($"Module '{_module.Path}' has already been built.");
        }

        #endregion
    }
}
