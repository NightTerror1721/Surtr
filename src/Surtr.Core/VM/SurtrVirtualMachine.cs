#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Runtime.Utilities;
using System;
using System.Buffers;
using System.Runtime.CompilerServices;

namespace Surtr.VM
{
    /// <summary>
    /// The Surtr virtual machine: the thing that actually executes bytecode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Internal on purpose.</b> The machine is the runtime's engine, not its API. A host holding
    /// one could push onto the data stack between calls or start a run at an arbitrary frame, and
    /// every invariant the interpreter depends on would become the host's to maintain. The host
    /// surface is <c>SurtrRuntime.Invoke</c> and its siblings, which push, run and clean up as one
    /// operation.
    /// </para>
    /// <para>
    /// <b>Two stacks.</b> The <em>data stack</em> is a flat block of unmanaged
    /// <see cref="SurtrRawValue"/> holding both frame locals and operands, so entering a call copies
    /// nothing - the callee's locals simply start where the caller left its arguments. The
    /// <em>call stack</em> is a managed <see cref="SurtrCallFrame"/> array, because a frame holds
    /// object references the CLR has to keep alive.
    /// </para>
    /// <para>
    /// <b>Fixed capacity, on purpose.</b> Neither stack ever grows. A growable data stack would have
    /// to be addressed by index rather than by pointer, because a reallocation would dangle every
    /// <c>sp</c> already spilled in a suspended dispatch loop - exactly what a re-entrant native
    /// call leaves behind. Fixing the size makes <c>sp</c> a register-resident pointer nothing can
    /// invalidate, and turns overflow into one compare per <em>call</em> rather than one per push.
    /// </para>
    /// <para>
    /// <b>Dispatch is one switch, not a table of function pointers.</b> A jump table costs a
    /// predicted indirect branch; a function-pointer table costs a real call per instruction, plus
    /// spilling <c>ip</c>, <c>sp</c> and the frame's cached pools across it. C# has no way to make
    /// that call a tail-jump, so the table loses on every axis. Everything hot therefore lives in
    /// locals of <see cref="Run"/>, and every opcode body is written out where it is used.
    /// </para>
    /// <para>
    /// <b>Re-entrancy.</b> A native function may call straight back in. That works because all
    /// machine state lives on this instance - the loop only <em>caches</em> it in locals - and the
    /// interpreter publishes <c>sp</c> and the executing frame's <c>IP</c> before every transfer
    /// into host code. A nested run pushes its frames above the current one and returns at its own
    /// depth, leaving everything below untouched.
    /// </para>
    /// <para>
    /// <b>Exceptions</b> are split in two. A Surtr <c>Throw</c> never becomes a CLR exception while
    /// a handler is in reach: the machine walks its own frames and jumps to the handler, so a caught
    /// exception costs a table scan. A trap the VM raises, or anything host code throws, arrives as
    /// a CLR exception, gets wrapped into an object, and is fed through the same search. Only an
    /// exception nothing catches leaves as a <see cref="SurtrThrownException"/>.
    /// </para>
    /// </remarks>
    internal sealed unsafe class SurtrVirtualMachine : IDisposable
    {
        /// <summary>How many value slots the data stack holds when no size is given: 512 KB worth.</summary>
        internal const int DefaultDataStackSlots = 64 * 1024;

        /// <summary>How many nested calls are allowed when no depth is given.</summary>
        internal const int DefaultCallDepth = 1024;

        private const int MinimumDataStackSlots = 256;
        private const int MinimumCallDepth = 8;

        private readonly SurtrRuntime _runtime;
        private readonly SurtrValueComparer _comparer;
        private readonly SurtrCallFrame[] _frames;

        /// <summary>
        /// Everything the machine itself keeps alive: slot 0 is the exception currently being
        /// unwound, and slot <c>depth + 1</c> is the closure the frame at <c>depth</c> is running.
        /// </summary>
        /// <remarks>
        /// Both are reachable from nowhere else. <c>InvokeClosure</c> takes its target off the stack
        /// and the frame's arguments take its place, and an in-flight exception has been popped from
        /// the stack of a frame that is about to be discarded. One store per call and per throw keeps
        /// the collector from sweeping either.
        /// </remarks>
        private readonly SurtrRawValue[] _roots;

        /// <summary>Scratch for <c>StrCat</c>'s operands when it joins more than two strings.</summary>
        /// <remarks>
        /// A field rather than a local array so a wide concatenation allocates the result and
        /// nothing else. Reusing it is sound because <c>StrCat</c> reads its operands, builds the
        /// string and is done - it transfers control nowhere in between, so no second use of the
        /// buffer can overlap the first, not even through a re-entrant native call.
        /// </remarks>
        private string[] _concatBuffer = new string[8];

        /// <summary>Writes <c>StrCat</c>'s gathered operands into the result it has just sized.</summary>
        /// <remarks>
        /// A cached static delegate, so the wide path costs one allocation - the string itself.
        /// Only the first <c>count</c> entries of the buffer belong to this instruction; the rest
        /// are whatever a previous concatenation left there.
        /// </remarks>
        private static readonly SpanAction<char, (string[] Parts, int Count)> ConcatParts =
            static (span, state) =>
            {
                int at = 0;
                for (int i = 0; i < state.Count; i++)
                {
                    string part = state.Parts[i];
                    part.AsSpan().CopyTo(span.Slice(at));
                    at += part.Length;
                }
            };

        private SurtrRawValue* _stack;
        private SurtrRawValue* _stackLimit;
        private SurtrRawValue* _sp;
        private int _frameCount;

        // Zero means unlimited, which is what every ordinary run uses. See StepBudget.
        private long _stepsRemaining;
        private bool _disposed;

        /// <summary>Creates a machine with the default stack sizes.</summary>
        internal SurtrVirtualMachine(SurtrRuntime runtime)
            : this(runtime, DefaultDataStackSlots, DefaultCallDepth) { }

        /// <summary>Creates a machine with explicit stack sizes.</summary>
        /// <param name="runtime">The runtime whose heap, globals and modules this machine executes against.</param>
        /// <param name="dataStackSlots">How many value slots the data stack holds. Never grows.</param>
        /// <param name="maxCallDepth">How many nested calls are allowed before the call stack traps.</param>
        internal SurtrVirtualMachine(SurtrRuntime runtime, int dataStackSlots, int maxCallDepth)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _comparer = runtime.ValueComparer;

            if (dataStackSlots < MinimumDataStackSlots)
                dataStackSlots = MinimumDataStackSlots;

            if (maxCallDepth < MinimumCallDepth)
                maxCallDepth = MinimumCallDepth;

            // Zeroed rather than merely allocated: a zeroed slot names nothing, so a collection that
            // happens to scan a slot the program has not written yet cannot retain anything.
            _stack = MemOps.AllocateZeroed<SurtrRawValue>((nuint)dataStackSlots);
            _stackLimit = _stack + dataStackSlots;
            _sp = _stack;

            _frames = new SurtrCallFrame[maxCallDepth];
            _roots = new SurtrRawValue[maxCallDepth + 1];
            _frameCount = 0;
        }

        /// <summary>Releases the data stack of a machine the host forgot to dispose.</summary>
        ~SurtrVirtualMachine() => ReleaseResources();

