# Plan: constructores `each` y literales de colección tipados por destino

> Propuesta 3 (tipo explícito en el literal) + Propuesta 1 (target-typed) sobre un único
> mecanismo: un constructor normal que declara una fase de relleno `each`, bajado a
> `ObjNew` + constructor + una llamada por elemento. Sin constructores de copia.

## 1. Resumen

- Un constructor puede declarar una cláusula `each` como **bloque hermano** tras su `}`.
- `each (item: T)` = relleno de **un valor por elemento** → literal `[ ... ]`.
- `each (key: K, value: V)` = relleno de **par clave/valor por entrada** → literal `{ ... }`.
- El relleno se compila como un método de instancia privado `$fill$...` (convención `$` de
  `SyntheticNames`, inalcanzable por identificador).
- Un literal tipado se baja a `ObjNew` + llamada al constructor + una llamada a `$fill$...`
  por elemento. **Nunca** materializa un array intermedio ni usa un constructor de copia.
- El mapping de defaults se declara **en la interfaz** (`interface IList<T> default List<T> : ...`),
  reusando el keyword existente `default` (el mismo del brazo de un `switch`). Sin `for` en los
  constructores, sin registro de compilación, sin unicidad que vigilar: una interfaz tiene una
  sola declaración → un solo default.
- La desambiguación entre literal y acceso por índice es **semántica** (en bind, según a qué
  resuelva el callee), nunca sintáctica por conteo de comas.

## 2. Sintaxis

### 2.1 Declaración del constructor `each`

```surtr
public constructor(initialCapacity: int = DefaultCapacity)
{
    // fase única — corre una vez
    if (initialCapacity < 0) throw IndexOutOfRangeException("Initial capacity must be non-negative");
    _capacity = initialCapacity;
    _items = array<T>(_capacity);
    _length = 0;
}
each (item: T)
{
    _items[_length] = item;
    _length++;
}
```

- El bloque `each ( params ) { body }` sigue a la `}` de un constructor **con cuerpo**.
- El bloque es **hermano del constructor**: ve `this` y sus propios parámetros; **no** ve
  parámetros ni locales del constructor (su ejecución terminó antes del relleno).
- El orden de evaluación del literal se preserva: los fills corren en orden de escritura.

### 2.2 El default en la interfaz

```surtr
public interface IList<T> default List<T> : IReadOnlyList<T>, ICollection<T>
{
    fun get(index: int): T;
    fun set(index: int, value: T): void;
}

public class List<T> : IList<T>
{
    public constructor() each (item: T)
    {
        _items[_length] = item;
        _length++;
    }
}
```

- El default se declara **en la interfaz**, entre el nombre y los `:`, reusando el keyword
  existente `default` (§4.3, el brazo de un `switch`). Una interfaz tiene **un solo default**
  por construcción — imposible duplicarlo.
- `each` en el constructor ya solo significa "**soy construible por literal**" (sin `for`).
- El default debe ser una **clase** que **implementa la interfaz** (check en el punto de
  declaración).
- `IIterable<T>` (built-in) **siempre enlaza a `T[]`**: no declara default, y el literal sobre
  ella construye un array (status quo).

### 2.3 Call-site (literal tipado)

```surtr
// Tipo explícito (Propuesta 3)
let a = List<int>[1, 2, 3];            // List<int>() + $fill$(1) $fill$(2) $fill$(3)
let b = List<int>(32)[1, 2, 3];        // List<int>(32) + $fill$×3   (args del constructor)
let m = Map<string, int>{ "x": 10 };   // 2 parámetros → forma {}
let n = Map<string, int>(16){ "x": 10, "y": 15 };
let i = IntList[1, 2, 3];              // tipo NO genérico, forma sin paréntesis (válida para cualquier tipo)

// Target-typed (Propuesta 1)
let c: List<int> = [1, 2, 3];          // el destino ES el builder
let d: IList<int> = [1, 2, 3];         // interfaz → su default declarado en la interfaz
let e: List<int> = [];                 // List<int>() vacío, cero fills
let f: List<float> = [1, 2];           // int → float por elemento (conversión implícita)
```

