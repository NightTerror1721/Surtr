# Plan: qué falta en la Standard Library de Surtr

**Estado: planificación — nada de este documento está implementado.** Es la contraparte de
`docs/Compiler-Plan.md` §10.2 ("la standard library es el programa más grande que el compilador
nunca ha tenido que compilar") pero centrada solo en `src/Surtr.Stdlib`: qué hay ya, qué falta
para que la parte "expresable en Surtr" de §13 esté mínimamente completa, y en qué orden.

Verificado leyendo el código real (`src/Surtr.Stdlib/`, `src/Surtr.Core/Runtime/BuiltIns/`,
`src/Surtr.Tests/Stdlib/SurtrStdlibTests.cs`), no solo el README — con un hallazgo concreto en la
Parte 1 que conviene resolver antes de añadir módulos nuevos.

---

## Parte 0 — Qué ya existe (verificado)

La infraestructura del proyecto está completa y no hay que tocarla para añadir contenido:
`Surtr.Stdlib.Tool` compila cada `.surtr` bajo `src/surtr/` a su propio módulo, `Surtr.Stdlib.csproj`
lo embebe como recurso (`build/`, `disasm/`), `SurtrStdlib.cs` lo carga (`LoadAll`/`LoadInto`,
filtrable por `StdlibModules`), y `src/Surtr.Tests/Stdlib/SurtrStdlibTests.cs` cubre el transporte
completo. Un módulo nuevo bajo una carpeta ya existente (`core/`, `math/`, `collections/`, `text/`)
no necesita ningún cambio de infraestructura — solo el archivo `.surtr` y, opcionalmente, más tests.

Contenido actual, módulo por módulo:

| Módulo | Archivo | Contenido | Estado |
|---|---|---|---|
| `surtr.core.Contracts` | `src/surtr/core/Contracts.surtr` | `IDisposable<T>` | Completo |
| `surtr.core.Exception` | `src/surtr/core/Exception.surtr` | `NotSupportedException`, `InvalidOperationException` | **Ver Parte 1 — colisión de nombres** |
| `surtr.math.Math` | `src/surtr/math/Math.surtr` | constantes, trig/float nativas, `abs`/`min`/`max`/`clamp`/`lerp`/`sign`/`approximately`/`repeat` | Completo |
| `surtr.math.Angle` | `src/surtr/math/Angle.surtr` | `value class Angle` (envuelve radianes) | Mínimo — solo constructor + getter |
| `surtr.collections.Collection` | `src/surtr/collections/Collection.surtr` | `IReadOnlyCollection<T>`, `ICollection<T>` | Completo |
| `surtr.collections.List` | `src/surtr/collections/List.surtr` | `IReadOnlyList<T>`, `IList<T>`, `List<T>`, `LinkedList<T>` | Completo |
| `surtr.collections.Stack` | `src/surtr/collections/Stack.surtr` | `IStack<T>`, `Stack<T>` | **En curso (cambios sin commitear)** — no tocar, es tu WIP |
| `surtr.text.StringBuilder` | `src/surtr/text/StringBuilder.surtr` | `StringBuilder` | Completo |

Todo lo que exige `Language-Syntax.md` §13.2/§13.3 (las cuatro interfaces core, la jerarquía de
`Exception`, `array.sort`, `string.format`) ya vive en `Surtr.Core` (built-in, C#) — no es trabajo
de este proyecto. `Type`/`Member`/`Module` (§13.5) también: `SurtrReflectionBuiltIns.cs` y
`SurtrModuleReflectionBuiltIns.cs` ya los implementan como built-ins. El trap-to-class mapping que
el README todavía listaba como pendiente (línea 175) también está resuelto —
`SurtrBuiltIns.cs:492-502` ya mapea cada excepción de CLR a su clase Surtr — así que el README está
desactualizado en ese punto.

---

## Parte 1 — Hallazgo: `InvalidOperationException` está declarada dos veces

`SurtrBuiltIns.cs` ya declara `InvalidOperationException` como built-in (línea 157/281/335,
trap-mapped, siempre en scope, sin necesidad de import — igual que `IndexOutOfRangeException` que
usa `List.surtr` sin importar nada). Pero `surtr.core.Exception` (`Exception.surtr:6-9`) declara
**otra clase distinta con el mismo nombre simple**, y `Stack.surtr` la trae a scope con
`import surtr.core.Exception;` — esa importación **tapa** (shadows) la built-in dentro de
`Stack.surtr`, según la regla de `CLAUDE.md`: *"a local declaration shadows an imported name"* /
*"imports [sit] in a scope of their own"* entre las declaraciones del módulo y los built-ins.

Consecuencia real: el `InvalidOperationException` que lanza `Stack.pop()` **no es la misma clase**
que un `catch (e: InvalidOperationException)` escrito en un archivo que no importa
`surtr.core.Exception` esperaría capturar — son dos descriptores distintos
(`surtr:InvalidOperationException` vs. algo como `Osurtr.core:Exception.InvalidOperationException;`).
Esto ya obligó a construir un mecanismo de compensación: `SurtrStdlib.cs` (`ExpandDependencies`,
líneas 270-285) fuerza `Collections → Core` específicamente porque `Stack` "necesita" las
excepciones de `Core` — y hay un test dedicado a ese único caso
(`SelectingCollectionsPullsInCoreBecauseStackNeedsItsExceptions`).

**Recomendación antes de añadir nada más:** borrar la clase `InvalidOperationException` de
`Exception.surtr` y dejar que `Stack.surtr` (y cualquier módulo futuro) use la built-in sin
importar `surtr.core.Exception` para eso — exactamente como ya hace con
`IndexOutOfRangeException`. Si nada más en `Exception.surtr` queda usándose desde `collections`,
la dependencia `Collections → Core` deja de ser real y se puede retirar `ExpandDependencies` y su
test, simplificando el loader. `NotSupportedException` sí es nueva (no hay built-in con ese
nombre) y se queda tal cual.

Esto es corrección de una colisión existente, no una decisión de diseño abierta — conviene
resolverlo en la primera pasada, porque cada módulo de colecciones nuevo (`Queue`, etc.) va a
querer lanzar `InvalidOperationException` también y heredaría el mismo problema si sigue el
patrón actual de `Stack.surtr` tal cual.

---

## Parte 2 — Estructura de directorios objetivo

```
src/Surtr.Stdlib/src/surtr/
├── core/
│   ├── Contracts.surtr        [existente] IDisposable<T>
│   └── Exception.surtr        [existente, corregir Parte 1] NotSupportedException
├── math/
│   ├── Math.surtr              [existente]
│   ├── Angle.surtr             [existente]
│   ├── Random.surtr            [nuevo, Tier 2] PRNG determinista con seed
│   └── Vector2.surtr           [nuevo, Tier 3] value class, opcional
├── collections/
│   ├── Collection.surtr        [existente] IReadOnlyCollection<T> / ICollection<T>
│   ├── Collections.surtr       [nuevo, Tier 1] helpers estáticos — ya prometido en el README, no existe
│   ├── List.surtr               [existente] IReadOnlyList<T> / IList<T>, List<T>, LinkedList<T>
│   ├── Stack.surtr              [existente, en curso] IStack<T>, Stack<T>
│   ├── Queue.surtr              [nuevo, Tier 1] IQueue<T>, Queue<T>
│   └── HashSet.surtr            [nuevo, Tier 2] ISet<T>, HashSet<T>
└── text/
    └── StringBuilder.surtr     [existente]
```

Ninguna de estas adiciones necesita un nuevo flag en `StdlibModules` — todas caen bajo una
categoría (`core`/`math`/`collections`/`text`) que ya existe. Un flag nuevo solo hace falta si
apareciera una carpeta de primer nivel nueva (p. ej. un futuro `surtr/io/` — ver Parte 5, no
recomendado por ahora).

---

## Parte 3 — Tier 1: lo mínimo para que "colecciones" esté completo

### 3.1 `surtr.collections.Collections` (helpers)

El README (`src/Surtr.Stdlib/README.md` línea 88) ya documenta este módulo — el archivo
simplemente no existe todavía. Es el hueco más directo entre lo prometido y lo real. Contenido
mínimo, todo funciones de módulo (sin estado, sin necesidad de `unsafe` → 100% Surtr):

- `toArray<T>(source: IReadOnlyCollection<T>): T[]`
- `addRange<T>(target: ICollection<T>, source: IReadOnlyCollection<T>): void`
- `reverse<T>(list: IList<T>): void`
- `equals<T>(a: IReadOnlyCollection<T>, b: IReadOnlyCollection<T>): bool` — comparación
  estructural elemento a elemento (longitud + `==` por posición), en el mismo espíritu que la
  igualdad estructural de `tuple`.

Deliberadamente **no** incluye `map`/`filter`/`reduce` con lambdas genéricas — sería la primera
vez que la stdlib se apoya en inferencia de tipo de lambda contra un parámetro genérico en una
función de módulo (`docs/Compiler-Plan.md` §10.1c ya lo soporta en general, pero no hay ningún
caso de uso real todavía ejercitando esa combinación exacta). Vale la pena probarlo aparte, no
mezclado con el hueco mínimo — ver Parte 4.

### 3.2 `surtr.collections.Queue` (`IQueue<T>`, `Queue<T>`)

Contraparte natural de `Stack<T>`, mismo patrón exacto que ya sigue `Stack.surtr`:

```
public interface IQueue<T>
{
    isEmpty: bool { get; }
    fun enqueue(item: T): void;
    fun dequeue(): T;
    fun peek(): T;
}

public class Queue<T> : IQueue<T>, ICollection<T>
{
    // buffer circular sobre T[] en vez del array simple de Stack —
    // dequeue() por el frente es O(1) amortizado en vez de O(n)
}
```

Nota de implementación (no mandato): `Stack` puede permitirse `T[]` + `push`/`pop` porque ambos
extremos son el mismo (el final). `Queue` sacando por el frente con un array simple sería O(n) por
`dequeue`; un buffer circular con `_head`/`_tail`/`_count` sobre `array<T>(capacity)` (mismo patrón
de `ensureCapacity` que ya usa `List.surtr`) mantiene todo en O(1) amortizado.

Aplica el mismo `private sealed class Iterator<T> : IIterator<T>` que ya usan `Stack`/`List`.
Lanza `InvalidOperationException` en `dequeue()`/`peek()` sobre cola vacía — una vez resuelta la
Parte 1, sin necesidad de importar `surtr.core.Exception`.

---

## Parte 4 — Tier 2: extensiones recomendadas, no bloqueantes

Útiles y de tamaño acotado, pero no necesarias para que la stdlib actual esté "completa" contra lo
que la especificación y el README ya prometen. Orden sugerido si se abordan:

- **`surtr.collections.HashSet`** (`ISet<T>`, `HashSet<T>`) — hoy no hay ningún tipo de conjunto;
  `dict` built-in cubre pares clave/valor pero no un set puro. Se puede construir sobre un
  `Dictionary<T, bool>`-shaped storage interno o replicar el patrón `{int: V}` especializado que
  ya usa `SurtrDictionary` — decisión de diseño a tomar al implementarlo, no de este plan.
- **`surtr.math.Random`** — generador determinista tipo xorshift32/64 sembrado por el usuario
  (`Random(seed: int)`), **puramente Surtr** (aritmética entera, sin `unsafe` ni servicio de VM),
  coherente con la filosofía de determinismo que ya sigue `array.sort` (§13.4: mismo resultado en
  toda plataforma/versión de runtime). No usar `System.Random` de C# ni una fuente de entropía del
  host salvo que se declare explícitamente un `seedFromSystem(): int` nativo aparte, opcional.
- **`surtr.collections.Collections` — helpers con lambda** (`any`/`all`/`find`/`count`) sobre
  `(T) -> bool`, una vez que 3.1 esté probado y estable. Primer caso real de lambda genérica sobre
  un parámetro de módulo — vale la pena aislarlo como su propio cambio para poder atribuir
  cualquier fallo del compilador a esto y no a otra cosa.
- **Excepciones adicionales** en `Exception.surtr` según haga falta al escribir lo anterior
  (`OverflowException`, `TimeoutException`) — añadir bajo demanda, no por adelantado.

---

## Parte 5 — Fuera de alcance (Tier 3 / diferido a propósito)

- **I/O, red, `DateTime`, JSON.** Surtr es un lenguaje embebido en Unity; estas superficies casi
  siempre deben venir del host (Unity ya tiene su propio `File`/`DateTime`/networking) vía
  `native` declarations del proyecto que lo hospeda, no de `Surtr.Stdlib`. Meterlas aquí ataría la
  stdlib a decisiones de plataforma que no le corresponden.
- **`Vector2`/`Vector3`/`Vector4`, `Quaternion`, `Matrix`.** Encajan bien con el caso de uso
  (juegos), pero Unity ya expone los suyos vía interop nativo — duplicar el tipo en Surtr solo
  tiene sentido si el diseño quiere que la lógica de juego pura no dependa de tipos de Unity, lo
  cual es una decisión de producto, no una que este documento deba forzar. Anotado para decidir
  más adelante, no para implementar ahora.
- **Reflexión (`Type`/`Member`/`Module`).** Ya implementada como built-in en `Surtr.Core` — no es
  trabajo de `Surtr.Stdlib`.

---

## Parte 6 — Checklist por módulo nuevo

1. Archivo en `src/Surtr.Stdlib/src/surtr/<categoría>/<Nombre>.surtr`; el path del módulo lo deriva
   la ubicación (`surtr.<categoría>.<Nombre>`) — sin cabecera, igual que cualquier módulo Surtr.
2. Nativo solo si necesita `unsafe`, un puntero crudo o un servicio de VM (§13.1); todo lo demás en
   Surtr puro. Si hace falta un cuerpo nativo, va en `Native/` (mismo patrón que
   `SurtrMathNative.cs`) y se publica en `SurtrStdlib.RegisterNativeBodies`.
3. Las colecciones nuevas implementan la escalera ya existente
   (`IReadOnlyCollection<T>`/`ICollection<T>`/`IReadOnlyList<T>`/`IList<T>` de `Collection.surtr`/
   `List.surtr`) en vez de inventar contratos paralelos.
4. Un `private sealed class Iterator<T> : IIterator<T>` anidado por colección para `iterate()`,
   siguiendo la forma exacta que ya usan `List`/`Stack`.
5. Antes de declarar una excepción nueva, comprobar si ya existe como built-in
   (`SurtrBuiltIns.cs`) o en `surtr.core.Exception` — ver el hallazgo de la Parte 1 sobre qué pasa
   si no se comprueba.
6. Regenerar imágenes: `dotnet build src/Surtr.Stdlib/Surtr.Stdlib.csproj` (reescribe `build/`,
   `disasm/`, re-embebe los recursos).
7. Tests en `src/Surtr.Tests/Stdlib/SurtrStdlibTests.cs`: al menos que el módulo cargue
   (`TryGetModule`) bajo `StdlibModules.All` y bajo su categoría concreta (patrón
   `SelectiveLoadOnlyLoadsTheChosenCategory`), más el comportamiento funcional real.
8. Actualizar el doc-comment de `StdlibModules` en `SurtrStdlib.cs` (hoy desactualizado — dice
   `Collections` = "`Collection`, `List`" y ya le falta `Stack`) y la tabla de módulos del
   `README.md` — ambos son el índice legible de qué hay en cada categoría y ya han quedado
   desincronizados una vez.

---

## Parte 7 — Orden de trabajo sugerido

1. **Resolver la colisión de `InvalidOperationException`** (Parte 1) — antes de que `Queue` u
   otro módulo repita el mismo patrón.
2. Terminar `Stack.surtr` (ya casi completo — es tu cambio en curso, no tocar desde aquí).
3. `surtr.collections.Collections` — helpers básicos (3.1), el hueco ya prometido por el README.
4. `surtr.collections.Queue` (3.2).
5. Actualizar `README.md` y el doc-comment de `StdlibModules` (checklist punto 8) — arrastran
   desactualización incluso antes de Tier 2.
6. Tier 2 según necesidad real de uso (`HashSet`, `Random`, helpers con lambda) — no de golpe.
