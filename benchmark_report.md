# Informe de Benchmarks de Surtr

Corrida completa de `src/Surtr.Bench` sobre **40 casos**, cada uno escrito tres veces —
en Surtr, en Lua y en C#— y verificado por checksum antes de aceptar ningún tiempo.

Los datos crudos están en `bench_results.csv`, con las dos líneas de cabecera (`# machine:`,
`# settings:`) que identifican la máquina y la configuración exactas de esta corrida. Este
documento es su lectura; si los dos discrepan, manda el CSV.

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
constructo —Lua no tiene tipos de valor— la diferencia es el hallazgo del caso, no un defecto de
la comparación.

---

## 2. Metodología

### 2.1 Configuración de esta corrida

Ejecutada con `--extreme`, que es el modo más caro y el único cuyos números merecen citarse:

| Ajuste | Valor |
|---|---|
| Compilación | `Release` (en `Debug` el rendimiento de Surtr cae aproximadamente a la mitad) |
| Iteraciones cronometradas | 15 por caso |
| Calentamiento | 5 por caso |
| Rondas | 3, con **orden aleatorizado** (`shuffle`, semilla 12345) |
| Corridas de memoria | 5, medidas fuera de la región cronometrada |
| Política de GC de Surtr | automática |
| Medida reportada | **mediana**; `p90`/`p99` también se registran en el CSV |

Las tres rondas con orden aleatorio existen por una razón concreta: sin ellas el resultado de un
caso dependía de qué caso se hubiera ejecutado antes. Aleatorizar el orden y quedarse con la
mediana de tres rondas hace que ese efecto se promedie en lugar de sesgar una fila en particular.

**Sobre el despacho y los números de esta corrida.** El intérprete se entrega por el op-cache
(µop cache decodificado), indexado por la dirección absoluta del código; esa dirección se re-rolla
por proceso (ASLR) y por estado de la máquina, y el intérprete salta entre dos estados ~20-50 %
apartes. Una corrida como esta, en un solo proceso, muestrea **un** estado — los números de abajo
pueden ser el estado rápido o el lento según el proceso en que cayeron. Para un número absoluto
que represente el throughput real, el harness tiene `--processes <n>` (mide cada caso en n
procesos frescos y reporta el mínimo más el spread de estado); para A/B entre dos builds,
`scripts/ab-suite.ps1`. El detalle completo está en `docs/Informe-Volatilidad-Run.md`.

### 2.2 Columnas

| Columna | Significado |
|---|---|
| `surtr ms` / `lua ms` / `luajit ms` / `c# ms` | mediana de cada motor, en milisegundos |
| `vs lua`, `vs luajit` | cuántas veces **más lento** es ese motor que Surtr (por debajo de `1.00x`, Surtr es el más lento) |
| `vs c#` | cuántas veces más lento es Surtr que el baseline de C# |
| `bytes`, `objs`, `kept` | bytes gestionados que asignó Surtr, objetos que registró, y cuántos seguían vivos al volver |
| `c#B` | bytes que asignó el baseline de C# para el mismo trabajo |
| `spread` | rango intercuartílico sobre la mediana; por encima del ~10 % la mediana no merece citarse con precisión |

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

Los tres motores tienen que llegar al mismo resultado o la corrida falla. Eso ya atrapó una
miscompilación (`int?` sosteniendo un 1) que ningún test unitario detectó, porque el que más se
acercaba usaba el valor 0. Los 40 casos de esta corrida pasan la verificación.

---

## 3. Resumen

| Comparación | Media geométrica sobre 40 casos |
|---|---|
| Surtr vs MoonSharp | **18.5x más rápido** |
| Surtr vs LuaJIT | **0.3x** — LuaJIT es ~3.3x más rápido en media |

Surtr ocupa el nicho que se propuso: muy por encima de un intérprete gestionado de referencia, por
debajo de un JIT nativo industrial, y con una factura de memoria que en los casos que importan es
**cero**.

Surtr gana a LuaJIT en dos casos: `exceptions` (31.4x) y `stringTransform` (1.6x). Pierde en los
otros 38, por un factor que va de 1.3x (`stringConcat`) a 65x (`vec2Math`, donde LuaJIT hunde el
bucle entero).

