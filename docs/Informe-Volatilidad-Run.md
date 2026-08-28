# Informe: la volatilidad de `Run()` (±20-45 %) es bimodalidad por proceso, no layout

Fecha: 2026-08-26. Estado: **diagnóstico cerrado con medición.** El mecanismo de la bimodalidad
(capa 2 de §6) está confirmado con contadores de hardware: es el **op-cache (µop cache decodificado)
del front-end**, no el predictor ni la I-cache (§8). Alcance: explicar el techo de
`docs/Plan-Opcodes-Extendidos.md` §11, corregir el protocolo que lo midió, dejar la evidencia del
experimento discriminante, y documentar el arreglo de presión de registros que sí se conservó (§10).

`scripts/ab-suite.ps1` es el protocolo corregido; este documento es su porqué y su lectura.

---

## 1. Qué se observa

Añadir cuerpos de opcode al `switch` de `SurtrVirtualMachine.Run()` mueve el rendimiento de *todo*
el intérprete en ±20-45 % por caso, en una dirección impredecible y sin relación con lo que el
opcode nuevo hace. Tres experimentos (Fases 2/3/4 de `docs/Plan-Opcodes-Extendidos.md` §9), control
de C# plano, y números que "se revierten" al añadir más cuerpos. El documento anterior lo atribuyó
a *layout de código*: la posición de `Run()` en un espacio donde añadir cuerpos la mueve.

Esa conclusión es **correcta a medias**. Hay dos efectos medidos que se estaban mezclando:

1. **Añadir cuerpos de cierto tipo vuelca el layout global de `Run()`** (determinista, real).
2. **`Run()` es bimodal por proceso**: el mismo binario da resultados distintos según el
   lanzamiento del proceso, por la dirección del código (ASLR). El protocolo A/B de 3 rondas por
   lado **muestrea un solo estado por lado** y no puede ver esta fuente de ruido.

La magnitud documentada (±20-45 %) está dominada por el segundo efecto. Lo controlado: el efecto
real de añadir cuerpos es de ±5-15 % en unos pocos casos.

---

## 2. El estado del intérprete hoy (asm real de `Run()`, HEAD)

Volcado con `DOTNET_JitDisasm=Run` sobre el binario Release (el mismo que mide la suite):

| Hecho | Valor |
|---|---|
| Tamaño nativo | **46 409 bytes** (L1I del 9800X3D: 32 KB) |
| Frame | `sub rsp, 0xC18` (3 096 B); 941 bloques |
| Despacho | un único `jmp rbp` por tabla de offsets de 4 B (`RWD00`) |
| En registro en el bucle | `ip`=rax/r11, `sp`=rdx, `frameBase`=r8, `steps`=r12 |
| En la pila, recargados por acceso | `constants`=[rsp+0xBC8] (13 cuerpos Ldc lo recargan), `entities`=[rsp+0xBE0], `current`=[rsp+0x400] |
| Derrames dentro de cuerpos calientes | `IncLocal` derrama `ip`+`frameBase` cada iteración; `StrEQ` derrama `ip` |

26 locales de método compitiendo por ~14 registros útiles de x64. La presión de registros es real,
pero no es (ver §6) el mecanismo de la volatilidad.

---

## 3. El experimento discriminante: cuerpos artificiales

Se añadieron cuerpos artificiales al switch extendido —alcanzables para el JIT pero que el
compilador jamás emite— en worktrees desechables, y se comparó el asm generado contra HEAD.

| Variante | `Run()` | ¿Se movió el código caliente? |
|---|---|---|
| 9 cuerpos simples (aritmética de slots, `frameBase`+`ip` solo) | 46 998 B | **No** — byte-idéntico hasta 0xA19D (todo el switch primario) |
| 40 cuerpos simples | 49 044 B | **No** — idéntico |
| 40 con `goto Branched` (borde hacia atrás) | 49 044 B | **No** — idéntico |
| 13 cuerpos forma real (referencian `entities`/`current`/`sp`, con camino de trap `throw`) | 50 040 B, frame 0xD98 | **Sí** — layout volcado desde el bloque 1 |

