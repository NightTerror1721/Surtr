# Informe y propuesta: El registry como mini-recolector y su automatización con políticas

> **Fecha:** 2026-08-22
> **Alcance:** `SurtrEntityRegistry.cs`, `SurtrContext.cs`, `SurtrRuntime.cs`, el VM, los tests del registry.
> **Objetivo:** identificar cómo integrar las llamadas al recolector (hoy 100 % manuales) y automatizarlas con un sistema de políticas configurable.

---

## 1. Cómo funciona el recolector hoy

El registry es un **mark-sweep generacional por edades** (no por regiones):

- **Tabla de entidades:** `Entities: SurtrRuntimeEntity?[]` gestionado, indexado por `SurtrRef` (`SurtrEntityRegistry.cs:47`). Id 0 = `NullRef` reservado.
- **Free list:** `_freeIds` unmanaged (LIFO); los ids se reaprovechan antes de subir el watermark `_nextId` (`:134-145`). `LiveCount = _nextId - 1 - _freeCount` es derivado, O(1), sin coste en `Register` (`:78`).
- **Marcas en bitset:** `_marks: ulong[]` unmanaged, 8× menos memoria y cache lines que un byte-array (`:94-98`), limpiado en bloque al inicio de cada colección (`:248`).
- **Pila de marcas:** `_marksStack` unmanaged, crece mid-collection (`:388-394`).
- **Edades:** `_ages: byte[]` unmanaged por slot; sobrevivir incrementa la edad hasta saturar `Age.MaxValue` (`:292-293`); se resetea en `Register`/`Release`/sweep.
- **Raíces:** pila del intérprete (`[stackStart, stackTop)`), `staticBlocks` (`SurtrStaticBlock`, marcados incondicionalmente por `ReferenceSlots`, `:258-263`), `explicitRoots` (roots del host + raíces transitorias staged en el slack del buffer, `SurtrRuntime.cs:1331-1338`).
- **Trazado:** marca + empuja; luego `entities[@ref].VisitReferences(marker)` por cada marcada (`:274-278`). `SurtrEntityMarker` es un ref struct que expone solo `Mark` (`:411-455`).
- **Sweep:** `fullCollection=false` (nursery) **perdona** cualquier entidad con `age>0` aunque sea inalcanzable (`:297-298`); `fullCollection=true` barre todo lo no marcado. Métricas con `Stopwatch` (`:246,308`).
- **Expansión:** `ExpandCapacity` duplica y reasigna `Entities` (Array.Resize) + 3 buffers HGlobal (`:366-386`). Es la única señal de presión que existe hoy.

## 2. Dónde y cómo se dispara hoy: SOLO manual

Confirmado por búsqueda exhaustiva:

- Único punto de entrada: `SurtrRuntime.Collect(bool fullCollection = true)` (`SurtrRuntime.cs:1300-1309`), que delega en el overload de 4 args (`:1320-1346`), el único llamador de `EntityRegistry.CollectGarbage` (`:1340`).
- `SurtrVirtualMachine.cs` (3.244 líneas) **no contiene ninguna llamada a `Collect`**. El bench lo dice explícitamente: *"Surtr's collector only runs when the host asks it to"* (`SurtrDriver.cs:109`).
- Llamadores reales: solo host/tests/bench (`SurtrArrayTests`, `SurtrDictionaryTests`, `SurtrTupleTests`, `SurtrInstanceTests`, `SurtrStandardLibraryTests`, etc.).

**Implicación de diseño:** un GC automático es un cambio de contrato. La política debe ser **opt-in** con default `Manual` (comportamiento actual intacto).

## 3. Puntos de integración posibles (safepoints y contadores)

### Los safepoints que YA existen

1. **Transferencia a código nativo** — `InvokeResolved` (`SurtrVirtualMachine.cs:3063-3082`): `_sp = sp; current.IP = ip;` se publican **antes** de `EntryPoint.Invoke(...)` (`:3068-3069`). El `SurtrCallArguments` apunta a la data stack **fija** (nunca se reasigna), así que el puntero sigue válido a través de una colección. Este es el safepoint de máxima confianza.
2. **Cada opcode que asigna** publica `current.IP = ip; _sp = sp;` antes de `Register` (22 sitios).
3. **Cada trap/throw**, **cada return**, y el **chequeo de presupuesto por salto/switch/call** (`Branched`, `:673-680`).
4. `Collect()` lee `machine.StackBase/StackTop/FrameRoots` (`SurtrRuntime.cs:1308`) — el contrato documentado es que el machine publica su top antes de toda transferencia a código host (`:1288-1293`).

