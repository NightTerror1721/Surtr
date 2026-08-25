# Plan-Varianza — Evaluación: varianza en los genéricos de Surtr

> **Estado: IMPLEMENTADO** (las 5 fases, FormatVersion 11). Lo que sigue era la evaluación
> previa; al final, en §7, queda el registro de cómo se implementó y las dos correcciones que
> la implementación hizo a este documento. Responde a la pregunta planteada: «evaluar la
> posibilidad de añadir varianza a los genéricos, ampliando sus constraints». Recoge el modelo
> actual, las dos variantes posibles (declaración y uso), qué costaría cada una sobre el borrado
> que ya tiene el lenguaje, con qué features existentes interactúa y una recomendación por fases.

## 1. El modelo hoy

Los genéricos de Surtr están **borrados al estilo Java**: se comprueban en compilación y se
descartan; en runtime solo existe una clase por declaración. Tres hechos del modelo condicionan
toda decisión de varianza:

1. **Todo parámetro de tipo es una referencia** (`TypeParameterSymbol.IsReferenceType`): un
   primitivo que entra a un `T` se boxea (§6.3). No hay representación especializada por
   instanciación, así que *no hay nada que la varianza pueda romper en memoria*.
2. **La comprobación de subtipado es puramente de compilación**
   (`Conversions.IsSubtype` + `WalkForBase` con `AsSeenFrom`, que ya sabe leer un tipo base
   «como lo hace ver» una construcción). Añadir varianza es enseñarle a ese paseo un caso nuevo;
   el runtime ni se entera.
3. **Invariancia total**: `Box<Cat>` no es `Box<Animal>`, `(Cat) -> Animal` no es
   `(Animal) -> int`, `array<Cat>` no es `array<Animal>`. Los comentarios del código lo asumen
   explícitamente («with generics invariant (§6)») y varios pases dependen de esa igualdad
   referencial entre construcciones internadas.

El dolor concreto que motiva la petición: las funciones y colecciones que *producen* valores no
pueden aceptar subtipos.

```surtr
interface IShape { fun area(): float; }
class Circle : IShape { ... }

fun drawAll(shapes: IIterable<IShape>): void { for (s in shapes) draw(s); }

let circles: IIterable<Circle> = ...;
drawAll(circles);   // ERROR hoy: IIterable<Circle> no es IIterable<IShape>
```

## 2. Las dos variantes posibles

### 2.1 Varianza en el punto de declaración — `out`/`in`

Se anota el parámetro en su declaración y el compilador comprueba que la clase la respeta:

```surtr
interface IIterable<out T> { fun iterate(): IIterator<T>; }
interface IIterator<out T> { fun moveNext(): bool; fun get_current(): T; }
interface IComparer<in T> { fun compare(a: T, b: T): int; }
```

- `out T` (**covariante**): `T` solo aparece en posiciones de salida — tipos de retorno,
  resultados de getters. `IIterable<Circle>` es entonces `IIterable<IShape>`.
- `in T` (**contravariante**): solo en posiciones de entrada — parámetros. Un
  `IComparer<IShape>` sirve donde se pide `IComparer<Circle>` (sabe comparar formas ⇒ sabe
  comparar círculos).
- Sin anotación: invariante, como hoy.

Es el modelo de C# y Kotlin: la anotación se paga una vez, en el dueño del tipo, y todos los
usos ganan. El compilador verifica las posiciones **una vez, en la declaración**, no en cada uso.

### 2.2 Varianza en el punto de uso — comodines estilo Java

```surtr
fun drawAll(shapes: IIterable<? extends IShape>): void   // hipotético
```

Java lo eligió para poder añadir genéricos sin romper un lenguaje ya publicado. Surtr no tiene
ese problema: puede elegir. Los comodines trasladan toda la complejidad a *cada uso*: captura de
comodines, imposibilidad de escribir el elemento en variables locales, inferencia que necesita
límites superiores e inferiores, mensajes de error ilegibles. Ningún lenguaje posterior a Java
con genéricos desde el día uno los ha copiado.

### 2.3 Veredicto parcial

**Declaración-site, nunca use-site.** Con genéricos desde el origen, los comodines son coste
permanente sin beneficio propio.

## 3. Qué costaría sobre este compilador

### 3.1 Subtipado variante — el paseo ya existe

`Conversions.WalkForBase` ya reconstruye cada interfaz/base «como la ve» la construcción hija
(`AsSeenFrom`). La varianza entra ahí como una regla más:

| Parámetro | ¿Cuándo casa `C<A>` con `C<B>`? |
|---|---|
| invariante `T` | solo si `A == B` (hoy) |
| covariante `out T` | si existe conversión implícita `A → B` |
| contravariante `in T` | si existe conversión implícita `B → A` |

Puntos finos que el código ya resuelve o casi:

- La sustitución `AsSeenFrom` compone: un `IIterator<T>` devuelto por `IIterable<out T>`
  hereda la varianza del contexto. La recursión de `WalkForBase` ya visita construcciones
  anidadas, así que el chequeo es local a cada arista.
