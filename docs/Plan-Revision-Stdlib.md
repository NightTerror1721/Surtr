# Plan-Revision-Stdlib — Auditoría de `src/Surtr.Stdlib` y propuestas

> **Estado:** Fases 0-4 implementadas (B1-B4, D1-D5, C1-C3, E1). Durante la Fase 4 apareció un
> **hallazgo nuevo y de prioridad más alta que todo lo anterior**: B5, un bug del *compilador* (no
> de la stdlib) que rompía en producción toda la superficie de álgebra de conjuntos de
> `Set<T>`/`ReadOnlySet<T>` para cualquier tipo de elemento — **ya corregido** (ver §2.0). Las Fases
> 5-7 (vectores/`Random`/`PriorityQueue`/`Map`) ya no están bloqueadas. Nace de una revisión
> manual de los 25 archivos `.surtr` de la stdlib (~3500 líneas), con hallazgos verificados
> compilando y ejecutando código real contra el runtime (`surtrc build`/`surtr run`, y el arnés de
> `SurtrCompilation` que ya usa `src/Surtr.Tests`), no solo por lectura. Cada arreglo de las Fases
> 0-4 tiene una prueba de regresión en `src/Surtr.Tests/Stdlib/SurtrStdlibBehaviorTests.cs`, que
> compila y ejecuta la fuente `.surtr` real (no la imagen `.surtrc` comitada) junto a un driver, así
> que confirma el comportamiento en vez de solo la compilación.

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

### 2.0 B5 — Bug del compilador: interfaces genéricas declaradas en Surtr rompen la VM — Prioridad CRÍTICA MÁXIMA — **Corregido**

**No era un bug de la stdlib** — vivía en `Surtr.Compiler` (el puente que emite `CodeGen/ModuleEmitter.cs`
para satisfacer una interfaz genérica), pero **rompía funcionalidad real y ya publicada de la
stdlib**, así que se documenta aquí primero.

**Síntoma:** llamar a un método a través de una referencia tipada por una **interfaz genérica
declarada en Surtr** (no una built-in como `IComparable<T>`/`IEquatable<T>`), cuando ese método toma
como parámetro el propio parámetro de tipo de la interfaz, revienta la VM con
`InvalidCastException: A '<Clase>' cannot be cast to 'erased'` — **para cualquier tipo de elemento**,
primitivo o referencia.

**Repro mínimo** (fuera de la stdlib, para aislar que no es un problema de `Set`):

```surtr
interface IHolder<T> { fun has(item: T): bool; }

class Box<T> : IHolder<T>
{
    private let _v: T;
    public constructor(v: T) { _v = v; }
    public fun has(item: T): bool => _v == item;
}

fun run(): bool
{
    let b: IHolder<int> = Box<int>(5);
    return b.has(5);   // revienta: "A 'int' cannot be cast to 'erased'"
}
```

Confirmado también con `T = string` (falla igual, así que no es específico de primitivos que
necesiten boxing) y confirmado que **calling a través del tipo concreto** (`Box<int>(5).has(5)`,
sin pasar por `IHolder<int>`) **funciona bien** — el fallo es específicamente del *dispatch por
interfaz*. Contrastado con el test ya existente
`ModuleEmitterTests.AnIntCastToIComparableCallsCompareToThroughTheInterface`
(`src/Surtr.Tests/Compiler/CodeGen/ModuleEmitterTests.cs:6768`), que hace lo mismo con la interfaz
**built-in** `IComparable<T>` y sí funciona — la diferencia parece estar en que las interfaces
built-in tienen manejo especial en C# que nunca se extendió a las declaradas en código Surtr.

