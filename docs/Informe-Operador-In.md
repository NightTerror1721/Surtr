# Informe: operador de pertenencia `in` (y `!in`) para Surtr

Estado: propuesta de diseño (solo investigación; no se ha tocado código).
Fecha: 2026-08-25.
Restricción de partida: para los objetos built-in (arrays, diccionarios, strings, rangos, tuplas) el operador debe **reutilizar opcodes existentes** tanto como sea posible.

---

## 1. Resumen ejecutivo

Surtr ya tiene casi todo lo necesario:

- `in` ya es palabra reservada (`TokenType.KeywordIn`, usada hoy solo por `for-in` y por la varianza `in T`), así que el léxico **no necesita cambios**.
- La VM ya tiene dos opcodes de pertenencia: **`ArrIn` (0x87)** y **`DictIn` (0x96)**, además del comparador de valores del runtime que ambos usan.
- El binder/codegen ya tienen el patrón exacto a imitar: `dict.containsKey(k)` se baja a `DictIn` y `arr.contains(v)` se baja a `ArrIn` (`MethodBodyEmitter.TryEmitDictionaryOperation/TryEmitArrayOperation`), y `is` demuestra cómo se añade un operador binario *keyword* a nivel de precedencia 10.
- Para strings y rangos no hace falta opcode nuevo: string tiene el método nativo `contains` (`SurtrStringBuiltIn`) y range tiene `contains` nativo (`SurtrCompositeBuiltIns.DeclareRange`); además, un rango puede bajarse a comparaciones enteras puras sin llamar a nada, igual que hace la cabecera de `for-in`.

**Conclusión**: `x in colección` se puede implementar en fases 1–2 **sin añadir ni un solo opcode ni tocar la VM**, reutilizando `ArrIn`, `DictIn`, llamadas nativas existentes y opcodes de comparación. El coste se concentra en parser (dos casos nuevos en `ParseBinary`), binder (una familia nueva en `BindBinary`) y emitter (una rutina de emisión con cuatro brazos).

---

## 2. Investigación del pipeline de operadores actual

### 2.1 Léxico

| Hecho | Referencia |
|---|---|
| `in` es keyword reservada (`KeywordIn`) desde el origen; hoy la usan `for-in` y la varianza contravariante `in T`. | `src/Surtr.Compiler/Syntax/TokenType.cs:165-166`; `src/Surtr.Compiler/Syntax/Lexer.cs:831`; varianza en `src/Surtr.Compiler/Syntax/Parser.Types.cs:281-302` |
| `!` léxico: consume `!=` → `NotEqual`, `!==` → `ReferenceNotEqual`, `!!` → `BangBang`, y en cualquier otro caso produce **`LogicalNot`** de un token. Por tanto `x !in y` ya lexifica hoy como `x`, `LogicalNot`, `KeywordIn`, `y` sin cambios en el lexer. | `src/Surtr.Compiler/Syntax/Lexer.cs:712-715` |
| Nada persiste `TokenType` a disco (a diferencia de `OpCode`), así que añadir tokens es libre si hiciera falta — no hará falta. | `src/Surtr.Compiler/Syntax/TokenType.cs:6-13` |

### 2.2 Parser de expresiones binarias

- Las precedencias viven en `Parser.BinaryPrecedence(TokenType)` (`src/Surtr.Compiler/Syntax/Parser.Expressions.cs:17-56`): igualdad (`== != === !==`) = nivel 9; relacional (`< <= > >=`) = 10; `<=>` = 11; `.. ..=` = 12. Todo es left-associative mediante climbing (`ParseBinary(precedence + 1)`, línea 217).
- `ToBinaryOperator` mapea token → `BinaryOperator` (`Parser.Expressions.cs:59-89`).
- **Precedente clave**: `is` es un operador binario *keyword* que no pasa por `BinaryPrecedence`; se resuelve con un caso especial dentro del bucle de `ParseBinary` que comprueba `10 >= minPrecedence` y parsea un tipo a la derecha (`Parser.Expressions.cs:192-205`). `in` seguiría exactamente este patrón.
- Unarios: `!` prefijo → `UnaryOperator.Not` (`Parser.Expressions.cs:237-240`). No existe ninguna construcción donde `!` siga a una expresión completa (no hay `!` postfijo), de modo que detectar `LogicalNot + KeywordIn` en posición binaria es inequívoco.
- `for-in`: `ParseFor` decide con el escaneo `IsForInAhead` (busca el primer `KeywordIn` tras un identificador opcionalmente anotado) (`src/Surtr.Compiler/Syntax/Parser.Statements.cs:226-304`). Este escaneo convive sin conflicto con un `in` binario: la secuencia tras el primer `in` se parsea como expresión completa, así que `for (x in a in b)` sería legal (y confuso; ver §9).
- Declaraciones de operadores: `ParseOperator` acepta `operator<token>` donde el token viene de `IsOverloadableOperator` (`src/Surtr.Compiler/Syntax/Parser.Declarations.cs:834-912` y `919+`); `operator[]` y `operator as` son los casos especiales reconocidos fuera de esa tabla. Ahí es donde entraría `operator in`.

### 2.3 AST

