# Plan-Generadores — Investigación: funciones generadoras (`generator`) para Surtr

> **Estado:** fase 1 implementada (§13). Recoge el estudio comparado de generadores en otros
> lenguajes, el diseño para Surtr — palabra clave `generator` como introductora de miembro, tipo
> built-in `generator<T>` hermano de `SurtrClosure` — las tres estrategias sobre la VM propia con
> su recomendación, la evaluación del encadenado (§11), las decisiones cerradas (§12) — que ganan
> sobre §3, §5 y §6 — y lo que la fase 1 dejó construido y medido (§13).

---

## 1. El problema que resuelven

Hoy escribir un iterador perezoso en Surtr exige una clase que implemente `IIterator<T>` a mano,
exactamente como los doce iteradores de la stdlib (`MapIterator<T,U>`, `FilterIterator<T>`,
`TakeIterator<T>`... en `surtr/collections/Sequence.surtr`): un campo de estado, un `moveNext`
con su bandera de agotado y un `current`. Cada pipeline nuevo es una clase nueva con el mismo
esqueleto repetido. Un generador mueve ese boilerplate al compilador:

```surtr
// Hoy: una clase completa por transformación.
class RangeIterator<T> : IIterator<T> { ... estado a mano ... }

// Con generadores: el cuerpo dice el bucle; el compilador guarda el estado.
generator fun countdown(from: int): int {
    var i = from;
    while (i >= 0) {
        yield i;
        i = i - 1;
    }
}
```

La llamada `countdown(5)` **no ejecuta el cuerpo**: devuelve un valor perezoso que produce un
elemento cada vez que alguien le pide el siguiente, recordando dónde se quedó entre petición y
petición.

## 2. Estudio comparado

### 2.1 C# — iteradores compilados a máquinas de estados

`yield return` aparece solo en métodos cuyo tipo de retorno es `IEnumerable<T>` o
`IEnumerator<T>`; el retorno declara el tipo del *elemento*. El compilador reescribe el cuerpo
completo como una clase anidada: cada variable viva se convierte en campo, el flujo se numera en
un `int state`, y `MoveNext` es un `switch` de reentrada. Consecuencias que importan:

- **Reanudable por construcción**: cada llamada a `GetEnumerator()` da un enumerador *nuevo* que
  vuelve a ejecutar el cuerpo desde el principio (dos bucles sobre el mismo `IEnumerable` ven dos
  recorridos completos).
- **`yield` no cruza llamadas**: solo puede estar léxicamente dentro del cuerpo del iterador. Un
  lambda interior que haga `yield` es un error. Es la restricción que hace barata toda la
  implementación.
- `try/catch` con `yield` dentro del `try` está prohibido; `try/finally` está permitido y el
  `finally` corre cuando el enumerador se descarta o termina.
- Evaluación perezosa y diferida por definición; los argumentos se capturan al llamar, no al
  iterar.

### 2.2 TypeScript / JavaScript — objetos generador de un solo uso

`function*` devuelve un objeto generador que implementa a la vez iterador e iterable. Diferencias
que pesan:

- **Un solo uso**: llamar a `f()` da *un* generador; recorrerlo dos veces no reinicia nada. Para
  reiniciar, se llama a `f()` otra vez.
- `yield*` delega en otro generador (aplana sin escribir bucles).
- Comunicación bidireccional: `gen.next(valor)` inyecta un valor en el punto de `yield`, y
  `.throw()`/`.return()` terminan el cuerpo desde fuera. Es la base de los efectos algebraicos
  caseros y de `redux-saga`. Para un lenguaje estático de juegos esto es poder (y superficie de
  errores) que Surtr no necesita en una primera fase.
- Implementación en V8/JSC: máquina de estados sobre contexto guardado, conceptualmente lo mismo
  que C# pero gestionada por el motor.

### 2.3 Python — el original moderno

Los generadores de Python son la misma idea de JS con semántica de reinicio igualmente
«un solo uso», más `yield from` (delegación) y la evolución histórica hacia corrutinas
(`send`/`throw`/`await`). La lección relevante: **la delegación (`yield*`) es la mitad del
valor diario** — componer generadores sin bucles manuales — y se puede añadir después sin
romper nada.

### 2.4 Kotlin — no hay generadores nativos

