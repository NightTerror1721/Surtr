# Plan-Revision-Stdlib — Auditoría de `src/Surtr.Stdlib` y propuestas

> **Estado:** diagnóstico completo, sin implementar. Nace de una revisión manual de los 25 archivos
> `.surtr` de la stdlib (~3500 líneas), con dos hallazgos verificados compilando y ejecutando código
> real contra el runtime (`surtrc build` + `surtr run`), no solo por lectura. Este documento no
> decide el orden de trabajo por sí mismo — la §5 recoge las decisiones que hacen falta antes de
> tocar código.

---

## 1. Resumen ejecutivo

La stdlib tiene un núcleo sólido — `Sequence.surtr` en particular es el módulo mejor diseñado del
conjunto (generadores perezosos, `dispose()` correcto en cada combinador vía `try/finally`) — pero
dos módulos de uso muy común están rotos de raíz (`StringBuilder`, `Profiler`), hay varias
inconsistencias de diseño entre módulos hermanos (`List` vs `LinkedList`, `Queue` vs `Deque`), algo
de código muerto, y la documentación del proyecto (`src/Surtr.Stdlib/README.md`) describe una
versión de la stdlib que ya no existe. Además, dado el propósito declarado del lenguaje —
alternativa a Lua embebida en Unity — falta por completo una librería de matemática de geometría
(`Vector2`/`Vector3`/`Quaternion`/...) y un generador de números aleatorios, que son casi
imprescindibles para ese caso de uso.

---

## 2. Hallazgos clasificados

Cada hallazgo lleva: **tipo**, **prioridad**, archivo/línea, evidencia y arreglo propuesto.

### 2.1 Bugs — Prioridad CRÍTICA (confirmados ejecutando código real)

#### B1 — `StringBuilder` produce contenido corrupto desde su construcción
**Archivo:** `src/surtr/text/StringBuilder.surtr:7-11`

```surtr
public constructor(initialCapacity: int = DefaultCapacity)
{
    if (initialCapacity < 1) throw ArgumentException("Initial capacity must be greater than 0");
    this._buffer = array<char>(initialCapacity);
}
```

`array<T>(n)` crea un array de **longitud** `n` relleno de ceros (`ArrNew` +
`InitializeLength`, `src/Surtr.Core/VM/SurtrVirtualMachine.cs:1748`), no una reserva de capacidad
con longitud 0. `StringBuilder` no lleva un `_length` separado de `_buffer.length` (a diferencia de
`List<T>`, que sí distingue `_capacity`/`_length`), y `append`/`appendChar` usan `_buffer.push(...)`,
que **añade** por detrás de esos `initialCapacity` caracteres NUL ya presentes.

**Evidencia empírica** (compilado y ejecutado contra el runtime real):
```
sbLength()   -> 16                  // debería ser 0 en un StringBuilder recién creado
sbToString() -> "\0\0...\0hi"       // 16 NULs seguidos del contenido real ("hi")
```

Afecta a **cualquier uso normal** de la clase, no es un caso límite.

**Arreglo propuesto:** replicar el patrón de `List<T>` — campo `_length` propio, escritura por
índice (`_buffer[_length++] = ch`) y `ensureCapacity` que solo reasigna cuando `_length == _capacity`,
igual que `List.ensureCapacity` (`List.surtr:128-140`). `clear()` debe resetear `_length = 0` en vez
de `_buffer.clear()`.

#### B2 — `Profiler`/`Stopwatch` no miden tiempo real
**Archivo:** `src/surtr/diagnostics/Profiler.surtr`

- `Stopwatch.start()`/`stop()`/`restart()` (líneas 22-42) solo cambian el flag `_running`; `_elapsed`
  solo cambia si alguien llama manualmente a `addElapsed(delta)`. Hay un `native fun
  stopwatchTimestamp(): float` declarado (línea 59) que **nadie invoca** desde dentro de `Stopwatch`.
