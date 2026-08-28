#nullable enable

using Surtr.Bytecode;
using Surtr.Runtime;
using Surtr.Runtime.BuiltIns;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using Surtr.Runtime.Utilities;
using System;
using System.Buffers;
using System.Collections.Generic;
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

        /// <summary>The operands of the shared call and generator-entry sequences, delivered as
        /// fields rather than locals so the dispatch loop does not hold six live ranges across the
        /// whole method for values written and read only on the call/generator paths (cold, once
        /// per call). They are copied back into short-lived locals at the top of the shared
        /// sequences, which is what keeps them safe from a native call that re-enters Run().</summary>
        private SurtrMethodInfo _pendingMethod = null!;
        private SurtrClosure? _pendingClosure = null;
        private int _pendingArguments = 0;
        private int _pendingResults = 0;
        private SurtrGenerator _pendingGenerator = null!;

        /// <summary>The operand of the four native-field sequences, on the same bargain as the
        /// call operands above: a field access resolves it on a branch it almost never takes, so
        /// handing it over in a field costs one store on the cold path and keeps the four hot
        /// field opcodes down to their own load and store.</summary>
        private SurtrNativeFieldInfo _pendingField = null!;
        private bool _disposed;

        /// <summary>Creates a machine with the default stack sizes.</summary>
        internal SurtrVirtualMachine(SurtrRuntime runtime)
            : this(runtime, DefaultDataStackSlots, DefaultCallDepth) { }

        /// <summary>Creates a machine with explicit stack sizes.</summary>
        /// <param name="runtime">The runtime whose heap and modules this machine executes against.</param>
        /// <param name="dataStackSlots">How many value slots the data stack holds. Never grows.</param>
        /// <param name="maxCallDepth">How many nested calls are allowed before the call stack traps.</param>
        internal SurtrVirtualMachine(SurtrRuntime runtime, int dataStackSlots, int maxCallDepth)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _comparer = _runtime.ValueComparer;

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
        /// <returns>
        /// The single-slot result for a bytecode method, or <see cref="SurtrValue.Null"/> for a
        /// native one - a native answers in place, leaving its slots on the data stack above its
        /// argument base, so anything that needs the value reads it there
        /// (<see cref="CallForResults"/>, or the host boundary).
        /// </returns>
        internal SurtrValue Call(SurtrMethodInfo method, int argumentCount)
        {
            if (method is null)
                throw new ArgumentNullException(nameof(method));

            if (method.ImplKind == SurtrMethodImplKind.Native)
            {
                SurtrRawValue* argumentBase = _sp - argumentCount;
                int results = ((SurtrNativeMethodInfo)method).EntryPoint
                    .Invoke(new SurtrCallArguments(_runtime, argumentBase, argumentCount, (int)(_stackLimit - argumentBase)));

                _sp = argumentBase + results;
                return SurtrValue.Null;
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
                // Same in-place answer as every native: the results land over the arguments and
                // the stack pointer moves to their end.
                SurtrRawValue* argumentBase = _sp - argumentCount;
                int results = closure.EntryPoint
                    .Invoke(new SurtrCallArguments(_runtime, argumentBase, argumentCount, (int)(_stackLimit - argumentBase)));

                _sp = argumentBase + results;
                return SurtrValue.Null;
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

        /// <summary>
        /// Calls <paramref name="method"/> and copies its result slots into
        /// <paramref name="destination"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The host-facing shape of a multi-slot return. A single-value method is answered the
        /// ordinary way - one slot in <paramref name="destination"/> - while a method that ended
        /// through <see cref="OpCode.ReturnValues"/> leaves its block on the data stack at the
        /// entry frame's base, which this reads, copies out and releases.
        /// </para>
        /// <para>
        /// How many slots came back is read off the stack pointer rather than out of shared state:
        /// a run ending in <see cref="OpCode.ReturnValues"/> leaves <c>sp</c> exactly one result
        /// block above the frame base, and every other return leaves it at the frame base. Both
        /// facts survive nesting untouched - an inner re-entrant run unwinds completely before the
        /// outer one resumes - so no per-run bookkeeping is needed.
        /// </para>
        /// </remarks>
        /// <param name="method">The bytecode or native method to call.</param>
        /// <param name="arguments">
        /// The arguments to push, receiver included for an instance method.
        /// </param>
        /// <param name="destination">
        /// Receives the result slots. Sized by the caller from the callee's declared result width;
        /// never more than that width is written.
        /// </param>
        /// <returns>How many slots were written.</returns>
        internal int CallForResults(SurtrMethodInfo method, ReadOnlySpan<SurtrValue> arguments, Span<SurtrRawValue> destination)
        {
            if (method is null)
                throw new ArgumentNullException(nameof(method));

            for (int i = 0; i < arguments.Length; i++)
                Push(arguments[i]);

            SurtrRawValue* resultBase = _sp - arguments.Length;
            int entryDepth = _frameCount;

            SurtrValue single = Call(method, arguments.Length);

            // A single-slot result came back as the ordinary return value with the stack pointer
            // back at the frame base - true for a native call as well, whose wrapper resets sp
            // over its arguments before answering.
            if (_sp == resultBase)
            {
                if (destination.Length > 0)
                    destination[0] = single.Raw;

                return 1;
            }

            int slotCount = (int)(_sp - resultBase);
            if (slotCount > destination.Length)
                slotCount = destination.Length;

            for (int i = 0; i < slotCount; i++)
                destination[i] = resultBase[i];

            _sp = resultBase;
            return slotCount;
        }

        /// <summary>
        /// The closure twin of <see cref="CallForResults"/>: calls and copies every result slot out.
        /// </summary>
        internal int CallClosureForResults(SurtrClosure closure, ReadOnlySpan<SurtrValue> arguments, Span<SurtrRawValue> destination)
        {
            if (closure is null)
                throw new ArgumentNullException(nameof(closure));

            for (int i = 0; i < arguments.Length; i++)
                Push(arguments[i]);

            SurtrRawValue* resultBase = _sp - arguments.Length;

            SurtrValue single = CallClosure(closure, arguments.Length);

            // A single-slot answer comes back as the ordinary return value with the stack pointer
            // restored to the argument base - true for a native closure too, whose in-place write
            // of zero results leaves the pointer exactly there.
            if (_sp == resultBase)
            {
                if (destination.Length > 0)
                    destination[0] = single.Raw;

                return 1;
            }

            // Anything above the base is an inline block: read it and release it.
            int slotCount = (int)(_sp - resultBase);
            if (slotCount > destination.Length)
                slotCount = destination.Length;

            for (int i = 0; i < slotCount; i++)
                destination[i] = resultBase[i];

            _sp = resultBase;
            return slotCount;
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
                _frames[i].Generator = null;
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

            // Same fast path as the interpreter's call-entry sequence: ≤16 bytes inline, larger
            // frames through the vectorised Clear.
            if (localCount > argumentCount)
            {
                SurtrRawValue* firstLocal = frameBase + argumentCount;
                int zeroSlots = localCount - argumentCount;
                if (zeroSlots <= 2)
                {
                    firstLocal[0] = 0;
                    if (zeroSlots == 2)
                        firstLocal[1] = 0;
                }
                else
                {
                    MemOps.Clear(firstLocal, (nuint)zeroSlots * sizeof(SurtrRawValue));
                }
            }

            var chunk = method.Chunk;
            byte* codeBase = chunk.Code.Pointer;

            ref SurtrCallFrame frame = ref _frames[depth];
            frame.Base = frameBase;
            frame.CodeBase = codeBase;
            frame.IP = codeBase + method.CodeOffset;
            frame.Chunk = chunk;
            frame.Method = method;
            frame.Closure = closure;
            frame.Generator = null;
            frame.LocalCount = localCount;
            frame.ArgumentCount = argumentCount;
            frame.ExpectedResults = 1;

            _roots[depth + 1] = closure is null
                ? 0
                : SurtrValue.TagMaskReference | (uint)closure.GetSurtrReference();

            _frameCount = depth + 1;
            _sp = frameBase + localCount;
        }

        /// <summary>
        /// Runs a generator until its next <c>yield</c> or its end, from outside the dispatch loop.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The general path, reached when a generator travels as an <c>IIterable&lt;T&gt;</c> and
        /// its <c>moveNext</c> is called through the contract - the native accessor lands here.
        /// The compiled fast path does the same work with <c>GenResume</c>, without a native call
        /// or a nested run; the two agree because both end in the same <c>Yield</c>.
        /// </para>
        /// <para>
        /// Re-entrancy is what makes this legal at all: the caller published <c>sp</c> and the
        /// executing frame's <c>IP</c> before entering host code, so pushing the generator's frame
        /// above them and running to its own depth leaves everything below untouched.
        /// </para>
        /// </remarks>
        /// <returns><see langword="true"/> if the body yielded a value; <see langword="false"/> if it finished.</returns>
        internal bool ResumeGenerator(SurtrGenerator generator)
            => Advance(generator, SurtrValue.Null);

        /// <summary>
        /// Resumes a generator with a value, which its suspended <c>yield</c> evaluates to (§3.7).
        /// </summary>
        /// <remarks>
        /// The whole of <c>send</c> beyond one write: injecting is resuming, with the value parked
        /// where <c>GenResumed</c> will read it. It goes to the <em>innermost</em> generator of a
        /// delegation for the same reason a resume does - that is the one with a frame, and the one
        /// whose <c>yield</c> is actually suspended.
        /// </remarks>
        /// <returns><see langword="true"/> if the body yielded again; <see langword="false"/> if it finished.</returns>
        internal bool SendToGenerator(SurtrGenerator generator, SurtrValue value)
        {
            var innermost = generator.Innermost;

            // A generator that has not started has no suspended `yield` to hand this to, so the
            // value would simply vanish. Python refuses the same call for the same reason; refusing
            // is what turns a silent loss into a legible mistake, exactly as §12.2 did for walking
            // an already-started generator.
            if (innermost.State == SurtrGeneratorState.NotStarted)
                throw GeneratorNotStarted();

            return Advance(generator, value);
        }

        /// <summary>
        /// Raises an exception inside a generator at the point it is suspended (§3.7).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The frame is rebuilt exactly as a resume rebuilds it, and then, instead of running, the
        /// handler search is offered the exception against that frame's own saved instruction
        /// pointer. That pointer sits inside whatever <c>try</c> the <c>yield</c> was written in, so
        /// a <c>catch</c> or a <c>finally</c> around the suspension point sees the raise - which is
        /// the whole reason §3.7 can now allow a <c>yield</c> inside a <c>try</c> at all.
        /// </para>
        /// <para>
        /// Nothing about this is a new unwinding mechanism: <see cref="TryEnterHandler"/> already
        /// walks frames, marks a generator whose body died as exhausted, and stops at the depth it
        /// was given.
        /// </para>
        /// </remarks>
        /// <returns><see langword="true"/> if the body caught it and yielded again.</returns>
        internal bool RaiseInGenerator(SurtrGenerator generator, SurtrRef exception)
        {
            var innermost = generator.Innermost;

            if (innermost.State == SurtrGeneratorState.Running)
                throw GeneratorAlreadyRunning();

            // Nothing left to raise into: a generator that never started has no suspended point and
            // an exhausted one has no frame, so the exception belongs to whoever asked for the
            // raise. Finishing first is what stops a not-started generator from later running its
            // body as though nothing had happened to it.
            if (innermost.State != SurtrGeneratorState.Suspended)
            {
                innermost.Finish();
                throw Uncaught(exception, _runtime.Context.EntityRegistry.Entities);
            }

            innermost.Resumed = SurtrValue.Null;

            int depth = PushGeneratorFrame(innermost, out SurtrRawValue* frameBase);

            try
            {
                // Offered against the frame that was just rebuilt, and only against it: the depth
                // bounds the search, so an exception nothing in the body catches leaves as a CLR
                // exception and resumes its travel through whoever called in.
                if (!TryEnterHandler(exception, depth))
                {
                    _sp = frameBase - 1;
                    throw Uncaught(exception, _runtime.Context.EntityRegistry.Entities);
                }

                Execute(depth);
            }
            catch
            {
                _sp = frameBase - 1;
                throw;
            }

            bool produced = (*(frameBase - 1) & 1UL) != 0;
            _sp = frameBase - 1;
            return produced;
        }

        /// <summary>
        /// Ends a generator from outside, running the <c>finally</c> blocks its body has pending
        /// (<c>docs/Plan-Disposicion.md</c> §3.5).
        /// </summary>
        /// <remarks>
        /// <para>
        /// What <c>dispose()</c> is. A suspended body is unwound by raising
        /// <see cref="SurtrBuiltIns.GeneratorExit"/> inside it - a class no typed <c>catch</c> ever
        /// matches, so only a <c>finally</c> sees it and no <c>catch (e: Exception)</c> in the body
        /// can swallow the close. The exit travelling back out is success rather than failure, so it
        /// is swallowed here; anything else the body raises on its way out is a real exception and
        /// travels on.
        /// </para>
        /// <para>
        /// A delegation is closed innermost-first, and every level is closed on its own frame. A
        /// delegating generator is suspended <em>with</em> a frame - <c>GenDelegate</c> copies one
        /// out, and it is only resumes that walk past it - so its own <c>finally</c> around a
        /// <c>yield from</c> has to run too, and would be lost if the chain were simply marked
        /// exhausted from the innermost end.
        /// </para>
        /// <para>
        /// Idempotent, as the contract requires: closing an exhausted generator does nothing, and
        /// closing one that never started only marks it.
        /// </para>
        /// </remarks>
        internal void DisposeGenerator(SurtrGenerator generator)
        {
            if (generator.State == SurtrGeneratorState.Running)
                throw GeneratorAlreadyRunning();

            // The overwhelmingly common shape - a generator delegating to nothing - closes without
            // building anything. Every `for-in` over a generator now ends in a close, so the
            // no-delegation path is worth keeping free of a list nobody reads twice.
            if (generator.Delegate is null)
            {
                CloseOne(generator);
                return;
            }

            // Snapshotted before anything is closed, because closing a level cuts the links this
            // walk would otherwise follow.
            var chain = new List<SurtrGenerator>(4);
            for (var level = generator; level is not null; level = level.Delegate)
            {
                if (level.State == SurtrGeneratorState.Running)
                    throw GeneratorAlreadyRunning();

                chain.Add(level);
            }

            for (int i = chain.Count - 1; i >= 0; i--)
                CloseOne(chain[i]);
        }

        /// <summary>Unwinds one generator of a delegation chain, running its own pending blocks.</summary>
        private void CloseOne(SurtrGenerator generator)
        {
            if (generator.State != SurtrGeneratorState.Suspended)
            {
                // Never started, or already over. Marking is all a close owes it - and it is worth
                // marking, because a not-started generator that was disposed must not go on to run
                // its body on the strength of never having been touched.
                if (generator.State == SurtrGeneratorState.NotStarted)
                    generator.FinishAndDetach();

                return;
            }

            // Cut loose from the chain first: the levels are closed one at a time on their own
            // frames, so a link left standing would send this raise straight past the very
            // generator it is meant to unwind.
            generator.Delegate = null;
            generator.DelegatedBy = null;
            generator.Resumed = SurtrValue.Null;

            // A body with nothing protecting its suspension point has nothing to run, so there is
            // no reason to build the frame or the exit object for it. This is what keeps a `break`
            // out of an ordinary generator free of an allocation: only a generator that actually
            // wrote a `try` around its `yield` pays for being closed.
            if (!HasHandlerAt(generator))
            {
                generator.Finish();
                return;
            }

            var exit = _runtime.NewException(SurtrBuiltIns.GeneratorExit, "The generator was disposed.");
            SurtrRef exitReference = exit.GetSurtrReference();

            int depth = PushGeneratorFrame(generator, out SurtrRawValue* frameBase);

            bool produced;

            try
            {
                if (!TryEnterHandler(exitReference, depth))
                {
                    // Nothing in the body was protecting the suspension point, so there was nothing
                    // to run and the close is already complete.
                    _sp = frameBase - 1;
                    generator.Finish();
                    return;
                }

                Execute(depth);
                produced = (*(frameBase - 1) & 1UL) != 0;
                _sp = frameBase - 1;
            }
            catch (SurtrThrownException thrown) when (thrown.Reference == exitReference)
            {
                // The exit came back out, which is the ordinary outcome: a `finally` ran and
                // re-raised it, and it found nothing else. That is the close succeeding, so it
                // stops here rather than reaching whoever called dispose().
                _sp = frameBase - 1;
                generator.Finish();
                return;
            }
            catch
            {
                _sp = frameBase - 1;
                throw;
            }

            // A body that answered a close with another element caught the exit and carried on,
            // which leaves a generator alive after something was told it was closed. Python refuses
            // the same shape, and for the same reason: the alternative is a resource whose release
            // silently did not happen.
            if (produced)
                throw GeneratorIgnoredClose();

            generator.Finish();
        }

        /// <summary>Whether anything in the body protects the point the generator is suspended at.</summary>
        /// <remarks>
        /// The same test <see cref="TryEnterHandler"/> would make, asked before a frame is built
        /// rather than after: closing a generator whose suspension is inside no <c>try</c> has
        /// nothing to run, and answering that here is what keeps the ordinary case - a <c>break</c>
        /// out of a generator that wrote no <c>try</c> at all - free of both a frame entry and an
        /// exception object.
        /// </remarks>
        private static bool HasHandlerAt(SurtrGenerator generator)
        {
            if (generator.Method is not SurtrBytecodeMethodInfo bytecode)
                return false;

            var handlers = bytecode.Handlers;
            for (int i = 0; i < handlers.Length; i++)
            {
                if (handlers[i].Covers(generator.ResumeOffset))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Runs a suspended generator to its next <c>yield</c> or its end, with a value to hand it.
        /// </summary>
        /// <remarks>
        /// The shared body of <see cref="ResumeGenerator"/> and <see cref="SendToGenerator"/>: the
        /// two differ only in what is parked in <c>Resumed</c>, which is why an ordinary resume
        /// parks null rather than leaving whatever was there.
        /// </remarks>
        private bool Advance(SurtrGenerator generator, SurtrValue resumed)
        {
            // Straight to the innermost generator that still has a frame, exactly as GenResume
            // does: a delegating generator is suspended without one.
            while (generator.Delegate is { } delegated)
                generator = delegated;

            if (generator.State == SurtrGeneratorState.Exhausted)
                return false;

            if (generator.State == SurtrGeneratorState.Running)
                throw GeneratorAlreadyRunning();

            generator.Resumed = resumed;

            int depth = PushGeneratorFrame(generator, out SurtrRawValue* frameBase);

            try
            {
                Execute(depth);
            }
            catch
            {
                // The handler search already marked the generator exhausted as it discarded the
                // frame; what is left is the reserved slot, which nothing will pop now that the
                // exception is travelling out through the native call.
                _sp = frameBase - 1;
                throw;
            }

            // The body wrote its answer into the reserved slot on the way out. Reading the state
            // instead would say the same thing, but reading the slot keeps this path and the
            // compiled one answering from exactly the same write.
            bool produced = (*(frameBase - 1) & 1UL) != 0;
            _sp = frameBase - 1;
            return produced;
        }

        /// <summary>
        /// Rebuilds a suspended generator's frame on top of the stack, ready to be run into.
        /// </summary>
        /// <remarks>
        /// The native counterpart of the interpreter's <c>EnterGeneratorFrame</c>, shared by every
        /// way host code enters a body: a resume, a send, a raise and a close. The interpreter's
        /// <c>GenResume</c> leaves the generator's own stack slot below the frame as the place the
        /// answer goes; there is no such slot here, so one is pushed - both ways a body can leave
        /// write into it unconditionally, and reserving it is cheaper than teaching them which
        /// caller they are answering.
        /// </remarks>
        /// <returns>The frame depth the run should unwind back to.</returns>
        private int PushGeneratorFrame(SurtrGenerator generator, out SurtrRawValue* frameBase)
        {
            int depth = _frameCount;
            if (depth == _frames.Length)
                throw CallStackOverflow(_frames.Length);

            if (_sp >= _stackLimit)
                throw DataStackOverflow();

            *_sp++ = SurtrValue.TagMaskReference | (uint)generator.GetSurtrReference();

            SurtrRawValue* localBase = _sp;
            int localCount = generator.LocalCount;
            int liveSlots = generator.SlotCount;

            if (localBase + localCount + generator.MaxStackSize > _stackLimit)
                throw DataStackOverflow();

            var slots = generator.Slots;
            for (int i = 0; i < liveSlots; i++)
                localBase[i] = slots[i].Raw;

            for (int i = liveSlots; i < localCount; i++)
                localBase[i] = 0;

            var chunk = generator.Chunk;
            byte* codeBase = chunk.Code.Pointer;

            ref SurtrCallFrame frame = ref _frames[depth];
            frame.Base = localBase;
            frame.CodeBase = codeBase;
            frame.IP = codeBase + generator.ResumeOffset;
            frame.Chunk = chunk;
            frame.Method = generator.Method;
            frame.Closure = null;
            frame.Generator = generator;
            frame.LocalCount = localCount;
            frame.ArgumentCount = generator.ArgumentCount;
            frame.ExpectedResults = 0;

            _roots[depth + 1] = SurtrValue.TagMaskReference | (uint)generator.GetSurtrReference();

            generator.State = SurtrGeneratorState.Running;
            _frameCount = depth + 1;
            _sp = localBase + (liveSlots > localCount ? liveSlots : localCount);

            frameBase = localBase;
            return depth;
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

                // An exception leaving a generator's body ends that generator for good: its frame
                // is being discarded here, so there is nothing left to resume into. Marking it
                // exhausted is what makes the next `moveNext` answer false rather than trying to
                // resume from a frame that no longer describes anything.
                frame.Generator?.Finish();

                frame.Chunk = null;
                frame.Method = null;
                frame.Closure = null;
                frame.Generator = null;
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
        /// <remarks>
        /// One class is excluded from every typed handler: <c>GeneratorExit</c>, which
        /// <c>dispose()</c> raises inside a suspended generator to unwind it. A catch-all is the
        /// only handler that sees it, and the compiler emits a catch-all for exactly one construct -
        /// a <c>finally</c> - so closing a generator runs its <c>finally</c> blocks and cannot be
        /// swallowed by a <c>catch (e: Exception)</c> in the body. It is Python's
        /// <c>BaseException</c> rule expressed as a condition rather than as a second hierarchy
        /// root; see <c>SurtrBuiltIns.GeneratorExit</c>.
        /// </remarks>
        private static bool Catches(in SurtrExceptionHandler handler, SurtrClass? raisedClass)
        {
            var catchType = handler.CatchType;
            if (catchType is null)
                return true;

            if (raisedClass is null)
                return false;

            if (ReferenceEquals(raisedClass, SurtrBuiltIns.GeneratorExit))
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
            
            

            // `context` se mantiene como local a prop??sito: Context es un getter ref que el inliner
            // deja como llamada real en un m??todo de este tama??o (medido: 45 get_Context por Run),
            // as?? que re-leer _runtime.Context en cada sitio fr??o costaba una llamada por uso ??? y
            // `interop` paga el camino nativo en bucle. Un solo acceso en el pr??logo y punteros
            // despu??s. Los arrays de frames/roots y el l??mite de pila s?? se leen de los campos
            // (ver abajo): esos no pasan por ning??n getter.
            ref SurtrContext context = ref _runtime.Context;

            // The call/generator paths read the frame array, the roots array and the stack limit
            // straight off the instance fields. Holding them in locals made five live ranges span
            // the whole method and cost three callee-saved registers that the dispatch loop would
            // rather spend on `constants` and `entities`. They are cold paths (once per call); the
            // extra field load does not show up there, and the loop stops paying for the pressure.
            // Diagnosed in docs/Informe-Volatilidad-Run.md ??2.

            SurtrRawValue* sp;

            // Held in a local for the same reason ip and sp are: a field read per instruction
            // would defeat the point. long.MaxValue stands in for "no limit" so the check itself
            // is unconditional and the branch predicts perfectly either way.
            
            long steps = _stepsRemaining;
            if (steps == 0) steps = long.MaxValue;

            // Both of these can move: registering an entity may grow the registry's array, and a
            // native call may register one. Every site that can cause either reloads them, and
            // nothing else has to.
            var entities = context.EntityRegistry.Entities;

            // Per-frame state, reloaded at LoadFrame whenever the executing frame changes. `current`
            // is what makes publishing the instruction pointer a single store with no bounds check,
            // which is why it is worth publishing at every site that can raise.
            ref SurtrCallFrame current = ref _frames[0];
            byte* ip;
            SurtrRawValue* frameBase;
            SurtrChunk chunk;
            SurtrRawValue* constants;
            

            SurtrClosure? closure;

            // The operands of the shared call-entry sequences below. Passing them in locals and
            // jumping keeps every call opcode from carrying its own copy of a twenty-line frame
            // setup, without turning that setup into a real call.
            

            // The operand of the shared generator-entry sequence, which three sites reach: a
            // resume, a delegation, and a delegated-to body ending. All three enter a frame at
            // `sp` with the answer slot at `sp - 1`, so they share one copy of the setup.
            

        LoadFrame:
            {
                current = ref _frames[_frameCount - 1];
                ip = current.IP;
                frameBase = current.Base;
                chunk = current.Chunk!;
                closure = current.Closure;
                sp = _sp;

                constants = chunk.Constants.Pointer;
                
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
                #region Hot core - ninety per cent of dispatches
                // Ordered by measured dispatch heat, families kept adjacent so they share cache
                // lines. This is not a stylistic ordering. Run() carries AggressiveOptimization,
                // which keeps it out of tiered compilation and so out of dynamic PGO: the JIT has
                // no profile to lay blocks out with, and emits the case bodies in source order -
                // measured at Spearman 1.0 against the native addresses, zero inversions. The
                // order of these blocks *is* the code layout. Their real core is 1.8 KB; before
                // this ordering it was diluted 11.5x across 29 KB of method and six pages.
                // docs/Informe-Opcodes-Layout.md 1 and 3 carry the measurement and the protocol
                // for re-checking it after anything is moved here.

                case OpCode.Ldl2: *sp++ = frameBase[2]; goto Dispatch;
                case OpCode.Ldl1: *sp++ = frameBase[1]; goto Dispatch;
                case OpCode.Ldl0: *sp++ = frameBase[0]; goto Dispatch;
                case OpCode.Ldl3: *sp++ = frameBase[3]; goto Dispatch;
                case OpCode.Ldl4: *sp++ = frameBase[4]; goto Dispatch;
                case OpCode.Ldl5: *sp++ = frameBase[5]; goto Dispatch;

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

                case OpCode.LdlS:
                    *sp++ = frameBase[*ip++];
                    goto Dispatch;

                case OpCode.Stl1: frameBase[1] = *--sp; goto Dispatch;
                case OpCode.Stl3: frameBase[3] = *--sp; goto Dispatch;
                case OpCode.Stl2: frameBase[2] = *--sp; goto Dispatch;
                case OpCode.Stl4: frameBase[4] = *--sp; goto Dispatch;
                case OpCode.Stl5: frameBase[5] = *--sp; goto Dispatch;

                case OpCode.StlS:
                    frameBase[*ip++] = *--sp;
                    goto Dispatch;

                case OpCode.Add:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) + right);
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

                case OpCode.FMul:
                {
                    double right = *(double*)(--sp);
                    *(double*)(sp - 1) = *(double*)(sp - 1) * right;
                    goto Dispatch;
                }

                case OpCode.FAdd:
                {
                    double right = *(double*)(--sp);
                    *(double*)(sp - 1) = *(double*)(sp - 1) + right;
                    goto Dispatch;
                }

                case OpCode.Mul:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) * right);
                    goto Dispatch;
                }

                case OpCode.JP:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2 + offset;
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

                case OpCode.JPZ:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    if ((uint)*--sp == 0) ip += offset;
                    goto Branched;
                }

                case OpCode.PushI8:
                    *sp++ = SurtrValue.TagMaskInt | (uint)(int)(sbyte)*ip++;
                    goto Dispatch;

                case OpCode.PushI32:
                    *sp++ = SurtrValue.TagMaskInt | (uint)(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                    ip += 4;
                    goto Dispatch;

                case OpCode.Ldc7: *sp++ = constants[7]; goto Dispatch;
                case OpCode.Ldc6: *sp++ = constants[6]; goto Dispatch;

                case OpCode.LdcS:
                    *sp++ = constants[*ip++];
                    goto Dispatch;

                case OpCode.Dup:
                    *sp = *(sp - 1);
                    sp++;
                    goto Dispatch;

                case OpCode.LoadLocalField:
                {
                    int index = ip[0] | (ip[1] << 8);
                    int offset = ip[2] | (ip[3] << 8);
                    ip += 4;

                    *sp++ = frameBase[index + offset];
                    goto Dispatch;
                }

                case OpCode.LoadValueLocal:
                {
                    SurtrRawValue* source = frameBase + (ip[0] | (ip[1] << 8));
                    int slotCount = ip[2];
                    ip += 3;

                    for (int i = 0; i < slotCount; i++)
                        *sp++ = source[i];

                    goto Dispatch;
                }

                case OpCode.StoreValueLocal:
                {
                    SurtrRawValue* destination = frameBase + (ip[0] | (ip[1] << 8));
                    int slotCount = ip[2];
                    ip += 3;

                    // The block being stored sits on the operand stack, which begins at
                    // frameBase + LocalCount, and the destination range ends at or before that -
                    // the compiler sized the local to hold it - so a forward copy never overlaps.
                    sp -= slotCount;
                    for (int i = 0; i < slotCount; i++)
                        destination[i] = sp[i];

                    goto Dispatch;
                }

                case OpCode.LoadValueField:
                {
                    int slotCount = ip[2];
                    var fields = ((SurtrInstance)entities[(SurtrRef)(*--sp)]!).Fields;
                    int slot = chunk.FieldTable[(ip[0] | (ip[1] << 8))].SlotIndex;
                    ip += 3;

                    // The receiver is gone; its block takes its place. No allocation, so no
                    // safepoint - the same contract LoadValueLocal moves under.
                    for (int i = 0; i < slotCount; i++)
                        *sp++ = fields[slot + i].Raw;

                    goto Dispatch;
                }

                case OpCode.FieldGet:
                {
                    var field = chunk.FieldTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;

                    // The native case leaves through a cold label at the bottom of the method
                    // rather than being written out here. It is a call plus a safepoint - 185
                    // bytes of code that used to sit between this instruction's first half and its
                    // second, splitting a 150-byte hot path across two cache lines with a jump
                    // over the gap. See docs/Informe-Opcodes-Layout.md §3.1.
                    if (field is SurtrNativeFieldInfo native) { _pendingField = native; goto NativeFieldGet; }

                    int slot = field.SlotIndex;
                    var instance = (SurtrInstance)entities[(SurtrRef)(*(sp - 1))]!;
                    *(sp - 1) = instance.Fields[slot].Raw;
                    goto Dispatch;
                }

                case OpCode.FieldSet:
                {
                    var field = chunk.FieldTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;

                    if (field is SurtrNativeFieldInfo native) { _pendingField = native; goto NativeFieldSet; }

                    int slot = field.SlotIndex;
                    SurtrRawValue value = *--sp;
                    var instance = (SurtrInstance)entities[(SurtrRef)(*--sp)]!;
                    instance.Fields[slot] = SurtrValue.FromRaw(value);
                    goto Dispatch;
                }

                case OpCode.ReturnValue:
                {
                    SurtrRawValue result = *(sp - 1);
                    int depth = _frameCount - 1;
                    ref SurtrCallFrame finished = ref _frames[depth];

                    sp = finished.Base;
                    int expected = finished.ExpectedResults;

                    // `return expr;` inside a generator body (§3.7). The value is not what the
                    // resumer wanted - a resume answers "did it yield?", and this answers "no" like
                    // any other end - so it is kept on the generator, where the `yield from` that
                    // delegated here or a consumer reading `result` picks it up afterwards.
                    // Ordinary frames never carry a generator, so this is the same single null test
                    // on a cached field that ReturnVoid already makes; and no module written before
                    // this branch existed can reach it, because the binder rejected `return expr;`
                    // in a generator outright.
                    var ended = finished.Generator;

                    finished.Chunk = null;
                    finished.Method = null;
                    finished.Closure = null;
                    finished.Generator = null;
                    _roots[depth + 1] = 0;
                    _frameCount = depth;

                    if (ended is not null)
                    {
                        ended.Result = SurtrValue.FromRaw(result);
                        var delegator = ended.FinishAndDetach();

                        if (delegator is null)
                        {
                            sp[-1] = SurtrValue.TagMaskBool;
                        }
                        else
                        {
                            delegator.Resumed = ended.Result;
                            _pendingGenerator = delegator;
                            goto EnterGeneratorFrame;
                        }
                    }

                    if (depth == entryDepth)
                    {
                        _sp = sp;
                        if (steps != long.MaxValue) _stepsRemaining = steps;
                        return SurtrValue.FromRaw(result);
                    }

                    if (expected != 0) *sp++ = result;
                    _sp = sp;
                    goto LoadFrame;
                }

                case OpCode.ReturnValues:
                {
                    // The result is a contiguous block sitting at the top of the operand stack.
                    // The destination (the frame base, below the whole operand stack) can overlap
                    // the source whenever the block was built on fewer slots than it is wide, so
                    // the copy direction follows the overlap: ascending when the destination sits
                    // at or below the source - each read then happens before the slot it names is
                    // overwritten - and descending for the mirror case above it.
                    int slotCount = *ip++;
                    SurtrRawValue* source = sp - slotCount;

                    int depth = _frameCount - 1;
                    ref SurtrCallFrame finished = ref _frames[depth];

                    SurtrRawValue* destination = finished.Base;
                    int expected = finished.ExpectedResults;

                    finished.Chunk = null;
                    finished.Method = null;
                    finished.Closure = null;
                    finished.Generator = null;
                    _roots[depth + 1] = 0;
                    _frameCount = depth;

                    if (expected != 0)
                    {
                        if (destination <= source)
                        {
                            for (int i = 0; i < slotCount; i++)
                                destination[i] = source[i];
                        }
                        else
                        {
                            for (int i = slotCount - 1; i >= 0; i--)
                                destination[i] = source[i];
                        }
                    }

                    sp = expected != 0 ? destination + slotCount : destination;

                    if (depth == entryDepth)
                    {
                        // A run ending here hands results to the host, which reads them off the
                        // stack through CallForResults rather than through this single-value
                        // return - hence the sentinel instead of one slot.
                        _sp = sp;
                        if (steps != long.MaxValue) _stepsRemaining = steps;
                        return SurtrValue.Null;
                    }

                    _sp = sp;
                    goto LoadFrame;
                }

                case OpCode.ReturnVoid:
                {
                    int depth = _frameCount - 1;
                    ref SurtrCallFrame finished = ref _frames[depth];

                    sp = finished.Base;
                    int expected = finished.ExpectedResults;

                    // This is how a generator body ends: `return;` or falling off the end, both of
                    // which the compiler emits as ReturnVoid. Ordinary frames never carry a
                    // generator, so this is one null test on a field already in cache.
                    var ended = finished.Generator;

                    // A dead frame must not keep its chunk, method, closure or generator alive.
                    finished.Chunk = null;
                    finished.Method = null;
                    finished.Closure = null;
                    finished.Generator = null;
                    _roots[depth + 1] = 0;
                    _frameCount = depth;

                    if (ended is not null)
                    {
                        // The resumer left a slot below this frame for the answer, and `false` is
                        // what "the body finished" means there - the mirror of what Yield writes.
                        var delegator = ended.FinishAndDetach();

                        if (delegator is null)
                        {
                            sp[-1] = SurtrValue.TagMaskBool;
                        }
                        else
                        {
                            // Unless somebody was delegating to it, in which case the sequence is
                            // not over at all: what ran out is the inner generator, and the outer
                            // still has whatever follows its `yield from`. It takes the frame the
                            // inner just vacated and answers into the same slot, so the consumer
                            // never learns that a delegation happened.
                            //
                            // The inner's return value travels with the hand-off, which is what
                            // makes `let r = yield from inner();` read it: to the delegator this is
                            // its suspension ending, so the value flows in through the very field
                            // a `send` would have used.
                            delegator.Resumed = ended.Result;
                            _pendingGenerator = delegator;
                            goto EnterGeneratorFrame;
                        }
                    }

                    if (depth == entryDepth)
                    {
                        _sp = sp;
                        if (steps != long.MaxValue) _stepsRemaining = steps;
                        return SurtrValue.Null;
                    }

                    if (expected != 0) *sp++ = SurtrValue.TagMaskReference;
                    _sp = sp;
                    goto LoadFrame;
                }

                case OpCode.InvokeSpecial:
                    _pendingMethod = chunk.MethodTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    _pendingArguments = *ip++;
                    _pendingResults = *ip++;
                    _pendingClosure = null;
                    goto InvokeResolved;

                case OpCode.InvokeClosure:
                {
                    int argumentCount = *ip++;
                    _pendingResults = *ip++;

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

                    _pendingMethod = invoked.Method;
                    _pendingClosure = invoked;
                    _pendingArguments = argumentCount;
                    goto InvokeResolved;
                }

                case OpCode.CallLocalModule:
                    _pendingMethod = chunk.MethodTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    _pendingArguments = *ip++;
                    _pendingResults = *ip++;
                    _pendingClosure = null;
                    goto InvokeResolved;

                case OpCode.ObjNew:
                {
                    HState s = HandleObjectOp(new HState { ip = ip, sp = sp }, ref entities, ref current, chunk, ref context);
                    ip = s.ip;
                    sp = s.sp;
                    if (s.Flow == 1)
                        goto Safepoint;
                    goto Dispatch;
                }

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

                    // The unmanaged buffer has no CLR bounds check of its own, so the explicit
                    // trap above is the only range check ArrGet pays.
                    *(sp - 1) = array.Items[index];
                    goto Dispatch;
                }

                #endregion

                #region Warm - the rest of what the benchmark suite executes
                // Real but not hot. Below the core so they do not split it, above the cold
                // families so a workload that leans on one still finds it near.

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

                    array.Items[index] = value;
                    goto Dispatch;
                }

                case OpCode.ArrPush:
                {
                    SurtrRawValue value = *--sp;
                    var array = (SurtrArray)entities[(SurtrRef)(*--sp)]!;

                    // Written out rather than calling Add, so the common case - room already
                    // available - is a store and an increment with no call at all.
                    int count = array.Count;
                    if (count == array.ItemsCapacity)
                    {
                        current.IP = ip;
                        _sp = sp;
                        array.EnsureCapacity(count + 1);
                    }

                    array.Items[count] = value;
                    array.Count = count + 1;
                    goto Dispatch;
                }

                case OpCode.ArrLen:
                    *(sp - 1) = SurtrValue.TagMaskInt
                        | (uint)((SurtrArray)entities[(SurtrRef)(*(sp - 1))]!).Count;
                    goto Dispatch;

                case OpCode.ArrPack:
                {
                    var arrayType = chunk.TypeTable[(ip[0] | (ip[1] << 8))].Reference;
                    int count = (ip[2] | (ip[3] << 8));
                    ip += 4;
                    current.IP = ip;
                    _sp = sp;

                    var array = new SurtrArray(arrayType, count);
                    array.InitializeLength(count);
                    SurtrRef reference = context.EntityRegistry.Register(array, out entities);

                    var items = array.Items;
                    sp -= count;
                    for (int i = 0; i < count; i++)
                        items[i] = sp[i];

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.ArrNew:
                {
                    var arrayType = chunk.TypeTable[(ip[0] | (ip[1] << 8))].Reference;
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
                            items[i] = elementZero;
                    }

                    SurtrRef reference = context.EntityRegistry.Register(array, out entities);

                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.UpValueGet:
                    *sp++ = closure!.UpValues[*ip++].Raw;
                    goto Dispatch;

                case OpCode.StaticFieldGet:
                {
                    var field = chunk.FieldTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;

                    if (field is SurtrNativeFieldInfo native) { _pendingField = native; goto NativeStaticFieldGet; }

                    // One indirect load: the linker resolved the slot's address when its owner was
                    // laid out, so nothing here tests whether the owner is a class or a module.
                    *sp++ = *field.StaticAddress;
                    goto Dispatch;
                }

                case OpCode.StaticFieldSet:
                {
                    var field = chunk.FieldTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;

                    if (field is SurtrNativeFieldInfo native) { _pendingField = native; goto NativeStaticFieldSet; }

                    *field.StaticAddress = *--sp;
                    goto Dispatch;
                }

                case OpCode.I2F:
                {
                    int value = (int)*(sp - 1);
                    *(double*)(sp - 1) = value;
                    goto Dispatch;
                }

                case OpCode.UnboxDynamic:
                {
                    SurtrRawValue subject = *(sp - 1);

                    // Not a reference at all - already the raw value this is supposed to produce.
                    if ((subject & SurtrValue.TagMask) != SurtrValue.TagMaskReference)
                        goto Dispatch;

                    SurtrRef reference = (SurtrRef)subject;

                    // Null, or a reference that is not a box at all (an ordinary object, array,
                    // string) - both stay exactly as they are, the same "leave a reference alone"
                    // rule BoxDynamic follows in the other direction.
                    if (reference != 0 && entities[reference] is SurtrBoxed boxed)
                        *(sp - 1) = boxed.Value.Raw;

                    goto Dispatch;
                }

                case OpCode.BoxInt:
                {
                    current.IP = ip;
                    _sp = sp;
                    var boxed = new SurtrBoxed(SurtrBuiltIns.Integer, SurtrValue.FromRaw(*(sp - 1)));
                    SurtrRef reference = context.EntityRegistry.Register(boxed, out entities);
                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.EQ:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) == right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.GE:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) >= right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.LT:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) < right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.LE:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) <= right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.Sub:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) - right);
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

                case OpCode.Inv:
                    *(sp - 1) = SurtrValue.TagMaskBool | ((*(sp - 1) & 1) ^ 1);
                    goto Dispatch;

                case OpCode.JPLE:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if ((int)sp[0] <= (int)sp[1]) ip += offset;
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

                case OpCode.JPInstanceOf:
                {
                    var target = chunk.TypeTable[(ip[0] | (ip[1] << 8))].ResolvedType!;
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

                case OpCode.JPN:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    if ((uint)*--sp == 0) ip += offset;
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

                case OpCode.JPLT:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if ((int)sp[0] < (int)sp[1]) ip += offset;
                    goto Branched;
                }

                case OpCode.StrLen:
                    *(sp - 1) = SurtrValue.TagMaskInt
                        | (uint)((SurtrString)entities[(SurtrRef)(*(sp - 1))]!).Value.Length;
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

                    SurtrRef reference = context.EntityRegistry.Register(new SurtrString(joined), out entities);

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.InvokeVirtual:
                {
                    var declared = chunk.MethodTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    _pendingArguments = *ip++;
                    _pendingResults = *ip++;

                    // The receiver is argument 0, which is what makes the frame base one subtraction
                    // regardless of whether a call has a receiver at all.
                    var receiver = (SurtrObject)entities[(SurtrRef)(*(sp - _pendingArguments))]!;
                    _pendingMethod = receiver.Class.VirtualMethods[declared.VTableSlot];
                    _pendingClosure = null;
                    goto InvokeResolved;
                }

                case OpCode.InvokeInterface:
                {
                    int declaredIndex = ip[0] | (ip[1] << 8);
                    var declared = chunk.MethodTable[declaredIndex];
                    ip += 2;
                    _pendingArguments = *ip++;
                    _pendingResults = *ip++;

                    var receiverClass = ((SurtrObject)entities[(SurtrRef)(*(sp - _pendingArguments))]!).Class;

                    // Monomorphic cache, keyed like the virtual one. The hit path collapses the
                    // whole open-addressed probe and the two extra indirections into one array load
                    // and one reference compare; the miss runs the probe and records its result.
                    var interfaceCache = chunk.InterfaceCallCache;
                    if (interfaceCache is null)
                        interfaceCache = chunk.InterfaceCallCache = new SurtrVirtualCallSite[chunk.MethodTable.Length];

                    ref var interfaceSlot = ref interfaceCache[declaredIndex];
                    if (interfaceSlot.Expected != receiverClass)
                    {
                        var contract = (SurtrInterface)declared.DeclaringType!.ResolvedType!;

                        // Which block of the receiver's dispatch table this contract owns. Written out
                        // rather than calling SurtrClass.IndexOfInterface, which would be a real call
                        // from a method this size - the two have to stay in step.
                        int contractId = contract.InterfaceId;
                        int indexMask = receiverClass.InterfaceIndexMask;

                        // A receiver whose class implements no interface has an empty interface-dispatch
                        // table (`InterfaceIndexMask` == -1). Indexing it below would read past the end
                        // of the `SurtrNativeArray` and trip the debug assertion; surface it as a cast
                        // failure instead, so a bad `InvokeInterface` is a diagnosable Surtr exception
                        // rather than a memory-safety crash.
                        if (indexMask < 0)
                            throw InvalidCast(receiverClass.Name, contract.Name);

                        int probe = contractId & indexMask;

                        while (receiverClass.InterfaceIndexById[probe << 1] != contractId)
                            probe = (probe + 1) & indexMask;

                        int contractIndex = receiverClass.InterfaceIndexById[(probe << 1) + 1];

                        // One extra indirection over a virtual call: the interface's block in the
                        // class's dispatch table maps the contract's slot onto a vtable index, so an
                        // override reached through the vtable applies here for free.
                        int vtableSlot = receiverClass.InterfaceMethodSlots[
                            receiverClass.InterfaceSlotOffsets[contractIndex] + declared.VTableSlot];

                        _pendingMethod = receiverClass.VirtualMethods[vtableSlot];
                        interfaceSlot = new SurtrVirtualCallSite { Expected = receiverClass, Method = _pendingMethod };
                    }
                    else
                    {
                        _pendingMethod = interfaceSlot.Method!;
                    }

                    _pendingClosure = null;
                    goto InvokeResolved;
                }

                case OpCode.NewFunction:
                {
                    var target = chunk.MethodTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    current.IP = ip;
                    _sp = sp;

                    // The one shared closure for the method: nothing to allocate on an evaluation,
                    // and registering it (on first use) may grow the entity table, hence the
                    // safepoint below rather than a plain dispatch.
                    var function = _runtime.GetOrCreateFunctionValue(target);
                    *sp++ = SurtrValue.CreateReference(function.GetSurtrReference()).Raw;
                    goto Safepoint;
                }

                case OpCode.CallModule:
                {
                    var target = chunk.ModuleTable[(ip[0] | (ip[1] << 8))];
                    _pendingMethod = target.Chunk.MethodTable[(ip[2] | (ip[3] << 8))];
                    ip += 4;
                    _pendingArguments = *ip++;
                    _pendingResults = *ip++;
                    _pendingClosure = null;
                    goto InvokeResolved;
                }

                case OpCode.NewClosure:
                {
                    var target = chunk.MethodTable[(ip[0] | (ip[1] << 8))];
                    int captureCount = ip[2];
                    ip += 3;
                    current.IP = ip;
                    _sp = sp;

                    var captures = captureCount > 0 ? new SurtrValue[captureCount] : Array.Empty<SurtrValue>();
                    sp -= captureCount;
                    for (int i = 0; i < captureCount; i++)
                        captures[i] = SurtrValue.FromRaw(sp[i]);

                    SurtrRef reference = context.EntityRegistry.Register(
                        new SurtrClosure(target.ToSignature(), target, captures), out entities);

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.PushI16:
                    *sp++ = SurtrValue.TagMaskInt | (uint)(int)(short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    goto Dispatch;

                case OpCode.PushAbsent:
                case OpCode.IsAbsent:
                case OpCode.IsPresent:
                case OpCode.JPA:
                case OpCode.JPNA:
                {
                    HState s = HandleNullableOp(new HState { ip = ip, sp = sp });
                    ip = s.ip;
                    sp = s.sp;
                    if (s.Flow == 1)
                        goto Branched;
                    goto Dispatch;
                }

                case OpCode.Pop:
                    sp--;
                    goto Dispatch;

                case OpCode.Ldc5: *sp++ = constants[5]; goto Dispatch;
                case OpCode.Ldc8: *sp++ = constants[8]; goto Dispatch;
                case OpCode.Ldc9: *sp++ = constants[9]; goto Dispatch;

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

                case OpCode.DictGet:
                {
                    SurtrRawValue rawKey = *--sp;
                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*(sp - 1))]!;

                    var ints = dictionary.IntEntries;
                    bool present;
                    SurtrValue found;

                    if (ints != null && (rawKey & SurtrValue.TagMask) == SurtrValue.TagMaskInt)
                        present = ints.TryGetValue((SurtrInt)rawKey, out found);
                    else
                        present = dictionary.TryGetGeneral(SurtrValue.FromRaw(rawKey), out found);

                    if (!present)
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
                    SurtrRawValue rawKey = *--sp;
                    current.IP = ip;
                    _sp = sp;

                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*--sp)]!;
                    var ints = dictionary.IntEntries;

                    if (ints != null && (rawKey & SurtrValue.TagMask) == SurtrValue.TagMaskInt)
                        ints[(SurtrInt)rawKey] = value;
                    else
                        dictionary.SetGeneral(SurtrValue.FromRaw(rawKey), value);

                    goto Dispatch;
                }

                case OpCode.DictDel:
                {
                    SurtrRawValue rawKey = *--sp;
                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*(sp - 1))]!;
                    var ints = dictionary.IntEntries;

                    bool removed = ints != null && (rawKey & SurtrValue.TagMask) == SurtrValue.TagMaskInt
                        ? ints.Remove((SurtrInt)rawKey)
                        : dictionary.RemoveGeneral(SurtrValue.FromRaw(rawKey));

                    *(sp - 1) = SurtrValue.TagMaskBool | (removed ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.DictIn:
                {
                    SurtrRawValue rawKey = *--sp;
                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*(sp - 1))]!;
                    var ints = dictionary.IntEntries;

                    bool contains = ints != null && (rawKey & SurtrValue.TagMask) == SurtrValue.TagMaskInt
                        ? ints.ContainsKey((SurtrInt)rawKey)
                        : dictionary.ContainsKeyGeneral(SurtrValue.FromRaw(rawKey));

                    *(sp - 1) = SurtrValue.TagMaskBool | (contains ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.DictPack:
                {
                    var dictionaryType = chunk.TypeTable[(ip[0] | (ip[1] << 8))].Reference;
                    int count = (ip[2] | (ip[3] << 8));
                    ip += 4;
                    current.IP = ip;
                    _sp = sp;

                    var dictionary = new SurtrDictionary(dictionaryType, _comparer, count);
                    SurtrRef reference = context.EntityRegistry.Register(dictionary, out entities);

                    sp -= count * 2;

                    // The specialised arm is written out here rather than reached through
                    // SurtrDictionary.Set: the JIT will not inline into a method this size, and a
                    // real call is exactly what the specialisation exists to avoid. The store is
                    // re-read after the general arm, which may have de-specialised the dictionary.
                    var packInts = dictionary.IntEntries;
                    for (int i = 0; i < count; i++)
                    {
                        SurtrRawValue packKey = sp[i * 2];
                        SurtrValue packValue = SurtrValue.FromRaw(sp[i * 2 + 1]);

                        if (packInts != null && (packKey & SurtrValue.TagMask) == SurtrValue.TagMaskInt)
                        {
                            packInts[(SurtrInt)packKey] = packValue;
                        }
                        else
                        {
                            dictionary.SetGeneral(SurtrValue.FromRaw(packKey), packValue);
                            packInts = dictionary.IntEntries;
                        }
                    }

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.DictKeys:
                {
                    var arrayType = chunk.TypeTable[(ip[0] | (ip[1] << 8))].Reference;
                    ip += 2;
                    current.IP = ip;
                    _sp = sp;

                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*(sp - 1))]!;
                    var keys = new SurtrArray(arrayType, dictionary.Count);
                    dictionary.CopyKeysTo(keys);

                    SurtrRef reference = context.EntityRegistry.Register(keys, out entities);

                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.StoreValueField:
                {
                    int slotCount = ip[2];
                    sp -= slotCount;
                    var instance = (SurtrInstance)entities[(SurtrRef)(*(sp - 1))]!;
                    var fields = instance.Fields;
                    int slot = chunk.FieldTable[(ip[0] | (ip[1] << 8))].SlotIndex;
                    ip += 3;

                    for (int i = 0; i < slotCount; i++)
                        fields[slot + i] = SurtrValue.FromRaw(sp[i]);

                    sp--;
                    goto Dispatch;
                }

                // `as?`. One type test where the lowering it replaces - spill, InstanceOf, branch,
                // Cast - pays for two, and the failure answer is already representable in the slot
                // the subject occupies.
                case OpCode.CastOrNull:
                {
                    var target = chunk.TypeTable[(ip[0] | (ip[1] << 8))].ResolvedType!;
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

                case OpCode.Yield:
                {
                    var suspending = current.Generator!;

                    // Read before anything is written: the value is the top operand and the copy
                    // below is about to take the rest of the frame with it.
                    suspending.Current = SurtrValue.FromRaw(*--sp);

                    SurtrRawValue* frameStart = current.Base;
                    int liveSlots = (int)(sp - frameStart);

                    var slots = suspending.Slots;
                    for (int i = 0; i < liveSlots; i++)
                        slots[i] = SurtrValue.FromRaw(frameStart[i]);

                    // Anything the previous suspension left above the new live width would be
                    // traced on the next collection and would retain objects this frame has already
                    // dropped, so the slack is blanked rather than left as it was.
                    for (int i = liveSlots; i < suspending.SlotCount; i++)
                        slots[i] = SurtrValue.Null;

                    suspending.SlotCount = liveSlots;
                    suspending.ResumeOffset = (int)(ip - current.CodeBase);
                    suspending.State = SurtrGeneratorState.Suspended;

                    // The slot the resumer left below this frame answers its question. Written the
                    // same way by the two ways a body can leave, so nothing downstream has to know
                    // which one happened.
                    frameStart[-1] = SurtrValue.TagMaskBool | 1UL;

                    // From here it is an ordinary return that happens to produce nothing: the frame
                    // is popped and control goes back to whoever resumed it, which is what lets one
                    // Yield serve both the compiled fast path and a resume driven by host code.
                    int depth = _frameCount - 1;
                    ref SurtrCallFrame parked = ref _frames[depth];

                    sp = frameStart;
                    parked.Chunk = null;
                    parked.Method = null;
                    parked.Closure = null;
                    parked.Generator = null;
                    _roots[depth + 1] = 0;
                    _frameCount = depth;

                    _sp = sp;

                    if (depth == entryDepth)
                    {
                        if (steps != long.MaxValue) _stepsRemaining = steps;
                        return SurtrValue.Null;
                    }

                    goto LoadFrame;
                }

                case OpCode.GenResume:
                {
                    var resumed = (SurtrGenerator)entities[(SurtrRef)(*(sp - 1))]!;

                    // Straight to the innermost generator that still has a frame. A delegating
                    // generator is suspended with no frame of its own (GenDelegate), so walking
                    // past it is what makes an N-deep `yield from` chain cost one frame copy per
                    // element rather than N. See Plan-Generadores §11.3.
                    while (resumed.Delegate is { } delegated)
                        resumed = delegated;

                    if (resumed.State == SurtrGeneratorState.Exhausted)
                    {
                        *(sp - 1) = SurtrValue.TagMaskBool;
                        goto Dispatch;
                    }

                    if (resumed.State == SurtrGeneratorState.Running)
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw GeneratorAlreadyRunning();
                    }

                    // An ordinary resume carries nothing in, so whatever a previous `send` left
                    // has to go: a stale injection read back by a later `yield` would be a value
                    // arriving from a resumption that never sent one.
                    resumed.Resumed = SurtrValue.Null;

                    // The generator's own slot stays where it is and becomes the result slot: the
                    // body's frame starts one above it, and whichever way the body leaves - a
                    // `yield` or the end - overwrites it with the answer. That is also what keeps
                    // the root generator reachable from the data stack for the whole resume,
                    // whatever the chain does underneath.
                    current.IP = ip;
                    _pendingGenerator = resumed;
                    goto EnterGeneratorFrame;
                }

                case OpCode.GenCurrent:
                {
                    var read = (SurtrGenerator)entities[(SurtrRef)(*(sp - 1))]!;

                    // The element belongs to whichever generator actually produced it, which under
                    // delegation is not the one the consumer holds. Following the same chain the
                    // resume followed is what keeps the two answering about the same `yield`.
                    while (read.Delegate is { } delegated)
                        read = delegated;

                    *(sp - 1) = read.Current.Raw;
                    goto Dispatch;
                }

                case OpCode.GenResumed:
                {
                    // What the last suspension was resumed with: `send(v)`'s injection at a
                    // `yield`, or the delegated-to generator's return value at a `yield from`.
                    // Emitted only where the source reads it, so a statement `yield` never pays.
                    *sp++ = current.Generator!.Resumed.Raw;
                    goto Dispatch;
                }

                case OpCode.GenNew:
                {
                    var body = (SurtrBytecodeMethodInfo)chunk.MethodTable[(ip[0] | (ip[1] << 8))];
                    var declared = chunk.TypeTable[(ip[2] | (ip[3] << 8))].Reference;
                    int argsCount = ip[4];
                    ip += 5;
                    current.IP = ip;
                    _sp = sp;

                    var built = new SurtrGenerator(declared, body, argsCount);

                    // Written straight out of the stack rather than through a temporary: the
                    // arguments are already raw values sitting in the slots this loop is about to
                    // pop, and the generator's buffer is exactly where they have to end up.
                    var slots = built.Slots;
                    sp -= argsCount;
                    for (int i = 0; i < argsCount; i++)
                        slots[i] = SurtrValue.FromRaw(sp[i]);

                    SurtrRef reference = context.EntityRegistry.Register(built, out entities);

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.GenIterate:
                {
                    var walked = (SurtrGenerator)entities[(SurtrRef)(*(sp - 1))]!;

                    // A generator object is walked once. Silently iterating nothing the second time
                    // is a bug that never announces itself, so it traps here instead; restarting is
                    // calling the generator function again, which builds a new one.
                    if (walked.State != SurtrGeneratorState.NotStarted)
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw GeneratorAlreadyStarted();
                    }

                    goto Dispatch;
                }

                case OpCode.GenDelegate:
                {
                    var inner = (SurtrGenerator)entities[(SurtrRef)(*(sp - 1))]!;
                    var outer = current.Generator!;
                    sp--;

                    // Delegating to something already finished produces nothing at all, so the
                    // outer simply carries on. Handled here rather than by suspending and being
                    // resumed straight back, which would have to explain a `false` that does not
                    // mean what a `false` means everywhere else. The result still has to be handed
                    // over: `yield from` evaluates to the inner generator's return value, and one
                    // that ended before the delegation began has one just as much as one that ends
                    // during it.
                    if (inner.State == SurtrGeneratorState.Exhausted)
                    {
                        outer.Resumed = inner.Result;
                        goto Dispatch;
                    }

                    // Covers `yield from self` and any longer cycle: a running generator's frame is
                    // live on this stack, and entering it again would copy a stale frame over it.
                    if (inner.State == SurtrGeneratorState.Running)
                    {
                        current.IP = ip;
                        _sp = sp;
                        throw GeneratorAlreadyRunning();
                    }

                    // The outer suspends exactly as a `yield` suspends it, minus the value: its
                    // frame is copied out and popped, and it resumes after this instruction once
                    // the inner runs out.
                    {
                        SurtrRawValue* outerStart = current.Base;
                        int outerLive = (int)(sp - outerStart);

                        var outerSlots = outer.Slots;
                        for (int i = 0; i < outerLive; i++)
                            outerSlots[i] = SurtrValue.FromRaw(outerStart[i]);

                        for (int i = outerLive; i < outer.SlotCount; i++)
                            outerSlots[i] = SurtrValue.Null;

                        outer.SlotCount = outerLive;
                        outer.ResumeOffset = (int)(ip - current.CodeBase);
                        outer.State = SurtrGeneratorState.Suspended;
                        outer.Delegate = inner;
                        inner.DelegatedBy = outer;

                        int outerDepth = _frameCount - 1;
                        ref SurtrCallFrame parked = ref _frames[outerDepth];

                        sp = outerStart;
                        parked.Chunk = null;
                        parked.Method = null;
                        parked.Closure = null;
                        parked.Generator = null;
                        _roots[outerDepth + 1] = 0;
                        _frameCount = outerDepth;
                    }

                    // The inner takes the frame the outer just vacated, answering into the very
                    // same slot - which is why a delegated element is indistinguishable from one
                    // the outer yielded itself. No IP to publish: the frame that had one is gone,
                    // and the frames still below it kept theirs.
                    _pendingGenerator = inner;
                    goto EnterGeneratorFrame;
                }

                // The prefix. One extra load and one extra indirect branch buy a second 256-value
                // space, which is why nothing lands here that saves less than the dispatch it
                // costs - see SurtrExtOpCode for the admission rule. The nested switch is written
                // the same way as the outer one: every body inline, every exit a goto, nothing
                // spilled across a call.
                case OpCode.Ext:
                    switch ((SurtrExtOpCode)(*ip++))
                    {
                        case SurtrExtOpCode.Probe:
                            *sp++ = frameBase[*ip++];
                            goto Dispatch;

                        // ---- Loop steps ---------------------------------------------------
                        // Each of these is a whole for-in iteration's overhead. They are written
                        // out twice, once per offset width, rather than sharing a body through a
                        // flag: the width is known at emit time and a test per element is exactly
                        // what the fusion exists to remove. Every taken branch leaves through
                        // Branched so the step budget still bounds the loop.

                        case SurtrExtOpCode.ArrForNext:
                        {
                            SurtrRawValue* slot = frameBase + ip[1];
                            int index = (int)*slot + 1;
                            var array = (SurtrArray)entities[(SurtrRef)frameBase[ip[0]]]!;

                            // Count is reloaded per element on purpose: the body may push, and the
                            // walk is defined to see that. The index slot is left alone on the way
                            // out - it is a compiler temporary nothing reads after the loop.
                            if ((uint)index >= (uint)array.Count)
                            {
                                ip += 5;
                                goto Dispatch;
                            }

                            *slot = SurtrValue.TagMaskInt | (uint)index;
                            frameBase[ip[2]] = array.Items[index];
                            ip += 5 + (short)(ip[3] | (ip[4] << 8));
                            goto Branched;
                        }

                        case SurtrExtOpCode.ArrForNextX:
                        {
                            SurtrRawValue* slot = frameBase + ip[1];
                            int index = (int)*slot + 1;
                            var array = (SurtrArray)entities[(SurtrRef)frameBase[ip[0]]]!;

                            if ((uint)index >= (uint)array.Count)
                            {
                                ip += 7;
                                goto Dispatch;
                            }

                            *slot = SurtrValue.TagMaskInt | (uint)index;
                            frameBase[ip[2]] = array.Items[index];
                            ip += 7 + (ip[3] | (ip[4] << 8) | (ip[5] << 16) | (ip[6] << 24));
                            goto Branched;
                        }

                        case SurtrExtOpCode.StrForNext:
                        {
                            SurtrRawValue* slot = frameBase + ip[1];
                            int index = (int)*slot + 1;
                            string text = ((SurtrString)entities[(SurtrRef)frameBase[ip[0]]]!).Value;

                            if ((uint)index >= (uint)text.Length)
                            {
                                ip += 5;
                                goto Dispatch;
                            }

                            *slot = SurtrValue.TagMaskInt | (uint)index;
                            frameBase[ip[2]] = SurtrValue.TagMaskChar | (uint)text[index];
                            ip += 5 + (short)(ip[3] | (ip[4] << 8));
                            goto Branched;
                        }

                        case SurtrExtOpCode.StrForNextX:
                        {
                            SurtrRawValue* slot = frameBase + ip[1];
                            int index = (int)*slot + 1;
                            string text = ((SurtrString)entities[(SurtrRef)frameBase[ip[0]]]!).Value;

                            if ((uint)index >= (uint)text.Length)
                            {
                                ip += 7;
                                goto Dispatch;
                            }

                            *slot = SurtrValue.TagMaskInt | (uint)index;
                            frameBase[ip[2]] = SurtrValue.TagMaskChar | (uint)text[index];
                            ip += 7 + (ip[3] | (ip[4] << 8) | (ip[5] << 16) | (ip[6] << 24));
                            goto Branched;
                        }

                        case SurtrExtOpCode.TupForNext:
                        {
                            SurtrRawValue* slot = frameBase + ip[1];
                            int index = (int)*slot + 1;
                            var elements = ((SurtrTuple)entities[(SurtrRef)frameBase[ip[0]]]!).Elements;

                            if ((uint)index >= (uint)elements.Length)
                            {
                                ip += 5;
                                goto Dispatch;
                            }

                            *slot = SurtrValue.TagMaskInt | (uint)index;
                            frameBase[ip[2]] = elements[index].Raw;
                            ip += 5 + (short)(ip[3] | (ip[4] << 8));
                            goto Branched;
                        }

                        case SurtrExtOpCode.TupForNextX:
                        {
                            SurtrRawValue* slot = frameBase + ip[1];
                            int index = (int)*slot + 1;
                            var elements = ((SurtrTuple)entities[(SurtrRef)frameBase[ip[0]]]!).Elements;

                            if ((uint)index >= (uint)elements.Length)
                            {
                                ip += 7;
                                goto Dispatch;
                            }

                            *slot = SurtrValue.TagMaskInt | (uint)index;
                            frameBase[ip[2]] = elements[index].Raw;
                            ip += 7 + (ip[3] | (ip[4] << 8) | (ip[5] << 16) | (ip[6] << 24));
                            goto Branched;
                        }

                        case SurtrExtOpCode.DictForNext:
                        {
                            SurtrRawValue* slot = frameBase + ip[1];
                            int index = (int)*slot + 1;
                            var keys = (SurtrArray)entities[(SurtrRef)frameBase[ip[0]]]!;

                            if ((uint)index >= (uint)keys.Count)
                            {
                                ip += 6;
                                goto Dispatch;
                            }

                            SurtrRawValue key = keys.Items[index];
                            var dictionary = (SurtrDictionary)entities[(SurtrRef)frameBase[ip[2]]]!;

                            // The specialised int store is picked here rather than in a
                            // DictGet of its own, which is where the fused form pays twice: the
                            // two arms are written out exactly as DictGet writes them, and the
                            // dispatch that would have separated them is gone.
                            var ints = dictionary.IntEntries;
                            bool present;
                            SurtrValue found;

                            if (ints != null && (key & SurtrValue.TagMask) == SurtrValue.TagMaskInt)
                                present = ints.TryGetValue((SurtrInt)key, out found);
                            else
                                present = dictionary.TryGetGeneral(SurtrValue.FromRaw(key), out found);

                            // Still trapping, and the trap is still reachable: the body can delete
                            // a key the snapshot was taken before.
                            if (!present)
                            {
                                current.IP = ip;
                                _sp = sp;
                                throw MissingKey();
                            }

                            *slot = SurtrValue.TagMaskInt | (uint)index;

                            SurtrRawValue* pair = frameBase + ip[3];
                            pair[0] = key;
                            pair[1] = found.Raw;

                            ip += 6 + (short)(ip[4] | (ip[5] << 8));
                            goto Branched;
                        }

                        case SurtrExtOpCode.DictForNextX:
                        {
                            SurtrRawValue* slot = frameBase + ip[1];
                            int index = (int)*slot + 1;
                            var keys = (SurtrArray)entities[(SurtrRef)frameBase[ip[0]]]!;

                            if ((uint)index >= (uint)keys.Count)
                            {
                                ip += 8;
                                goto Dispatch;
                            }

                            SurtrRawValue key = keys.Items[index];
                            var dictionary = (SurtrDictionary)entities[(SurtrRef)frameBase[ip[2]]]!;

                            var ints = dictionary.IntEntries;
                            bool present;
                            SurtrValue found;

                            if (ints != null && (key & SurtrValue.TagMask) == SurtrValue.TagMaskInt)
                                present = ints.TryGetValue((SurtrInt)key, out found);
                            else
                                present = dictionary.TryGetGeneral(SurtrValue.FromRaw(key), out found);

                            if (!present)
                            {
                                current.IP = ip;
                                _sp = sp;
                                throw MissingKey();
                            }

                            *slot = SurtrValue.TagMaskInt | (uint)index;

                            SurtrRawValue* pair = frameBase + ip[3];
                            pair[0] = key;
                            pair[1] = found.Raw;

                            ip += 8 + (ip[4] | (ip[5] << 8) | (ip[6] << 16) | (ip[7] << 24));
                            goto Branched;
                        }

                        case SurtrExtOpCode.ForRangeNextLE:
                        {
                            SurtrRawValue* slot = frameBase + ip[0];

                            // Written unconditionally, which is what IncLocal plus a top-of-loop
                            // guard did: the variable's value after the loop is observable and has
                            // to stay what it was. Wrapping is preserved for the same reason.
                            int value = unchecked((int)*slot + 1);
                            *slot = SurtrValue.TagMaskInt | (uint)value;

                            if (value <= (int)frameBase[ip[1]])
                            {
                                ip += 4 + (short)(ip[2] | (ip[3] << 8));
                                goto Branched;
                            }

                            ip += 4;
                            goto Dispatch;
                        }

                        case SurtrExtOpCode.ForRangeNextLEX:
                        {
                            SurtrRawValue* slot = frameBase + ip[0];
                            int value = unchecked((int)*slot + 1);
                            *slot = SurtrValue.TagMaskInt | (uint)value;

                            if (value <= (int)frameBase[ip[1]])
                            {
                                ip += 6 + (ip[2] | (ip[3] << 8) | (ip[4] << 16) | (ip[5] << 24));
                                goto Branched;
                            }

                            ip += 6;
                            goto Dispatch;
                        }

                        case SurtrExtOpCode.ForRangeNextLT:
                        {
                            SurtrRawValue* slot = frameBase + ip[0];
                            int value = unchecked((int)*slot + 1);
                            *slot = SurtrValue.TagMaskInt | (uint)value;

                            if (value < (int)frameBase[ip[1]])
                            {
                                ip += 4 + (short)(ip[2] | (ip[3] << 8));
                                goto Branched;
                            }

                            ip += 4;
                            goto Dispatch;
                        }

                        case SurtrExtOpCode.ForRangeNextLTX:
                        {
                            SurtrRawValue* slot = frameBase + ip[0];
                            int value = unchecked((int)*slot + 1);
                            *slot = SurtrValue.TagMaskInt | (uint)value;

                            if (value < (int)frameBase[ip[1]])
                            {
                                ip += 6 + (ip[2] | (ip[3] << 8) | (ip[4] << 16) | (ip[5] << 24));
                                goto Branched;
                            }

                            ip += 6;
                            goto Dispatch;
                        }

                        default:
                            current.IP = ip;
                            _sp = sp;
                            throw InvalidExtOpCode(*(ip - 1));
                    }

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

                #region Cold - reachable from the language, unexercised by the suite
                // Zero dispatches over the 50-workload suite, which is a statement about the
                // suite rather than about the language: every one of these is emittable. They sit
                // last because a body that never runs still displaces one that does.

                case OpCode.FDiv:
                {
                    double right = *(double*)(--sp);
                    *(double*)(sp - 1) = *(double*)(sp - 1) / right;
                    goto Dispatch;
                }

                case OpCode.FMod:
                {
                    double right = *(double*)(--sp);
                    *(double*)(sp - 1) = *(double*)(sp - 1) % right;
                    goto Dispatch;
                }

                case OpCode.FNeg:
                    // Flipping the sign bit rather than computing 0 - x, so negative zero and NaN
                    // both behave the way IEEE 754 says they should.
                    *(sp - 1) ^= 0x8000000000000000UL;
                    goto Dispatch;

                case OpCode.FSub:
                {
                    double right = *(double*)(--sp);
                    *(double*)(sp - 1) = *(double*)(sp - 1) - right;
                    goto Dispatch;
                }

                case OpCode.Neg:
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)(-(int)*(sp - 1));
                    goto Dispatch;

                case OpCode.ArrClear:
                    ((SurtrArray)entities[(SurtrRef)(*--sp)]!).Clear();
                    goto Dispatch;

                case OpCode.ArrIn:
                {
                    SurtrValue needle = SurtrValue.FromRaw(*--sp);
                    var array = (SurtrArray)entities[(SurtrRef)(*(sp - 1))]!;
                    *(sp - 1) = SurtrValue.TagMaskBool | (array.IndexOf(needle, _comparer) >= 0 ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.ArrIndexOf:
                {
                    SurtrValue needle = SurtrValue.FromRaw(*--sp);
                    var array = (SurtrArray)entities[(SurtrRef)(*(sp - 1))]!;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)array.IndexOf(needle, _comparer);
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

                case OpCode.ArrNewX:
                {
                    var arrayType = chunk.TypeTable[(ip[0] | (ip[1] << 8))].Reference;
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
                            items[i] = elementZero;
                    }

                    SurtrRef reference = context.EntityRegistry.Register(array, out entities);

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
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

                    *(sp - 1) = array.Items[last];

                    // Blanked, not merely abandoned: a stale reference past Count would keep an
                    // entity alive the moment anything traced beyond the live prefix.
                    array.Items[last] = 0;
                    array.Count = last;
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

                case OpCode.And:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) & right);
                    goto Dispatch;
                }

                case OpCode.Not:
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)(~(int)*(sp - 1));
                    goto Dispatch;

                case OpCode.Or:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) | right);
                    goto Dispatch;
                }

                case OpCode.Sar:
                {
                    int count = (int)*--sp & 31;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) >> count);
                    goto Dispatch;
                }

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

                case OpCode.Xor:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)((int)*(sp - 1) ^ right);
                    goto Dispatch;
                }

                case OpCode.DynEQ:
                {
                    SurtrValue right = SurtrValue.FromRaw(*--sp);
                    SurtrValue left = SurtrValue.FromRaw(*(sp - 1));
                    *(sp - 1) = SurtrValue.TagMaskBool | (_comparer.ValuesEqual(left, right) ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.DynNE:
                {
                    SurtrValue right = SurtrValue.FromRaw(*--sp);
                    SurtrValue left = SurtrValue.FromRaw(*(sp - 1));
                    *(sp - 1) = SurtrValue.TagMaskBool | (_comparer.ValuesEqual(left, right) ? 0UL : 1UL);
                    goto Dispatch;
                }

                case OpCode.FEQ:
                {
                    double right = *(double*)(--sp);
                    *(sp - 1) = SurtrValue.TagMaskBool | (*(double*)(sp - 1) == right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.FGE:
                {
                    double right = *(double*)(--sp);
                    *(sp - 1) = SurtrValue.TagMaskBool | (*(double*)(sp - 1) >= right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.FGT:
                {
                    double right = *(double*)(--sp);
                    *(sp - 1) = SurtrValue.TagMaskBool | (*(double*)(sp - 1) > right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.FLE:
                {
                    double right = *(double*)(--sp);
                    *(sp - 1) = SurtrValue.TagMaskBool | (*(double*)(sp - 1) <= right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.FLT:
                {
                    double right = *(double*)(--sp);
                    *(sp - 1) = SurtrValue.TagMaskBool | (*(double*)(sp - 1) < right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.FNE:
                {
                    double right = *(double*)(--sp);
                    *(sp - 1) = SurtrValue.TagMaskBool | (*(double*)(sp - 1) != right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.GT:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) > right ? 1UL : 0UL);
                    goto Dispatch;
                }

                case OpCode.IsNotNull:
                    *(sp - 1) = SurtrValue.TagMaskBool | ((uint)*(sp - 1) != 0 ? 1UL : 0UL);
                    goto Dispatch;

                case OpCode.IsNull:
                    *(sp - 1) = SurtrValue.TagMaskBool | ((uint)*(sp - 1) == 0 ? 1UL : 0UL);
                    goto Dispatch;

                case OpCode.NE:
                {
                    int right = (int)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) != right ? 1UL : 0UL);
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

                case OpCode.RNE:
                {
                    uint right = (uint)*--sp;
                    *(sp - 1) = SurtrValue.TagMaskBool | ((uint)*(sp - 1) != right ? 1UL : 0UL);
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

                case OpCode.Ldc:
                    *sp++ = constants[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    goto Dispatch;

                case OpCode.Ldc0: *sp++ = constants[0]; goto Dispatch;
                case OpCode.Ldc1: *sp++ = constants[1]; goto Dispatch;
                case OpCode.Ldc2: *sp++ = constants[2]; goto Dispatch;
                case OpCode.Ldc3: *sp++ = constants[3]; goto Dispatch;
                case OpCode.Ldc4: *sp++ = constants[4]; goto Dispatch;

                case OpCode.Nop:
                    goto Dispatch;

                case OpCode.PushChar:
                    *sp++ = SurtrValue.TagMaskChar | (uint)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    goto Dispatch;

                case OpCode.PushNull:
                    *sp++ = SurtrValue.TagMaskReference;
                    goto Dispatch;

                case OpCode.B2I:
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)(int)*(sp - 1);
                    goto Dispatch;

                case OpCode.BoxAs:
                {
                    var declared = chunk.TypeTable[(ip[0] | (ip[1] << 8))].ResolvedClass!;
                    ip += 2;
                    current.IP = ip;
                    _sp = sp;

                    var boxed = new SurtrBoxed(declared, SurtrValue.FromRaw(*(sp - 1)));
                    SurtrRef reference = context.EntityRegistry.Register(boxed, out entities);

                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.BoxBool:
                {
                    current.IP = ip;
                    _sp = sp;
                    var boxed = new SurtrBoxed(SurtrBuiltIns.Boolean, SurtrValue.FromRaw(*(sp - 1)));
                    SurtrRef reference = context.EntityRegistry.Register(boxed, out entities);
                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.BoxChar:
                {
                    current.IP = ip;
                    _sp = sp;
                    var boxed = new SurtrBoxed(SurtrBuiltIns.Character, SurtrValue.FromRaw(*(sp - 1)));
                    SurtrRef reference = context.EntityRegistry.Register(boxed, out entities);
                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.BoxDynamic:
                {
                    SurtrRawValue subject = *(sp - 1);

                    // Already a reference (or null) - the same no-op every fixed Box* opcode is for
                    // a value that needs none, just read off the tag instead of a static type.
                    if ((subject & SurtrValue.TagMask) == SurtrValue.TagMaskReference)
                        goto Dispatch;

                    current.IP = ip;
                    _sp = sp;
                    SurtrValue value = SurtrValue.FromRaw(subject);
                    var boxed = new SurtrBoxed(SurtrBuiltIns.ForValue(value), value);
                    SurtrRef reference = context.EntityRegistry.Register(boxed, out entities);
                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.BoxFloat:
                {
                    current.IP = ip;
                    _sp = sp;
                    var boxed = new SurtrBoxed(SurtrBuiltIns.Float, SurtrValue.FromRaw(*(sp - 1)));
                    SurtrRef reference = context.EntityRegistry.Register(boxed, out entities);
                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.C2I:
                    *(sp - 1) = SurtrValue.TagMaskInt | (uint)(int)*(sp - 1);
                    goto Dispatch;

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

                case OpCode.I2B:
                    // Normalises as well as retags, so every boolean payload is 0 or 1 and the
                    // boolean opcodes can treat it as a bit.
                    *(sp - 1) = SurtrValue.TagMaskBool | ((int)*(sp - 1) != 0 ? 1UL : 0UL);
                    goto Dispatch;

                case OpCode.I2C:
                    *(sp - 1) = SurtrValue.TagMaskChar | (uint)(ushort)(int)*(sp - 1);
                    goto Dispatch;

                case OpCode.Unbox:
                    *(sp - 1) = ((SurtrBoxed)entities[(SurtrRef)(*(sp - 1))]!).Value.Raw;
                    goto Dispatch;

                case OpCode.DictClear:
                {
                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*--sp)]!;
                    var ints = dictionary.IntEntries;

                    if (ints != null)
                        ints.Clear();
                    else
                        dictionary.Entries!.Clear();

                    goto Dispatch;
                }

                case OpCode.DictLen:
                {
                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*(sp - 1))]!;
                    var ints = dictionary.IntEntries;
                    *(sp - 1) = SurtrValue.TagMaskInt
                        | (uint)(ints != null ? ints.Count : dictionary.Entries!.Count);
                    goto Dispatch;
                }

                case OpCode.DictNew:
                {
                    var dictionaryType = chunk.TypeTable[(ip[0] | (ip[1] << 8))].Reference;
                    ip += 2;
                    current.IP = ip;
                    _sp = sp;

                    SurtrRef reference = context.EntityRegistry.Register(
                        new SurtrDictionary(dictionaryType, _comparer, 0), out entities);

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.DictValues:
                {
                    var arrayType = chunk.TypeTable[(ip[0] | (ip[1] << 8))].Reference;
                    ip += 2;
                    current.IP = ip;
                    _sp = sp;

                    var dictionary = (SurtrDictionary)entities[(SurtrRef)(*(sp - 1))]!;
                    var values = new SurtrArray(arrayType, dictionary.Count);
                    dictionary.CopyValuesTo(values);

                    SurtrRef reference = context.EntityRegistry.Register(values, out entities);

                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.InvokeStatic:
                    _pendingMethod = chunk.MethodTable[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    _pendingArguments = *ip++;
                    _pendingResults = *ip++;
                    _pendingClosure = null;
                    goto InvokeResolved;

                case OpCode.Ldl:
                    *sp++ = frameBase[(ip[0] | (ip[1] << 8))];
                    ip += 2;
                    goto Dispatch;

                case OpCode.Stl:
                    frameBase[(ip[0] | (ip[1] << 8))] = *--sp;
                    ip += 2;
                    goto Dispatch;

                case OpCode.Stl0: frameBase[0] = *--sp; goto Dispatch;

                case OpCode.LoadModule:
                case OpCode.LoadCurrentModule:
                {
                    HState s = HandleModuleOp(new HState { ip = ip, sp = sp }, ref entities, ref current, chunk, ref context);
                    ip = s.ip;
                    sp = s.sp;
                    goto Dispatch;
                }

                case OpCode.RangeNew:
                case OpCode.RangeNewInclusive:
                case OpCode.RangePack:
                case OpCode.RangeUnpack:
                {
                    HState s = HandleRangeOp(new HState { ip = ip, sp = sp }, ref entities, ref current, ref context);
                    ip = s.ip;
                    sp = s.sp;
                    if (s.Flow == 1)
                        goto Safepoint;
                    goto Dispatch;
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

                case OpCode.JPFGE:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if (((double*)sp)[0] >= ((double*)sp)[1]) ip += offset;
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

                case OpCode.JPFLE:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if (((double*)sp)[0] <= ((double*)sp)[1]) ip += offset;
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

                case OpCode.JPFNE:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if (((double*)sp)[0] != ((double*)sp)[1]) ip += offset;
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

                case OpCode.JPNN:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    if ((uint)*--sp != 0) ip += offset;
                    goto Branched;
                }

                case OpCode.JPNZ:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    if ((uint)*--sp != 0) ip += offset;
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

                case OpCode.JPRNE:
                {
                    short offset = (short)(ip[0] | (ip[1] << 8));
                    ip += 2;
                    sp -= 2;
                    if ((uint)sp[0] != (uint)sp[1]) ip += offset;
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

                case OpCode.StrHash:
                    // A load, not a walk: the hash is computed once, on first need, and cached on
                    // the string - and is the same in any process, which is what a compiled string
                    // switch needs.
                    *(sp - 1) = SurtrValue.TagMaskInt
                        | (uint)((SurtrString)entities[(SurtrRef)(*(sp - 1))]!).Hash;
                    goto Dispatch;

                case OpCode.Cast:
                {
                    var target = chunk.TypeTable[(ip[0] | (ip[1] << 8))].ResolvedType!;
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

                // No null check, on purpose: matches FieldGet and the native Type.of this
                // replaces. A primitive operand never reaches here at all - the compiler lowers
                // typeof of a primitive-typed expression straight to LoadType instead, since its
                // class can never differ from its static one.
                case OpCode.GetTypeOfValue:
                {
                    var valueClass = ((SurtrObject)entities[(SurtrRef)(*(sp - 1))]!).Class;
                    current.IP = ip;
                    _sp = sp;

                    var typeValue = _runtime.GetOrCreateTypeValue(valueClass);
                    entities = context.EntityRegistry.Entities;
                    *(sp - 1) = SurtrValue.TagMaskReference | (uint)typeValue.GetSurtrReference();
                    goto Dispatch;
                }

                case OpCode.InstanceOf:
                {
                    var target = chunk.TypeTable[(ip[0] | (ip[1] << 8))].ResolvedType!;
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

                case OpCode.LoadType:
                {
                    ref var typeHandle = ref chunk.TypeTable[(ip[0] | (ip[1] << 8))];
                    var target = typeHandle.ResolvedType!;
                    ip += 2;
                    current.IP = ip;
                    _sp = sp;

                    var typeValue = _runtime.GetOrCreateTypeValue(target, typeHandle.Reference);
                    entities = context.EntityRegistry.Entities;
                    *sp++ = SurtrValue.TagMaskReference | (uint)typeValue.GetSurtrReference();
                    goto Dispatch;
                }

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

                case OpCode.TupLen:
                    *(sp - 1) = SurtrValue.TagMaskInt
                        | (uint)((SurtrTuple)entities[(SurtrRef)(*(sp - 1))]!).Elements.Length;
                    goto Dispatch;

                case OpCode.TupPack:
                {
                    var tupleType = chunk.TypeTable[(ip[0] | (ip[1] << 8))].Reference;
                    int arity = ip[2];
                    ip += 3;
                    current.IP = ip;
                    _sp = sp;

                    var tuple = new SurtrTuple(tupleType, arity);
                    SurtrRef reference = context.EntityRegistry.Register(tuple, out entities);

                    var elements = tuple.Elements;
                    sp -= arity;
                    for (int i = 0; i < arity; i++)
                        elements[i] = SurtrValue.FromRaw(sp[i]);

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
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

                case OpCode.BoxValue:
                {
                    var declared = chunk.TypeTable[(ip[0] | (ip[1] << 8))].ResolvedClass!;
                    int slotCount = ip[2];
                    ip += 3;
                    current.IP = ip;
                    _sp = sp;

                    // The box is an ordinary instance whose field slots receive the stack slots
                    // verbatim, so every path that walks instances already walks a boxed value.
                    var box = new SurtrInstance(declared);
                    var fields = box.Fields;

                    SurtrRef reference = context.EntityRegistry.Register(box, out entities);

                    sp -= slotCount;
                    for (int i = 0; i < slotCount; i++)
                        fields[i] = SurtrValue.FromRaw(sp[i]);

                    *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    goto Safepoint;
                }

                case OpCode.LoadValueStatic:
                {
                    int slotCount = ip[2];
                    SurtrRawValue* source = chunk.FieldTable[(ip[0] | (ip[1] << 8))].StaticAddress;
                    ip += 3;

                    for (int i = 0; i < slotCount; i++)
                        *sp++ = source[i];

                    goto Dispatch;
                }

                case OpCode.StoreLocalField:
                {
                    int index = ip[0] | (ip[1] << 8);
                    int offset = ip[2] | (ip[3] << 8);
                    ip += 4;

                    sp--;
                    frameBase[index + offset] = *sp;
                    goto Dispatch;
                }

                case OpCode.StoreValueStatic:
                {
                    int slotCount = ip[2];
                    sp -= slotCount;
                    SurtrRawValue* destination = chunk.FieldTable[(ip[0] | (ip[1] << 8))].StaticAddress;
                    ip += 3;

                    for (int i = 0; i < slotCount; i++)
                        destination[i] = sp[i];

                    goto Dispatch;
                }

                case OpCode.UnboxValue:
                {
                    int slotCount = *ip++;
                    var fields = ((SurtrInstance)entities[(SurtrRef)(*--sp)]!).Fields;

                    for (int i = 0; i < slotCount; i++)
                        *sp++ = fields[i].Raw;

                    goto Dispatch;
                }

                #endregion

                #region Cold - the Wide prefix

                // `Wide` is a prefix, not an instruction: the byte after it is an ordinary opcode
                // whose single index or offset immediate is read as four bytes instead of two. It
                // replaces the thirty-nine `*X` twins that used to hold a value each, on the same
                // bargain the `Ext` prefix makes below - one extra dispatch, paid only by an
                // instruction that in the whole 50-workload suite never executed once. What
                // reaches here is the emitter's own relaxation: an offset past a 32 KB method
                // body, or a module with more than 65 535 constants, types or methods. The
                // compiler never writes one.
                //
                // The bodies stay inside Run() rather than moving to a helper because three of
                // them enter the shared call sequence and four the safepoint, both of which are
                // labels of this method. Measured, that costs nothing the hot path can see: the
                // region sits below every warm family (docs/Informe-Opcodes-Layout.md §5).
                case OpCode.Wide:
                    switch ((OpCode)(*ip++))
                    {
                    case OpCode.StaticFieldGet:
                    {
                        var field = chunk.FieldTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                        ip += 4;

                        if (field is SurtrNativeFieldInfo native) { _pendingField = native; goto NativeStaticFieldGet; }

                        *sp++ = *field.StaticAddress;
                        goto Dispatch;
                    }

                    case OpCode.StaticFieldSet:
                    {
                        var field = chunk.FieldTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                        ip += 4;

                        if (field is SurtrNativeFieldInfo native) { _pendingField = native; goto NativeStaticFieldSet; }

                        *field.StaticAddress = *--sp;
                        goto Dispatch;
                    }

                    case OpCode.Ldc:
                        *sp++ = constants[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                        ip += 4;
                        goto Dispatch;

                    case OpCode.BoxAs:
                    {
                        var declared = chunk.TypeTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].ResolvedClass!;
                        ip += 4;
                        current.IP = ip;
                        _sp = sp;

                        var boxed = new SurtrBoxed(declared, SurtrValue.FromRaw(*(sp - 1)));
                        SurtrRef reference = context.EntityRegistry.Register(boxed, out entities);

                        *(sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                        goto Safepoint;
                    }

                    case OpCode.CallLocalModule:
                        _pendingMethod = chunk.MethodTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                        ip += 4;
                        _pendingArguments = *ip++;
                        _pendingResults = *ip++;
                        _pendingClosure = null;
                        goto InvokeResolved;

                    case OpCode.CallModule:
                    {
                        var target = chunk.ModuleTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                        _pendingMethod = target.Chunk.MethodTable[(ip[4] | (ip[5] << 8) | (ip[6] << 16) | (ip[7] << 24))];
                        ip += 8;
                        _pendingArguments = *ip++;
                        _pendingResults = *ip++;
                        _pendingClosure = null;
                        goto InvokeResolved;
                    }

                    case OpCode.InvokeStatic:
                        _pendingMethod = chunk.MethodTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                        ip += 4;
                        _pendingArguments = *ip++;
                        _pendingResults = *ip++;
                        _pendingClosure = null;
                        goto InvokeResolved;

                    case OpCode.NewClosure:
                    {
                        var target = chunk.MethodTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                        int captureCount = ip[4];
                        ip += 5;
                        current.IP = ip;
                        _sp = sp;

                        var captures = captureCount > 0 ? new SurtrValue[captureCount] : Array.Empty<SurtrValue>();
                        sp -= captureCount;
                        for (int i = 0; i < captureCount; i++)
                            captures[i] = SurtrValue.FromRaw(sp[i]);

                        SurtrRef reference = context.EntityRegistry.Register(
                            new SurtrClosure(target.ToSignature(), target, captures), out entities);

                        *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                        goto Safepoint;
                    }

                    case OpCode.NewFunction:
                    {
                        var target = chunk.MethodTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                        ip += 4;
                        current.IP = ip;
                        _sp = sp;

                        var function = _runtime.GetOrCreateFunctionValue(target);
                        *sp++ = SurtrValue.CreateReference(function.GetSurtrReference()).Raw;
                        goto Safepoint;
                    }

                    case OpCode.JPEQ:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if ((int)sp[0] == (int)sp[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPFEQ:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if (((double*)sp)[0] == ((double*)sp)[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPFGE:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if (((double*)sp)[0] >= ((double*)sp)[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPFGT:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if (((double*)sp)[0] > ((double*)sp)[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPFLE:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if (((double*)sp)[0] <= ((double*)sp)[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPFLT:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if (((double*)sp)[0] < ((double*)sp)[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPFNE:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if (((double*)sp)[0] != ((double*)sp)[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPGE:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if ((int)sp[0] >= (int)sp[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPGT:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if ((int)sp[0] > (int)sp[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPInstanceOf:
                    {
                        var target = chunk.TypeTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].ResolvedType!;
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

                    case OpCode.JPLE:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if ((int)sp[0] <= (int)sp[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPLT:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if ((int)sp[0] < (int)sp[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPNE:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if ((int)sp[0] != (int)sp[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPNN:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        if ((uint)*--sp != 0) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPN:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        if ((uint)*--sp == 0) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPNZ:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        if ((uint)*--sp != 0) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPREQ:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if ((uint)sp[0] == (uint)sp[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPRNE:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        sp -= 2;
                        if ((uint)sp[0] != (uint)sp[1]) ip += offset;
                        goto Branched;
                    }

                    case OpCode.JPStrEQ:
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

                    case OpCode.JPStrNE:
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

                    case OpCode.JP:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4 + offset;
                        goto Branched;
                    }

                    case OpCode.JPZ:
                    {
                        int offset = (ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24));
                        ip += 4;
                        if ((uint)*--sp == 0) ip += offset;
                        goto Branched;
                    }

                    case OpCode.CastOrNull:
                    {
                        var target = chunk.TypeTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].ResolvedType!;
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

                    case OpCode.Cast:
                    {
                        var target = chunk.TypeTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].ResolvedType!;
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

                    case OpCode.InstanceOf:
                    {
                        var target = chunk.TypeTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].ResolvedType!;
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

                    case OpCode.LoadType:
                    {
                        ref var typeHandleX = ref chunk.TypeTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))];
                        var targetX = typeHandleX.ResolvedType!;
                        ip += 4;
                        current.IP = ip;
                        _sp = sp;

                        var typeValueX = _runtime.GetOrCreateTypeValue(targetX, typeHandleX.Reference);
                        entities = context.EntityRegistry.Entities;
                        *sp++ = SurtrValue.TagMaskReference | (uint)typeValueX.GetSurtrReference();
                        goto Dispatch;
                    }

                    case OpCode.JPA:
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
                        int offset = ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24);
                        ip += 4;
                        sp--;
                        if ((*sp & SurtrValue.TagMask) != SurtrValue.TagMaskAbsent)
                            ip += offset;
                        goto Branched;
                    }

                    case OpCode.LoadModule:
                    {
                        var target = chunk.ModuleTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))]!;
                        ip += 4;
                        current.IP = ip;
                        _sp = sp;

                        var moduleValue = _runtime.GetOrCreateModuleValue(target);
                        entities = context.EntityRegistry.Entities;
                        *sp++ = SurtrValue.TagMaskReference | (uint)moduleValue.GetSurtrReference();
                        goto Dispatch;
                    }

                    case OpCode.ObjNew:
                    {
                        var declared = chunk.TypeTable[(ip[0] | (ip[1] << 8) | (ip[2] << 16) | (ip[3] << 24))].ResolvedClass!;
                        ip += 4;
                        current.IP = ip;
                        _sp = sp;

                        if (declared.IsAbstract)
                            throw AbstractInstantiation(declared.Name);

                        SurtrRef reference = context.EntityRegistry.Register(new SurtrInstance(declared), out entities);

                        *sp++ = SurtrValue.TagMaskReference | (uint)reference;
                        goto Safepoint;
                    }

                        default:
                            current.IP = ip;
                            _sp = sp;
                            throw InvalidWideOpCode(*(ip - 1));
                    }

                #endregion

                default:
                    current.IP = ip;
                    _sp = sp;
                    throw InvalidOpCode(*(ip - 1));
            }

        // The single safepoint for automatic collection. Every allocation opcode that completed
        // (its result pushed on the stack) routes here instead of straight to Dispatch, so the
        // flag is drained by exactly one call site rather than one per opcode. Living after the
        // switch keeps it off the dispatch hot path entirely - nothing falls through to it.
        Safepoint:
            if (context.EntityRegistry.GcPending)
            {
                _sp = sp;
                _runtime.CollectAtSafepoint();
            }
            goto Dispatch;

        // ---- Native field sequences -----------------------------------------------------------
        // A native field's value lives in the host, so reading or writing one is a call across the
        // boundary plus a safepoint - roughly forty instructions on a branch that a field access
        // takes almost never. Written inline they were placed by the JIT *between* the two halves
        // of their own opcode's hot path: FieldGet's 150 bytes of real work ended up straddling
        // 260 bytes of this, with a jump over the gap (docs/Informe-Opcodes-Layout.md §3.1). Living
        // down here, reached by goto, they cost the hot path one predicted-not-taken branch and
        // nothing else - the same bargain Safepoint above makes.
        //
        // `_pendingField` is read into a local first: the invoke can re-enter Run(), and a nested
        // run would overwrite the field before this one got back to it.
        NativeFieldGet:
            {
                var native = _pendingField;
                current.IP = ip;
                _sp = sp;

                // The receiver is still on the stack: it is argument 0, and the getter answers in
                // place over it - one slot, which is what a field is.
                _ = native.Getter.Invoke(new SurtrCallArguments(_runtime, sp - 1, 1));

                entities = context.EntityRegistry.Entities;
                if (context.EntityRegistry.GcPending)
                    _runtime.CollectAtSafepoint();
                goto Dispatch;
            }

        NativeFieldSet:
            {
                var native = _pendingField;
                current.IP = ip;
                _sp = sp;

                // Receiver and value are contiguous on the stack: arguments 0 and 1.
                native.Setter.Invoke(new SurtrCallArguments(_runtime, sp - 2, 2));

                sp -= 2;
                _sp = sp;

                entities = context.EntityRegistry.Entities;
                if (context.EntityRegistry.GcPending)
                    _runtime.CollectAtSafepoint();
                goto Dispatch;
            }

        NativeStaticFieldGet:
            {
                var native = _pendingField;
                current.IP = ip;
                _sp = sp;

                // The getter answers in place over the empty argument block: slot 0 is where its
                // single result goes, so the capacity has to reach one past zero arguments.
                _ = native.Getter.Invoke(new SurtrCallArguments(_runtime, sp, 0, 1));
                sp += 1;
                _sp = sp;

                entities = context.EntityRegistry.Entities;
                if (context.EntityRegistry.GcPending)
                    _runtime.CollectAtSafepoint();
                goto Dispatch;
            }

        NativeStaticFieldSet:
            {
                var native = _pendingField;
                current.IP = ip;
                _sp = sp;

                native.Setter.Invoke(new SurtrCallArguments(_runtime, sp - 1, 1));

                sp -= 1;
                _sp = sp;

                entities = context.EntityRegistry.Entities;
                if (context.EntityRegistry.GcPending)
                    _runtime.CollectAtSafepoint();
                goto Dispatch;
            }

        // ---- Shared call sequences ------------------------------------------------------------
        // Reached by goto, never by a call: the operands arrive in the pending* locals, so every
        // call opcode shares one copy of this without paying a call's prologue, epilogue or
        // register spills. The branch on ImplKind is why there is no separate opcode for calling
        // host code - a virtual call can land on a native override, so the test has to exist here
        // regardless, and it predicts perfectly at any one call site.

        // Entering a generator's frame, reached by goto from the three sites that do it: a resume,
        // a delegation, and a delegated-to body ending. All three arrive with `sp` at the frame
        // base and the answer slot at `sp - 1`, and all three have already published `current.IP`.
        // Shared the same way the call sequences are - by jumping, not by calling - because the
        // alternative is three copies of a frame setup that has to stay in step.
        EnterGeneratorFrame:
            {
                var entering = _pendingGenerator;

                int resumeDepth = _frameCount;
                if (resumeDepth == _frames.Length)
                {
                    _sp = sp;
                    throw CallStackOverflow(_frames.Length);
                }

                int generatorLocals = entering.LocalCount;
                int liveSlots = entering.SlotCount;

                if (sp + generatorLocals + entering.MaxStackSize > _stackLimit)
                {
                    _sp = sp;
                    throw DataStackOverflow();
                }

                // The whole of strategy B in four lines: a frame is a flat run of untyped slots, so
                // restoring one is a copy back into the stack at whatever base is free now. Locals
                // keep their indices because every access is frameBase-relative, which is what
                // makes a frame relocatable at all.
                {
                    var slots = entering.Slots;
                    SurtrRawValue* target = sp;
                    for (int i = 0; i < liveSlots; i++)
                        target[i] = slots[i].Raw;

                    // Above the live width sits either a local the body has not written yet or
                    // operand space; both have to read as a zeroed slot rather than as whatever the
                    // last call left on the stack, or a collection would retain it.
                    for (int i = liveSlots; i < generatorLocals; i++)
                        target[i] = 0;
                }

                var generatorChunk = entering.Chunk;
                byte* generatorCodeBase = generatorChunk.Code.Pointer;

                ref SurtrCallFrame generatorFrame = ref _frames[resumeDepth];
                generatorFrame.Base = sp;
                generatorFrame.CodeBase = generatorCodeBase;
                generatorFrame.IP = generatorCodeBase + entering.ResumeOffset;
                generatorFrame.Chunk = generatorChunk;
                generatorFrame.Method = entering.Method;
                generatorFrame.Closure = null;
                generatorFrame.Generator = entering;
                generatorFrame.LocalCount = generatorLocals;
                generatorFrame.ArgumentCount = entering.ArgumentCount;

                // Nothing is returned through the frame protocol: a `yield` writes its value onto
                // the generator and GenCurrent reads it back, so the resumer wants no result slot
                // and ReturnVoid at the end of the body must not push one either.
                generatorFrame.ExpectedResults = 0;

                // A generator body captures nothing, so the roots slot its frame would have used
                // for a closure is free for the generator itself - which is what keeps it alive
                // across the collection its own body may trigger.
                _roots[resumeDepth + 1] = SurtrValue.TagMaskReference | (uint)entering.GetSurtrReference();

                entering.State = SurtrGeneratorState.Running;
                _frameCount = resumeDepth + 1;

                // The operand stack of the resumed frame starts above its locals, and anything it
                // had pending was restored by the copy above.
                _sp = sp + (liveSlots > generatorLocals ? liveSlots : generatorLocals);
                goto LoadFrame;
            }

        InvokeResolved:
            // The operands arrive in the _pending* fields (written by the call opcodes before the
            // jump), but they are copied back into locals the moment the shared sequence starts: a
            // native EntryPoint.Invoke below may re-enter Run() and overwrite the fields, and the
            // shared sequence must keep working with the values the call site meant. The fields
            // only carry state across the goto; these locals have short live ranges and cost the
            // dispatch loop nothing.
            var pendingMethod = _pendingMethod;
            var pendingClosure = _pendingClosure;
            int pendingArguments = _pendingArguments;
            int pendingResults = _pendingResults;

            if (pendingMethod.ImplKind == SurtrMethodImplKind.Native)
            {
                SurtrRawValue* nativeArgumentBase = sp - pendingArguments;

                _sp = sp;
                current.IP = ip;

                // The native answers in place: results overwrite the arguments from slot 0, and
                // the stack pointer simply moves to their end. The encoded retCount stays a gate -
                // zero discards whatever was written; non-zero trusts the callee's own count.
                var nativeArguments = new SurtrCallArguments(
                    _runtime,
                    nativeArgumentBase,
                    pendingArguments,
                    (int)(_stackLimit - nativeArgumentBase));

                int resolvedResults = pendingClosure is null
                    ? ((SurtrNativeMethodInfo)pendingMethod).EntryPoint.Invoke(nativeArguments)
                    : pendingClosure.EntryPoint.Invoke(nativeArguments);

                sp = pendingResults != 0
                    ? nativeArgumentBase + resolvedResults
                    : nativeArgumentBase;
                _sp = sp;

                entities = context.EntityRegistry.Entities;



                // The native boundary is the other safepoint for automatic collection: a host body
                // may have allocated enough to arm the flag, and the machine state is already
                // published above, so the sweep can run here with a consistent stack.
                if (context.EntityRegistry.GcPending)
                    _runtime.CollectAtSafepoint();

                goto Dispatch;
            }

            {
                var target = (SurtrBytecodeMethodInfo)pendingMethod;

                int depth = _frameCount;
                if (depth == _frames.Length)
                {
                    current.IP = ip;
                    _sp = sp;
                    throw CallStackOverflow(_frames.Length);
                }

                SurtrRawValue* newBase = sp - pendingArguments;
                int localCount = target.LocalCount;

                // The only stack-overflow check in the whole interpreter: the callee's own high
                // water mark is known at compile time, so nothing has to be checked per push.
                if (newBase + localCount + target.MaxStackSize > _stackLimit)
                {
                    current.IP = ip;
                    _sp = sp;
                    throw DataStackOverflow();
                }

                // Locals above the incoming arguments are zeroed, so a collection can never read a
                // slot the program has not written and retain whatever the last call left there.
                // The ≤16-byte case is written out rather than calling MemOps.Clear, whose body is
                // deliberately too large to inline (it is shared with the vectorised bulk paths):
                // the call-entry sequence is the hottest frame path and small frames dominate.
                if (localCount > pendingArguments)
                {
                    SurtrRawValue* firstLocal = newBase + pendingArguments;
                    int zeroSlots = localCount - pendingArguments;
                    if (zeroSlots <= 2)
                    {
                        firstLocal[0] = 0;
                        if (zeroSlots == 2)
                            firstLocal[1] = 0;
                    }
                    else
                    {
                        MemOps.Clear(firstLocal, (nuint)zeroSlots * sizeof(SurtrRawValue));
                    }
                }

                current.IP = ip;

                var targetChunk = target.Chunk;
                byte* targetCodeBase = targetChunk.Code.Pointer;

                ref SurtrCallFrame entered = ref _frames[depth];
                entered.Base = newBase;
                entered.CodeBase = targetCodeBase;
                entered.IP = targetCodeBase + target.CodeOffset;
                entered.Chunk = targetChunk;
                entered.Method = target;
                entered.Closure = pendingClosure;
                entered.Generator = null;
                entered.LocalCount = localCount;
                entered.ArgumentCount = pendingArguments;
                entered.ExpectedResults = pendingResults;

                _roots[depth + 1] = pendingClosure is null
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
        /// <summary>The interpreter state a cold helper carries in and out by value, so ip and sp never
        /// get a memory home from crossing a call boundary.</summary>
        private struct HState
        {
            public byte* ip;
            public SurtrRawValue* sp;
            public byte Flow;
        }

        /// <summary>Nullables. Flow: 0 = Dispatch, 1 = Branched.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static HState HandleNullableOp(HState s)
        {
            switch ((OpCode)(*(s.ip - 1)))
            {
                case OpCode.PushAbsent:
                    *s.sp++ = SurtrValue.TagMaskAbsent | *s.ip++;
                    return s;
                case OpCode.IsAbsent:
                    *(s.sp - 1) = SurtrValue.TagMaskBool | (((*(s.sp - 1)) & SurtrValue.TagMask) == SurtrValue.TagMaskAbsent ? 1UL : 0UL);
                    return s;
                case OpCode.IsPresent:
                    *(s.sp - 1) = SurtrValue.TagMaskBool | (((*(s.sp - 1)) & SurtrValue.TagMask) == SurtrValue.TagMaskAbsent ? 0UL : 1UL);
                    return s;
                case OpCode.JPA:
                {
                    short offset = (short)(s.ip[0] | (s.ip[1] << 8));
                    s.ip += 2;
                    s.sp--;
                    if ((*s.sp & SurtrValue.TagMask) == SurtrValue.TagMaskAbsent)
                        s.ip += offset;
                    s.Flow = 1;
                    return s;
                }
                case OpCode.JPNA:
                {
                    short offset = (short)(s.ip[0] | (s.ip[1] << 8));
                    s.ip += 2;
                    s.sp--;
                    if ((*s.sp & SurtrValue.TagMask) != SurtrValue.TagMaskAbsent)
                        s.ip += offset;
                    s.Flow = 1;
                    return s;
                }
            }
            return s;
        }

        /// <summary>Ranges. Flow: 0 = Dispatch, 1 = Safepoint.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private HState HandleRangeOp(HState s, ref SurtrRuntimeEntity?[] entities, ref SurtrCallFrame current, ref SurtrContext context)
        {
            switch ((OpCode)(*(s.ip - 1)))
            {
                case OpCode.RangeNew:
                    *s.sp++ = SurtrValue.TagMaskBool;
                    return s;
                case OpCode.RangeNewInclusive:
                    *s.sp++ = SurtrValue.TagMaskBool | 1UL;
                    return s;
                case OpCode.RangePack:
                {
                    current.IP = s.ip;
                    _sp = s.sp;

                    uint flag = (uint)*--s.sp;
                    uint hi = (uint)*--s.sp;

                    var range = new SurtrRange((int)*(s.sp - 1), (int)hi, (flag & 1UL) != 0UL);
                    SurtrRef reference = context.EntityRegistry.Register(range, out entities);

                    *(s.sp - 1) = SurtrValue.TagMaskReference | (uint)reference;
                    s.Flow = 1;
                    return s;
                }
                case OpCode.RangeUnpack:
                {
                    var range = (SurtrRange)entities[(SurtrRef)(*--s.sp)]!;

                    *s.sp++ = SurtrValue.TagMaskInt | (uint)range.Start;
                    *s.sp++ = SurtrValue.TagMaskInt | (uint)range.End;
                    *s.sp++ = range.IsInclusive ? SurtrValue.TagMaskBool | 1UL : SurtrValue.TagMaskBool;
                    return s;
                }
            }
            return s;
        }

        /// <summary>Module loads, always resume at Dispatch.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private HState HandleModuleOp(HState s, ref SurtrRuntimeEntity?[] entities, ref SurtrCallFrame current, SurtrChunk chunk, ref SurtrContext context)
        {
            switch ((OpCode)(*(s.ip - 1)))
            {
                case OpCode.LoadModule:
                {
                    var target = chunk.ModuleTable[(s.ip[0] | (s.ip[1] << 8))]!;
                    s.ip += 2;
                    current.IP = s.ip;
                    _sp = s.sp;

                    var moduleValue = _runtime.GetOrCreateModuleValue(target);
                    entities = context.EntityRegistry.Entities;
                    *s.sp++ = SurtrValue.TagMaskReference | (uint)moduleValue.GetSurtrReference();
                    return s;
                }
                case OpCode.LoadCurrentModule:
                {
                    current.IP = s.ip;
                    _sp = s.sp;

                    var moduleValue = _runtime.GetOrCreateModuleValue(chunk.OwningModule!);
                    entities = context.EntityRegistry.Entities;
                    *s.sp++ = SurtrValue.TagMaskReference | (uint)moduleValue.GetSurtrReference();
                    return s;
                }
            }
            return s;
        }

        /// <summary>Object construction. Flow: 0 = Dispatch, 1 = Safepoint.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private HState HandleObjectOp(HState s, ref SurtrRuntimeEntity?[] entities, ref SurtrCallFrame current, SurtrChunk chunk, ref SurtrContext context)
        {
            switch ((OpCode)(*(s.ip - 1)))
            {
                case OpCode.ObjNew:
                {
                    var declared = chunk.TypeTable[(s.ip[0] | (s.ip[1] << 8))].ResolvedClass!;
                    s.ip += 2;
                    current.IP = s.ip;
                    _sp = s.sp;

                    // Defense in depth: the binder rejects constructing an abstract class in source,
                    // but raw bytecode (or a frontend without that check) could still ask ObjNew to
                    // allocate one. An abstract class has no concrete layout to build, so reject it
                    // here too rather than hand back a half-made instance.
                    if (declared.IsAbstract)
                        throw AbstractInstantiation(declared.Name);

                    SurtrRef reference = context.EntityRegistry.Register(new SurtrInstance(declared), out entities);

                    *s.sp++ = SurtrValue.TagMaskReference | (uint)reference;
                    s.Flow = 1;
                    return s;
                }
            }
            return s;
        }

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
        private static SurtrExecutionException MissingKey()
            => new SurtrExecutionException("The dictionary holds no entry under that key.", SurtrBuiltIns.KeyNotFoundException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException InvalidCast(string fromName, string toName)
            => new SurtrExecutionException($"A '{fromName}' cannot be cast to '{toName}'.", SurtrBuiltIns.InvalidCastException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException AbstractInstantiation(string className)
            => new SurtrExecutionException($"'{className}' is abstract and cannot be instantiated.", SurtrBuiltIns.InvalidOperationException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException InvalidOpCode(byte opCode)
            => new SurtrExecutionException($"0x{opCode:X2} is not a valid opcode.", SurtrBuiltIns.InvalidOperationException);

        /// <summary>An opcode that has no widened form appeared behind <see cref="OpCode.Wide"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException InvalidWideOpCode(byte opCode)
            => new SurtrExecutionException($"Opcode 0x{opCode:X2} has no wide form.");

        private static SurtrExecutionException InvalidExtOpCode(byte subOpCode)
            => new SurtrExecutionException($"0xFF 0x{subOpCode:X2} is not a valid extended opcode.", SurtrBuiltIns.InvalidOperationException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException GeneratorAlreadyStarted()
            => new SurtrExecutionException(
                "This generator has already been iterated. A generator object is single-use; call the generator function again to start a new one.",
                SurtrBuiltIns.InvalidOperationException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException GeneratorAlreadyRunning()
            => new SurtrExecutionException(
                "This generator is already running: a generator cannot be resumed from inside its own body.",
                SurtrBuiltIns.InvalidOperationException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException GeneratorNotStarted()
            => new SurtrExecutionException(
                "This generator has not started, so there is no suspended 'yield' to send a value to. Resume it once before sending.",
                SurtrBuiltIns.InvalidOperationException);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SurtrExecutionException GeneratorIgnoredClose()
            => new SurtrExecutionException(
                "This generator yielded while it was being disposed: its body caught the close and carried on, so the disposal did not happen.",
                SurtrBuiltIns.InvalidOperationException);

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
