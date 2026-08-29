# Plan: paquete ejecutable de Surtr (`.surtrx`)

Estado objetivo: `surtrc build` produce **un solo archivo** que incluye el código del
usuario y la stdlib; `surtr <archivo>` lo ejecuta. Hoy el pipeline compila (`surtrc`) y
ejecuta (`surtr`), pero:

- la salida son **N archivos `.surtrc`** (uno por módulo), sin contenedor;
- `surtr run` carga en un `SurtrRuntime` desnudo y **no registra la stdlib**, así que
  cualquier programa que use `Math` (nativo) falla al cargar;
- Surtr **no tiene `main`**, hay que pasar módulo+función a mano.

Este plan cubre los tres huecos: (1) formato contenedor, (2) `surtr` registra la stdlib,
(3) convención de punto de entrada.

---

## 0. Hechos verificados en el código (base del diseño)

- `surtrc` = `Surtr.Cli`, ensamblado `surtrc`, solo comando `build`
  (`src/Surtr.Cli/Program.cs:35`). Escribe `<modulePath>.surtrc` por módulo
  (`src/Surtr.Compiler/Compilation/SurtrBuild.cs:297-310`).
- `surtr` = `Surtr.Run`, ensamblado `surtr`, comandos `run`/`list`
  (`src/Surtr.Run/Program.cs:41-42`). Crea `new SurtrRuntime()` y referencia **solo
  `Surtr.Core`** (`src/Surtr.Run/Surtr.Run.csproj:19-21`); no referencia `Surtr.Stdlib`.
- El formato `.surtrc` es **estrictamente de un módulo** (`SurtrModuleImage.cs:55-207`,
  magic `SURTRMOD`, `FormatVersion = 16`). No existe contenedor de varios módulos.
- Los miembros `native` viajan como **link name** (`Module-Format.md:385-419`). La stdlib
  publica sus cuerpos con `SurtrStdlib.RegisterNativeBodies(runtime)` y los carga con
  `SurtrStdlib.LoadAll/LoadInto` (`src/Surtr.Stdlib/SurtrStdlib.cs:137,187,369`). Los
  cuerpos son punteros C# en `Surtr.Stdlib.dll` y **no pueden serializarse en un archivo**:
  el host que carga siempre debe registrarlos.
- `DefineNativeBody` **reemplaza** (idempotente): registrar dos veces es inocuo
  (`SurtrRuntime.cs:1719-1733`).
- `LoadModule` **rechaza** una segunda carga de la misma ruta de módulo
  (`SurtrRuntime.cs:945`). Por tanto no se debe cargar la stdlib desde el DLL y desde el
  paquete a la vez.
- El compilador expone las referencias ya resueltas: `SurtrProject.ReferencedImages`
  (`SurtrProject.cs:174`). `surtrc build` puede recolectarlas para empaquetarlas.
- Carga con dependencias: bucle de punto fijo en `ModuleSet.Load`
  (`src/Surtr.Run/ModuleSet.cs:80-131`) y en `SurtrStdlib.LoadInto`
  (`SurtrStdlib.cs:197-226`). Se reutilizará.

---

## 1. Formato contenedor `.surtrx` (nuevo, en `Surtr.Core`)

Nuevo tipo en `src/Surtr.Core/Bytecode/Image/` (junto a `SurtrModuleImage`):

- `SurtrPackage` — envoltura: `Modules` (`IReadOnlyList<SurtrModuleImage>`),
  `EntryModulePath`, `EntryFunction`.
- `SurtrPackageWriter` / `SurtrPackageReader` — serialización estricta y con versión
  propia (independiente de `SurtrModuleImage.FormatVersion = 16`).

### 1.1 Layout binario

| Campo | Tipo | Notas |
|---|---|---|
| `magic` | `u64` | `0x5355525452504B47` = `SURTRPKG` (ASCII, little-endian) |
| `formatVersion` | `u16` | versión del contenedor, arranca en **1**; un lector rechaza lo desconocido |
| `entryModule` | `str` | i32 byteLen + UTF-8; ruta del módulo de entrada (p.ej. `game`) |
| `entryFunction` | `str` | i32 byteLen + UTF-8; nombre de la función de entrada (p.ej. `main`) |
| `moduleCount` | `i32` | cantidad de módulos embebidos |
| por cada módulo | | |
| `modulePath` | `str` | i32 byteLen + UTF-8; ruta (== `image.Path`), para diagnóstico |
| `length` | `i32` | bytes del image |
| `bytes` | `u8[length]` | un `.surtrc` completo (`SURTRMOD`…), leído con `SurtrModuleImage.FromBytes` |

- Cadenas **inline** (longitud + UTF-8), no tabla compartida: el contenedor es pequeño y
  el lector es una pasada secuencial estricta, igual que el de módulo.
