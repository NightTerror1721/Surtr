# Guía: interop Surtr ↔ C# (bridge de tipos nativos)

Esta guía explica cómo exponer clases, structs y enums de C# a Surtr como **tipos nativos**, usando
el paquete `Surtr.Interop`. Hay dos caminos que producen exactamente el mismo resultado:

- **Source generator** (recomendado): enlaza en tiempo de compilación con shims estáticos
  AOT-safe. Se activa referenciando `Surtr.Interop.SourceGenerator`.
- **Fallback por reflexión**: escanea en ejecución y genera los shims dinámicamente. Se usa cuando
  el generador no está disponible. No es AOT-safe.

Ambos convergen en el mismo modelo (`NativeTypeDescriptor`) y pasan por el mismo materializador, así
que el comportamiento es idéntico.

---

## 1. Paquetes

| Proyecto | TFM | Referencias | Qué contiene |
|---|---|---|---|
| `Surtr.Interop.Attributes` | netstandard2.0 | ninguna | los atributos, `SurtrInteropVisibility`, `SurtrNamingPolicy`, `SurtrNaming` |
| `Surtr.Interop` | netstandard2.1 | Core + Attributes | el bridge: modelo, marshaler, materializador, caché de enums, fallback por reflexión |
| `Surtr.Interop.SourceGenerator` | netstandard2.0 | Roslyn 4.x | el analizador que genera shims, descriptores y el catálogo |

El **código de usuario** (y Unity) solo necesita referenciar `Surtr.Interop.Attributes` para decorar
sus tipos; `Surtr.Interop` y el generador los consume el host al registrar.

## 2. Configuración y requisito de `unsafe`

El código generado por el source generator usa **function pointers** (`delegate*`) para enlazar los
shims con `SurtrNativeEntryPoint.FromFunctionPointer(&shim)`, la misma convención que ya usa
`Surtr.Core` y `Surtr.Stdlib`. Eso exige:

- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` en el proyecto que **contiene los tipos decorados**
  (donde se emite el código generado).
- En **Unity**: marcar la casilla **"Allow 'unsafe' Code"** en el Assembly Definition del assembly
  donde están los tipos.
- El DLL del generador en sí **no usa `unsafe`** y solo corre en compilación (proceso Roslyn/Unity),
  no en el runtime.

Para .NET, el `.csproj` del proyecto consumidor queda:

```xml
<PropertyGroup>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
</PropertyGroup>
```

Si algún target de IL2CPP no soportara function pointers, existe la alternativa de emitir
`SurtrNativeEntryPoint.FromDelegate(shim)` (sin `unsafe`, pero por reflexión y menos AOT-safe); se
decidirá por una opción de build si llega a hacer falta.

## 3. Atributos

Todos en `Surtr.Interop.Attributes`, sin dependencias.

### `SurtrNativeTypeAttribute` — clase, struct o enum

```csharp
[SurtrNativeType(Module = "game", Name = "Player", Description = "...", NamingPolicy = SurtrNamingPolicy.Surtr)]
public class Player { ... }
```

- `Module`: path Surtr (`game.entities`). **`null` (por defecto) = registro global**.
- `Name`: nombre Surtr; `null` = nombre CLR adaptado por la política de nomenclatura.
- `Description`, `NamingPolicy`.
- `TypeArguments`: formas cerradas de un tipo genérico (`AllowMultiple = true`). Un genérico sin
  `TypeArguments` se **omite con warning** (solo se exponen formas cerradas).

### `SurtrNativeMemberAttribute` — base opcional de miembro

```csharp
[SurtrNativeMember(Name = "foo", Description = "...", Visibility = SurtrInteropVisibility.Public, NamingPolicy = ..., Expose = true)]
```

`Expose = false` excluye el miembro. Si `Name` y `NamingPolicy` van juntos, `Name` gana.

### Atributos concretos

- **`SurtrNativeMethodAttribute`** (método/constructor): añade `ReturnDescriptor`.
- **`SurtrNativeFieldAttribute`** (campo): añade `ReadOnly` (default `false`) y `TypeDescriptor`.
- **`SurtrNativePropertyAttribute`** (propiedad): añade `TypeDescriptor`; el read-only se deduce de
  getter/setter públicos.
- **`SurtrNativeParameterAttribute`** (parámetro): `Name`, `Description`, `TypeDescriptor`.
- **`SurtrNativeIgnoreAttribute`**: atajo a `Expose = false`.

`TypeDescriptor`/`ReturnDescriptor` aceptan el **descriptor canónico Surtr** (`I`, `F`, `S`, `AI`,
`Omod:Tipo`, `Nmod:Tipo`, `L(...)R`, `T(...)`, `?I`, ...). Si el string no es un descriptor bien
formado, el source generator lo reporta como **error** (`SURTRINTEROP002`).

### Política de escaneo por defecto

Un tipo con `[SurtrNativeType]` expone **todos sus miembros públicos** con metadatos derivados de la
firma C#. Los atributos de miembro solo sobrescriben. Los miembros no públicos se ignoran.

## 4. Registro

### Global / por runtime (native classes)

```csharp
using var runtime = new SurtrRuntime();