### Los 22 sitios de `Register` en el VM (todos opcodes de asignación)

`BoxInt 1342`, `BoxFloat 1353`, `BoxBool 1364`, `BoxChar 1375`, `BoxDynamic 1398`, `StrCat 1564`, `ArrNew 1611`, `ArrNewX 1637`, `ArrPack 1654`, `TupPack 1824`, `DictNew 1897`, `DictPack 1914`, `DictKeys 2034`, `DictValues 2052`, `ObjNew 2103`, `ObjNewX 2120`, `NewClosure 2187`, `NewClosureX 2208`, `BoxAs 2997`, `BoxAsX 3012`, `RangeNew 3028`, `RangeNewInclusive 3042`. Además ~20 fábricas del runtime (lado host/nativo).

**El punto único para un contador es `Register` mismo** (`SurtrEntityRegistry.cs:126-153`): todo confluye ahí. Un `++_allocationsSinceLastCollection` tras asignar el id captura cada entidad nueva, incluido el reuso del free list.

## 4. Semántica contractual que debe sobrevivir (tests)

De `SurtrEntityRegistryTests.cs`:

- **Nursery vs full:** `fullCollection=false` perdona `age>0` aunque sea inalcanzable; full barre; la edad satura sin wraparound.
- **Roots explícitos** nombran y perdonan; trazado transitivo; ciclos inalcanzables se recogen; un root no-referencia se ignora.
- **Stack:** se marca `[stackStart, stackTop)`; lo que pasa de `stackTop` se ignora.
- **Static blocks:** marcados incondicionalmente.
- **Registry:** doble-Register idempotente; null → `NullRef`; free list LIFO; expansión preserva.
- **Métricas:** `TotalCollections == Nursery + Full`; `TotalCollectedEntities` contado por kind.

Cualquier integración de política tiene que preservar estas semánticas exactamente.

---

## 5. Diseño propuesto: sistema de políticas `SurtrGcPolicy`

### 5.1 Modelo

Una `readonly struct` copiada dentro del runtime (nada de clases alocadas en el hot path). Vive como campo `internal SurtrGcPolicy GcPolicy;` en `SurtrContext` (`SurtrContext.cs:32-33`).

```csharp
public enum SurtrGcMode { Manual = 0, Automatic = 1, Hybrid = 2 }

public readonly struct SurtrGcPolicy
{
    public SurtrGcMode Mode;                 // default Manual (comportamiento actual)
    public long  AllocationThreshold;        // n asignaciones desde la última colección
    public int   LiveEntityThresholdPercent; // % de Capacity; 0 = desactivado
    public int   NurseryFrequency;           // cada N colecciones → full (default 1)
    public long  CollectionBudgetTicks;      // advisory: pausa máxima tolerada
    public bool  SafepointOnly;              // true = diferir al siguiente safepoint (recomendado)
    public static SurtrGcPolicy Manual { get; }
}
```

| Parámetro | Mapeo al código existente |
|---|---|
| `Mode` | `Manual` = estado actual (solo `runtime.Collect()`); `Automatic` = solo la política; `Hybrid` = ambos. Con `Manual`, los umbrales se pliegan a `long.MaxValue` → el compare es siempre-falso y predicho perfecto. |
| `AllocationThreshold` | Contador nuevo `_allocationsSinceLastCollection`, incrementado en `Register` (`:147`), reseteado al final de `CollectGarbage` (junto a `_totalCollections++`, `:309`). |
| `LiveEntityThresholdPercent` | `LiveCount` (`:78`) vs `Capacity` (`:66`), evaluado en safepoint. |
| `NurseryFrequency` | Mapea 1:1 al bool `fullCollection` (`:244,312-315`) y a las edades (`:297-298`). |
| `CollectionBudgetTicks` | Advisory post-colecta: si `_lastCollectionElapsedTicks` (`:64`) supera el presupuesto, la política puede subir `NurseryFrequency` (más nurseries, menos fulls) o degradar la siguiente a nursery. El collector es stop-the-world; un presupuesto duro exigiría marcado incremental (fuera de alcance). |
| `SafepointOnly` | Si `true` (recomendado), `Register` solo **arma la bandera** `_gcPending`; la colección corre en el siguiente safepoint. Si `false`, se permite inline con los riesgos de §7. |

### 5.2 Dónde se engancha el trigger (recomendación)

