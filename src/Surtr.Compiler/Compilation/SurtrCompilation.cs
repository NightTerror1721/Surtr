#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Surtr.Compiler.Compilation
{
    /// <summary>One parsed source file, with the module its location puts it in.</summary>
    public sealed class SurtrSourceUnit
    {
        internal SurtrSourceUnit(SurtrSourceFile file, string modulePath, CompilationUnitSyntax syntax)
        {
            File = file;
            ModulePath = modulePath;
            Syntax = syntax;
        }

        /// <summary>The file it came from.</summary>
        public SurtrSourceFile File { get; }

        /// <summary>
        /// The module it belongs to, derived from where it lives (§2.1): its directories under the
        /// source root plus its own file name.
        /// </summary>
        public string ModulePath { get; }

        /// <summary>Its syntax tree.</summary>
        public CompilationUnitSyntax Syntax { get; }
    }

    /// <summary>One parsed source file, which is to say one module's source (§2.1: a module is a file).</summary>
    public sealed class SurtrSourceModule
    {
        private readonly List<SurtrSourceUnit> _units = new List<SurtrSourceUnit>();

        internal SurtrSourceModule(string path) => Path = path;

        /// <summary>The module's dotted path.</summary>
        public string Path { get; }

        /// <summary>The files contributing to it — always exactly one, since a module is a file.</summary>
        public IReadOnlyList<SurtrSourceUnit> Units => _units;

        internal void Add(SurtrSourceUnit unit) => _units.Add(unit);
    }

    /// <summary>
    /// A whole compilation: the project's files parsed, grouped into modules, ordered by
    /// dependency, and joined to whatever metadata the project references.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is everything before binding. It answers three questions the binder needs settled
    /// first: which module each file belongs to, what order the modules can be loaded in, and what
    /// already-built types are in scope.
    /// </para>
    /// <para>
    /// It never throws on a bad compilation. Everything wrong lands in <see cref="Diagnostics"/>,
    /// which is the same bag the lexer and parser reported into, so one bag holds everything wrong
    /// with the project.
    /// </para>
    /// </remarks>
    public sealed class SurtrCompilation : IDisposable, IModuleResolver
    {
        private readonly Dictionary<string, SurtrSourceModule> _modules =
            new Dictionary<string, SurtrSourceModule>(StringComparer.Ordinal);

        private readonly List<SurtrSourceModule> _ordered = new List<SurtrSourceModule>();

        private readonly ISourceProvider _sourceProvider;

        private SurtrCompilation(SurtrProject project)
        {
            Project = project;
            Diagnostics = new SurtrDiagnosticBag();
            Dependencies = new ModuleDependencyGraph();
            TypeFactory = new TypeSymbolFactory();
            Importer = new MetadataImporter(TypeFactory);
            _sourceProvider = project.SourceProvider;
        }

        /// <summary>The project this was created from.</summary>
        public SurtrProject Project { get; }

        /// <summary>Everything wrong with the project, from every stage that has run.</summary>
        public SurtrDiagnosticBag Diagnostics { get; }

        /// <summary>Which module reaches which, accumulated as more is discovered.</summary>
        public ModuleDependencyGraph Dependencies { get; }

        /// <summary>The factory every type in this compilation is interned through.</summary>
        public TypeSymbolFactory TypeFactory { get; }

        /// <summary>The gate that turns referenced metadata into symbols.</summary>
        public MetadataImporter Importer { get; }

        /// <summary>The source modules, keyed by path.</summary>
        public IReadOnlyDictionary<string, SurtrSourceModule> Modules => _modules;

        /// <summary>
        /// The source modules in load order — each after everything it depends on. Empty when the
        /// dependency graph has a cycle, which is reported rather than resolved.
        /// </summary>
        /// <remarks>
        /// Computed from <see cref="Dependencies"/> as it stands at the time it is read.
        /// <see cref="Create"/> computes it once from the import-derived edges alone, which is
        /// already right for anything that only reaches another module through an <c>import</c>. A
        /// fully qualified reference with no <c>import</c> (§2.6 allows one) adds its edge lazily,
        /// once the binder actually resolves it — see <see cref="TypeResolver"/>'s constructor — so
        /// <see cref="RefreshLoadOrder"/> exists for whoever needs the order to reflect binding that
        /// has run since.
        /// </remarks>
        public IReadOnlyList<SurtrSourceModule> LoadOrder => _ordered;

        /// <summary>
        /// Recomputes <see cref="LoadOrder"/> from <see cref="Dependencies"/> as it stands right now.
        /// </summary>
        /// <remarks>
        /// <c>CodeGen.ModuleEmitter</c> is the one caller that needs this: it emits in
        /// <see cref="LoadOrder"/>, and a call reaching another module by a fully qualified name with
        /// no <c>import</c> only adds that module's dependency edge once binding actually resolves
        /// the name (<see cref="TypeResolver"/>) — which, by the time it runs, it always has. Calling
        /// this before binding has run would just reproduce <see cref="Create"/>'s import-only order,
        /// since nothing else would have added to the graph yet.
        /// </remarks>
        internal void RefreshLoadOrder() => Order();

        /// <summary>Whether anything reported an error.</summary>
        public bool HasErrors => Diagnostics.HasErrors;

        /// <summary>
        /// Runs the binder's declaration and member phases, once. Everything they find joins
        /// <see cref="Diagnostics"/>.
        /// </summary>
        public Binder Bind() => _binder ??= Binder.Bind(this);

        private Binder? _binder;

        /// <summary>
        /// Releases what binding held: the scratch runtime const folding ran on, if it built one.
        /// </summary>
        /// <remarks>
        /// A compilation owns its binder, so it owns what the binder owns. Nothing is allocated
        /// unless the source declares a <c>const fun</c>, so a compilation that never folds anything
        /// has nothing to release and disposing it costs nothing.
        /// </remarks>
        public void Dispose() => _binder?.Dispose();

        /// <summary>
        /// Parses every file in <paramref name="project"/>, groups them into modules, orders the
        /// modules, and imports the referenced metadata.
        /// </summary>
        public static SurtrCompilation Create(SurtrProject project)
        {
            if (project is null)
                throw new ArgumentNullException(nameof(project));

            var compilation = new SurtrCompilation(project);
            compilation.ImportReferences();
            compilation.ParseSources();
            compilation.BuildDependencyGraph();
            compilation.Order();
            return compilation;
        }

        private void ImportReferences()
        {
            foreach (var module in Project.ReferencedModules)
                Importer.ImportModule(module);

            foreach (var image in Project.ReferencedImages)
                Importer.ImportModule(image.Instantiate());

            foreach (var type in Project.HostTypes)
                Importer.AddHostType(type);
        }

        private void ParseSources()
        {
            var files = Project.SourceFiles;

            // Parsing a file is independent of every other: derive its module, lex it, parse it —
            // the only shared state is read-only. Each file parses into its own bag and its own slot,
            // and the results merge back in file order, which keeps diagnostic report order exactly
            // what the sequential path produced. Bind, the dependency graph and emit stay sequential.
            var results = new FileParseResult[files.Count];
            Parallel.For(0, files.Count, i =>
            {
                results[i] = ParseFile(files[i]);
            });

            for (int i = 0; i < results.Length; i++)
                MergeParseResult(results[i]);
        }

        private readonly struct FileParseResult
        {
            internal FileParseResult(
                SurtrSourceFile file,
                string modulePath,
                CompilationUnitSyntax? syntax,
                SurtrDiagnosticBag bag,
                ModulePathStatus status,
                string offendingSegment)
            {
                File = file;
                ModulePath = modulePath;
                Syntax = syntax;
                Bag = bag;
                Status = status;
                OffendingSegment = offendingSegment;
            }

            internal SurtrSourceFile File { get; }
            internal string ModulePath { get; }
            internal CompilationUnitSyntax? Syntax { get; }
            internal SurtrDiagnosticBag Bag { get; }
            internal ModulePathStatus Status { get; }
            internal string OffendingSegment { get; }
        }

        /// <summary>The whole per-file front end, isolated so it can run on any thread.</summary>
        private FileParseResult ParseFile(SurtrSourceFile file)
        {
            string modulePath = file.ModulePath;
            ModulePathStatus status = ModulePathStatus.Ok;
            string offendingSegment = string.Empty;

            // A file that names its own module overrides §2.1's derivation from its location.
            if (modulePath is null)
            {
                status = ModulePath.TryDerive(
                    Project.SourceRoot,
                    file.Path,
                    Project.RootModulePath,
                    out modulePath,
                    out offendingSegment);
            }

            var bag = new SurtrDiagnosticBag();
            CompilationUnitSyntax? syntax = null;

            if (status == ModulePathStatus.Ok && ModulePath.IsValid(modulePath))
            {
                var parser = new Parser(SurtrSourceBuffer.FromString(file.Text, file.Path), bag);
                syntax = parser.ParseCompilationUnit();
            }

            return new FileParseResult(file, modulePath, syntax, bag, status, offendingSegment);
        }

        /// <summary>Folds one file's parallel result into the compilation, in the sequential path's reporting order.</summary>
        private void MergeParseResult(in FileParseResult result)
        {
            if (result.Status != ModulePathStatus.Ok)
            {
                ReportModulePath(result.File, result.Status, result.OffendingSegment);
                return;
            }

            if (!ModulePath.IsValid(result.ModulePath))
            {
                // An explicit module path that is not a legal dotted path cannot be named by an
                // import and is not something to keep around.
                Diagnostics.ReportError(
                    SurtrDiagnosticCode.InvalidModulePath,
                    $"'{result.ModulePath}' is not a legal module path.",
                    result.File.Path,
                    span: default);
                return;
            }

            if (result.Bag.Count > 0)
                Diagnostics.AddRange(result.Bag);

            if (result.Syntax is null)
                return;

            if (!_modules.TryGetValue(result.ModulePath, out var module))
            {
                module = new SurtrSourceModule(result.ModulePath);
                _modules.Add(result.ModulePath, module);
            }

            module.Add(new SurtrSourceUnit(result.File, result.ModulePath, result.Syntax));
        }

        /// <summary>Parses one source text into the compilation's module set, creating the module if needed.</summary>
        private void ParseSourceInto(string modulePath, string sourcePath, string text)
        {
            if (!ModulePath.IsValid(modulePath))
            {
                // An explicit module path that is not a legal dotted path cannot be named by an
                // import and is not something to keep around.
                Diagnostics.ReportError(
                    SurtrDiagnosticCode.InvalidModulePath,
                    $"'{modulePath}' is not a legal module path.",
                    sourcePath,
                    span: default);
                return;
            }

            // One bag for the whole project: the lexer and parser report into the same place
            // everything after them does, so a caller checks once.
            var parser = new Parser(SurtrSourceBuffer.FromString(text, sourcePath), Diagnostics);
            var syntax = parser.ParseCompilationUnit();

            if (!_modules.TryGetValue(modulePath, out var module))
            {
                module = new SurtrSourceModule(modulePath);
                _modules.Add(modulePath, module);
            }

            module.Add(new SurtrSourceUnit(new SurtrSourceFile(sourcePath, text, modulePath), modulePath, syntax));
        }

        private void BuildDependencyGraph()
        {
            // A module resolved lazily through a provider joins `_modules` while the graph is being
            // built, so the walk runs to a fixed point: each pass processes every module not yet
            // processed, and a pass that loaded new modules starts another. The set grows only from
            // provider loads, so it always terminates.
            var processed = new HashSet<string>(StringComparer.Ordinal);

            while (true)
            {
                SurtrSourceModule[] batch = _modules.Values.ToArray();
                bool loadedNew = false;

                foreach (var module in batch)
                {
                    if (!processed.Add(module.Path))
                        continue;

                    ProcessModuleImports(module);

                    // Processing a module may have loaded more through its imports; the next pass
                    // picks them up. Detected cheaply: a fresh count means new modules joined.
                    loadedNew |= _modules.Count > batch.Length;
                }

                if (!loadedNew)
                    break;
            }
        }

        private void ProcessModuleImports(SurtrSourceModule module)
        {
            Dependencies.AddModule(module.Path);

            foreach (var unit in module.Units)
            {
                foreach (var import in unit.Syntax.Imports)
                {
                    if (import.IsWildcard)
                    {
                        // A directory wildcard (§2.1, Fase 9) may resolve to the exact module,
                        // to one or more submodules nested under it, or both at once - so it
                        // gets its own resolution instead of `TryResolveImport`'s one-target
                        // shape, and one dependency edge per module it actually matched.
                        string prefix = Prefix(import.Path, import.Path.Count);
                        bool matchedAny = false;

                        if (KnowsModule(prefix))
                        {
                            Dependencies.AddDependency(module.Path, prefix);
                            matchedAny = true;
                        }

                        foreach (string nested in ModulesUnderPrefix(prefix))
                        {
                            Dependencies.AddDependency(module.Path, nested);
                            matchedAny = true;
                        }

                        if (!matchedAny)
                        {
                            Diagnostics.ReportError(
                                SurtrDiagnosticCode.UnresolvedImport,
                                $"No module provides '{string.Join(".", import.Path)}'.",
                                unit.File.Path,
                                import.Span);
                        }

                        continue;
                    }

                    if (import.Alias is null && import.Members is null)
                    {
                        // A path that resolves entirely as a module - or has submodules
                        // nested under it, even without a module of its own - is the longest
                        // possible module prefix, so it wins over any shorter prefix + type
                        // name `TryResolveImport` would try (§2.1: `import ModulePath;` is
                        // then equivalent to `import ModulePath.*;`), and may match several
                        // modules at once exactly like a wildcard does - so it gets the same
                        // multi-edge resolution instead of `TryResolveImport`'s one-target
                        // shape.
                        string wholePath = Prefix(import.Path, import.Path.Count);
                        bool matchedWhole = false;

                        if (KnowsModule(wholePath))
                        {
                            Dependencies.AddDependency(module.Path, wholePath);
                            matchedWhole = true;
                        }

                        foreach (string nested in ModulesUnderPrefix(wholePath))
                        {
                            Dependencies.AddDependency(module.Path, nested);
                            matchedWhole = true;
                        }

                        if (matchedWhole)
                            continue;
                    }

                    if (!TryResolveImport(import, out string target))
                    {
                        Diagnostics.ReportError(
                            SurtrDiagnosticCode.UnresolvedImport,
                            $"No module provides '{string.Join(".", import.Path)}'.",
                            unit.File.Path,
                            import.Span);

                        continue;
                    }

                    Dependencies.AddDependency(module.Path, target);
                }
            }
        }

        /// <inheritdoc/>
        public IEnumerable<string> ModulesUnderPrefix(string prefix)
        {
            string dotted = prefix + ModulePath.Separator;
            foreach (string path in _modules.Keys)
            {
                if (path.StartsWith(dotted, StringComparison.Ordinal))
                    yield return path;
            }
        }

        /// <summary>
        /// Works out which module a non-wildcard import names.
        /// </summary>
        /// <remarks>
        /// A wildcard import gets its own resolution in <see cref="BuildDependencyGraph"/> (it may
        /// match several modules at once, §2.1's Fase 9) and never reaches this method. An aliased
        /// or selective-list import names a module outright by its whole path. A plain named one
        /// names a type, and only the modules that exist say where the module path ends and the
        /// type name begins — so the longest known prefix wins, which is also what makes a nested
        /// type importable.
        /// </remarks>
        private bool TryResolveImport(ImportSyntax import, out string modulePath)
        {
            var segments = import.Path;

            if (import.Alias is not null || import.Members is not null)
            {
                // An aliased import and a selective list both name a module outright - unlike a
                // plain named import, there is no trailing type name to peel off the end.
                modulePath = Prefix(segments, segments.Count);
                return KnowsModule(modulePath);
            }

            for (int length = segments.Count - 1; length > 0; length--)
            {
                string candidate = Prefix(segments, length);
                if (KnowsModule(candidate))
                {
                    modulePath = candidate;
                    return true;
                }
            }

            modulePath = string.Empty;
            return false;
        }

        private static string Prefix(IReadOnlyList<string> segments, int length)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < length; i++)
            {
                if (i > 0)
                    builder.Append(ModulePath.Separator);

                builder.Append(segments[i]);
            }

            return builder.ToString();
        }

        /// <inheritdoc/>
        public bool KnowsModule(string modulePath)
        {
            if (_modules.ContainsKey(modulePath) || Importer.KnowsModule(modulePath))
                return true;

            // Lazy resolution (§2.1's provider seam): a module the project did not hand over up
            // front may still be reachable through the source provider, which parses it on demand
            // and joins it to the compilation. This is what lets an `import` resolve a module that
            // was never enumerated up front - an embedding host's in-memory tree, say.
            return TryGetSourceModule(modulePath) is not null;
        }

        /// <summary>
        /// The parsed source module for a path, loading it through the project's
        /// <see cref="ISourceProvider"/> on first use when it is not already in the compilation.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the lazy-loading seam: an <c>import</c> that names a module the project did not
        /// hand over up front can still resolve, provided some provider can supply its source. The
        /// module is parsed and joined to the compilation's module set, so everything after — the
        /// dependency graph, the binder — sees it exactly as it would a module discovered up front.
        /// </para>
        /// <para>
        /// Calling this may add a module and, through it, more imports; the dependency graph is
        /// append-only, so the new module's own dependencies are discovered when the graph is next
        /// asked to order. The caller is responsible for triggering that recompute after a
        /// successful load.
        /// </para>
        /// </remarks>
        /// <param name="modulePath">The dotted module path (§2.1).</param>
        /// <returns>The parsed module, or <see langword="null"/> when no provider supplies it.</returns>
        public SurtrSourceModule? TryGetSourceModule(string modulePath)
        {
            if (_modules.TryGetValue(modulePath, out var existing))
                return existing;

            if (Importer.KnowsModule(modulePath))
                return null;

            if (!_sourceProvider.TryGetSource(modulePath, out string text, out string diagnosticPath))
                return null;

            ParseSourceInto(modulePath, diagnosticPath, text);

            Dependencies.AddModule(modulePath);
            RefreshLoadOrder();
            return _modules.TryGetValue(modulePath, out var loaded) ? loaded : null;
        }

        private void Order()
        {
            // Re-runnable: RefreshLoadOrder calls this again once binding may have added edges
            // Create()'s own call never saw, and an append-only rebuild would just duplicate every
            // module already placed the first time.
            _ordered.Clear();

            if (!Dependencies.TryGetLoadOrder(out var order, out var cycle))
            {
                Diagnostics.ReportError(
                    SurtrDiagnosticCode.ModuleCycle,
                    "Modules depend on one another in a cycle, which has no load order: "
                        + string.Join(" -> ", cycle) + ".",
                    FirstSourceFileIn(cycle),
                    span: default);

                return;
            }

            foreach (string modulePath in order)
            {
                // A referenced module is in the graph too, but it is already built and has no
                // source to order.
                if (_modules.TryGetValue(modulePath, out var module))
                    _ordered.Add(module);
            }
        }

        /// <summary>
        /// A file to hang a cycle's diagnostic on. A cycle belongs to no single line, but pointing
        /// at one of the modules involved beats pointing at nothing.
        /// </summary>
        private string FirstSourceFileIn(IReadOnlyList<string> cycle)
        {
            for (int i = 0; i < cycle.Count; i++)
            {
                if (_modules.TryGetValue(cycle[i], out var module) && module.Units.Count > 0)
                    return module.Units[0].File.Path;
            }

            return Project.SourceRoot;
        }

        private void ReportModulePath(SurtrSourceFile file, ModulePathStatus status, string offendingSegment)
        {
            string message = status switch
            {
                ModulePathStatus.OutsideSourceRoot =>
                    $"'{file.Path}' is not under the source root '{Project.SourceRoot}', so nothing gives it a module.",

                _ => $"The segment '{offendingSegment}' is not a legal identifier, "
                        + "so no import could ever name the module it would create.",
            };

            Diagnostics.ReportError(SurtrDiagnosticCode.InvalidModulePath, message, file.Path, span: default);
        }
    }
}
