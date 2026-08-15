#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Surtr.Compiler.Binding;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax;

namespace Surtr.LanguageServer.Workspace
{
    /// <summary>
    /// One immutable view of the workspace's compilation: the compiler objects a request resolves
    /// against, and the diagnostics that build produced. Rebuilding swaps the whole snapshot, so a
    /// request never sees half of one compilation and half of another.
    /// </summary>
    public sealed class CompilationSnapshot : IDisposable
    {
        /// <summary>The snapshot a workspace holds before its first successful build.</summary>
        public static readonly CompilationSnapshot Empty = new CompilationSnapshot(null, null, new List<SurtrDiagnostic>());

        internal CompilationSnapshot(
            Surtr.Compiler.Compilation.SurtrCompilation? compilation,
            Binder? binder,
            IReadOnlyList<SurtrDiagnostic> diagnostics)
        {
            Compilation = compilation;
            Binder = binder;
            Diagnostics = diagnostics;
        }

        /// <summary>The compiled workspace, or <see langword="null"/> when it has not been built yet.</summary>
        public Surtr.Compiler.Compilation.SurtrCompilation? Compilation { get; }

        /// <summary>The binder after all three phases, or <see langword="null"/> when not built.</summary>
        public Binder? Binder { get; }

        /// <summary>Everything the last build reported, in report order.</summary>
        public IReadOnlyList<SurtrDiagnostic> Diagnostics { get; }

        /// <summary>Whether there is anything to resolve against.</summary>
        public bool IsEmpty => Compilation is null || Binder is null;

        public void Dispose()
        {
            if (!ReferenceEquals(this, Empty))
                Compilation?.Dispose();
        }

        /// <summary>
        /// The parse tree the compiler used for a file, or <see langword="null"/> when the file is
        /// not part of this compilation (no module derived from it).
        /// </summary>
        public Surtr.Compiler.Compilation.SurtrSourceUnit? UnitFor(string filePath)
        {
            if (Compilation is null)
                return null;

            string normalized = System.IO.Path.GetFullPath(filePath);

            foreach (var module in Compilation.Modules.Values)
            {
                foreach (var unit in module.Units)
                {
                    if (string.Equals(System.IO.Path.GetFullPath(unit.File.Path), normalized, StringComparison.OrdinalIgnoreCase))
                        return unit;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The mapping between absolute offsets and the (line, column) pairs LSP works in. Lines start
    /// at 0 and a column is a UTF-16 code-unit offset within its line, which is exactly what .NET's
    /// string indexing counts — so an offset is <c>lineStart[line] + character</c> and finding which
    /// line an offset is on is a binary search.
    /// </summary>
    public readonly struct TextLines
    {
        private readonly int[] _lineStarts;

        private TextLines(int[] lineStarts) => _lineStarts = lineStarts;

        /// <summary>The number of lines the text has.</summary>
        public int Count => _lineStarts.Length;

        /// <summary>Indexes a buffer's line starts.</summary>
        public static TextLines Index(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                    starts.Add(i + 1);
            }

            return new TextLines(starts.ToArray());
        }

        /// <summary>The absolute offset of an LSP position, clamped to the buffer.</summary>
        public int OffsetAt(int line, int character)
        {
            if (_lineStarts.Length == 0)
                return 0;

            if (line < 0)
                line = 0;
            if (line >= _lineStarts.Length)
                line = _lineStarts.Length - 1;

            int offset = _lineStarts[line] + character;
            if (character < 0)
                offset = _lineStarts[line];

            return offset;
        }

        /// <summary>The LSP position of an absolute offset.</summary>
        public PositionInfo PositionAt(int offset)
        {
            if (offset < 0)
                offset = 0;

            int lo = 0;
            int hi = _lineStarts.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (_lineStarts[mid] <= offset)
                    lo = mid;
                else
                    hi = mid - 1;
            }

            return new PositionInfo(lo, offset - _lineStarts[lo]);
        }

        /// <summary>The LSP range a compiler span covers.</summary>
        public LspRange RangeOf(SourceSpan span)
        {
            var start = PositionAt(span.Start.Position);
            var end = PositionAt(span.End);
            return new LspRange(
                new LspPosition(start.Line, start.Character),
                new LspPosition(end.Line, end.Character));
        }

        /// <summary>Borrowed shape to keep the struct's own members named without a using clash.</summary>
        public readonly struct PositionInfo
        {
            public PositionInfo(int line, int character)
            {
                Line = line;
                Character = character;
            }

            public int Line { get; }

            public int Character { get; }
        }

        public readonly struct LspPosition
        {
            public LspPosition(int line, int character)
            {
                Line = line;
                Character = character;
            }

            public int Line { get; }

            public int Character { get; }
        }

        public readonly struct LspRange
        {
            public LspRange(LspPosition start, LspPosition end)
            {
                Start = start;
                End = end;
            }

            public LspPosition Start { get; }

            public LspPosition End { get; }
        }
    }
}
