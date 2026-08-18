#nullable enable

using System.Collections.Generic;
using System.Linq;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax;

namespace Surtr.Tests.Compiler.Syntax
{
    /// <summary>
    /// Covers the lexer against docs/Language-Syntax.md - §1.2 for the reserved words, §5.7 for the
    /// operator set, §5.8 for literals. The cases that matter most are the ones where a token
    /// boundary is ambiguous without lookahead or without maximal munch, since those are the ones
    /// a plausible-looking implementation gets wrong.
    /// </summary>
    public sealed class LexerTests
    {
        private static List<Token> Lex(string source)
        {
            return new Lexer(SurtrSourceBuffer.FromString(source)).Tokenize();
        }

        private static TokenType[] Types(string source)
        {
            return Lex(source).Select(token => token.Type).ToArray();
        }

        [Fact]
        public void EmptySourceProducesOnlyEndOfFile()
        {
            Assert.Equal(new[] { TokenType.EndOfFile }, Types(""));
        }

        [Fact]
        public void TokenStreamAlwaysEndsWithEndOfFile()
        {
            Assert.Equal(TokenType.EndOfFile, Lex("let x = 1;")[^1].Type);
        }

        // ---- §1.2 reserved words ----------------------------------------------------------

        [Theory]
        [InlineData("abstract", TokenType.KeywordAbstract)]
        [InlineData("alias", TokenType.KeywordAlias)]
        [InlineData("as", TokenType.KeywordAs)]
        [InlineData("break", TokenType.KeywordBreak)]
        [InlineData("case", TokenType.KeywordCase)]
        [InlineData("catch", TokenType.KeywordCatch)]
        [InlineData("class", TokenType.KeywordClass)]
        [InlineData("const", TokenType.KeywordConst)]
        [InlineData("constructor", TokenType.KeywordConstructor)]
        [InlineData("continue", TokenType.KeywordContinue)]
        [InlineData("default", TokenType.KeywordDefault)]
        [InlineData("else", TokenType.KeywordElse)]
        [InlineData("enum", TokenType.KeywordEnum)]
        [InlineData("false", TokenType.KeywordFalse)]
        [InlineData("finally", TokenType.KeywordFinally)]
        [InlineData("for", TokenType.KeywordFor)]
        [InlineData("forceinline", TokenType.KeywordForceInline)]
        [InlineData("fun", TokenType.KeywordFun)]
        [InlineData("if", TokenType.KeywordIf)]
        [InlineData("import", TokenType.KeywordImport)]
        [InlineData("in", TokenType.KeywordIn)]
        [InlineData("inline", TokenType.KeywordInline)]
        [InlineData("interface", TokenType.KeywordInterface)]
        [InlineData("internal", TokenType.KeywordInternal)]
        [InlineData("is", TokenType.KeywordIs)]
        [InlineData("let", TokenType.KeywordLet)]
        [InlineData("moduleof", TokenType.KeywordModuleOf)]
        [InlineData("native", TokenType.KeywordNative)]
        [InlineData("null", TokenType.KeywordNull)]
        [InlineData("operator", TokenType.KeywordOperator)]
        [InlineData("override", TokenType.KeywordOverride)]
        [InlineData("private", TokenType.KeywordPrivate)]
        [InlineData("protected", TokenType.KeywordProtected)]
        [InlineData("public", TokenType.KeywordPublic)]
        [InlineData("return", TokenType.KeywordReturn)]
        [InlineData("sealed", TokenType.KeywordSealed)]
        [InlineData("singleton", TokenType.KeywordSingleton)]
        [InlineData("static", TokenType.KeywordStatic)]
        [InlineData("switch", TokenType.KeywordSwitch)]
        [InlineData("throw", TokenType.KeywordThrow)]
        [InlineData("true", TokenType.KeywordTrue)]
        [InlineData("try", TokenType.KeywordTry)]
        [InlineData("typeof", TokenType.KeywordTypeOf)]
        [InlineData("var", TokenType.KeywordVar)]
        [InlineData("virtual", TokenType.KeywordVirtual)]
        [InlineData("while", TokenType.KeywordWhile)]
        public void ReservedWordsLexAsKeywords(string source, TokenType expected)
        {
            Assert.Equal(new[] { expected, TokenType.EndOfFile }, Types(source));
        }