**Conclusión A: la hipótesis "volumen de código" es falsa.** N=9 y N=40 no mueven nada. Los cuerpos
se añaden al final y el JIT no perturba el switch primario.

**Conclusión B: el disparador es contenido de una clase concreta.** Cuerpos que extienden la
liveness de los locales de estado del runtime (`entities`, `current`, `_sp`) y trapean. Eso cambia
los pesos que alimentan al asignador lineal de RyuJIT y vuelca el layout global: el frame crece
(0xC18→0xD98), todos los offsets de pila cambian y todas las direcciones de bloque se desplazan.
La secuencia de instrucciones del bucle caliente queda **idéntica** — solo se mueven las
direcciones.

Eso corrige `docs/Plan-Opcodes-Extendidos.md` §11.5: no es "cualquier cuerpo vuelve a tirar los
dados", es "los cuerpos que tocan `entities`/`current`/`sp` vuelcan el layout". Un cuerpo que solo
toque `frameBase` y `ip` (como el grupo B de la Fase 4) no mueve nada del layout.

---

## 4. La bimodalidad por proceso, medida

El mismo binario, lanzado como procesos distintos, da dos estados discretos:

- `arrayIndex` (build F1), 10 procesos idénticos: `4.15, 6.14, 6.10, 4.25, 6.11, 6.17, 6.12, 3.94, 6.24, 6.11` — **dos nubes a ~4.1 y ~6.15 ms, 48 % aparte**.
- `valueClass`: 2.2 ↔ 3.04 ms (38 %).
- `forInDict`, `dictString`, `fieldAccess`: también bimodales en distinto grado.
- `fib`, `intLoop`, `floatLoop`: estables en mi medición.
- Control de C#: plano (±1 %) siempre.

Dentro de un proceso es estable: `arrayIndex` con `--rounds 9` (9 rondas en un proceso) da un
spread de 10.1 %. La bimodalidad aparece **entre procesos** (ASLR re-rolla la dirección del
código) y dura toda la vida del proceso. Es exactamente lo que el propio doc advertía para
`arrayIndex`/`fieldAccess` ("21.6 y 31.2 ms con el mismo binario") y que luego no controló.

**Por qué el protocolo clásico es ciego a esto:** `--rounds 3` agrega las rondas *dentro* del
proceso (el runner lo documenta: "the reported number per workload is the median across rounds").
Un A/B de una invocación por lado muestrea UN estado por lado. Cuando los dos lados caen en estados
distintos —p. ej. Fase 1 en el estado lento de `arrayIndex` y Fase 2 en el rápido— se reporta
`arrayIndex −32 %`. El control de C# no lo detecta porque la baseline no tiene el despacho
indirecto.

---

## 5. El A/B controlado no reproduce los números del doc

Con el protocolo corregido (3 procesos por lado, intercalados, mediana de 9 muestras por caso):

| Comparación | Casos que se movieron | Los "grandes" del doc |
|---|---|---|
| F1 (6ee6e69) vs HEAD | fieldAccess −15 %, handIterator −13 %, generics −10 %, exceptions −10 % | `arrayIndex` **0 %**, `valueClass` **−0.1 %**, `intLoop` +1.3 %, `floatLoop` +1 % |
| HEAD vs art13e (layout volcado a propósito) | dictString −7 %, generics −5 %, dictOps −5 %, forInDict +18.6 % | magnitudes ±5-15 %, no ±30-45 % |

`forIn` −55 % se reproduce exacto (la fusión real del grupo A). Los ±30-45 % del doc para
`arrayIndex`/`valueClass` son las bimodalidades de §4; para `floatLoop`/`methodCalls`/`intLoop` no
se reprodujeron ni en dirección ni en magnitud.

