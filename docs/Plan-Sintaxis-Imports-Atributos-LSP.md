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
| 2 | Asignar una función a una variable/campo/parámetro de tipo closure solo por su nombre, sin lambda explícita | **Hecho** — implementado como azúcar de lambda (`obj.method` ↔ `(p) => obj.method(p)`), no como un opcode nuevo, tras descubrir que `InvokeClosure` no antepone upvalues a los argumentos | — |
| 3 | Métodos y propiedades de solo lectura con `=>` | Ausente. `FatArrow` ya existe como token (solo lo usan las lambdas); es azúcar sintáctica pura | Pequeño |
| 4 | Modificadores (`inline`, `forceinline`, `override`, etc.) independientes por `get`/`set` | **Hecho** — alcance completo (visibilidad, inline/forceinline, virtual/override/abstract/sealed), más el descubrimiento y arreglo de un bug real (`sealed` en una propiedad nunca sellaba nada) | — |
| 5 | Atributos declarables con `attribute`, con retención y target | **Hecho** — keyword `attribute`, target y retención `CompileTimeOnly`/`Runtime` completos (comprobados en la misma compilación; import cruzado de target queda documentado como límite). API de reflexión desde Surtr (`Type`/`Member`, ver Fase 6 más abajo) también hecha | — |
| 6 | `operator[]` y operadores como miembros de instancia, no solo estáticos | **Ya implementado** — ver `docs/Plan-Globales-Nativos-Inline-Operadores.md`, Fase C (Ruta A: operadores de instancia con `abstract`/`virtual`/interfaz). Sin trabajo pendiente | — |
| 7 | Varianza de genéricos (`in`/`out`) | Genéricos correctos y completos hoy (§10.1b cerrado, sin TODOs). Varianza está **deliberadamente diferida** en `Language-Syntax.md` §14.4 — no es prioritaria mientras quede pendiente §10.2 (STDLIB en Surtr) | Diferido, no se planifica |
| 8 | Cargador de STDLIB con selección de módulos (sandbox) + enlace nativo portable | **Hecho en parte (Fase 11)** — selección por categoría (`StdlibModules`) y test de desincronización temprana, ambos hechos. Embeber los `.surtrc` dentro de `Surtr.Core.csproj` resultó ser un ciclo de build real (necesita compilar la stdlib, lo que necesita `Surtr.Compiler`, lo que necesita `Surtr.Core` — el mismo ensamblado a medio construir); documentado, no forzado | Pequeño-medio |
| 9 | Declarar clases/enums/interfaces/singletons/value classes dentro de un método | Investigado a fondo (Fase 10) y **diferido deliberadamente** — requiere descubrir tipos locales antes de la fase 3 del binder para evitar reentrada sobre `_declared`/`_bodies`/etc.; camino de implementación documentado en la Fase 10 | Medio-grande |
| 10 | Import de directorio completo (wildcard recursivo), import selectivo de miembros, alias de módulo | Alias (`import X as Y`) **hecho** — Fase 7. Selectivo (`import X.{Y, Z}`) **hecho** — Fase 8. Wildcard de directorio (recursivo sobre submódulos) **hecho** — Fase 9 | — |
| 11 | Built-ins siempre disponibles sin import, nunca rotos por imports | **Ya correcto**, con tests dedicados (`BinderTests.cs`) | — |
| 12 | LSP correcto para todo lo anterior, especialmente imports y built-ins | **Hecho** — Fase 0 (red de seguridad + 2 bugs) y Fase 12 (barrido final, encontró que hover/definición no seguían a autocompletado en alias/selectivo/wildcard de directorio) | — |

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

**Confirmado con el usuario**: alcance completo, incluyendo `override`/`sealed`/`abstract` por
accessor (no solo visibilidad + inline/forceinline).

**Estado previo**: `AccessorSyntax` (`DeclarationSyntax.cs:392-409`) solo tenía `IsGetter` y
`Body`; todos los modificadores (visibilidad incluida) vivían en `PropertyDeclarationSyntax` y
`WireAccessors` (`Binder.cs:2170-2233`) los copiaba tal cual a ambos accessors.

**Investigación previa a implementar** (clave para acotar el riesgo real): `WireAccessors` ya
crea un `MethodSymbol` **independiente** por `get_x`/`set_x`, y ambos entran en
`symbol.Members` como miembros de primer orden (`Binder.cs:1116-1124`). Todo el chequeo de
`override`/`sealed`/obligaciones de interfaz (`CheckSealedOverrides`, `CheckObligation`,
`CheckMembersImplemented`) ya opera sobre `MethodSymbol`s individuales por nombre, sin ningún
concepto de "propiedad" a ese nivel — así que una vez que `WireAccessors` fija `Dispatch`/
`IsOverride`/`IsSealed` de forma independiente por accessor, el resto de la maquinaria (vtable,
obligaciones, `sealed`) ya lo respeta sin tocar nada más. El riesgo real no estaba en el linker,
sino en la **visibilidad**: el único punto donde se comprueba accesibilidad de una propiedad
(`RequireAccessible(property, property.Accessibility, ...)`, `BodyBinder.Expressions.cs`) se
llama una vez, igual para lectura que para escritura, así que una visibilidad asimétrica
(`public get; private set;`) necesitaba una comprobación adicional específica en el punto de
escritura.

**Bug real encontrado en la propia investigación**: `PropertyDeclarationSyntax.IsSealed` se
parseaba correctamente pero **nunca llegaba a `WireAccessors`** — ni la llamada desde
`BindProperty` se lo pasaba, ni `WireAccessors` fijaba `MethodSymbol.IsSealed` en ningún sitio.
Es decir, `sealed override` en una propiedad se aceptaba sintácticamente pero **no sellaba
nada** — una subclase podía seguir sobrescribiéndola sin ningún error. Corregido como parte de
esta fase (test de regresión incluido: `APropertyLevelSealedOverrideActuallySealsItsAccessors`).

