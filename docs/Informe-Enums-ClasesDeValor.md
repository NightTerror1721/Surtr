# Informe: migrar los enums de Surtr de instancias estáticas a clases de valor

> **Estado:** investigación y propuesta (sin implementar). Responde al encargo: convertir el
> `enum` actual —una clase sellada con instancias estáticas con nombre (§2.4)— en una **clase de
> valor** cuyo campo `public let value: int` es el propio valor del enum, con valores explícitos
> por label escritos `CASO(args…) = n`, `equals`/`toString`/`hashCode`/`==`/`!=` sintetizados,
> operadores de bits para `@Flags`, un estático `values()` con todos los valores, conversores
> estáticos `of(value: int)` / `of(name: string)` (con `null` cuando no hay coincidencia),
> conformidad implícita con `IEquatable<T>` **e `IComparable<T>` en todos los enums** (con
> `compareTo` y `operator<=>` sintetizados),
> marcado `inline`/`@Pure`/`@NoAlloc` en los miembros que conviene, `forceinline` en el
> `operator<=>`, y **uso en tiempo de compilación garantizado** (`const`) para todos los miembros,
> constructores siempre
> privados, campos adicionales `let`, e interop de enums del host reconvertido a enteros puros sin
> cachés de referencias.

---

## Resumen ejecutivo

1. **La migración es viable con la maquinaria que ya existe.** §2.9 ya implementó todo lo que la
   representación necesita: borrado de value classes de un campo (`UnderlyingType`), bloques
   aplanados multi-campo (`IsValueType` + `ValueTypeLayout`), igualdad estructural emitida campo a
   campo (`EmitValueClassEquality`), boxeo cuando el tipo se desconoce (`BoxValue`/`UnboxValue`),
   y la elección de opcode por familia (`TypeCodeOf`). Un enum como value class de un campo
   `int` recorre exactamente ese camino.
2. **El cambio más rentable es el switch:** hoy un switch sobre un enum ordinario es una **cadena
   de comparaciones por referencia** (`JumpIfCompare Equal`, familia *Reference*), porque «un caso
   es una instancia y no hay miembro que dé el ordinal» (MethodBodyEmitter.cs:1087-1090). Con
   `value: int` plegable a constante en compilación, cada label es un literal entero y el switch
   entra gratis por la ruta `SwitchOn` existente (tabla densa o búsqueda binaria): **cero opcodes
   nuevos**, cero presupuesto de bytecode (restricción de `Informe-i64-y-f32.md` Parte C).
3. **El interop sale ganando de forma desproporcionada.** Hoy cada caso de un enum del host es un
   proxy `SurtrNativeObject` registrado como raíz GC, sellado en un campo estático en link time, más
   dos tablas de caché por runtime (`_nativeEnumCases` en el runtime,
   `SurtrEnumCache` en `SurtrInteropState`) que existen únicamente porque «los casos son
   referencias». Si el valor es un `int`, todo eso desaparece: marshalizar es `CreateInt(v)` /
   `Enum.ToObject(t, v)`, funciones puras sin estado por runtime, sin boxing CLR, sin raíces, y
   AOT-safe incluso en la ruta de reflexión.
4. **Riesgo principal — semántico, no mecánico:** la identidad desaparece. `Suit.Hearts ===
   Suit.Hearts` (hoy verdadero dentro de un módulo) pasa a estar **rechazado**, igual que sobre
   cualquier value class. Y hay una decisión de diseño con consecuencias de formato de imagen:
   si el descriptor del enum se borra a `I` (como hace hoy una value class de un campo), otro
   módulo ve `int` y se pierde exhaustividad, métodos y casos al cruzar frontera. **Recomendación:
   descriptor nominal siempre** (como las value classes multi-campo), conservando el borrado solo
   en la elección de opcodes.
5. **Esfuerzo estimado:** ~12 ficheros de producción + 8 ficheros de tests + 1 bump de versión de
   formato de imagen. Plan en 5 fases abajo.

---

## 1. Cómo funciona hoy (inventario por capa)

### 1.1 Sintaxis y parser

- `enum Suit : ICardSuit { Hearts("♥", true), ...; miembros }` — §2.4
  (`docs/Language-Syntax.md:368-459`). Cada entrada del case list es una **llamada al constructor
  del propio enum**; un caso sin argumentos llama al constructor implícito sin parámetros.
- El parser (`src/Surtr.Compiler/Syntax/Parser.Declarations.cs:545-585`, `ParseEnumCases`) lee
  `Nombre(args...)` y desambigua caso-vs-propiedad mirando un token hacia adelante (`(`, `,`, `;`,
  `}` = caso; `:` = propiedad). El `;` tras el case list es obligatorio solo si hay miembros.
- **No existe hoy la forma `Nombre = expr`.** El AST
  (`DeclarationSyntax.cs:477-495`, `EnumCaseSyntax`) solo lleva `Name`, `Arguments`, doc comment.
- `@Flags` (§11.1, único atributo que cambia *qué es* su objetivo) se declara en la línea de tipo;
  `SurtrBuiltIns.cs:324-488` lo publica como built-in.

### 1.2 Binding (fases de declaración y cuerpos)

| Paso | Dónde | Qué hace hoy |
|---|---|---|
| Caso → símbolo | `Binder.cs:2043-2061` | Cada caso se convierte en un `FieldSymbol` **estático, readonly, público, del tipo del propio enum**, más un `InitializerBinding` cuya expresión construye la instancia (es «un inicializador como cualquier otro»). |
| Marca flags | `Binder.cs:441` → `NamedTypeSymbol.IsFlagsEnum` (`Symbols/NamedTypeSymbol.cs:211`) | Se fija en fase de declaración desde la sintaxis escrita (antes de que el atributo bindee), porque la representación la necesitan todas las fases posteriores. |
| Reglas @Flags | `Binder.cs:2092-2132`, `CheckFlagsEnumIsPlain` | Prohíbe argumentos en casos, interfaces, cualquier miembro, y >31 casos («un int guarda 31 bits utilizables»). Diagnóstico `InvalidFlagsEnum`, error. |
| Valor de un caso | `BodyBinder.cs:210-250`, `BindEnumCase` | Enum normal → `BindObjectCreation` (instancia real). **@Flags → literal `1L << ordinal`**: «el propio case list es la notación; nada tiene que escribirse, y todo valor es potencia de dos por construcción». |
| Ordinal | `OrdinalOf` (`BodyBinder.cs:234-250`) | Se lee de la lista de miembros del enum, no se pasa, «para que coincida con el ordinal que toda otra parte del compilador lee». |
| Exhaustividad | `BodyBinder.Expressions.cs:4793-4842`, `CheckExhaustive` | Un *switch expression* sobre enum sin `else` debe listar todos los casos (por nombre, caminando `Members`). |
| Operadores flags | `BodyBinder.Expressions.cs:1132-1146` | `&`/`\|`/`^` entre valores del **mismo** enum @Flags devuelven ese enum; los shifts se rechazan. `contains(flag)` se sintetiza como `(value & flag) == flag` con temp para no evaluar dos veces (`TryBindFlagsContains`, línea 2347-2394). |
| Casts int↔enum | `Conversions.cs:540-560` | Explícitos en ambas direcciones, solo para @Flags. |

### 1.3 CodeGen

- **Tipo** (`ModuleEmitter.cs:308-382`, `DeclareType`):
  - Enum normal → `module.DefineEnum(...)` / `DefineNestedEnum(...)` (línea 339-348).
  - **@Flags → clase normal que contiene constantes int** (rama `default`, línea 333-338): «no es
    un enum en runtime; es una clase con constantes int, que es la verdad de su representación».
    Su descriptor es `I` (`DescriptorEmitter.cs:205-213`).
- **Campos** (`ModuleEmitter.cs:616-639`, `DeclareField`): el caso de un enum normal va por
  `@class.DefineEnumCase(nombre, visibilidad)` — es `SurtrClassBuilder` quien asigna el ordinal
  (`SurtrClassBuilder.cs:381-385`). Un caso @Flags cae al camino de static field ordinario.
- **Inicializadores** (`ModuleEmitter.cs:1271-1347`): los `InitializerBinding` de los casos se
  emiten en el **static initializer** del enum: cada instancia se construye en carga de módulo.
  Los flags, al ser literales, no construyen nada.
- **Familia de opcode** (`MethodBodyEmitter.cs:6050-6090`, `TypeCodeOf`): @Flags →
  `SurtrValueTypeCode.Integer` (línea 6083-6086); enum normal → `Object`.
