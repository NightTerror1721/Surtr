# Plan: tipos de valor multi-slot (`value class` generalizado) y retorno multi-slot

**Estado: cerrado — fases 0 a 7 implementadas, medidas y documentadas.** Lo que sigue es el plan
tal como se escribió, conservado como el registro de por qué cada decisión se tomó así. Para el
estado actual del código, la referencia es `docs/VM-Plan.md` §4.11 (cerrada como resuelta), y para
lo medido, `benchmark_report.md` §4.

| Fase | Estado |
|---|---|
| 0 — Red de seguridad y especificación | hecha |
| 1 — Retorno multi-slot end-to-end | hecha (`ReturnValues = 0xE9`) |
| 2 — Runtime de value types | hecha (opcodes `0xEA`–`0xF3`, formato de imagen v8) |
| 3 — `value class` multi-campo end-to-end | hecha |
| 4 — Campos value-type en línea | hecha, con la matriz de GC completa |
| 5 — La tupla como value type | hecha; `SurtrTuple` retenido como forma boxeada |
| 5b — Destructuring de tuplas | añadida sobre la marcha (`Language-Syntax.md` §4.5) |
| 6 — Periféricos | ítems 1 y 2 hechos. El 2 (nativos multi-retorno) se rediseñó a convenio in-place con una sola firma; el 1 (marshaler de structs CLR) llegó después como `[SurtrNativeType(Inline = true)]`, con `SurtrRuntime.DefineNativeValueClass` en el runtime y `SurtrValueLayout` en el bridge — ver `docs/Guia-Interop-Surtr-Csharp.md` §5.1. Los otros cuatro siguen sin hacer, cada uno independiente |
| 7 — Validación y documentación | hecha: cuatro casos de bench nuevos con el A/B `vec2Math`/`vec2Class`, auditoría de GC bajo recolección automática, `Opcodes.md`/`Runtime-Model.md`/`Module-Format.md`/`Language-Syntax.md`/`VM-Plan.md`/`README`/`CLAUDE.md`, y pase de humo LSP |

Tres huecos se dejaron abiertos a propósito, y los tres se rechazan con un error claro en lugar de
compilarse mal: capturar un valor multi-slot en línea dentro de una lambda (necesita su propio
diseño), `for-in` sobre una tupla cruda desde fuente (el binder tipa la variable `unknown` y la
gramática no admite anotación ahí), y patrones anidados dentro de un destructuring (v1 solo admite
nombres planos).

---

Investigación a fondo sobre el
runtime real (`SurtrVirtualMachine`, modelo de valores, registry/GC, linker de layouts, emisor,
descriptores, imagen `.surtrc`, interop y frontera nativa) para determinar si es viable introducir
tipos de valor que ocupen **uno o más slots** — en lugar de ser referencias a objetos del heap — y un
mecanismo de **retorno multi-slot** que permita devolverlos. Sigue el formato de
`docs/Plan-Extensiones.md`: fases independientes, cada una termina en su propio commit tras build +
suite en verde, ninguna empieza sin cerrar la anterior.

---

## Resumen ejecutivo

**Veredicto de viabilidad: alto.** Tres propiedades del diseño actual hacen viable esto donde en otro
VM sería una reescritura:

1. **La pila de datos son slots crudos.** Unmanaged `SurtrRawValue*` fija
   (`src/Surtr.Core/VM/SurtrVirtualMachine.cs:146`) sin tipado por slot: todo slot son 8 bytes
   opacos NaN-boxed (`SurtrValue.cs:15`). Un valor de N slots es simplemente N slots contiguos;
   nada en la pila sabe ni le importa la diferencia.
2. **El GC de pila es conservador por tag.** `CollectGarbage` recorre slot a slot con
   `MarkIfReference` (`SurtrEntityRegistry.cs:433`) — test exacto de tag de referencia. Un valor
   multi-slot en la pila se rastrea correctamente **hoy mismo**, sin tocar nada. Los locals frescos
   ya se ponen a cero al entrar en frame (`SurtrVirtualMachine.cs:3297-3311`), así que una
   colección nunca lee basura entre sub-slots.
3. **El protocolo de frames ya reserva "resultados escritos en el frame base".**
   `retCount` viaja como inmediato en las seis formas de llamada (`docs/Opcodes.md:101`) y vive en
   `SurtrCallFrame.ExpectedResults` (`SurtrCallFrame.cs:81`). Hoy está capado a 0..1
   (`SurtrCodeEmitter.OpCodes.cs:93`); el mecanismo multi-retorno está medio construido.

La fricción real se concentra en cinco puntos, todos acotados: la **frontera nativa/host** de un solo
valor (`delegate SurtrValue SurtrNativeFunction(...)`, `SurtrCallArguments.cs:41`;
`SurtrRuntime.Invoke` → `SurtrRuntime.cs:1412`), el **mapa de reference-slots anidado** cuando un
value type con campos referencia vive dentro de una instancia o static (`SurtrTypeLinker.cs:376`),
la **igualdad/identidad** (`===` pierde sentido sobre un valor sin identidad),
el **layout aplanado** en el linker (`SurtrClass.InstanceSlotCount`, `SurtrClass.cs:77`) y el
**cap de 255 slots** que impone el byte de `argsCount`.

