# Handoff — Sesión siguiente: continuar la migración de enums a clases de valor

## Contexto

Proyecto **Surtr** (compilador + VM + stdlib). Migración de los `enum` de «clase sellada con
instancias estáticas» a «clase de valor con campo `public let value: int`». Diseño completo y
aprobado en **`docs/Informe-Enums-ClasesDeValor.md`** (es la especificación autoritativa: 5 fases +
§2.3bis/ter/quater para marcas y `const`). Todo **commiteado y en verde**: build limpio,
**2991/2991 tests**.

**Historial:**
- `c557a07` — **`Fase 1a`** (último commit, lo hecho esta sesión): sintaxis `CASO(args…)=n`,
  valores constantes por caso, validaciones, ctor privado, nombres reservados.

Rama: `develop`. Ojo: hay **documentos sin commitear** de sesiones previas (`docs/Informe-*.md`,
`docs/Plan-*.md`, `docs/Handoff-Atributos-Siguiente-Sesion.md`, `AGENTS.md`, `CLAUDE.md`) — **no
tocar ni commitear**: pertenecen a otra línea de trabajo.

## Qué hizo la Fase 1a (commit c557a07) — ya en verde

| Pieza | Dónde | Qué hace |
|---|---|---|
| AST | `Syntax/Ast/DeclarationSyntax.cs` | `EnumCaseSyntax.ExplicitValue: long?` (construido por el parser, opcional). |
| Parser | `Syntax/Parser.Declarations.cs` `ParseEnumCases` | tras los args opcionales acepta `= <literal int>` (token `TokenType.Assign`). **Gotcha:** `TokenType.Equals` colisiona con `object.Equals` y no compila; usar `TokenType.Assign`. |
| Binder | `Binding/Binder.cs` (`BindTypeMembers`) | calcula el valor de cada caso (progresión desde 0 / explícito / `1<<posición` en flags), lo guarda en `FieldSymbol.EnumValue`, y valida: flags potencia-de-2 (o 0), duplicados (prohibidos planos / permitidos flags). |
| Binder | `Binding/Binder.cs` `BindConstructor` | ctor de enum **siempre private** (se fuerza; error si se escribe otra visibilidad). |
| Binder | `Binding/Binder.cs` `CheckEnumReservedNames` | `value`/`values`/`of` reservados en enums. Helpers: `IsPowerOfTwo`, `IsReservedEnumName`. |
| BodyBinder | `Binding/BodyBinder.cs` `BindEnumCase` | flags usan el valor escrito (`CaseValueOf`); se eliminó `OrdinalOf`. |
| Símbolo | `Binding/Symbols/MemberSymbols.cs` | `FieldSymbol.EnumValue: int?`. |
| Diagnósticos | `Diagnostics/SurtrDiagnosticCode.cs` | `InvalidEnumValue=3088`, `DuplicateEnumValue=3089`, `InvalidEnumConstructor=3090`, `ReservedEnumMember=3091`. |

**Gotcha crítico de 1a (evitar regresión):** el emisor solo baja literales **`long`**. `BindEnumCase`
debe construir `new BoundLiteralExpression(syntax, @enum, (long)value)` — pasar un `int` produce
`SURTR4001: literal of CLR type 'Int32', which code generation does not lower yet`.

Tests añadidos: 1 en `ParserTests` (`EnumParsesExplicitValuesAfterArguments`), 6 en `BinderTests`
(valores explícitos+progresión, flags no-potencia, duplicados flags/planos, ctor público rechazado,
reservados). Se actualizó `AnEnumCaseCarriesItsConstructorArguments` (ctor → `private`).

## Lo que falta (por prioridad)

### 1. Fase 1b — Representación: enums → value class (la cascada grande)
Objetivo: que `enum` se emita como value class (`IsValueType=true`, campos `let`, `value` como
primer campo). **El punto crítico es la inyección del valor del caso en el campo `value`** (no es
parámetro del constructor). Orden propuesto, manteniendo tests verdes en cada sub-paso:

1. **Sintetizar el campo `value`**: en `Binder.BindTypeMembers`, dentro del bloque enum (antes de
   los campos de usuario), añadir `FieldSymbol("value", symbol, int)` con `IsReadOnly=true`,
   `Accessibility=Public`, `IsSynthetic=true`, como **primer** campo de instancia. La reserva de
   nombre ya existe (1a). Extender `BindValueClassField` (o un equivalente) a enums: **todo campo de
   instancia debe ser `let`** (hoy solo se aplica a `value class`; la regla `instanceFields.Count ==
   letFields` serviría si se llama para enums). No poner `UnderlyingType` (decisión §6.1: descriptor
   nominal, nunca borrar a `I`).
