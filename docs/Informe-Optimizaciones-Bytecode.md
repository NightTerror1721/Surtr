# Informe: optimizaciones adicionales de bytecode y compilación para Surtr

Fecha: 2026-08-25. Alcance: rendimiento en **ejecución** (bytecode + despacho de la VM), no
velocidad de compilación — esa dimensión ya está cubierta por `docs/Compiler-Optimization-Plan.md`.
Este informe es solo investigación: no modifica código fuente.

Método: lectura del intérprete (`src/Surtr.Core/VM/SurtrVirtualMachine.cs`, 4474 líneas), del
emisor (`src/Surtr.Compiler/CodeGen/`), de los planes existentes (`docs/Opcodes.md`,
`docs/VM-Plan.md`, `docs/Runtime-Model.md`) y de la última corrida de benchmarks
(`benchmark_report.md`, `bench_results.csv`). Búsqueda de marcas TODO/HACK/FIXME: **no hay ninguna**
en `src/` relacionada con rendimiento; las oportunidades documentadas viven en `docs/VM-Plan.md`
(§3 y fase 5).

---

> **Nota de seguimiento (2026-08-26).** Este informe sigue siendo la investigacion de partida, y
> su inventario de §2.3 es exacto. Tres de sus propuestas han sido revisadas en
> `docs/Plan-Opcodes-Extendidos.md`, que es el plan que se esta ejecutando:
>
> - **P3** queda recortada: afirma que `IntEntries` nunca vuelve a null, y
>   `SurtrDictionary.Deoptimize()` lo pone a null desde el host, asi que el opcode rapido esta
>   obligado a conservar el test.
> - **P4 y P5** quedan supersedidas por el grupo A de ese plan, que colapsa el ciclo entero de un
>   `for-in` en una instruccion en vez de solo el paso.
> - **P9 esta cerrada en negativo, medida.** El mismo merge sort escrito en Surtr sale un 54 % mas
>   lento que el nativo (14.62 ms contra 9.47): el merge que el nativo obtiene gratis de C# cuesta
>   mas en bytecode que la reentrada en la frontera. Lo que si valia era la asignacion que la
>   medicion destapo - `ArraySort` asignaba 156 KB por llamada, y ahora no asigna nada.
> - **P7 tiene por fin su numero: 1.25 ns por llamada cruzada de modulo**, medido con dos casos
>   gemelos (`crossModule` contra `localModule`, el mismo cuerpo alcanzado por CallModule y por
>   CallLocalModule). La estimacion del informe era correcta, y ese es el techo de lo que la
>   propuesta podria devolver. Cerrada por coste/beneficio, no por error.
> - **P8 y P2/P3 quedan cerradas por una razon que el informe no podia conocer**: `Run()` ha
>   llegado a un tamano donde anadir cuerpos de opcode mueve el rendimiento de todo el interprete
>   en un +-20-45 % por caso, de forma impredecible. El presupuesto real no es de valores de opcode
>   sino de cuerpos. `docs/Plan-Opcodes-Extendidos.md` §11.
> - La escala de beneficio se recalibro: un despacho vale ~1 ns y un test de tipo predicho ~0.25 ns,
>   asi que P2 y P3 sueltas quedan dentro del ruido y solo pagan fusionadas.

---

## 1. Resumen ejecutivo

Surtr ya aplica casi todo el catálogo clásico de un intérprete sin JIT bien construido: despacho
por switch único con estado caliente en locales, opcodes tipados por familia de operando, familia
completa de compare-and-branch fusionados, codificaciones de ancho mínimo, plegado de `const fun`
ejecutando bytecode real, inlining por coste, lowering especializado de bucles, caché monomórfica
de interfaz y almacén especializado para diccionarios con clave `int`. Los benchmarks sitúan el
intérprete a 18.5x de MoonSharp y a ~3.3x de LuaJIT en media geométrica.

Lo que queda son huecos concretos y medibles, casi todos de la misma naturaleza: **el compilador
conoce en tiempo de emisión información que el opcode genérico vuelve a descubrir en tiempo de
ejecución** (si un campo es nativo, si una clave de diccionario es `int`, si ambos operandos son
literales, si el guard de un bucle ya acotó el índice). Este informe propone 9 mejoras accionables
en tres niveles, más 7 técnicas evaluadas y descartadas con su razón (varias ya estaban cerradas
por medición previa en este mismo repositorio). Todas caben en las restricciones reales:
netstandard2.1, IL2CPP, hot path sin asignaciones, despacho sin llamadas virtuales ni delegados; y
los nuevos opcodes necesarios caben en los 16 valores libres `0xF0`–`0xFF` sin renumerar el formato.

Prioridad recomendada: P1 (plegado constante), P2 (campos nativos especializados) y P3
(diccionarios int estáticos) primero; después P4 (paso de bucle contado) y P5 (lectura indexada sin
comprobación duplicada); el resto según medidas.

---

## 2. Investigación: cómo funciona hoy

### 2.1 Pipeline de generación de código

