# Plan: retención de metadata genérica — constraints, parámetros de método y superficie de reflexión

**Estado: Pasos 1 y 2 completos (implementados y verdes — 2264/2268, los 4 restantes son fallos
preexistentes de stdlib ajenos a este trabajo). Paso 3 pendiente.**

El objetivo, tal como se acordó con el usuario: que **toda la información genérica sobreviva a la
compilación y sea recuperable desde los metadatos**, sin crear una `SurtrClass` por especialización.
La investigación previa (`CLAUDE.md`, `docs/Compiler-Plan.md` §8) estableció que la erasure se
reparte en tres niveles: **(a)** aridad en el nombre — adoptado, **(b)** argumentos en el descriptor —
adoptado, **(c)** reificación en runtime — rechazado. Este plan conserva (a) y (b) intactos y cierra
los dos huecos declarativos que (a)+(b) dejan abiertos: **constraints** (Paso 1) y **parámetros
genéricos de método** (Paso 2), y expone el resultado por reflexión (Paso 3).

---

## 0. Inventario: lo que ya viaja (sin trabajo)

| Info | Dónde | Verificado en |
|---|---|---|
| Argumentos de toda construcción | En el descriptor: `Obox:Box`1;I`, en tabla de tipos, firmas, base, interfaces, atributos, handlers, access tables | `SurtrModuleImageWriter.cs:139-141, 312-471` |
| Nombres y aridad de los parámetros de tipo | `genericParameters: str[]` por clase e interfaz | `SurtrModuleImageWriter.cs:473-476, 557-560` |
| `G<n>` en firmas de miembros | Descriptor de tipo genérico del declarante | `SurtrClassReference.cs:457-463` |
| Lectura de vuelta | `MetadataImporter` reconstruye tipos construidos y `G<n>` | `MetadataImporter.cs:176-231, 314-329` |

## 1. No-objetivos (la línea que no se cruza)

- Sin clase por especialización, sin vector de argumentos por instancia, sin type tests por
  construcción, sin statics por construcción, sin layout por value type — todo el nivel (c).
- `SignatureKey()` **se queda erasure**: el linker sigue siendo un dictionary lookup
  (`SurtrTypeLinker.cs:442-607`). La colisión documentada `Wrapper<T,U>` (§8) queda como está.
- Sin sustitución en runtime: la vista construida sigue siendo asunto del compilador.

---

## 2. Paso 1 — Constraints en metadata

Las constraints viven hoy solo en `TypeParameterSymbol.Constraints`
(`TypeParameterSymbol.cs:61-65`) y desaparecen al emitir: `SurtrTypeInfo` no tiene tabla, la imagen
no tiene campo, y `MetadataImporter` no las reconstruye.

### 2.1 Runtime — `SurtrTypeInfo`

Espejo exacto de `GenericParameters` (`SurtrTypeInfo.cs:20-80`):

- `string[][] _genericConstraints` (una lista por parámetro genérico).
- `SetGenericConstraints(params string[][] descriptors)` — valida `ThrowIfBuilt()`, count ==
  `GenericParameterCount`, y que cada descriptor sea un descriptor válido.
- Getter `GenericConstraints`. Nada en el path de ejecución lo lee — mismo estatus que
  `GenericParameters`.

Cada constraint es un **descriptor string** (p. ej. `Osurtr:IComparable`1;G0`), que ya codifica
`G<n>` y construcciones anidadas — la misma representación que `DescriptorEmitter` produce.

### 2.2 Emisor — `ModuleEmitter.Parameterise`

`Parameterise` (`ModuleEmitter.cs:393-408`) copia hoy solo los nombres. Se extiende (o se le añade
una compañera) para emitir también las constraints de cada parámetro vía `_descriptors.Emit(...)`.
Es estático hoy; pasa a ser método de instancia o recibe el `DescriptorEmitter`. Call sites:
`DeclareType` (`ModuleEmitter.cs:340`) — clases e interfaces por el mismo camino.

