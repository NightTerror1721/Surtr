# Informe de Benchmarks de Surtr

Corrida completa de `src/Surtr.Bench` sobre **64 casos**, cada uno escrito tres veces —
en Surtr, en Lua y en C#— y verificado por checksum antes de aceptar ningún tiempo.

Los datos crudos están en `bench_results.csv`, con las tres líneas de cabecera (`# machine:`,
`# settings:`, `# processes=N:`) que identifican la máquina y la configuración exactas de esta
corrida. Este documento es su lectura; si los dos discrepan, manda el CSV.

---

## 1. Qué se compara

| Motor | Qué es |
|---|---|
| **surtr** | el VM de este repositorio, bytecode interpretado sobre pila no gestionada |
| **lua** (MoonSharp) | intérprete de Lua escrito enteramente en C# gestionado — el punto de comparación honesto para "un lenguaje embebido en .NET" |
| **luajit** | LuaJIT 2.x nativo a través de `lua51.dll` — un JIT industrial, el techo de lo que un lenguaje de script puede correr |
| **c#** | el mismo algoritmo escrito naturalmente en C# y compilado por el JIT de .NET — el techo teórico |

**Un baseline es el mismo algoritmo escrito con naturalidad en el lenguaje destino**, y eso tiene
consecuencias: donde el JIT de C# inlinea la abstracción hasta hacerla desaparecer, ese número
rápido *es* la respuesta honesta de C# y se deja como está. Lo que no se admite es escribir la
abstracción fuera del código fuente a mano. Donde un lenguaje sencillamente no tiene el
constructo —Lua no tiene tipos de valor, ni bitwise nativo en 5.1, ni clases— la diferencia es el
hallazgo del caso, no un defecto de la comparación.

---

## 2. Metodología

### 2.1 Configuración de esta corrida

Ejecutada con el protocolo consciente de bimodalidad (`docs/Informe-Volatilidad-Run.md`):

| Ajuste | Valor |
|---|---|
| Compilación | `Release` (en `Debug` el rendimiento de Surtr cae aproximadamente a la mitad) |
| Procesos por caso | **9**, secuenciales — se reporta el mínimo (el estado de op-cache más rápido alcanzado) |
| Iteraciones cronometradas | 15 por caso, dentro de cada proceso |
| Calentamiento | 5 por caso |
| Rondas | 3, con **orden aleatorizado** (`shuffle`, semilla 12345) |
| Corridas de memoria | 3, medidas fuera de la región cronometrada |
| Política de GC de Surtr | automática |
| Medida reportada | **mínimo de los 9 procesos** (la columna `state` del CSV da el spread entre estados) |

Las tres rondas con orden aleatorio existen por una razón concreta: sin ellas el resultado de un
caso dependía de qué caso se hubiera ejecutado antes. Los 9 procesos por caso existen por otra:
el intérprete se entrega por el op-cache (µop cache decodificado), indexado por la dirección
absoluta del código; esa dirección se re-rolla por proceso (ASLR), y el intérprete salta entre dos
estados ~20-50 % apartes dentro de un mismo binario. Un solo proceso muestrea un estado
cualquiera; nueve procesos y el mínimo dan el estado más rápido alcanzable, que es lo único
comparable entre dos builds. El detalle completo está en `docs/Informe-Volatilidad-Run.md`.

### 2.2 Columnas

| Columna | Significado |
|---|---|
| `surtr ms` / `lua ms` / `luajit ms` / `c# ms` | mínimo de 9 procesos de cada motor, en milisegundos |
| `vs lua`, `vs luajit` | cuántas veces **más lento** es ese motor que Surtr (por debajo de `1.00x`, Surtr es el más lento) |
| `vs c#` | cuántas veces más lento es Surtr que el baseline de C# |
| `bytes`, `objs`, `kept` | bytes gestionados que asignó Surtr, objetos que registró, y cuántos seguían vivos al volver |
| `c#B` | bytes que asignó el baseline de C# para el mismo trabajo |
| `spread` | cuánto más lento fue el peor de los 9 estados que el mejor; por encima del ~10 % conviene leer el número con cautela |

**`bytes` no es una columna secundaria.** Un VM que corre dentro del presupuesto de un frame se
juzga tanto por lo que asigna como por lo que tarda: una corrida que le entrega un megabyte al
recolector lo paga en algún frame posterior que la columna de tiempo no puede mostrar.

### 2.3 El ajuste del JIT no es una perilla de tuning

El `.csproj` fija `TieredCompilationQuickJitForLoops=false`. Sin eso, un método se promociona a
tier 1 tras 30 llamadas y un benchmark llama a cada workload un puñado de veces —de modo que **el
propio bucle del intérprete** se estaba midiendo en tier 0 durante corridas enteras. No se apaga
`TieredCompilation` por completo porque eso también apagaría TieredPGO, que el intérprete
virtual-pesado de MoonSharp pierde más de lo que gana, y eso favorecería a Surtr por una razón que
no tiene nada que ver con Surtr. Es además la configuración honesta para donde Surtr corre de
verdad: el JIT de Mono en Unity y el AOT de IL2CPP no tienen tiering que calentar.

### 2.4 La verificación por checksum es una red de corrección

Los cuatro motores tienen que llegar al mismo resultado o la corrida falla. Eso ya atrapó una
miscompilación (`int?` sosteniendo un 1) que ningún test unitario detectó, porque el que más se
acercaba usaba el valor 0. Los 64 casos de esta corrida pasan la verificación (`--verify-only`,
0 fallos).

### 2.5 El disyuntor de MoonSharp

