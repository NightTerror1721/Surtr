# Plan — Segunda ola de atributos built-in

Documento de diseño + handoff para implementar en otra sesión siete atributos nuevos:
**`@TestIgnore`, `@Benchmark`, `@NoAlloc`, `@Flags`, `@Throws`, `@TestBefore`, `@TestAfter`**.

Sigue el estilo del informe original (`docs/Informe-Atributos-BuiltIn.md`, §P1–P8) pero con el
nivel de detalle de implementación del handoff (`docs/Handoff-Atributos-Siguiente-Sesion.md`):
archivos exactos, puntos de enganche verificados, tests patrón y trampas conocidas.

---

## 0. Estado de partida (verificado al escribir este documento)

- Branch `develop`. Últimos commits: `91de605` (warnings a cero), `6408318` (CSE dominancia +
  natives reflexión/iteradores), `c960ea9` (natives puros en C#), `92652f4` (CSE entre
  statements), `d58e0af` (CSE mismo-expresión), `0f75952` (plegado `@Pure`), `d0db813`
  (`@Range` initializers/anidadas vía `BoundSequenceExpression`).
- Build: **0 advertencias, 0 errores**. Tests: **2907/2907**. Mantener ambos invariantes.
- El vocabulario actual es P1–P8 del informe: `@Obsolete`, `@NoDiscard`, `@Value`, `@Range`,
  `@Export`, `@Test`/`@TestSuite`, `@Pure` (fases 1–3 completas), `@MainThread`/`@ThreadSafe`
  (documentales).

---

## 1. Receta general — cómo se añade un atributo built-in

Verificado en el código. Checklist en orden de dependencia:

1. **Clase del atributo en C#:** `src/Surtr.Core/Runtime/BuiltIns/SurtrBuiltIns.cs`
   - Declarar el `SurtrClass` junto a los demás, en el bloque de las líneas ~411–420
     (`Obsolete`, `NoDiscard`, …, `ThreadSafe = DeclareObject("...", Attribute)`).
   - Los **campos** se añaden después, en las líneas ~488–493, con los helpers existentes:
     - `DeclareReasonAttribute(builder)` → campo `reason: string` (lo usan `Obsolete`,
       `NoDiscard`). **Reutilizar tal cual para `@TestIgnore`.**
     - `DeclareNamedAttribute(builder)` → campo `name: string` (lo usan `Export`, `Test`,
       `TestSuite`). **Reutilizar tal cual para `@Throws`** (el `name` lleva el tipo lanzado).
     - Los parameterless (`Value`, `Pure`, `MainThread`, `ThreadSafe`) no llevan nada:
       **así van `@Benchmark`, `@NoAlloc`, `@Flags`, `@TestBefore`, `@TestAfter`.**
2. **Reconocimiento:** `src/Surtr.Compiler/Binding/BuiltInAttributes.cs`
   - Constante de nombre + entrada en `TargetsByBuiltinName` (el enum
     `SurtrAttributeTargets.Enum` **existe** — lo usa `@Obsolete`).
   - Helpers de lectura siguiendo el patrón existente (`IsPure`, `IsNoDiscard`,
     `RangeLow/High`): `Find(symbol, Nombre)` + wrappers. Ojo: `Find` devuelve el **primer**
     use; si un atributo es repetible (`@Throws`) hace falta un collector que devuelva todos.
3. **Retención:** `ReachesImage` excluye `CompileTimeOnly` y `@Value`. **Los siete nuevos
   llegan a imagen** (el runner/host los lee por reflexión). Ninguno va en CompileTimeOnly.
4. **Diagnósticos:** `src/Surtr.Compiler/Diagnostics/SurtrDiagnosticCode.cs`. El último
   usado es `PureContractViolated = 3081`; **el siguiente libre es 3082** (verificar al
   implementar). Asignación propuesta en §9.
5. **Significado en compilador (si lo tiene):** engancharse donde diga la ficha de cada
   atributo. Recordatorio de orden de fases: `BindAttributes` corre dentro de `BindBodies`
   **antes** de ligar cuerpos, pero **nunca durante la member phase** — una marca no es
   visible ahí.
6. **Hover LSP (opcional por atributo):** `src/Surtr.LanguageServer/Workspace/HoverFormatter.cs`
   — mismo tratamiento que recibió `@Pure` en su fase 1.
7. **Tests,** en dos sitios con patrones ya escritos:
   - `src/Surtr.Tests/Compiler/Binding/BuiltInAttributeTests.cs` — targets
     (rechazado/aceptado), validación de argumentos, diagnósticos de compilación.
   - `src/Surtr.Tests/Compiler/CodeGen/ModuleEmitterTests.cs` — round-trip de imagen
     (`field.TryGetAttribute(SurtrBuiltIns.X, out var use)`), comportamiento runtime, y la
     región del runner.