El plan se ejecuta en 7 fases. La fase 1 (retorno multi-slot) es independiente y de bajo riesgo; las
fases 2–4 introducen los value types generales; la fase 5 migra la tupla al nuevo modelo; la 6
recoje periféricos deliberadamente diferidos y la 7 cierra con validación y documentación.

---

## Decisiones de diseño (confirmadas con el usuario)

| # | Decisión | Resolución | Por qué |
|---|---|---|---|
| 1 | Alcance del documento | Todas las fases en detalle | — |
| 2 | Sintaxis | **Extender `value class` a N campos.** El caso de exactamente 1 campo conserva su erasure actual (§2.9) tal cual — cero regresión para stdlib (`Angle`) y código existente; N≥2 campos obtiene la nueva representación | `value` ya es keyword contextual (`Parser.Declarations.cs:96-101`); no hay que enseñar nada nuevo al lexer, grammar TextMate ni LSP. La restricción actual vive en un único punto del binder (`BindValueClassField`, `Binder.cs:1931`, diagnóstico `InvalidValueClass = 3011`) |
| 3 | Identidad `===` sobre un value type | **Error de compilación** (diagnóstico nuevo). `==` sigue siendo igualdad estructural | Un valor inline no tiene identidad: dos copias del mismo `Vec2` no pueden distinguirse. Comparar bits bajo apariencia de identidad invitaría a bugs silenciosos. Precedente: la spec ya hace `==` igualdad de valor en todas partes (§5.7) |
| 4 | Nullabilidad `VT?` | **Prohibida en fases 1–5** con diagnóstico claro; diseño reservado para la fase 6 (tag reservado en el primer slot o par flag+slots) | El tag `Absent` (`TagAbsent`, `SurtrValue.cs:56`) resuelve primitivos; extenderlo a bloques multi-slot necesita su propio estudio y no bloquea nada |
| 5 | Mutabilidad interna | **Solo campos `let`** en un value class multi-campo; `var` se rechaza con diagnóstico. Copiar-al-asignar emerge gratis de las copias de bloque | Misma inmutabilidad que ya hace usable una tupla como clave de diccionario (`SurtrTuple.cs` remarks). Permitir `var` abre preguntas de aliasing local-vs-campo que no valen la complejidad inicial |
| 6 | Retorno multi-slot | **Opción B:** `retCount` sigue significando 0/1 resultados; la **anchura** la aporta el tipo declarado del callee. Nuevo opcode `ReturnValues n` emitido por el *callee* con n = anchura aplanada de su propio retorno | Cero cambios de encoding en las llamadas, cero cambios en `ExpectedResults`, imágenes existentes bit-a-bit idénticas. El caller ya conoce estáticamente cuántos slots va a recibir (conoció el tipo de retorno al compilar el call site) |
| 7 | Forma boxeada de un value type | **Un `SurtrInstance` de su propia clase** (no un `SurtrBoxed` nuevo): `ObjNew` aloca, se copian N slots, `FieldGet`/métodos funcionan sin cambios | Reutiliza layout, `VisitReferences` vía `ReferenceSlots`, vtable, interfaz dispatch y GC de las instancias ordinarias. `SurtrBoxed` (`SurtrBoxed.cs:35`) solo cabe un primitivo y su extensión duplicaría maquinaria |
| 8 | Descriptores | **Sin símbolo nuevo.** Un value type se nombra con su descriptor de clase normal `O<fullname>;<arg>...`; la anchura se resuelve en link desde los descriptores de sus campos + el flag `IsValueType` | La convención de llamada es slots en ambos casos; nadie en bytecode necesita distinguir valor de referencia — solo conocer la anchura, que el linker ya puede derivar. Evita tocar `DescriptorEmitter`, comparadores de descriptor e importador |
| 9 | Formato imagen | Un flag `IsValueType` por clase → **bump de `FormatVersion` 7→8** | El reader rechaza versiones distintas de forma estricta (`SurtrModuleImageReader.cs:90-92`). Añadir opcodes NO requiere bump (aditivo); cambiar metadata de clase sí |
| 10 | Tamaño máximo | Anchura aplanada ≤ **254 slots** (error de compilación si se pasa); recursión infinita (`value class A { let a: A; }`) rechazada en binder | `argsCount`/`retCount` son 1 byte; 255 lo ocupa el margen del receiver. Para el uso objetivo (matemática de juegos) 254 sobra |
| 11 | Value types genéricos (`value class Box<T>`) | **Prohibidos inicialmente** (diagnóstico), diferidos | La anchura dependería de la sustitución (`T=float` vs `T=Vec2`), y la erasure exige UN layout por declaración. Es una contradicción genuina que merece su propio diseño |
| 12 | Arrays y dicts | Elementos value-type **se boxean al cruzar el array** en fases 1–5 (stride 1 intacto). Storage inline stride-N diferido a fase 6 | Toda la familia `Arr*`/`Dict*` asume 8 bytes/elemento; romper el stride es el cambio más caro del espacio de diseño y no es necesario para el 80 % del beneficio |
| 13 | Igualdad estructural | Guiada por el layout del VT: slot a slot con las mismas reglas que hoy comparan elementos de tupla (`SurtrValueComparer`); la forma boxeada compara también la clase | Cierra la deuda que `docs/VM-Plan.md` §4.11 ya señala: el comparer compara boxes por contenido y un día debería mirar la clase |