### 2.4 Desambiguación

| Código | El identificador/callee resuelve a | Significado |
|---|---|---|
| `List<int>[1, 2, 3]` | **tipo** (nombre genérico) | builder |
| `IntList[5]` | **tipo** (identificador simple) | builder (aunque el cuerpo tenga un solo elemento) |
| `List<int>(32)[5]` | **tipo** (call) | builder |
| `Foo(5)[0]` | tipo **sin** `each` | error |
| `arr[0]` | **valor** (variable/parámetro/campo) | índice (re-encuadre en bind) |
| `Singleton[5]` | **singleton** (§2.8: un tipo que es valor) | índice sobre la instancia |
| `makeList<int>(5)[0]` | **función** | índice |
| `(List<int>(32))[0]` | paréntesis = valor | índice (escape explícito) |
| `obj.field[0]` | acceso a miembro | índice (camino actual, intacto) |

Regla: un `[ ... ]` tras un **identificador, nombre genérico o llamada** se parsea SIEMPRE
como cuerpo de literal (lista con comas) y el binder decide. El identificador resuelve a un
**tipo** → builder (el operador `[]` solo existe sobre instancias, nunca sobre un tipo); a un
**valor** → índice, solo si el cuerpo tiene un único elemento. Indexar una construcción recién
hecha exige `(Tipo(args))[i]`. El acceso a miembro (`obj.campo[0]`) conserva el camino de
índice actual y no se re-encuadra.

## 3. Reglas semánticas

### 3.1 Aridad del `each`

- Exactamente **1** parámetro (forma `[ ]`) o **2** (forma `{ }`). Exclusivos por constructor.
- Parámetros posicionales, **con tipo obligatorio** (un parámetro siempre lleva tipo, §5.9),
  sin defaults, sin varargs, sin nombre.
- Un tipo puede exponer ambas formas con **dos constructores** distintos (`each (item: T)` y
  `each (key: K, value: V)`).

### 3.2 Overloads y firma

- `constructor(...)` y `constructor(...) each (item: T)` son **overloads distintos** aunque el
  resto de la firma sea idéntica. `SignatureSet` incluye la cláusula `each` (presencia + tipos
  borrados de sus parámetros) en la clave de firma de los constructores.

### 3.3 Llamadas normales vs literales

- **Literal** → candidatos = SOLO constructores `each` de la aridad que cuadra. Ninguno →
  error de aridad.
- **Llamada normal** → candidatos = SOLO constructores **sin** `each`. Los `each` **nunca**
  son alcanzables por una llamada normal; si el tipo solo declara constructores `each`, una
  llamada normal es error. (`constructor(n)` y `constructor(n) each` coexisten sin ambigüedad:
  la llamada normal solo ve el primero, el literal solo el segundo.)

### 3.4 Alcance del bloque `each`

Solo `this` + sus propios parámetros. No accede a parámetros/locales del constructor; si el
relleno necesita un dato de la fase única, esta lo guarda en un campo. Solo válido en
constructores de **clases ordinarias** (no `value class`, no `singleton`, no `interface`).

### 3.5 El default en la interfaz

- `interface I<T> default C<T> : ...` — el default es la clase concreta que un literal
  target-typed construye cuando el tipo objetivo es `I<T>`.
- **Checks en el punto de declaración** (MemberPhase):
  1. El default es una clase (`InterfaceDefaultNotClass`).
  2. La implementa (con la sustitución de la interfaz, p.ej. `List<T>` implementa `IList<T>`)
     (`InterfaceDefaultNotImplemented`).
  3. El número de argumentos de tipo coincide con los parámetros de la interfaz
     (`InterfaceDefaultArity`).
  4. La clase declara **al menos un constructor `each`** (`InterfaceDefaultNoEach`); sin él, el
     default no puede rellenar ningún literal y la cláusula es inútil.
