#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;
using System;
using System.Collections.Generic;
using System.Text;

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

        /// <summary>The module it belongs to, derived from where it lives (§2.1).</summary>
        public string ModulePath { get; }

        /// <summary>Its syntax tree.</summary>
        public CompilationUnitSyntax Syntax { get; }
    }

    /// <summary>Every file in one directory, which is to say one module's source.</summary>
    public sealed class SurtrSourceModule
    {
        private readonly List<SurtrSourceUnit> _units = new List<SurtrSourceUnit>();

        internal SurtrSourceModule(string path) => Path = path;

        /// <summary>The module's dotted path.</summary>
        public string Path { get; }

        /// <summary>The files contributing to it.</summary>
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
    public sealed class SurtrCompilation
    {
        private readonly Dictionary<string, SurtrSourceModule> _modules =
            new Dictionary<string, SurtrSourceModule>(StringComparer.Ordinal);

        private readonly List<SurtrSourceModule> _ordered = new List<SurtrSourceModule>();

        private SurtrCompilation(SurtrProject project)
        {
            Project = project;
            Diagnostics = new SurtrDiagnosticBag();
            Dependencies = new ModuleDependencyGraph();
            TypeFactory = new TypeSymbolFactory();
            Importer = new MetadataImporter(TypeFactory);
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
        public IReadOnlyList<SurtrSourceModule> LoadOrder => _ordered;

        /// <summary>Whether anything reported an error.</summary>
        public bool HasErrors => Diagnostics.HasErrors;

        /// <summary>
        /// Runs the binder's declaration and member phases, once. Everything they find joins
        /// <see cref="Diagnostics"/>.
        /// </summary>
        public Binder Bind() => _binder ??= Binder.Bind(this);

        private Binder? _binder;

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
            foreach (var file in Project.SourceFiles)
            {
                var status = ModulePath.TryDerive(
                    Project.SourceRoot,
                    file.Path,
                    Project.RootModulePath,
                    out string modulePath,
                    out string offendingSegment);

                if (status != ModulePathStatus.Ok)
                {
                    ReportModulePath(file, status, offendingSegment);
                    continue;
                }

                // One bag for the whole project: the lexer and parser report into the same place
                // everything after them does, so a caller checks once.
                var parser = new Parser(SurtrSourceBuffer.FromString(file.Text, file.Path), Diagnostics);
                var syntax = parser.ParseCompilationUnit();

                if (!_modules.TryGetValue(modulePath, out var module))
                {
                    module = new SurtrSourceModule(modulePath);
                    _modules.Add(modulePath, module);
                }

                module.Add(new SurtrSourceUnit(file, modulePath, syntax));
            }
        }

        private void BuildDependencyGraph()
        {
            foreach (var module in _modules.Values)
            {
                Dependencies.AddModule(module.Path);

                foreach (var unit in module.Units)
                {
                    foreach (var import in unit.Syntax.Imports)
                    {
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
        }

        /// <summary>
        /// Works out which module an import names.
        /// </summary>
        /// <remarks>
        /// A wildcard import names a module outright. A named one names a type, and only the
        /// modules that exist say where the module path ends and the type name begins — so the
        /// longest known prefix wins, which is also what makes a nested type importable.
        /// </remarks>
        private bool TryResolveImport(ImportSyntax import, out string modulePath)
        {
            var segments = import.Path;

            if (import.IsWildcard)
            {
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

        private bool KnowsModule(string modulePath)
            => _modules.ContainsKey(modulePath) || Importer.KnowsModule(modulePath);

        private void Order()
        {
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

                ModulePathStatus.Empty =>
                    $"'{file.Path}' sits at the source root and the project declares no root module path, "
                        + "so there is no module for it to belong to.",

                _ => $"The directory '{offendingSegment}' is not a legal identifier, "
                        + "so no import could ever name the module it would create.",
            };

            Diagnostics.ReportError(SurtrDiagnosticCode.InvalidModulePath, message, file.Path, span: default);
        }
    }
}