---

## Investigación: hallazgos por área

### A. Modelo de valores y pila

- `SurtrValue` es un struct explícito de 8 bytes (`LayoutKind.Explicit`) con union de `Raw`/`AsFloat`
  (`Runtime/Objects/SurtrValue.cs:15`). Tags en los 16 bits altos: `Int/Float/Bool/Char/Reference/Absent`
  = `0xFFF1..0xFFF6`. **Quedan 9 nibbles libres** desde `0xFFF7` — el comentario del código dice
  explícitamente que el rango creció así "precisely so this stays a single range compare as the
  claimed range grows" (`SurtrValue.cs:161`). Si la fase 6 introduce `VT?`, hay sitio para un
  `TagValueNull`.
- Una referencia es su payload de 32 bits bajos: **un slot a cero ES null**
  (`docs/VM-Plan.md` §1.10). Consecuencia directa: poner a cero N slots es una inicialización válida
  de cualquier value type cuyo estado "todo-ceros" sea aceptable como valor interno de construcción.
- La pila de datos se asigna zeroed una vez (`AllocateZeroed`, `SurtrVirtualMachine.cs:146`), capacidad
  fija de 64K slots por defecto (`DefaultDataStackSlots`, línea 65). **No crece nunca** — un value
  type grande consume frame, no flexibilidad.
- `Dup/Dup2/Swap/Swap2` (`OpCode.cs:79-100`) cubren hasta 2 slots. No existe `DupN`. No hace falta:
  el compilador ya evita manipular valores complejos sobre el operand stack usando temporales
  (`$collect`, `$subject`, … en todo `MethodBodyEmitter`); un value type simplemente declara su
  temporal con anchura N.

### B. Convención de llamadas y frames

```
   ... | arg0 | arg1 | … | argN-1 |          <- sp
       ^
       frame base == locals[0] == donde se escriben los resultados
```

(`docs/VM-Plan.md` §1.5.) Hallazgos que condicionan el diseño:

- **Entrar en una llamada no copia nada**: el frame del callee empieza debajo de los argumentos
  (`InvokeResolved`, `SurtrVirtualMachine.cs:3269-3337`). Pasar un value type como argumento es
  **gratis** — sus slots ya están donde el callee los espera; solo crece `argsCount`.
- `argsCount(1)` y `retCount(1)` son bytes; el emisor valida `CheckRange(resultCount, 0, 1)`
  (`SurtrCodeEmitter.OpCodes.cs:86-94`). `ExpectedResults` es un `int` en el frame
  (`SurtrCallFrame.cs:81`) — la restricción es del encoding, no del protocolo.
- `ReturnValue` lee 1 slot del tope y lo escribe en `frame.Base` si `ExpectedResults != 0`
  (`SurtrVirtualMachine.cs:3068-3093`); `ReturnVoid` hace lo propio sin valor (`:3041`).
  Generalizar a "copia N slots contiguos" es un bucle de `MemOps` — el mismo patrón del zeroing.
- La comprobación de overflow es **una vez por llamada** contra `LocalCount + MaxStackSize`
  (`:3285`); ambos crecen con value types sin ningún cambio de mecanismo.
- `InvokeClosure` desliza los argumentos una posición hacia abajo moviendo slots crudos
  (`:3012-3015`) — funciona igual con bloques multi-slot contiguos.
- El tracker del emisor modela cada opcode como `(pop, push)` (`SurtrCodeEmitter.cs:116-143`) y ya
  maneja push N (`TupUnpack` hace pops=1/pushes=N). Declarar `ReturnValues(pop: n)` es trivial.
- La entrada en handler de excepción resetea el operand stack a `base + LocalCount` y pushea
  exactamente 1 slot (la excepción, `TryEnterHandler`, `:517-520`). Operandos multi-slot abandonados
  se descartan: correcto por construcción.

### C. GC / registry

- Raíces: pila completa (scan por tag, `CollectGarbage`, `SurtrEntityRegistry.cs:336-353`), bloques
  static (**marcado incondicional por lista declarada de slots**, `:355-360`), raíces explícitas y
  el grafo `VisitReferences` de cada entidad.
- `SurtrInstance.VisitReferences` camina **solo** `Class.ReferenceSlots` — construidos en link desde
  los tipos declarados de los campos (`SurtrTypeLinker.cs:376`, `BuildReferenceSlots`) — "k marks
  instead of n branches" (`SurtrInstance.cs:53-64`).
- **Implicación central:** un value type en la pila funciona gratis (A); un value type como *campo*
  de una clase requiere extender el cálculo de `ReferenceSlots` para incluir los sub-slots de
  referencia de sus value types anidados. Fallar esto es pérdida silenciosa de objetos — es el riesgo
  nº1 del plan y por eso tiene fase propia (Fase 4) con tests específicos.
- Los safepoints existen en dos sitios: tras cada opcode de asignación completado (`Safepoint`,
  `SurtrVirtualMachine.cs:3223`) y en la frontera nativa (`:3263`). `BoxValue` se engancha al primero
  exactamente como `BoxAs` (`:3156-3182`) hace hoy.

### D. Layout y linker

