# Informe y plan: definición formal de módulos/imports y operador `moduleof` — **Implementado**

**Estado: las Partes 2 y 4 completas — implementación, tests y documentación.** Verificado con
`dotnet build Surtr.sln` y `dotnet test Surtr.sln` en verde (2159/2159, 0 advertencias) tras cada
fase. Pendiente solo de commit si el usuario lo pide.

Este documento tiene cuatro partes, en el mismo formato de fases que
`docs/Plan-Sintaxis-Imports-Atributos-LSP.md` y `docs/Plan-Globales-Nativos-Inline-Operadores.md`.
La Parte 1 es un **informe de investigación**: contrasta la lista de requisitos que planteó el
usuario sobre módulos e imports contra el estado real del compilador y el runtime, verificado
empíricamente (no solo leído en la documentación) y con un hueco real encontrado y confirmado con
pruebas directas contra el binder. La Parte 2 es el **plan de implementación de ese hueco**:
`import ModulePath;` sin `.*` como azúcar sintáctica del wildcard, cuando el path completo ya es
un módulo real. Las Partes 3 y 4 son las decisiones de diseño y el **plan de implementación fase
a fase** de lo único que era diseño enteramente nuevo desde el principio: el operador `moduleof`.

---

## Parte 1 — Informe: qué de lo pedido ya existe

Hallazgo principal: **los puntos 1 a 4 de la especificación no son una propuesta nueva — ya
están implementados, documentados y cubiertos por tests de punta a punta**. Coinciden, casi
palabra por palabra, con `docs/Language-Syntax.md` §2.1 y con las Fases 7, 8 y 9 de
`docs/Plan-Sintaxis-Imports-Atributos-LSP.md`. Solo el punto 5 (`moduleof`) es diseño genuinamente
nuevo, sin ningún trabajo previo en ningún documento del repo.

### 1.1 "Un módulo es un archivo que contiene funciones, campos, propiedades y tipos"

**Ya así.** `Language-Syntax.md` §2.1 (líneas 145-158): no hay keyword `module`; un módulo es
justo lo que puede declarar un archivo — campos, propiedades, métodos, clases, enums —
descrito también en `CLAUDE.md` ("A module can contain fields, properties, methods, classes and
enums"). En runtime, `SurtrModule` (`src/Surtr.Core/Runtime/Classes/SurtrModule.cs:21-232`) tiene
exactamente esas cinco tablas (`_fields`, `_properties`, `_methods`, `_classes`, `_interfaces`).

### 1.2 "Los módulos se organizan en directorios desde un directorio raíz"

**Ya así.** `Language-Syntax.md` §2.1 líneas 151-158: el path de un módulo se deriva de la
ubicación del archivo relativa a un source root configurado — igual que Go deriva un paquete de
su directorio. Implementado en `src/Surtr.Compiler/Compilation/ModulePath.cs` (`TryDerive`):
normaliza ambos paths, exige que el archivo esté bajo el source root, valida cada segmento como
identificador válido y los une con `.`.

### 1.3 "El nombre completo es la concatenación de directorios + nombre del módulo"

**Ya así**, mismo mecanismo que 1.2. `Ogame/core/Entity.surtr` → módulo `Ogame.core` (el nombre
del *archivo* no forma parte del path del módulo — varios archivos en el mismo directorio
contribuyen al mismo módulo, `Language-Syntax.md` línea 157).

### 1.4 Las cuatro formas de `import` — **verificado empíricamente, no solo leído en la documentación**

No me fié de que la documentación describiera correctamente el comportamiento real: además de
correr la suite completa (`dotnet build Surtr.sln` en verde, `dotnet test Surtr.sln` →
**2132/2132 en verde**, 0 fallos) y leer el cuerpo real de los tests de imports (no solo sus
nombres — confirmado que son compilaciones de punta a punta contra una `SurtrRuntime` real, con
casos positivos y de rechazo, no *mocks*), escribí y ejecuté tres pruebas nuevas y desechables
directamente contra `SurtrCompilation`/`Binder`, fuera de la suite existente, para las formas más
ambiguas de tu propia especificación. Encontré **un hueco real**, corregido en la tabla:

| Forma pedida | Sintaxis | Estado real | Dónde |
|---|---|---|---|
| Módulo completo a scope, **sin `.*`** | `import game.entities;` (siendo `entities` un directorio, no un tipo) | **No compila** — `SURTR3003: No module provides 'game.entities'.` Confirmado con una prueba nueva contra el binder real, no es una lectura de la documentación. | ver "Hallazgo" abajo |
| Un símbolo | `import game.entities.Foo;` | Hecho (base del sistema) — confirmado con prueba nueva y con la suite existente | `Language-Syntax.md` §2.1 líneas 170-172 |
| Varios símbolos | `import game.entities.{Foo, Bar};` | **Hecho — Fase 8**, confirmado en la suite (3 tests, región línea 195) | `ImportSyntax.Members`, `Parser.ParseImportMemberList` |
| Alias de módulo | `import game.entities as GameData;` | **Hecho — Fase 7**, confirmado en la suite (4 tests, región línea 150) | `ImportSyntax.Alias`, `Scope.TryDeclareModuleAlias` |
| Wildcard de directorio (equivalente real de "módulo completo") | `import game.entities.*;` | **Hecho — Fase 9, y recursivo**, confirmado en la suite (5 tests, región línea 234) | `Binder.ImportWildcardModule` + `ModulesUnderPrefix` |

#### Hallazgo: "importar un módulo completo sin `.*`" no es una forma real del lenguaje hoy

Tu especificación (punto 1) pedía `import <module-path>;` a secas como forma de traer un módulo
entero a scope, con el ejemplo `game.Entities` refiriéndose *al archivo* `Entities.surtr`. Dos
cosas no cuadran con el sistema real, verificadas ambas con pruebas directas contra el binder:

1. **El nombre de archivo no es parte del path del módulo — solo los directorios lo son**
   (`ModulePath.cs`, `Language-Syntax.md` línea 157). `game/Entities.surtr` pertenece al módulo
   `game`, no a un módulo `game.Entities` — así que `import game.Entities;` en ese caso no es "el
   módulo completo", es el import de nombre único ya existente (`Language-Syntax.md` §2.1 línea
   170: *"A named import brings exactly that one type into unqualified scope"*), resolviendo
   `Entities` como el nombre de la clase declarada ahí. Probado: compila y funciona, pero es la
   forma 2 (símbolo único), no la forma 1.
