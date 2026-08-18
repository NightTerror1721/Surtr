# Plan: extension methods y extension properties (`extension`)

**Estado: planificación — nada de este documento está implementado.** Investigación a fondo
sobre el codebase real (parser/AST, binder, `MemberLookup`, `OverloadResolution`, codegen,
runtime, Language Server, cliente VSCode) para diseñar un mecanismo al estilo C#/Kotlin que
permita añadir métodos y propiedades — de instancia y estáticos — a un tipo ya definido
(de usuario o built-in) sin tocar su declaración original. Sigue el mismo formato que
`docs/Plan-Sintaxis-Imports-Atributos-LSP.md` y `docs/Plan-Stdlib.md`: fases independientes,
cada una termina en su propio commit tras build + suite en verde, ninguna empieza sin cerrar
la anterior.

---

## Resumen ejecutivo

**Veredicto de viabilidad: alto.** La investigación encontró que Surtr ya tiene, en producción,
el mecanismo exacto que un extension method necesita: `BindOperatorCall`
(`src/Surtr.Compiler/Binding/BodyBinder.Expressions.cs:931-962`) ya reescribe una llamada de
instancia a un operador estático como una llamada a función con el receptor insertado como
primer argumento — `Receiver: null`, `isVirtual: false`, mismo `BoundCallExpression` que
cualquier función de módulo. Ese es, literalmente, el mecanismo de emisión que necesita
`obj.ext(x)` → `Modulo.ext(obj, x)`.

La consecuencia más importante de la investigación: **un extension method no necesita tocar
absolutamente nada del runtime.** Ni `SurtrClass`, ni vtables, ni `SurtrTypeLinker`, ni
opcodes, ni el emisor de bytecode (`SurtrCodeEmitter`), ni la reflexión (`Type.members()`
nunca los vería, correctamente). Todo el trabajo real vive en el **binder** — es azúcar
sintáctica pura, del mismo tipo que ya se implementó para "método → closure sin lambda
explícita" (Fase 4 de `docs/Plan-Sintaxis-Imports-Atributos-LSP.md`) y para operadores
estáticos. El Language Server hereda casi todo gratis porque reusa el binder real
(`Workspace.cs:85-87`), salvo dos puntos concretos (ir-a-definición y el completado tras `.`)
que sí necesitan cambios explícitos, detallados en la Fase 7.

No hay ningún precedente de "extension methods" en la especificación ni en el roadmap
(`docs/Compiler-Plan.md` §10.2, `Language-Syntax.md` §14.4 "Deferred language features") — es
una construcción completamente nueva, sin hueco reservado en la gramática.

---

## Por qué `extension` y no `extend`

La propuesta inicial usaba `extend <Tipo> { ... }`. Descartada tras revisar cómo la spec ya usa
esa raíz: `docs/Language-Syntax.md` emplea "extend"/"extended" en un sentido **muy concreto y ya
establecido** — herencia de clase (`sealed class` "cannot be **extended**", `value class`
"cannot **extend** or be **extended**"), y §1.2 presenta como *ausencia deliberada de diseño*
que Surtr no tiene `extends`/`implements` como keyword ("§2.2 uses a single `:` list"). Introducir
una palabra reservada `extend` para un mecanismo que no crea un subtipo, no participa en la lista
`:`, y no afecta `is`/`as` — resucitaría exactamente la raíz que la spec dice deliberadamente
que evitó, con un significado distinto. Confunde a cualquiera que conozca esa regla y, peor,
contradice una decisión de diseño ya documentada.

`extension <Tipo> { ... }` (sustantivo, no verbo) evita el choque — no aparece hoy en la spec en
ningún sentido relacionado (grep: solo "`.surtr` extension" como extensión de fichero y "sign
extension" como operación de bits, ambos claramente desambiguados por contexto) — y es
además el término que ya usan tres lenguajes de referencia para este mecanismo exacto: **C# 14**
("extension blocks"), **Swift** (`extension Type { }`) y **Dart** (`extension ... on Type`).
Esto pesa especialmente porque el público objetivo de Surtr viene mayoritariamente de C#/Unity,
donde "extension method" ya es vocabulario conocido — menor curva de aprendizaje que cualquier
término inventado.

---

## Decisiones de diseño (confirmadas con el usuario en la Fase 0)

| # | Decisión | Recomendación | Por qué |
|---|---|---|---|
| 1 | Nombre de la palabra clave | **`extension`** | Ver apartado anterior — evita colisión con el vocabulario de herencia ya establecido (`extend`/`extended`) y coincide con el término que C# 14, Swift y Dart ya usan para el mismo mecanismo. |
| 2 | Reservada vs. contextual | **Reservada** (como `singleton`), no contextual (como `value`/`attribute`) | `value`/`attribute` son contextuales porque se usan profusamente como identificadores ordinarios (`value` es literalmente el parámetro implícito de todo `set` accessor); `extension` no tiene ese conflicto. Ser reservada evita toda la maquinaria de lookahead en el parser y el caso especial en `SemanticTokensProvider` que sí necesitan las contextuales — confirmado que `SemanticTokensProvider.CollectDeclarationKeywords` solo existe para keywords contextuales; una reservada la resuelve la gramática TextMate sola. |
| 3 | Miembros permitidos dentro de `extension { }` | `fun` (métodos de instancia y estáticos) y propiedades **solo computadas** (con cuerpo explícito `get`/`set` o `=>`, nunca auto-property) | No hay dónde guardar un campo — la instancia layout del tipo objetivo está congelada (`BuildState.Built`, `ThrowIfBuilt()`). `constructor`, `static { }` y campos (`let`/`var`) se rechazan con diagnóstico explícito: no hay una posición de identidad ni de storage donde tendrían sentido. |
| 4 | Anidación de `extension` dentro de una clase | Afecta **solo visibilidad** (scoping léxico), no dobles receptores estilo Kotlin (`this` de la clase contenedora + receptor de extensión simultáneos) | Full dual-receiver es una feature mucho mayor (interactúa con `this` implícito, captura, virtual dispatch, boxing) — se recomienda diferir explícitamente, con la misma justificación que ya usa `Language-Syntax.md` §14.4 para otras features consideradas y pospuestas. Un `extension` nested dentro de una clase, en este plan, es simplemente un bloque cuya visibilidad efectiva más amplia es la del contenedor (igual que un tipo anidado privado). |
| 5 | Visibilidad del bloque + de cada miembro | El bloque tiene su propia `Visibility` (default `internal`, igual que cualquier declaración de nivel de módulo, §3.1); cada miembro puede declarar la suya propia, que debe ser **igual o más estrecha, nunca más amplia**, que la del bloque — si no se especifica, hereda la del bloque | Mismo patrón exacto que ya existe para accessors de propiedad (`ResolveAccessorAccessibility`, Fase 3 de `docs/Plan-Sintaxis-Imports-Atributos-LSP.md`): "estrictamente más restrictiva, nunca igual ni más permisiva" salvo que aquí sí se permite igual (heredar). |
| 6 | Prioridad de resolución frente a un miembro real | Un miembro real (declarado en la jerarquía del tipo) **siempre gana, en silencio** — la extensión solo se prueba si la búsqueda normal devuelve cero candidatos | Igual que C#/Kotlin. No es una advertencia ni un error: es exactamente la regla ya vigente para "método → closure" (§8: "tried as a method group only after every other reading of it... has already failed"). |
| 7 | Ambigüedad entre dos extensiones aplicables por igual | Si dos bloques `extension` visibles desde el mismo punto de uso ofrecen candidatos igualmente específicos para el mismo receptor, es **error de ambigüedad** (mismo diagnóstico que ya produce `OverloadResolution` para cualquier otro empate) | Los candidatos de extensión de *todos* los bloques visibles (módulo propio + imports) se combinan en **una sola** llamada a `OverloadResolution.Resolve`, no en llamadas secuenciales — al contrario que el paso "miembro real vs. extensión", que sí es secuencial/con prioridad. |
| 8 | Genéricos | El bloque `extension` declara su **propia** lista `<T>` (no ve los parámetros del tipo objetivo — misma regla de "static-nested" que ya rige un tipo anidado, §6), inferida contra el receptor real en el punto de uso | `extension Array<T> { fun sum(self: Array<T>): T }` es análogo a `fun <T> Array<T>.sum(): T` en Kotlin/C#, salvo que el receptor es el primer parámetro explícito de `sum` (Fase 1 — ver hallazgo de diseño), no un `this` implícito. Reutiliza la inferencia de tipo genérico estructural ya existente (`TypeInference.cs`), no necesita mecanismo nuevo. |
| 9 | Emisión | Cero cambios de runtime. Se compila como una función de módulo ordinaria (`SurtrModuleBuilder.Method`, nunca `SurtrClassBuilder.AddMethod`), con su **nombre literal** (sin prefijo sintético `$`) — el receptor se inserta como primer argumento posicional en el binder | Confirmado en profundidad (ver Investigación, apartado D): no hay tabla de runtime que busque el método por convención de nombre; `SignatureSet` ya trata el primer parámetro como parte normal de la clave de colisión salvo para operadores (que no aplica aquí). |
| 10 | Atributos sobre el propio bloque `extension` | Fuera de alcance para la primera versión — se puede añadir después con un nuevo bit en `SurtrAttributeTargets` (hoy no existe ni siquiera `Module`) | No bloquea nada; los miembros *dentro* del bloque sí pueden llevar atributos normales (`Method`/`Property`) sin cambios. |

---

## Investigación: hallazgos por área

### A. Parser / AST

- **No hay un nodo base `MemberDeclarationSyntax`/`TypeDeclarationSyntax` separado de las hojas.**
  Hay un único `TypeDeclarationSyntax` (`Syntax/Ast/DeclarationSyntax.cs:270`) que cubre las
  cinco formas de tipo (`Class`/`ValueClass`/`Interface`/`Enum`/`Singleton` vía
  `TypeDeclarationKind`, línea 61), justificado explícitamente porque "comparten forma —
  modificadores, nombre, parámetros de tipo, lista base, miembros — y difieren solo en las
  reglas que una fase posterior impone". Un bloque `extension` **no comparte esa forma** (no
  tiene nombre propio sino un tipo objetivo, no tiene lista base, no tiene constructor) — se
  recomienda un nodo dedicado, `ExtensionDeclarationSyntax`, en vez de forzarlo dentro de
  `TypeDeclarationSyntax`.
