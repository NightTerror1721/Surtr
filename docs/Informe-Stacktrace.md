# Informe: Manejo de errores y sistema de stacktrace en Surtr

*Fecha: agosto de 2026. Alcance: investigación (sin cambios de código). Objetivo: determinar qué existe hoy para trazar errores, y proponer diseños para obtener, ante una excepción, el stack de llamadas completo (función, módulo y línea), como hacen Java, C#, Python, Lua, etc.*

---

## 1. Investigación: estado actual

### 1.1 Resumen ejecutivo

Surtr tiene **un manejo de excepciones maduro y bien diseñado** (try/catch/finally en el lenguaje, tablas de handlers por método, desenrollado propio de la VM, composición correcta a través de llamadas nativas), pero **no tiene ningún sistema de stacktrace**: cuando una excepción escapa al host C#, llega como mensaje de texto plano sin función, ni fichero, ni línea. Paradójicamente, la infraestructura para construirlo está casi toda puesta: los call frames ya guardan método + puntero de instrucción publicados, y el propio plan de la VM reserva una "Fase 6 — diagnostics" para ello (`docs/VM-Plan.md:1077-1079`: *"Frames already carry enough for a stack trace (method, chunk, saved IP), and the handler search already walks them. `SurtrThrownException` does not yet include one. Cheap to add"*). Lo único que falta de verdad es **información de línea en la imagen**, porque el compilador no emite ninguna tabla de secuencia puntos.

### 1.2 El call stack de la VM: ya es "walkable"

El call stack es un array gestionado de `SurtrCallFrame` (`src/Surtr.Core/VM/SurtrVirtualMachine.cs:76`, reservado en `:151`, capacidad `DefaultCallDepth = 1024` en `:69`). Cada frame contiene (`src/Surtr.Core/VM/SurtrCallFrame.cs:37-94`):

| Campo | Línea | Significado |
|---|---|---|
| `Base` | :48 | Slot 0 del frame (locales + argumentos + operandos). |
| `CodeBase` | :51 | Primer byte del chunk en ejecución. |
| `IP` | :57 | Puntero de instrucción **publicado** antes de cualquier transferencia fuera del bucle. |
| `Chunk` | :60 | Chunk (constantes, tablas de acceso, módulo dueño). |
| `Method` | :63 | Método en ejecución — *"Kept for diagnostics and stack traces"*. |
| `Closure` / `Generator` | :69/:81 | Closure o generador que corre el frame. |
| `LocalCount` / `ArgumentCount` / `ExpectedResults` | :84-93 | Protocolo de frame. |

El comentario de apertura del propio struct lo dice explícitamente (`src/Surtr.Core/VM/SurtrCallFrame.cs:14-18`): el array de frames es *"a real, walkable stack trace at any point — including while a native call is in flight"*.

La pieza que hace esto fiable es el **invariante de publicación de IP**: el bucle de interpretación escribe `current.IP = ip` y `_sp = sp` antes de todo lo que puede elevar o transferir control a código host (ejemplos: división entera en `src/Surtr.Core/VM/SurtrVirtualMachine.cs:1374-1378`; frontera nativa en `:4244-4245`; opcode `Throw` en `:3453-3455`). Por eso la búsqueda de handlers lee estado, nunca locales (`:894-906`), y por eso un walk del array de frames en cualquier punto de fallo produce offsets correctos para **todos** los frames, incluido el que estaba ejecutando.

Los frames se crean por tres caminos, todos escribiendo los mismos campos: entrada del host (`PushEntryFrame`, `:469-520`), entrada por opcode de llamada (secuencia compartida `InvokeResolved`, `:4278-4347`) y reconstrucción de un generador suspendido (`PushGeneratorFrame`, `:842-890`, y su gemelo dentro del bucle `EnterGeneratorFrame`, `:4169-4237`).

### 1.3 Manejo de excepciones: cómo funciona hoy