        /// <summary>§1.1: type names are ordinary identifiers, not keywords, so a nested type can shadow one.</summary>
        [Theory]
        [InlineData("int")]
        [InlineData("float")]
        [InlineData("bool")]
        [InlineData("char")]
        [InlineData("string")]
        [InlineData("void")]
        [InlineData("range")]
        [InlineData("unknown")]
        public void TypeNamesLexAsIdentifiers(string source)
        {
            Assert.Equal(new[] { TokenType.Identifier, TokenType.EndOfFile }, Types(source));
        }

        /// <summary>§3.2: the contextual keywords stay ordinary identifiers; recognising them is the parser's job.</summary>
        [Theory]
        [InlineData("this")]
        [InlineData("super")]
        [InlineData("value")]
        public void ContextualKeywordsLexAsIdentifiers(string source)
        {
            Assert.Equal(new[] { TokenType.Identifier, TokenType.EndOfFile }, Types(source));
        }

        [Theory]
        [InlineData("classy")]
        [InlineData("_let")]
        [InlineData("iff")]
        [InlineData("Class")]
        [InlineData("constant")]
        [InlineData("inlined")]
        [InlineData("aliased")]
        public void WordsMerelyContainingAKeywordAreIdentifiers(string source)
        {
            Assert.Equal(new[] { TokenType.Identifier, TokenType.EndOfFile }, Types(source));
        }

        /// <summary>
        /// `const`, `constructor` and `constant` share a prefix, and `inline` sits inside
        /// `forceinline`. Length-bucketed keyword lookup gets these right, but a naive
        /// longest-prefix matcher would not.
        /// </summary>
        [Fact]
        public void KeywordsSharingAPrefixStayDistinct()
        {
            Assert.Equal(
                new[]
                {
                    TokenType.KeywordConst, TokenType.KeywordConstructor, TokenType.Identifier,
                    TokenType.KeywordInline, TokenType.KeywordForceInline, TokenType.Identifier,
                    TokenType.EndOfFile,
                },
                Types("const constructor constant inline forceinline inlined"));
        }

        // ---- §2.7 aliases, §3.6 inlining, §7 compile-time evaluation ------------------------

        [Fact]
        public void GenericTypeAliasLexes()
        {
            Assert.Equal(
                new[]
                {
                    TokenType.KeywordAlias, TokenType.Identifier, TokenType.Less, TokenType.Identifier,
                    TokenType.Greater, TokenType.Assign, TokenType.LeftBrace, TokenType.Identifier,
                    TokenType.Colon, TokenType.Identifier, TokenType.RightBrace, TokenType.Semicolon,
                    TokenType.EndOfFile,
                },
                Types("alias IntMap<V> = {int: V};"));
        }

        [Fact]
        public void ClosureTypeAliasKeepsArrowDistinctFromFatArrow()
        {
            List<Token> tokens = Lex("alias Handler = (Entity, float) -> void;");

            Assert.Contains(tokens, token => token.Type == TokenType.Arrow);
            Assert.DoesNotContain(tokens, token => token.Type == TokenType.FatArrow);
        }

        /// <summary>§7.3: `const if` is two tokens, not a compound one - the parser pairs them.</summary>
        [Fact]
        public void ConstIfLexesAsTwoKeywords()
        {
            Assert.Equal(
                new[]
                {
                    TokenType.KeywordConst, TokenType.KeywordIf, TokenType.LeftParen, TokenType.Identifier,
                    TokenType.RightParen, TokenType.LeftBrace, TokenType.RightBrace, TokenType.KeywordElse,
                    TokenType.KeywordConst, TokenType.KeywordIf, TokenType.LeftParen, TokenType.Identifier,
                    TokenType.RightParen, TokenType.LeftBrace, TokenType.RightBrace, TokenType.EndOfFile,
                },
                Types("const if (Debug) { } else const if (Verbose) { }"));
        }

