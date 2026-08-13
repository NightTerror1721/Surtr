#nullable enable

using Surtr.Bytecode.Image;
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

        #region Refusals
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
    }
}
