<#
.SYNOPSIS
  Audio endpoint + Bluetooth link diagnostic. Prototype for an in-app "Audio device diagnostics"
  tool (would move to C#/Core-Audio in AudioMixer; this is the spec + a standalone check).

.DESCRIPTION
  Answers the questions that come up when an Anker mic goes dead but "shows connected":
    - Which audio endpoints are ACTIVE (streaming-capable) vs ghosts (remembered, disconnected)?
    - For each, is it CAPTURE (mic) or RENDER (speaker)?
    - Are any devices connected via Bluetooth right now (vs just paired)?
    - Does a device have its output link up but NOT its input? (Soundsync dongles keep the speaker
      endpoint present while the wireless mic path is down -- the exact 2026-07-05 dead-mic case.)

  Direction is derived from the MMDEVAPI endpoint id, which encodes data flow and is name-independent:
    ...{0.0.0.00000000}...  = RENDER (speaker)
    ...{0.0.1.00000000}...  = CAPTURE (mic)
  Status OK = active/connected; anything else (Unknown) = a ghost endpoint Windows still remembers.
  Bluetooth "connected" is the real link state (property {83DA6326-...} 15), NOT the paired-but-idle
  "OK" driver status.

.PARAMETER Filter
  Substring to match on device name (default "Anker"). Use "" to show every audio endpoint.

.EXAMPLE
  powershell -File tools\audio-device-diag.ps1
  powershell -File tools\audio-device-diag.ps1 -Filter Soundsync
  powershell -File tools\audio-device-diag.ps1 -Filter ""      # all devices
#>
param(
    [string]$Filter = "Anker"
)

$BT_CONNECTED = '{83DA6326-97A6-4088-9453-A1923F573B29} 15'

function Get-Direction($instanceId) {
    if ($instanceId -like '*{0.0.1.*') { return 'CAP' }   # capture / microphone
    if ($instanceId -like '*{0.0.0.*') { return 'REN' }   # render / speaker
    return '?'
}

Write-Host ""
Write-Host "Audio device diagnostic  (filter: '$Filter')" -ForegroundColor Cyan
Write-Host ("=" * 60)

# --- Bluetooth link state -------------------------------------------------
Write-Host ""
Write-Host "Bluetooth links (Connected = live audio link, not just paired):" -ForegroundColor Cyan
$bt = Get-PnpDevice -Class Bluetooth -ErrorAction SilentlyContinue |
      Where-Object { $_.FriendlyName -and (!$Filter -or $_.FriendlyName -match $Filter) -and
                     $_.FriendlyName -notmatch 'Avrcp|Transport|Enumerator|Hands-Free|Headset' }
# Dedupe by radio address (BLUETOOTHDEVICE_<addr>), NOT by FriendlyName: identical units (e.g. four
# "Anker PowerConf S500") share a name but are distinct radios -- Sort -Unique on name HIDES them and
# under-reports how many are live on BT. A single S500 can also hold its dongle AND a BT link at once.
$seen = @{}
$units = foreach ($d in $bt) {
    $addr = ($d.InstanceId -split '_')[-1]
    if ($seen.ContainsKey($addr)) { continue }
    $seen[$addr] = $true
    $c = ($d | Get-PnpDeviceProperty -KeyName $BT_CONNECTED -ErrorAction SilentlyContinue).Data
    [pscustomobject]@{ Addr = $addr; Name = $d.FriendlyName; Connected = [bool]$c }
}
$btLive = @($units | Where-Object Connected).Count
foreach ($u in ($units | Sort-Object @{ e = { -not $_.Connected } }, Name, Addr)) {
    $mark = if ($u.Connected) { "[CONNECTED]" } else { "[  idle   ]" }
    $col  = if ($u.Connected) { "Green" } else { "DarkGray" }
    Write-Host ("  {0}  {1}  ({2})" -f $mark, $u.Name, $u.Addr) -ForegroundColor $col
}
$anyBt = $btLive -gt 0
if (-not $units) { Write-Host "  (none found)" -ForegroundColor DarkGray }
elseif (-not $anyBt) { Write-Host "  -> nothing connected via Bluetooth right now (paired-idle only)." -ForegroundColor DarkGray }

# --- Audio endpoints ------------------------------------------------------
Write-Host ""
Write-Host "Audio endpoints (active = streaming-capable now; ghost = remembered/disconnected):" -ForegroundColor Cyan
$eps = Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue |
       Where-Object { $_.FriendlyName -and (!$Filter -or $_.FriendlyName -match $Filter) }

$rows = foreach ($e in $eps) {
    [pscustomobject]@{
        Dir   = Get-Direction $e.InstanceId
        State = if ($e.Status -eq 'OK') { 'active' } else { 'ghost ' }
        Name  = $e.FriendlyName
    }
}
$rows = $rows | Sort-Object Dir, State, Name
foreach ($r in $rows) {
    $col = if ($r.State -eq 'active') { if ($r.Dir -eq 'CAP') { 'Green' } else { 'Gray' } } else { 'DarkGray' }
    Write-Host ("  {0}  {1}  {2}" -f $r.Dir, $r.State, $r.Name) -ForegroundColor $col
}
if (-not $rows) { Write-Host "  (none found)" -ForegroundColor DarkGray }

# --- Summary / health flags ----------------------------------------------
$actCap = @($rows | Where-Object { $_.Dir -eq 'CAP' -and $_.State -eq 'active' }).Count
$actRen = @($rows | Where-Object { $_.Dir -eq 'REN' -and $_.State -eq 'active' }).Count
Write-Host ""
Write-Host "Summary:" -ForegroundColor Cyan
Write-Host ("  active capture (mics)    : {0}" -f $actCap)
Write-Host ("  active render (speakers) : {0}" -f $actRen)
if ($actRen -gt 0 -and $actCap -eq 0) {
    Write-Host "  ! Output endpoints are up but NO mic is active -- classic Soundsync half-link" -ForegroundColor Yellow
    Write-Host "    (dongle enumerates + speaker works, wireless mic path down). Re-pair the dongle." -ForegroundColor Yellow
}
# Policy: the Ankers should always run over their 2.4GHz Soundsync dongles, never Bluetooth
# (BT drops to HSP/HFP quality and contends with the dongle link). Flag any live BT link.
if ($anyBt) {
    Write-Host ("  ! {0} Anker unit(s) connected via BLUETOOTH -- a single S500 can hold its dongle AND a" -f $btLive) -ForegroundColor Yellow
    Write-Host "    BT link at once (self-contending 2.4GHz radio, garbles the weakest dongle mic)." -ForegroundColor Yellow
    Write-Host "    'Forget' them in Windows Bluetooth settings so units run dongle-only." -ForegroundColor Yellow
}
Write-Host ""