**En el lenguaje.** Surtr tiene `try/catch/finally`, `throw` como sentencia y como expresión, tipado del catch y cláusulas tipadas: la sintaxis está en `src/Surtr.Compiler/Syntax/Ast/StatementSyntax.cs:294-340` (`CatchClauseSyntax`, `TryStatementSyntax`) y `:359` (`ThrowStatementSyntax`), más `ThrowExpressionSyntax` (`ExpressionSyntax.cs:547`). La jerarquía de biblioteca existe: `Exception` y sus subclases declaradas como clases built-in en `src/Surtr.Core/Runtime/BuiltIns/SurtrBuiltIns.cs:320-329`.

**Tablas de handlers, no opcodes.** Cada método bytecode lleva `SurtrExceptionHandler[]` (`src/Surtr.Core/Runtime/Classes/SurtrBytecodeMethodInfo.cs:24,113-117`), con región protegida absoluta al chunk, offset del handler y tipo atrapado (null = catch-all, que es como se expresa `finally`): `src/Surtr.Core/Runtime/Classes/SurtrExceptionHandler.cs:28-93`. Entrar en un `try` no emite nada y no cuesta nada; solo pagar quien eleva (`docs/VM-Plan.md:110-116`).

**Dos velocidades, un mismo buscador** (`Execute` en `src/Surtr.Core/VM/SurtrVirtualMachine.cs:907-939`):

1. Un `throw` de Surtr **nunca se convierte en excepción CLR** mientras haya un handler al alcance: `OpCode.Throw` (`:3451-3463`) llama a `TryEnterHandler` (`:959-1025`), que desenrolla frames propios saltando directamente al handler. Un catch cuesta un recorrido de tabla, no los microsegundos de un throw CLR.
2. Los traps de la VM (índice fuera de rango, división por cero, cast inválido, overflow de pilas...) se crean como excepciones CLR en helpers fríos `NoInlining` (`:4369-4450`); `Execute` los convierte en objetos Surtr vía `AsSurtrObject` (`:1037-1044`, mapeo de tipos CLR a clases biblioteca en `SurtrBuiltIns.ExceptionClassFor`, `src/Surtr.Core/Runtime/BuiltIns/SurtrBuiltIns.cs:561-581`) y los ofrece a las mismas tablas.
3. Solo una excepción **nada atrapada** sale hacia el host, envuelta en `SurtrThrownException` (construida por `Uncaught`, `:4446-4450`) portando la referencia al objeto elevado, enraizada hasta `ResetExecution` (`src/Surtr.Core/Runtime/SurtrRuntime.cs:1890`).

**Composición con nativos.** La búsqueda se detiene en `entryDepth` (`:968`): una corrida iniciada desde dentro de una función nativa desenrolla solo sus frames y deja salir la excepción como CLR para que retome la búsqueda en la corrida exterior. Esto significa que en un throw profundo **todos** los frames siguen vivos en `_frames` hasta que la búsqueda termina — dato relevante para capturar el stack.

**Otros detalles:** el presupuesto de pasos cobra entrar en handler (`HandlerEntryCost`, `:436`; cargo en `:988-996`) y su aborto nunca es atrapable (`SurtrBudgetExceededException`, `src/Surtr.Core/VM/SurtrExecutionException.cs:78-82`); `GeneratorExit` no puede ser atrapado por catches tipados (`Catches`, `:1056-1075`) porque es el mecanismo de `dispose()` de generadores.

### 1.4 Qué le llega hoy al host

`SurtrExecutionException` (`src/Surtr.Core/VM/SurtrExecutionException.cs:27-59`) es una `System.Exception` ordinaria con `Message` y un `SurtrType` (clase biblioteca que representa al catch). `SurtrThrownException` (`:101-120`) añade solo `Reference` (el objeto elevado) y una descripción con el **nombre de la clase** del objeto (`Uncaught`, `SurtrVirtualMachine.cs:4446-4450`).

