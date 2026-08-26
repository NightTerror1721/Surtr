# ab-suite.ps1 — A/B de la suite completa entre dos árboles de Surtr, controlado contra la
# bimodalidad por proceso del intérprete.
#
# Por qué existe este script:
#
#   SurtrVirtualMachine.Run() despacha por un único salto indirecto (jmp [tabla]). El rendimiento
#   de ese salto depende de la dirección absoluta del código, que el ASLR re-rolla en cada
#   lanzamiento de proceso. Eso produce estados bimodales: el MISMO binario da, p.ej., arrayIndex a
#   ~4.1 ms o a ~6.15 ms según el proceso que lo lance. El protocolo clásico (una invocación por
#   lado, --rounds 3) muestrea UN solo estado por lado, y cuando los dos lados caen en estados
#   distintos se reporta un delta de ±20-45 % que no tiene nada que ver con el cambio que se
#   mide. Ver docs/Informe-Volatilidad-Run.md.
#
#   Este script corrige eso: lanza cada lado K veces como procesos independientes, intercalados
#   (A,B,A,B,...), y compara distribuciones en lugar de medianas de un solo proceso. La métrica
#   primaria es el MÍNIMO por proceso (el estado rápido, inmune a la probabilidad bimodal); la
#   mediana se reporta aparte porque un cambio de build puede mover la probabilidad de estado sin
#   significar nada por sí misma. Reporta por caso: min A/B, mediana A/B, control de C# y un
#   marcador de bimodalidad.
#
# Uso:
#
#   .\scripts\ab-suite.ps1 -RefA <commit> -RefB <path|commit> [-Runs 7] [-Shuffle] [-Rounds 3] [-Iters 15] [-Warmup 5] [-Workload <substring>] [-Smoke]
#
#   -RefA   ref de git para el lado A (se crea un worktree temporal; el baseline).
#   -RefB   ref de git (worktree temporal) O una ruta ya desplegada (p.ej. el árbol principal con
#           un cambio sin commitear). Es el lado bajo prueba.
#   -Runs   lanzamientos de proceso por lado (default 7). Intercalados.
#   -Smoke  reduce a 3 lanzamientos por lado, 1 ronda, 9 iteraciones: solo para comprobar que el
#           harness funciona o para barrer candidatos a gran velocidad. No citar los números.
#   -Workload  substring que filtra los casos (repetible no; usa comas).
#
# Salidas:
#   - Tabla por caso en consola: mediana A, mediana B, delta %, control de C#, banderas BIMODAL.
#   - Un resumen: mediana de los deltas, casos mejorados/empeorados >5 %, peor control de C#.
#   - CsvPath opcional para volcar la comparación.
#
# Convenciones del benchmark (src/Surtr.Bench): Release siempre, --surtr-only, y cada invocación
# escribe UNA fila por workload con la mediana de sus rondas (las rondas son dentro del proceso;
# por eso K procesos son lo que muestrea la bimodalidad).

param(
    [Parameter(Mandatory = $true)][string]$RefA,
    [Parameter(Mandatory = $true)][string]$RefB,
    [int]$Runs = 7,
    [switch]$Shuffle,
    [int]$Rounds = 3,
    [int]$Iters = 15,
    [int]$Warmup = 5,
    [string]$Workload = "",
    [switch]$Smoke,
    [string]$CsvPath = "",
    [string]$OutDir = ""
)

# PS 5.1 trata el stderr de los ejecutables nativos (git, dotnet) como errores si EAP=Stop;
# aquí se comprueba $LASTEXITCODE explícitamente y el stderr se descarta.
$ErrorActionPreference = "Continue"

if ($Smoke) { $Runs = 3; $Rounds = 1; $Iters = 9; $Warmup = 3 }

