#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Surtr.Compiler.Syntax;
using Surtr.Compiler.Syntax.Ast;
using Xunit;

namespace Surtr.Tests.Compiler.Syntax
{
    /// <summary>
    /// Holds the parser to the invariant <see cref="SyntaxNode.Span"/> states: a node covers the
    /// source of its whole construct, and therefore of every child hanging off it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enforced by reflecting over each node's children rather than by asserting on a list of node
    /// types, and that is the point: a production added later is covered without anyone remembering
    /// to come back here. The mistake this catches is easy to make and easy to miss — a node that
    /// parses its left operand before it knows what it is building captures its start position one
    /// token late and spans only the operator's own half, which every one of that production's own
    /// tests still passes.
    /// </para>
    /// <para>
    /// It also reads as a missing feature rather than a wrong underline. Anything that walks the
    /// tree by position — go-to-definition, hover, a refactoring — prunes on containment, so a
    /// parent that fails to cover a child silently discards the subtree the cursor is in, and the
    /// symptom is a tool that answers everywhere except over one kind of sub-expression.
    /// </para>
    /// </remarks>
    public sealed class SyntaxSpanInvariantTests
    {
        /// <summary>Every construct in the language, in one file.</summary>
        [Fact]
        public void EveryNodeInTheSpecSampleCoversItsChildren()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Compiler", "Syntax", "Sample.surtr");
            var parser = new Parser(SurtrSourceBuffer.FromFile(path));
            CompilationUnitSyntax unit = parser.ParseCompilationUnit();

            Assert.False(parser.Diagnostics.HasErrors);

            string text = File.ReadAllText(path);
            List<string> violations = Violations(unit, text, out int visited);

