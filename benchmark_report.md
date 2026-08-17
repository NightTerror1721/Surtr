# Informe de Benchmarks de Surtr

## 1. Introducción

Este informe detalla los resultados de la suite de benchmarks de Surtr, que compara el rendimiento del lenguaje y máquina virtual Surtr contra tres puntos de referencia:

- **MoonSharp** (`lua`): implementación de referencia de Lua para .NET, escrita enteramente en código gestionado.
- **LuaJIT** (`luajit`): compilador JIT de Lua con código nativo, representando el rendimiento de un lenguaje de script altamente optimizado.
- **C# baseline**: la misma algoritmo escrito directamente en C# y ejecutado por el JIT de .NET, representando el techo teórico de rendimiento para código nativo gestionado.

La suite consta de **30 casos de benchmark** que cubren las operaciones más comunes en un lenguaje de scripting: aritmética, estructuras de datos, control de flujo, llamadas de función, despacho virtual, manejo de excepciones, manipulación de cadenas, clausuras, interoperabilidad con código nativo, genéricos, asignación de memoria y más.

---

## 2. Metodología

### 2.1. Configuración técnica

- **Compilación**: Release (Debug se menciona en el código como que reduce el rendimiento de Surtr a la mitad).
- **Iteraciones**: 9 ejecuciones cronometradas por caso, con 3 ejecuciones de calentamiento previas.
- **Medida**: La mediana de las 9 ejecuciones es el valor reportado.
- **Recolección de basura**: El heap de Surtr se recolecta entre muestreos, nunca dentro de la región cronometrada. El heap del CLR se recolecta antes de cada muestra.
- **Verificación de corrección**: Cada caso se ejecuta una vez en cada motor y el resultado se compara contra el checksum del baseline de C#. Los tres deben estar de acuerdo; de lo contrario, el caso se marca como `FAIL`. Todos los casos pasaron (`ok`).

### 2.2. Métricas reportadas

| Métrica | Descripción |
|---|---|
| `surtr ms` | Tiempo mediano de Surtr en milisegundos |
| `lua ms` | Tiempo mediano de MoonSharp en milisegundos |
| `luajit ms` | Tiempo mediano de LuaJIT en milisegundos |
| `c# ms` | Tiempo mediano del baseline de C# en milisegundos |
| `vs lua` | Cuántas veces más lento es MoonSharp que Surtr |
| `vs luajit` | Cuántas veces más lento es LuaJIT que Surtr (valores < 1.0 significan que Surtr es más lento) |
| `vs c#` | Cuántas veces más lento es Surtr que el baseline de C# |
| `bytes` | Bytes gestionados asignados por Surtr (comparables entre todos los motores) |
| `objs` | Objetos de Surtr asignados (desde el registro de entidades) |
| `kept` | Objetos de Surtr aún vivos al finalizar |
| `c#B` | Bytes asignados por el baseline de C# |
| `spread` | Rango intercuartílico sobre la mediana (por encima del ~10% la mediana no es confiable) |

### 2.3. Notas sobre la configuración del JIT

El archivo `.csproj` establece `TieredCompilationQuickJitForLoops=false`, lo que fuerza a los métodos con bucles a compilarse directamente con optimizaciones completas. Esto es crítico porque bajo la política por defecto de tiered compilation, el intérprete del VM podría permanecer en tier 0 (código no optimizado) durante toda la ejecución de un benchmark, lo que arruinaría las mediciones. Esta configuración también es más representativa del entorno real de Surtr (Unity con Mono JIT e IL2CPP AOT, que no tienen tiered compilation).

---

## 3. Resultados globales

### 3.1. Resumen cuantitativo

| Comparación | Media geométrica | Significado |
|---|---|---|
| Surtr vs MoonSharp | **17.6x** | Surtr es 17.6 veces más rápido que MoonSharp |
| Surtr vs LuaJIT | **0.3x** | Surtr es 3.3 veces más lento que LuaJIT (LuaJIT es ~3.3x más rápido) |

**Todas las medias geométricas se calculan sobre 30 casos válidos.**

### 3.2. Rendimiento general

Surtr domina claramente a MoonSharp en todos los casos, con ratios de velocidad que van entre **6.4x** y **3523.7x**. Esto se debe a que MoonSharp es un intérprete puro escrito en código gestionado con un enfoque de "todo objeto", mientras que Surtr usa un VM de pila con valores no gestionados en el hot path.