8. **Commit:** uno por atributo (o por familia), con `detect_changes()` antes y verificando
   que solo cambian los símbolos esperados.

---

## 2. `@TestIgnore("motivo")`

El complemento de `@Test`: descubre el test pero no lo ejecuta; el runner lo reporta como
*skipped* con el motivo escrito.

| Aspecto | Valor |
|---|---|
| Clase C# | `TestIgnore = DeclareObject("TestIgnore", Attribute)` + `DeclareReasonAttribute` |
| Targets | `Method` |
| Campos | `reason: string` (opcional — los args posicionales que sobran mantienen default) |
| Significado compilador | Ninguno requerido |
| Retención | Imagen (lo lee `SurtrTestRunner`) |

**Lint opcional recomendado (fase 1.5):** `@TestIgnore` sobre un método **sin** `@Test` en la
misma declaración → warning `IgnoreWithoutTest` (§9). Punto de enganche: tras el bucle
`BindAttributes` en `BindBodies` (ahí ambas marcas ya existen en `symbol.Attributes`;
comprobar con `IsTestIgnored(m) && !IsMarkedTest(m)` — añadir helper `IsMarkedTest` leyendo
`Find(symbol, Test)`).

**Cambio en el runner:** `src/Surtr.Core/Runtime/Testing/SurtrTestRunner.cs` — el descubrimiento
filtra por `method.TryGetAttribute(SurtrBuiltIns.Test, out _)` (línea ~97) y nombra con
`NameOf(method, SurtrBuiltIns.Test)` (~101). Añadir: si además
`TryGetAttribute(SurtrBuiltIns.TestIgnore, out var ignore)` → estado nuevo **Skipped** en el
modelo de resultado, con el motivo leído del campo `reason` (mismo patrón de lectura de campo
que `NameOf`). Un test skipped **no ejecuta** el método.

**Tests:**
- Runner (`ModuleEmitterTests`, región del runner ~4640): un módulo con dos `@Test`, uno
  ignorado → 1 passed, 1 skipped, el motivo llega, y el cuerpo ignorado **no corrió**
  (contador de efectos en 0).
- `BuiltInAttributeTests`: `@TestIgnore` aceptado en método, rechazado en clase/campo;
  `@TestIgnore("x")` con argumento no-texto → `AttributeArgumentTypeMismatch`.

---

## 3. `@Benchmark`

Hermano de `@Test` para medición: misma descubrimiento por reflexión, ejecución repetida con
medición de tiempo.

| Aspecto | Valor |
|---|---|
| Clase C# | `Benchmark = DeclareObject("Benchmark", Attribute)`, parameterless |
| Targets | `Method` (el agrupamiento natural es la clase contenedora, igual que los tests) |
| Campos | Ninguno en v1 (iteraciones/warmup decide el runner; futuro: campo `iterations: int`) |
| Significado compilador | Ninguno |
| Retención | Imagen |

**Cambio en el runner:** segunda pasada de descubrimiento filtrando `SurtrBuiltIns.Benchmark`;
ejecuta warmup + N mediciones (`Stopwatch`), reporta mediana/mínimo y ns/op. El modelo de
resultados necesita una entrada de benchmark distinta de passed/failed/skipped.

**Decisión documentada:** los benchmarks deben compilarse **sin** el define `Debug` (para no
medir los checks de `@Range`); es política del host que compila, no del compilador — anotarlo
en el doc-comment de la clase.

**Lint opcional:** `@Benchmark` + `@Test` en el mismo método → warning (rol ambiguo), §9.

**Tests:** runner descubre y ejecuta un benchmark varias veces (contador de efectos > 1),
reporta tiempo > 0; `@Benchmark` rechazado en campo/clase.

---

## 4. `@NoAlloc`

Promesa de que el cuerpo **no aloca en el heap**. Es el hermano de `@Pure` en el eje memoria,
y su fase 2 reutiliza el molde de walker que ya existe dos veces
(`ConstFunctionCheck.cs`, `PureFoldVerifier`).

| Aspecto | Valor |
|---|---|
| Clase C# | `NoAlloc = DeclareObject("NoAlloc", Attribute)`, parameterless |
| Targets | `Method \| Property` (accessors — espejo exacto de `@Pure`) |
| Significado compilador | **Fase 2: lint** (abajo). Fase 1: metadata + hover |
| Diagnóstico | `AllocationInNoAllocBody` (§9) |
| Retención | Imagen |

