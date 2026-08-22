# Informe y propuesta: Puente de funciones nativas y el tercer tipo de función nativa

> **Fecha:** 2026-08-22
> **Alcance:** `SurtrNativeEntryPoint.cs`, `SurtrNativeMethodInfo.cs`, `SurtrAbstractMethodInfo.cs`, `SurtrCallArguments.cs`, `SurtrMethodInfo.cs`, `SurtrTypeLinker.cs`, el dispatch del VM.
> **Objetivo:** documentar los dos mecanismos actuales de enlace nativo (punteros a función y delegados) y evaluar un **tercer tipo**: una función nativa cuyo "código" es un método abstracto proporcionado por una implementación (clase host/instancia).

---

## 1. Los dos mecanismos actuales de enlace nativo

Toda función nativa comparte **una sola firma plana**: `SurtrValue(SurtrCallArguments)` (`SurtrNativeFunction`, `SurtrNativeEntryPoint.cs:44`). La firma es **gestionada** (managed calling convention) invocada a través de un puntero de función gestionado: no hay stub reverse-P/Invoke ni transición de modo GC (`SurtrNativeEntryPoint.cs:20-24`). `SurtrCallArguments` es un `readonly unsafe ref struct` (`SurtrCallArguments.cs:41`) que no puede boxearse ni sobrevivir al frame; lleva puntero al bloque de `SurtrRawValue` + longitud + `SurtrRuntime`.

### Mecanismo (a): `FromFunctionPointer` — puntero a método estático (preferido)

```csharp
SurtrNativeEntryPoint.FromFunctionPointer(&MyStatic)   // SurtrNativeEntryPoint.cs:118-121
```

- **Resuelto en tiempo de compilación**, sin reflexión. **Seguro bajo AOT/IL2CPP.** `KeepAlive` es `null` (el puntero a un método estático es válido de por vida).
- **Requiere `unsafe`** solo en el código de registro, no en los cuerpos (`:106-111`).
- Solo métodos **estáticos gestionados**. Un puntero a código C/C++ (`GetProcAddress`, `Marshal.GetFunctionPointerForDelegate`) es comportamiento indefinido que puede "funcionar" en x64 Windows antes de romper en x86/ARM/IL2CPP (`:56-63`).

### Mecanismo (b): `FromDelegate` — delegado ordinario (conveniencia)

```csharp
SurtrNativeEntryPoint.FromDelegate(myStaticDelegate)   // SurtrNativeEntryPoint.cs:150-166
```

- Rechaza `null`, **multicast** (`GetInvocationList().Length != 1`, `:155-156`) y **métodos de instancia o lambdas capturadoras** (`Target is not null`, `:158-162`) — su puntero espera un receptor oculto que no encaja con la firma plana.
- Resuelve el puntero por **reflexión** (`MethodHandle.GetFunctionPointer()`, `:164`), que AOT/IL2CPP pueden eliminar; la doc recomienda usar la dirección directa bajo IL2CPP (`:143-146`).
- **Keep-alive:** el delegado se retiene en `_keepAlive` del struct (`:68,165`), que vive dentro de `SurtrNativeMethodInfo._entryPoint` (`SurtrNativeMethodInfo.cs:43`), retenido por la tabla de métodos de la clase y por `SurtrContext.NativeBodies` (`SurtrContext.cs:52`) durante toda la vida del contexto.
- **Coste de llamada idéntico** al mecanismo (a): ambos colapsan en el mismo `IntPtr` y `Invoke` emite la misma llamada indirecta (`SurtrNativeEntryPoint.cs:177-179`).

| | `FromFunctionPointer(&M)` | `FromDelegate(d)` |
|---|---|---|
| Resolución | Compile-time, sin reflexión | Reflexión |
| AOT/IL2CPP | Seguro | Puede ser eliminado |
| Registro | Requiere `unsafe` | Sin `unsafe` |
| Keep-alive | Ninguno | Delegado retenido en el entry point |
| Multicast | Imposible | Rechazado |
| Instancia/lambda | Imposible (compilador) | Rechazado (el hueco que el tercer tipo tapa) |
| Llamada en runtime | Indirecta directa | Idéntica |