// Vía generador: catálogo generado que registra todos los tipos decorados.
Surtr.Interop.SurtrBindings.RegisterAll(runtime);

// Vía reflexión (fallback): mismos descriptores, escaneo en ejecución.
Surtr.Interop.SurtrBridge.ScanAndRegister(runtime, typeof(Player), typeof(LogLevel));

// Registro explícito de un tipo.
Surtr.Interop.SurtrBridge.Register<Player>(runtime);
```

`RegisterAll` registra primero los enums y las clases base (orden topológico por herencia).

### En un módulo concreto

```csharp
// "Módulo" es un ámbito de nombre: el full name pasa a ser "modulo:Nombre".
Surtr.Interop.SurtrBridge.RegisterIntoModule(runtime, "game.entities", descriptor);
```

Los tipos nativos son globales por runtime (`DefineNativeClass`); el `Module` en el atributo solo
cualifica el nombre, que es como el código Surtr los referencia.

### Nomenclatura por scope

`SurtrNamingPolicy` (`Default`/`Surtr`, `PascalCase`, `CamelCase`, `SnakeCase`, `LowerCase`,
`UpperCase`). Precedencia: **global < runtime < módulo < clase < miembro**.

- Global: `SurtrBridge.DefaultNamingPolicy`.
- Runtime: `SurtrBridge.Register<T>(runtime, policy)` y `ScanAndRegister(runtime, policy, types)`.
- Clase: `SurtrNativeTypeAttribute.NamingPolicy`.
- Miembro: `SurtrNativeMemberAttribute.NamingPolicy`.
- Por defecto (`Surtr`): tipos PascalCase, miembros camelCase (`Add` → `add`).

## 5. Semántica de marshaling

| Tipo CLR | Descriptor Surtr | Comportamiento |
|---|---|---|
| `sbyte..ulong` | `I` | se convierte a `int` |
| `float`/`double`/`decimal` | `F` | se convierte a `double` |
| `bool` | `B` | — |
| `char` | `C` | — |
| `string` | `S` | se interna como `SurtrString` |
| enum registrado | descriptor del enum | **enum Surtr completo** (objeto por valor, cacheado) |
| clase/struct `[SurtrNativeType]` | `N...` | se envuelve/desenvuelve; un struct `Inline = true` viaja como bloque de slots (§5.1) |
| delegate / `Action`/`Func` | `L(...)R` | **closure Surtr** (ambas direcciones) |
| `object` opaco | `Nsurtr:native` | `SurtrNativeProxy` |
| `T[]` | `A...` | `SurtrArray` |
| `Nullable<T>` | `?X` | ausencia como `null` |

- **Enums**: un enum C# es un enum Surtr real (clase sealed con cases). El bridge cachea un
  `SurtrNativeObject` por valor (rooteado) y lo cablea en los cases, de modo que `MyEnum.A` resuelve
  al mismo objeto y un `switch` exhaustivo compila a jump table. El marshaling es O(1) sin boxing.
- **Structs**: por defecto se empaquetan (boxing) y aparecen como clases normales. Con
  `[SurtrNativeType(Inline = true)]` se exponen como **tipo de valor**: un bloque de slots
  contiguos, sin asignacion. Ver §5.1.
- **Campos CLR**: son **campos nativos** reales (`SurtrNativeFieldInfo`, leídos/escritos por el VM
  vía entry points), no propiedades.
- **`out`**: se pliega al retorno — `void F(out int x)` → `int`; `bool TryGet(out int v)` → tupla
  `(bool, int)`. `ref`/`in` no tienen equivalente y **no se exponen** (warning).
  La tupla resultante es un **tipo de valor**, así que viaja como un bloque plano de slots: el
  cuerpo nativo escribe un slot por elemento y devuelve esa cuenta, que es lo que dice
  `ResultSlotCount`. No es una referencia a un `SurtrTuple` empaquetado — quien llama copia
  `ResultSlotCount` slots, y una sola referencia en el slot 0 dejaría el resto del bloque con lo
  que hubiera en la pila. `SurtrRuntime.Invoke` reempaqueta el bloque en un `SurtrTuple` al cruzar
  hacia el host, así que un llamador C# sigue recibiendo un valor único.
- **Genéricos**: solo formas cerradas. `typeof(Box<int>)` en reflexión, o `TypeArguments` en el
  atributo para el generador. Un genérico abierto sin forma cerrada se omite (warning).
- **Delegates**: un parámetro/retorno delegado se mapea a un closure (`L(...)R`). CLR→Surtr envuelve
  el delegado en un `SurtrClosure`; Surtr→CLR crea un delegado que invoca el closure.

### 5.1 Structs inline (`Inline = true`)

Un struct marcado `[SurtrNativeType(Inline = true)]` deja de ser un objeto del heap detras de una
referencia y pasa a ser un **tipo de valor de Surtr**: un tramo de slots contiguos.

```csharp
[SurtrNativeType(Module = "unity", Name = "Vector3", Inline = true)]
public struct Vector3
{
    public float X;
    public float Y;
    public float Z;

