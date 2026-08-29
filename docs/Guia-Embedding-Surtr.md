# Guía: embeber Surtr como host (runtime + compilador embebidos)

Esta guía cubre el lado del host: construir un `SurtrRuntime`, configurar su sandbox, compilar
scripts en memoria (con o sin incrementalidad), cargar la stdlib por partes, controlar cómo se
resuelven los `import`, invocar y manejar errores. Es la hermana de
`docs/Guia-Interop-Surtr-Csharp.md` (que cubre exponer tipos C# a Surtr) — esta va del lado
contrario: qué necesita un host para alojar el lenguaje.

---

## 1. Construir un runtime

```csharp
using var runtime = new SurtrRuntime();               // capacidad de heap por defecto (1024 entidades)
using var runtime2 = new SurtrRuntime(initialEntityCapacity: 256);
```

`SurtrRuntime` es el equivalente de un `lua_State`: un heap, las clases nativas del host, los
módulos cargados. Varios runtimes pueden coexistir en el mismo proceso con heaps completamente
separados — es la unidad de aislamiento (ver §9).

### 1.1 Tamaño de pila (sandbox de memoria/profundidad)

```csharp
using var runtime = new SurtrRuntime();
runtime.DataStackSlots = 4096;   // por defecto SurtrRuntime.DefaultDataStackSlots (64K slots)
runtime.MaxCallDepth = 64;       // por defecto SurtrRuntime.DefaultMaxCallDepth (1024)
```

La pila de datos y la de llamadas son de tamaño fijo y **nunca crecen** — un desbordamiento lanza
`SurtrExecutionException` (mapeado a `StackOverflowException` del lenguaje), nunca revienta el
proceso. Por eso mismo, **hay que fijarlas antes de la primera ejecución**: la máquina que las
posee se construye de forma perezosa en el primer `Invoke`/`InvokeClosure`, y cambiar cualquiera de
las dos propiedades después de eso lanza `InvalidOperationException`.

Un host que evalúa scripts de terceros con presupuestos de confianza distintos debería usar pilas
más pequeñas para el código menos confiable — cuanto más pequeña la pila, antes traps un bucle o
una recursión descontrolada, con menos memoria comprometida por intento.

### 1.2 Política de recolección y techo de heap

```csharp
runtime.ConfigureGc(new SurtrGcPolicy(
    mode: SurtrGcMode.Automatic,
    allocationThreshold: 10_000,
    liveEntityThresholdPercent: 75,
    nurseryFrequency: 1,
    maxLiveEntities: 100_000));   // 0 = sin límite (por defecto)
```

`MaxLiveEntities` es un techo duro, no un disparador de recolección: a diferencia de
`AllocationThreshold`/`LiveEntityThresholdPercent`, **no se anula en modo `Manual`** — un host que
hace sandboxing quiere el límite activo se colecte o no por su cuenta. Al alcanzarlo, una
asignación adicional lanza `SurtrHeapLimitExceededException` en vez de seguir creciendo.

Importante: se comprueba **cuando el almacenamiento interno del registro necesitaría crecer**, el
mismo camino frío en el que ya se dobla la capacidad — así que no cuesta nada en el camino
caliente, pero también significa que es un **techo de crecimiento**, no un contador instantáneo de
entidades vivas: un runtime cuya capacidad inicial ya está en el límite (o por encima) no lo nota
hasta la siguiente vez que necesite crecer. Si se quiere el límite activo desde la primera
asignación, hay que dimensionar `initialEntityCapacity` en consecuencia.

### 1.3 El presupuesto de instrucciones como mecanismo de "deadline"

```csharp
runtime.InstructionBudget = 1_000_000;   // 0 = sin límite (por defecto)
```

Surtr **no tiene timeout de reloj real**, y es deliberado: `StepBudget` se comprueba y decrementa
una vez por instrucción, dentro del bucle de despacho más caliente y más medido del proyecto
(`docs/Informe-Volatilidad-Run.md`), y cualquier comprobación añadida ahí — aunque sea "cada N
instrucciones" — exige el protocolo A/B completo de `scripts/ab-suite.ps1` antes de aceptarse. El
presupuesto de instrucciones ya es, por diseño, el proxy determinista de "tiempo": el mismo
programa agota el mismo presupuesto en cualquier máquina, lo que además es justo lo que hace falta
para que el folding de `const fun` en tiempo de compilación no pueda divergir de la ejecución real
(`docs/VM-Plan.md` §4.7).

Para un host que evalúa scripts no confiables, `InstructionBudget` es el control principal —
combinado con `DataStackSlots`/`MaxCallDepth` (§1.1) y `MaxLiveEntities` (§1.2) cubre los tres ejes
(CPU, pila, heap) sin ningún coste en el camino caliente cuando no se usan.

---

## 2. Buscar y construir metadatos: `SurtrMetadataQuery`

```csharp
// Localizar un miembro exacto por firma, no un grupo de overloads:
var get = SurtrMetadataQuery.FindMethod(boxClass, "get", SurtrClassReference.Integer, SurtrClassReference.Integer);

// Nombre/firma legibles (para un inspector, un log, un editor):
SurtrMetadataQuery.DescribeSignature(get!);              // "get(x: int, y: int): int"
SurtrMetadataQuery.FullName(boxClass, get!);              // "game.A:Box:get(int, int)"

// Todo lo que declara un módulo o una clase, en una llamada:
foreach (var member in SurtrMetadataQuery.AllMembers(module, includeSynthetic: false)) { ... }
foreach (var type in SurtrMetadataQuery.AllTypes(module, recursive: true)) { ... }

// Resolver un descriptor a metadatos (equivalente público de lo que usa Type.get desde Surtr):
if (runtime.TryResolveType(SurtrClassReference.Object("game.A:Box"), out var resolved)) { ... }

// Enumerar clases nativas del host, y submódulos por prefijo:
foreach (var native in runtime.NativeClasses) { ... }
foreach (var sub in runtime.GetSubmodules("game")) { ... }

// Construir metadatos a mano sin conocer dos APIs por separado:
var parameter = SurtrMetadataQuery.Parameter(runtime, "x", "I");   // descriptor -> SurtrParameterInfo
```

`SurtrMetadataQuery` cierra los huecos que `SurtrModule`/`SurtrClass`/`SurtrInterface` dejan
abiertos: solo ofrecen `TryGetMethods(nombre)` (grupo de overloads, no firma exacta) y ninguna vista
"todo lo que declara esto" de una sola vez. Nada aquí está en el camino de ejecución — es
introspección para el host, no algo que el intérprete use.

---

## 3. Cargar módulos desde memoria

```csharp
var image = SurtrModuleImage.FromBytes(bytes);   // o FromStream(stream)
var module = runtime.LoadModule(image);          // Instantiate() + LoadModule() en un paso
```

`SurtrModuleImage` no toca el sistema de ficheros — trabaja en bytes/streams. Es lo que permite que
un mismo módulo compilado se cargue en varios runtimes (`image.Instantiate()` produce un
`SurtrModule` fresco cada vez), y lo que hace que "cargar desde memoria" no sea un caso especial:
es el único camino que hay.

---

## 4. Compilar el lenguaje embebido

### 4.1 API en memoria (la vía primaria)

```csharp
var project = new SurtrProject(sourceRoot: "src", rootModulePath: "game");
project.AddSourceFile("player.surtr", textoLeidoDeCualquierSitio);   // texto, no un fichero real
project.AddReference(imagenYaCompilada);                             // SurtrModuleImage
project.Define("Debug", BuildConstant.Bool(true));

using var compilation = SurtrCompilation.Create(project);
var binder = compilation.Bind();
binder.BindBodies();
var images = new ModuleEmitter(compilation, binder).EmitImages();
```

`AddSourceFile` toma texto, no una ruta que tenga que existir — el compilador no depende del
filesystem en absoluto.

### 4.2 Configuración de proyecto: `.surtrproj` y `SurtrProjectFile.Parse`

```
root      = src
module    = game
output    = build
warningsAsErrors = true
suppress  ProjectFileInvalid, 2001
define    Debug = true
reference ../engine/engine.surtrc
```

- `warningsAsErrors`: cualquier warning cuenta como error a efectos de `SurtrBuild.Failed`.
- `suppress`: lista de `SurtrDiagnosticCode` a silenciar por completo, por nombre (`ProjectFileInvalid`) o por número (`2001`).

Un host sin fichero real en disco (config guardada en un `ScriptableObject` de Unity, en una base de
datos) usa `SurtrProjectFile.Parse(texto, directorioVirtual, diagnostics)` en vez de `Read` — misma
sintaxis, sin tocar el filesystem. `Read` es hoy un envoltorio fino sobre `Parse`.

### 4.3 Compilación incremental: `SurtrIncrementalBuild`

Pensado explícitamente para scripting embebido estilo Neverwinter Nights: recompilar un único
script sin rehacer el proyecto/módulo completo.

```csharp
var cache = new InMemoryIncrementalBuildCache();   // vive mientras el proceso lo mantenga vivo

var images = SurtrIncrementalBuild.Run(
    sources: new[] { ("npc.Greeter", textoDelScript) },
    cache: cache);

// Más tarde, tras editar solo "npc.Greeter":
var recompiled = SurtrIncrementalBuild.Run(
    sources: new[] { ("npc.Greeter", nuevoTexto) },
    cache: cache);   // solo recompila npc.Greeter (y quien dependa de él)
```

Regla de invalidación: un módulo es sucio si su hash de contenido cambió, **o** si algo de lo que
depende (transitivamente) está sucio — un cambio de firma en una dependencia puede afectar a quien
la llama aunque el texto del llamador no cambie, así que la regla es deliberadamente conservadora.
Los módulos limpios se pasan como `AddReference` de la imagen ya cacheada — nunca se vuelven a
analizar, bindear ni emitir.

**Granularidad: módulo = fichero.** En Surtr un módulo ya es exactamente un fichero fuente
(`docs/Language-Syntax.md` §2.1), así que "recompilar un script" y "recompilar su módulo" son la
misma operación — no hace falta (ni existe) una granularidad más fina dentro de un mismo fichero.

`SurtrBuild.RunIncremental(projectFilePath, cache)` es el mismo mecanismo integrado en el flujo de
`.surtrproj`, para quien prefiere seguir escribiendo `.surtrc` a disco pero quiere que una
recompilación repetida sea barata.

**Verificación de corrección:** una caché fría produce exactamente las mismas imágenes, byte a
byte, que un build no incremental sobre las mismas fuentes — la incrementalidad es una optimización
de coste, nunca una segunda semántica.

### 4.4 Stdlib por partes

```csharp
SurtrStdlib.LoadAll(runtime);                                          // todo
SurtrStdlib.LoadAll(runtime, StdlibModules.Math | StdlibModules.Core); // por categoría
SurtrStdlib.LoadInto(runtime, images, path => path == "surtr.math.Angle"); // un módulo exacto
```

`StdlibModules` es un flag-enum por categoría (`Core`, `Math`, `Collections`, `Text`, `Io`,
`Diagnostics`). El overload por predicado da control total sobre módulos individuales — útil cuando
se quiere, por ejemplo, solo `Stack` sin el resto de `Collections`, con la salvedad de que un
módulo que depende de otro (`Stack` de `Collection`) necesita que ambos estén seleccionados: la
carga reintenta en punto fijo, pero no resuelve una dependencia que nunca se pidió.

### 4.5 Control de la resolución de módulos en `import`

```csharp
var project = new SurtrProject(
    sourceRoot: ".",
    rootModulePath: "",
    sourceProvider: new CompositeSourceProvider(
        new DictionarySourceProvider(scriptsEnMemoria),   // manifiesto propio primero
        new FileSystemSourceProvider("assets/scripts")));  // filesystem como fallback
```

`ISourceProvider` es la costura que decide de dónde sale el texto de un módulo cuando un `import`
lo nombra sin que ya se le haya dado al proyecto — clave por *ruta de módulo* (`game.core.Entity`),
no por ruta de fichero. `CompositeSourceProvider` encadena varios (el primero que conoce el módulo
gana); `DictionarySourceProvider` es un mapa en memoria puro, para un host cuyos scripts ya son
datos (una tabla `nombre → texto`).

**Perezoso vs. de golpe, ambos ya soportados, sin flag que elegir:**
- **De golpe** (mundo cerrado): `AddSourceFile` de todo el árbol por adelantado — lo que hace
  `SurtrBuild`. Todo se resuelve contra lo ya dado; nombrar algo no presente es un error inmediato.
- **Perezoso** (mundo abierto): dar pocos o ningún `AddSourceFile` y un `ISourceProvider` propio —
  `SurtrCompilation.Create` resuelve cada `import` bajo demanda, en punto fijo, hasta que una pasada
  no añade nada nuevo.

---

## 5. Invocar, manejar errores, liberar

```csharp
try
{
    var result = runtime.Invoke(method, arguments);
}
catch (SurtrExecutionException)
{
    runtime.ResetExecution();   // limpia el intérprete a mitad de frame antes de volver a usarlo
    throw;
}
```

Una excepción que escapa de `Invoke` deja el intérprete a mitad de frame — `ResetExecution()` es
obligatorio antes de volver a tocar el runtime si se pretende seguir usándolo. `Dispose()` libera el
heap, los módulos cargados y todo buffer no gestionado; el finalizador es solo un respaldo.

---

## 6. Aislamiento multi-tenant: un runtime por dominio de confianza

Un `SurtrRuntime` es la unidad de aislamiento — heap y tabla de cuerpos nativos (`DefineNativeBody`)
completamente separados de cualquier otro runtime del proceso. Para scripts de distinta
procedencia o nivel de confianza, la recomendación es **un runtime por dominio de confianza**, no
intentar particionar cuerpos nativos por módulo dentro de un runtime compartido: `DefineNativeBody`
publica por *link name* único dentro de un runtime, y esa unicidad es justo lo que sostiene hoy el
formato de imagen y el mecanismo de carga. Tocarla para algo que "usar otro runtime" ya resuelve, y
resuelve barato, no compensa el riesgo.

---

## 7. Ver también

- `docs/Guia-Interop-Surtr-Csharp.md` — exponer tipos C# a Surtr (la dirección contraria).
- `docs/Module-Format.md` — el layout `.surtrc` y qué se enlaza al cargar en vez de escribirse.
- `docs/VM-Plan.md` §1.9 — política de validación/traps del intérprete, relevante para §1 y §5.
- `docs/Compiler-Plan.md` — el modelo de compilación completo detrás de §4.
