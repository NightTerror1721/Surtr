#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;

namespace Surtr.Tests.Compiler.Syntax
{
    /// <summary>
    /// Runs <c>Sample.surtr</c> — a source file exercising every construct in
    /// <c>docs/Language-Syntax.md</c> — through the lexer and then the parser, from imports and
    /// aliases to operator overloads, compile-time evaluation and the whole operator set.
    /// </summary>
    /// <remarks>
    /// The per-feature tests in <see cref="LexerTests"/> prove each token in isolation. This one
    /// proves they still work when they sit next to each other, which is where a maximal-munch
    /// mistake actually surfaces - a wrongly ordered arm usually still passes its own unit test and
    /// only misbehaves next to a neighbour that shares its prefix.
    /// </remarks>
    public sealed class LexerSpecCoverageTests
    {
        private static SurtrSourceBuffer LoadSample()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Compiler", "Syntax", "Sample.surtr");
            return SurtrSourceBuffer.FromFile(path);
        }

        [Fact]
        public void FullSpecSampleLexesCleanly()
        {
            List<Token> tokens = new Lexer(LoadSample()).Tokenize();

            Assert.DoesNotContain(tokens, token => token.Type == TokenType.Invalid);
            Assert.Equal(TokenType.EndOfFile, tokens[^1].Type);
            Assert.True(tokens.Count > 400, $"expected a substantial token stream, got {tokens.Count}");
        }

        /// <summary>
        /// Every token that only one construct in the language can produce. If the sample stops
        /// covering one of these, this fails rather than quietly losing the coverage.
        /// </summary>
        [Theory]
        [InlineData(TokenType.KeywordImport)]
        [InlineData(TokenType.KeywordAlias)]
        [InlineData(TokenType.KeywordConst)]
        [InlineData(TokenType.KeywordSingleton)]
        [InlineData(TokenType.KeywordEnum)]
        [InlineData(TokenType.KeywordInterface)]
        [InlineData(TokenType.KeywordSealed)]
        [InlineData(TokenType.KeywordAbstract)]
        [InlineData(TokenType.KeywordVirtual)]
        [InlineData(TokenType.KeywordOverride)]
        [InlineData(TokenType.KeywordNative)]
        [InlineData(TokenType.KeywordOperator)]
        [InlineData(TokenType.KeywordInline)]
        [InlineData(TokenType.KeywordIs)]
        [InlineData(TokenType.KeywordAs)]
        [InlineData(TokenType.KeywordConstructor)]
        [InlineData(TokenType.KeywordSwitch)]
        [InlineData(TokenType.KeywordTry)]
        [InlineData(TokenType.KeywordFinally)]
        [InlineData(TokenType.KeywordThrow)]
        [InlineData(TokenType.DocComment)]
        [InlineData(TokenType.At)]
        [InlineData(TokenType.Ellipsis)]
        [InlineData(TokenType.Spaceship)]
        [InlineData(TokenType.UnsignedShiftRight)]
        [InlineData(TokenType.UnsignedShiftRightAssign)]
        [InlineData(TokenType.NullCoalesce)]
        [InlineData(TokenType.NullCoalesceAssign)]
        [InlineData(TokenType.QuestionDot)]
        [InlineData(TokenType.BangBang)]
        [InlineData(TokenType.Arrow)]
        [InlineData(TokenType.FatArrow)]
        [InlineData(TokenType.DotDot)]
        [InlineData(TokenType.DotDotEquals)]
        [InlineData(TokenType.InterpolatedStringLiteral)]
        [InlineData(TokenType.StringLiteral)]
        [InlineData(TokenType.CharacterLiteral)]
        [InlineData(TokenType.IntegerLiteral)]
        [InlineData(TokenType.FloatLiteral)]
        public void SampleCoversToken(TokenType expected)
        {
            List<Token> tokens = new Lexer(LoadSample()).Tokenize();

            Assert.Contains(tokens, token => token.Type == expected);
        }

        /// <summary>
        /// Both right shifts appear in the sample, and they must stay distinct: `&gt;&gt;` is the
        /// arithmetic one (VM opcode <c>Sar</c>) and `&gt;&gt;&gt;` the logical one (<c>Shr</c>).
        /// Collapsing them would silently change what the emitted code does to negative values.
        /// </summary>
        [Fact]
        public void BothRightShiftsSurviveInTheSameExpression()
        {
            List<Token> tokens = new Lexer(LoadSample()).Tokenize();

            Assert.Contains(tokens, token => token.Type == TokenType.ShiftRight);
            Assert.Contains(tokens, token => token.Type == TokenType.UnsignedShiftRight);
        }

        /// <summary>
        /// The same sample, all the way through the parser. Unit tests exercise each production in
        /// isolation; this is the one that catches a construct working only when nothing else is
        /// around it.
        /// </summary>
        [Fact]
        public void FullSpecSampleParsesCleanly()
        {
            CompilationUnitSyntax unit = new Parser(LoadSample()).ParseCompilationUnit();

            Assert.Equal(2, unit.Imports.Count);
            Assert.NotEmpty(unit.Declarations);
        }

        /// <summary>Every kind of top-level declaration in the language appears in the sample and survives parsing.</summary>
        [Fact]
        public void SampleProducesEveryTopLevelDeclarationKind()
        {
            CompilationUnitSyntax unit = new Parser(LoadSample()).ParseCompilationUnit();

            Assert.Contains(unit.Declarations, d => d is AliasDeclarationSyntax);
            Assert.Contains(unit.Declarations, d => d is FieldDeclarationSyntax);
            Assert.Contains(unit.Declarations, d => d is MethodDeclarationSyntax);

            TypeDeclarationKind[] kinds = unit.Declarations
                .OfType<TypeDeclarationSyntax>()
                .Select(declaration => declaration.Kind)
                .Distinct()
                .ToArray();

            Assert.Contains(TypeDeclarationKind.Class, kinds);
            Assert.Contains(TypeDeclarationKind.ValueClass, kinds);
            Assert.Contains(TypeDeclarationKind.Interface, kinds);
            Assert.Contains(TypeDeclarationKind.Enum, kinds);
            Assert.Contains(TypeDeclarationKind.Singleton, kinds);
        }
    }
}
