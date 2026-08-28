# Informe: sistema de atributos de Surtr y propuesta de atributos built-in

> Fecha: 25/08/2026. Solo investigación y propuesta; no modifica codigo fuente.
> Referencias con formato `ruta:fichero:línea` relativas a la raíz del repositorio.

---

## 1. Resumen ejecutivo

Surtr tiene **dos sistemas de atributos independientes pero espejados**:

1. **Atributos del lenguaje** (§11 de `docs/Language-Syntax.md`): sintaxis Java-style `@Nombre(args)` sobre cualquier declaración; las clases de atributo son clases Surtr normales que extienden la raíz abstracta `Attribute` (declaradas preferentemente con el keyword contextual `attribute`, con lista opcional de *targets* y retención `Runtime`/`CompileTimeOnly`). Se bindean verificando tipo y targets, se pliegan a constantes, se serializan en la imagen del módulo y se **materializan como instancias reales en carga**, consultables tanto desde scripts (reflexión `Type`/`Member`) como desde el host C#.
2. **Atributos de interop C#** (`src/Surtr.Interop.Attributes`): atributos .NET (`[SurtrNativeType]`, `[SurtrNativeMethod]`, ...) que marcan tipos CLR para exponerlos a Surtr, consumidos por dos vías equivalentes: un source generator Roslyn (emite registro AOT en compile time) y un escáner por reflexión (`SurtrBridge.ScanAndRegister`, modo editor).

Hoy **no existe ni un solo atributo built-in del lenguaje**: la raíz `Attribute` está declarada, toda la maquinaria funciona punta a punta (hay tests de parser, binder, emitter, imagen, reflexión y LSP), pero el vocabulario queda abierto y vacío. El compilador todavía no da ningún significado semántico a ningún atributo: los valida y los transporta, pero no cambia nada del programa por llevarlos.

Este informe documenta la arquitectura completa con referencias, extrae limitaciones y puntos de extensión, estudia cómo resuelven esto otros lenguajes y propone **ocho atributos built-in** concretos (`@Obsolete`, `@NoDiscard`, `@Pure`, `@Range`, `@Export`, `@Value`, `@Test`, `@MainThread`), con priorización.

---

## 2. Investigación: arquitectura actual

### 2.1 Visión general del pipeline (lado lenguaje)

```
Lexer ('@' -> TokenType.At)
   -> Parser.ParseAttributes()          -> AttributeSyntax (nombre + args posicionales)
   -> adjunta a DeclarationSyntax.Attributes (cualquier declaración)
   -> Binder.RecordAttributes / BindAttributes
        - resuelve el nombre a una clase que extiende Attribute
        - valida targets (attribute(...) declarado en la clase de atributo)
        - pliega argumentos a constantes
        - guarda AttributeUse(type, constants) en Symbol.Attributes
   -> ModuleEmitter.Attach
        - copia a la metadata (SurtrMemberInfo.AddAttribute)
        - SALTA los atributos CompileTimeOnly
   -> Imagen del módulo (.surtrmod v8): WriteAttributes / ReadAttributes
   -> SurtrRuntime.MaterializeAttributes (en carga)
        - una instancia SurtrInstance por uso, enraizada permanentemente,
          campos rellenados posicionalmente desde las constantes
   -> Lectura: host (SurtrMemberInfo.TryGetAttribute) y scripts
      (Type.attributes() / Member.attributes())
```

### 2.2 Sintaxis léxica y parsing

- El lexer produce un token dedicado para `@`: `src/Surtr.Compiler/Syntax/Lexer.cs:689` (`case '@': return Make(TokenType.At, start);`). `@` no tiene otro uso en el lenguaje (la interpolación de cadenas usa `$`), así que el token no ambigüo.
- El parser acumula atributos en bucle antes de cualquier declaración: `ParseAttributes()` en `src/Surtr.Compiler/Syntax/Parser.cs:400-431`. Cada atributo es `@Identificador` seguido opcionalmente de `(expresión, ...)` — **solo argumentos posicionales**, separados por comas (`src/Surtr.Compiler/Syntax/Parser.cs:412-424`); no hay argumentos nombrados.
- La recolección ocurre al principio de `ParseDeclaration` (`src/Surtr.Compiler/Syntax/Parser.Declarations.cs:38`), igual que el doc comment `///`, y ambos viajan en la base de todas las declaraciones: `DeclarationSyntax.Attributes` y `DeclarationSyntax.DocComment` (`src/Surtr.Compiler/Syntax/Ast/DeclarationSyntax.cs:103-122`).
- El nodo AST es mínimo: `AttributeSyntax { Name: string, Arguments: IReadOnlyList<ExpressionSyntax> }` (`src/Surtr.Compiler/Syntax/Ast/DeclarationSyntax.cs:126-143`). En esta fase el nombre es texto crudo; no se resuelve.

Sintaxis real (test `AttributesAndDocCommentsAttachToTheDeclarationBelow`, `src/Surtr.Tests/Compiler/Syntax/ParserTests.cs:820-831`):

```surtr
/// Moves it.
/// @param dx offset
@Obsolete("use moveTo")
@Pure
public fun move(dx: float): void { }
```

### 2.3 Declarar una clase de atributo: el keyword contextual `attribute`

El parser reconoce `attribute` como **cuarta palabra contextual** (con `this`/`super`/`value`; `docs/Language-Syntax.md:108-113`), solo inmediatamente antes de una declaración de clase (`src/Surtr.Compiler/Syntax/Parser.Declarations.cs:93`). `ParseAttributeClassDeclaration` (`src/Surtr.Compiler/Syntax/Parser.Declarations.cs:475-528`) lee una lista opcional entre paréntesis que mezcla dos clases de elemento:

- **Targets**: `Class` (también value class y singleton), `Interface`, `Enum`, `Field`, `Property`, `Method` (también constructores y funciones de nivel de módulo) — mapeados por `ToAttributeTarget` (`src/Surtr.Compiler/Syntax/Parser.Declarations.cs:530-539`) al enum flags `SurtrAttributeTargets` (`src/Surtr.Compiler/Syntax/Ast/DeclarationSyntax.cs:412-434`). Lista vacía entre paréntesis es error deliberado (`Parser.Declarations.cs:487-491`); ausencia de lista = sin restricción.
- **Retención** (máximo una vez): `CompileTimeOnly` — el uso se comprueba y pliega pero **nunca llega a la imagen** — frente al defecto `Runtime`.

Los tres datos viajan en `TypeDeclarationSyntax`: `IsAttribute`, `SurtrAttributeTargets`, `IsCompileTimeOnlyAttribute` (`src/Surtr.Compiler/Syntax/Ast/DeclarationSyntax.cs:349-366`).

Ejemplos canónicos (`docs/Language-Syntax.md:2924-2933`):

```surtr
attribute class Obsolete {
    public let reason: string = "";
}

attribute(Method, Property) class Range {
    public let lo: int = 0;
    public let hi: int = 0;
}

attribute(CompileTimeOnly, Method) class Todo { }
```

Equivale a escribir `class Foo : Attribute { ... }` a mano: el binder infiere la base `Attribute` si no se escribió, y exige que una base explícita extienda `Attribute` (`src/Surtr.Compiler/Binding/Binder.cs:1746-1770`, con el chequeo `ExtendsAttribute`); en caso contrario diagnostico `InvalidAttribute`. Los flags quedan en el símbolo: `NamedTypeSymbol.IsAttribute`, `.AllowedAttributeTargets`, `.IsCompileTimeOnlyAttribute` (`src/Surtr.Compiler/Binding/Symbols/NamedTypeSymbol.cs:230-261`).

### 2.4 Binding de los usos

Todo el trabajo está concentrado en dos métodos del `Binder`:

