# Handoff — Value classes multi-campo nullable: sin representación de null

> **Estado: RESUELTO** (Opción A, "Null como boxed") — implementado y verificado en esta sesión:
> `T?` multi-campo se representa como referencia (presente = `SurtrInstance` boxeado, ausente =
> referencia null), con boxeo en `T → T?`, desboxeo en `T? → T` (cast, `!!`, narrowing, `?.`) y
> test de referencia para `== null`/`!= null`. `of` se sintetiza ahora también para enums
> multi-campo. Todos los tests (3037) en verde. Lo que sigue es el registro histórico del
> diagnóstico que motivó el cambio.
>
> La implementación tocó: `ValueTypeLayout.IsInlineType`/`TryGet` (anulable multi-campo = 1 slot),
> `MethodBodyEmitter` (`EmitConversionTail` box/desbox, `EmitReturnValue`, `UnboxIfNullableBlock`,
> `EnsureBlockSlot`, receptores de llamada inline/directa), `Binder.AddEnumMembers` (guard
> `canBeNull` eliminado), y tests en `ModuleEmitterTests`.

## El problema en una frase

Un **value class multi-campo** (y, desde la migración, un **enum con campos de usuario**) no tiene
ninguna representación de `null` para su tipo anulable `T?`: el `null` ocupa un slot pero el valor
ocupa `width > 1` slots, y el emisor se queda corto de stack.

## Evidencia reproducible

```surtr
value class Vec2 {
  public let x: int;
  public let y: int;
  public constructor(x: int, y: int) { this.x = x; this.y = y; }
}
fun make(): Vec2? { return null; }
```

Falla en emisión:

```
error SURTR4001: 'make' could not be emitted: Operand stack underflow at offset 1 in 'make':
the instruction pops 2 but the stack holds 1.
```

La misma carencia, vista desde un enum multi-campo (Fase 3):

```surtr
enum Suit {
  Hearts("h"), Spades("s");
  public let glyph: string;
  private constructor(glyph: string) { this.glyph = glyph; }
}
fun pick(): Suit? { return null; }
```

## Dónde está el problema

1. **`EmitLiteral`** (`MethodBodyEmitter.cs:1850`): un literal `null` de un tipo anulable de
   **un slot** emite `PushAbsent(TypeCodeOf(tipo))` — el tag de ausencia (§5.1). Para cualquier
   otro tipo anulable emite `Code.LoadNull()` (una referencia null, **1 slot**).
2. **`EmitReturnOf` / `EmitReturn`** (`MethodBodyEmitter.cs:1694`): calcula el ancho por
   `SlotCountOfType(tipo)` y emite `ReturnValues(width)`. Para `Vec2?` o `Suit?` multi-campo,
   `width = 2`, pero el `LoadNull` anterior dejó 1 slot en el stack → underflow.
3. **`IsNullablePrimitive`** (`MethodBodyEmitter.cs:2700`): hoy (tras Fase 3) es
   `IsNullable && SlotCountOfType == 1 && TypeCodeOf es Integer/Float/Boolean/Character`. Un
   `Vec2?` multi-campo tiene `SlotCountOfType == 2` → NO es "nullable primitive" → cae a la ruta
   de null-referencia, que no encaja con un bloque.
4. **Descriptor**: `Vec2?` se emite como descriptor nominal anulable (`?C<...;>`), que el runtime
   no distingue del de una referencia. No existe un convenio "bloque ausente" en el VM.

## Impacto concreto en la migración de enums (Fase 3)

- El miembro sintetizado `of(value): E?` / `of(name): E?` (§2.3) **solo se genera para enums de un
  campo** (`Binder.AddEnumMembers`, `canBeNull = fields.Count == 1`). Para enums con campos de
  usuario no se sintetiza, porque su `E?` no puede devolver `null`.
- Un `@Flags` (siempre un solo campo) sí lo tiene: su `E?` es `int?`-como, y `Perm.of(3)` es total
  (nunca null), así que la limitación no le afecta.
