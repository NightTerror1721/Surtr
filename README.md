# Surtr
An embedded scripting language written in C#, with strong static typing, designed to be used in Unity as a modern alternative to languages ​​like Lua.

## Getting started

- `docs/Language-Syntax.md` is the specification; `docs/Compiler-Plan.md` the compiler's plan; `docs/VM-Plan.md`, `docs/Runtime-Model.md`, `docs/Opcodes.md` and `docs/Module-Format.md` cover the runtime, instruction set and module image format.
- Build everything: `dotnet build Surtr.sln`. Run the test suite: `dotnet test Surtr.sln`. Benchmark the VM: `dotnet run --project src/Surtr.Bench -c Release`.

## Tools

- **`surtrc`** — the CLI over the compiler (`src/Surtr.Cli` for `surtrc build`, `src/Surtr.Run` for `surtr run <path> <module.path> <function> ...`).
- **`surtr-lsp`** — a Language Server (`src/Surtr.LanguageServer`, C# net8.0, stdio) exposing the real compiler binder as hover signatures, go-to-definition and diagnostics.
- **`vscode-surtr`** — the VS Code extension wiring that server up, plus a TextMate grammar, snippets and language configuration. See its `README.md` for setup and the `surtr.languageServer.path` setting.

