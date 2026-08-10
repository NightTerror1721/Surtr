#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Surtr.Compiler.Diagnostics;

namespace Surtr.Compiler.Utilities
{
    /// <summary>
    /// A forward-only cursor over an in-memory buffer of <typeparamref name="T"/>, with bounded
    /// lookahead. Shared base for the lexer's character cursor and the parser's token cursor -
    /// the two only differ in what extra state they track alongside the position (line/column
    /// for characters, nothing for tokens), which is why that stays out of this class and lives
    /// on the subclasses instead.
    /// </summary>
    /// <typeparam name="T">The element type the cursor walks over.</typeparam>
    internal class Cursor<T>
    {
        private static readonly EqualityComparer<T> Comparer = EqualityComparer<T>.Default;

        private readonly ReadOnlyMemory<T> buffer;

        /// <summary>The current read position, as an index into the buffer.</summary>
        internal int Position { get; private protected set; }

        /// <summary>Total number of elements in the underlying buffer.</summary>
        internal int Length => buffer.Length;

        /// <summary>True once <see cref="Position"/> has reached the end of the buffer.</summary>
        internal bool IsAtEnd => Position >= buffer.Length;

        internal Cursor(ReadOnlyMemory<T> buffer)
        {
            this.buffer = buffer;
        }

        /// <summary>The element at the current position, or <c>default</c> past the end.</summary>
        internal T Current => Peek(0);

        /// <summary>
        /// Looks at the element <paramref name="offset"/> slots ahead of the current position
        /// without consuming anything. Returns <c>default</c> if the offset falls outside the
        /// buffer - lookahead past the end is a normal question to ask (e.g. "is there a second
        /// character after this one?"), not an error.
        /// </summary>
        internal T Peek(int offset = 0)
        {
            int index = Position + offset;
            return (uint)index < (uint)buffer.Length ? buffer.Span[index] : default!;
        }

        /// <summary>Returns the element at the current position and moves past it.</summary>
        internal virtual T Advance()
        {
            T value = Current;
            if (!IsAtEnd)
            {
                Position++;
            }
            return value;
        }

        /// <summary>
        /// Advances up to <paramref name="count"/> positions (fewer if the buffer ends first) and
        /// returns everything consumed. Goes through <see cref="Advance()"/> one step at a time
        /// rather than jumping <see cref="Position"/> directly, so an override like a line/column
        /// tracker still sees every element it consumes.
        /// </summary>
        internal ReadOnlySpan<T> Advance(int count)
        {
            int start = Position;
            for (int i = 0; i < count && !IsAtEnd; i++)
            {
                Advance();
            }

            return Slice(start, Position - start);
        }

        /// <summary>Moves the cursor back one position, if it isn't already at the start.</summary>
        internal virtual T Back()
        {
            if (Position > 0)
            {
                Position--;
            }

            return Current;
        }

        /// <summary>
        /// Moves the cursor back up to <paramref name="count"/> positions (fewer if it reaches the
        /// start first) and returns the range backed over, in forward order.
        /// </summary>
        internal ReadOnlySpan<T> Back(int count)
        {
            int end = Position;
            for (int i = 0; i < count && Position > 0; i++)
            {
                Back();
            }

            return Slice(Position, end - Position);
        }

        /// <summary>Advances one position, discarding the element. Reads as intent at call sites that don't need the value.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Skip() => Advance();

        /// <summary>Advances up to <paramref name="count"/> positions, discarding the elements.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Skip(int count) => Advance(count);

        /// <summary>Tests whether the current element equals <paramref name="expected"/>, without consuming it.</summary>
        internal bool Check(T expected) => !IsAtEnd && Comparer.Equals(Current, expected);

        /// <summary>Tests whether the current element equals <paramref name="first"/> or <paramref name="second"/>, without consuming it.</summary>
        internal bool Check(T first, T second) => !IsAtEnd && (Comparer.Equals(Current, first) || Comparer.Equals(Current, second));

        /// <summary>Tests whether the current element equals any of <paramref name="first"/>, <paramref name="second"/> or <paramref name="third"/>, without consuming it.</summary>
        internal bool Check(T first, T second, T third) => !IsAtEnd && (Comparer.Equals(Current, first) || Comparer.Equals(Current, second) || Comparer.Equals(Current, third));

