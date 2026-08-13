#nullable enable

using Surtr.Bytecode.Emit;
using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.CodeGen
{
    /// <summary>
    /// Folds a <c>const fun</c> by emitting its bytecode and running it on a real
    /// <see cref="SurtrRuntime"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §7.2 settles the decision this type implements, and the reason is worth restating: Surtr
    /// already has a VM, so folding runs the function's <em>real</em> bytecode on the <em>same</em>
    /// interpreter the program will run on. A second constant-folding evaluator written in the
    /// compiler would have to agree with the first about integer overflow, string equality and every
    /// entry in <c>docs/VM-Plan.md</c> §1.9's trap policy — and would silently diverge the first
    /// time it did not. Here compile-time and run-time semantics cannot drift, because they are the
    /// same code.
    /// </para>
    /// <para>
    /// This is the one place in the compiler that loads a runtime, which is why it is a single type
    /// with a small surface. The runtime it owns is a scratch one: nothing else is loaded into it,
    /// its heap holds only what folding allocated, and it is disposed with the compilation.
    /// </para>
    /// <para>
    /// <b>The budget is the whole safety story.</b> A <c>const fun</c> may loop, so it may loop
    /// forever, and a compiler that hangs is not acceptable. Every run carries
    /// <see cref="SurtrRuntime.InstructionBudget"/>; exceeding it raises a
    /// <see cref="SurtrBudgetExceededException"/> that no Surtr <c>catch</c> can take, and comes back
    /// as an ordinary compile error. The budget is re-armed before every evaluation because
    /// exceeding it leaves it exhausted rather than cleared — deliberately, so there is no window
    /// where the ceiling silently stops applying.
    /// </para>
    /// </remarks>
    public sealed class ConstFolder : IDisposable
    {
        /// <summary>How many instructions one fold may run before it is aborted.</summary>
        /// <remarks>
        /// Charged on control transfers rather than per instruction, so this counts jumps, switch
        /// arms and calls — not straight-line work, which always reaches a return. Large enough that
        /// a real table-building <c>const fun</c> never notices, small enough that a compiler never
        /// appears to hang.
        /// </remarks>
        public const long DefaultInstructionBudget = 10_000_000;

        /// <summary>The scratch module every const-evaluable function is emitted into.</summary>
        private const string ModulePath = "surtr_const_eval";

        private readonly IReadOnlyDictionary<MethodSymbol, BoundStatement> _bodies;
        private readonly DescriptorEmitter _descriptors = new DescriptorEmitter();
        private readonly Dictionary<MethodSymbol, string> _failures = new Dictionary<MethodSymbol, string>();

        private readonly Dictionary<MethodSymbol, SurtrMethodInfo> _entryPoints =
            new Dictionary<MethodSymbol, SurtrMethodInfo>();

        private SurtrRuntime? _runtime;
        private bool _prepared;
        private string? _loadFailure;
        private bool _disposed;

        /// <summary>Creates a folder over the bodies a compilation bound.</summary>
        /// <param name="bodies">Every bound body, from which the <c>const fun</c>s are taken.</param>
        /// <param name="instructionBudget">The per-evaluation ceiling; zero means no limit.</param>
        public ConstFolder(
            IReadOnlyDictionary<MethodSymbol, BoundStatement> bodies,
            long instructionBudget = DefaultInstructionBudget)
        {
            _bodies = bodies ?? throw new ArgumentNullException(nameof(bodies));
            InstructionBudget = instructionBudget;
        }

        /// <summary>The ceiling each evaluation runs under.</summary>
        public long InstructionBudget { get; }

        /// <summary>
        /// Folds a call, or says why it could not.
        /// </summary>
        /// <param name="method">The <c>const fun</c> being called.</param>
        /// <param name="arguments">Its arguments, already folded to constants and in parameter order.</param>
        /// <param name="value">
        /// What it evaluated to, in the compiler's own value vocabulary: <see cref="long"/>,
        /// <see cref="double"/>, <see cref="bool"/>, <see cref="char"/>, <see cref="string"/>,
        /// <see langword="null"/>, or an <c>object?[]</c> for an array or a tuple.
        /// </param>
        /// <param name="failure">A message naming what stopped it, when it could not fold.</param>
        public bool TryFold(
            MethodSymbol method,
            IReadOnlyList<object?> arguments,
            out object? value,
            out string failure)
        {
            value = null;

            if (_disposed)
                throw new ObjectDisposedException(nameof(ConstFolder));

            Prepare();

            if (_loadFailure is not null)
            {
                failure = _loadFailure;
                return false;
            }

            if (!_entryPoints.TryGetValue(method, out var entryPoint))
            {
                failure = _failures.TryGetValue(method, out string? known)
                    ? known
                    : $"'{method.Name}' is not a const fun this compilation bound a body for.";

                return false;
            }

            if (arguments.Count != method.Parameters.Count)
            {
                failure = $"'{method.Name}' takes {method.Parameters.Count} argument(s) and was folded with {arguments.Count}.";
                return false;
            }

            var runtime = _runtime!;
            var marshalled = new SurtrValue[arguments.Count];

            for (int i = 0; i < marshalled.Length; i++)
            {
                if (!TryMarshalIn(runtime, arguments[i], method.Parameters[i].Type, out marshalled[i]))
                {
                    failure = $"'{method.Parameters[i].Name}' cannot be handed to the evaluator as a '{method.Parameters[i].Type.ToDisplayString()}'.";
                    return false;
                }
            }

            // Re-armed here rather than once at construction: exceeding the budget leaves it
            // exhausted, so a previous failure would otherwise abort every later fold instantly.
            runtime.InstructionBudget = InstructionBudget;

            SurtrValue result;
            try
            {
                result = runtime.Invoke(entryPoint, marshalled);
            }
            catch (SurtrBudgetExceededException)
            {
                // A failed run leaves the machine mid-frame, so the next fold starts from a clean
                // stack rather than inheriting this one's wreckage.
                runtime.ResetExecution();
                failure = $"'{method.Name}' did not finish within {InstructionBudget} steps at compile time.";
                return false;
            }
            catch (SurtrExecutionException exception)
            {
                runtime.ResetExecution();
                failure = $"'{method.Name}' failed at compile time: {exception.Message}";
                return false;
            }

            if (!TryMarshalOut(runtime, result, method.ReturnType, out value))
            {
                failure = $"'{method.Name}' returned a '{method.ReturnType.ToDisplayString()}', which is not a value a constant can hold.";
                return false;
            }

            failure = string.Empty;
            return true;
        }

        /// <summary>Why a <c>const fun</c> cannot be folded at all, if it cannot.</summary>
        public bool TryGetFailure(MethodSymbol method, out string failure)
        {
            Prepare();
            return _failures.TryGetValue(method, out failure!);
        }

        #region Building the scratch module
        /// <summary>
        /// Emits every const-evaluable function into one module, and loads it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Emission runs to a fixed point rather than once. A function the emitter cannot lower is
        /// dropped, and dropping it makes every function that <em>calls</em> it fail to emit in turn
        /// — so the round repeats until nothing new drops. That is what keeps one unsupported
        /// construct from quietly changing what a caller computes: a caller of a dropped function is
        /// itself dropped, never emitted against a stub.
        /// </para>
        /// <para>
        /// The whole set goes into one module because that is what makes a call between two const
        /// funs an ordinary <c>CallLocalModule</c>, with no cross-module table and no load order to
        /// arrange.
        /// </para>
        /// </remarks>
        private void Prepare()
        {
            if (_prepared)
                return;

            _prepared = true;

            var candidates = new List<MethodSymbol>();
            foreach (var pair in _bodies)
            {
                if (pair.Key.IsConst && CanDeclare(pair.Key))
                    candidates.Add(pair.Key);
            }

            // A dictionary's order is not guaranteed, and a scratch module's layout should not
            // depend on it - two identical compilations should fold identically.
            candidates.Sort(static (left, right) => string.CompareOrdinal(Qualify(left), Qualify(right)));

            while (true)
            {
                var builder = new SurtrModuleBuilder(ModulePath);
                var context = new EmitContext(builder, _descriptors);
                var declared = new Dictionary<MethodSymbol, SurtrMethodBuilder>();

                for (int i = 0; i < candidates.Count; i++)
                {
                    var function = Declare(builder, candidates[i], i);
                    declared.Add(candidates[i], function);
                    context.Declare(candidates[i], function);
                }

                var dropped = new List<MethodSymbol>();

                foreach (var candidate in candidates)
                {
                    try
                    {
                        new MethodBodyEmitter(declared[candidate], candidate, context).Emit(_bodies[candidate]);
                    }
                    catch (Exception exception) when (exception is SurtrCompilerException or InvalidOperationException)
                    {
                        _failures[candidate] = exception.Message;
                        dropped.Add(candidate);
                    }
                }

                if (dropped.Count != 0)
                {
                    foreach (var failed in dropped)
                        candidates.Remove(failed);

                    continue;
                }

                Load(builder, declared);
                return;
            }
        }

        private void Load(SurtrModuleBuilder builder, Dictionary<MethodSymbol, SurtrMethodBuilder> declared)
        {
            try
            {
                var runtime = new SurtrRuntime();
                runtime.LoadModule(builder.Build());
                _runtime = runtime;
            }
            catch (Exception exception) when (exception is SurtrExecutionException or InvalidOperationException or ArgumentException)
            {
                _loadFailure = $"The const evaluator's scratch module could not be loaded: {exception.Message}";
                return;
            }

            foreach (var pair in declared)
                _entryPoints.Add(pair.Key, pair.Value.Built!);
        }

        /// <summary>
        /// Whether a signature is one the scratch module can carry.
        /// </summary>
        /// <remarks>
        /// A const fun mentioning a type declared in the program being compiled would put a
        /// descriptor in the scratch module that nothing there can resolve, and loading would fail
        /// for the whole batch rather than for the one function. Checking the signature up front
        /// turns that into a message about the function that caused it.
        /// </remarks>
        private bool CanDeclare(MethodSymbol method)
        {
            if (!method.IsStatic && method.ContainingType is not null)
            {
                _failures[method] = $"'{method.Name}' needs a receiver, so it cannot be folded; a const fun is static.";
                return false;
            }

            if (!IsSelfContained(method.ReturnType))
            {
                _failures[method] = $"'{method.Name}' returns '{method.ReturnType.ToDisplayString()}', which the const evaluator cannot build without loading the program.";
                return false;
            }

            foreach (var parameter in method.Parameters)
            {
                if (IsSelfContained(parameter.Type))
                    continue;

                _failures[method] = $"'{method.Name}' takes a '{parameter.Type.ToDisplayString()}', which the const evaluator cannot build without loading the program.";
                return false;
            }

            return true;
        }

        /// <summary>Whether a type is one the built-in module already defines everywhere.</summary>
        private static bool IsSelfContained(TypeSymbol type)
        {
            var bare = type.NonNullable;

            switch (bare.SpecialType)
            {
                case SpecialType.Int:
                case SpecialType.Float:
                case SpecialType.Bool:
                case SpecialType.Char:
                case SpecialType.String:
                case SpecialType.Range:
                case SpecialType.Void:
                    return true;
            }

            switch (bare.TypeKind)
            {
                case TypeSymbolKind.Array:
                    return IsSelfContained(((ArrayTypeSymbol)bare).ElementType);

                case TypeSymbolKind.Dictionary:
                {
                    var dictionary = (DictionaryTypeSymbol)bare;
                    return IsSelfContained(dictionary.KeyType) && IsSelfContained(dictionary.ValueType);
                }

                case TypeSymbolKind.Tuple:
                {
                    foreach (var element in ((TupleTypeSymbol)bare).ElementTypes)
                    {
                        if (!IsSelfContained(element))
                            return false;
                    }

                    return true;
                }

                default:
                    return false;
            }
        }

        private SurtrMethodBuilder Declare(SurtrModuleBuilder builder, MethodSymbol method, int index)
        {
            var parameters = new SurtrParameterInfo[method.Parameters.Count];
            for (int i = 0; i < parameters.Length; i++)
            {
                parameters[i] = builder.Parameter(
                    method.Parameters[i].Name,
                    _descriptors.Emit(method.Parameters[i].Type));
            }

            // Named per slot rather than per source name: two modules may each declare `square`,
            // and this module flattens both into one namespace.
            return builder.DefineFunction(
                method.Name + "$" + index.ToString(),
                _descriptors.Emit(method.ReturnType),
                parameters);
        }

        private static string Qualify(MethodSymbol method)
            => (method.ContainingSymbol?.Name ?? string.Empty) + "." + method.Name + "/" + method.Parameters.Count.ToString();
        #endregion

        #region Marshalling
        private static bool TryMarshalIn(SurtrRuntime runtime, object? value, TypeSymbol type, out SurtrValue marshalled)
        {
            marshalled = SurtrValue.Null;

            switch (MethodBodyEmitter.TypeCodeOf(type))
            {
                case SurtrValueTypeCode.Integer when value is long integer && integer >= int.MinValue && integer <= int.MaxValue:
                    marshalled = SurtrValue.CreateInt(integer);
                    return true;

                // The widening §5.7 makes implicit is free here: the value is known, so it is
                // converted on the way in rather than by an instruction.
                case SurtrValueTypeCode.Float when value is long widened:
                    marshalled = SurtrValue.CreateFloat((double)widened);
                    return true;

                case SurtrValueTypeCode.Float when value is double real:
                    marshalled = SurtrValue.CreateFloat(real);
                    return true;

                case SurtrValueTypeCode.Boolean when value is bool boolean:
                    marshalled = SurtrValue.CreateBool(boolean);
                    return true;

                case SurtrValueTypeCode.Character when value is char character:
                    marshalled = SurtrValue.CreateChar(character);
                    return true;

                case SurtrValueTypeCode.String when value is string text:
                    marshalled = SurtrValue.CreateReference(runtime.InternString(text).GetSurtrReference());
                    return true;

                default:
                    return value is null && type.IsReferenceType;
            }
        }

        private static bool TryMarshalOut(SurtrRuntime runtime, SurtrValue value, TypeSymbol type, out object? result)
        {
            result = null;

            switch (MethodBodyEmitter.TypeCodeOf(type))
            {
                case SurtrValueTypeCode.Integer: result = (long)value.AsInt; return true;
                case SurtrValueTypeCode.Float: result = value.AsFloat; return true;
                case SurtrValueTypeCode.Boolean: result = value.AsBool; return true;
                case SurtrValueTypeCode.Character: result = value.AsChar; return true;

                case SurtrValueTypeCode.String:
                {
                    if (value.IsNullReference)
                        return true;

                    var text = runtime.Resolve<SurtrString>(value);
                    if (text is null)
                        return false;

                    result = text.Text;
                    return true;
                }

                case SurtrValueTypeCode.Array:
                {
                    if (value.IsNullReference)
                        return true;

                    var array = runtime.Resolve<SurtrArray>(value);
                    if (array is null)
                        return false;

                    var element = ((ArrayTypeSymbol)type.NonNullable).ElementType;
                    var elements = new object?[array.Length];

                    for (int i = 0; i < elements.Length; i++)
                    {
                        if (!TryMarshalOut(runtime, array[i], element, out elements[i]))
                            return false;
                    }

                    result = elements;
                    return true;
                }

                case SurtrValueTypeCode.Tuple:
                {
                    if (value.IsNullReference)
                        return true;

                    var tuple = runtime.Resolve<SurtrTuple>(value);
                    if (tuple is null)
                        return false;

                    var types = ((TupleTypeSymbol)type.NonNullable).ElementTypes;
                    if (tuple.Length != types.Count)
                        return false;

                    var elements = new object?[tuple.Length];
                    for (int i = 0; i < elements.Length; i++)
                    {
                        if (!TryMarshalOut(runtime, tuple[i], types[i], out elements[i]))
                            return false;
                    }

                    result = elements;
                    return true;
                }

                default:
                    return false;
            }
        }
        #endregion

        /// <summary>Releases the scratch runtime and everything folding allocated on it.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _runtime?.Dispose();
            _runtime = null;
        }
    }
}