- `ProfilerEntry.elapsed` (línea 136) es un `let` fijado a `0.0` en el constructor y nunca
  reasignado tras `stopwatch.stop()` en `ProfilerScope.dispose()` (línea 76-79).

**Evidencia empírica:** un `Profiler` que envuelve un bucle de 1.000.000 de iteraciones dentro de
`beginScope(...)`/`scope.dispose()` devuelve `getEntry(0).elapsed == 0`.

**Arreglo propuesto:**
- `Stopwatch.start()`/`restart()` deben capturar `stopwatchTimestamp()` en un campo `_startedAt`.
- `Stopwatch.stop()` y la propiedad `elapsed` deben calcular `_elapsed + (running ?
  stopwatchTimestamp() - _startedAt : 0)`.
- `ProfilerScope.dispose()` debe escribir el tiempo medido de vuelta en la `ProfilerEntry`
  correspondiente (hoy `elapsed` es inmutable — hay que convertirlo en `var` o en un campo mutado
  a través de un método interno de `ProfilerEntry`).

### 2.2 Bugs — Prioridad ALTA (alta confianza, por inspección de código)

#### B3 — `BinaryReader` corrompe silenciosamente lecturas truncadas
**Archivo:** `src/surtr/io/BinaryReader.surtr`

`readChar()` (línea 31), `readInt()` (línea 40), `readBytes()` (línea 50) y `readString()` (línea 63)
comprueban EOF **solo en el primer byte** de una lectura multi-byte (`if (b0 < 0) return 0;`). Si el
stream se agota a mitad de una lectura (p. ej. un `int` con 2 de 4 bytes disponibles), el resultado
es basura silenciosa (bytes de EOF, típicamente `-1`, mezclados por desplazamiento de bits) en vez de
una señal de error. Además `readBytes`/`readString` devuelven un buffer del tamaño pedido aunque se
haya leído menos, sin forma de que el llamador distinga "leí todo" de "leí menos de lo pedido".

Esto es inconsistente con el propio `Stream.readByteValue()` (`Stream.surtr:65-70`), que sí lanza
`InvalidOperationException` ante EOF.

**Arreglo propuesto:** cada lectura multi-byte debe comprobar cada byte individual, no solo el
primero, y lanzar (p. ej. una nueva `EndOfStreamException`, ver propuesta §3.9) en cuanto cualquiera
de ellos indica EOF a mitad de un valor. `readBytes`/`readString` deberían devolver el número real de
bytes leídos o truncar el resultado, nunca dejar cola de ceros sin marcar.

#### B4 — `ReadOnlySet.copyTo` lanza en un caso válido
**Archivo:** `src/surtr/collections/Set.surtr:114-119`

```surtr
public fun copyTo(array : T[], arrayIndex : int): void
{
    if (arrayIndex < 0 || arrayIndex >= array.length)
        throw IndexOutOfRangeException("arrayIndex is out of range.");
    ...
```

Si el set está vacío y se copia en un array vacío con `arrayIndex = 0`, `0 >= array.length` (`0 >=
0`) es `true` y lanza, aunque no hay nada que copiar. `List.copyTo` (`List.surtr:115-121`) no tiene
este problema porque solo comprueba `arrayIndex < 0`.

**Arreglo propuesto:** alinear la condición con `List.copyTo` — comprobar solo `arrayIndex < 0`, y
dejar que la comprobación de espacio disponible (ya presente en la línea siguiente) cubra el resto.

### 2.3 Inconsistencias de diseño — Prioridad MEDIA

#### D1 — `List<T>` no tiene `operator[]`
**Archivo:** `src/surtr/collections/List.surtr:16`

`LinkedList<T>` (línea 422-423) y `StringBuilder` (línea 54) sí declaran `operator[]`; la colección
más usada de la stdlib, no. Hoy `xs[i]` no funciona sobre un `List<int>` y hay que escribir
`xs.get(i)`/`xs.set(i, v)`.

**Arreglo propuesto:** añadir
`inline operator [](self: List<T>, index: int): T => self.get(index);` y el setter equivalente.

