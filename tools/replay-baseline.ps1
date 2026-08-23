<#
.SYNOPSIS
  Run a replay fixture through the real engine, summarise the selector's behaviour, and diff it
  against a stored baseline. Catches selector/engine drift without a room full of people.

.DESCRIPTION
  Launches AudioMixer with --replay over a labelled segment, polls /state, and reduces the samples to
  aggregates. It deliberately does NOT compare raw sample-by-sample state: the automix tick (~100 Hz)
  and the replay pump run on independent timers, so two runs of the same fixture differ by a few
  milliseconds of phase and an exact diff would be permanently flaky. The aggregates below are stable
  across runs but move immediately when selection logic actually changes.

  Compared per fixture:
    winner occupancy    % of samples each mic held the bus, per output   (tolerance: -Tolerance pts)
    hand-offs           number of winner changes                         (tolerance: 20% or 2)
    per-mic cv          median flux-CV, slow-moving and stable           (tolerance: 0.05)
    per-mic env         median level -- recorded for context, loose      (tolerance: 4 dB)

  IMPORTANT: record and check a baseline at the SAME -Speed. The automix tick is driven off the replay
  clock so the selector itself is speed-independent, but above about -Speed 2 the process starts
  saturating: /state polls get starved and the rig's catch-up cap begins dropping audio, which does
  move the numbers. Speed 1-2 is the reliable range.

.EXAMPLE
  # Record a baseline for the singing segment, then check against it later
  ./tools/replay-baseline.ps1 -Name singing -Stamp 20260809-092931 -Seek 95 -For 170 -Update
  ./tools/replay-baseline.ps1 -Name singing -Stamp 20260809-092931 -Seek 95 -For 170

.EXAMPLE
  # Faster than real time for a batch check
  ./tools/replay-baseline.ps1 -Name prayer -Stamp 20260726-093232 -Seek 0 -For 300 -Speed 4
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Name,
    [string]$Stamp,
    [double]$Seek = 0,
    [double]$For = 60,
    [double]$Speed = 1,
    [int]$Port = 7099,
    [string]$Exe,
    [double]$Tolerance = 8,
    [switch]$Update
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path $root 'AudioMixer\bin\Debug\net8.0-windows\AudioMixer.exe' }
if (-not (Test-Path $Exe)) { throw "AudioMixer.exe not found at $Exe (build first, or pass -Exe)" }

$baselineDir = Join-Path $PSScriptRoot 'baselines'
if (-not (Test-Path $baselineDir)) { New-Item -ItemType Directory -Path $baselineDir | Out-Null }
$baselinePath = Join-Path $baselineDir "$Name.json"

# --- run the fixture ----------------------------------------------------------------------------
# --advanced is explicit, not inherited: the goldens were recorded under the Advanced window, and a
# fixture must not change its UI load just because the app's default window changed.
$args = @("--replay$(if ($Stamp) { "=$Stamp" })", "--seek=$Seek", "--for=$For", "--speed=$Speed", "--state=$Port", "--advanced")
Write-Host "Running fixture '$Name': $($args -join ' ')" -ForegroundColor Cyan
$proc = Start-Process -FilePath $Exe -ArgumentList $args -PassThru

$samples = @()
$deadline = (Get-Date).AddSeconds(($For / $Speed) + 30)
try {
    while (-not $proc.HasExited -and (Get-Date) -lt $deadline) {
        try {
            $s = (Invoke-WebRequest -Uri "http://127.0.0.1:$Port/state" -UseBasicParsing -TimeoutSec 3).Content | ConvertFrom-Json
            if ($null -ne $s.replay) { $samples += $s }
        } catch { }
        Start-Sleep -Milliseconds 50
    }
} finally {
    if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(5000) }
}

if ($samples.Count -lt 5) { throw "only $($samples.Count) samples captured -- did the fixture run? check the session stamp" }
$rawCount = $samples.Count
$span = $samples[-1].replay.positionSec - $samples[0].replay.positionSec

# Resample onto a fixed grid in REPLAY-POSITION space, not wall-clock. Polling is wall-clock, so at
# -Speed 4 only half as many samples land per second of audio; comparing those raw counts to a
# real-time baseline measures the poll rate, not the selector. Gridding makes every speed produce the
# same sample positions, so occupancy and hand-off counts are comparable across speeds.
$GridStep = 0.5
$grid = @()
$pos = $samples[0].replay.positionSec
$idx = 0
while ($pos -le $samples[-1].replay.positionSec) {
    while ($idx -lt $samples.Count - 1 -and $samples[$idx + 1].replay.positionSec -le $pos) { $idx++ }
    $grid += $samples[$idx]
    $pos += $GridStep
}
$samples = $grid
Write-Host "captured $rawCount samples over $([math]::Round($span,1))s of audio -> $($samples.Count) on a $($GridStep)s grid"

