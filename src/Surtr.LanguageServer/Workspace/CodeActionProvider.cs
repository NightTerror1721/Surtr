#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax.Ast;
using Surtr.LanguageServer.Protocol;

namespace Surtr.LanguageServer.Workspace
{
    /// <summary>
    /// The <c>textDocument/codeAction</c> handler. Today this offers exactly one fix: implementing
    /// every interface member and inherited <c>abstract</c> member a class does not yet answer for
    /// (<see cref="Binder.MissingAbstractMembers"/>), the same obligation
    /// <c>SurtrDiagnosticCode.MissingImplementation</c> already reports as an error.
    /// </summary>
    /// <remarks>
    /// Deliberately built on <see cref="Binder.MissingAbstractMembers"/> rather than re-deriving the
    /// substitution-aware interface walk here — that walk is exactly what two real bugs
    /// (<c>5cca11a</c>/<c>0bef8a2</c>) got wrong before, and this provider has no reason to risk the
    /// same mistake a second time in a different assembly.
    /// </remarks>
    public static class CodeActionProvider
    {
        /// <summary>The fixes available at a range, in priority order.</summary>
        public static List<CodeAction> Complete(CompilationSnapshot snapshot, string filePath, string text, int rangeStart)
        {
            var actions = new List<CodeAction>();
            if (snapshot.IsEmpty || snapshot.Binder is null || snapshot.Compilation is null)
                return actions;

            var unit = snapshot.UnitFor(filePath);
            if (unit is null)
                return actions;

            var chain = FindEnclosingTypeChain(unit.Syntax, rangeStart);
            if (chain.Count == 0)
                return actions;

            NamedTypeSymbol? type = ResolveType(snapshot.Binder, unit.ModulePath, chain);
            if (type is null)
                return actions;

            var missing = snapshot.Binder.MissingAbstractMembers(type);
            if (missing.Count == 0)
                return actions;

            TypeDeclarationSyntax innermost = chain[chain.Count - 1];
            var lines = TextLines.Index(text);

            // The closing '}' is the declaration's own last character - a node's span runs from its
            // first token to its last (CLAUDE.md), and a type declaration's last token is always the
            // brace that closes its body.
            int insertAt = innermost.Span.End - 1;
            string memberIndent = IndentOf(text, innermost.Span.Start.Position) + "    ";

            var builder = new StringBuilder();
            builder.Append('\n');
            foreach (var member in missing)
                builder.Append(StubText(member, memberIndent));

            var start = lines.PositionAt(insertAt);
            var range = new Range(new Position(start.Line, start.Character), new Position(start.Line, start.Character));

            string title = missing.Count == 1
                ? $"Implement missing member '{missing[0].Required.Name}'"
                : $"Implement {missing.Count} missing members";

            actions.Add(new CodeAction
            {
                Title = title,
                Kind = CodeActionKinds.QuickFix,
                Edit = new WorkspaceEdit
                {
                    Changes = new Dictionary<string, List<TextEdit>>
                    {
                        [Surtr.LanguageServer.Workspace.Workspace.UriFromPath(filePath)] =
                            new List<TextEdit> { new TextEdit(range, builder.ToString()) },
                    },
                },
            });

            return actions;
        }

        /// <summary>
        /// One stub per missing member: <c>public</c>, <c>override</c> only when the obligation comes
        /// from a base class (never for an interface — §3.3, satisfying one never requires it), the
        /// substituted signature, and a body that throws rather than silently returning a default -
        /// the point is to be impossible to miss until it is actually implemented.
        /// </summary>
        private static string StubText(Binder.MissingMember member, string indent)
        {
            MethodSymbol required = member.Required;

            var builder = new StringBuilder();
            builder.Append(indent).Append("public ");
            if (!member.FromInterface)
                builder.Append("override ");

            builder.Append("fun ").Append(required.Name);
            if (required.TypeParameters.Count > 0)
                builder.Append('<').Append(string.Join(", ", required.TypeParameters.Select(p => p.Name))).Append('>');

            builder.Append('(');
            for (int i = 0; i < required.Parameters.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                ParameterSymbol parameter = required.Parameters[i];
                if (parameter.IsVararg)
                    builder.Append("...");
                builder.Append(parameter.Name).Append(" : ").Append(parameter.Type.ToDisplayString());
            }

            builder.Append(')');

            // §1.1: the return type is never omitted, void included, so a source-shaped stub always
            // writes it out explicitly.
            builder.Append(": ").Append(required.ReturnType.ToDisplayString());

            builder.Append(" {\n")
                .Append(indent).Append("    throw InvalidOperationException(\"Not implemented\");\n")
                .Append(indent).Append("}\n");

            return builder.ToString();
        }

        /// <summary>The leading whitespace of the line a position starts on.</summary>
        private static string IndentOf(string text, int position)
        {
            int lineStart = position;
            while (lineStart > 0 && text[lineStart - 1] != '\n')
                lineStart--;

            int i = lineStart;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
                i++;

            return text.Substring(lineStart, i - lineStart);
        }

        /// <summary>
        /// Every <see cref="TypeDeclarationSyntax"/> nested from the module's top level down to the
        /// innermost one containing <paramref name="position"/>, or empty when none does.
        /// </summary>
        private static List<TypeDeclarationSyntax> FindEnclosingTypeChain(CompilationUnitSyntax unit, int position)
        {
            var chain = new List<TypeDeclarationSyntax>();
            IReadOnlyList<DeclarationSyntax> level = unit.Declarations;

            while (true)
            {
                TypeDeclarationSyntax? next = null;
                foreach (var declaration in level)
                {
                    if (declaration is TypeDeclarationSyntax type && type.Span.Contains(position))
                    {
                        next = type;
                        break;
                    }
                }

                if (next is null)
                    break;

                chain.Add(next);
                level = next.Members;
            }

            return chain;
        }

        /// <summary>Resolves a syntax nesting chain to the symbol it bound to.</summary>
        private static NamedTypeSymbol? ResolveType(Binder binder, string modulePath, List<TypeDeclarationSyntax> chain)
        {
            if (!binder.Modules.TryGetValue(modulePath, out ModuleSymbol? module))
                return null;

            NamedTypeSymbol? current = null;
            foreach (var syntax in chain)
            {
                IReadOnlyList<NamedTypeSymbol> candidates = current is null
                    ? module.FindTypes(syntax.Name)
                    : current.FindNestedTypes(syntax.Name);

                current = candidates.FirstOrDefault(t => t.TypeParameters.Count == syntax.TypeParameters.Count);
                if (current is null)
                    return null;
            }

            return current;
        }
    }
}