**Causa raíz confirmada** (con debugging real: se instrumentó temporalmente el binder y se
inspeccionó el bytecode emitido para el repro): cuando una clase genérica (`Box<T>`) satisface una
interfaz genérica (`IHolder<T>`), el compilador sintetiza un **puente** (`ModuleEmitter.EmitBridges`/
`EmitBridge`, `src/Surtr.Compiler/CodeGen/ModuleEmitter.cs`) — un método `virtual` con la firma
erasionada exacta que pide el slot de la interfaz (`has(item: unknown)`), cuyo cuerpo reenvía al
método real de la clase (`has(item: T)`, con dispatch `Direct`). Ese reenvío tiene que leer el
argumento erasionado de vuelta al tipo que el método real declara — eso es lo que hace `Narrow`
(`ModuleEmitter.cs:1693`). El problema: **una clase genérica mantiene un único cuerpo compilado sea
cual sea su instanciación (§6)**, así que su propio parámetro de tipo (`T`) sigue erasionado *dentro*
de ese cuerpo — es exactamente la misma representación que ya traía el slot de la interfaz. `Narrow`
no tenía ningún caso para "el destino ya es él mismo un parámetro de tipo genérico sin instanciar";
caía en la rama general de "convertir a un tipo concreto" (`ModuleEmitter.cs:1734`, antes de este
arreglo), que emitía un `Cast` real contra el descriptor de `T` — que resuelve exactamente a la
misma clase marca `SurtrBuiltIns.Erased` que el slot ya tenía. En runtime, el opcode `Cast` comprueba
`subjectClass.IsSubclassOf(target)` (`SurtrVirtualMachine.cs:3608-3616`), y ninguna clase real es
jamás "subclase de" la marca `Erased` — por lo que la comprobación fallaba siempre, para cualquier
tipo de elemento.

**Impacto real que tenía en la stdlib ya publicada**, confirmado ejecutando el código antes del
arreglo:
- `Set<int>.isSubsetOf(other)` (y toda `IReadOnlySet<T>`/`ISet<T>`: `isSupersetOf`,
  `isProperSubsetOf`, `isProperSupersetOf`, `overlaps`, `equals`, `unionWith`, `intersectWith`,
  `exceptWith`, `symmetricExceptWith`, y los estáticos `union`/`intersect`/`except`/
  `symmetricExcept`) **reventaba la VM** en cuanto llamaban a `other.contains(item)` a través del
  parámetro `IReadOnlySet<T>`/`ISet<T>` — confirmado con `T = int` y `T = string`, universal, no
  dependía del tipo de elemento.
- `ReadOnlyCollection<T>`/`asReadOnly()` (C2, más abajo) heredaba el mismo problema en su
  `contains()`.

**Arreglo aplicado:** `Narrow` gana un caso al principio — si el tipo destino ya es él mismo un
`TypeParameterSymbol` (el parámetro de tipo de la clase contenedora, sin instanciar), no emite nada:
ni `Cast` ni `Unbox`, porque el valor ya está exactamente en la representación que el cuerpo real
espera. Confirmado con el repro mínimo (`IHolder<T>`/`Box<T>`, ambas rutas — dispatch directo y por
interfaz) y con el caso real (`Set<int>.isSubsetOf`, `Set<string>.isSubsetOf`). Tests:
`ModuleEmitterTests.AUserDeclaredGenericInterfaceDispatchesAMethodTakingItsOwnTypeParameter`
(`src/Surtr.Tests/Compiler/CodeGen/ModuleEmitterTests.cs`) para el repro aislado, y
`SurtrStdlibBehaviorTests.SetIsSubsetOfWorksAcrossTwoInstances` /
`ListAndSetAsReadOnlyStayLiveOverTheSource` (ahora también prueba `contains()` a través de la
interfaz) para el impacto real en la stdlib. Suite completa: 3309/3309 en verde.

**Por qué esto pausaba las Fases 4 (resto)-7 hasta arreglarse:** cualquier interfaz genérica nueva
declarada en Surtr que se llamase a través de su propio tipo (`PriorityQueue`'s `IPriorityQueue<T>`,
`Map`'s `IReadOnlyMap<K,V>`, cualquier operador de `Vector2`/`Quaternion` que recibiera
`IEquatable<Vector2>`, etc.) habría chocado con el mismo bug. Con el compilador arreglado, esa
restricción ya no aplica — las Fases 5-7 pueden retomarse sin esa limitación.

#### B1 — `StringBuilder` produce contenido corrupto desde su construcción — **Corregido**
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

#### B2 — `Profiler`/`Stopwatch` no miden tiempo real — **Corregido**
**Archivo:** `src/surtr/diagnostics/Profiler.surtr`

- `Stopwatch.start()`/`stop()`/`restart()` (líneas 22-42) solo cambian el flag `_running`; `_elapsed`
  solo cambia si alguien llama manualmente a `addElapsed(delta)`. Hay un `native fun
  stopwatchTimestamp(): float` declarado (línea 59) que **nadie invoca** desde dentro de `Stopwatch`.
- `ProfilerEntry.elapsed` (línea 136) es un `let` fijado a `0.0` en el constructor y nunca
  reasignado tras `stopwatch.stop()` en `ProfilerScope.dispose()` (línea 76-79).