**Fase 2 — lint.** Archivo nuevo `src/Surtr.Compiler/Binding/NoAllocCheck.cs` modelado sobre
`ConstFunctionCheck.cs` (walker statement/expresión que reporta). Enganche: al final de
`BindOne` (Binder.cs, tras `FlowAnalysis.Analyze`):

```csharp
if (BuiltInAttributes.IsNoAlloc(body.Method))
    NoAllocCheck.Verify(body.Method, bound, _diagnostics, body.SourceName);
```

Constructos que **rechaza** (todos alojan):

- `BoundObjectCreationExpression` (instanciar clase)
- `BoundArrayLiteralExpression`, `BoundCollectionCreationExpression`,
  `BoundDictLiteralExpression`
- `BoundInterpolatedStringExpression` y `BinaryOperator.Add` con operandos string
  (concatenación aloca)
- `BoundLambdaExpression` (crear la closure aloca; **invocar** una closure ya existente se
  permite — la alocação ocurrió en quien la creó)
- `BoundYieldExpression` (un generador aloca cursor + estado por definición)

Se permite: locals, params, lecturas de campo, aritmética/comparaciones, control flow,
lecturas de propiedad, llamadas en general.

**Decisiones documentadas (límites v1, escribir en el doc-comment):**
- **Tuplas permitidas**: el emisor las baja como bloques multi-slot inline
  (`TryMultiSlotWidth`), no alocan. Revisitar si algún día boxing.
- **Llamadas no se inspeccionan** en v1: `s.substring(0,1)` aloca *dentro del callee*; el lint
  local no lo ve. El check transitivo es fase futura y puede copiar el fixed-point de
  `PreparePureFolding`, pero exigiría una lista curada de natives alloc-free (¡`substring`
  aloca!) — explícitamente fuera de alcance ahora.
- Igual que `@Pure`: solo warning, la función compila.

**Tests:** cuerpo que instancia/array literal/dict/interpolación/lambda/yield → un warning
cada uno apuntando al constructo; cuerpo limpio (aritmética sobre params + return) callado;
función sin marca nunca revisada; `@NoAlloc` rechazado en clase, aceptado en propiedad.

---

## 5. `@Flags`

Operadores bit a bit y miembros derivados sobre enums. **El más caro del lote** — tocar
resolución de operadores + miembros sintéticos + lint. Implementar por fases separadas con
commit por fase.

| Aspecto | Valor |
|---|---|
| Clase C# | `Flags = DeclareObject("Flags", Attribute)`, parameterless (sin colisión de nombre en C#) |
| Targets | **`Enum`** (`SurtrAttributeTargets.Enum` existe) |
| Fases | 1 metadata/hover · 2 operadores · 2.5 lint · 3 miembros sintéticos |
| Retención | Imagen |

### Fase 2 — operadores binarios
Punto exacto: `BodyBinder.Expressions.cs`, bloque bitwise de `ResolveBinary` (líneas
~1116–1133). Hoy solo resuelve `int⊗int` y `bool⊗bool` y devuelve `null` para todo lo demás.
Añadir rama:

- `left`/`right` son el **mismo** `NamedTypeSymbol { TypeKind: TypeSymbolKind.Enum }`
- el enum lleva la marca (`IsMarkedFlags(enumType)` — helper nuevo)
- `op ∈ {BitAnd, BitOr, BitXor}` → devolver el propio tipo enum (identidad de conversión; el
  payload es int).

Los compound `|=`/`&=`/`^=` salen gratis: `Expand()` ya los mapea a estos mismos
`BinaryOperator`. Verificar el emisor: `EmitBinary` despacha por `TypeCodeOf` — confirmar que
emite el opcode entero aunque el tipo sea enum; si filtra por `SpecialType.Int` exclusivo,
relajar para enums marcados.

**Decisión abierta:** `~enum` (unario `BitNot`). Incluirlo solo si `EmitUnary` lo permite sin
obra; si no, dejarlo fuera y documentar. `+`/comparaciones relacionales siguen sin resolver.

### Fase 2.5 — lint de potencias de dos
Un caso de enum `@Flags` cuyo valor no es potencia de dos (0 permitido como `None`) → warning
`FlagCaseNotPowerOfTwo` (§9). Punto: validación post-ligadura del tipo enumerado en la member
phase… **no**: la marca no existe en member phase. Hacerlo tras `BindAttributes` en
`BindBodies` (ahí el enum ya tiene `Attributes` y sus casos tienen valores constantes
plegados), recorriendo `_modules`/tipos enum registrados con la marca.

