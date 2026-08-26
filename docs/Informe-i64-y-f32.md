# Informe: enteros largos (i64) y tipo f32 en Surtr

**Fecha:** 2026-08-25
**Alcance:** solo investigación y diseño. No se ha modificado código fuente.
**Método:** lectura directa de `src/Surtr.Core` (Runtime, VM, Bytecode), `src/Surtr.Compiler` (Binding, CodeGen, Syntax), `src/Surtr.Interop*`, los docs (`Opcodes.md`, `VM-Plan.md`, `Runtime-Model.md`, `Plan-TiposDeValor.md`, `Language-Syntax.md`, `Module-Format.md`) y los informes de análisis bajo `docs/analysis/`.

---

## Resumen ejecutivo

1. **i64:** el NaN-boxing actual reserva las etiquetas `0xFFF1`–`0xFFF6` en los 16 bits altos y deja **nueve nibbles libres** (`0xFFF7`–`0xFFFF`) con un camino de crecimiento ya documentado en el propio código. No hay sitio para un `long` crudo de 64 bits: el payload útil de los valores etiquetados es de 32 bits. Se analizan cuatro propuestas; la recomendada es una **tabla lateral de i64 con handles de 32 bits y tag propio `0xFFF7`**, que mantiene el valor fuera del entity registry (invisible al GC sin tocar nada), cuesta ~2 cargas extra por operación frente a `int`, y corrige de paso un defecto real: hoy el interop C# **trunca silenciosamente** todo `long`.
2. **f32:** hoy `float` es `double` de punta a punta (`SurtrFloat = System.Double`, literales sin sufijo, opcodes `F*` sobre `double`, stdlib matemática completa). Añadir f32 **no sale prácticamente gratis**: el coste no está en "añadir opcodes" sino en tres puntos concretos — (a) solo quedan **16 valores de opcode libres** (`0xF0`–`0xFF`) y la familia f32 completa necesita más de 20; (b) si el f32 se almacena como `double` normalizado, el runtime **no puede distinguirlo** de un f64 en tiempo de ejecución (rompe `ForValue`, `DynEQ`, `BoxDynamic`, concatenación); y (c) las reglas de conversión/sobrecarga pasan de dos familias numéricas a tres. Es factible con coste medio si se acepta tag propio + conversión por operación, y los beneficios de memoria/SIMD exigen además almacenamiento empaquetado (arrays stride 4), que es un proyecto aparte.

---

# Parte A — Enteros largos (i64) sobre NaN-tagging

## A.1 Cómo funciona hoy el NaN-boxing

### La representación

`SurtrValue` es un struct explícito de 8 bytes que solapa `Raw` (ulong) y `AsFloat` (double):

- Declaración y unión: `src/Surtr.Core/Runtime/Objects/SurtrValue.cs:15-16` y `:71-74`. Alias de tipos en `src/Surtr.Core/GlobalUsings.cs:1-5`: `SurtrRawValue = System.UInt64`, `SurtrRawTagValue = System.UInt16`, `SurtrRawPayloadValue = System.UInt32`, `SurtrInt = System.Int32`, `SurtrFloat = System.Double`, `SurtrRef = System.Int32`.

Los 16 bits altos llevan la etiqueta; los 48 bajos son teóricamente payload:

| Constante | Valor | Sitio |
|---|---|---|
| `TagMask` | `0xFFFF000000000000` | `SurtrValue.cs:18` |
| `PayloadMask` | `0x0000FFFFFFFFFFFF` | `SurtrValue.cs:19` |
| `TagMaskInt` | `0xFFF1000000000000` | `SurtrValue.cs:21` |
| `TagMaskBool` | `0xFFF3000000000000` | `SurtrValue.cs:23` |
| `TagMaskChar` | `0xFFF4000000000000` | `SurtrValue.cs:24` |
| `TagMaskReference` | `0xFFF5000000000000` | `SurtrValue.cs:25` |
| `TagMaskAbsent` | `0xFFF6000000000000` | `SurtrValue.cs:26` |
| `TagAbsent` | `0xFFF6` | `SurtrValue.cs:56` |

Puntos clave:

1. **El float no lleva tag.** Un float es su patrón IEEE 754 crudo; `IsFloat` se resuelve **por descarte**: `tag < TagMaskInt || tag > TagMaskAbsent` en una sola comparación de rango (`SurtrValue.cs:164-172`). El comentario de `:157-163` documenta la decisión deliberada: el tag `Absent` se colocó un nibble por encima del de referencia *precisamente* para que el rango pueda crecer manteniendo la comparación única.
2. **El payload real usado es de 32 bits.** Aunque `PayloadMask` cubre 48 bits, todos los constructores etiquetados casteán a `SurtrRawPayloadValue` (uint): `CreateInt(SurtrInt)` hace `(SurtrRawPayloadValue)value` (`SurtrValue.cs:231`) y `AsInt` lee `(SurtrInt)Raw`, truncando a los 32 bajos (`:113-117`). Las referencias son su payload de 32 bits (`SurtrRef = Int32`, `NullRef = 0` en `:59`). **Un long de 64 bits colisiona tanto con el patrón de tag como con el espacio de payload: no cabe.**
3. **Hueco libre:** tags `0xFFF7`–`0xFFFF`, nueve valores. Ya está inventariado en `docs/Plan-TiposDeValor.md:97-100` ("quedan 9 nibbles libres desde 0xFFF7"). Añadir un tag nuevo **por encima** de `Absent` conserva la comparación de rango de `IsFloat` cambiando una constante (el límite superior pasa de `TagMaskAbsent` al nuevo tope).
4. El pool de constantes del bytecode son u64 crudos NaN-boxed (`docs/Module-Format.md:154`; lectura en `SurtrModuleImageReader.cs:179`).

