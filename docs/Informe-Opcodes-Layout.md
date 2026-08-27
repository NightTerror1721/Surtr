# Informe: layout nativo de `Run()` y presupuesto real de opcodes

**Fecha:** 2026-08-27, ampliado el 2026-08-28 · **Estado:** medición sobre el binario; los cuatro caminos de §7 están implementados y verificados.
**Complementa** `docs/Informe-Opcodes-Eliminables.md`, del que corrige ocho puntos. Ese informe mide
**calor dinámico** (cuántas veces se ejecuta cada opcode); éste mide **dónde vive cada opcode en el
código máquina** y qué cuesta esa disposición. Los dos juntos son lo que hace falta para decidir:
el calor dice qué importa, el layout dice qué se puede mover.

## 0. Método

Nada aquí es estimación. Todo sale de tres extracciones sobre el binario `Release`:

1. **IL.** Un decodificador propio (`System.Reflection.Metadata` más la tabla de operandos de
   `System.Reflection.Emit.OpCodes`) desensambla `SurtrVirtualMachine.Run`, construye el grafo de
   flujo a nivel de instrucción y atribuye cada bloque al `case` que lo alcanza en exclusiva
   (parando el recorrido en la etiqueta `Dispatch`, que es donde acaba un cuerpo por definición).
2. **Nativo.** `DOTNET_JitDisasm=Run` más `DOTNET_JitStdOutFile` sobre `surtrbench`. El volcado
   **incluye la tabla de saltos** (`RWD00`, 256 entradas de 4 bytes relativas a `G_M000_IG02`), que
   es lo que permite mapear **valor de opcode → dirección nativa exacta**, sin heurísticas.
3. **Clasificación caliente/frío dentro de un cuerpo.** Cada bloque `G_M000_IGnn` del volcado se
   marca según contenga o no una instrucción `call`. En un intérprete una llamada es, por
   construcción, camino lento: separa el núcleo del cuerpo de su ruta de escape.

El calor por opcode se toma de `docs/Informe-Opcodes-Eliminables.md` (296 477 444 despachos de la
suite de 50 workloads). Medidas de referencia del binario actual (`ceabd65`):

| | |
|---|---|
| IL de `Run()` | 24 425 B |
| código nativo de `Run()` | **39 983 B** |
| tabla de saltos | 256 entradas × 4 B = 1 024 B en `.rdata` |
| datos de perfil que usa el JIT | **ninguno** (`; No PGO data`) |
| cuerpos atribuidos | 254 (241 primarios y 13 extendidos), 99,3 % del método |

## 1. El hallazgo que reordena las prioridades: el valor del opcode no controla el layout

Correlación de rangos entre las tres ordenaciones posibles de los 241 cuerpos primarios:

| par | Spearman ρ | inversiones |
|---|---|---|
| orden en el fuente ↔ orden en el IL | 1,0000 | 0 / 28 920 |
| orden en el IL ↔ **orden de direcciones nativas** | **1,0000** | **0 / 28 920** |
| **valor del opcode ↔ dirección nativa** | **0,7062** | — |

**El JIT emite los cuerpos exactamente en orden de fuente, sin una sola inversión.** Y lo hace
porque `Run()` lleva `[MethodImpl((MethodImplOptions)512)]` (`AggressiveOptimization`), que la
excluye de la compilación por niveles y por tanto **del PGO dinámico**: el volcado dice
`; No PGO data`, así que el JIT no tiene contadores con los que reordenar bloques y cae en su
heurística estática, que respeta el orden del IL.

Dos consecuencias operativas:

- **Renumerar el enum por calor no gana localidad de código.** ρ = 0,71 entre valor y dirección es
  el residuo de que el fuente está agrupado por familias y las familias están numeradas por
  familias. Mover un opcode de `0xBB` a `0x03` no mueve un solo byte de su cuerpo.
- **Mover el bloque `case` en el fuente sí da control 1:1 sobre el layout nativo.** Es la única
  palanca de disposición que existe, y es exacta.

Corolario menos obvio: el día que `Run()` dejara de llevar `AggressiveOptimization`, el JIT tendría
perfil y haría esta reordenación solo. No es una opción — el atributo está ahí porque un método de
este tamaño compilado en nivel 0 durante los primeros treinta despachos es peor que cualquier
layout. Pero conviene saber que **el trabajo de reordenación es exactamente lo que se compra al
renunciar al PGO**, y que hay que hacerlo a mano precisamente por eso.

## 2. La secuencia de despacho

```asm
G_M000_IG05:                       ; etiqueta Dispatch
       lea      rdx, [r15+0x01]    ; ip + 1
       mov      r8, rdx
       movzx    rdx, byte ptr [r15]; opcode
       cmp      edx, 255           ; comprobación de rango: nunca se toma
       ja       G_M000_IG809       ;   (un movzx de byte no puede pasar de 255)
       lea      r10, [reloc @RWD00]
       mov      r10d, dword ptr [r10+4*rdx]
       lea      r9, G_M000_IG02
       add      r10, r9
       jmp      r10
```

Tres cosas se leen de aquí:

- **El despacho es O(1) por tabla.** Añadir o quitar valores del enum no cambia el coste de la
  búsqueda. Toda ganancia de "reducir el número de opcodes" es de formato y de layout, nunca de
  despacho — lo que ya decía el informe anterior en su punto 7, y aquí queda confirmado en el
  código máquina.
- **El `cmp edx, 255 / ja` es una comprobación muerta**: 2 µops por despacho, unas 296 M veces en la
  suite. No está en la cadena dependiente del `jmp r10` (que va `movzx` → carga de tabla → `add`),
  así que su coste real es de emisión, no de latencia. No es un objetivo, pero conviene saber que
  está ahí y que sobrevive a cualquier renumeración: el JIT lo emite contra el máximo del enum, no
  contra el rango probado del byte.
- **La tabla de saltos ya tiene buena localidad.** Ponderando sus 16 líneas de caché por calor, el
  79 % de los despachos cae en 5 líneas y el 100 % en 15. Es 1 KB residente en L1d. Reordenar
  valores para "juntar los calientes en la tabla" es una ganancia de aproximadamente cero, y es
  otra razón por la que la renumeración no es el camino.

| línea (16 entradas) | valores | despachos | % |
|---|---|---|---|
| 1 | 0x10–0x1F | 98 591 708 | 33,25 |
| 2 | 0x20–0x2F | 47 720 832 | 16,10 |
| 3 | 0x30–0x3F | 39 967 644 | 13,48 |
| 0 | 0x00–0x0F | 26 058 428 | 8,79 |
| 4 | 0x40–0x4F | 22 175 000 | 7,48 |
| 10 | 0xA0–0xAF | 18 817 434 | 6,35 |
| 11 | 0xB0–0xBF | 15 464 758 | 5,22 |
| resto (9 líneas) | | 27 697 641 | 9,33 |

## 3. El intérprete cabe en 1,9 KB y vive esparcido por 29 KB

Separando, dentro de cada cuerpo, los bloques con `call` de los que no:

```
los 32 opcodes que concentran el 90 % de los despachos:
      2 538 B de código propio
    - 1 889 B de núcleo real sin llamadas  ....... 30 líneas de caché
    -   649 B de camino lento incrustado
    ... repartidos sobre 29 078 B de espacio de direcciones (0x151-0x72E7)
    ... con 26 633 B de código frío intercalado  =>  dilución 11,5x
```

Y el 99 % del calor lo cubren 68 opcodes con 8 316 B de código.

Huella de caché de instrucciones, tal como está hoy y como quedaría con los cuerpos ordenados por
calor (simulación exacta, sumando los tamaños medidos en el orden nuevo):

| top-k | líneas de 64 B hoy | B de I-cache | páginas de 4 K | hot-first: líneas | B | páginas |
|---|---|---|---|---|---|---|
| 10 | 14 | 896 | 2 | **8** | 512 | **1** |
| 20 | 41 | 2 624 | 5 | **24** | 1 536 | **1** |
| 32 (90 % del calor) | 57 | 3 648 | 6 | **40** | 2 560 | **1** |
| 50 | 95 | 6 080 | 7 | **71** | 4 544 | 2 |
| 68 (99 % del calor) | 166 | 10 624 | 8 | **130** | 8 320 | 3 |

La lectura correcta de esta tabla no es "ahorramos 1 KB de L1i" — 3,6 KB caben de sobra en una L1i
de 32 K. Es **la cuenta de páginas** (6 → 1 para el 90 % del calor, que es presión de iTLB y del
prefetcher) y **la densidad del caché de µops**, que se indexa por ventanas de código y penaliza
exactamente esto: 2,5 KB útiles esparcidos por 29 KB.

> **Corregido tras medir (§7.5).** Este informe conjeturaba aquí que la dispersión era también la
> explicación de la bimodalidad de ±20-45 % de `docs/Informe-Volatilidad-Run.md` §1, y que
> compactar el camino caliente la reduciría. **No lo hace.** El A/B contra `ceabd65` marca los
> mismos trece casos como bimodales en los dos lados. La conjetura no se sostiene y queda retirada:
> la bimodalidad vive en el predictor del salto indirecto y en la dirección absoluta que el ASLR
> re-tira por proceso, no en cuánto ocupa el camino caliente.

### 3.1 El defecto que ninguna reordenación de `case` arregla

Cuatro de los cuerpos más calientes llevan su camino lento **dentro**:

| opcode | B totales | núcleo | camino lento incrustado | ejecuciones |
|---|---|---|---|---|
| `FieldSet` | 519 | 243 | **276** | 3 550 012 |
| `FieldGet` | 418 | 233 | **185** | 5 925 002 |
| `StaticFieldGet` | 361 | ~40 | ~320 | 800 000 |
| `ReturnValue` | 232 | 148 | 84 | 5 237 641 |

`FieldGet` es el caso de manual. En `SurtrVirtualMachine.cs` la rama
`if (field is SurtrNativeFieldInfo nativeField)` está escrita **antes** del acceso ordinario, así
que el JIT la coloca en medio:

```
0x4162  IG356      52 B   lee el índice, carga el SurtrFieldInfo, test de tipo -> salta a IG362
0x4196  IG357      19 B   comparación del método-tabla de SurtrNativeFieldInfo
0x41A9  IG358-61  247 B   TODO el camino nativo: get_Getter, SurtrCallArguments, Invoke, safepoint
0x4299  IG362-64  107 B   <- el acceso a campo ordinario, que es el camino caliente
```