- `RecordAttributes` (`src/Surtr.Compiler/Binding/Binder.cs:3089-3093`): difiere la resolución; se invoca para tipos, campos, propiedades, métodos, constructores y miembros de extension (unas doce llamadas: `Binder.cs:494, 2404, 2564, 3514, 3533, 3595, 3653, 3694, 3714, 3873, 3900, 4031, 4111`).
- `BindAttributes` (`src/Surtr.Compiler/Binding/Binder.cs:3111-3177`), una vez que todos los tipos existen:
  1. Resuelve el nombre escrito como `NamedTypeSyntax` sintético contra el scope del sitio de uso (`Binder.cs:3117-3120`).
  2. Exige que la clase resuelta extienda `Attribute` caminando la cadena de bases (`ExtendsAttribute`, `Binder.cs:3179-3188`); si no, error `InvalidAttribute` (=3040, `src/Surtr.Compiler/Diagnostics/SurtrDiagnosticCode.cs:280-283`).
  3. Si la clase de atributo declaró targets, compara contra el tipo de la declaración anfitriona (`DeclarationTargetOf`, `Binder.cs:3196-3205`); si no coincide, error `AttributeTargetMismatch` (=3052, `SurtrDiagnosticCode.cs:381-385`). Un target no puede nombrar módulo, alias, parámetro ni local — esos símbolos devuelven `None` y jamás casan (`Binder.cs:3190-3195`).
  4. Pliega **cada argumento a constante** con `Constants.TryEvaluate` (`Binder.cs:3151-3169`); un argumento no constante produce `NotAConstant` (=3032) incluso en atributos `CompileTimeOnly`.
  5. Si todo cuadra, guarda `new AttributeUse(type, arguments)` en `binding.Target.Attributes`.

Decisiones de diseño relevantes documentadas en el propio binder:

- Un atributo que no resuelve se **reporta y descarta**, sin invalidar la declaración que lo porta ("§11's audience is tooling and host reflection rather than the program's own meaning", `Binder.cs:3104-3109`).
- El resultado tipado vive en `Symbol.Attributes` — propiedad de la base de **todos** los símbolos (`src/Surtr.Compiler/Binding/Symbols/Symbol.cs:95`) — como `AttributeUse { Type: NamedTypeSymbol, Arguments: IReadOnlyList<object?> }` (`Symbol.cs:120-133`), ya plegados porque el plegado necesita el evaluador de constantes, que es un hecho de binding.

### 2.5 Emisión a metadata e imagen

- `ModuleEmitter.Attach` (`src/Surtr.Compiler/CodeGen/ModuleEmitter.cs:647-656`) copia cada `AttributeUse` a la metadata con `member.AddAttribute(...)`, **saltando los `IsCompileTimeOnlyAttribute`** (`ModuleEmitter.cs:651-652`). El mismo filtro está replicado inline para propiedades, constructores, métodos y funciones (`ModuleEmitter.cs:686-692, 772-777, 845-850, 1084-1089, 1167-1172`).
- Cada uso viaja como `(descriptor de la clase de atributo, SurtrConstant[])`: `Usage` (`ModuleEmitter.cs:658-665`) y `Constant` (`ModuleEmitter.cs:1248-1259`). Los tipos de constante que la metadata sabe cargar son exactamente: `null`, `int` (también `long`, recortado), `double`, `bool`, `char`, `string`. Cualquier otra cosa es error de emisión.
- La serialización: `WriteAttributes` (`src/Surtr.Core/Bytecode/Image/SurtrModuleImageWriter.cs:458-473`) escribe count + (descriptor internado + count de args + constantes) por uso; se aplica a campos (`:322`), propiedades (`:339`), métodos (`:412`), **al propio tipo** (`:493`, añadido en la versión 2 del formato porque `SurtrTypeInfo` extiende `SurtrMemberInfo`; `docs/Module-Format.md:90-93`) y a interfaces/contratos (`:597`). El lector simétrico está en `src/Surtr.Core/Bytecode/Image/SurtrModuleImageReader.cs:542-570` (con una cola pendiente para propiedades, `:316-350`). Formato actual: v8 (`docs/Module-Format.md:83`).
- El desensamblador imprime los usos tal cual `@Nombre(args)` (`src/Surtr.Core/Bytecode/Emit/SurtrBytecodeDisassembler.cs:331-354`), lo que hace el formato inspeccionable.

### 2.6 Runtime: materialización y lectura desde el host

- Al cargar un módulo, `SurtrRuntime.MaterializeAttributes` (`src/Surtr.Core/Runtime/SurtrRuntime.cs:1078-1156`) construye **una instancia real por uso**, junto con el resto de statics del módulo:
  - Verifica que la clase resuelta derive de `SurtrBuiltIns.Attribute` (`SurtrRuntime.cs:1138`; la raíz abstracta se declara en `src/Surtr.Core/Runtime/BuiltIns/SurtrBuiltIns.cs:331`).
  - Instancia `SurtrInstance`, lo registra y **enraíza permanentemente** (la metadata vive lo que el runtime).
  - Rellena los campos **posicionalmente** desde las constantes materializadas, sin ejecutar bytecode (`SurtrRuntime.cs:1150-1151`); si hay más argumentos que slots lanza (`:1146-1148`). Los módulos en sí no llevan atributos (`:1080-1081`).
- El almacenamiento: `SurtrAttributeUsage { AttributeType: SurtrTypeHandle, Arguments: SurtrConstant[], Instance: SurtrRef }` (`src/Surtr.Core/Runtime/Classes/SurtrAttributeUsage.cs:28-64`) y la lista sobre `SurtrMemberInfo.Attributes` (`src/Surtr.Core/Runtime/Classes/SurtrMemberInfo.cs:78, 141-145`), con `AddAttribute` rechazando miembros ya construidos (`:149-160`) y la búsqueda `TryGetAttribute(clase, out uso)` por identidad de clase (`:166-179`).
- Lectura host-side directa: `member.Attributes` (span) o `TryGetAttribute`. Es la puerta por la que un host Unity, por ejemplo, expondría un campo al inspector.

### 2.7 Reflexión desde los propios scripts Surtr

La API de reflexión (§13.5 de `docs/Language-Syntax.md:3130-3216`, implementada en `src/Surtr.Core/Runtime/BuiltIns/SurtrReflectionBuiltIns.cs`) expone los atributos a los scripts:

- `Type.attributes(): Attribute[]` (`SurtrReflectionBuiltIns.cs:67`, impl `:276-277`) y `Member.attributes(): Attribute[]` (`:78`, impl `:333-334`).
- `WrapAttributes` (`:354-361`) devuelve las instancias **ya vivas** (no construye nada: la materialización pasó en carga). Como comenta la cabecera (`:23-27`), un atributo `CompileTimeOnly` jamás llega a `SurtrMemberInfo.Attributes` porque el emitter ni lo emite: no hay nada que filtrar aquí.
- El objeto devuelto es la instancia real de la clase de atributo, así que se lee con un cast normal y acceso a campos: `m.attributes()[0] as Range` y luego `.lo` / `.hi` (`docs/Language-Syntax.md:3212-3216`).

```surtr
let t = Type.of(someValue);
for (m in t.members()) {
    for (a in m.attributes()) {
        // a as Range, etc. — instancia ya construida
    }
}
```

### 2.8 Tooling: LSP

El LSP marca el keyword contextual `attribute` como modificador leyendo el AST (`type.IsAttribute` en `src/Surtr.LanguageServer/Workspace/SemanticTokensProvider.cs:446-450`). No hay tratamiento especial de los usos `@Nombre` más allá del coloreado general de tokens.

### 2.9 El lado C#: `Surtr.Interop.Attributes` + SourceGenerator

Proyecto sin dependencias, netstandard2.0, pensado para que el host compile contra él (`docs/Guia-Interop-Surtr-Csharp.md:20`). Inventario completo:

| Atributo | Targets CLR | Propiedades | Papel |
|---|---|---|---|
| `[SurtrNativeType]` | Class, Struct, Enum (`AllowMultiple`) | `Module`, `Name`, `Description`, `NamingPolicy`, `TypeArguments` (formas cerradas de genéricos), `Inline` (struct como value type Surtr con slots contiguos) | Marca un tipo CLR para exposición; todos los miembros públicos se exponen con metadata derivada de la firma C# (`src/Surtr.Interop.Attributes/SurtrNativeTypeAttribute.cs:23-80`) |
| `[SurtrNativeMember]` (base) | Method, Constructor, Field, Property | `Name`, `Description`, `Visibility`, `NamingPolicy`, `Expose` | Override puntual de metadata (`SurtrNativeMemberAttribute.cs:11-41`) |
| `[SurtrNativeMethod]` | Method, Constructor | heredados + `ReturnDescriptor` | Override de método (`SurtrNativeMethodAttribute.cs:10-18`) |
| `[SurtrNativeField]` | Field | heredados + `ReadOnly`, `TypeDescriptor` | Override de campo (`SurtrNativeFieldAttribute.cs:10-23`) |
| `[SurtrNativeProperty]` | Property | heredados + `TypeDescriptor` | Override de propiedad (`SurtrNativePropertyAttribute.cs:12-19`) |
| `[SurtrNativeParameter]` | **Parameter** | `Name`, `Description`, `TypeDescriptor` | Override por parámetro (`SurtrNativeParameterAttribute.cs:10-27`) |
| `[SurtrNativeConstructor]` | Method (estático público) | `Description` | Fábrica estática expuesta como constructor de un value type inline (`SurtrNativeConstructorAttribute.cs:26-30`) |
| `[SurtrNativeIgnore]` | Method, Constructor, Field, Property | — | Oculta un miembro (`SurtrNativeIgnoreAttribute.cs:11-15`) |

Enums de soporte: `SurtrNamingPolicy` (Default/Surtr/PascalCase/CamelCase/SnakeCase/LowerCase/UpperCase, `SurtrNamingPolicy.cs:8-35`) y `SurtrInteropVisibility` (espejo byte de la visibilidad Surtr, `SurtrInteropVisibility.cs:9-22`).

**Dos rutas de consumo equivalentes:**

1. **Source generator** (`src/Surtr.Interop.SourceGenerator/SurtrSourceGenerator.cs:19-68`): `ISourceGenerator` clásico con `SyntaxReceiver`; encuentra los tipos con `[SurtrNativeType]`, expande formas cerradas genéricas, valida, y emite (a) `SurtrGenerated.Bindings.g.cs` con `SurtrBindings.RegisterAll(SurtrRuntime)` y (b) un shim partial por tipo. Reconoce los atributos **por nombre completo en texto** (`GeneratorSupport.cs:15-23`) y duplica a mano las políticas de nombres (`GeneratorSupport.cs:33-58`) porque un generador no puede llamar al runtime. Es la ruta AOT-friendly: cero reflexión en build final.
2. **Escáner por reflexión**: `SurtrBridge.ScanAndRegister` (`src/Surtr.Interop/SurtrBridge.cs:127,142`) y `SurtrReflectionScanner`, que lee los mismos atributos con `GetCustomAttribute` y construye los mismos descriptores que el generador (`src/Surtr.Interop/SurtrReflectionScanner.cs:14`, usos por todo el fichero: `:186, :301, :574...`). Es la ruta de conveniencia para editor/desarrollo (`docs/analysis/Interop-Atributos-SourceGenerators.md` recomienda A para producción, B como modo editor).

En ambas rutas los atributos **se consumen enteramente en tiempo de registro del host**: producen llamadas a `DefineNativeClass`/`DefineNativeMethod`/`DefineNativeField`..., entry points por link name y conversiones CLR↔Surtr. No llegan "dentro" de la imagen Surtr como `SurtrAttributeUsage`; son semilla del alta, no metadata consultable desde scripts.

### 2.10 Respuestas a las preguntas clave

- **¿Son consultables en runtime desde scripts (reflexión)?** Sí, si su retención es `Runtime` (el defecto): `Type.attributes()` / `Member.attributes()` devuelven instancias reales ya materializadas (§2.7). Con `CompileTimeOnly`, no existen fuera del compilador.
- **¿Solo afectan a la compilación?** Hoy, efectivamente sí *en la práctica*: el pipeline completo existe (binding, imagen, runtime, reflexión), pero **ningún atributo cambia aún el significado o el diagnóstico del programa**. El compilador valida y transporta; nadie consume semánticamente.
- **¿Sobre qué se pueden aplicar?** Tipos (clase/value class/singleton, interface, enum), campos (incluidos module-level), propiedades y métodos (incluidos constructores y funciones de módulo) — el conjunto que `DeclarationTargetOf` distingue (`Binder.cs:3196-3205`). **No** se pueden aplicar a parámetros, variables locales, alias, bloques `extension` ni al módulo: `ParameterSyntax` no tiene lista de atributos (`DeclarationSyntax.cs:146-174`), `ParseParameterList` no los lee (`Parser.Declarations.cs:948-997`) y el comentario del binder lo dice explícito (`Binder.cs:3190-3195`). Nótese la discrepancia: `docs/Language-Syntax.md:2909` menciona "parameter" como objetivo posible, pero el plan de implementación lo dejó fuera deliberadamente (`docs/Plan-Sintaxis-Imports-Atributos-LSP.md:342`) — gap doc↔código pendiente de cerrar en una u otra dirección.
- **¿Qué tipos de argumentos admite un uso?** Solo constantes plegables, y la imagen solo sabe transportar `null/int/float/bool/char/string` (`ModuleEmitter.cs:1248-1259`). Ni arrays, ni tipos como argumento, ni argumentos nombrados.

---

## 3. Conclusiones: limitaciones actuales y puntos de extensión

### 3.1 Limitaciones

1. **Vocabulario vacío**: no existe ningún atributo definido por el lenguaje; ni siquiera un paquete estándar en `surtr` o `Surtr.Stdlib` (verificado: ninguna aparición de `attribute class` en `src/Surtr.Stdlib` ni `src/Surtr.Core` más allá de la raíz `Attribute`).
2. **Sin efectos semánticos en el compilador**: `BindAttributes` valida forma (tipo, targets, constantes) pero ningún pase posterior consulta `Symbol.Attributes`. No hay warnings por atributo, ni cambios de generación de código, ni influencia en overload resolution, flow analysis ni optimizador.
3. **Validación de uso débil respecto a la clase de atributo**: no se comprueba aridad ni tipos de los argumentos contra los campos declarados del atributo en compilación; un exceso de argumentos solo revienta en **carga** con una excepción (`SurtrRuntime.cs:1146-1148`), y un atributo sin campos acepta cualquier lista de constantes.
4. **Chequeo de targets incompleto entre imágenes**: un atributo importado de una imagen ya compilada no se valida contra sus targets en el sitio de uso, solo en su declaración (`docs/Language-Syntax.md:2956-2959`); el binder confía en `ExtendsAttribute` por nombre de cadena (`Binder.cs:3179-3188`, comparación ordinal por nombre `"Attribute"`).
5. **Solo argumentos posicionales**; sin `AllowMultiple` ni semántica de duplicados (aplicar el mismo atributo dos veces simplemente añade dos usos); sin herencia de atributos a overrides (no existe el análogo de `Inherited = true`).
6. **Superficie de aplicación limitada**: nada de parámetros, locals, enum cases (`EnumCaseSyntax` lleva doc comment pero no atributos, `DeclarationSyntax.cs:477-500`), bloques `extension` ni módulos (decidido así en carga: `SurtrRuntime.cs:1080-1081`).
7. **Argumentos de tipos limitados** en metadata (§2.10): sin arrays ni referencias a tipos como constantes de atributo.
8. En el lado C#, el generador duplica reglas del runtime a mano (`GeneratorSupport.cs:9-11`) y usa la API antigua `ISourceGenerator` con receiver de sintaxis, no `IIncrementalGenerator` como preveía el plan (`docs/Plan-Bridge-CSharp-Atributos-SourceGenerator.md:144`).

### 3.2 Puntos de extensión (dónde enchufar atributos built-in)

