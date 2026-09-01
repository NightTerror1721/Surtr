# Plan: hoja de ruta de novedades para Surtr como sustituto de Lua

> Consolida las doce propuestas discutidas en sesión (investigación del código y del propósito de
> Surtr, luego una lista de novedades pensadas para un lenguaje de scripting embebido que compita
> con Lua) en un plan con orden de implementación recomendado. No modifica código; es el documento
> de referencia para planificar ese trabajo, al estilo de `docs/Package-Plan.md` y
> `docs/Plan-Revision-Stdlib.md`.

---

## 0. Correcciones sobre lo ya construido

Antes de ordenar nada: dos de las doce propuestas resultaron estar más avanzadas de lo que parecían
en la conversación original, y el plan de abajo ya está ajustado a eso. Merece constar explícito
para que nadie vuelva a proponer desde cero lo que ya existe:

- **El contenedor ejecutable `.surtrx` (propuesta 10) ya está implementado**, no solo planeado:
  `src/Surtr.Core/Bytecode/Image/SurtrPackage.cs` + `SurtrPackageReader.cs`/`SurtrPackageWriter.cs`,
  `surtrc build --package` (`src/Surtr.Cli/Program.cs`) y `surtr <archivo>.surtrx` /
  `surtr run <archivo>.surtrx` (`src/Surtr.Run/Program.cs`) funcionan hoy. `docs/Package-Plan.md`
  describe el diseño que efectivamente se construyó. Lo que falta **no es el formato**, es la capa
  de ecosistema por encima (§10 más abajo).
- **El scheduler de corrutinas (propuesta 1) ya existe en la stdlib**:
  `src/Surtr.Stdlib/src/surtr/async/Scheduler.surtr` tiene `Scheduler.start`/`update`/`stopAll` y
  las recetas `delay`/`repeatEvery`/`repeatTimes` sobre `generator<float>`, con el protocolo
  "`yield segundos`" ya funcionando. `docs/Plan-Revision-Stdlib.md` lo registra como su propio P1,
  ya implementado. Lo que falta es la orquestación más fina que `docs/Informe-Corutinas-Asincronas.md`
  identifica (§1 más abajo), no el mecanismo base.

También existe, en `docs/Plan-Revision-Stdlib.md`, una propuesta **P3 — JSON** ya evaluada por ese
documento (coste medio-alto, riesgo medio, sin implementar por falta de tiempo) que es exactamente
la base de la propuesta 6 de este plan: se referencia en vez de reabrirla desde cero. Igualmente,
`Signal<T>`/`EventEmitter<T>` (P2 de ese mismo documento) es un complemento natural de varias
propuestas de aquí (workers, red) pero **queda fuera de este plan** — ya está scoped allí y no es
una de las doce propuestas originales.

---

## 1. Las doce propuestas

Cada una lleva: qué es, estado actual, qué falta, cómo encaja en lo ya construido, de qué depende y
una estimación cualitativa de coste/riesgo — **estimaciones de ingeniería, no medidas**; antes de
comprometerse a una cifra real hace falta la misma disciplina de medición que `docs/Informe-
Volatilidad-Run.md` exige para cualquier cambio en `Run()`.

