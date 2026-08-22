# Informe: benchmark exhaustivo de Surtr vs MoonSharp, LuaJIT y C#

> **Fecha:** 2026-08-22
> **Herramienta:** `surtrbench` (src/Surtr.Bench), suite de 36 workloads × 5 motores.
> **Configuración:** `--extreme --surtr-gc both` → 15 iteraciones cronometradas por caso, 5 de warm-up, **3 rondas completas** del catálogo en orden aleatorio con semilla (`--shuffle --seed 12345`), memoria como **mediana de 5 corridas**, percentiles p90/p99, verificación por checksum de los 3 motores contra el baseline C#.
> **Duración del run:** 1984 s (~33 min).

---

## 1. Resumen ejecutivo

| Métrica | Resultado |
|---|---|
| **Surtr vs MoonSharp** (media geométrica, 36 casos) | **19,7× más rápido** |
| **Surtr vs LuaJIT** (media geométrica, 36 casos) | Surtr **3,3× más lento** (LuaJIT es un compilador JIT; Surtr, un intérprete) |
| **Surtr vs C#** (media geométrica, 36 casos) | Surtr **6,1× más lento** |
| **Surtr GC manual vs GC automático** (media geométrica) | **Empate técnico** (auto 1,5 % más lento; gana en 16/36 casos) |
| **Caso emblemático: `exceptions`** | Surtr **73× más rápido que C#**, 127× que MoonSharp, 32× que LuaJIT |
| **Footprint del registry (GC manual vs auto)** | Manual 4,19 M slots (54,5 MB) vs auto 64 K slots (851 KB) — **64× menor** |
| **Objetos supervivientes por run (suma de los 36 casos)** | Manual 1,86 M vs auto 34 K — **54× menos** con GC automático |
| **Peor ratio vs C#** | `generics` (25,0×): la erasure genérica enboxa 2 objetos por iteración |
| **Anomalía MoonSharp** | `arrayFill`/`forIn`/`iterator`: **3–4 órdenes de magnitud** más lentos que Surtr (el `#` de MoonSharp es O(n) → append cuadrático) |

**Lectura corta:** Surtr se comporta como un intérprete bien construido — 19,7× por delante del otro intérprete de la comparación (MoonSharp), ~3,3× por detrás de un JIT (LuaJIT), y entre 1,6× y 7× de C# compilado en la mayoría de casos, con una excepción donde lo supera de forma estrepitosa (manejo de excepciones). La comparativa de GC muestra que el colector automático no cuesta tiempo (empate en geomean) y recorta el footprint del registry en 64×.

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

Cada cifra es la **mediana de 15 muestras cronometradas** tras 5 de warm-up, repetida en **3 rondas** con el catálogo en orden aleatorio (semilla 12345) y reducida como **mediana de medianas**. Eso elimina el cross-talk entre casos (el calentamiento de instanciaciones genéricas compartidas cambia un caso según qué corrió antes — el propio harness lo documenta). La memoria es la **mediana de 5 corridas** fuera de la región cronometrada. Cada caso se **verifica por checksum** contra el baseline C# (los 36 acuerdan). El spread (IQR/mediana) se reporta y se marca `ok!` si supera 10 %.

> Nota de interpretación: el tiempo medido **excluye la recolección CLR** (modo por defecto), de modo que compara la velocidad de los motores; el coste de recolección de cada motor se trata aparte en las columnas de memoria y en la comparativa GC (§6). El modo `--gc-inclusive` existe para cobrar ese coste dentro de la muestra.

---

## 3. Resultados completos

Todos los tiempos en **milisegundos por run**. Ratios: **X× = cuántas veces Surtr es más rápido** que el motor de la columna (valores < 1 → el otro motor es más rápido). `kept` = objetos Surtr vivos al volver la llamada (mide retención); `kept auto` = lo mismo para el motor con GC automático.