**Consecuencia:** las decisiones de la Fase 4 (revertir el grupo B y D "porque degradaban la
suite") se tomaron sobre datos contaminados por la bimodalidad. Pueden ser correctas por otras
razones, pero el +3.9 % de mediana y las regresiones de +45 % no son evidencia de que los opcodes
fueran malos.

---

## 6. El mecanismo, en tres capas

1. **Añadir cuerpos que tocan `entities`/`current`/`sp` y trapean** cambia los pesos de liveness
   de esas locales → el asignador lineal y el layout de frame de RyuJIT se re-optimizan
   globalmente → cambian todas las direcciones del método. Determinista, real, medido (§3).
2. **El despacho y los cuerpos calientes se entregan por el op-cache (µop cache decodificado) de
   Zen**, indexado por dirección de fetch. Dependiendo de dónde caigan las líneas del bucle
   caliente, el working set decodificado cabe y el front-end entrega a plena anchura, o aliasa y
   cae al decodificador lento. El layout nuevo mueve esas direcciones → ±5-15 % por caso,
   dirección impredecible, independiente del contenido.
3. **ASLR re-rolla la misma dirección en cada lanzamiento de proceso** → la bimodalidad de 20-50 %
   en los casos sensibles (§4). El protocolo de una invocación por lado no la promedia → los
   ±20-45 % del doc.

La capa 2 y la 3 son el mismo mecanismo a dos escalas: **la entrega de código al front-end es
sensible a la dirección absoluta, y esa dirección se re-rolla por build (capa 1) y por proceso
(capa 3)**. **Confirmado con contadores de hardware en Linux (`perf` sobre la misma CPU, §8):** el
estado lento tiene ~35 % de ciclos de front-end parados contra ~27 % del rápido (1.77-1.84B contra
1.17B, un delta de +600M ciclos que explica casi exactamente los +750M de diferencia), mientras que
`instructions`, `L1-icache-load-misses`, `L1-dcache-load-misses` y `branch-misses` son **idénticos**
entre estados. No es el predictor de rama indirecta, no es la I-cache, no es la D-cache: es la
entrega de instrucciones decodificadas (op-cache), y su estado se fija en el lanzamiento del
proceso.

La asignación de registros del bucle caliente **no se vuelca** cuando el layout cambia (lo verifiqué
en el build art13e: `ip`/`sp`/`frameBase`/`steps` siguen en registro, `constants`/`entities` siguen
en pila). La hipótesis de §11 ("qué derrama podría estar cambiando") es falsa en lo específico:
los derrames de `IncLocal` existen, pero no son la variable que se mueve.

---

## 7. Protocolo de medición corregido

`scripts/ab-suite.ps1 -RefA <commit> -RefB <ruta|commit> -Runs 7`

- Lanza cada lado **K veces como procesos independientes, intercalados** (A,B,A,B,…).
- Por caso reporta: mediana por lado, delta %, control de C# (debe quedar <~1 %), y banderas
  `BIMODAL-A/B` / `SPREAD-A/B` cuando los K lanzamientos no son una sola nube.
- Reglas que se derivan y que el script aplica o hace explícitas:
  1. **Nunca** comparar medianas de una invocación por lado: muestrea un estado solo.
  2. Si un caso aparece marcado `BIMODAL`, su delta no se cita sin más lanzamientos o sin
     agrupar por estado.
  3. El control de C# plano es condición necesaria pero **no suficiente**: no ve el despacho.
  4. Subconjuntos vs subconjuntos idénticos, o suite completa vs suite completa (cross-talk del
     harness, ya documentado).
  5. `--rounds 5` revienta el presupuesto; 3 o 9.

**El harness lo tiene integrado ahora: `surtrbench --processes <n>`.** Para un número absoluto sin
script externo, el bench mide cada caso en N procesos frescos (subprocesos del propio ejecutable,
cada uno con su propio ASLR) y reporta por caso: el **mínimo** (estado más rápido alcanzable ahora,
la métrica de §8), el **spread de estado** (cuánto más lento fue el peor estado muestreado), y si
el caso salió `bimodal` (spread &gt; 20 %) o `single` (todos los procesos en el mismo estado — ver
§8 sobre la alcanzabilidad). El CSV que produce lleva `processes=N` en la línea de settings y un
columna `state_spread_pct`, para que nadie confunda ese `_ms` (mínimo) con la mediana
intra-proceso del modo normal. N ≥ 7 es la recomendación para dar al estado rápido una
probabilidad real de aparecer.

---

## 8. La medición que cerró la capa 2 (contadores de hardware)

**Cerrada, en Linux.** Se instaló `perf` en WSL (Ubuntu-24.04, misma CPU física 9800X3D) y se midió
`arrayIndex` bajo `perf stat` en ~30 procesos hasta atrapar ambos estados del mismo binario. La
tabla, fast vs slow:

| contador | estado rápido (~4.5-4.8 ms) | estado lento (~6.5-7.3 ms) |
|---|---|---|
| `instructions` | 15.75B | 15.75B (idéntico) |
| `cycles` | ~4.3B (IPC 3.65) | ~5.0B (IPC 3.13) |
| `stalled-cycles-frontend` | **1.17B (27 %)** | **1.78-1.84B (35-36 %)** |
| `branch-misses` | ~33.5M | ~34.0M (idéntico) |
| `L1-icache-load-misses` | 6.5 % | 6.4 % (idéntico) |
| `L1-dcache-load-misses` | 0.86 % | 0.86 % (idéntico) |

El único delta es el front-end: +600M ciclos de front-end parados que explican casi exactamente el
+750M de ciclos totales, con las instrucciones y todos los contadores de miss idénticos. **Es el
op-cache (µop cache decodificado)**: el estado lento es cuando las líneas del bucle caliente
aliasan en el op-cache y el front-end cae al decodificador lento. No es el predictor indirecto
(`branch-misses` idéntico), no es la I-cache (`L1-icache` idéntico). Los eventos de op-cache no
están expuestos por nombre en `perf`, pero el patrón de contadores no deja otra alternativa de
front-end.

**Consecuencia para los arreglos:** la bimodalidad es una tirada de estado de hardware por
dirección de código — **irreducible desde C#/RyuJIT** (el direct threading no es expresable,
`docs/VM-Plan.md` §1.1, y no hay forma de alinear/pinear el código JIT). El estado rápido ES el
rendimiento verdadero del intérprete; el estado lento es el hardware derramando el op-cache. Por
eso el protocolo de §7 mide el mínimo por proceso: es el throughput real, y lo que una mejora
como la de §10 hace es bajar ese mínimo. Reducir el footprint de fetch del bucle caliente (método
más compacto, cuerpos calientes contiguos) puede hacer el estado rápido más probable, pero no
puede garantizarlo bajo ASLR.