- **Un solo default por interfaz**: una interfaz tiene una sola declaración → un solo default;
  es imposible declarar dos.
- **Sin guard de built-in**: las built-in (`IIterable`, `IIterator`, `IDisposable`,
  `IComparable`, `IEquatable`) no tienen cláusula `default` — su declaración no existe en
  fuente. `IIterable<T>` **siempre enlaza a `T[]`**.
- **Visibilidad entre módulos**: el default puede nombrar una clase de otro módulo **si ese
  módulo está importado** (o el nombre va totalmente calificado); ambos casos alimentan el
  `ModuleDependencyGraph`. Un ciclo (`Collection.surtr` ↔ `List.surtr`) es error
  `ModuleCycle` — no hay orden de carga válido para los inicializadores estáticos.
  Consecuencia en la stdlib: `IReadOnlyCollection`/`ICollection` (en `Collection.surtr`, base
  de todo) no pueden nombrar `List`; esos dos quedan sin default y su literal target-typed es
  error ("escribe el tipo concreto").

### 3.6 Tipos objetivo del literal target-typed

En `BindArrayLiteral`/`BindDictLiteral`, con `expected` no nulo:

1. `expected` es `ArrayTypeSymbol` / `DictionaryTypeSymbol` → camino actual.
2. `expected` es `IIterable<T>` → **siempre `T[]`** (status quo; nunca se consulta un default).
3. `expected` es un `NamedTypeSymbol` **concreto** con `each` de aridad que cuadra → builder.
4. `expected` es una **interfaz** con default declarado (cláusula `default` en la interfaz) →
   resolver el default (debe tener `each` que cuadre) → builder; sin default → error apuntando
   a la forma explícita.
5. Cualquier otro `NamedTypeSymbol` (`object`, tipos sin `each`, interfaces sin default) →
   **fall-through** al camino actual (inferir array/dict + `Convert`). Sin regresiones sobre
   `let o: object = [1, 2, 3]` ni `let x: IIterable<int> = [1, 2, 3]` (que ya cae en la regla 2).

## 4. Capas técnicas

### 4.1 Lexer

`each` → **keyword contextual** (precedente: `yield`, `Lexer.cs:861`), solo reconocida tras la
`}` de un constructor y en el call-site tras el tipo. Para el default de la interfaz no se toca
el lexer: `default` ya es keyword (`TokenType.KeywordDefault`, §4.3).

### 4.2 Parser

`Parser.Declarations.cs` — `ParseConstructor` (:795): tras el cuerpo, si `CheckContextual("each")`:
`reader.Advance()`, `ParseParameterList()`, `ParseBlock()`. Adjuntar a `ConstructorDeclarationSyntax`.

En la **cabecera de interfaz**: tras el identificador y los parámetros de tipo, si el token es
`KeywordDefault` (ya existente, §4.3), parsear un `TypeSyntax` como default; luego el `:` de
las interfaces extendidas.

`Parser.Expressions.cs` — `ParsePostfix` (:368):

- Tras un **identificador**, **nombre genérico** (`Name<...>`) o **call** (`...(...)`), si el
  siguiente es `[`, parsear la **lista con comas** como cuerpo de array (reutilizar la lógica
  de `ParseArrayLiteral`); si es `{`, parsear cuerpo de dict. Producir
  `CollectionInstantiationExpressionSyntax` con `Construction` = lo precedente. El binder
  decide entre builder e índice (un identificador que resuelve a un valor re-encuadra a índice
  si el cuerpo tiene un solo elemento).
- Rama genérica (:404-428): si tras `ParseTypeArgumentList()` el siguiente es `[` o `{`, **no**
  parsear argumentos de llamada; parsear el cuerpo (ídem). Si es `(`, camino actual de llamada.
