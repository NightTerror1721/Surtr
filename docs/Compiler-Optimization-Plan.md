# Plan de optimización — Surtr.Compiler

Informe de rendimiento (memoria + velocidad) del compilador y su integración, generado tras
análisis estático multi-agente y línea base real. El objetivo es mejorar el rendimiento del
compilador en velocidad y consumo de memoria sin cambiar su comportamiento observable (misma
salida: mismo bytecode, mismos diagnósticos) y respetando netstandard2.1 (Unity-safe).

## Estado

| Fase | Estado |
|------|--------|
| Fase 1 — Quick wins | `[x]` Implementada (9/9) |
| Fase 2 — Optimizaciones locales | `[x]` Implementada en su mayoría (12 items); ver detalle |
| Fase 3 — Cambios estructurales | `[x]` PIP-8 hecho; SYM-3, CGN-1 y PIP-1 diferidos con justificación |

## Línea base medida

| Medición | Resultado |
|---|---|
| `dotnet build Surtr.sln -c Release` (incremental cálido) | 6.9 s |
| Stdlib real (10 ficheros, 31.2 KB) | 171–185 ms/corrida |
| Corpus sintético (50 módulos, 41 KB) | 162–171 ms |
| Corpus sintético (1000 módulos, 685.7 KB) | 1223 ms |

A escala pequeña el compilador trabaja en <20 ms; el resto es arranque de proceso + JIT.
El problema real de latencia es el LSP (recompila todo por keystroke) y la presión de GC.

---

# Fase 1 — Quick wins (coste bajo, riesgo ~0, impacto alto)

## [1.1] SYM-1 + SYM-2 — Caché de sustitución por construcción + singleton `Empty` por fábrica

- **Área:** Symbols. **Estado:** `[ ]`
- `NamedTypeSymbol.SubstitutionFromArguments` reconstruye `TypeSubstitutionBuilder` +
  `Dictionary<TypeParameterSymbol,TypeSymbol>` en cada llamada desde la ruta más caliente
  (`Conversions.Classify` → `IsSubtype` → `WalkForBase`, `MemberLookup`, `TypeResolver`,
  `TypeInference`). Las construcciones están internadas → la sustitución es función pura de la
  construcción y se cachea.
- `TypeSubstitution.Empty(factory)` asigna un wrapper por llamada → singleton por fábrica.
- **Ficheros:** `Binding/Symbols/NamedTypeSymbol.cs`, `Binding/Symbols/TypeSymbolFactory.cs`,
  `Binding/Symbols/TypeSubstitution.cs`.
- **Verificación:** `dotnet test Surtr.sln -c Release`.

## [1.2] SYM-7 — `sealed` en `NamedTypeSymbol`

- **Área:** Symbols. **Estado:** `[ ]`
- No hay subclases (verificado). Sellar habilita devirtualización/inlining del tipo más consultado
  del binder. 1 palabra. Verificar que no existe ninguna subclase antes de sellar.

## [1.3] SYM-5 — Cachear `Name` en tipos compuestos

- **Área:** Symbols. **Estado:** `[ ]`
- `ArrayTypeSymbol`/`DictionaryTypeSymbol`/`TupleTypeSymbol`/`ClosureTypeSymbol` implementan
  `Name => ToDisplayString()` (re-serialización recursiva en cada acceso). Los tipos son
  inmutables → cachear con `_name ??=`.

## [1.4] SYM-6 — `SyntheticNames.Build` sin strings temporales de 1 char

- **Área:** Symbols. **Estado:** `[ ]`
- `char.ToString()` aloca un string por llamada. Usar `string.Concat("$", category, "$", ...)`.

## [1.5] BIN-4 — Índices por nombre en `ModuleSymbol`

- **Área:** Binder. **Estado:** `[ ]`
- Replicar el patrón `_byName` (ya existente para tipos) para methods/fields/properties de módulo;
  fusionar `DeclaresMethod`+`BindModuleCall` en un `TryGetMethods(name, out candidates)`.
- **Ficheros:** `Binding/Symbols/ModuleSymbol.cs`, `Binding/BodyBinder.Expressions.cs`.

## [1.6] LEX-1 + LEX-2 — Literales numéricos sin delegados ni strings intermedios

