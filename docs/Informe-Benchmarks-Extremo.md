# Informe de Benchmarks: corrida extrema con control de bimodalidad

Fecha: 2026-08-28. Máquina: AMD Ryzen 7 9800X3D, Windows 10.0.26200, .NET 8.0.13, Release.
Datos crudos: `bench_results.csv` (64 casos, los cuatro motores — esta lectura solo usa `surtr` y
`csharp`; para Lua/LuaJIT, ver `benchmark_report.md`). Es la lectura; el CSV manda si discrepa.

Esta corrida aplica el protocolo de `docs/Informe-Volatilidad-Run.md`: como el intérprete se
entrega por el op-cache y salta entre dos estados según el layout de código del proceso, cada caso
se midió en **9 procesos frescos** (subprocesos del propio bench, cada uno con su ASLR) y se reporta
el **mínimo** (el estado más rápido alcanzable en ese momento) junto con el **spread de estado**
(cuánto más lento fue el peor estado muestreado).

---

## 1. Configuración exacta

```
surtrbench --processes 9 --iters 15 --warmup 5 --rounds 3 --shuffle
```

- **9 procesos por caso**, secuenciales. La métrica reportada por caso es el mínimo de los 9
  (el estado de op-cache más favorable alcanzado en esa ventana de tiempo).
- `state_spread_pct` = `(max − min) / min` sobre los 9 procesos.
- Columnas de memoria del proceso más rápido (la memoria no es bimodal: no depende del op-cache).
- El control de C# se mide en cada subproceso, en las mismas condiciones.
- Esta corrida incluyó los cuatro motores (no solo `--surtr-only`); MoonSharp corrió bajo su
  disyuntor de 1000x (`CLAUDE.md`, sección "Benchmark the VM"), así que su columna no afecta a
  nada de lo que sigue — este informe es Surtr contra C#.

---

## 2. Tabla completa (64 casos)

`vs c#` = surtr/c# (más alto = Surtr más lento). `(diag)` marca `vec2Class`, fuera de la media de
§3 — mide deliberadamente el idioma lento de la clase normal, no el ranking de Surtr.