- **`ParseDeclaration()` (`Parser.Declarations.cs:28`) es el único punto de entrada**, reentrante
  y agnóstico de contexto: la misma función produce declaraciones de nivel de módulo
  (`Parser.cs:135`, dentro de `ParseCompilationUnit`) y los miembros del cuerpo `{ }` de
  cualquier tipo (`Parser.Declarations.cs:397-401`, un `while` que llama `ParseDeclaration()` en
  bucle). **Esto significa que "declarable a cualquier nivel" (módulo o anidado dentro de una
  clase) sale gratis** en cuanto `extension` se añade como un caso más al `switch` de
  `ParseDeclaration` — no hace falta ningún parser nuevo para la anidación.
- El cuerpo de miembros de `extension { }` reutiliza el mismo bucle `while (!RightBrace && !EOF)
  { members.Add(ParseDeclaration()); }` sin ninguna modificación — sintácticamente acepta
  cualquier declaración (`fun`, propiedad, `let`/`var`, `constructor`, `static { }`, tipos
  anidados); las restricciones semánticas (rechazar `constructor`/`static`/campos) se aplican en
  el **binder**, no en el parser — coherente con el principio de "una construcción nueva no
  está terminada cuando parsea, sino cuando algo la pide" que ya documenta `CLAUDE.md`.
- Precedente de palabra clave reservada nueva: `singleton` se añadió directamente al
  `switch (reader.CurrentType)` de reservadas de `ParseDeclaration` (línea 58-81) y a
  `Lexer.KeywordOrIdentifier` (`Syntax/Lexer.cs:699-778`) — el camino que seguiría `extension`.
- Todos los enums de modificador (`Visibility`, `DispatchModifier`, `InlineModifier`) y la
  struct interna `Modifiers` de `ParseModifiers()` (`Parser.Declarations.cs:15-25, 129-281`) son
  compartidos entre tipos y miembros; un `ParseExtensionDeclaration` recibiría el mismo
  `Modifiers modifiers` ya parseado y decidiría qué subconjunto es legal.
- Grep exhaustivo del repo: `extension` no aparece hoy en ningún token, palabra reservada,
  contextual, ni símbolo de prueba — completamente libre (solo en el sentido de extensión de
  fichero y de "sign extension" en manipulación de bits, ambos desambiguados por contexto).

### B. Binder — scopes, `MemberLookup`, resolución de llamadas

- **La resolución de miembros y de funciones no pasa por `Scope.Lookup`.** `Scope` (locals,
  alias de módulo) solo cubre identificadores locales/parámetros y un puñado de conceptos
  aparte (`TryDeclareModuleAlias`/`LookupModuleAlias`, `Scope.cs:155-181`, el precedente exacto
  de "diccionario paralelo para un concepto nuevo" que se recomienda replicar para extensiones).
  Miembros de tipo y funciones de módulo se resuelven con **recorridos explícitos de listas**
  (`MemberLookup.Reachable`, `ModuleSymbol.Methods`, `DeclaresMethod`/`AddModuleMethods` en
  `BodyBinder`) — así que un extension method necesita su propio índice explícito, no una
  entrada de `Scope`.