        [Fact]
        public void ConstFunAndInlineModifiersLexInDeclarationOrder()
        {
            Assert.Equal(
                new[]
                {
                    TokenType.KeywordPublic, TokenType.KeywordForceInline, TokenType.KeywordConst,
                    TokenType.KeywordFun, TokenType.Identifier, TokenType.LeftParen, TokenType.Identifier,
                    TokenType.Colon, TokenType.Identifier, TokenType.RightParen, TokenType.Colon,
                    TokenType.Identifier, TokenType.LeftBrace, TokenType.RightBrace, TokenType.EndOfFile,
                },
                Types("public forceinline const fun square(x: int): int { }"));
        }

        /// <summary>
        /// §5.6: an overload is introduced by `operator` alone - no `public`, no `static`, no
        /// `fun`, since none of them could be anything else.
        /// </summary>
        [Fact]
        public void OperatorOverloadNeedsNoOtherModifier()
        {
            Assert.Equal(
                new[]
                {
                    TokenType.KeywordOperator, TokenType.Plus, TokenType.LeftParen, TokenType.Identifier,
                    TokenType.Colon, TokenType.Identifier, TokenType.Comma, TokenType.Identifier,
                    TokenType.Colon, TokenType.Identifier, TokenType.RightParen, TokenType.Colon,
                    TokenType.Identifier, TokenType.LeftBrace, TokenType.RightBrace, TokenType.EndOfFile,
                },
                Types("operator+(a: Vec2, b: Vec2): Vec2 { }"));
        }

        /// <summary>Every overloadable token has to survive next to `operator`, including the ones that are themselves multi-character.</summary>
        [Theory]
        [InlineData("operator+", TokenType.Plus)]
        [InlineData("operator-", TokenType.Minus)]
        [InlineData("operator*", TokenType.Star)]
        [InlineData("operator/", TokenType.Slash)]
        [InlineData("operator%", TokenType.Percent)]
        [InlineData("operator&", TokenType.Ampersand)]
        [InlineData("operator|", TokenType.Pipe)]
        [InlineData("operator^", TokenType.Caret)]
        [InlineData("operator~", TokenType.Tilde)]
        [InlineData("operator!", TokenType.LogicalNot)]
        [InlineData("operator<<", TokenType.ShiftLeft)]
        [InlineData("operator>>", TokenType.ShiftRight)]
        [InlineData("operator>>>", TokenType.UnsignedShiftRight)]
        [InlineData("operator++", TokenType.Increment)]
        [InlineData("operator--", TokenType.Decrement)]
        [InlineData("operator==", TokenType.Equal)]
        [InlineData("operator<=>", TokenType.Spaceship)]
        [InlineData("operator as", TokenType.KeywordAs)]
        public void EveryOverloadableOperatorLexesAfterTheKeyword(string source, TokenType expected)
        {
            Assert.Equal(new[] { TokenType.KeywordOperator, expected, TokenType.EndOfFile }, Types(source));
        }

        [Fact]
        public void IndexerOverloadLexes()
        {
            Assert.Equal(
                new[]
                {
                    TokenType.KeywordOperator, TokenType.LeftBracket, TokenType.RightBracket,
                    TokenType.LeftParen, TokenType.Identifier, TokenType.Colon, TokenType.Identifier,
                    TokenType.RightParen, TokenType.Colon, TokenType.Identifier, TokenType.LeftBrace,
                    TokenType.RightBrace, TokenType.EndOfFile,
                },
                Types("operator[](i: int): float { }"));
        }

        /// <summary>§2.9: `value` stays a contextual keyword - it is the `class` after it that makes the declaration.</summary>
        [Fact]
        public void ValueClassLeavesValueAnIdentifier()
        {
            Assert.Equal(
                new[]
                {
                    TokenType.Identifier, TokenType.KeywordClass, TokenType.Identifier,
                    TokenType.LeftBrace, TokenType.RightBrace, TokenType.EndOfFile,
                },
                Types("value class EntityId { }"));
        }

