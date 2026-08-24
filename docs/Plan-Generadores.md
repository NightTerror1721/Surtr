# Plan-Generadores — Investigación: funciones generadoras (`generator`) para Surtr

> **Estado:** investigación (sin implementar). Recoge el estudio comparado de generadores en otros
> lenguajes, el diseño propuesto para Surtr — palabra clave `generator` en lugar de `fun`, nuevo
> tipo built-in hermano de `SurtrClosure` — y las tres estrategias de implementación sobre la VM
> propia, con recomendación y plan por fases.

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