2. **Cuando el path completo sí es un módulo real** (un directorio, p. ej. `game/entities/` con
   archivos dentro) **y no queda ningún segmento para tratar como tipo, `import game.entities;`
   falla** con `SURTR3003: No module provides 'game.entities'.` — probado directamente contra
   `Binder`/`SurtrCompilation`, reproducido de forma determinista. La rama de import de nombre
   único (`Binder.BindImports`, buscando "prefijo de módulo más largo + nombre de tipo restante")
   nunca contempla el caso "no queda ningún nombre de tipo porque el path entero ya es el
   módulo" — ni cae a un comportamiento equivalente al wildcard, ni reporta un diagnóstico más
   claro que "no existe tal módulo" (que es engañoso: el módulo sí existe, solo que no hay type
   name que emparejar).

**No es una regresión de una feature que se rompió** — ninguna línea de `Language-Syntax.md` §2.1
muestra realmente un quinto ejemplo `import Ogame.core;` a secas (solo los cuatro que ya
documentaba la Parte 1 original de este informe); la forma "traer todo un módulo sin calificar"
que sí está especificada y funciona es literalmente `import Ogame.core.*;` — el wildcard **es**
la forma de "módulo completo" del lenguaje tal y como existe hoy, y ya lo cubre por completo (Fase
9). El desajuste está entre el ejemplo de tu punto 1 (sin `.*`) y ese hecho.

**Pregunta abierta, no resuelta aquí a propósito**: ¿quieres que se añada `import ModulePath;`
(sin símbolo, sin `.*`, sin alias) como azúcar sintáctica equivalente a `import ModulePath.*;`
cuando el path completo resuelve a un módulo real y no a `<módulo>.<Tipo>`? Sería una fase
pequeña (una rama nueva en `Binder.BindImports`: si la resolución de nombre único falla por
"no queda tipo, el path entero ya es un módulo", delegar en el mismo camino que ya usa el
wildcard) — pero es una decisión de sintaxis del lenguaje, no una corrección de bug, así que la
dejo pendiente de tu confirmación en vez de añadirla ya al plan.

Detalles que conviene que quede constando, porque afinan tu propia especificación:

- **El wildcard es recursivo por diseño**, no solo un nivel: `import game.*;` trae las
  declaraciones propias de `game` (si las tiene) **y** las de `game.entities`,
  `game.entities.parts`, etc., a cualquier profundidad — porque un módulo es un directorio, así
  que `game` y `game.entities` son módulos distintos, no uno anidado en el otro
  (`Language-Syntax.md` líneas 176-185).
- **El alias es deliberadamente más estrecho que "traer el módulo como valor"**: no expone el
  módulo como algo que el programa pueda mantener, pasar o almacenar — es una reescritura de
  qualifier en tiempo de compilación (`Core.Entity` se lee exactamente como `game.Entities.Entity`
  en cualquier posición donde se nombre un tipo). Esto es clave para el diseño de `moduleof`: hoy
  Surtr **no tiene ningún valor de primera clase que represente un módulo** — `moduleof` sería la
  primera vía real de obtener uno.
- **Import selectivo y alias solo alcanzan tipos**, nunca una función o variable de nivel de
  módulo — solo el wildcard trae también funciones/variables de módulo a scope sin calificar.
- Una colisión de nombre se diagnostica en el **punto de uso**, salvo dos alias iguales, que se
  diagnostican en la propia línea `import` (código `DuplicateModuleAlias = 3053`) porque un alias
  no tiene un import propio al que hacer *shadow*.
- Cobertura de tests: región de imports en
  `src/Surtr.Tests/Compiler/CodeGen/ModuleEmitterTests.cs` (líneas ~150-280), más
  `SurtrCompilationTests.cs`, `ModulePathTests.cs`, `BinderTests.cs` (shadowing, colisión con
  stdlib) y `LanguageServerWorkspaceTests.cs` (autocompletado/hover/definición sobre las tres
  formas).

**Conclusión de esta parte**: de las cuatro formas de import pedidas, **tres están completas y
verificadas de punta a punta** (símbolo único, lista selectiva, alias) y una cuarta —"módulo
completo a scope"— **está cubierta funcionalmente por `import ModulePath.*;`**, no por la sintaxis
sin `.*` que usaba tu ejemplo, que hoy no compila cuando el path entero es un módulo real (ver el
hallazgo arriba). No hace falta ninguna fase de implementación salvo que confirmes que quieres la
forma sin `.*` como azúcar sintáctica nueva — en ese caso es una fase pequeña y autocontenida,
descrita arriba, independiente del plan de `moduleof` de la Parte 3. Si en algún otro momento
observas un comportamiento que difiere de lo descrito aquí (fuera de este hallazgo concreto), es
un **bug de regresión**, no una feature pendiente — merece un reporte con el snippet exacto.

### 1.5 `moduleof` — no existe nada, ni en código ni en documentación

