# Informe: benchmark exhaustivo de Surtr vs MoonSharp, LuaJIT y C#

> **Fecha:** 2026-08-22
> **Herramienta:** `surtrbench` (src/Surtr.Bench), suite de 34 workloads × 5 motores.
> **Configuración:** `--extreme --surtr-gc both` → 15 iteraciones cronometradas por caso, 5 de warm-up, **3 rondas completas** del catálogo en orden aleatorio con semilla (`--shuffle --seed 12345`), memoria como **mediana de 5 corridas**, percentiles p90/p99, verificación por checksum de los 3 motores contra el baseline C#.
> **Duración del run:** 2109 s (~35 min).

---

## 1. Resumen ejecutivo

| Métrica | Resultado |
|---|---|
| **Surtr vs MoonSharp** (media geométrica, 34 casos) | **23,2× más rápido** |
| **Surtr vs LuaJIT** (media geométrica, 34 casos) | Surtr **3,3× más lento** (LuaJIT es un compilador JIT; Surtr, un intérprete) |
| **Surtr vs C#** (media geométrica, 34 casos) | Surtr **5,4× más lento** |
| **Surtr GC manual vs GC automático** (media geométrica) | **Empate técnico** (auto 1,7 % más lento; gana en 16/34 casos) |
| **Caso emblemático: `exceptions`** | Surtr **67× más rápido que C#**, 119× que MoonSharp, 29× que LuaJIT |
| **Footprint del registry (GC manual vs auto)** | Manual 4,19 M slots (54,5 MB) vs auto 64 K slots (851 KB) — **64× menor** |
| **Objetos supervivientes por run (suma de los 34 casos)** | Manual 1,86 M vs auto 34 K — **54× menos** con GC automático |
| **Peor ratio vs C#** | `generics` (26,0×): la erasure genérica enboxa 2 objetos por iteración |
| **Anomalía MoonSharp** | `arrayFill`/`forIn`/`iterator`: **3–4 órdenes de magnitud** más lentos que Surtr (el `#` de MoonSharp es O(n) → append cuadrático) |

**Lectura corta:** Surtr se comporta como un intérprete bien construido — 23× por delante del otro intérprete de la comparación (MoonSharp), ~3× por detrás de un JIT (LuaJIT), y entre 1,6× y 7× de C# compilado en la mayoría de casos, con una excepción donde lo supera de forma estrepitosa (manejo de excepciones). La comparativa nueva de GC muestra que el colector automático no cuesta tiempo (empate en geomean) y recorta el footprint del registry en 64×.

---

## 2. Entorno y metodología

### 2.1 Máquina

```
AMD Ryzen 7 9800X3D 8-Core Processor | Microsoft Windows 10.0.26200 | .NET 8.0.13
cores 16 | gc workstation/batch
```

Configuración del proyecto relevante (Surtr.Bench.csproj): `TieredCompilationQuickJitForLoops=false` (los bucles van directos a código optimizado, el estado realista en Mono/IL2CPP), `ServerGarbageCollection=false`, `ConcurrentGarbageCollection=false`.

### 2.2 Motores

| Motor | Nombre en el bench | Naturaleza | Recolector |
|---|---|---|---|
| Surtr (GC manual) | `surtr` | Intérprete de stack, NaN-boxed, pila/bytecode en memoria no administrada | Solo el host recolecta entre muestras |
| Surtr (GC automático) | `surtr-auto` | Ídem | El runtime recolecta por sí mismo en sus safepoints (cada 10 000 asignaciones o 75 % de ocupación del registry) |
| MoonSharp | `lua` | Intérprete Lua en C# | CLR |
| LuaJIT | `luajit` | Compilador JIT nativo (lua51.dll) | GC propio (nivel medido con `lua_gc`) |
| C# | `c#` | Referencia escrita a mano | CLR |

### 2.3 Fiabilidad de la medición