        /// <summary>§3.3: `sealed` reused on a member, immediately before `override`.</summary>
        [Fact]
        public void SealedOverrideLexes()
        {
            Assert.Equal(
                new[]
                {
                    TokenType.KeywordPublic, TokenType.KeywordSealed, TokenType.KeywordOverride,
                    TokenType.KeywordFun, TokenType.Identifier, TokenType.LeftParen,
                    TokenType.RightParen, TokenType.Colon, TokenType.Identifier,
                    TokenType.LeftBrace, TokenType.RightBrace, TokenType.EndOfFile,
                },
                Types("public sealed override fun speak(): string { }"));
        }

        [Fact]
        public void SingletonDeclarationLexes()
        {
            Assert.Equal(
                new[]
                {
                    TokenType.KeywordSingleton, TokenType.Identifier, TokenType.Colon,
                    TokenType.Identifier, TokenType.LeftBrace, TokenType.RightBrace, TokenType.EndOfFile,
                },
                Types("singleton Registry : IRegistry { }"));
        }

        [Fact]
        public void ConstBindingLexes()
        {
            List<Token> tokens = Lex("const MaxEntities: int = 512;");

            Assert.Equal(TokenType.KeywordConst, tokens[0].Type);
            Assert.Equal(TokenType.Identifier, tokens[1].Type);
            Assert.Equal(512L, tokens[5].Payload.AsInteger);
        }

        // ---- §5.7 operators, and maximal munch --------------------------------------------

        [Theory]
        [InlineData("+", TokenType.Plus)]
        [InlineData("-", TokenType.Minus)]
        [InlineData("*", TokenType.Star)]
        [InlineData("/", TokenType.Slash)]
        [InlineData("%", TokenType.Percent)]
        [InlineData("=", TokenType.Assign)]
        [InlineData("+=", TokenType.PlusAssign)]
        [InlineData("-=", TokenType.MinusAssign)]
        [InlineData("*=", TokenType.StarAssign)]
        [InlineData("/=", TokenType.SlashAssign)]
        [InlineData("%=", TokenType.PercentAssign)]
        [InlineData("&=", TokenType.AmpersandAssign)]
        [InlineData("|=", TokenType.PipeAssign)]
        [InlineData("^=", TokenType.CaretAssign)]
        [InlineData("<<=", TokenType.ShiftLeftAssign)]
        [InlineData(">>=", TokenType.ShiftRightAssign)]
        [InlineData("??=", TokenType.NullCoalesceAssign)]
        [InlineData("==", TokenType.Equal)]
        [InlineData("!=", TokenType.NotEqual)]
        [InlineData("===", TokenType.ReferenceEqual)]
        [InlineData("!==", TokenType.ReferenceNotEqual)]
        [InlineData("<", TokenType.Less)]
        [InlineData(">", TokenType.Greater)]
        [InlineData("<=", TokenType.LessEqual)]
        [InlineData(">=", TokenType.GreaterEqual)]
        [InlineData("<=>", TokenType.Spaceship)]
        [InlineData("&&", TokenType.LogicalAnd)]
        [InlineData("||", TokenType.LogicalOr)]
        [InlineData("!", TokenType.LogicalNot)]
        [InlineData("&", TokenType.Ampersand)]
        [InlineData("|", TokenType.Pipe)]
        [InlineData("^", TokenType.Caret)]
        [InlineData("~", TokenType.Tilde)]
        [InlineData("<<", TokenType.ShiftLeft)]
        [InlineData(">>", TokenType.ShiftRight)]
        [InlineData(">>>", TokenType.UnsignedShiftRight)]
        [InlineData(">>>=", TokenType.UnsignedShiftRightAssign)]
        [InlineData("++", TokenType.Increment)]
        [InlineData("--", TokenType.Decrement)]
        [InlineData("?", TokenType.Question)]
        [InlineData("?.", TokenType.QuestionDot)]
        [InlineData("??", TokenType.NullCoalesce)]
        [InlineData("!!", TokenType.BangBang)]
        [InlineData(".", TokenType.Dot)]
        [InlineData("..", TokenType.DotDot)]
        [InlineData("..=", TokenType.DotDotEquals)]
        [InlineData("...", TokenType.Ellipsis)]
        [InlineData("->", TokenType.Arrow)]
        [InlineData("=>", TokenType.FatArrow)]
        [InlineData("@", TokenType.At)]
        [InlineData("(", TokenType.LeftParen)]
        [InlineData(")", TokenType.RightParen)]
        [InlineData("{", TokenType.LeftBrace)]
        [InlineData("}", TokenType.RightBrace)]
        [InlineData("[", TokenType.LeftBracket)]
        [InlineData("]", TokenType.RightBracket)]
        [InlineData(";", TokenType.Semicolon)]
        [InlineData(",", TokenType.Comma)]
        [InlineData(":", TokenType.Colon)]
        public void OperatorsAndPunctuationLexAsThemselves(string source, TokenType expected)
        {
            Assert.Equal(new[] { expected, TokenType.EndOfFile }, Types(source));
        }

