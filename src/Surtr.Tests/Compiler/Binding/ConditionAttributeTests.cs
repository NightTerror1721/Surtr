#nullable enable

using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Compilation;
using Surtr.Compiler.Diagnostics;
using System.Collections.Generic;
using System.Linq;

namespace Surtr.Tests.Compiler.Binding
{
    /// <summary>
    /// Covers <c>@Condition(expr)</c> (§11) and the <c>defined(Symbol)</c> operator (§7.4): the
    /// operator folds to whether a build constant exists, and a call to a <c>@Condition(false)</c>
    /// declaration is dropped from the bound tree before anything downstream reads it.
    /// </summary>
    public sealed class ConditionAttributeTests
    {
        private const string Root = "D:/proj/src";

        private static (SurtrCompilation Compilation, Binder Binder) Compile(
            IReadOnlyDictionary<string, BuildConstant> constants,
            string source)
        {
            var project = new SurtrProject(Root);
            foreach (var pair in constants)
                project.Define(pair.Key, pair.Value);

            project.AddSourceFile(Root + "/game/core/Test.surtr", source);

            var compilation = SurtrCompilation.Create(project);
            var binder = compilation.Bind();
            binder.BindBodies();
            return (compilation, binder);
        }

        private static (SurtrCompilation, Binder) Compile(string source)
            => Compile(new Dictionary<string, BuildConstant>(), source);

        private static void AssertClean(SurtrCompilation compilation)
        {
            Assert.True(
                !compilation.HasErrors,
                "Unexpected error: " + string.Join("; ", compilation.Diagnostics.Select(d => d.ToString())));
        }

        /// <summary>How many calls to a method of the given name survive in the bound tree.</summary>
        private static int CountCalls(Binder binder, string methodName)
        {
            int count = 0;

            void WalkStatement(BoundStatement statement)
            {
                switch (statement)
                {
                    case BoundBlockStatement block:
                        foreach (var inner in block.Statements)
                            WalkStatement(inner);
                        return;

                    case BoundExpressionStatement expression:
                        WalkExpression(expression.Expression);
                        return;

                    case BoundLocalDeclarationStatement { Initializer: not null } declaration:
                        WalkExpression(declaration.Initializer);
                        return;

                    case BoundReturnStatement { Value: not null } @return:
                        WalkExpression(@return.Value);
                        return;

                    case BoundIfStatement @if:
                        WalkExpression(@if.Condition);
                        WalkStatement(@if.Then);
                        if (@if.Else is not null)
                            WalkStatement(@if.Else);
                        return;

                    case BoundWhileStatement loop:
                        WalkExpression(loop.Condition);
                        WalkStatement(loop.Body);
                        return;

                    case BoundForStatement loop:
                        if (loop.Condition is not null)
                            WalkExpression(loop.Condition);
                        WalkStatement(loop.Body);
                        return;

                    case BoundForInStatement loop:
                        WalkExpression(loop.Sequence);
                        WalkStatement(loop.Body);
                        return;

                    case BoundSwitchStatement @switch:
                        WalkExpression(@switch.Subject);
                        foreach (var section in @switch.Sections)
                            foreach (var inner in section.Statements)
                                WalkStatement(inner);
                        return;

                    case BoundTryStatement @try:
                        WalkStatement(@try.Body);
                        foreach (var clause in @try.Catches)
                            WalkStatement(clause.Body);
                        if (@try.Finally is not null)
                            WalkStatement(@try.Finally);
                        return;
                }
            }

            void WalkExpression(BoundExpression expression)
            {
                switch (expression)
                {
                    case BoundCallExpression call:
                        if (call.Method.Name == methodName)
                            count++;
                        if (call.Receiver is not null)
                            WalkExpression(call.Receiver);
                        foreach (var argument in call.Arguments)
                            WalkExpression(argument);
                        return;

                    case BoundBinaryExpression binary:
                        WalkExpression(binary.Left);
                        WalkExpression(binary.Right);
                        return;

                    case BoundUnaryExpression unary:
                        WalkExpression(unary.Operand);
                        return;

                    case BoundConversionExpression conversion:
                        WalkExpression(conversion.Operand);
                        return;

                    case BoundPropertyExpression property:
                        if (property.Receiver is not null)
                            WalkExpression(property.Receiver);
                        return;
                }
            }

            foreach (var body in binder.Bodies.Values)
                WalkStatement(body);

            return count;
        }