### Fase 3 — miembros sintéticos
Mismo patrón que `@Value` (`ValueMemberSynthesizer.cs` + registro en
`SynthesizeValueMembers`, Binder.cs ~2983, que corre antes de ligar cuerpos):

- `$contains(other: E): bool` ≡ `(this & other) == other`
- `toDisplayString()` que imprima la combinación (`Read | Write`) **solo si el usuario no
  declaró el suyo** — misma regla de precedencia que `@Value`.

Nota abierta: comprobar cómo comparan hoy los enums (¿payload o identidad?). Si es identidad,
añadir `$equals` por payload a la síntesis.

**Riesgos:** tocar `ResolveBinary` es zona caliente (ya sabemos del informe que
`OverloadResolution` es HIGH); cambios aditivos al final del bloque bitwise, con tests de
no-regresión de los operadores int/bool existentes.

**Tests:** `BuiltInAttributeTests`: `@Flags` aceptado en enum, rechazado en class/method/field.
`ModuleEmitterTests` runtime: `let rw = Perm.Read | Perm.Write;` compila, corre y devuelve el
payload esperado; `rw.contains(Perm.Read)` sintético true/false; warning de caso
non-potencia; `toString` combinado; round-trip de imagen del atributo.

---

## 6. `@Throws("ExceptionTypeName")`

Documenta qué excepciones puede lanzar una función. Consumidores: hover LSP y documentación.

| Aspecto | Valor |
|---|---|
| Clase C# | `Throws = DeclareObject("Throws", Attribute)` + **`DeclareNamedAttribute`** (el campo `name` lleva el tipo) |
| Targets | `Method` |
| Repetible | Sí — varios `@Throws` en la misma declaración se registran como uses múltiples |
| Helper nuevo | `AllThrows(Symbol)` que recoja **todos** los uses nombrados `Throws` (el `Find` actual devuelve solo el primero) |
| Retención | Imagen |

**Fase 2 — validación (el significado real, barato):** tras `BindAttributes`, para cada use
`Throws` resolver `use.Arguments[0]` como nombre de tipo contra el `Scope` que guarda el
`AttributeBinding` (mismo mecanismo con que `BindAttributes` resuelve la clase del atributo
vía `_resolver.Resolve(new NamedTypeSyntax(...), binding.Scope, binding.SourceName)`), y
subir por `BaseType` comprobando que se llega al built-in `Exception`
(`SurtrBuiltIns.Exception`). Si no resuelve o no es excepción → warning
`ThrowsTypeNotException` (§9) en el span del argumento.

**Fase 1:** hover — “throws X, Y” concatenando `AllThrows`.

**Tests:** uso válido callado; nombre inexistente → warning; nombre de tipo no-excepción →
warning; dos `@Throws` registrados y legibles desde la imagen.

---

## 7. `@TestBefore` / `@TestAfter`

Fixtures del runner: se ejecutan antes/después de cada test. Nombres según decisión del
usuario; nota de consistencia: la familia actual es `Test`/`TestSuite`, así que el prefijo
`Test` encaja.

| Aspecto | Valor |
|---|---|
| Clases C# | `TestBefore` / `TestAfter`, parameterless |
| Targets | `Method` |
| Significado compilador | Ninguno obligatorio; **lint recomendado** (abajo) |
| Retención | Imagen |

**Semántica runner:** antes/después de **cada** test (per-test, el default estándar). Ámbito:
fixtures declaradas dentro de una clase `@TestSuite` aplican a los tests de esa clase;
fixtures a nivel módulo aplican a todos los tests del módulo. Orden: orden de declaración.
Garantía: si un `TestBefore` lanza, el test se reporta failed/blocked y el `TestAfter`
correspondiente **igualmente corre**.

**Lint recomendado** (barato, da significado real):
- fixture + `@Test` en el mismo método → warning;
- firma no válida para fixture (retorno ≠ void, o parámetros) → warning.
Código propuesto: `InvalidTestFixture` (§9). Punto: tras `BindAttributes` en `BindBodies`,
recorriendo métodos con cualquiera de las tres marcas.

**Tests runner:** contador de efectos prueba before/after alrededor de cada test (2 tests →
before×2, after×2); excepción en before marca el test y el after corre; ámbito de suite no
contamina a otro suite; fixtures de módulo aplican a tests sueltos del módulo.

---

## 8. Orden de implementación recomendado

De menor a mayor riesgo, cada ítem con su commit:

1. **Familia runner:** `@TestIgnore` → `@TestBefore`/`@TestAfter` → `@Benchmark`
   (todo vive en `SurtrBuiltIns` + `SurtrTestRunner` + tests del runner; cero riesgo de
   compilador más allá de la receta §1).
