#requires -version 5
<#
.SYNOPSIS
  Builds a single-file AudioMixer.exe for Windows x64.
.DESCRIPTION
  Default (self-contained): the exe bundles the .NET 8 runtime, so target
  machines need NOTHING pre-installed. Output: bin\publish\AudioMixer.exe (~68 MB).

  -Slim (framework-dependent): a much smaller exe that requires the
  .NET 8 Desktop Runtime to be installed on the target machine. Output:
  bin\publish-slim\AudioMixer.exe (~few MB). If the runtime is missing, Windows
  prompts the user to download it on first launch.

  (Either way, targets still install VB-CABLE manually for Zoom routing.)
.EXAMPLE
  .\publish.ps1          # self-contained, standalone exe
.EXAMPLE
  .\publish.ps1 -Slim    # framework-dependent exe (needs .NET 8 Desktop Runtime)
#>
param([switch]$Slim)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$proj = Join-Path $root 'AudioMixer\AudioMixer.csproj'
$out  = Join-Path $root ($Slim ? 'bin\publish-slim' : 'bin\publish')

# Resolve dotnet: PATH first, then the user-local SDK (this box has no machine-wide install).
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw "dotnet not found. Install the .NET 8 SDK or fix PATH." }

$kind = $Slim ? 'framework-dependent (needs .NET 8 Desktop Runtime)' : 'self-contained (standalone)'
Write-Host "Publishing AudioMixer - $kind, single-file, win-x64..." -ForegroundColor Cyan

# Compression + native-lib self-extract are only valid for self-contained builds
# (NETSDK1176). The framework-dependent build gets the native WPF DLLs from the runtime.
$args = @(
  'publish', $proj,
  '-c', 'Release',
  '-r', 'win-x64',
  "--self-contained", ($Slim ? 'false' : 'true'),
  '-p:PublishSingleFile=true',
  '-p:DebugType=none',
  '-p:DebugSymbols=false'
)
if (-not $Slim) {
  $args += '-p:EnableCompressionInSingleFile=true'
  $args += '-p:IncludeNativeLibrariesForSelfExtract=true'
}
& $dotnet @args -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

$exe = Join-Path $out 'AudioMixer.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "`nDone -> $exe ($size MB)" -ForegroundColor Green
if ($Slim) {
  Write-Host 'Target needs the .NET 8 Desktop Runtime: https://dotnet.microsoft.com/download/dotnet/8.0' -ForegroundColor Yellow
} else {
  Write-Host 'Copy that single file to other computers and run it (no prerequisites).' -ForegroundColor Green
}