        [Fact]
        public void DefinedOperatorIsFalseForAnAbsentBuildConstant()
        {
            // `defined(MISSING)` must fold without an "undefined name" error, unlike naming it.
            var (compilation, _) = Compile(
                "const if (defined(MISSING)) {\n"
                    + "    let kept: int = 1;\n"
                    + "} else {\n"
                    + "    let kept: int = 2;\n"
                    + "}\n");

            AssertClean(compilation);
        }

        [Fact]
        public void NamingAnAbsentConstantDirectlyIsStillAnError()
        {
            // The soft test exists precisely so this stays a hard error: `defined` is the opt-in.
            var (compilation, _) = Compile("const if (MISSING) { }");
            Assert.True(compilation.HasErrors, "A missing constant named directly should be an error.");
        }

        [Fact]
        public void DefinedOperatorIsTrueForASuppliedBuildConstant()
        {
            var (compilation, _) = Compile(
                new Dictionary<string, BuildConstant> { ["DEBUG"] = BuildConstant.Bool(true) },
                "const if (defined(DEBUG)) {\n"
                    + "    let kept: int = 1;\n"
                    + "} else {\n"
                    + "    let kept: int = 2;\n"
                    + "}\n");

            AssertClean(compilation);
        }

        [Fact]
        public void CallToConditionFalseMethodIsStripped()
        {
            const string source =
                "@Condition(defined(DEBUG))\n"
                    + "fun trace(msg: string) { }\n"
                    + "fun main() {\n"
                    + "    trace(\"hello\");\n"
                    + "}\n";

            var (compilation, binder) = Compile(source);
            AssertClean(compilation);
            Assert.Equal(0, CountCalls(binder, "trace"));
        }

        [Fact]
        public void CallToConditionTrueMethodIsKept()
        {
            const string source =
                "@Condition(defined(DEBUG))\n"
                    + "fun trace(msg: string) { }\n"
                    + "fun main() {\n"
                    + "    trace(\"hello\");\n"
                    + "}\n";

            var (compilation, binder) = Compile(
                new Dictionary<string, BuildConstant> { ["DEBUG"] = BuildConstant.Bool(true) },
                source);
            AssertClean(compilation);
            Assert.Equal(1, CountCalls(binder, "trace"));
        }

        [Fact]
        public void CallToConditionFalseMethodIsStrippedWhenConstantIsFalse()
        {
            // `@Condition(DEBUG)` reads DEBUG's value: present and false => condition false => stripped.
            const string source =
                "@Condition(DEBUG)\n"
                    + "fun trace(msg: string) { }\n"
                    + "fun main() {\n"
                    + "    trace(\"hello\");\n"
                    + "}\n";

            var (compilation, binder) = Compile(
                new Dictionary<string, BuildConstant> { ["DEBUG"] = BuildConstant.Bool(false) },
                source);
            AssertClean(compilation);
            Assert.Equal(0, CountCalls(binder, "trace"));
        }

        [Fact]
        public void BareAbsentConstantInConditionIsAnError()
        {
            // Referencing a constant that the build never supplied is a hard error - the soft test is
            // `defined(...)`, so a typo in a flag name still fails loudly rather than silently stripping.
            const string source =
                "@Condition(DEBUG)\n"
                    + "fun trace(msg: string) { }\n"
                    + "fun main() {\n"
                    + "    trace(\"hello\");\n"
                    + "}\n";

            var (compilation, _) = Compile(source);
            Assert.True(compilation.HasErrors, "An absent constant named directly should be an error.");
        }

