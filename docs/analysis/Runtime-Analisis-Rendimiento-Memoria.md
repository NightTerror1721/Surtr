# Informe: Análisis de rendimiento y memoria del runtime de Surtr.Core

> **Fecha:** 2026-08-22
> **Actualización (2026-08-22):** la recomendación B (GC automático con políticas) quedó **implementada** el mismo día — ver `Registry-GC-Politicas.md` §9. Las secciones que la citan como pendiente están corregidas.
>
> **Estado del plan de mejoras (2026-08-22, tras implementación + bench A/B):**
> - **HECHO:** B (GC automático, ya estaba), D (Register con `out bool resized`), I (puesta a cero de locales ≤16 B inline), F (inline cache para `InvokeInterface`, `interfaceCalls` −32 %), A (buffers de array unmanaged + pool, memoria de arrays −100 % bytes managed).
> - **DESCARTADO con evidencia:** E (jump table keyed por valor, el orden es irrelevante), H (identidad de referencia por boxing), G (el internado enraizado choca con el GC).
> - **Medición:** el switch gigante de `Run` es hipersensible al layout JIT entre binarios (±30-40 % de ruido por-workload); los deltas fiables son los consistentes entre todos los binarios (arriba). Suite: 2438 tests en verde.
> **Alcance:** `Surtr.Core` (netstandard2.1, `AllowUnsafeBlocks`), el runtime, el VM y los objetos.
> **Método:** lectura profunda del intérprete (`SurtrVirtualMachine.cs`, 3.244 líneas), la representación de valores (`SurtrValue.cs`), el registry (`SurtrEntityRegistry.cs`), la memoria no administrada (`MemOps.cs`, `SurtrNativeArray.cs`) y los planes `docs/VM-Plan.md` / `docs/Runtime-Model.md`.

---

## 1. Estado actual del runtime

### 1.1 Lo que ya está bien resuelto (rápido y lean)

1. **`SurtrValue` es un struct NaN-boxed de 8 bytes.** `[StructLayout(LayoutKind.Explicit, Size = 8)]` (`SurtrValue.cs:15-16`). Enteros, flotantes, bools, chars y *nullable primitives* **nunca tocan el heap gestionado**: el tag `Absent` (`0xFFF6`) hace que `int?`/`float?`/`bool?`/`char?` sean de primera clase sin asignación. `IsReference` es un único `and + cmp` (`SurtrValue.cs:214-218`).
2. **Pila de datos unmanaged de capacidad fija.** Un bloque `MemOps.AllocateZeroed<SurtrRawValue>(64*1024)` (`SurtrVirtualMachine.cs:146`), direccionada por puntero `sp` que nunca se invalida (nunca se reasigna, `:26-45`). Un solo chequeo de desbordamiento por llamada, no por push (`:3101-3106`).
3. **Dispatch por switch único con `goto Dispatch`** (`SurtrVirtualMachine.cs:682-683`): el techo teórico de C# (jump table + salto indirecto, sin call/prologue por instrucción). El método corre bajo `AggressiveOptimization`; los estados calientes (`ip`, `sp`, `frameBase`, punteros a constantes/tablas, `entities`) viven en **locales** recargados una vez por frame en `LoadFrame` (`:633-647`).
4. **Calling convention con copia cero:** los argumentos ya están en pila; el frame del callee empieza debajo (`:3096-3136`); los locales se ponen a cero solo por encima de los argumentos (`:3110-3111`). Opcodes especializados (`Ldl0..5`, `Stl0..5`, `Ldc0..9`, `IncLocal`) eliminan decodificación de operandos en bucles contados.
5. **Dispatch de interfaz sin scan lineal:** `InterfaceIndexById` es open-addressed y unmanaged (`SurtrClass.cs:171`), resuelta con máscara + carga + comparación (`SurtrVirtualMachine.cs:2802-2818`).
6. **`StrCat` de n operandos asigna una sola vez** (buffer `_concatBuffer` reutilizado + `string.Create` con `SpanAction` estático cacheado, `:96-114,1549-1562`).
7. **Excepciones sin excepción CLR cuando hay handler en alcance** (`TryEnterHandler`). Medido: es el mejor resultado de Surtr frente a C# (VM-Plan §3.7).

