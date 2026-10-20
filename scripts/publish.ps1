# Publish + package script for HAZEM Tunnel (Windows)
# Produces TWO layouts from the same source tree:
#   publish\win-x64\           — full self-contained program folder  → payload of the MSI installer
#   publish\win-x64-portable\  — single-file EXE                     → portable zip
# Usage: powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 [-Version 1.2.1]
param(
    [string]$Version = "1.2.1"
)
$ErrorActionPreference = "Stop"

Write-Host "=== HAZEM Tunnel publish (win-x64, self-contained) ===" -ForegroundColor Cyan

# ── 1. Full program folder (installed by the MSI into %ProgramFiles%\HAZEM Tunnel) ──
# Runs directly from the install folder at runtime: no TEMP self-extraction on every
# launch, faster startup, and no antivirus friction with per-run extraction.
dotnet publish src/HAZEM.Tunnel.App -c Release -r win-x64 --self-contained true `
    -p:DebugType=None -p:DebugSymbols=false -o publish/win-x64
if ($LASTEXITCODE -ne 0) { throw "Folder publish failed with exit code $LASTEXITCODE" }

# Sanity: a self-contained WPF app must carry the .NET runtime next to the exe.
foreach ($must in @("publish/win-x64/HAZEM.Tunnel.exe", "publish/win-x64/hostfxr.dll", "publish/win-x64/HAZEM.Tunnel.dll")) {
    if (-not (Test-Path $must)) { throw "Self-contained publish incomplete — missing $must" }
}

# ── 2. Single-file portable EXE (one file you can run from anywhere) ──
dotnet publish src/HAZEM.Tunnel.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -o publish/win-x64-portable
if ($LASTEXITCODE -ne 0) { throw "Portable publish failed with exit code $LASTEXITCODE" }

# ── 3. Package the portable EXE ──
$zip = "dist/HAZEM-Tunnel-$Version-win-x64.zip"
New-Item -ItemType Directory -Force -Path dist | Out-Null
Compress-Archive -Path "publish/win-x64-portable/HAZEM.Tunnel.exe" -DestinationPath $zip -Force

Write-Host "Packaged $zip" -ForegroundColor Green
Write-Host "MSI payload ready in publish/win-x64 ($(Get-ChildItem publish/win-x64 -File).Count files)" -ForegroundColor Green
Write-Host "Note: the binaries are not code-signed; SmartScreen may warn on first run." -ForegroundColor Yellow