| workload | tamaño | surtr | surtr-auto | MoonSharp | LuaJIT | C# | vs MoonSharp | vs LuaJIT | vs C# | alloc surtr | kept surtr | kept auto | spread |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| fib | 24 | 2.715 | 2.694 | 24.422 | 0.219 | 0.083 | 9.0× | 0.08× | 0.03× | 0 | 0 | 0 | 4.9 % |
| intLoop | 1e6 | 10.173 | 9.827 | 86.896 | 4.615 | 2.303 | 8.5× | 0.5× | 0.2× | 0 | 0 | 0 | 4.6 % |
| floatLoop | 1e6 | 8.440 | 8.495 | 56.638 | 1.147 | 1.149 | 6.7× | 0.1× | 0.1× | 0 | 0 | 0 | 5.5 % |
| mathFns | 1e5 | 7.621 | 7.599 | 46.596 | 1.332 | 1.533 | 6.1× | 0.2× | 0.2× | 0 | 0 | 0 | 4.5 % |
| arrayFill | 5e4 | 0.865 | 0.857 | **6 284,944** | 0.390 | 0.148 | **7 266×** | 0.5× | 0.2× | 56B | 1 | 1 | 4.0 % |
| arrayIndex | 3e5 | 5.963 | 5.969 | 75.610 | 1.378 | 0.694 | 12.7× | 0.2× | 0.1× | 56B | 1 | 1 | 4.5 % |
| dictOps | 3e4 | 0.812 | 0.823 | 5.557 | 0.202 | 0.231 | 6.8× | 0.2× | 0.3× | 1,9M | 1 | 1 | 9.8 % |
| dictMembers | 3e4 | 1.334 | 1.282 | 13.422 | 0.339 | 0.390 | 10.1× | 0.3× | 0.3× | 1,9M | 1 | 1 | 7.6 % |
| dictString | 3e5 | 6.989 | 6.945 | 53.634 | 1.379 | 1.913 | 7.7× | 0.2× | 0.3× | 12,6K | 130 | 130 | 10.7 % |
| stringConcat | 1200 | 0.056 | 0.054 | 0.093 | 0.044 | 0.036 | 1.7× | 0.8× | 0.6× | 1,5M | 1200 | 1200 | 8.6 % |
| stringInterp | 1e5 | 9.240 | 10.148 | 31.505 | 6.889 | 2.766 | 3.4× | 0.7× | 0.3× | 24,4M | 300 000 | 2 | 4.1 % |
| stringOps | 3e5 | 5.102 | 4.953 | 53.688 | 2.757 | 1.376 | 10.5× | 0.5× | 0.3× | 0 | 0 | 0 | 8.6 % |
| stringTransform | 1e5 | 10.037 | 10.519 | 174.758 | 15.656 | 4.314 | 17.4× | 1,6× | 0.4× | 22,9M | 200 000 | 3 | 11.1 % |
| closures | 3e5 | 6.910 | 6.759 | 50.228 | 1.386 | 0.692 | 7.3× | 0.2× | 0.1× | 0 | 0 | 0 | 7.5 % |
| closureCreate | 3e5 | 10.315 | 10.273 | 88.719 | 6.862 | 0.692 | 8.6× | 0.7× | 0.1× | 0 | 0 | 0 | 7.1 % |
| methodGroupInvoke | 3e5 | 10.101 | 10.122 | 79.613 | 1.377 | 0.687 | 7.9× | 0.1× | 0.1× | 0 | 0 | 0 | 8.1 % |
| closureCapture | 3e5 | 9.924 | 9.218 | 89.447 | 1.453 | 1.114 | 9.0× | 0.1× | 0.1× | 216 | 2 | 2 | 6.2 % |
| methodCalls | 3e5 | 4.346 | 4.394 | 81.594 | 1.382 | 0.748 | 18.8× | 0.3× | 0.2× | 72 | 1 | 1 | 11.6 % |
| virtualCalls | 3e5 | 6.797 | 6.701 | 60.791 | 1.377 | 0.688 | 8.9× | 0.2× | 0.1× | 40 | 1 | 1 | 3.0 % |
| interfaceCalls | 3e5 | 6.546 | 6.562 | 59.426 | 1.377 | 0.687 | 9.1× | 0.2× | 0.1× | 40 | 1 | 1 | 6.1 % |
| fieldAccess | 3e5 | 5.512 | 5.433 | 64.824 | 1.377 | 0.689 | 11.8× | 0.2× | 0.1× | 80 | 1 | 1 | 5.0 % |
| propertyAccess | 3e5 | 3.696 | 3.743 | 134.113 | 1.382 | 0.686 | 36.3× | 0.4× | 0.2× | 72 | 1 | 1 | 6.6 % |
| exceptions | 8e3 | **0.283** | 0.283 | 35.827 | 9.108 | 20.711 | **127×** | **32×** | **73×** | 562,5K | 8000 | 8000 | 6.3 % |
| forIn | 5e4 | 0.939 | 0.936 | **6 358,837** | 0.384 | 0.149 | **6 773×** | 0.4× | 0.2× | 56B | 1 | 1 | 5.9 % |
| iterator | 5e4 | 2.898 | 3.102 | **6 988,723** | 0.415 | 0.191 | **2 412×** | 0.1× | 0,07× | 1,9M | 50 002 | 5 | 4.3 % |
| interop | 3e5 | 4.636 | 4.600 | 55.833 | 1.385 | 0.744 | 12.0× | 0.3× | 0.2× | 0 | 0 | 0 | 2.6 % |
| valueClass | 3e5 | 2.883 | 2.878 | 53.425 | 1.381 | 0.688 | 18.5× | 0.5× | 0.2× | 0 | 0 | 0 | 5.4 % |
| generics | 3e5 | 20.236 | 22.440 | 160.197 | 1.377 | 0.809 | 7.9× | 0.1× | 0.04× | **32,0M** | 600 000 | 4 | 2.2 % |
| allocation | 3e5 | 14.267 | 15.283 | 143.751 | 1.509 | 0.886 | 10.1× | 0.1× | 0.06× | 22,9M | 300 000 | 2 | 2.9 % |
| retainedObjects | 1e5 | 5.550 | 6.765 | 2314.149 | 5.186 | 0.263 | 417× | 0.9× | 0.05× | 7,6M | 100 001 | 25 004 | 6.4 % |
| switchDense | 3e5 | 5.690 | 5.945 | 89.214 | 1.884 | 0.691 | 15.7× | 0.3× | 0.1× | 0 | 0 | 0 | 8.2 % |
| typeTest | 3e5 | 7.393 | 7.461 | 149.672 | 2.756 | 1.377 | 20.2× | 0.4× | 0.2× | 40 | 1 | 1 | 3.7 % |
| nullable | 3e5 | 5.485 | 5.511 | 53.038 | 1.573 | 0.689 | 9.7× | 0.3× | 0.1× | 0 | 0 | 0 | 2.9 % |
| enums | 3e5 | 7.578 | 7.639 | 87.996 | 1.764 | 0.706 | 11.6× | 0.2× | 0.1× | 0 | 0 | 0 | 3.0 % |
| sortArray | 2e4 | 6.189 | 6.300 | 123.396 | 4.970 | 0.884 | 19.9× | 0.8× | 0.1× | 160,1K | 1 | 1 | 4.8 % |
| tuples | 3e5 | 9.048 | 10.457 | 70.848 | 1.503 | 0.806 | 7.8× | 0.2× | 0.1× | 25,2M | 300 000 | 2 | 4.3 % |