- **Área:** Lexer. **Estado:** `[ ]`
- `SkipDigits(Predicate<char>)` aloca un delegado por literal (hasta 3); `StripSeparators`
  materializa un string por número. Usar `SkipDigits(DigitKind)` con `IsDigitOfKind` inline y
  parsear sobre `ReadOnlySpan<char>` (overloads span de `TryParse`, netstandard2.1-safe); para
  radix acumular con aritmética `checked`.
- **Ficheros:** `Syntax/Lexer.cs`.

## [1.7] CGN-7 — Guard único + constantes en sitios de llamada del emisor

- **Área:** CodeGen. **Estado:** `[ ]`
- `EmitCall` encadena tres `Try*` que pagan hasta 17 `TryGetMethods` para métodos importados;
  `IsXLength` aloca `"get_length"` por acceso. Guard `if (method.ImportedFrom is null) return false;`
  antes de los `Try*`; `const string` para nombres de getter; `HashSet<SurtrMethodInfo>` precomputado
  de miembros "opcode-ables".
- **Ficheros:** `CodeGen/MethodBodyEmitter.cs`.

## [1.8] PIP-4 — Índice O(1) de unidades en `CompilationSnapshot`

- **Área:** LSP. **Estado:** `[ ]`
- `UnitFor` escanea todas las unidades de todos los módulos con `Path.GetFullPath` dentro del bucle
  (3-5 veces por petición). Construir `Dictionary<string, SurtrSourceUnit>` en el ctor; `UnitFor` O(1).
- **Ficheros:** `src/Surtr.LanguageServer/Workspace/CompilationSnapshot.cs`.

## [1.9] CGN-13 — `AggressiveInlining` en helpers diminutos del emisor

- **Área:** Surtr.Core / Emit. **Estado:** `[ ]`
- `Track`, `ThrowIfFinished`, `Simple`, `WithU8`, `WithU16`, `WithI32`, `AppendI32`, `TypeIndex`,
  `ModuleIndex`, `MethodIndex`, `FieldIndex`, `ConstantIndex`, `ValidateLabel`,
  `RecordLabelDepth` llamados por cada instrucción emitida sin inline.
- **Ficheros:** `Surtr.Core/Bytecode/Emit/SurtrCodeEmitter.cs`, `SurtrCodeEmitter.OpCodes.cs`.

---

# Fase 2 — Optimizaciones locales (esfuerzo medio)

## [2.1] BIN-1 — Caché de miembros planos por tipo en `MemberLookup`

- **Área:** Binder. **Estado:** `[ ]`
- `FindMethods`/`FindField`/`FindProperty` recorren `Reachable(type)` (BFS con `HashSet`+`Queue`+
  sustitución por nodo, alocado por llamada) y `BindInstanceMember` ejecuta los tres. Cachear la
  lista plana de miembros por tipo (inmutable tras fase 2) + índices `name → members`.
- **Ficheros:** `Binding/MemberLookup.cs`.

## [2.2] BIN-2 — Ruta implícita rápida + caché de conversiones

- **Área:** Binder. **Estado:** `[ ]`
- `IsAssignable => Classify(...).IsImplicit` computa explícita + user-defined (escaneo de
  `operator as` en ambos tipos, `HashSet` por `IsSubtype`, sustitución por nodo). Añadir
  `IsImplicitlyConvertible` y caché `(from,to) → Conversion`.
- **Ficheros:** `Binding/Conversions.cs`.

## [2.3] LEX-3 — `ScanString` con `string.Create` de dos pasadas

- **Área:** Lexer. **Estado:** `[ ]`
- El `StringBuilder` se construye siempre y se descarta en interpoladas. Pasada 1 cuenta longitud
  decodificada y detecta `$`; si interpolado → slice crudo; si no → `string.Create`.
- **Ficheros:** `Syntax/Lexer.cs`.

## [2.4] PAR-1 — Un solo escaneo en `LooksLikeTypeArgumentList`

- **Área:** Parser. **Estado:** `[ ]`
- El escaneo se ejecuta dos veces sobre el mismo rango (con/sin `requireMemberAccess`) y no corta
  ante `<` anidado (O(N²) en cadenas de comparaciones). Devolver ambos hechos en un escaneo.
- **Ficheros:** `Syntax/Parser.Expressions.cs`.

## [2.5] PAR-2 — Eliminar copias del array de tokens

