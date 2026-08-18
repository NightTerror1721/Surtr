#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Surtr.Compiler.Binding;
using Surtr.Compiler.Binding.BoundTree;
using Surtr.Compiler.Binding.Symbols;
using Surtr.Compiler.Syntax;
using Surtr.LanguageServer.Protocol;

namespace Surtr.LanguageServer.Workspace
{
    /// <summary>
    /// The <c>textDocument/completion</c> and <c>textDocument/signatureHelp</c> handlers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Completion answers from two contexts, decided by the token before the cursor: a <c>.</c> or
    /// <c>?.</c> asks for the members reachable on the receiver, and anything else asks for whatever
    /// an expression may name. A member completion first looks for the bound expression whose span
    /// ends exactly at the dot — that receiver's type is known exactly, and its instance members are
    /// the candidates. When no bound receiver exists (a type name used as a value, which has no
    /// value node), the text before the dot is resolved as a type name instead, which is what covers
    /// a <c>singleton</c>'s surface, an enum's cases and a static holder.
    /// </para>
    /// <para>
    /// Signature help finds the innermost unmatched <c>(</c> before the cursor, then looks for a
    /// fully bound call, creation or closure-invocation node whose opening paren is that one. The
    /// bound tree is the source of the overloads whenever it exists — the same way overload
    /// resolution would have seen them. While the arguments are still being typed the tree may be
    /// incomplete, so a name-based fallback resolves the callee text directly against the module.
    /// </para>
    /// </remarks>
    public static class CompletionProvider
    {
        // ------------------------------------------------------------------------------------
        // textDocument/completion
        // ------------------------------------------------------------------------------------

        /// <summary>The suggestions at a cursor, in priority order.</summary>
        public static CompletionList Complete(CompilationSnapshot snapshot, string filePath, string text, int position)
        {
            if (snapshot.IsEmpty || snapshot.Binder is null)
                return NoItems();

            var tokens = new Lexer(SurtrSourceBuffer.FromString(text, filePath)).Tokenize();

            Token? last = LastTokenEndingBefore(tokens, position);
            if (last.HasValue && (last.Value.Type == TokenType.Dot || last.Value.Type == TokenType.QuestionDot))
                return CompleteMember(snapshot, filePath, text, position, last.Value);

            return CompleteExpression(snapshot, filePath, text, position);
        }

        private static CompletionList NoItems() => new CompletionList { IsIncomplete = false, Items = new List<CompletionItem>() };

        /// <summary>The last token that ends at or before the cursor, or <see langword="null"/>.</summary>
        private static Token? LastTokenEndingBefore(List<Token> tokens, int position)
        {
            Token? best = null;
            foreach (var token in tokens)
            {
                if (token.Span.End > position)
                    break;
                best = token;
            }

            return best;
        }

        /// <summary>The members of whatever the text before a dot reads as.</summary>
        private static CompletionList CompleteMember(CompilationSnapshot snapshot, string filePath, string text, int position, Token dot)
        {
            var items = new List<CompletionItem>();
            var binder = snapshot.Binder!;

            // A receiver that is a value: its span ends exactly at the dot.
            BoundExpression? receiver = FindReceiverEndingAt(binder, filePath, text.Length, dot.Span.Start.Position);
            if (receiver is not null)
            {
                AddReachableMembers(binder, receiver.Type, includeStatics: false, items);
                return new CompletionList { IsIncomplete = false, Items = items };
            }

            string receiverText = ReceiverText(text, dot.Span.Start.Position);
            if (receiverText.Length == 0)
                return NoItems();

            ModuleSymbol? module = ModuleFor(snapshot, filePath);

            // An in-scope value: the tree may have dropped the half-typed access, so the receiver
            // reads as a name the scope knows. A local shadows a type of the same name.
            var scope = CollectScope(binder, filePath, text.Length, position);
            if (receiverText == "this")
            {
                if (scope.ThisType is not null)
                {
                    AddReachableMembers(binder, scope.ThisType, includeStatics: false, items);
                    return new CompletionList { IsIncomplete = false, Items = items };
                }
            }
            else
            {
                foreach (var symbol in scope.Symbols)
                {
                    if (symbol.Name != receiverText)
                        continue;

                    TypeSymbol? valueType = symbol switch
                    {
                        ParameterSymbol parameter => parameter.Type,
                        LocalSymbol local => local.Type,
                        _ => null,
                    };

                    if (valueType is null)
                        continue;

                    AddReachableMembers(binder, valueType, includeStatics: false, items);
                    return new CompletionList { IsIncomplete = false, Items = items };
                }
            }

            // A module alias (§2.1, Fase 7): its own types, the same way a fully qualified module
            // path's would be if the receiver had been written out in full.
            string? aliasedModulePath = ResolveModuleAlias(snapshot, filePath, receiverText);
            if (aliasedModulePath is not null && binder.Modules.TryGetValue(aliasedModulePath, out ModuleSymbol? aliasedModule))
            {
                foreach (var aliasedType in aliasedModule.Types)
                {
                    if (MemberItem(aliasedType, includeStatics: true) is CompletionItem item)
                        items.Add(item);
                }

                return new CompletionList { IsIncomplete = false, Items = items };
            }

            // A type name: its statics, its nested types, a singleton's instance surface, and an
            // enum's cases.
            NamedTypeSymbol? type = module is null ? null : FindType(binder, snapshot, filePath, module, receiverText);
            if (type is not null)
            {
                AddReachableMembers(binder, type, includeStatics: true, items);
                return new CompletionList { IsIncomplete = false, Items = items };
            }

            return new CompletionList { IsIncomplete = false, Items = items };
        }