Es decir: el host recibe *qué* pasó y *de qué tipo*, pero **ni una sola pista de dónde**. El host de referencia confirma el síntoma: `surtr run` captura `SurtrExecutionException`, resetea y imprime únicamente `exception.Message` (`src/Surtr.Run/Program.cs:88-95`). Los tests tampoco esperan localización (`src/Surtr.Tests/VM/SurtrVirtualMachineExceptionTests.cs:37-50`).

Nota adicional: hoy **cada trap ya paga** la captura del stack CLR que hace `System.Exception` al construirse — un stack de frames internos del intérprete, inútil para diagnosticar scripts. El coste ya se está pagando; solo que el producto es ruido.

### 1.5 Metadatos de módulo: qué hay y qué no hay

Por módulo hay un chunk (`src/Surtr.Core/Runtime/Classes/SurtrChunk.cs:30`): bytecode contiguo (`Code`, :33), pool de constantes (:36), offsets por método (:39), y tablas de acceso resueltas en carga — tipos (:64), campos (:67), métodos (:70), caché monomórfica de interfaces (:86), módulos (:99) — más el módulo dueño (:108).

`SurtrBytecodeMethodInfo` (`src/Surtr.Core/Runtime/Classes/SurtrBytecodeMethodInfo.cs:16-62`) guarda nombre, firma, `EntryIndex`, `CodeOffset`, `LocalCount`, `MaxStackSize`, `Handlers`. De `SurtrMemberInfo` hereda `Name` y el tipo declarante. **No existe** ningún campo de fichero fuente, línea o columna en ningún metadato de runtime.

La imagen (`.surtrc`, versión actual 11, `src/Surtr.Core/Bytecode/Image/SurtrModuleImage.cs:150`, con historial documentado de bumps :74-149) serializa exactamente eso: cola de método `entryIndex, localCount, maxStackSize, handlers[]` (`docs/Module-Format.md:250`) y entradas de handler `tryStart/tryEnd/handlerOffset/catchType` (`docs/Module-Format.md:270-277`; escritura en `src/Surtr.Core/Bytecode/Image/SurtrModuleImageWriter.cs:399-430`, lectura en `SurtrModuleImageReader.cs:473-488`). **No hay sección de debug, ni tabla de líneas, ni nombre de fichero.**

Dato menor pero revelador: el emisor sí conserva **nombres de locales** durante la emisión "for diagnostics only" (`src/Surtr.Core/Bytecode/Emit/SurtrMethodBuilder.cs:81,388-413`), pero nunca los serializa — mueren con el builder.

### 1.6 Información de línea existente: solo en tiempo de compilación

- `SourceSpan`/`SourceLocation` (`src/Surtr.Compiler/Syntax/SourceSpan.cs:23`, `SourceLocation.cs:11-31`): línea/columna/offset 1-based en cada token y nodo de sintaxis.
- Diagnósticos del compilador llevan `sourceName` + span (`src/Surtr.Compiler/Diagnostics/SurtrDiagnosticBag.cs:71-81`); el binder arrastra `sourceName` por todo el binding (`src/Surtr.Compiler/Binding/BodyBinder.cs:45,143`).
- El LSP trabaja exclusivamente sobre texto + spans de AST re-parseando (`src/Surtr.LanguageServer/Workspace/CompilationSnapshot.cs:88-163`); no consume nada del runtime.
- **El gancho clave para el futuro**: `MethodBodyEmitter` mantiene `_at` — el nodo de sintaxis que se está bajando ahora mismo — actualizado en `Statement()` (`src/Surtr.Compiler/CodeGen/MethodBodyEmitter.cs:203-209`) y `Expression()` (`:1686-1692`), y lo usa para apuntar errores de emisión (`Unsupported`, `:6090-6093`; `ModuleEmitter.cs:194`). Es decir, **en cada momento de la emisión el compilador sabe qué línea está emitiendo**; solo falta grabarlo.
- La emisión de try/catch ya usa el maquinario de regiones protegidas basadas en etiquetas (`BeginTry`/`EndTry`/`AddCatch`, `SurtrMethodBuilder.cs:420-462`; uso en `MethodBodyEmitter.cs:1311-1351` y `:1415-1436`), patrón directamente reutilizable para puntos de secuencia (las posiciones absolutas se mueven por relajación de saltos — `SurtrMethodBuilder.cs:10-13` — así que cualquier registro debe ser vía etiqueta o post-layout, igual que los handlers).