**El hueco fundamental:** hoy **es imposible enlazar un método de instancia**. El estado de instancia solo puede llegar como argumento 0 (el receptor), resuelto dentro de un cuerpo estático.

## 2. Cómo se despacha una llamada a host hoy

Todos los opcodes de llamada desembocan en un bloque compartido `InvokeResolved` (alcanzado por `goto`, sin prólogo — `SurtrVirtualMachine.cs:3056-3061`):

```csharp
if (pendingMethod.ImplKind == SurtrMethodImplKind.Native)      // :3064
{
    SurtrRawValue* nativeArgumentBase = sp - pendingArguments;
    _sp = sp; current.IP = ip;                                  // safepoint publicado
    SurtrValue result = pendingClosure is null
        ? ((SurtrNativeMethodInfo)pendingMethod).EntryPoint
            .Invoke(new SurtrCallArguments(runtime, nativeArgumentBase, pendingArguments))
        : pendingClosure.EntryPoint.Invoke(...);
    sp = nativeArgumentBase;
    if (pendingResults != 0) *sp++ = result.Raw;
    _sp = sp;
    entities = context.EntityRegistry.Entities;                 // :3081 — el native pudo alojar/GC
    goto Dispatch;
}
```

**Coste exacto:** test de `ImplKind` (1 branch predecible) + construcción de `SurtrCallArguments` (3 palabras) + **una llamada indirecta gestionada** sin stub ni transición + reset de `sp`/push del resultado + recarga de `entities`. Idéntico para closures, llamadas directas, virtuales e interfaces.

## 3. El modelo de métodos: `SurtrMethodInfo`

Tres ejes ortogonales (`SurtrMethodInfo.cs`):

- **`SurtrMethodImplKind`** — dónde vive el cuerpo: `Bytecode`/`Native`/`Abstract` (`:11-21`).
- **`SurtrMethodDispatch`** — cómo se resuelve: `Direct`/`Virtual`/`Abstract` (`:32-45`).
- **`SurtrMethodRole`** — `Normal`/`Constructor`/`StaticInitializer` (`:59-69`).

**Vtable:** `SurtrClass.VirtualMethods: SurtrMethodInfo[]` indexada por `VTableSlot` (`SurtrClass.cs:121-126`, `SurtrMethodInfo.cs:219-224`). `BuildMethodTables` (`SurtrTypeLinker.cs:429-494`) copia la vtable base verbatim; `PlaceInVTable` (`:496-532`) reemplaza el slot **in situ** para los overrides (`:519`) — "replacing in place is the whole point: every existing call site picks up the override for free". El dispatch de interfaces guarda **índices de vtable, no métodos** (`:536-580`), de modo que un override posterior se propaga a toda interfaz que rute por el slot.

**Métodos abstractos hoy:** `SurtrAbstractMethodInfo` no tiene cuerpo (`SurtrAbstractMethodInfo.cs:16-51`); el loader espera que un derivado **reemplace el slot de la vtable** (`SurtrTypeLinker.cs:519`); `VerifyConcrete` (`:582-594`) rechaza una clase concreta que deje un slot `Abstract` sin implementar; llamarlo directamente lanza error (`SurtrVirtualMachine.cs:233-234`).

**Detalle clave para el tercer tipo:** el constructor de `SurtrNativeMethodInfo` **no rechaza** `dispatch: Abstract` (`SurtrNativeMethodInfo.cs:54-71`) y el base solo valida roles (`SurtrMethodInfo.cs:244-247`). Un `SurtrNativeMethodInfo` con `Dispatch.Abstract` se coloca en la vtable (porque `IsVirtualDispatch`) y **pasaría** `VerifyConcrete` (porque su `ImplKind` es `Native`). Es decir, **el runtime ya tolera la combinación "abstracto + nativo"**; lo que falta es que el compilador la produzca y un mecanismo de despacho host-side.

## 4. Conexión host→Surtr para clases/structs nativos (resumen)