Ninguno de los documentos (`Language-Syntax.md`, `Runtime-Model.md`, `Module-Format.md`,
`Compiler-Plan.md`, ni el propio `Plan-Sintaxis-Imports-Atributos-LSP.md`) menciona
`moduleof`/`ModuleOf` en ningún sitio. Es diseño y trabajo enteramente nuevos.

Hay, sin embargo, un patrón arquitectónico completo y muy reciente para copiar: **`typeof`**
(Fase 13 del plan de imports, cerrada). Es la plantilla de las 9 capas por las que pasa un
operador de reflexión en este compilador, de arriba abajo:

1. **Keyword reservada** en el lexer (`typeof`, `Syntax/Lexer.cs`/`TokenType.cs`).
2. **Nodo AST** con dos formas posibles — estática (`TypeOperand: TypeSyntax?`) e instancia
   (`Operand: ExpressionSyntax?`) — `TypeOfExpressionSyntax`
   (`Syntax/Ast/ExpressionSyntax.cs:346-368`).
3. **Parser** con lookahead mínimo para decidir cuál de las dos formas aplica
   (`Parser.ParseTypeOf`, `Syntax/Parser.Expressions.cs:516-533`).
4. **Binder** que resuelve contra el tipo built-in correspondiente y liga tipo-antes-que-valor
   (`BodyBinder.BindTypeOf`, `Binding/BodyBinder.Expressions.cs:3246-3273`).
5. **Nodo del bound tree** dedicado (`BoundTypeOfExpression`).
6. **Opcode(s) nuevos**, no una llamada nativa oculta: `LoadType`/`LoadTypeX` (forma estática,
   lee el pool de tipos del chunk) y `GetTypeOfValue` (forma instancia, lee `.Class` del valor en
   pila) — `OpCode.cs:997-1020`, valores `0xDD`-`0xDF` (el **último** tramo libre antes de
   `0xE0`).
7. **Clase runtime wrapper**: `SurtrTypeValue` (`Runtime/Objects/SurtrTypeValue.cs`) — un
   `SurtrObject` con un campo CLR plano (`Wrapped: SurtrTypeInfo`) apuntando directo a metadata
   compartida, sin pasar por slots de `SurtrValue` ni por el entity registry de la metadata (que
   nunca se registra — ver `CLAUDE.md`, "Class metadata is never registered with the entity
   registry").
8. **Caché por runtime**, rooteada permanentemente, con igualdad por referencia
   (`SurtrContext`/`SurtrRuntime.GetOrCreateTypeValue`).
9. **API nativa complementaria** en `Runtime/BuiltIns/SurtrReflectionBuiltIns.cs` (`Type.of(...)`,
   `.name`, `.members()`, `.attributes()`, ...) — deliberadamente **de solo enumeración**, sin
   leer ni invocar el valor de un miembro.

Este patrón se reutiliza casi 1:1 para `moduleof` en la Parte 2, con una diferencia
arquitectónica importante detectada al investigar: **`SurtrModule` no es metadata "muda" como
`SurtrClass`/`SurtrInterface`** — es un `SurtrRuntimeEntity` de pleno derecho, con
`VisitReferences` propio (`SurtrModule.cs:21`, `288-307`), así que ya vive registrado y trazado
por el colector. Esto simplifica el diseño del wrapper frente a `SurtrTypeValue`, no lo complica
(ver §2.3).

---

## Parte 2 — Plan: `import ModulePath;` como azúcar del wildcard

Confirmado con el usuario: se quiere esta forma añadida como azúcar sintáctica, equivalente
exacto a `import ModulePath.*;`, para el caso que hoy falla (§1.4 de la Parte 1).

### Objetivo

`import a.b.c;` (sin símbolo final, sin `.*`, sin `as`, sin `{}`) se comporta exactamente como
`import a.b.c.*;` — unión recursiva de las declaraciones propias del módulo exacto **y** de cada
submódulo (Fase 9 del plan de imports), funciones/variables de módulo incluidas — en el único
caso en que hoy no significa nada: cuando el path completo ya resuelve a un módulo real y no deja
ningún segmento que pueda ser un nombre de tipo. **No** es una fase nueva de diseño de gramática:
`import a.b.c;` ya parsea exactamente así hoy (es la misma forma sintáctica que el import de un
símbolo — `ImportSyntax` no distingue "nombre de tipo" de "nombre de módulo", esa lectura ocurre
enteramente en el binder). El cambio entero vive en la resolución, no en el parser ni en el AST.

### Diseño — reutiliza el algoritmo existente, no introduce uno nuevo

`Binder.BindImports`, rama de import de nombre único, ya busca "el prefijo de módulo más largo
que resuelva" y trata lo que sobra del path como un nombre de tipo (posiblemente anidado, vía
`TryResolveFromModule`). El caso sin manejar es exactamente cuando ese prefijo más largo consume
el path **entero**, dejando cero segmentos para un tipo — hoy eso cae al diagnóstico
`SURTR3003` ("no existe tal módulo", que además es engañoso: el módulo sí existe, solo que no
queda ningún tipo que emparejar con él). Con el cambio, ese caso concreto delega en exactamente
el mismo camino que ya usa la rama wildcard, `ImportWildcardModule` (Fase 9), sobre ese path
exacto. Ninguna otra rama cambia de comportamiento.

- **Sin cambios en `Syntax/Parser.cs`/`Syntax/Ast/DeclarationSyntax.cs`**: cero trabajo de
  gramática, ver arriba.
- **`Binder.BindImports`** (rama de nombre único): tras agotar la búsqueda de "prefijo de módulo
  + tipo restante" sin encontrar un tipo válido, comprobar si el path completo resuelve por sí
  mismo como módulo (mismo índice de módulos que ya consulta la rama wildcard) — si sí, delegar
  en `ImportWildcardModule` sobre ese path exacto; si no, mantener el diagnóstico de error actual
  sin cambios (el path no es ni un tipo alcanzable ni un módulo).