#### D2 — `Deque<T>.dequeueBack()` es O(n)
**Archivo:** `src/surtr/collections/Queue.surtr:187-212`

`Queue<T>.Node<T>` (línea 111) solo tiene `next` (lista simplemente enlazada). `Deque.dequeueBack()`
recorre toda la lista para encontrar el penúltimo nodo. Un tipo llamado "deque" cuya mitad de
operaciones es O(n) no cumple lo que promete su nombre — la implementación de referencia (`.NET`,
`std::deque`) es O(1) en ambos extremos.

**Arreglo propuesto (dos caminos, ver pregunta en §5):**
- (a) Convertir `Queue<T>.Node<T>` en doblemente enlazado (añadir `prev`), lo que hace `Queue` y
  `Deque` O(1) en todo — cambia la clase base que ambos comparten.
- (b) Dar a `Deque<T>` su propia lista doblemente enlazada independiente de `Queue<T>`, sin tocar la
  base — más código, cero riesgo sobre `Queue`.

#### D3 — Orden de iteración de `Stack<T>` no es LIFO
**Archivo:** `src/surtr/collections/Stack.surtr:65`

`Iterator<T>(_items, _items.length)` recorre `_items` de índice `0` a `length-1`, es decir, en orden
de **inserción** (FIFO), no en el orden de `pop()` (LIFO) que la mayoría de lenguajes usa al iterar
un stack (p. ej. `System.Collections.Generic.Stack<T>` en .NET itera top-to-bottom). No es
necesariamente un "bug", pero sorprende a cualquiera que espere semántica de pila al hacer
`for (x in stack)`.

**Arreglo propuesto:** iterar `_items` en orden inverso (`_index` empezando en `_items.length` y
decrementando), o documentar explícitamente que la iteración es en orden de inserción si se decide
mantenerla así.

#### D4 — Falta una `ObjectDisposedException` dedicada
**Archivo:** `src/surtr/io/Stream.surtr`, `BufferedStream.surtr`, `MemoryStream.surtr`,
`StreamReader.surtr`, `StreamWriter.surtr`, `BinaryReader.surtr`, `BinaryWriter.surtr`

Todos estos tipos comprueban `_isOpen`/`_stream != null` y lanzan `InvalidOperationException("...
is closed")` a mano, repitiendo el mismo mensaje en siete sitios distintos. Dado que `IDisposable`
es un concepto central del lenguaje (`CLAUDE.md` le dedica una sección propia), tener una excepción
dedicada permitiría a quien llama distinguir "usé esto después de cerrarlo" de cualquier otro
`InvalidOperationException`, con un `catch` específico.

**Arreglo propuesto:** añadir `ObjectDisposedException : Exception` en `core/Exception.surtr` (o un
nuevo `core/IOExceptions.surtr`) y sustituir los siete lanzamientos manuales.

#### D5 — Inconsistencia menor: `reset()` en iteradores
`Queue.Iterator` (`Queue.surtr:164-168`) declara `reset()`, que **no** forma parte del contrato
`IIterator<T>` (solo `moveNext`/`current`/`dispose`, ver `SurtrStandardLibrary.cs:132-133`).
`Stack.Iterator`, `List.Iterator`, `Set.Iterator` y `LinkedList.Iterator` no lo tienen. No es un bug
— es simplemente inconsistente entre iteradores hermanos y probablemente vestigial.

**Arreglo propuesto:** quitar el `reset()` suelto de `Queue.Iterator` (nadie puede llamarlo a través
de `IIterator<T>` de todas formas), o si se quiere ese comportamiento, ofrecerlo de forma consistente
en todos los iteradores propios de la stdlib.

### 2.4 Código muerto — Prioridad BAJA/MEDIA

#### C1 — `Buffer<T>` (core/Buffer.surtr) no está conectado a nada
**Archivo:** `src/surtr/core/Buffer.surtr`

