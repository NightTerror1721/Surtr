#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.CodeGen
{
    /// <summary>
    /// What one module's emission knows: which builder or metadata each symbol became, and the one
    /// gate from a <see cref="TypeSymbol"/> to a descriptor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It exists because <see cref="MethodBodyEmitter"/> cannot answer any of those questions from a
    /// symbol alone. A call site names a <see cref="MethodSymbol"/>, and what the emitter has to
    /// emit is a slot in a real method table — which is a <see cref="SurtrMethodBuilder"/> for a
    /// method of the module being built, a <see cref="SurtrMethodInfo"/> for one of a module built
    /// earlier or imported, and neither for one whose body never emitted.
    /// </para>
    /// <para>
    /// The split between <em>declared</em> and <em>built</em> is the emitter's own ordering showing
    /// through, not a distinction worth having: a method of this module has no metadata until
    /// <see cref="SurtrModuleBuilder.Build"/> has run, because
    /// <see cref="SurtrBytecodeMethodInfo"/> snapshots its body's offset in its constructor. So a
    /// call inside the module names the builder and a call out of it names the metadata, and
    /// <see cref="MethodBodyEmitter"/> asks this which it has.
    /// </para>
    /// </remarks>
    public sealed class EmitContext
    {
        private readonly Dictionary<MethodSymbol, SurtrMethodBuilder> _declared =
            new Dictionary<MethodSymbol, SurtrMethodBuilder>();

        private readonly Dictionary<MethodSymbol, SurtrMethodInfo> _bound =
            new Dictionary<MethodSymbol, SurtrMethodInfo>();

        private readonly Dictionary<MethodSymbol, SurtrModule> _foreign =
            new Dictionary<MethodSymbol, SurtrModule>();

        private readonly Dictionary<FieldSymbol, SurtrFieldInfo> _fields =
            new Dictionary<FieldSymbol, SurtrFieldInfo>();

        private readonly Dictionary<NamedTypeSymbol, SurtrClassBuilder> _classes =
            new Dictionary<NamedTypeSymbol, SurtrClassBuilder>();

        private readonly Dictionary<NamedTypeSymbol, SurtrInterfaceBuilder> _interfaces =
            new Dictionary<NamedTypeSymbol, SurtrInterfaceBuilder>();

        private readonly Dictionary<string, int> _syntheticCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Creates a context over one module being built.</summary>
        /// <param name="module">The module every declaration lands on.</param>
        /// <param name="descriptors">
        /// The descriptor gate, shared across a whole compilation so a type is encoded once.
        /// </param>
        public EmitContext(SurtrModuleBuilder module, DescriptorEmitter descriptors)
        {
            Module = module ?? throw new ArgumentNullException(nameof(module));
            Descriptors = descriptors ?? throw new ArgumentNullException(nameof(descriptors));
        }

        /// <summary>The module being built.</summary>
        public SurtrModuleBuilder Module { get; }

        /// <summary>The one place a <see cref="TypeSymbol"/> becomes a descriptor.</summary>
        public DescriptorEmitter Descriptors { get; }

        /// <summary>
        /// Every body the compilation bound, or <see langword="null"/> when nothing needs them.
        /// </summary>
        /// <remarks>
        /// Only <c>inline</c> reads this (§3.6): splicing a call site needs the callee's bound body,
        /// and a callee compiled earlier has none to give — which is why inlining across an image
        /// boundary is a request the compiler is allowed to decline.
        /// </remarks>
        public IReadOnlyDictionary<MethodSymbol, Binding.BoundTree.BoundStatement>? Bodies { get; set; }

        /// <summary>
        /// What answers a <c>const fun</c> call at compile time, or <see langword="null"/> when the
        /// compilation declares none.
        /// </summary>
        public ConstFolder? Folder { get; set; }

        #region Registration
        /// <summary>Records the builder a method of this module is being emitted onto.</summary>
        public void Declare(MethodSymbol symbol, SurtrMethodBuilder builder) => _declared[symbol] = builder;

        /// <summary>
        /// Records already-built metadata for a method: an abstract or native member of this
        /// module, or anything from a module built before it.
        /// </summary>
        /// <param name="symbol">The symbol a call site names.</param>
        /// <param name="method">The metadata it resolves to.</param>
        /// <param name="owner">
        /// The module owning it, when that module is not this one and the method is module-level —
        /// which is the only case that has to reach through the module access table.
        /// </param>
        public void Bind(MethodSymbol symbol, SurtrMethodInfo method, SurtrModule? owner = null)
        {
            _bound[symbol] = method;

            if (owner is not null)
                _foreign[symbol] = owner;
        }

        /// <summary>Records the metadata a field symbol became.</summary>
        public void Declare(FieldSymbol symbol, SurtrFieldInfo field) => _fields[symbol] = field;

        /// <summary>Records the builder a class symbol is being declared onto.</summary>
        public void Declare(NamedTypeSymbol symbol, SurtrClassBuilder builder) => _classes[symbol] = builder;

        /// <summary>Records the builder an interface symbol is being declared onto.</summary>
        public void Declare(NamedTypeSymbol symbol, SurtrInterfaceBuilder builder) => _interfaces[symbol] = builder;

        /// <summary>
        /// Records the constructor the compiler synthesised for a type that declares none.
        /// </summary>
        /// <remarks>
        /// A class with field initializers and no constructor still has to run them, and
        /// <c>ObjNew</c> only allocates — so one is made, and every creation site has to call it
        /// even though the binder saw no constructor to resolve to.
        /// </remarks>
        public void DeclareDefaultConstructor(NamedTypeSymbol symbol, SurtrMethodBuilder builder)
            => _defaultConstructors[symbol.Definition] = builder;

        /// <summary>The synthesised constructor a type got, if it got one.</summary>
        public bool TryGetDefaultConstructor(NamedTypeSymbol symbol, out SurtrMethodBuilder builder)
            => _defaultConstructors.TryGetValue(symbol.Definition, out builder!);

        /// <summary>
        /// Records the metadata a synthesised constructor became, for a module built before this one.
        /// </summary>
        /// <remarks>
        /// A synthesised constructor has no symbol, so it cannot travel through
        /// <see cref="Bind(MethodSymbol, SurtrMethodInfo, SurtrModule)"/> like every other member —
        /// and a creation site in this module still has to call it, or the instance it allocates runs
        /// none of the initializers the constructor exists for.
        /// </remarks>
        public void BindDefaultConstructor(NamedTypeSymbol symbol, SurtrMethodInfo method)
            => _builtDefaultConstructors[symbol.Definition] = method;

        /// <summary>The metadata of a synthesised constructor from a module built earlier.</summary>
        public bool TryGetBuiltDefaultConstructor(NamedTypeSymbol symbol, out SurtrMethodInfo method)
            => _builtDefaultConstructors.TryGetValue(symbol.Definition, out method!);

        private readonly Dictionary<NamedTypeSymbol, SurtrMethodBuilder> _defaultConstructors =
            new Dictionary<NamedTypeSymbol, SurtrMethodBuilder>();

        private readonly Dictionary<NamedTypeSymbol, SurtrMethodInfo> _builtDefaultConstructors =
            new Dictionary<NamedTypeSymbol, SurtrMethodInfo>();
        #endregion

        #region Lookup
        /// <summary>The builder a method of this module is being emitted onto, if it is one.</summary>
        /// <remarks>
        /// A substituted view of a generic's member — what a call on a <c>Box&lt;int&gt;</c> resolves
        /// to — is a clone of the declaration, and only the declaration was ever declared onto a
        /// builder. Erasure is exactly what makes asking the declaration the right answer: one class,
        /// one method table, one compiled body per declaration.
        /// </remarks>
        public bool TryGetBuilder(MethodSymbol symbol, out SurtrMethodBuilder builder)
            => _declared.TryGetValue(symbol, out builder!)
                || (symbol.OriginalDefinition is MethodSymbol original && _declared.TryGetValue(original, out builder!));

        /// <summary>
        /// The metadata a method resolves to, from anywhere: bound during this emission, or carried
        /// on the symbol since it was imported.
        /// </summary>
        public SurtrMethodInfo? Resolve(MethodSymbol symbol)
        {
            if (_bound.TryGetValue(symbol, out var bound))
                return bound;

            if (symbol.OriginalDefinition is MethodSymbol original)
                return _bound.TryGetValue(original, out var declared) ? declared : original.ImportedFrom;

            return symbol.ImportedFrom;
        }

        /// <summary>
        /// The module a module-level function belongs to when it is not this one, which is what a
        /// cross-module call has to name.
        /// </summary>
        public bool TryGetForeignModule(MethodSymbol symbol, out SurtrModule owner)
            => _foreign.TryGetValue(symbol, out owner!)
                || (symbol.OriginalDefinition is MethodSymbol original && _foreign.TryGetValue(original, out owner!));

        /// <summary>The metadata a field resolves to, declared here or imported.</summary>
        public SurtrFieldInfo? Resolve(FieldSymbol symbol)
        {
            if (_fields.TryGetValue(symbol, out var field))
                return field;

            if (symbol.OriginalDefinition is FieldSymbol original)
                return _fields.TryGetValue(original, out var declared) ? declared : original.ImportedFrom;

            return symbol.ImportedFrom;
        }

        /// <summary>The class builder a type symbol was declared onto, if this module declares it.</summary>
        public bool TryGetClass(NamedTypeSymbol symbol, out SurtrClassBuilder builder)
            => _classes.TryGetValue(symbol.Definition, out builder!);

        /// <summary>The interface builder a type symbol was declared onto, if this module declares it.</summary>
        public bool TryGetInterface(NamedTypeSymbol symbol, out SurtrInterfaceBuilder builder)
            => _interfaces.TryGetValue(symbol.Definition, out builder!);

        /// <summary>
        /// Whether a call to <paramref name="symbol"/> has to go through the interface dispatch
        /// table rather than a vtable slot.
        /// </summary>
        /// <remarks>
        /// A method's declaring type travels as a descriptor, and a descriptor does not say whether
        /// it names a class or a contract until load — so the emitter cannot infer this and the
        /// declaration has to be remembered. Both halves are covered: a contract this module
        /// declares is in <see cref="_interfaces"/>, and one that arrived as metadata says so on its
        /// own symbol.
        /// </remarks>
        public bool IsInterfaceMethod(MethodSymbol symbol)
        {
            if (symbol.ContainingType is not NamedTypeSymbol owner)
                return false;

            return owner.TypeKind == TypeSymbolKind.Interface;
        }
        #endregion

        /// <summary>
        /// Hands out the next name in a synthetic category, so two lambdas in one method do not
        /// collide.
        /// </summary>
        /// <remarks>
        /// The counter is per context — one name in one method table — rather than per method, so a
        /// name is stable for a given emission order and cannot be reused by a later declaration.
        /// </remarks>
        public int NextSyntheticIndex(string category, string context)
        {
            string key = category + SyntheticNames.Marker + context;
            _syntheticCounts.TryGetValue(key, out int next);
            _syntheticCounts[key] = next + 1;
            return next;
        }
    }
}