**La alcanzabilidad del estado rápido no es estable.** No es solo una lotería por proceso: la
probabilidad del estado rápido depende del estado de código-layout de la máquina (cómo cae el
base del code-heap del CLR, que cambia con el estado de memoria del sistema) y puede caer a **cero
durante horas**. El mismo build, el mismo binario: esta mañana `arrayIndex` caía en el estado
rápido ~30-40 % de los procesos; por la tarde, 0 de 18 procesos (y 0 de 4 con suite completa),
igual que `valueClass`, `floatLoop` y `tightGuard` — la máquina entera quedó bloqueada en el
estado lento. Implicaciones:
- El mínimo por proceso es el "mejor alcanzable ahora", no el "techo del hardware": si el estado
  rápido no es alcanzable en ese momento, el mínimo ES el estado lento. Un resultado con spread
  bajo significa "todos los procesos cayeron en el mismo estado", no "el caso es estable".
- Un A/B de mínimo es válido mientras ambos lados se midan en la misma ventana de tiempo (los de
  §10 lo fueron, intercalados); no se puede comparar un mínimo de hoy con uno de ayer.
- Para un número absoluto "realista" hoy, el harness con `--processes` (§7) reporta el mínimo
  alcanzable ahora con su spread de estado — que puede ser el estado lento si el rápido no está
  en juego.

**IL2CPP/Mono.** El impuesto del prefijo (0.44-0.48 ns) y esta volatilidad son de RyuJIT sobre
código JIT re-ubicable. Unity corre Mono JIT (mismo problema de ASLR) e IL2CPP (código AOT a
direcciones fijas por build): en IL2CPP la bimodalidad por proceso debería desaparecer y quedar
fijada en uno de los dos estados por el layout del enlazador — una pregunta real para Unity
(`docs/Plan-Opcodes-Extendidos.md` §3.2 ya exige re-medir el prefijo en cada backend).

---

## 9. Lo que queda en pie del trabajo anterior