- **Un único punto de validación/consumo temprano**: `BindAttributes` (`Binder.cs:3111`) ya tiene el `AttributeUse` con tipo y constantes plegadas. Reconocer ahí clases built-in conocidas (o mejor: reconocer por estructura, p. ej. extensión de una clase built-in `ObsoleteAttribute` en el módulo raíz) y anotar el símbolo con flags semánticos.
- **`Symbol` ya es universal**: cualquier símbolo porta `Attributes` (`Symbol.cs:95`); añadir propiedades derivadas (`IsObsolete`, `MustUseResult`...) es trivial y visible para todos los passes.
- **Warnings**: la infraestructura existe y está en uso — `SurtrDiagnosticBag.ReportWarning` (`SurtrDiagnosticBag.cs:76-83`) y precedentes como `GeneratorNeverYields` (`BodyBinder.cs:165-172`). Un `@Obsolete` es cuestión de avisar donde se resuelve un nombre.
- **Flow analysis**: `src/Surtr.Compiler/Binding/FlowAnalysis.cs` es el lugar natural para un `@NoDiscard` sobre expresiones de llamada en posición de sentencia.
- **CodeGen**: `ModuleEmitter.Attach` demuestra el patrón de decisión por atributo en emisión; un `@Range` con checks o un `@Value` con miembros sintéticos seguirían ese camino.
- **Retención ya resuelve el ciclo de vida**: `CompileTimeOnly` es perfecto para atributos que solo interesan al compilador (coste cero en la imagen), `Runtime` para los que debe leer el host o el script.
- **Reflexión script-side ya operativa**: cualquier atributo `Runtime` queda automáticamente visible para harnesses escritos en Surtr (p. ej. un runner de tests) sin trabajo adicional.

---

## 4. Inspiración de otros lenguajes

| Lenguaje | Mecanismo | Ejemplos relevantes | Lección para Surtr |
|---|---|---|---|
| C# | Atributos CLR + reflexión | `[Obsolete(msg)]` (warning del compilador, con override `#pragma`), `[CallerMemberName]` (relleno por el compilador), `[MethodImpl(AggressiveInlining)]` (codegen) | El atributo más rentable es el que produce un warning; el compilador debe poder *consumir* lo que declara |
| Java | Anotaciones | `@Override` (error si no sobrescribe), `@Deprecated` (warning + info en tooling), `@FunctionalInterface` (verificación estructural) | Las anotaciones útiles verifican invariantes declarativas en compile time |
| Kotlin | Anotaciones + modifiers | `@JvmStatic`/`@JvmOverloads` (afectan la superficie generada), `@DslMarker` (reglas de scope) | Un atributo puede gobernar la generación de miembros/superficie |
| Rust | Atributos built-in reales | `#[derive(Eq, Hash)]` (genera impls), `#[must_use]` (warning si se descarta el resultado), `#[inline]`, `#[deprecated]`, `#[cfg]` | `#[must_use]` y `#[derive]` son los dos patrones con mejor relación valor/coste; `#[cfg]` ya lo cubre `const if` en Surtr |

Mapeo rápido con lo que Surtr **ya** cubre por otras vías (importante para no proponer redundancias): inlining es modificador (`inline`/`forceinline`/`noinline`, §3.6, `DeclarationSyntax.cs:43-60`), código condicional es `const if` (§7.3), implementación nativa es el keyword `native` (§10), valores por defecto son parte de la gramática (§3.5). Véase §6.

---

## 5. Propuestas de atributos built-in

Convenciones comunes a todas las propuestas:

- Se declaran como clases Surtr normales con el keyword `attribute`, residentes del módulo built-in `surtr` (implícitamente importado, igual que `Exception` o `Attribute`), de modo que **el propio lenguaje define su vocabulario con su propia mecánica** — cero casos especiales en parser o binder más allá del reconocimiento.
- Donde el compilador debe darles significado, el reconocimiento puede hacerse por nombre cualificado en el módulo raíz (`surtr:Obsolete`), igual que hoy `ExtendsAttribute` reconoce `"Attribute"` por nombre (`Binder.cs:3183`).
- Todas admiten la lectura por reflexión salvo las marcadas `CompileTimeOnly`, que existen solo para el compilador.

---

### P1. `@Obsolete` — obsolescencia con warning del compilador

- **Declaración built-in**:
  ```surtr
  attribute(Class, Interface, Enum, Field, Property, Method)
  class Obsolete {
      public let reason: string = "";
  }
  ```
  Retención `Runtime` (que el host también pueda informar de APIs viejas).
- **Objetivos permitidos**: clase, interface, enum, campo, propiedad, método (incluye funciones de módulo y constructores vía `Method`).
- **Parámetros**: `reason: string` opcional (mensaje explicativo; admite "use X en su lugar").
- **Semántica**:
  1. *Compilador*: toda referencia vinculada a un símbolo marcado (lectura de campo/propiedad, llamada, uso de tipo) produce **warning** (código nuevo, p. ej. `ObsoleteMemberUsed`) apuntando al sitio de uso, con el mensaje como texto. El punto de enganche natural es la resolución de nombres en `BodyBinder.Expressions`/`TypeResolver`, consultando un flag derivado de `Symbol.Attributes`.
  2. *Overload resolution*: entre candidatos igual de válidos, se prefiere el no marcado; solo si no queda alternativa se elige el obsoleto (y avisa) — comportamiento C#.
  3. *LSP*: el warning aparece como squiggle y en quick-fix/hover.
- **Consumidores**: compilador (warning + overload), LSP, host (metadata), scripts (reflexión).
- **Ejemplo**:
  ```surtr
  @Obsolete("use moveTo(dx, dy)")
  public fun move(x: float, y: float): void { ... }

  fun update(): void {
      move(1.0, 2.0);   // warning: 'move' is obsolete: use moveTo(dx, dy)
  }
  ```
- **Coste**: bajo. Toda la infraestructura existe (`ReportWarning`, `Symbol.Attributes`, precedentes en `BodyBinder.cs:167`).
- **Por qué primero**: es el patrón con mejor relación valor/coste de todos los lenguajes estudiados y el que hace que el resto del vocabulario built-in tenga sentido: convierte los atributos en algo que afecta a la compilación.

### P2. `@NoDiscard` — resultado obligatorio de usar

- **Declaración**:
  ```surtr
  attribute(Method) class NoDiscard {
      public let reason: string = "";
  }
  ```
  Retención `Runtime` (documental también para el host).
- **Objetivos**: métodos y funciones de módulo que devuelven valor.
- **Parámetros**: `reason` opcional.
- **Semántica**: si una llamada a la función aparece como *statement expression* y el valor devuelto no se usa, el compilador emite **warning** (`UnusedReturnValue`-style). No es error (se puede querer ignorar deliberadamente); una idiomática de descarte explícito (`let _ = f();` si existiera, o asignación) callaría el aviso. Punto de implementación: `FlowAnalysis` (`src/Surtr.Compiler/Binding/FlowAnalysis.cs`), que ya recorre statements.
- **Consumidores**: compilador (warning), LSP.
- **Ejemplo**:
  ```surtr
  @NoDiscard("el Result indica si parseó")
  fun tryParse(text: string): Result { ... }

  fun load(): void {
      tryParse(input);            // warning: el resultado de 'tryParse' debería usarse
      let ok = tryParse(input);   // bien
  }
  ```
- **Coste**: bajo-medio (detección de statement-expressions de llamada ya existe conceptualmente en flow analysis).
- **Patrón de referencia**: Rust `#[must_use]`.

### P3. `@Pure` — contrato de pureza para optimizador y tooling

- **Declaración**:
  ```surtr
  attribute(Method, Property) class Pure { }
  ```
- **Objetivos**: métodos y funciones de módulo; en propiedades aplica a sus accessors.
- **Parámetros**: ninguno.
- **Semántica**: declara que la función no muta estado observable ni hace IO, y que a iguales argumentos devuelve igual resultado (transparencia referencial). Fases:
  1. *(mínimo)* Metadata `Runtime` + hover LSP ("pure function").
  2. *(verificación)* Warning si el cuerpo llama a funciones no marcadas como puras, asigna campos accesibles exteriormente o usa `native` impuro — análisis local barato.
  3. *(optimización)* Habilita plegado/CSE de llamadas con argumentos constantes entre funciones (hoy `ConstFolder`/const-folding se limita al cuerpo), y reordenamiento seguro.