**Cambios**:
- `AccessorSyntax`: nuevos campos `Visibility`, `HasOwnDispatch`, `Dispatch`, `IsSealed`,
  `Inline` (con valores por defecto en el constructor para no romper los 3 sitios existentes
  que ya lo construían).
- Parser: nuevo `AccessorModifiers`/`ParseAccessorModifiers()` en `Parser.Declarations.cs`,
  con el mismo orden de modificadores que ya fija §3.2 pero sin `static`/`const`/`native`
  (no tienen sentido por accessor); se llama justo antes de reconocer `get`/`set`.
- `WireAccessors` gana un parámetro `isSealed` (cierra el bug de arriba) y, por accessor:
  visibilidad efectiva = la propia si se escribió, si no la de la propiedad
  (`ResolveAccessorAccessibility`, que también valida que sea **estrictamente** más
  restrictiva, nunca igual ni más permisiva — nuevo diagnóstico `AccessorVisibilityNotNarrower`
  = 3051); dispatch+sealed efectivos = los propios si el accessor escribió alguno de
  `virtual`/`override`/`abstract`/`sealed` (`HasOwnDispatch`), si no los de la propiedad, como
  un par (evita la ambigüedad de que escribir solo `private` en un setter le resetee
  silenciosamente el dispatch heredado); inline efectivo = el propio si no es `None`, si no el
  de la propiedad.
- `BodyBinder.BindAssignment` (`BodyBinder.Expressions.cs`): nueva comprobación de
  accesibilidad contra `property.Setter.Accessibility` específicamente, en el único punto por
  el que pasa toda escritura — la comprobación de la propiedad en general (en la resolución
  inicial) sigue existiendo tal cual y usa la visibilidad más permisiva de las dos, así que
  esta es estrictamente una comprobación adicional, nunca menos estricta que antes.
- **Límite documentado, no implementado**: la lectura a través de un *getter* más estrecho que
  la propiedad (`private get; public set;`, patrón muy poco habitual) no tiene un punto de
  intercepción único análogo a `BindAssignment` — cada lectura consume el valor donde sea que
  fluya la expresión, no en un único sitio. Se deja sin reforzar (la metadata se guarda
  correctamente, solo falta el chequeo), documentado como límite deliberado en vez de
  implementado a medias en silencio.
- Cobertura en `BinderTests.cs` (10 tests nuevos): narrowing aceptado/rechazado (más
  restrictivo, igual, más permisivo), escritura rechazada desde fuera del alcance del setter,
  dispatch independiente por accessor, abstract en un accessor con el otro concreto,
  satisfacción de una obligación abstracta heredada, y el regression test del bug de `sealed`
  (incluyendo `sealed` escrito directamente en el accessor). Más un test de punta a punta en
  `ModuleEmitterTests.cs` que confirma que un getter `virtual` por accessor (sin modificador a
  nivel de propiedad) despacha de verdad a través de la vtable en la VM real.
- Actualizado `docs/Language-Syntax.md` §3.2 y §3.4.
- Suite completa verificada: 2080/2080 tests en verde. Los 10+1 tests nuevos pasaron a la
  primera, sin necesitar ninguna corrección tras escribirlos.

**Commit**: `Feature: modificadores independientes en accessors get/set + fix de sealed en propiedades`

---

## Fase 4 — Convertir un nombre de método en un valor closure sin lambda explícita

**Estado actual**: `BindIdentifier` (`BodyBinder.Expressions.cs:124`) nunca produce un grupo
de métodos como valor — solo resuelve locales, parámetros, singletons y miembros implícitos
(campo/propiedad). `Conversions.cs` no tiene ninguna conversión de grupo-de-métodos a tipo
closure. `ClosureValue` hace lo contrario: lee un campo/propiedad *ya* de tipo closure para
invocarlo, no envuelve un `MethodSymbol` suelto.

**Confirmado con el usuario**: sintaxis implícita (nombre suelto en contexto target-typed a un
closure); un método de instancia captura `this` implícitamente, igual que el delegado
`obj.Method` de C#.

**Decisión de diseño clave, encontrada al investigar el runtime antes de implementar**: intentar
envolver directamente un `MethodSymbol` *ya existente* en un `NewClosure` con el receptor como
upvalue **no funciona** — `InvokeClosure` (`SurtrVirtualMachine.cs`) arma el frame del método
invocado solo con los argumentos del sitio de llamada, sin anteponer los upvalues; un método
normal compilado espera su receptor en el slot 0 de argumentos ordinario, no vía `UpValueGet`
(eso solo lo entienden los cuerpos de lambda *lifted*, que se compilan sabiendo que van a leer
sus capturas así). La solución: **no se creó ningún `BoundExpression` ni opcode nuevo** — la
conversión se resuelve enteramente como azúcar de una lambda: `obj.method` con tipo esperado
`(T) -> R` se liga exactamente como se ligaría `(p) => obj.method(p)` escrita a mano,
reutilizando `BindLambda`/`EmitLambda` al 100% (captura del receptor por valor en el momento de
la conversión, dispatch virtual correcto, emisión) sin tocar `MethodBodyEmitter` en absoluto.

**Cambios**:
- `BindExpression` pasa ahora `expected` a `BindIdentifier` y `BindMemberAccess` (antes se
  perdía completamente para estos dos casos).
- Nuevo `TryBindMethodGroup`/`MatchesClosureShape`/`BindMethodGroupLambda` en
  `BodyBinder.Expressions.cs`: cuando el tipo esperado es un `ClosureTypeSymbol` y ningún otro
  camino resolvió el nombre, busca candidatos por aridad + asignabilidad de parámetros +
  igualdad de "vacuidad" de retorno (un closure `void` nunca puede envolver un método que sí
  retorna algo, para no desalinear el conteo de resultados que espera `InvokeClosure`), y arma
  la lambda sintética: parámetros frescos, receptor (si lo hay) ligado *dentro* del nuevo frame
  de lambda para que `NoteCapture`/`NoteReceiverCapture` lo atribuyan correctamente, cuerpo =
  una llamada al método con dispatch virtual si corresponde.