- **No** se re-encuadra el acceso a miembro (`obj.campo[0]` conserva el camino de índice) ni un
  `[` tras paréntesis (`(expr)[i]`).
- El binder ya resuelve cada identificador; el re-encuadre a índice añade solo una comprobación
  de forma (cuerpo con un único elemento), no una segunda resolución.

### 4.3 AST

```csharp
// DeclarationSyntax.cs
public sealed class ConstructorDeclarationSyntax : DeclarationSyntax
{
    public IReadOnlyList<ParameterSyntax>? EachParameters;   // null = no builder
    public BlockStatementSyntax?          EachBody;
}

public sealed class InterfaceDeclarationSyntax : DeclarationSyntax
{
    public TypeSyntax? DefaultBuilder;                        // null = sin default
}

// ExpressionSyntax.cs
public sealed class CollectionInstantiationExpressionSyntax : ExpressionSyntax
{
    public ExpressionSyntax Construction { get; }  // IdentifierExpressionSyntax | GenericNameExpressionSyntax | CallExpressionSyntax
    public ExpressionSyntax Body { get; }          // ArrayLiteralExpressionSyntax | DictLiteralExpressionSyntax
}
```

### 4.4 Símbolos

```csharp
// MemberSymbols.cs
public IReadOnlyList<ParameterSymbol>? EachParameters { get; internal set; }
public MethodSymbol?                   FillMethod { get; internal set; }
public bool  IsCollectionBuilder => EachParameters is not null;
public int   EachArity          => EachParameters?.Count ?? 0;

// NamedTypeSymbol.cs — la interfaz
public NamedTypeSymbol? DefaultBuilder { get; internal set; }   // null = sin default

// SyntheticNames.cs
public const string FillCategory = "fill";
public static string FillMethod(string typeName, int index) => Build(FillCategory, typeName, index);
// → "$fill$List$0", "$fill$List$1" — imposible de escribir como identificador; el compilador lo
// alcanza por símbolo. Viaja en la imagen, igual que "$generator$...".
```

El `$fill$...` es un `MethodSymbol` de instancia, `Role = Normal`, `Accessibility.Private`,
`MethodDispatch.Direct`, parámetros = los del `each`, retorno `void`, cuerpo = el bloque `each`.

### 4.5 SignatureSet

Extender la `Signature` de los constructores con la cláusula `each` (tipos borrados de sus
parámetros). Solo para `MethodRole.Constructor`; los métodos normales no cambian.

### 4.6 Binding de la declaración

- Declarar el constructor como hoy, más `EachParameters` (sin mapping; el default ya no vive
  aquí).
- Sintetizar y ligar el `$fill$...` (cuerpo = bloque `each`, ligado contra la clase, con
  `this` disponible).
- Checks del `each` (en el punto de declaración): aridad 1 o 2; parámetros posicionales, con
  tipo, sin defaults/varargs/nombre (`EachArityInvalid`).
- En la declaración de una interfaz con `default` (MemberPhase): resolver el default, aplicar
  los checks de 3.5 (clase, implementada, aridad, y que declara al menos un constructor `each`)
  y fijar `NamedTypeSymbol.DefaultBuilder`.

### 4.7 Binding de la expresión

- `BindCollectionInstantiation(syntax, expected)` (nodo nuevo):
  1. Si `Construction` es una referencia de tipo genérico → resolver como tipo → `BindCollectionBuild`.
  2. Si es un call: resolver el callee. Tipo con `each` → `BindCollectionBuild` (args escritos).
     Tipo sin `each` → error apuntando a `(Tipo(args))[i]`. Función/método → si el cuerpo es
     `[ e ]` (un solo elemento) re-encuadrar como `IndexExpression(Construction, e)`; si el
     cuerpo tiene 2+ elementos o es dict → error.
  3. Si es un identificador simple (forma sin paréntesis): resolver. **Tipo** con `each` →
     `BindCollectionBuild`. Tipo sin `each` → error. **Valor** (variable/parámetro/campo/
     singleton, §2.8) → si el cuerpo es `[ e ]` re-encuadrar como índice; si no, error.