- Nullability viaja aparte (`WithNullability`) y no interacciona.
- Los **tipos compuestos** (`T[]`, `{K: V}`, `(T1, T2)` y cierres `(A) -> R`) son símbolos
  estructurales, no clases: sus reglas se escriben directamente en `ClassifyImplicit`:
  - `(A) -> R` es subtipo de `(A') -> R'` si `A' → A` y `R → R'` (entrada contra, salida co).
    Es la pieza de mayor impacto diario: los tipos función son ubicuos en esta base de código
    (closures de primera clase, `Sequence.map(mapper: (T) -> U)`).
  - `(T1, T2)` tupla covariante en cada elemento.
  - `{K: V}`: clave invariante siempre (es mutable); valor según anotación si algún día existiera
    un dict declarado.
  - `T[]`: invariante mientras el array sea mutable. Java demostró que covarianza + mutabilidad =
    `ArrayStoreException` en runtime; Surtr **no tiene** comprobación dinámica de stores
    (el borrado boxea referencias y confía), así que la covarianza de arrays sería inseguridad
    silenciosa. Invariante, y documentarlo.

### 3.2 Verificación de posiciones — el trabajo nuevo de verdad

Para aceptar `<out T>`, el compilador debe probar que `T` jamás aparece en posición de entrada
dentro de la declaración. La noción es mecánica:

| Constructo | Posición de `T` |
|---|---|
| tipo de retorno de método/property-getter | salida |
| parámetro de método / setter value / `self` de extensión | entrada |
| campo de `value class` | **ambas** ⇒ fuerza invariante (un campo es legible y escribible) |
| argumento de tipo en `C<...>` anidado | como la varianza del parámetro correspondiente declare; invariante si el anidado es una clase invariante |
| constraint de otro parámetro `<U : T>` | posición de salida de `T` (produce promesas) |
| `T[]`, `{K: V}` | entrada+salida ⇒ contagia invariante |

Coste: un paseo nuevo sobre los miembros de la declaración (`CheckTypeParameterPositions`),
equivalente en forma al paseo de ciclos de constraints recién añadido (`CircularTypeParameterConstraint`),
más un error nuevo (`VariantParameterUsedAsInput`-estilo). Acotado: se ejecuta una vez por
declaración en `MemberPhase`, no por uso.

### 3.3 Metadatos cross-module

Una construcción importada de un `.surtrc` se re-verifica contra constraints importadas
(`MetadataImporter.ImportConstraints`). La varianza **tiene que viajar** en la imagen — un bit
por parámetro (`out`/`in`/invariante) junto a los `GenericParameters`/`GenericConstraints` que
`Plan-Genericos-Metadata.md` ya retiene — porque el importador debe poder responder la misma
pregunta de subtipo sin fuente. Formato: extensión de la tabla de parámetros genéricos ya
existente; bump menor de versión de imagen.

Sin esto, dos módulos compilados por versiones distintas del criterio podrían discrepar; con
esto, la varianza es tan local como cualquier otra regla de declaración.

### 3.4 Interacción con las features existentes

| Feature | Impacto |
|---|---|
| Borrado + boxing (§6.3) | ninguno: la varianza no cambia representaciones |
| Constraints `<T : C>` | composición natural: un `out T` con bound sigue prometiendo miembros; `MemberLookup.CollectReachable` ya camina bounds y no distingue |
| Inferencia de métodos genéricos | sin cambios: infiere argumentos concretos, no comodines (otro motivo para rechazar use-site) |
| Extension methods `extension<T> T[] { }` | el receptor `T[]` es invariante; nada cambia |
| Method-group → closure | gana: con varianza de cierres, `let f: (Circle) -> int = (a: Animal) => a.name();` pasaría a ser válido contravariante — un manejador de animales sirve donde se pide uno de perros, nunca al revés (corregido aquí: la versión original de este ejemplo tenía las dos partes invertidas) |
| Value classes genéricas | campos ⇒ invariantes de facto; la comprobación de posiciones lo declara sin caso especial |
| `===` identidad | sin interacción |
| Igualdad estructural de rangos/tuplas | tupla: covariante por elementos, coherente con su igualdad estructural |

### 3.5 Lo que NO hay que tocar

Runtime, VM, registry, GC, opcodes, descriptores de almacenamiento: cero cambios. La varianza
vive íntegramente en `IsSubtype`/`ClassifyImplicit`, en el verificador de posiciones y en el
importador de metadatos.

## 4. Riesgos