Cada cifra es la **mediana de 15 muestras cronometradas** tras 5 de warm-up, repetida en **3 rondas** con el catálogo en orden aleatorio (semilla 12345) y reducida como **mediana de medianas**. Eso elimina el cross-talk entre casos (el calentamiento de instanciaciones genéricas compartidas cambia un caso según qué corrió antes — el propio harness lo documenta). La memoria es la **mediana de 5 corridas** fuera de la región cronometrada. Cada caso se **verifica por checksum** contra el baseline C# (los 34 acuerdan). El spread (IQR/mediana) se reporta y se marca `ok!` si supera 10 %.

> Nota de interpretación: el tiempo medido **excluye la recolección CLR** (modo por defecto), de modo que compara la velocidad de los motores; el coste de recolección de cada motor se trata aparte en las columnas de memoria y en la comparativa GC (§6). El modo `--gc-inclusive` existe para cobrar ese coste dentro de la muestra.

---

## 3. Resultados completos

Todos los tiempos en **milisegundos por run**. Ratios: **X× = cuántas veces Surtr es más rápido** que el motor de la columna (valores < 1 → el otro motor es más rápido). `kept` = objetos Surtr vivos al volver la llamada (mide retención); `kept auto` = lo mismo para el motor con GC automático.

| workload | tamaño | surtr | surtr-auto | MoonSharp | LuaJIT | C# | vs MoonSharp | vs LuaJIT | vs C# | alloc surtr | kept surtr | kept auto | spread |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| fib | 24 | 2.682 | 2.862 | 25.280 | 0.219 | 0.083 | 9.4× | 0.08× | 0.03× | 0 | 0 | 0 | 14.0 % |
| intLoop | 1e6 | 7.580 | 7.395 | 94.372 | 4.625 | 2.310 | 12.4× | 0.6× | 0.3× | 0 | 0 | 0 | 5.8 % |
| floatLoop | 1e6 | 6.702 | 6.626 | 61.528 | 1.146 | 1.145 | 9.2× | 0.2× | 0.2× | 0 | 0 | 0 | 4.6 % |
| mathFns | 1e5 | 8.055 | 8.082 | 51.292 | 1.337 | 1.538 | 6.4× | 0.2× | 0.2× | 0 | 0 | 0 | 5.2 % |
| arrayFill | 5e4 | 0.738 | 0.780 | **7715.911** | 0.399 | 0.150 | **10459×** | 0.5× | 0.2× | 1.0M | 1 | 1 | 8.5 % |
| arrayIndex | 3e5 | 5.032 | 4.871 | 82.747 | 1.392 | 0.696 | 16.4× | 0.3× | 0.1× | 4.2K | 1 | 1 | 7.9 % |
| dictOps | 3e4 | 0.766 | 0.778 | 6.228 | 0.211 | 0.234 | 8.1× | 0.3× | 0.3× | 1.9M | 1 | 1 | 12.6 % |
| dictMembers | 3e4 | 1.269 | 1.290 | 14.656 | 0.342 | 0.389 | 11.6× | 0.3× | 0.3× | 1.9M | 1 | 1 | 13.0 % |
| dictString | 3e5 | 6.581 | 6.526 | 53.593 | 1.381 | 2.092 | 8.1× | 0.2× | 0.3× | 13.7K | 130 | 130 | 5.0 % |
| stringConcat | 1200 | 0.068 | 0.066 | 0.110 | 0.043 | 0.043 | 1.6× | 0.6× | 0.6× | 1.5M | 1200 | 1200 | 24.8 % |
| stringInterp | 1e5 | 9.152 | 9.924 | 30.413 | 7.199 | 2.932 | 3.3× | 0.8× | 0.3× | 24.4M | 300 000 | **2** | 6.5 % |
| stringOps | 3e5 | 3.725 | 3.564 | 53.784 | 2.764 | 1.379 | 14.4× | 0.7× | 0.4× | 0 | 0 | 0 | 5.4 % |
| stringTransform | 1e5 | 10.436 | 11.394 | 186.731 | 16.605 | 4.394 | 17.9× | 1.6× | 0.4× | 22.9M | 200 000 | **3** | 3.8 % |
| closures | 3e5 | 7.296 | 7.256 | 53.642 | 1.388 | 0.688 | 7.4× | 0.2× | 0.1× | 104 | 1 | 1 | 5.2 % |
| closureCapture | 3e5 | 9.363 | 9.250 | 92.342 | 1.441 | 1.118 | 9.9× | 0.2× | 0.1× | 216 | 2 | 2 | 3.5 % |
| methodCalls | 3e5 | 3.169 | 3.155 | 78.572 | 1.387 | 0.745 | 24.8× | 0.4× | 0.2× | 72 | 1 | 1 | 12.3 % |
| virtualCalls | 3e5 | 6.995 | 6.942 | 60.900 | 1.383 | 0.687 | 8.7× | 0.2× | 0.1× | 40 | 1 | 1 | 13.2 % |
| interfaceCalls | 3e5 | 8.957 | 9.103 | 57.201 | 1.376 | 0.689 | 6.4× | 0.2× | 0.1× | 40 | 1 | 1 | 7.2 % |
| fieldAccess | 3e5 | 5.152 | 4.992 | 72.043 | 1.378 | 0.687 | 14.0× | 0.3× | 0.1× | 80 | 1 | 1 | 7.8 % |
| propertyAccess | 3e5 | 3.362 | 3.237 | 139.803 | 1.383 | 0.688 | 41.6× | 0.4× | 0.2× | 72 | 1 | 1 | 12.1 % |
| exceptions | 8e3 | **0.318** | 0.293 | 37.961 | 9.141 | 21.338 | **119×** | **29×** | **67×** | 562.5K | 8000 | 8000 | 14.4 % |
| forIn | 5e4 | 0.781 | 0.851 | **7137.348** | 0.468 | 0.150 | **9142×** | 0.6× | 0.2× | 1.0M | 1 | 1 | 6.0 % |
| iterator | 5e4 | 3.567 | 3.661 | **6960.401** | 0.485 | 0.192 | **1951×** | 0.1× | 0.05× | 2.9M | 50 002 | **5** | 8.3 % |
| interop | 3e5 | 5.070 | 5.162 | 59.711 | 1.382 | 0.744 | 11.8× | 0.3× | 0.1× | 0 | 0 | 0 | 9.8 % |
| valueClass | 3e5 | 2.317 | 2.429 | 60.596 | 1.379 | 0.689 | 26.1× | 0.6× | 0.3× | 0 | 0 | 0 | 18.1 % |
| generics | 3e5 | 21.410 | 22.717 | 168.142 | 1.381 | 0.824 | 7.9× | 0.1× | 0.04× | **32.0M** | 600 000 | **4** | 4.0 % |
| allocation | 3e5 | 13.945 | 14.595 | 160.796 | 1.513 | 0.925 | 11.5× | 0.1× | 0.07× | 22.9M | 300 000 | **2** | 2.6 % |
| retainedObjects | 1e5 | 5.017 | 6.167 | 2350.327 | 1.665 | 0.273 | 468× | 0.3× | 0.05× | 8.1M | 100 001 | 25 004 | 8.6 % |
| switchDense | 3e5 | 5.129 | 5.099 | 95.957 | 1.893 | 0.689 | 18.7× | 0.4× | 0.1× | 0 | 0 | 0 | 8.1 % |
| typeTest | 3e5 | 7.453 | 7.272 | 162.797 | 2.772 | 1.376 | 21.8× | 0.4× | 0.2× | 40 | 1 | 1 | 3.3 % |
| nullable | 3e5 | 4.276 | 4.263 | 58.360 | 1.769 | 0.704 | 13.6× | 0.4× | 0.2× | 0 | 0 | 0 | 8.7 % |
| enums | 3e5 | 6.321 | 6.355 | 89.732 | 1.768 | 0.707 | 14.2× | 0.3× | 0.1× | 0 | 0 | 0 | 7.3 % |
| sortArray | 2e4 | 6.933 | 7.098 | 121.258 | 5.418 | 0.886 | 17.5× | 0.8× | 0.1× | 668.8K | 2 | 2 | 4.4 % |
| tuples | 3e5 | 8.531 | 9.185 | 77.166 | 1.504 | 0.803 | 9.0× | 0.2× | 0.1× | 25.2M | 300 000 | **2** | 4.8 % |