### Dónde se etiqueta y des-etiqueta (hot path)

En la VM (`src/Surtr.Core/VM/SurtrVirtualMachine.cs`, dispatch único con `goto Dispatch`):

- **Enteros:** la aritmética re-etiqueta en cada operación escribiendo `TagMaskInt | uint32(resultado)`: `Add :1325-1330`, `Sub :1339`, `Mul :1353`, `Div :1367-1383` (con trap por división por cero), `Mod :1392`, `Neg :1415-1417`, y `IncLocal :1315-1321` (un `i += 1` completo sin pasar por la pila). Comparaciones: `EQ :1431`, `NE :1466`, etc. Todo con wraparound unchecked de 32 bits.
- **Flotantes:** sin retag alguna — operan reinterpretando el slot como `*(double*)sp`: `FAdd :1332`, `FSub :1346`, `FMul :1360`, `FDiv :1385`, `FMod :1408`, `FNeg :1419-1423` (XOR del bit de signo). Este es el punto fuerte del diseño: el float es la representación "gratis".
- **Conversiones:** `I2F :1755-1760` (int→double in situ), `F2I :1762-1777` (saturante, determinista x64/ARM, NaN→0).
- **Boxing:** `BoxInt/BoxFloat/BoxBool/BoxChar :1797-1835` crean un `SurtrBoxed`, lo registran en el entity registry y saltan a safepoint; `Unbox :1837-1839` lee `.Value.Raw`. `BoxDynamic/UnboxDynamic :1841+` hacen lo mismo leyendo el tag en runtime para slots borrados.

Fuera de la VM, la etiqueta se consulta en: `SurtrBuiltIns.ForValue` (`SurtrBuiltIns.cs:628-636`, tag→clase), `MarkIfReference` del GC, `SurtrEntityMarker.Mark(SurtrValue)` (`SurtrEntityRegistry.cs:577-581`), el comparer de valores y los caminos dinámicos (`StrCat`, `BoxDynamic`).

### Igualdad y hashing

`SurtrValueComparer.ValuesEqual` (`src/Surtr.Core/Runtime/Objects/SurtrValueComparer.cs:48-73`): primero filtra el caso float (NaN/+0/-0); después **igualdad de bits crudos** (`left.Raw == right.Raw`, `:56`) para todo lo demás, con resolución especial box-vs-primitivo (`BoxEquals :76-87`) que exige además misma clase. `HashOf` (`:90-101`): `Raw.GetHashCode()` para cualquier primitivo etiquetado. Consecuencia importante para i64: **un nuevo primitivo etiquetado participa gratis en igualdad/hash por bits** — siempre que el valor completo viva dentro del SurtrValue. Si lo que viaja en el slot es un handle o índice, esto se rompe (ver A.4/P2).

### GC / entity registry

- El collector recorre la pila slot a slot con `MarkIfReference`, que compara contra el tag exacto de referencia (`SurtrEntityRegistry.cs:433-437`); los statics marcan desde una lista de slots-referencia construida a partir de tipos declarados (`:355-360`), sin tag-test.
- Cualquier valor cuyo tag no sea exactamente `TagMaskReference` es **invisible al collector por construcción** — así funciona `Absent` sin costo ("Tracing needs no change at all", `SurtrValue.cs:52-54`).
- `Register` (`SurtrEntityRegistry.cs:150-208`) es el hot path de asignación: free-list + watermark + contador de presión plegado a una comparación en modo Manual. Los docs de rendimiento miden el coste por asignación (~3 escrituras + recarga del local `entities`) en `docs/analysis/Runtime-Analisis-Rendimiento-Memoria.md` §1.2.5/§D.

## A.2 Estado actual de los enteros de 64 bits en la cadena

Hoy **no existe ningún camino i64**, y hay tres pruebas de ello en la cadena:

1. **Lexer:** acepta literales hasta rango `long` con overflow checked (`Lexer.cs:383-400`, diagnóstico `NumericLiteralOutOfRange` más allá de long); el payload del token es `TokenPayload.ForInteger(long)` (`TokenPayload.cs:40`). El plegado de constantes opera en `long` (`ConstantEvaluator.cs:170,276-305`).
2. **Emisor:** `EmitLiteral` rechaza con excepción cualquier literal entero que no quepa en `int` (`MethodBodyEmitter.cs:1853-1862`: "A literal wider than the machine's int would silently truncate... throw Unsupported"). Es decir, `let x = 3000000000;` no compila hoy.
3. **Interop C#:** el source generator mapea `long`/`ulong` al descriptor de `int` `"I"` (`GeneratorSupport.cs:103-105`) y el marshaling **trunca**: parámetro `long` → `args.GetInt(index)` y retorno → `SurtrValue.CreateInt((int)(expr))` (`SurtrSourceGenerator.cs:949-950` y `:1014-1016`). El fallback por reflexión hace lo mismo vía `Convert.ToInt32` (`SurtrMarshaler.cs:31-32`). Esto es un defecto latente real que un i64 corregiría.

