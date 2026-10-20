# Publish + package script for HAZEM Tunnel (Windows)
# Produces publish\win-x64\HAZEM.Tunnel.exe (single file, self-contained)
# Usage: powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
$ErrorActionPreference = "Stop"

Write-Host "=== HAZEM Tunnel publish (win-x64, self-contained) ===" -ForegroundColor Cyan
dotnet publish src/HAZEM.Tunnel.App -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o publish/win-x64
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }

$zip = "dist/HAZEM-Tunnel-1.1.0-win-x64.zip"
New-Item -ItemType Directory -Force -Path dist | Out-Null
Compress-Archive -Path "publish/win-x64/HAZEM.Tunnel.exe" -DestinationPath $zip -Force
Write-Host "Packaged $zip" -ForegroundColor Green
Write-Host "Note: the EXE is not code-signed; SmartScreen may warn on first run." -ForegroundColor Yellow