        /// <summary>Whatever an expression may name in the module, in the current scope.</summary>
        private static CompletionList CompleteExpression(CompilationSnapshot snapshot, string filePath, string text, int position)
        {
            var items = new List<CompletionItem>();
            var binder = snapshot.Binder!;
            ModuleSymbol? module = ModuleFor(snapshot, filePath);
            if (module is null)
                return NoItems();

            var scope = CollectScope(binder, filePath, text.Length, position);

            // A name may appear once; the module and the scope both answer it.
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(CompletionItem item)
            {
                if (seen.Add(item.Label))
                    items.Add(item);
            }

            foreach (var symbol in scope.Symbols)
                Add(VariableItem(symbol));

            if (scope.ThisType is not null)
            {
                Add(new CompletionItem
                {
                    Label = "this",
                    Kind = CompletionItemKinds.Keyword,
                    Detail = scope.ThisType.ToDisplayString(),
                    SortText = "0this",
                });
            }

            foreach (var method in module.Methods)
            {
                if (method.Role == MethodRole.Normal && !method.IsSynthetic && !method.Name.StartsWith("op_", StringComparison.Ordinal))
                {
                    if (MemberItem(method, includeStatics: true) is CompletionItem item)
                        Add(item);
                }
            }

            foreach (var field in module.Fields)
            {
                if (MemberItem(field, includeStatics: true) is CompletionItem item)
                    Add(item);
            }

            foreach (var property in module.Properties)
            {
                if (MemberItem(property, includeStatics: true) is CompletionItem item)
                    Add(item);
            }

            foreach (var alias in module.Aliases)
                Add(AliasItem(alias));

            foreach (var type in module.Types)
            {
                if (MemberItem(type, includeStatics: true) is CompletionItem item)
                    Add(item);
            }

            // What a wildcard import pulled into this module is in scope too.
            foreach (string path in ImportedModules(snapshot, filePath, module))
            {
                if (!binder.Modules.TryGetValue(path, out ModuleSymbol? foreign))
                    continue;

                foreach (var type in foreign.Types)
                {
                    if (MemberItem(type, includeStatics: true) is CompletionItem item)
                        Add(item);
                }
            }

            // A module alias (§2.1, Fase 7) is not reachable by its own name as a value or a type -
            // only qualified, `Alias.Something` - but the name itself belongs in the list so typing
            // it is discoverable at all. A selective import (`import X.{A, B}`, Fase 8) is the
            // opposite: exactly the listed names, unqualified, the same as a repeated named import.
            var unitForImportSyntax = snapshot.UnitFor(filePath);
            if (unitForImportSyntax is not null)
            {
                foreach (var import in unitForImportSyntax.Syntax.Imports)
                {
                    if (import.Alias is string alias)
                    {
                        Add(new CompletionItem { Label = alias, Kind = CompletionItemKinds.Module, SortText = "3" + alias });
                        continue;
                    }

                    if (import.Members is null)
                        continue;

                    string listedModulePath = string.Join(".", import.Path);
                    if (!binder.Modules.TryGetValue(listedModulePath, out ModuleSymbol? listedModule))
                        continue;

                    foreach (string memberName in import.Members)
                    {
                        foreach (var type in listedModule.FindTypes(memberName))
                        {
                            if (MemberItem(type, includeStatics: true) is CompletionItem item)
                                Add(item);
                        }
                    }
                }
            }

            foreach (var name in binder.GlobalScope.Names)
                Add(new CompletionItem { Label = name, Kind = CompletionItemKinds.Class, SortText = "3" + name });

            foreach (var keyword in Keywords)
                Add(new CompletionItem { Label = keyword, Kind = CompletionItemKinds.Keyword, SortText = "9" + keyword });

            return new CompletionList { IsIncomplete = false, Items = items };
        }

        /// <summary>The identifiers reserved by the language, offered where an expression goes.</summary>
        private static readonly string[] Keywords =
        {
            "abstract", "alias", "as", "break", "case", "catch", "class", "const", "constructor",
            "continue", "default", "else", "enum", "false", "finally", "for", "forceinline", "fun",
            "if", "import", "in", "inline", "interface", "internal", "is", "let", "native", "null",
            "operator", "override", "private", "protected", "public", "range", "return", "sealed",
            "singleton", "static", "super", "switch", "this", "throw", "true", "try", "typeof",
            "unknown", "value", "var", "virtual", "while",
        };

        // ------------------------------------------------------------------------------------
        // textDocument/signatureHelp
        // ------------------------------------------------------------------------------------

        /// <summary>The overloads visible for the call the cursor sits inside, or <see langword="null"/>.</summary>
        public static SignatureHelp? SignatureHelp(CompilationSnapshot snapshot, string filePath, string text, int position)
        {
            if (snapshot.IsEmpty || snapshot.Binder is null)
                return null;

            var binder = snapshot.Binder;
            var tokens = new Lexer(SurtrSourceBuffer.FromString(text, filePath)).Tokenize();

            int? openParen = InnermostOpenParen(tokens, position);
            if (!openParen.HasValue)
                return null;

            int activeParameter = CountTopLevelCommas(tokens, openParen.Value, position);

            var signatures = new List<SignatureInformation>();
            int? activeSignature = null;

            // A fully bound call node is the reliable source of the overloads.
            BoundExpression? target = FindCallNode(binder, filePath, text.Length, tokens, openParen.Value);

            if (target is BoundCallExpression call)
            {
                foreach (var overload in OverloadsOf(binder, call.Method))
                    activeSignature = Add(overload, activeParameter, signatures, activeSignature);
            }
            else if (target is BoundObjectCreationExpression creation)
            {
                if (creation.Constructor is not null)
                {
                    foreach (var overload in OverloadsOf(binder, creation.Constructor))
                        activeSignature = Add(overload, activeParameter, signatures, activeSignature);
                }
            }
            else if (target is BoundClosureInvocationExpression invocation && invocation.Type is ClosureTypeSymbol closure)
            {
                signatures.Add(ClosureSignature(closure));
                activeSignature = 0;
            }
            else
            {
                // The tree is incomplete while the arguments are being typed: resolve by name.
                List<MethodSymbol> overloads = ResolveCallableByName(binder, snapshot, filePath, text.Length, tokens, openParen.Value);
                foreach (var overload in overloads)
                    activeSignature = Add(overload, activeParameter, signatures, activeSignature);
            }

            if (signatures.Count == 0)
                return null;

            int? active = null;
            if (activeSignature.HasValue)
            {
                int paramCount = signatures[activeSignature.Value].Parameters.Count;
                active = paramCount == 0 ? 0 : Math.Min(activeParameter, paramCount - 1);
            }

            return new SignatureHelp
            {
                Signatures = signatures,
                ActiveSignature = activeSignature,
                ActiveParameter = active,
            };
        }

