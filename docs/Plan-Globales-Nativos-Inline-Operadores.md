# Plan: globales nativos, inlineado y operadores

Este documento registra el plan acordado para implementar seis propuestas derivadas de una
investigación del estado actual del compilador y el runtime. Cada fase es independiente y
se verifica con `dotnet build Surtr.sln` + `dotnet test Surtr.sln`.

## Contexto

Las seis propuestas, con la viabilidad que concluyó la investigación:

| # | Propuesta | Veredicto |
|---|---|---|
| 1 | Eliminar el sistema de variables/funciones globales del host (`Ldg`/`Stg`/`CallGlobalNative`), sustituyéndolo por métodos/propiedades `native` declarados en Surtr | Viable; el mecanismo sustituto está ~80% construido |
| 2 | Permitir operadores `abstract` y declarados en interfaces | Viable por la **Ruta A**: operadores como métodos de instancia |
| 3 | Garantizar que el tipo declarante aparece entre los operandos de un operador | Falta; hoy solo se aproxima en el uso, no en la declaración |
| 4 | Evitar colisiones de nombres entre accessors (`get_x`/`set_x`) y métodos escritos por el usuario | No hay colisión; había un hueco en `SignatureSet` |
| 5 | Heurística de inlineado por coste/tamaño: laxa por defecto, `inline` más exhaustiva, `forceinline` forzada | Implementada en la Fase D (`InlineCost.cs`, umbrales 2/8/∞) |
| 6 | Las propiedades (getter/setter) siguen las mismas reglas de inlineado; las auto-propiedades inlinean automáticamente | Implementada en la Fase D (auto-props a `Ldfld`/`Stfld`; el getter honra el hint) |

---

## Fase A — Colisiones de accessors (punto 4) — HECHA

Un `get_x`/`set_x` escrito por el usuario y el accessor sintetizado de una propiedad `x`
ocupan la misma entrada de tabla de métodos, pero el `SignatureSet` no veía los accessors,
así que la colisión explotaba como excepción del builder en vez de como diagnóstico.

Cambios:

- `SignatureSet.Add` recibe ahora `sourceName` por llamada (no guardado en el ctor), porque
  el conjunto de un módulo abarca varios archivos.
- En `Binder.BindMembers` (clase), los accessors de cada propiedad entran también en
  `signatures`.
- En `Binder.BindModuleMembers` (módulo), el `SignatureSet` se movió **fuera** del bucle de
  unidades (por módulo, no por archivo) — esto también cierra un bug latente: dos archivos
  del mismo módulo con métodos de la misma firma no se detectaban.

Tests añadidos en `BinderTests`: colisión getter/método, setter/método, accessor de módulo,
y duplicado cross-unit.

**Nota — corrección pre-existente de la stdlib**: durante la verificación se descubrió que
`src/Surtr.Stdlib/src/surtr/collections/List.surtr` no compilaba (SURTR3043): `LinkedList`
declaraba sus implementaciones de `IList`/`IReadOnlyList` sin `override` (en Surtr las
implementaciones de contrato se escriben `override`, §2.2) y le faltaban `removeAt` y `clear`
por completo. Se añadió `override` a `add`/`get`/`set`/`length` y se implementaron `removeAt`
y `clear`.

---

## Fase B — El tipo declarante entre los operandos (punto 3) — HECHA

**Problema**: `CheckOperatorSignature` (Binder.cs) no validaba que el tipo declarante
aparezca entre los operandos. La única aproximación estaba en `TryBindUserOperator`
(BodyBinder.Expressions.cs:680-728), que rechaza en el *uso* un operador cuyo primer
operando no coincide con el declarante. Resultado: se podía declarar `operator +(a: int, b: int)`
dentro de `class Foo` y solo fallaba al usarlo.

**Implementado**:

- `CheckOperatorSignature` comprueba ahora que al menos un operando sea el tipo declarante
  (o una construcción de él, como `Matrix<T>` dentro de `class Matrix<T>`), con la
  nullabilidad despellejada (`NonNullable`).
- `operator[]` es la excepción: su receptor (parámetro 0) debe ser el tipo declarante,
  porque el índice y el valor operan sobre el objeto indexado.