- `SurtrClass.InstanceSlotCount` (`SurtrClass.cs:77`) y `ReferenceSlots` (`:97`) se computan en
  `LinkType` (`SurtrTypeLinker.cs:340-385`). Precedente perfecto de aplanado: los campos heredados
  "come first and keep their base-class slots, so an access compiled against a base-typed index
  works unchanged on derived instances". El aplanado de value types anidados es el mismo truco con
  otra fuente de anchura.
- Statics: `BuildStaticReferenceSlots` (`:400-418`) comparte el patrón; `StaticBlock` lleva
  `Values*` + `ReferenceSlots*` (`SurtrEntityRegistry.cs:24-41`).
- `SurtrMethodBuilder.DeclareLocal()` entrega **un** slot (`SurtrMethodBuilder.cs:283-287`);
  `ArgumentSlotCount` asume 1 slot por parámetro (`:231`). El compilador mapea
  `LocalSymbol → SurtrLocal` 1:1 (`MethodBodyEmitter.cs:4472-4480`). Multi-slot necesita
  `DeclareLocals(n)` (rango contiguo) y que `Parameter(i)` salte anchuras.

### E. Compilador / binder / codegen

- El `value class` de 1 campo hoy: erasure al descriptor del campo donde el tipo es estático
  (`DescriptorEmitter.cs:200-205,270-285`), constructor *spliced* en cada sitio de creación
  (`EmitValueClassCreation`, `MethodBodyEmitter.cs:2963`), `BoxIfValueClass` en cada cruce a slot de
  referencia (`:1656`), receiver unbox/box según despacho (`LoadReceiver`, `:4521-4548`). Todo esto
  **queda intacto** para el caso de 1 campo.
- `EmitReturn` (`:1213-1258`) emite `ReturnValue` — añadir la rama "retorno de anchura N" es un caso
  más junto al de finallies.
- `EmitResolvedCall`/`EmitCall` (`:3056-3189`) derivan `results = 0/1` del tipo de retorno
  (`SurtrCodeEmitter.Helpers.cs:763-764`) — no cambian; lo nuevo es que un resultado lógico puede
  ocupar N slots, cosa que el emisor ya sabe desde el tipo del callee.
- Tuplas hoy: literal → `TupPack(typeIdx, arity)` (`EmitTupleLiteral`, `:3904-3913`), acceso
  constante → `TupGetC` (`OpCode.cs:1894`), unpack → `TupUnpack`. Arity cap 255
  (`MaxTupleArity`, `TypeResolver.cs:511`). Dos asignaciones CLR por tupla + registro GC.
- Diagnóstico `InvalidValueClass = 3011` (`SurtrDiagnosticCode.cs:149`) — el punto exacto donde se
  levanta la restricción de 1 campo y se añaden las nuevas (var, recursión, tamaño, genéricos, `VT?`,
  `===`).

### F. Imagen y metadatos

- `FormatVersion = 7` (`SurtrModuleImage.cs:112`); el reader es estricto (`:90-92`). Los métodos ya
  serializan `LocalCount`/`MaxStackSize` (`SurtrModuleImageWriter.cs:396-397`) — ambos crecen solos.
- Las clases ya viajan como entradas completas con sus fields y descriptores; el flag `IsValueType`
  es el único dato nuevo por clase (Fase 2, bump a 8).
- Añadir opcodes con valores libres no requiere bump: "New opcodes take a free value at the end"
  (`OpCode.cs:48-53`).

### G. Frontera nativa, host e interop

- Todo nativo tiene **una forma fija**: `delegate SurtrValue SurtrNativeFunction(SurtrCallArguments)`
  — retorna exactamente 1 slot (`README` interop; `SurtrNativeEntryPoint.Invoke`,
  `SurtrNativeEntryPoint.cs:178`). Decisión (D6/D7): los nativos mantienen retorno de 1 slot; un
  nativo que quiera producir un value type lo boxea. La variante multi-retorno nativa queda en fase 6.
  Los **argumentos** nativos ya son slots crudos (`SurtrCallArguments` envuelve `SurtrRawValue*` +
  length) — un value type como argumento de un native funciona sin cambios.
- `SurtrRuntime.Invoke(...)` retorna `SurtrValue` (`SurtrRuntime.cs:1412-1424`); la máquina expone
  `StackBase/StackTop/Call` internos suficientes para una sobrecarga `bool TryInvoke(...,
  Span<SurtrValue> results)` que copie N slots tras `Execute(entryDepth)`.
- Interop CLR: los structs se boxean hoy (`Descriptors.cs:15` "Struct = 1 ... boxing"). Con VTs
  reales, un struct CLR podría marshalarse inline — win futuro grande, trabajo del marshaler,
  **fase 6**.

### H. LSP y tooling

- El LSP reusa el binder real, así que hover/narrowing/completado heredan todo. El parser no cambia
  (misma gramática de `value class`); la grammar TextMate no cambia. Solo nuevos diagnósticos fluyen
  al editor, como cualquier otro del binder.

---

## Opcodes nuevos propuestos

Valores libres garantizados: `0xE9`–`0xFF` (23 valores; último asignado `NewFunctionX = 0xE8`).
Se proponen 11, agrupados por fase de introducción. Todos siguen las convenciones de
`docs/Opcodes.md` (immediates little-endian; transición escrita como `... -> ...`).