- Tres puntos de caída añadidos: `BindIdentifier` (nombre suelto — métodos de instancia del
  tipo contenedor si hay `this` disponible, más los del propio módulo y de cada import
  wildcard, en ese orden), `BindInstanceMember` (`obj.method`, nunca bajo `?.`), `BindStaticMember`
  (`Type.method`, solo candidatos estáticos).
- **No es resolución de sobrecarga completa**: si varias sobrecargas encajan con la forma del
  closure, gana la primera encontrada — decisión deliberada dado lo raro que es convertir un
  nombre *sobrecargado* a closure; documentado en §8, no silencioso.
- 6 tests de punta a punta en `ModuleEmitterTests.cs` (región "Method-group to closure (§8)"):
  función de módulo, método estático, método de instancia por `this` implícito y por receptor
  explícito, método `void` (efecto), y dispatch virtual a través del receptor capturado — los 6
  pasaron a la primera.
- Actualizado `docs/Language-Syntax.md` §8.
- Suite completa verificada: 2093/2093 tests en verde.

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

**Confirmado con el usuario**: `attribute class Foo { ... }` implica extender `Attribute`
automáticamente (sin `: Attribute` explícito); el target se anota directamente en la
declaración — `attribute(Method, Property) class X`; la retención se escribe en esa misma
lista con la palabra `CompileTimeOnly`, con `Runtime` por defecto.

**Cambios**:
- `attribute` como palabra clave contextual (cuarta, junto a `this`/`super`/`value`),
  reconocida solo justo antes de `class` (con o sin lista de parámetros) — igual que `value
  class`. Nueva `ParseAttributeClassDeclaration` en `Parser.Declarations.cs`, que parsea la
  lista opcional `(Target, ..., CompileTimeOnly?)` y delega en `ParseTypeDeclaration` (ganó
  tres parámetros opcionales) para el resto — una clase `attribute` es una clase normal en
  todo lo demás.
- Enum `SurtrAttributeTargets` ([Flags]: Class, Interface, Enum, Field, Property, Method) en
  `Syntax/Ast/DeclarationSyntax.cs` — sin `Parameter` (los atributos en parámetros no están
  confirmados como funcionales hoy, así que no se añade un target para algo no verificable) ni
  `Module` (un `fun`/`let` de módulo ya cae en Method/Field, no hace falta distinguirlo).
  Renombrado a `SurtrAttributeTargets` (no `AttributeTargets` a secas) porque colisionaba con
  `System.AttributeTargets` del BCL.
- `NamedTypeSymbol` gana `IsAttribute`, `AllowedAttributeTargets`, `IsCompileTimeOnlyAttribute`
  (mismo patrón que `IsSealed`/`IsAbstract`, indexado a través de `Definition`).
- `Binder.BindHierarchy`: si `syntax.IsAttribute` y no hay base explícita, resuelve `Attribute`
  automáticamente (mismo mecanismo que resuelve el nombre de un `@Foo`); si hay base explícita,
  exige que ya extienda `Attribute` (nuevo uso de `InvalidAttribute` en la declaración, no solo
  en el uso).
- `Binder.BindAttributes`: nueva comprobación de target contra `DeclarationTargetOf(binding.Target)`
  (mapea `NamedTypeSymbol`/`FieldSymbol`/`PropertySymbol`/`MethodSymbol` a su target; cualquier
  otra cosa —módulo, alias, parámetro, local— no matchea nunca contra una lista restringida,
  que es la respuesta correcta) — nuevo código `AttributeTargetMismatch` = 3052.
- `ModuleEmitter.cs`: los 5 puntos donde se emite un `SurtrAttributeUsage` ahora saltan los
  usos cuyo `use.Type.IsCompileTimeOnlyAttribute` es cierto — el uso se sigue comprobando y
  plegando en el binder (así un argumento no constante en un atributo `CompileTimeOnly` sigue
  reportando error), solo no llega a la imagen compilada.
- **Sin cambios en el formato de imagen** (`SurtrModuleImageWriter`/`Reader`): el target/
  retención son puramente de comprobación en tiempo de compilación, nunca los necesita un
  runtime que ya cargó el módulo.
- **Límite documentado, no implementado**: la lista de targets solo se comprueba contra usos
  en la **misma compilación** que la declaración del atributo — un atributo importado desde
  una imagen `.surtrc` ya compilada no lleva su target/retención de vuelta a través de
  `MetadataImporter` hoy (necesitaría extender el formato de imagen, fuera de alcance de esta
  fase). El caso común — atributo y usos en el mismo proyecto — no se ve afectado.
- 7 tests nuevos en `ModuleEmitterTests.cs` (región "Attributes (§11)"): keyword sin base
  explícita, target aceptado/rechazado, sin lista = sin restricción, base inválida rechazada,
  `CompileTimeOnly` comprobado pero no emitido, `CompileTimeOnly` sigue reportando argumento no
  constante. Todos pasaron a la primera.
- Actualizado `docs/Language-Syntax.md` §1.2 (cuarta palabra contextual) y §11 (sintaxis y
  semántica completas de `attribute`, target, retención, y el límite de import cruzado).
- Suite completa verificada: 2087/2087 tests en verde.

**Commit**: `Feature: keyword attribute con target y retencion`

---

## Fase 6 — API de reflexión de atributos desde Surtr — **Hecha**

**Depende de la Fase 5.** Hoy no existe ningún built-in de tipo `Type`/`Member` en Surtr —
leer atributos solo es posible desde C# vía `SurtrMemberInfo`. Añadir una familia de built-ins
mínima (p. ej. `Type`, con métodos nativos para enumerar miembros y sus atributos de
retención `Runtime`) siguiendo el mismo patrón que el resto de `SurtrBuiltIns`
(`Direct` dispatch, sin virtual salvo para contratos existentes). Es la pieza más grande de
este bloque porque es una familia de tipos nueva, no un parche.

