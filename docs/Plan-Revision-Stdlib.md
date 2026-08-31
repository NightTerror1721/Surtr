# Plan-Revision-Stdlib — Auditoría de `src/Surtr.Stdlib` y propuestas

> **Estado (actualizado):** Fases 0-7 completas (ver más abajo). **Fase 8 — auditoría posterior a la
> jerarquía `object`/`Enum`/`ValueType` (§6) — implementada** (§6.4): de las diez propuestas más las
> tres piezas añadidas sobre la marcha (Scheduler de corrutinas, `surtr.io.File`, compilación
> dinámica/`eval` vía el proyecto nuevo `Surtr.Stdlib.Script`), todo salvo P2 (excluido a petición),
> P3 (JSON, sin tiempo) y P8 (sin investigar) quedó implementado y verificado compilando y
> ejecutando contra el runtime real. Cuatro bugs/límites nuevos de compilador o runtime descubiertos
> en el camino (B10, B11, B12, y un límite de `SurtrRuntime.Invoke` con argumentos ya boxeados) —
> ninguno corregido, toda la superficie afectada retirada o evitada antes de comitear nada roto. Ver
> §6.4 para el detalle completo de qué se implementó y qué quedó fuera.
>
> **Estado (histórico, Fases 0-7):** Fases 0-4 completas (B1-B4, D1-D5, C1-C3, E1). Fases 5-7 completas. `Angle` y `Random`
> terminados y utilizables; `Vector2`/`Vector3`/`Quaternion` escritos, matemáticamente correctos,
> probados y ahora también **utilizables por un llamador real entre módulos** — B6 (§2.6), que lo
> bloqueaba, está corregido. Fase 6: `PriorityQueue<T>` y `Map<K,V>` ambas completas y utilizables de
> verdad entre módulos, incluyendo `Map<K,V>` con valores primitivos ahora que B8 está corregido. Fase
> 7 completa (ampliaciones a `List`/`StringBuilder`/`Sequence`). Cinco bugs de compilador/runtime
> nuevos en total esta ronda, **los cinco corregidos**: **B5** (interfaces genéricas declaradas en
> Surtr rompían la VM, §2.0), **B6** (una llamada entre módulos con un retorno `value class`
> multi-campo revienta si no se inlinea — la causa real no era "entrada y salida a la vez" como
> parecía al principio, §2.6), **B7** (un parámetro-tupla de 2+ elementos en un método con dispatch de
> interfaz revienta al emitirse — la causa real vivía en el bridge sintetizado para el método, no en
> el propio método, §2.7), **B8** (un `dict<K,V>` con K y V genéricos simultáneos de la misma clase
> corrompía en silencio los valores primitivos leídos vía `keys()`/`values()`/iteración, §2.8) y **B9**
> (el `T?` de un método genérico instanciado a un primitivo perdía su marca de ausencia, así que
> `resultado == null` daba `false` cuando debía dar `true`, §2.9). B8 y B9 fueron los más serios de
> los cinco mientras estuvieron abiertos: a diferencia de B6/B7 (que revientan ruidosamente en
> compilación), ambos fallaban **en silencio**, devolviendo datos incorrectos sin ningún error. Nace
> de una revisión
> manual de los 25 archivos `.surtr` originales de la stdlib (~3500 líneas), con hallazgos
> verificados compilando y ejecutando código real contra el runtime (`surtrc build`/`surtr run`, y
> el arnés de `SurtrCompilation` que ya usa `src/Surtr.Tests`), no solo por lectura. Cada arreglo
> tiene una prueba de regresión en `src/Surtr.Tests/Stdlib/SurtrStdlibBehaviorTests.cs`, que compila
> y ejecuta la fuente `.surtr` real (no la imagen `.surtrc` comitada) junto a un driver, así que
> confirma el comportamiento en vez de solo la compilación.

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

### 2.6 B6 — Bug del compilador: llamada entre módulos con `value class` multi-campo de entrada Y salida — Prioridad CRÍTICA (bloqueaba Fase 5) — **Corregido**

Descubierto implementando `Vector2`/`Vector3`/`Quaternion` (Fase 5, §3.1). Como B5, **no es un bug
de la stdlib** — vive en `Surtr.Compiler` (emisión de llamadas entre módulos) — pero determina si
`Vector2`/`Vector3`/`Quaternion` son utilizables de verdad desde código real.

**Síntoma:** una llamada (función, operador o método de instancia) que cruza un límite de módulo
revienta con `SURTR4001: Operand stack underflow` en el **llamador**, en cuanto esa llamada **recibe
Y devuelve** una `value class` multi-campo (dos o más campos, que se aplanan a varios slots
contiguos, §2.9) a la vez. El receptor de un método de instancia cuenta como "entrada" a este
efecto.

**Regla exacta, confirmada con cuatro repros mínimos aislados** (compilando dos módulos con
`surtrc build`):

| Entra `value class` multi-campo | Sale `value class` multi-campo | Resultado |
|---|---|---|
| No (solo escalares) | Sí | Funciona |
| Sí | No (sale un escalar) | Funciona |
| Sí | Sí | **Revienta siempre** |
| (cualquiera) | (cualquiera), mismo módulo | Funciona siempre |

El caso que revienta cubre, literalmente, `operator+`, `operator-`, `operator*` por escalar,
`normalized()`, `lerp(a, b, t)`, `rotate(v)` y la composición de quaterniones — es decir, **casi
toda la superficie útil de una API de vectores/quaterniones**. Un repro mínimo de una sola línea ya
lo dispara:

```surtr
// otromodulo.surtr
public value class Vector2 { public let x: float; public let y: float;
    public constructor(x: float, y: float) { this.x = x; this.y = y; } }
public fun scaleIt(a: Vector2, s: float): Vector2 => Vector2(a.x * s, a.y * s);
```
```surtr
// probe.surtr
import otromodulo;
fun run(): float { let v = scaleIt(Vector2(1.0, 2.0), 3.0); return v.x; }
```
falla con `Operand stack underflow at offset 14 in 'run': the instruction pops 2 but the stack holds 1`.

**Impacto real:** un script que `import surtr.math.Vector;` y escriba `a + b` entre dos `Vector2` —
el uso más básico imaginable, y el que cualquier llamador real haría, porque un llamador real vive
por definición en *otro* módulo — revienta al compilar. Confirmado con un test dedicado
(`SurtrStdlibBehaviorTests.VectorArithmeticFromAnotherModuleCurrentlyCrashesOnTheCompilerStackBug`)
que fija este comportamiento roto como conocido. Dentro del propio `Vector.surtr` todo funciona
(mismo módulo), lo que explica por qué pasó desapercibido hasta escribir tests que lo usan desde
fuera, exactamente como lo usaría un script real.

**Causa raíz real (confirmada con debugging directo de la metadata construida — la hipótesis
original, "las dos correcciones de conteo de slots se pisan", apuntaba al sitio correcto (el efecto
de pila de una llamada cross-módulo) pero no a la causa):** no hay ninguna interferencia entre "conteo
de argumento" y "conteo de retorno" — cada uno se calcula de forma completamente independiente. El
problema es que **`SurtrMethodInfo.ResultSlotCount` es una propiedad `virtual` cuya implementación
base es dinámica**: para una `value class`, pregunta `_returnType.ResolvedType` (el `SurtrClass` que
un `SurtrTypeHandle` resuelve a) por su `FlattenedSlotWidth`. Ese `ResolvedType` solo existe una vez
que `SurtrTypeLinker` ha enlazado el módulo — es decir, una vez que un `SurtrRuntime` real ha
ejecutado `LoadModule()` sobre él. Pero B6 se dispara **dentro de una misma compilación**: cuando
`ModuleEmitter` construye `otromodulo` y a continuación emite `probe` (que llama a `otromodulo` de
forma cruzada), ningún `LoadModule()` ha corrido todavía — los dos módulos existen solo como
`SurtrModule`s recién construidos por el propio compilador, sin runtime de por medio. En ese momento,
`EmitResolvedCall` (`MethodBodyEmitter.cs`) resuelve `scaleIt` a través de `_context.Resolve(method)`
y lee `built.ResultSlotCount` directamente sobre esa metadata recién construida — que, al no tener su
handle resuelto todavía, cae al valor por defecto de la ruta dinámica y devuelve **1** para cualquier
`value class` multi-campo, en vez de su anchura real. `Code.CallExternal` usa ese 1 (en vez de 2) para
llevar la cuenta de la pila de la instrucción de llamada — y el emisor, que sí conoce la anchura real
de `Vector2` a través de su propia tabla de símbolos, emite justo después un `StoreValueLocal(index,
2)` (o el equivalente) para guardar el resultado en un local de dos slots — que hace pop de 2 con solo
1 registrado como disponible. De ahí el mensaje exacto (`pops 2 but the stack holds 1`).

Un test de diagnóstico directo (`emitter.Modules[0].TryGetMethods("scaleIt", ...)`, leído **antes**
de cualquier `LoadModule()`) confirmó `ResultSlotCount == 1` para `scaleIt` — y también para
`makeIt(s: float): Vector2` (solo escalar de entrada, `value class` de salida), pese a que la
tabla original de la §2.6 marcaba ese caso como "Funciona". La explicación de esa aparente
contradicción es que **la tabla nunca aisló la variable correcta**: los repros usados para "solo
sale"/"solo entra" (`makeIt`/`sumIt` en esta investigación) son funciones triviales de una sola
expresión — exactamente el tipo de cuerpo que `MethodBodyEmitter.ShouldInlineByCost` decide **esplicar
en el propio call site** en vez de emitir una llamada real, lo cual evita por completo el camino
`CallExternal` donde vive el bug. `scaleIt` (con dos operandos y una construcción) es la primera
función, de las usadas en la caracterización original, que supera el umbral de coste y sí llega a
`CallExternal` — así que **B6 nunca fue realmente sobre "entrada Y salida a la vez"**: es sobre
cualquier retorno `value class` multi-campo cruzando un `CallExternal` real, sin importar la forma de
los parámetros. Confirmado forzando a `makeIt` a NO inlinearse (una versión de cuerpo grande,
`makeItBig`, con solo un `float` de entrada) — revienta exactamente igual que `scaleIt`.