### 1.2 Lo que hoy es caro o lento

1. **Cada objeto Surtr = 2 asignaciones CLR (menos `SurtrArray`).** El objeto (`SurtrInstance`, `SurtrTuple`, `SurtrClosure`) + su array de respaldo `SurtrValue[]` gestionado (`SurtrInstance.cs:29,33`; `SurtrTuple.cs:40,53`). `SurtrArray` ya no: su buffer es unmanaged (`SurtrArray.cs:47-49`) — 1 sola asignación CLR. Todo lo demás vive en el heap del CLR y es rastreado también por el GC del CLR.
2. **~~`ArrGet`/`ArrSet` pagan doble bounds check~~ — ELIMINADO para arrays (2026-08-22):** el buffer unmanaged no tiene bounds check CLR; solo queda el trap explícito (`SurtrVirtualMachine.cs:1691-1696`).
3. **El diccionario es el `Dictionary<TKey,TValue>` del BCL** (`SurtrDictionary.cs:58,65`). La vía general paga un interface call no devirtualizable por operación. `dictString` sigue a ~6,4× del baseline C# (VM-Plan §3.5).
4. **~~No hay GC automático.~~ HECHO (2026-08-22):** `SurtrGcPolicy` + safepoints implementados; ver `Registry-GC-Politicas.md` §9. `ExpandCapacity` (`SurtrEntityRegistry.cs:455-482`) sigue haciendo `Array.Resize` + 3 realloc HGlobal, pero ahora arma la bandera de colección cuando el umbral de entidades vivas está activo.
5. **Coste no nulo por `Register`:** 3 escrituras de estado + recarga del local `entities` en la VM tras cada asignación (18+ sitios).
6. **Sin inline cache en `InvokeVirtual`/`InvokeInterface`** (deref del receptor + carga de vtable, `:2759-2760`; sonda open-addressed para interfaces, `:2813-2818`).
7. **1 salto indirecto por instrucción** (~2-3 ciclos solo el salto, con ~221 opcodes la predicción no es perfecta).

### 1.3 Qué memoria se gasta

| Componente | Coste |
|---|---|
| Pila de datos VM | 512 KB unmanaged por VM (64K × 8 B) |
| Call stack + roots | `SurtrCallFrame[1024]` + `SurtrRawValue[1025]` gestionados (`:150-151`) |
| Registry | `Entities[capacity]` gestionado + `_freeIds`, `_marks` (bitset), `_ages`, `_marksStack` unmanaged |
| Por objeto | objeto CLR (~40-56 B cabecera: `Class`+`TypeCode`+`SurtrRef`) + array `SurtrValue[]` de 8 B/slot |
| Chunks | bytecode, constantes y tablas de índices en `SurtrNativeArray` unmanaged; strings gestionados |
| Estáticos de clase/módulo y tablas de interfaz | unmanaged |

---

## 2. Cuellos de botella y oportunidades, por impacto esperado

### A. [CRÍTICO] Representación de objetos: mover los buffers de respaldo a memoria no administrada — HECHO (2026-08-22, alcance: `SurtrArray`)