El camino caliente son unos 150 B en dos regiones separadas por 260 B de código frío, con un salto
hacia delante que las cruza. `FieldSet` repite el patrón con 276 B en medio. El arreglo es el que
el repositorio ya usa: sacar la rama a un helper `NoInlining`, como hacen `HandleNullableOp`,
`HandleRangeOp`, `HandleModuleOp` y `HandleObjectOp` desde `ceabd65`.

### 3.2 Los comparadores de valor: por qué no se eliminan y qué hay que hacer con ellos

La pregunta obvia mirando la tabla de calor es si los comparadores que **no** ramifican (`EQ`, `NE`,
`GT`, `GE`, `LT`, `LE` y sus doce parientes de float, referencia, texto y dinámico) sobran, dado que
existe la forma fusionada `JP*`. Son 18 opcodes, 1 535 B nativos y 1 644 790 ejecuciones (0,55 %).
La respuesta medida es que **no**, y por cuatro razones acumulativas:

1. **Ya están fusionados donde se puede.** `MethodBodyEmitter.Condition` llama a
   `TryFusedComparison` y, cuando acierta, emite `JumpIfCompare` en vez de `Compare`. Las 1,6 M de
   ejecuciones que quedan son el **residuo irreducible**: comparaciones cuyo booleano es un *valor*
   — `let ok = a < b`, `return a == b`, un argumento, un campo `bool`, un elemento de array. No hay
   condición que fusionar ahí porque no hay salto.

2. **El cuerpo es branchless y la sustitución no lo sería.** `GT` en `0x00C82` es
   `cmp / setg / movzx / or / mov`: seis instrucciones, ni un salto. La secuencia equivalente
   (`JPGT Ltrue; PushFalse; JP Lend; Ltrue: PushTrue`) son **2-3 despachos en vez de 1**, 8 bytes de
   bytecode en vez de 3, uno o dos decrementos extra del presupuesto de pasos (`JP*` sale por
   `Branched`, `GT` por `Dispatch`) y, sobre todo, **un salto condicional dependiente de datos donde
   había un `setcc`**. Y es dependiente de datos por construcción: si fuera predecible, el
   compilador ya lo habría fusionado como condición en el punto 1.

3. **Dos de los dieciocho no tienen forma con salto.** `JPDynEQ` y `JPDynNE` **no existen** en
   `OpCode.cs`. `DynEQ`/`DynNE` (los que resuelven igualdad por tag sobre ranuras borradas) no son
   sustituibles: retirarlos exigiría *añadir* dos opcodes nuevos. El ahorro real serían 16 valores,
   no 18.

4. **`<=>` los necesita.** El operador de comparación de tres vías se baja con `Compare(Greater)` y
   `Compare(Less)` (`MethodBodyEmitter.cs`): dos booleanos que se combinan en un entero. No hay
   salto en ninguno de los dos.

Orden de magnitud del cambio: unos **+2,4 M de despachos** (+0,8 % del recuento de la suite) más las
fallas de predicción de un salto que es una moneda al aire, a cambio de 16 valores del espacio y
1 535 B — cuando el prefijo `Wide` de §6 libera 38 valores sin tocar nada caliente.

**Pero la intuición apunta a la familia correcta, sólo que la operación es mover, no borrar.** La
ventana `0x500–0xE80`, que es literalmente el corazón del intérprete (los `Stl*`, `IncLocal`, toda la
aritmética y todos los comparadores), está así:

| | cuerpos | bytes | |
|---|---|---|---|
| caliente | 19 | **794 B** | 29 % |
| frío | 22 | **1 969 B** | 71 % |

La ventana abarca 38 líneas de caché; lo caliente que contiene cabría en **13**. Y el bloque frío
más grande es exactamente el que señala la pregunta:

```
0x0081F  EQ        48 B  <- caliente (800 000)
0x0084F  FEQ       58 B     frio
0x00889  REQ       48 B     frio
0x008B9  StrEQ    280 B     frio  ]
0x009D1  NE        48 B     frio  ]
0x00A01  FNE       58 B     frio  ]  815 B de StrEQ/StrNE/DynEQ/DynNE
0x00A3B  RNE       48 B     frio  ]  con cero ejecuciones, partiendo
0x00A6B  StrNE    279 B     frio  ]  la familia de comparadores en dos
0x00B82  DynEQ    128 B     frio  ]
0x00C02  DynNE    128 B     frio  ]
0x00C82  GT        48 B     frio
0x00CE7  GE        48 B  <- caliente (300 000)
0x00D4C  LT        48 B  <- caliente (276 022)
0x00DB5  LE        48 B  <- caliente (268 768)
```

`EQ` y `GE`/`LT`/`LE` son el mismo patrón de uso y están separados por 1 123 bytes de código que no
se ejecuta ni una vez. Con `FSub`/`FDiv`/`FMod`/`Neg`/`FNeg` (186 B) entre `Sub` y `Inv`, y con
`InstanceOf` (352 B) pegado detrás de `IsNotNull`, esta ventana es el mejor caso de prueba que hay
para el camino 2 de §7: **junta `EQ`/`GE`/`LT`/`LE` con la aritmética y manda `Str*`/`Dyn*`/`F*` no
usados al fondo, y 38 líneas de caché pasan a 13 sin retirar un solo opcode.**

## 4. Correcciones a `Informe-Opcodes-Eliminables.md`

Verificadas contra el código, no contra la tabla del informe.

1. **`ArrNewX` no es un gemelo ancho y no es eliminable.** El informe lo lista entre los "~30 pares
   `X`". Su propio `///` en `OpCode.cs` dice lo contrario: *"not a widened `ArrNew` but a different
   addressing mode — the length moves from the stack into the instruction"*. Su codificación es
   `opcode(1) typeIdx(2) size(4)`, no `typeIdx(4)`. Comparando la lista de campos de cada `X` con la
   de su forma base, **39 de los 40 son gemelos anchos genuinos y `ArrNewX` es el único que no**.
   Medido: enrutar `ArrNewX` al camino de error rompe **53 tests**; enrutar los otros 39 rompe 15,
   y esos 15 son sus propios tests dedicados.

2. **`InvokeSpecial` no tiene a `InvokeVirtual` como alternativa.** El informe le da Elim 30 con esa
   nota, siendo el 19.º opcode más caliente (5 108 074 ejecuciones, 1,72 %). Un método
   `SurtrMethodDispatch.Direct` **no tiene ranura de vtable**: no hay nada que `InvokeVirtual`
   pueda resolver. Su cuerpo son siete líneas que caen en la secuencia compartida `InvokeResolved`.
   Elim real ≈ 5.

3. **`TupGet` sí lo emite el compilador.** El informe dice "el compilador siempre emite `TupGetC`"
   y le da Elim 50. Se emite en `MethodBodyEmitter.cs` en dos sitios: el acceso a tupla con índice
   no constante y el paso de `for-in` sobre tupla. Elim real ≈ 20.

4. **`ArrIn` y `DictIn` también se emiten** (`MethodBodyEmitter.cs`): son la bajada del operador
   `in`. Retirarlos no es "quitar un opcode redundante", es cambiar la bajada de un operador del
   lenguaje por dos o tres instrucciones.

5. **`Nop`: el razonamiento es incorrecto y la conclusión es más fuerte de lo que dice.** El
   informe supone que el parcheo de saltos lo escribe y luego lo reescribe. Lo que hay en
   `SurtrCodeEmitter.cs` es un `JumpPatch { Current = OpCode.Nop, Wide = OpCode.Nop, Pinned = true }`,
   que es un **centinela** de "este parche escribe un offset i32 y no reescribe ningún opcode" —
   lo usan las tablas de `Switch`. Nadie emite `Nop` nunca. Está muerto del todo, no
   condicionalmente.

6. **El informe no registra que 16 opcodes ya están externalizados.** El commit `ceabd65` introdujo
   cuatro helpers `NoInlining` con fan-in de `case` y un `struct HState { ip, sp, Flow }`
   (`HandleNullableOp`, `HandleRangeOp`, `HandleModuleOp`, `HandleObjectOp`). Por eso `JPAX`,
   `JPNAX`, `LoadModuleX` y `ObjNewX` aparecen con 0 B de cuerpo propio en la tabla de §7: su código
   ya no está en `Run()`. **La técnica que el informe propone como futura ya existe y ya midió
   −4,6 %.**

7. **Los gemelos `X` no son todos igual de alcanzables**, y el informe los trata como un bloque.
   Son dos poblaciones con condiciones de disparo distintas:
   - **19 de rama** (`JPX`, `JPZX`, `JPGEX`, …): se alcanzan por **relajación automática de offset**
     (`SurtrJumpWidth.Auto` en `SurtrCodeEmitter.Helpers.cs`) cuando un cuerpo de método pasa de
     32 KB de bytecode. Son alcanzables de verdad; hay un test que lo demuestra
     (`AnAutoJumpTooFarForTwoBytes_IsWidenedAndStillLands`).
   - **20 de índice** (`LdcX`, `CastX`, `InvokeStaticX`, …): sólo se eligen si
     `índice > ushort.MaxValue`, es decir con más de 65 535 constantes, tipos o métodos en un
     módulo. `src/Surtr.Compiler` **no los nombra ni una vez**: son puramente relajación del emisor.

8. **El calor de `Ext` (100 002 despachos, 0,03 %) es incoherente con el −47 % medido en `forIn`** en
   `docs/Plan-Opcodes-Extendidos.md`. La explicación no es que la fusión no funcione: es que los
   bucles calientes de la suite son `while (i < n) { …; i += 1; }`, no `for-in`. `for i in 0..n`
   **ya fusiona** (`MethodBodyEmitter.cs`, `Code.ForRangeNext`, con `fused = FitsInSlotByte(...)`
   cierto en cualquier método ordinario). Esto **confirma la recomendación 1 del informe pero
   reapunta su objetivo**: lo que hay que fusionar es el `while` con contador, no el `for-in` sobre
   rango. Los cuatro despachos por iteración de ese patrón (`IncLocal`, `Ldl`, `Ldl`/`PushI32`,
   `JP<cmp>`) explican por sí solos cuatro de los diez opcodes más calientes.

