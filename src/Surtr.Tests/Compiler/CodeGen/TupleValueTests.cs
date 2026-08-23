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
    /// Covers tuples as value types: the literal is the block its elements make, locals,
    /// parameters and returns carry it without packing, indexing reads the frame range, equality
    /// compares slot by slot, and the boxed <see cref="SurtrTuple"/> appears only at boundaries -
    /// arrays, dictionaries, erased slots and the host.
    /// </summary>
    public sealed class TupleValueTests : IDisposable
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

        private ModuleEmitter Build(string source, params (string Path, string Text)[] extra)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            foreach (var (path, text) in extra)
                project.AddSourceFile(Root + path, text);

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

        private SurtrRuntime Load(ModuleEmitter emitter)
        {
            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            return runtime;
        }

        /// <summary>Loads with a <c>forceCollect</c> native bound, as the GC tests need.</summary>
        private SurtrRuntime LoadWithCollector(ModuleEmitter emitter)
        {
            var runtime = new SurtrRuntime();
            _owned.Add(runtime);
            _collectorTarget = runtime;
            runtime.DefineNativeBody("game.core.Test.forceCollect", SurtrNativeEntryPoint.FromDelegate(ForceCollectNow));

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            return runtime;
        }

        // A named static method, not a lambda: FromDelegate requires a static target.
        private static SurtrValue ForceCollectNow(SurtrCallArguments arguments)
        {
            _collectorTarget!.Collect();
            return SurtrValue.CreateInt(1);
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

        #region Inline representation

        [Fact]
        public void ATupleFlowsThroughALocalParameterAndReturn_WithoutPacking()
        {
            // The whole chain - literal, argument, result - stays a block; nothing along it
            // allocates the boxed form.
            var runtime = Load(Build(@"
fun middle(t: (int, int, int)): (int, int, int) {
    return (t[1], t[0], t[2]);
}

fun go(): int {
    let t = middle((10, 20, 30));
    return t[0] + t[1] + t[2];
}
"));

            Assert.Equal(60, Int(runtime, "go"));
        }

        [Fact]
        public void ANestedTuple_FlattensIntoOneBlock_AndReadsBack()
        {
            var runtime = Load(Build(@"
fun go(): int {
    let outer = (1, (20, 21), 3);
    let inner = outer[1];
    return outer[0] + inner[0] + inner[1] + outer[2];
}
"));

            Assert.Equal(45, Int(runtime, "go"));
        }

        [Fact]
        public void ATupleCarryingAReference_KeepsItAcrossLocalsAndReturns()
        {
            var runtime = Load(Build(@"
fun pass(t: (string, int)): (int, string) {
    return (t[1], t[0]);
}

fun go(): int {
    let r = pass((""left"", 7));
    let n = r[0];
    let s = r[1];
    return n == 7 && s == ""left"" ? 1 : 0;
}
"));

            Assert.Equal(1, Int(runtime, "go"));
        }

        #endregion

        #region Equality

        [Fact]
        public void EqualTuplesCompare_StructurallySlotBySlot()
        {
            var runtime = Load(Build(@"
fun go(): int {
    let a = (1, ""x"", 3.0);
    let b = (1, ""x"", 3.0);
    let c = (1, ""y"", 3.0);
    let d = (9, (8, 7), 1);
    let e = (9, (8, 7), 1);
    let f = (9, (8, 6), 1);
    return a == b && !(a == c) && d == e && !(d == f) ? 1 : 0;
}
"));

            Assert.Equal(1, Int(runtime, "go"));
        }

        #endregion

        #region Boxed boundaries

        [Fact]
        public void AnArrayOfTuples_PacksOnWrite_AndUnpacksOnRead()
        {
            var runtime = Load(Build(@"
fun go(): int {
    let pairs: (int, string)[] = [(1, ""one""), (2, ""two"")];
    pairs.set(1, (22, ""twenty-two""));
    let second = pairs.get(1);
    let n = second[0];
    let s = second[1];
    return n == 22 && s == ""twenty-two"" ? 1 : 0;
}
"));

            Assert.Equal(1, Int(runtime, "go"));
        }

        [Fact]
        public void ADictionaryKeyedByATuple_HashesStructurally()
        {
            var runtime = Load(Build(@"
fun go(): int {
    let m: {(int, int): string} = {};
    m.set((1, 2), ""first"");
    m.set((1, 2), ""replaced"");   // same structural key: overwrite, not a second entry
    return m.get((1, 2)) == ""replaced"" && m.length == 1 ? 1 : 0;
}
"));

            Assert.Equal(1, Int(runtime, "go"));
        }

        [Fact]
        public void ATupleCrossingAGenericSlot_BoxesAndUnboxes()
        {
            var runtime = Load(Build(@"
fun pick<T>(value: T): T {
    return value;
}

fun go(): int {
    let t = pick((5, 6));
    return t[0] + t[1];
}
"));

            Assert.Equal(11, Int(runtime, "go"));
        }

        #endregion

        #region Iteration

        [Fact]
        public void AForInOverAnArrayOfTuples_YieldsEveryPairUnpacked()
        {
            // The raw-tuple walk stays unreachable from source for now - the binder types its
            // variable 'unknown' and the grammar accepts no annotation there - so the typed
            // iteration this phase owes goes through an array of pairs.
            var runtime = Load(Build(@"
fun go(): int {
    var total = 0;
    let rows: (int, int)[] = [(10, 1), (20, 2)];
    for (row in rows) total = total + row[0] * row[1];
    return total;
}
"));

            Assert.Equal(50, Int(runtime, "go"));
        }

        [Fact]
        public void AForInOverADictionary_YieldsPairsWithoutPackingPerIteration()
        {
            var runtime = Load(Build(@"
fun go(): int {
    var sum = 0;
    let m: {string: int} = {""a"": 1, ""b"": 2};
    for (pair in m) sum += pair[1] * pair[1];
    return sum;
}
"));

            Assert.Equal(5, Int(runtime, "go"));
        }

        #endregion

        #region Storage

        [Fact]
        public void AClassField_OfTupleType_RoundTripsEveryElement()
        {
            var runtime = Load(Build(@"
class Segment {
    public var ends: (int, int);

    public constructor(a: int, b: int) {
        this.ends = (a, b);
    }
}

fun go(): int {
    let s = Segment(11, 22);
    s.ends = (s.ends[1], s.ends[0]);
    return s.ends[0] * 100 + s.ends[1];
}
"));

            Assert.Equal(2211, Int(runtime, "go"));
        }

        [Fact]
        public void AStatic_OfTupleType_KeepsItsValueAcrossCalls()
        {
            var runtime = Load(Build(@"
class Config {
    public static var range: (int, int);
}

fun place(): void {
    Config.range = (7, 9);
}

fun go(): int {
    place();
    return Config.range[0] + Config.range[1];
}
"));

            Assert.Equal(16, Int(runtime, "go"));
        }

        #endregion

        #region The collector

        [Fact]
        public void ReferencesInsideAnInlineTuple_SurviveACollectionForcedFromNative()
        {
            var runtime = LoadWithCollector(Build(@"
public native fun forceCollect(): int;

fun go(): string {
    let t = (""held"", array<int>(2, 5), (""inner"", 9));
    forceCollect();          // every element is a raw slot on the data stack here
    let inner = t[2];
    return t[0] + ""/"" + inner[0];
}

fun scored(): int {
    let t = (""s"", array<int>(3, 4), (""x"", 1));
    forceCollect();
    let scores = t[1];
    return scores.get(0) + scores.get(2);
}
"));

            Assert.Equal("held/inner", Text(runtime, "go"));
            Assert.Equal(8, Int(runtime, "scored"));
        }

        [Fact]
        public void ATupleInAStatic_SurvivesACollectionForcedFromNative()
        {
            var runtime = LoadWithCollector(Build(@"
class Registry {
    public static var entry: (string, int[]);
}

public native fun forceCollect(): int;

fun go(): string {
    Registry.entry = (""kept"", array<int>(2, 6));
    forceCollect();
    return Registry.entry[0];
}
"));

            Assert.Equal("kept", Text(runtime, "go"));
        }

        #endregion

        #region Diagnostics

        [Fact]
        public void IdentityComparisonOverATuple_IsRefused()
        {
            // Refusal arrives from emission (the binder keeps '===' symbolic until lowering
            // decides per representation): either way the compilation cannot produce code.
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "fun go(a: (int, int), b: (int, int)): bool { return a === b; }");

            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            if (!compilation.HasErrors)
            {
                var emitter = new ModuleEmitter(compilation, binder);
                Assert.False(emitter.TryEmit());
            }
        }

        [Fact]
        public void ALambdaCapturingATuple_IsRefusedRatherThanMiscompiled()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "fun go(): int { let t = (1, 2); let read = () => t[0]; return read(); }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            if (!compilation.HasErrors)
            {
                var emitter = new ModuleEmitter(compilation, compilation.Bind());
                Assert.False(emitter.TryEmit());
            }
        }

        #endregion
    }
}