- **`SurtrCompilation.BuildDependencyGraph`/`TryResolveImport`** (validación previa al binder,
  que hoy asume "el import de nombre único siempre tiene un tipo al final" — el mismo supuesto
  que ya hubo que romper en las Fases 7 y 8 para alias/lista selectiva, mismo motivo aquí):
  probar primero si el path completo coincide con un módulo real (exacto y/o con submódulos,
  reutilizando el mismo camino que ya usa la resolución de dependencias del wildcard) antes de
  asumir que el último segmento es un tipo; si coincide, registrar la(s) arista(s)
  correspondiente(s) igual que ya hace la rama wildcard, en vez de fallar antes de llegar al
  binder.
- **Prioridad cuando ambas lecturas podrían aplicar** (el path completo resuelve como módulo,
  pero un prefijo más corto + un tipo con ese nombre restante *también* resolvería): se mantiene
  "el prefijo de módulo más largo gana", el mismo criterio que el algoritmo ya usa hoy para
  elegir entre prefijos de distinta longitud — no hace falta ningún criterio de desempate nuevo,
  solo extender el criterio existente al caso límite de "el prefijo más largo posible es el path
  entero". Documentar este matiz explícitamente en `Language-Syntax.md` §2.1, porque es real y no
  necesariamente obvio para quien lea el ejemplo nuevo.
- **LSP**: confirmar que `CompletionProvider`/`SymbolResolver` ya manejan bien un import de
  nombre único plano cuyo path resulta ser un módulo (deberían, reutilizando el mismo camino que
  ya recorre el wildcard) — si no, mismo tipo de ajuste puntual que ya hizo falta en la Fase 12
  del plan de imports para las otras tres formas.

### Tests

- **Regresión del caso existente**: `import game.entities.Foo;` (tipo real al final) sigue
  funcionando exactamente igual que hoy — no debe cambiar.
- `import game.entities;` donde `entities` es un directorio con archivos propios — trae sus
  declaraciones sin calificar (mismo patrón de assert que
  `ADirectoryWildcardReachesBothTheModulesOwnTypesAndItsSubmodules`, sin el `.*`).
- `import game.entities;` donde `entities` es un directorio **sin** archivos propios, solo con
  submódulos anidados (`game/entities/parts/...`) — alcanza los submódulos recursivamente, el
  mismo caso límite que motivó la Fase 9 original
  (`ADirectoryWildcardReachesEverySubmoduleWhenTheDirectoryHasNoFilesOfItsOwn`, sin el `.*`).
- Funciones/variables de nivel de módulo también llegan sin calificar (mismo criterio que ya
  tiene el wildcard).
- Un módulo hermano (`game.other`) no se cuela.
- El diagnóstico `SURTR3003` ya no aparece para este caso concreto — sustituye mi reproducción
  manual desechable de la Parte 1 por un test permanente.
- Si es construible sin forzarlo: un caso real donde compiten "prefijo más largo = path entero,
  como módulo" contra "prefijo más corto + tipo restante con ese nombre" — confirmando por test
  explícito que gana el más largo, en vez de dejar el comportamiento solo documentado en prosa.

### Documentación

`Language-Syntax.md` §2.1 gana un quinto ejemplo junto a los cuatro que ya tiene
(`import Ogame.core;` trayendo el módulo completo sin `.*`, con una nota de que es equivalente
exacto a `import Ogame.core.*;`, y la regla de "prefijo de módulo más largo gana" cuando ambas
lecturas podrían aplicar).

### Commit

`Feature: import de modulo completo sin wildcard (import ModulePath; equivalente a import ModulePath.*;)`

---

## Parte 3 — Decisiones de diseño ya confirmadas

Antes de fijar las fases se confirmaron contigo tres decisiones, siguiendo la misma práctica que
ya usó este repo para `attribute` (Fase 5) y el paso de método a closure (Fase 4) — confirmar
alcance antes de programar evita rehacer fases:

1. **Solo forma estática.** `moduleof <ModulePath>` resuelve enteramente en compilación, contra
   un module path literal (o un alias resuelto contra él) — **no** hay forma de instancia sobre
   un valor arbitrario (`moduleof someValue`). Esto elimina de raíz la ambigüedad tipo-vs-valor
   que sí tiene `typeof`, y evita el problema real de que `SurtrClass` no guarda hoy un
   back-pointer a su `SurtrModule` declarante (habría que añadirlo solo para la forma de
   instancia, que queda fuera de alcance).
2. **Superficie del objeto `Module` devuelto**: `path: string`, `classes()`/`interfaces()`
   (arrays de `Type`, igual que ya expone `SurtrModule.Classes`/`.Interfaces`), `members()`
   (funciones y variables de nivel de módulo, como `Member[]`) y `submodules()` (módulos
   cargados en este runtime cuyo path empieza por `<este path>.`).
3. **Un alias de import es un argumento válido**: `import game.Entities as GameData; moduleof
   GameData;` debe comportarse exactamente como `moduleof game.Entities;` — coherente con que un
   alias ya es una reescritura de qualifier en cualquier otra posición donde se nombre un tipo.

---

## Parte 4 — Plan de implementación de `moduleof`, fase a fase

Cada fase termina en su propio commit, solo tras build + suite en verde, y no empieza la
siguiente sin cerrar la anterior — mismo protocolo que el resto de fases de este repo.

### Fase 1 — Sintaxis: keyword, AST, parser

**Objetivo**: `moduleof <path>` y `moduleof <alias>` parsean a un nodo AST propio.

