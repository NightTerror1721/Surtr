# Plan: bridge Surtr <-> C# — atributos, escaneo CLR y source generator

**Estado: DISEÑO EN REVISIÓN (v2).** Nada implementado todavía; este documento se presenta para
validación antes de escribir código. La red de seguridad de cada fase es `dotnet build Surtr.sln` +
`dotnet test Surtr.sln`, como en el resto de planes del repo.

## 1. Contexto y objetivos

Surtr ya tiene toda la superficie de runtime para exponer tipos del host como clases nativas:
`SurtrRuntime.DefineNativeClass`, `SurtrNativeMethodInfo` + `SurtrNativeEntryPoint`
(puntero managed `SurtrNativeFunction(SurtrCallArguments) -> SurtrValue`),
`SurtrRuntime.DefineNativeBody` (cuerpos por *link name*), `SurtrNativeObject`/`SurtrNativeProxy`,
y los builders `SurtrModuleBuilder`/`SurtrClassBuilder`. Falta la capa que convierte una
clase/struct C# decorada con atributos en esa maquinaria, de forma declarativa.

Objetivos:

1. Atributos declarativos que describan qué tipos y miembros CLR se exponen y cómo.
2. Escaneo de clases/structs/enums CLR que produzca un modelo intermedio de descriptores,
   derivando de los atributos o de la firma C# cuando el atributo no lo especifica.
3. Bridge que materialice ese modelo en metadatos Surtr (clases nativas, métodos, **campos**
   nativos, propiedades, enums) y marshaler que convierta valores CLR <-> `SurtrValue`.
4. Source generator que genere en compilación los shims estáticos AOT-safe, el catálogo de
   registro y el enlace del tipo host, distinguiendo clases `partial` de no-`partial`.
5. Alternativa por reflexión que haga lo mismo cuando el generador no corre.
6. Registro flexible: global, por runtime, o por módulo.
7. Nomenclatura configurable de tipos y miembros (política a nivel miembro/clase/módulo/runtime/global).

## 2. Decisiones confirmadas

| # | Decisión | Elección |
|---|---|---|
| D1 | Ubicación de atributos | Assembly propio sin dependencias: `Surtr.Interop.Attributes` (netstandard2.0) |
| D2 | AOT / Unity | Unity moderno (2021.2+); el camino del generador es AOT-safe (function pointers) |
| D3 | Formato de `TypeDescriptor`/`ReturnDescriptor` | Descriptor canónico Surtr (`SurtrClassReference`) |
| D4 | Enums | Enum Surtr **completo**: un objeto por valor, cacheado y rooteado |
| D5 | Disparo del registro | Catálogo generado `SurtrBindings.RegisterAll` + API explícita |
| D6 | Módulo por defecto | Sin `Module` -> registro global (`DefineNativeClass`) |
| D7 | Parámetros `out` | Se mapean a **retorno extra** (tupla) |
| D8 | Genéricos CLR | Solo se exponen **formas cerradas** |
| D9 | Operadores | Indexador -> `operator[]`; operadores con equivalente Surtr -> mapeo directo; el resto -> ignorar + warning |
| D10 | Delegates | Se mapean a **closure** Surtr |
| D11 | Campos nativos | Sí, en v1: `SurtrNativeFieldInfo` + rama nativa en `FieldGet`/`FieldSet` |
| D12 | Nomenclatura | Política configurable (`Default` = convención Surtr), con scope miembro/clase/módulo/runtime/global |

## 3. Hallazgos del runtime que condicionan el diseño

### 3.1 Campos nativos: no existen hoy, se añaden en v1

`FieldGet`/`FieldSet` hacen `(SurtrInstance)entities[...]` y leen `instance.Fields[slot]`
(`SurtrVirtualMachine.cs:2125-2144`). Un `SurtrNativeObject` no tiene array `Fields`, así que hoy
no hay forma de exponer un campo CLR como campo Surtr.

**Solución (v1, decisión D11):** añadir `SurtrNativeFieldInfo : SurtrFieldInfo` con un
`SurtrNativeEntryPoint` getter y otro setter, y una rama nativa en `FieldGet`/`FieldSet` (y en
`StaticFieldGet`/`StaticFieldSet`) que, si el campo es nativo, invoque el entry point en lugar de
leer/escribir un slot. Ver §11 para el detalle.

### 3.2 Las clases nativas no pueden ser enums (GAP #2)

`SurtrRuntime.DefineNativeClass` fija `isEnum: false` (`SurtrRuntime.cs:1194-1201`). Los enums Surtr
requieren `isEnum: true` + `AddEnumCase`.

**Cambio necesario en Surtr.Core (pequeño):** añadir `DefineNativeEnum(string fullName)` (o un
parámetro `bool isEnum` en `DefineNativeClass`) que construya el `SurtrClass` con `isEnum: true`.
`SurtrClass.AddEnumCase` ya existe y se reutiliza.