- `runtime.DefineNativeClass(fullName, baseClass)` (`SurtrRuntime.cs:1149-1175`) → `SurtrClass` con `TypeCode = Native` y descriptor `N<fullName>;` (`SurtrClassReference.cs:400`); los miembros se cuelgan con `SurtrClass.AddMethod/AddField/AddProperty` públicos (`SurtrClass.cs:458-538`) construyendo `SurtrNativeMethodInfo` a mano; `runtime.FinishNativeClass` (`:1178-1182`) congela tablas y linkea.
- Las instancias se crean con `runtime.WrapNative(class, target)` (`:488`) o `runtime.RegisterNative(instance)` (`:499`), que registran un `SurtrNativeProxy`/`SurtrNativeObject` en el registry.
- Los bodies se publican por **link name** con `runtime.DefineNativeBody(linkName, entryPoint)` (`:1207-1216`); `BindNativeBodies` (`:880-916`) los ata al cargar el módulo; un link name sin body falla el load (`:909-913`).

---

## 5. Propuesta: el tercer tipo de función nativa (método abstracto con implementación)

### 5.1 Idea

Que un miembro pueda declararse **`abstract` + `native`**: el *contrato* (signatura, vtable) lo declara Surtr; el *cuerpo* no es un puntero ni un delegado, sino un **método abstracto de una clase host**, y la implementación la aporta una **instancia de una clase C#** que el host proporciona. El dispatch resuelve: instancia host → su implementación del método abstracto.

### 5.2 Por qué es viable (piezas que ya existen)

1. **El receptor ya viaja como argumento 0** (`SurtrVirtualMachine.cs:2759`). Un cuerpo nativo ya puede hacer `args.Get<SurtrNativeProxy>(0)` para llegar a la instancia host.
2. **La vtable ya soporta sobreescritura de slots** (`PlaceInVTable`, `:519`) y propagación a interfaces (`:572-573`). Un slot "abstracto+nativo" se comporta como cualquier slot virtual.
3. **`SurtrNativeProxy`/`SurtrNativeObject` ya es la pieza que porta la instancia host** (`SurtrNativeObject.cs`, `SurtrRuntime.cs:488-500`).
4. **El runtime ya tolera `Dispatch.Abstract` + `ImplKind.Native`** (ver §3).
5. El coste de dispatch es un cast + una llamada virtual .NET dentro del cuerpo nativo — trivial comparado con el indirect call que ya existe.

### 5.3 Tres variantes de diseño

#### Variante 1: Thunk estático por firma + interfaz .NET

El host declara una interfaz .NET:

```csharp
public interface IHostFacade {
    SurtrValue SetPosition(SurtrCallArguments args);
}

public sealed class HostFacade : IHostFacade { ... }   // la implementación "contiene el código"
```

El puente es un cuerpo nativo estático generado (uno por contrato):

```csharp
static SurtrValue Dispatch(SurtrCallArguments args) {
    var self = args.Get<SurtrNativeProxy>(0);
    return ((IHostFacade)self.Target).SetPosition(args);
}
```

El runtime registra el thunk como body del miembro abstracto+nativo; cuando Surtr llama al método abstracto, el vtable resuelve al slot nativo y el thunk despacha a la instancia.

- **Ventajas:** AOT-seguro (sin reflexión); cero metadatos nuevos en el runtime; reutiliza `DefineNativeBody`/`BindNativeBodies` tal cual; la interfaz .NET da tipado en compile-time.
- **Inconvenientes:** el host tiene que mantener la interfaz en sincronía con el contrato Surtr; una firma = una interfaz; el patrón sigue siendo manual (alguien tiene que escribir el thunk y la interfaz) — este es el hueco que el source generator (documento `Interop-Atributos-SourceGenerators.md`) rellenaría.

#### Variante 2: Clase base abstracta host que implementa el contrato Surtr

El host declara una clase base .NET cuyos métodos abstractos **son** el contrato Surtr:

```csharp
public abstract class SurtrFacade {
    public abstract SurtrValue SetPosition(SurtrCallArguments args);
}
```

La instancia `SurtrNativeProxy` se crea con el objeto host; el miembro abstracto+nativo de Surtr se enlaza a un único thunk genérico que hace `((SurtrFacade)self.Target).SetPosition(args)` **sin reescribirlo por contrato** — un solo thunk reutilizable si el runtime puede resolver el método virtual del objeto host por nombre de contrato.

