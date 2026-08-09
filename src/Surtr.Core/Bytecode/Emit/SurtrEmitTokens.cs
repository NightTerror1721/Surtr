#nullable enable

using System;
using System.Runtime.CompilerServices;

namespace Surtr.Bytecode.Emit
{
    /// <summary>An index into the emitted chunk's constant pool.</summary>
    /// <remarks>
    /// A wrapper rather than a bare <see cref="int"/> because every pool an opcode can index -
    /// constants, types, fields, methods, modules - is a plain integer at the byte level, and
    /// handing the wrong one to an opcode is the single easiest mistake an emitter can make. The
    /// struct costs nothing at run time and makes the mix-up a compile error.
    /// <para>
    /// The stored value is the index plus one, so a default-constructed token is recognisably
    /// invalid instead of silently naming entry 0.
    /// </para>
    /// </remarks>
    public readonly struct SurtrConstantToken : IEquatable<SurtrConstantToken>
    {
        private readonly int _value;

        internal SurtrConstantToken(int index) => _value = index + 1;

        /// <summary>The pool index this token names.</summary>
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value - 1;
        }

        /// <summary>Whether this token names an entry at all.</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value > 0;
        }

        /// <inheritdoc/>
        public bool Equals(SurtrConstantToken other) => _value == other._value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is SurtrConstantToken other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => _value;

        /// <inheritdoc/>
        public override string ToString() => IsValid ? $"const#{Index}" : "const#invalid";
    }

    /// <summary>An index into the emitted chunk's type access table.</summary>
    public readonly struct SurtrTypeToken : IEquatable<SurtrTypeToken>
    {
        private readonly int _value;

        internal SurtrTypeToken(int index) => _value = index + 1;

        /// <summary>The table index this token names.</summary>
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value - 1;
        }

        /// <summary>Whether this token names an entry at all.</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value > 0;
        }

        /// <inheritdoc/>
        public bool Equals(SurtrTypeToken other) => _value == other._value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is SurtrTypeToken other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => _value;

        /// <inheritdoc/>
        public override string ToString() => IsValid ? $"type#{Index}" : "type#invalid";
    }

    /// <summary>An index into the emitted chunk's field access table.</summary>
    public readonly struct SurtrFieldToken : IEquatable<SurtrFieldToken>
    {
        private readonly int _value;

        internal SurtrFieldToken(int index) => _value = index + 1;

        /// <summary>The table index this token names.</summary>
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value - 1;
        }

        /// <summary>Whether this token names an entry at all.</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value > 0;
        }

        /// <inheritdoc/>
        public bool Equals(SurtrFieldToken other) => _value == other._value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is SurtrFieldToken other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => _value;

        /// <inheritdoc/>
        public override string ToString() => IsValid ? $"field#{Index}" : "field#invalid";
    }

    /// <summary>An index into the emitted chunk's method access table.</summary>
    /// <remarks>
    /// Also what <c>NewClosure</c>'s <c>functionIdx</c> names: a closure target is an ordinary
    /// method table entry, not a pool of its own.
    /// </remarks>
    public readonly struct SurtrMethodToken : IEquatable<SurtrMethodToken>
    {
        private readonly int _value;

        internal SurtrMethodToken(int index) => _value = index + 1;

        /// <summary>The table index this token names.</summary>
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value - 1;
        }

        /// <summary>Whether this token names an entry at all.</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value > 0;
        }

        /// <inheritdoc/>
        public bool Equals(SurtrMethodToken other) => _value == other._value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is SurtrMethodToken other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => _value;

        /// <inheritdoc/>
        public override string ToString() => IsValid ? $"method#{Index}" : "method#invalid";
    }

    /// <summary>An index into the emitted chunk's module access table.</summary>
    public readonly struct SurtrModuleToken : IEquatable<SurtrModuleToken>
    {
        private readonly int _value;

        internal SurtrModuleToken(int index) => _value = index + 1;

        /// <summary>The table index this token names.</summary>
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value - 1;
        }

        /// <summary>Whether this token names an entry at all.</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value > 0;
        }

        /// <inheritdoc/>
        public bool Equals(SurtrModuleToken other) => _value == other._value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is SurtrModuleToken other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => _value;

        /// <inheritdoc/>
        public override string ToString() => IsValid ? $"module#{Index}" : "module#invalid";
    }

    /// <summary>
    /// The two indices a cross-module call needs: which module, then which of <em>that</em>
    /// module's method table entries.
    /// </summary>
    /// <remarks>
    /// <c>CallModule</c> resolves its target in two steps because the callee's
    /// <c>SurtrMethodInfo</c> lives in the callee's own table, not the caller's. Pairing the two
    /// indices in one token is what stops a caller from combining a module index with a function
    /// index taken from somewhere else.
    /// </remarks>
    public readonly struct SurtrExternalMethodToken : IEquatable<SurtrExternalMethodToken>
    {
        private readonly int _module;
        private readonly int _function;

        internal SurtrExternalMethodToken(int moduleIndex, int functionIndex)
        {
            _module = moduleIndex + 1;
            _function = functionIndex;
        }

        /// <summary>The caller's module table index for the target module.</summary>
        public int ModuleIndex
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _module - 1;
        }

        /// <summary>The target's index in the <em>callee</em> module's method table.</summary>
        public int FunctionIndex
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _function;
        }

        /// <summary>Whether this token names a target at all.</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _module > 0;
        }

        /// <inheritdoc/>
        public bool Equals(SurtrExternalMethodToken other) => _module == other._module && _function == other._function;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is SurtrExternalMethodToken other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => (_module * 397) ^ _function;

        /// <inheritdoc/>
        public override string ToString() => IsValid ? $"module#{ModuleIndex}:method#{_function}" : "extern#invalid";
    }

    /// <summary>A branch target inside one method's instruction stream.</summary>
    /// <remarks>
    /// A label is allocated first and fixed to a position later, so a forward jump can be emitted
    /// before its destination exists. Positions are resolved once the whole body has been emitted,
    /// which is also when branch widths are settled - so a label never carries a byte offset a
    /// caller could hold on to and have go stale.
    /// </remarks>
    public readonly struct SurtrLabel : IEquatable<SurtrLabel>
    {
        private readonly int _value;

        internal SurtrLabel(int id) => _value = id + 1;

        /// <summary>The label's identity within its emitter.</summary>
        internal int Id
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value - 1;
        }

        /// <summary>Whether this label was allocated by an emitter.</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value > 0;
        }

        /// <inheritdoc/>
        public bool Equals(SurtrLabel other) => _value == other._value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is SurtrLabel other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => _value;

        /// <inheritdoc/>
        public override string ToString() => IsValid ? $"L{Id}" : "L?";
    }

    /// <summary>A slot in the current method's frame.</summary>
    /// <remarks>
    /// Locals are numbered from the frame base, and arguments arrive as the first of them - so on
    /// an instance method local 0 is the receiver, and the declared parameters follow. Anything
    /// <c>SurtrMethodBuilder.DeclareLocal</c> hands out sits above those.
    /// </remarks>
    public readonly struct SurtrLocal : IEquatable<SurtrLocal>
    {
        private readonly int _value;

        internal SurtrLocal(int index) => _value = index + 1;

        /// <summary>The slot index within the frame.</summary>
        public int Index
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value - 1;
        }

        /// <summary>Whether this token names a slot at all.</summary>
        public bool IsValid
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value > 0;
        }

        /// <inheritdoc/>
        public bool Equals(SurtrLocal other) => _value == other._value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is SurtrLocal other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => _value;

        /// <inheritdoc/>
        public override string ToString() => IsValid ? $"local#{Index}" : "local#invalid";
    }

    /// <summary>One arm of a sparse <c>SwitchLookup</c>: the integer it matches, and where it goes.</summary>
    public readonly struct SurtrSwitchCase
    {
        private readonly int _key;
        private readonly SurtrLabel _label;

        /// <summary>Pairs a case value with its target.</summary>
        public SurtrSwitchCase(int key, SurtrLabel label)
        {
            _key = key;
            _label = label;
        }

        /// <summary>The value this arm matches.</summary>
        public int Key
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _key;
        }

        /// <summary>Where control goes when the arm matches.</summary>
        public SurtrLabel Label
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _label;
        }

        /// <inheritdoc/>
        public override string ToString() => $"case {_key} -> {_label}";
    }

    /// <summary>How wide a branch's offset immediate should be.</summary>
    public enum SurtrJumpWidth : byte
    {
        /// <summary>
        /// Let the emitter decide: start with the 2-byte form and widen only the branches that
        /// turn out not to reach, once the body's final layout is known.
        /// </summary>
        Auto = 0,

        /// <summary>Force the 2-byte form. A target further than <c>±32767</c> away is an error.</summary>
        Short = 1,

        /// <summary>Force the 4-byte <c>X</c> form, whether or not the short one would reach.</summary>
        Wide = 2,
    }

    /// <summary>
    /// Which relational test a grouped comparison or compare-and-branch helper should emit.
    /// </summary>
    /// <remarks>
    /// The instruction set spells each combination of test and operand family as its own opcode
    /// (<c>EQ</c>, <c>FEQ</c>, <c>REQ</c>, <c>StrEQ</c>, and the same again fused with a branch).
    /// This enum is the axis a compiler actually has in hand - the operator it just parsed - with
    /// the operand family supplied separately as a <c>SurtrValueTypeCode</c>.
    /// </remarks>
    public enum SurtrComparison : byte
    {
        /// <summary>Equality.</summary>
        Equal = 0,

        /// <summary>Inequality.</summary>
        NotEqual = 1,

        /// <summary>Greater than. Not defined for reference or string operands.</summary>
        Greater = 2,

        /// <summary>Greater than or equal. Not defined for reference or string operands.</summary>
        GreaterOrEqual = 3,

        /// <summary>Less than. Not defined for reference or string operands.</summary>
        Less = 4,

        /// <summary>Less than or equal. Not defined for reference or string operands.</summary>
        LessOrEqual = 5,
    }
}