## 5. Prototipos construidos y medidos

Dos variantes en un worktree desechable, ambas verificadas con la suite de tests y con los
checksums del bench.

### 5.1 Mover los gemelos `X` al fondo del `switch`

Movimiento de texto puro: 36 grupos `case` reubicados justo antes de `default:`, sin tocar una sola
sentencia. Los tres grupos con fan-in mixto (`LoadModule`/`LoadModuleX`/`LoadCurrentModule`,
`ObjNew`/`ObjNewX`, y el grupo de nulables) se dejan como están, porque sus etiquetas comparten
cuerpo.

| | base | `X` al fondo |
|---|---|---|
| tamaño nativo de `Run()` | 39 983 B | 40 058 B (+0,2 %) |
| rango que abarca el top-32 | 0x151–0x72E7 = 29 078 B | 0x151–0x5878 = **22 311 B** (−23 %) |
| frío intercalado en el top-32 | 26 633 B | **19 863 B** (−25 %) |
| dilución | 11,5x | **8,9x** |
| líneas de I-cache del top-32 | 57 | 55 |
| páginas del top-32 | 6 | 5 |
| **tests** | — | **3 089 / 3 089** |
| **checksums del bench** | — | **50 / 50 de acuerdo** |

Confirma el mecanismo de §1 de punta a punta: mover fuente mueve código nativo, y no rompe nada.

### 5.2 Enrutar los gemelos `X` a un único camino frío (proxy de un prefijo `Wide`)

Los 36 grupos anteriores sustituidos por un solo fan-in de 36 etiquetas. `ArrNewX` restaurado con su
cuerpo, por §4.1.

| | base | proxy de `Wide` |
|---|---|---|
| **tamaño nativo de `Run()`** | 39 983 B | **31 165 B (−22,1 %)** |
| rango del top-32 | 29 078 B | 21 918 B |
| dilución | 11,5x | 8,7x |
| líneas de I-cache del top-32 | 57 | 54 |
| **cuerpos del top-32 con tamaño idéntico** | — | **27 de 32** |
| los 5 restantes | — | **todos más pequeños** (−27, −20, −8, −8, +3 B) |
| tests que fallan | — | 15, y son los tests dedicados de cada forma `X` |

Los cinco cuerpos que cambian lo hacen a la baja, y la razón es que al acercarse los destinos
algunos saltos pasan de codificación `rel32` a `rel8`. **Ningún opcode normal paga nada por el
prefijo**: los cuerpos calientes son byte a byte los mismos.

### 5.3 ¿Añade un prefijo `Wide` sobrecoste a los opcodes normales?

No, **siempre que se implemente como opcode-prefijo con `case` propio** que entra en un helper
`NoInlining` con su propia decodificación — que es exactamente la forma de `Ext` hoy y la de
`HandleNullableOp`. Razones, en orden de solidez:

- **Medido**: 27 de los 32 cuerpos calientes salen idénticos, los otros 5 más pequeños (§5.2).
- **Estructural**: el despacho es una tabla de saltos (§2). Un valor más o cuarenta menos no cambian
  ni una instrucción de la secuencia de búsqueda.
- **El coste del prefijo lo paga sólo quien lo usa**: un despacho extra, medido con
  `surtrbench --prefix-tax` en **0,654 ns** en esta máquina (spread 11,2 %; los 0,44–0,48 ns de
  `docs/Plan-Opcodes-Extendidos.md` siguen siendo la cifra calibrada). Se paga sobre instrucciones
  con **cero ejecuciones** en toda la suite.
- **Y hay una ganancia neta para el camino normal**: 8,8 KB menos de método, menos páginas y algún
  salto que se acorta.

La forma que **sí costaría** es la otra lectura de `wide`: un *flag* que cada lectura de inmediato
consulte para decidir si lee 2 o 4 bytes. Eso pone un test por operando en todos los opcodes
normales y hay que descartarlo explícitamente. El prefijo de la JVM es de la primera clase, no de
la segunda, y es la que aplica aquí.

Efectos de segundo orden que sí existen y conviene tener escritos:

- El `cmp edx, N` del despacho sigue igual: el JIT lo emite contra el máximo del enum. Sólo baja de
  256 a unos 205 si además se renumera contiguo, y da lo mismo.
- La tabla de saltos sigue en 1 KB mientras `Ext = 0xFF` exista. Si se pliega el espacio extendido y
  el máximo baja a ~0xCC, pasa a ~820 B. Irrelevante.
- La relajación de saltos de `SurtrCodeEmitter` pasa de un delta de longitud 3→5 a uno de 3→6. El
  bucle de punto fijo ya maneja cambios de longitud, pero es una diferencia real que hay que probar.
- **Cualquier cambio en `Run()` voltea el estado de layout.** Nada de esto es un resultado de
  rendimiento hasta pasar por `scripts/ab-suite.ps1` con varios procesos por lado, por
  `docs/Informe-Volatilidad-Run.md`.

## 6. Presupuesto de valores: sí se puede prescindir del espacio extendido

Los 39 gemelos anchos genuinos son **7 014 B nativos, el 17,5 % de `Run()`, con cero ejecuciones**.
Sustituirlos por un solo prefijo `Wide` (el `wide` de la JVM: reinterpreta el inmediato de la
instrucción siguiente como 4 bytes, atendido en un helper `NoInlining`):

```
hoy                            240 usados (0x00-0xEF) + Ext (0xFF), 15 libres (0xF0-0xFE)
- 39 gemelos anchos, + Wide    202
- Nop (nadie lo emite)         201
+ 6 ForNext base desde Ext     207     (sus 6 formas X las cubre Wide)
- Ext (el prefijo sobra)       206
                               ---------------------------------------
                               206 valores usados, 50 libres
```

**206 opcodes, sin espacio extendido, 50 valores libres**, y el peaje del prefijo desaparece de cada
paso de bucle fusionado, que es donde hoy se paga de verdad. Es el único plan de los evaluados que
cumple literalmente el objetivo de eliminar los extendidos, y el precio es un bump de
`SurtrModuleImage.FormatVersion` y tocar la relajación del emisor.

Lo que **no** conviene retirar aunque el informe anterior les dé puntuación alta:

- **`Ldl0`–`Ldl5` / `Stl0`–`Stl5`** (Elim 40 en el informe anterior): 104 237 132 ejecuciones entre
  los doce. El cuerpo dedicado son **22 B**; el indexado `LdlS` son **34 B** más una lectura de
  inmediato. Retirarlos cuesta un byte de bytecode por uso y trabajo por despacho en el 35 % del
  calor total, a cambio de 12 valores que con `Wide` ya no escasean.
- **`Ldc0`–`Ldc9`** (Elim 70): 4 100 070 ejecuciones, 219 B entre los diez. Retirarlos es defendible
  — pero es un cambio de coste positivo, no neutro, y sólo tiene sentido si hacen falta valores.
  Con `Wide` no hacen falta.

## 7. Lo que se aplico, y lo que midio

Los cuatro caminos se implementaron en ese orden. Cada paso se verifico con la suite completa de
tests y con los 50 checksums del bench antes de pasar al siguiente.

### 7.1 Externalizar el camino lento de los cuerpos calientes

La rama nativa de `FieldGet`, `FieldSet`, `StaticFieldGet` y `StaticFieldSet` sale ahora por una
etiqueta fria al final del metodo, alcanzada con `goto` — el idioma que `Safepoint:` y
`InvokeResolved:` ya usaban — en vez de escribirse en linea. El campo resuelto viaja en
`_pendingField`, por la misma razon que los operandos de llamada viajan en `_pending*`: una rama
que casi nunca se toma no debe costar un rango de vida en el bucle. Se descarto hacer lo mismo con
la rama de generador de `ReturnValue`: exigiria dos locales de ambito de metodo para ahorrar 84 B,
que es justo el intercambio que `docs/Informe-Volatilidad-Run.md` desaconseja.

| opcode | antes | despues | |
|---|---|---|---|
| `FieldGet` | 418 B | **221 B** | −47 % |
| `FieldSet` | 519 B | **269 B** | −48 % |
| `StaticFieldGet` | 361 B | **139 B** | −61 % |
| `StaticFieldSet` | 349 B | **136 B** | −61 % |
| los seis (con las formas anchas) | 2 405 B | **1 072 B** | **−55 %** |

Camino lento incrustado en el top-32: **649 B → 309 B**.

### 7.2 Reordenacion hot-first del `switch`

Los 229 grupos `case` se reordenaron en cuatro regiones — nucleo caliente, templado, frio
alcanzable, formas anchas — con las familias juntas dentro de cada una y las tiradas de opcodes
(`Ldl0`–`Ldl5`, `Stl0`–`Stl5`) sin romper. El comentario de cada cuerpo viaja con el.

| | base | tras 7.1 y 7.2 |
|---|---|---|
| rango que abarca el top-32 | 0x151–0x72E7 = 29 078 B | 0x13B–0x127D = **4 674 B** |
| frio intercalado en el top-32 | 26 633 B | **2 457 B** (−91 %) |
| **dilucion** | **11,5x** | **2,2x** |
| lineas de I-cache, top-10 / 20 / 32 / 68 | 14 / 41 / 57 / 166 | **11 / 29 / 40 / 127** |
| paginas de 4 K, top-32 | 6 | **2** |

### 7.3 Fusion del contador `while`

`MethodBodyEmitter.TryEmitCountedWhile` reconoce `while (i < n) { …; i += 1; }` y lo baja a la
misma superinstruccion `ForRangeNext` que `for i in 0..n` ya usaba, sin opcode nuevo: la guarda
sube una vez por encima del bucle y el paso de abajo se prueba a si mismo. El limite puede ser un
local, un parametro o una constante — esta ultima se materializa en una ranura una sola vez, que
es lo que saca el `PushI32` del bucle.

Rechaza la fusion, y son las condiciones que la hacen correcta: cualquier `continue` en el cuerpo
(en un `while` reprueba **sin** incrementar, y el paso fusionado se alcanza cayendo por el final —
el escaneo desciende a los bucles anidados porque un `continue` etiquetado puede nombrar el de
fuera), un incremento que no sea la ultima sentencia, un paso distinto de uno, o un contador que no
sea un `int` en una ranura direccionable con un byte.

