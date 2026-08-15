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
    /// The instance is immutable - <see cref="Value"/> never changes - but the hash in
    /// <see cref="Hash"/> is computed lazily, once, on first need, and then cached. Strings are
    /// the one reference type Surtr compares by value - see <see cref="SurtrValueComparer"/> - so
    /// a string used as a dictionary key hashes its whole text at most once, while a string that
    /// never becomes a key (a concatenation result, an error message, a formatted value) never
    /// pays for a hash it will never use. That matters: concatenation, casing, trimming, joining
    /// and interpolation all build strings, and hashing each one eagerly would add a second walk
    /// over the text to every one of them.
    /// </para>
    /// </remarks>
    public sealed class SurtrString : SurtrObject
    {
        /// <summary>The CLR string behind this object. Never null.</summary>
        internal readonly string Value;

        /// <summary>
        /// <see cref="Value"/>'s hash: <see cref="ComputeHash"/>'s, therefore the same in every
        /// process, on every platform, forever. Computed on first need and cached.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The cache is a plain field, not a lock. Built-in classes are process-wide, so two
        /// runtimes can demand the hash of the same string at once; the computation is a pure
        /// function of the text, both threads write the same value, and an <see cref="int"/> store
        /// is atomic - the race is benign.
        /// </para>
        /// <para>
        /// 0 is the "not computed" sentinel and also a legal FNV-1a output, so a text whose true
        /// hash is 0 is recomputed on every access: correct, and rarer than one text in 2^32.
        /// </para>
        /// </remarks>
        private int _hash;

        /// <summary>The string's cached hash, computed on first access.</summary>
        internal int Hash
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _hash != 0 ? _hash : (_hash = ComputeHash(Value));
        }

        internal SurtrString(string value) : base(SurtrBuiltIns.String)
        {
            Value = value;
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
        {
            // Cheap rejects first: identity, then the cached hashes, then the characters. The
            // hash is a fast reject only when both sides already have one - forcing the lazy
            // hash here would pay a scalar FNV walk to skip a vectorised ordinal compare, which
            // is backwards. Two strings of different text almost always differ in hash, so the
            // full ordinal comparison is reached only by genuine matches, real collisions, and
            // strings that were never hashed.
            if (ReferenceEquals(this, other))
                return true;
            if (_hash != 0 && other._hash != 0 && _hash != other._hash)
                return false;
            return string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        // A string holds no Surtr values, so there is nothing here to trace.
        internal override void VisitReferences(SurtrEntityMarker marker) { }

        /// <inheritdoc/>
        public override string ToString() => Value;
    }
}