        #region State
        /// <summary>How many values are currently on the data stack.</summary>
        internal int StackCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => (int)(_sp - _stack);
        }

        /// <summary>How deep the call stack currently is.</summary>
        internal int CallDepth
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _frameCount;
        }

        /// <summary>The first slot of the data stack, for the collector to scan from.</summary>
        internal SurtrRawValue* StackBase
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _stack;
        }

        /// <summary>One past the last live slot of the data stack.</summary>
        internal SurtrRawValue* StackTop
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _sp;
        }

        /// <summary>The in-flight exception and the live frames' closures, as extra roots for a collection.</summary>
        internal ReadOnlySpan<SurtrRawValue> FrameRoots
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new ReadOnlySpan<SurtrRawValue>(_roots, 0, _frameCount + 1);
        }
        #endregion

        #region Entry Points
        /// <summary>Pushes a value onto the data stack.</summary>
        /// <exception cref="SurtrExecutionException">The data stack is full.</exception>
        internal void Push(SurtrValue value)
        {
            if (_sp >= _stackLimit)
                throw DataStackOverflow();

            *_sp++ = value.Raw;
        }

        /// <summary>
        /// Calls <paramref name="method"/> with the <paramref name="argumentCount"/> values already
        /// on the data stack.
        /// </summary>
        /// <remarks>
        /// Arguments arrive on the stack rather than in a span because that is where a call already
        /// puts them: a bytecode call site leaves them there, and the callee's frame starts
        /// underneath them, so the whole convention copies nothing. The arguments are consumed. For
        /// an instance method, argument 0 is the receiver, which is what makes <c>Ldl0</c> read
        /// <c>this</c> and <c>arguments[0]</c> the receiver in a native entry point.
        /// </remarks>
        /// <returns>The result, or <see cref="SurtrValue.Null"/> for a method that returns nothing.</returns>
        internal SurtrValue Call(SurtrMethodInfo method, int argumentCount)
        {
            if (method is null)
                throw new ArgumentNullException(nameof(method));

            if (method.ImplKind == SurtrMethodImplKind.Native)
            {
                SurtrRawValue* argumentBase = _sp - argumentCount;
                SurtrValue result = ((SurtrNativeMethodInfo)method).EntryPoint
                    .Invoke(new SurtrCallArguments(_runtime, argumentBase, argumentCount));

                _sp = argumentBase;
                return result;
            }

            if (method.ImplKind != SurtrMethodImplKind.Bytecode)
                throw new SurtrExecutionException($"'{method.Name}' is abstract and has no body to call.");

            int entryDepth = _frameCount;
            PushEntryFrame((SurtrBytecodeMethodInfo)method, argumentCount, null);
            return Execute(entryDepth);
        }

        /// <summary>Pushes <paramref name="arguments"/> and calls <paramref name="method"/>.</summary>
        internal SurtrValue Call(SurtrMethodInfo method, ReadOnlySpan<SurtrValue> arguments)
        {
            for (int i = 0; i < arguments.Length; i++)
                Push(arguments[i]);

            return Call(method, arguments.Length);
        }

        /// <summary>
        /// Calls <paramref name="closure"/> with the <paramref name="argumentCount"/> values already
        /// on the data stack.
        /// </summary>
        /// <remarks>
        /// Unlike the bytecode <c>InvokeClosure</c>, the closure itself is not on the stack here -
        /// the caller holds it - so nothing has to be shifted out of the way before the frame starts.
        /// </remarks>
        internal SurtrValue CallClosure(SurtrClosure closure, int argumentCount)
        {
            if (closure is null)
                throw new ArgumentNullException(nameof(closure));

            if (closure.ImplKind == SurtrMethodImplKind.Native)
            {
                SurtrRawValue* argumentBase = _sp - argumentCount;
                SurtrValue result = closure.EntryPoint
                    .Invoke(new SurtrCallArguments(_runtime, argumentBase, argumentCount));

                _sp = argumentBase;
                return result;
            }

            int entryDepth = _frameCount;
            PushEntryFrame((SurtrBytecodeMethodInfo)closure.Method, argumentCount, closure);
            return Execute(entryDepth);
        }

        /// <summary>Pushes <paramref name="arguments"/> and calls <paramref name="closure"/>.</summary>
        internal SurtrValue CallClosure(SurtrClosure closure, ReadOnlySpan<SurtrValue> arguments)
        {
            for (int i = 0; i < arguments.Length; i++)
                Push(arguments[i]);

            return CallClosure(closure, arguments.Length);
        }

        /// <summary>Drops everything on both stacks, for a host recovering from an uncaught exception.</summary>
        /// <summary>
        /// How many more instructions this machine may execute before it raises, or <c>0</c> for
        /// no limit.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Exists for one caller: compile-time evaluation. A <c>const fun</c> is folded by emitting
        /// its bytecode and running it on this interpreter rather than on a second evaluator in the
        /// compiler, which is what keeps compile-time and run-time semantics from drifting - but a
        /// <c>const fun</c> may loop, so it may loop forever, and a compiler that hangs is not
        /// acceptable. A ceiling turns that into a diagnostic.
        /// </para>
        /// <para>
        /// <strong>The count is not checked per instruction.</strong> It is charged where a program
        /// transfers control - every jump and switch arm, and every frame entry - because those are
        /// the only ways to run forever: straight-line code always reaches a return. So the dispatch
        /// path is exactly what it was before the budget existed, and an ordinary run pays one
        /// register decrement per executed jump rather than per instruction. Any new opcode that
        /// moves <c>ip</c> by an offset has to end at <c>Branched</c> rather than <c>Dispatch</c>,
        /// which is the one rule this scheme asks of the switch.
        /// </para>
        /// <para>
        /// Charging every jump rather than only the backward ones is deliberate: telling them apart
        /// would cost a compare on the taken path to make the budget marginally less conservative,
        /// and a budget only has to bound a run, not measure it.
        /// </para>
        /// <para>
        /// Entering an exception handler is charged too, at a flat rate: without that, a program
        /// could raise and catch in a loop and never be billed for the instructions in between,
        /// because a run that unwinds does not write its progress back.
        /// </para>
        /// </remarks>
        internal long StepBudget
        {
            get => _stepsRemaining;
            set => _stepsRemaining = value < 0 ? 0 : value;
        }

        /// <summary>What entering a handler costs against <see cref="StepBudget"/>.</summary>
        private const long HandlerEntryCost = 256;

        /// <summary>
        /// What <see cref="StepBudget"/> holds once a run has spent it: negative, so the very next
        /// dispatch aborts again rather than running free the way <c>0</c> - unlimited - would.
        /// </summary>
        private const long Exhausted = -1;

        internal void Reset()
        {
            _sp = _stack;

            for (int i = 0; i < _frameCount; i++)
            {
                _frames[i].Chunk = null;
                _frames[i].Method = null;
                _frames[i].Closure = null;
                _roots[i + 1] = 0;
            }

            _roots[0] = 0;
            _frameCount = 0;
        }

        /// <summary>
        /// Builds the frame a host-initiated call starts in.
        /// </summary>
        /// <remarks>
        /// The interpreter writes this sequence out by hand instead of calling here, because it runs
        /// on every bytecode call. This copy exists only for the host boundary, which is crossed
        /// once per entry and is nowhere near the execution path.
        /// </remarks>
        private void PushEntryFrame(SurtrBytecodeMethodInfo method, int argumentCount, SurtrClosure? closure)
        {
            int depth = _frameCount;
            if (depth == _frames.Length)
                throw CallStackOverflow(_frames.Length);

            SurtrRawValue* frameBase = _sp - argumentCount;
            int localCount = method.LocalCount;

            if (frameBase + localCount + method.MaxStackSize > _stackLimit)
                throw DataStackOverflow();

            if (localCount > argumentCount)
                MemOps.Clear(frameBase + argumentCount, (nuint)(localCount - argumentCount) * sizeof(SurtrRawValue));

            var chunk = method.Chunk;
            byte* codeBase = chunk.Code.Pointer;

            ref SurtrCallFrame frame = ref _frames[depth];
            frame.Base = frameBase;
            frame.CodeBase = codeBase;
            frame.IP = codeBase + method.CodeOffset;
            frame.Chunk = chunk;
            frame.Method = method;
            frame.Closure = closure;
            frame.LocalCount = localCount;
            frame.ArgumentCount = argumentCount;
            frame.ExpectedResults = 1;

            _roots[depth + 1] = closure is null
                ? 0
                : SurtrValue.TagMaskReference | (uint)closure.GetSurtrReference();

            _frameCount = depth + 1;
            _sp = frameBase + localCount;
        }
        #endregion

        #region Exception Dispatch
        /// <summary>
        /// Runs bytecode until the frame at <paramref name="entryDepth"/> returns, converting any
        /// CLR exception raised along the way into a Surtr one and offering it to the handler tables.
        /// </summary>
        /// <remarks>
        /// The <see langword="try"/> lives here, wrapped around <see cref="Run"/>, rather than
        /// inside the dispatch loop. A protected region spanning the loop would constrain how the
        /// JIT may keep <c>ip</c> and <c>sp</c> in registers across every instruction, to buy
        /// something the frame protocol already provides: the executing frame's <c>IP</c> is
        /// published before anything that can raise, so the handler search reads state, never
        /// locals. That is also what lets the loop be re-entered after a handler is installed -
        /// <see cref="Run"/> loads everything it needs from the top frame.
        /// </remarks>
        private SurtrValue Execute(int entryDepth)
        {
            while (true)
            {
                try
                {
                    return Run(entryDepth);
                }
                catch (SurtrBudgetExceededException)
                {
                    // Never offered to the handler tables. A program that could catch its own
                    // watchdog and keep looping would defeat the only thing the budget promises.
                    throw;
                }
                catch (SurtrThrownException thrown)
                {
                    // Already a Surtr object: it escaped a nested run, or came back out through a
                    // native call that had re-entered the VM.
                    if (!TryEnterHandler(thrown.Reference, entryDepth))
                        throw;
                }
                catch (Exception clrException)
                {
                    // A trap, or anything host code threw. Either becomes an ordinary object so it
                    // can go through the same handler search - a trap as the library class it
                    // names, so `catch (e: IndexOutOfRangeException)` takes it, and anything else
                    // as a native proxy, so `catch (native e)` still sees a CLR exception the way
                    // it sees any other host object.
                    if (!TryEnterHandler(AsSurtrObject(clrException), entryDepth))
                        throw;
                }
            }
        }

        /// <summary>
        /// Unwinds towards <paramref name="entryDepth"/> looking for a handler that covers where
        /// each frame is suspended and catches <paramref name="exception"/>'s type.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Stops at <paramref name="entryDepth"/> rather than at the bottom of the stack, which is
        /// what makes nesting compose: a run started from inside a native function unwinds only its
        /// own frames, then lets the exception leave as a CLR one so it travels back out through
        /// that native frame and resumes the search in the run below.
        /// </para>
        /// <para>
        /// A real method rather than an inlined block, because it is only ever reached once an
        /// exception exists - the dispatch loop's rule against helpers is about the path taken by
        /// every instruction, not by the ones that fail.
        /// </para>
        /// </remarks>
        /// <returns><see langword="true"/> if a handler was installed and execution can resume.</returns>
        private bool TryEnterHandler(SurtrRef exception, int entryDepth)
        {
            // Rooted for the whole search: it has already been popped off the stack of a frame that
            // is about to be discarded, so nothing else is keeping it alive.
            _roots[0] = SurtrValue.TagMaskReference | (uint)exception;

            var entities = _runtime.Context.EntityRegistry.Entities;
            SurtrClass? raisedClass = exception == 0 ? null : (entities[exception] as SurtrObject)?.Class;

            while (_frameCount > entryDepth)
            {
                int depth = _frameCount - 1;
                ref SurtrCallFrame frame = ref _frames[depth];

                if (frame.Method is SurtrBytecodeMethodInfo bytecode)
                {
                    var handlers = bytecode.Handlers;
                    int resumeOffset = (int)(frame.IP - frame.CodeBase);

                    for (int i = 0; i < handlers.Length; i++)
                    {
                        if (!handlers[i].Covers(resumeOffset) || !Catches(handlers[i], raisedClass))
                            continue;

                        // The handler starts with a clean operand stack holding just the exception,
                        // so whatever the protected region had half-built is discarded.
                        // Charged before the handler runs, so raise-and-catch cannot be used to
                        // spin without ever being billed - a run that unwinds never reaches the
                        // write-back in the dispatch loop.
                        if (_stepsRemaining != 0)
                        {
                            _stepsRemaining -= HandlerEntryCost;
                            if (_stepsRemaining <= 0)
                            {
                                _stepsRemaining = Exhausted;
                                throw StepBudgetExceeded();
                            }
                        }

                        SurtrRawValue* handlerSp = frame.Base + frame.LocalCount;
                        *handlerSp++ = SurtrValue.TagMaskReference | (uint)exception;

                        _sp = handlerSp;
                        frame.IP = frame.CodeBase + handlers[i].HandlerOffset;
                        _roots[0] = 0;
                        return true;
                    }
                }

                frame.Chunk = null;
                frame.Method = null;
                frame.Closure = null;
                _roots[depth + 1] = 0;
                _frameCount = depth;
            }

            // Left rooted deliberately: the exception is about to travel out to a host that may want
            // to inspect it, and Reset is what finally releases it.
            return false;
        }

        /// <summary>
        /// Turns a CLR exception into the Surtr object a <c>catch</c> clause will be matched
        /// against.
        /// </summary>
        /// <remarks>
        /// Cold by construction - it only runs once an exception exists - so it can afford the type
        /// tests. The fallback matters as much as the mapping: a host exception with no Surtr
        /// counterpart stays a proxy rather than being forced into a class it is not, which is what
        /// keeps <c>catch (native e)</c> meaningful.
        /// </remarks>
        private SurtrRef AsSurtrObject(Exception clrException)
        {
            var surtrType = SurtrBuiltIns.ExceptionClassFor(clrException);

            return surtrType is null
                ? _runtime.WrapNative(clrException).GetSurtrReference()
                : _runtime.NewException(surtrType, clrException.Message).GetSurtrReference();
        }

        /// <summary>Whether a handler's declared catch type admits the raised object's class.</summary>
        private static bool Catches(in SurtrExceptionHandler handler, SurtrClass? raisedClass)
        {
            var catchType = handler.CatchType;
            if (catchType is null)
                return true;

            if (raisedClass is null)
                return false;

            var target = catchType.ResolvedType;
            if (target is null)
                return false;

            return target.Kind == SurtrMemberKind.Interface
                ? raisedClass.Implements((SurtrInterface)target)
                : raisedClass.IsSubclassOf((SurtrClass)target);
        }
        #endregion

        #region Interpreter
        /// <summary>
        /// The dispatch loop, entered at whatever frame is currently on top.
        /// </summary>
        /// <remarks>
        /// <para>
        /// One method on purpose. Every hot piece of machine state - the instruction pointer, the
        /// stack pointer, the frame base, and the current chunk's pools - lives in a local here, so
        /// an instruction is a jump-table dispatch plus a handful of register operations. Splitting
        /// opcode bodies into helpers would put all of that back in memory across every call.
        /// </para>
        /// <para>
        /// <paramref name="entryDepth"/> is the call depth <em>before</em> the entry frame was
        /// pushed. Returning to it is what ends this run. Nothing else about the method is tied to
        /// where it started, which is why it can be re-entered after a handler is installed.
        /// </para>
        /// </remarks>
        // 512 is MethodImplOptions.AggressiveOptimization, which netstandard2.1 does not name even
        // though every runtime that honours it accepts the value. It matters here more than
        // anywhere else in the codebase: it keeps the dispatch loop out of tiered compilation, so
        // the interpreter is fully optimised from its first instruction rather than after a
        // rejit - and a method this size is exactly the kind the quick JIT compiles worst.
        [MethodImpl((MethodImplOptions)512)]
        private SurtrValue Run(int entryDepth)
        {
            var runtime = _runtime;
            var comparer = _comparer;
            ref SurtrContext context = ref runtime.Context;

            var frames = _frames;
            var roots = _roots;
            int maxDepth = frames.Length;

            SurtrRawValue* stackLimit = _stackLimit;
            SurtrRawValue* sp;

            // Held in a local for the same reason ip and sp are: a field read per instruction
            // would defeat the point. long.MaxValue stands in for "no limit" so the check itself
            // is unconditional and the branch predicts perfectly either way.
            bool budgeted = _stepsRemaining != 0;
            long steps = budgeted ? _stepsRemaining : long.MaxValue;

            // Both of these can move: registering an entity may grow the registry's array, and the
            // host may declare a global from inside a native call. Every site that can cause either
            // reloads them, and nothing else has to.
            var entities = context.EntityRegistry.Entities;
            SurtrRawValue* globals = context.Globals.VariableTable.Pointer;

            // Per-frame state, reloaded at LoadFrame whenever the executing frame changes. `current`
            // is what makes publishing the instruction pointer a single store with no bounds check,
            // which is why it is worth publishing at every site that can raise.
            ref SurtrCallFrame current = ref frames[0];
            byte* ip;
            SurtrRawValue* frameBase;
            SurtrChunk chunk;
            SurtrRawValue* constants;
            SurtrTypeHandle[] typeTable;
            SurtrFieldInfo[] fieldTable;
            SurtrMethodInfo[] methodTable;
            SurtrModule[] moduleTable;

            // The module's own view of the host globals it declared as `native`. Both tables are
            // per-chunk and bound by name at load, so a compiled module is not tied to the order a
            // particular host registered its globals in.
            int* nativeVariableSlots;
            SurtrNativeGlobalFunction[] nativeFunctionTable;

            SurtrClosure? closure;

            // The operands of the shared call-entry sequences below. Passing them in locals and
            // jumping keeps every call opcode from carrying its own copy of a twenty-line frame
            // setup, without turning that setup into a real call.
            SurtrMethodInfo pendingMethod = null!;
            SurtrClosure? pendingClosure = null;
            int pendingArguments = 0;
            int pendingResults = 0;

        LoadFrame:
            {
                current = ref frames[_frameCount - 1];
                ip = current.IP;
                frameBase = current.Base;
                chunk = current.Chunk!;
                closure = current.Closure;
                sp = _sp;

                constants = chunk.Constants.Pointer;
                typeTable = chunk.TypeTable;
                fieldTable = chunk.FieldTable;
                methodTable = chunk.MethodTable;
                moduleTable = chunk.ModuleTable;
                nativeVariableSlots = chunk.NativeVariableSlots.Pointer;
                nativeFunctionTable = chunk.NativeFunctionTable;
            }

            // Inline immediates are read a byte at a time and recomposed with shifts rather than
            // through a `*(int*)ip` cast. Two reasons, both load-bearing:
            //
            //  - Alignment. Instructions are variable-length, so an immediate lands wherever the
            //    preceding opcodes leave it, which is rarely its natural boundary. A wide load off
            //    an unaligned address is undefined per ECMA-335 - a plain `ldind` assumes natural
            //    alignment, and only an `unaligned.` prefix relaxes that. x86 tolerates it and
            //    ARM64 usually does, but "usually" is not what the hottest loop should rest on.
            //  - Endianness. Composing explicitly pins the encoding to little-endian regardless of
            //    the host, so the bytecode format is canonical rather than whatever the machine is.
            //
            // The alternative, MemoryMarshal.Read<T>, is a call - and a method this size is exactly
            // the kind the inliner gives up on, so it would not reliably fold away. The byte loads
            // issue in parallel and stay off the critical path, which the switch's indirect branch
            // dominates regardless. Reads off `sp` keep their wide casts: it is a SurtrRawValue*,
            // so every slot is already 8-byte aligned and `*(double*)sp` is well-defined.

            // Straight-line code always reaches a return, so the only way a program can run
            // forever is to keep transferring control: a backward jump, a switch, or a call.
            // Charging the budget here rather than in the dispatch path is what keeps the
            // interpreter byte-for-byte what it was before the budget existed - an ordinary run
            // pays one decrement per executed jump and per frame entry, never per instruction.
            //
            // Fallen into from LoadFrame on purpose, so entering a frame is charged too.
        Branched:
            if (--steps < 0)
            {
                current.IP = ip;
                _sp = sp;
                _stepsRemaining = Exhausted;
                throw StepBudgetExceeded();
            }

        Dispatch:
            switch ((OpCode)(*ip++))
            {
                case OpCode.Nop:
                    goto Dispatch;

                #region Stack Operations
                case OpCode.Dup:
                    *sp = *(sp - 1);
                    sp++;
                    goto Dispatch;

                case OpCode.Dup2:
                    *sp = *(sp - 2);
                    *(sp + 1) = *(sp - 1);
                    sp += 2;
                    goto Dispatch;

                case OpCode.Swap:
                {
                    SurtrRawValue top = *(sp - 1);
                    *(sp - 1) = *(sp - 2);
                    *(sp - 2) = top;
                    goto Dispatch;
                }

                case OpCode.Swap2:
                {
                    SurtrRawValue first = *(sp - 4);
                    SurtrRawValue second = *(sp - 3);
                    *(sp - 4) = *(sp - 2);
                    *(sp - 3) = *(sp - 1);
                    *(sp - 2) = first;
                    *(sp - 1) = second;
                    goto Dispatch;
                }

                case OpCode.PushNull:
                    *sp++ = SurtrValue.TagMaskReference;
                    goto Dispatch;

                case OpCode.PushI8:
                    *sp++ = SurtrValue.TagMaskInt | (uint)(int)(sbyte)*ip++;
                    goto Dispatch;

                case OpCode.PushI16:
                    *sp++ = SurtrValue.TagMaskInt | (uint)(int)(short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    goto Dispatch;

                case OpCode.PushI32:
                    *sp++ = SurtrValue.TagMaskInt | (uint)(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    goto Dispatch;

                // The two booleans and every character literal are pushed inline. They could go
                // through the constant pool, but the pool's first ten slots have single-byte
                // opcodes behind them and are better spent on the values that have no inline form
                // at all - floats and strings.
                case OpCode.PushTrue:
                    *sp++ = SurtrValue.TagMaskBool | 1UL;
                    goto Dispatch;

                case OpCode.PushFalse:
                    *sp++ = SurtrValue.TagMaskBool;
                    goto Dispatch;

                case OpCode.PushChar:
                    *sp++ = SurtrValue.TagMaskChar | (uint)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    goto Dispatch;

                case OpCode.Pop:
                    sp--;
                    goto Dispatch;
                #endregion

                #region Load / Store Operations
                case OpCode.Ldc:
                    *sp++ = constants[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    goto Dispatch;

                case OpCode.Ldc0: *sp++ = constants[0]; goto Dispatch;
                case OpCode.Ldc1: *sp++ = constants[1]; goto Dispatch;
                case OpCode.Ldc2: *sp++ = constants[2]; goto Dispatch;
                case OpCode.Ldc3: *sp++ = constants[3]; goto Dispatch;
                case OpCode.Ldc4: *sp++ = constants[4]; goto Dispatch;
                case OpCode.Ldc5: *sp++ = constants[5]; goto Dispatch;
                case OpCode.Ldc6: *sp++ = constants[6]; goto Dispatch;
                case OpCode.Ldc7: *sp++ = constants[7]; goto Dispatch;
                case OpCode.Ldc8: *sp++ = constants[8]; goto Dispatch;
                case OpCode.Ldc9: *sp++ = constants[9]; goto Dispatch;

                case OpCode.LdcX:
                    *sp++ = constants[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                    ip += 4;
                    goto Dispatch;

                case OpCode.LdcS:
                    *sp++ = constants[*ip++];
                    goto Dispatch;

                case OpCode.Ldl:
                    *sp++ = frameBase[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    goto Dispatch;

                case OpCode.Ldl0: *sp++ = frameBase[0]; goto Dispatch;
                case OpCode.Ldl1: *sp++ = frameBase[1]; goto Dispatch;
                case OpCode.Ldl2: *sp++ = frameBase[2]; goto Dispatch;
                case OpCode.Ldl3: *sp++ = frameBase[3]; goto Dispatch;
                case OpCode.Ldl4: *sp++ = frameBase[4]; goto Dispatch;
                case OpCode.Ldl5: *sp++ = frameBase[5]; goto Dispatch;

                case OpCode.LdlS:
                    *sp++ = frameBase[*ip++];
                    goto Dispatch;

                // The immediate indexes the *module's* import table, not the runtime's global
                // table, so one extra load stands between the instruction and the storage. That
                // load is what buys binding by name at load time - and with it a clear failure
                // when a host never registered the global, instead of a silent read of whatever
                // that index happens to name in this runtime.
                case OpCode.Ldg:
                    *sp++ = globals[nativeVariableSlots[(ip[0] | (ip[1] << 8))]];
                    ip += 2;
                    goto Dispatch;

                case OpCode.LdgX:
                    *sp++ = globals[nativeVariableSlots[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))]];
                    ip += 4;
                    goto Dispatch;

                case OpCode.Stl:
                    frameBase[(ip[0] | (ip[1] << 8))] = *--sp;
                    ip += 2;
                    goto Dispatch;

                case OpCode.Stl0: frameBase[0] = *--sp; goto Dispatch;
                case OpCode.Stl1: frameBase[1] = *--sp; goto Dispatch;
                case OpCode.Stl2: frameBase[2] = *--sp; goto Dispatch;
                case OpCode.Stl3: frameBase[3] = *--sp; goto Dispatch;
                case OpCode.Stl4: frameBase[4] = *--sp; goto Dispatch;
                case OpCode.Stl5: frameBase[5] = *--sp; goto Dispatch;

                case OpCode.StlS:
                    frameBase[*ip++] = *--sp;
                    goto Dispatch;

                case OpCode.Stg:
                    globals[nativeVariableSlots[(ip[0] | (ip[1] << 8))]] = *--sp;
                    ip += 2;
                    goto Dispatch;

                case OpCode.StgX:
                    globals[nativeVariableSlots[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))]] = *--sp;
                    ip += 4;
                    goto Dispatch;

                // A whole `i += 1` without the operand stack: one load, one add, one store, and the
                // slot never leaves the frame. Written out it is Ldl, PushI8, Add, Stl - four
                // dispatches for an update that a counted loop performs once per iteration.
                case OpCode.IncLocal:
                {
                    SurtrRawValue* slot = frameBase + ip[0];
                    *slot = SurtrValue.TagMaskInt | (uint)((int)*slot + (sbyte)ip[1]);
                    ip += 2;
                    goto Dispatch;
                }
                #endregion

                #region Arithmetic Operations
                case OpCode.Add:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) + right);
                    goto Dispatch;
                }

                case OpCode.FAdd:
                {
                    double right = *(double*)(--sp);
                    *(double*)(sp - 1) = *(double*)(sp - 1) + right;
                    goto Dispatch;
                }

                case OpCode.Sub:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) - right);
                    goto Dispatch;
                }

                case OpCode.FSub:
                {
                    double right = *(double*)(--sp);
                    *(double*)(sp - 1) = *(double*)(sp - 1) - right;
                    goto Dispatch;
                }

                case OpCode.Mul:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) * right);
                    goto Dispatch;
                }

                case OpCode.FMul:
                {
                    double right = *(double*)(--sp);
                    *(double*)(sp - 1) = *(double*)(sp - 1) * right;
                    goto Dispatch;
                }

                case OpCode.Div:
                {
                    int right = (int)*--sp;
                    int left = (int)*(sp - 1);

                    // Both cases are hardware faults on x64, not C# exceptions, so they are caught
                    // here rather than left to trap in a way the host cannot recover from.
                    if (right == 0 || (right == -1 && left == int.MinValue))
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw IntegerDivision(left, right);
                    }

                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)(left / right);
                    goto Dispatch;
                }

                case OpCode.FDiv:
                {
                    double right = *(double*)(--sp);
                    *(double*)(sp - 1) = *(double*)(sp - 1) / right;
                    goto Dispatch;
                }

                case OpCode.Mod:
                {
                    int right = (int)*--sp;
                    int left = (int)*(sp - 1);

                    if (right == 0 || (right == -1 && left == int.MinValue))
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw IntegerDivision(left, right);
                    }

                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)(left % right);
                    goto Dispatch;
                }

                case OpCode.FMod:
                {
                    double right = *(double*)(--sp);
                    *(double*)(sp - 1) = *(double*)(sp - 1) % right;
                    goto Dispatch;
                }

                case OpCode.Pow:
                {
                    int exponent = (int)*--sp;
                    if (exponent < 0)
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw NegativeExponent(exponent);
                    }

                    // Exponentiation by squaring, written out rather than calling Math.Pow and
                    // rounding back: the double round-trip loses exactness past 2^53 and costs a
                    // call the JIT cannot inline.
                    int factor = (int)*(sp - 1);
                    int result = 1;
                    while (exponent != 0)
                    {
                        if ((exponent & 1) != 0)
                            result *= factor;

                        factor *= factor;
                        exponent >>= 1;
                    }

                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)result;
                    goto Dispatch;
                }

                case OpCode.FPow:
                {
                    double exponent = *(double*)(--sp);
                    *(double*)(sp - 1) = Math.Pow(*(double*)(sp - 1), exponent);
                    goto Dispatch;
                }

                case OpCode.Neg:
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)(-(int)*(sp - 1));
                    goto Dispatch;

                case OpCode.FNeg:
                    // Flipping the sign bit rather than computing 0 - x, so negative zero and NaN
                    // both behave the way IEEE 754 says they should.
                    *(sp - 1) ^= 0x8000000000000000UL;
                    goto Dispatch;

                case OpCode.Inv:
                    *(sp - 1) = SurtrValue.TagMaskBool | ((*(sp - 1) & 1) ^ 1);
                    goto Dispatch;
                #endregion

                #region Comparison Operations
                case OpCode.EQ:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) == right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.FEQ:
                {
                    double right = *(double*)(--sp);
                    *(sp - 1) = SurtrValue.TagMaskBool | (*(double*)(sp - 1) == right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.REQ:
                {
                    // A reference is its 32-bit payload; the tag exists for the collector. Comparing
                    // payloads is what makes a zeroed slot and an explicit null the same reference.
                    uint right = (uint)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((uint)*(sp - 1) == right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.StrEQ:
                {
                    uint right = (uint)*--sp;
                    uint left = (uint)*(sp - 1);
                    bool equal = left == right
                        || (left != 0 && right != 0
                            && ((SurtrString)entities[(SurtrRef)left]!).TextEquals((SurtrString)entities[(SurtrRef)right]!));

                    *(sp - 1) = SurtrValue.TagMaskBool | (equal ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.NE:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) != right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.FNE:
                {
                    double right = *(double*)(--sp);
                    *(sp - 1) = SurtrValue.TagMaskBool | (*(double*)(sp - 1) != right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.RNE:
                {
                    uint right = (uint)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((uint)*(sp - 1) != right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.StrNE:
                {
                    uint right = (uint)*--sp;
                    uint left = (uint)*(sp - 1);
                    bool equal = left == right
                        || (left != 0 && right != 0
                            && ((SurtrString)entities[(SurtrRef)left]!).TextEquals((SurtrString)entities[(SurtrRef)right]!));

                    *(sp - 1) = SurtrValue.TagMaskBool | (equal ? 0UL : 1UL);
                    goto Dispatch;
                }

                case OpCode.GT:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) > right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.FGT:
                {
                    double right = *(double*)(--sp);
                    *(sp - 1) = SurtrValue.TagMaskBool | (*(double*)(sp - 1) > right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.GE:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) >= right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.FGE:
                {
                    double right = *(double*)(--sp);
                    *(sp - 1) = SurtrValue.TagMaskBool | (*(double*)(sp - 1) >= right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.LT:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) < right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.FLT:
                {
                    double right = *(double*)(--sp);
                    *(sp - 1) = SurtrValue.TagMaskBool | (*(double*)(sp - 1) < right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.LE:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) <= right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.FLE:
                {
                    double right = *(double*)(--sp);
                    *(sp - 1) = SurtrValue.TagMaskBool | (*(double*)(sp - 1) <= right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.IsNull:
                    *(sp - 1) = SurtrValue.TagMaskBool | ((uint)*(sp - 1) == 0 ? 1UL : 0UL);
                    goto Dispatch;

                case OpCode.IsNotNull:
                    *(sp - 1) = SurtrValue.TagMaskBool | ((uint)*(sp - 1) != 0 ? 1UL : 0UL);
                    goto Dispatch;

                case OpCode.InstanceOf:
                {
                    var target = typeTable[(ip[0] | (ip[1] << 8))].ResolvedType!;
                    ip += 2;

                    SurtrRef subject = (SurtrRef)(*(sp - 1));
                    bool matches = false;
                    if (subject != 0)
                    {
                        var subjectClass = ((SurtrObject)entities[subject]!).Class;
                        matches = target.Kind == SurtrMemberKind.Interface
                            ? subjectClass.Implements((SurtrInterface)target)
                            : subjectClass.IsSubclassOf((SurtrClass)target);
                    }

                    *(sp - 1) = SurtrValue.TagMaskBool | (matches ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.InstanceOfX:
                {
                    var target = typeTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].ResolvedType!;
                    ip += 4;

                    SurtrRef subject = (SurtrRef)(*(sp - 1));
                    bool matches = false;
                    if (subject != 0)
                    {
                        var subjectClass = ((SurtrObject)entities[subject]!).Class;
                        matches = target.Kind == SurtrMemberKind.Interface
                            ? subjectClass.Implements((SurtrInterface)target)
                            : subjectClass.IsSubclassOf((SurtrClass)target);
                    }

                    *(sp - 1) = SurtrValue.TagMaskBool | (matches ? 1UL : 0UL);
                    goto Dispatch;
                }
                #endregion

                #region Bitwise Operations
                case OpCode.And:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) & right);
                    goto Dispatch;
                }

                case OpCode.Or:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) | right);
                    goto Dispatch;
                }

                case OpCode.Xor:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) ^ right);
                    goto Dispatch;
                }

                case OpCode.Not:
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)(~(int)*(sp - 1));
                    goto Dispatch;

                case OpCode.Shl:
                {
                    // Masked rather than trapped: this is what the hardware does on both x64 and
                    // ARM, and defining it costs nothing where trapping would cost a branch.
                    int count = (int)*--sp & 31;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) << count);
                    goto Dispatch;
                }

                case OpCode.Shr:
                {
                    int count = (int)*--sp & 31;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((uint)*(sp - 1) >> count);
                    goto Dispatch;
                }

                case OpCode.Sar:
                {
                    int count = (int)*--sp & 31;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) >> count);
                    goto Dispatch;
                }
                #endregion

                #region Conversion Operations
                case OpCode.I2F:
                {
                    int value = (int)*(sp - 1);
                    *(double*)(sp - 1) = value;
                    goto Dispatch;
                }

                case OpCode.F2I:
                {
                    double value = *(double*)(sp - 1);

                    // Saturating, with NaN going to zero. An unchecked C# cast of an out-of-range
                    // double is platform-defined, and Surtr ships on x64 and ARM alike, so the
                    // three compares buy determinism the cast does not give.
                    int converted;
                    if (value >= 2147483647.0) converted = int.MaxValue;
                    else if (value <= -2147483648.0) converted = int.MinValue;
                    else if (double.IsNaN(value)) converted = 0;
                    else converted = (int)value;

                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)converted;
                    goto Dispatch;
                }

                case OpCode.I2C:
                    *(sp - 1) = SurtrValue.TagMaskChar | (uint)(ushort)(int)*(sp - 1);
                    goto Dispatch;

                case OpCode.C2I:
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)(int)*(sp - 1);
                    goto Dispatch;

                case OpCode.I2B:
                    // Normalises as well as retags, so every boolean payload is 0 or 1 and the
                    // boolean opcodes can treat it as a bit.
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) != 0 ? 1UL : 0UL);
                    goto Dispatch;

                case OpCode.B2I:
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)(int)*(sp - 1);
                    goto Dispatch;

                case OpCode.BoxInt:
                {
                    current.IP = ip;
                    _sp = sp;
                    var boxed = new SurtrBoxed(SurtrBuiltIns.Integer, SurtrValue.FromRaw(*(sp - 1)));
                    SurtrRef reference = context.EntityRegistry.Register(boxed);
                    entities = context.EntityRegistry.Entities;
                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.BoxFloat:
                {
                    current.IP = ip;
                    _sp = sp;
                    var boxed = new SurtrBoxed(SurtrBuiltIns.Float, SurtrValue.FromRaw(*(sp - 1)));
                    SurtrRef reference = context.EntityRegistry.Register(boxed);
                    entities = context.EntityRegistry.Entities;
                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.BoxBool:
                {
                    current.IP = ip;
                    _sp = sp;
                    var boxed = new SurtrBoxed(SurtrBuiltIns.Boolean, SurtrValue.FromRaw(*(sp - 1)));
                    SurtrRef reference = context.EntityRegistry.Register(boxed);
                    entities = context.EntityRegistry.Entities;
                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.BoxChar:
                {
                    current.IP = ip;
                    _sp = sp;
                    var boxed = new SurtrBoxed(SurtrBuiltIns.Character, SurtrValue.FromRaw(*(sp - 1)));
                    SurtrRef reference = context.EntityRegistry.Register(boxed);
                    entities = context.EntityRegistry.Entities;
                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.Unbox:
                    *(sp - 1) = ((SurtrBoxed)entities[(SurtrRef)(*(sp - 1))]!).Value.Raw;
                    goto Dispatch;

                case OpCode.Cast:
                {
                    var target = typeTable[(ip[0] | (ip[1] << 8))].ResolvedType!;
                    ip += 2;

                    SurtrRef subject = (SurtrRef)(*(sp - 1));
                    if (subject != 0)
                    {
                        var subjectClass = ((SurtrObject)entities[subject]!).Class;
                        bool matches = target.Kind == SurtrMemberKind.Interface
                            ? subjectClass.Implements((SurtrInterface)target)
                            : subjectClass.IsSubclassOf((SurtrClass)target);

                        if (!matches)
                        {
                            current.IP = ip;
                            _sp = sp;
                            throw InvalidCast(subjectClass.Name, target.Name);
                        }
                    }

                    goto Dispatch;
                }

                case OpCode.CastX:
                {
                    var target = typeTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].ResolvedType!;
                    ip += 4;

                    SurtrRef subject = (SurtrRef)(*(sp - 1));
                    if (subject != 0)
                    {
                        var subjectClass = ((SurtrObject)entities[subject]!).Class;
                        bool matches = target.Kind == SurtrMemberKind.Interface
                            ? subjectClass.Implements((SurtrInterface)target)
                            : subjectClass.IsSubclassOf((SurtrClass)target);

                        if (!matches)
                        {
                            current.IP = ip;
                            _sp = sp;
                            throw InvalidCast(subjectClass.Name, target.Name);
                        }
                    }

                    goto Dispatch;
                }

                // `as?`. One type test where the lowering it replaces - spill, InstanceOf, branch,
                // Cast - pays for two, and the failure answer is already representable in the slot
                // the subject occupies.
                case OpCode.CastOrNull:
                {
                    var target = typeTable[(ip[0] | (ip[1] << 8))].ResolvedType!;
                    ip += 2;

                    SurtrRef subject = (SurtrRef)(*(sp - 1));
                    if (subject != 0)
                    {
                        var subjectClass = ((SurtrObject)entities[subject]!).Class;
                        bool matches = target.Kind == SurtrMemberKind.Interface
                            ? subjectClass.Implements((SurtrInterface)target)
                            : subjectClass.IsSubclassOf((SurtrClass)target);

                        if (!matches)
                            *(sp - 1) = SurtrValue.TagMaskReference;
                    }

                    goto Dispatch;
                }

                case OpCode.CastOrNullX:
                {
                    var target = typeTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].ResolvedType!;
                    ip += 4;

                    SurtrRef subject = (SurtrRef)(*(sp - 1));
                    if (subject != 0)
                    {
                        var subjectClass = ((SurtrObject)entities[subject]!).Class;
                        bool matches = target.Kind == SurtrMemberKind.Interface
                            ? subjectClass.Implements((SurtrInterface)target)
                            : subjectClass.IsSubclassOf((SurtrClass)target);

                        if (!matches)
                            *(sp - 1) = SurtrValue.TagMaskReference;
                    }

                    goto Dispatch;
                }
                #endregion

                #region String Operations
                case OpCode.StrLen:
                    *(sp - 1) = SurtrValue.TagMaskInt
                        | (uint)((SurtrString)entities[(SurtrRef)(*(sp - 1))]!).Value.Length;
                    goto Dispatch;

                case OpCode.StrHash:
                    // A load, not a walk: the hash is computed once, on first need, and cached on
                    // the string - and is the same in any process, which is what a compiled string
                    // switch needs.
                    *(sp - 1) = SurtrValue.TagMaskInt
                        | (uint)((SurtrString)entities[(SurtrRef)(*(sp - 1))]!).Hash;
                    goto Dispatch;

                case OpCode.StrCat:
                {
                    int count = *ip++;
                    current.IP = ip;
                    _sp = sp;

                    sp -= count;

                    // Two operands is what every `a + b` is, and it needs no buffer at all. Anything
                    // wider gathers into the reusable one and writes the result in a single pass,
                    // so an n-part concatenation allocates one string rather than n - 1.
                    string joined;
                    if (count == 2)
                    {
                        joined = string.Concat(
                            ((SurtrString)entities[(SurtrRef)sp[0]]!).Value,
                            ((SurtrString)entities[(SurtrRef)sp[1]]!).Value);
                    }
                    else
                    {
                        var parts = _concatBuffer;
                        if (parts.Length < count)
                            parts = _concatBuffer = new string[count];

                        int total = 0;
                        for (int i = 0; i < count; i++)
                        {
                            string part = ((SurtrString)entities[(SurtrRef)sp[i]]!).Value;
                            parts[i] = part;
                            total += part.Length;
                        }

                        joined = string.Create(total, (parts, count), ConcatParts);
                    }

                    SurtrRef reference = context.EntityRegistry.Register(new SurtrString(joined));
                    entities = context.EntityRegistry.Entities;

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.StrGet:
                {
                    int index = (int)*--sp;
                    string text = ((SurtrString)entities[(SurtrRef)(*(sp - 1))]!).Value;

                    if ((uint)index >= (uint)text.Length)
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw IndexOutOfRange(index, text.Length, "string");
                    }

                    *(sp - 1) = SurtrValue.TagMaskChar | (uint)text[index];
                    goto Dispatch;
                }
                #endregion

                #region Array Operations
                case OpCode.ArrNew:
                {
                    var arrayType = typeTable[(ip[0] | (ip[1] << 8))].Reference;
                    ip += 2;
                    current.IP = ip;
                    _sp = sp;

                    int length = (int)*(sp - 1);
                    var array = new SurtrArray(arrayType, length);
                    array.InitializeLength(length);

                    // A zeroed slot already reads as 0, 0.0, false, '\0' or null, so only the tag
                    // needs fixing - and only for the families whose tag is not zero. A float or
                    // reference array is correct with no work at all.
                    SurtrRawValue elementZero = ZeroOf(arrayType.NestedTypeCode);
                    if (elementZero != 0)
                    {
                        var items = array.Items;
                        for (int i = 0; i < length; i++)
                            items[i] = SurtrValue.FromRaw(elementZero);
                    }

                    SurtrRef reference = context.EntityRegistry.Register(array);
                    entities = context.EntityRegistry.Entities;

                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.ArrNewX:
                {
                    var arrayType = typeTable[(ip[0] | (ip[1] << 8))].Reference;
                    int length = (ip[2] | (ip[3] << 8) | (ip[4] << 16) | (ip[5] << 24));
                    ip += 6;
                    current.IP = ip;
                    _sp = sp;

                    var array = new SurtrArray(arrayType, length);
                    array.InitializeLength(length);

                    SurtrRawValue elementZero = ZeroOf(arrayType.NestedTypeCode);
                    if (elementZero != 0)
                    {
                        var items = array.Items;
                        for (int i = 0; i < length; i++)
                            items[i] = SurtrValue.FromRaw(elementZero);
                    }

                    SurtrRef reference = context.EntityRegistry.Register(array);
                    entities = context.EntityRegistry.Entities;

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.ArrPack:
                {
                    var arrayType = typeTable[(ip[0] | (ip[1] << 8))].Reference;
                    int count = (ip[2] | (ip[3] << 8));
                    ip += 4;
                    current.IP = ip;
                    _sp = sp;

                    var array = new SurtrArray(arrayType, count);
                    array.InitializeLength(count);
                    SurtrRef reference = context.EntityRegistry.Register(array);
                    entities = context.EntityRegistry.Entities;

                    var items = array.Items;
                    sp -= count;
                    for (int i = 0; i < count; i++)
                        items[i] = SurtrValue.FromRaw(sp[i]);

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.ArrLen:
                    *(sp - 1) = SurtrValue.TagMaskInt
                        | (uint)((SurtrArray)entities[(SurtrRef)(*(sp - 1))]!).Count;
                    goto Dispatch;

                case OpCode.ArrGet:
                {
                    int index = (int)*--sp;
                    var array = (SurtrArray)entities[(SurtrRef)(*(sp - 1))]!;

                    if ((uint)index >= (uint)array.Count)
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw IndexOutOfRange(index, array.Count, "array");
                    }

                    // Two range checks, not one: the explicit trap above, and the CLR's own on the
                    // managed buffer, which the JIT cannot elide because it compares against
                    // Items.Length rather than Count. Removing the second needs Unsafe.Add, which
                    // netstandard2.1 does not carry without a NuGet dependency a Unity host would
                    // have to ship as well - so it stays until the target framework moves.
                    *(sp - 1) = array.Items[index].Raw;
                    goto Dispatch;
                }

                case OpCode.ArrSet:
                {
                    SurtrRawValue value = *--sp;
                    int index = (int)*--sp;
                    var array = (SurtrArray)entities[(SurtrRef)(*--sp)]!;

                    if ((uint)index >= (uint)array.Count)
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw IndexOutOfRange(index, array.Count, "array");
                    }

                    array.Items[index] = SurtrValue.FromRaw(value);
                    goto Dispatch;
                }

                case OpCode.ArrPush:
                {
                    SurtrRawValue value = *--sp;
                    var array = (SurtrArray)entities[(SurtrRef)(*--sp)]!;

                    // Written out rather than calling Add, so the common case - room already
                    // available - is a store and an increment with no call at all.
                    int count = array.Count;
                    if (count == array.Items.Length)
                    {
                        current.IP = ip;
                        _sp = sp;
                        array.EnsureCapacity(count + 1);
                    }

                    array.Items[count] = SurtrValue.FromRaw(value);
                    array.Count = count + 1;
                    goto Dispatch;
                }

                case OpCode.ArrPop:
                {
                    var array = (SurtrArray)entities[(SurtrRef)(*(sp - 1))]!;
                    int last = array.Count - 1;

                    if (last < 0)
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw EmptyArray();
                    }

                    *(sp - 1) = array.Items[last].Raw;

                    // Blanked, not merely abandoned: a stale reference past Count would keep an
                    // entity alive the moment anything traced beyond the live prefix.
                    array.Items[last] = SurtrValue.Null;
                    array.Count = last;
                    goto Dispatch;
                }

                case OpCode.ArrInsert:
                {
                    SurtrRawValue value = *--sp;
                    int index = (int)*--sp;
                    var array = (SurtrArray)entities[(SurtrRef)(*--sp)]!;

                    if ((uint)index > (uint)array.Count)
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw IndexOutOfRange(index, array.Count, "array");
                    }

                    current.IP = ip;
                    _sp = sp;
                    array.Insert(index, SurtrValue.FromRaw(value));
                    goto Dispatch;
                }

                case OpCode.ArrRemoveAt:
                {
                    int index = (int)*--sp;
                    var array = (SurtrArray)entities[(SurtrRef)(*--sp)]!;

                    if ((uint)index >= (uint)array.Count)
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw IndexOutOfRange(index, array.Count, "array");
                    }

                    array.RemoveAt(index);
                    goto Dispatch;
                }

                case OpCode.ArrClear:
                    ((SurtrArray)entities[(SurtrRef)(*--sp)]!).Clear();
                    goto Dispatch;

                case OpCode.ArrIndexOf:
                {
                    SurtrValue needle = SurtrValue.FromRaw(*--sp);
                    var array = (SurtrArray)entities[(SurtrRef)(*(sp - 1))]!;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)array.IndexOf(needle, comparer);
                    goto Dispatch;
                }

                case OpCode.ArrIn:
                {
                    SurtrValue needle = SurtrValue.FromRaw(*--sp);
                    var array = (SurtrArray)entities[(SurtrRef)(*(sp - 1))]!;
                    *(sp - 1) = SurtrValue.TagMaskBool | (array.IndexOf(needle, comparer) >= 0 ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.ArrNIn:
                {
                    SurtrValue needle = SurtrValue.FromRaw(*--sp);
                    var array = (SurtrArray)entities[(SurtrRef)(*(sp - 1))]!;
                    *(sp - 1) = SurtrValue.TagMaskBool | (array.IndexOf(needle, comparer) < 0 ? 1UL : 0UL);
                    goto Dispatch;
                }
                #endregion

                #region Tuple Operations
                case OpCode.TupPack:
                {
                    var tupleType = typeTable[(ip[0] | (ip[1] << 8))].Reference;
                    int arity = ip[2];
                    ip += 3;
                    current.IP = ip;
                    _sp = sp;

                    var tuple = new SurtrTuple(tupleType, arity);
                    SurtrRef reference = context.EntityRegistry.Register(tuple);
                    entities = context.EntityRegistry.Entities;

                    var elements = tuple.Elements;
                    sp -= arity;
                    for (int i = 0; i < arity; i++)
                        elements[i] = SurtrValue.FromRaw(sp[i]);

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.TupUnpack:
                {
                    int arity = *ip++;
                    var elements = ((SurtrTuple)entities[(SurtrRef)(*(sp - 1))]!).Elements;

                    sp--;
                    for (int i = 0; i < arity; i++)
                        *sp++ = elements[i].Raw;

                    goto Dispatch;
                }

                case OpCode.TupLen:
                    *(sp - 1) = SurtrValue.TagMaskInt
                        | (uint)((SurtrTuple)entities[(SurtrRef)(*(sp - 1))]!).Elements.Length;
                    goto Dispatch;

                case OpCode.TupGet:
                {
                    int index = (int)*--sp;
                    var elements = ((SurtrTuple)entities[(SurtrRef)(*(sp - 1))]!).Elements;

                    if ((uint)index >= (uint)elements.Length)
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw IndexOutOfRange(index, elements.Length, "tuple");
                    }

                    *(sp - 1) = elements[index].Raw;
                    goto Dispatch;
                }

                // What an element access actually compiles to: a tuple index has to be a constant
                // for the element's type to be known, so the push TupGet needs is one the compiler
                // can always fold into the instruction.
                case OpCode.TupGetC:
                {
                    int index = *ip++;
                    var elements = ((SurtrTuple)entities[(SurtrRef)(*(sp - 1))]!).Elements;

                    if ((uint)index >= (uint)elements.Length)
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw IndexOutOfRange(index, elements.Length, "tuple");
                    }

                    *(sp - 1) = elements[index].Raw;
                    goto Dispatch;
                }
                #endregion

                #region Dictionary Operations
                case OpCode.DictNew:
                {
                    var dictionaryType = typeTable[(ip[0] | (ip[1] << 8))].Reference;
                    ip += 2;
                    current.IP = ip;
                    _sp = sp;

                    SurtrRef reference = context.EntityRegistry.Register(
                        new SurtrDictionary(dictionaryType, comparer, 0));
                    entities = context.EntityRegistry.Entities;

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.DictPack:
                {
                    var dictionaryType = typeTable[(ip[0] | (ip[1] << 8))].Reference;
                    int count = (ip[2] | (ip[3] << 8));
                    ip += 4;
                    current.IP = ip;
                    _sp = sp;

                    var dictionary = new SurtrDictionary(dictionaryType, comparer, count);
                    SurtrRef reference = context.EntityRegistry.Register(dictionary);
                    entities = context.EntityRegistry.Entities;

                    sp -= count * 2;
                    for (int i = 0; i < count; i++)
                        dictionary.Entries[SurtrValue.FromRaw(sp[i * 2])] = SurtrValue.FromRaw(sp[i * 2 + 1]);

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.DictLen:
                    *(sp - 1) = SurtrValue.TagMaskInt
                        | (uint)((SurtrDictionary)entities[(SurtrRef)(*(sp - 1))]!).Entries.Count;
                    goto Dispatch;

                case OpCode.DictGet:
                {
                    SurtrValue key = SurtrValue.FromRaw(*--sp);
                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*(sp - 1))]!;

                    if (!dictionary.Entries.TryGetValue(key, out SurtrValue found))
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw MissingKey();
                    }

                    *(sp - 1) = found.Raw;
                    goto Dispatch;
                }

                case OpCode.DictSet:
                {
                    SurtrValue value = SurtrValue.FromRaw(*--sp);
                    SurtrValue key = SurtrValue.FromRaw(*--sp);
                    current.IP = ip;
                    _sp = sp;
                    ((SurtrDictionary)entities[(SurtrRef)(*--sp)]!).Entries[key] = value;
                    goto Dispatch;
                }

                case OpCode.DictDel:
                {
                    SurtrValue key = SurtrValue.FromRaw(*--sp);
                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*(sp - 1))]!;
                    *(sp - 1) = SurtrValue.TagMaskBool | (dictionary.Entries.Remove(key) ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.DictClear:
                    ((SurtrDictionary)entities[(SurtrRef)(*--sp)]!).Entries.Clear();
                    goto Dispatch;

                case OpCode.DictKeys:
                {
                    var arrayType = typeTable[(ip[0] | (ip[1] << 8))].Reference;
                    ip += 2;
                    current.IP = ip;
                    _sp = sp;

                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*(sp - 1))]!;
                    var keys = new SurtrArray(arrayType, dictionary.Entries.Count);
                    dictionary.CopyKeysTo(keys);

                    SurtrRef reference = context.EntityRegistry.Register(keys);
                    entities = context.EntityRegistry.Entities;

                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.DictValues:
                {
                    var arrayType = typeTable[(ip[0] | (ip[1] << 8))].Reference;
                    ip += 2;
                    current.IP = ip;
                    _sp = sp;

                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*(sp - 1))]!;
                    var values = new SurtrArray(arrayType, dictionary.Entries.Count);
                    dictionary.CopyValuesTo(values);

                    SurtrRef reference = context.EntityRegistry.Register(values);
                    entities = context.EntityRegistry.Entities;

                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.DictIn:
                {
                    SurtrValue key = SurtrValue.FromRaw(*--sp);
                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*(sp - 1))]!;
                    *(sp - 1) = SurtrValue.TagMaskBool | (dictionary.Entries.ContainsKey(key) ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.DictNIn:
                {
                    SurtrValue key = SurtrValue.FromRaw(*--sp);
                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*(sp - 1))]!;
                    *(sp - 1) = SurtrValue.TagMaskBool | (dictionary.Entries.ContainsKey(key) ? 0UL : 1UL);
                    goto Dispatch;
                }
                #endregion

                #region Object Operations
                case OpCode.ObjNew:
                {
                    var declared = typeTable[(ip[0] | (ip[1] << 8))].ResolvedClass!;
                    ip += 2;
                    current.IP = ip;
                    _sp = sp;

                    SurtrRef reference = context.EntityRegistry.Register(new SurtrInstance(declared));
                    entities = context.EntityRegistry.Entities;

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.ObjNewX:
                {
                    var declared = typeTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].ResolvedClass!;
                    ip += 4;
                    current.IP = ip;
                    _sp = sp;

                    SurtrRef reference = context.EntityRegistry.Register(new SurtrInstance(declared));
                    entities = context.EntityRegistry.Entities;

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }
                #endregion

                #region Field Operations
                case OpCode.FieldGet:
                {
                    int slot = fieldTable[(ip[0] | (ip[1] << 8))].SlotIndex;
                    ip += 2;

                    var instance = (SurtrInstance)entities[(SurtrRef)(*(sp - 1))]!;
                    *(sp - 1) = instance.Fields[slot].Raw;
                    goto Dispatch;
                }

                case OpCode.FieldSet:
                {
                    int slot = fieldTable[(ip[0] | (ip[1] << 8))].SlotIndex;
                    ip += 2;

                    SurtrRawValue value = *--sp;
                    var instance = (SurtrInstance)entities[(SurtrRef)(*--sp)]!;
                    instance.Fields[slot] = SurtrValue.FromRaw(value);
                    goto Dispatch;
                }

                case OpCode.StaticFieldGet:
                    // One indirect load: the linker resolved the slot's address when its owner was
                    // laid out, so nothing here tests whether the owner is a class or a module.
                    *sp++ = *fieldTable[(ip[0] | (ip[1] << 8))].StaticAddress;
                    ip += 2;
                    goto Dispatch;

                case OpCode.StaticFieldGetX:
                    *sp++ = *fieldTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].StaticAddress;
                    ip += 4;
                    goto Dispatch;

                case OpCode.StaticFieldSet:
                    *fieldTable[(ip[0] | (ip[1] << 8))].StaticAddress = *--sp;
                    ip += 2;
                    goto Dispatch;

                case OpCode.StaticFieldSetX:
                    *fieldTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].StaticAddress = *--sp;
                    ip += 4;
                    goto Dispatch;
                #endregion

                #region Closure Operations
                case OpCode.NewClosure:
                {
                    var target = methodTable[(ip[0] | (ip[1] << 8))];
                    int captureCount = ip[2];
                    ip += 3;
                    current.IP = ip;
                    _sp = sp;

                    var captures = captureCount > 0 ? new SurtrValue[captureCount] : Array.Empty<SurtrValue>();
                    sp -= captureCount;
                    for (int i = 0; i < captureCount; i++)
                        captures[i] = SurtrValue.FromRaw(sp[i]);

                    SurtrRef reference = context.EntityRegistry.Register(
                        new SurtrClosure(target.ToSignature(), target, captures));
                    entities = context.EntityRegistry.Entities;

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.NewClosureX:
                {
                    var target = methodTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                    int captureCount = ip[4];
                    ip += 5;
                    current.IP = ip;
                    _sp = sp;

                    var captures = captureCount > 0 ? new SurtrValue[captureCount] : Array.Empty<SurtrValue>();
                    sp -= captureCount;
                    for (int i = 0; i < captureCount; i++)
                        captures[i] = SurtrValue.FromRaw(sp[i]);

                    SurtrRef reference = context.EntityRegistry.Register(
                        new SurtrClosure(target.ToSignature(), target, captures));
                    entities = context.EntityRegistry.Entities;

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }
                #endregion

                #region Upvalue Operations
                case OpCode.UpValueGet:
                    *sp++ = closure!.UpValues[*ip++].Raw;
                    goto Dispatch;
                #endregion

                #region Control Flow Operations
                case OpCode.JPZ:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    if ((uint)*--sp == 0) ip += offset;
                    goto Branched;
                }

                case OpCode.JPNZ:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    if ((uint)*--sp != 0) ip += offset;
                    goto Branched;
                }

                case OpCode.JPN:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    if ((uint)*--sp == 0) ip += offset;
                    goto Branched;
                }

                case OpCode.JPNN:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    if ((uint)*--sp != 0) ip += offset;
                    goto Branched;
                }

                case OpCode.JP:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2 + offset;
                    goto Branched;
                }

                case OpCode.JPZX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    if ((uint)*--sp == 0) ip += offset;
                    goto Branched;
                }

                case OpCode.JPNZX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    if ((uint)*--sp != 0) ip += offset;
                    goto Branched;
                }

                case OpCode.JPNX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    if ((uint)*--sp == 0) ip += offset;
                    goto Branched;
                }

                case OpCode.JPNNX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    if ((uint)*--sp != 0) ip += offset;
                    goto Branched;
                }

                case OpCode.JPX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4 + offset;
                    goto Branched;
                }

                case OpCode.JPEQ:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if ((int)sp[0] == (int)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPFEQ:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if (((double*)sp)[0] == ((double*)sp)[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPREQ:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if ((uint)sp[0] == (uint)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPStrEQ:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    uint left = (uint)sp[0];
                    uint right = (uint)sp[1];
                    if (left == right
                        || (left != 0 && right != 0
                            && ((SurtrString)entities[(SurtrRef)left]!).TextEquals((SurtrString)entities[(SurtrRef)right]!)))
                        ip += offset;
                    goto Branched;
                }

                case OpCode.JPEQX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if ((int)sp[0] == (int)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPFEQX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if (((double*)sp)[0] == ((double*)sp)[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPREQX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if ((uint)sp[0] == (uint)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPStrEQX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    uint left = (uint)sp[0];
                    uint right = (uint)sp[1];
                    if (left == right
                        || (left != 0 && right != 0
                            && ((SurtrString)entities[(SurtrRef)left]!).TextEquals((SurtrString)entities[(SurtrRef)right]!)))
                        ip += offset;
                    goto Branched;
                }

                case OpCode.JPNE:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if ((int)sp[0] != (int)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPFNE:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if (((double*)sp)[0] != ((double*)sp)[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPRNE:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if ((uint)sp[0] != (uint)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPStrNE:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    uint left = (uint)sp[0];
                    uint right = (uint)sp[1];
                    if (!(left == right
                        || (left != 0 && right != 0
                            && ((SurtrString)entities[(SurtrRef)left]!).TextEquals((SurtrString)entities[(SurtrRef)right]!))))
                        ip += offset;
                    goto Branched;
                }

                case OpCode.JPNEX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if ((int)sp[0] != (int)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPFNEX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if (((double*)sp)[0] != ((double*)sp)[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPRNEX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if ((uint)sp[0] != (uint)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPStrNEX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    uint left = (uint)sp[0];
                    uint right = (uint)sp[1];
                    if (!(left == right
                        || (left != 0 && right != 0
                            && ((SurtrString)entities[(SurtrRef)left]!).TextEquals((SurtrString)entities[(SurtrRef)right]!))))
                        ip += offset;
                    goto Branched;
                }

                case OpCode.JPGT:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if ((int)sp[0] > (int)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPFGT:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if (((double*)sp)[0] > ((double*)sp)[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPGTX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if ((int)sp[0] > (int)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPFGTX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if (((double*)sp)[0] > ((double*)sp)[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPGE:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if ((int)sp[0] >= (int)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPFGE:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if (((double*)sp)[0] >= ((double*)sp)[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPGEX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if ((int)sp[0] >= (int)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPFGEX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if (((double*)sp)[0] >= ((double*)sp)[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPLT:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if ((int)sp[0] < (int)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPFLT:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if (((double*)sp)[0] < ((double*)sp)[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPLTX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if ((int)sp[0] < (int)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPFLTX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if (((double*)sp)[0] < ((double*)sp)[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPLE:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if ((int)sp[0] <= (int)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPFLE:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if (((double*)sp)[0] <= ((double*)sp)[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPLEX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if ((int)sp[0] <= (int)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPFLEX:
                {
                    int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    sp -= 2;
                    if (((double*)sp)[0] <= ((double*)sp)[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.JPInstanceOf:
                {
                    var target = typeTable[(ip[0] | (ip[1] << 8))].ResolvedType!;
                    short offset = (short)(ip[2] | (ip[3] << 8));
                    ip += 4;

                    SurtrRef subject = (SurtrRef)(*--sp);
                    if (subject != 0)
                    {
                        var subjectClass = ((SurtrObject)entities[subject]!).Class;
                        bool matches = target.Kind == SurtrMemberKind.Interface
                            ? subjectClass.Implements((SurtrInterface)target)
                            : subjectClass.IsSubclassOf((SurtrClass)target);

                        if (matches) ip += offset;
                    }

                    goto Branched;
                }

                case OpCode.JPInstanceOfX:
                {
                    var target = typeTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].ResolvedType!;
                    int offset = (ip[4] | (ip[5] << 8) | (ip[6] << 16) | (ip[7] << 24));
                    ip += 8;

                    SurtrRef subject = (SurtrRef)(*--sp);
                    if (subject != 0)
                    {
                        var subjectClass = ((SurtrObject)entities[subject]!).Class;
                        bool matches = target.Kind == SurtrMemberKind.Interface
                            ? subjectClass.Implements((SurtrInterface)target)
                            : subjectClass.IsSubclassOf((SurtrClass)target);

                        if (matches) ip += offset;
                    }

                    goto Branched;
                }

                case OpCode.Switch:
                {
                    // Offsets are measured from the opcode byte, which a variable-length
                    // instruction has no fixed "next address" to replace.
                    byte* instruction = ip - 1;
                    int low = ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24);
                    int count = ip[4] | (ip[5] << 8) | (ip[6] << 16) | (ip[7] << 24);

                    int index = (int)*--sp - low;
                    int target;
                    if ((uint)index < (uint)count)
                    {
                        // The jump table starts at ip + 12; each entry is one 4-byte offset.
                        byte* entry = ip + 12 + (index * 4);
                        target = entry[0] | (entry[1] << 8) | (entry[2] << 16) | (entry[3] << 24);
                    }
                    else
                    {
                        target = ip[8] | (ip[9] << 8) | (ip[10] << 16) | (ip[11] << 24);
                    }

                    ip = instruction + target;
                    goto Branched;
                }

                case OpCode.SwitchLookup:
                {
                    byte* instruction = ip - 1;
                    int count = ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24);
                    int target = ip[4] | (ip[5] << 8) | (ip[6] << 16) | (ip[7] << 24);
                    byte* table = ip + 8;

                    int value = (int)*--sp;
                    int low = 0;
                    int high = count - 1;
                    while (low <= high)
                    {
                        int middle = (int)((uint)(low + high) >> 1);

                        // Each entry is a (key, offset) pair of 4-byte little-endian ints, so
                        // an entry is 8 bytes wide and the offset sits 4 bytes past the key.
                        byte* pair = table + (middle * 8);
                        int key = pair[0] | (pair[1] << 8) | (pair[2] << 16) | (pair[3] << 24);

                        if (key == value)
                        {
                            target = pair[4] | (pair[5] << 8) | (pair[6] << 16) | (pair[7] << 24);
                            break;
                        }

                        if (key < value) low = middle + 1;
                        else high = middle - 1;
                    }

                    ip = instruction + target;
                    goto Branched;
                }
                #endregion

                #region Call Operations
                case OpCode.CallLocalModule:
                    pendingMethod = methodTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    pendingArguments = *ip++;
                    pendingResults = *ip++;
                    pendingClosure = null;
                    goto InvokeResolved;

                case OpCode.CallLocalModuleX:
                    pendingMethod = methodTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                    ip += 4;
                    pendingArguments = *ip++;
                    pendingResults = *ip++;
                    pendingClosure = null;
                    goto InvokeResolved;

                case OpCode.CallModule:
                {
                    var target = moduleTable[(ip[0] | (ip[1] << 8))];
                    pendingMethod = target.Chunk.MethodTable[(ip[2] | (ip[3] << 8))];
                    ip += 4;
                    pendingArguments = *ip++;
                    pendingResults = *ip++;
                    pendingClosure = null;
                    goto InvokeResolved;
                }

                case OpCode.CallModuleX:
                {
                    var target = moduleTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                    pendingMethod = target.Chunk.MethodTable[(ip[4] | (ip[5] << 8) | (ip[6] << 16) | (ip[7] << 24))];
                    ip += 8;
                    pendingArguments = *ip++;
                    pendingResults = *ip++;
                    pendingClosure = null;
                    goto InvokeResolved;
                }

                case OpCode.CallGlobalNative:
                {
                    var function = nativeFunctionTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    int argumentCount = *ip++;
                    int resultCount = *ip++;

                    SurtrRawValue* argumentBase = sp - argumentCount;

                    // Published before the transfer, so a collection triggered inside host code sees
                    // the arguments as live and a re-entrant call knows where the stack really is.
                    _sp = sp;
                    current.IP = ip;

                    SurtrValue nativeResult = function.EntryPoint
                        .Invoke(new SurtrCallArguments(runtime, argumentBase, argumentCount));

                    sp = argumentBase;
                    if (resultCount != 0) *sp++ = nativeResult.Raw;
                    _sp = sp;

                    entities = context.EntityRegistry.Entities;
                    globals = context.Globals.VariableTable.Pointer;
                    goto Dispatch;
                }

                case OpCode.CallGlobalNativeX:
                {
                    var function = nativeFunctionTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                    ip += 4;
                    int argumentCount = *ip++;
                    int resultCount = *ip++;

                    SurtrRawValue* argumentBase = sp - argumentCount;

                    _sp = sp;
                    current.IP = ip;

                    SurtrValue nativeResult = function.EntryPoint
                        .Invoke(new SurtrCallArguments(runtime, argumentBase, argumentCount));

                    sp = argumentBase;
                    if (resultCount != 0) *sp++ = nativeResult.Raw;
                    _sp = sp;

                    entities = context.EntityRegistry.Entities;
                    globals = context.Globals.VariableTable.Pointer;
                    goto Dispatch;
                }
                #endregion

                #region Method Operations
                case OpCode.InvokeVirtual:
                {
                    var declared = methodTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    pendingArguments = *ip++;
                    pendingResults = *ip++;

                    // The receiver is argument 0, which is what makes the frame base one subtraction
                    // regardless of whether a call has a receiver at all.
                    var receiver = (SurtrObject)entities[(SurtrRef)(*(sp - pendingArguments))]!;
                    pendingMethod = receiver.Class.VirtualMethods[declared.VTableSlot];
                    pendingClosure = null;
                    goto InvokeResolved;
                }

                case OpCode.InvokeSpecial:
                    pendingMethod = methodTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    pendingArguments = *ip++;
                    pendingResults = *ip++;
                    pendingClosure = null;
                    goto InvokeResolved;

                case OpCode.InvokeStatic:
                    pendingMethod = methodTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    pendingArguments = *ip++;
                    pendingResults = *ip++;
                    pendingClosure = null;
                    goto InvokeResolved;

                case OpCode.InvokeStaticX:
                    pendingMethod = methodTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                    ip += 4;
                    pendingArguments = *ip++;
                    pendingResults = *ip++;
                    pendingClosure = null;
                    goto InvokeResolved;

                case OpCode.InvokeInterface:
                {
                    var declared = methodTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    pendingArguments = *ip++;
                    pendingResults = *ip++;

                    var receiverClass = ((SurtrObject)entities[(SurtrRef)(*(sp - pendingArguments))]!).Class;
                    var contract = (SurtrInterface)declared.DeclaringType!.ResolvedType!;

                    // Which block of the receiver's dispatch table this contract owns. Written out
                    // rather than calling SurtrClass.IndexOfInterface, which would be a real call
                    // from a method this size - the two have to stay in step.
                    int contractId = contract.InterfaceId;
                    int indexMask = receiverClass.InterfaceIndexMask;
                    int probe = contractId & indexMask;

                    while (receiverClass.InterfaceIndexById[probe << 1] != contractId)
                        probe = (probe + 1) & indexMask;

                    int contractIndex = receiverClass.InterfaceIndexById[(probe << 1) + 1];

                    // One extra indirection over a virtual call: the interface's block in the
                    // class's dispatch table maps the contract's slot onto a vtable index, so an
                    // override reached through the vtable applies here for free.
                    int vtableSlot = receiverClass.InterfaceMethodSlots[
                        receiverClass.InterfaceSlotOffsets[contractIndex] + declared.VTableSlot];

                    pendingMethod = receiverClass.VirtualMethods[vtableSlot];
                    pendingClosure = null;
                    goto InvokeResolved;
                }

                case OpCode.InvokeClosure:
                {
                    int argumentCount = *ip++;
                    pendingResults = *ip++;

                    SurtrRawValue* target = sp - argumentCount - 1;
                    var invoked = (SurtrClosure)entities[(SurtrRef)(*target)]!;

                    // The closure sits one slot below its arguments, and the frame it is about to
                    // enter has to start at argument 0. Sliding the arguments down over it is one
                    // move per argument; the alternative - keeping the slot and fixing the stack up
                    // on return - would pay on every return path instead of here. The closure stays
                    // rooted through the frame's entry in _roots, which is why dropping the only
                    // stack reference to it is safe.
                    for (int i = 0; i < argumentCount; i++)
                        target[i] = target[i + 1];

                    sp--;

                    pendingMethod = invoked.Method;
                    pendingClosure = invoked;
                    pendingArguments = argumentCount;
                    goto InvokeResolved;
                }
                #endregion

                #region Exception Operations
                case OpCode.Throw:
                {
                    current.IP = ip;
                    SurtrRef raised = (SurtrRef)(*--sp);
                    _sp = sp;

                    // No CLR exception while a handler is in reach: the search either lands on one
                    // in this run, or the throw leaves as an exception for the run below.
                    if (TryEnterHandler(raised, entryDepth))
                        goto LoadFrame;

                    throw Uncaught(raised, entities);
                }
                #endregion

                #region Return Operations
                case OpCode.ReturnVoid:
                {
                    int depth = _frameCount - 1;
                    ref SurtrCallFrame finished = ref frames[depth];

                    sp = finished.Base;
                    int expected = finished.ExpectedResults;

                    // A dead frame must not keep its chunk, method or closure alive.
                    finished.Chunk = null;
                    finished.Method = null;
                    finished.Closure = null;
                    roots[depth + 1] = 0;
                    _frameCount = depth;

                    if (depth == entryDepth)
                    {
                        _sp = sp;
                        if (budgeted) _stepsRemaining = steps;
                        return SurtrValue.Null;
                    }

                    if (expected != 0) *sp++ = SurtrValue.TagMaskReference;
                    _sp = sp;
                    goto LoadFrame;
                }

                case OpCode.ReturnValue:
                {
                    SurtrRawValue result = *(sp - 1);
                    int depth = _frameCount - 1;
                    ref SurtrCallFrame finished = ref frames[depth];

                    sp = finished.Base;
                    int expected = finished.ExpectedResults;

                    finished.Chunk = null;
                    finished.Method = null;
                    finished.Closure = null;
                    roots[depth + 1] = 0;
                    _frameCount = depth;

                    if (depth == entryDepth)
                    {
                        _sp = sp;
                        if (budgeted) _stepsRemaining = steps;
                        return SurtrValue.FromRaw(result);
                    }

                    if (expected != 0) *sp++ = result;
                    _sp = sp;
                    goto LoadFrame;
                }
                #endregion

                #region Nullable Primitive Operations
                case OpCode.PushAbsent:
                {
                    // The immediate says which primitive is missing. Nothing on this path reads it
                    // back - the compiler knows the declared type statically - but a native
                    // function handed the value, or a diagnostic printing it, can.
                    *sp++ = SurtrValue.TagMaskAbsent | *ip++;
                    goto Dispatch;
                }

                case OpCode.IsAbsent:
                    *(sp - 1) = SurtrValue.TagMaskBool | (((*(sp - 1)) & SurtrValue.TagMask) == SurtrValue.TagMaskAbsent ? 1UL : 0UL);
                    goto Dispatch;

                case OpCode.IsPresent:
                    *(sp - 1) = SurtrValue.TagMaskBool | (((*(sp - 1)) & SurtrValue.TagMask) == SurtrValue.TagMaskAbsent ? 0UL : 1UL);
                    goto Dispatch;

                case OpCode.JPA:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp--;
                    if ((*sp & SurtrValue.TagMask) == SurtrValue.TagMaskAbsent)
                        ip += offset;
                    goto Branched;
                }

                case OpCode.JPAX:
                {
                    int offset = ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24);
                    ip += 4;
                    sp--;
                    if ((*sp & SurtrValue.TagMask) == SurtrValue.TagMaskAbsent)
                        ip += offset;
                    goto Branched;
                }

                case OpCode.JPNA:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp--;
                    if ((*sp & SurtrValue.TagMask) != SurtrValue.TagMaskAbsent)
                        ip += offset;
                    goto Branched;
                }

                case OpCode.JPNAX:
                {
                    int offset = ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24);
                    ip += 4;
                    sp--;
                    if ((*sp & SurtrValue.TagMask) != SurtrValue.TagMaskAbsent)
                        ip += offset;
                    goto Branched;
                }
                #endregion

                #region Value Class Operations
                case OpCode.BoxAs:
                {
                    var declared = typeTable[(ip[0] | (ip[1] << 8))].ResolvedClass!;
                    ip += 2;
                    current.IP = ip;
                    _sp = sp;

                    var boxed = new SurtrBoxed(declared, SurtrValue.FromRaw(*(sp - 1)));
                    SurtrRef reference = context.EntityRegistry.Register(boxed);
                    entities = context.EntityRegistry.Entities;

                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.BoxAsX:
                {
                    var declared = typeTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].ResolvedClass!;
                    ip += 4;
                    current.IP = ip;
                    _sp = sp;

                    var boxed = new SurtrBoxed(declared, SurtrValue.FromRaw(*(sp - 1)));
                    SurtrRef reference = context.EntityRegistry.Register(boxed);
                    entities = context.EntityRegistry.Entities;

                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }
                #endregion

                #region Range Operations
                case OpCode.RangeNew:
                {
                    current.IP = ip;
                    _sp = sp;

                    sp--;
                    var range = new SurtrRange((int)*(sp - 1), (int)*sp, inclusive: false);
                    SurtrRef reference = context.EntityRegistry.Register(range);
                    entities = context.EntityRegistry.Entities;

                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }

                case OpCode.RangeNewInclusive:
                {
                    current.IP = ip;
                    _sp = sp;

                    sp--;
                    var range = new SurtrRange((int)*(sp - 1), (int)*sp, inclusive: true);
                    SurtrRef reference = context.EntityRegistry.Register(range);
                    entities = context.EntityRegistry.Entities;

                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Dispatch;
                }
                #endregion

                default:
                    current.IP = ip;
                    _sp = sp;
                    throw InvalidOpCode(*(ip - 1));
            }

        // ---- Shared call sequences ------------------------------------------------------------
        // Reached by goto, never by a call: the operands arrive in the pending* locals, so every
        // call opcode shares one copy of this without paying a call's prologue, epilogue or
        // register spills. The branch on ImplKind is why there is no separate opcode for calling
        // host code - a virtual call can land on a native override, so the test has to exist here
        // regardless, and it predicts perfectly at any one call site.

        InvokeResolved:
            if (pendingMethod.ImplKind == SurtrMethodImplKind.Native)
            {
                SurtrRawValue* nativeArgumentBase = sp - pendingArguments;

                _sp = sp;
                current.IP = ip;

                SurtrValue resolvedResult = pendingClosure is null
                    ? ((SurtrNativeMethodInfo)pendingMethod).EntryPoint
                        .Invoke(new SurtrCallArguments(runtime, nativeArgumentBase, pendingArguments))
                    : pendingClosure.EntryPoint
                        .Invoke(new SurtrCallArguments(runtime, nativeArgumentBase, pendingArguments));

                sp = nativeArgumentBase;
                if (pendingResults != 0) *sp++ = resolvedResult.Raw;
                _sp = sp;

                entities = context.EntityRegistry.Entities;
                globals = context.Globals.VariableTable.Pointer;
                goto Dispatch;
            }

            {
                var target = (SurtrBytecodeMethodInfo)pendingMethod;

                int depth = _frameCount;
                if (depth == maxDepth)
                {
                    current.IP = ip;
                    _sp = sp;
                    throw CallStackOverflow(maxDepth);
                }

                SurtrRawValue* newBase = sp - pendingArguments;
                int localCount = target.LocalCount;

                // The only stack-overflow check in the whole interpreter: the callee's own high
                // water mark is known at compile time, so nothing has to be checked per push.
                if (newBase + localCount + target.MaxStackSize > stackLimit)
                {
                    current.IP = ip;
                    _sp = sp;
                    throw DataStackOverflow();
                }

                // Locals above the incoming arguments are zeroed, so a collection can never read a
                // slot the program has not written and retain whatever the last call left there.
                if (localCount > pendingArguments)
                    MemOps.Clear(newBase + pendingArguments, (nuint)(localCount - pendingArguments) * sizeof(SurtrRawValue));

                current.IP = ip;

                var targetChunk = target.Chunk;
                byte* targetCodeBase = targetChunk.Code.Pointer;

                ref SurtrCallFrame entered = ref frames[depth];
                entered.Base = newBase;
                entered.CodeBase = targetCodeBase;
                entered.IP = targetCodeBase + target.CodeOffset;
                entered.Chunk = targetChunk;
                entered.Method = target;
                entered.Closure = pendingClosure;
                entered.LocalCount = localCount;
                entered.ArgumentCount = pendingArguments;
                entered.ExpectedResults = pendingResults;

                roots[depth + 1] = pendingClosure is null
                    ? 0
                    : SurtrValue.TagMaskReference | (uint)pendingClosure.GetSurtrReference();

                _frameCount = depth + 1;

                _sp = newBase + localCount;
                goto LoadFrame;
            }
        }

        /// <summary>
        /// The correctly tagged zero for a type family, or <c>0</c> where a zeroed slot is already
        /// right.
        /// </summary>
        /// <remarks>
        /// Floats and references need nothing: <c>0.0</c> is all-zero bits, and the interpreter
        /// reads a reference from its payload, so an untagged zero already means null. Integers,
        /// booleans and characters read correctly from a zeroed slot too, but their <em>tag</em>
        /// would be wrong - which a native function or a box would notice - so those are filled in.
        /// </remarks>
        private static SurtrRawValue ZeroOf(SurtrValueTypeCode elementType) => elementType switch
        {
            SurtrValueTypeCode.Integer => SurtrValue.TagMaskInt,
            SurtrValueTypeCode.Boolean => SurtrValue.TagMaskBool,
            SurtrValueTypeCode.Character => SurtrValue.TagMaskChar,
            _ => 0,
        };
        #endregion

        #region Traps
        // Every one of these is off the hot path by construction: the interpreter only branches here
        // on failure, and NoInlining keeps their bodies - message formatting and all - out of the
        // dispatch loop's register allocation. They are raised as CLR exceptions rather than
        // unwound directly, so a trap and a host exception reach Surtr's handler tables by exactly
        // one path.

        [MethodImpl(MethodImplOptions.NoInlining)]
        // Each trap names the library class it surfaces as. The pairing lives here, beside the
        // condition, rather than at the catch site: the validation policy fixes which conditions
        // trap at all, and every one of them is exactly one of these classes.
        private static SurtrExecutionException IndexOutOfRange(int index, int length, string kind)
            => new SurtrExecutionException($"Index {index} is outside the {kind}, which holds {length} element(s).", SurtrBuiltIns.IndexOutOfRangeException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException EmptyArray()
            => new SurtrExecutionException("Cannot remove the last element of an empty array.", SurtrBuiltIns.InvalidOperationException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException IntegerDivision(int left, int right)
            => new SurtrExecutionException(right == 0
                ? "Integer division by zero."
                : $"Integer division of {left} by {right} has no representable result.", SurtrBuiltIns.DivideByZeroException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException NegativeExponent(int exponent)
            => new SurtrExecutionException($"Integer exponentiation needs a non-negative exponent, but was given {exponent}.", SurtrBuiltIns.ArgumentException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException MissingKey()
            => new SurtrExecutionException("The dictionary holds no entry under that key.", SurtrBuiltIns.KeyNotFoundException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException InvalidCast(string fromName, string toName)
            => new SurtrExecutionException($"A '{fromName}' cannot be cast to '{toName}'.", SurtrBuiltIns.InvalidCastException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException InvalidOpCode(byte opCode)
            => new SurtrExecutionException($"0x{opCode:X2} is not a valid opcode.", SurtrBuiltIns.InvalidOperationException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException CallStackOverflow(int maxDepth)
            => new SurtrExecutionException($"Call stack overflow: more than {maxDepth} nested calls.", SurtrBuiltIns.StackOverflowException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrBudgetExceededException StepBudgetExceeded()
            => new SurtrBudgetExceededException("Execution exceeded its instruction budget.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException DataStackOverflow()
            => new SurtrExecutionException("Data stack overflow: the machine's value stack is full.", SurtrBuiltIns.StackOverflowException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrThrownException Uncaught(SurtrRef raised, SurtrRuntimeEntity?[] entities)
        {
            var raisedObject = raised == 0 ? null : entities[raised] as SurtrObject;
            return new SurtrThrownException(raised, raisedObject is null ? "null" : raisedObject.Class.Name);
        }
        #endregion

        /// <summary>Releases the machine's data stack.</summary>
        public void Dispose()
        {
            ReleaseResources();
            GC.SuppressFinalize(this);
        }

        private void ReleaseResources()
        {
            if (_disposed)
                return;

            _disposed = true;

            MemOps.Free(_stack);
            _stack = null;
            _stackLimit = null;
            _sp = null;
            _frameCount = 0;
        }
    }
}