El lenguaje no tiene keywords `long`/`byte`/`short` (la lista de tipos primitivos está cerrada en `TokenType.cs:20`), y `SurtrValueTypeCode` tampoco tiene hueco semántico reservado (aunque añadir un código nuevo es trivial, ver A.5).

## A.3 Propuesta 1 — Heap-boxing total de i64 (todo `long` es una entidad)

**Mecanismo.** Cada `long` vive exclusivamente en el heap gestionado por el entity registry: un `SurtrBoxed` (o entidad equivalente) cuya clase es la nueva `Long`. Variables, operandos de pila, campos y elementos de array llevan la **referencia** bajo `TagReference`. Nada cambia en `SurtrValue`.

**Cambios necesarios.**

| Capa | Trabajo |
|---|---|
| VM | Opcodes `LAdd/LSub/LMul/LDiv/LMod/LNeg/LEQ/...` que desreferencian dos entidades, computan en C#, allocan un box resultado y lo registran (con safepoint). Reutiliza el patrón de `BoxInt` (`SurtrVirtualMachine.cs:1797-1805`). |
| CodeGen/Binder | Tipo `long`, `SpecialType.Long`, conversiones `int→long` implícita y `long→float` implícita; emisión de ops long como llamadas/opcodes boxed. |
| GC | Cero cambios: los boxes ya se registran y rastrean (`VisitReferences` de `SurtrBoxed`). |
| Equality/hash | Cero cambios conceptuales: `BoxEquals` ya iguala box vs primitivo por clase+contenido (`SurtrValueComparer.cs:76-87`). |
| Interop | Trivial: `GetLong(i)` = resolver ref → leer box. Corrige el truncamiento actual. |

**Coste en hot path.** El peor de las cuatro opciones con diferencia: cada `a + b` paga dos derefs de registry + una asignación (`new SurtrBoxed` + `Register` + safepoint). Los benches existentes miden ese patrón: `tuples`/`allocation` son de los workloads más caros, y el informe de rendimiento trata la asignación como algo a minimizar, no a multiplicar (`docs/analysis/Runtime-Analisis-Rendimiento-Memoria.md` §1.2.1, §D). Bucles de contadores de 64 bits serían órdenes de magnitud más lentos que el `intLoop` actual.

**Pros/contras.**

- Pros: implementación más corta; representación intacta; GC/equality gratis; utilizable como paso intermedio para desbloquear el interop.
- Contras: contradice la premisa central del modelo ("un primitivo nunca toca el heap", `SurtrValue.cs:40-44`, `Runtime-Model.md:568-571`); presión de GC enorme; pierde el beneficio principal de tener i64 (aritmética rápida sin asignar). Como destino final no se sostiene; como puente temporal, sí.

## A.4 Propuesta 2 — Tabla lateral de i64 con handles de 32 bits y tag propio (recomendada)

**Mecanismo.**

- Nuevo tag `TagLong = 0xFFF7` (corrido: `0xFFF7000000000000`), colocado encima de `Absent` siguiendo el camino de crecimiento documentado (`SurtrValue.cs:157-163`). Único cambio en `IsFloat`: el límite superior del descarte pasa de `TagMaskAbsent` a `TagMaskLong` — se conserva la comparación de rango única.
- El payload de 32 bits es un **handle** (índice) a una tabla lateral unmanaged `long[]` propiedad del contexto/runtime, con crecimiento doble estilo `ExpandCapacity` (`SurtrEntityRegistry.cs:494-522`) y contador de agua (`_nextHandle`). Sin free-list en v1 (ver liberación abajo).
- `CreateLong(long)` = escribir en la tabla + devolver `TagMaskLong | (uint)handle`. `AsLong` = `_table[(int)(Raw & PayloadMask)]` (una carga indirecta).

**Hot path de la aritmética.** `LAdd`: pop de dos handles → dos cargas de la tabla → suma en registro de 64 bits → escritura en un slot nuevo de la tabla → push del handle resultado. Frente a `Add` de 32 bits: ~2 cargas extra y 1 store más la gestión del índice; sin asignación CLR, sin safepoint, sin presión de GC. Estimación honesta: 1,5–2,5× el coste de la aritmética int actual (que ya es casi gratuita), muy lejos del coste de P1. Bitwise/shifts (`LAnd/LOr/LXor/LShl/LSar/LShr`) idéntico en forma.

**Interacción con GC/entity registry.** Prácticamente nula por construcción:

- El tag no es `TagMaskReference`, así que `MarkIfReference`, `SurtrEntityMarker` y los walks de statics ignoran los handles **hoy mismo**, sin tocar el collector (el mismo argumento de `Absent`, `SurtrValue.cs:52-54`).
- Los handles no consumen ids del entity registry ni alimentan el contador de asignaciones del GC.
- Liberación de handles: un `long` es un valor sin identidad, así que "cuándo muere un handle" es indecidible sin un traceado. Solución pragmática: cuando la tabla cruza un umbral (armado junto a `GcPending`, `SurtrEntityRegistry.cs:204-205`), en el próximo safepoint se hace un pase barato: walk de pila + explicit roots + statics testando `TagLong`, marcar bitset de handles vivos, sweepear el resto a una free-list. Es estructuralmente el mismo bucle de `CollectGarbage` (`:336-430`) pero sobre la tabla lateral y sin grafo de objetos; puede ejecutarse acoplado a la colección de entidades ya programada. En Manual mode queda bajo control del host, igual que el resto del GC.

