#nullable enable

using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// One case of an enum: its name, its value, its position in the declaration, and the static
    /// field holding the value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An enum is a value class with a fixed set of named static values
    /// (<c>Language-Syntax.md</c> §2.4), so a case is not a separate kind of member - the storage
    /// is an ordinary <see cref="SurtrFieldInfo"/> and the value is built by the enum's static
    /// initializer. What this adds is the value and the ordinal: the value is the case's
    /// <c>= n</c> (or implied position), the one a <c>switch</c> over the enum dispatches on;
    /// the ordinal is the declaration position, metadata for reflection and tooling now that the
    /// dense table keys on the value itself.
    /// </para>
    /// <para>
    /// A struct rather than a class: it is four fields with no identity, always read out of the
    /// array on <see cref="SurtrClass.EnumCases"/> and never held on its own.
    /// </para>
    /// </remarks>
    public readonly struct SurtrEnumCaseInfo
    {
        private readonly string _name;
        private readonly int _ordinal;
        private readonly int _value;
        private readonly SurtrFieldInfo _field;

        /// <summary>Creates case metadata. The ordinal comes from <see cref="SurtrClass.AddEnumCase"/>.</summary>
        internal SurtrEnumCaseInfo(string name, int ordinal, int value, SurtrFieldInfo field)
        {
            _name = name;
            _ordinal = ordinal;
            _value = value;
            _field = field;
        }

        /// <summary>The case's declared name.</summary>
        public string Name
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _name;
        }

        /// <summary>The case's position in the declaration, counting from zero.</summary>
        public int Ordinal
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _ordinal;
        }

        /// <summary>The case's value: its explicit <c>= n</c>, or its implied progression/bit.</summary>
        public int Value
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value;
        }

        /// <summary>The static field holding this case's value.</summary>
        public SurtrFieldInfo Field
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _field;
        }
    }
}