```
Sintaxis (.surtr)
  -> Binding (Binding/Symbols, Binding/BoundTree)   [tipado estático, resolución, inferencia]
  -> CodeGen/ModuleEmitter                          [declara módulos, clases, tablas]
     -> CodeGen/MethodBodyEmitter (por método)      [baja el BoundTree a instrucciones]
        -> SurtrCodeEmitter (+ .Helpers/.OpCodes)   [elige codificación, relaja saltos]
           -> SurtrModuleBuilder.Build()            [cuerpos -> chunk no gestionado]
              -> Bytecode/Image (formato v10)
  -> ConstFolder: ejecuta `const fun`s sobre un SurtrRuntime real y pliega sus llamadas
```

Puntos clave verificados en el código:

- El emisor trabaja sobre el árbol bindeado, sin IR propia: cada método de `MethodBodyEmitter`
  (6096 líneas) reconoce formas del BoundTree y elige opcodes (`EmitBinary` en :2304, `EmitCall`
  en :4117, `EmitForInIndexed` en :775).
- La elección de codificación vive en un tercer nivel (`SurtrCodeEmitter.Helpers.cs`):
  `LoadConstant` elige `Ldc0..9/LdcS/Ldc/LdcX` (:89-108), `LoadInt` elige
  `PushI8/PushI16/PushI32` (:110-125), y los accesos a tablas eligen variante corta o `X` según
  el índice (:161-167, :200-233). El compilador nunca escribe un opcode a mano.
- Los saltos se emiten cortos y un pase de **relajación** los ensancha a punto fijo
  (`SurtrCodeEmitter.cs`:55, `Branch` :448-476, `CanWiden` :105). No existe pase peephole
  posterior ni análisis de liveness sobre el bytecode emitido.
- El formato de opcodes es estable: valores escritos a mano en `OpCode.cs` (240 asignados,
  `0x00`–`0xEF`), **libres `0xF0`–`0xFF`**; una renumeración exige subir
  `SurtrModuleImage.FormatVersion` (ocurrió dos veces; `docs/Opcodes.md` §2).

### 2.2 El intérprete: estructura del despacho

`Run()` (`SurtrVirtualMachine.cs:1101`) concentra las decisiones de diseño:

| Decisión | Dónde | Detalle |
|---|---|---|
| Un solo `switch` con `goto Dispatch` | :1202-1203 | Sin tabla de punteros: cada opcode es un caso del jump table que genera el JIT. Justificado en `docs/VM-Plan.md` §1.1. |
| `[MethodImpl((MethodImplOptions)512)]` | :1095-1100 | `AggressiveOptimization` por valor numérico (netstandard2.1 no lo nombra); saca el bucle del tiering. |
| Estado caliente en locales | :1107-1151 | `ip`, `sp`, `frameBase`, tablas del chunk y `entities`; `LoadFrame` (:1153-1167) las recarga al cambiar de frame. |
| Inmediatos leídos byte a byte | :1169-1184 | Alineación y endianness canónica; lecturas anchas reservadas a `sp` (slots alineados a 8). |
| Presupuesto cobrado por transferencia | :1186-1200 | Etiqueta `Branched`: saltos, switches y entradas de frame pagan; el despacho lineal no. |
| Safepoint único de GC | :4145-4155 | Los opcodes que asignan terminan en `Safepoint`; el límite nativo repite el chequeo (:4265-4275). |
| Secuencias compartidas por `goto` | :4169, :4239 | `EnterGeneratorFrame` e `InvokeResolved` comparten la entrada de frame sin pagar una llamada real. |
| Trampas `NoInlining` | :4369-4451 | Formato de mensajes fuera de la asignación de registros del bucle. |

La pila de datos es un bloque no gestionado plano de `SurtrRawValue` (NaN-boxed) donde los frames
se solapan — los argumentos del callee son el tope del caller — con capacidad fija y un único
chequeo de overflow por llamada (:4292-4299). La pila de llamadas es gestionada
(`SurtrCallFrame[]`) porque guarda referencias vivas para el CLR.

### 2.3 Inventario de optimizaciones YA existentes

**Codificación y despacho**

1. Codificaciones de ancho mínimo por frecuencia: `Ldc0..9` (:1260-1269), `Ldl0..5`/`Stl0..5`
   (:1285-1306), `LdcS/LdlS/StlS` de 1 byte, variantes `X` de 32 bits; selección automática en
   `SurtrCodeEmitter.Helpers.cs`.
2. Opcodes tipados por familia de operando: `Add`/`FAdd`, `EQ`/`FEQ`/`REQ`/`StrEQ`/`DynEQ`, etc.
   (:1325-1568). El opcode no paga ningún test de tag.
3. Compare-and-branch fusionados `JPEQ..JPFLEX` + variantes `X` (:2876-3182), `JPA/JPNA(X)` de
   ausencia (:3656-3694), saltos de nulidad. Producidos por `EmitConditionalJump`/
   `TryFusedComparison` (`MethodBodyEmitter.cs`:462-618).
4. `Switch` con tabla densa y `SwitchLookup` binaria, elegidos por factor de densidad 2
   (`Helpers.cs`:585-622); el `switch` de strings baja a `StrHash` + `SwitchLookup` + `StrEQ`
   (hash cacheado, VM :1976-1982).
5. Relajación de saltos de dos anchos con punto fijo (`SurtrCodeEmitter.cs`:448-476).
6. `IncLocal`: `i += k` entero sobre local en una instrucción (VM :1312-1321; emisor
   `TryEmitInPlaceIncrement` :3174-3214, `TryEmitInPlaceStep` :3220-3233).

