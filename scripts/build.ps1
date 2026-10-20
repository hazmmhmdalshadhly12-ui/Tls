# Build script for HAZEM Tunnel (Windows)
# Usage: powershell -ExecutionPolicy Bypass -File scripts\build.ps1
$ErrorActionPreference = "Stop"

Write-Host "=== HAZEM Tunnel build ===" -ForegroundColor Cyan
dotnet build HAZEM.Tunnel.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
Write-Host "Build OK." -ForegroundColor Green
