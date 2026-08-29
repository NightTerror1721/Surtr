#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Surtr.Bytecode.Image
{
    /// <summary>Serializes a <see cref="SurtrPackage"/> to bytes.</summary>
    /// <remarks>
    /// The layout is a small, strict, sequential pass — the same discipline the module image writer
    /// keeps, because a reader that cannot guess at a corrupt file is a reader that fails loudly:
    /// <c>SURTRPKG</c> magic, a container <c>formatVersion</c>, the entry point, then a count and a
    /// run of complete <c>.surtrc</c> images. Each string is an <c>i32</c> byte length followed by
    /// its UTF-8 bytes, so no shared table is needed for what is a short list.
    /// </remarks>
    internal static class SurtrPackageWriter
    {
        public static byte[] Write(SurtrPackage package)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            writer.Write(SurtrPackage.Magic);
            writer.Write(SurtrPackage.FormatVersion);
            WriteString(writer, package.EntryModulePath);
            WriteString(writer, package.EntryFunction);

            writer.Write(package.Modules.Count);
            foreach (var module in package.Modules)
            {
                WriteString(writer, module.Path);
                byte[] bytes = module.ToBytes();
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }

            writer.Flush();
            return stream.ToArray();
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            writer.Write(utf8.Length);
            writer.Write(utf8);
        }
    }
}
