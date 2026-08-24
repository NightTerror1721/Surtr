#nullable enable

using Surtr.Runtime.Classes;
using System;
using System.IO;

namespace Surtr.Bytecode.Image
{
    /// <summary>
    /// A compiled module as bytes: the portable form of everything
    /// <see cref="Emit.SurtrModuleBuilder.Build"/> produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes a module loadable into more than one runtime.</b> A
    /// <see cref="SurtrModule"/> cannot be: loading one patches string literals with references
    /// from the loading runtime's heap, binds its native imports to that runtime's global table,
    /// and hands its classes static storage that the collector traces through that runtime's
    /// registry. Splitting all of that out per runtime would put a test on the hot path of every
    /// static access to answer a question a second <see cref="SurtrModule"/> answers for free.
    /// </para>
    /// <para>
    /// So the shareable artefact is the image, not the object - the same bargain the JVM and the
    /// CLR strike, where a class file is shared and each loader gets its own classes.
    /// <see cref="Instantiate"/> builds a fresh module every time, and each one is loaded into one
    /// runtime:
    /// </para>
    /// <code>
    /// var image = SurtrModuleImage.FromModule(builder.Build());
    /// first.LoadModule(image);
    /// second.LoadModule(image);
    /// </code>
    /// <para>
    /// The bytes are also the on-disk form, which is what a compiler needs in order to be a
    /// compiler at all rather than something that has to hand its output straight to a runtime in
    /// the same process.
    /// </para>
    /// <para>
    /// <b>A native member travels as a name, never as an address.</b> A module written entirely by
    /// the host, and one mixing compiled Surtr bodies with host ones, are both images: each native
    /// method is written as its <see cref="SurtrNativeMethodInfo.LinkName"/>, and the runtime
    /// loading it supplies the body through <c>SurtrRuntime.DefineNativeBody</c>. A name nothing
    /// was published under fails the load, next to where an unresolved type does. That is the same
    /// arrangement a <c>native</c> import already had for host globals, applied to a member.
    /// </para>
    /// <para>
    /// <b>Two things still cannot travel.</b> The built-in module is process-wide and shared by
    /// every runtime, so a copy read back from an image would shadow the real one rather than
    /// extend it. And a module-level member of <em>another</em> module cannot be named in the
    /// field or method access table, because nothing on a module-level member records which module
    /// owns it; cross-module <em>calls</em> are unaffected, since those go through the module
    /// reference table by path.
    /// </para>
    /// </remarks>
    public sealed class SurtrModuleImage
    {
        /// <summary>
        /// The extension a compiled module is conventionally stored under: <c>.surtrc</c>.
        /// </summary>
        /// <remarks>
        /// A convention, not a requirement - nothing here touches a filesystem. Surtr's host is
        /// Unity, where reading a file is a platform question (a streaming asset on Android is
        /// inside the APK, and IL2CPP builds have their own rules), so the library deals in bytes
        /// and streams and lets the host decide where they come from.
        /// </remarks>
        public const string FileExtension = ".surtrc";

        /// <summary>Leading bytes of every image: <c>SURTRMOD</c> in ASCII.</summary>
        internal const ulong Magic = 0x444F4D5254525553;

        /// <summary>
        /// The layout version these bytes were written in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Bumped whenever the layout changes in a way an older reader would misread. It normally
        /// counts changes to how a module is <em>framed</em> rather than to what runs inside it,
        /// since the opcode set has its own rule - every value in <c>OpCode.cs</c> is written out
        /// and final, so a new instruction takes a free value and breaks nothing.
        /// </para>
        /// <para>
        /// Version 3 is the exception, and the reason the rule exists. The set was regrouped by
        /// family and renumbered once, deliberately, to fix its values before anything shipped -
        /// so every byte of every version 2 image means something different now. Refusing to load
        /// one is the whole point of the version, and there is no upgrade path: recompile.
        /// </para>
        /// <para>
        /// Version 4 retires the native global mechanism: images no longer carry lists of
        /// host-global variable and function imports, because module-level <c>native</c> members now
        /// travel as methods and properties published by link name. The layout change is confined to
        /// this removal, but a version 3 reader would still misparse a version 4 image, so it is
        /// refused like every other older format.
        /// </para>
        /// <para>
        /// Version 5 adds a per-generic-parameter constraint list to the <c>Class</c> and
        /// <c>Interface</c> sections, written right after each type's <c>genericParameters</c>.
        /// Each constraint travels as the descriptor string it already is, so a bound naming the
        /// type's own parameter (<c>G&lt;n&gt;</c>) means the same thing after the round trip.
        /// Nothing on an execution path reads the new table - it exists for the compiler's
        /// metadata importer, for tooling and for host interop - but a version 4 reader would
        /// misparse the extra counts, so it is refused like every other older format.
        /// </para>
        /// <para>
        /// Version 6 extends the same idea to methods: every <c>Method</c> entry now carries its
        /// own generic parameter names and per-parameter constraint lists, and the method-level
        /// parameter descriptor <c>H&lt;n&gt;</c> exists so a signature can say which parameter it
        /// means without knowing the declaring method. Both stay off every execution path; a
        /// version 5 reader would read the extra counts as the bytecode fields, so it is refused
        /// like every other older format.
        /// </para>
        /// <para>
        /// Version 7 exists in the record as the release the opcode set's free values resumed
        /// from; it changed no layout.
        /// </para>
        /// <para>
        /// Version 8 adds one boolean per class - <c>IsValueType</c>, written after the enum
        /// flag - which tells the linker to lay the class out as a flattened value block rather
        /// than one slot per field. A version 7 reader would read the flag byte as the first byte
        /// of the base-type index, so it is refused like every other older format.
        /// </para>
        /// <para>
        /// Version 9 changes no layout at all, and is a bump precisely because a byte already in
        /// the layout changed <em>meaning</em>: <c>SurtrValueTypeCode</c> gained
        /// <c>Generator</c> inside the built-in run, so every code from <c>Range</c> up shifted by
        /// one. A version 8 reader would read a class's family byte and get the wrong family
        /// silently, which is exactly the failure a refused version exists to prevent. Nothing was
        /// added for generators themselves: a generator's element travels in the descriptor
        /// (<c>YI</c>), and a call to a generator is an ordinary call to a stub whose declared
        /// return says so, so no flag was needed.
        /// </para>
        /// </remarks>
        internal const ushort FormatVersion = 9;

