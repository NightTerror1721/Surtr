# Plan: juego de instrucciones extendidas (prefijo `0xFF`)

Fecha: 2026-08-26. Alcance: diseño del espacio de opcodes extendidos y del catálogo de
superinstrucciones que lo va a habitar, más la reclasificación de las propuestas de
`docs/Informe-Optimizaciones-Bytecode.md` a la luz de ese espacio.

Documento vivo: el plan y su ejecución. El estado de cada fase está en §9, y las mediciones se
anotan aquí conforme se toman en vez de quedarse en el terminal de quien las corrió.
`src/Surtr.Core/Bytecode/OpCode.cs` y `SurtrExtOpCode.cs` son la fuente de verdad del juego de
instrucciones; `docs/Opcodes.md` es su lectura, y `docs/VM-Plan.md` el porqué de la forma del
intérprete.

---

## 1. Punto de partida: correcciones factuales

Tres datos que hay que fijar antes de decidir nada, porque la documentación existente discrepa
entre sí y con el código:

| Dato | Dónde está mal | Realidad verificada |
|---|---|---|
| Tamaño del juego | `CLAUDE.md` dice «247 opcodes, `0x00`–`0xFC`, libres `0xFD`–`0xFF`» | **240 opcodes**, `0x00`–`0xEF`, **libres `0xF0`–`0xFF`** (16 valores). `docs/Opcodes.md` y el informe sí lo dicen bien |
| `FormatVersion` | `CLAUDE.md` dice 9; `docs/Opcodes.md` dice 10; el informe dice 10 | **12** (`SurtrModuleImage.cs:158`) |
| Almacén `int` de diccionario | El informe dice «hoy `IntEntries` nunca vuelve a null» | **Falso.** `SurtrDictionary.Deoptimize()` lo pone a null (`SurtrDictionary.cs:283`), alcanzable desde el host. Recorta P3 (§2) |

La primera y la segunda son deuda documental (§10). La tercera cambia el diseño de una propuesta.

---

## 2. Reclasificación del informe previo

`docs/Informe-Optimizaciones-Bytecode.md` está bien verificado contra el código y su inventario de
§2.3 es exacto. Lo que le falta es un criterio homogéneo de beneficio: mezcla propuestas que
eliminan **despachos** con propuestas que eliminan **un test dentro de un despacho**, y las estima
en la misma escala porcentual. Son escalas distintas por un orden de magnitud.

**La unidad de cuenta.** En `intLoop` una iteración cuesta 10.099 ms / 1M = **~10.1 ns** repartidos
entre unas 8-10 instrucciones, así que un despacho vale del orden de **~1 ns**. Un `isinst` o una
comparación de tag perfectamente predicha vale **~0.2-0.3 ns**. Todo lo que sigue se mide en
despachos ahorrados.

Con ese criterio, las nueve propuestas quedan así:

| | Propuesta | Ahorro real | Veredicto |
|---|---|---|---|
| **A. Hacer** | **P4** paso de bucle contado | 5 despachos → 1 | Correcta, pero **infradimensionada**: ver §5 grupo A, que colapsa 10 → 1 en el recorrido indexado y **17 → 1** en el de diccionario |
| | **P5** lectura indexada sin guard duplicado | 1 comparación | **Absorbida** por el grupo A: el test de rango y la lectura pasan a ser la misma instrucción, así que no queda comprobación duplicada que eliminar ni hace falta un opcode «inseguro por contrato» |
| | **P1** plegado constante | n instrucciones en código constante-pesado | Correcta, sin riesgo, sin opcodes. Las dos cautelas que enumera son las buenas (envolver a 32 bits; no plegar lo que trapea) |
| | **P9** sort en Surtr | 1 reentrada al VM por comparación | Ortogonal a todo lo demás, sin tocar VM ni formato. **Medida y cerrada en negativo** (§9, Fase 1): el sort en bytecode es un 54 % más lento |
| **B. Solo fusionadas** | **P2** campos nativos | 1 `isinst` + 1 rama predicha | La estimación «3-8 % en `fieldAccess`» no se sostiene: realista 1-2 %, dentro del ruido. **No** justifica gastar 4 valores primarios. Sí paga fusionada (§5 grupo D) |
| | **P3** diccionarios `int` | 1 test de tag | Peor de lo que el informe cree: como `Deoptimize()` existe, el opcode rápido **está obligado** a conservar el test de `IntEntries != null`. Solo queda el test de tag. Paga fusionada, no suelta |
| **C. Reformular** | **P6** peephole/liveness sobre bytes | infraestructura | Destino correcto, vehículo equivocado: un pase sobre el buffer emitido tiene que **reconstruir** fronteras de etiqueta y rangos de handler que el emisor ya conoce. Ver §6 |
| | **P7** `CallModule` plano | ~1-3 ns/llamada cruzada | Existe una versión de riesgo casi nulo que el informe no separa: tabla plana `SurtrMethodInfo[]` en el chunk, resuelta en `LoadModule`, indexada por el operando **actual** — sin opcode nuevo ni cambio de imagen |
| **D. Cerrar** | **P8** split caliente/frío del switch | 0-5 %, probablemente 0 | El informe ya duda de ella. Argumento adicional en contra: el coste de I-cache no está en la **tabla de saltos** (240 × 4-8 B, en rodata) sino en los **cuerpos**, y partir el `switch` no controla el layout de los cuerpos — eso lo decide el JIT. Además ahora **interfiere** con el switch extendido |

De §4 del informe (técnicas descartadas) no hay nada que revisar: los siete descartes son correctos,
y el 3 —«no convertir a bytecode de registros»— es especialmente importante aquí, porque el grupo B
de §5 captura buena parte de ese beneficio **sin** renumerar el formato ni reescribir el emisor.

---

## 3. El espacio extendido

### 3.1 Codificación

`0xFF` pasa a ser **prefijo**, no opcode. La instrucción extendida es:

```
0xFF  sub(1)  <inmediatos>
```

`sub` indexa un enum nuevo, `SurtrExtOpCode`, con su propio espacio de 256 valores. Se reserva
`sub = 0xFF` como **segundo prefijo** por si algún día hicieran falta 65.536 valores; hoy no cuesta
nada y evita quedarse sin salida una segunda vez.

En el intérprete es un `switch` anidado dentro de un `case`:

```csharp
case OpCode.Ext:
    switch ((SurtrExtOpCode)(*ip++))
    {
        // cuerpos, todos inline, mismo estilo que el switch principal
    }
```

### 3.2 La regla de admisión

Una instrucción extendida paga, frente a una primaria: **1 byte** de codificación, **1 carga** y
**1 salto indirecto adicional**. La estimación de partida era que ese salto valiera del orden de un
despacho completo (~1 ns), con un matiz a favor: el segundo `switch` es un sitio de predicción
**distinto**, con una distribución de destinos mucho más estrecha que el despacho principal, así
que un predictor moderno lo acierta mejor.

**Medido (Fase 0, 2026-08-26, Ryzen 9800X3D / .NET 8.0.13 / Release):**