| workload | surtr ms | c# ms | vs c# | bytes | objs | kept | c#B | state |
|---|---|---|---|---|---|---|---|---|
| `fib` | 3.043 | 0.082 | 37.1x | 0 | 0 | 0 | 0 | 7.2% |
| `intLoop` | 7.754 | 2.302 | 3.4x | 0 | 0 | 0 | 0 | 8.7% |
| `tightGuard` | 4.203 | 0.191 | 22.0x | 0 | 0 | 0 | 0 | 3.5% |
| `floatLoop` | 6.064 | 1.145 | 5.3x | 0 | 0 | 0 | 0 | 4.6% |
| `mathFns` | 7.986 | 1.531 | 5.2x | 0 | 0 | 0 | 0 | 9.9% |
| `arrayFill` | 0.765 | 0.148 | 5.2x | 56 B | 1 | 1 | 1.0 M | 3.7% |
| `arrayIndex` | 5.442 | 0.690 | 7.9x | 56 B | 1 | 1 | 4.3 K | 3.1% |
| `dictOps` | 0.856 | 0.218 | 3.9x | 2.0 M | 1 | 1 | 2.0 M | 7.8% |
| `dictMembers` | 1.329 | 0.376 | 3.5x | 2.0 M | 1 | 1 | 2.0 M | 4.9% |
| `dictString` | 6.261 | 2.051 | 3.1x | 12.9 K | 130 | 130 | 7.8 K | **71.3%** |
| `stringConcat` | 0.060 | 0.039 | 1.5x | 1.5 M | 1.2 k | 1.2 k | 1.5 M | 5.0% |
| `stringInterp` | 9.212 | 2.398 | 3.8x | 25.6 M | 300.0 k | 2 | 17.6 M | 17.8% |
| `stringOps` | 4.394 | 1.375 | 3.2x | 0 | 0 | 0 | 0 | 3.7% |
| `stringTransform` | 9.388 | 3.433 | 2.7x | 24.0 M | 200.0 k | 3 | 14.4 M | 10.1% |
| `closures` | 6.973 | 0.688 | 10.1x | 0 | 0 | 0 | 0 | 4.3% |
| `closureCreate` | 10.030 | 0.688 | 14.6x | 0 | 0 | 0 | 0 | 18.6% |
| `methodGroupInvoke` | 10.099 | 0.687 | 14.7x | 0 | 0 | 0 | 0 | 6.1% |
| `closureCapture` | 10.036 | 1.123 | 8.9x | 216 B | 2 | 2 | 120 B | 5.1% |
| `methodCalls` | 3.555 | 0.744 | 4.8x | 72 B | 1 | 1 | 24 B | 7.5% |
| `localModule` | 7.789 | 0.745 | 10.5x | 0 | 0 | 0 | 0 | 4.3% |
| `crossModule` | 8.025 | 0.744 | 10.8x | 0 | 0 | 0 | 0 | 5.7% |
| `virtualCalls` | 6.161 | 0.687 | 9.0x | 40 B | 1 | 1 | 0 | 3.1% |
| `interfaceCalls` | 10.298 | 0.687 | 15.0x | 40 B | 1 | 1 | 0 | 3.4% |
| `fieldAccess` | 5.534 | 0.687 | 8.1x | 80 B | 1 | 1 | 32 B | 6.8% |
| `propertyAccess` | 3.623 | 0.687 | 5.3x | 72 B | 1 | 1 | 24 B | 3.6% |
| `exceptions` | 0.574 | 20.692 | **0.03x** | 576.0 K | 8.0 k | 8.0 k | 1.6 M | 10.3% |
| `forIn` | 0.523 | 0.148 | 3.5x | 56 B | 1 | 1 | 1.0 M | 8.0% |
| `forInDict` | 1.488 | 0.518 | 2.9x | 4.1 M | 2 | 2 | 4.1 M | 11.8% |
| `iterator` | 2.237 | 0.189 | 11.8x | 120 B | 2 | 2 | 1.0 M | 8.3% |
| `genYield` | 1.447 | 0.119 | 12.2x | 192 B | 1 | 1 | 56 B | 2.6% |
| `handIterator` | 1.825 | 0.124 | 14.7x | 80 B | 1 | 1 | 32 B | 2.6% |
| `genDelegate` | 1.505 | 0.338 | 4.5x | 544 B | 3 | 3 | 168 B | 4.4% |
| `genSend` | 7.218 | 0.115 | 62.8x | 2.0 M | 50.0 k | 4 | 40 B | 6.6% |
| `genFinally` | 1.575 | 0.131 | 12.0x | 200 B | 1 | 1 | 56 B | 1.8% |
| `interop` | 5.418 | 0.744 | 7.3x | 0 | 0 | 0 | 0 | **44.6%** |
| `valueClass` | 2.291 | 0.687 | 3.3x | 0 | 0 | 0 | 0 | 4.2% |
| `generics` | 9.856 | 0.753 | 13.1x | 12.0 M | 300.0 k | 2 | 7.2 M | 7.6% |
| `allocation` | 13.884 | 0.834 | 16.6x | 24.0 M | 300.0 k | 2 | 9.6 M | 16.7% |
| `retainedObjects` | 5.874 | 0.245 | 24.0x | 8.0 M | 100.0 k | 25.0 k | 3.7 M | **41.5%** |
| `switchDense` | 4.821 | 0.688 | 7.0x | 0 | 0 | 0 | 0 | 7.4% |
| `typeTest` | 7.327 | 1.378 | 5.3x | 40 B | 1 | 1 | 0 | 8.3% |
| `nullable` | 6.603 | 0.688 | 9.6x | 0 | 0 | 0 | 0 | 4.7% |
| `enums` | 6.852 | 0.707 | 9.7x | 0 | 0 | 0 | 0 | 7.6% |
| `sortArray` | 10.737 | 0.835 | 12.9x | 56 B | 1 | 1 | 524.6 K | 14.4% |
| `sortBytecode` | 16.359 | 0.831 | 19.7x | 112 B | 2 | 2 | 524.6 K | 1.6% |
| `tuples` | 3.456 | 0.801 | 4.3x | 0 | 0 | 0 | 0 | **37.6%** |
| `vec2Math` | 27.691 | 0.486 | 57.0x | 0 | 0 | 0 | 0 | 2.9% |
| `vec2Fields` | 29.950 | 0.859 | 34.9x | 96 B | 1 | 1 | 48 B | 3.9% |
| `vec2Class` (diag) | 44.288 | 1.055 | 42.0x | 48.0 M | 600.0 k | 6 | 19.2 M | 7.6% |
| `tupleReturn` | 9.787 | 0.767 | 12.8x | 0 | 0 | 0 | 0 | 6.8% |
| `bitwiseOps` | 7.200 | 0.688 | 10.5x | 0 | 0 | 0 | 0 | 3.6% |
| `rangeLoop` | 4.312 | 1.375 | 3.1x | 0 | 0 | 0 | 0 | 4.1% |
| `stringIndexSwitch` | 15.535 | 0.932 | 16.7x | 56 B | 1 | 1 | 64 B | 4.9% |
| `castAndTypeof` | 11.605 | 0.837 | 13.9x | 12.0 M | 300.0 k | 2 | 7.2 M | 14.5% |
| `staticCalls` | 3.322 | 0.697 | 4.8x | 0 | 0 | 0 | 0 | **30.4%** |
| `nativeInstanceCalls` | 5.724 | 0.699 | 8.2x | 40 B | 1 | 1 | 0 | **49.3%** |
| `nativeStaticCalls` | 5.899 | 0.697 | 8.5x | 0 | 0 | 0 | 0 | **41.3%** |
| `forInStringTuple` | 11.409 | 2.757 | 4.1x | 5.2 M | 50.0 k | 2 | 0 | 14.3% |
| `arrayFullSurface` | 0.684 | 0.158 | 4.3x | 56 B | 1 | 1 | 1.0 M | 1.6% |
| `tupleBoxed` | 9.848 | 0.744 | 13.2x | 26.4 M | 300.0 k | 2 | 0 | 8.5% |
| `disposal` | 16.745 | 0.759 | 22.1x | 21.6 M | 300.0 k | 2 | 7.2 M | 4.6% |
| `countdownWhile` | 2.657 | 0.687 | 3.9x | 0 | 0 | 0 | 0 | 4.9% |
| `collatzWhile` | 3.197 | 0.203 | 15.7x | 0 | 0 | 0 | 0 | 6.5% |
| `linkedListWalk` | 44.762 | 1.364 | 32.8x | 24.0 M | 300.0 k | 300.0 k | 9.6 M | 4.8% |