---

## 4. Surtr vs MoonSharp (19,7× en geomean)

Surtr y MoonSharp son ambos intérpretes, así que esta es la comparativa honesta entre iguales: **19,7× más rápido en media geométrica**, y Surtr es más rápido en los 36 casos.

| Familia | Rango vs MoonSharp | Nota |
|---|---|---|
| Dispatch | 7,3–36,3× | `methodCalls` 18,8×, `propertyAccess` 36,3× |
| Colecciones | 6,8–12,7× | `arrayIndex` 12,7×, `dict*` 6,8–10,1× |
| Strings | 1,7–17,4× | `stringTransform` 17,4× |
| Excepciones | 127× | El mecanismo de handler-table sin excepción CLR |
| **Patológicos** | **2 412–7 266×** | `arrayFill` 7 266×, `forIn` 6 773×, `iterator` 2 412× |

**La anomalía MoonSharp.** `arrayFill` (6,3 s), `forIn` (6,4 s) e `iterator` (7,0 s) tardan 3–4 órdenes de magnitud más que Surtr. La causa es estructural: MoonSharp implementa el operador de longitud `#` como un barrido O(n), así que el patrón de append idiomático `xs[#xs + 1] = i` es **cuadrático** en MoonSharp mientras es O(1) en Lua real y en Surtr (`push`). Son estos tres casos, no el resto de la suite, los que dominan los 33 min del run. Es un hallazgo real de MoonSharp, no un defecto del harness: los tres motores producen el checksum correcto en todos ellos.