- `BindCollectionBuild(syntax, type, writtenArgs, body)` (rutina compartida):
  1. Candidatos = constructores de `type` con `Role.Constructor` y `EachArity` == aridad del cuerpo.
  2. Resolver los args normales sobre esos candidatos (`BindArguments` + `OverloadResolution`).
     Forma explícita: los escritos. Forma target-typed: cero args → `Omitted()` para cada
     parámetro (sus defaults), reutilizando `BodyBinder.Expressions.cs:3157`.
  3. Unir cada elemento/entrada contra los `EachParameters`:
     `[ e ]` → `Convert(e, Each[0])`; `{ k: v }` → `Convert(k, Each[0])`, `Convert(v, Each[1])`.
     Si el parámetro es un `T` del contenedor, convertir contra el tipo sustituido y envolver en
     `ImplicitErasure` para boxear en el slot borrado — lógica de `ConvertIntoErased`
     (`BodyBinder.Expressions.cs:3124`).
- `BindArrayLiteral`/`BindDictLiteral`: rama de 3.6 ANTES del camino actual; si no aplica,
  fall-through al comportamiento de hoy (sin regresión).

### 4.8 Resolución del default (sin registro)

- El default viaja en la **interfaz**: `NamedTypeSymbol.DefaultBuilder` (null en las built-in).
- En el uso target-typed (regla 4 de 3.6): `expected` es una interfaz → leer su
  `DefaultBuilder`, sustituir los parámetros de la interfaz por los del uso
  (`IList<int>` → `List<int>`), verificar que tiene un `each` de la aridad que cuadra y
  construir. Sin registro de compilación, sin ambigüedad posible.
- En los módulos importados, el default se lee de la imagen (ver §5).

### 4.9 Bound tree

```csharp
// BoundExpressions.cs
public sealed class BoundCollectionBuildExpression : BoundExpression
{
    public NamedTypeSymbol Type;
    public MethodSymbol Constructor;                            // el `each` elegido
    public MethodSymbol FillMethod;                             // $fill$...
    public IReadOnlyList<BoundExpression> ConstructorArguments;
    public IReadOnlyList<IReadOnlyList<BoundExpression>> FillArguments;  // uno por elemento/entrada
}
```

### 4.10 Emisor

```csharp
// MethodBodyEmitter.cs — dispatch (~:2273) + EmitCollectionBuild
Code.NewObject(Descriptors.Emit(build.Type));      // ObjNew
Code.Dup();
foreach (var argument in build.ConstructorArguments) Expression(argument);
EmitResolvedCall(build.Constructor, virtualCall: false, discardResult: true);
foreach (var fillArgs in build.FillArguments)      // un fill por elemento/entrada
{
    Code.Dup();
    foreach (var argument in fillArgs) { Expression(argument); }
    EmitResolvedCall(build.FillMethod, virtualCall: false, discardResult: true);
}
// queda exactamente una copia en la pila = el valor del literal
```

`$fill$` es directo (privado, no virtual): sin dispatch por elemento. El boxeo del primitivo
en el slot borrado se resuelve en el binder (paso 3 de 4.7).

### 4.11 Análisis estáticos

Cada visitador que hoy trata `BoundArrayLiteralExpression`/`BoundDictLiteralExpression` recibe
un caso para `BoundCollectionBuildExpression`:

| Archivo | Comportamiento |
|---|---|
| `NoAllocCheck.cs:187/196` | aloca → reportar en cuerpo `@NoAlloc`, igual que el literal |
| `ConstFunctionCheck.cs:208/220` | no constante → reportar |
| `FlowAnalysis.cs:627/643` | caminar args + fillArgs |
| `InitializerOrder.cs:414/430` | orden: constructor → fills en orden |
| `PureFoldVerifier.cs:218/228` | no plegable (efectos de `$fill$`) |
| `InlineCost.cs:244/260` | coste = construcción + N·coste(fill) |