- `moduleof` como **palabra reservada de verdad** (no contextual) — a diferencia de `typeof` no
  necesita doblar como identificador en ningún otro contexto, y al no tener forma de instancia no
  compite con ninguna expresión. Añadir a `Syntax/Lexer.cs`, `Syntax/TokenType.cs`, y a la lista
  autoritativa de `Language-Syntax.md` §1.2.
- Nuevo `ModuleOfExpressionSyntax` en `Syntax/Ast/ExpressionSyntax.cs`: carga un
  `IReadOnlyList<string> Path` (la misma forma de ruta punteada que ya usa `ImportSyntax.Path`) —
  **no** un `TypeSyntax`/`ExpressionSyntax`, porque al ser solo-estática no hay nada más que
  decidir en el parser.
- `Parser.ParseModuleOf()`: consume `moduleof`, reutiliza el mismo bucle de
  `Identifier ('.' Identifier)*` que ya usa `ParseImport` para la parte de ruta — cero lookahead
  especial, a diferencia de `ParseTypeOf`.
- Sin cambios en `ParseStatement`: es una expresión, entra por el punto de entrada normal de
  expresiones primarias (`moduleof` como token que abre una expresión, igual que `typeof`).

**Tests**: parseo de `moduleof a.b.c;` como expresión-sentencia y dentro de una asignación;
`moduleof` sin ruta reporta el diagnóstico sintáctico esperado en vez de colgar el parser.

### Fase 2 — Binder: resolver el path (o alias) contra los módulos conocidos

**Objetivo**: `BodyBinder` liga `ModuleOfExpressionSyntax` a un `BoundModuleOfExpression` con el
path de módulo ya resuelto y validado.

- Reutilizar exactamente la resolución que `Binder.BindImports` ya hace para el path completo de
  un import con alias/lista selectiva (`TryResolveFromModule`/el diccionario `_modules` del
  binder) — **no** crear un segundo mecanismo de "¿existe este módulo?".
- Resolución de alias: antes de mirar `_modules`, comprobar `Scope.LookupModuleAlias` (el mismo
  diccionario separado de alias que ya usa `TryResolveThroughAlias`) — si el primer segmento de
  la ruta escrita es un alias declarado en el archivo, sustituirlo por el path real que representa
  antes de validar.
- Verificar explícitamente si `surtr` (el módulo built-in) resuelve por esta vía — hoy los
  built-ins están siempre en scope sin necesidad de import, pero no está confirmado que
  `_modules`/el índice de módulos de la compilación incluya una entrada para `surtr` bajo ese
  nombre exacto. Si no la incluye, añadir el caso especial aquí (mismo sitio donde
  `Binder.cs:210-218` ya siembra el scope global con el módulo `surtr`).
- Nuevo diagnóstico si el path/alias no resuelve a ningún módulo conocido — puede reutilizar
  `UnresolvedImport` con mensaje adaptado, o un código nuevo (`3xxx`) si se prefiere distinguirlo
  en tooling; decidir por consistencia con el resto de 3xxx al implementar.
- `BoundModuleOfExpression` (nuevo, `Binding/BoundTree/BoundExpressions.cs`): carga el
  `SurtrModule`-path ya resuelto (string, no un símbolo — el compilador no tiene hoy un
  `ModuleSymbol`, y no hace falta crear uno solo para esto) y `Type = SurtrBuiltIns.Module`
  (resuelto vía `ResolveBuiltInType("Module", ...)`, mismo mecanismo que `typeof` usa para
  `Type`).
- **Grafo de dependencias**: `moduleof OtroModulo;` es una dependencia real del módulo que lo
  escribe, aunque nunca llame a nada de `OtroModulo`. Verificar si el binder ya añade esta arista
  automáticamente al resolver el nombre (el mecanismo genérico que documenta
  `Compiler-Plan.md` — "a fully-qualified name with no import is an edge only the binder
  discovers" — debería cubrirlo sin cambios, pero hay que confirmarlo con un test de un módulo
  cuyo *único* uso de otro sea a través de `moduleof`, para pillar en seco si la arista falta).

**Tests**: `moduleof` sobre un módulo con import previo, sobre un módulo solo referenciado por
nombre completo (sin import), sobre un alias, sobre `surtr`; path inexistente reporta el
diagnóstico esperado; el grafo de dependencias/orden de carga refleja la referencia aunque no haya
ninguna llamada.

### Fase 3 — Runtime: la clase built-in `Module` y su wrapper

**Objetivo**: existe un valor Surtr real de clase `Module`, análogo a `Type`/`Member`.

- `SurtrBuiltIns.cs`: nuevo `public static readonly SurtrClass Module` (mismo patrón que `Type`/
  `Member`, líneas 179/185/268-269 — `DeclareObject("Module")`).
- Nuevo `SurtrModuleValue : SurtrObject` (`Runtime/Objects/SurtrModuleValue.cs`), mismo patrón que
  `SurtrTypeValue`: un campo CLR plano `Wrapped: SurtrModule`. **Nota de diseño concreta**: a
  diferencia de `SurtrClass`/`SurtrInterface`, `SurtrModule` ya es un `SurtrRuntimeEntity` con
  `VisitReferences` propio — ya vive registrado y trazado. Esto no cambia la forma del wrapper
  (sigue siendo un campo CLR crudo, no una re-entrada al entity registry — un módulo cargado vive
  tanto como el runtime, igual que la metadata de clase), pero sí confirma que no hace falta
  preocuparse por que el módulo referenciado se colecte mientras el wrapper esté vivo: ya está
  rooteado por el propio mecanismo de carga de módulos.
- Caché por runtime con igualdad por referencia (`SurtrContext.ModuleValues:
  Dictionary<SurtrModule, SurtrModuleValue>`), y `SurtrRuntime.GetOrCreateModuleValue` — mismo
  patrón que `GetOrCreateTypeValue`.

