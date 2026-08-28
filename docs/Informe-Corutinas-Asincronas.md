# Informe: corutinas asíncronas reales para Surtr

> **Alcance:** investigación del sistema de generadores actual (`generator`/`yield`) y diseño de
> cómo implementar **corutinas asíncronas** al estilo de las corrutinas de Unity
> (`MonoBehaviour.StartCoroutine`): ejecución en el thread principal que progresa a lo largo de
> varios ticks/frames, con suspensiones que pueden esperar tiempo, condiciones, otras corutinas o
> valores del host. No se trata de threads ni de paralelismo.
>
> Este documento solo investiga y propone. No modifica código fuente.

---

## Índice

1. [Resumen ejecutivo](#1-resumen-ejecutivo)
2. [Investigación: el sistema de generadores actual](#2-investigación-el-sistema-de-generadores-actual)
3. [El modelo de referencia: las corrutinas de Unity](#3-el-modelo-de-referencia-las-corrutinas-de-unity)
4. [Análisis de reutilización](#4-análisis-de-reutilización)
5. [Propuesta A — Scheduler en el host sobre los generadores actuales](#5-propuesta-a--scheduler-en-el-host-sobre-los-generadores-actuales)
6. [Propuesta B — Corutinas de primera clase con planificador en Core](#6-propuesta-b--corutinas-de-primera-clase-con-planificador-en-core)
7. [Propuesta C — `async`/`await` estilo C#](#7-propuesta-c--asyncawait-estilo-c)
8. [Recomendación final](#8-recomendación-final)

---

## 1. Resumen ejecutivo

Surtr ya tiene el 90 % de la maquinaria difícil de unas corutinas asíncronas:

- La suspensión **no es una transformación a máquina de estados**: la VM copia el frame vivo
  (`locals + operandos pendientes`) fuera de la pila de datos al heap en cada `yield`
  (*estrategia B* de `docs/Plan-Generadores.md` §4). El estado suspendido es un objeto heap
  (`SurtrGenerator`), trazado por el GC, **totalmente persistible entre llamadas** — exactamente lo
  que necesita una corutina que vive varios frames.
- Existe reanudación **desde fuera de la VM** (`ResumeGenerator`/`SendToGenerator`/
  `RaiseInGenerator`/`DisposeGenerator` en `src/Surtr.Core/Runtime/SurtrRuntime.cs:612-629`), lo
  que significa que un host puede reanimar una corutina en cualquier tick posterior.
- Existe comunicación bidireccional completa (`send`/`raise`/`dispose`/`result`,
  `docs/Language-Syntax.md` §3.7): un `yield` es una expresión cuyo valor es el que el resumidor
  inyecta. Ese es literalmente el canal que usa un planificador para entregar «ya pasó el tiempo /
  se cumplió la condición».
- Existe cierre determinista: `dispose()` lanza `GeneratorExit` dentro del cuerpo suspendido,
  corre sus `finally` y ningún `catch` tipado lo puede tragar
  (`docs/Plan-Disposicion.md` §3.5).

Lo que falta **no es lenguaje sino orquestación**: nadie llama `moveNext` cada tick, no hay tipos
de espera (tiempo, condición, otra corutina), no hay árbol de cancelación ni política de errores
entre ticks, y la API de avance desde C# es `internal`.

La recomendación es **la Propuesta A como fase inmediata** (scheduler propiedad del host sobre los
generadores actuales, con un hueco pequeño que cerrar en Core: exponer públicamente la API de
avance), evolucionando hacia partes acotadas de la B (un vocabulario mínimo de instrucciones de
espera acordado como tipos nativos estándar, para portabilidad entre hosts). La C
(`async`/`await`) se descarta como objetivo inicial.

---

## 2. Investigación: el sistema de generadores actual

### 2.1 Superficie del lenguaje

Documentada en `docs/Language-Syntax.md` §3.7 (líneas 1056-1285). Los rasgos que importan para
corutinas:

```surtr
generator countdown(from: int): int {
    var i = from;
    while (i > 0) { yield i; i = i - 1; }
}
```

- `generator` es introductora de miembro (sustituye a `fun` entera), palabra reservada dura
  (`Lexer.cs:896`; dispatch en `Parser.Declarations.cs:85`). El tipo escrito tras `:` es el
  **elemento**; el tipo de vista de la llamada es `generator<T>` (`Binder.cs:3963-4016`,
  `BindGeneratorShape`: rechaza `inline`/`const`/`native`, exige cuerpo salvo `abstract`, exige
  elemento escrito, rechaza `void`; asigna `MethodSymbol.YieldType` y
  `ReturnType = generator<T>`, `MemberSymbols.cs:141,147`).
- Llamar a la función **no ejecuta nada**: devuelve el objeto generador con los argumentos
  capturados.
- El objeto es de **un solo uso**; iterarlo dos veces lanza `InvalidOperationException`
  (`GenIterate`, `SurtrVirtualMachine.cs:3936-3951`). Llamar otra vez a la función crea uno nuevo.
- `yield expr` es una **expresión** de precedencia mínima (`Parser.Expressions.cs:143-160`,
  `ParseYield`); evalúa a `unknown`: lo que el resumidor inyectó con `send(v)` (o el `return` de
  un `yield from`). Como sentencia no paga lectura (`GenResumed` solo se emite si la fuente lee el
  valor).
- `yield from expr` delega (`Parser.Expressions.cs:154`; lowering dual en
  `MethodBodyEmitter.cs:1504-1583`).
- `return expr;` termina dejando un **resultado** legible por `.result` (`BodyBinder.Statements.cs:692-705`;
  el valor convierte a `unknown`, no hay segundo tipo declarado).
- Superficie de corrutina ya expuesta en la clase built-in `generator<T>`:
  `moveNext()`, `current`, `send(v)`, `raise(e)`, `dispose()`, `result`
  (`SurtrGeneratorBuiltIns.cs:45-93`; ejemplos en `docs/Language-Syntax.md:1220-1263`).
- Restricciones de colocación de `yield`: solo en el cuerpo léxico del generador (nunca en un
  lambda — `BodyBinder.Statements.cs:757-765`; nunca en un `finally` — `:777-783`; sí legal
  dentro de `try/catch`). Un generador sin `yield` advierte (`BodyBinder.cs:165`). FlowAnalysis
  exime al generador del «not all paths return» porque caer al final es terminar
  (`FlowAnalysis.cs:93-105`).

### 2.2 Compilación: stub + cuerpo, cero transformación de control

**No hay máquina de estados en el compilador.** El cuerpo se compila *igual que un método
ordinario*, con `Yield` como única instrucción especial:

- `ModuleEmitter.cs:1693-1715` emite **dos métodos** por declaración `generator` (como C# con sus
  iteradores, pero sin reescritura):
  - **stub**: lleva el nombre público y retorno descriptor `Y<elem>`; su cuerpo completo es
    `GenNew <cuerpo>` sobre los argumentos + `ReturnValue` (`MethodBodyEmitter.cs:5747-5775`,
    `EmitGeneratorFactory`). Por eso el sitio de llamada es una llamada ordinaria, y
    `virtual`/`override`/interfaces salen gratis (`docs/Plan-Generadores.md` §12.4).
  - **cuerpo oculto** `$generator$<nombre>$<n>`: el código real con los `Yield`s; el emisor le
    añade el `ReturnVoid` final (`ModuleEmitter.cs:1708-1712`).
- `EmitYield` (`MethodBodyEmitter.cs:1466-1484`): evalúa el valor, `BoxIfMultiSlot` (un valor más
  ancho que un slot boxea), emite `Yield`; si la fuente leyó el valor, añade `GenResumed`.
- `for-in` sobre algo estáticamente `generator<T>` baja al **camino rápido**
  `GenIterate`/`GenResume`/`GenCurrent` + cierre por `dispose()` en las cuatro salidas
  (`MethodBodyEmitter.cs:951-1001`, `EmitForInGenerator`).

### 2.3 Opcodes existentes

Familia completa en `OpCode.cs:2144-2224`, documentada en `docs/Opcodes.md:653-675`:

| Valor | Opcode | Efecto |
|---|---|---|
| `0xE9` | `GenNew` | construye el generador desde método + args; **no ejecuta nada** |
| `0xEA` | `GenIterate` | comprueba un solo uso (camino rápido de `for-in`) |
| `0xEB` | `GenResume` | reanuda hasta el próximo `yield` o fin; apila `bool` |
| `0xEC` | `GenCurrent` | lee el último valor cedido |
| `0xED` | `Yield` | **suspende**: copia el frame vivo al generador y retorna |
| `0xEE` | `GenDelegate` | `yield from` a otro generador: enlace de delegación |
| `0xEF` | `GenResumed` | empuja el valor con el que se reanudó |

Quedan libres `0xF0`–`0xFF` (`OpCode.cs:48-50`), sin bump de formato por añadir opcodes.

### 2.4 La VM: qué pasa exactamente al suspender

El frame (`SurtrCallFrame.cs:37-94`) es una entrada plana en `_frames[]` con `Base` (puntero a la
zona de slots del frame en la **pila de datos no gestionada**, `SurtrRawValue*`), `CodeBase`, `IP`
(publicado antes de toda transferencia fuera del bucle), y el campo `Generator`
(`SurtrCallFrame.cs:81`). Los locals viven en esa pila de datos unsafe; los operandos encima.

Suspensión (`Yield`, `SurtrVirtualMachine.cs:4083-4136`):

1. Lee el valor cedido del tope (`suspending.Current`).
2. Copia `[current.Base .. sp)` — locals + operandos pendientes — al buffer `Slots` del
   `SurtrGenerator`, blanqueando el hueco sobrante (`:4094-4104`).
3. Graba `ResumeOffset = ip - CodeBase` (`:4105`) y `State = Suspended`.
4. Escribe la respuesta en el slot que el resumedor reservó debajo del frame
   (`frameStart[-1] = true`, `:4111`) y **desmonta el frame** como un retorno ordinario
   (`:4116-4135`). Para quien reanudó, un `yield` es un `Ret` que no produce resultado.

Reanudación (dos caminos que comparten protocolo):

- **Compilado**: `GenResume` (`:3953-3990`) sigue la cadena `Delegate` hasta el generador más
  interno con frame, valida estados, limpia `Resumed`, y entra en el bloque compartido
  `EnterGeneratorFrame` (`:4169-4237`): copia `Slots` de vuelta a la pila en cualquier base libre,
  cero por encima del ancho vivo, monta el frame con `ExpectedResults = 0`, rootea el generador en
  `_roots[depth+1]` para el GC y marca `Running`.
- **Nativo (host)**: `PushGeneratorFrame` (`:842-890`) hace lo mismo desde fuera del bucle, y
  `Advance` (`:792-828`) ejecuta hasta el próximo `yield`/fin con `Execute(depth)`, leyendo la
  respuesta del slot reservado. Es legal porque la VM publica `sp` e `IP` antes de entrar en código
  nativo (`SurtrCallFrame.cs:20-26`): un host puede reentrar incluso desde un shim en plena
  ejecución.

**Conclusión clave: el frame de un generador ya es una continuación persistible, reubicable y
trazada por el GC.** Suspendido, no ocupa pila; vive en `SurtrGenerator.Slots`
(`SurtrGenerator.cs:98`, reservado una vez a `LocalCount + MaxStackSize`, así que ningún `yield`
asigna), con su punto de reentrada (`ResumeOffset`), su canal de entrada (`Resumed`),
su salida (`Current`/`Result`) y sus cuatro estados (`NotStarted`/`Suspended`/`Running`/`Exhausted`,
`SurtrGenerator.cs:19-32`; `Running` evita reanudar un frame vivo sobre sí mismo).

### 2.5 GC, raíces y presupuesto

- El registro de entidades barre soltando referencias (`SurtrEntityRegistry.cs:43`);
  `VisitReferences` de `SurtrGenerator` traza el prefijo vivo de `Slots`, `Current`, `Resumed`,
  `Result` y `Delegate` (`SurtrGenerator.cs:341-366`). Un generador que el host quiere conservar
  entre ticks debe estar **alcanzable o rooteado**: `SurtrRuntime.AddRoot`/`RemoveRoot`
  (`SurtrRuntime.cs:1903-1915`) existe justo para eso («lo que un host usa para sostener un objeto
  entre llamadas», `:1898-1900`).
- Hay dos safepoints de recolección automática: tras opcodes que asignan
  (`SurtrVirtualMachine.cs:4149-4155`) y en la frontera nativa (`:4269-4273`).
- Existe **presupuesto de instrucciones** por ejecución (`StepBudget`, `SurtrVirtualMachine.cs:122-123`,
  `429-439`; configurable desde el host vía `SurtrRuntime.InstructionBudget`,
  `SurtrRuntime.cs:157-167`), con excepción dedicada que nunca cruza handlers
  (`:911-919`, `4438-4439`). Es la herramienta natural para acotar el coste de un paso de corutina.

### 2.6 Excepciones, cierre y cancelación primitiva

- `raise(e)` reconstruye el frame suspendido y ofrece la excepción al buscador de handlers contra
  el `IP` guardado — un `try` alrededor del punto de suspensión ve el error
  (`RaiseInGenerator`, `SurtrVirtualMachine.cs:584-627`).
- `dispose()` lanza `GeneratorExit` (clase invisible a todo `catch` tipado,
  `SurtrBuiltIns.cs:330` y condición en `Catches`, `SurtrVirtualMachine.cs:1048-1065`) dentro del
  cuerpo: corren los `finally` pendientes y el generador queda agotado. Idempotente; cierra cadenas
  de delegación nivel a nivel (`DisposeGenerator`/`CloseOne`, `:654-759`; el camino rápido
  `HasHandlerAt` evita montar frame cuando no hay nada que correr, `:769-782`). Un `break` en un
  `for-in` cierra el cursor igualmente (`Plan-Disposicion.md` §3.4-3.5).
- Una excepción que escapa del cuerpo agota el generador y propaga en el momento del avance que la
  alcanza (`Finish`, `SurtrGenerator.cs:285-311`).
- Hueco reconocido: un generador abandonado a medias no corre su `finally` por recolección — el
  registro barre sin hook de finalización (`Plan-Generadores.md` §15.5, última línea;
  `Plan-Disposicion.md` §5.3).

### 2.7 Qué NO existe hoy (los huecos reales)

1. **Nadie hace ticking.** Toda reanudación hoy ocurre dentro de la misma ejecución que consume
   el generador (`for-in`, bucles manuales `moveNext`/`send`). No hay cola de corutinas vivas ni
   reloj.
2. **API de avance no pública.** Los puntos de entrada nativos (`ResumeGenerator`,
   `SendToGenerator`, `RaiseInGenerator`, `DisposeGenerator`) son `internal`
   (`SurtrRuntime.cs:612-629`). Desde C#, hoy se puede *crear* un generador (`TryInvoke` del stub,
   `:1731-1750`) pero no avanzarlo.
3. **No hay tipos de espera.** Nada dice «espera 2 segundos», «espera esta condición», «espera a
   esa otra corutina». El canal de valores (`send`/`GenResumed`) está listo, pero no hay vocabulario.
4. **Sin jerarquía ni cancelación compuesta.** `dispose()` cancela un generador; no hay padre/hijo
   entre generadores (salvo la cadena de delegación, que es composición secuencial, no spawning).
5. **Sin política de errores entre ticks**: quién captura, registra o propaga el fallo de una
   corutina que explotó en el tick 47.
6. Sin completación por callback (solo polling de `state`/`result`).

Los propios planes del proyecto ya señalaban esto: «no hay scheduler ni event loop en Surtr»
(`docs/Plan-Generadores.md` línea 346, fila descartada de async/await; `docs/Plan-Disposicion.md`
§5.4 «Disposición asíncrona»).

---

## 3. El modelo de referencia: las corrutinas de Unity

Referencia estable de la plataforma objetivo de Surtr (todo single-thread, dentro del player loop):

**Arranque.** `MonoBehaviour.StartCoroutine(IEnumerator routine)` ejecuta el cuerpo
*síncronamente hasta el primer `yield return`* y devuelve un manejador opaco `Coroutine`. A partir
de ahí el motor llama `MoveNext()` cada frame en el momento que determine el último valor cedido.

**Instrucciones de espera** (lo que interpreta el scheduler según lo cedido):

| `yield return ...` | Cuándo se reanuda |
|---|---|
| `null` | el siguiente frame (tras todos los `Update`) |
| `new WaitForSeconds(t)` | cuando transcurren `t` segundos escalados |
| `WaitForFixedUpdate` | tras el paso de física |
| `WaitForEndOfFrame` | al cerrar el render del frame |
| `WaitUntil(pred)` / `WaitWhile(pred)` | cuando el predicado pasa/falla (polling por frame) |
| otro `IEnumerator` | sub-corutina anidada: corre hasta terminarla y luego continúa |
| `StartCoroutine(...)` (manejador) | ídem, pero con handle para `StopCoroutine` |
| `AsyncOperation` / `CustomYieldInstruction` | polling de `isDone`/`keepWaiting` |

**Composición.** Anidar por `yield return inner()` es espera *estructurada*: el continuador no
sigue hasta que el interno acaba. `StartCoroutine` en cambio *lanza* corutinas independientes sin
vínculo de vida.

**Cancelación.** `StopCoroutine(handle)` / `StopAllCoroutines()`; destruir el `MonoBehaviour`
detiene sus corutinas silenciosamente. No hay `finally` garantizado al cancelar en Unity: la
corutina simplemente deja de avanzar (diferencia importante con Surtr, donde `dispose()` sí corre
los `finally` — Surtr queda *mejor*).

**Errores.** Una excepción dentro del cuerpo mata esa corutina (se registra) y no contamina a las
demás; no hay propagación a un padre.

**Lecciones para Surtr:** (1) el scheduler pertenece al dueño del reloj (el engine/host), no al
lenguaje; (2) basta un protocolo mínimo — «avanza y dime el valor cedido» — y un vocabulario de
instrucciones interpretadas por ese scheduler; (3) la espera estructurada (anidada) y el spawn
independiente son dos operaciones distintas y ambas necesarias; (4) la cancelación debe ser
explícita y barata.

---

## 4. Análisis de reutilización

| Componente | Estado | Dónde | Reutilizable tal cual | Notas |
|---|---|---|---|---|
| Suspensión por copia de frame (`Yield`) | existe | `SurtrVirtualMachine.cs:4083-4136` | **Sí** | es el mecanismo de pausa de la corutina |
| Estado heap persistible (`SurtrGenerator`) | existe | `SurtrGenerator.cs:66-207` | **Sí** | ya es la «continuación» serializada en vivo |
| Reanudación desde host (`Advance`/`PushGeneratorFrame`) | existe, `internal` | `SurtrVirtualMachine.cs:792-890` | **Sí**, requiere hacerla pública | hueco A-1 |
| Inyección de valores (`send`/`Resumed`/`GenResumed`) | existe | `:552-564`, `SurtrGenerator.cs:131` | **Sí** | canal exacto del planificador |
| Lanzar excepciones en el punto suspendido (`raise`) | existe | `:584-627` | **Sí** | para timeouts/interrupciones del scheduler |
| Cancelación con `finally` (`dispose`/`GeneratorExit`) | existe | `:654-782` | **Sí** | mejor que Unity |
| Resultado final (`return expr;`/`result`) | existe | `SurtrGenerator.cs:140`, `SurtrRuntime.cs` built-ins | **Sí** | valor de completion |
| Guardia anti-reentrada (`State.Running`) | existe | `SurtrGenerator.cs:19-32` | **Sí** | protege frente a doble tick |
| Stub/cuerpo, despacho ordinario, interfaces/virtual | existe | `ModuleEmitter.cs:1693-1715` | **Sí** | ninguna pieza nueva de compilación necesaria en A |
| Delegación `yield from` (enlace O(1)) | existe | `SurtrVirtualMachine.cs:3992-4058` | **Sí** | composición secuencial de esperas |
| Rooting para el GC entre ticks | existe | `SurtrRuntime.cs:1903-1915` | **Sí** | el host rootea cada handle |
| Presupuesto de instrucciones por paso | existe | `SurtrVirtualMachine.cs:429-439` | **Sí** | acota coste de un tick |
| Interop C#/Unity (function pointers, closures, marshaling) | existe | `docs/Guia-Interop-Surtr-Csharp.md` | **Sí** | instrucciones de espera como tipos nativos; predicados como closures |
| API pública de avance de generadores desde C# | **falta** | `SurtrRuntime.cs:612-629` | — | envoltorios públicos (~60 líneas, A-1) |
| Scheduler/ticker (cola, reloj, orden, política) | **falta** | nuevo (host o Core) | — | corazón de cualquiera de las propuestas |
| Vocabulario de instrucciones de espera (tiempo/condición/join/all/any) | **falta** | nuevo | — | tipos nativos registrados por el host (A) o built-ins tipados (B) |
| Jerarquía spawn/join/cancelación compuesta | **falta** | nuevo | — | bookkeeping del scheduler (A manual; B estructurado) |
| Política de errores entre ticks + callbacks de fin | **falta** | nuevo | — | pollable hoy; callbacks nuevos |
| Tipado estático de las esperas | **falta** | — | — | elemento `unknown` o familia base (verificar conversión implícita a `unknown` en `yield`) |

---

## 5. Propuesta A — Scheduler en el host sobre los generadores actuales

**Filosofía:** el lenguaje ya sabe suspender y reanudar; lo único que falta es *quién* llama
`moveNext` cada tick y *qué significan* los valores cedidos. Ambas cosas pertenecen al host, que
es el dueño del reloj (Unity u otro). Cambio de compilador: **ninguno**. Cambio de formato: **ninguno**.

### 5.1 Diseño técnico

**A-1. Core: fachada pública de avance** (único cambio en `src/Surtr.Core`).
Envoltorios públicos en `SurtrRuntime` alrededor de los internos de
`SurtrRuntime.cs:612-629`:

```csharp
public bool GeneratorMoveNext(SurtrValue generator);        // ResumeGenerator
public bool GeneratorSend(SurtrValue generator, SurtrValue v);
public void GeneratorCancel(SurtrValue generator);          // DisposeGenerator (finallys)
public SurtrValue GeneratorResult(SurtrValue generator);
public bool GeneratorIsDone(SurtrValue generator);          // State == Exhausted
public SurtrValue GeneratorCurrent(SurtrValue generator);
```

(~60 líneas, cero riesgo: mismos caminos que usan los built-ins.)

**A-2. Nuevo componente host: `SurtrCoroutineHost`** (proyecto nuevo `Surtr.Host`, o carpeta del
juego; netstandard2.1, sin dependencias más allá de Core/Interop):

```csharp
public sealed class SurtrCoroutineHost
{
    public SurtrCoroutine Start(SurtrRuntime rt, SurtrValue generator); // AddRoot + encolar
    public void Tick(double timeSeconds, float deltaTimeSeconds);      // un frame
    public void Stop(SurtrCoroutine c);                                 // dispose + desencolar
    public event Action<SurtrCoroutine, Exception>? OnError;            // política de fallos
}

public sealed class SurtrCoroutine
{
    public SurtrCoroutineStatus Status { get; }   // Running/Succeeded/Failed/Cancelled
    public SurtrValue Result { get; }             // válido si Succeeded
}
```

`Tick` recorre la lista activa: para cada corutina pregunta a su **waiter** (construido del último
valor cedido) si ya puede continuar; si sí, `GeneratorSend(gen, valorDeContinuación)` (o
`GeneratorMoveNext` en el primer arranque); interpreta el nuevo `current` para construir el waiter
siguiente; `IsDone` → completada, `RemoveRoot`, leer `result`; excepción → `Failed` + evento.

**Interpretación de esperas, enchufable** (`ISurtrWaitResolver`): dado el `SurtrValue` cedido,
devuelve un waiter:

- `null` → continuar el próximo tick (el `yield null;` estilo Unity).
- Instancia de clases nativas acordadas → waiters tipados (abajo).
- Otro generador → arrancar sub-corutina hija y esperar su fin (**espera estructurada**, el caso
  de `yield return inner()`).
- Objeto nativo con contrato registrado `IYieldInstruction { ready(): bool; payload(): unknown }` →
  polling genérico para tipos del propio juego.

**A-2bis. Modo Unity directo (atajo recomendado).** En vez de un host propio, un adaptador de
~80 líneas expone el generador como `System.Collections.IEnumerator` y se lo entrega a
`MonoBehaviour.StartCoroutine`: `MoveNext()` avanza el generador Surtr; `Current` devuelve el valor
cedido **sin tocarlo**, así que Unity mismo interpreta `null`, `WaitForSeconds`, enumeradores
anidados y `AsyncOperation`. `StopCoroutine` mapea a `dispose()`. Cero schedulers escritos; el
motor hace de scheduler.

**Vocabulario mínimo de instrucciones** (tipos nativos C# registrados vía
`Surtr.Interop`, cf. `docs/Guia-Interop-Surtr-Csharp.md` §4-5; los predicados cruzan como
closures, §5 tabla «delegate → closure»):

```csharp
[SurtrNativeType(Module = "async", Name = "Wait")]
public static class Wait
{
    public static object Seconds(float t) => new WaitForSecondsWaiter(t);
    public static object Until(Func<bool> pred) => ...;   // closure Surtr -> C#
    public static object Frames(int n) => ...;
}
```

### 5.2 API propuesta y ejemplos Surtr

Las corutinas son funciones generadoras cuyo elemento declara `unknown` (acepta cualquier
instrucción cedida; verificar la conversión implícita a `unknown` en `BindConverted`,
`BodyBinder.Statements.cs:786` — `unknown` acepta todo valor por diseño de §5.10). Nadie consume
`current`: se consume `result` y el flujo de control.

```surtr
import async;                      // tipos nativos Wait.* registrados por el host

generator respawn(enemy: Enemy): unknown {
    log("muerto: " + enemy.name);
    yield Wait.seconds(3.0);                       // espera tiempo
    yield Wait.until(() => spawner.free);          // espera condición (closure)
    enemy.activate();
}

generator wave(n: int): string {                   // 'string' es lo que RETURN deja en .result
    for (var i = 0; i < n; i += 1) {
        yield respawn(spawnGrunt(i));              // esperar a OTRA corutina (sub-corutina)
        yield null;                                // un frame
    }
    yield Wait.all(Wait.seconds(1.0), shieldUp());
    return "wave-complete";
}

fun onPlayerDeath(): void {
    Host.start(wave(3));                           // nativo: encola en el scheduler del host
}
```

Lado C# (modo propio):

```csharp
var host = new SurtrCoroutineHost();
var start = FindFunction("game", "Host", "start");          // shim nativo -> host.Start(...)
var c = host.Start(runtime, CallGeneratorStub("wave", runtime.Int(3)));
// en Update():
host.Tick(Time.time, Time.deltaTime);
if (c.Status == SurtrCoroutineStatus.Succeeded) Show(c.Result.AsString());
```

Lado C# (modo Unity directo):

```csharp
IEnumerator Adapt(SurtrRuntime rt, SurtrValue gen)
{
    while (rt.GeneratorMoveNext(gen))
    {
        var yielded = rt.GeneratorCurrent(gen);
        if (yielded.IsReference && Unwrap(yielded) is IEnumerator sub)   // generador anidado
            yield return Adapt(rt, yielded);                              // recursivo
        else
            yield return ToUnityObject(yielded);                          // null / WaitForSeconds / ...
    }
}
gameObject.AddComponent<Runner>().StartCoroutine(Adapt(runtime, gen));
```

### 5.3 Cambios por proyecto

| Proyecto | Cambio |
|---|---|
| `Surtr.Core` | A-1: fachada pública de avance/result/current/cancel sobre `SurtrRuntime.cs:612-629`. Nada más |
| `Surtr.Compiler` | **ninguno** |
| `Surtr.Interop(.Attributes)` | opcional: atributos/plantilla para los tipos `Wait*` y contrato `IYieldInstruction` de ejemplo |
| Nuevo `Surtr.Host` (o código del juego) | `SurtrCoroutineHost` + waiters + adaptador Unity + shims `Host.start/stop` |
| Stdlib/tests | tests de integración host-driven (tick manual determinista) |

### 5.4 Pros / contras / riesgos

**Pros**

- Coste mínimo y riesgo mínimo: 100 % de la mecánica ya construida y probada (14 tests de opcode +
  65 de emisión, `Surtr.Tests/VM/SurtrVirtualMachineGeneratorTests.cs`,
  `src/Surtr.Tests/Compiler/CodeGen/GeneratorEmissionTests.cs`).
- Modelo mental idéntico al de Unity para el público objetivo (`Plan-Extensiones.md:57`).
- El modo adaptador delega el scheduling en el motor: WaitForSeconds, física, AsyncOperation y
  anidado funcionan con semántica nativa de Unity sin reimplementar nada.
- El scheduler vive donde vive el tiempo; cada host usa el suyo (editor determinista, juego, tests).
- Cero cambios de formato/opcodes/descriptores.

**Contras**

- Las esperas no están tipadas en Surtr (elemento `unknown`); un typo en `Wait.secunds(3)` falla en
  runtime, no en compilación.
- El vocabulario es convención de host: dos hosts pueden definir waiters incompatibles (mitigable
  con un paquete estándar de tipos, ver recomendación).
- Spawn/join/cancelación compuesta es bookkeeping manual del host; sin árbol, `Stop` de un padre no
  cancela hijos salvo que el host lo implemente.
- Fácil olvidarse de llamar `Tick` (las corutinas se congelan en silencio).

**Riesgos**

- R-1: rooting olvidado → el GC barre el generador entre ticks. Mitigar: `Start` rootea siempre y
  `SurtrCoroutine` posee el ciclo `AddRoot`/`RemoveRoot` (`SurtrRuntime.cs:1903-1915`).
- R-2: excepción en un tick deja la máquina mid-frame. Mitigar: capturar en el host y llamar
  `ResetExecution()` si se continúa (`SurtrRuntime.cs:1890`, documentado exactamente para eso).
- R-3: `send()` queda **reservado para el planificador** mientras la corutina está planificada
  (el valor de reanudación es del scheduler). Documentarlo; el uso interactivo de `send` sigue
  siendo válido fuera del scheduler.
- R-4: un paso de corutina puede ser caro; usar `InstructionBudget` por paso si se quiere acotar.
- R-5: confirmar con un test que `generator f(): unknown { yield Wait.seconds(1.0); }` compila
  (conversión clase-nativa → `unknown`); si no, declarar el elemento como una clase base común
  `YieldSignal` registrada por el host.

---

## 6. Propuesta B — Corutinas de primera clase con planificador en Core

**Filosofía:** el modelo de corutina es parte del lenguaje/runtime, con esperas tipadas, spawn/join
estructurado y un scheduler canónico determinista dentro de `Surtr.Core` (single-thread,
driven por el host que aporta el reloj). Los hosts solo hacen `Tick(now)`.

### 6.1 Diseño técnico

**B-1. Tipo y declaraciones.**

- Nueva introductora de miembro `coroutine` hermana de `generator` (reservada dura, como
  `Lexer.cs:896` hizo con `generator`):

  ```surtr
  coroutine fun wave(n: int): string { ... }
  ```

  Semántica: el **tipo de retorno es el resultado** (`return expr;` se chequea contra él, a
  diferencia del generador); los `wait` ceden instrucciones de una familia built-in cerrada. Se
  implementa como un generador cuyo elemento es el built-in `Waiter` y cuyo `return` es el
  resultado — el split stub/cuerpo (`ModuleEmitter.cs:1693-1715`) se reutiliza tal cual con un
  tercer molde.
- Descriptor nuevo `C<R>` (hermano de `Y<elem>`, `SurtrClassReference.SymbolGenerator`), con bump
  menor de `FormatVersion` — asumible: las imágenes se recompilan por versión.

**B-2. Familia de instrucciones de espera** (built-ins en Core, sellada, trazable):

```
WaitTime(f: float)   WaitFrames(n: int)   WaitUntil(p: closure): bool
Join(h: Coro)        WaitAny(ws: array<Waiter>)   WaitAll(ws: array<Waiter>)
```

Instancias son entidades registradas (trazado por `VisitReferences`, patrón de
`SurtrGenerator.cs:341-366`). `null` cedido = próximo tick.

**B-3. Sintaxis de espera y composición.**

```surtr
wait <expr>;                 // statement: cede la instrucción; se reanuda cuando el scheduler
                             // resuelve, inyectando por send el payload (canal existente)
let x = join h;              // espera estructurada + lee el resultado del hijo
spawn f(args);               // dispara corutina independiente, devuelve Coro (handle entidad)
cancel h;                    // dispose del generador + cancelación recursiva de hijos
```

Lowerings (todos con mecánica existente):

- `wait e;` → evaluar `e`; convertir a `Waiter`; `Yield` (+ `GenResumed` si el payload se lee).
  Es exactamente `EmitYield` (`MethodBodyEmitter.cs:1466-1484`).
- `join h` → `wait Join(h);` + cast del `Resumed` al tipo resultado del hijo (tipado: el binder
  conoce `R` de `h` porque el stub de `wave` declara `C<string>`).
- `spawn f(x)` → llamada ordinaria al stub (crea el generador, no ejecuta) + llamada nativa
  `scheduler.spawn(generator)` que registra y devuelve el handle. **Cero opcodes nuevos**; si un
  perfil lo pidiera, `CoroSpawn` iría a los `0xF0-0xFF` libres (`OpCode.cs:48-50`).

**B-4. Planificador canónico en Core** (`SurtrScheduler`, propiedad de `SurtrRuntime`):

- Cola priorizada por instante de despertar + lista de polling (condiciones); `Tick(double now)`
  avanza las listas usando la fachada del A-1 (misma `Advance`, `SurtrVirtualMachine.cs:792-828`).
- Árbol de corutinas: cada `SurtrCoroutineNode` conoce padre/hijos; `cancel` recorre en
  profundidad llamando `DisposeGenerator` (los `finally` corren, `SurtrVirtualMachine.cs:654-759`)
  — cancelación compuesta que Unity no da.
- Política de errores: `onFailure(handler)` global o por handle; el fallo de un padre cancela (o
  no, configurable) a los hijos; `join` relanza el error del hijo en el padre.
- Presupuesto por paso y por tick (reusa `StepBudget`).
- Determinismo: sin reloj propio; el host pasa `now` — testeable en CI sin engine.

**B-5. Tipado.** `wait e;` exige que `e` convierta a `Waiter` (error en compilación, a diferencia
de A); `join h` tipa como el resultado del hijo; `spawn` exige argumentos de una `coroutine fun`.
FlowAnalysis: el `return expr;` de una corutina se chequea contra `R` (extensión directa de
`BodyBinder.Statements.cs:692-731`, hoy convertido a `unknown`).

### 6.2 Ejemplos Surtr

```surtr
coroutine fun respawn(enemy: Enemy): void {
    yield wait seconds(3.0);
    wait until(() => spawner.free);
    enemy.activate();
}

coroutine fun wave(n: int): string {
    for (var i = 0; i < n; i += 1) {
        let g = spawn grunt(i);          // independiente
        wait seconds(0.5);
        if (g.failed()) { cancel all(); raise WaveError(g.error()); }
    }
    let s = join shield();               // estructurado: espera + resultado tipado
    wait all(seconds(1.0), archers());
    return "ok";
}

fun tickHost(dt: float): void {          // llamado por el host cada frame
    // el scheduler de Core avanza; este shim solo pasa el reloj
}
```

### 6.3 Cambios por proyecto

| Proyecto | Cambio |
|---|---|
| `Surtr.Core` | `SurtrScheduler`, nodos de árbol, `SurtrWaiter` family, fachada pública (A-1), built-ins `Wait*` |
| `Surtr.Core/Bytecode` | opcional: descriptor `C<R>` (bump de formato); ninguno obligatorio |
| `Surtr.Compiler` | keyword `coroutine`, `ParseWait`/`ParseSpawn`/`ParseJoin`, binder (reglas de `wait`, resultado tipado, `spawn`), emitter (lowerings sobre `Yield`/nativas), FlowAnalysis, LSP |
| `Surtr.Stdlib` | módulo `surtr.async` con los azúcares documentables |
| Tests/Bench | suite nueva scheduler; caso bench de corutina con esperas |

### 6.4 Pros / contras / riesgos

**Pros**

- Esperas **tipadas**: errores de vocabulario en compilación; `join` con resultado estático.
- Semántica canónica única y portable entre hosts; testing determinista sin engine.
- Cancelación compuesta y política de errores estructurada (mejor que Unity).
- Composición: `WaitAll`/`WaitAny`/`Join` son combinadores de primera clase; `yield from` cubre la
  secuenciación pura con coste O(1) medido (`Plan-Generadores.md` §14.3).

**Contras**

- Superficie de lenguaje grande: keyword, statements, familia built-in, scheduler en Core.
- Bump de formato (si hay descriptor) y mantenimiento de una segunda ruta de compilación junto a
  `generator`.
- Solapa con schedulers que los hosts ya tienen (Unity): dos planificadores compitiendo por el
  frame; el de Core necesita además que el host recuerde darle el reloj.
- El canal `send` queda definitivamente reservado al scheduler (rompe el uso interactivo de
  `send()` en corutinas planificadas).

**Riesgos**

- R-1: diseñar semántica de fallo/cancelación mal a la primera; es la parte con historia larga en
  otros ecosistemas (structured concurrency). Mitigar: fase de especificación propia.
- R-2: coste de mantener dos introducidas (`generator` y `coroutine`) con reglas parecidas pero no
  iguales (elemento vs resultado).
- R-3: interacción `wait` dentro de `try/finally` — hoy permitida y correcta a nivel VM
  (`RaiseInGenerator`, `dispose`); hay que escribirla: ¿un `finally` puede `wait`? (respuesta
  razonable: no; misma razón que `BodyBinder.Statements.cs:777-783` prohíbe `yield` en `finally`).

---

## 7. Propuesta C — `async`/`await` estilo C#

Mencionada por completitud (`Plan-Generadores.md` §8 ya la descartó: «no hay scheduler ni event
loop»). Diseño posible: `async fun foo(): int` compila exactamente a un generador; `await e` =
`yield waiter` + cast del `GenResumed` al tipo esperado; el scheduler es el de la propuesta B.

Por qué no como objetivo inicial:

1. **Coloración de funciones**: todo llamador transitivo de un `await` debe ser `async`; en una
   VM single-thread sin thread pool ni IO real, la división sync/async no compra nada y parte la
   API del stdlib y del código de juego en dos mundos que no componen sin `GetAwaiter().GetResult()`
   — que aquí sería bloquear el único thread, imposible.
2. `Task<T>` no tiene referente: no hay paralelismo; el «futuro» es una corutina ya planificada.
   El concepto honesto del dominio es `coroutine`/`spawn`/`join`, no `Task`.
3. El público objetivo (Unity) piensa en corrutinas, no en tasks; C# dentro de Unity usa ambos y
   para scripting de juego la corrutina es el idioma común.
4. Como **azúcar superficial futura** sobre B (`await` = `wait` + cast tipado del payload) es
   compatible y barata; nada se pierde posponiéndola.

---

## 8. Recomendación final

**Adoptar la Propuesta A ahora, con dos refinamientos que abren la puerta a la B sin costo hoy.**

Justificación:

1. **Lo difícil ya está hecho y probado.** La investigación muestra que la suspensión surt-mode
   (copia de frame relocatable a heap, `SurtrGenerator.cs:98` + `SurtrVirtualMachine.cs:4083-4136`),
   la reanudación desde fuera de la máquina (`:792-890`), la inyección bidireccional
   (`send`/`GenResumed`), el lanzamiento en el punto suspendido (`raise`) y la cancelación con
   `finally` (`dispose`/`GeneratorExit`) son exactamente el mecanismo de unas corutinas asíncronas.
   Reimplementar o transformar nada de eso (estrategia A de máquinas de estado, frames en heap
   siempre) sería tirar trabajo medido y superior al estándar de la industria para este uso.
2. **El hueco es de orquestación y el orquestador es del host.** El tiempo, el frame y la política
   de errores pertenecen al embedder (Unity u otro). Poner el scheduler en el host (A) replica la
   arquitectura de Unity, que es el público objetivo declarado del lenguaje, y permite además el
   modo adaptador en el que **el propio motor hace de scheduler** con sus `WaitForSeconds`,
   física y `AsyncOperation` nativos — imposible de igualar desde Core.
3. **Coste/riesgo mínimos**: A es ~60 líneas en Core + un componente host; cero cambios de
   compilador, opcodes, descriptores o formato. B es una superficie de lenguaje entera cuyo valor
   añadido principal (tipado de esperas y cancelación compuesta) se puede incorporar después de
   forma incremental y compatible.
4. **Camino de evolución sin callejón**: fijar desde el día uno un **vocabulario estándar de
   instrucciones de espera** como tipos nativos documentados (paquete compartido, no convención
   privada de cada host) hace que (a) los scripts sean portables entre hosts, y (b) la migración a
   la B sea re-tipificar esas mismas clases como built-ins y añadir `spawn`/`join`/`cancel` sobre
   la misma mecánica de suspensión. Nada de lo construido en A se descarta: el scheduler de Core de
   la B consumiría la misma fachada de avance que el host.

**Plan sugerido:**

| Fase | Contenido |
|---|---|
| A-1 | Fachada pública de avance en `SurtrCore` (`GeneratorMoveNext/Send/Cancel/Result/Current/IsDone`) + test de tick manual determinista |
| A-2 | `SurtrCoroutineHost` de referencia (cola, rooting, OnError, sub-corutinas anidadas) en un proyecto host de ejemplo |
| A-3 | Adaptador Unity (`IEnumerator` ↔ generador) + guía documental hermana de `Guia-Interop-Surtr-Csharp.md` |
| A-4 | Vocabulario estándar de waiters (`Wait.seconds/until/frames/all/any`) como tipos nativos publicados |
| B (evolución) | `coroutine fun` tipado, `wait/spawn/join/cancel` como statements, scheduler opcional en Core para hosts sin motor |

**Riesgos a gestionar transversalmente:** rooting del generador entre ticks (obligatorio en
`Start`/`Stop`), `ResetExecution` tras una excepción de tick, reserva del canal `send` para el
planificador, presupuesto de instrucciones por paso, y verificación temprana (un test) de que
ceder objetos de instrucción contra elemento `unknown` (o una base común `YieldSignal`) compila.