        /// <summary>Adds one overload's signature; returns the index of the first that covers the cursor.</summary>
        private static int? Add(MethodSymbol method, int activeParameter, List<SignatureInformation> signatures, int? activeSignature)
        {
            var info = MethodSignature(method);
            signatures.Add(info.Signature);

            if (!activeSignature.HasValue && info.ParameterCount > activeParameter)
                return signatures.Count - 1;

            return activeSignature;
        }

        /// <summary>The '(' the cursor is still inside, innermost first, or <see langword="null"/>.</summary>
        private static int? InnermostOpenParen(List<Token> tokens, int position)
        {
            var stack = new Stack<int>();
            foreach (var token in tokens)
            {
                if (token.Span.End > position)
                    break;

                if (token.Type == TokenType.LeftParen)
                    stack.Push(token.Span.Start.Position);
                else if (token.Type == TokenType.RightParen && stack.Count > 0)
                    stack.Pop();
            }

            return stack.Count > 0 ? stack.Peek() : (int?)null;
        }

        /// <summary>How many top-level arguments have been fully typed before the cursor.</summary>
        private static int CountTopLevelCommas(List<Token> tokens, int openParen, int position)
        {
            int count = 0;
            int depth = 0;

            foreach (var token in tokens)
            {
                // Skip the '(' itself and everything before it: anything at or before the paren is
                // not inside the argument list.
                if (token.Span.Start.Position <= openParen)
                    continue;
                if (token.Span.Start.Position >= position)
                    break;

                if (token.Type == TokenType.LeftParen || token.Type == TokenType.LeftBracket || token.Type == TokenType.LeftBrace)
                    depth++;
                else if (token.Type == TokenType.RightParen || token.Type == TokenType.RightBracket || token.Type == TokenType.RightBrace)
                {
                    if (depth > 0)
                        depth--;
                }
                else if (token.Type == TokenType.Comma && depth == 0)
                    count++;
            }

            return count;
        }

        /// <summary>The deepest call-like node whose '(' is the one the cursor sits inside.</summary>
        private static BoundExpression? FindCallNode(Binder binder, string filePath, int textLength, List<Token> tokens, int openParen)
        {
            BoundExpression? best = null;
            int bestStart = -1;

            void Consider(BoundExpression expression)
            {
                if (expression is not BoundCallExpression and not BoundObjectCreationExpression and not BoundClosureInvocationExpression)
                    return;

                if (OpenParenOf(tokens, expression.Span) != openParen)
                    return;

                if (expression.Span.Start.Position > bestStart)
                {
                    best = expression;
                    bestStart = expression.Span.Start.Position;
                }
            }

            WalkToAnchor(binder, filePath, textLength, openParen, Consider);
            return best;
        }

        /// <summary>The '(' a call-like node was written with, or -1 when its span has none.</summary>
        private static int OpenParenOf(List<Token> tokens, SourceSpan span)
        {
            foreach (var token in tokens)
            {
                if (token.Span.End <= span.Start.Position)
                    continue;
                if (token.Span.Start.Position >= span.End)
                    break;

                if (token.Type == TokenType.LeftParen)
                    return token.Span.Start.Position;
            }

            return -1;
        }

        /// <summary>Resolves the text before the '(' as a module function, a type being constructed, or a qualified member.</summary>
        private static List<MethodSymbol> ResolveCallableByName(Binder binder, CompilationSnapshot snapshot, string filePath, int textLength, List<Token> tokens, int openParen)
        {
            var module = ModuleFor(snapshot, filePath);
            if (module is null)
                return new List<MethodSymbol>();

            string chain = ChainBefore(tokens, openParen);
            if (chain.Length == 0)
                return new List<MethodSymbol>();

            int lastDot = chain.LastIndexOf('.');
            string name = lastDot < 0 ? chain : chain.Substring(lastDot + 1);
            string prefix = lastDot < 0 ? string.Empty : chain.Substring(0, lastDot);

            if (prefix.Length > 0)
            {
                // A qualified name: a foreign module, that module reached through an alias, or a
                // member of a type in this one.
                if (binder.Modules.TryGetValue(prefix, out ModuleSymbol? foreign))
                    return MethodsNamed(foreign, name);

                string? aliasedModulePath = ResolveModuleAlias(snapshot, filePath, prefix);
                if (aliasedModulePath is not null && binder.Modules.TryGetValue(aliasedModulePath, out ModuleSymbol? aliasedForeign))
                    return MethodsNamed(aliasedForeign, name);

                NamedTypeSymbol? type = FindType(binder, snapshot, filePath, module, prefix);
                if (type is not null)
                    return MethodsNamed(type, name);

                // The tree can drop a half-typed call, so read the prefix as an in-scope value
                // instead of only a type.
                var scope = CollectScope(binder, filePath, textLength, openParen);
                foreach (var symbol in scope.Symbols)
                {
                    if (symbol.Name != prefix)
                        continue;

                    TypeSymbol? valueType = symbol switch
                    {
                        ParameterSymbol parameter => parameter.Type,
                        LocalSymbol local => local.Type,
                        _ => null,
                    };

                    if (valueType is null || valueType is not NamedTypeSymbol named)
                        continue;

                    return MethodsNamed(named, name);
                }

                return new List<MethodSymbol>();
            }

            List<MethodSymbol> moduleMethods = MethodsNamed(module, name);
            if (moduleMethods.Count > 0)
                return moduleMethods;

            NamedTypeSymbol? constructed = FindType(binder, snapshot, filePath, module, name);
            return constructed is not null ? ConstructorsOf(constructed) : new List<MethodSymbol>();
        }