**Compilador**

7. Inlining: `forceinline`, pista `inline`, heurística por coste (umbral 2 normal, 8 con `inline`,
   profundidad máxima 8) — `InlineCost.cs`:29-38, `MethodBodyEmitter.cs`:124, `TryInline`
   :4737-4848, con camino rápido para retorno único (:4814-4828) y argumentos en slots reales.
8. Plegado de `const fun` ejecutando bytecode real sobre un runtime de descarte con presupuesto de
   10M pasos (`ConstFolder.cs`:45-174; gancho `TryFoldConstCall` :4668-4706).
9. Miembros de colecciones incorporadas bajados a opcodes dedicados (`OpcodeableMembers`
   :4644-4666; diccionario :4337, array :4434, string :4535).
10. Accessors triviales inlineados (:3594, :3677, :3799); `length` de las cuatro colecciones a
    opcode directo (:3479-3573).
11. Lowering de bucles: rango sin construir el rango (:684-768), indexado array/string/tupla
    (:775-824), snapshot de claves de diccionario (:855), reanudación directa de generadores
    (:951); solo la ruta general paga interface calls (:1003).
12. Value classes y tuplas como bloques multi-slot: `LoadValueLocal/StoreValueLocal`,
    `LoadLocalField/StoreLocalField` (VM :3756-3775), `ReturnValues` consciente del solape
    (:3583-3635), `TupGetC` con índice constante (:2308-2325), slots de rango (:4623-4642).
13. Especializaciones de nulos/ausencia: `IsNull/IsNotNull` sin empujar el literal (:2336-2352),
    tests de ausencia fundidos (:2650-2712), `CastOrNull` como opcode (VM :1929-1967).
14. Concatenación n-aria: la espina `a + b + c` se pliega en un `StrCat` con contador
    (:2370-2376); camino rápido de 2 operandos en VM (:1996-2001) y buffer reutilizable (:90-115).
15. `discardResult`: los opcodes de llamada llevan si se quiere resultado, eliminando el `Pop`
    (`Helpers.cs`:628-636).

**Intérprete/runtime**

16. Caché monomórfica de `InvokeInterface` por chunk (`SurtrChunk.cs`:72-86; acierto en VM
    :3380-3381) sobre la sonda open-addressed de `InterfaceIndexById`. La virtual resuelve con dos
    cargas por vtable y no lleva caché (:3324-3337) — cerrado con medición (§2.6).
17. Diccionarios con clave `int`: almacén lateral `IntEntries` que evita el comparador
    (`DictGet/Set/Del/In`, VM :2390-2506; `DictPack` escrito a mano :2356-2375).
18. `ArrPush` escrito en línea (:2157-2175); `ArrNew` solo retaguea familias cuyo cero no es
    todo-bits-cero (:2054-2063); cero de frames pequeño en línea (:4306-4320).
19. Subtipo O(1): `IsSubclassOf` compara `Ancestors[depth]` sin recorrer la cadena
    (`SurtrClass.cs`:359-373).
20. Cierres sin captura canónicos cacheados (`NewFunction`, VM :2763-2776); pool thread-local de
    buffers no gestionados por clases de tamaño (`SurtrValueBufferPool.cs`).
21. Excepciones por tablas de handlers sin excepción CLR mientras haya handler alcanzable
    (:907-1025) — 71x más rápido que C# lanzando y capturando.

### 2.4 Estado de los benchmarks

Corrida del 2026-08-23 (`bench_results.csv`; Ryzen 9800X3D, .NET 8.0.13, Release, 15 iteraciones +
5 calentamiento + 3 rondas barajadas, verificación por checksum). Media geométrica: 18.5x sobre
MoonSharp, ~3.3x bajo LuaJIT. Filas relevantes:

| Caso | surtr ms | vs C# | Lectura |
|---|---|---|---|
| `intLoop` (1M) | 10.099 | 4.4x | Lo mejor del bloque aritmético. |
| `floatLoop` (1M) | 8.697 | 7.6x | Peor que el entero. |
| `fieldAccess` (300K) | 5.784 | 8.4x | Par get/set de campo de instancia. |
| `propertyAccess` (300K) | 3.878 | 5.6x | Mejor que campos gracias al inlining de accessors. |
| `arrayIndex` (300K) | 6.148 | 8.8x | `ArrGet`/`ArrSet` sobre array dimensionado. |
| `dictOps` / `dictMembers` | 0.846 / 1.308 | 3.8x / 3.4x | La mejor relación de la suite (almacén `int`). |
| `dictString` (300K) | 7.459 | 4.0x | Ruta del comparador. |
| `enums` (300K) | 7.778 | 10.9x | Acceso a casos + comparación. |
| `typeTest` (300K) | 7.740 | 5.6x | `InstanceOf`/`CastOrNull`. |
| `methodCalls` / `virtualCalls` / `interfaceCalls` | 4.241 / 6.839 / 6.624 | 5.7x / 9.9x / 9.6x | El delta virtual−directo (~6 ns) fija el techo de cualquier mejora de resolución. |
| `vec2Math` / `vec2Fields` | 31.0 / 34.0 | 62.9x / 39.1x | Coste del protocolo de frame, no de asignación (0 B). |
| `generics` | 22.251 | 29.5x | Boxing por borrado: cerrado por diseño. |
| `iterator` vs `forIn` | 3.592 vs 0.957 | 17.1x vs 6.5x | Mide lo que vale el lowering de `for-in`. |
| `stringInterp` / `stringConcat` | 9.623 / 0.061 | 3.8x / 1.6x | `StrCat` n-ario ya evita temporales. |
| `sortArray` | 9.203 | 10.4x | El comparador nativo reentra en la VM por comparación. |
| `closureCreate` / `methodGroupInvoke` | 10.350 / 11.316 | 15.1x / 16.4x | Construcción/invocación de cierres. |