```
surtrbench --prefix-tax --iters 3000000 --rounds 15

  LdlS          54.499 ms   spread 5.1 %
  Ext/Probe     66.036 ms   spread 5.9 %
  prefix tax     0.481 ns per prefixed dispatch     (repetición: 0.441 ns)
```

**0.44–0.48 ns**, la mitad de lo estimado: el matiz era el efecto dominante. La regla se recalibra
en consecuencia y queda **más permisiva** de lo que el diseño anticipaba:

> **Un opcode extendido debe ahorrar ≥1 despacho para ganar**, con margen sobrado — un despacho
> ahorrado vale ~1 ns y el prefijo cuesta ~0.46 ns.
> Lo que sigue perdiendo es el opcode que **no ahorra ningún despacho** y solo elimina un test de
> tipo, un test de tag o una comparación: ~0.25 ns de ahorro contra ~0.46 ns de prefijo.

**Corregida por la Fase 3 (§9), y esta es la forma buena.** Contar despachos como si valieran todos
lo mismo es demasiado grueso: el grupo C eliminó dos por iteración y no movió nada, ni siquiera en
un bucle construido para exponerlo. Un `Ldl` es una carga sin dependencias cuya rama indirecta es la
mejor predicha del intérprete, y quitarlo quita trabajo que el motor fuera de orden ya solapaba.

> Lo que paga no es el número de despachos sino **el trabajo que serializa** dentro de ellos: una
> rama que decide el flujo, una dependencia de memoria, una búsqueda en una tabla, una comprobación
> de rango. El grupo A ganó un 47 % quitando eso; el grupo C perdió quitando cargas baratas.

Corolario práctico, que no cambia: el espacio extendido es para **fusiones**, no para
micro-especializaciones sueltas. Lo que sí cambia es el margen — el grupo D, que ahorra uno o dos
despachos *además* del test, entra con holgura en vez de entrar al límite, y `NativeFieldGetL`
deja de estar condicionada.

La medición se conserva como instrumento (`SurtrExtOpCode.Probe` y `src/Surtr.Bench/PrefixTax.cs`),
no como anécdota: el número depende de la CPU, del JIT y del backend, así que en IL2CPP o en otra
máquina hay que volver a pedirlo antes de apoyarse en él.

### 3.3 Política del espacio primario

Reservando `0xFF` como prefijo quedan **15 valores primarios** (`0xF0`–`0xFE`). Son irreemplazables:
son el único sitio donde una instrucción cuesta un solo despacho.

**Se quedan reservados.** Ninguna de las propuestas evaluadas necesita esa propiedad: las que
ahorran despachos van igual de bien en extendido, y las que ahorran sub-despacho (P2, P3) no
justifican consumirlos. Se gastarán solo cuando aparezca algo que **únicamente** el espacio
primario pueda cubrir — es decir, una instrucción cuyo beneficio total sea menor que un despacho
y aun así merezca existir.

### 3.4 `FormatVersion`

Añadir opcodes no cambia el *framing* de la imagen, así que la regla vigente («un opcode nuevo no
bumpea») sería aplicable. Aun así **se sube a 13 al introducir el prefijo**, una sola vez:

- Un build **antiguo** leyendo una imagen **nueva** encontraría `0xFF` y lo trataría como opcode
  desconocido en mitad de la ejecución, en vez de rechazar la imagen al cargarla. Ese es
  exactamente el fallo que una versión de formato existe para convertir en un error honesto.
- A partir de ese bump, **añadir opcodes extendidos no vuelve a subirla**. La versión sigue
  cubriendo cómo se enmarca un módulo, más «este build entiende el prefijo».

### 3.5 Contrato de las instrucciones extendidas

Cinco reglas que toda instrucción del espacio extendido debe cumplir, y que hay que verificar caso
por caso:

1. **Presupuesto.** Una superinstrucción que transfiere control (todo el grupo A, todo el grupo C)
   es una transferencia y debe salir por `Branched`, no por `Dispatch`. Si no, `InstructionBudget`
   deja de acotar bucles — y el plegado de `const fun` depende de ese acotamiento
   (`ConstFolder.cs`).
2. **Ancho de slot.** Los operandos de slot van en 1 byte. El emisor emite la forma fusionada
   **solo si todos los slots implicados caben en un byte**; con más de 256 locales cae a la
   secuencia clásica. Es la misma disciplina que ya aplica `SurtrCodeEmitter.Helpers.cs` al elegir
   entre `Ldl0..5` / `LdlS` / `Ldl`.
3. **Relajación de saltos.** Toda instrucción extendida con offset lleva **gemelo `X`** con offset
   de 4 bytes, para que `SurtrJumpWidth.Auto` pueda ensancharla en el pase de punto fijo. El
   espacio extendido es abundante; des-fusionar durante la relajación no lo es.
4. **Offsets.** Se miden desde el **final de la instrucción**, como todas las ramas salvo `Switch`.
   El prefijo cuenta como parte de la instrucción.
5. **Safepoints.** Cualquier extendida que asigne (ninguna del catálogo de §5 lo hace, y es
   deliberado) sale por `Safepoint`, no por `Dispatch`.

### 3.6 Convención de nombres

Se añaden tres afijos a la tabla de `docs/Opcodes.md` §1:

| Afijo | Significado |
|---|---|
| sufijo `LL` | Ambos operandos se leen de slots del frame, no de la pila |
| sufijo `LI` | Operando izquierdo de un slot, derecho inmediato |
| sufijo `Next` | Paso de bucle: incrementa, comprueba y salta atrás; la caída es la salida del bucle |

---

## 4. Puntos de extensión en el código

Todo lo que hay que tocar para abrir el espacio, antes de añadir un solo opcode útil:

| Fichero | Cambio |
|---|---|
| `src/Surtr.Core/Bytecode/OpCode.cs` | `Ext = 0xFF` + enum nuevo `SurtrExtOpCode`, misma disciplina de valor explícito y bloque `///` de tres partes |
| `src/Surtr.Core/VM/SurtrVirtualMachine.cs` | `case OpCode.Ext:` con el switch anidado, situado tras la última familia primaria |
| `src/Surtr.Core/Bytecode/Emit/SurtrCodeEmitter.OpCodes.cs` | Un método por opcode extendido, literal, con su pop/push declarado |
| `src/Surtr.Core/Bytecode/Emit/SurtrCodeEmitter.Helpers.cs` | Los agrupadores que deciden fusionar o no (§6) |
| `src/Surtr.Core/Bytecode/Emit/SurtrBytecodeDisassembler.cs` | El `switch (op)` de `:852` necesita el caso del prefijo y su decodificador |
| `src/Surtr.Core/Bytecode/Image/SurtrModuleImage.cs` | `FormatVersion` → 13 |
| `src/Surtr.Tests/Bytecode/OpCodeValueTests.cs` | Fijar la tabla del espacio extendido igual que la primaria |
| `docs/Opcodes.md` | Sección nueva para el espacio prefijado, con los afijos de §3.6 |