- **Ventajas:** un solo thunk para todos los miembros; la clase base es la "implementación" que el usuario pedía; el puente es trivial de generar.
- **Inconvenientes:** acopla la jerarquía Surtr a la jerarquía .NET (una clase Surtr hereda de otra Surtr; la base host tiene que reflejar la jerarquía); los constructores y estáticos nativos no encajan; la resolución por nombre de contrato puede requerir reflexión (mitigable con thunk por firma como en V1).

#### Variante 3: Miembro abstracto+nativo sin cuerpo, resuelto por la instancia en el momento del alta

El registro no enlaza un cuerpo estático; en su lugar el runtime guarda una **referencia a la instancia implementadora** y despacha llamando a su método virtual .NET directamente (con un puntero de función capturado por instancia). Esto es esencialmente V1 con el thunk materializado por el propio objeto.

- **Ventajas:** el despacho es una llamada virtual .NET directa, no hay thunk intermedio; encaja con "el método abstracto contiene el código y se crean implementaciones".
- **Inconvenientes:** requiere metadatos nuevos en `SurtrNativeMethodInfo` (una entrada de "instancia implementadora" + resolución del método); el keep-alive de la instancia pasa a ser responsabilidad del runtime (rootearla o que `SurtrNativeProxy.Target` la retenga — ya lo hace); rompe la serialización a imagen (una instancia no viaja en un `.surtrc`), por lo que debería ser solo en proceso.

### 5.4 Recomendación

**V1 como diseño base** (thunk estático por firma + interfaz .NET), con el thunk y la interfaz generados automáticamente por el source generator. Es el que menor superficie toca del runtime (reutiliza `DefineNativeBody`, `BindNativeBodies` y la vtable existente), es AOT-seguro y mantiene el modelo de "el método abstracto declara el contrato, la implementación aporta el código". V3 es el que más se acerca literalmente a la frase del usuario ("un método abstracto, el que contiene el código, y crear implementaciones") pero exige metadatos nuevos y no es serializable; puede añadirse después como refinamiento de V1.

**Cambios necesarios en el runtime para V1:**
1. Permitir que el compilador emita `SurtrNativeMethodInfo` con `Dispatch = Abstract` (hoy `SurtrAbstractMethodInfo` fija `ImplKind.Abstract`; habría que una variante nativa — un flag `isNativeAbstract` o un constructor de `SurtrNativeMethodInfo` para este caso).
2. El slot abstracto+nativo se coloca en la vtable normalmente; `VerifyConcrete` debe aceptar `ImplKind.Native` en un slot abstracto (ya lo hace).
3. El host publica el body (el thunk) por link name como en cualquier native. **No hace falta ningún cambio en el intérprete.**

**Cambios necesarios para V3 (opcional):**
4. `SurtrNativeMethodInfo` guarda opcionalmente un `SurtrNativeProxy`/instancia implementadora + el método resuelto; `InvokeResolved` despacha por instancia. Keep-alive vía `SurtrNativeObject.Target`.

### 5.5 Nota sobre los structs (value types)

Los value types Surtr (`value class`, `SurtrValueTypeCode.cs`) son instancias registradas como cualquier entidad, así que el tercer tipo funciona igual para ellos: el receptor viaja como argumento 0 y el thunk llega a la instancia. No hay distinción en el camino de despacho.

---

## 6. Conclusión

El tercer tipo de función nativa es **viable y encaja sin tocar el intérprete**: el receptor ya es el argumento 0, la vtable ya soporta sobreescritura de slots, `SurtrNativeProxy` ya porta la instancia host, y el runtime ya tolera `Dispatch.Abstract + ImplKind.Native`. La pieza que falta es (a) que el compilador produzca la combinación "abstracto + nativo" y (b) el thunk de despacho host-side. La forma natural de materializarlo es la **Variante 1** con el thunk generado automáticamente, que es exactamente el trabajo que el sistema de atributos + source generators del documento siguiente resolvería de forma declarativa.