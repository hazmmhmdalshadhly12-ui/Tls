# Test script for HAZEM Tunnel (Windows)
# Usage: powershell -ExecutionPolicy Bypass -File scripts\test.ps1
$ErrorActionPreference = "Stop"

Write-Host "=== HAZEM Tunnel tests ===" -ForegroundColor Cyan
dotnet test HAZEM.Tunnel.slnx -c Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }
Write-Host "All tests passed." -ForegroundColor Green