El comentario de cabecera dice "la implementación concreta vive en el host (`bytes`, la clase
built-in)", pero `bytes` (`SurtrBuiltIns.Declare("bytes", ...)`,
`src/Surtr.Core/Runtime/BuiltIns/SurtrBuiltIns.cs:465`) es una clase de profundidad 0 sin relación
con `Buffer<T>` — no lo extiende ni lo implementa. Además `Buffer<T>` está incompleto frente a lo
que `bytes` realmente ofrece (le faltan `capacity`, `reserve`, `truncate`, que sí existen en
`SurtrBytesBuiltIn.cs`).

**Propuesta:** o se conecta de verdad (documentando qué tipo de usuario lo implementaría — p. ej.
un buffer definido en Surtr puro), o se elimina. Tal como está, es documentación engañosa disfrazada
de código.

#### C2 — `ReadOnlyCollection<T>` (Collection.surtr) es código muerto
**Archivo:** `src/surtr/collections/Collection.surtr:20-50`

Declarada `private`, no la usa nada dentro del propio `Collection.surtr`, y ni `List` ni `Set` la
usan para ofrecer un `asReadOnly()`. `ReadOnlyList`/`ReadOnlySet` son implementaciones
**independientes**, no envoltorios sobre esta clase.

**Propuesta:** o se hace pública y se cablea un `asReadOnly(): IReadOnlyCollection<T>` en `ICollection<T>`
que la use de verdad (propuesta detallada en §3.4), o se elimina.

#### C3 — Código comentado en `Set.of`
**Archivo:** `src/surtr/collections/Set.surtr:246`

```surtr
//public static inline fun of(items: T...): Set<T> => Set<T>(items);
```

`Set.of(...)` solo cubre 0–3 elementos a mano; la variante varargs está comentada, probablemente
porque no se puede reenviar un parámetro varargs ya recogido a otro varargs sin "desempaquetarlo".
Dejar código comentado en la stdlib publicada no es buena práctica.

**Propuesta:** o se investiga si el lenguaje permite reenviar varargs de alguna forma y se activa,
o se documenta con un comentario claro por qué el límite es 3, o se elimina la línea muerta.

### 2.5 Documentación desactualizada — Prioridad MEDIA

#### E1 — `src/Surtr.Stdlib/README.md` describe una stdlib que ya no existe
- Lista solo 8 módulos; hoy hay 25 archivos `.surtr`.
- Describe `surtr.core.Contracts` con contenido "`IDisposable<T>`" en `src/surtr/core/Contracts.surtr`
  — ese archivo **no existe**. Lo que existe es `src/surtr/diagnostics/Contracts.surtr`, con contenido
  completamente distinto (`PreconditionException`/`PostconditionException`/`InvariantException` y sus
  helpers `require`/`ensure`/`invariant`).
- Referencia `surtr.collections.Collections` (con "s") en `src/surtr/collections/Collections.surtr`,
  que nunca se llegó a crear (el archivo real es `Collection.surtr`, singular).
- No menciona ninguno de los módulos de `diagnostics/` (`Assert`, `Debug`, `Log`, `Profiler`,
  `RuntimeInfo`), ni la mayoría de `io/`, ni `Queue`, `Set`, `Stack`, `Sequence`, `byte`.

Por la propia norma de `CLAUDE.md` ("un doc que contradice al código es peor que no tener doc"),
esto necesita una pasada de actualización — sea a mano o generándolo desde la lista real de módulos.

---

## 3. Propuestas de mejora y adición (detalle)

### 3.1 Librería de geometría — `Vector2`/`Vector3`/`Vector4`/`Quaternion`/`Color`/`Rect`

**Por qué:** Surtr se define explícitamente como alternativa a Lua embebida en Unity
(`CLAUDE.md`, primera sección). Prácticamente todo script de gameplay necesita vectores; hoy no
existe ninguno. `Angle` (`src/surtr/math/Angle.surtr`) sugiere que se empezó una librería de
geometría y se abandonó — es un `value class` con un único campo `_radians` y un getter, sin
operadores, sin `fromDegrees`/`toDegrees`, sin `normalize`, sin `lerp`.

