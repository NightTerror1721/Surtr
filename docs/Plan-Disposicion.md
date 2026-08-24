# Plan-Disposicion — Disposición determinista: `IDisposable`, `using`, y el cierre de un `for-in`

> **Estado:** implementado (§7). Nace como prerequisito de la fase 3 de
> generadores (`docs/Plan-Generadores.md` §12.8), que necesita que un `finally` dentro de un cuerpo
> suspendido corra de verdad — pero se decide como **protocolo general del lenguaje**, no como
> mecanismo privado de los generadores, porque el hueco que tapa no es de los generadores.

---

## 1. El hueco

Surtr no tiene ninguna historia de disposición. No es una omisión de la biblioteca: es que **el
registro de entidades barre soltando la referencia, sin hook de finalización**. `SurtrRuntimeEntity`
no tiene destructor, `SurtrDictionary` no llama a nada al desalojar una entrada, y `SurtrObject`
declara `VisitReferences` para *marcar*, no para despedirse. Un objeto muere sin enterarse.

Eso es una decisión buena y deliberada — un finalizador es un callback en el peor momento posible
para una VM dentro de un presupuesto de frame — pero deja al lenguaje sin forma de decir «cuando
salgas de aquí, suelta esto», y sin forma de que un objeto reaccione a su propio final.

Hasta ahora nada lo pedía. Los tres consumidores que lo piden ahora:

| Quién | Qué necesita |
|---|---|
| **Generadores (fase 3)** | Un cuerpo suspendido dentro de un `try/finally` tiene código pendiente. Si nadie lo reanuda ni lo cierra, ese `finally` no corre nunca — donde CPython lo cierra por refcount y JS por el motor |
| **Handles nativos** | Un `SurtrNativeObject` que envuelve un `FileStream` o un `NativeArray` de Unity no tiene hoy dónde colgar su liberación salvo un método que hay que acordarse de llamar |
| **Los cursores de la stdlib** | `FlatMapIterator` sostiene un `IIterator<U>` interno vivo; abandonarlo a mitad no suelta nada |

## 2. Estudio comparado

### 2.1 C# — `IDisposable`, `using`, y `foreach` que dispone

El modelo de referencia, y el más cercano a Surtr porque parte del mismo sitio: recolección no
determinista, más un contrato explícito para lo que no puede esperar al recolector.

- `IDisposable` con un único `Dispose()`. Contrato de biblioteca, pero **el compilador lo conoce**:
  `using` y `foreach` bajan contra él por nombre.
- `using (var f = ...) { B }` baja a `try { B } finally { if (f != null) f.Dispose(); }`. Sin
  supresión: si `Dispose()` lanza mientras una excepción viajaba, la de `Dispose` gana y la
  original se pierde.
- **`IEnumerator<T> : IDisposable`**, y `foreach` llama a `Dispose()` en el `finally` de todo bucle
  sobre un enumerador genérico — normal, `break`, `return` o excepción. Es exactamente lo que hace
  que el `finally` de un iterador `yield return` corra al salir de un `foreach` por `break`.
- Idempotencia: por contrato, `Dispose()` llamado dos veces no debe fallar.

La lección que importa: **el cierre determinista de un iterador perezoso no es una pieza de los
iteradores, es una consecuencia de que el cursor sea disponible**. C# no tiene un `close` de
generador; tiene un `Dispose` de enumerador que la máquina de estados implementa corriendo sus
`finally`.

### 2.2 Java — `AutoCloseable` y try-with-resources

`AutoCloseable.close()`, y `try (Resource r = ...) { B }` como sentencia. Dos diferencias con C#
que hay que decidir a favor o en contra:

- **Suprime en vez de sustituir**: si el cuerpo lanza y `close()` también, la del cuerpo gana y la
  de `close()` se cuelga de ella como *suppressed*. Es más correcto y cuesta una lista por
  excepción.
- **Reusa `try`**, sin palabra reservada nueva.

Java **no** dispone el iterador de un `for-each`: `Iterator` no extiende `AutoCloseable`, y por eso
los `Stream` tienen que cerrarse a mano.

### 2.3 Python — `with`, y el refcount que tapa el resto

`with` sobre un protocolo de dos métodos (`__enter__`/`__exit__`), más `contextlib.contextmanager`,
que convierte un *generador* en un gestor de contexto — la dirección contraria a la nuestra.