        /// <summary>The name chain immediately before an offset, as <c>module.path.Type.member</c>.</summary>
        private static string ChainBefore(List<Token> tokens, int offset)
        {
            // Read right-to-left from the paren: the callee is the last contiguous run of names
            // and dots. Anything before it (a `let`, a `=`) stops the run.
            var parts = new List<string>();
            bool expectingDot = false;

            for (int i = tokens.Count - 1; i >= 0; i--)
            {
                Token token = tokens[i];
                if (token.Span.Start.Position >= offset)
                    continue;

                if (token.Type == TokenType.Identifier && !expectingDot)
                    parts.Add(token.Lexeme.ToString());
                else if (token.Type == TokenType.Dot)
                    parts.Add(".");
                else
                    break;

                expectingDot = token.Type == TokenType.Identifier;
            }

            parts.Reverse();
            return string.Concat(parts);
        }

        /// <summary>The methods of that name declared on the module, in declaration order.</summary>
        private static List<MethodSymbol> MethodsNamed(ModuleSymbol module, string name)
        {
            var result = new List<MethodSymbol>();
            foreach (var method in module.Methods)
            {
                if (method.Name == name && !method.IsSynthetic)
                    result.Add(method);
            }

            return result;
        }

        /// <summary>The methods of that name declared on the type, in declaration order.</summary>
        private static List<MethodSymbol> MethodsNamed(NamedTypeSymbol type, string name)
        {
            var result = new List<MethodSymbol>();
            foreach (var member in type.Definition.Members)
            {
                if (member is MethodSymbol method && method.Name == name && !method.IsSynthetic && method.Role != MethodRole.StaticInitializer)
                    result.Add(method);
            }

            return result;
        }

        /// <summary>The constructors a type declares, in declaration order.</summary>
        private static List<MethodSymbol> ConstructorsOf(NamedTypeSymbol type)
        {
            var result = new List<MethodSymbol>();
            foreach (var member in type.Definition.Members)
            {
                if (member is MethodSymbol method && method.Role == MethodRole.Constructor && !method.IsSynthetic)
                    result.Add(method);
            }

            return result;
        }

        /// <summary>Every overload sharing the declaration the call resolved to.</summary>
        private static List<MethodSymbol> OverloadsOf(Binder binder, MethodSymbol method)
        {
            var containing = method.OriginalDefinition?.ContainingSymbol ?? method.ContainingSymbol;
            string name = method.OriginalDefinition?.Name ?? method.Name;

            List<MethodSymbol> result = containing switch
            {
                NamedTypeSymbol type => MethodsNamed(type, name),
                ModuleSymbol module => MethodsNamed(module, name),
                _ => new List<MethodSymbol>(),
            };

            if (result.Count == 0)
                result.Add(method);

            return result;
        }

        // ------------------------------------------------------------------------------------
        // Walking the bound tree
        // ------------------------------------------------------------------------------------

        /// <summary>Everything an expression may name at the cursor, gathered along the one path to it.</summary>
        private sealed class ScopeInfo
        {
            public readonly List<Symbol> Symbols = new List<Symbol>();

            public NamedTypeSymbol? ThisType;
        }

        /// <summary>Collects the locals, parameters and <c>this</c> in scope at a position.</summary>
        private static ScopeInfo CollectScope(Binder binder, string filePath, int textLength, int position)
        {
            var info = new ScopeInfo();

            foreach (var pair in binder.Bodies)
            {
                if (!binder.BodyFiles.TryGetValue(pair.Key, out string? bodyFile) || !SameFile(bodyFile, filePath))
                    continue;

                var body = pair.Value;
                if (body.Span.End > textLength || body.Span.Start.Position > position)
                    continue;

                if (pair.Key.ContainingType is NamedTypeSymbol containing && !pair.Key.IsStatic)
                    info.ThisType ??= containing;

                foreach (var parameter in pair.Key.Parameters)
                    info.Symbols.Add(parameter);

                CollectStatement(binder, body, position, info);
            }

            foreach (var initializer in binder.FieldInitializers)
            {
                if (initializer.Value.Span.End > textLength)
                    continue;

                if (initializer.DeclaringType is NamedTypeSymbol type && !initializer.Field.IsStatic)
                    info.ThisType ??= type;

                CollectExpression(binder, initializer.Value, position, info);
            }

            foreach (var block in binder.StaticBlocks)
            {
                if (block.Body.Span.End > textLength)
                    continue;

                CollectStatement(binder, block.Body, position, info);
            }

            foreach (var chain in binder.ConstructorChains.Values)
            {
                foreach (var argument in chain.Arguments)
                    CollectExpression(binder, argument, position, info);
            }

            return info;
        }

