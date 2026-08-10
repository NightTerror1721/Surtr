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
        /// <see cref="Value"/>'s hash, computed once by <see cref="ComputeHash"/> and therefore the
        /// same in every process, on every platform, forever.
        /// </summary>
        internal readonly int Hash;

        internal SurtrString(string value) : base(SurtrBuiltIns.String)
        {
            Value = value;
            Hash = ComputeHash(value);
        }

        /// <summary>
        /// The hash Surtr gives a piece of text: FNV-1a over its UTF-16 code units.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Deliberately not <see cref="string.GetHashCode()"/>.</b> On .NET Core that is seeded
        /// per process, so the same text hashes differently on every run - fine for a hash table
        /// that lives and dies inside one process, and fatal for the one other thing a string hash
        /// is for here. Lowering a <c>switch</c> over strings means the compiler hashes the case
        /// labels and the running program hashes the subject, and those are two different
        /// processes: a per-run seed would make every compiled switch take the wrong arm the moment
        /// it was loaded anywhere but the process that built it.
        /// </para>
        /// <para>
        /// What that costs is the hash-flooding resistance randomisation buys, and the reason it is
        /// affordable is the threat model: an embedded scripting language runs the host's own
        /// scripts, not text an attacker chose. What it buys is that compiled bytecode means the
        /// same thing everywhere, which is the whole point of compiling it.
        /// </para>
        /// <para>
        /// FNV-1a specifically because it is four lines, has no tables, distributes short ASCII
        /// keys well - which is what identifiers and enum-ish string constants are - and is
        /// trivially reproducible by any other implementation of Surtr.
        /// </para>
        /// </remarks>
        /// <param name="text">The text to hash.</param>
        /// <returns>A hash that depends only on <paramref name="text"/>.</returns>
        public static int ComputeHash(string text)
        {
            const uint OffsetBasis = 2166136261;
            const uint Prime = 16777619;

            uint hash = OffsetBasis;
            for (int i = 0; i < text.Length; i++)
            {
                // Each code unit is folded a byte at a time, so the result does not depend on the
                // machine's endianness.
                char unit = text[i];
                hash = (hash ^ (byte)unit) * Prime;
                hash = (hash ^ (byte)(unit >> 8)) * Prime;
            }

            return (int)hash;
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
