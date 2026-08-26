# Informe de Benchmarks: corrida extrema con control de bimodalidad

Fecha: 2026-08-26 (tarde). Máquina: AMD Ryzen 7 9800X3D, Windows 10.0.26200, .NET 8.0.13, Release.
Datos crudos: `bench_results_extreme.csv` (50 casos). Es la lectura; el CSV manda si discrepa.

Esta corrida aplica el protocolo de `docs/Informe-Volatilidad-Run.md`: como el intérprete se
entrega por el op-cache y salta entre dos estados según el layout de código del proceso, cada caso
se midió en **9 procesos frescos** (subprocesos del propio bench, cada uno con su ASLR) y se reporta
el **mínimo** (el estado más rápido alcanzable en ese momento) junto con el **spread de estado**
(cuánto más lento fue el peor estado muestreado).

---

## 1. Configuración exacta

```
surtrbench --surtr-only --extreme --processes 9
--extreme = --shuffle --rounds 3 --iters 15 --warmup 5 --memory-runs 5 --percentiles
```

- **9 procesos por caso**, secuenciales. La métrica reportada por caso es el mínimo de los 9
  (el estado de op-cache más favorable alcanzado en esa ventana de tiempo).
- `state_spread_pct` = `(max − min) / min` sobre los 9 procesos. Un caso con `bimodal` significa
  que el peor estado muestreado superó al mejor en más de un 20 %.
- Columnas de memoria del proceso más rápido (la memoria no es bimodal: no depende del op-cache).
- El control de C# se mide en cada subproceso, en las mismas condiciones.

**Estado de la máquina durante la corrida.** El estado rápido del op-cache (el que entregó
`arrayIndex` a ~4.2 ms por la mañana) **no estaba alcanzable**: a lo largo del día la máquina entró
en un régimen en el que todos los procesos caen en el estado lento (verificado antes y después de
la corrida con 40+ lanzamientos de varios casos). Por lo tanto **los números de abajo son el
throughput del estado lento — el mejor alcanzable en este momento**, no el techo del hardware. Un
A/B entre builds sigue siendo válido (ambos lados se miden en la misma ventana); un número
absoluto de hoy no es comparable con uno de ayer.

---

## 2. Tabla completa (50 casos)

