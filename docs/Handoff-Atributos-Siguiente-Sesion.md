# Handoff — Sesión siguiente: completar lo que falta de @Range y @Pure

## Contexto

Proyecto **Surtr** (compilador + VM + stdlib). Sistema de atributos built-in según
`docs/Informe-Atributos-BuiltIn.md` (§P1–P8). Todo **commiteado y en verde**: build limpio,
**2877/2877 tests**.

**Historial:**
- `b9d7ae3` — prerrequisito (validación de argumentos vs campos) + `@Obsolete`/`@NoDiscard`
- `baba38e` — `@Range`/`@Value`/`@Export`/`@Test`+runner/`@Pure`/`@MainThread`/`@ThreadSafe` + targets
- `42cd327` — `@Obsolete` usos de tipo + `@Value` métodos sintetizados
- `ab35cd7` — **`@Range` fase 2 (checks runtime gateados por `Debug`) + `@Pure` fase 2 (verificación)** ← sesión anterior

## Lo que falta (por prioridad)

### 1. `@Pure` fase 3 — plegado/CSE inter-funcional (el gran pendiente)
El informe §P3 fase 3: habilitar plegado/CSE de llamadas `@Pure` con argumentos constantes
**entre** funciones. Hoy `ConstFolder`/const-folding se limita al cuerpo (y a `const fun`).
- Archivos: `src/Surtr.Compiler/CodeGen/ConstFolder.cs`, `InlineCost.cs`. **Riesgo alto**.
- Puntos de entrada ya conocidos: `Binder.PrepareConstFolding` (Binder.cs:3545), `_constFunctions`
  keyed por nombre, `_constFolder = new ConstFolder(_bound)` (Binder.cs:3566), `Constants.CallFolder`.
- La marca `@Pure` da la base *sound* para CSE (transparencia referencial), algo que
  `const fun` no da para funciones con efectos.
- **No ampliar sin consensuar**: el usuario la dejó documentada por riesgo alto. Propón un plan
  (p.ej. reemplazar llamadas `@Pure f(argsConst)` por su resultado evaluado en un runtime de
  scratch, reusando el patrón `ConstFolder`) y aprueba con el usuario antes de tocar el optimizador.

### 2. `@Range` fase 2 — huecos conocidos
Lo hecho (ab35cd7) cubre **solo asignaciones-statement**. Quedan:
- **a) Field initializers no se comprueban**: `public var health: float = 150.0;`. El valor se liga
  vía `Binder.BindInitializer` (Binder.cs:3054) → `BodyBinder.BindInitializer` (BodyBinder.cs:195,
  delega a `BindConverted`). Reto: no hay contexto de statement para un temporal; un initializer con
  efectos (`getDefault()`) no debe evaluarse dos veces. Decidir implementar o dejar como límite.
- **b) Asignaciones en expresiones anidadas**: `x = (obj.f = v) + 1`, inicializadores de `for`, etc.
  no pasan por `BindExpressionStatement`. Para cubrirlos: enganchar en
  `BodyBinder.BindAssignment` (BodyBinder.Expressions.cs:1444). El reto de siempre: el check es
  statement y la asignación es expresión. `BoundThrowExpression` (BoundExpressions.cs:826) permite
  envolver el RHS en un `BoundConditionalExpression`, PERO duplica la evaluación del RHS si tiene
  efectos — solo seguro si el RHS es un literal/local o se captura en temporal. Recomendación: dejar
  statement-level salvo aprobación.
- **c) `@Range` de un solo bound** (`@Range(100.0)` solo `hi`): ya soportado (`RangeLow`/`RangeHigh`
  devuelven null por lado); sin test explícito.
- **d) `@Range` en campo `static`**: debería funcionar (`BoundFieldExpression` con receiver null);
  sin test explícito.

### 3. `@Pure` fase 2 — refinamiento del ruido
- **a) Built-ins C# sin marca**: `s.charAt(0)`, `xs.get(i)`, `s.substring(...)` avisan desde un
  cuerpo `@Pure` y **no se pueden marcar desde source** (se declaran en C#, `SurtrBuiltIns`).
  El usuario eligió "marcar `@Pure` en stdlib" sobre whitelist del compilador; el ruido de los
  built-ins C# quedó como límite documentado. Opciones a consensuar: whitelist de natives
  conocidos-puros en `BuiltInAttributes`, o eximir reads de built-ins. **Confirmar antes de tocar.**
- **b) `@Pure` en property accessors**: el informe dice que `@Pure` en una Property aplica a sus
  accessors. Hoy los property reads están exentos siempre; un setter `@Pure` que muta es edge case
  sin resolver. `Angle.radians` está marcado `@Pure` (getter).
- **c) Constructores**: `Foo()` en cuerpo `@Pure` no avisa (`BoundObjectCreationExpression` no se
  revisa). Un ctor puede tener efectos. Decidir.
- **d) Closures**: `BoundClosureInvocationExpression` no se revisa. Decidir.
- **e) Operadores de usuario**: `a + b` en tipos sobrecargados → `BoundCallExpression` al operador;
  avisa si no está `@Pure`. Posible falso positivo.
- **f) Mutation check**: solo campos/propiedades `public`/`internal` avisan (según report). Escribir
  un campo `private` propio sigue siendo estrictamente impuro (muta `this`); alcance ya decidido.

### 4. `@MainThread` / `@ThreadSafe`
**Explícitamente fuera de alcance** (decisión del usuario en la sesión anterior). Quedan como
metadata documental (fase 1). **No** implementar la fase 2 (lint de contexto de hilos) sin modelo de
hilos del runtime.

## Arquitectura que debes conocer (verificada)

