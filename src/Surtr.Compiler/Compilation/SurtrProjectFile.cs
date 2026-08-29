#nullable enable

using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Surtr.Compiler.Compilation
{
    /// <summary>
    /// A build's own settings, read from a project file (§14.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// §2.1 derives a module from where a file lives and calls the source-root configuration a
    /// compiler concern rather than a syntax one; this is that concern, written down. §7.4 needs
    /// somewhere for build constants to come from, and §3.6 needs a way to name the modules already
    /// compiled — the same file answers both.
    /// </para>
    /// <para>
    /// The format is one directive per line, because the alternative is a dependency. Surtr.Compiler
    /// targets <c>netstandard2.1</c> so it can sit beside the runtime in Unity, where a JSON
    /// serializer is a package the host would also have to ship — for six settings that is a bad
    /// trade. Anything a line cannot express belongs in a host that builds a
    /// <see cref="SurtrProject"/> itself, which stays the primary API.
    /// </para>
    /// <code>
    /// # game.surtrproj
    /// root    = src
    /// module  = game
    /// output  = build
    /// define  Debug = true
    /// define  Platform = "IL2CPP"
    /// reference ../engine/engine.surtrc
    /// </code>
    /// </remarks>
    public sealed class SurtrProjectFile
    {
        private SurtrProjectFile(string path, string directory)
        {
            Path = path;
            Directory = directory;
        }

        private SurtrProjectFile(string path)
            : this(path, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ".")
        {
        }

        /// <summary>Where the project file itself is.</summary>
        public string Path { get; }

        /// <summary>The directory every relative path in it is relative to.</summary>
        public string Directory { get; }

        /// <summary>The source root, relative to <see cref="Directory"/>. Defaults to <c>src</c>.</summary>
        public string Root { get; private set; } = "src";

        /// <summary>What the source root itself is called, prefixed onto every module path (§2.1).</summary>
        public string RootModulePath { get; private set; } = string.Empty;

        /// <summary>Where built images are written. Defaults to <c>build</c>.</summary>
        public string Output { get; private set; } = "build";

        /// <summary>The <c>.surtrc</c> images this project compiles against.</summary>
        public IReadOnlyList<string> References => _references;

        /// <summary>The constants the build defines (§7.4).</summary>
        public IReadOnlyDictionary<string, BuildConstant> Constants => _constants;

        /// <summary>Whether every warning is treated as an error for the purpose of <c>SurtrBuild.Failed</c>. Defaults to <see langword="false"/>.</summary>
        public bool WarningsAsErrors { get; private set; }

        /// <summary>Diagnostic codes silenced entirely, named by <c>suppress</c> directives.</summary>
        public IReadOnlyCollection<SurtrDiagnosticCode> SuppressedCodes => _suppressedCodes;

        private readonly List<string> _references = new List<string>();

        private readonly Dictionary<string, BuildConstant> _constants =
            new Dictionary<string, BuildConstant>(StringComparer.Ordinal);

        private readonly HashSet<SurtrDiagnosticCode> _suppressedCodes = new HashSet<SurtrDiagnosticCode>();

        /// <summary>Reads a project file, reporting anything malformed rather than throwing.</summary>
        /// <param name="path">The file to read.</param>
        /// <param name="diagnostics">Where problems are recorded.</param>
        public static SurtrProjectFile Read(string path, SurtrDiagnosticBag diagnostics)
        {
            if (path is null)
                throw new ArgumentNullException(nameof(path));

            if (diagnostics is null)
                throw new ArgumentNullException(nameof(diagnostics));

            var project = new SurtrProjectFile(path);

            if (!File.Exists(path))
            {
                diagnostics.ReportError(
                    SurtrDiagnosticCode.ProjectFileInvalid,
                    $"There is no project file at '{path}'.",
                    path,
                    span: default);

                return project;
            }

            return ReadLines(project, File.ReadAllLines(path), diagnostics);
        }

        /// <summary>
        /// Parses a project file's text directly, for a host with no real file to point
        /// <see cref="Read"/> at - project settings stored in memory, in an asset database, or
        /// wherever else a host without a real filesystem keeps them.
        /// </summary>
        /// <param name="text">The project file's contents.</param>
        /// <param name="virtualDirectory">
        /// The directory every relative <c>root</c>/<c>reference</c> path is resolved against -
        /// what <see cref="Directory"/> would otherwise be derived from a real file's location.
        /// Also used as <see cref="Path"/>, since there is no file to name.
        /// </param>
        /// <param name="diagnostics">Where problems are recorded.</param>
        public static SurtrProjectFile Parse(string text, string virtualDirectory, SurtrDiagnosticBag diagnostics)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));

            if (virtualDirectory is null)
                throw new ArgumentNullException(nameof(virtualDirectory));

            if (diagnostics is null)
                throw new ArgumentNullException(nameof(diagnostics));

            var project = new SurtrProjectFile(virtualDirectory, virtualDirectory);
            return ReadLines(project, text.Replace("\r\n", "\n").Split('\n'), diagnostics);
        }

        private static SurtrProjectFile ReadLines(SurtrProjectFile project, string[] lines, SurtrDiagnosticBag diagnostics)
        {
            for (int i = 0; i < lines.Length; i++)
                project.ReadLine(lines[i], i + 1, diagnostics);

            return project;
        }

        private void ReadLine(string line, int number, SurtrDiagnosticBag diagnostics)
        {
            string text = line.Trim();

            if (text.Length == 0 || text[0] == '#')
                return;

            if (TryTake(text, "define", out string rest))
            {
                ReadDefine(rest, number, diagnostics);
                return;
            }

            if (TryTake(text, "reference", out rest))
            {
                if (rest.Length == 0)
                    Invalid(number, "a 'reference' needs a path to a .surtrc image", diagnostics);
                else
                    _references.Add(Unquote(rest));

                return;
            }

            if (TryTake(text, "suppress", out rest))
            {
                ReadSuppress(rest, number, diagnostics);
                return;
            }

            int equals = text.IndexOf('=');
            if (equals < 0)
            {
                Invalid(number, $"'{text}' is not a setting, a 'define' or a 'reference'", diagnostics);
                return;
            }

            string key = text.Substring(0, equals).Trim();
            string value = Unquote(text.Substring(equals + 1).Trim());

            switch (key)
            {
                case "root": Root = value; return;
                case "module": RootModulePath = value; return;
                case "output": Output = value; return;

                case "warningsAsErrors":
                    if (bool.TryParse(value, out bool warningsAsErrors))
                        WarningsAsErrors = warningsAsErrors;
                    else
                        Invalid(number, $"'{value}' is not 'true' or 'false'", diagnostics);
                    return;

                default:
                    Invalid(number, $"'{key}' is not a setting this build understands", diagnostics);
                    return;
            }
        }

        /// <summary>
        /// Reads <c>suppress Code1, Code2</c>: a comma-separated list of diagnostic codes to drop
        /// entirely, named either by their <see cref="SurtrDiagnosticCode"/> member
        /// (<c>ProjectFileInvalid</c>) or by their numeric value (<c>2001</c>).
        /// </summary>
        private void ReadSuppress(string rest, int number, SurtrDiagnosticBag diagnostics)
        {
            if (rest.Length == 0)
            {
                Invalid(number, "a 'suppress' needs at least one diagnostic code", diagnostics);
                return;
            }

            foreach (string token in rest.Split(','))
            {
                string name = token.Trim();
                if (name.Length == 0)
                    continue;

                if (TryParseDiagnosticCode(name, out var code))
                    _suppressedCodes.Add(code);
                else
                    Invalid(number, $"'{name}' is not a known diagnostic code", diagnostics);
            }
        }

        private static bool TryParseDiagnosticCode(string name, out SurtrDiagnosticCode code)
        {
            if (Enum.TryParse(name, ignoreCase: true, out code))
                return true;

            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            {
                code = (SurtrDiagnosticCode)numeric;
                return true;
            }

            code = SurtrDiagnosticCode.None;
            return false;
        }

        /// <summary>
        /// Reads <c>define Name = value</c>, typing the value the way a literal would be typed (§5.8).
        /// </summary>
        /// <remarks>
        /// Deliberately the same rules the lexer applies: quoted is a string, <c>true</c>/<c>false</c>
        /// a bool, a decimal point makes a float, everything else an int. §7.4 says a build constant
        /// behaves exactly as one declared <c>const</c> at the top of every module, so it had better
        /// be typed like one.
        /// </remarks>
        private void ReadDefine(string rest, int number, SurtrDiagnosticBag diagnostics)
        {
            int equals = rest.IndexOf('=');
            string name = (equals < 0 ? rest : rest.Substring(0, equals)).Trim();

            if (!ModulePath.IsValidSegment(name))
            {
                Invalid(number, $"'{name}' is not a legal name for a build constant", diagnostics);
                return;
            }

            // A flag with no value is `true`, which is the shape a switch wants; §7.4's rule that a
            // constant always has a value is what makes that safe - there is no "defined" state to
            // confuse it with.
            string value = equals < 0 ? "true" : rest.Substring(equals + 1).Trim();

            if (_constants.ContainsKey(name))
            {
                Invalid(number, $"the build already defines '{name}'", diagnostics);
                return;
            }

            _constants.Add(name, Parse(value));
        }

        private static BuildConstant Parse(string value)
        {
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                return BuildConstant.String(value.Substring(1, value.Length - 2));

            if (string.Equals(value, "true", StringComparison.Ordinal))
                return BuildConstant.Bool(true);

            if (string.Equals(value, "false", StringComparison.Ordinal))
                return BuildConstant.Bool(false);

            if (value.IndexOf('.') >= 0 && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
                return BuildConstant.Float(real);

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer))
                return BuildConstant.Int(integer);

            return BuildConstant.String(value);
        }

        private static bool TryTake(string text, string keyword, out string rest)
        {
            if (text.Length > keyword.Length
                && text.StartsWith(keyword, StringComparison.Ordinal)
                && char.IsWhiteSpace(text[keyword.Length]))
            {
                rest = text.Substring(keyword.Length).Trim();
                return true;
            }

            rest = string.Empty;
            return false;
        }

        private static string Unquote(string value)
            => value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"'
                ? value.Substring(1, value.Length - 2)
                : value;

        private void Invalid(int line, string problem, SurtrDiagnosticBag diagnostics)
            => diagnostics.ReportError(
                SurtrDiagnosticCode.ProjectFileInvalid,
                $"{Path}({line}): {problem}.",
                Path,
                span: default);
    }
}