- Los operadores binarios son **un enum, no subclases**: `BinaryExpressionSyntax` lleva `BinaryOperator Operator` (`src/Surtr.Compiler/Syntax/Ast/ExpressionSyntax.cs:94-117`; justificación en `src/Surtr.Compiler/Syntax/Ast/SyntaxNode.cs:20-26`). El enum está en `SyntaxNode.cs:50-126`. Añadir `In`/`NotIn` son dos miembros nuevos del enum.
- `UnaryExpressionSyntax` cubre `!(...)` (`ExpressionSyntax.cs:120-137`), que es la forma canónica de negación.

### 2.4 Binder

Todo el peso está en `BodyBinder.Expressions.cs`:

- `BindBinary` (`src/Surtr.Compiler/Binding/BodyBinder.Expressions.cs:845-887`):
  - Orden de resolución para `==`/`!=`: **primero** el operador de usuario, después el built-in (líneas 860-871); la razón documentada es que dos operandos de la misma clase serían "asignables" y taparían el overload. `!=` **reusa la búsqueda de `op_==` y niega el resultado** envolviéndolo en `BoundUnaryExpression(Not)` (líneas 868-870). Este es el precedente directo para `!in`.
  - Si nada built-in encaja (`ResolveBinary` devuelve null), se intenta `TryBindUserOperator` (líneas 873-878); si tampoco, error `OperatorNotDefined`.
- `ResolveBinary` (`BodyBinder.Expressions.cs:896-992`): despacho por categoría del operador y tipos de los operandos; devuelve el tipo resultado o null. Casos ilustrativos:
  - Relacionales sobre strings: no hay opcode que ordene strings, se declaran definidos y el emitter baja a `compareTo` (líneas 950-954). Es decir, **el binder ya practica "definir el operador aunque la VM no tenga opcode" delegando al emitter una bajada especial**: exactamente lo que necesitamos para string/range/tuple.
  - Igualdad: bool si hay conversión de asignabilidad en cualquier dirección (líneas 967-979).
- Operadores de usuario: `TryBindUserOperator` (`1026-1065`) busca métodos por nombre `op_X` en ambos operandos (`_lookup.FindMethods`), resuelve overloads y construye la llamada con `BindOperatorCall` (`1074-1105`), que soporta forma estática (receiver como argumento 0) e instancia virtual/interface (receiver primero). `TokenFor` (`1107-1124`) mapea `BinaryOperator`→token de búsqueda: nótese que `Equal or NotEqual => TokenType.Equal` (línea 1120): **la negación comparte el nombre del operador base**.
- Nombres de operadores: `OperatorNames.Prefix = "op_"` y nombres **no escribibles** por usuarios porque ningún identificador puede contener `op_`… salvo que el símbolo sea una letra: para keywords se usa el texto de la keyword (`op_as`, `OperatorNames.Conversion`, `src/Surtr.Compiler/Binding/Symbols/OperatorNames.cs:33-37`). `TryGetSymbol` (`OperatorNames.cs:76-104`) define qué tokens son sobrecargables. Ahí se añadiría `TokenType.KeywordIn → "in"`, produciendo el nombre `op_in` (mismo estilo que `op_as`).
- `for-in` como referencia semántica de "qué es iterable": `TryFindIterableElementType` (`src/Surtr.Compiler/Binding/BodyBinder.Statements.cs:399-469`) responde estructuralmente para array/dict/string/range y por contrato `IIterable<T>.iterate()` para todo lo demás. Cualquier definición de "qué acepta `in`" debería apoyarse en esta misma función para no divergir.
- Pasadas genéricas que ya tolerarían un nuevo miembro del enum sin cambios (patronan sobre `BoundBinaryExpression` sin cerrar el enum): análisis de flujo (`src/Surtr.Compiler/Binding/FlowAnalysis.cs:422-425`), chequeo `const` (`src/Surtr.Compiler/Binding/ConstFunctionCheck.cs:185-188`), orden de inicializadores (`InitializerOrder.cs:377`), coste de inlineado (`CodeGen/InlineCost.cs:213`). Solo requieren revisión, no edición.

### 2.5 CodeGen

`MethodBodyEmitter.EmitBinary` (`src/Surtr.Compiler/CodeGen/MethodBodyEmitter.cs:2304-2423`) es una cascada de bajadas especiales antes del caso general:

1. Igualdad de value classes multi-slot (`EmitValueClassEquality`, 2306-2312).
2. Cortocircuito `&&`/`||` y `??` (2314-2324).
3. Test de ausencia (`int? == null` etc., 2333).
4. `x == null` → `IsNull`/`IsNotNull` de un solo operando (2336-2352).
5. Ordenación de strings → llamada nativa `compareTo` + comparación contra 0 (`EmitStringOrdering`, 2356-2362 y 2430-2450).
6. `<=>` → `EmitThreeWayCompare` (2364-2368).
7. Spine de `+` de strings → `StrCat` contado (2370-2376).
8. Caso general: push izquierda, push derecha, opcode por categoría (2378-2422). `NotEqual` **no** niega: emite `Compare(NotEqual)` directamente (2403); la negación fusionada existe porque el juego de instrucciones la trae.

El segundo mecanismo relevante es la **bajada de llamadas built-in a opcodes**: `EmitCall` consulta el conjunto precalculado `OpcodeableMembers` (`MethodBodyEmitter.cs:4650-4657`, poblado con `clear/get/set/containsKey/remove/keys/values` de dict, `get/set/push/pop/insert/removeAt/clear/indexOf/contains` de array y `charAt` de string) y despacha a `TryEmitDictionaryOperation`/`TryEmitArrayOperation`/`TryEmitStringOperation` (4127-4144):