        /// <summary>
        /// Maximal munch: each of these prefixes a shorter operator, so a lexer that checked the
        /// short form first would split them. The whole `=`, `!`, `&lt;` and `?` families are the
        /// ones at risk.
        /// </summary>
        [Fact]
        public void LongOperatorsWinOverTheirPrefixes()
        {
            Assert.Equal(
                new[]
                {
                    TokenType.ReferenceEqual, TokenType.ReferenceNotEqual, TokenType.Spaceship,
                    TokenType.ShiftLeftAssign, TokenType.ShiftRightAssign, TokenType.NullCoalesceAssign,
                    TokenType.Ellipsis, TokenType.DotDotEquals, TokenType.EndOfFile,
                },
                Types("=== !== <=> <<= >>= ??= ... ..="));
        }

        /// <summary>
        /// The `&gt;` family is the deepest munch in the language: four tokens share the `&gt;&gt;`
        /// prefix, and getting the order wrong silently turns an unsigned shift into a signed one.
        /// </summary>
        [Fact]
        public void TheRightShiftFamilyMunchesLongestFirst()
        {
            Assert.Equal(
                new[]
                {
                    TokenType.Greater, TokenType.GreaterEqual, TokenType.ShiftRight,
                    TokenType.ShiftRightAssign, TokenType.UnsignedShiftRight,
                    TokenType.UnsignedShiftRightAssign, TokenType.EndOfFile,
                },
                Types("> >= >> >>= >>> >>>="));
        }

        /// <summary>
        /// Three levels of generic nesting close as one `&gt;&gt;&gt;`, which the parser has to
        /// split - the same bargain `&gt;&gt;` already forced, one level deeper.
        /// </summary>
        [Fact]
        public void TripleGenericNestingClosesAsOneUnsignedShift()
        {
            List<Token> tokens = Lex("fun f(x: Box<Box<Box<int>>>): void { }");

            Assert.Contains(tokens, token => token.Type == TokenType.UnsignedShiftRight);
            Assert.DoesNotContain(tokens, token => token.Type == TokenType.ShiftRight);
        }

        /// <summary>`::` was removed with the rest of the pre-spec token set: §2.6 makes `.` the only member-access operator.</summary>
        [Fact]
        public void DoubleColonIsTwoSeparateColons()
        {
            Assert.Equal(new[] { TokenType.Colon, TokenType.Colon, TokenType.EndOfFile }, Types("::"));
        }

        /// <summary>§5.7: `as?` is `as` then `?`. A type must follow `as`, so the parser can join them with no adjacency test.</summary>
        [Fact]
        public void SafeCastLexesAsKeywordThenQuestion()
        {
            Assert.Equal(
                new[] { TokenType.Identifier, TokenType.KeywordAs, TokenType.Question, TokenType.Identifier, TokenType.EndOfFile },
                Types("obj as? Dog"));
        }

        // ---- §5.8 literals ------------------------------------------------------------------