- **No** se re-genera ni se valida el contenido de cada módulo; se delega en
  `SurtrModuleImage.FromBytes`, que ya valida magic/versión propios.
- El registro de versión del contenedor tiene su propia regla: cualquier cambio de layout
  bumpa `formatVersion`; el lector rechaza versiones desconocidas (sin compatibilidad
  hacia adelante), coherente con `docs/Module-Format.md:440-456`.

### 1.2 API

```csharp
public sealed class SurtrPackage
{
    public const string FileExtension = ".surtrx";
    internal const ulong Magic = 0x5355525452504B47;
    internal const ushort FormatVersion = 1;

    public IReadOnlyList<SurtrModuleImage> Modules { get; }
    public string EntryModulePath { get; }
    public string EntryFunction { get; }

    public static SurtrPackage Create(
        IReadOnlyList<SurtrModuleImage> modules,
        string entryModulePath, string entryFunction);

    public static SurtrPackage FromBytes(byte[] bytes);   // valida magic+versión
    public static SurtrPackage FromStream(Stream stream);
    public byte[] ToBytes();
    public void WriteTo(Stream stream);
}
```

### 1.3 Carga de un paquete en un runtime

Se factoriza el bucle de punto fijo a `Surtr.Core` para reutilizarlo desde `Surtr.Run`:

- `SurtrRuntime.LoadModules(IReadOnlyList<SurtrModuleImage> images)` — carga módulos con
  reintentos (misma lógica que `ModuleSet.Load`/`SurtrStdlib.LoadInto`), **omitendo los
  paths ya cargados** para no duplicar con la stdlib. `ModuleSet.Load` pasa a llamarlo.
- Responsabilidad de la stdlib: **quien ejecuta (`surtr`) es el que la carga y la añade
  junto con el código a ejecutar** (decisión del usuario). Por tanto `surtr` siempre hace
  `SurtrStdlib.LoadAll(runtime)` (registra cuerpos nativos + carga los módulos de la stdlib
  desde `Surtr.Stdlib.dll`) y luego carga los módulos del `.surtrx` (o sueltos).

> Nota: los **cuerpos nativos** de la stdlib (`Math.*`) son punteros C# que solo existen en
> el proceso que enlaza `Surtr.Stdlib.dll` y no se pueden serializar en un archivo. Por eso
> el ejecutor, y no el paquete, es quien los aporta. El `.surtrx` contiene el código del
> proyecto; la stdlib (módulos + nativos) la provee `surtr`. Esto es análogo a como `java`
> provee `java.lang.*` o `dotnet` la BCL. El paquete puede, opcionalmente, embeber también
> módulos de stdlib; `surtr` los omitirá por path ya cargado (idempotente).

---

## 2. `surtr` carga la stdlib (Punto 2) — DECIDIDO: `surtr` la aporta

- `src/Surtr.Run/Surtr.Run.csproj`: añadir
  `<ProjectReference Include="..\Surtr.Stdlib\Surtr.Stdlib.csproj" />`.
- En el helper de carga (cortafuegos común para `.surtrx` y sueltos), **siempre primero**:
  `SurtrStdlib.LoadAll(runtime);` — registra los cuerpos nativos (`Math.*`) y carga los
  módulos de la stdlib desde `Surtr.Stdlib.dll`. Cubre tanto paquetes como archivos sueltos
  que usen stdlib (auto-stdlib).
- Luego se cargan los módulos objetivo (paquete o sueltos) con
  `runtime.LoadModules(images)`, que **omite los paths ya cargados** (la stdlib ya está),
  evitando el rechazo por ruta duplicada de `LoadModule` (`SurtrRuntime.cs:945`).
- Alcance de native bodies: **solo stdlib** (decisión del usuario). No se construye un
  mecanismo general de registro de native bodies de hosts en esta fase.

---

## 3. Convención de punto de entrada (Punto 3)

Surtr no tiene `main` (`Language-Syntax.md` §2.5). El paquete almacena
`entryModule` + `entryFunction`. Fuente de ese valor en el build (decisión abierta):

- **(A) Directiva explícita** en `.surtrproj`: `entry = <module.path> <function>`
  (p.ej. `entry = game main`). El compilador la lee (`SurtrProjectFile`) y la pasa a
  `SurtrBuild` → `SurtrPackage.Create`.
- **(B) Auto-detección**: si existe exactamente una función de módulo llamada `main`,
  esa es la entrada; error si hay ambigüedad (varias) o ninguna.
- **(C) Ambas**: la directiva explícita gana; si no, se auto-detecta `main`; si no, error.

En runtime, `surtr` resuelve la sobrecarga con `EntryPoint.Resolve(module, entryFunction,
argCount)` y bindea argumentos con `EntryPoint.Bind` (`src/Surtr.Run/EntryPoint.cs:48,89`),
igual que hoy. Mantener `surtr run <path> <module> <function>` para anular/usar sueltos.

