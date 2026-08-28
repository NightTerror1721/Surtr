# Plan: bajar la presión de registros de `Run()`

**Fecha:** 2026-08-27
**Estado:** plan acordado, sin implementar.
**Objetivo:** reducir las locales de `SurtrVirtualMachine.Run()` a un conjunto que quepa en los
registros útiles de x64, y encoger el método, para que su asignación de registros sea buena de forma
estable en vez de por suerte.

Este documento está escrito para que una sesión que llegue en frío pueda ejecutarlo. §0 es el
porqué —incluyendo lo que ya se probó y se descartó, con números— y §3 el inventario del punto de
partida.

---

## 0. Por qué esto, y por qué no las alternativas

La pregunta de fondo es: **`Run()` está en un techo donde añadir cualquier cosa puede costar más de
lo que aporta.** Antes de aceptar este plan se evaluaron tres salidas. Dos están cerradas con
medición y una es esta.

### 0.1 Cerrada: quitar el NaN-tagging

Medido el 2026-08-27 con un worktree desechable en el que `Add`, `Sub`, `Mul`, `Div`, `Mod`, `Neg`,
`IncLocal`, las bitwise y las seis comparaciones enteras dejaron de aplicar tag (68 de los 153
sitios de tag del método). El experimento es semánticamente válido porque **todos los cuerpos
enteros leen con `(int)*sp`, que trunca a 32 bits e ignora el tag**, y los saltos booleanos leen los
bits bajos: las 50 cargas de la suite siguieron verificando su checksum.

| | con tag | sin tag int/bool |
|---|---|---|
| `Run()` | 42 763 B | 41 794 B (**−969**) |
| instrucciones | 8 678 | 8 535 (**−143**) |
| prólogo | `0xC18` | `0xC18` (sin cambio) |

**Resultado: −1.13 % de mediana** sobre las 50 cargas (−1.31 % excluyendo seis casos que el propio
hack distorsiona: un int sin etiqueta tiene los bits altos a cero, así que `IsFloat` devuelve
`true` y el comparer lo enruta por el camino de coma flotante — de ahí `dictMembers` +41 %,
`dictOps` +27 %, `forInDict` +23 %, que son artefacto y no diseño).

Por casos intensivos en enteros: `switchDense` −9.2 %, `typeTest` −4.5 %, `intLoop` −3.3 %,
`methodCalls` −2.2 %, `arrayIndex` −1.8 %, `enums`/`valueClass`/`fib`/`nullable` ≈ 0,
`tightGuard` +3.0 %.

**Extrapolación al rediseño completo:** se quitaron 68 de 153 sitios (44 %); los restantes son
referencia (37), máscara (15), `Absent` (7) y `char` (5). Añadiendo la eliminación de los *tests* de
tag, el techo optimista es **2-3 %**.

Contra eso: 240 cuerpos de opcode reescritos, **mapas de pila en el emisor con modo de fallo de
corrupción silenciosa del heap**, primitivos anulables que pasan de gratis a dos slots o boxing, y
bump de formato. La ganancia no lo paga, y sobre todo **no resuelve el techo**: cuerpos algo más
cortos, pero 240 opcodes en un método siguen siendo 240 opcodes en un método.

La causa de que la ganancia fuese tan pequeña es la misma que `docs/Informe-Volatilidad-Run.md`
lleva midiendo: **el cuello es la entrega de instrucciones del front-end, no la ALU.** Las dos
instrucciones por tag (`mov reg, 0xFFF1…` + `or`, porque la constante no cabe en un inmediato de 32
bits) son independientes de la cadena de load/store y un núcleo fuera de orden se las come.

> **No re-proponer sin evidencia nueva.** Si alguien vuelve a plantearlo, el número está aquí.

### 0.2 Cerrada: tabla de punteros a función en vez del `switch`

`docs/VM-Plan.md` §1.1 ya la rechaza por diseño: C# no puede convertir esa llamada en un salto de
cola, así que cada handler paga prólogo y epílogo **y** hay que derramar `ip`/`sp`/el estado de
frame a memoria y recargarlo dentro. C# tampoco puede expresar despacho replicado (el
*direct threading* clásico) porque `goto case` exige constante de compilación.

Aritmética de apoyo: el coste por instrucción del intérprete es de **~1.0-1.3 ns** (`intLoop`,
1 M de iteraciones en ~8.1 ms en estado rápido, ~6-8 dispatches por iteración). El prefijo `0xFF`
—que es solo una rama indirecta anidada, sin llamada— está medido en **0.44-0.48 ns**. Una llamada
real es estrictamente más. Sería del orden del 50-100 % de regresión.