---

## 3. Resumen

| Métrica | Valor |
|---|---|
| Media geométrica vs C# (63 casos, excluido `vec2Class`) | **7.86x** |
| Casos con spread de estado ≥ 20 % | 7 |
| Casos con spread < 5 % | 27 |
| 5-10 % | 21 |
| 10-20 % | 9 |
| Surtr más rápido que C# | `exceptions` (36.0x) |
| Peor ratio | `genSend` (62.8x) |

Siete casos superaron el 20 % de spread: `dictString` (71.3 %), `nativeInstanceCalls` (49.3 %),
`interop` (44.6 %), `retainedObjects` (41.5 %), `nativeStaticCalls` (41.3 %), `tuples` (37.6 %) y
`staticCalls` (30.4 %). Tres de los siete (`staticCalls`, `nativeInstanceCalls`,
`nativeStaticCalls`) son casos nuevos de esta sesión, sin historial de estabilidad todavía — su
dispersión no dice nada por sí sola hasta que se repita en una segunda corrida.

---

## 4. Desglose por categorías

Misma categorización que `benchmark_report.md` §5, para que ambos documentos se puedan leer juntos
sin reconciliar dos esquemas.

| Categoría | casos | mediana surtr ms | geomean vs C# |
|---|---|---|---|
| Aritmética y control | 13 | 6.603 | 7.39x |
| Cadenas | 5 | 9.212 | 3.86x |
| Llamadas y despacho | 16 | 6.030 | 9.61x |
| Generadores | 5 | 1.575 | 14.32x |
| Asignación, GC y excepciones | 6 | 7.865 | 4.69x |
| Tipos de valor | 5 | 9.848 | 17.07x |
| Estructuras de datos | 13 | 2.237 | 6.41x |