| Hook | Coste | Reentrancia | Veredicto |
|---|---|---|---|
| **(i) `Register`** como fuente del trigger | ~2-4 uops (`mov/add/cmp/jcc`), branch predicho not-taken; en `Manual`, 1 cmp | NO seguro para correr inline | ✅ **Fuente** del trigger (único choke point) |
| **(ii) Contador en los 22 opcodes de asignación** | Idéntico, pero duplicado y pierde las fábricas del runtime | Idéntico | ❌ Dominado por (i) |
| **(iii) Frontera nativa `InvokeResolved`** | ~2 uops por native call (frío) | Safepoint ya publicado; puntero de args sigue válido | ✅ **Punto de ejecución** |
| **(iv) `Branched` cada N saltos** | ~2 uops, correlaciona mal con presión de asignación | Seguro | ⚠️ Backstop opcional (off por defecto) |
| **(v) `ExpandCapacity`** | 0 en hot path (NoInlining) | El entity en vuelo no está en la tabla | ❌ No como trigger; sí como **hint** (forzar próxima full) |

**Recomendación: (i) como fuente + (iii) como ejecución**, con (iv) en `Branched` como backstop opcional. Coste en estado estable: ~2-4 uops por asignación + ~2 por native call. La ejecución de la colección es la existente; la política solo la agenda.

### 5.3 Exposición al host

```csharp
SurtrRuntime.ConfigureGc(in SurtrGcPolicy policy);   // copia a _context.GcPolicy; valida rangos
SurtrGcStats SnapshotGcStats();                       // TotalCollections, TotalNursery, TotalFull,
                                                      // TotalCollected, AllocationsSinceLastCollection,
                                                      // LastCollectionMs, LastCollectionKind
```

Ya existen `LiveObjectCount` (`:167`), `HeapCapacity` (`:160`), `TotalCollections` (`:1349`), `TotalCollectedObjects` (`:181`), `LastCollectionMilliseconds` (`:1356`). `SnapshotGcStats()` devuelve una struct-copia para evitar lecturas a medias. La colección automática debe invocar el overload de 1 arg (`:1300-1309`) para tomar `StackBase/StackTop/FrameRoots` automáticamente.

---

## 6. Pasos de implementación

1. `_allocationsSinceLastCollection` en `Register` (`:147`) + `_gcPending`/`_collecting` en el registry.
2. `SurtrGcPolicy` struct + `SurtrGcMode` enum + `ConfigureGc` con validación (rechazar `AllocationThreshold < 1`, `NurseryFrequency < 1`, percent fuera de `[1,100]`).
3. Ejecutor de colección diferida en el VM: en la frontera nativa (`InvokeResolved`, tras recargar `entities` en `:3081`) y, opcional, en `Branched`. Marcado `[NoInlining]` y en rama fría.
4. `SnapshotGcStats()`.
5. Tests nuevos: (a) una colección disparada por política **nunca** reclama una entidad recién registrada aún no empujada al stack; (b) default `Manual` no cambia nada del comportamiento actual; (c) `NurseryFrequency` se comporta como los tests existentes de nursery/full; (d) reentrancia: un native que aloca mientras `_gcPending` está armado no provoca colección inline.

---

## 7. Riesgos concretos y cómo se mitigan

1. **Reentrancia (native que aloca mientras se dispara la política).** Si la colección corriera inline en `Register`, un string recién creado que solo vive en un local del native se barrería → ref colgante. **Regla de oro: `Register` nunca corre una colección; solo incrementa el contador y arma `_gcPending`.** La colección corre en el siguiente safepoint, cuando el valor ya fue empujado al stack (`*sp++ = result`, `:3078`).
2. **Pausar a mitad de bytecode.** Nunca: la colección corre entre instrucciones (frontera nativa/`Branched`), con `current.IP`/`_sp` ya publicados. El camino de colección debe **no lanzar** (try/catch en la capa de política) para no desenrollar el switch. El guard `_collecting` evita re-entrar.
3. **Objetos mid-construcción** (por qué el inline es inviable): `NewClosure` saca las capturas del stack **antes** de `Register` (`:2183,2204`); `ArrPack`/`TupPack`/`DictPack` llenan elementos **después** de `Register` (`:1657-1660,1827-1830,1917-1938`); los `Box*` solo tienen el valor en el local. El modelo diferido elimina todos estos casos.
4. **Lo que ya protege el diseño actual:** data stack fija que nunca se reasigna (el puntero de `SurtrCallArguments` sigue válido); roots explícitos + staging en slack (`:1331-1338`); static blocks incondicionales; `FrameRoots` con excepción en vuelo y closures de frames (`:188-192`).
5. **Lo que hay que añadir:** contador de asignaciones, bandera `_gcPending` + ejecutor, guard `_collecting`, struct de política + `ConfigureGc`, `SnapshotGcStats()`, camino de colección `[NoInlining]`.

