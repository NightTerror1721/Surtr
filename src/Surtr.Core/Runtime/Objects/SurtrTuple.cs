#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// The built-in tuple: a fixed, heterogeneous, immutable sequence of <see cref="SurtrValue"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Flat by construction - one <see cref="SurtrValue"/><c>[]</c> sized exactly to the arity and
    /// never resized. That is the whole difference from <see cref="SurtrArray"/>: no count field,
    /// no slack, no growth path, so <c>TupGet</c> is a single indexed load and <c>TupUnpack</c> is
    /// a straight block copy onto the stack. The empty tuple is legal and costs nothing, since it
    /// shares <see cref="Array.Empty{T}"/>.
    /// </para>
    /// <para>
    /// Elements are heterogeneous but still carry no per-element type tag. Each element is
    /// NaN-boxed, so it already says whether it holds an int, a float, a bool, a char or a
    /// reference - which is everything the collector needs and most of what a debugger wants. The
    /// part boxing cannot recover, the declared type behind a reference element, is instead kept
    /// once for the whole tuple in <see cref="TypeReference"/>: the descriptor <c>T(IS)</c> names
    /// element 0 as <c>int</c> and element 1 as <c>string</c> in one interned string. A parallel
    /// type array would store per element what the shape descriptor already says once, and the
    /// compiler knows the shape statically at every use site anyway.
    /// </para>
    /// <para>
    /// Immutability is what makes tuples usable as dictionary keys: <see cref="SurtrValueComparer"/>
    /// compares them structurally, which is only sound because nothing can change an element after
    /// the tuple is packed.
    /// </para>
    /// </remarks>
    public sealed class SurtrTuple : SurtrObject
    {
        /// <summary>The elements, in order. Exactly as long as the tuple's arity.</summary>
        internal readonly SurtrValue[] Elements;

        private readonly SurtrClassReference _typeReference;

        internal SurtrTuple(SurtrClassReference typeReference, SurtrValue[] elements) : base(SurtrBuiltIns.Tuple)
        {
            _typeReference = typeReference;
            Elements = elements;
        }

        internal SurtrTuple(SurtrClassReference typeReference, int arity) : base(SurtrBuiltIns.Tuple)
        {
            _typeReference = typeReference;
            Elements = arity > 0 ? new SurtrValue[arity] : Array.Empty<SurtrValue>();
        }

        /// <summary>The tuple's arity. What <c>TupLen</c> pushes.</summary>
        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Elements.Length;
        }

        /// <summary>Whether this is the empty tuple.</summary>
        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Elements.Length == 0;
        }

        /// <summary>
        /// This tuple's shape descriptor - <c>T(IS)</c> for <c>(int, string)</c>. Slice it with
        /// <see cref="SurtrClassReference.GetTupleElementTypes"/> to recover the declared type of
        /// each element.
        /// </summary>
        public SurtrClassReference TypeReference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _typeReference;
        }

        /// <summary>Reads an element. The caller is responsible for the range check.</summary>
        public SurtrValue this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Elements[index];
        }

        /// <summary>Whether <paramref name="index"/> addresses an element of this tuple.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsInRange(int index) => (uint)index < (uint)Elements.Length;

        /// <summary>
        /// Writes an element while the tuple is still being packed.
        /// </summary>
        /// <remarks>
        /// Internal and deliberately unguarded: <c>TupPack</c> allocates the tuple and then fills
        /// it from the stack, so there is a window between construction and the value becoming
        /// visible to Surtr code. Once packed, nothing in the language can write an element again -
        /// there is no <c>TupSet</c> opcode.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void SetDuringPack(int index, SurtrValue value) => Elements[index] = value;

        internal override void VisitReferences(SurtrEntityMarker marker)
        {
            var elements = Elements;
            for (int i = 0; i < elements.Length; i++)
                marker.Mark(elements[i]);
        }
    }
}
