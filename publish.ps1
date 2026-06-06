#requires -version 5
<#
.SYNOPSIS
  Builds a self-contained, single-file AudioMixer.exe for Windows x64.
.DESCRIPTION
  The output exe bundles the .NET 8 runtime, so target machines need NOTHING
  pre-installed. Copy bin\publish\AudioMixer.exe to any Windows 10/11 x64 box
  and double-click. (Targets still install VB-CABLE manually for Zoom routing.)
.EXAMPLE
  .\publish.ps1
#>
$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$proj = Join-Path $root 'AudioMixer\AudioMixer.csproj'
$out  = Join-Path $root 'bin\publish'

# Resolve dotnet: PATH first, then the user-local SDK (this box has no machine-wide install).
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw "dotnet not found. Install the .NET 8 SDK or fix PATH." }

Write-Host 'Publishing AudioMixer (self-contained, single-file, win-x64)...' -ForegroundColor Cyan

& $dotnet publish $proj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=none `
  -p:DebugSymbols=false `
  -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

$exe = Join-Path $out 'AudioMixer.exe'
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "`nDone -> $exe ($size MB)" -ForegroundColor Green
Write-Host 'Copy that single file to other computers and run it.' -ForegroundColor Green