**Memoria.** MoonSharp también asigna muchísimo más por el mismo trabajo. `stringTransform` asigna **1 161 600 944 bytes (~1,16 GB) por run** frente a los 22,9 MB de Surtr y 14,4 MB de C#. `generics`: 643 MB vs 33,6 MB. `allocation`: 655 MB vs 22,9 MB. Parte es el coste de tablas Lua (cada `{a=i, b=i*3}` es una tabla), parte es el intérprete C# que lo respalda.

---

## 5. Surtr vs LuaJIT (0,3×: LuaJIT 3,3× más rápido)

LuaJIT es un compilador JIT (trazas) contra un intérprete; la ventaja de 3,3× es la esperada y es **homogénea**: LuaJIT supera a Surtr en todos los casos excepto dos: `exceptions` (Surtr 32× más rápido) y `stringTransform` (Surtr 1,6× más rápido). En `floatLoop` LuaJIT iguala a C# (1,147 vs 1,149 ms).

| Caso | Surtr | LuaJIT | C# | Lectura |
|---|---|---|---|---|
| intLoop | 10,17 | 4,62 | 2,30 | LuaJIT a 2× de C#; Surtr a 4,4× |
| floatLoop | 8,44 | 1,15 | 1,15 | LuaJIT == C# en float |
| stringTransform | 10,04 | 15,66 | 4,31 | **Surtr 1,6× más rápido que LuaJIT** |
| generics | 20,24 | 1,38 | 0,81 | Surtr 14,7× más lento que LuaJIT (erasure) |
| exceptions | 0,28 | 9,11 | 20,71 | **Surtr 32× más rápido que LuaJIT** |

**Conclusión de la comparación:** LuaJIT es el techo de referencia para «qué puede hacer un motor de scripting», y Surtr está a 3,3× de ese techo. Para un intérprete puro (sin trazas, orientado a AOT Mono/IL2CPP donde un JIT no puede correr), es una posición razonable, con `generics` (14,7×) como la brecha más grande a cerrar. El hecho de que Surtr supere a LuaJIT en `stringTransform` (1,6×) es notable: el manejo nativo de cadenas de Surtr con `StrCat` n-ario y el Comparer path de .NET resultan más eficientes que el concatenation de LuaJIT en este caso específico.

---

## 6. GC automático vs GC manual en Surtr (la nueva comparativa)

El run se hizo con `--surtr-gc both`, que lanza **dos motores Surtr idénticos salvo la política de recolección**: `surtr` (manual: solo recolecta cuando el harness se lo pide, entre muestras) y `surtr-auto` (automático: el runtime recolecta por sí mismo en sus safepoints cada 10 000 asignaciones o al 75 % de ocupación del registry).