Lo relevante es lo que Python **no** necesita: no hay que cerrar un generador abandonado, porque el
refcount lo hace, lanzando `GeneratorExit` dentro del cuerpo en cuanto la última referencia muere.
Esa es la garantía que Surtr no puede dar y no va a fingir que da (§5.3).

### 2.4 Rust, Go, Kotlin

| Lenguaje | Qué tiene | Lección |
|---|---|---|
| Rust | `Drop`, corrido por el compilador en el punto exacto en que el valor muere | La garantía perfecta, y exige *ownership* en el sistema de tipos. Fuera del alcance de Surtr por diseño |
| Go | `defer`, atado a la función y no al valor | Barato y explícito, pero no es un contrato: nada dice que un tipo *tenga* que cerrarse |
| Kotlin | `Closeable.use { }` — función de biblioteca, no sintaxis | Es lo que se puede hacer sin tocar el lenguaje; requiere lambdas y paga un cierre por recurso |

### 2.5 Resumen

| Decisión | C# | Java | Python | **Surtr** |
|---|---|---|---|---|
| Contrato | `IDisposable.Dispose` | `AutoCloseable.close` | `__exit__` | **`IDisposable.dispose`** |
| Sintaxis de ámbito | `using (...)` | `try (...)` | `with` | **`using (...)`** |
| Cursor disponible | sí | no | n/a (refcount) | **sí** |
| Supresión de secundarias | no | sí | sí | **no** (§5.2) |
| Cierre de lo abandonado | no | no | sí (refcount) | **no** (§5.3) |

---

## 3. Diseño

### 3.1 El contrato: built-in, aridad 0, un miembro

```surtr
public interface IDisposable {
    fun dispose(): void;
}
```

Vive en el módulo `surtr` junto a `IIterable`, `IIterator`, `IComparable` e `IEquatable`
(`SurtrStandardLibrary.DeclareCoreInterfaces`), y **no en la stdlib**, por dos razones que no son
de gusto:

1. **El emisor tiene que nombrarlo.** `MethodBodyEmitter` ya nombra `SurtrBuiltIns.IIterable` para
   bajar un `for-in`; resolver en cambio un nombre de un módulo de la stdlib sería la primera vez
   que el compilador depende de que un módulo concreto esté cargado.
2. **La clase built-in `generator` tiene que implementarlo**, y se construye en el constructor
   estático de `SurtrBuiltIns`, antes de que exista ningún módulo de stdlib que cargar.

Se llama `dispose()` y no `close()` porque el generador de la fase 3 no debe acabar con dos
métodos que significan lo mismo, y porque el árbol ya apuntaba en esa dirección: hay un
`IDisposable<T>` en `src/Surtr.Stdlib/src/surtr/core/Contracts.surtr` — **huérfano** (nadie lo
referencia, ningún documento lo menciona) y **mal declarado** (genérico sin usar el parámetro).
Se borra y lo sustituye éste.

`ReservedInterfaceIds` crece en uno. Es un contador *process-wide* del contexto, no un valor que
viaje en disco, así que no hay bump de `SurtrModuleImage.FormatVersion` por esto.

### 3.2 `IIterator<T>` extiende `IDisposable`

```surtr
public interface IIterator<T> : IDisposable {
    fun moveNext(): bool;
    let current: T;
}
```

Es la decisión de C# y por el motivo de C#: **el cierre determinista de un iterador perezoso es una
consecuencia de que el cursor sea disponible**, no una pieza aparte. Con esto, un `for-in` sabe
*estáticamente* que tiene algo que cerrar y no hace ninguna pregunta en tiempo de ejecución — ni un
`InstanceOf` por bucle, ni un test por elemento.

Y cierra el agujero que ninguna otra opción cerraba: `iterate()` está declarado devolviendo
`IIterator<T>`, así que un generador que viaje como `IIterable<int>` se recorre por el camino
general — y ahí el tipo estático del cursor es la interfaz. Si la interfaz no es disponible, ese
generador no se cierra nunca por mucho que el objeto concreto sepa cerrarse.