| Valor | Nombre | Encoding | Transición | Fase | Notas |
|---|---|---|---|---|---|
| `0xE9` | `ReturnValues` | `opcode(1) n(1)` · 2 B | `..., s1..sn -> (escritos en frame base)` | 1 | Copia n slots contiguos del tope a `frame.Base` si `ExpectedResults != 0`; descarta si no. Emitido por el callee con n = anchura aplanada de su retorno (n ≥ 2; 0/1 siguen usando `ReturnVoid`/`ReturnValue`) |
| `0xEA` | `LoadValueLocal` | `opcode(1) localIdx(2) n(1)` · 4 B | `... -> ..., s1..sn` | 2 | Copia n slots del rango del local al tope. Sin resolución de tipos: n viaja en la instrucción |
| `0xEB` | `StoreValueLocal` | `opcode(1) localIdx(2) n(1)` · 4 B | `..., s1..sn -> ...` | 2 | Pop n → rango del local |
| `0xEC` | `LoadLocalField` | `opcode(1) localIdx(2) offset(2)` · 5 B | `... -> ..., v` | 2 | Empuja 1 slot: `*(frameBase + localIdx + offset)`. Leer `v.x` sin copiar el `Vec2` entero. Legal también fuera del ctor |
| `0xED` | `StoreLocalField` | `opcode(1) localIdx(2) offset(2)` · 5 B | `..., v -> ...` | 2 | Pop 1 → sub-slot. En la práctica solo lo emite el splice del constructor (campos `let`) |
| `0xEE` | `BoxValue` | `opcode(1) typeIdx(2) n(1)` · 4 B | `..., s1..sn -> ..., ref` | 2 | Aloca instancia de la clase (layout ya aplanado), copia los n slots, registra, Safepoint. El gemelo de `BoxAs` (`SurtrVirtualMachine.cs:3156`) |
| `0xEF` | `UnboxValue` | `opcode(1) n(1)` · 2 B | `..., ref -> ..., s1..sn` | 2 | Copia `Fields[0..n)` a la pila. n lo conoce el compilador; el VM no resuelve el tipo |
| `0xF0` | `LoadValueField` | `opcode(1) fieldIdx(2) n(1)` · 4 B | `..., obj -> ..., s1..sn` | 4 | Lee un campo value-type aplanado de una instancia (pop ref, push n slots) |
| `0xF1` | `StoreValueField` | `opcode(1) fieldIdx(2) n(1)` · 4 B | `..., obj, s1..sn -> ...` | 4 | Escribe un campo value-type aplanado |
| `0xF2` | `LoadValueStatic` | `opcode(1) fieldIdx(2) n(1)` · 4 B | `... -> ..., s1..sn` | 4 | Análogo a `StaticFieldGet` para anchura N |
| `0xF3` | `StoreValueStatic` | `opcode(1) fieldIdx(2) n(1)` · 4 B | `..., s1..sn -> ...` | 4 | Análogo a `StaticFieldSet` |

Lectura de un sub-slot de un value type **campo de una instancia** (`entity.position.x`): no lleva
opcode nuevo. Con layout aplanado, `position.x` ES `Fields[positionSlot + 0]` — el compilador baja
`entity.position.x` a un `FieldGet` con el slot absoluto ya sumado, igual que la herencia reusa los
slots base (`SurtrTypeLinker.cs:340`). `FieldGet/FieldSet` no cambian.

---

## Fases de implementación

### Fase 0 — Red de seguridad y especificación

**Objetivo:** fijar el comportamiento actual antes de tocarlo y escribir la spec del lenguaje.

- Actualizar `docs/Language-Syntax.md` §2.9: `value class` pasa a admitir N campos `let`; reglas
  nuevas (inmutabilidad, prohibiciones de la tabla de decisiones, cap de tamaño, sin genéricos aún).
  Marcar explícitamente que el caso de 1 campo conserva semántica de erasure.
- Tests de pinning previos (deben seguir verdes durante TODO el plan):
  - Suite existente de `value class` de 1 campo (stdlib `Angle` incluida) — **no se toca ninguna rama
    de 1 campo en todo el plan**; esta es la garantía anti-regresión.
  - Test de bytecode que ejercite `ReturnValue`/`ExpectedResults` actuales a nivel de emisor manual
    (`src/Surtr.Tests/VM`, estilo de los tests que fijan layout de bytes).

**Criterio de done:** suite en verde; §2.9 reescrita.
**Commit sugerido:** `Docs: especificacion de value class multi-campo y red de seguridad de tests`

### Fase 1 — Retorno multi-slot end-to-end

**Objetivo:** infraestructura completa de retorno N-slots, usable desde bytecode generado a mano y
desde el host, aunque nadie la use todavía.

**Core (`src/Surtr.Core`):**

- `Bytecode/OpCode.cs`: `ReturnValues = 0xE9` con doc de encoding/transición estilo familia.
- `VM/SurtrVirtualMachine.cs`: caso en `Run` — pop n, copia a `finished.Base` condicionada a
  `ExpectedResults`, misma disciplina de limpieza de frame muerto que `ReturnValue` (`:3077-3081`).
  Copia con fast path ≤2 slots escrito a mano + `MemOps.Copy` para el resto (patrón del zeroing de
  frames, `:3297-3311`). **Sin helper calls en el path caliente** — regla de oro del switch.
