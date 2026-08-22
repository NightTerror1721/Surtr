# Surtr.Interop

Bridge declarativo entre Surtr y C#: decora clases/structs/enums CLR con atributos y exponlos como
tipos nativos Surtr, con enlace en tiempo de compilación (source generator, AOT-safe) o por reflexión
(fallback en ejecución).

## Paquetes

| Proyecto | TFM | Dependencias | Qué contiene |
|---|---|---|---|
| `Surtr.Interop.Attributes` | netstandard2.0 | ninguna | los atributos + `SurtrInteropVisibility` + `SurtrNamingPolicy` |
| `Surtr.Interop` | netstandard2.1 | Core + Attributes | el bridge: modelo, marshaler, materializador, caché de enums, fallback por reflexión |
| `Surtr.Interop.SourceGenerator` | netstandard2.0 | Roslyn 4.x | el analizador que genera los shims y el catálogo |

El código de usuario (y Unity) solo necesita referenciar `Surtr.Interop.Attributes` para decorar sus
tipos; `Surtr.Interop` y el generador los consume el host.

## Uso

```csharp
using Surtr.Interop.Attributes;

[SurtrNativeType]                    // sin Module -> registro global
public class Player
{
    public int Health;
    public string Name { get; set; } = "";
    public int TakeDamage(int amount) => ...;
}

[SurtrNativeType(Module = "game", Name = "LogLevel")]
public enum LogLevel { Debug, Info, Error }
```

Registro:

```csharp
using var runtime = new SurtrRuntime();

// Vía generador (compile-time): el catálogo generado registra todos los tipos escaneados.
Surtr.Interop.SurtrBindings.RegisterAll(runtime);

// Vía fallback por reflexión (sin generador): mismo resultado.
Surtr.Interop.SurtrBridge.ScanAndRegister(runtime, typeof(Player), typeof(LogLevel));
```

Ambas vías convergen en el mismo `NativeTypeDescriptor` y pasan por el mismo materializador.

## Atributos

- **`SurtrNativeTypeAttribute`** (clase/struct/enum): `Module` (path Surtr; `null` = global), `Name`,
  `Description`, `NamingPolicy`, y `TypeArguments` (formas cerradas de un genérico; `AllowMultiple`).
- **`SurtrNativeMemberAttribute`** (base opcional): `Name`, `Description`, `Visibility`, `NamingPolicy`,
  `Expose` (default `true`).
- **`SurtrNativeMethodAttribute`** (método/constructor): añade `ReturnDescriptor`.
- **`SurtrNativeFieldAttribute`** (campo): añade `ReadOnly` y `TypeDescriptor`.
- **`SurtrNativePropertyAttribute`** (propiedad): añade `TypeDescriptor`; el *read-only* se deduce de
  getter/setter públicos.
- **`SurtrNativeParameterAttribute`** (parámetro): `Name`, `Description`, `TypeDescriptor`.
- **`SurtrNativeIgnoreAttribute`**: atajo a `Expose = false`.

Un tipo con `[SurtrNativeType]` expone **todos sus miembros públicos** con valores derivados de la
firma C#; los atributos de miembro solo sobrescriben. `TypeDescriptor`/`ReturnDescriptor` aceptan el
descriptor canónico Surtr (`I`, `F`, `S`, `AI`, `Omod:Tipo`, `Nmod:Tipo`, ...).

## Nomenclatura

`SurtrNamingPolicy` (`Default`/`Surtr`, `PascalCase`, `CamelCase`, `SnakeCase`, `LowerCase`,
`UpperCase`). `Surtr` (por defecto) deja los tipos en PascalCase y adapta los miembros a camelCase
(`Add` -> `add`). Precedencia: global (`SurtrBridge.DefaultNamingPolicy`) < runtime < módulo < clase <
miembro.

## Semántica

- **Enums**: un enum C# es un enum Surtr completo (clase sealed con cases). El bridge cachea un
  `SurtrNativeObject` por valor (rooteado) y los cablea en los cases, así el marshaling es O(1) sin
  boxing. `out` se pliega al retorno (tupla); `ref`/`in` se rechazan. Genéricos: solo formas cerradas.
  Delegates: closure Surtr. Indexadores/operadores: se mapean cuando hay equivalente.
- **Structs**: se empaquetan (boxing) y aparecen como clases normales.
- **Campos**: un campo CLR es un **campo nativo** real (`SurtrNativeFieldInfo`), leído/escrito por el
  VM a través de entry points; no es una propiedad.

## AOT / IL2CPP

El camino del generador emite shims estáticos y los enlaza con
`SurtrNativeEntryPoint.FromFunctionPointer(&shim)` (sin reflection). Requiere
`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` en el proyecto consumidor (igual que `Surtr.Stdlib`).
El fallback por reflexión emite shims con `AssemblyBuilder` y **no es AOT-safe**: úsalo solo donde el
generador no esté disponible.

## Decisiones de diseño

El plan completo (atributos, marshaling, enums cacheados, política de nomenclatura, fases) está en
`docs/Plan-Bridge-CSharp-Atributos-SourceGenerator.md`.