**Evidencia empírica:** un `Profiler` que envuelve un bucle de 1.000.000 de iteraciones dentro de
`beginScope(...)`/`scope.dispose()` devuelve `getEntry(0).elapsed == 0`.

**Arreglo aplicado:**
- `Stopwatch.start()`/`restart()` capturan `stopwatchTimestamp()` en un nuevo campo `_startedAt`;
  `stop()` y la propiedad `elapsed` calculan `_elapsed + (running ? stopwatchTimestamp() -
  _startedAt : 0)`.
- `ProfilerEntry.elapsed` dejó de ser un `let` fijado una vez y pasó a ser una propiedad computada
  que lee `stopwatch.elapsed` directamente — no queda ningún "olvidé actualizarlo" posible, porque
  no hay copia que actualizar. `ProfilerScope.dispose()` no necesitó cambios: ya llamaba a
  `_stopwatch.stop()`, que ahora sí calcula algo real.
- Test de regresión: `ProfilerScopeMeasuresRealElapsedTime`, `StopwatchElapsedGrowsWhileRunning`.

### 2.2 Bugs — Prioridad ALTA (alta confianza, por inspección de código)

#### B3 — `BinaryReader` corrompe silenciosamente lecturas truncadas — **Corregido**
**Archivo:** `src/surtr/io/BinaryReader.surtr`

`readChar()` (línea 31), `readInt()` (línea 40), `readBytes()` (línea 50) y `readString()` (línea 63)
comprueban EOF **solo en el primer byte** de una lectura multi-byte (`if (b0 < 0) return 0;`). Si el
stream se agota a mitad de una lectura (p. ej. un `int` con 2 de 4 bytes disponibles), el resultado
es basura silenciosa (bytes de EOF, típicamente `-1`, mezclados por desplazamiento de bits) en vez de
una señal de error. Además `readBytes`/`readString` devuelven un buffer del tamaño pedido aunque se
haya leído menos, sin forma de que el llamador distinga "leí todo" de "leí menos de lo pedido".

Esto es inconsistente con el propio `Stream.readByteValue()` (`Stream.surtr:65-70`), que sí lanza
`InvalidOperationException` ante EOF.

**Arreglo aplicado:** `readChar`/`readInt` siguen devolviendo el valor "en blanco" (`char(0)`/`0`) en
un EOF limpio antes del primer byte, pero lanzan la nueva `EndOfStreamException` (declarada en
`Stream.surtr`, §3.9) en cuanto un byte posterior indica EOF a mitad de valor. `readBytes` trunca el
resultado a lo realmente leído (igual que `Stream.read`); `readString` lanza siempre que falte
cualquier byte de los `len` declarados, porque ahí un corte a mitad no tiene lectura legítima
posible. Tests: `ReadIntThrowsOnATruncatedStreamInsteadOfReturningGarbage`,
`ReadIntAtCleanEofReturnsZero`, `ReadBytesPastEndOfStreamReturnsOnlyWhatWasRead`.

#### B4 — `ReadOnlySet.copyTo` lanza en un caso válido — **Corregido**
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

**Arreglo aplicado:** alineada la condición con `List.copyTo` — solo comprueba `arrayIndex < 0`.
Test: `CopyingAnEmptySetIntoAnEmptyArrayDoesNotThrow`.

### 2.3 Inconsistencias de diseño — Prioridad MEDIA

#### D1 — `List<T>` no tiene `operator[]` — **Corregido**
**Archivo:** `src/surtr/collections/List.surtr:16`

`LinkedList<T>` (línea 422-423) y `StringBuilder` (línea 54) sí declaran `operator[]`; la colección
más usada de la stdlib, no. Hoy `xs[i]` no funciona sobre un `List<int>` y hay que escribir
`xs.get(i)`/`xs.set(i, v)`.

**Arreglo aplicado:** añadido `inline operator [](self: List<T>, index: int): T => self.get(index);`
y el setter equivalente, tras `iterate()`. Test: `ListSupportsIndexerReadAndWrite`.

#### D2 — `Deque<T>.dequeueBack()` es O(n) — **Corregido**
**Archivo:** `src/surtr/collections/Queue.surtr:187-212`

`Queue<T>.Node<T>` (línea 111) solo tiene `next` (lista simplemente enlazada). `Deque.dequeueBack()`
recorre toda la lista para encontrar el penúltimo nodo. Un tipo llamado "deque" cuya mitad de
operaciones es O(n) no cumple lo que promete su nombre — la implementación de referencia (`.NET`,
`std::deque`) es O(1) en ambos extremos.