**Propuesta concreta:**
- Nuevo módulo `surtr.math.Vector` con `Vector2`/`Vector3` como `value class` (igual que `Angle`,
  se benefician de layout inline sin heap). Superficie mínima por tipo: constructor, `x`/`y`(`/z`),
  `+`/`-`/`*` (escalar)/`/` (escalar), `==`, `length()`/`lengthSquared()`, `normalized()`,
  `dot(other)`, y para `Vector3` también `cross(other)`. Estáticos: `zero`, `one`, `up`, `right`
  (y `forward` para `Vector3`), `distance(a, b)`, `lerp(a, b, t)`.
- `Vector4` solo si hay demanda real (color/homogéneas) — no es tan universal como `Vector2`/`3`.
- `Quaternion` en el mismo módulo o en `surtr.math.Quaternion`: constructor, `identity`,
  `fromAxisAngle(axis: Vector3, angle: Angle)`, `*` (composición), `*` sobre `Vector3` (rotar un
  punto), `slerp`.
- Completar `Angle`: operadores `+`/`-`/`*` (escalar), `fromDegrees(float)`/`toDegrees(): float`,
  `normalized()` (envolver a `[0, 2π)` o `(-π, π]`), `<=>`/`==`.
- `Color`/`Rect` son más opcionales — propondría dejarlos para una segunda pasada una vez que
  Vector/Quaternion estén validados, porque su forma depende mucho de cómo el host (Unity) los va a
  interoperar (¿`Color` con floats 0-1 o bytes 0-255? ¿`Rect` con `x,y,w,h` o `min,max`?).

**Coste/riesgo:** todo esto es Surtr puro (sin `native`), salvo quizá `sqrt` para `length()`, que ya
existe en `surtr.math.Math`. Bajo riesgo, alto valor.

### 3.2 `Random` / generador de números pseudoaleatorios

**Por qué:** ningún juego funciona sin aleatoriedad controlable (con semilla, para reproducibilidad
de tests/replay). Hoy no existe absolutamente nada en la stdlib para esto.

**Propuesta concreta:** nuevo módulo `surtr.math.Random`, clase `Random`:
- Constructor `Random(seed: int)` y `Random()` (semilla derivada de tiempo/entropía del host vía un
  `native fun`, similar a como `Math.surtr` importa `sin`/`cos` nativos).
- Algoritmo determinista puro-Surtr (p. ej. xorshift/PCG de 32 o 64 bits) para que **la misma
  semilla dé la misma secuencia en cualquier plataforma** — el mismo principio que ya se aplica a
  `array.sort` (mergesort propio en vez de delegar al BCL) y a `SurtrString.ComputeHash`.
- Superficie: `nextInt()`, `nextInt(max: int)`, `nextInt(min: int, max: int)`, `nextFloat()`
  (`[0,1)`), `nextFloat(min, max)`, `nextBool()`, `nextBool(probability: float)`.
- Solo necesita ser `native` si se quiere una fuente de entropía real para la semilla por defecto;
  el propio PRNG puede (y debería, por determinismo) ser Surtr puro.

### 3.3 `PriorityQueue<T>` (heap binario)

**Por qué:** encaja con el patrón ya establecido `Stack`/`Queue`/`Deque` en `collections/`, y es
casi obligatorio para pathfinding (A*), colas de eventos con timestamp, sistemas de turnos, etc. —
todos casos de uso típicos de un motor de juego.

**Propuesta concreta:** `src/surtr/collections/PriorityQueue.surtr`, interfaz
`IPriorityQueue<T> default PriorityQueue<T> : ICollection<T>` con `enqueue(item: T, priority:
float)`, `dequeue(): T` (extrae el de menor prioridad), `peek(): T`, siguiendo exactamente el mismo
patrón `each`/interfaz-con-`default` que ya usan `Stack`/`Queue`/`List`/`Set`. Implementación: array
binario heap (como `List<T>` internamente), sin necesidad de nodos enlazados.

