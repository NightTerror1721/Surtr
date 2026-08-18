#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using System.Linq;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// Covers the binder's first two phases: every declared type gets a symbol, then every base
    /// type, interface and member signature is resolved against the complete set. After these, a
    /// source type and one imported from a <c>.surtrc</c> are the same kind of thing.
    /// </summary>
    public sealed class BinderTests
    {
        private const string Root = "D:/proj/src";

        private static Binder Bind(string source, string path = "game/core/Test.surtr")
            => Bind(out _, (path, source));

        private static Binder Bind(out SurtrCompilation compilation, params (string Path, string Source)[] files)
        {
            var project = new SurtrProject(Root);
            foreach (var file in files)
                project.AddSourceFile(Root + "/" + file.Path, file.Source);

            compilation = SurtrCompilation.Create(project);
            return compilation.Bind();
        }

        private static NamedTypeSymbol Type(Binder binder, string module, string name)
            => binder.Modules[module].Types.Single(t => t.Name == name);

        private static void AssertNoErrors(SurtrCompilation compilation)
        {
            Assert.True(
                !compilation.HasErrors,
                "Unexpected: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        private static void AssertReports(SurtrCompilation compilation, SurtrDiagnosticCode code)
        {
            Assert.True(
                compilation.Diagnostics.Any(d => d.Code == code),
                $"Expected {code}, got: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        #region Declaration
        [Fact]
        public void EveryDeclaredTypeGetsASymbol()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Entity { }\ninterface IThing { }\nenum Suit { Hearts, Spades }\nsingleton Registry { }"));

            AssertNoErrors(compilation);

            Assert.Equal(TypeSymbolKind.Class, Type(binder, "game.core", "Entity").TypeKind);
            Assert.Equal(TypeSymbolKind.Interface, Type(binder, "game.core", "IThing").TypeKind);
            Assert.Equal(TypeSymbolKind.Enum, Type(binder, "game.core", "Suit").TypeKind);
            Assert.Equal(TypeSymbolKind.Singleton, Type(binder, "game.core", "Registry").TypeKind);
        }

        [Fact]
        public void ANestedTypeIsQualificationAndKeepsItsContainer()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Entity { class Handle { } }"));

            AssertNoErrors(compilation);

            var entity = Type(binder, "game.core", "Entity");
            var handle = Assert.Single(entity.NestedTypes);

            Assert.Equal("Handle", handle.Name);
            Assert.Same(entity, handle.ContainingType);
            Assert.Equal("game.core:Entity.Handle", handle.FullMetadataName);
        }

        [Fact]
        public void TwoAritiesOfOneNameCoexist()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Result<T> { }\nclass Result<T, E> { }"));

            AssertNoErrors(compilation);

            var declared = binder.Modules["game.core"].FindTypes("Result");
            Assert.Equal(2, declared.Count);
            Assert.Contains(declared, t => t.Arity == 1);
            Assert.Contains(declared, t => t.Arity == 2);
        }

        [Fact]
        public void TwoDeclarationsOfOneNameAndArityCollide()
        {
            Bind(out var compilation, ("game/core/Test.surtr", "class Entity { }\nclass Entity { }"));

            AssertReports(compilation, SurtrDiagnosticCode.DuplicateDeclaration);
        }

        [Fact]
        public void FilesInOneModuleShareItsDeclarations()
        {
            var binder = Bind(out var compilation,
                ("game/core/A.surtr", "class Vec2 { }"),
                ("game/core/B.surtr", "class Entity { public var position: Vec2; }"));

            AssertNoErrors(compilation);

            var position = Type(binder, "game.core", "Entity").Members.OfType<FieldSymbol>().Single();
            Assert.Same(Type(binder, "game.core", "Vec2"), position.Type);
        }
        #endregion

        #region Type resolution
        [Fact]
        public void TheBuiltInTypeNamesResolve()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Holder {\n"
                + "  public var a: int;\n"
                + "  public var b: float;\n"
                + "  public var c: bool;\n"
                + "  public var d: char;\n"
                + "  public var e: string;\n"
                + "  public var f: range;\n"
                + "  public var g: unknown;\n"
                + "}"));

            AssertNoErrors(compilation);

            var factory = compilation.TypeFactory;
            var fields = Type(binder, "game.core", "Holder").Members.OfType<FieldSymbol>().ToList();

            Assert.Same(factory.Int, fields[0].Type);
            Assert.Same(factory.Float, fields[1].Type);
            Assert.Same(factory.Bool, fields[2].Type);
            Assert.Same(factory.Char, fields[3].Type);
            Assert.Same(factory.String, fields[4].Type);
            Assert.Same(factory.Range, fields[5].Type);
            Assert.Same(factory.Unknown, fields[6].Type);
        }

        [Fact]
        public void CompositeTypesResolveThroughTheFactory()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Holder {\n"
                + "  public var a: int[];\n"
                + "  public var b: {int: string};\n"
                + "  public var c: (int, float);\n"
                + "  public var d: (int, int) -> float;\n"
                + "  public var e: int?;\n"
                + "}"));

            AssertNoErrors(compilation);

            var factory = compilation.TypeFactory;
            var fields = Type(binder, "game.core", "Holder").Members.OfType<FieldSymbol>().ToList();

            Assert.Same(factory.Array(factory.Int), fields[0].Type);
            Assert.Same(factory.Dictionary(factory.Int, factory.String), fields[1].Type);
            Assert.Same(factory.Tuple(new TypeSymbol[] { factory.Int, factory.Float }), fields[2].Type);
            Assert.Same(factory.Closure(new TypeSymbol[] { factory.Int, factory.Int }, factory.Float), fields[3].Type);
            Assert.Same(factory.Int.Nullable, fields[4].Type);
        }

        [Fact]
        public void AGenericTypeIsConstructedAtEachUse()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Box<T> { }\nclass Holder { public var a: Box<int>; public var b: Box<string>; }"));

            AssertNoErrors(compilation);

            var box = Type(binder, "game.core", "Box");
            var fields = Type(binder, "game.core", "Holder").Members.OfType<FieldSymbol>().ToList();

            Assert.NotSame(fields[0].Type, fields[1].Type);
            Assert.Same(box, ((NamedTypeSymbol)fields[0].Type).Definition);
            Assert.Same(box, ((NamedTypeSymbol)fields[1].Type).Definition);
        }

        [Fact]
        public void ATypeParameterIsInScopeInsideItsType()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Box<T> { private let _value: T; }"));

            AssertNoErrors(compilation);

            var box = Type(binder, "game.core", "Box");
            var field = box.Members.OfType<FieldSymbol>().Single();

            Assert.Same(box.TypeParameters[0], field.Type);
        }

        [Fact]
        public void ANestedTypeIsReachableThroughItsContainer()
        {
            // §2.6: a nested type takes a visibility like any other member, so reaching it from
            // outside its container needs one written.
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Entity { public class Handle { } }\nclass Holder { public var h: Entity.Handle; }"));

            AssertNoErrors(compilation);

            var handle = Type(binder, "game.core", "Entity").NestedTypes.Single();
            Assert.Same(handle, Type(binder, "game.core", "Holder").Members.OfType<FieldSymbol>().Single().Type);
        }

        [Fact]
        public void AFullyQualifiedNameWorksWithoutAnImport()
        {
            // §2.1: an import is convenience, not a requirement to reference something.
            var binder = Bind(out var compilation,
                ("game/math/Vec2.surtr", "public class Vec2 { }"),
                ("game/core/Entity.surtr", "class Entity { public var p: game.math.Vec2; }"));

            AssertNoErrors(compilation);

            Assert.Same(
                Type(binder, "game.math", "Vec2"),
                Type(binder, "game.core", "Entity").Members.OfType<FieldSymbol>().Single().Type);
        }

        [Fact]
        public void AnUnknownNameIsReportedOnce()
        {
            Bind(out var compilation, ("game/core/Test.surtr", "class Holder { public var a: Nope; }"));

            AssertReports(compilation, SurtrDiagnosticCode.UnresolvedName);
            Assert.Single(compilation.Diagnostics);
        }

        [Fact]
        public void TheWrongNumberOfTypeArgumentsIsReported()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Box<T> { }\nclass Holder { public var a: Box<int, string>; }"));

            AssertReports(compilation, SurtrDiagnosticCode.WrongTypeArgumentCount);
        }

        /// <summary>
        /// §5.3.1: <c>array&lt;T&gt;</c>/<c>dict&lt;K,V&gt;</c>/<c>tuple&lt;...&gt;</c> are a pure
        /// alias for the symbolic forms — the literal same interned <see cref="TypeSymbol"/>, not
        /// merely a convertible one, so a field declared through either spelling is the same field.
        /// </summary>
        [Fact]
        public void TheNameableFormOfACompositeIsTheSameTypeAsItsSymbolicForm()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Holder {\n"
                    + "  public var a1: array<int>;\n"
                    + "  public var a2: int[];\n"
                    + "  public var d1: dict<int, string>;\n"
                    + "  public var d2: {int: string};\n"
                    + "  public var t1: tuple<int, string>;\n"
                    + "  public var t2: (int, string);\n"
                    + "}"));

            AssertNoErrors(compilation);

            var fields = Type(binder, "game.core", "Holder").Members.OfType<FieldSymbol>().ToList();

            Assert.Same(fields[0].Type, fields[1].Type);
            Assert.Same(fields[2].Type, fields[3].Type);
            Assert.Same(fields[4].Type, fields[5].Type);
        }

        /// <summary>An explicit <c>tuple&lt;&gt;</c> names the same 0-arity/unit tuple a bare <c>()</c> element list would.</summary>
        [Fact]
        public void AnExplicitEmptyDiamondNamesTheUnitTuple()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Holder { public var u: tuple<>; }"));

            AssertNoErrors(compilation);

            var factory = compilation.TypeFactory;
            var field = Type(binder, "game.core", "Holder").Members.OfType<FieldSymbol>().Single();

            Assert.Same(factory.Tuple(System.Array.Empty<TypeSymbol>()), field.Type);
        }

        /// <summary>
        /// The redirect is keyed on the built-in's own identity, not the name "array" in the
        /// abstract — a module that shadows it with its own declaration keeps meaning that
        /// declaration, exactly as §1.1 already promises for any other built-in name.
        /// </summary>
        [Fact]
        public void AUserDeclarationShadowingArrayIsNotRedirectedToTheBuiltIn()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class array<T> { public let tag: T; }\nclass Holder { public var a: array<int>; }"));

            AssertNoErrors(compilation);

            var userArray = Type(binder, "game.core", "array");
            var field = Type(binder, "game.core", "Holder").Members.OfType<FieldSymbol>().Single();

            Assert.Same(userArray, ((NamedTypeSymbol)field.Type).Definition);
            Assert.NotSame(compilation.TypeFactory.Array(compilation.TypeFactory.Int), field.Type);
        }

        /// <summary>Matches <c>TupPack</c>'s 255-element arity cap (§5.3.1), diagnosed here rather than left to fail only at emission.</summary>
        [Fact]
        public void ATupleWithMoreThan255ElementsIsReported()
        {
            var elements = string.Join(", ", System.Linq.Enumerable.Repeat("int", 256));

            Bind(out var compilation, ("game/core/Test.surtr",
                $"class Holder {{ public var t: tuple<{elements}>; }}"));

            AssertReports(compilation, SurtrDiagnosticCode.WrongTypeArgumentCount);
        }
        #endregion

        #region Imports
        [Fact]
        public void ANamedImportBringsOneTypeIntoScope()
        {
            var binder = Bind(out var compilation,
                ("game/math/Vec2.surtr", "public class Vec2 { }\npublic class Vec3 { }"),
                ("game/core/Entity.surtr", "import game.math.Vec2;\nclass Entity { public var p: Vec2; }"));

            AssertNoErrors(compilation);

            Assert.Same(
                Type(binder, "game.math", "Vec2"),
                Type(binder, "game.core", "Entity").Members.OfType<FieldSymbol>().Single().Type);
        }

        [Fact]
        public void AWildcardImportBringsThemAllIn()
        {
            var binder = Bind(out var compilation,
                ("game/math/Vec2.surtr", "public class Vec2 { }\npublic class Vec3 { }"),
                ("game/core/Entity.surtr", "import game.math.*;\nclass Entity { public var p: Vec3; }"));

            AssertNoErrors(compilation);

            Assert.Same(
                Type(binder, "game.math", "Vec3"),
                Type(binder, "game.core", "Entity").Members.OfType<FieldSymbol>().Single().Type);
        }

        [Fact]
        public void ANameTwoImportsBothProvideIsReportedAtTheUse()
        {
            // §2.1 puts this error at the point of use, not at the import line: importing both is
            // fine right up until someone writes the name.
            Bind(out var compilation,
                ("a/Vec2.surtr", "class Vec2 { }"),
                ("b/Vec2.surtr", "class Vec2 { }"),
                ("game/core/Entity.surtr", "import a.*;\nimport b.*;\nclass Entity { public var p: Vec2; }"));

            AssertReports(compilation, SurtrDiagnosticCode.AmbiguousName);
        }

        [Fact]
        public void ImportingBothWithoutWritingTheNameIsFine()
        {
            Bind(out var compilation,
                ("a/Vec2.surtr", "class Vec2 { }"),
                ("b/Vec2.surtr", "class Vec2 { }"),
                ("game/core/Entity.surtr", "import a.*;\nimport b.*;\nclass Entity { }"));

            AssertNoErrors(compilation);
        }

        [Fact]
        public void TheStandardLibraryIsInScopeWithoutAnImport()
        {
            // §13: `surtr` is in scope in every file with no import line, which is what lets the
            // spec write `Exception` and `IComparable<T>` unqualified throughout.
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Holder { public var c: IComparable<int>; }"));

            AssertNoErrors(compilation);

            var field = Type(binder, "game.core", "Holder").Members.OfType<FieldSymbol>().Single();
            var comparable = Assert.IsType<NamedTypeSymbol>(field.Type);

            Assert.Equal("IComparable", comparable.Definition.Name);
            Assert.Same(compilation.TypeFactory.Int, Assert.Single(comparable.TypeArguments));
        }

        [Fact]
        public void ABuiltInClassImportsAsTheFactorysOwnSymbol()
        {
            // The `surtr` module holds the built-in classes too, so importing it must land on the
            // same `int` the factory already has - or every use would be an ambiguous name.
            var binder = Bind(out var compilation, ("game/core/Test.surtr", "class Holder { public var v: int; }"));

            AssertNoErrors(compilation);

            Assert.Same(
                compilation.TypeFactory.Int,
                Type(binder, "game.core", "Holder").Members.OfType<FieldSymbol>().Single().Type);
        }

        [Fact]
        public void ADeclarationShadowsTheStandardLibrary()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "interface IComparable<T> { }\nclass Holder { public var c: IComparable<int>; }"));

            AssertNoErrors(compilation);

            var field = Type(binder, "game.core", "Holder").Members.OfType<FieldSymbol>().Single();

            Assert.Same(
                Type(binder, "game.core", "IComparable"),
                Assert.IsType<NamedTypeSymbol>(field.Type).Definition);
        }

        [Fact]
        public void ALocalDeclarationShadowsAnImportedOne()
        {
            var binder = Bind(out var compilation,
                ("game/math/Vec2.surtr", "class Vec2 { }"),
                ("game/core/Entity.surtr", "import game.math.*;\nclass Vec2 { }\nclass Entity { public var p: Vec2; }"));

            AssertNoErrors(compilation);

            Assert.Same(
                Type(binder, "game.core", "Vec2"),
                Type(binder, "game.core", "Entity").Members.OfType<FieldSymbol>().Single().Type);
        }
        #endregion

        #region Aliases
        [Fact]
        public void AnAliasIsTransparent()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "alias EntityId = int;\nclass Holder { public var id: EntityId; }"));

            AssertNoErrors(compilation);

            // §2.7: EntityId and int are the same type everywhere.
            Assert.Same(
                compilation.TypeFactory.Int,
                Type(binder, "game.core", "Holder").Members.OfType<FieldSymbol>().Single().Type);
        }

        [Fact]
        public void AGenericAliasSubstitutesAtEachUse()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "alias IntMap<V> = {int: V};\nclass Holder { public var m: IntMap<string>; }"));

            AssertNoErrors(compilation);

            Assert.Same(
                compilation.TypeFactory.Dictionary(compilation.TypeFactory.Int, compilation.TypeFactory.String),
                Type(binder, "game.core", "Holder").Members.OfType<FieldSymbol>().Single().Type);
        }

        [Fact]
        public void AnAliasMayTargetAnotherAliasDeclaredLater()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "alias A = B;\nalias B = int;\nclass Holder { public var v: A; }"));

            AssertNoErrors(compilation);

            Assert.Same(
                compilation.TypeFactory.Int,
                Type(binder, "game.core", "Holder").Members.OfType<FieldSymbol>().Single().Type);
        }

        [Fact]
        public void AliasesThatDefineOneAnotherAreReported()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "alias A = B;\nalias B = A;\nclass Holder { public var v: A; }"));

            AssertReports(compilation, SurtrDiagnosticCode.AliasCycle);
        }

        [Fact]
        public void TwoMembersDifferingOnlyByAnAliasAreADuplicate()
        {
            // §2.7 states this consequence outright: an alias is not a distinct type, so this is a
            // duplicate rather than an overload.
            Bind(out var compilation, ("game/core/Test.surtr",
                "alias EntityId = int;\n"
                + "class Store {\n"
                + "  public fun put(id: int): void { }\n"
                + "  public fun put(id: EntityId): void { }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.DuplicateOverload);
        }
        #endregion

        #region Hierarchy
        [Fact]
        public void ABaseClassAndInterfacesAreToldApartByTheirOwnMetadata()
        {
            // §2.2: nothing in the syntax distinguishes them; each name's own kind decides.
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "interface IBar { }\ninterface IBaz { }\nclass Base { }\nclass Foo : Base, IBar, IBaz { }"));

            AssertNoErrors(compilation);

            var foo = Type(binder, "game.core", "Foo");
            Assert.Same(Type(binder, "game.core", "Base"), foo.BaseType);
            Assert.Equal(2, foo.Interfaces.Count);
        }

        [Fact]
        public void AClassWithNoBaseSitsAtDepthZero()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr", "class Foo { }"));

            AssertNoErrors(compilation);
            Assert.Null(Type(binder, "game.core", "Foo").BaseType);
        }

        [Fact]
        public void TwoBaseClassesAreReported()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "class A { }\nclass B { }\nclass C : A, B { }"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidBaseType);
        }

        [Fact]
        public void ExtendingASealedClassIsReported()
        {
            Bind(out var compilation, ("game/core/Test.surtr", "sealed class A { }\nclass B : A { }"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidBaseType);
        }

        [Fact]
        public void AnInterfaceMayNotExtendAClass()
        {
            Bind(out var compilation, ("game/core/Test.surtr", "class A { }\ninterface IB : A { }"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidBaseType);
        }

        [Fact]
        public void AHierarchyThatLoopsIsReported()
        {
            Bind(out var compilation, ("game/core/Test.surtr", "class A : B { }\nclass B : A { }"));

            AssertReports(compilation, SurtrDiagnosticCode.InheritanceCycle);
        }

        [Fact]
        public void AnEnumIsASealedClass()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr", "enum Suit { Hearts, Spades }"));

            AssertNoErrors(compilation);

            var suit = Type(binder, "game.core", "Suit");
            Assert.True(suit.IsSealed);

            // Each case is a static readonly of the enum's own type.
            var cases = suit.Members.OfType<FieldSymbol>().ToList();
            Assert.Equal(2, cases.Count);
            Assert.All(cases, c => Assert.True(c.IsStatic && c.IsReadOnly));
            Assert.All(cases, c => Assert.Same(suit, c.Type));
        }
        #endregion

        #region Members
        [Fact]
        public void APropertyBecomesAccessorMethodsTheLinkerCanFind()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Entity { public health: int { get; set; } }"));

            AssertNoErrors(compilation);

            var entity = Type(binder, "game.core", "Entity");
            var property = entity.Members.OfType<PropertySymbol>().Single();

            Assert.Equal("get_health", property.Getter!.Name);
            Assert.Equal("set_health", property.Setter!.Name);
            Assert.Same(compilation.TypeFactory.Int, property.Getter.ReturnType);
            Assert.Same(compilation.TypeFactory.Void, property.Setter.ReturnType);
            Assert.Same(compilation.TypeFactory.Int, property.Setter.Parameters[0].Type);
        }

        [Fact]
        public void APropertyAccessorCollidesWithAMethodOfTheSameName()
        {
            // The runtime only ever sees get_x/set_x, so a declared get_x is the same table entry
            // a property's synthesized getter occupies - a collision no name check can catch.
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Entity {\n"
                + "  public health: int { get; set; }\n"
                + "  public fun get_health(): int { return 0; }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.DuplicateOverload);
        }

        [Fact]
        public void APropertySetterCollidesWithAMethodOfTheSameName()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Entity {\n"
                + "  public health: int { get; set; }\n"
                + "  public fun set_health(value: int): void { }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.DuplicateOverload);
        }

        [Fact]
        public void AModulePropertyAccessorCollidesWithAModuleMethod()
        {
            // A module property synthesizes static get_x/set_x methods just like a class one does.
            Bind(out var compilation, ("game/core/Test.surtr",
                "public health: int { get; set; }\n"
                + "public fun get_health(): int { return 0; }\n"));

            AssertReports(compilation, SurtrDiagnosticCode.DuplicateOverload);
        }

        [Fact]
        public void TwoUnitsOfOneModuleCollideOnASharedSignature()
        {
            // A module's method table is one table no matter how many files declared it, so a
            // duplicate must be found across units, not just within one.
            Bind(out var compilation,
                ("game/core/A.surtr", "public fun f(): int { return 1; }\n"),
                ("game/core/B.surtr", "public fun f(): int { return 2; }\n"));

            AssertReports(compilation, SurtrDiagnosticCode.DuplicateOverload);
        }

        [Fact]
        public void AModuleFunctionAndAClassMethodOfTheSameNameDoNotCollide()
        {
            // A class's SignatureSet and its containing module's are separate instances (one per
            // type, one per module), so a class method never contends for the same table slot as
            // a module-level function of the same name - unlike an accessor and a written method,
            // which really do share one.
            Bind(out var compilation, ("game/core/Test.surtr",
                "public fun greet(): int { return 1; }\n"
                + "class Greeter {\n"
                + "  public fun greet(): int { return 2; }\n"
                + "}"));

            AssertNoErrors(compilation);
        }

        [Fact]
        public void AMethodKeepsItsModifiers()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "abstract class Animal {\n"
                + "  public abstract fun speak(): string;\n"
                + "  public virtual fun move(): void { }\n"
                + "  public static fun count(): int { return 0; }\n"
                + "}"));

            AssertNoErrors(compilation);

            var animal = Type(binder, "game.core", "Animal");
            var methods = animal.Members.OfType<MethodSymbol>().ToDictionary(m => m.Name);

            Assert.True(animal.IsAbstract);
            Assert.Equal(MethodDispatch.Abstract, methods["speak"].Dispatch);
            Assert.Equal(MethodDispatch.Virtual, methods["move"].Dispatch);
            Assert.True(methods["count"].IsStatic);
            Assert.Equal(MethodDispatch.Direct, methods["count"].Dispatch);
        }

        [Fact]
        public void AConstructorTakesTheNameTheEmitterUses()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Vec2 { constructor(x: float, y: float) { } }"));

            AssertNoErrors(compilation);

            var ctor = Type(binder, "game.core", "Vec2").Members.OfType<MethodSymbol>().Single();

            Assert.Equal(MemberNames.Constructor, ctor.Name);
            Assert.Equal(MethodRole.Constructor, ctor.Role);
            Assert.Equal(2, ctor.Parameters.Count);
        }

        [Fact]
        public void AnOperatorTakesItsMangledNameAndIsAlwaysPublicStatic()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Vec2 {\n"
                + "  operator+(a: Vec2, b: Vec2): Vec2 { return a; }\n"
                + "  operator-(v: Vec2): Vec2 { return v; }\n"
                + "  operator<=>(a: Vec2, b: Vec2): int { return 0; }\n"
                + "}"));

            AssertNoErrors(compilation);

            var names = Type(binder, "game.core", "Vec2").Members.OfType<MethodSymbol>().ToList();

            Assert.Contains(names, m => m.Name == "op_+");
            Assert.Contains(names, m => m.Name == "op_-u");
            Assert.Contains(names, m => m.Name == "op_<=>");
            Assert.All(names, m => Assert.True(m.IsStatic && m.Accessibility == Accessibility.Public));
        }

        [Fact]
        public void ModuleLevelMembersLandOnTheModule()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "public var counter: int = 0;\npublic fun step(): void { }"));

            AssertNoErrors(compilation);

            var module = binder.Modules["game.core"];
            Assert.Equal("counter", Assert.Single(module.Fields).Name);
            Assert.Equal("step", Assert.Single(module.Methods).Name);

            // There are no true globals: a module member is a static of its module.
            Assert.True(module.Fields[0].IsStatic);
            Assert.True(module.Methods[0].IsStatic);
        }
        #endregion

        #region Value classes and interfaces
        [Fact]
        public void AValueClassKnowsTheFieldItWraps()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "value class EntityId { public let value: int; constructor(value: int) { } }"));

            AssertNoErrors(compilation);

            var entityId = Type(binder, "game.core", "EntityId");

            Assert.Equal(TypeSymbolKind.ValueClass, entityId.TypeKind);
            Assert.Same(compilation.TypeFactory.Int, entityId.UnderlyingType);
            Assert.True(entityId.IsSealed);
        }

        [Fact]
        public void AValueClassWithTwoFieldsHasNothingToEraseTo()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "value class Pair { public let a: int; public let b: int; }"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidValueClass);
        }

        [Fact]
        public void ANativeLetCannotBeTheFieldAValueClassWraps()
        {
            // A native `let` has no backing storage - it is a property in disguise - so it cannot
            // be the one field §2.9 needs a value class to erase to. Excluded from BindMembers's
            // `letFields` count on purpose, this reports zero instance fields rather than one.
            Bind(out var compilation, ("game/core/Test.surtr",
                "value class EntityId { public native let raw: int; }"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidValueClass);
        }

        [Fact]
        public void AValueClassCannotExtend()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Base { }\nvalue class EntityId : Base { public let value: int; }"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidValueClass);
        }

        [Fact]
        public void AnInterfaceCannotDeclareAField()
        {
            Bind(out var compilation, ("game/core/Test.surtr", "interface IThing { var x: int; }"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidInterfaceMember);
        }

        [Fact]
        public void AnInterfaceCannotDeclareAStatic()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "interface IThing { static fun make(): int; }"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidInterfaceMember);
        }

        [Fact]
        public void AnInterfaceCannotCarryADefaultImplementation()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "interface IThing { fun doThing(): void { } }"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidInterfaceMember);
        }

        [Fact]
        public void AnInterfaceCannotDeclareANativeMethod()
        {
            // A native method has a real body - the host's - so it is exactly as much a default
            // implementation as one written in Surtr, and §2.3 allows neither. Body is null for a
            // native declaration, so the check just above (no body) does not catch this on its own.
            Bind(out var compilation, ("game/core/Test.surtr",
                "interface IThing { native fun doThing(): void; }"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidInterfaceMember);
        }

        [Fact]
        public void AnInterfaceCannotDeclareANativeProperty()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "interface IThing { native x: int { get; } }"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidInterfaceMember);
        }

        [Fact]
        public void AnInterfacesMembersAreAbstractAndPublic()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "interface IShape { fun area(): float; name: string { get; } }"));

            AssertNoErrors(compilation);

            var shape = Type(binder, "game.core", "IShape");
            foreach (var method in shape.Members.OfType<MethodSymbol>())
            {
                Assert.Equal(MethodDispatch.Abstract, method.Dispatch);
                Assert.Equal(Accessibility.Public, method.Accessibility);
            }
        }
        #endregion

        #region Overloads
        [Fact]
        public void RealOverloadsAreAccepted()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Store {\n"
                + "  public fun put(a: int): void { }\n"
                + "  public fun put(a: string): void { }\n"
                + "  public fun put(a: int, b: int): void { }\n"
                + "}"));

            AssertNoErrors(compilation);
        }

        [Fact]
        public void TwoOverloadsDifferingOnlyByReferenceNullabilityCollide()
        {
            // A reference's nullability never reaches a descriptor, so these are one signature in
            // the method table with nothing left to diagnose them.
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Foo { }\n"
                + "class Store {\n"
                + "  public fun put(a: Foo): void { }\n"
                + "  public fun put(a: Foo?): void { }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.DuplicateOverload);
        }

        [Fact]
        public void ANullablePrimitiveIsStillItsOwnType()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Store {\n"
                + "  public fun put(a: int): void { }\n"
                + "  public fun put(a: int?): void { }\n"
                + "}"));

            AssertNoErrors(compilation);
        }

        [Fact]
        public void AValueClassAndTheTypeItErasesToCollide()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "value class EntityId { public let value: int; }\n"
                + "class Store {\n"
                + "  public fun put(a: EntityId): void { }\n"
                + "  public fun put(a: int): void { }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.DuplicateOverload);
        }

        [Fact]
        public void TwoTypeParametersOfOneTypeEraseTogether()
        {
            // Java's "same erasure", from the same cause: G<n> is written as E in a signature key.
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Wrapper<T, U> {\n"
                + "  public fun take(a: T): void { }\n"
                + "  public fun take(a: U): void { }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.DuplicateOverload);
        }

        [Fact]
        public void TwoConversionsToDifferentTargetsAreNotADuplicate()
        {
            // operator as is overloaded on its target, which a signature key excludes - so the
            // target joins the key here exactly as it joins the name at emit.
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Vec3 { }\n"
                + "class Vec2 {\n"
                + "  operator as Vec3(v: Vec2) { return Vec3(); }\n"
                + "  operator as string(v: Vec2) { return \"vec\"; }\n"
                + "}"));

            AssertNoErrors(compilation);
        }

        [Fact]
        public void TwoConversionsToOneTargetAreADuplicate()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Vec2 {\n"
                + "  operator as string(v: Vec2) { return \"a\"; }\n"
                + "  operator as string(v: Vec2) { return \"b\"; }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.DuplicateOverload);
        }

        [Fact]
        public void TheUnaryAndBinaryMinusAreSeparateOverloads()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Vec2 {\n"
                + "  operator-(a: Vec2, b: Vec2): Vec2 { return a; }\n"
                + "  operator-(v: Vec2): Vec2 { return v; }\n"
                + "}"));

            AssertNoErrors(compilation);
        }

        [Fact]
        public void AnOperatorMustTakeTheDeclaringTypeAmongItsOperands()
        {
            // §5.6: a type cannot define how two types foreign to it interact.
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Foo {\n"
                + "  operator+(a: int, b: int): int { return a + b; }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidOperatorSignature);
        }

        [Fact]
        public void AStaticOperatorInASubclassMustTakeTheSubclassItselfAmongItsOperands()
        {
            // §5.6: a *static* operator has no override to preserve a slot for, so a subclass
            // cannot satisfy the rule merely because its own ancestor happens to be one of the
            // operand types - the ancestor-walking leniency belongs to an instance operator's
            // receiver alone, never to a static operator's operands.
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Base { }\n"
                + "class Foo : Base {\n"
                + "  operator+(a: Base, b: Base): Base { return a; }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidOperatorSignature);
        }

        [Fact]
        public void AnIndexerMustTakeTheDeclaringTypeAsItsReceiver()
        {
            // The index and value operate on the receiver, so only the receiver has to be the
            // declaring type — a foreign receiver is an indexer that belongs to nobody.
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Foo {\n"
                + "  operator[](a: int, i: int): int { return i; }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidOperatorSignature);
        }

        [Fact]
        public void AConversionMustTakeTheDeclaringTypeAsItsSource()
        {
            // operator as names its target as the return, so its single parameter is the only
            // operand — and it has to be the type declaring the conversion.
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Vec2 { }\n"
                + "class Foo {\n"
                + "  operator as string(a: Vec2) { return \"vec\"; }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidOperatorSignature);
        }

        [Fact]
        public void AGenericClasssOwnConstructionIsItsDeclaringType()
        {
            // Inside class Matrix<T>, the operands are Matrix<T> itself — a construction of the
            // declaring definition, which still counts as the declaring type.
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Matrix<T> {\n"
                + "  operator+(a: Matrix<T>, b: Matrix<T>): Matrix<T> { return a; }\n"
                + "}"));

            AssertNoErrors(compilation);
        }

        [Fact]
        public void ANullableOperandStillCountsAsTheDeclaringType()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Foo {\n"
                + "  operator==(a: Foo?, b: Foo): bool { return true; }\n"
                + "}"));

            AssertNoErrors(compilation);
        }

        [Fact]
        public void AVirtualOperatorBecomesAnInstanceMethod()
        {
            // §5.6: a dispatch modifier makes the operator an instance method whose receiver is its
            // first parameter — the one spelling that can reach a vtable slot.
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Base {\n"
                + "  virtual operator==(self: Base, other: Base): bool { return true; }\n"
                + "}\n"
                + "class Foo : Base {\n"
                + "  virtual operator+(self: Foo, other: Foo): Foo { return self; }\n"
                + "  override operator==(self: Base, other: Base): bool { return true; }\n"
                + "}"));

            AssertNoErrors(compilation);
        }

        [Fact]
        public void AStaticOperatorWithADispatchModifierIsRejected()
        {
            // `static virtual` is contradictory: instance is what a dispatch modifier *means*.
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Foo {\n"
                + "  static virtual operator+(self: Foo, other: Foo): Foo { return self; }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidModifier);
        }

        [Fact]
        public void AConversionCannotBeInstance()
        {
            // `operator as` names its source as its only parameter; its target lives in the return,
            // and nothing about a conversion ever dispatches — so a dispatch modifier is rejected.
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Foo {\n"
                + "  virtual operator as string(self: Foo) { return \"foo\"; }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidOperatorSignature);
        }

        [Fact]
        public void AnAbstractOperatorCannotHaveABody()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Foo {\n"
                + "  abstract operator+(self: Foo, other: Foo): Foo { return self; }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidOperatorSignature);
        }

        [Fact]
        public void AConcreteInstanceOperatorNeedsABody()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Foo {\n"
                + "  virtual operator+(self: Foo, other: Foo): Foo;\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidOperatorSignature);
        }

        [Fact]
        public void AnInterfaceOperatorIsAbstractAndInstance()
        {
            // An interface operator is a promise, exactly like an interface method: no body, and
            // the runtime reaches it through the interface's method slots.
            Bind(out var compilation, ("game/core/Test.surtr",
                "interface IAddable {\n"
                + "  operator+(self: IAddable, other: IAddable): IAddable;\n"
                + "}\n"
                + "class Vec2 : IAddable {\n"
                + "  override operator+(self: IAddable, other: IAddable): IAddable { return self; }\n"
                + "}"));

            AssertNoErrors(compilation);
        }

        [Fact]
        public void AnInterfaceOperatorCannotHaveABody()
        {
            Bind(out var compilation, ("game/core/Test.surtr",
                "interface IAddable {\n"
                + "  operator+(self: IAddable, other: IAddable): IAddable { return self; }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidInterfaceMember);
        }

        [Fact]
        public void AnInstanceOperatorsReceiverMustBeTheClassOrAnAncestor()
        {
            // The receiver of an instance operator is its first parameter, and it has to be the
            // declaring type — or, for an override implementing a base or interface, that ancestor,
            // since the receiver never enters the method table's signature and an interface slot
            // routes onto a vtable entry without it.
            Bind(out var compilation, ("game/core/Test.surtr",
                "interface IAddable {\n"
                + "  operator+(self: IAddable, other: IAddable): IAddable;\n"
                + "}\n"
                + "class Bar : IAddable {\n"
                + "  override operator+(self: string, other: IAddable): IAddable { return self; }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.InvalidOperatorSignature);
        }

        [Fact]
        public void TwoInstanceOperatorsDifferingOnlyByReceiverAreADuplicate()
        {
            // The receiver is implicit in the method table, so both would land on the same key and
            // a module that compiled clean would fail to load.
            Bind(out var compilation, ("game/core/Test.surtr",
                "class Base { }\n"
                + "class Foo : Base {\n"
                + "  virtual operator+(self: Foo, other: int): Foo { return self; }\n"
                + "  virtual operator+(self: Base, other: int): Foo { return self; }\n"
                + "}"));

            AssertReports(compilation, SurtrDiagnosticCode.DuplicateOverload);
        }
        #endregion

        #region Build constants
        [Fact]
        public void AModuleMemberCannotShadowABuildConstant()
        {
            // §7.4: shadowing a build flag would be invisible at the use site.
            var project = new SurtrProject(Root);
            project.Define("Debug", BuildConstant.Bool(true));
            project.AddSourceFile(Root + "/game/Test.surtr", "public var Debug: bool = false;");

            var compilation = SurtrCompilation.Create(project);
            compilation.Bind();

            AssertReports(compilation, SurtrDiagnosticCode.BuildConstantShadowed);
        }

        [Fact]
        public void AMemberInsideATypeMayShareTheNameOfABuildConstant()
        {
            // The rule is about module members: a field on a class is never reached unqualified.
            var project = new SurtrProject(Root);
            project.Define("Debug", BuildConstant.Bool(true));
            project.AddSourceFile(Root + "/game/Test.surtr", "class Options { public var Debug: bool = false; }");

            var compilation = SurtrCompilation.Create(project);
            compilation.Bind();

            AssertNoErrors(compilation);
        }
        #endregion

        #region Inherited members through a construction
        [Fact]
        public void AnInheritedMemberIsReadThroughTheReceiversConstruction()
        {
            // The stdlib Collection.surtr shape: `iterate()` is not declared on IReadOnlyCollection<T>
            // itself, so reaching it walks the inherited IIterable<T> — and that walk must apply the
            // receiver's own type argument, not leak the interface's parameter.
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "interface IReadOnlyCollection<T> : IIterable<T>\n"
                + "{\n"
                + "    fun get(index: int): T;\n"
                + "}\n"
                + "private value class ReadOnlyCollection<T> : IReadOnlyCollection<T>\n"
                + "{\n"
                + "    private let _col: IReadOnlyCollection<T>;\n"
                + "    public inline constructor(collection: IReadOnlyCollection<T>) { this._col = collection; }\n"
                + "    public override fun get(index: int): T { return _col.get(index); }\n"
                + "    public override fun iterate(): IIterator<T> { return _col.iterate(); }\n"
                + "}"));

            binder.BindBodies();

            AssertNoErrors(compilation);
        }
        #endregion

        #region Override signature compatibility
        [Fact]
        public void AMemberImplementingTheWrongConstructionIsRejected()
        {
            // The runtime cannot see this: it matches by name plus erased parameter types and
            // excludes the return, so `ReadOnlyCollection<T>` implementing `IReadOnlyCollection<int>`
            // with members typed on its own `T` links cleanly and misbehaves at a call site compiled
            // against the contract. The compiler has to reject it.
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "interface IReadOnlyCollection<T> : IIterable<T>\n"
                + "{\n"
                + "    fun get(index: int): T;\n"
                + "}\n"
                + "private value class ReadOnlyCollection<T> : IReadOnlyCollection<int>\n"
                + "{\n"
                + "    private let _col: IReadOnlyCollection<T>;\n"
                + "    public inline constructor(collection: IReadOnlyCollection<T>) { this._col = collection; }\n"
                + "    public override fun get(index: int): T { return _col.get(index); }\n"
                + "    public override fun iterate(): IIterator<T> { return _col.iterate(); }\n"
                + "}"));

            binder.BindBodies();

            AssertReports(compilation, SurtrDiagnosticCode.OverrideSignatureMismatch);
        }

        [Fact]
        public void TheSubstitutionSurvivesAnInterfaceChain()
        {
            // `ICollection<int>` extends `IReadOnlyCollection<T>` in terms of *its own* parameter,
            // so the `int` must follow the walk into the inherited contract or the members reached
            // through it would be checked against `IReadOnlyCollection<ICollection.T>` instead.
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "interface IReadOnlyCollection<T> : IIterable<T>\n"
                + "{\n"
                + "    fun get(index: int): T;\n"
                + "}\n"
                + "interface ICollection<T> : IReadOnlyCollection<T>\n"
                + "{\n"
                + "    fun add(item: T): void;\n"
                + "}\n"
                + "private value class ReadOnlyCollection<T> : ICollection<int>\n"
                + "{\n"
                + "    private let _col: IReadOnlyCollection<T>;\n"
                + "    public inline constructor(collection: IReadOnlyCollection<T>) { this._col = collection; }\n"
                + "    public override fun get(index: int): T { return _col.get(index); }\n"
                + "    public override fun add(item: int): void { }\n"
                + "    public override fun iterate(): IIterator<T> { return _col.iterate(); }\n"
                + "}"));

            binder.BindBodies();

            AssertReports(compilation, SurtrDiagnosticCode.OverrideSignatureMismatch);
        }

        [Fact]
        public void AMemberImplementingTheMatchingConstructionIsAccepted()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "interface IReadOnlyCollection<T> : IIterable<T>\n"
                + "{\n"
                + "    fun get(index: int): T;\n"
                + "}\n"
                + "private value class ReadOnlyCollection<T> : IReadOnlyCollection<T>\n"
                + "{\n"
                + "    private let _col: IReadOnlyCollection<T>;\n"
                + "    public inline constructor(collection: IReadOnlyCollection<T>) { this._col = collection; }\n"
                + "    public override fun get(index: int): T { return _col.get(index); }\n"
                + "    public override fun iterate(): IIterator<T> { return _col.iterate(); }\n"
                + "}"));

            binder.BindBodies();

            AssertNoErrors(compilation);
        }
        #endregion

        #region Built-in generic interfaces (regression net for the substitution fixes in 5cca11a/0bef8a2)
        // These are deliberately independent of the "Inherited members through a construction" and
        // "Override signature compatibility" regions above: those cover a *chain* of user-declared
        // interfaces walking into a built-in one, and a *generic* class implementing a built-in
        // generic interface in terms of its own parameter. The scenarios below are the simpler ones
        // reported as broken — implementing, extending or using a built-in generic interface with no
        // chain and no substitution involved at all — which the investigation could not reproduce on
        // this branch, but which had no test of their own naming them explicitly.

        [Fact]
        public void AClassMayImplementABuiltInGenericInterfaceWithAConcreteArgumentDirectly()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Counter : IIterable<int>\n"
                + "{\n"
                + "    public override fun iterate(): IIterator<int> { return [1, 2, 3].iterate(); }\n"
                + "}"));

            binder.BindBodies();

            AssertNoErrors(compilation);
        }

        [Fact]
        public void AnInterfaceMayExtendABuiltInGenericInterfaceWithoutAddingMembersOfItsOwn()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "interface INumbers : IIterable<int> { }"));

            binder.BindBodies();

            AssertNoErrors(compilation);
        }

        [Fact]
        public void AClassImplementingAPassThroughInterfaceMustStillProvideTheInheritedBuiltInMember()
        {
            // INumbers itself declares nothing - the obligation it owes to Counter comes entirely
            // from the built-in IIterable<int> it extends, so this fails only if that walk sees it.
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "interface INumbers : IIterable<int> { }\n"
                + "class Counter : INumbers { }"));

            binder.BindBodies();

            AssertReports(compilation, SurtrDiagnosticCode.MissingImplementation);
        }

        [Fact]
        public void AClassImplementingAPassThroughInterfaceSatisfiesItByImplementingTheInheritedBuiltInMember()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "interface INumbers : IIterable<int> { }\n"
                + "class Counter : INumbers\n"
                + "{\n"
                + "    public override fun iterate(): IIterator<int> { return [1, 2, 3].iterate(); }\n"
                + "}"));

            binder.BindBodies();

            AssertNoErrors(compilation);
        }
        #endregion

        #region Per-accessor modifiers (§3.2, §3.4)
        [Fact]
        public void AnAccessorMayNarrowItsOwnVisibilityBelowTheProperty()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Box\n"
                + "{\n"
                + "    private var _value: int;\n"
                + "    public value: int { get => _value; private set { _value = value; } }\n"
                + "}"));

            binder.BindBodies();

            AssertNoErrors(compilation);

            var box = Type(binder, "game.core", "Box");
            var property = box.Members.OfType<PropertySymbol>().Single(p => p.Name == "value");
            Assert.Equal(Accessibility.Public, property.Getter!.Accessibility);
            Assert.Equal(Accessibility.Private, property.Setter!.Accessibility);
        }

        [Fact]
        public void AnAccessorWiderThanThePropertyIsRejected()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Box\n"
                + "{\n"
                + "    private var _value: int;\n"
                + "    private value: int { get => _value; public set { _value = value; } }\n"
                + "}"));

            binder.BindBodies();

            AssertReports(compilation, SurtrDiagnosticCode.AccessorVisibilityNotNarrower);
        }

        [Fact]
        public void AnAccessorRepeatingThePropertysOwnVisibilityIsRejected()
        {
            // Equal is not narrower - the accessor could have written nothing and inherited it.
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Box\n"
                + "{\n"
                + "    private var _value: int;\n"
                + "    public value: int { get => _value; public set { _value = value; } }\n"
                + "}"));

            binder.BindBodies();

            AssertReports(compilation, SurtrDiagnosticCode.AccessorVisibilityNotNarrower);
        }

        [Fact]
        public void AWriteThroughANarrowerSetterIsRejectedFromOutsideItsReach()
        {
            var binder = Bind(out var compilation, (
                "game/core/Box.surtr",
                "public class Box\n"
                + "{\n"
                + "    private var _value: int;\n"
                + "    public value: int { get => _value; private set { _value = value; } }\n"
                + "}"),
                ("game/core/Other.surtr",
                "class Other { public fun run(): void { let b = Box(); b.value = 1; } }"));

            binder.BindBodies();

            AssertReports(compilation, SurtrDiagnosticCode.Inaccessible);
        }

        [Fact]
        public void AnAccessorMayDeclareItsOwnDispatchIndependentlyOfTheOtherAccessor()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Box\n"
                + "{\n"
                + "    private var _value: int;\n"
                + "    public value: int { virtual get => _value; set { _value = value; } }\n"
                + "}"));

            binder.BindBodies();

            AssertNoErrors(compilation);

            var box = Type(binder, "game.core", "Box");
            var property = box.Members.OfType<PropertySymbol>().Single(p => p.Name == "value");
            Assert.Equal(MethodDispatch.Virtual, property.Getter!.Dispatch);
            Assert.Equal(MethodDispatch.Direct, property.Setter!.Dispatch);
        }

        [Fact]
        public void AnAccessorMayBeAbstractWhileItsSiblingIsConcrete()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "abstract class Shape\n"
                + "{\n"
                + "    public value: int { abstract get; set { } }\n"
                + "}"));

            binder.BindBodies();

            AssertNoErrors(compilation);

            var shape = Type(binder, "game.core", "Shape");
            var property = shape.Members.OfType<PropertySymbol>().Single(p => p.Name == "value");
            Assert.Equal(MethodDispatch.Abstract, property.Getter!.Dispatch);
            Assert.Equal(MethodDispatch.Direct, property.Setter!.Dispatch);
        }

        [Fact]
        public void AConcreteClassMaySatisfyAnAbstractAccessorDeclaredOnAnAbstractBase()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "abstract class Shape\n"
                + "{\n"
                + "    public value: int { abstract get; set { } }\n"
                + "}\n"
                + "class Square : Shape\n"
                + "{\n"
                + "    public override value: int { get => 4; }\n"
                + "}"));

            binder.BindBodies();

            AssertNoErrors(compilation);
        }

        [Fact]
        public void ALeftoverAbstractAccessorIsReportedOnAConcreteSubclass()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "abstract class Shape\n"
                + "{\n"
                + "    public value: int { abstract get; set { } }\n"
                + "}\n"
                + "class Square : Shape { }"));

            binder.BindBodies();

            AssertReports(compilation, SurtrDiagnosticCode.MissingImplementation);
        }

        [Fact]
        public void APropertyLevelSealedOverrideActuallySealsItsAccessors()
        {
            // Regression: WireAccessors used to drop `sealed` on the floor entirely - a property's
            // `sealed override` looked accepted but never reached the accessor MethodSymbols, so
            // nothing downstream ever rejected a further override. This is the same shape
            // CheckSealedOverrides already tests for an ordinary method.
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Animal { public virtual name: string { get => \"Animal\"; } }\n"
                + "class Dog : Animal { public sealed override name: string { get => \"Dog\"; } }\n"
                + "class Puppy : Dog { public override name: string { get => \"Puppy\"; } }"));

            binder.BindBodies();

            AssertReports(compilation, SurtrDiagnosticCode.InvalidBaseType);
        }

        [Fact]
        public void AnAccessorMaySealItsOwnOverrideIndependentlyOfTheProperty()
        {
            var binder = Bind(out var compilation, ("game/core/Test.surtr",
                "class Animal { public virtual name: string { get => \"Animal\"; } }\n"
                + "class Dog : Animal { public name: string { sealed override get => \"Dog\"; } }\n"
                + "class Puppy : Dog { public override name: string { get => \"Puppy\"; } }"));

            binder.BindBodies();

            AssertReports(compilation, SurtrDiagnosticCode.InvalidBaseType);
        }
        #endregion
    }
}
