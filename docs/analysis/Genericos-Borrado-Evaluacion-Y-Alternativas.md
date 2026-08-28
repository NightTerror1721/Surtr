# Informe: genéricos y borrado en Surtr — cómo funciona, qué cuesta, y qué pasaría si se dejara de borrar

> **Fecha:** 2026-08-24 · **Alcance:** compilador (`Surtr.Compiler`), runtime (`Surtr.Core`) y suite de benchmarks.
> **Método:** lectura directa del código y de los planes de diseño (`docs/Compiler-Plan.md` §8,
> `docs/Plan-Genericos-Metadata.md`), descomposición de los números de `benchmark_report.md`, y estimación
> de blast radius sobre el grafo del propio código. No se modificó ninguna pieza de ejecución.

---

## Resumen ejecutivo

1. El borrado de genéricos en Surtr **no es un accidente ni una deuda**: es una decisión registrada y
   acotada (`docs/Compiler-Plan.md` §8) que separa el problema en tres niveles —(a) aridad en el nombre,
   (b) argumentos en el descriptor, (c) reificación— y adopta los dos primeros rechazando el tercero.
   Todo el nivel declarativo ya sobrevive a la compilación (formato v8); lo que se borra es únicamente
   la **representación en ejecución**.
2. El coste medido del borrado está **concentrado en dos puntos**, no repartido: *caja al entrar* a un
   slot borrado (`BoxDynamic`: 1 asignación CLR + registro en el colector) y *cast+unbox al salir*
   (`Unerase`). El despacho a través de `T` **no añade coste** (emite los mismos opcodes que un receptor
   concreto) y las colecciones ya guardan slots crudos sin cajar.