- `dict.containsKey(k)` → `DictIn` (`4385-4393`).
- `arr.contains(v)` → `ArrIn` (`4516-4524`).
- Los operandos pasan por `EmitCollectionOperand` (`4319-4335`), que resuelve el boxing/desboxing del slot borrado `G0/K/V` — detalle obligatorio para agujas de tipos valor.
- Todo lo demás (p. ej. `string.contains`) queda como **llamada nativa real** vía `EmitResolvedCall` (`4238-4280`): `CallVirtual`/`Call`/`CallInterface` según la tabla del método. La VM no distingue bytecode de host (`OpCode.cs:59-64`).

Para `for-in`, `EmitForIn` (`MethodBodyEmitter.cs:620-656`) ya demuestra las bajadas por tipo que reutilizaremos: rango inline leído sin materializar (694-703), rango escapado leído por slots con `EnsureLocalRange`/`LoadLocalField` (705-746), walks indexados de array/string/tupla (775+), dict (855+), generador (951-1001) y el camino general por contrato `IIterable` con `CallInterface` (1003-1062). También `TryRangeSlotRead` (4623-4642) baja `.start/.end/.isInclusive` a lecturas de sub-slot sin llamada.

### 2.6 VM

- `ArrIn` (`src/Surtr.Core/VM/SurtrVirtualMachine.cs:2245-2251`): pop aguja, pop array, `array.IndexOf(needle, comparer) >= 0`, deja `bool`. Igualdad por **semántica de valores** del runtime (`SurtrValueComparer`), no bits crudos (`OpCode.cs:1253-1259`).
- `DictIn` (`SurtrVirtualMachine.cs:2494-2506`): pop clave, pop dict, ruta rápida `IntEntries.ContainsKey` o `ContainsKeyGeneral`, deja `bool`. Es prueba de **clave**, no de valor.
- Ambos asumen receptor no nulo (el cast lanza `NullReferenceException`), coherente con el resto de accesos a miembros.

---

## 3. Inventario de opcodes y built-ins reutilizables, por tipo

| Tipo contenedor | ¿Pertenencia hoy? | Opcode reutilizable | Built-in reutilizable | Coste |
|---|---|---|---|---|
| `array<T>` | Sí, completo | **`ArrIn` 0x87** (`OpCode.cs:1262-1268`; helper `SurtrCodeEmitter.OpCodes.cs:610-611`; VM `2245-2251`) | `contains(value: G0): bool` (`SurtrCompositeBuiltIns.cs:68,230-232`; baja a opcode hoy mismo, `LoweringChoiceTests` línea 1232) | O(n), comparador de valores |
| `{K: V}` | Sí, por clave | **`DictIn` 0x96** (`OpCode.cs:1398-1403`; helper `681-682`; VM `2494-2506`) | `containsKey(key: K): bool` (`SurtrCompositeBuiltIns.cs:317,358-359`; baja a opcode, test línea 517-522) | O(1) |
| `string` | Parcial | **Ninguno** (solo `StrGet` 0x78, `StrLen` 0x77, `StrCat`, `StrHash`; no hay `StrContains`) | `contains(value: text): bool` nativo Ordinal (`SurtrStringBuiltIn.cs:38,117-122`) | O(n·m) como llamada nativa; opcional opcode propio en fase futura |
| `range` | Sí, completo | **Ninguno necesario**: comparaciones enteras (`GE/LT/LE`) o llamada nativa | `contains(value: int): bool` nativo (`SurtrCompositeBuiltIns.cs:418,450-458`; `SurtrRange.Contains` en `src/Surtr.Core/Runtime/Objects/SurtrRange.cs:93-96`) | O(1); mejor aún sin llamada (§5.4) |
| `(A, B, ...)` tupla | No | Cadena de `TupGetC` (0x8C) + igualdad por elemento (`EQ/FEQ/StrEQ/DynEQ`) + `Or`/cortocircuito | No tiene métodos elementales (tupla sin genéricos, `SurtrCompositeBuiltIns.cs:282-293`) | O(aridad), aridad estática ≤ 255 |
| `generator<T>` / `IIterable<T>` | Solo iteración | Camino general de `for-in` (`GenResume/GenCurrent` o `CallInterface` iterate/moveNext/current) | `iterate()` (`SurtrIteratorBuiltIns.cs:94-104`) | O(n) y con efectos: un generador es de un solo uso — se recomienda **no** soportarlo (§9) |

Otros relevantes:

- `Inv` (0x4F) para negar un bool si se opta por emitir `!` encima del resultado (`OpCode.cs:738-744`; emisión actual en `MethodBodyEmitter.cs:3082-3085`).
- `DynEQ`/`DynNE` (0x60/0x61) si algún brazo necesita igualdad decidida en runtime sobre slots borrados.
- Valores libres 0xF0–0xFF en `OpCode` si alguna fase futura quisiera un opcode dedicado **sin** romper el formato (`OpCode.cs:48-57`).

---

## 4. Semántica propuesta

Resultado siempre `bool` (no nullable). Orden de evaluación izquierda→derecha (ver §5.4 para cómo se preserva con el orden de pila invertido de los opcodes).