| workload | surtr ms | c# ms | vs c# | bytes | objs | kept | c#B | state |
|---|---|---|---|---|---|---|---|---|
| `fib` | 2.866 | 0.175 | 16.4x | 0 | 0 | 0 | 0 | 4.3% |
| `intLoop` | 9.759 | 2.301 | 4.2x | 0 | 0 | 0 | 0 | 11.1% |
| `tightGuard` | 7.046 | 0.191 | 36.9x | 0 | 0 | 0 | 0 | 4.3% |
| `floatLoop` | 8.495 | 1.144 | 7.4x | 0 | 0 | 0 | 0 | 6.3% |
| `mathFns` | 7.944 | 1.543 | 5.1x | 0 | 0 | 0 | 0 | 7.4% |
| `arrayFill` | 0.863 | 0.213 | 4.1x | 56 B | 1 | 1 | 1.0 M | 4.4% |
| `arrayIndex` | 6.147 | 0.690 | 8.9x | 56 B | 1 | 1 | 4.2 K | 5.3% |
| `dictOps` | 0.918 | 0.218 | 4.2x | 1.9 M | 1 | 1 | 1.9 M | 1.9% |
| `dictMembers` | 1.393 | 0.378 | 3.7x | 1.9 M | 1 | 1 | 1.9 M | 4.1% |
| `dictString` | 6.798 | 1.840 | 3.7x | 12.6 K | 130 | 130 | 7.6 K | **62.9%** |
| `stringConcat` | 0.065 | 0.039 | 1.7x | 1.5 M | 1.2 k | 1.2 k | 1.4 M | 13.8% |
| `stringInterp` | 8.924 | 2.365 | 3.8x | 24.4 M | 300.0 k | 2 | 16.8 M | 9.6% |
| `stringOps` | 4.874 | 1.375 | 3.5x | 0 | 0 | 0 | 0 | 9.9% |
| `stringTransform` | 9.553 | 3.301 | 2.9x | 22.9 M | 200.0 k | 3 | 13.7 M | 19.8% |
| `closures` | 7.181 | 0.687 | 10.5x | 0 | 0 | 0 | 0 | 5.6% |
| `closureCreate` | 10.025 | 0.689 | 14.6x | 0 | 0 | 0 | 0 | 5.0% |
| `methodGroupInvoke` | 10.534 | 0.687 | 15.3x | 0 | 0 | 0 | 0 | 3.9% |
| `closureCapture` | 10.486 | 1.118 | 9.4x | 216 B | 2 | 2 | 120 B | 5.1% |
| `methodCalls` | 4.326 | 0.744 | 5.8x | 72 B | 1 | 1 | 24 B | 10.4% |
| `localModule` | 8.261 | 0.744 | 11.1x | 0 | 0 | 0 | 0 | 4.8% |
| `crossModule` | 8.570 | 0.745 | 11.5x | 0 | 0 | 0 | 0 | 3.7% |
| `virtualCalls` | 6.367 | 0.687 | 9.3x | 40 B | 1 | 1 | 0 | 7.9% |
| `interfaceCalls` | 10.528 | 0.687 | 15.3x | 40 B | 1 | 1 | 0 | 2.9% |
| `fieldAccess` | 6.892 | 0.687 | 10.0x | 80 B | 1 | 1 | 32 B | 6.5% |
| `propertyAccess` | 4.484 | 0.688 | 6.5x | 72 B | 1 | 1 | 24 B | 9.5% |
| `exceptions` | 0.465 | 19.935 | **0.02x** | 562.5 K | 8.0 k | 8.0 k | 1.5 M | 2.2% |
| `forIn` | 0.618 | 0.213 | 2.9x | 56 B | 1 | 1 | 1.0 M | 3.4% |
| `forInDict` | 2.021 | 0.374 | 5.4x | 3.9 M | 2 | 2 | 3.9 M | 4.4% |
| `iterator` | 3.178 | 0.262 | 12.1x | 120 B | 2 | 2 | 1.0 M | 7.7% |
| `genYield` | 1.509 | 0.117 | 12.9x | 192 B | 1 | 1 | 56 B | 7.9% |
| `handIterator` | 1.871 | 0.124 | 15.1x | 80 B | 1 | 1 | 32 B | 8.3% |
| `genDelegate` | 1.541 | 0.369 | 4.2x | 544 B | 3 | 3 | 168 B | 8.8% |
| `genSend` | 6.809 | 0.115 | 59.2x | 1.9 M | 50.0 k | 4 | 40 B | 5.7% |
| `genFinally` | 1.605 | 0.129 | 12.4x | 200 B | 1 | 1 | 56 B | 3.1% |
| `interop` | 4.861 | 0.744 | 6.5x | 0 | 0 | 0 | 0 | 15.3% |
| `valueClass` | 2.883 | 0.687 | 4.2x | 0 | 0 | 0 | 0 | 9.5% |
| `generics` | 20.770 | 0.752 | 27.6x | 32.0 M | 600.0 k | 4 | 6.9 M | 3.2% |
| `allocation` | 15.775 | 0.829 | 19.0x | 22.9 M | 300.0 k | 2 | 9.2 M | 3.3% |
| `retainedObjects` | 7.286 | 0.239 | 30.5x | 7.6 M | 100.0 k | 25.0 k | 3.6 M | 3.7% |
| `switchDense` | 5.498 | 0.689 | 8.0x | 0 | 0 | 0 | 0 | 7.5% |
| `typeTest` | 7.494 | 1.375 | 5.5x | 40 B | 1 | 1 | 0 | 8.4% |
| `nullable` | 5.603 | 0.688 | 8.1x | 0 | 0 | 0 | 0 | 4.6% |
| `enums` | 7.104 | 0.706 | 10.1x | 0 | 0 | 0 | 0 | 10.2% |
| `sortArray` | 10.514 | 0.829 | 12.7x | 56 B | 1 | 1 | 512.3 K | 4.2% |
| `sortBytecode` | 17.345 | 0.833 | 20.8x | 112 B | 2 | 2 | 512.3 K | 2.3% |
| `tuples` | 4.224 | 0.802 | 5.3x | 0 | 0 | 0 | 0 | 9.2% |
| `vec2Math` | 28.463 | 0.487 | 58.4x | 0 | 0 | 0 | 0 | 3.7% |
| `vec2Fields` | 30.578 | 0.860 | 35.6x | 96 B | 1 | 1 | 48 B | 2.6% |
| `vec2Class` | 49.127 | 1.057 | 46.5x | 45.8 M | 600.0 k | 6 | 18.3 M | 1.9% |
| `tupleReturn` | 9.869 | 0.767 | 12.9x | 0 | 0 | 0 | 0 | 8.3% |

`vs c#` = surtr/c# (más alto = Surtr más lento). `exceptions` a 0.02x significa que Surtr es
**42.9x más rápido** que C#. `state` = spread de estado (min→max de los 9 procesos).

---

## 3. Resumen

| Métrica | Valor |
|---|---|
| Media geométrica vs C# (50 casos) | **8.38x** |
| Casos bimodales durante la corrida | 1 (`dictString`, 62.9 %) |
| Casos con spread de estado < 5 % | 22 |
| 5-10 % | 21 |
| 10-20 % | 6 |
| Surtr más rápido que C# | `exceptions` (42.9x) |
| Peor ratio | `genSend` (59.2x) |

**La dispersión de estado es baja porque la máquina estaba en el régimen lento** (todos los
procesos cayeron en el mismo estado): un spread de 2-5 % aquí significa "los 9 procesos en el
mismo estado", no "el caso es estable". Solo `dictString` saltó de estado durante la corrida
(2 de 9 procesos cayeron a ~11.2 ms contra ~7.0 — +60 %; re-medido tras la corrida: 8 de 10 a
~7.0, 2 de 10 a ~11.2).