        [Theory]
        [InlineData("42", 42L)]
        [InlineData("0", 0L)]
        [InlineData("1_000_000", 1000000L)]
        [InlineData("0x2A", 42L)]
        [InlineData("0X2a", 42L)]
        [InlineData("0b0010_1010", 42L)]
        [InlineData("0B101", 5L)]
        [InlineData("0xFF_FF", 65535L)]
        public void IntegerLiteralsDecodeInEveryBase(string source, long expected)
        {
            Token token = Lex(source)[0];

            Assert.Equal(TokenType.IntegerLiteral, token.Type);
            Assert.Equal(expected, token.Payload.AsInteger);
        }

        [Theory]
        [InlineData("3.14", 3.14)]
        [InlineData("6.02e23", 6.02e23)]
        [InlineData("1e10", 1e10)]
        [InlineData("1.5e-3", 1.5e-3)]
        [InlineData("2E+4", 2e4)]
        [InlineData("1_000.5", 1000.5)]
        public void FloatLiteralsAreDecidedByAPointOrAnExponent(string source, double expected)
        {
            Token token = Lex(source)[0];

            Assert.Equal(TokenType.FloatLiteral, token.Type);
            Assert.Equal(expected, token.Payload.AsFloat);
        }

        /// <summary>
        /// The lookahead that makes first-class ranges (§5.4) lexable next to integers: a `.` only
        /// opens a fractional part when a digit follows, so `0..10` is three tokens, not a broken
        /// float.
        /// </summary>
        [Fact]
        public void ADotAfterDigitsOnlyStartsAFloatWhenADigitFollows()
        {
            Assert.Equal(
                new[] { TokenType.IntegerLiteral, TokenType.DotDot, TokenType.IntegerLiteral, TokenType.EndOfFile },
                Types("0..10"));

            Assert.Equal(
                new[] { TokenType.IntegerLiteral, TokenType.DotDotEquals, TokenType.IntegerLiteral, TokenType.EndOfFile },
                Types("0..=10"));

            // Not a float either: `1.` with no digit leaves the dot as member access.
            Assert.Equal(
                new[] { TokenType.IntegerLiteral, TokenType.Dot, TokenType.Identifier, TokenType.EndOfFile },
                Types("1.toString"));
        }

        [Fact]
        public void RangeOverAnExpressionLexesCleanly()
        {
            Assert.Equal(
                new[]
                {
                    TokenType.KeywordFor, TokenType.LeftParen, TokenType.Identifier, TokenType.KeywordIn,
                    TokenType.IntegerLiteral, TokenType.DotDot, TokenType.Identifier, TokenType.Dot,
                    TokenType.Identifier, TokenType.RightParen, TokenType.EndOfFile,
                },
                Types("for (i in 0..items.length)"));
        }

        [Theory]
        [InlineData("\"hello\"", "hello")]
        [InlineData("\"\"", "")]
        [InlineData("\"line1\\nline2\"", "line1\nline2")]
        [InlineData("\"tab\\there\"", "tab\there")]
        [InlineData("\"quote\\\"inside\"", "quote\"inside")]
        [InlineData("\"back\\\\slash\"", "back\\slash")]
        [InlineData("\"\\u0041\"", "A")]
        [InlineData("\"cost: \\$5\"", "cost: $5")]
        public void StringLiteralsDecodeTheirEscapes(string source, string expected)
        {
            Token token = Lex(source)[0];

            Assert.Equal(TokenType.StringLiteral, token.Type);
            Assert.Equal(expected, token.Payload.AsString);
        }

        /// <summary>
        /// §5.2: a literal carrying an unescaped `$` keeps its raw text instead of a decoded value,
        /// because splitting it into text and expression parts still needs to see which dollars
        /// were written `\$`.
        /// </summary>
        [Theory]
        [InlineData("\"Hello, $name!\"", "Hello, $name!")]
        [InlineData("\"You have ${cart.length} items.\"", "You have ${cart.length} items.")]
        [InlineData("\"mixed \\n $x\"", "mixed \\n $x")]
        public void InterpolatedStringsKeepTheirRawText(string source, string expectedRaw)
        {
            Token token = Lex(source)[0];

            Assert.Equal(TokenType.InterpolatedStringLiteral, token.Type);
            Assert.Equal(expectedRaw, token.Payload.AsString);
        }