| Riesgo | Mitigación |
|---|---|
| Romper la igualdad referencial que algunos pases asumen («invariant ⇒ mismas construcciones internadas») | la internalización de construcciones no cambia; solo cambian las respuestas de *subtipo*. Auditar `SignatureSet` y `OverloadResolution`, que comparan tipos por referencia en claves de firma — una sobrecarga `f(IIterable<Circle>)` y otra `f(IIterable<IShape>)` siguen siendo overloads distintas aunque ahora una sea subtipo de la otra (la resolución ya ordena por especificidad) |
| Covarianza mal usada sobre tipos mutables (`IIList<out T>`) | imposible por construcción: quien declare `out` con `T` en posición de entrada obtiene error de declaración, no warning |
| Mensajes de error peores en fallos de subtipo profundo | el fallo ocurre en la arista exacta (`IIterable<Circle>` vs `IIterable<IShape>`); reportar la cadena de varianza aplicada |
| Presión para añadir comodines después | cerrado por diseño: los casos de uso de comodines quedan cubiertos por métodos genéricos con bounds (`fun sum<T : ...>(xs: IIterable<T>)`), el patrón Kotlin |

## 5. Qué ganarían la stdlib y el código real

| Tipo existente | Anotación natural | Ganancia inmediata |
|---|---|---|
| `IIterable<out T>`, `IIterator<out T>` | `out` | `for-in`/consumo polimórfico: el ejemplo de §1 pasa a compilar |
| `Sequence<out T>`* | `out` | pipelines covariantes (`Sequence<Circle>` usable como `Sequence<IShape>`); *requiere revisar `_provider` (posición mixta ⇒ quizá queda invariante y se gana vía `IIterable`) |
| Tipos cierre `(A) -> R` | co/contra integradas | el caso diario de callbacks y handlers |
| `(T1, T2)` tuplas | covariantes por elemento | asignación de tuplas heterogéneas a supertipos |
| `array<T>`, `dict<K,V>`, clases concretas | invariantes | sin cambio — correcto por mutabilidad |

## 6. Veredicto

**Viable, de riesgo bajo y de valor medio-alto; hacerla por fases cuando haya demanda real de la
stdlib.** La arquitectura de borrado la vuelve barata (todo es compilación), el paseo de bases
con `AsSeenFrom` ya está escrito y la verificación de posiciones es un paseo acotado con un error
nuevo. El 80 % del valor diario está en dos piezas: **varianza de tipos cierre** y
**`out T` en `IIterable`/`IIterator`**.

Orden sugerido si se aprueba:

1. Bits de varianza en metadatos + importador (sin semántica nueva aún; formato primero).
2. Verificador de posiciones + diagnóstico de declaración.
3. Reglas en `IsSubtype`/`ClassifyImplicit` para construcciones declaradas variantes.
4. Varianza built-in de cierres y tuplas (misma maquinaria, tipos estructurales).
5. Anotar la stdlib (`IIterable`, `IIiterator`) y auditar tests de resolución de overloads.

Fase 1–2 son infraestructura invisible; 3 es el interruptor; 4–5 son adopción. Nada de esto
bloquea ni es bloqueado por generadores ni por la clase base evaluada por separado.

## 7. Implementación (registro)

Las cinco fases están implementadas y la suite completa en verde (2806 tests). Decisiones que
quedaron cerradas al implementar, más dos correcciones a lo escrito arriba:

1. **Matching restringido (corrección a §3.1).** «Conversión implícita» era demasiado ancho:
   `int → float` es implícita en Surtr y, bajo borrado, no hay conversión por elemento que
   aplicarla — un `IIterable<int>` covariante hacia `IIterable<float>` sería corrupción
   silenciosa. El matching de argumentos usa solo identidad + jerarquía + ampliación nullable,
   excluyendo numéricos y `operator as`. (`IsVariantAssignable` en `Conversions.cs`.)
2. **El ejemplo de cierres de §3.4 estaba invertido** y está corregido arriba: la contravarianza
   dice que `(Animal) -> R` sirve como `(Dog) -> R`, no al revés.
3. **Metadatos:** byte por parámetro tras los nombres, tanto en clases como en interfaces;
   `FormatVersion 10 → 11`; el importador traduce a `TypeParameterSymbol.Variance`.
   Los built-ins nativos ganan una API gemela (`SurtrTypeInfo.SetGenericVariance`).
4. **Built-ins anotados:** `IIterable<out T>`, `IIIterator<out T>`, `IComparable<in T>`,
   `IEquatable<in T>` — declarados en C# (`DeclareCoreInterfaces`), no en `.surtr`; §5 asumía
   fuente Surtr. `Sequence<T>` queda invariante por su campo `_provider`, como anticipaba §5.
5. **Estructurales:** cierres contra/co, tuplas covariantes por elemento, `generator<T>`
   covariante (solo produce), arrays y dicts invariantes por mutabilidad.
6. **Verificador de posiciones:** paseo por miembros ya ligados en `MemberPhase`
   (`Binder.CheckTypeParameterPositions`), polaridad compuesta a través de construcciones
   anidadas, un diagnóstico por parámetro; códigos nuevos 3074–3076. `<out T>`/`<in T>` en
   métodos se rechaza con 3074; `out` es contextual y `in` ya era palabra clave del `for-in`,
   así que se reconoce como token propio.