**Igualdad/hashing — el punto que hay que tocar sí o sí.** `ValuesEqual` compara `Raw == Raw` para no-floats (`SurtrValueComparer.cs:56`); con handles, dos copias del mismo `long` tendrían handles distintos y compararían falsos. Cambios localizados:

- `ValuesEqual`: si ambos llevan `TagLong`, cargar los dos `long` de la tabla y compararlos (rama nueva antes del `Raw == Raw`).
- `HashOf`: hash del `long` contenido, no del handle (`:98`).
- Diccionarios `{long: V}`: replicar la especialización que `{int: V}` ya tiene (`TryUnwrapBoxedInt`, `SurtrValueComparer.cs:114-127`; storage especializado en `SurtrDictionary.cs:79`) con claves long crudas.
- Mejor aún para hot paths tipados: opcodes `LEQ/LNE/LGT/...` que comparan longs directamente, dejando la vía comparer solo para `DynEQ`/diccionarios (mismo reparto que hoy entre `EQ` y `DynEQ`).

**Binder/CodeGen.**

- `SpecialType.Long` + `TypeSymbolFactory.Long` (patrón de `TypeSymbolFactory.cs`, `SpecialType` en `TypeSymbol.cs:56-98`) + clase built-in `Long` en `SurtrBuiltIns` (patrón de `DeclareFloat`, `SurtrPrimitiveBuiltIns.cs:171-256`) + código `SurtrValueTypeCode.Long` (el enum tiene huecos libres tras `Void=15`; revisar los range-compares de `IsPrimitive`/`IsValueType`/`IsBuiltIn` en `SurtrValueTypeCode.cs:122-162`, que hoy asumen primitivos = `Integer..Character` contiguos — habría que extender el rango o colocar Long justo tras Character).
- Regla de conversión: ampliar la única regla implícita actual (`int→float`, `Conversions.cs:517-520`) a `int→long` y `long→float`, ambas `ImplicitNumeric`; explícitas entre primitivos ya existen en bloque (`:545-549`). Nuevos opcodes de conversión: `I2L`, `L2I` (saturante o truncante — decidir; `F2I` satura), `L2F`, `F2L` (saturante, espejo de `F2I`).
- Literales: el lexer ya produce `long` (`Lexer.cs:383`); basta eliminar el rechazo de `EmitLiteral` (`MethodBodyEmitter.cs:1853-1862`) y materializarlos con un opcode `PushI64 imm(8)` inline de 9 bytes — **no** por el pool: el pool son u64 interpretados como `SurtrValue`, y un long crudo podría aterrizar en un patrón tag/float (colisión NaN). Alternativa: pool paralelo de longs con fixup a handle en carga de imagen.
- Sobrecargas: `OverloadResolution` ya vive del caso `int` vs `float` ("§5.6 makes overload resolution non-trivial", `Conversions.cs:82-84`); una tercera familia numérica encarece la clasificación pero no cambia su estructura (ver también B.3).

**Presupuesto de opcodes (restricción transversal, ver §C).** Quedan 16 bytes libres (`0xF0`–`0xFF`, `OpCode.cs:50`, verificado: 240 valores asignados, máximo `GenResumed = 0xEF`). Una familia long mínima (6 aritméticas + neg + 6 comparaciones + 2 branch fused + 4 conversiones + box ≈ 19) **no cabe** con encoding plano. Soluciones: opcode compuesto con sub-immediato (p. ej. `LongOp sub(1)`), o escape `0xFF` + segundo byte de opcode extendido (bump de `FormatVersion`, aditivo en espíritu según `OpCode.cs:39-56`), o recorte de familia (comparaciones long vía `LDyn`-style + comparer). Esta restricción afecta por igual a f32 (B.2) y conviene resolverla una vez para las dos.

**Interop.** `GetLong(index)`/`CreateLong(long)` en `SurtrCallArguments` (patrón de `GetInt/GetFloat`, `SurtrCallArguments.cs:156-162`); el generator mapea `System_Int64` a descriptor `"L"` y deja de truncar (`SurtrSourceGenerator.cs:949-950`, `GeneratorSupport.cs:103-105`). Casos de uso directos en hosts Unity: ids de entidad, ticks de cronómetros, tamaños de fichero, hashes de 64 bits.

**Pros/contras.**

- Pros: primitivo real sin heap pressure; invisible al GC de entidades sin tocarlo; hueco de tag y camino de crecimiento ya diseñados en el código; corrige el truncamiento de interop; equality/hash con cambios acotados y testeables.
- Contras: estado nuevo por runtime (tabla + ciclo de liberación propio); comparer/diccionarios requieren cambios reales (no gratuitos); presupuesto de opcodes obliga a decidir política de encoding; serialización de constantes long necesita formato.

## A.5 Propuesta 3 — smi-53 (enteros hasta 2^53 como double) con fallback boxeado