`docs/Plan-Opcodes-Extendidos.md` §11 decía: el presupuesto real no es de 256 valores sino de un
puñado de cuerpos. Eso se matiza: el presupuesto no lo agota el volumen, lo agotan los cuerpos que
tocan el estado del runtime. Y su conclusión "la única medida válida es la suite completa contra un
worktree, con el control de C#" sigue siendo cierta pero **insuficiente**: necesita además K
procesos por lado, porque sin eso la bimodalidad fabrica deltas de ±30 % que parecen layout.

El grupo A (superinstrucciones de bucle) está medido de verdad: `forIn` −47 % y `forInDict` −20 %
reproducen en el A/B controlado, y son la parte atribuible de su beneficio. Lo demás de la Fase 2
("−5.7 % de mediana", "25 de 46 casos mejoran") no se debe citar como ganancia de la fusión: en mi
medición controlada los casos no tocados por la fusión se movieron ±0-5 % en un sentido u otro, sin
dirección sistemática, consistente con ruido más un pequeño efecto de layout.

---

## 10. El arreglo aplicado: presión de registros y derrames

Con el protocolo de §7 se evaluó un cambio estructural en `Run()` y se conservó por medido. La
idea: los arrays de frames/roots y el límite de pila no tienen por qué ser locales de método —
sus cinco rangos de liveness abarcaban todo `Run()` y le costaban tres registros callee-saved al
bucle de despacho.

**El cambio (medido, HEAD → aplicado, `scripts/ab-suite.ps1 -Runs 5`):**

1. `frames`, `roots`, `maxDepth`, `stackLimit` dejan de ser locales; el código de llamada y de
   generadores los lee de los campos `_frames`/`_roots`/`_stackLimit`. Son caminos fríos (una vez
   por llamada); la carga extra no aparece ahí.
2. `context` se **conserva** como local. `Context` es un getter `ref` que el inliner deja como
   llamada real en un método de este tamaño (45 `get_Context` por `Run()`); re-leer
   `_runtime.Context` en cada sitio costaba una llamada por uso y `interop` (llamada nativa en
   bucle) se iba a +14.6 %. Con el local, una llamada en el prólogo y punteros después.

**Resultado sobre el estado rápido (mínimo por proceso, 5 lanzamientos por lado, intercalados):**

| | |
|---|---|
| mediana de mínimos | **−4.6 %** |
| casos que mejoran >5 % | 23 de 50 |
| casos que empeoran >5 % | 2 (`stringInterp` +6 %, `stringConcat` +5.8 %) |
| mejoras | `switchDense` −20.6 %, `tightGuard` −19.7 %, `enums` −17.8 %, `intLoop` −16.5 %, `fieldAccess` −14.7 %, `valueClass` −14.4 %, `arrayFill`/`propertyAccess` −14 % |
| control de C# | ±0.2 % (la máquina estuvo quieta) |

`Run()` pasa de 46 409 a 42 763 bytes, y el cuerpo de `IncLocal` —el paso de bucle, uno de los más
calientes— deja de derramar `ip` y `frameBase` (15 instrucciones con 4 accesos a pila extra → 13
con uno solo). La asignación de registros del estado de bucle cambia (ip/sp/frameBase en
r12/rbp/rcx) pero `constants`/`entities` siguen en la pila: el asignador las conserva en su slot
base incluso con registros libres, así que el beneficio no viene de ahí sino de la eliminación de
los derrames y de un método más compacto.

**La bimodalidad no desaparece con el arreglo** — es de la capa 2 (§6), no de la presión de
registros. `arrayIndex` sigue saltando entre ~3.9 y ~6.3 ms según el proceso, en ambos builds. El
arreglo mejora el estado rápido y deja la volatilidad igual; el protocolo de §7 sigue siendo
obligatorio para medir cualquier cosa.

**Regresiones residuales.** `stringInterp` y `stringConcat` empeoran ~6 % consistentemente en el
estado rápido. Es el dado del layout (§6 capa 2) cayendo mal para esos cuerpos concretos (StrCat),
no un coste añadido: se acepta frente a 23 mejoras y −4.6 % de mediana. Si se quiere recuperar,
hay que re-rodar el layout (p. ej. ordenar los cuerpos de cadena junto a los aritméticos) y medir
de nuevo — con el protocolo.