### 4.12 LSP

Casos para el nodo y la sintaxis en `SymbolResolver`, `SemanticTokensProvider`,
`InlayHintProvider` y `CompletionProvider` (donde ya tratan `BoundArrayLiteralExpression`/
`BoundDictLiteralExpression`: `:416/:426`, `:387/:397`, `:238/:248`, `:1193/:1203`).

### 4.13 Diagnósticos

- `EachOutsideConstructor` — `each` que no sigue a un constructor con cuerpo.
- `EachArityInvalid` — `each` con 0 o 3+ parámetros; con defaults/varargs/nombre.
- `InterfaceDefaultNotClass` — el `default` de una interfaz no es una clase.
- `InterfaceDefaultNotImplemented` — la clase no implementa la interfaz.
- `InterfaceDefaultArity` — el número de argumentos de tipo del default no coincide con los
  parámetros de la interfaz.
- `InterfaceDefaultNoEach` — la clase default no declara ningún constructor `each`.
- `BuilderArityMismatch` — `[ ]`/`{ }` frente a la aridad de los `each` disponibles.
- `CollectionLiteralOnFunction` — `[ a, b ]`/`{ ... }` tras una llamada a función.
- `CannotIndexConstruction` — indexar una construcción exige `(Tipo(args))[i]`.
- Reuso de `CannotConvert` / `CollectionElementConversionMissing` para los elementos.

## 5. Metadatos e imagen

- `SurtrInterface` gana `DefaultBuilder` (un `SurtrTypeHandle` a la clase default): la interfaz
  declara su default una vez y el uso lo lee directo.
- Los constructores `each` también viajan: la imagen registra en cada constructor su cláusula
  `each` (los tipos de sus parámetros), igual que los `$fill$` son métodos reales — sin eso, un
  tipo importado no expondría sus builders ni se podría verificar `InterfaceDefaultNoEach`
  cross-módulo.
- `SurtrModuleImageWriter`/`SurtrModuleImageReader`: escribir/leer el campo en la sección de
  interfaces. Bump de versión de formato del módulo.
- `MetadataImporter`: al importar una interfaz, leer su `DefaultBuilder` y fijar
  `NamedTypeSymbol.DefaultBuilder`.

## 6. Tests

- **Parser**: `List<int>[1,2,3]`, `List<int>(32)[1,2,3]`, `List<int>[5]`, `IntList[1,2,3]` (no
  genérico), `Map<string,int>{...}`, `Map<string,int>(16){...}`, `makeList<int>(5)[0]` (índice),
  `(List<int>(32))[0]` (índice), `arr[0]` (índice), constructor `each` con/sin bloque, `each` con
  3 params → error, `each` con default → error, `each` fuera de constructor → error, interfaz
  con `default` (y sin `default`), `default` que no es clase → error.
- **Binding**: `constructor(int)` vs `constructor(int) each` coexisten; los `each` no son
  alcanzables por llamada normal; el literal elige por aridad; conversiones por elemento;
  defaults de ctor; `[]`/`{}` vacíos; checks del default de interfaz (no-clase, no-implementada,
  aridad, sin ningún `each`); resolución target-typed de interfaz → default; interfaz sin
  default → error apuntando al concreto.
- **Runtime/emisión**: `List<int>[1,2,3]` produce la lista en orden; capacidad con `(32)`;
  dict por `each (key, value)`; boxeo de primitivos en `List<int>`; `@NoAlloc` reporta el
  builder; `$fill$` no es alcanzable por identificador.
- **Metadatos**: `DefaultBuilder` round-trip imagen; resolución cross-módulo.

## 7. Archivos a tocar