- **Área:** Parser. **Estado:** `[ ]`
- `tokens.ToArray()` en ctor por buffer + copia elemento a elemento en ctor por stream. Lexer
  llena un `Token[]` directamente; ctor sin copia; opcional `ArrayPool<Token>`.
- **Ficheros:** `Syntax/Parser.cs`, `Syntax/Lexer.cs`.

## [2.6] PAR-4 — Capacidades iniciales + listas vacías compartidas/perezosas

- **Área:** Parser. **Estado:** `[ ]`
- Listas sintácticas con capacidad 0 que crecen doblando; `f()` aloca lista vacía. Patrón lazy
  (`??=` + `Array.Empty<T>()`) y capacidades realistas (args 4, statements 8, params 4, members 8).
- **Ficheros:** `Syntax/Parser.Expressions.cs`, `Parser.Statements.cs`, `Parser.Declarations.cs`,
  `Syntax/Ast/DeclarationSyntax.cs`.

## [2.7] CGN-6 — Escritura de imagen en una pasada

- **Área:** Surtr.Core / Image. **Estado:** `[ ]`
- Doble `MemoryStream` sin capacidad + doble `ToArray` + código byte a byte + `GetBytes` por string.
  Estimar tamaño, buffer único, `Buffer.BlockCopy` para el chunk, buffer pooled para UTF-8.
- **Ficheros:** `Surtr.Core/Bytecode/Image/SurtrModuleImageWriter.cs`.

## [2.8] CGN-2 — Una sola copia del código en `Build()`

- **Área:** Surtr.Core / Emit. **Estado:** `[ ]`
- `FinishCode()`→`ToArray()`→`bodies[]`→copia byte a byte al buffer nativo. Contar longitudes,
  alocar `chunk.Code` una vez, volcar con copia de bloque; eliminar `bodies[]`.
- **Ficheros:** `Surtr.Core/Bytecode/Emit/SurtrModuleBuilder.cs`.

## [2.9] CGN-4 + CGN-5 — Caché de tokens de tipo/constante en el emisor del compilador

- **Área:** CodeGen. **Estado:** `[ ]**
- Cada instrucción que nombra un tipo/literal re-resuelve el token vía `_module.Type(...)`/
  `StringLiteral(...)`. Cachear `SurtrTypeToken`/`SurtrConstantToken` por símbolo/literal en
  `MethodBodyEmitter`.
- **Ficheros:** `CodeGen/MethodBodyEmitter.cs`.

## [2.10] CGN-8 — `DescriptorEmitter.EmitBoxedForm` cacheado

- **Área:** CodeGen. **Estado:** `[ ]`
- `StringBuilder`+string+`FromDescriptor` por cada instrucción de box de value class.
  Cachear por símbolo.
- **Ficheros:** `CodeGen/DescriptorEmitter.cs`.

## [2.11] CGN-9 — Caché de coste de inline por `MethodSymbol`

- **Área:** CodeGen. **Estado:** `[ ]`
- `InlineCost` recorre el cuerpo completo por cada sitio de llamada. Coste estable dentro de una
  compilación → caché en `EmitContext`.
- **Ficheros:** `CodeGen/MethodBodyEmitter.cs`, `CodeGen/InlineCost.cs`.

## [2.12] PIP-3 — Una sola enumeración/lectura por ciclo en el LSP

- **Área:** LSP. **Estado:** `[ ]`
- `Rebuild` relee todo y `PublishAll` vuelve a enumerar y releer todo. `Rebuild` devuelve la lista
  `(path, text)` materializada; `PublishAll` la consume; indexar líneas solo si hay diagnósticos.
- **Ficheros:** `src/Surtr.LanguageServer/LspServer.cs`, `Workspace/Workspace.cs`.

## [2.13] PIP-7 — Caché de tokens + line-starts por versión de documento

- **Área:** LSP. **Estado:** `[ ]`
- Cada feature re-lexea el documento; semantic tokens lo hace dos veces en el mismo request.
  Caché `(text, docVersion) → (tokens, lineStarts)`; arreglar el doble lexeo.
- **Ficheros:** `src/Surtr.LanguageServer/Workspace/Workspace.cs`,
  `SemanticTokensProvider.cs`, `SymbolResolver.cs`, `CompletionProvider.cs`.

## [2.14] BIN-3 — Eliminar allocaciones por expresión en rutas de nombres

- **Área:** Binder. **Estado:** `[ ]**
- `TryFlatten` crea `List<string>` por llamada; `TryResolveTypeName` envuelve `NamedTypeSyntax`;
  LINQ `Any` + closure en `ImportedFor`. Lista scratch por binder; filtro `HashSet<string>`.