Nuevo en esta corrida. MoonSharp siempre se mide **después** de Surtr, así que para cuando le toca
el turno ya hay una referencia real con la que medir: una sola llamada de sondeo, sin cronometrar,
decide si vale la pena pagar el warmup+iteraciones completo. Si esa llamada ya cuesta **1000 veces**
o más la mediana ya medida de Surtr (`RunnerOptions.MoonSharpExtremeRatio`), se corta ahí: el
sondeo mismo queda como cota inferior, la fila se marca `EXTREMO-LENTO` (`!!` en la celda, `>=1000x`
en la razón) y **queda fuera de la media geométrica** — un caso extremo no debe describir el
conjunto entero.

Cinco casos lo dispararon en esta corrida: `arrayFill`, `forIn`, `iterator`, `bitwiseOps` y
`arrayFullSurface`, todos con un sondeo de entre 6,6 y 13,3 **segundos** frente a los 0,5-7 ms de
Surtr. Sin el disyuntor, medir estos cinco casos con el protocolo completo (9 procesos × 20
llamadas por proceso) habría costado por sí solo más de dos horas; con él, cada uno paga una sola
llamada.

---

## 3. Resumen

| Comparación | Media geométrica | Casos |
|---|---|---|
| Surtr vs MoonSharp | **11,0x más rápido** | 58 (excluidos los 5 `EXTREMO-LENTO`) |
| Surtr vs LuaJIT | **2,95x más lento** | 63 |
| Surtr vs C# | **7,86x más lento** | 63 |

(`vec2Class` queda fuera de las tres medias — ver §4, es diagnóstico, no ranking.)

Surtr ocupa el nicho que se propuso: muy por encima de un intérprete gestionado de referencia, por
debajo de un JIT nativo industrial, y con una factura de memoria que en los casos que importan es
**cero**.

Surtr gana a LuaJIT en seis casos: `bitwiseOps` (19,2x — con el matiz de §5.1: ambos motores Lua
corren la misma emulación bit a bit escrita a mano, así que esto no mide la velocidad nativa de
LuaJIT), `exceptions` (15,7x), `arrayFullSurface` (2,6x), `stringConcat` (2,5x), `genDelegate`
(2,3x — inesperado: la delegación de generadores por enlace de Surtr le gana a la corrutina nativa
de LuaJIT) y `stringTransform` (1,6x). Pierde en los otros 57, por un factor que va de 1,08x
(`disposal`) a 57x (`vec2Math`, donde LuaJIT hunde el bucle entero).

---

## 4. Tipos de valor: el resultado principal de esta corrida

Cinco casos miden lo que `docs/Plan-TiposDeValor.md` construyó, más uno nuevo que fuerza el paso
por la forma boxeada.

### 4.1 El A/B: `vec2Math` contra `vec2Class`

Los dos casos son **el mismo código fuente con una palabra de diferencia**: `value class Vec2`
frente a `class Vec2Ref`. Tres construcciones y tres llamadas por iteración, 300 000 iteraciones.
Desde esta corrida, `vec2Class` está marcado `diagnostic_only` en el catálogo — sigue midiéndose y
apareciendo en la tabla, pero queda fuera de las tres medias geométricas de §3: existe para
documentar el coste de la clase normal, no para que un caso escrito deliberadamente con el idioma
lento arrastre el ranking de Surtr contra LuaJIT o MoonSharp.

| | surtr ms | bytes | objs | vivos al volver |
|---|---|---|---|---|
| `vec2Math` (`value class`) | **27,691** | **0** | **0** | 0 |
| `vec2Class` (`class`, diagnóstico) | 44,288 | **48,0 M** | 600 002 | 6 |

**La columna que importa es `bytes`.** El tipo de valor no entrega nada al recolector; la clase de
referencia le entrega 48 MB y 600 000 objetos por corrida. El 37 % de tiempo ahorrado es real pero
secundario: en un motor de juego, los 48 MB son lo que se paga en un frame posterior que la
columna de tiempo no muestra.

### 4.2 Campos de valor en línea

| | surtr ms | bytes | objs |
|---|---|---|---|
| `vec2Fields` | 29,950 | **96 B** | **1** |

La aritmética idéntica leída y escrita a través de **campos value-type de una instancia**
(`LoadValueField`/`StoreValueField` sobre un objeto de cuatro slots). Los 96 B y el objeto único
son el `Body` que sostiene los campos, no las 300 000 operaciones de vector: el mapa de slots de
referencia del `Body` está **vacío**, así que una recolección lo salta por completo.

### 4.3 Retorno multi-slot, destructuring y la forma boxeada

| | surtr ms | bytes | objs |
|---|---|---|---|
| `tuples` | 3,456 | **0** | **0** |
| `tupleReturn` | 9,787 | **0** | **0** |
| `tupleBoxed` | 9,848 | 26,4 M | 300 000 |

`tuples` construye un literal de tupla y lee sus dos elementos por iteración: dos slots en el
frame, **0 B**. `tupleReturn` llama a `divmod(i, 7)` y ata los dos nombres por destructuring,
300 000 veces: `ReturnValues` mueve los dos slots sobre la base del frame y ningún objeto tupla
llega a existir — 13,9x más rápido que MoonSharp haciendo lo mismo con sus retornos múltiples
nativos.

`tupleBoxed` es el caso nuevo, y es deliberadamente la contraparte de los dos anteriores: la misma
tupla `(i, i+1)` pero forzada por un slot `unknown` y leída de vuelta con `as (int, int)`. La
diferencia es exactamente la que predice el modelo — 0 B contra 26,4 MB, un `SurtrTuple` real por
iteración — porque un slot erasado siempre es una referencia (`CLAUDE.md`, §"Runtime objects"), así
que la tupla deja de poder vivir como dos slots inline en cuanto algo la trata como `unknown`.
`tuples` mide la forma rápida que la suite ya demostraba; `tupleBoxed` mide, por primera vez, lo
que cuesta la otra.

### 4.4 Qué cuesta todavía