        /// <summary>An escaped dollar does not make a literal interpolated - that is the point of writing `\$`.</summary>
        [Fact]
        public void EscapedDollarDoesNotTriggerInterpolation()
        {
            Assert.Equal(TokenType.StringLiteral, Lex("\"\\$5\"")[0].Type);
        }

        [Theory]
        [InlineData("'a'", 'a')]
        [InlineData("'\\n'", '\n')]
        [InlineData("'\\''", '\'')]
        [InlineData("'\\\\'", '\\')]
        [InlineData("'\\u0041'", 'A')]
        [InlineData("'\\0'", '\0')]
        public void CharacterLiteralsDecodeTheirEscapes(string source, char expected)
        {
            Token token = Lex(source)[0];

            Assert.Equal(TokenType.CharacterLiteral, token.Type);
            Assert.Equal(expected, token.Payload.AsCharacter);
        }

        // ---- §11 comments -------------------------------------------------------------------

        [Fact]
        public void OrdinaryCommentsAreTrivia()
        {
            Assert.Equal(
                new[] { TokenType.KeywordLet, TokenType.Identifier, TokenType.EndOfFile },
                Types("// leading\nlet /* inline */ x // trailing"));
        }

        [Fact]
        public void BlockCommentsDoNotNest()
        {
            // The first `*/` closes it, so the trailing `*/` is two ordinary tokens.
            Assert.Equal(
                new[] { TokenType.Star, TokenType.Slash, TokenType.EndOfFile },
                Types("/* outer /* inner */ */"));
        }

        /// <summary>§11: a doc comment carries meaning about the declaration after it, so it survives as a token.</summary>
        [Fact]
        public void DocCommentsBecomeTokensCarryingTheirText()
        {
            List<Token> tokens = Lex("/// Moves the entity.\n/// @param dx offset\nfun move");

            Assert.Equal(TokenType.DocComment, tokens[0].Type);
            Assert.Equal("Moves the entity.", tokens[0].Payload.AsString);
            Assert.Equal(TokenType.DocComment, tokens[1].Type);
            Assert.Equal("@param dx offset", tokens[1].Payload.AsString);
            Assert.Equal(TokenType.KeywordFun, tokens[2].Type);
        }

        [Fact]
        public void FourSlashesIsAnOrdinaryComment()
        {
            Assert.Equal(new[] { TokenType.EndOfFile }, Types("//// not a doc comment"));
        }

        // ---- locations ----------------------------------------------------------------------

        [Fact]
        public void TokensCarryTheirLineColumnAndLexeme()
        {
            List<Token> tokens = Lex("let x\n  = 42;");

            Assert.Equal(1, tokens[0].Location.Line);
            Assert.Equal(1, tokens[0].Location.Column);
            Assert.Equal("let", tokens[0].ToString());

            Token assign = tokens[2];
            Assert.Equal(TokenType.Assign, assign.Type);
            Assert.Equal(2, assign.Location.Line);
            Assert.Equal(3, assign.Location.Column);

            Assert.Equal("42", tokens[3].ToString());
        }

        // ---- malformed input ----------------------------------------------------------------

        [Theory]
        [InlineData("\"unterminated")]
        [InlineData("\"multi\nline\"")]
        [InlineData("\"bad \\q escape\"")]
        [InlineData("\"\\u12\"")]
        [InlineData("'")]
        [InlineData("''")]
        [InlineData("'ab'")]
        [InlineData("/* unterminated")]
        [InlineData("0x")]
        [InlineData("0b")]
        [InlineData("#")]
        public void MalformedInputIsReportedAndSkipped(string source)
        {
            Lexer lexer = new Lexer(SurtrSourceBuffer.FromString(source));
            List<Token> tokens = lexer.Tokenize();

            SurtrDiagnostic diagnostic = lexer.Diagnostics[0];

            Assert.True(lexer.Diagnostics.HasErrors);
            Assert.True(diagnostic.IsError);
            Assert.True(diagnostic.Span.Start.Line >= 1);
            Assert.StartsWith("SURTR1", diagnostic.Id);
            Assert.Contains("<memory>", diagnostic.ToString());

            // Scanning still reaches the end, which is what lets the parser run and report what
            // else is wrong with the file.
            Assert.Equal(TokenType.EndOfFile, tokens[tokens.Count - 1].Type);
        }