        /// <summary>Tests whether the current element equals any of <paramref name="candidates"/>, without consuming it.</summary>
        internal bool Check(params T[] candidates)
        {
            if (IsAtEnd)
            {
                return false;
            }

            T current = Current;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (Comparer.Equals(current, candidates[i]))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Tests <paramref name="predicate"/> against the current element, without consuming it.</summary>
        internal bool Check(Predicate<T> predicate) => !IsAtEnd && predicate(Current);

        /// <summary>
        /// Consumes the current element if it equals <paramref name="expected"/>. The cursor only
        /// moves on a match; returns whether it did.
        /// </summary>
        internal bool Match(T expected)
        {
            if (!Check(expected))
            {
                return false;
            }

            Advance();
            return true;
        }

        /// <summary>Consumes the current element if it equals <paramref name="first"/> or <paramref name="second"/>. The cursor only moves on a match; returns whether it did.</summary>
        internal bool Match(T first, T second)
        {
            if (!Check(first, second))
            {
                return false;
            }

            Advance();
            return true;
        }

        /// <summary>Consumes the current element if it equals any of <paramref name="first"/>, <paramref name="second"/> or <paramref name="third"/>. The cursor only moves on a match; returns whether it did.</summary>
        internal bool Match(T first, T second, T third)
        {
            if (!Check(first, second, third))
            {
                return false;
            }

            Advance();
            return true;
        }

        /// <summary>Consumes the current element if it equals any of <paramref name="candidates"/>. The cursor only moves on a match; returns whether it did.</summary>
        internal bool Match(params T[] candidates)
        {
            if (!Check(candidates))
            {
                return false;
            }

            Advance();
            return true;
        }

        /// <summary>Consumes the current element if <paramref name="predicate"/> holds for it. The cursor only moves on a match; returns whether it did.</summary>
        internal bool Match(Predicate<T> predicate)
        {
            if (!Check(predicate))
            {
                return false;
            }

            Advance();
            return true;
        }

        /// <summary>
        /// Consumes the current element if it equals <paramref name="expected"/> and returns it.
        /// Unlike <see cref="Match(T)"/>, failure is not a normal outcome here - it means the
        /// grammar guaranteed something that turned out not to be there - so it throws
        /// <see cref="SurtrCursorException"/> with <paramref name="message"/> instead of returning
        /// a sentinel the caller might forget to check.
        /// </summary>
        internal T Consume(T expected, string message)
        {
            if (Check(expected))
            {
                return Advance();
            }

            throw new SurtrCursorException(message);
        }

        /// <summary>Consumes the current element if it equals <paramref name="first"/> or <paramref name="second"/> and returns it, else throws <see cref="SurtrCursorException"/> with <paramref name="message"/>.</summary>
        internal T Consume(T first, T second, string message)
        {
            if (Check(first, second))
            {
                return Advance();
            }

            throw new SurtrCursorException(message);
        }

        /// <summary>Consumes the current element if it equals any of <paramref name="first"/>, <paramref name="second"/> or <paramref name="third"/> and returns it, else throws <see cref="SurtrCursorException"/> with <paramref name="message"/>.</summary>
        internal T Consume(T first, T second, T third, string message)
        {
            if (Check(first, second, third))
            {
                return Advance();
            }

            throw new SurtrCursorException(message);
        }

        /// <summary>Consumes the current element if it equals any of <paramref name="candidates"/> and returns it, else throws <see cref="SurtrCursorException"/> with <paramref name="message"/>.</summary>
        internal T Consume(string message, params T[] candidates)
        {
            if (Check(candidates))
            {
                return Advance();
            }

            throw new SurtrCursorException(message);
        }

        /// <summary>
        /// Consumes the current element if <paramref name="predicate"/> holds for it and returns
        /// it, else throws <see cref="SurtrCursorException"/> with <paramref name="message"/>.
        /// The return value is what makes this overload worth having over
        /// <see cref="Match(Predicate{T})"/>: the predicate covers a class of elements, so the
        /// caller does not already know which one is about to match.
        /// </summary>
        internal T Consume(string message, Predicate<T> predicate)
        {
            if (Check(predicate))
            {
                return Advance();
            }

            throw new SurtrCursorException(message);
        }

        /// <summary>Advances while the current element equals <paramref name="value"/> and returns everything consumed.</summary>
        internal ReadOnlySpan<T> AdvanceWhile(T value) => AdvanceWhile(element => Comparer.Equals(element, value));

        /// <summary>Advances while <paramref name="predicate"/> holds for the current element and returns everything consumed.</summary>
        internal ReadOnlySpan<T> AdvanceWhile(Predicate<T> predicate)
        {
            int start = Position;
            while (!IsAtEnd && predicate(Current))
            {
                Advance();
            }

            return Slice(start, Position - start);
        }

        /// <summary>Advances until the current element equals <paramref name="terminator"/> (or the buffer ends) and returns everything consumed.</summary>
        internal ReadOnlySpan<T> AdvanceUntil(T terminator) => AdvanceUntil(element => Comparer.Equals(element, terminator));

        /// <summary>Advances until <paramref name="predicate"/> holds for the current element (or the buffer ends) and returns everything consumed.</summary>
        internal ReadOnlySpan<T> AdvanceUntil(Predicate<T> predicate)
        {
            int start = Position;
            while (!IsAtEnd && !predicate(Current))
            {
                Advance();
            }

            return Slice(start, Position - start);
        }

        /// <summary>A read-only view of <paramref name="length"/> elements starting at <paramref name="start"/>.</summary>
        internal ReadOnlySpan<T> Slice(int start, int length) => buffer.Span.Slice(start, length);
    }
}