### 1. Vocabulario de espera y cancelación para el scheduler de corrutinas
**Qué es:** ampliar `surtr.async.Scheduler` con lo que `Informe-Corutinas-Asincronas.md` identifica
como la pieza que falta: `WaitUntil(cond: () -> bool)`, `WaitForCoroutine(other)` (esperar a que
otra corrutina termine, no solo un tiempo), cancelación individual (`Scheduler.stop(handle)`, no
solo `stopAll()`), y una política explícita de qué pasa cuando una corrutina lanza una excepción
entre ticks (¿se propaga al llamador de `update()`? ¿se aísla y solo esa corrutina muere?).
**Estado actual:** el mecanismo base (frame-copy en `yield`, reanudación externa vía
`ResumeGenerator`/`SendToGenerator`/`RaiseInGenerator`/`DisposeGenerator`, cierre determinista) está
completo en el runtime; el `Scheduler` de la stdlib ya cubre el caso de tiempo puro.
**Qué falta:** el `Scheduler` actual guarda `generator<float>[]` planos — para dar handles
cancelables por corrutina individual hace falta un id estable por entrada (hoy usa swap-removal, que
invalida índices). `WaitForCoroutine` necesita que el valor que fluye por `yield` distinga "espera
n segundos" de "espera a que termine este otro generador", lo que probablemente signifique cambiar
el protocolo de `generator<float>` a `generator<WaitInstruction>` con un tipo (`value class` o unión
cerrada) en vez de un `float` desnudo — un cambio de firma que toca todo el código que ya usa
`delay`/`repeatEvery`/`repeatTimes`.
**Depende de:** nada. **Bloquea:** parte de la propuesta 9 (I/O de red asíncrono querrá reusar el
mismo vocabulario de espera).
**Coste/riesgo:** bajo-medio; riesgo bajo (Surtr puro sobre generadores ya estables). El único riesgo
real es de diseño: elegir mal la forma de `WaitInstruction` obliga a romper la firma pública otra vez.

### 2. Hot-reload de módulos preservando instancias vivas
**Qué es:** un `SurtrRuntime.ReplaceModule` que recompile un módulo y, cuando el layout de instancias
no cambió (mismos campos, mismo orden), reemplace solo las entradas de las vtables ya existentes —
las instancias vivas siguen vivas, con comportamiento nuevo. Cuando el layout sí cambió, falla con
diagnóstico claro en vez de corromper el heap.
**Estado actual:** nada — `LoadModule` hoy rechaza directamente una segunda carga en el mismo path
(`SurtrRuntime.cs:945`, `"A module is already loaded at path..."`). El modelo entero asume que un
módulo cargado es inmutable: vtables flattened en el link, inicializadores estáticos corridos una
vez, slots de campo fijados por `SurtrTypeLinker`.
**Qué falta:** todo. Esto es, con diferencia, el trabajo más invasivo de las doce propuestas porque
toca invariantes que el resto del sistema da por sentadas (`SurtrBuildState.Built` es terminal hoy;
un método `SurtrBytecodeMethodInfo` cachea el offset de su cuerpo en el constructor). Necesita, como
mínimo: (a) una noción de "reemplazo compatible" a nivel de `SurtrTypeLinker` — comparar el layout
nuevo contra el viejo campo a campo; (b) parchear entradas de vtable in-place sin re-flattenear toda
la jerarquía de subclases que heredan de este tipo; (c) decidir qué pasa con closures y generadores
ya suspendidos que capturaron el método viejo.
**Depende de:** ninguna de las otras técnicamente, pero es mucho más seguro de construir y depurar
si la propuesta 4 (DAP) ya existe — se necesita poder *ver* qué se reemplazó y en qué instancias.
**Coste/riesgo:** alto; riesgo alto (toca el linker y las invariantes de build state que todo el
resto del compilador asume terminales). Candidato a su propio informe de investigación antes de
tocar código, siguiendo el patrón que ya usó `Informe-Corutinas-Asincronas.md` para la propuesta 1.

### 3. Snapshot/serialización determinista del heap (guardado de partidas)
**Qué es:** `SurtrRuntime.Snapshot()`/`Restore()` que recorre el grafo alcanzable desde las raíces
—igual que hace el colector— y lo vuelca a un formato binario versionado.
**Estado actual:** el registro de entidades ya indexa cada objeto vivo con un id y cada clase ya
declara `ReferenceSlots` (qué slots de instancia son referencias) precisamente para que el trazador
no tenga que tag-testear cada campo — es la misma información que un serializador de heap necesita
para saber qué seguir y qué copiar como valor plano.
**Qué falta:** todo el volcado/restauración en sí, más decidir qué identifica a un tipo de forma
estable entre una partida guardada y una versión posterior del juego (un descriptor cambia si el
layout de una clase cambia — hot-reload, propuesta 2, agrava exactamente este problema). Un
`@Serializable` que delimite qué es elegible reduce el problema a un subgrafo controlado en vez de
"todo el heap".
**Depende de:** aprovecha mejor la infraestructura de recorrido/reflexión que deje construida la
propuesta 6 (JSON) — no es un bloqueo duro, pero conviene no duplicar el trabajo de "describir qué
campos de un tipo hay que visitar".
**Coste/riesgo:** medio-alto; riesgo medio (reutiliza mecanismo del GC, pero versionar formato de
guardado a través de cambios de esquema es un problema abierto de diseño, no solo de implementación).