        private readonly byte[] _bytes;
        private readonly string _path;

        private SurtrModuleImage(byte[] bytes, string path)
        {
            _bytes = bytes;
            _path = path;
        }

        /// <summary>The dot-separated path of the module these bytes describe.</summary>
        public string Path => _path;

        /// <summary>How many bytes the image occupies.</summary>
        public int Length => _bytes.Length;

        /// <summary>Serializes a built module.</summary>
        /// <param name="module">The module to write. Must have been built.</param>
        /// <exception cref="ArgumentException">The module is not built, or carries something an image cannot hold.</exception>
        public static SurtrModuleImage FromModule(SurtrModule module)
        {
            if (module is null)
                throw new ArgumentNullException(nameof(module));

            byte[] bytes = SurtrModuleImageWriter.Write(module);
            return new SurtrModuleImage(bytes, module.Path);
        }

        /// <summary>Adopts an image that was written earlier.</summary>
        /// <param name="bytes">The bytes, as produced by <see cref="ToBytes"/>.</param>
        /// <exception cref="SurtrImageFormatException">The bytes are not a Surtr image, or are of a version this build cannot read.</exception>
        public static SurtrModuleImage FromBytes(byte[] bytes)
        {
            if (bytes is null)
                throw new ArgumentNullException(nameof(bytes));

            // Read the header now rather than at the first Instantiate, so a corrupt or
            // wrong-version image is a failure at the point it was handed over.
            string path = SurtrModuleImageReader.ReadPath(bytes);

            var copy = new byte[bytes.Length];
            Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);

            return new SurtrModuleImage(copy, path);
        }

        /// <summary>Reads an image from a stream.</summary>
        public static SurtrModuleImage FromStream(Stream stream)
        {
            if (stream is null)
                throw new ArgumentNullException(nameof(stream));

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return FromBytes(buffer.ToArray());
        }

        /// <summary>A copy of the image's bytes, ready to be written to disk or sent over a wire.</summary>
        public byte[] ToBytes()
        {
            var copy = new byte[_bytes.Length];
            Buffer.BlockCopy(_bytes, 0, copy, 0, _bytes.Length);
            return copy;
        }

        /// <summary>Writes the image's bytes to a stream.</summary>
        public void WriteTo(Stream stream)
        {
            if (stream is null)
                throw new ArgumentNullException(nameof(stream));

            stream.Write(_bytes, 0, _bytes.Length);
        }

        /// <summary>
        /// Builds a fresh <see cref="SurtrModule"/> from the image, ready to be loaded into one
        /// runtime.
        /// </summary>
        /// <remarks>
        /// Call it once per runtime. The module it returns owns unmanaged buffers and per-runtime
        /// state from the moment it is loaded, which is exactly why two runtimes cannot share one.
        /// </remarks>
        /// <exception cref="SurtrImageFormatException">The bytes do not describe a module this build can rebuild.</exception>
        public SurtrModule Instantiate() => SurtrModuleImageReader.Read(_bytes);
    }

    /// <summary>Thrown when an image cannot be read: a bad header, an unknown version, or truncated bytes.</summary>
    public sealed class SurtrImageFormatException : Exception
    {
        /// <summary>Initializes the exception with what was wrong with the image.</summary>
        /// <param name="message">A description of the problem.</param>
        public SurtrImageFormatException(string message) : base(message)
        {
        }
    }
}