Kotlin eligió `Sequence<T>` perezoso + funciones de orden superior (`map`/`filter` construyen
nuevas secuencias envolviendo iteradores), y aparte corrutinas para suspensión general. La
stdlib de Surtr ya copió la mitad de `Sequence`: lo que falta es exactamente la parte que
Kotlin también echa de menos — escribir iteradores nuevos sigue siendo verboso. Los
*builders* de secuencia (`sequence { yield(x); yieldAll(rest) }) son la aproximación de
biblioteca a los generadores; requieren corrutinas internas, así que en Surtr esa vía solo
existiría *sobre* generadores de lenguaje.

### 2.5 Rust, Lua, Java

| Lenguaje | Qué tiene | Lección para Surtr |
|---|---|---|
| Rust | Generadores nightly; `async` se apoya en ellos | La transformación a máquina de estados es la vía «seria»; también la más cara de mantener |
| Lua | Corrutinas simétricas completas, no generadores | Suspensión *arbitraria* (cruzar llamadas) exige pila por-corutina: caro y no hace falta para iteración |
| Java | Nada nativo; `Stream` + `Spliterator` a mano | El destino de un lenguaje sin generadores: bibliotecas verbosas o APIs de callbacks |

### 2.6 Resumen del estudio

| Decisión | C# | JS/TS | Python | Recomendado en Surtr |
|---|---|---|---|---|
| Reinicio por llamada | Sí (por enumerador) | No (un solo uso) | No | **Sí** — casa con `IIterable<T>` y `for-in` |
| Delegación `yield*` | — (`foreach` interno) | Sí | Sí | Fase 2 (azúcar sobre `for-in`) |
| Inyección bidireccional | No | Sí | Sí | **No** — potencia innecesaria, rompe el tipado simple |
| `yield` cruzando llamadas | No | No | No | **No** — misma restricción, misma razón |
| `try` alrededor de `yield` | Prohibido con catch | Permitido | Permitido | **Prohibido en fase 1** (ver §5.4) |

La decisión de reinicio es la que alinea todo lo demás: si `countdown(5)` devuelve algo que
satisface `IIterable<int>`, entonces `for (i in countdown(5))` funciona sin tocar el lenguaje, y
los generadores componen con `Sequence<T>` gratis.

---

## 3. Diseño propuesto

### 3.1 Superficie

```surtr
generator fun digits(n: int): int {
    var x = n;
    if (x == 0) { yield 0; }
    while (x > 0) {
        yield x % 10;
        x = x / 10;
    }
}

fun run(): int {
    var sum = 0;
    for (d in digits(4021)) {   // digits(...) satisface IIterable<int>
        sum += d;               // 4 + 0 + 2 + 1
    }
    return sum;                  // 7
}
```

- `generator fun` sustituye a `fun`; **el tipo de retorno declara el elemento**, como C#.
  `digits` tiene tipo de vista `generator<int>` (§3.2).
- `yield expr;` es una sentencia: entrega el valor y suspende hasta el siguiente pedido. El tipo
  de `expr` se convierte contra el elemento declarado, igual que un `return`.
- `return;` (o llegar al final) agota el generador. `return expr;` con valor es error — un
  generador no produce un resultado final además de sus elementos.
- Llamar a `digits(...)` **no ejecuta nada del cuerpo**: crea el objeto generador capturando los
  argumentos. Ejecutar es iterar.

### 3.2 El tipo built-in: `generator<T>`

Hermano de `closure`, con la misma filosofía de borrado:

```surtr
let g: generator<int> = digits(99);
let xs: IIterable<int> = g;      // satisface el contrato, sin declaración extra
```

- **Descriptor**: símbolo desnudo `'Y'` — el elemento va borrado, igual que `closure` borra sus
  parámetros y `range` borra ser rango. El elemento vive en los descriptores genéricos de
  *metadatos* (`generator` declara `T` como `H0`/`G0` según posición) para reflexión y para
  re-chequeo cross-module, exactamente como `Plan-Genericos-Metadata.md` retiene constraints.
- **Runtime — `SurtrGenerator : SurtrObject`**, moldeado sobre `SurtrClosure`:

| Campo | Papel (copiado de `SurtrClosure` donde aplica) |
|---|---|
| `Method`, `ImplKind`, `EntryPoint`, `Chunk`, `CodeOffset`, `LocalCount`, `MaxStackSize` | idénticos: todo lo necesario para construir/reanudar el frame sin buscar en metadatos |
| `UpValues` | capturas congeladas, mismo modelo de valores finales que los cierres |
| `Arguments` | los valores con que se llamó a la función generadora (slot inicial del frame) |
| `Slots` | buffer de slots del frame suspendido; `null` hasta el primer `yield` |
| `State` | `-1` = no iniciado · `0` = suspendido tras un `yield` · `1` = agotado |

- **Semántica de iteración**: `generator<T> : IIterable<T>` por declaración del built-in (como
  `range` declara el suyo). `iterate()` sobre el *mismo* objeto generador respeta el cursor
  compartido (un solo uso, estilo JS); llamar de nuevo a la **función** crea un generador nuevo y
  reinicia (estilo C# en lo que importa). Así `for-in` sobre `digits(5)` siempre recorre completo,
  y quien guarde `g` y lo recorra dos veces ve el segundo recorrido vacío — el comportamiento
  menos sorprendente para quien viene de cualquier lenguaje con generadores.

### 3.3 Convención de ejecución

Un generador es una tercera forma de llamada junto al método normal y el cierre:

1. **Creación** (opcode nuevo `GenNew`, ver §4): construye `SurtrGenerator` con método, capturas y
   argumentos; el cuerpo no arranca.
2. **Primer `iterate()/moveNext`**: la VM monta el frame igual que una llamada normal — argumentos
   y capturas en slots, `IP` al inicio del cuerpo — y ejecuta hasta el primer `yield` o fin.
3. **`yield v`**: el frame *no se destruye*: su región viva se copia a `Slots`, el punto de
   reentrada queda en `State`, y `v` sube como resultado de la llamada anidada. Para el que llama,
   un `yield` se comporta como un `Ret` de un slot.
4. **Reanudación** (`moveNext` siguiente): la VM reconstruye el frame desde `Slots`, apila el
   punto de reentrada y continúa.

El contrato `IIterator<int>` que alimenta `for-in` lo fabrica el propio built-in: `iterate()`
devuelve un iterador ligero cuyo `moveNext` es literalmente «reanuda el generador y dime si
produjo algo».

---

## 4. Las tres estrategias de implementación sobre esta VM

Aquí está el corazón de la investigación. La VM de Surtr es una máquina de pila con frames
contiguos en un array plano (`frames[]`, `MemOps` para copiar bloques de slots, presupuesto de
stack comprobado por llamada). Suspender un cuerpo vivo equivale a preguntarse qué pasa con su
frame entre `yield` y la reanudación.

### Estrategia A — Máquina de estados en el compilador (C#/Rust)

El compilador reescribe el cuerpo: cada variable viva a través de un `yield` pasa a campo de un
registro de estado (un `SurtrInstance` sintético), el flujo se numera, y el cuerpo compilado
empieza con un `switch(state)` que salta al punto de reanudación. La VM no cambia: un `yield`
es un `Ret` normal y la reanudación una llamada normal.

- **A favor**: cero cambios de VM; el frame muere en cada `yield` como el de cualquier función;
  compatible con el presupuesto de stack tal cual; es el camino con más precedentes (C#, Rust,
  JS engines).
- **En contra**: análisis de vida de variables y numeración de flujos en un compilador nuevo —
  la pieza más delicada del front-end; promociona cada local capturado a heap aunque el
  generador se consuma inline; los saltos entre estados complican FlowAnalysis y los handlers de
  excepciones (offsets remapeados dos veces).

### Estrategia B — Copiar el frame al suspender (recomendada)

Aprovechar lo que esta VM ya tiene: frames planos de slots sin tipo. En `yield`, la copia de la
región viva del frame — locales + operandos pendientes, `[frameBase .. sp)` — a `Slots` del
`SurtrGenerator` es un `MemOps.Copy`; en la reanudación, otro de vuelta. El `IP` de reentrada viaja
dentro del propio bloque copiado (se guarda al lado).

- **A favor**: superficie mínima en el compilador — el cuerpo se compila *igual que hoy*, sin
  numerar estados ni promover locales; `yield`/reanudar son dos opcodes nuevos (`Yield`,
  `GenResume`) más un par de ramas en el despacho de llamadas; el coste por `yield` es una copia
  lineal de slots ya medida como operación básica de esta VM (`LoadValueLocal`, los packs y los
  pasos de interop hacen lo mismo); encaja con el presupuesto porque el frame del generador deja
  de ocupar pila durante la pausa — el stack *baja* mientras el generador espera.
- **En contra**: una asignación por `yield` (el buffer de slots) — mitigable reusando `Slots` entre
  yields del mismo generador; restricción idéntica a C#: solo se puede hacer `yield` en el cuerpo
  léxico del generador, nunca dentro de una función llamada por él (su frame ya no existe para
  copiar). Esa restricción es aceptada por todos los lenguajes del estudio.
- **Riesgo real y su mitigation**: los handlers de excepciones y las etiquetas del frame
  suspendido viajan con la copia (son valores del frame); el recolector traza `Slots` como array
  de valores etiquetados, lo mismo que `UpValues`.

### Estrategia C — Frames en heap siempre (Lua)

Cada frame vive en su propio buffer dinámico desde el principio; suspender es simplemente dejar
de ejecutarlo.

- Descartada: convierte la regla de oro del rendimiento de este proyecto («una llamada comprueba
  stack, empuja frame y despacha con un `switch`») en una gestión de memoria por llamada. Es el
  precio de la suspensión arbitraria — cruzar `yield` dentro de funciones llamadas — que §2
  descarta por diseño.

### Recomendación

**B primero, A como optimización futura si el perfil lo pide.** B entrega la semántica completa
con dos opcodes y una clase nueva; su límite (no cruzar llamadas) es exactamente el estándar de
la industria. Si algún día el coste de copia por `yield` apareciese en un perfil real, la
máquina de estados de A se puede aplicar *selectivamente* a los generadores calientes sin
cambiar ni una línea de código fuente Surtr — los dos modelos compilan el mismo árbol.

---

## 5. Reglas de tipado y semántica fina

### 5.1 Tipos

| Regla | Decisión |
|---|---|
| Tipo de vista de `generator fun f(): T` | `generator<T>` |
| `generator<T>` frente a `IIterable<T>` | satisface por declaración del built-in (como `array<T>`) |
| Conversión de `yield e` | implícita de `typeof(e)` a `T`, mismas reglas que `return e` |
| `generator<T>` como tipo escrito | válido en cualquier posición de tipo; borra a `'Y'` |
| Varianza | `generator<T>` covariante en `T` *cuando* exista varianza (ver Plan-Varianza); hasta entonces invariante como todo |
| `unknown` | entra y sale como todo valor borrado: boxing al entrar al generador, cast al salir |

### 5.2 Restricciones de emisión (errores de compilación)

| Contexto | Regla |
|---|---|
| `yield` en `constructor`, `operator`, accessor | rechazado — no hay elemento que entregar |
| `yield` dentro de un lambda | rechazado en fase 1 (la lambda compila como cierre aparte; su frame no es copiable desde el generador) |
| `yield` dentro de `const fun` | rechazado — el plegado ejecutaría el cuerpo parcialmente en compilación |
| `return expr;` en generador | rechazado; solo `return;` o caída al final |
| `yield` dentro de `try` | prohibido en fase 1 (ver §5.4); `try` *después* del `yield` es legal |
| `yield` en un generador declarado en interfaz | fase 2 — requiere decidir cómo viaja el flag en metadatos |

### 5.3 Capturas y estado

Las variables capturadas por el generador siguen el modelo de cierres de Surtr: **valores
finales congelados** al crear el generador. Las variables *locales del cuerpo* mutables a través
de un `yield` viven en el frame copiado — la estrategia B las mantiene en su sitio sin
promoción a heap, que era el coste oculto de la estrategia A.

### 5.4 Excepciones y `finally`

Fase 1: `yield` dentro de `try` prohibido. Razón honesta: con frames copiados, un `finally`
pendiente durante una pausa indefinida plantea preguntas de semántica (¿corre si nadie reanuda?
¿cuándo?) que C# tardó años en fijar y que aquí no valen el bloqueo de la fase 1. `try/catch`
alrededor del *consumo* del generador funciona como siempre; las excepciones lanzadas dentro del
cuerpo propagan en el momento del `moveNext` que las alcanza y agotan el generador (`State = 1`).

### 5.5 Interacción con `for-in` y `Sequence<T>`

`for (x in genExpr)` baja por el camino general de `IIterable<T>` sin cambios. Y aparece la
combinación natural:

```surtr
generator fun primesBelow(limit: int): int { ... }
let s: Sequence<int> = ...;
for (p in primesBelow(1000)) { ... }          // directo
// composición: Sequence ya envuelve IIterable<T>
```

La stdlib puede reescribir sus iteradores internos uno a uno sin cambiar su API pública; los
doce `XxxIterator<T>` son candidatos a convertirse en generadores privados, con tests de
equivalencia que ya existen.

---

## 6. Formato, opcodes y metadatos

| Pieza | Cambio |
|---|---|
| Descriptores | símbolo desnudo nuevo `'Y'` para el valor generador (borrado); el elemento viaja como parámetro genérico del miembro en metadatos, no en el descriptor de almacenamiento |
| Opcodes | `GenNew` (crea generador desde método + capturas + args, no ejecuta), `GenResume` (reanuda; usado por el built-in), `Yield` (suspende entregando un slot). Ninguno con formato de imagen nuevo más allá del byte de opcode |
| Imagen | bump menor si los flags de método ganan el bit *is-generator*; alternativamente se deduce del cuerpo (primer opcode `Yield`) — decisión de implementación, no de formato |
| Reflexión | `SurtrMethodInfo` expone `IsGenerator` y el tipo elemento; `SurtrGenerator` participa del registry como cualquier objeto |

## 7. Rendimiento esperado

Comparado con el patrón actual (clase `IIterator` + dos llamadas virtuales por elemento:
`moveNext` + `current`):

- **Por elemento**: un `GenResume` + un `Yield` frente a dos despachos de interfaz — comparable o
  mejor, y sin campos de clase que el GC trace salvo el buffer de slots.
- **Por creación**: una asignación (`SurtrGenerator`) frente a una del iterador de la clase —
  igual; el buffer de `Slots` se reserva perezosamente al primer `yield`, así que un generador
  vacío nunca lo paga.
- **Pipelines**: donde hoy `Sequence.map(...).filter(...)` encadena N objetos iterador, un
  generador fusiona las etapas en un frame — la diferencia clásica que motivó los generadores en
  C#.

## 8. Alternativas evaluadas y descartadas

| Alternativa | Por qué no |
|---|---|
| Solo biblioteca (`sequence { }` builder estilo Kotlin) | necesita corrutinas internas, es decir generadores debajo; circular |
| Corrutinas completas (suspensión arbitraria) | frames en heap siempre (§4.C); contradice el presupuesto de frame del proyecto para un poder que la iteración no pide |
| Async/await integrado | no hay scheduler ni event loop en Surtr; ortogonal y muy posterior |
| Iteradores por reflexión/delegación de `Sequence` | no elimina el boilerplate por iterador nuevo, que es el problema |
| Makros para derivar iteradores | no hay sistema de macros; añadirlo sería más grande que los generadores |

## 9. Plan por fases

1. **Fase 1 — núcleo**: token `generator`, parseo, tipado de `yield`, restricciones de §5.2,
   `SurtrGenerator`, opcodes `GenNew`/`Yield`/`GenResume` (estrategia B), satisfacción de
   `IIterable<T>`, tests end-to-end de `for-in`, agotamiento, doble recorrido, capturas.
2. **Fase 2 — comodidad**: `yield*` delegación (azúcar sobre `for-in` interno), generadores en
   interfaces, reconsiderar `try/finally` con `yield` bajo semántica escrita.
3. **Fase 3 — rendimiento**: medir en `Surtr.Bench`; si la copia por `yield` aparece, máquina de
   estados selectiva (estrategia A) para los generadores calientes, transparente al usuario.

## 10. Preguntas abiertas para decisión

1. **Reinicio**: confirmar el modelo mixto propuesto («llamar de nuevo reinicia; el mismo objeto
   es de un solo uso») — es el de C#/JS combinados y el que mejor casa con `for-in`.
2. **Delegación `yield*`**: ¿fase 2 directa o se pospone hasta tener uso real?
3. **Nombre del tipo en superficie**: `generator<T>` propuesto; alternativa `sequence<T>` choca
   con la stdlib existente.

---

## 11. Evaluación del encadenado (`yield*` / delegación)

> Pedida antes de decidir si la delegación entra en la fase 1. Analiza qué es `yield*` bajo las
> decisiones ya tomadas, qué cuesta sobre la estrategia B, cómo interactúa con `Sequence<T>`, y
> propone un diseño concreto aunque se posponga.

### 11.1 Bajo nuestras decisiones, `yield*` es azúcar puro

Esto es lo primero que hay que fijar, porque cambia el peso de la decisión. En JS y Python
`yield*`/`yield from` **no** es azúcar sobre un bucle: reenvía `send()` y `throw()` al generador
interno, y la expresión *evalúa* al valor de retorno del interno. Las tres cosas son semántica
propia, imposible de escribir con un `for`.

Surtr no tiene ninguna de las tres — §2.6 descarta la inyección bidireccional, y §3.1 prohíbe
`return expr;` en un generador, así que no hay valor de retorno que propagar. Con eso:

```surtr
yield* other;              // equivale exactamente a:
for (x in other) { yield x; }
```

Son **semánticamente idénticos**, sin residuo. La consecuencia es doble y apunta en la misma
dirección: posponer `yield*` no cierra ninguna puerta (el desazucarado es toda su semántica, así
que añadirlo después es compatible por construcción), y añadirlo tampoco desbloquea nada que hoy
sea inexpresable. Es comodidad y rendimiento, no capacidad.

### 11.2 El coste sobre la estrategia B: la cadena se anida, no se fusiona

§7 promete que un generador «fusiona las etapas en un frame» frente a los N objetos iterador de
`Sequence.map(...).filter(...)`. Eso es cierto de un generador que hace el trabajo de las N etapas
en su propio cuerpo, y **falso de una cadena de delegación**: `yield*` anida, no fusiona.

Con el desazucarado ingenuo, en una cadena de N generadores donde cada uno delega en el siguiente,
cada elemento tiene que subir por los N niveles. Cada nivel paga, por elemento:

| Operación | Coste bajo estrategia B |
|---|---|
| Reanudar el nivel | copiar `LocalCount + profundidad de operandos` slots *hacia* la pila + montar frame |
| `yield` que lo atraviesa | copiar la misma anchura *fuera* + desmontar frame |

Es decir **2N copias de frame por elemento**, más 2N entradas/salidas de frame. La anchura real
salva bastante la cara: un generador que solo delega es diminuto (1–2 locales, 1–2 operandos), así
que cada nivel son 3–4 movimientos de 8 bytes. Una tubería de 5 etapas sale a ~30 movimientos de
slot y 10 montajes de frame por elemento.

Para calibrar, el patrón que hoy tiene la stdlib para lo mismo — `Sequence` con 5 iteradores
encadenados — paga por elemento **10 despachos de interfaz** (`moveNext` + `current` por etapa),
cada uno con su entrada de frame real. O sea que la cadena por copia sale *comparable*, quizá algo
mejor, pero no arrasa. El «gran salto» de los generadores está en escribir un cuerpo que haga las
cinco cosas, no en encadenar cinco generadores.

### 11.3 Lo que sí arrasa: el enlace de delegación

Existe una optimización que convierte la cadena de O(N) por elemento en O(1) amortizado, y es la
razón principal por la que `yield*` merece ser una construcción del lenguaje en vez de un bucle
escrito a mano. Es lo que hacen CPython (PEP 380) y V8.

Cuando un generador ejecuta `yield*` sobre **otro generador**, en vez de reanudar al interno desde
su propio cuerpo — lo que exige mantener vivo el frame del externo en cada ida y vuelta — el
externo:

1. copia su frame fuera **una sola vez**, como en cualquier `yield`;
2. graba `Delegate = interno` y queda suspendido *sin frame*;
3. a partir de ahí, `GenResume` sobre el externo **sigue la cadena de `Delegate`** hasta el
   generador más interno que no delega, y reanuda **solo a ese**.

Por elemento se paga entonces una reanudación y un `yield` del frame más interno, más un paseo de
punteros por la cadena — sin copiar un solo slot de los niveles intermedios, que no están
cambiando. La cadena solo se recorre de verdad cuando el interno se agota: se limpia el enlace y
se reanuda al padre, que continúa tras su `yield*`.

| Escenario | Sin enlace | Con enlace |
|---|---|---|
| Tubería estable de N etapas | 2N copias de frame por elemento | 2 copias + N saltos de puntero |
| Agotamiento de un nivel | — | O(1), se limpia un enlace |
| Recorrido de árbol, profundidad d | O(d) copias por elemento | O(1) amortizado (cada nivel se entra y sale una vez por subárbol) |

El coste de implementación es pequeño y está *todo* en el camino de reanudación: un campo
`Delegate` en `SurtrGenerator` y unas quince líneas en `GenResume`. Pero es precisamente el camino
que la fase 1 está estrenando.

**El caso que lo justifica** es el recorrido recursivo, que sin delegación no tiene forma decente:

```surtr
generator inorder(node: Node?): int {
    if (node == null) { return; }
    yield* inorder(node.left);
    yield node.value;
    yield* inorder(node.right);
}
```

Escrito con `for (x in inorder(node.left)) { yield x; }` funciona igual, pero cada elemento sube
por tantos frames como profundidad tenga el árbol.

### 11.4 Dos lowerings, no uno

El enlace de delegación **solo** sirve cuando el operando de `yield*` es estáticamente un
`generator<T>`. Si es un `IIterable<T>` cualquiera — un array, una `Sequence`, una clase de
usuario — no hay frame que enlazar y hay que caer al bucle. Así que `yield*` tiene dos
lowerings elegidos por el tipo estático del operando:

- **`generator<T>`** → `GenDelegate`: graba el enlace y suspende. Camino rápido.
- **cualquier `IIterable<T>`** → el `for (x in it) { yield x; }` literal. Camino general.

Es exactamente el mismo reparto que §4.2 ya hace en `for-in` entre el bucle indexado y el camino
por interfaz, así que no introduce un principio nuevo — pero sí es una segunda ruta que mantener,
y ese es el coste honesto de `yield*`.

### 11.5 Interacción con `Sequence<T>` — y la buena noticia

`Sequence<T>` guarda un **proveedor**, `() -> IIterator<T>`, no un iterador: por eso es
recorrible más de una vez. Una función generadora *es* un proveedor, porque llamarla otra vez
reinicia (§10.1). Y con la decisión de que `SurtrGenerator` implemente `IIterator<T>` además de
`IIterable<T>`, un generador **es** directamente un `IIterator<T>`. Las dos cosas juntas dan el
puente gratis:

```surtr
let s: Sequence<int> = Sequence<int>(() => digits(4021));
let total = s.map(x => x * 2).filter(x => x > 2).count();
```

Nada nuevo que declarar, ninguna API que añadir: **los generadores entran como *fuentes* de la
tubería existente desde el primer día, sin `yield*`**. Eso es lo que descarga a la delegación de
ser urgente.

Donde `yield*` sí pagaría dentro de la stdlib es en los dos iteradores que *son* delegación:
`FlatMapIterator` y `ChainIterator`. Reescribirlos como generadores con `yield*` es la ganancia
limpia; escritos con `for-in` anidado dan la misma semántica con un nivel más de frame por
elemento mientras dure la secuencia interna.

### 11.6 Seguridad: ciclos de delegación

`yield* self`, o cualquier ciclo en la cadena de enlaces, se cubre sin código extra con el estado
`Running` y su trap (§10.4): seguir la cadena hasta reanudar un generador que ya está corriendo
lanza `InvalidOperationException` en vez de copiar un frame sobre el que está vivo.

### 11.7 Recomendación

**Fase 2, con el diseño de §11.3–11.4 ya fijado.** Tres razones, en orden de peso:

1. **Bajo nuestra semántica es azúcar** (§11.1), así que posponerlo no cierra ninguna puerta ni
   deja nada inexpresable — al contrario que en JS/Python, donde `yield*` es semántica propia.
2. **Lo que lo hace valioso es el enlace de delegación** (§11.3), y ese enlace vive entero en el
   camino de reanudación que la fase 1 está estrenando. Diseñarlo contra una fase 1 funcionando y
   medida es sustancialmente mejor que diseñarlo a ciegas.
3. **Nada en el árbol lo necesita todavía**: la fase 1 no reescribe la stdlib (§10.2), y el puente
   con `Sequence<T>` (§11.5) ya da la composición perezosa sin delegación.

Lo que sí conviene hacer *en* la fase 1, y cuesta casi nada: dejar el hueco. Un campo `Delegate`
declarado en `SurtrGenerator` desde el principio, aunque siempre valga `null`, y el paseo de la
cadena escrito como un bucle trivial en `GenResume` — para que la fase 2 sea añadir un opcode y un
lowering, no reabrir el protocolo de reanudación.

---

## 12. Decisiones tomadas

> Cierra §10 y corrige los puntos donde el diseño propuesto no encajaba con el código. Lo que
> diga esta sección gana sobre §3, §5 y §6, que se escribieron antes.

### 12.1 Superficie

| Decisión | Resuelto |
|---|---|
| Introductora | **`generator`, no `generator fun`** — es una palabra clave introductora de miembro como `constructor` y `operator`, y sustituye a `fun` entera: `public generator digits(n: int): int { ... }` |
| Reserva | **`generator` y `yield` son reservadas duras** (§1.2). Cero colisiones en las fuentes `.surtr` del árbol, así que no rompe nada |
| Tipo en superficie | **`generator<T>`** |
| Cuerpo de flecha | **Rechazado** — una expresión no puede contener un `yield`, así que un generador exige cuerpo de bloque |
| Cuerpo sin ningún `yield` | **Legal** (generador vacío, útil como caso base) **con warning**, porque casi siempre es un olvido |
| Retorno `void` | **Error** — un generador declara su *elemento*, y `void` no es un tipo |

### 12.2 Reinicio: modelo mixto, y el caso silencioso se convierte en error

Confirmado el modelo de §3.2 — llamar de nuevo a la función crea un generador nuevo y reinicia; el
mismo objeto es de un solo uso — **con un añadido**: iterar un generador **ya iniciado** lanza
`InvalidOperationException` en vez de recorrer vacío. Recorrer en silencio un generador agotado es
un bug que no se manifiesta; el trap lo convierte en un error legible sin tocar el caso normal,
donde la expresión del `for-in` produce un generador recién creado.

El trap vive en los **dos** caminos: en `iterate()` para el camino por interfaz, y en el opcode
`GenIterate` para el camino rápido de §12.5, que no pasa por `iterate()`.

### 12.3 El descriptor lleva el elemento: `Y<elem>`

§3.2 proponía el símbolo desnudo `'Y'` por analogía con `closure`, y la analogía es falsa:
`closure` **no** borra sus parámetros (`L(II)F` los lleva completos) y `array` es `AI`. El único
símbolo desnudo es `R`, y porque un rango no tiene nada que parametrizar.

Así que el descriptor es **`Y<elem>`**: `YI` es `generator<int>`, `YOgame:Vec2;` es
`generator<Vec2>`. Mantiene el lookahead de un carácter, cuesta cero, y conserva el elemento para
diagnósticos, `ToDisplayString()` y re-chequeo cross-module.

### 12.4 Stub + cuerpo: el sitio de llamada es una llamada ordinaria

§6 hacía que el sitio de llamada emitiese `GenNew <token>`, lo que obliga a saber estáticamente que
el destino es un generador — un bit en metadatos, un bump de formato de imagen, y despacho virtual
imposible (un `override` podría no ser generador, y no hay `GenNewVirtual`).

En su lugar, `ModuleEmitter` emite **dos métodos por generador**, como C# con sus iteradores:

| | Nombre | Retorno | Cuerpo |
|---|---|---|---|
| **stub** | `digits` | `YI` | `GenNew $generator$digits$0` sobre sus argumentos, y `ReturnValue` |
| **cuerpo** | `$generator$digits$0` | `V` | el código real, con `Yield` |

Lo que compra:

- **El sitio de llamada es una llamada ordinaria**, sin opcode ni metadatos nuevos. El descriptor
  de retorno del stub (§12.3) ya dice `generator<int>`, así que cross-module no hace falta ningún
  bit: **no hay bump de `SurtrModuleImage.FormatVersion`**.
- **`virtual`/`override`/`abstract` sobre un generador salen gratis**, porque el stub se despacha
  como cualquier método — y los generadores en interfaces, que §9 aparcaba a fase 2, se vuelven
  casi triviales: una interfaz declara un método que devuelve `generator<T>`.
- `$generator$digits$0` encaja en el esquema `$categoría$contexto$índice` de `SyntheticNames`.

Cuesta una entrada extra en la tabla de métodos por generador y **un frame de más por creación**,
no por elemento.

### 12.5 Ejecución: dos caminos, como §4.2 con `for-in`

`SurtrGenerator` implementa **`IIterable<T>` e `IIterator<T>` a la vez**, y `iterate()` devuelve
`this` — con el objeto de un solo uso (§12.2) un cursor aparte sería una asignación por recorrido
sin ganar nada. Es además el modelo de JS/Python.

- **Camino rápido**: cuando el tipo estático de la secuencia de un `for-in` es `generator<T>`, el
  emisor baja a opcodes directos. El frame del generador se empuja en el mismo bucle `Run`, sin
  llamada nativa ni re-entrada en la VM.
- **Camino general**: cuando el generador viaja como `IIterable<T>`, el `moveNext` nativo reanuda
  re-entrando en la VM. Correcto y uniforme, y notablemente más caro por elemento.

Es el mismo reparto que §4.2 ya hace entre el bucle indexado y el camino por interfaz.

**Cinco opcodes nuevos desde `0xF6`**: `GenNew`, `GenIterate`, `GenResume`, `GenCurrent`, `Yield`.

### 12.6 Estados: cuatro, no tres, y como enum

§3.2 daba tres estados en un entero. Son cuatro y son un `enum SurtrGeneratorState`:

| Estado | Significado |
|---|---|
| `NotStarted` | creado, cuerpo sin arrancar |
| `Suspended` | parado en un `yield`, con su frame en `Slots` |
| `Running` | ejecutándose ahora mismo |
| `Exhausted` | terminado, por caída al final, por `return;` o por excepción |

`Running` es el que faltaba: sin él, un generador que se reanuda a sí mismo — directa o
indirectamente, y en fase 2 a través de un ciclo de delegación — copiaría su frame encima del que
está vivo. Reanudar uno que ya está `Running` lanza `InvalidOperationException`.

### 12.7 Alcance de la fase 1

**Dentro**: funciones de módulo, métodos de instancia y estáticos, genéricos, y **extensiones**
(§15) — el parser despacha miembros por keyword introductora en un único `switch` que las
extensiones reutilizan, así que salen casi gratis.

**Fuera**: constructores, operadores, accessors de propiedad, lambdas, interfaces, `const fun`, e
**`inline`/`forceinline`** — un cuerpo de generador no se puede splicear en el sitio de llamada, así
que la combinación es un error de compilación.

**Valores multi-slot**: un `yield` de una `value class` de varios campos o de una tupla **boxea**,
igual que hace el camino de interfaz. Una sola representación en fase 1; mantenerlo ancho en el
camino rápido queda como optimización medible.

**`yield` dentro de `try` sigue prohibido** (§5.4), con diagnóstico propio. `try` que no contenga
ningún `yield` es legal.

**La stdlib no se toca**: los doce `XxxIterator<T>` de `Sequence.surtr` quedan intactos. Mezclar la
sustitución con la introducción del mecanismo haría imposible saber a qué achacar una regresión.

**Sí entra un caso en `Surtr.Bench`** (generador vs iterador a mano vs bucle directo), para tener
número base desde el principio — y porque el acuerdo de checksum entre las tres implementaciones
del harness es una red de correctitud que los tests no dan.

### 12.8 El destino es el modelo completo, y es la fase 3

`yield*` llega en fase 2 con el enlace de delegación de §11.3. Pero el destino declarado **no** es
el `yield*` de §11.1 — azúcar bajo nuestra semántica reducida — sino el comportamiento completo de
JS/Python, que son generadores como **corrutinas**:

| Pieza | Qué arrastra |
|---|---|
| `gen.send(v)` | `yield` pasa a ser una **expresión**, con tipo: un segundo parámetro `generator<TYield, TSend>` (TS: `Generator<T, TReturn, TNext>`) o `unknown` con cast en cada `yield` |
| `return expr;` en generador, y `yield*` evaluando a él | un tercer parámetro `TReturn`, y `yield*` pasa de sentencia a expresión |
| `gen.throw(e)` / `close()` | reabrir §5.4 — `try/finally` cruzando un `yield` |

Y una pieza que **Surtr no tiene y los otros sí**: el registro de entidades barre soltando la
referencia, **sin hook de finalización** (`SurtrRuntimeEntity`, `SurtrDictionary`), así que un
generador abandonado a medias no correría nunca su `finally` por recolección — donde CPython lo
cierra por refcount y JS por el motor. La garantía de Python exige **cierre determinista**: que el
`for-in` emita un `close` al salir, `break` y excepción incluidos, que es lo que hace `foreach` de
C# con `IDisposable`. Surtr no tiene hoy ninguna historia de disposición, así que eso es un hueco
de lenguaje que la fase 3 tiene que resolver, no un detalle de implementación.

Por eso es fase 3 con plan propio, y por eso la fase 1 **no reserva sitio**: descriptor `Y<elem>`
limpio y `yield` atado como sentencia. Las imágenes se rechazan por versión y el árbol entero
recompila, así que crecer el descriptor o convertir `yield` en expresión más adelante cuesta una
recompilación, no una incompatibilidad — y encarecer hoy el 99% del uso (iteración simple) por el
1% futuro es mal negocio.

---

## 13. Fase 1: implementada

> Estado al cierre de la fase 1. Lo que sigue es lo que existe en el árbol, no lo que se planeó.

### 13.1 Qué se construyó

| Pieza | Dónde |
|---|---|
| Tipo de valor `generator` | `SurtrValueTypeCode.Generator` (insertado en la corrida de built-ins, entre `Closure` y `Range`) |
| Descriptor `Y<elem>` | `SurtrClassReference.SymbolGenerator`, factoría `Generator(elem)`, `GetGeneratorElementType()`, y las ramas de `SkipDescriptor`/`CodeOf`/`AppendDisplay`/`ContainsOpenParameter` |
| Objeto runtime | `Runtime/Objects/SurtrGenerator.cs` — `Slots`/`SlotCount`/`ResumeOffset`/`Current`/`State`/`Delegate`, y el `enum SurtrGeneratorState` de cuatro estados |
| Clase built-in | `SurtrBuiltIns.Generator` + `Runtime/BuiltIns/SurtrGeneratorBuiltIns.cs` (`iterate` devolviendo `this`, `moveNext`, `current`), satisfaciendo `IIterable<T>` e `IIterator<T>` |
| Opcodes | `GenNew` `0xF6`, `GenIterate` `0xF7`, `GenResume` `0xF8`, `GenCurrent` `0xF9`, `Yield` `0xFA` |
| Intérprete | los cinco cuerpos escritos en el bucle de despacho, más `SurtrCallFrame.Generator` y `SurtrVirtualMachine.ResumeGenerator` para el camino nativo |
| Emisor de bytecode | los cinco métodos de nivel 2 en `SurtrCodeEmitter.OpCodes.cs`, y el desensamblador |
| Léxico y sintaxis | `KeywordGenerator`/`KeywordYield` reservadas duras, `generator` como introductora de miembro, `YieldStatementSyntax`, `generator<T>` en posición de tipo |
| Binder | `MethodSymbol.IsGenerator`/`YieldType`, `BindGeneratorShape`, `BindYield`, `GeneratorTypeSymbol`, y las ramas de `Conversions`/`MemberLookup.BackingType`/`MetadataImporter`/`DescriptorEmitter` |
| Emisión | el split stub/cuerpo en `ModuleEmitter` (clase, módulo y extensión), `EmitGeneratorFactory` y `EmitYield` en `MethodBodyEmitter`, y el camino rápido `EmitForInGenerator` |
| Tests | `src/Surtr.Tests/VM/SurtrVirtualMachineGeneratorTests.cs` (8, a nivel de opcode) y `src/Surtr.Tests/Compiler/CodeGen/GeneratorEmissionTests.cs` (32, de fuente a ejecución) |
| Banco | los casos `genYield` y `handIterator` en `Surtr.Bench`, en Surtr, Lua y C# |

**No hizo falta el bit `IsGenerator` en metadatos.** El descriptor de retorno del stub ya dice
`YI`, y §12.4 hace que la llamada sea ordinaria, así que un módulo que importe otro no necesita
saber nada más. El único bump de formato (`SurtrModuleImage.FormatVersion` 8 → 9) es por la
renumeración de `SurtrValueTypeCode`, no por los generadores.

### 13.2 Lo que midió el banco

50.000 elementos, `-c Release`, mediana de 7 iteraciones:

| Caso | surtr ms | objetos | Qué es |
|---|---|---|---|
| `forIn` | 0.67 | 1 | bucle indexado sobre un array — el suelo |
| `genYield` | **1.42** | **1** | generador: suspender y reanudar un frame por elemento |
| `handIterator` | 1.60 | 1 | la clase cursor escrita a mano, llamada directamente |
| `iterator` | 4.76 | 50.000 | el camino general `iterate()`/`moveNext()` por interfaz |

Tres lecturas:

- **Un generador sale algo más rápido que el cursor escrito a mano** (1.42 frente a 1.60) y
  **3.4× más rápido que el camino por interfaz**, que es lo que §7 predijo: una copia de frame por
  elemento cuesta menos que dos despachos de interfaz.
- **Asigna un solo objeto**, independientemente de cuántos elementos produzca. El buffer de slots se
  reserva entero al crear el generador, así que ningún `yield` asigna — y frente a los 50.000
  objetos del camino por interfaz, esa columna es la diferencia que un presupuesto de frame nota.
- **Sigue costando ~2× un bucle indexado**, que es el precio honesto de la suspensión. Un `for-in`
  sobre un array no debe convertirse en un generador; lo que un generador sustituye es la clase.

Un defecto propio salió de esta medición: la primera versión del camino rápido copiaba de
`EmitForInIterable` su `BoxDynamic`, que allí normaliza un valor leído de una ranura borrada. En el
camino del generador los dos extremos son conocidos, así que boxear era una asignación por elemento
— 50.000 objetos y 1.9 MB. `UnboxDynamic` cubre los dos casos reales sin asignar nada, y es lo que
lleva la columna `objs` de 50.000 a 1.

### 13.3 Lo que la fase 1 no cierra

- **`yield*`**, con el diseño de §11 ya fijado. El campo `Delegate` y el paseo de la cadena en
  `GenResume` están escritos, aunque nunca iteren.
- **Generadores en interfaces**, que §12.4 abarata mucho: con el stub, una interfaz solo tendría que
  declarar un método que devuelva `generator<T>`.
- **`yield` dentro de `try`**, aparcado en §5.4 hasta que exista una semántica escrita de cierre.
- **Los doce iteradores de la stdlib**, intactos a propósito (§12.7).
- **Un `yield` de valor multi-slot boxea**, en los dos caminos. Mantenerlo ancho en el camino rápido
  es una optimización medible, no una corrección pendiente.
- **Las corrutinas completas de §12.8** — `send`/`throw`/`close`, `yield` como expresión, `return`
  con valor —, que son fase 3 y arrastran el hueco de cierre determinista que Surtr no tiene.