---

## 5. Catálogo propuesto

Todos los cuerpos son inline, sin llamadas auxiliares, en el estilo de `IncLocal` y `ArrGet`. Los
esbozos de abajo asumen que `ip` apunta al **primer inmediato** (el prefijo y el `sub` ya
consumidos).

### Grupo A — Superinstrucciones de bucle

**Es donde está el dinero.** El ciclo de un `for-in` indexado hoy
(`MethodBodyEmitter.EmitForInIndexed`, :775) cuesta **10 despachos de sobrecoste por elemento**:

```
top:   Ldl idx · Ldl src · ArrLen · JPGE end      (guard)
       Ldl src · Ldl idx · ArrGet · Stl var        (lectura)
       [cuerpo]
step:  IncLocal idx · Jump top                     (paso)
end:
```

Una sola instrucción al fondo los colapsa a **1**, con el bucle rotado al estilo FORPREP/FORLOOP de
Lua. En los recorridos cuyo índice es un temporal del compilador —array, string, tupla,
diccionario— la rotación es completa: el índice se inicializa a −1, un `Jump` de entrada salta al
fondo, y de ahí en adelante cada iteración es una única instrucción. El recorrido de rango rota
solo el paso, por la razón que explica la nota de contrato correspondiente.

| Opcode | Encoding | Stack | Reemplaza |
|---|---|---|---|
| `ArrForNext` | `0xFF sub src(1) idx(1) var(1) off(2)` — 7 B | `... -> ...` | 10 → 1 |
| `ArrForNextX` | `... off(4)` — 9 B | `... -> ...` | gemelo de relajación |
| `StrForNext` / `X` | idem | `... -> ...` | 10 → 1 |
| `TupForNext` / `X` | idem | `... -> ...` | 10 → 1 |
| `DictForNext` / `X` | `keys(1) idx(1) dict(1) pair(1) off(2)` — 8 B | `... -> ...` | **17 → 1** (§ más abajo) |
| `ForRangeNextLE` / `X` | `var(1) limit(1) off(2)` — 6 B | `... -> ...` | 5 → 1, límite inclusivo |
| `ForRangeNextLT` / `X` | idem | `... -> ...` | 5 → 1, límite exclusivo |

```csharp
case SurtrExtOpCode.ArrForNext:
{
    SurtrRawValue* slot = frameBase + ip[1];
    int index = (int)*slot + 1;
    var array = (SurtrArray)entities[(SurtrRef)frameBase[ip[0]]]!;

    if ((uint)index >= (uint)array.Count)
    {
        ip += 5;                       // se agotó: cae a la salida del bucle
        goto Dispatch;
    }

    *slot = SurtrValue.TagMaskInt | (uint)index;
    frameBase[ip[2]] = array.Items[index];
    ip += 5 + (short)(ip[3] | (ip[4] << 8));
    goto Branched;
}
```

Notas de contrato, todas verificables en la emisión:

- **`Count` se recarga cada iteración**, igual que hoy: el cuerpo puede hacer `push`, y la
  semántica actual es que el recorrido lo ve. `Items` puede ser null mientras el array nunca haya
  tenido elementos (`SurtrArray.cs:50`), pero entonces `Count` es 0 y el guard sale antes de
  tocarlo — no hace falta un test extra.
- **El trap de rango desaparece porque el rango deja de poder fallar**, no porque se suprima una
  comprobación. Ésa es la diferencia con P5 y por lo que la política de validación de
  `docs/VM-Plan.md` §1.9 queda intacta.
- **La restricción es sobre el ancho del elemento leído, no sobre la variable.** `ArrForNext`,
  `StrForNext` y `TupForNext` escriben **un** slot, así que solo se emiten cuando la variable del
  bucle ocupa uno; con un tipo multi-slot el lowering actual llama a `UnpackIfMultiSlot` y se sigue
  emitiendo la secuencia clásica. `DictForNext` es la excepción y escribe **dos** slots
  contiguos — su variable es siempre un par `(K, V)`.
- El destino de `continue` pasa a ser la propia instrucción `*ForNext`; `PushLoop(step, end)` se
  monta con `step` en esa etiqueta.
- `ArrForNext` inicializa el índice a −1 y entra por un `Jump` al fondo. Es seguro porque el índice
  es un temporal del compilador que siempre empieza en 0. **La familia `ForRangeNext*` no puede
  hacerlo** — su variable es visible para el usuario y `start - 1` podría desbordar — así que
  conserva el guard de cabecera para la primera iteración y solo rota el paso. Cuesta 5 bytes de
  código duplicado una vez por bucle.
- **Dos variantes de rango en vez de una, y esto es una corrección deliberada.** La tentación es
  normalizar el límite a inclusivo en el prólogo y tener un solo opcode. No sale gratis: en la rama
  con rango inline el límite puede ser una expresión cualquiera, y `limit - 1` desborda si vale
  `int.MinValue` — que es exactamente el caso especial que la rama escapada ya trata a mano
  (`MethodBodyEmitter.cs:735`). Como el emisor ya sabe estáticamente cuál es
  (`limitIsInclusive`, `MethodBodyEmitter.cs:692`), dos opcodes en un espacio abundante eliminan
  el problema entero en vez de trasladarlo al prólogo.
- **Qué se escribe en la salida.** `ArrForNext` **no** actualiza el slot del índice en el camino
  agotado: es un temporal del compilador que nadie lee después, así que se ahorra el store. La
  familia `ForRangeNext*` sí escribe la variable **incondicionalmente** antes del test, porque es
  lo que hace hoy `IncLocal` + guard y hay que conservar el valor observable.
- La semántica de desbordamiento se conserva **verbatim**, incluido el envolvimiento en
  `int.MaxValue`, que hoy se comporta igual (`IncLocal` + test).

**`DictForNext` merece su propio párrafo, porque el recuento cambia el orden de prioridades.** El
ciclo del `for-in` sobre diccionario (`EmitForInDictionary`, :855) no cuesta 10 despachos sino
**17**: cuatro para el guard, cuatro para leer la clave del snapshot, cuatro para el `DictGet` del
valor, tres para montar el par en el bloque de la variable, y dos para el paso. Es **el bucle más
caro de los tres** y el que más gana. Su cuerpo es también el más largo: lee la clave del snapshot,
resuelve el valor —y ahí es donde el fusionado de P3 aterriza de forma natural, eligiendo entre
`IntEntries` y `Entries` una sola vez— y escribe los dos slots del par. Conserva el trap por clave
ausente, que sigue siendo alcanzable: el cuerpo del bucle puede borrar del diccionario una clave que
el snapshot todavía lista. Ese camino publica `ip`/`sp` como cualquier otro trap.

### Grupo B — Aritmética con operandos en slots (4 → 1)

`Ldl a · Ldl b · Add · Stl c` es el patrón más frecuente del cuerpo de cualquier bucle. Fusionarlo
pone el banco de slots a trabajar como registros **sin** convertir el formato a máquina de
registros, que es justo lo que §4.3 del informe descarta con razón.