        [Fact]
        public void PropertyConditionStripsAccesses()
        {
            const string source =
                "class Logger {\n"
                    + "    @Condition(defined(DEBUG))\n"
                    + "    public Level: int { get; set; }\n"
                    + "}\n"
                    + "fun main() {\n"
                    + "    let l = Logger();\n"
                    + "    l.Level = 3;\n"
                    + "    let x = l.Level;\n"
                    + "}\n";

            var (compilation, binder) = Compile(source);
            AssertClean(compilation);
            Assert.Equal(0, CountPropertyAccesses(binder, "Level"));
        }

        [Fact]
        public void PropertyConditionKeepsAccessesWhenEnabled()
        {
            const string source =
                "class Logger {\n"
                    + "    @Condition(defined(DEBUG))\n"
                    + "    public Level: int { get; set; }\n"
                    + "}\n"
                    + "fun main() {\n"
                    + "    let l = Logger();\n"
                    + "    l.Level = 3;\n"
                    + "    let x = l.Level;\n"
                    + "}\n";

            var (compilation, binder) = Compile(
                new Dictionary<string, BuildConstant> { ["DEBUG"] = BuildConstant.Bool(true) },
                source);
            AssertClean(compilation);
            // One setter and one getter survive.
            Assert.Equal(2, CountPropertyAccesses(binder, "Level"));
        }

        /// <summary>How many property accesses of the given name survive in the bound tree.</summary>
        private static int CountPropertyAccesses(Binder binder, string propertyName)
        {
            int count = 0;

            void WalkStatement(BoundStatement statement)
            {
                switch (statement)
                {
                    case BoundBlockStatement block:
                        foreach (var inner in block.Statements)
                            WalkStatement(inner);
                        return;

                    case BoundExpressionStatement expression:
                        WalkExpression(expression.Expression);
                        return;

                    case BoundLocalDeclarationStatement { Initializer: not null } declaration:
                        WalkExpression(declaration.Initializer);
                        return;

                    case BoundReturnStatement { Value: not null } @return:
                        WalkExpression(@return.Value);
                        return;

                    case BoundIfStatement @if:
                        WalkExpression(@if.Condition);
                        WalkStatement(@if.Then);
                        if (@if.Else is not null)
                            WalkStatement(@if.Else);
                        return;

                    case BoundWhileStatement loop:
                        WalkExpression(loop.Condition);
                        WalkStatement(loop.Body);
                        return;

                    case BoundForStatement loop:
                        if (loop.Condition is not null)
                            WalkExpression(loop.Condition);
                        WalkStatement(loop.Body);
                        return;

                    case BoundForInStatement loop:
                        WalkExpression(loop.Sequence);
                        WalkStatement(loop.Body);
                        return;

                    case BoundSwitchStatement @switch:
                        WalkExpression(@switch.Subject);
                        foreach (var section in @switch.Sections)
                            foreach (var inner in section.Statements)
                                WalkStatement(inner);
                        return;

                    case BoundTryStatement @try:
                        WalkStatement(@try.Body);
                        foreach (var clause in @try.Catches)
                            WalkStatement(clause.Body);
                        if (@try.Finally is not null)
                            WalkStatement(@try.Finally);
                        return;
                }
            }

            void WalkExpression(BoundExpression expression)
            {
                switch (expression)
                {
                    case BoundPropertyExpression property:
                        if (property.Property.Name == propertyName)
                            count++;
                        if (property.Receiver is not null)
                            WalkExpression(property.Receiver);
                        return;

                    case BoundAssignmentExpression assignment:
                        WalkExpression(assignment.Target);
                        WalkExpression(assignment.Value);
                        return;

                    case BoundCallExpression call:
                        if (call.Receiver is not null)
                            WalkExpression(call.Receiver);
                        foreach (var argument in call.Arguments)
                            WalkExpression(argument);
                        return;

                    case BoundBinaryExpression binary:
                        WalkExpression(binary.Left);
                        WalkExpression(binary.Right);
                        return;

                    case BoundUnaryExpression unary:
                        WalkExpression(unary.Operand);
                        return;

                    case BoundConversionExpression conversion:
                        WalkExpression(conversion.Operand);
                        return;
                }
            }

            foreach (var body in binder.Bodies.Values)
                WalkStatement(body);

            return count;
        }
    }
}