**Tests**: unitarios de que dos `moduleof` sobre el mismo módulo devuelven el mismo wrapper
(identidad), y que el wrapper sobrevive a una colección (`Collect()`) sin invalidarse.

### Fase 4 — Codegen: opcode(s) y reutilización del pool de módulos existente

**Objetivo**: `moduleof` emite bytecode que resuelve el módulo en O(1) a través de una tabla ya
existente, no de una nueva.

- **Hallazgo clave de la investigación**: el chunk **ya tiene** un `moduleTable: str[]` — "Other
  modules to which this module calls, by path" (`Module-Format.md` §3.3), poblado en
  `SurtrModuleBuilder.cs:292-293` (`_moduleTable`, con dedup) y resuelto a instancias reales de
  `SurtrModule` en `LoadModule` (paso 2, junto al resto de tablas de acceso — ver
  `Module-Format.md` §4). Es el mismo pool que ya usan `CallModule`/`CallModuleX`
  (`SurtrCodeEmitter.OpCodes.cs:990-998`, vía `SurtrEmitTokens.ModuleIndex`). `moduleof` no
  necesita un pool nuevo — necesita **poder pedir un índice en este pool sin que haya una llamada
  de por medio**, así que el helper que hoy arma un `ModuleIndex` a partir de una llamada tiene
  que quedar también alcanzable a partir de solo un path de módulo resuelto.
- Dos opcodes nuevos, siguiendo el patrón `LoadType`/`LoadTypeX` exactamente: `LoadModule`
  (índice de 2 bytes) / `LoadModuleX` (índice de 4 bytes) — próximos valores libres desde `0xE0`
  (el primer tramo realmente libre tras el renumerado final; `typeof` agotó hasta `0xDF`). Cada
  uno lee `frame.Chunk.ModuleTable[idx]` y lo envuelve con `SurtrRuntime.GetOrCreateModuleValue`
  antes de empujarlo. **No hace falta ningún opcode de "instancia"** (el equivalente a
  `GetTypeOfValue`), porque la Fase de diseño descartó la forma de instancia.
- `SurtrCodeEmitter.OpCodes.cs`: `LoadModule(SurtrModuleToken)`/`LoadModuleX(...)`, igual forma
  que `LoadType`/`LoadTypeX` (`SurtrCodeEmitter.OpCodes.cs:505-511`), y un helper de tier 3
  (`SurtrCodeEmitter.Helpers.cs`) que elige entre las dos según si el índice cabe en `ushort` —
  exactamente `LoadTypeOf` (líneas 214-218) pero para módulos.
- Documentar en `docs/Opcodes.md`, familia "Module Access" (junto a `CallModule`/`CallModuleX`,
  ya que comparten el mismo pool), con el mismo formato de tres partes (`Encoding`/`Stack`/
  `Notes`) que exige `CLAUDE.md`.
- Actualizar `SurtrBytecodeDisassembler.cs` (mismo patrón que las líneas 1031-1039 de `LoadType`/
  `LoadTypeX`, reutilizando el helper de líneas 1288 que ya resuelve `chunk.ModuleTable[idx]` para
  mostrar el path).

**Tests**: opcode-level en `src/Surtr.Tests/VM` (la suite que exercita el layout de bytes
directamente, no vía emitter, según convención de `CLAUDE.md`) para `LoadModule`/`LoadModuleX`,
más `OpCodeValueTests.cs` fijando los nuevos valores.

### Fase 5 — API nativa: `Module.path`, `.classes()`, `.interfaces()`, `.members()`, `.submodules()`

**Objetivo**: superficie de solo-enumeración sobre `Module`, mismo patrón y mismas restricciones
deliberadas que `Type`/`Member` (nunca lee ni invoca el valor de un miembro).

Nuevo `Runtime/BuiltIns/SurtrModuleReflectionBuiltIns.cs` (o una región dentro de
`SurtrReflectionBuiltIns.cs` si se prefiere no triplicar archivos — decidir al implementar según
cuánto crezca), con `DeclareModule(SurtrBuiltInTypeBuilder builder)` registrado desde
`SurtrBuiltIns.cs` junto a `DeclareType`/`DeclareMember` (línea ~327).

- **`path: string`** — trivial, `SurtrModule.Path` ya existe.
- **`classes(): Type[]`** / **`interfaces(): Type[]`** — recorren `SurtrModule.Classes`/
  `.Interfaces` (ya expuestos como `Dictionary<...>.ValueCollection`) y envuelven cada uno con
  `SurtrRuntime.GetOrCreateTypeValue` (reutilizando el wrapper de `typeof`, no uno nuevo — un
  `Type` sigue siendo un `Type` venga de `typeof` o de `Module.classes()`).
  Deliberadamente **separado** de `members()`: `SurtrModule` ya distingue tipos anidados de
  fields/properties/methods en tablas propias, así que replicar esa separación evita reinventar
  la deduplicación que `Type.members()` sí necesita para no listar dos veces el backing field de
  una auto-property.
- **`members(): Member[]`** — funciones y variables de nivel de módulo (`SurtrModule.Fields`,
  `.Properties`, `.Methods`), envueltos con el mismo `SurtrMemberValue` que ya usa
  `Type.members()`. Reutilizar la misma regla de deduplicación (excluir accessors sintéticos de
  propiedad, excluir cualquier nombre `$...`) documentada en la Fase 6 del plan de imports.
  **Punto a decidir en la propia fase**: `Member.kind` hoy distingue
  `field/property/method/class/enum/interface` a nivel de *miembro de clase* — confirmar que un
  campo/función de módulo no necesita un `kind` nuevo (`"field"`/`"method"` deberían bastar, ya
  que la distinción "es de módulo, no de clase" ya la da `Member.declaringType` siendo `null` o
  apuntando a un `Type` sintético — decidir cuál de las dos al implementar, ya que `Member` no
  contempla hoy un `declaringType` nulo).