```
antes:  top: Ldl i · PushI8 10 · JPGE end · <cuerpo> · IncLocal i · JP top     9 despachos/iter
ahora:       Ldl i · Ldl lim · JPGE end                                        (una vez)
        top: <cuerpo> · Ext ForRangeNextLT i lim -> top                        5 despachos/iter
```

El overhead del bucle pasa de cinco instrucciones a **una** — pero de cinco despachos a **dos**,
no a uno: `ForRangeNext` vive tras el prefijo `Ext`, y el prefijo es un despacho propio. Medido en
el recuento de calor de §8: las 750 000 iteraciones que la suite fusiona pierden `IncLocal`, `JP`,
`JPGE` y dos cargas de local (−3 750 000 despachos) y ganan `Ext` mas `ForRangeNextLT`
(+1 500 000). Es la mejor razon que hay para plegar el espacio extendido en el primario (§7.6):
ese segundo despacho desapareceria de cada paso de cada bucle del lenguaje.

Cubierto por catorce tests nuevos en
`LoopFusionTests`: cada salida, el cuerpo que mueve el contador, el que mueve el limite, el `try`
alrededor, y las cinco formas que la rechazan.

### 7.4 El prefijo `Wide`

`0xF0` es un prefijo: el byte siguiente es un opcode ordinario cuyo unico indice o desplazamiento
se lee de cuatro bytes. Sustituye a los 39 gemelos anchos, que dejan sus valores **retirados**
(`OpCodeValueTests.RetiredValues` los lista y falla si algo se archiva en uno).

- **Juego primario: 240 → 203 opcodes.** `0xF1`–`0xFE` libres, mas los 39 huecos retirados.
- **`SurtrModuleImage.FormatVersion` 13 → 14.** El encuadre no cambia; el bump existe porque un
  lector de la 13 leeria el opcode tras un `Wide` como una instruccion propia, y un `JPGEX` de la
  13 no decodificaria en la 14. Los dos son lecturas erroneas silenciosas.
- **La relajacion del emisor no necesito maquinaria nueva.** `Widen` ya sabia reescribir una
  cabecera de dos bytes, porque el prefijo `Ext` la tiene: escribir `Wide` mas el mismo opcode es
  el mismo caso.
- **La API del emisor no cambia.** Los metodos de nivel dos siguen llamandose `LdcX`, `JPGEX`,
  `InvokeStaticX`; lo que cambia es lo que codifican.
- **`ArrNewX` se queda** — §4.1: no es un gemelo ancho.

**Lo que este paso no dio, y conviene tenerlo escrito:** el −22 % de tamano que midio el prototipo
de §5.2 **no se materializa**, porque alli venia de *borrar* los cuerpos, y los cuerpos tienen que
vivir en algun sitio. Estan en un sub-switch tras el prefijo, dentro de `Run()`, exactamente como
los de `Ext`: tres de ellos entran en la secuencia de llamada compartida y cuatro en el safepoint,
y ambas son etiquetas de este metodo. Sacarlos a un helper `NoInlining` con codigos de flujo es un
paso mas, y §5.2 ya midio lo que valdria en layout: **una linea de caché**. Lo que este paso si dio
es el espacio de opcodes y el formato: 39 valores y una convencion de sufijo que ya no miente.

### 7.5 Estado final, y que midio

| | base (`ceabd65`) | ahora (`a65c9c0`) |
|---|---|---|
| opcodes primarios | 240 | **203** |
| `Run()` nativo | 39 983 B | 41 170 B |
| dilucion del top-32 | 11,5x | **2,2x** |
| paginas de 4 K del top-32 | 6 | **2** |
| lineas de I-cache del top-32 | 57 | **40** |
| camino lento en cuerpos calientes | 649 B | **309 B** |
| despachos primarios de la suite | 296 477 444 | **293 477 444** (−1,01 %) |
| tests | 3 089 | **3 103** |
| checksums del bench | 50/50 | **50/50** |

**Rendimiento: neutral.** `scripts/ab-suite.ps1 -RefA ceabd65 -RefB a65c9c0 -Runs 7`, 14
lanzamientos intercalados sobre los 50 casos, metrica primaria el minimo por proceso (el estado
rapido):

```
mediana del delta: 0 %     mejoran >5 %: 0     empeoran >5 %: 0
```

El único caso que asomó la cabeza — `generics` a +5,3 % — se re-midio solo, con 11 lanzamientos por
lado y un control de C# de 0 %, y dio **+0,1 %**: era ruido. Los extremos reales del resto van de
−3,3 % (`arrayFill`) a +2,4 % (`vec2Class`), dentro de la banda que el propio protocolo considera
indistinguible.

**Por que neutral, y por que era previsible.** Este informe ya decia (§3) que la magnitud en juego
no era la L1i: 3,6 KB de camino caliente caben de sobra en 32 KB con cualquier disposicion. Lo que
la compactacion ataca es la cuenta de paginas y la densidad del caché de µops, y una suite de
microbenchmarks — un bucle apretado repetido un millon de veces, con todo residente desde la
segunda iteracion — es justo el perfil que **no** ejerce ninguna de las dos. La dispersion se paga
cuando el conjunto de trabajo recorre muchas familias de opcodes, que no es lo que mide esta suite.

Lo que si movio la aguja de forma medible es lo contable: **−3 000 000 de despachos (−1,01 %)**,
39 valores de opcode liberados, seis cuerpos calientes a la mitad de tamaño y la dilucion del
camino caliente de 11,5x a 2,2x. Y lo que importa tanto como eso: **nada regresiono**. Cambiar el
juego de 240 a 203 opcodes, reordenar el switch entero y meter un prefijo nuevo salio a coste cero.

### 7.6 Lo que sigue abierto

- **Plegar el espacio extendido en el primario.** Es ahora la unica de las cuatro que tiene un
  numero de rendimiento esperado y no medido: cada paso de bucle fusionado paga hoy **dos**
  despachos, el prefijo `Ext` y el sub-opcode (§7.3), y plegarlo se lleva uno de los dos de todos
  los bucles del lenguaje — no solo de los `while`. Con 39 valores libres cabe de sobra. Es el
  siguiente bump de formato natural.
- **Medir donde la dispersion si se paga.** La suite no ejerce ni el iTLB ni el caché de µops. Un
  workload que recorra muchas familias, o el objetivo real (Mono/IL2CPP dentro de Unity, sin el
  JIT de .NET), son los sitios donde el trabajo de §7.1 y §7.2 deberia aparecer. Hasta que se mida
  ahi, lo honesto es decir que la compactacion salio neutral.
- **Sacar los cuerpos anchos a un helper.** Vale ~8,8 KB de `Run()` y una linea de caché (§5.2).
- **Renombrar `ArrNewX`.** Es el unico `X` primario que queda y significa otra cosa (§4.1).

## 8. Calor recontado tras los cambios

Mismo método que `docs/Informe-Opcodes-Eliminables.md`: un contador por opcode en la etiqueta
`Dispatch`, mas dos contadores nuevos para los sub-opcodes tras `Ext` y tras `Wide`, sobre un
worktree desechable y la suite completa con checksums verificados.

**294 327 446 despachos** contando el sub-opcode de un prefijo aparte. Sobre lo que contaba el
instrumento anterior — solo despachos primarios — son **293 477 444 contra 296 477 444, −1,01 %**.

Todo el delta es la fusion del `while`, y cuadra exacto:

| opcode | antes | ahora | delta |
|---|---|---|---|
| `LdlS` | 8 609 672 | 7 709 672 | −900 000 |
| `JP` | 14 849 417 | 14 099 417 | −750 000 |
| `JPGE` | 14 174 747 | 13 424 747 | −750 000 |
| `IncLocal` | 13 342 584 | 12 592 584 | −750 000 |
| `Ldl2` / `Ldl1` / `Ldl0` | | | −600 000 |
| `Ext` | 100 002 | 850 002 | **+750 000** |
| `ext:ForRangeNextLT` | — | 750 000 | **+750 000** |

750 000 iteraciones tomaron el paso fusionado: −3 750 000 despachos por lo que dejan de emitir,
+1 500 000 por lo que emiten. Son pocas para esta suite — sus bucles contados son en su mayoria
`for i in 0..n`, que ya fusionaba — asi que el −1 % es el suelo de lo que la fusion vale, no el
techo: en codigo escrito con el idioma `while` el reparto es otro.

**`Wide`: cero despachos.** El prefijo no se ejecuta ni una vez en toda la suite, que es
exactamente lo que decia el analisis y lo que hace que los 39 valores liberados no le cuesten nada
a nadie.

El reparto apenas se movio: **33 opcodes cubren el 90 %** del calor (antes 32), **70 el 99 %**
(antes 68), y **103 de los 203 primarios** se ejecutan. Los niveles de la reordenacion de §7.2
siguen bien puestos.

