#nullable enable

using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Diagnostics;
using Surtr.Compiler.Syntax.Ast;
using System;
using System.Collections.Generic;

namespace Surtr.Compiler.Binding
{
    /// <summary>
    /// Reports a declaration carrying test-family marks (§11) that cannot all be true of it at
    /// once — <c>@TestIgnore</c> with nothing to skip, and the other combinations the runner would
    /// have to pick between.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The marks themselves are metadata a host reads by reflection, so nothing in the compiler
    /// has to understand them — which is exactly why a wrong combination is otherwise silent: the
    /// runner simply never discovers the method, and the mark reads as if it had worked. These
    /// checks are what make the vocabulary say something at compile time.
    /// </para>
    /// <para>
    /// Runs against the <em>written</em> attributes rather than the recorded uses, so a report
    /// lands on the mark that caused it rather than on the whole declaration; the conditions are
    /// read from the recorded uses, which is what <see cref="BuiltInAttributes"/> already answers.
    /// That means it has to run after <c>BindAttributes</c> — before it, a target carries no marks
    /// at all. Everything reported here is a warning: each combination compiles, and each is a
    /// mistake about what the host will do rather than about what the code means.
    /// </para>
    /// </remarks>
    internal static class AttributeRoleCheck
    {
        /// <summary>
        /// Checks one declaration's written marks, reporting each combination that cannot hold.
        /// </summary>
        /// <param name="target">The declaration the marks were written on, with its uses recorded.</param>
        /// <param name="written">The attributes as source wrote them, for their spans.</param>
        /// <param name="sourceName">The file the marks were written in.</param>
        /// <param name="diagnostics">Where the warnings go.</param>
        internal static void Verify(
            Symbol target,
            IReadOnlyList<AttributeSyntax> written,
            string sourceName,
            SurtrDiagnosticBag diagnostics)
        {
            if (target is not MethodSymbol method)
                return;

            for (int i = 0; i < written.Count; i++)
            {
                var attribute = written[i];

                if (Is(attribute, BuiltInAttributes.TestIgnore)
                    && BuiltInAttributes.IsTestIgnored(method)
                    && !BuiltInAttributes.IsMarkedTest(method))
                {
                    diagnostics.ReportWarning(
                        SurtrDiagnosticCode.IgnoreWithoutTest,
                        $"'{method.Name}' carries '@TestIgnore' but not '@Test', so there is nothing to skip; the runner never discovers it.",
                        sourceName,
                        attribute.Span);
                }
            }
        }

        /// <summary>Whether a written mark names the given built-in, by the §11 name rule.</summary>
        private static bool Is(AttributeSyntax attribute, string name)
            => string.Equals(attribute.Name, name, StringComparison.Ordinal);
    }
}