---

## 4. Desglose por categorías

| Categoría | casos | mediana surtr ms | geomean vs C# |
|---|---|---|---|
| Aritmética y control | 8 | 7.494 | 8.24x |
| Cadenas | 4 | 8.924 | 2.83x |
| Llamadas y despacho | 11 | 8.261 | 10.86x |
| Generadores | 5 | 1.605 | 14.30x |
| Asignación, GC, excepciones | 5 | 7.286 | 4.36x |
| Tipos de valor | 5 | 28.463 | 23.08x |
| Estructuras de datos | 10 | 2.021 | 6.33x |

### 4.1 Tipos de valor: la memoria es la columna que manda

`vec2Math` (`value class`) contra `vec2Class` (`class`), mismo código con una palabra de
diferencia: **0 B contra 45.8 M** y 600 002 objetos. El ahorro de tiempo (28.5 vs 49.1 ms, 42 %) es
real pero secundario frente a los 45.8 MB que el tipo de valor no entrega al recolector. `tuples`
y `tupleReturn` (destructuring de retorno multi-slot): **0 B**. La distinción reproduce la de C#
(`struct` 0 B vs `class` 18.3 M).

### 4.2 Excepciones: el mejor resultado de la suite

`exceptions` a **0.465 ms contra 19.935 ms de C#** (42.9x más rápido) y 31x más rápido que LuaJIT
en la corrida anterior. Un `try` que nunca lanza cuesta exactamente cero (tablas de handlers, no
opcodes). *Nota:* la corrida anterior lo midió a 0.290 ms; el caso es de los más sensibles al
layout del op-cache (bucle cerrado de throw/catch) y está en el régimen lento — re-medir en el
estado rápido antes de citar la cifra.

### 4.3 Llamadas y despacho

`methodCalls` 5.8x sobre C# (despacho directo); `interfaceCalls`/`virtualCalls` ~9-15x (una
indirección más). `localModule` (11.1x) contra `crossModule` (11.5x): la llamada cruzada de módulo
sigue costando ~1.0-1.25 ns/llamada sobre la local (el orden de magnitud que cerró P7).
`interop` (6.5x) es la frontera nativa, con spread de estado de 15.3 % — el camino de llamada
nativa es de los más sensibles.

### 4.4 Estructuras de datos y bucles

`forIn` 2.9x (el bucle fusionado `ArrForNext`), `forInDict` 5.4x, contra `iterator` 12.1x — el
valor exacto del lowering de `for-in`: ~4x de tiempo y toda la asignación. Los diccionarios con
clave `int` (3.7-4.2x) siguen siendo la mejor relación de la suite. `sortBytecode` (20.8x) vs
`sortArray` (12.7x): el sort en bytecode es 65 % más lento que el nativo que reentra por
comparación — el cierre de P9.

### 4.5 Cadenas

El bloque más cerca de C# (2.83x de media): el trabajo real lo hace la BCL y Surtr solo añade el
despacho. `stringOps` a 0 B; `stringConcat` asigna (1.5 M para 1200 iteraciones); `stringTransform`
(2.9x) y `stringInterp` (3.8x) con presión de asignación.

### 4.6 El coste aceptado de los genéricos

`generics` a 27.6x y 32.0 M es el peor resultado no-bucle: el borrado obliga a cajear el primitivo
que cruza el slot erasure y a un `Cast` al salir, dos objetos por iteración. Es el precio explícito
del trato de Java; la alternativa son los tipos de valor (§4.1).

---

## 5. Cómo leer estos números (y cómo no)

1. **Son el estado lento.** El estado rápido del op-cache no estaba alcanzable durante la corrida;
   estos son los mínimos de 9 procesos en ese régimen. El techo del hardware para `arrayIndex` es
   ~4.2 ms (medido por la mañana); hoy el mejor alcanzable fue 6.147. No es una regresión del
   intérprete: es la máquina en otro estado de código-layout.
2. **`state_spread_pct` es la honestidad del caso.** Un valor bajo con la máquina en régimen
   uniforme significa "todos los procesos en el mismo estado". Cuando el estado rápido vuelva a ser
   alcanzable, la misma corrida dará mínimos menores y spreads mayores en los casos sensibles.
3. **El A/B sigue siendo lo único comparable.** Estos números absolutos son una foto de hoy;
   comparar dos builds exige el mismo protocolo en la misma ventana (`scripts/ab-suite.ps1` o
   `--processes` por lado).
4. **La memoria es estable.** Las columnas `bytes`/`objs`/`kept` no dependen del op-cache y sí son
   comparables con corridas anteriores.

---

## 6. Referencias

- Protocolo, mecanismo y hallazgos: `docs/Informe-Volatilidad-Run.md`.
- A/B entre builds: `scripts/ab-suite.ps1`.
- Datos crudos de esta corrida: `bench_results_extreme.csv`.
- Informe de la corrida previa (40 casos, todos los motores): `benchmark_report.md`.