| Opcode | Encoding | Cuerpo |
|---|---|---|
| `AddLL` / `SubLL` / `MulLL` | `dst(1) a(1) b(1)` — 5 B | entero, sin tocar la pila |
| `FAddLL` / `FSubLL` / `FMulLL` | idem | flotante |
| `AddLI` / `MulLI` | `dst(1) a(1) imm8(1)` — 5 B | operando derecho inmediato |

```csharp
case SurtrExtOpCode.AddLL:
    frameBase[ip[0]] = SurtrValue.TagMaskInt
        | (uint)((int)frameBase[ip[1]] + (int)frameBase[ip[2]]);
    ip += 3;
    goto Dispatch;
```

**No** se propone una forma genérica `BinOp op, dst, a, b`: el `switch` sobre `op` sería un tercer
salto indirecto y se comería el ahorro. La división y el módulo quedan fuera del grupo porque
trapean, y un cuerpo con trap necesita publicar `ip`/`sp` — no imposible, pero sin beneficio claro
frente a la forma actual.

### Grupo C — Comparación y salto con operandos en slots (3 → 1)

Complementa la familia de compare-and-branch ya existente (`JPEQ..JPFLEX`) allí donde ambos
operandos son locales, que es el caso de casi toda guarda y toda condición de bucle.

| Opcode | Encoding |
|---|---|
| `JPLTLL` / `JPLELL` / `JPEQLL` / `JPNELL` (+ `X`) | `a(1) b(1) off(2)` — 6 B |
| `JPLTLI` / `JPLELI` / `JPEQLI` / `JPNELI` (+ `X`) | `a(1) imm8(1) off(2)` — 6 B |

```csharp
case SurtrExtOpCode.JPLTLL:
    if ((int)frameBase[ip[0]] < (int)frameBase[ip[1]])
    {
        ip += 4 + (short)(ip[2] | (ip[3] << 8));
        goto Branched;
    }
    ip += 4;
    goto Dispatch;
```

### Grupo D — Micro-especializaciones fusionadas

Aquí es donde P2 y P3 **sí** pagan. En vez de gastar valores primarios en ahorrar un `isinst`, se
fusiona la carga del receptor con el acceso y con la especialización: la instrucción ahorra
despachos *y* tests, y la regla de §3.2 se cumple por el primer término.

| Opcode | Encoding | Reemplaza | Ahorra |
|---|---|---|---|
| `NativeFieldGetL` | `obj(1) field(2)` — 5 B | `Ldl obj · FieldGet f` | 1 despacho + `isinst` |
| `NativeFieldSetL` | `obj(1) field(2)` — 5 B | `Ldl obj · … · FieldSet f` | 1 despacho + `isinst` |
| `ArrGetLL` | `arr(1) idx(1)` — 4 B | `Ldl a · Ldl i · ArrGet` | 2 despachos |
| `ArrSetLL` | `arr(1) idx(1)` — 4 B | `Ldl a · Ldl i · … · ArrSet` | 2 despachos |
| `DictGetILL` | `dict(1) key(1)` — 4 B | `Ldl d · Ldl k · DictGet` | 2 despachos + test de tag |
| `DictSetILL` | `dict(1) key(1)` — 4 B | `Ldl d · Ldl k · … · DictSet` | 2 despachos + test de tag |

`NativeFieldGetL` parecía estar en el límite de la regla (ahorra 1 despacho + el `isinst`, ~1.3 ns,
frente a un coste de prefijo estimado en ~1 ns) y entró condicionada a la Fase 0. **La condición se
cumplió con holgura**: el prefijo mide 0.46 ns, así que el par gana ~0.85 ns por acceso y entra sin
reservas.

`DictGetILL` **conserva** el test de `IntEntries != null`, obligatorio mientras `Deoptimize()`
exista (§1). Como ya ahorra dos despachos, sigue ganando con holgura; lo que no puede es
presentarse como «el opcode que elimina el test de null».

### Grupo E — Bloques de slots

`LdlRange base(1) count(1)` — 4 B — copia un run de `count` slots del frame al tope de pila en una
instrucción. Cubre los argumentos de llamada cuando todos son locales (N → 1) y las copias de
value class que hoy no encajan en `LoadValueLocal`. **Requiere comprobación previa** de qué solapa
exactamente con `LoadValueLocal` (`OpCode.cs:466`), que ya mueve un bloque multi-slot de un local
al tope: si la diferencia es solo que aquél nombra un rango declarado y éste un rango arbitrario,
puede bastar con relajar el existente y no hace falta opcode nuevo.

### Lo que deliberadamente no entra

Por no cumplir la regla de §3.2, y anotado aquí para no volver a proponerlo:

| Idea | Ahorro neto |
|---|---|
| `LdlLdl a, b` (empujar dos locales) | 2 → 1, neto **0** |
| `ThisFieldGet field` (receptor implícito en el slot 0) | 2 → 1, neto **0** |
| `RetLocal slot` | 2 → 1, neto **0** |
| `NativeFieldGet` / `DictGetI` sueltas (P2/P3 tal cual) | 0 despachos, neto **negativo** |

---

## 6. La ventana de fusión en el emisor

Los grupos A y E los emite `MethodBodyEmitter` directamente, en los lowerings que ya posee: sabe
que está montando un bucle y sabe qué slots participan.

Los grupos B, C y D necesitan reconocer un patrón que abarca varias instrucciones ya emitidas. La
propuesta P6 del informe lo resolvía con un pase sobre el buffer de bytes; el problema es que ese
pase tiene que **reconstruir** las fronteras de etiqueta y los rangos de región protegida, que son
exactamente lo que el emisor ya conoce y el buffer ya no.

La forma correcta es una **ventana de emisión diferida** dentro de `SurtrCodeEmitter`:

- Un buffer de 1-3 instrucciones pendientes, con sus operandos aún en forma estructurada.
- **Volcado forzoso** en `MarkLabel`, en `MarkHandler`, ante cualquier salto, ante los límites de
  región protegida y al cerrar el método. Fuera de esos puntos, ninguna instrucción puede tener un
  destino de salto que caiga en su interior, que es la única condición que la fusión necesita.
- El seguimiento de pila (`MaxStackSize`, la comprobación de acuerdo de profundidad en etiquetas)
  se hace sobre la instrucción **fusionada**, no sobre las originales — la mayoría del grupo B y D
  tiene efecto de pila 0, que es parte del beneficio.
- La relajación de saltos corre **después**, sin cambios: opera sobre instrucciones ya fusionadas y
  sus gemelos `X` (§3.5 regla 3).

Esto es P6 con el mismo destino y sin el riesgo de etiquetas, y de paso es la infraestructura sobre
la que aterrizarían futuros plegados entre instrucciones (propagación de constantes y de copias
tras el inlining), que es la otra mitad de lo que P6 quería.

---

## 7. Riesgos