- **Consumidores**: optimizador del compilador (`src/Surtr.Compiler/CodeGen/ConstFolder.cs`, `InlineCost.cs`), LSP, documentación automática.
- **Ejemplo**:
  ```surtr
  @Pure
  fun clamp01(x: float): float { return Math.max(0.0, Math.min(1.0, x)); }
  ```
- **Coste**: medio (el beneficio pleno depende del optimizador; las fases 1-2 son inmediatas).
- **Nota**: complementa, no sustituye, `inline` (§3.6): `@Pure` describe *semántica*, `forceinline` describe *estrategia*.

> **Estado (implementado):** fases 1, 2 y 3 completas. Fase 1 (metadata + hover) y fase 2 (verificación): el compilador emite `PureContractViolated` (3081) cuando un cuerpo `@Pure` llama a una función sin la marca o escribe un campo/propiedad `public`/`internal`. Las funciones puras de la stdlib de *source* llevan `@Pure` (todo `Math.*`, el getter de `Angle.radians`); los built-ins C# puros se marcan **en el propio C#** (`isPure: true` en `SurtrBuiltInTypeBuilder` → `SurtrMethodInfo.IsPure` → el importer adjunta el `AttributeUse` "Pure" → `MemberLookup` lo propaga a las vistas sustituidas): strings, lecturas de colecciones (nunca mutadores como `push`/`set`), primitivas/matemáticas y `char`, `Exception.message`, reflexión (`Type`/`Member`/`Module`), contratos (`equals`/`compareTo`) y lecturas de iterador/generador (`current`, `result` — nunca `moveNext`/`send`/`raise`). Un cuerpo `@Pure` que llama a un native puro ya no avisa, y una función que los alcanza es plegable (el native corre en el runtime scratch). Fase 3 (plegado + CSE): el `ConstFolder` acepta funciones `@Pure` y `Complete` sustituye `f(argsConst)` por su resultado evaluado en runtime scratch. El gate (`PureFoldVerifier`) es un **fixed-point transitivo** (un callee impuro descualifica a sus callers; un native puro se trata como trusted). El **CSE** cubre mismo-expresión (`f(x)+f(x)`, `g(f(x),f(x))` vía `BoundSequenceExpression`) y **entre statements con dominancia completa** (`CrossStatementCse`): un valor disponible antes de `if`/`switch`/`try`/loop sobrevive al cruce (join estructurado) y se reutiliza después, salvo que un cuerpo escriba uno de sus operandos (kills por subárbol); dentro de un cuerpo de loop no se reutilizan valores pre-loop (back-edge). No queda nada pendiente salvo extensiones futuras (plegado de chains más allá de fixed-point, CSE de calls anidadas en expresiones arbitrarias).

### P4. `@Range(lo, hi)` — validación declarativa de rangos

- **Declaración**: la que el propio manual usa como ejemplo (`docs/Language-Syntax.md:2928-2931`):
  ```surtr
  attribute(Field, Property) class Range {
      public let lo: float = 0.0;
      public let hi: float = 0.0;
  }
  ```
- **Objetivos**: campos y propiedades numéricas (int/float).
- **Parámetros**: `lo`, `hi` posicionales.
- **Semántica**:
  1. *(metadata)* Legible por el host/inspector de Unity para dibujar sliders — es literalmente el caso de uso que §11 nombra como audiencia (`docs/Language-Syntax.md:2910-2912`).
  2. *(opcional, modo checks)* El compilador inserta comprobación en cada asignación al campo/property-set en builds con verificaciones activadas, lanzando una excepción con mensaje formateado; en release, coste cero.
- **Consumidores**: host (inspector), compilador (checks opcionales), scripts (reflexión).
- **Ejemplo**:
  ```surtr
  class Player {
      @Range(0.0, 100.0)
      public var health: float = 100.0;

      @Range(1, 8)
      public var bounces: int = 3;
  }
  ```
- **Coste**: bajo (fase 1); medio (fase 2).
- **Ventaja estratégica**: es el ejemplo canónico de la sección §11; implementarlo valida la historia completa compilador→imagen→host.

> **Estado (implementado):** fases 1 y 2 completas. Fase 2: en builds que definen la constante `Debug` (`SurtrProject.Define("Debug", ...)`), el binder reescribe cada asignación a un campo o property-set con `@Range` en una secuencia que captura el valor en un temporal, lanza `ArgumentOutOfRangeException` si cae fuera de `[lo, hi]` (mensaje con el nombre del miembro y los límites), y solo entonces escribe. Sin `Debug`, coste cero (no se inserta nada). El tipo `ArgumentOutOfRangeException` se añadió a la stdlib como subclase de `ArgumentException`. Los **field initializers** (instancia y static) se comprueban ahora: `BindInitializer` envuelve el valor en el guard vía el nodo nuevo `BoundSequenceExpression`, que también permite cubrir **asignaciones anidadas** (`x = (p.health = v) + 1`), compuestas (`+=`) e initializers de `for` — todo con el RHS evaluado exactamente una vez. La comprobación está centralizada en `BindAssignment`/`RangeCheckValue`.

### P5. `@Export` — superficie expuesta al host/inspector

- **Declaración**:
  ```surtr
  attribute(Class, Field, Property) class Export {
      public let name: string = "";
  }
  ```
- **Objetivos**: clases (exportar el tipo entero como superficie host), campos y propiedades individuales.
- **Parámetros**: `name` opcional (alias de exposición distinto del nombre Surtr).
- **Semántica**: marca qué parte de un módulo Surtr está destinada a ser consumida por el host incrustante (inspector de Unity, editor, bindings automáticos). No cambia la visibilidad del lenguaje (`public`/`internal` siguen mandando); es un contrato *adicional* consultable: el host recorre `moduleof(...)`/`Type.members()` y filtra por `TryGetAttribute(ExportClass)`. Espejo language-side del par `[SurtrNativeType]`/`[SerializeField]` del mundo C#.
- **Consumidores**: host (descubrimiento de superficie, inspector), LSP (marcado visual de API exportada), scripts (reflexión).
- **Ejemplo**:
  ```surtr
  @Export
  class Enemy {
      @Export("hitPoints")
      public var health: float = 10.0;

      internal var tempCalc: float = 0.0;   // no exportado
  }
  ```
- **Coste**: casi nulo en compilador (solo metadata); el trabajo real es del host consumidor.
- **Dependencia**: definir junto a la integración Unity/embedding (coherente con `docs/Guia-Interop-Surtr-Csharp.md`).

### P6. `@Value` — derivación de igualdad/hash/tostring estructural (estilo Rust `#[derive]`)

- **Declaración**:
  ```surtr
  attribute(CompileTimeOnly, Class) class Value { }
  ```
- **Objetivos**: clases (especialmente `value class`).
- **Parámetros**: ninguno (futuro: lista de miembros a incluir/excluir).
- **Semántica**: el compilador **genera miembros sintéticos** (prefijo `$`, la convención ABI ya establecida que la reflexión oculta, `SurtrReflectionBuiltIns.cs:294-302`): igualdad campo a campo, hash combinado, `operator==`/`!=` coherentes y `toDisplayString()` estructural — salvo que la clase declare los suyos, en cuyo caso gana lo declarado. Cierra directamente la opción ya esbozada en `docs/Plan-ClaseBase-Equals-HashCode.md:91` ("atributo `[value]` o declarar `equals`"). Retención `CompileTimeOnly`: es azúcar de compilación, nada que leer en runtime.
- **Consumidores**: exclusivamente el compilador (binder/codegen); efecto observable en la semántica del programa.
- **Ejemplo**:
  ```surtr
  @Value
  class Vec2 {
      public let x: float = 0.0;
      public let y: float = 0.0;
  }

  fun same(): bool {
      return Vec2(1.0, 2.0) == Vec2(1.0, 2.0);   // true: igualdad estructural generada
  }
  ```
- **Coste**: medio-alto (generación de miembros, integración con dispatch/tablas de métodos), pero es la propuesta de mayor ganancia ergonómica y elimina una fuente clásica de bugs (igualdad por identidad en tipos-valor conceptuales).

