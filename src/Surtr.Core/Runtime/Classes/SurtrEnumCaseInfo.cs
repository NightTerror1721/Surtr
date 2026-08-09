#nullable enable

using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Classes
{
    /// <summary>
    /// One case of an enum: its name, its position in the declaration, and the static field
    /// holding the instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An enum is a sealed class with a fixed set of named static instances
    /// (<c>Language-Syntax.md</c> §2.4), so a case is not a separate kind of member - the storage
    /// is an ordinary <see cref="SurtrFieldInfo"/> and the instance is an ordinary object. What
    /// this adds is the ordinal, and the ordinal exists for one reason: an exhaustive
    /// <c>switch</c> over an enum (§4.3) has to compile to a dense jump table, and the cases are
    /// references, which a table cannot index on.
    /// </para>
    /// <para>
    /// A struct rather than a class: it is three fields with no identity, always read out of the
    /// array on <see cref="SurtrClass.EnumCases"/> and never held on its own.
    /// </para>
    /// </remarks>
    public readonly struct SurtrEnumCaseInfo
    {
        private readonly string _name;
        private readonly int _ordinal;
        private readonly SurtrFieldInfo _field;

        /// <summary>Creates case metadata. The ordinal comes from <see cref="SurtrClass.AddEnumCase"/>.</summary>
        internal SurtrEnumCaseInfo(string name, int ordinal, SurtrFieldInfo field)
        {
            _name = name;
            _ordinal = ordinal;
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

        /// <summary>The static field holding this case's instance.</summary>
        public SurtrFieldInfo Field
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _field;
        }
    }
}
