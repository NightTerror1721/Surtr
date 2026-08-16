#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Runtime.Utilities;
using System;
using System.Collections.Generic;

using SurtrRawValue = System.UInt64;

namespace Surtr.Tests.VM
{
    /// <summary>
    /// A minimal hand-rolled assembler for building a single bytecode method, for tests to exercise
    /// <c>SurtrVirtualMachine</c> directly. There is no compiler yet (see <c>docs/VM-Plan.md</c>), so
    /// every VM test has to build a chunk by hand; this is the shared plumbing for doing that instead
    /// of every test file reinventing it.
    /// </summary>
    internal sealed class BytecodeBuilder
    {
        private readonly List<byte> _code = new();
        private readonly List<SurtrRawValue> _constants = new();
        private readonly Dictionary<int, int> _labelPositions = new();
        private readonly List<Patch> _patches = new();
        private int _nextLabel;

        private readonly List<SurtrTypeHandle> _typeTable = new();
        private readonly List<SurtrFieldInfo> _fieldTable = new();
        private readonly List<SurtrMethodInfo> _methodTable = new();
        private readonly List<SurtrModule> _moduleTable = new();

        private readonly struct Patch
        {
            public readonly int WritePosition;
            public readonly int RelativeToPosition;
            public readonly int Width;
            public readonly int Label;

            public Patch(int writePosition, int relativeToPosition, int width, int label)
            {
                WritePosition = writePosition;
                RelativeToPosition = relativeToPosition;
                Width = width;
                Label = label;
            }
        }

        /// <summary>The next byte offset that will be written, for measuring jump distances by hand.</summary>
        public int Position => _code.Count;

        #region Labels

        /// <summary>Allocates a label, to be fixed to a position with <see cref="MarkLabel"/>.</summary>
        public int NewLabel() => _nextLabel++;

        /// <summary>Fixes <paramref name="label"/> to the current code position.</summary>
        public BytecodeBuilder MarkLabel(int label)
        {
            _labelPositions[label] = _code.Count;
            return this;
        }

        #endregion

        #region Raw emission

        public BytecodeBuilder Op(OpCode op)
        {
            _code.Add((byte)op);
            return this;
        }

        public BytecodeBuilder U8(int value)
        {
            _code.Add((byte)value);
            return this;
        }

        public BytecodeBuilder I16(int value)
        {
            _code.Add((byte)value);
            _code.Add((byte)(value >> 8));
            return this;
        }

        public BytecodeBuilder I32(int value)
        {
            _code.Add((byte)value);
            _code.Add((byte)(value >> 8));
            _code.Add((byte)(value >> 16));
            _code.Add((byte)(value >> 24));
            return this;
        }

        #endregion

        #region Jumps

        /// <summary>
        /// Emits a jump opcode with a placeholder 2-byte offset, measured (like every jump opcode
        /// except <c>Switch</c>/<c>SwitchLookup</c>) from the position right after the immediate.
        /// </summary>
        public BytecodeBuilder JumpShort(OpCode op, int label)
        {
            Op(op);
            int position = _code.Count;
            I16(0);
            _patches.Add(new Patch(position, position + 2, 2, label));
            return this;
        }

        /// <summary>The <c>X</c>-suffixed, 4-byte-offset form of <see cref="JumpShort"/>.</summary>
        public BytecodeBuilder JumpWide(OpCode op, int label)
        {
            Op(op);
            int position = _code.Count;
            I32(0);
            _patches.Add(new Patch(position, position + 4, 4, label));
            return this;
        }

        /// <summary>
        /// Emits <c>JPInstanceOf</c>: a leading 2-byte type index, then a 2-byte offset measured
        /// (like <see cref="JumpShort"/>) from the position right after it.
        /// </summary>
        public BytecodeBuilder JumpShortInstanceOf(int typeIndex, int label)
        {
            Op(OpCode.JPInstanceOf);
            I16(typeIndex);
            int position = _code.Count;
            I16(0);
            _patches.Add(new Patch(position, position + 2, 2, label));
            return this;
        }