- **Igualdad**: clases normales → identidad (`REQ`/`RNE`); value classes → walk estructural campo
  a campo (`EmitValueClassEquality`, `MethodBodyEmitter.cs:2831-2921`); `===` rechazado sobre
  valores (línea 2833-2834).

### 1.4 Switch (el punto que el encargo señala)

`EmitSwitchStatement` / `EmitSwitchExpression` comparten `EmitDispatch`
(`MethodBodyEmitter.cs:1075-1174`, `5671-5722`):

| Familia del subject | Codificación hoy |
|---|---|
| `int`/`char` con labels constantes | `SwitchOn` → tabla densa contigua u `OpCode.SwitchLookup` binaria (`SurtrCodeEmitter.Helpers.cs:585`, VM `OpCode.Switch`/`SwitchLookup`, `SurtrVirtualMachine.cs:3224/3249`). |
| `string` | hash estable (`StrHash` + `ComputeHash`) con confirmación por colisión. |
| **Todo lo demás, incluido el enum ordinario** | **cadena de comparaciones**: por cada label, cargar subject, cargar la instancia del caso, `JumpIfCompare Equal` por referencia. El comentario lo justifica: «un caso es una instancia singleton, y switchear por ordinal necesitaría un miembro que el enum no tiene» (líneas 1087-1090). |

Duplicados de clave: `TryCollectIntegerCases` (1184-1222) los detecta y abandona la tabla, cayendo
a la cadena, que respeta primer-match — el binder no rechaza duplicados.

### 1.5 Runtime

- `SurtrClass.IsEnum` + `EnumCases : ReadOnlySpan<SurtrEnumCaseInfo>`
  (`Runtime/Classes/SurtrClass.cs:337-357`); `SurtrEnumCaseInfo { Name, Ordinal, Field }`
  (`SurtrEnumCaseInfo.cs:25-59`) — struct porque «los casos son referencias, que una tabla no puede
  indexar»: el ordinal existe **solo** para que el switch exhaustivo pueda ser denso algún día.
- `AddEnumCase` (`SurtrClass.cs:523+`) asigna ordinal por orden de declaración y crea el campo
  estático respaldo.
- No existe `hashCode()`/`equals()` invocables desde fuente para clases de usuario; la igualdad
  runtime compartida es `SurtrValueComparer.ValuesEqual` (identidad para objetos normales).
  La evaluación de `Plan-ClaseBase-Equals-HashCode.md` recomienda **síntesis por el compilador**
  (Opción B: `operator==` → `equals` → síntesis; convenio FNV compartido con `SlotsHash`).

### 1.6 Formato de imagen

- `SurtrModuleImageWriter.cs:484` escribe `isEnum`; `524-540` escribe `enumCases =
  { name: str, visibility: u8 }[]` **sin el ordinal**: «escribir el ordinal y confiar en él dejaría
  que una imagen editada a mano renumerara un switch» — el lector repasa `AddEnumCase`
  (`SurtrModuleImageReader.cs:662-671`) y el ordinal se reasigna por orden de declaración. Los
  campos respaldo de casos se excluyen de la sección de fields (`566-588`).
- Versión 8 añadió `isValueType` junto a `isEnum` (`docs/Module-Format.md:126,309-315`) — el
  precedente exacto del flag que esta migración necesita ampliar.

### 1.7 Interop (host C# → Surtr)

Cadena completa hoy, para un `[SurtrNativeType] public enum LogLevel`:

1. **Source generator** (`SurtrInterop.SourceGenerator/SurtrSourceGenerator.cs:191-212`,
   `EmitEnumRegistration`): emite un `NativeTypeDescriptor { Kind = Enum, EnumCases = nombres[],
   EnumValues = boxed CLR[] }` y su `__SurtrRegister`.
2. **Materializer** (`SurtrTypeMaterializer.cs:96-120`, `RegisterEnum`):
   `runtime.DefineNativeEnum(fullName)` → por cada caso:
   `runtime.WrapNative(boxed)` (**proxy por valor**) + `runtime.AddRoot(proxy)` (**raíz GC**)
   + `DefineNativeEnumCase(clase, nombre, proxy)`; al final registra un
   `SurtrEnumCache(CLR type → entries)`.
3. **Runtime** (`SurtrRuntime.cs`): `_nativeEnumCases` (línea 54) retiene los proxies hasta
   `FinishNativeClass` → `SealNativeEnumCases` (1528-1551), que escribe cada proxy en el campo
   estático del caso ya enlazado (`*field.StaticAddress = CreateReference(...)`).
4. **Cachés de marshaling** (`SurtrInteropState.cs`): tabla por-runtime
   `ConditionalWeakTable<SurtrRuntime, …>` → `Dictionary<Type, SurtrEnumCache>` donde
   `SurtrEnumCache` mapea `long (valor CLR) → SurtrRef`. Existe porque «el CLR no cachea enums
   boxeados (la igualdad por referencia entre boxeos es inestable)».
5. **Marshaling** (`SurtrMarshaler.cs:46-55 y 89-93`, fallback reflexión;
   `SurtrEnums.cs`, fachada para shims generados): `ToSurtr` busca caché y devuelve la referencia
   del proxy; si no hay caché, degrada a `CreateInt` (¡incoherencia latente!);
   `ToClr<TEnum>` resuelve el proxy y devuelve su `Target`.
6. **Escáner de reflexión** (`SurtrReflectionScanner.cs:52-57`): `Enum.GetNames` +
   `Enum.GetValues().Cast<object>()`.

Problemas actuales de este diseño que la migración elimina de raíz:

- **Valores sin nombre registrados fallan**: un combo de bits host (`LogLevel.Warning |
  LogLevel.Error`) no está en `GetNames` → `GetReference` lanza «is not registered».
- **Coste por valor**: un objeto proxy + una raíz GC + dos entradas de diccionario por caso.
- **Estado por runtime innecesario**: un `SurtrRef` pertenece al heap de un runtime, de ahí el
  `ConditionalWeakTable` — pura consecuencia de representar el enum como referencia.
- **Dos rutas de código** (generador vs escáner) duplicando registro de casos.

### 1.8 Value classes: qué ya existe y se reutiliza

§2.9 (`docs/Language-Syntax.md:640-711`) implementado:

- Un campo → **borrado**: la clase ES el campo donde el tipo es conocido
  (`Binder.cs:2134-2168`, `BindValueClassField`; `DescriptorEmitter.cs:215-232`).
- Varios campos → bloque aplanado, `IsValueType = true` en metadata
  (`ModuleEmitter.cs:365-369`), layout en `ValueTypeLayout.cs` + linker
  (`SurtrTypeLinker.cs:513`), máx. 254 slots, genéricos prohibidos multi-campo, ciclos rechazados.
- Igualdad `==` estructural emitida; `===` rechazado; boxing a objeto real donde el slot solo sabe
  de referencias (`BoxValue`/`UnboxValue`, tests `SurtrVirtualMachineValueTypesTests.cs`).
- Interop inline structs ya se materializa como value class nativa
  (`SurtrTypeMaterializer.RegisterValueClass`, `DefineNativeValueClass`/`DefineValueField`).

---

## 2. Diseño propuesto

### 2.1 Forma en fuente

```surtr
enum Suit {
    Hearts("♥", true) = 1,
    Spades("♠", false),
    Diamonds("♦", true),
    Clubs("♣", false);

    private let _symbol: string;
    private let _isRed: bool;

    private constructor(symbol: string, isRed: bool) { ... }   // privado siempre

    fun describe(): string { ... }
}

for (suit in Suit.values()) { ... }        // estático sintetizado con todos los valores

let s = Suit.of(2);                        // Diamonds (null si ningún caso lo vale)
let t = Suit.of("hearts");                 // null: la búsqueda por nombre es exacta
```

