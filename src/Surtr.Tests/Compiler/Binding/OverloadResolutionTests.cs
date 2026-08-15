#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.Symbols;
using System.Collections.Generic;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// Covers §3.5's rules 2 through 4: which candidates the arguments fill, which of those is most
    /// specific, and the refusal to guess when two tie. Rule 1 belongs to a declaration rather than
    /// a call and lives in <see cref="BinderTests"/>.
    /// </summary>
    public sealed class OverloadResolutionTests
    {
        private static OverloadResolution Setup(out TypeSymbolFactory factory)
        {
            factory = new TypeSymbolFactory();
            return new OverloadResolution(new Conversions(factory));
        }

        private static MethodSymbol Method(TypeSymbolFactory factory, string name, params ParameterSymbol[] parameters)
        {
            var owner = factory.DeclareType("Owner", TypeSymbolKind.Class, new ModuleSymbol("game.core"));
            var method = new MethodSymbol(name, owner, factory.Void);

            for (int i = 0; i < parameters.Length; i++)
                parameters[i] = parameters[i];

            method.Parameters = parameters;
            return method;
        }

        private static ParameterSymbol Parameter(string name, TypeSymbol type, int ordinal, bool hasDefault = false, bool isVararg = false)
            => new ParameterSymbol(name, type, ordinal) { HasDefaultValue = hasDefault, IsVararg = isVararg };

        private static ArgumentInfo Arg(TypeSymbol type, string? name = null) => new ArgumentInfo(type, name);

        #region Applicability
        [Fact]
        public void ACallWithNoCandidatesSaysSo()
        {
            var resolution = Setup(out var factory);

            var result = resolution.Resolve(new List<MethodSymbol>(), new[] { Arg(factory.Int) });

            Assert.Equal(OverloadStatus.NoCandidates, result.Status);
        }

        [Fact]
        public void TheParameterListDecidesWhichOverloadIsMeant()
        {
            var resolution = Setup(out var factory);

            var ofInt = Method(factory, "log", Parameter("code", factory.Int, 0));
            var ofString = Method(factory, "log", Parameter("message", factory.String, 0));

            Assert.Same(ofString, resolution.Resolve(new[] { ofInt, ofString }, new[] { Arg(factory.String) }).Method);
            Assert.Same(ofInt, resolution.Resolve(new[] { ofInt, ofString }, new[] { Arg(factory.Int) }).Method);
        }

        [Fact]
        public void AnArgumentThatFitsNoParameterMakesTheCallInapplicable()
        {
            var resolution = Setup(out var factory);

            var log = Method(factory, "log", Parameter("code", factory.Int, 0));
            var result = resolution.Resolve(new[] { log }, new[] { Arg(factory.String) });

            Assert.Equal(OverloadStatus.NoApplicableCandidate, result.Status);
            Assert.Same(log, Assert.Single(result.Candidates));
        }

        [Fact]
        public void TooManyArgumentsIsInapplicable()
        {
            var resolution = Setup(out var factory);

            var log = Method(factory, "log", Parameter("code", factory.Int, 0));

            Assert.Equal(
                OverloadStatus.NoApplicableCandidate,
                resolution.Resolve(new[] { log }, new[] { Arg(factory.Int), Arg(factory.Int) }).Status);
        }

        [Fact]
        public void AnImplicitConversionMakesACandidateApplicable()
        {
            var resolution = Setup(out var factory);

            var takesFloat = Method(factory, "scale", Parameter("by", factory.Float, 0));

            Assert.True(resolution.Resolve(new[] { takesFloat }, new[] { Arg(factory.Int) }).IsResolved);
        }
        #endregion

        #region Defaults and named arguments
        [Fact]
        public void ADefaultFillsATrailingOmission()
        {
            var resolution = Setup(out var factory);

            var spawn = Method(
                factory,
                "spawn",
                Parameter("x", factory.Float, 0),
                Parameter("y", factory.Float, 1),
                Parameter("hp", factory.Int, 2, hasDefault: true));

            Assert.True(resolution.Resolve(new[] { spawn }, new[] { Arg(factory.Float), Arg(factory.Float) }).IsResolved);
            Assert.True(resolution.Resolve(
                new[] { spawn },
                new[] { Arg(factory.Float), Arg(factory.Float), Arg(factory.Int) }).IsResolved);
        }

        [Fact]
        public void AMissingParameterWithNoDefaultIsInapplicable()
        {
            var resolution = Setup(out var factory);

            var spawn = Method(factory, "spawn", Parameter("x", factory.Float, 0), Parameter("y", factory.Float, 1));

            Assert.Equal(
                OverloadStatus.NoApplicableCandidate,
                resolution.Resolve(new[] { spawn }, new[] { Arg(factory.Float) }).Status);
        }

        [Fact]
        public void NamedArgumentsMayComeInAnyOrderAndSkipDefaults()
        {
            var resolution = Setup(out var factory);

            var spawn = Method(
                factory,
                "spawn",
                Parameter("x", factory.Float, 0),
                Parameter("y", factory.Float, 1),
                Parameter("hp", factory.Int, 2, hasDefault: true));

            Assert.True(resolution.Resolve(
                new[] { spawn },
                new[] { Arg(factory.Float, "y"), Arg(factory.Float, "x") }).IsResolved);

            Assert.True(resolution.Resolve(
                new[] { spawn },
                new[] { Arg(factory.Float), Arg(factory.Float, "y") }).IsResolved);
        }

        [Fact]
        public void OnceNamingStartsItContinuesToTheEnd()
        {
            var resolution = Setup(out var factory);

            var spawn = Method(factory, "spawn", Parameter("x", factory.Float, 0), Parameter("y", factory.Float, 1));

            // §3.5: `spawn(1.0, y: 2.0)` is fine; `spawn(x: 1.0, 2.0)` is not.
            Assert.Equal(
                OverloadStatus.NoApplicableCandidate,
                resolution.Resolve(new[] { spawn }, new[] { Arg(factory.Float, "x"), Arg(factory.Float) }).Status);
        }

        [Fact]
        public void AParameterCannotBeGivenTwice()
        {
            var resolution = Setup(out var factory);

            var spawn = Method(factory, "spawn", Parameter("x", factory.Float, 0), Parameter("y", factory.Float, 1));

            Assert.Equal(
                OverloadStatus.NoApplicableCandidate,
                resolution.Resolve(new[] { spawn }, new[] { Arg(factory.Float, "x"), Arg(factory.Float, "x") }).Status);
        }

        [Fact]
        public void ANameNoParameterCarriesIsInapplicable()
        {
            var resolution = Setup(out var factory);

            var spawn = Method(factory, "spawn", Parameter("x", factory.Float, 0));

            Assert.Equal(
                OverloadStatus.NoApplicableCandidate,
                resolution.Resolve(new[] { spawn }, new[] { Arg(factory.Float, "z") }).Status);
        }
        #endregion

        #region Varargs
        [Fact]
        public void VarargsAbsorbsTheSurplus()
        {
            var resolution = Setup(out var factory);

            var format = Method(
                factory,
                "format",
                Parameter("pattern", factory.String, 0),
                Parameter("args", factory.Array(factory.String), 1, isVararg: true));

            Assert.True(resolution.Resolve(new[] { format }, new[] { Arg(factory.String) }).IsResolved);
            Assert.True(resolution.Resolve(
                new[] { format },
                new[] { Arg(factory.String), Arg(factory.String), Arg(factory.String) }).IsResolved);
        }

        [Fact]
        public void VarargsRejectsASurplusOfTheWrongElementType()
        {
            var resolution = Setup(out var factory);

            var format = Method(
                factory,
                "format",
                Parameter("pattern", factory.String, 0),
                Parameter("args", factory.Array(factory.String), 1, isVararg: true));

            Assert.Equal(
                OverloadStatus.NoApplicableCandidate,
                resolution.Resolve(new[] { format }, new[] { Arg(factory.String), Arg(factory.Int) }).Status);
        }

        [Fact]
        public void TheWholeArrayMayBePassedInstead()
        {
            var resolution = Setup(out var factory);

            var format = Method(
                factory,
                "format",
                Parameter("pattern", factory.String, 0),
                Parameter("args", factory.Array(factory.String), 1, isVararg: true));

            Assert.True(resolution.Resolve(
                new[] { format },
                new[] { Arg(factory.String), Arg(factory.Array(factory.String)) }).IsResolved);
        }

        [Fact]
        public void ANonVarargsCandidateAlwaysBeatsAVarargsOne()
        {
            var resolution = Setup(out var factory);

            var exact = Method(factory, "log", Parameter("a", factory.String, 0), Parameter("b", factory.String, 1));
            var variadic = Method(
                factory,
                "log",
                Parameter("a", factory.String, 0),
                Parameter("rest", factory.Array(factory.String), 1, isVararg: true));

            var result = resolution.Resolve(new[] { variadic, exact }, new[] { Arg(factory.String), Arg(factory.String) });

            Assert.Same(exact, result.Method);
        }
        #endregion

        #region Specificity
        [Fact]
        public void AnExactMatchBeatsAConversion()
        {
            var resolution = Setup(out var factory);

            var takesInt = Method(factory, "f", Parameter("v", factory.Int, 0));
            var takesFloat = Method(factory, "f", Parameter("v", factory.Float, 0));

            Assert.Same(takesInt, resolution.Resolve(new[] { takesFloat, takesInt }, new[] { Arg(factory.Int) }).Method);
        }

        [Fact]
        public void ADerivedParameterBeatsItsBase()
        {
            var resolution = Setup(out var factory);
            var module = new ModuleSymbol("game.core");

            var animal = factory.DeclareType("Animal", TypeSymbolKind.Class, module);
            var dog = factory.DeclareType("Dog", TypeSymbolKind.Class, module);
            dog.BaseType = animal;

            var takesAnimal = Method(factory, "f", Parameter("a", animal, 0));
            var takesDog = Method(factory, "f", Parameter("a", dog, 0));

            Assert.Same(takesDog, resolution.Resolve(new[] { takesAnimal, takesDog }, new[] { Arg(dog) }).Method);
        }

        [Fact]
        public void ACandidateNeedingNoDefaultsWins()
        {
            var resolution = Setup(out var factory);

            var plain = Method(factory, "f", Parameter("a", factory.Int, 0));
            var defaulted = Method(
                factory,
                "f",
                Parameter("a", factory.Int, 0),
                Parameter("b", factory.Int, 1, hasDefault: true));

            Assert.Same(plain, resolution.Resolve(new[] { defaulted, plain }, new[] { Arg(factory.Int) }).Method);
        }

        [Fact]
        public void ATieIsRejectedRatherThanGuessed()
        {
            var resolution = Setup(out var factory);
            var module = new ModuleSymbol("game.core");

            // Two candidates each needing one conversion, neither better everywhere.
            var left = Method(factory, "f", Parameter("a", factory.Float, 0), Parameter("b", factory.Int, 1));
            var right = Method(factory, "f", Parameter("a", factory.Int, 0), Parameter("b", factory.Float, 1));

            var result = resolution.Resolve(new[] { left, right }, new[] { Arg(factory.Int), Arg(factory.Int) });

            Assert.Equal(OverloadStatus.Ambiguous, result.Status);
            Assert.Equal(2, result.Candidates.Count);
            Assert.Null(result.Method);
            Assert.NotNull(module);
        }

        [Fact]
        public void BeingBestAgainstOneIsNotBeingBestAgainstAll()
        {
            var resolution = Setup(out var factory);

            // A beats C, C ties with B, B ties with A: the winner must be checked against every
            // candidate, not just the one it happened to be compared with.
            var a = Method(factory, "f", Parameter("x", factory.Float, 0), Parameter("y", factory.Int, 1));
            var b = Method(factory, "f", Parameter("x", factory.Int, 0), Parameter("y", factory.Float, 1));

            var result = resolution.Resolve(new[] { a, b }, new[] { Arg(factory.Int), Arg(factory.Int) });

            Assert.Equal(OverloadStatus.Ambiguous, result.Status);
        }
        #endregion
    }
}