        /// <summary>The <c>X</c>-suffixed, 4-byte form of <see cref="JumpShortInstanceOf"/>.</summary>
        public BytecodeBuilder JumpWideInstanceOf(int typeIndex, int label)
        {
            Op(OpCode.JPInstanceOfX);
            I32(typeIndex);
            int position = _code.Count;
            I32(0);
            _patches.Add(new Patch(position, position + 4, 4, label));
            return this;
        }

        /// <summary>
        /// Emits a dense <c>Switch</c>: pops an int, and jumps to <paramref name="caseLabels"/>
        /// <c>[value - low]</c> when that index is in range, or to <paramref name="defaultLabel"/>
        /// otherwise. Unlike every other jump, offsets here are measured from the opcode byte
        /// itself, because a variable-length instruction has no fixed "next address" to measure
        /// from at emit time.
        /// </summary>
        public BytecodeBuilder EmitSwitch(int low, int[] caseLabels, int defaultLabel)
        {
            Op(OpCode.Switch);
            int opcodePosition = _code.Count - 1;

            I32(low);
            I32(caseLabels.Length);

            int defaultPatchPosition = _code.Count;
            I32(0);
            _patches.Add(new Patch(defaultPatchPosition, opcodePosition, 4, defaultLabel));

            for (int i = 0; i < caseLabels.Length; i++)
            {
                int entryPosition = _code.Count;
                I32(0);
                _patches.Add(new Patch(entryPosition, opcodePosition, 4, caseLabels[i]));
            }

            return this;
        }

        /// <summary>
        /// Emits a sparse <c>SwitchLookup</c>: pops an int and binary-searches
        /// <paramref name="sortedCases"/> (which must already be sorted by key, ascending) for a
        /// match, jumping to its label or to <paramref name="defaultLabel"/> if none matches.
        /// </summary>
        public BytecodeBuilder EmitSwitchLookup(IReadOnlyList<(int Key, int Label)> sortedCases, int defaultLabel)
        {
            Op(OpCode.SwitchLookup);
            int opcodePosition = _code.Count - 1;

            I32(sortedCases.Count);

            int defaultPatchPosition = _code.Count;
            I32(0);
            _patches.Add(new Patch(defaultPatchPosition, opcodePosition, 4, defaultLabel));

            for (int i = 0; i < sortedCases.Count; i++)
            {
                I32(sortedCases[i].Key);

                int entryPosition = _code.Count;
                I32(0);
                _patches.Add(new Patch(entryPosition, opcodePosition, 4, sortedCases[i].Label));
            }

            return this;
        }

        #endregion

        #region Constant pool

        /// <summary>Adds a raw value to the constant pool and returns its index.</summary>
        public int Constant(SurtrRawValue raw)
        {
            _constants.Add(raw);
            return _constants.Count - 1;
        }

        /// <summary>Adds a constant and emits <c>LdcS</c> for it. Only valid for the first 256 entries.</summary>
        public BytecodeBuilder LoadConstant(SurtrRawValue raw)
        {
            int index = Constant(raw);
            return Op(OpCode.LdcS).U8(index);
        }

        /// <summary>Adds a float constant (untagged IEEE754 bits, as <c>SurtrValue.CreateFloat</c> stores it) and loads it.</summary>
        public BytecodeBuilder LoadFloat(double value)
            => LoadConstant(unchecked((SurtrRawValue)BitConverter.DoubleToInt64Bits(value)));

        /// <summary>
        /// Adds a constant naming <paramref name="entity"/> (or a null reference) and loads it.
        /// </summary>
        /// <remarks>
        /// The constant pool holds arbitrary raw values, not just literals a real compiler would
        /// emit - a reference to an already-registered entity is just as valid a pool entry. This
        /// is how tests get a pre-built array/tuple/dictionary/object/closure/string onto the
        /// stack without first having to prove the opcode that allocates one.
        /// </remarks>
        public BytecodeBuilder LoadReference(SurtrRuntimeEntity? entity)
            => LoadConstant(entity is null ? SurtrValue.Null.Raw : SurtrValue.CreateReference(entity.GetSurtrReference()).Raw);

