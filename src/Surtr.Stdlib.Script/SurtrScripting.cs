#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Stdlib.Script
{
    /// <summary>
    /// Fase 8, §6.3: dynamic compilation and <c>eval</c> for Surtr, as its own optional assembly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Surtr-facing surface (<c>surtr.script.Script</c>) is not built by <c>Surtr.Stdlib.Tool</c>
    /// into a <c>.surtrc</c> image the way the rest of the stdlib is - it is compiled from the
    /// embedded <see cref="ScriptModuleSource"/> string the first time <see cref="LoadInto"/> runs,
    /// using the very compiler this assembly already carries as a real dependency. That sidesteps
    /// needing a second build-time tool just for one small module, and keeps the base
    /// <c>Surtr.Stdlib</c> assembly free of any reference to <c>Surtr.Compiler</c> - a host that
    /// never calls <see cref="LoadInto"/> never pays for any of this.
    /// </para>
    /// <para>
    /// <c>Script.compile(source)</c> compiles <c>source</c> as a fresh module (a new synthetic
    /// module path per call, so two calls never collide) and loads it into the same
    /// <see cref="SurtrRuntime"/> the call is running on - reached through
    /// <c>SurtrCallArguments.Runtime</c>, the same way every other native body reaches its runtime.
    /// <c>Script.call(name, args...)</c> looks the named function up by name and argument count (the
    /// same "match by arity alone" rule <c>Surtr.Run</c>'s <c>EntryPoint.Resolve</c> already uses for
    /// the same reason: a compiled artifact has no source overload-resolution step to redo honestly)
    /// and invokes it through <see cref="SurtrRuntime.Invoke(SurtrMethodInfo, SurtrValue[])"/>, which
    /// already does the erased-argument/result boxing a host boundary needs (§4.8) - nothing here
    /// hand-rolls that part.
    /// </para>
    /// <para>
    /// A compile failure does not throw - Fase 8 (§6.4) found no established, public way for a
    /// native body to raise a Surtr-level exception a script's own <c>catch</c> can see, and
    /// inventing one just for this would be exactly the kind of scope creep this round of work
    /// avoided elsewhere. <c>Script.compile</c> instead returns a script whose <c>isValid</c> is
    /// <c>false</c> and whose <c>lastError()</c> holds the diagnostics text - compile errors are
    /// data a script branches on, not a thrown surprise.
    /// </para>
    /// </remarks>
    public static class SurtrScripting
    {
        /// <summary>The module path a driver's own source must <c>import</c> to reach <c>Script</c>.</summary>
        public const string ScriptModulePath = "surtr.script.Script";

        /// <summary>
        /// The embedded Surtr source declaring <c>Script</c>/<c>evalInt</c>/etc. A driver program
        /// compiled with <c>SurtrCompilation</c> (rather than loaded from a prebuilt image) needs
        /// this added to its OWN compilation alongside its own source, under
        /// <see cref="ScriptModulePath"/>, before it can <c>import surtr.script.Script;</c> - binding
        /// happens at compile time, against declared symbols, which <see cref="LoadInto"/> alone
        /// cannot supply since it only publishes bodies and loads a module into a *running* runtime,
        /// after compilation has already finished.
        /// </summary>
        // language=surtr
        public const string ScriptModuleSource = @"
public class Script
{
    private let _handle: int;

    private constructor(handle: int)
    {
        this._handle = handle;
    }

    /// Compiles `source` as a fresh module. Check `isValid` before calling `call` - a script whose
    /// compilation failed still constructs, with `lastError()` holding the diagnostics text.
    public static fun compile(source: string): Script => Script(scriptCompile(source));

    public inline isValid: bool => _handle >= 0;

    /// The compiler's diagnostics text for a failed `compile()`. Empty for a valid script.
    public fun lastError(): string => scriptLastError(_handle);

    /// Whether the compiled module declares a function named `name`, of any argument count.
    public fun hasFunction(name: string): bool => scriptHasFunction(_handle, name);

    /// Calls the compiled module's function `name`, matched by name and argument count alone (a
    /// compiled script has no source-level overload resolution to redo). Arguments and the result
    /// travel as `unknown`, boxed/unboxed the same way any erased slot is.
    public fun call(name: string, args: unknown...): unknown => scriptCall(_handle, name, args);
}

native fun scriptCompile(source: string): int;
native fun scriptLastError(handle: int): string;
native fun scriptHasFunction(handle: int, name: string): bool;
native fun scriptCall(handle: int, name: string, args: unknown[]): unknown;

/// Compiles and evaluates a single expression, e.g. `eval(""2 + 2 * x"", 5)`-style usage via the
/// named-argument overloads below. Convenience over `Script.compile` + `call` for the common case
/// of ""run this one expression"" without declaring a whole function.
public fun evalInt(expression: string): int => Script.compile(wrapEval(expression, ""int"")).call(""evaluateExpression"") as int;
public fun evalFloat(expression: string): float => Script.compile(wrapEval(expression, ""float"")).call(""evaluateExpression"") as float;
public fun evalBool(expression: string): bool => Script.compile(wrapEval(expression, ""bool"")).call(""evaluateExpression"") as bool;
public fun evalString(expression: string): string => Script.compile(wrapEval(expression, ""string"")).call(""evaluateExpression"") as string;

private fun wrapEval(expression: string, returnType: string): string
    => ""fun evaluateExpression(): "" + returnType + "" { return ("" + expression + "" ); }"";
";

        /// <summary>
        /// Publishes <c>surtr.script.Script</c>'s native bodies on <paramref name="runtime"/>. Call
        /// before loading any module (yours or the one <see cref="LoadInto"/> compiles) that
        /// declares them - the same "publish before <c>LoadModule</c>" rule every native body
        /// follows (§10).
        /// </summary>
        public static void RegisterNativeBodies(SurtrRuntime runtime)
        {
            if (runtime is null)
                throw new ArgumentNullException(nameof(runtime));

            unsafe
            {
                runtime.DefineNativeBody("surtr.script.Script.scriptCompile", SurtrNativeEntryPoint.FromFunctionPointer(&ScriptCompile));
                runtime.DefineNativeBody("surtr.script.Script.scriptLastError", SurtrNativeEntryPoint.FromFunctionPointer(&ScriptLastError));
                runtime.DefineNativeBody("surtr.script.Script.scriptHasFunction", SurtrNativeEntryPoint.FromFunctionPointer(&ScriptHasFunction));
                runtime.DefineNativeBody("surtr.script.Script.scriptCall", SurtrNativeEntryPoint.FromFunctionPointer(&ScriptCall));
            }
        }

        /// <summary>
        /// Compiles the embedded <c>surtr.script.Script</c> module on its own, publishes its native
        /// bodies, and loads it into <paramref name="runtime"/> - the convenience path for a host
        /// with no driver source of its own to bind it against (e.g. a Unity project that only ever
        /// reaches <c>Script</c> from other already-compiled/loaded modules). A driver being compiled
        /// fresh via <c>SurtrCompilation</c> in the same process instead needs
        /// <see cref="ScriptModuleSource"/> added to its own <c>SurtrProject</c> (so binding sees its
        /// declared symbols) plus <see cref="RegisterNativeBodies"/> - see the remarks above.
        /// </summary>
        public static void LoadInto(SurtrRuntime runtime)
        {
            if (runtime is null)
                throw new ArgumentNullException(nameof(runtime));

            var project = new SurtrProject("D:/surtr-script-embedded");
            project.AddSourceFile("D:/surtr-script-embedded/Script.surtr", ScriptModulePath, ScriptModuleSource);

            var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            if (compilation.HasErrors)
            {
                string diagnostics = string.Join("; ", compilation.Diagnostics.Select(d => d.ToString()));
                throw new InvalidOperationException($"Surtr.Stdlib.Script's own embedded module failed to compile: {diagnostics}");
            }

            var emitter = new ModuleEmitter(compilation, binder);
            if (!emitter.TryEmit())
            {
                string diagnostics = string.Join("; ", compilation.Diagnostics.Select(d => d.ToString()));
                throw new InvalidOperationException($"Surtr.Stdlib.Script's own embedded module failed to emit: {diagnostics}");
            }

            RegisterNativeBodies(runtime);

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);
        }

        /// <summary>One compiled script, keyed by an opaque int handle a Surtr `Script` instance carries.</summary>
        private sealed class CompiledScript
        {
            public SurtrModule? Module;
            public string Error = string.Empty;
        }

        // Keyed per-runtime so two runtimes in the same process (e.g. two tests) never share handles.
        private static readonly Dictionary<SurtrRuntime, List<CompiledScript>> _scripts = new();
        private static int _nextModuleId;

        private static List<CompiledScript> ScriptsFor(SurtrRuntime runtime)
        {
            if (!_scripts.TryGetValue(runtime, out var list))
            {
                list = new List<CompiledScript>();
                _scripts[runtime] = list;
            }
            return list;
        }

        private static unsafe int ScriptCompile(SurtrCallArguments arguments)
        {
            string source = arguments.GetString(0).ToString();
            var runtime = arguments.Runtime;
            var scripts = ScriptsFor(runtime);

            string modulePath = "surtr.script.eval" + System.Threading.Interlocked.Increment(ref _nextModuleId);
            var project = new SurtrProject("D:/surtr-eval");
            project.AddSourceFile($"D:/surtr-eval/{modulePath}.surtr", modulePath, source);

            var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            if (compilation.HasErrors)
            {
                var entry = new CompiledScript { Error = string.Join("\n", compilation.Diagnostics.Select(d => d.ToString())) };
                scripts.Add(entry);
                return arguments.Return(SurtrValue.CreateInt(-(scripts.Count)));
            }

            var emitter = new ModuleEmitter(compilation, binder);
            if (!emitter.TryEmit())
            {
                var entry = new CompiledScript { Error = string.Join("\n", compilation.Diagnostics.Select(d => d.ToString())) };
                scripts.Add(entry);
                return arguments.Return(SurtrValue.CreateInt(-(scripts.Count)));
            }

            SurtrModule? mainModule = null;
            foreach (var module in emitter.Modules)
            {
                runtime.LoadModule(module);
                if (module.Path == modulePath)
                    mainModule = module;
            }

            scripts.Add(new CompiledScript { Module = mainModule });
            return arguments.Return(SurtrValue.CreateInt(scripts.Count - 1));
        }

        private static bool TryResolve(SurtrCallArguments arguments, int handle, out CompiledScript? script)
        {
            var scripts = ScriptsFor(arguments.Runtime);
            if (handle < 0)
            {
                int errorIndex = -handle - 1;
                script = errorIndex >= 0 && errorIndex < scripts.Count ? scripts[errorIndex] : null;
                return false;
            }

            script = handle < scripts.Count ? scripts[handle] : null;
            return script?.Module is not null;
        }

        private static int ScriptLastError(SurtrCallArguments arguments)
        {
            int handle = arguments.GetInt(0);
            TryResolve(arguments, handle, out var script);
            string text = script?.Error ?? string.Empty;
            return arguments.Return(SurtrValue.CreateReference(arguments.Runtime.NewString(text).GetSurtrReference()));
        }

        private static int ScriptHasFunction(SurtrCallArguments arguments)
        {
            int handle = arguments.GetInt(0);
            string name = arguments.GetString(1).ToString();
            bool found = TryResolve(arguments, handle, out var script)
                && script!.Module!.TryGetMethods(name, out _);
            return arguments.Return(SurtrValue.CreateBool(found));
        }

        private static bool IsPrimitiveTypeCode(SurtrValueTypeCode code)
            => code is SurtrValueTypeCode.Integer or SurtrValueTypeCode.Float or SurtrValueTypeCode.Boolean or SurtrValueTypeCode.Character;

        private static int ScriptCall(SurtrCallArguments arguments)
        {
            int handle = arguments.GetInt(0);
            string name = arguments.GetString(1).ToString();
            var argsArray = arguments.Get<SurtrArray>(2);

            if (!TryResolve(arguments, handle, out var script))
                throw new InvalidOperationException($"Script (handle {handle}) has no successfully compiled module to call '{name}' on.");

            if (!script!.Module!.TryGetMethods(name, out var overloads))
                throw new InvalidOperationException($"Compiled script declares no function named '{name}'.");

            int argCount = argsArray.Length;
            SurtrMethodInfo? method = overloads.FirstOrDefault(m => m.ArgumentSlotCount == argCount)
                ?? (overloads.Length == 1 ? overloads[0] : null);
            if (method is null)
                throw new InvalidOperationException($"No overload of '{name}' takes {argCount} argument(s).");

            // `args` arrived from a Surtr-level `unknown[]` (a real varargs collection), so every
            // element is boxed - that is what an erased slot always holds (§1.11). `Invoke` expects
            // each argument already in the exact representation the callee's own parameter declares
            // (boxed only for a reference/erased parameter; a raw tagged primitive for a concrete
            // one, e.g. `add(a: int, b: int)`) - it does not unbox a concrete scalar the way a real
            // call site's own emitted `UnboxIfStillErased` would. So: unbox here, per parameter,
            // wherever the callee's declared type is a concrete primitive.
            var parameters = method.Parameters;
            var callArgs = new SurtrValue[argCount];
            for (int i = 0; i < argCount; i++)
            {
                SurtrValue value = argsArray[i];
                if (i < parameters.Length && IsPrimitiveTypeCode(parameters[i].ParameterType.TypeCode))
                {
                    var boxed = arguments.Runtime.Resolve<SurtrBoxed>(value);
                    if (boxed is not null)
                        value = boxed.BoxedValue;
                }
                callArgs[i] = value;
            }

            var result = arguments.Runtime.Invoke(method, callArgs);
            return arguments.Return(result);
        }
    }
}