| Expresión | Semántica | Equivalente hoy |
|---|---|---|
| `x in xs` (`xs: array<T>`) | pertenencia por valor con `SurtrValueComparer` | `xs.contains(x)` |
| `k in d` (`d: {K: V}`) | **pertenencia de clave** (como Python) | `d.containsKey(k)` |
| `sub in s` (`s: string`) | subcadena, comparación `Ordinal` (consistente con `indexOf`/`equals` del built-in) | `s.contains(sub)` |
| `x in lo..hi` / `lo..=hi` | intervalo: `x >= lo && (x < hi | x <= hi)` según el flag | `r.contains(x)`; con cabecera literal, sin materializar el rango |
| `x in t` (tupla) | `x == t.0 || x == t.1 || …` con la igualdad normal de cada elemento | (nuevo azúcar) |
| `x in obj` (clase/objeto) | Error de compilación salvo que el tipo declare `operator in` (§6). No se mira dentro de campos. | — |
| `x in iterableDeUsuario` | Fase posterior u omitida: exigiría recorrer con `IIterator` (efectos, cursores desechables, generadores de un solo uso). Ver decisión abierta D3. | — |

Reglas de tipado (binder):

- El **operando derecho decide la forma**; el izquierdo se comprueba contra él:
  - array: `left.Type` asignable al elemento `T` (mismo criterio que `push(value: G0)`), si no `CannotConvert`.
  - dict: asignable a `K`.
  - string: izquierdo debe ser `string` (decisión abierta D2 sobre aceptar `char`).
  - range: izquierdo debe ser `int`.
  - tupla: asignable a **al menos uno** de los elementos.
  - otro tipo: buscar `op_in` de usuario; si no, `OperatorNotDefined` (el mensaje existente interpola el enum: convendría darles display `"in"`/`"!in"`).
- Contenedor nullable (`array?<T>`, `string?`): permitido por el binder usando `NonNullable`; en runtime un contenedor nulo trapa igual que una llamada de método sobre null (coherente con `FieldGet`/`InvokeVirtual`). Alternativa más estricta (rechazar en compilación) queda como decisión abierta D4.
- Aguja nula (`null in xs`): permitida donde el comparador la defina; `ArrIndexOf` ya usa el comparador para strings por texto; documentar el caso concreto en tests.

Negación: `x !in y` ≡ `!(x in y)`, garantizada por construcción (misma resolución, resultado negado), igual que hoy `!=` reusa `op_==` y niega (`BodyBinder.Expressions.cs:861-871`).

---

## 5. Diseño propuesto (fase 1–2, sin opcodes nuevos)

### 5.1 Gramática y léxico

Sin cambios en `Lexer` ni en `TokenType`.

```
expresión        ::= … nivel 11
nivel-relacional ::= nivel-spaceship ( ('<' | '<=' | '>' | '>=' | 'is' Type | 'in' | '!in') nivel-spaceship )*
```

Cambios concretos en `Parser.Expressions.cs`:

1. `BinaryPrecedence`: `case TokenType.KeywordIn: return 10;` (misma fila que `< <= > >= is`; Python coloca `in/not in` en su nivel de comparaciones, y aquí conviene por la misma razón: `x in 1..10` debe leerse `x in (1..10)`, y como `..` está en el nivel 12 > 10, el rango se agrupa primero automáticamente).
2. `ToBinaryOperator`: `case TokenType.KeywordIn: return BinaryOperator.In;`
3. En el bucle de `ParseBinary`, junto al caso de `is` (líneas 192-205), detección de la forma negada:

```csharp
// x !in y: '!' no puede seguir a una expresión completa en ninguna otra producción,
// así que el par LogicalNot+KeywordIn en posición binaria sólo puede ser '!in'.
if (reader.Check(TokenType.LogicalNot) && reader.CheckAt(1, TokenType.KeywordIn)
    && 10 >= minPrecedence)
{
    reader.Advance(); reader.Advance();
    ExpressionSyntax right = ParseBinary(11);
    left = new BinaryExpressionSyntax(left.Span.To(right.Span), BinaryOperator.NotIn, left, right);
    continue;
}
```

No hay colisión con `IsForInAhead` (busca el primer `in` tras un identificador; el resto se parsea como expresión) ni con la varianza `in T` (posición de tipo, nunca de expresión).

### 5.2 AST

Dos miembros en `BinaryOperator` (`src/Surtr.Compiler/Syntax/Ast/SyntaxNode.cs:50-126`):

```csharp
/// <summary><c>in</c> — pertenencia.</summary>
In,
/// <summary><c>!in</c> — negación de pertenencia.</summary>
NotIn,
```

No hay nodo nuevo: `BinaryExpressionSyntax` ya parametriza por operador.

### 5.3 Binding (`BodyBinder.BindBinary`)

Nuevo bloque en `BindBinary` tras ligar operandos (antes del fallback de usuario general, siguiendo el estilo de los casos `Equal/NotEqual` de las líneas 861-871):

```csharp
if (syntax.Operator is BinaryOperator.In or BinaryOperator.NotIn)
    return BindMembership(syntax, syntax.Operator, left, right);
```

`BindMembership`:

1. Si cualquiera de los dos es error → `Error(syntax)`.
2. `ResolveMembership(op, ref left, ref right)`: clasifica por `right.Type.NonNullable`:
   - `ArrayTypeSymbol a`   → exige asignabilidad left→`a.ElementType`; resultado `_factory.Bool`.
   - `DictionaryTypeSymbol d` → asignabilidad left→`d.KeyType`; `_factory.Bool`.
   - `SpecialType.String`  → left debe ser `String` (D2: ¿también `Char`?); `_factory.Bool`.
   - `SpecialType.Range`   → left debe ser `Int`; `_factory.Bool`.
   - `TupleTypeSymbol t`   → asignable a algún elemento; `_factory.Bool`.
   - otro                  → null (caerá a operador de usuario, §6).
3. Devuelve `new BoundBinaryExpression(syntax, op, left, right, _factory.Bool)` — el emitter distingue el tipo del contenedor; el bound tree no necesita nodos por colección.

Detalles de integración:

- `TokenFor` gana `BinaryOperator.In or BinaryOperator.NotIn => TokenType.KeywordIn` (espejo de la línea 1120).
- Narrowing por `!= null` no cambia; `in` no estrecha tipos (a diferencia de `is T` hoy, que sí estrecha a `T` cuando `T` cabe en el tipo declarado, §5.7 de Language-Syntax.md).
- Plegado constante opcional en `ConstantEvaluator.TryBinary` (`ConstantEvaluator.cs:231`, caso `NotEqual` en 262): plegar `"ell" in "hello"`, `5 in 1..10`, `2 in [1,2,3]` cuando los operandos sean literales; no es requisito de fase 1.

### 5.4 Emisión (`MethodBodyEmitter.EmitBinary`)

Un único punto de entrada nuevo, `EmitMembership(binary)`, invocado desde la cascada de `EmitBinary` (junto a los casos de cortocircuito de las líneas 2316-2324). Desglose por contenedor, con los opcodes exactos:

**(a) Array — reusa `ArrIn` tal cual.**

```
emit(right)            ; el array (receptor)
Ldl(tmp)               ; la aguja, guardada antes en un temporal de método (patrón $limit de EmitForInRange, línea 687)
ArrIn                  ; 0x87: ..., arr, value -> ..., bool
[ NotIn:  Inv ]        ; 0x4F
```

Orden de pila: `ArrIn` quiere `(array, aguja)` pero el código fuente evalúa primero la aguja (izquierda→derecha). Se preserva evaluando la izquierda a un temporal declarado con `DeclareLocal` antes de emitir el receptor:

```
tmp = DeclareLocal("$needle")
emit(left); store tmp       ; 1) la aguja, como en el fuente
emit(right)                 ; 2) el contenedor
Ldl(tmp)                    ; 3) la aguja, en el orden de pila que ArrIn exige
ArrIn
```

Cuando la izquierda es trivial (local, literal, campo) se puede emitir derecha-primero sin temporal; la optimización es opcional y debe quedar behind un chequeo de pureza o simplemente omitirse.

**(b) Diccionario — reusa `DictIn`.**

```
tmp = DeclareLocal("$key")
emit(left); store tmp      ; 1) la clave, como en el fuente
emit(right)                ; 2) el dict
Ldl(tmp)                   ; 3) la clave, encima de la pila (con BoxIfMultiSlot/UnboxIfStillErased vía EmitCollectionOperand)
DictIn                     ; 0x96: ..., dict, key -> ..., bool
[ NotIn: Inv ]
```

Mismo truco del temporal que en (a): `DictIn` exige `(dict, clave)` y el fuente evalúa la clave primero.

**(c) String — reusa la llamada nativa `contains` (cero opcodes nuevos).**

Recomendado: bajarlo en el **emitter** dentro de `EmitMembership`, reutilizando la identidad del método built-in (`IsStringMember`, `MethodBodyEmitter.cs:4586-4594`) y el camino ordinario de llamada:

```
tmp = DeclareLocal("$needle")
emit(left); store tmp        ; 1) la aguja, como en el fuente
emit(right)                  ; 2) el receptor string
Ldl(tmp)                     ; 3) la aguja como argumento
BoxReceiverForCall(...)
CallVirtual string.contains  ; InvokeVirtual 0xDB hacia el EntryPoint nativo
```

Esto es exactamente el tratamiento que hoy reciben `substring`, `startsWith`, etc.: llamada nativa sin opcode dedicado. La identidad importada del método garantiza que un `contains` de un tipo usuario nunca se confunde con el del built-in.

Alternativa: que el **binder** sintetice directamente una `BoundCallExpression` hacia `string.contains` (hay precedente de síntesis de llamadas en `ModuleEmitter.cs:1767`) y deje todo el trabajo al `EmitCall` existente. Es menos código, pero invierte el orden de evaluación (el receptor se emite antes que los argumentos en `EmitCall`, líneas 4164-4177); aceptable solo si se documenta o si la izquierda es trivial.

**(d) Rango — comparaciones puras; cero materialización cuando la cabecera es `lo..hi`.**

Caso 1: el derecho es el nodo binario `Range`/`RangeInclusive` (`x in lo..hi`). Como `EmitForInRange` hace con la cabecera (`MethodBodyEmitter.cs:694-703`), sin materializar el rango; el orden de evaluación es `x`, `lo`, `hi`:

```
tmp = DeclareLocal("$x")
emit(left); store tmp                              ; 1) x
emit(binary.Left)                                  ; 2) lo
Ldl(tmp); Compare(GreaterOrEqual, Integer)         ;    x >= lo        (GE 0x53)
emit(binary.Right)                                 ; 3) hi
Ldl(tmp); Compare(Less|LessOrEqual, Integer)       ;    x < hi | x <= hi según el flag del nodo
And                                                ; 0x49
[ NotIn: Inv ]                                     ; o invertir la comparación final (LT↔GE, LE↔GT)
```