**Mecanismo.** Estilo JS: un `long` cuyo valor absoluto cabe en la mantisa de 53 bits se representa como double **sin tag** (la representación float actual); fuera de rango cae a un box/tabla lateral. El tipado estático decide qué opcode aplica: `LAdd` sobre smis compila a `FAdd` hardware.

**Análisis contra el código real — los problemas pesan más que la elegancia:**

1. **Aliasing NaN real, no teórico.** Hoy un double cuyo patrón caiga en `0xFFF1…`–`0xFFF6…` solo puede surgir de un bitcast manual y el sistema lo trata como "no-float reservado" (el riesgo ya está señalado dos veces: `Runtime-Model.md:215` y `VM-Plan.md:969-975`). Con smis, *ese mismo patrón* sería además un valor long legítimo: la distinción float/smi desaparecería a nivel de bits. JS/V8 lo evitan con smi-tagging (bit robado), no con mantisa pura; robar un bit aquí significa cambiar la representación de `int` o de `float` — invasivo.
2. **La aritmética entera no baja limpia a doubles.** Suma/multiplicación hasta 2^53 sí; **división truncante, módulo, shifts y bitwise exigen round-trips** long↔double por operación (o emulación cara), con ramas de overflow hacia el fallback. Los opcodes bitwise actuales (`Shl/Shr/Sar`, `SurtrVirtualMachine.cs:1700-1751`) son de las instrucciones más baratas del set; bajo smi-53 dejarían de serlo precisamente para el tipo que más los usa.
3. **Equality mixta** gana algo (`5L == 5.0` sería una comparación double directa) pero es la única ganancia clara de hot path.
4. **Fallback:** cada operación necesita comprobar rango y degradar a P1/P2 fuera de ±2^53 — dos representaciones para un mismo tipo en toda la cadena (comparer, hashing, StrCat, interop, imagen).

**Veredicto:** solo compensaría en un perfil donde los longs sean casi siempre ≤2^53 y casi nunca bitwise. En un lenguaje estático con VM interpretada, P2 da mejor hot path con menos casos especiales. Descartada como propuesta principal; documentada porque es el patrón JS/LuaJIT que suele proponerse primero.

## A.6 Propuesta 4 — Dos slots inline (estilo multi-slot value)

**Mecanismo.** Un `long` = bloque de 2 slots (hi/lo) aprovechando la maquinaria multi-slot ya construida: `LoadValueLocal/StoreValueLocal`, layout aplanado de `value class` (`ValueTypeLayout.IsInlineType`, `ValueTypeLayout.cs:57-102`), retorno multi-slot (`ReturnValues = 0xE7`).

**Por qué no.** Toda la familia de operand stack asume 1 slot por operando: `Add` lee `sp[-1]`/`sp[-2]` (`SurtrVirtualMachine.cs:1325-1330`), `Dup/Dup2/Swap` cubren 2 slots, los jumps fusionados leen 1+1. Habría que duplicar aritmética/comparación/branch en formas de 2×2 slots, y sobre todo **romper el stride-1 de arrays y diccionarios** — la decisión nº 12 de `Plan-TiposDeValor.md:86` evitó exactamente eso ("romper el stride es el cambio más caro del espacio de diseño"). Es la opción más invasiva para el VM y no aporta nada sobre P2 salvo evitar la tabla lateral. Descartada.

## A.7 Comparativa

| Criterio | P1 Box total | P2 Side table (tag 0xFFF7) | P3 smi-53 | P4 Dos slots |
|---|---|---|---|---|
| Hot path aritmética | Muy malo (alloc+safepoint por op) | Bueno (~1,5-2,5× int) | Regular (bitwise caro, ramas de rango) | Bueno si se paga la reescritura |
| Presión GC | Extrema | Ninguna (liberación propia barata) | Media (fallback) | Ninguna |
| Cambios en `SurtrValue` | Ninguno | 1 tag + `IsFloat` (1 constante) | Ninguno (¡y ahí el problema!) | Ninguno |
| Cambios en comparer/hash | Ninguno | Localizados y claros | Complejos (dos repr.) | Grandes (stride) |
| GC de entidades | Gratis | Invisible por tag (gratis) | Mixto | Gratis |
| Interop C# | Fácil | Fácil, corrige truncamiento | Difícil (¿qué devuelve?) | Difícil |
| Riesgo de aliasing NaN | Ninguno | Ninguno | Real y documentado | Ninguno |
| Invasividad del VM | Media | Media-baja (familia nueva de opcodes) | Alta (ramas por doquier) | Muy alta (stride, jumps, frames) |
| Veredicto | Puente temporal, no destino | **Recomendada** | Descartada | Descartada |

---

# Parte B — El tipo f32

## B.1 Estado actual: `float` es `double` de punta a punta