- **`submodules(): Module[]`** — **no hace falta ningún índice nuevo**: `SurtrContext.Modules`
  (`SurtrContext.cs:40`) ya es un `Dictionary<string, SurtrModule>` de todo módulo cargado en
  *este* runtime, así que basta un recorrido lineal filtrando por el prefijo `<path>.` — mismo
  razonamiento de coste que ya justificó la Fase 9 del plan de imports para el wildcard recursivo
  ("el número de módulos de un proyecto no justifica una estructura de índice dedicada"), aplicado
  ahora en runtime en vez de en compilación.

**Tests**: 9-10 en `ModuleEmitterTests.cs` siguiendo el estilo de la región "Reflexion de
atributos: Type/Member" — `moduleof` sobre el propio módulo, sobre un módulo importado con nombre
completo, sobre un alias, sobre `surtr`; `classes()`/`interfaces()` devuelven exactamente lo
declarado ahí (ni heredado ni de submódulos); `members()` incluye función y variable de módulo,
excluye sintéticos; `submodules()` sobre un módulo con hijos anidados a más de un nivel, vacío en
un módulo hoja; identidad de `Type` estable entre `typeof` y `Module.classes()` sobre la misma
clase.

### Fase 6 — Búsqueda dinámica por nombre en runtime: `get`/`tryGet` en `Module` y en `Type`

**Objetivo**: además de `moduleof <path>` (resuelto en compilación, contra un path literal),
poder pedirle al runtime un `Module` o un `Type` **a partir de un string calculado en
ejecución** — el equivalente dinámico de `moduleof`/`typeof`. Añadido a petición explícita, no
estaba en el alcance original de este documento.

Cada clase gana dos métodos estáticos, mismo patrón `nombre`/`try` + `nombre` que
`Dictionary.TryGetValue` en C# pero adaptado a que Surtr no tiene parámetros `out`: la versión
normal **lanza**, la versión `try` **devuelve nulo** (una referencia ya representa la ausencia de
forma nativa — `Language-Syntax.md`, "a reference is its 32-bit payload", así que no hace falta
envolver nada en un `?` para un tipo referencia).

- **`Module.get(path: string): Module`** — lanza `KeyNotFoundException` si no hay ningún módulo
  cargado en este runtime bajo ese path exacto.
- **`Module.tryGet(path: string): Module?`** — devuelve `null` en el mismo caso, sin lanzar.
- **`Type.get(name: string): Type`** — lanza `KeyNotFoundException` si el nombre no resuelve a
  ninguna clase/interfaz conocida.
- **`Type.tryGet(name: string): Type?`** — devuelve `null` en el mismo caso.

**`KeyNotFoundException` ya existe** en la jerarquía de excepciones de la stdlib
(`Language-Syntax.md` §13, línea 2486) y encaja exactamente con esta semántica — no hace falta
ninguna clase de excepción nueva.

**Diseño y reutilización de código, por clase**:

- **`Module.get`/`tryGet` es trivial**: `SurtrContext.Modules` (`SurtrContext.cs:40`) ya es un
  `Dictionary<string, SurtrModule>` de todo módulo cargado en este runtime, indexado exactamente
  por el mismo path que acepta `moduleof`. El cuerpo nativo es un `TryGetValue` + (lanzar u
  envolver con `GetOrCreateModuleValue`, Fase 3). Nótese que el módulo built-in `surtr` **no**
  vive en `_context.Modules` (`SurtrRuntime.cs:571-581` ya documenta por qué: es de proceso, no de
  runtime) — `Module.get("surtr")` necesita el mismo caso especial que ya tiene
  `TryResolveHandle` para no fallar en falso ahí.
- **`Type.get`/`tryGet` reutiliza la resolución de descriptores que ya existe para cargar
  módulos**, en vez de escribir un segundo parser: `SurtrRuntime.TryResolveHandle`
  (`SurtrRuntime.cs:538-600`) ya hace exactamente "descriptor → `SurtrClassReference` →
  `SurtrTypeInfo`" para cada tipo que un módulo menciona al cargar — primitivos/built-ins,
  nativos, y objeto (con el mismo camino de `TrySplitFullName` + `_context.Modules` +
  `module.FindClass`/`TryGetInterface` que usaría esto). Solo trabaja hoy a partir de un
  `SurtrTypeHandle` ya construido en compilación; para `Type.get` hace falta arrancar desde un
  `string` puesto en runtime. Refactor propuesto, de bajo riesgo: extraer el cuerpo de
  `TryResolveHandle` desde la línea 554 (`if (!reference.TryGetFullName(...))`) a un método
  reutilizable `TryResolveReference(SurtrClassReference reference, out SurtrTypeInfo? resolved)`,
  llamado tanto por `TryResolveHandle` (sin cambiar su comportamiento) como por el nuevo cuerpo
  nativo de `Type.get`, que arranca con `SurtrClassReference.FromDescriptor(name)` (ya público,
  `SurtrClassReference.cs:485`) antes de llamarlo.