- `Bytecode/Emit/SurtrCodeEmitter.OpCodes.cs`: `ReturnValues(int n)` con `Track(n, 0)`, validación
  `n >= 2 && n <= 255`.
- `Runtime/SurtrRuntime.cs`: sobrecarga pública `bool TryInvoke(SurtrMethodInfo, ReadOnlySpan<SurtrValue> args, Span<SurtrValue> results)`
  que llama a `machine.Call` y, si el método retorna, copia los N slots resultantes. Mantener
  `Invoke` existente intacto (delega cuando la anchura es ≤1).
- `Bytecode/Emit/SurtrBytecodeDisassembler.cs`: decodificar/imprimir la instrucción nueva.

**Tests:** opcode-level (bytes exactos), ejecución de una función que retorna 3 slots consumidos por
una segunda función; `TryInvoke` con span; presupuesto de steps no alterado.

**Criterio de done:** suite en verde; benchmarks `allocation/tuples` sin regresión medida.
**Commit sugerido:** `Feature: retorno multi-slot (ReturnValues) en VM, emisor y host API`

### Fase 2 — Runtime de value types (metadatos, layout, opcodes)

**Objetivo:** toda la maquinaria de Core necesaria para representar y mover value types, todavía sin
que el compilador los genere.

**Core:**

- `Runtime/Classes/SurtrClass.cs`: `internal bool IsValueType;` + `internal int FlattenedSlotWidth;`
- Linker (`SurtrTypeLinker.cs`): en `LinkType`, si `IsValueType`, calcular anchura aplanada
  recursivamente (primitivo/ref/string/array/dict/closure/range = 1; value type anidado = su
  anchura) con detección de ciclos; `InstanceSlotCount` pasa a ser esa anchura. Rechazar anchura > 254.
- Imagen: flag `IsValueType` por clase en writer/reader → **`FormatVersion` 7 → 8**
  (`SurtrModuleImage.cs:112`), actualizar `docs/Module-Format.md`.
- Opcodes `0xEA`–`0xEF` (tabla anterior): casos en `Run` escritos inline; `BoxValue`/`UnboxValue`
  por el camino de `ObjNew`+copia y `Safepoint`; tracker del emisor para los seis.
- Comparer (`SurtrValueComparer`): rama estructural para boxes de value type — comparar clase y luego
  slot a slot con las reglas vigentes de elementos de tupla; hash coherente. Cierra además la deuda
  señalada en `docs/VM-Plan.md` §4.11.
- `MetadataImporter` (lado compiler, para imágenes referenciadas): reconstruir `IsValueType` y la
  anchura desde los descriptores importados.

**Tests:** linker con VTs anidados 2 y 3 niveles (anchuras exactas); ciclo rechazado; >254 rechazado;
round-trip de imagen con el flag; comparer (dos boxes iguales/distintos, hash estable).

**Criterio de done:** suite en verde; nada del lenguaje nuevo visible aún (el compilador no emite
estos opcodes).
**Commit sugerido:** `Feature: runtime de value types - layout aplanado, opcodes de movimiento y comparacion estructural (formato v8)`

### Fase 3 — Compilador: `value class` multi-campo end-to-end (mismo módulo)

**Objetivo:** declarar y usar un `value class` de N campos dentro de un módulo: locals, argumentos,
retorno, creación, lectura de campo, métodos y boxing en los cruces.

**Compiler (`src/Surtr.Compiler`):**

- Binder:
  - `BindValueClassField` (`Binder.cs:1931`): aceptar N campos; todos `let` (rechazar `var` con
    diagnóstico); rechazar genéricos en la declaración (D11); detectar recursión de layout; validar
    cap 254. Reusar/ampliar `InvalidValueClass = 3011` o crear códigos propios.
  - Tipos: `TypeSymbolKind.ValueClass` ya existe; el binder debe exponer la anchura aplanada al
    emisor (cacheada por símbolo, como hace `TypeSymbolFactory` con tuplas).
  - Diagnósticos nuevos: `===` sobre value type; `VT?` en anotación; asignación de campo fuera del
    constructor.
- Codegen (`MethodBodyEmitter.cs`):
  - Locales-rango: `SurtrMethodBuilder.DeclareLocals(name, width)` en Core + mapa
    `_locals[local] = (slotBase, width)`; lecturas de value type completo → `LoadValueLocal`;
    escrituras → `StoreValueLocal`; lecturas de campo → `LoadLocalField localIdx offset` con offset
    precalculado.
  - Parámetros: `Parameter(i)` avanza anchuras acumuladas; `ArgumentSlotCount` suma anchuras
    (`SurtrMethodBuilder.cs:231`). Los call sites ya cuentan bien porque usan esa misma propiedad
    (`Helpers.cs:619,645`).
  - Retorno: `EmitReturn` emite `ReturnValues(width)` cuando la anchura del tipo de retorno ≥ 2
    (`:1213-1258`).
  - Creación: el splice del constructor escribe cada campo con `StoreLocalField` sobre el temporal
    `$this` (generalización de `EmitValueClassCreation`, `:2963`).
  - Cruces a slot de referencia (genérico erased, `unknown`, interfaz): `BoxValue`/`UnboxValue`
    sustituyen a `BoxAs`/`Unbox` para N≥2 campos (el camino de 1 campo sigue en `BoxIfValueClass`,
    `:1656`). Lectura de vuelta: `Cast` a la clase + `UnboxValue`.
  - `==` estructural: bajar a llamada al comparer — decisión de emisión: opcode `DynEQ`
    (`OpCode.cs:793`) ya compara por contenido vía `SurtrValueComparer.ValuesEqual`; verificar que
    cubre instancias-box de VT con la nueva rama estructural de Fase 2, y en caso contrario añadir
    la rama antes de tocar nada más.
