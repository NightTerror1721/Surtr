#nullable enable

using Surtr.Compiler.Diagnostics;
using System.Collections.Generic;
using Surtr.Compiler.Syntax.Ast;

namespace Surtr.Compiler.Syntax
{
    public sealed partial class Parser
    {
        /// <summary>
        /// Parses a type: a name, an array, a dict, a tuple or a closure, with any number of
        /// trailing <c>[]</c> and <c>?</c> suffixes (§5.3, §5.1).
        /// </summary>
        private TypeSyntax ParseType()
        {
            TypeSyntax type = ParseCoreType();

            // Suffixes bind left to right, so `int[]?` is a nullable array and `int?[]` an array
            // of nullable ints. Looping here rather than recursing keeps that order obvious.
            while (true)
            {
                // A suffixed type spans the type it wraps plus the suffix: `int[]` starts where
                // `int` did, not at the bracket.
                if (reader.Check(TokenType.LeftBracket) && reader.CheckAt(1, TokenType.RightBracket))
                {
                    reader.Advance();
                    reader.Advance();
                    type = new ArrayTypeSyntax(SourceSpan.FromBounds(type.Location, reader.ConsumedEnd), type);
                    continue;
                }

                if (reader.Check(TokenType.Question))
                {
                    reader.Advance();
                    type = new NullableTypeSyntax(SourceSpan.FromBounds(type.Location, reader.ConsumedEnd), type);
                    continue;
                }

                return type;
            }
        }

        /// <summary>Parses a type without its <c>[]</c>/<c>?</c> suffixes.</summary>
        private TypeSyntax ParseCoreType()
        {
            SourceLocation start = reader.CurrentLocation;

            if (reader.Check(TokenType.LeftBrace))
            {
                return ParseDictType();
            }

            if (reader.Check(TokenType.LeftParen))
            {
                return ParseTupleOrClosureType();
            }

            if (!reader.Check(TokenType.Identifier))
            {
                throw reader.Error(SurtrDiagnosticCode.ExpectedType, "Expected a type.");
            }

            List<string> path = new List<string> { reader.Advance().ToString() };
            while (reader.Check(TokenType.Dot) && reader.CheckAt(1, TokenType.Identifier))
            {
                reader.Advance();
                path.Add(reader.Advance().ToString());
            }

            IReadOnlyList<TypeSyntax> typeArguments = reader.Check(TokenType.Less)
                ? ParseTypeArgumentList()
                : EmptyTypes;

            return new NamedTypeSyntax(SpanFrom(start), path, typeArguments);
        }

        /// <summary>Parses <c>&lt;A, B&gt;</c>, closing through <see cref="TokenReader.ConsumeTypeArgumentClose"/>.</summary>
        private IReadOnlyList<TypeSyntax> ParseTypeArgumentList()
        {
            reader.Expect(TokenType.Less, "'<' to open the type arguments");

            List<TypeSyntax> arguments = new List<TypeSyntax>();
            while (true)
            {
                arguments.Add(ParseType());

                if (reader.Match(TokenType.Comma))
                {
                    // Trailing comma before the close (§1).
                    if (reader.CheckTypeArgumentClose())
                    {
                        break;
                    }

                    continue;
                }

                break;
            }

            reader.ConsumeTypeArgumentClose();
            return arguments;
        }

        /// <summary>Parses <c>{K: V}</c> — a self-delimiting production, per §5.3.</summary>
        private TypeSyntax ParseDictType()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.LeftBrace, "'{' to open the dictionary type");

            TypeSyntax key = ParseType();
            reader.Expect(TokenType.Colon, "':' between the key and value types");
            TypeSyntax value = ParseType();

            reader.Expect(TokenType.RightBrace, "'}' to close the dictionary type");
            return new DictTypeSyntax(SpanFrom(start), key, value);
        }

        /// <summary>
        /// Parses <c>(A, B)</c> or <c>(A, B) -&gt; R</c>. The two are identical until the closing
        /// paren, so this parses the shared prefix once and lets the <c>-&gt;</c> decide.
        /// </summary>
        private TypeSyntax ParseTupleOrClosureType()
        {
            SourceLocation start = reader.CurrentLocation;
            reader.Expect(TokenType.LeftParen, "'(' to open the type");

            List<TypeSyntax> elements = new List<TypeSyntax>();
            while (!reader.Check(TokenType.RightParen))
            {
                elements.Add(ParseType());
                if (!reader.Match(TokenType.Comma))
                {
                    break;
                }
            }

            reader.Expect(TokenType.RightParen, "')' to close the type");

            if (reader.Match(TokenType.Arrow))
            {
                return new ClosureTypeSyntax(SpanFrom(start), elements, ParseType());
            }

            return new TupleTypeSyntax(SpanFrom(start), elements);
        }

        /// <summary>Parses <c>&lt;T, U : IComparable&lt;U&gt; &amp; IEquatable&lt;U&gt;&gt;</c> (§6).</summary>
        private IReadOnlyList<TypeParameterSyntax> ParseTypeParameterList()
        {
            if (!reader.Check(TokenType.Less))
            {
                return EmptyTypeParameters;
            }

            reader.Advance();

            List<TypeParameterSyntax> parameters = new List<TypeParameterSyntax>();
            while (true)
            {
                SourceLocation start = reader.CurrentLocation;
                string name = reader.ExpectIdentifier("a type parameter name");

                List<TypeSyntax> constraints = new List<TypeSyntax>();
                if (reader.Match(TokenType.Colon))
                {
                    do
                    {
                        constraints.Add(ParseType());
                    }
                    while (reader.Match(TokenType.Ampersand));
                }

                parameters.Add(new TypeParameterSyntax(SpanFrom(start), name, constraints));

                if (reader.Match(TokenType.Comma))
                {
                    if (reader.CheckTypeArgumentClose())
                    {
                        break;
                    }

                    continue;
                }

                break;
            }

            reader.ConsumeTypeArgumentClose();
            return parameters;
        }
    }
}