### 1.7 Reconocimiento en la documentación

- `docs/VM-Plan.md:1077-1079` — **Fase 6 (diagnostics), abierta**: frames suficientes para un stack trace; `SurtrThrownException` aún no incluye uno; "barato de añadir, no cuesta hasta que algo eleva".
- `docs/Opcodes.md:648` — semántica de `Throw` y de las tablas de handlers.
- `docs/Language-Syntax.md:993,1043-1045` — las llamadas inlinadas **no dejan frame** y no aparecerán en un stack trace (decisión ya documentada; afecta a la fidelidad del trace futuro).
- `docs/Plan-TiposDeValor.md:137-138` — describe la entrada en handler (reset del operand stack a `base + LocalCount` + push de la excepción).

### 1.8 Casos que cualquier diseño debe contemplar

1. **Frames del frame en suspensión vs en ejecución.** El frame superior está ejecutando (IP publicado justo antes de fallar); los inferiores están aparcados tras su llamada. Ambos casos dan un offset válido gracias al invariante de publicación.
2. **Corridas anidadas (re-entrada nativa).** Como los frames de la corrida exterior siguen debajo (`InvokeResolved` publica y la corrida interna apila encima), un walk desde arriba captura también la cadena lógica del llamante nativo.
3. **Generadores.** Un cuerpo reanudado solo tiene sus propios frames encima del stack; el consumidor no aparece. Limitación idéntica a la de otros lenguajes (Python muestra el traceback interno del generador). Además, `RaiseInGenerator` (`:584-627`) y `CloseOne` (`:684-759`) reconstruyen frames ad hoc — el walk debe cubrirlos (lo hace, pues pasan por `TryEnterHandler`/`Execute`).
4. **Inlining.** Los cuerpos inlinados no dejan frame (`docs/Language-Syntax.md:1043-1045`): el trace mostrará el frame del método contenedor con la línea del sitio de llamada, no un frame sintético. Aceptable y estándar.
5. **Traps vs throw de Surtr vs excepciones host.** Tres orígenes, un único punto común de salida hacia el host: los `catch` de `Execute` (`:915-937`) — punto natural de captura única.

### 1.9 Cómo lo resuelven otras VMs embebidas

