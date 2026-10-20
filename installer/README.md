# Installers

Two installer options ship with the project — both behave like a normal Windows program:
install into **Program Files**, add a **Start Menu** shortcut, a **desktop** shortcut, and a proper
**uninstall** entry under *Settings → Apps → Installed apps*.

| Option | File | Built by | Notes |
|---|---|---|---|
| **MSI (recommended)** | `HAZEM-Tunnel-<version>-setup.msi` | `scripts\build-installer.ps1` or the release workflow (WiX 5) | Machine-wide install (per-machine), major-upgrade aware, icon in *Apps & features* |
| Inno Setup EXE | `HAZEM-Tunnel-<version>-setup.exe` | `ISCC.exe installer\hazem-tunnel.iss` | Per-user install (no admin), wizard-style UI |

## MSI (WiX 5)

- Authoring: [`msi/HAZEMTunnel.wxs`](msi/HAZEMTunnel.wxs)
- Build on Windows:
  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
  # → dist\HAZEM-Tunnel-1.2.0-setup.msi (+ SHA-256 printed)
  ```
- Build in CI: the release workflow (`.github/workflows/release.yml`) compiles the MSI on
  `windows-latest` for every `v*` tag and attaches it to the GitHub Release.
  (WiX's `Directory/@Name` canonicalization misbehaves on Linux — see
  `BundleValidator.GetCanonicalRelativePath` hard-coding `C:\` — so the MSI must be
  compiled on Windows, which CI provides.)
- What it does:
  - Installs `HAZEM.Tunnel.exe` into `%ProgramFiles%\HAZEM Tunnel` (64-bit).
  - Start Menu + desktop shortcuts (with the app icon).
  - Registers in *Apps & features* with icon and project links.
  - **Major upgrade aware**: installing 1.2.1 over 1.2.0 replaces it; a downgrade is refused.
  - **Clean uninstall**: removes the EXE and shortcuts; user data
    (`%LocalAppData%\HAZEMTunnel`) is deliberately preserved.

## Inno Setup (EXE)

1. Install [Inno Setup 6](https://jrsoftware.org/isinfo.php).
2. Produce the EXE first:
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
   ```
3. Compile: `ISCC.exe installer\hazem-tunnel.iss`
4. Output: `dist\HAZEM-Tunnel-1.2.0-setup.exe`

## Notes

- Neither artifact is code-signed yet — SmartScreen shows the usual *"Windows protected
  your PC"* on first run (More info → Run anyway). Verify SHA-256 checksums published
  with each release.
- Most users can simply use the portable zip — no installation needed. The MSI exists
  for the "install like every other program" experience: shortcuts, upgrades, uninstall.