| # | Opcode | Valor | Exec | Delta | % | Acum % |
|---|---|---|---|---|---|---|
| 1 | `Ldl2` | 0x1A | 22 529 971 | −300000 | 7.65 | 7.7 |
| 2 | `Ldl1` | 0x19 | 22 432 932 | −150000 | 7.62 | 15.3 |
| 3 | `Ldl0` | 0x18 | 21 269 474 | −150000 | 7.23 | 22.5 |
| 4 | `Add` | 0x3C | 15 230 054 | = | 5.17 | 27.7 |
| 5 | `JP` | 0xA5 | 14 099 417 | −750000 | 4.79 | 32.5 |
| 6 | `JPGE` | 0xB9 | 13 424 747 | −750000 | 4.56 | 37.0 |
| 7 | `PushI8` | 0x06 | 13 039 140 | = | 4.43 | 41.5 |
| 8 | `Mod` | 0x44 | 12 985 000 | = | 4.41 | 45.9 |
| 9 | `IncLocal` | 0x28 | 12 592 584 | −750000 | 4.28 | 50.1 |
| 10 | `PushI32` | 0x08 | 10 605 000 | = | 3.60 | 53.8 |
| 11 | `Ldl3` | 0x1B | 10 255 088 | = | 3.48 | 57.2 |
| 12 | `Stl1` | 0x21 | 8 201 253 | = | 2.79 | 60.0 |
| 13 | `LdlS` | 0x1E | 7 709 672 | −900000 | 2.62 | 62.6 |
| 14 | `LoadLocalField` | 0x32 | 7 300 000 | = | 2.48 | 65.1 |
| 15 | `FMul` | 0x41 | 7 000 000 | = | 2.38 | 67.5 |
| 16 | `FieldGet` | 0x29 | 5 925 002 | = | 2.01 | 69.5 |
| 17 | `FAdd` | 0x3D | 5 900 000 | = | 2.00 | 71.5 |
| 18 | `ReturnValue` | 0xE6 | 5 237 641 | = | 1.78 | 73.3 |
| 19 | `InvokeSpecial` | 0xDC | 5 108 074 | = | 1.74 | 75.0 |
| 20 | `LoadValueLocal` | 0x30 | 4 800 002 | = | 1.63 | 76.7 |
| 21 | `StoreValueLocal` | 0x31 | 4 500 002 | = | 1.53 | 78.2 |
| 22 | `Ldl4` | 0x1C | 4 395 257 | = | 1.49 | 79.7 |
| 23 | `Stl3` | 0x23 | 4 338 026 | = | 1.47 | 81.2 |
| 24 | `Stl2` | 0x22 | 4 250 049 | = | 1.44 | 82.6 |
| 25 | `FieldSet` | 0x2A | 3 550 012 | = | 1.21 | 83.8 |
| 26 | `Ldc7` | 0x12 | 3 000 004 | = | 1.02 | 84.8 |
| 27 | `StlS` | 0x26 | 2 838 851 | = | 0.96 | 85.8 |
| 28 | `Ldl5` | 0x1D | 2 390 030 | = | 0.81 | 86.6 |
| 29 | `JPZ` | 0xA7 | 2 360 006 | = | 0.80 | 87.4 |
| 30 | `ArrGet` | 0x7F | 2 152 600 | = | 0.73 | 88.1 |
| 31 | `LdcS` | 0x15 | 2 009 214 | = | 0.68 | 88.8 |
| 32 | `Stl4` | 0x24 | 1 975 031 | = | 0.67 | 89.5 |
| 33 | `Dup` | 0x01 | 1 608 008 | = | 0.55 | 90.0 |
| 34 | `Stl5` | 0x25 | 1 600 021 | = | 0.54 | 90.6 |
| 35 | `Mul` | 0x40 | 1 590 000 | = | 0.54 | 91.1 |
| 36 | `ReturnValues` | 0xE7 | 1 500 000 | = | 0.51 | 91.6 |
| 37 | `InvokeClosure` | 0xE0 | 1 468 768 | = | 0.50 | 92.1 |
| 38 | `CallLocalModule` | 0xD7 | 1 350 055 | = | 0.46 | 92.6 |
| 39 | `ObjNew` | 0xA3 | 1 308 011 | = | 0.44 | 93.0 |
| 40 | `ReturnVoid` | 0xE5 | 1 300 016 | = | 0.44 | 93.5 |
| 41 | `LoadValueField` | 0x34 | 1 200 000 | = | 0.41 | 93.9 |
| 42 | `Ldc6` | 0x11 | 1 000 000 | = | 0.34 | 94.2 |
| 43 | `UpValueGet` | 0x2F | 900 000 | = | 0.31 | 94.5 |
| 44 | `I2F` | 0x66 | 900 000 | = | 0.31 | 94.8 |
| 45 | `ArrSet` | 0x80 | 900 000 | = | 0.31 | 95.1 |
| 46 | `Ext` | 0xFF | 850 002 | +750000 | 0.29 | 95.4 |
| 47 | `StaticFieldGet` | 0x2B | 800 000 | = | 0.27 | 95.7 |
| 48 | `EQ` | 0x50 | 800 000 | = | 0.27 | 96.0 |
| 49 | `ext:ForRangeNextLT` | 0x0B | 750 000 | nuevo | 0.25 | 96.2 |
| 50 | `Sub` | 0x3E | 737 584 | = | 0.25 | 96.5 |
| 51 | `UnboxDynamic` | 0x74 | 650 000 | = | 0.22 | 96.7 |
| 52 | `JPLE` | 0xBD | 640 010 | = | 0.22 | 96.9 |
| 53 | `StrLen` | 0x77 | 600 001 | = | 0.20 | 97.1 |
| 54 | `JPNE` | 0xB5 | 600 000 | = | 0.20 | 97.3 |
| 55 | `InvokeVirtual` | 0xDB | 400 004 | = | 0.14 | 97.5 |
| 56 | `InvokeInterface` | 0xDF | 400 003 | = | 0.14 | 97.6 |
| 57 | `PushI16` | 0x07 | 380 257 | = | 0.13 | 97.7 |
| 58 | `DictGet` | 0x90 | 360 000 | = | 0.12 | 97.8 |
| 59 | `BoxInt` | 0x6C | 350 000 | = | 0.12 | 98.0 |
| 60 | `NewFunction` | 0xE3 | 300 004 | = | 0.10 | 98.1 |
| 61 | `StoreValueField` | 0x35 | 300 002 | = | 0.10 | 98.2 |
| 62 | `Div` | 0x42 | 300 000 | = | 0.10 | 98.3 |
| 63 | `Inv` | 0x4F | 300 000 | = | 0.10 | 98.4 |
| 64 | `GE` | 0x53 | 300 000 | = | 0.10 | 98.5 |
| 65 | `IsPresent` | 0x65 | 300 000 | = | 0.10 | 98.6 |
| 66 | `CastOrNull` | 0x9B | 300 000 | = | 0.10 | 98.7 |
| 67 | `JPN` | 0xAB | 300 000 | = | 0.10 | 98.8 |
| 68 | `JPStrNE` | 0xD1 | 300 000 | = | 0.10 | 98.9 |
| 69 | `JPInstanceOf` | 0xD3 | 300 000 | = | 0.10 | 99.0 |
| 70 | `Switch` | 0xD5 | 300 000 | = | 0.10 | 99.1 |
| 71 | `CallModule` | 0xD9 | 300 000 | = | 0.10 | 99.2 |
| 72 | `Pop` | 0x02 | 276 022 | = | 0.09 | 99.3 |
| 73 | `LT` | 0x54 | 276 022 | = | 0.09 | 99.4 |
| 74 | `LE` | 0x55 | 268 768 | = | 0.09 | 99.5 |
| 75 | `ArrPush` | 0x81 | 215 320 | = | 0.07 | 99.5 |
| 76 | `Yield` | 0xED | 200 000 | = | 0.07 | 99.6 |
| 77 | `GenResume` | 0xEB | 150 003 | = | 0.05 | 99.7 |
| 78 | `GenCurrent` | 0xEC | 150 000 | = | 0.05 | 99.7 |
| 79 | `ArrLen` | 0x7E | 115 005 | = | 0.04 | 99.8 |
| 80 | `DictSet` | 0x91 | 110 064 | = | 0.04 | 99.8 |
| 81 | `StrCat` | 0x79 | 101 264 | = | 0.03 | 99.8 |
| 82 | `Ldc5` | 0x10 | 100 001 | = | 0.03 | 99.9 |
| 83 | `PushAbsent` | 0x0A | 100 000 | = | 0.03 | 99.9 |
| 84 | `JPLT` | 0xBB | 50 001 | = | 0.02 | 99.9 |
| 85 | `ext:ArrForNext` | 0x01 | 50 001 | nuevo | 0.02 | 99.9 |
| 86 | `ext:DictForNext` | 0x07 | 50 001 | nuevo | 0.02 | 99.9 |
| 87 | `PushTrue` | 0x04 | 50 000 | = | 0.02 | 100.0 |
| 88 | `GenResumed` | 0xEF | 50 000 | = | 0.02 | 100.0 |
| 89 | `DictDel` | 0x92 | 30 000 | = | 0.01 | 100.0 |
| 90 | `DictIn` | 0x96 | 30 000 | = | 0.01 | 100.0 |
| 91 | `Throw` | 0xE8 | 8 000 | = | 0.00 | 100.0 |
| 92 | `Ldc8` | 0x13 | 64 | = | 0.00 | 100.0 |
| 93 | `ArrPack` | 0x7D | 8 | = | 0.00 | 100.0 |
| 94 | `GenNew` | 0xE9 | 6 | = | 0.00 | 100.0 |
| 95 | `DictPack` | 0x8E | 4 | = | 0.00 | 100.0 |
| 96 | `StaticFieldSet` | 0x2D | 3 | = | 0.00 | 100.0 |
| 97 | `GenIterate` | 0xEA | 3 | = | 0.00 | 100.0 |
| 98 | `GenDelegate` | 0xEE | 2 | = | 0.00 | 100.0 |
| 99 | `PushFalse` | 0x05 | 1 | = | 0.00 | 100.0 |
| 100 | `Ldc9` | 0x14 | 1 | = | 0.00 | 100.0 |
| 101 | `ArrNew` | 0x7B | 1 | = | 0.00 | 100.0 |
| 102 | `DictKeys` | 0x94 | 1 | = | 0.00 | 100.0 |
| 103 | `NewClosure` | 0xE1 | 1 | = | 0.00 | 100.0 |

## 9. Tabla: los 241 cuerpos primarios por calor, antes de los cambios

> **Foto de `ceabd65`, antes de los cambios de §7.** Los 39 gemelos anchos que aparecen aqui ya no
> existen como opcodes (§7.4) y las direcciones nativas son las de entonces. Se conserva porque es
> la medicion sobre la que se decidio, y porque las columnas de calor siguen valiendo.

**#** rank por calor · **Valor** del opcode · **Dir.** dirección nativa · **B nat.** bytes del cuerpo ·
**núcleo** bloques sin `call` · **lento** bloques con `call` · **IL excl.** bytes de IL exclusivos ·
**Exec** ejecuciones · **%** del total de 296 477 444.

> Notas de lectura. Un cuerpo con 0 B de IL exclusivo comparte todo su código: o entra por fan-in a
> un helper `NoInlining` (§4.6) o cae en una secuencia compartida (`InvokeResolved`, la entrada de
> generador). El tamaño nativo de un cuerpo se mide como la distancia hasta la siguiente entrada
> distinta de la tabla de saltos, repartida entre las etiquetas que comparten dirección: el último
> tramo del método absorbe la cola compartida y por eso su cifra no es comparable.