| VM | Info de líneas | Cuándo construye el trace | Notas |
|---|---|---|---|
| **Lua 5.x** | `lineinfo`/`abslineinfo` por prototipo: array compacto instrucción→línea (deltas). | Solo bajo demanda: `debug.traceback()` recorre la cadena de `CallInfo` y formatea texto; `luaL_where` antepone `fichero:línea:` al mensaje. | Debug info siempre en memoria pero barata; errores via longjmp; hooks de depuración opcionales. |
| **LuaJIT** | Igual que Lua (líneas por instrucción BC) más mapas para trazas desde código JIT. | `lj_debug_dumpstack` al error; `debug.traceback` funciona incluso para errores C; frames C marcados `[C]`. | Muestra el mismo modelo: metadatos aparte, formato perezoso. |
| **Wren** | `FnDebug` adjunto a cada función: `name`, `sourcePath` y array `lines[]` indexado por offset de instrucción. | En error de runtime, el walk de `fiber->frames` imprime `[módulo línea N] in método` y publica el error en el fiber. | Siempre emite la info (no hay modo sin ella); coste cero en ejecución normal, ~2 bytes/instrucción de memoria. |
| **AngelScript** | `scriptFunction->lineNumbers`: pares ordenados posiciónPrograma→línea generados por el compilador. | Resolución por búsqueda en la tabla; el host registra callback de excepción y consulta `GetCallstackSize()/GetFunction(i)/GetLineNumber(i)` para construir el trace completo. | API de introspección del contexto; frames nativos distinguidos. |
| **Pawn** | Ninguna en release; archivo lateral `.dbg` (AMX_DBG) opcional con símbolos/líneas generado con `-d`. | Sin traceback de pila estándar; solo línea actual si el dbg está cargado. | Ejemplo de extremo "release pelado": útil como advertencia, no como modelo. |
| **JVM** | `LineNumberTable` por método en el class file (bci→línea). | `Throwable.fillInStackTrace` captura en el throw; `StackTraceElement` se resuelve **perezosamente** desde método+bci al volcar. | Flags para omitir (`-XX:-StackTraceInThrowable`); dedupe de fast-throws. |
| **Python** | `co_lnotab` por code object; frames son objetos heap. | Traceback objects creados al propagarse la excepción. | Modelo caro (frame = objeto); no imitable en una VM sin asignaciones. |

**Lecciones transversales:** (1) todos guardan el mapa instrucción→línea en metadatos **separados** del hot path, jamás en la instrucción; (2) los frames guardan función+offset y la línea se **resuelve al formatear**, no se almacena por llamada; (3) el trace se construye/formatea solo cuando hay error; (4) salvo Pawn, todos emiten la info siempre porque su coste real es solo tamaño; (5) los frames nativos/host se marcan como tales.

---

## 2. Conclusiones

**Qué existe y se puede reutilizar tal cual:**
- El array de frames es un stack trace caminable con `Method` + `CodeBase` + `IP` válidos en todo punto de fallo (invariante de publicación, §1.2). Es el 80 % del trabajo.
- La búsqueda de handlers ya recorre esos frames exactamente como haría la captura (`TryEnterHandler`); hay un punto único donde toda excepción que escapa pasa necesariamente: los `catch` de `Execute` (§1.8.5).
- El compilador sabe la línea que está emitiendo en todo momento (`_at`, §1.6); la mecánica de etiquetas/región protegida resuelve el problema de la relajación de saltos.
- La imagen tiene versión, pool de strings con internado, y una disciplina de bumps documentada lista para alojar una sección nueva.
- Clase `Exception` con slot de mensaje (`MessageSlot`, `src/Surtr.Core/Runtime/BuiltIns/SurtrStandardLibrary.cs:37`) y helpers de construcción fríos (`SurtrRuntime.NewException`, `SurtrRuntime.cs:514-531`).

**Qué falta:**
- Capturar y transportar el stack: `SurtrThrownException`/`SurtrExecutionException` no llevan frames.
- Información de líneas: ni emisor, ni metadato, ni formato de imagen la registran. Sin ella, un trace solo puede dar `módulo::función + offset`.
- Formato/presentación: ningún formateador tipo `at game.combat:hit(archivo:línea)`; el host de referencia imprime solo `Message`.
- Decisión de política para generadores (traza parcial aceptada) y para el caso catch-relanza-per-frame (evitar presión de GC en hosts de Unity que atrapan cada frame).

**Riesgos si no se hace nada:** diagnosticar un script en producción de Unity queda reducido a leer el mensaje; el LSP/host no pueden señalar la línea; y cada trap sigue pagando una captura CLR inútil que ni siquiera sirve como sustituto.

---

## 3. Propuestas de implementación

Las tres propuestas son complementarias: A resuelve "quién llamó a quién", B añade "en qué línea", C afina el coste de A+B. Se estima en días de trabajo de un desarrollador familiarizado con el repo.

