#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Tests.Compiler.CodeGen
{
    /// <summary>
    /// The value-type GC audit: every scenario the inline-layout work has to survive, run under
    /// both collection policies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ValueClassFieldTests"/> proved the same shapes against a collection the program
    /// asks for by name, at a point the test chose. This suite asks the harder question: does a
    /// reference living inside an inline value survive a collector that fires <em>on its own</em>,
    /// at whatever safepoint it reaches first, while the value is mid-flight. Every case runs
    /// twice - once under <see cref="SurtrGcPolicy.Manual"/> with an explicit
    /// <c>forceCollect()</c>, and once under an automatic policy tuned to consider collecting at
    /// <b>every single allocation</b>, which is the harshest schedule the runtime can be given.
    /// </para>
    /// <para>
    /// The two policies are not the same test written twice. A manual collection runs at a native
    /// boundary the program walked into deliberately, with the stack in a shape the test author
    /// pictured. An automatic one is armed by an allocation and taken at the next safepoint, which
    /// lands in the middle of expression evaluation - partway through building a value whose slots
    /// are on the operand stack, between the two halves of a nested constructor, between a block
    /// copy's read and its write. If the reference-slot map of an inline value were wrong, this is
    /// the schedule that finds it.
    /// </para>
    /// <para>
    /// Every case allocates hard enough that the automatic collector really runs: an arming
    /// allocation alone proves nothing, so each body builds throwaway objects in a loop around the
    /// value under test. Assertions read <em>through</em> the references an inline value holds -
    /// an element out of its array, the length or the text of its string - because a swept-out
    /// reference does not announce itself, it answers wrongly or faults.
    /// </para>
    /// </remarks>
    public sealed class ValueTypeGcAuditTests : IDisposable
    {
        private const string Root = "D:/proj/src";

        private readonly List<IDisposable> _owned = new List<IDisposable>();

        private static SurtrRuntime? _collectorTarget;

        public void Dispose()
        {
            _collectorTarget = null;

            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
        }

        /// <summary>
        /// The two schedules every case runs under. <c>true</c> arms the automatic collector at
        /// every allocation, which makes the program's own <c>forceCollect()</c> one more
        /// collection among very many rather than the only one.
        /// </summary>
        public static TheoryData<bool> Policies => new TheoryData<bool> { false, true };

        /// <summary>
        /// A value type carrying two references - one string, one array. <c>array&lt;int&gt;(n, v)</c>
        /// is <c>n</c> elements all equal to <c>v</c>, so a score read at any index answers <c>v</c>
        /// and an assertion never depends on which index a test happened to pick.
        /// </summary>
        private const string Tag = @"
value class Tag {
    public let label: string;
    public let scores: int[];

    public constructor(label: string, scores: int[]) {
        this.label = label;
        this.scores = scores;
    }
}
";

        /// <summary>
        /// A value type whose fields are themselves value types, so its reference slots come from
        /// the nested layout rather than from its own fields. This is the shape the flattening
        /// walk gets wrong if it gets anything wrong.
        /// </summary>
        private const string Pair = Tag + @"
value class Pair {
    public let first: Tag;
    public let second: Tag;

    public constructor(first: Tag, second: Tag) {
        this.first = first;
        this.second = second;
    }
}
";

        /// <summary>Allocation pressure, written once and pasted into every case that needs it.</summary>
        private const string Churn = @"
class Junk {
    public var a: int;
    public var b: string;
    public constructor(a: int) { this.a = a; this.b = ""junk""; }
}

// Enough allocation to cross any threshold and reach many safepoints. The result is used by every
// caller so nothing about the loop can be folded away.
fun churn(n: int): int {
    var acc: int = 0;
    for (var i = 0; i < n; i += 1) {
        let j = Junk(i);
        let xs: int[] = [i, i + 1];
        acc = (acc + j.a + xs[1]) % 1000003;
    }
    return acc;
}

public native fun forceCollect(): int;
";

        private ModuleEmitter Build(string source)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            var compilation = SurtrCompilation.Create(project);
            _owned.Add(compilation);

            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(
                !compilation.HasErrors,
                "Binding reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            var emitter = new ModuleEmitter(compilation, binder);

            Assert.True(
                emitter.TryEmit(),
                "Emission reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.Message)));

            return emitter;
        }

        /// <summary>
        /// Loads under the requested schedule. The automatic policy considers collecting after a
        /// single allocation and sweeps everything each time - deliberately far past anything a
        /// real host would configure, because the point is the worst case rather than a plausible
        /// one.
        /// </summary>
        private SurtrRuntime Load(string source, bool automatic)
        {
            var runtime = new SurtrRuntime();
            _owned.Add(runtime);
            _collectorTarget = runtime;

            runtime.ConfigureGc(automatic
                ? new SurtrGcPolicy(SurtrGcMode.Automatic, allocationThreshold: 1, liveEntityThresholdPercent: 0, nurseryFrequency: 1)
                : SurtrGcPolicy.Manual);

            runtime.DefineNativeBody("game.core.Test.forceCollect", SurtrNativeEntryPoint.FromDelegate(ForceCollectNow));

            foreach (var module in Build(source).Modules)
                runtime.LoadModule(module);

            return runtime;
        }

        // A named static method, not a lambda: FromDelegate requires a static target, and this one
        // reaches the owning test's runtime through the shared field above.
        private static int ForceCollectNow(SurtrCallArguments arguments)
        {
            _collectorTarget!.Collect();
            return arguments.Return(SurtrValue.CreateInt(1));
        }

        private static SurtrMethodInfo Function(SurtrRuntime runtime, string name)
        {
            Assert.True(runtime.TryGetModule("game.core.Test", out var module), "No module was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"The module declares no '{name}'.");
            return overloads[0];
        }

        private static int Int(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => runtime.Invoke(Function(runtime, name), arguments).AsInt;

        private static string Text(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => runtime.Resolve<SurtrString>(runtime.Invoke(Function(runtime, name), arguments))!.Text;

        #region The harness itself

        [Theory]
        [MemberData(nameof(Policies))]
        public void TheAutomaticSchedule_ReallyCollects_WhileTheManualOneOnlyCollectsWhenAsked(bool automatic)
        {
            // The guard on every case below. A GC audit that could pass without the collector ever
            // running is not an audit, so this pins the harness: the same `churn` every other case
            // calls has to produce hundreds of collections under the automatic policy, and exactly
            // the ones the program asks for under the manual one. Since all cases share `Load` and
            // all of them churn at least this hard, proving it here proves it for all of them.
            var runtime = Load(Churn + @"
fun go(): int {
    return churn(400);
}
", automatic);

            Assert.Equal(0, runtime.TotalCollections);

            Int(runtime, "go");

            if (automatic)
                Assert.True(runtime.TotalCollections > 100, $"The automatic policy collected only {runtime.TotalCollections} time(s).");
            else
                Assert.Equal(0, runtime.TotalCollections);
        }

        #endregion

        #region A value in a local, on the stack

        [Theory]
        [MemberData(nameof(Policies))]
        public void AReferenceInALocalValue_SurvivesCollectionsThroughout(bool automatic)
        {
            var runtime = Load(Tag + Churn + @"
fun score(): int {
    let t = Tag(""local"", array<int>(4, 9));
    churn(300);
    forceCollect();
    churn(300);
    return t.scores[3] * 100 + t.label.length;
}

fun label(): string {
    let t = Tag(""local"", array<int>(4, 9));
    churn(300);
    forceCollect();
    return t.label;
}
", automatic);

            Assert.Equal(905, Int(runtime, "score"));
            Assert.Equal("local", Text(runtime, "label"));
        }

        [Theory]
        [MemberData(nameof(Policies))]
        public void AValueMidFlightOnTheOperandStack_SurvivesACollectionInsideTheExpression(bool automatic)
        {
            // The Tag is built, then churn runs and collects while the Tag's slots are still on the
            // operand stack as a pending argument and no local names them. This is the case a
            // manual collection cannot reach: nothing in the program chose this moment. `spin` is
            // read so the churn cannot be folded away, but its value never reaches the result.
            var runtime = Load(Tag + Churn + @"
fun read(t: Tag, spin: int): int {
    if (spin < 0) { return -1; }
    return t.scores[0] * 100 + t.label.length;
}

fun go(): int {
    return read(Tag(""flight"", array<int>(6, 1)), churn(400));
}
", automatic);

            Assert.Equal(106, Int(runtime, "go"));
        }

        [Theory]
        [MemberData(nameof(Policies))]
        public void AValueReturnedAcrossAFrameBoundary_KeepsItsReferences(bool automatic)
        {
            var runtime = Load(Tag + Churn + @"
fun make(seed: int): Tag {
    return Tag(""made"", array<int>(3, seed));
}

fun go(): int {
    let t = make(2);
    churn(400);
    let u = make(5);
    forceCollect();
    return t.scores[0] * 100 + u.scores[2] * 10 + t.label.length;
}
", automatic);

            Assert.Equal(254, Int(runtime, "go"));
        }

        #endregion

        #region A value in an instance field

        [Theory]
        [MemberData(nameof(Policies))]
        public void AReferenceInsideAnInstanceFieldValue_Survives(bool automatic)
        {
            var runtime = Load(Tag + Churn + @"
class Enemy {
    public var tag: Tag;
    public constructor(tag: Tag) { this.tag = tag; }
}

fun label(): string {
    let e = Enemy(Tag(""field"", array<int>(3, 7)));
    churn(400);
    forceCollect();
    return e.tag.label;
}

fun score(): int {
    let e = Enemy(Tag(""field"", array<int>(3, 7)));
    churn(400);
    forceCollect();
    return e.tag.scores[2];
}
", automatic);

            Assert.Equal("field", Text(runtime, "label"));
            Assert.Equal(7, Int(runtime, "score"));
        }

        [Theory]
        [MemberData(nameof(Policies))]
        public void RewritingAnInstanceFieldValue_LeavesNoStaleReferenceBehind(bool automatic)
        {
            // The first Tag's array becomes unreachable the moment the field is overwritten, and a
            // collection under this policy will take it. What must survive is only the second.
            var runtime = Load(Tag + Churn + @"
class Enemy {
    public var tag: Tag;
    public constructor(tag: Tag) { this.tag = tag; }
}

fun go(): string {
    let e = Enemy(Tag(""first"", array<int>(2, 1)));
    churn(200);
    e.tag = Tag(""second"", array<int>(3, 8));
    churn(200);
    forceCollect();
    return e.tag.label;
}

fun score(): int {
    let e = Enemy(Tag(""first"", array<int>(2, 1)));
    churn(200);
    e.tag = Tag(""second"", array<int>(3, 8));
    churn(200);
    forceCollect();
    return e.tag.scores[0];
}
", automatic);

            Assert.Equal("second", Text(runtime, "go"));
            Assert.Equal(8, Int(runtime, "score"));
        }

        [Theory]
        [MemberData(nameof(Policies))]
        public void ManyInstancesEachHoldingAValue_AllKeepTheirOwnReferences(bool automatic)
        {
            // A hundred live holders, each with its own inline Tag, with churn between them: the
            // walk has to visit every instance's nested reference slots, not just the last one's.
            var runtime = Load(Tag + Churn + @"
class Enemy {
    public var tag: Tag;
    public constructor(tag: Tag) { this.tag = tag; }
}

fun go(): int {
    let all: Enemy[] = [];
    for (var i = 0; i < 100; i += 1) {
        all.push(Enemy(Tag(""e"", array<int>(2, i))));
        churn(20);
    }
    forceCollect();
    var acc: int = 0;
    for (var i = 0; i < all.length; i += 1) {
        acc = acc + all[i].tag.scores[1] + all[i].tag.label.length;
    }
    return acc;
}
", automatic);

            // scores[1] is i for each of 0..99, plus one character of label each.
            Assert.Equal(4950 + 100, Int(runtime, "go"));
        }

        #endregion

        #region A value in a static

        [Theory]
        [MemberData(nameof(Policies))]
        public void AReferenceInsideAStaticValue_Survives(bool automatic)
        {
            var runtime = Load(Tag + Churn + @"
class Registry {
    public static var badge: Tag;
}

fun seed(): int {
    Registry.badge = Tag(""static"", array<int>(2, 5));
    churn(400);
    forceCollect();
    return 0;
}

fun read(): int {
    churn(400);
    return Registry.badge.scores[1] * 100 + Registry.badge.label.length;
}
", automatic);

            Assert.Equal(0, Int(runtime, "seed"));
            Assert.Equal(506, Int(runtime, "read"));
        }

        [Theory]
        [MemberData(nameof(Policies))]
        public void AModuleLevelValueVariable_Survives(bool automatic)
        {
            var runtime = Load(Tag + Churn + @"
let current: Tag = Tag(""module"", array<int>(9, 4));

fun go(): int {
    churn(400);
    forceCollect();
    churn(400);
    return current.scores[8] * 100 + current.label.length;
}
", automatic);

            Assert.Equal(406, Int(runtime, "go"));
        }

        #endregion

        #region Nesting, boxing and tuples

        [Theory]
        [MemberData(nameof(Policies))]
        public void ANestedValueTypesReferences_AllSurvive(bool automatic)
        {
            // Pair's own fields are values, so all four of its reference slots come from the
            // flattening walk. Every one of them is read back after collection.
            var runtime = Load(Pair + Churn + @"
class Holder {
    public var pair: Pair;
    public constructor(pair: Pair) { this.pair = pair; }
}

fun go(): int {
    let h = Holder(Pair(Tag(""a"", array<int>(2, 1)), Tag(""bb"", array<int>(2, 4))));
    churn(400);
    forceCollect();
    return h.pair.first.scores[1] * 1000
         + h.pair.second.scores[0] * 100
         + h.pair.first.label.length * 10
         + h.pair.second.label.length;
}
", automatic);

            Assert.Equal(1412, Int(runtime, "go"));
        }

        [Theory]
        [MemberData(nameof(Policies))]
        public void AValueBoxedIntoAnErasedSlot_KeepsItsReferencesThroughTheRoundTrip(bool automatic)
        {
            // Into an erased slot the value has to become a real instance; out of it, back to
            // slots. Both halves happen with the collector armed.
            var runtime = Load(Tag + Churn + @"
class Box<T> {
    private let _value: T;
    public constructor(value: T) { _value = value; }
    public fun get(): T { return _value; }
}

fun go(): int {
    let b = Box<Tag>(Tag(""boxed"", array<int>(3, 7)));
    churn(400);
    forceCollect();
    let t = b.get();
    churn(400);
    return t.scores[0] * 100 + t.label.length;
}
", automatic);

            Assert.Equal(705, Int(runtime, "go"));
        }

        [Theory]
        [MemberData(nameof(Policies))]
        public void ValuesStoredAsArrayElements_SurviveTheirBoxedForm(bool automatic)
        {
            // An array element holds a reference by definition, so each Tag is boxed on the way in
            // and unboxed on the way out - and the box is what the collector has to keep.
            var runtime = Load(Tag + Churn + @"
fun go(): int {
    let tags: Tag[] = [];
    for (var i = 0; i < 50; i += 1) {
        tags.push(Tag(""t"", array<int>(2, i)));
        churn(20);
    }
    forceCollect();
    var acc: int = 0;
    for (var i = 0; i < tags.length; i += 1) {
        acc = acc + tags[i].scores[1] + tags[i].label.length;
    }
    return acc;
}
", automatic);

            // scores[1] is i for each of 0..49, plus one character of label each.
            Assert.Equal(1225 + 50, Int(runtime, "go"));
        }

        [Theory]
        [MemberData(nameof(Policies))]
        public void ATupleHoldingReferences_SurvivesInlineAndThroughAReturn(bool automatic)
        {
            // A tuple is a value type on the same mechanism, so its reference elements are inline
            // slots in the frame - not an object the collector can see by name.
            var runtime = Load(Churn + @"
fun make(seed: int): (string, int[]) {
    return (""tuple"", array<int>(3, seed));
}

fun go(): int {
    let (label, scores) = make(6);
    churn(400);
    forceCollect();
    churn(400);
    return scores[2] * 100 + label.length;
}
", automatic);

            Assert.Equal(605, Int(runtime, "go"));
        }

        [Theory]
        [MemberData(nameof(Policies))]
        public void AValueClosedOverByALambda_KeepsWhatItHolds(bool automatic)
        {
            var runtime = Load(Tag + Churn + @"
class Enemy {
    public var tag: Tag;
    public constructor(tag: Tag) { this.tag = tag; }
}

fun go(): int {
    let e = Enemy(Tag(""closed"", array<int>(5, 2)));
    let read = () => e.tag.scores[4] * 100 + e.tag.label.length;
    churn(400);
    forceCollect();
    churn(400);
    return read();
}
", automatic);

            Assert.Equal(206, Int(runtime, "go"));
        }

        #endregion

        #region The host boundary

        [Theory]
        [MemberData(nameof(Policies))]
        public void AValueCrossingTheHostBoundary_ArrivesRepackedAndIntact(bool automatic)
        {
            // SurtrRuntime.Invoke re-packs a multi-slot result into an object on the way out, and
            // that object is allocated with the collector armed. Reading it back proves the
            // packing happened before anything could sweep what it was packing.
            var runtime = Load(Churn + @"
fun make(): (string, int[]) {
    churn(400);
    return (""host"", array<int>(2, 8));
}
", automatic);

            var result = runtime.Invoke(Function(runtime, "make"), Array.Empty<SurtrValue>());
            var tuple = runtime.Resolve<SurtrTuple>(result);

            Assert.NotNull(tuple);
            Assert.Equal("host", runtime.Resolve<SurtrString>(tuple![0])!.Text);

            var scores = runtime.Resolve<SurtrArray>(tuple[1]);
            Assert.NotNull(scores);
            Assert.Equal(8, scores![1].AsInt);
        }

        #endregion
    }
}