| # | Opcode | Valor | Dir. | B nat. | núcleo | lento | IL excl. | Exec | % |
|---|---|---|---|---|---|---|---|---|---|
| 1 | `Ldl2` | 0x1A | 0x0044A | 22 | 22 | 0 | 19 | 22 829 971 | 7,70 |
| 2 | `Ldl1` | 0x19 | 0x00434 | 22 | 22 | 0 | 16 | 22 582 932 | 7,62 |
| 3 | `Ldl0` | 0x18 | 0x0041E | 22 | 22 | 0 | 14 | 21 419 474 | 7,22 |
| 4 | `Add` | 0x3C | 0x005E9 | 42 | 42 | 0 | 37 | 15 230 054 | 5,14 |
| 5 | `JP` | 0xA5 | 0x050C5 | 40 | 40 | 0 | 28 | 14 849 417 | 5,01 |
| 6 | `JPGE` | 0xB9 | 0x05B45 | 52 | 52 | 0 | 60 | 14 174 747 | 4,78 |
| 7 | `IncLocal` | 0x28 | 0x005AC | 61 | 61 | 0 | 47 | 13 342 584 | 4,50 |
| 8 | `PushI8` | 0x06 | 0x00151 | 45 | 45 | 0 | 31 | 13 039 140 | 4,40 |
| 9 | `Mod` | 0x44 | 0x0073C | 74 | 74 | 0 | 85 | 12 985 000 | 4,38 |
| 10 | `PushI32` | 0x08 | 0x001B8 | 75 | 75 | 0 | 57 | 10 605 000 | 3,58 |
| 11 | `Ldl3` | 0x1B | 0x00460 | 22 | 22 | 0 | 19 | 10 255 088 | 3,46 |
| 12 | `LdlS` | 0x1E | 0x004A2 | 34 | 34 | 0 | 26 | 8 609 672 | 2,90 |
| 13 | `Stl1` | 0x21 | 0x0050B | 25 | 25 | 0 | 16 | 8 201 253 | 2,77 |
| 14 | `LoadLocalField` | 0x32 | 0x072E7 | 93 | 93 | 0 | 73 | 7 300 000 | 2,46 |
| 15 | `FMul` | 0x41 | 0x006AE | 35 | 35 | 0 | 24 | 7 000 000 | 2,36 |
| 16 | `FieldGet` | 0x29 | 0x04162 | 418 | 233 | 185 | 183 | 5 925 002 | 2,00 |
| 17 | `FAdd` | 0x3D | 0x00613 | 35 | 35 | 0 | 24 | 5 900 000 | 1,99 |
| 18 | `ReturnValue` | 0xE6 | 0x06E29 | 232 | 148 | 84 | 319 | 5 237 641 | 1,77 |
| 19 | `InvokeSpecial` | 0xDC | 0x067DC | 104 | 0 | 104 | 71 | 5 108 074 | 1,72 |
| 20 | `LoadValueLocal` | 0x30 | 0x0723C | 82 | 82 | 0 | 105 | 4 800 002 | 1,62 |
| 21 | `StoreValueLocal` | 0x31 | 0x0728E | 89 | 89 | 0 | 123 | 4 500 002 | 1,52 |
| 22 | `Ldl4` | 0x1C | 0x00476 | 22 | 22 | 0 | 19 | 4 395 257 | 1,48 |
| 23 | `Stl3` | 0x23 | 0x0053D | 25 | 25 | 0 | 19 | 4 338 026 | 1,46 |
| 24 | `Stl2` | 0x22 | 0x00524 | 25 | 25 | 0 | 19 | 4 250 049 | 1,43 |
| 25 | `FieldSet` | 0x2A | 0x04304 | 519 | 243 | 276 | 204 | 3 550 012 | 1,20 |
| 26 | `Ldc7` | 0x12 | 0x00340 | 22 | 22 | 0 | 19 | 3 000 004 | 1,01 |
| 27 | `StlS` | 0x26 | 0x00588 | 36 | 36 | 0 | 26 | 2 838 851 | 0,96 |
| 28 | `Ldl5` | 0x1D | 0x0048C | 22 | 22 | 0 | 19 | 2 390 030 | 0,81 |
| 29 | `JPZ` | 0xA7 | 0x04FE9 | 55 | 55 | 0 | 44 | 2 360 006 | 0,80 |
| 30 | `ArrGet` | 0x7F | 0x02C4B | 122 | 122 | 0 | 93 | 2 152 600 | 0,73 |
| 31 | `LdcS` | 0x15 | 0x003C7 | 41 | 41 | 0 | 26 | 2 009 214 | 0,68 |
| 32 | `Stl4` | 0x24 | 0x00556 | 25 | 25 | 0 | 19 | 1 975 031 | 0,67 |
| 33 | `Dup` | 0x01 | 0x00128 | 16 | 16 | 0 | 15 | 1 608 008 | 0,54 |
| 34 | `Stl5` | 0x25 | 0x0056F | 25 | 25 | 0 | 19 | 1 600 021 | 0,54 |
| 35 | `Mul` | 0x40 | 0x00683 | 43 | 43 | 0 | 37 | 1 590 000 | 0,54 |
| 36 | `ReturnValues` | 0xE7 | 0x06F11 | 204 | 204 | 0 | 399 | 1 500 000 | 0,51 |
| 37 | `InvokeClosure` | 0xE0 | 0x06C4C | 219 | 152 | 67 | 191 | 1 468 768 | 0,50 |
| 38 | `CallLocalModule` | 0xD7 | 0x0647B | 104 | 0 | 104 | 71 | 1 350 055 | 0,46 |
| 39 | `ObjNew` | 0xA3 | 0x040D3 | 71 | 16 | 127 | 0 | 1 308 011 | 0,44 |
| 40 | `ReturnVoid` | 0xE5 | 0x06D52 | 215 | 115 | 100 | 284 | 1 300 016 | 0,44 |
| 41 | `LoadValueField` | 0x34 | 0x075A9 | 206 | 206 | 0 | 149 | 1 200 000 | 0,40 |
| 42 | `Ldc6` | 0x11 | 0x0032A | 22 | 22 | 0 | 19 | 1 000 000 | 0,34 |
| 43 | `UpValueGet` | 0x2F | 0x04FB1 | 56 | 56 | 0 | 36 | 900 000 | 0,30 |
| 44 | `I2F` | 0x66 | 0x0162A | 22 | 22 | 0 | 19 | 900 000 | 0,30 |
| 45 | `ArrSet` | 0x80 | 0x02CC5 | 131 | 131 | 0 | 101 | 900 000 | 0,30 |
| 46 | `StaticFieldGet` | 0x2B | 0x0450B | 361 | 151 | 210 | 161 | 800 000 | 0,27 |
| 47 | `EQ` | 0x50 | 0x0081F | 48 | 48 | 0 | 38 | 800 000 | 0,27 |
| 48 | `Sub` | 0x3E | 0x00636 | 42 | 42 | 0 | 37 | 737 584 | 0,25 |
| 49 | `UnboxDynamic` | 0x74 | 0x01B0E | 137 | 137 | 0 | 83 | 650 000 | 0,22 |
| 50 | `JPLE` | 0xBD | 0x05D51 | 52 | 52 | 0 | 60 | 640 010 | 0,22 |
| 51 | `StrLen` | 0x77 | 0x022E5 | 101 | 101 | 0 | 42 | 600 001 | 0,20 |
| 52 | `JPNE` | 0xB5 | 0x0567B | 52 | 52 | 0 | 52 | 600 000 | 0,20 |
| 53 | `InvokeVirtual` | 0xDB | 0x066F1 | 235 | 163 | 72 | 131 | 400 004 | 0,13 |
| 54 | `InvokeInterface` | 0xDF | 0x0692C | 800 | 202 | 598 | 590 | 400 003 | 0,13 |
| 55 | `PushI16` | 0x07 | 0x0017E | 58 | 58 | 0 | 40 | 380 257 | 0,13 |
| 56 | `DictGet` | 0x90 | 0x0397A | 247 | 124 | 123 | 134 | 360 000 | 0,12 |
| 57 | `BoxInt` | 0x6C | 0x0176E | 160 | 0 | 160 | 76 | 350 000 | 0,12 |
| 58 | `NewFunction` | 0xE3 | 0x04E39 | 176 | 0 | 176 | 86 | 300 004 | 0,10 |
| 59 | `StoreValueField` | 0x35 | 0x07677 | 312 | 230 | 82 | 169 | 300 002 | 0,10 |
| 60 | `Div` | 0x42 | 0x006D1 | 72 | 72 | 0 | 85 | 300 000 | 0,10 |
| 61 | `Inv` | 0x4F | 0x007FA | 37 | 37 | 0 | 29 | 300 000 | 0,10 |
| 62 | `GE` | 0x53 | 0x00CE7 | 48 | 48 | 0 | 41 | 300 000 | 0,10 |
| 63 | `IsPresent` | 0x65 | 0x06FDD | 17 | 5 | 117 | 0 | 300 000 | 0,10 |
| 64 | `CastOrNull` | 0x9B | 0x01F35 | 459 | 170 | 289 | 125 | 300 000 | 0,10 |
| 65 | `JPN` | 0xAB | 0x05057 | 55 | 55 | 0 | 44 | 300 000 | 0,10 |
| 66 | `JPStrNE` | 0xD1 | 0x0571E | 278 | 240 | 38 | 136 | 300 000 | 0,10 |
| 67 | `JPInstanceOf` | 0xD3 | 0x05E6B | 479 | 170 | 309 | 185 | 300 000 | 0,10 |
| 68 | `Switch` | 0xD5 | 0x06258 | 218 | 218 | 0 | 237 | 300 000 | 0,10 |
| 69 | `CallModule` | 0xD9 | 0x06563 | 177 | 0 | 177 | 107 | 300 000 | 0,10 |
| 70 | `Pop` | 0x02 | 0x0026E | 12 | 12 | 0 | 9 | 276 022 | 0,09 |
| 71 | `LT` | 0x54 | 0x00D4C | 48 | 48 | 0 | 38 | 276 022 | 0,09 |
| 72 | `LE` | 0x55 | 0x00DB5 | 48 | 48 | 0 | 41 | 268 768 | 0,09 |
| 73 | `ArrPush` | 0x81 | 0x02D48 | 216 | 127 | 89 | 103 | 215 320 | 0,07 |
| 74 | `Yield` | 0xED | 0x07E3A | 342 | 201 | 141 | 441 | 200 000 | 0,07 |
| 75 | `GenResume` | 0xEB | 0x07B3E | 185 | 120 | 65 | 167 | 150 003 | 0,05 |
| 76 | `GenCurrent` | 0xEC | 0x07DC5 | 91 | 91 | 0 | 82 | 150 000 | 0,05 |
| 77 | `ArrLen` | 0x7E | 0x02BE9 | 98 | 98 | 0 | 37 | 115 005 | 0,04 |
| 78 | `DictSet` | 0x91 | 0x03A71 | 275 | 19 | 256 | 127 | 110 064 | 0,04 |
| 79 | `StrCat` | 0x79 | 0x023E7 | 827 | 381 | 446 | 247 | 101 264 | 0,03 |
| 80 | `Ext` | 0xFF | 0x07F90 | 48 | 48 | 0 | 109 | 100 002 | 0,03 |
| 81 | `Ldc5` | 0x10 | 0x00314 | 22 | 22 | 0 | 19 | 100 001 | 0,03 |
| 82 | `PushAbsent` | 0x0A | 0x06FDD | 17 | 5 | 117 | 0 | 100 000 | 0,03 |
| 83 | `JPLT` | 0xBB | 0x05C47 | 52 | 52 | 0 | 60 | 50 001 | 0,02 |
| 84 | `PushTrue` | 0x04 | 0x00203 | 28 | 28 | 0 | 20 | 50 000 | 0,02 |
| 85 | `GenResumed` | 0xEF | 0x07E20 | 26 | 26 | 0 | 28 | 50 000 | 0,02 |
| 86 | `DictDel` | 0x92 | 0x03B84 | 239 | 185 | 54 | 111 | 30 000 | 0,01 |
| 87 | `DictIn` | 0x96 | 0x03FE1 | 242 | 188 | 54 | 111 | 30 000 | 0,01 |
| 88 | `Throw` | 0xE8 | 0x06D27 | 43 | 5 | 38 | 60 | 8 000 | 0,00 |
| 89 | `Ldc8` | 0x13 | 0x00356 | 22 | 22 | 0 | 19 | 64 | 0,00 |
| 90 | `ArrPack` | 0x7D | 0x02AA0 | 329 | 66 | 263 | 171 | 8 | 0,00 |
| 91 | `GenNew` | 0xE9 | 0x07914 | 474 | 114 | 360 | 265 | 6 | 0,00 |
| 92 | `DictPack` | 0x8E | 0x0364B | 592 | 94 | 498 | 250 | 4 | 0,00 |
| 93 | `StaticFieldSet` | 0x2D | 0x047F5 | 349 | 148 | 201 | 162 | 3 | 0,00 |
| 94 | `GenIterate` | 0xEA | 0x07AEE | 80 | 80 | 0 | 44 | 3 | 0,00 |
| 95 | `GenDelegate` | 0xEE | 0x07BF7 | 462 | 251 | 211 | 504 | 2 | 0,00 |
| 96 | `PushFalse` | 0x05 | 0x0021F | 28 | 28 | 0 | 20 | 1 | 0,00 |
| 97 | `Ldc9` | 0x14 | 0x0036C | 22 | 22 | 0 | 20 | 1 | 0,00 |
| 98 | `ArrNew` | 0x7B | 0x027C2 | 340 | 25 | 315 | 169 | 1 | 0,00 |
| 99 | `DictKeys` | 0x94 | 0x03D38 | 343 | 24 | 319 | 122 | 1 | 0,00 |
| 100 | `NewClosure` | 0xE1 | 0x04AC7 | 429 | 70 | 359 | 173 | 1 | 0,00 |
| 101 | `Nop` | 0x00 | 0x00123 | 5 | 5 | 0 | 0 | — | — |
| 102 | `PushNull` | 0x03 | 0x00138 | 25 | 25 | 0 | 20 | — | — |
| 103 | `PushChar` | 0x09 | 0x0023B | 51 | 51 | 0 | 39 | — | — |
| 104 | `Ldc0` | 0x0B | 0x002A7 | 21 | 21 | 0 | 14 | — | — |
| 105 | `Ldc1` | 0x0C | 0x002BC | 22 | 22 | 0 | 16 | — | — |
| 106 | `Ldc2` | 0x0D | 0x002D2 | 22 | 22 | 0 | 19 | — | — |
| 107 | `Ldc3` | 0x0E | 0x002E8 | 22 | 22 | 0 | 19 | — | — |
| 108 | `Ldc4` | 0x0F | 0x002FE | 22 | 22 | 0 | 19 | — | — |
| 109 | `Ldc` | 0x16 | 0x0027A | 45 | 45 | 0 | 35 | — | — |
| 110 | `LdcX` | 0x17 | 0x00382 | 69 | 69 | 0 | 53 | — | — |
| 111 | `Ldl` | 0x1F | 0x003F0 | 46 | 46 | 0 | 35 | — | — |
| 112 | `Stl0` | 0x20 | 0x004F2 | 25 | 25 | 0 | 14 | — | — |
| 113 | `Stl` | 0x27 | 0x004C4 | 46 | 46 | 0 | 35 | — | — |
| 114 | `StaticFieldGetX` | 0x2C | 0x04674 | 385 | 175 | 210 | 179 | — | — |
| 115 | `StaticFieldSetX` | 0x2E | 0x04952 | 373 | 172 | 201 | 180 | — | — |
| 116 | `StoreLocalField` | 0x33 | 0x07344 | 106 | 106 | 0 | 73 | — | — |
| 117 | `LoadValueStatic` | 0x36 | 0x077AF | 98 | 98 | 0 | 112 | — | — |
| 118 | `StoreValueStatic` | 0x37 | 0x07811 | 105 | 105 | 0 | 130 | — | — |
| 119 | `BoxValue` | 0x38 | 0x073AE | 352 | 65 | 287 | 221 | — | — |
| 120 | `UnboxValue` | 0x39 | 0x0750E | 155 | 155 | 0 | 109 | — | — |
| 121 | `RangePack` | 0x3A | 0x0787A | 38 | 16 | 138 | 0 | — | — |
| 122 | `RangeUnpack` | 0x3B | 0x0787A | 38 | 16 | 138 | 0 | — | — |
| 123 | `FSub` | 0x3F | 0x00660 | 35 | 35 | 0 | 24 | — | — |
| 124 | `FDiv` | 0x43 | 0x00719 | 35 | 35 | 0 | 24 | — | — |
| 125 | `FMod` | 0x45 | 0x00786 | 61 | 0 | 61 | 24 | — | — |
| 126 | `Neg` | 0x46 | 0x007C3 | 30 | 30 | 0 | 26 | — | — |
| 127 | `FNeg` | 0x47 | 0x007E1 | 25 | 25 | 0 | 21 | — | — |
| 128 | `And` | 0x48 | 0x01501 | 42 | 42 | 0 | 37 | — | — |
| 129 | `Or` | 0x49 | 0x0152B | 42 | 42 | 0 | 37 | — | — |
| 130 | `Xor` | 0x4A | 0x01555 | 42 | 42 | 0 | 37 | — | — |
| 131 | `Not` | 0x4B | 0x0157F | 30 | 30 | 0 | 26 | — | — |
| 132 | `Shl` | 0x4C | 0x0159D | 47 | 47 | 0 | 43 | — | — |
| 133 | `Shr` | 0x4D | 0x015CC | 47 | 47 | 0 | 43 | — | — |
| 134 | `Sar` | 0x4E | 0x015FB | 47 | 47 | 0 | 43 | — | — |
| 135 | `NE` | 0x51 | 0x009D1 | 48 | 48 | 0 | 41 | — | — |
| 136 | `GT` | 0x52 | 0x00C82 | 48 | 48 | 0 | 38 | — | — |
| 137 | `FEQ` | 0x56 | 0x0084F | 58 | 58 | 0 | 36 | — | — |
| 138 | `FNE` | 0x57 | 0x00A01 | 58 | 58 | 0 | 39 | — | — |
| 139 | `FGT` | 0x58 | 0x00CB2 | 53 | 53 | 0 | 36 | — | — |
| 140 | `FGE` | 0x59 | 0x00D17 | 53 | 53 | 0 | 39 | — | — |
| 141 | `FLT` | 0x5A | 0x00D7C | 57 | 57 | 0 | 36 | — | — |
| 142 | `FLE` | 0x5B | 0x00DE5 | 57 | 57 | 0 | 39 | — | — |
| 143 | `REQ` | 0x5C | 0x00889 | 48 | 48 | 0 | 38 | — | — |
| 144 | `RNE` | 0x5D | 0x00A3B | 48 | 48 | 0 | 41 | — | — |
| 145 | `StrEQ` | 0x5E | 0x008B9 | 280 | 264 | 16 | 86 | — | — |
| 146 | `StrNE` | 0x5F | 0x00A6B | 279 | 263 | 16 | 86 | — | — |
| 147 | `DynEQ` | 0x60 | 0x00B82 | 128 | 0 | 128 | 62 | — | — |
| 148 | `DynNE` | 0x61 | 0x00C02 | 128 | 0 | 128 | 62 | — | — |
| 149 | `IsNull` | 0x62 | 0x00E1E | 36 | 36 | 0 | 28 | — | — |
| 150 | `IsNotNull` | 0x63 | 0x00E42 | 52 | 52 | 0 | 28 | — | — |
| 151 | `IsAbsent` | 0x64 | 0x06FDD | 17 | 5 | 117 | 0 | — | — |
| 152 | `F2I` | 0x67 | 0x01640 | 181 | 134 | 47 | 91 | — | — |
| 153 | `I2C` | 0x68 | 0x016F5 | 29 | 29 | 0 | 26 | — | — |
| 154 | `C2I` | 0x69 | 0x01712 | 28 | 28 | 0 | 25 | — | — |
| 155 | `I2B` | 0x6A | 0x0172E | 36 | 36 | 0 | 28 | — | — |
| 156 | `B2I` | 0x6B | 0x01752 | 28 | 28 | 0 | 25 | — | — |
| 157 | `BoxFloat` | 0x6D | 0x0180E | 160 | 0 | 160 | 76 | — | — |
| 158 | `BoxBool` | 0x6E | 0x018AE | 160 | 0 | 160 | 76 | — | — |
| 159 | `BoxChar` | 0x6F | 0x0194E | 160 | 0 | 160 | 76 | — | — |
| 160 | `BoxAs` | 0x70 | 0x07057 | 230 | 0 | 230 | 117 | — | — |
| 161 | `BoxAsX` | 0x71 | 0x0713D | 255 | 0 | 255 | 135 | — | — |
| 162 | `Unbox` | 0x72 | 0x019EE | 85 | 85 | 0 | 31 | — | — |
| 163 | `BoxDynamic` | 0x73 | 0x01A43 | 203 | 39 | 164 | 112 | — | — |
| 164 | `RangeNew` | 0x75 | 0x0787A | 38 | 16 | 138 | 0 | — | — |
| 165 | `RangeNewInclusive` | 0x76 | 0x0787A | 38 | 16 | 138 | 0 | — | — |
| 166 | `StrGet` | 0x78 | 0x02722 | 160 | 160 | 0 | 104 | — | — |
| 167 | `StrHash` | 0x7A | 0x0234A | 157 | 95 | 62 | 37 | — | — |
| 168 | `ArrNewX` | 0x7C | 0x02916 | 394 | 38 | 356 | 197 | — | — |
| 169 | `ArrPop` | 0x82 | 0x02E20 | 122 | 122 | 0 | 100 | — | — |
| 170 | `ArrInsert` | 0x83 | 0x02E9A | 214 | 94 | 120 | 117 | — | — |
| 171 | `ArrRemoveAt` | 0x84 | 0x02F70 | 148 | 87 | 61 | 86 | — | — |
| 172 | `ArrClear` | 0x85 | 0x03004 | 128 | 93 | 35 | 24 | — | — |
| 173 | `ArrIndexOf` | 0x86 | 0x03084 | 157 | 19 | 138 | 62 | — | — |
| 174 | `ArrIn` | 0x87 | 0x03121 | 175 | 19 | 156 | 68 | — | — |
| 175 | `TupPack` | 0x88 | 0x031D0 | 399 | 77 | 322 | 159 | — | — |
| 176 | `TupUnpack` | 0x89 | 0x0335F | 139 | 139 | 0 | 75 | — | — |
| 177 | `TupLen` | 0x8A | 0x033EA | 93 | 93 | 0 | 39 | — | — |
| 178 | `TupGet` | 0x8B | 0x03447 | 125 | 125 | 0 | 92 | — | — |
| 179 | `TupGetC` | 0x8C | 0x034C4 | 132 | 132 | 0 | 93 | — | — |
| 180 | `DictNew` | 0x8D | 0x03548 | 259 | 0 | 259 | 101 | — | — |
| 181 | `DictLen` | 0x8F | 0x0389B | 223 | 109 | 114 | 68 | — | — |
| 182 | `DictClear` | 0x93 | 0x03C73 | 197 | 75 | 122 | 58 | — | — |
| 183 | `DictValues` | 0x95 | 0x03E8F | 338 | 24 | 314 | 122 | — | — |
| 184 | `InstanceOf` | 0x97 | 0x00E76 | 352 | 129 | 223 | 129 | — | — |
| 185 | `InstanceOfX` | 0x98 | 0x00FD6 | 376 | 129 | 247 | 147 | — | — |
| 186 | `Cast` | 0x99 | 0x01B97 | 451 | 140 | 311 | 143 | — | — |
| 187 | `CastX` | 0x9A | 0x01D5A | 475 | 140 | 335 | 161 | — | — |
| 188 | `CastOrNullX` | 0x9C | 0x02100 | 485 | 159 | 326 | 143 | — | — |
| 189 | `LoadType` | 0x9D | 0x0114E | 273 | 0 | 273 | 121 | — | — |
| 190 | `LoadTypeX` | 0x9E | 0x0125F | 297 | 5 | 292 | 139 | — | — |
| 191 | `GetTypeOfValue` | 0x9F | 0x01388 | 246 | 74 | 172 | 89 | — | — |
| 192 | `LoadModule` | 0xA0 | 0x0147E | 43 | 0 | 131 | 0 | — | — |
| 193 | `LoadModuleX` | 0xA1 | 0x0147E | 43 | 0 | 131 | 0 | — | — |
| 194 | `LoadCurrentModule` | 0xA2 | 0x0147E | 43 | 0 | 131 | 0 | — | — |
| 195 | `ObjNewX` | 0xA4 | 0x040D3 | 71 | 16 | 127 | 0 | — | — |
| 196 | `JPX` | 0xA6 | 0x05225 | 57 | 57 | 0 | 45 | — | — |
| 197 | `JPZX` | 0xA8 | 0x050ED | 78 | 78 | 0 | 61 | — | — |
| 198 | `JPNZ` | 0xA9 | 0x05020 | 55 | 55 | 0 | 44 | — | — |
| 199 | `JPNZX` | 0xAA | 0x0513B | 78 | 78 | 0 | 61 | — | — |
| 200 | `JPNX` | 0xAC | 0x05189 | 78 | 78 | 0 | 61 | — | — |
| 201 | `JPNN` | 0xAD | 0x0508E | 55 | 55 | 0 | 44 | — | — |
| 202 | `JPNNX` | 0xAE | 0x051D7 | 78 | 78 | 0 | 61 | — | — |
| 203 | `JPA` | 0xAF | 0x06FDD | 17 | 5 | 117 | 0 | — | — |
| 204 | `JPAX` | 0xB0 | 0x06FDD | 17 | 5 | 117 | 0 | — | — |
| 205 | `JPNA` | 0xB1 | 0x06FDD | 17 | 5 | 117 | 0 | — | — |
| 206 | `JPNAX` | 0xB2 | 0x06FDD | 17 | 5 | 117 | 0 | — | — |
| 207 | `JPEQ` | 0xB3 | 0x0525E | 52 | 52 | 0 | 52 | — | — |
| 208 | `JPEQX` | 0xB4 | 0x05440 | 75 | 75 | 0 | 69 | — | — |
| 209 | `JPNEX` | 0xB6 | 0x05834 | 75 | 75 | 0 | 77 | — | — |
| 210 | `JPGT` | 0xB7 | 0x05A43 | 52 | 52 | 0 | 60 | — | — |
| 211 | `JPGTX` | 0xB8 | 0x05AAD | 75 | 75 | 0 | 77 | — | — |
| 212 | `JPGEX` | 0xBA | 0x05BAF | 75 | 75 | 0 | 77 | — | — |
| 213 | `JPLTX` | 0xBC | 0x05CB5 | 75 | 75 | 0 | 77 | — | — |
| 214 | `JPLEX` | 0xBE | 0x05DBF | 75 | 75 | 0 | 77 | — | — |
| 215 | `JPFEQ` | 0xBF | 0x05292 | 60 | 60 | 0 | 50 | — | — |
| 216 | `JPFEQX` | 0xC0 | 0x0548B | 83 | 83 | 0 | 67 | — | — |
| 217 | `JPFNE` | 0xC1 | 0x056AF | 59 | 59 | 0 | 58 | — | — |
| 218 | `JPFNEX` | 0xC2 | 0x0587F | 79 | 79 | 0 | 75 | — | — |
| 219 | `JPFGT` | 0xC3 | 0x05A77 | 54 | 54 | 0 | 58 | — | — |
| 220 | `JPFGTX` | 0xC4 | 0x05AF8 | 77 | 77 | 0 | 75 | — | — |
| 221 | `JPFGE` | 0xC5 | 0x05B79 | 54 | 54 | 0 | 58 | — | — |
| 222 | `JPFGEX` | 0xC6 | 0x05BFA | 77 | 77 | 0 | 75 | — | — |
| 223 | `JPFLT` | 0xC7 | 0x05C7B | 58 | 58 | 0 | 58 | — | — |
| 224 | `JPFLTX` | 0xC8 | 0x05D00 | 81 | 81 | 0 | 75 | — | — |
| 225 | `JPFLE` | 0xC9 | 0x05D85 | 58 | 58 | 0 | 58 | — | — |
| 226 | `JPFLEX` | 0xCA | 0x05E0A | 97 | 97 | 0 | 75 | — | — |
| 227 | `JPREQ` | 0xCB | 0x052CE | 52 | 52 | 0 | 52 | — | — |
| 228 | `JPREQX` | 0xCC | 0x054DE | 75 | 75 | 0 | 69 | — | — |
| 229 | `JPRNE` | 0xCD | 0x056EA | 52 | 52 | 0 | 60 | — | — |
| 230 | `JPRNEX` | 0xCE | 0x058CE | 75 | 75 | 0 | 77 | — | — |
| 231 | `JPStrEQ` | 0xCF | 0x05302 | 318 | 292 | 26 | 99 | — | — |
| 232 | `JPStrEQX` | 0xD0 | 0x05529 | 338 | 312 | 26 | 116 | — | — |
| 233 | `JPStrNEX` | 0xD2 | 0x05919 | 298 | 260 | 38 | 153 | — | — |
| 234 | `JPInstanceOfX` | 0xD4 | 0x0604A | 526 | 169 | 357 | 220 | — | — |
| 235 | `SwitchLookup` | 0xD6 | 0x06332 | 329 | 329 | 0 | 362 | — | — |
| 236 | `CallLocalModuleX` | 0xD8 | 0x064E3 | 128 | 0 | 128 | 89 | — | — |
| 237 | `CallModuleX` | 0xDA | 0x06614 | 221 | 0 | 221 | 143 | — | — |
| 238 | `InvokeStatic` | 0xDD | 0x06844 | 104 | 0 | 104 | 71 | — | — |
| 239 | `InvokeStaticX` | 0xDE | 0x068AC | 128 | 0 | 128 | 89 | — | — |
| 240 | `NewClosureX` | 0xE2 | 0x04C74 | 453 | 70 | 383 | 191 | — | — |
| 241 | `NewFunctionX` | 0xE4 | 0x04EE9 | 200 | 0 | 200 | 104 | — | — |
