#nullable enable

using Surtr.Compiler.Compilation;
using Surtr.Compiler.CodeGen;
using Surtr.Runtime;
using Surtr.Runtime.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Surtr.Tests.Compiler.CodeGen
{
    /// <summary>
    /// End-to-end coverage of §5.x: an <c>each</c> clause on a constructor turns it into a
    /// collection builder, <c>[ ... ]</c>/<c>{ ... }</c> literals lower to
    /// <c>ObjNew</c> + constructor + one <c>$fill$</c> call per element, and an interface's
    /// <c>default</c> clause drives the target-typed form.
    /// </summary>
    public sealed class EachConstructorTests : IDisposable
    {
        private const string Root = "D:/proj/src";

        private readonly List<IDisposable> _owned = new List<IDisposable>();

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
                _owned[i].Dispose();
        }

        private ModuleEmitter Emit(string source)
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
                "Emission reported: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));

            return emitter;
        }

        private SurtrRuntime Run(string source)
        {
            var runtime = new SurtrRuntime();
            _owned.Add(runtime);

            foreach (var module in Emit(source).Modules)
                runtime.LoadModule(module);

            return runtime;
        }

        private static SurtrValue Call(SurtrRuntime runtime, string name, params SurtrValue[] arguments)
        {
            Assert.True(runtime.TryGetModule("game.core.Test", out var module), "No test module was loaded.");
            Assert.True(module.TryGetMethods(name, out var overloads), $"'{name}' declares no function.");
            return runtime.Invoke(overloads[0], arguments);
        }

        private const string BagSource = @"
class Bag<T>
{
    private var _items: T[];
    private var _capacity: int;

    public constructor(capacity: int = 4)
    {
        _items = [];
        _capacity = capacity;
    }
    each (item: T)
    {
        _items.push(item);
    }

    public fun length(): int => _items.length;
    public fun get(index: int): T => _items[index];
    public fun capacity(): int => _capacity;
}";

        [Fact]
        public void AnArrayLiteralOverABuilderConstructsAndFillsInOrder()
        {
            var runtime = Run(BagSource + @"
public fun build(): int
{
    let b = Bag<int>[10, 20, 30];
    return b.length();
}");
            Assert.Equal(3L, Call(runtime, "build").AsInt);
        }

        [Fact]
        public void AnArrayLiteralWithConstructorArgumentsPassesThemToTheConstructor()
        {
            var runtime = Run(BagSource + @"
public fun build(): int
{
    let b = Bag<int>(8)[1, 2, 3, 4];
    return b.capacity() + b.get(2);
}");
            Assert.Equal(11L, Call(runtime, "build").AsInt);
        }

        [Fact]
        public void AnEmptyLiteralBuildsAnEmptyObjectWithoutFills()
        {
            var runtime = Run(BagSource + @"
public fun build(): int
{
    let b = Bag<int>[];
    return b.length();
}");
            Assert.Equal(0L, Call(runtime, "build").AsInt);
        }

        [Fact]
        public void ADictLiteralOverATwoParameterEachBuildsEntryByEntry()
        {
            var runtime = Run(@"
class PairBag<K, V>
{
    private var _keys: K[];
    private var _values: V[];

    public constructor()
    {
        _keys = [];
        _values = [];
    }
    each (key: K, value: V)
    {
        _keys.push(key);
        _values.push(value);
    }

    public fun length(): int => _keys.length;
    public fun valueOf(key: K): V
    {
        for (i in 0.._keys.length)
        {
            if (_keys[i] == key) return _values[i];
        }
        return _values[0];
    }
}

public fun build(): int
{
    let m = PairBag<string, int>{ ""x"": 10, ""y"": 15 };
    return m.length() + m.valueOf(""y"");
}");
            Assert.Equal(17L, Call(runtime, "build").AsInt);
        }

        [Fact]
        public void ABareIdentifierBuilderWorksForNonGenericTypes()
        {
            var runtime = Run(@"
class IntList
{
    private var _items: int[];
    private var _length: int;

    public constructor()
    {
        _items = [];
        _length = 0;
    }
    each (item: int)
    {
        _items.push(item);
    }

    public fun length(): int => _items.length;
}

public fun build(): int
{
    let i = IntList[1, 2, 3, 4, 5];
    return i.length();
}");
            Assert.Equal(5L, Call(runtime, "build").AsInt);
        }

        [Fact]
        public void ATargetTypedLiteralOverAnInterfaceUsesItsDeclaredDefault()
        {
            var runtime = Run(@"
interface IBag<T> default Bag<T>
{
    fun length(): int;
    fun get(index: int): T;
}

class Bag<T> : IBag<T>
{
    private var _items: T[];

    public constructor()
    {
        _items = [];
    }
    each (item: T)
    {
        _items.push(item);
    }

    public fun length(): int => _items.length;
    public fun get(index: int): T => _items[index];
}

public fun build(): int
{
    let b: IBag<int> = [1, 2, 3];
    return b.length();
}");
            Assert.Equal(3L, Call(runtime, "build").AsInt);
        }

        [Fact]
        public void TheFillMethodIsNotReachableByIdentifier()
        {
            var runtime = Run(BagSource + @"
public fun build(): int
{
    let b = Bag<int>[1];
    return b.length();
}");
            Assert.True(runtime.TryGetModule("game.core.Test", out var module));
            Assert.False(module.TryGetMethods("$fill$Bag$0", out _), "$fill$ is unreachable by name.");
        }

        [Fact]
        public void AnEachConstructorIsNotCallableAsAPlainConstruction()
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", BagSource + @"
public fun build(): Bag<int>
{
    return Bag<int>();
}");
            using var compilation = SurtrCompilation.Create(project);
            compilation.Bind().BindBodies();
            Assert.True(compilation.HasErrors);
        }
    }
}