#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// The built-in dictionary: a hash map from <see cref="SurtrValue"/> to
    /// <see cref="SurtrValue"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built on the BCL's <see cref="Dictionary{TKey, TValue}"/> rather than a hand-rolled open
    /// addressing table. The BCL implementation is already the tuned one - single-array buckets,
    /// no per-entry allocation, resize amortised - and re-implementing it would only be worth it
    /// to store entries unmanaged, which is off the table anyway: a dictionary is a collectable
    /// value and the registry sweeps without a finalization hook, so its storage has to be managed
    /// (the same reasoning as <see cref="SurtrArray"/>).
    /// </para>
    /// <para>
    /// Keys go through <see cref="SurtrValueComparer"/>, not raw bits, which is the whole reason
    /// the dictionary is constructed with the runtime's comparer instead of standing alone.
    /// <c>dict["hello"]</c> has to find the entry stored under a different string object holding
    /// the same text; comparing handles would make every string key a fresh key and the dictionary
    /// useless.
    /// </para>
    /// <para>
    /// No per-entry type tags, for the same reason as <see cref="SurtrArray"/>: the compiler knows
    /// the declared key and value types at every use site, and each stored value is NaN-boxed and
    /// so self-describing to the collector. The declared pair is kept once for the whole
    /// dictionary in <see cref="TypeReference"/> - <c>DIS</c> for <c>{int: string}</c>.
    /// </para>
    /// </remarks>
    public sealed class SurtrDictionary : SurtrObject
    {
        /// <summary>The entries, keyed under the owning runtime's value semantics.</summary>
        internal readonly Dictionary<SurtrValue, SurtrValue> Entries;

        private readonly SurtrClassReference _typeReference;

        internal SurtrDictionary(SurtrClassReference typeReference, SurtrValueComparer comparer, int capacity)
            : base(SurtrBuiltIns.Dictionary)
        {
            _typeReference = typeReference;
            Entries = new Dictionary<SurtrValue, SurtrValue>(capacity, comparer);
        }

        /// <summary>How many entries the dictionary holds. What <c>DictLen</c> pushes.</summary>
        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Entries.Count;
        }

        /// <summary>Whether the dictionary holds no entries.</summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Entries.Count == 0;
        }

        /// <summary>
        /// This dictionary's full parameterised type descriptor - <c>DIS</c> for
        /// <c>{int: string}</c>. Slice it with
        /// <see cref="SurtrClassReference.GetDictionaryKeyType"/> and
        /// <see cref="SurtrClassReference.GetDictionaryValueType"/>.
        /// </summary>
        public SurtrClassReference TypeReference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _typeReference;
        }

        /// <summary>The declared key type, sliced out of <see cref="TypeReference"/>.</summary>
        public SurtrClassReference KeyType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _typeReference.IsValid ? _typeReference.GetDictionaryKeyType() : default;
        }

        /// <summary>The declared value type, sliced out of <see cref="TypeReference"/>.</summary>
        public SurtrClassReference ValueType
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _typeReference.IsValid ? _typeReference.GetDictionaryValueType() : default;
        }

        /// <summary>Reads the value stored under <paramref name="key"/>. What <c>DictGet</c> resolves to.</summary>
        /// <returns><see langword="false"/> if the key is absent, leaving <paramref name="value"/> null.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(SurtrValue key, out SurtrValue value) => Entries.TryGetValue(key, out value);

        /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>, inserting or replacing. What <c>DictSet</c> resolves to.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(SurtrValue key, SurtrValue value) => Entries[key] = value;

        /// <summary>Whether <paramref name="key"/> is present. What <c>DictIn</c> resolves to.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsKey(SurtrValue key) => Entries.ContainsKey(key);

        /// <summary>Drops the entry under <paramref name="key"/>.</summary>
        /// <returns><see langword="true"/> if an entry was removed.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(SurtrValue key) => Entries.Remove(key);

        /// <summary>Drops every entry.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear() => Entries.Clear();

        /// <summary>Copies the keys into <paramref name="destination"/>, in the dictionary's own order.</summary>
        public void CopyKeysTo(SurtrArray destination)
        {
            destination.EnsureCapacity(destination.Length + Entries.Count);
            foreach (var key in Entries.Keys)
                destination.Add(key);
        }

        /// <summary>Copies the values into <paramref name="destination"/>, in the dictionary's own order.</summary>
        public void CopyValuesTo(SurtrArray destination)
        {
            destination.EnsureCapacity(destination.Length + Entries.Count);
            foreach (var value in Entries.Values)
                destination.Add(value);
        }

        internal override void VisitReferences(SurtrEntityMarker marker)
        {
            // Both halves are roots: a key can be a string or a tuple just as easily as a value
            // can be an object, and dropping either would collect something still reachable.
            foreach (var entry in Entries)
            {
                marker.Mark(entry.Key);
                marker.Mark(entry.Value);
            }
        }
    }
}
