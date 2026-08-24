# Plan-Rangos — Evaluación: `range` como clase de valor, inclusividad y rangos genéricos

> **Estado:** la Propuesta A está **implementada** (ver §2.1). Las propuestas B y C siguen siendo
> evaluación: B concluye «no cambiar nada», C queda aparcada hasta que haya demanda real.

## 2.1 Implementación de la Propuesta A — registro

`range` es hoy un **valor inline de 3 slots** (`start`, `end`, `inclusive`), igual que una tupla:

- **Anchura**: `ValueTypeLayout.IsInlineType` responde 3 para `SpecialType.Range`; el linker del
  runtime (`FieldSlotWidth`, anchuras de tupla anidadas, `SlotWidthOf`/`ResultSlotCount`) da el
  mismo número desde el descriptor `'R'`. Un wrapper de un campo cuyo campo es `range` hereda la
  anchura 3 — el borrado a un campo ya no presupone que el campo ocupa un slot.
- **Opcodes**: `RangeNew`/`RangeNewInclusive` (`0xDB`/`0xDC`) reciclados: escriben bloque en vez
  de apilar referencia. Nuevos `RangePack` (`0xF4`) y `RangeUnpack` (`0xF5`) para el cruce hacia y
  desde almacenamiento monoranura, con la misma convención que `TupPack`/`TupUnpack`.
- **Plegado en acceso**: `start`/`end`/`isInclusive` son lecturas de sub-slot (ni llamada ni
  pack); `length`/`isEmpty`/`contains`/`toString` llegan a sus cuerpos nativos con el bloque como
  receptor. El `for-in` sobre un rango escapado lee los slots directamente — ni pack ni getters.
- **Igualdad estructural**: `==`/`!=` comparan los tres slots; el comparador del runtime trata dos
  packs iguales como la misma clave de diccionario y hashea por contenido.
- **Interop**: sin cambios respecto a su estado previo — el marshaler no reconocía `'R'` antes y
  sigue sin hacerlo; un método host que declare `range` necesita primero soporte explícito.

Los goldens de `Surtr.Stdlib/disasm` se regeneraron con el nuevo flujo de stack.

## 1. Estado actual

Un rango es hoy una **referencia al heap**: `RangeNew`/`RangeNewInclusive`
(`SurtrVirtualMachine.cs`, opcodes `0xDB`/`0xDC`) construyen un `SurtrRange` (dos `int` + flag
`_inclusive`) y lo registran en la entity registry con safepoint de GC. El descriptor es el símbolo
desnudo `'R'` (`SpecialType.Range`), igual que un primitivo — sin forma de anidamiento.

Dos paliativos ya existen:

- **Cabecera de `for-in` nunca asigna**: el compilador baja `for (i in lo..hi)` a bucle contado
  sobre dos enteros (`MethodBodyEmitter.EmitForInRange`); solo un rango que escapa a variable,
  parámetro o retorno materializa el objeto.
- **Derrame a bloque local**: `EnsureLocalRange` ya sabe extender un rango residente en local a un
  tramo de slots cuando un operador lo consume, que es la mitad del andamiaje que usaría un
  rango-inline.

Las tuplas, el modelo que se propone imitar, son híbridas: `ValueTypeLayout.IsInlineType` las
declara **tipos inline** (bloque plano de slots en locales/argumentos/retornos, `ResultSlotCount >
1`) pero se **empaquetan** (`PackTuple`) al entrar en almacenamiento de una sola ranura — arrays,
diccionarios, campos borrados, genéricos. Ese doble régimen es exactamente el que un rango-valor
necesitaría.

## 2. Propuesta A — `range` como clase de valor

### Diseño natural

| Aspecto | Hoy | Como clase de valor |
|---|---|---|
| Representación | referencia a `SurtrRange` registrado | bloque de 3 slots: `start`, `end`, `inclusive` |
| Creación | opcode + registro + safepoint | escribir 3 slots (los opcodes `0xDB`/`0xDC` se reciclan) |
| Paso por llamada | 1 slot (referencia) | 3 slots, como una tupla `(int, int, bool)` |
| Almacenamiento (array/dict/genérico) | 1 slot | empaquetado bajo demanda, igual que tuplas |
| Descriptor | `'R'` | `'R'` sin cambios |
| Iteración | `IterateRange` sobre referencia | sobre bloque o sobre el empaquetado |

El flag ocupa su propio slot en vez de normalizar los bounds a medio-abierto, porque
`0..=int.MaxValue` es legal y su forma normalizada no existe (misma razón que da hoy
`SurtrRange` para guardar los bounds tal cual).

### Coste real

1. **`ValueTypeLayout`**: añadir `SpecialType.Range` como inline type de anchura 3. Es el
   predicado del que cuelgan locales, temporales, parámetros, retornos, fields y el walk de
   igualdad — un punto, pero todos los anchures del compilador pasan por él.
2. **Opcodes y emisor**: `RangeNew*` pasan de apilar referencia a escribir bloque; los accesores
   `start/end/length/isEmpty/isInclusive/contains` (`SurtrCompositeBuiltIns.DeclareRange`) leen
   slots en vez de `GetUnchecked<SurtrRange>`; `EmitForInRange` apenas cambia (hoy ya evita el
   objeto); comparaciones y `contains` bajadas nativas ganan (menos indirección).
3. **Frontera de interop**: `SurtrMarshaler`/generador tratan `'R'` como primitivo de un slot;
   pasaría a anchura 3 como las tuplas, con el mismo repliegue que ya hicieron para `out`.
4. **Golden files**: bytecode y desensamblados de rangos escapados cambian (mismo encoding, otro
   flujo de stack).