### 2.3 Imagen — `Class`/`Interface` + bump de formato

En `WriteClass`/`WriteInterface` (`SurtrModuleImageWriter.cs:473-476, 557-560`), **justo después de
`genericParameters`**:

| Campo | Tipo | Notas |
|---|---|---|
| por parámetro genérico: `constraintCount` | `i32` | 0 si el parámetro no tiene bounds |
| … `constraints` | `str[]` | descriptores, en orden de declaración |

El reader (`SurtrModuleImageReader.cs:593-601, 677-685`) los lee en el mismo sitio y llama a
`SetGenericConstraints`. La sección es un paso secuencial estricto: **la posición de los campos es
parte del formato**. `SurtrModuleImage.FormatVersion` (`SurtrModuleImage.cs:95`) sube a **5**.

### 2.4 Importer — `MetadataImporter`

En la importación de un tipo (`CreateShell` + `Complete`, `MetadataImporter.cs:382-470`), después de
crear los `TypeParameterSymbol` (línea 420-428): resolver cada constraint con
`Import(descriptor, symbol)` (el `declaringType` correcto ya existe, así `G0` resuelve al parámetro
del tipo) y asignar `parameter.Constraints`. La caché de import por `SurtrTypeInfo`
(`MetadataImporter.cs:140-165`) ya protege contra ciclos.

### 2.5 Tests