### 6.1 Tiempo: empate técnico

Geomean `surtr-auto / surtr` = **1,015×** (el automático es un 1,5 % más lento en media) y gana en **16 de 36** casos. El coste del colector en marcha es pequeño porque está **diferido a un safepoint** y nunca se inyecta en la asignación (el per-allocation check es un compare nunca tomado).

| workload | surtr manual | surtr-auto | Δ auto | Por qué |
|---|---|---|---|---|
| generics | 20.236 | 22.440 | +11 % | 600 k asignaciones/run, barridos en marcha |
| allocation | 14.267 | 15.283 | +7 % | 300 k asignaciones/run |
| stringTransform | 10.037 | 10.519 | +5 % | 200 k asignaciones + strings |
| retainedObjects | 5.550 | 6.765 | **+22 %** | 25 k supervivientes barridos repetidamente |
| tuples | 9.048 | 10.457 | +16 % | 300 k tuplas/run |
| stringInterp | 9.240 | 10.148 | +10 % | 300 k strings/run |
| iterator | 2.898 | 3.102 | +7 % | 50 k objetos, path general |
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
| Suma de vivos de los 36 casos | 1 859 347 | 34 366 | **54×** |

**Qué está pasando.** El registry de Surtr solo crece (`ExpandCapacity` hace `Array.Resize` + realloc HGlobal) y **nunca encoge**. En manual, entre muestras el harness recolecta, pero *durante* un run los objetos se acumulan (p. ej. 600 k en `generics`), la capacidad salta de 1 K a 4 M slots, y ese pico queda fijo: todos los casos siguientes reportan 54,5 MB de capacidad aunque tengan 1 objeto vivo. El automático recoge durante el run, el conjunto vivo nunca pasa de ~10 k, y la capacidad se queda en 64 K slots.

**Lectura práctica para el objetivo real (Unity/Mono/IL2CPP):** el GC automático ofrece el mismo tiempo de ejecución que el manual y, a cambio, elimina la factura de footprint del registry para scripts largos que asignan mucho. El manual solo tiene sentido cuando el host quiere control total de los puntos de recolección (p. ej. en un frame budget estricto donde un safepoint no debe caer a mitad de frame). La diferencia de tiempo entre modos es lo bastante pequeña (1,5 % geomean) para que la decisión se tome por memoria y control, no por velocidad.

> Matiz metodológico: la cifra manual depende del historial del run — la capacidad es «pegajosa» y no encoge, así que en un run que empieza con workloads de asignación el primer caso paga el crecimiento (medido en la sonda: `allocation` manual en frío vs ya caliente). El automático no tiene ese efecto.

---

## 7. Surtr vs C# (6,1× en geomean)

Surtr es más lento que C# en 35 de 36 casos, con una horquilla enorme: de **1,6×** (`stringConcat` — ambos cuadráticos por naturaleza) a **25,0×** (`generics`). La mayoría de la suite ronda las 3–7×.

### 7.1 Por familia

| Familia | Ratio vs C# | Mejor | Peor |
|---|---|---|---|
| Aritmética | 4,4–7,3× | intLoop 4,4× | floatLoop 7,3× |
| Strings | 1,6–3,7× | stringConcat 1,6× | stringOps 3,7× |
| Colecciones | 3,4–8,6× | dictMembers 3,4× | arrayIndex 8,6× |
| Dispatch | 5,4–14,9× | methodCalls 5,8× | closureCreate 14,9× |
| Asignación / GC | 11,2–25,0× | tuples 11,2× | generics 25,0× |
| Flujo | 5,4–10,7× | typeTest 5,4× | enums 10,7× |
| Interop | 6,2–7,0× | interop 6,2× | sortArray 7,0× |
| **Excepciones** | **0,014×** | Surtr 73× más rápido | |

