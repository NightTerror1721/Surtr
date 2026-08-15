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
    /// <b>The storage is specialised by declared key type.</b> Keys normally go through
    /// <see cref="SurtrValueComparer"/>, not raw bits - <c>dict["hello"]</c> has to find the entry
    /// stored under a different string object holding the same text, and comparing handles would
    /// make every string key a fresh key. But a custom comparer is only reachable from a
    /// <see cref="Dictionary{TKey, TValue}"/> through <see cref="IEqualityComparer{T}"/>, so every
    /// lookup pays an interface call the JIT cannot devirtualise. A <c>{int: V}</c> dictionary
    /// needs none of that: Surtr is statically typed, so the compiler has already proved every key
    /// is an <c>int</c>, and an int compares by its bits. Those store in
    /// <see cref="IntEntries"/> - a <c>Dictionary&lt;int, SurtrValue&gt;</c> keyed on the extracted
    /// 32-bit payload, under the BCL's own default comparer, which the JIT does devirtualise.
    /// Every other key type keeps <see cref="Entries"/> and the runtime's comparer.
    /// </para>
    /// <para>
    /// Exactly one of the two is non-null at any moment, and readers on a hot path are expected to
    /// test <see cref="IntEntries"/> and write both arms out rather than call in here. The
    /// specialisation is not a promise the type can keep on its own: the compiler guarantees the
    /// declared key type, but a host calling <see cref="Set"/> directly is not bound by Surtr's
    /// type system. A key that arrives boxed is unwrapped, since boxing is a representation choice
    /// and a boxed <c>5</c> is the same key as an unboxed one; anything else de-specialises the
    /// dictionary back onto <see cref="Entries"/> rather than silently changing its semantics.
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
        /// <summary>
        /// The entries, keyed under the owning runtime's value semantics. Null while
        /// <see cref="IntEntries"/> holds them instead.
        /// </summary>
        internal Dictionary<SurtrValue, SurtrValue>? Entries;

        /// <summary>
        /// The entries of an <c>int</c>-keyed dictionary, keyed on the raw 32-bit payload under the
        /// BCL's default comparer. Null unless this dictionary was declared <c>{int: V}</c> and has
        /// not been handed a key that store cannot hold.
        /// </summary>
        internal Dictionary<SurtrInt, SurtrValue>? IntEntries;

        private readonly SurtrClassReference _typeReference;
        private readonly SurtrValueComparer _comparer;

        internal SurtrDictionary(SurtrClassReference typeReference, SurtrValueComparer comparer, int capacity)
            : base(SurtrBuiltIns.Dictionary)
        {
            _typeReference = typeReference;
            _comparer = comparer;

            // NestedTypeCode reads the key's symbol straight off the descriptor rather than slicing
            // a reference out of it, which also settles `{int?: V}` correctly: a nullable key reads
            // as '?' and stays on the general path, where the absent tag is an ordinary key.
            if (typeReference.NestedTypeCode == SurtrValueTypeCode.Integer)
                IntEntries = new Dictionary<SurtrInt, SurtrValue>(capacity);
            else
                Entries = new Dictionary<SurtrValue, SurtrValue>(capacity, comparer);
        }

        /// <summary>How many entries the dictionary holds. What <c>DictLen</c> pushes.</summary>
        public int Count
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                var ints = IntEntries;
                return ints != null ? ints.Count : Entries!.Count;
            }
        }

        /// <summary>Whether the dictionary holds no entries.</summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Count == 0;
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

        /// <summary>
        /// Whether this dictionary is currently on the <c>int</c>-specialised store. Diagnostic
        /// only - nothing about the dictionary's behaviour depends on the answer.
        /// </summary>
        public bool IsIntSpecialized
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => IntEntries != null;
        }

        /// <summary>Reads the value stored under <paramref name="key"/>. What <c>DictGet</c> resolves to.</summary>
        /// <returns><see langword="false"/> if the key is absent, leaving <paramref name="value"/> null.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(SurtrValue key, out SurtrValue value)
        {
            var ints = IntEntries;
            if (ints != null && key.IsInt)
                return ints.TryGetValue(key.AsInt, out value);

            return TryGetGeneral(key, out value);
        }

        /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>, inserting or replacing. What <c>DictSet</c> resolves to.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(SurtrValue key, SurtrValue value)
        {
            var ints = IntEntries;
            if (ints != null && key.IsInt)
                ints[key.AsInt] = value;
            else
                SetGeneral(key, value);
        }

        /// <summary>Whether <paramref name="key"/> is present. What <c>DictIn</c> resolves to.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ContainsKey(SurtrValue key)
        {
            var ints = IntEntries;
            return ints != null && key.IsInt
                ? ints.ContainsKey(key.AsInt)
                : ContainsKeyGeneral(key);
        }

        /// <summary>Drops the entry under <paramref name="key"/>.</summary>
        /// <returns><see langword="true"/> if an entry was removed.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Remove(SurtrValue key)
        {
            var ints = IntEntries;
            return ints != null && key.IsInt
                ? ints.Remove(key.AsInt)
                : RemoveGeneral(key);
        }

        /// <summary>Drops every entry.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            var ints = IntEntries;
            if (ints != null)
                ints.Clear();
            else
                Entries!.Clear();
        }

        #region The general path
        // Everything below is what the specialised store cannot answer by itself: the dictionary is
        // on the general store, or it is specialised and the key did not arrive as a raw int. Each
        // is NoInlining so the fast arm above stays small enough for its callers to inline, and so
        // the interpreter's hand-written copies of that arm pay one predicted branch and no more.

        /// <summary>The <see cref="TryGet"/> arm the <c>int</c> store cannot answer directly.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool TryGetGeneral(SurtrValue key, out SurtrValue value)
        {
            var entries = Entries;
            if (entries != null)
                return entries.TryGetValue(key, out value);

            // Specialised, and the key is not a raw int. A boxed int is still the same key, so it
            // is unwrapped; anything else could never have been inserted without de-specialising
            // first, so it cannot be present and the miss needs no store to prove it.
            if (_comparer.TryUnwrapBoxedInt(key, out SurtrInt unwrapped))
                return IntEntries!.TryGetValue(unwrapped, out value);

            value = default;
            return false;
        }

        /// <summary>The <see cref="Set"/> arm the <c>int</c> store cannot answer directly.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetGeneral(SurtrValue key, SurtrValue value)
        {
            var entries = Entries;
            if (entries != null)
            {
                entries[key] = value;
                return;
            }

            if (_comparer.TryUnwrapBoxedInt(key, out SurtrInt unwrapped))
            {
                IntEntries![unwrapped] = value;
                return;
            }

            Deoptimize()[key] = value;
        }

        /// <summary>The <see cref="ContainsKey"/> arm the <c>int</c> store cannot answer directly.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool ContainsKeyGeneral(SurtrValue key)
        {
            var entries = Entries;
            if (entries != null)
                return entries.ContainsKey(key);

            return _comparer.TryUnwrapBoxedInt(key, out SurtrInt unwrapped)
                && IntEntries!.ContainsKey(unwrapped);
        }

        /// <summary>The <see cref="Remove"/> arm the <c>int</c> store cannot answer directly.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool RemoveGeneral(SurtrValue key)
        {
            var entries = Entries;
            if (entries != null)
                return entries.Remove(key);

            return _comparer.TryUnwrapBoxedInt(key, out SurtrInt unwrapped)
                && IntEntries!.Remove(unwrapped);
        }

        /// <summary>
        /// Moves an <c>int</c>-specialised dictionary back onto the general store, because a key
        /// arrived that the specialised one cannot represent.
        /// </summary>
        /// <remarks>
        /// Only reachable from a host calling the native dictionary API with a key of a type the
        /// declaration rules out - compiled Surtr code cannot get here, since the key type is
        /// checked at every use site. Paying a rehash once is what keeps the specialisation an
        /// optimisation rather than a change of semantics. Insertion order carries over, so
        /// <c>keys()</c> answers the same way either side of it.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private Dictionary<SurtrValue, SurtrValue> Deoptimize()
        {
            var ints = IntEntries!;
            var entries = new Dictionary<SurtrValue, SurtrValue>(ints.Count + 1, _comparer);

            foreach (var entry in ints)
                entries[SurtrValue.CreateInt(entry.Key)] = entry.Value;

            Entries = entries;
            IntEntries = null;
            return entries;
        }
        #endregion

        /// <summary>Copies the keys into <paramref name="destination"/>, in the dictionary's own order.</summary>
        public void CopyKeysTo(SurtrArray destination)
        {
            var ints = IntEntries;
            if (ints != null)
            {
                destination.EnsureCapacity(destination.Length + ints.Count);
                foreach (SurtrInt key in ints.Keys)
                    destination.Add(SurtrValue.CreateInt(key));

                return;
            }

            var entries = Entries!;
            destination.EnsureCapacity(destination.Length + entries.Count);
            foreach (var key in entries.Keys)
                destination.Add(key);
        }

        /// <summary>Copies the values into <paramref name="destination"/>, in the dictionary's own order.</summary>
        public void CopyValuesTo(SurtrArray destination)
        {
            var ints = IntEntries;
            if (ints != null)
            {
                destination.EnsureCapacity(destination.Length + ints.Count);
                foreach (var value in ints.Values)
                    destination.Add(value);

                return;
            }

            var entries = Entries!;
            destination.EnsureCapacity(destination.Length + entries.Count);
            foreach (var value in entries.Values)
                destination.Add(value);
        }

        /// <summary>
        /// The keys as they stand now, in the dictionary's own order. What an iterator walks - see
        /// <see cref="SurtrIterator"/> on why a dictionary is iterated over a snapshot.
        /// </summary>
        public SurtrValue[] SnapshotKeys()
        {
            var ints = IntEntries;
            int next = 0;

            if (ints != null)
            {
                var snapshot = new SurtrValue[ints.Count];
                foreach (SurtrInt key in ints.Keys)
                    snapshot[next++] = SurtrValue.CreateInt(key);

                return snapshot;
            }

            var entries = Entries!;
            var keys = new SurtrValue[entries.Count];
            foreach (var key in entries.Keys)
                keys[next++] = key;

            return keys;
        }

        internal override void VisitReferences(SurtrEntityMarker marker)
        {
            // On the specialised store the keys are ints, so nothing there can be a reference and
            // only the values are worth walking.
            var ints = IntEntries;
            if (ints != null)
            {
                foreach (var value in ints.Values)
                    marker.Mark(value);

                return;
            }

            // Both halves are roots: a key can be a string or a tuple just as easily as a value
            // can be an object, and dropping either would collect something still reachable.
            foreach (var entry in Entries!)
            {
                marker.Mark(entry.Key);
                marker.Mark(entry.Value);
            }
        }
    }
}