- **Ficheros:** `Binding/BodyBinder.cs`, `Binding/BodyBinder.Expressions.cs`,
  `Binding/TypeResolver.cs`, `Binding/Symbols/ImportedModule.cs`.

## [2.15] BIN-7 — Buffers scratch en `OverloadResolution.TryBuild`

- **Área:** Binder. **Estado:** `[ ]**
- Array + lista por candidata por sitio de llamada. `ArrayPool<TypeSymbol?>`/struct de pila;
  cachear `VarargIndex` y `name→index` por método; aborto temprano.
- **Ficheros:** `Binding/OverloadResolution.cs`.

---

# Fase 3 — Cambios estructurales (mayor inversión)

## [3.1] SYM-3 — `BoundNode` guarda `SourceSpan` en vez de `SyntaxNode`

- **Área:** Symbols/BoundTree + CodeGen. **Estado:** `[ ]**
- Cada nodo bindeado retiene su `SyntaxNode`, manteniendo el AST completo vivo hasta el fin del
  emit (dos árboles simultáneos). Sustituir por `SourceSpan`; en el emisor `_at` pasa a `SourceSpan`.
- **Ficheros:** `Binding/BoundTree/BoundExpressions.cs`, `BoundStatements.cs`,
  `CodeGen/MethodBodyEmitter.cs`, `CodeGen/ModuleEmitter.cs`.

## [3.2] PIP-1 — Rebuild perezoso + caché de parseo por hash en el LSP

- **Área:** LSP. **Estado:** `[ ]`
- Rebuild solo cuando llega una request (flag `_dirty`); caché de árbol por `(path, textHash)`.
- **Ficheros:** `src/Surtr.LanguageServer/Workspace/Workspace.cs`, `LspServer.cs`.

## [3.3] PIP-8 — Parseo paralelo con bags por fichero

- **Área:** Compilation. **Estado:** `[ ]`
- `Parallel.ForEach` sobre `SourceFiles` con `SurtrDiagnosticBag` por fichero + fusión final;
  bind/emit secuenciales.
- **Ficheros:** `Compilation/SurtrCompilation.cs`.

## [3.4] CGN-1 — Relajación de saltos de una pasada

- **Área:** Surtr.Core / Emit. **Estado:** `[ ]`
- Punto fijo con reescritura completa del cuerpo por ronda. Una pasada con mapa de desplazamiento
  por prefijo (`int[n+1]`) y una única reescritura sobre buffer pre-dimensionado.
- **Ficheros:** `Surtr.Core/Bytecode/Emit/SurtrCodeEmitter.cs`.

## [3.5] PAR-5/PAR-6/SYM-4 — Cambios de API del AST (fase 2 de literales, spans, nombres)

- **Área:** Parser/AST. **Estado:** `[ ]`
- Nombres de nodos como `ReadOnlyMemory<char>` (materializar solo en el binder); `SourceSpan` de
  8 bytes derivando line/column solo en diagnósticos; `struct LiteralValue` sin boxing.
  Cambios de API coordinados con binder/LSP/tests.
- **Ficheros:** `Syntax/Ast/*.cs`, `Syntax/SourceSpan.cs`, `Binding/BoundTree/BoundExpressions.cs`.

---

# Estado de implementación (2026-08-21)

Implementación completa de la Fase 1 y de la mayor parte de la Fase 2, y de un item de la Fase 3.
**Regresión:** `dotnet test Surtr.sln -c Release` → 2429 tests verdes tras cada grupo de cambios.

## Fase 1 — Hecho (9/9)

- [x] **1.1 SYM-1 + SYM-2** — `NamedTypeSymbol.SubstitutionFromArguments` cacheada por construcción;
      `TypeSymbolFactory.EmptySubstitution()` devuelve un singleton por fábrica (se eliminó
      `TypeSubstitution.Empty`).