**Pero la intuición detrás sí es válida** y aparece en la fase 2 de este plan: lo que se busca es
que añadir algo deje de perturbar todo lo demás. Eso se consigue **aislando familias frías detrás
de un cuerpo compartido con helper**, sin pagar una llamada por instrucción en las calientes.

### 0.3 Abierta: esto

La única intervención de esta familia que **ha ganado de verdad y está medida** es §10 de
`docs/Informe-Volatilidad-Run.md`: sacar `frames`, `roots`, `maxDepth` y `stackLimit` de las
locales bajó `Run()` de 46 409 a 42 763 bytes, quitó los derrames de `ip`+`frameBase` en
`IncLocal`, y midió **−4.6 % de mediana con 23 de 50 casos mejorando más de un 5 %**.

Este plan es continuar ese hilo con el resto de las locales.

---

## 1. El problema, dicho con precisión

`Run()` son ~4 700 líneas de `switch` en un método de **42 763 bytes** con **~22 locales
declaradas** (el informe cuenta 26 incluyendo temporales del compilador) compitiendo por los ~14
registros útiles de x64. `docs/Informe-Volatilidad-Run.md` §6 descompone el efecto en tres capas:

1. **Añadir cuerpos que tocan `entities`/`current`/`sp` cambia los pesos de liveness** → el
   asignador lineal y el layout de frame de RyuJIT se re-optimizan globalmente → cambian todas las
   direcciones del método. Determinista y real.
2. **El despacho y los cuerpos calientes se entregan por el op-cache**, indexado por dirección de
   fetch. El layout nuevo mueve esas direcciones → ±5-15 % por caso, dirección impredecible.
3. **ASLR re-tira la misma dirección en cada lanzamiento** → bimodalidad del 20-50 %.

**Este plan ataca la capa 1**, que es la única sobre la que se puede actuar desde C#. Las capas 2 y
3 son consecuencia; reducir el footprint de fetch del bucle caliente hace el estado rápido más
probable, pero no puede garantizarlo bajo ASLR (§8 del informe).

**Y hay una segunda razón, medida hoy, para hacerlo antes que cualquier otra cosa:** mientras la
presión siga así, cada opcode nuevo es una tirada de dados. Al escribir la familia aritmética de
`long` (rama `feature/i64-long`), seis cuerpos que solo añadían una referencia gestionada por cuerpo
ensancharon el frame de `0xC18` a `0xCF8` y movieron 5 969 de las 10 809 líneas de asm **empezando
por el prólogo**. El mismo trabajo detrás de un helper: +24 bytes y prólogo intacto. Con menos
presión, ese margen se ensancha para todo lo que venga después.

---

## 2. El patrón que funciona (§10, y por qué)

La forma del arreglo que ganó, para replicarla:

> Una local cuyo **rango de liveness abarca todo el método** pero cuyos **usos son pocos y fríos**
> cuesta un registro callee-saved al bucle de despacho durante toda la ejecución, a cambio de
> ahorrar una carga de campo en un puñado de sitios que se ejecutan una vez por llamada.

`frames`, `roots`, `maxDepth` y `stackLimit` cumplían eso: se leen en los caminos de llamada y de
generadores, una vez por llamada. Pasaron a leerse de los campos `_frames`/`_roots`/`_stackLimit`,
la carga extra no aparece en el perfil, y el bucle recuperó tres registros.

**El criterio, entonces:** `usos_calientes / amplitud_de_liveness`. Una local con muchos usos en
cuerpos calientes se queda; una con pocos usos fríos y liveness total se va al campo o se re-lee de
donde salió.

---

## 3. Punto de partida: inventario de `Run()`

Medido sobre `develop` en `567f5a6`. `Run()` = **42 763 bytes**, prólogo `sub rsp, 0xC18` (3 096 B),
**8 678 instrucciones**, 941 bloques.

Los conteos son ocurrencias del identificador en `SurtrVirtualMachine.cs` (incluyen la declaración y
la asignación en `LoadFrame`, así que réstense ~2 para los usos reales).