Advertencia de la propia suite (§6 del informe): nueve casos con dispersión >10 %, todos con
medianas bajo ~4 ms; cualquier A/B nuevo necesita tamaños mayores, no más iteraciones.

### 2.5 Restricciones que condicionan cualquier propuesta

1. **netstandard2.1 / Unity IL2CPP.** Sin `System.Runtime.Intrinsics` (SIMD explícito fuera), sin
   `Unsafe.Add` sin NuGet adicional (`docs/VM-Plan.md` §3.2), sin `AggressiveOptimization`
   nominado (ya resuelto por valor numérico en :1095-1100).
2. **Hot path sin asignaciones.** Cualquier "caché" nueva debe ser array preasignado o memoria no
   gestionada; nada por instrucción que llame al recolector.
3. **Despacho simple.** Ni delegados ni llamadas virtuales por instrucción; los cuerpos de opcode
   se escriben en línea dentro de `Run()` porque una llamada derramaría `ip`/`sp` (:42-46,
   :1082-1088).
4. **Formato congelado salvo decisión explícita.** Quedan 16 valores libres (`0xF0`–`0xFF`);
   renumerar exige `FormatVersion` y recompilar todo.
5. **Política de validación.** Los traps explícitos son contrato (`docs/VM-Plan.md` §1.9):
   eliminar comprobaciones solo donde el compilador prueba redundancia.

### 2.6 Hipótesis ya probadas o cerradas — no re-proponer sin evidencia nueva

| Idea | Veredicto | Evidencia |
|---|---|---|
| Caché monomórfica en `InvokeVirtual` | Probada y retirada (ago 2026) | A/B con caché gemela de la de interfaz: 6.5 ms base vs 6.7 ms con caché, ±4 % de dispersión; la resolución virtual ya son dos cargas dependientes. `benchmark_report.md` §8.2, `docs/VM-Plan.md` fase 5, `SurtrChunk.cs`:83-85. |
| Incrustar slots de `SurtrInstance` (quitar el array `Fields`) | Medido y cerrado | Techo ~2 % de `allocation` (~1 ns/iter); bifurcaría el layout que leen `FieldGet`, el trazado y el indexador. `benchmark_report.md` §8.1. |
| Internamiento de cajas pequeñas (`BoxInt` -128..127) | Rechazado por semántica | Cambia la identidad observable de referencias (`===` compara payloads). `benchmark_report.md` §8.1. |
| Quitar el boxing de genéricos | Cerrado por diseño | Precio declarado del borrado estilo Java; la alternativa son los value classes. §8.1. |
| Segundo chequeo de límites vía `Unsafe.Add` | Bloqueado por framework | `docs/VM-Plan.md` §3.2. Nota: esa sección está desactualizada — `SurtrArray.Items` es hoy un puntero no gestionado (`SurtrArray.cs`:50) y `ArrGet` paga una sola comprobación (comentario en VM :2134-2136). Conviene actualizar el doc. |

---

## 3. Catálogo de propuestas

Organizadas por esfuerzo/beneficio. Cada una indica descripción, ficheros, riesgo, beneficio
esperado y benchmark de medición.

### Nivel 1 — Victorias rápidas (días, riesgo bajo)

#### P1. Plegado constante en la emisión, reutilizando `ConstantOf`

- **Descripción.** El emisor ya sabe evaluar expresiones de literales: `ConstantOf`
  (`MethodBodyEmitter.cs`:5970-6019) pliega binarias, unarias y conversiones — pero **solo se
  usa** para claves de `switch` y argumentos de `const fun`. Una expresión como `let n = 2 * 3;`
  o un límite de bucle `N - 1` emite hoy `PushI8, PushI8, Mul`, etc. Extender `EmitBinary`
  (:2304), `EmitUnary` (:3073) y la cola de conversión para que, cuando `ConstantOf(expr)`
  devuelva valor, emitan el literal plegado. Dos cautelas: (a) envolver a 32 bits antes de
  decidir si cabe — la VM opera en `int` con envoltura y el plegado actual opera en `long`;
  (b) no plegar división/módulo por cero ni nada que trapee en ejecución: se emite la instrucción
  y el trap conserva su semántica observable.
- **Ficheros.** `src/Surtr.Compiler/CodeGen/MethodBodyEmitter.cs`.
- **Riesgo.** Bajo: el evaluador existe; los tests dorados de bytecode detectan divergencias. El
  único peligro real es la fidelidad de desbordamientos; se acota con pruebas de envoltura int32.
