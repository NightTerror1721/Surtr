#nullable enable

using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.CodeGen;
using Surtr.Compiler.Syntax;
using System;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// Pins the two naming conventions that are ABI: an overloaded operator's name and a synthetic
    /// member's. Both go into a module's real tables and travel in the image, so changing either
    /// invalidates every <c>.surtrc</c> already written.
    /// </summary>
    public sealed class MemberNameTests
    {
        #region Operators
        [Theory]
        [InlineData(TokenType.Plus, 2, "op_+")]
        [InlineData(TokenType.Minus, 2, "op_-")]
        [InlineData(TokenType.Star, 2, "op_*")]
        [InlineData(TokenType.Slash, 2, "op_/")]
        [InlineData(TokenType.Percent, 2, "op_%")]
        [InlineData(TokenType.Ampersand, 2, "op_&")]
        [InlineData(TokenType.Pipe, 2, "op_|")]
        [InlineData(TokenType.Caret, 2, "op_^")]
        [InlineData(TokenType.ShiftLeft, 2, "op_<<")]
        [InlineData(TokenType.ShiftRight, 2, "op_>>")]
        [InlineData(TokenType.UnsignedShiftRight, 2, "op_>>>")]
        [InlineData(TokenType.Equal, 2, "op_==")]
        [InlineData(TokenType.Spaceship, 2, "op_<=>")]
        [InlineData(TokenType.LogicalNot, 1, "op_!")]
        [InlineData(TokenType.Tilde, 1, "op_~")]
        [InlineData(TokenType.Increment, 1, "op_++")]
        [InlineData(TokenType.Decrement, 1, "op_--")]
        [InlineData(TokenType.LeftBracket, 1, "op_[]")]
        public void EveryOverloadableOperatorHasItsSymbolBehindThePrefix(TokenType op, int arity, string expected)
        {
            Assert.Equal(expected, OperatorNames.For(op, arity));
        }

        [Fact]
        public void TheUnaryAndBinaryFormsOfMinusAreToldApart()
        {
            Assert.Equal("op_-", OperatorNames.For(TokenType.Minus, 2));
            Assert.Equal("op_-u", OperatorNames.For(TokenType.Minus, 1));
        }

        [Fact]
        public void AnIndexedWriteSharesItsNameWithAnIndexedRead()
        {
            // §5.6 gives the two forms one token and separates them by arity, which a signature key
            // does too - one takes an index, the other an index and a value.
            Assert.Equal(OperatorNames.For(TokenType.LeftBracket, 1), OperatorNames.For(TokenType.LeftBracket, 2));
        }

        [Fact]
        public void AnOperatorNameCannotBeSpelledInSource()
        {
            // An identifier is letter|_ then letterOrDigit|_, so every one of these contains at
            // least one character no declaration can produce.
            foreach (var op in new[]
            {
                TokenType.Plus, TokenType.Equal, TokenType.Spaceship, TokenType.LeftBracket, TokenType.Increment,
            })
            {
                string name = OperatorNames.For(op, 2);
                Assert.Contains(name, c => !char.IsLetterOrDigit(c) && c != '_');
            }
        }

        [Fact]
        public void ANonOverloadableTokenIsRejected()
        {
            Assert.Throws<ArgumentException>(() => OperatorNames.For(TokenType.Question, 2));
            Assert.False(OperatorNames.TryGetSymbol(TokenType.Question, out _));
        }
        #endregion

        #region Conversions
        [Fact]
        public void AConversionIsDeclaredUnderOneNameAndEmittedUnderItsTarget()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();
            var module = new ModuleSymbol("game.core");

            var vec2 = factory.DeclareType("Vec2", TypeSymbolKind.Class, module);
            var vec3 = factory.DeclareType("Vec3", TypeSymbolKind.Class, module);

            var toVec3 = new MethodSymbol(OperatorNames.For(TokenType.KeywordAs, 1), vec2, vec3)
            {
                Role = MethodRole.Operator,
                IsConversion = true,
                IsStatic = true,
                Parameters = new[] { new ParameterSymbol("v", vec2, 0) },
            };

            Assert.Equal("op_as", toVec3.Name);
            Assert.Equal("op_as$Ogame.core:Vec3;", emitter.EmitMethodName(toVec3));
        }

        [Fact]
        public void TwoConversionsFromOneSourceTypeGetDifferentEmittedNames()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();
            var module = new ModuleSymbol("game.core");

            var vec2 = factory.DeclareType("Vec2", TypeSymbolKind.Class, module);
            var vec3 = factory.DeclareType("Vec3", TypeSymbolKind.Class, module);

            MethodSymbol Conversion(TypeSymbol target) => new MethodSymbol(OperatorNames.Conversion, vec2, target)
            {
                Role = MethodRole.Operator,
                IsConversion = true,
                IsStatic = true,
                Parameters = new[] { new ParameterSymbol("v", vec2, 0) },
            };

            // Same name, same parameter list: a signature key excludes the return, so without the
            // target in the name these two would collide in the method table.
            var toVec3 = Conversion(vec3);
            var toString = Conversion(factory.String);

            Assert.Equal(toVec3.Name, toString.Name);
            Assert.NotEqual(emitter.EmitMethodName(toVec3), emitter.EmitMethodName(toString));
            Assert.Equal("op_as$S", emitter.EmitMethodName(toString));
        }

        [Fact]
        public void AnOrdinaryOperatorEmitsUnderTheNameItWasDeclaredWith()
        {
            var factory = new TypeSymbolFactory();
            var emitter = new DescriptorEmitter();
            var vec2 = factory.DeclareType("Vec2", TypeSymbolKind.Class, new ModuleSymbol("game.core"));

            var plus = new MethodSymbol(OperatorNames.For(TokenType.Plus, 2), vec2, vec2)
            {
                Role = MethodRole.Operator,
                IsStatic = true,
            };

            Assert.Equal("op_+", emitter.EmitMethodName(plus));
        }
        #endregion

        #region Synthetics
        [Fact]
        public void EverySyntheticNameFollowsOneShape()
        {
            Assert.Equal("$lambda$move$0", SyntheticNames.Lambda("move", 0));
            Assert.Equal("$lambda$move$3", SyntheticNames.Lambda("move", 3));
            Assert.Equal("$backing$health", SyntheticNames.BackingField("health"));
            Assert.Equal("$instance$Registry", SyntheticNames.Instance("Registry"));
        }

        [Fact]
        public void ALeadingMarkerIsWhatMakesANameSynthetic()
        {
            Assert.True(SyntheticNames.IsSynthetic(SyntheticNames.Lambda("move", 0)));
            Assert.True(SyntheticNames.IsSynthetic(SyntheticNames.BackingField("health")));
            Assert.True(SyntheticNames.IsSynthetic(SyntheticNames.Instance("Registry")));

            Assert.False(SyntheticNames.IsSynthetic("move"));
            Assert.False(SyntheticNames.IsSynthetic(""));
        }

        [Fact]
        public void ASyntheticNameCannotBeSpelledInSource()
        {
            Assert.False(char.IsLetter(SyntheticNames.Marker));
            Assert.NotEqual('_', SyntheticNames.Marker);
        }

        [Fact]
        public void ANameAnotherLayerLooksForIsNotSynthetic()
        {
            // get_x/set_x are what SurtrTypeLinker looks for when it wires a property up, and a
            // bridge carries the contract method's own name because the slot is keyed on it — so
            // marking either would hide it from the layer that has to find it.
            Assert.False(SyntheticNames.IsSynthetic("get_health"));
            Assert.False(SyntheticNames.IsSynthetic("set_health"));
            Assert.False(SyntheticNames.IsSynthetic("compareTo"));
        }

        [Fact]
        public void ANegativeIndexIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SyntheticNames.Lambda("move", -1));
        }
        #endregion
    }
}
