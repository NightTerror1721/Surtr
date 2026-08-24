# Plan-ClaseBase — Evaluación: clase base universal con `equals`/`toString`/`hashCode`

> **Estado:** evaluación (sin implementar). Responde a la pregunta planteada: «evaluar la
> posibilidad de crear una clase base madre para todas las demás, con métodos básicos como
> `equals`, `toString`, `hashCode`, para simplificar la igualdad y el hash de los tipos que
> comparan valores, y hacer que `==`/`!=` llamen implícitamente a `equals` cuando no se
> sobreescriben».

## 1. Lo que ya existe (inventario honesto)

Antes de diseñar hay que ver cuánta «madre» ya está repartida por el sistema:

| Necesidad | Dónde vive hoy | Quién la usa |
|---|---|---|
| Igualdad de valores en runtime (claves de dict, búsquedas en arrays) | `SurtrValueComparer.ValuesEqual` / `HashOf`: strings por texto, tuplas y value classes boxeadas por slots, primitivos por bits normalizados, resto por identidad | toda la VM, un comparador compartido |
| Igualdad de value classes en fuente | `EmitValueClassEquality`: `==` baja a comparación campo a campo; `===` rechazado («un valor no tiene identidad») | §2.9 |
| Igualdad de clases normales en fuente | identidad por defecto (`REQ`/`RNE`) salvo `operator==` declarado, que gana | §5.6 |
| Hash coherente con esa igualdad | solo runtime (`SlotsHash`, FNV de tuplas); **no existe `hashCode()` invocable desde Surtr** | — |
| Texto | conversión `string(x)` + `toString` nativo en los built-ins (`range.toString`, etc.); clases de usuario: nada automático | §5.4 |

El hueco real es doble: **no hay forma portátil de pedir «¿este valor es igual a otro?» ni
«dame tu hash» desde fuente** para un tipo cualquiera, y la igualdad por defecto de las clases
(identidad) no es la que quien viene de C#/Java/Kotlin espera combinar con diccionarios.

## 2. Opción A — clase base madre universal (`object` implícito)

Todas las clases heredan de una raíz con `equals`, `hashCode`, `toString`.

### Costes estructurales

| Problema | Gravedad |
|---|---|
| Contradice dos decisiones documentadas del lenguaje («no `object`, sin raíz»: README y Language-Syntax §1) — las clases de usuario hoy *empiezan* en profundidad 0 | alta: es una reversión de diseño, no una adición |
| Los built-ins no son clases de esa jerarquía: `int`, `range`, `closure`, `array` son familias especiales (`SpecialType`) con descriptores propios. ¿Heredan de la madre? Entonces la madre no puede tener campos ni estado, y sus métodos no pueden ser virtuales con vtable para ellos sin inventar despacho sobre primitivos | alta: la «madre común» sería mentira para la mitad de los tipos que motivan la evaluación |
| Colisiones: cada clase existente que declare su propio `toString`/`equals` pasa a ser override; cada usuario con un miembro llamado igual choca. El lookup de miembros cambia para todo el lenguaje | media-alta |
| `unknown` y parámetros genéricos borrados reciben valores de cualquier familia: si la madre declara métodos, `unknown` debería exponerlos — pero `unknown` deliberadamente no expone nada sin cast | media: rompe la simetría de §5.10 |
| Interop: los objetos host adoptados (`SurtrNativeObject`) no tienen esos miembros CLR equivalentes salvo coincidencia | media |

### Conclusión parcial

Una jerarquía universal resuelve el síntoma (falta de `equals`/`hashCode` comunes) pagando el
precio equivocado: acoplar *todos* los tipos a una raíz cuando lo único compartible entre un
primitivo, un array y una clase de usuario es exactamente el trío de operaciones — que ya tiene
un punto natural de unión fuera de la jerarquía: el propio runtime.

## 3. Opción B — contratos sintetizados por el compilador (recomendada)

Mismo trío de operaciones, cero jerarquía: el compilador **sintetiza implementaciones por
defecto** cuando el tipo no las declara, y el runtime ya hace lo mismo por su lado.

### 3.1 Las tres operaciones

```surtr
class Enemy {
    public var x: float;
    public let kind: string;
    // NADA de esto se escribe; el compilador lo aporta:
    //
    // fun equals(other: Enemy): bool   → campo a campo con == de cada campo
    // fun hashCode(): int              → mezcla FNV de los hashes de campo
    // fun toString(): string           → "Enemy(x=..., kind=...)"
}
```

| Operación | Síntesis para clases | Built-ins | Value classes |
|---|---|---|---|
| `equals(other)` | `==` campo a campo (el walk que `EmitValueClassEquality` ya sabe emitir, reutilizado como cuerpo sintético); null-safe: `other === this` cortocircuito verdadero, `other is null` falso, tipo distinto falso | ya responden vía `SurtrValueComparer`; los nativos `equals` de primitivos (§13.2) quedan como la misma cosa vista desde fuente | ya lo son: su `==` estructural es el caso base |
| `hashCode(): int` | mezcla FNV-1a sobre `hashCode()` de cada campo — **idéntico algoritmo que `SurtrValueComparer.SlotsHash`**, garantía explícita: iguales ⇒ mismo bucket | delegar al comparer runtime (nativo) | idem |
| `toString(): string` | `"Nombre(campo=valor, ...)"` con `toString` recursivo por campo; round-trip legible | ya tienen; el sintético solo rellena huecos | `"Nombre(...)"` |