2. **Inyección del valor (el crux)**: un caso se construye con los args del ctor del usuario; `value`
   se escribe **justo después de la construcción, en el static initializer del enum** (solo el
   compilador lo genera). Punto de enganche: la emisión de `InitializerBinding` de los casos
   (`ModuleEmitter` `SortInitializers`/`EmitStaticFragments`, ~líneas 1271-1347). El `let` es una
   disciplina del binder para código de usuario; el emisor puede emitir un `stf` al campo `value`
   tras construir la instancia sin pasar por el chequeo. Alternativa descartada: parámetro oculto en
   el ctor (delegación de ctor en value classes no está resuelta en el lenguaje).
3. **`DeclareType`** (`CodeGen/ModuleEmitter.cs:308-382`): borrar la rama `case TypeSymbolKind.Enum
   when !symbol.IsFlagsEnum:` (líneas 339-348) y rutear **todo** enum por la rama de clase con
   `@class.Class.IsValueType = true`. El comentario «§P14: un @Flags no es un enum en runtime»
   desaparece. Cuidado: mantener `isEnum` en metadata (la reflexión y `EnumCases` dependen de él) —
   ver cómo `SurtrModuleBuilder.DefineEnum` (Bytecode/Emit/SurtrModuleBuilder.cs:534) fija el flag y
   qué hay que ajustar al pasar por la rama de clase (o añadir un `DefineEnum` que fije
   `isValueType`).
4. **`TypeCodeOf`** (`CodeGen/MethodBodyEmitter.cs:6083-6086`): `case TypeSymbolKind.Enum when
   IsFlagsEnum` → `case TypeSymbolKind.Enum` (todos los enums → `SurtrValueTypeCode.Integer`).
5. **`DescriptorEmitter`** (`CodeGen/DescriptorEmitter.cs:205-213`): quitar el caso especial
   flags→`'I'`; todos los enums caen a descriptor nominal (AppendNamed). El linker achata por campos
   (`IsValueType`), así que 1 slot.
6. **`DeclareField`** (`CodeGen/ModuleEmitter.cs:616-639`): decidir si los casos siguen por
   `DefineEnumCase` (crea campo + ordinal + tabla EnumCases) o como statics normales. Recomendación:
   **seguir con `DefineEnumCase`** por ahora (la tabla de metadata sigue valiendo para
   reflexión/disassembler); el `value` y los campos de usuario van por el flujo normal. Con la rama
   flags-as-int eliminada, los casos flags también pasan por `DefineEnumCase`.
7. **`===` sobre enums → error** (decisión §6.2): como sobre value classes. El binder rechaza
   `===` sobre `TypeSymbolKind.ValueClass`; extender a enums. Esto rompe
   `TwoEnumCasesAreDifferentInstances` (usa `===`) → actualizar a `==`.
8. **Imagen** (`SurtrModuleImageWriter.cs:484-588`, `SurtrModuleImageReader.cs:662-671`): decidir si
   el cambio de formato (`{name, value, vis}`) entra en 1b o en Fase 2. Recomendación: **entra con la
   Fase 2** (los valores en metadata son necesarios para el switch cross-módulo; acoplarlas evita dos
   bumps). Si se difiere, 1b mantiene el formato actual (ordinal derivado de `AddEnumCase`).
9. **Runtime** (`Runtime/Classes/SurtrClass.cs`, `SurtrEnumCaseInfo.cs`, `SurtrRuntime.cs`):
   `SurtrEnumCaseInfo.Value` + `AddEnumCase(name, value, vis)` SOLO si se cambia el formato en 1b;
   si no, queda para Fase 2.

Tests que tocar en 1b (inventario del informe §8):
- `AFlagsEnumTravelsAsAClassOfIntConstants` (ModuleEmitterTests) → flags ahora value class.
- `TwoEnumCasesAreDifferentInstances` → `===` rechazado; pasar a `==`.
- `AnEnumCaseCarriesItsConstructorArguments` → verificar que `value` existe y está relleno por caso.
- `AnEnumIsASealedClass` (BinderTests) → añadir aserción del campo `value` sintetizado.
- Round-trips de imagen (`SurtrModuleImageTests.cs:262`) → el campo `value` viaja.
- Tests que aserten descriptor `'I'` de flags (grep `Descriptor` + `Perm`) → descriptor nominal.
- `ASwitchOverAnEnumMatchesByCase` → verificar que sigue verde (familia Integer → cadena de
  comparaciones, correcto mientras no esté la Fase 2).

### 2. Fase 2 — Switch tables (adaptación pedida)
- `BindEnumCase` → literal **para todos** los enums (hoy solo flags). Con `FieldSymbol.EnumValue` ya
  hay de dónde sacar el valor.
