# Surtr Language for Visual Studio Code

Syntax highlighting, hover, go-to-definition and diagnostics for the
[Surtr](https://github.com/anomalyco/opencode) embedded scripting language
(`.surtr` files).

## Features

- **Full syntax highlighting** for the surface language described in
  `docs/Language-Syntax.md` (from the repository root):
  - reserved words, modifiers and contextual keywords
  - built-in type names (`int`, `float`, `bool`, `char`, `string`, `void`, `range`, `unknown`)
  - string interpolation (`"Hello, $name!"`, `"${expr}"`) with `\$` escapes
  - char/string/numeric literals (hex, binary, `_` digit separators)
  - `///` doc comments with `@tag` highlighting
  - type/function/variable/property/operator/const/alias/import declarations
  - variable *uses* (`variable.other.surtr`), including locals, globals, fields,
    member receivers and loop variables
  - parameters (`variable.parameter.surtr`), property names and get/set accessors
  - type names in type positions (`support.type.surtr`): base class and interface
    lists, type annotations, generic constraints, return types, `as`/`as?`/`is`
    targets, and catch bindings — everything except the built-in primitives, which
    stay `storage.type.primitive.surtr`
  - attributes (`@Range(0, 100)`)
  - every operator from the §5.7 precedence table
- **Hover signatures** — type-accurate, cross-file, produced by the real compiler
  binder: fields, locals, parameters, methods (with their full signature), types
  and aliases.
- **Go to definition** — cross-file, resolved from the bound tree.
- **Diagnostics** — every compile error the binder reports, re-published on each
  edit (errors and warnings underlined in the editor).
- **Code snippets** — classes, methods, constructors, properties, enums,
  singletons, value classes, control flow, and more.
- Matching bracket colorization and auto-closing pairs via `language-configuration.json`.

## The language server

The IDE features come from `src/Surtr.LanguageServer` in the repository root, a
C# `net8.0` program speaking LSP over stdio. The extension starts it one of two
ways:

1. **The `surtr.languageServer.path` setting** — point it at `surtr-lsp.exe`
   (Windows) or `surtr-lsp` (Linux/macOS), or at a directory containing it.
   After `dotnet build Surtr.sln`, the executable is at
   `src/Surtr.LanguageServer/bin/Debug/net8.0/surtr-lsp.exe`.
2. **`surtr-lsp` on your PATH** — the default when the setting is empty.

If neither exists, the extension shows a message instead of failing silently.

## Operators and separators

The default themes (Dark+, Dark Modern, …) deliberately give no color to most operator
and punctuation scopes: `keyword.operator` maps to the plain foreground `#D4D4D4`, and
`punctuation.*` scopes have no rule at all. So `;` `:` `,` `.` `{` `}` and the
arithmetic/assignment/logical operators all render in plain text (only `as`/`as?`/`is`
come out blue, via the theme's `keyword.operator.cast`/`.expression` rules).

To give them colors, add an `editor.tokenColorCustomizations` block to your settings
(user or workspace). These rules match the exact scopes the grammar emits and are scoped
to `source.surtr`, so they only affect Surtr files:

```json
"editor.tokenColorCustomizations": {
  "textMateRules": [
    { "scope": "source.surtr keyword.operator.surtr",                   "settings": { "foreground": "#C586C0" } },
    { "scope": "source.surtr keyword.operator.varargs.surtr",           "settings": { "foreground": "#C586C0" } },
    { "scope": "source.surtr keyword.operator.arithmetic.surtr",        "settings": { "foreground": "#C586C0" } },
    { "scope": "source.surtr keyword.operator.comparison.surtr",        "settings": { "foreground": "#C586C0" } },
    { "scope": "source.surtr keyword.operator.logical.surtr",           "settings": { "foreground": "#C586C0" } },
    { "scope": "source.surtr keyword.operator.other.surtr",             "settings": { "foreground": "#C586C0" } },
    { "scope": "source.surtr keyword.operator.arrow.surtr",             "settings": { "foreground": "#C586C0" } },
    { "scope": "source.surtr keyword.operator.assignment.surtr",        "settings": { "foreground": "#C586C0" } },
    { "scope": "source.surtr keyword.operator.cast.surtr",              "settings": { "foreground": "#C586C0" } },
    { "scope": "source.surtr keyword.operator.cast.safe.surtr",         "settings": { "foreground": "#C586C0" } },
    { "scope": "source.surtr keyword.operator.expression.surtr",        "settings": { "foreground": "#C586C0" } },
    { "scope": "source.surtr punctuation.separator.comma.surtr",        "settings": { "foreground": "#9CDCFE" } },
    { "scope": "source.surtr punctuation.separator.colon.surtr",        "settings": { "foreground": "#9CDCFE" } },
    { "scope": "source.surtr punctuation.terminator.statement.surtr",  "settings": { "foreground": "#9CDCFE" } },
    { "scope": "source.surtr punctuation.accessor.dot.surtr",           "settings": { "foreground": "#9CDCFE" } },
    { "scope": "source.surtr punctuation.definition.annotation.surtr", "settings": { "foreground": "#9CDCFE" } },
    { "scope": "source.surtr punctuation.section.block.surtr",         "settings": { "foreground": "#C586C0" } },
    { "scope": "source.surtr punctuation.section.group.surtr",         "settings": { "foreground": "#C586C0" } },
    { "scope": "source.surtr punctuation.section.brackets.surtr",      "settings": { "foreground": "#C586C0" } }
  ]
}
```

Notes:

- The `.surtr` suffix is part of every scope the grammar emits — keep it.
- The `source.surtr` parent scope is what keeps these rules from leaking into other
  languages; the theme matching engine checks it against the token's scope stack.
- The colors reuse the default theme's own palette (keyword purple `#C586C0`,
  punctuation blue `#9CDCFE`); replace them freely.
- Rules must be listed per operator family: the theme already has deeper children
  (`keyword.operator.cast`, `keyword.operator.expression`) and specificity decides,
  not a wildcard.

## Using it

Three ways:

1. **F5 debug** — open this folder (`vscode-surtr`) in VS Code and press `F5` to launch
   an Extension Development Host with the grammar, snippets and language server active.
   This is the fastest loop for iterating on any of them.
2. **Install from source**:
   ```
   npm install
   npm run compile
   npm install -g @vscode/vsce
   vsce package
   code --install-extension surtr-language-0.1.0.vsix
   ```
3. **Manually** — copy the `out/` folder, `syntaxes/`, `snippets/`,
   `language-configuration.json` and `package.json` into your extensions directory
   (`~/.vscode/extensions/`), then restart VS Code.

Remember to build the language server first (`dotnet build Surtr.sln` from the
repository root) and to make `surtr-lsp` reachable per the section above.

## Layout

- `syntaxes/surtr.tmLanguage.json` — the TextMate grammar (`source.surtr`).
- `language-configuration.json` — comments, brackets, auto-closing pairs, indentation.
- `snippets/surtr.code-snippets` — the Surtr snippets.
- `src/extension.ts` — the client: starts `surtr-lsp`, wires hover, definition and
  diagnostics through `vscode-languageclient`.
- `package.json` — the extension manifest binding `surtr` to `.surtr` files and
  declaring the `surtr.languageServer.path` setting.

## Development notes

The grammar mirrors the compiler's own lexer (`src/Surtr.Compiler/Syntax/Lexer.cs`), which
is the authoritative tokenizer. A few deliberate simplifications, matching how TextMate
grammars work rather than how a real parser does:

- Built-in type names are highlighted everywhere, even though §1.1 allows them to be
  shadowed by a user type name — a static highlighter cannot resolve namespaces.
- Type names are highlighted *in type positions*, recognized by grammar rather than
  resolution: an identifier after `:` (annotations, return types, catch bindings), in a
  base/interface list, inside `<…>` constraints, or after `as`/`as?`/`is` is a
  `support.type`. The language server, by contrast, resolves the same word from the
  bound tree — so the highlight and the hover can differ by design.
- Generic nesting is matched up to depth 2 (`Box<Box<int>>`); at depth 3 the outer name
  is still colored but the innermost arguments fall back to the built-in rules. An
  annotation type is only recognized when a `:` precedes it — an explicit `->` return
  after a parameter list is left plain, and a primitive annotation (like `x: int`) keeps
  the built-in color rather than becoming `support.type`.
- String interpolation with nested braces (`"${{ "k": 1 }}"`) colors the nested literal
  at string-level after the first `}`, rather than tracking brace depth.

The lexer's rules that shape the grammar:

- `///` is a doc comment, `////` an ordinary comment.
- A `.` after digits only starts a float if a digit follows it — so `0..10` is `0` `..`
  `10`, and `1.toString()` is member access.
- Floats are made by a decimal point or an exponent, never a suffix.
- Strings never span lines.