- **Alias de tipos:** `global using SurtrFloat = System.Double;` (`GlobalUsings.cs:5`). No existe representación de 32 bits en ninguna capa.
- **Tipo en el binder:** `SurtrValueTypeCode.Float = 2` (`SurtrValueTypeCode.cs:19`), `SpecialType.Float` (`TypeSymbol.cs:65`), creado por `TypeSymbolFactory` y declarado como built-in `float` en `SurtrBuiltIns.Float` (`SurtrBuiltIns.cs:304`, tabla `ByTypeCode` `:344`).
- **Literales:** un literal es float si y solo si tiene punto o exponente, "**never a suffix**" (§5.8, `Language-Syntax.md:2199-2222`; token en `TokenType.cs:62-63`; escaneo en `Lexer.cs:308-357`); se parsea con `double.TryParse` (`Lexer.cs:450-464`) y viaja como bits de double (`TokenPayload.ForFloat`, `TokenPayload.cs:42-43`). El binder lo tipa `_factory.Float` (`BodyBinder.Expressions.cs:74-76`).
- **Constantes:** `LoadFloat(double)` va siempre al pool como u64 crudo (`SurtrCodeEmitter.Helpers.cs:128`; pool en `Module-Format.md:154`). No existe `PushF` inline.
- **Opcodes aritméticos: especializados por familia, todos f64.** Existe `Add` (int) y `FAdd` (float) como instrucciones distintas — la respuesta a "¿genéricos o especializados?" es **especializados**: `FAdd=0x3D`, `FSub=0x3F`, `FMul`, `FDiv=0x43`, `FMod`, `FNeg`; comparación `FEQ/FNE/FGT/FGE/FLT/FLE` (`0x56`–`0x5B`); branch fusionados `JPFEQ…JPFLE` (`0xBF`–`0xC9`); conversiones `I2F=0x66`, `F2I=0x67`; boxing `BoxFloat=0x6D` (`docs/Opcodes.md:328-401,575-585`; enum en `OpCode.cs`). La VM los ejecuta con `double` directo (`SurtrVirtualMachine.cs:1332-1423,1755-1777`).
- **Selección de opcode en CodeGen:** `MethodBodyEmitter.Binary` toma la familia de `TypeCodeOf(binary.Left.Type)` y despacha (`MethodBodyEmitter.cs:2354,2381-2407`); el helper `Add(SurtrValueTypeCode)` mapea `Float → FAdd` e integrales a las formas sin tag (`SurtrCodeEmitter.Helpers.cs:301-336`). Las comparaciones van por `ComparisonOpCode(..., operandType)` (`:358+`). Incrementos: `family == Float ? LoadFloat(1.0)...` (`MethodBodyEmitter.cs:3130-3138`).
- **Conversiones:** exactamente **una** conversión numérica implícita, `int → float` (`Conversions.cs:517-520`, kind `ImplicitNumeric` definido en `:20-21`); entre primitivos cualesquiera existe la explícita (`ClassifyExplicit`, `:545-549`). Emisión: `EmitConversionTail` → `Code.Convert(TypeCodeOf(from), TypeCodeOf(to))` (`MethodBodyEmitter.cs:1964-1989`), que hoy solo sabe emitir `I2F`/`F2I`/retags (`SurtrCodeEmitter.Helpers.cs:491-511`).
- **Stdlib:** toda la matemática declara `float` (= f64): `Math.surtr` (sin/cos/tan/atan2/sqrt/pow/exp/log/hypot/constantes Pi…) y los built-ins de la clase float con `toString("R")`, `floor/ceil/round→int`, `isNaN/isInfinite`, `parse/parseStrict` (`SurtrPrimitiveBuiltIns.cs:171-256`). `Angle` es un `value class` sobre `float` (`src/Surtr.Stdlib/src/surtr/math/Angle.surtr`).
- **Interop:** `Single`, `Double` y `Decimal` de C# colapsan al descriptor `"F"` (`GeneratorSupport.cs:107-110`); marshaling tipado `(float)args.GetFloat(i)` / `SurtrValue.CreateFloat((double)(expr))` (`SurtrSourceGenerator.cs:951-953,1018-1021`); reflexión vía `Convert.ToDouble` (`SurtrMarshaler.cs:34-35`). Para hosts Unity (donde `Vector3`, `Transform`, etc. son `float`), **cada cruce de frontera paga `double↔float`**, aunque es un cast por llamada, no por elemento.

## B.2 Qué haría falta para f32 — plan de fases

### Decisión previa obligatoria: la representación

Este es el diseño que condiciona todo lo demás. Opciones:

| Opción | Descripción | Consecuencia |
|---|---|---|
| (a) f32 crudo + tag propio | `TagFloat32 = 0xFFF7` con el float32 empaquetado en los 32 bits bajos | Auto-descriptible (reflexión OK), pero **toda** op requiere unpack/cvt/compute/round/pack |
| (b) f32 como f64 normalizado | El valor se guarda widening a double; el tipo estático dice f32; cada op redondea a precisión simple | Slots uniformes, ops baratas (una `cvtsd2ss` extra), pero **indistinguible de f64 en runtime** |
| (c) f32 crudo en slots de 4 bytes | Rompe el slot universal de 8 bytes y el stride-1 | Invasivo, mismo problema de stride que descartó `Plan-TiposDeValor.md:86` |

**(b) puro no es viable como única representación** porque el runtime consulta la clase de un valor sin metadata estática en varios sitios: `SurtrBuiltIns.ForValue` por tag (`SurtrBuiltIns.cs:628-636`), `BoxDynamic`, `DynEQ`, concatenación de strings, impresión de diagnósticos. Un f32-normalizado respondería "soy Float (f64)" y `typeof(x)`/`is` mentirían. Por eso la recomendación es un **híbrido (a)+(b): tag propio `TagFloat32 = 0xFFF7` y payload float32**, donde cada op f32 hace cvt a double, opera en doble (aprovechando el hardware x64/ARM que así lo hace internamente), redondea a single y re-etiqueta. Coste honesto: ~3 instrucciones extra por operación respecto a f64.