3. Ese coste es real y visible: el bench `generics` es **el peor de la suite** (22.25 ms, 32 MB, 600k
   objetos, **29.5× sobre C#**), con causa atribuida y aceptada en el propio informe de benchmarks.
4. Dejar de borrar por completo (monomorfización o reificación) **mejoraría exactamente esos dos puntos**
   y nada más: ni el despacho, ni las colecciones, ni el protocolo de frames. La ganancia esperable en el
   bench `generics` es grande (est. −40–55 % tiempo, −60 % bytes) pero su precio es tocar el corazón del
   emisor (~18 métodos de `MethodBodyEmitter`), el mecanismo de identidad de `EmitContext`, el formato de
   imagen (v9+) y unas cien pruebas pineadas — con riesgo de regresión alto sobre una superficie
   auditada contra el GC.
5. **Recomendación:** no reificar ni monomorfizar todo. Si el rendimiento genérico importa, hay una vía
   intermedia incremental — *especialización dirigida* de construcciones cerradas con argumentos
   primitivos/value-kind (§5, Opción D) — que captura la mayor parte de la ganancia con una fracción del
   blast radius, y es compatible con mantener el trato de Java como modelo semántico. Corto plazo, la
   escapatoria ya existente son los tipos de valor (`vec2Math`: 0 B frente a `vec2Class`: 45.8 MB).

---

## 1. Cómo funcionan los genéricos hoy

### 1.1 La decisión de diseño: tres niveles separables

`docs/Compiler-Plan.md` §8 separa lo que históricamente se llamó "erasure" en tres niveles:

| Nivel | Qué retiene | Estado |
|---|---|---|
| **(a)** Aridad en el nombre | ``Box`1`` ≠ ``Box`2``: clases distintas, entradas distintas en tablas | **Adoptado** |
| **(b)** Argumentos en el descriptor | ``Obox:Box`1;I`` ≠ ``Obox:Box`1;S``: descriptores distintos → misma clase | **Adoptado** |
| **(c)** Reificación | La instancia sabe que es `Box<int>` (vector de argumentos / layout / vtable por instanciación) | **Rechazado** |

La consecuencia deliberada (§8, tabla "What is and is not distinguished"):

| | ¿Distintos? | Por qué |
|---|---|---|
| `Box<T>` vs `Box<T,U>` | sí | nivel (a) |
| `f(b: Box<int>)` vs `f(b: Box<string>)` | sí | nivel (b) |
| `Box<int>.get()` vs `Box<string>.get()` | **no** | un solo cuerpo compilado, compartido |

La tercera fila **es la definición** de no reificar. El compilador sí distingue: `Box<int>` lleva la
sustitución `T -> int` y `box.set("x")` es error de tipos. El runtime no.

Sobre esa base, `docs/Plan-Genericos-Metadata.md` (completado) cerró el nivel declarativo:
constraints de parámetro (formato v5), parámetros genéricos de método con símbolo `H<n>` (v6), y
superficie de reflexión con `genericArguments` sobre `Type`. Hoy el formato va por **v8**.

### 1.2 Vida del genérico en el compilador

- **Construcción internada.** No existe un "ConstructedTypeSymbol": una construcción es un
  `NamedTypeSymbol` con `_typeArguments`, creado por `NamedTypeSymbol.Construct(...)` e internado por
  lista de argumentos (`src/Surtr.Compiler/Binding/Symbols/NamedTypeSymbol.cs:367–394`). La sustitución
  vive en `TypeSubstitution` — diccionario `TypeParameterSymbol -> TypeSymbol`
  (`Symbols/TypeSubstitution.cs:17–47`), cacheado por construcción.
- **Los miembros viven solo en la declaración.** `Members` de una construcción lee los de la
  definición (`NamedTypeSymbol.cs:268–272`); `MemberLookup.MembersOf` clona campo a campo por
  construcción con los tipos sustituidos, y cada clon conserva `OriginalDefinition`
  (`Binding/MemberLookup.cs:262–280, :302, :379`).
- **Un cuerpo por declaración.** El fallback `OriginalDefinition` de
  `EmitContext.TryGetBuilder/Resolve/TryGetForeignModule`
  (`src/Surtr.Compiler/CodeGen/EmitContext.cs:167–192`; comentario en :162–165) es mecánicamente el
  mecanismo que garantiza un solo builder, una sola method table, un solo cuerpo.
- **Inferencia local de una pasada.** `TypeInference.TryInfer` une estructuralmente declarado contra
  suministrado (`Binding/TypeInference.cs:54, :111`); métodos genéricos por
  `SubstituteGenericCandidates` (`BodyBinder.Expressions.cs:~2180–2263`); creaciones por
  `BindGenericObjectCreation` (:3044–3089) con tres fuentes (argumentos escritos, tipo destino,
  unificación por constructor). Las constraints se comprueban en cada sitio de uso con los bounds
  sustituidos (:2278–2303).
- **Construcciones diferidas.** `ArgumentInfo.DeferredConstruction`
  (`Binding/OverloadResolution.cs:53–74`): `take(Box())` se resuelve tras elegir la sobrecarga, igual
  que las lambdas — no hay solver con backtracking.

### 1.3 Dónde se borra exactamente

El borrado ocurre **tarde** — en emisión, nunca antes — y en sitios contados:

| # | Sitio | Qué hace | Referencia |
|---|---|---|---|
| 1 | `DescriptorEmitter` | Parámetro de tipo → `G<n>`; de método → `H<n>`; `unknown`/`never` → `E` | `CodeGen/DescriptorEmitter.cs:164–193, :247–249` |
| 2 | `MethodBodyEmitter.TypeCodeOf` | `TypeParameter` → `SurtrValueTypeCode.Erased` (elige familia de operandos/opcodes) | `CodeGen/MethodBodyEmitter.cs:5375–5409` |
| 3 | Conversiones de borrado | `ImplicitErasure` (hacia slot borrado) / `ExplicitErasure` (desde él) | `Binding/Conversions.cs:290–291, :320–321, :372–373` |
| 4 | `BoxIfStillErased` / `UnboxIfStillErased` / `Unerase` | Emite `BoxDynamic`/`UnboxDynamic`/`CastTo`+`Unbox` en los 17 cruces de slot borrado | `MethodBodyEmitter.cs:4454–4479, :1677–1711` |
| 5 | Comparaciones fusionadas | Se niegan sobre operandos borrados → `DynEQ/DynNE` sin rama fusionada | `MethodBodyEmitter.cs:561–574`; `SurtrCodeEmitter.Helpers.cs:381–389` |
| 6 | `SignatureKey()` / `ModuleEmitter.SlotKey` | Clave de firma con todo `G<n>`/`H<n>` reescrito a `E` | `Surtr.Core/Runtime/Classes/SurtrMethodInfo.cs:629, :689–699`; `ModuleEmitter.cs:1412–1445` |

Los consumidores de la clave borrada son todos lookups de diccionario: numeración de slots de contrato
(`SurtrTypeLinker.LinkInterface`), colocación en vtable (`BuildMethodTables`), y el emparejamiento
implementación↔contrato (`BuildInterfaceDispatch`,
`src/Surtr.Core/Runtime/Classes/SurtrTypeLinker.cs:173, :696, :806–834`). Mantener `SignatureKey()`
borrado es **exactamente** lo que mantiene al linker como dictionary lookup en lugar de un motor de
sustitución (§8 de Compiler-Plan lo dice literalmente). La verruga aceptada: `f(Box<T>)` y `f(Box<U>)`
colisionan — la de Java — con arreglo esbozado y rechazado por ahora.

Los 17 cruces de slot borrado en el emisor: stores de campos `T`, resultado de llamada genérica,
resultado de campo borrado, operandos de escritura nativa en colecciones, operaciones de índice,
literales array/dict/tuple, `for-in` sobre iterable, colas de conversión y `array-from-iterable`
(`MethodBodyEmitter.cs:2854, :2983, :3666, :3775, :3812, :3890, :4398–4419, :4486, :4523, :4532–4535,
:910–912, :1637–1658, :4854`).

### 1.4 Lo que ya sobrevive (y que nada en ejecución lee)

Descriptors completos con argumentos en firmas/base/interfaces/atributos; nombres y arity de
parámetros de clase e interfaz; constraints por parámetro; parámetros y constraints de método (`H<n>`);
reconstrucción completa por `MetadataImporter`; reflexión `Type.descriptor/genericParameters/
genericConstraints/genericArguments` con caché de identidad por construcción cerrada
(`SurtrContext.ConstructedTypeValueCache`). Todo ello es metadata: **ninguna ruta de ejecución lo toca**.

Una restricción relevante para el informe: **una value class multi-campo no puede ser genérica**
(`Binder.cs:1977–1983`, diagnóstico `ValueTypeLayout`; rationale en `docs/Plan-TiposDeValor.md:85`) —
porque su anchura dependería de la sustitución, que es precisamente lo que el borrado prohíbe.
`Box<Vec2>` existe, pero el `Vec2` cruza el slot borrado **cajado como referencia**.

---

## 2. Qué cuesta realmente el borrado en el VM

### 2.1 Entrada: la caja

Un primitivo (o value class multi-slot) que entra a un slot borrado se convierte en referencia:

- `OpCode.BoxDynamic` (`VM/SurtrVirtualMachine.cs:1504–1520`): prueba de tag; si ya es referencia,
  no-op; si no, **1 asignación CLR** (`new SurtrBoxed(...)`, ~40 B: cabecera + clase + typecode +
  `SurtrValue` de 8 B), 2 stores de spill para seguridad de GC, **1 `EntityRegistry.Register`**
  (~2–4 µops: pop de freelist o watermark + store + contador de umbral de recolección;
  `Runtime/Objects/SurtrEntityRegistry.cs:163–208`), retag del stack, y salto a `Safepoint`.
- Cada entidad registrada consume **~13 B permanentes de slot de registry** hasta el barrido
  (`docs/analysis/Surtr-Bench-Informe-Rendimiento-Memoria.md`).
- Interacción con el GC automático (umbral 10k asignaciones): el modo automático cuesta +1.5 %
  geomean, pero **+11 % sobre `generics`** (600k cajas por corrida dispara ~60 recolecciones) y
  llegó a +76 % en `stringInterp` — la sensibilidad del suite a la presión de asignación está medida.

### 2.2 Salida: cast + unbox

Leer del slot borrado hacia un tipo concreto emite `Unerase` (`MethodBodyEmitter.cs:1677–1711`):
`CastTo` (que en el VM recorre `Implements`/`IsSubclassOf`, `VM:1541–1587`) + `Unbox`/`UnboxValue n`.
**No asigna**, pero tampoco es el `FieldGet` de carga por offset que emitiría un campo concreto: cada
lectura de campo/retorno genérico paga un paseo de subtipos + dispatch de unboxing.

### 2.3 Igualdad y comparación

`==` sobre `T` baja a `DynEQ`/`DynNE` (no a `EQ` directo ni a `REQ`, que daría respuestas erróneas entre
dos cajas iguales): mejor caso ~2 comparaciones de tag; peor caso dos derefs al registry + comparación
estructural (`SurtrValueComparer.ValuesEqual`, `Runtime/Objects/SurtrValueComparer.cs:48–73`). Además,
la fusión compare+branch está **desactivada** para operandos borrados (`MethodBodyEmitter.cs:561–574`).

### 2.4 Lo que el borrado NO cuesta (medido)

- **Despacho.** Un receptor borrado emite los mismos opcodes que uno concreto
  (`EmitResolvedCall`, `MethodBodyEmitter.cs:3679–3721`; tabla en `Helpers.cs:746–778`).
  Directo/virtual/interfaz miden 14.1/22.8/22.1 ns por llamada; el delta ~6 ns es la indirección de
  vtable, no el borrado. La caché en línea de `InvokeVirtual` se implementó, se midió (±4 %, no
  separa distribuciones) y **se retiró**: el techo lo fija el protocolo de frame, no la resolución.
- **Colecciones.** `array<T>` guarda slots NaN-boxed crudos sin tags por elemento
  (`Runtime/Objects/SurtrArray.cs:31–50`); `dict<int,V>` tiene store especializado que salta el
  comparador (`Runtime/Objects/SurtrDictionary.cs:58–83, :133`) y es la mejor relación de la suite
  (3.4–4.0× de C#). Dentro de la colección no se caja nada; el boxeo ocurre en los bordes de los
  cuerpos todavía genéricos.

### 2.5 Los números

| Caso | surtr ms | bytes | objs | vs C# | Nota |
|---|---|---|---|---|---|
| `generics` | **22.251** | **32.0 M** | **600k** | **29.5×** | Peor de la suite; 14.7× sobre LuaJIT |
| `allocation` | 15.087 | 22.9 M | 300k | 18.0× | Suelo natural de "asignar un objeto por iteración" |
| `iterator` | 3.592 | 1.9 M | 50k | 17.1× | Camino interfaz + boxeo de `current` |
| `forIn` | 0.957 | 56 B | 1 | 6.5× | El mismo bucle abajado a indexado: **3.8× y toda la asignación** |
| `vec2Class` / `vec2Math` | 47.3 / 31.0 | 45.8 M / **0** | 600k / 0 | 44.9× / 62.9× | Misma fuente, `class` vs `value class`: la escapatoria |

Descomposición registrada (`benchmark_report.md:357–372`): cada iteración de `generics` registra **dos
objetos** (el `SurtrBoxed` del argumento + la instancia de `Box<int>` con su array `Fields`, ~106 B
CLR); la vuelta (`Cast`+`Unbox`) no asigna nada. Internar cajas pequeñas fue descartado porque cambia
la identidad de referencias observable (`R`), y el layout inline de instancias se midió y cerró
(~2 % techo). Es decir: **dentro del modelo borrado, el coste restante ya está optimizado a fondo; lo
único que lo eliminaría es dejar de borrar.**

---

## 3. Alternativas para no borrar

### Opción A — Monomorfización total en compilación (estilo Rust/Swift)

Cada construcción cerrada usada recibe su propio cuerpo emitido desde el mismo AST bindeado: el cuerpo
de `Box<int>.get` se compila con `T=int`, sin slots borrados.

- **Pros:** elimina box-in y cast-out por completo; `EQ` directo y ramas fusionadas recuperadas;
  `FieldGet` por offset; abre la puerta a value classes genéricas multi-campo con layout por
  instanciación. Máxima ganancia posible.
- **Contras:** rompe los tres pilares actuales — (i) la identidad builder/metadata pasa de
  `MethodSymbol` a `(definición, argumentos)` y desaparecen los fallbacks `OriginalDefinition`;
  (ii) el emisor debe iterar construcciones, no declaraciones; (iii) el modelo de clases del runtime
  debe decidir: N clases por declaración (mangling por construcción → multiplica imagen, importer,
  reflexión, linker) o clase compartida con variantes privadas por construcción (puentes extra hacia
  los puntos de entrada borrados que virtual/interface siguen usando). Explosión combinatoria de
  cuerpos e imagen; tiempo de compilación; semántica nueva que decidir (`Type.of(instancia)` pasa a
  poder conocer la construcción; statics por construcción o no…).

### Opción B — Reificación estilo CLR (nivel (c))

Vector de argumentos de tipo por instancia, layout y vtables por instanciación, sustitución en runtime.

- **Pros:** un solo cuerpo por declaración se conserva; `Type.of` conoce la construcción; soporta
  layouts inline por value-kind.
- **Contras:** es el nivel (c) que el diseño rechazó explícitamente, y con razón para un intérprete:
  o cada acceso de miembro paga sustitución en runtime, o se precomputan tablas por instanciación —
  que converge estructuralmente a la Opción A con más maquinaria. El VM tendría que aprender
  sustitución de tipos en caliente. Máximo coste, beneficio superpuesto al de A.

### Opción C — Especialización en carga (stencil)

Las imágenes siguen borradas; al cargar un módulo, el linker clona y parchea los chunks de los métodos
genéricos por construcción caliente.

- **Pros:** sin cambio de formato; la explosión de código queda en memoria de proceso, no en disco;
  puede guiarse por perfil de uso.
- **Contras:** mueve la complejidad de A al tiempo de carga (frío inicial más caro — relevante en
  Unity); necesita gestión/dedup de chunks derivados y sus caches; el depurador/disassembler y las
  pruebas de bytecode ganan una dimensión. Mismo blast radius conceptual que A, en otra capa.

### Opción D — Especialización dirigida e incremental (recomendada si hay objetivo de perf)

Mantener el borrado como **modelo semántico** y añadir, como optimización transparente, cuerpos
especializados solo para construcciones cerradas cuyos argumentos son todos primitivos/value-kind —
donde el boxing es lo único que duele. Fases:

1. **P1 (intra-módulo):** el emisor detecta llamadas/creaciones directas sobre construcciones cerradas
   primitivas y emite una variante del cuerpo sin slots borrados. Sin cambios de imagen: las variantes
   son privadas del módulo. Despacho virtual/interfaz y `unknown` siguen cayendo al cuerpo borrado
   compartido (que permanece como universal fallback). Medir contra `generics`.
2. **P2 (cross-module):** persistir variantes en la imagen (formato v9) con nombre derivado del
   descriptor de construcción; `MetadataImporter` las mapea; los pending-members siguen resolviendo
   por clave borrada.
3. **P3 (opcional):** argumentos value-kind multi-campo usando la anchura que `ValueTypeLayout` ya
   calcula (equivalente a relajar la prohibición de §1.4, solo para variantes especializadas).

- **Pros:** captura el caso dominante (`Box<int>`, `Pair<float,int>`, iteradores numéricos) sin tocar
  linker ni semántica observable; reversible fase a fase; el cuerpo borrado sigue siendo la verdad.
- **Contras:** dos cuerpos para lo mismo (tamaño); no ayuda a construcciones sobre referencias (que hoy
  apenas pagan: un reference-arg no se caja); deduplicación de variantes idénticas entre sí.

### Opción E — Mantener el borrado (status quo informado)

Las micro-optimizaciones identificables ya están medidas y cerradas (layout inline de `Fields`,
cache virtual, interning de cajas). Queda como palanca barata **documentar la escapatoria**: código
genérico crítico escrito sobre `value class` y bajado a indexado no paga un byte. No es una alternativa
al borrado, sino la decisión consciente de conservarlo.

---

## 4. Pros y contras de dejar de borrar

### Pros

1. **Elimina la única asignación por cruce de slot:** sin `SurtrBoxed` (≈40 B CLR + 13 B de registry +
   safepoint) ni presión sobre el GC automático (+11 % medido en `generics` por recolecciones).
2. **Lecturas directas:** `FieldGet` por offset en lugar de `Cast`(paseo de subtipos)+`Unbox` en cada
   lectura de campo/retorno genérico.
3. **Igualdad y ramas:** `EQ` plano y compare+branch fusionados vuelven a estar disponibles sobre `T`.
4. **Valor potencial grande en genéricos numéricos:** el bench `generics` podría bajar de 29.5× a
   rangos de 8–15× (ver §6), y bucles tipo `iterator` acercarse a ratios `forIn`.
5. **Habilita value-class genérica multi-campo** (con layout por variante): el hueco semántico que hoy
   se prohibe por el borrado.
6. **Reflexión más fuerte:** `Type.of(instancia)` podría conocer la construcción (hoy documentado que
   no puede).

### Contras

1. **No mejora nada fuera de los cruces de slot.** Ni despacho (mismos opcodes; techo medido en el
   protocolo de frame), ni colecciones (ya raw), ni frames, ni excepciones.
2. **Explosión de cuerpos/imagen y de tiempo de compilación/carga** proporcional a construcciones ×
   miembros genéricos; en A/B además N identidades de clase con sus statics, atributos y handlers.
3. **Rompimiento del mecanismo de identidad actual:** `EmitContext` clavea por símbolo y resuelve por
   `OriginalDefinition`; toda la Registration/Lookup region cambia de forma.
4. **El linker y los contratos siguen necesitando la forma borrada.** `IComparable.compareTo(E)` debe
   seguir emparejando implementaciones por clave borrada (los built-ins del stdlib se declaran así,
   `SurtrContractBuiltIns.cs:49–58`): las variantes necesitan puentes hacia los puntos de entrada
   borrados que virtual/interfaz recorren, o una segunda dict de claves no borradas (el arreglo que §8
   ya esbozó y rechazó por coste).
5. **Decisiones semánticas nuevas con riesgo de especificación:** identidad de `Type.of` sobre
   instancias, statics por construcción, igualdad por referencia observable (`R`) cuando deja de haber
   caja, deduplicación de variantes.
6. **Superficie de regresión máxima:** `MethodBodyEmitter` (~5.4k líneas) es el archivo más pineado del
   proyecto; las auditorías GC de value classes (`ValueTypeGcAuditTests`) dependen del convenio actual
   de slots. Un cambio aquí invalida pins, goldens de disassembly y round-trips de imagen.
7. **Coste de mantenimiento permanente:** dos representaciones que deben seguir coincidiendo
   (borrado como fuente de verdad + variantes), o un motor de sustitución vivo en runtime (B).
8. **Interop:** la superficie host (`SurtrTypeInfo`, `SurtrMethodInfo`, reflection, source generator)
   multiplicaría vistas por construcción o necesitaría una noción nueva de "variante".

---

## 5. Qué cambios implica cada opción, por capa

Blast radius medido sobre el código (conteos de métodos/sitios, no estimación a ojo):

| Capa | Opción A/C (monomorfizar/reificar) | Opción D (dirigida, P1+P2) |
|---|---|---|
| `CodeGen/EmitContext.cs` | Toda la región Registration/Lookup (:90–285) pasa a clave `(definition, args)`; fuera fallbacks `OriginalDefinition` | Intacta para el camino borrado; tabla lateral de variantes |
| `CodeGen/ModuleEmitter.cs` | Bucle de emisión itera construcciones (:1206–1235); `Parameterise` (:406); bridges/`SlotKey` (:1301–1520) rediseñados | Nuevo pass posterior que deriva variantes y rewires call sites directos |
| `CodeGen/MethodBodyEmitter.cs` | ~18 métodos y 17 sitios de helpers de borrado se vuelven camino muerto por variante; `TypeCodeOf` concreto; fusión de comparas recuperada | Los mismos sitios, pero solo en la variante; el original no se toca |
| `Binding/` (MemberLookup, Conversions, BodyBinder, SignatureSet) | Clones con identidad propia; `Implicit/ExplicitErasure` innecesarios por variante; unicidad de sobrecargas definida hoy sobre claves borradas requiere clave partida | Casi intacto (la inferencia ya produce la construcción cerrada) |
| `CodeGen/ValueTypeLayout.cs` | Relajación de la regla del clon (:117–121); prohibición multi-campo genérica revisitable | Solo en P3 |
| Runtime (`Surtr.Core`) | Si N-clases: mangling, tablas, statics, linker, reflection — 8–10 ficheros; si clase-compartida+variantes: registro de variantes y resolución | Ninguno en P1; registro de variantes en P2 |
| Imagen | `SurtrModuleImageWriter/Reader`, pending members, `SurtrModuleBuilder`, `MetadataImporter` — **FormatVersion → 9+** | Igual, pero solo sección opcional de variantes (P2) |
| VM | Prácticamente nada (los opcodes ya existen; sobran `BoxDynamic/UnboxDynamic/DynEQ/DynNE` en variantes) | Nada |
| Tests | ~102 pruebas llevan `Generic/Erase` en el nombre (de 2097); matriz nueva construcciones×familias; goldens y auditorías GC | P1 cubrible con pruebas end-to-end nuevas acotadas |
| Docs/plans | Compiler-Plan §8, Runtime-Model, Module-Format, Language-Syntax, CLAUDE.md | Igual, acotado a la fase |

Volumen bruto estimado para A completa: **~13 ficheros de compilador (35–40 métodos) + 8–10 de
runtime/imagen**, además del churn de pruebas. Para D (P1+P2): **~4–6 ficheros del emisor + 2–3 de
imagen/importer**, con el cuerpo borrado intocado.

---

## 6. Cuánto costaría

Calibración de ritmo: los planes recientes del propio repo — retención de metadata genérica (3 pasos,
dos bumps de formato) y tipos de valor (7 fases, formato v8) — se ejecutaron en ráfagas de ~1 fase/día
con test-rojo-primero, documentación y cierre audited. Usamos esa unidad.

| Escenario | Ámbito | Formato | Pruebas | Riesgo | Calendario (ritmo del repo) |
|---|---|---|---|---|---|
| **D-P1** variantes intra-módulo primitivas | emisor + binder pasivo | ninguno | +10–20 end-to-end; pins intactos | Medio (toca hot path del emisor) | **3–5 días** + medición |
| **D-P2** variantes persistentes cross-module | + writer/reader/importer/linker-pending | v9 | round-trips + importer + cross-module | Medio-alto | **+3–5 días** |
| **D-P3** value-kind multi-campo | + ValueTypeLayout en variantes | v9 | auditoría GC nueva (tipo `ValueTypeGcAuditTests`) | Alto | **+3–5 días** |
| **A completa** (todas las construcciones) | 13+8 ficheros, identidad nueva | v9+ | churn amplio (~100 pineadas + matriz) | **Alto** | **3–6 semanas** equivalentes, con alto riesgo de cola de regresiones |
| **B reificación** | motor de sustitución en runtime + tablas precomputadas | v9+/10 | — | **Muy alto** | ≥ A, sin beneficios adicionales para un intérprete |
| **C stencil en carga** | linker + gestión de chunks derivados | ninguno | goldens de carga | Alto | ~A, desplazado a load-time |

**Ganancia esperable** (estimación a partir de la descomposición del bench, a confirmar siempre con
corridas A/B como manda la casa):

- `generics`: desaparece el `SurtrBoxed` del argumento (queda solo la instancia `Box<int>`): de
  2 objetos/~106 B por iteración a 1 objeto/~53 B. Tiempo plausible **10–13 ms** (−40–55 %) con el
  suelo puesto por `allocation` (15 ms asignando un objeto por iteración); bytes **~12 MB** (−60 %).
- Bucles genéricos de iteración: del ratio 17× de `iterator` hacia el 6.5× de `forIn` cuando la
  variante elimina caja de `current` y el cast.
- Construcciones sobre referencias: ganancia marginal (no había caja que quitar) — otro motivo para la
  Opción D en vez de A.

---

## 7. Recomendación

1. **No abandonar el borrado como modelo.** Es una decisión registrada, sus costes residuales están
   medidos y cerrados uno a uno, y lo que quedaría por ganar fuera de los cruces de slot es cero.
   Reificación (B) queda descartada para un intérprete; monomorfización total (A) compra la mitad de
   un microbench al precio de rehacer la identidad del emisor y el formato.
2. **Si el rendimiento de genéricos con primitivos es un objetivo real**, implementar la **Opción D por
   fases**, empezando por P1 y midiendo contra `generics`/`iterator` antes de decidir P2. Su relación
   ganancia/esfuerzo es la mejor de la tabla y es reversible.
3. **Corto plazo y gratis:** documentar la escapatoria existente (value class + `for-in` indexado),
   que ya demuestra 0 bytes frente a 45.8 MB en la misma fuente; y mantener cerradas —con su medida—
   las micro-propuestas (interning, cache virtual, layout inline) salvo cambio de plataforma.
4. **Registrar esta evaluación** como el estado de la decisión §8: el borrado se conserva; la puerta
   de la especialización dirigida queda descrita con su coste, para abrirla solo si aparece el caso.

---

## Anexo: evidencia principal

- Diseño: `docs/Compiler-Plan.md` §8 (decisión a/b/c), `docs/Plan-Genericos-Metadata.md` (metadata
  retenida, formatos 5/6), `docs/Plan-TiposDeValor.md:85` (prohibición multi-campo genérica).
- Erasure en emisor: `DescriptorEmitter.cs:164–193`; `MethodBodyEmitter.cs:5375–5409` (`TypeCodeOf`),
  `:4454–4479` (`Box/UnboxIfStillErased`), `:1677–1711` (`Unerase`), `:561–574` (sin fusión borrada);
  `Conversions.cs:290–321`; `EmitContext.cs:160–192` (un cuerpo por declaración).
- Claves borradas y linker: `SurtrMethodInfo.cs:629,689–699`; `SurtrTypeLinker.cs:173,696,806–834`;
  `ModuleEmitter.cs:1412–1445`.
- VM: `SurtrVirtualMachine.cs:1460–1539` (`Box*`, `Unbox`, `BoxDynamic`, `UnboxDynamic`), `:1162–1176`
  (`DynEQ/DynNE`), `:1541–1587` (`Cast`), `:3009–3107` (despacho); `SurtrEntityRegistry.cs:163–208`;
  `SurtrArray.cs:31–50`; `SurtrDictionary.cs:58–83,133`; `SurtrValueComparer.cs:48–73`.
- Medición: `benchmark_report.md` §5.4–5.5, §8 (`generics` 22.251 ms / 32 MB / 600k objs / 29.5×;
  descomposición 2 objetos ~106 B por iteración; cache virtual retirada ±4 %; techo de despacho ~6 ns);
  `docs/analysis/Registry-GC-Politicas.md` (umbral 10k, +11 % en `generics` bajo auto-GC);
  `docs/analysis/Surtr-Bench-Informe-Rendimiento-Memoria.md` (13 B/slot de registry).
- Superficie de prueba: 102 pruebas con `Generic/Erase` en el nombre sobre 2097 totales;
  `ValueTypeGcAuditTests.cs:471–495` (round-trip multi-campo a través de `Box<T>` bajo GC);
  `ModuleEmitterTests.cs:2542–2553, 4970–4984, 5343–5350`.