- Stdlib de prueba (temporal, hasta decidir API pública): un `Vec2` de ejemplo en los tests, no en
  `surtr.math`.

**Tests (end-to-end `Surtr.Run`/tests de emitter, estilo región "Reflexion de atributos"):**
declaración N campos; pasar/recibir VT por argumento (incl. mezclado con primitivos y referencias);
retorno de VT (verifica Fase 1 de verdad); `v.x` sin copiar; métodos de instancia sobre VT (receiver);
boxing round-trip por genérico; `==`/`!=`; todos los diagnósticos nuevos; el caso de 1 campo intacto
(pinning de Fase 0).

**Criterio de done:** suite en verde; benchmark rápido de humo (workload Vec2) muestra 0 allocaciones
en el hot path medido con la columna `alloc` del harness.
**Commit sugerido:** `Feature: value class multi-campo - binder, locals-rango, constructores spliced y boxing`

### Fase 4 — Campos value-type inline en clases, singletons y statics

**Objetivo:** `class Enemy { public var position: Vec2; }` sin indirección: los slots del VT viven
dentro del layout de la instancia/static.

**Core:**

- Linker: aplanado total — un campo de tipo VT contribuye k slots en offset continuo (los miembros
  del VT continúan la numeración, mismo precedente que la herencia); `BuildReferenceSlots`
  (`SurtrTypeLinker.cs:376`) y `BuildStaticReferenceSlots` (`:400-418`) deben registrar los
  sub-slots de referencia de los VTs anidados (p. ej. `struct Attack { Vec2 dir; string label; }` →
  el slot del string entra al mapa). **Este es el punto de mayor riesgo del plan** — ver Riesgos.
- Opcodes `0xF0`–`0xF3` (tabla anterior).
- `SurtrFieldInfo`: exponer anchura/offsets para que el emisor calcule slots absolutos.

**Compiler:**

- Acceso a miembro de campo-VT: `enemy.position.x` baja a `FieldGet(slot absoluto)` — sin opcode
  nuevo (ver sección de opcodes). Asignación completa `enemy.position = v` → `StoreValueField`;
  lectura completa → `LoadValueField`; statics equivalentes.
- Constructores: el splice encadena `ObjNew` + `StoreValueField` por cada campo VT.

**Tests (los más importantes del plan):**

- **GC stress:** VT con `string`/`array` dentro, como campo de clase, en static, capturado en
  closure, pasado a native que fuerza colección — nada se barre (regresión silenciosa imposible de
  ver sin estos tests).
- Round-trip completo: crear → escribir campo → leer sub-slot → forzar `Collect()` → leer de nuevo.
- Herencia: clase base con campo VT, acceso compilado contra el tipo base sobre instancia derivada.

**Criterio de done:** suite en verde incl. GC stress; benchmark de workload con entidades.
**Commit sugerido:** `Feature: campos value-type inline en instancias y statics con reference-slots anidados`

### Fase 5 — La tupla como value type

**Objetivo:** `(A, B)` deja de alocar en el camino caliente; `SurtrTuple` queda como forma boxeada.

**Decisiones ya tomadas que aplican:** `===` sobre tuplas pasa a error (consistente con D3);
elementos de tupla en arrays/dicts/keys se boxean (D12); inmutabilidad ya garantizada.

**Compiler:**

- Literal → secuencia de elementos + dejarlos en pila como bloque (ya ocurre: `EmitTupleLiteral`
  empuja elementos y empaqueta; ahora no empaqueta, solo tipa el bloque). `t.i` constante →
  `TupGetC` se sustituye por `LoadLocalField`/lectura directa del rango cuando la tupla vive en
  local; `TupGet` dinámico (for-in lowered) mantiene su forma leyendo del rango del temporal.
- Destructuring: `TupUnpack` se vuelve copia de bloque (o desaparece del lowering).
- Retornos/destructuring de llamadas que retornan tupla → `ReturnValues(n)` (Fase 1 paga aquí).
- Claves de dict y elementos de array: box explícito en el boundary.

**Core:** `SurtrTuple` se conserva para las formas boxeadas (claves, iteradores, reflexión);
`TupLen/TupPack/TupUnpack/TupGet/TupGetC` se redefinen sobre la nueva representación o se retiran
según quede el lowering final — decidir con el lowering delante, no antes.

**Tests:** migración de TODA la suite existente de tuplas (es el mejor test de regresión posible);
benchmark `tuples` del harness — objetivo: columna `alloc` a ~0 en el caso de tuplas efímeras.

**Criterio de done:** suite en verde; benchmark `tuples` mejora o empata con evidencia.
**Commit sugerido:** `Feature: tupla como value type - representacion inline con forma boxeada retenida`

### Fase 6 — Periféricos (opcional, cada uno independiente)

Ordenado por relación beneficio/coste; **ninguno bloquea el cierre del plan**:

