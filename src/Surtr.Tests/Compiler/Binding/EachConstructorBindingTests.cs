#nullable enable

using Surtr.Bytecode.Image;
using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using System;
using System.Linq;
using Xunit;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// Binder-side coverage of §5.x: the interface <c>default</c> clause's validation, the
    /// semantic disambiguation between a collection literal and an index, and the metadata
    /// round-trip that lets an imported module's builders and defaults reach a target-typed
    /// literal.
    /// </summary>
    public sealed class EachConstructorBindingTests
    {
        private const string Root = "D:/proj/src";

        private static SurtrCompilation Compile(string source)
        {
            var project = new SurtrProject(Root);
            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();
            return compilation;
        }

        private static void AssertReports(SurtrCompilation compilation, SurtrDiagnosticCode code)
            => Assert.True(
                compilation.Diagnostics.Any(d => d.Code == code),
                $"Expected {code}, got: " + string.Join("; ", compilation.Diagnostics));

        private const string GoodBag = @"
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
}";

        [Fact]
        public void AValidInterfaceDefaultBinds()
        {
            var compilation = Compile(GoodBag);
            Assert.True(!compilation.HasErrors, string.Join("; ", compilation.Diagnostics));
        }

        [Fact]
        public void AnInterfaceDefaultThatIsNotAClassIsRejected()
        {
            var compilation = Compile(@"
interface IBag<T> default IOther<T>
{
    fun length(): int;
}

interface IOther<T> { fun other(): void; }");
            AssertReports(compilation, SurtrDiagnosticCode.InterfaceDefaultNotClass);
        }

        [Fact]
        public void AnInterfaceDefaultThatDoesNotImplementTheInterfaceIsRejected()
        {
            var compilation = Compile(@"
interface IBag<T> default Other<T>
{
    fun length(): int;
}

class Other<T>
{
    public constructor() each (item: T) { }
}");
            AssertReports(compilation, SurtrDiagnosticCode.InterfaceDefaultNotImplemented);
        }

        [Fact]
        public void AnInterfaceDefaultWithTheWrongTypeArgumentCountIsRejected()
        {
            var compilation = Compile(@"
interface IBag<T> default Other<T, T>
{
    fun length(): int;
}

class Other<A, B> : IBag<A>
{
    public constructor() each (item: A) { }
}");
            AssertReports(compilation, SurtrDiagnosticCode.InterfaceDefaultArity);
        }

        [Fact]
        public void AnInterfaceDefaultWithNoEachConstructorIsRejected()
        {
            var compilation = Compile(@"
interface IBag<T> default Bag<T>
{
    fun length(): int;
}

class Bag<T> : IBag<T>
{
    private var _items: T[];
    public constructor() { _items = []; }
    public fun length(): int => _items.length;
}");
            AssertReports(compilation, SurtrDiagnosticCode.InterfaceDefaultNoEach);
        }

        [Fact]
        public void AValueIndexBindsAsAnIndex()
        {
            var compilation = Compile(@"
class Bag
{
    public fun run(): int
    {
        let a = [10, 20, 30];
        return a[1];
    }
}");
            Assert.True(!compilation.HasErrors, string.Join("; ", compilation.Diagnostics));
        }

        [Fact]
        public void ASingleElementBareLiteralOverATypeIsABuilder()
        {
            var compilation = Compile(@"
class Bag<T>
{
    private var _items: T[];
    public constructor() { _items = []; }
    each (item: T) { _items.push(item); }
    public fun run(): int
    {
        let b = Bag<int>[5];
        return b._items.length;
    }
}");
            Assert.True(!compilation.HasErrors, string.Join("; ", compilation.Diagnostics));
        }

        [Fact]
        public void AnEachArityMismatchIsReported()
        {
            var compilation = Compile(@"
class Bag<T>
{
    private var _items: T[];
    public constructor() { _items = []; }
    each (key: T, value: T) { }
    public fun run(): int
    {
        let b = Bag<int>[5];
        return 0;
    }
}");
            AssertReports(compilation, SurtrDiagnosticCode.BuilderArityMismatch);
        }

        [Fact]
        public void AnEachConstructorWithThreeParametersIsRejected()
        {
            var compilation = Compile(@"
class Bag<T>
{
    public constructor() { }
    each (a: T, b: T, c: T) { }
}");
            AssertReports(compilation, SurtrDiagnosticCode.EachArityInvalid);
        }

        [Fact]
        public void AnEachClauseOnAValueClassConstructorIsRejected()
        {
            var compilation = Compile(@"
value class Bag
{
    public constructor(v: int) { }
    each (item: int) { }
}");
            AssertReports(compilation, SurtrDiagnosticCode.EachOutsideConstructor);
        }

        [Fact]
        public void AnEachConstructorIsNotReachableByAPlainConstruction()
        {
            var compilation = Compile(@"
class Bag<T>
{
    private var _items: T[];
    public constructor() { _items = []; }
    each (item: T) { _items.push(item); }
    public fun run(): Bag<int>
    {
        return Bag<int>();
    }
}");
            AssertReports(compilation, SurtrDiagnosticCode.UnresolvedCall);
        }

        [Fact]
        public void ImportedEachConstructorsAndDefaultsSurviveTheImage()
        {
            byte[] libraryImage = CompileToImage("lib", @"
public interface IBag<T> default Bag<T>
{
    fun length(): int;
    fun get(index: int): T;
}

public class Bag<T> : IBag<T>
{
    private var _items: T[];
    public constructor() { _items = []; }
    each (item: T) { _items.push(item); }
    public fun length(): int => _items.length;
    public fun get(index: int): T => _items[index];
}");

            var project = new SurtrProject(sourceRoot: ".");
            project.AddReference(SurtrModuleImage.FromBytes(libraryImage));
            project.AddSourceFile("driver.surtr", "driver",
                "import lib;\n"
                + "fun run(): int\n"
                + "{\n"
                + "    let explicit = Bag<int>[1, 2, 3];\n"
                + "    let viaDefault: IBag<int> = [4, 5];\n"
                + "    return explicit.length() + viaDefault.length();\n"
                + "}\n");

            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(
                !compilation.Diagnostics.HasErrors,
                "Binding reported: " + string.Join("; ", compilation.Diagnostics));
        }

        private static byte[] CompileToImage(string moduleName, string source)
        {
            var project = new SurtrProject(sourceRoot: ".");
            project.AddSourceFile(moduleName + ".surtr", moduleName, source);

            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(!compilation.Diagnostics.HasErrors, string.Join("; ", compilation.Diagnostics));

            var emitter = new ModuleEmitter(compilation, binder);
            return emitter.EmitImages()[0].ToBytes();
        }

        [Fact]
        public void TheRealStdlibListBindsItsBuilderAndDefault()
        {
            // The real List.surtr (and the Collection.surtr it imports) declare `IList<T> default
            // List<T>` and the builder constructor — this pins that the stdlib's own use of the
            // feature binds, including the runtime-constructor reuse (a plain `ctor(int)` and an
            // `each` `ctor(int)` sharing one method-table slot).
            string root = System.IO.Path.GetFullPath("../../../../../src/Surtr.Stdlib/src/surtr/collections");
            string collection = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "Collection.surtr"));
            string list = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "List.surtr"));

            var project = new SurtrProject(sourceRoot: ".");
            project.AddSourceFile("collections/Collection.surtr", "surtr.collections.Collection", collection);
            project.AddSourceFile("collections/List.surtr", "surtr.collections.List", list);
            project.AddSourceFile("driver.surtr", "driver",
                "import surtr.collections.List;\n"
                + "fun run(): int\n"
                + "{\n"
                + "    let explicit = List<int>[1, 2, 3];\n"
                + "    let withCapacity = List<int>(8)[4, 5];\n"
                + "    let viaDefault: IList<int> = [6, 7, 8];\n"
                + "    return explicit.length + withCapacity.length + viaDefault.length;\n"
                + "}\n");

            using var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();

            Assert.True(
                !compilation.Diagnostics.HasErrors,
                "Binding reported: " + string.Join("; ", compilation.Diagnostics));

            var emitter = new ModuleEmitter(compilation, binder);
            Assert.True(emitter.TryEmit());
        }
    }
}