### Fases

**Fase 0 — Política de encoding de opcodes (bloqueante compartido con i64).**
Quedan exactamente 16 valores libres (`0xF0`–`0xFF`; `OpCode.cs:48-56` lo afirma y el conteo del enum lo confirma: 240 asignados, último `GenResumed = 0xEF`). La familia f32 completa (6 aritméticas + 6 comparaciones + 6 branches fusionados + 4 conversiones + neg + box ≈ 24) **no cabe** con encoding plano, y f32+i64 juntas menos todavía. Elegir una:
1. **Opcode compuesto:** un valor nuevo `Float32Op sub(1)` que despacha por sub-immediato. Barato en espacio, +1 byte y +1 dispatch indirecto por instrucción.
2. **Escape extendido:** `0xFF` = "opcode extendido", seguido de otro byte (255 opcodes nuevos). Requiere bump de `FormatVersion` y actualizar `OpCodeValueTests`; los handlers nuevos viven en una segunda tabla.
3. **Recortar familia:** comparaciones f32 vía `DynEQ`-style o vía convertir ambos operandos (el binder inserta `F32_2_F64` y reusa `FEQ`): solo aritmética/conversiones nuevas (~10 opcodes). Semánticamente correcto (comparar en double tras widen es exacto) pero paga cvt en comparaciones calientes de bucles.

Recomendación: opción 2 (escape) como solución definitiva compartida, opción 3 como v1 si se quiere minimizar el cambio de formato.

**Fase 1 — Binder y tipos.** `SurtrValueTypeCode.Float32`, `SpecialType.Float32`, `TypeSymbolFactory.Float32`, clase built-in (nombre propuesto: `float32`; la palabra `float` queda como alias de `float64` por compatibilidad). Decidir la gramática de literales: hoy el sufijo está explícitamente prohibido por spec (§5.8) — añadir `1.5f` requiere lexer (`Lexer.cs:308-357`), grammar TextMate, LSP y documento de lenguaje. Alternativa sin sufijo: inferencia por anotación (`let x: float32 = 1.5;` ya funciona con target-typing §5.9) y funciones `float32(...)`.