(`vec2Class` no cuenta en "Tipos de valor" — sigue en la tabla de §2 pero fuera de toda media.)

### 4.1 Tipos de valor: la memoria es la columna que manda

`vec2Math` (`value class`) contra `vec2Class` (`class`, ahora diagnóstico), mismo código con una
palabra de diferencia: **0 B contra 48.0 M** y 600 002 objetos. El ahorro de tiempo (27.7 vs
44.3 ms, 37 %) es real pero secundario frente a los 48 MB que el tipo de valor no entrega al
recolector. `tuples`, `tupleReturn` y `tupleBoxed` completan el cuadro: los dos primeros, **0 B**;
el tercero — la misma tupla forzada por un slot `unknown` — **26.4 MB**, la medida exacta de lo que
cuesta la forma boxeada que los otros dos evitan.

### 4.2 Excepciones: el mejor resultado de la suite

`exceptions` a **0.574 ms contra 20.692 ms de C#** (36.0x más rápido). Un `try` que nunca lanza
cuesta exactamente cero (tablas de handlers, no opcodes).

### 4.3 Llamadas y despacho

`methodCalls` 4.8x sobre C# (despacho directo); `interfaceCalls`/`virtualCalls` ~9-15x (una
indirección más). `localModule` (10.5x) contra `crossModule` (10.8x): la llamada cruzada de módulo
sigue costando poco sobre la local. Tres casos nuevos — `staticCalls` (4.8x, igual que
`methodCalls`: un estático no paga receptor), `nativeInstanceCalls` (8.2x) y `nativeStaticCalls`
(8.5x) — cierran el hueco de cobertura de métodos nativos sobre una clase propia, con y sin
receptor; ambos salen cerca de `interop` (7.3x), la misma frontera nativa.

### 4.4 Estructuras de datos y bucles

`forIn` 3.5x (el bucle fusionado `ArrForNext`), `forInDict` 2.9x, contra `iterator` 11.8x — el
valor exacto del lowering de `for-in`: ~3.4x de tiempo y toda la asignación. Los diccionarios con
clave `int` (3.5-3.9x) siguen siendo la mejor relación de la suite. `sortBytecode` (19.7x) vs
`sortArray` (12.9x): el sort en bytecode sigue siendo más lento que el nativo que reentra por
comparación. `arrayFullSurface` (4.3x, nuevo) cubre `pop`/`insert`/`removeAt`/`clear`/`indexOf`/
`contains`; `forInStringTuple` (4.1x, nuevo) es el primer `for-in` sobre `string` y sobre tupla.
`linkedListWalk` (32.8x, nuevo) es el más caro de la categoría: la primera prueba cuya condición de
parada es una comparación contra `null` en vez de un índice, y sus 300 000 objetos retenidos (la
lista entera viva durante el recorrido) son los más altos de toda la suite fuera de
`retainedObjects`.

### 4.5 Cadenas