### 4. Debug Adapter Protocol (DAP) sobre el Language Server existente
**Qué es:** breakpoints, step over/into/out, inspección de variables en vivo, integrado en VSCode
igual que el LSP ya lo está.
**Estado actual:** `src/Surtr.LanguageServer` ya tiene `LspServer`, `CompletionProvider`,
`HoverFormatter`, `SemanticTokensProvider`, `InlayHintProvider`, `CodeActionProvider` — el transporte
JSON-RPC y la resolución de símbolos (`SymbolResolver`) ya existen. La VM expone `SurtrCallFrame`
por cada frame activo (chunk, método, closure) y el modelo de excepciones como tablas de handlers
hace que interceptar en un punto concreto del bytecode sea barato de insertar sin tocar el propio
bytecode compilado.
**Qué falta:** el servidor DAP en sí (un segundo protocolo JSON-RPC, distinto del LSP, aunque puede
vivir en el mismo proyecto), y la instrumentación de la VM: un modo de ejecución "step" en
`Execute()`, resolución de breakpoints a offsets de bytecode vía el mapa fuente↔offset que el
compilador ya genera para diagnósticos con span, e inspección de locales por frame vía los mismos
slots que `SurtrCallFrame` ya guarda.
**Depende de:** nada estructuralmente — usa lo que el LSP ya construyó como transporte, y la VM tal
como está. **Ordenar antes de la propuesta 2 (hot-reload)** porque depurar el propio hot-reload
mientras se construye es mucho más fácil con esto ya en pie.
**Coste/riesgo:** medio; riesgo medio (protocolo bien documentado, VM ya expone casi todo lo
necesario — el riesgo está en no introducir coste en el hot path al soportar breakpoints, que tiene
que ser opt-in y a coste cero cuando no hay depurador conectado).