### Propuesta A — Capturar el stack de llamadas de la VM al escapar una excepción (solo Core + Run)

**Descripción.** Cuando una excepción va a dejar la máquina hacia el host, recorrer `_frames[0.._frameCount)` construyendo instantáneas `{ Método, Offset de byte }` y adjuntarlas a la excepción CLR que sale. La captura se engancha en el punto único que ya existe: los bloques `catch` de `Execute` (`SurtrVirtualMachine.cs:907-939`), justo antes de re-lanzar cuando `TryEnterHandler` devolvió false — ahí cubre los tres orígenes (trap CLR, excepción host, `SurtrThrownException` re-escapada) y ve **todo** el array de frames, incluidos los de corridas exteriores todavía vivas debajo (§1.8.2). Para el camino del `Throw` directo (`:3462`), `Uncaught()` ya tiene acceso de instancia y puede capturar en el mismo lugar.

**Cambios necesarios.**
- *Surtr.Core (VM)*: nuevo tipo público `readonly struct SurtrStackFrame { SurtrMethodInfo? Method; int BytecodeOffset; }` (quizá `SurtrModule? Module`, derivable del `Chunk.OwningModule`). Nuevas sobrecargas internas de `Uncaught(...)` y un helper `CaptureStack(int maxFrames)` que haga el walk (offset = `frame.IP - frame.CodeBase`; el frame en ejecución ya tiene IP publicado por invariante). Propiedad nueva `IReadOnlyList<SurtrStackFrame>? CallStack` en `SurtrExecutionException`, asignada una sola vez (primera captura gana; idempotente para re-lanzamientos a través de corridas anidadas). Tope de frames configurable (p.ej. 256, análogo a `MaxJavaStackTraceDepth`).
- *Surtr.Core (Runtime)*: opcionalmente un método `FormatCallStack(SurtrExecutionException)` que produzca texto estilo:
  ```
  at Game.Combat.hit(Game/Combat.surtr:118)   <- sin línea hasta propuesta B: Game.Combat.hit+0x2A
  at Game.Main.tick(...)
  ```
  resolviendo el módulo vía `Method.DeclaringType`/chunk. Marcar huecos de frontera nativa como `(native)` es una mejora posterior (hoy no se registra qué segmento entre dos frames bytecode fue nativo).
- *Surtr.Run*: en el `catch` de `Program.cs:88-95`, imprimir el trace formateado además del mensaje.
- *Surtr.Tests*: extender `SurtrVirtualMachineExceptionTests` con asserts de profundidad, orden y offsets.

**Ventajas.** Cero cambios en el bucle de despacho (respeta la regla de oro de la VM); cero coste hasta que existe una excepción (y ya existe una construcción de excepción CLR en ese punto de todas formas); cierra la Fase 6 del `VM-Plan.md:1077-1079`; valor inmediato para hosts aunque no haya imágenes nuevas (funciona con imágenes v11 existentes porque solo usa metadatos que ya viajan); compatible con generadores y corridas anidadas sin trabajo extra.

**Desventajas.** Sin líneas ni ficheros: `Función+offset` es diagnóstico de desarrollador, no de usuario final. No identifica frames nativos intermedios. La captura asigna un array por excepción que escapa (mitigable con el tope de frames; ver C).

**Coste estimado.** 2-4 días incluyendo tests y ajuste de `Surtr.Run`. Riesgo bajo: todo el código nuevo es frío y no toca el hot path.

### Propuesta B — Tabla de line-info por método en la imagen, resuelta a función+línea en el throw (Compiler + Core + Run)

**Descripción.** El compilador emite, por método, una **tabla de puntos de secuencia** (offset absoluto en el chunk → número de línea) y un **nombre de fichero fuente** por módulo. La VM, al capturar el stack (Propuesta A), resuelve cada offset a línea mediante búsqueda en esa tabla, igual que la JVM resuelve bci→línea con `LineNumberTable`. El formato se construye perezosamente: nadie lee las tablas durante la ejecución normal.

