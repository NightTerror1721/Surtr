# Informe: ¿referencias o punteros en Surtr?

> Investigación y propuesta de diseño. Referencias con formato `ruta:línea` relativas a la raíz del
> repositorio. Este informe no modifica código fuente: es solo investigación y diseño.

## Índice

1. [Resumen ejecutivo y veredicto](#1-resumen-ejecutivo-y-veredicto)
2. [Investigación: dónde vive el estado hoy](#2-investigación-dónde-vive-el-estado-hoy)
3. [El problema central: tres estabilidades distintas](#3-el-problema-central-tres-estabilidades-distintas)
4. [Comparativa de enfoques](#4-comparativa-de-enfoques)
5. [Casos de uso reales evaluados](#5-casos-de-uso-reales-evaluados)
6. [La alternativa «sin referencias» y sus límites](#6-la-alternativa-sin-referencias-y-sus-límites)
7. [Propuesta 1 — `ref<T>` de almacenamiento (MVP recomendado)](#7-propuesta-1--reft-de-almacenamiento-mvp-recomendado)
8. [Propuesta 2 — parámetros `out` / `inout` / `ref` estilo C#](#8-propuesta-2--parámetros-out--inout--ref-estilo-c)
9. [Propuesta 3 — células estilo Lua para capturas mutables](#9-propuesta-3--células-estilo-lua-para-capturas-mutables)
10. [Costes y riesgos transversales](#10-costes-y-riesgos-transversales)
11. [Roadmap recomendado por fases](#11-roadmap-recomendado-por-fases)
12. [Decisiones abiertas](#12-decisiones-abiertas)

---

## 1. Resumen ejecutivo y veredicto

**Veredicto: sí vale la pena, pero referencias, no punteros.** Concretamente:

1. **Sí** a una primitiva de *referencia a almacenamiento* (`ref<T>`) que apunta a **un campo de
   instancia**, **un campo estático/variable de módulo**, y (más adelante) **un elemento de array**
   — nunca a una dirección de memoria ni a un local de pila en v1. Representada como una entidad
   registrada `{dueño, slot}` (handle estructurado), no como puntero crudo.
2. **Sí** a construir encima el azúcar de parámetros `out` / `inout` estilo C#, porque con la
   primitiva anterior el ABI de llamadas no cambia ni un byte: una referencia es un valor de un
   slot bajo `TagReference`, igual que cualquier objeto.
3. **No** a punteros crudos interiores (`SurtrRawValue*` visibles desde el lenguaje): son
   inválidos para campos (el array `Fields` de una instancia es gestionado por el CLR y puede
   moverse con el GC), semánticamente colgantes para locals (la pila no se realoca, pero el slot se
   reutiliza al morir el frame) y directamente inservibles a través de un generador (el frame se
   copia fuera de la pila en cada `yield`).
4. **No ahora** a upvalues abiertas/cerradas estilo Lua para capturar `var` mutables en closures.
   Es factible aquí (la pila de datos nunca se mueve), pero la regla actual («una closure solo
   captura `let`», aplicada en el binder) es simple, ya está implementada punta a punta, y la
   Propuesta 1 le da al programador una vía de escape limpia (`let r = ref contador;`) cuando
   necesita estado compartido mutable.

El argumento decisivo no es ergonomía sino coherencia arquitectónica: **Surtr ya es un lenguaje de
handles**. Un objeto Surtr es su id de 32 bits en el entity registry (`SurtrValue.cs:59`,
`SurtrEntityRegistry.cs:95`), estable aunque el array de entidades crezca con `Array.Resize`
(`SurtrEntityRegistry.cs:507`). Una referencia a campo como entidad `{idDueño, índiceDeSlot}` es
exactamente el mismo truco un nivel más abajo: estabilidad por indirección, cero punteros crudos,
y el GC existente la rastrea gratis vía `VisitReferences`.

Coste honesto: crear una referencia asigna (patrón `BoxInt`: registro + safepoint) y cada acceso
paga una indirección más que un `FieldGet` directo. Es una herramienta para *guardar lvalues*, no
para sustituir accesos directos en bucles cerrados.

---

## 2. Investigación: dónde vive el estado hoy

### 2.1 El valor de 8 bytes

Todo valor Surtr es un `SurtrValue` NaN-boxed de 64 bits: tag en los 16 bits altos, payload en los
bajos (`src/Surtr.Core/Runtime/Objects/SurtrValue.cs:15-32`). Las familias hoy son `int`, `float`,
`bool`, `char`, `reference` y `absent` (tags `0xFFF1`–`0xFFF6`, `SurtrValue.cs:21-56`). Una
referencia es **su payload de 32 bits**: el id de entidad (`AsReference`, `SurtrValue.cs:134-137`;
`NullRef = 0` en `:59`). Quedan libres los tags `0xFFF7`–`0xFFFF`, con un camino de crecimiento ya
documentado en el propio código (`SurtrValue.cs:157-163`) y analizado en detalle en
`docs/Informe-i64-y-f32.md`.

Consecuencia estructural clave para este informe: **un valor ocupa exactamente un slot**, siempre,
en todas partes (operandos, locals, campos, elementos de array/dict). Romper ese invariante fue
descartado expresamente al diseñar los tipos de valor: «romper el stride es el cambio más caro del
espacio de diseño» (`docs/Plan-TiposDeValor.md:86`; reafirmado en
`docs/Informe-i64-y-f32.md:154`). Cualquier representación de referencia debe caber en un slot.

### 2.2 Locals: una pila de datos fija, unmanaged, que nunca crece

Los locals y los operandos comparten una única **pila de datos**: un bloque plano de
`SurtrRawValue` en memoria unmanaged (`src/Surtr.Core/VM/SurtrVirtualMachine.cs:28-33`,
`:117-119`). Se aloja con ceros una vez, en el constructor, con capacidad fija por defecto de
64 K slots (512 KB, `SurtrVirtualMachine.cs:66`, `:147`) y **nunca crece**:

> «Fixed capacity, on purpose. Neither stack ever grows. A growable data stack would have to be
> addressed by index rather than by pointer, because a reallocation would dangle every `sp` already
> spilled in a suspended dispatch loop.» (`SurtrVirtualMachine.cs:35-40`)

El desbordamiento es un trap (`DataStackOverflow`, `SurtrVirtualMachine.cs:4442-4443`), no un
realloc. Esta decisión — tomada por la re-entrancia de funciones nativas, no por las referencias —
elimina de raíz el peligro clásico «tomé un puntero a un local y la pila creció». En Surtr eso no
puede pasar: **la dirección de un slot de la pila es estable durante toda la vida de la máquina**.

Un frame entra sin copiar nada: la base del callee es `sp - argsCount` (`PushEntryFrame`,
`SurtrVirtualMachine.cs:475-479`), los argumentos quedan colocados como locals `0..N-1`, y los
locals restantes se ponen a cero (`:483-497`). El frame guarda esa base como puntero crudo
(`SurtrCallFrame.Base`, `src/Surtr.Core/VM/SurtrCallFrame.cs:48`); el array de frames sí es
gestionado porque contiene objetos CLR (`SurtrCallFrame.cs:28-35`).

Leer y escribir un local es aritmética base+índice directa sobre ese puntero: `Ldl*` lee
`frameBase[idx]` (`SurtrVirtualMachine.cs:1280-1294`), `Stl*` escribe (`:1296-1310`), `IncLocal`
hace `i += 1` entero en sitio sin tocar la pila de operandos (`:1315-1321`). Los valores multi-slot
(value classes) son bloques contiguos que se copian enteros entre local/pila/campo
(`LoadValueLocal`/`StoreValueLocal`/`LoadLocalField`/`StoreLocalField`,
`SurtrVirtualMachine.cs:3728-3770`; el layout plano y el plegado de sub-campos en índices absolutos
están descritos en `docs/Opcodes.md:295-299`).

Lo que **sí** invalida un local no es la memoria sino el significado: al retornar el frame, `sp`
retrocede y los siguientes frames reutilizan esos slots. Un hipotético «puntero a local» seguiría
siendo una dirección válida apuntando a basura nueva. Y hay un segundo invalidador específico de
Surtr: los generadores (§2.6).

### 2.3 Campos de instancia: un array gestionado por objeto

Una instancia de clase Surtr es `class-pointer + bloque plano de campos`
(`src/Surtr.Core/Runtime/Objects/SurtrInstance.cs:9-25`). El almacenamiento es
`internal readonly SurtrValue[] Fields` — **un array CLR gestionado** (`SurtrInstance.cs:29`),
indexado por `SurtrFieldInfo.Slot`, con el layout heredado plegado por el linker
(`SurtrInstance.cs:14-19`). Hay incluso un indexador que devuelve `ref SurtrValue` directo al slot
(`SurtrInstance.cs:47-51`) — un byref CLR perfectamente válido *transitoriamente*, dentro de un
método C#.

En la VM, `FieldGet` resuelve el campo por índice de tabla (resuelto al enlazar, no por nombre),
prueba si es campo nativo y, si no, hace `instance.Fields[slot].Raw`
(`SurtrVirtualMachine.cs:2548-2577`); `FieldSet` simétrico (`:2579-2607`). Los campos nativos
(declarados por el puente C#) no tienen slot: cada acceso llama al getter/setter del host
(`SurtrVirtualMachine.cs:2553-2571` y `:2584-2601`; convención documentada en
`docs/Opcodes.md:272`).

Implicación crítica: **no existe ninguna dirección estable para el slot de un campo de instancia**.
El array `Fields` vive en el heap del CLR, que puede reubicarlo en una colección compactante; ni la
VM ni nadie lo pinea. Un `fixed` temporal funciona; un puntero guardado en un slot Surtr (que
sobrevive a llamadas, safepoints y colecciones) es un bug latente. Nota aparte: el propio equipo ya
documentó esta limitación de plataforma para otro caso — netstandard2.1 no tiene ref fields y el
array de entidades es gestionado, así que «a pointer is off the table»
(`SurtrEntityRegistry.cs:538-545`).

### 2.4 Statics y variables de módulo: storage unmanaged con dirección pre-resuelta

Aquí el panorama es el opuesto. El linker aloja el storage estático de cada tipo/módulo en memoria
unmanaged, cero-inicializada, una sola vez al enlazar
(`src/Surtr.Core/Runtime/Classes/SurtrTypeLinker.cs:594-596`) y entrega a cada campo estático la
dirección cruda de su propio slot (`BindStaticStorage`,
`SurtrTypeLinker.cs:607-615`; `SurtrRawValue* StaticAddress` en
`src/Surtr.Core/Runtime/Classes/SurtrFieldInfo.cs:37`). Por eso `StaticFieldGet` es «una carga
indirecta»: `*sp++ = *field.StaticAddress` (`SurtrVirtualMachine.cs:2610-2639`, concretamente
`:2637`), y `StaticFieldSet` escribe por esa misma dirección (`:2690`). Las variables de módulo son
statics del módulo y usan exactamente el mismo camino (`docs/Opcodes.md:278-280`).

Es decir: **Surtr ya tiene, internamente, «referencias» a statics en forma de punteros crudos** —
y funcionan porque ese storage es el único cuya dirección es realmente permanente (muere solo con
el runtime).

### 2.5 Closures: captura por copia, regla effectively-final

Las closures capturan **valores copiados, no variables compartidas**: `NewClosure` saca los
capturados de la pila y los congela en `SurtrClosure.UpValues` (`SurtrVirtualMachine.cs:2723-2761`;
`src/Surtr.Core/Runtime/Objects/SurtrClosure.cs:24-42`). `UpValueGet` lee; **no existe setter por
decisión de lenguaje** (`docs/Opcodes.md:286-290`, `SurtrClosure.cs:24-31`).

La regla que lo hace sound se aplica en el binder, en el punto de captura: una lambda solo puede
capturar un local `let` (assign-once), un parámetro o un miembro; capturar un `var` local es error
de compilación («'X' is reassigned, so a lambda cannot capture it; declare it 'let'.»,
`src/Surtr.Compiler/Binding/BodyBinder.cs:267-303`, mensaje en `:287-294`; regla de superficie en
`docs/Language-Syntax.md:2642-2652`). La emisión levanta la lambda a función de módulo cuyo cuerpo
lee capturas con `UpValueGet` (`src/Surtr.Compiler/CodeGen/MethodBodyEmitter.cs:5906-5913`), y
rechaza capturar valores multi-slot inline (`MethodBodyEmitter.cs:4908-4912`). Una lambda sin
capturas ni siquiera aloca: usa la función canónica cacheada (`NewFunction`,
`SurtrVirtualMachine.cs:2763-2776`).

Consecuencia para este informe: **hoy no existe captura mutable**, así que el problema de
«upvalues abiertas» (¿qué pasa cuando el frame dueño muere?) simplemente no se ha pagado todavía.

### 2.6 Generadores: los frames migran

Un generador suspendido **copia su frame fuera de la pila** al heap gestionado: `Yield` copia
`[frameBase, sp)` a `SurtrGenerator.Slots` (array CLR de `SurtrValue`,
`src/Surtr.Core/Runtime/Objects/SurtrGenerator.cs:98-101`, alloc en `:199`; copia en
`SurtrVirtualMachine.cs:4083-4136`) y `GenResume` lo reconstruye **en cualquier base libre**
(`PushGeneratorFrame`, `SurtrVirtualMachine.cs:842-890`; camino compilado en `:3953-3990`). Los
locals conservan sus índices, no sus direcciones — es lo que hace el frame reubicable
(`docs/Opcodes.md:655`).

Esto mata definitivamente cualquier esquema basado en direcciones: una referencia a un local de un
generador suspendido sería inválida tras el primer `yield` **sin que haya pasado nada raro** — sin
realloc, sin GC, sin overflow. Es la migración normal del sistema.

### 2.7 GC: raíces, safepoint y registry

El recolector es mark-sweep con nursery sobre el entity registry
(`SurtrEntityRegistry.CollectGarbage`, `SurtrEntityRegistry.cs:336-430`). Raíces: (1) escaneo de la
pila de datos completa con test de tag (`MarkIfReference`, `:349-353`, `:432-437`), (2) los bloques
estáticos por lista de slots-referencia construida en link time (`:355-360`), (3) raíces explícitas
de la máquina — closure de cada frame vivo y excepción en vuelo (`_roots`,
`SurtrVirtualMachine.cs:78-93`, expuestas en `:188-193`) — y (4) pins del host. Cada entidad
declara sus referencias vía `VisitReferences` (ejemplos: `SurtrInstance.cs:53-64` por lista de
slots-referencia; `SurtrClosure.cs:163-170` recorriendo upvalues). La recolección corre en el
**safepoint** único tras cada opcode que asigna (`Safepoint`, `SurtrVirtualMachine.cs:4149-4155`).

Detalle importante para referencias: el array `Entities` del registry es gestionado y crece con
`Array.Resize` (`ExpandCapacity`, `SurtrEntityRegistry.cs:494-522`, resize en `:507`). Ni siquiera
un puntero a *entidad* sería estable; el handle `SurtrRef` lo es. El diseño de referencias debe ser
handles hasta el final.

### 2.8 Interop nativo: `out` se pliega al retorno, `ref`/`in` se rechazan

El puente C# hoy: un método nativo con parámetros `out` los pliega al retorno (tupla);
`ref`/`in` se rechazan (`src/Surtr.Interop/README.md:83`; detección en
`SurtrReflectionScanner.cs:102`, `:281-283`, `:327`; manejo en
`SurtrReflectionInvoker.cs:285`). Es decir, **el escenario interop que motivaría refs ya choca hoy
con un muro duro**: no hay forma de exponer un método C# con `ref` tal cual, y los `out` exigen la
reconstrucción del resultado en el lado script.

### 2.9 Superficie de sintaxis disponible

`ref` y `out` **no están reservadas** (`docs/Language-Syntax.md:76-94`); se pueden introducir como
palabras contextuales siguiendo el precedente de `this`/`super`/`value`/`attribute`
(`Language-Syntax.md:108-113`) o reservarse como se hizo con `generator`/`yield`
(`:101-106`). `let` es assign-once y `var` mutable (`Language-Syntax.md:35-38`); existen
propiedades con getter/setter (`:828`), tuplas con destructuring (`:1534-1545`), value classes de
campos `let` únicamente (`:639`), y el formato de imagen va por `FormatVersion = 11`
(`src/Surtr.Core/Bytecode/Image/SurtrModuleImage.cs:150`) con 16 valores de opcode libres
(`0xF0`–`0xFF`, cola de `src/Surtr.Core/Bytecode/OpCode.cs`; política de números finales y bump de
versión en `docs/Opcodes.md:82-95`).

---

## 3. El problema central: tres estabilidades distintas

«Guardar una referencia a otra variable o campo» cruza tres mundos de almacenamiento con
propiedades de invalidación distintas:

| Destino | Dirección estable | Qué la rompería | Mecanismo de fallo |
|---|---|---|---|
| Local de pila | Sí (memoria), mientras viva la VM | Retorno del frame; suspensión de generador | Reutilización del slot por otro frame; copia del frame a `Slots` y vuelta a otra base |
| Campo de instancia | **No** | Colección compactante del CLR | El array `Fields` (`SurtrInstance.cs:29`) se reubica; ningún pin lo protege |
| Static/var de módulo | Sí, para siempre | Solo el teardown del runtime | Storage unmanaged alocado una vez en link (`SurtrTypeLinker.cs:594-615`) |
| Elemento de array | No (semántica) | `ArrInsert`/`ArrRemoveAt` | Desplazamiento de elementos; el índice pasa a señalar otro dato |

Conclusiones que se siguen directamente:

1. **Un puntero crudo uniforme es imposible.** Para campos de instancia choca contra el GC del CLR
   (§2.3); para locals es memory-safe pero semánticamente colgante tras el retorno (§2.2); para
   generadores se rompe por diseño (§2.6). Habría que inventar pinning de instancias o mover los
   fields a memoria unmanaged — ambos mucho más caros que el problema que resuelven.
2. **Una managed reference persistente estilo C# (byref guardable) tampoco cabe.** Un byref CLR no
   puede vivir en el heap (no hay ref fields en clases; además netstandard2.1 ni siquiera los
   declara, `SurtrEntityRegistry.cs:538-545`) y no se representa en 8 bytes NaN-boxed sin trucos
   frágiles. C# puede porque su GC entiende byrefs como raíces interiores; el GC de Surtr escanea
   slots con un test de tag y no sabría qué hacer con ellos.
3. **Queda la familia de indirecciones**: célula/box explícito, o handle estructural. Ambas caben
   en un slot como entidades normales, son trazables por el GC existente, y sobreviven a todo lo de
   la tabla (retorno de frame, migración de generador, compactación CLR) porque nunca guardan
   direcciones.

---

## 4. Comparativa de enfoques

### 4.1 A. Puntero crudo (`SurtrRawValue*` en el payload de un slot nuevo)

- **Cómo sería**: tag propio (hay 9 libres, `SurtrValue.cs:157-163`) cuyo payload es una dirección
  de 32 bits… que ni siquiera alcanza para espacio de direcciones de 64 bits; habría que guardar
  64 bits crudos sin tag, colisionando con floats — exactamente el callejón sin salida que ya
  analizó el informe de i64 para valores etiquetados (`docs/Informe-i64-y-f32.md:42`).
- **Viabilidad real**: nula para campos (§2.3), trampa para locals (§2.2), rota para generadores
  (§2.6). Además obligaría a que el collector distinguiera «este slot es dirección» en el escaneo
  de pila, y a definir qué pasa con una referencia a un slot liberado.
- **Veredicto**: descartado. Es la opción que el propio diseño del VM hizo inviable a propósito
  (pila fija sí, pero frames reutilizables y migrables; fields gestionados).

### 4.2 B. Managed reference persistente (byref estilo C#)

- **Cómo sería**: un valor cuyo significado es «interior pointer con reporting al GC», como
  `ref int` de C# que puede escapar del método.
- **Viabilidad real**: el CLR prohíbe byrefs en el heap; netstandard2.1 carece de ref fields
  (`SurtrEntityRegistry.cs:538-545`); el GC de Surtr tendría que aprender una segunda familia de
  raíces interiores; y el invariante de un-slot-por-valor se mantendría de milagro.
- **Veredicto**: descartado. Es pelear contra ambas plataformas (CLR y la propia VM) para obtener
  algo que un handle da con menos riesgo.

### 4.3 C. Box/célula explícita (estilo Python/Ruby/Kotlin)

- **Cómo sería**: el programador (o el compilador) envuelve el valor en un objeto caja mutable:
  `let caja = Cell(enemy.health)`; escribir es `caja.set(v)`.
- **Viabilidad real**: alta — casi trivial. De hecho el material ya existe a medias: un box es una
  instancia ordinaria cuyos slots el GC ya sabe recorrer (`docs/Opcodes.md:297`,
  `BoxValue`/`UnboxValue` en `docs/Opcodes.md:307-308`). Bastaría una clase built-in `Cell<T>`
  con `value` propiedad `var` y azúcar de compilación opcional.
- **Contrapartida**: cada acceso es una llamada/property (dispatch), la creación asigna, y el
  original (campo o local) queda desconectado: la célula *es* el almacenamiento nuevo, no una vista
  del viejo. Sirve para estado compartido de closures, no para `swap(a, b)` ni para `out` reales.
- **Veredicto**: útil como complemento barato (y probablemente acabará existiendo como clase
  built-in de la stdlib), pero no responde a la pregunta central del informe: tomar una referencia
  a *otra variable o campo ya existente*.

### 4.4 D. Handle estructural: referencia como entidad `{dueño, slot}` (recomendado)

- **Cómo sería**: una referencia es una entidad registrada pequeña — `SurtrRefSlot { Dueño:
  SurtrRef (0 para statics), Slot/Index: int, DirecciónEstática: SurtrRawValue* (solo statics) }` —
  que viaja por el lenguaje como un valor normal bajo `TagReference`. Leer/escribir a través de
  ella desreferencia en el momento del acceso: `registry.Get(dueño)` + carga/escritura del slot
  (o escritura por `StaticAddress` para statics).
- **Por qué es el punto dulce en esta VM**:
  - Repite el patrón dominante del proyecto: identidad por handle estable (`SurtrRef`), nunca por
    dirección (`SurtrEntityRegistry.cs:95`, `:507`).
  - El GC la rastrea sin tocar nada: `VisitReferences` marca al dueño
    (`SurtrInstance.cs:53-64` como plantilla); una referencia mantiene vivo al objeto que aloja el
    slot, que es exactamente la semántica correcta.
  - Sobrevive a retorno de frames (la entidad vive en el heap), a migración de generadores (no
    guarda direcciones de pila) y a compactación CLR (el dueño se resuelve por id en cada acceso).
  - El coste de acceso es conocido y acotado: lo que hoy paga `FieldGet`
    (`SurtrVirtualMachine.cs:2574`: lookup `entities[id]` + cast) más una indirección por el
    propio cell.
- **Coste**: creación asigna (registro + safepoint, patrón `BoxInt` de
  `SurtrVirtualMachine.cs:1797-1835`). Igualdad: dos referencias al mismo slot son dos entidades
  distintas salvo que se internen (decisión abierta, §12).
- **Veredicto**: recomendado como primitiva única sobre la que montar todo lo demás.

### 4.5 E. Upvalues abiertas/cerradas estilo Lua (solo capturas mutables)

- **Cómo sería** (Lua 5.x): cada variable capturable-mutable obtiene una *celda*; mientras el frame
  vive, la celda está «abierta» y apunta al slot de la pila; cuando el frame muere, la celda se
  «cierra» copiando el valor al heap, y las closures que la compartían pasan a ver el heap. Todas
  las closures sobre la misma variable comparten celda y ven las escrituras de las demás.
- **Factibilidad concreta aquí**: sorprendentemente buena, porque la pila de datos **nunca se
  mueve** (`SurtrVirtualMachine.cs:35-40`): la parte «abierta» de Lua (que allí exige lista por
  hilo y parcheo al encoger la pila) aquí sería un puntero estable sin más. El cierre en retorno
  engancha donde ya se caminan frames (retorno, unwinding). Pero hay una arruga específica de
  Surtr: **los generadores**. `Yield` copia el frame fuera de la pila (`SurtrVirtualMachine.cs:4083-4136`)
  y `GenResume` lo devuelve a otra base (`:842-890`); las celdas abiertas habría que cerrarlas al
  suspender y re-abrirlas (recalcular dirección) al reanudar, o prohibir locals capturados-mutables
  en generadores. Es costo real en dos instrucciones calientes de una característica ortogonal.
- **Qué compraría**: captura por referencia de `var` locales (contadores compartidos, acumuladores
  en callbacks).
- **Veredicto**: posponible. La regla actual (`let`-only, `BodyBinder.cs:267-303`) es simple,
  sound y ya enseña el remedio en el propio diagnóstico; y con la Propuesta 1 el escape natural es
  `let r = ref contador;` capturando `r` (binding inmutable de un objeto mutable). Detalle en §9.

### 4.6 F. Nada (statu quo)

Legítimo y detallado en §6. Cubre la mayoría del día a día con setters/properties y tuplas; falla
de forma puntual y repetible en interop (`ref` C# imposible hoy, §2.8), en APIs de patrón
lvalue (`TryGet`, `swap`), y en navegación profunda repetida.

### 4.7 Tabla resumen

| Criterio | A. Puntero crudo | B. Byref persistente | C. Célula | D. Handle `{dueño,slot}` | E. Upvalues Lua |
|---|---|---|---|---|---|
| Caben en 1 slot NaN-boxed | No (colisión tags) | No (CLR lo prohíbe) | Sí (entidad) | Sí (entidad) | Sí (celda-entidad) |
| Seguro frente a GC CLR | No | Sí (pero irrepresentable) | Sí | Sí | Sí |
| Válido a través de `yield` | No | — | Sí | Sí | Requiere cerrar/reabrir |
| Referencia a storage *existente* (campo/local ajeno) | Sí (frágil) | Sí | No (storage nuevo) | **Sí** | Solo locales propios |
| Cambios en VM | Grandes | Grandes | Mínimos | Acotados (4–6 opcodes) | Medios + generadores |
| Cambios en GC | Grandes | Grandes | Ninguno | Ninguno (VisitReferences) | Pequeños |
| Hot path | — | — | Llamada/acceso por propiedad | +1 indirección vs FieldGet | Indirección en capturados |

---

## 5. Casos de uso reales evaluados

1. **`swap(a, b)`** — Imposible hoy sin tuplas manuales (`(a, b) = (b, a)` cubre el caso concreto
   pero no se factoriza en una función genérica). Con `fun swap<T>(a: ref<T>, b: ref<T>)` y
   handle-refs, sale directo y sin cambios de ABI. *Veredicto: caso canónico a favor.*
2. **Parámetros `out` script→script** — Hoy: devolver tupla y destructurar en el llamador;
   funciona pero contamina cada sitio de llamada y duplica firmas cuando hay varios resultados +
   valor principal. Con `out` como azúcar sobre refs (Propuesta 2), la llamada se ve como C#. 
   *Veredicto: valor alto, costo bajo una vez existe P1.*
3. **Interop C#: métodos con `ref`/`out`** — Hoy `out` se pliega al retorno y `ref`/`in` se
   rechazan (`src/Surtr.Interop/README.md:83`, `SurtrReflectionScanner.cs:102,281,327`). Con refs
   como valores, el source generator puede envolver cualquier método C#: lee el cell, invoca,
   escribe de vuelta. Nota honesta: para structs nativos inline el marshaling sigue reconstruyendo
   el struct (eso no cambia); lo que se gana es poder exponer la firma original sin replegar, y
   mutar estado Surtr desde el host sin devolver nada. *Veredicto: desbloqueo real, no cosmético.*
4. **Navegación profunda sin repetir** — `ref hp = party[i].equipment.weapon.damageBonus`
   evalúa la cadena una vez; después `hp += 5`. Sin refs, cada acceso repite N dispatches de
   `FieldGet` (`SurtrVirtualMachine.cs:2548-2577`) o requiere copiar-modificar-escribir el camino
   completo (imposible si un eslabón es `var` de instancia con setters). *Veredicto: el caso de uso
   de rendimiento/legibilidad más frecuente en gameplay scripting.*
5. **Estado mutable compartido en closures/callbacks** — Hoy prohibido por diseño para locales
   (`let`-only) y posible a mano con objetos contenedor. Con P1, `let hits = ref score;` capturada
   por bindings inmutables da contadores compartidos sin células nuevas de VM. *Veredicto: se
   resuelve gratis con P1; no necesita E (§9).*
6. **APIs de patrón lvalue en stdlib** — p.ej. un futuro `map.entry(k)` que devuelva referencia
   escribible, o vistas de elemento de array. Elementos de array tienen el problema de
   invalidación por `ArrInsert`/`ArrRemoveAt` (`docs/Opcodes.md:463-464`): la referencia por
   índice señalaría otro elemento tras un shift. *Veredicto: aplazar arrays a una fase posterior
   con política explícita (generaciones o documento «la referencia observa posiciones, no
   elementos»).*

---

## 6. La alternativa «sin referencias» y sus límites

Lo que el lenguaje ofrece hoy y con qué se sustituiría cada caso:

- **Devolver valores + destructuring**: `let (ok, v) = tryLookup(k);` — cubre `out` funcional.
  Multi-retorno ya es ciudadano de primera (tuplas `TupPack/TupUnpack`, `docs/Opcodes.md:480-481`;
  retornos multi-slot `ReturnValues`, `docs/Opcodes.md:639`).
- **Properties y setters**: `enemy.stats.health = v;` — cubre escritura dirigida, pero no
  *parametrizar* un destino: no se puede pasar «dónde escribir» a una función.
- **Objetos contenedor escritos a mano** (clase con campo `var`): cubre estado compartido de
  closures; es el equivalente manual de la opción C.
- **Interop**: replegar `out` a tuplas ya implementado (§2.8); para `ref`, reescribir el API C#
  expuesto (cuando el API es propio) o renunciar (cuando no lo es: Unity está lleno de `out`).

Dónde se queda corta de forma estructural (no cosmética):

1. **Funciones que escriben en el llamador** requieren duplicar lógica o devolver «qué escribir»,
   lo que fuerza al llamador a conocer la estructura destino — justo lo que un `out` encapsula.
2. **Algoritmos genéricos sobre lvalues** (swap, minmax con escritura, acumuladores) no se pueden
   expresar una sola vez.
3. **Cadenas de navegación repetidas** pagan N dispatches por uso, y en código caliente eso se
   nota; la alternativa manual (copiar a temporal, operar, escribir de vuelta) reintroduce el bug
   clásico de obsolescencia que las refs evitan.
4. **Interop refleja el muro**: cada firma C# con `ref` exige adaptador escrito a mano.

Ni siquiera con todo esto deja de ser un lenguaje usable — lo es hoy. La pregunta es si la pieza
que falta se paga con cambio acotado. Con el diseño D, sí.

---

## 7. Propuesta 1 — `ref<T>` de almacenamiento (MVP recomendado)

### 7.1 Semántica

Una expresión de tipo `ref<T>` es una **vista escribible de un slot ajeno**: leer a través de ella
devuelve el valor actual del slot; escribir a través de ella lo reemplaza. La referencia no copia
ni mueve nada en creación: apunta al almacenamiento existente (campo de instancia, static/var de
módulo). En v1 **no** se pueden tomar referencias a: locals ni parámetros (error de compilación
claro, con sugerencia de la alternativa), elementos de array (aplazado, §12), slots de value class
multi-campo como destino independiente (vienen de la mano del campo contenedor), ni campos nativos
se excluyen: se soportan vía getter/setter (ver 7.4).

### 7.2 Sintaxis propuesta

`ref` como palabra **contextual** (precedente: `value`, `attribute`, `Language-Syntax.md:108-113`);
en posición de tipo es tipo:

```surtr
// Tipo y declaración: binding assign-once (es 'let' por naturaleza)
let hp: ref<int> = ref enemy.stats.health;
let maxHp: ref<float> = ref Enemy.defaultMaxHp;      // static
let name: ref<string?> = ref enemy.displayName;

hp += 25;                 // escritura a través de la referencia (auto-deref en contexto lvalue)
if hp < 10 { die(enemy);} // lectura: auto-deref en contexto de valor
print(hp);                // imprime el int apuntado

// Explícito cuando se quiere la referencia misma:
passAround(hp);           // fun passAround(h: ref<int>)
```

Regla de ambiguidad (misma solución que C#): el binding de una variable `ref` no se reasigna nunca
(es declarado una vez); por tanto `hp = X` y `hp += X` siempre escriben *a través*. Quien quiera
re-apuntar crea otra `let`. Esto elimina la doble lectura del `=` y simplifica flow analysis.

Alternativa explícita (más ruidosa, cero ambigüedad, decidir en fase 0): `hp.value` para leer y
`hp.value = X` para escribir. El resto del diseño es idéntico; solo cambia el binder.

### 7.3 Tipado y restricciones

- `ref<T>` es un tipo ordinario: puede aparecer en locals (`let`), parámetros, retornos, campos de
  clase y capturas de lambda (la captura copia el *binding* — la entidad — que es inmutable en su
  puntería; escribir a través sigue funcionando: consecuencia deliberada, ver §5.5).
- **T restringido a tipos de un slot**: primitivos, nullable de primitivo, `string`, clases,
  interfaces, `unknown`. Excluidos en v1: value classes multi-slot (un `ref` apunta a UN slot;
  extender a bloques es trivial después: añadir `Width` al cell y usar los opcodes de bloque
  existentes como guía), otros `ref<U>` (sin refs de refs).
- No hay `null` de referencias: crear a través de un receptor nulo trapa
  `NullReferenceException` igual que `FieldGet` (`SurtrVirtualMachine.cs:2574`). `ref<T>?` queda
  prohibido en v1 (evita una familia entera de chequeos).
- `ref<T>` no es argumento de genéricos en v1 (fuera de `ref` mismo): evita interactuar con
  varianza e inference (`docs/Plan-Varianza-Genericos.md` queda intacto).
- Igualdad: `REQ` compara identidad de la entidad-cell (dos `ref`s al mismo slot tomados por
  caminos distintos no son `REQ`-iguales salvo interning; ver §12). Comparar `ref<T>` con `T` es
  error de compilación.

### 7.4 Runtime y opcodes

Entidad nueva en `Surtr.Core/Runtime/Objects`:

```csharp
public sealed class SurtrRefSlot : SurtrObject {
    internal readonly SurtrRef Owner;            // 0 => static (usa StaticAddress)
    internal readonly SurtrRawValue* StaticAddress; // solo statics; storage permanente (SurtrTypeLinker.cs:611-615)
    internal readonly int Slot;                  // índice de campo (instancia) — o de elemento (futuro)
    // VisitReferences: marcar Owner (nada que marcar si es static)
}
```

Para **campos nativos** (sin slot, getter/setter del host, §2.3): el cell lleva la
`SurtrNativeFieldInfo` y `RefGet`/`RefSet` llaman a sus entry points — mismo contrato que ya
ejecutan `FieldGet`/`FieldSet` (`SurtrVirtualMachine.cs:2553-2601`), así el interop C# queda
cubierto sin excepciones.

Opcodes (valores libres `0xF0`–`0xFF`, numeración final + bump `FormatVersion` según la política de
`docs/Opcodes.md:82-95`):

| Opcode propuesto | Encoding | Stack | Baja de |
|---|---|---|---|
| `RefNewField` | `opcode(1) fieldIdx(2)` · 3 B | `..., obj -> ..., refSlot` | `fieldIdx` resuelve slot al enlazar (igual que `FieldGet`); aloca cell con `Owner=receiver`, `Slot`; safepoint |
| `RefNewFieldX` | `opcode(1) fieldIdx(4)` · 5 B | ídem | forma ancha, patrón `CastX` |
| `RefNewStatic` | `opcode(1) fieldIdx(2)` · 3 B | `... -> ..., refSlot` | cell con `StaticAddress = field.StaticAddress` (sin asignación de dueño) |
| `RefGet` | `opcode(1)` · 1 B | `..., refSlot -> ..., value` | static: `*StaticAddress`; instancia: `((SurtrInstance)entities[Owner]).Fields[Slot]` |
| `RefSet` | `opcode(1)` · 1 B | `..., refSlot, value -> ...` | escritura espejo |
| *(fase arrays)* `RefNewElement` | `opcode(1)` · 1 B | `..., arr, idx -> ..., refSlot` | aplazado por invalidación (§12) |

Notas de implementación:

- `RefGet`/`RefSet` reusan literalmente el cuerpo de `FieldGet`/`FieldSet`
  (`SurtrVirtualMachine.cs:2573-2576`, `:2603-2606`) tras una carga extra del cell; sin ramas por
  tipo en el caso instancia (el cell ya distingue static por `Owner==0`, predicción trivial).
- Creación asigna: ruta `Register(...)` + `goto Safepoint`, clavada al patrón de `BoxInt`
  (`SurtrVirtualMachine.cs:1797-1835`, safepoint en `:4149-4155`).
- GC: `VisitReferences` marca `Owner` (plantilla: `SurtrClosure.cs:163-170`); cero cambios en el
  collector. La referencia mantiene vivo al dueño mientras viva — semántica correcta y deseada.
- Generadores: nada que hacer. Un cell nunca apunta a pila, así que sobrevive a `Yield`/
  `GenResume` sin saber que existen.
- Excepciones/unwinding: los cells son entidades normales; nada especial.

### 7.5 Compilador

- **Binder**: nuevo nodo bound (`BoundRefExpression`) que exige lvalue de las formas admitidas;
  rechazo con diagnóstico accionable para locals («no se puede tomar una referencia a un local;
  usa un objeto contenedor o replantea») y arrays (fase futura). Flow analysis: el binding `ref`
  es definitivamente asignado en su declaración (como `let`).
- **Emisor** (`MethodBodyEmitter`): baja `ref expr` a `RefNew*`; baja lecturas/escrituras a través
  de un local tipado `ref<T>` a `Ldl(local)+RefGet` / `Ldl(local)+value+RefSet` — o, optimización
  barata, un par fusionado `RefGetLocal`/`RefSetLocal localIdx(1)` si el perfil lo pide (empezar
  sin fusión).
- **Tipos** (`TypeSymbolFactory`/`Conversions`): tipo compuesto `ref<T>` con identidad
  estructural; sin conversiones implícitas ni con `T` ni entre `ref<A>`/`ref<B>`.
- **Metadata/imagen**: los cells no aparecen en metadata (son valores runtime); solo hay que
  estabilizar los números de opcode → bump de `FormatVersion` (`SurtrModuleImage.cs:150`).
- **LSP**: hover muestra `ref<int>`; completar `ref ` tras `=`; nada estructural nuevo.

### 7.6 Coste en hot path (honesto)

- Crear: ~lo que `BoxInt` (aloc + registro + safepoint armado). Está bien: crear refs será raro
  respecto a usarlas.
- Acceder: `RefGet` ≈ `FieldGet` + 1 lookup de cell + 1 rama static/instancia ≈ **1,2–1,5×
  FieldGet**. `FieldGet` ya paga `fieldTable` + lookup `entities[id]` + cast
  (`SurtrVirtualMachine.cs:2550-2576`), así que el delta es pequeño. Recomendación de uso en
  docs: refs para *nombrar destinos*, accesos directos dentro de bucles cerrados cuando el
  receptor es estable.
- Memoria: 1 entidad por ref viva (≈ 48-64 B + id). Presión despreciable salvo abuso en bucles.

### 7.7 Pros / contras

Pros: resuelve los cinco casos de uso principales (§5.1–5.5); cero cambios de ABI de llamadas;
cero cambios de GC; inmune a las tres invalidaciones del §3; superficie nueva mínima (4–5 opcodes,
1 entidad, 1 tipo compuesto); extiende el modelo mental existente (handles) en vez de estrenar
otro.

Contras: no referencia locals (limitación visible; mitigada con diagnóstico claro + contenedores);
asigna al crear; igualdad de refs por identidad de cell puede sorprender (mitigar documentando o
internando, §12); una familia de tipos nueva atraviesa binder/emitter/LSP/reflexión
(`GetTypeOfValue` sobre un cell dirá la clase built-in `RefSlot`; decidir presentación, §12).

---

## 8. Propuesta 2 — parámetros `out` / `inout` / `ref` estilo C#

Se construye **encima** de la Propuesta 1; sin ella es inviable sin expansiones intrusivas.

### 8.1 Diseño

Azúcar de declaración en firmas Surtr:

```surtr
fun trySplit(s: string, out left: string, out right: string): bool { ... }
fun normalize(inout amount: float): void { amount = clamp(amount, 0.0, 1.0); }
```

Mapeo a la primitiva: `out T` y `inout T` (o `ref T`, elegir uno; se propone `inout` por claridad
frente al `ref` de creación — decisión abierta §12) son parámetros de tipo `ref<T>` con reglas
extra aplicadas por el compilador:

- `out`: flow analysis exige asignación-definitiva antes de cada retorno (incluida la ruta de
  excepción: un `throw` la libera, igual que el análisis de `finally` existente en `FlowAnalysis`).
  El llamador no necesita inicializar el argumento.
- `inout`: exige argumento inicializado; dentro del cuerpo es lectura/escritura normal.
- `ref`-creación (P1) y estos modificadores conviven: `ref` construye, `inout` documenta intención
  de parámetro. Si se prefiere mínimo vocabulario, fase 1 puede vivir solo con `ref<T>`
  explícito y dejar `out/inout` para después.

Sitios de llamada:

```surtr
if trySplit(line, out var a, out var b) { use(a, b); }   // declaración inline del out
normalize(inout speed);                                   // argumento lvalue existente
swap(ref i, ref j);                                       // forma explícita equivalente
```

### 8.2 Por qué el ABI no cambia

Un parámetro `ref<T>` es un slot bajo `TagReference` como cualquier objeto: `argsCount` lo cuenta
normal (`docs/Opcodes.md:104-121`), el callee lo lee como local `Ldl`, y escribe con
`Stl+RefSet`/fusionado. Ni `CallLocalModule` ni `InvokeVirtual` ni `InvokeClosure` cambian un
byte. Los resultados siguen siendo 0 o 1 (`retCount`), así que el valor de retorno de
`trySplit` arriba es el `bool` normal — los `out` viajan como argumentos, no como retorno.

### 8.3 Interop C#

El source generator (`src/Surtr.Interop.SourceGenerator`) genera el wrapper de un método C#
`bool TryParse(string s, out int value)` como nativo Surtr
`tryParse(s: string, out value: int): bool`:

1. Lee el cell del argumento `out` (helper host nuevo en `Surtr.Interop`:
   `RefRead(args[i])`/`RefWrite(args[i], v)` que despachan a slot o native-field).
2. Declara el `out int` local C#, invoca, escribe de vuelta con `RefWrite`.

Los métodos con `ref` C# dejan de estar prohibidos (`SurtrReflectionScanner.cs:102,281,327` pasa a
aceptarlos mapeándolos a `inout`). El fallback por reflexión (`SurtrReflectionInvoker.cs:285`)
obtiene el mismo tratamiento. Los structs nativos inline siguen marshalándose por valor (rebuild),
que es independiente de esto.

### 8.4 Variante sin P1 (por completitud, no recomendada): expansión en sitio de llamada

Transformar `out` en azúcar puro de compilador: `trySplit(s, out a, out b)` se reescrita a
`(let t = __split(s); a = t.0; b = t.1; t.ok)` con la función real devolviendo tupla. Cero
cambios de VM, pero: multiplica firmas sintéticas y trabajo de overload resolution; exige
evaluar argumentos con efectos a temporales (orden y unicidad de efectos); no sirve para `inout`
(no hay «leer el estado previo del llamador» sin pasar algo); y no produce referencias
almacenables. Es la salida si P1 se rechaza, a costa de complejidad en el compilador mayor que la
de P1 en la VM.

### 8.5 Pros / contras

Pros: semántica C# real dentro de lo posible; ABI intacto; interop se abre de golpe; casos
§5.1–5.3 resueltos con sintaxis natural. Contras: flow analysis nuevo (definite assignment para
`out`); vocabulario de modificadores a fijar; presión sobre mensajes de error de llamadas (un
argumento no-lvalue pasado a `inout` debe explicarse bien).

---

## 9. Propuesta 3 — células estilo Lua para capturas mutables

**Recomendación: no ahora.** Se documenta como diseño completo para tener la alternativa medida.

### 9.1 Diseño técnico

- El binder, al detectar captura de un `var` local (hoy error en `BodyBinder.cs:287-294`),
  marca ese local como *cellular*. Cada **activación** del frame que declara un local celular
  registra la celda abierta en una lista del frame (o de la máquina, con backpointer al depth).
  `NewClosure` captura la **celda** (todas las lambdas del mismo frame comparten la misma celda
  por variable: ahí está la semántica compartida).
- Lecturas/escrituras del local en el frame dueño siguen siendo `Ldl`/`Stl` directos mientras la
  celda está abierta (la celda apunta al slot de pila; dirección estable, §2.2).
- Al morir el frame (retorno normal, unwinding por handler — ambos puntos ya caminan frames),
  se cierran sus celdas: copiar el valor del slot a la celda y marcarla cerrada. `UpValueGet`
  pasa a leer: celda abierta → slot; cerrada → campo de la celda. Nuevo `UpValueSet` para
  escritura a través de celda cerrada (y a través de abierta: escribe el slot).
- Generadores (la arruga específica de Surtr): `Yield` cierra preventivamente las celdas abiertas
  del frame (copiar a celda) y `GenResume` las reabre recalculando la dirección del slot en la
  base nueva; o bien se prohíbe capturar `var` en funciones generadoras en v1. La primera opción
  añade coste a `Yield`/`GenResume` proporcionales a las celdas vivas; la segunda es una regla
  más que explicar.
- Coste: 1 asignación por variable capturada-mutable por activación (¡por llamada!, no por
  lambda); indirección en cada `UpValueGet/Set`; bookkeeping de cierre en dos rutas de salida de
  frame + generadores.

### 9.2 Evaluación

Compra: capturas mutables de **locales**. Eso es todo; para campos/statics P1 ya basta. El
equivalente manual hoy es corto (`let c = Counter();` con clase contenedora) y el escape con P1
(`let r = ref modulo.contador;` capturando `r`) cubre el 90 % restante con la infraestructura de
P1. El costo recurrente en `Yield`/`GenResume` — instrucciones calientes del modelo de
corrutinas — y la tercera política de captura que documentar hacen que la relación valor/costo no
compense salvo demanda real. Revisar si aparece un patrón dominante de contadores-compartidos en
juegos script.

---

## 10. Costes y riesgos transversales

1. **Formato**: bump de `FormatVersion` (`SurtrModuleImage.cs:150`) por los números de opcode
   nuevos; recompilación de imágenes, sin upgrade path — política ya asumida por el proyecto
   (`docs/Opcodes.md:82-95`). Tests de valores dorados en
   `src/Surtr.Tests/Bytecode/OpCodeValueTests.cs`.
2. **Igualdad/identidad**: `REQ` sobre cells compara identidad de cell. Riesgo de confusión
   «dos refs al mismo slot no son ==». Mitigaciones posibles (mutuamente excluyentes): (a)
   documentar + ofrecer `RefTargetsEqual` futuro; (b) internar cells por `(dueño, slot)` en una
   caché del runtime con limpieza en safepoint (costo: tabla + política de expiración). Decisión
   §12-D1.
3. **Reflexión**: `Type.members()`/`typeof` expondrán la clase built-in del cell; presentarla como
   `ref<T>` legible exige mapear descriptor↔tipo-compuesto en `SurtrReflectionBuiltIns` (mismo
   esfuerzo que cualquier tipo genérico built-in, cf. `generator<T>`).
4. **LSP/analyzer**: nueva familia de tipos en hover/completion/go-to-def; diagnósticos nuevos
   (local/array/no-un-slot). Riesgo bajo, trabajo real.
5. **Seguridad del storage de statics en cells**: `StaticAddress` es un puntero crudo dentro del
   cell — seguro porque el storage muere solo con el runtime (`SurtrTypeLinker.cs:594-596`), pero
   hay que garantizar que un módulo descargado (si algún día existe descarga) invalide o impida
   cells supervivientes. Hoy no hay descarga de módulos: riesgo latente, documentarlo.
6. **Disciplina de tags**: si algún día se quisiera representar la referencia *sin* entidad
   (tag propio con payload dividido), el espacio existe (`0xFFF7`+, `SurtrValue.cs:157-63`) pero
   32 bits no dan para dueño+slot+tag con comodidad; la entidad registrada evita el problema y
   sigue el precedente i64 de no forzar el payload (`docs/Informe-i64-y-f32.md:42,98-114`).
7. **No-regresión del hot path**: `Ldl/Stl/FieldGet/FieldSet` no cambian. Los opcodes nuevos viven
   en la cola del switch; el único punto compartido es el safepoint ya existente.

---

## 11. Roadmap recomendado por fases

**Fase 0 — Decisión y spec (sin código)**
- Fijar: nombre del modificador de creación (`ref`), sintaxis de acceso (auto-deref vs `.value`),
  vocabulario de parámetros (`inout` vs `ref` param), política de igualdad (D1).
- Escribir la sección de `docs/Language-Syntax.md` (nuevo §) y el plan de opcodes en
  `docs/Opcodes.md`; reservar valores `0xF0–0xF5`.

**Fase 1 — Primitiva P1 (MVP)** — objetivo: `ref` a campos de instancia y statics, punta a punta.
1. `Surtr.RefSlot` entidad + `VisitReferences` + built-in class para reflexión (Core).
2. Opcodes `RefNewField/X`, `RefNewStatic`, `RefGet`, `RefSet` + disassembler + golden values
   (Core/Bytecode) + bump `FormatVersion`.
3. Binder/emitter: tipo `ref<T>`, `BoundRefExpression`, restricciones y diagnósticos; lowering de
   lectura/escritura a través de bindings (Compiler).
4. Tests: unidad de opcodes (VM), extremo-a-extremo de compilación, casos §5 (swap manual,
   navegación profunda, captura de ref por lambda escribiendo a través).
5. Docs: Opcodes.md, Language-Syntax.md, Runtime-Model.md (modelo de referencia).

**Fase 2 — Parámetros P2**
1. Azúcar `out`/`inout` en firmas + definite-assignment para `out` en `FlowAnalysis`.
2. Sitios de llamada: formas `out var x`, `inout expr`, `ref expr`; errores de no-lvalue.
3. Interop: helpers host `RefRead/RefWrite`; source generator y reflection scanner aceptan
   `ref`/`out` C# (derogar `SurtrReflectionScanner.cs:102,281,327` y
   `SurtrReflectionInvoker.cs:285`); actualizar `Guia-Interop` y README del Interop.
4. Stdlib: revisar APIs propias que hoy devuelven tuplas-solo-por-out y ofrecer duales con `out`.

**Fase 3 — Extensiones condicionadas a demanda**
- Elementos de array (`RefNewElement`) con política de invalidación elegida (§12-D4).
- Bloques multi-slot (`ref<Vec2>` sobre value classes) usando `Width` en el cell.
- Fusión `RefGetLocal/RefSetLocal` si el perfilado lo justifica.
- Interning de cells o `RefTargetsEqual` según D1.
- Células Lua (§9) solo con demanda demostrada de captura mutable de locales.

---

## 12. Decisiones abiertas

- **D1 — Igualdad de referencias**: identidad de cell (simple) vs internado por `(dueño, slot)`
  (REQ significativo, costo de caché) vs opcode comparador. Recomiendo: identidad + documentar;
  reabrir si el código real sufre.
- **D2 — Sintaxis de acceso**: auto-deref estilo C# (proponido) vs explícito `.value`/`get()/set()`.
  El auto-deref exige que el binder clasifique lvalues a través de bindings `ref`; el explícito es
  mecánico pero ruidoso. Decidir en Fase 0 con un spike de binder.
- **D3 — Vocabulario de parámetros**: `inout` (propuesto, evita colisión con `ref` creador) vs
  `ref` param unificado estilo C#. Impacta solo binder/diagnósticos.
- **D4 — Arrays**: ¿`ref xs[i]` observa posición (post-shift apunta a otro elemento, documentado)
  o se invalida/trapa (necesita generación o marca por array)? Recomiendo aplazar y entonces
  elegir «observa posición» + `ArrRemoveAt` documentado como rompe-refs, que es lo barato y
  predecible.
- **D5 — `ref` a campos `let`**: permitir (lectura-only naturalmente, `RefSet` rechazado por el
  compilador según el field sea `let` — el cell podría llevar flag writable). Recomiendo permitir
  con flag, para navegar estructuras inmutables sin copiar.
- **D6 — Presentación en reflexión/depuración**: clase built-in visible (`RefSlot`) vs azúcar
  `ref<T>` en descriptores. Segunda opción más limpia, más trabajo en metadata.