**Diseño final**: dos clases built-in nuevas, `Type` y `Member`, declaradas en `SurtrBuiltIns`
exactamente como `Attribute`/`Math`/`Iterator` — comparten módulo `surtr`, así que
`MetadataImporter` las ve automáticamente sin ningún cambio en el compilador. La parte no
trivial era cómo guardar una referencia a metadata (`SurtrClass`/`SurtrMemberInfo`) dentro de
un valor Surtr, dado que un slot de `SurtrInstance` solo admite `SurtrValue`s. Se descartó
"guardar el descriptor como string y re-resolver" (funciona pero pierde identidad exacta de
overload en un método) a favor de replicar el patrón que ya usa `SurtrIterator`: dos
`SurtrObject` dedicados nuevos, `SurtrTypeValue`/`SurtrMemberValue`
(`Runtime/Objects/SurtrTypeValue.cs`, `SurtrMemberValue.cs`), cada uno con un campo CLR normal
(`Wrapped`) apuntando directamente a la metadata real — sin pasar nunca por slots, sin
re-resolución, y la identidad de un overload concreto queda fijada en el propio wrapper.
`SurtrRuntime.NewTypeValue`/`NewMemberValue` los registran en el entity registry igual que
`NewIterator`. Los miembros nativos viven en `Runtime/BuiltIns/SurtrReflectionBuiltIns.cs`.

**API resultante** (documentada en `Language-Syntax.md` §11):
- `Type.of(value: unknown): Type` — única forma de obtener uno; ni `Type()` ni `Member()` son
  invocables desde Surtr porque ninguna de las dos clases declara constructor (mismo mecanismo
  que ya impedía `iterator()`).
- `Type.name`, `Type.baseType` (null en la raíz), `Type.members(): Member[]`,
  `Type.attributes(): Attribute[]`.
- `Member.name`, `Member.kind` (string: field/property/method/class/enum/interface),
  `Member.isStatic`, `Member.declaringType: Type`, `Member.attributes(): Attribute[]`.
- `members()` deduplica de dos formas: el getter/setter sintético de una auto-property no
  aparece por separado del `property` que los generó (se excluyen contra el conjunto de
  accessors de cada `SurtrPropertyInfo`), y cualquier nombre que empiece por `$` (backing
  field, lambda, bridge — la convención ABI de nombres sintéticos ya documentada en este mismo
  archivo) se omite directamente. Un constructor sintetizado sí aparece, como `ctor`.
- Cada `SurtrAttributeUsage.Instance` ya es la instancia real materializada en la carga del
  módulo (§11), así que `attributes()` no construye nada — solo empaqueta las referencias ya
  vivas en un array. Como `ModuleEmitter` nunca emite un atributo `CompileTimeOnly` sobre un
  miembro, `SurtrMemberInfo.Attributes` ya contiene únicamente retención `Runtime`; no hace
  falta ningún filtro en tiempo de ejecución.
- Deliberadamente sin lectura/invocación de miembro (`field.get(instance)`,
  `method.invoke(...)`) — es una API de solo enumeración, tal como pedía el alcance.

**Tests**: 9 nuevos en `ModuleEmitterTests.cs` (región "Reflexion de atributos: Type/Member"),
ejecutando Surtr real de punta a punta — nombre de clase y de primitivo, conteo/deduplicación
de miembros, `kind` por miembro, `declaringType`, lista vacía de atributos, lectura de un
atributo con argumento vía `as`, atributo a nivel de clase, y `baseType` subiendo la jerarquía
y siendo `null` en la raíz. 2102/2102 tests en verde, 0 warnings.

**Commit**: `Feature: API de reflexion de atributos accesible desde Surtr`

---

## Fase 7 — Import: alias de módulo (`import X as Y`) — **Hecha**

**Estado actual**: `ImportSyntax` (`DeclarationSyntax.cs:202-219`) no tiene campo de alias;
`ParseImport` (`Parser.cs:313-334`) va directo del path punteado a `;`. `as` ya es un token
reservado (`operator as`, §5.6) así que no hace falta una keyword nueva.

**Cambios**:
- `ImportSyntax.Alias: string?`; `ParseImport` acepta `as Identifier` opcional antes de `;`,
  solo en la forma no comodín.
- `SurtrCompilation.TryResolveImport` (validación previa al binder, en `BuildDependencyGraph`)
  necesitó su propio ajuste: un import con alias trata el path **completo** como módulo — a
  diferencia de un import con nombre, no hay un segmento final que sea un tipo — igual que ya
  hacía la rama de wildcard. Sin este cambio, `import game.math as M;` se reportaba como
  `UnresolvedImport` antes de que el binder llegara a verlo.
- Binder (`BindImports`): con alias, resuelve el path completo como módulo y lo declara en el
  scope de imports del módulo fuente. Sin alias, la rama existente (prefijo de módulo más largo
  + nombre de tipo) sigue igual.
- **Diseño real, distinto del boceto original**: en vez de una "entrada de scope sintética"
  compartiendo el diccionario de tipos de `Scope` (lo que habría expuesto un `ModuleSymbol` a
  cualquier consumidor existente de `Scope.Lookup` que asume que solo hay tipos/miembros ahí),
  `Scope` ganó un diccionario **separado** solo para alias de módulo
  (`TryDeclareModuleAlias`/`LookupModuleAlias`, con la misma cadena "innermost primero" que
  `Lookup`) — cero riesgo de romper ningún consumidor existente. En `TypeResolver.ResolveNamed`,
  entre `TryResolveThroughScope` (tipo anidado) y `TryResolveQualified` (módulo escrito
  completo), se añadió `TryResolveThroughAlias`. Los dos casos comparten la parte de "leer el
  primer segmento tras el módulo como tipo de nivel superior y caminar el resto como tipos
  anidados" a través de un nuevo helper común, `TryResolveFromModule`.