        private static void CollectStatement(Binder binder, BoundStatement statement, int position, ScopeInfo info)
        {
            if (statement.Span.Start.Position > position || statement.Span.End < position)
                return;

            switch (statement)
            {
                case BoundBlockStatement block:
                    foreach (var child in block.Statements)
                    {
                        // A declaration that ended before the cursor is in scope from here on; a
                        // statement without a name is not, and its internals are out of scope.
                        if (child.Span.End <= position)
                        {
                            if (child is BoundLocalDeclarationStatement declaration)
                                info.Symbols.Add(declaration.Local);
                            continue;
                        }

                        if (child.Span.Start.Position > position)
                            continue;

                        CollectStatement(binder, child, position, info);
                    }
                    break;

                case BoundLocalDeclarationStatement declaration:
                    if (declaration.Initializer is not null && declaration.Initializer.Span.Contains(position))
                    {
                        // Inside its own initializer a local is not in scope yet.
                        CollectExpression(binder, declaration.Initializer, position, info);
                        break;
                    }

                    info.Symbols.Add(declaration.Local);
                    if (declaration.Initializer is not null)
                        CollectExpression(binder, declaration.Initializer, position, info);
                    break;

                case BoundExpressionStatement expressionStatement:
                    CollectExpression(binder, expressionStatement.Expression, position, info);
                    break;

                case BoundIfStatement ifStatement:
                    CollectExpression(binder, ifStatement.Condition, position, info);
                    CollectStatement(binder, ifStatement.Then, position, info);
                    if (ifStatement.Else is not null)
                        CollectStatement(binder, ifStatement.Else, position, info);
                    break;

                case BoundWhileStatement whileStatement:
                    CollectExpression(binder, whileStatement.Condition, position, info);
                    CollectStatement(binder, whileStatement.Body, position, info);
                    break;

                case BoundForStatement forStatement:
                    if (forStatement.Initializer is not null)
                        CollectStatement(binder, forStatement.Initializer, position, info);
                    if (forStatement.Condition is not null)
                        CollectExpression(binder, forStatement.Condition, position, info);
                    if (forStatement.Step is not null)
                        CollectExpression(binder, forStatement.Step, position, info);
                    CollectStatement(binder, forStatement.Body, position, info);
                    break;

                case BoundForInStatement forIn:
                    if (!forIn.Sequence.Span.Contains(position))
                        info.Symbols.Add(forIn.Variable);
                    CollectExpression(binder, forIn.Sequence, position, info);
                    CollectStatement(binder, forIn.Body, position, info);
                    break;

                case BoundSwitchStatement switchStatement:
                    CollectExpression(binder, switchStatement.Subject, position, info);
                    foreach (var section in switchStatement.Sections)
                    {
                        foreach (var label in section.Labels)
                            CollectExpression(binder, label, position, info);
                        foreach (var child in section.Statements)
                            CollectStatement(binder, child, position, info);
                    }
                    break;

                case BoundTryStatement tryStatement:
                    CollectStatement(binder, tryStatement.Body, position, info);
                    foreach (var clause in tryStatement.Catches)
                    {
                        info.Symbols.Add(clause.Exception);
                        CollectStatement(binder, clause.Body, position, info);
                    }
                    if (tryStatement.Finally is not null)
                        CollectStatement(binder, tryStatement.Finally, position, info);
                    break;

                case BoundThrowStatement throwStatement:
                    CollectExpression(binder, throwStatement.Value, position, info);
                    break;

                case BoundReturnStatement returnStatement:
                    if (returnStatement.Value is not null)
                        CollectExpression(binder, returnStatement.Value, position, info);
                    break;

                case BoundLabeledStatement labeled:
                    CollectStatement(binder, labeled.Statement, position, info);
                    break;
            }
        }

        private static void CollectExpression(Binder binder, BoundExpression expression, int position, ScopeInfo info)
        {
            if (expression.Span.Start.Position > position || expression.Span.End < position)
                return;

            if (expression is BoundLambdaExpression lambda)
            {
                // A lambda's parameters shadow whatever is outside it.
                foreach (var parameter in lambda.Parameters)
                    info.Symbols.Add(parameter);
                CollectStatement(binder, lambda.Body, position, info);
                return;
            }

            foreach (var child in ChildrenOf(expression))
                CollectExpression(binder, child, position, info);
        }

        /// <summary>The bound expression whose span ends exactly at the anchor, choosing the deepest.</summary>
        private static BoundExpression? FindReceiverEndingAt(Binder binder, string filePath, int textLength, int anchorEnd)
        {
            BoundExpression? best = null;
            int bestLength = -1;

            void Consider(BoundExpression expression)
            {
                if (expression.Span.End != anchorEnd)
                    return;

                if (expression.Span.Length > bestLength)
                {
                    best = expression;
                    bestLength = expression.Span.Length;
                }
            }

            WalkToAnchor(binder, filePath, textLength, anchorEnd, Consider);
            return best;
        }

        /// <summary>Walks every tree in the file, visiting each expression once, pruned to the anchor.</summary>
        private static void WalkToAnchor(Binder binder, string filePath, int textLength, int anchorEnd, Action<BoundExpression> consider)
        {
            foreach (var pair in binder.Bodies)
            {
                if (!binder.BodyFiles.TryGetValue(pair.Key, out string? bodyFile) || !SameFile(bodyFile, filePath))
                    continue;

                if (pair.Value.Span.End > textLength)
                    continue;

                WalkStatementToAnchor(pair.Value, anchorEnd, consider);
            }

            foreach (var initializer in binder.FieldInitializers)
            {
                if (initializer.Value.Span.End > textLength)
                    continue;

                WalkExpressionToAnchor(initializer.Value, anchorEnd, consider);
            }

            foreach (var block in binder.StaticBlocks)
            {
                if (block.Body.Span.End > textLength)
                    continue;

                WalkStatementToAnchor(block.Body, anchorEnd, consider);
            }

            foreach (var chain in binder.ConstructorChains.Values)
            {
                foreach (var argument in chain.Arguments)
                    WalkExpressionToAnchor(argument, anchorEnd, consider);
            }
        }