El precio es real y se paga una vez: **los cursores de la stdlib ganan un `dispose()` vacío**.
Las interfaces de Surtr no admiten implementación por defecto (regla dura: `SurtrInterface.AddMethod`
la rechaza para que las tablas de despacho puedan darlo por hecho), así que no hay forma de
evitarlo. Se acepta porque la alternativa — preguntar en tiempo de ejecución — mete una pregunta
dinámica en un lenguaje que presume de no necesitarlas, y porque un cursor que sostiene un cursor
interno (`FlatMapIterator`, `ChainIterator`) tiene algo que soltar de verdad.

`SurtrIteratorBuiltIns.DeclareIterator` añade el `dispose` de la clase `iterator` built-in, que es
un no-op: un cursor sobre un array o sobre un snapshot de claves no sostiene nada que soltar. Es
`Virtual`, como el resto de la superficie de iteración, porque el despacho por interfaz resuelve
por la vtable del receptor.

### 3.3 `using`

```surtr
using (let file = openFile("data.bin")) {
    process(file);
}
```

`using` pasa a **reservada dura** (§1.2). Contextual no vale: `using (x)` en posición de sentencia
es indistinguible de llamar a una función llamada `using`, y §1.2 resuelve ese tipo de ambigüedad
reservando. El coste de reservarla es cero medido: **ninguna fuente `.surtr` del árbol usa `using`
como identificador**.

Se elige sobre el `try (...)` de Java a pesar de que aquélla no habría costado palabra nueva, porque
`using` dice lo que hace y `try` dice lo contrario de lo que hace — la construcción de Java no
maneja excepciones, las hereda de compartir sentencia. Un lector que ve `try` espera manejo de
errores; en `try (r) { }` sin `catch` ni `finally` no hay ninguno.

Reglas:

- El recurso se declara con `let` (nunca `var`: reasignarlo dejaría al `finally` cerrando otra cosa)
  y su tipo debe satisfacer `IDisposable`, o es error de compilación con el tipo en el mensaje.
- Varios recursos en una sentencia se escriben separados por `,` y se cierran **en orden inverso**
  al de apertura, porque el segundo puede depender del primero.
- Un recurso de tipo nullable se admite y no se cierra si es null.
- El recurso es de solo lectura dentro del bloque, y su ámbito es el bloque.

**Baja a `try/finally` y a nada más.** `using (let f = e) { B }` es exactamente:

```
let f = e;
try { B } finally { if (f != null) { f.dispose(); } }
```

Eso significa cero rutas de emisión nuevas: `MethodBodyEmitter.EmitTry` ya construye la región
protegida, el catch-all, el `$raised` y el re-lanzamiento. El lowering vive en el **binder**, no en
el emisor, porque no depende de ninguna decisión de representación — a diferencia de `for-in`, donde
sí (§3.4).

### 3.4 `for-in` cierra su cursor

El camino general ya declara el cursor en un local; ahora el bucle entero va dentro de una región
protegida cuyo `finally` lo cierra. Normal, `break`, `return` y excepción, las cuatro salidas.

Entrar en un `try` en esta VM **no cuesta nada** — `SurtrBytecodeMethodInfo.Handlers` es una tabla
de rangos, no hay opcode de entrada — así que lo que un bucle paga de más es una llamada de
interfaz al salir. Una por bucle, no por elemento.

El camino rápido sobre `generator<T>` cierra igual, pero sin llamada de interfaz: el tipo es
estáticamente un generador, así que llama a su `dispose` directamente.

Los caminos que **no** cambian son los que no tienen cursor: un `for-in` sobre un array, una tupla,
un dict, un `range` o un `string` baja a un bucle indexado y no crea nada que cerrar. Es decir, la
inmensa mayoría de los bucles del árbol no paga absolutamente nada por esta sección.

### 3.5 Qué hace `dispose()` sobre un generador

Lo cuenta `docs/Plan-Generadores.md` §15: entra al frame suspendido y lanza dentro un
`GeneratorExit`, que ningún `catch` tipado atrapa y solo un `finally` ve. Es lo que convierte esta
sección en la condición de posibilidad de la fase 3, y la razón de que el protocolo se decidiera
antes que ella.

---

## 4. Impacto