- Para enums de **un campo** el subject ya es int (`TypeCodeOf=Integer`); los labels son constantes
  → `TryCollectIntegerCases` (`MethodBodyEmitter.cs:1184`) los acepta → `Code.SwitchOn` →
  `OpCode.Switch`/`SwitchLookup` (VM `SurtrVirtualMachine.cs:3224/3249`). Cero opcodes nuevos.
- Para enums **multi-campo**: bajar el subject a `subject.value` (o extraer el sub-slot en `EmitDispatch`).
- La elisión del último brazo del switch-expression exhaustivo (`MethodBodyEmitter.cs:5692-5704`)
  se mantiene. Duplicados (@Flags): `TryCollectIntegerCases` ya cae a cadena primer-match.
- `ConstantOf` debe plegar lecturas de casos: o bien los casos se ligan como literales (recomendado),
  o se extiende `ConstantOf` para leer `FieldSymbol.EnumValue` en statics de enum.
- **Formato de imagen v+1**: casos `{name, value: i32, vis}` + `SurtrEnumCaseInfo.Value` +
  `AddEnumCase(name, value, vis)` (writer/reader/disassembler `AppendEnumCase` → `case X = <value>`).
  Bump siguiendo el precedente v8 (`docs/Module-Format.md:126`). Opcional: `isFlags` byte junto a
  `isValueType` (decisión §6.4) para que flags-ness sobreviva fronteras.

### 3. Fase 3 — Miembros sintetizados + contratos (§2.3, §2.3bis/ter/quater)
Síntesis real de métodos (`MethodSymbol` con `IsSynthetic=true`) sobre cada enum:
`equals(other:E): bool`, `hashCode(): int`, `toString(): string` (switch sobre value), `values(): E[]`
(array fresco, **sin `@Pure`** — §6.7: el CSE aliñaría dos llamadas), `of(value:int): E?` y
`of(name:string): E?`, `compareTo(other:E): int` y `operator<=>` (§5.6 da `< <= > >=`).
- Contratos implícitos: `IEquatable<E>` + `IComparable<E>` **en todos** (decisión §6.8 revisada) →
  añadirlos a `DeclaredInterfaces` y enlazar slots por firma borrada (machinery en
  `ModuleEmitter.cs:1404`, bridge `compareTo(E)`).
- **Exención `==`↛`equals`**: `==` sobre enum NUNCA baja a una llamada a `equals` (regla del
  Plan-ClaseBase revocada para enums); se queda en comparación de slots/opcodes.
- Marcas por miembro (tabla §2.3bis): `equals`/`hashCode`/`compareTo`/`contains` → `inline @Pure
  @NoAlloc`; `of(int)` → idem; `of(name)` → solo `@Pure @NoAlloc`; `toString` → `@Pure` (NO `@NoAlloc`,
  interpola en el fallback); `values()` → **sin marcas**; `operator<=>` → **`forceinline`**.
- `CheckFlagsEnumIsPlain` (Binder.cs:2092-2132): sustituir la rama de interfaces por lista cerrada
  (flags: solo los dos contratos sintetizados); la rama de miembros sigue (flags sin miembros propios).
- `BuiltInAttributes.IsPure` y el analizador `AllocationInNoAllocBody` deben pasar los cuerpos
  sintetizados tal cual (es test gratis).

### 4. Fase 4 — Interop (borrado grande)
- `NativeTypeDescriptor.EnumCases` → `{name, long value}[]`; `EnumValues` se elimina
  (`Interop/Descriptors.cs:61-68`).
- `SurtrTypeMaterializer.RegisterEnum` (`SurtrTypeMaterializer.cs:96-120`): sin `WrapNative`, sin
  `AddRoot`, sin `SurtrEnumCache` — `DefineNativeEnum` + tabla (name,value) + `FinishNativeClass`.
- **Borrar** `SurtrInteropState`/`SurtrEnumCache` (`Interop/SurtrInteropState.cs`) y
  `SurtrRuntime._nativeEnumCases`/`SealNativeEnumCases` (`SurtrRuntime.cs:54, 1539-1551`).
- Marshaler (`SurtrMarshaler.cs:46-55, 89-93`, `SurtrEnums.cs`): `CreateInt(Convert.ToInt32(v))` /
  `(TEnum)Enum.ToObject(typeof(TEnum), v.AsInt)` — aritmética pura, sin estado por runtime. Los combos
  sin nombre dejan de lanzar «not registered».
- `SurtrSourceGenerator.EmitEnumRegistration` (`SurtrInterop.SourceGenerator/SurtrSourceGenerator.cs:191-212`):
  emitir `ConstantValue` por caso + detectar `[Flags]` del CLR → `IsFlags=true`.
- `SurtrReflectionScanner.cs:52-57`: `Enum.GetNames` + valor numérico vía `IConvertible.ToInt64`; sin
  DynamicMethod (AOT-safe para enums).

