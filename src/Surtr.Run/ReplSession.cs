#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.VM;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Surtr.Run
{
    /// <summary>
    /// One outcome of <see cref="ReplSession.Submit"/>: what to print, or what went wrong. Never
    /// both - a failed submission changes nothing about the session's accumulated state.
    /// </summary>
    internal readonly struct ReplOutcome
    {
        public bool Success { get; }
        public string? Printed { get; }
        public string? Error { get; }

        private ReplOutcome(bool success, string? printed, string? error)
        {
            Success = success;
            Printed = printed;
            Error = error;
        }

        /// <summary>Blank input - nothing happened, nothing to report.</summary>
        public static ReplOutcome Empty { get; } = new ReplOutcome(true, null, null);

        public static ReplOutcome Ok(string? printed) => new ReplOutcome(true, printed, null);
        public static ReplOutcome Failure(string error) => new ReplOutcome(false, null, error);
    }

    /// <summary>
    /// One REPL's worth of state over a single, already-running <see cref="SurtrRuntime"/>: a
    /// growing set of module-level declarations, and one-shot evaluation of everything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>LoadModule</c> rejects reloading the same module path, and a module's static
    /// initializers run once, at load, in declaration order (docs/VM-Plan.md §1.12) - there is no
    /// "add a declaration to an already-loaded module" operation (that is hot-reload, a separate,
    /// much larger piece of work). So a <c>let</c>/<c>var</c>/<c>fun</c>/<c>class</c>/... the user
    /// types is instead appended to a growing source text and the *whole* accumulated session is
    /// recompiled under a fresh, incrementing module path each time - <c>surtr.repl.session0</c>,
    /// <c>session1</c>, and so on. A failed recompile leaves the previous generation active, so a
    /// typo never destroys state already built. Every earlier generation stays loaded (harmless,
    /// if wasteful, metadata for a development tool - not something to run for long unattended
    /// sessions or embed in production).
    /// </para>
    /// <para>
    /// An <c>import</c> is tracked separately from every other declaration and re-stated verbatim
    /// at the top of <em>every</em> later compilation, session generations and one-shot eval
    /// modules alike - it is not enough to import the session module itself and rely on it having
    /// re-exported anything. <c>export import</c> looked like the fix and is not one: a re-export
    /// is resolved entirely at bind time and "the runtime knows nothing about it" (CLAUDE.md), so
    /// nothing survives into the compiled module's own metadata for a *later, separate*
    /// <c>SurtrCompilation</c> to discover - confirmed while building this by watching
    /// <c>import surtr.math.Math;</c> compile fine on its own line and then <c>floor(3.7)</c> still
    /// fail to resolve on the next. Re-issuing the literal import line every time sidesteps the
    /// question entirely.
    /// </para>
    /// <para>
    /// Everything besides a declaration - a bare expression, a call, an assignment - runs exactly
    /// once, in a disposable module of its own that imports the current session (so it can see
    /// whatever the session has declared), following <c>lua.c</c>'s own precedent: try compiling
    /// the input as an expression to print (<c>return (input);</c>) first, and only fall back to a
    /// bare statement with no value if that fails. Because the wrapper module is thrown away and
    /// never re-run, a side effect in the input (a <c>print(...)</c>) happens exactly once - unlike
    /// the accumulated declarations above, nothing here is ever replayed.
    /// </para>
    /// <para>
    /// A <c>let</c>/<c>var</c> submitted here always becomes a module-level declaration - a static
    /// of its module (§2.5) - so, unlike a local, it needs an explicit type: §5.9 makes annotation
    /// mandatory for a member's declared type, with nothing to infer it from before any initializer
    /// is even bound. <see cref="LacksExplicitType"/> catches this up front with a clear message,
    /// rather than letting it reach the compiler - which (also confirmed while building this)
    /// accepts an untyped field/static all the way to <c>ModuleEmitter.TryEmit()</c> and only then
    /// fails with an internal-sounding "the error type '?' reached emission" instead of a clean
    /// diagnostic.
    /// </para>
    /// </remarks>
    internal sealed class ReplSession
    {
        private const string Root = "D:/surtr-repl";

        private static readonly HashSet<string> DeclarationKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "let", "var", "fun", "class", "interface", "enum", "singleton", "import", "extension", "native",
        };

        private static readonly HashSet<string> VisibilityModifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "public", "private", "protected", "internal",
        };

        private static readonly HashSet<string> OtherLeadingModifiers = new HashSet<string>(StringComparer.Ordinal)
        {
            "sealed", "abstract", "inline", "forceinline", "noinline",
        };

        private readonly SurtrRuntime _runtime;
        private readonly StringBuilder _imports = new StringBuilder();
        private readonly StringBuilder _declarations = new StringBuilder();
        private SurtrModule? _sessionModule;
        private int _sessionGeneration;
        private int _evalCounter;

        public ReplSession(SurtrRuntime runtime)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        /// <summary>Feeds one line of input to the session.</summary>
        public ReplOutcome Submit(string rawLine)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                return ReplOutcome.Empty;

            var (introducer, hasVisibility) = ClassifyDeclaration(line);
            return DeclarationKeywords.Contains(introducer)
                ? SubmitDeclaration(introducer, hasVisibility, line)
                : SubmitStatement(line);
        }

        /// <summary>
        /// Walks past any leading modifiers (<c>public sealed class</c>, <c>private inline fun</c>,
        /// ...) to find the word that actually introduces the declaration, and whether one of them
        /// already named a visibility.
        /// </summary>
        private static (string Introducer, bool HasVisibility) ClassifyDeclaration(string line)
        {
            int i = 0;
            bool hasVisibility = false;

            while (true)
            {
                while (i < line.Length && char.IsWhiteSpace(line[i]))
                    i++;

                int start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                    i++;

                string word = line.Substring(start, i - start);
                if (word.Length == 0)
                    return (string.Empty, hasVisibility);

                if (VisibilityModifiers.Contains(word))
                {
                    hasVisibility = true;
                    continue;
                }

                if (OtherLeadingModifiers.Contains(word))
                    continue;

                return (word, hasVisibility);
            }
        }

        /// <summary>
        /// Whether a <c>let</c>/<c>var</c> line has no <c>:</c> before its first <c>=</c> (or
        /// anywhere, if it has no initializer) - a module-level declaration is a static of its
        /// module (§2.5), and Language-Syntax.md §5.9 is explicit that a member's type is one of
        /// exactly three places annotation is mandatory: "a signature is the contract, so it is
        /// always written out". Unlike a local, there is no body-binding pass this could infer from
        /// even in principle - a field's type has to be known in the declaration phase, before any
        /// initializer is bound at all.
        /// </summary>
        private static bool LacksExplicitType(string declaration)
        {
            int equals = declaration.IndexOf('=');
            string beforeEquals = equals < 0 ? declaration : declaration.Substring(0, equals);
            return !beforeEquals.Contains(':');
        }

        private ReplOutcome SubmitDeclaration(string introducer, bool hasVisibility, string declaration)
        {
            if (introducer == "import")
                return SubmitImport(declaration);

            if ((introducer == "let" || introducer == "var") && LacksExplicitType(declaration))
            {
                return ReplOutcome.Failure(
                    "A module-level 'let'/'var' needs an explicit type - Surtr infers types for "
                        + "locals only, not fields or module-level statics (Language-Syntax.md §5.9). "
                        + "Try: '" + introducer + " name: Type = ...;'");
            }

            // A REPL session's whole point is that a later line can see what an earlier one
            // declared - and an earlier line is always a *different* module (one path per
            // successful declaration), so the internal default visibility (§3.1/§2.5) would make
            // everything unreachable the moment it needed to cross that boundary.
            string effective = hasVisibility ? declaration : "public " + declaration;

            string body = _imports.ToString() + _declarations.ToString() + effective + "\n";
            if (!TryRecompileSession(body, out var built, out string diagnostics))
                return ReplOutcome.Failure(diagnostics);

            _declarations.Append(effective).Append('\n');
            _sessionModule = built;
            return ReplOutcome.Ok(null);
        }

        private ReplOutcome SubmitImport(string importLine)
        {
            string candidateImports = _imports.ToString() + importLine + "\n";

            if (!TryRecompileSession(candidateImports + _declarations.ToString(), out var built, out string diagnostics))
                return ReplOutcome.Failure(diagnostics);

            _imports.Append(importLine).Append('\n');
            _sessionModule = built;
            return ReplOutcome.Ok(null);
        }

        /// <summary>
        /// Recompiles the whole session - <paramref name="body"/> is <c>_imports</c> plus whatever
        /// declarations this generation should hold - under a fresh, incrementing module path.
        /// </summary>
        private bool TryRecompileSession(string body, out SurtrModule? built, out string diagnostics)
        {
            string candidatePath = "surtr.repl.session" + _sessionGeneration;
            if (!TryCompileAndLoad(candidatePath, body, out built, out diagnostics))
                return false;

            _sessionGeneration++;
            return true;
        }

        private ReplOutcome SubmitStatement(string statement)
        {
            string preamble = _imports.ToString()
                + (_sessionModule is null ? string.Empty : $"import {_sessionModule.Path};\n");

            string exprSource = preamble + $"fun replEval(): unknown {{ return ({statement}); }}\n";
            if (TryCompileAndLoad(NextEvalModulePath(), exprSource, out var exprModule, out _)
                && exprModule!.TryGetMethods("replEval", out var exprOverloads))
            {
                return InvokeAndDescribe(exprOverloads[0]);
            }

            string stmtSource = preamble + $"fun replEval(): void {{ {statement} }}\n";
            if (!TryCompileAndLoad(NextEvalModulePath(), stmtSource, out var stmtModule, out string diagnostics))
                return ReplOutcome.Failure(diagnostics);

            stmtModule!.TryGetMethods("replEval", out var stmtOverloads);
            return InvokeAndDescribe(stmtOverloads[0]);
        }

        private string NextEvalModulePath() => "surtr.repl.eval" + _evalCounter++;

        private ReplOutcome InvokeAndDescribe(SurtrMethodInfo method)
        {
            SurtrValue result;
            try
            {
                result = _runtime.Invoke(method, Array.Empty<SurtrValue>());
            }
            catch (SurtrExecutionException exception)
            {
                // Leaves the interpreter mid-frame - reset before this session touches the runtime
                // again, the same rule Surtr.Run's own Program.Invoke already follows.
                _runtime.ResetExecution();
                return ReplOutcome.Failure(exception.Message);
            }

            return ReplOutcome.Ok(EntryPoint.Describe(_runtime, method, result));
        }

        /// <summary>
        /// Compiles <paramref name="source"/> as <paramref name="modulePath"/> and loads it.
        /// </summary>
        /// <remarks>
        /// Every module already in <c>_runtime.LoadedModules</c> - the standard library if the host
        /// loaded it, every earlier session generation, anything else this process put there - is
        /// referenced on the compilation. Compiling and loading are two different things:
        /// <c>LoadModule</c> resolving a type at *load* time is not what lets this compilation's
        /// *binder* see another module's declarations at all - an <c>import</c> only binds against a
        /// module the compilation itself was told about, via
        /// <see cref="SurtrProject.AddReference(SurtrModule)"/> (<c>SurtrCompilation.ImportReferences</c>
        /// feeds it straight to <c>MetadataImporter</c>). Referencing the already-instantiated
        /// modules directly, rather than round-tripping them through an image, is also what lets the
        /// eval side of a session see a mutable module-level <c>var</c> the declaration side wrote
        /// to a moment earlier - both compilations end up pointing at the same live storage.
        /// </remarks>
        private bool TryCompileAndLoad(string modulePath, string source, out SurtrModule? module, out string diagnostics)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile($"{Root}/{modulePath}.surtr", modulePath, source);

            foreach (var loadedAlready in _runtime.LoadedModules)
                project.AddReference(loadedAlready);

            var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            if (compilation.HasErrors)
            {
                module = null;
                diagnostics = string.Join("\n", compilation.Diagnostics.Select(d => d.ToString()));
                return false;
            }

            var emitter = new ModuleEmitter(compilation, binder);
            if (!emitter.TryEmit())
            {
                module = null;
                diagnostics = string.Join("\n", compilation.Diagnostics.Select(d => d.ToString()));
                return false;
            }

            try
            {
                SurtrModule? built = null;
                foreach (var loaded in emitter.Modules)
                {
                    _runtime.LoadModule(loaded);
                    if (loaded.Path == modulePath)
                        built = loaded;
                }

                module = built;
                diagnostics = string.Empty;
                return module is not null;
            }
            catch (InvalidOperationException exception)
            {
                module = null;
                diagnostics = exception.Message;
                return false;
            }
        }
    }
}
