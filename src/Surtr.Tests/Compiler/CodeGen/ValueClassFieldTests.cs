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
    /// Covers value-type fields held inline - in instances, statics and globals: reading and
    /// writing whole blocks, sub-slot reads at absolute offsets, the constructor splice seeding
    /// them, and the collector keeping every reference that lives inside one alive.
    /// </summary>
    public sealed class ValueClassFieldTests : IDisposable
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

        private const string Vec2 = @"
value class Vec2 {
    public let x: float;
    public let y: float;

    public constructor(x: float, y: float) {
        this.x = x;
        this.y = y;
    }
}
";

        /// <summary>A value type carrying two references - the shape every GC test needs.</summary>
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

        /// <summary>
        /// Loads with a <c>forceCollect</c> native bound, so Surtr code can trigger a full
        /// collection from inside its own execution - the harshest point to survive.
        /// </summary>
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

        private static double Float(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => runtime.Invoke(Function(runtime, name), arguments).AsFloat;

        private static int Int(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => runtime.Invoke(Function(runtime, name), arguments).AsInt;

        private static string Text(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => runtime.Resolve<SurtrString>(runtime.Invoke(Function(runtime, name), arguments))!.Text;

        #region Instances

        [Fact]
        public void AClassField_HoldsAValueInline_AndItsSubSlotsReadBack()
        {
            var runtime = Load(Build(Vec2 + @"
class Enemy {
    public var position: Vec2;
    public var hp: int;

    public constructor(px: float, py: float, hp: int) {
        this.position = Vec2(px, py);
        this.hp = hp;
    }
}

fun go(): float {
    let e = Enemy(1.5, 2.5, 10);
    return e.position.x + e.position.y;
}
"));

            Assert.Equal(4.0, Float(runtime, "go"));
        }

        [Fact]
        public void WritingAField_CopiesTheBlock_SoInstancesStayIndependent()
        {
            var runtime = Load(Build(Vec2 + @"
class Enemy {
    public var position: Vec2;

    public constructor(x: float, y: float) {
        this.position = Vec2(x, y);
    }
}

fun go(): float {
    let a = Enemy(1.0, 1.0);
    let b = Enemy(2.0, 2.0);
    b.position = a.position;   // a copy, not an alias
    b.position = Vec2(9.0, 9.0);
    return a.position.x + b.position.x;
}
"));

            Assert.Equal(10.0, Float(runtime, "go"));
        }

        [Fact]
        public void ANestedValueField_FlattensIntoTheHolder()
        {
            var runtime = Load(Build(@"
value class Inner {
    public let n: int;

    public constructor(n: int) {
        this.n = n;
    }
}

value class Outer {
    public let inner: Inner;
    public let tag: int;

    public constructor(inner: Inner, tag: int) {
        this.inner = inner;
        this.tag = tag;
    }
}

class Holder {
    public var payload: Outer;

    public constructor(payload: Outer) {
        this.payload = payload;
    }
}

fun go(): int {
    let h = Holder(Outer(Inner(40), 2));
    return h.payload.inner.n + h.payload.tag;
}
"));

            Assert.Equal(42, Int(runtime, "go"));
        }

        [Fact]
        public void FieldInitializers_SeedValueTypeFields_ThroughTheSplice()
        {
            var runtime = Load(Build(Vec2 + @"
class Anchor {
    public let origin: Vec2 = Vec2(1.0, 2.0);

    public constructor() { }
}

fun go(): float {
    let a = Anchor();
    return a.origin.y;
}
"));

            Assert.Equal(2.0, Float(runtime, "go"));
        }

        [Fact]
        public void TwoInstancesWithEqualValues_CompareStructurallyThroughTheirFields()
        {
            var runtime = Load(Build(Vec2 + @"
class Enemy {
    public var position: Vec2;

    public constructor(x: float, y: float) {
        this.position = Vec2(x, y);
    }
}

fun go(): int {
    let a = Enemy(1.0, 2.0);
    let b = Enemy(1.0, 2.0);
    let c = Enemy(1.0, 3.0);
    return a.position == b.position && a.position != c.position ? 1 : 0;
}
"));

            Assert.Equal(1, Int(runtime, "go"));
        }

        [Fact]
        public void AStatementPositionRead_DiscardsTheWholeBlock()
        {
            var runtime = Load(Build(Vec2 + @"
class Enemy {
    public var position: Vec2;

    public constructor(x: float, y: float) {
        this.position = Vec2(x, y);
    }
}

fun go(): float {
    let e = Enemy(1.0, 2.0);
    e.position;   // discarded: both slots have to leave the stack
    return e.position.y;
}
"));

            Assert.Equal(2.0, Float(runtime, "go"));
        }

        #endregion

        #region Statics and globals

        [Fact]
        public void AStaticValueType_AcceptsWritesAndKeepsItsValueAcrossCalls()
        {
            var runtime = Load(Build(Vec2 + @"
class Config {
    public static var origin: Vec2;
}

fun place(): void {
    Config.origin = Vec2(3.0, 4.0);
}

fun go(): float {
    place();
    return Config.origin.x + Config.origin.y;
}
"));

            Assert.Equal(7.0, Float(runtime, "go"));
        }

        [Fact]
        public void AStaticInitializer_SeedsAValueTypeStatic()
        {
            var runtime = Load(Build(Vec2 + @"
class Config {
    public static let start: Vec2 = Vec2(5.0, 6.0);
}

fun go(): float {
    return Config.start.x * Config.start.y;
}
"));

            Assert.Equal(30.0, Float(runtime, "go"));
        }

        [Fact]
        public void AMoudleLevelVariable_HoldsAnInlineValue()
        {
            var runtime = Load(Build(Vec2 + @"
let home: Vec2 = Vec2(7.0, 8.0);

fun go(): float {
    return home.x + home.y;
}
"));

            Assert.Equal(15.0, Float(runtime, "go"));
        }

        #endregion

        #region The collector

        [Fact]
        public void AReferenceInsideAnInstanceFieldValue_SurvivesACollectionForcedFromNative()
        {
            var runtime = LoadWithCollector(Build(Tag + @"
class Enemy {
    public var tag: Tag;

    public constructor(tag: Tag) {
        this.tag = tag;
    }
}

public native fun forceCollect(): int;

fun label(): string {
    let e = Enemy(Tag(""precious"", array<int>(3, 7)));
    forceCollect();          // a full collection while only `e` names the value
    return e.tag.label;
}

fun score(): int {
    let e = Enemy(Tag(""t"", array<int>(3, 7)));
    forceCollect();
    return e.tag.scores[0] + e.tag.scores[2];
}
"));

            Assert.Equal("precious", Text(runtime, "label"));
            Assert.Equal(14, Int(runtime, "score"));
        }

        [Fact]
        public void AReferenceInsideAStaticValue_SurvivesACollectionForcedFromNative()
        {
            var runtime = LoadWithCollector(Build(Tag + @"
class Registry {
    public static var badge: Tag;
}

public native fun forceCollect(): int;

fun go(): string {
    Registry.badge = Tag(""kept"", array<int>(2, 5));
    forceCollect();
    return Registry.badge.label;
}

fun total(): int {
    return Registry.badge.scores[0] + Registry.badge.scores[1];
}
"));

            Assert.Equal("kept", Text(runtime, "go"));
            Assert.Equal(10, Int(runtime, "total"));
        }

        [Fact]
        public void AHolderCapturedInAClosure_KeepsTheNestedReferencesAlive()
        {
            var runtime = LoadWithCollector(Build(Tag + @"
class Enemy {
    public var tag: Tag;

    public constructor(tag: Tag) {
        this.tag = tag;
    }
}

public native fun forceCollect(): int;

fun go(): string {
    let e = Enemy(Tag(""held"", array<int>(1, 4)));
    let read = () => e.tag.label;
    forceCollect();          // the closure frame is what keeps `e` reachable here
    return read();
}
"));

            Assert.Equal("held", Text(runtime, "go"));
        }

        [Fact]
        public void ARoundTrip_CreateWriteReadCollectRead_HoldsEveryValue()
        {
            var runtime = LoadWithCollector(Build(Tag + @"
class Enemy {
    public var tag: Tag;

    public constructor(tag: Tag) {
        this.tag = tag;
    }
}

public native fun forceCollect(): int;

fun before(): string {
    let e = Enemy(Tag(""round"", array<int>(2, 3)));
    let seen = e.tag.label;   // reads the sub-slot first...
    forceCollect();           // ...then the collection runs...
    return seen + e.tag.label; // ...and the same slot must answer again
}
"));

            Assert.Equal("roundround", Text(runtime, "before"));
        }

        #endregion

        #region Inheritance and erasure pinning

        [Fact]
        public void ABasetypedAccess_ReadsAnInlineFieldOfADerivedInstance()
        {
            var runtime = Load(Build(Vec2 + @"
class Shape {
    public var origin: Vec2;

    public constructor() { }
}

class Labeled : Shape {
    public var spot: Vec2;

    public constructor() { }
}

// Compiled against the base's layout; the derived instance must agree with it.
fun paint(s: Shape): float {
    return s.origin.x + s.origin.y;
}

fun go(): float {
    let l = Labeled();
    l.origin = Vec2(2.0, 3.0);
    l.spot = Vec2(10.0, 20.0);
    return paint(l) + l.spot.x;
}
"));

            Assert.Equal(15.0, Float(runtime, "go"));
        }

        [Fact]
        public void AOneFieldValueField_KeepsItsErasure()
        {
            var runtime = Load(Build(@"
value class EntityId {
    public let raw: int;

    public constructor(raw: int) {
        this.raw = raw;
    }
}

class Token {
    public var id: EntityId;

    public constructor(i: int) {
        this.id = EntityId(i);
    }
}

fun go(): int {
    let t = Token(41);
    return t.id.raw + 1;
}
"));

            Assert.Equal(42, Int(runtime, "go"));
        }

        #endregion
    }
}
