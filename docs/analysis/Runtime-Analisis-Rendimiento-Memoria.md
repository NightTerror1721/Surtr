# Informe: Análisis de rendimiento y memoria del runtime de Surtr.Core

> **Fecha:** 2026-08-22
> **Actualización (2026-08-22):** la recomendación B (GC automático con políticas) quedó **implementada** el mismo día — ver `Registry-GC-Politicas.md` §9. Las secciones que la citan como pendiente están corregidas.
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

1. **Cada objeto Surtr = 2 asignaciones CLR.** El objeto (`SurtrArray`, `SurtrInstance`, `SurtrTuple`, `SurtrClosure`) + su array de respaldo `SurtrValue[]` gestionado (`SurtrArray.cs:50,60`; `SurtrInstance.cs:29,33`; `SurtrTuple.cs:40,53`). Todo vive en el heap del CLR y es rastreado también por el GC del CLR.
2. **`ArrGet`/`ArrSet` pagan doble bounds check** — el trap explícito (`:1676-1681`) + el del CLR sobre el array gestionado (`:1683-1688`).
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

### A. [CRÍTICO] Representación de objetos: mover los buffers de respaldo a memoria no administrada

- **Hoy:** `SurtrArray.Items`, `SurtrInstance.Fields`, `SurtrTuple.Elements` son `SurtrValue[]` gestionados. Cada array/instancia/tupla = **2 objetos CLR**, doble bounds check en `ArrGet`/`ArrSet`, y el GC del CLR ve todo el tráfico.
- **Bloqueante conocido** (documentado en `SurtrArray.cs:22-29`): un buffer unmanaged propiedad de un objeto coleccionable se filtraría en cada colección porque el sweep no tiene hook de finalización.
- **Propuesta:** mover los buffers a `SurtrNativeArray<SurtrRawValue>` (puntero + longitud). Cerrar el hueco de ciclo de vida con **un registro de buffers coleccionables en el contexto**: una lista por contexto que `CollectGarbage` drena al liberar la entidad (`SurtrEntityRegistry.cs:239-318`). La entidad, al ser recogida, devuelve su buffer a un *pool* o lo libera.
- **Beneficios:** 1 sola asignación por objeto; cero bounds checks CLR en `ArrGet`/`ArrSet`; `ArrNew` rellena con `MemOps.Fill/Clear` vectorizado (`MemOps.cs:91-128`); el collector no rastrea esos arrays; el GC del CLR deja de ver el tráfico.
- **Riesgo:** las entidades dejan de ser "solo managed" — la tabla `Entities` ya es managed y no puede moverse a unmanaged mientras contenga referencias CLR (el collector necesita `VisitReferences` virtual). Diseño mixto: objeto managed "cáscara" + buffer unmanaged.

### B. ~~[ALTO] GC automático~~ — HECHO (2026-08-22)

Implementado: `SurtrGcPolicy` (Manual/Automatic, umbral de asignaciones, umbral de entidades vivas, frecuencia de nursery), bandera `GcPending` armada en `Register` (1 compare plegado en Manual), safepoint único tras el dispatch + frontera nativa en el VM, API `SurtrRuntime.ConfigureGc`, default `Automatic`. Detalles y medición A/B en `Registry-GC-Politicas.md` §9.

### C. [ALTO] Diccionario propio open-addressed sobre un buffer unmanaged

- **Hoy:** `SurtrDictionary` envuelve el `Dictionary` del BCL. La especialización `{int:V}` ya quitó el interface call para claves int (medido: `dictOps` 2,2×→1,5×; `dictMembers` −35 % — VM-Plan §3.5), pero la vía general paga `IEqualityComparer<SurtrValue>` por operación.
- **Propuesta:** tabla open-addressed con claves/valores `SurtrRawValue` crudos en un solo buffer unmanaged (cero objetos por inserción, cero interface calls). Fast path por identidad de `SurtrString` para claves string. Requiere el mismo hook de ciclo de vida que (A).
- **Riesgo:** reimplementar un hash table correcta (rehash, borrado con tombstones, factor de carga) es trabajo fino; empezar por la vía de claves string.

### D. [MEDIO-ALTO] Abaratar el registro y el deref de entidades

- Cada sitio de asignación hace `new X()` → `Register` (3 escrituras) → recarga `entities = context.EntityRegistry.Entities` (18+ sitios). Son ~5-8 instrucciones de libro por asignación.
- **Propuesta:** que `Register` exponga un patrón que evite la recarga en los sitios calientes (p. ej. devolver el id y usar una sobrecarga que reasigne local si creció), o convertir el deref `entities[id]` en un acceso sin bounds check del CLR cuando las entidades vayan a un arena unmanaged (se funde con A).

### E. [MEDIO] Orden de los `case` del dispatch por frecuencia

Con 221 targets, la predicción de saltos indirectos falla parcialmente. Reordenar los `case` por frecuencia favorece la jump table. Bajo IL2CPP el switch C++ se compila igualmente a jump table (VM-Plan §1.1). Es el único grado de libertad restante del dispatch (computed goto y threaded code no son expresables en C#).

### F. [MEDIO] Inline cache monomórfico en `InvokeVirtual`/`InvokeInterface`

1 entrada por call site: comparar `receiver.Class` contra la clase esperada con una sola carga; si acierta, saltar deref/indirecto. Requiere un opcode variante o un campo por call site. El benchmark `virtualCalls`/`interfaceCalls` ya existe para medirlo (VM-Plan §5 lo lista como pendiente).

### G. [MEDIO] Strings: `StrCat` asigna CLR string + `SurtrString` (2 objetos)

Internar resultados cortos o usar un `SurtrString` "vista" sobre un buffer; mantener el `string.Concat` vectorizado del CLR (no reimplementar).

### H. [MEDIO] Caché de boxes para ints pequeños (−128..127)

`BoxInt` etc. = `new SurtrBoxed` + `Register` (`:1337-1346`). Caché de `SurtrBoxed` pre-registrados e inmutables (seguro por contenido — `SurtrBoxed.cs:31-34`), estilo CLR.

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
2. **Buffers de objetos en `SurtrNativeArray` + registro de buffers coleccionables** (impacto crítico, esfuerzo alto, riesgo medio). Desbloqueado por B: el sweep de `CollectGarbage` es un punto único donde colgar el hook de liberación.
3. **Diccionario open-addressed unmanaged** (impacto alto, esfuerzo alto).
4. **Abaratar `Register` + recarga de `entities`** (impacto medio-alto, esfuerzo bajo).
5. **Inline cache monomórfico** para virtuales/interfaces (impacto medio, esfuerzo medio).
6. **Caché de boxes pequeños + reorden de `case`** (impacto medio, esfuerzo bajo).

Todo esto asume que el objetivo es un runtime "lo más rápido y veloz posible, consumiendo el mínimo de memoria, delegando lo que se pueda al mundo no administrado" — la dirección que el propio código ya marca en `MemOps`, `SurtrNativeArray` y el registry.