2. **`@Throws`** (metadata + validación; introduce el patrón de atributo repetible).
3. **`@NoAlloc`** (archivo walker nuevo, aislado; cero interacción con resolución).
4. **`@Flags`** (el único que toca resolución de operadores y sintéticos; por fases internas:
   operadores → lint → sintéticos, commit por fase).

## 9. Diagnósticos nuevos (asignación propuesta)

Verificar en `SurtrDiagnosticCode.cs` que están libres al implementar (último conocido: 3081):

| Código | Nombre | Atributo |
|---|---|---|
| 3082 | `AllocationInNoAllocBody` | `@NoAlloc` |
| 3083 | `FlagCaseNotPowerOfTwo` | `@Flags` |
| 3084 | `ThrowsTypeNotException` | `@Throws` |
| 3085 | `InvalidTestFixture` | `@TestBefore`/`@TestAfter` (+ fixture+junto a `@Test`) |
| (opcional) | `IgnoreWithoutTest` / `BenchmarkWithTest` | lint de roles mezclados |

Todos **warnings**, salvo decisión contraria explícita.

## 10. Reglas obligatorias del repo (AGENTS.md) y verificación

1. Antes de editar cualquier símbolo: `impact({target, direction:"upstream"})` y reportar blast
   radius. Conocidos: `BindAttributes` CRITICAL, `FlowAnalysis` CRITICAL, `MemberLookup` tocó
   vistas sustituidas (cuidado), `OverloadResolution`/`ResolveBinary` zona caliente para
   `@Flags`. Cambios aditivos aprobados por patrón, pero confirmar.
2. Antes de commit: `detect_changes()` — solo símbolos esperados.
3. Renames solo con `rename` de GitNexus.
4. Verificación: `dotnet build Surtr.sln` (**0 warnings exigido** — acabamos de limpiarlos)
   y `dotnet test src/Surtr.Tests` (2907+ verde).
5. No ampliar alcance silenciosamente: cada decisión abierta marcada arriba (BitNot, tuplas,
   transitividad de `@NoAlloc`, naming de fixtures) se consensúa o se documenta como límite.

## 11. Trampas aprendidas (sesiones anteriores + esta)

- **Literal int fuera de int32** lo valida el emisor (p.ej. `2166136261` falla).
- **`checked` es keyword de C#** — no usar como variable de patrón.
- **`Assert.Throws<T>` de xUnit exige tipo exacto**: excepción script → `SurtrThrownException`
  (subclase de `SurtrExecutionException`), no la base.
- **El harness no auto-resuelve stdlib source**: proveer source inline con
  `AddSourceFile(...)` o cargar imagen real (`ModuleEmitterTests.Build(source, defineDebug)`
  + `RunDebug`).
- **`DeclareLocal` reporta duplicados** — temporales únicos por contador por método
  (`$rangeN`, `$cseN`).
- **`Widen` es privado de `BodyBinder`** (partial): accesible desde cualquier partial.
- **Orden de fases**: `BindAttributes` corre antes de ligar cuerpos pero **nunca** en member
  phase — cualquier lint que lea marcas engancha tras `BindAttributes` en `BindBodies`.
- **Badges de índice**: `AGENTS.md`/`CLAUDE.md` se auto-modifican con el contador de símbolos;
  `git checkout -- AGENTS.md CLAUDE.md` antes de commitear. Los `docs/Informe-*.md` del
  usuario van sin trackear — no commitearlos sin pedirlo.
- **Build 0 warnings es invariante nuevo**: al cambiar firmas privadas, actualizar doc
  comments (`CS1734`), nullable (`CS8604`), y usar `NoSyntax` (no `null`) en nodos bound
  sintéticos (`CS8625`); colecciones de símbolos Roslyn con `SymbolEqualityComparer.Default`
  (`RS1024`).
- **Vistas sustituidas copian `Attributes`** (arreglado en `MemberLookup`): cualquier helper
  nuevo de lectura de marca funciona también sobre `Array<int>.get` etc.

## 12. Criterios de aceptación globales

- Build: 0 warnings, 0 errores.
- Tests: todo verde, con los nuevos cubriendo por atributo: targets, argumentos, diagnóstico
  de compilación (si lo tiene), round-trip de imagen, comportamiento runtime/runner.
- `docs/Language-Syntax.md` §atributos actualizado con los siete (tabla de §11).
- `docs/Informe-Atributos-BuiltIn.md`: añadir §P9..P15 con las mismas notas de
  **> Estado (implementado)** que tiene el resto.
