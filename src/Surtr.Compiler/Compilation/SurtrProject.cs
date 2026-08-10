#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Runtime.Classes;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Compilation
{
    /// <summary>What kind of value a build-defined constant holds.</summary>
    public enum BuildConstantKind
    {
        /// <summary>An <c>int</c>.</summary>
        Int,

        /// <summary>A <c>float</c>.</summary>
        Float,

        /// <summary>A <c>bool</c>.</summary>
        Bool,

        /// <summary>A <c>char</c>.</summary>
        Char,

        /// <summary>A <c>string</c>.</summary>
        String,
    }

    /// <summary>
    /// A constant the build supplies rather than a source file (§7.4) — the equivalent of
    /// <c>UNITY_EDITOR</c>.
    /// </summary>
    /// <remarks>
    /// It behaves exactly as if declared <c>const</c> at the top of every module, so
    /// <c>const if (Debug)</c> needs no import and no special syntax. A build constant always has a
    /// value: there is no "is this defined" test, and an optional flag is defined as <c>false</c>
    /// rather than left absent, which is what turns a typo in a flag name into an undefined-name
    /// error instead of a silently false <c>#ifdef</c>.
    /// </remarks>
    public readonly struct BuildConstant
    {
        private BuildConstant(BuildConstantKind kind, object value)
        {
            Kind = kind;
            Value = value;
        }

        /// <summary>What kind of value this holds.</summary>
        public BuildConstantKind Kind { get; }

        /// <summary>The value, boxed. Constant folding is not a hot path.</summary>
        public object Value { get; }

        /// <summary>A build constant holding an <c>int</c>.</summary>
        public static BuildConstant Int(int value) => new BuildConstant(BuildConstantKind.Int, value);

        /// <summary>A build constant holding a <c>float</c>.</summary>
        public static BuildConstant Float(double value) => new BuildConstant(BuildConstantKind.Float, value);

        /// <summary>A build constant holding a <c>bool</c>.</summary>
        public static BuildConstant Bool(bool value) => new BuildConstant(BuildConstantKind.Bool, value);

        /// <summary>A build constant holding a <c>char</c>.</summary>
        public static BuildConstant Char(char value) => new BuildConstant(BuildConstantKind.Char, value);

        /// <summary>A build constant holding a <c>string</c>.</summary>
        public static BuildConstant String(string value)
            => new BuildConstant(BuildConstantKind.String, value ?? throw new ArgumentNullException(nameof(value)));

        /// <inheritdoc/>
        public override string ToString() => Value?.ToString() ?? string.Empty;
    }

    /// <summary>One source file, with the text to compile and the path that gives it its module.</summary>
    public sealed class SurtrSourceFile
    {
        /// <summary>Creates a source file.</summary>
        /// <param name="path">Where it lives, which decides its module (§2.1).</param>
        /// <param name="text">Its contents.</param>
        public SurtrSourceFile(string path, string text)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Text = text ?? throw new ArgumentNullException(nameof(text));
        }

        /// <summary>Where the file lives.</summary>
        public string Path { get; }

        /// <summary>Its contents.</summary>
        public string Text { get; }

        /// <inheritdoc/>
        public override string ToString() => Path;
    }

    /// <summary>
    /// Everything a compilation needs that is not source: where the source root is, what the
    /// modules already built are, and what the build defines.
    /// </summary>
    /// <remarks>
    /// The source-root configuration is deliberately a compiler concern rather than a syntax one
    /// (§2.1), which is why it lives here and not in any file's text.
    /// </remarks>
    public sealed class SurtrProject
    {
        private readonly List<SurtrSourceFile> _sourceFiles = new List<SurtrSourceFile>();
        private readonly List<SurtrModuleImage> _referencedImages = new List<SurtrModuleImage>();
        private readonly List<SurtrModule> _referencedModules = new List<SurtrModule>();
        private readonly List<SurtrClass> _hostTypes = new List<SurtrClass>();

        private readonly Dictionary<string, BuildConstant> _buildConstants =
            new Dictionary<string, BuildConstant>(StringComparer.Ordinal);

        /// <summary>Creates a project.</summary>
        /// <param name="sourceRoot">The directory module paths are derived relative to.</param>
        /// <param name="rootModulePath">
        /// What the source root itself is called. Empty means a file directly at the root belongs
        /// to no module, which is reported rather than allowed.
        /// </param>
        public SurtrProject(string sourceRoot, string rootModulePath = "")
        {
            SourceRoot = sourceRoot ?? throw new ArgumentNullException(nameof(sourceRoot));
            RootModulePath = rootModulePath ?? string.Empty;
        }

        /// <summary>The directory module paths are derived relative to.</summary>
        public string SourceRoot { get; }

        /// <summary>What the source root itself is called, prefixed onto every derived module path.</summary>
        public string RootModulePath { get; }

        /// <summary>The files to compile.</summary>
        public IReadOnlyList<SurtrSourceFile> SourceFiles => _sourceFiles;

        /// <summary>Compiled modules referenced as <c>.surtrc</c> images.</summary>
        public IReadOnlyList<SurtrModuleImage> ReferencedImages => _referencedImages;

        /// <summary>Modules already instantiated, referenced directly.</summary>
        public IReadOnlyList<SurtrModule> ReferencedModules => _referencedModules;

        /// <summary>Types the embedding host declares, which no Surtr source produced.</summary>
        public IReadOnlyList<SurtrClass> HostTypes => _hostTypes;

        /// <summary>The constants the build defines (§7.4).</summary>
        public IReadOnlyDictionary<string, BuildConstant> BuildConstants => _buildConstants;

        /// <summary>Adds a file to compile.</summary>
        public SurtrProject AddSourceFile(SurtrSourceFile file)
        {
            _sourceFiles.Add(file ?? throw new ArgumentNullException(nameof(file)));
            return this;
        }

        /// <summary>Adds a file to compile.</summary>
        public SurtrProject AddSourceFile(string path, string text) => AddSourceFile(new SurtrSourceFile(path, text));

        /// <summary>References a compiled module by its image.</summary>
        public SurtrProject AddReference(SurtrModuleImage image)
        {
            _referencedImages.Add(image ?? throw new ArgumentNullException(nameof(image)));
            return this;
        }

        /// <summary>References a module that is already instantiated.</summary>
        public SurtrProject AddReference(SurtrModule module)
        {
            _referencedModules.Add(module ?? throw new ArgumentNullException(nameof(module)));
            return this;
        }

        /// <summary>References a type the host declares.</summary>
        public SurtrProject AddHostType(SurtrClass type)
        {
            _hostTypes.Add(type ?? throw new ArgumentNullException(nameof(type)));
            return this;
        }

        /// <summary>Defines a build constant (§7.4).</summary>
        /// <exception cref="ArgumentException"><paramref name="name"/> is not a legal identifier, or is already defined.</exception>
        public SurtrProject Define(string name, BuildConstant value)
        {
            if (!ModulePath.IsValidSegment(name))
                throw new ArgumentException($"'{name}' is not a legal name for a build constant.", nameof(name));

            if (_buildConstants.ContainsKey(name))
                throw new ArgumentException($"The build already defines '{name}'.", nameof(name));

            _buildConstants.Add(name, value);
            return this;
        }
    }
}
