# Informes de análisis — Surtr.Core

> **Fecha:** 2026-08-22
> **Objetivo:** investigar el runtime de Surtr (rendimiento, memoria, delegación a lo no administrado), su registry (mini-recolector) y su superficie de interop con el host, y proponer la automatización de la conexión host ↔ Surtr vía atributos + source generators.

## Documentos

| Documento | Contenido |
|---|---|
| [`Runtime-Analisis-Rendimiento-Memoria.md`](Runtime-Analisis-Rendimiento-Memoria.md) | Estado actual del runtime: qué es rápido, qué es lento, qué memoria gasta. Cuellos de botella priorizados (buffers unmanaged, GC automático, diccionario propio, inline caches...). Qué es ya no administrado y qué candidatos quedan. |
| [`Registry-GC-Politicas.md`](Registry-GC-Politicas.md) | Cómo funciona el registry como mini-recolector (mark-sweep generacional por edades), por qué hoy solo se recolecta manualmente, y la propuesta de automatización con un sistema de políticas `SurtrGcPolicy` (modo, umbral de asignaciones, nursery vs full, safepoints, exposición al host). |
| [`Puente-Nativo-Tercer-Tipo.md`](Puente-Nativo-Tercer-Tipo.md) | Los dos mecanismos actuales de enlace nativo (puntero a función `&Método` y delegado `FromDelegate`), el modelo de vtable/abstractos, y el análisis de viabilidad del **tercer tipo de función nativa** (método abstracto con implementación aportada por una clase host), con tres variantes y recomendación. |
| [`Interop-Atributos-SourceGenerators.md`](Interop-Atributos-SourceGenerators.md) | **Dos diseños** de arquitectura para el sistema de atributos + source generators que automatiza la conexión de clases/structs del host con Surtr: *A) Generación de registro declarativo* y *B) Metadatos + reflexión de alta*, con ventajas, inconvenientes, comparación y recomendación (A para producción, B como modo editor). |

## Relación entre los documentos

```
Runtime (rendimiento/memoria)
   └─ el registry es parte del runtime → GC + políticas
Puente nativo (cómo se enlaza código host hoy)
   └─ el tercer tipo añade "método abstracto con implementación"
Interop (atributos + source generators)
   └─ automatiza la conexión de tipos, y materializa el tercer tipo
```

## Preguntas que el análisis responde

1. **¿Qué hace al runtime rápido/lento y qué memoria gasta?** → `Runtime-Analisis-Rendimiento-Memoria.md`
2. **¿Cómo se podría integrar el recolector y automatizarlo con políticas?** → `Registry-GC-Politicas.md`
3. **¿Es viable un tercer tipo de función nativa (método abstracto con implementación)?** → `Puente-Nativo-Tercer-Tipo.md` — sí, sin tocar el intérprete.
4. **¿Qué dos diseños de atributos + source generators convienen y cuáles son sus pros/contras?** → `Interop-Atributos-SourceGenerators.md`