        private static void WalkStatementToAnchor(BoundStatement statement, int anchorEnd, Action<BoundExpression> consider)
        {
            if (statement.Span.End < anchorEnd)
                return;

            switch (statement)
            {
                case BoundBlockStatement block:
                    foreach (var child in block.Statements)
                        WalkStatementToAnchor(child, anchorEnd, consider);
                    break;

                case BoundLocalDeclarationStatement declaration:
                    if (declaration.Initializer is not null)
                        WalkExpressionToAnchor(declaration.Initializer, anchorEnd, consider);
                    break;

                case BoundExpressionStatement expressionStatement:
                    WalkExpressionToAnchor(expressionStatement.Expression, anchorEnd, consider);
                    break;

                case BoundIfStatement ifStatement:
                    WalkExpressionToAnchor(ifStatement.Condition, anchorEnd, consider);
                    WalkStatementToAnchor(ifStatement.Then, anchorEnd, consider);
                    if (ifStatement.Else is not null)
                        WalkStatementToAnchor(ifStatement.Else, anchorEnd, consider);
                    break;

                case BoundWhileStatement whileStatement:
                    WalkExpressionToAnchor(whileStatement.Condition, anchorEnd, consider);
                    WalkStatementToAnchor(whileStatement.Body, anchorEnd, consider);
                    break;

                case BoundForStatement forStatement:
                    if (forStatement.Initializer is not null)
                        WalkStatementToAnchor(forStatement.Initializer, anchorEnd, consider);
                    if (forStatement.Condition is not null)
                        WalkExpressionToAnchor(forStatement.Condition, anchorEnd, consider);
                    if (forStatement.Step is not null)
                        WalkExpressionToAnchor(forStatement.Step, anchorEnd, consider);
                    WalkStatementToAnchor(forStatement.Body, anchorEnd, consider);
                    break;

                case BoundForInStatement forIn:
                    WalkExpressionToAnchor(forIn.Sequence, anchorEnd, consider);
                    WalkStatementToAnchor(forIn.Body, anchorEnd, consider);
                    break;

                case BoundSwitchStatement switchStatement:
                    WalkExpressionToAnchor(switchStatement.Subject, anchorEnd, consider);
                    foreach (var section in switchStatement.Sections)
                    {
                        foreach (var label in section.Labels)
                            WalkExpressionToAnchor(label, anchorEnd, consider);
                        foreach (var child in section.Statements)
                            WalkStatementToAnchor(child, anchorEnd, consider);
                    }
                    break;

                case BoundTryStatement tryStatement:
                    WalkStatementToAnchor(tryStatement.Body, anchorEnd, consider);
                    foreach (var clause in tryStatement.Catches)
                        WalkStatementToAnchor(clause.Body, anchorEnd, consider);
                    if (tryStatement.Finally is not null)
                        WalkStatementToAnchor(tryStatement.Finally, anchorEnd, consider);
                    break;

                case BoundThrowStatement throwStatement:
                    WalkExpressionToAnchor(throwStatement.Value, anchorEnd, consider);
                    break;

                case BoundReturnStatement returnStatement:
                    if (returnStatement.Value is not null)
                        WalkExpressionToAnchor(returnStatement.Value, anchorEnd, consider);
                    break;

                case BoundLabeledStatement labeled:
                    WalkStatementToAnchor(labeled.Statement, anchorEnd, consider);
                    break;
            }
        }

        private static void WalkExpressionToAnchor(BoundExpression expression, int anchorEnd, Action<BoundExpression> consider)
        {
            if (expression.Span.End < anchorEnd)
                return;

            consider(expression);

            foreach (var child in ChildrenOf(expression))
                WalkExpressionToAnchor(child, anchorEnd, consider);
        }

        /// <summary>The immediate sub-expressions of a node, for a full walk.</summary>
        private static IEnumerable<BoundExpression> ChildrenOf(BoundExpression expression)
        {
            switch (expression)
            {
                case BoundCallExpression call:
                    if (call.Receiver is not null)
                        yield return call.Receiver;
                    foreach (var argument in call.Arguments)
                        yield return argument;
                    break;

                case BoundNullConditionalExpression conditional:
                    yield return conditional.Receiver;
                    yield return conditional.Access;
                    break;

                case BoundClosureInvocationExpression invocation:
                    yield return invocation.Callee;
                    foreach (var argument in invocation.Arguments)
                        yield return argument;
                    break;

                case BoundObjectCreationExpression creation:
                    foreach (var argument in creation.Arguments)
                        yield return argument;
                    break;

                case BoundCollectionCreationExpression collection:
                    if (collection.Capacity is not null)
                        yield return collection.Capacity;
                    if (collection.Source is not null)
                        yield return collection.Source;
                    if (collection.Source2 is not null)
                        yield return collection.Source2;
                    if (collection.DefaultValue is not null)
                        yield return collection.DefaultValue;
                    if (collection.Thrown is not null)
                        yield return collection.Thrown;
                    break;

                case BoundBinaryExpression binary:
                    yield return binary.Left;
                    yield return binary.Right;
                    break;

                case BoundUnaryExpression unary:
                    yield return unary.Operand;
                    break;

                case BoundAssignmentExpression assignment:
                    yield return assignment.Target;
                    yield return assignment.Value;
                    break;

                case BoundConditionalExpression conditionalExpression:
                    yield return conditionalExpression.Condition;
                    yield return conditionalExpression.WhenTrue;
                    yield return conditionalExpression.WhenFalse;
                    break;

                case BoundIndexExpression index:
                    yield return index.Target;
                    yield return index.Index;
                    break;

                case BoundNullAssertExpression assertion:
                    yield return assertion.Operand;
                    if (assertion.Thrown is not null)
                        yield return assertion.Thrown;
                    break;

                case BoundConversionExpression conversion:
                    yield return conversion.Operand;
                    break;

                case BoundTypeTestExpression typeTest:
                    yield return typeTest.Operand;
                    break;

                case BoundTypeOfExpression typeOf:
                    if (typeOf.Operand is not null)
                        yield return typeOf.Operand;
                    break;

                case BoundArrayLiteralExpression array:
                    foreach (var element in array.Elements)
                        yield return element;
                    break;

                case BoundTupleLiteralExpression tuple:
                    foreach (var element in tuple.Elements)
                        yield return element;
                    break;

                case BoundDictLiteralExpression dict:
                    foreach (var entry in dict.Entries)
                    {
                        yield return entry.Key;
                        yield return entry.Value;
                    }
                    break;

                case BoundInterpolatedStringExpression interpolated:
                    foreach (var part in interpolated.Parts)
                        yield return part;
                    break;

                case BoundSwitchExpression switchExpression:
                    foreach (var arm in switchExpression.Arms)
                    {
                        foreach (var value in arm.Values)
                            yield return value;
                        yield return arm.Result;
                    }
                    break;

                case BoundFieldExpression field:
                    if (field.Receiver is not null)
                        yield return field.Receiver;
                    break;

                case BoundPropertyExpression property:
                    if (property.Receiver is not null)
                        yield return property.Receiver;
                    break;
            }
        }

        // ------------------------------------------------------------------------------------
        // Rendering
        // ------------------------------------------------------------------------------------