| Pieza | Cambio |
|---|---|
| `SurtrStandardLibrary.DeclareCoreInterfaces` | declara `IDisposable` (aridad 0) y hace que `IIterator` lo extienda |
| `SurtrBuiltIns` | `IDisposable` como campo, `ReservedInterfaceIds` +1 |
| `SurtrIteratorBuiltIns` | `dispose` no-op en la clase `iterator` |
| `SurtrGeneratorBuiltIns` | `dispose` real en la clase `generator` |
| `src/Surtr.Stdlib` | un `dispose()` vacío por cursor; se borra `IDisposable<T>` de `Contracts.surtr` |
| Léxico | `using` reservada dura |
| Sintaxis | `UsingStatementSyntax` |
| Binder | `BindUsing`, comprobación del contrato, lowering a `try/finally` |
| Emisor | `for-in`: región protegida y cierre en los dos caminos |
| Documentación | `Language-Syntax.md` §1.2 y una sección nueva; `CLAUDE.md` |

**Sin bump de formato de imagen.** No cambia ningún opcode, ningún descriptor, ningún código de
tipo de valor y ninguna estructura del `.surtrc`. Lo único que se mueve es el contador de ids de
interfaz, que se reparte por contexto en cada carga.

## 5. Lo que deliberadamente no entra

### 5.1 `Drop` determinista general

Que *cualquier* objeto se entere de su propia muerte exige ownership en el sistema de tipos (Rust) o
refcount (Python/Swift), y las dos cosas son decisiones de lenguaje mucho más grandes que ésta y
contrarias al modelo de recolección de este proyecto. `IDisposable` es explícito por diseño: lo que
tiene que cerrarse lo dice, y quien lo sostiene lo cierra.

### 5.2 Supresión de excepciones secundarias

Si el cuerpo lanza y `dispose()` también, gana la de `dispose()` y la original se pierde — el
comportamiento de C#, no el de Java. Guardar la suprimida exige un campo lista en `Exception`, una
forma de leerlo, y decidir qué hace con él el `toString()` de una cadena de excepciones. Es una
mejora aislable que no bloquea nada, y cuesta más que lo que arregla mientras no haya un caso real.

### 5.3 Cerrar lo que nadie cierra

Un `IDisposable` que se guarda en un campo y se abandona no se cierra nunca. Un generador guardado
en una variable y no recorrido no corre su `finally` nunca. **Esto es un hueco enunciado, no un
descuido**: es exactamente la posición de C# y de Java, y taparlo exige el refcount de Python, que
es la decisión de recolección que este proyecto no tomó.

Lo que sí se garantiza, y es lo que la fase 3 necesita: **todo `for-in` cierra lo que recorre**, y
`using` cierra lo que abre. Las dos formas normales de consumir un recurso en el lenguaje son
seguras; salirse de ellas es explícito.

### 5.4 Disposición asíncrona

No hay scheduler ni bucle de eventos en Surtr (`Plan-Generadores.md` §8), así que un
`IAsyncDisposable` no tendría con qué componerse.

## 6. Plan de implementación

1. **Runtime**: `IDisposable` built-in, `IIterator` extendiéndolo, `dispose` en `iterator`.
2. **Stdlib**: los `dispose()` vacíos y el borrado del contrato huérfano; el árbol vuelve a compilar.
3. **Lenguaje**: `using` reservada, sintaxis, binder, lowering.
4. **`for-in`**: cierre en los dos caminos, con caso de banco para medir el sobrecoste por bucle.
5. **Documentación**: §1.2 y la sección nueva de `Language-Syntax.md`, `CLAUDE.md`.

A partir de ahí, `docs/Plan-Generadores.md` §15 construye la fase 3 encima.

---

## 7. Implementado

Todo el diseño de §3 está en el árbol, sin desviaciones. Lo que sigue es lo que se aprendió al
escribirlo.

### 7.1 El coste real fue el que §3.2 anticipó

**24 cursores de la stdlib ganaron un `dispose()`**, más dos en fuentes de test y uno en el banco.
El diagnóstico del compilador (`SURTR3043`) los nombró uno a uno, así que fue mecánico — pero la
mitad de los de `Sequence.surtr` no son vacíos: `FilterIterator`, `MapIterator`, `TakeIterator` y
compañía sostienen un `IIterator<T>` interno y **propagan el cierre**. Eso es lo que convierte el
protocolo en algo útil de verdad: un `Sequence` construido sobre un generador cierra el generador
cuando el bucle se va, atravesando todas las etapas que lo envuelven.