- **Beneficio esperado.** Pequeño en la suite actual (ningún caso es constante-pesado), pero
  reduce instrucciones en todo código con tamaños/constantes derivadas y reduce tamaño de imagen.
- **Medición.** Nuevo microcaso en `src/Surtr.Bench` (bucle con límites derivados de constantes);
  bytes de imagen antes/después sobre la stdlib compilada (`Surtr.Stdlib/disasm`).

#### P2. Opcodes especializados para campos nativos: quitar el test por acceso

- **Descripción.** Cada `FieldGet`/`FieldSet`/`StaticFieldGet(X)`/`StaticFieldSet(X)` ejecuta hoy
  un `field is SurtrNativeFieldInfo` antes del acceso gestionado (VM :2548-2608, :2610-2719). El
  compilador **sabe** si el campo es nativo en tiempo de emisión (`EmitFieldRead` :3388,
  `Field()` :5950 resuelve el `SurtrFieldInfo` concreto), así que el test es trabajo repetido en
  una de las instrucciones más frecuentes (`fieldAccess`: 8.4x sobre C#; los casos de enum, que
  son campos estáticos, pasan por ahí también: `enums` 10.9x). Añadir cuatro opcodes
  `NativeFieldGet/NativeFieldSet/NativeStaticFieldGet/NativeStaticFieldSet` que ejecutan solo la
  mitad correspondiente, emitidos cuando el campo resuelto sea `SurtrNativeFieldInfo`; el opcode
  genérico queda para imágenes antiguas y para campos normales.
- **Ficheros.** `src/Surtr.Core/Bytecode/OpCode.cs` (4 valores libres), `Emit/SurtrCodeEmitter.OpCodes.cs`,
  `Emit/SurtrCodeEmitter.Helpers.cs` (`LoadField/StoreField/LoadStaticField/StoreStaticField`
  :156-179), `src/Surtr.Compiler/CodeGen/MethodBodyEmitter.cs` (:3388-3456 y escrituras),
  `src/Surtr.Core/VM/SurtrVirtualMachine.cs` (4 casos nuevos que replican las mitades nativas ya
  escritas), `src/Surtr.Core/Bytecode/Emit/SurtrBytecodeDisassembler.cs`.
- **Riesgo.** Bajo-medio: son 8 sitios de emisión y 4 cuerpos nuevos en el switch; sin cambios de
  formato (caben en `0xF0`–`0xFF`) ni de protocolo.
- **Beneficio esperado.** Elimina una comparación de tipo + rama por acceso a campo nativo o
  estático. Estimación prudente: 3-8 % en `fieldAccess`, algo en `enums` y `propertyAccess`
  (los accessors inlineados terminan en `FieldGet`).
- **Medición.** `fieldAccess`, `propertyAccess`, `enums`, `vec2Fields` con el protocolo habitual
  (`--extreme`, 3 rondas barajadas, mediana).

#### P3. Opcodes de diccionario para clave `int` estática

- **Descripción.** `DictGet`/`DictSet`/`DictIn`/`DictDel` comprueban en cada operación dos cosas
  que el compilador suele saber: que el tag del raw es entero y que `IntEntries != null`
  (VM :2390-2506). Cuando el tipo declarado del diccionario es `dict<int, V>`, ambas condiciones
  son invariantes del objeto: si nació con almacén int lo sigue siendo. Cuatro opcodes
  `DictGetI/DictSetI/DictInI/DictDelI` que asumen almacén int (con fallback de seguridad al
  general si `IntEntries` fuera null, o documentando la invariante y confiando) eliminan el test
  de tag, el test de null y la bifurcación por operación. La semántica de trap por clave ausente
  se mantiene idéntica.
- **Ficheros.** `OpCode.cs`, `SurtrCodeEmitter.OpCodes.cs` + `.Helpers.cs`
  (`TryEmitDictionaryOperation` en `MethodBodyEmitter.cs`:4337-4433 e indexación `m[k]`),
  `SurtrVirtualMachine.cs`, disassembler.
- **Riesgo.** Bajo-medio. Punto delicado: la des-especialización — si algún día un diccionario
  `int` pudiera degradar a almacén general, el opcode rápido debe detectarlo; hoy `IntEntries`
  nunca vuelve a null tras crearse, pero hay que fijarlo como contrato en `SurtrDictionary`.