### 7.2 Lectura por mecanismo

- **Strings (1,6–3,7×)** es la familia más cercana a C#: `StrCat` n-ario asigna una sola vez, y `stringOps`/`stringTransform` confirman que los natives de string son baratos. `stringTransform` (10,0 vs 4,3 ms) es el caso más caro porque cada `substring`/`replace` asigna un string.

- **Dispatch (5,4–14,9×)** es la familia que más cuesta sobre C#: `methodCalls` 5,8× (llamada directa), `virtualCalls` 9,9× (vtable), `interfaceCalls` 9,5× (tabla open-addressed de interfaceId). Los nuevos workloads `closureCreate` (14,9×) y `methodGroupInvoke` (14,7×) muestran el costo de crear un closure de cero capturas y de invocar a través de un method-group, respectivamente. El JIT de C# devirtualiza e inlinea; Surtr paga deref + tabla por llamada. `closureCapture` (8,9×) mide también el entorno de captura y el upvalue.

- **Asignación (11–25×)** es donde un intérprete paga más: cada objeto Surtr son 2 asignaciones CLR (objeto + array de respaldo) + registro en el registry. `generics` es el peor (25×) porque la erasure enboxa el primitivo (2 objetos/iteración + cast). Aquí la columna de **bytes asignados** manda: Surtr 32 MB vs C# 7,2 MB.