El "argumento" nunca estuvo roto porque `ArgumentSlotCount` **ya tenía** el arreglo que a
`ResultSlotCount` le faltaba: `SurtrMethodBuilder.Build()` ya horneaba (`bakea`) el ancho de argumento
calculado en tiempo de compilación (`_argumentSlots`, puesto por `SetArgumentLayout`) directamente en
la metadata construida (`SurtrBytecodeMethodInfo._argumentSlotCount`), y `ArgumentSlotCount` prefiere
ese valor horneado sobre el cálculo dinámico — con un comentario explícito señalando exactamente por
qué ("Metadata read back from an image carries no baked count and falls through to the declared
shape"). El mismo tratamiento nunca se le dio a `_resultSlots` — `Build()` simplemente no lo pasaba al
constructor de `SurtrBytecodeMethodInfo`, así que el retorno se quedó dependiendo por completo del
camino dinámico, que es exactamente el que no puede responder todavía en este punto de la
compilación.

**Arreglo aplicado en el compilador** (`src/Surtr.Core/Runtime/Classes/SurtrBytecodeMethodInfo.cs` y
`src/Surtr.Core/Bytecode/Emit/SurtrMethodBuilder.cs`): se añadió un parámetro `resultSlotCount` al
constructor de `SurtrBytecodeMethodInfo` (mismo patrón que el ya existente `argumentSlotCount` —
sentinela `-1` cuando no se hornea nada, para no romper el otro sitio que construye esta metadata
directamente desde una imagen en disco, `SurtrModuleImageReader`, donde el ancho real tampoco se
necesita: `MetadataImporter`, el lado del compilador que lee una imagen como referencia, calcula el
ancho desde su propia tabla de símbolos, nunca desde `SurtrMethodInfo.ResultSlotCount`), una propiedad
`ResultSlotCount` que la prefiere sobre la base dinámica, y `SurtrMethodBuilder.Build()` ahora pasa su
propio `_resultSlots` (que `ApplyValueLayout`/`SetResultSlots` ya calculaba correctamente desde el
principio) al construir la metadata. Sin coste de rendimiento: `ResultSlotCount` no se lee nunca desde
`SurtrVirtualMachine.Execute()` — solo desde el propio emisor en tiempo de compilación y desde los
puntos de entrada de host (`SurtrRuntime.Invoke`/`InvokeClosure`/`TryInvoke`), ninguno de los cuales
está en la ruta de dispatch por instrucción.

Verificado con `src/Surtr.Tests/Stdlib/SurtrStdlibBehaviorTests.cs`: el repro mínimo de un solo
`float` de la doc (`VectorArithmeticFromAnotherModuleWorks`, antes
`...CurrentlyCrashesOnTheCompilerStackBug`) ahora compila y devuelve el valor correcto, junto con
cuatro tests nuevos (`CrossModuleCallReturningValueClassWorks`,
`CrossModuleCallTakingValueClassWorks`, `CrossModuleCallTakingAndReturningValueClassWorks`,
`CrossModuleCallReturningValueClassWorksWhenNotInlined`) que fijan la regla real: el argumento nunca
importó, solo si el retorno multi-campo pasa por una llamada real. Los siete tests de
`Vector2`/`Vector3`/`Quaternion` que usaban `BuildAndLoadWithin` (compilar la función de aserción
dentro del propio módulo `surtr.math.Vector` para esquivar B6) vuelven a `BuildAndLoad` normal (un
módulo `test` separado, importando `surtr.math.Vector` como haría cualquier llamador real) — el
propio helper `BuildAndLoadWithin` se eliminó por quedar sin uso. Suite completa: 3377/3377 en verde.

`Quaternion` sigue fusionado en el mismo archivo que `Vector2`/`Vector3` (`surtr.math.Vector`) —
separarlo de vuelta a su propio módulo, `surtr.math.Quaternion`, queda como mejora aparte, no
forzosa: el arreglo del compilador ya lo permite.

Aprovechando la re-verificación, se hizo una pasada de humo más amplia sobre el resto de la stdlib
real (no solo Vector/Quaternion) para comprobar si esa misma tanda de commits había roto algo más:
`byte`, `Vector2`, enums (`LogLevel`, `SeekOrigin`) y `Angle` ejercitados **entre módulos** (el
`equals`/`hashCode`/`toString` que ahora es un override de vtable de verdad) — incluyendo `Set<T>` y
`List<T>` con `byte`, `Vector2` y un enum como elemento, que es exactamente el camino que dependía de
la comparación por identidad antes de que estos tipos tuvieran overrides reales. Los ocho escenarios
probados pasan sin cambios en la stdlib. La suite completa (3332 tests) también sigue en verde. No se
ha encontrado ninguna regresión de esa tanda de commits en la stdlib.

**Hallazgo menor sí encontrado y corregido en el camino (no relacionado con B6/la tanda de
commits, preexistente):** `Quaternion.rotate()` está marcado `@Pure` pero llamaba a `operator+` y
`operator*` de `Vector3`, que no lo estaban — el compilador ya lo señalaba (`warning SURTR3081:
'rotate' is marked @Pure but calls 'op_+'/'op_*', which is not marked @Pure`) pero la advertencia
nunca se veía porque el paso de build de la stdlib (`Surtr.Stdlib.Tool`) solo vuelca diagnósticos
cuando hay un error, no en éxito. Corregido marcando `@Pure` todos los operadores aritméticos y de
comparación de `Vector2`/`Vector3`/`Quaternion` (ya eran puros de hecho — sin efectos secundarios,
solo leen campos y construyen un valor nuevo — simplemente no llevaban la marca), coherente con
`dot`/`cross`/`toString`/`conjugate`, que ya la llevaban. Verificado con una compilación de la stdlib
completa que vuelca todos los diagnósticos: cero warnings tras el arreglo.

### 2.7 B7 — Bug del compilador: parámetro-tupla de 2+ elementos en un método con dispatch por interfaz — Prioridad ALTA — **Corregido**

Descubierto implementando `PriorityQueue<T>`/`Map<K,V>` (Fase 6, §3.3/§3.4). Distinto de B6: B6 es
sobre `value class` **concretas** de varios campos; este es sobre **tuplas** (`(A, B, ...)`), y ni
siquiera hace falta que sus elementos sean genéricos/erasionados — lo que importa es que la tupla
tenga 2 o más elementos y sea el tipo de un **parámetro** (no del receptor, no del retorno) de un
método cuyo dispatch no es `Direct` — es decir, un método que satisface una interfaz.

**Síntoma:** `SURTR4001: Operand stack underflow` al **emitir el propio cuerpo del método**, no en
quien lo llama — falla incluso si nada invoca el método todavía, porque el fallo está en cómo el
emisor calcula el layout de parámetros de un método con dispatch no-`Direct`, no en el call site.

**Regla exacta, confirmada con cinco repros mínimos aislados** (vía `SurtrCompilation`/
`ModuleEmitter`, sin pasar por disco):

| Método... | ...con parámetro tupla de 2+ elementos | ...con parámetro tupla de 1 elemento | ...sin parámetro tupla (solo retorno tupla) |
|---|---|---|---|
| **No** satisface ninguna interfaz (dispatch `Direct`) | Funciona | Funciona | Funciona |
| **Sí** satisface una interfaz (dispatch no-`Direct`) | **Revienta siempre** | Funciona | Funciona |

Repro mínimo (una interfaz, una clase, sin `dict` ni genéricos de por medio):

```surtr
public interface IThing<K, V> { fun contains(item: (K, V)): bool; }
public class Box<K, V> : IThing<K, V> {
    public constructor() { }
    public fun contains(item: (K, V)): bool {
        if (item[0] == item[0]) return true;
        return item[1] == item[1];
    }
}
```
falla con `Operand stack underflow at offset 5 in 'contains': the instruction pops 3 but the stack
holds 2`, sin que nada llame nunca a `contains`.

**Causa raíz real (confirmada con debugging en el emisor — no era la hipótesis original):** el fallo
no está en `EmitReceiverUnpackIfNeeded` ni en el layout de parámetros del **propio** método —
`Box<K,V>` es una clase normal (ancho de receptor 1), así que esa rutina retorna inmediatamente sin
tocar nada. El repro mínimo falla igual con un cuerpo trivial (`return true;`), lo que ya descartaba
cualquier hipótesis relacionada con el contenido del cuerpo o con el desplazamiento de parámetros del
método real.

El verdadero origen es el mecanismo de **bridges** que `76e076c` añadió a `ModuleEmitter.EmitBridge`
(no `EmitReceiverUnpackIfNeeded`, que es del `MethodBodyEmitter` y opera sobre el método real, no
sobre el bridge). `contains` no lleva `override`, así que su dispatch es `Direct` (§3.3 — no hay
override implícito en Surtr) y por tanto `SurtrTypeLinker` nunca lo coloca en la vtable: `NeedsBridge`
lo detecta y `EmitBridge` sintetiza un método puente, `Virtual`, con la forma erasionada de la
interfaz, cuyo cuerpo hace `LoadLocal(receptor)`, `LoadLocal(cada parámetro)` y por último `Call` al
método real. Ese bridge se construye con `SurtrClassBuilder.DefineMethod`, que **nunca invoca el
equivalente de `ApplyValueLayout`/`SetArgumentLayout`** — a diferencia de todo método declarado por el
compilador normal (`DeclareMethod`/`DeclareModuleMembers`/`DeclareExtensionFunction`, que sí lo
hacen). Así que el bridge asume que cada parámetro ocupa un slot, lo cual es falso para una tupla de
2+ elementos: su propio `LoadLocal(bridge.Parameter(0))` empuja un único valor donde la tupla ocupa
dos slots contiguos, y el `Call` final al método real —construido contra el `ArgumentSlotCount`
**correcto** de `contains` (receptor 1 + tupla 2 = 3)— hace pop de 3 con solo 2 en la pila. De ahí el
mensaje exacto (`pops 3 but the stack holds 2`) y por qué el repro con un cuerpo vacío falla
igual: el underflow ocurre en el bridge, no en el cuerpo del método declarado. Confirma también por
qué un **retorno** tupla no dispara nada: el retorno se maneja con `code.Box(...)`/`ReturnValue()`, un
camino distinto que no pasa por este bucle de `LoadLocal` por parámetro.

**Impacto real:** cierra la puerta a que `IMap<K,V>` extienda `IReadOnlyCollection<(K,V)>` (que
exigiría implementar `contains(item: (K,V)): bool`) — ver B8 más abajo para el motivo por el que
`Map<K,V>` tampoco podría haber usado esa vía de todas formas. `PriorityQueue<T>` no se ve afectado:
ningún método suyo toma una tupla como parámetro.

**Arreglo aplicado en el compilador** (`src/Surtr.Compiler/CodeGen/ModuleEmitter.cs`,
`EmitBridge`/`Narrow`): antes de emitir el cuerpo del bridge se calcula el ancho de cada parámetro
declarado (`ValueTypeLayout.WidthOfType`) y, si alguno ocupa más de un slot, se llama a
`bridge.SetArgumentLayout(receiverWidth: 1, parameterWidths, callSiteReceiverWidth: 1)` — la misma
corrección que `ApplyValueLayout` aplica a un método ordinario, ahora también al bridge. El bucle de
reenvío de parámetros distingue el caso ancho: para un parámetro-tupla, un nuevo
`EmitForwardedTupleParameter` reenvía **elemento a elemento** (leyendo cada uno con
`LoadLocalField`/`LoadValueLocal` en su propio offset dentro del bloque, exactamente como
`MethodBodyEmitter.EmitIndexRead` lee un índice de tupla constante) en vez de un único `LoadLocal`,
porque no existe ningún opcode que reduzca/castee un slot en medio de un bloque que ya está en la
pila de operandos — `Cast`/`Unbox` solo leen la cima. Cada elemento se estrecha (`Narrow`) por
separado si su tipo difiere entre la interfaz y la implementación, preservando el comportamiento
exacto que ya existía para un parámetro de un solo slot. Un elemento anidado de más de un slot dentro
de la tupla (otra tupla, o un `value class` multi-campo) no está cubierto — no lo ejercita ni el
repro de B7 ni la stdlib real — y se reporta con `SurtrEmitException` en vez de generar bytecode
incorrecto en silencio.

Verificado con `src/Surtr.Tests/Stdlib/SurtrStdlibBehaviorTests.cs`
(`InterfaceDispatchedMethodWithTwoElementTupleParameterWorks`), que ejercita tanto la llamada directa
(no pasa por el bridge) como la llamada a través de una variable tipada por la interfaz (sí pasa por
el bridge) — ambas devuelven el resultado correcto. Suite completa: 3371/3371 en verde.

**Arreglo aplicado en la stdlib:** `IReadOnlyMap<K,V>`/`IMap<K,V>` (`collections/Map.surtr`) siguen
declarando únicamente `IIterable<(K,V)>` en vez de `IReadOnlyCollection<(K,V)>` por ahora — el arreglo
del compilador ya lo permitiría (ver la nota en §3.4/la propuesta original), pero ampliar la interfaz
es un cambio de superficie de la stdlib aparte, no parte de este arreglo puntual.

### 2.8 B8 — Bug del compilador: `dict<K,V>` con K y V genéricos simultáneos corrompe silenciosamente los valores primitivos leídos vía `keys()`/`values()`/iteración — Prioridad CRÍTICA — **Corregido**

Descubierto implementando `Map<K,V>` (Fase 6, §3.4), al intentar validar `for (pair in map)` con
`Map<string, int>`. Es el hallazgo más serio de toda esta ronda: a diferencia de B6/B7 (que fallan
ruidosamente, `SURTR4001` en tiempo de compilación), **este falla en silencio** — compila y ejecuta
sin ningún error, y simplemente devuelve datos incorrectos.

**Síntoma:** un campo `{K: V}` declarado dentro de una clase **genérica propia** (no un `dict<K,V>`
suelto con tipos concretos, y no el `{T: bool}` de `Set<T>` — ver por qué abajo), con **K y V siendo
dos parámetros de tipo genéricos distintos simultáneamente**, devuelve valores corruptos para un `V`
primitivo (`int` confirmado; `float`/`bool`/`char` no probados pero con la misma forma de erasure, se
asume el mismo riesgo) en cuanto se lee a través de `keys()`/`values()` o de la iteración
(`for (pair in dict)`) — confirmado hasta con indexación directa sobre el array resultante (`vs[0]`),
así que no es un problema específico del lowering de `for-in`.

**Regla exacta, confirmada con seis repros mínimos aislados** (vía `SurtrCompilation`/
`ModuleEmitter`):

| Operación sobre `{K: V}` (K, V genéricos de la misma clase) | V = `string` (referencia) | V = `int` (primitivo) |
|---|---|---|
| `get(key): V` (un único valor, sin array) | Correcto | **Correcto** |
| `values(): V[]` + `for`-in sobre el array, leyendo `.length`/sumando | Correcto | **Corrupto** |
| `values(): V[]` + indexación directa `vs[0]` (sin bucle) | — (no probado, innecesario) | **Corrupto** |
| `dict<string, int>` **concreto**, sin genéricos de por medio | — | Correcto |
| `Set<T>`'s `{T: bool}` (solo la key es genérica; el value, `bool`, es concreto) | — | Correcto (ya en producción) |

Repro mínimo:

```surtr
public class Box<K, V> {
    private let _dict: {K: V};
    public constructor() { _dict = {}; }
    public fun set(k: K, v: V): void { _dict.set(k, v); }
    public fun values(): V[] => _dict.values();
}
// Box<string, int>(): set("a",1), set("b",2), set("c",3)
// vs = box.values(); vs[0] debería ser 1 — en la práctica sale un valor distinto (visto: 4)
// sum de vs sumando en un for-in debería ser 6 — en la práctica sale 21
```

**Por qué `Set<T>` no lo tenía ya y por qué nadie lo había visto:** en todo el resto de la stdlib
(`List<T>`, `Set<T>`, `Queue<T>`/`Stack<T>`/`Deque<T>`), cada colección solo tiene **un** slot
genérico erasionado (el elemento `T`) — el `{T: bool}` de `Set<T>` tiene la key genérica pero el
value es `bool`, un tipo **concreto**, no un segundo parámetro genérico. `Map<K,V>` es el primer sitio
en toda la stdlib donde un `dict` tiene **sus dos lados** ligados a parámetros de tipo genéricos
distintos de la misma clase (`G0` y `G1`) — un caso que, hasta ahora, nada había ejercitado.

**Causa raíz real (confirmada con debugging del bytecode emitido — la hipótesis original apuntaba al
sitio equivocado):** `GetDictionaryKeyType()`/`GetDictionaryValueType()` **no tenían ningún bug** —
se verificó con un test que inspecciona el descriptor real de un campo `{K: V}` (`DG0G1`, correcto) y
el de la propia clase built-in `dict` (`G0`/`G1` correctamente separados; ambos resuelven a
`SurtrValueTypeCode.Erased` sin que el dígito importe para la representación). El array que
`values()`/`keys()` construye SÍ tiene el descriptor de elemento correcto. El bug estaba en que los
**elementos guardados dentro del propio `dict`** quedaban boxeados cuando no debían.

`array`/`dict` leen y escriben su almacenamiento nativo **crudo** (§3.5, "no per-element type
tags") — un valor que cruza hacia dentro de una de estas colecciones desde un cuerpo genérico se
desboxea primero (`MethodBodyEmitter.EmitCollectionOperand` llama a `UnboxIfStillErased`), porque un
valor "en reposo" dentro de un cuerpo aún genérico siempre está boxeado (llegó boxeado a través de un
límite de erasure — un argumento, una lectura de campo), pero el `array`/`dict` nunca lo estuvo.
`EmitCollectionOperand` tenía dos ramas: una para cuando el argumento ya trae su propia conversión
`ImplicitErasure` (el caso típico: `dict.set(key: G0, value: G1)` está declarado con los parámetros
propios *sin sustituir* del built-in, así que el binder SIEMPRE envuelve el argumento en
`ImplicitErasure`, incluso cuando, tras la sustitución de `Map<K,V>`'s propio `{K: V}`, el tipo
sustituido coincide exactamente con el tipo del argumento) y otra para cuando no la trae (conversión
por identidad). La segunda rama llamaba a `UnboxIfStillErased` correctamente; **la primera no** — se
limitaba a "deshacer" la conversión (usar la expresión pre-erasure de `conversion.Operand` tal cual),
lo cual es correcto cuando ese operando es un tipo concreto (nunca estuvo boxeado, así que no hay nada
que desboxear) pero incorrecto cuando el operando es *él mismo* un parámetro de tipo genérico todavía
abierto (`v: V`, el propio `V` de `Box`/`Map`) — que, "en reposo", SÍ está boxeado, por un límite de
erasure *distinto y anterior* (el de la llamada externa `Box<string,int>.set("a", 1)`, que boxeó el
`1` al entrar en el cuerpo genérico de `Box<K,V>.set`). Confirmado desensamblando `Box<K,V>.set`: antes
del arreglo, `Ldl2` (cargar `v`) iba directo a `DictSet` sin ningún `UnboxDynamic` de por medio; el
valor guardado en el dict quedaba como una referencia boxeada (`SurtrBoxed`) en vez del `int` crudo
que la colección espera — confirmado leyendo el elemento resultante de `values()` con
`runtime.Resolve<SurtrRuntimeEntity>`, que devolvía un `SurtrBoxed` real. `get(key)` no se veía
afectado porque su *lectura* sí pasa por la conversión "unerase" correcta al volver de la llamada
(`UnerasedCallResult`/`Unerase`, que usa `UnboxDynamic` — lee el tag propio del valor y funciona igual
esté o no boxeado); el problema era exclusivamente de **escritura**.

**Arreglo aplicado en el compilador** (`src/Surtr.Compiler/CodeGen/MethodBodyEmitter.cs`,
`EmitCollectionOperand`): la rama que despoja la conversión `ImplicitErasure` ahora también llama a
`UnboxIfStillErased(conversion.Operand.Type)`, igual que ya hacía la rama de identidad — simétrico en
ambas, y sin efecto donde no hacía falta (`UnboxDynamic` es no-op sobre un valor que nunca estuvo
boxeado). Verificado desensamblando de nuevo `Box<K,V>.set`: ahora tanto `k` como `v` reciben
`UnboxDynamic` antes de `DictSet`, y el elemento leído de vuelta por `values()` es un `int` crudo, no
una referencia. `Set<T>` nunca disparaba esto (su `value` es `bool`, concreto — el argumento nunca
llega envuelto en `ImplicitErasure` con un operando *también* genérico) y `List<T>`/`array<T>` tampoco
(sus elementos nunca se boxean para empezar: escriben directamente vía `ArrSet`/`ArrGet`, que
`EmitCollectionOperand` también cubre con la misma corrección para cuando sí aplica).

**Impacto real:** afectaba a `Map<K,V>` con cualquier `V` primitivo — el caso de uso más común
(`Map<string, int>`, contadores, tablas de puntuación, etc.) — para `keys()`/`values()`/`for-in`.
`get(key)` nunca estuvo afectado.

Verificado con `src/Surtr.Tests/Stdlib/SurtrStdlibBehaviorTests.cs`:
`MapIterationWithPrimitiveIntValuesCurrentlyReturnsCorruptedData` (`for (pair in m) sum += pair[1];`
ahora suma 6 de verdad) y la nueva `MapValuesOfPrimitiveIntReturnsRealNumbers` (`values()[0]` e
indexación directa, y un `for-in` sobre el array que devuelve, ambos con `V = int`). Suite completa:
3372/3372 en verde. La guía de "no usar `Map<K, int>` más allá de `get`/`set`" en el `README.md` de la
stdlib y en este documento queda retirada — ya no aplica.

### 2.9 B9 — Bug del compilador: `T?` de un método genérico pierde su marca de ausencia al instanciarse a un primitivo — Prioridad CRÍTICA — **Corregido**

Descubierto implementando la Fase 7, al escribir la primera prueba de verdad que compara con `null`
el resultado de `Sequence<T>.firstOrNull()`/`lastOrNull()`/`min()`/`max()` sobre una secuencia **vacía**
con `T` primitivo. Como B8, **falla en silencio** — sin `SURTR4001`, sin excepción — así que es fácil
de no ver: nada en la suite existente había comparado nunca ese resultado contra `null` con un `T`
primitivo antes de estas pruebas.

**Síntoma:** un método genérico declarado `fun f<T>(): T?` (o con `T` fijado por la clase contenedora,
como el `T` de `Sequence<T>`), instanciado a un tipo primitivo (confirmado con `int`), que hace
`return null;` en algún camino, devuelve un valor que **no compara igual a `null`** en el llamador —
`resultado == null` da `false` cuando debería dar `true`. El mismo valor a través de un `T?`
**concreto, no genérico** (`fun f(): int? { return null; }`) compara correctamente.

**Regla exacta, confirmada con dos repros mínimos aislados:**

```surtr
fun getNull(): int? { return null; }
fun run(): bool { return getNull() == null; }              // true  - correcto

fun getNull<T>(): T? { return null; }
fun run(): bool { return getNull<int>() == null; }          // false - incorrecto
```

**Causa raíz real (confirmada leyendo el propio emisor — la hipótesis original, "el Cast al leer de
vuelta", apuntaba al mecanismo correcto pero no a lo que realmente le faltaba):** dentro del cuerpo
genérico de `getNull<T>(): T?`, `return null;` **no** se compila como `PushAbsent` — `EmitLiteral`
solo elige `PushAbsent` cuando `IsNullablePrimitive(literal.Type)` es cierto, y esa comprobación mira
el `TypeCode` del tipo, que para `T?` con `T` todavía sin resolver es `Erased`, no
`Integer`/`Float`/... — así que el `null` de un cuerpo genérico se compila igual que el de cualquier
tipo referencia: `Code.LoadNull()`, una referencia nula corriente. Esto es correcto en sí mismo — el
cuerpo se compila una sola vez para todo `T`, y no puede saber de antemano si terminará en algo que
necesita la marca de ausencia.

El punto donde se pierde es la conversión de vuelta. `MethodBodyEmitter.UnerasedCallResult` — que ya
existe precisamente para "leer el resultado de una llamada genérica de vuelta como cualquier otro slot
erasionado" (§1.11) — detecta correctamente que `getNull<int>()`'s declaración original (`T?`, un
parámetro de tipo desnudo tras quitar la nulabilidad) necesita esta conversión, y llama a `Unerase`
sobre el tipo sustituido (`int?`). Pero `Unerase` **ignora la nulabilidad de `target`**: hace
`bare = target.NonNullable` y, para un primitivo, emite sencillamente `Code.UnboxDynamic()` — el
opcode que "lee el propio tag del valor" para servir tanto al caso boxeado como al crudo. `UnboxDynamic`
sabe convertir una referencia boxeada (`SurtrBoxed`) a su primitivo crudo, y sabe dejar en paz
cualquier cosa que no sea una referencia — pero una **referencia nula** no es ni lo uno ni lo otro:
no es un `SurtrBoxed`, así que `UnboxDynamic` la deja exactamente como estaba (confirmado leyendo su
propia implementación en la VM: "Null, or a reference that is not a box at all... both stay exactly
as they are"). El valor que llega al llamador sigue siendo una referencia nula ordinaria
(`TagMaskReference`, payload 0) en vez del valor con la marca de ausencia propia de un nulable
primitivo (`TagMaskAbsent`) — dos tags **distintos** por diseño (§5.1: "un `int?` ausente es su propio
valor con marca, no una referencia nula"). Y la comparación `resultado == null` que el llamador
escribe, al ver que el lado izquierdo es un nulable primitivo *concreto* (`int?`, ya sustituido),
se compila como una prueba de tag contra `TagMaskAbsent` (`TryEmitAbsenceTest`/`Code.IsAbsent()`) —
que sobre una referencia nula (`TagMaskReference`) da `false`. De ahí el síntoma exacto.

**Arreglo aplicado en el compilador** (`src/Surtr.Compiler/CodeGen/MethodBodyEmitter.cs`,
`UnerasedCallResult`): en vez de ensanchar `Unerase` (compartido por sitios donde el valor erasionado
sí puede ser legítimamente un primitivo crudo sin marca — el propio almacenamiento de un
`array`/`dict`, o `IIterator<T>.current` sobre uno — donde una prueba ingenua de "¿el payload es
cero?" confundiría un `0` real con ausencia), se añadió un camino específico para cuando el tipo
sustituido es un nulable primitivo: antes de desboxear, se duplica el valor y se comprueba si es una
referencia nula (`Dup` + `IsNull` + `JPZ`) — comprobación segura aquí porque un valor `T?` "en reposo"
dentro de un cuerpo todavía genérico, al volver de una llamada real (no de un acceso a almacenamiento
nativo), solo puede haberse producido como referencia: boxeado (presente) o nulo (ausente), nunca como
primitivo crudo — la misma invariante que `BoxIfStillErased`/`UnboxIfStillErased` ya hacen cumplir en
cualquier otro límite de erasure. Si es nulo, se descarta y se empuja `PushAbsent` con el tipo
concreto correcto; si no, seguía el mismo `UnboxDynamic` de siempre.

**Impacto real:** no era nuevo de esta ronda — era un hueco preexistente en `Sequence<T>` que nadie
había probado hasta ahora. Afectaba a `firstOrNull()` (ya en el árbol desde antes de esta revisión) y
a los `min()`/`max()`/`lastOrNull()` de la Fase 7, todos ellos en el caso "secuencia vacía, `T`
primitivo". Nunca afectó al valor no-nulo, ni a `T` de tipo referencia (una referencia nula ya se
representa de forma nativa sin necesitar la marca especial que usa un primitivo nulable).

Verificado con `src/Surtr.Tests/Stdlib/SurtrStdlibBehaviorTests.cs`:
`SequenceFirstOrNullOnEmptySequenceCurrentlyReturnsFalseNotNull`,
`SequenceMinOnEmptySequenceCurrentlyReturnsFalseNotNull`,
`SequenceLastOrNullOnEmptySequenceCurrentlyReturnsFalseNotNull` (las tres ahora aciertan de verdad) y
la nueva `GenericMethodNullablePrimitiveReturnComparesCorrectlyAgainstNull`, con el repro mínimo del
propio documento — incluyendo explícitamente el caso "presente con valor `0`", que es exactamente lo
que una corrección ingenua basada en el payload (en vez del tag) habría vuelto a romper. Suite
completa: 3373/3373 en verde. La advertencia de "no usar `firstOrNull()`/`lastOrNull()`/`min()`/`max()`
con un `T` primitivo para distinguir vacío del valor por defecto" queda retirada — ya no aplica.

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

**Fase 5 — Adiciones de alto valor (§3.1, §3.2) — Hecha; B6, descubierto en el camino, ya corregido**
`Angle` completo y `Random` — **hechos, utilizables**. `Vector2`/`Vector3`/`Quaternion`
(`src/surtr/math/Vector.surtr`) — **escritos, verificados matemáticamente y ahora utilizables entre
módulos de verdad**: B6 (§2.6), descubierto durante esta fase, rompía cualquier llamada entre módulos
que devolviera una `value class` multi-campo sin inlinearse, lo que cubría casi toda su API útil (`+`,
`-`, `*`, `normalized()`, `lerp`, `rotate`, composición de quaterniones) — corregido en el compilador
(§2.6), verificado con `VectorArithmeticFromAnotherModuleWorks` y el resto de tests de
`Vector2`/`Vector3`/`Quaternion`, que ya usan un driver en su propio módulo.

**Fase 6 — Adiciones de valor medio (§3.3, §3.4) — Hecha, con matices; B6 resultó no bloquearla**
La suposición original de que B6 bloquearía esta fase (por seguir "el mismo patrón") era demasiado
cautelosa: B6 es específicamente sobre `value class`es concretas multi-campo, y toda la superficie de
`PriorityQueue<T>`/`Map<K,V>` es genérica (`T`/`K`/`V` erasionados a un slot), así que se implementaron
y probaron de verdad en vez de dejarlas en pausa. En el camino aparecieron dos bugs de compilador
nuevos, más estrechos que B6 y catalogados por separado:

- **`PriorityQueue<T>`** (`collections/PriorityQueue.surtr`) — **completa y utilizable de verdad**,
  incluso entre módulos y con `T` instanciado a una `value class` multi-campo (`PriorityQueue<Vector2>`
  probado). Sin `default`/soporte de literal `[...]` (§3.3 lo daba por hecho; ver el comentario del
  propio archivo sobre por qué no encaja con una prioridad por elemento).
- **`Map<K,V>`/`ReadOnlyMap<K,V>`** (`collections/Map.surtr`) — utilizable para `get`/`set`/
  `containsKey`/`remove`/`clear` por clave con cualquier `V`, y para `keys()`/`values()`/`for-in` con
  `V` de tipo referencia (`string`, clases, `value class`es). **No recomendado con `V` primitivo
  (`int`/`float`/`bool`/`char`) más allá de acceso por clave individual** — B8 (§2.8) corrompe
  silenciosamente los datos ahí. Tampoco extiende `IReadOnlyCollection<(K,V)>` como proponía §3.4
  originalmente — B7 (§2.7) lo impide (un `contains(item: (K,V))` con dispatch de interfaz revienta
  en la propia emisión) — así que `reutilizarían ReadOnlyCollection<T>` de la propuesta original no
  aplicó; en su lugar hay un `ReadOnlyMap<K,V>` propio, un nivel más arriba en la jerarquía.

**Fase 7 — Ampliaciones incrementales (§3.5-§3.7) — Hecha**
Como se esperaba, la menos afectada por los bugs de compilador encontrados en las fases anteriores
(son sobre todo métodos sobre tipos ya existentes, sin `value class` multi-campo ni parámetro-tupla de
por medio). Aun así apareció un cuarto bug — B9 (§2.9) — y un matiz de diseño en dos sitios:

- **`List<T>`:** `sort(comparator)`, `reverse()`, `addRange(items)`, `lastIndexOf(item)`, `toArray()`
  — todos según lo previsto en §3.5. `sort` copia a un `toArray()` propio en vez de llamar a
  `_items.sort(comparator)` directamente, porque `_items` está sobre-reservado a `_capacity` y el
  `sort` del array built-in no sabe distinguir eso de `_length`.
- **`StringBuilder`:** `insert`/`remove`/`replace`/`indexOf(char)`/`indexOf(string)`/`substring`/
  `capacity` — todos según §3.6 (el setter de `operator[]` que pedía D1 ya existía desde el arreglo de
  B1).
- **`Sequence<T>`:** `min`/`max` (comparador explícito, no una restricción `IComparable<T>`),
  `groupBy<K>` (usa `Map<K, List<T>>`, ya disponible desde la Fase 6), `distinctBy<K>`,
  `sortBy<K>`/`sortByDescending<K>` (comparador explícito sobre `K`, mismo motivo que `min`/`max`),
  `joinToString` (con selector `(T) -> string` explícito en vez de asumir `T.toString()` — `T` no
  lleva la restricción `<T : object>` que eso necesitaría), `elementAt`/`last`/`lastOrNull`, y
  `sumInts`/`averageInts`/`sumFloats`/`averageFloats` (dos extensiones concretas en vez de un
  `sum()`/`average()` genérico — ver el porqué exacto en el propio archivo: `Sequence<T>` es una
  `value class` de un solo campo y erosiona `Sequence<int>`/`Sequence<float>` a la misma firma).
  Descubierto en el camino: **B9** (§2.9), donde `firstOrNull()`/`lastOrNull()`/`min()`/`max()` no
  distinguen "vacío" de "cero" con un `T` primitivo. Además, `distinctBy`/`groupBy`/`sortBy`/
  `sortByDescending` necesitan el argumento de tipo explícito (`.groupBy<int>(...)`) cuando el
  `keySelector` es un lambda literal — la inferencia no lo deduce del cuerpo del lambda como sí hace
  con un `(T) -> U` ya tipado que se pasa directamente (visto en `joinToString`'s propio uso interno
  de `map(selector)`); no bloqueante, solo menos ergonómico de lo que sugería §3.7.

---

## 5. Decisiones tomadas y preguntas abiertas

Las cuatro preguntas originales de esta sección ya están resueltas y las Fases 0-4 implementadas
sobre esas respuestas:

1. **Alcance:** Fases 0-3 primero (bugs + inconsistencias), luego, al descubrirse B5, priorizar su
   arreglo antes de seguir con las Fases 5-7.
2. **Documento:** comiteado en `docs/` como `Plan-Revision-Stdlib.md`, sin añadirlo a la tabla de
   mapa de documentación de `CLAUDE.md`.
3. **`Deque<T>` (D2):** opción (b) — lista doblemente enlazada propia, independiente de `Queue<T>`.
4. **Vector/Quaternion/Random (Fase 5):** diseño directo, sin pasada de validación previa por
   separado — se implementó y se ajustó sobre la marcha contra los límites reales del compilador
   (B6), que una pasada de diseño puramente sobre el papel no habría podido prever.
5. **B5:** se priorizó arreglar el bug del compilador antes de seguir con las Fases 5-7 — hecho
   (§2.0).
6. **B6:** se documentó y se paró por ese día en vez de arreglarlo antes de seguir — la sesión
   siguiente retomó con "seguir con el resto del plan", así que la Fase 6 se intentó igualmente en
   vez de esperar: resultó ser la decisión correcta, B6 no la bloqueaba (ver el análisis en la
   entrada de la Fase 6, §4).
7. **Fase 6, orden `Map<K,V>` vs `PriorityQueue<T>`:** no importó en la práctica — se implementaron
   ambas en la misma sesión, `PriorityQueue<T>` primero por ser la más simple de las dos.

Los cinco bugs de compilador/runtime de esta ronda están **todos corregidos**:

- **B6 (§2.6):** **corregido** — `Vector2`/`Vector3`/`Quaternion` ya son usables entre módulos.
  `Quaternion` separarse de `Vector.surtr` a su propio archivo (`surtr.math.Quaternion`) queda como
  mejora aparte, no forzosa: el arreglo del compilador ya lo permite.
- **B7 (§2.7):** **corregido** — `IMap<K,V>` ya podría extender `IReadOnlyCollection<(K,V)>` como
  proponía §3.4 originalmente; el ensanchamiento en sí queda como mejora aparte, no forzosa.
- **B8 (§2.8):** **corregido** — `Map<K,V>` ya se recomienda sin reservas con valores primitivos.
- **B9 (§2.9):** **corregido** — `firstOrNull()`/`lastOrNull()`/`min()`/`max()` ya distinguen
  correctamente "vacío" de "el valor por defecto" con un `T` primitivo.

Preguntas que siguen abiertas para cuando se retome cada fase:

- **Fase 5:** confirmar alcance exacto de la superficie de `Vector2`/`Vector3`/`Quaternion`/`Angle`
  antes de diseñar (¿se incluye `Vector4`/`Color`/`Rect` ya, o se dejan para después como sugiere
  §3.1?).

---

## 6. Fase 8 — auditoría tras la jerarquía `object`/`Enum`/`ValueType` (optimización + expansión)

Disparada por una pregunta directa: con `object` como raíz real de todo (`6a31338`
`feat(runtime): add a real object/Enum/ValueType root hierarchy`, y la cadena que lo completa —
`d865828`, `9f82f5b`, `76e076c`, `9b8de0b` `feat(runtime): expose equals/hashCode/toString on
SurtrObject, wire into the untyped comparer fallback` — las cinco ya en el árbol **antes** de que
empezaran las Fases 5-7), ¿qué debería la stdlib estar aprovechando que no aprovecha todavía, y qué
hueco queda ahora que el lenguaje tiene una raíz de objeto de verdad? Esta sección es solo análisis y
propuesta — nada de lo que sigue está implementado todavía; queda para que se decida qué abordar y en
qué orden.

Método: relectura completa de los 30 archivos `.surtr` reales de la stdlib (~4755 líneas, la lista
completa creció de 24 a 30 desde la E1 original con `PriorityQueue`, `Map`, `Vector`, `Quaternion`,
`Random` y el paquete `diagnostics/`), más tres sondas de verificación compiladas y ejecutadas de
verdad contra el runtime (no solo inspección de código) para confirmar exactamente qué desbloquea la
jerarquía `object` antes de proponer nada sobre esa base.

### 6.1 Lo que la jerarquía `object` desbloquea de verdad — confirmado, no solo leído

El hallazgo central de esta fase: **`Set<T>`, `Map<K,V>` y, por extensión, cualquier colección
genérica de la stdlib respaldada por `{T: bool}`/`{K: V}`, ya respetan un `equals()`/`hashCode()`
declarado por el usuario — para una `value class` de la propia stdlib sin ningún override explícito,
y para una clase corriente con un override explícito de verdad.** Esto no estaba documentado en
ningún sitio de la stdlib ni cubierto por ningún test de la propia stdlib (`SurtrStdlibBehaviorTests`
no tiene ni un solo caso que ejercite esta ruta), así que antes de basar ninguna propuesta en ello se
verificó con tres sondas reales (compiladas y ejecutadas con el mismo arnés que usa
`SurtrStdlibBehaviorTests`, `SurtrCompilation`/`ModuleEmitter`/`SurtrRuntime.LoadModule`, luego
descartadas — no quedan en el árbol):

1. `Set<Vector2>` con `Vector2(1.0, 2.0)` insertado dos veces (instancias distintas, mismos campos) más
   `Vector2(3.0, 4.0)` → `length == 2`. `Vector2` no declara `override fun equals(...)` ni
   `hashCode()` en absoluto — solo `operator==` (§2.9, ya existente) y el `toString()` de la Fase 5.
2. `Map<Vector2, int>`: `m.set(Vector2(1.0, 2.0), 42)` seguido de `m.get(Vector2(1.0, 2.0))` (una
   **segunda** instancia, mismos campos) → `42`. Confirma que la clave de un `Map<K,V>` con `K` una
   `value class` de la stdlib hashea y compara por estructura, no por identidad, sin que
   `Vector2`/`Angle`/`Quaternion`/`byte` tengan que hacer nada especial.
3. Una clase corriente (no `value class`) con `override fun equals(other: object?): bool` y
   `override fun hashCode(): int` escritos a mano, metida en un `Set<T>` de la stdlib →
   `length == 2` tras insertar dos instancias "iguales" por su override y una distinta. (La mitad
   `equals` de esto ya estaba cubierta por un test de compilador ajeno a la stdlib,
   `AnArrayOfPlainClassesWithARealEqualsOverride_IndexOfNowRespectsIt`, pero **ningún test en todo el
   árbol** — ni de compilador ni de stdlib — ejercitaba la mitad `hashCode`, que es la que de verdad
   importa para un `Set`/`Map`: `equals` sin `hashCode` coherente rompería silenciosamente el
   contrato hash/igualdad de cualquier colección respaldada por un diccionario.)

**Por qué esto importa más que un dato suelto:** es la pieza que le faltaba a `collections/` para
que una clase de dominio del usuario (un `Item`, un `Entity`, un `Vector2` importado) se comporte como
"de primera clase" dentro de `Set<T>`/`Map<K,V>` — antes de esta cadena de commits, solo las
`value class` con operadores escritos a mano y las built-in tenían igualdad estructural; una clase de
usuario corriente solo podía compararse por identidad dentro de un `Set`/`Map`, sin forma de optar a
otra cosa. Ahora puede, y ya funciona, pero **nadie lo ha probado con la propia stdlib ni lo dice en
ningún sitio** — el README de la stdlib y los propios docstrings de `Set`/`Map` siguen sin
mencionarlo. Recomendación inmediata, de bajo coste: llevar las tres sondas de arriba a
`SurtrStdlibBehaviorTests.cs` como tests permanentes (hoy no existe ninguno) y añadir una línea en
`Set.surtr`/`Map.surtr` señalando que la igualdad respeta un `equals()`/`hashCode()` de verdad.

### 6.2 Hallazgos de optimización sobre el código ya existente

Cada uno con archivo/línea y una acción concreta — no son bugs (nada de esto compila mal ni da un
resultado incorrecto), son ocasiones reales de aprovechar la jerarquía `object` o de evitar trabajo
que ya no hace falta.

**O1 — `Map<K,V>.iterate()` copia el mapa entero a un array en cada llamada, aunque el mecanismo para
no hacerlo ya existe en el propio runtime.** `collections/Map.surtr:71-81`: cada `for (pair in map)`
snapshotea **todos** los pares a un `array<(K,V)>` nuevo antes de poder iterar uno solo, porque
`dict<K,V>.iterate()` está declarado contra `IIterable<K>` (una sola K), no contra `(K,V)`. Pero el
propio comentario del archivo ya señala que el `for-in` sobre un `dict` real está "specially lowered
to walk real (K, V) pairs regardless" — es decir, el lowering que necesita ya existe y ya lo usa el
propio cuerpo de `iterate()` (`for (pair in _dict)`, línea 75) para construir el array; lo que falta
es exponer esa misma capacidad como un método nativo del `dict` built-in
(`fun pairs(): IIterator<(K, V)>`, sin cambiar `iterate()` para no romper nada que dependa de su forma
actual) para que `Map<K,V>.iterate()` pueda delegar directamente en él sin copiar. Esto es un cambio
de runtime/compilador (`SurtrCompositeBuiltIns`/`SurtrDictionaryBuiltIn`), no de la stdlib en sí — la
stdlib solo cambiaría una línea una vez exista. Impacto: cada `for (pair in map)` sobre cualquier
`Map<K,V>` de tamaño N asigna hoy un array de N elementos por adelantado en vez de ser perezoso;
importa sobre todo si `Map<K,V>` se usa dentro de un bucle caliente (p. ej. un sistema de juego que
recorre un mapa de entidades cada frame).

**O2 — `Sequence<T>`/`IIterable<T>.joinToString` exige siempre un `selector: (T) -> string` explícito
porque `T` no lleva la restricción `<T : object>` (razón ya documentada en el propio archivo,
`collections/Sequence.surtr:167-170`) — pero ahora esa restricción es exactamente lo que hace falta y
ya funciona (§4.8/Compiler-Plan.md, "escribir `<T : object>` a mano consigue el mismo efecto donde se
quiera").** Se puede añadir una sobrecarga **adicional** (no sustituir la actual, que sigue
sirviendo para un `T` sin esa cota) `joinToString<T: object>(self: IIterable<T>, separator: string):
string => self.asSequence().map(x => x.toString()).joinToString(separator, x => x)` — o más
directamente, una nueva `fun toDisplayString<T: object>(self: IIterable<T>): string`. Coste: una
firma nueva, sin tocar nada existente.

**O3 — ninguna colección de `collections/` (`List`, `Set`, `Map`, `Queue`, `Stack`, `Deque`,
`PriorityQueue`, `LinkedList`) tiene un `toString()` de depuración**, a diferencia de `StringBuilder`,
`byte`, `Angle`, `Vector2/3`, `Quaternion`, que sí lo tienen desde que se escribieron. Antes de la
jerarquía `object` esto no se podía hacer de forma genérica sin asumir algo sobre `T`; ahora
`toString<T: object>(): string => "[" + elements-joined-by-toString + "]"` es directo — el mismo
mecanismo de O2. Es una mejora de ergonomía de depuración pura (nada compila distinto sin ella), pero
es exactamente el tipo de hueco que salta a la vista en cuanto se usa `dump()`/`println()`
(`diagnostics/Debug.surtr`) sobre un `List<T>` hoy: sale el nombre desnudo de la clase, no su
contenido (regla ya documentada para `surtr run`/`EntryPoint.Resolve` en `CLAUDE.md`: "a class that
writes no toString() of its own still falls back to its bare name").

**O4 — ni `byte` ni `Angle` implementan `IComparable<T>`/`IEquatable<T>`, pese a que ambos ya
escriben `compareTo`/`equals`/`<=>`/`==` a mano y ambos built-ins existen desde antes de que ninguno
de los dos se escribiera** (`SurtrStandardLibrary.cs:145-149`, `DeclareInterface(module, handles,
"IComparable", "T")`/`"IEquatable"`). Confirmado con `grep`: **ningún archivo de toda la stdlib
declara `IComparable<T>` ni `IEquatable<T>`** — ni siquiera `Vector2`/`Vector3`/`Quaternion`, para los
que tendría menos sentido (no hay un orden natural de un vector), pero `byte` y `Angle` sí tienen un
orden total real y ya implementado en la práctica. El coste de arreglarlo es añadir
`: IComparable<byte>, IEquatable<byte>` (y lo mismo en `Angle`) a la lista de interfaces — los cuerpos
ya existen, no hay que escribir nada nuevo. La razón por la que importa **ahora**: es exactamente la
pieza que le falta a la stdlib para poder escribir, sin comparador explícito en cada llamada, un
`List<T: IComparable<T>>.sort(): void` o un `Sequence<T: IComparable<T>>.min()/max()/sorted()` — hoy
`List.sort`/`Sequence.min`/`Sequence.max`/`Sequence.sortBy` piden siempre un `comparator`/`selector`
explícito (decisión consciente documentada en el propio `Sequence.surtr`, "no hay rasgo numérico
genérico al que pedir uno"), lo cual sigue siendo cierto para un `T` sin cota, pero deja de serlo para
un `T` acotado a `IComparable<T>` — que hoy ningún tipo de la stdlib satisface, así que la sobrecarga
sin comparador nunca tendría con qué probarse de verdad.

**O5 — `List<T>.sort()` hace dos copias completas (`toArray()` + `sorted.sort(...)` + escritura de
vuelta) para evitar que el `sort()` del array built-in incluya la cola sin usar más allá de
`_length`** (`collections/List.surtr:172-184`, ya documentado en el propio comentario). No es
arreglable desde Surtr puro sin la copia — necesitaría una sobrecarga `array<T>.sort(comparator,
count: int)` en el built-in que ordene solo el prefijo `[0, count)`. Se deja anotado como candidato de
runtime, no de stdlib, en la misma categoría que O1.

### 6.3 Diez propuestas de expansión (nuevas, no cubiertas por las Fases 3.1-3.8 originales)

Ordenadas de mayor a menor "encaja con la misión declarada de Surtr" (alternativa a Lua embebida en
Unity, `CLAUDE.md` primera sección), no por facilidad de implementación.

**P1 — Un planificador de corrutinas sobre `generator` (`surtr.async.Scheduler`/`Timer`).** El
lenguaje ya tiene corrutinas completas de verdad — `generator`, `send`/`raise`/`dispose`, `yield`
como expresión, cierre determinista en las cuatro salidas (`CLAUDE.md`, sección de generadores) — y
hoy la stdlib no tiene absolutamente nada que las use para lo que un juego las necesita: temporizar
una secuencia de acciones sin bloquear el frame (`WaitForSeconds` de Unity es el paralelo directo). Un
`Scheduler` que registre generadores/closures con un delay o una condición y los reanude cuando un
`update(deltaTime: float)` avance el reloj sería la pieza que conecta la inversión ya hecha en
corrutinas con el caso de uso real que las motivó. Coste: medio (Surtr puro, sobre `List<T>` +
`generator` ya existentes); riesgo: bajo.

**P2 — `surtr.events.Signal<T>` / `EventEmitter<T>` (multicast de closures).** Los closures ya son
valores de primera clase (`ClosureValue`, `CLAUDE.md` §8). Un tipo mínimo — `subscribe((T) -> void):
Subscription`, `unsubscribe(Subscription)`, `emit(T): void`, sobre un `List<(T) -> void>` interno — es
el patrón más repetido de scripting de gameplay (input, daño, cambios de estado) y hoy hay que
reinventarlo a mano en cada script. Coste: bajo; riesgo: bajo.

**P3 — Un módulo de serialización ligera (`surtr.text.Json` o `surtr.serialization.Json`).** Ahora
que `object.toString()` es real y cada tipo (incluyendo enums y `value class`) tiene una
representación textual de verdad, un encoder/decoder JSON mínimo (a/desde `Map<string, unknown>` +
`List<unknown>` + primitivos) cubriría guardado de partidas, configuración y mensajes de red — los
tres casos de uso que cualquier script embebido en un motor necesita y que hoy no tienen ninguna
respuesta en la stdlib. Coste: medio-alto (un parser recursivo-descendente y un encoder, ambos Surtr
puro); riesgo: medio (el propio `unknown`/erasure hace que el árbol de resultado sea menos cómodo de
consumir que en un lenguaje con `any` real — hay que diseñar la forma exacta del árbol antes de
escribir nada).

**P4 — `Grid<T>` / `Array2D<T>` (`surtr.collections.Grid`).** Encaja directamente al lado de
`Vector2` (Fase 5): un wrapper sobre `array<T>` plano indexado por `(x, y)` con bounds-checking,
`width`/`height`, y idealmente `get(pos: Vector2Int)`/`operator[]` — el patrón de tablero/tilemap que
todo juego 2D necesita y que hoy solo se puede montar a mano con `array<array<T>>` (doble
indirección, doble alocación). Coste: bajo; riesgo: bajo. Necesitaría decidir si existe ya o hace
falta un `Vector2Int`/`(int, int)` — hoy `Vector2` es de `float`, no de `int`.

**P5 — Cerrar `Color`/`Rect` (ya anticipados en §3.1, nunca implementados).** Con `Vector2`/`Vector3`
ya probados y estables, el motivo original para posponerlos (validar el diseño de vectores primero)
ya no aplica. Sigue habierta la pregunta de diseño de entonces: `Color` en floats `[0,1]` o bytes
`[0,255]` (con `byte` ya disponible desde esta misma ronda de trabajo, la opción de bytes ahora tiene
un tipo natural donde antes no lo había) y `Rect` en `x,y,w,h` o `min,max`.

**P6 — Cerrar O4 + extensiones `IComparable<T>` (`List<T: IComparable<T>>.sort()` sin comparador,
`Sequence<T: IComparable<T>>.min()/max()/sorted()`).** Descrito en detalle en §6.2/O4 — se cataloga
aquí también porque es, en sí mismo, una adición de superficie (métodos nuevos), no solo una
corrección.

**P7 — `toString()`/`toDisplayString<T: object>()` en las colecciones (O2/O3 llevados a código).**
Igual que P6, ya descrito en detalle en §6.2 — entra en la lista de diez porque es superficie nueva,
no solo optimización.

**P8 — `StringBuilder`/`string`: `padLeft`/`padRight`/`repeat(string, n)`/`trim` con variantes
(`trimStart`/`trimEnd`).** No se ha podido confirmar desde la stdlib si el `string` built-in ya cubre
esto (vive en `SurtrStringBuiltIn.cs`, fuera del alcance de esta auditoría de `.surtr`) — se incluye
como pregunta abierta a resolver antes de implementar: si el built-in ya los tiene, esto se cae de la
lista; si no, son huecos de uso muy frecuente (formateo de HUD, tablas de depuración) que hoy solo se
pueden montar a mano concatenando espacios en un bucle.

**P9 — `Optional<T>`/`Result<T, E>` explícito, sin excepciones.** Menor prioridad que P1-P4: el
lenguaje ya tiene nulables reales y excepciones con jerarquía completa (`CLAUDE.md`, sección de
excepciones y trap-to-class), así que esto es "azúcar de estilo Rust/Swift" más que un hueco
funcional — pero es un patrón habitual para quien viene de esos lenguajes y quiere evitar excepciones
en rutas de error esperadas (parseo, validación) sin recurrir a un nulable que pierde el motivo del
fallo. Coste: bajo (una `value class` con dos casos, apoyada en `@Value`/enum interno); riesgo: bajo.

**P10 — `Vector2Int`/`Vector3Int` (vectores de coordenadas enteras).** Todo lo que usa `Vector2`/
`Vector3` para indexar una `Grid<T>` (P4) o una posición de tablero/tile necesita coordenadas enteras,
no `float` — hoy no existe ningún tipo así, y `Vector2`/`Vector3` no sirven para el caso (dividir con
`/` o comparar con `==` sobre floats en una posición de rejilla es exactamente el tipo de error sutil
que un tipo dedicado evita). Coste: bajo (misma forma que `Vector2`/`Vector3`, aritmética entera en
vez de flotante); riesgo: bajo. Encaja directamente como prerrequisito de P4.

### 6.3b B10 — Bug del compilador/runtime: un array de una `value class` de un solo campo, cruzando el `self` de una extensión genérica, corrompe (o revienta) `array.sort(comparator)` — Prioridad ALTA — **Descubierto, no corregido; superficie afectada retirada de la stdlib**

Descubierto implementando P6 (§6.2/O4): al intentar añadir `T[]`/`List<T>.sortNatural()` (ordenar sin
comparador para un `T : IComparable<T>`), usando `byte` como primer caso real. Como B8/B9, **falla en
silencio** en uno de sus dos modos — sin `SURTR4001`, sin excepción — así que solo se vio al comparar
el resultado, no al compilar ni al ejecutar sin más.

**Síntoma exacto, confirmado con tres sondas mínimas aisladas** (`SurtrCompilation`/`ModuleEmitter`,
sin pasar por disco):

| Camino | `T = int` (primitivo, no `value class`) | `T = byte` (`value class` de un campo) |
|---|---|---|
| `a.compareTo(b)` llamado directamente, sin lambda, dentro de una extensión genérica sobre `T[]` | Correcto | **Correcto** |
| Lo mismo a través de una lambda `(a, b) => a.compareTo(b)` invocada por bytecode Surtr normal (sin pasar por sort nativo) | Correcto | **Correcto** |
| `self.sort((a, b) => a.compareTo(b))` sobre el **propio parámetro `self: T[]`** de una extensión `T[]` | Correcto | **Incorrecto en silencio** — reordena mal (visto: `[3,1,2]` → `[2,3,1]` en vez de `[1,2,3]`) |
| Lo mismo sobre `List<T>` (extensión `List<T>.sortNatural()`, que internamente llama a `List<T>.sort(comparator)`) | Correcto | **Revienta**: `SurtrExecutionException: A 'int' cannot be cast to 'byte'` |
| El mismo patrón dentro de `Sequence<T>.sortedNatural()`/`minNatural()`/`maxNatural()` (Sequence.surtr) — el array de trabajo se construye **dentro** de un generador ya genérico, `push()` a `push()` | Correcto | **Correcto** |

**Causa raíz (hipótesis fundamentada, no confirmada con debugging directo del bytecode — a
diferencia de B6-B9, no se llegó a instrumentar el emisor esta vez; el patrón de evidencia es
consistente pero no se verificó leyendo el IL/bytecode generado):** un array **concretamente
tipado** `byte[]` en su sitio de construcción (p. ej. el literal `[byte(3), byte(1), byte(2)]`)
almacena sus elementos de un solo campo **sin boxear**, exactamente como predice §2.9 ("un `value
class` de un solo campo... erosiona al campo que envuelve") — el mismo motivo por el que `byte[]` e
`int[]` son indistinguibles en almacenamiento. Ese mismo array, al cruzar hacia el parámetro `self:
T[]` de una extensión (una extensión declara su **propio** parámetro de tipo, §15.4 — `T` ahí es
genérico/erosionado, no el `byte` concreto que el llamador tiene) **no se re-boxea** en ese cruce. El
`Compare()` nativo de `array.sort` (`SurtrCompositeBuiltIns.cs:184-195`) lee el elemento crudo con
`SurtrValue.FromRaw(items[left])` y lo pasa tal cual a `runtime.InvokeClosure(comparator, ...)` — pero
el comparador fue compilado contra el `T` erosionado de la extensión, que espera una referencia
boxeada (todo slot erosionado es referencia, §1.11), no un entero crudo. `Sequence<T>.sortedNatural()`
no lo sufre porque su array de trabajo se construye **dentro** del propio cuerpo ya-erosionado
(`items.push(source.current)`, con cada elemento boxeado al entrar, igual que hace cualquier otro
límite de erasure) — nunca es un array **concreto ajeno** que cruza la frontera desde fuera.
`minNatural()`/`maxNatural()` tampoco, porque ninguno llama a `array.sort`: recorren un
`IIterator<T>` e invocan el comparador con una llamada Surtr corriente (bytecode `Call`), no con el
`InvokeClosure` nativo de C# que usa `Compare()`.

**Impacto real:** cualquier `T[]`/`List<T>` con `T` un `value class` de un solo campo (`byte`, y
cualquier futuro `value class` de un campo — no `Vector2`/`Quaternion`, que son multi-campo y ya
tienen su propia clase de bugs, B6) que se ordene **a través de un método de extensión genérico**
(no un método declarado directamente en `List<T>` con su propio `T`, que no cruza esta frontera)
puede dar un orden incorrecto en silencio, o reventar. `int[]`/`List<int>` (y cualquier primitivo)
no están afectados — confirmado con el mismo repro exacto sustituyendo `byte` por `int`.

**Arreglo aplicado en la stdlib (mientras el bug sigue abierto):** se retiraron `T[].sortNatural()` y
`List<T>.sortNatural()` de `collections/List.surtr` antes de comitear nada — nunca llegaron a
publicarse rotos. `Sequence<T>.minNatural()`/`maxNatural()`/`sortedNatural()` sí se mantienen: los
tres están verificados correctos con `byte` mediante un test de regresión real
(`ComparableSequenceExtensionsWorkWithNoComparator`,
`src/Surtr.Tests/Stdlib/SurtrStdlibBehaviorTests.cs`). Queda una nota en el propio `List.surtr`
explicando por qué la superficie no está — para que nadie la vuelva a proponer sin saber por qué
falla, y para que se recupere en cuanto el compilador lo arregle.

**No corregido en el compilador** — a diferencia de B5-B9, esta ronda no llegó a instrumentar el
emisor para confirmar la causa exacta (la hipótesis de arriba está bien fundamentada por las cinco
filas de la tabla, pero no verificada leyendo bytecode real como sí se hizo con B6-B9). Candidato
natural de arreglo: que `SurtrCompositeBuiltIns.Compare` (o el punto donde se emite/resuelve la
llamada al comparador de `array.sort`) boxee el valor crudo leído del array antes de invocar el
closure, cuando el closure lo declare con un parámetro erosionado — simétrico al arreglo de B8
("UnboxIfStillErased" en escritura; aquí haría falta un "BoxIfNeededForClosureCall" en lectura).

### 6.3c B11 — Bug del compilador: un método invoca su propio parámetro-closure con el valor equivocado cuando ese closure devuelve el propio parámetro de tipo del método — Prioridad ALTA — **Descubierto, no corregido; `Result<T,E>.map/mapError/match` retirados antes de comitear**

Descubierto implementando P9 (`Result<T,E>`, §6.3). Como B10, se manifiesta en silencio — sin
`SURTR4001`, sin excepción — devolviendo un valor incorrecto.

**Repro mínimo, sin `Result` ni ningún campo de por medio** (confirmado con `SurtrCompilation`/
`ModuleEmitter`):

```surtr
fun apply<T>(v: T, f: (T) -> T): T { return f(v); }
fun run(): int { return apply(5, (v) => v * 100); }   // da 100, no 500 - "v" dentro de la lambda vale 1
```

Reproduce igual con dos parámetros de tipo separados (`apply<T, U>(v: T, f: (T) -> U): U`), con el
argumento de tipo explícito o inferido, y con el valor leído de un campo (`_payload as T`) en vez de
un parámetro — la condición común en los seis repros que sí fallaron es: un método (de instancia o
función suelta) recibe un valor de su **propio** genérico `T` y lo pasa, **en la misma llamada**, como
argumento a un **parámetro-closure** cuyo tipo de retorno es también un genérico propio del método
(`U`, o el propio `T`).

**Lo que NO reproduce, y por qué importa:** `Sequence<T>.map<U>(mapper)`/`groupBy<K>(keySelector)` —
ya en producción y cubiertos por tests que pasan — tienen la misma forma superficial y **no** están
afectados. La diferencia real: `map` nunca llama a `mapper` dentro de su propio cuerpo — lo guarda y
se lo pasa a un generador privado (`seqMap`) que lo invoca más tarde, fuera del método que lo
declaró; `groupBy` sí invoca `keySelector` en el sitio, pero contra un **local** (`iter.current`),
no contra un valor leído de un campo/cast. Ninguno de los dos coincide exactamente con la forma que
falla (invocación **síncrona**, **dentro** del mismo método que declara el genérico, sobre un valor
que **es** ese genérico).

**Causa raíz:** no confirmada — a diferencia de B6-B9, no se llegó a instrumentar el emisor ni a leer
el bytecode generado; el patrón de evidencia (siete repros, todos consistentes) es fuerte pero la
frontera exacta de qué combinación dispara el bug no quedó cerrada.

**Impacto real:** `Result<T, E>.map<U>()`, `.mapError<F>()` y `.match<U>(onOk, onError)` — las tres
retiradas de `core/Result.surtr` antes de comitear nada; nunca llegaron a publicarse rotas. El resto
de `Result<T,E>` (`ok`/`error`/`isOk`/`isError`/`unwrap`/`unwrapOr`/`unwrapError`) no invoca ningún
parámetro-closure y está verificado correcto (`ResultOkAndErrorRoundTripAcrossModules`,
`src/Surtr.Tests/Stdlib/SurtrStdlibBehaviorTests.cs`).

**No corregido en el compilador.** Candidato de investigación futura: instrumentar
`MethodBodyEmitter`/`EmitResolvedCall` para el repro mínimo de arriba y comparar contra el caso que
sí funciona (`Sequence.groupBy`) para aislar en qué paso exacto el valor se pierde o se sustituye.

### 6.3d B12 — Bug del parser: `generator<T>` no se acepta anidado dentro de otro `<...>` — Prioridad BAJA — **Descubierto al construir `Scheduler` (§6.3/P1); evitado en la stdlib con `unknown[]`, no corregido**

`generator<float>` como anotación de tipo suelta (parámetro, local, retorno) funciona bien, pero
`array<generator<float>>(n)` y `List<generator<float>>` fallan ambos a la primera con `SURTR2003:
Expected an expression, found KeywordGenerator`. Consistente con que `generator` es "la única palabra
reservada que también aparece en posición de tipo" (`CLAUDE.md`) — el reconocimiento contextual de
`generator` como tipo parece limitarse a la posición de anotación directa y no a un argumento de tipo
anidado dentro de otro genérico. `collections/Scheduler.surtr` guarda las corrutinas en un `unknown[]`
y castea de vuelta a `generator<float>` en cada lectura (`x as generator<float>`, que sí parsea al
ser un target de cast, no anidado) para esquivarlo por completo. No investigado más allá de
confirmar el síntoma — de baja prioridad porque tiene un rodeo de coste cero, pero documentado para
que la próxima vez que alguien quiera `List<generator<T>>` no lo redescubra desde cero.

### 6.4 Estado final de la Fase 8 e implementación real

Sesión posterior a §6.1-§6.3: se implementó **todo lo de §6.2/§6.3 salvo P2 (Signal, pedido
explícitamente fuera de alcance) y P8** (pendiente de confirmar qué ya cubre el `string` built-in —
no investigado), más P3 (JSON quedó **fuera** — ver nota abajo), y tres piezas nuevas que no estaban
en la lista original de diez: un `Scheduler` de corrutinas (`surtr.async`), una sección de ficheros
(`surtr.io.File`) y un sistema de compilación dinámica/`eval` (`Surtr.Stdlib.Script`, proyecto
opcional aparte). Todo verificado compilando y ejecutando contra el runtime real, con tests de
regresión en `src/Surtr.Tests/Stdlib/`.

**Lo que se implementó y quedó en verde:**

- **§6.1** — las tres sondas de la jerarquía `object` (`Set<Vector2>`, `Map<Vector2,int>`,
  `Set<Point>` con `equals`/`hashCode` reales) ahora son tests permanentes.
- **§6.2/O4 + P6** — `byte`/`Angle` implementan `IComparable<T>`/`IEquatable<T>` de verdad (sin
  `override`: satisfacer una interfaz corriente en Surtr **no** lleva ese modificador — solo
  `override`ar un miembro ya virtual de `object`/una clase base lo lleva; confirmado por el propio
  compilador, `SURTR3068`). `Sequence<T: IComparable<T>>.minNatural()/maxNatural()/sortedNatural()`
  añadidos y verificados correctos. **`T[]`/`List<T>.sortNatural()` NO se pudieron añadir** — ver B10
  abajo.
- **§6.2/O3 + P7** — `toDisplayString()` en `List`, `LinkedList`, `Set`, `Map`, `Queue`, `Deque`,
  `Stack`, `PriorityQueue`, `Sequence` e `IIterable<T>` (todas con `<T: object>`, y **nunca** llamadas
  `toString` — un miembro real de ese nombre, aunque sea el heredado de `object`, bloquea cualquier
  extensión del mismo nombre en el tipo, sin importar la aridad).
- **Assert/Contracts revisados** (pedido explícito): `assertEqual<T: object>(a, b)` y
  `assertNotEqual<T: object>` (2 args, sin mensaje) ahora dan un mensaje real ("Expected X but got
  Y") — coexisten sin colisión con las versiones de 3 args con mensaje explícito, porque una llamada
  de aridad exacta gana sobre una que necesitaría rellenar un default (confirmado empíricamente).
  `Contracts.surtr` gana `requireEqual<T: object>`/`ensureEqual<T: object>` con el mismo mensaje real.
- **P6.3/collection builders** (pedido explícito, no estaba en la lista original): `Map<K,V>` gana
  `each (key: K, value: V)` — `Map<string,int>{"a":1}` y `let m: IMap<K,V> = {...}` ya construyen de
  verdad — y `IDeque<T>` gana `default Deque<T>`, que le faltaba.
- **P10** — `Vector2Int`/`Vector3Int` en `math/Vector.surtr`, con `IEquatable<T>`.
- **P5** — `Color` (4 floats, `[0,1]`) y `Color32` (un `int` empaquetado `0xAARRGGBB`, tal como pidió
  el usuario explícitamente: "una que use un int y otra que use 4 floats"), con conversión en ambas
  direcciones; `Rect` (`x,y,width,height`, estilo Unity) en `math/Rect.surtr`.
- **P4** — `Grid<T>` (`collections/Grid.surtr`): array plano bajo índice `(x,y)`, `get`/`set`/`fill`,
  sin `operator[]` multi-índice (el lenguaje solo admite indexadores de un índice, §5.7 — documentado
  en el propio archivo).
- **P9** — `Result<T,E>` (`core/Result.surtr`): `ok`/`error`/`isOk`/`isError`/`unwrap`/`unwrapOr`/
  `unwrapError`. **`map`/`mapError`/`match` NO se pudieron añadir** — ver B11 abajo. Es una `class`
  ordinaria, no `value class` — el compilador rechaza un `value class` genérico de más de un campo
  (`SURTR3012`), aunque ninguno de los dos campos varíe realmente por sustitución. `Optional<T>`
  deliberadamente **no** se añadió: `T?` ya cubre exactamente ese hueco (§5.1), y añadir un segundo
  envoltorio habría sido la clase de abstracción redundante que `CLAUDE.md` pide evitar.
- **"Investiga más usos de corrutinas"** — `surtr.async.Scheduler`: registra corrutinas
  `generator<float>` donde cada `yield <segundos>` es una espera; `update(deltaTime)` las avanza.
  Además tres corrutinas ya escritas para usar directamente: `delay`, `repeatEvery`, `repeatTimes`.
  Ver B12 (bug de parser encontrado y evitado en el camino).
- **Sección de ficheros** — `surtr.io.File`: operaciones de fichero completo (`fileReadAllText`/
  `fileWriteAllText`/`fileReadAllBytes`/`fileWriteAllBytes`/`fileExists`/`fileDelete`/
  `createDirectory`/`directoryExists`/`listFiles`/`listDirectories`). **Limitación real, documentada
  en el propio archivo**: un fallo (fichero inexistente, sin permiso...) hoy **no es capturable por
  un `catch` de Surtr** — no existe una API pública para que un cuerpo `native` lance una excepción
  Surtr real (se buscó explícitamente y no hay); el error de `System.IO` cruza sin modificar hasta el
  `try/catch` del **host** en C#, no hasta el del script. No hay streaming (`FileStream` con handle)
  todavía — solo fichero completo, suficiente para config/guardado, que es el caso de uso dominante.
- **Compilación dinámica / `eval`** — proyecto nuevo y **separado**, `Surtr.Stdlib.Script`
  (netstandard2.1, referencia `Surtr.Core` + `Surtr.Compiler`), deliberadamente fuera de
  `Surtr.Stdlib` (que no referencia el compilador a propósito, para que un host normal no cargue con
  todo el front-end). Expone `surtr.script.Script`: `Script.compile(source)` compila una cadena como
  módulo nuevo y aislado (sin colisión entre llamadas), `.isValid`/`.lastError()` para un fallo de
  compilación (**no lanza** — no hay forma de que un `native` lance algo capturable, mismo motivo que
  arriba; el error es un valor, no una excepción), `.hasFunction(name)`, `.call(name, args:
  unknown...)` empareja por nombre y **aridad** (igual que `Surtr.Run`'s `EntryPoint.Resolve` — un
  artefacto compilado no tiene resolución de sobrecarga de fuente que rehacer). Más
  `evalInt`/`evalFloat`/`evalBool`/`evalString(expr)` como azúcar sobre lo anterior. Un driver
  compilado en el mismo proceso vía `SurtrCompilation` (no cargado desde una imagen) necesita añadir
  `SurtrScripting.ScriptModuleSource` a su propio `SurtrProject` para que el *binder* vea los símbolos
  declarados — `SurtrScripting.LoadInto`/`RegisterNativeBodies` por sí solos publican cuerpos y cargan
  en un runtime **ya en marcha**, después de que la compilación ya terminó, así que no sirven para
  resolver un `import` en tiempo de compilación. Documentado con detalle en los comentarios del propio
  `SurtrScripting.cs`. Seis tests end-to-end reales en
  `src/Surtr.Tests/Stdlib/SurtrScriptingTests.cs`.

**No implementado / fuera de alcance de esta ronda:**

- **P3 (JSON)** — no se llegó a implementar por presión de tiempo frente al resto de la lista. Sigue
  siendo la propuesta de mayor valor pendiente para una próxima ronda.
- **P8** — sin investigar.
- **P2 (Signal)** — excluido explícitamente por el usuario.
- **O1** (evitar la copia de `Map<K,V>.iterate()`) — sigue siendo un cambio de runtime, no abordado.

**Hallazgo adicional, fuera de la lista original — el límite real de `SurtrRuntime.Invoke` con
argumentos boxeados:** al construir `Script.call`, pasar argumentos ya boxeados (como llegan desde un
`unknown[]` de Surtr) directamente a `runtime.Invoke(method, args)` da un resultado **incorrecto en
silencio** en cuanto el método de destino declara un parámetro **primitivo concreto** (`int`, no
`unknown`) — `Invoke` no desboxea por su cuenta en ese caso (solo maneja el boxeo/desboxeo de
parámetros *inline*, no de un primitivo boxeado suelto). Un llamador nativo que reenvíe valores desde
un `unknown[]`/similar debe desboxear él mismo antes de invocar, mirando el `TypeCode` de cada
parámetro declarado (`method.Parameters[i].ParameterType.TypeCode`) y resolviendo un `SurtrBoxed` a
su `.BoxedValue` cuando ese tipo es primitivo. `SurtrScripting.ScriptCall` ya lo hace; no se investigó
si algún otro sitio del árbol tiene el mismo problema latente, porque ningún otro sitio existente
reenvía argumentos de esta forma exacta (boxeados, desde host, hacia un método de firma arbitraria).

Los cuatro bugs de compilador/parser descubiertos esta ronda (B10, B11, B12, más el hallazgo de
`Invoke` de arriba) están documentados con su repro exacto en §6.3b-d — ninguno corregido, todos con
la superficie afectada retirada o evitada antes de comitear código roto.