- Nuevo `SurtrDiagnosticCode.DuplicateModuleAlias = 3053`: dos `import ... as` con el mismo
  alias en el mismo módulo, reportado en la propia línea `import` (a diferencia de una colisión
  de import con nombre/wildcard, que se reporta en el punto de uso — un alias no tiene un
  import propio al que hacerle *shadow*, así que no hay nada a lo que el segundo pueda perder).
- `Language-Syntax.md` §2.1 actualizado: el alias es deliberadamente más estrecho que un import
  de valor — solo alcanza *tipos* calificados (`Core.Entity`, en anotación, lista de
  base/interfaces, `is`/`as`, construcción), nunca una función o variable a nivel de módulo, y
  el nombre del alias no se añade al scope sin calificar (no es también un wildcard).
- LSP (`CompletionProvider.cs`): nuevo helper `ResolveModuleAlias` (lee `ImportSyntax.Alias` del
  archivo actual, ya que un alias no queda como `Symbol` en ningún sitio que el binder exponga).
  Usado en `CompleteMember` (completado tras `Alias.`), en `ResolveCallableByName` (hints de
  parámetro tras `Alias.Tipo(`), y el propio nombre del alias se añade a la lista de completado
  de identificador suelto (kind `Module`) para que sea descubrible aunque no resuelva nada por
  sí solo.

**Tests**: 4 en `ModuleEmitterTests.cs` (construcción y anotación de tipo a través del alias,
que el alias NO trae el nombre sin calificar a scope, colisión de dos alias) + 2 en
`LanguageServerWorkspaceTests.cs` (completado de los tipos de un módulo aliasado, el nombre del
alias en el completado suelto). 2108/2108 tests en verde, 0 warnings.

**Commit**: `Feature: alias de modulo en import (import X as Y)`

---

## Fase 8 — Import: lista selectiva de miembros — **Hecha**

**Estado actual**: `import Path.To.Name;` ya importa exactamente un nombre — la semántica
existe por nombre suelto, pero no hay forma de listar varios en una línea; hace falta repetir
`import` una vez por nombre.

**Cambios**:
- Sintaxis: `import Ogame.core.{Entity, Vec2};` — reutiliza el estilo de ruta punteada
  existente en vez de introducir `from`.
- `ImportSyntax.Members: IReadOnlyList<string>?` (`null` = las demás formas: nombre único,
  wildcard, alias). `ParseImport` gana una rama tras un `.`: si el siguiente token es `{`,
  delega en `ParseImportMemberList` (identificador, `,` repetido, `}`) en vez de esperar un
  identificador — igual que `*` desvía a wildcard. `as` queda excluido si ya hay una lista.
- `SurtrCompilation.TryResolveImport` (la validación previa al binder) trata un import con
  `Members` igual que uno con `Alias`: el path **completo** es el módulo, sin segmento final
  que sea un tipo — mismo ajuste que ya hizo falta en la Fase 7, mismo motivo.
- `Binder.BindImports`: rama nueva, antes de la de nombre único — resuelve el módulo por el
  path completo y añade cada nombre listado como candidato de tipo, igual que ya hace la rama
  wildcard con `module.Types` pero limitado a los nombres pedidos. Solo alcanza tipos, igual
  que el import de nombre único ya existente — nunca una función o variable de módulo, que
  siguen fuera del alcance de un import con nombre (solo un wildcard las trae).
- LSP (`CompletionProvider.cs`): el bloque que ya leía `ImportSyntax.Alias` para el completado
  suelto ahora también lee `ImportSyntax.Members` y añade cada tipo listado. Más importante:
  `FindType` (usado tanto por el completado tras un punto como por los hints de parámetro)
  **no alcanzaba ni siquiera el import de nombre único ya existente** — un vacío preexistente,
  no introducido por esta fase — así que se corrigió ahí mismo para las dos formas (nombre
  único y lista selectiva) a la vez, ya que son la misma operación semántica repetida.

**Tests**: 3 en `ModuleEmitterTests.cs` (trae cada nombre listado, dejar fuera un nombre no
listado del mismo módulo, funciona en anotación de tipo) + 1 en
`LanguageServerWorkspaceTests.cs` (completado suelto solo con el tipo listado, no con su
hermano no listado). 2112/2112 tests en verde, 0 warnings.

**Commit**: `Feature: import selectivo de miembros (import X.{Y, Z})`

---

## Fase 9 — Import: wildcard de directorio (recursivo) — **Hecha**

**Estado actual**: `import a.*` solo alcanza los tipos declarados directamente en el módulo
`a` — un módulo es un directorio (`ModulePath.cs`), así que no llega a submódulos como `a.b`.
`Binder.cs`'s `_modules` es un diccionario por ruta exacta; no existe hoy ningún índice de
"todos los módulos bajo este prefijo".

**Es la pieza más difícil del bloque de imports** porque requiere ese índice nuevo, no solo
gramática.

**Diseño real**: `import a.*;` trae la unión de dos cosas — las declaraciones propias de `a`
si `a` existe como módulo por sí mismo, **y** las de todo módulo cuya ruta empiece por `a.` a
cualquier profundidad — no solo cuando `a` no resuelve por sí mismo (aunque ese es el caso que
antes fallaba en silencio, ya que un directorio que solo contiene subdirectorios no es un
módulo por sí mismo). No hizo falta ningún índice persistente nuevo: un recorrido lineal sobre
el conjunto de módulos de la compilación (`_modules.Values` en el binder,
`_modules.Keys` en `SurtrCompilation`) filtrando por prefijo es suficiente — el número de
módulos de un proyecto no justifica una estructura de índice dedicada, y esto está fuera del
camino de ejecución del VM (las reglas de rendimiento de `CLAUDE.md` no aplican al compilador).
Restringido a los módulos **de esta compilación**: un módulo ya compilado a imagen (`.surtrc`)
no tiene índice de directorio que recorrer.