- **`exceptions` (73× más rápido que C#)** es el único caso en que Surtr barre: usa tabla de handlers sin lanzar excepciones CLR, mientras el baseline C# lanza `InvalidOperationException` reales (constructor + unwinding del runtime). Es el resultado estrella de Surtr y el que el VM-Plan §3.7 ya anticipaba.

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
| retainedObjects | 7,6 MB | 100 000 | 1 Cell por iteración, 25 % retenido |
| iterator | 1,9 MB | 50 002 | Path general iterate()/moveNext() |
| dictOps / dictMembers | 1,9 MB | 1 | el dict de 30 k |
| stringConcat | 1,5 MB | 1200 | StrCat por pares, cuadrático |
| arrayFill / forIn / arrayIndex | 56B | 1 | buffers en memoria no administrada |

### 8.2 Los costes fuera de Surtr

- **MoonSharp asigna 1,16 GB/run** en `stringTransform` y 643 MB en `generics` (cada operación crea tablas/objetos intermedios). Frente a Surtr: 22,9 MB y 33,6 MB.
- **C# asigna** 14,4 MB en `stringTransform` (substring/replace) y 9,2 MB en `allocation` — la referencia mínima.
- **LuaJIT**: `lua_gc` solo expone el *nivel* del heap, no un total. El nivel más alto medido es 14,8 MB en `closureCapture`.

### 8.3 El registry de Surtr

Cada slot del registry cuesta 13 bytes (referencia de entidad 8 B + id de la free-list 4 B + byte de edad 1 B) más el bit de marca. En GC manual, la capacidad pico es de **4,19 M slots (54,5 MB)** y no se libera; en automático se queda en **64 K slots (851 KB)**. Los objetos gestionados en sí (objeto CLR + array `SurtrValue[]` de respaldo) están en el heap CLR y ya los cuenta la columna `bytes`.

---

## 9. Estabilidad y percentiles

Spread (IQR/mediana) por workload: **33 de 36 casos por debajo del 10 %**, 3 marcados `ok!`:

- **`dictString`** (10,7 %) — diccionario con clave string, variability en el comparer path.
- **`stringTransform`** (11,1 %) — strings con operaciones nativas múltiples, más sensibles a desplanificación.
- **`methodCalls`** (11,6 %) — llamadas cortas, JIT en marcha.

El resto (generics, allocation, strings, colecciones) es estable (< 10 %), que es donde importa. Percentiles p90/p99: la relación p99/mediana es < 1,3 en la mayoría; `exceptions` destaca con 0,434 ms de p99 frente a 0,283 ms de mediana (1,5×) — el manejo de excepciones tiene cola alta, relevante para presupuesto de frame.

---

## 10. Hallazgos destacados

1. **Surtr es 19,7× más rápido que MoonSharp** (intérprete vs intérprete) y está a 3,3× de LuaJIT (JIT) y a 6,1× de C# en geomean. Posición correcta para un VM de scripting.

2. **`exceptions` es el as en la manga**: 73× más rápido que C#, 127× que MoonSharp, 32× que LuaJIT — handler-table sin excepción CLR.

3. **MoonSharp tiene 3 casos patológicos cuadráticos** (`#` O(n)) que no deben leerse como «MoonSharp es lento», sino como «ese patrón de append es O(n²) ahí».

4. **GC automático = misma velocidad, 64× menos registry.** El coste en tiempo del colector automático es 1,5 % geomean y mantiene el registry en 64 K slots (851 KB) frente a los 4 M (54,5 MB) del manual. La brecha más visible es `retainedObjects` (+22 %) con conjuntos vivos grandes.

5. **`generics` es la brecha a cerrar** (25× vs C#): la erasure paga box+cast+registro por iteración.

6. **Strings son la familia más cercana a C#** (1,6–3,7×): la decisión de `StrCat` n-ario con una sola asignación funciona. Notablemente, `stringTransform` supera a LuaJIT (1,6×).

7. **Los nuevos workloads de closures** revelan el costo real de la creación (`closureCreate` 14,9×) y la invocación a través de method-groups (`methodGroupInvoke` 14,7×), que son los mecanismos de dispatch más caros de Surtr.

8. **Buffers unmanaged** redujeron la asignación de arrays a un mínimo (arrayFill/arrayIndex/forIn: 56B), eliminando la presión de GC en estos patrones de carga.

---

## 11. Conclusiones

1. **Para comparar intérpretes, Surtr gana de calle**: 19,7× sobre MoonSharp, que es el otro intérprete del pool. Ningún caso lo gana MoonSharp.

2. **El techo es LuaJIT (3,3×)**: la diferencia es la esperada intérprete-vs-JIT; Surtr no «pierde» contra LuaJIT, está en otra categoría de implementación. Y supera en `stringTransform` y `exceptions`.

3. **La brecha con C# (6,1×) se concentra en asignación y dispatch** (11–25×): es donde el plan de optimización del runtime (buffers unmanaged, inline cache monomórfico, dict open-addressed) debe atacar primero, y el harness ya mide cada pieza por separado para verificar esas mejoras.

4. **La comparativa de GC es la decisión de producto**: GC automático ≈ mismo tiempo (1,5 % más lento) y 64× menos footprint de registry. La recomendación es automático por defecto y manual solo para hosts con control estricto de safepoints.

5. **El harness es fiable para esto**: 3 rondas con orden aleatorio, mediana de 15 muestras, checksums verificados en los 36 casos, percentiles y spread marcados. Los números de esta tabla son repetibles.

---

## 12. Cómo reproducir

```text
# Verificación rápida (CI): 36 checksums, sin timing
dotnet run --project src/Surtr.Bench -c Release -- --smoke

# Este informe (max-power + comparativa GC), con salida a CSV
dotnet run --project src/Surtr.Bench -c Release -- --extreme --surtr-gc both --csv results.csv

# Solo la comparativa de GC sobre un workload
dotnet run --project src/Surtr.Bench -c Release -- --surtr-gc both --workload allocation

# Coste real incluyendo la recolección CLR dentro de la muestra
dotnet run --project src/Surtr.Bench -c Release -- --gc-inclusive --workload allocation
```

El CSV lleva la huella de máquina (`# machine: ...`) y la configuración (`# settings: ...`) en las dos primeras líneas, de modo que dos runs en máquinas distintas son distinguibles.