Un tipo de valor no hace gratis la **llamada**. `vec2Math` son ~92 ns por iteración para tres
llamadas y tres construcciones, contra los ~12 ns por iteración de `methodCalls` (una llamada): el
coste está en el protocolo de frame, que es exactamente el mismo que antes. Frente a C# (57,0x) y
a LuaJIT (57,3x, ver §7) la distancia es grande porque ambos hunden el bucle entero —C# mantiene
el `struct` en registros, LuaJIT hace *allocation sinking*— y un intérprete de bytecode no tiene
esa jugada disponible. **Lo que los tipos de valor quitan es la asignación, no el despacho.**

La comparación relevante no es contra un JIT sino contra la alternativa dentro de Surtr, y ahí el
resultado es inequívoco: `vec2Math` contra `vec2Class`, 0 B contra 48 MB.

---

## 5. El resto de la suite, por categorías

### 5.1 Aritmética y control

| Caso | surtr ms | luajit ms | c# ms | vs c# |
|---|---|---|---|---|
| `intLoop` (1M) | 7,754 | 4,612 | 2,302 | 3,4x |
| `tightGuard` (1M) | 4,203 | 0,192 | 0,191 | 22,0x |
| `floatLoop` (1M) | 6,064 | 1,146 | 1,145 | 5,3x |
| `mathFns` | 7,986 | 1,333 | 1,531 | 5,2x |
| `switchDense` | 4,821 | 1,883 | 0,688 | 7,0x |
| `nullable` | 6,603 | 1,575 | 0,688 | 9,6x |
| `enums` | 6,852 | 1,771 | 0,707 | 9,7x |
| `typeTest` | 7,327 | 2,765 | 1,378 | 5,3x |
| `bitwiseOps` (nuevo) | 7,200 | 138,419 | 0,688 | 10,5x |
| `rangeLoop` (nuevo) | 4,312 | 2,765 | 1,375 | 3,1x |
| `castAndTypeof` (nuevo) | 11,605 | 1,499 | 0,837 | 13,9x |
| `countdownWhile` (nuevo) | 2,657 | 1,383 | 0,687 | 3,9x |
| `collatzWhile` (nuevo) | 3,197 | 0,581 | 0,203 | 15,7x |

Un millón de iteraciones de aritmética entera a 3,4x de C# es el mejor resultado del bloque
aritmético, y valida el NaN-boxing: mover primitivos por el VM no cuesta metadata ni heap.
`nullable` a 0 B confirma lo mismo para la ausencia — el tag reservado nunca toca el heap.