1. **Marshaler CLR struct ↔ VT inline** (`Surtr.Interop`): hoy `Struct = boxing` (`Descriptors.cs:15`).
   Con VTs reales, `[SurtrNativeType]` sobre un struct puede mapearlo a un value type Surtr sin box.
2. **Nativos multi-retorno:** segunda forma de entry point (`int Native(SurtrCallArguments,
   SurtrRawValue* results)`) o convención "escribe en los slots de resultado". Rompe la invariant
   de "una sola forma de delegado" — medir antes de hacer.
3. **`VT?` nullable:** tag reservado en el primer slot (hay nibbles libres, `SurtrValue.cs:18-56`)
   o par flag+slots; `?.`/`??`/`as?` sobre VT.
4. **Arrays stride-N:** storage inline de value types (rompe `Arr*`/`Dict*` — el cambio más caro del
   espacio; solo si hay evidencia de demanda).
5. **Value types genéricos:** requiere repensar erasure-vs-layout (contradice "one layout per
   declaration").
6. **For-in sobre VT sin box del receiver:** especializar el lowering cuando el iterable es
   estáticamente VT (hoy interface dispatch exige referencia).

### Fase 7 — Validación y documentación

- **Benchmarks** (`src/Surtr.Bench`, `-c Release`): caso nuevo de matemática con value types
  (workload Vec2 estilo juego); comparar `alloc`/tiempo contra la versión previa y contra LuaJIT/C#
  baseline. Documentar en `bench_results.csv`/`benchmark_report.md`.
- **Auditoría GC:** repetir la matriz de escenarios de Fase 4 con colecciones automáticas activadas
  (política por defecto) y con manuales.
- **Docs:** `Opcodes.md` (11 instrucciones nuevas con encoding), `Runtime-Model.md` (convención con
  anchuras), `Module-Format.md` (v8 + flag), `VM-Plan.md` §4.11 (cerrarla como resuelta),
  `Language-Syntax.md` §2.9 definitiva, README (tabla del object model).
- **LSP:** pase de humo de diagnósticos/hover sobre un proyecto de ejemplo con VTs.

**Commit sugerido:** `Docs y bench: cierre del plan de tipos de valor (opcodes, runtime model, formato v8)`

---

## Riesgos y mitigaciones

| # | Riesgo | Prob. | Impacto | Mitigación |
|---|---|---|---|---|
| 1 | `ReferenceSlots` anidado mal calculado → objetos barridos o retenidos (bug silencioso de GC) | Media | Crítico | Fase 4 dedicada exclusivamente a esto, con la matriz completa de tests GC-stress ANTES de dar la fase por cerrada; los statics van primero porque su walk es incondicional y más fácil de verificar |
| 2 | Regresión del dispatch loop (helper calls en hot path) | Baja | Alto | Regla escrita en cada tarea de VM: bodies inline en el switch, `MemOps` vectorizado solo para >2 slots, traps por `NoInlining` como hoy |
| 3 | Frames más grandes agotan la pila fija (64K slots) | Baja | Medio | El overflow ya se detecta una vez por call con mensaje claro; tunear `DefaultDataStackSlots` si los benches lo piden |
| 4 | Compatibilidad de imágenes | Baja | Medio | Único bump de formato (Fase 2); opcodes siempre aditivos; el caso 1-campo intocado elimina la mayoría del riesgo de regresión semántica |
| 5 | Cap de 255 slots mordiendo casos reales | Muy baja | Bajo | Error de compilación claro desde Fase 2; ampliar a formas X wide solo si hay demanda |
| 6 | Comparer inconsistente (hash ≠ equals) rompiendo dict keys | Media | Alto | Rama estructural con tests de contrato hash/equals en Fase 2, antes de que exista cualquier VT real |
| 7 | Alcance descontrolado en Fase 5 (tupla toca mucho lowering) | Media | Medio | `SurtrTuple` boxeado retenido; migrar por puntos de uso (literal → retorno → destructuring → keys), un commit por paso |

---

## Orden y dependencias

```
Fase 0 ──► Fase 1 ────────────────────────────────────────────┐
        └─► Fase 2 ──► Fase 3 ──► Fase 4 ──► Fase 5 ──► Fase 7
                                  └───────► Fase 6 (items independientes, cualquier momento tras Fase 4)
```

Fases 1 y 2 son independientes entre sí y pueden desarrollarse en paralelo. La Fase 3 depende de 1+2;
la 4 de 3; la 5 de 4. La 6 es un menú. Cada fase termina en commit propio con build + suite en verde.

---

## Apéndice: qué NO cambia (garantías del diseño)

- La representación de 8 bytes por slot y el NaN boxing (`SurtrValue.cs`) — intocado.
- La convención de llamada existente: encoding de los opcodes de call, `argsCount`, `retCount`,
  `ExpectedResults`, zero-copy de argumentos — intocados (D6).
- Todo el comportamiento de `value class` de 1 campo (erasure, `BoxAs`, splice, stdlib `Angle`) —
  intocado por construcción.
- El GC de pila, los safepoints, el budget de pasos, las tablas de handlers — sin cambios de
  mecanismo; solo nuevos cuerpos de opcode que respetan las mismas reglas.
- La frontera nativa de un solo valor y `SurtrRuntime.Invoke` — intactos en fases 1–5.
