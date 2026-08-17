# Plan: closures como valores, sintaxis `=>`, modificadores por accessor, atributos, imports, tipos locales, STDLIB seleccionable y LSP

Este documento registra el plan para doce propuestas investigadas a partir de las preguntas
planteadas por el usuario sobre el estado actual del compilador, el runtime y el Language
Server. Cada fase es independiente, se implementa **una a una**, termina en su propio commit
solo tras build + suite en verde, y no empieza la siguiente sin cerrar la anterior. Sigue el
mismo formato que `docs/Plan-Globales-Nativos-Inline-Operadores.md`.

## Resumen de la investigación

| # | Propuesta | Veredicto | Alcance |
|---|---|---|---|
| 1 | Bug reportado: interfaces built-in (`IIterable<T>`, `IIterator<T>`, ...) no reconocidas al implementarlas/extenderlas/usarlas | **No reproducido en HEAD** (`develop`, commit `0bef8a2`). Todo indica que ya lo arreglaron los dos últimos commits (`5cca11a`, `0bef8a2`) — ver detalle abajo. Pendiente de confirmación con el usuario | Por confirmar |
| 2 | Asignar una función a una variable/campo/parámetro de tipo closure solo por su nombre, sin lambda explícita | Ausente. `SurtrClosure` ya soporta un closure de cero capturas sobre cualquier `SurtrMethodInfo`; falta la conversión en el binder | Medio |
| 3 | Métodos y propiedades de solo lectura con `=>` | Ausente. `FatArrow` ya existe como token (solo lo usan las lambdas); es azúcar sintáctica pura | Pequeño |
| 4 | Modificadores (`inline`, `forceinline`, `override`, etc.) independientes por `get`/`set` | Ausente. Hoy `WireAccessors` copia un único juego de modificadores a ambos accessors, incluida la visibilidad | Pequeño-medio |
| 5 | Atributos declarables con `attribute`, con retención y target | Los atributos ya funcionan de punta a punta (declarar, aplicar, leer desde C#), pero sin keyword `attribute`, sin retención, sin restricción de target y sin API de reflexión desde Surtr | Medio |
| 6 | `operator[]` y operadores como miembros de instancia, no solo estáticos | **Ya implementado** — ver `docs/Plan-Globales-Nativos-Inline-Operadores.md`, Fase C (Ruta A: operadores de instancia con `abstract`/`virtual`/interfaz). Sin trabajo pendiente | — |
| 7 | Varianza de genéricos (`in`/`out`) | Genéricos correctos y completos hoy (§10.1b cerrado, sin TODOs). Varianza está **deliberadamente diferida** en `Language-Syntax.md` §14.4 — no es prioritaria mientras quede pendiente §10.2 (STDLIB en Surtr) | Diferido, no se planifica |
| 8 | Cargador de STDLIB con selección de módulos (sandbox) + enlace nativo portable | El mecanismo de carga (`SurtrStdlib.LoadInto`) ya existe pero es todo-o-nada; el enlace nativo ya es independiente del código fuente `.surtr` en tiempo de ejecución, pero las imágenes no están embebidas como recursos y no hay detección temprana de desincronización | Pequeño-medio |
| 9 | Declarar clases/enums/interfaces/singletons/value classes dentro de un método | Totalmente ausente en parser, AST, binder y emisor | Medio-grande |
| 10 | Import de directorio completo (wildcard recursivo), import selectivo de miembros, alias de módulo | Import de módulo completo y wildcard de un módulo ya existen; selectivo, wildcard de directorio y alias, ausentes | Pequeño-medio (alias/selectivo), medio (wildcard de directorio) |
| 11 | Built-ins siempre disponibles sin import, nunca rotos por imports | **Ya correcto**, con tests dedicados (`BinderTests.cs`) | — |
| 12 | LSP correcto para todo lo anterior, especialmente imports y built-ins | Implementación real sobre el compilador real (no un analizador simplificado), pero sin ningún test propio y con una lista de keywords desincronizada de §1.2 | Transversal a cada fase |

Tres puntos no requieren ninguna fase de implementación:

- **Operadores (#6)** — resuelto por completo el 2026-08-16 (Fases A-F de
  `docs/Plan-Globales-Nativos-Inline-Operadores.md`, 1936/1936 tests en verde).
- **Built-ins siempre disponibles (#11)** — `Binder.cs:210-218` siembra el scope global con
  el módulo `surtr` completo antes que cualquier import de un módulo, y `Scope.Lookup`
  siempre resuelve primero la declaración más interna, así que una declaración de usuario
  hace *shadow* de un built-in sin poder romperlo ni colisionar con él
  (`BinderTests.cs:295-433` cubre shadowing directo, import que colisiona con la stdlib,
  y declaración local que hace shadow de un import). No se propone ningún cambio.
- **Varianza de genéricos (#7)** — `Language-Syntax.md` §14.4 ya la lista como "considerada y
  diferida deliberadamente", y §10.2 (`Compiler-Plan.md`) todavía tiene un único punto
  pendiente — la STDLIB escrita en Surtr — que debería cerrarse antes de añadir maquinaria de
  comprobación de tipos que hoy nadie necesita.

---

## Fase 0 — Red de seguridad del LSP — **primero, antes de tocar el binder**

**Por qué primero**: `Surtr.LanguageServer` reutiliza el compilador real
(`Workspace.cs:62-121` llama a `SurtrCompilation.Create` → `Binder.Bind()` →
`BindBodies()`), pero `CompletionProvider.cs`/`SymbolResolver.cs` están acoplados
directamente a internals del binder (`Binder.Bodies`, `.BodyFiles`, `.GlobalScope`,
`.MemberLookup`, `_importScopes`, etc. — ~1800 líneas). Ninguna de las fases siguientes
(imports, atributos, accessors, tipos locales) puede verificarse en el LSP sin una base de
tests que hoy no existe (`grep -rl "LanguageServer\|Lsp" src/Surtr.Tests` no devuelve nada).

**Cambios**:
- Creado `src/Surtr.Tests/LanguageServer/LanguageServerWorkspaceTests.cs` con cobertura de:
  completado de miembros built-in sin ningún import (`.push`, `.length` sobre `int[]`),
  completado de un tipo traído por import wildcard, diagnóstico de colisión de imports
  reportado en el archivo que usa el nombre ambiguo (no en los imports), coherencia de
  `CompletionProvider.Keywords` con §1.2, y hover/definición sobre un tipo importado.
- Corregido `CompletionProvider.Keywords` (`CompletionProvider.cs:225-233`): quitado `new`
  (§1.2 dice explícitamente que no existe `new` en Surtr) y `not`/`or` (no son tokens reales —
  los operadores lógicos son simbólicos: `&&`, `||`, `!`); añadido `constructor`, que §1.2 sí
  reserva y faltaba.
- **Dos bugs reales encontrados por el propio test de hover/definición sobre import** (no
  estaban en el alcance original de esta fase, pero es exactamente el tipo de regresión que la
  red de seguridad existe para atrapar):
  - `SymbolResolver.TypeCard` (`SymbolResolver.cs`) solo buscaba un tipo nombrado en el módulo
    del propio archivo — nunca en sus imports wildcard — así que hover/ir-a-definición sobre un
    tipo alcanzado solo por import en una anotación de tipo (campo, propiedad, parámetro,
    retorno, tipo base, restricción genérica) nunca resolvía su declaración. Corregido:
    `TypeCard` ahora también busca en los módulos importados por wildcard del archivo
    (`FindTypesInWildcardImports`, nuevo helper), igual que ya hacía `CompletionProvider`.
  - `HoverFormatter.BuiltInLabel` tenía una rama `default` que devolvía `"built-in type"` para
    **cualquier** nombre no reconocido — no solo los primitivos de §1.1 (`int`, `float`, `bool`,
    `char`, `string`, `range`, `unknown`). Como `TypeCard` trataba "no es null" como "confirmado
    built-in", esto hacía que **cualquier** tipo de usuario (built-in real o no) fuera
    etiquetado como "built-in type" sin buscar jamás su declaración real — el bug de fondo que
    ocultaba el anterior. Corregido: `BuiltInLabel` ahora devuelve `string?` y `null` para
    cualquier nombre fuera de esa lista fija; `TypeCard` añade como último nivel una búsqueda en
    `Binder.GlobalScope` (donde viven los built-ins reales tipo `Exception`/`IIterable`, según
    §13), así que un built-in real, un tipo de usuario y un nombre genuinamente no resuelto ya
    se distinguen correctamente.
- Verificado con la suite completa: 2060/2060 tests en verde tras el cambio.

**Commit**: `Fix: LSP keywords desincronizadas de §1.2, resolucion de tipos importados/built-in en hover y cobertura de tests base`

---

## Fase 1 — Verificación del bug reportado en interfaces built-in genéricas

**Estado de la investigación**: se intentó reproducir exhaustivamente sobre HEAD
(`develop`, commit `0bef8a2`) usando el binder real (`SurtrCompilation`/`Binder`), con estos
casos — todos ligan limpio hoy:
- `class Foo : IIterable<int> { override fun iterate(): IIterator<int> { ... } }`
- `interface Bar : IIterable<int> { }`
- `let x: IIterable<int> = [1, 2, 3];` y un campo `var x: IIterable<int>;`
- Una clase genérica que implementa un built-in genérico: `class Single<T> : IIterable<T>`
  (ya cubierto por `ModuleEmitterTests.AGenericClassCanSatisfyAGenericContract`,
  `src/Surtr.Tests/Compiler/CodeGen/ModuleEmitterTests.cs:3148`, ejecutado de punta a punta
  contra la VM real).
- Cadenas de interfaces (`IReadOnlyCollection<T> : IIterable<T>`, `ICollection<T> :
  IReadOnlyCollection<T>`, una `value class` implementándolas) — ya cubierto por
  `BinderTests.cs:1188-1286`.
- Restricciones genéricas sobre built-ins (`class Box<T : IIterable<T>>`,
  `fun max<T : IComparable<T>>`), interfaz extendiendo dos built-ins a la vez, y uso
  cross-file dentro del mismo módulo.

**Causa raíz de la clase de bug — ya corregida por los dos commits más recientes**:
1. `5cca11a` (`src/Surtr.Compiler/Binding/MemberLookup.cs:~172`) — el recorrido de jerarquía
   encolaba los miembros de una interfaz/base construida **sin aplicar antes la sustitución de
   argumentos de tipo del receptor**, así que un miembro alcanzado a través de una interfaz
   built-in construida (p. ej. `iterate()` vía `IReadOnlyCollection<T> : IIterable<T>`)
   respondía con la `T` sin sustituir de la propia interfaz en vez de la del receptor —
   apareciendo como fallos de binding alrededor de `IIterable`/`IIterator`.
2. `0bef8a2` (`Binder.cs` `CheckObligation`/`CollectInterfaces`, `SignatureSet.cs` nuevo) —
   `CollectInterfaces` no tenía en cuenta la sustitución (su propio comentario antiguo lo
   admitía), así que la comprobación de obligaciones/overrides contra una interfaz built-in
   construida se validaba contra la firma sin sustituir — aceptando en falso un override
   incorrecto o señalando mal qué miembro faltaba.

**Conclusión provisional**: el síntoma descrito por el usuario coincide casi exactamente con
lo que arreglaron estos dos commits, y ningún escenario probado en HEAD reproduce el fallo.
Antes de escribir ningún fix nuevo hace falta **confirmación del usuario**:
- ¿En qué commit/build viste el error exactamente?
- Si sigue fallando ya sobre `0bef8a2`, ¿tienes el snippet concreto que falla? (la investigación
  no llegó a ejercitar el camino completo de round-trip por imagen `.surtrc` a través del
  proyecto `Surtr.Stdlib` por separado — es el único hueco no descartado con certeza).

**Resuelto con el usuario**: confirmó que ya no ve el fallo — cierre solo con tests de
regresión, sin buscar más.

**Cambios**: añadida una región nueva "Built-in generic interfaces (regression net for the
substitution fixes in 5cca11a/0bef8a2)" en `BinderTests.cs`, con los tres escenarios exactos
del reporte que **no** tenían cobertura propia (la cobertura existente en `BinderTests.cs`
líneas 1188-1286 y en `ModuleEmitterTests.cs:3148` cubre una *cadena* de interfaces de usuario
hacia un built-in y una clase *genérica* implementándolo — casos relacionados pero distintos,
que sí ejercitan la sustitución que arreglaron los dos commits):
- `AClassMayImplementABuiltInGenericInterfaceWithAConcreteArgumentDirectly` — clase no
  genérica implementando `IIterable<int>` directamente, sin cadena.
- `AnInterfaceMayExtendABuiltInGenericInterfaceWithoutAddingMembersOfItsOwn` — interfaz vacía
  que solo extiende un built-in genérico.
- `AClassImplementingAPassThroughInterfaceMustStillProvideTheInheritedBuiltInMember` /
  `...SatisfiesItByImplementingTheInheritedBuiltInMember` — la obligación de implementar
  `iterate()` debe verse a través de una interfaz de paso que no declara nada propio (el caso
  exacto que `CollectInterfaces` sin sustitución dejaba mal, según 0bef8a2).
- `ANonGenericClassImplementsABuiltInGenericInterfaceDirectly` (`ModuleEmitterTests.cs`) — la
  misma forma, pero de punta a punta contra la VM real con un `for-in`.
- Suite completa verificada: 2065/2065 tests en verde.

**Commit**: `Test: cobertura de regresion para interfaces built-in genericas (IIterable/IIterator) en implementacion directa y a traves de una interfaz de paso`

---

## Fase 2 — Sintaxis `=>` para métodos y propiedades de solo lectura

**Estado actual**: `TokenType.FatArrow` existe pero solo lo consumen las lambdas
(`Parser.Expressions.cs:767,800`). `ParseMethod` (`Parser.Declarations.cs:373-395`) exige
`{ }` o `;`; `ParseProperty` (`Parser.Declarations.cs:322-370`) exige `{ }` o `;` por
accessor. No hay binder/emisor que tocar — es azúcar sintáctica pura sobre
`BlockStatementSyntax`.

**Confirmado con el usuario**: se admite también la forma corta de propiedad sin `get`/`set`
(`x: int => _x;`).

**Cambios**:
- Nuevo helper `ParseArrowBody(bool returnsVoid)` en `Parser.Declarations.cs`: consume `=>`,
  parsea una expresión y la envuelve en un `BlockStatementSyntax` de un solo elemento — un
  `ReturnStatementSyntax` normalmente, o un `ExpressionStatementSyntax` cuando `returnsVoid` es
  `true` (un `return <valor>` dentro de un método `void` lo rechaza `BodyBinder.BindReturn`, la
  misma razón por la que C# trata un miembro expression-bodied como sentencia y no como azúcar
  de un `return` literal).
- `ParseMethod`: si en vez de `{`/`;` se ve `FatArrow`, usa `ParseArrowBody`, decidiendo
  `returnsVoid` mirando si el tipo de retorno escrito es literalmente `void` (comprobación
  sintáctica, sin depender del binder).
- `ParseProperty`: el bucle de accessors gana una rama `FatArrow` antes de la de `{`/`;` —
  `get => expr;` siempre es un `return`, `set => expr;` siempre una sentencia-expresión (un
  setter no retorna nada). Además, antes de exigir `{` para abrir el bloque de accessors, si lo
  que sigue al tipo es `=>`, se produce directamente una propiedad de solo lectura con un único
  accessor `get` sintético — `x: int => _x;` es azúcar de `x: int { get => _x; }`.
- Una propiedad/accessor con cuerpo `=>` dentro de una interfaz queda rechazada por el mismo
  camino que ya rechaza un cuerpo `{ }` ahí (`SurtrInterface.AddMethod`/`AddProperty` no
  aceptan implementación) — no hizo falta ninguna validación nueva.
- Cobertura de punta a punta en `ModuleEmitterTests.cs` (región "Arrow-bodied members"):
  método con valor de retorno, método `void` con efecto, propiedad de forma corta, y
  get/set ambos con `=>` en la misma propiedad — las 4 pasan a la primera.
- Actualizado `docs/Language-Syntax.md` §3.3 y §3.4 con la nueva forma.
- Suite completa verificada: 2069/2069 tests en verde.

**Commit**: `Feature: soporte de miembros con cuerpo => (métodos y propiedades de solo lectura)`

---

## Fase 3 — Modificadores independientes por accessor (`get`/`set`)

**Estado actual**: `AccessorSyntax` (`DeclarationSyntax.cs:392-409`) solo tiene `IsGetter` y
`Body`; todos los modificadores (visibilidad incluida) viven en `PropertyDeclarationSyntax` y
`WireAccessors` (`Binder.cs:2170-2233`) los copia tal cual a ambos accessors. Ni siquiera la
asimetría de visibilidad al estilo C# (`public int X { get; private set; }`) funciona hoy.

**Cambios**:
- Grammar: modificador opcional antes de `get`/`set` dentro del bloque de la propiedad;
  ausente = hereda el de la propiedad (compatible con todo el código existente).
- AST: campos de visibilidad/inline en `AccessorSyntax` (y espacio para atributos por
  accessor, ver Fase 5 — §11 ya dice que los atributos aplican a "cualquier declaración").
- Parser: extender el bucle de accessors de `ParseProperty`.
- Binder: `WireAccessors` recibe valores por accessor en vez de uno compartido; regla de
  validez — la visibilidad de un accessor debe ser igual o más restrictiva que la de la
  propiedad (como C#). Empezar solo por visibilidad + inline/forceinline (bajo riesgo);
  `override`/`sealed`/`abstract` por accessor es mecánicamente posible (getter y setter ya
  son `MethodSymbol`s y slots de vtable independientes) pero necesita que
  `SurtrTypeLinker`/el chequeo de overrides razone por accessor, no por propiedad — dejarlo
  para una fase posterior si hace falta, no bloquea el resto del plan.
- Actualizar `Language-Syntax.md` §3.2/§3.4.

**Commit**: `Feature: modificadores independientes en accessors get/set`

---

## Fase 4 — Convertir un nombre de método en un valor closure sin lambda explícita

**Estado actual**: `BindIdentifier` (`BodyBinder.Expressions.cs:124`) nunca produce un grupo
de métodos como valor — solo resuelve locales, parámetros, singletons y miembros implícitos
(campo/propiedad). `Conversions.cs` no tiene ninguna conversión de grupo-de-métodos a tipo
closure. `ClosureValue` hace lo contrario: lee un campo/propiedad *ya* de tipo closure para
invocarlo, no envuelve un `MethodSymbol` suelto.

**Cambios**:
- Decidir sintaxis: recomendado el camino implícito (un nombre suelto en contexto
  target-typed a un tipo closure se resuelve como grupo de métodos, reutilizando el mismo
  target-typing que ya usan las lambdas, §5.9) en vez de introducir un operador nuevo tipo
  `::method` de Kotlin — menos superficie de sintaxis.
- `BindIdentifier`/`BindMemberOf`: rama nueva que, cuando el nombre no resuelve a nada mejor y
  el tipo esperado es un closure, busca métodos candidatos vía `_lookup.FindMethods` y aplica
  resolución de sobrecarga contra la forma del tipo closure esperado (mismo mecanismo que ya
  usa `BodyBinder` para lambdas sin tipos de parámetro escritos).
- Nuevo `BoundExpression` para "referencia a método ligada a un tipo closure"; decidir si un
  método de instancia captura `this` implícitamente (recomendado: sí, igual que C#
  `obj.Method` como delegado) o si solo se permite sobre métodos estáticos/de módulo en una
  primera iteración para reducir alcance.
- `MethodBodyEmitter`: emitir `ClosureNew`/equivalente con el método resuelto, sin capturas o
  con `this` como única captura.
- Actualizar `Language-Syntax.md` §8.

**Commit**: `Feature: conversion de grupo de metodos a valor closure sin lambda explicita`

---

## Fase 5 — Atributos: keyword `attribute`, retención y target

**Estado actual**: los atributos funcionan de punta a punta hoy — `@Nombre(args)` (§11),
`SurtrBuiltIns.Attribute` como marcador, `SurtrAttributeUsage` con argumentos constantes,
`Binder.BindAttributes` (`Binder.cs:1598-1660`) valida herencia de `Attribute` y pliega
argumentos, `ModuleEmitter.cs:590-866` los emite, el image reader/writer los serializa, y
`SurtrMemberInfo.TryGetAttribute` los expone — pero solo a un **host en C#**. No hay keyword
`attribute`, ni distinción de retención (compile-time-only vs runtime-visible), ni
restricción de target (nada impide poner cualquier atributo en cualquier tipo de
declaración), y §11 lo deja explícito: "cómo el host los lee es una cuestión de diseño
posterior".

**Cambios**:
- Palabra clave contextual `attribute` sobre `class Foo : Attribute` (alias de parser que
  además valida las restricciones de forma — sin campos adicionales más allá de los que exige
  el contrato de atributo, sin más de una interfaz salvo `Attribute` misma).
- Enum `SurtrAttributeTargets` ([Flags]: Class, Interface, Enum, Field, Property, Method,
  Parameter, ...) y `SurtrAttributeRetention` (CompileTimeOnly / Runtime), declarados sobre
  la propia clase de atributo (al estilo `[AttributeUsage]` de C#, pero auto-hospedado con la
  sintaxis `attribute` en vez de un segundo atributo bootstrap).
- `BindAttributes`: valida el target contra la clase de declaración donde se usa; si la
  retención es `CompileTimeOnly`, no emitir el `SurtrAttributeUsage` tras las comprobaciones
  de binding (se pliega y se descarta, como `@Obsolete`).
- `ModuleEmitter.cs` / image reader-writer: nuevos campos de metadata para target/retención.
- Actualizar `Language-Syntax.md` §11 con la sintaxis y semántica final.

**Commit**: `Feature: keyword attribute con target y retencion`

---

## Fase 6 — API de reflexión de atributos desde Surtr

**Depende de la Fase 5.** Hoy no existe ningún built-in de tipo `Type`/`Member` en Surtr —
leer atributos solo es posible desde C# vía `SurtrMemberInfo`. Añadir una familia de built-ins
mínima (p. ej. `Type`, con métodos nativos para enumerar miembros y sus atributos de
retención `Runtime`) siguiendo el mismo patrón que el resto de `SurtrBuiltIns`
(`Direct` dispatch, sin virtual salvo para contratos existentes). Es la pieza más grande de
este bloque porque es una familia de tipos nueva, no un parche.

**Commit**: `Feature: API de reflexion de atributos accesible desde Surtr`

---

## Fase 7 — Import: alias de módulo (`import X as Y`)

**Estado actual**: `ImportSyntax` (`DeclarationSyntax.cs:202-219`) no tiene campo de alias;
`ParseImport` (`Parser.cs:313-334`) va directo del path punteado a `;`. `as` ya es un token
reservado (`operator as`, §5.6) así que no hace falta una keyword nueva.

**Cambios**:
- `ImportSyntax.Alias: string?`; `ParseImport` acepta `as Identifier` opcional antes de `;`.
- Binder: como Surtr no tiene módulos como valor de primera clase (solo `singleton` puede
  serlo, §2.8), el alias se resuelve como una entrada de scope sintética cuya búsqueda
  delega en los tipos de ese módulo — reutilizando el camino de `TypeResolver` que ya lee un
  nombre punteado como tipo anidado antes que como nombre completamente calificado.
- Nuevos `SurtrDiagnosticCode` para colisión de alias.
- Actualizar `Language-Syntax.md` §2.1. Actualizar `CompletionProvider.ImportedModules` en el
  LSP para reconocer el alias (Fase 0 ya deja tests que detectan la regresión si no se hace).

**Commit**: `Feature: alias de modulo en import (import X as Y)`

---

## Fase 8 — Import: lista selectiva de miembros

**Estado actual**: `import Path.To.Name;` ya importa exactamente un nombre — la semántica
existe por nombre suelto, pero no hay forma de listar varios en una línea; hace falta repetir
`import` una vez por nombre.

**Cambios**:
- Sintaxis recomendada: reutilizar el estilo de ruta punteada existente en vez de introducir
  `from` — `import Ogame.core.{Entity, Vec2};`.
- `ImportSyntax.Members: IReadOnlyList<string>?` (`null` = las formas actuales de
  nombre-único/wildcard).
- `ParseImport`: rama nueva tras el path si el siguiente token es `{`.
- `Binder.BindImports`: la rama de "import con nombre" pasa a iterar la lista.
- Actualizar `Language-Syntax.md` §2.1 y el LSP (mismo camino que la Fase 7).

**Commit**: `Feature: import selectivo de miembros (import X.{Y, Z})`

---

## Fase 9 — Import: wildcard de directorio (recursivo)

**Estado actual**: `import a.*` solo alcanza los tipos declarados directamente en el módulo
`a` — un módulo es un directorio (`ModulePath.cs`), así que no llega a submódulos como `a.b`.
`Binder.cs`'s `_modules` es un diccionario por ruta exacta; no existe hoy ningún índice de
"todos los módulos bajo este prefijo".

**Es la pieza más difícil del bloque de imports** porque requiere ese índice nuevo, no solo
gramática.

**Cambios**:
- `Compilation`/`ModuleDependencyGraph`: exponer "todo módulo cuya ruta empieza por este
  prefijo", construido una vez a partir del conjunto de módulos de la compilación.
- `Binder.BindImports`: la rama wildcard, cuando el path no resuelve a un módulo exacto pero
  sí es prefijo de uno o más módulos, itera todos los módulos coincidentes en vez de exigir un
  único acierto exacto. Un edge de `ModuleDependencyGraph` por módulo resuelto basta — no
  hacen falta edges por símbolo (la ambigüedad ya se resuelve en el punto de uso, §2.1).
- Actualizar `Language-Syntax.md` §2.1 y el índice de módulos que consulta el LSP para
  completar imports de directorio.

**Commit**: `Feature: import wildcard de directorio (recursivo sobre submodulos)`

---

## Fase 10 — Clases (y enums, value classes, singletons, interfaces) declaradas dentro de un método

**Estado actual**: totalmente ausente. `ParseStatement` (`Parser.Statements.cs:45-114`) no
tiene ninguna rama para `class`/`interface`/`enum`/`singleton`/`value class`;
`ParseDeclaration` (que sí las reconoce) nunca se invoca desde dentro de un cuerpo. No existe
ningún nodo `LocalClassDeclarationStatementSyntax` en el AST. Ni `Language-Syntax.md` §2.6 ni
§14.4 mencionan esta posibilidad — no es una feature diferida a propósito, es simplemente algo
que nunca se planteó.

**Es la fase más grande del plan.** El binder es la parte difícil: `BodyBinder`, la cadena de
`Scope` y la comprobación de captura "effectively final" asumen hoy que la única construcción
capturadora dentro de un cuerpo es una lambda (§8); enseñarles una segunda construcción con
forma de clase, con su propia síntesis de constructor, es el mayor sub-problema.

**Decisiones de diseño recomendadas** (evitan abrir preguntas nuevas, reutilizan mecanismo
existente):
- **Metadata**: tipo anidado sintético de la clase/módulo contenedor, con el esquema de
  nombres ya existente `$categoria$contexto[$indice]` (`SyntheticNames.cs:21-24`) — p. ej.
  `$local$foo$0$Local` — en vez de inventar una tabla de tipos por método.
- **Captura**: igual que una lambda — solo locales "effectively final", copiados por valor al
  construir la instancia (mismos parámetros de constructor sintéticos que ya usa una lambda),
  nunca una celda compartida — el lenguaje no tiene celdas de variable.
- **Estáticos**: una clase local **no admite** miembros estáticos ni bloques `static { }` — no
  hay una posición de orden de carga sensata para un tipo que solo existe cuando su método se
  ejecuta (`InitializerOrder` razona sobre orden de declaración a nivel de carga del módulo).
- **Genéricos**: una clase local no ve los parámetros de tipo del método/clase contenedor —
  misma regla que ya aplica a un tipo anidado ordinario (§6, "nested type does not see its
  container's parameters").
- Actualizar `Language-Syntax.md` §2.6 y §4.4 con la nueva forma de declaración local.

**Commit**: `Feature: declaracion de tipos (class/enum/interface/singleton/value class) dentro de un cuerpo de metodo`

*(Si el alcance real resulta mayor de lo estimado al implementar, dividir en sub-commits por
tipo de declaración — p. ej. clases y enums primero, interfaces/singletons/value classes
después — en vez de forzar un único commit gigante.)*

---

## Fase 11 — Cargador de STDLIB seleccionable (sandbox) + enlace nativo portable

**Estado actual**: `src/Surtr.Stdlib/src/surtr/` tiene 7 módulos hoy (`collections/`,
`core/`, `math/`, `text/`). `Surtr.Stdlib.Tool` los compila uno a uno a `.surtrc` sin producir
ningún manifiesto. `SurtrStdlib.LoadInto` (`SurtrStdlib.cs:49-100`) carga **todo lo que se le
pase**, sin selección; `Surtr.Core` no depende de `Surtr.Stdlib` ni de `Surtr.Compiler` a
propósito (frontera de arquitectura documentada en el README de `Surtr.Stdlib`), así que la
selección debe vivir fuera de `SurtrRuntime`, no dentro.

**Verificación del enlace nativo (lo que pedía el usuario específicamente)**: confirmado que
**ya es portable en tiempo de ejecución** — `SurtrStdlib.RegisterNativeBodies`
(`SurtrStdlib.cs:114-137`) registra los 16 `native fun` de `Math.surtr` (únicos nativos hoy en
toda la stdlib, confirmado por grep en todo `src/Surtr.Stdlib/src`) mediante
`runtime.DefineNativeBody("surtr.math.Math.<nombre>", ...)` — pura cadena de link name +
puntero a función C#, sin reflexión ni referencia a `Surtr.Compiler` ni a ningún `.surtr`
fuente (`Surtr.Core.csproj` no tiene ninguna `ProjectReference`). Un enlace que falta produce
un error de carga limpio y ya probado (`SurtrRuntime.BindNativeBodiesIn`,
`SurtrStdlibTests.TheImageFailsToLoadWithoutThePublishedBodies`) — nunca una lectura
silenciosa a cero. **El enlace en sí no es el problema.**

**Gaps reales de portabilidad, encontrados**:
1. Las imágenes `.surtrc` **no están embebidas como recursos** en ningún ensamblado — el único
   consumidor de punta a punta (`SurtrStdlibTests.cs:26-39`) las localiza subiendo directorios
   desde `AppContext.BaseDirectory` hasta encontrar `Surtr.sln`, es decir, **solo funciona
   dentro de un checkout del repositorio**. Ni `Surtr.Run` ni `Surtr.Cli` llaman hoy a
   `SurtrStdlib.LoadInto`, así que no hay ningún consumidor real que valide el escenario "solo
   tengo la DLL".
2. **No hay detección de desincronización en build**: si alguien añade
   `native fun cos2(...)` a `Math.surtr` y `Surtr.Stdlib.Tool` recompila la imagen sin que
   nadie actualice `RegisterNativeBodies`, el desajuste solo se descubre cuando alguien carga
   el runtime — no hay ningún paso de build/CI que lo detecte antes (no existe CI en el repo
   todavía).

**Cambios**:
- `Surtr.Stdlib.Tool` genera, junto a cada `.surtrc`, un manifiesto pequeño
  (`StdlibModuleDescriptor`: nombre de módulo, ruta del recurso, dependencias por ruta de
  módulo) **y** una lista plana de los link names nativos que compiló
  (`build/native-link-names.txt` o equivalente en el manifiesto).
- Embeber los `.surtrc` como `<EmbeddedResource>` en `Surtr.Core.csproj` (no requiere ninguna
  referencia de proyecto nueva — son solo bytes), expuestos vía
  `Assembly.GetManifestResourceStream` y el `LoadInto(runtime, IEnumerable<byte[]>)` que ya
  existe. Esto hace que "solo tengo la DLL" funcione de fábrica.
- `SurtrStdlib` gana una sobrecarga selectiva, p. ej.
  `SurtrStdlib.LoadInto(runtime, StdlibModules seleccion)`, que resuelve el cierre de
  dependencias del manifiesto antes de reusar el `LoadInto` existente — sin tocar el mecanismo
  de carga de punto fijo, que ya es correcto.
- **Test de desincronización temprana**: un test en `Surtr.Tests` (que sí puede referenciar
  tanto artefactos compilados por `Surtr.Compiler` como `Surtr.Core`) que compara la lista de
  link names publicada por `Surtr.Stdlib.Tool` contra las claves que
  `SurtrStdlib.RegisterNativeBodies` registra en un runtime de prueba — sin CI en el repo
  todavía, la suite de tests (`dotnet test`) es el punto de aplicación más cercano disponible.
- `RegisterNativeBodies` se queda como está por ahora (tabla pequeña, registrar un link name
  no usado es inofensivo); revisarlo solo si la tabla crece mucho.
- Actualizar el README de `Surtr.Stdlib` (cierra el ítem 1 pendiente) y documentar el nuevo
  API en `docs/Runtime-Model.md`.

**Commit**: `Feature: cargador de STDLIB seleccionable y portable (recursos embebidos + verificacion de enlace nativo)`

---

## Fase 12 — Barrido final de LSP y documentación

**Última fase**, tras todo lo anterior. El agente de investigación del LSP advirtió que
`CompletionProvider`/`SymbolResolver` están en sincronía con el binder **solo por
convención**, no por una abstracción compartida — cada fase de imports/atributos/accessors/
tipos locales de este plan necesita, además de su propia actualización puntual (ya listada en
cada fase), una pasada de cierre:

- Ejecutar toda la suite nueva de `src/Surtr.Tests/LanguageServer/` (Fase 0) contra el estado
  final y añadir los casos que falten para cada feature nueva (hover/completado de un tipo
  local, de un miembro importado por alias, de un atributo con target).
- Revisar que ningún literal de `CompletionProvider`/`SymbolResolver` haya quedado
  desincronizado con los cambios de `Binder.cs` (scopes de import, accessors, tipos locales).
- Pasada final de coherencia doc↔código: `Language-Syntax.md`, `Compiler-Plan.md`,
  `VM-Plan.md` y `CLAUDE.md` — cada uno ya se actualiza fase a fase arriba, esta fase es solo
  la relectura cruzada final antes de cerrar el plan.

**Commit**: `Docs+LSP: barrido final de coherencia tras las fases 1-11`

---

## Orden de ejecución y estado

| Fase | Descripción | Estado |
|---|---|---|
| 0 | Red de seguridad LSP + fix de keywords (+ 2 bugs reales de hover/imports encontrados y corregidos) | **Hecha** |
| 1 | Verificación del bug de interfaces built-in genéricas | **Hecha** (no reproducido; tests de regresión añadidos) |
| 2 | Sintaxis `=>` en métodos/propiedades | **Hecha** |
| 3 | Modificadores independientes por accessor | Pendiente |
| 4 | Método → valor closure sin lambda | Pendiente |
| 5 | Keyword `attribute`, target y retención | Pendiente |
| 6 | API de reflexión de atributos en Surtr | Pendiente (depende de 5) |
| 7 | Alias de import | Pendiente |
| 8 | Import selectivo de miembros | Pendiente |
| 9 | Import wildcard de directorio | Pendiente |
| 10 | Tipos locales dentro de métodos | Pendiente |
| 11 | Cargador de STDLIB seleccionable y portable | Pendiente |
| 12 | Barrido final LSP + docs | Pendiente |
| — | Operadores de instancia / `operator[]` | **Ya hecho** (`Plan-Globales-Nativos-Inline-Operadores.md`) |
| — | Varianza de genéricos | **Diferido a propósito** (§14.4), no planificado |
| — | Built-ins siempre disponibles | **Ya correcto**, sin cambios |

Cada fase termina con build completo + suite verde (`dotnet build Surtr.sln` +
`dotnet test Surtr.sln`) antes de su commit, siguiendo la misma disciplina que el plan de
operadores. Ninguna fase empieza antes de que la anterior esté commiteada.
