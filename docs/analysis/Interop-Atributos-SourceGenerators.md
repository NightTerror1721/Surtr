# Propuesta de diseño: sistema de atributos + source generators para la conexión automática host ↔ Surtr

> **Fecha:** 2026-08-22
> **Estado:** propuesta de arquitectura. Se piden **dos diseños** con sus ventajas e inconvenientes.
> **Contexto:** hoy la conexión de clases/structs del host (C#/Unity) con Surtr es 100 % manual y procedural: el host construye `SurtrClass`, `SurtrNativeMethodInfo`, `SurtrParameterInfo`, escribe bodies `SurtrValue(SurtrCallArguments)` a mano, los publica por link name y envuelve objetos en `SurtrNativeProxy`. No hay conversión automática CLR↔Surtr, ni registro por barrido, ni atributos declarativos del lado C# (detalles en `Runtime-Analisis-Rendimiento-Memoria.md` y `Puente-Nativo-Tercer-Tipo.md`).

---

## 0. Objetivo y restricciones

- **Atributos** (definidos en un proyecto "core/bridge", visible para el host) para marcar: clases, structs, campos, properties, métodos, constructores, eventos, enums, interfaces.
- **Source generator** en un **proyecto aparte** que lee los atributos y genera el puente (registro de tipos, bodies nativos, wrappers, conversiones CLR↔Surtr).
- **Un registro de tipos conectados** (catálogo en runtime que acumula lo que el generador va declarando) para poder resolver, crear instancias e invocar desde Surtr y desde el host.
- Compatible con el modelo existente: `SurtrNativeEntryPoint` (puntero a función / delegado), `DefineNativeClass`, `DefineNativeBody`, `WrapNative`, la vtable y — opcionalmente — el **tercer tipo de función nativa** (documento `Puente-Nativo-Tercer-Tipo.md`).
- Debe encajar con **Unity/IL2CPP**: nada de reflexión en runtime por defecto (los atributos se consumen en compile-time por el generador; el runtime usa direcciones de métodos, no `GetMethod`).

---

## 1. Piezas comunes a ambos diseños

### 1.1 El proyecto de atributos (nuevo: p. ej. `Surtr.Interop`)

Ensamblado `netstandard2.1` (o 2.0 para Unity) con solo tipos de metadatos. Sin dependencia de `Surtr.Core` para que el host lo pueda referenciar sin arrastrar el VM (o con dependencia opcional).

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class SurtrTypeAttribute : Attribute
{
    public string? Name;            // nombre Surtr (por defecto: tipo CLR)
    public string Module;           // módulo/namespace Surtr
    public bool ValueType;          // struct => value class
    public Type? BaseType;          // base Surtr
    public string? Descriptor;      // override manual del descriptor
}

[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class SurtrFieldAttribute : Attribute { public string? Name; public bool Expose; }

[AttributeUsage(AttributeTargets.Method)]
public sealed class SurtrMethodAttribute : Attribute
{
    public string? Name;
    public bool IsConstructor;
    public bool IsStatic;
    public bool IsVirtual;          // virtual/abstract => slot de vtable
    public bool IsAbstract;         // tercer tipo: cuerpo host vía instancia
    public bool Inline;
}

[AttributeUsage(AttributeTargets.Constructor)]
public sealed class SurtrConstructorAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class SurtrParameterAttribute : Attribute { public string? Name; public bool Varargs; }

[AttributeUsage(AttributeTargets.Enum)]
public sealed class SurtrEnumAttribute : Attribute { }
```

El **registro de tipos conectados** (catálogo) vive en el core: una clase `SurtrBridgeRegistry` (estática por contexto o por runtime) con `RegisterBridge(...)`, `TryResolve(Type|string)`, tablas de wrappers/conversores. El generador emite código que llama a este registro en el momento del alta (o el host llama a `SurtrBridgeRegistry.AutoRegister(assembly)`).

### 1.2 Lo que el generador debe producir (sin importar el diseño)

Por cada tipo anotado:

1. **Metadatos Surtr**: `SurtrClass` con campos, properties, métodos, constructores, interfaces — con sus descriptores correctos (`I`, `F`, `S`, `N<name>;`, `A<E>`, `D<K,V>`, `T(...)`, `L(...)` — `SurtrClassReference.cs`).
2. **Bodies nativos**: estáticos `SurtrValue Name(SurtrCallArguments args)` que convierten argumentos, llaman al miembro CLR real y convierten el resultado. Emitidos como direcciones (`&`) o delegados — ver diseños.
3. **Wrappers de instancia**: un `SurtrNativeProxy` (o subclase) por tipo que porta el objeto host; conversores CLR↔Surtr por tipo miembro.
4. **Registro automático**: un método `RegisterBridge(SurtrRuntime runtime)` (o una clase estática `XxxBridge` que el host invoca una vez) que llama a `DefineNativeClass`/`FinishNativeClass`/`DefineNativeBody` y rellena el registro de tipos.
5. **`partial`**: el generador necesita acceder a miembros no públicos → el usuario marca la clase como `partial` y el generador declara los thunks como miembros `internal/private` de la misma clase, o el generador usa un "strong name" al método real y el thunk se emite en una clase separada con `InternalsVisibleTo` cuando hace falta.

### 1.3 Mapa de tipos CLR ↔ Surtr (núcleo de la conversión)

El generador traduce cada tipo CLR a su descriptor Surtr (`SurtrClassReference`):

| CLR | Surtr |
|---|---|
| `int`, `long` (si cabe), `float`, `double`, `bool`, `char`, `string` | `I`, `F`, `B`, `C`, `S` |
| `T[]` | `A<E>` |
| `Dictionary<K,V>` / `SurtrDictionary` | `D<K,V>` |
| `ValueTuple<...>` / `SurtrTuple` | `T(...)` |
| Tipo anotado con `[SurtrType]` | `N<fullName>;` |
| Delegado / `SurtrClosure` | `L(params)V` |
| Enums anotados | primitivo subyacente con caja `SurtrEnum` |

El "bridge" de cada miembro genera el cuerpo que hace: `args.Get<T>(i)` → llamada real → `SurtrValue.CreateReference(...)`/`SurtrValue.FromInt(...)`/etc.

---

## 2. Los dos diseños

---

### Diseño A — «Generación de registro declarativo» (bridge por clase generada)

#### A.1 Arquitectura

Un **source generator incremental** (`IIncrementalGenerator`) procesa cada tipo anotado y genera **una clase `partial` `XxxBridge` por tipo**, más un **registrador agregado**.

```
[SurtrType(Module = "game")]
public partial class Player
{
    [SurtrField] public string Name;
    [SurtrMethod(IsVirtual = true)] public virtual void Move(float dx, float dy);
    [SurtrConstructor] public Player(string name);
}
```

Generado:

```csharp
public partial class Player
{
    // 1) Bodies nativos por miembro (uno por firma), como estáticos:
    private static SurtrValue _bridge_Move(SurtrCallArguments args)
    {
        var self = args.Get<SurtrNativeProxy>(0);
        var that = (Player)self.Target;
        that.Move(args.GetFloat(1), args.GetFloat(2));
        return SurtrValue.Null;
    }

    // 2) Registro del tipo en el runtime:
    internal static void RegisterBridge(SurtrRuntime runtime)
    {
        var cls = runtime.DefineNativeClass("game:Player");
        var selfType = cls.SelfReference;
        var floatT = SurtrClassReference.Float;
        cls.AddMethod(new SurtrNativeMethodInfo(
            "Move", SurtrMethodImplKind.Native, SurtrMethodDispatch.Virtual, SurtrMethodRole.Normal,
            isOverride: false, SurtrClassReference.Void,
            new[] { new SurtrParameterInfo("dx", floatT), new SurtrParameterInfo("dy", floatT) },
            isStatic: false, SurtrVisibility.Public, selfType,
            SurtrNativeEntryPoint.FromFunctionPointer(&_bridge_Move)));
        // ... campos, constructor, getters/setters ...
        runtime.FinishNativeClass(cls);
        SurtrBridgeRegistry.Register("game:Player", cls, BridgeInfo.Create(
            createInstance: static (runtime) => new Player(),
            wrap: static (obj) => runtime.WrapNative(cls, obj)));
    }
}
```

**Registrador agregado** (también generado, uno por ensamblado):

```csharp
public static class GameBridgeRegistration
{
    public static void RegisterAll(SurtrRuntime runtime)
    {
        PlayerBridge.RegisterBridge(runtime);   // una línea por tipo anotado
        EnemyBridge.RegisterBridge(runtime);
        Vec2Bridge.RegisterBridge(runtime);
    }
}
```

El host hace **una sola llamada** al construirse el runtime: `GameBridgeRegistration.RegisterAll(runtime)`. Los bodies se publican como direcciones (`&_bridge_Move`) → **AOT/IL2CPP seguro, sin reflexión**.

El **tercer tipo de función nativa** encaja de forma natural: un miembro `[SurtrMethod(IsAbstract = true)]` se emite como `SurtrNativeMethodInfo` con `Dispatch = Abstract` (variante nativa del documento `Puente-Nativo-Tercer-Tipo.md`); el cuerpo lo aporta la instancia host vía un thunk estático por firma que despacha a un método abstracto de la clase base del host.

#### A.2 Ventajas

1. **Cero reflexión en runtime** — todo se resuelve en compile-time (direcciones `&`). Máximo rendimiento y compatibilidad IL2CPP/AOT.
2. **Sin cambios en el núcleo del runtime salvo el registro de tipos** — reutiliza `DefineNativeClass`, `DefineNativeBody`, `SurtrNativeMethodInfo`, `SurtrNativeProxy`, la vtable. La superficie nueva es solo `SurtrBridgeRegistry` + el flag de tercer tipo.
3. **Incremental y diagnóstico** — `IIncrementalGenerator` con cacheo por sintaxis; errores de anotación se reportan como diagnostics del compilador, antes de ejecutar nada.
4. **Escalable** — un método por tipo, un `RegisterAll` por ensamblado; el host controla exactamente qué ensamblados registra y cuándo.
5. **Acceso a miembros privados** — con `partial`, el generador declara los thunks dentro de la propia clase y accede a todo sin reflexión ni `InternalsVisibleTo`.
6. **Facilita el tercer tipo nativo** — el generador materializa exactamente el thunk de la Variante 1 del documento de puente nativo.

#### A.3 Inconvenientes

1. **El código generado vive en el ensamblado del host** — el host debe ser `partial` y permitir generadores; los thunks se compilan con la app (más tamaño de build, pero nada en runtime).
2. **Un cuerpo nativo por firma** — para N tipos con M miembros, N×M métodos estáticos generados (código muerto si el tipo no se usa).
3. **El catálogo es por ensamblado** — si dos ensamblados anotan el mismo nombre Surtr, colisiona (hay que resolverlo en `SurtrBridgeRegistry` con prioridad o error).
4. **Requiere el flag de tercer tipo en el runtime** para `IsAbstract`, o el miembro abstracto se emite como abstracto normal (perdiendo el cuerpo host).
5. **Menos flexible para tipos genéricos** — un `Box<T>` genérico requiere instanciación del generador por construcción genérica (o solo soportar genéricos no abiertos al principio).

---

### Diseño B — «Metadatos + reflexión de alta» (atributos consumidos en runtime, generador mínimo)

#### B.1 Arquitectura

Los atributos **no** generan los bodies. El generador (mucho más simple, o incluso opcional) solo produce **una tabla de metadatos estática** (un `TypeInfo` por tipo anotado con los nombres de miembros y sus descriptores). El **runtime** hace el resto en el momento del alta: un `SurtrBridgeRegistry.RegisterAssembly(assembly)` usa **reflexión limitada y cacheada** para:

1. Enumerar los tipos con `[SurtrType]`.
2. Para cada uno, construir `SurtrClass` con `DefineNativeClass`, recorriendo campos/properties/métodos anotados y creando `SurtrNativeMethodInfo`.
3. Resolver los bodies por **delegado generado** (un `Expression` compilado o `Delegate.CreateDelegate`) — o, como refinamiento, por un puñado de **thunks genéricos** reutilizables por firma.

```csharp
public static class SurtrBridgeRegistry
{
    public static void RegisterAssembly(SurtrRuntime runtime, Assembly assembly);
    // usa CacheAttributeReader (reflexión cacheada, una vez por tipo)
    // usa SurtrTypeMap.Map(Type, hint) para los descriptores
    // usa DelegateBuilder.Build(type, method) -> SurtrNativeFunction
}
```

Los thunks se construyen con `Delegate.CreateDelegate(typeof(SurtrNativeFunction), ...)` sobre un **método de adaptación** que el generador emitió como miembro estático genérico — el puente de firma es **una sola implementación genérica** en lugar de N×M métodos:

```csharp
static class BridgeThunks<T>
{
    public static SurtrValue CallVoid(SurtrCallArguments args) { ... }
    public static SurtrValue CallValue(SurtrCallArguments args) { ... }
}
```

El generador solo emite, por tipo anotado, un `partial` con un método `static SurtrValue Dispatch_<Name>(SurtrCallArguments args)` que hace el cast concreto `((T)proxy.Target).Method(...)` (tipado fuerte, sin reflexión por llamada). El coste de reflexión queda **solo en el alta** (una vez por tipo), no en las llamadas.

#### B.2 Ventajas

1. **Generador mínimo** — sin generar N×M bodies; la mayor parte del puente vive en el runtime (`SurtrBridgeRegistry`), más fácil de mantener y de probar que código generado.
2. **Registro por barrido** — `RegisterAssembly(assembly)` descubre todo automáticamente; el host no necesita una llamada por tipo, ni mantener `RegisterAll` al día. Menos boilerplate en el host.
3. **Cambios en el host sin recompilar el generador** — los atributos son datos; cambiar la forma de construir el puente se hace en el runtime, no regenerando.
4. **Soporte de genéricos más fácil** — la construcción del puente puede instanciar `T` en runtime para construcciones genéricas cerradas.
5. **Mejor para prototipar** — un script o un editor de Unity pueden cargar tipos anotados en caliente.

#### B.3 Inconvenientes

1. **Reflexión en el alta** — aunque cacheada (una vez por tipo), `RegisterAssembly` es más lento que el registro por direcciones; bajo IL2CPP hay que marcar los tipos/miembros con `[Preserve]`/links para que no se recorten. El coste es solo de setup, no por llamada, pero es el punto débil AOT.
2. **`Delegate.CreateDelegate` sobre métodos estáticos con `SurtrCallArguments`** — si el método adaptador no existe como método real (el generador no lo emitió), hay que compilar un `Expression` (no permitido en IL2CPP) o caer a reflexión lenta. La vía segura exige que el generador emita el `Dispatch_<Name>` de todas formas → el generador ya no es "mínimo".
3. **Menos control del host** — el barrido de `RegisterAssembly` registra *todo* lo anotado; si el host quiere registrar subtipos de forma selectiva, necesita filtros.
4. **Colisiones por barrido** — dos ensamblados con el mismo nombre Surtr colisionan igual que en A, pero detectado más tarde (en runtime, no en compile-time).
5. **`partial` sigue siendo necesario** para acceder a miembros no públicos del tipo anotado, o hay que confiar en `InternalsVisibleTo`/reflexión con `BindingFlags.NonPublic` (más frágil).

---

## 3. Comparación resumida

| Criterio | Diseño A (generación de registro declarativo) | Diseño B (metadatos + reflexión de alta) |
|---|---|---|
| Reflexión en runtime | Ninguna (direcciones `&`) | Solo en el alta, cacheada |
| IL2CPP/AOT | Seguro por defecto | Requiere `[Preserve]` y generador que emita `Dispatch_<Name>` |
| Código generado | N×M bodies + `RegisterAll` | Mínimo (`Dispatch_<Name>` por miembro) |
| Registro | Llamada explícita por ensamblado | Barrido por ensamblado (`RegisterAssembly`) |
| Coste por llamada | Indirecto directo (igual que hoy) | Igual (el `Dispatch_<Name>` es un static) |
| Mantenimiento | El generador es la fuente de verdad | El runtime contiene más lógica |
| Genéricos | Solo cerrados al inicio | Abiertos más fácil (instanciación en runtime) |
| Soporte tercer tipo nativo | Directo (thunk por firma) | Indirecto (mismo thunk, pero el alta es reflexivo) |
| Diagnóstico de errores de anotación | Compile-time (diagnostics del generador) | Runtime (excepciones al registrar) |
| Host boilerplate | `RegisterAll(runtime)` una vez | `RegisterAssembly(runtime, asm)` una vez |

---

## 4. Recomendación

**Diseño A** como base para el runtime de producción (rendimiento y AOT: la prioridad declarada del proyecto), con el proyecto de atributos `Surtr.Interop` y el source generator `Surtr.Interop.Generator` en proyectos separados. **Diseño B** como modo "editor" o "debug" del mismo sistema: el `SurtrBridgeRegistry` puede ofrecer `RegisterAssembly` como *conveniencia de desarrollo* que invoque el mismo catálogo que A (cada tipo registrado por el barrido termina llamando al mismo `RegisterBridge`), de modo que en el editor de Unity se registra por barrido y en build final por `RegisterAll` generado. Esa fusión da lo mejor de ambos: rendimiento AOT en producción y cero mantenimiento del `RegisterAll` durante el desarrollo.

### 4.1 Plan de trabajo propuesto (hitos)

1. **`Surtr.Interop`** (nuevo proyecto): atributos (`SurtrType`, `SurtrField`, `SurtrMethod`, `SurtrConstructor`, `SurtrParameter`, `SurtrEnum`) + `SurtrTypeMap`.
2. **`Surtr.Core`**: `SurtrBridgeRegistry` (catálogo de tipos conectados) + flag del tercer tipo nativo en `SurtrNativeMethodInfo` (o una subclase `SurtrNativeAbstractMethodInfo`).
3. **`Surtr.Interop.Generator`** (nuevo proyecto, `IIncrementalGenerator`): consume los atributos y emite `XxxBridge` + `RegisterAll`. Incluye la generación del thunk para `[SurtrMethod(IsAbstract = true)]`.
4. **Ejemplo de referencia**: portar `Surtr.Stdlib`/un tipo de prueba a la forma declarativa para validar contra los tests existentes (`SurtrStdlibTests` comprueba que todo `native fun` publicado tiene body).
5. **Tests**: un proyecto de pruebas que compile un ensamblado anotado, registre el puente y ejercite llamadas bidireccionales (Surtr→host y host→Surtr) verificando conversiones de tipos y el registro de tipos conectados.