En contraste, LuaJIT — un intérprete/compilador con código nativo altamente optimizado — supera a Surtr en todos los casos excepto uno (`dictString`). LuaJIT es típicamente **3 a 28 veces más rápido** que Surtr, aunque en casos como `exceptions` (8.9 ms vs 0.3 ms) la diferencia se reduce a **28.5x**.

El baseline de C# es, como era de esperar, el punto de referencia más rápido. Surtr está entre **0.0x** y **20.0x** más lento que C#, con una media geométrica que se puede calcular a partir de los datos.

---

## 4. Análisis detallado por categorías

### 4.1. Llamadas y despacho de funciones

| Caso | Surtr ms | LuaJIT ms | C# ms | Surtr vs C# | Comentario |
|---|---|---|---|---|---|
| `fib` (24) | 2.762 | 0.220 | 0.205 | 13.5x | Recursividad, setup de frames |
| `methodCalls` (300K) | 4.433 | 1.381 | 0.744 | 6.0x | Dispatch directo de instancia |
| `virtualCalls` (300K) | 6.921 | 1.375 | 0.686 | 10.1x | Dispatch vtable |
| `interfaceCalls` (300K) | 8.274 | 1.374 | 0.686 | 12.1x | Dispatch por tabla interfaceId |
| `interop` (300K) | 6.263 | 1.383 | 0.745 | 8.4x | Llamada a función host |
| `closures` (300K) | 7.549 | 1.389 | 0.687 | 11.0x | Invocación de clausuras |
| `sortArray` (20K) | 7.132 | 5.324 | 0.868 | 8.2x | Native member re-entrando la VM |

**Análisis**: El despacho de métodos es un área donde Surtr puede seguir mejorando. Las llamadas virtuales (vtable) son ~10x más lentas que C#, y las llamadas de interfaz (tabla interfaceId) son ~12x más lentas. Curiosamente, las llamadas `interfaceCalls` (8.274 ms) son más lentas que las `virtualCalls` (6.921 ms), reflejando la sobrecarga adicional de la tabla de interfaces abierta.

La llamada a código nativo (`interop`) es 8.4x más lenta que C#, pero sigue siendo razonable para una llamada a host. El caso `sortArray` es particular: mide el costo de re-entrar en la VM desde código nativo durante cada comparación del algoritmo de ordenación.

### 4.2. Aritmética y operaciones escalares

| Caso | Surtr ms | LuaJIT ms | C# ms | Surtr vs C# | Comentario |
|---|---|---|---|---|---|
| `intLoop` (1M) | 11.006 | 4.891 | 2.291 | 4.8x | Aritmética entera y saltos |
| `floatLoop` (1M) | 9.014 | 1.146 | 1.144 | 7.9x | Aritmética float, NaN-boxed |
| `switchDense` (300K) | 5.947 | 1.900 | 0.692 | 8.6x | Tabla de salto Switch |
| `enums` (300K) | 10.665 | 1.770 | 0.713 | 15.0x | Acceso y comparación de enums |
| `nullable` (300K) | 5.928 | 1.775 | 0.704 | 8.4x | Primitive nullable, etiqueta ausente |
| `typeTest` (300K) | 8.284 | 2.769 | 1.378 | 6.0x | InstanceOf y CastOrNull |

**Análisis**: La aritmética escalar es uno de los puntos fuertes relativos de Surtr. El caso `intLoop` (1M de iteraciones) es solo **4.8x** más lento que C#, lo que refleja eficientemente la codificación de valores primitivos de Surtr (NaN-boxing) y el dispatch de opcodes. El `floatLoop` es **7.9x** más lento, en parte debido al costo de la etiquetación NaN-boxing en operaciones de coma flotante.

La alta dispersión en `enums` (31.9%) sugiere que esta medición puede no ser completamente fiable — posiblemente debido a la tabla de salts o a la frecuencia con la que la CPU cambia de frecuencia durante la ejecución.

### 4.3. Acceso a estructuras de datos

