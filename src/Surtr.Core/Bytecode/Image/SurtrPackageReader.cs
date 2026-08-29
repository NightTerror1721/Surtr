#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Surtr.Bytecode.Image
{
    /// <summary>Reads a <see cref="SurtrPackage"/> from bytes, refusing anything that is not one.</summary>
    /// <remarks>
    /// Mirrors <see cref="SurtrPackageWriter"/> field for field. The reader validates the magic and
    /// the container version up front, then reads each embedded module image through
    /// <see cref="SurtrModuleImage.FromBytes"/>, which validates the module's own magic and version —
    /// so a bad package fails at its boundary, not partway through a module.
    /// </remarks>
    internal static class SurtrPackageReader
    {
        public static SurtrPackage Read(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream);

            ulong magic = reader.ReadUInt64();
            if (magic != SurtrPackage.Magic)
                throw new SurtrImageFormatException("The bytes are not a Surtr package (bad magic).");

            ushort version = reader.ReadUInt16();
            if (version != SurtrPackage.FormatVersion)
                throw new SurtrImageFormatException(
                    $"Unsupported Surtr package version {version}; expected {SurtrPackage.FormatVersion}.");

            string entryModule = ReadString(reader);
            string entryFunction = ReadString(reader);

            int count = reader.ReadInt32();
            if (count < 0)
                throw new SurtrImageFormatException("A package cannot declare a negative module count.");

            var modules = new List<SurtrModuleImage>(count);
            for (int i = 0; i < count; i++)
            {
                ReadString(reader); // module path: informational only; image.Path is authoritative.
                int length = reader.ReadInt32();
                if (length < 0 || reader.BaseStream.Position + length > reader.BaseStream.Length)
                    throw new SurtrImageFormatException("A package module is truncated.");

                byte[] moduleBytes = reader.ReadBytes(length);
                modules.Add(SurtrModuleImage.FromBytes(moduleBytes));
            }

            return SurtrPackage.Create(modules, entryModule, entryFunction);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0)
                throw new SurtrImageFormatException("A package string has a negative length.");

            byte[] utf8 = reader.ReadBytes(length);
            return Encoding.UTF8.GetString(utf8);
        }
    }
}