- `SurtrModuleImageTests` (round-trip): clase `Box<T : IComparable<T>>` y `Pair<T : A & B, U>`
  pasan por imagen y vuelven con constraints idénticas, incluida una con `G0` y una con
  construcción (`Obox:Pair`2;IO...`).
- `MetadataImporterTests`: `IComparable<T>` importado resuelve al `TypeParameterSymbol` del tipo
  declarante, no a `unknown`.
- `ModuleEmitterTests` end-to-end: compilar → imagen → `Instantiate` → importer.
- `SurtrBytecodeDisassembler` (`SurtrBytecodeDisassembler.cs:356-358`): opcional, mostrar
  constraints junto a `GenericParameters`.

### 2.6 Documentación

- `docs/Module-Format.md` — sección `Class`/`Interface` (la línea 272 dice "Names only. Generics are
  erased."), histórico de versiones (4 → 5).
- `docs/Compiler-Plan.md` §8 — lo que ahora sobrevive.
- `docs/Runtime-Model.md` §3.1.

---

## 3. Paso 2 — Parámetros genéricos de método

Los parámetros de un método genérico se borran a `E` pelado en la firma
(`DescriptorEmitter.cs:163-167`): no hay lista por método, no hay índice, nada en un call site puede
decir "el 2º parámetro de este método". `ImportMethod` (`MetadataImporter.cs:579-627`) ni siquiera
asigna `TypeParameters` al `MethodSymbol` importado — **un método genérico compilado en el módulo A
y llamado desde el módulo B llega sin parámetros y con sus tipos degradados a `unknown`** (hueco real
verificado por lectura, no por test — el test rojo va primero, §3.4).

### 3.1 Gramática del descriptor — símbolo `H<n>`

`G<n>` está ocupado ("parámetro del tipo declarante"). Se añade `H<n>` — "el n-ésimo parámetro
genérico del **método** declarante". `H` está libre (usadas: `I F B C S R A D T L O N G ? E V`).
Alternativas consideradas: `M` (colisión mental con "module" en los docs), `K` (reserva futura).
**Decisión por confirmar con el usuario (§4).**

Cambios en `SurtrClassReference`:

| Miembro | Comportamiento nuevo |
|---|---|
| `MethodGenericParameter(int)` | internado `"H{n}"`, 0-9 |
| `TryGetMethodGenericParameterIndex(out int)` | gemelo de `TryGetGenericParameterIndex` (`SurtrClassReference.cs:266-281`) |
| TypeCode | `H` → `Erased` (junto a `G` y `E`, `SurtrClassReference.cs:890`) |
| `AppendErased` | `H` → `E` (el `SignatureKey` no cambia: `f(E)` sigue siendo la clave) |
| parse (`FromDescriptor`) y `ToDisplayString` | reconocer `H<n>` |

Consecuencia deliberada y ya cubierta: `f<T>(x: T)` y `f(x: unknown)` comparten clave `f(E)`; la
colisión se sigue reportando en compilación por `SignatureSet` (`SignatureSet.cs:128-133`, que
borra por símbolo, sin cambios).

### 3.2 Runtime y builders

- `SurtrMethodInfo` (base, `SurtrMethodInfo.cs:202-265`): `string[] _genericParameters` +
  `SetGenericParameters` (cap 10, `ThrowIfBuilt`) + getter — espejo de `SurtrTypeInfo`.
- `SurtrMethodBuilder`: los atributos esperan en la lista pendiente hasta `Build`
  (`SurtrMethodBuilder.cs:80-88`); los parámetros genéricos siguen el mismo patrón — campo
  `string[]?` + `SetGenericParameters` + aplicación en `Build`.

### 3.3 Emisor e imagen

- `DescriptorEmitter`: `TypeParameterSymbol { IsMethodTypeParameter: true }` → `H{Ordinal}`
  (hoy `E`, `DescriptorEmitter.cs:157-178`; mismo límite 9 que `G`).
- Helper `ParameteriseMethod(builder, method)` en `ModuleEmitter` + `ConstFolder.Declare`
  (`ConstFolder.cs:357`), llamado en todos los `DefineMethod`/`DefineConstructor`/bridges
  (`ModuleEmitter.cs:546, 716, 736, 747, 760, 1259`) — no-op para sintéticos.
- `WriteMethod` (`SurtrModuleImageWriter.cs:349-388`): tras `isSealed`, `count` + `str[]` de
  nombres. `ReadMethod` los lee y llama `SetGenericParameters`.

### 3.4 El test rojo primero (el agujero cross-module)

`MetadataImporterTests` (o `ModuleEmitterTests`): compilar módulo A con
`fun pick<T : IComparable<T>>(a: T, b: T): T` a imagen; importarlo desde B. Estado actual esperado:
o falla la llamada, o compila **sin** inferencia ni chequeo de constraints (params `unknown`).
Escribir el test y confirmar el rojo **antes** de tocar el importer.

### 3.5 Importer

En `ImportMethod`, en este orden:

1. Crear los `TypeParameterSymbol` del método (`_factory.DeclareTypeParameter(nombre, symbol, i)`)
   y asignar `symbol.TypeParameters` — **antes** de importar tipos de parámetros.
2. Constraints de método: misma resolución que §2.4 (misma representación, ahora también en
   `Method`).
3. `Import(reference, declaringType)` gana un tercer parámetro opcional
   `methodParameters: IReadOnlyList<TypeParameterSymbol>?`, encadenado por `ImportAll`/
   `ImportNamed`/`ImportHandle` y composites (`MetadataImporter.cs:176-231`). En el caso `Erased`,
   **`H` se comprueba antes que `G`**: `TryGetMethodGenericParameterIndex` → `methodParameters[i]`,
   si no `G` → `declaringType.TypeParameters[i]`.

### 3.6 Tests y documentación

- `DescriptorEmitterTests`: parámetro de método emite `H0`; pins de `SignatureKey` (`f(E)`) sin
  cambio.
- Pins existentes a actualizar: `SurtrBuiltInGenericsTests`/`SurtrStandardLibraryTests` si algún
  `H` entra en claves (no debería: `AppendErased` lo borra).
- `ModuleEmitterTests` end-to-end cross-module (el §3.4, en verde).
- Doc: `CLAUDE.md` (tabla de descriptores + nota), `docs/Runtime-Model.md` §3.1, `docs/Compiler-Plan.md`
  §8, `docs/Module-Format.md` (sección `Method`). `FormatVersion` → **6**.

---

## 4. Paso 3 — Superficie de reflexión (diseño con opciones, decisión del usuario)

La base ya existe: `Type.of`, `Type.get(descriptor)`, `members`, `attributes`
(`SurtrReflectionBuiltIns.cs:31-63`). El problema estructural: `Type` envuelve la **clase
compartida** (`WrapType`), y la clase compartida no sabe su construcción. Tres opciones:

**Opción A (recomendada) — clase compartida + descriptor retenido en `Type.get`/`typeof`.**
`Type` expone `genericParameterCount`, `genericParameters` (nombres) y `genericConstraints`
(descriptores) sobre la clase. Además, un `Type` que llega por descriptor de construcción
(`Type.get("Obox:Box`1;I")`) o por `typeof(Box<int>)` retiene el descriptor completo, y
`genericArguments: Type[]` los expone (`GetTypeArguments`, `SurtrClassReference.cs:634`). Coste:
una pequeña envoltura (no una clase por especialización — un campo `SurtrClassReference[]` en el
`SurtrTypeValue`). Consecuencia a documentar: `Type.get("Obox:Box`1;I")` ≠ `Type.get("Obox:Box`1;S")`
— coherente con C#, donde `List<int>` ≠ `List<string>`; y `Type.of(instancia)` sigue sin poder
decir la construcción (un objeto no la lleva).

**Opción B — solo clase compartida.** `genericParameterCount`, `genericParameters`,
`genericConstraints`. Nada de `genericArguments`; se documenta que las construcciones comparten
clase. Coste mínimo.

**Opción C — B + `Type.argumentsOf(descriptor)`** como método estático aparte que no toca la
identidad de `Type`. API más fea; no recomendada.

En las tres: los built-ins nuevos en `SurtrReflectionBuiltIns` leen tablas que **nada en el path de
ejecución** toca. Tests en `SurtrStandardLibraryTests` (o archivo propio). La decisión (A/B/C) es la
pregunta 1 de §5.

---

## 5. Decisiones pendientes del usuario

1. **Alcance del Paso 3**: opción A, B o C (§4).
2. **Símbolo del descriptor** para parámetros de método: `H` (recomendado), `M` o `K` (§3.1).
3. **Constraints de método**: ¿viajan también en el Paso 2 (lista por parámetro en `Method`, misma
   forma que en tipos)? Recomendado: sí — sin ellas un `pick<T : IComparable<T>>` importado no
   podría re-chequearse en el módulo B. Alternativa: solo parámetros (nombres), constraints de
   método diferidas.

## 6. Orden de implementación y verificación

1. Paso 1 completo (runtime → emisor → imagen → importer → tests → docs, `FormatVersion` = 5),
   `dotnet build Surtr.sln` + `dotnet test Surtr.sln` en verde.
2. Paso 2: test rojo del agujero cross-module primero, luego §3.2 → §3.3 → §3.5 → verde → docs,
   `FormatVersion` = 6.
3. Paso 3 según la decisión tomada.

## 7. Riesgos

- **Reader estricto secuencial**: un campo fuera de posición corrompe la lectura; por eso cada
  adición va con su bump de `FormatVersion` y sus tests de round-trip.
- **`Import` con `methodParameters`**: churn mecánico en `MetadataImporter` (firmas opcionales por
  composites); el orden H-antes-de-G en el caso `Erased` es el único punto con ambigüedad real.
- **Pins de descriptores**: `DescriptorEmitterTests` y `SurtrClassReferenceTests` pueden tener
  literales que asumen `E` para parámetros de método — revisarlos, no "arreglarlos".
- **`ConstFolder`**: los `const fun` genéricos usan el mismo `Declare`; el helper
  `ParameteriseMethod` debe cubrirlo o el formato del método y el de la imagen divergen en el
  scratch module.