---

## 4. Cambios en `surtrc build` (empaquetado)

- Añadir modo paquete a `Surtr.Cli/Program.cs`:
  - flag `--package <nombre>.surtrx`, y/o directiva `package = true` en `.surtrproj`.
  - Recolectar módulos: `userImages` (salida del emitter) **+**
    `project.ReferencedImages` (stdlib y otras referencias) para que el paquete sea
    autocontenido.
- En `SurtrBuild` añadir:
  - `Run(..., packagePath, entryModule, entryFunction)` (o parámetros opcionales) que
    llame a `Compile` y luego a `WritePackage`.
  - `WritePackage(images, references, entry, packagePath)` →
    `SurtrPackage.Create(...).WriteTo(...)`.
- Compatibilidad: `surtrc build` sigue pudiendo escribir `.surtrc` sueltos; el paquete es
  una salida adicional (o exclusiva según flag/directiva). Decisión abierta.

---

## 5. Cambios en `surtr run`

- `surtr run <path>` donde `<path>` termina en `.surtrx` → carga paquete, ejecuta
  `EntryModulePath`/`EntryFunction` con los argumentos restantes.
- (Opcional) forma corta `surtr <pkg>.surtrx` sin subcomando.
- `surtr list <path>` también lista el contenido de un paquete (módulos + entry point).
- Modo suelos/directorio: sin cambios de comportamiento obligatorio; ver pregunta 5
  sobre auto-carga de stdlib faltante.

---

## 6. Impacto y orden de implementación

Antes de editar, ejecutar `gitnexus_impact` sobre los símbolos a tocar
(`SurtrRuntime.LoadModule`, `ModuleSet.Load`, `SurtrBuild.WriteImages`,
`SurtrProjectFile`, `Program` de Cli y Run). Riesgo esperado: medio (tocamos la API de
carga de `Surtr.Core` y dos hosts). Pasos:

1. `Surtr.Core`: `SurtrPackage`, `SurtrPackageWriter/Reader`, `SurtrRuntime.LoadModules`.
2. `Surtr.Stdlib`: sin cambios de API (ya expone `RegisterNativeBodies`,
   `EmbeddedImages`, `LoadInto`).
3. `Surtr.Compiler`: `SurtrBuild.WritePackage` + entrada de entry point / paquete en
   `SurtrProjectFile`.
4. `Surtr.Cli`: flag `--package` / directiva `package`.
5. `Surtr.Run`: referencia a `Surtr.Stdlib`, registro de cuerpos nativos, carga de
   `.surtrx`, forma corta opcional, auto-stdlib opcional.
6. `docs/Module-Format.md`: añadir apéndice del contenedor `.surtrx`.
7. Tests en `Surtr.Tests` (empaquetar+ejecutar un programa con stdlib).

---

## 7. Preguntas abiertas — RESUELTAS

1. Nombre del formato: **`.surtrx`**.
2. Stdlib: **`surtr` (el ejecutor) es quien carga la stdlib y la añade junto con el código
   a ejecutar** (desde `Surtr.Stdlib.dll`); el paquete no necesita embeberla.
3. Entry point: **ambas** — directiva `entry =` en `.surtrproj` gana; si no, auto-detectar
   una función `main`; si no, error.
4. `surtrc build`: por defecto siguen saliendo `.surtrc` sueltos; el `.surtrx` se genera
   solo con `--package` (o directiva `package = true`).
5. `surtr`: **forma corta `surtr <pkg>`** además de `surtr run`; y **auto-stdlib** en modo
   suelto/directorio (ya cubierto porque siempre se hace `LoadAll`).
6. Native bodies: **solo stdlib**.

---

## 8. Discrepancia resuelta: "empaquetarlo todo" vs "surtr carga la stdlib"

El usuario pidió originalmente "empaquetarlo todo en un archivo (incluida la stdlib)". Con
la decisión 2, la stdlib la aporta el ejecutor. Esto es coherente porque:

- Los **cuerpos nativos** de la stdlib (`Math.*`) son punteros C# y **no pueden** viajar en
  un archivo; el proceso que ejecuta debe registrarlos. Así que `surtr` los aporta sí o sí.
- El `.surtrx` lleva **todo el código Surtr del proyecto** en un archivo. La stdlib se comporta
  como la biblioteca del sistema que el runtime provee (como `java.lang` / BCL), no como
  parte empaquetada del programa. Esto es lo que el usuario confirmó en la decisión 2.
- Si se quiere máxima autocontención, el paquete **puede** embeber también los módulos de
  stdlib; `surtr` los omitirá por path ya cargado. El build lo decide; por defecto basta
  con que `surtr` la cargue.