1. **Presión de registros — el riesgo real de este trabajo.** `Run()` son 4474 líneas con `ip`,
   `sp`, `frameBase`, `constants`, `typeTable`, `fieldTable`, `methodTable`, `moduleTable`,
   `entities`, `current`, `steps`, `chunk` y `closure` compitiendo por registros. Añadir 20-40
   cuerpos nuevos **en el mismo método** puede empujar al asignador a derramar en caminos que hoy
   no derraman, degradando **todo** el intérprete y no solo lo nuevo. Es la razón por la que cada
   fase se mide contra la **suite completa** y no contra su caso objetivo, y por la que las fases
   son incrementales en vez de un aterrizaje único.
2. **El impuesto del prefijo puede ser mayor de lo estimado.** Toda la aritmética de §3.2 descansa
   en «un salto indirecto anidado ≈ un despacho». La Fase 0 lo mide antes de que nada dependa de
   ello, y el catálogo se recorta desde abajo si sale caro (primero grupo D, luego B).
3. **Superficie de emisión.** Cada opcode fusionado es un sitio donde el emisor puede equivocarse
   de slot o de ancho. Mitigación: los tests dorados de bytecode y el desensamblador, más la
   verificación por checksum de `src/Surtr.Bench` — que ya demostró atrapar una miscompilación que
   ningún test unitario vio (`docs/VM-Plan.md` §3.6).
4. **Bucles con `break`/`continue`/`return` y con regiones protegidas.** La rotación del grupo A
   cambia dónde caen las etiquetas `step` y `end`, así que un `for-in` dentro de un `try` necesita
   que los límites de la región protegida sigan cayendo donde deben. Lo que **no** entra en juego
   es el cierre de `IDisposable`: los tres lowerings que el grupo A toca —indexado, rango y
   diccionario— no abren región protegida ninguna; el cierre vive en `EmitForInGenerator` y en la
   ruta de contrato (`docs/Plan-Disposicion.md`), y el grupo A no las toca. Eso abarata la Fase 2
   respecto a lo que parecía.

---

## 8. Protocolo de medición

Metodología de `benchmark_report.md`: Release, 3 rondas barajadas, verificación por checksum,
mediana, y tamaños suficientes para mantener el spread bajo el 10 % (la propia suite advierte de
nueve casos con dispersión >10 %, todos con medianas bajo ~4 ms; un A/B nuevo necesita **tamaños
mayores, no más iteraciones**).

**Fase 5 — las tres propuestas restantes cerradas, dos de ellas con datos que no existían.**

Bajo §11 ninguna de las tres podía construirse tal como estaba planteada: las tres añaden cuerpos a
`Run()` o estado caliente a su marco. Así que la fase se dedicó a lo único que quedaba por hacer
honestamente — **contestar las preguntas que estaban abiertas** — y a cerrarlas con la respuesta.

**P7 (llamada cruzada de módulo): medida por primera vez, y cerrada.** El informe la estimaba en
1-3 ns por llamada y la suite no tenía ningún caso que la ejerciera, así que la estimación llevaba
un año sin poder confirmarse. Se añadieron dos casos gemelos: `crossModule` llama a una función de
otro módulo (`CallModule`: tabla de módulos, luego la tabla de métodos de *ese* módulo) y
`localModule` llama a **el mismo cuerpo, byte por byte**, dentro del propio módulo
(`CallLocalModule`: una tabla). El callee es deliberadamente lo bastante grande para que el inliner
lo deje en paz, o ambos casos medirían un `add` inlineado y nada más.

| | corridas | mediana |
|---|---|---|
| `localModule` | 8.095 · 7.971 · 8.179 | **8.095** |
| `crossModule` | 8.438 · 8.482 · 8.469 | **8.469** |

**1.25 ns por llamada**, un 4.6 % sobre un bucle que no hace otra cosa que llamar. La estimación del
informe era correcta. Y ese es el **techo absoluto** de lo que P7 podría recuperar, en el caso más
favorable imaginable; en un programa donde las llamadas cruzadas son una fracción del trabajo queda
muy por debajo del 1 %.

Cerrada, porque cualquiera de sus formas cuesta más de lo que devuelve: la versión con opcode añade
un cuerpo, la versión con tabla plana añade un local caliente al marco de `Run()` — que es
exactamente lo que la Fase 4 midió como contraproducente — y la versión con caché en `SurtrModule`
introduce un campo que hay que mantener en sincronía con uno mutable. Los dos casos se quedan en el
catálogo: la próxima vez que alguien quiera abrir esta pregunta, ya tiene el número.

**P8 (partir el switch caliente/frío): cerrada bajo §11, y con el objetivo cambiado.** El plan la
tenía como experimento de bajo coste y baja expectativa. Ya no es eso. La Fase 4 midió que la
posición de `Run()` domina el rendimiento del intérprete, así que partir el `switch` deja de ser
"quizá ahorra algo de I-cache" y pasa a ser **la única palanca conocida sobre el techo** — y también
un experimento mucho más arriesgado, porque mueve exactamente aquello cuyo movimiento resultó valer
±45 %. Se cierra sin construir, con el objetivo reescrito en §11.5: no reducir la tabla de saltos,
sino aislar los cuerpos fríos para que el asignador de registros deje de tratarlos como una sola
región.

**Grupo E (`LdlRange`): cerrado sin construir.** Un cuerpo más, para una fusión de la clase que la
Fase 4 midió en negativo. La comprobación de solape con `LoadValueLocal` que el plan pedía tampoco
llegó a hacer falta.

**Fase 4 — los dos grupos construidos, los dos revertidos, y el techo encontrado.**

**Grupo B (aritmética con operandos en slots).** Entró como sondeo de dos opcodes, porque la Fase 3
había tumbado una forma parecida. **El sondeo salió bien**: `AddLL`/`AddLI` sobre `tightGuard`, a
escala 5 y tres corridas por lado, dieron 29.114 · 29.366 · 29.066 contra 26.659 · 26.481 · 27.584 —
**−8.4 %**, distribuciones que no se solapan y control de C# idéntico. La explicación encajaba con lo
aprendido en la Fase 3: lo que esto quita y el grupo C no quitaba es el **viaje de ida y vuelta por
la pila de datos**, que es tráfico de memoria y no despacho. Así que se completó a nueve opcodes
(`Add`/`Sub`/`Mul` × slot-slot y slot-constante, más las tres formas flotantes).

**Grupo D (accesos fusionados).** Seis opcodes — `FieldGetL`, `FieldSetL`, `ArrGetLL`, `ArrSetLL`,
`DictGetLL`, `DictSetLL` — que quitan una búsqueda en el registro de entidades y el tráfico de pila
además de los despachos, que es la clase de trabajo que el grupo A demostró que sí cuenta. Se
emiten correctamente (test dorado sobre las tres formas) y **empeoraron sus propios objetivos**:
`arrayIndex` +10 %, `fieldAccess` +6 %, `dictOps` +8 %, medido a escala 5 con tres corridas por lado.