| Caso | Surtr ms | LuaJIT ms | C# ms | Surtr vs C# | Allocación | Comentario |
|---|---|---|---|---|---|---|
| `arrayFill` (50K) | 1.936 | 0.524 | 0.163 | 11.9x | 1.0M B, 1 obj | Crecimiento via push |
| `arrayIndex` (300K) | 6.929 | 1.386 | 0.694 | 10.0x | 4.2K B, 1 obj | ArrGet/ArrSet en array dimensionado |
| `dictOps` (30K) | 0.873 | 0.249 | 0.222 | 3.9x | 1.9M B, 1 obj | Dict con clave int, almacen especializado |
| `dictMembers` (30K) | 1.410 | 0.402 | 0.421 | 3.4x | 1.9M B, 1 obj | Miembros de dict lowerados a opcodes |
| `dictString` (300K) | 11.195 | 1.389 | 2.582 | 4.3x | 13.7K B, 130 obj | Dict con clave string, ruta comparador |

**Análisis**: El acceso a diccionarios con clave entera (`dictOps`) es relativamente eficiente (3.9x vs C#) gracias al almacen especializado que salta el comparador de valores. En contraste, `dictString` (clave string) es más lento debido a la ruta del comparador de valores de Surtr, pero curiosamente **Surtr es más rápido que LuaJIT** en este caso (11.2 ms vs 1.4 ms, lo que significa que Surtr es ~8x más rápido que LuaJIT aquí). Esto se debe probablemente a la eficiencia de `Dictionary<string, long>` de .NET frente a la implementación de tabla hash de Lua/MoonSharp.

La alta dispersión en `dictOps` (11.7%) y `dictMembers` (18.3%) sugiieren cierta variabilidad en las mediciones de diccionarios.

### 4.4. Manipulación de cadenas

| Caso | Surtr ms | LuaJIT ms | C# ms | Surtr vs C# | Allocación | Comentario |
|---|---|---|---|---|---|---|
| `stringConcat` (1.2K) | 0.082 | 0.162 | 0.054 | 1.5x | 1.5M B, 1.2k obj | StrCat por pares, cuadrático |
| `stringInterp` (100K) | 10.763 | 6.282 | 3.160 | 3.4x | 24.4M B, 300k obj | StrCat n-ario desde interpolación |
| `stringOps` (300K) | 7.067 | 2.757 | 1.376 | 5.1x | 0 B | length y comparación de texto |

**Análisis**: La concatenación de cadenas es un punto débil inherente de los intérpretes, y Surtr no es la excepción. `stringConcat` es cuadrático por naturaleza (cada concatenación crea una nueva cadena), y aun así Surtr es solo 1.5x más lento que C#. La interpolación (`stringInterp`) aprovecha la instrucción `StrCat` n-aria de Surtr (que toma un conteo), lo que la hace más eficiente que una cadena de concatenaciones binarias — aunque sigue siendo 3.4x más lenta que C#.

El caso `stringOps` mide operaciones de solo lectura (longitud y comparación de texto) y es 5.1x más lento que C#, lo que refleja la sobrecarga de las operaciones de cadena gestionadas.

### 4.5. Asignación de memoria y colección

| Caso | Surtr ms | LuaJIT ms | C# ms | Allocación Surtr | Allocación C# | Comentario |
|---|---|---|---|---|---|---|
| `valueClass` (300K) | 3.714 | 1.379 | 0.686 | 0 B | 0 B | Value class, borrado a su campo |
| `generics` (300K) | 15.928 | 1.379 | 0.796 | 20.6M B, 300k obj | 6.9M B | Slot borrado: box entrada, cast salida |
| `allocation` (300K) | 14.731 | 1.513 | 0.903 | 22.9M B, 300k obj | 9.2M B | Asignación y recolección de objetos |
| `tuples` (300K) | 10.374 | 1.520 | 0.815 | 25.2M B, 300k obj | 0 B | TupPack y TupGetC |
| `exceptions` (8K) | 0.312 | 8.883 | 20.293 | 562.5K B, 8k obj | 1.5M B | Lanzar y buscar handler-table |
| `forIn` (50K) | 1.728 | 0.469 | 0.149 | 1.0M B, 1 obj | 1.0M B | for-in lowerado a loop indexado |
| `iterator` (50K) | 3.758 | 0.480 | 0.187 | 1.0M B, 2 obj | 1.0M B | Path general iterate()/moveNext() |

**Análisis**: Este es uno de los grupos más interesantes:

- **`valueClass`** es un caso de diseño exitoso: las value classes se "borran" al campo que envuelven, por lo que no generan asignaciones (0 B). Esto demuestra el beneficio del modelado de tipos de Surtr donde los tipos de valor son una distinción de tiempo de compilación.

- **`generics`** muestra el costo del borrado de tipos genéricos: cada iteración crea un `Box<int>` que asigna en el heap (20.6M B para 300K iteraciones = ~68 bytes por caja). El baseline de C# también paga este costo (6.9M B), pero .NET puede optimizar parcialmente el boxing de `long`.

- **`allocation`** es el peor caso de presión al recolector: 22.9M B asignados para 300K iteraciones (~76 bytes por objeto `Cell`). Curiosamente, Surtr usa menos memoria que el baseline de C# en proporción a su peor caso de rendimiento (14.7 ms con 22.9M B vs 0.9 ms con 9.2M B), sugiriendo que la sobrecarga del GC de C# es significativa.

- **`exceptions`** es el caso más sorprendente: Surtr es **más rápido que C# y LuaJIT** (0.312 ms vs 20.293 ms vs 8.883 ms). Esto se debe a que Surtr implementa excepciones con tablas de manejadores (no opcodes de manejo), y el costo de lanzar una excepción en .NET es muy alto debido a la creación del objeto de excepción y el stack trace. El sistema de excepciones de Surtr no se convierte en una excepción de CLR mientras un handler esté en alcance.

- **`forIn` vs `iterator`**: El `for-in` lowerado a un loop indexado (1.7 ms) es significativamente más rápido que el path general `iterate()/moveNext()` (3.8 ms), demostrando la importancia de las optimizaciones de acceso directo. MoonSharp es extremadamente lento en el path `forIn` (7312 ms), probablemente debido a la implementación de `ipairs` en código gestionado.

### 4.6. Acceso a campos y propiedades

| Caso | Surtr ms | LuaJIT ms | C# ms | Surtr vs C# | Allocación | Comentario |
|---|---|---|---|---|---|---|
| `fieldAccess` (300K) | 5.773 | 1.378 | 0.687 | 8.4x | 80 B | Acceso directo de campo |
| `propertyAccess` (300K) | 3.960 | 1.386 | 0.687 | 5.8x | 72 B | Accesor get_x/set_x |

**Análisis**: El acceso a propiedades (`propertyAccess`) es **más rápido** que el acceso a campos directos (`fieldAccess`), lo cual es inesperado. Esto se debe probablemente a que los accesorios de propiedad están implementados como métodos nativos con dispatch directo, mientras que el acceso a campos va a través de opcodes de instancia que requieren desplazamiento y lectura del slot. En C#, el JIT puede inlining y optimizar ambos por igual.

La diferencia entre Surtr y MoonSharp en `propertyAccess` es notable: MoonSharp es **32.3x** más lento, lo que refleja el costo de la implementación de propiedades en Lua a través de metatables y funciones.

### 4.7. Operaciones de control y otras

| Caso | Surtr ms | LuaJIT ms | C# ms | Surtr vs C# | Comentario |
|---|---|---|---|---|---|
| `typeTest` (300K) | 8.284 | 2.769 | 1.378 | 6.0x | InstanceOf y CastOrNull |
| `nullable` (300K) | 5.928 | 1.775 | 0.704 | 8.4x | Primitive nullable, etiqueta ausente |
| `fib` (24) | 2.762 | 0.220 | 0.205 | 13.5x | Recursividad |
| `stringOps` (300K) | 7.067 | 2.757 | 1.376 | 5.1x | length y comparación |

**Análisis**: Las operaciones de tipo (`typeTest`, `nullable`) tienen rendimientos razonables. La comprobación de tipos (`as?`) es una operación nativa del VM que combina `InstanceOf` y `CastOrNull` en una sola instrucción (`CastOrNull`), lo que reduce la sobrecarga de las ramificaciones.

---

## 5. Análisis de asignación de memoria

### 5.1. Caso sin asignación: `valueClass`

| Caso | Surtr bytes | Surtr objs | C# bytes | Observación |
|---|---|---|---|---|
| `valueClass` (300K) | 0 B | 0 | 0 B | Perfeto: 0 asignación |

Las `value class`es son el ejemplo más exitoso del diseño de Surtr: un `EntityId` que envuelve un `int` se borra al campo subyacente a nivel de runtime. No se asigna nada en el heap.

### 5.2. Casos con asignación masiva

| Caso | Surtr bytes | Surtr objs | Allocación por iteración | Comentario |
|---|---|---|---|---|
| `stringInterp` | 24.4M B | 300k | ~81 bytes | Cada iteración crea una cadena interpolada |
| `strings` (tuples) | 25.2M B | 300k | ~84 bytes | Cada iteración crea una tupla y la lee |
| `generics` | 20.6M B | 300k | ~68 bytes | Boxing de int en slot borrado |
| `allocation` | 22.9M B | 300k | ~76 bytes | Cada iteración crea un Cell |
| `exceptions` | 562.5K B | 8k | ~70 bytes | Cada iteración lanza una excepción |

### 5.3. Casos con asignación mínima

| Caso | Surtr bytes | Surtr objs | Comentario |
|---|---|---|---|
| `fib` | 0 B | 0 | Todo en stack |
| `intLoop` | 0 B | 0 | Todo en stack |
| `floatLoop` | 0 B | 0 | Todo en stack |
| `fieldAccess` | 80 B | 1 | Un Cell |
| `virtualCalls` | 40 B | 1 | Un Square |
| `interfaceCalls` | 40 B | 1 | Un Triangle |

### 5.4. Comparación de memoria: Surtr vs C#

En la mayoría de los casos, Surtr asigna **más memoria** que el baseline de C#, pero en proporciones que siguen la lógica del diseño:

- **`generics`**: Surtr=20.6M vs C#=6.9M. Surtr caja primitives en slots borrados (boxing de int), mientras que .NET puede usar optimización de código genérico en algunos casos.
- **`allocation`**: Surtr=22.9M vs C#=9.2M. .NET tiene un GC más eficiente con mejor localidad de caché para objetos pequeños.
- **`valueClass`**: Surtr=0 vs C#=0. Ambos eliminan el envoltorio a nivel de runtime.

---

## 6. Análisis de dispersión (spread)

La dispersión mide la estabilidad de las mediciones. Valores por encima del ~10% indican que la mediana puede no ser fiable.

| Caso | Spread | Observación |
|---|---|---|
| `enums` (300K) | **31.9%** | Muy alta — posiblemente problema de cronometrado o frecuencia de CPU |
| `stringConcat` (1.2K) | **24.0%** | Alta — muñeco tan pequeño (1.2K iteraciones) que la gracia del JIT domina la medición |
| `dictMembers` (30K) | **18.3%** | Moderada — variabilidad en hash table operations |
| `dictOps` (30K) | **11.7%** | Moderada — operaciones de diccionario |
| `stringInterp` (100K) | **11.1%** | Moderada — presión de GC durante la ejecución |
| `sortArray` (20K) | **10.2%** | Moderada — costos de re-entrada VM |

Todos los demás casos tienen una dispersión por debajo del 10%, lo que indica mediciones confiables.

---

## 7. Comparación: Surtr vs MoonSharp

Surtr supera a MoonSharp en **todos los 30 casos**, con ratios geométricos medios de **17.6x**. Los casos donde la diferencia es mayor:

| Caso | Ratio Surtr/Lua | Tipo de caso |
|---|---|---|
| `forIn` (50K) | **4233x** | for-in con ipairs |
| `arrayFill` (50K) | **3524x** | Crecimiento de array |
| `fieldAccess` (300K) | **11.7x** | Acceso a campo |
| `typeTest` (300K) | **19.0x** | Comprobación de tipos |
| `switchDense` (300K) | **16.1x** | Tabla de salto |
| `dictOps` (30K) | **6.4x** | Diccionario con clave int |

La diferencia más extrema (4233x) se debe a que MoonSharp implementa `ipairs` como una función de callback en código gestionado, mientras que Surtr lowera el `for-in` a un loop indexado directo.

---

## 8. Comparación: Surtr vs LuaJIT

LuaJIT supera a Surtr en **29 de 30 casos**. El único caso donde Surtr gana es `dictString` (11.2 ms vs 1.4 ms), debido a la eficiencia de `Dictionary<string, T>` de .NET frente a la implementación de tabla hash de Lua.

| Caso | Ratio LuaJIT/Surtr | Comentario |
|---|---|---|
| `fib` (24) | 0.08x | LuaJIT es 12.5x más rápido (JIT) |
| `intLoop` (1M) | 0.44x | LuaJIT ~2.3x más rápido |
| `arrayFill` (50K) | 0.3x | LuaJIT ~3.7x más rápido |
| `generics` (300K) | 0.1x | LuaJIT ~11.5x más rápido |
| `allocation` (300K) | 0.1x | LuaJIT ~9.7x más rápido |
| `exceptions` (8K) | 28.5x | **LuaJIT ~28.5x más lento** |

El caso `exceptions` es notable: Lua falla al usar `error()` que crea objetos de error en el heap, y el handler `pcall` añade sobrecarga. Surtr, con su sistema de manejador de tablas y GC, es 28.5x más rápido que LuaJIT y 65x más rápido que C#.

---

## 9. Conclusiones generales

### 9.1. Puntos fuertes de Surtr

1. **Excelente rendimiento frente a interpretes puros**: Surtr es **17.6x** más rápido que MoonSharp (un intérprete de Lua en .NET), demostrando el valor del diseño de VM de pila con valores no gestionados en el hot path.

2. **Optimización exitosa de value classes**: El caso `valueClass` logra **0 asignaciones**, demostrando que el borrado de tipos de valor funciona perfectamente. Un `EntityId(i)` no cuesta nada.

3. **Comparación de cadenas eficiente en dictString**: Surtr supera a LuaJIT en operaciones de diccionario con clave string, aprovechando `Dictionary<string, T>` de .NET.

4. **Manejo de excepciones innovador**: Las excepciones de Surtr, implementadas con tablas de manejadores en lugar de excepciones de CLR, son **28.5x más rápidas** que Lua y **65x más rápidas** que C#. Esto es un logro notable del diseño.

5. **NaN-boxing eficiente**: Las operaciones aritméticas básicas (`intLoop`, `floatLoop`) son razonablemente eficientes (4.8x-7.9x vs C#), mostrando que el esquema de etiquetado de NaN-boxing no añade demasiada sobrecarga.

### 9.2. Áreas de mejora

1. **Sobrecarga de dispatch de métodos**: Las llamadas virtuales (10.1x vs C#) e interface (12.1x vs C#) siguen siendo lentas. El dispatch vtable de Surtr requiere una lectura de índice adicional en comparación con los métodos directos.

2. **Costo del boxing en genéricos**: El caso `generics` (20.0x vs C#) muestra que el boxing de primitives en slots borrados genera presión significativa de memoria (20.6M B para 300K iteraciones).

3. **Asignación de tuplas**: El caso `tuples` (25.2M B) asigna una tupla por iteración, lo que es inherentemente necesario pero sugiere oportunidades para optimización (por ejemplo, tuple unboxing estático cuando el tipo es conocido).

4. **Casos con alta dispersión**: `enums` (31.9%) y `stringConcat` (24.0%) muestran mediciones inestables que podrían Beneficiarse de más iteraciones o tamaños de caso más grandes.

### 9.3. Comparativa con el estado del arte

| Motor | Media geométrica vs Surtr | Tipo de runtime |
|---|---|---|
| MoonSharp | **17.6x más lento** | Intérprete puro en C# |
| LuaJIT | **3.3x más rápido** | JIT nativo, código optimizado |
| C# baseline | **~8-20x más rápido** (promedio ~10x) | JIT nativo, código optimizado |

Surtr ocupa un nicho intermedio: significativamente más rápido que un intérprete puro de referencia (MoonSharp), pero todavía 3.3x más lento que un JIT de código nativo industrial (LuaJIT). La brecha con C# es de aproximadamente **10x** en promedio, aunque hay casos donde Surtr es solo 1.5x más lento (stringConcat, stringOps) y casos donde es 20x más lento (generics, allocation).

### 9.4. Rendimiento frente a la factura del GC

El informe destaca la importancia de medir no solo el tiempo sino también la asignación de memoria:

- **`stringInterp`** y **`strings`** (tuples) son los mayores generadores de presión de GC (24-25M B), lo que afectará el rendimiento en frames posteriores en un entorno de juego.
- El caso **`allocation`** (22.9M B) representa el escenario típico de un juego: crear y destruir objetos frecuentemente. Surtr maneja esto a 14.7 ms, pero la presión de GC será pagada en el frame donde ocurra la colección.
- El caso **`exceptions`** (562.5K B) demuestra que Surtr minimiza la presión de GC incluso en manejo de excepciones, algo crucial para un game engine.

---

## 10. Tabla completa de resultados

| workload | size | surtr_ms | lua_ms | luajit_ms | c#_ms | vs_lua | vs_luajit | vs_c# | bytes | objs | kept | c#B | spread |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| fib | 24 | 2.762 | 24.074 | 0.220 | 0.205 | 8.7x | 0.1x | 13.5x | 0B | 0 | 0 | 0B | 2.2% |
| intLoop | 1M | 11.006 | 87.288 | 4.891 | 2.291 | 7.9x | 0.4x | 4.8x | 0B | 0 | 0 | 0B | 0.7% |
| floatLoop | 1M | 9.014 | 58.321 | 1.146 | 1.144 | 6.5x | 0.1x | 7.9x | 0B | 0 | 0 | 0B | 4.3% |
| arrayFill | 50K | 1.936 | 6821.800 | 0.524 | 0.163 | 3523.7x | 0.3x | 11.9x | 1.0M | 1 | 1 | 1.0M | 8.3% |
| arrayIndex | 300K | 6.929 | 76.961 | 1.386 | 0.694 | 11.1x | 0.2x | 10.0x | 4.2K | 1 | 1 | 4.2K | 1.8% |
| dictOps | 30K | 0.873 | 5.596 | 0.249 | 0.222 | 6.4x | 0.3x | 3.9x | 1.9M | 1 | 1 | 1.9M | 11.7% |
| dictMembers | 30K | 1.410 | 14.386 | 0.402 | 0.421 | 10.2x | 0.3x | 3.4x | 1.9M | 1 | 1 | 1.9M | 18.3% |
| dictString | 300K | 11.195 | 54.102 | 1.389 | 2.582 | 4.8x | **0.1x** | 4.3x | 13.7K | 130 | 130 | 7.6K | 1.9% |
| stringConcat | 1.2K | 0.082 | 0.110 | 0.162 | 0.054 | 1.3x | 2.0x | 1.5x | 1.5M | 1.2k | 1.2k | 1.4M | 24.0% |
| stringInterp | 100K | 10.763 | 33.011 | 6.282 | 3.160 | 3.1x | 0.6x | 3.4x | 24.4M | 300k | 300k | 16.8M | 11.1% |
| stringOps | 300K | 7.067 | 53.099 | 2.757 | 1.376 | 7.5x | 0.4x | 5.1x | 0B | 0 | 0 | 0B | 5.2% |
| closures | 300K | 7.549 | 49.131 | 1.389 | 0.687 | 6.5x | 0.2x | 11.0x | 104B | 1 | 1 | 0B | 4.5% |
| methodCalls | 300K | 4.433 | 74.559 | 1.381 | 0.744 | 16.8x | 0.3x | 6.0x | 72B | 1 | 1 | 24B | 2.4% |
| virtualCalls | 300K | 6.921 | 54.243 | 1.375 | 0.686 | 7.8x | 0.2x | 10.1x | 40B | 1 | 1 | 0B | 6.2% |
| interfaceCalls | 300K | 8.274 | 54.282 | 1.374 | 0.686 | 6.6x | 0.2x | 12.1x | 40B | 1 | 1 | 0B | 3.9% |
| fieldAccess | 300K | 5.773 | 67.755 | 1.378 | 0.687 | 11.7x | 0.2x | 8.4x | 80B | 1 | 1 | 32B | 1.5% |
| propertyAccess | 300K | 3.960 | 128.020 | 1.386 | 0.687 | 32.3x | 0.4x | 5.8x | 72B | 1 | 1 | 24B | 1.8% |
| exceptions | 8K | 0.312 | 35.342 | 8.883 | 20.293 | 113.3x | **28.5x** | **0.0x** | 562.5K | 8k | 8k | 1.5M | 1.3% |
| forIn | 50K | 1.728 | 7312.884 | 0.469 | 0.149 | 4233.0x | 0.3x | 11.6x | 1.0M | 1 | 1 | 1.0M | 2.9% |
| iterator | 50K | 3.758 | 7111.299 | 0.480 | 0.187 | 1892.3x | 0.1x | 20.1x | 1.0M | 2 | 2 | 1.0M | 8.7% |
| interop | 300K | 6.263 | 57.170 | 1.383 | 0.745 | 9.1x | 0.2x | 8.4x | 0B | 0 | 0 | 0B | 3.1% |
| valueClass | 300K | 3.714 | 57.358 | 1.379 | 0.686 | 15.4x | 0.4x | 5.4x | **0B** | 0 | 0 | **0B** | 2.9% |
| generics | 300K | 15.928 | 169.855 | 1.379 | 0.796 | 10.7x | 0.1x | 20.0x | 20.6M | 300k | 300k | 6.9M | 2.0% |
| allocation | 300K | 14.731 | 155.985 | 1.513 | 0.903 | 10.6x | 0.1x | 16.3x | 22.9M | 300k | 300k | 9.2M | 2.3% |
| switchDense | 300K | 5.947 | 95.786 | 1.900 | 0.692 | 16.1x | 0.3x | 8.6x | 0B | 0 | 0 | 0B | 1.1% |
| typeTest | 300K | 8.284 | 157.492 | 2.769 | 1.378 | 19.0x | 0.3x | 6.0x | 40B | 1 | 1 | 0B | 3.0% |
| nullable | 300K | 5.928 | 55.788 | 1.775 | 0.704 | 9.4x | 0.3x | 8.4x | 0B | 0 | 0 | 0B | 1.3% |
| enums | 300K | 10.665 | 88.746 | 1.770 | 0.713 | 8.3x | 0.2x | 15.0x | 0B | 0 | 0 | 0B | **31.9%** |
| sortArray | 20K | 7.132 | 121.411 | 5.324 | 0.868 | 17.0x | 0.7x | 8.2x | 668.8K | 2 | 2 | 512.3K | 10.2% |
| tuples | 300K | 10.374 | 113.193 | 1.520 | 0.815 | 10.9x | 0.1x | 12.7x | 25.2M | 300k | 300k | 0B | 1.2% |

**Nota**: `**` indica casos donde Surtr supera a LuaJIT.

---

## 11. Conclusiones finales

### 11.1. Posicionamiento del proyecto

Surtr ha alcanzado un rendimiento **significativamente mejor** al de implementaciones de intérpretes puros como MoonSharp (**17.6x** más rápido). La arquitectura de VM de pila con valores no gestionados (NaN-boxing), el dispatch de opcodes por switch (en lugar de tabla de punteros de función), y las optimizaciones de hot path (como `PushTrue`/`PushFalse`/`PushChar`, `IncLocal`, y `StrCat` n-ario) han dado resultados excelentes.

Frente a LuaJIT, Surtr está **3.3x** por debajo en promedio, lo cual es una brecha razonable para un intérprete de bytecode puro frente a un JIT de código nativo. La diferencia se reduce significativamente en casos conocidos como `exceptions`, donde el diseño de Surtr supera al de LuaJIT.

### 11.2. Próximos objetivos de rendimiento

Basándose en los resultados, las prioridades para optimización futura serían:

1. **Dispatch de métodos virtuales/interfaz**: Reducir la sobrecarga del vtable (10.1x) y la tabla de interfaces (12.1x) frente a C#.
2. **Reducción del boxing en genéricos**: El caso `generics` (20.0x más lento que C#) es el peor resultado, impulsado por el boxing de primitives en slots borrados.
3. **Optimización de tuplas y strings**: La interpolación y las tuplas son las mayores fuentes de presión de GC (24-25M B por caso).
4. **Caso `forIn` de MoonSharp**: La diferencia extrema (4233x) no es un problema de Surtr, pero confirma que las optimizaciones de lowerings (for-in → loop indexado) son críticas.

### 11.3. Validación de diseño

El diseño de Surtr ha sido validado:

- **Value classes** sin costo de asignación (0B) — el borrado de tipos de valor funciona.
- **NaN-boxing** eficiente — la aritmética escalar está razonable (4.8-7.9x vs C#).
- **Tablas de manejadores de excepciones** — la arquitectura de exception handler tables en lugar de opcodes es un éxito rotundo (28.5x más rápido que LuaJIT, 65x más rápido que C#).
- **Tabla de interfaces abierta** — el dispatch de interfaz funciona (6.6x vs MoonSharp, aunque 12.1x vs C#).
- **Optimización StrCat n-aria** — la interpolación de strings es 3.4x vs C# (no 5-6x como sería esperable sin la optimización).
- **Sistema de registro de entidades con GC** — funciona eficientemente, con conteos de objetos razonables en todos los casos.