**Cambios necesarios.**
- *Surtr.Compiler*:
  - `SurtrCodeEmitter`/`SurtrMethodBuilder`: nueva API `MarkSequencePoint(SurtrLabel, int line)` (o par `(posición, línea)` remapeada en `FinishCode`), invocada desde `MethodBodyEmitter.Statement()` (`MethodBodyEmitter.cs:203`) cuando la línea de `_at` cambia respecto al último punto grabado — granularidad de sentencia, como hacen javac/C#; evita una entrada por instrucción. Usar etiquetas por la relajación de saltos (§1.6), resolviendo offsets en `BuildHandlers`-style (`SurtrMethodBuilder.cs:523-549` es el molde exacto).
  - Codificación compacta estilo Lua `lnotab`/deltas LEB128: pares (deltaOffset, deltaLinea) desde el último punto; típicamente 1-2 bytes por sentencia.
  - `SurtrCompilation`/CLI: nombre de fichero fuente del módulo (ya existe como `sourceName` en binding, `BodyBinder.cs:143`); flag `--no-debug` (o atributo de proyecto) para omitir tablas en releases.
- *Surtr.Core (metadatos)*: `SurtrBytecodeMethodInfo.LineTable` (`int[]` pares ordenados o dos arrays) + setter estilo `SetExceptionHandlers` (`SurtrBytecodeMethodInfo.cs:121`); `SurtrChunk.SourceFileName` o campo equivalente en `SurtrModule`.
- *Surtr.Core (imagen)*: nueva sección por método en `SurtrModuleImageWriter`/`Reader` (contador + bytes codificados) y string internado del fichero a nivel de módulo; **`FormatVersion` 11 → 12** siguiendo la disciplina documentada (`SurtrModuleImage.cs:74-149`): un lector antiguo malinterpretaría los bytes extra, así que se rechaza y se recompila. Documentar en `docs/Module-Format.md`.
- *Surtr.Core (VM)*: resolver línea en la captura (búsqueda binaria sobre la tabla pequeña del método; los métodos suelen tener decenas de puntos). Integrar en el formateador de A: `at Game.Combat.hit(Game/Combat.surtr:118)`.
- *Extras de bajo coste*: el desensamblador (`SurtrBytecodeDisassembler`) puede imprimir `; línea 42` junto a cada punto de secuencia; el LSP podría algún día consumir estos datos para "ir al error de runtime".

**Ventajas.** Solución completa al objetivo (paridad con Java/Python/Lua en calidad de trace); coste de ejecución **cero** (las tablas solo se leen al capturar); el nombre de fichero abre puerta a mejores mensajes de traps actuales (`Index 5 is outside...` podría anteponer `Combat.surtr:118:` como hace `luaL_where`); beneficia también al desensamblado y a futuras herramientas; la emisión condicionada por flag da control de tamaño.

**Desventajas.** Es el cambio más ancho: toca emisor, formato binario (bump de versión → todas las `.surtrc` deben recompilarse), lectores y VM. Incremento de tamaño de imagen estimado +5-15 % según densidad de sentencias (mucho menos con codificación por deltas). Requiere disciplina para mantener los puntos de secuencia correctos frente a optimizaciones futuras del compilador (reordenamientos, inlining ya emitidos en línea: el punto debe quedar en la línea del sitio de llamada, no del callee).

**Coste estimado.** 1-2 semanas: 2-3 días emisor+builder, 2-3 días formato+lectores+versión, 2 días VM+formateo, resto tests y documentación (`Opcodes.md`/`Module-Format.md`/`VM-Plan.md`).

### Propuesta C — Variante perezosa/barata: captura cruda + formateo diferido + política anti-GC (refino de A+B, aplicable sola)