**Y entonces la medición se cayó.** Al revertir el grupo D para quedarse con el B, `arrayIndex` daba
21.6 en una corrida y 31.2 en la siguiente **con el mismo binario**. Un 44 % de oscilación bimodal
invalida cualquier conclusión de ±10 %, incluida la que acababa de tomar sobre el grupo D. Así que
la pregunta hubo que hacerla al revés: en vez de "¿cuánto gana este opcode?", **"¿qué le pasa al
intérprete entero cuando se le añaden cuerpos?"**. Suite completa, grupo B solo, contra la Fase 3:

| | |
|---|---|
| mediana | **+3.9 %** |
| control de C# | +0.0 % |
| casos que empeoran >5 % | **20 de 48** |
| peores | `arrayIndex` +45 %, `valueClass` +38 %, `stringOps` +38 %, `methodCalls` +36 %, `floatLoop` +35 % |
| `tightGuard` (su objetivo) | −10.5 % |

Nueve cuerpos aritméticos de cinco líneas cada uno hacen al intérprete un 3.9 % más lento de
mediana. **Revertido también.** La Fase 4 no deja código.

**Lo que sí deja, y es el hallazgo que cierra el plan.** Esas veinte regresiones son casi
exactamente el reverso de las ganancias que la Fase 2 había anotado como "layout, no la fusión":
`valueClass` −30 % → +38 %, `floatLoop` −25 % → +35 %, `methodCalls` −25 % → +36 %, `arrayIndex`
−32 % → +45 %, `intLoop` −21 % → +23 %. La suerte de la Fase 2 no era un regalo: era la posición de
`Run()` en un espacio donde añadir cuerpos la mueve, y añadir nueve más la movió de vuelta.

Ver §11.

**Fase 3 — construida, medida y revertida. El resultado negativo es lo valioso.**

Se implementaron 24 opcodes (`JPEQLL`…`JPLELIX`: seis comparaciones × dos formas de operando ×
dos anchos de offset), con su reconocimiento en el emisor y sus tests. Funcionaban: los tests
dorados confirman que `for (var i = 0; i < n; i += 1)` emite `JPGELL` y que `i < 10` emite
`JPGELI`, y que una variable capturada, un flotante o una constante mayor que un byte caen a la
forma escrita.

**Y no sirvieron para nada.** Medido contra un worktree en el commit de la Fase 2:

| Caso | Fase 2 | Fase 3 | |
|---|---|---|---|
| `intLoop` | 7.983 | 7.978 | idéntico |
| `fib` | 2.765 | 2.763 | idéntico |
| `floatLoop` | 6.174 | 6.155 | idéntico |
| `switchDense` | 5.952 | 5.943 | idéntico |
| suite completa (47 casos) | — | — | mediana **+0.3 %**, control de C# +0.0 % |

La sospecha inmediata era que ningún caso del catálogo estuviera dominado por el guard: `intLoop`
lleva un `%` en el cuerpo, y una división entera de ~30 ciclos esconde detrás casi cualquier cosa.
Así que se añadió `tightGuard` — un bucle contado cuyo cuerpo es un solo `store` de un valor
fresco, sin cadena de dependencia entre iteraciones y sin nada que solape el guard. Tres corridas
por lado:

| | corridas | mediana |
|---|---|---|
| Fase 2 (sin grupo C) | 5.705 · 5.687 · 5.675 | **5.687** |
| Fase 3 (con grupo C) | 5.375 · 5.753 · 5.846 | **5.753** |

Ni siquiera ahí. **Revertido.**

**Lo que esto corrige del modelo de coste, y es la parte que importa.** La regla de §3.2 medía todo
en "despachos ahorrados" tratándolos como intercambiables a ~1 ns. Eso es falso, y la Fase 2 y la
Fase 3 juntas dicen por qué: **no todos los despachos cuestan lo mismo**.

- Un `Ldl` es una carga y un almacenamiento sin dependencias, y su rama indirecta es la mejor
  predicha del intérprete porque es el opcode más frecuente que hay. Quitar dos de ellos de un
  bucle quita trabajo que el motor fuera de orden **ya estaba solapando** con lo que sí serializa.
- Lo que pagó en el grupo A no fue el número de despachos sino **qué** llevaban dentro: una
  comprobación de rango, una búsqueda en el registro de entidades, una dependencia de memoria, y
  el colapso de una estructura de bucle de dos ramas en una.

La regla queda así:

> Un opcode extendido paga cuando elimina **trabajo que serializa** — una rama que decide el flujo,
> una dependencia de memoria, una búsqueda, una comprobación — no cuando elimina instrucciones
> baratas e independientes, por muchas que sean.

Eso reordena lo que queda: el **grupo B** (`AddLL` y compañía) tiene exactamente la forma que acaba
de fallar — cargas baratas de slots fusionadas — así que entra en la Fase 4 **solo como sondeo de
un opcode**, no como grupo completo. El **grupo D** es de la otra clase: elimina una búsqueda de
entidad y un test de tipo además de los despachos, que es lo que el grupo A demostró que sí cuenta.

**La ventana de fusión (§6, P6 reformulado) no llegó a hacer falta y no se construyó.** El diseño
suponía que fusionar el grupo C exigía reconocer un patrón repartido en varias instrucciones ya
emitidas. Es falso: `EmitConditionalJump` tiene los **operandos bindeados en la mano** antes de
emitir nada, así que la pregunta "¿son los dos lados slots?" tiene respuesta directa, sin reescribir
bytes y sin tener que demostrar que ninguna etiqueta cae en medio. Un pase posterior habría tenido
que redescubrir información que ese lado ya tiene. Si algún día hace falta fusionar algo que el
emisor genuinamente no ve como una unidad, la ventana sigue siendo el diseño correcto; nada de lo
propuesto hasta ahora lo es.

`tightGuard` se queda en el catálogo del bench, igual que `sortBytecode`: es el instrumento con el
que se cerró esta pregunta y con el que habrá que volver a abrirla en otro backend.

**Fase 2 — grupo A completo, y el resultado tiene dos mitades que hay que separar.**

Doce opcodes (`ArrForNext`, `StrForNext`, `TupForNext`, `DictForNext`, `ForRangeNextLE/LT`, más sus
seis gemelos `X`), la relajación de saltos generalizada al espacio extendido, y los tres lowerings
de bucle reescritos. La medición es un A/B contra un worktree en el commit de la Fase 1, mismos
flags, misma semilla, con la columna de C# como control.

**La mitad atribuible.** Subconjunto de cuatro casos, flags idénticos:

| Caso | Fase 1 | Grupo A | Δ |
|---|---|---|---|
| `forIn` (recorrido de array) | 0.990 | **0.521** | **−47.4 %** |
| `forInDict` (recorrido de diccionario) | 2.486 | **1.997** | **−19.7 %** |
| `iterator` (control: ruta de contrato, sin tocar) | 2.166 | 2.086 | −3.7 % |
| `handIterator` (control: cursor a mano) | 1.712 | 1.802 | +5.3 % |

Ambos criterios de §8 cumplidos: ≥15 % en `forIn` (47 %) y ≥20 % en el diccionario (19.7 %, justo
en la raya). `forInDict` no existía en el catálogo y se añadió con este trabajo, porque afirmar
17 → 1 sin medirlo no era afirmar nada.