- `operator as` lleva el destino como retorno, así que su único parámetro es el único
  operando y debe ser el declarante.
- Todo reporta `InvalidOperatorSignature` (3044) con span, en la declaración, no en el uso.
- `IsDeclaringType` compara por `ReferenceEquals` contra el símbolo, o contra
  `Definition` cuando el tipo es construido.
- Tests en `BinderTests`: operador con operandos ajenos, índice receptor ajeno,
  conversión desde tipo ajeno, construcción genérica propia, operando nullable.

---

## Fase C — Operadores `abstract` y en interfaces (punto 2) — HECHA

**Estado actual**: la especificación decía que los operadores son siempre `public static`
(§5.6), pero la implementación aceptaba `abstract`/`virtual` y los descartaba silenciosamente,
y `SurtrInterface.AddMethod` aceptaba por accidente un operador de interfaz reinterpretándolo
como método abstracto de instancia.

**Decisión: Ruta A** — los operadores declarados `abstract`, `virtual`, `override` o en una
interfaz se tratan como **métodos de instancia** cuyo receptor es el primer operando. Esto
reutiliza el vtable/interface dispatch existente y requirió **cero cambios de runtime**.

Cambios implementados:

- **Sintaxis**: `OperatorDeclarationSyntax` (DeclarationSyntax.cs) gana `Dispatch` (del
  modificador) y `IsSealed`; el cuerpo pasa a ser nullable (un operador `abstract` no tiene
  cuerpo).
- **Parser**: `ParseOperator` acepta `abstract`/`virtual`/`override`/`sealed` antes de
  `operator` y permite terminar en `;`. Sigue rechazando `static` explícito y visibilidad.
- **Binding**: `BindOperator` crea el símbolo con `Dispatch`/`IsStatic` correctos (interfaz →
  `abstract` de instancia; `sealed` → instancia; sin modificador → estático `Direct`), valida
  cuerpo (abstract sin cuerpo; concreto con cuerpo; interfaz sin cuerpo), y rechaza
  `operator as` de instancia. `SignatureSet` excluye el receptor (param 0) de la firma de un
  operador de instancia, como el runtime; `CheckOperatorSignature` exige que el receptor sea el
  tipo declarante o un ancestro.
- **Usos**: `BindOperatorCall` compartido — un operador estático es `BoundCallExpression` sin
  receptor; uno de instancia lleva el primer operando como receptor (conversión a `Parameters[0]`)
  y dispatch virtual/interface. Aplicado en `TryBindUserOperator`, `TryBindUserUnary` y
  `BindIndexOperator`.
- **Emisión**: el receptor (param 0) de un operador de instancia no entra en la lista de
  parámetros del método (el runtime lo trata como slot implícito); `ParameterSlot` lo mapea al
  slot del receptor. `ModuleEmitter` y `SignatureSet` quitan el param 0 en consecuencia.
- **Spec**: `Language-Syntax.md` §5.6 actualizada (operadores de instancia, regla del receptor).
- **Tests**: 9 de binder (operador virtual/override de instancia, conversión no instanciable,
  cuerpo abstract/concreto, operador de interfaz, receptor extranjero, duplicado por receptor
  excluido), 3 de parser (modificadores, `;`, `sealed`), 3 de runtime (dispatch virtual,
  dispatch por interfaz, `this` en el cuerpo). **1927 tests verdes, build 0 warnings.**