> **Nota de gramática — valor explícito y argumentos.** El encargo fija la forma
> `CASO(arg1, arg2) = 1`: los argumentos del constructor pegados al nombre (donde les corresponde
> leerse) y el valor al final, separado por `=`. Es más descriptiva que la alternativa
> `CASO = 1(arg1, arg2)` — ahí el `1(...)` se lee como una llamada y el ojo no distingue qué es
> valor y qué son argumentos. Las cuatro formas admitidas quedan así:
>
> | Forma | Significado |
> |---|---|
> | `Hearts,` | sin args, sin valor → progresión |
> | `Hearts = 5,` | sin args, valor 5 |
> | `Hearts("♥", true),` | args, sin valor → progresión |
> | `Hearts("♥", true) = 5,` | args, valor 5 |
>
> El parser extiende `ParseEnumCases` en dos puntos encadenados: tras el identificador, la lista de
> argumentos opcional (como hoy); tras ella, un `=` + literal entero opcional (constante en tiempo
> de compilación; sin llamadas). La desambiguación caso-vs-propiedad no se toca: sigue mirando el
> token posterior al nombre (`(`, `,`, `;`, `}`, `=` → caso; `:` → propiedad), y una propiedad
> nunca va seguida de `=` literal en posición de miembro.

Reglas:

| Regla | Decisión |
|---|---|
| Campo implícito | Toda enum declara sintéticamente `public let value: int` como **primer** campo de instancia. Nombre reservado dentro del enum (redeclaración = error). |
| Valor implícito | Progresión habitual: `anterior + 1`, empezando en `0`. |
| Valor explícito | `Label(args…) = n` fija `value = n`; la progresión continúa desde ahí. Solo literales enteros ≥ 0 (negativos: error; el encargo no los contempla y romperían `~` y los masks). |
| Duplicados | **@Flags: permitidos** (precedente C#). Enums planos: **error** (`DuplicateEnumValue`) — ver §6.3. |
| @Flags valores | Todo valor explícito debe ser **potencia de dos o 0** (`InvalidFlagsEnum`); implícitos siguen siendo `1 << posición` entre los casos declarados. Con valores explícitos, el límite «>31 casos» deja de tener sentido (los bits pueden repetirse) y se sustituye por la comprobación por-caso. |
| Constructores | **Privados siempre.** `private` explícito aceptado; omisión = private implícito; cualquier otra visibilidad = error (`InvalidEnumConstructor`). |
| Campos extra | Libre declaración de propiedades/métodos/campos `let` pedidos por el constructor, como hoy §2.4. Todos `let` (readonly), coherente con §2.9. |
| Interfaces | Enums planos: las declaradas por el usuario siguen permitidas (hay receiver: el valor) **más** `IEquatable<Suit>` e `IComparable<Suit>` implícitas. @Flags: hoy prohibidas (`CheckFlagsEnumIsPlain`, `Binder.cs:2114-2118`) — la prohibición se sustituye por una lista cerrada: solo los contratos sintetizados `IEquatable<E>` e `IComparable<E>` (§2.3), iguales para ambos tipos de enum; miembros e interfaces declaradas por el usuario siguen vedadas en flags. Ver §6.8. |
| `value` en ctor | **Nunca** se pasa ni se acepta como parámetro: el compilador lo inserta al construir cada label. |

### 2.2 Representación

- **Un enum es un value class cuyo primer campo es `value: int`.**
  - Sin campos extra → caso de **un campo**: borrado a `int` en toda elección de opcode
    (`TypeCodeOf` → `Integer`, generalizando la línea 6083 de flags a todos los enums), slots de
    1, `==` es una comparación de int, el paso por registro/stack cuesta lo que un int.
  - Con campos extra → value class **multi-campo**: bloque aplanado `value + extras`, layout por
    `ValueTypeLayout`, boxeo idéntico al de `Vec2`.
- **Descriptor: nominal siempre** (ver §6.1): `C<Module:Name>;` como cualquier clase — nunca se
  borra a `I` en la imagen, para que otro módulo siga viendo el enum como enum (casos, métodos,
  exhaustividad). El borrado a int ocurre solo al elegir opcodes, que es una decisión del emisor
  local, no del wire format.
- **Casos**: siguen siendo statics readonly públicos del tipo del enum (el flow analysis y la
  reflexión cuentan con ello — test `AnEnumCaseIsAStaticLikeAnyOther`), pero su inicializador
  construye el **valor** (value class), no una instancia heap. Para enums de un campo el static
  puede quedar como const-fold: leer `Suit.Hearts` en un contexto tipado **no carga nada** — es el
  literal del valor. El static se conserva en metadata para dirección/reflexión/disassembler.
- `IsFlagsEnum` pierde sentido representacional (todos son ints) pero **se conserva como hecho de
  binding** para: validar potencias de dos, habilitar `| & ^ ~ contains`, y decidir si los casts
  int↔enum están disponibles.

### 2.3 Miembros sintetizados (encargo: equals/toString/hashCode/operadores)

Siguiendo la Opción B de `Plan-ClaseBase-Equals-HashCode.md` (síntesis del compilador, convenio
FNV único compartido con `SurtrValueComparer.SlotsHash`):

| Miembro | Cuerpo sintetizado | Notas |
|---|---|---|
| `fun equals(other: E): bool` | walk campo-a-campo que `EmitValueClassEquality` ya emite; para un campo: `this.value == other.value`. Null-safe: `other === null` → false (cortocircuito), tipo distinto → false. | Overridable escribiendo uno propio. **Es a la vez la implementación de `IEquatable<E>`** (§13.2: `equals(other: T): bool`). |
| `fun hashCode(): int` | un campo → `value` directamente (hash trivial, coincide con `HashOf` de un int). Multi-campo → FNV-1a sobre los hashes de campo (idéntico convenio que `SlotsHash` ⇒ iguales ⇒ mismo bucket). | Fase 2 del plan base. |
| `fun toString(): string` | **switch sintetizado sobre `value` que devuelve el nombre del caso** (tabla densa, coste ~O(log n)/O(1)); valor sin caso conocido → `"ModuleName.EnumName(value)"`. Con duplicados gana el primero en orden de declaración. | Es el único miembro que necesita el mapa inverso valor→nombre; haciéndolo cuerpo sintetizado viaja en la imagen y no requiere tablas en runtime/context. |
| `static fun values(): E[]` | array construido en el cuerpo con los casos en orden de declaración (`return [Hearts, Spades, Diamonds, Clubs];` — literales, cero tablas). | Ver decisión §6.7 (por qué array fresco y no iterable cacheado). Nombre reservado en enums (colisión = error, R9). |
| `static fun of(value: int): E?` | switch sintetizado sobre el argumento que devuelve el caso cuyo `value` coincide, o `null`. Tabla densa/esparza — la misma ruta §2.4. Duplicados (@Flags): primero en orden de declaración (mismo convenio que `toString`). **@Flags: total** — todo int es un valor representable del conjunto, así que nunca devuelve `null` (documentado; ver nota bajo la tabla). | El inverso de `.value`: round-trip de persistencia `E.of(x.value)`. Reserva del nombre incluida en R9. |
| `static fun of(name: string): E?` | búsqueda exacta por nombre (comparación ordinal, sensible a mayúsculas): cadena de comparaciones constantes o dispatch por hash — la maquinaria de `TryEmitStringDispatch` ya existe; `null` si ningún caso se llama así. Los nombres son únicos por construcción (el binder ya rechaza casos duplicados). | El inverso de `toString()` para nombres conocidos. Sobrecarga resuelta por tipos de parámetro (§3.5). |
| `==` / `!=` | Baja a comparación de slots (ya existe para value classes). Resultado observable **idéntico al de hoy** mientras no haya duplicados: casos distintos tenían instancias distintas; ahora valores distintos. `!=` incluido (negación). | |
| `===` / `!==` | **Rechazado sobre enums** (nuevo diagnóstico), como sobre value classes: un valor no tiene identidad. | Única ruptura real de semántica; ver §6.2. |
| `@Flags`: `\| & ^ ~` (+compuestos) | Ya resueltos: binding mismo-tipo (1132-1146) + `TypeCodeOf=Integer` los baja a opcodes int. `~x` = complemento del int. | Sin cambios salvo validaciones de valores. |
| `contains(flag)` | Igual que hoy: `(v & flag) == flag`. | |

**Contratos implícitos (encargo: IEquatable e IComparable en TODOS los enums).** El binder añade a
`DeclaredInterfaces` del enum los contratos sin que la fuente los escriba:

| Enum | Contratos satisfechos | Implementación |
|---|---|---|
| Todos | `IEquatable<E>` | el propio `equals` sintetizado; si el usuario escribe uno propio, ese gana y sigue satisfaciendo el contrato (misma firma que §13.2 exige). |
| Todos | `IComparable<E>` | `compareTo(other: E): int` sintetizado como comparación de `value` (`this.value <=> other.value`, opcodes int existentes), **más un `operator<=>` sintetizado** del que §5.6 deriva `<`, `<=`, `>`, `>=` gratuitamente. Coherencia garantizada: `a == b ⇔ compareTo(a,b) == 0` porque ambos comparan el mismo slot; con duplicados, dos labels de igual valor son «iguales» también para ordenar — mismo convenio C#, donde todo enum es `IComparable`. |

Semántica del orden en enums planos con valores explícitos: se compara por **valor**, no por
posición declarativa — con la progresión habitual ambas coinciden; con saltos explícitos
(`A = 1, B = 100`) el orden es el numérico, que es el único que `.value` y las tablas de switch ya
reconocen. Documentarlo en §2.4 al reescribirla.

Mecánica, con precedente directo en el lenguaje:

- Los contratos viajan como interfaces normales: la lista de interfaces declaradas ya se escribe y
  lee en la imagen (`SurtrModuleImageWriter.cs` sección interfaces), así que **no hay cambio de
  formato** por esta vía.
- La implementación se ata al slot del contrato por firma borrada — exactamente lo que §13.2
  documenta para `IComparable<Vec2>` (`compareTo(E)` borrado) y lo que el emisor ya resuelve con
  bridge cuando hace falta (`ModuleEmitter.cs:1404`: «slot keyed `compareTo(E)`»).
- Satisfacer un constraint genérico `<T : IEquatable<T>>` / `<T : IComparable<T>>` funciona sin
  trabajo extra: los primitivos ya satisfacen esos mismos constraints por vía runtime
  (tests `APrimitiveIntSatisfiesAnIComparableConstraint`, `APrimitiveIntSatisfiesAnIEquatableConstraint`
  en `ModuleEmitterTests.cs`), y un enum de un campo ES un int donde el tipo es conocido.
- Coste de boxing: fluir el enum a un slot `IEquatable<E>`/`IComparable<E>`/`T`-borrado lo boxea,
  igual que cualquier value class (§2.9) — es el tradeoff documentado, no una novedad de esta
  migración (R10).
- `CheckFlagsEnumIsPlain` pierde sus dos ramas de interfaces/miembros tal como están escritas hoy
  (`Binder.cs:2114-2131`): pasa a validar la lista cerrada de contratos permitidos y a rechazar
  cualquier miembro *declarado* — los sintetizados no pasan por esa lista.

> **Nota sobre `of(value)` en @Flags.** Que sea total (nunca `null`) no es una licencia: es la
> semántica del propio tipo. §2.4 ya establece que cualquier int es un valor válido de un enum
> marcado («`let none: Perm = 0 as Perm;`», combos sin caso declarado), así que `Perm.of(3)` debe
> devolver un `Perm` con 3 aunque 3 no sea label. En enums planos, en cambio, solo los valores
> asignados a labels existen y el `null` significa «no hay tal caso». La firma uniforme `E?`
> mantiene los dos mundos intercambiables genéricamente; quien necesite no-nullable en flags puede
> usar el cast `as`.

### 2.3bis Marcado `inline`, `@Pure` y `@NoAlloc` de los miembros sintetizados

Semántica aplicable: `inline` es hint con heurística de coste (`InlineCost.cs`, §3.6) — y para
cuerpos de una sola operación la heurística por defecto ya empalma sin marca alguna;
`@Pure` promete «mismo resultado para los mismos argumentos, sin efectos» (§11.1:3094);
`@NoAlloc` promete «el cuerpo no pone nada en el heap», con el analizador reportando lo visible —
creación de objetos, literales de colección, concatenación/interpolación de strings, lambdas,
`yield` (`AllocationInNoAllocBody`, §11.1:3116-3122). Ambos atributos son trusted, viajan en
metadata y un módulo importador los lee (`MetadataImporter.cs:774`).

| Miembro | `inline` | `@Pure` | `@NoAlloc` | Por qué |
|---|---|---|---|---|
| `equals(other)` | sí | sí | **sí** | walk corto; determinista, sin efectos; compara campos sin construir nada (comparar un campo string es `TextEquals`, sin alloc). |
| `hashCode()` | sí | sí | **sí** | trivial (int) o FNV aritmético. |
| `compareTo(other)` | sí | sí | **sí** | una comparación int — la heurística por defecto ya la empalmaría; la marca lo hace explícito. En todos los enums (§6.8). |
| `contains(flag)` (flags) | sí | sí | **sí** | secuencia `(v & flag) == flag` con temp; pura y sin alloc desde su diseño original. |
| `of(value: int)` | sí | sí | **sí** | tabla pequeña que devuelve valores ya existentes o null; cero construcciones. Converso de `.value` en rutas de deserialización, donde el coste de llamada importa. |
| `of(name: string)` | no | sí | **sí** | cuerpo mediano (dispatch por hash + confirmaciones): el hint saldría declinado casi siempre; puro igualmente; compara strings y devuelve casos/null, nada se aloca. |
| `toString()` | no | sí | no | determinista y sin efectos observables; pero el fallback `"Nombre(valor)"` interpola — el analizador de `@NoAlloc` lo reportaría con razón. |
| `values()` | **no** | **no, deliberadamente** | no | devuelve un array **fresco por llamada** (§6.7). El plegado/CSE hoy exige `native && pure` (`Binder.cs:3846`, consumido por `CrossStatementCse.cs:382` y el folder de `Binder.cs:3838`), así que hoy no habría riesgo — pero si esa puerta se abre a métodos fuente, marcar `values()` permitiría aliñar dos llamadas en **el mismo** array y romper la garantía de copia fresca. Se deja sin marca con comentario en la síntesis explicándolo. |

Reglas complementarias:

- **Los constructores sintetizados no se marcan**: §3.6 rechaza `inline`/`forceinline` sobre
  constructores (`InvalidModifier`) y el ctor de value class ya se empalma por su ruta dedicada.
- Los operadores se tratan aparte, abajo (§2.3ter) — casi ninguno es un método.
- Mecánica: los `MethodSymbol` sintetizados nacen con `IsInline` y sus usos de `@Pure`/`@NoAlloc`
  ya resueltos (mismo camino que atributos escritos en fuente), de modo que el emisor las emite
  como metadata ordinaria y un módulo importador las ve idénticas a las de un autor humano. Los
  cuerpos sintetizados deben pasar el analizador `AllocationInNoAllocBody` tal cual — es además un
  test gratuito de que la síntesis no aloja donde promete que no lo hace.

### 2.3ter Operadores: qué se marca y qué ni siquiera es una llamada

El encargo pide `forceinline` en los operadores sobrecargados, con `inline` como mínimo. La
investigación muestra que la mayoría de operadores de un enum **ni siquiera llegan a ser
llamadas**, que es mejor que cualquier inline; y el único que sí lo es, lleva `forceinline`:

| Operación | Qué es mecánicamente | Marca |
|---|---|---|
| `==` / `!=` | Comparación de slots emitida *in situ* (`Compare` sobre familia Integer para un campo; walk de `EmitValueClassEquality` multi-campo). **No existe método alguno que empalmar.** | ninguna (ya está «por debajo» del inline) |
| `\| & ^ ~` (+compuestos) en @Flags | Opcodes enteros directos desde §P14 (binding mismo-tipo en `BodyBinder.Expressions.cs:1132-1146` + `TypeCodeOf=Integer`). | ninguna (idem) |
| `contains(flag)` | Expansión en tiempo de *bind* (`BoundSequenceExpression`, `TryBindFlagsContains`): el cuerpo vive ya dentro del árbol del llamador. | ninguna (empalmado antes de emitir) |
| `< <= > >= <=>` (todos los enums, nuevo) | Para que los relacionales funcionen en fuente, se sintetiza un **`operator<=>` real** en cada enum (§5.6: declararlo da los cuatro gratuitamente). Este SÍ es un método y SÍ se llama desde cada `a < b`. | **`forceinline`** — cuerpo trivial (`value <=> value`), imposible que el empalme falle; si algún día fuera imposible, el error nominal de §3.6 es exactamente el aviso que se quiere |
| Cualquier operador futuro que acabe emitido como llamada | — | mínimo `inline`, según el encargo |

**Exención explícita para `==` (importante).** La regla de resolución del Plan-ClaseBase dice
«si declara `fun equals`, `==` baja a la llamada». Para enums hay que dejarlo escrito al revés:
**`==` nunca baja a `equals`** — se queda en comparación de slots/opcodes. Si no, la síntesis de
`equals` degradaría silenciosamente cada comparación de enum de un `Compare` a una llamada con
frame. La coherencia semántica entre ambos (iguales ⇔ mismos slots) es la garantía que hace
innecesaria la indirección.

### 2.3quater Enums en tiempo de compilación: evaluación `const` garantizada

Base factual (§7.1-7.3): un `const fun` se evalúa **ejecutando su bytecode real en la VM** con
presupuesto de instrucciones (§7.2:2513-2525), cuando todos los argumentos son constantes; en
posiciones que exigen constante el plegado es obligatorio. El cuerpo admite bucles,
condicionales, locales, **arrays/strings/dicts creados localmente** y llamadas a otros `const fun`;
prohíbe `native`, mutación no local, I/O, y `virtual`/`abstract`. `const` no implica ningún
sentido de `inline` (§7.2:2498-2504).

**Decisión del encargo: los enums deben poder usarse en tiempo de compilación SIEMPRE.** Eso
convierte los dos únicos bloqueos detectados en trabajo comprometido de esta migración, no en
condiciones externas:

1. **Fold de receptores constantes** — para `Suit.Hearts.describe()`-style: cuando el receptor es
   una constante (todo caso lo es por diseño, §2.3), el folder evalúa la llamada resolviendo el
   cuerpo por despacho directo (la clase es sealed ⇒ desvirtualizable; el fold nunca va por slot de
   interfaz). Es una ampliación acotada del `ConstFolder`: los métodos instancia entran por la
   misma puerta que los estáticos una vez el receptor está plegado.
2. **Fallback de `toString` sin nativo** — el bloqueo era que `"Nombre(valor)"` necesita int→string
   (`IntToString` es builtin nativo y los nativos están prohibidos en cuerpos const). Solución:
   sintetizar el fallback con un **bucle de dígitos puro** (`do { ... } while` sobre `/ 10`, `% 10`
   + `'0' as char`) dentro de un helper sintético compartido por módulo (`$intToString`, privado y
   `const fun`), que `toString` llama. Todo el cuerpo pasa a estar dentro del subconjunto permitido.
   Beneficio lateral: ese mismo helper sirve para cualquier `toString` sintetizado futuro
   (Plan-ClaseBase fase 3).

Con ambos habilitados, veredicto por miembro:

| Miembro | `const` | Justificación |
|---|---|---|
| Lectura de un caso (`Suit.Hearts`) | **ya la tiene por diseño** | Bajo el nuevo modelo todo caso baja a literal entero (`BindEnumCase` generalizado): una lectura de caso ES una expresión constante, utilizable en inicializadores `const`, condiciones `const if`, tamaños de array… |
| `.value` sobre un caso plegado | **sí** | Aritmética/literal sobre constante — la ruta binaria del folder ya la cubre. |
| `of(value: int)` | **sí** | Estático, cuerpo = condicional/tabla dentro del subconjunto permitido. Premio mayor: claves de dicts `const`, tablas generadas en compile-time indexadas por enums. |
| `of(name: string)` | **sí** | Ídem; la igualdad de strings la resuelve el mismo intérprete que ejecutará el programa (§7.2:2513-2518: «compile-time and runtime semantics cannot drift»). |
| `values()` | **sí**, con matiz | El cuerpo crea un array *localmente*, que §7.2 permite explícitamente. En una posición `const` el array se materializa **una vez** en carga (precedente exacto: `static let Sines = buildSineTable(256)`, §7.2:2483) — eso es opt-in posicional, no el aliñado automático que motivó vetar `@Pure`; las llamadas ordinarias siguen devolviendo copias frescas. Documentar que mutar el resultado de una posición `const` afecta a quien comparta ese binding. |
| `equals` / `hashCode` / `compareTo` / `contains` | **sí** (con habilitación 1) | Métodos de instancia: plegables desde que el receptor es constante y el despacho directo está garantizado — ambas cosas comprometidas arriba. Caso típico: `Suit.Hearts.equals(Suit.of(1))` pliega a `true`. |
| `toString()` | **sí** (con habilitación 2) | Con el fallback de dígitos puro no queda ningún nativo en el cuerpo. `const Nombre: string = Suit.Hearts.toString();` pliega al literal. |
| Operadores (`== != \| & ^ ~ < <= > >= <=>`) | no aplicable / ya pliegan | No son funciones; sus formas constantes ya se pliegan por las rutas binarias del folder (ints). `<` sobre enums pliega vía el `operator<=>` forceinline (empalme → comparación int → fold). |

Habilitación requerida fuera de la síntesis: **atributos con parámetros enum**. Hoy un argumento
de atributo debe plegarse y pasar `ConstantFitsField` (`Binder.cs:3500`, switch por
`SpecialType`), y un tipo enum no es `SpecialType.Int` → se rechazaría. Cambio pequeño pero
**obligatorio** bajo este encargo: aceptar una constante cuyo tipo es un enum almacenando su
`value` en la lista de constantes del atributo (`ModuleEmitter.cs:641-654`). Sin él, «enums
utilizables en compile time» tendría una excepción visible justo en el caso de uso más típico.

Documentación acompañante: Language-Syntax §2.4 (nueva) y §7.2 recogen el matiz de `values()` en
posiciones const y el orden por valor de los enums planos con valores explícitos.

### 2.4 Switch tables (adaptación pedida)

Con `BindEnumCase` generalizado — **todo** caso baja a literal entero (el valor calculado, sea
implícito, explícito o bit) — las labels de un switch sobre enum son constantes int en el árbol
ligado. Entonces:

- `TryCollectIntegerCases` (MethodBodyEmitter.cs:1184) las acepta sin cambios y el subject (int)
  entra por `Code.SwitchOn(cases, fallback)` → `OpCode.Switch` denso o `SwitchLookup`.
- La elisión del último brazo del switch expression exhaustivo (5692-5704) se mantiene tal cual.
- Duplicados de valor (@Flags): `TryCollectIntegerCases` ya devuelve false y cae a la cadena
  primer-match. Semántica preservada sin código nuevo.
- **Se elimina** la cadena por referencia para enums y con ella la justificación de existencia de
  `SurtrEnumCaseInfo.Ordinal` como soporte de tablas (pasa a ser metadato de reflexión).
- Coste: de O(n) comparaciones por referencia + resolución de campo estático a O(1) índice de
  tabla. Este es el beneficio medible principal en benchmarks de dispatch.

### 2.5 Imagen (bump de versión)

- Sección `Class`: los casos pasan a escribir `{ name: str, value: i32, visibility: u8 }[]`.
- El lector llama `AddEnumCase(name, value, visibility)`: **deja de inferir el valor del orden**;
  el ordinal (posición) sigue derivándose del orden para reflexión, pero el switch ya no depende
  de él (depende de `value`, escrito y verificable).
- Los campos respaldo de casos dejan de excluirse «porque AddEnumCase los crea»: ahora los casos
  tienen campos de instancia reales (value + extras) y sus statics son campos normales inicializados
  por el static initializer del enum, igual que cualquier static de value class. Simplificación
  neta: desaparecen `CountFieldsExcludingCases`/`IsEnumCaseField` (Writer 566-588).
- `isValueType` (v8) pasa a `true` para todo enum; se propone escribir también `isFlags: bool` junto
  a él (opcional, §6.4) para que flags-ness sobreviva fronteras — corrigiendo la carencia documentada
  en Language-Syntax.md:449-452.
- Los contratos implícitos (`IEquatable<E>`, `IComparable<E>`) y los miembros sintetizados viajan
  por las secciones ya existentes de interfaces y métodos — **sin formato nuevo** (§2.3).
- Versión de formato +1 con nota al estilo de la v8 (`docs/Module-Format.md:126`).

### 2.6 Runtime

- `SurtrEnumCaseInfo` gana `int Value` (y conserva `Ordinal` como posición declarativa).
- `AddEnumCase(SurtrFieldInfo)` → `AddEnumCase(string name, int value, SurtrVisibility v)`;
  el campo respaldo deja de crearse aquí (lo declara el flujo normal de fields).
- `DefineEnum`/`DefineEnumCase` de `SurtrModuleBuilder`/`SurtrClassBuilder` se replantean:
  `DefineEnum` crea un class builder con `isValueType=true, isEnum=true`; los casos se declaran como
  statics + entradas de tabla (nombre, valor).
- `SurtrRuntime._nativeEnumCases` + `SealNativeEnumCases` (54, 1539-1551): **eliminados** — ya no
  hay proxies que sellar; el static initializer del propio módulo/registro inicializa los statics.
- `DefineNativeEnum` (1426-1450) pasa a construir con `isValueType: true` y familia… decisión: la
  familia del class builder nativo sigue siendo `Native` (descriptor nominal), pero el linker lo
  achata a 1+N slots por sus fields `let`.
- `SurtrValueComparer.ValuesEqual`: los enums caen por la rama value-class/slots ya existente.

### 2.7 Interop completo (encargo: source generators + reflexión + maquinaria)

| Pieza | Hoy | Tras migración |
|---|---|---|
| Descriptor (`Descriptors.cs:61-68`) | `EnumCases: string[]`, `EnumValues: object[]` (boxed). | `EnumCases: NativeEnumCaseDescriptor[] { Name, long Value }` — valores tomados de `field.ConstantValue` en el generador (C# los garantiza) y de `Convert.ToInt64` en el escáner. `EnumValues` eliminado (o retenido solo para enums CLR de underlying ≠ int, §6.5). |
| Generador (`EmitEnumRegistration`) | Emite nombres + boxes. | Emite nombre + valor literal por caso. Detecta `[Flags]` del CLR (`type.GetAttributes()` → `FlagsAttribute`) y lo refleja en el descriptor (`IsFlags = true`) para que el materializador registre el enum como @Flags y `\| & ^` funcionen en Surtr. Validación host-side: si el generador ve valores no-potencia-de-2 en un `[Flags]`, warning (C# lo permite; Surtr lo tolera en interop, el check duro es solo para fuente Surtr). |
| Escáner reflexión (`SurtrReflectionScanner.cs:52-57`) | Nombres + boxed values. | `Enum.GetNames` + `Convert.ToInt64(Enum.GetObjectRaw?)` — mejor: `Enum.GetValues<Type under>` o `IConvertible.ToInt64`. Sin shims DynamicMethod: registrar un enum **no genera ningún entry point** → la ruta reflexión de enums pasa a ser AOT-safe (hoy no lo es por `SurtrReflectionInvoker`). |
| Materializer (`RegisterEnum`) | Proxy + root + DefineNativeEnumCase + cache. | `DefineNativeEnum` (value-class-backed) + tabla de casos (name,value) + `FinishNativeClass`. Sin `WrapNative`, sin `AddRoot`, sin `SurtrEnumCache`. |
| `SurtrInteropState` / `SurtrEnumCache` | Tablas por runtime ref→valor. | **Eliminados.** No queda estado: int↔CLR-enum es función pura. La razón de existir del ConditionalWeakTable (refs atadas al heap de un runtime) desaparece. |
| Marshaling (`SurtrMarshaler.cs`, `SurtrEnums.cs`) | Caché o fallback incoherente. | `ToSurtr: SurtrValue.CreateInt((int)value)`; `ToClr<TEnum>: (TEnum)Enum.ToObject(typeof(TEnum), v.AsInt)`. Los combos de bits sin nombre dejan de lanzar excepción — mejora directa. Los shims generados hacen aritmética inline sin llamada a fachada (la fachada se conserva para el path dinámico). |
| `SurtrBridge.RegisterAll` | Ordena enums primero (92-98). | Se conserva (los tipos deben existir antes que quien los nombra en firmas). |
| Reflexión Surtr (§13.5) | `KindName` → "enum" (SurtrReflectionBuiltIns.cs:342-351). | Igual; los casos aparecen como statics + la tabla `EnumCases` da nombre/valor. `MaterializeAttributes` intacto. |
| Disassembler (`AppendEnumCase`, línea 321-327) | `case X = <ordinal>` | `case X = <value>` (+ ordinal solo en modo verbose). |

**Encargo explícito cumplido**: «las tablas de caché se pueden declarar dentro de las propias
clases generadas por el source generator o en la reflexión en lugar de en el context o el
runtime» — de hecho el diseño llega más lejos: **no hacen falta tablas de caché en ningún sitio**,
porque no hay identidad que cachear. Lo único que persiste es la tabla declarativa (nombre→valor)
dentro del `SurtrClass` generado/materializado, que ya era metadata, no caché.

---

## 3. Impacto por proyecto

| Proyecto | Cambio | Tamaño |
|---|---|---|
| `Surtr.Compiler` (Syntax) | `EnumCaseSyntax.Value`, parser args-then-`=` (`CASO(args…) = n`) | pequeño |
| `Surtr.Compiler` (Binding) | `BindEnumCase` → literal siempre; validaciones (potencias 2, duplicados, ctor privado, `value`/`values`/`of` reservados); `CheckExhaustive` por valor; síntesis equals/hashCode/toString/values/of(int)/of(name) con marcado inline/@Pure/@NoAlloc/const (§2.3bis/§2.3quater); contratos implícitos `IEquatable<E>` + `IComparable<E>` **en todos** con su `operator<=>` forceinline y sustitución de las ramas de interfaces de `CheckFlagsEnumIsPlain` | medio-alto |
| `Surtr.Compiler` (CodeGen) | `DeclareType` (enums → rama value class con flag isEnum), `DeclareField` (casos = statics normales), `TypeCodeOf` (todos los enums → Integer), `DescriptorEmitter` (flags deja de borrar a I), `EmitDispatch` (borrar cadena de refs para enums), síntesis de cuerpos (array literal de `values()`, switch de `toString` y `of(value)` con marca, dispatch hash de `of(name)`), emisión de `inline`/`@Pure` en los sintetizados | medio |
| `Surtr.Core` (Bytecode/Image) | formato casos `{name,value,vis}`, version bump, quitar exclusión de fields | pequeño-medio |
| `Surtr.Core` (Runtime) | `SurtrEnumCaseInfo.Value`, `AddEnumCase`, eliminar `_nativeEnumCases`/`SealNativeEnumCases`, `DefineNativeEnum` value-class | medio |
| `Surtr.Interop` | Descriptors, Materializer, Scanner, Marshaler, fachada `SurtrEnums`, **borrar** `SurtrInteropState`/`SurtrEnumCache` | medio (mucho borrado) |
| `Surtr.Interop.SourceGenerator` | `EmitEnumRegistration` con valores + `[Flags]` | pequeño |
| `Surtr.LanguageServer` | nada estructural (símbolos fluyen); revisar hover/completion de casos | mínimo |
| `Surtr.Stdlib` | **sin enums en fuentes .surtr** (verificado) — sin impacto | nulo |
| Tests (8 ficheros) | ver §5 | medio |

---

## 4. Beneficios

1. **Switch O(1)** sobre cualquier enum (hoy O(n) compares por referencia) — el win de perf más
   visible, junto con la desaparición de la construcción de instancias por caso en carga.
2. **Interop sin allocations ni estado**: sin proxies, sin raíces GC, sin diccionarios por runtime,
   sin excepciones por valores no registrados, reflexión-AOT-safe para enums.
3. **Memoria/GC**: cero objetos por caso (hoy: 1 proxy heap + root por valor de enum host).
4. **Coherencia de lenguaje**: un enum plano y un `@Flags` pasan a compartir representación; la
   anomalía «flags no es un enum en runtime» (ModuleEmitter.cs:333-338) desaparece; `Plan-TiposDeValor`
   cierra su círculo.
5. **Superficie de mantenimiento**: `EmitValueClassEquality`, `ValueTypeLayout`, boxing y linker ya
   probados por los value types — el enum deja de tener rutas propias (case fields excluidos de
   imagen, ordinales inferidos, cadena especial de switch).

## 5. Riesgos y cambios rompentes

| # | Riesgo | Severidad | Mitigación |
|---|---|---|---|
| R1 | `===` sobre enums pasa de válido a error | media (rompe código que compare identidad) | diagnóstico claro con migración mecánica a `==`; buscar usos en corpus/tests antes de activar |
| R2 | Descriptor: si se borrara a `I`, otro módulo pierde enum-ness (exhaustividad, métodos, casos) | alta si se elige mal | **decisión cerrada: descriptor nominal** (§6.1); el encargo de value-class se cumple en representación, no en wire format |
| R3 | Valores explícitos + switch: dos casos con mismo valor en enum plano rompen name-lookup de `toString` y legibilidad de tablas | media | prohibir duplicados fuera de @Flags (§6.3) |
| R4 | `TwoEnumCasesAreDifferentInstances` y tests de identidad/ordinales dejan de aplicar | baja (tests, no producto) | actualizar expectativas (§7) |
| R5 | Enums CLR con underlying `long`/`byte` truncados a `int` | media-baja | validar en generador/escáner: underlying ≤ 32 bits sin signo problemático → error/warning; alternativa futura `value: i64` (fuera de alcance) |
| R6 | Version bump de imagen rompe imágenes existentes | baja (formato ya renumera opcodes con precedentes v3/v10) | bump + recompilar stdlib (`build-stdlib.ps1`) |
| R7 | `contains`/operadores sobre imports cruzados de imagen si `isFlags` no viaja | media | escribir `isFlags` en metadata (§6.4) o aceptar la erosión actual documentada |
| R8 | Síntesis `toString` exige mapa valor→nombre con duplicados | baja | primero-en-orden-declaración, documentado |
| R9 | Colisión de los nombres reservados `values` y `of` (y `equals`/`hashCode`/`compareTo`) con miembros que el enum declare | media-baja | `values`/`of` reservados en enums (error); los otros tres son overridables por diseño (Plan-ClaseBase ya define la colisión como override) |
| R10 | Boxeo al fluir un enum a un slot `IEquatable<E>`/`IComparable<E>`/constraint genérico | baja | tradeoff documentado de §2.9 para toda value class; el caso tipado directo no boxea; medir en bench si algún hot path cruza |
| R11 | Uso `const` total exige tres habilitaciones en el compilador: fold de receptores constantes con despacho directo, fallback de `toString` sin nativo y atributos que acepten constantes con tipo enum (`ConstantFitsField`, Binder.cs:3500) | media-baja | **Trabajo comprometido del plan**, no condición externa (§2.3quater): las tres son cambios acotados con diseño cerrado; sin ellas el encargo «usable en compile time siempre» no se cumple |

---

## 6. Decisiones abiertas (con recomendación)

### 6.1 ¿Descriptor nominal o borrado a `I`? → **Nominal (recomendado)**

Borrar a `I` (comportamiento value-class de un campo) haría que un módulo importado vía imagen viera
`Suit` como `int`: sin exhaustividad, sin `describe()`, sin casos, y `Color.Red` asignable a
`Suit` silenciosamente. Las flags ya pagan eso hoy y está documentado como carencia. Mantener el
descriptor nominal cuesta nada en ejecución (el linker achata por fields; `TypeCodeOf` decide
opcodes localmente) y preserva todo el sistema de tipos. Precedente: las value classes multi-campo
ya usan descriptor nominal (`DescriptorEmitter.cs:217-225`).

### 6.2 `===` sobre enums → **rechazar** (coherencia), con periodo de aviso

Es la única semántica que retrocede. Alternativa blanda: bajar `===` a `==` con warning durante una
versión. Recomendado: error directo — `===` ya está rechazado en value classes y el mensaje es el
mismo; mantener dos criterios de identidad según el tipo sería peor.

### 6.3 Duplicados de valor en enums planos → **prohibir**

El encargo permite duplicados explícitamente para `@Flags` (donde son idiomáticos, p. ej. alias de
bit). En enums planos romperían la inversión valor→nombre de `toString`, la exhaustividad por valor
y las tablas densas (dos keys iguales → caída a cadena). C# los permite y paga con `ToString` no
inyectivo; Surtr puede permitirse el check barato.

### 6.4 ¿`isFlags` en metadata? → **Sí** (1 byte junto a `isValueType`)

Corrige la erosión cross-module de flags (Language-Syntax.md:449-452), habilita validar
potencia-de-dos en importación y permite al LSP ofrecer `contains`/operadores sobre enums
importados. Coste: un byte y una línea de writer/reader.

### 6.5 Casts `int ↔ enum` para enums planos → **mantener restricción; el escape es `of(value)`**

Hoy solo @Flags castea. Con `.value` público readonly la lectura es libre, y el inverso que antes
se postergaba a un builtin de reflexión (`Enum.of(Suit, 2)`) ahora existe como miembro sintetizado
de primera clase: `Suit.of(2): Suit?` (§2.3) — con `null` cuando ningún caso lo vale, que es
justamente el check que un cast ciego no daría. La escritura de un raw `int` **como tipo** sigue
vetada para enums planos (el constructor es privado — precisamente lo que el encargo busca al
forzarlo). **No** abrir `as` generalizado: convertiría cada enum en un int con nombres y destruiría
el valor del tipo fuerte; `of` da lo mismo con un punto de control y un fallo explícito.

### 6.6 Tipo del campo → **`int` fijo** (no genérico, no i64 aún)

El encargo lo fija en `int`. Los enums CLR sub-32-bit se convierten; los `long` se rechazan con
diagnóstico (R5). Una extensión futura `enum : long` encajaría con el trabajo de i64 de
`Informe-i64-y-f32.md` sin reabrir este diseño.

### 6.7 `values()` → **array fresco por llamada** (`static fun values(): E[]`)

El encargo admite array o iterable («iterable si se quiere que sea inmutable»). Comparación:

| Opción | Pros | Contras |
|---|---|---|
| **A: `E[]` fresco por llamada** (recomendada) | cero maquinaria nueva; el cuerpo sintetizado son literales (`return [Hearts, Spades];`); inmutable *de facto* porque cada llamada entrega su copia y los elementos son valores inmutables; indexable y `for-in`-able (los arrays ya iteran) | una asignación de array por llamada (N slots, trivial) |
| B: `IIterable<E>` cacheado sobre los statics | cero allocación por llamada | el cursor es un objeto heap por `iterate()` igualmente; mutable si se cachea el array interno (alguien hace `values()[0] = …` sobre la única copia); más superficie (cursor, reset, genericidad borrada) |
| C: tipo readonly nuevo (`readonly[E]`) | inmutabilidad real | introduce un concepto de colección nuevo al lenguaje solo para esto — fuera de alcance |

Con A, quien quiera cachear escribe `let all = Suit.values();` en su módulo — decisión local y
visible, en vez de semántica de caché oculta en la síntesis. Precedente Java (`values()` devuelve
array fresco por el mismo motivo). Es también la razón por la que `values()` **no** lleva `@Pure`
(§2.3bis): la pureza habilita plegado/CSE, y aliñar dos llamadas rompería esta garantía.

### 6.8 Contratos implícitos → **`IEquatable<E>` e `IComparable<E>` en TODOS los enums** (encargo revisado)

El encargo inicial limitaba `IComparable` a los flags; la revisión lo extiende a todos, y es la
mejor opción por tres razones: (1) **precedente unánime** — C# y Java hacen que *todo* enum sea
`IComparable`; nadie espera que un tipo con valores discretos no se pueda ordenar; (2) **coincide
con el valor** — comparar por `value` es exactamente lo que las tablas de switch y `.value` ya
reconocen, así que no hay una segunda noción de orden que mantener; (3) **cuesta lo mismo** — el
`compareTo` y el `operator<=>` son un cuerpo trivial sintetizado igual para ambos tipos de enum;
la distinción flags/planos no compraba nada.

Semántica fijada (ver §2.3): el orden es **por valor**. Con la progresión implícita coincide con el
orden de declaración; con valores explícitos (`A = 1, B = 100`) manda el numérico. Duplicados:
iguales para `compareTo` (coherente con `equals`). Documentar en §2.4 al reescribirla.

Se mantiene sin cambios la regla de lista cerrada de §2.1: en @Flags los únicos contratos son los
dos sintetizados (miembros e interfaces declaradas por el usuario siguen vedadas); en enums planos,
estos dos se suman implícitamente a cualquiera que el autor declare además.

---

## 7. Plan de migración por fases

1. **Fase 1 — Representación (núcleo)**: parser (`= n`), binder (campo `value` sintético,
   validaciones, `BindEnumCase` → literal), CodeGen (enums por la ruta value-class, `TypeCodeOf`,
   descriptor nominal), image v+1, runtime (`SurtrEnumCaseInfo.Value`, `AddEnumCase`).
   *Criterio:* los tests de parser/binding/image en verde con la sintaxis nueva.
2. **Fase 2 — Switch**: eliminar la cadena de referencias para enums; verificar
   `Switch`/`SwitchLookup` con labels implícitas/explícitas/duplicadas(flags); microbench
   antes/después (`Surtr.Bench`).
3. **Fase 3 — Miembros sintetizados y contratos**: `equals`+`==` (walk existente), `hashCode`
   (convenio FNV), `toString` (switch sobre value + fallback de dígitos puro, §2.3quater),
   `values()` (array fresco, §6.7), `of(value: int)` / `of(name: string)` con su semántica de
   `null` y totalidad en flags (§2.3); marcado `inline` + `@Pure` + `@NoAlloc` según §2.3bis
   (`values()` sin marca, comentado), **`operator<=>` sintetizado en todos los enums con
   `forceinline`** (§6.8) y exención explícita `==`↛`equals`; **todo miembro marcado `const`**
   (§2.3quater), incluyendo las tres habilitaciones de R11 (fold de receptor constante,
   `$intToString` sintético, atributos con parámetros enum); `IEquatable<E>` e `IComparable<E>`
   implícitas en todos los enums (con `compareTo`) — incluida la reescritura de las ramas de
   interfaces de `CheckFlagsEnumIsPlain`; regla de resolución `operator==` → `equals` → síntesis
   del Plan-ClaseBase aplicada a enums primero.
4. **Fase 4 — Interop**: descriptors con valores, generador (`[Flags]`, constantes), materializer
   sin proxies, **borrado** de `SurtrInteropState`/`SurtrEnumCache`/`_nativeEnumCases`, marshaler
   aritmético, escáner AOT-safe.
5. **Fase 5 — Limpieza y docs**: quitar ramas muertas (flags-as-class en `DeclareType`,
   `CheckFlagsEnumIsPlain` parcial, exclusión de case-fields en imagen), actualizar
   `Language-Syntax.md` §2.4/§11.1 y `Module-Format.md`, `build-stdlib.ps1`.

Cada fase compila y pasa tests por separado; la 1-2 ya entregan el win de switch; la 4 es
independiente en gran medida del 3.

## 8. Inventario de tests afectados (detectados)

| Fichero | Qué cambia |
|---|---|
| `Compiler/Syntax/ParserTests.cs:112-124` | parseo con `CASO(args…) = n`; ambigüedad caso/propiedad con `=` tras args |
| `Compiler/Binding/BinderTests.cs` | validaciones nuevas (potencia 2, dup, ctor privado, `value`/`values` reservados) |
| `Compiler/Binding/BuiltInAttributeTests.cs` | @Flags con valores explícitos/no-potencia |
| `Compiler/Binding/FlowAnalysisTests.cs:357` | «un caso es un static como cualquier otro» — sigue cierto, revisar supuestos |
| `Compiler/CodeGen/ModuleEmitterTests.cs:1636` | `TwoEnumCasesAreDifferentInstances` → «different values / equal by ==» |
| `Compiler/CodeGen/ModuleEmitterTests.cs` (contratos) | todo enum satisface `<E : IEquatable<E>>` e `<I : IComparable<I>>`, y `a < b` funciona en fuente vía el `operator<=>` sintetizado (precedente: `APrimitiveIntSatisfiesAnIComparableConstraint`, línea 6219) |
| `Bytecode/Image/SurtrModuleImageTests.cs:262` | round-trip con valores explícitos; ordinal vs value; lista de interfaces implícitas |
| `Runtime/Classes/SurtrDeclarationModifierTests.cs:198-247` | `AddEnumCase` nueva firma; ordinals |
| `Interop/SurtrInteropTests.cs:145-152` | sin proxies: casos = (nombre, valor); marshaling de combos |
| `Interop/SurtrSourceGeneratorTests.cs` | snapshot del generado con `ConstantValue` y `[Flags]` |
| nuevos | switch-table con enum (denso/esparzo), toString sintetizado, hashCode/equals, === rechazado, interop AOT |
| nuevos (encargo) | `values()`: contenido, orden de declaración, copia fresca (mutar el resultado no afecta a la siguiente llamada), colisión con miembro propio; `IEquatable`: `equals` sintetizado y overridable; flags: `compareTo`/`<=>` por valor con duplicados (`equals ⇔ compareTo==0`) |
| nuevos (encargo) | `of(value)`: round-trip con `.value`, `null` en desconocidos, **totalidad en @Flags** (`Perm.of(3)` no es null), duplicados → primer caso; `of(name)`: exacto/sensible a mayúsculas, `null` en desconocidos, sobrecarga resuelta por tipos; marcado: `inline`/`@Pure` visibles en metadata importada (`IsPure` tras round-trip de imagen), y CSE **no** aliña dos `values()` |
| nuevos (encargo) | `@NoAlloc`: los cuerpos sintetizados marcados pasan el analizador sin `AllocationInNoAllocBody`; operadores: `<` sobre **cualquier** enum empalma su `operator<=>` (ninguna llamada en el bytecode resultante); `==` sobre enum emite `Compare`/walk de slots, **no** una llamada a `equals`; orden por valor: `Suit.Hearts < Suit.Spades` tras `Hearts = 1, Spades = 100`, y duplicados ⇒ `compareTo == 0`; `const` **siempre**: casos y `.value` plegables en `const`/`const if`, `of`×2/`values()` pliegan, llamadas instancia sobre receptor constante pliegan (`Suit.Hearts.equals(Suit.of(1))` ⇒ `true` en compilación), `toString()` pliega vía `$intToString` sin nativo, atributo con parámetro enum aceptado, y `const All = Suit.values()` materializa un array único mientras las llamadas ordinarias siguen siendo copias frescas |

## Anexo: evidencia principal (file:line)

- Sintaxis/§: `docs/Language-Syntax.md:368-459` (§2.4), `:640-711` (§2.9), flags-cross-module `:449-452`,
  `:3278-3302` (§13.2 contratos núcleo: `IIterable<T>`, `IComparable<T>`, `IEquatable<T>` y binding
  de implementaciones por firma borrada), `:1006-1065` (§3.6 inline/forceinline/noinline y
  heurística `InlineCost`), `:3094 y :3116-3122` (§11.1 `@Pure`, `@NoAlloc` y lo que su analizador
  reporta), `:2100-2118` (§5.6 tabla de operadores: `operator<=>` da `< <= > >=` gratis),
  `:2467-2530` (§7.2 `const fun`: evaluación por VM con presupuesto, subconjunto permitido,
  independencia de inline)
- Pureza, plegado y atributos en el compilador: `Binder.cs:3081, 3798, 3838-3846`;
  `BodyBinder.Expressions.cs:2278`; `CrossStatementCse.cs:382-394`; `BuiltInAttributes.cs:232`
  (`IsPure`); `MetadataImporter.cs:774` (`IsPure` viaja en metadata); `FlowAnalysis.cs:50, 86`;
  `Binder.cs:3500` (`ConstantFitsField` por SpecialType — habilitación R11);
  `ModuleEmitter.cs:641-654` (argumentos de atributos plegados en compilación)
- Parser: `Parser.Declarations.cs:545-585`; AST `DeclarationSyntax.cs:477-495`
- Binder: `Binder.cs:2043-2061, 2092-2132, 2134-2168`; `BodyBinder.cs:210-250`;
  `BodyBinder.Expressions.cs:1120-1146, 2347-2394, 4783-4842`; `NamedTypeSymbol.cs:211`;
  `Conversions.cs:540-560`
- CodeGen: `ModuleEmitter.cs:308-382, 616-639, 1271-1347`; `MethodBodyEmitter.cs:6050-6090,
  1075-1222, 2831-2921, 5671-5722`; `DescriptorEmitter.cs:205-232`
- Runtime: `SurtrClass.cs:337-357, 523+`; `SurtrEnumCaseInfo.cs`; `SurtrRuntime.cs:54, 1412-1488, 1528-1551`;
  `SurtrValueComparer.cs`
- Image: `SurtrModuleImageWriter.cs:484-588`; `SurtrModuleImageReader.cs:662-671`;
  `docs/Module-Format.md:126, 309-329`
- Interop: `SurtrSourceGenerator.cs:191-212`; `SurtrTypeMaterializer.cs:96-120`;
  `SurtrReflectionScanner.cs:52-57`; `SurtrInteropState.cs`; `SurtrEnums.cs`;
  `SurtrMarshaler.cs:46-55, 89-93, 118-119`; `SurtrBridge.cs:69-98`
- Value types ya existentes: `ValueTypeLayout.cs`; `SurtrTypeLinker.cs:513`; `SurtrBoxed.cs`;
  `SurtrVirtualMachine.cs:1837-1870, 3800`
- Evaluación previa relacionada: `docs/Plan-ClaseBase-Equals-HashCode.md` (síntesis Opción B),
  `docs/Plan-TiposDeValor.md`
