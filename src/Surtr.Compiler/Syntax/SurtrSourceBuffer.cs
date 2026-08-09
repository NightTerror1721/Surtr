#nullable enable

using System;
using System.IO;
using System.Text;

namespace Surtr.Compiler.Syntax
{
    /// <summary>
    /// Normalizes a Surtr source into a single in-memory character buffer, whatever it originally
    /// came from - a string already in memory, a file on disk, or an open stream. Everything past
    /// this point in the front end (<see cref="CharReader"/> and up) only ever deals with
    /// <see cref="Text"/>; it never needs to know where the source came from.
    /// </summary>
    public sealed class SurtrSourceBuffer
    {
        /// <summary>The full source text.</summary>
        public ReadOnlyMemory<char> Text { get; }

        /// <summary>Identifies the source for diagnostics - a file path, or a placeholder for in-memory sources.</summary>
        public string Name { get; }

        private SurtrSourceBuffer(ReadOnlyMemory<char> text, string name)
        {
            Text = text;
            Name = name;
        }

        /// <summary>Wraps an in-memory string as a source buffer.</summary>
        public static SurtrSourceBuffer FromString(string text, string name = "<memory>")
        {
            return new SurtrSourceBuffer(text.AsMemory(), name);
        }

        /// <summary>Wraps an already-materialized character buffer as a source buffer.</summary>
        public static SurtrSourceBuffer FromMemory(ReadOnlyMemory<char> text, string name = "<memory>")
        {
            return new SurtrSourceBuffer(text, name);
        }

        /// <summary>Reads an entire file into a source buffer. <paramref name="path"/> becomes the buffer's <see cref="Name"/>.</summary>
        public static SurtrSourceBuffer FromFile(string path, Encoding? encoding = null)
        {
            string text = encoding is null ? File.ReadAllText(path) : File.ReadAllText(path, encoding);
            return new SurtrSourceBuffer(text.AsMemory(), path);
        }

        /// <summary>
        /// Reads an open stream to its end into a source buffer. The stream is left open - it was
        /// the caller's to open, so it stays the caller's to close.
        /// </summary>
        public static SurtrSourceBuffer FromStream(Stream stream, string name = "<stream>", Encoding? encoding = null)
        {
            using StreamReader reader = new StreamReader(stream, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
            string text = reader.ReadToEnd();
            return new SurtrSourceBuffer(text.AsMemory(), name);
        }
    }
}