**Arreglo aplicado:** opción (b) — `Deque<T>` pasó a ser una implementación independiente con su
propio `Node<T>` doblemente enlazado (`prev`+`next`), implementando `IDeque<T>` directamente en vez
de extender `Queue<T>`. `Queue<T>` no se tocó (sigue con su `Node<T>` de un solo enlace, que le
basta); se le quitó el helper `makeNode`, que solo existía para que el antiguo `Deque : Queue<T>` lo
usara y quedó sin llamadas. Efecto secundario aceptado: `Deque<T>` ya no es subtipo de la clase
concreta `Queue<T>` (solo de `IQueue<T>`/`IDeque<T>`, que sigue implementando por completo). Tests:
`DequeWorksAtBothEndsAndThroughTheCollectionContract`, `QueueStillWorksAfterDequeWasSeparatedFromIt`.

#### D3 — Orden de iteración de `Stack<T>` no es LIFO — **Corregido**
**Archivo:** `src/surtr/collections/Stack.surtr:65`

`Iterator<T>(_items, _items.length)` recorre `_items` de índice `0` a `length-1`, es decir, en orden
de **inserción** (FIFO), no en el orden de `pop()` (LIFO) que la mayoría de lenguajes usa al iterar
un stack (p. ej. `System.Collections.Generic.Stack<T>` en .NET itera top-to-bottom). No es
necesariamente un "bug", pero sorprende a cualquiera que espere semántica de pila al hacer
`for (x in stack)`.

**Arreglo aplicado:** `Iterator` ahora arranca en `_index = _items.length` y cuenta hacia abajo hasta
`0` inclusive. Test: `StackIteratesInPopOrder`.

#### D4 — Falta una `ObjectDisposedException` dedicada — **Corregido**
**Archivo:** `src/surtr/io/Stream.surtr`, `BufferedStream.surtr`, `MemoryStream.surtr`,
`StreamReader.surtr`, `StreamWriter.surtr`, `BinaryReader.surtr`, `BinaryWriter.surtr`

Todos estos tipos comprueban `_isOpen`/`_stream != null` y lanzan `InvalidOperationException("...
is closed")` a mano, repitiendo el mismo mensaje en siete sitios distintos. Dado que `IDisposable`
es un concepto central del lenguaje (`CLAUDE.md` le dedica una sección propia), tener una excepción
dedicada permitiría a quien llama distinguir "usé esto después de cerrarlo" de cualquier otro
`InvalidOperationException`, con un `catch` específico.

**Arreglo aplicado:** `ObjectDisposedException : Exception` declarada en `io/Stream.surtr` (no en
`core/Exception.surtr` — así ningún fichero de `io/` necesita un import nuevo, todos ya importan
`surtr.io.Stream`), y los 10 lanzamientos manuales (`MemoryStream` ×5, `BufferedStream` ×3,
`StreamWriter` ×2) sustituidos por ella. De paso se declaró también `EndOfStreamException` ahí mismo,
usada por el arreglo de B3. Test: `ADisposedMemoryStreamThrowsObjectDisposedException`.

#### D5 — Inconsistencia menor: `reset()` en iteradores — **Corregido**
`Queue.Iterator` (`Queue.surtr:164-168`) declara `reset()`, que **no** forma parte del contrato
`IIterator<T>` (solo `moveNext`/`current`/`dispose`, ver `SurtrStandardLibrary.cs:132-133`).
`Stack.Iterator`, `List.Iterator`, `Set.Iterator` y `LinkedList.Iterator` no lo tienen. No es un bug
— es simplemente inconsistente entre iteradores hermanos y probablemente vestigial.

**Arreglo aplicado:** quitado el `reset()` suelto de `Queue.Iterator` (inalcanzable a través de
`IIterator<T>` de todas formas) y limpiada la rama muerta `_current = _current;` de su `moveNext()`.

### 2.4 Código muerto — Prioridad BAJA/MEDIA

#### C1 — `Buffer<T>` (core/Buffer.surtr) no está conectado a nada — **Corregido (eliminado)**
**Archivo:** `src/surtr/core/Buffer.surtr`

