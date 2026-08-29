#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace Surtr.Bytecode.Image
{
    /// <summary>
    /// A compiled Surtr program as a single file: every module image the program needs, plus the
    /// entry point <c>surtr</c> starts from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SurtrModuleImage"/> is one module; a program is usually several of them, and the
    /// standard library is more still. A host running a program therefore loads a set of images and
    /// then has to be told which module-level function to call, because Surtr has no <c>main</c>
    /// (Language-Syntax.md §2.5). The package is the artefact that carries both: the images and the
    /// entry point, so <c>surtrc build</c> writes one file and <c>surtr</c> runs it without being
    /// handed a module path and a function name.
    /// </para>
    /// <para>
    /// The standard library's modules and native bodies are <em>not</em> the package's concern.
    /// Native members travel as link names and their bodies are C# function pointers that only exist
    /// in the process running the program, so the runtime that loads a package is the one that
        /// supplies them — the <c>surtr</c> runner calls <c>SurtrStdlib.LoadAll</c> before loading the
        /// package's modules. A package may still embed stdlib module images; a module already loaded is
    /// simply skipped, so embedding them is harmless and makes the file self-contained in Surtr code.
    /// </para>
    /// <para>
    /// Nothing here touches a filesystem: the bytes are the shareable form, and the host decides
    /// where they come from, the same bargain <see cref="SurtrModuleImage"/> strikes.
    /// </para>
    /// </remarks>
    public sealed class SurtrPackage
    {
        /// <summary>The extension a packaged program is conventionally stored under: <c>.surtrx</c>.</summary>
        public const string FileExtension = ".surtrx";

        /// <summary>Leading bytes of every package: <c>SURTRPKG</c> in ASCII.</summary>
        internal const ulong Magic = 0x5355525452504B47;

        /// <summary>The layout version these bytes were written in. Bumped on any container change.</summary>
        internal const ushort FormatVersion = 1;

        private readonly List<SurtrModuleImage> _modules;

        private SurtrPackage(List<SurtrModuleImage> modules, string entryModulePath, string entryFunction)
        {
            _modules = modules;
            EntryModulePath = entryModulePath;
            EntryFunction = entryFunction;
        }

        /// <summary>The dot-separated module path the entry point is declared in.</summary>
        public string EntryModulePath { get; }

        /// <summary>The module-level function <c>surtr</c> calls to start the program.</summary>
        public string EntryFunction { get; }

        /// <summary>The module images the program is made of, in the order they were written.</summary>
        public IReadOnlyList<SurtrModuleImage> Modules => _modules;

        /// <summary>Builds a package from its parts.</summary>
        /// <exception cref="ArgumentNullException">modules is null.</exception>
        /// <exception cref="ArgumentException">an entry module or function is missing.</exception>
        public static SurtrPackage Create(
            IReadOnlyList<SurtrModuleImage> modules, string entryModulePath, string entryFunction)
        {
            if (modules is null)
                throw new ArgumentNullException(nameof(modules));

            if (string.IsNullOrEmpty(entryModulePath))
                throw new ArgumentException("A package needs an entry module path.", nameof(entryModulePath));

            if (string.IsNullOrEmpty(entryFunction))
                throw new ArgumentException("A package needs an entry function name.", nameof(entryFunction));

            var copy = new List<SurtrModuleImage>(modules.Count);
            foreach (var image in modules)
                copy.Add(image);

            return new SurtrPackage(copy, entryModulePath, entryFunction);
        }

        /// <summary>Reads a package from bytes, refusing anything that is not one.</summary>
        /// <exception cref="ArgumentNullException">bytes is null.</exception>
        /// <exception cref="SurtrImageFormatException">The bytes are not a Surtr package, or a version this build cannot read.</exception>
        public static SurtrPackage FromBytes(byte[] bytes)
        {
            if (bytes is null)
                throw new ArgumentNullException(nameof(bytes));

            return SurtrPackageReader.Read(bytes);
        }

        /// <summary>Reads a package from a stream.</summary>
        public static SurtrPackage FromStream(Stream stream)
        {
            if (stream is null)
                throw new ArgumentNullException(nameof(stream));

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return FromBytes(buffer.ToArray());
        }

        /// <summary>A copy of the package's bytes, ready to be written to disk or sent over a wire.</summary>
        public byte[] ToBytes() => SurtrPackageWriter.Write(this);

        /// <summary>Writes the package's bytes to a stream.</summary>
        public void WriteTo(Stream stream)
        {
            if (stream is null)
                throw new ArgumentNullException(nameof(stream));

            byte[] bytes = ToBytes();
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