    public static Vector3 Of(float x, float y, float z) => new(x, y, z);

    public float SqrMagnitude() => (X * X) + (Y * Y) + (Z * Z);

    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
}
```

**El almacenamiento pasa a ser de Surtr.** Un `Vector3` son tres slots: leer `v.x` es una lectura de
slot que no entra en codigo del host, pasarlo copia tres slots, y `a + b` es una llamada de seis
slots de entrada y tres de salida que no asigna nada en el heap de Surtr. El struct CLR se
reconstruye a partir de los slots **solo** cuando un miembro nativo necesita uno. Sin `Inline`, el
mismo struct se envuelve en un `SurtrNativeObject` y cada acceso a un campo cruza la frontera.

**Es opt-in a proposito.** Un valor inline no tiene identidad — dos copias del mismo `Vector3` no se
pueden distinguir — asi que `===` deja de significar nada, sus campos pasan a ser de solo lectura y
nunca puede ser nulo. Esas son las semanticas que un tipo de valor debe tener, pero no son las que
tenia un struct empaquetado: cambiarlas en silencio para todos los structs cambiaria lo que
significa el codigo host que ya existe.

**Que es elegible.** Cada campo de instancia expuesto tiene que ser un primitivo de Surtr (entero,
flotante, booleano o caracter) u otro struct expuesto tambien con `Inline = true`. Un campo de
cualquier otro tipo — un `string`, un array, una clase — es una referencia a algo que el struct CLR
posee, y reconstruir el struct desde slots obligaria a decidir quien es el dueño del referente. El
scanner **rechaza el tipo** en vez de exponer la mitad. Un struct anidado pliega sus propios slots en
el tramo, asi que un `Bounds` de dos `Vector3` son seis slots, no dos referencias; hay que
registrarlo despues del que contiene, el mismo orden que ya necesita una clase base.

**Tres cosas no se pueden hacer, y las tres fallan con un error claro en vez de compilar mal:**

| Que | Por que |
|---|---|
| Ocultar un campo con `[SurtrNativeIgnore]` | Dejaria un hueco en mitad del bloque y el struct CLR ya no se podria reconstruir. Un tipo inline son todos sus campos o ninguno. |
| Un struct sin campos de instancia | Un tipo de valor inline *es* sus campos; uno sin ninguno no es nada. |
| `Inline = true` sobre una clase o un enum | Solo un struct tiene representacion inline que pedir. |

**No se expone el constructor.** Un constructor de Surtr se alcanza asignando primero y ejecutando
el cuerpo contra la instancia nueva como receptor; un valor inline no tiene nada que asignar ni
receptor que rellenar — *es* su resultado. Una **fabrica estatica** cubre el caso exactamente y ya
funciona: un metodo estatico que devuelve el struct entrega el bloque plano, como `Vector3.Of` en el
ejemplo. (El agujero de fondo es mas amplio que los tipos inline: un constructor de una clase nativa
tampoco encaja hoy en el protocolo asignar-y-luego-inicializar.)

**Coste.** La ruta de reflexion asigna una caja por struct y por llamada, porque es la unica forma
sobre la que la reflexion puede escribir un campo. Es el precio del fallback, no del modelo: leer un
campo desde Surtr no pasa por ahi en absoluto, y el generador de codigo fuente emite conversiones
tipadas que no pagan esa caja.

## 6. Operadores, indexadores y comparación

Los operadores C# con equivalente Surtr se mapean a sus nombres `op_*`:

| C# | Surtr |
|---|---|
| `+ - * / %` | `op_+ - * / %` |
| `& \| ^ << >> >>>` | `op_& \| ^ << >> >>>` |
| `-` unario, `!`, `~`, `++`, `--` | `op_-u`, `op_!`, `op_~`, `op_++`, `op_--` |
| `==` | `op_==` |
| indexador `this[i]` | `op_[]` (lectura y escritura) |
| conversión explícita `(T)` | `op_as$<descriptor>` |
| `IComparable<T>.CompareTo` | **`op_<=>`** (deriva `<`, `<=`, `>`, `>=` y `<=>` en Surtr) |

Los operadores sin equivalente (`!=`, `< <= > >=` como declaraciones independientes, `op_UnaryPlus`,
`op_True`, `op_False`, `op_Implicit`) se **omiten con warning** (`SURTRINTEROP001`), porque Surtr
deriva `!=` de `==` y los relacionales de un único `<=>`.

## 7. Source generator

Generador `ISourceGenerator` (Roslyn 4.x) que, por cada tipo `[SurtrNativeType]`:

1. Emite **shims estáticos** `SurtrValue(SurtrCallArguments)` por método/constructor/campo/
   propiedad/operador/indexador/`CompareTo`.
2. Emite el **descriptor** y un método `__SurtrRegister` que usa
   `SurtrNativeEntryPoint.FromFunctionPointer(&shim)` (AOT-safe).
3. Emite el **catálogo** `SurtrBindings.RegisterAll(runtime)`.

**`partial` vs no-`partial`**: para una clase `partial`, el generador emite
`public partial class X { ... }` con los shims (pueden acceder a privados); para una no-`partial`,
emite `internal static class SurtrGenerated_X` (solo accede a públicos).

**Requiere `unsafe`** en el proyecto consumidor (sección 2).

## 8. Fallback por reflexión

`SurtrBridge.ScanAndRegister(runtime, typeof(Player), ...)` escanea los atributos en ejecución y
construye los mismos descriptores. Los shims se emiten con `AssemblyBuilder` (no `DynamicMethod`:
su `MethodHandle` no produce function pointer). **No AOT-safe** — úsalo solo donde el generador no
esté disponible.

## 9. Diagnostics del source generator

| Id | Severidad | Situación |
|---|---|---|
| `SURTRINTEROP001` | Warning | miembro no expuesto (operador sin equivalente, `ref`/`in`, abstract, indexador multidim, genérico abierto) |
| `SURTRINTEROP002` | Error | `TypeDescriptor`/`ReturnDescriptor` no es un descriptor Surtr bien formado |
| `SURTRINTEROP003` | Error | `TypeArguments` no coincide con la aridad del genérico |
| `SURTRINTEROP004` | Error | tipo `static` no puede registrarse como tipo nativo |

## 10. Ejemplo completo

```csharp
using Surtr.Interop.Attributes;