### 3.3 El patrón de registro ya está establecido

`SurtrStdlib.LoadInto` + `RegisterNativeBodies` (`SurtrStdlib.cs:181-220,352-369`) es el modelo:
registrar cuerpos por *link name* antes de cargar. El bridge reutiliza `DefineNativeBody` +
`SurtrNativeEntryPoint.FromFunctionPointer(&shim)`.

### 3.4 Análisis: enums Surtr completos con valores cacheados (D4)

Requisito: un enum C# no se convierte a `int` plano, sino a un **enum Surtr completo**, con un
objeto por cada valor (como los enums declarados en Surtr). Hay que evitar construir/boxear objetos
en cada conversión.

Cómo es un enum Surtr en runtime:

- Es una clase sealed (`isEnum: true`) con un `SurtrEnumCaseInfo` por valor; cada case es un campo
  estático read-only del propio tipo, cuya instancia se construye una vez (en un enum declarado en
  Surtr lo hace el static initializer; `SurtrClass.AddEnumCase` asigna el ordinal).
- La instancia de un case es un objeto Surtr normal (referencia), no un int.

Cómo se traduce a un enum **nativo**:

- La clase enum nativa es un `SurtrClass` con `isEnum: true` (TypeCode `Native`), creada con
  `DefineNativeEnum`. Sus instancias son `SurtrNativeObject` que envuelven el valor CLR boxeado
  (`(object)MyEnum.A`), igual que cualquier struct/valor nativo.
- **Problema a resolver — el boxing no es estable:** el CLR **no** cachea los enums boxeados;
  `(object)MyEnum.A == (object)MyEnum.A` es `false`. No se puede usar identidad de referencia como
  clave, ni reconstruir el boxed por llamada (asignación por conversión).
- **Solución:** una caché por enum (y por runtime, porque el `SurtrRef` pertenece a un heap concreto)
  `Dictionary<long, SurtrRef>` indexada por el valor subyacente (`Convert.ToInt64`), que guarda el
  único `SurtrNativeObject` por valor. Esos objetos se crean **una vez** en el registro y se
  **rootean** (`runtime.AddRoot`) para toda la vida del runtime; además se cablean en los campos
  estáticos de case del enum (ver §8.4), de modo que `MyEnum.A` en Surtr resuelve al mismo objeto
  cacheado.
- **Marshaling:** `CLR -> Surtr` es `cache[Convert.ToInt64(v)]` (O(1), cero boxing); `Surtr -> CLR`
  es `(TEnum)runtime.Resolve<SurtrNativeObject>(value).Target` (un unbox). Ni el int subyacente ni
  el ordinal se pierden: el objeto conserva el valor CLR, y el ordinal lo da `SurtrEnumCaseInfo`.

**Decisión final (rendimiento vs facilidad):** se **mantienen los objetos** (no ints), por dos
razones:

1. **Coherencia de tipos.** Un enum Surtr es una clase referencia con cases; si un método nativo
   declarado para devolver `MyEnum` devolviera un `int` crudo, el slot de tipo referencia quedaría
   con un valor de familia `Integer` y se rompería el sistema de tipos. Solo los objetos hacen que
   `doSomething(MyEnum.A)` y `var e = MyEnum.A` funcionen sin conversión manual (facilidad).
2. **El coste no es "demasiado pesado".** La caché elimina toda asignación por llamada: en el camino
   caliente `CLR -> Surtr` es un único *load* de array o un *lookup* de diccionario + `CreateReference`;
   `Surtr -> CLR` es un *load* del entity registry + un unbox. No hay boxing por llamada.

**Optimización de la caché:** cuando los valores subyacentes del enum son contiguos (`0..N-1`, el
caso habitual), la caché es un **array indexado por ordinal** (`SurtrRef[]`), de modo que
`CLR -> Surtr` es literalmente `cache[unchecked((int)v)]` — un bounds-check + un load, indistinguible
de marshallear un int. Solo los enums no contiguos o `[Flags]` caen a un `Dictionary<long, SurtrRef>`.

**Cambio necesario en Surtr.Core (pequeño):** un helper para (a) declarar el case y (b) escribir la
referencia del objeto cacheado en el `StaticAddress` del campo tras el link — acceso que hoy es
`internal`. Ver §11.

## 4. Arquitectura general

### 4.1 Proyectos nuevos

```
src/Surtr.Interop.Attributes/      netstandard2.0   (cero dependencias)
src/Surtr.Interop/                 netstandard2.1   (ref: Surtr.Core + Surtr.Interop.Attributes)
src/Surtr.Interop.SourceGenerator/ netstandard2.0   (Roslyn; ref: Surtr.Interop.Attributes)
```

- `Surtr.Interop.Attributes` — atributos + `SurtrInteropVisibility` + `SurtrNamingPolicy` + la
  pequeña lógica de nomenclatura (§10), sin `Surtr.Core`.