if ($OutDir -eq "") { $OutDir = Join-Path $env:TEMP "surtr-ab" }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# El bench AGREGARÁ a sus CSV (append), así que los run-*.csv de una invocación anterior contaminan
# la siguiente si no se borran. OutDir limpio por invocación.
Get-ChildItem (Join-Path $OutDir "run-*.csv") -ErrorAction SilentlyContinue | Remove-Item -Force

# --- Resolver los dos lados a rutas de build ------------------------------------------------
# -RefB puede ser una ruta (árbol ya desplegado) o una ref de git (se crea worktree).
function Resolve-Side {
    param([string]$Spec, [string]$Label)
    if (Test-Path -LiteralPath $Spec) {
        # Es una ruta existente; asumimos que es el árbol ya desplegado.
        return @{ Path = $Spec; Worktree = $false }
    }
    $dir = Join-Path $OutDir "side-$Label"
    if (-not (Test-Path $dir)) {
        cmd /c "git worktree add `"$dir`" $Spec 2>nul" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "git worktree add falló para $Spec" }
    }
    return @{ Path = $dir; Worktree = $true }
}

$sideA = Resolve-Side -Spec $RefA -Label "A"
$sideB = Resolve-Side -Spec $RefB -Label "B"

$exeA = Join-Path $sideA.Path "src\Surtr.Bench\bin\Release\net8.0\surtrbench.exe"
$exeB = Join-Path $sideB.Path "src\Surtr.Bench\bin\Release\net8.0\surtrbench.exe"

Write-Host "== Construyendo lado A ($RefA) en $($sideA.Path)"
Push-Location $sideA.Path
try { dotnet build src/Surtr.Bench/Surtr.Bench.csproj -c Release -v q --nologo 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { throw "build A falló" } } finally { Pop-Location }

Write-Host "== Construyendo lado B ($RefB) en $($sideB.Path)"
Push-Location $sideB.Path
try { dotnet build src/Surtr.Bench/Surtr.Bench.csproj -c Release -v q --nologo 2>&1 | Out-Null; if ($LASTEXITCODE -ne 0) { throw "build B falló" } } finally { Pop-Location }

if (-not (Test-Path $exeA)) { throw "No está el ejecutable A: $exeA" }
if (-not (Test-Path $exeB)) { throw "No está el ejecutable B: $exeB" }

# --- Correr la suite: K procesos por lado, intercalados ---------------------------------------
$args = @("--surtr-only", "--rounds", "$Rounds", "--iters", "$Iters", "--warmup", "$Warmup")
if ($Shuffle) { $args += "--shuffle" }
if ($Workload -ne "") {
    foreach ($w in ($Workload -split ',')) { if ($w.Trim() -ne "") { $args += "--workload"; $args += $w.Trim() } }
}

$i = 0
for ($k = 1; $k -le $Runs; $k++) {
    foreach ($side in @('A', 'B')) {
        $i++
        $exe = if ($side -eq 'A') { $exeA } else { $exeB }
        $csv = Join-Path $OutDir "run-$side-$k.csv"
        Write-Host "  corrida $i/$($Runs * 2): $side"
        & $exe @args --csv $csv 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "falló la corrida $side #$k (exit $LASTEXITCODE)" }
    }
}

# --- Agregar: por caso, K muestras por lado ----------------------------------------------------
function Get-Rows($dir, $side) {
    $rows = @()
    Get-ChildItem (Join-Path $dir "run-$side-*.csv") | ForEach-Object {
        $rows += Get-Content $_.FullName | Where-Object { $_ -and $_ -notmatch '^#' } | ConvertFrom-Csv
    }
    return $rows
}
function Median($arr) { $s = @($arr) | Sort-Object; return $s[[int](($s.Count - 1) / 2)] }

$rowsA = Get-Rows $OutDir 'A'
$rowsB = Get-Rows $OutDir 'B'

# Un workload puede aparecer más de una vez por invocación (variantes); los agregamos por (workload).
$cases = @{}
foreach ($r in $rowsA) {
    if (-not $cases.ContainsKey($r.workload)) { $cases[$r.workload] = @{} }
    $cases[$r.workload].A = @($cases[$r.workload].A) + @($r.surtr_ms)
    $cases[$r.workload].Ac = @($cases[$r.workload].Ac) + @($r.csharp_ms)
}
foreach ($r in $rowsB) {
    if (-not $cases.ContainsKey($r.workload)) { $cases[$r.workload] = @{} }
    $cases[$r.workload].B = @($cases[$r.workload].B) + @($r.surtr_ms)
    $cases[$r.workload].Bc = @($cases[$r.workload].Bc) + @($r.csharp_ms)
}

function Flag-Bimodal($vals) {
    # ¿Los valores forman dos nubes separadas por más del 15 % de la nube baja?
    $s = @($vals | Sort-Object)
    if ($s.Count -lt 4) { return $false }
    for ($i = 1; $i -lt $s.Count - 1; $i++) {
        $lo = $s[0..($i - 1)]
        $hi = $s[$i..($s.Count - 1)]
        $loMed = [double](Median $lo)
        $hiMed = [double](Median $hi)
        if ($loMed -gt 0 -and ($hiMed - $loMed) / $loMed -gt 0.15 -and $lo.Count -ge 2 -and $hi.Count -ge 2) {
            return $true
        }
    }
    return $false
}

$report = @()
$minDeltas = @()
$worstCsharp = 0.0
foreach ($name in ($cases.Keys | Sort-Object)) {
    $c = $cases[$name]
    # El mínimo por proceso es el estimador del estado rápido: la mediana queda contaminada por la
    # probabilidad del estado bimodal, que cambia entre builds sin significar nada por sí misma.
    $mina = [double](($c.A | Measure-Object -Minimum).Minimum)
    $minb = [double](($c.B | Measure-Object -Minimum).Minimum)
    $ma = [double](Median $c.A); $mb = [double](Median $c.B)
    $mca = [double](Median $c.Ac); $mcb = [double](Median $c.Bc)
    $csharpDelta = if ($mca -gt 0) { [math]::Abs(($mcb - $mca) / $mca * 100) } else { 0 }
    if ($csharpDelta -gt $worstCsharp) { $worstCsharp = $csharpDelta }
    $delta = if ($mina -gt 0) { ($minb - $mina) / $mina * 100 } else { 0 }
    $minDeltas += $delta
    $flag = @()
    if (Flag-Bimodal $c.A) { $flag += "BIMODAL-A" }
    if (Flag-Bimodal $c.B) { $flag += "BIMODAL-B" }
    $report += [pscustomobject]@{
        workload   = $name
        minA_ms    = [math]::Round($mina, 3)
        minB_ms    = [math]::Round($minb, 3)
        min_delta  = [math]::Round($delta, 1)
        medA_ms    = [math]::Round($ma, 3)
        medB_ms    = [math]::Round($mb, 3)
        csharp_dp  = [math]::Round($csharpDelta, 1)
        flags      = ($flag -join " ")
    }
}

$report | Sort-Object min_delta | Format-Table -AutoSize

$med = [math]::Round((Median $minDeltas), 2)
$better = ($report | Where-Object { $_.min_delta -lt -5 }).Count
$worse = ($report | Where-Object { $_.min_delta -gt 5 }).Count
Write-Host ""
Write-Host "== Resumen sobre el estado rapido (min por proceso, $($report.Count) casos): mediana $med %   mejoran >5%: $better   empeoran >5%: $worse"
Write-Host "== Peor control de C# (debe ser <~1 %; si no, la corrida no vale): $([math]::Round($worstCsharp,1)) %"

if ($CsvPath -ne "") {
    $report | ConvertTo-Csv -NoTypeInformation | Set-Content $CsvPath -Encoding ascii
    Write-Host "== Comparación escrita en $CsvPath"
}