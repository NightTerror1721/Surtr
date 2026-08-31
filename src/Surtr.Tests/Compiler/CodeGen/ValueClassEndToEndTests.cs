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
    /// Covers multi-field <c>value class</c>es end to end: declared in source, compiled, loaded,
    /// and run - construction, locals, arguments, returns, field access, equality and the boxing
    /// boundaries.
    /// </summary>
    public sealed class ValueClassEndToEndTests : IDisposable
    {
        private const string Root = "D:/proj/src";

        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public void Dispose()
        {
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

    public fun sum(): float {
        return this.x + this.y;
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
                "Emission reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            return emitter;
        }

        private SurtrRuntime Run(string source, params (string Path, string Text)[] extra) => Load(Build(source, extra));

        /// <summary>Asserts that the source is refused, naming the given fragment.</summary>
        private void BuildFails(string source, string messageFragment)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            var compilation = SurtrCompilation.Create(project);
            _owned.Add(compilation);

            var binder = compilation.Bind();
            binder.BindBodies();

            if (compilation.HasErrors)
            {
                Assert.Contains(messageFragment, string.Join("; ", compilation.Diagnostics.Select(d => d.Message)));
                return;
            }

            var emitter = new ModuleEmitter(compilation, binder);
            Assert.False(emitter.TryEmit());
            Assert.Contains(messageFragment, string.Join("; ", compilation.Diagnostics.Select(d => d.Message)));
        }

        private SurtrRuntime Load(ModuleEmitter emitter)
        {
            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            foreach (var module in emitter.Modules)
                runtime.LoadModule(module);

            return runtime;
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
            => runtime.Resolve<SurtrString>(runtime.Invoke(Function(runtime, name), arguments))!.Value;

        #region Construction and fields

        [Fact]
        public void AValueConstructs_AndItsFieldsReadBack()
        {
            var runtime = Run(Vec2 + @"
fun go(): float {
    let v = Vec2(1.5, 2.5);
    return v.x + v.y;
}
");

            Assert.Equal(4.0, Float(runtime, "go"));
        }

        [Fact]
        public void FieldsReadThroughAReceiverExpression()
        {
            var runtime = Run(Vec2 + @"
fun go(): float {
    return Vec2(3.0, 4.0).sum();
}
");

            Assert.Equal(7.0, Float(runtime, "go"));
        }

        [Fact]
        public void AnInstanceMethod_ReadsItsOwnReceiver()
        {
            var runtime = Run(Vec2 + @"
fun go(): float {
    let v = Vec2(10.0, 1.0);
    return v.sum();
}
");

            Assert.Equal(11.0, Float(runtime, "go"));
        }

        #endregion

        #region Arguments and returns

        [Fact]
        public void AValuePassesAsAnArgument_AndComesBackAsAResult()
        {
            var runtime = Run(Vec2 + @"
fun echo(v: Vec2): Vec2 {
    return v;
}

fun go(): float {
    return echo(Vec2(3.0, 4.0)).y;
}
");

            Assert.Equal(4.0, Float(runtime, "go"));
        }

        [Fact]
        public void AValueReturnedFromACall_FlowsIntoALocal()
        {
            var runtime = Run(Vec2 + @"
fun make(): Vec2 {
    return Vec2(6.0, 7.0);
}

fun go(): float {
    let v = make();
    return v.x * v.y;
}
");

            Assert.Equal(42.0, Float(runtime, "go"));
        }

        [Fact]
        public void TwoValuesPassThroughSeparateParameters()
        {
            var runtime = Run(Vec2 + @"
fun dot(a: Vec2, b: Vec2): float {
    return a.x * b.x + a.y * b.y;
}

fun go(): float {
    return dot(Vec2(2.0, 3.0), Vec2(4.0, 5.0));
}
");

            Assert.Equal(23.0, Float(runtime, "go"));
        }

        #endregion

        #region Equality

        [Fact]
        public void EqualValuesCompareEqual_SlotBySlot()
        {
            var runtime = Run(Vec2 + @"
fun same(): bool {
    let a = Vec2(1.0, 2.0);
    let b = Vec2(1.0, 2.0);
    return a == b;
}

fun differ(): bool {
    let a = Vec2(1.0, 2.0);
    let b = Vec2(1.0, 9.0);
    return a != b;
}

fun notEqual(): bool {
    let a = Vec2(1.0, 2.0);
    let b = Vec2(1.0, 2.0);
    return a != b;
}
");

            Assert.True(Int(runtime, "same") != 0);
            Assert.True(Int(runtime, "differ") != 0);
            Assert.Equal(0, Int(runtime, "notEqual"));
        }

        #endregion

        #region Boxing boundaries

        [Fact]
        public void AValueBoxesCrossingAGenericSlot_AndUnboxesComingBack()
        {
            var runtime = Run(Vec2 + @"
fun pick<T>(value: T): T {
    return value;
}

fun go(): float {
    return pick(Vec2(8.0, 9.0)).x;
}
");

            Assert.Equal(8.0, Float(runtime, "go"));
        }

        [Fact]
        public void ANestedValue_FlattensIntoTheOuterBlock()
        {
            var runtime = Run(@"
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

fun go(): int {
    let o = Outer(Inner(41), 1);
    return o.inner.n + o.tag;
}
");

            Assert.Equal(42, Int(runtime, "go"));
        }

        #endregion

        #region Interfaces

        /// <summary>
        /// A multi-field value class satisfying an interface, the supported shape: the member
        /// stays <c>Direct</c> and a synthetic bridge occupies the contract's slot (§6.3), unboxing
        /// the whole width before forwarding - so both the interface call, which boxes with
        /// <c>BoxValue</c> and crosses one reference slot, and the direct-typed call, which never
        /// boxes, compute over every field.
        /// </summary>
        [Fact]
        public void AValueClassSatisfyingAnInterfaceAnswersThroughItsBridge()
        {
            var runtime = Run(@"
interface IMeasure {
    fun lengthSquared(): float;
}

value class Vec2 : IMeasure {
    public let x: float;
    public let y: float;

    public constructor(x: float, y: float) {
        this.x = x;
        this.y = y;
    }

    public fun lengthSquared(): float {
        return this.x * this.x + this.y * this.y;
    }
}

fun throughInterface(): float {
    let m: IMeasure = Vec2(3.0, 4.0);
    return m.lengthSquared();
}

fun direct(): float {
    let v = Vec2(3.0, 4.0);
    return v.lengthSquared();
}
");

            Assert.Equal(25.0, Float(runtime, "direct"));
            Assert.Equal(25.0, Float(runtime, "throughInterface"));
        }

        /// <summary>
        /// A multi-field value class calling one of its own non-Direct methods, reached through a
        /// receiver statically typed as `object` so the call cannot devirtualise to a direct one.
        /// §6.3's boxed-receiver convention now covers a multi-field block, not just a single field:
        /// the caller boxes the whole block into one reference (BoxValue over its full width) and
        /// the callee's prologue unpacks it back into its per-field slots before the body runs.
        /// </summary>
        [Fact]
        public void AMultiFieldValueClassDeclaringANonDirectMethod_DispatchesThroughTheVirtualSlot()
        {
            var runtime = Run(@"
interface IMeasure {
    fun lengthSquared(): float;
}

value class Vec2 : IMeasure {
    public let x: float;
    public let y: float;

    public constructor(x: float, y: float) {
        this.x = x;
        this.y = y;
    }

    public virtual fun lengthSquared(): float {
        return this.x * this.x + this.y * this.y;
    }
}

fun throughInterface(): float {
    let m: IMeasure = Vec2(3.0, 4.0);
    return m.lengthSquared();
}

fun throughObject(): string {
    let o: object = Vec2(3.0, 4.0);
    return o.toString();
}
");

            Assert.Equal(25.0, Float(runtime, "throughInterface"));
            Assert.Equal("Vec2", Text(runtime, "throughObject"));
        }

        #endregion

        #region Diagnostics

        [Fact]
        public void IdentityComparisonOverAValue_IsRefused()
        {
            BuildFails(Vec2 + @"
fun go(a: Vec2, b: Vec2): bool {
    return a === b;
}
", "===");
        }

        [Fact]
        public void AMutableFieldOnAValue_IsRefused()
        {
            BuildFails(@"
value class Bad {
    public var x: int;

    public constructor(x: int) {
        this.x = x;
    }
}
", "'let'");
        }

        [Fact]
        public void AValueTypeThatContainsItself_IsRefused()
        {
            // Refusal arrives either from the binder's direct self-reference check or from the
            // layout walk when the emitter first needs the width - asserting the refusal alone.
            BuildFails(@"
value class Loop {
    public let self: Loop;

    public constructor(self: Loop) {
        this.self = self;
    }
}
", "");
        }

        #endregion
    }
}