La clave de coherencia: **un solo algoritmo de hash compartido entre lo que emite el compilador
y lo que ejecuta el comparer del runtime**, porque ambos derivan del mismo convenio FNV sobre
campos. Un `dict<Enemy, int>` y un `HashSet`-de-stdlib ven claves iguales como iguales.

### 3.2 `==`/`!=` llamando a `equals` implícitamente

Regla propuesta, de menor a mayor:

1. Si la clase declara `operator==` → se usa (hoy).
2. Si declara `fun equals(...)` → `==` baja a la llamada (y `!=` niega), igual que hoy hace con
   `operator==`.
3. Si no declara ninguno → **síntesis**: `==` es el campo-a-campo de 3.1.

El punto 3 es un **cambio de semántica rompente**: hoy `a == b` sobre clases sin operador
responde identidad. Pasar a igualdad estructural silenciosa cambiaría programas existentes
(two distinct `Enemy`s with equal fields would newly compare equal). Dos caminos:

- **B1 (ruptura asumida)**: cambio de versión mayor del lenguaje, documentado. Coherencia total:
  clases y value classes comparten filosofía de valor.
- **B2 (compatible)**: el default sintético de `==` queda disponible pero solo se activa si la
  clase opta (atributo `[value]` o declarar `equals` sin `operator==`), manteniendo identidad
  como default histórico. Menos mágico, migración suave.

Recomendación: **B2 ahora, B1 cuando el lenguaje tenga usuarios** — el mecanismo es idéntico;
solo cambia qué enciende la síntesis.

`===`/`!==` siguen siendo identidad pura e inalterados, y siguen rechazados sobre value classes.

### 3.3 Por qué esto sí cubre a los built-ins (y la madre no)

Los casos que motivaban la clase madre — «que los built-in que requieren comparar u obtener hash
lo obtengan del mismo sitio» — quedan servidos por la garantía central de §3.1:

```
iguales por ==  ⇔  ValuesEqual(runtime)  ⇔  hashCode()/HashOf() iguales
```

con UN convenio (FNV sobre campos/slots) definido una vez y respetado por los tres frentes:
emisor (cuerpos sintéticos), comparer (runtime) y stdlib. La «madre» deja de ser una clase y
pasa a ser ese convenio + las reglas de síntesis.

## 4. Detalles de diseño de la síntesis

| Pregunta | Decisión propuesta |
|---|---|
| ¿Se puede llamar explícitamente `x.equals(y)`? | Sí: los sintéticos son miembros reales (visibles en metadata, llamables, overridables escribiendo uno propio) |
| Campos estáticos / funciones | excluidos de la síntesis; solo estado de instancia |
| Campos de tipo referencia mutable (`array`, otra clase) | `equals` compara con `==` del campo (identidad para arrays — correcto y predecible; igualdad profunda nunca implícita). Documentarlo |
| Herencia: `Enemy : Entity`, ¿compara campos de base? | Sí, walk completo por `MemberLookup.Reachable` en orden declaración; `equals` sintético exige mismo runtime-type exacto (clase distinta ⇒ false), patrón C# |
| Ciclos (campo apunta al contenedor) | imposible por defecto: comparación por identidad de ese campo corta el ciclo (arrays/referencias), a diferencia de la igualdad profunda recursiva |
| Rendimiento | el cuerpo sintético se genera como cualquier método: inlinable (`inline` heurística), sin reflexión, sin despacho extra; costo cero si nadie lo llama |
| Interop host | los proxies nativos NO reciben síntesis (su identidad es del host); `SurtrNativeObject` decide después si expone equivalentes |

## 5. Comparativa final

| Criterio | A: clase madre | B: síntesis + convenio |
|---|---|---|
| Cubre primitivos/built-ins | mal (no están en la jerarquía) | bien (comparer + nativos ya lo hacen) |
| Cambio de diseño del lenguaje | revierte «sin raíz» | ninguno |
| Riesgo de colisiones/overrides globales | alto | nulo (aparece solo donde falta) |
| Coste VM/runtime | nuevo despacho virtual para primitivos o nada | cero (ya existe) |
| `==` → `equals` implícito | natural | igual de natural (regla de resolución §3.2) |
| Coherencia dict/hash fuente-runtime | indirecta | directa: un convenio, tres consumidores |

**Veredicto: Opción B.** No añadir clase base universal. Añadir: (1) síntesis de `equals`/
`hashCode`/`toString` para clases que no los declaren, reutilizando el walk de igualdad existente;
(2) regla de resolución `operator==` → `equals` → síntesis para `==`/`!=`; (3) documento del
convenio de hash compartido con `SurtrValueComparer`. Fases: 1=`equals`+`==` (el valor diario),
2=`hashCode` visible desde fuente + `Set`/`Map` de stdlib consumiéndolo, 3=`toString` sintético.