- La firma uniforme `E?` del diseño queda, por tanto, **incumplida para enums multi-campo** hasta
  que esta carencia se resuelva.

## Lo que ya se arregló (contexto)

- En Fase 3 se extendió `IsNullablePrimitive` para que un enum de un campo (`Suit?`, un slot) use
  el tag de ausencia como `int?`. Eso desbloqueó `of` en enums planos/flags de un campo.
- Lo que **sigue sin resolver** es el caso multi-slot: `Vec2?`, `Suit?` con campos, cualquier value
  class multi-campo anulable.

## Opciones para resolverlo en otra sesión

| Opción | Idea | Coste |
|---|---|---|
| **A: Null como boxed** | `T?` multi-campo se representa como **referencia**: presente = `SurtrInstance` (BoxValue) del valor; ausente = referencia null. Requiere boxear en la conversión `T → T?` y desboxear en cada lectura `T? → T`, y que `== null`/`!= null` prueben la referencia. Es el modelo de C# (`Nullable<T>` como boxed o null). | medio-alto (toca conversiones, flujo, `TryGetNullCheckOperand`, `EmitNullCoalesce`, boxeo) |
| **B: Tag de ausencia para bloques** | Un convenio "bloque ausente" en el VM: un slot marcador (p. ej. el primero con tag de ausencia) hace que los `width` slots se lean como ausentes. Requiere tocar el VM y todas las lecturas de bloques. | alto (VM-wide) |
| **C: Restringir anulabilidad** | Prohibir `T?` en value classes multi-campo con un diagnóstico claro (`InvalidValueClass`/nuevo código), y que `of` de enums multi-campo se declare o se rechace explícitamente. | bajo (acepta la carencia como regla del lenguaje) |
| **D: `of` sin null** | `of` para enums multi-campo devuelve `E` y lanza/falla si no hay caso (o queda sin sintetizar, como hoy). Parcial: no arregla `Vec2?` en general. | bajo |

La opción A es la que más se acerca a la intención del diseño (§2.3 mantiene la firma `E?`
uniforme). La B es la más limpia a nivel de representación pero invade el VM. La C es la que menos
presupuesto consume si la prioridad es cerrar la migración de enums.

## Piezas que tocar (si se elige A)

- `Conversions`: clasificar `T → T?` y `T? → T` para value classes multi-campo como boxeo/desboxeo
  (hoy `ImplicitNullable` es no-op, que es la base del bug).
- `MethodBodyEmitter`: `EmitLiteral` (null → `LoadNull` para multi-slot en vez de `PushAbsent`),
  `IsNullablePrimitive` (solo un slot), `EmitConversionTail` (boxear al entrar en `T?`),
  `TryGetNullCheckOperand`/`EmitNullCoalesce`/`TryEmitAbsenceTest` (probar la referencia).
- `ValueTypeLayout.WidthOfType(T?)`: decidir si `T?` cuenta como un slot (referencia) o como
  `width` (bloque) en cada punto.
- `Binder.AddEnumMembers`: quitar el guard `canBeNull` y volver a sintetizar `of` para enums
  multi-campo, con tests de round-trip `of`/`.value` y de `== null`.
- Tests de regresión: `SurtrVirtualMachineValueTypesTests`, más los nuevos de Fase 3
  (`TheSynthesizedApiWorksForACaseCarryingEnum`, `OfValueRoundTripsAndIsNullForUnknowns`).

## Referencias

- Diagnóstico original: `value class Vec2 ... fun make(): Vec2? { return null; }` → SURTR4001
  underflow. Reproducido también con `enum Suit { ... } fun pick(): Suit? { return null; }`.
- `EmitLiteral` `MethodBodyEmitter.cs:1850`; `EmitReturnOf` `:1694`; `IsNullablePrimitive` `:2700`;
  `AddEnumMembers` `Binder.cs` (guard `canBeNull`).
- Diseño: `docs/Informe-Enums-ClasesDeValor.md` §2.3 (firma `E?` de `of`) y §6.7.