# --- reduce to aggregates -----------------------------------------------------------------------
function Median([double[]]$v) {
    if ($v.Count -eq 0) { return $null }
    $s = $v | Sort-Object
    return [math]::Round($s[[int]([math]::Floor($s.Count / 2))], 2)
}

$nCh = $samples[0].channels.Count
$nOut = $samples[0].outputs.Count

$channels = @()
for ($i = 0; $i -lt $nCh; $i++) {
    $env = @($samples | ForEach-Object { $_.channels[$i].envDb } | Where-Object { $_ -ne $null -and $_ -gt -119 })
    $cv = @($samples | ForEach-Object { $_.channels[$i].fluxCv } | Where-Object { $_ -ne $null -and $_ -gt 0 })
    $channels += [ordered]@{
        index     = $i
        label     = $samples[0].channels[$i].label
        medianEnv = Median $env
        medianCv  = Median $cv
    }
}

$outputs = @()
for ($o = 0; $o -lt $nOut; $o++) {
    $winners = @($samples | ForEach-Object { $_.outputs[$o].winner })
    $handoffs = 0
    for ($k = 1; $k -lt $winners.Count; $k++) { if ($winners[$k] -ne $winners[$k - 1]) { $handoffs++ } }
    $occ = [ordered]@{}
    $winners | Group-Object | Sort-Object Name | ForEach-Object {
        $occ["$($_.Name)"] = [math]::Round(100 * $_.Count / $winners.Count, 1)
    }
    $outputs += [ordered]@{
        index        = $o
        mode         = $samples[0].outputs[$o].mode
        preferNatural = $samples[0].outputs[$o].preferNatural
        handoffs     = $handoffs
        occupancy    = $occ
    }
}

$result = [ordered]@{
    fixture  = $Name
    stamp    = $samples[0].replay.stamp
    seek     = $Seek
    duration = $For
    samples  = $samples.Count
    channels = $channels
    outputs  = $outputs
}

# --- write or diff ------------------------------------------------------------------------------
if ($Update -or -not (Test-Path $baselinePath)) {
    $result | ConvertTo-Json -Depth 8 | Set-Content -Path $baselinePath -Encoding utf8
    Write-Host "baseline written: $baselinePath" -ForegroundColor Green
    exit 0
}

$base = Get-Content $baselinePath -Raw | ConvertFrom-Json
$issues = @()

for ($o = 0; $o -lt $nOut; $o++) {
    $b = $base.outputs[$o]; $n = $result.outputs[$o]
    if ($b.mode -ne $n.mode) { $issues += "output $o mode: $($b.mode) -> $($n.mode)" }
    $hTol = [math]::Max(2, $b.handoffs * 0.2)
    if ([math]::Abs($b.handoffs - $n.handoffs) -gt $hTol) {
        $issues += "output $o hand-offs: $($b.handoffs) -> $($n.handoffs) (tolerance +/-$([math]::Round($hTol,1)))"
    }
    $keys = @($b.occupancy.PSObject.Properties.Name) + @($n.occupancy.Keys) | Sort-Object -Unique
    foreach ($k in $keys) {
        $bv = if ($b.occupancy.PSObject.Properties.Name -contains $k) { [double]$b.occupancy.$k } else { 0 }
        $nv = if ($n.occupancy.Contains($k)) { [double]$n.occupancy[$k] } else { 0 }
        if ([math]::Abs($bv - $nv) -gt $Tolerance) {
            $issues += "output $o mic $k occupancy: $bv% -> $nv% (tolerance +/-$Tolerance pts)"
        }
    }
}

# medianEnv is recorded for context but only loosely checked: env is a fast-moving smoothed RMS and
# /state is polled at an arbitrary phase, so subsampling it costs a few dB run to run. That is
# measurement jitter, not engine drift -- tightening this produces false alarms, not earlier warnings.
# medianCv IS checked: flux-CV moves slowly (~1 s EMA) and proved stable across runs and speeds.
for ($i = 0; $i -lt $nCh; $i++) {
    $b = $base.channels[$i]; $n = $result.channels[$i]
    if ($null -ne $b.medianEnv -and $null -ne $n.medianEnv -and [math]::Abs($b.medianEnv - $n.medianEnv) -gt 4.0) {
        $issues += "in$i ($($n.label)) median env: $($b.medianEnv) -> $($n.medianEnv) dB"
    }
    if ($null -ne $b.medianCv -and $null -ne $n.medianCv -and [math]::Abs($b.medianCv - $n.medianCv) -gt 0.05) {
        $issues += "in$i ($($n.label)) median fluxCv: $($b.medianCv) -> $($n.medianCv)"
    }
}

Write-Host ""
if ($issues.Count -eq 0) {
    Write-Host "PASS -- '$Name' matches baseline" -ForegroundColor Green
    exit 0
}
Write-Host "DRIFT -- '$Name' differs from baseline:" -ForegroundColor Yellow
$issues | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
Write-Host ""
Write-Host "If the change is intended, re-run with -Update to accept it as the new baseline."
exit 1