### 5. Pattern matching y destructuring extendido
**Qué es:** patrones de tipo en `switch` (`case x: Dog =>`), destructuring de tuplas/value
classes/arrays dentro de un `case`, y guards (`case x: Dog if x.age > 2 =>`).
**Estado actual:** ya está en la lista propia del lenguaje como diferido a propósito —
`Language-Syntax.md` §14.4 lo nombra explícitamente ("Pattern matching — type patterns in switch and
destructuring... additive"). El estrechamiento que permite `if (x is Dog)` ya existe (§5.1); lo que
falta es la superficie de patrones más amplia.
**Qué falta:** gramática nueva en el parser para patrones dentro de `case`, y en el binder decidir
cómo interactúa con la exhaustividad de `switch` que hoy solo cubre enums (`Compiler-Plan.md`).
**Depende de:** nada — es trabajo de frontend puro (parser + binder + codegen del `switch`), sin
tocar el runtime ni el formato de bytecode.
**Coste/riesgo:** medio; riesgo bajo (superficie ya diseñada de antemano en el documento de sintaxis,
"cada una es aditiva" — no invalida nada existente).

### 6. Serialización de datos declarativa (JSON)
**Qué es:** un encoder/decoder JSON en la stdlib, y — como extensión posterior — un atributo
`@Serializable` que genere (de)serialización tipada sin reflection en runtime.
**Estado actual:** ya evaluado como P3 en `docs/Plan-Revision-Stdlib.md` (coste medio-alto, riesgo
medio) y explícitamente no implementado por falta de tiempo, no por bloqueo técnico. Ese documento ya
resolvió el diseño base: un árbol sobre `Map<string, unknown>` + `List<unknown>` + primitivos, con
la advertencia de que `unknown` (erasure) hace ese árbol más incómodo de consumir que un `any` real,
así que la forma exacta hay que fijarla antes de escribir el parser.
**Qué falta:** implementar el parser recursivo-descendente y el encoder que P3 ya diseñó (fase 1);
después, como fase separada y nueva respecto a lo ya scoped, la capa `@Serializable` que aprovecha
que los atributos ya son clases reales con reflection (`Type`/`Member`/`Module`, §13.5 de
`Language-Syntax.md`) para generar `toJson()`/`fromJson()` en tiempo de compilación.
**Depende de:** nada para la fase 1 (P3 tal cual). La fase `@Serializable` es la que más vale la pena
construir antes de la propuesta 3 (snapshot), porque ambas necesitan "recorrer un objeto y decidir
qué campos visitar" y conviene que compartan un solo mecanismo en vez de dos.
**Coste/riesgo:** medio-alto (heredado directamente de la propia estimación de P3); riesgo medio.

### 7. Workers aislados para paralelismo real
**Qué es:** una API de host (`SurtrWorker`) que levanta un `SurtrRuntime` en su propio hilo del SO,
comunicándose por mensajes serializados — nunca memoria compartida ni punteros entre heaps.
**Estado actual:** la base conceptual ya existe: varios `SurtrRuntime` con heaps completamente
separados ya pueden coexistir en un proceso (`Guia-Embedding-Surtr.md` §1), y la ejecución es
single-threaded *por runtime*, exactamente el aislamiento que un modelo de actores necesita. No hay
nada de infraestructura de hilos, colas de mensajes o serialización entre heaps todavía.
**Qué falta:** el propio `SurtrWorker` (hilo del SO + bucle de mensajes), y — esto es lo que lo
vuelve no trivial — una forma de serializar valores para cruzar el canal sin compartir memoria. Lo
más barato es reusar el módulo JSON de la propuesta 6 (o un formato binario más compacto sobre el
mismo mecanismo de recorrido) en vez de inventar un tercer formato de intercambio.
**Depende de:** la propuesta 6 (serialización de mensajes) como pieza compartida; se beneficia de la
propuesta 10 (empaquetado) para poder entregarle a un worker todo su código en un solo `.surtrx` en
vez de una lista de módulos sueltos.
**Coste/riesgo:** medio-alto; riesgo medio (el aislamiento de heaps ya está garantizado por diseño,
así que el riesgo está en el ciclo de vida de los hilos del host y en no reintroducir estado
compartido por la puerta de atrás).

### 8. Regex nativo en la stdlib
**Qué es:** una implementación de expresiones regulares real (no los "patterns" limitados de Lua),
expuesta como `Regex`/`Match` en `surtr.text`.
**Estado actual:** nada en la stdlib actual cubre esto; no aparece en `Plan-Revision-Stdlib.md`
tampoco, así que es un hueco genuinamente nuevo.
**Qué falta:** todo. Dos caminos: (a) un motor propio compilado a un autómata, en Surtr puro o
parcialmente nativo si el rendimiento lo exige; (b) un wrapper nativo fino sobre algo ya probado —
más barato pero ata el runtime a una dependencia externa, lo que choca con el objetivo `netstandard2.1`
sin dependencias de `Surtr.Core`. Probablemente el motor va en la stdlib (como `Math`, con las partes
que necesitan primitivas de bajo nivel en `Native/`), nunca en `Surtr.Core`.
**Depende de:** nada. **Coste/riesgo:** medio; riesgo bajo (problema bien entendido, aislado del
resto del lenguaje).

### 9. Fecha/hora y networking mínimo en la stdlib
**Qué es:** `DateTime`/`Duration` en `surtr.time`, y un cliente HTTP/WebSocket mínimo en
`surtr.net`, este último integrado con el vocabulario de espera de la propuesta 1 (una petición de
red se espera con `yield` como cualquier otra corrutina, no bloqueando el frame).
**Estado actual:** ninguno de los dos existe en la stdlib ni aparece en `Plan-Revision-Stdlib.md`.
**Qué falta:** `DateTime`/`Duration` es trabajo aislado y barato (mayormente Surtr puro sobre un
puñado de `native fun` de reloj, siguiendo el patrón exacto que `Math`/`RuntimeInfo` ya establecen).
La parte de red es más grande: necesita I/O real desde el host (sockets/HTTP vía la CLR), expuesto
como algo *awaitable* — lo natural es que una petición de red sea un `generator` que hace `yield`
de un `WaitInstruction` de la propuesta 1 hasta que el host resuelve la operación en otro hilo y
empuja el resultado de vuelta.
**Depende de:** la propuesta 1 (vocabulario de espera) para la parte de red; fecha/hora no depende
de nada. **Coste/riesgo:** fecha/hora bajo/bajo; red medio-alto/medio (I/O real desde un motor
embebido en Unity implica decisiones de threading que el host, no Surtr, normalmente ya resuelve —
hay que diseñar la frontera con cuidado).

### 10. Gestor de dependencias sobre `.surtrx`
**Qué es:** manifiesto de proyecto + lockfile + resolución semver para compartir librerías Surtr
entre proyectos — lo que LuaRocks intenta ser, pero verificado en la frontera de tipos.
**Estado actual — corregido respecto a la propuesta original:** el contenedor ejecutable ya existe y
funciona (§0 de este documento); esto **no es construir `.surtrx`**, es construir la capa de encima.
`SurtrProjectFile` ya lee directivas (`root`, `module`, `output`, `define`, `reference`,
`package`, `entry`) línea a línea — el mecanismo de "referenciar algo externo" ya tiene un punto de
entrada (`reference`) aunque hoy probablemente apunte a rutas locales, no a paquetes versionados.
**Qué falta:** el formato del manifiesto de dependencias (nombre + rango semver), el lockfile, un
resolutor de versiones, y decidir el modelo de distribución (¿un registry central al estilo npm, o
resolución puramente de rutas/git como Go modules? — esto último es mucho más barato de construir
primero y no exige operar infraestructura).
**Depende de:** nada técnicamente (el contenedor ya está), pero tiene más sentido después de que el
resto de la stdlib nueva (propuestas 6, 8, 9) exista, para tener algo real que empaquetar como primer
caso de prueba del gestor.
**Coste/riesgo:** medio; riesgo bajo si se evita un registry central en la primera versión (resolver
por URL/ruta primero, registry después si hace falta).

### 11. Sandbox de capacidades declarativo
**Qué es:** una allowlist de qué módulos/`native` puede resolver un script en tiempo de carga —
"este módulo solo puede importar `surtr.math`" — fallando en `LoadModule`, no en tiempo de
ejecución.
**Estado actual:** el sandboxing de recursos (CPU vía `InstructionBudget`, pila vía
`DataStackSlots`/`MaxCallDepth`, heap vía `SurtrGcPolicy.MaxLiveEntities`) ya está completo y a coste
cero en el hot path (`Guia-Embedding-Surtr.md` §1). No hay ningún control de *qué* puede ver un
script, solo de *cuánto* puede consumir.
**Qué falta:** todo, pero es pequeño — el punto de intercepción natural es el mismo bucle de
resolución de imports/`native` que ya existe en la carga de módulos (`LoadModule`,
`SurtrPendingMember`); añadir una lista de paths permitidos que se consulta ahí antes de resolver
cada handle, y fallar la carga entera si algo pedido no está en la lista.
**Depende de:** nada. Se beneficia de estar cerca de la propuesta 10 en el calendario porque ambas
tocan el mismo camino de carga de módulos.
**Coste/riesgo:** bajo; riesgo bajo (aditivo puro, no cambia el comportamiento de nadie que no
active la allowlist).

### 12. REPL interactivo
**Qué es:** `surtr repl` — evaluar expresiones/declaraciones interactivamente contra un
`SurtrRuntime` vivo.
**Estado actual:** `Surtr.Stdlib.Script` ya existe específicamente para compilación dinámica/`eval`,
y el propio *const folding* del compilador (`CodeGen/ConstFolder.cs`) ya demuestra en producción que
correr bytecode real bajo un runtime controlado, con reset entre evaluaciones, funciona y es seguro.
**Qué falta:** el bucle de lectura-eval-impresión en sí sobre `Surtr.Stdlib.Script`, más manejo de
estado persistente entre líneas (variables declaradas en una línea deben seguir vivas en la
siguiente, lo que probablemente signifique tratar cada línea del REPL como una extensión del mismo
módulo/scope en vez de una compilación aislada).
**Depende de:** nada. **Coste/riesgo:** bajo; riesgo bajo — es, con diferencia, la propuesta más
barata de las doce.

---

## 2. Orden recomendado

El criterio de orden combina tres cosas: (a) qué depende técnicamente de qué, (b) apalancar primero
lo que ya está construido al 80-90% (menor riesgo, mayor retorno inmediato), y (c) dejar lo más
invasivo — lo que toca invariantes del linker/build-state que el resto del sistema da por
terminales — para el final, cuando haya más superficie estable (y, en el caso del hot-reload, cuando
ya exista el DAP para depurarlo mientras se construye).

| Fase | Propuestas | Por qué en este orden |
|---|---|---|
| **0 — Quick wins** | 12 (REPL), 8 (regex), parte de 9 (fecha/hora), 11 (sandbox de capacidades) | Sin dependencias entre sí ni con el resto del plan; cada una es barata, aislada, y construye sobre mecanismos que ya llevan tiempo en producción (`Stdlib.Script`, el patrón `Native/`, el camino de carga de módulos). Sirven también para generar tracción/adopción mientras arranca el trabajo más grande. |
| **1 — Cerrar el bucle de juego** | 5 (pattern matching), 1 (vocabulario de espera del scheduler) | Ambas son trabajo aislado (frontend puro la 5; Surtr-sobre-generadores-ya-estables la 1) que no toca invariantes del runtime. La 1 es la que el propio proyecto ya identificó como el paso inmediato en `Informe-Corutinas-Asincronas.md`. |
| **2 — Serialización** | 6 (JSON, fase P3 + `@Serializable`), 3 (snapshot de heap) | La 3 reutiliza el mecanismo de recorrido de objetos que conviene diseñar una sola vez en la 6, así que va después. Juntas cierran guardado de partidas, configuración y mensajes de red — los tres casos de uso que `Plan-Revision-Stdlib.md` ya señala como ausentes. |
| **3 — Red y ecosistema de paquetes** | resto de 9 (red, sobre el vocabulario de la fase 1), 10 (gestor de dependencias, sobre el `.surtrx` ya existente) | La parte de red de la 9 necesita el vocabulario de espera de la fase 1 ya construido. La 10 tiene más sentido con algo real (los módulos nuevos de las fases 0-2) que empaquetar como primer caso de prueba. |
| **4 — Aislamiento** | 7 (workers) | Depende de tener ya un formato de mensaje serializable (fase 2) y, idealmente, de poder entregarle a un worker un `.surtrx` autocontenido (fase 3). Es la propuesta que más se beneficia de llegar tarde. |
| **5 — Tooling de desarrollo** | 4 (DAP), luego 2 (hot-reload) | Las dos más caras y de mayor riesgo del plan, así que van al final, cuando el resto de la superficie del lenguaje ya está más asentada. El DAP va primero porque reutiliza la mitad del trabajo del LSP ya existente (riesgo medio) y porque depurar la implementación del hot-reload — que si algo va mal corrompe vtables en caliente — es mucho más seguro con breakpoints e inspección de estado ya disponibles. |

### Nota sobre la fase 5

Antes de tocar código en la propuesta 2 (hot-reload), conviene un informe de investigación dedicado
— exactamente el patrón que ya dio buen resultado con `Informe-Corutinas-Asincronas.md` para la
propuesta 1 — que audite `SurtrTypeLinker`, `SurtrBuildState` y el cacheo de offsets en
`SurtrBytecodeMethodInfo` antes de comprometerse a un diseño. Es la única propuesta de las doce
donde "investigar primero, proponer después" parece más barato que iterar directamente sobre código.