[SurtrNativeType(Module = "game", Name = "Inventory")]
public class Inventory : IComparable<Inventory>
{
    public int Capacity;
    public int Count { get; set; }

    public void Add(int itemId) { /* ... */ }

    public bool TryTake(out int itemId) { itemId = 0; return true; }

    public int this[int i] => i;

    public static Inventory operator +(Inventory a, Inventory b) => a;

    public int CompareTo(Inventory? other) => Count.CompareTo(other?.Count ?? 0);
}
```

```csharp
using var runtime = new SurtrRuntime();

// Con el source generator:
Surtr.Interop.SurtrBindings.RegisterAll(runtime);

// Sin el generador:
// Surtr.Interop.SurtrBridge.ScanAndRegister(runtime, typeof(Inventory));

var inventory = runtime.WrapNative(
    runtime.TryGetNativeClass("game:Inventory", out var cls) ? cls! : throw new InvalidOperationException(),
    new Inventory());

// En código Surtr: `var i: game.Inventory = ...; i.Add(3); var ok = i.tryTake(); i[0];`
```

Expone en Surtr: campo `capacity`, propiedad `count` (lectura/escritura), métodos `add`/`tryTake`
(retorno tupla `(bool, int)`), indexador `operator[]`, `operator+` y `operator<=>` (con sus
relacionales derivados).

## 11. Diseño y plan

El diseño completo (decisiones, hallazgos de runtime, fases) está en
`docs/Plan-Bridge-CSharp-Atributos-SourceGenerator.md`. La documentación de runtime está en
`docs/Runtime-Model.md` (§5.5 native fields/enums), `docs/Language-Syntax.md` (§10) y
`docs/Opcodes.md` (rama nativa en field ops).