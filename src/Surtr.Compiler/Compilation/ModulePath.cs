#nullable enable

using System;
using System.IO;
using System.Text;

namespace Surtr.Compiler.Compilation
{
    /// <summary>Why deriving a module path from a file location failed.</summary>
    public enum ModulePathStatus
    {
        /// <summary>A usable module path was derived.</summary>
        Ok,

        /// <summary>The file is not under the project's source root.</summary>
        OutsideSourceRoot,

        /// <summary>
        /// The file sits at the source root and the project declares no root module path, so
        /// nothing names the module it would belong to.
        /// </summary>
        Empty,

        /// <summary>A directory name that is not a legal Surtr identifier, so no import could name it.</summary>
        InvalidSegment,
    }

    /// <summary>
    /// Derives a module's path from where its file lives (§2.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no <c>module</c> header in source: directories map to path segments joined by
    /// <c>.</c>, relative to the project's source root, the way Go derives a package from its
    /// directory. Nothing has to be kept in sync with the folder, and the result already has the
    /// <c>modulePath:typeName</c> shape the descriptor grammar commits to.
    /// </para>
    /// <para>
    /// Every segment must be a legal identifier, because an <c>import</c> has to be able to name
    /// it. A directory called <c>my-module</c> is rejected here rather than producing a module no
    /// source file could reach.
    /// </para>
    /// </remarks>
    public static class ModulePath
    {
        /// <summary>Separates the segments of a module path.</summary>
        public const char Separator = '.';

        /// <summary>
        /// Whether <paramref name="segment"/> could be written as an identifier, and so can be
        /// part of a module path.
        /// </summary>
        public static bool IsValidSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment) || !(char.IsLetter(segment[0]) || segment[0] == '_'))
                return false;

            for (int i = 1; i < segment.Length; i++)
            {
                if (!char.IsLetterOrDigit(segment[i]) && segment[i] != '_')
                    return false;
            }

            return true;
        }

        /// <summary>Whether <paramref name="path"/> is a dotted sequence of legal segments.</summary>
        public static bool IsValid(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            foreach (string segment in path.Split(Separator))
            {
                if (!IsValidSegment(segment))
                    return false;
            }

            return true;
        }

        /// <summary>Joins two module paths, either of which may be empty.</summary>
        public static string Combine(string left, string right)
        {
            if (string.IsNullOrEmpty(left))
                return right;

            return string.IsNullOrEmpty(right) ? left : left + Separator + right;
        }

        /// <summary>
        /// Derives the module path a file at <paramref name="filePath"/> belongs to.
        /// </summary>
        /// <param name="sourceRoot">The project's source root.</param>
        /// <param name="filePath">The source file.</param>
        /// <param name="rootModulePath">
        /// What the source root itself is called, prefixed onto every derived path. May be empty,
        /// in which case a file directly at the root has no module to belong to.
        /// </param>
        /// <param name="modulePath">The derived path, or an empty string on failure.</param>
        /// <param name="offendingSegment">
        /// The directory name that made this fail, when the status is
        /// <see cref="ModulePathStatus.InvalidSegment"/>.
        /// </param>
        public static ModulePathStatus TryDerive(
            string sourceRoot,
            string filePath,
            string rootModulePath,
            out string modulePath,
            out string offendingSegment)
        {
            modulePath = string.Empty;
            offendingSegment = string.Empty;

            // The root keeps a trailing separator so "src" cannot prefix-match "srcExtra"; the file
            // must not have one, since what follows the root is the path being split.
            string root = NormalizeDirectory(sourceRoot);
            string file = NormalizeFile(filePath);

            // Case-insensitive because the primary target is Windows, where two spellings of one
            // path are the same directory and rejecting the mismatch would be a false negative.
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return ModulePathStatus.OutsideSourceRoot;

            string relative = file.Substring(root.Length).TrimStart('/');
            int lastSlash = relative.LastIndexOf('/');
            string directories = lastSlash < 0 ? string.Empty : relative.Substring(0, lastSlash);

            var builder = new StringBuilder(rootModulePath);

            if (directories.Length > 0)
            {
                foreach (string segment in directories.Split('/'))
                {
                    if (segment.Length == 0)
                        continue;

                    if (!IsValidSegment(segment))
                    {
                        offendingSegment = segment;
                        return ModulePathStatus.InvalidSegment;
                    }

                    if (builder.Length > 0)
                        builder.Append(Separator);

                    builder.Append(segment);
                }
            }

            if (builder.Length == 0)
                return ModulePathStatus.Empty;

            modulePath = builder.ToString();
            return ModulePathStatus.Ok;
        }

        private static string NormalizeFile(string path) => Path.GetFullPath(path).Replace('\\', '/');

        private static string NormalizeDirectory(string path)
        {
            string full = NormalizeFile(path);
            return full.EndsWith("/", StringComparison.Ordinal) ? full : full + "/";
        }
    }
}
