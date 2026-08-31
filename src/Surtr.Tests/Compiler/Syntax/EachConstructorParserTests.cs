#nullable enable

using System.Linq;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;
using Xunit;

namespace Surtr.Tests.Compiler.Syntax
{
    /// <summary>
    /// Parses §5.x's syntax: the <c>each</c> clause on a constructor, the <c>default</c> clause on
    /// an interface, and the <c>[ ... ]</c>/<c>{ ... }</c> collection literals written over a type.
    /// </summary>
    public sealed class EachConstructorParserTests
    {
        private static CompilationUnitSyntax ParseWithoutErrors(string source)
        {
            Parser parser = new Parser(SurtrSourceBuffer.FromString(source));
            CompilationUnitSyntax unit = parser.ParseCompilationUnit();
            Assert.Empty(parser.Diagnostics);
            return unit;
        }

        private static ExpressionSyntax ParseExpression(string expression)
        {
            MethodDeclarationSyntax method = Assert.IsType<MethodDeclarationSyntax>(
                ParseWithoutErrors($"fun f(): void {{ let x = {expression}; }}").Declarations.Single());
            LocalDeclarationStatementSyntax local = Assert.IsType<LocalDeclarationStatementSyntax>(method.Body!.Statements.Single());
            return local.Initializer!;
        }

        private static ConstructorDeclarationSyntax ParseConstructor(string source)
            => Assert.IsType<ConstructorDeclarationSyntax>(
                Assert.IsType<TypeDeclarationSyntax>(
                    ParseWithoutErrors(source).Declarations.Single()).Members.Single());

        [Fact]
        public void AConstructorWithAnEachClauseParses()
        {
            var constructor = ParseConstructor(@"
class Bag<T>
{
    public constructor()
    {
    }
    each (item: T)
    {
    }
}");
            Assert.NotNull(constructor.EachParameters);
            Assert.NotNull(constructor.EachBody);
            Assert.Single(constructor.EachParameters!);
            Assert.Equal("item", constructor.EachParameters![0].Name);
        }

        [Fact]
        public void AConstructorWithATwoParameterEachClauseParses()
        {
            var constructor = ParseConstructor(@"
class Bag<K, V>
{
    public constructor()
    {
    }
    each (key: K, value: V)
    {
    }
}");
            Assert.Equal(2, constructor.EachParameters!.Count);
        }

        [Fact]
        public void AConstructorWithoutAnEachClauseParsesWithNone()
        {
            var constructor = ParseConstructor(@"
class Bag<T>
{
    public constructor()
    {
    }
}");
            Assert.Null(constructor.EachParameters);
            Assert.Null(constructor.EachBody);
        }

        [Fact]
        public void AnInterfaceWithADefaultParses()
        {
            var type = Assert.IsType<TypeDeclarationSyntax>(
                ParseWithoutErrors("interface IBag<T> default Bag<T> : IIterable<T> { }").Declarations.Single());
            Assert.NotNull(type.DefaultBuilder);
        }

        [Fact]
        public void AnInterfaceWithoutADefaultParsesWithNone()
        {
            var type = Assert.IsType<TypeDeclarationSyntax>(
                ParseWithoutErrors("interface IBag<T> : IIterable<T> { }").Declarations.Single());
            Assert.Null(type.DefaultBuilder);
        }

        [Fact]
        public void AGenericCollectionLiteralParses()
        {
            var expression = ParseExpression("Bag<int>[1, 2, 3]");
            var instantiation = Assert.IsType<CollectionInstantiationExpressionSyntax>(expression);
            Assert.IsType<GenericNameExpressionSyntax>(instantiation.Construction);
            Assert.IsType<ArrayLiteralExpressionSyntax>(instantiation.Body);
        }

        [Fact]
        public void ACollectionLiteralWithConstructorArgumentsParses()
        {
            var expression = ParseExpression("Bag<int>(8)[1, 2, 3]");
            var instantiation = Assert.IsType<CollectionInstantiationExpressionSyntax>(expression);
            Assert.IsType<CallExpressionSyntax>(instantiation.Construction);
        }

        [Fact]
        public void ABareIdentifierCollectionLiteralParses()
        {
            var expression = ParseExpression("IntList[1, 2, 3]");
            var instantiation = Assert.IsType<CollectionInstantiationExpressionSyntax>(expression);
            Assert.IsType<IdentifierExpressionSyntax>(instantiation.Construction);
        }

        [Fact]
        public void ADictCollectionLiteralParses()
        {
            var expression = ParseExpression("Map<string, int>{ \"x\": 10 }");
            var instantiation = Assert.IsType<CollectionInstantiationExpressionSyntax>(expression);
            Assert.IsType<DictLiteralExpressionSyntax>(instantiation.Body);
        }

        [Fact]
        public void AnOrdinaryArrayIndexStillParsesAsTheAmbiguousShape()
        {
            // `arr[0]` over an identifier parses as the collection-instantiation node; the binder
            // re-reads it as an index when the identifier is a value.
            var expression = ParseExpression("arr[0]");
            var instantiation = Assert.IsType<CollectionInstantiationExpressionSyntax>(expression);
            Assert.IsType<IdentifierExpressionSyntax>(instantiation.Construction);
        }

        [Fact]
        public void AGenericTypeArrayIndexStillParsesAsTheAmbiguousShape()
        {
            // `getList<int>(5)[0]` over a call — the binder re-reads it as an index.
            var expression = ParseExpression("getList<int>(5)[0]");
            Assert.IsType<CollectionInstantiationExpressionSyntax>(expression);
        }
    }
}