        /// <summary>Adds every member reachable on a receiver, as the suggestions for one dot.</summary>
        /// <remarks>
        /// <para>
        /// Two things have to be squeezed out here, and neither is visible from one member alone.
        /// </para>
        /// <para>
        /// A property's <c>get_x</c>/<c>set_x</c> accessors are the property over again under a name
        /// source cannot write. A source-declared one says so in its <see cref="MethodRole"/>, but an
        /// imported one cannot: the runtime models three roles only, so metadata brings an accessor
        /// back as an ordinary method and the property it belongs to is the sole thing identifying
        /// it. Matching the names a property implies covers both, which is what keeps a built-in
        /// like <c>array</c> reading the same as a class declared in source.
        /// </para>
        /// <para>
        /// And a member a class shares with an interface it implements is reached twice, once down
        /// each path. A name alone cannot tell that from a genuine overload, so the parameters are
        /// part of the key — <c>push(T)</c> and <c>push(T, int)</c> are two suggestions, while the
        /// two paths to one <c>iterate()</c> are one.
        /// </para>
        /// </remarks>
        private static void AddReachableMembers(Binder binder, TypeSymbol receiver, bool includeStatics, List<CompletionItem> items)
        {
            var members = new List<Symbol>(binder.MemberLookup.Reachable(receiver));

            var accessorNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (Symbol member in members)
            {
                if (member is PropertySymbol property)
                {
                    accessorNames.Add(MemberNames.Getter(property.Name));
                    accessorNames.Add(MemberNames.Setter(property.Name));
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Symbol member in members)
            {
                if (member is MethodSymbol accessor && accessorNames.Contains(accessor.Name))
                    continue;

                if (MemberItem(member, includeStatics) is not CompletionItem item)
                    continue;

                if (seen.Add(MemberKey(member)))
                    items.Add(item);
            }
        }

        /// <summary>What makes two suggestions the same member: the name, plus a method's parameters.</summary>
        private static string MemberKey(Symbol member)
        {
            if (member is not MethodSymbol method)
                return member.Name;

            var builder = new StringBuilder(method.Name);
            builder.Append('(');
            for (int i = 0; i < method.Parameters.Count; i++)
            {
                if (i > 0)
                    builder.Append(',');
                builder.Append(method.Parameters[i].Type.ToDisplayString());
            }

            builder.Append(')');
            return builder.ToString();
        }

        /// <summary>The completion item for one reachable member, or <see langword="null"/> to skip it.</summary>
        private static CompletionItem? MemberItem(Symbol member, bool includeStatics)
        {
            switch (member)
            {
                case MethodSymbol method:
                    if (method.IsSynthetic || method.Role == MethodRole.StaticInitializer || method.Role == MethodRole.Constructor)
                        return null;

                    // A property is offered under its own name, so its accessors would be the same
                    // member listed a second and third time in a spelling source cannot write.
                    // They are not synthetic — `SurtrTypeLinker` matches a property on `get_x`/
                    // `set_x`, so marking them so would hide them from the layer that needs them —
                    // which is why the role, not the name, is what rules them out here.
                    if (method.Role == MethodRole.PropertyGetter || method.Role == MethodRole.PropertySetter)
                        return null;
                    if (!includeStatics && method.IsStatic)
                        return null;
                    if (method.Name.StartsWith("op_", StringComparison.Ordinal))
                        return null;

                    return new CompletionItem
                    {
                        Label = method.Name,
                        Kind = CompletionItemKinds.Method,
                        Detail = HoverFormatter.MethodHeading(method),
                        Documentation = Markup(method),
                        SortText = "1" + method.Name,
                    };

                case FieldSymbol field:
                    if (field.IsSynthetic)
                        return null;
                    if (!includeStatics && field.IsStatic)
                        return null;

                    return new CompletionItem
                    {
                        Label = field.Name,
                        Kind = CompletionItemKinds.Field,
                        Detail = field.Name + " : " + field.Type.ToDisplayString(),
                        Documentation = Markup(field),
                        SortText = "1" + field.Name,
                    };

                case PropertySymbol property:
                    if (!includeStatics && property.IsStatic)
                        return null;

                    return new CompletionItem
                    {
                        Label = property.Name,
                        Kind = CompletionItemKinds.Property,
                        Detail = property.Name + " : " + property.Type.ToDisplayString(),
                        Documentation = Markup(property),
                        SortText = "1" + property.Name,
                    };

                case NamedTypeSymbol type:
                    if (!includeStatics)
                        return null;

                    return new CompletionItem
                    {
                        Label = type.Name,
                        Kind = TypeKindItem(type),
                        Detail = type.ToDisplayString(),
                        Documentation = Markup(type),
                        SortText = "2" + type.Name,
                    };

                default:
                    return null;
            }
        }

        private static CompletionItem VariableItem(Symbol symbol)
        {
            string type = symbol switch
            {
                ParameterSymbol parameter => parameter.Type.ToDisplayString(),
                LocalSymbol local => local.Type.ToDisplayString(),
                _ => string.Empty,
            };

            return new CompletionItem
            {
                Label = symbol.Name,
                Kind = CompletionItemKinds.Variable,
                Detail = symbol.Name + " : " + type,
                SortText = "0" + symbol.Name,
            };
        }

        private static CompletionItem AliasItem(AliasSymbol alias)
        {
            string target = alias.Target is not null ? alias.Target.ToDisplayString() : "?";
            return new CompletionItem
            {
                Label = alias.Name,
                Kind = CompletionItemKinds.Text,
                Detail = "alias " + alias.Name + " = " + target,
                Documentation = Markup(alias),
                SortText = "2" + alias.Name,
            };
        }

        private static int TypeKindItem(NamedTypeSymbol type) => type.TypeKind switch
        {
            TypeSymbolKind.Interface => CompletionItemKinds.Interface,
            TypeSymbolKind.Enum => CompletionItemKinds.Enum,
            _ => CompletionItemKinds.Class,
        };

        /// <summary>One overload as the client renders a signature.</summary>
        private static (SignatureInformation Signature, int ParameterCount) MethodSignature(MethodSymbol method)
        {
            var signature = new SignatureInformation
            {
                Label = HoverFormatter.MethodHeading(method),
                Documentation = Markup(method),
            };

            for (int i = 0; i < method.Parameters.Count; i++)
            {
                signature.Parameters.Add(new ParameterInformation
                {
                    Label = HoverFormatter.ParameterText(method, i),
                });
            }

            return (signature, method.Parameters.Count);
        }

        private static SignatureInformation ClosureSignature(ClosureTypeSymbol closure)
        {
            var signature = new SignatureInformation { Label = ClosureHeading(closure) };
            for (int i = 0; i < closure.ParameterTypes.Count; i++)
            {
                signature.Parameters.Add(new ParameterInformation
                {
                    Label = "arg" + i + " : " + closure.ParameterTypes[i].ToDisplayString(),
                });
            }

            return signature;
        }

        private static string ClosureHeading(ClosureTypeSymbol closure)
        {
            var builder = new StringBuilder();
            builder.Append('(');
            for (int i = 0; i < closure.ParameterTypes.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");
                builder.Append("arg").Append(i).Append(": ").Append(closure.ParameterTypes[i].ToDisplayString());
            }

            builder.Append("): ").Append(closure.ReturnType.ToDisplayString());
            return builder.ToString();
        }

        private static MarkupContent Markup(Symbol symbol)
            => new MarkupContent { Kind = "markdown", Value = HoverFormatter.FormatSymbol(symbol) };

        // ------------------------------------------------------------------------------------
        // Small helpers
        // ------------------------------------------------------------------------------------

        /// <summary>The module a file belongs to, derived the same way the compiler derives one.</summary>
        private static ModuleSymbol? ModuleFor(CompilationSnapshot snapshot, string filePath)
        {
            var unit = snapshot.UnitFor(filePath);
            if (unit is null)
                return null;

            return snapshot.Binder!.Modules.TryGetValue(unit.ModulePath, out ModuleSymbol? module) ? module : null;
        }

        /// <summary>The type a receiver's text names, in this module and its wildcard imports.</summary>
        private static NamedTypeSymbol? FindType(Binder binder, CompilationSnapshot snapshot, string filePath, ModuleSymbol module, string text)
        {
            int lastDot = text.LastIndexOf('.');
            string name = lastDot < 0 ? text : text.Substring(lastDot + 1);

            var types = module.FindTypes(name);
            if (types.Count > 0)
                return types[0];

            foreach (string path in ImportedModules(snapshot, filePath, module))
            {
                if (!binder.Modules.TryGetValue(path, out ModuleSymbol? foreign))
                    continue;

                types = foreign.FindTypes(name);
                if (types.Count > 0)
                    return types[0];
            }

            // A single-name import (`import X.Y;`) or a selective list (`import X.{Y, Z}`, Fase 8)
            // brings its own name(s) into unqualified scope too, the same way a wildcard's types
            // do above - neither is stored as a `Symbol` anywhere this pass could ask a module for
            // it back, so the file's own import syntax is read directly, same as an alias is.
            var unit = snapshot.UnitFor(filePath);
            if (unit is not null)
            {
                foreach (var import in unit.Syntax.Imports)
                {
                    if (import.IsWildcard || import.Alias is not null)
                        continue;

                    if (import.Members is not null)
                    {
                        if (!import.Members.Contains(name) || !binder.Modules.TryGetValue(string.Join(".", import.Path), out ModuleSymbol? listedModule))
                            continue;

                        types = listedModule.FindTypes(name);
                        if (types.Count > 0)
                            return types[0];

                        continue;
                    }

                    if (import.Path.Count == 0 || import.Path[import.Path.Count - 1] != name)
                        continue;

                    string namedModulePath = string.Join(".", import.Path.Take(import.Path.Count - 1));
                    if (!binder.Modules.TryGetValue(namedModulePath, out ModuleSymbol? namedModule))
                        continue;

                    types = namedModule.FindTypes(name);
                    if (types.Count > 0)
                        return types[0];
                }
            }

            return null;
        }

        /// <summary>
        /// The module path an <c>import X as Name;</c> line in this file names, or
        /// <see langword="null"/> if <paramref name="name"/> is not an alias here.
        /// </summary>
        /// <remarks>
        /// Reads the file's own <see cref="ImportSyntax"/> rather than the binder's compiled
        /// symbols: a module alias (§2.1, Fase 7) resolves through <c>Scope</c> at bind time and
        /// is not itself stored as a <see cref="Symbol"/> anywhere a completion pass could ask a
        /// module for it back.
        /// </remarks>
        private static string? ResolveModuleAlias(CompilationSnapshot snapshot, string filePath, string name)
        {
            var unit = snapshot.UnitFor(filePath);
            if (unit is null)
                return null;

            foreach (var import in unit.Syntax.Imports)
            {
                if (import.Alias == name)
                    return string.Join(".", import.Path);
            }

            return null;
        }

        /// <summary>The modules a file's wildcard imports name, other than its own.</summary>
        private static List<string> ImportedModules(CompilationSnapshot snapshot, string filePath, ModuleSymbol current)
        {
            var result = new List<string>();
            var unit = snapshot.UnitFor(filePath);
            if (unit is null)
                return result;

            var binder = snapshot.Binder!;

            foreach (var import in unit.Syntax.Imports)
            {
                if (!import.IsWildcard)
                    continue;

                string path = string.Join(".", import.Path);
                if (path != current.Path && binder.Modules.ContainsKey(path))
                    result.Add(path);

                // A directory wildcard (§2.1, Fase 9) also reaches every submodule nested under
                // it - the exact module may not even exist on its own, only its submodules do.
                string dotted = path + ".";
                foreach (string candidate in binder.Modules.Keys)
                {
                    if (candidate != current.Path && candidate.StartsWith(dotted, StringComparison.Ordinal))
                        result.Add(candidate);
                }
            }

            return result;
        }

        /// <summary>The identifier chain that ends at an offset, ignoring trailing whitespace.</summary>
        private static string ReceiverText(string text, int offset)
        {
            int end = offset;
            int i = offset - 1;
            while (i >= 0 && char.IsWhiteSpace(text[i]))
                i--;

            int nameEnd = i + 1;
            while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.' || text[i] == '`'))
                i--;

            return text.Substring(i + 1, nameEnd - i - 1);
        }

        private static bool SameFile(string a, string b)
            => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
