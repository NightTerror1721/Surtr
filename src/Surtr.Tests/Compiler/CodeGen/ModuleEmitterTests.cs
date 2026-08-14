#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Compiler.Binding;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using Surtr.Runtime;
using Surtr.Runtime.Classes;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Tests.Compiler.CodeGen
{
    /// <summary>
    /// Covers Step 5 end to end: Surtr source becomes a real module, is loaded into a real runtime,
    /// and is run.
    /// </summary>
    /// <remarks>
    /// Nothing here stops at the bytecode. A test that asserted on an instruction sequence would
    /// pin the encoding rather than the meaning, and the encoding is the emitter's to choose — what
    /// has to hold is that the program computes what the source says it computes.
    /// </remarks>
    public sealed class ModuleEmitterTests : IDisposable
    {
        private const string Root = "D:/proj/src";

        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public void Dispose()
        {
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
                "Emission reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

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

        private SurtrRuntime Run(string source, params (string Path, string Text)[] extra) => Load(Build(source, extra));

        private static SurtrMethodInfo Function(SurtrRuntime runtime, string modulePath, string name)
        {
            Assert.True(runtime.TryGetModule(modulePath, out var module), $"No module '{modulePath}' was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"'{modulePath}' declares no '{name}'.");
            return overloads[0];
        }

        private static SurtrValue Call(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => runtime.Invoke(Function(runtime, "game.core", name), arguments);

        private static int Int(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => Call(runtime, name, arguments).AsInt;

        private static string Text(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
            => runtime.Resolve<SurtrString>(Call(runtime, name, arguments))!.Text;

        #region A whole module
        [Fact]
        public void AModuleLevelFunctionRunsWhatItsSourceSays()
        {
            var runtime = Run("fun square(x: int): int { return x * x; }");
            Assert.Equal(49, Int(runtime, "square", SurtrValue.CreateInt(7)));
        }

        [Fact]
        public void OneFunctionCallsAnother()
        {
            var runtime = Run(
                "fun square(x: int): int { return x * x; }\n"
                    + "fun sumOfSquares(a: int, b: int): int { return square(a) + square(b); }");

            Assert.Equal(25, Int(runtime, "sumOfSquares", SurtrValue.CreateInt(3), SurtrValue.CreateInt(4)));
        }

        [Fact]
        public void AModuleVariableIsInitialisedByTheModulesOwnInitializer()
        {
            var runtime = Run("var counter: int = 41;\nfun bump(): int { counter = counter + 1; return counter; }");
            Assert.Equal(42, Int(runtime, "bump"));
        }

        [Fact]
        public void AModuleIsWrittenAsAnImageAndReadBack()
        {
            var emitter = Build("fun answer(): int { return 42; }");
            var images = emitter.EmitImages();

            Assert.Single(images);

            // A fresh runtime, from bytes alone: what makes the image the artefact rather than the
            // in-memory module.
            var reloaded = SurtrModuleImage.FromBytes(images[0].ToBytes());
            using var runtime = new SurtrRuntime();
            runtime.LoadModule(reloaded.Instantiate());

            Assert.Equal(42, runtime.Invoke(Function(runtime, "game.core", "answer"), Array.Empty<SurtrValue>()).AsInt);
        }

        [Fact]
        public void AModuleReachesAnotherOneItDependsOn()
        {
            var runtime = Run(
                "import game.math.*;\nfun run(): int { return twice(21); }",
                ("/game/math/Math.surtr", "fun twice(x: int): int { return x + x; }"));

            Assert.Equal(42, Int(runtime, "run"));
        }
        #endregion

        #region Classes
        [Fact]
        public void AClassIsConstructedAndItsFieldsRead()
        {
            var runtime = Run(
                "class Point {\n"
                    + "  public var x: int;\n"
                    + "  public var y: int;\n"
                    + "  public constructor(x: int, y: int) { this.x = x; this.y = y; }\n"
                    + "  public fun sum(): int { return this.x + this.y; }\n"
                    + "}\n"
                    + "fun run(): int { let p = Point(3, 4); return p.sum(); }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AnInstanceFieldInitializerRunsFromEveryConstructor()
        {
            var runtime = Run(
                "class Counter {\n"
                    + "  public var value: int = 10;\n"
                    + "  public constructor() { this.value = this.value + 5; }\n"
                    + "}\n"
                    + "fun run(): int { return Counter().value; }");

            Assert.Equal(15, Int(runtime, "run"));
        }

        [Fact]
        public void AClassWithInitializersAndNoConstructorStillGetsThem()
        {
            var runtime = Run(
                "class Defaults { public var value: int = 7; }\nfun run(): int { return Defaults().value; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AStaticFieldIsInitialisedBeforeAnythingReadsIt()
        {
            var runtime = Run(
                "class Config { public static var limit: int = 99; }\nfun run(): int { return Config.limit; }");

            Assert.Equal(99, Int(runtime, "run"));
        }

        [Fact]
        public void AnAutoPropertyReadsAndWritesItsBackingField()
        {
            var runtime = Run(
                "class Player {\n"
                    + "  public health: int { get; set; }\n"
                    + "}\n"
                    + "fun run(): int { let p = Player(); p.health = 33; return p.health; }");

            Assert.Equal(33, Int(runtime, "run"));
        }

        [Fact]
        public void AWrittenAccessorBodyIsWhatRuns()
        {
            var runtime = Run(
                "class Box {\n"
                    + "  public var raw: int = 4;\n"
                    + "  public doubled: int { get { return this.raw * 2; } }\n"
                    + "}\n"
                    + "fun run(): int { return Box().doubled; }");

            Assert.Equal(8, Int(runtime, "run"));
        }

        [Fact]
        public void AVirtualCallLandsOnTheOverride()
        {
            var runtime = Run(
                "class Shape { public virtual fun sides(): int { return 0; } }\n"
                    + "class Square : Shape { public override fun sides(): int { return 4; } }\n"
                    + "fun run(): int { let s: Shape = Square(); return s.sides(); }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void ASuperCallReachesTheBaseImplementation()
        {
            var runtime = Run(
                "class Shape { public virtual fun sides(): int { return 3; } }\n"
                    + "class Square : Shape { public override fun sides(): int { return super.sides() + 1; } }\n"
                    + "fun run(): int { return Square().sides(); }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void AnInterfaceCallResolvesThroughTheDispatchTable()
        {
            var runtime = Run(
                "interface Named { fun name(): string; }\n"
                    + "class Hero : Named { public override fun name(): string { return \"hero\"; } }\n"
                    + "fun run(): string { let n: Named = Hero(); return n.name(); }");

            Assert.Equal("hero", Text(runtime, "run"));
        }
        #endregion

        #region Enums
        [Fact]
        public void AnEnumCaseIsAStaticInstanceTheInitializerBuilt()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                    + "fun run(): bool { return Suit.Hearts === Suit.Hearts; }");

            Assert.True(Call(runtime, "run").AsBool);
        }

        [Fact]
        public void TwoEnumCasesAreDifferentInstances()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                    + "fun run(): bool { return Suit.Hearts === Suit.Spades; }");

            Assert.False(Call(runtime, "run").AsBool);
        }

        [Fact]
        public void AnEnumCaseCarriesItsConstructorArguments()
        {
            var runtime = Run(
                "enum Suit {\n"
                    + "  Hearts(1), Spades(4);\n"
                    + "  public let rank: int;\n"
                    + "  public constructor(rank: int) { this.rank = rank; }\n"
                    + "}\n"
                    + "fun run(): int { return Suit.Spades.rank; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void ASwitchOverAnEnumMatchesByCase()
        {
            var runtime = Run(
                "enum Suit { Hearts, Spades }\n"
                    + "fun rank(s: Suit): int { switch (s) { case Suit.Hearts: return 1; case Suit.Spades: return 4; } return 0; }\n"
                    + "fun run(): int { return rank(Suit.Spades); }");

            Assert.Equal(4, Int(runtime, "run"));
        }
        #endregion

        #region The lowerings Step 5 owed
        [Fact]
        public void ALambdaBecomesAClosureOverALiftedFunction()
        {
            var runtime = Run(
                "fun run(): int { let add = (a: int, b: int) => a + b; return add(2, 3); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void ALambdaCapturesByValue()
        {
            var runtime = Run(
                "fun run(): int { let base = 40; let bump = (x: int) => x + base; return bump(2); }");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void ALambdaInsideAMethodCapturesTheReceiver()
        {
            var runtime = Run(
                "class Adder {\n"
                    + "  public var offset: int = 10;\n"
                    + "  public fun make(): (int) -> int { return (x: int) => x + this.offset; }\n"
                    + "}\n"
                    + "fun run(): int { let f = Adder().make(); return f(5); }");

            Assert.Equal(15, Int(runtime, "run"));
        }

        [Fact]
        public void ATryCatchRunsItsHandler()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  try { throw Exception(\"boom\"); }\n"
                    + "  catch (e: Exception) { return 1; }\n"
                    + "  return 0;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AFinallyRunsOnTheNormalPath()
        {
            var runtime = Run(
                "var trace: int = 0;\n"
                    + "fun run(): int {\n"
                    + "  try { trace = trace + 1; }\n"
                    + "  finally { trace = trace + 10; }\n"
                    + "  return trace;\n"
                    + "}");

            Assert.Equal(11, Int(runtime, "run"));
        }

        [Fact]
        public void AFinallyRunsBeforeAReturnInsideTheTry()
        {
            var runtime = Run(
                "var trace: int = 0;\n"
                    + "fun body(): int { try { return 1; } finally { trace = 7; } }\n"
                    + "fun run(): int { let r = body(); return r + trace; }");

            Assert.Equal(8, Int(runtime, "run"));
        }

        [Fact]
        public void AFinallyRunsWhenTheTryThrowsAndNothingCatches()
        {
            var runtime = Run(
                "var trace: int = 0;\n"
                    + "fun risky(): void { try { throw Exception(\"boom\"); } finally { trace = 5; } }\n"
                    + "fun run(): int { try { risky(); } catch (e: Exception) { } return trace; }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void ASafeCastYieldsNullWhenItDoesNotApply()
        {
            var runtime = Run(
                "class Animal { }\nclass Dog : Animal { }\nclass Cat : Animal { }\n"
                    + "fun run(): int { let a: Animal = Cat(); let d = a as? Dog; return d == null ? 1 : 0; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ASafeCastKeepsTheValueWhenItApplies()
        {
            var runtime = Run(
                "class Animal { }\nclass Dog : Animal { public fun legs(): int { return 4; } }\n"
                    + "fun run(): int { let a: Animal = Dog(); let d = a as? Dog; return d == null ? 0 : d!!.legs(); }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void StringsAreOrderedThroughCompareTo()
        {
            var runtime = Run("fun run(): bool { return \"apple\" < \"banana\"; }");
            Assert.True(Call(runtime, "run").AsBool);
        }

        [Fact]
        public void ThreeWayCompareOnStringsGivesTheSign()
        {
            var runtime = Run("fun run(): int { return \"b\" <=> \"a\"; }");
            Assert.True(Int(runtime, "run") > 0);
        }

        [Fact]
        public void AStringSwitchMatchesByTextRatherThanByHash()
        {
            var runtime = Run(
                "fun pick(s: string): int {\n"
                    + "  switch (s) { case \"one\": return 1; case \"two\": return 2; case \"three\": return 3; }\n"
                    + "  return 0;\n"
                    + "}\n"
                    + "fun run(): int { return pick(\"two\") * 100 + pick(\"nope\"); }");

            Assert.Equal(200, Int(runtime, "run"));
        }

        [Fact]
        public void ADenseIntegerSwitchStillPicksTheRightArm()
        {
            var runtime = Run(
                "fun pick(n: int): int {\n"
                    + "  switch (n) { case 0: return 10; case 1: return 11; case 2: return 12; case 3: return 13; }\n"
                    + "  return -1;\n"
                    + "}\n"
                    + "fun run(): int { return pick(2) + pick(9); }");

            Assert.Equal(11, Int(runtime, "run"));
        }

        [Fact]
        public void AForInOverADictionaryWalksKeyValuePairs()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let m: {string: int} = {\"a\": 1, \"b\": 2, \"c\": 3};\n"
                    + "  var total = 0;\n"
                    + "  for (e in m) { total = total + e[1]; }\n"
                    + "  return total;\n"
                    + "}");

            Assert.Equal(6, Int(runtime, "run"));
        }

        [Fact]
        public void AForInOverAnIterableGoesThroughItsCursor()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let xs: int[] = [1, 2, 3];\n"
                    + "  let it: IIterable<int> = xs;\n"
                    + "  var total = 0;\n"
                    + "  for (x in it) { total = total + x; }\n"
                    + "  return total;\n"
                    + "}");

            Assert.Equal(6, Int(runtime, "run"));
        }

        [Fact]
        public void AForInOverARangeHeldInALocalStillWalksIt()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let r = 1..=4;\n"
                    + "  var total = 0;\n"
                    + "  for (i in r) { total = total + i; }\n"
                    + "  return total;\n"
                    + "}");

            Assert.Equal(10, Int(runtime, "run"));
        }

        [Fact]
        public void AnInlineFunctionIsSplicedIntoItsCallSite()
        {
            var runtime = Run(
                "inline fun twice(x: int): int { return x + x; }\n"
                    + "fun run(): int { return twice(3) + twice(4); }");

            Assert.Equal(14, Int(runtime, "run"));
        }

        [Fact]
        public void AConstFunctionCallWithConstantArgumentsIsFoldedAway()
        {
            var runtime = Run(
                "const fun square(x: int): int { return x * x; }\n"
                    + "fun run(): int { return square(5); }");

            Assert.Equal(25, Int(runtime, "run"));
        }

        [Fact]
        public void AConstFunctionIsStillCallableWithSomethingNotConstant()
        {
            var runtime = Run(
                "const fun square(x: int): int { return x * x; }\n"
                    + "fun run(n: int): int { return square(n); }");

            Assert.Equal(36, Int(runtime, "run", SurtrValue.CreateInt(6)));
        }

        [Fact]
        public void IncrementLeavesTheRightValueBehind()
        {
            var runtime = Run(
                "fun run(): int { var i = 5; let post = i++; let pre = ++i; return post * 100 + pre * 10 + i; }");

            // post reads 5, i becomes 6; pre makes it 7 and reads 7.
            Assert.Equal(577, Int(runtime, "run"));
        }

        [Fact]
        public void AValueClassIsItsFieldWhereTheTypeIsKnown()
        {
            var runtime = Run(
                "value class EntityId { public let raw: int; public constructor(raw: int) { this.raw = raw; } }\n"
                    + "fun run(): int { let id = EntityId(7); return id.raw; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AnArraysOwnMembersAreCallableFromSource()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let xs: int[] = [1, 2];\n"
                    + "  xs.push(3);\n"
                    + "  return xs.length * 100 + xs.get(2);\n"
                    + "}");

            Assert.Equal(303, Int(runtime, "run"));
        }
        #endregion

        #region Parameter defaults (§3.5)
        [Fact]
        public void AnOmittedArgumentTakesItsDefault()
        {
            var runtime = Run(
                "fun spawn(x: int, hp: int = 100): int { return x * 1000 + hp; }\n"
                    + "fun run(): int { return spawn(1); }");

            Assert.Equal(1100, Int(runtime, "run"));
        }

        [Fact]
        public void AWrittenArgumentStillWins()
        {
            var runtime = Run(
                "fun spawn(x: int, hp: int = 100): int { return x * 1000 + hp; }\n"
                    + "fun run(): int { return spawn(1, 50); }");

            Assert.Equal(1050, Int(runtime, "run"));
        }

        [Fact]
        public void ANamedArgumentMaySkipADefaultedOne()
        {
            var runtime = Run(
                "fun make(a: int = 1, b: int = 2, c: int = 4): int { return a * 100 + b * 10 + c; }\n"
                    + "fun run(): int { return make(c: 9); }");

            Assert.Equal(129, Int(runtime, "run"));
        }

        [Fact]
        public void ADefaultMayBeAConstOrAConstFunction()
        {
            var runtime = Run(
                "const Base: int = 7;\n"
                    + "const fun twice(x: int): int { return x + x; }\n"
                    + "fun f(a: int = Base, b: int = twice(4)): int { return a * 100 + b; }\n"
                    + "fun run(): int { return f(); }");

            Assert.Equal(708, Int(runtime, "run"));
        }

        [Fact]
        public void AnIntegerDefaultWidensIntoAFloatParameter()
        {
            var runtime = Run(
                "fun scale(v: float = 2): float { return v * 3.0; }\nfun run(): float { return scale(); }");

            Assert.Equal(6.0, Call(runtime, "run").AsFloat);
        }

        [Fact]
        public void ADefaultThatDoesNotFoldIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "fun other(): int { return 1; }\nfun f(a: int = other()): int { return a; }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.NotAConstant);
        }

        [Fact]
        public void ADefaultSurvivesTheImage()
        {
            var emitter = Build("fun spawn(x: int, hp: int = 100): int { return x + hp; }");
            var reloaded = SurtrModuleImage.FromBytes(emitter.EmitImages()[0].ToBytes()).Instantiate();

            Assert.True(reloaded.TryGetMethods("spawn", out var overloads));
            Assert.True(overloads[0].Parameters[1].HasDefault);
            Assert.Equal(100, overloads[0].Parameters[1].DefaultValue.Value.AsInt);
        }
        #endregion

        #region Singletons (§2.8)
        [Fact]
        public void ASingletonIsBuiltOnceAndReachedByItsOwnName()
        {
            var runtime = Run(
                "singleton Counter {\n"
                    + "  public var value: int = 0;\n"
                    + "  public fun bump(): int { this.value = this.value + 1; return this.value; }\n"
                    + "}\n"
                    + "fun run(): int { Counter.bump(); Counter.bump(); return Counter.value; }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void ASingletonIsAValueAndSatisfiesItsInterface()
        {
            var runtime = Run(
                "interface Named { fun name(): string; }\n"
                    + "singleton Registry : Named { public override fun name(): string { return \"registry\"; } }\n"
                    + "fun describe(n: Named): string { return n.name(); }\n"
                    + "fun run(): string { return describe(Registry); }");

            Assert.Equal("registry", Text(runtime, "run"));
        }

        [Fact]
        public void ASingletonHoldsItsStateAcrossCalls()
        {
            var runtime = Run(
                "singleton Store {\n"
                    + "  private var _entries: {string: int} = {};\n"
                    + "  public fun put(k: string, v: int): void { this._entries[k] = v; }\n"
                    + "  public fun get(k: string): int { return this._entries[k]; }\n"
                    + "}\n"
                    + "fun run(): int { Store.put(\"a\", 41); return Store.get(\"a\") + 1; }");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void ASingletonCannotDeclareAConstructor()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "singleton Bad { public constructor() { } }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidValueClass);
        }
        #endregion

        #region Bridges into a generic interface's erased slot
        [Fact]
        public void ATypedImplementationReachesAGenericContractThroughABridge()
        {
            var runtime = Run(
                "class Score : IComparable<Score> {\n"
                    + "  public let value: int;\n"
                    + "  public constructor(value: int) { this.value = value; }\n"
                    + "  public override fun compareTo(other: Score): int { return this.value <=> other.value; }\n"
                    + "}\n"
                    + "fun order(a: IComparable<Score>, b: Score): int { return a.compareTo(b); }\n"
                    + "fun run(): int { return order(Score(9), Score(4)); }");

            Assert.True(Int(runtime, "run") > 0);
        }

        [Fact]
        public void ABridgeForwardsToWhicheverOverrideTheReceiverHas()
        {
            var runtime = Run(
                "class Base : IEquatable<Base> {\n"
                    + "  public virtual fun equals(other: Base): bool { return false; }\n"
                    + "}\n"
                    + "class Always : Base { public override fun equals(other: Base): bool { return true; } }\n"
                    + "fun same(a: IEquatable<Base>, b: Base): bool { return a.equals(b); }\n"
                    + "fun run(): bool { return same(Always(), Base()); }");

            Assert.True(Call(runtime, "run").AsBool);
        }
        #endregion

        #region Value classes (§2.9)
        [Fact]
        public void AValueClassMethodIsCalledOnTheBoxedForm()
        {
            var runtime = Run(
                "value class EntityId {\n"
                    + "  public let raw: int;\n"
                    + "  public constructor(raw: int) { this.raw = raw; }\n"
                    + "  public fun doubled(): int { return this.raw * 2; }\n"
                    + "}\n"
                    + "fun run(): int { let id = EntityId(21); return id.doubled(); }");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void AValueClassStillCostsNothingWhereItsTypeIsKnown()
        {
            var runtime = Run(
                "value class EntityId { public let raw: int; public constructor(raw: int) { this.raw = raw; } }\n"
                    + "fun run(): int { let id = EntityId(7); return id.raw; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AValueClassFlowingIntoAnErasedSlotIsBoxedAsItself()
        {
            var runtime = Run(
                "value class EntityId {\n"
                    + "  public let raw: int;\n"
                    + "  public constructor(raw: int) { this.raw = raw; }\n"
                    + "}\n"
                    + "fun run(): int { let u: unknown = EntityId(5); let back = u as EntityId; return back.raw; }");

            Assert.Equal(5, Int(runtime, "run"));
        }
        #endregion

        #region Nested lambdas
        [Fact]
        public void ALambdaInsideALambdaCapturesThroughTheOuterOne()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let base = 40;\n"
                    + "  let outer = (a: int) => ((b: int) => a + b + base)(1);\n"
                    + "  return outer(1);\n"
                    + "}");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void ANestedLambdaMayCaptureTheReceiverThroughItsOuterOne()
        {
            var runtime = Run(
                "class Adder {\n"
                    + "  public var offset: int = 10;\n"
                    + "  public fun make(): (int) -> int { return (x: int) => ((y: int) => y + this.offset)(x); }\n"
                    + "}\n"
                    + "fun run(): int { return Adder().make()(5); }");

            Assert.Equal(15, Int(runtime, "run"));
        }

        [Fact]
        public void ANestedLambdaReturnedFromTheOuterOneStillSeesTheCapture()
        {
            var runtime = Run(
                "fun make(): (int) -> (int) -> int {\n"
                    + "  let scale = 3;\n"
                    + "  return (a: int) => (b: int) => (a + b) * scale;\n"
                    + "}\n"
                    + "fun run(): int { return make()(2)(5); }");

            Assert.Equal(21, Int(runtime, "run"));
        }
        #endregion

        #region Refusals
        [Fact]
        public void OverridingASealedMemberIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class A { public virtual fun f(): int { return 1; } }\n"
                    + "class B : A { public sealed override fun f(): int { return 2; } }\n"
                    + "class C : B { public override fun f(): int { return 3; } }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidBaseType);
        }

        [Fact]
        public void ACompilationWithErrorsIsNotEmitted()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", "fun run(): int { return nope; }");

            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(compilation.HasErrors);
            Assert.False(new ModuleEmitter(compilation, binder).TryEmit());
        }
        #endregion

        #region Constructor chaining (§3.2)
        [Fact]
        public void ASuperChainRunsTheBaseConstructor()
        {
            var runtime = Run(
                "class Animal {\n"
                    + "  public let name: string;\n"
                    + "  public constructor(name: string) { this.name = name; }\n"
                    + "}\n"
                    + "class Dog : Animal {\n"
                    + "  public constructor(name: string) : super(name) { }\n"
                    + "}\n"
                    + "fun run(): string { return Dog(\"rex\").name; }");

            Assert.Equal("rex", Text(runtime, "run"));
        }

        [Fact]
        public void AThisChainRunsTheOtherConstructor()
        {
            var runtime = Run(
                "class C {\n"
                    + "  public var n: int = 0;\n"
                    + "  public constructor() : this(5) { }\n"
                    + "  public constructor(n: int) { this.n = n; }\n"
                    + "}\n"
                    + "fun run(): int { return C().n; }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>
        /// §3.2: the chained-to constructor already ran them, so running them again would undo
        /// whatever it did with them.
        /// </summary>
        [Fact]
        public void AThisChainDoesNotRerunTheInstanceInitializers()
        {
            var runtime = Run(
                "class C {\n"
                    + "  public var log: int = 0;\n"
                    + "  public constructor() : this(0) { log += 1; }\n"
                    + "  public constructor(n: int) { }\n"
                    + "}\n"
                    + "fun run(): int { return C().log; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void TheInstanceInitializersRunAfterTheSuperChain()
        {
            var runtime = Run(
                "class Base { public var b: int = 0; public constructor(b: int) { this.b = b; } }\n"
                    + "class Derived : Base {\n"
                    + "  public var d: int = 4;\n"
                    + "  public constructor() : super(6) { }\n"
                    + "}\n"
                    + "fun run(): int { let x = Derived(); return x.b + x.d; }");

            Assert.Equal(10, Int(runtime, "run"));
        }

        /// <summary>§3.2: a constructor that omits the chain still reaches the base's parameterless one.</summary>
        [Fact]
        public void AConstructorWithNoChainStillReachesItsBase()
        {
            var runtime = Run(
                "class Base { public var n: int = 0; public constructor() { n = 7; } }\n"
                    + "class Derived : Base { public constructor() { } }\n"
                    + "fun run(): int { return Derived().n; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>
        /// A derived class declaring nothing at all still has to be constructed: <c>ObjNew</c> only
        /// allocates, so without a synthesised constructor the base's initializers never run.
        /// </summary>
        [Fact]
        public void ADerivedClassWithNoMembersStillRunsItsBasesInitializers()
        {
            var runtime = Run(
                "class Base { public var n: int = 7; }\n"
                    + "class Derived : Base { }\n"
                    + "fun run(): int { return Derived().n; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AChainReachesThroughThreeLevels()
        {
            var runtime = Run(
                "class A { public var n: int = 0; public constructor(n: int) { this.n = n; } }\n"
                    + "class B : A { public constructor(n: int) : super(n + 1) { } }\n"
                    + "class C : B { public constructor() : super(5) { } }\n"
                    + "fun run(): int { return C().n; }");

            Assert.Equal(6, Int(runtime, "run"));
        }

        [Fact]
        public void AChainToASuperThatDoesNotExistIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class C { public constructor() : super() { } }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidConstructorChain);
        }

        /// <summary>
        /// §3.2 gives an omitted chain one meaning — the base's parameterless constructor — so where
        /// the base has none, the omission names nothing and the base would go unconstructed.
        /// </summary>
        [Fact]
        public void AConstructorWithNoChainWhoseBaseHasNoParameterlessOneIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class A { public var n: int = 0; public constructor(n: int) { this.n = n; } }\n"
                    + "class B : A { public constructor() { } }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.BaseConstructorUnreachable);
        }

        /// <summary>The same case, reached by declaring no constructor at all.</summary>
        [Fact]
        public void AClassWithNoConstructorWhoseBaseNeedsArgumentsIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class A { public constructor(n: int) { } }\nclass B : A { }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.BaseConstructorUnreachable);
        }

        /// <summary>
        /// Constructors are not inherited, so a grandparent's parameterless one does not answer for
        /// the parent that sits between.
        /// </summary>
        [Fact]
        public void AGrandparentsParameterlessConstructorDoesNotSatisfyTheParent()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class A { public constructor() { } }\n"
                    + "class B : A { public constructor(n: int) : super() { } }\n"
                    + "class C : B { }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.BaseConstructorUnreachable);
        }

        /// <summary>
        /// A base that declares no constructor needs nothing called: its initializers run from the
        /// parameterless one the emitter synthesises for it.
        /// </summary>
        [Fact]
        public void ABaseThatDeclaresNoConstructorNeedsNoChain()
        {
            var runtime = Run(
                "class A { public var n: int = 5; }\nclass B : A { }\nfun run(): int { return B().n; }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>
        /// §9's own shape: every library exception takes a message, so a subclass has to pass one up.
        /// </summary>
        [Fact]
        public void AUserExceptionChainsItsMessageIntoTheLibrary()
        {
            var runtime = Run(
                "class BadThing : Exception { constructor(message: string) : super(message) { } }\n"
                    + "fun run(): string {\n"
                    + "  try { throw BadThing(\"nope\"); }\n"
                    + "  catch (e: BadThing) { return e.message; }\n"
                    + "}");

            Assert.Equal("nope", Text(runtime, "run"));
        }

        [Fact]
        public void AThisChainThatLoopsBackIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class C {\n"
                    + "  public constructor() : this(1) { }\n"
                    + "  public constructor(n: int) : this() { }\n"
                    + "}");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidConstructorChain);
        }

        /// <summary>
        /// The synthesised constructor has no symbol, so a creation site in another module can only
        /// reach it through metadata the emitter carried across.
        /// </summary>
        [Fact]
        public void ConstructingAClassFromAnotherModuleRunsItsInitializers()
        {
            var runtime = Run(
                "import game.util.Thing;\nfun run(): int { return Thing().n; }",
                ("/game/util/Thing.surtr", "public class Thing { public let n: int = 6; }"));

            Assert.Equal(6, Int(runtime, "run"));
        }
        #endregion

        #region Static blocks (§2.5, §3.2)
        [Fact]
        public void AModuleStaticBlockRunsAtLoad()
        {
            var runtime = Run("var counter: int = 0;\nstatic { counter = 7; }\nfun run(): int { return counter; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AClassStaticBlockRunsAtLoad()
        {
            var runtime = Run(
                "class C { public static var n: int = 1; static { n = 7; } }\nfun run(): int { return C.n; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        /// <summary>
        /// §2.5 runs a block in the source position it appears among the field initializers, so a
        /// block reads what the ones above it wrote and is read by the ones below.
        /// </summary>
        [Fact]
        public void AStaticBlockRunsInItsSourcePositionAmongTheInitializers()
        {
            var runtime = Run(
                "var a: int = 1;\nstatic { a += 1; }\nvar b: int = 10;\nstatic { b += a; }\nfun run(): int { return b; }");

            Assert.Equal(12, Int(runtime, "run"));
        }
        #endregion

        #region Nullable access (§5.1)
        [Fact]
        public void ASafeNavigationYieldsNullInsteadOfFaulting()
        {
            var runtime = Run(
                "class Holder { public let name: string = \"x\"; }\n"
                    + "fun run(): int {\n"
                    + "  let h: Holder? = null;\n"
                    + "  let n = h?.name;\n"
                    + "  return n == null ? 1 : 0;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ASafeNavigationReadsTheMemberWhenTheReceiverIsThere()
        {
            var runtime = Run(
                "class Holder { public let name: string = \"x\"; }\n"
                    + "fun run(): string { let h: Holder? = Holder(); return h?.name ?? \"fallback\"; }");

            Assert.Equal("x", Text(runtime, "run"));
        }

        /// <summary>A primitive member's absence is the absent tag, which is what <c>??</c> tests.</summary>
        [Fact]
        public void ASafeNavigationOnAPrimitiveMemberCoalesces()
        {
            var runtime = Run(
                "class Holder { public let size: int = 9; }\n"
                    + "fun run(): int { let h: Holder? = null; return h?.size ?? 4; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void ASafeNavigationChainShortCircuitsAtTheFirstNull()
        {
            var runtime = Run(
                "class Inner { public let name: string = \"x\"; }\n"
                    + "class Outer { public var inner: Inner? = null; }\n"
                    + "fun run(): int {\n"
                    + "  let o: Outer? = Outer();\n"
                    + "  return o?.inner?.name == null ? 1 : 0;\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>
        /// The receiver is evaluated once, which is the half of <c>?.</c> that a re-evaluating
        /// lowering would get wrong without ever looking wrong.
        /// </summary>
        [Fact]
        public void ASafeNavigationEvaluatesItsReceiverOnce()
        {
            var runtime = Run(
                "var calls: int = 0;\n"
                    + "class Holder { public let name: string = \"x\"; }\n"
                    + "fun make(): Holder? { calls += 1; return null; }\n"
                    + "fun run(): int { let n = make()?.name; return calls; }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ANullAssertionThrowsWhenItDoesNotHold()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  let s: string? = null;\n"
                    + "  try { let t = s!!; return 0; }\n"
                    + "  catch (e: NullReferenceException) { return 1; }\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void ANullAssertionPassesTheValueThroughWhenItHolds()
        {
            var runtime = Run("fun run(): string { let s: string? = \"x\"; return s!!; }");

            Assert.Equal("x", Text(runtime, "run"));
        }

        [Fact]
        public void ANullAssertionOnAnAbsentPrimitiveThrows()
        {
            var runtime = Run(
                "fun run(): int {\n"
                    + "  var n: int? = null;\n"
                    + "  try { let v = n!!; return 0; }\n"
                    + "  catch (e: NullReferenceException) { return 1; }\n"
                    + "}");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>
        /// A present <c>0</c> is not absence: a reference is its 32-bit payload, so the two would be
        /// one value without the absent tag.
        /// </summary>
        [Fact]
        public void APresentZeroIsNotAbsent()
        {
            var runtime = Run("fun run(): int { var n: int? = 0; return n ?? 7; }");

            Assert.Equal(0, Int(runtime, "run"));
        }
        #endregion

        #region Varargs (§3.5)
        [Fact]
        public void AVarargsCallAbsorbsTheSurplus()
        {
            var runtime = Run(
                "fun count(first: string, rest: string...): int { return rest.length; }\n"
                    + "fun run(): int { return count(\"a\", \"b\", \"c\"); }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void AVarargsCallWithNoSurplusPacksAnEmptyArray()
        {
            var runtime = Run(
                "fun count(first: string, rest: string...): int { return rest.length; }\n"
                    + "fun run(): int { return count(\"a\"); }");

            Assert.Equal(0, Int(runtime, "run"));
        }

        [Fact]
        public void AVarargsParameterMayBePassedAWholeArray()
        {
            var runtime = Run(
                "fun count(first: string, rest: string...): int { return rest.length; }\n"
                    + "fun run(): int { return count(\"a\", [\"b\", \"c\", \"d\"]); }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        [Fact]
        public void TheBodySeesAVarargsParameterAsAnArray()
        {
            var runtime = Run(
                "fun first(prefix: string, rest: string...): string { return rest.get(0); }\n"
                    + "fun run(): string { return first(\"a\", \"b\", \"c\"); }");

            Assert.Equal("b", Text(runtime, "run"));
        }

        /// <summary>§13.4's own shape, which was unreachable while varargs did not resolve.</summary>
        [Fact]
        public void StringFormatIsCallableFromSource()
        {
            var runtime = Run("fun run(): string { return string.format(\"{0}-{1}\", \"a\", \"b\"); }");

            Assert.Equal("a-b", Text(runtime, "run"));
        }

        [Fact]
        public void AVarargsSignatureSurvivesAModuleBoundary()
        {
            var runtime = Run(
                "import game.util.*;\nfun run(): int { return tally(\"a\", \"b\", \"c\"); }",
                ("/game/util/M.surtr", "public fun tally(first: string, rest: string...): int { return rest.length; }"));

            Assert.Equal(2, Int(runtime, "run"));
        }
        #endregion

        #region Generics (§6)
        private const string Box =
            "class Box<T> {\n"
                + "  private let _value: T;\n"
                + "  constructor(value: T) { _value = value; }\n"
                + "  public fun get(): T { return _value; }\n"
                + "}\n";

        /// <summary>
        /// §6's own example: a bound is what lets a body call anything on a <c>T</c> at all.
        /// </summary>
        [Fact]
        public void AConstraintExposesItsMembersOnATypeParameter()
        {
            var runtime = Run(
                "class Score : IComparable<Score> {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "  public override fun compareTo(other: Score): int { return value - other.value; }\n"
                    + "}\n"
                    + "fun biggest<T : IComparable<T>>(a: T, b: T): T { return a.compareTo(b) >= 0 ? a : b; }\n"
                    + "fun run(): int { let s: Score = biggest(Score(4), Score(9)); return s.value; }");

            Assert.Equal(9, Int(runtime, "run"));
        }

        /// <summary>An unconstrained parameter promises nothing, and there is no root class to fall back to.</summary>
        [Fact]
        public void AnUnconstrainedTypeParameterExposesNothing()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "fun nope<T>(a: T): int { return a.compareTo(a); }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.UnresolvedName);
        }

        [Fact]
        public void AGenericTypeIsConstructedFromTheTypeItGoesInto()
        {
            var runtime = Run(Box + "fun run(): int { let b: Box<int> = Box(5); return b.get(); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericTypeIsConstructedFromWrittenTypeArguments()
        {
            var runtime = Run(Box + "fun run(): int { let b = Box<int>(5); return b.get(); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericTypeIsConstructedFromItsConstructorsArguments()
        {
            var runtime = Run(Box + "fun run(): int { let b = Box(5); return b.get(); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>
        /// One class, one method table, one compiled body — and two constructions that read as
        /// different types. That is the whole of what erasure buys and what the compiler owes.
        /// </summary>
        [Fact]
        public void TwoConstructionsOfOneGenericKeepTheirOwnTypes()
        {
            var runtime = Run(
                Box + "fun run(): string { let a = Box(\"x\"); let b = Box(\"y\"); return a.get() + b.get(); }");

            Assert.Equal("xy", Text(runtime, "run"));
        }

        [Fact]
        public void ASubstitutedMemberRejectsTheWrongArgument()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                Box + "fun run(): int { let b: Box<int> = Box(\"x\"); return b.get(); }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.True(compilation.HasErrors);
        }

        [Fact]
        public void AGenericTypeMayBeItsOwnTypeArgument()
        {
            var runtime = Run(
                Box + "fun run(): int {\n"
                    + "  let inner: Box<int> = Box(3);\n"
                    + "  let outer: Box<Box<int>> = Box(inner);\n"
                    + "  return outer.get().get();\n"
                    + "}");

            Assert.Equal(3, Int(runtime, "run"));
        }

        /// <summary>§6: arity is part of identity, so these are two declarations sharing a spelling.</summary>
        [Fact]
        public void ArityPicksBetweenTwoDeclarationsOfOneName()
        {
            var runtime = Run(
                "class Result<T> { public fun n(): int { return 1; } }\n"
                    + "class Result<T, E> { public fun n(): int { return 2; } }\n"
                    + "fun run(): int { let r: Result<int, string> = Result(); return r.n(); }");

            Assert.Equal(2, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericCallInfersItsTypeArgumentsFromTheArguments()
        {
            var runtime = Run("fun pick<T>(a: T, b: T): T { return a; }\nfun run(): int { return pick(1, 2); }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericCallMayWriteItsTypeArguments()
        {
            var runtime = Run("fun pick<T>(a: T, b: T): T { return a; }\nfun run(): int { return pick<int>(1, 2); }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>Inference walks into a composite: <c>T[]</c> against an <c>int[]</c> gives <c>int</c>.</summary>
        [Fact]
        public void InferenceWalksIntoACompositeParameter()
        {
            var runtime = Run("fun count<T>(items: T[]): int { return items.length; }\nfun run(): int { return count([1, 2, 3]); }");

            Assert.Equal(3, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericMethodInsideAGenericClassSubstitutesBoth()
        {
            var runtime = Run(
                "class Holder<T> {\n"
                    + "  private let _value: T;\n"
                    + "  constructor(value: T) { _value = value; }\n"
                    + "  public fun map<U>(other: U): U { return other; }\n"
                    + "}\n"
                    + "fun run(): int { let h: Holder<string> = Holder(\"x\"); return h.map(5); }");

            Assert.Equal(5, Int(runtime, "run"));
        }

        /// <summary>
        /// §6 checks a bound against the <em>substituted</em> type: <c>T : IComparable&lt;T&gt;</c>
        /// asked of a <c>Plain</c> is asking about <c>IComparable&lt;Plain&gt;</c>.
        /// </summary>
        [Fact]
        public void AnArgumentThatDoesNotSatisfyItsBoundIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class Plain { }\n"
                    + "fun biggest<T : IComparable<T>>(a: T, b: T): T { return a; }\n"
                    + "fun run(): int { biggest(Plain(), Plain()); return 1; }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.ConstraintNotSatisfied);
        }

        /// <summary>Two answers for one parameter is a refusal, not a widening — §3.5's "no silent pick".</summary>
        [Fact]
        public void ContradictoryInferenceIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "fun pick<T>(a: T, b: T): T { return a; }\nfun run(): int { return pick(1, \"x\"); }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CannotInferTypeArgument);
        }

        /// <summary>§1.11's two obligations, seen from source: box on the way in, cast on the way out.</summary>
        [Fact]
        public void APrimitiveSurvivesARoundTripThroughAnErasedSlot()
        {
            var runtime = Run(Box + "fun run(): int { let b: Box<int> = Box(42); let n: int = b.get(); return n + 0; }");

            Assert.Equal(42, Int(runtime, "run"));
        }

        [Fact]
        public void AValueClassSurvivesARoundTripThroughAnErasedSlot()
        {
            var runtime = Run(
                Box + "value class EntityId {\n"
                    + "  public let value: int;\n"
                    + "  constructor(value: int) { this.value = value; }\n"
                    + "}\n"
                    + "fun run(): int { let b: Box<EntityId> = Box(EntityId(7)); return b.get().value; }");

            Assert.Equal(7, Int(runtime, "run"));
        }

        [Fact]
        public void AGenericFromAnotherModuleIsConstructedAndCalled()
        {
            var runtime = Run(
                "import game.util.Box;\nfun run(): int { let b: Box<int> = Box(3); return b.get(); }",
                ("/game/util/Box.surtr",
                    "public class Box<T> {\n"
                        + "  private let _value: T;\n"
                        + "  public constructor(value: T) { _value = value; }\n"
                        + "  public fun get(): T { return _value; }\n"
                        + "}"));

            Assert.Equal(3, Int(runtime, "run"));
        }

        /// <summary>
        /// A generic class satisfying a generic contract, walked by <c>for-in</c> — which puts the
        /// bridge, the erased slot and interface dispatch on one path.
        /// </summary>
        [Fact]
        public void AGenericClassCanSatisfyAGenericContract()
        {
            var runtime = Run(
                "class Single<T> : IIterable<T> {\n"
                    + "  private let _value: T;\n"
                    + "  constructor(value: T) { _value = value; }\n"
                    + "  public override fun iterate(): IIterator<T> { return [_value].iterate(); }\n"
                    + "}\n"
                    + "fun run(): int { var total = 0; for (n in Single(4)) { total += n; } return total; }");

            Assert.Equal(4, Int(runtime, "run"));
        }

        [Fact]
        public void AConstructionMayStopShortOfADefaultedParameter()
        {
            var runtime = Run(
                "class Counter<T> {\n"
                    + "  private let _value: T;\n"
                    + "  private let _n: int;\n"
                    + "  constructor(value: T, n: int = 1) { _value = value; _n = n; }\n"
                    + "  public fun n(): int { return _n; }\n"
                    + "}\n"
                    + "fun run(): int { let c = Counter(\"x\"); return c.n(); }");

            Assert.Equal(1, Int(runtime, "run"));
        }

        /// <summary>
        /// A construction with nothing to infer from is refused rather than guessed at, the same
        /// trade §5.9 makes for a bare <c>[]</c>.
        /// </summary>
        [Fact]
        public void AConstructionWithNothingToInferFromIsReported()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class Empty<T> { public fun n(): int { return 1; } }\nfun run(): int { let e = Empty(); return e.n(); }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.CannotInferTypeArgument);
        }

        /// <summary>
        /// A construction whose arguments the compiler inferred is still a construction, and its
        /// bounds are not optional because nobody wrote them.
        /// </summary>
        [Fact]
        public void AnInferredConstructionStillChecksItsBounds()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class Plain { }\n"
                    + "class Sorted<T : IComparable<T>> {\n"
                    + "  private let _value: T;\n"
                    + "  constructor(value: T) { _value = value; }\n"
                    + "}\n"
                    + "fun run(): int { let s = Sorted(Plain()); return 1; }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.ConstraintNotSatisfied);
        }

        /// <summary>
        /// And one written inside a body: those sites are recorded while the body binds, which is
        /// after the member phase verified the ones written on declarations.
        /// </summary>
        [Fact]
        public void AConstructedTypeWrittenInABodyChecksItsBounds()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(
                Root + "/game/core/Test.surtr",
                "class Plain { }\n"
                    + "class Sorted<T : IComparable<T>> { }\n"
                    + "fun run(): int { let s: Sorted<Plain>? = null; return 1; }");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.ConstraintNotSatisfied);
        }

        /// <summary>
        /// The other half of the type-argument scan: a <c>&lt;</c> that closes nothing is a
        /// comparison, and stays one.
        /// </summary>
        [Fact]
        public void AComparisonIsNotReadAsATypeArgumentList()
        {
            var runtime = Run("fun run(): bool { let a = 1; let b = 2; return a < b; }");

            Assert.True(Call(runtime, "run").AsBool);
        }
        #endregion

        #region Module-level natives (§10)
        /// <summary>
        /// §10: a module naming a host global nobody registered fails to load, rather than reading a
        /// zero out of storage of its own.
        /// </summary>
        [Fact]
        public void AModuleNamingAnUnregisteredNativeVariableFailsToLoad()
        {
            var emitter = Build("native let ScreenWidth: int;\nfun run(): int { return ScreenWidth; }");

            using var runtime = new SurtrRuntime();
            Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(emitter.Modules[0]));
        }

        [Fact]
        public void AModuleNamingAnUnregisteredNativeFunctionFailsToLoad()
        {
            var emitter = Build("native fun hostLog(message: string): void;\nfun run(): int { hostLog(\"hi\"); return 1; }");

            using var runtime = new SurtrRuntime();
            Assert.Throws<InvalidOperationException>(() => runtime.LoadModule(emitter.Modules[0]));
        }

        [Fact]
        public void ANativeVariableReadsTheHostsOwnStorage()
        {
            var emitter = Build("native let ScreenWidth: int;\nfun run(): int { return ScreenWidth; }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            var width = runtime.DefineGlobal("ScreenWidth", SurtrClassReference.Integer, isReadOnly: true);
            runtime.Globals.SetValue(width, SurtrValue.CreateInt(1280));
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(1280, Int(runtime, "run"));
        }

        [Fact]
        public void AWriteToANativeVariableLandsInTheHostsOwnStorage()
        {
            var emitter = Build("native var TimeScale: float;\nfun run(): int { TimeScale = 0.5; return 1; }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            runtime.DefineGlobal("TimeScale", SurtrClassReference.Float);
            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(1, Int(runtime, "run"));
            Assert.True(runtime.Globals.TryGetValue("TimeScale", out var written));
            Assert.Equal(0.5, written.AsFloat);
        }

        [Fact]
        public unsafe void ANativeFunctionCallReachesTheHostsGlobal()
        {
            var emitter = Build("native fun hostSquare(value: int): int;\nfun run(): int { return hostSquare(3); }");

            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            runtime.DefineGlobalFunction(
                "hostSquare",
                SurtrClassReference.Integer,
                new[] { new SurtrParameterInfo("value", runtime.TypeHandle(SurtrClassReference.Integer)) },
                SurtrNativeEntryPoint.FromFunctionPointer(&Square));

            runtime.LoadModule(emitter.Modules[0]);

            Assert.Equal(9, Int(runtime, "run"));
        }

        // A host global takes no receiver, so its first declared parameter is argument zero.
        private static SurtrValue Square(SurtrCallArguments arguments)
            => SurtrValue.CreateInt(arguments.GetInt(0) * arguments.GetInt(0));

        [Fact]
        public void ANativeVariableCannotHaveAnInitializer()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", "native let ScreenWidth: int = 5;");

            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();

            Assert.Contains(compilation.Diagnostics, d => d.Code == SurtrDiagnosticCode.InvalidNativeDeclaration);
        }
        #endregion
    }
}