| Local | Tipo | Usos | Liveness | Notas |
|---|---|---|---|---|
| `entities` | `SurtrRuntimeEntity?[]` | **111** | todo | Recargada en cada sitio que puede moverla. **Intocable.** |
| `current` | `ref SurtrCallFrame` | **84** | todo | Publicar el IP es un store sin bounds check. **Intocable.** Hoy en pila `[rsp+0x400]`. |
| `runtime` | `SurtrRuntime` | 32 | todo | |
| `closure` | `SurtrClosure?` | 30 | por frame | |
| `typeTable` | `SurtrTypeHandle[]` | 26 | todo | Recargada en `LoadFrame` |
| `chunk` | `SurtrChunk` | 19 | por frame | Origen de las cinco tablas |
| `pendingArguments` | `int` | 20 | todo | Operando de la secuencia de llamada compartida |
| `constants` | `SurtrRawValue*` | 16 | todo | Hoy en pila `[rsp+0xBC8]`; **13 cuerpos `Ldc` la recargan** |
| `pendingMethod` | `SurtrMethodInfo` | 16 | todo | |
| `pendingClosure` | `SurtrClosure?` | 16 | todo | |
| `methodTable` | `SurtrMethodInfo[]` | 15 | todo | |
| `pendingResults` | `int` | 13 | todo | |
| `fieldTable` | `SurtrFieldInfo[]` | 12 | todo | |
| **`moduleTable`** | `SurtrModule[]` | **7** | todo | ~5 usos reales |
| **`comparer`** | `SurtrValueComparer` | **7** | todo | ~6 usos reales |
| **`pendingGenerator`** | `SurtrGenerator` | **6** | todo | ~4 usos reales |
| `context` | `ref SurtrContext` | — | todo | **Intocable, y está medido:** ver §5 |
| `ip`, `sp`, `frameBase`, `steps` | punteros / `long` | — | todo | El estado del bucle. **Intocables.** |
| `budgeted` | `bool` | 2 | prólogo | Muerta tras inicializar `steps`; probablemente ya eliminada |

Asignación actual en el bucle (del asm de `develop`): `ip`=rax/r11, `sp`=rdx, `frameBase`=r8,
`steps`=r12. En pila y recargadas por acceso: `constants`, `entities`, `current`.

---

## 4. Candidatos, por orden

Cada uno se hace **por separado y se mide por separado**, porque el objetivo es saber cuál mueve
qué. El orden es de mejor a peor relación usos/liveness.

### Fase 1 — Las tres de liveness total con menos de siete usos

| Candidato | Sustitución propuesta |
|---|---|
| `moduleTable` | `current.Chunk!.ModuleTable` en el sitio de uso |
| `comparer` | campo `_comparer` en el sitio de uso |
| `pendingGenerator` | ver nota abajo |

`moduleTable` y `comparer` son mecánicos. `pendingGenerator` es distinto: es el operando de la
secuencia compartida de entrada a generador, alcanzada por tres sitios (resume, delegación, y fin de
un cuerpo delegado). No se puede leer de ningún campo — **hay que decidir si vale la pena**, y la
opción es un campo de instancia `_pendingGenerator` escrito antes del `goto`, que cambia un registro
por un store/load en un camino frío.

**Gate:** el diff de asm debe mostrar el prólogo igual o menor y ningún cuerpo caliente movido. Si
el prólogo no baja, la local no estaba costando un registro y el cambio se revierte — no se acumula
deuda por un cambio que no midió.

### Fase 2 — Las tablas por frame de uso medio

`fieldTable` (12), `methodTable` (15), `typeTable` (26). Las tres se derivan de `chunk`, que ya es
local. La pregunta es si `chunk.FieldTable` en el sitio de uso sale más barato que mantener la
tabla viva: es una carga de campo contra un registro retenido.

**Riesgo específico:** `typeTable` con 26 usos puede estar en cuerpos calientes (`InstanceOf`,
`Cast`, `ArrNew`). Hay que mirar *dónde* se usa antes de tocarla, no solo cuánto.

### Fase 3 — El bloque `pending*`

`pendingMethod`, `pendingClosure`, `pendingArguments`, `pendingResults` = cuatro locales cuyo rango
la JIT probablemente extiende a todo el `switch` aunque conceptualmente sean cortos. Dos vías:

- **Agruparlas en un struct local.** Puede que el asignador lo trate mejor, o peor. Hay que medirlo.
- **Campos de instancia.** Un store/load por llamada en un camino que ya hace veinte líneas de
  montaje de frame.

Es la fase con más incertidumbre y por eso va la última.

### Fase 4 (opcional) — Aislar familias frías detrás de helpers