- **Hoy:** `SurtrArray.Items`, `SurtrInstance.Fields`, `SurtrTuple.Elements` son `SurtrValue[]` gestionados. Cada array/instancia/tupla = **2 objetos CLR**, doble bounds check en `ArrGet`/`ArrSet`, y el GC del CLR ve todo el tráfico.
- **Implementado (2026-08-22):** `SurtrArray.Items` ahora es un buffer unmanaged (`SurtrRawValue*` + `ItemsCapacity`), liberado por el sweep a través del hook `ISurtrNativeBufferOwner` (interface + `ReleaseBuffer` en `Release`/sweep/`Dispose` del registry) y reutilizado por `SurtrValueBufferPool` (pool thread-local por clases de tamaño, presupuesto acotado por clase). Un solo objeto CLR por array, cero bounds checks CLR en `ArrGet`/`ArrSet`, el collector no rastrea el buffer.
- **Medido:** memoria managed de los workloads de array −100 % (de 1.0M/4.2K a 56 B por run); `sortArray` −23 %; `arrayFill`/`arrayIndex` −20-30 % en la mayoría de binarios (ruido de layout JIT del switch dificulta deltas por-workload estables).
- **Alcance reducido tras medir:** `SurtrTuple`, `SurtrClosure` y `SurtrInstance` **quedan en managed**. El coste por objeto del buffer unmanaged (rent/return + puesta a cero) regresó la creación masiva de objetos (`tuples` +19 %, `allocation` +8-13 %, `retainedObjects` +14 % con spreads fiables) incluso con pool lock-free/thread-local; las instancias ganan `fieldAccess` pero pierden `allocation`/`retainedObjects`. Se eligió el subconjunto sin regresiones fiables: solo arrays.
- **Bloqueante original resuelto por B:** el sweep de `CollectGarbage` es el punto único donde el hook de liberación cierra el hueco de ciclo de vida.

### B. ~~[ALTO] GC automático~~ — HECHO (2026-08-22)

Implementado: `SurtrGcPolicy` (Manual/Automatic, umbral de asignaciones, umbral de entidades vivas, frecuencia de nursery), bandera `GcPending` armada en `Register` (1 compare plegado en Manual), safepoint único tras el dispatch + frontera nativa en el VM, API `SurtrRuntime.ConfigureGc`, default `Automatic`. Detalles y medición A/B en `Registry-GC-Politicas.md` §9.

### C. [ALTO] Diccionario propio open-addressed sobre un buffer unmanaged

- **Hoy:** `SurtrDictionary` envuelve el `Dictionary` del BCL. La especialización `{int:V}` ya quitó el interface call para claves int (medido: `dictOps` 2,2×→1,5×; `dictMembers` −35 % — VM-Plan §3.5), pero la vía general paga `IEqualityComparer<SurtrValue>` por operación.
- **Propuesta:** tabla open-addressed con claves/valores `SurtrRawValue` crudos en un solo buffer unmanaged (cero objetos por inserción, cero interface calls). Fast path por identidad de `SurtrString` para claves string. Requiere el mismo hook de ciclo de vida que (A).
- **Riesgo:** reimplementar un hash table correcta (rehash, borrado con tombstones, factor de carga) es trabajo fino; empezar por la vía de claves string.

### D. [MEDIO-ALTO] Abaratar el registro y el deref de entidades — HECHO (2026-08-22)

- Cada sitio de asignación hace `new X()` → `Register` (3 escrituras) → recarga `entities = context.EntityRegistry.Entities` (18+ sitios). Son ~5-8 instrucciones de libro por asignación.
- **Implementado:** `Register(entity, out SurtrRuntimeEntity?[] entities)` devuelve el array directamente (siempre asignado, tras cualquier `ExpandCapacity`). El load del campo `Entities` se comparte (CSE) con el `Entities[newId] = entity` que ya hacía el propio `Register`, así que el camino caliente paga un mov de registro y **cero branches** frente al `if (resized)` anterior. A/B medido: `allocation` −8 %, `tuples` −9 % (los deltas grandes de intLoop/methodCalls/arrayIndex en esa sesión eran ruido de layout).

### E. [MEDIO] Orden de los `case` del dispatch por frecuencia — DESCARTADO