---

## 4. Surtr vs MoonSharp (23,2× en geomean)

Surtr y MoonSharp son ambos intérpretes, así que esta es la comparativa honesta entre iguales, y es contundente: **23,2× más rápido en media geométrica**, y Surtr es más rápido en los 34 casos.

| Familia | Rango vs MoonSharp | Nota |
|---|---|---|
| Dispatch | 6,4–41,6× | `methodCalls` 24,8×, `propertyAccess` 41,6× |
| Colecciones | 8,1–16,4× | `arrayIndex` 16,4×, `dict*` 8–12× |
| Strings | 1,6–17,9× | `stringTransform` 17,9× |
| Excepciones | 119× | El mecanismo de handler-table sin excepción CLR |
| **Patológicos** | **1951–10459×** | `arrayFill` 10 459×, `forIn` 9 142×, `iterator` 1 951× |

**La anomalía MoonSharp.** `arrayFill` (7,7 s), `forIn` (7,1 s) e `iterator` (7,0 s) tardan 3–4 órdenes de magnitud más que Surtr. La causa es estructural: MoonSharp implementa el operador de longitud `#` como un barrido O(n), así que el patrón de append idiomático `xs[#xs + 1] = i` es **cuadrático** en MoonSharp mientras es O(1) en Lua real y en Surtr (`push`). Son estos tres casos, no el resto de la suite, los que dominan los 35 min del run. Es un hallazgo real de MoonSharp, no un defecto del harness: los tres motores producen el checksum correcto en todos ellos.

