#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// A Surtr string: a CLR <see cref="string"/> wearing a <see cref="SurtrClass"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Surtr does not own its own character storage. The host is C#/Unity, every string that
    /// crosses the boundary is already a CLR string, and every operation Surtr needs
    /// (concatenation, slicing, comparison, culture-invariant casing) is already implemented
    /// there in vectorised form. Wrapping is the whole design: the VM gets a
    /// <see cref="SurtrRef"/> it can trace, and the host gets its own string back with no copy
    /// and no marshalling.
    /// </para>
    /// <para>
    /// The instance is immutable, which is what lets <see cref="Hash"/> be computed once at
    /// construction. That matters because strings are the one reference type Surtr compares by
    /// value - see <see cref="SurtrValueComparer"/> - so a string used as a dictionary key would
    /// otherwise rehash its whole text on every lookup.
    /// </para>
    /// </remarks>
    public sealed class SurtrString : SurtrObject
    {
        /// <summary>The CLR string behind this object. Never null.</summary>
        internal readonly string Value;

        /// <summary>
        /// <see cref="Value"/>'s hash, computed once. Stable for the life of the process, which is
        /// all a runtime hash table needs; never persist it.
        /// </summary>
        internal readonly int Hash;

        internal SurtrString(string value) : base(SurtrBuiltIns.String)
        {
            Value = value;
            Hash = value.GetHashCode();
        }

        /// <summary>The CLR string this object wraps, for the VM and for host code reading it back.</summary>
        public string Text
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Value;
        }

        /// <summary>The string's length in UTF-16 code units, which is what <c>StrLen</c> pushes.</summary>
        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Value.Length;
        }

        /// <summary>Whether the string has no characters.</summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Value.Length == 0;
        }

        /// <summary>Reads one character, as <c>StrGet</c> does. The caller is responsible for the range check.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrChar CharAt(int index) => Value[index];

        /// <summary>Ordinal comparison against another Surtr string.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TextEquals(SurtrString other)
            // Cheap rejects first: identity, then the cached hashes, then the characters. Two
            // strings of different text almost always differ in hash, so the full ordinal
            // comparison is reached only by genuine matches and by real collisions.
            => ReferenceEquals(this, other) || (Hash == other.Hash && string.Equals(Value, other.Value, StringComparison.Ordinal));

        // A string holds no Surtr values, so there is nothing here to trace.
        internal override void VisitReferences(SurtrEntityMarker marker) { }

        /// <inheritdoc/>
        public override string ToString() => Value;
    }
}