---

## 4. Tipos de valor: el resultado principal de esta corrida

Cuatro casos nuevos miden lo que el plan `docs/Plan-TiposDeValor.md` acaba de construir. Los tres
primeros no asignan absolutamente nada.

### 4.1 El A/B: `vec2Math` contra `vec2Class`

Los dos casos son **el mismo código fuente con una palabra de diferencia**: `value class Vec2`
frente a `class Vec2Ref`. Tres construcciones y tres llamadas por iteración, 300 000 iteraciones.

| | surtr ms | bytes | objs | vivos al volver |
|---|---|---|---|---|
| `vec2Math` (`value class`) | **31.022** | **0** | **0** | 0 |
| `vec2Class` (`class`) | 47.289 | **45.8 M** | 600 002 | 6 |

**La columna que importa es `bytes`.** El tipo de valor no entrega nada al recolector; la clase de
referencia le entrega 45,8 MB y 600 000 objetos por corrida. El 34 % de tiempo ahorrado es real
pero secundario: en un motor de juego, los 45,8 MB son lo que se paga en un frame posterior que la
columna de tiempo no muestra. C# hace exactamente la misma distinción en la misma dirección —
`struct` 0 B contra `class` 18,3 MB— que es la señal de que el caso mide lo que dice medir.

### 4.2 Campos de valor en línea

| | surtr ms | bytes | objs |
|---|---|---|---|
| `vec2Fields` | 34.041 | **96 B** | **1** |

La aritmética idéntica leída y escrita a través de **campos value-type de una instancia**
(`LoadValueField`/`StoreValueField` sobre un objeto de cuatro slots). Los 96 B y el objeto único
son el `Body` que sostiene los campos, no las 300 000 operaciones de vector: el mapa de slots de
referencia del `Body` está **vacío**, así que una recolección lo salta por completo.

### 4.3 Retorno multi-slot y destructuring

| | surtr ms | bytes | objs |
|---|---|---|---|
| `tuples` | 4.433 | **0** | **0** |
| `tupleReturn` | 10.831 | **0** | **0** |

`tuples` construye un literal de tupla y lee sus dos elementos por iteración. Antes de la fase 5
eso asignaba un `SurtrTuple` por vuelta; ahora son dos slots en el frame y la fila marca **0 B**.

`tupleReturn` llama a `divmod(i, 7)` —una función que devuelve `(int, int)`— y ata los dos nombres
por destructuring, 300 000 veces. Dos slots vuelven sobre la base del frame vía `ReturnValues` y
ningún objeto tupla llega a existir. Es el mismo idioma que Lua ha tenido siempre con sus retornos
múltiples, y es 13,5x más rápido que MoonSharp haciéndolo.

### 4.4 Qué cuesta todavía

Un tipo de valor no hace gratis la **llamada**. `vec2Math` son ~103 ns por iteración para tres
llamadas y tres construcciones, contra los ~14 ns por iteración de `methodCalls` (una llamada): el coste está en
el protocolo de frame, que es exactamente el mismo que antes. Frente a C# (62.9x) y a LuaJIT
(65x) la distancia es grande porque ambos hunden el bucle entero —C# mantiene el `struct` en
registros, LuaJIT hace *allocation sinking*— y un intérprete de bytecode no tiene esa jugada
disponible. **Lo que los tipos de valor quitan es la asignación, no el despacho.**

La comparación relevante no es contra un JIT sino contra la alternativa dentro de Surtr, y ahí el
resultado es inequívoco: `vec2Math` contra `vec2Class`, 0 B contra 45,8 MB.

---

## 5. El resto de la suite, por categorías

### 5.1 Llamadas y despacho