El comentario de cabecera decía "la implementación concreta vive en el host (`bytes`, la clase
built-in)", pero `bytes` (`SurtrBuiltIns.Declare("bytes", ...)`,
`src/Surtr.Core/Runtime/BuiltIns/SurtrBuiltIns.cs:465`) es una clase de profundidad 0 sin relación
con `Buffer<T>` — no lo extendía ni lo implementaba. Además estaba incompleto frente a lo que
`bytes` realmente ofrece (le faltaban `capacity`, `reserve`, `truncate`, que sí existen en
`SurtrBytesBuiltIn.cs`).

**Arreglo aplicado:** eliminado el archivo entero. No había ningún tipo, en la stdlib ni en los
tests, que lo implementara o lo nombrara — inventar un caso de uso ficticio para "conectarlo" no
era mejor que quitar documentación engañosa disfrazada de código. Si en el futuro hace falta un
buffer definido en Surtr puro con esta forma, se puede volver a proponer contra un caso de uso real.

#### C2 — `ReadOnlyCollection<T>` (Collection.surtr) es código muerto — **Corregido**
**Archivo:** `src/surtr/collections/Collection.surtr:20-50`

Declarada `private`, no la usaba nada dentro del propio `Collection.surtr`, y ni `List` ni `Set` la
usaban para ofrecer un `asReadOnly()`. `ReadOnlyList`/`ReadOnlySet` son implementaciones
independientes, no envoltorios sobre esta clase.

**Arreglo aplicado:** hecha `public`, y añadido `asReadOnly(): IReadOnlyCollection<T>` como método
concreto en `List<T>` y `Set<T>` (no en la interfaz `ICollection<T>` — eso obligaría a
`Stack`/`Queue`/`Deque` a implementarlo también sin necesidad, solo por compartir la interfaz).
Se simplificó además a un único constructor `(collection: IReadOnlyCollection<T>)`: el segundo
constructor original, `(collection: ICollection<T>)`, era redundante (`ICollection<T>` ya extiende
`IReadOnlyCollection<T>`) y además **nunca fue invocable** — pasar una instancia real de
`List<T>`/`Set<T>` a los dos constructores a la vez resultaba en "no candidate", un bug de
resolución de sobrecarga del compilador con esta forma exacta (interfaz derivada + interfaz base),
reportado aparte (ver el chip de tarea de la sesión). Test:
`ListAndSetAsReadOnlyStayLiveOverTheSource` (cubre `length`/`iterate()`; `contains()` a través de
la vista choca con B5, ver §2.0).

#### C3 — Código comentado en `Set.of` — **Investigado, comentario corregido**
**Archivo:** `src/surtr/collections/Set.surtr:246`

```surtr
//public static inline fun of(items: T...): Set<T> => Set<T>(items);
```

`Set.of(...)` solo cubre 0–3 elementos a mano; la variante varargs estaba comentada. El comentario
original no explicaba por qué.

**Investigación:** se confirmó (probeta fuera de la stdlib, compilando y ejecutando) que Surtr **sí**
permite reenviar un parámetro `T...` ya recogido a otro parámetro `T...` sin desempaquetarlo — la
regla "un candidato no-varargs siempre gana a uno varargs" incluso hace que, en ese caso, la llamada
real resuelva contra el *otro* constructor de `Set<T>` (`(collection: IIterable<T>)`), lo cual
también es correcto. La razón real de que estuviera comentado es otra: `SignatureSet` erosiona un
parámetro varargs igual que erosiona uno singular del mismo tipo, así que `of(items: T...)` y
`of(item: T)` (ya declarado arriba) colisionan en la firma emitida `of(E)` — confirmado con
`SURTR4001` al intentarlo. **Arreglo aplicado:** se dejó sin la sobrecarga varargs (no se puede
tener ambas), pero se sustituyó el comentario por uno que documenta la razón real, para que nadie
vuelva a intentarlo sin saber por qué falla.

### 2.5 Documentación desactualizada — Prioridad MEDIA

#### E1 — `src/Surtr.Stdlib/README.md` describe una stdlib que ya no existe — **Corregido**
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

**Arreglo aplicado:** tabla de módulos reescrita con los 24 archivos reales (23 más el que quedó
tras eliminar `Buffer.surtr` en C1), agrupados igual que `src/surtr/`, y añadida a la tabla de "ya
está en C#" la fila de `Native/SurtrDiagnosticsNative.cs` (`Profiler`/`Debug`/`RuntimeInfo`), que
faltaba por completo.

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