- `Surtr.Interop` — runtime del bridge: modelo intermedio, scanner por reflexión, marshaler,
  materializador, caché de enums y API de registro.
- `Surtr.Interop.SourceGenerator` — generador incremental de Roslyn; reconoce los atributos por su
  nombre completo en los símbolos, sin cargar ensamblados de runtime.

### 4.2 Flujo de datos

```
  [atributos en código C#]
      +--- (compilación) --- Surtr.Interop.SourceGenerator --- shims + catálogo (código fuente)
      +--- (ejecución, fallback) --- ReflectionTypeScanner ------------+
                                                                       v
                                                              NativeTypeDescriptor
                                                                       |
                                                        SurtrTypeMaterializer + SurtrMarshaler
                                                                       |
                     SurtrRuntime.DefineNativeClass/DefineNativeEnum + DefineNativeBody
                     + DefineNativeField + caché de enums
                     o SurtrModuleBuilder (registro en módulo)
```

**Ambas vías convergen en el mismo `NativeTypeDescriptor`.** El materializador no sabe de dónde
vino el descriptor.

### 4.3 Shims estáticos (AOT-safe)

Cada método/constructor/accessor/campo expuesto necesita un `SurtrNativeEntryPoint`
`SurtrValue(SurtrCallArguments)`. El generador emite, por miembro, un shim estático y lo enlaza con
`FromFunctionPointer(&shim)`:

```csharp
static SurtrValue __SurtrInvoke_MyType_DoWork(SurtrCallArguments args) {
    var target = args.Runtime.Resolve<SurtrNativeObject>(args[0]).TargetAs<MyType>();
    int a = args.GetInt(1);
    double b = args.GetFloat(2);
    int result = target.DoWork(a, b);
    return SurtrValue.CreateInt(result);
}
```

Esto es AOT-safe (sin reflection, sin `Marshal`, sin `Delegate.CreateDelegate`). El fallback por
reflexión es el único camino que paga reflection, marcado como no-AOT.

## 5. El sistema de atributos

Namespace `Surtr.Interop.Attributes`. Ninguno depende de `Surtr.Core`.

### 5.1 Enum de visibilidad (para no acoplar a Surtr.Core)

```csharp
public enum SurtrInteropVisibility : byte {
    Private = 0, Internal = 1, Protected = 2, Public = 3,
}
```

Se mapea 1:1 a `Surtr.Runtime.Classes.SurtrVisibility` en la materialización.

### 5.2 `SurtrNativeTypeAttribute` (clase, struct o enum)

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum,
                AllowMultiple = true)]
public sealed class SurtrNativeTypeAttribute : Attribute {
    public string? Module { get; set; }               // módulo Surtr; null -> registro global
    public string? Name { get; set; }                 // nombre Surtr; null -> nombre CLR del tipo
    public string? Description { get; set; }
    public SurtrNamingPolicy? NamingPolicy { get; set; }   // política de nomenclatura (§10)
    public Type[]? TypeArguments { get; set; }        // formas cerradas de un tipo genérico (D8)
}
```

- `Module` es un path punteado (`game.entities`). Full name del descriptor: `<Module>:<Name>`, o
  solo `<Name>` si no hay módulo (global). `Name` null -> nombre simple del tipo CLR.
- `AllowMultiple` + `TypeArguments` expresan formas cerradas: un tipo genérico se expone una vez por
  cada aplicación del atributo con un `TypeArguments` concreto (p. ej.
  `[SurtrNativeType(TypeArguments = new[] { typeof(int) })]` sobre `Box<T>` expone `Box<int>`). Sin
  `TypeArguments`, un tipo genérico abierto se **omite** con diagnóstico (D8).

### 5.3 `SurtrNativeMemberAttribute` (base, opcional)

```csharp
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor |
                AttributeTargets.Field | AttributeTargets.Property)]