- **Atributos built-in**: clases reales en `SurtrBuiltIns` (Surtr.Core/Runtime/BuiltIns/SurtrBuiltIns.cs).
  `Range` en C# se llama **`RangeAttribute`** (colisión con el primitivo `range`). `@Pure` → `Pure`.
- **Reconocimiento**: por nombre, en `BuiltInAttributes` (Binding/BuiltInAttributes.cs): tabla de
  targets + `ReachesImage`. Helpers útiles ya existentes: `IsPure`, `RangeLow`, `RangeHigh`,
  `IsNoDiscard`, `IsObsolete`, `IsMarkedValue`.
- **Pipeline**: lexer `@` → `Parser.ParseAttributes` → `Binder.RecordAttributes`/`BindAttributes`
  → `Symbol.Attributes` → `ModuleEmitter.Attach` → imagen → `SurtrRuntime.MaterializeAttributes`.
- **Orden de fases**: `BindAttributes` corre en `BindBodies` **antes** de ligar cuerpos → las marcas
  existen cuando los cuerpos se ligan, pero **NO** durante `MemberPhase`.
- **`$`-convención**: sintetizado por el compilador lleva `$`; la reflexión lo oculta
  (`IsSynthetic`). `SyntheticNames` (Binding/Symbols).
- **Gate de checks**: `BodyBinder` ahora recibe `rangeChecksEnabled` (bool) por ctor; lo pasa
  `Binder.BindOne` (Binder.cs:3512) leyendo `_compilation.Project.BuildConstants.ContainsKey("Debug")`.
  **Ojo: hay 5 construcciones `new BodyBinder(` en Binder.cs** (2813, 3067, 3407, 3449, 3512). El
  flag solo se pasa en `BindOne`. Si se necesita en initializers (`BindInitializer`, 3067), pasarlo ahí.
- **`BuildLibraryException(syntax, name, message)`** (BodyBinder.Expressions.cs:718): resuelve el ctor
  de una clase stdlib por reflexión en `_typeScope`; devuelve `null` si la clase no existe (degrade
  silencioso). Ahora existe `surtr:ArgumentOutOfRangeException` (subclase de `ArgumentException`).
- **Emisor**: tolera `BoundLiteralExpression(_factory.Float, double)` y interpolación de strings con
  partes primitivas (el emisor las stringifica). `BoundThrowStatement(syntax, value)` emite `Throw`.
- **Excepción lanzada por script** → `SurtrThrownException` (subclase de `SurtrExecutionException`).
  `Assert.Throws<T>` de xUnit exige **tipo exacto** (usa `SurtrThrownException`, no la base).
- **Diagnóstico nuevo**: `PureContractViolated = 3081` (SurtrDiagnosticCode.cs).

## Reglas obligatorias del repo (AGENTS.md)

1. **Antes de editar cualquier símbolo**: `impact({target, direction:"upstream"})` y reportar blast
   radius. HIGH/CRITICAL → detente y avisa. Ya sabidos: `BindAttributes` CRITICAL,
   `FlowAnalysis` CRITICAL, `TypeResolver.Resolve` CRITICAL, `OverloadResolution.Resolve` HIGH —
   cambios aditivos aprobados por patrón, pero confirma.
2. **Antes de commit**: `detect_changes()` y verificar que solo cambian símbolos esperados.
3. No renombrar con find-and-replace; usa `rename` de GitNexus.
4. Verificación: `dotnet build Surtr.sln` y `dotnet test src/Surtr.Tests`.
5. **No amplíes alcance silenciosamente**: whitelist, gate, alcance de fases → aprobación explícita.

## Cómo arrancar

1. `git status` (debería estar limpio salvo `docs/Informe-*.md` sin trackear, del usuario).
2. `git log --oneline -5`.
3. Lee `docs/Informe-Atributos-BuiltIn.md` §P3 y §P4 — tienen notas **> Estado (implementado)**
   actualizadas que resumen qué se hizo y qué queda.
4. Para cada ítem: impact() → implementar → tests (BuiltInAttributeTests.cs para diagnósticos;
   ModuleEmitterTests.cs para runtime/imagen) → build+test → checkpoint con el usuario.

## Trampas aprendidas en la sesión anterior

- **Literal int fuera de int32**: el emisor lo valida (ej. `2166136261` falla). Usa constantes que quepan.
- **`checked` es keyword de C#**: no la uses como nombre de variable de patrón
  (`... is BoundStatement checked` → CS1026). Renombré a `guarded`.
- **El fallo "no lanza" era el tipo del assert**: el check SÍ emitía (confirmado con
  `SurtrBytecodeDisassembler`); `Assert.Throws<SurtrExecutionException>` fallaba porque el script
  lanza `SurtrThrownException` (subtipo). Usa el tipo exacto.
- **El harness de tests no auto-resuelve stdlib source**: `import surtr.math.Math` en
  `BuiltInAttributeTests.Compile` falla ("Math does not name anything in scope"). Para stdlib hay que
  proveer el source inline (con `AddSourceFile(..., modulePath, ...)`) o cargar la imagen real
  (patrón `SurtrStdlibTests.MathImage()` / `RunDebug` de ModuleEmitterTests). `ModuleEmitterTests.Build`
  ahora tiene el overload `Build(source, bool defineDebug, ...)` + `RunDebug`.
- **`DeclareLocal` reporta duplicados**: los temporales `$rangeN`/`$destructureN` son únicos por
  método gracias al contador.
- **Comparación contra bounds float**: usa `BodyBinder.Widen(value, _factory.Float, syntax)`
  (BodyBinder.Expressions.cs:1212) para subir un int a float antes de comparar.
- **`Widen` es privado de BodyBinder** (partial class): accesible desde cualquier partial.
- **Los initializers de campo no comprobados**: `@Range` en `public var health: float = 100.0;` no se
  valida (límite documentado en el informe).