La Ruta B (operadores estáticos abstractos tipo C# 11) se descarta: requiere un modelo de
dispatch nuevo y más pesado que no compensa.

---

## Fase D — Inlineado por coste y propiedades (puntos 5 y 6) — HECHA

**Estado actual**: `inline` y `forceinline` se trataban idénticamente en el call site
(MethodBodyEmitter.cs:2475-2482); no había heurística. El flag `inline`/`native` de una
propiedad se descartaba al bindear; las auto-propiedades siempre emitían llamada real
(`IsAutoAccessor`/`EmitAutoAccessor`, ModuleEmitter.cs:1346-1387).

Cambios:

- **D1 — Plomería en propiedades**: `PropertyDeclarationSyntax` (DeclarationSyntax.cs:412-454)
  gana `Inline`/`IsNative`; el parser los pasa (Parser.Declarations.cs:368-369) y
  `WireAccessors` (Binder.cs:1925-1980) los propaga a los accessors
  (`MethodSymbol.IsInline`/`IsForceInline`/`IsNative`). La Fase E reutiliza `IsNative`.
- **D2 — Auto-propiedades**: `TryInlineAutoAccessorGet`/`TryInlineAutoAccessorSet`
  (MethodBodyEmitter.cs) bajan el `get` a un `Ldfld`/`Ldsfld` y el `set` a un `Stfld`/`Stsfld`
  del backing field en el propio call site, sin frame. Solo accessors no virtuales (un virtual
  tiene que despachar para que corra un override) y con receptor no value class. El setter de
  una propiedad *computada* sigue siendo llamada directa — el hint puede declinarse ahí.
- **D3 — Heurística de coste** (`CodeGen/InlineCost.cs`): estimador sobre el árbol ligado
  (conteo ponderado de nodos; una llamada cuesta 4, un `try` 3, un cuerpo multi-`return` paga
  la maquinaria de exit-label; las hojas triviales — literal, local, parámetro, conversión
  identidad — cuestan 0). Umbrales: por defecto **2** (retorno de campo/constante, una o dos
  instrucciones), `inline` **8** (cuerpos moderados), `forceinline` sin umbral.
- **Aplicación** (MethodBodyEmitter.cs): `EmitCall` consulta `ShouldInlineByCost` para el caso
  por defecto, `TryInline` para `inline` (hint; si no puede, llamada real), y `forceinline`
  sigue lanzando `Unsupported` si el cuerpo no está disponible. **Guard nuevo: un constructor
  nunca se splicea** — un `super(...)` que nombrara el cuerpo de un constructor lo dejaría sin
  su cadena e inicializadores (lo descubrió `AChainReachesThroughThreeLevels`). La lectura de
  propiedad honra el hint y la heurística en su getter (`TryInlinePropertyGetter`, que da forma
  de llamada de cero argumentos al getter y splicea por la vía normal).
- **Spec**: `Language-Syntax.md` §3.6 (heurística por defecto, constructor nunca spliceado,
  getter de propiedad) y §3.4 (el `inline`/`forceinline` de una propiedad aplica a sus
  accessors).
- **Tests**: 8 nuevos (2 de valor en `ModuleEmitterTests`: función trivial spliceada por
  defecto, auto-propiedad lee/escribe el backing field; 6 de opcodes en `LoweringChoiceTests`:
  umbral por defecto a ambos lados, hint `inline` sobre el umbral, auto-propiedad sin
  `InvokeSpecial`, virtual que sí despacha, getter `inline` spliceado). Un test de const-fun
  se ajustó a un cuerpo por encima del umbral para seguir probando el *fold* y no el inline.
  **1935 tests verdes, build 0 warnings.**

---

## Fase E — Eliminar los globales del host (punto 1) — EN CURSO, ROMPE EL BUILD

**Auditoría (2026-08-16)**: el trabajo de runtime/compilador está sustancialmente hecho y es
**correcto** donde está hecho — `SurtrContext.Globals`, `DefineGlobal`/`DefineGlobalFunction`,
`BindNativeImports`, `SurtrNativeGlobals.cs` y las tablas viejas de `SurtrChunk` están borrados;
los opcodes `Ldg`/`LdgX`/`Stg`/`StgX`/`CallGlobalNative`/`CallGlobalNativeX` están retirados de
`OpCode.cs` (no reasignados, cumple la regla de valores finales) y sin rama huérfana en
`SurtrVirtualMachine.cs`; el emisor ya no tiene rama `CallGlobal` en `MethodBodyEmitter.cs` —
una llamada a `native fun` de módulo pasa por el mismo camino que un método normal
(`Call`/`Direct`); `SurtrModuleBuilder` tiene `DeclareNativeFunction`/`DefineNativeFunction`
análogos a los de `SurtrClassBuilder`; la imagen subió `FormatVersion` a **4** y el
reader/writer ya no tocan las listas de imports viejas; el parser trata `native let/var/fun`
de módulo como el mismo nodo (`FieldDeclarationSyntax`/`FunctionDeclarationSyntax` con
`IsNative=true`) que un miembro `native` de clase, y `Binder.BindModuleNativeVariable`
(Binder.cs:1230-1242, 1882-1936) construye la propiedad nativa por el mismo camino que
`WireAccessors`, sin duplicación.

**Pero el build está roto ahora mismo** porque quedan consumidores del sistema viejo sin
migrar. Checklist real, en orden de dependencia:

1. **Bloquean la compilación** (arreglar primero):
   - `src/Surtr.Tests/VM/BytecodeBuilder.cs:33-41, 274-297` — `AddNativeVariable`/
     `AddNativeFunction` construyen `SurtrNativeGlobalVariable`/`SurtrNativeGlobalFunction`
     y las tablas `_nativeVariableImports`/`_nativeFunctionTable`, todo borrado. Quitar estos
     helpers (o rehacerlos sobre `DefineNativeFunction`/`SurtrNativeEntryPoint` si algún test
     los necesita de verdad).
   - `src/Surtr.Bench/SurtrDriver.cs:62-73` (`RegisterNativeGlobals`) — llama a
     `runtime.DefineGlobalFunction(...)`, borrado. Migrar a
     `runtime.DefineNativeBody("hostAdd", ...)` con el link name que emita el compilador para
     un `native fun hostAdd` de módulo.
2. **Rotos por el mismo motivo, no listados en la redacción original de esta fase**:
   - `src/Surtr.Tests/Runtime/Classes/SurtrParameterInfoTests.cs:197` —
     `new SurtrNativeGlobalFunction(...)`.
   - `src/Surtr.Tests/Compiler/CodeGen/ModuleEmitterTests.cs:3067-3098` — usa
     `runtime.DefineGlobal`, `runtime.Globals.SetValue/TryGetValue`, `runtime.DefineGlobalFunction`.
   - `src/Surtr.Tests/Runtime/SurtrNativeImportTests.cs` (archivo completo, ~178 líneas) —
     prueba el sistema viejo de punta a punta (`DefineGlobal`, `Globals`, `DefineGlobalFunction`,
     `builder.NativeVariable(...)`). Necesita reescritura completa sobre el mecanismo nuevo
     (`DeclareNativeFunction`/`DefineNativeFunction` + `DefineNativeBody`), no un parche.
3. **`src/Surtr.Tests/Bytecode/OpCodeValueTests.cs:37,68`** — sigue pineando
   `(OpCode.Ldg, 0x2C)`, `(OpCode.LdgX, 0x2D)`, `(OpCode.Stg, 0x2E)`, `(OpCode.StgX, 0x2F)`,
   `(OpCode.CallGlobalNative, 0xAA)`, `(OpCode.CallGlobalNativeX, 0xAB)` — miembros que ya no
   existen en el enum (CS0117). Quitar esas tuplas; 0x2C-0x2F y 0xAA-0xAB quedan ausentes de
   la tabla.
   - `src/Surtr.Tests/VM/SurtrVirtualMachineCallTests.cs:183,203,222` y
     `SurtrVirtualMachineExceptionTests.cs:310,360` — usan `OpCode.CallGlobalNative`/
     `CallGlobalNativeX` + `builder.AddNativeFunction(...)`; se arreglan solos una vez resuelto
     el punto 1.
   - `src/Surtr.Tests/VM/SurtrVirtualMachineStackAndLoadStoreTests.cs:407,408,427,428,447` —
     usan `OpCode.Stg`/`Ldg`/`StgX`/`LdgX` directamente. No hay instrucción equivalente (acceder
     a un `native` de módulo ahora es una llamada, no una carga/almacén directo) — estos tests
     se borran, no se migran.
4. **Documentación** (después de que compile y pase la suite) — todos desincronizados,
   ninguno tocado por el diff actual:
   - `docs/Language-Syntax.md` §10 (líneas 1819-1849) — sigue describiendo el modelo viejo
     ("host globals are genuinely global... there's no `native` member inside a class body"),
     que ya no es cierto. Reescritura completa de la sección.
   - `docs/Opcodes.md` (líneas 105, 247-250, 442, 450-451) — tabla con los 6 opcodes retirados.
   - `docs/VM-Plan.md` (líneas 92, 103, 811+) — describe `Ldg`/`Stg`/`CallGlobalNative` como
     diseño vigente.
   - `docs/Module-Format.md` (líneas 325, 353, 360-361) — "a `native` in Surtr source is always
     a host global and never a native method", contradicho por el código actual.
   - `docs/Runtime-Model.md:583` y `docs/Compiler-Plan.md:749` — mismas referencias obsoletas.
   - **`CLAUDE.md` mismo** — la sección "The Surtr language model" todavía afirma que las
     variables/funciones nativas del host "can never be declared from Surtr source — only by
     the embedding host", que ya no es exacta bajo el modelo nuevo. Corregir en el mismo commit
     que cierre esta fase, por la propia regla del archivo ("a doc that contradicts the code is
     worse than no doc").

---

## Fase F — Correcciones encontradas en la auditoría (2026-08-16)

Dos bugs reales y cuatro huecos de cobertura de test, encontrados verificando las Fases B–D
contra el código actual en vez de fiarse de este documento.

### F1 — Bug: el operador **estático** no exige el tipo declarante literal (punto 3)

`CheckOperatorSignature` (Binder.cs:2239-2325) calcula `receiverIsDeclaring` con `IsReceiver`
(Binder.cs:2345-2379), que además de la igualdad estricta **camina bases e interfaces** —
correcto para un receptor de instancia con `override`, donde un ancestro debe contar. Pero esa
misma variable alimenta `anyOperandIsDeclaring` (línea 2315) también para el caso
**estático**, donde la regla debería ser la estricta `IsDeclaringType` (despellejando
nulabilidad, sin caminar jerarquía) — es la que ya usa el resto del bucle sobre operandos
adicionales.

Consecuencia: `class Foo : Base { operator +(a: Base, b: Base): Base { ... } }` (operador
**estático**, sin modificador de despacho) pasa la validación porque `a: Base` matchea a `Foo`
por la vía del recorrido de ancestros, aunque ningún operando sea literalmente `Foo`. Esto
contradice el comentario de la propia regla (Binder.cs:2305-2313: "a type cannot define how
two types foreign to it interact") para el caso estático. Ningún test en `BinderTests.cs`
cubre este caso — todos los existentes usan tipos totalmente ajenos (`int`), no una subclase
con operandos del tipo base.

**Fix**: en la rama estática, calcular `anyOperandIsDeclaring` con `IsDeclaringType` sobre cada
operando (incluido el que hoy se lee como "receptor" posicional), no con `IsReceiver`. Añadir
test: operador estático en `Foo : Base` con ambos operandos `Base` debe reportar
`InvalidOperatorSignature`.

### F2 — Bug: el setter de una propiedad computada no honra `inline`/`forceinline` (punto 6)

El camino de escritura de propiedad (MethodBodyEmitter.cs:2213-2231) solo prueba
`TryInlineAutoAccessorSet` (auto-propiedad sin cuerpo ligado); si eso falla, va directo a
`EmitResolvedCall` — **nunca** consulta `IsInline`/`IsForceInline` ni la heurística de coste.
No existe `TryInlinePropertySetter`. El getter sí lo hace (`TryInlinePropertyGetter`,
MethodBodyEmitter.cs:2418-2441), simétrico a un método normal, incluyendo lanzar `Unsupported`
cuando el getter está marcado `forceinline` y no se puede inlinear.

Consecuencia: un setter computado marcado `forceinline` **se ignora en silencio** — no
inlinea y no lanza error — mientras que un método o un getter en la misma situación sí fallan
ruidosamente. Rompe la garantía de "fuerza o falla" específicamente para setters, y contradice
la premisa de que getter/setter siguen las mismas reglas. (El propio documento ya lo admitía
como limitación conocida en la Fase D — línea 134 de este archivo — pero no como bug a
corregir; tras la investigación del usuario, sí lo es.)

**Fix**: añadir `TryInlinePropertySetter`, simétrico a `TryInlinePropertyGetter` — da forma de
llamada de un argumento al setter y lo splicea por la vía normal (`TryInline`/coste), lanzando
`Unsupported` en el caso `forceinline` imposible. Test: setter computado `forceinline` sobre un
método virtual (o cualquier caso estructuralmente imposible) debe lanzar, no ignorar.

### F3 — Huecos de cobertura de test (sin bug asociado, pero sin red de seguridad)

- **`forceinline` no tiene ningún test** — ni éxito, ni el caso de error (`Unsupported`), ni
  virtual+`forceinline`, ni recursión. `grep forceinline` en `src/Surtr.Tests` solo aparece en
  `LexerTests.cs` (reconocimiento de token).
- **`sealed abstract`** en un operador (o en un método normal — el hueco de validación es
  preexistente y no específico de operadores) no se rechaza; `Binder.cs:2192` fija `IsSealed`
  sin comprobar `Dispatch==Abstract`. Sin test.
- **Colisión módulo/clase homónima**: `SignatureSet` está correctamente aislado por tipo y por
  módulo (instancias separadas, `Binder.cs:984` y `:1216`), y `BodyBinder.BindCall`
  (BodyBinder.Expressions.cs:1091-1102) resuelve primero contra la clase contenedora antes que
  contra el módulo — shadowing silencioso, sin ambigüedad, tal como se espera. Pero no hay
  ningún test que declare explícitamente un método de clase y una función de módulo con el
  mismo nombre y verifique cuál gana.
- **Operador estático con receptor de subclase** (ver F1) — sin test, ver arriba.

### F2b — Bug gemelo encontrado al implementar F2: el getter tenía el mismo fallo (punto 6)

Al escribir el test de regresión para F2 (`forceinline` en un setter virtual debe lanzar, no
ignorarse) se descubrió que **la propia auditoría original se equivocaba** al afirmar que el
getter ya lanzaba correctamente en ese caso. `TryInlinePropertyGetter` (MethodBodyEmitter.cs)
comprobaba `getter.Dispatch != MethodDispatch.Direct || getter.IsNative` como primera línea y
devolvía `false` inmediatamente — **antes** de llegar a su propio chequeo de
`getter.IsForceInline` al final del método. Como la `BoundCallExpression` sintética que construye
para intentar el splice lleva `isVirtual: false` siempre (para que `TryInline` no la rechace por
otro motivo), ese guard de cabecera era la única defensa contra spliciar el cuerpo equivocado de
un getter virtual — pero como efecto secundario, un getter virtual marcado `forceinline` también
caía en él y se ignoraba en silencio en vez de fallar, exactamente el mismo bug que motivó F2,
solo que en el lado de lectura.

**Fix**: en `TryInlinePropertyGetter` y en el `TryInlinePropertySetter` de F2, el guard de
`Dispatch`/`IsNative` ahora comprueba `IsForceInline` antes de devolver `false` y lanza
`Unsupported` si corresponde, en vez de devolver `false` sin más. Tests añadidos:
`AForceInlineVirtualPropertyGetterIsNotLowered` (ModuleEmitterTests) confirma el caso que antes
pasaba en silencio.

### F4 — Nota, no bug: `sealed`/dispatch sin modificador explícito en operador concreto

No se encontró bug adicional en la Fase C (abstract/interfaz) más allá de F1/F3; `BindOperator`
fuerza correctamente `Dispatch=Abstract` para operadores de interfaz vía `TranslateDispatch`
(Binder.cs:2531-2543) sin importar lo escrito en fuente, y `SurtrInterface.AddMethod` valida
`Dispatch==Abstract && Visibility==Public && !IsStatic` — condiciones que `BindOperator` ya
garantiza, incluido `operator[]`. `operator as` de interfaz se rechaza limpiamente antes de
llegar a `AddMethod`. No requiere acción.

---

## Orden de ejecución y estado

| Fase | Estado |
|---|---|
| A — Accessors en `SignatureSet` | **Implementada** (tests verdes) |
| B — Tipo declarante en operadores | **Implementada** — bug corregido en F1, con test de regresión |
| C — Operadores abstract/interfaz | **Implementada** (tests verdes) |
| D — Heurística inline + propiedades | **Implementada** — bugs corregidos en F2/F2b, con tests de regresión |
| E.1-E.3 — Eliminar globales host (código + tests) | **Implementada**: build y suite en verde |
| E.4 — Documentación | **Implementada**: `Language-Syntax.md` §10, `Opcodes.md`, `VM-Plan.md` §1.6/§4.14, `Module-Format.md`, `Runtime-Model.md`, `Compiler-Plan.md`, y `CLAUDE.md` mismo |
| F — Correcciones de la auditoría | **Implementada** (F1, F2, F2b, F3) |

Cada fase termina con build completo + suite verde. **Estado a 2026-08-16: plan completo.**
`dotnet build Surtr.sln` y `dotnet test Surtr.sln` en verde, **1936/1936 tests**.

### E.4 — lo que se hizo (2026-08-16)

Todos los documentos que aún describían el sistema viejo (globales del host, `Ldg`/`Stg`/
`CallGlobalNative`, tabla de imports nativos por módulo) se reescribieron para describir el
mecanismo unificado actual — un miembro `native` (a nivel de módulo o de clase) es un miembro
ordinario que enlaza contra un cuerpo del host por *link name* en la carga, sin tabla ni opcode
propios. Donde un documento llevaba un historial tipo *"era X, ahora es Y"* (`Compiler-Plan.md`
§10.1, `VM-Plan.md` "lo que aterrizó"), se dejó la entrada histórica intacta y se añadió una nota
señalando que ese mecanismo concreto fue retirado y sustituido, en vez de reescribir la historia.
`Language-Syntax.md` §10 ganó además un ejemplo de clase híbrida Surtr/host (`Sprite`), que es el
caso que esta fase entera existía para habilitar.

### E.1-E.3 — lo que se hizo (2026-08-16)

Todos los consumidores del sistema viejo listados en el checklist de la Fase E, más uno que el
checklist original no había detectado (`SurtrEntityRegistry.CollectGarbage` perdió sus
parámetros de escaneo de globals, lo que rompía `SurtrEntityRegistryTests.cs` en 5 sitios; y
`SurtrNativeModuleImageTests.AModuleLevelNativeAccessor_IsRejected` probaba justo la conducta
vieja que esta migración existe para invertir — se sustituyó por
`AModuleLevelNativeAccessor_RoundTripsAndRuns`, que confirma que un accesor nativo de módulo
ahora se declara, enlaza por link name y ejecuta con normalidad).

- `BytecodeBuilder.cs`, `SurtrDriver.cs` (Bench), `SurtrParameterInfoTests.cs`,
  `ModuleEmitterTests.cs`, `OpCodeValueTests.cs`, `SurtrVirtualMachineCallTests.cs`,
  `SurtrVirtualMachineExceptionTests.cs`, `SurtrVirtualMachineStackAndLoadStoreTests.cs`,
  `SurtrEntityRegistryTests.cs`, `SurtrNativeModuleImageTests.cs`: migrados o recortados a
  `DefineNativeBody`/`DeclareNativeFunction`/`InvokeStatic` según el caso; donde no había
  instrucción ni mecanismo equivalente (los tres tests de `Ldg`/`Stg`, el de
  `CollectGarbage` a través de la tabla de globals) el test se borró en vez de migrarse.
- `SurtrNativeImportTests.cs` se reescribió entero sobre el mecanismo unificado (método nativo
  de módulo declarado con `DeclareNativeFunction` + `DefineNativeBody`), conservando la
  cobertura de comportamiento (enlace por nombre, fallo de carga si el host no registró el
  cuerpo, rollback tras un fallo de carga, independencia entre runtimes) y retirando la mitad
  "variable" al ser ahora el mismo mecanismo que la mitad "función".
- `OpCodeValueTests.cs`: el test `TheAssignedValuesAreContiguousFromZero` asumía cero huecos en
  los valores de opcode, lo cual ya no es cierto con opcodes retirados. Se sustituyó por
  `TheAssignedValuesAreContiguousExceptAtRetiredSlots` (contigüidad salvo en los huecos
  documentados) y se añadió `RetiredValuesAreNotAssignedToAnything` como red de seguridad
  explícita contra una futura reasignación accidental de 0x2C-0x2F/0xAA-0xAB.