**Dos sitios necesitaban el mismo ajuste, no solo uno**:
- `SurtrCompilation.BuildDependencyGraph` (validación previa al binder): antes solo pasaba una
  ruta exacta a `TryResolveImport`. Un wildcard gana su propia resolución ahí — un edge de
  `ModuleDependencyGraph` por cada módulo que coincide (exacto y/o cada submódulo), y solo se
  reporta `UnresolvedImport` si ninguno coincidió. `TryResolveImport` (que sigue existiendo
  para import con nombre/alias/lista selectiva) ya no necesita su rama de wildcard, que era
  inalcanzable desde este punto tras el cambio.
- `Binder.BindImports`: la rama wildcard ahora trae el módulo exacto (si existe) y cada
  submódulo (`ModulesUnderPrefix`), factorizando la lógica de "traer tipos + registrar para
  miembros de módulo" en `ImportWildcardModule` para no repetirla.
- LSP (`CompletionProvider.ImportedModules`, ya usada tanto por el completado suelto como por
  `FindType`): mismo ajuste — además de la ruta exacta, añade cada módulo cuya ruta empiece
  por el prefijo seguido de `.`.

**Tests**: 5 en `ModuleEmitterTests.cs` (directorio sin ficheros propios, unión con los tipos
propios del módulo exacto, recursión a más de un nivel, un módulo hermano no se cuela, las
funciones de un submódulo también llegan sin calificar) + 1 en
`LanguageServerWorkspaceTests.cs`. 2118/2118 tests en verde, 0 warnings.

**Commit**: `Feature: import wildcard de directorio (recursivo sobre submodulos)`

---

## Fase 10 — Clases (y enums, value classes, singletons, interfaces) declaradas dentro de un método — **Investigada, diferida deliberadamente**

**Estado actual**: totalmente ausente. `ParseStatement` (`Parser.Statements.cs:45-114`) no
tiene ninguna rama para `class`/`interface`/`enum`/`singleton`/`value class`;
`ParseDeclaration` (que sí las reconoce) nunca se invoca desde dentro de un cuerpo. No existe
ningún nodo `LocalClassDeclarationStatementSyntax` en el AST. Ni `Language-Syntax.md` §2.6 ni
§14.4 mencionan esta posibilidad — no es una feature diferida a propósito, es simplemente algo
que nunca se planteó.