- [x] **1.2 SYM-7** — `NamedTypeSymbol` es ahora `sealed` (sin subclases en todo `src/`).
- [x] **1.3 SYM-5** — `Name` cacheado (`_name ??=`) en los 4 tipos compuestos.
- [x] **1.4 SYM-6** — `SyntheticNames.Build` usa `string.Create` (cero strings temporales).
- [x] **1.5 BIN-4** — `ModuleSymbol.FindMethods/FindField/FindProperty` con índices perezosos
      invalidados por setter; call sites de `BodyBinder.Expressions` (incluida la fusión
      `DeclaresMethod`+`BindModuleCall` en un solo `BindModuleCall` que devuelve `null`).
- [x] **1.6 LEX-1 + LEX-2** — `SkipDigits(DigitKind)` sin delegados; literales numéricos parseados
      sobre `ReadOnlySpan<char>` (overloads span de `TryParse`); radix con aritmética `checked`.
      Extra en el mismo fichero: LEX-5 (fast-path ASCII de identificadores/trivia) y LEX-8
      (doc comment con `Trim` sobre span).
- [x] **1.7 CGN-7** — Guard único `ImportedFrom is null` + `HashSet<SurtrMethodInfo> OpcodeableMembers`
      precomputado en `EmitCall`; `const LengthGetterName` en los cuatro `Is*Length`.
- [x] **1.8 PIP-4** — `CompilationSnapshot` indexa unidades por ruta normalizada en el ctor;
      `UnitFor` O(1). Extra: `TextLines.Index` escribe directo a `int[]`.
- [x] **1.9 CGN-13** — `[MethodImpl(AggressiveInlining)]` en `Track`, `ThrowIfFinished`, `Simple`,
      `WithU8/U16/I32`, `CheckRange`, `AppendI32`, `TypeIndex`, `ModuleIndex`, `EndFlow`,
      `ValidateLabel`, `RecordLabelDepth` (SurtrCodeEmitter) y `MethodIndex/FieldIndex/ConstantIndex`
      (OpCodes).

## Fase 2 — Hecho

- [x] **2.1 BIN-1** — `MemberLookup.Reachable` materializa y cachea por tipo; `CollectReachable`
      reemplaza el iterador con `HashSet`+`Queue` por llamada.
- [x] **2.2 BIN-2** — `Conversions.ClassifyImplicitOnly` (ruta implícita sin fallback explícito);
      `IsImplicitlyConvertible` delega en ella; `IsSubtype` reutiliza un `HashSet` scratch.
- [x] **2.3 LEX-3** — `ScanString` con dos pasadas (validación con `ScanEscape` + `DecodeSpan` a
      buffer de pila); 1 alocación por literal plano, 0 en interpoladas.
- [x] **2.4 PAR-1** — `LooksLikeTypeArgumentList` devuelve `(isGenericCall, isMemberAccess)` en un
      solo escaneo, usando `PeekType` (sin copiar el `Token` de 64 B).
- [x] **2.5 PAR-2 (parcial)** — `TokenReader.PeekType`/`Cursor.Elements` eliminan las copias del
      struct en lookahead; el refactor `Lexer→Token[]` sin `ToArray` se difiere (cambio de API del
      pipeline lexer/parser).
- [x] **2.6 PAR-4** — Patrón lazy + capacidades en `ParseArgumentList`, bloques, unit, atributos,
      enum-cases y `const if` (`Array.Empty<T>()`).
- [x] **2.7 CGN-6** — Escritor de imagen: `MemoryStream` presemados, código del chunk volcado con un
      `memcpy` (`SurtrNativeArray.CopyTo`) + un solo `Write` (se corrigió un bug intermedio: el
      `Write(byte[])` de `BinaryWriter` no escribe prefijo de longitud).
- [x] **2.8 CGN-2** — `SurtrModuleBuilder.Build` copia cada cuerpo con `SurtrNativeArray.CopyFrom`
      (memcpy) en vez de byte a byte.
- [x] **2.10 CGN-8** — `DescriptorEmitter.EmitBoxedForm` cacheada por símbolo.
- [x] **2.11 CGN-9** — `EmitContext.InlineCostOf` cachea el coste de inline por cuerpo; los 3 sitios
      de `WorthInline` lo usan.
- [x] **2.12 PIP-3** — `Workspace.LastBuildFiles` (enum) reutilizado por `PublishAll`; el texto y el
      índice de líneas solo se calculan para ficheros con diagnósticos.