Independiente de las locales: mover cuerpos de opcodes fríos a métodos `[MethodImpl(NoInlining)]`
alcanzados por un `case` compartido. **Ya validado por medición** en la rama `feature/i64-long`:

| forma | `Run()` | prólogo | líneas de asm que cambian |
|---|---|---|---|
| seis cuerpos escritos | 43 824 | `0xCF8` (+224 B) | 5 969 de 10 809 |
| un cuerpo + helper | **42 787** (+24 B) | `0xC18` **sin cambio** | 2 120 |

Las 2 120 líneas restantes son el precio irreducible de cruzar un `call`. Esta fase encoge `Run()`
de verdad, a cambio de una llamada por instrucción **solo en las familias movidas**.

**Candidatos naturales**, por frialdad: los opcodes de generadores, los de rango, los de conversión,
los de boxing dinámico, `Switch`/`SwitchLookup`. **Nunca** los de carga/almacenamiento de local, los
aritméticos enteros, los saltos ni los de acceso a campo.

---

## 5. Lo que no se toca, y por qué está medido

- **`context`.** Es un getter `ref` que el inliner deja como llamada real en un método de este
  tamaño (**45 `get_Context` por `Run()`**). Re-leer `_runtime.Context` en cada sitio costaba una
  llamada por uso, e `interop` —que hace llamada nativa en bucle— se fue a **+14.6 %**. Un acceso en
  el prólogo y punteros después. Si alguien lo propone otra vez, este es el número.
- **`ip`, `sp`, `frameBase`, `steps`.** Son el estado del bucle. Sacarlos es re-leer memoria por
  instrucción.
- **`entities` (111 usos) y `current` (84).** Ya están en pila y se recargan por acceso; el
  asignador las conserva ahí incluso con registros libres. §10 lo verificó: el beneficio de aquel
  arreglo no vino de subirlas a registro sino de eliminar derrames.
- **El `switch` como forma de despacho.** §0.2.
- **El NaN-tagging.** §0.1.

---

## 6. Protocolo de medición

**La medida primaria de este trabajo es determinista**, y eso es una ventaja enorme frente al
trabajo de opcodes: el objetivo *es* el prólogo, así que la métrica de éxito se lee del asm sin
depender del ruido de la máquina.

### 6.1 El diff de asm (primario)

```
DOTNET_JitDisasm="Run" DOTNET_TieredCompilation=0 \
  dotnet run --project src/Surtr.Bench -c Release --no-build -- \
  --workload intLoop --iters 1 --warmup 0 --surtr-only
```

Extraer el listado de `Surtr.VM.SurtrVirtualMachine:Run(int)`:

```
awk '/^; Assembly listing for method Surtr.VM.SurtrVirtualMachine:Run\(int\)/{f=1} \
     f&&/^; Total bytes of code/{print;f=0;next} f'
```

Antes de comparar, **normalizar dos cosas que cambian sin que cambie el código**:

- etiquetas de bloque: `s/G_M000_IG[0-9]+/LBL/g`
- direcciones absolutas: `s/0x7F[0-9A-F]{10}/ADDR/g` (varían por ASLR **entre procesos**)

Sin normalizar, un cambio nulo parece mover miles de líneas.

**Tres cifras, en este orden de importancia:**

1. **`sub rsp, …` del prólogo.** Es la señal. Si baja, se liberó presión. Si sube, el cambio empeoró
   la asignación y se revierte sin más discusión.
2. **Tamaño total.**
3. **Líneas realmente distintas** tras normalizar.

`DOTNET_JitDisasmSummary=1` da solo la línea de tamaño, útil para una comprobación rápida.

### 6.2 El A/B (secundario, y en esta máquina siempre inválido de forma estricta)

`scripts/ab-suite.ps1 -RefA <commit> -RefB <ruta> -Runs 9`, intercalado.

**Lo que hay que saber para leerlo, aprendido a base de corridas inválidas el 2026-08-27:**

- El control de C# salió entre **4.2 % y 15.6 %** en cuatro corridas seguidas, contra el <1 % que el
  protocolo exige. **La máquina de este proyecto está en uso y no va a haber una ventana limpia.**
- Con esa firma, la métrica del mínimo por proceso **no es válida**: el mínimo solo compara si ambos
  lados alcanzan los mismos estados, y aquí aparecían once o trece casos `BIMODAL-A` y **ninguno
  `BIMODAL-B`**. Ejemplo real: `arrayIndex` con `minA 4.331 / medA 6.206` (uno o dos procesos de
  nueve en el estado rápido) contra `minB 6.129 / medB 6.231` (ninguno). El mínimo comparaba la
  suerte de un lado contra la ausencia de suerte del otro y reportaba **+41 %** donde la mediana
  reportaba **+0.4 %**.