**Descripción.** En lugar de construir strings al capturar, la excepción transporta los datos crudos (array de `SurtrStackFrame`, structs sin strings) y el texto se genera solo cuando alguien llama a `FormatCallStack()`/`ToString()`. Complementos: (1) presupuesto máximo de frames capturados (los primeros N, que son los interesantes); (2) opción de desactivar la captura por runtime (`SurtrRuntime`/machine flag) para hosts que usan excepciones como flujo de control por frame en Unity (patrón catch-relanza cada tick), dejando el comportamiento actual; (3) reutilizar un buffer scratch por máquina para la instantánea cuando el host solo va a formatear inmediatamente.

**Cambios necesarios.** Sobre A: convertir la propiedad `CallStack` en materialización perezosa (guardar `SurtrStackFrame[]` y crear strings en el formateador); flag en constructor del runtime/machine; documentación de la política en `Guia-Interop-Surtr-Csharp.md`.

**Ventajas.** Coste marginal nulo en el happy path y acotado en el de fallo; respeta la prioridad del proyecto de cero asignaciones por instrucción y minimiza presión de GC en el caso pathological de excepciones frecuentes; el host decide su política (editor con trace completo, build de producción sin captura).

**Desventajas.** Una API algo mayor y una decisión de configuración más que explicar; el formateo perezoso obliga a mantener la validez de los `SurtrMethodInfo` referenciados hasta después de `ResetExecution` (los métodos sobreviven al reset — viven en el módulo —, así que la restricción real es solo conservar el array, trivial).

**Coste estimado.** 1-2 días sobre A; casi gratis si se integra desde el principio en A/B.

### Alternativas descartadas

- **Guardar línea por instrucción en un array denso** (Wren `lines[]` exacto): desperdicia memoria frente a puntos de secuencia por sentencia (Surtr ya emite varias instrucciones por sentencia); el acceso binario a la tabla dispersa es suficiente porque solo se consulta en fallos.
- **Usar el stack trace CLR de la excepción** como sustituto: describe los frames internos de `Run()`, inútil y ya pagándose hoy (§1.4); suprimirlo requeriría APIs no disponibles en netstandard2.1.
- **Adjuntar el trace al objeto Surtr elevado** (slot en `Exception`): mezcla diagnóstico con semántica del lenguaje, obliga a tocar el contrato de la clase biblioteca y complica los catch-relanza; mejor vivir en el transporte CLR, como hacen JVM/CLR.

---

## 4. Recomendación final

Implementar **A + C primero, B como segunda fase**:

1. **Fase 1 (A+C, ~1 semana)**: captura del walk de frames en el punto único de `Execute`, transporte crudo perezoso, tope de frames, formateador `módulo::función+offset`, impresión en `Surtr.Run` y flag de opt-out para hosts de alta frecuencia. Es exactamente la Fase 6 ya prevista en `docs/VM-Plan.md:1077-1079`, no rompe imágenes existentes, no toca el hot path (invariante del proyecto: cero coste por instrucción) y entrega valor inmediato de diagnóstico.
2. **Fase 2 (B, ~1-2 semanas)**: puntos de secuencia por sentencia con codificación por deltas, nombre de fichero por módulo, `FormatVersion` 12 con rechazo explícito de versiones viejas, resolución a `archivo:línea` en el formateador y flag `--no-debug` para builds finales. Emitir **por defecto** (como Lua/Wren/AngelScript): el coste es solo tamaño de imagen, y en desarrollo Unity el diagnóstico vale más que el 5-15 % de `.surtrc`; la opción de strip cubre producción.

Esta secuencia reparte riesgo (el cambio de formato binario, que fuerza recompilar todo, queda aislado al final), mantiene intacta la filosofía de rendimiento de la VM — todo el coste nuevo vive en el camino del fallo — y lleva a Surtr a la paridad práctica de traceback con los lenguajes de referencia: quién llamó, en qué función y en qué línea.
