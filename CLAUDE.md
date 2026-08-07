# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What Surtr is

Surtr is an embedded scripting language, written in C#, designed to be used inside Unity as a modern alternative to languages like Lua. It is strongly and statically typed.

Core design goals that shape most architectural decisions in this repo:

- **Own virtual machine.** Surtr compiles to its own opcodes and executes them on a custom VM, which runs alongside (and interoperates with) the standard C#/CLR runtime.
- **Unmanaged core.** The VM's core is intended to be unmanaged, making heavy use of `unsafe` C# wherever feasible, rather than relying on ordinary managed objects and GC-tracked memory.
- **Managed/unmanaged object registry.** Because the core is unmanaged but still needs to reference managed (CLR) objects, the project will need a registry that indexes managed objects, hands them an internal id usable from unmanaged code, and de-indexes them again — effectively a small, purpose-built garbage collector for that boundary.
- **Long-running project.** This is being built incrementally over a long timeframe; expect the codebase to grow substantially beyond the current skeleton.

## The Surtr language model

Facts about the language itself (not just the implementation) that drive most of the type/metadata design:

- **Everything is an object.** Every value conceptually has its own `SurtrClass`, including primitives. `SurtrValue` / `SurtrRawValue` exist purely as a VM-level optimization so the interpreter can move primitives around without allocating or going through class metadata — they are a fast path, not a separate "non-object" tier in the language semantics.
- **Modules are the only top-level container.** A module can contain fields, properties, methods, classes and enums. A class can in turn contain fields, properties, methods and nested classes/enums.
- **There are no real globals in Surtr code.** "Global" only ever means module-level. The single exception is host-defined native variables and functions, which *are* genuinely global and can never be declared from Surtr source — only by the embedding host.
- **Strongly and statically typed**, so every member signature is fully known at compile time and type references are resolved from metadata rather than discovered at runtime.

### Type references are descriptor strings

Member signatures refer to types through `SurtrClassReference`, which wraps a compact **descriptor string** — not a `SurtrClass` instance (that would make class construction circular and order-dependent) and not a `SurtrRef` (those only exist after an entity is registered, which happens after construction).

The encoding is a JVM/CLR-style descriptor rather than a dotted C#-style full name, and the deciding reason is that composite types nest: `Array`, `Dictionary`, `Tuple` and `Closure` are all parameterized. Descriptors nest unambiguously and parse in one left-to-right pass with a single character of lookahead, where `Array<Dictionary<int, string>>` would need a real bracket-and-comma parser.

```
I F B C S                 integer, float, boolean, character, string
A<elem>                   array            AI            -> int[]
D<key><value>             dictionary       DIS           -> {int: string}
T(<elem>...)              tuple            T(IF)         -> (int, float)
L(<param>...)<ret>        closure          L(II)F        -> (int, int) -> float
O<fullname>;              Surtr class      Ogame.core:Entity.Handle;
N<fullname>;              native type      NUnityEngine:GameObject;
fullname := modulePath ':' typeName ('.' nestedTypeName)*
```

The `:` separating module path from type path is deliberate: the resolver splits it in O(1) instead of probing prefixes to find where the module ends. Descriptors are the canonical form for comparison, hashing and bytecode; `ToDisplayString()` exists purely for diagnostics — never key off it.

## Commands

Build the whole solution:

```
dotnet build Surtr.sln
```

Build just the core library:

```
dotnet build src/Surtr.Core/Surtr.Core.csproj
```

There is no test project, lint config, or CI yet — add commands here once those exist rather than assuming standard `dotnet test`/`dotnet format` invocations apply.

## Architecture / structure

- `Surtr.sln` — solution root; `src/<ProjectName>/<ProjectName>.csproj` is the layout for every project (core library today, future Roslyn source generator and test projects later).
- `Directory.Build.props` — MSBuild settings shared by *every* project in the solution (`LangVersion`, `Nullable`, etc.). Put cross-project settings here, not per-project settings. `ImplicitUsings` is deliberately left off (default/disabled) — usings must be written explicitly in every file.
- `src/Surtr.Core` — the main library, built for `netstandard2.1` so the compiled DLL can be dropped into Unity (2021.2+, including IL2CPP) as a plain managed assembly. `AllowUnsafeBlocks` is enabled here specifically because the VM/registry work described above depends on it; don't assume other projects (e.g. a future source generator) need or should have it. `LangVersion` is inherited as `latest` from `Directory.Build.props` — targeting `netstandard2.1` does not cap the C# language version, since `TargetFramework` (runtime/BCL surface) and `LangVersion` (compiler syntax) are independent settings. Only watch for C# features that need runtime support beyond what's in the BCL (e.g. default interface methods), since Unity's Mono/IL2CPP backends can behave unreliably there even though the code compiles fine.

No VM, opcode set, or object registry has been implemented yet — the repo currently only contains the project skeleton.

## Coding conventions

- Every `.cs` file starts with `#nullable enable`, even though `Nullable` is already `enable` at the project level via `Directory.Build.props`. This is intentional and non-negotiable — don't remove it as "redundant".
- No `ImplicitUsings` — write out every `using` directive explicitly in each file (see above).
- Any documentation of a type, method, property, or field must use `///` XML doc comments (`<summary>`, `<remarks>`, `<param>`, etc.), never a plain `//` block sitting above the declaration — that's what lets Visual Studio's IntelliSense pick it up. This is about *format*, not coverage: it doesn't mandate documenting every member. Plain `//` comments are still the right tool for a short, non-obvious implementation note *inside* a method body (a specific line or block), since those aren't documenting a declaration and `///` can't attach to arbitrary statements anyway.