Con 221 targets, la predicción de saltos indirectos falla parcialmente. **Evaluado y descartado (2026-08-22):** el switch de `Run` compila a una única jump table indexada por el valor del opcode (IL verificado: 1 instrucción `switch`), donde el orden de los `case` en el fuente es irrelevante. El único efecto orden-dependiente sería la localidad I-cache de los cuerpos de los `case`, que necesita un perfil de frecuencia por opcode que el benchmark no produce (solo por workload). Reordenar ~220 `case` a ciegas por un beneficio estimado <1-2 % es riesgo sin retorno medible.

### F. [MEDIO] Inline cache monomórfico en `InvokeVirtual`/`InvokeInterface` — HECHO (solo interfaces)

1 entrada por call site: comparar `receiver.Class` contra la clase esperada con una sola carga; si acierta, saltar deref/indirecto. **Implementado (2026-08-22):** caché per-chunk indexada por el método declarado, perezosa, solo para `InvokeInterface` — el virtual resuelve con una sola carga de vtable y el caché le añadía instrucciones sin quitar nada (medido: `virtualCalls` sin cambio neto). Resultado: `interfaceCalls` −38 % (9,21 → 5,72 ms). Requiere vtables/tablas de interfaz inmutables post-link, lo que el linker garantiza.

### G. [MEDIO] Strings: `StrCat` asigna CLR string + `SurtrString` (2 objetos) — DESCARTADO

Internar resultados cortos o usar un `SurtrString` "vista" sobre un buffer; mantener el `string.Concat` vectorizado del CLR (no reimplementar).

**Evaluado y descartado (2026-08-22):** internar con `InternString` enraiza permanentemente (contrato ya existente para literales), lo que para resultados computados cortos **lucha contra el GC automático** — `stringInterp` produce 100k strings distintas ≤16 chars; internarlas pasaría `kept` de 2 a 100k (retención ilimitada). Una caché acotada (FIFO enraizada rotatoria) solo ayuda a patrones de concatenación repetitiva que ningún workload del bench ejercita (`stringConcat` es cuadrático, sus resultados crecen y no quedan cortos). El coste real de `stringInterp` lo ataca la Fase A (menos objetos CLR por asignación), no el internado.

### H. [MEDIO] Caché de boxes para ints pequeños (−128..127) — DESCARTADO

`BoxInt` etc. = `new SurtrBoxed` + `Register` (`:1337-1346`). **Evaluado y descartado (2026-08-22):** el contrato del lenguaje da **identidad de referencia por boxing** — dos `BoxInt 5` deben ser referencias distintas (tests `BoxInt_ProducesALiveReferenceEachTime`, `REQ_OfTwoDistinctBoxes_IsFalse_EvenWithEqualContent`, `RNE_OfTwoDistinctBoxes_IsTrue`, `JPRNE_BranchesOnDistinctReferences_EvenWithEqualContent`). Compartir un box cacheado los haría iguales por referencia. Además pre-registrar los boxes contaminaría el contador de asignaciones del GC y desplazaría el espacio de ids. El objetivo (boxing más barato) se cubre con la Fase A (arena unmanaged) sin tocar la semántica.

### I. [BAJO] Puesta a cero de locales en la entrada de frame

`MemOps.Clear` (`:3110-3111`) es un método grande no inlineable. Inline de los casos ≤16 bytes con un `*(ulong*)` directo y reservar `Clear` para frames grandes.

---

## 3. Delegación a lo no administrado: estado y candidatos

### Ya es unmanaged hoy

| Componente | Dónde |
|---|---|
| Pila de datos de la VM | `SurtrVirtualMachine.cs:146` |
| Bytecode, constantes, offsets de método, slots de strings | `SurtrChunk.cs:33-61` (`SurtrNativeArray`) |
| Estáticos de clase/módulo + slots de referencia | `SurtrClass.cs:97-113`, `SurtrModule.cs:48-51` |
| Tablas de dispatch de interfaz | `SurtrClass.cs:144-186` |
| Registry: free-list, bitset, pila de marcas, edades | `SurtrEntityRegistry.cs:109-114` |
| `MemOps` completo (AllocHGlobal/ReAlloc/Free + Clear/Fill/Compare vectorizados) | `MemOps.cs:15-320` |
| `SurtrNativeArray<T>` (puntero + longitud) | `SurtrNativeArray.cs:19-91` |