Caso 2: el derecho es un rango escapado (variable/retorno). Spill del bloque de 3 slots con `EnsureLocalRange(seq, RangeSlotWidth)` (usado en `TryRangeSlotRead`, `MethodBodyEmitter.cs:4639`) y lectura por `LoadLocalField(baseSlot, 0|1|2)`:

```
base = EnsureLocalRange(right)
tmp = "$x"; emit(left); store tmp
LoadLocalField(base,0); Ldl(tmp); Compare(GE, Int)          ; start
LoadLocalField(base,2); JumpIfFalse(exclusivo)              ; isInclusive?
  LoadLocalField(base,1); Ldl(tmp); Compare(LE, Int); Jump(fin)
exclusivo: LoadLocalField(base,1); Ldl(tmp); Compare(LT, Int)
fin: And
[ NotIn: Inv ]
```

Todo son opcodes existentes; ni pack ni llamadas (coherente con Plan-Rangos.md, líneas 18-19 y 72-74).

**(e) Tupla — cadena de igualdades con elementos estáticos.**

Con aridad conocida (`TupleTypeSymbol`), la tupla y la aguja en locales:

```
tup = DeclareLocal("$tup");  emit(right); store tup
tmp = DeclareLocal("$needle"); emit(left); store tmp
por cada i: Ldl(tup); TupGetC(i)             ; 0x8C, índice inmediato
            Ldl(tmp)
            <igualdad del elemento i>        ; EQ/FEQ/StrEQ/REQ o la ruta de EmitBinary(Equal) del tipo
            combinar con Or (cortocircuito con EmitShortCircuit si se sintetiza como LogicalOr)
[ NotIn: Inv ]
```

Alternativa más simple y uniforme: que el **binder** sintetice el árbol equivalente (`BoundBinaryExpression(LogicalOr, BoundBinaryExpression(Equal, TupGetC-sintético, left), …)`), como ya hace `BindNullCoalesce` construyendo nodos a mano (`BodyBinder.Expressions.cs:1147-1152`). Contras: la izquierda quedaría evaluada varias veces; por eso la recomendación es bajarlo en el emitter con temporal. Fase 2.

**(f) Fallback de usuario:** si `BindMembership` resolvió por `op_in`, el bound tree ya es un `BoundCallExpression` y `EmitCall` lo emite sin cambios (estático → `InvokeStatic`/`Call`; virtual/interface → `CallVirtual`/`CallInterface` vía `BindOperatorCall`, `BodyBinder.Expressions.cs:1074-1105`).

**Fusión con saltos (opcional, fase de optimización):** `EmitCondition` fusiona comparaciones en `JPxx` cuando la condición de un `if`/`while` es un `BoundBinaryExpression` comparativo (`MethodBodyEmitter.cs:457-560`). La pertenencia no participará en la fusión (caerá al camino genérico: producir bool y `JPZ/JPNZ`), lo cual cuesta un dispatch extra; aceptable de inicio.

### 5.5 Ejecución en la VM

**Ningún cambio.** `ArrIn` y `DictIn` ya están implementados y probados (`SurtrVirtualMachine.cs:2245-2251` y `2494-2506`; tests `SurtrVirtualMachineArrayTests.cs:302-309`, `SurtrVirtualMachineTupleAndDictionaryTests.cs:254-262`). Las llamadas nativas de string/range ya corren sobre `SurtrNativeEntryPoint`. `docs/Opcodes.md` **no se modifica** en estas fases (no aparece ningún opcode nuevo), solo `docs/Language-Syntax.md` (§5.6 tabla de sobrecargables, §5.7 tabla de precedencias y notas).

### 5.6 El operador negado `!in`

Resumen de las tres opciones consideradas:

| Opción | Descripción | Valoración |
|---|---|---|
| A. Enum `NotIn` propio (recomendada) | Parser produce `NotIn`; binder resuelve igual que `In` y marca la negación; emitter añade `Inv` (o invierte la comparación en el caso rango: `LT↔GE` etc.) | Refleja el precedente `!=`→`op_==`+negación; permite diagnosticar `!in` con su propia grafía; coste: un `Inv` extra en el hot path |
| B. Azúcar puro en parser | `a !in b` ⇒ `UnaryExpressionSyntax(Not, BinaryExpressionSyntax(In, …))` | Cero cambios en binder/emitter, pero los diagnósticos y el LSP hablan de `!` sobre un `in` que el usuario no escribió; pierde la grafía en errores |
| C. Opcode `NotIn` fusionado por colección | `ArrNotIn`, `DictNotIn`, … | Viola la restricción de reutilización; consume bytes del set para una ganancia de 1 dispatch |

Con A, en el caso rango se puede evitar el `Inv` invirtiendo directamente la comparación final (`Less ↔ GreaterOrEqual`, `LessOrEqual ↔ Greater`) igual que hace el mapa de `Invert` que ya usa el emitter para igualdades (`MethodBodyEmitter.cs:601-602`). Para `ArrIn`/`DictIn` el `Inv` es un byte y un dispatch: se acepta.

Microdetalle léxico: `a ! in b` (con espacio) produce los mismos tokens que `a !in b` y se aceptará igual; no hay forma de distinguirlos ni motivo para hacerlo.