**La mitad no atribuible, y es la más interesante.** La suite completa (46 casos) da una mediana de
**−5.0 %** con 29 casos mejorando más de un 3 % y 9 empeorando — y la mediana de la columna de C#
es **−0.1 %**, así que la máquina estuvo estable y el movimiento es real. Pero buena parte cae en
casos que la fusión **no toca**: `valueClass` −30 %, `floatLoop` −25 %, `methodCalls` −25 %,
`arrayIndex` −32 %, `intLoop` −21 %, ninguno de los cuales usa `for-in`. Y en el otro sentido,
`handIterator` +15 %, `sortArray` +6 %, `switchDense` +5 %.

Eso es **layout de código en `Run()`**, no la fusión: añadir trece cuerpos al `switch` cambió cómo
el JIT dispone el método y los opcodes calientes cayeron mejor. Es reproducible (dos semillas
distintas, mismos deltas) pero es suerte, no ingeniería, y hay que decirlo así: el criterio de
`intLoop` ≥8 % de §8 aparece cumplido con creces y **no cuenta**, porque `intLoop` es un `for`
clásico que ninguna instrucción nueva alcanza.

Es también el riesgo §7.1 materializándose con el signo contrario. Que esta vez saliera a favor no
lo convierte en un beneficio con el que contar: la próxima fase que añada cuerpos puede moverlo al
otro lado, y por eso la comprobación de suite completa se repite en cada una.

**Fase 1 — P1 aterrizado, P9 cerrado en negativo.**

**P1 (plegado constante).** `ConstantOf` existía y solo lo preguntaban dos sitios; ahora
`EmitBinary` y `EmitUnary` lo preguntan primero y emiten el literal. Lo delicado no era plegar sino
**plegar lo mismo que habría contestado la instrucción**, y ahí el evaluador tenía dos divergencias
reales, ninguna visible mientras sus únicos consumidores fueran claves de `switch` y argumentos de
`const fun`:

- Plegaba en `long` mientras la máquina calcula en `int` con envoltura, así que
  `2000000000 + 2000000000` contestaba 4·10⁹ donde la instrucción contesta −294967296. Todas las
  operaciones enteras se pliegan ahora en `int`, envolviendo en cada paso.
- Plegaba `int.MinValue / -1`, que el intérprete **trapea** explícitamente. Se refusa, igual que ya
  se refusaba la división por cero: un plegado no puede tragarse una falla que el programa tiene
  derecho a observar.

`src/Surtr.Tests/Compiler/CodeGen/ConstantFoldingTests.cs` fija las dos, comparando cada plegado
contra la misma aritmética alcanzada por variables — que no puede plegarse — en vez de contra un
número escrito a mano.

**P9 (sort en Surtr): medido, y la hipótesis del informe es falsa.** El mismo merge sort estable,
escrito en Surtr y compilado a bytecode, contra el nativo, sobre los mismos datos y el mismo
comparador (`surtrbench --workload sort`, ambos verificados por checksum):

| Caso | ms | vs C# |
|---|---|---|
| `sortArray` (nativo, reentra por comparación) | **9.47** | 10.8x |
| `sortBytecode` (Surtr, sin frontera) | **14.62** | 16.8x |

Un **54 % más lento**. El razonamiento del informe —que las fronteras dominan— no se sostiene: el
merge que el nativo obtiene gratis de C# cuesta más en bytecode que lo que cuesta la reentrada en
la frontera. El caso `sortBytecode` se queda en el catálogo del bench como registro permanente del
A/B, igual que se cerró la caché virtual.

**Lo que sí encontró la medición.** La columna `bytes` delataba que `ArraySort` asignaba un
`SurtrValue[]` gestionado **por llamada** — 156.4 KB para ordenar 20k elementos, en un motor con
presupuesto de frame. El scratch se alquila ahora de `SurtrValueBufferPool` y el par de operandos
del comparador es un `stackalloc`, así que **un sort no asigna nada**: 156.4 KB → 56 B, con el
tiempo sin cambio (9.80 ms contra 9.47, dentro de un spread del 7 %). Alquilar es seguro pese a
que el buffer no es raíz de recolección, porque todo valor del scratch es copia de uno que sigue
vivo en `SurtrArray.Items`, y el receptor está en la pila de datos durante toda la llamada.

**Fase 0 — el experimento nulo. Hecho; resultados en §3.2 y aquí.** Dos funciones emitidas a mano,
idénticas salvo que una carga sus locales con `LdlS` y la otra con `SurtrExtOpCode.Probe`, que hace
lo mismo a través del prefijo. Vive en `src/Surtr.Bench/PrefixTax.cs` y se invoca con
`surtrbench --prefix-tax`. **Resultado: 0.44–0.48 ns por despacho prefijado.**

La segunda pregunta de la fase —si abrir el `switch` anidado degrada el intérprete por presión de
registros— es distinta de la primera y se respondió por separado, con un A/B contra un worktree en
el commit anterior, mismo comando y mismas semillas:

| Caso | HEAD | con `Ext` | Δ |
|---|---|---|---|
| `fib` | 2.763 | 2.751 | −0.4 % |
| `intLoop` | 10.781 | 10.345 | −4.0 % |
| `floatLoop` | 8.605 | 8.460 | −1.7 % |
| `arrayIndex` | 6.371 | 6.486 | +1.8 % |
| `methodCalls` | 4.400 | 4.362 | −0.9 % |
| `virtualCalls` | 6.320 | 6.275 | −0.7 % |
| `fieldAccess` | 5.889 | 5.895 | +0.1 % |
| `forIn` | 0.996 | 0.991 | −0.5 % |
| `switchDense` | 5.455 | 5.571 | +2.1 % |
| `enums` | 7.498 | 7.245 | −3.4 % |

Todo dentro del ruido y **sin dirección sistemática** (cinco arriba, cinco abajo, spreads de 3-9 %):
añadir el caso del prefijo al `switch` no degrada nada. El riesgo §7.1 sigue vivo para las fases que
añaden cuerpos de verdad — esta solo añadió uno — así que la comprobación se repite en cada fase.

Criterios de éxito por grupo:

| Grupo | Casos | Criterio |
|---|---|---|
| A | `forIn`, `arrayIndex`, `intLoop`, `floatLoop`, `arrayFill`, `sortArray`, `dictMembers` | ≥15 % en `forIn`/`arrayIndex`, ≥8 % en `intLoop`, ≥20 % en el `for-in` sobre diccionario |
| B | `intLoop`, `floatLoop`, `fib`, `vec2Math` | ≥5 % en `intLoop` |
| C | `fib`, `intLoop`, `sortArray` | ≥5 % en `fib` |
| D | `fieldAccess`, `enums`, `dictOps`, `dictMembers`, `arrayIndex` | ≥3 % en `fieldAccess`, ≥5 % en `dictOps` |
| Cualquiera | **suite completa** | Sin regresión separable en ningún caso ajeno |