El bloque más cerca de C# (3.86x de media): el trabajo real lo hace la BCL y Surtr solo añade el
despacho. `stringOps` a 0 B; `stringConcat` asigna (1.5 M para 1200 iteraciones); `stringTransform`
(2.7x) y `stringInterp` (3.8x) con presión de asignación. `stringIndexSwitch` (16.7x, nuevo) es el
más caro del bloque: indexación de string, switch por string y switch disperso, ninguno de los
cuales tocaba `switchDense` (deliberadamente denso y contiguo).

### 4.6 Genéricos: el coste real, aislado por fin

`generics` bajó de 22.3 ms/32.0 MB (la cifra de la corrida anterior) a **9.9 ms/12.0 MB** en esta.
No es una mejora del intérprete: `Box<T>` era `class`, así que el caso pagaba dos asignaciones por
iteración — la caja del primitivo que cruza el slot erasado (lo que el caso dice medir) y la
instancia del contenedor (un coste que `allocation` ya aísla por su cuenta). Al declarar
`Box<T>` como `value class` de un solo campo — que erasa a exactamente ese campo, sin objeto
propio — el caso quedó midiendo solo lo que su nombre promete. El coste que queda (13.1x sobre C#,
un objeto por iteración) sigue siendo el precio explícito del borrado; la alternativa siguen siendo
los tipos de valor de §4.1.

### 4.7 Lo que se añadió a propósito: cobertura, no solo velocidad

`bitwiseOps`, `rangeLoop`, `castAndTypeof`, `disposal`, `countdownWhile` y `collatzWhile` no
estaban antes en el catálogo — cada uno ejerce un mecanismo del lenguaje que el resto de la suite
dejaba en cero despachos (bitwise, `range`, `as`/`typeof` como valor, `IDisposable`+`using` fuera
de un generador, un bucle descendente, un `while` cuya longitud no se conoce de antemano). Ninguno
es el resultado más rápido ni el más lento de su categoría; existen para que "0 ejecuciones" deje
de ser cierto de esa parte del lenguaje, no para mover una media.

---

## 5. Cómo leer estos números (y cómo no)

1. **Son el mínimo de 9 procesos frescos**, no una mediana dentro de un solo proceso — el estado de
   op-cache más rápido alcanzable en el momento de la corrida, no necesariamente el techo absoluto
   del hardware.
2. **`state_spread_pct` es la honestidad del caso.** Un valor bajo significa que los 9 procesos
   cayeron en el mismo estado; uno alto (`dictString`, `interop`, `retainedObjects`, `tuples`) dice
   que ese caso concreto es sensible al layout de código y su mínimo puede no repetirse igual en la
   próxima corrida.
3. **El A/B sigue siendo lo único comparable entre builds.** Estos números absolutos son una foto
   de hoy; comparar dos builds exige el mismo protocolo en la misma ventana
   (`scripts/ab-suite.ps1` o `--processes` por lado).
4. **La memoria es estable.** Las columnas `bytes`/`objs`/`kept` no dependen del op-cache y sí son
   comparables con corridas anteriores — es así como se verificó que `generics` bajó de 32.0 MB a
   12.0 MB por un cambio de tipo, no por ruido de medición.
5. **`vec2Class` es diagnóstico, no ranking.** Aparece en la tabla de §2 con su número real, pero
   no entra en la media de §3 ni en el desglose de §4.1 salvo como comparación explícita — mide
   deliberadamente el idioma lento, y sumarlo a una media distorsionaría el número que la suite
   existe para dar.

---

## 6. Referencias

- Protocolo, mecanismo y hallazgos: `docs/Informe-Volatilidad-Run.md`.
- A/B entre builds: `scripts/ab-suite.ps1`.
- Datos crudos de esta corrida: `bench_results.csv`.
- Informe con los cuatro motores (Surtr/Lua/LuaJIT/C#), metodología completa y el disyuntor de
  MoonSharp: `benchmark_report.md`.