- **A decidir/confirmar antes de implementar esta fase**: el `name` que acepta `Type.get` es el
  **descriptor canónico** (`Ogame.core:Entity;`, `AI` para `int[]`, `` Obox:Box`1;I `` para
  `Box<int>`) — no un nombre "bonito" con genéricos entre `<>`. Es la opción de coste cero (cero
  parsing nuevo, generics incluidos gratis porque el descriptor ya los codifica), pero
  `CLAUDE.md` es explícito en que el descriptor es una forma interna/canónica y
  `ToDisplayString()` existe "purely for diagnostics — never key off it" — exponer el descriptor
  como argumento de una API pública que un programa Surtr llama en runtime es una superficie algo
  más cruda que el resto de la API de reflexión (`Type.name`, `Module.path`, etc., que sí son
  nombres legibles). La alternativa (aceptar un nombre completo sin genéricos, tipo
  `game.entities.Entity`, y rechazar explícitamente cualquier cosa parametrizada) es más amigable
  pero exige mantener un segundo camino de resolución solo para ese subconjunto. Recomendación:
  empezar por el descriptor (reutiliza el 100% del mecanismo existente) y revisar si hace falta
  una forma más amigable una vez haya un caso de uso real que lo pida.

**Tests**: `Module.get`/`tryGet` sobre un módulo cargado, sobre `surtr`, sobre un path
inexistente (lanza / devuelve `null` respectivamente); `Type.get`/`tryGet` sobre un primitivo, una
clase de usuario, un array (`AI`), una construcción genérica (`` Obox:Box`1;I ``), un descriptor
mal formado o inexistente (lanza / devuelve `null`); identidad estable entre `Type.get(d)` y
`typeof` sobre el mismo tipo, y entre `Module.get(p)` y `moduleof` sobre el mismo path.

### Fase 7 — LSP

**Objetivo**: `moduleof` no queda desincronizado del resto del tooling — el propio historial de
este repo documenta que es exactamente el tipo de regresión silenciosa que ocurre si no se toca
`CompletionProvider`/`SymbolResolver` a la vez que el binder (Fase 12 del plan de imports).

- `CompletionProvider.Keywords`: añadir `moduleof`.
- Autocompletado de la ruta tras `moduleof `: reutilizar el mismo `ImportedModules`/resolución de
  alias que ya usa el completado de una línea `import` — es estructuralmente la misma tarea
  (completar un path de módulo).
- Hover sobre `moduleof <path>`: mostrar qué módulo resuelve, mismo formato que el hover sobre un
  tipo importado.

**Tests**: 2-3 en `LanguageServerWorkspaceTests.cs`, mismo estilo que los ya existentes para
alias/selectivo/wildcard.

### Fase 8 — Documentación

- `Language-Syntax.md`: nueva subsección (§2.1 bis o nueva sección junto a §11, decidir según
  dónde encaje mejor con "reflexión" ya cubierta ahí) describiendo `moduleof`, su forma única
  (estática), y la API de `Module` (incluyendo `get`/`tryGet`). **Corregir explícitamente** la
  frase actual de §2.1 línea 191-192 ("Surtr has no first-class module value") — con `moduleof`
  deja de ser cierta strictu sensu para el *resultado* de `moduleof` (aunque el *alias* de import
  sigue sin serlo, que es la distinción real a documentar).
- `docs/Opcodes.md`: familia "Module Access" con `LoadModule`/`LoadModuleX`.
- `docs/Runtime-Model.md`: mención de `SurtrModuleValue` junto a `SurtrTypeValue` en la sección de
  reflexión, y de que `SurtrModule` ya era un entity registrado antes de esto (aclarar que no es
  un cambio de esta feature, solo pasa a ser observable desde Surtr); documentar también
  `TryResolveReference` como el punto único de resolución de descriptor→tipo, ahora con dos
  llamadores (carga de módulo y `Type.get`).
- `docs/Module-Format.md`: si `LoadModule`/`LoadModuleX` amplían el uso del `moduleTable` a
  "módulos nombrados, no solo llamados", una nota en §3.3 evita que quien lea la tabla asuma que
  toda entrada ahí corresponde a una llamada real.
- Actualizar también la documentación existente de `Type` (donde viva hoy la de `typeof`/`Type.of`)
  con `Type.get`/`Type.tryGet` y la decisión tomada sobre el formato de `name` (Fase 6).

### Fase 9 — Suite completa y cierre

- Build + `dotnet test Surtr.sln` en verde.
- Commit único cerrando la fase, siguiendo el formato ya usado: `Feature: operador moduleof,
  reflexion de modulos y busqueda dinamica por nombre (Module/Type.get/tryGet)`.

---

## Resumen para seguimiento

Dos planes independientes en este documento — no comparten código ni orden de dependencia entre
sí, pueden implementarse en cualquier orden relativo:

**Parte 2 — `import ModulePath;` sin `.*`** (una sola fase, autocontenida):

| Fase | Contenido | Depende de |
|---|---|---|
| única | Binder + dependencia previa al binder: path completo como módulo cuando no queda tipo restante, delegando en el mismo camino que el wildcard | — |

**Parte 4 — `moduleof`:**

| Fase | Contenido | Depende de |
|---|---|---|
| 1 | Keyword, AST, parser | — |
| 2 | Binder: resolución de path/alias, dependencia | 1 |
| 3 | Runtime: clase `Module`, `SurtrModuleValue`, caché | — (paralelizable con 1-2) |
| 4 | Codegen: `LoadModule`/`LoadModuleX`, reutilización de `moduleTable` | 2, 3 |
| 5 | API nativa: `path`/`classes()`/`interfaces()`/`members()`/`submodules()` | 3 |
| 6 | `Module.get`/`tryGet` y `Type.get`/`tryGet` (búsqueda dinámica por nombre) | 3, 5 |
| 7 | LSP | 1, 2 |
| 8 | Documentación | 4, 5, 6 |
| 9 | Suite + commit de cierre | todas |

De las cuatro formas de import de la especificación original, tres (símbolo único, lista
selectiva, alias) no tienen trabajo pendiente — verificado con pruebas directas contra el binder,
no solo con la documentación. La cuarta (módulo completo) tiene su hueco cerrado por el plan de
la Parte 2. Si en algún otro momento observas un caso que no coincide con lo descrito en la
Parte 1, trátalo como bug de regresión y repórtalo con el snippet exacto, no como diseño nuevo.
