#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>Which kind of value a <see cref="SurtrIterator"/> is walking.</summary>
    /// <remarks>
    /// A field rather than five subclasses, for the same reason <c>SurtrMethodDispatch</c> is a
    /// field: the state is identical in every case - a source and a position - and the kind only
    /// selects which two lines of <see cref="SurtrIterator.MoveNext"/> run.
    /// </remarks>
    public enum SurtrIteratorKind : byte
    {
        /// <summary>Walking a <see cref="SurtrArray"/>, yielding its elements.</summary>
        Array = 0,

        /// <summary>Walking a <see cref="SurtrString"/>, yielding its characters.</summary>
        String = 1,

        /// <summary>Walking a <see cref="SurtrTuple"/>, yielding its elements.</summary>
        Tuple = 2,

        /// <summary>Walking a <see cref="SurtrRange"/>, yielding its integers.</summary>
        Range = 3,

        /// <summary>Walking a <see cref="SurtrDictionary"/>, yielding <c>(key, value)</c> pairs.</summary>
        Dictionary = 4,
    }

    /// <summary>
    /// The cursor behind <c>IIterator&lt;T&gt;</c> for every built-in collection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One class covers every source</b>, the same way <see cref="SurtrArray"/>'s class covers
    /// every element type. All five sources are walked by a position and a bound, so five classes
    /// would differ in a <c>switch</c> arm and nothing else - and each would cost its own
    /// <see cref="SurtrClass"/>, its own interface dispatch table, and its own entry in every
    /// table the linker builds.
    /// </para>
    /// <para>
    /// <b>This is the general path, and it is not the one a loop should take.</b>
    /// <c>Language-Syntax.md</c> §4.2 requires the compiler to lower <c>for-in</c> over a range, an
    /// array, a tuple or a dict into a direct indexed loop with no iterator object at all. What
    /// this exists for is the uniformity that makes the language whole: an <c>int[]</c> can be
    /// passed to something expecting an <c>IIterable&lt;int&gt;</c>, and a user class implementing
    /// the same contract iterates identically.
    /// </para>
    /// <para>
    /// <b>A dictionary is walked over a snapshot of its keys.</b> Taking one costs an array per
    /// <c>iterate()</c>, and it buys the only defined answer to what happens when the dictionary
    /// is mutated mid-loop: a key removed after the snapshot is skipped, and one added after it is
    /// not seen. The alternative - holding a CLR enumerator - would throw from inside a native
    /// method on any mutation, which is a worse answer to give a scripting language.
    /// </para>
    /// </remarks>
    public sealed class SurtrIterator : SurtrObject
    {
        private readonly SurtrIteratorKind _kind;
        private readonly SurtrObject _source;

        /// <summary>A dictionary's keys as they stood when this iterator was made; null otherwise.</summary>
        private readonly SurtrValue[]? _keys;

        private int _index;
        private SurtrValue _current;

        internal SurtrIterator(SurtrIteratorKind kind, SurtrObject source, SurtrValue[]? keys = null)
            : base(SurtrBuiltIns.Iterator)
        {
            _kind = kind;
            _source = source;
            _keys = keys;
            _index = -1;
            _current = SurtrValue.Null;
        }

        /// <summary>Which kind of source this walks.</summary>
        public SurtrIteratorKind Kind
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _kind;
        }

        /// <summary>The collection being walked.</summary>
        public SurtrObject Source
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _source;
        }

        /// <summary>
        /// The value at the current position, or null before the first <see cref="MoveNext"/> and
        /// after the last one.
        /// </summary>
        public SurtrValue Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _current;
        }

        /// <summary>
        /// Advances to the next value, and reports whether there was one.
        /// </summary>
        /// <param name="runtime">
        /// The runtime to allocate in. Only a dictionary needs it, to pack each <c>(key, value)</c>
        /// pair, but every kind takes it so the caller has no case to distinguish.
        /// </param>
        public bool MoveNext(SurtrRuntime runtime)
        {
            switch (_kind)
            {
                case SurtrIteratorKind.Array:
                {
                    var source = (SurtrArray)_source;
                    if (++_index >= source.Length)
                        return Exhausted();

                    _current = source[_index];
                    return true;
                }

                case SurtrIteratorKind.String:
                {
                    var source = (SurtrString)_source;
                    if (++_index >= source.Length)
                        return Exhausted();

                    _current = SurtrValue.CreateChar(source.CharAt(_index));
                    return true;
                }

                case SurtrIteratorKind.Tuple:
                {
                    var source = (SurtrTuple)_source;
                    if (++_index >= source.Length)
                        return Exhausted();

                    _current = source[_index];
                    return true;
                }

                case SurtrIteratorKind.Range:
                {
                    var source = (SurtrRange)_source;
                    if (++_index >= source.Length)
                        return Exhausted();

                    // Counted off the start rather than by incrementing a stored value, so an
                    // inclusive range ending at int.MaxValue cannot overflow its own bound.
                    _current = SurtrValue.CreateInt(source.Start + _index);
                    return true;
                }

                default:
                    return MoveNextEntry(runtime);
            }
        }

        /// <summary>Rewinds to before the first value, so the source can be walked again.</summary>
        public void Reset()
        {
            _index = -1;
            _current = SurtrValue.Null;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool MoveNextEntry(SurtrRuntime runtime)
        {
            var source = (SurtrDictionary)_source;
            var keys = _keys!;

            // Skips a key removed since the snapshot rather than reporting a pair that no longer
            // exists; a loop over a dictionary being mutated is otherwise undefined.
            while (++_index < keys.Length)
            {
                SurtrValue key = keys[_index];
                if (!source.TryGet(key, out SurtrValue value))
                    continue;

                var pair = runtime.NewTuple(
                    SurtrClassReference.Tuple(source.KeyType, source.ValueType),
                    new[] { key, value });

                _current = runtime.ValueOf(pair);
                return true;
            }

            return Exhausted();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Exhausted()
        {
            _current = SurtrValue.Null;
            return false;
        }

        internal override void VisitReferences(SurtrEntityMarker marker)
        {
            // The source is reachable only through here for as long as the loop runs: the stack
            // slot the collection was read from may well have been overwritten by the call that
            // produced this iterator.
            marker.Mark(_source);
            marker.Mark(_current);

            var keys = _keys;
            if (keys is null)
                return;

            // The snapshot outlives the dictionary's own view of them: a key removed mid-loop is
            // still held here, and collecting it would leave a dangling reference in the array.
            for (int i = 0; i < keys.Length; i++)
                marker.Mark(keys[i]);
        }
    }
}