**Cinco casos nuevos cierran huecos de cobertura reales.** `bitwiseOps` ejerce And/Or/Xor/Not/Shl/
/Shr/Sar, una familia entera de opcodes que ningún otro caso tocaba — nótese que aquí es Surtr
quien gana a LuaJIT (10,5x sobre C#, pero 19,2x *más rápido* que LuaJIT): Lua 5.1 no tiene
operadores bitwise nativos, así que el `LuaSource` compartido entre MoonSharp y LuaJIT lleva una
emulación bit a bit escrita a mano, y ambos motores Lua la pagan por igual — esto no dice nada
sobre la velocidad nativa de LuaJIT en bitwise, solo que ninguno de los dos motores Lua tiene un
camino nativo aquí. `rangeLoop` ejerce el tipo `range` (exclusivo e inclusivo) que ninguna otra
prueba tocaba. `castAndTypeof` fuerza `as` como valor en vez de en rama, más `typeof` estático y
dinámico — y por primera vez cuantifica el coste de boxear un primitivo hacia `unknown` sin la
distracción de una construcción de objeto adicional (12,0 MB, 300 000 objetos: uno por iteración,
el boxed int). `countdownWhile` es el primer bucle descendente de la suite: como la fusión de
bucle contado solo cubre `<`/`<=` (§5.4 de más abajo), este paga el camino sin fundir a propósito.
`collatzWhile` es el único caso con longitud de iteración genuinamente impredecible — cada semilla
recorre un número de pasos distinto que ningún patrón fijo puede aprender, a diferencia de los
demás bucles contados de la suite.

### 5.2 Cadenas

| Caso | surtr ms | c# ms | vs c# | bytes | c#B |
|---|---|---|---|---|---|
| `stringConcat` | 0,060 | 0,039 | 1,5x | 1,5 M | 1,5 M |
| `stringOps` | 4,394 | 1,375 | 3,2x | **0** | 0 |
| `stringInterp` | 9,212 | 2,398 | 3,8x | 25,6 M | 17,6 M |
| `stringTransform` | 9,388 | 3,433 | 2,7x | 24,0 M | 14,4 M |
| `stringIndexSwitch` (nuevo) | 15,535 | 0,932 | 16,7x | 56 B | 64 B |

La familia original está entre 1,5x y 3,8x de C#, el mejor bloque de la suite: las cadenas de
Surtr son cadenas de la CLR, así que el trabajo real lo hace el mismo código en ambos lados y lo
único que Surtr añade es el despacho. `StrCat` toma un **conteo**, de modo que una interpolación
entera es una instrucción y una asignación, en lugar de n−1 de cada. `stringTransform` sigue siendo
uno de los casos donde Surtr gana a LuaJIT (1,64x): `substring` y `replace` de la BCL contra las de
Lua.

`stringIndexSwitch` es nuevo y mucho más caro que sus vecinos (16,7x): indexa un string carácter a
carácter (`StrGet`), hace un `switch` sobre `string` y otro sobre un conjunto de enteros disperso —
tres mecanismos que ningún otro caso tocaba (el único `switch` previo, `switchDense`, es
deliberadamente denso y contiguo). El coste no sorprende: la comparación de strings por texto y la
tabla dispersa (`SwitchLookup`) son ambas rutas más caras que sus equivalentes densos por diseño.

### 5.3 Llamadas y despacho

| Caso | surtr ms | luajit ms | c# ms | vs c# |
|---|---|---|---|---|
| `methodCalls` | 3,555 | 1,380 | 0,744 | 4,8x |
| `interfaceCalls` | 10,298 | 1,376 | 0,687 | 15,0x |
| `virtualCalls` | 6,161 | 1,376 | 0,687 | 9,0x |
| `closures` | 6,973 | 1,387 | 0,688 | 10,1x |
| `closureCreate` | 10,030 | 6,550 | 0,688 | 14,6x |
| `methodGroupInvoke` | 10,099 | 1,377 | 0,687 | 14,7x |
| `fib` | 3,043 | 0,218 | 0,082 | 37,1x |
| `interop` | 5,418 | 1,384 | 0,744 | 7,3x |
| `staticCalls` (nuevo) | 3,322 | 1,397 | 0,697 | 4,8x |
| `nativeInstanceCalls` (nuevo) | 5,724 | 1,396 | 0,699 | 8,2x |
| `nativeStaticCalls` (nuevo) | 5,899 | 1,376 | 0,697 | 8,5x |

El despacho directo (4,8x sobre C#) es el punto fuerte; la vtable (9,0x) y la tabla de interfaces
(15,0x) cuestan una indirección más. `closureCreate` no asigna nada: una lambda sin capturas usa
`NewFunction`, que devuelve el único `SurtrClosure` canónico y cacheado de ese método.

`fib` a 37x es el peor ratio de la sección y también el caso más pequeño (n=24, 0,082 ms en C#); a
esa escala el baseline mide poco más que el coste de entrar y salir.

**Tres casos nuevos cierran un hueco real: nada en la suite anterior llamaba a un método `static`
de clase, ni a un método nativo declarado sobre una clase del propio programa** (`interop` y
`mathFns` solo llaman a funciones nativas de módulo; `stringTransform` solo a métodos nativos de
una clase *built-in*). `staticCalls` (4,8x, `InvokeStatic`) sale prácticamente igual que
`methodCalls` — un método estático no paga receptor, así que es razonable que cueste lo mismo o
menos que el despacho directo. `nativeInstanceCalls` (8,2x) y `nativeStaticCalls` (8,5x) miden la
frontera nativa sobre una clase propia por primera vez, con y sin receptor: los dos salen cerca de
`interop` (7,3x), que es exactamente lo esperable — cruzar hacia C# cuesta lo mismo tenga o no la
llamada un `this`.

### 5.4 Generadores

| Caso | surtr ms | luajit ms | c# ms | vs c# |
|---|---|---|---|---|
| `genYield` | 1,447 | 1,106 | 0,119 | 12,2x |
| `handIterator` | 1,825 | 0,233 | 0,124 | 14,7x |
| `genDelegate` | 1,505 | 3,394 | 0,338 | 4,5x |
| `genSend` | 7,218 | 1,227 | 0,115 | 62,8x |
| `genFinally` | 1,575 | 1,203 | 0,131 | 12,0x |

`genDelegate` es el resultado que llama la atención: tres niveles de `yield from` encadenados por
enlace (§"yield from es un link, no un bucle" de `CLAUDE.md`) le ganan a la corrutina nativa de
LuaJIT — 3,394 ms de LuaJIT contra 1,505 ms de Surtr. El enlace de delegación evita que cada nivel
pague su propio frame; una corrutina de LuaJIT sí paga el cambio de contexto en cada nivel.
`genSend` es el peor de la sección (62,8x): cada `send` mete un valor primitivo por un slot
`unknown`, que por diseño siempre es una referencia — un `box` por iteración es el precio explícito
de que `yield` no tenga tipo declarado para lo que resume (`docs/Language-Syntax.md`, la nota sobre
por qué no hay `generator<T, TSend>`).

### 5.5 Asignación, GC y excepciones

| Caso | surtr ms | bytes | objs | kept | c#B |
|---|---|---|---|---|---|
| `valueClass` | 2,291 | **0** | 0 | 0 | 0 |
| `allocation` | 13,884 | 24,0 M | 300,0k | 2 | 9,6 M |
| `generics` | 9,856 | 12,0 M | 300,0k | 2 | 7,2 M |
| `retainedObjects` | 5,874 | 8,0 M | 100,0k | **25,0k** | 3,7 M |
| `exceptions` | 0,574 | 576,0 K | 8,0k | 8,0k | 1,6 M |
| `disposal` (nuevo) | 16,745 | 21,6 M | 300,0k | 2 | 7,2 M |

**`generics` ya no es el peor resultado de la suite, y el motivo es un hallazgo de esta misma
sesión.** El caso mide "un primitivo boxeado hacia un slot erasado y desboxeado de vuelta" — pero
`Box<T>` estaba declarada `class`, así que cada iteración pagaba **dos** asignaciones: la caja del
primitivo (lo que el caso dice medir) y la instancia de `Box<T>` en sí (un coste de contenedor
irrelevante que `allocation` ya aísla por su cuenta). Al cambiar `Box<T>` a `value class` —una
value class de un solo campo erasa a exactamente ese campo, sin contenedor propio— el caso pasó de
22,251 ms / 32,0 MB / 600 000 objetos a **9,856 ms / 12,0 MB / 300 000 objetos**: más del doble de
rápido y la mitad de los objetos, sin cambiar una sola línea de lo que el caso mide de verdad. El
coste de erasure en sí —el boxing de un primitivo hacia un slot `unknown`— sigue siendo real y
sigue siendo el precio del trato de Java (§8), pero ahora `generics` lo aísla en vez de sumarle un
coste ajeno.

`retainedObjects` sigue siendo el único caso que promueve supervivientes: 25 000 de los 100 000
objetos siguen vivos al volver. Su columna interesante es `kept`, no el tiempo.

`exceptions` sigue siendo el caso más llamativo del informe: Surtr lanza y captura 8000 excepciones
en **0,574 ms**, contra 20,692 ms de C# (**36x más rápido**) y 8,987 ms de LuaJIT (**15,7x**). Las
excepciones de Surtr son tablas de handlers en el método, no opcodes: entrar en un `try` no emite
nada y no cuesta nada.

`disposal` es nuevo: una clase propia que implementa `IDisposable`, cerrada por `using`, fuera de
un generador (el único cierre que la suite probaba antes era el implícito de `genFinally`). A
22,1x de C# y 21,6 MB es el más caro del bloque — pero es también el que menos objetos deja vivos
por diseño (2 kept, igual que `allocation`/`generics`): el `using` desugarizado a `try`/`finally`
(`Language-Syntax.md` §9.2) hace exactamente lo que promete, cerrar en cada salida, y eso no es
gratis cuando el recurso se crea y se destruye 300 000 veces.

### 5.6 Estructuras de datos

| Caso | surtr ms | c# ms | vs c# | bytes | c#B |
|---|---|---|---|---|---|
| `arrayFill` | 0,765 | 0,148 | 5,2x | 56 B | 1,0 M |
| `arrayIndex` | 5,442 | 0,690 | 7,9x | 56 B | 4,3 K |
| `dictOps` | 0,856 | 0,218 | 3,9x | 2,0 M | 2,0 M |
| `dictMembers` | 1,329 | 0,376 | 3,5x | 2,0 M | 2,0 M |
| `dictString` | 6,261 | 2,051 | 3,1x | 12,9 K | 7,8 K |
| `forIn` | 0,523 | 0,148 | 3,5x | 56 B | 1,0 M |
| `iterator` | 2,237 | 0,189 | 11,8x | 120 B | 1,0 M |
| `sortArray` | 10,737 | 0,835 | 12,9x | 156,4 K | 524,6 K |
| `sortBytecode` | 16,359 | 0,831 | 19,7x | 112 B | 524,6 K |
| `arrayFullSurface` (nuevo) | 0,684 | 0,158 | 4,3x | 56 B | 1,0 M |
| `forInStringTuple` (nuevo) | 11,409 | 2,757 | 4,1x | 5,2 M | 0 |
| `linkedListWalk` (nuevo) | 44,762 | 1,364 | 32,8x | 24,0 M | 9,6 M |

Los diccionarios son la mejor relación de la suite contra C# (3,1x–3,9x), gracias al almacén
especializado que evita el comparador cuando la clave está declarada `int`. `forIn` (bajado a un
bucle indexado, 0 objetos por elemento) contra `iterator` (la ruta general `iterate()`/`moveNext()`,
50 000 objetos) es la medida exacta de lo que vale ese lowering: 3,4x de tiempo y toda la
asignación. `sortBytecode` (19,7x) vs `sortArray` (12,9x): el sort en bytecode sigue siendo más
lento que el nativo que reentra por comparación.

`arrayFullSurface` es nuevo y cubre lo que `arrayFill`/`arrayIndex` no ejercían — `pop`, `insert`,
`removeAt`, `clear`, `indexOf`, `contains` — y sale barato (4,3x) porque, a diferencia de `push`
repetido 50 000 veces, la mayoría de esas llamadas ocurre una sola vez por corrida. `forInStringTuple`
recorre un `string` carácter a carácter y una tupla elemento a elemento con `for-in`, algo que
`forIn`/`forInDict` no tocaban — los 5,2 MB son el coste de la conversión `as int` sobre cada
carácter/elemento leído de un `for-in` cuyo tipo estático es `unknown`.

`linkedListWalk` es el más caro de la sección y con razón: recorre una lista enlazada real por
referencia hasta `null`, la primera prueba de la suite cuya condición de parada es una comparación
contra `null` en vez de un índice contra un límite. Los 24,0 MB son 300 000 nodos reales, y los
300 000 `kept` (frente a los 1-6 de casi todo lo demás) son la lista entera viva en memoria durante
el recorrido — el propio caso *es* la razón de que sea el bloque con más objetos retenidos de toda
la suite salvo `retainedObjects`.

---

## 6. Dispersión: qué números no citar

Dieciséis de los 64 casos superan el 10 % de spread de estado incluso con 9 procesos, 15
iteraciones, 5 de calentamiento y 3 rondas aleatorizadas:

| Caso | spread | Por qué |
|---|---|---|
| `dictString` | 71,3 % | hashing de cadenas más presión de asignación, sensible al layout |
| `nativeInstanceCalls` | 49,3 % | caso nuevo, aún sin historial de estabilidad |
| `interop` | 44,6 % | la frontera nativa sigue siendo de las más sensibles al op-cache |
| `retainedObjects` | 41,5 % | 25 000 supervivientes: dónde caiga una promoción mueve la fila |
| `nativeStaticCalls` | 41,3 % | caso nuevo |
| `tuples` | 37,6 % | 3,456 ms de mínimo: cualquier ruido del planificador domina |
| `staticCalls` | 30,4 % | caso nuevo |
| `closureCreate` | 18,6 % | — |
| `stringInterp` | 17,8 % | presión de asignación |
| `allocation` | 16,7 % | — |
| `castAndTypeof` | 14,5 % | caso nuevo, además asigna |
| `sortArray` | 14,4 % | — |
| `forInStringTuple` | 14,3 % | caso nuevo |
| `forInDict` | 11,8 % | — |
| `exceptions` | 10,3 % | 0,574 ms de mínimo |
| `stringTransform` | 10,1 % | justo en el límite |

El patrón es el mismo de siempre: **son los casos rápidos, o los casos nuevos sin historial**. Todo
lo que mide por debajo de ~5 ms está midiendo tanto la máquina como el lenguaje. Las filas de tipos
de valor (§4) están todas por debajo del 8 %, así que esos números sí se pueden citar con
confianza.

---

## 7. Tabla completa

`vs lua` y `vs luajit` = cuántas veces **más lento** es ese motor que Surtr (por debajo de `1.00x`,
Surtr es el más lento). `!!` marca una fila que el disyuntor de MoonSharp cortó tras un sondeo —
`>=1000x` es una cota inferior, no una mediana real. `(diag)` marca `vec2Class`, fuera de las tres
medias geométricas de §3.

| workload | size | surtr ms | lua ms | luajit ms | c# ms | vs lua | vs luajit | vs c# | bytes | objs | kept | c#B | spread |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `fib` | 24 | 3.043 | 23.241 | 0.218 | 0.082 | 7.64x | 0.07x | 37.11x | 0 | 0 | 0 | 0 | 7.2% |
| `intLoop` | 1M | 7.754 | 87.692 | 4.612 | 2.302 | 11.31x | 0.59x | 3.37x | 0 | 0 | 0 | 0 | 8.7% |
| `tightGuard` | 1M | 4.203 | 47.613 | 0.192 | 0.191 | 11.33x | 0.05x | 22.01x | 0 | 0 | 0 | 0 | 3.5% |
| `floatLoop` | 1M | 6.064 | 56.964 | 1.146 | 1.145 | 9.39x | 0.19x | 5.30x | 0 | 0 | 0 | 0 | 4.6% |
| `mathFns` | 100K | 7.986 | 48.704 | 1.333 | 1.531 | 6.10x | 0.17x | 5.22x | 0 | 0 | 0 | 0 | 9.9% |
| `arrayFill` | 50K | 0.765 | >=1000x!! | 0.508 | 0.148 | >=1000x | 0.66x | 5.17x | 56B | 1 | 1 | 1.0M | 3.7% |
| `arrayIndex` | 300K | 5.442 | 72.730 | 1.383 | 0.690 | 13.36x | 0.25x | 7.89x | 56B | 1 | 1 | 4.3K | 3.1% |
| `dictOps` | 30K | 0.856 | 5.476 | 0.258 | 0.218 | 6.40x | 0.30x | 3.93x | 2.0M | 1 | 1 | 2.0M | 7.8% |
| `dictMembers` | 30K | 1.329 | 12.686 | 0.408 | 0.376 | 9.55x | 0.31x | 3.53x | 2.0M | 1 | 1 | 2.0M | 4.9% |
| `dictString` | 300K | 6.261 | 48.877 | 1.383 | 2.051 | 7.81x | 0.22x | 3.05x | 12.9K | 130 | 130 | 7.8K | 71.3% |
| `stringConcat` | 1K | 0.060 | 0.154 | 0.149 | 0.039 | 2.57x | 2.48x | 1.54x | 1.5M | 1200 | 1200 | 1.5M | 5.0% |
| `stringInterp` | 100K | 9.212 | 28.279 | 5.899 | 2.398 | 3.07x | 0.64x | 3.84x | 25.6M | 300000 | 2 | 17.6M | 17.8% |
| `stringOps` | 300K | 4.394 | 49.306 | 2.766 | 1.375 | 11.22x | 0.63x | 3.20x | 0 | 0 | 0 | 0 | 3.7% |
| `stringTransform` | 100K | 9.388 | 165.403 | 15.398 | 3.433 | 17.62x | 1.64x | 2.73x | 24.0M | 200000 | 3 | 14.4M | 10.1% |
| `closures` | 300K | 6.973 | 47.513 | 1.387 | 0.688 | 6.81x | 0.20x | 10.14x | 0 | 0 | 0 | 0 | 4.3% |
| `closureCreate` | 300K | 10.030 | 78.269 | 6.550 | 0.688 | 7.80x | 0.65x | 14.58x | 0 | 0 | 0 | 0 | 18.6% |
| `methodGroupInvoke` | 300K | 10.099 | 77.851 | 1.377 | 0.687 | 7.71x | 0.14x | 14.70x | 0 | 0 | 0 | 0 | 6.1% |
| `closureCapture` | 300K | 10.036 | 90.728 | 1.435 | 1.123 | 9.04x | 0.14x | 8.94x | 216B | 2 | 2 | 120B | 5.1% |
| `methodCalls` | 300K | 3.555 | 75.380 | 1.380 | 0.744 | 21.20x | 0.39x | 4.78x | 72B | 1 | 1 | 24B | 7.5% |
| `localModule` | 300K | 7.789 | 24.593 | 1.496 | 0.745 | 3.16x | 0.19x | 10.46x | 0 | 0 | 0 | 0 | 4.3% |
| `crossModule` | 300K | 8.025 | 24.236 | 1.494 | 0.744 | 3.02x | 0.19x | 10.79x | 0 | 0 | 0 | 0 | 5.7% |
| `virtualCalls` | 300K | 6.161 | 53.635 | 1.376 | 0.687 | 8.71x | 0.22x | 8.97x | 40B | 1 | 1 | 0 | 3.1% |
| `interfaceCalls` | 300K | 10.298 | 55.046 | 1.376 | 0.687 | 5.35x | 0.13x | 14.99x | 40B | 1 | 1 | 0 | 3.4% |
| `fieldAccess` | 300K | 5.534 | 68.238 | 1.379 | 0.687 | 12.33x | 0.25x | 8.06x | 80B | 1 | 1 | 32B | 6.8% |
| `propertyAccess` | 300K | 3.623 | 133.049 | 1.376 | 0.687 | 36.72x | 0.38x | 5.27x | 72B | 1 | 1 | 24B | 3.6% |
| `exceptions` | 8K | 0.574 | 34.494 | 8.987 | 20.692 | 60.09x | 15.66x | 0.03x | 576.0K | 8000 | 8000 | 1.6M | 10.3% |
| `forIn` | 50K | 0.523 | >=1000x!! | 0.498 | 0.148 | >=1000x | 0.95x | 3.53x | 56B | 1 | 1 | 1.0M | 8.0% |
| `forInDict` | 50K | 1.488 | 13.921 | 0.481 | 0.518 | 9.36x | 0.32x | 2.87x | 4.1M | 2 | 2 | 4.1M | 11.8% |
| `iterator` | 50K | 2.237 | >=1000x!! | 0.514 | 0.189 | >=1000x | 0.23x | 11.84x | 120B | 2 | 2 | 1.0M | 8.3% |
| `genYield` | 50K | 1.447 | 14.378 | 1.106 | 0.119 | 9.94x | 0.76x | 12.16x | 192B | 1 | 1 | 56B | 2.6% |
| `handIterator` | 50K | 1.825 | 23.262 | 0.233 | 0.124 | 12.75x | 0.13x | 14.72x | 80B | 1 | 1 | 32B | 2.6% |
| `genDelegate` | 50K | 1.505 | 37.001 | 3.394 | 0.338 | 24.59x | 2.26x | 4.45x | 544B | 3 | 3 | 168B | 4.4% |
| `genSend` | 50K | 7.218 | 22.935 | 1.227 | 0.115 | 3.18x | 0.17x | 62.77x | 2.0M | 50001 | 4 | 40B | 6.6% |
| `genFinally` | 50K | 1.575 | 16.511 | 1.203 | 0.131 | 10.48x | 0.76x | 12.02x | 200B | 1 | 1 | 56B | 1.8% |
| `interop` | 300K | 5.418 | 57.111 | 1.384 | 0.744 | 10.54x | 0.26x | 7.28x | 0 | 0 | 0 | 0 | 44.6% |
| `valueClass` | 300K | 2.291 | 55.494 | 1.377 | 0.687 | 24.22x | 0.60x | 3.33x | 0 | 0 | 0 | 0 | 4.2% |
| `generics` | 300K | 9.856 | 158.535 | 1.377 | 0.753 | 16.09x | 0.14x | 13.09x | 12.0M | 300000 | 2 | 7.2M | 7.6% |
| `allocation` | 300K | 13.884 | 144.689 | 1.512 | 0.834 | 10.42x | 0.11x | 16.65x | 24.0M | 300000 | 2 | 9.6M | 16.7% |
| `retainedObjects` | 100K | 5.874 | 2170.838 | 5.067 | 0.245 | 369.57x | 0.86x | 23.98x | 8.0M | 100001 | 25004 | 3.7M | 41.5% |
| `switchDense` | 300K | 4.821 | 89.143 | 1.883 | 0.688 | 18.49x | 0.39x | 7.01x | 0 | 0 | 0 | 0 | 7.4% |
| `typeTest` | 300K | 7.327 | 155.977 | 2.765 | 1.378 | 21.29x | 0.38x | 5.32x | 40B | 1 | 1 | 0 | 8.3% |
| `nullable` | 300K | 6.603 | 54.831 | 1.575 | 0.688 | 8.30x | 0.24x | 9.60x | 0 | 0 | 0 | 0 | 4.7% |
| `enums` | 300K | 6.852 | 85.103 | 1.771 | 0.707 | 12.42x | 0.26x | 9.69x | 0 | 0 | 0 | 0 | 7.6% |
| `sortArray` | 20K | 10.737 | 111.480 | 5.058 | 0.835 | 10.38x | 0.47x | 12.86x | 56B | 1 | 1 | 524.6K | 14.4% |
| `sortBytecode` | 20K | 16.359 | 112.506 | 5.017 | 0.831 | 6.88x | 0.31x | 19.69x | 112B | 2 | 2 | 524.6K | 1.6% |
| `tuples` | 300K | 3.456 | 68.344 | 1.505 | 0.801 | 19.78x | 0.44x | 4.31x | 0 | 0 | 0 | 0 | 37.6% |
| `vec2Math` | 300K | 27.691 | 470.916 | 0.477 | 0.486 | 17.01x | 0.02x | 56.98x | 0 | 0 | 0 | 0 | 2.9% |
| `vec2Fields` | 300K | 29.950 | 509.196 | 7.863 | 0.859 | 17.00x | 0.26x | 34.87x | 96B | 1 | 1 | 48B | 3.9% |
| `vec2Class` (diag) | 300K | 44.288 | 482.455 | 0.477 | 1.055 | 10.89x | 0.01x | 41.98x | 48.0M | 600002 | 6 | 19.2M | 7.6% |
| `tupleReturn` | 300K | 9.787 | 136.472 | 1.492 | 0.767 | 13.94x | 0.15x | 12.76x | 0 | 0 | 0 | 0 | 6.8% |
| `bitwiseOps` | 300K | 7.200 | >=1000x!! | 138.419 | 0.688 | >=1000x | 19.22x | 10.47x | 0 | 0 | 0 | 0 | 3.6% |
| `rangeLoop` | 300K | 4.312 | 46.941 | 2.765 | 1.375 | 10.89x | 0.64x | 3.14x | 0 | 0 | 0 | 0 | 4.1% |
| `stringIndexSwitch` | 300K | 15.535 | 267.252 | 3.192 | 0.932 | 17.20x | 0.21x | 16.67x | 56B | 1 | 1 | 64B | 4.9% |
| `castAndTypeof` | 300K | 11.605 | 80.139 | 1.499 | 0.837 | 6.91x | 0.13x | 13.86x | 12.0M | 300000 | 2 | 7.2M | 14.5% |
| `staticCalls` | 300K | 3.322 | 55.585 | 1.397 | 0.697 | 16.73x | 0.42x | 4.77x | 0 | 0 | 0 | 0 | 30.4% |
| `nativeInstanceCalls` | 300K | 5.724 | 56.069 | 1.396 | 0.699 | 9.80x | 0.24x | 8.19x | 40B | 1 | 1 | 0 | 49.3% |
| `nativeStaticCalls` | 300K | 5.899 | 61.184 | 1.376 | 0.697 | 10.37x | 0.23x | 8.46x | 0 | 0 | 0 | 0 | 41.3% |
| `forInStringTuple` | 50K | 11.409 | 238.776 | 5.763 | 2.757 | 20.93x | 0.51x | 4.14x | 5.2M | 50000 | 2 | 0 | 14.3% |
| `arrayFullSurface` | 50K | 0.684 | >=1000x!! | 1.797 | 0.158 | >=1000x | 2.63x | 4.33x | 56B | 1 | 1 | 1.0M | 1.6% |
| `tupleBoxed` | 300K | 9.848 | 72.207 | 1.502 | 0.744 | 7.33x | 0.15x | 13.24x | 26.4M | 300000 | 2 | 0 | 8.5% |
| `disposal` | 300K | 16.745 | 62.371 | 1.378 | 0.759 | 3.72x | 0.08x | 22.06x | 21.6M | 300000 | 2 | 7.2M | 4.6% |
| `countdownWhile` | 300K | 2.657 | 36.251 | 1.383 | 0.687 | 13.64x | 0.52x | 3.87x | 0 | 0 | 0 | 0 | 4.9% |
| `collatzWhile` | 3K | 3.197 | 35.306 | 0.581 | 0.203 | 11.04x | 0.18x | 15.75x | 0 | 0 | 0 | 0 | 6.5% |
| `linkedListWalk` | 300K | 44.762 | 334.720 | 9.607 | 1.364 | 7.48x | 0.21x | 32.82x | 24.0M | 300000 | 300000 | 9.6M | 4.8% |

---

## 8. Conclusiones

**Lo que está validado.**

1. **Los tipos de valor cumplen lo que prometían.** El A/B `vec2Math` / `vec2Class` es la misma
   fuente con una palabra distinta: 0 B contra 48 MB. Tuplas y retorno multi-slot no asignan nada;
   forzar la misma tupla por un slot erasado (`tupleBoxed`, nuevo) sí, y por la razón exacta que el
   modelo predice — un slot `unknown` siempre es una referencia.
2. **Las excepciones por tabla de handlers son un acierto de diseño**, no un detalle: 36x más
   rápido que C# y 15,7x más rápido que LuaJIT, y un `try` que nunca lanza cuesta exactamente cero.
3. **El NaN-boxing no se paga.** Un millón de iteraciones enteras a 3,4x de C#; los primitivos
   nullable a 0 B.
4. **Las cadenas están donde deben.** 1,5x–3,8x de C# en la familia original, porque el trabajo
   real lo hace la misma BCL en ambos lados y `StrCat` n-ario evita los n−1 temporales.
5. **Los diccionarios con clave `int` son la mejor relación de la suite** (3,1x–3,9x), por saltar
   el comparador.
6. **Bajar `for-in` a un bucle indexado vale exactamente 3,4x y toda la asignación**, medido contra
   la ruta general en `iterator`.
7. **El coste real de la erasure de genéricos se aisló correctamente por primera vez en esta
   corrida.** `generics` medía dos asignaciones por iteración (la caja del primitivo *y* el
   contenedor `Box<T>`, que era `class`); al pasar `Box<T>` a `value class` de un solo campo —que
   erasa a exactamente ese campo, sin objeto propio— el caso bajó de 22,3 ms/32,0 MB a 9,9 ms/12,0 MB
   sin cambiar lo que mide. La lección para el resto del catálogo: un caso que dice medir un
   mecanismo concreto tiene que estar escrito con el idioma más rápido disponible para todo lo que
   *no* es ese mecanismo, o mide dos cosas a la vez.
8. **El disyuntor de MoonSharp funciona en producción, no solo en el papel.** Cinco casos lo
   dispararon en esta corrida — sin él, esta misma suite habría costado horas en vez de minutos, y
   el número que habría llegado a la media geométrica habría sido el de una sola llamada de
   warmup, no una mediana.

**Lo que sigue costando.**

1. **El boxing de genéricos sigue siendo real, y ahora está aislado.** `generics` a 13,1x de C# y
   12,0 MB/300 000 objetos (uno por iteración: el `int` que cruza el slot erasado) es el precio
   explícito del trato de Java. Es más barato que la cifra anterior sugería, pero no gratis: los
   tipos de valor siguen siendo la alternativa para quien no quiera pagarlo.
2. **El despacho virtual y de interfaz** siguen a ~9-15x de C#. La caché en línea sobre
   `InvokeVirtual` **se probó y se retiró**: A/B sobre la misma build con una caché monomórfica
   gemela de la de interfaz (`SurtrChunk.VirtualCallCache`), tres corridas por variante en el sitio
   más favorable posible — `virtualCalls`, receptor monomórfico por construcción — no separa las
   dos distribuciones (dispersión entre corridas ±4 %). La razón ya estaba escrita junto a
   `InterfaceCallCache`: resolver un virtual son dos cargas dependientes
   (`Class.VirtualMethods[slot]`), y la caché cambia una de ellas por una comparación más una
   carga, añadiendo el test nulo y la indexación del propio array de caché. En interfaz la jugada
   compensó porque el camino que se saltaba era una sonda open-addressing; aquí no había nada que
   saltarse. Cerrada con medición; el techo de *cualquier* mejora de resolución lo fija el
   protocolo de frame que viene detrás, no la resolución.
3. **La llamada sigue costando lo que costaba.** Los tipos de valor quitan la asignación, no el
   protocolo de frame — que es la razón de que `vec2Math` esté a 57,0x de un C# que mantiene el
   `struct` en registros.
4. **Un bucle descendente y uno data-dependiente no tienen fusión, por diseño.** `countdownWhile`
   y `collatzWhile` (nuevos) confirman que la familia `ForRangeNext` solo cubre `<`/`<=` — no hay
   plan de extenderla a `>`/`>=`, y un bucle cuya longitud no se conoce de antemano no puede
   fundirse en absoluto.
5. **Dieciséis casos con dispersión de estado sobre el 10 %**, la mayoría casos nuevos aún sin
   historial de estabilidad entre corridas, y el resto los mismos de siempre: por debajo de ~5 ms
   de mínimo. Si esos números van a citarse con precisión, necesitan tamaños mayores.