public class SurtrNativeMemberAttribute : Attribute {
    public string? Name { get; set; }                 // override del nombre Surtr
    public string? Description { get; set; }
    public SurtrInteropVisibility? Visibility { get; set; }
    public SurtrNamingPolicy? NamingPolicy { get; set; }   // política a nivel de miembro (§10)
    public bool Expose { get; set; } = true;          // false = no exponer
}
```

Es `class` (no `sealed`) porque es base de Method/Field/Property. Si `Name` y `NamingPolicy` se
especifican a la vez, `Name` gana.

### 5.4 `SurtrNativeMethodAttribute` (método o constructor)

```csharp
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor)]
public sealed class SurtrNativeMethodAttribute : SurtrNativeMemberAttribute {
    public string? ReturnDescriptor { get; set; }   // descriptor Surtr del retorno
}
```

`ReturnDescriptor` sobreescribe el tipo de retorno Surtr e implica convertir el dato CLR al tipo
indicado (si existe). Sin él, se usa el tipo adaptado del retorno del método. Los `out` se añaden
como retorno extra (D7, §7).

### 5.5 `SurtrNativeFieldAttribute` (campo)

```csharp
[AttributeUsage(AttributeTargets.Field)]
public sealed class SurtrNativeFieldAttribute : SurtrNativeMemberAttribute {
    public bool ReadOnly { get; set; }          // default false
    public string? TypeDescriptor { get; set; } // tipo Surtr al que convertir
}
```

Con D11 los campos se exponen como **campos** Surtr (no como propiedades): `ReadOnly` se traduce al
`isReadOnly` del `SurtrNativeFieldInfo`, no a "solo getter".

### 5.6 `SurtrNativePropertyAttribute` (propiedad)

```csharp
[AttributeUsage(AttributeTargets.Property)]
public sealed class SurtrNativePropertyAttribute : SurtrNativeMemberAttribute {
    public string? TypeDescriptor { get; set; }
}
```

Nota de diseño: **hereda de `SurtrNativeMemberAttribute` (no de `SurtrNativeFieldAttribute`)**,
porque `ReadOnly` no debe existir aquí: se extrae de si la propiedad tiene getter y setter públicos
(solo getter -> read-only; getter+setter -> lectura/escritura).

### 5.7 `SurtrNativeParameterAttribute` (parámetro de método/constructor)

```csharp
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class SurtrNativeParameterAttribute : Attribute {
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? TypeDescriptor { get; set; }
}
```

Independiente (no hereda de Member): `Visibility`/`Expose` no aplican a un parámetro.

### 5.8 `SurtrNativeIgnoreAttribute` (atajo a Expose=false)

```csharp
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor |
                AttributeTargets.Field | AttributeTargets.Property)]
public sealed class SurtrNativeIgnoreAttribute : Attribute { }
```

Semánticamente equivalente a `[SurtrNativeMember(Expose = false)]`.

### 5.9 Política de escaneo por defecto

Un tipo con `[SurtrNativeType]` expone **todos sus miembros públicos** (métodos, constructores,
campos, propiedades, operadores, indexadores) con valores derivados de la firma C#. Los atributos de
miembro son opcionales y solo **sobrescriben** nombre/descripción/visibilidad/tipo/política.
`[SurtrNativeIgnore]` (o `Expose=false`) excluye un miembro. Los miembros no públicos se ignoran
salvo que lleven un atributo de miembro explícito.

## 6. Modelo intermedio (`NativeTypeDescriptor`)

Tipos POCO en `Surtr.Interop`, producidos por el generador (en compilación) o por el scanner de
reflexión (en ejecución), y consumidos por el materializador.

```
NativeTypeDescriptor
    FullName      : string            // "Mod:Tipo" o "Tipo"
    Module        : string?           // null -> global
    Name          : string
    Description   : string?
    Kind          : Class | Struct | Enum | Delegate
    BaseType      : string?           // full name de la clase base nativa, si la hay
    TypeArguments : string[]          // descriptores; vacío si no es forma cerrada genérica
    Members       : NativeMemberDescriptor[]
    EnumCases     : string[]          // solo enums (nombres en orden de declaración)
    IsPartial     : bool              // solo informativo para el generador

NativeMemberDescriptor (base)
    Name, Description, Visibility (SurtrInteropVisibility), Expose, NamingPolicy

NativeMethodDescriptor : NativeMemberDescriptor
    ReturnDescriptor : string?          // null -> derivar del retorno CLR
    Parameters       : NativeParameterDescriptor[]
    IsStatic, IsConstructor, IsVirtual
    LinkName         : string           // derivado o declarado
    Operator         : SurtrOperator?   // si es op_/indexador (D9)
    OutParameters    : int              // nº de `out` añadidos al retorno (D7)

NativeFieldDescriptor : NativeMemberDescriptor
    TypeDescriptor : string?
    ReadOnly       : bool               // ahora campo nativo de verdad (D11)

NativePropertyDescriptor : NativeMemberDescriptor
    TypeDescriptor : string?
    HasGetter, HasSetter               // públicos; define read-only

NativeParameterDescriptor
    Name, Description, TypeDescriptor

NativeDelegateDescriptor : NativeTypeDescriptor   // D10
    ParameterDescriptors : string[]  // parámetros del closure
    ReturnDescriptor     : string