---

## 6. Objetos definidos por usuario: `operator in`

Propuesta alineada con §5.6 de Language-Syntax.md:

```
class Inventory {
    operator in(item: Item): bool { … }        // estático por defecto: op_in(item: Inventory, ...)
    // o, con despacho (Plan-Globales-Nativos-Inline-Operadores.md fase C ya lo soporta):
    virtual operator in(self: IContainer, item: Item): bool;
}
interface IContainer { operator in(self: IContainer, item: Item): bool; }
```

Puntos de enganche:

1. **Nombre**: `OperatorNames.TryGetSymbol` gana `case TokenType.KeywordIn: symbol = "in"; return true;` → el método se declara como `op_in` (mismo estilo que `op_as`, no escribible por usuarios porque `in` es reservada; ver `OperatorNames.cs:11-27`). Alternativa estilo C#/Python sería llamarlo `op_Contains`, pero rompería la regla "el nombre sale del token" (`For()`, líneas 59-72) y ensuciaría el desensamblado.
2. **Declaración**: `ParseOperator` admite `KeywordIn` en la posición del token (añadirlo a `IsOverloadableOperator`, `Parser.Declarations.cs:919+`). Aridad 2 obligatoria; retorno `bool` exigido por el binder.
3. **Resolución**: `TryBindUserOperator` ya busca el nombre en **ambos** operandos (línea 1036-1037) y respeta la regla "al menos un operando debe ser el tipo declarante". Para `in` no hace falta el truco de precedencia de `==` (el built-in solo reclama colecciones built-in, no tapa overloads); orden recomendado: built-in primero, usuario después — aunque probar usuario primero también es correcto y haría el pipeline uniforme con `==`. Decisión menor, fijar en implementación.
4. **Negación**: `!in` reusa la misma búsqueda (`TokenFor` mapea ambos a `KeywordIn`) y niega el resultado, idéntico a `!=` (líneas 864-871). No existe `operator !in` declarable.
5. **Extensiones** (`extension`, §15): un `extension` podría añadir `op_in` a tipos ajenos con el mismo mecanismo de métodos — gratis, sin trabajo adicional.
6. **Interfaces y operadores abstract/virtual/sealed**: el soporte existe (fases C/F1 del plan de operadores, `docs/Plan-Globales-Nativos-Inline-Operadores.md:75,235`), así que `op_in` hereda el comportamiento sin trabajo extra.

Semántica que NO se propone: `in` sobre campos de una clase (reflexión implícita) ni sobre módulos. Quien quiera ese efecto declara `op_in` o usa el módulo de reflexión existente.

---

## 7. Comparativa con otros lenguajes

| Lenguaje | Forma | Precedencia | Dict | String | Range/iterables | Sobrecarga |
|---|---|---|---|---|---|---|
| **Python** | `x in y` / `x not in y` | mismo nivel que comparaciones; encadenables (`a < x in b`) | **claves** (`__contains__`/`__getitem__`) | subcadena | cualquier objeto con `__iter__` (fallback tras `__contains__`) | `__contains__(self, item)`; sin `__not_contains__` (negación sintética) |
| **C#** | no hay `in` de pertenencia en expresiones (`in` = foreach/parámetros/patrones `is … and not null`); pertenencia idiomática: `xs.Contains(x)` (LINQ/métodos), patrones `or` para rangos | — | `ContainsKey`/`ContainsValue` | `string.Contains(string)` (y `char` por sobrecarga) | `Enumerable.Range(..).Contains(..)`, `ICollection<T>.Contains` | `IOperable` no aplica; se expone como método. `INotifyPropertyChanged`-style patterns no aplican. La lección útil: C# eligió **métodos**, no operador — Surtr hace lo contrario por ergonomia de scripting, apoyándose en los mismos métodos built-in |
| **Lua** | no existe `in` como operador (palabra clave solo en `for k in pairs(t)`); pertenencia manual (`t[x] ~= nil` para claves; bucles para valores) | — | claves vía indexado | `string.find(sub)` | manual | metamétodo `__index` no equivale; no hay `__contains` |
| **GDScript (Godot)** | `x in y` / `x not in y` | nivel de comparación | **claves** | subcadena | arrays, rangos (`i in range(n)`), cualquier iterable (`iterates`/`has_method`) | sin sobrecarga pública directa (se basa en `has`/`iterates` internos) |

Conclusiones aplicables:

1. Colocar `in`/`!in` en el **nivel relacional (10)** coincide con Python/GDScript y evita sorpresas con `..` (que agrupa antes).
2. Dict por **claves** es la elección unánime (Python, GDScript); la búsqueda por valores queda expresible hoy como composición si se desea (`d.values()` + fase futura), y no merece segunda forma del operador.
3. La negación va siempre **acoplada** al positivo (`not in`, `!in`); ningún lenguaje la trata como operador independiente — valida la opción A del §5.6.
4. La sobrecarga por un único gancho (`__contains__`) es el patrón dominante; `op_in` con negación derivada es el equivalente natural en la convención `op_*` de Surtr.

---

## 8. Roadmap de implementación por fases