- **En esa situación se lee la mediana por proceso**, que compara el estado lento que ambos lados sí
  alcanzaron. No es el rendimiento "verdadero" del intérprete, pero es una comparación válida.

**Suelo de ruido, calibrado:** la fase F1 de la rama `feature/i64-long` **no tocó `Run()` en
absoluto** —verificado por tamaño nativo idéntico, 42 763 bytes— y su A/B dio aun así **+2.62 % de
mediana sobre mínimos con 22 casos peor de 5 %**. Cualquier resultado de esa magnitud es
indistinguible de nada.

Para computar la mediana sobre medianas desde el CSV que produce el script:

```bash
awk -F'","' 'NR>1 {a=$5; b=$6; gsub(/"/,"",a); gsub(/"/,"",b);
  gsub(/,/,".",a); gsub(/,/,".",b);
  if (a+0>0) printf "%.2f\n", (b-a)/a*100}' <csv> | sort -n
```

---

## 7. Fases y gates

| Fase | Contenido | Gate |
|---|---|---|
| **P0** | Capturar la línea base del asm de `develop` y guardarla | Reproducir 42 763 B / `0xC18` |
| **P1** | `moduleTable`, `comparer`, `pendingGenerator`, **una a una** | Prólogo igual o menor por cambio; revertir el que no baje |
| **P2** | `fieldTable`, `methodTable`, `typeTable` — inspeccionar antes *dónde* se usan | Igual, más A/B por mediana al cerrar la fase |
| **P3** | El bloque `pending*` (struct o campos) | Igual; es la fase con más incertidumbre |
| **P4** | Aislar familias frías tras helpers (opcional) | Encoger `Run()` sin mover cuerpos calientes |

**Regla que atraviesa todo:** un cambio que no baje el prólogo **se revierte**. La tentación es
acumular cambios «neutros» hasta que uno funcione; eso reintroduce exactamente la volatilidad que se
intenta quitar, y además hace imposible atribuir el resultado.

Tests: `dotnet test Surtr.sln` debe quedar en verde en cada fase (3 089 al escribir esto). Ninguna
de estas fases cambia semántica, así que un test rojo es un error de refactor, no un cambio de
comportamiento.

---

## 8. Lo que no sabemos

1. **Cuánto hay realmente que ganar.** §10 sacó −4.6 % quitando cuatro locales. No sabemos si
   quedan otros cuatro puntos o si aquello cogió la fruta baja. El inventario de §3 sugiere que
   `moduleTable`/`comparer`/`pendingGenerator` son del mismo tipo que las que ya funcionaron, pero
   son tres y no cuatro, y con menos usos frías cada una.
2. **Si el asignador de RyuJIT responde monótonamente.** Quitar una local puede no liberar un
   registro si el cuello está en otra parte. De ahí el gate por cambio.
3. **Si esto se transfiere a IL2CPP y Mono**, que es donde Surtr corre de verdad. Toda la
   volatilidad medida es de RyuJIT sobre código re-ubicable; IL2CPP es AOT a direcciones fijas por
   build. **Ninguna medición de este plan es transferible a Unity sin re-medir.**
4. **Cuál es el techo teórico.** Nadie ha contado cuántos registros necesitaría el bucle de despacho
   en su forma mínima.

---

## 9. Documentos relacionados

| Documento | Qué aporta a este plan |
|---|---|
| `docs/Informe-Volatilidad-Run.md` | El diagnóstico completo. §2 el asm de `Run()`, §6 las tres capas, §7 el protocolo, §8 los contadores de hardware, **§10 el arreglo que funcionó y que este plan continúa** |
| `docs/Plan-Opcodes-Extendidos.md` | §11 el techo y sus reglas; §3.2 la regla de admisión del espacio prefijado |
| `docs/VM-Plan.md` | §1.1 por qué el `switch` y no punteros a función; §1.2 las dos pilas |
| `docs/Informe-Benchmarks-Extremo.md` | La suite bajo el protocolo con `--processes` |
| Rama `feature/i64-long` | `docs/Plan-i64-y-f32.md` en esa rama, con la medición inline-vs-helper que valida la fase P4, y el trabajo de `long` que quedó parado por este techo |