- **`MemberLookup.cs`**: `FindMethods(TypeSymbol, string)` (línea 66) y el motor común
  `Reachable(TypeSymbol)` (154-200) hacen BFS sobre `type.Members` + `BaseType` + `Interfaces`.
  `BackingType(TypeSymbol)` (207-232) es lo que traduce un tipo compuesto (`int[]`, `dict`) a la
  clase built-in parametrizada real (`array<int>`, `dict<K,V>`) antes de walkear — el mismo
  mecanismo que necesitará `extension int[] { }` para saber contra qué construcción concreta
  compara el tipo objetivo declarado. **Recomendación: no meter la búsqueda de extensiones
  dentro de `Reachable`** (mezclaría prioridades distintas — jerarquía real vs. fallback) sino
  un método paralelo, p. ej. `FindExtensionMethods(TypeSymbol, string, ModuleSymbol useSiteModule,
  IReadOnlyList<ModuleSymbol> imported)`, invocado solo cuando `FindMethods` da cero candidatos.
- **Puntos de inserción exactos** en `BodyBinder.Expressions.cs`:
  - `BindCall` (1216-1336): entre `_lookup.FindMethods(owner, name)` (1315) y `ClosureValue`
    (1320), o justo después de `ClosureValue` y antes de la caída a funciones de módulo
    (1323-1330) — ahí se probarían los candidatos de extensión, construyendo el
    `BoundCallExpression` exactamente como `BindOperatorCall` (941-946): receptor convertido con
    `Convert(receiver, method.Parameters[0].Type, ...)` como `arguments[0]`, resto de argumentos
    desplazados, `Receiver: null`, `isVirtual: false`.
  - `BindInstanceMember` (604-642, acceso sin llamada — closures/method groups): justo antes del
    `return Error(...)` final (línea 635).
  - `BindStaticMember` (660-686, `Type.miembro`): antes del `Error` final (682), para los
    estáticos de extensión (`Type.ext()`).
  - `BindIdentifier` (126-169): entre el paso 4 (miembros de módulo, línea 159-160) y el paso 5
    (grupo de métodos, 165-166) — fallback opcional para `ext()` suelto con `this` implícito.
- **`OverloadResolution.cs`**: `Resolve` (línea 120) es homogéneo — todos los candidatos son
  `MethodSymbol` planos, sin ningún concepto de "tier"/prioridad entre ellos (confirmado:
  `Candidate` en 355-372 no tiene ningún campo de prioridad). Esto **fuerza** el diseño de dos
  pasadas: primero `Resolve` sobre miembros reales; solo si `NoCandidates`, una segunda llamada
  a `Resolve` sobre el conjunto combinado de candidatos de extensión (de todos los bloques
  visibles a la vez, para que la regla de ambigüedad del punto 7 de la tabla de decisiones
  funcione con la maquinaria ya existente sin tocar `Candidate`/`IsBetter`).
- **`MethodSymbol`** (`Binding/Symbols/MemberSymbols.cs:50-202`) ya tiene el precedente exacto
  para "función sin tipo declarante": `ContainingType => ContainingSymbol as NamedTypeSymbol`
  (línea 76) da `null` cuando `ContainingSymbol` es un `ModuleSymbol`. La clase es `sealed`, así
  que el diseño recomendado es **añadir un campo nuevo** (p. ej. `NamedTypeSymbol?
  ExtensionTargetType`) en vez de subclasificar — coherente con la regla de `CLAUDE.md`: "keep
  new axes as fields unless they genuinely add state". `MethodRole` (28-47) gana opcionalmente
  un valor `Extension` si conviene distinguirlo de `Normal` en otros puntos del pipeline.
- **`ModuleSymbol`** (`ModuleSymbol.cs:17-114`) necesita un índice nuevo, construido en la fase
  de declaración: `Dictionary<NamedTypeSymbol, List<MethodSymbol>>` (clave = `BackingType` del
  tipo objetivo del bloque `extension`), análogo al patrón `_byName` que ya usa `FindTypes`
  (líneas 54-81) para tipos — así la búsqueda por tipo destino es O(1), no un recorrido lineal
  de todo el módulo por cada `obj.metodo()` que no resuelva.
- **`AccessCheck.IsAccessible`** (`AccessCheck.cs:30-109`) se reutiliza **sin ningún cambio** si
  el `MethodSymbol` sintético de una extensión tiene `ContainingSymbol` apuntando al
  `ModuleSymbol` que declara el bloque `extension` (exactamente como una función de módulo
  suelta hoy) — cae directamente en la rama `ModuleSymbol declaringModule → SameModule(...)`. La
  "visibilidad de scope" (¿está el módulo declarante importado desde el punto de uso?) **no la
  cubre `AccessCheck`** — se resuelve, como ya ocurre hoy para funciones de módulo, construyendo
  el conjunto de candidatos solo a partir de `_module` + `_imported` en el punto de la llamada
  (Fase 2 de este plan).

### C. Codegen / runtime

- **Confirmado con el precedente ya en producción**: `BindOperatorCall`
  (`BodyBinder.Expressions.cs:931-962`), rama de operador estático, ya construye exactamente
  `new BoundCallExpression(syntax, null, method, arguments, isVirtual: false)` con el receptor
  como `arguments[0]` — el mismo patrón que necesita `extension`, ya probado end-to-end.
- **`MethodBodyEmitter.EmitCall`** (`MethodBodyEmitter.cs:2931-2980`) no distingue "llamada de
  instancia" de "llamada a función de módulo" por tipo de nodo, sino por si `call.Receiver` es
  `null`. Si es `null`, nunca invoca `BoxReceiverForCall` — evalúa `call.Arguments` en orden y
  llama a `EmitResolvedCall`, que resuelve el opcode únicamente por `method.ContainingType is
  null` → `CallLocalModule`/`CallExternal` (2992-3034). **Cero opcodes nuevos, cero cambios en
  el emisor.**
- **`SurtrCodeEmitter.Call`/`EmitCall`** (`Bytecode/Emit/SurtrCodeEmitter.Helpers.cs:606-717`)
  confirma lo mismo un nivel más abajo: `moduleLevel = callee.DeclaringType is null` es la
  primera rama, sin ninguna relación con de dónde vino sintácticamente la llamada.
- **`Conversions.cs`/`OverloadResolution.TryBuild`** no tienen ningún concepto de "argumento 0 =
  receptor" — operan sobre `IReadOnlyList<ArgumentInfo>` frente a `method.Parameters` sin
  distinción posicional especial. La conversión del receptor al tipo del primer parámetro de la
  función de extensión usa exactamente `Conversions.Classify` como cualquier argumento — sin
  boxing especial: si el receptor es una `value class` o un primitivo, se comporta igual que
  pasarlo a cualquier función normal (boxing solo si el parámetro es `unknown`/genérico erasure,
  regla ya existente).
- **`SurtrClass.cs`**: las tablas (`_fields`, `_properties`, `_methods`, `VirtualMethods`,
  `DirectMethods`, `StaticMethods`) solo se pueblan vía `AddMethod`/`AddField` con
  `ThrowIfBuilt()` — un método declarado como función de módulo **nunca pasa por esa API**, así
  que no hay ningún punto de fuga posible.
- **`SurtrReflectionBuiltIns.cs` `TypeMembers`** (123-191) itera exclusivamente
  `cls.Fields`/`Properties`/`Methods` de la clase, nunca los métodos de un `SurtrModule` —
  confirmado que `Type.of(receptor).members()` **nunca** mostrará un extension method, sin
  necesitar ningún filtro adicional.