---

## 8. Conclusión

El recolector ya tiene todo lo que necesita una política (generaciones por edad, nursery vs full, bitset, free list, métricas). **Falta únicamente el disparador**: un contador en `Register` + un ejecutor en la frontera nativa. El coste en el hot path es de ~2-4 uops por asignación (predichos not-taken), y con default `Manual` el comportamiento actual queda intacto byte a byte. La automatización es de bajo riesgo y alto beneficio, sobre todo si se combina con la propuesta del informe de rendimiento (buffers unmanaged con registro de buffers coleccionables), donde el sweep necesitará un hook de liberación por entidad que la misma lista de buffers coleccionables proporciona.

---

## 9. Estado de implementación (2026-08-22)

Lo propuesto en este documento se ha implementado, con estas decisiones tomadas por el usuario y diferencias frente a la propuesta inicial:

- **Modo por defecto: `Automatic`** (la propuesta recomendaba `Manual`). `SurtrContext.Initialize` configura `SurtrGcPolicy.Automatic` (`SurtrContext.cs`). Un host que quiera el comportamiento antiguo llama `SurtrRuntime.ConfigureGc(SurtrGcPolicy.Manual)`.
- **Safepoints: sitios de asignación + frontera nativa.** Cada opcode de asignación completa (empuja su resultado) y enruta a **un único `Safepoint`** tras el `switch` del dispatch (`SurtrVirtualMachine.cs`), que revisa `GcPending` y recolecta. Hay exactamente un call site de recolección en el intérprete, fuera del hot path del dispatch (nada cae a él). La frontera nativa (`InvokeResolved`) es el segundo safepoint para cuerpos host que asignan.
- **`SafepointOnly` eliminado:** siempre se difiere al safepoint (lo seguro). **`CollectionBudgetTicks` eliminado:** solo era advisory y el collector es stop-the-world.
- **Parámetros implementados:** `Mode` (`Manual`/`Automatic`, sin `Hybrid` — innecesario porque `Collect()` manual siempre funciona), `AllocationThreshold`, `LiveEntityThresholdPercent`, `NurseryFrequency`. Definidos en `SurtrGcPolicy` (`Runtime/Objects/SurtrGcPolicy.cs`).
- **Mecánica:** `Register` hace `++_allocationsSinceLastCollection; if (>= _allocationThreshold) _gcPending = true;` (1 add + 1 cmp predicho not-taken; en Manual el umbral se pliega a `long.MaxValue`). `ExpandCapacity` arma `_gcPending` si el umbral de entidades vivas está activo. `CollectGarbage` drena el flag y reinicia el contador al final.
- **API nueva en `SurtrRuntime`:** `ConfigureGc(in SurtrGcPolicy)`, `GcPolicy`, `AllocationsSinceLastCollection`, `TotalFullCollections`, `TotalNurseryCollections`, y el `CollectAtSafepoint()` interno que el VM invoca.
- **`_collecting` no hizo falta** como guard de reentrancia: el camino de política solo se entra desde el safepoint del VM (no ejecuta bytecode), y una colección manual dentro de un native drena el flag de todos modos, evitando el doble barrido.
- **Tests:** `Surtr.Tests/Runtime/Objects/SurtrGcPolicyTests.cs` (9 tests) cubren el default, la configuración/validación, Manual vs Automatic, el drenado por colección manual, `NurseryFrequency`, y end-to-end los safepoints (bucle puro bytecode y frontera nativa). Suite completa: 2438 tests en verde.
- **Rendimiento (mismo equipo, A/B con `git stash`):** el hot path del dispatch queda prácticamente intacto — `intLoop` +0,8 %, `methodCalls` +0,6 %, `virtualCalls` +1,1 %, y varios workloads salen más rápidos (−1 a −28 %) por efectos de layout del JIT. El único coste real es `stringInterp` (+76 %): recolecta ~30 veces durante la ejecución (300k asignaciones, umbral 10k) y baja el `kept` de 300k a 1 objeto — el trade-off memoria/velocidad que el GC automático persigue. Un host que prefiera más velocidad en ese caso sube `AllocationThreshold` o usa `Manual`.
- **Nota:** el benchmark (`src/Surtr.Bench`) no podía ejecutarse por un problema preexistente: `SourceRoot="D:/proj/src"` y `ModulePath="bench"` no coincidían con el módulo `bench.Bench` que deriva el compilador (`ModulePath.TryDerive`), así que el link name del `native fun hostAdd` no se registraba. Se corrigió `ModulePath` a `bench.Bench` (y los comentarios del driver).