            // A walk that reaches nothing would pass every assertion below it, so the corpus being
            // substantial is part of what is being asserted.
            Assert.True(visited > 300, $"expected a substantial tree, walked {visited} nodes");
            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        /// <summary>
        /// The shapes that build a node around an operand already parsed, which is where the start
        /// position is easiest to capture one token too late.
        /// </summary>
        [Theory]
        [InlineData("a.b")]
        [InlineData("a?.b")]
        [InlineData("a.b.c")]
        [InlineData("a.b(1).c")]
        [InlineData("a[0].b")]
        [InlineData("a++")]
        [InlineData("a!!")]
        [InlineData("a is int")]
        [InlineData("a as int")]
        [InlineData("a as? int")]
        [InlineData("a ? b : c")]
        [InlineData("a + b * c")]
        [InlineData("a?.b!!.c")]
        public void AnExpressionCoversItsChildren(string expression)
        {
            string source = $"fun f(): void {{ let x = {expression}; }}";
            var parser = new Parser(SurtrSourceBuffer.FromString(source));
            CompilationUnitSyntax unit = parser.ParseCompilationUnit();

            Assert.False(parser.Diagnostics.HasErrors);

            List<string> violations = Violations(unit, source, out _);
            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        /// <summary>The statement forms, which wrap expressions and other statements.</summary>
        [Theory]
        [InlineData("this.field = 1;")]
        [InlineData("this.field += other.value;")]
        [InlineData("if (a.b) { c.d(); } else { e.f(); }")]
        [InlineData("while (a.b) { c++; }")]
        [InlineData("for (i in 0..n) { a[i] = i; }")]
        [InlineData("for (item in items) { item.use(); }")]
        [InlineData("switch (a.b) { case 1: break; default: break; }")]
        [InlineData("try { a.b(); } catch (e: Exception) { e.report(); } finally { c.d(); }")]
        [InlineData("return a.b as int;")]
        [InlineData("throw Exception(\"x\");")]
        [InlineData("let f = (x: int) => x.value + 1;")]
        public void AStatementCoversItsChildren(string statement)
        {
            string source = $"fun f(): void {{ {statement} }}";
            var parser = new Parser(SurtrSourceBuffer.FromString(source));
            CompilationUnitSyntax unit = parser.ParseCompilationUnit();

            Assert.False(parser.Diagnostics.HasErrors);

            List<string> violations = Violations(unit, source, out _);
            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        /// <summary>
        /// A <c>${...}</c> hole is scanned out of the file, so what it produces is measured against
        /// that file rather than against a buffer of its own.
        /// </summary>
        /// <remarks>
        /// Worth a test of its own beside the structural walk, because the walk only asks whether a
        /// child sits inside its parent, and the failure this guards against is one where the child
        /// lands somewhere else entirely: nodes from a hole used to start at offset zero however far
        /// into the file they were written, so an editor sent to one arrived at the top of the file.
        /// The padding is what makes that visible — without it, offset zero and the real position
        /// are close enough to pass.
        /// </remarks>
        [Fact]
        public void AnInterpolationHoleIsMeasuredAgainstTheFile()
        {
            const string padding = "// ------------------------------------------------\n";
            string source = padding + "fun f(): void { let s = \"x ${alpha + beta}\"; }";

            var parser = new Parser(SurtrSourceBuffer.FromString(source));
            CompilationUnitSyntax unit = parser.ParseCompilationUnit();
            Assert.False(parser.Diagnostics.HasErrors);

            InterpolatedStringExpressionSyntax interpolated = Descendants(unit)
                .OfType<InterpolatedStringExpressionSyntax>()
                .Single();

            BinaryExpressionSyntax hole = interpolated.Parts.OfType<BinaryExpressionSyntax>().Single();

            // The hole's nodes name the text they were actually written as.
            Assert.Equal("alpha + beta", source.Substring(hole.Span.Start.Position, hole.Span.Length));
            Assert.Equal("alpha", source.Substring(hole.Left.Span.Start.Position, hole.Left.Span.Length));
            Assert.Equal("beta", source.Substring(hole.Right.Span.Start.Position, hole.Right.Span.Length));

            // And they are on the literal's line, which is the only line a literal can be on.
            Assert.Equal(2, hole.Span.Start.Line);
            Assert.True(interpolated.Span.Contains(hole.Span));
        }

        // ------------------------------------------------------------------------------------

        /// <summary>Every node under a root, itself included.</summary>
        private static IEnumerable<SyntaxNode> Descendants(SyntaxNode root)
        {
            yield return root;

            foreach (SyntaxNode child in ChildrenOf(root))
            {
                foreach (SyntaxNode nested in Descendants(child))
                    yield return nested;
            }
        }

        /// <summary>Every parent that fails to cover a child, described against the source.</summary>
        private static List<string> Violations(SyntaxNode root, string text, out int visited)
        {
            var violations = new List<string>();
            int count = 0;

            void Walk(SyntaxNode node)
            {
                count++;

                foreach (SyntaxNode child in ChildrenOf(node))
                {
                    if (!node.Span.Contains(child.Span))
                    {
                        violations.Add(
                            $"{node.GetType().Name} spans {Describe(node.Span, text)} "
                            + $"but its child {child.GetType().Name} spans {Describe(child.Span, text)}");
                    }

                    Walk(child);
                }
            }

            Walk(root);
            visited = count;
            return violations;
        }

        /// <summary>
        /// A node's child nodes, found by reflection over its get-only properties.
        /// </summary>
        /// <remarks>
        /// The AST has no children API of its own — every node exposes its parts under names that
        /// say what they are, which is what makes the tree readable — so the generic walk a test
        /// like this needs has to be built here. Reflection is what keeps it honest: it sees a
        /// property added to an existing node just as readily as a whole new node type.
        /// </remarks>
        private static IEnumerable<SyntaxNode> ChildrenOf(SyntaxNode node)
        {
            foreach (PropertyInfo property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                    continue;

                object? value = property.GetValue(node);
                if (value is SyntaxNode child)
                {
                    yield return child;
                    continue;
                }

                // A string is an IEnumerable of characters, and every node has several.
                if (value is IEnumerable sequence && value is not string)
                {
                    foreach (object? item in sequence)
                    {
                        if (item is SyntaxNode listed)
                            yield return listed;
                    }
                }
            }
        }

        private static string Describe(SourceSpan span, string text)
        {
            int start = Math.Max(0, Math.Min(span.Start.Position, text.Length));
            int end = Math.Max(start, Math.Min(span.End, text.Length));

            var builder = new StringBuilder();
            builder.Append('[').Append(span.Start.Position).Append(',').Append(span.End).Append(") '");
            builder.Append(text.Substring(start, end - start).Replace("\r", string.Empty).Replace("\n", "\\n"));
            builder.Append('\'');
            return builder.ToString();
        }
    }
}