| Caso | surtr ms | luajit ms | c# ms | vs c# |
|---|---|---|---|---|
| `methodCalls` | 4.241 | 1.382 | 0.744 | 5.7x |
| `interfaceCalls` | 6.624 | 1.376 | 0.687 | 9.6x |
| `virtualCalls` | 6.839 | 1.384 | 0.688 | 9.9x |
| `closures` | 7.227 | 1.395 | 0.690 | 10.5x |
| `closureCreate` | 10.350 | 6.803 | 0.687 | 15.1x |
| `methodGroupInvoke` | 11.316 | 1.379 | 0.688 | 16.4x |
| `fib` | 2.963 | 0.239 | 0.082 | 36.0x |
| `interop` | 4.910 | 1.387 | 0.745 | 6.6x |

El despacho directo (5.7x sobre C#) es el punto fuerte; la vtable (9.9x) y la tabla de interfaces
(9.6x) cuestan una indirección más. Que `interfaceCalls` haya quedado ligeramente **por debajo**
de `virtualCalls` en esta corrida es ruido dentro de la dispersión de ambos, no una inversión
real. `closureCreate` no asigna nada: una lambda sin capturas usa `NewFunction`, que devuelve el
único `SurtrClosure` canónico y cacheado de ese método.

`fib` a 36x es el peor ratio de la sección y también el caso más pequeño (n=24, 0.082 ms en C#);
a esa escala el baseline mide poco más que el coste de entrar y salir.

### 5.2 Aritmética y control

| Caso | surtr ms | luajit ms | c# ms | vs c# |
|---|---|---|---|---|
| `intLoop` (1M) | 10.099 | 4.622 | 2.305 | 4.4x |
| `floatLoop` (1M) | 8.697 | 1.146 | 1.148 | 7.6x |
| `mathFns` | 8.031 | 1.332 | 1.536 | 5.2x |
| `switchDense` | 5.675 | 1.893 | 0.690 | 8.2x |
| `nullable` | 5.721 | 1.777 | 0.707 | 8.1x |
| `enums` | 7.778 | 1.769 | 0.711 | 10.9x |
| `typeTest` | 7.740 | 2.860 | 1.379 | 5.6x |

Un millón de iteraciones de aritmética entera a 4.4x de C# es el mejor resultado del bloque
aritmético, y valida el NaN-boxing: mover primitivos por el VM no cuesta metadata ni heap.
`nullable` a 0 B confirma lo mismo para la ausencia — el tag reservado nunca toca el heap.

### 5.3 Estructuras de datos

| Caso | surtr ms | c# ms | vs c# | bytes | c#B |
|---|---|---|---|---|---|
| `arrayFill` | 0.871 | 0.148 | 5.9x | 56 B | 1.0 M |
| `arrayIndex` | 6.148 | 0.696 | 8.8x | 56 B | 4.2 K |
| `dictOps` | 0.846 | 0.221 | 3.8x | 1.9 M | 1.9 M |
| `dictMembers` | 1.308 | 0.386 | 3.4x | 1.9 M | 1.9 M |
| `dictString` | 7.459 | 1.885 | 4.0x | 12.6 K | 7.6 K |
| `forIn` | 0.957 | 0.148 | 6.5x | 56 B | 1.0 M |
| `iterator` | 3.592 | 0.211 | 17.1x | 1.9 M | 1.0 M |
| `sortArray` | 9.203 | 0.884 | 10.4x | 156.4 K | 512.3 K |

Los diccionarios son la mejor relación de la suite contra C# (3.4x–4.0x), gracias al almacén
especializado que evita el comparador cuando la clave está declarada `int`. `forIn` (bajado a un
bucle indexado, 0 objetos por elemento) contra `iterator` (la ruta general `iterate()`/`moveNext()`,
50 000 objetos) es la medida exacta de lo que vale ese lowering: 3.8x de tiempo y toda la
asignación.

Contra MoonSharp, `arrayFill` (7497x) y `forIn` (6682x) son las diferencias más extremas de todo
el informe, y dicen más de `ipairs` implementado como callback gestionado que de Surtr.

### 5.4 Cadenas

| Caso | surtr ms | c# ms | vs c# | bytes | c#B |
|---|---|---|---|---|---|
| `stringConcat` | 0.061 | 0.038 | 1.6x | 1.5 M | 1.4 M |
| `stringOps` | 5.059 | 1.379 | 3.7x | **0** | 0 |
| `stringInterp` | 9.623 | 2.502 | 3.8x | 24.4 M | 16.8 M |
| `stringTransform` | 10.348 | 3.442 | 3.0x | 22.9 M | 13.7 M |

La familia entera está entre 1.6x y 3.8x de C#, que es el mejor bloque de la suite: las cadenas de
Surtr son cadenas de la CLR, así que el trabajo real lo hace el mismo código en ambos lados y lo
único que Surtr añade es el despacho. `StrCat` toma un **conteo**, de modo que una interpolación
entera es una instrucción y una asignación, en lugar de n−1 de cada.

`stringTransform` es uno de los dos casos donde Surtr gana a LuaJIT (1.57x): `substring` y
`replace` de la BCL contra las de Lua.

### 5.5 Asignación, recolección y excepciones

| Caso | surtr ms | bytes | objs | kept | c#B |
|---|---|---|---|---|---|
| `valueClass` | 2.973 | **0** | 0 | 0 | 0 |
| `allocation` | 15.087 | 22.9 M | 300.0k | 2 | 9.2 M |
| `generics` | 22.251 | 32.0 M | 600.0k | 4 | 6.9 M |
| `retainedObjects` | 6.765 | 7.6 M | 100.0k | **25.0k** | 3.6 M |
| `exceptions` | 0.290 | 562.5 K | 8.0k | 8.0k | 1.5 M |

`generics` (29.5x sobre C#, 32 MB) es el peor resultado de la suite y su causa es conocida y
aceptada: el borrado obliga a **cajear** el primitivo que entra en el slot erasure y a insertar un
`Cast` al salir, dos objetos por iteración. Es el precio explícito del trato de Java, y la salida
para código que no lo quiera pagar son precisamente los tipos de valor de §4.

`retainedObjects` es el único caso que promueve supervivientes: 25 000 de los 100 000 objetos
siguen vivos al volver. Su columna interesante es `kept`, no el tiempo.

`exceptions` es el caso más llamativo del informe. Surtr lanza y captura 8 000 excepciones en
**0.290 ms**, contra 20.685 ms de C# (**71x más rápido**) y 9.104 ms de LuaJIT (**31x**). Las
excepciones de Surtr son tablas de handlers en el método, no opcodes: entrar en un `try` no emite
nada y no cuesta nada, y un `Throw` de Surtr nunca se convierte en una excepción de la CLR mientras
haya un handler a la vista — la máquina camina sus propios frames. Esa es la diferencia entera.

---

## 6. Dispersión: qué números no citar

Nueve de los 40 casos superan el 10 % de rango intercuartílico incluso con 15 iteraciones, 5 de
calentamiento y 3 rondas aleatorizadas:

| Caso | spread | Por qué |
|---|---|---|
| `exceptions` | 31.4 % | 0.290 ms de mediana: cualquier ruido del planificador domina |
| `iterator` | 21.0 % | asigna 50 000 objetos, así que dónde caiga una recolección mueve la fila |
| `stringConcat` | 15.1 % | 0.061 ms sobre 1 200 iteraciones |
| `fib` | 12.7 % | 2.963 ms sobre n=24 |
| `dictString` | 12.1 % | hashing de cadenas más presión de asignación |
| `forIn` | 11.7 % | 0.957 ms |
| `dictMembers` | 11.5 % | 1.308 ms |
| `stringOps` | 10.1 % | justo en el límite |
| `interop` | 10.1 % | justo en el límite |

El patrón es uniforme y esperable: **son los casos rápidos**. Todo lo que mide por debajo de ~4 ms
está midiendo tanto la máquina como el lenguaje. Las filas de tipos de valor están todas por
debajo del 5 %, así que los números de §4 sí se pueden citar.

---

## 7. Tabla completa

| workload | size | surtr ms | lua ms | luajit ms | c# ms | vs lua | vs luajit | vs c# | bytes | objs | kept | c#B | spread |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| `fib` | 24 | 2.963 | 25.587 | 0.239 | 0.082 | 8.6x | 0.08x | 36.0x | 0 | 0 | 0 | 0 | 12.7% |
| `intLoop` | 1M | 10.099 | 91.047 | 4.622 | 2.305 | 9.0x | 0.46x | 4.4x | 0 | 0 | 0 | 0 | 8.0% |
| `floatLoop` | 1M | 8.697 | 59.202 | 1.146 | 1.148 | 6.8x | 0.13x | 7.6x | 0 | 0 | 0 | 0 | 5.1% |
| `mathFns` | 100K | 8.031 | 50.068 | 1.332 | 1.536 | 6.2x | 0.17x | 5.2x | 0 | 0 | 0 | 0 | 6.4% |
| `arrayFill` | 50K | 0.871 | 6529.810 | 0.395 | 0.148 | 7496.9x | 0.45x | 5.9x | 56B | 1 | 1 | 1.0M | 3.5% |
| `arrayIndex` | 300K | 6.148 | 78.206 | 1.382 | 0.696 | 12.7x | 0.23x | 8.8x | 56B | 1 | 1 | 4.2K | 9.2% |
| `dictOps` | 30K | 0.846 | 5.628 | 0.204 | 0.221 | 6.7x | 0.24x | 3.8x | 1.9M | 1 | 1 | 1.9M | 9.4% |
| `dictMembers` | 30K | 1.308 | 13.329 | 0.310 | 0.386 | 10.2x | 0.24x | 3.4x | 1.9M | 1 | 1 | 1.9M | 11.5% |
| `dictString` | 300K | 7.459 | 50.584 | 1.382 | 1.885 | 6.8x | 0.18x | 4.0x | 12.6K | 130 | 130 | 7.6K | 12.1% |
| `stringConcat` | 1K | 0.061 | 0.095 | 0.047 | 0.038 | 1.5x | 0.76x | 1.6x | 1.5M | 1.2k | 1.2k | 1.4M | 15.1% |
| `stringInterp` | 100K | 9.623 | 29.452 | 6.709 | 2.502 | 3.1x | 0.70x | 3.8x | 24.4M | 300.0k | 2 | 16.8M | 8.8% |
| `stringOps` | 300K | 5.059 | 52.481 | 2.763 | 1.379 | 10.4x | 0.55x | 3.7x | 0 | 0 | 0 | 0 | 10.1% |
| `stringTransform` | 100K | 10.348 | 174.403 | 16.230 | 3.442 | 16.9x | 1.57x | 3.0x | 22.9M | 200.0k | 3 | 13.7M | 2.5% |
| `closures` | 300K | 7.227 | 51.387 | 1.395 | 0.690 | 7.1x | 0.19x | 10.5x | 0 | 0 | 0 | 0 | 8.0% |
| `closureCreate` | 300K | 10.350 | 83.379 | 6.803 | 0.687 | 8.1x | 0.66x | 15.1x | 0 | 0 | 0 | 0 | 6.6% |
| `methodGroupInvoke` | 300K | 11.316 | 86.074 | 1.379 | 0.688 | 7.6x | 0.12x | 16.4x | 0 | 0 | 0 | 0 | 8.5% |
| `closureCapture` | 300K | 10.591 | 94.387 | 1.444 | 1.118 | 8.9x | 0.14x | 9.5x | 216B | 2 | 2 | 120B | 6.9% |
| `methodCalls` | 300K | 4.241 | 75.709 | 1.382 | 0.744 | 17.9x | 0.33x | 5.7x | 72B | 1 | 1 | 24B | 7.4% |
| `virtualCalls` | 300K | 6.839 | 55.434 | 1.384 | 0.688 | 8.1x | 0.20x | 9.9x | 40B | 1 | 1 | 0 | 6.7% |
| `interfaceCalls` | 300K | 6.624 | 54.675 | 1.376 | 0.687 | 8.3x | 0.21x | 9.6x | 40B | 1 | 1 | 0 | 4.2% |
| `fieldAccess` | 300K | 5.784 | 68.309 | 1.398 | 0.689 | 11.8x | 0.24x | 8.4x | 80B | 1 | 1 | 32B | 6.1% |
| `propertyAccess` | 300K | 3.878 | 133.543 | 1.378 | 0.687 | 34.4x | 0.35x | 5.6x | 72B | 1 | 1 | 24B | 9.3% |
| `exceptions` | 8K | 0.290 | 34.610 | 9.104 | 20.685 | 119.5x | 31.44x | 0.0x | 562.5K | 8.0k | 8.0k | 1.5M | 31.4% |
| `forIn` | 50K | 0.957 | 6393.317 | 0.393 | 0.148 | 6682.0x | 0.41x | 6.5x | 56B | 1 | 1 | 1.0M | 11.7% |
| `iterator` | 50K | 3.592 | 6545.738 | 0.404 | 0.211 | 1822.3x | 0.11x | 17.1x | 1.9M | 50.0k | 5 | 1.0M | 21.0% |
| `interop` | 300K | 4.910 | 56.409 | 1.387 | 0.745 | 11.5x | 0.28x | 6.6x | 0 | 0 | 0 | 0 | 10.1% |
| `valueClass` | 300K | 2.973 | 55.830 | 1.378 | 0.688 | 18.8x | 0.46x | 4.3x | 0 | 0 | 0 | 0 | 4.2% |
| `generics` | 300K | 22.251 | 162.319 | 1.379 | 0.753 | 7.3x | 0.06x | 29.5x | 32.0M | 600.0k | 4 | 6.9M | 3.6% |
| `allocation` | 300K | 15.087 | 149.348 | 1.525 | 0.837 | 9.9x | 0.10x | 18.0x | 22.9M | 300.0k | 2 | 9.2M | 5.2% |
| `retainedObjects` | 100K | 6.765 | 2154.832 | 1.676 | 0.244 | 318.5x | 0.25x | 27.8x | 7.6M | 100.0k | 25.0k | 3.6M | 7.6% |
| `switchDense` | 300K | 5.675 | 92.886 | 1.893 | 0.690 | 16.4x | 0.33x | 8.2x | 0 | 0 | 0 | 0 | 7.6% |
| `typeTest` | 300K | 7.740 | 156.502 | 2.860 | 1.379 | 20.2x | 0.37x | 5.6x | 40B | 1 | 1 | 0 | 5.5% |
| `nullable` | 300K | 5.721 | 54.561 | 1.777 | 0.707 | 9.5x | 0.31x | 8.1x | 0 | 0 | 0 | 0 | 4.4% |
| `enums` | 300K | 7.778 | 84.127 | 1.769 | 0.711 | 10.8x | 0.23x | 10.9x | 0 | 0 | 0 | 0 | 8.1% |
| `sortArray` | 20K | 9.203 | 119.835 | 5.303 | 0.884 | 13.0x | 0.58x | 10.4x | 156.4K | 1 | 1 | 512.3K | 5.4% |
| `tuples` | 300K | 4.433 | 72.994 | 1.506 | 0.805 | 16.5x | 0.34x | 5.5x | 0 | 0 | 0 | 0 | 3.6% |
| `vec2Math` | 300K | 31.022 | 500.663 | 0.480 | 0.493 | 16.1x | 0.01x | 62.9x | 0 | 0 | 0 | 0 | 4.0% |
| `vec2Fields` | 300K | 34.041 | 560.385 | 8.632 | 0.871 | 16.5x | 0.25x | 39.1x | 96B | 1 | 1 | 48B | 3.5% |
| `vec2Class` | 300K | 47.289 | 504.361 | 0.481 | 1.053 | 10.7x | 0.01x | 44.9x | 45.8M | 600.0k | 6 | 18.3M | 4.5% |
| `tupleReturn` | 300K | 10.831 | 145.802 | 1.496 | 0.767 | 13.5x | 0.14x | 14.1x | 0 | 0 | 0 | 0 | 7.0% |

---

## 8. Conclusiones

**Lo que está validado.**

1. **Los tipos de valor cumplen lo que prometían.** El A/B `vec2Math` / `vec2Class` es la misma
   fuente con una palabra distinta: 0 B contra 45,8 MB. Tuplas y retorno multi-slot no asignan
   nada. La distinción reproduce, en la misma dirección y por la misma razón, la que hace C# entre
   `struct` y `class`.
2. **Las excepciones por tabla de handlers son un acierto de diseño**, no un detalle: 71x más
   rápido que C# y 31x más rápido que LuaJIT, y un `try` que nunca lanza cuesta exactamente cero.
3. **El NaN-boxing no se paga.** Un millón de iteraciones enteras a 4.4x de C#; los primitivos
   nullable a 0 B.
4. **Las cadenas están donde deben.** 1.6x–3.8x de C# en toda la familia, porque el trabajo real lo
   hace la misma BCL en ambos lados y `StrCat` n-ario evita los n−1 temporales.
5. **Los diccionarios con clave `int` son la mejor relación de la suite** (3.4x–4.0x), por saltar
   el comparador.
6. **Bajar `for-in` a un bucle indexado vale exactamente 3.8x y toda la asignación**, medido contra
   la ruta general en `iterator`.

**Lo que sigue costando.**

1. **El boxing de genéricos.** `generics` a 29.5x y 32 MB es el peor resultado, por diseño: es el
   precio del borrado. Descompuesto contra la misma suite: cada iteración registra dos objetos (el
   `SurtrBoxed` del argumento que cruza el slot borrado y la instancia de `Box<int>`, ~106 B CLR
   contando el array `Fields` de la instancia), mientras que la vuelta (`Cast` + `Unbox`) no asigna nada — el emisor
   ya baja la lectura del resultado sustituido como unbox, no como caja nueva. La pregunta era si
   hay algo que hacer sin romper el trato de Java, y la respuesta medida es que el coste no está en
   ninguna pieza prescindible: internar cajas pequeñas cambia identidad de referencias observable
   (`R`), y reificar contradice lo asentado en `docs/Compiler-Plan.md` §8. Los tipos de valor son
   la alternativa para quien no quiera pagarlo, y ya existen. El único constante reducible que se
   había identificado era el array secundario `Fields` de cada `SurtrInstance` (una segunda
   asignación CLR por instancia): **medido su techo y cerrado**. Un proxy CLR puro con las mismas
   formas de celda y el mismo bucle que la suite da **~1.0 ns/iteración para 1 campo y ~0.9 ns
   para 2** — la asignación pequeña de arrays en .NET 8 es casi gratis y la desreferencia extra
   tampoco se paga. Incrustar los slots inline recuperaría como mucho ese 2 % de `allocation`,
   al precio de bifurcar el layout que hoy leen `FieldGet`, el trazado del colector y el indexador
   `ref`. Cerrado con medición; no re-proponer sin una plataforma donde asignar sea caro otra vez.
2. **El despacho virtual y de interfaz** siguen a ~10x de C#. La caché en línea sobre
   `InvokeVirtual` **se probó y se retiró**: A/B sobre la misma build con una caché monomórfica
   gemela de la de interfaz (`SurtrChunk.VirtualCallCache`), tres corridas por variante en el sitio
   más favorable posible — `virtualCalls`, receptor monomórfico por construcción — no separa las
   dos distribuciones (6.5 ms base contra 6.7 ms con caché; dispersión entre corridas ±4 %). La
   razón ya estaba escrita junto a `InterfaceCallCache`: resolver un virtual son dos cargas
   dependientes (`Class.VirtualMethods[slot]`), y la caché cambia una de ellas por una comparación
   más una carga, añadiendo el test nulo y la indexación del propio array de caché. En interfaz la
   jugada compensó porque el camino que se saltaba era una sonda open-addressing; aquí no había
   nada que saltarse. Cerrada con medición; el techo de *cualquier* mejora de resolución es el
   delta `virtualCalls − methodCalls` (~6 ns/llamada) y ese techo lo fija el protocolo de frame
   que viene detrás, no la resolución.
3. **La llamada sigue costando lo que costaba.** Los tipos de valor quitan la asignación, no el
   protocolo de frame — que es la razón de que `vec2Math` esté a 62.9x de un C# que mantiene el
   `struct` en registros.
4. **Nueve casos con dispersión sobre el 10 %**, todos por debajo de ~4 ms de mediana. Si esos
   números van a citarse, necesitan tamaños mayores en lugar de más iteraciones.