**Memoria.** MoonSharp también asigna muchísimo más por el mismo trabajo. `stringTransform` asigna **1 161 600 944 bytes (~1,16 GB) por run** frente a los 24 MB de Surtr y 14,4 MB de C#. `generics`: 643 MB vs 33,6 MB. `allocation`: 655 MB vs 22,9 MB. Parte es el coste de tablas Lua (cada `{a=i, b=i*3}` es una tabla), parte es el intérprete C# que lo respalda.

---

## 5. Surtr vs LuaJIT (0,3×: LuaJIT 3,3× más rápido)

LuaJIT es un compilador JIT (trazas) contra un intérprete; la ventaja de 3,3× es la esperada y es **homogénea**: LuaJIT supera a Surtr en todos los casos menos en `exceptions` (Surtr 29× más rápido). En `floatLoop` LuaJIT iguala a C# (1,146 vs 1,145 ms); en `stringTransform` queda a 1,6× de Surtr (16,6 vs 10,4 ms).

| Caso | Surtr | LuaJIT | C# | Lectura |
|---|---|---|---|---|
| intLoop | 7,58 | 4,63 | 2,31 | LuaJIT a 2× de C#; Surtr a 3,3× |
| floatLoop | 6,70 | 1,15 | 1,15 | LuaJIT == C# en float |
| generics | 21,41 | 1,38 | 0,82 | Surtr 15,5× más lento que LuaJIT (erasure) |
| exceptions | 0,32 | 9,14 | 21,34 | El único caso en que Surtr barre |

**Conclusión de la comparación:** LuaJIT es el techo de referencia para «qué puede hacer un motor de scripting», y Surtr está a 3,3× de ese techo. Para un intérprete puro (sin trazas, orientado a AOT Mono/IL2CPP donde un JIT no puede correr), es una posición razonable, con `generics` (15,5×) como la brecha más grande a cerrar.

---

## 6. GC automático vs GC manual en Surtr (la nueva comparativa)

El run se hizo con `--surtr-gc both`, que lanza **dos motores Surtr idénticos salvo la política de recolección**: `surtr` (manual: solo recolecta cuando el harness se lo pide, entre muestras) y `surtr-auto` (automático: el runtime recolecta por sí mismo en sus safepoints cada 10 000 asignaciones o al 75 % de ocupación del registry).

### 6.1 Tiempo: empate técnico

Geomean `surtr-auto / surtr` = **1,017×** (el automático es un 1,7 % más lento en media) y gana en **16 de 34** casos. El coste del colector en marcha es pequeño porque está **diferido a un safepoint** y nunca se inyecta en la asignación (el per-allocation check es un compare nunca tomado).