**Fase 0 — Red de seguridad — Hecho**
Se comprobó `src/Surtr.Tests` (incluida `SurtrStdlibTests.cs`, la suite existente de la stdlib): nada
afirmaba el comportamiento roto de B1-B4 como correcto, así que no había riesgo de "arreglar" algo que
un test daba por bueno. Los tests de regresión se escribieron a la vez que cada arreglo (no antes,
dado que ya se sabía qué debían afirmar) en el nuevo
`src/Surtr.Tests/Stdlib/SurtrStdlibBehaviorTests.cs`, que compila y ejecuta la fuente `.surtr` real
junto a un driver — no la imagen `.surtrc` comitada — así que un futuro cambio a estos archivos
vuelve a ejercitar el comportamiento, no solo la compilación. Las 3306 pruebas de la suite completa
(`dotnet test src/Surtr.Tests`) pasan tras las Fases 1-3.

**Fase 1 — Bugs críticos (B1, B2) — Hecho**
`StringBuilder` y `Profiler`/`Stopwatch`. Alto impacto, arreglo localizado a un archivo cada uno, sin
decisiones de diseño abiertas.

**Fase 2 — Bugs de alta confianza (B3, B4) — Hecho**
`BinaryReader` (la forma exacta de señalar EOF a mitad de lectura se decidió por comportamiento: EOF
limpio antes del valor sigue devolviendo el "cero" blando; a mitad de valor lanza
`EndOfStreamException`; ver B3) y `Set.copyTo`.

**Fase 3 — Inconsistencias de diseño (D1-D5) — Hecho**
`operator[]` en `List`, `Deque<T>` reimplementado con lista propia doblemente enlazada (D2, opción
(b) de §5), orden de iteración de `Stack`, `ObjectDisposedException`, limpieza de `reset()`.

**Fase 4 — Limpieza (C1-C3, E1) — Hecho**
`Buffer<T>` eliminado, `ReadOnlyCollection<T>` resucitada con `asReadOnly()`, comentario de
`Set.of` corregido con la razón real (§2.4), `README.md` reescrito con los 24 módulos reales (E1).
Descubierto y **corregido** durante esta fase: **B5** (§2.0), un bug de compilador que rompía
`Set<T>`/`ReadOnlySet<T>` en producción.

**Fase 5 — Adiciones de alto valor (§3.1, §3.2)**
`Vector2`/`Vector3`/`Quaternion` + `Angle` completo, y `Random`. Ya no bloqueadas por B5.

**Fase 6 — Adiciones de valor medio (§3.3, §3.4)**
`PriorityQueue<T>` y `Map<K,V>`/`IMap<K,V>` (reutilizarían `ReadOnlyCollection<T>`, ya disponible
desde la Fase 4). Ya no bloqueadas por B5.

**Fase 7 — Ampliaciones incrementales (§3.5-§3.7)**
Métodos añadidos a `List`, `StringBuilder` y `Sequence` una vez sus bases respectivas están
corregidas/estables. Sigue detrás de las Fases 5-6 en el orden del plan.

---

## 5. Decisiones tomadas y preguntas abiertas

Las cuatro preguntas originales de esta sección ya están resueltas y las Fases 0-4 implementadas
sobre esas respuestas:

1. **Alcance:** Fases 0-3 primero (bugs + inconsistencias), luego, al descubrirse B5, priorizar su
   arreglo antes de seguir con las Fases 5-7.
2. **Documento:** comiteado en `docs/` como `Plan-Revision-Stdlib.md`, sin añadirlo a la tabla de
   mapa de documentación de `CLAUDE.md`.
3. **`Deque<T>` (D2):** opción (b) — lista doblemente enlazada propia, independiente de `Queue<T>`.
4. **Vector/Quaternion/Random (Fase 5):** cuando se retome, primero se diseña y valida la API antes
   de escribir código — pendiente de que se decida entrar en la Fase 5.
5. **B5:** se priorizó arreglar el bug del compilador antes de seguir con las Fases 5-7 — hecho
   (§2.0).

Preguntas que siguen abiertas para cuando se retome cada fase:

- **Fase 5:** confirmar alcance exacto de la superficie de `Vector2`/`Vector3`/`Quaternion`/`Angle`
  antes de diseñar (¿se incluye `Vector4`/`Color`/`Rect` ya, o se dejan para después como sugiere
  §3.1?).
- **Fase 6:** ¿`Map<K,V>` antes o después de `PriorityQueue<T>`? Son independientes entre sí.