### 7.2 Dos cosas que el diseño no había previsto

**La pila de `finally` pendientes tenía que dejar de guardar nodos.** `MethodBodyEmitter` mantenía
un `List<BoundStatement>` para que un `return` dentro de un `try` corriera los bloques que se salta.
El cierre de un cursor no es un nodo del árbol enlazado — es una decisión de representación que se
toma en el emisor —, así que la lista pasó a guardar *emisores*. Con eso, un `return` desde dentro
de un `for-in` cierra el cursor por el mismo mecanismo que un `finally` escrito a mano.

**El cierre tuvo que ganar dos caminos rápidos.** Cada `for-in` sobre un cursor acaba ahora en un
`dispose()`, así que la ruta importa: si el generador no delega no se construye la lista de la
cadena, y si nada en el cuerpo protege el punto de suspensión no hay `finally` que correr, así que
no se monta el frame ni se construye la señal de cierre. Sin la segunda, un `break` sobre un
generador cualquiera asignaba una excepción.

**La forma del bucle es la de C#, y no por gusto.** La región protegida envuelve el bucle *entero*,
etiqueta de `break` incluida, para que un `break` salga del bucle **sin salir de la región** y el
cierre corra exactamente una vez a la salida. Hacer que el `break` corriera el cierre él mismo lo
habría cerrado dos veces por ese camino.

### 7.3 Un efecto de borde real, y es correcto

Un trap que escapa de un `for-in` — una división por cero dentro del cuerpo de un generador, por
ejemplo — ahora sale como `SurtrThrownException` en vez de como la excepción CLR cruda: el
catch-all que cierra el cursor lo atrapa, lo convierte en el objeto Surtr de la clase que
`docs/VM-Plan.md` §1.9 le asigna, corre el cierre y lo relanza. Es exactamente lo que ya pasaba con
un `try/finally` escrito a mano alrededor del mismo bucle, y la clase se conserva, así que un
`catch (e: DivideByZeroException)` sigue tomándolo. Dos tests que afirmaban el tipo CLR se
actualizaron para afirmar el tipo Surtr, que además es la aserción con más contenido.

### 7.4 Lo que midió el banco

50 000 elementos, `-c Release`, mediana de 31 iteraciones. La pregunta era si el cierre se nota:

| Caso | antes | después | Qué cambió |
|---|---|---|---|
| `genYield` | 1.43 ms | **1.42 ms** | el bucle lleva ahora región protegida y una llamada de cierre |
| `iterator` | 3.43 ms | 3.17 ms | ídem, por el camino de interfaz |
| `forIn` | 0.67 ms | 0.66 ms | sin cursor, así que sin cierre |

**No se nota**, que es la afirmación de §3.4: entrar en una región protegida no cuesta ninguna
instrucción en esta VM, y el cierre es **una llamada por bucle, nunca por elemento**. Un bucle sobre
un array, una tupla, un dict o un rango no crea cursor y no paga absolutamente nada.

Y **el cierre no asigna** en el caso normal. `DisposeGenerator` mira dos cosas antes de montar nada:
si el generador no delega, no construye la lista de la cadena; y si nada en el cuerpo protege el
punto de suspensión, no hay `finally` que correr, así que ni entra al frame ni construye el
`GeneratorExit`. Un `break` sobre un generador que no escribió ningún `try` — que es la mayoría —
cuesta una llamada y dos comparaciones.

### 7.5 Lo que sigue abierto

- **§5.2 sigue abierto por decisión**: si `dispose()` lanza mientras viajaba otra excepción, gana la
  de `dispose()`. Aislable, y no bloquea nada.
- **§5.3 sigue siendo el hueco enunciado**: un `IDisposable` guardado y abandonado no se cierra
  nunca.
- **`EmitArrayFromIterable` no cierra su cursor**, y es correcto: `int[](secuencia)` agota la
  secuencia por construcción, así que el cursor ya terminó y el cierre sería una llamada a un
  no-op.
- **Nadie usa `using` todavía en el árbol.** No hay ficheros ni handles nativos en la stdlib; la
  construcción existe para cuando los haya, y su primer consumidor real es el `dispose()` de un
  generador.