5. **Runtime**: `SurtrRange` deja de ser entidad registrada (muere el camino `NewRange`);
   `VisitReferences` vacío desaparece; nada que trazar — 3 primitivos.

### Beneficio esperado

- Cero asignación y cero safepoint por rango escapado. El caso que duele hoy: funciones que
  devuelven rangos dentro de bucles (paginación, ventanas, slices) pagan heap + registro + GC por
  llamada. Con valor, tres writes.
- Igualdad estructural más barata y sin identidad falsa (dos rangos iguales ya comparan igual;
  dejar de ser entidades lo hace obvio).
- Coherencia con el modelo §2.9/§5.3: «lo pequeño e inmuble viaja plano».

### Veredicto

**Vale la pena, como fase acotada** — el andamiaje de tuplas/value classes está completo y probado,
y `EnsureLocalRange` demuestra que el derrame de rangos ya se contempló. No es urgente: el caso
caliente (cabecera de `for`) ya no asigna, así que la ganancia es real pero secundaria. Hacerlo
**después** de medir en `Surtr.Bench` cuánto pesa materializar rangos escapados; si el bench no lo
nota, queda como higiene de modelo más que como optimización.

Orden sugerido si se aprueba: (1) anchura en `ValueTypeLayout` + derrame, (2) opcodes/emisor,
(3) empaquetado en almacenamiento monoranura, (4) accessors nativos leyendo bloque, (5) goldens y
bench antes/después.

## 3. Propuesta B — ¿diferenciar inclusivo/exclusivo?

**Ya está diferenciado**, y bien: `..` exclusivo / `..=` inclusivo (`DotDotEquals`), dos opcodes
distintos, `isInclusive` observable, `contains` y `length` respetando el extremo. La pregunta es si
haría falta algo más fuerte. Las alternativas, evaluadas:

| Alternativa | Problema | Veredicto |
|---|---|---|
| Normalizar todo a medio-abierto | `0..=int.MaxValue` no tiene forma normalizada; `length` satura igual pero el extremo se pierde | Rechazada (ya se rechazó en `SurtrRange`) |
| Dos tipos (`RangeExclusive`/`RangeInclusive`) | Duplica API (`contains`, `iterate`, operadores ×2), obliga a conversiones entre ambos y envenena la inferencia de `a..b` vs `a..=b` | Rechazada: el flag es datos, no tipo |
| Solo exclusivo (estilo Rust sin `..=`) | Los rangos inclusivos son idiomáticos en dominios enteros pequeños (índices `0..=n`, meses, chars); perderlos empuja a `+1` manuales con sus off-by-one | Rechazada |
| Flag interno invisible (ocultar `isInclusive`) | Rompe `toString` redondo-trip y la serialización fiel del rango escrito | Innecesaria |

El diseño actual (un solo tipo + bandera + dos constructores) es el punto óptimo entre coste de
API y expresividad. **No cambiar nada.**

## 4. Propuesta C — Rangos genéricos estilo Kotlin (`T..T` para cualquier comparable)

Kotlin define `ComparableRange<T>` más especializaciones (`IntRange`, `CharRange`,
`ClosedFloatingPointRange`) y progresiones con paso (`downTo`, `step`). Para Surtr:

**Lo que ya existe a favor:** `operator<=>` (§5.6) da el orden triple que un rango genérico
necesita; los constraints de genéricos (§6) saben expresar `<T : IComparable<T>>`; `value class`
genérica podría portarlo casi todo en stdlib.

**Lo que falta y cuesta caro:**

1. **Descriptor**: `'R'` es un símbolo desnuo compartido con el primitivo-int. Un `range<T>`
   necesitaría forma de anidamiento (`R<T...>`) — tocar emisor/comparador/importador de
   descriptores, formato de imagen y toda la tooling, el mismo trabajo que evitó el §2.9 al
   elegir borrado.
2. **Despacho de iteración**: avanzar un `T` genérico exige `successor(T)` — que el lenguaje no
   tiene. Con `<=>` solo se compara, no se avanza. Kotlin lo esquiva con progresiones aritméticas
   por tipo concreto; un rango genérico honesto necesita un contrato nuevo (`ISteppable` o
   similar), no azúcar.
3. **Coste por elemento**: iterar vía vtable (una llamada `<=>`+step por elemento) contradice el
   presupuesto del bucle `for-in`, que hoy compila a contador plano. Monomorfizar por tipo
   elemental es código-bloat puro para cubrir `char` y fechas.
4. **Flotantes**: un rango continuo necesita `step` explícito y política de extremo; semántica
   entera disfrazada produce bucles flotantes sorpresa.

**Veredicto: no hacerlo ahora.** El 90 % del uso real es `int` (y lo cubre el builtin). Si algún
día aparece demanda real (iterar `char`, fechas de un dominio host), el camino barato es stdlib
sin tocar el núcleo:

```surtr
// value class genérica de stdlib, iteración por delegación, sin descriptor nuevo
public sequence fun chars(from: char, to: char): Sequence<char>
```

y, solo si eso se queda corto, un `Range<T : IComparable<T>, TStep>` en stdlib con el contrato de
paso — manteniendo `range` int como caso especial rápido que es.

## 5. Resumen ejecutivo

| Pregunta | Decisión recomendada |
|---|---|
| ¿`range` como clase de valor? | **Hecho (§2.1)**: valor inline de 3 slots con pack/unpack, igualdad estructural y for-in leyendo bloque |
| ¿Diferenciar inclusivo/exclusivo? | **Ya diferenciado** con el diseño correcto (tipo único + flag + 2 opcodes); no cambiar |
| ¿Ranges estilo Kotlin? | **No por ahora**: descriptor anidado + contrato de paso + coste por elemento; alternativa stdlib cuando haya demanda |
