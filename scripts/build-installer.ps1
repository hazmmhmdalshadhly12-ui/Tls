# Build the HAZEM Tunnel MSI installer (Windows)
# Requires: .NET SDK 10 (global.json) and the WiX toolset (installed automatically if missing).
# Usage: powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 [-Version 1.2.0]
param(
    [string]$Version = "1.2.0"
)
$ErrorActionPreference = "Stop"

Write-Host "=== HAZEM Tunnel installer build (v$Version) ===" -ForegroundColor Cyan

# 1. Publish the self-contained single-file EXE
pwsh -File scripts/publish.ps1 -Version $Version
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }

# 2. Ensure the WiX toolset is available
$wix = Get-Command wix -ErrorAction SilentlyContinue
if ($null -eq $wix) {
    Write-Host "Installing WiX Toolset v5 (dotnet global tool)..." -ForegroundColor Yellow
    dotnet tool install --global wix --version 5.0.2
    if ($LASTEXITCODE -ne 0) { throw "Failed to install the WiX toolset." }
}

# 3. Compile the MSI
$msi = "dist/HAZEM-Tunnel-$Version-setup.msi"
wix build -arch x64 `
    -d "PublishDir=publish/win-x64/" `
    -d "AppIcon=src/HAZEM.Tunnel.App/Assets/app.ico" `
    installer/msi/HAZEMTunnel.wxs `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed with exit code $LASTEXITCODE" }

# 4. Print the SHA-256 for release verification
$hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLower()
Write-Host ""
Write-Host "Built $msi" -ForegroundColor Green
Write-Host "SHA-256: $hash"
Write-Host "Note: the MSI is not code-signed; Windows may show the publisher as unknown."