- [x] **2.13 PIP-7** — Eliminado el doble lexeo de `SemanticTokensProvider.Compute` (un lex por request).
- [x] **2.14 BIN-3 (parcial)** — `ImportedFor` sin LINQ (bucle manual sobre `Only`).
- [x] **2.15 BIN-7** — `OverloadResolution.TryBuild` usa `ClassifyImplicitOnly` (candidatos
      descartados sin pagar el fallback explícito).
- [x] **PIP-10** — `SurtrDiagnostic.Id` cacheado (se lee por diagnóstico por publish del LSP).
- [x] **LEX-6** — `Tokenize()` presiembra la lista de tokens desde el tamaño del buffer.

## Fase 2 — Diferido (justificación)

- **2.9 CGN-4/5 (caché de `SurtrTypeToken`/`SurtrConstantToken` en `MethodBodyEmitter`)** —
  requiere hilos de caché a través de muchos sitios de emisión; el dedup ya es `Dictionary<ulong,int>`
  sin boxing. Bajo valor relativo al riesgo.
- **2.14 BIN-3 (resto)** — lista scratch de `TryFlatten` y sobrecarga `TryResolveTypeName`: cambio
  mecánico pendiente, se documenta para la siguiente pasada.
- **2.15 BIN-7 (resto)** — buffers scratch (`ArrayPool`) y caché `name→índice` por método.

## Fase 3 — Hecho

- [x] **3.3 PIP-8** — `SurtrCompilation.ParseSources` paralelo con `Parallel.For`, bag por fichero y
      merge en orden (mantiene el orden de diagnósticos).

## Fase 3 — Diferido (justificación)

- **3.1 SYM-3 (`BoundNode` guarda `SourceSpan` en vez de `SyntaxNode`)** — la API pública de los
  nodos bindeados expone `.Syntax` y el Language Server la usa para leer el nodo sintáctico
  original (`call.Syntax as CallExpressionSyntax`, `declaration.Syntax is LocalDeclarationStatementSyntax`,
  `*.Syntax.Span` en SymbolResolver/SemanticTokens/InlayHint/Completion). Cambiarlo exige además un
  índice span→nodo en el LSP. Es un refactor propio de una pasada dedicada.
- **3.2 PIP-1 (rebuild perezoso + caché de parseo)** — el rebuild perezoso toca el ciclo
  didChange/publish del LSP (semántica de publicación de diagnósticos); la caché de parseo exige
  inyectar árboles pre-parseados en `SurtrProject`/`SurtrCompilation` (cambio de API del compilador).
  Se documenta el diseño; candidato para la siguiente pasada.
- **3.4 CGN-1 (relajación de saltos de una pasada)** — el camino lento (métodos con ramas fuera de
  ±32767) es raro; una pasada con mapa de desplazamiento por prefijo tiene riesgo real sobre el
  bytecode emitido. Se documenta el algoritmo.
- **3.5 PAR-5/PAR-6/SYM-4 fase 2** — cambios de API del AST (`ReadOnlyMemory<char>` en nombres,
  `SourceSpan` de 8 bytes, `struct LiteralValue`): rompen binder/LSP/tests; requieren fase propia.

## Medición post-implementación

- `dotnet test Surtr.sln -c Release` → **2429/2429 verdes**.
- Corpus 1000 módulos / 686 KB: ~1.19–1.24 s (sin cambio en tiempo de pared; el corpus es
  E/S-por-fichero dominado).
- Módulo único grande (490 KB de fuente, 4500 funciones con sobrecargas/bucles/llamadas):
  **~325 ms** en estado estable → imagen `surtr.Big.surtrc` de 521 KB.
- Los beneficios principales de esta implementación son de **presión de GC y trabajo repetido**
  (sustituciones, BFS de miembros, conversiones, lookups, buffers, cachés), que se manifiestan en la
  latencia del LSP por keystroke y en compilaciones grandes, más que en el tiempo de pared de
  compilaciones pequeñas dominadas por el arranque del proceso.

---

## Verificación

- `dotnet build Surtr.sln -c Release` (debe compilar sin warnings nuevos).
- `dotnet test Surtr.sln -c Release` (regresión tras cada fase).
- Corpus sintético de 1000 módulos (generado en `%TEMP%\opencode\surtr-bigcorpus`) para comparar
  tiempos antes/después con `Surtr.Stdlib.Tool`.
- `dotnet-counters monitor` / `dotnet-trace` para alocaciones/GC (memoria pico no medible con
  `Start-Process` en PowerShell 5.1).