### 3.4 `IMap<K,V>`/`Map<K,V>` para paridad con el resto de `collections/`

**Por qué:** `collections/` sigue sistemáticamente el patrón `IReadOnlyX<T>`/`IX<T>` + `default` para
`List`, `Set`, `Stack`, `Queue` — pero no hay ningún wrapper equivalente para diccionarios. El
built-in `dict<K,V>` ya cubre lo básico (`containsKey`, `remove`, `keys()`, `values()`, indexación),
así que esto es más "consistencia de API" que una necesidad urgente.

**Propuesta concreta:** `IReadOnlyMap<K,V>`/`IMap<K,V>` en `collections/Map.surtr`, con una clase
`Map<K,V>` que envuelva un `{K:V}` igual que `Set<T>` envuelve un `{T:bool}` — dando acceso a
`ICollection`-style (`add`/`remove`/`clear`/`copyTo`/`iterate` sobre pares `(K,V)`) donde hoy solo
existe el `dict<K,V>` crudo. Prioridad baja frente a Vector/Random: es "bonito tenerlo", no un hueco
que bloquee casos de uso reales.

**Aprovechar C2 aquí:** este sería el sitio natural para resucitar `ReadOnlyCollection<T>`
(hallazgo C2) como base de `asReadOnly()` en `Map`, `List` y `Set` a la vez.

### 3.5 Ampliaciones a `List<T>`

Además de `operator[]` (D1, ya clasificado como inconsistencia a corregir), añadiría:
- `sort(comparator: (T, T) -> int): void` — envolver `_items.sort(comparator)` (ya existe en el
  array built-in, `SurtrCompositeBuiltIns.cs:76-80`) recortado a `_length`. Hoy solo se puede
  ordenar una `List` pasando por `asSequence().sorted(cmp).toList()`, mucho más caro.
- `reverse(): void` in-place.
- `addRange(items: IIterable<T>): void`.
- `lastIndexOf(item: T): int`.
- `toArray(): T[]` propio (copia directa de `_items[0.._length]`) en vez de depender de la extensión
  genérica de `Sequence`, que pasa por un generador.

### 3.6 Ampliaciones a `StringBuilder` (una vez arreglado B1)

- `insert(index: int, value: string): StringBuilder`.
- `remove(start: int, length: int): StringBuilder`.
- `replace(start: int, length: int, value: string): StringBuilder`.
- `indexOf(value: char): int` / `indexOf(value: string): int`.
- `substring(start: int, length: int): string`.
- `capacity: int { get; }` expuesta (separada de `length`, una vez exista de verdad esa distinción).
- Setter de `operator[]` (hoy solo hay getter en la línea 54).

### 3.7 Ampliaciones a `Sequence<T>`

Es el módulo mejor diseñado de la stdlib (generadores perezosos + `dispose()` correcto vía
`try/finally` en cada combinador) — las siguientes son adiciones naturales sobre el mismo patrón, no
correcciones:
- `min(comparator)`/`max(comparator)`, `sum()` (int/float), `average(): float`.
- `groupBy<K>(keySelector: (T) -> K): Map<K, List<T>>` (depende de que exista `Map<K,V>`, §3.4).
- `distinctBy<K>(keySelector: (T) -> K): Sequence<T>`.
- `sortBy<K>(keySelector: (T) -> K): Sequence<T>` / `sortByDescending`.
- `joinToString(separator: string): string`.
- `elementAt(index: int): T`, `last(): T`, `lastOrNull(): T?`.

### 3.8 `ObjectDisposedException` (detalle de D4)

Ya cubierto en D4 — lo repito aquí solo para que quede junto al resto de "añadir", ya que a
diferencia de D1-D3 esto es puramente aditivo (una clase nueva), no una reescritura de algo
existente.

---

## 4. Plan por fases