        /// <summary>
        /// One bad literal does not hide the next: recovery is what makes a second problem
        /// reachable at all.
        /// </summary>
        [Fact]
        public void SeveralProblemsAreAllReported()
        {
            Lexer lexer = new Lexer(SurtrSourceBuffer.FromString("let a = #; let b = 0x; let c = '';"));
            lexer.Tokenize();

            SurtrDiagnosticCode[] reported = lexer.Diagnostics.Select(diagnostic => diagnostic.Code).ToArray();

            Assert.Contains(SurtrDiagnosticCode.UnexpectedCharacter, reported);
            Assert.Contains(SurtrDiagnosticCode.InvalidNumericLiteral, reported);
            Assert.Contains(SurtrDiagnosticCode.InvalidCharacterLiteral, reported);
        }

        /// <summary>
        /// Recovering inside a bad literal must not invent a second problem out of the first: a
        /// failed literal is skipped whole, so its closing quote is never read as opening another.
        /// </summary>
        [Theory]
        [InlineData("\"bad \\q escape\"")]
        [InlineData("\"\\u12\"")]
        [InlineData("''")]
        [InlineData("'ab'")]
        public void ABadLiteralReportsOnce(string source)
        {
            Lexer lexer = new Lexer(SurtrSourceBuffer.FromString(source));
            lexer.Tokenize();

            Assert.Single(lexer.Diagnostics);
        }

        /// <summary>A diagnostic covers the whole offending construct, not just its first character.</summary>
        [Fact]
        public void ADiagnosticSpansWhatWentWrong()
        {
            Lexer lexer = new Lexer(SurtrSourceBuffer.FromString("\"unterminated"));
            lexer.Tokenize();

            SurtrDiagnostic diagnostic = Assert.Single(lexer.Diagnostics);

            Assert.Equal(SurtrDiagnosticCode.UnterminatedStringLiteral, diagnostic.Code);
            Assert.Equal(0, diagnostic.Span.Start.Position);
            Assert.Equal(13, diagnostic.Span.Length);
        }

        /// <summary>The simple behaviour is still one call away.</summary>
        [Fact]
        public void ThrowIfErrorsRaisesTheFirstProblem()
        {
            Lexer lexer = new Lexer(SurtrSourceBuffer.FromString("#"));
            lexer.Tokenize();

            SurtrDiagnosticException failure = Assert.Throws<SurtrDiagnosticException>(() => lexer.Diagnostics.ThrowIfErrors());
            Assert.Equal(SurtrDiagnosticCode.UnexpectedCharacter, failure.Diagnostic.Code);
        }

        // ---- a realistic slice of the language ----------------------------------------------

        /// <summary>
        /// A declaration exercising the pieces most likely to interact badly: generics closing with
        /// `&gt;&gt;`, a nullable type, a lambda, an attribute, and a dict type.
        /// </summary>
        [Fact]
        public void ARealisticDeclarationLexesEndToEnd()
        {
            List<Token> tokens = Lex(
                "@Range(0, 100)\n" +
                "public virtual fun clamp<T : IComparable<T>>(v: T?, lookup: {string: int}): T {\n" +
                "    return v ?? items.first((x) => x >= 0);\n" +
                "}");

            Assert.Equal(TokenType.At, tokens[0].Type);
            Assert.DoesNotContain(tokens, token => token.Type == TokenType.Invalid);

            // The tail of `<T : IComparable<T>>` arrives as one `>>`, which the parser splits in
            // type-argument position - the bargain documented on TokenType.
            Assert.Contains(tokens, token => token.Type == TokenType.ShiftRight);

            Assert.Contains(tokens, token => token.Type == TokenType.Question);
            Assert.Contains(tokens, token => token.Type == TokenType.NullCoalesce);
            Assert.Contains(tokens, token => token.Type == TokenType.FatArrow);
            Assert.Contains(tokens, token => token.Type == TokenType.GreaterEqual);
        }
    }
}