Casos nuevos que hacen falta y hoy no existen: un bucle con límites derivados de constantes (para
P1), y un `for-in` sobre array lo bastante largo para que el spread baje del 10 % (para el grupo A).

---

## 9. Plan por fases

| Fase | Contenido | Depende de |
|---|---|---|
| **0** ✅ | Abrir el prefijo (`OpCode.Ext`, `SurtrExtOpCode`, switch anidado, disassembler, `FormatVersion` → 13) + el experimento nulo de §8. **Hecho**: prefijo a 0.44-0.48 ns, sin degradación de la suite | — |
| **1** ✅ | **P1** plegado constante en `EmitBinary`/`EmitUnary` + **P9** sort en Surtr. **Hecho**: P1 aterrizado, P9 medido y cerrado en negativo | Nada; fue en paralelo con la 0 |
| **2** ✅ | **Grupo A** (superinstrucciones de bucle). Cierra P4 y P5. **Hecho**: `forIn` −47 %, `forInDict` −20 % | 0 |
| **3** ⛔ | **Ventana de fusión** (§6) + **grupo C**. **Construido, medido y revertido**: cero ganancia. La ventana resultó innecesaria | 0 |
| **4** ⛔ | **Grupos B y D**. **Construidos, medidos y revertidos**, y el porqué cierra el plan entero (§11) | 3 |
| **5** ✅ | **P7 medido y cerrado** con su número (1.25 ns/llamada), **P8** y **grupo E** cerrados bajo §11. Deja dos casos de bench nuevos y ningún opcode | 4 |

Cada fase entra con su A/B según §8, `OpCodeValueTests.cs` actualizado y la sección
correspondiente de `docs/Opcodes.md` escrita en el mismo commit.

**Criterio de abandono — no se activó.** Estaba escrito así: si la Fase 0 midiera un impuesto de
prefijo por encima de ~1.5 ns, o si abrir el switch anidado degradara la suite de forma separable,
el plan se recortaría al grupo A emitido desde los **15 valores primarios**, donde cabe entero
(seis formas más sus seis gemelos `X` son doce valores). Midió 0.44-0.48 ns y no degradó nada, así
que el plan sigue completo y los quince valores primarios siguen reservados. El criterio se deja
escrito porque vuelve a aplicar en cada backend nuevo: en IL2CPP hay que volver a medirlo.

---

## 11. El techo: `Run()` no admite más cuerpos

Este es el resultado principal de todo el trabajo, y no estaba en el diseño.

`SurtrVirtualMachine.Run()` son ~4700 líneas de `switch` con trece valores calientes compitiendo
por registros. El riesgo estaba anotado en §7.1 como "podría derramar"; lo que las Fases 2, 3 y 4
midieron es más concreto y más duro:

> **Añadir cuerpos de opcode a `Run()` mueve el rendimiento de *todo* el intérprete en ±20-45 %
> por caso, en una dirección que no se puede predecir y que no tiene nada que ver con lo que el
> opcode nuevo hace.**

La evidencia son tres experimentos independientes:

| Fase | Cuerpos añadidos | Efecto en su objetivo | Efecto en la suite (mediana) |
|---|---|---|---|
| 2 (grupo A) | 13 | `forIn` **−47 %** | −5.0 % — favorable **por suerte** |
| 3 (grupo C) | 24 | ninguno | +0.3 % |
| 4 (grupo B) | 9 | `tightGuard` −10.5 % | **+3.9 %**, 20 casos peor |

El control de C# está plano (±0.1 %) en las tres, así que no es la máquina.

**Consecuencias, y son las reglas con las que hay que seguir:**

1. **El presupuesto de opcodes nuevos no es de 256 valores; es de un puñado de cuerpos.** El espacio
   extendido resuelve el problema equivocado: nunca faltaron valores, falta sitio en el método.
2. **Un opcode nuevo tiene que justificar el layout que desplaza, no solo su propio ahorro.** El
   grupo A lo hizo (−47 % en su objetivo, y aun así su beneficio de suite era prestado). El grupo B
   ganó un 10 % en su objetivo y costó un 3.9 % en todo lo demás: ese cambio no se hace.
3. **La única medida válida es la suite completa contra un worktree, con el control de C#.** Medir
   el caso objetivo en un subconjunto contesta una pregunta que ya no es la que importa.
4. **Lo que queda por optimizar no está en el juego de instrucciones.** Está en lo que no toca
   `Run()`: la emisión (P1, que aterrizó) o fuera del intérprete (la asignación del sort, que
   aterrizó). Todas las propuestas restantes del informe que añaden cuerpos — grupo E, lo que
   quedaba de P2/P3, y P7 en cualquiera de sus formas — quedan **cerradas por esta razón**, no por
   estar mal pensadas. P7 además tiene ahora su número: 1.25 ns por llamada cruzada (§9, Fase 5),
   que es el techo de lo que podría devolver.
5. **Si alguna vez hace falta romper el techo**, la vía no es añadir menos: es partir `Run()` de
   forma que el JIT deje de tratarlo como una única región de asignación. Eso es P8 con otro
   objetivo — no reducir la tabla de saltos, sino aislar los cuerpos fríos — y sigue siendo un
   experimento sin evidencia.

**Lo que sí queda en pie del trabajo**, medido de punta a punta contra el estado anterior al grupo A
(suite completa, control de C# a −0.1 %):

| | |
|---|---|
| mediana | **−5.7 %** |
| casos que mejoran >5 % | **25 de 46** |
| casos que empeoran >5 % | 3 |
| `forIn` | −47.6 % |
| `forInDict` | −19.7 % |

De ese −5.7 %, lo atribuible a ingeniería es el recorrido de `for-in`; el resto es la posición
afortunada en la que quedó `Run()`, que la Fase 4 demostró que se pierde en cuanto se le añade algo.
Vale la pena tenerlo, y no vale la pena construir sobre ello.

---

## 10. Deuda documental

Saldada en la Fase 0, toda en el mismo commit que la abrió:

- ✅ `CLAUDE.md`: «247 opcodes, `0x00`–`0xFC`, libres `0xFD`–`0xFF`» → 240, `0x00`–`0xEF`, libres
  `0xF0`–`0xFE` más el prefijo; `FormatVersion` 9 → 13; y este documento añadido al mapa.
- ✅ `docs/Opcodes.md`: el recuento y la versión al día, más §8, la sección del espacio prefijado.
- ✅ `docs/VM-Plan.md` §3.2: cerrada. La premisa sobre el buffer gestionado de arrays había dejado
  de ser cierta — `SurtrArray.Items` es hoy un puntero no gestionado (`SurtrArray.cs:50`) y
  `ArrGet` paga **una** comprobación, no dos — y lo que queda de la sección es una fusión del lado
  del compilador, que es el grupo A de §5.
- ✅ `docs/Informe-Optimizaciones-Bytecode.md`: anotado que P3 quedó recortada por `Deoptimize()` y
  que P4/P5 quedan supersedidas por el grupo A.