- **Beneficio esperado.** Una o dos comparaciones menos por operación en los benches de
  diccionario ya dominantes (3.4x-4.0x sobre C#): estimación 5-10 % en `dictOps`/`dictMembers`,
  menor en `for-in` sobre diccionario.
- **Medición.** `dictOps`, `dictMembers`, `dictString` (esta última no cambia: clave string),
  `forIn` variante diccionario.

### Nivel 2 — Esfuerzo medio (1-2 semanas, riesgo controlado)

#### P4. Superinstrucción de paso de bucle contado (estilo FORLOOP de Lua)

- **Descripción.** El paso de todo bucle contado cuesta hoy hasta cinco despachos:
  `IncLocal` + `Ldl i` + `Ldl limit` + `JPcmpX` + `JP` (visible en `EmitForInRange` :748-767 y en
  cualquier `for`). Lua 5.x dedica FORPREP/FORLOOP a exactamente este patrón. Un opcode
  `LoopStep varSlot(1) limitSlot(1) offset(2)` que incremente `var`, compare contra `limit` y salte
  atrás si procede colapsa el paso a un despacho. El patrón es tan regular que el emisor puede
  reconocerlo en sus tres lowerings de bucle y en `EmitFor` genérico; donde no encaje, se sigue
  emitiendo la secuencia actual.
- **Ficheros.** `OpCode.cs`, `SurtrCodeEmitter.Helpers.cs` (helper `LoopStep`),
  `MethodBodyEmitter.cs` (:684-824 y :409-443), `SurtrVirtualMachine.cs` (un caso nuevo junto a
  `IncLocal`).
- **Riesgo.** Medio-bajo: instrucción nueva con contrato claro (slots de frame, offset relativo);
  no toca frames ni llamadas. El presupuesto por transferencia debe cobrarla como `Branched`.
- **Beneficio esperado.** En `intLoop` (10.099 ms / 1M iter = ~10 ns/iteración totales) el paso es
  buena parte del cuerpo: estimación 10-20 %. También `floatLoop` (el paso entero es igual),
  `forIn` y `arrayIndex` cuando el bucle exterior es contado.
- **Medición.** `intLoop`, `floatLoop`, `forIn`, `arrayFill`.

#### P5. Lectura indexada sin comprobación duplicada en `for-in`

- **Descripción.** El lowering indexado compara `index >= len` al inicio de cada iteración
  (`EmitForInIndexed` :801-805) y acto seguido hace `ArrGet`/`StrGet`/`TupGet`, que repiten la
  comprobación y el trap (VM :2122-2138, :2025-2039, :2292-2306). Entre el guard y la lectura no
  corre código de usuario: la secuencia es guard -> cargar fuente -> cargar índice -> leer. Por
  tanto la segunda comprobación es redundante **en ese patrón exacto**. Un par de opcodes
  `ArrGetU`/`StrGetU` (sin trap, contrato "el emisor garantiza el límite") emitidos solo desde ese
  lowering mantiene la política de validación intacta en todos los demás usos. Alternativa aún
  mejor: fusionar la lectura con los operandos en slots (`GetIndexed srcSlot(1) idxSlot(1)`),
  ahorrando además las dos cargas de locales.
- **Ficheros.** `OpCode.cs`, `SurtrCodeEmitter.OpCodes.cs`, `MethodBodyEmitter.cs` (:801-814 y
  helpers `Length`/`Element` :826-844), `SurtrVirtualMachine.cs`.
- **Riesgo.** Medio: es la única propuesta que introduce un opcode "inseguro por contrato". Se
  acota emitiéndolo exclusivamente desde `EmitForInIndexed` y documentando el invariante junto al
  opcode (patrón ya usado por `TupGetC`, cuyo índice constante también descarga la comprobación al
  compilador).
- **Beneficio esperado.** Una comparación+rama menos por elemento. `arrayIndex` está a 8.8x y
  `forIn` a 6.5x de C# con ~20 ns/elemento totales: estimación 3-8 %.
- **Medición.** `arrayIndex`, `forIn`, `arrayFill`, `sortArray` (recorre mucho).

#### P6. Pase peephole/liveness post-emisión

- **Descripción.** Tras el inlining y el splicing de temps quedan patrones eliminables que hoy
  nadie mira: saltos `JP` a la instrucción siguiente, `Stl` hacia locales muertos (típicos de
  temporales `$inlineResult` o `$assigned` cuya única lectura se pliegó), pares `Ldl; Pop`, y
  constantes booleanas seguidas de `JPZ/JPNZ` que no llegaron a fusionarse (condiciones
  compuestas). Un pase sobre el buffer del emitter antes de la relajación: (1) análisis de
  liveness de locals hacia atrás por bloque lineal con los saltos como fronteras, (2) borrado de
  stores muertos y reescritura de los patrones triviales. Los opcodes de llamada llevan longitud
  fija por codificación, lo que simplifica reempaquetar.
- **Ficheros.** Nuevo paso dentro de `SurtrCodeEmitter` (`Finish`, junto a la relajación) o clase
  nueva en `src/Surtr.Compiler/CodeGen/` invocada desde `SurtrModuleBuilder.Build`; el
  disassembler sirve de verificación.
- **Riesgo.** Medio: tocar código ya emitido exige cuidado con etiquetas y con handlers de
  excepción (las regiones protegidas cubren rangos). Mitigable restringiendo el pase a
  transformaciones locales sin mover etiquetas (borrado se sustituye por `Nop`).
- **Beneficio esperado.** Modesto en tiempo, visible en tamaño de imagen y en I-cache de métodos
  grandes con muchos inlines. Es también la infraestructura sobre la que aterrizar futuros
  plegados entre instrucciones (propagación constante/copias post-inline).
- **Medición.** Tamaño de imagen de la stdlib y del corpus grande; suite completa como regresión;
  `methodCalls`/`propertyAccess` (mucho inline) buscando mejora secundaria.

#### P7. Resolución plana de `CallModule` en carga (quickening estático del loader)

- **Descripción.** Una llamada cruzada de módulo hace dos cargas dependientes: `moduleTable[i]` y
  luego `target.Chunk.MethodTable[j]` (VM :3300-3320). Como un chunk pertenece a un único runtime
  (`docs/VM-Plan.md` §3.3), el loader podría, al vincular `PendingModulePaths`, reescribir el
  operando de cada `CallModule` a un índice de una tabla plana por runtime (o parchear directamente
  el `SurtrMethodInfo*`), dejando la llamada en una sola carga igual que `CallLocalModule`. Es
  quickening sin coste en ejecución: el trabajo se paga una vez en `LoadModule`.
- **Ficheros.** `Runtime/Classes/SurtrTypeLinker.cs` o el punto donde se resuelven referencias
  pendientes; `SurtrChunk.cs` (tabla plana opcional); VM :3300-3320 (nuevo camino u opcode
  `CallResolved`).
- **Riesgo.** Medio-alto: toca carga/vinculación y posiblemente la serialización; requiere
  decidir si el parche es en imagen (no: la imagen es compartible) o en memoria (sí). Beneficio
  por llamada pequeño (~1-3 ns), así que solo compensa en código stdlib-pesado.
- **Beneficio esperado.** Menor que P1-P5 por llamada, pero multiplicado por todas las llamadas
  entre módulos de un proyecto real.
- **Medición.** Caso nuevo con llamada cruzada de módulo en bucle cerrado (el equivalente
  inter-módulo de `methodCalls`); `sortArray` si el comparador cruza módulos.

### Nivel 3 — Mayor inversión (evaluar con prototipo medido)

#### P8. Reordenación/división caliente-fría del switch de despacho

- **Descripción.** El jump table tiene 240 entradas contiguas; el JIT genera una tabla y una
  comprobación de rango. Dividir el switch en uno "caliente" (~30 opcodes: cargas/stores locales,
  aritmética, comparaciones fundidas, JP/JPZ, llamada/retorno) y delegar el resto a un segundo
  switch reduce el tamaño de la tabla y el footprint de caché de la región del despacho. Es el
  mismo principio de ordenar casos por frecuencia que usan CPython y otros intérpretes de switch;
  aquí, con tabla densa, el efecto esperado es pequeño y puede ser nulo — por eso es un
  experimento con criterio de abandono explícito.
- **Ficheros.** `src/Surtr.Core/VM/SurtrVirtualMachine.cs` (reestructuración pura del switch;
  ningún cambio de opcode ni de formato).
- **Riesgo.** Bajo técnico (es reorganización), alto de expectativa: la ganancia puede no separarse
  del ruido, como pasó con la caché virtual.
- **Beneficio esperado.** 0-5 % en los microbenchmarks de despacho puro.
- **Medición.** `intLoop`, `fib`, `arrayIndex`; abandonar si el intervalo de las medianas se
  solapa.

#### P9. Batching del límite nativo en `sortArray` (y superficies nativas reentrantes)

- **Descripción.** `sortArray` (10.4x) paga dos fronteras VM/nativo por comparación porque el sort
  nativo invoca el comparador Surtr por elemento. Implementar el orden en Surtr mismo (una función
  de biblioteca compilada a bytecode) elimina las fronteras: el intérprete ya demostró que sus
  excepciones y su despacho ganan a las fronteras repetidas (`exceptions` 71x, `interop` 6.6x vs
  el reentrada de `sortArray` 10.4x). No es cambio de VM sino de dónde vive el algoritmo; se lista
  porque el perfil lo señala y porque el patrón (evitar N reentradas por operación) aplica a
  cualquier built-in que acepte callbacks.
- **Ficheros.** `src/Surtr.Stdlib/src/surtr/collections` (sort en .surtr) o
  `SurtrCompositeBuiltIns.cs` si se prefiere un híbrido (inserción para cortos, merge en bytecode).
- **Riesgo.** Bajo funcionalmente; hay que mantener estabilidad y semántica del comparador.
- **Beneficio esperado.** Potencialmente grande en `sortArray` (el coste está en las fronteras).
- **Medición.** `sortArray` con comparadores trivial y caro.

---

## 4. Técnicas evaluadas y descartadas (con razón explícita)

Del checklist habitual de intérpretes sin JIT, esto es lo que **no** conviene hacer aquí, además de
lo ya cerrado en §2.6:

1. **Threading computado (computed goto).** Imposible en C#/.NET y en IL2CPP: no hay saltos
   indirectos a etiquetas ni garantía de tail-call entre handlers. El switch único con jump table
   es el equivalente óptimo disponible y la decisión está documentada (`docs/VM-Plan.md` §1.1,
   comentario en VM :42-46).
2. **Quickening dinámico clásico** (reemplazar el opcode genérico por uno especializado tras la
   primera ejecución). En un lenguaje estáticamente tipado, casi todo lo que quickening descubre en
   ejecución el compilador ya lo sabe: por eso P2/P3/P5 son "quickening en tiempo de emisión". Los
   dos sitios donde el dinámico añadiría algo (caché de receptor virtual, especialización de
   llamada por receptor) están medidos: la caché virtual salió negativa (§2.6) y la de interfaz ya
   existe. Reescribir bytes del chunk en caliente añadiría además problemas de visibilidad entre
   runs reentrantes sin beneficio demostrado.
3. **Conversión completa a bytecode de registros (estilo Lua 5.x/Wren).** El diseño actual ya
   captura la mayor parte del beneficio: los frames son un bloque plano direccionable
   `frameBase[i]` (equivalente a un banco de registros), los temporales de expresiones grandes van
   a slots reales vía `DeclareTemp`, y `ReturnValues` mueve bloques sin pila intermedia. Una
   conversión completa exigiría renumerar el formato completo, reescribir el 100 % del emisor y
   del intérprete, para atacar un coste (tráfico de pila de expresión) que no aparece como cuello
   en ningún benchmark: los peores ratios (`vec2Math`, `generics`) se deben al protocolo de frame
   y al boxing, no al despacho de operandos.
4. **SIMD para arrays numéricos dentro de la VM.** netstandard2.1 no expone
   `System.Runtime.Intrinsics`; `Vector<T>` existe pero IL2CPP no garantiza mapear a SIMD y el
   buffer de `SurtrArray` es `SurtrRawValue*` NaN-boxed, no un array de primitivos contiguos:
   vectorizar exige primero desentrelazar tags, lo que se come la ganancia. Viable solo como
   built-ins nativos del host (donde el host controla el layout) y siempre midiendo en IL2CPP real
   antes de prometer nada.
5. **Especialización de CALL por aridad/tipos de argumento.** Ya parcialmente existente: los
   opcodes de llamada llevan conteos inline y `discardResult`; la entrada de frame es una sola
   secuencia compartida (`InvokeResolved`). La evidencia dice que el coste de la llamada está en el
   protocolo de frame (~6 ns fijados por el delta `virtualCalls − methodCalls`), no en el despacho
   de la llamada; más variantes de opcode no tocan ese techo.
6. **Branch hinting por orden de casos.** Con un jump table denso el orden de los `case` es
   irrelevante para predicción (la tabla indexa directamente); solo tendría sentido si el JIT
   compilara a cadena de comparaciones, que no es el caso para valores contiguos. Lo aprovechable
   de esta técnica está en P8 (tamaño/ubicación), no en ordenar casos.
7. **Pool de enteros pequeños / internado general de boxes.** Rechazado por identidad observable
   (§2.6); cualquier versión futura exigiría un cambio de lenguaje (por ejemplo, semántica de
   valor para `===` sobre boxes), fuera del alcance de optimización.

---

## 5. Recomendación priorizada

Orden propuesto, con el criterio de decisión de cada paso:

| Orden | Propuesta | Por qué va ahí | Criterio de éxito |
|---|---|---|---|
| 1 | **P1** Plegado constante | Días, riesgo mínimo, elimina un hueco real (el evaluador existe y no se usa en emisión); mejora base para todo lo demás. | Imágenes más pequeñas; microcaso nuevo sin regresiones. |
| 2 | **P2** Campos nativos especializados | Ataca la instrucción más frecuente después de cargas locales; `fieldAccess`/`enums` son de las filas más lentas frente a C#. | >=3 % en `fieldAccess` con spread <10 %. |
| 3 | **P3** Diccionarios int estáticos | Misma naturaleza que P2 sobre las mejores filas de la suite; poco código nuevo. | >=5 % en `dictOps`/`dictMembers`. |
| 4 | **P4** Paso de bucle contado | La superinstrucción de mayor cobertura (todo bucle contado la usa); `intLoop`/`floatLoop`/`forIn`. | >=8 % en `intLoop`. |
| 5 | **P5** Lectura indexada sin guard duplicado | Complementa P4 en el lowering de `for-in`; contrato acotado a un único sitio de emisión. | >=3 % en `arrayIndex`/`forIn`. |
| 6 | **P6** Peephole/liveness | Infraestructura para seguir reduciendo instrucciones tras inlining; beneficio modesto pero acumulativo. | Reducción de imagen sin regresión temporal. |
| 7 | **P7** `CallModule` plano en carga | Solo si el perfil de proyectos reales muestra llamadas cruzadas dominantes; prototipo antes de comprometer loader. | Mejora separable en caso inter-módulo nuevo. |
| 8 | **P9** Sort en bytecode | Barato de validar comparando contra `sortArray` actual. | >=20 % en `sortArray`. |
| 9 | **P8** Split caliente-frío del switch | Experimento final: costo bajo, expectativa baja; abandonar sin dolor si no separa distribuciones. | Separación de medianas en `fib`/`intLoop`; si no, cerrarlo documentado como la caché virtual. |

Notas finales para la ejecución:

- Cada propuesta de Nivel 1-2 debe entrar con su A/B en `src/Surtr.Bench` siguiendo la
  metodología de `benchmark_report.md` (Release, rondas barajadas, checksum, tamaños suficientes
  para mantener el spread bajo el 10 %) y con los tests dorados de opcode
  (`src/Surtr.Tests/Bytecode/OpCodeValueTests.cs`) actualizados al asignar valores `0xF0`+.
- Las cinco primeras propuestas consumen 11 de los 16 valores libres de opcode; si alguna propuesta
  futura necesitara más, es el momento de planificar la única renumeración restante con subida de
  `FormatVersion`.
- Actualizar `docs/VM-Plan.md` §3.2 (premisa desactualizada sobre el buffer gestionado de arrays)
  cuando se toque esta área.