**Fase 2 — Conversiones.** Reglas propuestas (estilo C#): `int→float32` implícito; `float32→float(64)` implícito (exacto); `float→float32` **explícito** (pérdida); `float32↔int` explícito. Impacto directo en `Conversions.ClassifyImplicit` (`:517-520`) y en `OverloadResolution`: la mezcla de familias numéricas pasa de binaria a ternaria — es el caso duro reconocido por la propia spec (`Conversions.cs:80-84`). Diagnóstico nuevo recomendado para ambigüedad `f(f32, f64)` con overloads `(f32,f32)` y `(f64,f64)`.

**Fase 3 — Bytecode y VM.** Opcodes `F32` (según fase 0): `FAdd32/FSub32/FMul32/FDiv32/FMod32/FNeg32`, `FEQ32..FLE32` (+branches si no se recorta), `I2F32/F2I32` (saturante, espejo de `F2I`), `F2F32`/`F32_2_F`, `BoxFloat32`, y materialización de literales (pool f64 + cvt en `PushF32`, o pool propio). Handlers VM: patrón idéntico a los `F*` actuales (`SurtrVirtualMachine.cs:1332-1423`) con `MathF`/cvt y re-tag. Actualizar disassembler (`SurtrBytecodeDisassembler.cs:1268` ya documenta el supuesto NaN-boxed), `Opcodes.md` y `OpCodeValueTests`.

**Fase 4 — Runtime y reflexión.** Clase built-in `Float32` con miembros espejo de `DeclareFloat` (`SurtrPrimitiveBuiltIns.cs:171-256`) pero en precisión simple (`sqrt`/`pow`/`abs`...). `ForValue` aprende el tag nuevo. `SurtrValueComparer`: el caso float (`:53-54`) debe tratar `IsFloat32` como familia aparte (igualdad de bits del float32 + NaN semantics). Stdlib `math`: decidir si se duplican las constantes (`Pi32`) o se dejan en f64 con conversión explícita (recomendado).

**Fase 5 — Interop y CodeGen.** Descriptor nuevo `"f"` (o `"F32"`); `MapType` separa `System_Single` de `System_Double` (`GeneratorSupport.cs:107-110`); `ToClrExpression`/`ToSurtrExpression` dejan de castear (`SurtrSourceGenerator.cs:951,1021`); `SurtrMarshaler.ToSurtr` distingue por descriptor (`SurtrMarshaler.cs:34-35`). En CodeGen basta que `TypeCodeOf` (`MethodBodyEmitter.cs:6050`) devuelva `Float32` y que los helpers despachen la familia nueva.

**Fase 6 (opcional, el beneficio grande) — Arrays/storage empaquetado.** Aquí vive el ahorro real de memoria: un `SurtrArray` especializado con buffer `float*` (stride 4) para `float32[]`, reutilizando el patrón unmanaged + `ISurtrNativeBufferOwner` + pool de `SurtrArray.cs:26-66`. Sin esto, `float32[]` sigue gastando 8 bytes por elemento y el beneficio de memoria es **cero**. Con esto: 2× densidad de caché en buffers grandes y base para SIMD futuro (aún así, SIMD real requeriría opcodes vectoriales nuevos — fuera de alcance).

## B.3 Evaluación honesta: ¿"sale prácticamente gratis"?

**No.** Es *relativamente* barato comparado con i64 (no toca el GC, no altera en profundidad la representación de valores), pero tiene tres costes duros:

1. **Espacio de opcodes agotado** — obliga a una decisión de formato (fase 0) que también necesita i64. Este es el coste oculto principal y no aparece si solo se mira el binder.
2. **Auto-descripción en runtime** — la representación ingenuamente "gratis" (guardarlo como double) rompe la reflexión de valores; la correcta (tag + payload) mete conversiones en cada operación. No hay representación que dé simultáneamente ops sin cvt y `typeof` correcto.
3. **Explosión combinatoria de conversiones/sobrecargas** — de 2 a 3 familias numéricas: más casos en `Conversions`, `OverloadResolution`, `ConstFolder` (ya distingue `SurtrValueTypeCode.Float` en `ConstFolder.cs:392-424`), stdlib duplicada, y trampas de precisión para el usuario (comparar `0.1f + 0.2f` contra literales double, acumulación en bucles largos, igualdad exacta).

Lo que **sí** es cierto: el mecanismo de selección por familia ya existe (`TypeCodeOf` + helpers despachan por `SurtrValueTypeCode`, no hay nada hardcodeado a dos familias más allá de los propios helpers), el patrón de "familia nueva" está maduro (range/value types lo acabaron de ejercitar), y el binder centraliza la conversión implícita en un único punto. Eso reduce el riesgo, no el volumen.

**Beneficios reales esperables, ordenados por certeza:**
- Interop Unity sin castear por frontera (cierto pero modesto: el cast ya cuesta poco por llamada).
- Memoria en arrays grandes de floats — solo con fase 6; sin ella, cero.
- SIMD — solo con fase 6 + opcodes vectoriales nuevos; no contar con ello a corto plazo.
- Throughput aritmético escalar: **negativo o neutro** en la VM interpretada (el cuello es el dispatch, no la ALU; añadir cvt por op probablemente pierde frente a f64 puro). f32 aquí es sobre todo una feature de *semántica e interoperabilidad*, no de velocidad del intérprete.

---

# Parte C — Restricción transversal: presupuesto de opcodes

Verificado sobre `OpCode.cs`: 240 valores asignados (`0x00`–`0xEF`), 16 libres (`0xF0`–`0xFF`). El propio archivo establece la política ("New opcodes take a free value at the end and are never given one already in use", `OpCode.cs:48-56`) y que cambiar el framing del formato exige bump de `FormatVersion` (`:54-56`; el reset de numeración ya ocurrió una vez y está registrado en `OpCodeValueTests`).

Entre i64 (≈19-20 opcodes mínimos) y f32 (≈24, o ≈10 recortada) hay demanda de 30-45 valores para 16 huecos. **Decisión recomendada:** adoptar el mecanismo de escape extendido (opción 2 de B.2 fase 0) una sola vez, con `FormatVersion` bump y tests de valores; asignar entonces bloques limpios por familia. Alternativa conservadora: opcode compuesto por familia (`LongOp sub`, `Float32Op sub`) sin tocar el formato, pagando un byte y un nivel de despacho por instrucción nueva.

---

# Recomendaciones finales

1. **i64 — implementar la Propuesta 2 (side table + tag `0xFFF7`)**, por fases: (1) tag + tabla lateral + aritmética/comparación long con encoding decidido en C.1; (2) binder: `long`, conversiones (`int→long`, `long→float` implícitas; resto explícito), literales > int.MaxValue dejan de ser error de emisión; (3) comparer/hash/diccionario `{long:V}`; (4) interop: `GetLong/CreateLong` y fin del truncamiento silencioso de `long` en el source generator — este punto por sí solo justifica el proyecto; (5) pase de compactación de handles enganchado a `GcPending`.
   - Antes de nada, decidir la política de opcodes (escape vs compuesto): condiciona las fases 1-3.
2. **No adoptar** heap-boxing como estado final (P1) ni smi-53 (P3: aliasing NaN documentado + bitwise caro) ni dos slots (P4: rompería stride-1, decisión ya tomada con evidencia en `Plan-TiposDeValor.md`).
3. **f32 — viable, con coste medio; no es gratis.** Ejecutarlo *después* de i64 y de la decisión de encoding, con: híbrido tag+payload (auto-descriptible), `float32→float` implícito y vuelta explícita, comparaciones vía widen en v1 (menos opcodes), y **sin** storage empaquetado en la primera entrega. Medir con el A/B `vec2Math` existente antes de prometer beneficios de rendimiento; comunicar f32 como feature de semántica/interop, y la memoria de arrays como segunda entrega (fase 6) medible por separado.
4. **Defensa del statu quo donde importa:** el hueco de tags y el rango-compare de `IsFloat` están diseñados para crecer (`SurtrValue.cs:157-163`) — usar exactamente ese camino (tags nuevos por encima de `Absent`), nunca reordenar los existentes: `OpCode.cs` y los tests de valores documentan lo que cuesta ignorar ese tipo de disciplina.