El orden respeta dependencias reales (no se puede escribir un test de regresión de `StringBuilder`
fiable hasta arreglarlo; no tiene sentido diseñar `Map<K,V>` antes de decidir si se quiere) y separa
"arreglar lo que hay" de "añadir lo que falta", que son decisiones independientes.

**Fase 0 — Red de seguridad**
Antes de tocar nada: comprobar si existen tests actuales (`src/Surtr.Tests`) que ejerciten
`StringBuilder`/`Profiler`/`BinaryReader`/`Set.copyTo` y que puedan estar afirmando el comportamiento
roto como si fuera correcto (un test que hoy pase con `sbLength() == 16` se rompería al arreglar B1,
lo cual sería lo correcto, pero hay que saberlo de antemano). Añadir tests de regresión para B1-B4
*antes* del arreglo, en rojo, luego arreglarlos en verde.

**Fase 1 — Bugs críticos (B1, B2)**
`StringBuilder` y `Profiler`/`Stopwatch`. Alto impacto, arreglo localizado a un archivo cada uno, sin
decisiones de diseño abiertas.

**Fase 2 — Bugs de alta confianza (B3, B4)**
`BinaryReader` (requiere decidir la forma exacta de señalar EOF a mitad de lectura — nueva excepción
vs. valor de retorno) y `Set.copyTo`.

**Fase 3 — Inconsistencias de diseño (D1, D3-D5; D2 pendiente de decisión)**
`operator[]` en `List`, orden de iteración de `Stack`, `ObjectDisposedException`, limpieza de
`reset()`. `D2` (Deque O(n)) depende de la pregunta 3 de §5.

**Fase 4 — Limpieza (C1-C3, E1)**
Decidir destino de `Buffer<T>` y `ReadOnlyCollection<T>` (conectar o eliminar), quitar código
comentado, reescribir `README.md` para reflejar los 25 módulos reales.

**Fase 5 — Adiciones de alto valor (§3.1, §3.2)**
`Vector2`/`Vector3`/`Quaternion` + `Angle` completo, y `Random`. Son independientes entre sí y
pueden ir en paralelo. Requieren una pasada de diseño de API antes de escribir código (ver pregunta
4 de §5).

**Fase 6 — Adiciones de valor medio (§3.3, §3.4)**
`PriorityQueue<T>` y `Map<K,V>`/`IMap<K,V>` (esta última reutiliza `ReadOnlyCollection<T>` si se
decidió conservarla en la Fase 4).

**Fase 7 — Ampliaciones incrementales (§3.5-§3.7)**
Métodos añadidos a `List`, `StringBuilder` y `Sequence` una vez sus bases respectivas están
corregidas/estables. Es la fase con menos urgencia y se puede hacer incremental, método a método.

---

## 5. Preguntas abiertas

Antes de empezar a implementar nada de lo anterior:

1. **Alcance de esta primera tanda de trabajo** — ¿solo los bugs confirmados (Fases 0-1), bugs +
   inconsistencias (Fases 0-3), o todo el plan incluyendo las adiciones nuevas?
2. **Dónde vive este documento** — ¿se queda como archivo de trabajo sin commitear, se comitea tal
   cual en `docs/` siguiendo la convención `Plan-*.md` ya existente, o prefieres que además lo añada
   a la tabla de "mapa de documentación" de `CLAUDE.md`?
3. **Rediseño de `Deque<T>` (D2)** — ¿conviene convertir `Queue<T>.Node<T>` a doblemente enlazado
   (afecta a `Queue` y `Deque` a la vez, todo O(1)) o dar a `Deque` su propia lista enlazada
   independiente sin tocar `Queue`?
4. **Vector/Quaternion/Random (Fase 5)** — ¿quieres que primero prepare y valide contigo el diseño
   de la API (nombres, superficie exacta, qué constantes/estáticos incluir) antes de escribir
   código, o prefieres que implemente directamente una primera versión razonable y la revisamos
   después?