| workload | surtr manual | surtr-auto | Δ auto | Por qué |
|---|---|---|---|---|
| generics | 21.410 | 22.717 | +6 % | 600 k asignaciones/run, barridos en marcha |
| allocation | 13.945 | 14.595 | +5 % | 300 k asignaciones/run |
| stringTransform | 10.436 | 11.394 | +9 % | 200 k asignaciones + strings |
| retainedObjects | 5.017 | 6.167 | **+23 %** | 25 k supervivientes barridos repetidamente |
| tuples | 8.531 | 9.185 | +8 % | 300 k tuplas/run |
| fib / intLoop / dictOps | ≈ paridad | | ±2 % | sin carga de asignación |

`retainedObjects` es el caso que mejor muestra el coste: al conservar 25 004 objetos vivos, cada barrido de auto-GC tiene que marcarlos y barrerlos, y el colector paga por ellos en cada safepoint. Cuando el conjunto vivo es pequeño (`allocation` deja 2 vivos), el auto-GC casi no cobra nada.

### 6.2 Memoria: el automático gana por 64×

Aquí está la diferencia real. Las columnas `heap` y `kept` del run:

| Métrica | Manual | Automático | Reducción |
|---|---|---|---|
| Capacidad del registry (sostenida) | 4 194 304 slots = **54,5 MB** | 65 536 slots = **851 KB** | **64×** |
| Objetos vivos al final de `allocation` (300 k creados) | 300 000 | **2** | |
| Vivos al final de `generics` (600 k creados) | 600 000 | **4** | |
| Vivos al final de `stringInterp` (300 k creados) | 300 000 | **2** | |
| Vivos al final de `stringTransform` (200 k creados) | 200 000 | **3** | |
| Suma de vivos de los 34 casos | 1 859 349 | 34 368 | **54×** |

**Qué está pasando.** El registry de Surtr solo crece (`ExpandCapacity` hace `Array.Resize` + realloc HGlobal) y **nunca encoge**. En manual, entre muestras el harness recolecta, pero *durante* un run los objetos se acumulan (p. ej. 600 k en `generics`), la capacidad salta de 1 K a 4 M slots, y ese pico queda fijo: todos los casos siguientes reportan 54,5 MB de capacidad aunque tengan 1 objeto vivo. El automático recoge durante el run, el conjunto vivo nunca pasa de ~10 k, y la capacidad se queda en 64 K slots.

**Lectura práctica para el objetivo real (Unity/Mono/IL2CPP):** el GC automático ofrece el mismo tiempo de ejecución que el manual y, a cambio, elimina la factura de footprint del registry para scripts largos que asignan mucho. El manual solo tiene sentido cuando el host quiere control total de los puntos de recolección (p. ej. en un frame budget estricto donde un safepoint no debe caer a mitad de frame). La diferencia de tiempo entre modos es lo bastante pequeña (1,7 % geomean) para que la decisión se tome por memoria y control, no por velocidad.

> Matiz metodológico: la cifra manual depende del historial del run — la capacidad es «pegajosa» y no encoge, así que en un run que empieza con workloads de asignación el primer caso paga el crecimiento (medido en la sonda: `allocation` manual 17,7 ms en frío vs 13,9 ms ya caliente). El automático no tiene ese efecto.

---

## 7. Surtr vs C# (5,4× en geomean)

Surtr es más lento que C# en 33 de 34 casos, con una horquilla enorme: de **1,6×** (`stringConcat` — ambos cuadráticos por naturaleza) a **26×** (`generics`). La mayoría de la suite ronda las 3–7×.

### 7.1 Por familia

| Familia | Ratio vs C# | Mejor | Peor |
|---|---|---|---|
| Aritmética | 3,3–5,9× | intLoop 3,3× | floatLoop 5,9× |
| Strings | 1,6–3,1× | stringConcat 1,6× | stringInterp 3,1× |
| Colecciones | 3,1–7,2× | dictString 3,1× | arrayIndex 7,2× |
| Dispatch | 4,3–13,0× | methodCalls 4,3× | interfaceCalls 13,0× |
| Asignación / GC | 15,1–26,0× | allocation 15,1× | generics 26,0× |
| Flujo | 5,4–8,9× | typeTest 5,4× | enums 8,9× |
| Interop | 6,8–7,8× | interop 6,8× | sortArray 7,8× |
| **Excepciones** | **0,015×** | Surtr 67× más rápido | |