```

Reglas de derivación (cuando el atributo no especifica):

- `Name` = nombre CLR del miembro (ya transformado por la política de nomenclatura §10);
  `Description` = null; `Visibility` = `Public`.
- `TypeDescriptor`/`ReturnDescriptor` = descriptor Surtr derivado del tipo CLR (ver §7).
- `LinkName` = `<fullName>.<nombreMiembro>` (o `<fullName>.ctor` para constructores), el mismo
  formato que `SurtrNativeMethodInfo.DeriveLinkName` usa con `owner + NameSeparator + SignatureKey`
  simplificado. El host puede publicar cuerpos con `DefineNativeBody` bajo ese nombre.

## 7. Marshaling (CLR <-> SurtrValue)

El marshaler convierte entre un valor CLR y un `SurtrValue` (y viceversa), guiado por el descriptor
Surtr del parámetro/campo/retorno. Matriz de conversión v1:

| Tipo CLR | Descriptor Surtr por defecto | Dirección CLR->Surtr | Dirección Surtr->CLR |
|---|---|---|---|
| sbyte/byte/short/ushort/int/uint/long/ulong | `I` | `SurtrValue.CreateInt((int)v)` | `(int)value.AsInt` |
| float/double/decimal | `F` | `CreateFloat((double)v)` | `(double)value.AsFloat` |
| bool | `B` | `CreateBool` | `value.AsBool` |
| char | `C` | `CreateChar` | `value.AsChar` |
| string | `S` | `NewString` + referencia | `SurtrString` -> `string` |
| enum CLR registrado | descriptor del enum nativo | `cache[valor]` (referencia cacheada) | `(TEnum)obj.Target` (unbox) |
| clase/struct CLR con `[SurtrNativeType]` | `N<mod>:<Nombre>` | `WrapNative(target)` | `Resolve<SurtrNativeObject>().TargetAs<T>()` (structs: box/unbox) |
| delegate / `Action`/`Func` | `L(params)ret` | envolver en `SurtrClosure` (§7.1) | delegado que invoca el closure (§7.1) |
| object (opaco) | `Nsurtr:native` | `WrapNative` (proxy) | `Resolve<SurtrNativeProxy>().Target` |
| `SurtrValue` / `SurtrObject` | passthrough | identidad | identidad |
| `SurtrCallArguments` (receiver de método) | — | — | `args[0]` |
| arrays (`T[]`) | `A<elem>` | construir `SurtrArray` | `SurtrArray` -> `T[]` |
| `Nullable<T>` | `?<prim>` o referencia | `CreateAbsent` / `Null` | ausente -> `null` |

### 7.1 Enums (D4)

Ver §3.4: la conversión usa la caché por runtime (`Dictionary<long, SurtrRef>`), nunca boxea en el
camino caliente, y el valor/ordinal se conservan en el objeto cacheado.

### 7.2 `out` como retorno extra (D7)

Los parámetros `out` se eliminan de la lista de parámetros Surtr y se añaden al retorno:

- `void F(out int x)` -> retorna `int`.
- `void F(out int x, out bool y)` -> retorna tupla `(int, bool)`.
- `bool TryGet(int k, out int v)` -> retorna tupla `(bool, int)`.

El shim generado declara las variables `out`, llama al método, y empaqueta `(retorno, out1, out2…)`
en un `SurtrTuple` (o devuelve el único `out` directamente). Los parámetros `ref`/`in` no tienen
equivalente y se rechazan con diagnóstico (son bidireccionales; `in` podría ignorarse en el futuro).

### 7.3 Delegates <-> closure (D10)

- **CLR -> Surtr (un delegate como argumento):** se envuelve el delegado en un `SurtrNativeProxy`
  (para poder capturarlo), y se crea un `SurtrClosure` cuyo método es un shim nativo que (a) recibe
  el proxy como upvalue, (b) desempaqueta los argumentos del closure, (c) invoca el delegado y (d)
  marshallea el resultado. El tipo del closure es `L(params)ret`, derivado de la firma del delegado.
  Funciona igual para `System.Action`/`System.Func` y para tipos delegado propios.
- **Surtr -> CLR (un closure como retorno/argumento de método C#):** el shim generado crea el
  delegado CLR con una **lambda capturadora** normal:
  `Action<int> a = x => runtime.InvokeClosure(closure, SurtrValue.CreateInt(x));` — código C# común
  que el generador emite, AOT-safe (no usa reflection ni `Expression.Compile`; la clase capturadora
  es código generado ordinario). Para tipos delegado propios se genera una lambda con la firma
  exacta del delegado. La vía por reflexión usa `Delegate.CreateDelegate` contra un adaptador
  genérico (no-AOT, coherente con la naturaleza del fallback).

Conclusión de diseño: ambas direcciones soportadas; el camino del generador es 100% AOT-safe
(lambdas capturadoras emitidas), el del fallback usa reflection como el resto del fallback.

### 7.4 Structs

Se empaquetan (boxing): al entrar `box`, al salir `unbox`. Un struct CLR aparece en Surtr como una
clase normal (Surtr no tiene structs).

### 7.5 Sobreescritura por descriptor

`ReturnDescriptor`/`TypeDescriptor` sobreescriben el descriptor; el marshaler usa el descriptor
indicado, no el tipo CLR real. Si el descriptor es irresoluble se lanza en registro, no en la llamada.
Conversiones de precisión int<->float se hacen con cast explícito en el shim, con pérdida documentada.

## 8. El bridge / materializador (`SurtrTypeMaterializer`)

Consume un `NativeTypeDescriptor` y produce metadatos Surtr. Dos destinos:

### 8.1 Registro global / por runtime (`SurtrRuntime`)

```
runtime.DefineNativeClass(fullName, baseClass)   // o DefineNativeEnum si Kind == Enum
   + por cada método/constructor: SurtrNativeMethodInfo + entryPoint (DefineNativeBody)
   + por cada campo: DefineNativeField (SurtrNativeFieldInfo, §11)
   + por cada propiedad: SurtrClass.AddProperty(...) con accessors nativos