### Sigue siendo gestionado hoy, candidato a no administrado

| Componente | Dónde | Esfuerzo | Riesgo |
|---|---|---|---|
| Buffers de respaldo de arrays/instancias/tuplas | `SurtrArray/Instance/Tuple` | Alto | Ciclo de vida (ver A) |
| Tabla `Entities` del registry | `SurtrEntityRegistry.cs:47` | Alto | Contiene referencias CLR; `VisitReferences` virtual; no puede ser unmanaged tal cual |
| Tablas de métodos/vtable gestionadas | `SurtrClass.cs:121-126` | Alto | Metadata; no está en el hot path crítico |
| `SurtrDictionary` | `SurtrDictionary.cs:58,65` | Medio | Ver C |

---

## 4. Conteo de asignaciones por opcode representativo

| Opcode | Asignaciones CLR hoy | Con la propuesta |
|---|---|---|
| `BoxInt/Float/Bool/Char` | 1 (`SurtrBoxed`) + registro | 0 (caché de boxes) o 1 arena |
| `StrCat` | 2 (CLR string + `SurtrString`) | 1–2 (depende de internado) |
| `ArrNew`/`ArrPack` | 2 (objeto + `SurtrValue[]`) + registro | 1 (arena) |
| `TupPack` | 2 (objeto + `SurtrValue[]`) + registro | 1 (arena) |
| `DictNew`/`DictPack` | 2 (objeto + `Dictionary` BCL) + registro | 1 (arena) |
| `ObjNew` | 2 (objeto + `SurtrValue[]` de campos) + registro | 1 (arena) |
| `NewClosure` | 1 (objeto + array de capturas) + registro | 1 (arena) |
| `RangeNew` | 1 (`SurtrRange`) + registro | 0 (inline como valor) si se decidiera |

---

## 5. Recomendaciones priorizadas

1. **~~GC automático con políticas~~ HECHO (2026-08-22)** — ver `Registry-GC-Politicas.md` §9.
2. **~~Buffers de objetos en `SurtrNativeArray`~~ HECHO para `SurtrArray` (2026-08-22)** — hook `ISurtrNativeBufferOwner` + pool thread-local; tuplas/closures/instancias quedan en managed tras medir regresiones de creación (ver §2.A).
3. **Diccionario open-addressed unmanaged** (impacto alto, esfuerzo alto). **Diferido**: depende del hook de ciclo de vida de la fase 2; la especialización `{int:V}` ya cubre el caso caliente.
4. **Abaratar `Register` + recarga de `entities`** — HECHO (2026-08-22): `Register(entity, out entities)` devuelve el array por out (sin branch); `allocation`/`tuples` −8/9 %.
5. **Inline cache monomórfico** — HECHO para interfaces (2026-08-22), `interfaceCalls` −38 %. El virtual no se beneficia (una sola carga de vtable).
6. **Caché de boxes pequeños + reorden de `case`** — AMBOS DESCARTADOS (2026-08-22): identidad de referencia por boxing (contrato de lenguaje) y jump table keyed por valor (el orden es irrelevante).
7. **Internado de `StrCat`** — DESCARTADO (2026-08-22): el internado con enraizado permanente choca con el GC automático (ver §2.G).

Todo esto asume que el objetivo es un runtime "lo más rápido y veloz posible, consumiendo el mínimo de memoria, delegando lo que se pueda al mundo no administrado" — la dirección que el propio código ya marca en `MemOps`, `SurtrNativeArray` y el registry.