- **`SignatureSet.Add`** (`SignatureSet.cs:63-92`) ya excluye el primer parámetro de la clave de
  colisión solo para `MethodRole.Operator` de instancia — un extension method (no operador)
  incluye su receptor como parámetro 0 normal en la clave, así que dos extensiones
  `push(arr: int[], x)` y `push(d: dict, x)` son, correctamente, sobrecargas distintas.
- **`SyntheticNames.cs`**: no hace falta ningún prefijo `$` — el nombre literal escrito por el
  usuario es la opción correcta, tanto porque no hay tabla de runtime que lo busque por
  convención como porque un prefijo sintético activamente estorbaría una eventual llamada
  explícita `Modulo.push(xs, 3)`.

### D. Especificación (`docs/Language-Syntax.md`)

- **§1.2**: exactamente 4 palabras contextuales hoy (`this`, `super`, `value`, `attribute`),
  "recognized by what follows them" — el precedente metodológico existe, aunque se recomienda
  reservada para `extension` (ver decisión #2). La palabra está completamente libre.
- **§2.8 (`singleton`)** es el precedente metodológico más relevante para justificar `extension`
  como construcción nueva: la spec explica por qué existe citando qué hace que ninguna
  construcción existente pueda hacer — *"A module cannot implement an interface and cannot be
  passed to a function expecting one... that gap, not organisation, is what this declaration is
  for."* La justificación paralela: hoy no existe ningún mecanismo para añadir miembros a un
  tipo ya declarado (de usuario o built-in) sin editar su declaración original — ese es el hueco
  que `extension` cierra.
- **§6 (genéricos)**: invarianza estricta, sin variance de sitio de declaración (`out`/`in`
  diferido en §14.4). Regla del "static-nested": *"A nested type does not see its container's
  type parameters"* — la razón por la que `extension Array<T> { }` debe declarar su propio `<T>`
  en vez de heredar el del tipo objetivo. La relación de ese `T` con el `G<n>` real de erasure de
  `Array` (CLAUDE.md, "Generics are erased") **no aplica directamente** — el `T` de un bloque
  `extension` nunca se convierte en el `G0` real de la clase, porque el método de extensión no es
  miembro declarado por esa clase; es un parámetro de tipo ordinario de una función de módulo
  sintética, inferido en el call site como cualquier método genérico normal.
- **§2.9 (`value class`)**: erased al único campo que envuelve, boxed solo al fluir a un slot
  erasure/interfaz. Sin caso de dispatch previo aplicable a un receptor de extensión (nunca
  `Direct` propio ni "reached via interface", porque nunca es miembro real) — se resuelve tratando
  el receptor siempre como parámetro ordinario, sin boxing salvo el que ya aplicaría a cualquier
  argumento (ver apartado C).
- **`docs/Compiler-Plan.md` §10.2** y **`Language-Syntax.md` §14.4**: ninguno menciona
  "extension methods" — confirmado que es un mecanismo completamente nuevo, sin borrador previo.
- **§11 (atributos)**: `SurtrAttributeTargets` (`[Flags]`: `Class, Interface, Enum, Field,
  Property, Method`) no tiene hoy ni `Module` ni nada análogo a un bloque contenedor de nivel
  superior — coherente con dejar "atributos sobre `extension`" fuera de la primera versión
  (decisión #10).

### E. Language Server y cliente VSCode

- **`Workspace.Rebuild()`** (`Workspace.cs:85-87`) llama literalmente al pipeline real del
  compilador (`SurtrCompilation.Create` → `Binder.Bind()` → `BindBodies()`) — todo lo que el
  binder modele como símbolos normales, el LSP lo hereda **gratis** en la mayoría de sitios.
- **`CompletionProvider.CompleteMember`** (75-151) no reimplementa member lookup: llama
  directamente a `binder.MemberLookup.Reachable(receiver)` (dentro de `AddReachableMembers`,
  1096-1122). Como se decidió **no** meter extensiones dentro de `MemberLookup.Reachable` (para
  no romper la semántica de prioridad — ver apartado B), `AddReachableMembers` necesita un
  cambio explícito y localizado: además de `Reachable(receiver)`, invocar el nuevo método
  paralelo del compilador (`binder.MemberLookup.FindExtensionMethods`) y fusionar los resultados
  para el completado tras `.`. Mismo ajuste en `ResolveCallableByName` (454-513, usado por
  `textDocument/signatureHelp`).
- **`Keywords`** (`CompletionProvider.cs:273-281`): array plano, sin distinguir reservadas de
  contextuales — `extension` se añade ahí sin lógica extra. Ya hay historial de que esta lista
  se desincroniza (dos bugs reales corregidos en fases anteriores).
- **`SymbolResolver.FindMethodDeclaration`/`MatchesParent`** (1231-1249, 1328-1338) asumen que
  todo método vive dentro de un `TypeDeclarationSyntax` — falso para un método de un bloque
  `extension`, cuya declaración léxica vive en el nuevo `ExtensionDeclarationSyntax`. Para que
  "ir a definición" salte al sitio correcto:
  - Nuevo caso en `AllDeclarations` (1341-1353) que recorra los miembros de un
    `ExtensionDeclarationSyntax` igual que hoy recorre los de un `TypeDeclarationSyntax`.
  - Ajuste de `MatchesParent` (o de la comparación equivalente) para reconocer ese nuevo tipo de
    "padre" — `method.ContainingType` sigue apuntando al **tipo receptor** (para que
    `MemberLookup`/overload resolution lo traten como si fuera suyo), pero el LSP necesita además
    saber en qué archivo/rango de sintaxis vive realmente, vía el campo nuevo del punto B
    (`ExtensionTargetType` o un campo hermano tipo `DeclaringExtension`).
  - La pasada 1 de hover (`BoundHit`, resuelve por nodo ligado) **funciona sin cambios** siempre
    que el bound tree produzca un `BoundCallExpression` con `call.Method` apuntando a un
    `MethodSymbol` real — que es exactamente el diseño de la Fase 1.
- **Semantic tokens**: el legend actual es de **un solo tipo** (`{"keyword"}`,
  `SemanticTokensProvider.cs:31`), usado únicamente para resolver keywords **contextuales** que
  una regex no puede (`this`/`super`/`value`/`attribute`/`get`/`set` por posición). Con la
  decisión #2 (`extension` reservada), **este archivo no necesita ningún cambio** — la gramática
  TextMate la resuelve sola sin ambigüedad, igual que `class`/`singleton` hoy.
- **`vscode-surtr/syntaxes/surtr.tmLanguage.json`**: precedente directo para una reservada nueva
  — añadir `extension` a la alternancia `\\b(?:class|interface|enum|singleton)\\b` (línea 341) y
  un bloque `extension-declaration` análogo a `type-declaration` (148-161), capturando el tipo
  objetivo como `support.type.surtr` (no `entity.name.type.surtr`, porque no se está declarando
  un tipo nuevo sino nombrando uno existente). Los miembros dentro del bloque no necesitan
  reglas nuevas (`function-declaration`/`property-declaration` ya no dependen de estar dentro de
  un `class`).
- **Cuatro sitios a mantener sincronizados** con la lista de reservadas/contextuales (aparte de
  `Language-Syntax.md` §1.2): `Lexer.KeywordOrIdentifier` (`Syntax/Lexer.cs:699-778`),
  `Parser.Declarations.cs` (si fuera contextual), `CompletionProvider.Keywords`, y el
  repositorio `keywords` de `surtr.tmLanguage.json` — ya hay historial de bugs reales por
  desincronización entre estos cuatro.
- **Versionado de `vscode-surtr`**: patrón confirmado — commit de *feature* (fuente) separado
  del commit de *release* (solo bump de `package.json` + regeneración del `.vsix`, sin
  `CHANGELOG.md` — el mensaje del commit de release hace de changelog). Versión actual `0.1.4`.

---

## Fases de implementación

### Fase 0 — Especificación y decisiones de diseño — **Hecha**

**Confirmado con el usuario** (las cuatro decisiones que eran una elección real, no una
consecuencia técnica forzada — el resto de la tabla se adoptó tal cual estaba propuesto):
- #2 — **reservada**, no contextual.
- #4 — anidar `extension` dentro de una clase afecta **solo a visibilidad**, sin doble
  receptor estilo Kotlin.
- #6 — un miembro real del tipo **siempre gana en silencio** sobre una extensión con el mismo
  nombre/firma aplicable — sin warning, sin error.
- #7 — dos extensiones igualmente aplicables desde distintos imports son **ambigüedad** (mismo
  diagnóstico que cualquier otro empate de `OverloadResolution`), sin regla nueva de
  prioridad de import.

**Cambios**: nueva sección `docs/Language-Syntax.md` §15 "Extensions" (§15.1-§15.5: qué puede
declarar un bloque `extension`, dónde se declara y su visibilidad, orden de resolución/
prioridad/ambigüedad, extensiones genéricas, y qué emite) con la sintaxis y semántica completas
según las diez decisiones de este documento; `extension` añadida a la tabla de palabras
reservadas de §1.2. No se ha tocado ningún código todavía — esta fase es puramente de
especificación, para que las fases siguientes implementen contra un documento estable.

### Fase 1 — Núcleo: extension methods de instancia sobre tipos no genéricos, mismo módulo — **Hecha**

Alcance end-to-end implementado tal como estaba planeado, sin imports (candidatos limitados al
propio módulo — Fase 2) y sin genéricos (Fase 6).

**Hallazgo de diseño real, que corrige el borrador de la Fase 0**: el receptor **no puede** ser
un `this` implícito estilo Kotlin (`fun length(): float => Math.sqrt(x*x+y*y);`, como mostraba el
primer borrador de §15). Investigando `BodyBinder.BindThis` (`BodyBinder.Expressions.cs:502`) y
`BindMethodGroupLambda` (`BodyBinder.Expressions.cs:281-285`) se encontró que el binder asume en
varios sitios independientes que **`!method.IsStatic` implica `ContainingSymbol is
NamedTypeSymbol`** — exactamente la condición que un método de extensión rompe a propósito (es
`IsStatic = true`, `ContainingSymbol = ModuleSymbol`, para caer en el camino `CallLocalModule` sin
tocar el runtime). Forzar `IsStatic = false` para habilitar `this` implícito habría roto ese
invariante en múltiples puntos no relacionados entre sí (cast inválido a `NamedTypeSymbol`,
`MethodCandidatesForBareName`, etc.) — el tipo de cambio invasivo que la fase 0 quería evitar.
**Solución adoptada**: el receptor es un **parámetro explícito y corriente**, con el mismo nombre
que cualquier otro parámetro (`fun length(self: Vec2): float => ...`) — exactamente el modelo
clásico de C# (`this Vec2 v`), salvo que aquí no hace falta ningún modificador `this` en el
parámetro porque el propio bloque `extension Vec2 { }` ya deja claro cuál es el receptor. Cero
maquinaria nueva en `BodyBinder` para `this`/acceso implícito a miembros — el cuerpo de una
extensión es, para el binder, un módulo-level `fun` corriente. `docs/Language-Syntax.md` §15 se
actualizó con este modelo (ejemplos y §15.1/§15.4 reescritos).

**Cambios**:
- Parser: `extension` como palabra reservada (`TokenType.KeywordExtension`,
  `Lexer.KeywordOrIdentifier` case 9); nuevo `ExtensionDeclarationSyntax`
  (`Syntax/Ast/DeclarationSyntax.cs`) con `TargetType: TypeSyntax`, `Visibility`,
  `Members: IReadOnlyList<DeclarationSyntax>`; `ParseExtensionDeclaration`
  (`Parser.Declarations.cs`) reutiliza el bucle de miembros de `ParseTypeDeclaration` sin
  modificarlo y rechaza en el parser cualquier modificador que no sea visibilidad
  (`static`/`sealed`/`virtual`/etc., mismo patrón que `ParseOperator`); declarable a nivel de
  módulo o anidado dentro de una clase, gratis por la reentrancia de `ParseDeclaration`.
- Binder — fase de miembros (`Binder.BindExtension`, llamado desde `BindModuleMembers` a nivel de
  módulo y desde `BindMembers` para el caso anidado): resuelve `TargetType` vía el `TypeResolver`
  normal; rechaza (con los cuatro diagnósticos nuevos) un objetivo que no sea un `NamedTypeSymbol`
  plano (compuestos/built-ins parametrizados, Fase 5), cualquier miembro que no sea un `fun` de
  instancia concreto, no genérico, no nativo (constructor/`static {}`/campo/propiedad/estático,
  Fases 3-4), y un método cuyo primer parámetro no sea exactamente el tipo objetivo. Cada método
  válido se crea como `MethodSymbol` con `ContainingSymbol = module` (igual que una función de
  módulo) más los dos campos nuevos `ExtensionTargetType`/`ExtensionDeclaringContainer`; la
  visibilidad efectiva del miembro sale de `ResolveExtensionMemberAccessibility` (mismo patrón que
  `ResolveAccessorAccessibility`, pero permitiendo igual-o-más-estrecha en vez de estrictamente más
  estrecha). Acumulados en `_extensionMethodsByModule` y volcados a la nueva
  `ModuleSymbol.ExtensionMethods` (lista separada de `.Methods`, para que la resolución de nombre
  suelto nunca los vea) en un paso final de `MemberPhase`.
- Binder — fase de cuerpos (`BodyBinder.Expressions.cs`): `ExtensionCandidates`/
  `IsExtensionAccessible`/`CompleteExtension`, enganchados en `BindCall` entre el chequeo de
  `ClosureValue` y la caída a funciones de módulo, activos solo cuando hay receptor (`obj.foo()`
  explícito o `this` implícito dentro de un método de instancia — nunca una llamada de módulo sin
  receptor). `CompleteExtension` es un primo de `Complete` en vez de una llamada a él: `Complete`
  siempre re-liga `syntax.Arguments` desde cero, y el receptor aquí ya está ligado — pasarlo tal
  cual (en vez de devolver su sintaxis para una segunda ligadura) es lo que evita evaluarlo dos
  veces si tiene efecto (verificado con test dedicado). `AccessCheck.IsAccessibleWithin`
  (`AccessCheck.cs`) nuevo, para la visibilidad de un bloque anidado en una clase (reutiliza
  `SharesOutermostType`/`Inherits`/`SameModule`, ya privados en la misma clase).
- CodeGen (`ModuleEmitter.cs`): **un cambio real, no cero como se pensó inicialmente** —
  `DeclareModuleMembers`/`EmitModuleBodies` solo recorrían `module.Methods`, así que un método de
  extensión (en `module.ExtensionMethods`, deliberadamente aparte) nunca se declaraba ni se emitía
  y cualquier llamada a él fallaba en `ModuleEmitter` con `SURTR4001` ("neither being emitted here
  nor already built"). Corregido añadiendo el mismo recorrido para `module.ExtensionMethods` en
  ambos métodos. El resto de la hipótesis de "cero cambios de runtime" se confirmó: nada se tocó
  en `SurtrClass`/`SurtrTypeLinker`/`SurtrCodeEmitter`/opcodes.
- Tests: `ModuleEmitterTests.cs`, región "Extension methods (§15) — Fase 1" (18 tests, todos en
  verde a la primera tras el fix de `ModuleEmitter`): método de instancia básico contra la VM
  real, argumentos extra tras el receptor, evaluación única del receptor cuando tiene efecto,
  colisión con miembro real (el real gana en silencio), dos extensiones sobre tipos distintos
  resolviendo independientemente (`Vec2`/`int`), extensión anidada en clase alcanzable desde sus
  propios miembros y no alcanzable desde fuera, miembro más estrecho que el bloque (aceptado) y
  más amplio (rechazado), objetivo compuesto rechazado, receptor ausente/incorrecto rechazado,
  campo/constructor dentro del bloque rechazados (estático dejó de estar rechazado en la Fase 3),
  modificador no-visibilidad en el bloque rechazado en el parser.
- Suite completa verificada: 2224/2228 en verde — los 4 fallos restantes son preexistentes al
  WIP de `Stack.surtr` (`docs/Plan-Stdlib.md`, "no tocar"), confirmado sin relación con `extension`
  (`Stack.surtr` no usa la palabra, y el error es sobre `ICollection.iterate`/una referencia de
  excepción, nada de esto tocado en esta fase).

**Commit sugerido**: `Feature: extension methods de instancia (Fase 1 de §15) — parser, binder y codegen`

### Fase 2 — Imports y visibilidad de scope — **Hecha**

**Cambios**:
- `BodyBinder.ExtensionCandidates` (`BodyBinder.Expressions.cs`) ahora recorre `_module` +
  cada `_imported` — la misma lista de módulos wildcard-importados que ya usa
  `MethodCandidatesForBareName`/`AddModuleMethods` para funciones de módulo sueltas — a través
  de un nuevo helper compartido `AddExtensionCandidates`. Confirmado en la investigación de esta
  fase que `_imported` solo contiene módulos importados por **wildcard** (`import X.*`), nunca
  por nombre único ni por lista selectiva (`import X.{Y, Z}`) — igual que ya pasa con las
  funciones de módulo, que tampoco llegan por esas dos formas — así que no hizo falta ninguna
  lógica nueva de resolución de imports: un método de extensión hereda exactamente el mismo
  alcance que ya tenía una función de módulo.
- Todos los candidatos (propios + de cada import) se pasan en **una sola** llamada a
  `OverloadResolution.Resolve` (no secuencial entre sí, como sí lo es el paso "miembro real
  primero, extensión como fallback") — dos extensiones igualmente aplicables desde dos imports
  distintos producen `Ambiguous` con el diagnóstico ya existente, sin tocar `Candidate`/
  `IsBetter`.
- `AccessCheck.IsAccessible`/`IsAccessibleWithin` (reusados sin cambios) ya distinguen
  correctamente declarante vs. punto de uso cuando ambos están en módulos distintos — un
  `internal` (el default) sigue sin cruzar el import, solo un `public` explícito lo hace.
- **Hallazgo real, no anticipado en la Fase 0/1**: `ModuleEmitter.Record` (`ModuleEmitter.cs`,
  la función que registra los métodos ya construidos de un módulo en `_builtMethods` para que
  módulos compilados **después** puedan referenciarlos) solo recorría `symbol.Methods` — un
  método de extensión llamado desde un módulo distinto al que lo declara fallaba en emisión con
  `SURTR4001` exactamente igual que había fallado en la Fase 1 antes de tocar
  `DeclareModuleMembers`/`EmitModuleBodies`. Corregido con el mismo patrón: un `foreach` más
  sobre `symbol.ExtensionMethods` junto al de `symbol.Methods`. Van ya **tres** sitios en
  `ModuleEmitter` que necesitaban este espejo (declarar, emitir cuerpo, registrar lo construido)
  — todos los demás, confirmados sin cambios.
- Tests: `ModuleEmitterTests.cs`, región "Extension methods (§15) — Fase 2" (4 tests) —
  extensión traída por wildcard import y ejecutada de punta a punta contra la VM real
  (verificando también el fix de `Record`), extensión declarada pero nunca importada (no es
  candidata), ambigüedad real entre dos imports que aportan la misma extensión (grafo de
  módulos sin ciclos: un módulo `shapes` compartido, importado por los dos módulos de
  extensión y por el que llama, para no disparar el error de ciclo de
  `ModuleDependencyGraph`), y un bloque `extension` sin visibilidad escrita (`internal` por
  defecto) no alcanzable desde el módulo que lo importa.
- Suite completa verificada: 2228/2232 en verde (mismos 4 fallos preexistentes del WIP de
  `Stack.surtr`, sin relación).

**Commit sugerido**: `Feature: extension methods via imports wildcard (Fase 2 de §15)`

### Fase 3 — Extension methods estáticos — **Hecha**

`extension Type { static fun foo(): void { ... } }` → invocable como `Type.foo()`.

**Cambios**:
- `MethodSymbol.ExtensionIsStatic` (`MemberSymbols.cs`) nuevo — necesario porque `IsStatic` en sí
  ya vale `true` para **cualquier** método de extensión, estático o no (es lo que evita el
  invariante roto de la Fase 1, `!IsStatic ⟹ ContainingSymbol is NamedTypeSymbol`); este campo
  aparte es lo que de verdad distingue "recibe el receptor como primer parámetro explícito" de
  "no recibe receptor en absoluto", y es lo que usa la resolución de llamada para elegir entre
  `ExtensionCandidates` (instancia) y `StaticExtensionCandidates` (estático).
- `Binder.BindExtension`: la comprobación de receptor (primer parámetro == tipo objetivo) ahora
  se salta por completo cuando el método fue declarado `static` en la sintaxis — un estático no
  necesita ningún parámetro especial, se liga exactamente como una función de módulo corriente.
  El resto de rechazos (genérico/nativo/const/dispatch/sealed/sin cuerpo) se mantienen igual para
  estáticos e instancias.
- `BodyBinder.Expressions.cs`: nuevo `StaticExtensionCandidates`/`AddStaticExtensionCandidates`,
  enganchado en la rama de `BindCall` que ya maneja `Type.miembro(...)` (`TryBindAsType` sobre el
  receptor) — solo se prueba cuando `_lookup.FindMethods(staticOwner, name)` da cero candidatos
  reales (mismo criterio de prioridad silenciosa que la Fase 1), y se completa con el `Complete`
  **ya existente** sin ningún truco de inserción de receptor: un estático no tiene receptor, así
  que se liga exactamente como una llamada a función de módulo. A diferencia del receptor de una
  instancia (emparejado por conversión de argumento, por tanto polimórfico vía jerarquía), el
  candidato estático se empareja por **identidad de referencia** contra
  `ExtensionTargetType` — `Type.miembro` nunca se hereda a través de un nombre de tipo, ni
  siquiera para un miembro estático real (§3.1), así que un estático de extensión no debería
  comportarse de otra forma.
- `AddExtensionCandidates` (instancia) gana el filtro `!method.ExtensionIsStatic`, para que un
  estático nunca aparezca como candidato de una llamada `obj.foo()`.
- Sin cambios en `ModuleEmitter` — los tres sitios ya arreglados en las Fases 1-2
  (`DeclareModuleMembers`/`EmitModuleBodies`/`Record`) recorren `module.ExtensionMethods` sin
  distinguir estático de instancia, así que un estático de extensión ya se declaraba/emitía/
  registraba correctamente en cuanto el binder lo produjo.
- Test de Fase 1 obsoleto (`AStaticMethodInsideAnExtensionBlockIsRejectedForNow`, que esperaba
  el rechazo que esta fase retira) eliminado.
- Tests: `ModuleEmitterTests.cs`, región "Extension methods (§15) — Fase 3" (5 tests) —
  estático básico contra la VM real, estático con argumentos ordinarios, colisión con un
  estático real del tipo (el real gana en silencio), estático traído por import wildcard,
  estático `internal` (default) no alcanzable desde un módulo que importa.
- Suite completa verificada: 2232/2236 en verde (mismos 4 fallos preexistentes del WIP de
  `Stack.surtr`, sin relación).

**Commit sugerido**: `Feature: extension methods estaticos (Fase 3 de §15)`

### Fase 4 — Extension properties — **Hecha**

**Hallazgo de diseño real, análogo al de la Fase 1**: una propiedad no tiene lista de
parámetros donde escribir el receptor explícito que un método de extensión sí puede escribir
(`fun length(self: Vec2): float`) — no hay sitio en `x: float { get; set; }` para nombrar nada.
Investigado a fondo antes de implementar: la solución NO es dar a las propiedades su propia
sintaxis de parámetro (habría exigido tocar `PropertyDeclarationSyntax`/`ParseProperty`, un
cambio de gramática real). En su lugar, el receptor de una propiedad de extensión de instancia
es un parámetro **sintetizado** (`SyntheticNames.ExtensionReceiver`), alcanzable desde el
cuerpo del accessor solo a través de `this` — y ese mismo mecanismo (`BodyBinder.ExtensionReceiver`,
derivado de `_method.ExtensionTargetType`/`ExtensionIsStatic`, no de un parámetro nuevo del
constructor de `BodyBinder`) resulta que **también sirve para los métodos de extensión de la
Fase 1**: `this` dentro de un método de extensión ahora resuelve al mismo parámetro que el
usuario nombró explícitamente, como alias añadido sin coste — la Fase 1 sigue exigiendo el
parámetro explícito, `this` es una forma adicional de leerlo, no un reemplazo.

**Segundo hallazgo real, en el emisor**: `SurtrModuleBuilder.DefineProperty`/`DefineGetter`/
`DefineSetter` (usado para toda propiedad ordinaria, de módulo o de clase) asume siempre que un
getter no declara parámetros y un setter exactamente uno (`value`) — el receptor, cuando existe,
es siempre implícito. Una propiedad de extensión rompe esa asunción a propósito (su getter
declara el receptor como parámetro real), así que sus accessors **no pasan por esa API en
absoluto** — se declaran/emiten como funciones de módulo corrientes, exactamente igual que un
método de extensión (`ModuleEmitter.DeclareExtensionFunction`, extraído como el mismo helper que
ya usaba `module.ExtensionMethods`, reutilizado también para cada accessor de
`module.ExtensionProperties`). Confirmado además que la inserción del receptor en la pila
(`EmitPropertyRead`/`Store` en `MethodBodyEmitter.cs`) ya hace exactamente lo necesario **sin
ningún cambio**, siempre que `PropertySymbol.IsStatic` reflete la verdad de la fuente (no,
como en `MethodSymbol.IsStatic`, forzado siempre a `true`) — la única pieza nueva ahí fueron dos
guardas (`TryInlinePropertyGetter`/`TryInlinePropertySetter`) para que la optimización de
splicing existente nunca intente hacer inline de un accessor de extensión, cuyo receptor no
encaja en la forma que esa optimización asume.

**Cambios**:
- `MethodSymbol.IsStatic` se mantiene siempre `true` en los accessors de extensión (igual que en
  un método de extensión, para no romper el invariante de `BindThis`), pero
  `PropertySymbol.IsStatic` guarda la verdad de la sintaxis — son dos campos independientes que
  para una propiedad ordinaria siempre coincidían y aquí se desincronizan a propósito.
  `PropertySymbol` gana `ExtensionTargetType`/`ExtensionDeclaringContainer` (igual que
  `MethodSymbol`, para el emparejamiento estático por identidad y la visibilidad anidada).
- `Binder.BindExtensionProperty`/`BindExtensionAccessor`: solo computadas (`get`/`set` con
  cuerpo o `=>`), nunca auto-property (diagnóstico nuevo si no hay accessors o si alguno no
  tiene cuerpo); estática o de instancia, con la misma regla de visibilidad
  bloque-miembro que un método; el receptor de una de instancia es
  `SyntheticNames.ExtensionReceiver` como `Parameters[0]` del getter (y del setter, antes de
  `value`), ausente por completo en una estática.
- `BodyBinder.ExtensionReceiver` (nuevo, computado desde `_method` sin ningún parámetro nuevo en
  el constructor) + `BindThis` actualizado: `this` dentro de un método o accessor de extensión de
  instancia resuelve a ese parámetro; `super` se rechaza (una extensión no tiene base).
- `BodyBinder.Expressions.cs`: `InstanceExtensionProperty`/`StaticExtensionProperty` (candidatos
  desde `_module` + `_imported`, emparejamiento por conversión para la de instancia —
  polimórfico igual que el receptor de un método — y por identidad de referencia para la
  estática, igual que `StaticExtensionCandidates`); `PickExtensionProperty` reporta ambigüedad
  si hay más de un candidato (las propiedades no tienen argumentos con los que
  `OverloadResolution` pudiera desempatar). Enganchado en `BindInstanceMember`/`BindStaticMember`
  justo antes del `Error` final, tras fallar campo/propiedad/method-group real — misma prioridad
  silenciosa que un método. La escritura (`obj.prop = valor`) no necesitó ningún cambio en
  `BindAssignment`: como la lectura ya produce un `BoundPropertyExpression` corriente, toda su
  maquinaria de asignación (accesibilidad del setter, `IsAssignable`) funciona sin tocarla.
- `ModuleEmitter.cs`: `DeclareExtensionFunction` extraído del cuerpo del bucle de
  `module.ExtensionMethods` y reutilizado para cada accessor de `module.ExtensionProperties`;
  mismo añadido en `EmitModuleBodies` y `Record` que ya hizo falta para los métodos en las Fases
  1-2, esta vez para los accessors.
- `MethodBodyEmitter.cs`: guarda nueva en `TryInlinePropertyGetter`/`TryInlinePropertySetter`
  (`if (accessor.ExtensionTargetType is not null) return false;`) para que el splicing de
  `inline`/`forceinline` nunca se intente sobre un accessor de extensión.
- Tests: `ModuleEmitterTests.cs`, región "Extension methods (§15) — Fase 4" (11 tests) —
  propiedad de solo lectura (`=>`) contra la VM real, `get`/`set` explícitos con escritura real
  sobre un campo mutable del receptor, colisión con propiedad real (la real gana en silencio),
  propiedad estática, `this` dentro de un método de extensión (confirma que sigue exigiendo el
  parámetro explícito y que `this` es solo un alias), propiedad anidada en clase alcanzable
  desde sus propios miembros, propiedad traída por import wildcard, ambigüedad real entre dos
  imports, auto-property sin accessors rechazada, accessor sin cuerpo rechazado.
- Suite completa verificada: 2246/2246 en verde.

**Commit sugerido**: `Feature: extension properties (Fase 4 de §15)`

### Fase 5 — Extensiones sobre tipos compuestos y built-ins

- `extension int[] { }`, `extension Dictionary<K, V> { }` (o el tipo dict real), `extension
  string { }`, extensiones sobre una `value class` de usuario.
- Resolución del tipo objetivo declarado a través de `MemberLookup.BackingType` (misma
  indirección que hace que `int[].push(3)` funcione hoy) — necesario tanto para saber contra qué
  construcción concreta (`array<int>` vs. `array<string>`) compara el receptor real, como para
  evitar que `extension int[] { }` y `extension string[] { }` colisionen entre sí.
- Verificar (con test, no solo lectura de código) que el boxing de un receptor `value class`/
  primitivo sigue las reglas normales de conversión de argumento — sin caso especial nuevo.
- Tests: extensión sobre `int[]`, sobre un `dict`, sobre una `value class` de usuario, sobre
  `string`.

### Fase 6 — Extensiones genéricas

- `extension Array<T> { fun sum(self: Array<T>): T }`, `extension List<T> { }` sobre un tipo
  genérico de usuario.
- El bloque `extension` declara su propia lista `<T>` (regla "static-nested" de §6, no ve los
  parámetros del tipo objetivo); inferencia en el call site contra el argumento de tipo real
  del receptor, reutilizando `TypeInference.cs` (mismo mecanismo que ya usa un método genérico
  normal) — sin mecanismo nuevo de inferencia.
- Tests: extensión genérica sobre `Array<T>`, sobre una clase de usuario `Box<T>`, con
  constraint (`<T : IComparable<T>>`).

### Fase 7 — Language Server

- `CompletionProvider.Keywords`: añadir `extension`.
- `MemberLookup` (compilador): nuevo método público `FindExtensionMethods`, reutilizado tanto
  por `BodyBinder` (Fases 1-2) como por el LSP.
- `CompletionProvider.AddReachableMembers`/`ResolveCallableByName`: invocar el método nuevo de
  `MemberLookup` y fusionar candidatos de extensión con los reachable normales, para completado
  tras `.` y `signatureHelp`.
- `SymbolResolver.AllDeclarations`: nuevo caso para recorrer miembros de un
  `ExtensionDeclarationSyntax`; ajuste de la comparación de "padre" (`MatchesParent` o
  equivalente) para reconocerlo — ir-a-definición debe saltar al sitio real dentro del bloque
  `extension`, no fallar ni apuntar al tipo receptor.
- `HoverFormatter`: distinguir en el texto de hover que un miembro viene de una extensión
  (p. ej. "extension method sobre `Tipo`, declarado en `Modulo`") — cosmético pero evita
  confusión.
- Semantic tokens: **sin cambios** (confirmado — solo aplica a contextuales).
- Tests: `LanguageServerWorkspaceTests.cs` — completado tras `.` incluyendo un extension method
  visible por import wildcard, hover y definición sobre una llamada a extensión, `extension` en
  el completado suelto de keywords.

### Fase 8 — Cliente VSCode

- `syntaxes/surtr.tmLanguage.json`: añadir `extension` a la alternancia de reservadas de tipo;
  nuevo bloque `extension-declaration` (análogo a `type-declaration`), capturando el tipo
  objetivo como `support.type.surtr`.
- `snippets/surtr.code-snippets`: snippet nuevo (`extension`), análogo a `class`/`interface`/
  `singleton`/`vclass` ya existentes.
- Commit de *feature* separado del commit de *release* (bump de `package.json`, regeneración del
  `.vsix`), siguiendo el patrón ya establecido en el historial del proyecto.

### Fase 9 (opcional, diferida) — Extension members con doble receptor estilo Kotlin

Un bloque `extension` declarado como miembro de una clase que, además del receptor de
extensión, también tiene acceso implícito al `this` de la clase contenedora (Kotlin: "member
extension functions"). **Se recomienda no implementar** salvo demanda real — interactúa con
captura, dispatch virtual y boxing de forma no trivial, y ninguna de las fases anteriores lo
necesita. Documentar como "considerada y diferida deliberadamente" en `Language-Syntax.md`
§14.4, con la misma justificación que ya usa esa sección para otras features pospuestas.

---

## Orden de trabajo sugerido

1. Fase 0 — cerrar diseño con el usuario (en particular decisiones #2, #4, #6, #7).
2. Fase 1 — núcleo end-to-end, la única fase que toca binder y parser a la vez; todo lo
   siguiente construye sobre ella sin volver a tocar `BindCall`/`OverloadResolution` en su forma.
3. Fases 2-4 — imports, estáticos, propiedades (independientes entre sí, orden intercambiable).
4. Fase 5 — built-ins y compuestos (depende de que 1-4 ya estén sólidas, mismo motivo por el que
   la stdlib se ordena después del núcleo de colecciones en `docs/Plan-Stdlib.md`).
5. Fase 6 — genéricos (la más grande de las fases de binder, dejar para el final del bloque de
   compilador).
6. Fases 7-8 — LSP y cliente, una vez el binder está estable (evita re-trabajo si el diseño de
   metadata cambia durante 1-6).
7. Fase 9 — solo si hay demanda real confirmada.
