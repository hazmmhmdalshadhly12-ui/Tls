# Build the HAZEM Tunnel MSI installer (Windows)
# Requires: .NET SDK 10 (global.json), Python 3 (preinstalled on CI) and the WiX toolset.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 [-Version 1.2.1]
param(
    [string]$Version = "1.2.1"
)
$ErrorActionPreference = "Stop"

Write-Host "=== HAZEM Tunnel installer build (v$Version) ===" -ForegroundColor Cyan

# 1. Publish both layouts (folder for the MSI + portable single-file for the zip)
pwsh -File scripts/publish.ps1 -Version $Version
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }

# 2. Ensure the WiX toolset + Util extension are available
$wix = Get-Command wix -ErrorAction SilentlyContinue
if ($null -eq $wix) {
    Write-Host "Installing WiX Toolset v5 (dotnet global tool)..." -ForegroundColor Yellow
    dotnet tool install --global wix --version 5.0.2
    if ($LASTEXITCODE -ne 0) { throw "Failed to install the WiX toolset." }
}
# Idempotent: re-adding an already-cached extension succeeds.
wix extension add -g WixToolset.Util.wixext/5.0.2
if ($LASTEXITCODE -ne 0) { throw "Failed to install the WiX Util extension." }

# 3. Generate the harvested .wxs (one component per published file)
$python = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $python) { $python = Get-Command py -ErrorAction Stop }
& $python.Source installer/msi/gen_wxs.py `
    --publish-dir publish/win-x64 `
    --template installer/msi/HAZEMTunnel.wxs.template `
    --out obj/msi/HAZEMTunnel.wxs
if ($LASTEXITCODE -ne 0) { throw "gen_wxs.py failed with exit code $LASTEXITCODE" }

# 4. Compile the MSI
$msi = "dist/HAZEM-Tunnel-$Version-setup.msi"
wix build -arch x64 `
    -ext WixToolset.Util.wixext `
    -d "PublishDir=publish/win-x64/" `
    -d "AppIcon=src/HAZEM.Tunnel.App/Assets/app.ico" `
    obj/msi/HAZEMTunnel.wxs `
    -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed with exit code $LASTEXITCODE" }

# 5. Print the SHA-256 for release verification
$hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLower()
Write-Host ""
Write-Host "Built $msi" -ForegroundColor Green
Write-Host "SHA-256: $hash"
Write-Host "Note: the MSI is not code-signed; Windows may show the publisher as unknown."