1. `Syntax/Ast/DeclarationSyntax.cs`, `Syntax/Ast/ExpressionSyntax.cs`
2. `Syntax/Parser.Declarations.cs`, `Syntax/Parser.Expressions.cs` (cabecera de interfaz: `default`)
3. `Binding/Symbols/SyntheticNames.cs`, `Binding/Symbols/MemberSymbols.cs`,
   `Binding/Symbols/NamedTypeSymbol.cs` (DefaultBuilder)
4. `Binding/SignatureSet.cs`
5. `Binding/BoundTree/BoundExpressions.cs`
6. `Binding/BodyBinder.Expressions.cs` (call-site, literales)
7. `CodeGen/MethodBodyEmitter.cs`, `CodeGen/InlineCost.cs`
8. `Binding/NoAllocCheck.cs`, `Binding/ConstFunctionCheck.cs`, `Binding/FlowAnalysis.cs`,
   `Binding/InitializerOrder.cs`, `Binding/PureFoldVerifier.cs`
9. `Diagnostics/SurtrDiagnosticCode.cs`
10. `Surtr.Core` — `SurtrInterface.DefaultBuilder`, `SurtrModuleImageWriter/Reader`
11. LSP: `SymbolResolver.cs`, `SemanticTokensProvider.cs`, `InlayHintProvider.cs`, `CompletionProvider.cs`
12. Stdlib: declarar el `default` en cada interfaz de colección (`IList`, `IReadOnlyList`,
    `ISet`, `IReadOnlySet`, `IQueue`, `IStack`) y los constructores `each` en sus clases
13. Tests

## 8. Riesgo / blast radius

- `BindArrayLiteral`/`BindDictLiteral`: **MEDIO** (46 símbolos, 1 caller directo =
  `BindExpression`; el resto son los visitadores en profundidad 2). El cambio es aditivo (rama
  nueva antes del camino actual), no reescritura.
- `ParsePostfix` es camino caliente; el cambio se limita a "tras un identificador, nombre
  genérico o `CallExpressionSyntax`". El camino de índice de variables se conserva (el binder
  re-encuadra, no re-parsea).
- Formato de imagen: bump de versión por `DefaultBuilder` (sección de interfaces).
- Regla clave contra regresiones: la intercepción target-typed es de **alcance estrecho**
  (3.6); `object`/`unknown`/`IIterable` no cambian de comportamiento.

## 9. Decisiones tomadas y pendientes

**Tomadas:**
1. El default se declara en la **interfaz** (`interface IList<T> default List<T> : ...`),
   reusando el keyword existente `default` (§4.3). Sin `for` en constructores, sin registro, sin
   unicidad que vigilar (una interfaz, un default). Las built-in no tienen default:
   `IIterable<T>` **siempre enlaza a `T[]`**. El default puede nombrar una clase de otro módulo
   si está importado (o calificado), pero **no** un ciclo (`ModuleCycle`, error duro): en la
   stdlib, `IReadOnlyCollection`/`ICollection` quedan sin default.
2. Forma sin paréntesis válida para **cualquier tipo** (genérico o no): la desambiguación es
   semántica (el identificador resuelve a tipo → builder; a valor → índice). El operador `[]`
   solo existe sobre instancias, nunca sobre un tipo.
3. Llamada normal: los constructores `each` **nunca** son alcanzables; solo los literales los
   ven. No hay tie-break que especificar.
4. Metadatos: `SurtrInterface.DefaultBuilder` + bump de formato de imagen; los constructores
   `each` viajan en la imagen.
5. El default exige que la clase declare **al menos un constructor `each`**
   (`InterfaceDefaultNoEach`); y cada interfaz admite **un solo** default (una declaración →
   un default, imposible duplicar).

**Pendiente:**
6. `@NoAlloc`: el builder aloca → reportado, igual que un array literal (implícito en 4.11;
   confirmar). Y el alcance del re-encuadre: el acceso a miembro (`obj.campo[0]`) NO se
   re-encuadra, así que un tipo calificado por punto usa la forma con llamada `foo.Bar()[...]`.