### P7. `@Test` / `@TestSuite` — descubrimiento de pruebas

- **Declaración**:
  ```surtr
  attribute(Method) class Test {
      public let name: string = "";
  }

  attribute(Class) class TestSuite {
      public let name: string = "";
  }
  ```
- **Objetivos**: métodos sin parámetros (`Test`); clases contenedoras (`TestSuite`).
- **Parámetros**: nombre legible opcional.
- **Semántica**: un harness (CLI `Surtr.Cli`, o el LSP como test explorer) enumera módulos cargados, recorre `Type.members()` y filtra por el atributo, ejecuta cada `Test` y reporta excepciones como fallos. Todo el descubrimiento se apoya **únicamente** en la reflexión existente (`Type.get`, `members()`, `attributes()`) — cero cambios de compilador si el runner es host-side; un runner escrito en Surtr también es viable hoy con la misma API.
- **Consumidores**: harness/tests (principal), LSP (decoración), CI.
- **Ejemplo**:
  ```surtr
  @TestSuite("Vec2")
  class Vec2Tests {
      @Test("suma componentes")
      fun sumWorks(): void {
          assert(Vec2(1.0, 2.0).add(Vec2(3.0, 4.0)) == Vec2(4.0, 6.0));
      }
  }
  ```
- **Coste**: nulo en compilador; el coste es construir el runner. Valor alto para el propio proyecto (los tests de stdlib dejarían de ser solo C#).

### P8. `@MainThread` / `@ThreadSafe` — contratos de concurrencia

- **Declaración**:
  ```surtr
  attribute(Method, Property, Class) class MainThread { }
  attribute(Method, Class) class ThreadSafe { }
  ```
- **Objetivos**: métodos, propiedades (sus accessors), clases.
- **Parámetros**: ninguno.
- **Semántica**: contratos de hilo documentales y verificables:
  1. *(fase 1)* Metadata + hover LSP; el host (Unity) puede usarla para decidir scheduling/asserts.
  2. *(fase 2)* Lint del compilador cuando exista análisis de contexto de ejecución: llamar a un `@MainThread` desde código que el runtime marca como off-thread produce warning. Mientras tanto, sirve de contrato auditado en revisión.
- **Consumidores**: host (scheduler/asserts), compilador (lint futuro), LSP, documentación.
- **Ejemplo**:
  ```surtr
  @MainThread
  fun draw(ui: Canvas): void { ... }

  @ThreadSafe
  fun pathfind(map: Grid, from: Vec2, to: Vec2): Path[] { ... }
  ```
- **Coste**: bajo (fase 1); alto (fase 2, requiere modelo de hilos del runtime). Priorizar como documental.

---

### P9. `@TestIgnore` — el test que se descubre pero no corre

- **Declaración built-in**: `attribute(Method) class TestIgnore { public let reason: string = ""; }`, con el mismo helper `DeclareReasonAttribute` que usan `@Obsolete` y `@NoDiscard`. Retención `Runtime`.
- **Objetivos permitidos**: método.
- **Semántica**: el runner descubre el método igual que cualquier `@Test`, lo reporta como *skipped* con el motivo escrito, y **no entra en el cuerpo**. Saltarlo en el punto de reporte y no en el descubrimiento es la decisión de diseño: un test saltado que nadie ve es indistinguible de uno borrado.
- **Consumidores**: `SurtrTestRunner` (host), reflexión.

> **Estado (implementado):** completo. `SurtrTestResult` gana `SurtrTestOutcome` de tres estados (`Passed`/`Failed`/`Skipped`) más `SkipReason`; `Passed` queda como propiedad derivada, así que un host que solo contaba booleanos lee lo mismo que antes. Un test ignorado tampoco corre sus fixtures. `AttributeRoleCheck` (archivo nuevo, corre tras el bucle de `BindAttributes` en `BindBodies`) reporta `IgnoreWithoutTest` (3086) cuando la marca va sin `@Test`: sin ese lint el caso falla en silencio, porque el runner sencillamente nunca descubre el método.

---

### P10. `@TestBefore` / `@TestAfter` — fixtures por test

- **Declaración built-in**: dos clases `attribute(Method)` sin campos.
- **Semántica**: se ejecutan antes y después de **cada** test de su ámbito, que es el default estándar y no una vuelta por grupo. Ámbito: una fixture declarada en una clase envuelve los tests de esa clase; una de nivel módulo envuelve todos los tests del módulo, los de sus clases incluidos. Orden: módulo antes que clase a la entrada, clase antes que módulo a la salida.
- **Garantías**: el test y sus fixtures comparten **una sola instancia** — lo único que permite a un `@TestBefore` preparar estado que el test lee. Un `@TestAfter` corre pase lo que pase, incluido tras un `@TestBefore` que lanzó; se reporta el primer fallo, porque un release que falla por culpa del acquire es consecuencia y no causa.

> **Estado (implementado):** completo. Ámbito por clase declarante y **no** heredado por anidadas: una fixture de instancia de la clase externa no podría correr sobre una instancia de la anidada, así que es la única regla coherente para fixtures estáticas y de instancia a la vez (el nombre de suite sí sigue bajando, que es agrupación y no algo a llamar). Un test estático junto a una fixture de instancia también recibe instancia, porque la fixture necesita sobre qué correr. `InvalidTestFixture` (3085) cubre los tres casos que el runner no podría resolver: fixture que además es test, fixture con parámetros que nada llenaría, y fixture que devuelve algo que nadie lee.
>
> **Ampliación explícita de alcance:** el runner ahora descubre también funciones `@Test` **a nivel de módulo**, no solo miembros de clase. Sin eso el ámbito de módulo de las fixtures no tendría tests sueltos que cubrir. Un módulo es el único contenedor de nivel superior (§2.5), así que un `@Test fun` suelto es un test tan ordinario como uno de clase; su suite es la ruta del módulo, al no haber `@TestSuite` que lo nombre.

---

### P11. `@Benchmark` — el hermano de `@Test` que mide

- **Declaración built-in**: `attribute(Method) class Benchmark { }`. Retención `Runtime`.
- **Semántica**: descubrimiento por reflexión igual que `@Test`, pero ejecución repetida con warmup y medición.
- **Consumidores**: `SurtrTestRunner.RunBenchmarks` (host).

> **Estado (implementado):** completo, con **pasada de descubrimiento aparte**: cuánto tarda es otra pregunta que si pasa, y un host normalmente quiere una de las dos — mezclarlas haría que una suite de tests se llevase en silencio cien llamadas cronometradas por benchmark. Por lo mismo `SurtrBenchmarkResult` es tipo propio y no un `SurtrTestResult` con campos extra: meterlo dentro pondría tiempos en cada test que pasa y un pass/fail en cada medición. Reporta mediana (no media, que arrastra el outlier de la llamada que perdió su turno), mínimo, total y ns/op derivado. El receptor de un benchmark de instancia se construye **una vez, antes del warmup**, para no medir construcción ni inicializadores. El warmup existe por lo mismo que el de `Surtr.Bench`: un método sube de tier 0 tras unas decenas de llamadas, así que las primeras miden el JIT.
>
> **Límites v1, documentados:** las fixtures **no** corren alrededor de un benchmark — dentro del bucle se medirían y fuera significarían algo distinto de lo que promete `@TestBefore`; setup por benchmark es un concepto que el vocabulario aún no tiene. Y un benchmark debe compilarse **sin** la constante `Debug` o la medición incluye los checks de `@Range`: es decisión del host al construir, no algo que la marca pueda exigir, así que queda anotada en el doc-comment de la clase. `BenchmarkWithTest` (3087) cierra el rol ambiguo.

---

### P12. `@Throws("Name")` — qué puede lanzar una función

- **Declaración built-in**: `attribute(Method) class Throws { public let name: string = ""; }`, con el helper `DeclareNamedAttribute` que ya usan `@Export`/`@Test`/`@TestSuite`. Retención `Runtime`.
- **Repetible**: sí. Una función que puede lanzar tres cosas lleva tres marcas, cada una con su instancia, porque un campo `name` guarda un nombre.
- **Consumidores**: hover LSP, documentación, reflexión.

> **Estado (implementado):** completo, incluida la validación, que es lo que le da significado real y es barata. El argumento es un literal de texto, que por sí solo no resuelve contra ningún scope, así que una excepción renombrada dejaría la marca apuntando a un nombre que ya no existe y nada que lo dijera. Se resuelve por `TryResolveTypeName` —no `Resolve`— precisamente para que un fallo aquí no reporte un error propio, y se sube por `BaseType` hasta `Exception` comparando por identidad de referencia, que basta porque todo tipo está internado; si `Exception` no resuelve en esa compilación se omite el check en vez de acusar a todas las marcas. `ThrowsTypeNotException` (3084) es **warning**: la marca es documentación y un nombre viejo debe verse sin romper el build. Introduce además el primer lector *colector* del vocabulario (`AllThrows`), frente al `Find` de siempre que devuelve solo el primer uso.
>
> **Hover LSP:** línea `throws X, Y`. Es el único atributo que hover renderiza, y a propósito: lo que una función puede lanzar es información de firma sobre la que un llamante actúa, mientras el resto del vocabulario habla de la declaración y no de cómo llamarla.

---

### P13. `@NoAlloc` — el hermano de `@Pure` en el eje memoria

- **Declaración built-in**: `attribute(Method, Property) class NoAlloc { }` — espejo exacto de los objetivos de `@Pure`. Retención `Runtime` (un host que perfila un build quiere saber qué miembros prometieron qué).
- **Semántica**: promete que el cuerpo no aloca en el heap. Un VM dentro de un presupuesto de frame se juzga tanto por alocación como por tiempo, que es lo que hace que valga la pena poder escribir la promesa.

> **Estado (implementado):** fases 1 y 2 completas. `NoAllocCheck` (archivo nuevo, modelado sobre `ConstFunctionCheck`) corre al final de `BindOne` tras `FlowAnalysis` y solo si la marca está, así que un cuerpo que no prometió nada nunca se recorre. Reporta `AllocationInNoAllocBody` (3082, **warning** como `@Pure`) para: construcción de clase, literal de array, literal de dict, creación de colección, interpolación, concatenación de strings, creación de closure y `yield`. Exento: construir un `value class`, que §2.9 dispone inline.
>
> **Tres límites deliberados**, escritos en el doc-comment porque cada uno es un sitio donde el check calla en vez de estar satisfecho: (1) las llamadas **no se siguen** — `substring` aloca dentro del callee y un recorrido local no lo ve; hacerlo transitivo pediría el punto fijo de `@Pure` *más* una lista curada de natives sin alocación, y `substring` no estaría en ella; (2) las **tuplas se permiten**, por ser value type bajado a slots contiguos; (3) **invocar** una closure se permite y **crearla** no, porque la alocación ocurrió donde se escribió la lambda y ahí es donde va el reporte.

---

### P14. `@Flags` — el único atributo que cambia lo que su objetivo *es*

- **Declaración built-in**: `attribute(Enum) class Flags { }`. Retención `Runtime`.

> **El plan original partía de una premisa falsa, y hubo que rehacer el diseño.** `Plan-Atributos-Segunda-Ola.md` §5 decía «el payload es int»; en Surtr no lo es. Un caso de enum es una **instancia estática** —una referencia— y el ordinal es metadata solo para jump tables, como dice literalmente `SurtrEnumCaseInfo`: *"the cases are references"*. Emitir `Code.And()` sobre dos casos haría AND de dos entity ids: un valor que no nombra ningún caso, no es una referencia válida y corrompería la vista del colector. El lint de potencias de dos tampoco aplicaba: no hay valores escritos que comprobar. Consultado, el usuario eligió la opción grande — que los enums marcados **cambien de representación**.

> **Estado (implementado):** completo salvo un punto declarado abajo. Un enum `@Flags` vale `1 << ordinal` y una variable suya guarda un `int`, exactamente como un `value class` de un solo campo sobre `int` — y **reusa esa erasure ya existente** en vez de inventar concepto nuevo: descriptor `I`, `TypeCodeOf` → `Integer`, `SignatureSet.Erase` → `int` (así `f(Perm)` y `f(int)` colisionan en el binder y no en el linker). Los valores son potencia de dos por construcción, que es mejor que una convención vigilada por un lint.
>
> **La ordenación de fases fue la dificultad real.** La marca se lee de la **sintaxis** en la fase de declaración, no de un `AttributeUse` ligado. Es el único atributo de §11 que lo necesita: la representación que elige la usan la member phase, `SignatureSet` y todo descriptor, y los atributos no ligan hasta `BindBodies`, mucho después. La presencia de un atributo sin argumentos es una pregunta que la sintaxis ya responde — el mismo razonamiento por el que un `const if` de nivel declaración se contesta antes de ligar nada. El uso ligado se registra igual, para imagen y reflexión.
>
> **Operadores:** `&`, `|`, `^` sobre el **mismo** enum marcado dan ese enum, así que `let rw: Perm = Perm.Read | Perm.Write;` es asignación y no cast; los compound (`|=`, `&=`, `^=`) salen gratis vía `Expand()`. `~` da también el enum. Los shifts quedan fuera: el bit lo asigna el compilador, y moverlo da un valor que ningún caso nombra. Combinar dos enums marcados distintos se rechaza, porque no comparten significados de bit.
>
> **Cast explícito a `int` y desde `int`:** no mueve bits pero hay que escribirlo, porque un `int` cualquiera no es una combinación de los casos. Es lo que hace expresable el conjunto vacío (`0 as Perm`) y lo que permite guardar o enviar un set de flags donde solo caben números.
>
> **`contains` es una bajada, no un miembro:** `p.contains(f)` liga a `(p & f) == f`. La representación lo obliga — un valor `@Flags` es un `int` sin instancia detrás, así que un miembro no tendría sobre qué correr y dársela significaría boxear en cada llamada, justo el coste que la marca existe para evitar. El argumento va a un temporal porque el test lo lee dos veces y el programa una.
>
> **`InvalidFlagsEnum` (3083)** —reemplaza al `FlagCaseNotPowerOfTwo` propuesto, que ya no tiene nada que comprobar— exige que un enum marcado sea **plano**: sin miembros, sin interfaces, sin argumentos de constructor en los casos, máximo 31 casos. Es **error** y no warning, a diferencia del resto de §11: no es consejo sobre intención, es que no hay representación donde poner eso.
>
> **Límites documentados:** (1) un enum marcado es una *clase de constantes int* en runtime, no un enum, así que la reflexión del host lo reporta como clase — la misma erasure que paga un `value class`; (2) su descriptor es `I`, así que la flags-ness **no cruza el límite de módulo**, otra vez igual que un `value class`; (3) **fuera de alcance, declarado**: `toString()` de una combinación (imprimir `"Read | Write"`). Necesita resolver nombres en runtime sobre un `int`, en un tipo que no tiene instancia ni casos registrados como enum — es trabajo de otra naturaleza, no una línea más.
>
> **Riesgo asumido y mitigado:** `detect_changes` marcó **CRITICAL** (34 flujos afectados, todos vía `DescriptorEmitter.Append`), lo cual es inherente a cambiar la representación de un tipo. Cada toque en ruta caliente es aditivo y guardado (`case ... when IsFlagsEnum` en `Append` y `TypeCodeOf`; `when !IsFlagsEnum` en `DeclareType`/`DeclareField`), así que el camino de todo lo no marcado queda idéntico. Evidencia: suite completa en verde y los 11 módulos de `Surtr.Stdlib` recompilados desde fuente en cada build, más un test de no-regresión explícito de las ramas `int`/`bool` del bloque bitwise.

---

### P15. Diagnósticos añadidos por esta ola

| Código | Nombre | Severidad | Atributo |
|---|---|---|---|
| 3082 | `AllocationInNoAllocBody` | Warning | `@NoAlloc` |
| 3083 | `InvalidFlagsEnum` | **Error** | `@Flags` |
| 3084 | `ThrowsTypeNotException` | Warning | `@Throws` |
| 3085 | `InvalidTestFixture` | Warning | `@TestBefore`/`@TestAfter` |
| 3086 | `IgnoreWithoutTest` | Warning | `@TestIgnore` |
| 3087 | `BenchmarkWithTest` | Warning | `@Benchmark` |

Todos warnings salvo `InvalidFlagsEnum`, por el motivo de P14: los demás son juicios sobre la intención de quien escribe, y ese es sobre una declaración para la que el compilador no tiene representación.

---

## 6. Candidatas descartadas (redundantes con características existentes)

Analizar los lenguajes de referencia produce varias ideas que Surtr **ya cubre con mecánica propia**, y proponerlas como atributos sería introducir dos formas de decir lo mismo:

| Idea descartada | Ya cubierto por | Referencia |
|---|---|---|
| `@Inline` / `@ForceInline` / `@NoInline` | Modificadores `inline`/`forceinline`/`noinline` (§3.6), con semántica de hint/obligatorio/prohibido ya completa | `src/Surtr.Compiler/Syntax/Ast/DeclarationSyntax.cs:43-60` |
| `@Native` | Keyword `native` (§10) en campos, métodos y propiedades | `docs/Language-Syntax.md` §10 |
| `@Default(T)` para inicialización de campos | Inicializadores de campo/propiedad y defaults de parámetro trailing (§3.5) | Gramática del lenguaje |
| `@Conditional` / `#if` | `const if` a nivel de declaración y statement (§7.3) — el miembro del branch no tomado no existe | `docs/Language-Syntax.md` §7.3 |
| `@Serializable` (serialización de estado) | Subconjunto de `@Export` + value types inline del interop; posponer hasta tener una historia de persistencia | — |
| Hints de overload resolution (`@Priority`) | Reglas deterministas actuales; sin evidencia de necesidad práctica todavía | `src/Surtr.Compiler/Binding/OverloadResolution.cs` |
| `@CallerMemberName` (relleno implícito de argumento) | Requeriría atributos sobre **parámetros**, que el lenguaje no soporta aún; candidato natural para cuando se cierren los gaps de §3.1.6 | `Binder.cs:3190-3195`, `Parser.Declarations.cs:948` |

---

## 7. Priorización

Orden recomendado de implantación, por relación valor/coste y dependencias:

| # | Atributo | Fase sugerida | Justificación |
|---|---|---|---|
| 1 | `@Obsolete` | Primera | Máximo valor, coste mínimo: solo necesita warnings (infraestructura existente) y un flag derivado en `Symbol`. Además establece el *patrón* de atributo consumido por el compilador, que todas las demás seguirán. Útil de inmediato para evolucionar la stdlib. |
| 2 | `@NoDiscard` | Primera | Mismo coste bajo; `FlowAnalysis` ya existe como punto de enganche. Prevención barata de errores con APIs estilo `tryX`/`Result`. |
| 3 | `@Value` | Segunda | Mayor ganancia ergonómica del lote; cierra el plan pendiente de equals/hashCode (`Plan-ClaseBase-Equals-HashCode.md`). Más coste: generación de miembros sintéticos. |
| 4 | `@Range` | Segunda | Barato en fase metadata; valida la historia completa compilador→imagen→host-inspector que §11 declara como audiencia. Decidir entonces la política de checks en debug. |
| 5 | `@Export` | Tercera | Casi gratis en compilador, pero su valor depende del host consumidor; diseñarla junto a la integración Unity. |
| 6 | `@Test`/`@TestSuite` | Tercera | Coste cero en compilador; requiere construir el runner. Alto valor interno (tests de stdlib y de proyectos host). |
| 7 | `@Pure` | Cuarta | Especificar pronto (contrato), materializar el beneficio con el optimizador; las fases 1-2 pueden adelantarse si hay demanda de tooling. |
| 8 | `@MainThread`/`@ThreadSafe` | Cuarta | Documental hasta disponer de modelo de hilos analizable; bajo coste de mantenerlas como metadata desde el principio. |

**Prerrequisito transversal recomendado**: endurecer `BindAttributes` para validar aridad y compatibilidad de tipos de los argumentos contra los campos declarados de la clase de atributo (§3.1.3). Los atributos built-in serán usados masivamente; fallar en carga por un argumento de más sería un error de calidad de compilador inaceptable. Es un cambio localizado en `Binder.cs:3111-3177` que además beneficia a todo atributo de usuario.

---

## 8. Apéndice: referencia rápida del sistema actual

| Aspecto | Estado | Dónde |
|---|---|---|
| Token `@` | `TokenType.At` | `src/Surtr.Compiler/Syntax/Lexer.cs:689` |
| Uso `@Name(args)` posicionales | `ParseAttributes` | `src/Surtr.Compiler/Syntax/Parser.cs:400-431` |
| Nodo de uso | `AttributeSyntax(Name, Arguments)` | `src/Surtr.Compiler/Syntax/Ast/DeclarationSyntax.cs:126-143` |
| Atributos en toda declaración | `DeclarationSyntax.Attributes` | `DeclarationSyntax.cs:103` |
| Keyword contextual `attribute` + targets + retención | `ParseAttributeClassDeclaration` | `src/Surtr.Compiler/Syntax/Parser.Declarations.cs:475-528` |
| Enum de targets | `SurtrAttributeTargets` (flags) | `DeclarationSyntax.cs:412-434` |
| Validación y plegado de usos | `Binder.BindAttributes` | `src/Surtr.Compiler/Binding/Binder.cs:3111-3177` |
| Errores | `InvalidAttribute`=3040, `NotAConstant`=3032, `AttributeTargetMismatch`=3052 | `src/Surtr.Compiler/Diagnostics/SurtrDiagnosticCode.cs` |
| Uso tipado | `Symbol.Attributes` : `AttributeUse(Type, Arguments)` | `src/Surtr.Compiler/Binding/Symbols/Symbol.cs:95,120-133` |
| Emisión (salta `CompileTimeOnly`) | `ModuleEmitter.Attach` | `src/Surtr.Compiler/CodeGen/ModuleEmitter.cs:647-656` |
| Constantes transportables | null/int/long/double/bool/char/string | `ModuleEmitter.cs:1248-1259` |
| Serialización en imagen | `WriteAttributes`/`ReadAttributes` (v8; tipos desde v2) | `SurtrModuleImageWriter.cs:458-473`; `SurtrModuleImageReader.cs:542-570`; `docs/Module-Format.md:90` |
| Raíz de atributos | `SurtrBuiltIns.Attribute` (abstracta) | `src/Surtr.Core/Runtime/BuiltIns/SurtrBuiltIns.cs:331` |
| Materialización en carga (instancias enraizadas) | `SurtrRuntime.MaterializeAttributes` | `src/Surtr.Core/Runtime/SurtrRuntime.cs:1078-1156` |
| Metadata runtime | `SurtrAttributeUsage`, `SurtrMemberInfo.TryGetAttribute` | `src/Surtr.Core/Runtime/Classes/SurtrAttributeUsage.cs`; `SurtrMemberInfo.cs:141-179` |
| Reflexión script-side | `Type.attributes()` / `Member.attributes()` | `src/Surtr.Core/Runtime/BuiltIns/SurtrReflectionBuiltIns.cs:67,78` |
| LSP | keyword `attribute` en semantic tokens | `src/Surtr.LanguageServer/Workspace/SemanticTokensProvider.cs:446-450` |
| Interop C# (8 atributos + enums) | `Surtr.Interop.Attributes` | `src/Surtr.Interop.Attributes/*.cs` |
| Generación AOT | `SurtrSourceGenerator` (ISourceGenerator) | `src/Surtr.Interop.SourceGenerator/SurtrSourceGenerator.cs:19-68` |
| Registro por escaneo | `SurtrBridge.ScanAndRegister` + `SurtrReflectionScanner` | `src/Surtr.Interop/SurtrBridge.cs:127`; `SurtrReflectionScanner.cs` |
