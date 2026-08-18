# Builds the Surtr standard library: compiles every .surtr source under
# src/Surtr.Stdlib/src/surtr to a .surtrc image (plus disassemblies and the
# native-link-names manifest) in src/Surtr.Stdlib/build.
#
# This is the same invocation the BuildStdlibImages MSBuild target in
# src/Surtr.Stdlib/Surtr.Stdlib.csproj runs on every build of Surtr.Stdlib.
# The difference: this script does NOT embed the images into Surtr.Stdlib.dll
# as EmbeddedResource -- only the MSBuild target does that. If you need the
# embedding, build the project/solution instead:
#
#   dotnet build src/Surtr.Stdlib/Surtr.Stdlib.csproj
#
# Usage:
#   .\build-stdlib.ps1                    # defaults, Debug configuration
#   .\build-stdlib.ps1 -Configuration Release
#   .\build-stdlib.ps1 -SourceRoot <path> -Output <path> -Disassembly <path>

[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [string]$SourceRoot,
    [string]$Output,
    [string]$Disassembly
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

$toolProject = Join-Path $repoRoot 'src\Surtr.Stdlib.Tool\Surtr.Stdlib.Tool.csproj'

if (-not $SourceRoot)  { $SourceRoot = Join-Path $repoRoot 'src\Surtr.Stdlib\src' }
if (-not $Output)      { $Output = Join-Path $repoRoot 'src\Surtr.Stdlib\build' }
if (-not $Disassembly) { $Disassembly = Join-Path $repoRoot 'src\Surtr.Stdlib\disasm' }

Write-Host "Building Surtr.Stdlib.Tool ($Configuration) and compiling the stdlib..."
Write-Host "  source : $SourceRoot"
Write-Host "  output : $Output"
Write-Host "  disasm : $Disassembly"

dotnet run --project $toolProject --configuration $Configuration -- $SourceRoot $Output $Disassembly

exit $LASTEXITCODE