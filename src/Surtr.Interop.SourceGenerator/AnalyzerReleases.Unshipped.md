; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SURTRINTEROP001 | Surtr.Interop | Warning | Member not exposed to Surtr
SURTRINTEROP002 | Surtr.Interop | Error | Invalid Surtr descriptor
SURTRINTEROP003 | Surtr.Interop | Error | Generic arity mismatch
SURTRINTEROP004 | Surtr.Interop | Error | Static type cannot be a native type