**Fase 1 — built-ins principales (parser, binder, emitter, docs, tests).**
1. `BinaryOperator.In/NotIn`; precedencia 10; detección `!in` en `ParseBinary` (§5.1).
2. `BindMembership` + `ResolveMembership` para array/dict/string/range (§5.3) con diagnósticos (`CannotConvert`, `OperatorNotDefined` con display `in`/`!in`).
3. Emisión: `ArrIn`, `DictIn`, llamada `string.contains`, comparaciones de rango (§5.4 a-d), `Inv` para `NotIn`.
4. Tests: parser (nuevo archivo en `src/Surtr.Tests/Compiler/Syntax`), diagnósticos del binder (`Compiler/Binding`), elección de bajada estilo `LoweringChoiceTests.cs` (`Compiler/CodeGen`; patrón de los tests `AContainsKeyOnADictionaryIsDictIn`, línea 517), ejecución en `VM/`.
5. Docs: filas nuevas en §5.6/§5.7 de `Language-Syntax.md`. **Sin tocar** `Opcodes.md` ni `OpCode.cs`.

**Fase 2 — tuplas y pulido.**
6. Bajada de tupla con `TupGetC` + igualdad por elemento y temporal (§5.4e).
7. Plegado constante de pertenencias literales en `ConstantEvaluator.TryBinary`.
8. Revisión de `FlowAnalysis/ConstFunctionCheck/InlineCost/InitializerOrder` (se espera cero diffs) y de los mensajes del LSP.

**Fase 3 — sobrecarga por usuario.**
9. `OperatorNames` + `ParseOperator` + `TokenFor` para `op_in` (§6); resolución estática/virtual/interface; `!in` derivado.
10. Tests de overload resolution y de operadores en interfaces; doc §5.6.

**Fase 4 (opcional, bajo demanda) — optimizaciones.**
11. Fusión condición+saltos para pertenencia (evitar bool intermedio) en `EmitCondition`.
12. Evaluar con benchmarks (`Surtr.Bench`) si `StrContains` como opcode justifica consumir un valor libre 0xF0 (rompería la restricción de reutilización; solo si el perfilado lo pide).

**Fuera de alcance propuesto**: `in` sobre `IIterable<T>/generator` arbitrarios (ver D3) y `in` reflexivo sobre clases.

---

## 9. Riesgos y decisiones abiertas

**Riesgos.**

- R1 — **Ambigüedad con `for-in`**: `for (x in a in b)` es legal pero ilegible. Mitigación: nota de estilo; el parser no necesita regla extra (el primer `in` separa siempre).
- R2 — **Orden de evaluación vs orden de pila**: `ArrIn`/`DictIn` quieren el contenedor debajo; evaluar derecha-primero rompería izquierda→derecha. Mitigación: temporal local (§5.4a); riesgo de olvido en refactorizaciones → test específico con efectos laterales en ambos operandos.
- R3 — **Operandos multi-slot (value classes) como aguja**: pasarlos crudos a `ArrIn`/`DictIn` corrompe el storage. Mitigación: reusar `EmitCollectionOperand` (box/unbox de erasure), igual que `arr.contains` hoy.
- R4 — **Igualdad consistente**: la pertenencia usa el comparador de valores (strings por texto, boxed == unboxed). El brazo tupla (fase 2) debe usar la misma igualdad por elemento o se introduciría una segunda noción de "==". Mitigación: reutilizar la emisión de `BinaryOperator.Equal` por elemento, no un comparador ad-hoc.
- R5 — **Contenedor nulo**: trap en runtime. Documentarlo claramente; es coherente con el resto del lenguaje pero difiere de Python (`TypeError` con mensaje).
- R6 — **Explosión de casos en `EmitBinary`**: ya es la función con más brazos del emitter. Mitigación: `EmitMembership` aislado con su propia región y tests de bajada por tipo.

**Decisiones abiertas.**

- D1 — ¿Probar operador de usuario antes o después del built-in en `BindMembership`? Recomendado: built-in primero (no hay solape real); uniformidad argumentaría lo contrario.
- D2 — ¿`char in string`? El built-in `contains` toma `text`. Opciones: rechazar; convertir `char`→`string` implícitamente (necesitaría `fromChar` sintetizado); o añadir overload nativo `contains(char)` (toque mínimo en `SurtrStringBuiltIn`, sin opcode). Recomendado: overload nativo en una fase posterior; v1 solo `string`.
- D3 — ¿`in` sobre `IIterable<T>`/generators? Recorrer tiene efectos (dispose, generadores de un solo uso que se consumirían como efecto lateral de un *test*) y coste O(n) invisible. Recomendado: no soportar en v1; error claro sugiriendo convertir a array. Revisar si aparece demanda real.
- D4 — ¿Rechazar en compilación un contenedor nullable (`xs?: array?<T>`)? Hoy se propone permitir y trapear como cualquier acceso sobre null. Alternativa estricta posible sin coste.
- D5 — ¿Búsqueda por **valores** en dicts (`v in d.values()`)? Fuera de v1; componible con lo existente.
- D6 — Nombre del overload de usuario: `op_in` (recomendado, sigue la regla del token) frente a `op_Contains` (estilo C#/Python). Decidir antes de fase 3 porque es metadato visible en desensamblados y firmas.
- D7 — ¿Encadenamiento tipo Python (`a in b in c` ≡ `(a in b) and (b in c)`)? Surtr no encadena comparaciones hoy (`a < b < c` ya es `(a<b)<c`); mantener la semántica binaria simple y no introducir encadenamiento solo para `in`.