        #endregion

        #region Access tables

        /// <summary>Adds an entry to the chunk's type table and returns its index.</summary>
        public int AddType(SurtrTypeHandle handle)
        {
            _typeTable.Add(handle);
            return _typeTable.Count - 1;
        }

        /// <summary>Adds an entry to the chunk's field table and returns its index.</summary>
        public int AddField(SurtrFieldInfo field)
        {
            _fieldTable.Add(field);
            return _fieldTable.Count - 1;
        }

        /// <summary>Adds an entry to the chunk's method table and returns its index.</summary>
        public int AddMethod(SurtrMethodInfo method)
        {
            _methodTable.Add(method);
            return _methodTable.Count - 1;
        }

        /// <summary>Adds an entry to the chunk's module table and returns its index.</summary>
        public int AddModule(SurtrModule module)
        {
            _moduleTable.Add(module);
            return _moduleTable.Count - 1;
        }

        #endregion

        /// <summary>
        /// Resolves every jump patch, materializes the chunk's unmanaged pools onto
        /// <paramref name="module"/>'s chunk, and returns metadata for the single method this
        /// builder assembled, starting at code offset 0.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One method per chunk: every test in this suite builds a fresh <see cref="SurtrModule"/>,
        /// so there is never a second body to make room for. Multi-method chunks (needed for
        /// cross-method call opcodes) get their own builder support when those opcodes are covered.
        /// </para>
        /// <para>
        /// <paramref name="dispatch"/>/<paramref name="role"/>/<paramref name="isOverride"/> default
        /// to a plain directly-invoked method, which is all most opcode tests need. Class-dispatch
        /// tests (<c>InvokeVirtual</c>, <c>InvokeInterface</c>) pass <c>Virtual</c> so
        /// <see cref="SurtrTypeLinker"/> places the method in a vtable slot instead of
        /// <c>DirectMethods</c>.
        /// </para>
        /// </remarks>
        public SurtrBytecodeMethodInfo Build(
            SurtrModule module,
            int localCount,
            int maxStackSize,
            SurtrMethodDispatch dispatch = SurtrMethodDispatch.Direct,
            SurtrMethodRole role = SurtrMethodRole.Normal,
            bool isOverride = false,
            SurtrParameterInfo[]? parameters = null,
            string name = "test")
        {
            foreach (var patch in _patches)
            {
                if (!_labelPositions.TryGetValue(patch.Label, out int target))
                    throw new InvalidOperationException($"Label {patch.Label} was never marked.");

                int offset = target - patch.RelativeToPosition;
                WriteLittleEndian(patch.WritePosition, offset, patch.Width);
            }

            var chunk = module.Chunk;

            chunk.Code = new SurtrNativeArray<byte>(_code.Count);
            for (int i = 0; i < _code.Count; i++)
                chunk.Code[i] = _code[i];

            chunk.Constants = new SurtrNativeArray<SurtrRawValue>(_constants.Count);
            for (int i = 0; i < _constants.Count; i++)
                chunk.Constants[i] = _constants[i];

            chunk.MethodOffsets = new SurtrNativeArray<int>(1);
            chunk.MethodOffsets[0] = 0;

            chunk.TypeTable = _typeTable.ToArray();
            chunk.FieldTable = _fieldTable.ToArray();
            chunk.MethodTable = _methodTable.ToArray();
            chunk.ModuleTable = _moduleTable.ToArray();

            var returnType = module.TypeHandles.GetOrAdd(SurtrClassReference.Void);

            return new SurtrBytecodeMethodInfo(
                name,
                dispatch,
                role,
                isOverride,
                returnType,
                parameters ?? Array.Empty<SurtrParameterInfo>(),
                isStatic: false,
                SurtrVisibility.Public,
                declaringType: null,
                chunk,
                entryIndex: 0,
                localCount,
                maxStackSize);
        }

        private void WriteLittleEndian(int position, int value, int width)
        {
            for (int i = 0; i < width; i++)
                _code[position + i] = (byte)(value >> (8 * i));
        }
    }
}