### 5. Fase 5 — const-always (§2.3quater, trabajo comprometido)
- Fold de **receptores constantes** en `ConstFolder` (Binder.cs:3838-3846; `_isPureCandidate` exige
  hoy `native && pure`): llamadas de instancia con receptor = caso (todo caso es literal) y despacho
  directo garantizado.
- **`$intToString` sintético** (helper `const fun` por módulo, bucle de dígitos puro sin nativos):
  desbloquea `toString()` como `const` (el fallback interpola; `IntToString` es nativo y los nativos
  están prohibidos en cuerpos const, §7.2).
- **Constantes enum en atributos**: `ConstantFitsField` (`Binder.cs:3500`) filtra por `SpecialType`;
  aceptar constantes con tipo enum guardando su `value` (`ModuleEmitter.cs:641-654`).
- Marcas `const` en: `of`×2, `values()`, y (con habilitación 1) instancia `equals`/`hashCode`/
  `compareTo`/`contains`/`toString`. `const` NO implica inline (§7.2:2498).

### 6. Fase 6 — Limpieza y docs
- Ramas muertas: `DeclareType` flags-as-class, `CheckFlagsEnumIsPlain` parcial,
  `CountFieldsExcludingCases`/`IsEnumCaseField` (si cambia el formato), `OrdinalOf` (ya eliminado).
- Docs: `Language-Syntax.md` §2.4 (enums como value class + valores explícitos + orden por valor) y
  §11.1 (`@Flags`), `Module-Format.md`, `build-stdlib.ps1` (imagen v+1 → recompilar stdlib).

## Arquitectura que debes conocer (verificada esta sesión)

- **Value class ya existe** (§2.9, `docs/Language-Syntax.md:640-711`): borrado de un campo
  (`UnderlyingType`), bloques multi-campo (`ValueTypeLayout.cs`, `SurtrClass.IsValueType`, linker
  `SurtrTypeLinker.cs:513`), igualdad estructural `EmitValueClassEquality`
  (`MethodBodyEmitter.cs:2831`), `===` rechazado, boxing `BoxValue`/`UnboxValue`. Los statics de
  value class ya se leen por bloque: `EmitFieldRead` → `LoadValueStatic(info, width)`
  (`MethodBodyEmitter.cs:3395-3411`).
- **Dónde está el special-case de flags hoy** (a eliminar en 1b): `ModuleEmitter.cs:333-348`
  (DeclareType: flags como clase plana), `:616-629` (DeclareField: casos flags como static int),
  `MethodBodyEmitter.cs:6083-6086` (TypeCodeOf), `DescriptorEmitter.cs:205-213` (descriptor `'I'`),
  `BodyBinder.cs:216-220` (BindEnumCase), `Binder.cs:2092-2132` (CheckFlagsEnumIsPlain).
- **`value` del caso**: `FieldSymbol.EnumValue` (1a). Para la Fase 2, o bien `BindEnumCase` produce
  literales, o `ConstantOf` lo lee.
- **`MethodSymbol`** tiene ya `IsInline/IsForceInline/IsNoInline/IsConst/IsSynthetic/Role`
  (`Binding/Symbols/MemberSymbols.cs:116-154`). `FieldSymbol.IsConst` elide storage — NO usar para
  casos (rompe reflexión); `EnumValue` es el lugar.
- **Contratos núcleo** (§13.2): `IEquatable<T>.equals`, `IComparable<T>.compareTo` en el namespace
  `surtr`, auto-importados; binding por firma borrada + bridge (`ModuleEmitter.cs:1404`); primitivos
  ya satisfacen constraints (`APrimitiveIntSatisfiesAnIComparableConstraint`).
- **`@Pure`/`@NoAlloc`**: `BuiltInAttributes.IsPure` (Binding/BuiltInAttributes.cs:232); `@NoAlloc`
  con analizador `AllocationInNoAllocBody` (§11.1:3116-3122); viajan en metadata
  (`MetadataImporter.cs:774`).
- **Ciclo de build/test**: `dotnet build Surtr.sln -c Debug` (7 s) ·
  `dotnet test src\Surtr.Tests\Surtr.Tests.csproj -c Debug` (1 s, 2991 tests).
- **Línea base**: 2984 tests antes de 1a → 2991 tras 1a.

## Decisiones del informe ya cerradas (no reabrir sin usuario)

Descriptor nominal (§6.1) · `===` rechazado (§6.2) · duplicados solo en @Flags (§6.3) · `isFlags` en
metadata (§6.4) · casts: solo flags, escape vía `of(value)` (§6.5) · `value: int` fijo (§6.6) ·
`values()` array fresco y sin `@Pure` (§6.7) · `IEquatable`+`IComparable` en todos los enums (§6.8) ·
`const` garantizado (§2.3quater). La sintaxis `CASO(args…)=n` la fijó el usuario explícitamente.