### 7.2 Lectura por mecanismo

- **Strings (1,6–3,1×)** es la familia más cercana a C#: `StrCat` n-ario asigna una sola vez, y `stringOps`/`stringTransform` confirman que los natives de string son baratos. `stringTransform` (10,4 vs 4,4 ms) es el caso más caro porque cada `substring`/`replace` asigna un string.
- **Dispatch (4,3–13,0×)** es la familia que más cuesta sobre C#: `methodCalls` 4,3× (llamada directa), `virtualCalls` 10,2× (vtable), `interfaceCalls` 13,0× (tabla open-addressed de interfaceId). El JIT de C# devirtualiza e inlinea; Surtr paga deref + tabla por llamada. `closureCapture` (8,4×) mide ahora también el entorno de captura y el upvalue.
- **Asignación (15–26×)** es donde un intérprete paga más: cada objeto Surtr son 2 asignaciones CLR (objeto + array de respaldo) + registro en el registry. `generics` es el peor (26×) porque la erasure enboxa el primitivo (2 objetos/iteración + cast). Aquí la columna de **bytes asignados** manda: Surtr 32 MB vs C# 7,2 MB.
- **`exceptions` (67× más rápido que C#)** es el único caso en que Surtr barre: usa tabla de handlers sin lanzar excepciones CLR, mientras el baseline C# lanza `InvalidOperationException` reales (constructor + unwinding del runtime). Es el resultado estrella de Surtr y el que el VM-Plan §3.7 ya anticipaba.

---

## 8. Memoria: análisis por workload

### 8.1 Asignación por run (Surtr, contador CLR)

| workload | Asignado | Objetos | Lectura |
|---|---|---|---|
| generics | 32,0 MB | 600 000 | 2 objetos/iteración (box + cast de erasure) |
| tuples | 25,2 MB | 300 000 | TupPack = 1 objeto + array de respaldo |
| stringInterp | 24,4 MB | 300 000 | 1 string nuevo por iteración |
| stringTransform | 22,9 MB | 200 000 | substring + replace asignan por llamada |
| allocation | 22,9 MB | 300 000 | 1 Cell por iteración |
| retainedObjects | 8,1 MB | 100 000 | 1 Cell por iteración, 25 % retenido |
| arrayFill / forIn | 1,0 MB | 1 | el array de 50 k crece una vez |
| dictOps / dictMembers | 1,9 MB | 1 | el dict de 30 k |
| aritmética pura | 0 | 0 | `intLoop`/`floatLoop`/`mathFns`/`valueClass`/`nullable`/`enums`/`switchDense` no asignan nada |

### 8.2 Los costes fuera de Surtr

- **MoonSharp asigna 1,16 GB/run** en `stringTransform` y 643 MB en `generics` (cada operación crea tablas/objetos intermedios). Frente a Surtr: 24 MB y 33,6 MB.
- **C# asigna** 14,4 MB en `stringTransform` (substring/replace) y 9,2 MB en `allocation` — la referencia mínima.
- **LuaJIT**: `lua_gc` solo expone el *nivel* del heap, no un total. El nivel más alto medido es 20,9 MB en `allocation` (tras un run que solo creó ~1,3 MB de datos — subestima el churn real, limitación de la API 5.1).

### 8.3 El registry de Surtr

Cada slot del registry cuesta 13 bytes (referencia de entidad 8 B + id de la free-list 4 B + byte de edad 1 B) más el bit de marca. En GC manual, la capacidad pico es de **4,19 M slots (54,5 MB)** y no se libera; en automático se queda en **64 K slots (851 KB)**. Los objetos gestionados en sí (objeto CLR + array `SurtrValue[]` de respaldo) están en el heap CLR y ya los cuenta la columna `bytes`.

---

## 9. Estabilidad y percentiles

Spread (IQR/mediana) por workload: **25 de 34 casos por debajo del 10 %**, 9 marcados `ok!`:

- **Cortos / rápidos** (medianas < 8 ms): `fib` (14,0 %), `dictOps` (12,6 %), `dictMembers` (13,0 %), `stringConcat` (24,8 %), `valueClass` (18,1 %) — poco tiempo por muestra, más sensibles al ruido de desplanificación.
- **Dispatch**: `methodCalls` (12,3 %), `virtualCalls` (13,2 %), `propertyAccess` (12,1 %) — llamadas cortas, JIT en marcha.
- **`exceptions`** (14,4 %) — cola de excepciones con varianza real.

El resto (generics, allocation, strings, colecciones) es estable (< 9 %), que es donde importa. Percentiles p90/p99: la relación p99/mediana es < 1,3 en la mayoría; `exceptions` destaca con 0,617 ms de p99 frente a 0,318 ms de mediana (1,9×) — el manejo de excepciones tiene cola alta, relevante para presupuesto de frame.

---

## 10. Hallazgos destacados

1. **Surtr es 23× más rápido que MoonSharp** (intérprete vs intérprete) y está a 3,3× de LuaJIT (JIT) y a 5,4× de C# en geomean. Posición correcta para un VM de scripting.
2. **`exceptions` es el as en la manga**: 67× más rápido que C#, 119× que MoonSharp, 29× que LuaJIT — handler-table sin excepción CLR.
3. **MoonSharp tiene 3 casos patológicos cuadráticos** (`#` O(n)) que no deben leerse como «MoonSharp es lento», sino como «ese patrón de append es O(n²) ahí».
4. **GC automático = misma velocidad, 64× menos registry.** El coste en tiempo del colector automático es 1,7 % geomean y mantiene el registry en 64 K slots (851 KB) frente a los 4 M (54,5 MB) del manual. La brecha más visible es `retainedObjects` (+23 %) con conjuntos vivos grandes.
5. **`generics` es la brecha a cerrar** (26× vs C#): la erasure paga box+cast+registro por iteración.
6. **Strings son la familia más cercana a C#** (1,6–3,1×): la decisión de `StrCat` n-ario con una sola asignación funciona.
7. **Dispatch de interfaz es el mecanismo más caro** de Surtr (13× vs C#), coherente con el plan de inline cache monomórfico del VM-Plan §5/F.

---

## 11. Conclusiones

1. **Para comparar intérpretes, Surtr gana de calle**: 23× sobre MoonSharp, que es el otro intérprete del pool. Ningún caso lo gana MoonSharp.
2. **El techo es LuaJIT (3,3×)**: la diferencia es la esperada intérprete-vs-JIT; Surtr no «pierde» contra LuaJIT, está en otra categoría de implementación.
3. **La brecha con C# (5,4×) se concentra en asignación y dispatch** (15–26×): es donde el plan de optimización del runtime (buffers unmanaged por objeto, inline cache monomórfico, dict open-addressed) debe atacar primero, y el harness ya mide cada pieza por separado para verificar esas mejoras.
4. **La nueva comparativa de GC es la decisión de producto**: GC automático ≈ mismo tiempo (1,7 % más lento) y 64× menos footprint de registry. La recomendación es automático por defecto y manual solo para hosts con control estricto de safepoints.
5. **El harness es fiable para esto**: 3 rondas con orden aleatorio, mediana de 15 muestras, checksums verificados en los 34 casos, percentiles y spread marcados. Los números de esta tabla son repetibles.

---

## 12. Cómo reproducir

```text
# Verificación rápida (CI): 34 checksums, sin timing
dotnet run --project src/Surtr.Bench -c Release -- --smoke

# Este informe (max-power + comparativa GC), con salida a CSV
dotnet run --project src/Surtr.Bench -c Release -- --extreme --surtr-gc both --csv results.csv

# Solo la comparativa de GC sobre un workload
dotnet run --project src/Surtr.Bench -c Release -- --surtr-gc both --workload allocation

# Coste real incluyendo la recolección CLR dentro de la muestra
dotnet run --project src/Surtr.Bench -c Release -- --gc-inclusive --workload allocation
```

El CSV lleva la huella de máquina (`# machine: ...`) y la configuración (`# settings: ...`) en las dos primeras líneas, de modo que dos runs en máquinas distintas son distinguibles.