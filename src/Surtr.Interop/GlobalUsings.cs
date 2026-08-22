// Surtr.Core's value aliases are global usings there and do not flow across assembly boundaries,
// so Surtr.Interop re-declares the ones it needs. SurtrRef is the currency of enum caches here.
global using SurtrRef = System.Int32;
