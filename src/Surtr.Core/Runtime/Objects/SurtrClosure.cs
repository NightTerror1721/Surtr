#nullable enable

using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using System;
using System.Runtime.CompilerServices;

namespace Surtr.Runtime.Objects
{
    /// <summary>
    /// The built-in closure: a callable method plus the variables it captured when it was created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A closure wraps either a bytecode method or a native one, and <c>InvokeClosure</c> is the
    /// one call opcode with no index immediate - the target arrives on the stack, so nothing is
    /// known about it until the instruction runs. That is what shapes this type: everything the
    /// interpreter needs to make the call is copied into fields here at construction, so the call
    /// path is a switch on <see cref="ImplKind"/> followed by either an indirect call through
    /// <see cref="EntryPoint"/> or a jump to <see cref="CodeOffset"/>. No cast of
    /// <see cref="Method"/> to its subclass, no walk through metadata, no branch on anything the
    /// loader already settled.
    /// </para>
    /// <para>
    /// Captures are <em>final values</em>, not shared cells: <c>NewClosure</c> pops them off the
    /// stack and they are frozen in <see cref="UpValues"/> from then on, which is why
    /// <c>UpValueGet</c> has no matching setter. That removes the whole upvalue-closing problem a
    /// by-reference capture would create - there is no cell to keep open when a frame dies, and no
    /// way for two closures over the same variable to observe each other's writes. It also means a
    /// closure's captures can be traced as a plain value array.
    /// </para>
    /// </remarks>
    public sealed class SurtrClosure : SurtrObject
    {
        /// <summary>The method this closure calls.</summary>
        internal readonly SurtrMethodInfo Method;

        /// <summary>
        /// The captured values, in capture order: <c>UpValues[0]</c> is what <c>UpValueGet 0</c>
        /// reads. Frozen once the closure is built.
        /// </summary>
        internal readonly SurtrValue[] UpValues;

        /// <summary>Where <see cref="Method"/>'s body lives, copied off it so dispatch is a field read.</summary>
        internal readonly SurtrMethodImplKind ImplKind;

        /// <summary>How many arguments <see cref="Method"/> expects, excluding any receiver.</summary>
        internal readonly int ParameterCount;

        /// <summary>The host address to call. Only meaningful when <see cref="ImplKind"/> is <see cref="SurtrMethodImplKind.Native"/>.</summary>
        internal readonly SurtrNativeEntryPoint EntryPoint;

        /// <summary>The chunk holding the body. Only set when <see cref="ImplKind"/> is <see cref="SurtrMethodImplKind.Bytecode"/>.</summary>
        internal readonly SurtrChunk? Chunk;

        /// <summary>Byte offset of the body inside <see cref="Chunk"/>'s code. Bytecode closures only.</summary>
        internal readonly int CodeOffset;

        /// <summary>How many local slots a frame for this closure needs. Bytecode closures only.</summary>
        internal readonly int LocalCount;

        /// <summary>The deepest the operand stack gets while the body runs. Bytecode closures only.</summary>
        internal readonly int MaxStackSize;

        private readonly SurtrClassReference _typeReference;

        internal SurtrClosure(SurtrClassReference typeReference, SurtrMethodInfo method, SurtrValue[] upValues)
            : base(SurtrBuiltIns.Closure)
        {
            _typeReference = typeReference;
            Method = method;
            UpValues = upValues;
            ImplKind = method.ImplKind;
            ParameterCount = method.ParameterCount;

            switch (method)
            {
                case SurtrNativeMethodInfo native:
                    // The address is copied out flat here and read on every call afterwards, so a
                    // body bound later would never be seen. Loading is what binds one, so a closure
                    // over an unbound method means its module was never loaded - caught here for
                    // the same reason the abstract case below is.
                    if (!native.IsBound)
                        throw new ArgumentException(
                            $"Native method '{native.Name}' has no body bound; its module has not been loaded into a runtime that published '{native.LinkName}'.",
                            nameof(method));

                    EntryPoint = native.EntryPoint;
                    break;

                case SurtrBytecodeMethodInfo bytecode:
                    Chunk = bytecode.Chunk;
                    CodeOffset = bytecode.CodeOffset;
                    LocalCount = bytecode.LocalCount;
                    MaxStackSize = bytecode.MaxStackSize;
                    break;

                default:
                    // An abstract method has no body by definition, so there is nothing a closure
                    // over one could ever call. Caught here rather than at the call site, where it
                    // would be a null jump target in the middle of the interpreter loop.
                    throw new ArgumentException(
                        $"Cannot build a closure over '{method.Name}': it has no body ({method.ImplKind}).",
                        nameof(method));
            }
        }

        /// <summary>The method this closure calls.</summary>
        public SurtrMethodInfo TargetMethod
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Method;
        }

        /// <summary>
        /// This closure's full signature descriptor - <c>L(II)F</c> for
        /// <c>(int, int) -&gt; float</c>.
        /// </summary>
        public SurtrClassReference TypeReference
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _typeReference;
        }

        /// <summary>How many arguments this closure expects.</summary>
        public int Arity
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ParameterCount;
        }

        /// <summary>How many variables this closure captured.</summary>
        public int UpValueCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => UpValues.Length;
        }

        /// <summary>Whether the body is host code rather than bytecode.</summary>
        public bool IsNative
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ImplKind == SurtrMethodImplKind.Native;
        }

        /// <summary>Reads a captured value. The caller is responsible for the range check.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SurtrValue GetUpValue(int index) => UpValues[index];

        /// <summary>
        /// Calls a native closure's host function.
        /// </summary>
        /// <remarks>
        /// One indirect call through the managed function pointer, identical to the one
        /// <c>CallGlobalNative</c> and <c>InvokeNative</c> make - the interpreter reaches every
        /// host body the same way, whatever wrapped it. Only valid when <see cref="IsNative"/>;
        /// calling it on a bytecode closure jumps through a null pointer.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal SurtrValue InvokeNative(SurtrCallArguments arguments)
            => EntryPoint.Invoke(arguments);

        internal override void VisitReferences(SurtrEntityMarker marker)
        {
            // The captures are the only entity references a closure holds: the method and the
            // chunk are metadata, which the registry does not own.
            var upValues = UpValues;
            for (int i = 0; i < upValues.Length; i++)
                marker.Mark(upValues[i]);
        }
    }
}