runtime.FinishNativeClass(clase)
```

Los cuerpos se publican primero con `DefineNativeBody(linkName, entryPoint)`; el materializador los
enlaza a los `SurtrNativeMethodInfo`. Los campos usan `SurtrNativeFieldInfo` con getter/setter
nativos (D11); las propiedades usan `SurtrPropertyInfo` con accessors nativos `get_x`/`set_x`.

### 8.2 Registro en módulo (`SurtrModuleBuilder`)

Cuando el host quiere que el tipo viva en un módulo Surtr concreto (o cuando `Module` está en el
atributo), el materializador declara en el builder con `DeclareNativeMethod` (solo *link name*, sin
cuerpo) y el host publica los cuerpos con `DefineNativeBody` antes de `LoadModule`. Es el patrón
exacto de `SurtrStdlib`.

### 8.3 El catálogo (`SurtrBindings`)

```csharp
public static class SurtrBindings {
    public static void RegisterAll(SurtrRuntime runtime) { ... }          // global + por runtime
    public static void Register<T>(SurtrRuntime runtime) { ... }          // un tipo
    public static void RegisterIntoModule(SurtrModuleBuilder module) { ... }
    public static void RegisterIntoModule<T>(SurtrModuleBuilder module) { ... }
}
```

`RegisterAll` registra primero los enums y las clases base (orden topológico por herencia) para que
los `SurtrTypeHandle` resuelvan; después los cuerpos, y por último `FinishNativeClass`.

### 8.4 Materialización de enums nativos (D4)

Para un `NativeTypeDescriptor` con `Kind == Enum`:

1. `DefineNativeEnum(fullName)` -> `SurtrClass` (`isEnum: true`).
2. Por cada case, en orden de declaración: `AddEnumCase(nombre)` -> `SurtrEnumCaseInfo` (ordinal).
3. Crear el objeto cacheado por valor: `runtime.WrapNative(enumClass, (object)valor)` y
   `runtime.AddRoot(objeto)`; registrar en `Dictionary<long, SurtrRef>`.
4. Tras `FinishNativeClass`, escribir la referencia de cada objeto en el `StaticAddress` de su campo
   de case (helper en Surtr.Core, §11). Así `MyEnum.A` en Surtr es el mismo objeto cacheado.

### 8.5 Nomenclatura aplicada

El materializador recibe nombres ya finales (la política se aplicó al construir el descriptor). Para
la vía por reflexión, la política se resuelve con el orden de precedencia de §10; para el generador,
los nombres se resuelven en compilación con la misma regla.

## 9. Source generator

Generador incremental (`IIncrementalGenerator`), netstandard2.0, Roslyn 4.x.

### 9.1 Qué genera

Por cada tipo con `[SurtrNativeType]` (y por cada forma cerrada, D8):

1. **Shims estáticos** — un `static SurtrValue __SurtrInvoke_<Tipo>_<Miembro>(SurtrCallArguments)`
   por método/constructor/accessor/campo expuesto (§4.3), más un shim getter y otro setter por campo
   nativo, y el shim del closure por delegate (D10). Usa `FromFunctionPointer(&shim)`, AOT-safe.
2. **Registro del tipo** — un método que materializa el `NativeTypeDescriptor` (o que llama
   directamente a `DefineNativeClass`/`DefineNativeMethod`/`DefineNativeField`), de modo que no
   dependa del scanner de reflexión.
3. **Catálogo** — la clase `SurtrBindings` (§8.3).

### 9.2 `partial` vs no-`partial`

- **Clase `partial`**: el generador emite un `public partial class <Tipo> { ... }` que añade el
  código de enlace (shims + `Register`) como parte del propio tipo. Ventaja: los shims pueden ser
  `private static` dentro del tipo y acceder a miembros privados.
- **Clase no-`partial`**: el generador emite una clase auxiliar separada
  `internal static class SurtrGenerated_<Tipo> { ... }` con los shims y el registro. Los shims solo
  acceden a miembros **públicos** (o `InternalsVisibleTo` hacia el assembly generado si hace falta;
  por defecto, públicos).

En ambos casos el catálogo `SurtrBindings` es una clase estática separada, siempre generada.

### 9.3 Distinción en el momento de generar

El generador lee `INamedTypeSymbol` y decide con `symbol.IsPartial`/los `SyntaxReference` si el
tipo es `partial`. Esto es la rama exacta que pide el requisito: si `partial`, el código de conexión
se añade como parte del tipo; si no, en la clase auxiliar.

### 9.4 Operadores e indexadores (D9)

El generador detecta `op_*` e indexadores (`this[...]`) y los mapea:

| C# | Surtr |
|---|---|
| indexador `this[i]` (lectura) | `operator[]` |
| indexador `this[i]` con setter | `operator[]` retornando `void` (2 parámetros) |
| `op_Addition/Subtraction/Multiply/Division/Modulus` | `operator+ - * / %` |
| `op_BitwiseAnd/Or/ExclusiveOr/LeftShift/RightShift/UnsignedRightShift` | `operator& \| ^ << >> >>>` |
| `op_UnaryNegation` | `operator-` (unario) |
| `op_LogicalNot`, `op_OnesComplement` | `operator!`, `operator~` |
| `op_Increment`, `op_Decrement` | `operator++`, `operator--` |
| `op_Equality` | `operator==` |
| `op_Explicit` (conversión explícita) | `operator as T` |
| `op_Inequality`, `op_LessThan/GreaterThan/LessThanOrEqual/GreaterThanOrEqual` | ignorar + warning (Surtr deriva `!=` de `==` y los 4 relacionales de un único `operator<=>`; no hay equivalente 1:1) |
| `op_UnaryPlus`, `op_True`, `op_False`, `op_Implicit` | ignorar + warning (sin equivalente Surtr) |

La regla es la de D9: si hay equivalente, se mapea; si no, se omite el miembro y se emite un warning
en compilación (y el scanner de reflexión registra el aviso equivalente).

### 9.5 AOT / IL2CPP

Nada del camino del generador usa reflection en ejecución: solo `FromFunctionPointer(&shim)`.
Compatible con IL2CPP. El generador registra `DiagnosticDescriptor` para reportar tipos expuestos no
soportados (p. ej. `ref`/`in`, genéricos abiertos sin `TypeArguments`) en compilación.

## 10. Política de nomenclatura (D12)

Enum en `Surtr.Interop.Attributes`:

```csharp
public enum SurtrNamingPolicy {
    Default,     // = Surtr (convención del lenguaje)
    Surtr,       // tipos PascalCase; miembros camelCase; paths de módulo en minúsculas
    PascalCase,  // tipos y miembros tal cual C# (sin adaptar)
    CamelCase,   // tipos y miembros con primera letra en minúscula
    SnakeCase,   // snake_case (DoWork -> do_work; HTTPResponse -> http_response)
    LowerCase,   // todo en minúsculas
    UpperCase,   // todo en mayúsculas
}
```

Reglas:

- **`Surtr`/`Default`**: los tipos conservan PascalCase (coincide con C#); los **miembros** se
  adaptan a camelCase (`DoWork` -> `doWork`) porque C# los escribe PascalCase y Surtr camelCase.
  Es el "adaptar los nombres cuando haga falta".
- **Transformación** aplicada a nombres de tipo y de miembro por separado, mediante un helper puro
  `SurtrNaming.Apply(string clrName, SurtrNamingPolicy policy)` (en `Surtr.Interop.Attributes`); el
  generador usa una copia espejo en compilación (misma regla, duplicada a propósito como el resto de
  reglas que comparten compilador y runtime).
- **Precedencia** (de menor a mayor): global < runtime < módulo < clase < miembro. Cada nivel tiene
  un `NamingPolicy?`; el primero no nulo gana; si ninguno, `Surtr`.
- **Dónde se configura**: global = propiedad estática del registry; runtime = parámetro/opción de
  `Register*`; módulo = parámetro de `RegisterIntoModule`; clase = `SurtrNativeTypeAttribute.NamingPolicy`;
  miembro = `SurtrNativeMemberAttribute.NamingPolicy`.
- El nombre resultante alimenta `Name`, los `LinkName` y las claves de `SurtrBindings`, de modo que
  la política es coherente en todo el camino (descriptor, cuerpo y catálogo).

## 11. Cambios necesarios en Surtr.Core

1. **`SurtrRuntime.DefineNativeEnum(string fullName)`** (o `bool isEnum` en `DefineNativeClass`) —
   cierra el GAP #2 (§3.2).
2. **Helper de case de enum nativo** — declarar el case y escribir la referencia del objeto cacheado
   en el `StaticAddress` del campo tras el link (hoy `internal`). Por ejemplo
   `SurtrRuntime.DefineNativeEnumCase(SurtrClass, string name, SurtrNativeObject value)` que
   internamente hace `AddEnumCase` + guarda el valor para sellarlo en `FinishNativeClass`.
3. **`SurtrNativeFieldInfo : SurtrFieldInfo`** (D11) — campos nativos reales:
   - Campos nuevos: `SurtrNativeEntryPoint? NativeGetter`, `NativeSetter` (o un único entry point con
     modo). Un campo nativo no tiene `SlotIndex` de instancia (sigue `-1`).
   - `SurtrRuntime.DefineNativeField(...)` / `SurtrClassBuilder.DefineNativeField(...)` para
     declararlo.
   - **Rama en el VM**: `FieldGet`/`FieldSet` (y `StaticFieldGet`/`StaticFieldSet`) comprueban si el
     campo es `SurtrNativeFieldInfo` y, en ese caso, invocan el entry point con el receptor
     (`args[0]` = el `SurtrNativeObject`) en vez de leer/escribir `instance.Fields[slot]`. Es un
     `is`/cast sobre el campo ya cargado (se leía `SlotIndex` de todos modos), coste acotado en el
     camino caliente.
   - El shim generado hace `target.X` (getter) o `target.X = v` (setter) sobre el `SurtrNativeObject`.

Cambios opcionales (fuera de v1): ninguno obligatorio más allá de lo anterior; el resto de la
maquinaria (clases nativas, métodos nativos, propiedades nativas, cuerpos por link name) ya existe.

## 12. Plan por fases

Cada fase termina en `dotnet build Surtr.sln` + `dotnet test Surtr.sln` en verde, y un commit.

| Fase | Contenido | Depende de |
|---|---|---|
| F1 | `Surtr.Interop.Attributes` (atributos + `SurtrInteropVisibility` + `SurtrNamingPolicy` + `SurtrNaming`) | — |
| F2 | Surtr.Core: `DefineNativeEnum` + helper de case nativo + tests | — (paralelo a F1) |
| F3 | Surtr.Core: `SurtrNativeFieldInfo` + rama nativa en `FieldGet`/`FieldSet` + tests | — (paralelo a F1/F2) |
| F4 | Modelo intermedio `NativeTypeDescriptor*` en `Surtr.Interop` | F1 |
| F5 | `SurtrMarshaler` (matriz §7: primitivos, enum cacheado, out, delegates) + tests | F2, F4 |
| F6 | `SurtrTypeMaterializer` (registro global/runtime: clases, enums, campos nativos, propiedades) | F2, F3, F4 |
| F7 | `ReflectionTypeScanner` (fallback) + `SurtrBridge.ScanAndRegister` | F1, F6 |
| F8 | `SurtrTypeMaterializer` en módulo (`RegisterIntoModule`) | F6 |
| F9 | `Surtr.Interop.SourceGenerator` (shims + catálogo + partial/no-partial + operadores + out + formas cerradas) | F1 |
| F10 | Integración: `SurtrBindings.RegisterAll`, orden de herencia, enums, nomenclatura | F6, F9 |
| F11 | Documentación (`Language-Syntax.md` §10, `Runtime-Model.md`, `Opcodes.md` por campos nativos, README interop) | F10 |
| F12 | Suite completa + bench si procede | todas |

## 13. Limitaciones y decisiones pendientes

1. **`ref`/`in`**: se rechazan con diagnóstico (solo `out` está soportado, D7). `in` podría ignorarse
   en una fase posterior.
2. **Genéricos abiertos**: omitidos con diagnóstico; solo formas cerradas vía `TypeArguments` (D8).
3. **Operadores sin equivalente**: `!=`, relacionales (`< <= > >=`), `op_UnaryPlus`, `op_True`,
   `op_False`, `op_Implicit` se ignoran con warning (D9, §9.4). La conversión implícita de C# no
   tiene equivalente (Surtr solo tiene `operator as`, explícito).
4. **Delegates -> closure**: ambas direcciones soportadas (§7.3). El camino del generador usa
   lambdas capturadoras emitidas (AOT-safe); el fallback usa `Delegate.CreateDelegate` (no-AOT, como
   el resto del fallback). Sin limitación de aridad en el camino del generador (se genera una lambda
   por firma de delegado concreta).
5. **`InternalsVisibleTo`**: para que el generador de no-`partial` acceda a internos; por defecto
   públicos.
6. **Unity**: el paquete de interop debe empaquetarse como UPM; el generador va como analizador del
   proyecto (Roslyn 4.x soportado en Unity 2021.2+). Verificar el empaquetado en una fase final.
7. **Caché de enums por runtime** (D4): resuelto — caché **por runtime** (un `SurtrRef` pertenece a
   un heap concreto), con array indexado por ordinal cuando los valores son contiguos y diccionario
   en caso contrario (§3.4). No hay caché estática compartida: solo el boxed CLR sería compartible,
   no la referencia Surtr, y no aporta.