**Se prototipó la parte de parser** (`LocalTypeDeclarationStatementSyntax` + una rama nueva en
`ParseStatement` para `class`/`enum` bare) y funcionaba — pero se revirtió antes de commitear
al confirmar que el binder no tiene ningún sitio downstream que lea ese nodo todavía:
`BodyBinder.BindStatement`'s `default` cae a `BoundNopStatement`, así que la declaración
parsearía correctamente y luego **no haría absolutamente nada**, en silencio — exactamente el
antipatrón que este mismo repositorio documenta como el error más común de §10.1
(`CLAUDE.md`: "la mayoría fueron un mismo error — un nodo que el parser produjo que nada
downstream leyó... un constructo nuevo no está terminado cuando parsea; lo está cuando algo lo
pide"). Publicar sintaxis que compila pero se ignora es peor que no publicarla, así que se
revirtió sin commit.

**Por qué es genuinamente la fase más grande del plan — hallazgo concreto, no solo estimación**:
un tipo local tiene que atravesar el mismo pipeline de tres fases que cualquier otro tipo
(declaración → jerarquía/miembros → cuerpos) para que sus propios métodos sean vinculables,
pero solo es *descubrible* leyendo la sintaxis del cuerpo de un método — y esa sintaxis no se
recorre hasta la fase 3 (`Binder.BindBodies`), que ya asume que la fase 2
(`Binder.MemberPhase`) terminó y cerró la lista completa de tipos (`_declared`) antes de que
ninguna fase 3 empiece. Descubrir un tipo local *durante* la fase 3 y querer ejecutar
declaración+jerarquía+miembros+cuerpos para él ahí mismo es reentrada sobre el propio
orquestador del binder (`_declared`, `_bodies`, `_initializers`, `_staticBlocks`, `_chains`,
`_attributes`, `_bound`), no una extensión aislada de `BodyBinder`.

**La vía que sí evita la reentrada, para cuando se retome**: descubrir los tipos locales antes,
no durante, la fase 3 — recorriendo el cuerpo sintáctico de cada método/constructor (ya
disponible en memoria desde el parseo completo, antes de que arranque ningún bind) en el mismo
punto donde `Binder.BindMembers` construye el `MethodSymbol` de ese método a partir de su
`MethodDeclarationSyntax`. Cada tipo local encontrado ahí se declara con el mismo
`Binder.DeclareType` que ya usa un tipo anidado ordinario, añadido a `_declared` y resuelto
in-line con `BindHierarchy`/`BindMembers` en el momento del descubrimiento — sin tocar el
bucle exterior, que sigue siendo un `foreach` normal. Con esto, la fase 3 ordinaria ya vincula
sus cuerpos sin ningún caso especial. El único hueco real que queda por resolver con ese
diseño: `Binder.DeclareType` añade el símbolo al scope de la clase/módulo contenedor
(`scope.AddCandidate`), lo que lo haría visible desde *cualquier* miembro de ese contenedor, no
solo desde el método que lo declaró — hay que pasar un scope aparte, nunca consultado por
nadie más, para esa única llamada, y en su lugar añadir el tipo a la cadena `_typeScope` de
`BodyBinder` (que hoy es un campo fijo, no encadenado por bloque como `_values`) justo cuando
la fase 3 alcanza la sentencia de declaración local — para lo cual `_typeScope` tiene que dejar
de ser `readonly` y `PushScope`/`PopScope` tienen que empujar/sacar un hijo suyo en paralelo a
`_values`, dando alcance de bloque a un nombre de tipo por primera vez en este binder.

**Decisiones de diseño que siguen en pie para cuando se implemente** (evitan abrir preguntas
nuevas, reutilizan mecanismo existente):
- **Metadata**: tipo anidado sintético de la clase/módulo contenedor. El nombre visible en el
  scope del método debe seguir siendo el nombre de fuente tal cual (`Foo`, no
  `$local$foo$0$Foo`) — es la clave con la que el propio código del método lo busca — así que
  el esquema `$categoria$contexto[$indice]` de `SyntheticNames.cs` solo puede aplicarse al
  nombre *emitido*/de metadata si `NamedTypeSymbol` llega a distinguir nombre-de-fuente de
  nombre-emitido; mientras no lo distinga, la alternativa más simple es aceptar el nombre de
  fuente tal cual como nombre real del tipo anidado, con la limitación documentada de que dos
  clases locales del mismo nombre en el mismo método (incluso en bloques hermanos que no se
  solapan) colisionan como declaración duplicada.
- **Captura**: igual que una lambda — solo locales "effectively final", copiados por valor al
  construir la instancia (mismos parámetros de constructor sintéticos que ya usa una lambda),
  nunca una celda compartida — el lenguaje no tiene celdas de variable. Con la resolución de
  tipos local ordinaria (sin heredar el scope de valores del método contenedor), un tipo local
  que referencia un local externo simplemente no resuelve el nombre hoy — un error claro de
  "nombre no encontrado", no una lectura silenciosamente incorrecta - así que capturar puede
  añadirse después sin que la ausencia actual sea insegura, solo incompleta.
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

**Decisión**: dado el hallazgo de reentrada anterior — que toca la orquestación central del
binder, compartida por las 2118 pruebas existentes — implementarlo con solidez en el tiempo
restante de esta sesión (que todavía debe cubrir las Fases 11 y 12) suponía un riesgo real de
dejar el binder en un estado inestable. Se deja diferida, con el camino de implementación ya
investigado y documentado arriba, en vez de forzar un commit de algo a medio terminar o
arriesgado. Sin cambios de código de esta fase — el prototipo de parser se revirtió (ver
arriba) precisamente para no dejar sintaxis que parsea y no hace nada.

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
- `Surtr.Stdlib.Tool` ahora escribe, junto a las imágenes, `build/native-link-names.txt` — la
  lista plana y ordenada de todo link name nativo que encontró en cualquier módulo o clase
  anidada de cualquier profundidad (`Program.CollectNativeLinkNames`), recorriendo el
  `SurtrModule` que el propio emisor ya construyó por cada fichero.
- **Se intentó embeber los `.surtrc` como `<EmbeddedResource>` en `Surtr.Core.csproj` (tal y
  como decía el boceto original de esta fase) y se encontró un ciclo de build real, no solo
  teórico**: un `ProjectReference` de `Surtr.Core` a `Surtr.Stdlib` (incluso con
  `ReferenceOutputAssembly="false"`, solo para forzar orden) hace que el build de
  `Surtr.Core` dispare el `AfterTargets="Build"` de `Surtr.Stdlib`, que a su vez ejecuta
  `dotnet run --project Surtr.Stdlib.Tool` — y `Surtr.Stdlib.Tool` referencia `Surtr.Compiler`,
  que referencia `Surtr.Core`, **el mismísimo proyecto que ya está a mitad de compilarse en el
  build exterior**. El resultado observado fue exactamente el "hang" que el propio comentario
  de `Surtr.Stdlib.csproj` ya advertía haber sufrido antes por una ruta distinta
  (`Surtr.Stdlib` → `Surtr.Stdlib.Tool` directo): "MSBUILD : error MSB4166: El nodo secundario
  '2' se cerró antes de tiempo" tras ~2:28 min. Se revirtió el cambio de inmediato.
  Arquitectónicamente es un problema de bootstrap, no un detalle de MSBuild: para compilar
  cualquier `.surtr` (incluida la propia stdlib) hace falta un `Surtr.Compiler` que ya
  referencia un `Surtr.Core` construido — así que un único `dotnet build` de `Surtr.Core` no
  puede a la vez compilar la stdlib y embeber el resultado en el propio ensamblado del que esa
  compilación depende. Las imágenes ya estaban commiteadas en el repo bajo
  `Surtr.Stdlib/build/` (confirmado con `git ls-files`) precisamente por esto — el propio
  README de `Surtr.Stdlib` ya decía "committed, not built on demand" antes de esta fase, más
  certero que el boceto de este plan.
- `SurtrStdlib` gana `StdlibModules` (`[Flags]`: `Core`/`Math`/`Collections`/`Text`/`All`) y dos
  sobrecargas de `LoadInto` que filtran por categoría — el segundo segmento del path del módulo
  (`surtr.math.Math` → `math`) — antes de delegar en el `LoadInto` sin filtrar. Sin cierre de
  dependencias real: confirmado por grep que hoy ningún módulo de la stdlib importa otro
  (`Surtr.Stdlib/src/surtr/**/*.surtr` no tiene ninguna línea `import`), así que una categoría
  ya es la unidad exacta que una selección necesita — si eso deja de ser cierto, la reintentona
  de punto fijo que `LoadInto` ya tiene sigue haciendo que una selección incompleta falle
  limpio (un módulo que nombra uno dejado fuera simplemente no resuelve) en vez de cargar con
  un agujero silencioso.
- **Test de desincronización temprana**: `SurtrStdlibTests.EveryNativeLinkNameTheStdlibBuildCompiledIsRegistered`
  lee `build/native-link-names.txt` y comprueba, contra un runtime de prueba, que
  `SurtrStdlib.RegisterNativeBodies` (cambiado de `private` a `internal` para que el test
  pueda invocarlo directamente) publica cada uno.
- `RegisterNativeBodies` se queda como está por lo demás (tabla pequeña, registrar un link name
  no usado es inofensivo); revisarlo solo si la tabla crece mucho.
- Actualizado el README de `Surtr.Stdlib` (reescribe el punto 1 de "qué falta" con el hallazgo
  del ciclo, en vez de dejar la descripción original que resultó no ser viable) y añadida una
  sección nueva (§16) a `docs/Runtime-Model.md`.

**Alcance real vs. boceto original**: el boceto pedía explícitamente recursos embebidos en
`Surtr.Core.csproj` como parte del "enlace nativo portable". Esa pieza concreta no es viable sin
una reestructuración de build en dos pasadas genuina (compilar con un toolchain que ya funcione,
generar las imágenes, y solo *después* embeberlas en una pasada separada) — fuera de alcance
razonable sin CI en el repo. Lo que sí se entrega y se prueba: selección por categoría y
detección de desincronización — dos de los tres pedidos originales, con el tercero (embeber
dentro de `Surtr.Core`) documentado como un hallazgo arquitectónico concreto en vez de
silenciado o forzado.

**Commit**: `Feature: cargador de STDLIB seleccionable y verificacion de enlace nativo (embeber en Surtr.Core resulto ser un ciclo de build)`

---

## Fase 12 — Barrido final de LSP y documentación — **Hecha**

**Última fase**, tras todo lo anterior (Fase 10 quedó diferida — ver arriba — así que no hay
tipos locales que barrer). El agente de investigación del LSP advirtió que
`CompletionProvider`/`SymbolResolver` están en sincronía con el binder **solo por
convención**, no por una abstracción compartida — esta pasada confirmó exactamente eso.

**Hallazgo real, no solo repaso**: `CompletionProvider.cs` ya se había actualizado fase a fase
(alias en la Fase 7, lista selectiva en la Fase 8, wildcard de directorio en la Fase 9) — pero
`SymbolResolver.cs` (hover/ir-a-definición) **nunca se tocó** en ninguna de esas tres fases.
`SymbolResolver.TypeCard` solo sabía leer el último segmento de una referencia de tipo y
buscarlo en las declaraciones propias del módulo o en un wildcard *exacto* — así que hover/
definición sobre `Core.Entity` (alias), sobre un tipo traído por `import X.{Entity}`
(selectivo), o sobre un tipo alcanzado solo a través de un submódulo de un wildcard de
directorio, no resolvían en absoluto, mientras que el mismo caso YA funcionaba en autocompletado.
Corregido:
- `FindTypesInWildcardImports` renombrado a `FindTypesInImports` y extendido para cubrir las
  tres formas (wildcard con submódulos, lista selectiva, import con nombre), replicando
  exactamente la lógica que `CompletionProvider.FindType`/`ImportedModules` ya tenían.
- `TryResolveThroughAlias` nuevo: `Alias.Tipo` se resuelve contra el módulo que el `import ...
  as Alias;` de ese fichero nombra, antes de intentar cualquier otra cosa — un alias necesita
  el segmento receptor completo, que el resto de `TypeCard` descarta de entrada.
- 3 tests nuevos en `LanguageServerWorkspaceTests.cs` (uno por forma), los tres fallaban antes
  de la corrección y pasan después — confirmando que era una regresión real, no una duda
  teórica.
- Revisado el resto de literales de `CompletionProvider`/`SymbolResolver` (lista de keywords,
  formato de hover) contra los cambios de las Fases 3-11: sin más desincronía encontrada.
- Pasada final de coherencia doc↔código: `Language-Syntax.md` (§2.1, §11) y
  `docs/Runtime-Model.md` (§16) ya se habían actualizado fase a fase; `CLAUDE.md` revisado y
  sigue siendo exacto — describe el mecanismo general de imports/scope que alias/selectivo/
  wildcard de directorio extienden, no algo que esas fases contradigan.

**Tests**: 3 nuevos en `LanguageServerWorkspaceTests.cs`. 2126/2126 tests en verde, 0 warnings.

**Commit**: `Fix: hover y definicion del LSP no resolvian alias/import selectivo/wildcard de directorio`

---

## Orden de ejecución y estado

| Fase | Descripción | Estado |
|---|---|---|
| 0 | Red de seguridad LSP + fix de keywords (+ 2 bugs reales de hover/imports encontrados y corregidos) | **Hecha** |
| 1 | Verificación del bug de interfaces built-in genéricas | **Hecha** (no reproducido; tests de regresión añadidos) |
| 2 | Sintaxis `=>` en métodos/propiedades | **Hecha** |
| 3 | Modificadores independientes por accessor | **Hecha** (alcance completo, incluye fix de bug de `sealed`) |
| 4 | Método → valor closure sin lambda | **Hecha** |
| 5 | Keyword `attribute`, target y retención | **Hecha** |
| 6 | API de reflexión de atributos en Surtr | **Hecha** |
| 7 | Alias de import | **Hecha** |
| 8 | Import selectivo de miembros | **Hecha** |
| 9 | Import wildcard de directorio | **Hecha** |
| 10 | Tipos locales dentro de métodos | **Investigada, diferida deliberadamente** (ver arriba — riesgo de reentrada sobre la orquestación central del binder) |
| 11 | Cargador de STDLIB seleccionable y portable | **Hecha en parte** — selección por categoría + test de desincronización hechos; embeber en `Surtr.Core` resultó ser un ciclo de build, documentado en vez de forzado |
| 12 | Barrido final LSP + docs | **Hecha** — encontró y corrigió una regresión real: hover/definición no resolvían alias/import selectivo/wildcard de directorio aunque el autocompletado sí |
| — | Operadores de instancia / `operator[]` | **Ya hecho** (`Plan-Globales-Nativos-Inline-Operadores.md`) |
| — | Varianza de genéricos | **Diferido a propósito** (§14.4), no planificado |
| — | Built-ins siempre disponibles | **Ya correcto**, sin cambios |

Cada fase termina con build completo + suite verde (`dotnet build Surtr.sln` +
`dotnet test Surtr.sln`) antes de su commit, siguiendo la misma disciplina que el plan de
operadores. Ninguna fase empieza antes de que la anterior esté commiteada.
